using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.ShortcutBar;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthCore.Plugin.RynthDefective;

/// <summary>
/// What the windows and the commands act on: the character's data, the use controller, and the
/// things a player does to slots (set one from the selected item, clear, move, bind a key). The
/// host-facing parts are delegates, so all of it runs offline in the tests.
/// </summary>
internal sealed class DefSession
{
    public DefData Data = new();
    public readonly UseController Use = new();
    public readonly HotkeyLookup Keys;

    /// <summary>Everything carried now (cached by the plugin; at most a moment old).</summary>
    public Func<IReadOnlyList<CarriedItem>> Items = () => Array.Empty<CarriedItem>();
    public Func<uint> SelectedId = () => 0;
    public Func<long> Now = () => Environment.TickCount64;
    /// <summary>Types a line to the engine's chat parser ("/rc hotkey add F9 /rdef use 3"); false when it can't.</summary>
    public Func<string, bool> RunChat = _ => false;
    /// <summary>Asks for the character's file to be written soon.</summary>
    public Action Save = () => { };
    /// <summary>AC's 18 shortcut slots now (object ids, spell ids; slot 1 first); false when they can't be read (out of the world, an older engine).</summary>
    public Func<uint[], uint[], bool> ReadRetail = (_, _) => false;
    /// <summary>An object's name as the client knows it (an item in one of AC's slots that the pack read missed); null = unknown.</summary>
    public Func<uint, string?> ObjectName = _ => null;
    /// <summary>The spells the character knows, with names, A-Z (empty = can't be read now). Spell slots.</summary>
    public Func<IReadOnlyList<SpellInfo>> KnownSpells = () => Array.Empty<SpellInfo>();
    /// <summary>The keys the engine's hotkeys give a chat command ("/rdef use 3"): "F9, Ctrl+1"; "" = none.</summary>
    public Func<string, string> CommandKeys = _ => string.Empty;
    /// <summary>Gives own slot N the key (it comes off whatever had it): the plugin's BindOwnSlotKey. False when it can't.</summary>
    public Func<int, string, bool> BindOwnKey = (_, _) => false;
    /// <summary>The pictures and bar buttons need op level 2 script windows.</summary>
    public bool IconsAvailable;
    public bool LoggedIn;
    public string Character = string.Empty;

    // The line under the bar: the last thing that happened.
    private string _status = string.Empty;
    private long _statusAtMs;
    private bool _statusIsWarning;
    public const long StatusShownMs = 9000;

    public DefSession(HotkeyLookup keys) => Keys = keys;

    public DefSettings Settings => Data.Settings;

    public void SetStatus(string text, bool warning = false)
    {
        _status = text;
        _statusIsWarning = warning;
        _statusAtMs = Now();
    }

    /// <summary>The status line while it is fresh; empty after <see cref="StatusShownMs"/>.</summary>
    public string Status(out bool warning)
    {
        warning = _statusIsWarning;
        return _status.Length > 0 && Now() - _statusAtMs < StatusShownMs ? _status : string.Empty;
    }

    // ── Using ───────────────────────────────────────────────────────────────

    /// <summary>The slot's button or key: use what it holds. Returns what to tell the player when nothing could be queued.</summary>
    public string? UseSlot(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        DefSlot slot = Data.Slots[slotNumber - 1];
        if (slot.IsSpell)
        {
            Use.Request(UseRequest.Cast(slot.SpellId, slot.SpellLabel, "slot " + slotNumber, Now()));
            return null;
        }
        if (slot.Item == null) return $"Slot {slotNumber} is empty. Set it from the bar (Edit slots) or with /rdef set {slotNumber}.";
        Use.Request(new UseRequest(slot.Item, null, slot.Mode, "slot " + slotNumber, Now()));
        return null;
    }

    /// <summary>
    /// A click on AC's own shortcut slot <paramref name="number"/> (1-18, the usual bar) holding
    /// <paramref name="objectId"/>: a healing kit is used on yourself here, through the use controller
    /// (one touch, nothing selected, as a RynthDefective slot does it), and true is returned. Anything
    /// else returns false and the click goes to AC's own toolbar use. Only the bar's click comes here:
    /// the slot's own key (AC's UseQuickSlot_N in AC's keymap) is AC's, and still goes straight to AC.
    /// </summary>
    public bool UseRetailSlot(int number, uint objectId)
    {
        CarriedItem? kit = BarMapping.RetailKit(objectId, Items());
        if (kit == null) return false;
        Use.Request(new UseRequest(ItemRef.From(kit), null, UseMode.OnSelf, "game slot " + number, Now()));
        return true;
    }

