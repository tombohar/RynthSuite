using System.Numerics;

namespace RynthNav.Baker;

// The game's own terrain look, drawn top-down on the CPU: --map --textured.
//  Each 24-yard cell is blended the way the client's TexMerge does it (ported from AcClientReborn's
//  Program.cs, which does it on the GPU): the cell's four corner terrain types and road bits make a
//  palette code; that picks a base ground texture, up to three overlay textures (the neighbouring
//  types) each cut by a corner/side alpha mask under one of four rotations, and a road texture cut
//  by a road mask. Ground textures cover one cell (u, v run 0..1 across it); masks are single-channel.
//  Then the cell is lit from the terrain's slope (the vertex shade the flat render uses).
//  The textures come from portal.dat through AcClientReborn's AcTextureDecoder.
internal sealed class TexturedTerrain
{
    private readonly record struct Img(int W, int H, byte[] Px);

    // Atlas layers as in AcClientReborn: 0-31 terrain-type ground, 40 road stone, 50-62 road masks, 64+ terrain masks.
    private readonly Img?[] _layers = new Img?[128];
    private int _stone = -1;
    private readonly List<(uint Code, int Layer)> _road = new(), _corner = new(), _side = new();

    // AC's LandblockStruct.LandUVs (cell-corner UVs: SW, SE, NE, NW) and the 4 rotation permutations.
    private static readonly (float U, float V)[] LandUV = { (0, 1), (1, 1), (1, 0), (0, 0) };
    private static readonly int[][] LandUVRot = { new[] { 0, 1, 2, 3 }, new[] { 3, 0, 1, 2 }, new[] { 2, 3, 0, 1 }, new[] { 1, 2, 3, 0 } };

    /// <summary>The client lights terrain with sun and ambient on top of what the textures hold; this stands in for it.</summary>
    public const float Gain = 1.55f;
    public int Layers { get; private set; }
    public AcClientReborn.AcTextureDecoder Decoder { get; private set; } = null!;

    public static TexturedTerrain? Load(RynthCore2.Raycast.GeometryLoader geo, Action<string> log)
    {
        var ss = geo.ScatterSystemRef;
        if (ss == null) { log("textured terrain: no region description"); return null; }
        var dec = new AcClientReborn.AcTextureDecoder(geo.PortalDat);
        var t = new TexturedTerrain { Decoder = dec };
        Img? Decode(uint gid)
        {
            var d = dec.DecodeAny(gid);
            return d?.Rgba != null ? new Img(d.Width, d.Height, d.Rgba) : null;
        }
        foreach (var kv in ss.TerrainTexGIDs)
        {
            if (kv.Key >= 40) continue;
            if (Decode(kv.Value) is Img i) { t._layers[kv.Key] = i; t.Layers++; }
        }
        if (ss.TerrainTexGIDs.TryGetValue(0x20u, out uint stone) && Decode(stone) is Img si) { t._layers[40] = si; t._stone = 40; t.Layers++; }
        var maskLayer = new Dictionary<uint, int>();
        int next = 64;
        int Mask(uint gid)
        {
            if (gid == 0) return -1;
            if (maskLayer.TryGetValue(gid, out int l)) return l;
            l = next++;
            if (l < 128 && Decode(gid) is Img mi) { t._layers[l] = mi; t.Layers++; }
            maskLayer[gid] = l;
            return l;
        }
        foreach (var (code, gid) in ss.CornerTerrainMaps) t._corner.Add((code, Mask(gid)));
        foreach (var (code, gid) in ss.SideTerrainMaps) t._side.Add((code, Mask(gid)));
        var roadLayer = new Dictionary<uint, int>();
        foreach (var (rcode, gid) in ss.RoadMaps)
        {
            if (!roadLayer.TryGetValue(gid, out int l))
            {
                l = 50 + roadLayer.Count;
                if (l <= 62 && Decode(gid) is Img ri) { t._layers[l] = ri; t.Layers++; }
                roadLayer[gid] = l;
            }
            t._road.Add((rcode, l));
        }
        int types = 0; for (int i = 0; i < 32; i++) if (t._layers[i] != null) types++;
        log($"textured terrain: {types} ground textures, stone road L{t._stone}, {t._corner.Count} corner + {t._side.Count} side + {t._road.Count} road masks ({t.Layers} images)");
        foreach (int l in new[] { 0, 1, 3, 20, 40 }) if (t._layers[l] is Img im) log($"  layer {l}: {im.W}x{im.H}");
        return t;
    }

    // ── TexMerge (AcClientReborn Program.cs) ────────────────────────────
    private static uint PalCode(int r1, int r2, int r3, int r4, int t1, int t2, int t3, int t4)
        => (uint)((1 << 28) | (r1 << 26 | r2 << 24 | r3 << 22 | r4 << 20) | (t1 << 15 | t2 << 10 | t3 << 5 | t4));

