using System.Numerics;
using RynthCore2.TerrainData;
using V3 = System.Numerics.Vector3;

namespace RynthNav.Baker;

/// <summary>
/// What the game collides with, read from the dats: the PHYSICS polygons of every GfxObj
/// (not the drawing polygons), Setup cylinders/spheres, Setup default scales, the physics
/// polygons of the buildings' cells (EnvCell CellStructs) and the objects in them, and the
/// scenery the server places (Scenery). The baker used the drawing meshes before 2026-10-02:
/// they draw things that don't collide (Bandit Castle's gate arch is filled in) and leave out
/// what does (most building walls live in the cells, not in the building model).
/// </summary>
internal sealed class CollisionGeometry
{
    private readonly DatDatabase _portal, _cell;
    public CollisionGeometry(RynthCore2.Raycast.GeometryLoader geo) { Geo = geo; _portal = geo.PortalDat; _cell = geo.CellDat; }

    public record Placement(string Kind, uint Model, Frame Frame);

    /// <summary>Source id stamped on triangles made next (diagnostics only).</summary>
    [ThreadStatic] public static uint Tag;

    // ── GfxObj ────────────────────────────────────────────────────────────────
    public sealed class Mesh
    {
        public uint Flags;
        public Dictionary<int, V3> Verts = new();
        public List<int[]> Phys = new(), Draw = new();
    }
    private readonly Dictionary<uint, Mesh?> _gfx = new();

    public Mesh? GfxObj(uint id)
    {
        if (_gfx.TryGetValue(id, out var m)) return m;
        m = null;
        byte[]? data = _portal.GetFileData(id);
        if (data != null && data.Length >= 20)
        {
            try { m = ParseGfxObj(data); } catch { m = null; }
        }
        _gfx[id] = m;
        return m;
    }

    private static uint ReadCompressed(BinaryReader r)
    {
        byte b0 = r.ReadByte();
        if ((b0 & 0x80) == 0) return b0;
        byte b1 = r.ReadByte();
        if ((b0 & 0x40) == 0) return (uint)(((b0 & 0x7F) << 8) | b1);
        ushort s = r.ReadUInt16();
        return (uint)((((b0 & 0x3F) << 8) | b1) << 16 | s);
    }

    private static Mesh ParseGfxObj(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        var m = new Mesh();
        r.ReadUInt32();
        m.Flags = r.ReadUInt32();
        uint numSurf = ReadCompressed(r);
        ms.Seek(numSurf * 4L, SeekOrigin.Current);
        r.ReadInt32();                                          // VertexType
        uint numVerts = r.ReadUInt32();
        for (uint i = 0; i < numVerts; i++)
        {
            ushort key = r.ReadUInt16();
            ushort numUVs = r.ReadUInt16();
            var p = new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            ms.Seek(12 + numUVs * 8L, SeekOrigin.Current);      // normal + UVs
            m.Verts[key] = p;
        }
        List<int[]> ReadPolys()
        {
            var fs = new List<int[]>();
            uint nPoly = ReadCompressed(r);
            for (uint p = 0; p < nPoly; p++)
            {
                r.ReadUInt16();                                 // key
                byte numPts = r.ReadByte();
                byte stip = r.ReadByte();
                int sides = r.ReadInt32();
                r.ReadInt16(); r.ReadInt16();                   // pos/neg surface
                int[] vids = new int[numPts];
                for (int v = 0; v < numPts; v++) vids[v] = r.ReadInt16();
                if ((stip & 0x04) == 0) ms.Seek(numPts, SeekOrigin.Current);
                if ((stip & 0x08) == 0 && sides == 2) ms.Seek(numPts, SeekOrigin.Current);
                if (numPts >= 3) fs.Add(vids);
            }
            return fs;
        }
        if ((m.Flags & 0x01) != 0)
        {
            m.Phys = ReadPolys();
            SkipBsp(r, ms, BspKind.Physics);
        }
        ms.Seek(12, SeekOrigin.Current);                       // SortCenter
        if ((m.Flags & 0x02) != 0) m.Draw = ReadPolys();
        return m;
    }

