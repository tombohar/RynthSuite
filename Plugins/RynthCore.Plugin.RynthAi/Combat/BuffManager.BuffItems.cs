using System;
using System.Collections.Generic;
using System.Text;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>One line of the Buff Items list for the window and /ra buffitems list (pump thread makes it).</summary>
public sealed class BuffItemRow
{
    public string Name = string.Empty;
    public bool BuiltIn;
    public bool Allowed;
    /// <summary>How many are in the pack (stacks counted).</summary>
    public int InPack;
    /// <summary>What it is doing now ("buff on", "not in your pack", ...).</summary>
    public string Status = string.Empty;
    /// <summary>The spells that mean its buff is on, by name; empty when it has none to check.</summary>
    public string Spells = string.Empty;
    /// <summary>No spell to check: used every <see cref="RepeatMinutes"/> minutes instead.</summary>
    public bool UsesTimer;
    public int RepeatMinutes;
}

// Buff items (Use Buff Items): BuffItemPicker.cs decides, this part reads the game and sends the use.
public partial class BuffManager
{
    private readonly BuffItemPicker _buffItemPicker = new();
    private DateTime _lastBuffItemCheck = DateTime.MinValue;
    /// <summary>Items whose appraisal was asked for to read their spells (object id -> when).</summary>
    private readonly Dictionary<int, DateTime> _buffItemIdAsked = new();
    /// <summary>How long an asked-for appraisal may take before the item counts as casting nothing we can read.</summary>
    private const double BuffItemAppraiseWaitSec = 10;
    /// <summary>PropertyDataId.Spell: the spell an item casts when used (CreateObject header).</summary>
    private const uint STypeDidSpell = 28;

    /// <summary>
    /// Uses a buff item whose buff isn't on the player: the built-ins (Asheron's Benediction, Asheron's
    /// Lesser Benediction, Blackmoor's Favor) unless blocked, and the player's own allowed items. One
    /// item per check, 3 s apart; the rest is BuffItemPicker's (15 s before the same item again, a rest
    /// after two uses whose buff didn't show, a repeat time for an item with no spell to check). Only
    /// items in the pack are used. Off with UseBuffItems.
    /// </summary>
    private void TryUseBuffItems()
    {
        if (!_settings.UseBuffItems || _worldObjectCache == null || !_host.HasReadPlayerEnchantments) return;
        if ((RynthClock.Now - _lastBuffItemCheck).TotalMilliseconds < 3000) return;
        _lastBuffItemCheck = RynthClock.Now;
        if (!_host.CanCastNow) return;

        var inventory = _worldObjectCache.GetDirectInventory(forceRefresh: false);
        ReadCustomBuffItemSpells(inventory);
        var entries = BuffItemEntry.Build(_settings.BlockedBuffItems, _settings.CustomBuffItems);
        var pack = PackForBuffItems(inventory);
        var active = ReadActiveSpellIds();

        BuffItemChoice? pick = _buffItemPicker.Choose(entries, pack, active, RynthClock.Now, DateTime.UtcNow);
        foreach (string note in _buffItemPicker.TakeNotes()) { _host.Log(note); _host.WriteToChat(note, 2); }
        if (pick is not BuffItemChoice c) return;

        _host.Log($"[RynthAi] Buff item: using {c.Entry.Name} ({c.Why}).");
        bool sent = _host.UseFor(unchecked((uint)c.ItemId), "Buff", $"buff item: {c.Entry.Name}, {c.Why}");
        _buffItemPicker.NoteUsed(c, entries, RynthClock.Now, DateTime.UtcNow, sent);
        _lastCastAttempt = RynthClock.Now;
    }

    private static List<BuffItemInPack> PackForBuffItems(IReadOnlyList<WorldObject> inventory)
    {
        var pack = new List<BuffItemInPack>(inventory.Count);
        foreach (var wo in inventory)
            if (!string.IsNullOrEmpty(wo.Name)) pack.Add(new BuffItemInPack(wo.Id, wo.Name));
        return pack;
    }

