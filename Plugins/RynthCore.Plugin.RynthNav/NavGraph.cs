using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RynthNav.Routing;

/// <summary>
/// The coarse long-range graph of the navmesh (NavData\navgraph.bin, made by
/// RynthNav.Baker --graph from the finished tiles).
///
///  - Regions: the connected walkable areas of each tile (<see cref="NavRegions"/>).
///  - Portals: places where a region links to a region of the neighbouring tile (Detour's own
///    cross-tile links, grouped into stretches along the border; one point per stretch).
///  - Costs: the walking distance inside a region between each pair of its portals.
///
/// A route is an A* over portals: from the region underfoot to the target's region. The walker
/// then follows the portal points with the detailed planner, a few landblocks at a time.
///
/// Tile set check: the file records each tile's polygon/vertex/detail-triangle counts. The
/// plugin compares them with every tile it loads and ignores the graph on the first mismatch
/// (a graph from another bake), falling back to walking without it.
///
/// Host-free (no AC, no Detour): the plugin, the baker and the tests share it.
/// </summary>
internal sealed class NavGraph
{
    public const string FileName = "navgraph.bin";
    private const uint Magic = 0x31474E52; // "RNG1"
    private const int Version = 1;
    private const ushort NoCost = 0xFFFF;
    public const float CostUnit = 0.5f;    // costs are stored in half-units

    public readonly struct TileInfo
    {
        public readonly ushort Lb; public readonly int Polys, Verts, DetailTris, RegionBase, RegionCount;
        public TileInfo(ushort lb, int polys, int verts, int detailTris, int regionBase, int regionCount)
        { Lb = lb; Polys = polys; Verts = verts; DetailTris = detailTris; RegionBase = regionBase; RegionCount = regionCount; }
    }

    public byte[] SetId { get; private set; } = Array.Empty<byte>();
    public string SetIdHex => Convert.ToHexString(SetId).ToLowerInvariant();
    public int RegionCount { get; private set; }
    public int PortalCount => _px.Length;
    public int TileCount => _tiles.Count;

    private readonly Dictionary<ushort, TileInfo> _tiles = new();
    private float[] _px = Array.Empty<float>(), _py = Array.Empty<float>(), _pz = Array.Empty<float>();
    private int[] _pa = Array.Empty<int>(), _pb = Array.Empty<int>();
    private int[]?[] _regionPortals = Array.Empty<int[]?>();
    private ushort[]?[] _regionCosts = Array.Empty<ushort[]?>();

    public bool TryGetTile(uint lb, out TileInfo t) => _tiles.TryGetValue((ushort)lb, out t);

    /// <summary>Global region id of local region <paramref name="local"/> in tile <paramref name="lb"/>, or -1.</summary>
    public int Region(uint lb, int local)
        => _tiles.TryGetValue((ushort)lb, out TileInfo t) && local >= 0 && local < t.RegionCount ? t.RegionBase + local : -1;

    private ushort[] _regionLb = Array.Empty<ushort>();

    /// <summary>The landblock a global region belongs to.</summary>
    public uint RegionLandblock(int region) => (uint)region < (uint)_regionLb.Length ? _regionLb[region] : 0u;

    /// <summary>Portal position in world units: east, height, north.</summary>
    public (float Ew, float Up, float Ns) Portal(int p) => (_px[p], _py[p], _pz[p]);
    public (int A, int B) PortalRegions(int p) => (_pa[p], _pb[p]);
    public int[] RegionPortals(int region) => _regionPortals[region] ?? Array.Empty<int>();

    /// <summary>True when a loaded tile matches what the graph was built from.</summary>
    public bool Matches(uint lb, int polys, int verts, int detailTris)
        => _tiles.TryGetValue((ushort)lb, out TileInfo t) && t.Polys == polys && t.Verts == verts && t.DetailTris == detailTris;

    // ── Building (the baker) ───────────────────────────────────────────────────

    public sealed class Builder
    {
        private readonly List<TileInfo> _tiles = new();
        private readonly List<(float x, float y, float z, int a, int b)> _portals = new();
        private readonly Dictionary<int, (int[] portals, ushort[] costs)> _costs = new();
        private int _regions;

        /// <summary>Adds a tile (in any order); returns the global id of its region 0.</summary>
        public int AddTile(uint lb, int polys, int verts, int detailTris, int regionCount)
        {
            int b = _regions;
            _tiles.Add(new TileInfo((ushort)lb, polys, verts, detailTris, b, regionCount));
            _regions += regionCount;
            return b;
        }

