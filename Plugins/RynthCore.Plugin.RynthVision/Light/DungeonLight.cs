// ============================================================================
//  RynthVision - Light/DungeonLight.cs
//  The dungeon light's rules (2026-10-27), pure: where it lights and how
//  brightly, the /rv light words, and the status line. A player asked for
//  SkunkVision's "Dungeon Light" (problem report R-20261027-141534-6a2b:
//  the dark TOR dungeons). Tools/RynthVision.Tests compiles this file as is.
//
//  How it lights: RynthCore's ambient light floor (plugin API v91,
//  RynthCore D3D9/AmbientLight.cs) raises the ambient light AC draws the
//  world with, per channel, never below AC's own. Drawing only, in this
//  client: nothing goes to the server and nobody else sees a difference.
//  AC itself lights every dungeon cell with a flat 20 % white ambient
//  (CellManager::ChangePosition -> SmartBox::SetWorldAmbientLight(0.2,
//  white)); torches are baked into the walls on top of that. Strength 0..100
//  maps onto 20 %..100 %: 0 is AC's own dungeon, 100 lights every wall fully
//  (flat, no shading); the default 40 gives 52 %, about an overcast day.
//
//  Where, as AC decides it: an outdoor cell (cell id low word below 0x100)
//  is outdoors; an EnvCell whose dat entry says it sees outside
//  (EnvCellSeenOutside, a building's room) is a building, lit by AC from the
//  sky; every other EnvCell is a dungeon. Unknown (no cell dat) counts as a
//  dungeon: the light then reaches buildings too, harmlessly.
// ============================================================================

using System;
using System.Globalization;

namespace RynthCore.Plugin.RynthVision.Light;

/// <summary>Where the player is, the way AC picks its ambient light.</summary>
internal enum LightPlace { NotInWorld = 0, Outdoors = 1, Building = 2, Dungeon = 3 }

/// <summary>Where the dungeon light lights (setting "lightScope").</summary>
internal enum LightScope { Dungeons = 0, Indoors = 1, Everywhere = 2 }

internal enum LightCommandKind { Toggle, On, Off, Strength, Scope, Color, Show, Status, Help, Error }

/// <summary>One parsed "/rv light ..." command.</summary>
internal readonly record struct LightCommand(LightCommandKind Kind, int Strength = 0, LightScope Scope = LightScope.Dungeons,
    uint ColorArgb = 0, string Error = "");

/// <summary>What the tick knows, for the status line and the window.</summary>
internal readonly record struct LightStatus(bool On, LightScope Scope, int Strength, LightPlace Place, bool EngineHasApi,
    bool HookLive, uint ClientAmbient, uint AppliedAmbient);

/// <summary>
/// What the Vision window's Light tab draws (published by the tick, read on AC's render thread): the settings and the
/// status line. <paramref name="Warn"/>: the line says why it can't light.
/// </summary>
internal sealed record LightView(bool On, int Strength, LightScope Scope, uint ColorArgb, string Status, bool Warn)
{
    public static readonly LightView Empty =
        new(false, DungeonLight.DefaultStrength, LightScope.Dungeons, DungeonLight.DefaultColor, "Waiting for RynthVision...", false);
}

internal static class DungeonLight
{
    public const int DefaultStrength = 40;
    public const uint DefaultColor = 0xFFFFFFFF;

    /// <summary>AC's own dungeon ambient (SetWorldAmbientLight(0.2, white)).</summary>
    public const float AcDungeonLevel = 0.2f;

    /// <summary>Colour presets for the window and /rv light color: name, ARGB.</summary>
    public static readonly (string Name, uint Argb)[] Presets =
    {
        ("white", 0xFFFFFFFF),
        ("torch", 0xFFFFD8A8),   // warm, like AC's torches
        ("moon", 0xFFC8D8FF),    // cool blue-white
    };

    public static string ScopeWord(LightScope s) => s switch
    {
        LightScope.Indoors => "indoors",
        LightScope.Everywhere => "everywhere",
        _ => "dungeons",
    };

    public static string PlaceWord(LightPlace p) => p switch
    {
        LightPlace.Outdoors => "outdoors",
        LightPlace.Building => "in a building",
        LightPlace.Dungeon => "in a dungeon",
        _ => "not in the world",
    };

    /// <summary>Where <paramref name="cellId"/> is: <paramref name="seenOutside"/> is the EnvCell's flag (null = unknown).</summary>
    public static LightPlace Classify(uint cellId, bool? seenOutside)
    {
        if ((cellId >> 16) == 0) return LightPlace.NotInWorld;
        if ((cellId & 0xFFFF) < 0x100) return LightPlace.Outdoors;
        return seenOutside == true ? LightPlace.Building : LightPlace.Dungeon;
    }

    /// <summary>True when the light lights <paramref name="place"/> under <paramref name="scope"/>.</summary>
    public static bool Applies(LightScope scope, LightPlace place) => place switch
    {
        LightPlace.Dungeon => true,
        LightPlace.Building => scope != LightScope.Dungeons,
        LightPlace.Outdoors => scope == LightScope.Everywhere,
        _ => false,
    };

    /// <summary>The ambient level (0.2..1) for a strength (0..100, clamped).</summary>
    public static float Level(int strength) =>
        AcDungeonLevel + (1f - AcDungeonLevel) * Math.Clamp(strength, 0, 100) / 100f;

