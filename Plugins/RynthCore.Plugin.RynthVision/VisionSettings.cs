using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RynthCore.Plugin.RynthVision;

/// <summary>
/// Feature toggles and colours for RynthVision overlays. Colours are ARGB
/// (0xAARRGGBB) to match the engine's Nav3D API. Persists to
/// %APPDATA%\RynthCore\rynthvision.json. RynthVision's own Vision window
/// (Ui/VisionWindow.cs, 2026-10-27) edits a copy (<see cref="Clone"/>) and queues
/// each change for the tick; the engine's Vision panels (its ImGui face on an
/// older engine, the Avalonia twin) still read and write these through the
/// plugin's JSON exports. Same file and keys either way, so nothing to migrate.
/// </summary>
internal sealed class VisionSettings
{
    public bool ShowRadarRing = true;
    public bool ShowUnclimbableSlopes = false;
    public bool ShowImpassableWater = false;

    public uint SlopeColorArgb = 0x60FF2020;       // semi-transparent red
    public uint WaterColorArgb = 0x600060FF;       // semi-transparent blue
    public uint RadarRingColorArgb = 0x80FFD000;   // semi-transparent gold

    public float RadarRangeWorld = 192f;
    public float RadarRingThickness = 2.0f;
    // Cylinder-wall height for the radar ring, in world meters. Independent
    // of thickness — a thin ring at e.g. 6 m height stands out against the
    // landscape without becoming visually heavy. Default 3 m, range 0.5–30.
    public float RadarRingHeight = 3.0f;

    // Cell-radius around the player (1 cell = 24 m, 8 cells per landblock).
    // The overlays read up to 3 landblocks out in each direction
    // (LandblockWindow), which covers the whole square for any radius up to
    // MaxRenderRadius from any cell of the player's landblock. Default 12 cells
    // (~288 m); 24 is the cap, where the engine's triangle budget (8192) is
    // already the limit (49×49 cells × 2 triangles for slopes alone).
    public const int MaxRenderRadius = 24;
    public int SlopeRenderRadius = 12;
    // Same cell-radius idea as SlopeRenderRadius. Default 12 matches slopes.
    public int WaterRenderRadius = 12;

    // Cell normal.Z threshold below which a triangle is treated as
    // unwalkable. Matches ACE PhysicsGlobals.FloorZ (= cos 48.4°). Lower
    // values are more permissive (only steeper slopes painted); higher
    // values are stricter (gentler slopes start painting). Range roughly
    // 0.3 (cos 72°, only near-cliffs) to 0.9 (cos 25°, mild hills).
    public float SlopeFloorZ = 0.66417414618662751f;

    // Vertical offset between the painted triangle and the actual terrain
    // mesh. Higher values reduce z-fight flicker on steep faces but make
    // the paint visibly hover above the ground. 0.05–0.3 m is the usable
    // range.
    public float SlopeHeightBias = 0.15f;

    // AC terrain-type indices treated as water. This MUST mirror ACE's
    // SurfChar[type]==1 lookup (LandblockStruct.cs:62-68) — indices 16..20:
    //   0x10 = 16 WaterRunning
    //   0x11 = 17 WaterStandingFresh
    //   0x12 = 18 WaterShallowSea
    //   0x13 = 19 WaterShallowStillSea
    //   0x14 = 20 WaterDeepSea
    // Landblocks whose every vertex is one of these ("EntirelyWater" per ACE
    // LandblockStruct.CalcWater) are always painted solid: a regular
    // character can't enter them. Other water cells follow the two options
    // below. Replaced (never mutated) on change; WaterOverlay relies on that.
    // An empty or all-invalid list from the panel resets to these defaults.
    private static readonly int[] DefaultWaterTerrainTypes = { 0x10, 0x11, 0x12, 0x13, 0x14 };
    public int[] WaterTerrainTypes = (int[])DefaultWaterTerrainTypes.Clone();

    // Outside fully-water landblocks: true = a cell is water if ANY of its
    // four corners is a water type (catches shorelines, where often only 1-2
    // vertices are water); false = only when all four corners are.
    public bool WaterAnyCorner = true;

    // Outside fully-water landblocks: true = paint only the water cells'
    // unwalkable triangles (normal Z below SlopeFloorZ) — the seafloor
    // drop-offs AC's slope physics blocks, i.e. water that is certainly
    // impassable (AC's wading depth check needs a water surface height the
    // dats don't give us). false = paint every water cell: a "where is
    // water" view that doesn't signal impassability.
    public bool WaterImpassableOnly = true;

    // Dungeon light (2026-10-27; Light/DungeonLight.cs): raises the ambient light AC draws
    // dungeons with (RynthCore's ambient light floor, plugin API v91). Off by default. Strength
    // 0-100 maps onto an ambient of 20 % (AC's own dungeon) to 100 %; the colour tints it (ARGB,
    // alpha always FF); the scope is where it lights (LightScope: 0 dungeons, 1 also buildings,
    // 2 also outdoors). The engine's Vision panel doesn't show these (plugin screens move out
    // of the engine, RynthCore CLAUDE.md): the Light tab of RynthVision's own Vision window and /rv light.
    public bool LightOn = false;
    public int LightStrength = Light.DungeonLight.DefaultStrength;
    public uint LightColorArgb = Light.DungeonLight.DefaultColor;
    public int LightScope = (int)Light.LightScope.Dungeons;

    // ── JSON bridge (manual build + JsonDocument parse — both AOT-safe) ───────

