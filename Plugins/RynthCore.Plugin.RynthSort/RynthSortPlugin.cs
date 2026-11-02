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
/// are taken over on a character's first run.
///
/// It stays out of the way (Sorting/SortGate.cs): nothing moves while a corpse or chest is being
/// looted, a vendor or trade window is open, the character is busy or animating, in portal space,
/// or while RynthAi is looting, salvaging, buffing, making ammo, waiting on the server or (unless
/// "Sort during fights" is on) fighting. One move at a time, each waited on, then a gap.
///
/// Everything runs on the plugin pump (OnTick and the events the engine drains into it) except
/// OnChatBarEnter, which the engine may call on AC's thread: it only queues the command.
/// </summary>
public sealed class RynthSortPlugin : RynthPluginBase
{
    public const string Version = "0.3.1";
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

    /// <summary>For the engine's dashboard toggle (RynthSortAutoState): -1 not logged in yet, 0 auto-sort off, 1 on. Any thread.</summary>
    public int AutoState => _autoState;

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
        _panel = new SortPanel(_sort, RequestSave)
        {
            Warnings = () => _warnings,
            Character = () => _identityKnown ? _character : string.Empty,
            Icons = IconsOn,
            CanSelect = () => Host.HasSelectItem,
            SelectInGame = id => Host.HasSelectItem && Host.SelectItem(id),
        };
        // No bar button of its own: opened from the engine Inventory window's Sort button, the
        // Scripts menu or /rsort.
        _window = _ui.Add(new UiWindow("RynthSort/Main", "RynthSort")
        {
            DefaultWidth = 360f, DefaultHeight = 420f, MinWidth = 300f, MinHeight = 200f,
            BarButton = false,
        });
        Host.Log($"[RynthSort] {Version} initialized. /rsort shows or hides the window; /rsort on|off|now|tidy|clean|unpack. Files: {_store.Root}");
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
                if (_window != null)
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
            case SortVerb.Help:
                Print(SortCommand.Usage);
                break;
            default:
                PrintFail(SortCommand.Unknown(c.Word));
                break;
        }
        if (_window != null) _window.NeedsPass = true;
    }

    private void PrintStatus()
    {
        SortView v = _sort.Planner.View;
        Print($"{_character}: auto-sort {(_sort.Data.Settings.Auto ? "on" : "off")}, {_sort.Status}; {_sort.Data.Rules.Count} rules, "
              + $"{v.Packs.Count} side packs, main pack {v.MainUsed}/{v.MainCapacity}; moved {_sort.Moved}, failed {_sort.Failed} this session.");
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
                try { _sort.Tick(_world, now); }
                catch (Exception ex)
                {
                    Host.Log($"[RynthSort] sorting step failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        if (_saveDueMs != 0 && now >= _saveDueMs) SaveNow();
        _autoState = _inWorld && _identityKnown ? (_sort.Data.Settings.Auto ? 1 : 0) : -1;

        if (!Host.HasUi || _window == null || _panel == null) return;
        Record(_window, now);
        _ui.Submit(Host, now);

        if (_identityKnown && _window.Visible != _sort.Data.Settings.WindowOpen)
        {
            _sort.Data.Settings.WindowOpen = _window.Visible;
            RequestSave();
        }
    }

    // Item icons in script windows need op level 2 (engines from 2026-09-30); asked until it says yes.
    private bool _icons;
    private bool IconsOn()
    {
        if (!_icons) _icons = Host.HasUi && Host.TryGetUiInfo(out RynthCore.PluginSdk.UiInfoNative info) && info.OpLevel >= 2;
        return _icons;
    }

    private void TryStartCharacter(long now)
    {
        _nextIdentityTryMs = now + IdentityRetryMs;
        uint player = Host.GetPlayerId();
        if (player == 0 || !Host.TryGetObjectName(player, out string name) || string.IsNullOrWhiteSpace(name)) return;
        string server = Host.TryGetWorldName(out string w) ? w : string.Empty;

        _server = server;
        _character = name;
        CharacterSortData data = _store.Load(server, name, out string? problem);
        if (problem != null) Host.Log($"[RynthSort] couldn't read the saved rules ({problem}); using defaults.");
        _sort.Data = data;
        _identityKnown = true;
        _backupOnSave = false;

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
