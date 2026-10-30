namespace RynthNav.Baker;

/// <summary>
/// DXT1 (BC1) texture compression: each 4x4 block of RGB pixels becomes 8 bytes (two RGB565 endpoints and
/// 16 two-bit indices), 6:1 against 24-bit RGB, and a D3D9 device holds the blocks as they are, so the
/// engine uploads them untouched. The endpoints are found along the block's principal colour axis (a few
/// power iterations on its covariance), then the pixels take the nearest of the four palette colours.
/// Used for the World Map's tiles (worldmap.rmap version 2); Decode is the reference for tests.
/// </summary>
internal static class Dxt1
{
    public const int BlockBytes = 8;

    /// <summary>Compresses a w x h RGB image (both multiples of 4) to w/4 * h/4 blocks, row-major, top row first.</summary>
    public static byte[] Encode(byte[] rgb, int w, int h)
    {
        if (w % 4 != 0 || h % 4 != 0) throw new ArgumentException("size must be a multiple of 4");
        var outp = new byte[(w / 4) * (h / 4) * BlockBytes];
        Span<int> px = stackalloc int[48];
        int o = 0;
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int s = ((by + y) * w + bx + x) * 3, d = (y * 4 + x) * 3;
                        px[d] = rgb[s]; px[d + 1] = rgb[s + 1]; px[d + 2] = rgb[s + 2];
                    }
                EncodeBlock(px, outp.AsSpan(o, BlockBytes));
                o += BlockBytes;
            }
        return outp;
    }

    private static ushort To565(float r, float g, float b)
    {
        int ri = Math.Clamp((int)MathF.Round(r * 31f / 255f), 0, 31), gi = Math.Clamp((int)MathF.Round(g * 63f / 255f), 0, 63), bi = Math.Clamp((int)MathF.Round(b * 31f / 255f), 0, 31);
        return (ushort)((ri << 11) | (gi << 5) | bi);
    }

    private static void From565(ushort c, out int r, out int g, out int b)
    {
        int r5 = c >> 11, g6 = (c >> 5) & 63, b5 = c & 31;
        r = (r5 << 3) | (r5 >> 2); g = (g6 << 2) | (g6 >> 4); b = (b5 << 3) | (b5 >> 2);
    }

    private static void EncodeBlock(ReadOnlySpan<int> px, Span<byte> block)
    {
        // Mean and covariance.
        float mr = 0, mg = 0, mb = 0;
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i * 3], g = px[i * 3 + 1], b = px[i * 3 + 2];
            mr += r; mg += g; mb += b;
            if (r < minR) minR = r; if (g < minG) minG = g; if (b < minB) minB = b;
            if (r > maxR) maxR = r; if (g > maxG) maxG = g; if (b > maxB) maxB = b;
        }
        mr /= 16; mg /= 16; mb /= 16;
        if (maxR == minR && maxG == minG && maxB == minB)
        {
            ushort solid = To565(minR, minG, minB);
            block[0] = (byte)solid; block[1] = (byte)(solid >> 8); block[2] = block[0]; block[3] = block[1];
            block[4] = block[5] = block[6] = block[7] = 0;
            return;
        }
        float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int i = 0; i < 16; i++)
        {
            float r = px[i * 3] - mr, g = px[i * 3 + 1] - mg, b = px[i * 3 + 2] - mb;
            crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
        }
        // Dominant axis by power iteration, started along the box diagonal.
        float ax = maxR - minR, ay = maxG - minG, az = maxB - minB;
        for (int it = 0; it < 4; it++)
        {
            float nx = crr * ax + crg * ay + crb * az, ny = crg * ax + cgg * ay + cgb * az, nz = crb * ax + cgb * ay + cbb * az;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) break;
            ax = nx / len; ay = ny / len; az = nz / len;
        }
        // Extent of the pixels along it: the endpoints.
        float tMin = float.MaxValue, tMax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float t = (px[i * 3] - mr) * ax + (px[i * 3 + 1] - mg) * ay + (px[i * 3 + 2] - mb) * az;
            if (t < tMin) tMin = t; if (t > tMax) tMax = t;
        }
        ushort c0 = To565(mr + ax * tMax, mg + ay * tMax, mb + az * tMax);
        ushort c1 = To565(mr + ax * tMin, mg + ay * tMin, mb + az * tMin);
        if (c0 == c1)
        {
            block[0] = (byte)c0; block[1] = (byte)(c0 >> 8); block[2] = block[0]; block[3] = block[1];
            block[4] = block[5] = block[6] = block[7] = 0;
            return;
        }
        if (c0 < c1) (c0, c1) = (c1, c0);                       // c0 > c1: the four-colour mode

        From565(c0, out int r0, out int g0, out int b0);
        From565(c1, out int r1, out int g1, out int b1);
        Span<int> pal = stackalloc int[12];
        pal[0] = r0; pal[1] = g0; pal[2] = b0;
        pal[3] = r1; pal[4] = g1; pal[5] = b1;
        pal[6] = (2 * r0 + r1) / 3; pal[7] = (2 * g0 + g1) / 3; pal[8] = (2 * b0 + b1) / 3;
        pal[9] = (r0 + 2 * r1) / 3; pal[10] = (g0 + 2 * g1) / 3; pal[11] = (b0 + 2 * b1) / 3;

        block[0] = (byte)c0; block[1] = (byte)(c0 >> 8); block[2] = (byte)c1; block[3] = (byte)(c1 >> 8);
        for (int row = 0; row < 4; row++)
        {
            int bits = 0;
            for (int col = 0; col < 4; col++)
            {
                int i = row * 4 + col;
                int r = px[i * 3], g = px[i * 3 + 1], b = px[i * 3 + 2];
                int best = 0, bestD = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int dr = r - pal[k * 3], dg = g - pal[k * 3 + 1], db = b - pal[k * 3 + 2];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bestD) { bestD = d; best = k; }
                }
                bits |= best << (col * 2);
            }
            block[4 + row] = (byte)bits;
        }
    }

    /// <summary>Decompresses one block to 16 RGB pixels (row-major) - the reference a GPU follows. For tests.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgb48)
    {
        ushort c0 = (ushort)(block[0] | (block[1] << 8)), c1 = (ushort)(block[2] | (block[3] << 8));
        From565(c0, out int r0, out int g0, out int b0);
        From565(c1, out int r1, out int g1, out int b1);
        Span<int> pal = stackalloc int[12];
        pal[0] = r0; pal[1] = g0; pal[2] = b0; pal[3] = r1; pal[4] = g1; pal[5] = b1;
        if (c0 > c1)
        {
            pal[6] = (2 * r0 + r1) / 3; pal[7] = (2 * g0 + g1) / 3; pal[8] = (2 * b0 + b1) / 3;
            pal[9] = (r0 + 2 * r1) / 3; pal[10] = (g0 + 2 * g1) / 3; pal[11] = (b0 + 2 * b1) / 3;
        }
        else
        {
            pal[6] = (r0 + r1) / 2; pal[7] = (g0 + g1) / 2; pal[8] = (b0 + b1) / 2;
            pal[9] = 0; pal[10] = 0; pal[11] = 0;
        }
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
            {
                int idx = (block[4 + row] >> (col * 2)) & 3, d = (row * 4 + col) * 3;
                rgb48[d] = (byte)pal[idx * 3]; rgb48[d + 1] = (byte)pal[idx * 3 + 1]; rgb48[d + 2] = (byte)pal[idx * 3 + 2];
            }
    }

    /// <summary>Decompresses a whole w x h image (for tests).</summary>
    public static byte[] Decode(byte[] blocks, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        Span<byte> px = stackalloc byte[48];
        int o = 0;
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                DecodeBlock(blocks.AsSpan(o, BlockBytes), px);
                o += BlockBytes;
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        Buffer.BlockCopy(px.Slice((y * 4 + x) * 3, 3).ToArray(), 0, rgb, ((by + y) * w + bx + x) * 3, 3);
            }
        return rgb;
    }
}
