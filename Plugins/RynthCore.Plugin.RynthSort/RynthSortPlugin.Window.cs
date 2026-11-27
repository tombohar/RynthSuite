// ============================================================================
//  RynthSort - RynthSortPlugin.Window.cs
//  The window drawn with ImGui (2026-11-26, the approved design for #rynth-issues
//  "The bag sort feature is lacking in UX"): RynthCore hosts a frameless window
//  (sections manifest, plugin API v90, "chrome": "none", "scale": "draw") and
//  RynthSort draws it (Window/SortWindow.cs) through RynthPluginRenderEmbed, as
//  RynthDefective draws its shortcut bar. On an engine without v90's frameless
//  windows and item pictures the script-window panel (Views/SortPanel.cs,
//  Ui/UiWindow.cs) stays.
//
//  The tick builds the window's view-model (Screen/ScreenBuilder.cs) while the
//  window shows (it was drawn in the last 2 s), twice a second and at once after
//  anything the window or a command changed, and publishes it whole; the render
//  thread reads it and queues every click for the tick (WindowActions).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Globalization;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Views;
using RynthCore.Plugin.RynthSort.Window;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthSort;

public sealed partial class RynthSortPlugin
{
    internal const string WindowSurface = "window:rynthsort";
    private const long ScreenBuildMs = 500, ScreenShownMs = 2000, OpenWhenHostedMs = 8000, WindowSyncMs = 1000;

    /// <summary>
    /// The engine hosts frameless plugin windows and hands out item pictures and its look (plugin API
    /// v90): the window drawn with ImGui. Else the script-window panel. Decided once, at Initialize.
    /// </summary>
    private bool _direct;

    private SortWindow? _sortWindow;                                   // render thread
    private int _drawFailures;                                          // render thread
    private volatile ScreenModel _screen = ScreenModel.Empty;           // tick writes, render reads
    private readonly ConcurrentQueue<Action> _windowActions = new();
    private readonly ScreenSession _session = new();
    private long _screenDrawnMs = long.MinValue / 2;                    // render writes (Volatile), tick reads
    private long _nextScreenMs;
    private bool _screenDirty = true;
    private volatile bool _wantLayouts;
    private long _openUntilMs, _nextSyncMs;

    internal bool UsesDirectWindow(RynthCoreHost host) =>
        host.HasFramelessWindows && host.HasShortcuts && host.HasSetPanelOpen && host.HasUiTheme;

    internal static readonly string Manifest =
        "{\"version\":1,\"windows\":[{\"id\":\"sort\",\"title\":\"" + SortWindow.Title + "\",\"surface\":\"" + WindowSurface +
        "\",\"icon\":\"\\u" + ((int)RynthIcons.SortAscending[0]).ToString("X4", CultureInfo.InvariantCulture) +
        "\",\"width\":1060,\"height\":660,\"minWidth\":340,\"minHeight\":240,\"bar\":false,\"edgeToEdge\":true," +
        "\"chrome\":\"none\",\"scale\":\"draw\"}]}";

    /// <summary>The sections manifest: the window, on an engine that can host it. Any thread.</summary>
    internal string SectionsJson() => _direct ? Manifest : "{\"version\":1}";

