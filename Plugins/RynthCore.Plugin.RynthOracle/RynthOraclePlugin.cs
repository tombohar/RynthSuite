using System;
using System.Collections.Concurrent;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthOracle;

/// <summary>
/// RynthOracle: quest flags against a master quest list, character status and progression,
/// augmentations and luminance, society standing, titles, Void timers and fellowship info, in
/// the engine's script windows (ImGui). A RynthCore port of the Decal plugin Oracle of Dereth
/// by Advis Eveldan (advis61), MIT; see THIRD_PARTY_NOTICES.md.
///
/// Everything runs on the plugin pump (OnTick and the events it drains) except OnChatBarEnter,
/// which the engine may call on AC's thread: it only classifies the line and queues work.
/// </summary>
public sealed class RynthOraclePlugin : RynthPluginBase
{
    // Previous release: 0.2.2.
    public const string Version = "0.3.0";

    private const long QuestReadDelayMs = 6000;   // after login, before the hidden /myquests
    private const long CharacterReadMs = 1000;
    // The Character tab's session (RynthAi): read only while that tab is on screen.
    private const long CharacterExtrasReadMs = 2000;
    private const long BookWorldWaitMs = 15_000;  // how long the quest book waits for the world name

    private readonly OracleContext _c = new();
    private readonly UiHost _ui = new();
    private UiWindow? _main, _void, _timers;
    private OracleView? _mainView;
    private VoidView? _voidView;
    private QuestTimersView? _timersView;
    private SkillsEmbed? _skills;
    private int _seenBookRevision = -1;

    private readonly ConcurrentQueue<string> _commands = new();
    private long _questReadAtMs = -1;
    private long _loginMs;
    private long _nextCharacterReadMs;
    private long _nextCharacterExtrasMs;
    private bool _voidAutoDecided;
    private bool _dailyCheckStarted;
    private int _seenFlagRevision = -1, _seenCatalogRevision = -1, _seenVoidRevision = -1;
    // RynthCore's server list (ServerStateClient.Revision) last applied to the server identity.
    private int _seenServerStateRevision = -1;
    private string? _catalogKey;          // what the catalog was loaded for (QuestPackSource.Key; "" = retail only)
    private int _seenPackRevision = -1;   // the active server plugin's quest pack (ServerQuestPackClient.Revision)
    private int _titleCount = -2;
    private uint _titleCurrent;

    public override int Initialize()
    {
        _c.Host = Host;
        _c.Reply = new CommandReply("[RynthOracle]", (t, c) => Host.WriteToChat(t, c), t => Host.Log(t));
        _c.Settings.Load();
        _c.RequestQuests = RequestQuests;
        _c.RereadCharacter = RereadCharacter;
        _c.RereadTitles = RereadTitles;
        _c.ReloadQuestList = ReloadQuestList;
        _c.Send = cmd => _c.InWorld && Host.InvokeChatParser(cmd);
        _c.RunCommand = cmd => _c.InWorld && Host.InvokeChatParser(cmd);
        _c.SendNav = line => Host.HasSendPluginCommand && Host.SendPluginCommand("RynthNav", "rnav", line);
        _ui.Log = msg => Host.Log(msg);

        _mainView = new OracleView(_c);
        // The Skills / Attributes / Progress tabs: RynthCore draws its Skills panel body in the window.
        _skills = new SkillsEmbed(cmd => Host.InvokeChatParser(cmd));
        _voidView = new VoidView(_c);
        _timersView = new QuestTimersView(_c);
        _main = _ui.Add(new UiWindow("RynthOracle/Oracle", "Oracle")
        {
            DefaultWidth = 700f, DefaultHeight = 600f, MinWidth = 420f, MinHeight = 260f,
        });
        // The Void window is opened from the Oracle window: no bar button (and no Scripts menu
        // line, which would bring back the Scripts button just for it).
        _void = _ui.Add(new UiWindow("RynthOracle/Void", "Void")
        {
            DefaultWidth = 220f, DefaultHeight = 150f, MinWidth = 160f, MinHeight = 110f,
            ShowInBar = false,
        });
        _mainView.SetVoidWindow = v => { if (_void != null) { _void.Visible = v; _void.NeedsPass = true; } };
        _mainView.VoidWindowVisible = () => _void?.Visible ?? false;
        // The Quest Timers window (tracked quests): opened from the Quests tab, /ro timers or the
        // engine's Character menu; like the Void window it has no bar button of its own.
        _timers = _ui.Add(new UiWindow("RynthOracle/QuestTimers", "Quest Timers")
        {
            DefaultWidth = 310f, DefaultHeight = 220f, MinWidth = 220f, MinHeight = 90f,
            ShowInBar = false,
        });
        _c.SetQuestTimersWindow = SetQuestTimers;
        _c.QuestTimersWindowVisible = () => _timers?.Visible ?? false;

        Host.Log($"[RynthOracle] {Version} initialized. /ro opens the window. Based on Oracle of Dereth by Advis Eveldan - thank you!");
        return 0;
    }

