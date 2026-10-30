using System.Globalization;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>
/// The line /rdef answers with for the commands that used to say nothing (2026-10-28: every command
/// answers, Plugins/Shared/CommandReply.cs): a window shown or hidden, a use started, a test
/// selection. Pure, so RynthDefective.Tests checks every verb's line.
/// </summary>
internal static class DefReplies
{
    /// <summary>A window after a show / hide / toggle: <paramref name="state"/> 1 shown, 0 hidden, -1 the engine can't say.</summary>
    public static (bool Ok, string Text) Window(string title, int state) => state switch
    {
        1 => (true, CommandReply.Shown(title, true)),
        0 => (true, CommandReply.Shown(title, false)),
        _ => (false, $"This RynthCore has no {title} window (it needs plugin API v90)."),
    };

    /// <summary>A slot's use started (the result shows on the bar; a problem is said in chat).</summary>
    public static string UseSlot(DefSlot slot, int number) => slot.IsSpell
        ? $"Casting {slot.SpellLabel} (slot {number})."
        : $"Using {slot.Label} (slot {number}).";

    /// <summary>"/rdef use &lt;name&gt;" started: the item is looked up when the use runs.</summary>
    public static string UseName(string name) => $"Using {name.Trim()}.";

    /// <summary>The weapon picker after /rdef weapons [search].</summary>
    public static string Weapons(bool shown, string search) =>
        !shown ? "Weapon picker hidden."
        : search.Trim().Length > 0 ? $"Weapon picker shown, searching '{search.Trim()}'."
        : "Weapon picker shown.";

    /// <summary>
    /// "/rdef selection &lt;0xId&gt;" (the Selection window's test hook): the object id (hex, "0x" optional),
    /// or false with the usage in <paramref name="why"/>.
    /// </summary>
    public static bool TryParseSelection(string text, out uint id, out string why)
    {
        why = string.Empty;
        string t = text.Trim();
        if (uint.TryParse(t.Replace("0x", "", System.StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, null, out id)) return true;
        why = $"'{t}' isn't an object id. Usage: /rdef selection [0x80001234 | stack]";
        return false;
    }

    /// <summary>"/rdef selection stack" with no stack in the pack.</summary>
    public const string NoStack = "No stack in your pack to select.";

    /// <summary>What a test selection did.</summary>
    public static (bool Ok, string Text) Selected(uint id, bool selected, string? name) =>
        selected ? (true, $"Selected 0x{id:X8}{(string.IsNullOrEmpty(name) ? "" : " (" + name + ")")}.")
        : (false, $"The game didn't select 0x{id:X8}.");
}