    private enum BspKind { Drawing, Physics, Cell }

    // Steps over a BSP tree (ACE BSPNode/BSPLeaf/BSPPortal). Cell trees: planes only; Physics:
    // plus a sphere per node, and leaves carry solid + sphere + polys; Drawing: plus poly lists.
    private static void SkipBsp(BinaryReader r, MemoryStream ms, BspKind kind)
    {
        string tag = new string(System.Text.Encoding.ASCII.GetString(r.ReadBytes(4)).Reverse().ToArray());
        if (tag == "LEAF")
        {
            r.ReadInt32();                                      // leaf index
            if (kind == BspKind.Physics)
            {
                r.ReadInt32();                                  // solid
                ms.Seek(16, SeekOrigin.Current);                // sphere
                uint n = r.ReadUInt32();
                ms.Seek(n * 2L, SeekOrigin.Current);
            }
            return;
        }
        ms.Seek(16, SeekOrigin.Current);                        // splitting plane
        switch (tag)
        {
            case "PORT": SkipBsp(r, ms, kind); SkipBsp(r, ms, kind); break;
            case "BPnn": case "BPIn": SkipBsp(r, ms, kind); break;
            case "BpIN": case "BpnN": SkipBsp(r, ms, kind); break;
            case "BPIN": case "BPnN": SkipBsp(r, ms, kind); SkipBsp(r, ms, kind); break;
        }
        if (kind == BspKind.Cell) return;
        ms.Seek(16, SeekOrigin.Current);                        // sphere
        if (kind == BspKind.Physics) return;
        uint np = r.ReadUInt32();
        uint npp = tag == "PORT" ? r.ReadUInt32() : 0;          // a portal node: polys, then portal polys
        ms.Seek(np * 2L + npp * 4L, SeekOrigin.Current);
    }

    // ── Setup ─────────────────────────────────────────────────────────────────
    public sealed class Setup
    {
        public uint Flags;
        public uint[] Parts = Array.Empty<uint>();
        public V3[]? Scale;
        public Frame[]? Frames;
        public List<CollisionCylinder> Cyl = new();
        public List<CollisionSphere> Sph = new();
        public float Height, Radius;
        public V3 SortCenter; public float SortRadius;
        public float StepUp = float.NaN, StepDown = float.NaN;
    }
    private readonly Dictionary<uint, Setup?> _setups = new();

