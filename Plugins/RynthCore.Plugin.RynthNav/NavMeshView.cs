using System;
using System.Collections.Generic;
using DotRecast.Detour;
using RynthCore.Plugin.Shared;

namespace RynthNav.Routing;

/// <summary>
/// The navmesh drawn in the world: where you can walk, and where the mesh ends.
///
///  - Floor: the walkable ground, as translucent triangles lying on it (the tile's detail mesh,
///    so they follow the terrain and the building floors).
///  - Walls: every polygon edge with no neighbour, as an upright strip of triangles. Those edges are
///    exactly where the baker said "not here": buildings, trees, rocks, cliffs, steep slopes, water,
///    and the body-width margin round them. Where there is no green and a red wall stands, nav
///    will not take you.
///
/// Host-free (it writes to an <see cref="INav3DSink"/>), so the tests cover it. Tick thread only.
/// The tiles are registered as the plugin loads them (just a reference); a tile's triangles are
/// worked out the first time it is within range. The engine's Nav3D buffer holds 8192 triangles
/// for every plugin, so a draw is capped (<see cref="Options.MaxTriangles"/>), nearest first.
/// </summary>
internal sealed class NavMeshView
{
    public const float TileSize = 192f;

    /// <summary>One tile's triangles, relative to the tile's south-west corner (precise in float at any landblock).</summary>
    internal sealed class Tile
    {
        public int OriginX, OriginY;                 // world metres of the corner (landblock x/y * 192)
        public float[] Floor = Array.Empty<float>(); // 9 per triangle: (east, up, north) x3
        public float[] Walls = Array.Empty<float>(); // 6 per edge: (east, up, north) of its two ends
        public int FloorCount => Floor.Length / 9;
        public int WallCount => Walls.Length / 6;
    }

    internal readonly struct Options
    {
        public readonly float Radius;        // metres from the character
        public readonly int MaxTriangles;    // for floor and walls together (a wall strip is two)
        public readonly bool Floor, Walls;
        public readonly float Lift;          // floor and wall bottoms above the mesh, so they don't fight the ground
        public readonly float WallHeight;
        public readonly float MaxUpDown;     // ignore mesh further above/below the character than this (other levels)
        public readonly uint FloorColor, WallColor;
        public readonly int Layer;           // 0 = the outdoor tiles, 1 = the dungeon's: only one is drawn
        public readonly double FrameX, FrameY;   // world metres of the landblock whose local frame Nav3D is in; NaN = the one the character stands in (outdoors)

        public Options(float radius, int maxTriangles, bool floor, bool walls, float lift = 0.15f, float wallHeight = 1.5f,
            float maxUpDown = 20f, uint floorColor = DefaultFloorColor, uint wallColor = DefaultWallColor,
            int layer = 0, double frameX = double.NaN, double frameY = double.NaN)
        {
            Radius = radius; MaxTriangles = maxTriangles; Floor = floor; Walls = walls; Lift = lift;
            WallHeight = wallHeight; MaxUpDown = maxUpDown; FloorColor = floorColor; WallColor = wallColor;
            Layer = layer; FrameX = frameX; FrameY = frameY;
        }
    }

    public const uint DefaultFloorColor = 0x3A30E060;   // translucent green
    public const uint DefaultWallColor = 0x78FF3A2A;    // translucent red

    // A wall counts as nearer than it is when choosing what to keep under the cap: a wall is what
    // you most want to see, and one strip costs two triangles.
    private const float WallPriority = 0.25f;
    // The pick is kept until the character moves this far (or a tile comes or goes).
    private const float RepickMetres = 1.5f;

    // One registered tile: its data, where its corner is on the mesh's own frame (what the vertices are
    // measured from) and in the world, and which layer it is drawn in.
    private sealed class Reg
    {
        public DtMeshData Md = null!;
        public int MeshOx, MeshOy, WorldOx, WorldOy, Layer;
    }
    private const uint DungeonKeyBase = 0x100000;

    private readonly Dictionary<uint, Reg> _data = new();
    private readonly Dictionary<uint, Tile?> _built = new();
    private readonly List<uint> _dungeonKeys = new();
    private int _version;
    private int _pickLayer = -1;