    /// <summary>/rdef use &lt;name&gt;: an item by what the player typed.</summary>
    public void UseByName(string name) =>
        Use.Request(new UseRequest(null, name, UseMode.Auto, "name '" + name + "'", Now()));

    /// <summary>The picker: wield this weapon.</summary>
    public void Wield(ItemRef item) =>
        Use.Request(new UseRequest(item, null, UseMode.Equip, "picker", Now()));

    // ── Slots ───────────────────────────────────────────────────────────────

    public string SetFromSelected(int slotNumber)
    {
        uint sel = SelectedId();
        if (sel == 0) return "Select the item in the game first (click it in your pack), then set the slot.";
        CarriedItem? it = FindById(sel);
        if (it == null) return "The selected object isn't in your pack, so it can't go in a slot.";
        return SetSlot(slotNumber, it);
    }

    /// <summary>An item dragged onto the slot (its object id).</summary>
    public string SetFromId(int slotNumber, uint objectId)
    {
        CarriedItem? it = FindById(objectId);
        if (it == null) return "That isn't in your pack, so it can't go in a slot.";
        return SetSlot(slotNumber, it);
    }

    public string SetFromName(int slotNumber, string name)
    {
        CarriedItem? it = ItemMatcher.FindByName(name, Items());
        if (it == null) return $"No item named '{name}' in your pack.";
        return SetSlot(slotNumber, it);
    }

