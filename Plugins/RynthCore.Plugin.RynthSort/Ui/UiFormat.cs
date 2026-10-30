using System;

namespace RynthCore.Plugin.RynthSort.Ui;

/// <summary>
/// Display-list format 1 for the engine's script windows (RynthSuite
/// Docs/RYNTHLUA_WINDOWS_DESIGN.md §4.2-4.3; the engine's DisplayListParser.cs is the source of
/// truth). Little-endian. Only the ops RynthSort draws are listed.
/// </summary>
internal static class UiFormat
{
    public const uint Magic = 0x31575352;          // 'RSW1'
    public const ushort FormatVersion = 1;
    public const uint BodyUnchanged = 0xFFFFFFFF;
    public const int MaxString = 4096;
    public const int MaxStr8 = 128;
    public const int MaxOps = 1500;                 // engine limit per window
    public const int MaxBodyBytes = 64 * 1024;      // engine limit per window
    public const byte OpHasBlock = 1;

    // Ops
    public const byte OpEnd = 0x00;
    public const byte OpText = 0x01;
    public const byte OpTextColored = 0x02;
    public const byte OpTextWrapped = 0x03;
    public const byte OpTextDisabled = 0x04;
    public const byte OpBulletText = 0x05;
    public const byte OpSeparatorText = 0x06;
    public const byte OpSeparator = 0x10;
    public const byte OpSameLine = 0x11;
    public const byte OpSpacing = 0x13;
    public const byte OpDummy = 0x14;
    public const byte OpIndent = 0x15;
    public const byte OpUnindent = 0x16;
    public const byte OpButton = 0x20;
    public const byte OpCheckbox = 0x21;
    public const byte OpSliderInt = 0x22;
    public const byte OpInputText = 0x24;
    public const byte OpCombo = 0x25;
    public const byte OpSelectable = 0x26;
    public const byte OpProgressBar = 0x27;
    public const byte OpImage = 0x28;              // op level 2 (engines from 2026-09-30)
    public const byte OpPushID = 0x30;
    public const byte OpPopID = 0x31;
    public const byte OpPushStyleColor = 0x32;
    public const byte OpPopStyleColor = 0x33;
    public const byte OpSetItemTooltip = 0x38;
    public const byte OpChild = 0x40;
    public const byte OpCollapsingHeader = 0x41;

    // Window flags
    public const uint FlagVisible = 1u << 0;
    public const uint FlagShowInBar = 1u << 1;
    /// <summary>With ShowInBar: a bar button of its own instead of a Scripts menu line (engines from
    /// 2026-10-11; older ones ignore it and list the window in the Scripts menu).</summary>
    public const uint FlagBarButton = 1u << 11;
    public const uint FlagNoScrollbar = 1u << 5;

    // Image icon kinds (op level 2)
    public const byte IconKindIcon = 0;            // a 0x06 texture id
    public const byte IconKindObject = 1;          // a live object's icon with underlay and overlay

    // Selectable flags
    public const uint SelectableAllowDoubleClick = 4;

    // Event types
    public const ushort EvClicked = 1;
    public const ushort EvBool = 2;
    public const ushort EvInt = 3;
    public const ushort EvText = 5;
    public const ushort EvOpen = 6;
    public const ushort EvVisibility = 8;
    public const ushort EvError = 10;

    // ImGuiCol ids the engine accepts in PushStyleColor (ImGui.NET 1.91.6.1)
    public const int ColText = 0;
    public const int ColButton = 21;
    public const int ColButtonHovered = 22;
    public const int ColHeader = 24;

    // InputText flags
    public const uint InputAutoSelectAll = 4096;
    /// <summary>The text box reports Enter (its event carries submitted = true).</summary>
    public const uint InputEnterReturnsTrue = 32;

    // CollapsingHeader flags
    public const uint TreeDefaultOpen = 32;

    public static uint Fnv1a(ReadOnlySpan<byte> bytes, uint hash = 2166136261)
    {
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }

    /// <summary>The length of <paramref name="s"/> cut to at most <paramref name="max"/> bytes without splitting a character.</summary>
    public static int CutUtf8(ReadOnlySpan<byte> s, int max)
    {
        if (s.Length <= max) return s.Length;
        int n = Math.Max(0, max);
        while (n > 0 && (s[n] & 0xC0) == 0x80) n--;
        return n;
    }

    /// <summary>An ImGui U32 colour (0xAABBGGRR) from RGB bytes.</summary>
    public static uint Rgb(byte r, byte g, byte b, byte a = 255) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
}

/// <summary>Colours RynthSort uses (0xAABBGGRR).</summary>
internal static class UiColors
{
    public static readonly uint Green = UiFormat.Rgb(110, 210, 110);
    public static readonly uint Red = UiFormat.Rgb(225, 95, 95);
    public static readonly uint Yellow = UiFormat.Rgb(235, 205, 90);
    public static readonly uint Grey = UiFormat.Rgb(150, 150, 150);
    public static readonly uint Gold = UiFormat.Rgb(230, 180, 60);
    /// <summary>Upstream's "destruction" highlight: deep pink brightened by half (255, 30, 220).</summary>
    public static readonly uint Destruction = UiFormat.Rgb(255, 30, 220);
    public static readonly uint TabActive = UiFormat.Rgb(60, 110, 170);
    public static readonly uint TabActiveHover = UiFormat.Rgb(75, 130, 195);
    public static readonly uint OtherServer = UiFormat.Rgb(150, 185, 235);
    public static readonly uint Stale = UiFormat.Rgb(225, 150, 80);
    public static readonly uint White = UiFormat.Rgb(255, 255, 255);
    /// <summary>The Lua script's amber (paused, full packs): 1.0, 0.75, 0.25.</summary>
    public static readonly uint Amber = UiFormat.Rgb(255, 191, 64);
}
