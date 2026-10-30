using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>One slot of the bar: an item or a spell (or nothing), and how pressing it uses that item.</summary>
internal sealed class DefSlot
{
    /// <summary>The item the slot holds; null = none.</summary>
    public ItemRef? Item;
    /// <summary>How pressing it uses the item. <see cref="UseMode.Auto"/> picks by what the item is.</summary>
    public UseMode Mode = UseMode.Auto;
    /// <summary>The spell the slot casts (2026-10-28); 0 = none. A slot holds an item or a spell, never both.</summary>
    public uint SpellId;
    /// <summary>The spell's name when it was put in the slot (shown when the spell table doesn't know it).</summary>
    public string SpellName = string.Empty;

    public bool Empty => Item == null && SpellId == 0;
    public bool IsSpell => Item == null && SpellId != 0;

    /// <summary>What the slot holds, by name: the item's, the spell's, or "".</summary>
    public string Label => Item?.FullName ?? (SpellId != 0 ? SpellLabel : string.Empty);

    /// <summary>The spell's name: the table's, else the saved one, else "Spell 1234".</summary>
    public string SpellLabel =>
        SpellTable.TryGet(SpellId, out SpellInfo s) ? s.Name : SpellName.Length > 0 ? SpellName : SpellTable.Name(SpellId);

    public DefSlot Clone() => new() { Item = Item?.Clone(), Mode = Mode, SpellId = SpellId, SpellName = SpellName };
}

/// <summary>The "use as" choice of a slot.</summary>
internal enum UseMode { Auto, Use, OnSelf, OnSelected, Equip }

internal static class UseModes
{
    public static readonly string[] Labels =
    {
        "Automatic (by item)", "Use it", "Use it on me", "Use it on the selected target", "Wield / wear it",
    };

    /// <summary>The action for a slot's mode and the item's kind.</summary>
    public static UseAction ActionFor(UseMode mode, ItemKind kind) => mode switch
    {
        UseMode.Use => UseAction.Use,
        UseMode.OnSelf => UseAction.UseOnSelf,
        UseMode.OnSelected => UseAction.UseOnSelected,
        UseMode.Equip => UseAction.Equip,
        _ => ItemKinds.DefaultAction(kind),
    };

    public static string Describe(UseAction a) => a switch
    {
        UseAction.Use => "used",
        UseAction.UseOnSelf => "used on you",
        UseAction.UseOnSelected => "used on the selected target",
        _ => "wielded",
    };
}

/// <summary>The kind of weapon the picker is narrowed to.</summary>
internal enum KindFilter { All, Melee, Missile, War, Void }

/// <summary>RynthDefective's settings for one character.</summary>
internal sealed class DefSettings
{
    /// <summary>
    /// The shortcut bar (2026-10-22, plugin API v90): its first 18 slots are AC's own (the retail
    /// toolbar's, its keys AC's keymap), the rest this plugin's own slots (Slots, keys the engine's
    /// hotkeys). The player picks how many it shows. With <see cref="IndependentBar"/> every slot
    /// is this plugin's own (ShortcutBar/BarMapping.cs).
    /// </summary>
    public const int RetailSlots = 18, BarSlotsMin = 1, BarSlotsMax = 42;

    /// <summary>
    /// RynthDefective's own slots (DefData.Slots, "/rdef use 1..42"): as many as the bar can show, so
    /// an independent bar is as long as the usual one (which shows own slots 1-24 after the game's 18).
    /// The old script-window bar shows the first <see cref="SlotCountMax"/> of them.
    /// </summary>
    public const int SlotsMin = 1, SlotsMax = BarSlotsMax, SlotCountMax = 24, PerRowMin = 1, PerRowMax = 12, ButtonMin = 20, ButtonMax = 64,
        PickerIconMin = 20, PickerIconMax = 48;
    public int BarSlots = RetailSlots;

    /// <summary>
    /// The shortcut bar is independent of AC's toolbar (2026-10-27): AC's 18 slots are not on it, every
    /// slot is RynthDefective's own (bar slot N = own slot N, clicks through the use controller, keys
    /// the engine's hotkeys running /rdef use N). Per character, like the slots it decides about: AC
    /// keeps its 18 shortcut slots per character on the server, and the own slots are per character
    /// here, so one character can keep the game's toolbar on the bar while another goes independent.
    /// Off (the default, and what a file from before reads as): slots 1-18 are AC's.
    /// </summary>
    public bool IndependentBar;

