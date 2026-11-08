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
//     rooms as the patrol lays them), kept out of the circles of portal objects standing inside.
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
    }

    private readonly DatDatabase? _cellDat;
    private readonly Func<DungeonLOS?>? _los;
    private readonly Func<uint, bool>? _isDungeon;
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

    /// <summary>On the real dats: <paramref name="isDungeon"/> says which landblocks are dungeons (their cells are no building's).</summary>
    public BuildingMap(DatDatabase? cellDat, Func<DungeonLOS?>? los, Func<uint, bool>? isDungeon = null)
    {
        _cellDat = cellDat;
        _los = los;
        _isDungeon = isDungeon;
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
            try { b = Load(landblock, _cellDat, _los?.Invoke()); }
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

    private PathResult WorkOutInside(uint fromCell, Vec from, uint toCell, Vec to)
    {
        var none = new PathResult(PathStatus.NoPath, Array.Empty<Vec>(), 0, 0);
        if ((fromCell >> 16) != (toCell >> 16)) return none;
        var blk = BlockFor(fromCell >> 16);
        if (blk == null) return none;
        if (!blk.ByCell.TryGetValue(fromCell, out var bf) || !blk.ByCell.TryGetValue(toCell, out var bt) || bf != bt) return none;
        var cells = DungeonPathfinder.FindPath(fromCell, toCell, blk.Graph);
        if (cells.Count == 0) return none;

        var pts = DungeonPathfinder.BuildPathPoints(cells, blk.Graph, blk.Geo, null,
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
    /// </summary>
    internal static Block Load(uint landblock, DatDatabase cellDat, DungeonLOS? los)
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
        var block = Build(landblock, graph, portals, floors, walls, exitPolys);
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
    /// </summary>
    internal static Block Build(uint landblock, Dictionary<uint, DungeonNavNode> graph,
                                IReadOnlyList<(uint, Vector3[])>? portals, IReadOnlyList<(uint, Vector3[])>? floors,
                                IReadOnlyList<(uint, Vector3[])>? walls, IReadOnlyList<(uint Cell, Vector3[] Poly)> exitPolys)
    {
        var exits = JoinSharedOpenings(graph, exitPolys, out int joined);
        DungeonGeometry? geo = portals == null ? null
            : DungeonGeometry.Build(graph, portals, floors ?? Array.Empty<(uint, Vector3[])>(), walls ?? Array.Empty<(uint, Vector3[])>());
        var block = new Block { Landblock = landblock, Graph = graph, Geo = geo, SharedOpenings = joined };

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
            if (TryDoor(cell, node, poly, geo, b.Cells, out var door, out string why))
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
    /// foot off the floor inside (a window).
    /// </summary>
    internal static bool TryDoor(uint cell, DungeonNavNode node, Vector3[] poly, DungeonGeometry? geo, IReadOnlyCollection<uint> buildingCells,
                                 out OutdoorHunt.Door door, out string why)
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
        }

        // The step out is onto the ground: no floor of the landblock under it, from 8 m below to just
        // above (A9B4, A9B3: upper-storey doors onto the roof of the floor below, 4 m down; DF61: doors
        // onto the top of a fort's wall, 5 to 10 m up).
        if (geo != null && geo.TryFloorHeight(x + nx * OutsideMeters, y + ny * OutsideMeters, z - StepOutBelow / 2, StepOutBelow / 2 + 0.4, out double below))
        {
            why = string.Format(inv, "its step out is over a floor {0:F1} m below (a roof, a balcony, another room)", z - below);
            return false;
        }

        // The inner point: as far in as InnerLongest that is on the building's floor.
        double inS = -1;
        if (geo != null)
        {
            for (double s = InnerLongest; s >= InnerShortest - 1e-9; s -= 0.25)
                if (geo.OnFloorOfAny(buildingCells, x - nx * s, y - ny * s, floor)) { inS = s; break; }
            if (inS < 0) { why = "no floor a step inside it"; return false; }
        }
        else inS = 1.0;

        door = new OutdoorHunt.Door(cell,
            Inner: new Vec(x - nx * inS, y - ny * inS, floor, true),
            At: new Vec(x, y, z, true),
            Outside: new Vec(x + nx * OutsideMeters, y + ny * OutsideMeters, z, true));
        why = "";
        return true;
    }
}
