using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.TerrainData;

namespace RynthNav.Baker;

/// <summary>
/// Dereth's terrain as one global vertex grid (2049 x 2049: the east / north edge vertex of a landblock is
/// the west / south edge vertex of its neighbour), read from client_cell_1.dat: per vertex the terrain type
/// (0-31; 255 = no landblock, open sea), the road bits and the height. Row-major, index y * V + x, y
/// north. Shared by the flat map render, the textured render and the world bake.
/// </summary>
internal sealed class WorldGrid
{
    public const int Blocks = 256, Side = 8, V = Blocks * Side + 1;
    public const float CellUnits = 24f;

    public readonly byte[] Type = new byte[V * V];
    public readonly byte[] Road = new byte[V * V];
    public readonly float[] Height = new float[V * V];
    public int Loaded { get; private set; }

    private WorldGrid() { Array.Fill(Type, (byte)255); }

    /// <summary>Reads landblocks lx0..lx1, ly0..ly1 (inclusive; the rest stay open sea).</summary>
    public static WorldGrid Load(TerrainSampler sampler, int lx0, int lx1, int ly0, int ly1, Action<string>? log = null)
    {
        var g = new WorldGrid();
        lx0 = Math.Max(0, lx0); ly0 = Math.Max(0, ly0); lx1 = Math.Min(Blocks - 1, lx1); ly1 = Math.Min(Blocks - 1, ly1);
        for (int lx = lx0; lx <= lx1; lx++)
        {
            for (int ly = ly0; ly <= ly1; ly++)
            {
                LandblockData? lb = null;
                try { lb = sampler.LoadLandblock((uint)((lx << 8) | ly)); } catch { }
                if (lb == null || lb.TerrainWords == null) continue;
                g.Loaded++;
                for (int ix = 0; ix <= Side; ix++)
                    for (int iy = 0; iy <= Side; iy++)
                    {
                        int gi = (ly * Side + iy) * V + (lx * Side + ix);
                        ushort w = lb.TerrainWords[ix * 9 + iy];
                        g.Type[gi] = (byte)((w >> 2) & 0x1F);
                        g.Road[gi] = (byte)(w & 3);
                        g.Height[gi] = lb.HeightsZ[ix, iy];
                    }
            }
            if (log != null && lx % 16 == 0) log($"  read landblock column {lx:X2}");
        }
        return g;
    }

    /// <summary>How many vertices of each terrain type (0-31).</summary>
    public long[] TypeCounts()
    {
        var seen = new long[32];
        foreach (byte t in Type) if (t < 32) seen[t]++;
        return seen;
    }

    /// <summary>
    /// Light per vertex from the slope (central differences of the heights, lit from the north-west, 45 degrees
    /// up): 1.0 on flat ground. <paramref name="exaggeration"/> stretches the relief (Dereth's is gentle).
    /// </summary>
    public float[] Shade(float exaggeration)
    {
        var shade = new float[V * V];
        for (int gy = 0; gy < V; gy++)
            for (int gx = 0; gx < V; gx++)
            {
                int gi = gy * V + gx;
                if (Type[gi] == 255) { shade[gi] = 1f; continue; }
                float hE = Height[gy * V + Math.Min(gx + 1, V - 1)], hW = Height[gy * V + Math.Max(gx - 1, 0)];
                float hN = Height[Math.Min(gy + 1, V - 1) * V + gx], hS = Height[Math.Max(gy - 1, 0) * V + gx];
                float dx = (hE - hW) / (2 * CellUnits), dy = (hN - hS) / (2 * CellUnits);
                float nx = -dx * exaggeration, ny = -dy * exaggeration, nz = 1f;
                float inv = 1f / MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                const float lx = -0.5f, ly = 0.5f, lz = 0.707f;         // toward the light
                float lit = (nx * lx + ny * ly + nz * lz) * inv;
                shade[gi] = Math.Clamp(lit / lz, 0.55f, 1.35f);
            }
        return shade;
    }
}