    /// <summary>The old script-window bar's slot count (engines without the shortcut bar).</summary>
    public int SlotCount = 8;
    public int SlotsPerRow = 8;
    /// <summary>Icon size of the slot buttons, in logical units.</summary>
    public int ButtonSize = 36;
    /// <summary>Icon size in the weapon picker's rows.</summary>
    public int PickerIconSize = 32;
    public bool ShowIcons = true;
    /// <summary>Counts and uses left under the slot buttons.</summary>
    public bool ShowCounts = true;
    /// <summary>The weapon kind the picker shows; All = every kind.</summary>
    public KindFilter PreferredKind = KindFilter.All;
    /// <summary>Weapon swaps are refused while RynthAi's macro runs (it picks the weapon itself); this lets them through.</summary>
    public bool SwapWhileBotRuns;
    /// <summary>
    /// The Weapons button (the sword) in the Shortcuts bar's title (2026-10-29). Off (the default): the
    /// title holds only edit, settings and hide, so a click aimed at one doesn't open the weapon picker;
    /// the picker opens from the settings window, the bar's right-click menu and /rdef weapons.
    /// </summary>
    public bool WeaponsButtonOnBar;
    public bool BarOpen;
    public bool PickerOpen;

    public void Clamp()
    {
        SlotCount = Math.Clamp(SlotCount, SlotsMin, SlotCountMax);
        BarSlots = Math.Clamp(BarSlots, BarSlotsMin, BarSlotsMax);
        SlotsPerRow = Math.Clamp(SlotsPerRow, PerRowMin, PerRowMax);
        ButtonSize = Math.Clamp(ButtonSize, ButtonMin, ButtonMax);
        PickerIconSize = Math.Clamp(PickerIconSize, PickerIconMin, PickerIconMax);
        if (!Enum.IsDefined(PreferredKind)) PreferredKind = KindFilter.All;
    }
}

/// <summary>One character's slots, favourites, recent weapons and settings.</summary>
internal sealed class DefData
{
    public const int MaxFavourites = 64, MaxRecent = 8;

    public DefSettings Settings = new();
    /// <summary>Always <see cref="DefSettings.SlotsMax"/> entries: shrinking the bar hides slots, it doesn't forget them.</summary>
    public List<DefSlot> Slots = NewSlots();
    public List<ItemRef> Favourites = new();
    /// <summary>The weapons wielded from the picker or a slot, newest first.</summary>
    public List<ItemRef> Recent = new();

    public static List<DefSlot> NewSlots()
    {
        var l = new List<DefSlot>(DefSettings.SlotsMax);
        for (int i = 0; i < DefSettings.SlotsMax; i++) l.Add(new DefSlot());
        return l;
    }

    /// <summary>Pads or cuts the slot list to the fixed length.</summary>
    public void FixLength()
    {
        while (Slots.Count < DefSettings.SlotsMax) Slots.Add(new DefSlot());
        if (Slots.Count > DefSettings.SlotsMax) Slots.RemoveRange(DefSettings.SlotsMax, Slots.Count - DefSettings.SlotsMax);
    }

    public bool IsFavourite(ItemRef r) => Favourites.Exists(f => f.SameAs(r));

    /// <summary>Adds or removes a favourite; returns whether it is one now.</summary>
    public bool ToggleFavourite(ItemRef r)
    {
        int i = Favourites.FindIndex(f => f.SameAs(r));
        if (i >= 0) { Favourites.RemoveAt(i); return false; }
        if (Favourites.Count >= MaxFavourites) return false;
        Favourites.Add(r.Clone());
        return true;
    }

    /// <summary>Puts a weapon first in the recent list (once), at most <see cref="MaxRecent"/>.</summary>
    public void NoteRecent(ItemRef r)
    {
        Recent.RemoveAll(x => x.SameAs(r));
        Recent.Insert(0, r.Clone());
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
    }

    /// <summary>The slot (1-based number) that holds an item of this identity, or 0.</summary>
    public int SlotOf(ItemRef r)
    {
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Item is { } x && x.SameAs(r)) return i + 1;
        return 0;
    }
}
