using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

// Buff items (Use Buff Items, 2026-11-11): the items RynthAi uses for their buff when it isn't on.
// Three are built in (Asheron's Benediction, Asheron's Lesser Benediction, Blackmoor's Favor); the
// player can block any of them and add their own (a server's custom gems, potions). This file is the
// pure part: the lists as saved in the profile, the edits made to them, and which item to use now.
// BuffManager.BuffItems.cs reads the pack and the enchantments and sends the use. No game calls here.

/// <summary>
/// A buff item the player added (LegacyUiSettings.CustomBuffItems). Saved in the character's profile.
/// </summary>
public sealed class CustomBuffItem
{
    /// <summary>The item's name, matched against the pack without case.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Ticked in the Buff Items list: RynthAi may use it.</summary>
    public bool Allowed { get; set; } = true;
    /// <summary>
    /// The spells its use casts, read from the item: its spell (data id 28 in the object's
    /// CreateObject header), else the spell book its appraisal lists. Buffs only: a spell the
    /// catalog lists with no duration is left out. Its buff counts as on when any of them is on
    /// the player.
    /// </summary>
    public List<uint> SpellIds
    {
        get => _spellIds;
        set => _spellIds = value ?? new List<uint>();
    }
    private List<uint> _spellIds = new();
    /// <summary>
    /// The spells were read. With none found the item is used on <see cref="RepeatMinutes"/>
    /// instead. False: not read yet (added by name and not in the pack since), so it waits.
    /// </summary>
    public bool SpellsRead { get; set; }
    /// <summary>For an item with no spell to check: use it again this many minutes after the last use.</summary>
    public int RepeatMinutes { get; set; } = BuffItemRules.DefaultRepeatMinutes;
    /// <summary>When RynthAi last used it (UTC); the repeat timer counts from here, across reloads.</summary>
    public DateTime? LastUsedUtc { get; set; }

    public CustomBuffItem Copy() => new()
    {
        Name = Name,
        Allowed = Allowed,
        SpellIds = new List<uint>(SpellIds),
        SpellsRead = SpellsRead,
        RepeatMinutes = RepeatMinutes,
        LastUsedUtc = LastUsedUtc,
    };
}

/// <summary>The built-in items, the limits, and the edits the window and /ra buffitems make.</summary>
internal static class BuffItemRules
{
    /// <summary>The same item is not used again this soon, so its buff has time to land.</summary>
    public const int RetryAfterUseSec = 15;
    /// <summary>Uses in a row whose buff never showed before the item is rested.</summary>
    public const int UsesBeforeRest = 2;
    /// <summary>How long an item whose buff didn't show is left alone.</summary>
    public const int RestMinutes = 10;
    public const int DefaultRepeatMinutes = 60;
    public const int MinRepeatMinutes = 1;
    public const int MaxRepeatMinutes = 7 * 24 * 60;
    public const int MaxNameLength = 80;

    /// <summary>
    /// The built-in items: name, and the spell ids that mean its buff is already on (the
    /// Benediction and the Lesser one give the same +10% max health, so either counts for both).
    /// </summary>
    public static readonly (string Name, uint[] Spells)[] BuiltIns =
    {
        ("Asheron's Benediction",        new uint[] { 3810, 4024 }),
        ("Asheron's Lesser Benediction", new uint[] { 3810, 4024 }),
        ("Blackmoor's Favor",            new uint[] { 3811 }),
    };

