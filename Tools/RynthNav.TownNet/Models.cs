using System.Numerics;
using DatDb = RynthCore.Plugin.RynthAi.Raycasting.DatDatabase;

namespace RynthNav.TownNet;

/// <summary>
/// The solid shape of a model (a Setup 0x02.. or a GfxObj 0x01..) in its own frame: the physics
/// polygons of its parts, its collision cylinders and spheres, and the Setup's own radius and height.
/// Formats as in ACE (SetupModel.Unpack, GfxObj.Unpack); a model with no physics has no polygons.
/// </summary>
internal sealed class ModelShape
{
    public List<Vector3[]> Polygons = new();                     // model space
    public List<(Vector3 Origin, float Radius, float Height)> Cylinders = new();
    public List<(Vector3 Center, float Radius)> Spheres = new();
    public float Radius, Height;
    public bool Parsed;

    public bool IsEmpty => Polygons.Count == 0 && Cylinders.Count == 0 && Spheres.Count == 0;
}

internal sealed class ModelReader
{
    private readonly DatDb _portal;
    private readonly Dictionary<uint, ModelShape> _cache = new();

    public ModelReader(DatDb portalDat) { _portal = portalDat; }

    public ModelShape Get(uint id)
    {
        if (_cache.TryGetValue(id, out var s)) return s;
        s = new ModelShape();
        try
        {
            if ((id >> 24) == 0x01) { s.Parsed = ReadGfxObj(id, s.Polygons, Matrix4x4.Identity); }
            else if ((id >> 24) == 0x02) { s.Parsed = ReadSetup(id, s); }
        }
        catch (Exception) { s.Parsed = false; }
        _cache[id] = s;
        return s;
    }

    private bool ReadGfxObj(uint id, List<Vector3[]> into, Matrix4x4 m)
    {
        byte[]? data = _portal.GetFileData(id);
        if (data == null) return false;
        var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32();
        uint flags = r.ReadUInt32();
        uint numSurfaces = ReadCompressed(r);
        ms.Seek(numSurfaces * 4L, SeekOrigin.Current);
        var verts = CellDat.ReadVertexArray(r, ms);
        if (verts == null) return false;
        if ((flags & 1) == 0) return true;                      // no physics: walk through it
        uint numPhys = ReadCompressed(r);
        foreach (var (_, ids) in CellDat.ReadPolygons(r, ms, numPhys))
        {
            var p = CellDat.Resolve(ids, verts);
            if (p.Length >= 3) into.Add(p.Select(v => Vector3.Transform(v, m)).ToArray());
        }
        return true;
    }

    private bool ReadSetup(uint id, ModelShape s)
    {
        byte[]? data = _portal.GetFileData(id);
        if (data == null) return false;
        var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32();
        uint flags = r.ReadUInt32();
        uint numParts = r.ReadUInt32();
        if (numParts > 1000) return false;
        var parts = new uint[numParts];
        for (int i = 0; i < numParts; i++) parts[i] = r.ReadUInt32();
        if ((flags & 1) != 0) ms.Seek(numParts * 4L, SeekOrigin.Current);          // parents
        var scale = Enumerable.Repeat(Vector3.One, (int)numParts).ToArray();
        if ((flags & 2) != 0)
            for (int i = 0; i < numParts; i++) scale[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        SkipLocations(r, ms);                                                      // holding locations
        SkipLocations(r, ms);                                                      // connection points
        int placements = r.ReadInt32();
        Matrix4x4[]? frames = null;
        bool hooksSeen = false;
        for (int p = 0; p < placements && !hooksSeen; p++)
        {
            int key = r.ReadInt32();
            var f = new Matrix4x4[numParts];
            for (int i = 0; i < numParts; i++) f[i] = ReadFrame(r);
            if (frames == null || key == 0) frames = f;
            if (r.ReadUInt32() != 0) hooksSeen = true;                             // variable-size hooks: stop here
        }
        for (int i = 0; i < numParts; i++)
        {
            Matrix4x4 m = Matrix4x4.CreateScale(scale[i]) * (frames != null ? frames[i] : Matrix4x4.Identity);
            if ((parts[i] >> 24) == 0x01) ReadGfxObj(parts[i], s.Polygons, m);
        }
        if (hooksSeen) return true;
        uint numCyl = r.ReadUInt32();
        for (uint i = 0; i < numCyl && i < 64; i++)
            s.Cylinders.Add((new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle(), r.ReadSingle()));
        uint numSph = r.ReadUInt32();
        for (uint i = 0; i < numSph && i < 64; i++)
            s.Spheres.Add((new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle()));
        s.Height = r.ReadSingle();
        s.Radius = r.ReadSingle();
        return true;
    }

    private static Matrix4x4 ReadFrame(BinaryReader r)
    {
        var o = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        float w = r.ReadSingle(), x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
        var q = new Quaternion(x, y, z, w);
        if (q.LengthSquared() < 1e-6f) q = Quaternion.Identity;
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(o);
    }

    private static void SkipLocations(BinaryReader r, MemoryStream ms)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 1000) throw new InvalidDataException("bad location table");
        ms.Seek(n * 36L, SeekOrigin.Current);                                       // key, part id, frame
    }

    private static uint ReadCompressed(BinaryReader r)
    {
        byte b0 = r.ReadByte();
        if ((b0 & 0x80) == 0) return b0;
        byte b1 = r.ReadByte();
        if ((b0 & 0x40) == 0) return (uint)(((b0 & 0x7F) << 8) | b1);
        ushort s = r.ReadUInt16();
        return (uint)((((b0 & 0x3F) << 8 | b1) << 16) | s);
    }
}