    /// <summary>Shows or hides the Quest Timers window and remembers the choice for the next login.</summary>
    private void SetQuestTimers(bool visible)
    {
        if (_timers == null) return;
        _timers.Visible = visible;
        _timers.NeedsPass = true;
        if (_c.Settings.QuestTimersOpen != visible)
        {
            _c.Settings.QuestTimersOpen = visible;
            _c.Settings.Save();
        }
        if (_main != null) _main.NeedsPass = true;
    }

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _c.InWorld = true;
        _c.Flags.Clear();
        _c.Void.Clear();
        _c.Server.Reset();
        _c.ServerState.Clear();
        _c.ServerState.Poll(Host, Environment.TickCount64, force: true);
        _seenServerStateRevision = _c.ServerState.Revision;
        _c.VoidDotScale = VoidScaleFrom(_c.ServerState.ProfileJson);
        _c.Server.Refresh(Host, _c.ServerState.State);
        _c.QuestPacks.Clear();
        _c.QuestPacks.Poll(Host, Shared.ServerQuestPackClient.OwnerOf(_c.ServerState.State), Environment.TickCount64, force: true);
        _seenPackRevision = _c.QuestPacks.Revision;
        _catalogKey = null;
        ApplyServer();
        _titleCount = -2;
        _c.TitlesKnown = false;
        _c.EarnedTitles.Clear();

        long now = Environment.TickCount64;
        _loginMs = now;
        _questReadAtMs = _c.Settings.ReadQuestsAtLogin ? now + QuestReadDelayMs : -1;
        _nextCharacterReadMs = now;
        _nextCharacterExtrasMs = now;
        ClearCharacterExtras();
        _voidAutoDecided = false;
        _dailyCheckStarted = false;
        if (_main != null) { _main.Visible = _c.Settings.MainWindowOpen; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
        // The quest book loads once the character's name (and world) are known (TickData).
        _c.Book.Clear();
        _c.Timers.Reset();
        if (_timers != null) { _timers.Visible = _c.Settings.QuestTimersOpen; _timers.NeedsPass = true; }
    }

    public override void OnLogout()
    {
        _c.InWorld = false;
        _questReadAtMs = -1;
        // Closed for character select (the saved "left open" choice is kept for the next login).
        if (_main != null) { _main.Visible = false; _main.NeedsPass = true; }
        if (_void != null) { _void.Visible = false; _void.NeedsPass = true; }
        if (_timers != null) { _timers.Visible = false; _timers.NeedsPass = true; }
        _skills?.Update(string.Empty, false, false, Environment.TickCount64);
        _ui.SubmitEmpty(Host);
        _c.Book.Clear();   // saved as it changed
        _c.Timers.Reset();
        _c.Flags.Clear();
        _c.Void.Clear();
        _c.Server.Reset();
        _c.VoidDotScale = 1.0;
        _c.ServerState.Clear();
        _c.QuestPacks.Clear();
        _c.TitlesKnown = false;
        _c.EarnedTitles.Clear();
        ClearCharacterExtras();
    }

    /// <summary>Forgets the Character tab's session (login, logout): the next character's comes fresh.</summary>
    private void ClearCharacterExtras()
    {
        _c.Session = default;
        _c.SessionKnown = false;
        _c.AiSession.Reset();
    }

