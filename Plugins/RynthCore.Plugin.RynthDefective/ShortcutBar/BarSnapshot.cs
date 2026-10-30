// ============================================================================
//  RynthDefective - ShortcutBar/BarSnapshot.cs
//  What the shortcut bar shows, built on the plugin tick (BarSnapshotBuilder)
//  and drawn on the render thread (ShortcutBarWindow): one immutable record per
//  slot, swapped whole.
//
//  Slots 1-18 are AC's own shortcut slots (RynthCoreHost.GetShortcuts, plugin
//  API v90): the items the retail toolbar holds, the keys AC's keymap gives
//  UseQuickSlot_1-18. Slots 19 and on are this plugin's own (DefData.Slots),
//  their keys the engine's hotkeys (/rdef use N). The player chooses how many
//  the bar shows (DefSettings.BarSlots).
//  An independent bar (DefSettings.IndependentBar) has no AC slots: every slot
//  N is own slot N (BarMapping), and AC's toolbar isn't read at all.
//  An own slot can hold a spell (2026-10-28): its picture is the spell's, and
//  the snapshot carries the character's spells for the slot menu's search.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Views;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective.ShortcutBar;

/// <summary>How a slot's count line reads: plain, in hand / worn, or nothing carried.</summary>
internal enum CountTone { Plain, InHand, Missing }

/// <summary>One slot as drawn. Own: RynthDefective's own slot number it is (0 for one of AC's).</summary>
internal sealed record BarSlot(
    int Number,
    bool Retail,
    int Own,
    uint ObjectId,
    uint SpellId,
    string Name,
    string Count,
    CountTone Tone,
    string Key,
    string AllKeys,
    string Tooltip)
{
    public bool Empty => ObjectId == 0 && SpellId == 0 && Name.Length == 0;
}

/// <summary>
/// The bar as drawn. Independent: every slot is RynthDefective's own (DefSettings.IndependentBar).
/// Spells: the character's spells, A-Z, for "Put a spell here..." (empty when they can't be read);
/// CanCast: the engine casts for plugins. WeaponsButton: the title shows the Weapons button
/// (DefSettings.WeaponsButtonOnBar).
/// </summary>
internal sealed record BarSnapshot(BarSlot[] Slots, bool Ready, int IconSize, bool Independent = false,
    IReadOnlyList<SpellInfo>? Spells = null, bool CanCast = false, bool WeaponsButton = false)
{
    public static readonly BarSnapshot NotReady = new(Array.Empty<BarSlot>(), false, 36);

    public IReadOnlyList<SpellInfo> KnownSpells => Spells ?? Array.Empty<SpellInfo>();
}

/// <summary>Builds the snapshot on the plugin tick (reads the host and the carried items).</summary>
internal sealed class BarSnapshotBuilder
{
    private readonly RynthCoreHost _host;
    private readonly uint[] _ids = new uint[DefSettings.RetailSlots], _spells = new uint[DefSettings.RetailSlots];
    private readonly string[] _keys = new string[DefSettings.RetailSlots];
    private long _keysReadMs = long.MinValue / 2, _fastKeysUntilMs;

    public BarSnapshotBuilder(RynthCoreHost host)
    {
        _host = host;
        Array.Fill(_keys, string.Empty);
    }

    /// <summary>A key was changed from the bar: ask again often for a few seconds (the change is made on the game's tick).</summary>
    public void InvalidateKeys()
    {
        _keysReadMs = long.MinValue / 2;
        _fastKeysUntilMs = Environment.TickCount64 + 4000;
    }

    /// <summary>AC's keys for the 18 retail slots (re-asked every 2 s: the keymap can change, from any client).</summary>
    private void RefreshKeys(long now)
    {
        if (now - _keysReadMs < (now < _fastKeysUntilMs ? 250 : 2000)) return;
        _keysReadMs = now;
        for (int i = 0; i < _keys.Length; i++) _keys[i] = _host.GetActionKeys("UseQuickSlot_" + (i + 1));
    }

    public BarSnapshot Build(DefSession session, IReadOnlyList<CarriedItem> items, long now)
    {
        if (!_host.HasShortcuts) return BarSnapshot.NotReady;
        DefSettings st = session.Settings;
        IReadOnlyList<SpellInfo> spells = session.KnownSpells();
        bool canCast = _host.HasCastSpell;
        if (st.IndependentBar)
        {
            // Every slot is RynthDefective's own: AC's toolbar isn't read or shown (BarMapping).
            var own = new BarSlot[st.BarSlots];
            for (int n = 1; n <= st.BarSlots; n++) own[n - 1] = Own(n, BarMapping.OwnSlot(n, true), true, session, items, spells, _host);
            return new BarSnapshot(own, true, st.ButtonSize, true, spells, canCast, st.WeaponsButtonOnBar);
        }
        if (_host.GetShortcuts(_ids, _spells) == 0) return BarSnapshot.NotReady;
        RefreshKeys(now);
        var byId = new Dictionary<uint, CarriedItem>(items.Count);
        foreach (CarriedItem it in items) byId[it.Id] = it;

        var slots = new BarSlot[st.BarSlots];
        for (int n = 1; n <= st.BarSlots; n++)
            slots[n - 1] = BarMapping.IsRetail(n, false) ? Retail(n, byId) : Own(n, BarMapping.OwnSlot(n, false), false, session, items, spells, _host);
        return new BarSnapshot(slots, true, st.ButtonSize, false, spells, canCast, st.WeaponsButtonOnBar);
    }