    private static (uint c0, uint c1, bool all) RoadCode(uint pcode)
    {
        uint mask = 0;
        if ((pcode & 0xC000000) != 0) mask = 1;
        if ((pcode & 0x3000000) != 0) mask |= 2;
        if ((pcode & 0xC00000) != 0) mask |= 4;
        if ((pcode & 0x300000) != 0) mask |= 8;
        return mask switch
        {
            0xF => (0u, 0u, true),
            0xE => (6u, 12u, false),
            0xD => (9u, 12u, false),
            0xB => (9u, 3u, false),
            0x7 => (3u, 6u, false),
            0x0 => (0u, 0u, false),
            _ => (mask, 0u, false),
        };
    }

    private (int layer, int rot) FindRoadAlpha(uint pcode, uint rcode)
    {
        int n = _road.Count;
        if (n == 0 || rcode == 0) return (-1, 0);
        int prng = (int)Math.Floor((1379576222.0 * pcode - 1372186442.0) * 2.3283064e-10 * n);
        for (int i = 0; i < n; i++)
        {
            int idx = ((i + prng) % n + n) % n;
            uint alphaCode = _road[idx].Code;
            for (int j = 0; j < 4; j++)
            {
                if (alphaCode == rcode) return (_road[idx].Layer, j);
                alphaCode *= 2;
                if (alphaCode >= 16) alphaCode -= 15;
            }
        }
        return (-1, 0);
    }

    private static int[] TerrainCodes(uint pcode) => new[] { (int)((pcode >> 15) & 0x1F), (int)((pcode >> 10) & 0x1F), (int)((pcode >> 5) & 0x1F), (int)(pcode & 0x1F) };

    private static (int baseType, (int type, uint tcode)[] overlays) BuildTCodes(int[] p, int baseIdx)
    {
        int baseType = p[baseIdx], t2 = -1, ov0 = -1, ov1 = -1;
        uint tc0 = 0, tc1 = 0;
        for (int k = 0; k < 4; k++)
        {
            if (baseType == p[k]) continue;
            if (tc0 == 0) { tc0 = (uint)(1 << k); t2 = p[k]; ov0 = t2; }
            else
            {
                if (t2 == p[k] && tc0 == (uint)(1 << (k - 1))) tc0 += (uint)(1 << k);
                else { ov1 = p[k]; tc1 = (uint)(1 << k); }
                break;
            }
        }
        var list = new List<(int, uint)>();
        if (ov0 >= 0) list.Add((ov0, tc0));
        if (ov1 >= 0) list.Add((ov1, tc1));
        return (baseType, list.ToArray());
    }

    private static (int baseType, (int type, uint tcode)[] overlays) Terrain(uint pcode)
    {
        var p = TerrainCodes(pcode);
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                if (p[i] == p[j]) return BuildTCodes(p, i);
        return (p[0], new (int, uint)[] { (p[1], 2u), (p[2], 4u), (p[3], 8u) });
    }

    private (int layer, int rot) FindTerrainAlpha(uint pcode, uint tcode)
    {
        var maps = (tcode == 1 || tcode == 2 || tcode == 4 || tcode == 8) ? _corner : _side;
        int n = maps.Count;
        if (n == 0) return (-1, 0);
        int prng = (int)Math.Floor((1379576222.0 * pcode - 1372186442.0) * 2.3283064e-10 * n);
        if (prng >= n || prng < 0) prng = 0;
        uint alphaCode = maps[prng].Code;
        for (int i = 0; ; i++)
        {
            if (alphaCode == tcode) return (maps[prng].Layer, i);
            alphaCode *= 2;
            if (alphaCode >= 16) alphaCode -= 15;
            if (i >= 3) return (-1, 0);
        }
    }

    /// <summary>
    /// A cell's merge result, flat: [0] base layer; [1..3], [4..6], [7..9] overlay i (colour layer, mask layer, rotation);
    /// [10] road stone layer; [11, 12] road 0 (mask layer, rotation); [13, 14] road 1. -1 = unused.
    /// </summary>
    private int[] CellSurf(int t1, int t2, int t3, int t4, int r1, int r2, int r3, int r4)
    {
        var s = new int[15];
        Array.Fill(s, -1);
        uint pcode = PalCode(r1, r2, r3, r4, t1, t2, t3, t4);
        var (c0, c1, allRoad) = RoadCode(pcode);
        if (allRoad && _stone >= 0) { s[0] = _stone; return s; }
        var (baseType, overlays) = Terrain(pcode);
        s[0] = baseType;
        int slot = 0;
        foreach (var (otype, tcode) in overlays)
        {
            if (tcode == 0 || slot >= 3) continue;
            var (mlayer, rot) = FindTerrainAlpha(pcode, tcode);
            if (mlayer < 0) continue;
            s[1 + slot * 3] = otype; s[2 + slot * 3] = mlayer; s[3 + slot * 3] = rot;
            slot++;
        }
        if (_stone >= 0 && (r1 | r2 | r3 | r4) != 0)
        {
            var (l0, rot0) = FindRoadAlpha(pcode, c0);
            if (l0 >= 0)
            {
                s[10] = _stone; s[11] = l0; s[12] = rot0;
                if (c1 != 0) { var (l1, rot1) = FindRoadAlpha(pcode, c1); if (l1 >= 0) { s[13] = l1; s[14] = rot1; } }
            }
        }
        return s;
    }