    public Setup? SetupModel(uint id)
    {
        if (_setups.TryGetValue(id, out var s)) return s;
        s = null;
        byte[]? data = _portal.GetFileData(id);
        if (data != null)
        {
            var info = new SetupInfo();
            try
            {
                if (info.Unpack(data) && info.PartIds != null)
                {
                    s = new Setup { Flags = info.Flags, Parts = info.PartIds, Frames = info.PartFrames, Cyl = info.Cylinders, Sph = info.Spheres, Height = info.Height, Radius = info.Radius,
                        SortCenter = new V3(info.BoundingSphereCenter.X, info.BoundingSphereCenter.Y, info.BoundingSphereCenter.Z), SortRadius = info.BoundingSphereRadius };
                    // StepUp/StepDown follow Height and Radius in the file; SetupInfo reads but drops them.
                    for (int o = data.Length - 8; o >= 16; o--)
                        if (BitConverter.ToSingle(data, o - 8) == info.Height && BitConverter.ToSingle(data, o - 4) == info.Radius && (info.Height != 0 || info.Radius != 0))
                        { s.StepUp = BitConverter.ToSingle(data, o); s.StepDown = BitConverter.ToSingle(data, o + 4); break; }
                    if ((info.Flags & 0x02) != 0)
                    {
                        int o = 12 + info.PartIds.Length * 4 + ((info.Flags & 0x01) != 0 ? info.PartIds.Length * 4 : 0);
                        s.Scale = new V3[info.PartIds.Length];
                        for (int i = 0; i < s.Scale.Length; i++, o += 12)
                            s.Scale[i] = new V3(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
                    }
                }
            }
            catch { s = null; }
        }
        _setups[id] = s;
        return s;
    }

    // ── LandBlockInfo ─────────────────────────────────────────────────────────
    /// <summary>LandBlockInfo (0xXXYYFFFE): static objects, then buildings.</summary>
    public List<Placement> ReadLandBlockInfo(uint lb)
    {
        var list = new List<Placement>();
        byte[]? data = _cell.GetFileData((lb << 16) | 0xFFFE);
        if (data == null || data.Length < 12) return list;
        uint numObjects = BitConverter.ToUInt32(data, 8);
        int pos = 12;
        for (uint i = 0; i < numObjects && pos + 32 <= data.Length; i++, pos += 32)
            list.Add(new Placement("obj", BitConverter.ToUInt32(data, pos), ReadFrame(data, pos + 4)));
        if (pos + 4 <= data.Length)
        {
            ushort numB = BitConverter.ToUInt16(data, pos); int b = pos + 4;
            for (int k = 0; k < numB && b + 40 <= data.Length; k++)
            {
                list.Add(new Placement("bld", BitConverter.ToUInt32(data, b), ReadFrame(data, b + 4)));
                b += 36;
                uint numPortals = BitConverter.ToUInt32(data, b); b += 4;
                for (uint pp = 0; pp < numPortals && b + 8 <= data.Length; pp++)
                {
                    ushort numStabs = BitConverter.ToUInt16(data, b + 6);
                    int psz = 8 + numStabs * 2; psz += (4 - (psz & 3)) & 3;
                    b += psz;
                }
            }
        }
        return list;
    }

    private static Frame ReadFrame(byte[] d, int o) => new Frame
    {
        OriginX = BitConverter.ToSingle(d, o), OriginY = BitConverter.ToSingle(d, o + 4), OriginZ = BitConverter.ToSingle(d, o + 8),
        RotW = BitConverter.ToSingle(d, o + 12), RotX = BitConverter.ToSingle(d, o + 16), RotY = BitConverter.ToSingle(d, o + 20), RotZ = BitConverter.ToSingle(d, o + 24),
    };

    // ── World triangles ───────────────────────────────────────────────────────
    public readonly struct Tri
    {
        public readonly V3 A, B, C;
        public readonly float MinX, MaxX, MinY, MaxY;
        /// <summary>Where it came from (model id, or EnvCell id), for diagnostics.</summary>
        public readonly uint Src;
        public Tri(V3 a, V3 b, V3 c)
        {
            A = a; B = b; C = c; Src = Tag;
            MinX = MathF.Min(a.X, MathF.Min(b.X, c.X)); MaxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
            MinY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)); MaxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        }
        /// <summary>Segment p0-p1 against the triangle (either side).</summary>
        public bool Hit(V3 p0, V3 p1, out V3 at)
        {
            at = default;
            V3 d = p1 - p0, e1 = B - A, e2 = C - A;
            V3 h = V3.Cross(d, e2);
            float det = V3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-9f) return false;
            float inv = 1f / det;
            V3 s = p0 - A;
            float u = V3.Dot(s, h) * inv;
            if (u < 0 || u > 1) return false;
            V3 q = V3.Cross(s, e1);
            float v = V3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return false;
            float t = V3.Dot(e2, q) * inv;
            if (t < 0 || t > 1) return false;
            at = p0 + d * t;
            return true;
        }
    }

    // ── Building interiors (EnvCells) ─────────────────────────────────────────
    public sealed class CellStruct { public Dictionary<int, V3> Verts = new(); public List<int[]> Phys = new(); }
    private readonly Dictionary<uint, Dictionary<uint, CellStruct>?> _envs = new();

    private Dictionary<uint, CellStruct>? Environment(uint id)
    {
        if (_envs.TryGetValue(id, out var e)) return e;
        e = null;
        byte[]? data = _portal.GetFileData(id);
        if (data != null)
        {
            try
            {
                using var ms = new MemoryStream(data);
                using var r = new BinaryReader(ms);
                r.ReadUInt32();
                uint n = r.ReadUInt32();
                e = new Dictionary<uint, CellStruct>();
                for (uint i = 0; i < n; i++)
                {
                    uint key = r.ReadUInt32();
                    e[key] = ParseCellStruct(r, ms);
                }
            }
            catch { e = null; }
        }
        _envs[id] = e;
        return e;
    }

    private static void Align4(MemoryStream ms) { long p = ms.Position; if ((p & 3) != 0) ms.Position = p + (4 - (p & 3)); }

    private static CellStruct ParseCellStruct(BinaryReader r, MemoryStream ms)
    {
        var cs = new CellStruct();
        uint numPolys = r.ReadUInt32(), numPhys = r.ReadUInt32(), numPortals = r.ReadUInt32();
        r.ReadInt32();                                          // VertexType
        uint numVerts = r.ReadUInt32();
        for (uint i = 0; i < numVerts; i++)
        {
            ushort key = r.ReadUInt16();
            ushort numUVs = r.ReadUInt16();
            var p = new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            ms.Seek(12 + numUVs * 8L, SeekOrigin.Current);
            cs.Verts[key] = p;
        }
        List<int[]> Polys(uint n)
        {
            var fs = new List<int[]>((int)n);
            for (uint p = 0; p < n; p++)
            {
                r.ReadUInt16();
                byte numPts = r.ReadByte(); byte stip = r.ReadByte();
                int sides = r.ReadInt32();
                r.ReadInt16(); r.ReadInt16();
                int[] vids = new int[numPts];
                for (int v = 0; v < numPts; v++) vids[v] = r.ReadInt16();
                if ((stip & 0x04) == 0) ms.Seek(numPts, SeekOrigin.Current);
                if ((stip & 0x08) == 0 && sides == 2) ms.Seek(numPts, SeekOrigin.Current);
                if (numPts >= 3) fs.Add(vids);
            }
            return fs;
        }
        Polys(numPolys);
        ms.Seek(numPortals * 2L, SeekOrigin.Current);
        Align4(ms);
        SkipBsp(r, ms, BspKind.Cell);
        cs.Phys = Polys(numPhys);
        SkipBsp(r, ms, BspKind.Physics);
        if (r.ReadUInt32() != 0) SkipBsp(r, ms, BspKind.Drawing);
        Align4(ms);
        return cs;
    }

    /// <summary>
    /// The cells of the landblock's buildings (EnvCells flagged SeenOutside): their walls and floors
    /// (CellStruct physics polygons) and the static objects inside them. Dungeon cells are left out
    /// (<see cref="AppendDungeonCells"/> reads those).
    /// Stab frames in an EnvCell are landblock positions already (ACE uses them as they are).
    /// </summary>
    public void AppendBuildingCells(uint lb, List<Tri> outTris, Func<Placement, bool>? objFilter = null)
        => AppendCells(lb, outTris, dungeon: false, objFilter);

    /// <summary>
    /// The cells of a dungeon landblock (EnvCells not flagged SeenOutside): the other half of
    /// <see cref="AppendBuildingCells"/>. Returns how many cells were read.
    /// </summary>
    public int AppendDungeonCells(uint lb, List<Tri> outTris, Func<Placement, bool>? objFilter = null)
    {
        int before = CellsRead;
        AppendCells(lb, outTris, dungeon: true, objFilter);
        return CellsRead - before;
    }

    /// <summary>How many EnvCells the landblock has that are dungeon cells (0 for a plain outdoor landblock).</summary>
    public int DungeonCellCount(uint lb)
    {
        byte[]? info = _cell.GetFileData((lb << 16) | 0xFFFE);
        if (info == null || info.Length < 8) return 0;
        uint numCells = BitConverter.ToUInt32(info, 4);
        int n = 0;
        for (uint c = 0; c < numCells && c < 0xFE00; c++)
        {
            byte[]? data = _cell.GetFileData((lb << 16) | (0x0100 + c));
            if (data != null && data.Length >= 20 && (BitConverter.ToUInt32(data, 4) & 0x01) == 0) n++;
        }
        return n;
    }

    private void AppendCells(uint lb, List<Tri> outTris, bool dungeon, Func<Placement, bool>? objFilter)
    {
        byte[]? info = _cell.GetFileData((lb << 16) | 0xFFFE);
        if (info == null || info.Length < 8) return;
        uint numCells = BitConverter.ToUInt32(info, 4);
        float offX = ((lb >> 8) & 0xFF) * 192f, offY = (lb & 0xFF) * 192f;
        for (uint c = 0; c < numCells && c < 0xFE00; c++)
        {
            byte[]? data = _cell.GetFileData((lb << 16) | (0x0100 + c));
            if (data == null || data.Length < 20) continue;
            try
            {
                using var ms = new MemoryStream(data);
                using var r = new BinaryReader(ms);
                r.ReadUInt32();
                uint flags = r.ReadUInt32();
                if (((flags & 0x01) == 0) != dungeon) continue; // SeenOutside = a building cell, else a dungeon cell
                r.ReadUInt32();
                byte numSurf = r.ReadByte(), numPortals = r.ReadByte();
                ushort numStabs = r.ReadUInt16();
                ms.Seek(numSurf * 2L, SeekOrigin.Current);
                uint envId = 0x0D000000u | r.ReadUInt16();
                ushort structIdx = r.ReadUInt16();
                var frame = new Frame(); frame.Unpack(r);
                ms.Seek(numPortals * 8L + numStabs * 2L, SeekOrigin.Current);
                Tag = (lb << 16) | (0x0100 + c);
                if (Environment(envId) is { } env && env.TryGetValue(structIdx, out var cs))
                {
                    CellsRead++;
                    foreach (var f in cs.Phys)
                    {
                        if (f.Any(k => !cs.Verts.ContainsKey(k))) continue;
                        V3 W(int k) { var w = frame.TransformPoint(new RynthCore2.TerrainData.Vector3(cs.Verts[k].X, cs.Verts[k].Y, cs.Verts[k].Z)); return new V3(w.X + offX, w.Y + offY, w.Z); }
                        V3 a = W(f[0]);
                        for (int t = 1; t < f.Length - 1; t++) outTris.Add(new Tri(a, W(f[t]), W(f[t + 1])));
                    }
                }
                if ((flags & 0x02) != 0)
                {
                    uint n = r.ReadUInt32();
                    for (uint i = 0; i < n && ms.Position + 32 <= ms.Length; i++)
                    {
                        uint id = r.ReadUInt32();
                        var f = new Frame(); f.Unpack(r);
                        var pl = new Placement("cellobj", id, f);
                        if (objFilter != null && !objFilter(pl)) continue;
                        CellObjects++;
                        int first = outTris.Count;
                        AppendModel(id, f, 1f, offX, offY, outTris);
                        // On a floor, at its origin: the base is where the origin stands, not the object's own lowest point (a
                        // panel modelled around its origin goes 1 m under the floor; measured from there it is 2 m tall and the
                        // part above the floor, 1 m, read as a step the mesh can climb: the Town Network's wooden panels).
                        RaiseIfLow(outTris, first, f.OriginZ);
                    }
                }
            }
            catch { CellsFailed++; }
        }
    }

    /// <summary>Counters for --audit: building cells read / not readable, objects placed in them.</summary>
    public int CellsRead, CellsFailed, CellObjects;

    // ── Scenery ───────────────────────────────────────────────────────────────
    private Scenery? _scenery;
    public RynthCore2.Raycast.GeometryLoader Geo { get; }

    /// <summary>Scenery the server places in this landblock (see <see cref="Scenery"/>), as collision.</summary>
    public List<Scenery.Placed> AppendScenery(uint lb, RynthCore.Plugin.RynthAi.Raycasting.LandblockData land, List<Tri> outTris)
    {
        _scenery ??= new Scenery(Geo);
        var buildingCells = new HashSet<int>();
        foreach (var p in ReadLandBlockInfo(lb))
            if (p.Kind == "bld")
                buildingCells.Add(Math.Clamp((int)(p.Frame.OriginX / 24f), 0, 7) * 8 + Math.Clamp((int)(p.Frame.OriginY / 24f), 0, 7));
        var placed = _scenery.Place(lb, land, buildingCells, Shape);
        float offX = ((lb >> 8) & 0xFF) * 192f, offY = (lb & 0xFF) * 192f;
        foreach (var p in placed)
        {
            int first = outTris.Count;
            AppendModel(p.ObjId, p.Frame, p.Scale, offX, offY, outTris);
            RaiseIfLow(outTris, first, p.Frame.OriginZ);   // scenery stands on the terrain
        }
        return placed;
    }

    public bool SceneryReady => (_scenery ??= new Scenery(Geo)).Ready;

    /// <summary>What obj_within_block needs: sorting sphere, BSP or not, cylinders, spheres.</summary>
    private (V3 c, float r, bool bsp, List<(V3 low, float r)> cyl, bool sph) Shape(uint model)
    {
        var none = new List<(V3, float)>();
        if ((model >> 24) == 0x01)
        {
            var g = GfxObj(model);
            if (g == null || g.Phys.Count == 0) return (V3.Zero, 0, false, none, false);
            var pts = g.Phys.SelectMany(f => f).Where(g.Verts.ContainsKey).Select(k => g.Verts[k]).ToList();
            if (pts.Count == 0) return (V3.Zero, 0, false, none, false);
            V3 mn = pts.Aggregate(V3.Min), mx = pts.Aggregate(V3.Max);
            return ((mn + mx) / 2, (mx - mn).Length() / 2, true, none, false);
        }
        var s = SetupModel(model);
        if (s == null) return (V3.Zero, 0, false, none, false);
        bool bsp = s.Parts.Any(pid => GfxObj(pid) is { } g && (g.Flags & 0x01) != 0 && g.Phys.Count > 0);
        var cyl = s.Cyl.Select(c => (new V3(c.BottomCenter.X, c.BottomCenter.Y, c.BottomCenter.Z), c.Radius)).ToList();
        return (s.SortCenter, s.SortRadius, bsp, cyl, s.Sph.Count > 0);
    }

    // ── Collision triangles of placed models ──────────────────────────────────
    /// <summary>LandBlockInfo objects and building shells of one landblock, as the game collides with them.</summary>
    public void AppendStatics(uint lb, List<Tri> outTris, Func<float, float, float>? groundLocal = null)
    {
        float offX = ((lb >> 8) & 0xFF) * 192f, offY = (lb & 0xFF) * 192f;
        foreach (var p in ReadLandBlockInfo(lb))
        {
            int first = outTris.Count;
            AppendModel(p.Model, p.Frame, 1f, offX, offY, outTris);
            // Outdoors the base is the ground under the object; in a dungeon (no terrain) it is where the object's origin stands.
            if (p.Kind == "obj") RaiseIfLow(outTris, first, groundLocal != null ? groundLocal(p.Frame.OriginX, p.Frame.OriginY) : p.Frame.OriginZ);
        }
    }

    /// <summary>
    /// The highest step a character takes (the human Setup 0x02000001's StepUpHeight). The navmesh
    /// keeps a 1 m climb so steep terrain stays connected, which on its own let the walker step onto
    /// posts, fences and low walls 0.6-1.0 m tall that stop a character.
    /// </summary>
    public const float StepUp = 0.60f;
    /// <summary>The navmesh's climb (set from NavBake); an object top within it reads as a step.</summary>
    public static float Climb = 1.0f;

    /// <summary>
    /// An object whose top is higher than a character can step (StepUp) but within the navmesh's
    /// climb would read as a step: add a copy of it raised so its top is 0.5 m above the climb, which
    /// makes it a wall to Recast. Base: the ground under the object when known, else its lowest point.
    /// </summary>
    public static void RaiseIfLow(List<Tri> tris, int first, float? groundZ)
    {
        if (tris.Count <= first) return;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = first; i < tris.Count; i++)
        {
            var t = tris[i];
            lo = MathF.Min(lo, MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z)));
            hi = MathF.Max(hi, MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z)));
        }
        float baseZ = groundZ is float g && !float.IsNaN(g) ? MathF.Max(lo, g) : lo;
        float h = hi - baseZ;
        if (!(h > StepUp + 0.02f) || h > Climb + 0.05f) return;
        var up = new V3(0, 0, Climb + 0.5f - h);   // 0.5 m clear of the climb: span tops are quantized to 0.2 m
        uint tag = Tag;
        int end = tris.Count;
        for (int i = first; i < end; i++)
        {
            var t = tris[i];
            Tag = t.Src;
            tris.Add(new Tri(t.A + up, t.B + up, t.C + up));
        }
        Tag = tag;
    }

    /// <summary>
    /// One placed model, the way ACE's PhysicsObj.FindObjCollisions picks its shape: the physics
    /// polygons of every part that has a physics BSP; a Setup with none of those collides with its
    /// cylinders, or failing that its spheres. <paramref name="scale"/> is the object's scale.
    /// </summary>
    public void AppendModel(uint model, Frame place, float scale, float offX, float offY, List<Tri> outTris)
    {
        Tag = model;
        V3 World(V3 p) { var w = place.TransformPoint(new RynthCore2.TerrainData.Vector3(p.X, p.Y, p.Z)); return new V3(w.X + offX, w.Y + offY, w.Z); }
        if ((model >> 24) == 0x01)
        {
            var g = GfxObj(model);
            if (g != null) AddPolys(g, V3.One * scale, null, World, outTris);
            return;
        }
        var s = SetupModel(model);
        if (s == null) return;
        bool anyBsp = false;
        for (int i = 0; i < s.Parts.Length; i++)
            if (GfxObj(s.Parts[i]) is { } g && (g.Flags & 0x01) != 0 && g.Phys.Count > 0) { anyBsp = true; break; }
        if (anyBsp)
        {
            for (int i = 0; i < s.Parts.Length; i++)
            {
                var g = GfxObj(s.Parts[i]);
                if (g == null || (g.Flags & 0x01) == 0 || g.Phys.Count == 0) continue;
                V3 sc = (s.Scale != null && i < s.Scale.Length ? s.Scale[i] : V3.One) * scale;
                Frame? pf = s.Frames != null && i < s.Frames.Length ? s.Frames[i] : null;
                AddPolys(g, sc, pf, p => World(p), outTris, scale);
            }
            return;
        }
        if (s.Cyl.Count > 0)
            foreach (var c in s.Cyl)
                AddPrism(new V3(c.BottomCenter.X, c.BottomCenter.Y, c.BottomCenter.Z) * scale, c.Radius * scale, 0, c.Height * scale, World, outTris);
        else
            foreach (var sp in s.Sph)
                AddPrism(new V3(sp.Center.X, sp.Center.Y, sp.Center.Z) * scale, sp.Radius * scale, -sp.Radius * scale, sp.Radius * scale, World, outTris);
    }

    private static void AddPolys(Mesh g, V3 partScale, Frame? partFrame, Func<V3, V3> world, List<Tri> outTris, float objScale = 1f)
    {
        V3 Local(int k)
        {
            V3 v = g.Verts[k] * partScale;
            if (partFrame == null) return v;
            var r = partFrame.RotatePoint(new RynthCore2.TerrainData.Vector3(v.X, v.Y, v.Z));
            return new V3(r.X + partFrame.OriginX * objScale, r.Y + partFrame.OriginY * objScale, r.Z + partFrame.OriginZ * objScale);
        }
        foreach (var f in g.Phys)
        {
            bool ok = true;
            foreach (int k in f) if (!g.Verts.ContainsKey(k)) { ok = false; break; }
            if (!ok) continue;
            V3 a = world(Local(f[0]));
            for (int t = 1; t < f.Length - 1; t++)
                outTris.Add(new Tri(a, world(Local(f[t])), world(Local(f[t + 1]))));
        }
    }

    /// <summary>A closed 8-sided prism (sides, top and bottom) standing in for a cylinder or sphere.</summary>
    private static void AddPrism(V3 center, float radius, float z0, float z1, Func<V3, V3> world, List<Tri> outTris)
    {
        if (!(radius > 0.01f) || !(z1 > z0)) return;
        const int N = 8;
        var lo = new V3[N]; var hi = new V3[N];
        for (int i = 0; i < N; i++)
        {
            float a = i * MathF.PI * 2 / N;
            float x = center.X + radius * MathF.Cos(a), y = center.Y + radius * MathF.Sin(a);
            lo[i] = world(new V3(x, y, center.Z + z0)); hi[i] = world(new V3(x, y, center.Z + z1));
        }
        V3 cl = world(new V3(center.X, center.Y, center.Z + z0)), ch = world(new V3(center.X, center.Y, center.Z + z1));
        for (int i = 0; i < N; i++)
        {
            int j = (i + 1) % N;
            outTris.Add(new Tri(lo[i], lo[j], hi[j])); outTris.Add(new Tri(lo[i], hi[j], hi[i]));
            outTris.Add(new Tri(ch, hi[i], hi[j])); outTris.Add(new Tri(cl, lo[j], lo[i]));
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────
    public IEnumerable<string> Describe(uint lb)
    {
        foreach (var p in ReadLandBlockInfo(lb))
        {
            string where = FormattableString.Invariant($"at ({p.Frame.OriginX:F1},{p.Frame.OriginY:F1},{p.Frame.OriginZ:F1})");
            foreach (var line in DescribeModel(p.Model, $"0x{lb:X4} {p.Kind}", where)) yield return line;
        }
    }

    public IEnumerable<string> DescribeModel(uint model, string head = "", string where = "")
    {
        if ((model >> 24) == 0x01)
        {
            yield return $"{head} gfx 0x{model:X8} {where} {DescribeGfx(GfxObj(model))}";
            yield break;
        }
        var s = SetupModel(model);
        if (s == null) { yield return $"{head} setup 0x{model:X8} {where} UNREADABLE"; yield break; }
        yield return FormattableString.Invariant($"{head} setup 0x{model:X8} {where} flags=0x{s.Flags:X} parts={s.Parts.Length} frames={(s.Frames?.Length ?? -1)} cyl={string.Join(";", s.Cyl.Select(c => $"r{c.Radius:F2}h{c.Height:F2}@{c.BottomCenter.X:F1},{c.BottomCenter.Y:F1},{c.BottomCenter.Z:F1}"))} sph={string.Join(";", s.Sph.Select(c => $"r{c.Radius:F2}@{c.Center.X:F1},{c.Center.Y:F1},{c.Center.Z:F1}"))} h={s.Height:F2} r={s.Radius:F2} stepUp={s.StepUp:F2} stepDown={s.StepDown:F2}{(s.Scale != null ? " scale=" + string.Join(";", s.Scale.Select(v => $"{v.X:F2},{v.Y:F2},{v.Z:F2}")) : "")}");
        for (int i = 0; i < s.Parts.Length; i++)
            yield return $"      part {i} 0x{s.Parts[i]:X8} {DescribeGfx(GfxObj(s.Parts[i]))}";
    }

    private static string DescribeGfx(Mesh? g)
    {
        if (g == null) return "UNREADABLE";
        string Box(List<int[]> polys)
        {
            if (polys.Count == 0) return "-";
            var pts = polys.SelectMany(f => f).Where(g.Verts.ContainsKey).Select(k => g.Verts[k]).ToList();
            var mn = pts.Aggregate(V3.Min); var mx = pts.Aggregate(V3.Max);
            return FormattableString.Invariant($"[{mn.X:F1},{mn.Y:F1},{mn.Z:F1}]..[{mx.X:F1},{mx.Y:F1},{mx.Z:F1}]");
        }
        return $"flags=0x{g.Flags:X} phys={g.Phys.Count} {Box(g.Phys)} draw={g.Draw.Count} {Box(g.Draw)}";
    }
}