    /// <summary>The built-in item of that name (its own spelling), or null.</summary>
    public static string? BuiltInName(string? name)
    {
        string n = (name ?? "").Trim();
        foreach (var (b, _) in BuiltIns)
            if (b.Equals(n, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    public static int ClampRepeatMinutes(int minutes) => Math.Clamp(minutes, MinRepeatMinutes, MaxRepeatMinutes);

    /// <summary>A name as typed: trimmed, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name)
    {
        string n = (name ?? "").Trim();
        return n.Length > MaxNameLength ? n.Substring(0, MaxNameLength).TrimEnd() : n;
    }

    public static CustomBuffItem? FindCustom(IReadOnlyList<CustomBuffItem>? customs, string? name)
    {
        if (customs == null) return null;
        string n = (name ?? "").Trim();
        foreach (var c in customs)
            if (c != null && c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public static bool IsBlocked(IReadOnlyList<string>? blocked, string name)
    {
        if (blocked == null) return false;
        foreach (string b in blocked)
            if (string.Equals(b, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public enum EditResult { Changed, Unchanged, NotFound, Added, AlreadyListed, BuiltIn, EmptyName, Updated, Removed }

    /// <summary>
    /// Allows or blocks a built-in or custom item by name. Built-ins are blocked through the
    /// blocked list (a new list when it changes); a custom item's Allowed is set on a copy in a
    /// new list, so a reader holding the old lists never sees them change.
    /// </summary>
    public static EditResult SetAllowed(IReadOnlyList<string> blocked, IReadOnlyList<CustomBuffItem> customs, string name, bool allowed,
        out List<string> newBlocked, out List<CustomBuffItem> newCustoms, out string shownName)
    {
        newBlocked = new List<string>(blocked);
        newCustoms = new List<CustomBuffItem>(customs);
        shownName = CleanName(name);
        string? builtIn = BuiltInName(name);
        if (builtIn != null)
        {
            shownName = builtIn;
            bool isBlocked = IsBlocked(blocked, builtIn);
            if (isBlocked != allowed) return EditResult.Unchanged;
            if (allowed) newBlocked.RemoveAll(b => string.Equals(b, builtIn, StringComparison.OrdinalIgnoreCase));
            else newBlocked.Add(builtIn);
            return EditResult.Changed;
        }
        for (int i = 0; i < newCustoms.Count; i++)
        {
            var c = newCustoms[i];
            if (c == null || !c.Name.Equals(shownName, StringComparison.OrdinalIgnoreCase)) continue;
            shownName = c.Name;
            if (c.Allowed == allowed) return EditResult.Unchanged;
            var copy = c.Copy();
            copy.Allowed = allowed;
            newCustoms[i] = copy;
            return EditResult.Changed;
        }
        return EditResult.NotFound;
    }

    /// <summary>
    /// Adds a custom item (a new list). <paramref name="spells"/> null: its spells aren't known yet
    /// (added by name). Adding one already listed with spells read from the item refreshes them
    /// (<see cref="EditResult.Updated"/>); a built-in name is refused.
    /// </summary>
    public static EditResult Add(IReadOnlyList<CustomBuffItem> customs, string? name, IReadOnlyList<uint>? spells,
        out List<CustomBuffItem> newCustoms, out CustomBuffItem? item)
    {
        newCustoms = new List<CustomBuffItem>(customs);
        item = null;
        string n = CleanName(name);
        if (n.Length == 0) return EditResult.EmptyName;
        if (BuiltInName(n) != null) return EditResult.BuiltIn;
        for (int i = 0; i < newCustoms.Count; i++)
        {
            var c = newCustoms[i];
            if (c == null || !c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) continue;
            if (spells == null) { item = c; return EditResult.AlreadyListed; }
            var copy = c.Copy();
            copy.SpellIds = new List<uint>(spells);
            copy.SpellsRead = true;
            newCustoms[i] = copy;
            item = copy;
            return EditResult.Updated;
        }
        item = new CustomBuffItem
        {
            Name = n,
            SpellIds = spells != null ? new List<uint>(spells) : new List<uint>(),
            SpellsRead = spells != null,
        };
        newCustoms.Add(item);
        return EditResult.Added;
    }

    /// <summary>Removes a custom item (a new list). Built-ins can't be removed, only blocked.</summary>
    public static EditResult Remove(IReadOnlyList<CustomBuffItem> customs, string? name, out List<CustomBuffItem> newCustoms, out string shownName)
    {
        newCustoms = new List<CustomBuffItem>(customs);
        shownName = CleanName(name);
        string? builtIn = BuiltInName(name);
        if (builtIn != null) { shownName = builtIn; return EditResult.BuiltIn; }
        for (int i = 0; i < newCustoms.Count; i++)
        {
            if (newCustoms[i] == null || !newCustoms[i].Name.Equals(shownName, StringComparison.OrdinalIgnoreCase)) continue;
            shownName = newCustoms[i].Name;
            newCustoms.RemoveAt(i);
            return EditResult.Removed;
        }
        return EditResult.NotFound;
    }
}

/// <summary>One line of the buff item list as the picker sees it: a built-in or a custom item.</summary>
internal sealed class BuffItemEntry
{
    public string Name = string.Empty;
    public bool BuiltIn;
    public bool Allowed = true;
    /// <summary>The spell ids that mean its buff is on (empty: none known).</summary>
    public uint[] Spells = Array.Empty<uint>();
    /// <summary>The spells are known (always for a built-in): with none, the repeat timer is used.</summary>
    public bool SpellsKnown = true;
    public int RepeatMinutes = BuffItemRules.DefaultRepeatMinutes;
    /// <summary>The custom item behind this line (its LastUsedUtc is set on a use); null for a built-in.</summary>
    public CustomBuffItem? Custom;

    public bool UsesTimer => SpellsKnown && Spells.Length == 0;

    /// <summary>The built-ins (a blocked one is listed, not allowed), then the custom items.</summary>
    public static List<BuffItemEntry> Build(IReadOnlyList<string>? blocked, IReadOnlyList<CustomBuffItem>? customs)
    {
        var list = new List<BuffItemEntry>();
        foreach (var (name, spells) in BuffItemRules.BuiltIns)
            list.Add(new BuffItemEntry { Name = name, BuiltIn = true, Allowed = !BuffItemRules.IsBlocked(blocked, name), Spells = spells });
        if (customs != null)
        {
            foreach (var c in customs)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.Name) || BuffItemRules.BuiltInName(c.Name) != null) continue;
                list.Add(new BuffItemEntry
                {
                    Name = c.Name.Trim(),
                    Allowed = c.Allowed,
                    Spells = c.SpellIds.ToArray(),
                    SpellsKnown = c.SpellsRead || c.SpellIds.Count > 0,
                    RepeatMinutes = BuffItemRules.ClampRepeatMinutes(c.RepeatMinutes),
                    Custom = c,
                });
            }
        }
        return list;
    }

    public static BuffItemEntry? Find(IReadOnlyList<BuffItemEntry> entries, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var e in entries)
            if (e.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }
}

/// <summary>An item in the pack: its object id and name.</summary>
internal readonly record struct BuffItemInPack(int Id, string Name);

/// <summary>The item to use now, the line it matched, and why.</summary>
internal readonly record struct BuffItemChoice(int ItemId, BuffItemEntry Entry, string Why);

/// <summary>
/// Which buff item to use now. One item per call, the first in pack order whose line is allowed and
/// whose buff is due: with spells, none of them on the player; with no spell to check, its repeat
/// time gone by since the last use. Never the same item within <see cref="BuffItemRules.RetryAfterUseSec"/>
/// of a use (an item that shares a spell with it counts as used too), and an item used
/// <see cref="BuffItemRules.UsesBeforeRest"/> times in a row without its buff showing rests
/// <see cref="BuffItemRules.RestMinutes"/> minutes (a potion is not drunk every 15 s for nothing).
/// Only items in the pack are ever picked. Times are passed in.
/// </summary>
internal sealed class BuffItemPicker
{
    private sealed class State
    {
        public DateTime UsedAt = DateTime.MinValue;
        public int UsesWithoutBuff;
        public DateTime RestUntil = DateTime.MinValue;
    }

    private readonly Dictionary<string, State> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = new();

    /// <summary>Lines for the player (an item rested); taken by <see cref="TakeNotes"/>.</summary>
    public List<string> TakeNotes()
    {
        var n = new List<string>(_notes);
        _notes.Clear();
        return n;
    }

    private State StateOf(string name)
    {
        if (!_state.TryGetValue(name, out var s)) _state[name] = s = new State();
        return s;
    }

    private static bool AnyOn(uint[] spells, IReadOnlyCollection<uint> active)
    {
        foreach (uint s in spells)
            if (Contains(active, s)) return true;
        return false;
    }

    private static bool Contains(IReadOnlyCollection<uint> set, uint v)
    {
        if (set is ISet<uint> hs) return hs.Contains(v);
        foreach (uint x in set) if (x == v) return true;
        return false;
    }

    public BuffItemChoice? Choose(IReadOnlyList<BuffItemEntry> entries, IReadOnlyList<BuffItemInPack> pack,
        IReadOnlyCollection<uint> active, DateTime now, DateTime utcNow)
    {
        foreach (var p in pack)
        {
            var e = BuffItemEntry.Find(entries, p.Name);
            if (e == null || !e.Allowed) continue;
            var st = StateOf(e.Name);
            if (e.Spells.Length > 0 && AnyOn(e.Spells, active))
            {
                st.UsesWithoutBuff = 0;
                continue;
            }
            if (now < st.RestUntil) continue;
            if ((now - st.UsedAt).TotalSeconds < BuffItemRules.RetryAfterUseSec) continue;
            if (e.Spells.Length > 0)
            {
                if (st.UsesWithoutBuff >= BuffItemRules.UsesBeforeRest)
                {
                    st.UsesWithoutBuff = 0;
                    st.RestUntil = now.AddMinutes(BuffItemRules.RestMinutes);
                    _notes.Add($"[RynthAi] Buff item: {e.Name} was used {BuffItemRules.UsesBeforeRest} times and its buff didn't show. Trying it again in {BuffItemRules.RestMinutes} minutes.");
                    continue;
                }
                return new BuffItemChoice(p.Id, e, "its buff isn't on");
            }
            if (!e.SpellsKnown) continue;   // a custom item whose spells haven't been read yet
            DateTime? last = e.Custom?.LastUsedUtc;
            if (last is DateTime l && (utcNow - l).TotalMinutes < e.RepeatMinutes) continue;
            return new BuffItemChoice(p.Id, e, $"no spell to check, used every {e.RepeatMinutes} min");
        }
        return null;
    }

    /// <summary>
    /// The choice went out (or was tried): no use of it, or of an item sharing a spell with it,
    /// for <see cref="BuffItemRules.RetryAfterUseSec"/>. <paramref name="sent"/>: the use was sent,
    /// so it counts towards the rest and starts a custom item's repeat time.
    /// </summary>
    public void NoteUsed(BuffItemChoice c, IReadOnlyList<BuffItemEntry> entries, DateTime now, DateTime utcNow, bool sent = true)
    {
        var st = StateOf(c.Entry.Name);
        st.UsedAt = now;
        if (c.Entry.Spells.Length > 0)
        {
            foreach (var other in entries)
            {
                if (ReferenceEquals(other, c.Entry) || other.Spells.Length == 0) continue;
                foreach (uint s in other.Spells)
                    if (Array.IndexOf(c.Entry.Spells, s) >= 0) { StateOf(other.Name).UsedAt = now; break; }
            }
        }
        if (!sent) return;
        if (c.Entry.Spells.Length > 0) st.UsesWithoutBuff++;
        if (c.Entry.Custom != null) c.Entry.Custom.LastUsedUtc = utcNow;
    }

    /// <summary>What the line is doing now, for the Buff Items list and /ra buffitems list.</summary>
    public string Describe(BuffItemEntry e, int inPack, IReadOnlyCollection<uint>? active, DateTime now, DateTime utcNow)
    {
        if (!e.Allowed) return "blocked";
        if (inPack <= 0) return "not in your pack";
        _state.TryGetValue(e.Name, out var st);
        if (e.Spells.Length > 0)
        {
            if (active != null && AnyOn(e.Spells, active)) return "buff on";
            if (st != null && now < st.RestUntil)
                return $"its buff didn't show, trying again in {Math.Max(1, (int)Math.Ceiling((st.RestUntil - now).TotalMinutes))} min";
            if (st != null && (now - st.UsedAt).TotalSeconds < BuffItemRules.RetryAfterUseSec) return "used, waiting for its buff";
            return active == null ? "in your pack" : "buff off: used at the next check";
        }
        if (!e.SpellsKnown) return "reading its spell";
        DateTime? last = e.Custom?.LastUsedUtc;
        if (last is not DateTime l) return $"no spell to check: used now, then every {e.RepeatMinutes} min";
        double left = e.RepeatMinutes - (utcNow - l).TotalMinutes;
        return left <= 0
            ? $"no spell to check: due (every {e.RepeatMinutes} min)"
            : $"no spell to check: next in {Math.Max(1, (int)Math.Ceiling(left))} min (every {e.RepeatMinutes} min)";
    }
}
