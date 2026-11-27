using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>
/// "Keep handy items at the front of the main pack": the things a player reaches for (healing
/// kits, mana stones, food, potions and elixirs; the list is the player's to change) sit in the main
/// pack's first slots. Only items that live in the main pack anyway: a kind a side pack's rule names
/// still goes to that pack. It needs moves to a slot (plugin API v80). Items left alone (held) and
/// items that can't move now are never moved for it: handy items go after them. Pure.
/// </summary>
internal static class HandyItems
{
    public const int MaxClasses = 16, MaxNames = 12;

    /// <summary>The kinds a character starts with.</summary>
    public static readonly int[] DefaultClasses = { ObjectClasses.HealingKit, ObjectClasses.ManaStone, ObjectClasses.Food };

    /// <summary>
    /// Parts of names that count as handy whatever their kind: potions and elixirs (the client
    /// calls most of them Food already, but not every server's do).
    /// </summary>
    public static readonly string[] DefaultNames = { "Potion", "Elixir" };

    /// <summary>Whether <paramref name="it"/> is handy by these kinds and names (a name part matches any case).</summary>
    public static bool Matches(IReadOnlyList<int> classes, IReadOnlyList<string> names, SortItem it)
    {
        for (int i = 0; i < classes.Count; i++)
            if (classes[i] == it.ObjectClass) return true;
        string name = it.Name ?? string.Empty;
        if (name.Length == 0) return false;
        for (int i = 0; i < names.Count; i++)
            if (names[i].Length > 0 && name.Contains(names[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The planner's test for the settings now (a copy of the lists: later edits don't change a plan under way), or null when the option is off.</summary>
    public static Func<SortItem, bool>? For(SortSettings s)
    {
        if (!s.HandyFront || (s.HandyClasses.Count == 0 && s.HandyNames.Count == 0)) return null;
        int[] classes = s.HandyClasses.ToArray();
        string[] names = s.HandyNames.ToArray();
        return it => Matches(classes, names, it);
    }

    /// <summary>"healing kits, mana stones, food, potions and elixirs": the list in a sentence (names as they are typed).</summary>
    public static string Describe(IReadOnlyList<int> classes, IReadOnlyList<string> names)
    {
        var parts = new List<string>(classes.Count + names.Count);
        foreach (int c in classes) parts.Add(ObjectClasses.Plural(c).ToLowerInvariant());
        foreach (string n in names) parts.Add(Plural(n).ToLowerInvariant());
        if (parts.Count == 0) return "nothing";
        if (parts.Count == 1) return parts[0];
        return string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[^1];
    }

    private static string Plural(string n) => n.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? n : n + "s";

    /// <summary>Adds a kind (false when it is there, the list is full, or it isn't one the picker offers).</summary>
    public static bool AddClass(SortSettings s, int cls)
    {
        if (ObjectClasses.ChoiceIndex(cls) < 0 || s.HandyClasses.Contains(cls) || s.HandyClasses.Count >= MaxClasses) return false;
        s.HandyClasses.Add(cls);
        return true;
    }

    /// <summary>Adds a part of a name, trimmed (false when blank, there already, or the list is full).</summary>
    public static bool AddName(SortSettings s, string? text)
    {
        string t = SortRule.Clean(text);
        if (t.Length == 0 || s.HandyNames.Count >= MaxNames) return false;
        foreach (string n in s.HandyNames) if (string.Equals(n, t, StringComparison.OrdinalIgnoreCase)) return false;
        s.HandyNames.Add(t);
        return true;
    }

    public static bool RemoveName(SortSettings s, string text) =>
        s.HandyNames.RemoveAll(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) > 0;
}
