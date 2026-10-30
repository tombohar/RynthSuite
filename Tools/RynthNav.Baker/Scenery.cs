using System.Reflection;
using RynthCore.TerrainData;
using RynthCore.Plugin.RynthAi.Raycasting;
using AcFrame = RynthCore2.TerrainData.Frame;
using V3 = System.Numerics.Vector3;

namespace RynthNav.Baker;

/// <summary>
/// Scenery (trees, rocks, bushes) placed exactly as ACE's Landblock.get_land_scenes places it on
/// the server: the same pseudo-random scene pick, frequency and displacement as before, plus the
/// rules the old scatter path left out: nothing on a road (OnRoad), nothing in a land cell that
/// holds a building (has_building, one 24 m cell per building), nothing on a slope outside the
/// object's MinSlope..MaxSlope, nothing poking out of the landblock (obj_within_block), the AC
/// heading convention for rotation, slope alignment, and the per-object scale (ScaleObj).
/// The old path dropped scenery inside an expanded bounding box of every big static object
/// instead, which removed trees the game has next to large buildings and kept ones it doesn't.
/// </summary>
internal sealed class Scenery
{
    private readonly RynthCore2.TerrainData.DatDatabase _portal;
    private readonly List<RynthCore2.Raycast.ScatterTerrainType>? _terrainTypes;
    private readonly List<RynthCore2.Raycast.ScatterSceneType>? _sceneTypes;
    private readonly Dictionary<uint, List<SceneObj>> _scenes = new();

    public record SceneObj(uint ObjId, AcFrame BaseLoc, float Freq, float DisplaceX, float DisplaceY, float MinScale, float MaxScale,
        float MaxRotation, float MinSlope, float MaxSlope, uint Align, uint WeenieObj);

    public bool Ready => _terrainTypes != null && _sceneTypes != null;

    public Scenery(RynthCore2.Raycast.GeometryLoader geo)
    {
        _portal = geo.PortalDat;
        // The region's terrain -> scene tables are already parsed by the shared ScatterSystem
        // (RegionDesc 0x13000000); it keeps them private, so read them by reflection.
        var ss = geo.ScatterSystemRef;
        if (ss == null || !ss.IsInitialized) return;
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        _terrainTypes = typeof(RynthCore2.Raycast.ScatterSystem).GetField("_terrainTypes", F)?.GetValue(ss) as List<RynthCore2.Raycast.ScatterTerrainType>;
        _sceneTypes = typeof(RynthCore2.Raycast.ScatterSystem).GetField("_sceneTypes", F)?.GetValue(ss) as List<RynthCore2.Raycast.ScatterSceneType>;
    }

