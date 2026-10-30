// ============================================================================
//  RynthDefective - RynthDefectivePlugin.Bar.cs
//  The shortcut bar (2026-10-22), in place of AC's retail toolbar: RynthCore
//  hosts a frameless window (sections manifest, plugin API v90) and RynthDefective
//  draws it (ShortcutBar/ShortcutBarWindow.cs). Its first 18 slots are AC's own
//  (the retail toolbar's items and AC's keys, so both always agree), the rest
//  this plugin's slots. On an engine without v90's shortcuts the old script
//  window bar stays.
//
//  Independent bar (DefSettings.IndependentBar, 2026-10-27): every slot is this
//  plugin's own (ShortcutBar/BarMapping.cs): AC's 18 aren't shown or mapped.
//  A healing kit in one of AC's slots is used on yourself by this plugin when
//  the slot is clicked (DefSession.UseRetailSlot).
//
//  The tick: the window's clicks (queued from the render thread), the snapshot
//  it draws (4 times a second while it shows).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Globalization;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.ShortcutBar;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective;

public sealed partial class RynthDefectivePlugin : IBarActions
{
    internal const string BarSurface = "window:bar", PanelsSurface = "window:panels";

    private BarSnapshotBuilder? _barBuilder;
    private ShortcutBarWindow? _shortcutBar;                             // render thread
    private PanelButtonsWindow? _panelButtons;                           // render thread
    private int _panelsFailures;
    private volatile BarSnapshot _barView = BarSnapshot.NotReady;        // tick writes, render reads
    private readonly ConcurrentQueue<Action> _barActions = new();
    private long _barDrawnMs = long.MinValue / 2, _nextBarBuildMs;
    private int _barFailures;

    /// <summary>The engine has v90's shortcuts and frameless windows: the new bar, not the script window one.</summary>
    internal bool UsesShortcutBar => Host.HasShortcuts && Host.HasFramelessWindows;

    /// <summary>AC's panels can be opened from here: the engine has v90's AcPanel (the buttons in the corner).</summary>
    internal bool UsesPanelButtons => UsesShortcutBar && Host.HasAcPanels;

    /// <summary>The sections manifest: the bar's window and the panel buttons' (only on an engine that has what they need).</summary>
    internal string SectionsJson()
    {
        if (!UsesShortcutBar) return "{\"version\":1}";
        string bar = "{\"id\":\"bar\",\"title\":\"" + ShortcutBarWindow.Title + "\",\"surface\":\"" + BarSurface +
              "\",\"icon\":\"\\u" + ((int)RynthIcons.Lightning[0]).ToString("X4", CultureInfo.InvariantCulture) +
              "\",\"width\":420,\"height\":120,\"minWidth\":90,\"minHeight\":40,\"bar\":true,\"edgeToEdge\":true," +
              "\"chrome\":\"none\",\"fitHeight\":true,\"scale\":\"draw\"}";
        // Nine 22-unit buttons and the grip in a row, first in the bottom-right corner.
        string panels = !UsesPanelButtons ? "" :
            ",{\"id\":\"panels\",\"title\":\"" + PanelButtonsWindow.Title + "\",\"surface\":\"" + PanelsSurface +
            "\",\"icon\":\"\\u" + ((int)RynthIcons.SquaresFour[0]).ToString("X4", CultureInfo.InvariantCulture) +
            "\",\"width\":236,\"height\":30,\"minWidth\":30,\"minHeight\":20,\"bar\":false,\"edgeToEdge\":true," +
            "\"chrome\":\"none\",\"fitHeight\":true,\"corner\":\"bottomRight\",\"scale\":\"draw\"}";
        // What is selected and its health, first at the top centre.
        string selection = ",{\"id\":\"selection\",\"title\":\"" + SelectionWindow.Title + "\",\"surface\":\"" + SelectionSurface +
            "\",\"icon\":\"\\u" + ((int)RynthIcons.Crosshair[0]).ToString("X4", CultureInfo.InvariantCulture) +
            "\",\"width\":280,\"height\":46,\"minWidth\":120,\"minHeight\":24,\"bar\":false,\"edgeToEdge\":true," +
            "\"chrome\":\"none\",\"fitHeight\":true,\"corner\":\"top\",\"scale\":\"draw\"}";
        // Where your corpses are (a bar button of its own).
        string corpses = ",{\"id\":\"corpses\",\"title\":\"" + Corpses.CorpsesWindow.Title + "\",\"surface\":\"" + CorpsesSurface +
            "\",\"icon\":\"\\u" + ((int)RynthIcons.Skull[0]).ToString("X4", CultureInfo.InvariantCulture) +
            "\",\"width\":290,\"height\":80,\"minWidth\":140,\"minHeight\":30,\"bar\":true,\"edgeToEdge\":true," +
            "\"chrome\":\"none\",\"fitHeight\":true,\"scale\":\"draw\"}";
        return "{\"version\":1,\"windows\":[" + bar + panels + selection + corpses + "]}";
    }