    private BarSlot Retail(int n, Dictionary<uint, CarriedItem> byId)
    {
        uint id = _ids[n - 1], spell = _spells[n - 1];
        string allKeys = _keys[n - 1];
        string key = FirstKey(allKeys);
        if (id == 0 && spell == 0)
            return new BarSlot(n, true, 0, 0, 0, string.Empty, string.Empty, CountTone.Plain, key, allKeys,
                $"Slot {n} (the game's own): empty.{KeysLine(allKeys)}\nRight-click: put the selected item here, or change its keys.");
        string name;
        string count = string.Empty;
        CountTone tone = CountTone.Plain;
        if (id != 0 && byId.TryGetValue(id, out CarriedItem? it))
        {
            name = it.FullName;
            count = SlotText.Count(new SlotStatus(it, it.Stack, it.Uses, false), out uint color);
            tone = color == SlotText.InHand ? CountTone.InHand : color == SlotText.Missing ? CountTone.Missing : CountTone.Plain;
        }
        else if (id != 0)
        {
            name = _host.TryGetObjectName(id, out string n2) && n2.Length > 0 ? n2 : $"0x{id:X8}";
            count = "none";
            tone = CountTone.Missing;
        }
        else name = SpellTable.Name(spell);
        string tip = $"Slot {n} (the game's own): {name}{(count.Length > 0 ? " - " + count : "")}.{KeysLine(allKeys)}\n" +
                     "Click: use it, as its key does. Right-click: change the slot or its keys.";
        return new BarSlot(n, true, 0, id, id == 0 ? spell : 0, name, count, tone, key, allKeys, tip);
    }

    private static BarSlot Own(int n, int own, bool independent, DefSession session, IReadOnlyList<CarriedItem> items,
        IReadOnlyList<SpellInfo> spells, RynthCoreHost host)
    {
        DefSlot slot = own >= 1 && own <= session.Data.Slots.Count ? session.Data.Slots[own - 1] : new DefSlot();
        // Every key of "/rdef use N" (the engine's hotkeys), else the one bound to the item by name.
        string all = host.HasKeyBinding ? host.GetActionKeys(DefCommandParser.UseCommand(own)) : string.Empty;
        string key = all.Length > 0 ? FirstKey(all) : session.KeyOfSlot(own) ?? string.Empty;
        if (all.Length == 0) all = key;
        string whose = independent ? "RynthCore's: this bar is independent of the game's toolbar" : "RynthCore's: the game holds 18";
        if (slot.IsSpell) return OwnSpell(n, own, slot, whose, key, all, spells);
        if (slot.Item == null)
            return new BarSlot(n, false, own, 0, 0, string.Empty, string.Empty, CountTone.Plain, SlotText.ShortKey(key), all,
                $"Slot {n} ({whose}): empty.{KeysLine(all)}\nRight-click: put the selected item or one of your spells here, or change its keys.");
        SlotStatus s = ItemMatcher.Status(slot.Item, items);
        string count = SlotText.Count(s, out uint color);
        CountTone tone = color == SlotText.InHand ? CountTone.InHand : color == SlotText.Missing ? CountTone.Missing : CountTone.Plain;
        string tip = SlotText.Tooltip(n, slot.Item, s, slot.Mode, key.Length > 0 ? key : null)
                     + $"\n({whose}.)";
        return new BarSlot(n, false, own, s.Item?.Id ?? 0, 0, s.Item?.FullName ?? slot.Item.FullName, count, tone,
            SlotText.ShortKey(key), all, tip);
    }

    /// <summary>
    /// A spell slot: the spell's picture and name. A spell the character doesn't know (another
    /// character's file, a spell lost) shows dimmed with "?" (when the spellbook can be read at all).
    /// </summary>
    private static BarSlot OwnSpell(int n, int own, DefSlot slot, string whose, string key, string all, IReadOnlyList<SpellInfo> spells)
    {
        string name = slot.SpellLabel;
        SpellInfo? info = SpellTable.TryGet(slot.SpellId, out SpellInfo si) ? si : null;
        bool known = spells.Count == 0;
        foreach (SpellInfo s in spells)
            if (s.Id == slot.SpellId) { known = true; break; }
        string tip = $"Slot {n}: {name}, a spell.\nClick or press the key: {SpellTargeting.Describe(info)} (Magic mode first)."
                     + (known ? "" : "\nYou don't know this spell: pressing it does nothing.")
                     + (key.Length > 0 ? "\nKey: " + key : "\nNo key yet.") + $"\n({whose}.)\nRight-click: change the slot or its keys.";
        return new BarSlot(n, false, own, 0, slot.SpellId, name, known ? string.Empty : "?", known ? CountTone.Plain : CountTone.Missing,
            SlotText.ShortKey(key), all, tip);
    }

    /// <summary>The first of "1, Ctrl+1": what fits on the slot.</summary>
    private static string FirstKey(string keys)
    {
        int comma = keys.IndexOf(',');
        return comma < 0 ? keys : keys[..comma];
    }

    private static string KeysLine(string keys) => keys.Length > 0 ? "\nKeys: " + keys : "\nNo key yet.";
}