    private readonly struct Pick
    {
        public readonly Tile T; public readonly int Index; public readonly bool Wall; public readonly float Key;
        public Pick(Tile t, int index, bool wall, float key) { T = t; Index = index; Wall = wall; Key = key; }
    }
    private readonly List<Pick> _picks = new();
    private readonly List<Pick> _cands = new();
    private int _pickVersion = -1;
    private double _pickX = double.NaN, _pickY;
    private float _pickRadius;
    private int _pickBudget;
    private bool _pickFloor, _pickWalls;
    private float _pickUp = float.NaN;

    /// <summary>Triangles / wall strips submitted by the last <see cref="Emit"/>.</summary>
    public int LastTriangles { get; private set; }
    public int TileCount => _data.Count;

    /// <summary>An outdoor tile (nav_LLLL.tile): its vertices are world metres, its corner is the landblock's.</summary>
    public void Register(uint lb, DtMeshData md)
    {
        int ox = (int)((lb >> 8) & 0xFF) * (int)TileSize, oy = (int)(lb & 0xFF) * (int)TileSize;
        _data[lb] = new Reg { Md = md, MeshOx = ox, MeshOy = oy, WorldOx = ox, WorldOy = oy, Layer = 0 };
        _built.Remove(lb); _version++;
    }

    public void Unregister(uint lb)
    {
        if (_data.Remove(lb)) { _built.Remove(lb); _version++; }
    }

    /// <summary>The landblock whose dungeon is registered (0 = none).</summary>
    public uint DungeonLandblock { get; private set; }

    /// <summary>
    /// A dungeon's tiles (dun_LLLL.tile), replacing any dungeon registered before. Their vertices are on the
    /// file's shifted frame (landblock-local + shift); the world corner of a tile is the landblock's plus the
    /// tile's mesh corner minus the shift.
    /// </summary>
    public void RegisterDungeon(uint lb, DungeonTileFile f)
    {
        UnregisterDungeon();
        double lbX = ((lb >> 8) & 0xFF) * (double)TileSize, lbY = (lb & 0xFF) * (double)TileSize;
        for (int i = 0; i < f.Tiles.Count; i++)
        {
            DtMeshData md = f.Tiles[i];
            int mx = md.header.x * (int)TileSize, my = md.header.y * (int)TileSize;
            uint key = DungeonKeyBase + (uint)i;
            _data[key] = new Reg { Md = md, MeshOx = mx, MeshOy = my, WorldOx = (int)(lbX + mx - f.ShiftX), WorldOy = (int)(lbY + my - f.ShiftY), Layer = 1 };
            _built.Remove(key); _dungeonKeys.Add(key);
        }
        DungeonLandblock = lb; _version++;
    }

    public void UnregisterDungeon()
    {
        foreach (uint k in _dungeonKeys) { _data.Remove(k); _built.Remove(k); }
        if (_dungeonKeys.Count > 0 || DungeonLandblock != 0) _version++;
        _dungeonKeys.Clear(); DungeonLandblock = 0;
    }

    public void Clear()
    {
        _data.Clear(); _built.Clear(); _dungeonKeys.Clear(); DungeonLandblock = 0; _picks.Clear(); _cands.Clear(); _version++; LastTriangles = 0;
    }

    // ── Reading a tile ───────────────────────────────────────────────────────

    /// <summary>The floor triangles and wall edges of one baked tile.</summary>
    internal static Tile BuildTile(uint lb, DtMeshData md)
    {
        int ox = (int)((lb >> 8) & 0xFF) * (int)TileSize, oy = (int)(lb & 0xFF) * (int)TileSize;
        return BuildTile(md, ox, oy, ox, oy);
    }