    /// <summary>RynthPluginRenderEmbed "window:panels". 1 drawn, 0 not ready, -2 failed (three in a row). Render thread.</summary>
    internal int RenderPanels(out float wantHeight)
    {
        wantHeight = 0;
        if (!UsesPanelButtons) return 0;
        try
        {
            _panelButtons ??= new PanelButtonsWindow(Host);
            wantHeight = _panelButtons.Draw();
            _panelsFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthDefective] panel buttons draw failed: {ex.GetType().Name}: {ex.Message}");
            return ++_panelsFailures >= 3 ? -2 : 0;
        }
    }

    /// <summary>RynthPluginRenderEmbed "window:bar". 1 drawn, 0 not ready, -2 failed (three in a row). Render thread.</summary>
    internal int RenderBar(bool popped, out float wantHeight)
    {
        wantHeight = 0;
        if (!UsesShortcutBar) return 0;
        System.Threading.Volatile.Write(ref _barDrawnMs, Environment.TickCount64);
        try
        {
            _shortcutBar ??= new ShortcutBarWindow(Host, this);
            wantHeight = _shortcutBar.Draw(_barView, popped);
            _barFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthDefective] shortcut bar draw failed: {ex.GetType().Name}: {ex.Message}");
            return ++_barFailures >= 3 ? -2 : 0;
        }
    }

    /// <summary>Plugin tick: the bar's clicks, then its snapshot while it shows.</summary>
    private void TickBar(long now)
    {
        while (_barActions.TryDequeue(out Action? act))
        {
            try { act(); }
            catch (Exception ex) { Host.Log($"[RynthDefective] bar action failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (!UsesShortcutBar || !_identityKnown) return;
        bool shown = now - System.Threading.Volatile.Read(ref _barDrawnMs) < 2000;
        if (!shown || now < _nextBarBuildMs) return;
        _nextBarBuildMs = now + 250;
        _barBuilder ??= new BarSnapshotBuilder(Host);
        _barView = _barBuilder.Build(_session, _session.Items(), now);
    }

    private void QueueBar(Action action)
    {
        if (_barActions.Count < 32) _barActions.Enqueue(action);
    }

    /// <summary>Bar slot <paramref name="number"/> is one of AC's 18 (never on an independent bar).</summary>
    private bool IsRetail(int number) => BarMapping.IsRetail(number, _session.Settings.IndependentBar);

    /// <summary>RynthDefective's own slot bar slot <paramref name="number"/> is (BarMapping).</summary>
    private int OwnOf(int number) => BarMapping.OwnSlot(number, _session.Settings.IndependentBar);

    private readonly uint[] _retailIds = new uint[DefSettings.RetailSlots], _retailSpells = new uint[DefSettings.RetailSlots];

    /// <summary>The object in AC's slot <paramref name="number"/> (1-18) now; 0 = empty, a spell, or out of the world.</summary>
    private uint RetailObject(int number) =>
        Host.GetShortcuts(_retailIds, _retailSpells) >= number && number >= 1 ? _retailIds[number - 1] : 0;

    /// <summary>AC's 18 slots for the session (the independent bar's copy).</summary>
    private bool ReadRetail(uint[] ids, uint[] spells) => Host.GetShortcuts(ids, spells) >= DefSettings.RetailSlots;

    // ── Keys of RynthDefective's own slots: the one seam ─────────────────────
    //
    // Every key bound to or taken off one of RynthDefective's own slots from the bar goes through
    // these three: the bar's "Add a key" / "Remove" (usual bar slots past 18, every slot of an
    // independent bar) and the keys the independent bar's copy moves with their slots. On the usual
    // bar they are BindActionKey's command keys: the key also comes off AC's own actions in AC's
    // keymap (so no key shows on two slots), which rewrites the keymap file and every client follows.
    // An independent bar's keys are engine hotkeys only (BindCommandKeyOnly, plugin API v92): AC's
    // keymap is never touched and no client changes its keys; the engine's hotkey takes the key
    // before AC sees it. (/rdef bind and /rdef unbind type "/rc hotkey add|remove", unchanged.)

    /// <summary>What binding <paramref name="key"/> to own slot <paramref name="own"/> would take from others.
    /// On an independent bar AC's actions keep the key in AC's keymap, but the hotkey still takes it
    /// first, so naming them is still the right warning.</summary>
    private RynthKeyCheck CheckOwnSlotKey(int own, string key, out string message) =>
        Host.CheckActionKey(DefCommandParser.UseCommand(own), key, out message);

    /// <summary>Gives own slot <paramref name="own"/> ("/rdef use N") the key <paramref name="key"/>; it comes off whatever had it.</summary>
    private bool BindOwnSlotKey(int own, string key, out string message) =>
        _session.Settings.IndependentBar
            ? Host.BindCommandKeyOnly(DefCommandParser.UseCommand(own), key, out message)
            : Host.BindActionKey(DefCommandParser.UseCommand(own), key, out message);

    /// <summary>Takes <paramref name="key"/> off own slot <paramref name="own"/>.</summary>
    private bool UnbindOwnSlotKey(int own, string key, out string message) =>
        _session.Settings.IndependentBar
            ? Host.UnbindCommandKeyOnly(DefCommandParser.UseCommand(own), key, out message)
            : Host.UnbindActionKey(DefCommandParser.UseCommand(own), key, out message);

    RynthKeyCheck IBarActions.CheckKey(BarSlot slot, string key, out string message) =>
        slot.Retail ? Host.CheckActionKey("UseQuickSlot_" + slot.Number, key, out message) : CheckOwnSlotKey(slot.Own, key, out message);

    bool IBarActions.BindKey(BarSlot slot, string key, out string message) =>
        slot.Retail ? Host.BindActionKey("UseQuickSlot_" + slot.Number, key, out message) : BindOwnSlotKey(slot.Own, key, out message);

    bool IBarActions.UnbindKey(BarSlot slot, string key, out string message) =>
        slot.Retail ? Host.UnbindActionKey("UseQuickSlot_" + slot.Number, key, out message) : UnbindOwnSlotKey(slot.Own, key, out message);

    // ── IBarActions (render thread: queue only) ─────────────────────────────

    public void Use(int number) => QueueBar(() =>
    {
        if (IsRetail(number))
        {
            // A healing kit in AC's slot: used on yourself here (the use controller, one touch, nothing
            // selected), not handed to AC. Everything else is AC's own toolbar use. This is the bar's
            // click only: the slot's key (AC's UseQuickSlot_N) is read by AC itself and still goes
            // straight to AC, unchanged.
            if (_session.UseRetailSlot(number, RetailObject(number))) return;
            if (!Host.ShortcutAction(RynthShortcutOp.Use, number - 1)) _session.SetStatus($"Slot {number} can't be used now.", warning: true);
            return;
        }
        string? problem = _session.UseSlot(OwnOf(number));
        if (problem != null) { _session.SetStatus(problem, warning: true); Print(problem); }
    });

    public void SetFromSelected(int number) => QueueBar(() =>
    {
        if (IsRetail(number))
        {
            uint sel = Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0;
            if (sel == 0) { Print("Select the item in the game first, then put it in the slot."); return; }
            if (!Host.ShortcutAction(RynthShortcutOp.Set, number - 1, sel)) Print($"Slot {number} can't be changed now.");
            _nextBarBuildMs = 0;
            return;
        }
        Print(_session.SetFromSelected(OwnOf(number)));
        _nextBarBuildMs = 0;
    });

    public void Clear(int number) => QueueBar(() =>
    {
        if (IsRetail(number))
        {
            if (!Host.ShortcutAction(RynthShortcutOp.Clear, number - 1)) Print($"Slot {number} can't be changed now.");
            _nextBarBuildMs = 0;
            return;
        }
        Print(_session.ClearSlot(OwnOf(number)));
        _nextBarBuildMs = 0;
    });

    public void DropItem(int number, uint objectId) => QueueBar(() =>
    {
        if (IsRetail(number))
        {
            // The retail toolbar's own "make a shortcut": AC checks the item (and says why not).
            if (!Host.ShortcutAction(RynthShortcutOp.Set, number - 1, objectId)) Print($"Slot {number} can't be changed now.");
        }
        else Print(_session.SetFromId(OwnOf(number), objectId));
        _nextBarBuildMs = 0;
    });

    public void SetSpell(int number, uint spellId) => QueueBar(() =>
    {
        // The game's own slots take spells from the game's spellbook (drag one onto the retail toolbar).
        if (IsRetail(number)) { Print($"Slot {number} is the game's own: drag the spell onto it from the game's spellbook."); return; }
        Print(_session.SetSpell(OwnOf(number), spellId));
        _nextBarBuildMs = 0;
    });

    public void ChangeSlotCount(int delta) => QueueBar(() =>
    {
        DefSettings st = _session.Settings;
        st.BarSlots = Math.Clamp(st.BarSlots + delta, DefSettings.BarSlotsMin, DefSettings.BarSlotsMax);
        RequestSave();
        _nextBarBuildMs = 0;
    });

    public void KeysChanged() => QueueBar(() =>
    {
        _barBuilder?.InvalidateKeys();
        _session.Keys.Invalidate();
        _nextBarBuildMs = 0;
    });

    public void ShowWeapons() => QueueBar(() => ShowPicker(true));

    public void ShowSettings() => QueueBar(() => ShowSettingsWindow(true));

    public void Hide() => QueueBar(() => Host.SetPanelOpen(ShortcutBarWindow.Title, RynthPanelOpen.Close));
}