    /// <summary>
    /// The floor for RynthCore (0x00RRGGBB): <paramref name="colorArgb"/>'s channels times the level.
    /// 0 (no floor) for strength 0 or less: AC's own dungeon.
    /// </summary>
    public static uint FloorRgb(uint colorArgb, int strength)
    {
        if (strength <= 0) return 0;
        float level = Level(strength);
        uint Ch(int shift) => (uint)Math.Clamp((int)MathF.Round(((colorArgb >> shift) & 0xFF) * level), 0, 255);
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    /// <summary>The floor to set now: 0 when off or when <paramref name="place"/> isn't in the scope.</summary>
    public static uint FloorFor(bool on, LightScope scope, int strength, uint colorArgb, LightPlace place) =>
        on && Applies(scope, place) ? FloorRgb(colorArgb, strength) : 0u;

    /// <summary>An ARGB colour's brightest channel as a percentage (the ambient level a person reads).</summary>
    public static int Percent(uint argb)
    {
        uint m = Math.Max((argb >> 16) & 0xFF, Math.Max((argb >> 8) & 0xFF, argb & 0xFF));
        return (int)MathF.Round(m * 100f / 255f);
    }

    /// <summary>"RRGGBB", "AARRGGBB" (alpha dropped) with or without '#', or a preset name; opaque ARGB.</summary>
    public static bool TryParseColor(string? text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimStart('#');
        foreach (var (name, value) in Presets)
            if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) { argb = value; return true; }
        if (s.Length == 8) s = s.Substring(2);
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return false;
        argb = 0xFF000000u | rgb;
        return true;
    }

    /// <summary>The preset name for <paramref name="argb"/>, or its hex ("FFE0C0").</summary>
    public static string ColorName(uint argb)
    {
        foreach (var (name, value) in Presets)
            if ((value & 0xFFFFFF) == (argb & 0xFFFFFF)) return name;
        return (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// "/rv light" and its words (<paramref name="args"/> is what follows "light"): nothing = toggle;
    /// on | off | toggle; 0-100 (a trailing % is fine) = strength, and on; dungeons | indoors | everywhere;
    /// color &lt;RRGGBB | white | torch | moon&gt;; show (the Vision window's Light tab); status; help.
    /// </summary>
    public static LightCommand ParseCommand(string? args)
    {
        string a = (args ?? string.Empty).Trim();
        if (a.Length == 0) return new LightCommand(LightCommandKind.Toggle);
        string[] words = a.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string w = words[0].ToLowerInvariant();
        string rest = words.Length > 1 ? words[1] : string.Empty;
        switch (w)
        {
            case "on": return new LightCommand(LightCommandKind.On);
            case "off": return new LightCommand(LightCommandKind.Off);
            case "toggle": return new LightCommand(LightCommandKind.Toggle);
            case "dungeon": case "dungeons": return new LightCommand(LightCommandKind.Scope, Scope: LightScope.Dungeons);
            case "indoor": case "indoors": case "inside": return new LightCommand(LightCommandKind.Scope, Scope: LightScope.Indoors);
            case "everywhere": case "all": case "always": return new LightCommand(LightCommandKind.Scope, Scope: LightScope.Everywhere);
            case "show": case "window": case "settings": return new LightCommand(LightCommandKind.Show);
            case "status": case "?": return new LightCommand(LightCommandKind.Status);
            case "help": return new LightCommand(LightCommandKind.Help);
            case "color": case "colour":
                if (rest.Length == 0)
                    return new LightCommand(LightCommandKind.Error, Error: "Give a colour: RRGGBB (hex), white, torch or moon.");
                return TryParseColor(rest, out uint c)
                    ? new LightCommand(LightCommandKind.Color, ColorArgb: c)
                    : new LightCommand(LightCommandKind.Error, Error: $"'{rest}' isn't a colour: RRGGBB (hex), white, torch or moon.");
        }
        string num = w.EndsWith('%') ? w[..^1] : w;
        if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return n is >= 0 and <= 100
                ? new LightCommand(LightCommandKind.Strength, Strength: n)
                : new LightCommand(LightCommandKind.Error, Error: $"Strength {n} is outside 0-100.");
        return new LightCommand(LightCommandKind.Error, Error: $"Unknown '/rv light {a}'.");
    }

    public const string Usage =
        "/rv light [on|off] | 0-100 (strength) | dungeons|indoors|everywhere | color RRGGBB|white|torch|moon | show | status";

    /// <summary>What /rv light says after a change: the light's settings now (and why it can't light on an old engine).</summary>
    public static string Summary(bool on, int strength, LightScope scope, uint colorArgb, bool engineHasApi) =>
        $"Dungeon light {(on ? "on" : "off")}: strength {strength}%, {ScopeWord(scope)}, {ColorName(colorArgb)}."
        + (engineHasApi ? "" : " Needs a newer RynthCore (plugin API 91) to light anything.");

    /// <summary>One line for chat and the Light tab: what the light is doing here and why.</summary>
    public static string StatusLine(in LightStatus s)
    {
        if (!s.EngineHasApi)
            return "Needs a newer RynthCore (plugin API 91): update RynthCore in the launcher.";
        if (!s.On)
            return $"Off. Strength {s.Strength}%, {ScopeWord(s.Scope)}.";
        if (!s.HookLive)
            return "On, but RynthCore's light hook is off or unavailable (/rc hooks; switch: AmbientLight).";
        if (s.Place == LightPlace.NotInWorld)
            return "On; waiting to be in the world.";
        if (!Applies(s.Scope, s.Place))
            return $"On for {ScopeWord(s.Scope)}; not lighting here ({PlaceWord(s.Place)}).";
        string levels = s.ClientAmbient != 0 || s.AppliedAmbient != 0
            ? $": ambient {Percent(s.ClientAmbient)}% -> {Percent(s.AppliedAmbient)}%"
            : string.Empty;
        return $"Lighting {PlaceWord(s.Place)}{levels}.";
    }

    /// <summary>True when the status line says the light can't work (an old engine, the hook off).</summary>
    public static bool IsWarning(in LightStatus s) => !s.EngineHasApi || (s.On && !s.HookLive);
}
