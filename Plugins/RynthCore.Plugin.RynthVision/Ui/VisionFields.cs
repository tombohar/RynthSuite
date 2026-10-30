// ============================================================================
//  RynthVision - Ui/VisionFields.cs
//  The Vision window's pure parts (2026-10-27), the same rules the engine's
//  VisionFace used (UI/Data/VisionData.cs, ArgbOpacity.cs) so a player's
//  colours and lists read and write exactly as before: the tabs, a colour's
//  alpha byte as an opacity percentage, AARRGGBB / RRGGBB hex, and the water
//  terrain types as "18,19,20". Tools/RynthVision.Tests compiles this file.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthVision.Ui;

/// <summary>The Vision window's tabs, in strip order (the engine's three, then the light).</summary>
internal enum VisionTab { Terrain = 0, Nameplates = 1, CombatText = 2, Light = 3 }

internal static class VisionFields
{
    public static readonly string[] TabNames = { "Terrain", "Nameplates", "Combat text", "Light" };

    /// <summary>The engine's tab body for a tab it still owns (RenderSettingsDrawer key), or null.</summary>
    public static string? EngineTabKey(VisionTab tab) => tab switch
    {
        VisionTab.Nameplates => "nameplates",
        VisionTab.CombatText => "combattext",
        _ => null,
    };

    /// <summary>The colour's alpha as 0..100 %.</summary>
    public static float OpacityPercent(uint argb) => (argb >> 24) * 100f / 255f;

    /// <summary>The colour with its alpha set from <paramref name="percent"/> (clamped to 0..100); RGB kept.</summary>
    public static uint WithOpacity(uint argb, float percent)
    {
        float p = float.IsNaN(percent) ? 0f : Math.Clamp(percent, 0f, 100f);
        uint a = (uint)Math.Clamp((int)MathF.Round(p * 255f / 100f), 0, 255);
        return (argb & 0x00FFFFFFu) | (a << 24);
    }

    /// <summary>"AARRGGBB" or "RRGGBB" (opaque), with or without '#'.</summary>
    public static bool TryParseHex(string? s, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        return s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
    }

    /// <summary>"18,19,20" -> the numbers in it (anything else is dropped).</summary>
    public static List<int> ParseCsv(string? csv)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) list.Add(n);
        return list;
    }

    public static string Csv(int[] types) => string.Join(",", types);
}
