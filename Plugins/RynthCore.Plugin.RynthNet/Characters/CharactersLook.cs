// ============================================================================
//  RynthNet - Characters/CharactersLook.cs
//  The Characters window's two looks (2026-10-31, the owner: "our networking
//  tab should be changed to look similar to our Lua script for components. Very
//  minimalistic."), pure so Tools/RynthNet.Tests compiles it in:
//
//    Minimal  the default: one compact line per character, no frame and no
//             background on an engine with frameless windows (API v90): a
//             status dot (botting, fighting, idle, dead, stale...), the name
//             (amber when its health is low, red when critical or dead), three
//             thin vital bars, and the arrow and distance when there is room.
//    Full     "edit mode": the framed window with every column (the look the
//             window had before), where it is moved and resized.
//
//  Here: the view names and /rn view, the low-health thresholds, the status
//  dot, and the minimal line's layout at a width. Kept per character in
//  RynthNet's settings.json ("view" under the character).
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthNet.Characters;

/// <summary>How the Characters window looks.</summary>
internal enum CharactersViewMode
{
    /// <summary>One compact line per character, frameless (the default).</summary>
    Minimal = 0,
    /// <summary>The framed window with every column.</summary>
    Full = 1,
}

/// <summary>A character's health, for the minimal line's colour.</summary>
internal enum HpWarn { Ok, Low, Critical, Dead, Unknown }

/// <summary>The minimal line's status dot.</summary>
internal enum CharDot { Self, Botting, Fighting, Idle, Dead, Portal, Stale, Off }

/// <summary>The minimal line at a width (px): the name column, the bars and whether the arrow and distance fit.</summary>
internal readonly record struct MinimalLine(float NameWidth, bool Bars, float BarWidth, bool Distance)
{
    /// <summary>How wide the line is drawn (dot, name, bars, distance).</summary>
    public float Width(float k, float distWidth) =>
        CharactersLook.DotWidth * k + NameWidth
        + (Bars ? CharactersLook.Gap * k + 3 * BarWidth + 2 * CharactersLook.BarGap * k : 0)
        + (Distance ? CharactersLook.Gap * k + CharactersLook.ArrowWidth * k + distWidth : 0);
}

/// <summary>What /rn view did.</summary>
internal readonly record struct ViewAnswer(bool Ok, CharactersViewMode Next, bool Changed, string Reply);

internal static class CharactersLook
{
    /// <summary>Health at or below this share is low (amber), at or below <see cref="CriticalHp"/> critical (red).</summary>
    public const float LowHp = 0.5f, CriticalHp = 0.25f;

    /// <summary>Minimal line metrics (px at 100%).</summary>
    public const float RowHeight = 16f, DotWidth = 12f, Gap = 6f, BarWidth = 24f, MinBarWidth = 8f, BarGap = 2f,
        BarHeight = 4f, ArrowWidth = 14f, MinNameWidth = 30f;

    public const string Usage = "usage: /rn view [minimal|full] (alone: the view now)";

    // ── colours ─────────────────────────────────────────────────────────

    /// <summary>The health warning for a row: dead (its state, or 0 health), critical, low, fine, or unknown (no vitals).</summary>
    public static HpWarn Hp(CharacterRow r)
    {
        if (r.StateKind == CharStateKind.Dead) return HpWarn.Dead;
        if (!r.HasVitals) return HpWarn.Unknown;
        return Hp(r.Hp.Fraction);
    }

    /// <summary>The warning for a health share (0..1).</summary>
    public static HpWarn Hp(float fraction)
    {
        if (fraction <= 0f) return HpWarn.Dead;
        if (fraction <= CriticalHp) return HpWarn.Critical;
        if (fraction <= LowHp) return HpWarn.Low;
        return HpWarn.Ok;
    }

    /// <summary>
    /// The status dot: logged out or no status (off), this client, dead, not updating (stale), portal
    /// space, fighting, botting, else idle. Dead and stale come before what the bot was doing.
    /// </summary>
    public static CharDot Dot(CharacterRow r)
    {
        if (r.LoggedOut || r.StateKind == CharStateKind.NoStatus) return CharDot.Off;
        if (r.IsSelf) return CharDot.Self;
        if (r.StateKind == CharStateKind.Dead) return CharDot.Dead;
        if (r.Stale) return CharDot.Stale;
        return r.StateKind switch
        {
            CharStateKind.Portal => CharDot.Portal,
            CharStateKind.Fighting => CharDot.Fighting,
            CharStateKind.Botting => CharDot.Botting,
            _ => CharDot.Idle,
        };
    }

