// DungeonGeometry: the openings, floors and walls of a dungeon landblock, for laying routes.
//
// DungeonPathfinder walks the EnvCell portal graph. A graph edge says two cells share an
// opening; this class says where that opening is, how wide it is and which way it faces,
// from the portal polygons of the dungeon map geometry DungeonLOS already builds (render
// polygons with the portal flag, one in each of the two cells, at the same spot). With that:
//
//   - a NARROW opening (a doorway, a hallway mouth, the joint between two corridor pieces)
//     gets an approach point centred in front of it and an exit point centred beyond it, on
//     its centre line, so the character lines up and goes through straight;
//   - a WIDE opening (8 m or more: a whole 10 m cell side, or the diagonal of one) is no
//     opening at all to a player: it joins two cells of one big room. DungeonPathfinder
//     merges those cells into one room and crosses it directly.
//
// Floors (physics floor polygons) and walls (upright render polygons) say whether a straight
// line across a room stays on its floor and clear of its walls (an L-shaped room, a pillar).
//
// World units throughout (metres; the nav code calls them yards): x east, y north, z up.
// Pure data and maths; Build takes plain polygon lists, so tests feed it fixtures.

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

/// <summary>One opening between two cells.</summary>
internal sealed class DungeonDoorway
{
    public uint   CellA, CellB;
    /// <summary>Centre of the opening at floor level (world units; z = its lowest vertex).</summary>
    public double X, Y, Z;
    /// <summary>Horizontal unit normal, pointing from CellA into CellB.</summary>
    public double Nx, Ny;
    /// <summary>Horizontal width of the opening.</summary>
    public double Width;

    public bool IsWide => Width >= DungeonGeometry.WideOpeningMeters;

    /// <summary>The normal pointing out of <paramref name="fromCell"/> through the opening.</summary>
    public (double Nx, double Ny) NormalFrom(uint fromCell) => fromCell == CellA ? (Nx, Ny) : (-Nx, -Ny);
}

/// <summary>What a placed object is to a body on the floor it stands on (DungeonGeometry's placed objects).</summary>
internal enum PlacedClass
{
    /// <summary>In the body band, and nothing else here: walked round.</summary>
    Obstacle,
    /// <summary>Its top no more than the step-up (0.60 m) over its floor, with somewhere to stand on it.</summary>
    StepOver,
    /// <summary>Walkable faces climbed onto from its floor reaching 1 m and more (stairs, ramps, platforms), or a slab that is a floor the map lacks.</summary>
    Connector,
    /// <summary>All of it under the band (under its floor + 0.45 m).</summary>
    Low,
    /// <summary>All of it over the band (over its floor + 1.83 m).</summary>
    Overhead,
    /// <summary>Nothing of it in the band, though it reaches both under and over it (an arch's beam with no posts).</summary>
    Clear,
}

/// <summary>One placed object as the map took it: model, cell, class, whether it went on the map, world box, its floor (L),
/// whether that floor is the map's, its footprint at L (m2), and where it came from.</summary>
internal readonly record struct PlacedInfo(uint Setup, uint Cell, PlacedClass Class, bool OnMap,
                                           double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ,
                                           double Floor, bool FloorFound, double FootprintArea, string Source);

/// <summary>Which placed objects go on the map, and the tag their layer gets.</summary>
internal sealed record PlacedObjectOptions
{
    /// <summary>Null: obstacles, step-overs and connectors (whatever is in the body band at its floor).</summary>
    public Func<PlacedInfo, bool>? Keep { get; init; }
    /// <summary>Where the objects came from ("dats" for the base layer by default).</summary>
    public string? Source { get; init; }
}

internal sealed class DungeonGeometry
{
    /// <summary>An opening at least this wide joins two cells of one room (a 10 m cell side is
    /// open). Corridor and doorway openings in AC dungeons are about 5 m; 0x6346 has 5.3 m
    /// (568 pairs), 7 m (2), 10 m (60) and 14 m (35).</summary>
    public const double WideOpeningMeters = 8.0;

    /// <summary>Portal polygons in two cells are the same opening when their centres are this close.</summary>
    private const double PairTolerance = 1.5;

    // A floor counts under a point when it is at most this far above / below the given height.
    private const double FloorAbove = 1.0, FloorBelow = 4.0;

    // Walls that matter for walking: they reach into this band above the floor. Lower lips
    // and kerbs are what snags at doorways, so the band starts low.
    private const double WallBandLow = 0.25, WallBandHigh = 1.8;

    private readonly Dictionary<ulong, DungeonDoorway> _doorways = new();
    private readonly Dictionary<uint, List<Vector3[]>> _floors = new();
    private readonly Dictionary<uint, List<WallSeg>> _walls = new();

    // Every floor polygon and wall of the landblock, bucketed on a grid, for clearance.
    private readonly List<FloorPoly> _floorAll = new();
    private readonly Dictionary<long, List<int>> _floorGrid = new();
    private readonly List<WallSeg> _wallAll = new();
    private readonly Dictionary<long, List<int>> _wallGrid = new();

    private readonly struct WallSeg
    {
        public readonly double X0, Y0, X1, Y1, ZMin, ZMax;
        public readonly Vector3[]? Poly;   // the wall polygon, for its height at a point along it
        public WallSeg(double x0, double y0, double x1, double y1, double zMin, double zMax, Vector3[]? poly = null)
        { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; ZMin = zMin; ZMax = zMax; Poly = poly; }

        /// <summary>The wall's height range straight above (x, y), a point on its footprint: a
        /// wall over a ramp, or the face above an arch, doesn't reach down to the floor all along.</summary>
        public (double Lo, double Hi) ZRangeAt(double x, double y)
        {
            if (Poly == null) return (ZMin, ZMax);
            double ux = X1 - X0, uy = Y1 - Y0, len = Math.Sqrt(ux * ux + uy * uy);
            if (len < 1e-9) return (ZMin, ZMax);
            ux /= len; uy /= len;
            double s = (x - X0) * ux + (y - Y0) * uy;
            double lo = double.MaxValue, hi = double.MinValue;
            int n = Poly.Length;
            for (int i = 0; i < n; i++)
            {
                var a = Poly[i]; var b = Poly[(i + 1) % n];
                double sa = (a.X - X0) * ux + (a.Y - Y0) * uy, sb = (b.X - X0) * ux + (b.Y - Y0) * uy;
                if ((s < Math.Min(sa, sb) - 1e-6) || (s > Math.Max(sa, sb) + 1e-6)) continue;
                double z;
                if (Math.Abs(sb - sa) < 1e-9) { lo = Math.Min(lo, Math.Min(a.Z, b.Z)); hi = Math.Max(hi, Math.Max(a.Z, b.Z)); continue; }
                z = a.Z + (b.Z - a.Z) * (s - sa) / (sb - sa);
                lo = Math.Min(lo, z); hi = Math.Max(hi, z);
            }
            return lo <= hi ? (lo, hi) : (ZMin, ZMax);
        }
    }

    public int DoorwayCount => _doorways.Count;
    public int FloorCellCount => _floors.Count;

