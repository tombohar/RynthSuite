using System.Diagnostics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using RynthNav.Routing;

namespace RynthNav.Baker;

// --graph <tile dir> [--graph-out <file>]: the coarse long-range graph RynthNav routes over
// (NavGraph, navgraph.bin), built from FINISHED tiles. No rebake; rerun it whenever tiles change.
//  1. Regions: each tile's connected walkable areas (NavRegions: internal polygon links only).
//  2. Portals: every east/north neighbour pair is added to a fresh 2-tile Detour navmesh, so the
//     cross-tile links are exactly the ones Detour (and so the plugin) makes. Links between the
//     same two regions are grouped into stretches along the border (a gap over GapUnits starts a
//     new one); each stretch becomes one portal, at the link nearest its middle.
//  3. Costs: inside each region, the walking distance between every pair of its portals
//     (Detour FindPath + straight path in that tile alone).
// Reads only complete nav_XXXX.tile files (tiles are written to .tmp and moved into place, and a
// file younger than a few seconds is left for the next run), so it can run beside a bake.
internal static class GraphBuild
{
    public const float GapUnits = 16f;
    private const int VertsPerPoly = 6;

    private sealed class Tile
    {
        public uint Lb;
        public int RegionBase, RegionCount;
        public int[] Regions = Array.Empty<int>();
        // Portals touching this tile: (portal id, local region, polygon index, point).
        public readonly List<(int portal, int region, int poly, RcVec3f pt)> Ends = new();
    }

    public sealed record Stats(int Tiles, int Skipped, int Regions, int Portals, int RegionsWithPortals, long CostPairs, long CostsUnknown, long Bytes, string SetId, TimeSpan Time);

