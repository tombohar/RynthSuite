// ============================================================================
//  RynthSuite - Plugins/Shared/SafeImGui.cs
//  ImGui text calls for text that isn't a literal: chat lines, item and player
//  names, server replies, settings values, interpolated strings.
//
//  ImGui.NET passes the string of Text / TextColored / TextWrapped /
//  TextDisabled / BulletText / SetTooltip / SetItemTooltip / LabelText to
//  native ImGui as a printf format with no arguments, so a '%' in it reads
//  arguments that were never passed (garbage, or a crash on %s / %n). These
//  wrappers double every '%' so the text draws exactly as given. Same
//  signatures as ImGui's, so a call site only changes "ImGui." to "SafeImGui.".
//  Compiled into each plugin that draws (linked source). Render thread only.
// ============================================================================

using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.Shared;

internal static class SafeImGui
{
    /// <summary>The text as a printf format that prints it unchanged ('%' doubled); null is empty.</summary>
    internal static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.IndexOf('%') < 0 ? text : text.Replace("%", "%%");
    }

    public static void Text(string? text) => ImGui.Text(Escape(text));

    public static void TextColored(Vector4 color, string? text) => ImGui.TextColored(color, Escape(text));

    public static void TextWrapped(string? text) => ImGui.TextWrapped(Escape(text));

    public static void TextDisabled(string? text) => ImGui.TextDisabled(Escape(text));

    public static void BulletText(string? text) => ImGui.BulletText(Escape(text));

    public static void SetTooltip(string? text) => ImGui.SetTooltip(Escape(text));

    public static void SetItemTooltip(string? text) => ImGui.SetItemTooltip(Escape(text));

    /// <summary>The label is drawn as text, not used as a format; only the value is escaped.</summary>
    public static void LabelText(string label, string? text) => ImGui.LabelText(label, Escape(text));
}
