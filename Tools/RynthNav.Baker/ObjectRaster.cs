using System.Numerics;
using RynthCore2.Raycast;
using Vector3 = System.Numerics.Vector3;

namespace RynthNav.Baker;

// The world seen from straight above: the game's own static objects (buildings with their roofs,
// walls, bridges, statues) and scenery (trees, rocks, bushes), textured, drawn over the terrain by a
// small CPU rasteriser with a depth buffer: --map --textured --objects.
//  - Orthographic, looking down: a point's pixel is its world x, y; the higher one wins.
//  - Each triangle is filled from its surface's texture (alpha-tested, so leaves cut out); a surface
//    with no texture uses its flat colour; the translucent panes of doors and windows are skipped.
//  - Lit from the north-west, high up: a flat roof is full bright, a wall in shade.
internal sealed class ObjectRaster
{
    internal sealed record Tex(int W, int H, byte[]? Px, uint Solid, float Alpha);

    /// <summary>Decoded object surfaces, shared by every raster and thread (the decoder itself is not thread-safe).</summary>
    internal sealed class SurfaceStore
    {
        private readonly AcClientReborn.AcTextureDecoder _dec;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Tex?> _cache = new();
        private readonly object _gate = new();
        public SurfaceStore(AcClientReborn.AcTextureDecoder dec) { _dec = dec; }
        public int Count => _cache.Count;

        public Tex? Get(uint surface)
        {
            if (_cache.TryGetValue(surface, out Tex? t)) return t;
            lock (_gate)
            {
                if (_cache.TryGetValue(surface, out t)) return t;
                var d = _dec.GetSurface(surface);
                t = d == null ? null : new Tex(d.Width, d.Height, d.Rgba, d.SolidColor, d.Alpha);
                _cache[surface] = t;
                return t;
            }
        }
    }

    private readonly byte[] _rgb;
    private readonly float[] _depth;
    private readonly int _w, _h, _ppc;
    private readonly double _cx0, _cy0;
    private readonly SurfaceStore _surfaces;
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.35f, 0.35f, 0.87f));

    public long Triangles { get; private set; }

    public ObjectRaster(byte[] rgb, int w, int h, int cx0, int cy0, int ppc, SurfaceStore surfaces)
    {
        _rgb = rgb; _w = w; _h = h; _cx0 = cx0; _cy0 = cy0; _ppc = ppc; _surfaces = surfaces;
        _depth = new float[w * h];
        Array.Fill(_depth, float.NegativeInfinity);
    }

    // World yards to pixels (x grows east; row 0 is the north edge).
    private float Px(float worldX) => (float)((worldX / 24.0 - _cx0) * _ppc);
    private float Py(float worldY) => (float)(_h - (worldY / 24.0 - _cy0) * _ppc);

    public void Add(IEnumerable<GeometryLoader.TexTri> tris)
    {
        foreach (var t in tris)
        {
            Triangles++;
            Tex? tex = _surfaces.Get(t.Surface);
            if (tex != null && tex.Alpha < 0.9f) continue;                    // a door or window pane
            float e1x = t.B.X - t.A.X, e1y = t.B.Y - t.A.Y, e1z = t.B.Z - t.A.Z, e2x = t.C.X - t.A.X, e2y = t.C.Y - t.A.Y, e2z = t.C.Z - t.A.Z;
            float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) continue;
            nx /= len; ny /= len; nz /= len;
            if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }                      // two-sided
            float lit = TexturedTerrain.Gain * (0.7f + 0.45f * Math.Max(nx * Light.X + ny * Light.Y + nz * Light.Z, 0f));
            Raster(t, tex, lit);
        }
    }

    private void Raster(GeometryLoader.TexTri t, Tex? tex, float lit)
    {
        float ax = Px(t.A.X), ay = Py(t.A.Y), bx = Px(t.B.X), by = Py(t.B.Y), cx = Px(t.C.X), cy = Py(t.C.Y);
        float minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
        float minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));
        int x0 = Math.Max(0, (int)MathF.Floor(minX)), x1 = Math.Min(_w - 1, (int)MathF.Ceiling(maxX));
        int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(_h - 1, (int)MathF.Ceiling(maxY));
        if (x1 < x0 || y1 < y0) return;
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (MathF.Abs(area) < 1e-6f) return;                                  // edge-on from above (a wall)
        float inv = 1f / area;
        for (int y = y0; y <= y1; y++)
        {
            float py = y + 0.5f;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;   // weight of A
                float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;   // weight of B
                float w2 = 1f - w0 - w1;                                            // weight of C
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * t.A.Z + w1 * t.B.Z + w2 * t.C.Z;
                int idx = y * _w + x;
                if (z <= _depth[idx]) continue;
                byte r, g, b;
                if (tex?.Px != null)
                {
                    float u = w0 * t.Au + w1 * t.Bu + w2 * t.Cu, v = w0 * t.Av + w1 * t.Bv + w2 * t.Cv;
                    u -= MathF.Floor(u); v -= MathF.Floor(v);
                    int tx = Math.Min((int)(u * tex.W), tex.W - 1), ty = Math.Min((int)(v * tex.H), tex.H - 1);
                    int o = (ty * tex.W + tx) * 4;
                    if (tex.Px[o + 3] < 128) continue;                              // a cut-out (leaf gap, window)
                    r = tex.Px[o]; g = tex.Px[o + 1]; b = tex.Px[o + 2];
                }
                else if (tex != null) { r = (byte)(tex.Solid >> 16); g = (byte)(tex.Solid >> 8); b = (byte)tex.Solid; }
                else continue;
                _depth[idx] = z;
                long q = (long)idx * 3;
                _rgb[q] = (byte)Math.Min(255, r * lit);
                _rgb[q + 1] = (byte)Math.Min(255, g * lit);
                _rgb[q + 2] = (byte)Math.Min(255, b * lit);
            }
        }
    }
}
