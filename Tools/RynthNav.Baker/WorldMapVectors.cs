using System.Numerics;
using Vector3 = System.Numerics.Vector3;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.TerrainData;

namespace RynthNav.Baker;

// --map-vectors: the World Map's vector layers, worldmap.vec, drawn over the tiles when you zoom in.
//  - Buildings: each landblock's building list (LandBlockInfo, file 0xLLLLFFFE in the cell dat), the
//    model's triangles from GeometryLoader.GetTexturedModel, their ground outline (convex hull, cut
//    down to at most MaxHullPoints corners) placed by the building's frame. Outlines are in map cells.
//  - Roads: the terrain words' road bits (vertex bits 0-1): a segment between every two neighbouring
//    road vertices (east, north, and both diagonals), in whole cells.
//  File (little-endian): "RVEC", u32 version (1), u32 building count, u32 road segment count; per
//  building u8 corner count then that many (f32 x, f32 y) in cells; per road segment u16 x0, y0, x1, y1
//  (cells). The engine's WorldMapVectors reads this.
internal static class WorldMapVectors
{
    private const int Blocks = 256, Side = 8, V = Blocks * Side + 1;
    private const int MaxHullPoints = 10;

    public sealed class Data
    {
        public readonly List<Vector2[]> Buildings = new();
        public readonly List<(ushort X0, ushort Y0, ushort X1, ushort Y1)> Roads = new();
    }

    public static Data Build(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, Action<string> log)
    {
        var d = new Data();
        BuildRoads(sampler, d, log);
        BuildBuildings(geo, d, log);
        return d;
    }

    // ── Roads ────────────────────────────────────────────────────────────
    private static void BuildRoads(TerrainSampler sampler, Data d, Action<string> log)
    {
        var road = new byte[V * V];
        int loaded = 0;
        for (int lx = 0; lx < Blocks; lx++)
            for (int ly = 0; ly < Blocks; ly++)
            {
                LandblockData? lb = null;
                try { lb = sampler.LoadLandblock((uint)((lx << 8) | ly)); } catch { }
                if (lb?.TerrainWords == null) continue;
                loaded++;
                for (int ix = 0; ix <= Side; ix++)
                    for (int iy = 0; iy <= Side; iy++)
                        road[(ly * Side + iy) * V + lx * Side + ix] = (byte)(lb.TerrainWords[ix * 9 + iy] & 3);
            }
        int verts = 0;
        for (int y = 0; y < V; y++)
            for (int x = 0; x < V; x++)
            {
                if (road[y * V + x] == 0) continue;
                verts++;
                // East, north, north-east, north-west: each pair once.
                if (x + 1 < V && road[y * V + x + 1] != 0) d.Roads.Add(((ushort)x, (ushort)y, (ushort)(x + 1), (ushort)y));
                if (y + 1 < V && road[(y + 1) * V + x] != 0) d.Roads.Add(((ushort)x, (ushort)y, (ushort)x, (ushort)(y + 1)));
                if (x + 1 < V && y + 1 < V && road[(y + 1) * V + x + 1] != 0) d.Roads.Add(((ushort)x, (ushort)y, (ushort)(x + 1), (ushort)(y + 1)));
                if (x > 0 && y + 1 < V && road[(y + 1) * V + x - 1] != 0) d.Roads.Add(((ushort)x, (ushort)y, (ushort)(x - 1), (ushort)(y + 1)));
            }
        log($"roads: {verts} road vertices in {loaded} landblocks, {d.Roads.Count} segments");
    }

    // ── Buildings ────────────────────────────────────────────────────────
    private static void BuildBuildings(RynthCore2.Raycast.GeometryLoader geo, Data d, Action<string> log)
    {
        var hulls = new Dictionary<uint, Vector2[]?>();     // model -> its outline in model space
        int blocksWith = 0, noHull = 0;
        for (int lx = 0; lx < Blocks; lx++)
            for (int ly = 0; ly < Blocks; ly++)
            {
                uint lb = (uint)((lx << 8) | ly);
                byte[]? data = null;
                try { data = geo.CellDat.GetFileData((lb << 16) | 0xFFFE); } catch { }
                if (data == null || data.Length < 16) continue;
                int count = 0;
                foreach (var (model, origin, rot) in ReadBuildings(data))
                {
                    if (!hulls.TryGetValue(model, out Vector2[]? hull)) { hull = ModelOutline(geo, model); hulls[model] = hull; }
                    if (hull == null || hull.Length < 3) { noHull++; continue; }
                    var pts = new Vector2[hull.Length];
                    for (int i = 0; i < hull.Length; i++)
                    {
                        Vector3 p = Vector3.Transform(new Vector3(hull[i].X, hull[i].Y, 0), rot) + origin;
                        // Landblock-local yards to map cells (24 yards each); the landblock is 8 cells wide.
                        pts[i] = new Vector2(lx * 8f + p.X / 24f, ly * 8f + p.Y / 24f);
                    }
                    d.Buildings.Add(pts);
                    count++;
                }
                if (count > 0) blocksWith++;
            }
        log($"buildings: {d.Buildings.Count} in {blocksWith} landblocks ({hulls.Count} models, {noHull} with no outline)");
    }