    // ── Sampling ─────────────────────────────────────────────────────────
    /// <summary>Bilinear sample of a layer; u, v in 0..1 (repeat). r, g, b 0..1, a 0..1.</summary>
    private void Sample(int layer, float u, float v, bool wrap, out float r, out float g, out float b, out float a)
    {
        r = g = b = 0; a = 1;
        if ((uint)layer >= 128 || _layers[layer] is not Img im) return;
        float x = u * im.W - 0.5f, y = v * im.H - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int x1 = x0 + 1, y1 = y0 + 1;
        if (wrap) { x0 = ((x0 % im.W) + im.W) % im.W; x1 = ((x1 % im.W) + im.W) % im.W; y0 = ((y0 % im.H) + im.H) % im.H; y1 = ((y1 % im.H) + im.H) % im.H; }
        else { x0 = Math.Clamp(x0, 0, im.W - 1); x1 = Math.Clamp(x1, 0, im.W - 1); y0 = Math.Clamp(y0, 0, im.H - 1); y1 = Math.Clamp(y1, 0, im.H - 1); }
        int o00 = (y0 * im.W + x0) * 4, o10 = (y0 * im.W + x1) * 4, o01 = (y1 * im.W + x0) * 4, o11 = (y1 * im.W + x1) * 4;
        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        byte[] p = im.Px;
        r = (p[o00] * w00 + p[o10] * w10 + p[o01] * w01 + p[o11] * w11) / 255f;
        g = (p[o00 + 1] * w00 + p[o10 + 1] * w10 + p[o01 + 1] * w01 + p[o11 + 1] * w11) / 255f;
        b = (p[o00 + 2] * w00 + p[o10 + 2] * w10 + p[o01 + 2] * w01 + p[o11 + 2] * w11) / 255f;
        a = (p[o00 + 3] * w00 + p[o10 + 3] * w10 + p[o01 + 3] * w01 + p[o11 + 3] * w11) / 255f;
    }

    /// <summary>The cell-local u, v of a pixel in a mask rotated by <paramref name="rot"/> (bilinear of the permuted corner UVs).</summary>
    private static void MaskUV(int rot, float fx, float fy, out float u, out float v)
    {
        // Corners SW, SE, NE, NW with weights (1-fx)(1-fy), fx(1-fy), fx*fy, (1-fx)fy.
        var perm = LandUVRot[rot];
        var c0 = LandUV[perm[0]]; var c1 = LandUV[perm[1]]; var c2 = LandUV[perm[2]]; var c3 = LandUV[perm[3]];
        float w0 = (1 - fx) * (1 - fy), w1 = fx * (1 - fy), w2 = fx * fy, w3 = (1 - fx) * fy;
        u = c0.U * w0 + c1.U * w1 + c2.U * w2 + c3.U * w3;
        v = c0.V * w0 + c1.V * w1 + c2.V * w2 + c3.V * w3;
    }

