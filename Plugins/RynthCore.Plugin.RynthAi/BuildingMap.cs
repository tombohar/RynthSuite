// BuildingMap: the buildings of the outdoor landblocks round the character, for the outdoor hunt.
//
// RynthNav's landscape map ends at a building's walls (2026-10-25: the hunt left every monster in a
// house alone). A building's rooms are EnvCells of its landblock (0xXXYY0100 up), joined by portal
// records like a dungeon's; a portal record whose other cell is 0xFFFF opens to the outside: a door
// (or a window). Here, per landblock, from cell.dat:
//   - the cell graph and the room geometry (DungeonPathfinder.BuildGraph, DungeonGeometry from
//     DungeonLOS's polygons), the same the Dungeon Patrol walks on;
//   - its buildings: the groups of cells joined to each other (a building with two parts that
//     only the outside joins is two);
//   - each building's doors: its 0xFFFF portal polygons wide and tall enough to walk through with
//     their foot at the floor (not a window), with a point a step inside and one a step outside;
//   - walks inside a building between two spots (the cell path, laid through doorways and across
//     rooms as the patrol lays them), kept out of the circles of portal objects standing inside;
//   - what its cells place (tables, chairs, beds, cabinets: GeometryLoader's statics), as the dungeon
//     map's placed objects, less the stairs (see "Placed objects" below).
// Cached per landblock (the last MaxBlocks). Tick thread.

using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.Raycasting;
using Vec = RynthCore.Plugin.RynthAi.OutdoorHunt.Vec;
using PathResult = RynthCore.Plugin.RynthAi.OutdoorHunt.PathResult;
using PathStatus = RynthCore.Plugin.RynthAi.OutdoorHunt.PathStatus;

namespace RynthCore.Plugin.RynthAi;

internal sealed class BuildingMap : OutdoorHunt.IBuildings
{
    /// <summary>A door's outer point: this far out of it along its facing (RynthNav's map keeps its
    /// agent radius from the walls; its way to the door ends on the map's edge in front of it).</summary>
    internal const double OutsideMeters = 1.5;
    /// <summary>A door's inner point: this far in, shortened (in 0.25 m steps) to stay on the cell's floor.</summary>
    internal const double InnerLongest = 1.5, InnerShortest = 0.5;
    /// <summary>An opening to the outside narrower or lower than this is not a door (a window, a vent).</summary>
    internal const double MinDoorWidth = 0.8, MinDoorHeight = 1.5;
    /// <summary>A door's foot at most this far above or below the floor just inside it (else a window).</summary>
    internal const double DoorSillMax = 0.75;
    /// <summary>An opening under this tall can be a window with a deep sill (see TryDoor); no door this tall or taller is
    /// judged one.</summary>
    internal const double WindowTallMax = 2.4;
    /// <summary>The highest step a body walks up (AC's step-up height).</summary>
    internal const double StepUpMeters = 0.6;
    /// <summary>The floor a body stands on inside, for the window test: this far in and more (past a sill's depth).</summary>
    internal const double WindowRoomFrom = 0.75;
    /// <summary>A door's step out over a floor of the landblock at most this far below is no door from the ground.</summary>
    internal const double StepOutBelow = 12.0;
    /// <summary>Doors of one building whose centres are this close are one (a double door's two halves).</summary>
    internal const double SameDoorMeters = 1.0;
    /// <summary>A portal object's keep-out circle: its touch distance (1.9 m) and the corner the nav
    /// reach can cut (the Dungeon Patrol's default, 3.5 m in all).</summary>
    internal const double PortalKeepOutMeters = 3.5;
    /// <summary>A portal counts on the floors within this height of it.</summary>
    internal const double PortalLevelBand = 3.0;
    /// <summary>A bend round a portal keeps this much more than its circle.</summary>
    internal const double PortalBendMargin = 0.4;
    internal const int MaxBlocks = 25;
    /// <summary>A cell's floor centre in furniture moves at most this far to be out of it.</summary>
    internal const double CentreOutOfPlaced = 3.0;

    /// <summary>A portal object inside a building: its circle (world units).</summary>
    internal readonly record struct PortalSpot(uint Id, double X, double Y, double Z, double R = PortalKeepOutMeters);

    internal sealed class Building
    {
        public int Id;
        public uint Landblock;
        public int Index;
        public readonly List<uint> Cells = new();
        public readonly List<OutdoorHunt.Door> Doors = new();
        /// <summary>Openings to the outside that aren't doors, and why (diagnostics).</summary>
        public readonly List<string> NotDoors = new();
    }

    /// <summary>One landblock's buildings.</summary>
    internal sealed class Block
    {
        public uint Landblock;
        public Dictionary<uint, DungeonNavNode> Graph = new();
        public DungeonGeometry? Geo;
        public readonly List<Building> Buildings = new();
        public readonly Dictionary<uint, Building> ByCell = new();
        /// <summary>Portal records to the outside (cell, polygon key) the dats have, found polygon or not.</summary>
        public int ExitRecords;
        /// <summary>Openings to the outside shared by two cells, joined (JoinSharedOpenings).</summary>
        public int SharedOpenings;
        /// <summary>What the cells place: pieces given, how many went on the map (furniture), how many were left out as stairs.</summary>
        public int PlacedGiven, Furniture, Stairs;

        private Dictionary<uint, DungeonNavNode>? _pointGraph;
        /// <summary><see cref="Graph"/> with each cell that has a floor at its floor's centre (<see cref="AtFloorCentres"/>), for laying a walk's points.</summary>
        public Dictionary<uint, DungeonNavNode> PointGraph => _pointGraph ??= AtFloorCentres(Graph, Geo);
    }

    /// <summary>
    /// A copy of <paramref name="graph"/> with each cell that has a floor moved to its floor's centre. Every
    /// cell of a building sits at the building's own origin, so where a step has no doorway (the top of a
    /// stair is a floor opening, not a doorway) the walk's points fell back to the origin, on the ground
    /// floor: DreamWeave Thread E74E#14 (2026-11-17), every walk up its stairs went from the stair's foot
    /// back to the middle of the ground floor, then straight at the monster above, and ended under it.
    /// </summary>
    internal static Dictionary<uint, DungeonNavNode> AtFloorCentres(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo)
    {
        var g = new Dictionary<uint, DungeonNavNode>(graph.Count);
        foreach (var (id, n) in graph)
        {
            var m = new DungeonNavNode { CellId = n.CellId, NS = n.NS, EW = n.EW, Z = n.Z, Neighbors = n.Neighbors };
            if (geo != null && geo.TryCellFloorCentre(id, out double x, out double y, out double z))
            {
                // A centre in furniture (E74E#0's 0103: on a 0.45 m dais): the nearest spot of the cell's floor out of it.
                if (geo.PlacedCount > 0 && DungeonPathfinder.InsidePlacedAt(geo, x, y, z))
                    DungeonPathfinder.NearestOutOfPlaced(geo, ref x, ref y, z, CentreOutOfPlaced, new[] { id });
                m.NS = DungeonGeometry.WorldToNav(y);
                m.EW = DungeonGeometry.WorldToNav(x);
                m.Z = (float)z;
            }
            g[id] = m;
        }
        return g;
    }

