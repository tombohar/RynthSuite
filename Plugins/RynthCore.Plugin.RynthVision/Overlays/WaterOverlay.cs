using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// Highlights water the player can't walk through.
///
/// Fully-water landblocks — every vertex a water terrain type, the
/// "EntirelyWater" branch of ACE LandblockStruct.CalcWater — are painted solid:
/// a regular character can't enter them at all (open ocean, deep lakes).
///
/// Elsewhere (shorelines, rivers, ponds) a cell counts as water when ANY of its
/// four corners is a water type (<see cref="VisionSettings.WaterAnyCorner"/>)
/// or, with that off, when all four are. AC decides how deep you can wade from
/// a water-surface depth the dat files don't give us, so with
/// <see cref="VisionSettings.WaterImpassableOnly"/> on (the default) only the
/// water cells' unwalkable triangles are painted — the seafloor drop-offs AC's
/// slope physics blocks, which are certainly impassable. With it off every
/// water cell is painted: a "where is water" view, not an impassability one.
///
/// Cells are visited nearest-first out to the water radius (up to 3 landblocks
/// away, <see cref="LandblockWindow"/>) and emitted in PLAYER-landblock-local
/// coordinates so the engine's view-matrix shift keeps them anchored when the
/// camera crosses a landblock boundary.
/// </summary>
internal sealed class WaterOverlay
{
    // Water cells get a slightly larger bias than slopes because the water
    // surface usually sits exactly at the mesh Z, so even tiny z-fighting
    // here is visible.
    private const float HeightBias = 0.20f;
    private const int MaxInfoCache = 128;

    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;

    // Per-landblock water masks, rebuilt when the water-type list changes
    // (VisionSettings replaces the array on every change, so a reference
    // check is enough). Well above the 49 landblocks of a full window.
    private sealed class WaterInfo
    {
        public ulong AnyCornerMask; // bit cx*8+cy: some corner of the cell is water
        public ulong AllCornerMask; // bit cx*8+cy: every corner is water
        public bool FullyWater;     // all 81 vertices are water
    }
    private readonly Dictionary<uint, WaterInfo> _info = new();
    private int[]? _infoTypes;

    // /rv debug: one log per landblock entry summarising what we found.
    private uint _lastDiagLandblock;

