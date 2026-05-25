using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RynthCore.Plugin.RynthVision;

/// <summary>
/// Feature toggles and colours for RynthVision overlays. Colours are ARGB
/// (0xAARRGGBB) to match the engine's Nav3D API. Persists to
/// %APPDATA%\RynthCore\rynthvision.json; the engine's Avalonia panel reads and
/// writes these through the plugin's JSON bridge exports.
/// </summary>
internal sealed class VisionSettings
{
    public bool ShowRadarRing = true;
    public bool ShowUnclimbableSlopes = false;
    public bool ShowImpassableWater = false;
    public bool ShowDungeonLighting = false;

    public uint SlopeColorArgb = 0x60FF2020;       // semi-transparent red
    public uint WaterColorArgb = 0x600060FF;       // semi-transparent blue
    public uint RadarRingColorArgb = 0x80FFD000;   // semi-transparent gold

    public float RadarRangeWorld = 192f;
    public float RadarRingThickness = 2.0f;
    // Cylinder-wall height for the radar ring, in world meters. Independent
    // of thickness — a thin ring at e.g. 6 m height stands out against the
    // landscape without becoming visually heavy. Default 3 m, range 0.5–30.
    public float RadarRingHeight = 3.0f;

    // Cell-radius around the player. 1 cell = 24 m. Slopes can extend across
    // adjacent landblocks (see SlopeOverlay), so values above 8 cross
    // landblock boundaries and pull in extra terrain data on demand. Default
    // 12 cells (~288 m) gives roughly one landblock of look-ahead each
    // direction; 24 cells is the practical max before the triangle budget
    // becomes the bottleneck.
    public int SlopeRenderRadius = 12;
    // Same cell-radius idea as SlopeRenderRadius — extends across landblock
    // boundaries when set large. Default 12 matches slopes for parity.
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
    // The overlay only paints landblocks whose every vertex is one of these
    // types ("EntirelyWater" landblocks per ACE LandblockStruct.CalcWater),
    // because that's exactly the condition AC's LandCell.find_terrain_poly
    // turns into TransitionState.Collided for a regular character. Partial-
    // water landblocks rely on per-position depth checks we don't have
    // surface-Z data for, so we don't speculate.
    public int[] WaterTerrainTypes = { 0x10, 0x11, 0x12, 0x13, 0x14 };

    // Default true: a cell counts as water if ANY of its four corners is a
    // water-type. With WaterImpassableOnly=true (also default), the cell
    // additionally needs a too-steep triangle — so the visible result is
    // "shoreline cells where the seafloor drops below walkable angle", i.e.
    // the actual spots AC blocks you. all-four-corners was too strict at
    // typical shorelines where only 1-2 vertices fall in deep water.
    public bool WaterAnyCorner = true;

    // When true, the water overlay only paints cells that are BOTH water
    // AND have an unwalkable triangle (steep underwater slope). That's
    // the actual "impassable water" — the deep parts where AC's slope
    // physics blocks you at the shore drop-off. When false, every cell
    // matching WaterTerrainTypes is painted regardless of walkability —
    // useful as a "where is water" awareness overlay but does not signal
    // impassability. Uses SlopeFloorZ as the steepness threshold.
    public bool WaterImpassableOnly = true;

    // ── JSON bridge (manual build + JsonDocument parse — both AOT-safe) ───────

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
            + $"\"waterTypes\":[{string.Join(",", WaterTerrainTypes)}]"
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
            // Self-heal: an earlier version of the panel could push an empty
            // array on first-edit (Populate race) which then persisted. An
            // empty array means "match nothing" → no water ever painted. If
            // the JSON has zero entries, keep the constructor defaults
            // instead so the overlay works out-of-the-box.
            if (list.Count > 0)
                WaterTerrainTypes = list.ToArray();
        }
    }

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
