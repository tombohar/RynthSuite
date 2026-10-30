// TranslateLanguages.cs — Language codes offered by the chat translator (ISO 639-1 / Google style).
// Providers that use other codes (DeepL upper-case, LibreTranslate "zh") are mapped in TranslateClient.
using System;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Translate;

internal static class TranslateLanguages
{
    /// <summary>Pseudo-code meaning "let the provider detect the source language".</summary>
    public const string Auto = "auto";

    /// <summary>Codes in display order; index-aligned with <see cref="Names"/>.</summary>
    public static readonly string[] Codes =
    {
        Auto, "en", "es", "fr", "de", "it", "pt", "nl", "sv", "no", "da", "fi", "pl", "cs", "hu",
        "ro", "el", "tr", "ru", "uk", "ar", "he", "hi", "th", "vi", "id", "ja", "ko", "zh-CN", "zh-TW",
    };

    public static readonly string[] Names =
    {
        "Auto-detect", "English", "Spanish", "French", "German", "Italian", "Portuguese", "Dutch", "Swedish",
        "Norwegian", "Danish", "Finnish", "Polish", "Czech", "Hungarian", "Romanian", "Greek", "Turkish",
        "Russian", "Ukrainian", "Arabic", "Hebrew", "Hindi", "Thai", "Vietnamese", "Indonesian", "Japanese",
        "Korean", "Chinese (Simplified)", "Chinese (Traditional)",
    };

    public static int IndexOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return -1;
        for (int i = 0; i < Codes.Length; i++)
            if (string.Equals(Codes[i], code.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>True when the code is in the list (auto only counts when <paramref name="allowAuto"/>).</summary>
    public static bool IsKnown(string? code, bool allowAuto)
    {
        int i = IndexOf(code);
        return i > 0 || (i == 0 && allowAuto);
    }

    /// <summary>Canonical spelling of a known code (e.g. "ZH-cn" → "zh-CN"); null when unknown.</summary>
    public static string? Normalize(string? code)
    {
        int i = IndexOf(code);
        return i < 0 ? null : Codes[i];
    }

    /// <summary>"Spanish (es)" for UI and chat feedback; unknown codes are shown as-is.</summary>
    public static string Label(string? code)
    {
        int i = IndexOf(code);
        return i < 0 ? (code ?? string.Empty) : i == 0 ? Names[0] : $"{Names[i]} ({Codes[i]})";
    }

    /// <summary>Base language of a code ("zh-CN" → "zh"), lower-case, for same-language checks.</summary>
    public static string BaseOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        string c = code.Trim().ToLowerInvariant();
        int dash = c.IndexOf('-');
        return dash > 0 ? c.Substring(0, dash) : c;
    }

    /// <summary>
    /// ImGui language picker. Returns true when the selection changed. Render thread only.
    /// </summary>
    public static bool Combo(string label, ref string code, bool allowAuto, float width = 0f)
    {
        bool changed = false;
        if (width > 0f) ImGui.SetNextItemWidth(width);
        int current = IndexOf(code);
        string preview = current < 0 ? code : current == 0 ? Names[0] : Codes[current];
        if (ImGui.BeginCombo(label, preview, ImGuiComboFlags.HeightLarge))
        {
            for (int i = allowAuto ? 0 : 1; i < Codes.Length; i++)
            {
                bool selected = i == current;
                if (ImGui.Selectable(i == 0 ? Names[0] : $"{Codes[i]}  {Names[i]}", selected))
                {
                    code = Codes[i];
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered() && current > 0) SafeImGui.SetTooltip(Names[current]);
        return changed;
    }
}
