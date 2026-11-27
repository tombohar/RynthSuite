using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>
/// What the window calls a pack in a sentence (the activity feed, toasts, Next moves): "main pack",
/// the side pack's own name ("Satchel"), and, when another carried pack has the same name, its
/// number too, as its card's badge shows it ("Satchel (2)"). A pack the client hasn't named yet is
/// "pack 3". Pure.
/// </summary>
internal static class PackNames
{
    public const string Main = "main pack";

    /// <summary>Pack <paramref name="index"/> (0 = the main pack, n = side pack n) among <paramref name="names"/> (side packs, pack 1 first).</summary>
    public static string Label(IReadOnlyList<string> names, int index)
    {
        if (index == 0) return Main;
        if (index < 1 || index > names.Count) return "pack " + index;
        string name = Clean(names[index - 1]);
        if (name.Length == 0) return "pack " + index;
        for (int i = 0; i < names.Count; i++)
            if (i != index - 1 && string.Equals(Clean(names[i]), name, StringComparison.OrdinalIgnoreCase))
                return name + " (" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        return name;
    }

    /// <summary>As <see cref="Label(IReadOnlyList{string}, int)"/>, for the packs as read.</summary>
    public static string Label(SortInventory? inv, int index)
    {
        if (index == 0) return Main;
        if (inv == null) return "pack " + index;
        var names = new string[inv.Packs.Count];
        for (int i = 0; i < names.Length; i++) names[i] = inv.Packs[i].Name;
        return Label(names, index);
    }

    /// <summary>As <see cref="Label(IReadOnlyList{string}, int)"/>, for the packs a planning pass listed.</summary>
    public static string Label(SortView view, int index)
    {
        if (index == 0) return Main;
        var names = new string[view.Packs.Count];
        for (int i = 0; i < names.Length; i++) names[i] = view.Packs[i].Name;
        return Label(names, index);
    }

    private static string Clean(string? s) => (s ?? string.Empty).Trim();
}
