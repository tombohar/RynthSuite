using System;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// Highlights LANDBLOCKS that AC's physics treats as fully impassable water
/// — every vertex is a water terrain type (the "EntirelyWater" branch of
/// ACE LandblockStruct.CalcWater). When a regular character enters such a
/// landblock, LandCell.find_terrain_poly returns TransitionState.Collided
/// outright — no depth check needed, the player can't enter at all. Those
/// are the open-ocean / deep-lake landblocks the user can't walk through.
///
/// Partial-water landblocks (shorelines, wadeable rivers/ponds) are
/// intentionally NOT painted here: AC determines passability per position
/// via get_water_depth() against a water surface Z we don't have access
/// to from the dat files alone, so any heuristic over-marks or
/// under-marks. Use the SLOPE overlay alongside for shoreline drop-offs.
///
/// Iterates landblocks in a cell-radius around the player that can extend
/// into the 8 surrounding landblocks; emits triangles in PLAYER-landblock-
/// local coordinates so the engine's view-matrix shift keeps them anchored
/// when the camera crosses a landblock boundary.
/// </summary>
internal sealed class WaterOverlay
{
    // Water cells get a slightly larger bias than slopes because the water
    // surface usually sits exactly at the mesh Z, so even tiny z-fighting
    // here is visible.
    private const float HeightBias = 0.20f;

    private const float Cell = 24f;
    private const int   CellsPerSide = 8;

    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;
    private readonly TerrainSampler _terrain;

    // Auto-diagnostic: one log per landblock entry summarising what we found
    // (cell count, types present, types matched). Mirrors RadarRingOverlay's
    // pattern — saves the user from having to type /rv watertypes when water
    // isn't being marked where they expect it.
    private uint _lastDiagLandblock;

    public WaterOverlay(RynthCoreHost host, VisionSettings settings, TerrainSampler terrain)
    {
        _host = host;
        _settings = settings;
        _terrain = terrain;
    }