    // ── layout ──────────────────────────────────────────────────────────

    /// <summary>
    /// The minimal line in <paramref name="width"/> px, for names up to <paramref name="nameWidth"/> and a
    /// distance up to <paramref name="distWidth"/> (k: the text size factor). The name and the bars come
    /// first; the arrow and distance only when everything fits. Narrower, the name gives way down to
    /// <see cref="MinNameWidth"/>, then the bars shrink to <see cref="MinBarWidth"/>, then go.
    /// </summary>
    public static MinimalLine Minimal(float width, float nameWidth, float distWidth, float k = 1f)
    {
        if (k <= 0) k = 1f;
        float dot = DotWidth * k, gap = Gap * k, barGap = BarGap * k;
        float bars = 3 * BarWidth * k + 2 * barGap;
        float dist = gap + ArrowWidth * k + distWidth;
        if (distWidth > 0 && width >= dot + nameWidth + gap + bars + dist)
            return new MinimalLine(nameWidth, true, BarWidth * k, true);

        float minName = Math.Min(nameWidth, MinNameWidth * k);
        float name = Math.Clamp(width - dot - gap - bars, minName, nameWidth);
        if (width >= dot + name + gap + bars)
            return new MinimalLine(name, true, BarWidth * k, false);

        float bar = (width - dot - minName - gap - 2 * barGap) / 3f;
        if (bar >= MinBarWidth * k)
            return new MinimalLine(minName, true, MathF.Floor(bar), false);
        return new MinimalLine(Math.Max(0, Math.Min(nameWidth, width - dot)), false, 0, false);
    }

    /// <summary>The height the minimal window asks for: one line per row (at least one, for the status), and a small margin.</summary>
    public static float MinimalHeight(int rows, float k = 1f) => MathF.Ceiling((Math.Max(1, rows) * RowHeight + 4f) * (k <= 0 ? 1f : k));

    // ── the view ────────────────────────────────────────────────────────

    public static string Name(CharactersViewMode mode) => mode == CharactersViewMode.Full ? "full" : "minimal";

    /// <summary>"minimal" (or "min", "compact", "bare"), "full" (or "edit", "framed"); any case. False for anything else.</summary>
    public static bool TryParse(string? text, out CharactersViewMode mode)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "minimal": case "min": case "compact": case "bare": mode = CharactersViewMode.Minimal; return true;
            case "full": case "edit": case "framed": mode = CharactersViewMode.Full; return true;
            default: mode = CharactersViewMode.Minimal; return false;
        }
    }

    /// <summary>The settings file's value: a known name, else the default (minimal).</summary>
    public static CharactersViewMode FromSetting(string? text) => TryParse(text, out CharactersViewMode m) ? m : CharactersViewMode.Minimal;

    /// <summary>
    /// /rn view [minimal|full|toggle]: alone says the view now. <paramref name="loggedIn"/>: the view is
    /// kept per character, so changing it needs one.
    /// </summary>
    public static ViewAnswer Command(string? args, CharactersViewMode current, bool loggedIn)
    {
        string a = (args ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0) return new ViewAnswer(true, current, false, Describe(current, "the Characters window is in the "));
        CharactersViewMode next;
        if (a == "toggle") next = current == CharactersViewMode.Full ? CharactersViewMode.Minimal : CharactersViewMode.Full;
        else if (!TryParse(a, out next)) return new ViewAnswer(false, current, false, Usage);
        if (!loggedIn) return new ViewAnswer(false, current, false, "log in first: the view is kept per character.");
        bool changed = next != current;
        return new ViewAnswer(true, next, changed,
            Describe(next, changed ? "the Characters window now shows the " : "the Characters window is already in the "));
    }

    /// <summary>"&lt;head&gt;minimal view (one line per character...). /rn view full for every column."</summary>
    public static string Describe(CharactersViewMode mode, string head) => head + (mode == CharactersViewMode.Full
        ? "full view (every column; move and resize it here). /rn view minimal for the small one."
        : "minimal view (one line per character, no frame). /rn view full for every column, to move or resize it.");
}
