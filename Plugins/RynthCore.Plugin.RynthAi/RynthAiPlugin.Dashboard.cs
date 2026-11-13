// RynthAiPlugin.Dashboard.cs — RynthAi's dashboard window (Dashboard/InstrumentDashboard.cs, 2026-10-21).
//
// RynthAi draws its dashboard itself now: the sections manifest declares the window (Huds/
// SectionsManifest.cs, DashboardWindow) as "RynthAi" with "takeOver", so it takes the engine
// panel's place (its saved spot, bar button, hotkeys and /rc commands), and with RynthCore's
// plugin API v90 "chrome": "none" and "fitHeight": RynthAi draws the whole window, frame
// included, and the engine fits its height. On an older engine the window isn't declared and
// RynthCore's own dashboard stays.
//
// The Classic (retail) look is still RynthCore's face: picking it here runs "/rc dashboard
// classic" and drops the window, so the engine's face comes back for it; switching back to the
// modern look there brings this window back (the engine's state file says which look is on).
//
// The tick side: the window's queued clicks, the player's vitals (the engine's snapshot poll
// refreshed them while its dashboard was open), the monster RynthAi is fighting for the target
// bar (combat doesn't select it since 2026-06-03, so the selection alone left the bar empty in a
// fight), the prefs' save, and the settings' save after an opacity or lock change.
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Dashboard;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    internal const string DashboardSurface = "window:dashboard";

    private InstrumentDashboard? _instrument;                           // render thread
    private DashboardBridge? _dashBridge;                               // render thread
    private DashboardPrefs _dashPrefs = new();                          // loaded in Initialize
    private readonly ConcurrentQueue<Action> _dashActions = new();
    private readonly DateTime _dashLoadedUtc = DateTime.UtcNow;
    private long _dashDrawnMs = long.MinValue / 2;                      // render thread writes, tick reads
    private long _nextDashVitalsMs, _nextDashClassicMs, _dashSaveAtMs, _nextDashSortMs;
    private volatile int _dashSort = -1;                                // RynthSort's auto-sort (tick writes, render reads)
    private volatile bool _classicLook;                                 // the engine's look is Classic (its file)
    private long _classicAskedMs;                                       // "Classic look" clicked: the window steps aside now
    private volatile FightTarget? _dashFight;                           // tick writes, render reads
    private volatile int _dashStance = CombatMode.Melee;                // the Combat key's stance (tick writes, render reads)
    private volatile NavTrail.Kind _dashNav;                            // the Navigation key's shape (tick writes, render reads)
    private volatile bool _dashMetaLoaded;                              // the Meta key: a meta with rules is loaded (tick writes, render reads)
    private volatile bool _dashSortMoving;                              // the auto-sort key: RynthSort is moving items (tick writes, render reads)
    private int _dashFailures;                                          // draws in a row that threw

    /// <summary>The monster RynthAi is fighting, for the target bar: its name, health (0..1, -1 unknown) and vitals.</summary>
    internal sealed record FightTarget(uint Id, string Name, float Health, uint Stamina, uint MaxStamina, uint Mana, uint MaxMana);

    internal DashboardPrefs DashPrefs => _dashPrefs;
    internal DateTime DashLoadedUtc => _dashLoadedUtc;
    /// <summary>The engine (RynthPluginBase.Host is protected).</summary>
    internal RynthCoreHost DashHost => Host;

    /// <summary>
    /// The dashboard window goes in the manifest: an engine with frameless windows (v90), and not
    /// while the Classic look is on (RynthCore's face draws that). Render thread (the manifest poll).
    /// </summary>
    private bool DashboardWindowWanted =>
        Host.HasFramelessWindows && !_classicLook && Interlocked.Read(ref _classicAskedMs) == 0;

    /// <summary>
    /// RynthPluginRenderEmbed "window:dashboard": the dashboard, into the host's window (docked, or
    /// popped out in its own context). 1 drawn, 0 not ready, -2 failed (three draws in a row threw:
    /// the engine switches the surface off and says so). Render thread.
    /// </summary>
    internal int RenderDashboard(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || !_loginComplete || _dashboard == null || Host.ImGuiContext == IntPtr.Zero) return 0;
        Volatile.Write(ref _dashDrawnMs, Environment.TickCount64);
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _dashBridge ??= new DashboardBridge(this);
            _instrument ??= new InstrumentDashboard(_dashBridge);
            want = _instrument.Draw(popped);
            _dashFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderDashboard");
            return ++_dashFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    /// <summary>Plugin tick: the window's clicks, the vitals and the fight while it shows, the look, the saves.</summary>
    private void TickDashboard()
    {
        while (_dashActions.TryDequeue(out Action? act))
        {
            try { act(); }
            catch (Exception ex) { Host.Log("[RynthAi] Dashboard: " + ex.Message); }
        }
        long now = Environment.TickCount64;
        bool shown = now - Volatile.Read(ref _dashDrawnMs) < 2000;
        if (shown && now >= _nextDashVitalsMs)
        {
            _nextDashVitalsMs = now + 100;
            _dashboard?.RefreshDashboardVitals();
            _dashFight = ReadFightTarget(_dashFight);
            _dashStance = ReadStance(_dashStance);
            _dashNav = ReadNavKind();
            _dashMetaLoaded = ReadMetaLoaded();
        }
        if (now >= _nextDashSortMs)
        {
            // Twice a second for the dashboard's key; every 2 s otherwise, for Auto Cram (AutoSortOn).
            _nextDashSortMs = now + (shown ? 500 : 2000);
            _dashSort = ReadAutoSort(out bool sortMoving);
            _dashSortMoving = sortMoving;
            if (_dashboard?.Settings is { } sortSettings) sortSettings.AutoSortOn = _dashSort == 1;
            if (shown) RefreshDashProfileState();   // the Loaded files drawer's unsaved mark (RynthAiPlugin.ProfileFiles.cs)
        }
        if (now >= _nextDashClassicMs)
        {
            _nextDashClassicMs = now + 2000;
            bool classic = DashboardPrefs.ClassicLook();
            _classicLook = classic;
            long asked = Interlocked.Read(ref _classicAskedMs);
            // Once the engine's file says Classic (or 10 s on, if it never does) the click has landed.
            if (asked != 0 && (classic || now - asked > 10_000)) Interlocked.Exchange(ref _classicAskedMs, 0);
        }
        _dashPrefs.SaveIfChanged();
        // An opacity or lock change from the window, saved with the profile a moment later (a few
        // clicks on the opacity buttons make one save).
        if (_dashSaveAtMs != 0 && now >= _dashSaveAtMs)
        {
            _dashSaveAtMs = 0;
            _dashboard?.SaveSettings();
        }
    }

    /// <summary>The window changed something the settings profile keeps (opacity, lock). Plugin tick.</summary>
    internal void ScheduleDashSave() => _dashSaveAtMs = Environment.TickCount64 + 1000;

    /// <summary>
    /// The monster combat is on while the macro runs, or null: its name, the health the server sends
    /// for the mob we fight, and its stamina and mana when known. The last one again when nothing
    /// changed (no allocation). Plugin tick.
    /// </summary>
    private FightTarget? ReadFightTarget(FightTarget? last)
    {
        var settings = _dashboard?.Settings;
        int id = _combatManager?.activeTargetId ?? 0;
        if (id == 0 || settings == null || !settings.IsMacroRunning || !settings.EnableCombat) return null;
        uint uid = unchecked((uint)id);
        float health = _objectCache?.GetHealthRatio(id) ?? -1f;
        uint st = 0, maxSt = 0, mn = 0, maxMn = 0;
        if (Host.HasGetTargetVitals) Host.TryGetTargetVitals(uid, out _, out _, out st, out maxSt, out mn, out maxMn);
        if (last != null && last.Id == uid && last.Health == health && last.Stamina == st && last.MaxStamina == maxSt
            && last.Mana == mn && last.MaxMana == maxMn)
            return last;
        string name = last != null && last.Id == uid ? last.Name : _objectCache?[id]?.Name ?? string.Empty;
        if (name.Length == 0) name = "0x" + uid.ToString("X8");
        return new FightTarget(uid, name, health, st, maxSt, mn, maxMn);
    }

    /// <summary>
    /// The stance the Combat key draws: the client's combat mode when it is Melee, Missile or Magic;
    /// in peace (or with no engine call) the last one, so a resting mage keeps the wands. Plugin tick.
    /// </summary>
    private int ReadStance(int last)
    {
        int mode = Host.HasGetCurrentCombatMode ? Host.GetCurrentCombatMode() : CombatMode.NonCombat;
        return mode is CombatMode.Melee or CombatMode.Missile or CombatMode.Magic ? mode : last;
    }

    /// <summary>
    /// What the Navigation key draws: the Outdoor Hunt while it runs, else a dungeon patrol's
    /// route, else following (a fellow or a Follow route), else the loaded route's type; nothing
    /// loaded is None. Plugin tick.
    /// </summary>
    private NavTrail.Kind ReadNavKind()
    {
        var settings = _dashboard?.Settings;
        if (settings == null) return NavTrail.Kind.None;
        if (HuntActive) return NavTrail.Kind.Hunt;
        NavRouteParser? route = settings.CurrentRoute;
        if (_dunPatrolActive && route != null && ReferenceEquals(route, _dunPatrolRoute)) return NavTrail.Kind.Patrol;
        if (IsFollowActive(settings)) return NavTrail.Kind.Follow;
        if (route == null || route.Points.Count == 0) return NavTrail.Kind.None;
        return route.RouteType switch
        {
            NavRouteType.Circular => NavTrail.Kind.Circular,
            NavRouteType.Linear => NavTrail.Kind.Linear,
            NavRouteType.Once => NavTrail.Kind.Once,
            NavRouteType.Follow => NavTrail.Kind.Follow,
            _ => NavTrail.Kind.None,
        };
    }

    /// <summary>
    /// A meta with rules is loaded (the Meta key draws its states hollow otherwise). Read on the
    /// tick, under the rules' lock, so the render thread never takes it. Plugin tick.
    /// </summary>
    private bool ReadMetaLoaded()
    {
        var settings = _dashboard?.Settings;
        if (settings == null) return false;
        lock (settings.MetaRulesLock) return settings.MetaRules is { Count: > 0 };
    }

    /// <summary>
    /// The readout with the Combat key's stance, the Navigation key's shape and whether a meta is
    /// loaded, and the target bar on the monster RynthAi is fighting when it is fighting one. Render thread.
    /// </summary>
    internal DashboardReadout WithFightTarget(DashboardReadout r)
    {
        r = r with { CombatStance = _dashStance, NavKind = _dashNav, MetaLoaded = _dashMetaLoaded };
        FightTarget? f = _dashFight;
        if (f == null) return r;
        return r with
        {
            TargetId = f.Id, TargetLabel = f.Name, TargetHealth = f.Health, TargetIsFight = true,
            TargetStamina = f.Stamina, TargetMaxStamina = f.MaxStamina, TargetMana = f.Mana, TargetMaxMana = f.MaxMana,
        };
    }

    /// <summary>"Classic look": RynthCore's face draws it; this window steps aside at once. Render thread.</summary>
    internal void UseClassicLook()
    {
        Interlocked.Exchange(ref _classicAskedMs, Math.Max(1, Environment.TickCount64));
        QueueDashCommand("/rc dashboard classic");
    }

    /// <summary>RynthSort's auto-sort for the dashboard's key: -1 not loaded / not logged in / too old, 0 off, 1 on.</summary>
    internal int DashAutoSort => _dashSort;

    /// <summary>RynthSort is moving items right now (the auto-sort key's sorting look); false when it can't say.</summary>
    internal bool DashSortMoving => _dashSortMoving;

    /// <summary>
    /// RynthSort's auto-sort state, through the engine's JSON broker (RynthSort's
    /// RynthPluginGetAutoSortJson, {"auto":-1|0|1,"moving":0|1}); -1 when RynthSort isn't loaded or
    /// predates the export. <paramref name="moving"/>: it is moving items (false from a RynthSort
    /// older than the flag). Plugin tick.
    /// </summary>
    private int ReadAutoSort(out bool moving)
    {
        moving = false;
        if (!Host.HasGetPluginExportJson) return -1;
        string? json = Host.GetPluginExportJson("RynthSort", "RynthPluginGetAutoSortJson");
        if (json == null) return -1;
        moving = json.Contains("\"moving\":1", StringComparison.Ordinal);
        return json.Contains("\"auto\":1", StringComparison.Ordinal) ? 1
             : json.Contains("\"auto\":0", StringComparison.Ordinal) ? 0 : -1;
    }

    /// <summary>The dashboard's auto-sort key: RynthSort on or off, quietly (the key shows it). Plugin tick.</summary>
    internal void SetDashAutoSort(bool on)
    {
        if (!SendSortCommand(on ? "on quiet" : "off quiet")) return;
        // Lit at once; RynthSort runs the command on its own tick, so the next read waits a little.
        _dashSort = on ? 1 : 0;
        _nextDashSortMs = Environment.TickCount64 + 1000;
    }

    /// <summary>An /rsort command (without "/rsort") to RynthSort; false when it isn't loaded. Plugin tick.</summary>
    internal bool SendSortCommand(string arg) =>
        Host.HasSendPluginCommand && Host.SendPluginCommand("RynthSort", "rsort", arg);

    internal void QueueDashAction(Action action)
    {
        if (_dashActions.Count < 64) _dashActions.Enqueue(action);
    }

    /// <summary>A chat command for the tick (RynthCore's /rc, another plugin's /ro ...).</summary>
    internal void QueueDashCommand(string command)
    {
        if (!Host.HasInvokeChatParser || _engineCommands.Count >= MaxQueuedEngineCommands) return;
        _engineCommands.Enqueue(command);
    }

    /// <summary>The Mini Remote drawer: RynthAi's remote, into the current window. Render thread.</summary>
    internal bool DrawDashMiniRemote(out Vector2 want)
    {
        want = default;
        var huds = _huds;
        if (huds == null) return false;
        want = huds.RenderEmbedded(false, true);
        return true;
    }
}

