using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthInventory;

internal enum InventoryVerb { Window, Scan, Keep, Reload, Status, Help, Search }

/// <summary>
/// A parsed "/ginv ..." and the lines it answers with (2026-10-28: every command answers,
/// Plugins/Shared/CommandReply.cs). Anything that isn't a verb is a search. Pure, so
/// RynthInventory.Tests checks every word.
/// </summary>
internal readonly record struct InventoryCommand(InventoryVerb Verb, string Text = "")
{
    public static InventoryCommand Parse(string? arg)
    {
        string a = (arg ?? string.Empty).Trim();
        return a.ToLowerInvariant() switch
        {
            "" => new(InventoryVerb.Window),
            "scan" or "rescan" => new(InventoryVerb.Scan),
            "keep" => new(InventoryVerb.Keep),
            "reload" => new(InventoryVerb.Reload),
            "status" => new(InventoryVerb.Status),
            "help" or "?" => new(InventoryVerb.Help),
            _ => new(InventoryVerb.Search, a),
        };
    }

    public const string Usage =
        "/ginv (window), /ginv <text> (search), /ginv scan (rescan and save now), /ginv keep (record the open container as storage), /ginv reload, /ginv status";

    public const string NoWindows =
        "This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). Scanning and saving still work; /ginv status shows what was saved.";

    /// <summary>/ginv alone: the window after the toggle.</summary>
    public static string Window(bool shown) => CommandReply.Shown("Global Inventory", shown);

    /// <summary>/ginv &lt;text&gt;: the search the window now shows.</summary>
    public static string Searching(string text) => $"Global Inventory: searching for '{text.Trim()}'.";
}