        public int AddPortal(float x, float y, float z, int regionA, int regionB)
        {
            _portals.Add((x, y, z, regionA, regionB));
            return _portals.Count - 1;
        }

        /// <summary>
        /// Walking costs inside a region between its portals: costs[i*n+j] (i &lt; j used),
        /// world units; NaN or infinity = no path found.
        /// </summary>
        public void SetRegionCosts(int region, int[] portals, float[] costs)
        {
            int n = portals.Length;
            var tri = new ushort[n * (n - 1) / 2];
            int k = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    float c = costs[i * n + j];
                    tri[k++] = float.IsFinite(c) ? (ushort)Math.Min(NoCost - 1, Math.Max(1, (int)Math.Round(c / CostUnit))) : NoCost;
                }
            _costs[region] = (portals, tri);
        }

        public int Regions => _regions;

        public void Write(Stream output)
        {
            using var gz = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
            using var w = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);
            w.Write(Magic); w.Write(Version);
            w.Write(SetIdOf(_tiles));
            w.Write(_tiles.Count);
            foreach (TileInfo t in _tiles)
            {
                w.Write(t.Lb); w.Write(t.Polys); w.Write(t.Verts); w.Write(t.DetailTris); w.Write(t.RegionBase); w.Write(t.RegionCount);
            }
            w.Write(_regions);
            w.Write(_portals.Count);
            foreach (var p in _portals) { w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(p.a); w.Write(p.b); }
            w.Write(_costs.Count);
            foreach (var (region, (portals, tri)) in _costs)
            {
                w.Write(region); w.Write(portals.Length);
                foreach (int p in portals) w.Write(p);
                foreach (ushort c in tri) w.Write(c);
            }
        }
    }

    /// <summary>Id of a tile set: SHA-256 (first 16 bytes) over every tile's landblock and fingerprint, in landblock order.</summary>
    public static byte[] SetIdOf(IEnumerable<TileInfo> tiles)
    {
        var sorted = new List<TileInfo>(tiles);
        sorted.Sort((a, b) => a.Lb.CompareTo(b.Lb));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            foreach (TileInfo t in sorted) { w.Write(t.Lb); w.Write(t.Polys); w.Write(t.Verts); w.Write(t.DetailTris); }
        return SHA256.HashData(ms.ToArray()).AsSpan(0, 16).ToArray();
    }

    // ── Loading ──────────────────────────────────────────────────────────────────

    public static NavGraph Load(string path)
    {
        using var f = File.OpenRead(path);
        return Load(f);
    }

    public static NavGraph Load(Stream input)
    {
        using var gz = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a RynthNav graph");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"graph version {version}, expected {Version}");
        var g = new NavGraph { SetId = r.ReadBytes(16) };
        int tiles = r.ReadInt32();
        if (tiles < 0 || tiles > 65536) throw new InvalidDataException("bad tile count");
        var list = new List<TileInfo>(tiles);
        for (int i = 0; i < tiles; i++)
        {
            var t = new TileInfo(r.ReadUInt16(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            g._tiles[t.Lb] = t;
            list.Add(t);
        }
        g.RegionCount = r.ReadInt32();
        if (g.RegionCount < 0 || g.RegionCount > 50_000_000) throw new InvalidDataException("bad region count");
        g._regionLb = new ushort[g.RegionCount];
        foreach (TileInfo t in list)
        {
            if (t.RegionBase < 0 || t.RegionCount < 0 || (long)t.RegionBase + t.RegionCount > g.RegionCount) throw new InvalidDataException("bad tile region range");
            for (int k = 0; k < t.RegionCount; k++) g._regionLb[t.RegionBase + k] = t.Lb;
        }
        int portals = r.ReadInt32();
        if (portals < 0 || portals > 50_000_000) throw new InvalidDataException("bad portal count");
        g._px = new float[portals]; g._py = new float[portals]; g._pz = new float[portals];
        g._pa = new int[portals]; g._pb = new int[portals];
        for (int i = 0; i < portals; i++)
        {
            g._px[i] = r.ReadSingle(); g._py[i] = r.ReadSingle(); g._pz[i] = r.ReadSingle();
            g._pa[i] = r.ReadInt32(); g._pb[i] = r.ReadInt32();
            if ((uint)g._pa[i] >= (uint)g.RegionCount || (uint)g._pb[i] >= (uint)g.RegionCount) throw new InvalidDataException("portal region out of range");
        }
        g._regionPortals = new int[]?[g.RegionCount];
        g._regionCosts = new ushort[]?[g.RegionCount];
        int withCosts = r.ReadInt32();
        for (int i = 0; i < withCosts; i++)
        {
            int region = r.ReadInt32(), n = r.ReadInt32();
            if ((uint)region >= (uint)g.RegionCount || n < 0 || n > 100_000) throw new InvalidDataException("bad region record");
            var ps = new int[n];
            for (int k = 0; k < n; k++) { ps[k] = r.ReadInt32(); if ((uint)ps[k] >= (uint)portals) throw new InvalidDataException("bad portal id"); }
            var tri = new ushort[n * (n - 1) / 2];
            for (int k = 0; k < tri.Length; k++) tri[k] = r.ReadUInt16();
            g._regionPortals[region] = ps;
            g._regionCosts[region] = tri;
        }
        if (!SetIdOf(list).AsSpan().SequenceEqual(g.SetId)) throw new InvalidDataException("graph set id doesn't match its own tile list");
        return g;
    }

    // ── Routing ──────────────────────────────────────────────────────────────────

    /// <summary>Walking cost inside <paramref name="region"/> between its portals (local indices); +inf when unknown.</summary>
    private float Cost(int region, int i, int j)
    {
        if (i == j) return 0;
        if (i > j) (i, j) = (j, i);
        int n = _regionPortals[region]!.Length;
        int k = i * (2 * n - i - 1) / 2 + (j - i - 1);
        ushort c = _regionCosts[region]![k];
        return c == NoCost ? float.PositiveInfinity : c * CostUnit;
    }

    private float Leg(Func<int, float>? leg, int p, float x, float z)
    {
        float c = leg?.Invoke(p) ?? float.NaN;
        return float.IsNaN(c) ? Dist(x, z, _px[p], _pz[p]) : c;
    }

    private static float Dist(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    /// <summary>
    /// A* from a point in <paramref name="startRegion"/> to a point in <paramref name="goalRegion"/>.
    /// Returns the portals to cross, in order (empty when both are the same region), or null when
    /// the graph has no route. Costs: walking distances inside regions; straight lines for the first
    /// and last legs (inside the start and goal regions) unless <paramref name="startLeg"/> /
    /// <paramref name="goalLeg"/> give the real walking distance from the start to a portal and from
    /// a portal to the goal (NaN = not known: the straight line is used).
    /// </summary>
    public List<int>? FindRoute(int startRegion, float sx, float sz, int goalRegion, float gx, float gz,
                                out float cost, out int expanded, int maxExpand = 2_000_000,
                                Func<int, float>? startLeg = null, Func<int, float>? goalLeg = null)
    {
        cost = 0; expanded = 0;
        if ((uint)startRegion >= (uint)RegionCount || (uint)goalRegion >= (uint)RegionCount) return null;
        if (startRegion == goalRegion) return new List<int>();
        int[]? startPortals = _regionPortals[startRegion];
        if (startPortals == null || _regionPortals[goalRegion] == null) return null;

        // State s = portal*2 + side: crossed portal p into region (side 0 = A, 1 = B).
        var g = new Dictionary<int, float>();
        var from = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        float best = float.PositiveInfinity; int bestLast = -1;

        foreach (int p in startPortals)
        {
            int side = _pa[p] == startRegion ? 1 : 0;   // crossing p from the start region lands on the other side
            int s = p * 2 + side;
            float c = Leg(startLeg, p, sx, sz);
            if (!float.IsFinite(c)) continue;
            if (!g.TryGetValue(s, out float old) || c < old) { g[s] = c; from[s] = -1; open.Enqueue(s, c + Dist(_px[p], _pz[p], gx, gz)); }
        }

        while (open.TryDequeue(out int s, out float f))
        {
            if (f >= best) break;
            if (++expanded > maxExpand) break;
            float gs = g[s];
            int p = s >> 1, side = s & 1;
            int region = side == 0 ? _pa[p] : _pb[p];
            if (f > gs + Dist(_px[p], _pz[p], gx, gz) + 0.01f) continue;   // stale: a cheaper way here was queued later
            if (region == goalRegion)
            {
                float total = gs + Leg(goalLeg, p, gx, gz);
                if (total < best) { best = total; bestLast = s; }
                continue;
            }
            int[]? ps = _regionPortals[region];
            if (ps == null) continue;
            int li = Array.IndexOf(ps, p);
            if (li < 0) continue;
            for (int j = 0; j < ps.Length; j++)
            {
                if (j == li) continue;
                float c = Cost(region, li, j);
                if (!float.IsFinite(c)) continue;
                int q = ps[j];
                int qside = _pa[q] == region ? 1 : 0;
                int t = q * 2 + qside;
                float ng = gs + c;
                if (g.TryGetValue(t, out float og) && og <= ng) continue;
                g[t] = ng; from[t] = s;
                open.Enqueue(t, ng + Dist(_px[q], _pz[q], gx, gz));
            }
        }
        if (bestLast < 0) return null;
        cost = best;
        var route = new List<int>();
        for (int s = bestLast; s >= 0; s = from[s]) route.Add(s >> 1);
        route.Reverse();
        return route;
    }

    // ── Landmasses ───────────────────────────────────────────────────────────────

    private int[]? _component;              // per region: its landmass (connected through portals)
    private int[]? _componentSize;          // regions per landmass
    private Dictionary<ushort, int[]>? _landblockComponents;

    /// <summary>
    /// Groups the regions into landmasses: regions joined by portals, directly or through others.
    /// Two places on different landmasses can't be walked between (water, cliffs). Built once (the
    /// plugin calls it on its loading worker); after that the graph is read-only again.
    /// </summary>
    public void BuildComponents()
    {
        if (_component != null) return;
        int n = RegionCount;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (int p = 0; p < _pa.Length; p++)
        {
            int a = Find(_pa[p]), b = Find(_pb[p]);
            if (a != b) parent[a] = b;
        }
        var comp = new int[n];
        var size = new int[n];
        for (int i = 0; i < n; i++) { comp[i] = Find(i); size[comp[i]]++; }
        var byLb = new Dictionary<ushort, int[]>(_tiles.Count);
        var tmp = new List<int>();
        foreach (var (lb, t) in _tiles)
        {
            tmp.Clear();
            for (int k = 0; k < t.RegionCount; k++) { int c = comp[t.RegionBase + k]; if (!tmp.Contains(c)) tmp.Add(c); }
            byLb[lb] = tmp.ToArray();
        }
        _componentSize = size;
        _landblockComponents = byLb;
        _component = comp;
    }

    /// <summary>A region's landmass (-1 before BuildComponents, or a bad region).</summary>
    public int Component(int region) => _component != null && (uint)region < (uint)_component.Length ? _component[region] : -1;

    /// <summary>How many regions a landmass has.</summary>
    public int ComponentSize(int component) => _componentSize != null && (uint)component < (uint)_componentSize.Length ? _componentSize[component] : 0;

    /// <summary>The landmasses with ground in landblock <paramref name="lb"/> (empty: no tile, or not built yet).</summary>
    public int[] LandblockComponents(uint lb) =>
        _landblockComponents != null && _landblockComponents.TryGetValue((ushort)lb, out int[]? c) ? c : Array.Empty<int>();

    /// <summary>
    /// Can you walk from landblock <paramref name="a"/> to landblock <paramref name="b"/>? True when
    /// they share a landmass, false when they share none, null when either has no tile (unknown).
    /// Landmasses of fewer than <paramref name="minRegions"/> regions (a roof, a ledge, a rock) only
    /// count when a landblock has nothing bigger, so standing on a roof doesn't cut you off.
    /// </summary>
    public bool? SameLand(uint a, uint b, int minRegions = 8)
    {
        int[] ca = Big(LandblockComponents(a), minRegions), cb = Big(LandblockComponents(b), minRegions);
        if (ca.Length == 0 || cb.Length == 0) return null;
        foreach (int x in ca) if (Array.IndexOf(cb, x) >= 0) return true;
        return false;
    }

    private int[] Big(int[] comps, int minRegions)
    {
        if (comps.Length <= 1) return comps;
        int big = 0;
        foreach (int c in comps) if (ComponentSize(c) >= minRegions) big++;
        if (big == 0 || big == comps.Length) return comps;
        var r = new int[big];
        int i = 0;
        foreach (int c in comps) if (ComponentSize(c) >= minRegions) r[i++] = c;
        return r;
    }

    /// <summary>The landblock under a world point (east, north), or uint.MaxValue off the map.</summary>
    public static uint LandblockAt(double ew, double ns)
    {
        int x = (int)Math.Floor(ew / 192.0), y = (int)Math.Floor(ns / 192.0);
        return x is >= 0 and <= 255 && y is >= 0 and <= 255 ? (uint)((x << 8) | y) : uint.MaxValue;
    }

    /// <summary>The region a route's portal leads into (crossing it in route order from <paramref name="previousRegion"/>).</summary>
    public int Across(int portal, int previousRegion) => _pa[portal] == previousRegion ? _pb[portal] : _pa[portal];
}