    public static Stats Run(string dir, string outPath, Action<string> log, TimeSpan? minAge = null)
    {
        var sw = Stopwatch.StartNew();
        DateTime cutoff = DateTime.UtcNow - (minAge ?? TimeSpan.FromSeconds(5));
        var files = new SortedDictionary<uint, string>();
        int skipped = 0;
        foreach (string f in Directory.GetFiles(dir, "nav_*.tile"))
        {
            string name = Path.GetFileName(f);
            if (name.Length != 13 || !name.EndsWith(".tile", StringComparison.OrdinalIgnoreCase)) continue;
            if (!uint.TryParse(name.AsSpan(4, 4), System.Globalization.NumberStyles.HexNumber, null, out uint lb)) continue;
            if (File.GetLastWriteTimeUtc(f) > cutoff) { skipped++; continue; }   // maybe still being written
            files[lb] = f;
        }
        log($"graph: {files.Count} tiles in {dir}{(skipped > 0 ? $" ({skipped} too new, left for the next run)" : "")}");

        var builder = new NavGraph.Builder();
        var tiles = new Dictionary<uint, Tile>();
        var cache = new Dictionary<uint, DtMeshData>();

        DtMeshData? Read(uint lb)
        {
            if (cache.TryGetValue(lb, out var md)) return md;
            if (!files.TryGetValue(lb, out string? path)) return null;
            try
            {
                using var fr = File.OpenRead(path);
                using var br = new BinaryReader(fr);
                md = new DtMeshDataReader().Read(br, VertsPerPoly);
            }
            catch (Exception ex) { log($"graph: 0x{lb:X4} unreadable ({ex.GetType().Name}); left out"); files.Remove(lb); return null; }
            cache[lb] = md;
            return md;
        }

        // 1. Regions (and the tile list the graph is checked against).
        foreach (uint lb in files.Keys.ToList())
        {
            DtMeshData? md = Read(lb);
            if (md == null) continue;
            int[] regions = NavRegions.Build(md, out int count);
            var (p, v, d) = NavRegions.Fingerprint(md);
            int b = builder.AddTile(lb, p, v, d, count);
            tiles[lb] = new Tile { Lb = lb, RegionBase = b, RegionCount = count, Regions = regions };
            cache.Remove(lb);   // re-read column by column below; keeps memory flat
        }
        log($"graph: {tiles.Count} tiles, {builder.Regions} regions [{sw.Elapsed.TotalSeconds:F0} s]");

        // 2 + 3. Column by column: portals with the east and north neighbours, then (once a
        // column's every neighbour is done) the costs inside its regions.
        int portals = 0;
        long costPairs = 0, costsUnknown = 0;
        int withPortals = 0;
        var columns = tiles.Keys.Select(lb => (int)(lb >> 8)).Distinct().OrderBy(x => x).ToList();
        int? prevColumn = null;
        foreach (int x in columns)
        {
            foreach (uint lb in tiles.Keys.Where(k => (k >> 8) == x).OrderBy(k => k))
            {
                int y = (int)(lb & 0xFF);
                if (x < 255) portals += Link(lb, (uint)(((x + 1) << 8) | y), 0);
                if (y < 255) portals += Link(lb, (uint)((x << 8) | (y + 1)), 2);
            }
            // Column x-1 now has all its portals (west pairs were done with it, east pairs just now).
            if (prevColumn is int px) { Costs(px); foreach (uint k in cache.Keys.Where(k => (k >> 8) <= px).ToList()) cache.Remove(k); }
            prevColumn = x;
        }
        if (prevColumn is int last) Costs(last);

        int Link(uint a, uint b, int side)
        {
            if (!tiles.TryGetValue(a, out Tile? ta) || !tiles.TryGetValue(b, out Tile? tb)) return 0;
            DtMeshData? ma = Read(a), mb = Read(b);
            if (ma == null || mb == null) return 0;
            var nav = new DtNavMesh();
            var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 4, maxPolys = 1 << 16 };
            nav.Init(ref prm, VertsPerPoly);
            nav.AddTile(ma, 0, 0, out long refA);
            nav.AddTile(mb, 0, 0, out long refB);
            DtMeshTile tA = nav.GetTileByRef(refA), tB = nav.GetTileByRef(refB);
            bool east = side == 0;
            // Every link from a polygon of A into B: (region A, region B) -> segments along the border.
            var groups = new Dictionary<(int, int), List<(float lo, float hi, RcVec3f mid, int pa, int pb)>>();
            for (int i = 0; i < ma.header.polyCount; i++)
            {
                DtPoly poly = ma.polys[i];
                for (int k = poly.firstLink; k != DtDetour.DT_NULL_LINK; k = tA.links[k].next)
                {
                    DtLink l = tA.links[k];
                    if (l.side != side) continue;
                    nav.GetTileAndPolyByRefUnsafe(l.refs, out DtMeshTile lt, out DtPoly lp);
                    if (lt != tB) continue;
                    int va = poly.verts[l.edge], vb = poly.verts[(l.edge + 1) % poly.vertCount];
                    var p0 = new RcVec3f(ma.verts[va * 3], ma.verts[va * 3 + 1], ma.verts[va * 3 + 2]);
                    var p1 = new RcVec3f(ma.verts[vb * 3], ma.verts[vb * 3 + 1], ma.verts[vb * 3 + 2]);
                    float t0 = l.bmin / 255f, t1 = l.bmax / 255f;
                    var s0 = RcVec3f.Lerp(p0, p1, t0); var s1 = RcVec3f.Lerp(p0, p1, t1);
                    float a0 = east ? s0.Z : s0.X, a1 = east ? s1.Z : s1.X;
                    var key = (ta.Regions[i], tb.Regions[lp.index]);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                    list.Add((Math.Min(a0, a1), Math.Max(a0, a1), RcVec3f.Lerp(s0, s1, 0.5f), i, lp.index));
                }
            }
            int made = 0;
            foreach (var ((ra, rb), segs) in groups)
            {
                segs.Sort((u, v) => u.lo.CompareTo(v.lo));
                int start = 0; float hi = segs[0].hi;
                for (int i = 1; i <= segs.Count; i++)
                {
                    if (i < segs.Count && segs[i].lo <= hi + GapUnits) { hi = Math.Max(hi, segs[i].hi); continue; }
                    // Stretch segs[start..i): one portal at the link nearest its middle.
                    float middle = (segs[start].lo + hi) / 2;
                    int bestK = start; float bestD = float.MaxValue;
                    for (int k = start; k < i; k++)
                    {
                        float m = (segs[k].lo + segs[k].hi) / 2, d = Math.Abs(m - middle);
                        if (d < bestD) { bestD = d; bestK = k; }
                    }
                    var s = segs[bestK];
                    int pid = builder.AddPortal(s.mid.X, s.mid.Y, s.mid.Z, ta.RegionBase + ra, tb.RegionBase + rb);
                    ta.Ends.Add((pid, ra, s.pa, s.mid));
                    tb.Ends.Add((pid, rb, s.pb, s.mid));
                    made++;
                    if (i < segs.Count) { start = i; hi = segs[i].hi; }
                }
            }
            return made;
        }