    /// <summary>Vertices are measured from the tile's corner on the mesh's frame; the tile itself sits at its world corner.</summary>
    internal static Tile BuildTile(DtMeshData md, int meshOx, int meshOy, int worldOx, int worldOy)
    {
        var t = new Tile { OriginX = worldOx, OriginY = worldOy };
        float ox = meshOx, oy = meshOy;
        var floor = new List<float>(md.header.detailTriCount * 9);
        var walls = new List<float>();

        void Vtx(List<float> to, float x, float up, float ns) { to.Add(x - ox); to.Add(up); to.Add(ns - oy); }

        for (int i = 0; i < md.header.polyCount; i++)
        {
            DtPoly p = md.polys[i];
            if (p.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND) continue;
            int nv = p.vertCount;

            // Edges nothing is joined to (not a tile border: those carry DT_EXT_LINK, so they're nonzero).
            for (int j = 0; j < nv; j++)
            {
                if (p.neis[j] != 0) continue;
                int a = p.verts[j] * 3, b = p.verts[(j + 1) % nv] * 3;
                Vtx(walls, md.verts[a], md.verts[a + 1], md.verts[a + 2]);
                Vtx(walls, md.verts[b], md.verts[b + 1], md.verts[b + 2]);
            }

            // The ground: the polygon's detail triangles (they follow the height), else a fan.
            DtPolyDetail[]? dm = md.detailMeshes;
            if (dm != null && i < dm.Length && dm[i].triCount > 0)
            {
                DtPolyDetail pd = dm[i];
                for (int k = 0; k < pd.triCount; k++)
                    for (int c = 0; c < 3; c++)
                    {
                        int idx = md.detailTris[(pd.triBase + k) * 4 + c];
                        if (idx < nv) { int v = p.verts[idx] * 3; Vtx(floor, md.verts[v], md.verts[v + 1], md.verts[v + 2]); }
                        else { int v = (pd.vertBase + idx - nv) * 3; Vtx(floor, md.detailVerts[v], md.detailVerts[v + 1], md.detailVerts[v + 2]); }
                    }
            }
            else
            {
                for (int k = 1; k + 1 < nv; k++)
                    foreach (int idx in new[] { 0, k, k + 1 })
                    { int v = p.verts[idx] * 3; Vtx(floor, md.verts[v], md.verts[v + 1], md.verts[v + 2]); }
            }
        }
        t.Floor = floor.ToArray();
        t.Walls = walls.ToArray();
        return t;
    }

    private Tile? TileFor(uint key)
    {
        if (_built.TryGetValue(key, out Tile? t)) return t;
        t = _data.TryGetValue(key, out Reg? r) ? BuildTile(r.Md, r.MeshOx, r.MeshOy, r.WorldOx, r.WorldOy) : null;
        _built[key] = t;
        return t;
    }

    // ── Choosing what to draw ────────────────────────────────────────────────