    public WaterOverlay(RynthCoreHost host, VisionSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    public void Submit(TickContext ctx)
    {
        if (!_settings.ShowImpassableWater || ctx.Terrain == null || !ctx.OnLandscape)
            return;
        if (!_host.HasNav3DTriangle) // requires API v60+
            return;

        LandblockWindow window = ctx.Terrain;
        const int cells = TerrainPaint.CellsPerSide;
        int playerCX = Math.Clamp((int)(ctx.X / TerrainPaint.Cell), 0, cells - 1);
        int playerCY = Math.Clamp((int)(ctx.Y / TerrainPaint.Cell), 0, cells - 1);
        int radius = Math.Clamp(_settings.WaterRenderRadius, 1, VisionSettings.MaxRenderRadius);
        uint color = _settings.WaterColorArgb;
        bool anyCorner = _settings.WaterAnyCorner;
        bool impassableOnly = _settings.WaterImpassableOnly;
        float floorZ = Math.Clamp(_settings.SlopeFloorZ, 0.05f, 0.999f);

        if (!ReferenceEquals(_infoTypes, _settings.WaterTerrainTypes))
        {
            _info.Clear();
            _infoTypes = _settings.WaterTerrainTypes;
        }

        int submittedCells = 0;
        for (int r = 0; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) < r && Math.Abs(dy) < r) continue;

                    int absCellX = playerCX + dx;
                    int absCellY = playerCY + dy;
                    int lbDX = TerrainPaint.FloorDiv(absCellX, cells);
                    int lbDY = TerrainPaint.FloorDiv(absCellY, cells);
                    int localCX = absCellX - lbDX * cells;
                    int localCY = absCellY - lbDY * cells;

                    LandblockData? lb = window.Get(lbDX, lbDY);
                    if (lb == null) continue;
                    WaterInfo info = GetInfo(lb);

                    bool swToNe = TerrainSampler.SwToNeCut(lb.LandblockKey, localCX, localCY);
                    if (info.FullyWater)
                    {
                        TerrainPaint.EmitCell(_host, lb, lbDX, lbDY, localCX, localCY, swToNe, true, true, HeightBias, color);
                        submittedCells++;
                        continue;
                    }

                    ulong bit = 1UL << (localCX * cells + localCY);
                    if (((anyCorner ? info.AnyCornerMask : info.AllCornerMask) & bit) == 0)
                        continue;

                    bool paintT1 = true, paintT2 = true;
                    if (impassableOnly)
                    {
                        TerrainSampler.GetTrianglePassability(lb, localCX, localCY, swToNe,
                            out bool t1ok, out bool t2ok, floorZ);
                        paintT1 = !t1ok;
                        paintT2 = !t2ok;
                        if (!paintT1 && !paintT2) continue;
                    }

                    TerrainPaint.EmitCell(_host, lb, lbDX, lbDY, localCX, localCY, swToNe, paintT1, paintT2, HeightBias, color);
                    submittedCells++;
                }
            }
        }

        if (ctx.Debug && ctx.Landblock != _lastDiagLandblock)
        {
            _lastDiagLandblock = ctx.Landblock;
            LogWaterDiag(window.Get(0, 0), submittedCells, radius);
        }
    }

    private WaterInfo GetInfo(LandblockData lb)
    {
        if (_info.TryGetValue(lb.LandblockKey, out WaterInfo? info))
            return info;

        info = new WaterInfo();
        bool fully = true;
        Span<bool> water = stackalloc bool[81];
        for (int ix = 0; ix < 9; ix++)
            for (int iy = 0; iy < 9; iy++)
            {
                bool w = IsWater(lb, ix, iy);
                water[ix * 9 + iy] = w;
                fully &= w;
            }
        for (int cx = 0; cx < 8; cx++)
            for (int cy = 0; cy < 8; cy++)
            {
                bool sw = water[cx * 9 + cy], se = water[(cx + 1) * 9 + cy];
                bool nw = water[cx * 9 + cy + 1], ne = water[(cx + 1) * 9 + cy + 1];
                ulong bit = 1UL << (cx * 8 + cy);
                if (sw || se || nw || ne) info.AnyCornerMask |= bit;
                if (sw && se && nw && ne) info.AllCornerMask |= bit;
            }
        info.FullyWater = fully;

        if (_info.Count >= MaxInfoCache) _info.Clear();
        _info[lb.LandblockKey] = info;
        return info;
    }

    private bool IsWater(LandblockData lb, int ix, int iy)
    {
        int type = TerrainSampler.GetTerrainType(lb, ix, iy);
        int[] set = _settings.WaterTerrainTypes;
        for (int i = 0; i < set.Length; i++)
            if (set[i] == type) return true;
        return false;
    }

    private void LogWaterDiag(LandblockData? lb, int submittedCells, int radius)
    {
        if (lb == null) return;
        var seen = new SortedSet<int>();
        for (int ix = 0; ix < 9; ix++)
            for (int iy = 0; iy < 9; iy++)
                seen.Add(TerrainSampler.GetTerrainType(lb, ix, iy));
        bool anyMatch = false;
        foreach (int t in seen)
            if (Array.IndexOf(_settings.WaterTerrainTypes, t) >= 0) { anyMatch = true; break; }
        _host.Log($"[RynthVision] Water lb=0x{lb.LandblockKey:X4} cellsPainted={submittedCells} r={radius} " +
                  $"presentTypes=[{string.Join(",", seen)}] configured=[{string.Join(",", _settings.WaterTerrainTypes)}] " +
                  $"anyMatch={anyMatch} anyCorner={_settings.WaterAnyCorner} impassableOnly={_settings.WaterImpassableOnly} " +
                  $"fullyWaterLb={GetInfo(lb).FullyWater}");
    }
}
