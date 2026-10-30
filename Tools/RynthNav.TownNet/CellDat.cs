using System.Numerics;
using DatDb = RynthCore.Plugin.RynthAi.Raycasting.DatDatabase;

namespace RynthNav.TownNet;

// Reads indoor cells from the dats: each EnvCell's frame, its cell portals (which cell is on the
// other side of which doorway polygon) and static objects, and the Environment's CellStruct for it
// (render polygons with the doorway polygons, physics polygons with the floors).
//
// Formats as in ACE (EnvCell.Unpack, Environment/CellStruct.Unpack, Polygon.Unpack, BSP trees);
// the polygon, vertex and BSP readers are ported from RynthAi's Raycasting/DungeonLOS.cs, which
// reads the same structures for the dungeon map and missile LOS.

internal sealed class CellPortal
{
    public ushort Flags;          // 0x2 PortalSide
    public ushort PolygonId;      // key into the CellStruct's render polygons
    public ushort OtherCell;      // low 16 bits of the cell on the other side; 0xFFFF = outdoors
    public ushort OtherPortal;
    public Vector3[] Polygon = Array.Empty<Vector3>();   // the doorway, landblock-local
}

internal sealed class Stab
{
    public uint Id;               // a GfxObj (0x01..) or Setup (0x02..)
    public Vector3 Origin;        // landblock-local
    public Quaternion Rotation;
}

internal sealed class EnvCell
{
    public uint Id;               // full cell id 0xLLLLCCCC
    public uint Flags;
    public uint EnvironmentId;
    public ushort CellStructure;
    public Vector3 Origin;        // landblock-local
    public Quaternion Rotation;
    public List<CellPortal> Portals = new();
    public List<ushort> VisibleCells = new();
    public List<Stab> Stabs = new();
    public List<Vector3[]> Floors = new();    // physics polygons facing up, landblock-local
    public List<Vector3[]> Walls = new();     // every other physics polygon
    public bool GeometryOk;

    public ushort Short => (ushort)(Id & 0xFFFF);
}

internal static class CellDat
{
    /// <summary>A walkable floor: ACE's PhysicsGlobals.FloorZ (cos 48.4 degrees).</summary>
    public const float FloorZ = 0.66417414f;

    public static List<EnvCell> LoadLandblock(DatDb cellDat, DatDb portalDat, uint landblock)
    {
        var envCache = new Dictionary<uint, Dictionary<uint, RawCellStruct>>();
        var cells = new List<EnvCell>();
        foreach (uint id in cellDat.GetLandblockCellIds(landblock))
        {
            byte[]? data = cellDat.GetFileData(id);
            if (data == null) continue;
            EnvCell? c = ParseEnvCell(data);
            if (c == null) continue;
            c.Id = id;
            if (!envCache.TryGetValue(c.EnvironmentId, out var structs))
            {
                byte[]? env = portalDat.GetFileData(c.EnvironmentId);
                structs = env == null ? new() : ParseEnvironment(env);
                envCache[c.EnvironmentId] = structs;
            }
            if (structs.TryGetValue(c.CellStructure, out RawCellStruct? cs))
                Attach(c, cs);
            cells.Add(c);
        }
        return cells;
    }

    public static Vector3 ToLandblock(EnvCell c, Vector3 local) => Vector3.Transform(local, c.Rotation) + c.Origin;

    private static void Attach(EnvCell c, RawCellStruct cs)
    {
        c.GeometryOk = cs.PhysicsOk;
        foreach (var poly in cs.Physics)
        {
            var w = poly.Select(v => ToLandblock(c, v)).ToArray();
            Vector3 n = Normal(w);
            if (n.Z >= FloorZ) c.Floors.Add(w); else c.Walls.Add(w);
        }
        foreach (var p in c.Portals)
            if (cs.Render.TryGetValue(p.PolygonId, out var poly))
                p.Polygon = poly.Select(v => ToLandblock(c, v)).ToArray();
    }