    private void Repick(double px, double py, float pz, in Options o)
    {
        _picks.Clear(); _cands.Clear();
        float r = o.Radius, r2 = r * r;
        int cost = 0;

        foreach (var kv in _data)
        {
            uint lb = kv.Key;
            if (kv.Value.Layer != o.Layer) continue;
            float x0 = kv.Value.WorldOx, y0 = kv.Value.WorldOy;
            // Distance from the character to the tile's square.
            double dx = Math.Max(Math.Max(x0 - px, px - (x0 + TileSize)), 0), dy = Math.Max(Math.Max(y0 - py, py - (y0 + TileSize)), 0);
            if (dx * dx + dy * dy > (double)r2) continue;
            Tile? t = TileFor(lb);
            if (t == null) continue;
            float ox = t.OriginX - (float)px, oy = t.OriginY - (float)py;   // tile corner relative to the character

            if (o.Floor)
            {
                float[] f = t.Floor;
                for (int k = 0, n = t.FloorCount; k < n; k++)
                {
                    int b = k * 9;
                    float cx = ox + (f[b] + f[b + 3] + f[b + 6]) / 3f, cy = oy + (f[b + 2] + f[b + 5] + f[b + 8]) / 3f;
                    float d2 = cx * cx + cy * cy;
                    if (d2 > r2) continue;
                    if (Math.Abs((f[b + 1] + f[b + 4] + f[b + 7]) / 3f - pz) > o.MaxUpDown) continue;
                    _cands.Add(new Pick(t, k, false, d2));
                    cost++;
                }
            }
            if (o.Walls)
            {
                float[] w = t.Walls;
                for (int k = 0, n = t.WallCount; k < n; k++)
                {
                    int b = k * 6;
                    float cx = ox + (w[b] + w[b + 3]) * 0.5f, cy = oy + (w[b + 2] + w[b + 5]) * 0.5f;
                    float d2 = cx * cx + cy * cy;
                    if (d2 > r2) continue;
                    if (Math.Abs((w[b + 1] + w[b + 4]) * 0.5f - pz) > o.MaxUpDown) continue;
                    _cands.Add(new Pick(t, k, true, d2 * WallPriority));
                    cost += 2;
                }
            }
        }

        if (cost > o.MaxTriangles) _cands.Sort((a, b) => a.Key.CompareTo(b.Key));
        int used = 0;
        foreach (Pick c in _cands)
        {
            int c2 = c.Wall ? 2 : 1;
            if (used + c2 > o.MaxTriangles) { if (cost > o.MaxTriangles) break; continue; }
            _picks.Add(c);
            used += c2;
        }
        _cands.Clear();
        _pickVersion = _version; _pickX = px; _pickY = py; _pickUp = pz;
        _pickRadius = o.Radius; _pickBudget = o.MaxTriangles; _pickFloor = o.Floor; _pickWalls = o.Walls; _pickLayer = o.Layer;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Submits the mesh near the character (world metres east/north/up) to the sink, in the Nav3D
    /// frame: the character's landblock, X = east, Y = height, Z = north. Returns triangles submitted.
    /// </summary>
    public int Emit(INav3DSink sink, double px, double py, double pz, in Options o)
    {
        LastTriangles = 0;
        if (_data.Count == 0 || o.MaxTriangles <= 0 || (!o.Floor && !o.Walls)) return 0;

        double dxm = px - _pickX, dym = py - _pickY;
        bool stale = _pickVersion != _version || double.IsNaN(_pickX) || dxm * dxm + dym * dym > RepickMetres * RepickMetres
            || Math.Abs(pz - _pickUp) > 2f || o.Radius != _pickRadius || o.MaxTriangles != _pickBudget
            || o.Floor != _pickFloor || o.Walls != _pickWalls || o.Layer != _pickLayer;
        if (stale) Repick(px, py, (float)pz, o);

        // Outdoors the character is inside its landblock, so its corner is the frame; in a dungeon the
        // character's local x/y can be anywhere (down to -360), and the caller names the landblock.
        double baseX = double.IsNaN(o.FrameX) ? Math.Floor(px / TileSize) * TileSize : o.FrameX;
        double baseY = double.IsNaN(o.FrameY) ? Math.Floor(py / TileSize) * TileSize : o.FrameY;
        int tris = 0;
        foreach (Pick c in _picks)
        {
            Tile t = c.T;
            float ox = (float)(t.OriginX - baseX), oy = (float)(t.OriginY - baseY);
            if (!c.Wall)
            {
                float[] f = t.Floor; int b = c.Index * 9;
                sink.Triangle(ox + f[b], f[b + 1] + o.Lift, oy + f[b + 2],
                              ox + f[b + 3], f[b + 4] + o.Lift, oy + f[b + 5],
                              ox + f[b + 6], f[b + 7] + o.Lift, oy + f[b + 8], o.FloorColor);
                tris++;
            }
            else
            {
                float[] w = t.Walls; int b = c.Index * 6;
                float ax = ox + w[b], ay = w[b + 1] + o.Lift, az = oy + w[b + 2];
                float bx = ox + w[b + 3], by = w[b + 4] + o.Lift, bz = oy + w[b + 5];
                sink.Triangle(ax, ay, az, bx, by, bz, bx, by + o.WallHeight, bz, o.WallColor);
                sink.Triangle(ax, ay, az, bx, by + o.WallHeight, bz, ax, ay + o.WallHeight, az, o.WallColor);
                tris += 2;
            }
        }
        LastTriangles = tris;
        return tris;
    }
}