    /// <summary>
    /// While the Character tab is on screen: RynthAi's session, every <see cref="CharacterExtrasReadMs"/>.
    /// Nothing is read with the window closed or on another tab.
    /// </summary>
    private void ReadCharacterExtras(long now)
    {
        if (_main == null || _mainView == null || !_main.Visible || _mainView.Tab != OracleView.TabCharacter) return;
        if (now < _nextCharacterExtrasMs) return;
        _nextCharacterExtrasMs = now + CharacterExtrasReadMs;

        bool known = _c.AiSession.TryGet(Host, now, out Shared.SessionStatsV1 s);
        if (known != _c.SessionKnown || known && (s.Xp != _c.Session.Xp || s.Lum != _c.Session.Lum || s.Kills != _c.Session.Kills))
            _main.NeedsPass = true;
        _c.SessionKnown = known;
        _c.Session = known ? s : default;
    }

    /// <summary>Opens the Quests tab on the trackers: one kind (/ro legendary, /ro markers) or all of them (null).</summary>
    private void ShowTrackers(QuestKind? kind)
    {
        _mainView?.ShowTrackers(kind);
        if (_main != null) { _main.Visible = true; _main.NeedsPass = true; }
    }

    /// <summary>Shows the main window on a tab (another plugin's link, or a /ro tab command).</summary>
    private void ShowTab(string tab)
    {
        if (_mainView != null) _mainView.Tab = tab;
        if (_main != null) { _main.Visible = true; _main.NeedsPass = true; }
        if (tab == OracleView.TabCharacter) _nextCharacterExtrasMs = 0;   // read at once
    }

    /// <summary>
    /// Loads what depends on the server: the quest catalog with the active server's pack (the one its
    /// plugin offers through "RynthServer.QuestPack"; none on a server without one). Runs at login and
    /// again whenever the server or its pack changes (either can arrive a moment after login).
    /// </summary>
    private void ApplyServer()
    {
        Shared.ServerQuestPack? pack = Shared.ServerQuestPackClient.ForActive(_c.ServerState.State, _c.QuestPacks.Pack);
        QuestPackSource? source = QuestCatalog.SourceFor(_c.Server, pack);
        // The pack client's revision goes up whenever the offered pack changes (same size or not).
        string key = source == null ? "" : source.Key + "|" + _c.QuestPacks.Revision;
        if (_catalogKey == key) return;
        _catalogKey = key;
        try { _c.Catalog.Load(source, msg => Host.Log(msg)); }
        catch (Exception ex) { Host.Log($"[RynthOracle] Couldn't load the quest list: {ex.Message}"); }
        _c.Catalog.SyncDiscoveries(_c.Flags);
        Host.Log($"[RynthOracle] Server: {_c.Server.DisplayName} ({_c.Server.Source}); {_c.Catalog.RetailCount} retail quests{PackText()}.");
        if (_main != null) _main.NeedsPass = true;
    }

    /// <summary>" + 546 Conquest (from RynthConquest)", or "" without a pack.</summary>
    private string PackText() => _c.Catalog.PackServer.Length > 0
        ? $" + {_c.Catalog.ServerCount} {_c.Catalog.PackServer} ({_c.Catalog.PackSource})"
        : "";

    public override void Shutdown()
    {
        _skills?.Shutdown();
        _ui.SubmitEmpty(Host);
        _c.Updater.Shutdown();
        _c.Settings.Save();
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        long now = Environment.TickCount64;
        string line = text.TrimEnd('\r', '\n');

        MyQuestsLine kind = _c.Flags.OnChatLine(line, now);
        if (kind != MyQuestsLine.None)
        {
            // Only the reply to RynthOracle's own request, and only when the setting says so.
            if (_c.Flags.AwaitingOurs && _c.Settings.HideOwnQuestReplies && kind is MyQuestsLine.Quest or MyQuestsLine.Empty)
                eat = 1;
            return;
        }
        if (!_c.InWorld) return;

        // A server's own command replies (Conquest's /b, Aelrynth's /top, ...) are its server plugin's.
        // A server's own Void DoT duration rule comes from its plugin's profile (RynthConquest's for
        // Conquest); 1 everywhere else (VoidScaleFrom).
        _c.Void.OnChatLine(line.Trim(), now, ResolveTarget, () => _c.Character.HasEnchantment(SpellIds.SurgeOfDestruction), _c.VoidDotScale);
    }

