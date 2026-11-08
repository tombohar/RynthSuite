using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

// Loot by name (2026-11-02). A short list of names the player types (/ra lootname add Unearthed Cache);
// while botting, anything lying in the world with one of those names inside the radius is used: a
// container opens (and the chest looter takes it), an item on the ground is picked up. The list, the
// matching, the settings string and the attempt bookkeeping live here, with no game types, so they can be tested alone;
// RynthAiPlugin.LootByName.cs is the host side.

/// <summary>The names to loot and how far to look (the settings' Loot By Names and Loot By Name Radius).</summary>
internal sealed class LootByNameList
{
    public const double DefaultRadius = 30, MinRadius = 3, MaxRadius = 200;
    public const int MaxNames = 50;

    private readonly List<string> _names = new();

    public double Radius { get; private set; } = DefaultRadius;
    public IReadOnlyList<string> Names => _names;
    public bool IsEmpty => _names.Count == 0;

    /// <summary>True when added (false: empty, already there, or the list is full).</summary>
    public bool Add(string? name)
    {
        string n = Clean(name);
        if (n.Length == 0 || _names.Count >= MaxNames || IndexOf(n) >= 0) return false;
        _names.Add(n);
        return true;
    }

    public bool Remove(string? name)
    {
        int i = IndexOf(Clean(name));
        if (i < 0) return false;
        _names.RemoveAt(i);
        return true;
    }

    public void Clear() => _names.Clear();

    public void SetRadius(double yards) => Radius = Math.Clamp(yards, MinRadius, MaxRadius);

    public bool MatchesAny(string? objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return false;
        foreach (string p in _names)
            if (Matches(p, objectName)) return true;
        return false;
    }

    /// <summary>
    /// Whole name, ignoring case. A * at the start and/or end makes it "ends with", "starts with" or "contains":
    /// "*cache*" matches "Unearthed Cache". A name with no * never matches part of another ("Cache" does not
    /// match "Unearthed Cache"), so a short word cannot loot half the world.
    /// </summary>
    public static bool Matches(string pattern, string name)
    {
        if (pattern.Length == 0 || name.Length == 0) return false;
        bool head = pattern[0] == '*', tail = pattern.Length > 1 && pattern[^1] == '*';
        string core = pattern.Trim('*');
        if (core.Length == 0) return false;   // "*" alone matches nothing
        var cmp = StringComparison.OrdinalIgnoreCase;
        if (head && tail) return name.Contains(core, cmp);
        if (head) return name.EndsWith(core, cmp);
        if (tail) return name.StartsWith(core, cmp);
        return name.Equals(core, cmp);
    }

    private int IndexOf(string n)
    {
        for (int i = 0; i < _names.Count; i++)
            if (string.Equals(_names[i], n, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Clean(string? s) => (s ?? string.Empty).Trim().Trim('"').Trim();

    // ── The settings string ────────────────────────────────────────────────

    /// <summary>The names as saved in the settings: separated by |.</summary>
    public const char Separator = '|';

    /// <summary>A list from the settings' names string and radius (blank and repeated names dropped).</summary>
    public static LootByNameList Parse(string? names, double radius)
    {
        var list = new LootByNameList();
        list.SetRadius(radius > 0 ? radius : DefaultRadius);
        foreach (string n in (names ?? string.Empty).Split(Separator, StringSplitOptions.RemoveEmptyEntries))
            list.Add(n);
        return list;
    }

    /// <summary>The names string the settings keep: "Unearthed Cache | *Doubloon*".</summary>
    public string ToSettingString() => string.Join(" | ", _names);

    /// <summary>A names string tidied the way a list would save it (the settings box can hold anything typed).</summary>
    public static string Normalize(string? names) => Parse(names, DefaultRadius).ToSettingString();
}

/// <summary>
/// What was tried on each object: how often it was used and when, which ones are finished (opened, or given up
/// on). Entries for objects that left the world are dropped by <see cref="Prune"/>, so a chest that comes back
/// (a new one with a new id, or a regenerating one) is looted again.
/// </summary>
internal sealed class LootNameTracker
{
    /// <summary>Uses on one object before giving up on it for a while.</summary>
    public const int MaxUses = 4;
    /// <summary>Between two uses: the client walks to the object in this time.</summary>
    public const long UseGapMs = 5000;
    /// <summary>After MaxUses with nothing to show, the object is left alone this long.</summary>
    public const long GiveUpMs = 120_000;

    private sealed class Entry
    {
        public int Uses;
        public long LastUseAt;
        public long BlockedUntil;
        public bool Done;
    }

    private readonly Dictionary<int, Entry> _entries = new();

    /// <summary>The object is finished or being left alone: do not pick it.</summary>
    public bool IsBlocked(int id, long now)
        => _entries.TryGetValue(id, out var e) && (e.Done || now < e.BlockedUntil);

    /// <summary>A use may go out now: none yet, or the last one is old enough and the budget is not spent.</summary>
    public bool CanUse(int id, long now)
    {
        if (!_entries.TryGetValue(id, out var e)) return true;
        if (e.Done || now < e.BlockedUntil) return false;
        return e.Uses < MaxUses && now - e.LastUseAt >= UseGapMs;
    }

    /// <summary>True when this was the last use of the budget and the gap after it has passed: give up on it.</summary>
    public bool Exhausted(int id, long now)
        => _entries.TryGetValue(id, out var e) && e.Uses >= MaxUses && now - e.LastUseAt >= UseGapMs;

    public void NoteUse(int id, long now)
    {
        if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry();
        e.Uses++;
        e.LastUseAt = now;
    }

    /// <summary>Opened or picked up: finished until the object is gone.</summary>
    public void MarkDone(int id)
    {
        if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry();
        e.Done = true;
    }

    public void GiveUp(int id, long now)
    {
        if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry();
        e.Uses = 0;
        e.BlockedUntil = now + GiveUpMs;
    }

    public int Uses(int id) => _entries.TryGetValue(id, out var e) ? e.Uses : 0;

    /// <summary>Forget objects that are no longer in the world.</summary>
    public void Prune(Func<int, bool> stillThere)
    {
        List<int>? gone = null;
        foreach (int id in _entries.Keys)
            if (!stillThere(id)) (gone ??= new()).Add(id);
        if (gone != null)
            foreach (int id in gone) _entries.Remove(id);
    }

    public void Clear() => _entries.Clear();
}