    /// <summary>The buildings in a LandBlockInfo: model id, origin and rotation (the layout GeometryLoader.ParseLandblockObjects reads).</summary>
    private static IEnumerable<(uint Model, Vector3 Origin, Quaternion Rot)> ReadBuildings(byte[] data)
    {
        uint numObjects = BitConverter.ToUInt32(data, 8);
        if (numObjects > 5000) numObjects = 5000;
        int off = 12 + (int)numObjects * 32;
        if (off + 4 > data.Length) yield break;
        ushort numBuildings = BitConverter.ToUInt16(data, off);
        off += 4;
        for (int b = 0; b < numBuildings && off + 36 <= data.Length; b++)
        {
            uint model = BitConverter.ToUInt32(data, off);
            var origin = new Vector3(BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8), BitConverter.ToSingle(data, off + 12));
            float rw = BitConverter.ToSingle(data, off + 16), rx = BitConverter.ToSingle(data, off + 20),
                  ry = BitConverter.ToSingle(data, off + 24), rz = BitConverter.ToSingle(data, off + 28);
            off += 36;
            if (off + 4 > data.Length) yield break;
            uint numPortals = BitConverter.ToUInt32(data, off);
            off += 4;
            for (uint p = 0; p < numPortals && off + 8 <= data.Length; p++)
            {
                ushort numStabs = BitConverter.ToUInt16(data, off + 6);
                int psz = 8 + numStabs * 2; psz += (4 - (psz & 3)) & 3;
                off += psz;
            }
            if (float.IsNaN(origin.X) || Math.Abs(origin.X) > 50000) continue;
            yield return (model, origin, new Quaternion(rx, ry, rz, rw));
        }
    }

    /// <summary>The ground outline of a model: the hull of its triangles' x, y, cut to a few corners. Null when it has no triangles.</summary>
    private static Vector2[]? ModelOutline(RynthCore2.Raycast.GeometryLoader geo, uint model)
    {
        List<RynthCore2.Raycast.GeometryLoader.TexTri> tris;
        try { tris = geo.GetTexturedModel(model); } catch { return null; }
        if (tris.Count == 0) return null;
        var pts = new List<Vector2>(tris.Count * 3);
        foreach (var t in tris) { pts.Add(new Vector2(t.A.X, t.A.Y)); pts.Add(new Vector2(t.B.X, t.B.Y)); pts.Add(new Vector2(t.C.X, t.C.Y)); }
        Vector2[] hull = Hull(pts);
        return hull.Length <= MaxHullPoints ? hull : Reduce(hull, MaxHullPoints);
    }

    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise.</summary>
    private static Vector2[] Hull(List<Vector2> pts)
    {
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var u = new List<Vector2>();
        foreach (Vector2 p in pts)
        {
            if (u.Count > 0 && u[^1] == p) continue;
            u.Add(p);
        }
        if (u.Count < 3) return u.ToArray();
        var h = new Vector2[2 * u.Count];
        int k = 0;
        float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        for (int i = 0; i < u.Count; i++) { while (k >= 2 && Cross(h[k - 2], h[k - 1], u[i]) <= 0) k--; h[k++] = u[i]; }
        for (int i = u.Count - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(h[k - 2], h[k - 1], u[i]) <= 0) k--; h[k++] = u[i]; }
        return h.AsSpan(0, k - 1).ToArray();
    }

    /// <summary>Drops the corner whose removal changes the area least, until <paramref name="max"/> are left.</summary>
    private static Vector2[] Reduce(Vector2[] hull, int max)
    {
        var l = new List<Vector2>(hull);
        while (l.Count > max)
        {
            int best = 0; float bestArea = float.MaxValue;
            for (int i = 0; i < l.Count; i++)
            {
                Vector2 a = l[(i + l.Count - 1) % l.Count], b = l[i], c = l[(i + 1) % l.Count];
                float area = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * 0.5f;
                if (area < bestArea) { bestArea = area; best = i; }
            }
            l.RemoveAt(best);
        }
        return l.ToArray();
    }

    // ── File ─────────────────────────────────────────────────────────────
    public static void Write(string file, Data d)
    {
        string tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(new byte[] { (byte)'R', (byte)'V', (byte)'E', (byte)'C' });
            w.Write(1u); w.Write((uint)d.Buildings.Count); w.Write((uint)d.Roads.Count);
            foreach (Vector2[] b in d.Buildings)
            {
                w.Write((byte)b.Length);
                foreach (Vector2 p in b) { w.Write(p.X); w.Write(p.Y); }
            }
            foreach (var r in d.Roads) { w.Write(r.X0); w.Write(r.Y0); w.Write(r.X1); w.Write(r.Y1); }
        }
        File.Move(tmp, file, overwrite: true);
    }

    // ── Preview: the vectors drawn over a crop of the terrain render, to check them by eye ────
    public static void DrawOverlay(byte[] rgb, int w, int h, int cx0, int cy0, int ppc, Data d)
    {
        // Pixel of a map cell position: x grows east; image row 0 is the north edge.
        Vector2 Px(float cx, float cy) => new((cx - cx0) * ppc, h - (cy - cy0) * ppc);
        foreach (var r in d.Roads)
        {
            Vector2 a = Px(r.X0, r.Y0), b = Px(r.X1, r.Y1);
            if (a.X < -4 || b.X < -4 || a.Y < -4 || b.Y < -4 || a.X > w + 4 || b.X > w + 4 || a.Y > h + 4 || b.Y > h + 4) continue;
            Line(rgb, w, h, a, b, (230, 205, 120), Math.Max(1, ppc / 5));
        }
        foreach (Vector2[] bld in d.Buildings)
        {
            var poly = new Vector2[bld.Length];
            bool any = false;
            for (int i = 0; i < bld.Length; i++) { poly[i] = Px(bld[i].X, bld[i].Y); if (poly[i].X >= 0 && poly[i].X < w && poly[i].Y >= 0 && poly[i].Y < h) any = true; }
            if (!any) continue;
            Fill(rgb, w, h, poly, (150, 70, 60));
            for (int i = 0; i < poly.Length; i++) Line(rgb, w, h, poly[i], poly[(i + 1) % poly.Length], (40, 20, 20), 1);
        }
    }

    private static void Plot(byte[] rgb, int w, int h, int x, int y, (byte R, byte G, byte B) c)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        long o = ((long)y * w + x) * 3;
        rgb[o] = c.R; rgb[o + 1] = c.G; rgb[o + 2] = c.B;
    }

    private static void Line(byte[] rgb, int w, int h, Vector2 a, Vector2 b, (byte R, byte G, byte B) c, int thick)
    {
        float len = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)(len * 1.5f));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
            for (int dx = -(thick - 1) / 2; dx <= thick / 2; dx++)
                for (int dy = -(thick - 1) / 2; dy <= thick / 2; dy++)
                    Plot(rgb, w, h, (int)MathF.Round(p.X) + dx, (int)MathF.Round(p.Y) + dy, c);
        }
    }

    private static void Fill(byte[] rgb, int w, int h, Vector2[] poly, (byte R, byte G, byte B) c)
    {
        float minY = poly.Min(p => p.Y), maxY = poly.Max(p => p.Y);
        for (int y = Math.Max(0, (int)MathF.Floor(minY)); y <= Math.Min(h - 1, (int)MathF.Ceiling(maxY)); y++)
        {
            var xs = new List<float>();
            for (int i = 0; i < poly.Length; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Length];
                if ((a.Y <= y + 0.5f && b.Y > y + 0.5f) || (b.Y <= y + 0.5f && a.Y > y + 0.5f))
                    xs.Add(a.X + (y + 0.5f - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                for (int x = (int)MathF.Round(xs[i]); x <= (int)MathF.Round(xs[i + 1]); x++) Plot(rgb, w, h, x, y, c);
        }
    }
}