    private List<SceneObj> Scene(uint id)
    {
        if (_scenes.TryGetValue(id, out var list)) return list;
        list = new List<SceneObj>();
        byte[]? data = _portal.GetFileData(id);
        if (data != null && data.Length >= 8)
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadUInt32();
            uint n = r.ReadUInt32();
            for (uint i = 0; i < n && ms.Position + 76 <= ms.Length; i++)   // ObjectDesc: 4 + Frame 28 + 8 floats + 3 uints
            {
                uint objId = r.ReadUInt32();
                var bl = new AcFrame(); bl.Unpack(r);
                list.Add(new SceneObj(objId, bl, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                    r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadUInt32(), SkipOrient(r)));
            }
        }
        _scenes[id] = list;
        return list;
    }
    private static uint SkipOrient(BinaryReader r) { r.ReadUInt32(); return r.ReadUInt32(); }   // Orient, then WeenieObj

    public record Placed(uint ObjId, AcFrame Frame, float Scale);

    /// <summary>The scenery of one landblock. <paramref name="buildingCells"/>: land cells (x*8+y) holding a building.</summary>
    public List<Placed> Place(uint lb, LandblockData land, HashSet<int> buildingCells, Func<uint, (V3 c, float r, bool bsp, List<(V3 low, float r)> cyl, bool sph)> shape)
    {
        var result = new List<Placed>();
        if (!Ready || land.TerrainWords == null) return result;
        ushort[] terrain = land.TerrainWords;
        uint blockX = ((lb >> 8) & 0xFF) * 8, blockY = (lb & 0xFF) * 8;
        unchecked
        {
            for (uint i = 0; i < 81; i++)
            {
                int terrainType = (terrain[i] >> 2) & 0x1F;
                int sceneType = terrain[i] >> 11;
                if (terrainType >= _terrainTypes!.Count) continue;
                var tt = _terrainTypes[terrainType];
                if (sceneType >= tt.SceneTypeIndices.Count) continue;
                int sceneInfo = (int)tt.SceneTypeIndices[sceneType];
                if (sceneInfo >= _sceneTypes!.Count) continue;
                var scenes = _sceneTypes[sceneInfo].SceneFileIds;
                if (scenes.Count == 0) continue;

                uint cellX = i / 9, cellY = i % 9;
                uint gx = cellX + blockX, gy = cellY + blockY;
                uint cellMat = gy * (712977289u * gx + 1813693831u) - 1109124029u * gx + 2139937281u;
                double offset = cellMat * 2.3283064e-10;
                int sceneIdx = (int)(scenes.Count * offset);
                if (sceneIdx >= scenes.Count || sceneIdx < 0) sceneIdx = 0;
                var objs = Scene(scenes[sceneIdx]);

                uint cellXMat = 0u - 1109124029u * gx;
                uint cellYMat = 1813693831u * gy;
                uint cellMat2 = 1360117743u * gx * gy + 1888038839u;
                for (uint j = 0; j < objs.Count; j++)
                {
                    var obj = objs[(int)j];
                    double noise = (uint)(cellXMat + cellYMat - cellMat2 * (23399u + j)) * 2.3283064e-10;
                    if (!(noise < obj.Freq) || obj.WeenieObj != 0) continue;

                    var pos = Displace(obj, gx, gy, j);
                    float lx = cellX * 24f + pos.X, ly = cellY * 24f + pos.Y;
                    if (lx < 0 || ly < 0 || lx >= 192f || ly >= 192f || OnRoad(terrain, lx, ly)) continue;
                    int cx = Math.Min((int)(lx / 24f), 7), cy = Math.Min((int)(ly / 24f), 7);
                    if (buildingCells.Contains(cx * 8 + cy)) continue;

                    V3 n = TerrainNormal(land, lb, lx, ly);
                    if (!(n.Z >= obj.MinSlope && n.Z <= obj.MaxSlope)) continue;
                    float z = TerrainSampler.GetTerrainZ(land, lx, ly);

                    float heading;
                    bool setHeading;
                    if (obj.Align != 0) { heading = Heading(-n.X, -n.Y); setHeading = true; }
                    else if (obj.MaxRotation > 0f)
                    {
                        heading = (float)((uint)(1813693831u * gy - (j + 63127u) * (1360117743u * gy * gx + 1888038839u) - 1109124029u * gx) * 2.3283064e-10 * obj.MaxRotation);
                        setHeading = true;
                    }
                    else { heading = 0; setHeading = false; }
                    var frame = new AcFrame
                    {
                        OriginX = lx, OriginY = ly, OriginZ = z,
                        RotW = obj.BaseLoc.RotW, RotX = obj.BaseLoc.RotX, RotY = obj.BaseLoc.RotY, RotZ = obj.BaseLoc.RotZ,
                    };
                    if (setHeading)
                    {
                        // AFrame.set_heading: heading 0 = north (+Y), clockwise; a rotation about Z by -heading.
                        float half = -heading * MathF.PI / 180f * 0.5f;
                        frame.RotW = MathF.Cos(half); frame.RotX = 0; frame.RotY = 0; frame.RotZ = MathF.Sin(half);
                    }

                    if (!WithinBlock(frame, shape(obj.ObjId))) continue;
                    result.Add(new Placed(obj.ObjId, frame, ScaleObj(obj, gx, gy, j)));
                }
            }
        }
        return result;
    }

    // ── ACE ObjectDesc maths (uint arithmetic wraps, as in the client) ─────────
    private static V3 Displace(SceneObj obj, uint ix, uint iy, uint iq)
    {
        unchecked
        {
            float x = obj.DisplaceX <= 0 ? obj.BaseLoc.OriginX
                : (float)((uint)(1813693831u * iy - (iq + 45773u) * (1360117743u * iy * ix + 1888038839u) - 1109124029u * ix) * 2.3283064e-10 * obj.DisplaceX + obj.BaseLoc.OriginX);
            float y = obj.DisplaceY <= 0 ? obj.BaseLoc.OriginY
                : (float)((uint)(1813693831u * iy - (iq + 72719u) * (1360117743u * iy * ix + 1888038839u) - 1109124029u * ix) * 2.3283064e-10 * obj.DisplaceY + obj.BaseLoc.OriginY);
            float z = obj.BaseLoc.OriginZ;
            double quadrant = (uint)(1813693831u * iy - ix * (1870387557u * iy + 1109124029u) - 402451965u) * 2.3283064e-10;
            if (quadrant >= 0.75) return new V3(y, -x, z);
            if (quadrant >= 0.5) return new V3(-x, -y, z);
            if (quadrant >= 0.25) return new V3(-y, x, z);
            return new V3(x, y, z);
        }
    }

    private static float ScaleObj(SceneObj obj, uint x, uint y, uint k)
    {
        if (obj.MinScale == obj.MaxScale) return obj.MaxScale;
        unchecked
        {
            double t = (uint)(1813693831u * y - (k + 32593u) * (1360117743u * y * x + 1888038839u) - 1109124029u * x) * 2.3283064e-10;
            return (float)(Math.Pow(obj.MaxScale / obj.MinScale, t) * obj.MinScale);
        }
    }

    /// <summary>Vector3.get_heading: degrees, 0 = +Y (north), clockwise.</summary>
    private static float Heading(float x, float y)
    {
        float len = MathF.Sqrt(x * x + y * y);
        if (len < 1e-6f) return 0;
        return (450f - MathF.Atan2(y / len, x / len) * 180f / MathF.PI) % 360f;
    }

    /// <summary>Landblock.OnRoad (ACE): road bits of the four corners of the 24 m tile, 5 m road width.</summary>
    private static bool OnRoad(ushort[] terrain, float ox, float oy)
    {
        const float tile = 24f, rMin = 5f, rMax = tile - rMin;
        int x = (int)(ox / tile), y = (int)(oy / tile);
        uint R(int vx, int vy) => (vx < 0 || vx > 8 || vy < 0 || vy > 8) ? 0u : (uint)(terrain[vx * 9 + vy] & 3);
        uint r0 = R(x, y), r1 = R(x, y + 1), r2 = R(x + 1, y), r3 = R(x + 1, y + 1);
        if (r0 == 0 && r1 == 0 && r2 == 0 && r3 == 0) return false;
        float dx = ox - x * tile, dy = oy - y * tile;
        if (r0 > 0)
        {
            if (r1 > 0)
            {
                if (r2 > 0) return r3 > 0 || dx < rMin || dy < rMin;
                return r3 > 0 ? (dx < rMin || dy > rMax) : dx < rMin;
            }
            if (r2 > 0) return r3 > 0 ? (dx > rMax || dy < rMin) : dy < rMin;
            return r3 > 0 ? Math.Abs(dx - dy) < rMin : dx + dy < rMin;
        }
        if (r1 > 0)
        {
            if (r2 > 0) return r3 > 0 ? (dx > rMax || dy > rMax) : Math.Abs(dx + dy - tile) < rMin;
            return r3 > 0 ? dy > rMax : tile + dx - dy < rMin;
        }
        if (r2 > 0) return r3 > 0 ? dx > rMax : tile - dx + dy < rMin;
        return r3 > 0 && tile * 2f - dx - dy < rMin;
    }

    /// <summary>Upward unit normal of the terrain triangle under (lx, ly), on AC's per-cell split.</summary>
    private static V3 TerrainNormal(LandblockData land, uint lb, float lx, float ly)
    {
        int cx = Math.Min((int)(lx / 24f), 7), cy = Math.Min((int)(ly / 24f), 7);
        float u = (lx - cx * 24f) / 24f, v = (ly - cy * 24f) / 24f;
        V3 sw = new(0, 0, land.GetVertexZ(cx, cy)), se = new(24, 0, land.GetVertexZ(cx + 1, cy));
        V3 nw = new(0, 24, land.GetVertexZ(cx, cy + 1)), ne = new(24, 24, land.GetVertexZ(cx + 1, cy + 1));
        V3 a, b, c;
        if (TerrainSampler.SwToNeCut(lb, cx, cy)) { if (v <= u) { a = sw; b = se; c = ne; } else { a = sw; b = ne; c = nw; } }
        else { if (u + v <= 1f) { a = sw; b = se; c = nw; } else { a = se; b = ne; c = nw; } }
        V3 n = V3.Normalize(V3.Cross(b - a, c - a));
        return n.Z < 0 ? -n : n;
    }

    /// <summary>PhysicsObj.obj_within_block (ACE), before scaling, as the server checks it.</summary>
    private static bool WithinBlock(AcFrame f, (V3 c, float r, bool bsp, List<(V3 low, float r)> cyl, bool sph) s)
    {
        V3 G(V3 p) { var w = f.TransformPoint(new RynthCore2.TerrainData.Vector3(p.X, p.Y, p.Z)); return new V3(w.X, w.Y, w.Z); }
        if (s.bsp)
        {
            V3 gc = G(s.c);
            return gc.X >= s.r && gc.Y >= s.r && gc.X < 192f - s.r && gc.Y < 192f - s.r;
        }
        if (s.cyl.Count > 0)
        {
            foreach (var (low, r) in s.cyl)
            {
                V3 gc = G(low);
                if (gc.X < r || gc.Y < r || gc.X >= 192f - r || gc.Y >= 192f - r) return false;
            }
            return true;
        }
        if (s.sph)
        {
            V3 gc = G(s.c);
            return gc.X >= s.r && gc.Y >= s.r && gc.X < 192f - s.r && gc.Y < 192f - s.r;
        }
        return f.OriginX >= 0 && f.OriginY >= 0 && f.OriginX < 192f && f.OriginY < 192f;
    }
}