/// <summary>What the dashboard window reads and asks (Dashboard/InstrumentDashboard.cs): RynthAi, queued for its tick.</summary>
internal sealed class DashboardBridge : IDashboardHost
{
    private readonly RynthAiPlugin _p;

    public DashboardBridge(RynthAiPlugin plugin) => _p = plugin;

    private LegacyDashboardRenderer Renderer => _p.DashboardRenderer ?? throw new InvalidOperationException("RynthAi's dashboard isn't ready.");

    public RynthCoreHost Host => _p.DashHost;
    public DashboardReadout Read() => _p.WithFightTarget(Renderer.ReadDashboard());
    public DashboardFiles ReadFiles() => Renderer.ReadDashboardFiles();
    public DashboardPrefs Prefs => _p.DashPrefs;
    public string Version => RynthPluginInfo.Version;
    public DateTime LoadedUtc => _p.DashLoadedUtc;

    public void Queue(Action action) => _p.QueueDashAction(action);
    public void Command(string command) => _p.QueueDashCommand(command);
    public void ToggleMacro() => Queue(_p.ToggleMacroFromPanel);   // refuses a start while VTank runs
    public void SetSubsystem(int id, bool on) => Queue(() => _p.DashboardRenderer?.SetSubsystemEnabled(id, on));
    public void ForceRebuff() => Queue(() => _p.DashboardRenderer?.RequestForceRebuff());
    public void CancelForceRebuff() => Queue(() => _p.DashboardRenderer?.RequestCancelForceRebuff());
    public void AdjustOpacity(float delta) => Queue(() => { _p.DashboardRenderer?.AdjustOpacity(delta); _p.ScheduleDashSave(); });
    public void ToggleLock() => Queue(() => { _p.DashboardRenderer?.TogglePanelLock(); _p.ScheduleDashSave(); });
    public void SelectProfile(int kind, int index) => Queue(() => _p.DashboardRenderer?.SelectProfileAtIndex(kind, index));
    public void SetLootMode(int mode) => Queue(() => _p.SetLootModeFromPanel(mode));
    public int AutoSort => _p.DashAutoSort;
    public bool AutoSortMoving => _p.DashSortMoving;
    public bool FightSelectedTarget => _p.DashboardRenderer?.Settings.FightSelectedTarget == true;
    public void SetAutoSort(bool on) => Queue(() => _p.SetDashAutoSort(on));
    public void ToggleSortWindow() => Queue(() => _p.SendSortCommand(string.Empty));
    public void StartPatrol() => Queue(_p.HandleDungeonNavPatrol);
    public void ToggleHunt() => Queue(_p.ToggleHunt);
    public bool HuntActive => _p.HuntActive;
    public void ToggleHuntPanel() => _p.ToggleHuntPanel();
    public void ToggleDfHeightPanel() => _p.ToggleDfHeightPanel();
    public bool DfHeightRotation => _p.DfHeightRotationOn;
    public void ToggleSpells() => _p.EnqueueRemoteCommand("spells", "toggle");
    public void ToggleActiveGear() => Queue(_p.ToggleActiveGearWindow);
    public void ToggleTimedCommands() => Queue(_p.ToggleTimedCommandsWindow);
    public void UseClassicLook() => _p.UseClassicLook();

    public ProfileFileInfo ProfileInfo(int kind)
    {
        var k = (ProfileKind)kind;
        ProfileFileInfo info = Renderer.ProfileInfo(k);
        if (k == ProfileKind.Loot && _p.DashLootEditorDirty) info = info with { Unsaved = true };
        else if (k == ProfileKind.Buffs && _p.DashSpellsDirty) info = info with { Unsaved = true };
        return info;
    }

    public void ProfileAction(int kind, ProfileVerb verb, string name, bool replace, Action<ProfileResult> done) => Queue(() =>
    {
        ProfileResult r;
        try { r = _p.RunProfileAction((ProfileKind)kind, verb, name, replace); }
        catch (Exception ex) { r = ProfileResult.Failed($"{verb} failed: {ex.Message}"); }
        // A Save As that would replace another file waits for the drawer's yes: nothing in chat yet.
        if (!r.NeedsReplace) _p.DashHost.WriteToChat(r.Text, r.Ok ? CommandReply.OkColor : CommandReply.FailColor);
        done(r);
    });
    public bool DrawMiniRemote(out Vector2 want) => _p.DrawDashMiniRemote(out want);
}
