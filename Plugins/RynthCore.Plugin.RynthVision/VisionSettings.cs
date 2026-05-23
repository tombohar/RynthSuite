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

    public int SlopeRenderRadius = 4;
    public int WaterRenderRadius = 4;

    // AC terrain-type indices treated as impassable water. Starting guess (AC's
    // deep-water family) — verify with the panel's inspect button and adjust.
    public int[] WaterTerrainTypes = { 19, 20, 21 };

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
            + $"\"slopeRadius\":{SlopeRenderRadius},"
            + $"\"waterRadius\":{WaterRenderRadius},"
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
        if (r.TryGetProperty("slopeRadius", out v)) SlopeRenderRadius = v.GetInt32();
        if (r.TryGetProperty("waterRadius", out v)) WaterRenderRadius = v.GetInt32();
        if (r.TryGetProperty("waterTypes", out v) && v.ValueKind == JsonValueKind.Array)
        {
            var list = new List<int>();
            foreach (var e in v.EnumerateArray())
                if (e.TryGetInt32(out int n)) list.Add(n);
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
