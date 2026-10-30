using RynthNav.Baker;

namespace RynthNav.MapTests;

/// <summary>The baker's DXT1 encoder against the standard decode (what a GPU does with the blocks).</summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        Sizes();
        Solid();
        TwoColours();
        Gradient();
        Noise();
        Rejects();
        Console.WriteLine(_failed == 0 ? $"OK: {_checks} checks" : $"FAILED: {_failed} of {_checks} checks");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) { _failed++; Console.WriteLine("FAIL: " + what); }
    }

    private static byte[] Fill(int w, int h, Func<int, int, (int R, int G, int B)> f)
    {
        var img = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = f(x, y);
                int o = (y * w + x) * 3;
                img[o] = (byte)r; img[o + 1] = (byte)g; img[o + 2] = (byte)b;
            }
        return img;
    }

    private static double MeanAbsError(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double mse = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; mse += d * d; }
        mse /= a.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static void Sizes()
    {
        byte[] blocks = Dxt1.Encode(new byte[256 * 256 * 3], 256, 256);
        Check(blocks.Length == 256 * 256 / 2, "a 256 x 256 tile is 32768 bytes (4 bits a pixel), got " + blocks.Length);
        Check(Dxt1.Encode(new byte[8 * 4 * 3], 8, 4).Length == 16, "an 8 x 4 image is two blocks");
    }

    private static void Solid()
    {
        foreach (var c in new[] { (0, 0, 0), (255, 255, 255), (28, 66, 118), (120, 150, 60) })
        {
            byte[] img = Fill(8, 8, (x, y) => c);
            byte[] back = Dxt1.Decode(Dxt1.Encode(img, 8, 8), 8, 8);
            // RGB565 keeps 5/6/5 bits: within 8 of the colour on each channel.
            bool near = true;
            for (int i = 0; i < back.Length; i += 3)
                if (Math.Abs(back[i] - c.Item1) > 8 || Math.Abs(back[i + 1] - c.Item2) > 4 || Math.Abs(back[i + 2] - c.Item3) > 8) near = false;
            Check(near, $"a solid {c} block comes back within the 565 step");
        }
        // A solid block never uses the transparent mode: every index is 0 (the colour itself).
        byte[] blk = Dxt1.Encode(Fill(4, 4, (x, y) => (50, 100, 150)), 4, 4);
        Check(blk[4] == 0 && blk[5] == 0 && blk[6] == 0 && blk[7] == 0, "a solid block's indices are all 0");
    }

    private static void TwoColours()
    {
        byte[] img = Fill(4, 4, (x, y) => x < 2 ? (200, 40, 40) : (30, 60, 220));
        byte[] blocks = Dxt1.Encode(img, 4, 4);
        ushort c0 = (ushort)(blocks[0] | (blocks[1] << 8)), c1 = (ushort)(blocks[2] | (blocks[3] << 8));
        Check(c0 > c1, "the four-colour mode (colour 0 above colour 1), never the transparent one");
        Check(MeanAbsError(img, Dxt1.Decode(blocks, 4, 4)) < 6, "two flat colours come back close");
    }

    private static void Gradient()
    {
        byte[] img = Fill(256, 256, (x, y) => (x, y, (x + y) / 2));
        byte[] blocks = Dxt1.Encode(img, 256, 256);
        byte[] back = Dxt1.Decode(blocks, 256, 256);
        double mae = MeanAbsError(img, back), psnr = Psnr(img, back);
        Check(mae < 5, $"a colour gradient: mean error {mae:F2} (under 5)");
        Check(psnr > 33, $"a colour gradient: PSNR {psnr:F1} dB (over 33)");
        // No block uses the transparent mode.
        bool fourColour = true;
        for (int i = 0; i < blocks.Length; i += 8)
        {
            ushort a = (ushort)(blocks[i] | (blocks[i + 1] << 8)), b = (ushort)(blocks[i + 2] | (blocks[i + 3] << 8));
            if (a < b) fourColour = false;
        }
        Check(fourColour, "every block of the gradient is in the four-colour mode");
    }

    private static void Noise()
    {
        // Ground texture stand-in: a mid green-brown with grain. DXT1 is lossy but must stay recognisable.
        var rnd = new Random(11);
        byte[] img = Fill(128, 128, (x, y) =>
        {
            int n = rnd.Next(-25, 26);
            return (110 + n, 120 + n + rnd.Next(-6, 7), 60 + n / 2);
        });
        byte[] back = Dxt1.Decode(Dxt1.Encode(img, 128, 128), 128, 128);
        double psnr = Psnr(img, back);
        Check(psnr > 24, $"grainy ground: PSNR {psnr:F1} dB (over 24)");
        // The average colour of the tile is kept (a map zoomed out averages these).
        double ma = 0, mb = 0;
        for (int i = 0; i < img.Length; i++) { ma += img[i]; mb += back[i]; }
        Check(Math.Abs(ma - mb) / img.Length < 2, "the average colour of grainy ground is kept");
    }

    private static void Rejects()
    {
        bool threw = false;
        try { Dxt1.Encode(new byte[5 * 4 * 3], 5, 4); } catch (ArgumentException) { threw = true; }
        Check(threw, "a width that isn't a multiple of 4 is refused");
    }
}