    /// <summary>
    /// Every setting. <see cref="ApplyJson"/> takes any subset of these keys,
    /// so a panel can send just the field that changed.
    /// </summary>
    public string ToJson()
    {
        var ci = CultureInfo.InvariantCulture;
        return "{"
            + $"\"radar\":{(ShowRadarRing ? 1 : 0)},"
            + $"\"slopes\":{(ShowUnclimbableSlopes ? 1 : 0)},"
            + $"\"water\":{(ShowImpassableWater ? 1 : 0)},"
            + $"\"slopeColor\":{SlopeColorArgb},"
            + $"\"waterColor\":{WaterColorArgb},"
            + $"\"radarColor\":{RadarRingColorArgb},"
            + $"\"radarRange\":{RadarRangeWorld.ToString("R", ci)},"
            + $"\"ringThick\":{RadarRingThickness.ToString("R", ci)},"
            + $"\"ringHeight\":{RadarRingHeight.ToString("R", ci)},"
            + $"\"slopeRadius\":{SlopeRenderRadius},"
            + $"\"slopeFloorZ\":{SlopeFloorZ.ToString("R", ci)},"
            + $"\"slopeBias\":{SlopeHeightBias.ToString("R", ci)},"
            + $"\"waterRadius\":{WaterRenderRadius},"
            + $"\"waterAnyCorner\":{(WaterAnyCorner ? 1 : 0)},"
            + $"\"waterImpassableOnly\":{(WaterImpassableOnly ? 1 : 0)},"
            + $"\"waterTypes\":[{string.Join(",", WaterTerrainTypes)}],"
            + $"\"light\":{(LightOn ? 1 : 0)},"
            + $"\"lightStrength\":{LightStrength},"
            + $"\"lightColor\":{LightColorArgb},"
            + $"\"lightScope\":{LightScope}"
            + "}";
    }

    public void ApplyJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("radar", out var v))   ShowRadarRing = v.GetInt32() != 0;
        if (r.TryGetProperty("slopes", out v))      ShowUnclimbableSlopes = v.GetInt32() != 0;
        if (r.TryGetProperty("water", out v))       ShowImpassableWater = v.GetInt32() != 0;
        if (r.TryGetProperty("slopeColor", out v))  SlopeColorArgb = v.GetUInt32();
        if (r.TryGetProperty("waterColor", out v))  WaterColorArgb = v.GetUInt32();
        if (r.TryGetProperty("radarColor", out v))  RadarRingColorArgb = v.GetUInt32();
        if (r.TryGetProperty("radarRange", out v))  RadarRangeWorld = (float)v.GetDouble();
        if (r.TryGetProperty("ringThick", out v))   RadarRingThickness = (float)v.GetDouble();
        if (r.TryGetProperty("ringHeight", out v))  RadarRingHeight = (float)v.GetDouble();
        if (r.TryGetProperty("slopeRadius", out v)) SlopeRenderRadius = v.GetInt32();
        if (r.TryGetProperty("slopeFloorZ", out v)) SlopeFloorZ = (float)v.GetDouble();
        if (r.TryGetProperty("slopeBias",   out v)) SlopeHeightBias = (float)v.GetDouble();
        if (r.TryGetProperty("waterRadius", out v)) WaterRenderRadius = v.GetInt32();
        if (r.TryGetProperty("waterAnyCorner", out v)) WaterAnyCorner = v.GetInt32() != 0;
        if (r.TryGetProperty("waterImpassableOnly", out v)) WaterImpassableOnly = v.GetInt32() != 0;
        if (r.TryGetProperty("waterTypes", out v) && v.ValueKind == JsonValueKind.Array)
        {
            var list = new List<int>();
            foreach (var e in v.EnumerateArray())
                if (e.TryGetInt32(out int n)) list.Add(n);
            WaterTerrainTypes = NormalizeWaterTypes(list);
        }
        // Out-of-range light values are pulled into range (a hand-edited file, an older panel).
        if (r.TryGetProperty("light", out v))         LightOn = v.GetInt32() != 0;
        if (r.TryGetProperty("lightStrength", out v)) LightStrength = Math.Clamp(v.GetInt32(), 0, 100);
        if (r.TryGetProperty("lightColor", out v))    LightColorArgb = 0xFF000000u | (v.GetUInt32() & 0x00FFFFFFu);
        if (r.TryGetProperty("lightScope", out v))    LightScope = Math.Clamp(v.GetInt32(), 0, 2);
    }

    /// <summary>
    /// The water types to keep from <paramref name="types"/>: 0..31, each once, in order. An empty
    /// (or all-invalid) list would match nothing and paint no water ever, so it means "back to the
    /// defaults"; the window (or a panel) then shows the defaults.
    /// </summary>
    public static int[] NormalizeWaterTypes(IEnumerable<int> types)
    {
        var list = new List<int>();
        foreach (int n in types)
            if (n >= 0 && n <= 31 && !list.Contains(n)) list.Add(n);
        return list.Count > 0 ? list.ToArray() : (int[])DefaultWaterTerrainTypes.Clone();
    }

    /// <summary>
    /// A copy for the window (published by the tick, read on AC's render thread). The water list is
    /// shared: it is replaced, never changed in place.
    /// </summary>
    public VisionSettings Clone() => (VisionSettings)MemberwiseClone();

    // ── Persistence ───────────────────────────────────────────────────────────

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RynthCore", "rynthvision.json");

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToJson());
        }
        catch { }
    }

    public void Load()
    {
        try
        {
            string path = FilePath;
            if (File.Exists(path)) ApplyJson(File.ReadAllText(path));
        }
        catch { }
    }
}