    public void Submit()
    {
        if (!_settings.ShowImpassableWater || !_host.HasNav3D || !_terrain.IsReady)
            return;
        if (!_host.HasNav3DTriangle) // requires API v60+
            return;

        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out _,
                out _, out _, out _, out _))
            return;
        if ((cellId & 0xFFFF) >= 0x100) return; // indoor
        if ((cellId >> 16) == 0) return;         // portalspace

        int playerLbX = (int)((cellId >> 24) & 0xFF);
        int playerLbY = (int)((cellId >> 16) & 0xFF);
        int playerCX = Math.Clamp((int)(px / Cell), 0, CellsPerSide - 1);
        int playerCY = Math.Clamp((int)(py / Cell), 0, CellsPerSide - 1);
        int radius = Math.Clamp(_settings.WaterRenderRadius, 1, 24);
        uint color = _settings.WaterColorArgb;

        const int LbSpan = 5; // -2..+2
        var lbCache = new LandblockData?[LbSpan * LbSpan];
        var lbCacheLoaded = new bool[LbSpan * LbSpan];
        // Per-landblock "fully water" flag — when every one of the 81 vertices
        // in a landblock matches a water terrain type, the entire 192×192 m
        // area is open sea / impassable lake. Paint all 64 cells regardless
        // of the per-cell corner rule, so deep open-water landblocks fill
        // solidly instead of leaving gaps where the per-cell filter is too
        // strict.
        var lbFullyWater = new bool[LbSpan * LbSpan];

        int submittedCells = 0;
        uint playerLandblock = cellId >> 16;

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int absCellX = playerCX + dx;
                int absCellY = playerCY + dy;

                int lbDX = FloorDiv(absCellX, CellsPerSide);
                int lbDY = FloorDiv(absCellY, CellsPerSide);
                int localCX = absCellX - lbDX * CellsPerSide;
                int localCY = absCellY - lbDY * CellsPerSide;

                int lbX = playerLbX + lbDX;
                int lbY = playerLbY + lbDY;
                if (lbX < 0 || lbX > 0xFF || lbY < 0 || lbY > 0xFF) continue;
                if (lbDX < -2 || lbDX > 2 || lbDY < -2 || lbDY > 2) continue;

                int cacheIdx = (lbDX + 2) * LbSpan + (lbDY + 2);
                LandblockData? lb;
                if (lbCacheLoaded[cacheIdx])
                {
                    lb = lbCache[cacheIdx];
                }
                else
                {
                    uint lbKey = (uint)((lbX << 8) | lbY);
                    lb = _terrain.LoadLandblock(lbKey);
                    lbCache[cacheIdx] = lb;
                    lbCacheLoaded[cacheIdx] = true;
                    if (lb != null) lbFullyWater[cacheIdx] = ComputeFullyWater(lb);
                }
                if (lb == null) continue;

                // Only paint fully-water (EntirelyWater) landblocks — that's
                // the precise AC condition for "physics collides immediately,
                // no depth math". Partial-water landblocks deliberately skipped
                // (see class-summary).
                if (!lbFullyWater[cacheIdx]) continue;

                float x0 = lbDX * 192f + localCX * Cell;
                float x1 = x0 + Cell;
                float z0 = lbDY * 192f + localCY * Cell;
                float z1 = z0 + Cell;
                float h00 = lb.GetVertexZ(localCX,     localCY)     + HeightBias;
                float h10 = lb.GetVertexZ(localCX + 1, localCY)     + HeightBias;
                float h01 = lb.GetVertexZ(localCX,     localCY + 1) + HeightBias;
                float h11 = lb.GetVertexZ(localCX + 1, localCY + 1) + HeightBias;

                // Water cells get both triangles filled — we don't care which
                // diagonal AC's PRNG picked; either pair of triangles covers
                // the same flat-ish square. Use SW→NE split for simplicity.
                _host.Nav3DAddTriangle(x0, h00, z0,  x1, h10, z0,  x1, h11, z1, color);
                _host.Nav3DAddTriangle(x0, h00, z0,  x1, h11, z1,  x0, h01, z1, color);
                submittedCells++;
            }
        }

        // One-shot per-landblock diagnostic. Whenever we cross to a new
        // landblock, log what we found: which terrain types are present in
        // the player's landblock, and how many cells we painted as water.
        // If the user expects water marked here but submittedCells=0, the
        // log will show what types ARE present so they can adjust
        // VisionSettings.WaterTerrainTypes.
        if (playerLandblock != _lastDiagLandblock)
        {
            _lastDiagLandblock = playerLandblock;
            LogWaterDiag(_terrain.LoadLandblock(playerLandblock), submittedCells, radius);
        }
    }

    private void LogWaterDiag(LandblockData? lb, int submittedCells, int radius)
    {
        if (lb == null) return;
        var seen = new System.Collections.Generic.SortedSet<int>();
        for (int ix = 0; ix < 9; ix++)
            for (int iy = 0; iy < 9; iy++)
                seen.Add(TerrainSampler.GetTerrainType(lb, ix, iy));
        string types = string.Join(",", seen);
        string configured = string.Join(",", _settings.WaterTerrainTypes);
        string anyMatch = "false";
        foreach (int t in seen)
            foreach (int w in _settings.WaterTerrainTypes)
                if (t == w) { anyMatch = "true"; goto done; }
        done:
        bool fullyWater = ComputeFullyWater(lb);
        _host.Log($"[RynthVision] Water lb=0x{lb.LandblockKey:X4} cellsPainted={submittedCells} r={radius} " +
                  $"presentTypes=[{types}] configured=[{configured}] anyMatch={anyMatch} " +
                  $"anyCorner={_settings.WaterAnyCorner} fullyWaterLb={fullyWater}");
    }

    // VisionSettings.WaterAnyCorner toggles between "any corner is water" (catches
    // shorelines, default) and "all four corners are water" (skips shoreline
    // cells, marks only fully-water cells). AC's own impassability check is
    // depth-based and not derivable from terrain types alone — for the
    // "actually impassable" signal, enable the SLOPE overlay alongside; the
    // overlap of red (slope-blocked) and blue (water) is where you can't walk.
    private bool IsWaterCell(LandblockData lb, int cx, int cy)
    {
        bool sw = IsWater(lb, cx,     cy);
        bool se = IsWater(lb, cx + 1, cy);
        bool nw = IsWater(lb, cx,     cy + 1);
        bool ne = IsWater(lb, cx + 1, cy + 1);
        return _settings.WaterAnyCorner
            ? (sw || se || nw || ne)
            : (sw && se && nw && ne);
    }

    private bool IsWater(LandblockData lb, int ix, int iy)
    {
        int type = TerrainSampler.GetTerrainType(lb, ix, iy);
        int[] set = _settings.WaterTerrainTypes;
        for (int i = 0; i < set.Length; i++)
            if (set[i] == type) return true;
        return false;
    }

    // True only when every one of the landblock's 81 vertices is a configured
    // water-type. That means there's NO dry land anywhere in the landblock,
    // so the entire 192×192 m area is open water and the player can't enter
    // any of it — paint the whole thing solid rather than risking gaps.
    private bool ComputeFullyWater(LandblockData lb)
    {
        for (int ix = 0; ix < 9; ix++)
            for (int iy = 0; iy < 9; iy++)
                if (!IsWater(lb, ix, iy)) return false;
        return true;
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}
