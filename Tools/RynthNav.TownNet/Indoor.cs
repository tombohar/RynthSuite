using System.Numerics;

namespace RynthNav.TownNet;

/// <summary>Something solid seen from above: a polygon outline (or a disc), between two heights.</summary>
internal sealed class Footprint
{
    public Vector3[] Poly = Array.Empty<Vector3>();
    public bool Filled;                  // the outline has area: inside counts as touching
    public Vector2 Center; public float Radius;   // a disc when Poly is empty
    public float ZMin, ZMax;
    public string Source = "";
    public float MinX, MinY, MaxX, MaxY;

    public float Dist(float x, float y) =>
        Poly.Length == 0 ? MathF.Max(0, Vector2.Distance(Center, new Vector2(x, y)) - Radius) : Geo.DistXY(Poly, x, y, Filled);

    /// <summary>Does it stand in the way of someone whose feet are at z (step over below +0.45, head at +1.8)?</summary>
    public bool Blocks(float z) => ZMax > z + Indoor.StepUp && ZMin < z + Indoor.BodyHeight;

    public void Bounds()
    {
        if (Poly.Length == 0) { MinX = Center.X - Radius; MaxX = Center.X + Radius; MinY = Center.Y - Radius; MaxY = Center.Y + Radius; return; }
        MinX = Poly.Min(v => v.X); MaxX = Poly.Max(v => v.X); MinY = Poly.Min(v => v.Y); MaxY = Poly.Max(v => v.Y);
    }

    public static Footprint FromPolygon(Vector3[] p, string source)
    {
        var f = new Footprint { Poly = p, Filled = Geo.AreaXY(p) > 0.01f, ZMin = p.Min(v => v.Z), ZMax = p.Max(v => v.Z), Source = source };
        f.Bounds();
        return f;
    }

    public static Footprint Disc(float x, float y, float r, float zmin, float zmax, string source)
    {
        var f = new Footprint { Center = new Vector2(x, y), Radius = r, ZMin = zmin, ZMax = zmax, Source = source };
        f.Bounds();
        return f;
    }
}

/// <summary>A portal object inside the network: walking into it uses it, so the walks keep clear of all but their own.</summary>
internal sealed class PortalZone
{
    public string Guid = "";
    public string Name = "";
    public uint Cell;
    public Vector3 Pos;
    public float Radius;                 // its collision radius
}

/// <summary>The indoor world the walks are planned in: cells, solid things, portal objects.</summary>
internal sealed class Indoor
{
    public const float StepUp = 0.45f;
    public const float BodyHeight = 1.8f;
    public const float StepZ = 0.45f;    // largest height change between neighbouring floor samples

    public readonly Dictionary<uint, EnvCell> Cells = new();
    public readonly List<Footprint> Solids = new();
    public readonly List<PortalZone> Portals = new();
    public float AgentRadius = 0.5f;
    public readonly List<string> Notes = new();

    public EnvCell Cell(uint id) => Cells[id];

    /// <summary>The floor height of a cell at (x, y), or null when (x, y) is not on its floor.</summary>
    public float? FloorZ(EnvCell c, float x, float y, float? near = null, float eps = 0.01f)
    {
        float? best = null;
        foreach (var f in c.Floors)
        {
            if (!Geo.InsideXY(f, x, y, eps)) continue;
            float z = Geo.PlaneZ(f, x, y);
            if (best == null || (near != null && MathF.Abs(z - near.Value) < MathF.Abs(best.Value - near.Value))) best = z;
        }
        return best;
    }

    /// <summary>The portal polygons of cell a that open into cell b.</summary>
    public IEnumerable<Vector3[]> Doorways(EnvCell a, uint b)
    {
        ushort bs = (ushort)(b & 0xFFFF);
        foreach (var p in a.Portals)
            if (p.OtherCell == bs && p.Polygon.Length >= 3) yield return p.Polygon;
    }

    public uint Full(EnvCell from, ushort other) => (from.Id & 0xFFFF0000u) | other;

