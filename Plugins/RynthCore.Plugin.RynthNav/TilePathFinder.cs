using System;
using System.Collections.Generic;
using System.IO;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using RynthCore.Plugin.RynthNav;

namespace RynthNav.Routing;

/// <summary>
/// Whole walking legs on the navmesh, worked out ahead of time for a planned trip (the path
/// markers and "save as nav route"), the way the walker would go: the route graph's border
/// crossings from the start's region to the goal's (NavGraph A*), then the detailed Detour path
/// between each pair of crossings, kept out of the Atlas's portals (AvoidFilter, PortalAvoid.Bend)
/// except the leg's own. Its own navmesh, filled from the tile files a few tiles at a time, so it
/// runs on a worker and never touches the walker's (tick-thread) navmesh. One instance per worker.
/// Without a route graph only short legs (a few landblocks) are planned; longer ones get none.
///
/// The route maker (LoopMaker, RynthNavPlugin.RouteMaker.cs) sets the stricter options: no way
/// through a portal's circle at all (<see cref="StrictPortals"/>), no straight line over a navmesh gap
/// (<see cref="NoGaps"/>), corners pushed off walls (<see cref="WallClearance"/>, RynthNav's own walks
/// do this as they go), building floors left out where there's another way (<see cref="AvoidArea"/>);
/// and it asks this navmesh for ground (<see cref="Snap"/>, <see cref="WallDistance"/>, <see cref="Leg"/>).
/// With none of them set it plans exactly as it always has.
/// </summary>
internal sealed class TilePathFinder
{
    private const int MaxTiles = 64;
    private const int MaxDirectTiles = 16;        // without a graph: a leg spanning at most this many tiles
    private const float ProbeReach = 40f;
    private const double OwnPortalUnits = 15.0;   // the leg's own portal (its goal) isn't avoided
    private const double AvoidNearUnits = 300.0;

    private readonly string _dir;
    private readonly NavGraph? _graph;
    private readonly IReadOnlyList<PortalAvoid.Spot> _portals, _live;
    private readonly DtNavMesh _nav;
    private DtNavMeshQuery _query;
    private readonly HashSet<uint> _loaded = new(), _bad = new();
    private readonly PortalAvoid _avoid = new();
    private readonly AvoidFilter _filter;
    private readonly AreaFilter _area;
    private readonly DtQueryDefaultFilter _plain = new();
    private string _pieceNote = "";

    /// <summary>Tile files read so far (for the log).</summary>
    public int TilesRead { get; private set; }

    /// <summary>Never through a portal's circle: no plan through one when there's no way round, and a bent path that still crosses one is refused.</summary>
    public bool StrictPortals { get; set; }
    /// <summary>A stretch the navmesh can't plan fails the walk, instead of becoming a straight line.</summary>
    public bool NoGaps { get; set; }
    /// <summary>Each corner pushed this far out from the wall it turns round, where the ground is walkable (0 = not).</summary>
    public double WallClearance { get; set; }
    /// <summary>Ground kept off where there is another way (east, north: true = keep off); null = none.</summary>
    public Func<double, double, bool>? AvoidArea { get; set; }