    /// <summary>Unit normal by the vertex order (ACE Polygon.make_plane: sum of fan cross products).</summary>
    public static Vector3 Normal(IReadOnlyList<Vector3> v)
    {
        Vector3 n = Vector3.Zero;
        for (int i = 1; i < v.Count - 1; i++)
            n += Vector3.Cross(v[i] - v[0], v[i + 1] - v[0]);
        float len = n.Length();
        return len < 1e-6f ? Vector3.Zero : n / len;
    }

    // ── EnvCell ──────────────────────────────────────────────────────────────

    private static EnvCell? ParseEnvCell(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            var c = new EnvCell();
            r.ReadUInt32();                       // id
            c.Flags = r.ReadUInt32();
            r.ReadUInt32();                       // id again
            byte numSurfaces = r.ReadByte();
            byte numPortals = r.ReadByte();
            ushort numVisible = r.ReadUInt16();
            for (int i = 0; i < numSurfaces; i++) r.ReadUInt16();
            c.EnvironmentId = 0x0D000000u | r.ReadUInt16();
            c.CellStructure = r.ReadUInt16();
            c.Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float w = r.ReadSingle(), x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
            c.Rotation = new Quaternion(x, y, z, w);
            for (int i = 0; i < numPortals; i++)
                c.Portals.Add(new CellPortal { Flags = r.ReadUInt16(), PolygonId = r.ReadUInt16(), OtherCell = r.ReadUInt16(), OtherPortal = r.ReadUInt16() });
            for (int i = 0; i < numVisible; i++) c.VisibleCells.Add(r.ReadUInt16());
            if ((c.Flags & 0x2) != 0)
            {
                uint n = r.ReadUInt32();
                for (uint i = 0; i < n && i < 4096; i++)
                {
                    // A stab's frame is landblock-local, like the cell's own (not relative to the cell).
                    var s = new Stab { Id = r.ReadUInt32(), Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) };
                    float sw = r.ReadSingle(), sx = r.ReadSingle(), sy = r.ReadSingle(), sz = r.ReadSingle();
                    s.Rotation = new Quaternion(sx, sy, sz, sw);
                    c.Stabs.Add(s);
                }
            }
            return c;
        }
        catch (EndOfStreamException) { return null; }
    }

    // ── Environment / CellStruct ─────────────────────────────────────────────

    private sealed class RawCellStruct
    {
        public Dictionary<ushort, Vector3[]> Render = new();
        public List<Vector3[]> Physics = new();
        public bool PhysicsOk;
    }

    private static Dictionary<uint, RawCellStruct> ParseEnvironment(byte[] data)
    {
        var result = new Dictionary<uint, RawCellStruct>();
        var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32();                           // id
        uint n = r.ReadUInt32();
        for (uint i = 0; i < n && i < 512; i++)
        {
            uint key = r.ReadUInt32();
            var cs = ParseCellStruct(r, ms, out bool toEnd);
            if (cs == null) break;
            result[key] = cs;
            if (!toEnd) break;                    // stream position unknown after this one
        }
        return result;
    }

    private static RawCellStruct? ParseCellStruct(BinaryReader r, MemoryStream ms, out bool toEnd)
    {
        toEnd = false;
        uint numPolys = r.ReadUInt32(), numPhys = r.ReadUInt32(), numPortals = r.ReadUInt32();
        if (numPolys > 5000 || numPhys > 5000) return null;
        var verts = ReadVertexArray(r, ms);
        if (verts == null) return null;
        var cs = new RawCellStruct();
        foreach (var (key, ids) in ReadPolygons(r, ms, numPolys))
            cs.Render[key] = Resolve(ids, verts);
        for (uint i = 0; i < numPortals; i++) r.ReadUInt16();
        Align(ms);
        if (!SkipBsp(r, ms, Bsp.Cell)) return cs;
        foreach (var (_, ids) in ReadPolygons(r, ms, numPhys))
        {
            var p = Resolve(ids, verts);
            if (p.Length >= 3) cs.Physics.Add(p);
        }
        cs.PhysicsOk = cs.Physics.Count > 0;
        if (!SkipBsp(r, ms, Bsp.Physics)) return cs;
        if (ms.Position + 4 <= ms.Length && r.ReadUInt32() != 0 && !SkipBsp(r, ms, Bsp.Drawing)) return cs;
        Align(ms);
        toEnd = true;
        return cs;
    }

    internal static Vector3[] Resolve(List<ushort> ids, Dictionary<ushort, Vector3> verts)
    {
        var list = new List<Vector3>(ids.Count);
        foreach (var id in ids) if (verts.TryGetValue(id, out var v)) list.Add(v);
        return list.ToArray();
    }

    private static void Align(MemoryStream ms)
    {
        long a = (ms.Position + 3) & ~3L;
        if (a <= ms.Length) ms.Position = a;
    }

    internal static Dictionary<ushort, Vector3>? ReadVertexArray(BinaryReader r, MemoryStream ms)
    {
        if (r.ReadInt32() != 1) return null;
        uint n = r.ReadUInt32();
        if (n > 50000) return null;
        var verts = new Dictionary<ushort, Vector3>();
        for (uint i = 0; i < n; i++)
        {
            ushort key = r.ReadUInt16();
            ushort numUv = r.ReadUInt16();
            var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            ms.Seek(12 + numUv * 8L, SeekOrigin.Current);   // normal + UVs
            verts[key] = v;
        }
        return verts;
    }

    internal static List<(ushort, List<ushort>)> ReadPolygons(BinaryReader r, MemoryStream ms, uint count)
    {
        var polys = new List<(ushort, List<ushort>)>();
        for (uint i = 0; i < count; i++)
        {
            ushort key = r.ReadUInt16();
            byte numPts = r.ReadByte();
            byte stippling = r.ReadByte();
            int sides = r.ReadInt32();
            r.ReadInt16(); r.ReadInt16();         // surfaces
            if (numPts == 0 || numPts > 50) throw new InvalidDataException("bad polygon");
            var ids = new List<ushort>(numPts);
            for (int v = 0; v < numPts; v++) ids.Add((ushort)r.ReadInt16());
            if ((stippling & 0x04) == 0) ms.Seek(numPts, SeekOrigin.Current);
            if (sides == 2 && (stippling & 0x08) == 0) ms.Seek(numPts, SeekOrigin.Current);
            polys.Add((key, ids));
        }
        return polys;
    }

    private enum Bsp { Cell, Physics, Drawing }

    private const uint PORT = 0x504F5254, LEAF = 0x4C454146, BPnn = 0x42506E6E, BPIn = 0x4250496E, BpIN = 0x4270494E,
        BpnN = 0x42706E4E, BPIN = 0x4250494E, BPnN = 0x42506E4E, BPFL = 0x4250464C, BPOL = 0x42504F4C, BpIn = 0x4270496E;

    private static bool SkipBsp(BinaryReader r, MemoryStream ms, Bsp t)
    {
        if (ms.Position + 4 > ms.Length) return false;
        uint tag = r.ReadUInt32();
        if (tag == LEAF || tag == PORT)
        {
            r.ReadInt32();                        // leaf index
            if (t == Bsp.Physics)
            {
                r.ReadInt32();                    // solid
                ms.Seek(16, SeekOrigin.Current);  // sphere
                uint np = r.ReadUInt32();
                if (np > 100000) return false;
                ms.Seek(np * 2L, SeekOrigin.Current);
            }
            return ms.Position <= ms.Length;
        }
        ms.Seek(16, SeekOrigin.Current);          // splitting plane
        switch (tag)
        {
            case BPnn: case BPIn: case BpIN: case BpnN:
                if (!SkipBsp(r, ms, t)) return false;
                break;
            case BPIN: case BPnN:
                if (!SkipBsp(r, ms, t) || !SkipBsp(r, ms, t)) return false;
                break;
            case BPFL: case BPOL: case BpIn:
                break;
            default:
                return false;
        }
        if (t == Bsp.Cell) return true;
        ms.Seek(16, SeekOrigin.Current);          // sphere
        if (t == Bsp.Physics) return true;
        uint n = r.ReadUInt32();
        if (n > 100000) return false;
        ms.Seek(n * 2L, SeekOrigin.Current);
        return ms.Position <= ms.Length;
    }
}