    // ── Building ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the geometry for a cell graph. <paramref name="portalPolys"/> are the polygons
    /// flagged as portals (render geometry), <paramref name="floorPolys"/> the upward-facing
    /// physics floor polygons, <paramref name="wallPolys"/> the other render polygons (only
    /// the upright ones are used). Any list may be empty: missing data just means fewer
    /// doorways (the pathfinder falls back to cell-centre midpoints) or no floor/wall checks.
    /// </summary>
    public static DungeonGeometry Build(
        IReadOnlyDictionary<uint, DungeonNavNode> graph,
        IEnumerable<(uint CellId, Vector3[] Verts)> portalPolys,
        IEnumerable<(uint CellId, Vector3[] Verts)> floorPolys,
        IEnumerable<(uint CellId, Vector3[] Verts)> wallPolys)
    {
        var g = new DungeonGeometry();

        foreach (var (cell, v) in floorPolys)
        {
            if (v == null || v.Length < 3) continue;
            if (!IsUpwardFloor(v)) continue;   // the "floor" list holds ceilings too
            if (!g._floors.TryGetValue(cell, out var list)) g._floors[cell] = list = new List<Vector3[]>();
            list.Add(v);
            g.AddFloorToGrid(v, cell);
        }

        foreach (var (cell, v) in wallPolys)
        {
            if (v == null || v.Length < 3) continue;
            if (!TryWallFootprint(v, out var seg)) continue;
            if (!g._walls.TryGetValue(cell, out var list)) g._walls[cell] = list = new List<WallSeg>();
            list.Add(seg);
            g.AddWallToGrid(seg);
        }

        // Portal polygons per cell: centre, horizontal normal, width.
        var portals = new Dictionary<uint, List<PortalPoly>>();
        foreach (var (cell, v) in portalPolys)
        {
            if (v == null || v.Length < 3) continue;
            if (!TryPortalPoly(v, out var pp)) continue;
            if (!portals.TryGetValue(cell, out var list)) portals[cell] = list = new List<PortalPoly>();
            list.Add(pp);
        }

        foreach (var node in graph.Values)
        {
            foreach (uint nb in node.Neighbors)
            {
                if (nb <= node.CellId) continue;                 // each edge once
                if (!graph.TryGetValue(nb, out var other)) continue;
                if (!portals.TryGetValue(node.CellId, out var aList) || !portals.TryGetValue(nb, out var bList)) continue;
                if (TryMatch(g, node, other, aList, bList, out var door))
                    g._doorways[EdgeKey(node.CellId, nb)] = door;
            }
        }
        return g;
    }

    private readonly struct PortalPoly
    {
        public readonly double X, Y, Z, CentreZ, Nx, Ny, Width;
        public PortalPoly(double x, double y, double z, double cz, double nx, double ny, double w)
        { X = x; Y = y; Z = z; CentreZ = cz; Nx = nx; Ny = ny; Width = w; }
    }

    private static bool TryPortalPoly(Vector3[] v, out PortalPoly pp)
    {
        pp = default;
        NewellNormal(v, out double nx, out double ny, out double nz);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-6) return false;
        double hx = nx / len, hy = ny / len;
        double h = Math.Sqrt(hx * hx + hy * hy);
        // A floor or ceiling portal (a shaft) has no horizontal facing: not a doorway.
        if (h < 0.5) return false;
        hx /= h; hy /= h;