    private HashSet<uint> ReadActiveSpellIds()
    {
        var active = new HashSet<uint>();
        if (!_host.HasReadPlayerEnchantments) return active;
        var sids = new uint[256];
        var exp = new double[256];
        int n = _host.ReadPlayerEnchantments(sids, exp, sids.Length);
        for (int i = 0; i < n && i < sids.Length; i++) active.Add(sids[i]);
        return active;
    }

    /// <summary>
    /// A custom item added by name has no spells read yet: read them from the first one in the pack
    /// (once; the profile keeps them). An item still waiting for its appraisal stays unread and isn't used.
    /// </summary>
    private void ReadCustomBuffItemSpells(IReadOnlyList<WorldObject> inventory)
    {
        foreach (var c in _settings.CustomBuffItems)
        {
            if (c == null || c.SpellsRead) continue;
            WorldObject? wo = null;
            foreach (var w in inventory)
                if (w.Name != null && w.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) { wo = w; break; }
            if (wo == null) continue;
            if (!TryReadItemUseSpells(wo.Id, out var spells)) continue;
            // In place: one item's own fields, read by this thread only (the window shows the next snapshot).
            c.SpellIds = spells;
            c.SpellsRead = true;
            _host.Log($"[RynthAi] Buff item: {c.Name} " + (spells.Count > 0
                ? $"casts {SpellNames(spells)}; its buff counts as on while that is on you."
                : $"casts no spell RynthAi can check; it is used every {BuffItemRules.ClampRepeatMinutes(c.RepeatMinutes)} min."));
        }
    }

    /// <summary>
    /// The buff spells an item casts when used: its spell (data id 28 from its CreateObject header, no
    /// appraisal needed), plus the spell book an appraisal lists. A spell the catalog lists with no
    /// duration (a heal) is left out: it never shows as an enchantment. False when that can't be told
    /// yet: no spell in the header and no appraisal (one is asked for; after
    /// <see cref="BuffItemAppraiseWaitSec"/> without one, the item counts as casting nothing).
    /// </summary>
    internal bool TryReadItemUseSpells(int objectId, out List<uint> spells)
    {
        spells = new List<uint>();
        uint uid = unchecked((uint)objectId);
        bool appraised = false;
        try
        {
            if (_host.TryGetObjectDataIdProperty(uid, STypeDidSpell, out uint did) && did != 0 && did < 0x10000)
                spells.Add(did);
        }
        catch { }
        try
        {
            if (_host.HasGetObjectSpellIds)
            {
                var buf = new uint[32];
                int n = _host.GetObjectSpellIds(uid, buf, buf.Length);
                if (n >= 0) appraised = true;
                for (int i = 0; i < n && i < buf.Length; i++)
                    if (buf[i] != 0 && !spells.Contains(buf[i])) spells.Add(buf[i]);
            }
        }
        catch { }

        if (spells.Count == 0 && !appraised && _host.HasGetObjectSpellIds)
        {
            if (!_buffItemIdAsked.TryGetValue(objectId, out DateTime asked))
            {
                _buffItemIdAsked[objectId] = RynthClock.Now;
                if (_host.HasRequestId) { try { _host.RequestId(uid); } catch { } }
                return false;
            }
            if ((RynthClock.Now - asked).TotalSeconds < BuffItemAppraiseWaitSec) return false;
        }

        if (SpellCatalog.IsLoaded)
            spells.RemoveAll(id => SpellCatalog.Get((int)id) is { } e && e.DurationSec <= 0);
        return true;
    }