    // ── A cell ───────────────────────────────────────────────────────────
    /// <summary>
    /// Paints one cell (its south-west vertex gx, gy) as a ppc x ppc block into <paramref name="rgb"/>, whose top-left pixel
    /// is (px0, py0) in an image <paramref name="width"/> pixels wide. <paramref name="shade"/> is the per-vertex light (1 = flat).
    /// </summary>
    public void PaintCell(int gx, int gy, byte[] type, byte[] road, float[] shade, int V, int ppc, byte[] rgb, int width, int px0, int py0)
    {
        int Ti(int x, int y) => type[Math.Min(y, V - 1) * V + Math.Min(x, V - 1)] == 255 ? 20 : type[Math.Min(y, V - 1) * V + Math.Min(x, V - 1)];
        int Ri(int x, int y) => road[Math.Min(y, V - 1) * V + Math.Min(x, V - 1)];
        int[] s = CellSurf(Ti(gx, gy), Ti(gx + 1, gy), Ti(gx + 1, gy + 1), Ti(gx, gy + 1), Ri(gx, gy), Ri(gx + 1, gy), Ri(gx + 1, gy + 1), Ri(gx, gy + 1));
        float sSW = shade[Math.Min(gy, V - 1) * V + Math.Min(gx, V - 1)], sSE = shade[Math.Min(gy, V - 1) * V + Math.Min(gx + 1, V - 1)],
              sNE = shade[Math.Min(gy + 1, V - 1) * V + Math.Min(gx + 1, V - 1)], sNW = shade[Math.Min(gy + 1, V - 1) * V + Math.Min(gx, V - 1)];
        bool anyOv = s[1] >= 0 && s[2] >= 0;
        bool anyRoad = s[10] >= 0 && s[11] >= 0;

        for (int j = 0; j < ppc; j++)
        {
            float fy = 1f - (j + 0.5f) / ppc;                // j = 0 is the cell's north edge
            for (int i = 0; i < ppc; i++)
            {
                float fx = (i + 0.5f) / ppc;
                float bu = fx, bv = 1f - fy;
                Sample(s[0], bu, bv, true, out float br, out float bg, out float bb, out _);

                // Overlays (maskBlend3).
                float or_ = 0, og = 0, ob = 0, oa = 0;
                if (anyOv)
                {
                    float ar0 = 1, ar1 = 1, ar2 = 1;
                    float cr0 = 0, cg0 = 0, cb0 = 0, cr1 = 0, cg1 = 0, cb1 = 0, cr2 = 0, cg2 = 0, cb2 = 0;
                    bool h0 = s[1] >= 0 && s[2] >= 0, h1 = s[4] >= 0 && s[5] >= 0, h2 = s[7] >= 0 && s[8] >= 0;
                    if (h0) { Sample(s[1], bu, bv, true, out cr0, out cg0, out cb0, out _); MaskUV(s[3], fx, fy, out float mu, out float mv); Sample(s[2], mu, mv, false, out float m, out _, out _, out _); ar0 = m; }
                    if (h1) { Sample(s[4], bu, bv, true, out cr1, out cg1, out cb1, out _); MaskUV(s[6], fx, fy, out float mu, out float mv); Sample(s[5], mu, mv, false, out float m, out _, out _, out _); ar1 = m; }
                    if (h2) { Sample(s[7], bu, bv, true, out cr2, out cg2, out cb2, out _); MaskUV(s[9], fx, fy, out float mu, out float mv); Sample(s[8], mu, mv, false, out float m, out _, out _, out _); ar2 = m; }
                    // t.a of an absent overlay counts as 1 in the shader's product; present ones use their mask.
                    float a0 = h0 ? ar0 : 1f, a1 = h1 ? ar1 : 1f, a2 = h2 ? ar2 : 1f;
                    float aR = 1f - a0 * a1 * a2;
                    if (aR >= 1e-4f)
                    {
                        float b0 = 1 - a0, b1 = 1 - a1, b2 = 1 - a2;
                        or_ = (b0 * cr0 + (1 - b0) * b1 * cr1 + (1 - b1) * b2 * cr2) / aR;
                        og = (b0 * cg0 + (1 - b0) * b1 * cg1 + (1 - b1) * b2 * cg2) / aR;
                        ob = (b0 * cb0 + (1 - b0) * b1 * cb1 + (1 - b1) * b2 * cb2) / aR;
                        oa = aR;
                    }
                }

                // Road.
                float rr = 0, rg = 0, rb = 0, ra = 0;
                if (anyRoad)
                {
                    Sample(s[10], bu, bv, true, out rr, out rg, out rb, out _);
                    MaskUV(s[12], fx, fy, out float mu, out float mv);
                    Sample(s[11], mu, mv, false, out float a0, out _, out _, out _);
                    ra = 1f - a0;
                    if (s[13] >= 0)
                    {
                        MaskUV(s[14], fx, fy, out float mu1, out float mv1);
                        Sample(s[13], mu1, mv1, false, out float a1, out _, out _, out _);
                        ra = 1f - a0 * a1;
                    }
                }

                float baseK = (1f - oa) * (1f - ra), ovK = oa * (1f - ra);
                float r = Math.Clamp(br * baseK, 0, 1) + Math.Clamp(or_ * ovK, 0, 1) + rr * ra;
                float g = Math.Clamp(bg * baseK, 0, 1) + Math.Clamp(og * ovK, 0, 1) + rg * ra;
                float b = Math.Clamp(bb * baseK, 0, 1) + Math.Clamp(ob * ovK, 0, 1) + rb * ra;

                float light = (sSW * (1 - fx) * (1 - fy) + sSE * fx * (1 - fy) + sNE * fx * fy + sNW * (1 - fx) * fy);
                long o = ((long)(py0 + j) * width + px0 + i) * 3;
                rgb[o] = (byte)Math.Clamp(r * light * Gain * 255f, 0, 255);
                rgb[o + 1] = (byte)Math.Clamp(g * light * Gain * 255f, 0, 255);
                rgb[o + 2] = (byte)Math.Clamp(b * light * Gain * 255f, 0, 255);
            }
        }
    }
}