    public TilePathFinder(string navDataDir, NavGraph? graph, IReadOnlyList<PortalAvoid.Spot>? portals,
        IReadOnlyList<PortalAvoid.Spot>? livePortals = null)
    {
        _dir = navDataDir;
        _graph = graph;
        _portals = portals ?? Array.Empty<PortalAvoid.Spot>();
        _live = livePortals ?? Array.Empty<PortalAvoid.Spot>();
        _nav = new DtNavMesh();
        var p = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = MaxTiles, maxPolys = 1 << 16 };
        _nav.Init(p, 6);
        _query = new DtNavMeshQuery(_nav);
        _filter = new AvoidFilter(_avoid);
        _area = new AreaFilter(_filter);
    }

    /// <summary>
    /// The walk from <paramref name="from"/> to <paramref name="to"/>: corners, both ends included.
    /// <paramref name="ownPortal"/>: the walk ends by stepping into a portal there (not avoided).
    /// Null when the navmesh can't give one (no tiles, no way over land); <paramref name="note"/> says why.
    /// </summary>
    public List<WorldPt>? Walk(WorldPt from, WorldPt to, bool ownPortal, out string note)
    {
        note = "";
        var anchors = new List<WorldPt> { from };
        if (_graph != null)
        {
            NavCoarse.Probe? s = GroundProbe(from), g = GroundProbe(to);
            if (s == null || g == null) { note = $"no navmesh at its {(s == null ? "start" : "end")}"; return null; }
            List<int>? route = _graph.FindRoute(s.Region, s.Pt.X, s.Pt.Z, g.Region, g.Pt.X, g.Pt.Z, out _, out _);
            if (route == null) { note = "no way over land on the navmesh"; return null; }
            // Plan from and to the ground found there (the trip's own ends are added back by the caller).
            anchors[0] = new WorldPt(s.Pt.X, s.Pt.Z, s.Pt.Y);
            from = anchors[0];
            to = new WorldPt(g.Pt.X, g.Pt.Z, g.Pt.Y);
            foreach (int p in route)
            {
                var c = _graph.Portal(p);
                anchors.Add(new WorldPt(c.Ew, c.Ns, c.Up));
            }
        }
        else if (TileSpan(from, to) > MaxDirectTiles)
        {
            note = "too far to plan without the route graph (navgraph.bin)";
            return null;
        }
        anchors.Add(to);

        var path = new List<WorldPt> { from };
        int gaps = 0;
        for (int k = 0; k + 1 < anchors.Count; k++)
        {
            bool last = k + 2 == anchors.Count;
            List<WorldPt>? piece = Piece(anchors[k], anchors[k + 1], last && ownPortal ? anchors[k + 1] : null);
            if (piece == null && NoGaps)
            {
                note = $"no navmesh path from {NavCoords.Fmt(anchors[k].Ns, anchors[k].Ew)} to {NavCoords.Fmt(anchors[k + 1].Ns, anchors[k + 1].Ew)}"
                     + (_pieceNote.Length > 0 ? ": " + _pieceNote : "");
                return null;
            }
            if (piece == null) { gaps++; piece = new List<WorldPt> { anchors[k], anchors[k + 1] }; }
            for (int i = 1; i < piece.Count; i++) path.Add(piece[i]);
        }
        if (gaps > 0) note = $"{gaps} gap(s) in the navmesh, straight lines there";
        return path;
    }

    /// <summary>
    /// Walkable ground at <paramref name="p"/> for the route search. A point with no height (a
    /// landing, a portal, a town's centre) can find a roof or a ledge first: a region of a tiny
    /// landmass that leads nowhere (the real map: Lytelthorpe's centre is under a roof). Then the
    /// ground a few yards round it (rings out to 36 yd) that's part of a real landmass is taken.
    /// </summary>
    private NavCoarse.Probe? GroundProbe(WorldPt p)
    {
        NavGraph g = _graph!;
        NavCoarse.Probe? first = NavCoarse.ProbeFile(g, _dir, p.X, p.H, p.Y, ProbeReach);
        if (first != null && !Tiny(first.Region)) return first;
        for (double r = 6; r <= 36; r += 6)
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                NavCoarse.Probe? q = NavCoarse.ProbeFile(g, _dir, p.X + Math.Cos(a) * r, p.H, p.Y + Math.Sin(a) * r, 4f);
                if (q != null && !Tiny(q.Region)) return q;
            }
        return first;
    }

    /// <summary>A region of a landmass this small (a roof, a ledge); false when the graph has no landmasses worked out.</summary>
    private bool Tiny(int region)
    {
        int c = _graph!.Component(region);
        return c >= 0 && _graph.ComponentSize(c) < 8;
    }

    private static int TileSpan(WorldPt a, WorldPt b)
    {
        int ax = (int)Math.Floor(a.X / 192.0), ay = (int)Math.Floor(a.Y / 192.0), bx = (int)Math.Floor(b.X / 192.0), by = (int)Math.Floor(b.Y / 192.0);
        return (Math.Abs(ax - bx) + 1) * (Math.Abs(ay - by) + 1);
    }

    /// <summary>The Detour path a→b (both ends' tiles and those between loaded); null when there's none (_pieceNote: why).</summary>
    private List<WorldPt>? Piece(WorldPt a, WorldPt b, WorldPt? own)
    {
        _pieceNote = "";
        if (!LoadBox(a, b)) { _pieceNote = "no navmesh tiles there"; return null; }
        var half = new RcVec3f(8, 600, 8);
        var pa = new RcVec3f((float)a.X, double.IsNaN(a.H) ? 0f : (float)a.H, (float)a.Y);
        var pb = new RcVec3f((float)b.X, double.IsNaN(b.H) ? 0f : (float)b.H, (float)b.Y);
        _query.FindNearestPoly(pa, half, _plain, out long sRef, out RcVec3f sPt, out _);
        _query.FindNearestPoly(pb, half, _plain, out long gRef, out RcVec3f gPt, out _);
        if (sRef == 0 || gRef == 0) { _pieceNote = $"no walkable ground at its {(sRef == 0 ? "start" : "end")}"; return null; }

        // Portals near this piece, except the leg's own.
        var near = new List<PortalAvoid.Spot>();
        var nearLive = new List<PortalAvoid.Spot>();
        double minX = Math.Min(a.X, b.X) - AvoidNearUnits, maxX = Math.Max(a.X, b.X) + AvoidNearUnits;
        double minY = Math.Min(a.Y, b.Y) - AvoidNearUnits, maxY = Math.Max(a.Y, b.Y) + AvoidNearUnits;
        foreach (PortalAvoid.Spot sp in _portals)
            if (sp.X >= minX && sp.X <= maxX && sp.Y >= minY && sp.Y <= maxY) near.Add(sp);
        foreach (PortalAvoid.Spot sp in _live)
            if (sp.X >= minX && sp.X <= maxX && sp.Y >= minY && sp.Y <= maxY) nearLive.Add(sp);
        if (own is WorldPt ownAt) _avoid.Set(nearLive, near, ownAt.X, ownAt.Y, OwnPortalUnits);
        else _avoid.Set(nearLive, near);
        _filter.AllowA = sRef; _filter.AllowB = gRef;

        Span<long> polys = new long[1024];
        int pc = 0;
        if (AvoidArea != null)
        {
            // Off the floors kept off (buildings) when there's a way; else as without.
            _area.Inside = AvoidArea; _area.AllowA = sRef; _area.AllowB = gRef;
            _query.FindPath(sRef, gRef, sPt, gPt, _area, polys, out pc, 1024);
            if (pc == 0 || polys[pc - 1] != gRef) pc = 0;
        }
        if (pc == 0) _query.FindPath(sRef, gRef, sPt, gPt, _filter, polys, out pc, 1024);
        if ((pc == 0 || polys[pc - 1] != gRef) && _avoid.Count > 0)
        {
            _query.FindPath(sRef, gRef, sPt, gPt, _plain, polys, out pc, 1024);
            // Strict: a way that only goes through a portal's circle is no way (and that is why).
            if (StrictPortals && pc > 0 && polys[pc - 1] == gRef) { _pieceNote = "no way round " + PortalsNamed(_avoid.Spots); return null; }
        }
        if (pc == 0 || polys[pc - 1] != gRef) { _pieceNote = "the navmesh doesn't join them (water, a cliff or a wall between)"; return null; }
        Span<DtStraightPath> sp2 = new DtStraightPath[256];
        _query.FindStraightPath(sPt, gPt, polys[..pc], pc, sp2, out int spc, 256, 0);
        if (spc < 2) { _pieceNote = "no straight path along the navmesh"; return null; }

        var corners = new List<(double X, double Y)>(spc);
        List<double>? heights = new List<double>(spc);
        for (int i = 0; i < spc; i++) { corners.Add((sp2[i].pos.X, sp2[i].pos.Z)); heights.Add(sp2[i].pos.Y); }
        if (_avoid.Count > 0)
        {
            var q = _query;
            bool OnMesh(double x, double y)
            {
                q.FindNearestPoly(new RcVec3f((float)x, pa.Y, (float)y), new RcVec3f(0.5f, 600, 0.5f), _plain, out long r, out RcVec3f np, out _);
                return r != 0 && Math.Abs(np.X - x) < 0.05 && Math.Abs(np.Z - y) < 0.05;
            }
            bool LegOnMesh(double ax, double ay, double bx, double by)
            {
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int n = Math.Max(1, (int)Math.Ceiling(len));
                for (int k = 1; k < n; k++)
                    if (!OnMesh(ax + (bx - ax) * k / n, ay + (by - ay) * k / n)) return false;
                return true;
            }
            int before = corners.Count;
            corners = _avoid.Bend(corners, OnMesh, 12, LegOnMesh);
            if (corners.Count != before) heights = null;   // bend corners: heights filled from the neighbours
        }
        if (WallClearance > 0 && corners.Count > 2)
        {
            // As RynthNav's own walks (Replan): each corner pushed out along the turn's outside, onto ground.
            var pushed = new List<(double X, double Y)>(corners);
            PushFromWalls(pushed, heights, pa.Y);
            if (!StrictPortals || Crosses(pushed) < 0) corners = pushed;   // a push into a portal's circle isn't taken
        }
        int hit = StrictPortals ? Crosses(corners) : -1;
        if (hit >= 0)
        {
            string name = _avoid.Spots[hit].Name;
            _pieceNote = $"it would cross {(name.Length > 0 ? "the portal " + name : "a portal")}";
            return null;
        }
        var o = new List<WorldPt>(corners.Count) { a with { H = heights != null ? heights[0] : (double.IsNaN(a.H) ? sPt.Y : a.H) } };
        for (int i = 1; i < corners.Count; i++)
            o.Add(new WorldPt(corners[i].X, corners[i].Y, heights != null ? heights[i] : double.NaN));
        if (heights == null) o[^1] = o[^1] with { H = gPt.Y };
        return o;
    }

    /// <summary>"the portal X" / "the portals X and Y" / "3 portals" for a refusal.</summary>
    private static string PortalsNamed(IReadOnlyList<PortalAvoid.Spot> spots)
    {
        var names = new List<string>();
        foreach (PortalAvoid.Spot s in spots) if (s.Name.Length > 0 && !names.Contains(s.Name)) names.Add(s.Name);
        if (names.Count == 0) return spots.Count == 1 ? "a portal" : $"{spots.Count} portals";
        if (names.Count > 2) return $"{spots.Count} portals ({names[0]}, {names[1]}, ...)";
        return names.Count == 1 ? "the portal " + names[0] : $"the portals {names[0]} and {names[1]}";
    }

    /// <summary>
    /// The first kept-out circle a leg of <paramref name="pts"/> enters (or its end lies in), or -1. A circle
    /// the start is already in doesn't count: that's where the walk begins (the walker steers out of it).
    /// </summary>
    private int Crosses(IReadOnlyList<(double X, double Y)> pts)
    {
        IReadOnlyList<PortalAvoid.Spot> spots = _avoid.Spots;
        if (pts.Count == 0) return -1;
        for (int k = 0; k < spots.Count; k++)
        {
            PortalAvoid.Spot s = spots[k];
            if (Math.Sqrt((pts[0].X - s.X) * (pts[0].X - s.X) + (pts[0].Y - s.Y) * (pts[0].Y - s.Y)) < s.R) continue;
            for (int i = 1; i < pts.Count; i++)
                if (PortalAvoid.SegmentDistance(s.X, s.Y, pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y) < s.R - 0.05) return k;
        }
        return -1;
    }

    /// <summary>
    /// Walls: each corner the path turns round sits on the mesh's edge; push it <see cref="WallClearance"/>
    /// out along the turn's outside bisector where the ground is walkable (inside a polygon, not merely
    /// near one), as the walker's Replan does. The ends never move.
    /// </summary>
    private void PushFromWalls(List<(double X, double Y)> wps, List<double>? heights, float startH)
    {
        for (int i = 1; i < wps.Count - 1; i++)
        {
            var (px, py) = wps[i]; var (ax, ay) = wps[i - 1]; var (bx, by) = wps[i + 1];
            double ux = ax - px, uy = ay - py, ul = Math.Sqrt(ux * ux + uy * uy);
            double vx = bx - px, vy = by - py, vl = Math.Sqrt(vx * vx + vy * vy);
            if (ul < 0.5 || vl < 0.5) continue;
            double ox = -(ux / ul + vx / vl), oy = -(uy / ul + vy / vl), ol = Math.Sqrt(ox * ox + oy * oy);
            if (ol < 0.05) continue;                   // straight on: no corner to clear
            double nx = px + ox / ol * WallClearance, ny = py + oy / ol * WallClearance;
            float h = heights != null && i < heights.Count ? (float)heights[i] : startH;
            _query.FindNearestPoly(new RcVec3f((float)nx, h, (float)ny), new RcVec3f(0.3f, 24, 0.3f), _plain, out long rr, out RcVec3f rp, out _);
            if (rr != 0 && Math.Abs(rp.X - nx) < 0.05 && Math.Abs(rp.Z - ny) < 0.05) wps[i] = (nx, ny);
        }
    }

    // ── Ground for the route maker (LoopMaker) ───────────────────────────

    /// <summary>Loads the tiles under the box (world units); false when it's more than this navmesh holds at once, or there are none.</summary>
    public bool LoadArea(double minX, double minY, double maxX, double maxY) =>
        LoadBox(new WorldPt(minX, minY, double.NaN), new WorldPt(maxX, maxY, double.NaN));

    /// <summary>
    /// Walkable ground within <paramref name="reach"/> yards (across) of <paramref name="p"/>, the nearest
    /// to its height (NaN: to height 0, so the lowest floor); its tiles must be loaded. False when none.
    /// </summary>
    public bool Snap(WorldPt p, double reach, out WorldPt ground, out long poly)
    {
        ground = p;
        var at = new RcVec3f((float)p.X, double.IsNaN(p.H) ? 0f : (float)p.H, (float)p.Y);
        _query.FindNearestPoly(at, new RcVec3f((float)reach, 600, (float)reach), _plain, out poly, out RcVec3f pt, out _);
        if (poly == 0) return false;
        ground = new WorldPt(pt.X, pt.Z, pt.Y);
        if (ground.FlatDistance(p) > reach) { poly = 0; return false; }   // the search box's corner: farther than asked
        return true;
    }

    /// <summary>How far ground found by <see cref="Snap"/> is from the navmesh's edge (a wall, a drop, water), at most <paramref name="max"/>.</summary>
    public double WallDistance(WorldPt ground, long poly, double max)
    {
        DtStatus st = _query.FindDistanceToWall(poly, new RcVec3f((float)ground.X, (float)ground.H, (float)ground.Y), (float)max, _plain, out float d, out _, out _);
        return st.Failed() ? 0 : Math.Min(d, max);
    }

    /// <summary>
    /// A leg between two points on ground already loaded (no route graph: a loop's legs are short), with
    /// the options set; null when there is none, and <paramref name="note"/> says why.
    /// </summary>
    public List<WorldPt>? Leg(WorldPt from, WorldPt to, out string note)
    {
        List<WorldPt>? piece = Piece(from, to, null);
        note = piece == null ? _pieceNote : "";
        return piece;
    }

    /// <summary>Loads the tiles of the box a..b (evicting others when full); false when it's too big or none loads.</summary>
    private bool LoadBox(WorldPt a, WorldPt b)
    {
        int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / 192.0), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / 192.0);
        int y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / 192.0), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / 192.0);
        // A crossing on a tile edge belongs to both tiles: take one more on each side then.
        if (a.X - x0 * 192.0 < 1 || b.X - x0 * 192.0 < 1) x0--;
        if ((x1 + 1) * 192.0 - Math.Max(a.X, b.X) < 1) x1++;
        if (a.Y - y0 * 192.0 < 1 || b.Y - y0 * 192.0 < 1) y0--;
        if ((y1 + 1) * 192.0 - Math.Max(a.Y, b.Y) < 1) y1++;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(255, x1); y1 = Math.Min(255, y1);
        int want = (x1 - x0 + 1) * (y1 - y0 + 1);
        if (want > MaxTiles / 2) return false;
        var needed = new HashSet<uint>();
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++) needed.Add((uint)((x << 8) | y));
        bool changed = false;
        if (_loaded.Count + want > MaxTiles - 1)
        {
            foreach (uint lb in new List<uint>(_loaded))
            {
                if (needed.Contains(lb)) continue;
                long r = _nav.GetTileRefAt((int)(lb >> 8), (int)(lb & 0xFF), 0);
                if (r != 0) _nav.RemoveTile(r);
                _loaded.Remove(lb);
                changed = true;
            }
        }
        bool any = false;
        foreach (uint lb in needed)
        {
            if (_loaded.Contains(lb)) { any = true; continue; }
            if (_bad.Contains(lb)) continue;
            string path = Path.Combine(_dir, $"nav_{lb:X4}.tile");
            if (!File.Exists(path)) { _bad.Add(lb); continue; }
            try
            {
                DtMeshData md;
                using (var fr = File.OpenRead(path)) using (var br = new BinaryReader(fr)) md = new DtMeshDataReader().Read(br, 6);
                TilesRead++;
                if (_nav.AddTile(md, 0, 0, out _).Failed()) { _bad.Add(lb); continue; }
                _loaded.Add(lb);
                changed = any = true;
            }
            catch { _bad.Add(lb); }
        }
        if (changed) _query = new DtNavMeshQuery(_nav);
        return any;
    }
}