        void Costs(int column)
        {
            foreach (Tile t in tiles.Values.Where(t => (t.Lb >> 8) == column))
            {
                if (t.Ends.Count == 0) continue;
                DtMeshData? md = Read(t.Lb);
                if (md == null) continue;
                var nav = new DtNavMesh();
                var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 2, maxPolys = 1 << 16 };
                nav.Init(ref prm, VertsPerPoly);
                nav.AddTile(md, 0, 0, out long tref);
                long baseRef = nav.GetPolyRefBase(nav.GetTileByRef(tref));
                var q = new DtNavMeshQuery(nav);
                var filter = new DtQueryDefaultFilter();
                foreach (var grp in t.Ends.GroupBy(e => e.region))
                {
                    var ends = grp.ToList();
                    int n = ends.Count;
                    withPortals++;
                    var costs = new float[n * n];
                    for (int i = 0; i < n; i++)
                        for (int j = i + 1; j < n; j++)
                        {
                            costPairs++;
                            float c = PathLength(q, filter, baseRef | (uint)ends[i].poly, ends[i].pt, baseRef | (uint)ends[j].poly, ends[j].pt);
                            if (!float.IsFinite(c)) costsUnknown++;
                            costs[i * n + j] = c;
                        }
                    builder.SetRegionCosts(t.RegionBase + grp.Key, ends.Select(e => e.portal).ToArray(), costs);
                }
                t.Ends.Clear();
                t.Ends.TrimExcess();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        string tmp = outPath + ".tmp";
        using (var fs = File.Create(tmp)) builder.Write(fs);
        File.Move(tmp, outPath, overwrite: true);
        var g = NavGraph.Load(outPath);   // read it back: the plugin's own loader must accept it
        long bytes = new FileInfo(outPath).Length;
        var st = new Stats(tiles.Count, skipped, builder.Regions, g.PortalCount, withPortals, costPairs, costsUnknown, bytes, g.SetIdHex, sw.Elapsed);
        log($"graph: {st.Regions} regions ({st.RegionsWithPortals} with portals), {st.Portals} portals, {st.CostPairs} region crossings costed ({st.CostsUnknown} without a path), {bytes / 1024.0 / 1024.0:F1} MB -> {outPath}; set {st.SetId} [{sw.Elapsed.TotalSeconds:F0} s]");
        return st;
    }

    /// <summary>Walking distance (2D) along the straight path between two points of one tile; +inf if not connected.</summary>
    private static float PathLength(DtNavMeshQuery q, IDtQueryFilter filter, long a, RcVec3f pa, long b, RcVec3f pb)
    {
        if (a == b) return MathF.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Z - pb.Z) * (pa.Z - pb.Z));
        Span<long> path = stackalloc long[1024];
        var st = q.FindPath(a, b, pa, pb, filter, path, out int pc, 1024);
        if (st.Failed() || pc == 0 || path[pc - 1] != b) return float.PositiveInfinity;
        Span<DtStraightPath> sp = new DtStraightPath[256];
        q.FindStraightPath(pa, pb, path[..pc], pc, sp, out int spc, 256, 0);
        if (spc < 2) return float.PositiveInfinity;
        float len = 0;
        for (int i = 1; i < spc; i++)
        {
            float dx = sp[i].pos.X - sp[i - 1].pos.X, dz = sp[i].pos.Z - sp[i - 1].pos.Z;
            len += MathF.Sqrt(dx * dx + dz * dz);
        }
        // A straight path capped at 256 corners stops short: add what's left in a straight line.
        var end = sp[spc - 1].pos;
        len += MathF.Sqrt((end.X - pb.X) * (end.X - pb.X) + (end.Z - pb.Z) * (end.Z - pb.Z));
        return len;
    }
}