    /// <summary>
    /// Follows the straight segment a-b along the floor from cell <paramref name="start"/>, sampling every
    /// <paramref name="step"/>: every sample must be on the floor of the cell it is in, and the walk may only
    /// change cell where the segment passes through a doorway (cell portal) between the two. Returns the cells
    /// visited, or null with a reason when the segment leaves the floor or goes through a wall.
    /// </summary>
    public List<uint>? Trace(Vector3 a, Vector3 b, uint start, out string why, float step = 0.05f)
    {
        why = "";
        var cells = new List<uint> { start };
        EnvCell cur = Cells[start];
        float len = Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
        int n = Math.Max(1, (int)MathF.Ceiling(len / step));
        Vector3 prev = a;
        float prevZ = FloorZ(cur, a.X, a.Y, a.Z, 0.02f) ?? float.NaN;
        if (float.IsNaN(prevZ)) { why = $"start ({a.X:F2},{a.Y:F2}) is not on the floor of {start:X8}"; return null; }
        for (int i = 1; i <= n; i++)
        {
            Vector3 p = Vector3.Lerp(a, b, i / (float)n);
            // A doorway crossed between the last sample and this one moves us into the next cell.
            EnvCell? next = null;
            float lift = 0.6f;
            var pa = new Vector3(prev.X, prev.Y, prevZ + lift);
            foreach (var cp in cur.Portals)
            {
                if (cp.OtherCell == 0xFFFF || cp.Polygon.Length < 3) continue;
                uint oid = Full(cur, cp.OtherCell);
                if (!Cells.TryGetValue(oid, out var other)) continue;
                float? oz = FloorZ(other, p.X, p.Y, prevZ, 0.02f);
                if (oz == null || MathF.Abs(oz.Value - prevZ) > StepZ) continue;
                if (Geo.SegmentCrosses(cp.Polygon, pa, new Vector3(p.X, p.Y, oz.Value + lift))) { next = other; break; }
            }
            if (next != null)
            {
                cur = next;
                cells.Add(cur.Id);
                prevZ = FloorZ(cur, p.X, p.Y, prevZ, 0.02f)!.Value;
            }
            else
            {
                float? z = FloorZ(cur, p.X, p.Y, prevZ, 0.02f);
                if (z == null) { why = $"({p.X:F2},{p.Y:F2}) is off the floor of {cur.Id:X8}"; return null; }
                if (MathF.Abs(z.Value - prevZ) > StepZ) { why = $"({p.X:F2},{p.Y:F2}) height jump {prevZ:F2} -> {z:F2} in {cur.Id:X8}"; return null; }
                prevZ = z.Value;
            }
            prev = p;
        }
        return cells;
    }

    /// <summary>The nearest solid thing (that blocks at the floor height z) to (x, y), within limit.</summary>
    public float SolidDist(float x, float y, float z, float limit, out Footprint? which)
    {
        which = null;
        float best = limit;
        if (_buckets == null) Index();
        int bx0 = (int)MathF.Floor((x - limit) / Bucket), bx1 = (int)MathF.Floor((x + limit) / Bucket);
        int by0 = (int)MathF.Floor((y - limit) / Bucket), by1 = (int)MathF.Floor((y + limit) / Bucket);
        var seen = new HashSet<Footprint>();
        for (int bx = bx0; bx <= bx1; bx++)
        for (int by = by0; by <= by1; by++)
        {
        if (!_buckets!.TryGetValue((bx, by), out var list)) continue;
        foreach (var f in list)
        {
            if (!seen.Add(f)) continue;
            if (x < f.MinX - best || x > f.MaxX + best || y < f.MinY - best || y > f.MaxY + best) continue;
            if (!f.Blocks(z)) continue;
            float d = f.Dist(x, y);
            if (d < best) { best = d; which = f; }
        }
        }
        return best;
    }

    private const float Bucket = 4f;
    private Dictionary<(int, int), List<Footprint>>? _buckets;

    /// <summary>A coarse spatial index of the solids (call again after changing <see cref="Solids"/>).</summary>
    public void Index()
    {
        _buckets = new();
        foreach (var f in Solids)
            for (int bx = (int)MathF.Floor(f.MinX / Bucket); bx <= (int)MathF.Floor(f.MaxX / Bucket); bx++)
                for (int by = (int)MathF.Floor(f.MinY / Bucket); by <= (int)MathF.Floor(f.MaxY / Bucket); by++)
                {
                    if (!_buckets.TryGetValue((bx, by), out var l)) _buckets[(bx, by)] = l = new List<Footprint>();
                    l.Add(f);
                }
    }

    /// <summary>Clearance from portal objects other than <paramref name="except"/>: distance minus the portal's radius.</summary>
    public float PortalDist(float x, float y, float z, string? except, out PortalZone? which)
    {
        which = null;
        float best = float.MaxValue;
        foreach (var p in Portals)
        {
            if (p.Guid == except || MathF.Abs(p.Pos.Z - z) > 2.5f) continue;
            float d = Vector2.Distance(new Vector2(p.Pos.X, p.Pos.Y), new Vector2(x, y)) - p.Radius;
            if (d < best) { best = d; which = p; }
        }
        return best;
    }
}