        // Width along the opening (perpendicular to the normal, horizontal); centre at the
        // middle of that span, the mean along the normal, at the lowest vertex.
        double px = -hy, py = hx;
        double minS = double.MaxValue, maxS = double.MinValue, sumN = 0, minZ = double.MaxValue, sumZ = 0;
        foreach (var p in v)
        {
            double s = p.X * px + p.Y * py;
            if (s < minS) minS = s;
            if (s > maxS) maxS = s;
            sumN += p.X * hx + p.Y * hy;
            if (p.Z < minZ) minZ = p.Z;
            sumZ += p.Z;
        }
        double midS = (minS + maxS) * 0.5, meanN = sumN / v.Length;
        pp = new PortalPoly(px * midS + hx * meanN, py * midS + hy * meanN, minZ, sumZ / v.Length, hx, hy, maxS - minS);
        return true;
    }

    /// <summary>
    /// One portal polygon on its own (a building's door to the outside has no twin in another
    /// cell): its centre at floor level (z = its lowest vertex), its horizontal facing (either way
    /// round), its width and its top. False for a floor or ceiling portal (a shaft).
    /// </summary>
    internal static bool TryOpening(Vector3[] v, out double x, out double y, out double z,
                                    out double nx, out double ny, out double width, out double top)
    {
        x = y = z = nx = ny = width = top = 0;
        if (v == null || v.Length < 3 || !TryPortalPoly(v, out var pp)) return false;
        x = pp.X; y = pp.Y; z = pp.Z; nx = pp.Nx; ny = pp.Ny; width = pp.Width;
        top = double.MinValue;
        foreach (var p in v) top = Math.Max(top, p.Z);
        return true;
    }

    private static bool TryMatch(DungeonGeometry g, DungeonNavNode a, DungeonNavNode b,
                                 List<PortalPoly> aList, List<PortalPoly> bList, out DungeonDoorway door)
    {
        door = null!;
        double ax = NavToWorld(a.EW), ay = NavToWorld(a.NS), bx = NavToWorld(b.EW), by = NavToWorld(b.NS);
        double midX = (ax + bx) * 0.5, midY = (ay + by) * 0.5;
        double best = double.MaxValue;
        PortalPoly hit = default;
        bool found = false;
        foreach (var pa in aList)
        {
            foreach (var pb in bList)
            {
                double dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.CentreZ - pb.CentreZ;
                if (dx * dx + dy * dy + dz * dz > PairTolerance * PairTolerance) continue;
                // If two cells share more than one opening, take the one nearest the line between them.
                double dm = (pa.X - midX) * (pa.X - midX) + (pa.Y - midY) * (pa.Y - midY);
                if (dm < best) { best = dm; hit = pa; found = true; }
            }
        }
        if (!found) return false;

        double nx = hit.Nx, ny = hit.Ny;
        // Orient A -> B. The floors decide when they can (a cell's origin isn't always its
        // middle): a step back from the opening must be on A's floor, a step forward on B's.
        int vote = 0;
        if (g.HasFloor(a.CellId)) vote += g.OnCellFloor(a.CellId, hit.X - nx, hit.Y - ny, hit.Z) ? 1 : g.OnCellFloor(a.CellId, hit.X + nx, hit.Y + ny, hit.Z) ? -1 : 0;
        if (g.HasFloor(b.CellId)) vote += g.OnCellFloor(b.CellId, hit.X + nx, hit.Y + ny, hit.Z) ? 1 : g.OnCellFloor(b.CellId, hit.X - nx, hit.Y - ny, hit.Z) ? -1 : 0;
        if (vote == 0) vote = (bx - ax) * nx + (by - ay) * ny >= 0 ? 1 : -1;   // cell centres
        if (vote < 0) { nx = -nx; ny = -ny; }

        door = new DungeonDoorway
        {
            CellA = a.CellId, CellB = b.CellId,
            X = hit.X, Y = hit.Y, Z = hit.Z,
            Nx = nx, Ny = ny,
            Width = hit.Width,
        };
        return true;
    }

    private static bool TryWallFootprint(Vector3[] v, out WallSeg seg)
    {
        seg = default;
        NewellNormal(v, out double nx, out double ny, out double nz);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-6) return false;
        if (Math.Abs(nz) / len > 0.5) return false;         // floor / ceiling / ramp, not a wall
        double hx = nx, hy = ny, h = Math.Sqrt(hx * hx + hy * hy);
        if (h < 1e-9) return false;
        double px = -hy / h, py = hx / h;                     // along the wall
        double minS = double.MaxValue, maxS = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        double sx0 = 0, sy0 = 0, sx1 = 0, sy1 = 0;
        foreach (var p in v)
        {
            double s = p.X * px + p.Y * py;
            if (s < minS) { minS = s; sx0 = p.X; sy0 = p.Y; }
            if (s > maxS) { maxS = s; sx1 = p.X; sy1 = p.Y; }
            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;
        }
        if (maxS - minS < 0.05) return false;
        seg = new WallSeg(sx0, sy0, sx1, sy1, minZ, maxZ, v);
        return true;
    }

    /// <summary>
    /// A polygon facing up (counter-clockwise seen from above). DungeonLOS's floor polygon list
    /// keeps every near-horizontal physics polygon, ceilings included: at 0x6346 the corridor
    /// ceilings 3.8 m up are in it, and some cells have only their ceiling there.
    /// </summary>
    internal static bool IsUpwardFloor(Vector3[] v)
    {
        NewellNormal(v, out double nx, out double ny, out double nz);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return len > 1e-6 && nz / len >= 0.4;
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    public bool TryGetDoorway(uint a, uint b, out DungeonDoorway door) => _doorways.TryGetValue(EdgeKey(a, b), out door!);

    /// <summary>Every opening paired between two cells (the jump-downs look at them all: a closed ledge is gone from the graph, not from here).</summary>
    internal IEnumerable<DungeonDoorway> AllDoorways => _doorways.Values;

    /// <summary>Openings whose centre is within r (horizontally) of (x, y) and <paramref name="dz"/> of height z.</summary>
    public IEnumerable<DungeonDoorway> DoorwaysNear(double x, double y, double z, double r, double dz = 2.0)
    {
        foreach (var d in _doorways.Values)
            if ((d.X - x) * (d.X - x) + (d.Y - y) * (d.Y - y) <= r * r && Math.Abs(d.Z - z) < dz) yield return d;
    }

    public bool HasFloor(uint cell) => _floors.ContainsKey(cell);

    /// <summary>True when (x, y) is on one of the cell's floor polygons, the floor at most 1 m
    /// above and 4 m below z. False when the cell has no floor data.</summary>
    public bool OnCellFloor(uint cell, double x, double y, double z)
    {
        if (!_floors.TryGetValue(cell, out var list)) return false;
        foreach (var v in list)
        {
            if (!InsidePolygon2D(v, x, y)) continue;
            double fz = FloorZAt(v);
            double dz = z - fz;
            if (dz >= -FloorAbove && dz <= FloorBelow) return true;
        }
        return false;
    }

    /// <summary>The height of the cell's floor at (x, y), the floor polygon nearest
    /// <paramref name="zRef"/> in height if several are under it (any height apart). False when
    /// (x, y) is off the cell's floor or the cell has no floor data.</summary>
    internal bool TryCellFloorHeight(uint cell, double x, double y, double zRef, out double h)
    {
        h = 0;
        if (!_floors.TryGetValue(cell, out var list)) return false;
        double best = double.MaxValue;
        foreach (var v in list)
        {
            if (!InsidePolygon2D(v, x, y)) continue;
            NewellNormal(v, out double nx, out double ny, out double nz);
            if (Math.Abs(nz) < 1e-9) continue;
            double px = 0, py = 0, pz = 0;
            foreach (var q in v) { px += q.X; py += q.Y; pz += q.Z; }
            px /= v.Length; py /= v.Length; pz /= v.Length;
            double fh = pz - (nx * (x - px) + ny * (y - py)) / nz;
            if (Math.Abs(fh - zRef) < best) { best = Math.Abs(fh - zRef); h = fh; }
        }
        return best < double.MaxValue;
    }

    /// <summary>The box round the cell's floor polygons (false: no floor data).</summary>
    internal bool TryCellFloorBounds(uint cell, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
        if (!_floors.TryGetValue(cell, out var list)) return false;
        foreach (var v in list)
            foreach (var q in v)
            {
                minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
            }
        return minX <= maxX;
    }

    /// <summary>True when (x, y) is on the floor of any of <paramref name="cells"/>; also true
    /// when none of them has floor data (nothing to check against).</summary>
    public bool OnFloorOfAny(IEnumerable<uint> cells, double x, double y, double z)
    {
        bool anyFloor = false;
        foreach (uint c in cells)
        {
            if (!_floors.ContainsKey(c)) continue;
            anyFloor = true;
            if (OnCellFloor(c, x, y, z)) return true;
        }
        return !anyFloor;
    }

    /// <summary>
    /// Can a character walk the straight line (x0, y0) to (x1, y1) at floor height z, inside
    /// <paramref name="cells"/> (one room)? The line and two lines <paramref name="clearance"/>
    /// to either side must stay on the room's floor (sampled every half metre) and cross none of
    /// its walls that reach into knee-to-head height.
    /// </summary>
    public bool SegmentClear(IReadOnlyCollection<uint> cells, double x0, double y0, double z0,
                             double x1, double y1, double z1, double clearance = 0.5)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return true;
        double ox = -dy / len * clearance, oy = dx / len * clearance;

        // Floor: centre line and both sides.
        bool checkFloor = false;
        foreach (uint c in cells) if (_floors.ContainsKey(c)) { checkFloor = true; break; }
        if (checkFloor)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                double x = x0 + dx * t, y = y0 + dy * t, z = z0 + (z1 - z0) * t;
                if (!OnFloorOfAny(cells, x, y, z)) return false;
                if (!OnFloorOfAny(cells, x + ox, y + oy, z)) return false;
                if (!OnFloorOfAny(cells, x - ox, y - oy, z)) return false;
            }
        }

        // Walls (the cells' own and the placed objects' filed under them; an overlay's too).
        double zLo = Math.Min(z0, z1) + WallBandLow, zHi = Math.Max(z0, z1) + WallBandHigh;
        bool Crossed(Dictionary<uint, List<WallSeg>> byCell)
        {
            foreach (uint c in cells)
            {
                if (!byCell.TryGetValue(c, out var list)) continue;
                foreach (var w in list)
                {
                    if (w.ZMax < zLo || w.ZMin > zHi) continue;
                    if (SegmentsCross(x0, y0, x1, y1, w.X0, w.Y0, w.X1, w.Y1)) return true;
                    if (SegmentsCross(x0 + ox, y0 + oy, x1 + ox, y1 + oy, w.X0, w.Y0, w.X1, w.Y1)) return true;
                    if (SegmentsCross(x0 - ox, y0 - oy, x1 - ox, y1 - oy, w.X0, w.Y0, w.X1, w.Y1)) return true;
                }
            }
            return false;
        }
        if (Crossed(_walls)) return false;
        return _placedOverlay == null || !Crossed(_placedOverlay.CellWalls);
    }

    // ── Clearance from walls ────────────────────────────────────────────────
    //
    // How far a character standing at a point is from anything it can't walk into: an upright
    // wall reaching above step height, or the edge of the walkable floor (a drop, or a slope
    // too steep to stand on, like the raised sides of the Olthoi hive's corridors). Measured
    // over the whole landblock, not one cell: a point beside a doorway is close to the next
    // cell's walls too. Probed along 16 directions in 0.1 m steps, following the floor up and
    // down ramps, plus the exact distance to the nearest wall.

    /// <summary>AC's walkable slope limit (PhysicsGlobals.FloorZ = 0.66417, about 48 degrees):
    /// a steeper "floor" polygon is a slope the character slides off, so it bounds the floor.</summary>
    public const double WalkableNz = 0.664;

    private const double GridCell = 4.0;
    private const int ProbeDirs = 16;
    private const double ProbeStep = 0.1;
    // Height change allowed between two probe steps 0.1 m apart: a 48 degree slope rises 0.11,
    // a stair 0.25; more is a ledge.
    private const double ProbeMaxRise = 0.3;
    // A wall counts for clearance when it reaches into this band above the floor: lower lips
    // are stepped over (the human Setup's step-up height is 0.45; see PatrolProof "human").
    private const double ClearWallLow = 0.45, ClearWallHigh = 1.8;

    private sealed class FloorPoly
    {
        public Vector3[] V = null!;
        public double Nx, Ny, Nz, Px, Py, Pz;     // unit normal and a point on the plane
        public double MinX, MinY, MaxX, MaxY;
        public bool Walkable;
        public uint Cell;                          // the cell it is the floor of (a placed object's walls go to it too)
        public double HeightAt(double x, double y) => Pz - (Nx * (x - Px) + Ny * (y - Py)) / Nz;
    }

    private static long GridKey(int gx, int gy) => ((long)gx << 32) ^ (uint)gy;
    private static int GridOf(double v) => (int)Math.Floor(v / GridCell);

    private void AddFloorToGrid(Vector3[] v, uint cell)
    {
        NewellNormal(v, out double nx, out double ny, out double nz);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-9 || nz <= 1e-9) return;
        var f = new FloorPoly { V = v, Cell = cell, Nx = nx / len, Ny = ny / len, Nz = nz / len, MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue };
        double sx = 0, sy = 0, sz = 0;
        foreach (var p in v)
        {
            sx += p.X; sy += p.Y; sz += p.Z;
            f.MinX = Math.Min(f.MinX, p.X); f.MaxX = Math.Max(f.MaxX, p.X);
            f.MinY = Math.Min(f.MinY, p.Y); f.MaxY = Math.Max(f.MaxY, p.Y);
        }
        f.Px = sx / v.Length; f.Py = sy / v.Length; f.Pz = sz / v.Length;
        f.Walkable = f.Nz >= WalkableNz;
        int idx = _floorAll.Count;
        _floorAll.Add(f);
        for (int gx = GridOf(f.MinX); gx <= GridOf(f.MaxX); gx++)
            for (int gy = GridOf(f.MinY); gy <= GridOf(f.MaxY); gy++)
            {
                long k = GridKey(gx, gy);
                if (!_floorGrid.TryGetValue(k, out var list)) _floorGrid[k] = list = new List<int>();
                list.Add(idx);
            }
    }

    private void AddWallToGrid(WallSeg w) => AddWallTo(_wallAll, _wallGrid, w);

    private static void AddWallTo(List<WallSeg> all, Dictionary<long, List<int>> grid, WallSeg w)
    {
        int idx = all.Count;
        all.Add(w);
        for (int gx = GridOf(Math.Min(w.X0, w.X1)); gx <= GridOf(Math.Max(w.X0, w.X1)); gx++)
            for (int gy = GridOf(Math.Min(w.Y0, w.Y1)); gy <= GridOf(Math.Max(w.Y0, w.Y1)); gy++)
            {
                long k = GridKey(gx, gy);
                if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>();
                list.Add(idx);
            }
    }

    /// <summary>The floor under (x, y) nearest in height to zRef, at most <paramref name="up"/>
    /// above and <paramref name="down"/> below it, of any cell. <paramref name="walkable"/>:
    /// flat enough to stand on.</summary>
    private bool FloorUnder(double x, double y, double zRef, double up, double down, out double h, out bool walkable)
    {
        if (FloorUnderExact(x, y, zRef, up, down, out h, out walkable)) return true;
        // A point exactly on the seam between two polygons can fall outside both.
        return FloorUnderExact(x + 1.3e-3, y + 0.7e-3, zRef, up, down, out h, out walkable);
    }

    private bool FloorUnderExact(double x, double y, double zRef, double up, double down, out double h, out bool walkable)
    {
        h = 0; walkable = false;
        if (!_floorGrid.TryGetValue(GridKey(GridOf(x), GridOf(y)), out var list)) return false;
        double best = double.MaxValue;
        foreach (int i in list)
        {
            var f = _floorAll[i];
            if (x < f.MinX || x > f.MaxX || y < f.MinY || y > f.MaxY) continue;
            if (!InsidePolygon2D(f.V, x, y)) continue;
            double fh = f.HeightAt(x, y), dz = fh - zRef;
            if (dz > up || dz < -down) continue;
            if (Math.Abs(dz) < best) { best = Math.Abs(dz); h = fh; walkable = f.Walkable; }
        }
        return best < double.MaxValue;
    }

    /// <summary>True when (x, y) stands on walkable floor (any cell) about height z.</summary>
    public bool OnWalkableFloor(double x, double y, double z)
        => FloorUnder(x, y, z, FloorAbove, FloorBelow, out _, out bool w) && w;

    /// <summary>
    /// How far (metres, up to <paramref name="maxR"/>) a character at (x, y), floor height about
    /// z, is from the nearest wall or edge of the walkable floor. 0 when the point is off the
    /// floor (in or behind a wall, on a slope too steep to stand on). Where the landblock has no
    /// floor data near the point, only walls are measured.
    /// </summary>
    public double Clearance(double x, double y, double z, double maxR = 3.0)
    {
        bool floorKnown = FloorUnder(x, y, z, FloorAbove, FloorBelow, out double h0, out bool walk);
        if (floorKnown && !walk) return 0;
        if (!floorKnown)
        {
            if (FloorNear(x, y, z)) return 0;   // past the floor's edge: in or behind a wall
            h0 = z;
        }
        if (InsidePlaced(x, y, h0)) return 0;   // inside an altar or a statue: its middle is far from its sides
        double best = maxR;
        for (int k = 0; k < ProbeDirs && best > 0; k++)
        {
            double a = k * (2 * Math.PI / ProbeDirs);
            best = Math.Min(best, FreeRun(x, y, h0, Math.Cos(a), Math.Sin(a), best, floorKnown));
        }
        return Math.Min(best, WallDistance(x, y, h0 + ClearWallLow, h0 + ClearWallHigh, best));
    }

    // ── Placed objects (2026-11-18) ─────────────────────────────────────────
    //
    // What a dungeon's cells place (an altar, a statue, a pillar: the EnvCells' stabs) isn't in the cells'
    // walls and floors, so walks went through them. Thread 0x000A ("It also still doesn't see world
    // objects such as this altar it tried to run through"): an explore leg ran straight through a 1 m altar
    // in a hall, the character stuck there in a fight and died, four times in one run. Thread 0x00C1: a
    // room's middle (a look-in point) inside a 9 x 9 m statue.
    //
    // Each object is a solid model (PlacedSolid: 0.1 m columns, each with the heights where it is solid),
    // measured against a body: from the feet + PlacedBandLow (the map's step) to the top of the human setup's
    // upper sphere (PlacedBandHigh, 1.83 m). At the floor it stands on (L) that gives its footprint. The
    // footprint's outline is walls from L + 0.45 to L + 1.83 (within the object's own height): every clearance
    // measure sees them, so the corner put into a leg brushing a wall, the straightener, the walk round a hole
    // and the stuck detours go round. They are filed under every cell whose floor the footprint covers, so a
    // straight line over the next cell sees an object reaching into it. A point is
    // inside an object when, for a walker on the floor there, something of it is between that floor + 0.45 and
    // + 1.83: a table's top counts all over (not only at its legs), a rail at 0.8 m closes, an arch's beam
    // overhead and a block whose top is the floor walked on (a door sill's) don't, nor does anything on a floor
    // 4 m up or down. An open plane (a fence, a web) is its line, never a filled area.
    //
    // Classes, at L: LOW (all of it under the band), OVERHEAD (all of it over), CLEAR (nothing of it in the
    // band either way: an arch with no posts), STEP-OVER (top no more than the step-up, 0.60 m, over L, with a
    // top to stand on), CONNECTOR (walkable faces a body could climb onto, 0.6 m at most at a time, from L and
    // across other objects, reaching 1 m and more over L: stairs, ramps, platforms; or a slab that is a floor
    // the map lacks), OBSTACLE (the rest). Only what is in the band at L goes on the map. Step-overs and
    // connectors still do in this wave (the 0.60 m step-up isn't proven in game; taking a ramp's sides out was
    // measured wrong in 0x0174), but they are tagged, for a map that leaves them out (AddPlacedObjects' options).
    //
    // The dats' objects sit in the base layer. A second, replaceable layer (objects found at run time, tagged
    // with where they came from) joins it through SetPlacedOverlay and leaves through ClearPlacedOverlay, with
    // walls of its own, without the base being built again.

    /// <summary>The body a placed object is measured against, over the floor a walker stands on: from the map's
    /// own step (ClearWallLow) to the top of the human setup 0x02000001's upper sphere (1.35 + 0.48).</summary>
    internal const double PlacedBandLow = 0.45, PlacedBandHigh = 1.83;
    /// <summary>The human setup's step-up height: an object no taller over its floor, with a top to stand on, is a step-over.</summary>
    internal const double StepOverTop = 0.60;
    // Connector chain: a step up at most this high between walkable faces at most this far apart (a step down
    // any small drop), reaching at least this high over the object's floor.
    private const double ConnectorRise = 0.6, ConnectorGap = 0.3, ConnectorDrop = 1.5, ConnectorReach = 1.0;
    // A slab: walkable faces this big (m2), raised this far over its floor (or no floor under it), and the map
    // has no floor within SlabFloorNear of most of them.
    private const double SlabArea = 1.0, SlabRaised = 1.0, SlabFloorNear = 0.3;
    // The floor an object stands on is looked for this far over and under its lowest point.
    private const double PlacedFloorSearchUp = 2.0, PlacedFloorSearchDown = 3.0;
    // A cell's floor this near an object's floor, under its footprint, gets the object's walls too.
    private const double PlacedCellFloorBand = 2.0;

    private sealed class PlacedObj
    {
        public uint Setup, Cell;
        public PlacedShape Shape = null!;
        public PlacedBand Band = null!;
        public double Floor, ReachHigh = double.NegativeInfinity;
        public bool FloorFound, OnMap;
        public PlacedClass Class;
        public string Source = "";

        public PlacedInfo Info => new(Setup, Cell, Class, OnMap, Shape.MinX, Shape.MinY, Shape.MinZ, Shape.MaxX, Shape.MaxY, Shape.MaxZ,
                                      Floor, FloorFound, Band.Area, Source);
    }

    // One list of placed objects with its grid. The base layer's walls are the map's walls; an overlay keeps its own.
    private sealed class PlacedLayer
    {
        public readonly string Source;
        public readonly bool OwnWalls;
        public readonly List<PlacedObj> All = new();
        public readonly List<PlacedObj> OnMap = new();
        public readonly Dictionary<long, List<int>> Grid = new();
        public readonly List<WallSeg> Walls = new();
        public readonly Dictionary<long, List<int>> WallGrid = new();
        public readonly Dictionary<uint, List<WallSeg>> CellWalls = new();
        public PlacedLayer(string source, bool ownWalls) { Source = source; OwnWalls = ownWalls; }
    }

    private readonly PlacedLayer _placedBase = new("dats", ownWalls: false);
    private PlacedLayer? _placedOverlay;

    /// <summary>How many placed objects stand in the way at body height (on the map, both layers).</summary>
    public int PlacedCount => _placedBase.OnMap.Count + (_placedOverlay?.OnMap.Count ?? 0);

    /// <summary>Every placed object given to the map (both layers), with its class, whether it went on the map, its box and floor.</summary>
    internal IReadOnlyList<PlacedInfo> PlacedObjects
    {
        get
        {
            var list = new List<PlacedInfo>(_placedBase.All.Count + (_placedOverlay?.All.Count ?? 0));
            foreach (var layer in PlacedLayers())
                foreach (var p in layer.All) list.Add(p.Info);
            return list;
        }
    }

    /// <summary>
    /// Adds the objects the cells place, each as its triangles in world metres (three vertices a triangle, closed or
    /// open), to the base layer: each one's footprint in the body band at its floor becomes walls, and inside it is no
    /// room. What isn't in the band there (or what <paramref name="options"/> leaves out) goes on the list only.
    /// Returns how many went on the map.
    /// </summary>
    internal int AddPlacedObjects(IEnumerable<(uint SetupId, uint CellId, Vector3[] Triangles)> objects, PlacedObjectOptions? options = null)
        => AddPlacedTo(_placedBase, objects, options);

    /// <summary>
    /// Replaces the overlay layer (objects found at run time) with <paramref name="objects"/>, classified and walled
    /// the same way, its walls kept apart from the map's so it can go again. Returns how many went on the map.
    /// </summary>
    internal int SetPlacedOverlay(IEnumerable<(uint SetupId, uint CellId, Vector3[] Triangles)> objects, PlacedObjectOptions? options = null)
    {
        var layer = new PlacedLayer(options?.Source ?? "overlay", ownWalls: true);
        int n = AddPlacedTo(layer, objects, options);
        _placedOverlay = layer;
        InvalidateClearance();
        return n;
    }

    /// <summary>Drops the overlay layer: the map is the cells and the dats' objects again.</summary>
    internal void ClearPlacedOverlay()
    {
        if (_placedOverlay == null) return;
        _placedOverlay = null;
        InvalidateClearance();
    }

    /// <summary>Forgets every clearance worked out so far (after the map's walls or objects change).</summary>
    internal void InvalidateClearance() => _clearanceMemo.Clear();

    private static bool KeepByDefault(PlacedInfo p) => p.Class is PlacedClass.Obstacle or PlacedClass.StepOver or PlacedClass.Connector;

    private int AddPlacedTo(PlacedLayer layer, IEnumerable<(uint SetupId, uint CellId, Vector3[] Triangles)> objects, PlacedObjectOptions? options)
    {
        var keep = options?.Keep ?? KeepByDefault;
        string source = options?.Source ?? layer.Source;
        var batch = new List<PlacedObj>();
        foreach (var (setup, cell, tris) in objects)
        {
            var shape = PlacedSolid.Place(setup, tris);
            if (shape == null) continue;
            // The floor it stands on: under its middle, near its lowest point (none there: its lowest point).
            // (A statue's mesh can reach 1 m and more under the floor: Thread 0x00C1's 020014A3 1.1 m.)
            bool found = FloorUnder((shape.MinX + shape.MaxX) / 2, (shape.MinY + shape.MaxY) / 2, shape.MinZ, PlacedFloorSearchUp, PlacedFloorSearchDown, out double f, out _);
            double floor = found ? f : shape.MinZ;
            batch.Add(new PlacedObj
            {
                Setup = setup, Cell = cell, Shape = shape, Floor = floor, FloorFound = found, Source = source,
                Band = shape.Band(floor + PlacedBandLow, floor + PlacedBandHigh),
            });
        }
        ReachUp(batch);
        int added = 0;
        foreach (var p in batch)
        {
            p.Class = Classify(p);
            layer.All.Add(p);
            if (p.Band.Empty || !keep(p.Info)) continue;
            p.OnMap = true;
            PutOnMap(layer, p);
            added++;
        }
        if (added > 0) InvalidateClearance();
        return added;
    }

    private PlacedClass Classify(PlacedObj p)
    {
        double top = p.Shape.MaxZ - p.Floor, bottom = p.Shape.MinZ - p.Floor;
        if (p.Band.Empty)
        {
            if (IsSlab(p, bottom)) return PlacedClass.Connector;
            return top < PlacedBandLow ? PlacedClass.Low : bottom > PlacedBandHigh ? PlacedClass.Overhead : PlacedClass.Clear;
        }
        if (p.ReachHigh >= ConnectorReach) return PlacedClass.Connector;
        if (top <= StepOverTop && p.Shape.Model.WalkableTop) return PlacedClass.StepOver;
        return PlacedClass.Obstacle;
    }

    // A slab that is a floor the map doesn't have (stepping stones over a pit, a raised platform's floor plate).
    private bool IsSlab(PlacedObj p, double bottom)
    {
        if (p.FloorFound && bottom <= SlabRaised) return false;
        var m = p.Shape.Model;
        double area = 0, missing = 0;
        for (int k = 0; k < m.WalkFaceCount; k++)
        {
            double a = m.WalkFaceArea(k);
            area += a;
            p.Shape.WalkFaceCentre(k, out double x, out double y, out double z);
            if (!TryFloorHeight(x, y, z, SlabFloorNear, out _)) missing += a;
        }
        return area >= SlabArea && missing >= area / 2;
    }

    // The connector chain: walkable faces reached from each object's floor, a step up at a time, across every object
    // of the batch; each object's highest reached face over its own floor.
    private static void ReachUp(List<PlacedObj> batch)
    {
        var faces = new List<(int Obj, double MinX, double MinY, double MaxX, double MaxY, double Z0, double Z1)>();
        for (int o = 0; o < batch.Count; o++)
        {
            var s = batch[o].Shape;
            for (int k = 0; k < s.Model.WalkFaceCount; k++)
            {
                s.WalkFace(k, out double x0, out double y0, out double x1, out double y1, out double z0, out double z1);
                faces.Add((o, x0, y0, x1, y1, z0, z1));
            }
        }
        if (faces.Count == 0) return;
        const double bucket = 2.0;
        static int B(double v) => (int)Math.Floor(v / bucket);
        var grid = new Dictionary<long, List<int>>();
        for (int f = 0; f < faces.Count; f++)
            for (int gx = B(faces[f].MinX); gx <= B(faces[f].MaxX); gx++)
                for (int gy = B(faces[f].MinY); gy <= B(faces[f].MaxY); gy++)
                {
                    long key = GridKey(gx, gy);
                    if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
                    l.Add(f);
                }
        var reached = new bool[faces.Count];
        var queue = new Queue<int>();
        for (int f = 0; f < faces.Count; f++)
        {
            double fl = batch[faces[f].Obj].Floor;
            if (faces[f].Z0 - fl <= ConnectorRise && faces[f].Z1 - fl >= -ConnectorRise) { reached[f] = true; queue.Enqueue(f); }
        }
        while (queue.Count > 0)
        {
            var f = faces[queue.Dequeue()];
            for (int gx = B(f.MinX - ConnectorGap); gx <= B(f.MaxX + ConnectorGap); gx++)
                for (int gy = B(f.MinY - ConnectorGap); gy <= B(f.MaxY + ConnectorGap); gy++)
                {
                    if (!grid.TryGetValue(GridKey(gx, gy), out var l)) continue;
                    foreach (int g in l)
                    {
                        if (reached[g]) continue;
                        var h = faces[g];
                        if (h.MinX > f.MaxX + ConnectorGap || h.MaxX < f.MinX - ConnectorGap || h.MinY > f.MaxY + ConnectorGap || h.MaxY < f.MinY - ConnectorGap) continue;
                        if (h.Z0 > f.Z1 + ConnectorRise || h.Z1 < f.Z0 - ConnectorDrop) continue;
                        reached[g] = true;
                        queue.Enqueue(g);
                    }
                }
        }
        for (int f = 0; f < faces.Count; f++)
        {
            if (!reached[f]) continue;
            var p = batch[faces[f].Obj];
            p.ReachHigh = Math.Max(p.ReachHigh, faces[f].Z1 - p.Floor);
        }
    }

    private void PutOnMap(PlacedLayer layer, PlacedObj p)
    {
        var s = p.Shape;
        int idx = layer.OnMap.Count;
        layer.OnMap.Add(p);
        for (int gx = GridOf(s.MinX - 0.1); gx <= GridOf(s.MaxX + 0.1); gx++)
            for (int gy = GridOf(s.MinY - 0.1); gy <= GridOf(s.MaxY + 0.1); gy++)
            {
                long key = GridKey(gx, gy);
                if (!layer.Grid.TryGetValue(key, out var list)) layer.Grid[key] = list = new List<int>();
                list.Add(idx);
            }
        var cells = CellsUnder(p);
        // The band at its floor, no higher or lower than the object itself: a walker on the floor over a block under a
        // door sill (E74E: its top the floor walked on, 1.2 m over its own) is above its sides, not beside them.
        double zLo = Math.Max(p.Floor + PlacedBandLow, s.MinZ), zHi = Math.Min(p.Floor + PlacedBandHigh, s.MaxZ);
        var byCell = layer.OwnWalls ? layer.CellWalls : _walls;
        var o = p.Band.Outline;
        for (int k = 0; k + 3 < o.Length; k += 4)
        {
            s.ToWorld(o[k], o[k + 1], out double x0, out double y0);
            s.ToWorld(o[k + 2], o[k + 3], out double x1, out double y1);
            var w = new WallSeg(x0, y0, x1, y1, zLo, zHi);
            if (layer.OwnWalls) AddWallTo(layer.Walls, layer.WallGrid, w);
            else AddWallToGrid(w);
            foreach (uint c in cells)
            {
                if (!byCell.TryGetValue(c, out var wl)) byCell[c] = wl = new List<WallSeg>();
                wl.Add(w);
            }
        }
    }

    // The object's own cell, and every cell with a floor under its footprint near its floor's height.
    private List<uint> CellsUnder(PlacedObj p)
    {
        var cells = new List<uint> { p.Cell };
        var s = p.Shape;
        var tested = new HashSet<int>();
        double[]? centres = null;
        for (int gx = GridOf(s.MinX); gx <= GridOf(s.MaxX); gx++)
            for (int gy = GridOf(s.MinY); gy <= GridOf(s.MaxY); gy++)
            {
                if (!_floorGrid.TryGetValue(GridKey(gx, gy), out var list)) continue;
                foreach (int fi in list)
                {
                    var f = _floorAll[fi];
                    if (!tested.Add(fi) || cells.Contains(f.Cell)) continue;
                    if (f.MaxX < s.MinX || f.MinX > s.MaxX || f.MaxY < s.MinY || f.MinY > s.MaxY) continue;
                    if (centres == null)
                    {
                        centres = new double[p.Band.Cells.Length * 2];
                        for (int k = 0; k < p.Band.Cells.Length; k++) s.CellCentre(p.Band.Cells[k], out centres[2 * k], out centres[2 * k + 1]);
                    }
                    for (int k = 0; k < centres.Length; k += 2)
                    {
                        double x = centres[k], y = centres[k + 1];
                        if (x < f.MinX || x > f.MaxX || y < f.MinY || y > f.MaxY || !InsidePolygon2D(f.V, x, y)) continue;
                        if (Math.Abs(f.HeightAt(x, y) - p.Floor) > PlacedCellFloorBand) continue;
                        cells.Add(f.Cell);
                        break;
                    }
                }
            }
        return cells;
    }

    /// <summary>True when a walker at (x, y), on a floor at <paramref name="floorZ"/>, is inside a placed object:
    /// something of one there between that floor + <see cref="PlacedBandLow"/> and + <see cref="PlacedBandHigh"/>.</summary>
    internal bool InsidePlaced(double x, double y, double floorZ)
        => InsideLayer(_placedBase, x, y, floorZ, null) || (_placedOverlay != null && InsideLayer(_placedOverlay, x, y, floorZ, null));

    /// <summary>InsidePlaced, counting only the objects whose class <paramref name="which"/> takes (a step-over, a connector).</summary>
    internal bool InsidePlaced(double x, double y, double floorZ, Func<PlacedClass, bool> which)
        => InsideLayer(_placedBase, x, y, floorZ, which) || (_placedOverlay != null && InsideLayer(_placedOverlay, x, y, floorZ, which));

    /// <summary>
    /// A spot at height <paramref name="z"/> (where a character stands, a recorded waypoint) is up on something placed: it
    /// reads as inside one for a walker on the map's floor under it (<paramref name="floorZ"/>), and not at its own height,
    /// which is over that object's top (a dais, a ramp, a platform). The map has no floor on an object's top, so every test
    /// from the floor under such a spot takes it for one inside the object.
    /// </summary>
    internal bool OnTopOfPlaced(double x, double y, double z, double floorZ)
        => z > floorZ && InsidePlaced(x, y, floorZ) && !InsidePlaced(x, y, z);

    private static bool InsideLayer(PlacedLayer layer, double x, double y, double h, Func<PlacedClass, bool>? which = null)
    {
        if (layer.OnMap.Count == 0 || !layer.Grid.TryGetValue(GridKey(GridOf(x), GridOf(y)), out var list)) return false;
        double lo = h + PlacedBandLow, hi = h + PlacedBandHigh;
        foreach (int i in list)
            if ((which == null || which(layer.OnMap[i].Class)) && layer.OnMap[i].Shape.SolidIn(x, y, lo, hi)) return true;
        return false;
    }

    /// <summary>The placed objects on the map near (x, y) within r, for offline inspection: model, cell, box, floor.</summary>
    internal List<(uint Setup, uint Cell, double MinX, double MinY, double MaxX, double MaxY, double Floor)> PlacedNear(double x, double y, double r)
    {
        var outList = new List<(uint, uint, double, double, double, double, double)>();
        foreach (var layer in PlacedLayers())
            foreach (var p in layer.OnMap)
            {
                var s = p.Shape;
                if (s.MaxX >= x - r && s.MinX <= x + r && s.MaxY >= y - r && s.MinY <= y + r)
                    outList.Add((p.Setup, p.Cell, s.MinX, s.MinY, s.MaxX, s.MaxY, p.Floor));
            }
        return outList;
    }

    private IEnumerable<PlacedLayer> PlacedLayers()
    {
        yield return _placedBase;
        if (_placedOverlay != null) yield return _placedOverlay;
    }

    // Clearance is asked again and again for the same spots (a patrol passes a doorway several
    // times a lap; every standoff is tried in turn): remembered to the centimetre.
    private readonly Dictionary<(long, long, long, int), double> _clearanceMemo = new();

    internal double ClearanceMemo(double x, double y, double z, double maxR)
    {
        var key = ((long)Math.Round(x * 100), (long)Math.Round(y * 100), (long)Math.Round(z * 10), (int)Math.Round(maxR * 100));
        if (_clearanceMemo.TryGetValue(key, out double c)) return c;
        if (_clearanceMemo.Count > 200_000) _clearanceMemo.Clear();
        return _clearanceMemo[key] = Clearance(x, y, z, maxR);
    }

    /// <summary>
    /// Moves (x, y) to where a character has <paramref name="want"/> clearance, at most
    /// <paramref name="maxMove"/> from where it started, staying on walkable floor (and on the
    /// floor of <paramref name="cells"/> when given): away from the walls and floor edges it is
    /// too close to, each probe direction that hits something within reach pushing the other
    /// way. Between two walls closer than 2 x want it ends on the middle line. A point off the
    /// floor (a dead end's cell origin inside its end cap) first goes to the nearest floor.
    /// Returns the clearance where it ends; the point is left alone when nothing is better.
    /// </summary>
    public double PushClear(ref double x, ref double y, double z, double want, double maxMove, IReadOnlyCollection<uint>? cells = null)
    {
        double reach = want + 0.5;
        double ox = x, oy = y;
        double c = ClearanceMemo(x, y, z, reach);
        if (c >= want) return c;

        bool Allowed(double px, double py)
            => (px - ox) * (px - ox) + (py - oy) * (py - oy) <= maxMove * maxMove + 1e-9
            && OnWalkableFloor(px, py, z)
            && (cells == null || OnFloorOfAny(cells, px, py, z));

        if (c <= 0 && !OnWalkableFloor(x, y, z))
        {
            // Off the floor: the ring nearest the point that has floor with any room, its best spot.
            bool found = false;
            for (double r = 0.1; r <= maxMove + 1e-9 && !found; r += 0.1)
            {
                double bx = 0, by = 0, bc = 0;
                for (int k = 0; k < ProbeDirs; k++)
                {
                    double a = k * (2 * Math.PI / ProbeDirs);
                    double px = ox + Math.Cos(a) * r, py = oy + Math.Sin(a) * r;
                    if (!Allowed(px, py)) continue;
                    double pc = ClearanceMemo(px, py, z, reach);
                    if (pc > bc) { bc = pc; bx = px; by = py; }
                }
                if (bc > 0) { x = bx; y = by; c = bc; found = true; }
            }
            if (!found) return c;
        }

        for (int iter = 0; iter < 16 && c < want; iter++)
        {
            var runs = ProbeRuns(x, y, z, reach);
            double vx = 0, vy = 0;
            for (int k = 0; k < ProbeDirs; k++)
            {
                double push = want - runs[k];
                if (push <= 0) continue;
                double a = k * (2 * Math.PI / ProbeDirs);
                vx -= Math.Cos(a) * push;
                vy -= Math.Sin(a) * push;
            }
            double vl = Math.Sqrt(vx * vx + vy * vy);
            if (vl < 1e-9) break;
            vx /= vl; vy /= vl;
            bool moved = false;
            for (double step = Math.Max(0.05, want - c); step >= 0.02 && !moved; step *= 0.5)
            {
                double px = x + vx * step, py = y + vy * step;
                if (!Allowed(px, py)) continue;
                double pc = ClearanceMemo(px, py, z, reach);
                if (pc > c + 0.01) { x = px; y = py; c = pc; moved = true; }
            }
            if (!moved) break;
        }
        if (c >= want) return c;

        // The push found no way out (the point sits right on a wall line or a floor corner, where
        // every probe stops at once): the nearest spot round it that has the clearance, else the
        // clearest spot within reach.
        double rx = x, ry = y, rc = c;
        for (double r = 0.1; r <= maxMove + 1e-9 && rc < want; r += 0.1)
            for (int k = 0; k < ProbeDirs; k++)
            {
                double a = k * (2 * Math.PI / ProbeDirs);
                double px = x + Math.Cos(a) * r, py = y + Math.Sin(a) * r;
                if (!Allowed(px, py)) continue;
                double pc = ClearanceMemo(px, py, z, reach);
                if (pc > rc + 0.02) { rx = px; ry = py; rc = pc; }
            }
        if (rc > c) { x = rx; y = ry; c = rc; }
        return c;
    }

    /// <summary>
    /// The least clearance (up to maxR) along the straight walk from p0 to p1, leaving out
    /// <paramref name="skipEnds"/> at each end (the end points are judged on their own),
    /// sampled every 0.4 m following the floor up and down ramps; where it is least in
    /// (wx, wy, wz). maxR when the walk is shorter than the two ends.
    /// </summary>
    public double LegClearance(double x0, double y0, double z0, double x1, double y1, double z1,
                               double maxR, double skipEnds, out double wx, out double wy, out double wz)
    {
        wx = (x0 + x1) * 0.5; wy = (y0 + y1) * 0.5; wz = (z0 + z1) * 0.5;
        double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 2 * skipEnds) return maxR;
        int steps = Math.Max(1, (int)Math.Ceiling((len - 2 * skipEnds) / 0.4));
        double best = maxR, zf = z0;
        for (int i = 0; i <= steps; i++)
        {
            double d = skipEnds + (len - 2 * skipEnds) * i / steps, t = d / len;
            double x = x0 + dx * t, y = y0 + dy * t, zl = z0 + (z1 - z0) * t;
            if (TryFloorHeight(x, y, zf, 0.6, out double h)) zf = h;
            else if (TryFloorHeight(x, y, zl, 0.6, out h)) zf = h;
            else zf = zl;
            double c = ClearanceMemo(x, y, zf, maxR);
            if (c < best) { best = c; wx = x; wy = y; wz = zf; }
        }
        return best;
    }

    /// <summary>Distances (metres, up to maxR) from (x, y) to the first wall or floor edge along
    /// each of the 16 probe directions (k * 22.5 degrees from east, counter-clockwise).</summary>
    internal double[] ProbeRuns(double x, double y, double z, double maxR)
    {
        var runs = new double[ProbeDirs];
        bool floorKnown = FloorUnder(x, y, z, FloorAbove, FloorBelow, out double h0, out bool walk);
        if (floorKnown && !walk) return runs;
        if (!floorKnown) h0 = z;
        for (int k = 0; k < ProbeDirs; k++)
        {
            double a = k * (2 * Math.PI / ProbeDirs);
            runs[k] = FreeRun(x, y, h0, Math.Cos(a), Math.Sin(a), maxR, floorKnown);
        }
        return runs;
    }

    // Any floor (steep or not) at about the point's height within 2 m of a point that has none
    // under it: the point is past the floor's edge (a dead end's cell origin is inside its end
    // cap), not in a cell whose floor the dats don't give (0x0174 has such cells above others).
    private bool FloorNear(double x, double y, double z)
    {
        for (double r = 0.5; r <= 2.0 + 1e-9; r += 0.5)
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                if (FloorUnder(x + Math.Cos(a) * r, y + Math.Sin(a) * r, z, FloorAbove, 1.5, out _, out _)) return true;
            }
        return false;
    }

    /// <summary>The height of the floor (any cell) under (x, y) nearest zRef, within
    /// <paramref name="band"/> above or below it.</summary>
    internal bool TryFloorHeight(double x, double y, double zRef, double band, out double h)
        => FloorUnder(x, y, zRef, band, band, out h, out _);

    // Distance along (dx, dy) from (x, y) to the first wall crossed or the first step off the
    // walkable floor, following the floor's height; maxR when nothing is hit.
    private double FreeRun(double x, double y, double h0, double dx, double dy, double maxR, bool checkFloor)
    {
        double px = x, py = y, ph = h0, prevR = 0;
        int n = (int)Math.Ceiling(maxR / ProbeStep - 1e-9);
        for (int i = 1; i <= n; i++)
        {
            double r = Math.Min(i * ProbeStep, maxR);
            double qx = x + dx * r, qy = y + dy * r, qh = ph;
            if (checkFloor && (!FloorUnder(qx, qy, ph, ProbeMaxRise, ProbeMaxRise, out qh, out bool w) || !w))
                return prevR + (r - prevR) * 0.5;
            if (WallHit(px, py, qx, qy, Math.Min(ph, qh) + ClearWallLow, Math.Max(ph, qh) + ClearWallHigh, out double t))
                return prevR + (r - prevR) * t;
            px = qx; py = qy; ph = qh; prevR = r;
        }
        return maxR;
    }

    // The first wall (reaching into zLo..zHi) the segment p0-p1 crosses, as a fraction along it.
    private bool WallHit(double x0, double y0, double x1, double y1, double zLo, double zHi, out double t)
    {
        t = 1;
        bool hit = WallHitIn(_wallAll, _wallGrid, x0, y0, x1, y1, zLo, zHi, ref t);
        if (_placedOverlay is { Walls.Count: > 0 } ov) hit |= WallHitIn(ov.Walls, ov.WallGrid, x0, y0, x1, y1, zLo, zHi, ref t);
        return hit;
    }

    private static bool WallHitIn(List<WallSeg> all, Dictionary<long, List<int>> grid, double x0, double y0, double x1, double y1, double zLo, double zHi, ref double t)
    {
        bool hit = false;
        double bx0 = Math.Min(x0, x1), bx1 = Math.Max(x0, x1), by0 = Math.Min(y0, y1), by1 = Math.Max(y0, y1);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all);
        // Every bucket the step touches (a wall is in every bucket its own box touches).
        for (int gx = GridOf(bx0); gx <= GridOf(bx1); gx++)
            for (int gy = GridOf(by0); gy <= GridOf(by1); gy++)
            {
                if (!grid.TryGetValue(GridKey(gx, gy), out var list)) continue;
                foreach (int i in list)
                {
                    ref readonly var w = ref span[i];
                    if (w.ZMax < zLo || w.ZMin > zHi) continue;
                    // Boxes apart: no crossing (a 0.1 m probe step against a 4 m bucket's walls, mostly).
                    if ((w.X0 < bx0 && w.X1 < bx0) || (w.X0 > bx1 && w.X1 > bx1) || (w.Y0 < by0 && w.Y1 < by0) || (w.Y0 > by1 && w.Y1 > by1)) continue;
                    if (!SegmentParam(x0, y0, x1, y1, w.X0, w.Y0, w.X1, w.Y1, out double s) || s >= t) continue;
                    var (lo, hi) = w.ZRangeAt(x0 + (x1 - x0) * s, y0 + (y1 - y0) * s);
                    if (hi < zLo || lo > zHi) continue;
                    t = s; hit = true;
                }
            }
        return hit;
    }

    /// <summary>
    /// Line of sight between two points on the floor (the Sweep patrol): no wall of the landblock
    /// reaching into knee-to-head height between them (the same band <see cref="SegmentClear"/>
    /// uses), whichever cells the walls belong to. Floors aren't looked at: a monster sees across
    /// a gap.
    /// </summary>
    internal bool SightClear(double x0, double y0, double z0, double x1, double y1, double z1)
        => !WallHit(x0, y0, x1, y1, Math.Min(z0, z1) + WallBandLow, Math.Max(z0, z1) + WallBandHigh, out _);

    /// <summary>A point on the cell's floor: the centre of its biggest floor polygon. False when
    /// the dats give the cell no floor (then its origin stands in).</summary>
    internal bool TryCellFloorCentre(uint cell, out double x, out double y, out double z)
    {
        x = y = z = 0;
        if (!_floors.TryGetValue(cell, out var list) || list.Count == 0) return false;
        double bestArea = -1;
        foreach (var v in list)
        {
            if (v.Length < 3) continue;
            // In doubles, from the first corner: a.X * b.Y in floats at world coordinates (44388 * 15006)
            // keeps about 40 m² of 6.7e8, so a small cell's floor came out with no area and the cell read
            // as floorless (building E74E#14, 2026-11-17: 01B2, the west stairs 01BE and 01C0, its doorway cells).
            double area = 0, cx = 0, cy = 0, cz = 0, ox = v[0].X, oy = v[0].Y;
            for (int i = 0; i < v.Length; i++)
            {
                var a = v[i]; var b = v[(i + 1) % v.Length];
                area += (a.X - ox) * (b.Y - oy) - (b.X - ox) * (a.Y - oy);
                cx += a.X; cy += a.Y; cz += a.Z;
            }
            area = Math.Abs(area) * 0.5;
            if (area <= bestArea) continue;
            bestArea = area;
            x = cx / v.Length; y = cy / v.Length; z = cz / v.Length;
        }
        return bestArea > 0;
    }

    /// <summary>Walls (footprint and height range) within r of (x, y), for offline inspection.</summary>
    internal List<(double X0, double Y0, double X1, double Y1, double ZMin, double ZMax, double Dist)> WallsNear(double x, double y, double r)
    {
        var res = new List<(double, double, double, double, double, double, double)>();
        void From(List<WallSeg> all, Dictionary<long, List<int>> grid)
        {
            var seen = new HashSet<int>();
            for (int gx = GridOf(x - r); gx <= GridOf(x + r); gx++)
                for (int gy = GridOf(y - r); gy <= GridOf(y + r); gy++)
                {
                    if (!grid.TryGetValue(GridKey(gx, gy), out var list)) continue;
                    foreach (int i in list)
                    {
                        if (!seen.Add(i)) continue;
                        var w = all[i];
                        double d = PointSegDist(x, y, w.X0, w.Y0, w.X1, w.Y1, out double cx, out double cy);
                        var (lo, hi) = w.ZRangeAt(cx, cy);   // its height range at the nearest point
                        if (d <= r) res.Add((w.X0, w.Y0, w.X1, w.Y1, lo, hi, d));
                    }
                }
        }
        From(_wallAll, _wallGrid);
        if (_placedOverlay != null) From(_placedOverlay.Walls, _placedOverlay.WallGrid);
        return res;
    }

    // Exact distance from (x, y) to the nearest wall reaching into zLo..zHi, up to maxR.
    private double WallDistance(double x, double y, double zLo, double zHi, double maxR)
    {
        double best = WallDistanceIn(_wallAll, _wallGrid, x, y, zLo, zHi, maxR);
        if (_placedOverlay is { Walls.Count: > 0 } ov) best = WallDistanceIn(ov.Walls, ov.WallGrid, x, y, zLo, zHi, best);
        return best;
    }

    private static double WallDistanceIn(List<WallSeg> all, Dictionary<long, List<int>> grid, double x, double y, double zLo, double zHi, double maxR)
    {
        double best = maxR;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all);
        for (int gx = GridOf(x - maxR); gx <= GridOf(x + maxR); gx++)
            for (int gy = GridOf(y - maxR); gy <= GridOf(y + maxR); gy++)
            {
                if (!grid.TryGetValue(GridKey(gx, gy), out var list)) continue;
                foreach (int i in list)
                {
                    ref readonly var w = ref span[i];
                    if (w.ZMax < zLo || w.ZMin > zHi) continue;
                    // Its box farther off than the best so far: no nearer point on it.
                    if ((w.X0 < x - best && w.X1 < x - best) || (w.X0 > x + best && w.X1 > x + best) || (w.Y0 < y - best && w.Y1 < y - best) || (w.Y0 > y + best && w.Y1 > y + best)) continue;
                    double d = PointSegDist(x, y, w.X0, w.Y0, w.X1, w.Y1, out double cx, out double cy);
                    if (d >= best) continue;
                    var (lo, hi) = w.ZRangeAt(cx, cy);
                    if (hi < zLo || lo > zHi) continue;
                    best = d;
                }
            }
        return best;
    }

    private static double PointSegDist(double px, double py, double ax, double ay, double bx, double by)
        => PointSegDist(px, py, ax, ay, bx, by, out _, out _);

    private static double PointSegDist(double px, double py, double ax, double ay, double bx, double by, out double cx, out double cy)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        cx = ax + dx * t; cy = ay + dy * t;
        return Math.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py));
    }

    // Where segment a-b meets segment c-d, as a fraction along a-b (touching counts).
    private static bool SegmentParam(double ax, double ay, double bx, double by,
                                     double cx, double cy, double dx, double dy, out double t)
    {
        t = 0;
        double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return false;
        double qx = cx - ax, qy = cy - ay;
        t = (qx * sy - qy * sx) / den;
        double u = (qx * ry - qy * rx) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    internal static ulong EdgeKey(uint a, uint b)
        => a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

    internal static double NavToWorld(double navCoord) => (navCoord * 10.0 + 1019.5) * 24.0;
    internal static double WorldToNav(double world) => (world / 24.0 - 1019.5) / 10.0;

    private static void NewellNormal(Vector3[] v, out double nx, out double ny, out double nz)
    {
        nx = 0; ny = 0; nz = 0;
        int n = v.Length;
        for (int i = 0; i < n; i++)
        {
            var a = v[i]; var b = v[(i + 1) % n];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
    }

    private static double FloorZAt(Vector3[] v)
    {
        double z = 0;
        foreach (var p in v) z += p.Z;
        return z / v.Length;
    }

    internal static bool InsidePolygon2D(Vector3[] v, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            double xi = v[i].X, yi = v[i].Y, xj = v[j].X, yj = v[j].Y;
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    // Proper crossing of two 2D segments (touching at an end point does not count).
    private static bool SegmentsCross(double ax, double ay, double bx, double by,
                                      double cx, double cy, double dx, double dy)
    {
        double d1 = Cross(cx, cy, dx, dy, ax, ay);
        double d2 = Cross(cx, cy, dx, dy, bx, by);
        double d3 = Cross(ax, ay, bx, by, cx, cy);
        double d4 = Cross(ax, ay, bx, by, dx, dy);
        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
               ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }

    private static double Cross(double ax, double ay, double bx, double by, double px, double py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
}
