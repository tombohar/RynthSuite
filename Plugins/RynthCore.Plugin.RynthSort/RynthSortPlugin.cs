using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.RynthSort.Views;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthSort;

/// <summary>
/// RynthSort: keeps your packs sorted while you play or RynthAi hunts. Auto-sort keeps every pack
/// tidy: new items that land in the main pack move to the side pack their rule names once they have
/// sat there for the settle time, items in the wrong side pack move to theirs, stacks of one kind
/// are joined and (with the engine's slot orders, API v80) items are put in order, A-Z, inside
/// each pack. Clean also sends items no rule wants back to the main pack; Unpack empties the side
/// packs. A rule lists classes and parts of names and the pack they go to; a name match beats a
/// class match. Rules and settings are kept per character; the RynthSort Lua script's saved rules
/// are taken over on a character's first run. A character it hasn't seen starts with auto-sort off
/// and no rules; the window offers starter layouts fitted to its packs (Sorting/StarterLayouts.cs),
/// previews what a sort would move, and Put back undoes the last sort. Items the player is using
/// (moved, selected, dragged, taken off, picked up) are left alone for a few minutes
/// (Sorting/TouchWatch.cs; the selection is polled each tick, drags come from the v77 UI hooks).
///
/// The window (0.5.0, 2026-11-26): drawn by RynthSort with ImGui in RynthCore's frameless plugin
/// window (RynthSortPlugin.Window.cs, Window/SortWindow.cs) on engines with plugin API v90; the
/// script-window panel (Views/SortPanel.cs) on older ones.
///
/// It stays out of the way (Sorting/SortGate.cs): nothing moves while a corpse or chest is being
/// looted, a vendor or trade window is open, the character is busy or animating, in portal space,
/// or while RynthAi is looting, salvaging, buffing, making ammo, waiting on the server or (unless
/// "Sort during fights" is on) fighting. One move at a time, each waited on, then a gap.
///
/// Everything runs on the plugin pump (OnTick and the events the engine drains into it) except
/// OnChatBarEnter, which the engine may call on AC's thread: it only queues the command.
/// </summary>
public sealed partial class RynthSortPlugin : RynthPluginBase
{
    public const string Version = "0.6.1";
    private const long IdentityRetryMs = 1000;
    private const long SaveDelayMs = 750;
    private const long PassIntervalMs = 500;

    private readonly SortStore _store = new();
    private readonly InventoryReader _reader = new();
    private readonly LootWatch _loot = new();
    private readonly SortController _sort = new();
    private readonly UiHost _ui = new();
    private readonly ConcurrentQueue<string> _commands = new();
    private HostSortWorld? _world;
    private UiWindow? _window;
    private SortPanel? _panel;

    private bool _inWorld;
    private bool _identityKnown;
    private long _nextIdentityTryMs;
    private string _server = string.Empty;
    private string _character = string.Empty;
    private long _saveDueMs;
    private bool _vendorOpen;
    private bool _backupOnSave;
    private readonly List<string> _warnings = new();
    private volatile int _autoState = -1;
    private volatile bool _moving;
    private int _movedSeen;
    private long _lastMoveMs;

    /// <summary>
    /// How long after a move lands RynthSort still counts as moving, so a run of moves (a gap of
    /// up to a second or two between them) reads as one on RynthAi's dashboard key.
    /// </summary>
    private const long MovingHoldMs = 1500;

    /// <summary>For the engine's dashboard toggle (RynthSortAutoState): -1 not logged in yet, 0 auto-sort off, 1 on. Any thread.</summary>
    public int AutoState => _autoState;

    /// <summary>
    /// RynthSort is moving items: a move is out, or one landed in the last <see cref="MovingHoldMs"/>.
    /// RynthAi's auto-sort key plays its sorting look while this is true. Any thread.
    /// </summary>
    public bool Moving => _moving;