    /// <summary>The server profile value RynthOracle's Void timers apply ("RynthServer.Profile" values).</summary>
    internal const string VoidDotScaleKey = "voidDotDurationScale";

    /// <summary>
    /// The active server's Void DoT duration multiplier, from its plugin's profile: a number from 1 to
    /// 10 (anything else, or none, is 1). Re-read whenever RynthCore's server state or the profile changes.
    /// </summary>
    internal static double VoidScaleFrom(string? profileJson)
    {
        double v = RynthCore.Plugin.Shared.ServerProfileView.Value(profileJson, VoidDotScaleKey, 1.0);
        return v >= 1.0 && v <= 10.0 ? v : 1.0;
    }

    /// <summary>The selected target's id when its name matches (RynthCore has no spell-cast event for plugins).</summary>
    private uint ResolveTarget(string name)
    {
        uint selected = Host.GetSelectedItemId();
        return selected != 0 && Host.TryGetObjectName(selected, out string n) && n == name ? selected : 0;
    }

    // May run on AC's thread: classify and queue only.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (t.Equals("/myquests", StringComparison.OrdinalIgnoreCase))
        {
            _c.Flags.NoteTypedCommand(); // not eaten: the server answers it
            return;
        }
        if (!t.StartsWith("/ro", StringComparison.OrdinalIgnoreCase) || (t.Length > 3 && !char.IsWhiteSpace(t[3])))
            return;
        eat = 1;
        _commands.Enqueue(t.Length > 3 ? t.Substring(3).Trim().ToLowerInvariant() : "");
    }

    private void RunCommand(string arg)
    {
        // Every /ro answers in chat (2026-10-28, OracleCommand.cs; Plugins/Shared/CommandReply.cs). RynthOracle
        // can't tell a typed /ro from a meta's or another plugin's menu (they all come through chat), so
        // each one does.
        OracleCommand c = OracleCommand.Parse(arg);
        // A window shown: says so, or (an engine without plugin windows) why nothing shows.
        void Shown(string text)
        {
            if (Host.HasUi) _c.Print(text);
            else _c.PrintFail(OracleCommand.NoWindows);
        }
        switch (c.Verb)
        {
            case OracleVerb.Window:
                if (_main != null) { _main.Visible = !_main.Visible; _main.NeedsPass = true; Shown(OracleCommand.Shown("Oracle window", _main.Visible)); }
                break;
            case OracleVerb.Void:
                if (_void != null) { _void.Visible = !_void.Visible; _void.NeedsPass = true; Shown(OracleCommand.Shown("Void window", _void.Visible)); }
                break;
            case OracleVerb.Timers:
                if (_timers != null) { SetQuestTimers(!_timers.Visible); Shown(OracleCommand.Shown("Quest Timers window", _timers.Visible)); }
                break;
            case OracleVerb.Quests:
            {
                bool sent = RequestQuests();
                var (ok, text) = OracleCommand.Quests(_c.InWorld, sent, _c.Flags.ThrottleSecondsLeft(Environment.TickCount64));
                if (ok) _c.Print(text); else _c.PrintFail(text);
                break;
            }
            case OracleVerb.Tab:
                // RynthAi, the server plugins and RynthCore's menus link to these; they show, never toggle off.
                ShowTab(c.Tab);
                Shown(OracleCommand.OnTab(c.Tab));
                break;
            case OracleVerb.Trackers:
                // A server's own trackers and tabs are its server plugin's pages (since 2026-10-21).
                ShowTrackers(c.Trackers);
                Shown(OracleCommand.OnTrackers(c.Trackers));
                break;
            case OracleVerb.Status:
                string titles = _c.TitlesKnown ? $", {_c.EarnedTitles.Count} titles held" : "";
                _c.Print($"Server: {_c.Server.DisplayName} ({_c.Server.Source}). "
                         + $"{_c.Flags.Flags.Count} quest flags read, {_c.Catalog.RetailCount} retail quests{PackText()}, {_c.Void.Dots.Count} Void spells tracked{titles}.");
                break;
            case OracleVerb.About:
                // The About tab (settings, the server RynthCore says this is, the quest list).
                _c.Print("Based on Oracle of Dereth by Advis Eveldan - thank you! github.com/advis61/OracleOfDereth (MIT)");
                ShowTab(c.Tab);
                if (!Host.HasUi) _c.PrintFail(OracleCommand.NoWindows);
                break;
            case OracleVerb.Help:
                _c.Print(OracleCommand.Usage);
                break;
            default:
                _c.PrintFail(OracleCommand.Unknown(c.Word));
                break;
        }
    }

    /// <summary>Sends /myquests; false when nothing went out (not in the world, or one was sent moments ago).</summary>
    private bool RequestQuests()
    {
        if (!_c.InWorld) return false;
        long now = Environment.TickCount64;
        bool sent = _c.Flags.Request(cmd => Host.InvokeChatParser(cmd), now);
        if (!sent)
        {
            int wait = _c.Flags.ThrottleSecondsLeft(now);
            Host.Log(wait > 0
                ? $"[RynthOracle] The server answers one /myquests a minute; try again in {wait}s (the flags shown are kept)."
                : "[RynthOracle] /myquests was sent moments ago; waiting for that reply.");
        }
        if (_main != null) _main.NeedsPass = true;
        return sent;
    }

    // ── The Oracle window's Refresh buttons (the data side; Views\RefreshBar.cs has the rest) ──

    /// <summary>Reads the character's stats, buffs and (on the Character tab) RynthAi's session now, not at the next timer.</summary>
    private void RereadCharacter()
    {
        if (!_c.InWorld) return;
        long now = Environment.TickCount64;
        _c.Character.Read(Host);
        _c.Void.RemoveExpired(now);
        _nextCharacterReadMs = now + CharacterReadMs;
        _nextCharacterExtrasMs = 0;
        ReadCharacterExtras(now);
        if (_main != null) _main.NeedsPass = true;
        if (_void != null) _void.NeedsPass = true;
    }

    /// <summary>Asks the engine for the character's titles again (it keeps them from the login events).</summary>
    private void RereadTitles()
    {
        _titleCount = -2;
        ReadTitles();
        if (_main != null) _main.NeedsPass = true;
    }

    /// <summary>
    /// Reloads the quest list and the server's pack (a downloaded list, a pack dropped into
    /// %APPDATA%\RynthCore\RynthOracle\packs, the server plugin's pack asked for again) and asks
    /// RynthCore which server this is.
    /// </summary>
    private void ReloadQuestList()
    {
        long now = Environment.TickCount64;
        _c.ServerState.Poll(Host, now, force: true);
        _seenServerStateRevision = _c.ServerState.Revision;
        _c.VoidDotScale = VoidScaleFrom(_c.ServerState.ProfileJson);
        _c.Server.Refresh(Host, _c.ServerState.State);
        _c.QuestPacks.Poll(Host, Shared.ServerQuestPackClient.OwnerOf(_c.ServerState.State), now, force: true);
        _seenPackRevision = _c.QuestPacks.Revision;
        _catalogKey = null;   // the files may have changed: load again even for the same pack
        ApplyServer();
        if (_main != null) _main.NeedsPass = true;
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        _c.NowMs = now;
        _c.UtcNow = DateTime.UtcNow;

        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd);
        _c.Reply?.Flush();

        _ui.Poll(Host);
        TickData(now);

        if (!Host.HasUi) return;
        // After the poll, so a tab clicked this tick is already selected.
        if (_skills != null && _main != null && _mainView != null)
            _skills.Update(_mainView.Tab, _main.Visible, _c.InWorld, now);
        RecordWindows(now);
        _ui.Submit(Host, now);

        // Remember whether the main window was left open (the player's X or the bar menu).
        if (_c.InWorld && _main != null && _main.Visible != _c.Settings.MainWindowOpen)
        {
            _c.Settings.MainWindowOpen = _main.Visible;
            _c.Settings.Save();
        }
        // The same for the Quest Timers window (closed with its X or opened from the engine's menu).
        if (_c.InWorld && _timers != null && _timers.Visible != _c.Settings.QuestTimersOpen)
        {
            _c.Settings.QuestTimersOpen = _timers.Visible;
            _c.Settings.Save();
            if (_main != null) _main.NeedsPass = true;
        }
    }

    private void TickData(long now)
    {
        _c.Flags.Tick(now);
        if (_c.Flags.LastPartialReply is { } partial)
        {
            _c.Flags.LastPartialReply = null;
            Host.Log($"[RynthOracle] /myquests reply looked cut short ({partial.Got} lines, {partial.Had} flags held): merged it instead of replacing the list.");
        }
        if (_c.Flags.Revision != _seenFlagRevision)
        {
            _seenFlagRevision = _c.Flags.Revision;
            _c.Catalog.SyncDiscoveries(_c.Flags);
            if (_main != null) _main.NeedsPass = true;
            if (_timers != null) _timers.NeedsPass = true;
        }
        if (_c.Catalog.Revision != _seenCatalogRevision)
        {
            _seenCatalogRevision = _c.Catalog.Revision;
            if (_main != null) _main.NeedsPass = true;
            if (_timers != null) _timers.NeedsPass = true;
        }
        if (_c.Book.Revision != _seenBookRevision)
        {
            _seenBookRevision = _c.Book.Revision;
            if (_main != null) _main.NeedsPass = true;
            if (_timers != null) _timers.NeedsPass = true;
        }

        string? result = _c.Updater.TakeResult(out bool updated);
        if (result != null)
        {
            _c.UpdaterMessage = result;
            Host.Log("[RynthOracle] " + result);
            if (updated)
            {
                _c.Catalog.Reload(msg => Host.Log(msg));
                _c.Catalog.SyncDiscoveries(_c.Flags);
            }
            if (_main != null) _main.NeedsPass = true;
        }

        if (!_c.InWorld) return;

        ReadCharacterExtras(now);

        // RynthCore's server list (every 2 s): a new pick or override applies now, not at the next character read.
        _c.ServerState.Poll(Host, now);
        bool serverChanged = _c.ServerState.Revision != _seenServerStateRevision;
        if (serverChanged)
        {
            _seenServerStateRevision = _c.ServerState.Revision;
            _c.VoidDotScale = VoidScaleFrom(_c.ServerState.ProfileJson);
            _c.Server.Refresh(Host, _c.ServerState.State);
        }
        // The active server plugin's quest pack (every 2 s; the CSV is read only when it changed).
        _c.QuestPacks.Poll(Host, Shared.ServerQuestPackClient.OwnerOf(_c.ServerState.State), now, force: serverChanged);
        if (serverChanged || _c.QuestPacks.Revision != _seenPackRevision)
        {
            _seenPackRevision = _c.QuestPacks.Revision;
            ApplyServer();
            if (_main != null) _main.NeedsPass = true;
        }

        if (_questReadAtMs >= 0 && now >= _questReadAtMs)
        {
            _questReadAtMs = -1;
            // Another plugin (RynthAi's meta manager) or the player may already have asked.
            if (_c.Flags.LastReadUtc == DateTime.MinValue && !_c.Flags.Loading) RequestQuests();
        }

        if (!_dailyCheckStarted && _c.Settings.CheckQuestListDaily
            && _c.UtcNow - _c.Settings.LastQuestListCheckUtc >= QuestCatalogUpdater.Interval)
        {
            _dailyCheckStarted = true;
            _c.Settings.LastQuestListCheckUtc = _c.UtcNow;
            _c.Settings.Save();
            _c.Updater.Start();
        }

        if (now >= _nextCharacterReadMs)
        {
            _nextCharacterReadMs = now + CharacterReadMs;
            _c.Character.Read(Host);
            _c.Void.RemoveExpired(now);
            _c.Server.Refresh(Host, _c.ServerState.State);
            ApplyServer();
            LoadQuestBook(now);
            AnnounceTimers();
            ReadTitles();
            if (!_voidAutoDecided && _c.Character.Valid)
            {
                _voidAutoDecided = true;
                if (_void != null && _c.Settings.AutoShowVoidWindow && _c.Character.VoidMagic.Known)
                {
                    _void.Visible = true;
                    _void.NeedsPass = true;
                }
            }
        }

        if (_c.Void.Revision != _seenVoidRevision)
        {
            _seenVoidRevision = _c.Void.Revision;
            if (_void != null) _void.NeedsPass = true;
            if (_main != null && _mainView != null && _mainView.Tab == OracleView.TabVoid) _main.NeedsPass = true;
        }
    }

    /// <summary>Says in chat which watched quest timers came up: tracked quests, or every held quest if so set.</summary>
    private void AnnounceTimers()
    {
        if (!_c.Settings.AlertTimers || !_c.Book.Loaded) { _c.Timers.Reset(); return; }
        var ready = _c.Timers.Check(_c.Flags, _c.Catalog, _c.Settings.AlertAllTimers ? null : _c.Book.Tracked, _c.UtcNow);
        foreach (Quest q in ready)
            _c.Print($"Quest ready again: {q.Label}");
    }

    /// <summary>
    /// Loads this character's quest book (tracked quests, rewards) once the name is known and the
    /// world has been announced, or after <see cref="BookWorldWaitMs"/> without one (then filed
    /// under "default"), so the file isn't picked before the world name arrives.
    /// </summary>
    private void LoadQuestBook(long now)
    {
        if (_c.Book.Loaded || !_c.Character.Valid || _c.Character.Name.Length == 0) return;
        if (_c.Server.WorldName.Length == 0 && now - _loginMs < BookWorldWaitMs) return;
        _c.Book.Load(_c.Server.WorldName, _c.Character.Name);
        if (_c.Book.LoadNote.Length > 0) _c.Print(_c.Book.LoadNote);
    }

    /// <summary>The titles the character holds (engine API v75), re-read each second (cheap; changes are rare).</summary>
    private void ReadTitles()
    {
        if (!Host.TryGetCharacterTitles(out uint[] ids, out uint current))
        {
            if (_c.TitlesKnown) { _c.TitlesKnown = false; _c.EarnedTitles.Clear(); }
            return;
        }
        if (ids.Length == _titleCount && current == _titleCurrent && _c.TitlesKnown) return;
        _titleCount = ids.Length;
        _titleCurrent = current;
        _c.EarnedTitles.Clear();
        foreach (uint id in ids) _c.EarnedTitles.Add((int)id);
        _c.CurrentTitle = current;
        _c.TitlesKnown = true;
        if (_main != null) _main.NeedsPass = true;
    }

    private void RecordWindows(long now)
    {
        if (_main != null && _mainView != null)
        {
            long interval = _mainView.TabHasLiveTimers ? 1000 : 3000;
            Record(_main, now, interval, _mainView.Draw);
        }
        if (_void != null && _voidView != null)
            Record(_void, now, 500, _voidView.Draw);
        if (_timers != null && _timersView != null)
            Record(_timers, now, 1000, _timersView.Draw);
    }

    /// <summary>
    /// A pass when one is wanted, or on the refresh interval while visible. A hidden window is
    /// still recorded when asked (so the bar menu shows it with content); it costs nothing more.
    /// </summary>
    private void Record(UiWindow w, long now, long interval, Action<UiWindow> draw)
    {
        bool due = w.NeedsPass || (w.Visible && now >= w.NextPassMs);
        if (!due) return;
        w.NeedsPass = false;
        w.NextPassMs = now + interval;
        w.Begin();
        try
        {
            draw(w);
        }
        catch (Exception ex)
        {
            w.TextWrapped($"Something went wrong drawing this page: {ex.GetType().Name}: {ex.Message}");
            Host.Log($"[RynthOracle] {w.Key} pass failed: {ex}");
        }
        w.End();
    }
}
