using System;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// What every overlay needs for one tick, sampled once in OnTick so the radar
/// ring, slopes and water all draw from the same player pose (sampling it per
/// overlay let one of them land on the other side of a landblock boundary).
/// </summary>
internal sealed class TickContext
{
    public const float BlockLength = 192f;

    public uint CellId;
    public float X, Y, Z;   // AC frame: X=east, Y=north, Z=up, landblock-local metres

    /// <summary>
    /// True when the pose is in landscape coordinates: an outdoor cell, or a
    /// building interior on the landscape (see RynthVisionPlugin.IsOnLandscape).
    /// </summary>
    public bool OnLandscape;

    /// <summary>The terrain around the player, or null while terrain data isn't loaded.</summary>
    public LandblockWindow? Terrain;

    /// <summary>/rv debug: per-crossing diagnostics.</summary>
    public bool Debug;

    public uint Landblock => CellId >> 16;
    public int LandblockX => (int)((CellId >> 24) & 0xFF);
    public int LandblockY => (int)((CellId >> 16) & 0xFF);
}

/// <summary>
/// The landblocks within ±<see cref="Span"/> of the player's, loaded on first
/// use each tick and shared by every overlay. Reset per tick; the arrays are
/// reused. Radius 24 cells from any cell of the player's landblock reaches at
/// most 3 landblocks away, so ±3 covers every radius the settings allow.
/// </summary>
internal sealed class LandblockWindow
{
    public const int Span = 3;
    private const int Size = 2 * Span + 1;

    private readonly TerrainSampler _terrain;
    private readonly LandblockData?[] _data = new LandblockData?[Size * Size];
    private readonly bool[] _loaded = new bool[Size * Size];
    private int _baseX, _baseY;

    public LandblockWindow(TerrainSampler terrain) => _terrain = terrain;

    public void Reset(int playerLbX, int playerLbY)
    {
        _baseX = playerLbX;
        _baseY = playerLbY;
        Array.Clear(_loaded);
        Array.Clear(_data);
    }

    /// <summary>Landblock at (dx, dy) landblocks from the player's; null outside the window, the map, or the dat.</summary>
    public LandblockData? Get(int lbDX, int lbDY)
    {
        if (lbDX < -Span || lbDX > Span || lbDY < -Span || lbDY > Span) return null;
        int lbX = _baseX + lbDX, lbY = _baseY + lbDY;
        if (lbX < 0 || lbX > 0xFF || lbY < 0 || lbY > 0xFF) return null;

        int idx = (lbDX + Span) * Size + (lbDY + Span);
        if (!_loaded[idx])
        {
            _data[idx] = _terrain.LoadLandblock((uint)((lbX << 8) | lbY));
            _loaded[idx] = true;
        }
        return _data[idx];
    }

    /// <summary>Ground Z at (x, y) in player-landblock-local metres (may lie in a neighbouring landblock).</summary>
    public bool TryGetTerrainZ(float x, float y, out float z)
    {
        int lbDX = (int)MathF.Floor(x / TickContext.BlockLength);
        int lbDY = (int)MathF.Floor(y / TickContext.BlockLength);
        LandblockData? lb = Get(lbDX, lbDY);
        if (lb == null) { z = 0f; return false; }
        z = TerrainSampler.GetTerrainZ(lb, x - lbDX * TickContext.BlockLength, y - lbDY * TickContext.BlockLength);
        return true;
    }
}

/// <summary>Cell geometry shared by the slope and water overlays.</summary>
internal static class TerrainPaint
{
    public const float Cell = 24f;
    public const int CellsPerSide = 8;

    /// <summary>Floor division: FloorDiv(-1, 8) = -1, not 0.</summary>
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    /// <summary>
    /// Submits the chosen halves of cell (cx, cy) of the landblock at
    /// (lbDX, lbDY) from the player's, in player-landblock-local coordinates,
    /// lifted by <paramref name="bias"/>. Halves follow AC's split
    /// (<see cref="TerrainSampler.SwToNeCut"/>): with swToNe, t1 = SE half
    /// (SW,SE,NE) and t2 = NW half (SW,NE,NW); otherwise t1 = SW half
    /// (SW,SE,NW) and t2 = NE half (SE,NE,NW) — the same pairing as
    /// <see cref="TerrainSampler.GetTrianglePassability(LandblockData,int,int,bool,out bool,out bool,float)"/>.
    /// Nav3D is Y-up: (x=east, y=up, z=north).
    /// </summary>
    public static void EmitCell(RynthCoreHost host, LandblockData lb, int lbDX, int lbDY, int cx, int cy,
        bool swToNe, bool t1, bool t2, float bias, uint color)
    {
        float x0 = lbDX * TickContext.BlockLength + cx * Cell;
        float x1 = x0 + Cell;
        float z0 = lbDY * TickContext.BlockLength + cy * Cell;
        float z1 = z0 + Cell;
        float h00 = lb.GetVertexZ(cx,     cy)     + bias;
        float h10 = lb.GetVertexZ(cx + 1, cy)     + bias;
        float h01 = lb.GetVertexZ(cx,     cy + 1) + bias;
        float h11 = lb.GetVertexZ(cx + 1, cy + 1) + bias;

        if (swToNe)
        {
            if (t1) host.Nav3DAddTriangle(x0, h00, z0,  x1, h10, z0,  x1, h11, z1, color);
            if (t2) host.Nav3DAddTriangle(x0, h00, z0,  x1, h11, z1,  x0, h01, z1, color);
        }
        else
        {
            if (t1) host.Nav3DAddTriangle(x0, h00, z0,  x1, h10, z0,  x0, h01, z1, color);
            if (t2) host.Nav3DAddTriangle(x1, h10, z0,  x1, h11, z1,  x0, h01, z1, color);
        }
    }
}