    private readonly DatDatabase? _cellDat;
    private readonly Func<DungeonLOS?>? _los;
    private readonly Func<uint, bool>? _isDungeon;
    private readonly Func<uint, IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)>?>? _statics;
    private readonly Func<double, double, double>? _terrain;
    private readonly Dictionary<uint, Block?> _blocks = new();
    private readonly Queue<uint> _order = new();

    /// <summary>The portal objects round the character, in buildings and out (the plugin sets them): walks
    /// inside keep out of them, and a door with one at its step in or out isn't offered. A different set
    /// forgets the walks worked out round the last one.</summary>
    public IReadOnlyList<PortalSpot> Portals
    {
        get => _portals;
        set
        {
            var next = value ?? Array.Empty<PortalSpot>();
            bool same = next.Count == _portals.Count;
            for (int i = 0; same && i < next.Count; i++) same = next[i] == _portals[i];
            if (!same) _walks.Clear();
            _portals = next;
        }
    }
    private IReadOnlyList<PortalSpot> _portals = Array.Empty<PortalSpot>();

    // Walks worked out (the rooms never change; a monster or the character a little way off is the same
    // walk): asked several times a second, by several doors, and up to 3 ms each.
    private readonly Dictionary<(uint, long, long, long, uint, long, long, long), PathResult> _walks = new();
    private const double WalkKeySpacing = 0.25;
    private const int MaxWalks = 512;
    internal int WalksWorkedOut { get; private set; }
    private static long Key(double v) => (long)Math.Round(v / WalkKeySpacing);

    /// <summary>On the real dats: <paramref name="isDungeon"/> says which landblocks are dungeons (their cells are no building's);
    /// <paramref name="statics"/> gives what a landblock's cells place (GeometryLoader.GetDungeonStaticMeshes; null: no furniture);
    /// <paramref name="terrain"/> the ground's height at a world spot (GeometryLoader.GetTerrainZWorld; NaN unknown; null: no window test).</summary>
    public BuildingMap(DatDatabase? cellDat, Func<DungeonLOS?>? los, Func<uint, bool>? isDungeon = null,
                       Func<uint, IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)>?>? statics = null,
                       Func<double, double, double>? terrain = null)
    {
        _cellDat = cellDat;
        _los = los;
        _isDungeon = isDungeon;
        _statics = statics;
        _terrain = terrain;
    }

    internal DatDatabase? CellDat => _cellDat;

    /// <summary>A landblock built elsewhere (tests: a hand-built building).</summary>
    internal void Add(Block block)
    {
        if (!_blocks.ContainsKey(block.Landblock)) _order.Enqueue(block.Landblock);
        _blocks[block.Landblock] = block;
        _walks.Clear();
    }

    /// <summary>The landblock's buildings, read from the dats the first time (null: no dats, or a dungeon).</summary>
    internal Block? BlockFor(uint landblock)
    {
        if (_blocks.TryGetValue(landblock, out var b)) return b;
        if (_cellDat == null || !_cellDat.IsLoaded) return null;
        if (_isDungeon != null && _isDungeon(landblock)) b = null;
        else
        {
            try { b = Load(landblock, _cellDat, _los?.Invoke(), _statics, _terrain); }
            catch { b = null; }
        }
        while (_order.Count >= MaxBlocks) _blocks.Remove(_order.Dequeue());
        _blocks[landblock] = b;
        _order.Enqueue(landblock);
        return b;
    }

    private Building? Find(int id)
    {
        if (id <= 0) return null;
        var blk = BlockFor((uint)id >> 12);
        int idx = (id & 0xFFF) - 1;
        return blk != null && idx >= 0 && idx < blk.Buildings.Count ? blk.Buildings[idx] : null;
    }

    // ── OutdoorHunt.IBuildings ───────────────────────────────────────────────────────────────────

    public int BuildingOf(uint cell)
    {
        if (!OutdoorHunt.IsIndoorCell(cell)) return 0;
        var blk = BlockFor(cell >> 16);
        return blk != null && blk.ByCell.TryGetValue(cell, out var b) ? b.Id : 0;
    }

    /// <summary>The building's doors, less any a portal stands in front of (or behind).</summary>
    public IReadOnlyList<OutdoorHunt.Door> Doors(int building)
    {
        var b = Find(building);
        if (b == null) return Array.Empty<OutdoorHunt.Door>();
        if (Portals.Count == 0) return b.Doors;
        var list = new List<OutdoorHunt.Door>(b.Doors.Count);
        foreach (var d in b.Doors)
            if (!LegHitsPortal(d.Inner, d.At, Portals, out _) && !LegHitsPortal(d.At, d.Outside, Portals, out _)
                && !InAnyPortal(d.Inner, Portals) && !InAnyPortal(d.Outside, Portals))
                list.Add(d);
        return list;
    }

    public string Name(int building)
    {
        var b = Find(building);
        return b == null ? $"building {building}" : $"building {b.Landblock:X4}#{b.Index}";
    }

    public PathResult Inside(uint fromCell, Vec from, uint toCell, Vec to)
    {
        var key = (fromCell, Key(from.X), Key(from.Y), Key(from.Z), toCell, Key(to.X), Key(to.Y), Key(to.Z));
        if (_walks.TryGetValue(key, out var known)) return known;
        var p = WorkOutInside(fromCell, from, toCell, to);
        if (_walks.Count >= MaxWalks) _walks.Clear();
        _walks[key] = p;
        WalksWorkedOut++;
        return p;
    }

    /// <summary>
    /// The rooms walked through from <paramref name="fromCell"/> to <paramref name="toCell"/>, the shortest
    /// by the walk itself: from where it stands to each doorway in turn, then to the goal (Dijkstra, a cell
    /// entered at its doorway). A building's cells all sit at the building's own origin, so the dungeon A*
    /// (cell centre to cell centre) saw every step as 0 m and took whichever path came first: DreamWeave
    /// Thread BA17#4 (2026-11-17), from 012C to the door in 0122 it went five rooms round (west, north, east
    /// through the raised 012D) instead of two, and its last leg, 012D's doorway to the door, ran into the
    /// wall; the character ran at it for two minutes ("He's also still running into walls").
    /// </summary>
    internal static List<uint> WalkCells(Block blk, uint fromCell, Vec from, uint toCell, Vec to)
    {
        var g = blk.Graph;
        if (!g.ContainsKey(fromCell) || !g.ContainsKey(toCell)) return new List<uint>();
        if (fromCell == toCell) return new List<uint> { fromCell };
        var at = new Dictionary<uint, Vec> { [fromCell] = from };          // where each cell is entered
        var cost = new Dictionary<uint, double> { [fromCell] = 0 };
        var came = new Dictionary<uint, uint>();
        var open = new PriorityQueue<uint, double>();
        var done = new HashSet<uint>();
        open.Enqueue(fromCell, 0);
        double best = double.MaxValue;
        while (open.Count > 0)
        {
            uint c = open.Dequeue();
            if (!done.Add(c)) continue;
            if (cost[c] >= best) break;
            var p = at[c];
            if (c == toCell) { best = Math.Min(best, cost[c] + p.DistanceTo(to)); continue; }
            var n = g[c];
            foreach (uint nb in n.Neighbors)
            {
                if (done.Contains(nb) || !g.TryGetValue(nb, out var o) || DungeonPathfinder.IsDropEdge(n, o)) continue;
                // No doorway known (a floor or ceiling opening: a stair's top, a stairwell, a hole): the next
                // room's floor centre (PointGraph), not no distance. At no cost, E74E#14 (2026-11-17) went from
                // the ground floor up through the air under its platform and down to the far end of the second
                // floor, rather than up the stairs beside it.
                Vec d = blk.Geo != null && blk.Geo.TryGetDoorway(c, nb, out var door)
                    ? new Vec(door.X, door.Y, door.Z)
                    : blk.PointGraph.TryGetValue(nb, out var m)
                        ? new Vec(DungeonGeometry.NavToWorld(m.EW), DungeonGeometry.NavToWorld(m.NS), m.Z)
                        : p;
                double step = cost[c] + p.DistanceTo(d) + 0.1;   // a little for each doorway: fewer rooms when equal
                if (step >= cost.GetValueOrDefault(nb, double.MaxValue)) continue;
                cost[nb] = step;
                at[nb] = d;
                came[nb] = c;
                open.Enqueue(nb, step);
            }
        }
        if (!came.ContainsKey(toCell)) return new List<uint>();
        var path = new List<uint>();
        for (uint c = toCell; ; c = came[c])
        {
            path.Add(c);
            if (c == fromCell) break;
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// A leg of the walk that crosses a wall (SegmentClear says no) is bent through one point it can go by
    /// with both halves clear: a room's middle or a doorway on the way (the walk's own cells), the one
    /// adding the least. BA17#4 (2026-11-17): from the raised 012D the walk went from its doorway straight
    /// at the front door across 012B, into the wall; by 012B's middle both halves are clear. A leg with
    /// no such point is left as it was.
    /// </summary>
    internal static int MendBlockedLegs(List<Vec> way, IReadOnlyList<uint> cells, DungeonGeometry geo, Func<Vec, Vec, bool> clear)
    {
        var vias = new List<Vec>();
        foreach (uint c in cells)
            if (geo.TryCellFloorCentre(c, out double x, out double y, out double z)) vias.Add(new Vec(x, y, z));
        for (int i = 0; i + 1 < cells.Count; i++)
            if (geo.TryGetDoorway(cells[i], cells[i + 1], out var d)) vias.Add(new Vec(d.X, d.Y, d.Z, true));
        int mended = 0;
        for (int i = 0; i + 1 < way.Count && mended < 8; i++)
        {
            Vec a = way[i], b = way[i + 1];
            if (clear(a, b)) continue;
            Vec? best = null;
            double bestLen = double.MaxValue;
            foreach (var v in vias)
            {
                if (v.DistanceTo(a) < 0.3 || v.DistanceTo(b) < 0.3) continue;
                double len = a.DistanceTo(v) + v.DistanceTo(b);
                if (len >= bestLen || !clear(a, v) || !clear(v, b)) continue;
                best = v;
                bestLen = len;
            }
            if (best is Vec via)
            {
                way.Insert(i + 1, via);
                mended++;
                i++;   // the second half is clear: on past it
                continue;
            }
            // No one point: two (a room's middle, then a doorway, say), all three parts clear.
            Vec? b1 = null, b2 = null;
            foreach (var v in vias)
            {
                if (v.DistanceTo(a) < 0.3 || !clear(a, v)) continue;
                foreach (var w in vias)
                {
                    if (w.Equals(v) || w.DistanceTo(b) < 0.3) continue;
                    double len = a.DistanceTo(v) + v.DistanceTo(w) + w.DistanceTo(b);
                    if (len >= bestLen || !clear(v, w) || !clear(w, b)) continue;
                    b1 = v; b2 = w; bestLen = len;
                }
            }
            if (b1 is not Vec v1 || b2 is not Vec v2) continue;
            way.Insert(i + 1, v1);
            way.Insert(i + 2, v2);
            mended++;
            i += 2;
        }
        return mended;
    }

    private PathResult WorkOutInside(uint fromCell, Vec from, uint toCell, Vec to)
    {
        var none = new PathResult(PathStatus.NoPath, Array.Empty<Vec>(), 0, 0);
        if ((fromCell >> 16) != (toCell >> 16)) return none;
        var blk = BlockFor(fromCell >> 16);
        if (blk == null) return none;
        if (!blk.ByCell.TryGetValue(fromCell, out var bf) || !blk.ByCell.TryGetValue(toCell, out var bt) || bf != bt) return none;
        // Each end in the cell whose floor holds it. A door is mostly a thin cell of its own, and its step in, 1.5 m
        // in, is on the next cell's floor (BA17#4's front door 0122: 012B's; Holtburg A9B4's 010B: 0104's): a walk
        // from it in the door's cell went back out to the doorway between the two first, then in again.
        if (blk.Geo != null)
        {
            fromCell = CellHolding(blk.Geo, fromCell, bf.Cells, from.X, from.Y, from.Z);
            toCell = CellHolding(blk.Geo, toCell, bt.Cells, to.X, to.Y, to.Z);
        }
        var cells = WalkCells(blk, fromCell, from, toCell, to);
        if (cells.Count == 0) return none;

        var pts = DungeonPathfinder.BuildPathPoints(cells, blk.PointGraph, blk.Geo, null,
            DungeonGeometry.WorldToNav(from.Y), DungeonGeometry.WorldToNav(from.X), from.Z,
            DungeonGeometry.WorldToNav(to.Y), DungeonGeometry.WorldToNav(to.X), to.Z, out _, mergeSamePieces: false);
        var way = new List<Vec>(pts.Count + 2) { from };
        foreach (var p in pts)
            way.Add(new Vec(DungeonGeometry.NavToWorld(p.EW), DungeonGeometry.NavToWorld(p.NS), p.Z * 240.0, p.Doorway));
        way.Add(to);

        var geo = blk.Geo;
        var cellsOf = bf.Cells;
        Func<Vec, bool>? onFloor = geo == null ? null : v => geo.OnFloorOfAny(cellsOf, v.X, v.Y, v.Z);
        Func<Vec, Vec, bool>? clear = geo == null ? null : (a, b) => geo.SegmentClear(cellsOf, a.X, a.Y, a.Z, b.X, b.Y, b.Z, 0.3);
        if (geo != null && clear != null) MendBlockedLegs(way, cells, geo, clear);
        var status = KeepOutOfPortals(way, Portals, onFloor, clear, out double gap);
        if (status == PathStatus.NoPath) return none;

        double len = 0;
        for (int i = 1; i < way.Count; i++) len += way[i - 1].DistanceTo(way[i]);
        way.RemoveAt(0);
        return new PathResult(status, way, len, gap);
    }

    // ── Portals inside ───────────────────────────────────────────────────────────────────────────

    private static bool SameLevel(PortalSpot p, double z) => Math.Abs(z - p.Z) <= PortalLevelBand;

    internal static bool InPortal(PortalSpot p, Vec v) => SameLevel(p, v.Z) && Sq(v.X - p.X) + Sq(v.Y - p.Y) < p.R * p.R - 1e-6;

    internal static bool InAnyPortal(Vec v, IReadOnlyList<PortalSpot> portals)
    {
        foreach (var p in portals) if (InPortal(p, v)) return true;
        return false;
    }

    /// <summary>The leg a-b passes inside a portal's circle (on its level): the first such portal.</summary>
    internal static bool LegHitsPortal(Vec a, Vec b, IReadOnlyList<PortalSpot> portals, out PortalSpot hit)
    {
        hit = default;
        foreach (var p in portals)
        {
            if (!SameLevel(p, a.Z) && !SameLevel(p, b.Z)) continue;
            if (SegDist(a, b, p.X, p.Y, out _) < p.R - 1e-6) { hit = p; return true; }
        }
        return false;
    }

    /// <summary>
    /// Keeps the walk <paramref name="way"/> (start first, goal last; changed in place) out of the
    /// portals' circles: a goal inside one ends the walk where it would go in (Partial, <paramref name="gap"/>
    /// from the goal); a leg through one is bent round it (a corner either side, else two: on the floor,
    /// <paramref name="onFloor"/>, legs clear of walls, <paramref name="legClear"/>); a first leg that only
    /// leaves the circle the start stands in is kept. NoPath when a leg can't be bent round.
    /// </summary>
    internal static PathStatus KeepOutOfPortals(List<Vec> way, IReadOnlyList<PortalSpot> portals,
                                                Func<Vec, bool>? onFloor, Func<Vec, Vec, bool>? legClear, out double gap)
    {
        gap = 0;
        var status = PathStatus.Ok;
        if (portals.Count == 0 || way.Count < 2) return status;

        var goal = way[^1];
        if (InAnyPortal(goal, portals))
        {
            int k = way.Count - 1;
            while (k > 0 && InAnyPortal(way[k], portals)) k--;
            if (InAnyPortal(way[k], portals)) return PathStatus.NoPath;   // all of it inside: nowhere to stand
            // The leg way[k] -> way[k+1] goes in: the walk ends at its edge (a little short of it).
            var a = way[k]; var b = way[k + 1];
            double t = EntryAlong(a, b, portals);
            double len = a.FlatDistanceTo(b);
            double back = len > 1e-9 ? Math.Min(t, Math.Max(0, t - 0.3 / len)) : 0;
            var end = new Vec(a.X + (b.X - a.X) * back, a.Y + (b.Y - a.Y) * back, a.Z + (b.Z - a.Z) * back);
            way.RemoveRange(k + 1, way.Count - k - 1);
            if (end.FlatDistanceTo(a) > 0.05) way.Add(end);
            if (way.Count < 2) return PathStatus.NoPath;
            gap = way[^1].DistanceTo(goal);
            status = PathStatus.Partial;
        }

        // A corner on the way inside a circle (a doorway beside a portal): left out, and the legs either
        // side of it bent round the portal instead (on the floor, clear of walls) or no way at all.
        for (int i = way.Count - 2; i >= 1; i--)
            if (InAnyPortal(way[i], portals)) way.RemoveAt(i);

        int guard = 0;
        for (int i = 0; i + 1 < way.Count;)
        {
            var a = way[i]; var b = way[i + 1];
            if (!LegHitsPortal(a, b, portals, out var p)) { i++; continue; }
            // The character stands in the slack ring round a portal: the first leg may lead it out.
            if (i == 0 && InPortal(p, a) && MovesAway(a, b, p)) { i++; continue; }
            if (++guard > 24) return PathStatus.NoPath;
            var bend = Bend(a, b, p, portals, onFloor, legClear);
            if (bend == null) return PathStatus.NoPath;
            way.InsertRange(i + 1, bend);
        }
        return status;
    }

    // The fraction along a-b where it first comes into a circle (1 when it never does).
    private static double EntryAlong(Vec a, Vec b, IReadOnlyList<PortalSpot> portals)
    {
        double best = 1;
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        if (l2 < 1e-12) return 0;
        foreach (var p in portals)
        {
            if (!SameLevel(p, b.Z)) continue;
            // |a + t d - p|^2 = R^2: the smaller root in [0, 1].
            double fx = a.X - p.X, fy = a.Y - p.Y;
            double bq = 2 * (fx * dx + fy * dy), c = fx * fx + fy * fy - p.R * p.R;
            double disc = bq * bq - 4 * l2 * c;
            if (disc < 0) continue;
            double t = (-bq - Math.Sqrt(disc)) / (2 * l2);
            if (t >= 0 && t < best) best = t;
        }
        return best;
    }

    // The leg a-b gets no closer to p than a is (it leads away).
    private static bool MovesAway(Vec a, Vec b, PortalSpot p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return (a.X - p.X) * dx + (a.Y - p.Y) * dy >= 0;
    }

    // Corners round portal p for the leg a-b: one corner either side (out from the leg's nearest
    // approach), else two (a box round the circle's side); the shortest whose corners are on the floor,
    // whose legs miss p and are clear of walls. Null when none is.
    private static List<Vec>? Bend(Vec a, Vec b, PortalSpot p, IReadOnlyList<PortalSpot> portals,
                                   Func<Vec, bool>? onFloor, Func<Vec, Vec, bool>? legClear)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return null;
        dx /= len; dy /= len;
        SegDist(a, b, p.X, p.Y, out double t);
        double qx = a.X + (b.X - a.X) * t, qy = a.Y + (b.Y - a.Y) * t, qz = a.Z + (b.Z - a.Z) * t;
        double ux = qx - p.X, uy = qy - p.Y, ul = Math.Sqrt(ux * ux + uy * uy);
        if (ul < 0.05) { ux = -dy; uy = dx; } else { ux /= ul; uy /= ul; }
        double r = p.R + PortalBendMargin;

        List<Vec>? best = null;
        double bestLen = double.MaxValue;
        void Try(params Vec[] corners)
        {
            var pts = new List<Vec>(corners.Length + 2) { a };
            pts.AddRange(corners);
            pts.Add(b);
            double l = 0;
            bool startsIn = InPortal(p, a);   // the character in the ring round it: its first leg must lead away
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                if (k == 0 && startsIn ? !MovesAway(pts[0], pts[1], p) : SegDist(pts[k], pts[k + 1], p.X, p.Y, out _) < p.R - 1e-6) return;
                if (legClear != null && !legClear(pts[k], pts[k + 1])) return;
                l += pts[k].FlatDistanceTo(pts[k + 1]);
            }
            foreach (var c in corners)
                if (InAnyPortal(c, portals) || (onFloor != null && !onFloor(c))) return;
            if (l < bestLen) { bestLen = l; best = new List<Vec>(corners); }
        }
        // Corners at the circle's tangent distance on each side: r / cos of the half angle would be exact;
        // the leg's ends are usually far enough that r out from the nearest approach clears it.
        foreach (double side in new[] { 1.0, -1.0 })
        {
            double sx = ux * side, sy = uy * side;
            Try(new Vec(p.X + sx * r, p.Y + sy * r, qz));
            if (best != null) continue;
            // A box: two corners beside the circle, before and after it along the leg.
            Try(new Vec(p.X + sx * r - dx * r, p.Y + sy * r - dy * r, qz), new Vec(p.X + sx * r + dx * r, p.Y + sy * r + dy * r, qz));
        }
        return best;
    }

    // Flat distance from (px, py) to the segment a-b, and where along it (0..1) the nearest point is.
    private static double SegDist(Vec a, Vec b, double px, double py, out double t)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        t = l2 < 1e-12 ? 0 : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / l2, 0, 1);
        return Math.Sqrt(Sq(a.X + dx * t - px) + Sq(a.Y + dy * t - py));
    }

    private static double Sq(double v) => v * v;

    // ── Reading a landblock ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A landblock's buildings from the dats: its cell graph, the room geometry (with DungeonLOS), and
    /// the polygons of its portal records to the outside (other cell 0xFFFF). Without DungeonLOS no
    /// door is known (the hunt then leaves its buildings alone, and walks out of one the way in).
    /// <paramref name="statics"/>: what the cells place, for the furniture (none: the rooms bare, as before).
    /// </summary>
    internal static Block Load(uint landblock, DatDatabase cellDat, DungeonLOS? los,
                               Func<uint, IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)>?>? statics = null,
                               Func<double, double, double>? terrain = null)
    {
        // A graph of its own (never DungeonPathfinder's cached one): shared openings add edges to it.
        var graph = DungeonPathfinder.BuildGraph(landblock, cellDat);
        var exits = ExitRecords(landblock, cellDat);
        var exitPolys = new List<(uint, Vector3[])>();
        if (los == null || graph.Count == 0)
        {
            var bare = Build(landblock, graph, null, null, null, exitPolys);
            bare.ExitRecords = exits.Count;
            return bare;
        }
        var portals = new List<(uint, Vector3[])>();
        var walls = new List<(uint, Vector3[])>();
        var floors = new List<(uint, Vector3[])>();
        var want = new HashSet<(uint, ushort)>(exits);
        foreach (var p in los.GetDungeonMapPolygons(landblock))
        {
            if (p.Vertices == null) continue;
            if (p.IsPortal)
            {
                portals.Add((p.CellId, p.Vertices));
                if (want.Contains((p.CellId, p.PolyKey))) exitPolys.Add((p.CellId, p.Vertices));
            }
            else walls.Add((p.CellId, p.Vertices));
        }
        foreach (var p in los.GetDungeonMapFloorPolygons(landblock))
            if (p.Vertices != null && !p.IsPortal) floors.Add((p.CellId, p.Vertices));
        IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)>? placed = null;
        try { placed = statics?.Invoke(landblock); }
        catch { placed = null; }   // no statics: the rooms bare, the doors and walks as before
        var block = Build(landblock, graph, portals, floors, walls, exitPolys, placed, terrain);
        block.ExitRecords = exits.Count;
        return block;
    }

    /// <summary>Two openings to the outside this close (centres, and foot heights) are one opening seen from two cells.</summary>
    internal const double SharedOpeningMeters = 1.0;

    /// <summary>
    /// Openings to the outside that are the same opening from two cells (the walls of a fort, DF61:
    /// the gatehouse and the wall beside it are separate groups of cells whose "outside" there is the
    /// other's inside): each pair becomes an edge of <paramref name="graph"/> (both ways), so the
    /// geometry makes it a doorway and the two groups one building. Returns the openings left.
    /// </summary>
    internal static List<(uint Cell, Vector3[] Poly)> JoinSharedOpenings(Dictionary<uint, DungeonNavNode> graph,
                                                                          IReadOnlyList<(uint Cell, Vector3[] Poly)> exitPolys, out int joined)
    {
        joined = 0;
        var info = new List<(uint Cell, Vector3[] Poly, bool Ok, double X, double Y, double Z)>(exitPolys.Count);
        foreach (var (cell, poly) in exitPolys)
        {
            bool ok = DungeonGeometry.TryOpening(poly, out double x, out double y, out double z, out _, out _, out _, out _);
            info.Add((cell, poly, ok, x, y, z));
        }
        var used = new bool[info.Count];
        for (int i = 0; i < info.Count; i++)
        {
            if (!info[i].Ok || used[i]) continue;
            for (int j = i + 1; j < info.Count; j++)
            {
                if (!info[j].Ok || used[j] || info[j].Cell == info[i].Cell) continue;
                double dx = info[i].X - info[j].X, dy = info[i].Y - info[j].Y;
                if (dx * dx + dy * dy > SharedOpeningMeters * SharedOpeningMeters || Math.Abs(info[i].Z - info[j].Z) > 0.5) continue;
                if (!graph.TryGetValue(info[i].Cell, out var a) || !graph.TryGetValue(info[j].Cell, out var b)) continue;
                if (!a.Neighbors.Contains(b.CellId)) a.Neighbors.Add(b.CellId);
                if (!b.Neighbors.Contains(a.CellId)) b.Neighbors.Add(a.CellId);
                used[i] = used[j] = true;
                joined++;
                break;
            }
        }
        var left = new List<(uint, Vector3[])>(info.Count);
        for (int i = 0; i < info.Count; i++)
            if (!used[i]) left.Add((info[i].Cell, info[i].Poly));
        return left;
    }

    /// <summary>Every portal record of the landblock's EnvCells whose other cell is 0xFFFF (the outside): (cell, polygon key).</summary>
    internal static List<(uint Cell, ushort Poly)> ExitRecords(uint landblock, DatDatabase cellDat)
    {
        var list = new List<(uint, ushort)>();
        foreach (uint cellId in cellDat.GetLandblockCellIds(landblock))
        {
            byte[]? data = cellDat.GetFileData(cellId);
            if (data == null || data.Length < 36) continue;
            try
            {
                using var ms = new MemoryStream(data);
                using var r = new BinaryReader(ms);
                r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();   // id, flags, id again
                byte numSurfaces = r.ReadByte();
                byte numPortals = r.ReadByte();
                r.ReadUInt16();                                    // visible cells
                ms.Seek(numSurfaces * 2, SeekOrigin.Current);
                r.ReadUInt16(); r.ReadUInt16();                    // environment, cell structure
                ms.Seek(28, SeekOrigin.Current);                   // position, rotation
                for (int i = 0; i < numPortals && ms.Position + 8 <= ms.Length; i++)
                {
                    r.ReadUInt16();                                // flags
                    ushort poly = r.ReadUInt16();
                    ushort other = r.ReadUInt16();
                    r.ReadUInt16();                                // other portal
                    if (other == 0xFFFF) list.Add((cellId, poly));
                }
            }
            catch { /* a malformed cell: no doors from it */ }
        }
        return list;
    }

    /// <summary>
    /// A landblock's buildings from its cell graph, its polygons (portal, floor and wall polygons as
    /// DungeonGeometry.Build takes them; null portals: no geometry) and the polygons of its openings to
    /// the outside (each in its cell): openings shared by two cells join them; the cells joined to each
    /// other are a building; each opening wide and tall enough, its foot at the floor inside and the
    /// ground (no floor of the landblock) under its step out, is a door (facing out of its cell).
    /// <paramref name="statics"/> (what the cells place) go on the geometry as furniture before the doors
    /// are looked for, so no door's step in is inside one (AddFurniture).
    /// </summary>
    internal static Block Build(uint landblock, Dictionary<uint, DungeonNavNode> graph,
                                IReadOnlyList<(uint, Vector3[])>? portals, IReadOnlyList<(uint, Vector3[])>? floors,
                                IReadOnlyList<(uint, Vector3[])>? walls, IReadOnlyList<(uint Cell, Vector3[] Poly)> exitPolys,
                                IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)>? statics = null,
                                Func<double, double, double>? terrain = null)
    {
        var exits = JoinSharedOpenings(graph, exitPolys, out int joined);
        DungeonGeometry? geo = portals == null ? null
            : DungeonGeometry.Build(graph, portals, floors ?? Array.Empty<(uint, Vector3[])>(), walls ?? Array.Empty<(uint, Vector3[])>());
        var block = new Block { Landblock = landblock, Graph = graph, Geo = geo, SharedOpenings = joined };
        if (geo != null) DungeonPathfinder.MarkBuildingMap(geo);
        if (geo != null && statics is { Count: > 0 })
        {
            var f = AddFurniture(geo, statics);
            block.PlacedGiven = statics.Count;
            block.Furniture = f.Kept;
            block.Stairs = f.Stairs.Count;
        }

        // Buildings: the cells joined to each other (union-find over the portal records, either way).
        var parent = new Dictionary<uint, uint>(graph.Count);
        foreach (uint c in graph.Keys) parent[c] = c;
        uint Root(uint x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var n in graph.Values)
            foreach (uint nb in n.Neighbors)
            {
                if (!parent.ContainsKey(nb)) continue;
                uint ra = Root(n.CellId), rb = Root(nb);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        var ids = new List<uint>(graph.Keys);
        ids.Sort();
        var byRoot = new Dictionary<uint, Building>();
        foreach (uint c in ids)
        {
            uint r = Root(c);
            if (!byRoot.TryGetValue(r, out var b))
            {
                b = new Building { Index = block.Buildings.Count, Landblock = landblock };
                b.Id = (int)((landblock & 0xFFFF) << 12) | (b.Index + 1);
                byRoot[r] = b;
                block.Buildings.Add(b);
            }
            b.Cells.Add(c);
            block.ByCell[c] = b;
        }

        foreach (var (cell, poly) in exits)
        {
            if (!block.ByCell.TryGetValue(cell, out var b) || !graph.TryGetValue(cell, out var node)) continue;
            if (TryDoor(cell, node, poly, geo, b.Cells, out var door, out string why, terrain))
            {
                bool same = false;
                foreach (var d in b.Doors)
                    if (d.At.FlatDistanceTo(door.At) < SameDoorMeters && Math.Abs(d.At.Z - door.At.Z) < 1.0) { same = true; break; }
                if (!same) b.Doors.Add(door);
            }
            else b.NotDoors.Add($"{cell:X8}: {why}");
        }
        return block;
    }

    /// <summary>
    /// The door an opening to the outside makes, in <paramref name="cell"/>: facing out of the cell (the
    /// floor behind it is the cell's, else the cell's origin is behind it), its inner point on the cell's
    /// floor, its outer point OutsideMeters out. False (and why) for one too narrow, too low, or with its
    /// foot off the floor inside (a window). The inner point is never inside furniture, nor the leg in to
    /// it through one (InnerPoint). It can be past a doorway, in the next cell: a walk from it starts there
    /// (WorkOutInside, CellHolding).
    /// </summary>
    internal static bool TryDoor(uint cell, DungeonNavNode node, Vector3[] poly, DungeonGeometry? geo, IReadOnlyCollection<uint> buildingCells,
                                 out OutdoorHunt.Door door, out string why, Func<double, double, double>? terrain = null)
    {
        door = default;
        if (!DungeonGeometry.TryOpening(poly, out double x, out double y, out double z, out double nx, out double ny, out double width, out double top))
        {
            why = "faces up or down (a shaft)";
            return false;
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (width < MinDoorWidth) { why = string.Format(inv, "{0:F1} m wide", width); return false; }
        if (top - z < MinDoorHeight) { why = string.Format(inv, "{0:F1} m tall", top - z); return false; }

        // Out of the cell: the floor behind the opening is the cell's.
        int vote = 0;
        if (geo != null && geo.HasFloor(cell))
            vote = geo.OnCellFloor(cell, x - nx * 0.75, y - ny * 0.75, z) ? 1 : geo.OnCellFloor(cell, x + nx * 0.75, y + ny * 0.75, z) ? -1 : 0;
        if (vote == 0)
        {
            double cx = DungeonGeometry.NavToWorld(node.EW), cy = DungeonGeometry.NavToWorld(node.NS);
            vote = (cx - x) * nx + (cy - y) * ny <= 0 ? 1 : -1;
        }
        if (vote < 0) { nx = -nx; ny = -ny; }

        // Its foot at the floor inside (a window's sill is a metre or more up).
        double floor = z;
        if (geo != null && geo.HasFloor(cell))
        {
            bool found = false;
            for (double s = 0.5; s <= InnerLongest + 1e-9 && !found; s += 0.25)
                found = geo.TryFloorHeight(x - nx * s, y - ny * s, z, 2.5, out floor);
            if (!found) { why = "no floor inside it"; return false; }
            if (Math.Abs(z - floor) > DoorSillMax) { why = string.Format(inv, "its foot is {0:F1} m off the floor (a window)", z - floor); return false; }

            // A window with a deep sill: the floor just inside is the sill's own top, level with the foot, and the
            // room's floor is a metre below (DreamWeave Thread B470#25, 2026-11-19: the hunt walked at its two 1.5 m
            // tall windows, 1.0 m over the room's floor and the ground, as doors). Under WindowTallMax, a foot more than a
            // step over both the ground outside and the floor a body stands on inside (WindowRoomFrom to InnerLongest in;
            // none there: no floor to step down to) is a window. Over the 1,633 landblocks with buildings this took 416
            // of 6,322 doors, every one 1.5 to 2.0 m tall; 9 buildings were left without a door, each an opening 5.7 m
            // up a tower or a wide window.
            if (terrain != null && top - z < WindowTallMax)
            {
                double ground = terrain(x + nx * OutsideMeters, y + ny * OutsideMeters);
                if (!double.IsNaN(ground) && z - ground > StepUpMeters)
                {
                    double room = double.NaN;
                    for (double s = InnerLongest; s >= WindowRoomFrom - 1e-9 && double.IsNaN(room); s -= 0.25)
                        if (geo.TryFloorHeight(x - nx * s, y - ny * s, z, 2.5, out double f)) room = f;
                    if (double.IsNaN(room) || z - room > StepUpMeters)
                    {
                        why = string.Format(inv, "a window: {0:F1} m tall, its foot {1:F1} m over the ground outside and {2}", top - z, z - ground,
                            double.IsNaN(room) ? "no floor a step past it inside" : string.Format(inv, "{0:F1} m over the floor inside", z - room));
                        return false;
                    }
                }
            }
        }

        // The step out is onto the ground: no floor of the landblock under it, from 8 m below to just
        // above (A9B4, A9B3: upper-storey doors onto the roof of the floor below, 4 m down; DF61: doors
        // onto the top of a fort's wall, 5 to 10 m up).
        if (geo != null && geo.TryFloorHeight(x + nx * OutsideMeters, y + ny * OutsideMeters, z - StepOutBelow / 2, StepOutBelow / 2 + 0.4, out double below))
        {
            why = string.Format(inv, "its step out is over a floor {0:F1} m below (a roof, a balcony, another room)", z - below);
            return false;
        }

        // The inner point: as far in as InnerLongest that is on the building's floor (and clear of furniture).
        double ix = x - nx, iy = y - ny;
        if (geo != null && !InnerPoint(geo, buildingCells, x, y, nx, ny, floor, out ix, out iy, out bool furniture))
        {
            why = furniture ? "furniture stands a step inside it" : "no floor a step inside it";
            return false;
        }

        door = new OutdoorHunt.Door(cell,
            Inner: new Vec(ix, iy, floor, true),
            At: new Vec(x, y, z, true),
            Outside: new Vec(x + nx * OutsideMeters, y + ny * OutsideMeters, z, true));
        why = "";
        return true;
    }

    /// <summary>How far a door's step in goes to either side of its centre line when furniture is on the line.</summary>
    internal const double InnerSideways = 0.5;

    /// <summary>
    /// The door's inner point: on its centre line as far in as InnerLongest (shortened in 0.25 m steps to
    /// InnerShortest) on the building's floor, not inside furniture, the leg in to it from the door crossing
    /// none; else the same InnerSideways either side of the line. False when none is (<paramref name="furniture"/>:
    /// one had a floor, furniture was in the way).
    /// </summary>
    private static bool InnerPoint(DungeonGeometry geo, IReadOnlyCollection<uint> buildingCells, double x, double y, double nx, double ny,
                                   double floor, out double ix, out double iy, out bool furniture)
    {
        furniture = false;
        bool objects = geo.PlacedCount > 0;
        foreach (double side in objects ? new[] { 0.0, InnerSideways, -InnerSideways } : new[] { 0.0 })
        {
            double ox = -ny * side, oy = nx * side;
            for (double s = InnerLongest; s >= InnerShortest - 1e-9; s -= 0.25)
            {
                ix = x - nx * s + ox; iy = y - ny * s + oy;
                if (!geo.OnFloorOfAny(buildingCells, ix, iy, floor)) continue;
                if (objects && !LegClearOfPlaced(geo, x, y, ix, iy, floor)) { furniture = true; continue; }
                return true;
            }
        }
        ix = iy = 0;
        return false;
    }

    /// <summary>The straight walk from (x0, y0) to (x1, y1), on the floor under it (the nearest within a metre of
    /// <paramref name="floor"/>, then of the last sample's), is inside no placed object anywhere along it (every 0.1 m,
    /// the far end included). The floor under a door's step in can be a step below its sill (Holtburg A9B4#0's door
    /// 0102: the sill at 67.0, the room at 66.0, a bench there whose top is the sill's height).</summary>
    internal static bool LegClearOfPlaced(DungeonGeometry geo, double x0, double y0, double x1, double y1, double floor)
    {
        double len = Math.Sqrt(Sq(x1 - x0) + Sq(y1 - y0));
        int n = Math.Max(1, (int)Math.Ceiling(len / 0.1));
        double h = floor;
        for (int k = 1; k <= n; k++)
        {
            double t = (double)k / n, px = x0 + (x1 - x0) * t, py = y0 + (y1 - y0) * t;
            if (geo.TryFloorHeight(px, py, h, 1.0, out double fh)) h = fh;
            if (geo.InsidePlaced(px, py, h)) return false;
        }
        return true;
    }

    /// <summary>The building's cell whose floor (the nearest in height to <paramref name="floor"/>, within 0.6 m) holds (x, y):
    /// <paramref name="cell"/> itself when its floor does; <paramref name="cell"/> too when none does.</summary>
    private static uint CellHolding(DungeonGeometry geo, uint cell, IReadOnlyCollection<uint> buildingCells, double x, double y, double floor)
    {
        if (geo.TryCellFloorHeight(cell, x, y, floor, out double h0) && Math.Abs(h0 - floor) <= 0.6) return cell;
        uint best = cell;
        double bestDz = 0.6;
        foreach (uint c in buildingCells)
            if (c != cell && geo.TryCellFloorHeight(c, x, y, floor, out double h) && Math.Abs(h - floor) <= bestDz) { best = c; bestDz = Math.Abs(h - floor); }
        return best;
    }

    // ── Placed objects (2026-11-18) ─────────────────────────────────────────────────────────────
    //
    // A building's cells place its furniture (tables, chairs, beds, stools, benches, cabinets) and, in most
    // buildings, its stairs too: 6,350 of the 6,571 placed stairs world-wide stand on a flat cell floor, the
    // object the only thing to walk up. Without them every walk went through the furniture (the survey's six
    // landblocks 7D64, E74E, BA17, A9B4, A9B3 and DF61: 194 of 1,110 walks, 88 of their corners inside a piece).
    // The furniture goes on the map as the dungeon map's placed objects (DungeonGeometry.AddPlacedObjects: walls
    // round what is in a body's way at its floor), the stairs stay off it: as walls the ground floor would meet a
    // stair's sides on every walk up (stairs as floors is a later step).
    //
    // A stair is a CONNECTOR that also climbs by itself. The map's own class alone is not enough here: its chain
    // of walkable faces runs across objects, so a chair (its seat, then the top of its back at 1.02 m), a cup on a
    // table and a cabinet beside one are connectors too (in the six landblocks 107 of the 184 connectors are such
    // furniture). What climbs by itself: a sloped walkable face of some size (a ramp, a stair's stringer: furniture
    // tops are flat), walkable faces at three heights or more over 0.9 m, or a slab raised a metre or more (the landing
    // between DF61 010C's two ramps). A sphere's cap is sloped too, but in triangles too small to count; its rings are
    // walkable faces at many heights though, so the loader's round solids (its cylinders and spheres) are never stairs: a
    // 1 m ball the chain ran to from a dais beside it was taken for steps and left off (8090012A's 0x0200035E, review of
    // wf/obst-int). Besides those
    // 77, a piece the map doesn't take for a connector is still a stair when it is a ramp up from its own floor (the
    // bottom step of a stair whose chain doesn't reach the next: 7 in the six). Measured: every stair of the six the
    // survey's own rule found is one of these 84, but for 7 spheres and platforms that rule counted by their caps.
    // Dungeons are not touched: there every object the map takes stays a wall (DungeonPathfinder.AddPlacedObjectsTo).

    /// <summary>A walkable face less flat than this (nz) is a slope: a ramp, a stair's stringer.</summary>
    internal const double StairSlopeNz = 0.95;
    /// <summary>A sloped face at least this big (m2, one triangle) makes a connector a stair; a sphere's cap rings are smaller.</summary>
    internal const double StairSlopeTriangle = 0.3;
    /// <summary>Stepped: walkable faces at StairSteps heights or more, StairStepGap apart, spanning StairStepSpan.</summary>
    internal const double StairStepGap = 0.15, StairStepSpan = 0.9;
    internal const int StairSteps = 3;
    /// <summary>A raised slab: its bottom this far over its floor, its flat walkable area (m2) this big.</summary>
    internal const double StairSlabRaised = 1.0, StairSlabArea = 1.0;

    /// <summary>Which of a landblock's placed objects a building map takes: what the dungeon map would (in the body band at its
    /// floor) less the stairs, whose keys it keeps.</summary>
    internal sealed class FurnitureFilter
    {
        private readonly Dictionary<PieceKey, Vector3[]> _tris = new();
        private readonly HashSet<PieceKey> _round = new();
        /// <summary>The pieces left out as stairs.</summary>
        public readonly HashSet<PieceKey> Stairs = new();
        /// <summary>How many went on the map.</summary>
        public int Kept;

        /// <summary><paramref name="round"/>: the pieces that are the loader's cylinders and spheres (BoundingVolume.Round), never stairs.</summary>
        public FurnitureFilter(IEnumerable<(uint SetupId, uint CellId, Vector3[] Triangles)> pieces, IEnumerable<PieceKey>? round = null)
        {
            foreach (var (setup, cell, tris) in pieces)
                if (KeyOf(setup, cell, tris) is PieceKey k) _tris.TryAdd(k, tris);
            if (round != null) _round.UnionWith(round);
        }

        public PlacedObjectOptions Options => new() { Keep = Keep };

        private bool Keep(PlacedInfo p)
        {
            if (p.Class is not (PlacedClass.Obstacle or PlacedClass.StepOver or PlacedClass.Connector)) return false;
            var k = new PieceKey(p.Setup, p.Cell, p.MinX, p.MinY, p.MinZ, p.MaxX, p.MaxY, p.MaxZ);
            if (!_round.Contains(k) && _tris.TryGetValue(k, out var t)
                && (p.Class == PlacedClass.Connector ? ClimbsByItself(t, p.Floor) : RampFromFloor(t, p.Floor)))
            {
                Stairs.Add(k);
                return false;
            }
            Kept++;
            return true;
        }
    }

    /// <summary>A placed piece by its model, cell and box (the box PlacedSolid gives it: its vertices' own).</summary>
    internal readonly record struct PieceKey(uint Setup, uint Cell, double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

    internal static PieceKey? KeyOf(uint setup, uint cell, Vector3[]? tris)
    {
        if (tris == null || tris.Length < 3) return null;
        int n = tris.Length - tris.Length % 3;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int k = 0; k < n; k++)
        {
            var p = tris[k];
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        return new PieceKey(setup, cell, minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>
    /// A piece standing on <paramref name="floor"/> climbs by itself (see the section's comment): a sloped walkable
    /// triangle of StairSlopeTriangle or more, walkable faces at StairSteps heights over StairStepSpan, or a raised slab.
    /// Either winding counts: the dats list some stairs' faces both ways round (Holtburg A9B3's 020001B4).
    /// </summary>
    internal static bool ClimbsByItself(Vector3[] t, double floor)
    {
        double bottom = double.MaxValue, flat = 0;
        int lo = int.MaxValue, hi = int.MinValue;
        var heights = new HashSet<int>();
        bool slope = false;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            var p0 = t[i]; var p1 = t[i + 1]; var p2 = t[i + 2];
            bottom = Math.Min(bottom, Math.Min(p0.Z, Math.Min(p1.Z, p2.Z)));
            double ux = p1.X - p0.X, uy = p1.Y - p0.Y, uz = p1.Z - p0.Z, vx = p2.X - p0.X, vy = p2.Y - p0.Y, vz = p2.Z - p0.Z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, len = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (len < 1e-9) continue;
            double nz = Math.Abs(cz) / len;
            if (nz < DungeonGeometry.WalkableNz) continue;
            double area = len / 2;
            if (nz < StairSlopeNz) { if (area >= StairSlopeTriangle) slope = true; }
            else flat += area;
            double h = (p0.Z + p1.Z + p2.Z) / 3.0 - floor;
            if (h < StairStepGap || h > 6.0) continue;
            int step = (int)Math.Round(h / StairStepGap);
            if (heights.Add(step)) { lo = Math.Min(lo, step); hi = Math.Max(hi, step); }
        }
        if (slope) return true;
        if (heights.Count >= StairSteps && (hi - lo) * StairStepGap >= StairStepSpan - 1e-9) return true;
        // A slab's top and bottom are both flat walkable faces: half the area is its top.
        return bottom - floor >= StairSlabRaised && flat / 2 >= StairSlabArea;
    }

    /// <summary>
    /// A piece the map doesn't take for a connector that is still a ramp up from its floor: a sloped walkable triangle of
    /// StairSlopeTriangle or more whose foot is on the floor (within StairStepGap). The bottom step of a stair whose next
    /// one the chain doesn't reach (E74E's door steps: 01000ACA, 0 to 0.6 m, under 01000AC5 and 01000ACD up to the sill
    /// at 32.0), a dais with sloped sides (7D64's 0200033D). A desk's or a lectern's top is no ramp: its foot is off the floor.
    /// </summary>
    internal static bool RampFromFloor(Vector3[] t, double floor)
    {
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            var p0 = t[i]; var p1 = t[i + 1]; var p2 = t[i + 2];
            double ux = p1.X - p0.X, uy = p1.Y - p0.Y, uz = p1.Z - p0.Z, vx = p2.X - p0.X, vy = p2.Y - p0.Y, vz = p2.Z - p0.Z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, len = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (len < 1e-9) continue;
            double nz = Math.Abs(cz) / len;
            if (nz < DungeonGeometry.WalkableNz || nz >= StairSlopeNz || len / 2 < StairSlopeTriangle) continue;
            if (Math.Min(p0.Z, Math.Min(p1.Z, p2.Z)) - floor <= StairStepGap) return true;
        }
        return false;
    }

    /// <summary>The furniture onto <paramref name="geo"/> (DungeonGeometry.AddPlacedObjects, the stairs left out).</summary>
    internal static FurnitureFilter AddFurniture(DungeonGeometry geo, IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)> statics)
    {
        var pieces = new List<(uint, uint, Vector3[])>(statics.Count);
        var round = new List<PieceKey>();
        foreach (var s in statics)
            if (s.Mesh?.MeshTriangles is { Length: >= 3 } t)
            {
                pieces.Add((s.SetupId, s.CellId, t));
                if (s.Mesh.Round && KeyOf(s.SetupId, s.CellId, t) is PieceKey k) round.Add(k);
            }
        var filter = new FurnitureFilter(pieces, round);
        geo.AddPlacedObjects(pieces, filter.Options);
        return filter;
    }
}