    private static string SpellNames(IReadOnlyList<uint> spells)
    {
        var sb = new StringBuilder();
        foreach (uint id in spells)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(SpellCatalog.Get((int)id)?.Name is { Length: > 0 } n ? n : $"spell {id}");
        }
        return sb.ToString();
    }

    private static string HowItIsUsed(CustomBuffItem item) => !item.SpellsRead
        ? "its spell is read from the item when it is in your pack, before the first use"
        : item.SpellIds.Count > 0
            ? $"its buff counts as on while {SpellNames(item.SpellIds)} is on you"
            : $"it casts no spell RynthAi can check, so it is used every {BuffItemRules.ClampRepeatMinutes(item.RepeatMinutes)} min (change it in the list or with /ra buffitems repeat)";

    /// <summary>"Add selected item" and /ra buffitems addselected: the selected item as a custom buff item. Pump thread.</summary>
    public bool AddSelectedBuffItem(out string reply)
    {
        uint sel = _host.GetSelectedItemId();
        if (sel == 0) { reply = "[RynthAi] Buff items: nothing selected. Click the item in your inventory first."; return false; }
        var wo = _worldObjectCache?[unchecked((int)sel)];
        if (wo == null || string.IsNullOrEmpty(wo.Name)) { reply = $"[RynthAi] Buff items: item 0x{sel:X8} isn't known yet. Try again in a moment."; return false; }
        return AddBuffItem(wo.Name, wo.Id, out reply);
    }

    /// <summary>
    /// /ra buffitems add &lt;name&gt; and the window's name box: adds an item by name. When one is in the
    /// pack its spells are read now, else before its first use. Pump thread.
    /// </summary>
    public bool AddBuffItemByName(string name, out string reply)
    {
        string n = BuffItemRules.CleanName(name);
        int id = 0;
        if (_worldObjectCache != null)
            foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
                if (wo.Name != null && wo.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) { id = wo.Id; n = wo.Name; break; }
        return AddBuffItem(n, id, out reply);
    }

    private bool AddBuffItem(string name, int objectId, out string reply)
    {
        List<uint>? spells = null;
        if (objectId != 0 && TryReadItemUseSpells(objectId, out var read)) spells = read;
        var r = _settings.AddCustomBuffItem(name, spells, out var item);
        switch (r)
        {
            case BuffItemRules.EditResult.EmptyName:
                reply = "[RynthAi] Buff items: no name given. Usage: /ra buffitems add <item name>";
                return false;
            case BuffItemRules.EditResult.BuiltIn:
                string b = BuffItemRules.BuiltInName(name) ?? name;
                reply = $"[RynthAi] Buff items: {b} is built in and {(BuffItemRules.IsBlocked(_settings.BlockedBuffItems, b) ? "blocked" : "allowed")}. Tick or untick it in the list (/ra buffitems allow|block {b}).";
                return false;
            case BuffItemRules.EditResult.AlreadyListed:
                reply = $"[RynthAi] Buff items: {item!.Name} is already in the list ({(item.Allowed ? "allowed" : "blocked")}); {HowItIsUsed(item)}.";
                return true;
            case BuffItemRules.EditResult.Updated:
                reply = $"[RynthAi] Buff items: {item!.Name} read again from the item; {HowItIsUsed(item)}.";
                return true;
            default:
                reply = $"[RynthAi] Buff items: added {item!.Name} (allowed); {HowItIsUsed(item)}."
                    + (_settings.UseBuffItems ? "" : " Use Buff Items is off, so nothing is used until it is on.");
                return true;
        }
    }

    /// <summary>The list as the window and /ra buffitems list show it: every line, its pack count and what it is doing. Pump thread.</summary>
    public List<BuffItemRow> DescribeBuffItems()
    {
        var entries = BuffItemEntry.Build(_settings.BlockedBuffItems, _settings.CustomBuffItems);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_worldObjectCache != null)
        {
            foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
            {
                if (string.IsNullOrEmpty(wo.Name) || BuffItemEntry.Find(entries, wo.Name) == null) continue;
                counts.TryGetValue(wo.Name, out int had);
                counts[wo.Name] = had + Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
            }
        }
        HashSet<uint>? active = _host.HasReadPlayerEnchantments ? ReadActiveSpellIds() : null;
        var rows = new List<BuffItemRow>(entries.Count);
        foreach (var e in entries)
        {
            counts.TryGetValue(e.Name, out int inPack);
            rows.Add(new BuffItemRow
            {
                Name = e.Name,
                BuiltIn = e.BuiltIn,
                Allowed = e.Allowed,
                InPack = inPack,
                Status = _buffItemPicker.Describe(e, inPack, active, RynthClock.Now, DateTime.UtcNow),
                Spells = e.Spells.Length > 0 ? SpellNames(e.Spells) : string.Empty,
                UsesTimer = e.UsesTimer,
                RepeatMinutes = e.RepeatMinutes,
            });
        }
        return rows;
    }
}