    public string SetSlot(int slotNumber, CarriedItem it)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        DefSlot slot = Data.Slots[slotNumber - 1];
        slot.Item = ItemRef.From(it);
        slot.SpellId = 0;
        slot.SpellName = string.Empty;
        slot.Mode = UseMode.Auto;
        Save();
        ItemKind kind = ItemKinds.Classify(it);
        return $"Slot {slotNumber}: {it.FullName} ({UseModes.Describe(ItemKinds.DefaultAction(kind))} when pressed).";
    }

    /// <summary>
    /// Puts a spell the character knows in a slot (the bar's "Put a spell here...": a search over the
    /// known spells; the engine can't tell which spell is selected in the game's spellbook). Refused for
    /// a spell the character doesn't know, when the spellbook can be read.
    /// </summary>
    public string SetSpell(int slotNumber, uint spellId)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        if (spellId == 0) return "That isn't a spell.";
        IReadOnlyList<SpellInfo> known = KnownSpells();
        SpellInfo? spell = null;
        foreach (SpellInfo s in known)
            if (s.Id == spellId) { spell = s; break; }
        if (spell == null && known.Count > 0) return $"You don't know {SpellTable.Name(spellId)}, so it can't go in a slot.";
        SpellInfo info = spell ?? (SpellTable.TryGet(spellId, out SpellInfo t) ? t : new SpellInfo(spellId, SpellTable.Name(spellId), false, false));
        Data.Slots[slotNumber - 1] = new DefSlot { SpellId = spellId, SpellName = info.Name };
        Save();
        return $"Slot {slotNumber}: {info.Name} ({SpellTargeting.Describe(info)} when pressed).";
    }

    /// <summary>/rdef spell &lt;slot&gt; &lt;name&gt;: one of the character's spells by what the player typed.</summary>
    public string SetSpellFromName(int slotNumber, string text)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        IReadOnlyList<SpellInfo> known = KnownSpells();
        if (known.Count == 0) return "Your spells can't be read right now (in the world, on RynthCore with ReadKnownSpells).";
        List<SpellInfo> found = SpellTable.Search(known, text, 6, out int total);
        if (total == 0) return $"You know no spell named '{text}'.";
        bool exact = found[0].Name.Equals((text ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        if (total > 1 && !exact)
            return $"{total} of your spells match '{text}': {string.Join(", ", found.ConvertAll(s => s.Name))}{(total > found.Count ? ", ..." : "")}. Type more of the name.";
        return SetSpell(slotNumber, found[0].Id);
    }

    public string ClearSlot(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        Data.Slots[slotNumber - 1] = new DefSlot();
        Save();
        return $"Slot {slotNumber} cleared.";
    }

    /// <summary>Swaps slot <paramref name="a"/> with <paramref name="b"/> (1-based), as moving one place.</summary>
    public bool SwapSlots(int a, int b)
    {
        int max = Settings.SlotCount;
        if (a < 1 || b < 1 || a > max || b > max || a == b) return false;
        (Data.Slots[a - 1], Data.Slots[b - 1]) = (Data.Slots[b - 1], Data.Slots[a - 1]);
        Save();
        return true;
    }

    public void SetSlotMode(int slotNumber, UseMode mode)
    {
        Data.Slots[slotNumber - 1].Mode = mode;
        Save();
    }

    public void SetSlotCount(int count)
    {
        Settings.SlotCount = Math.Clamp(count, DefSettings.SlotsMin, DefSettings.SlotCountMax);
        Save();
    }

    public CarriedItem? FindById(uint id)
    {
        foreach (CarriedItem it in Items())
            if (it.Id == id) return it;
        return null;
    }

    /// <summary>
    /// After an inventory read: every slot, favourite and recent entry follows the item it stands for
    /// (a used-up stack's replacement, a new picture). Never clears anything. Returns whether a
    /// save is due. Skipped while nothing at all is carried (the inventory isn't loaded yet).
    /// </summary>
    public bool Maintain(IReadOnlyList<CarriedItem> items)
    {
        if (items.Count == 0) return false;
        bool changed = false;
        foreach (DefSlot s in Data.Slots)
            if (s.Item != null && ItemMatcher.Maintain(s.Item, items)) changed = true;
        foreach (ItemRef f in Data.Favourites)
            if (ItemMatcher.Maintain(f, items)) changed = true;
        foreach (ItemRef r in Data.Recent)
            if (ItemMatcher.Maintain(r, items)) changed = true;
        return changed;
    }

    // ── Keys ────────────────────────────────────────────────────────────────

    /// <summary>The key bound to slot <paramref name="slotNumber"/> or to its item by name, or null.</summary>
    public string? KeyOfSlot(int slotNumber)
    {
        long now = Now();
        string? key = Keys.KeyFor(DefCommandParser.UseCommand(slotNumber), now);
        if (key != null) return key;
        ItemRef? item = Data.Slots[slotNumber - 1].Item;
        return item == null ? null : Keys.KeyFor(NameCommand(item), now);
    }

    public static string NameCommand(ItemRef item) => "/rdef use " + item.FullName;

    /// <summary>Binds a key to the slot ("/rdef use 3") or, with <paramref name="byName"/>, to its item's name ("/rdef use Stamina Kit"). Returns the message.</summary>
    public string BindKey(int slotNumber, string combo, bool byName)
    {
        combo = (combo ?? string.Empty).Trim();
        if (combo.Length == 0) return "Type the key first, e.g. F9 or Ctrl+1.";
        if (combo.IndexOf(' ') >= 0) return "A key has no spaces, e.g. Ctrl+F9.";
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        ItemRef? item = Data.Slots[slotNumber - 1].Item;
        if (byName && Data.Slots[slotNumber - 1].IsSpell) return $"Slot {slotNumber} holds a spell: bind the slot itself (/rdef bind {slotNumber} <key>).";
        if (byName && item == null) return $"Slot {slotNumber} is empty: nothing to bind by name.";

        string command = byName ? NameCommand(item!) : DefCommandParser.UseCommand(slotNumber);
        UnbindQuiet(slotNumber);
        if (!RunChat($"/rc hotkey add {combo} {command}"))
            return $"This engine can't bind keys from here. Type: /rc hotkey add {combo} {command}";
        Keys.Invalidate();
        return $"Binding {combo} to {command} (the engine confirms in chat; Settings > Hotkeys lists it).";
    }

    public string UnbindKey(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        string? key = UnbindQuiet(slotNumber);
        Keys.Invalidate();
        return key == null ? $"Slot {slotNumber} has no key." : $"Key {key} removed from slot {slotNumber}.";
    }

    /// <summary>Removes the slot's current key (either way of binding); returns it.</summary>
    private string? UnbindQuiet(int slotNumber)
    {
        string? key = KeyOfSlot(slotNumber);
        if (key == null) return null;
        string combo = key.EndsWith(" (off)", StringComparison.Ordinal) ? key[..^6] : key;
        RunChat($"/rc hotkey remove {combo}");
        return combo;
    }

    // ── Independent bar ─────────────────────────────────────────────────────

    /// <summary>
    /// How many items in AC's slots the settings window offers to copy into the bar's own slots, after
    /// the bar was made independent (0 = no offer). Nothing is copied until the player says so.
    /// </summary>
    public int CopyOffer;

    /// <summary>Turns the independent bar on or off (DefSettings.IndependentBar). Returns what to tell the player.</summary>
    public string SetIndependent(bool on)
    {
        CopyOffer = 0;
        if (Settings.IndependentBar != on)
        {
            Settings.IndependentBar = on;
            Save();
        }
        if (!on)
            return "The Shortcuts bar shows the game's 18 slots first again; RynthDefective's own slots follow them from slot 19 (its slot 1 there).";
        var ids = new uint[DefSettings.RetailSlots];
        var spells = new uint[DefSettings.RetailSlots];
        int items = ReadRetail(ids, spells) ? BarMapping.RetailShortcuts(ids, spells) : 0;
        CopyOffer = items;
        const string what = "The Shortcuts bar is independent of the game's toolbar now: every slot is RynthDefective's own "
                            + "(clicks one-touch, keys RynthCore hotkeys). The game's toolbar and its keys are unchanged.";
        return items == 0 ? what
            : what + $" The game's 18 slots hold {items} shortcut{(items == 1 ? "" : "s")} (items and spells): Defective Settings offers to copy them in.";
    }

    /// <summary>The player said no to the copy.</summary>
    public void DeclineCopy() => CopyOffer = 0;

    /// <summary>
    /// The copy the settings window offers: AC's 18 slots into the own slots 1-18, the own slots that
    /// followed them moved to 19 and on with their keys (BarMapping.CopyRetail), so the independent bar
    /// looks as the usual one did. Returns what to tell the player.
    /// </summary>
    public string CopyRetailSlots()
    {
        CopyOffer = 0;
        if (!Settings.IndependentBar) return "The bar uses the game's slots itself: make it independent first (Defective Settings).";
        var ids = new uint[DefSettings.RetailSlots];
        var spells = new uint[DefSettings.RetailSlots];
        if (!ReadRetail(ids, spells)) return "The game's shortcut slots can't be read right now (in the world, on RynthCore with the Shortcuts bar).";

        // The moving slots' keys, read before anything is bound (a bind can land at once).
        var keysOf = new Dictionary<int, string[]>();
        for (int n = 1; n <= DefSettings.SlotsMax - DefSettings.RetailSlots; n++)
            if (!Data.Slots[n - 1].Empty)
                keysOf[n] = BarMapping.SplitKeys(CommandKeys(DefCommandParser.UseCommand(n)));

        BarCopyResult r = BarMapping.CopyRetail(Data.Slots, ids, spells, ResolveRetail);
        if (r.Refused != null) return r.Refused;
        Save();
        int keysMoved = 0, keysFailed = 0;
        foreach ((int from, int to) in r.Moved)
            if (keysOf.TryGetValue(from, out string[]? keys))
                foreach (string key in keys)
                    if (BindOwnKey(to, key)) keysMoved++; else keysFailed++;
        Keys.Invalidate();

        string msg = r.Copied == 0 ? "None of the game's slots held an item or a spell to copy." : $"Copied {r.Copied} of the game's slots into this bar (slots 1-18).";
        if (r.Skipped > 0) msg += $" {r.Skipped} couldn't be (an item not in your pack whose name the game doesn't know).";
        if (r.Moved.Count > 0)
            msg += $" Your own {r.Moved.Count} slot{(r.Moved.Count == 1 ? "" : "s")} moved after them, to 19 and on"
                   + (keysMoved > 0 ? $", {keysMoved} key{(keysMoved == 1 ? "" : "s")} with them" : "") + ".";
        if (keysFailed > 0) msg += $" {keysFailed} key{(keysFailed == 1 ? "" : "s")} couldn't be moved: bind {(keysFailed == 1 ? "it" : "them")} again (right-click a slot).";
        if (r.Copied > 0) msg += " The game's keys still use the game's toolbar: give these slots keys with right-click > Add a key.";
        return msg;
    }

    /// <summary>An item in one of AC's slots as a slot reference: from the pack read, else by the name the client knows.</summary>
    private ItemRef? ResolveRetail(uint id)
    {
        CarriedItem? it = FindById(id);
        if (it != null) return ItemRef.From(it);
        string? name = ObjectName(id);
        return string.IsNullOrWhiteSpace(name) ? null : new ItemRef { Id = id, Name = name.Trim() };
    }

    // ── Favourites ──────────────────────────────────────────────────────────

    public string ToggleFavourite(ItemRef r)
    {
        bool now = Data.ToggleFavourite(r);
        Save();
        return now ? $"{r.FullName} is a favourite." : $"{r.FullName} is no longer a favourite.";
    }

    /// <summary>/rdef fav: the item selected in the game.</summary>
    public string ToggleSelectedFavourite()
    {
        uint sel = SelectedId();
        if (sel == 0) return "Select a weapon in the game first.";
        CarriedItem? it = FindById(sel);
        if (it == null) return "The selected object isn't in your pack.";
        return ToggleFavourite(ItemRef.From(it));
    }
}