    /// <summary>
    /// A command from another plugin (RynthPluginApplyRemoteCommand): the /rsort argument when
    /// <paramref name="action"/> is "rsort" (value "now", "status"...), else null (not ours).
    /// </summary>
    internal static string? RemoteSortArgument(string? action, string? value)
        => string.Equals(action, "rsort", StringComparison.OrdinalIgnoreCase) ? value ?? string.Empty : null;

    /// <summary>An /rsort command from the engine (RynthSortCommand), without "/rsort". Any thread; runs on the tick.</summary>
    public void EnqueueCommand(string? arg)
    {
        if (arg != null) _commands.Enqueue(arg.Trim());
    }

    public override int Initialize()
    {
        _reply = new CommandReply("[RynthSort]", (t, c) => Host.WriteToChat(t, c), t => Host.Log(t));
        _ui.Log = msg => Host.Log(msg);
        _world = new HostSortWorld(Host, _reader, _loot, () => _vendorOpen);
        _sort.SettingsChanged = RequestSave;
        _sort.PacksFixed = OnPacksFixed;
        _sort.Warn = text => _reply.Fail(text);   // what didn't fit, once per sort
        _direct = UsesDirectWindow(Host);
        if (_direct)
        {
            // The window drawn with ImGui.NET bound to the engine's cimgui, in the engine's look (as RynthDefective's bar).
            RynthCore.Plugin.Shared.ImGuiNativeBinding.Ensure(msg => Host.Log("[RynthSort] " + msg));
            RynthCore.Plugin.Shared.RynthUi.RynthUi.Init(Host);
        }
        _panel = new SortPanel(_sort, RequestSave)
        {
            Warnings = () => _warnings,
            Character = () => _identityKnown ? _character : string.Empty,
            Icons = IconsOn,
            OpLevel = OpLevelNow,
            CanSelect = () => Host.HasSelectItem,
            SelectInGame = id => Host.HasSelectItem && Host.SelectItem(id),
            ReadSelected = () => _world?.ReadSelected() ?? SelectedItem.Of(SelectionKind.Unavailable),
        };
        // No bar button of its own: opened from the engine Inventory window's Sort button, the
        // Scripts menu or /rsort. Only on an engine without frameless plugin windows: the window
        // drawn with ImGui takes the "RynthSort" title, which a script window would hold.
        if (!_direct)
            _window = _ui.Add(new UiWindow("RynthSort/Main", "RynthSort")
            {
                // Room for the status lines, the four page buttons and a pack's buttons on one line each.
                DefaultWidth = 440f, DefaultHeight = 560f, MinWidth = 330f, MinHeight = 260f,
                BarButton = false,
            });
        Host.Log($"[RynthSort] {Version} initialized ({(_direct ? "its own window" : "script window")}). /rsort shows or hides the window; /rsort on|off|now|tidy|clean|unpack|back|add. Files: {_store.Root}");
        return 0;
    }

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _inWorld = true;
        _identityKnown = false;
        _nextIdentityTryMs = Environment.TickCount64;
        _sort.Reset();
        _reader.Reset();
        _loot.Reset();
        _warnings.Clear();
        _session.Reset();
        _screenDirty = true;
    }

    public override void OnLogout()
    {
        SaveNow();
        _inWorld = false;
        _identityKnown = false;
        _sort.Reset();
        _reader.Reset();
        _loot.Reset();
        _vendorOpen = false;
        if (_window != null) { _window.Visible = false; _window.NeedsPass = true; }
        _ui.SubmitEmpty(Host);
    }

    public override void Shutdown()
    {
        SaveNow();
        _ui.SubmitEmpty(Host);
    }

    public override void OnVendorOpen(uint vendorId) => _vendorOpen = true;
    public override void OnVendorClose(uint vendorId) => _vendorOpen = false;

    // The player's own hands on an item (API v77 drag and drop, on the pump): left alone for a while.
    public override void OnDragStart(uint objectId, uint spellId, uint iconId)
    {
        if (objectId != 0 && _identityKnown) _sort.Touch.NoteDrag(objectId, Environment.TickCount64);
    }

    public override void OnItemDropped(uint objectId, uint spellId, uint targetElementId)
    {
        if (objectId != 0 && _identityKnown) _sort.Touch.NoteDrop(objectId, Environment.TickCount64);
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    // May run on AC's thread: classify and queue only.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (!t.StartsWith("/rsort", StringComparison.OrdinalIgnoreCase) || (t.Length > 6 && !char.IsWhiteSpace(t[6])))
            return;
        eat = 1;
        _commands.Enqueue(t.Length > 6 ? t.Substring(6).Trim() : string.Empty);
    }

    private void RunCommand(string arg)
    {
        // Every /rsort answers in chat (2026-10-28, SortCommand.cs; Plugins/Shared/CommandReply.cs). RynthSort
        // can't tell a typed /rsort from a meta's or RynthAi's /ub packsort, so each one does; only the
        // dashboard's "on quiet" / "off quiet" (it shows the state itself) is logged instead.
        SortCommand c = SortCommand.Parse(arg);
        if (c.NeedsLogin && !_identityKnown)
        {
            PrintFail("Log in first.");
            return;
        }
        switch (c.Verb)
        {
            case SortVerb.Window:
                if (_direct)
                {
                    int state = ToggleWindow();
                    if (state >= 0) Print(SortCommand.Window(state == 1));
                    else PrintFail("The RynthSort window isn't ready yet; try again in a moment.");
                }
                else if (_window != null)
                {
                    _window.Visible = !_window.Visible; _window.NeedsPass = true;
                    if (Host.HasUi) Print(SortCommand.Window(_window.Visible));
                    else PrintFail(SortCommand.NoWindows);
                }
                break;
            case SortVerb.On:
            case SortVerb.Off:
                _sort.SetAuto(c.Verb == SortVerb.On);
                _reply.Ok("Auto-sort " + (c.Verb == SortVerb.On ? "on" : "off"), quiet: c.Quiet);
                break;
            case SortVerb.Now:
                _sort.StartJob(SortScope.All);
                Print(SortCommand.Started(c.Verb));
                break;
            case SortVerb.Clean:
                _sort.StartJob(SortScope.Clean);
                Print(SortCommand.Started(c.Verb));
                break;
            case SortVerb.Unpack:
                _sort.StartUnpack(c.Pack);
                Print(SortCommand.Unpacking(c.Pack));
                break;
            case SortVerb.Status:
                PrintStatus();
                break;
            case SortVerb.Back:
            {
                int count = _sort.PutBackCount;
                if (_sort.StartPutBack()) Print(SortCommand.PuttingBack(count));
                else PrintFail(SortCommand.PuttingBack(0));
                break;
            }
            case SortVerb.Add:
                // The selected item's name into a pack's rules: the window's "+ Add selected".
                if (c.Pack == null) PrintFail(SortCommand.AddUsage);
                else if (_direct)
                {
                    AddOutcome added = AddSelectedHere(c.Pack.Value);
                    if (added.Status == AddStatus.Refused) PrintFail(added.Message);
                    else Print(added.Message);
                }
                else if (_panel == null) PrintFail("The RynthSort window isn't ready yet.");
                else
                {
                    AddOutcome added = _panel.AddSelected(c.Pack.Value);
                    if (added.Status == AddStatus.Refused) PrintFail(added.Message);
                    else Print(added.Message);
                }
                break;
            case SortVerb.Help:
                Print(SortCommand.Usage);
                break;
            default:
                PrintFail(SortCommand.Unknown(c.Word));
                break;
        }
        if (_window != null) _window.NeedsPass = true;
        _screenDirty = true;
    }

    private void PrintStatus()
    {
        SortView v = _sort.Planner.View;
        string held = v.Held.Count > 0 ? $", {v.Held.Count} left alone (you used them)" : string.Empty;
        Print($"{_character}: auto-sort {(_sort.Data.Settings.Auto ? "on" : "off")}, {_sort.Status}; {_sort.Data.Rules.Count} rules, "
              + $"{v.Packs.Count} side packs, main pack {v.MainUsed}/{v.MainCapacity}{held}; moved {_sort.Moved}, failed {_sort.Failed} this session.");
    }

    // Chat lines wait in the shared reply queue (the host drops a line within 100 ms of another
    // plugin's); the tick writes them.
    private CommandReply _reply = null!;

    private void Print(string text) => _reply.Ok(text);

    private void PrintFail(string text) => _reply.Fail(text);

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd);
        _reply.Flush();
        _ui.Poll(Host);

        if (_inWorld)
        {
            if (!_identityKnown && now >= _nextIdentityTryMs) TryStartCharacter(now);
            if (_identityKnown && _world != null)
            {
                // The item selected in the game counts as in use while it is selected (and for the hold after).
                if (Host.HasGetSelectedItemId) _sort.Touch.NoteSelected(Host.GetSelectedItemId(), now);
                try { _sort.Tick(_world, now); }
                catch (Exception ex)
                {
                    Host.Log($"[RynthSort] sorting step failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        if (_saveDueMs != 0 && now >= _saveDueMs) SaveNow();
        _autoState = _inWorld && _identityKnown ? (_sort.Data.Settings.Auto ? 1 : 0) : -1;
        if (_sort.Moved > _movedSeen) _lastMoveMs = now;   // a move landed (Moved goes back to 0 on Reset: not a move)
        _movedSeen = _sort.Moved;
        _moving = _inWorld && _identityKnown && (_sort.HasMoveOut || (_lastMoveMs != 0 && now - _lastMoveMs < MovingHoldMs));

        TickWindow(now);
        if (_direct || !Host.HasUi || _window == null || _panel == null) return;
        Record(_window, now);
        _ui.Submit(Host, now);

        if (_identityKnown && _window.Visible != _sort.Data.Settings.WindowOpen)
        {
            _sort.Data.Settings.WindowOpen = _window.Visible;
            RequestSave();
        }
    }

    // The engine's script-window op level: 2 draws item icons (engines from 2026-09-30), 3 tables
    // and fonts (not released yet). Asked until it answers.
    private int _opLevel = -1;
    private int OpLevelNow()
    {
        if (_opLevel < 0 && Host.HasUi && Host.TryGetUiInfo(out RynthCore.PluginSdk.UiInfoNative info)) _opLevel = info.OpLevel;
        return Math.Max(_opLevel, 0);
    }

    private bool IconsOn() => OpLevelNow() >= 2;

    private void TryStartCharacter(long now)
    {
        _nextIdentityTryMs = now + IdentityRetryMs;
        uint player = Host.GetPlayerId();
        if (player == 0 || !Host.TryGetObjectName(player, out string name) || string.IsNullOrWhiteSpace(name)) return;
        string server = Host.TryGetWorldName(out string w) ? w : string.Empty;

        _server = server;
        _character = name;
        CharacterSortData data = _store.Load(server, name, out string? problem);
        if (problem != null) Host.Log($"[RynthSort] couldn't read the saved rules ({problem}); starting with none (the file is kept as .bak at the next save).");
        _sort.Data = data;
        _identityKnown = true;
        _backupOnSave = problem != null;   // a broken file is kept beside the new one (.bak)

        string own = _store.PathFor(server, name);
        if (data.ImportedFrom.Length > 0 && !File.Exists(own))
        {
            string? error = _store.Save(server, name, data);
            Print(error == null
                ? $"Took over the RynthSort script's rules for {name} ({data.Rules.Count} rules, auto-sort {(data.Settings.Auto ? "on" : "off")})."
                : $"Took over the RynthSort script's rules for {name}, but couldn't save them: {error}");
            Host.Log($"[RynthSort] imported {data.ImportedFrom} -> {own}");
        }
        CheckLuaScriptAutostart(server, name);
        if (_window != null) { _window.Visible = data.Settings.WindowOpen; _window.NeedsPass = true; }
        if (_direct)
        {
            _session.Reset();
            _screenDirty = true;
            _openUntilMs = data.Settings.WindowOpen ? now + OpenWhenHostedMs : 0;
        }
        Host.Log($"[RynthSort] sorting for {name} on {server}: {data.Rules.Count} rules, auto-sort {(data.Settings.Auto ? "on" : "off")}; file {own}");
    }

    /// <summary>The RynthSort Lua script set to start too would sort beside the plugin: say so.</summary>
    private void CheckLuaScriptAutostart(string server, string character)
    {
        _warnings.Clear();
        try
        {
            string path = Path.Combine(RynthCore.Install.RynthInstallPaths.SuiteDir, "RynthLua", "settings.json");
            if (!File.Exists(path)) return;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!LuaScriptStarts(doc.RootElement, server + "|" + character)) return;
            const string line = "The RynthSort Lua script is set to start too; stop it in RynthLua so only one sorts";
            _warnings.Add(line);
            Print(line + ".");
        }
        catch
        {
            // Only a warning.
        }
    }

    /// <summary>True when RynthLua's settings start the "RynthSort" script for everyone or for this character.</summary>
    internal static bool LuaScriptStarts(JsonElement root, string characterKey)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        static bool Has(JsonElement list)
        {
            if (list.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement e in list.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), "RynthSort", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        if (root.TryGetProperty("GlobalScripts", out JsonElement g) && Has(g)) return true;
        if (root.TryGetProperty("CharacterScripts", out JsonElement c) && c.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in c.EnumerateObject())
                if (string.Equals(p.Name, characterKey, StringComparison.OrdinalIgnoreCase) && Has(p.Value)) return true;
        return false;
    }

    /// <summary>The one-time pack fix ran: one log line per rule, a chat line, and a save (keeping a .bak of an imported file).</summary>
    private void OnPacksFixed(List<string> lines, int changed)
    {
        bool imported = _sort.Data.ImportedFrom.Length > 0;
        foreach (string line in lines) Host.Log("[RynthSort] pack fix: " + line);
        if (imported)
        {
            int left = lines.Count - changed;
            Print($"The rules taken over from the RynthSort script counted foci as packs: {changed} now point at the right pack"
                  + (left > 0 ? $", {left} left as they were (see the log)." : "."));
            _backupOnSave = true;
        }
        else if (changed > 0)
        {
            Host.Log($"[RynthSort] pack fix: {changed} rule(s) now remember their pack by id");
        }
        RequestSave();
    }

    private void RequestSave()
    {
        if (_window != null) _window.NeedsPass = true;
        _screenDirty = true;
        if (_saveDueMs == 0) _saveDueMs = Environment.TickCount64 + SaveDelayMs;
    }

    private void SaveNow()
    {
        _saveDueMs = 0;
        if (!_identityKnown) return;
        _sort.Data.Settings.Clamp();
        string? error = _store.Save(_server, _character, _sort.Data, _backupOnSave);
        if (error != null) Host.Log("[RynthSort] save failed: " + error);
        else _backupOnSave = false;
    }

    /// <summary>A pass when one is wanted, or on the refresh interval while visible (the status ticks on).</summary>
    private void Record(UiWindow w, long now)
    {
        bool due = w.NeedsPass || (w.Visible && now >= w.NextPassMs);
        if (!due) return;
        w.NeedsPass = false;
        w.NextPassMs = now + PassIntervalMs;
        w.Begin();
        try
        {
            _panel!.Draw(w);
        }
        catch (Exception ex)
        {
            w.TextWrapped($"Something went wrong drawing this window: {ex.GetType().Name}: {ex.Message}");
            Host.Log($"[RynthSort] {w.Key} pass failed: {ex}");
        }
        w.End();
    }
}