    /// <summary>RynthPluginRenderEmbed "window:rynthsort". 1 drawn, 0 not ready, -2 failed (three in a row). Render thread.</summary>
    internal int RenderWindow(bool popped, out float wantHeight)
    {
        wantHeight = 0;
        if (!_direct) return 0;
        System.Threading.Volatile.Write(ref _screenDrawnMs, Environment.TickCount64);
        try
        {
            _sortWindow ??= new SortWindow(Host, new WindowActions(this));
            wantHeight = _sortWindow.Draw(_screen, popped);
            _drawFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthSort] window draw failed: {ex.GetType().Name}: {ex.Message}");
            return ++_drawFailures >= 3 ? -2 : 0;
        }
    }

    /// <summary>Plugin tick: the window's clicks, then its view while it shows.</summary>
    private void TickWindow(long now)
    {
        while (_windowActions.TryDequeue(out Action? act))
        {
            try { act(); }
            catch (Exception ex) { Host.Log($"[RynthSort] window action failed: {ex.GetType().Name}: {ex.Message}"); }
            _screenDirty = true;
        }
        if (!_direct) return;
        _session.Expire(now);
        OpenWhenHosted(now);
        SyncWindowOpen(now);
        bool shown = now - System.Threading.Volatile.Read(ref _screenDrawnMs) < ScreenShownMs;
        if (!shown || (!_screenDirty && now < _nextScreenMs)) return;
        _screenDirty = false;
        _nextScreenMs = now + ScreenBuildMs;
        try
        {
            _screen = ScreenBuilder.Build(_sort, new ScreenInputs
            {
                Character = _identityKnown ? _character : string.Empty,
                Server = _identityKnown ? _server : string.Empty,
                Warnings = _warnings,
                CanAddSelected = Host.HasGetSelectedItemId,
                CanSelect = Host.HasSelectItem,
                WantLayouts = _wantLayouts,
                Session = _session,
            });
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthSort] window view failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The window was open when this character was last seen: open it again once the engine hosts it.</summary>
    private void OpenWhenHosted(long now)
    {
        if (_openUntilMs == 0) return;
        int state = Host.SetPanelOpen(SortWindow.Title, RynthPanelOpen.Open);
        if (state >= 0 || now > _openUntilMs) _openUntilMs = 0;
    }

    /// <summary>Keeps the saved "window open" in step with the engine's window (closed with its x, opened from the Inventory).</summary>
    private void SyncWindowOpen(long now)
    {
        if (!_identityKnown || _openUntilMs != 0 || now < _nextSyncMs) return;
        _nextSyncMs = now + WindowSyncMs;
        int state = Host.SetPanelOpen(SortWindow.Title, RynthPanelOpen.Ask);
        if (state < 0) return;
        bool open = state == 1;
        if (open == _sort.Data.Settings.WindowOpen) return;
        _sort.Data.Settings.WindowOpen = open;
        RequestSave();
    }

    /// <summary>/rsort: shows or hides the window. Returns its state afterwards (-1: not hosted yet).</summary>
    private int ToggleWindow() => Host.SetPanelOpen(SortWindow.Title, RynthPanelOpen.Toggle);

    /// <summary>"+ Add selected" through the window's toast (and /rsort add): the selected item's name into side pack <paramref name="number"/>'s rules.</summary>
    private AddOutcome AddSelectedHere(int number)
    {
        AddOutcome outcome = new() { Status = AddStatus.Refused, Message = "Nothing was added." };
        _session.Edit(_sort.Data.Rules, rules =>
        {
            outcome = AddSelectedRules.Apply(rules, _sort.Planner.View, number, _world?.ReadSelected() ?? SelectedItem.Of(SelectionKind.Unavailable));
            return new EditResult(outcome.Status == AddStatus.Added, outcome.Message);
        }, Environment.TickCount64);
        if (outcome.Status == AddStatus.Added) RequestSave();
        _screenDirty = true;
        return outcome;
    }

    private void QueueWindow(Action action)
    {
        if (_windowActions.Count < 64) _windowActions.Enqueue(action);
    }

    /// <summary>What the window asks (render thread: queue only; everything runs on the tick).</summary>
    private sealed class WindowActions : ISortWindowActions
    {
        private readonly RynthSortPlugin _p;

        public WindowActions(RynthSortPlugin plugin) => _p = plugin;

        private SortController C => _p._sort;
        private static long Now => Environment.TickCount64;

        private void Q(Action a) => _p.QueueWindow(a);

        /// <summary>A rule change with its toast and Undo; saved when it changed something.</summary>
        private void Edit(Func<System.Collections.Generic.List<SortRule>, EditResult> change) => Q(() =>
        {
            if (!_p._identityKnown) return;
            if (_p._session.Edit(C.Data.Rules, change, Now)) _p.RequestSave();
        });

        public void SetAuto(bool on) => Q(() => { C.SetAuto(on); _p.RequestSave(); });
        public void SortNow() => Q(() => C.StartJob(SortScope.All));
        public void Clean() => Q(() => C.StartJob(SortScope.Clean));
        public void PutBack() => Q(() =>
        {
            int n = C.PutBackCount;
            if (C.StartPutBack()) _p._session.Show($"Putting back {SortPanel.Count(n, "item")}; auto-sort is off so nothing moves again.", false, Now);
        });

        public void SendKind(int pack, int cls) => Edit(rules => RuleEdits.SendKind(rules, C.Planner.View, pack, cls));

        public void AddOverflow(int pack, int cls) => Edit(rules => RuleEdits.AddOverflow(rules, C.Planner.View, pack, cls));
        public void SetJob(int pack, int kind, string mule) => Q(() =>
        {
            SortInventory? inv = C.LastInventory;
            if (!_p._identityKnown || inv == null) return;
            var k = (JobKind)kind;
            if (!PackJobs.Set(C.Data.Jobs, inv, pack, k, mule))
            {
                _p._session.Show("Nothing changed.", false, Now);
                return;
            }
            _p.RequestSave();
            string where = PackNames.Label(inv, pack);
            _p._session.Show(k switch
            {
                JobKind.Salvage => $"{where} now takes what RynthAi's loot profile marks Salvage. Nothing has moved yet.",
                JobKind.Sell => $"{where} now takes what RynthAi's AutoVendor would sell. Nothing has moved yet.",
                JobKind.Mule => $"{where} now holds what RynthAi's AutoTrade would give {(mule.Trim().Length > 0 ? mule.Trim() : "a mule")}. Nothing has moved yet.",
                _ => $"{where} has no job now: only its rules.",
            }, false, Now);
        });

        public void SendName(int pack, uint itemId) => Edit(rules =>
        {
            SortItem? item = Find(itemId);
            return item == null ? EditResult.Nothing("That item isn't in your packs now.") : RuleEdits.SendName(rules, C.Planner.View, pack, item);
        });

        public void MoveOnce(uint itemId, int pack) => Q(() =>
        {
            SortInventory? inv = C.LastInventory;
            SortItem? item = Find(itemId);
            if (inv == null || item == null) { _p._session.Show("That item isn't in your packs now.", false, Now); return; }
            uint dest = pack == 0 ? inv.Main.Id : pack <= inv.Packs.Count ? inv.Packs[pack - 1].Id : 0;
            if (!C.StartSend(itemId, dest)) { _p._session.Show("That pack isn't carried now.", false, Now); return; }
            string where = pack == 0 ? "the main pack" : inv.Packs[pack - 1].Name.Length > 0 ? inv.Packs[pack - 1].Name : "pack " + pack;
            int hold = C.Data.Settings.HoldMin;
            _p._session.Show(hold > 0
                ? $"{Name(item)} goes to {where} now and is left alone for {hold} min."
                : $"{Name(item)} goes to {where} now. Leaving items alone is off, so a sort may move it back.", false, Now);
        });

        public void Hold(uint itemId) => Q(() =>
        {
            C.Touch.Hold(itemId, C.Clock, TouchKind.Chosen);
            SortItem? item = Find(itemId);
            _p._session.Show($"{(item != null ? Name(item) : "It")} is left alone for {C.Data.Settings.HoldMin} min.", false, Now);
        });

        public void Release(uint itemId) => Q(() =>
        {
            C.Touch.Release(itemId);
            SortItem? item = Find(itemId);
            _p._session.Show($"RynthSort may move {(item != null ? Name(item) : "it")} again.", false, Now);
        });

        public void Unhold(uint itemId) => Q(() => C.Touch.Release(itemId));

        public void RemoveChip(int ruleUid, bool isName, int cls, string name) =>
            Edit(rules => RuleEdits.RemoveEntry(rules, C.Planner.View, ruleUid, isName, cls, name));

        public void TurnOn(int ruleUid) => Edit(rules => RuleEdits.TurnOn(rules, ruleUid));

        public void AddSelected(int pack) => Q(() => _p.AddSelectedHere(pack));

        public void UseLayout(string key) => Edit(rules =>
        {
            StarterLayout? l = StarterLayouts.Find(key);
            return l == null ? EditResult.Nothing("That layout is gone.") : RuleEdits.UseLayout(rules, C.LastInventory, l);
        });

        public void Undo() => Q(() => { if (_p._session.Undo(C.Data.Rules, Now)) _p.RequestSave(); });
        public void DismissToast() => Q(() => _p._session.Show(string.Empty, false, Now));

        public void SetSettle(int seconds) => Setting(s => s.SettleSec = seconds);
        public void SetHold(int minutes) => Setting(s => s.HoldMin = minutes);
        public void SetGap(int ms) => Setting(s => s.GapMs = ms);
        public void SetCombat(bool on) => Setting(s => s.SortInCombat = on);
        public void SetOrder(bool on) => Setting(s => s.OrderInPacks = on);
        public void SetKindFirst(bool on) => Setting(s => s.OrderKindFirst = on);
        public void SetHandy(bool on) => Setting(s => s.HandyFront = on);
        public void HandyKind(int cls, bool add) => Setting(s => { if (add) HandyItems.AddClass(s, cls); else s.HandyClasses.Remove(cls); });
        public void HandyName(string name, bool add) => Setting(s => { if (add) HandyItems.AddName(s, name); else HandyItems.RemoveName(s, name); });

        private void Setting(Action<SortSettings> set) => Q(() =>
        {
            if (!_p._identityKnown) return;
            set(C.Data.Settings);
            C.Data.Settings.Clamp();
            _p.RequestSave();
        });

        public void EmptySidePacks() => Q(() =>
        {
            C.StartUnpack(null);
            _p._session.Show("Emptying the side packs; auto-sort is off so nothing moves back.", false, Now);
        });

        public void SelectInGame(uint objectId) => Q(() =>
        {
            if (_p.Host.HasSelectItem) _p.Host.SelectItem(objectId);
        });

        public void WantLayouts(bool open) => Q(() => _p._wantLayouts = open);

        public void SendNameText(int pack, string text) => Edit(rules => RuleEdits.SendNameText(rules, C.Planner.View, pack, text));

        public void SetCollapsed(uint packId, bool collapsed) => Fold(s => s.SetCollapsed(packId, collapsed));

        public void SetMainCollapsed(bool collapsed) => Fold(s =>
        {
            if (s.MainCollapsed == collapsed) return false;
            s.MainCollapsed = collapsed;
            return true;
        });

        public void SetAllCollapsed(bool collapsed) => Fold(s =>
        {
            SortInventory? inv = C.LastInventory;
            if (inv == null) return false;
            bool changed = false;
            foreach (SortPack pk in inv.Packs) changed |= s.SetCollapsed(pk.Id, collapsed);
            return changed;
        });

        /// <summary>A card folded or opened: saved, and the window's model made again at once (no toast, no Undo).</summary>
        private void Fold(Func<SortSettings, bool> change) => Q(() =>
        {
            if (!_p._identityKnown || !change(C.Data.Settings)) return;
            _p.RequestSave();
            _p._screenDirty = true;
        });

        private SortItem? Find(uint id)
        {
            SortInventory? inv = C.LastInventory;
            if (inv == null || id == 0) return null;
            foreach (SortItem it in inv.Main.Items) if (it.Id == id) return it;
            foreach (SortPack p in inv.Packs)
                foreach (SortItem it in p.Items) if (it.Id == id) return it;
            return null;
        }

        private static string Name(SortItem it) => it.Name.Length > 0 ? it.Name : "the item";
    }
}
