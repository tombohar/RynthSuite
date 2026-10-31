// RynthAiPlugin.MonsterStats.cs — combat per kind of monster since login (MonsterStats/), shown
// under each monster in the Damage panel's Monsters tab, and /ra mstats (2026-10-30, a player's
// request: "combat statistics under individual monsters, evades/resists for both the player and
// the monster").
//
// Where it shows: the Damage panel (the dashboard's Monsters key) draws RynthAi's rows
// (BuildMonsterDamageJson); each monster row carries its numbers as "mstats", and an engine that
// draws them (RynthCore feature/monster-stats-tab: UI/Data/DamageRowStats.cs, GetUiHookFlags bit 8)
// puts a line under the monster. On an older engine (the owner's PC, plugin API 90) the field is
// ignored, so RynthAi's own Monster Stats window (a plugin window, API v89) is the fallback, with
// the Session Stats view's "Monster stats" button; on an engine with the tab neither is offered.
//
// Sources (MonsterStats/): melee and missile from the server's typed combat events (API v78
// server messages: 0x01B1..0x01B4, asked for in Initialize); spells from the server's spell lines
// in chat (CombatLines, one table); kills from the kill notice. Not a player's chat types, never a
// player's or a pet's name, and an enchantment line only for a creature (items cast the same line).
//
// Lifetime: from login to logout (per character), kept across a hot reload (MonsterStatsStore),
// started over only by /ra mstats reset, the window's Reset or the Damage panel's Reset stats; not
// by Reset XP (the owner's rule, 2026-10-30). Pump thread throughout, except the window (render
// thread, the published view) and the Damage panel's poll (the published rows).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.MonsterStats;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The window's title (its panel key) and surface (Huds/SectionsManifest.cs).</summary>
    internal const string MonsterStatsTitle = "Monster Stats", MonsterStatsSurface = "window:monsterstats";

    /// <summary>The engine's Damage panel key (the dashboard's Monsters key opens it).</summary>
    internal const string DamagePanelKey = "Monsters";

    internal const string MonsterStatsUsage = "Usage: /ra mstats [monster] | list | reset | show | hide";

    /// <summary>
    /// GetUiHookFlags bit 8 (SDK RynthUiHookFlags.DamageRowStats on RynthCore feature/monster-stats-tab):
    /// the engine's Damage panel draws a row's "mstats". Spelled here so RynthAi builds against an
    /// SDK without the name.
    /// </summary>
    internal const uint DamageRowStatsFlag = 1u << 8;

    private static readonly TimeSpan MonsterStatsSaveInterval = TimeSpan.FromSeconds(30);

    private readonly MonsterCombatStats _monsterStats = new();
    private volatile MonsterStatsView _monsterStatsView = MonsterStatsView.Empty;   // tick writes, render reads
    private int _monsterStatsViewVersion = -1;
    private string _monsterStatsViewFocus = string.Empty;
    private bool _monsterStatsViewInWorld;
    private long _monsterStatsViewAtMs;
    private long _monsterStatsDrawnMs = long.MinValue / 2;                           // render writes, tick reads
    // The rows by name for the Damage panel's poll (BuildMonsterDamageJson, its own thread): swapped whole.
    private volatile Dictionary<string, MonsterStatsRow> _monsterStatsByName = new(StringComparer.OrdinalIgnoreCase);
    private int _monsterStatsByNameVersion = -1;
    /// <summary>The counting has begun for this login (the save looked for); false before login and after logout.</summary>
    private bool _monsterStatsStarted;
    /// <summary>When the numbers started (login, or the last reset), UTC.</summary>
    private DateTime _monsterStatsSinceUtc;
    /// <summary>The Damage panel's Reset stats asked (its thread); done on the tick.</summary>
    private volatile bool _monsterStatsResetWanted;
    private int _monsterStatsSavedVersion = -1;
    private DateTime _monsterStatsSaveAtUtc;
    private bool _monsterStatsSaveFailedLogged;
    /// <summary>The engine streams the combat game events (Initialize); false = melee and missile go uncounted.</summary>
    private bool _combatEventsStreaming = true;
    private MonsterStatsWindow? _monsterStatsWindow;                                   // render thread
    private int _monsterStatsFailures;                                                 // render thread
    // Creature names around, by class (refreshed at most every 2 s): who a line's name is.
    private readonly Dictionary<string, AcObjectClass> _creatureClassByName = new(StringComparer.OrdinalIgnoreCase);
    private long _creatureNamesAtMs = long.MinValue / 2;

    /// <summary>Where the save goes (tests point it at a temp folder).</summary>
    internal string MonsterStatsDirectory { get; set; } = MonsterStatsStore.DefaultDirectory();

    /// <summary>The counters (tests, and RynthAi's own readers).</summary>
    internal MonsterCombatStats MonsterStatsForTests => _monsterStats;

    /// <summary>The engine's Damage panel shows the numbers under each monster (GetUiHookFlags bit 8). Any thread.</summary>
    internal bool EngineDrawsMonsterStats => Host.HasUiHooks && ((uint)Host.GetUiHookFlags() & DamageRowStatsFlag) != 0;

    // ── Events (pump thread) ─────────────────────────────────────────────

    /// <summary>One of the four combat game events (PluginExports.OnServerMessage).</summary>
    internal void NoteCombatEvent(uint eventType, ReadOnlySpan<byte> body)
    {
        if (!_loginComplete) return;
        if (!CombatEvents.TryParse(eventType, body, out PhysicalEvent kind, out string name, out int damage)) return;
        if (!CountsAsMonster(name, mayBeItem: false)) return;
        _monsterStats.OnPhysical(kind, name, damage, DateTime.UtcNow);
    }

    /// <summary>A chat line: one of the spell lines (CombatLines) about a monster, unless a player wrote it.</summary>
    private void NoteMonsterStatsChat(string text, int chatType)
    {
        if (!_loginComplete || RynthCore.Plugin.Shared.ChatSource.IsPlayerAuthored(chatType)) return;
        if (!CombatLines.TryParse(text, out CombatLine line)) return;
        if (!CountsAsMonster(line.Name, line.MayBeItem)) return;
        _monsterStats.OnSpell(line, DateTime.UtcNow);
    }

    /// <summary>The kill notice: a kill, and the killing blow by the combat mode it was struck in.</summary>
    private void NoteMonsterStatsKill(string? notice)
    {
        if (!_loginComplete) return;
        KillBlow blow = _currentCombatMode switch
        {
            CombatMode.Melee or CombatMode.Missile => KillBlow.Physical,
            CombatMode.Magic => KillBlow.Spell,
            _ => KillBlow.None,
        };
        _monsterStats.OnKill(notice, blow, DateTime.UtcNow, CreatureNameIn);
    }

    /// <summary>
    /// A name the stats count: not a player's or a pet's (PvP, a summon), nor an NPC's; and for a line
    /// an item could have sent (<paramref name="mayBeItem"/>), a creature around or a monster already
    /// counted. A name the client doesn't know otherwise counts (a monster out of sight).
    /// </summary>
    private bool CountsAsMonster(string name, bool mayBeItem)
    {
        AcObjectClass? cls = CreatureClassOf(name);
        if (cls is AcObjectClass.Player or AcObjectClass.CombatPet or AcObjectClass.Npc or AcObjectClass.Vendor) return false;
        if (mayBeItem && cls == null && !_monsterStats.IsTracked(name)) return false;
        return true;
    }

    /// <summary>The class of the creature around with that name, or null when none is known.</summary>
    private AcObjectClass? CreatureClassOf(string name)
    {
        RefreshCreatureNames(false);
        if (_creatureClassByName.TryGetValue(name, out AcObjectClass cls)) return cls;
        // A creature that just came: look once more before calling it unknown (at most every 250 ms).
        if (Environment.TickCount64 - _creatureNamesAtMs > 250)
        {
            RefreshCreatureNames(true);
            if (_creatureClassByName.TryGetValue(name, out cls)) return cls;
        }
        return null;
    }

    private void RefreshCreatureNames(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _creatureNamesAtMs < 2000) return;
        _creatureNamesAtMs = now;
        _creatureClassByName.Clear();
        var cache = _objectCache;
        if (cache == null) return;
        foreach (WorldObject wo in cache.GetLandscape())
        {
            if (wo == null || string.IsNullOrEmpty(wo.Name)) continue;
            // The cache can file a player as a monster (a creature whose kind wasn't readable yet):
            // a player's guid says player. A player's name wins over a monster's of the same name.
            AcObjectClass cls = CreatureData.PlayerWeenies.IsPlayerGuid(unchecked((uint)wo.Id)) ? AcObjectClass.Player : wo.ObjectClass;
            if (_creatureClassByName.TryGetValue(wo.Name, out AcObjectClass had) && had == AcObjectClass.Player) continue;
            _creatureClassByName[wo.Name] = cls;
        }
        // Players, NPCs and pets that aren't in the creature list (everything with a place in the
        // world): never a monster. Items stay unknown (an enchantment line from one doesn't count).
        foreach (WorldObject wo in cache.GetLandscapeObjects())
        {
            if (wo == null || string.IsNullOrEmpty(wo.Name)) continue;
            AcObjectClass cls = CreatureData.PlayerWeenies.IsPlayerGuid(unchecked((uint)wo.Id)) ? AcObjectClass.Player : wo.ObjectClass;
            if (cls is AcObjectClass.Player or AcObjectClass.Npc or AcObjectClass.Vendor or AcObjectClass.CombatPet)
                _creatureClassByName[wo.Name] = cls;
        }
    }

    /// <summary>The kill notice names a monster with no numbers yet (killed by its first blow): the longest creature name around in it.</summary>
    private string? CreatureNameIn(string notice)
    {
        RefreshCreatureNames(true);
        string? best = null;
        foreach (var kv in _creatureClassByName)
        {
            if (kv.Value is AcObjectClass.Player or AcObjectClass.CombatPet or AcObjectClass.Npc or AcObjectClass.Vendor) continue;
            if ((best == null || kv.Key.Length > best.Length) && notice.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                best = kv.Key;
        }
        return best;
    }

    // ── Tick (pump thread) ───────────────────────────────────────────────

    /// <summary>Starts the login's counting (or brings the save back after a reload), resets on asking, saves, publishes.</summary>
    private void TickMonsterStats()
    {
        DateTime now = DateTime.UtcNow;
        if (!_monsterStatsStarted && _loginComplete && _playerId != 0)
        {
            // The login's first tick with the character known: after a hot reload the save is there.
            _monsterStatsStarted = true;
            if (TryRestoreMonsterStats(now))
                Host.Log($"[RynthAi] Monster stats carried over from before the reload ({_monsterStats.Count} kinds of monster).");
            else
            {
                _monsterStatsSinceUtc = now;
                CleanupMonsterStatsSaves(now);
            }
            _monsterStatsSaveAtUtc = now + MonsterStatsSaveInterval;
        }
        if (_monsterStatsResetWanted)
        {
            _monsterStatsResetWanted = false;
            ResetMonsterStats("the Damage panel's Reset stats");
        }
        if (_monsterStatsStarted && now >= _monsterStatsSaveAtUtc)
        {
            _monsterStatsSaveAtUtc = now + MonsterStatsSaveInterval;
            if (_monsterStats.Version != _monsterStatsSavedVersion) SaveMonsterStats(now);
        }
        PublishMonsterStatsRows();
        PublishMonsterStatsView(Environment.TickCount64);
    }

    /// <summary>Every monster's numbers start over (/ra mstats reset, the window's Reset, the Damage panel's Reset stats). Pump thread.</summary>
    private int ResetMonsterStats(string by)
    {
        int n = _monsterStats.Count;
        _monsterStats.Reset();
        DateTime now = DateTime.UtcNow;
        _monsterStatsSinceUtc = now;
        // Saved at once, so a reload right after a reset can't bring the old numbers back.
        if (_monsterStatsStarted) SaveMonsterStats(now);
        Host.Log($"[RynthAi] Monster stats started over ({by}; {n} kinds of monster cleared).");
        return n;
    }

    /// <summary>The Damage panel's Reset stats (RynthPluginClearMonsterStats, its thread): the monster stats start over too.</summary>
    internal void RequestMonsterStatsReset() => _monsterStatsResetWanted = true;

    /// <summary>The rows by name for the Damage panel's poll, when something changed.</summary>
    private void PublishMonsterStatsRows()
    {
        if (_monsterStats.Version == _monsterStatsByNameVersion) return;
        _monsterStatsByNameVersion = _monsterStats.Version;
        var byName = new Dictionary<string, MonsterStatsRow>(StringComparer.OrdinalIgnoreCase);
        foreach (MonsterStatsRow r in _monsterStats.Snapshot()) byName[r.Name] = r;
        _monsterStatsByName = byName;
    }

    /// <summary>
    /// A Damage-panel row's "mstats" field and its comma, for the monster named <paramref name="name"/>
    /// (UI/Data/DamageRowStats.cs in the engine reads it); "" for a monster without numbers. Any thread.
    /// </summary>
    internal string MonsterStatsJsonFor(string name)
    {
        if (!_monsterStatsByName.TryGetValue(name, out MonsterStatsRow? r) || r.IsEmpty) return string.Empty;
        return MonsterStatsJson(r) + ",";
    }

    /// <summary>"mstats":{...} for one monster (the field names DamageRowStats reads).</summary>
    internal static string MonsterStatsJson(MonsterStatsRow r)
    {
        CultureInfo ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(160);
        sb.Append("\"mstats\":{")
          .Append("\"sw\":").Append(r.Swings.ToString(ic)).Append(",\"ev\":").Append(r.Evaded.ToString(ic))
          .Append(",\"sp\":").Append(r.SpellsAtIt.ToString(ic)).Append(",\"rs\":").Append(r.Resisted.ToString(ic))
          .Append(",\"ne\":").Append(r.NoEffect.ToString(ic))
          .Append(",\"at\":").Append(r.Attacks.ToString(ic)).Append(",\"yev\":").Append(r.YouEvaded.ToString(ic))
          .Append(",\"ysp\":").Append(r.SpellsAtYou.ToString(ic)).Append(",\"yrs\":").Append(r.YouResisted.ToString(ic))
          .Append(",\"yne\":").Append(r.NoEffectOnYou.ToString(ic))
          .Append(",\"k\":").Append(r.Kills.ToString(ic))
          .Append(",\"dd\":").Append(r.DamageDealt.ToString(ic)).Append(",\"dt\":").Append(r.DamageTaken.ToString(ic))
          .Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// A new view for the fallback window when something changed (at most 4 a second): while it is on
    /// screen, and off screen only for a login or logout, so it never opens on the wrong one.
    /// </summary>
    private void PublishMonsterStatsView(long nowMs)
    {
        bool shown = nowMs - Volatile.Read(ref _monsterStatsDrawnMs) <= 2000;
        if (!shown && _monsterStatsViewVersion != -1 && _monsterStatsViewInWorld == _loginComplete) return;
        if (nowMs - _monsterStatsViewAtMs < 250) return;
        string focus = MonsterStatsFocusName();
        if (_monsterStats.Version == _monsterStatsViewVersion && focus == _monsterStatsViewFocus
            && _loginComplete == _monsterStatsViewInWorld) return;
        _monsterStatsViewAtMs = nowMs;
        _monsterStatsViewVersion = _monsterStats.Version;
        _monsterStatsViewFocus = focus;
        _monsterStatsViewInWorld = _loginComplete;
        _monsterStatsView = new MonsterStatsView(_monsterStats.Snapshot(), focus, _combatEventsStreaming, _loginComplete);
    }

    /// <summary>The monster in focus: the one combat fights, else the selected creature; "" for none.</summary>
    private string MonsterStatsFocusName()
    {
        int id = _combatManager?.activeTargetId ?? 0;
        if (id == 0) id = unchecked((int)_currentTargetId);
        if (id == 0) return string.Empty;
        if (CreatureData.PlayerWeenies.IsPlayerGuid(unchecked((uint)id))) return string.Empty;
        WorldObject? wo = _objectCache?[id];
        return wo != null && wo.ObjectClass != AcObjectClass.Player ? wo.Name ?? string.Empty : string.Empty;
    }

    /// <summary>A real logout ends them (they are per character and login), and the save with them.</summary>
    private void OnLogoutMonsterStats()
    {
        if (_monsterStatsStarted) DeleteMonsterStatsSave();
        _monsterStats.Reset();
        _monsterStatsStarted = false;
        _monsterStatsSavedVersion = -1;
        _creatureClassByName.Clear();
        PublishMonsterStatsRows();
        _monsterStatsView = MonsterStatsView.Empty;
        _monsterStatsViewVersion = -1;
    }

    /// <summary>Plugin unload (hot reload, update, client closing): saved while counting, never deleted.</summary>
    private void ShutdownMonsterStats()
    {
        if (_monsterStatsStarted) SaveMonsterStats(DateTime.UtcNow);
    }

    // ── Saving (MonsterStatsStore) ───────────────────────────────────────

    private void SaveMonsterStats(DateTime now)
    {
        if (_playerId == 0 || !_monsterStatsStarted) return;
        try
        {
            var save = new MonsterStatsSave
            {
                ProcessId = Environment.ProcessId,
                ProcessStartUtcTicks = MonsterStatsStore.ProcessStartTicks,
                CharacterId = _playerId,
                SavedUtcTicks = now.Ticks,
                SinceUtcTicks = _monsterStatsSinceUtc.Ticks,
                Rows = new List<MonsterStatsRow>(_monsterStats.Snapshot()),
            };
            MonsterStatsStore.Save(MonsterStatsDirectory, save);
            _monsterStatsSavedVersion = _monsterStats.Version;
            _monsterStatsSaveFailedLogged = false;
        }
        catch (Exception ex)
        {
            if (_monsterStatsSaveFailedLogged) return;   // one line until a save works again
            _monsterStatsSaveFailedLogged = true;
            Host.Log($"[RynthAi] Couldn't save the monster stats: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryRestoreMonsterStats(DateTime now)
    {
        if (_playerId == 0) return false;
        try
        {
            int pid = Environment.ProcessId;
            if (!MonsterStatsStore.TryLoad(MonsterStatsDirectory, pid, _playerId, out MonsterStatsSave save)) return false;
            if (!MonsterStatsStore.ShouldRestore(save, pid, MonsterStatsStore.ProcessStartTicks, _playerId, now)) return false;
            _monsterStats.Load(save.Rows);
            _monsterStatsSinceUtc = new DateTime(save.SinceUtcTicks, DateTimeKind.Utc);
            _monsterStatsSavedVersion = _monsterStats.Version;
            return true;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] Couldn't read the saved monster stats, starting fresh: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void DeleteMonsterStatsSave()
    {
        if (_playerId == 0) return;
        try { MonsterStatsStore.Delete(MonsterStatsDirectory, Environment.ProcessId, _playerId); }
        catch (Exception ex) { Host.Log($"[RynthAi] Couldn't delete the saved monster stats: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void CleanupMonsterStatsSaves(DateTime now)
    {
        try { MonsterStatsStore.Cleanup(MonsterStatsDirectory, Environment.ProcessId, now); }
        catch { }
    }

    // ── /ra mstats ───────────────────────────────────────────────────────

    /// <summary>
    /// "/ra mstats [monster]" prints a monster's numbers (no name: the monster combat fights, else
    /// the selected one); "list" the monsters with numbers; "reset" starts them over; "show", "hide",
    /// "toggle" the Damage panel (or, on an older engine, the Monster Stats window). Pump thread.
    /// </summary>
    private void HandleMonsterStatsCommand(string[] parts)
    {
        string arg = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2).Trim() : string.Empty;
        switch (arg.ToLowerInvariant())
        {
            case "list":
                ListMonsterStats();
                return;
            case "reset":
            case "clear":
            {
                int n = ResetMonsterStats("/ra mstats reset");
                ReplyOk($"[RynthAi] Monster stats started over ({n} {(n == 1 ? "kind" : "kinds")} of monster cleared).");
                return;
            }
            case "show":
            case "open":
            case "window":
            case "hide":
            case "close":
            case "toggle":
                SetMonsterStatsWindow(arg.ToLowerInvariant());
                return;
            case "help":
            case "?":
                ChatLine("[RynthAi] /ra mstats [monster] — a monster's evades, resists, hits, spells and kills since login (no name: the one you fight or have selected)");
                ChatLine("[RynthAi] /ra mstats list | reset | show | hide — every monster, start over, where they show (the Damage panel's Monsters tab)");
                return;
        }

        MonsterStatsRow? row;
        if (arg.Length == 0)
        {
            string focus = MonsterStatsFocusName();
            row = focus.Length > 0 ? _monsterStats.Get(focus) : null;
            if (row == null)
            {
                ReplyFail((focus.Length > 0
                        ? $"[RynthAi] No monster stats for {focus} since login yet."
                        : "[RynthAi] Select a monster or name one.")
                    + KnownMonsters() + " " + MonsterStatsUsage);
                return;
            }
        }
        else
        {
            row = _monsterStats.Find(arg, out List<string> candidates);
            if (row == null)
            {
                ReplyFail(candidates.Count > 1
                    ? $"[RynthAi] '{arg}' matches {candidates.Count} monsters: {string.Join(", ", candidates)}. Name one exactly. {MonsterStatsUsage}"
                    : $"[RynthAi] No monster stats for '{arg}' since login.{KnownMonsters()} {MonsterStatsUsage}");
                return;
            }
        }
        foreach (string line in MonsterStatsText.ChatLines(row)) ChatLine(line);
    }

    /// <summary>" Monsters with stats: A, B, C." (the most recent few), or " No monster has stats yet."</summary>
    private string KnownMonsters()
    {
        MonsterStatsRow[] rows = _monsterStats.Snapshot();
        if (rows.Length == 0) return " No monster has stats yet.";
        var names = new List<string>();
        for (int i = 0; i < rows.Length && i < 6; i++) names.Add(rows[i].Name);
        return " Monsters with stats: " + string.Join(", ", names) + (rows.Length > 6 ? ", ..." : "") + ".";
    }

    private void ListMonsterStats()
    {
        MonsterStatsRow[] rows = _monsterStats.Snapshot();
        if (rows.Length == 0)
        {
            ChatLine("[RynthAi] No monster stats yet since login: fight something.");
            return;
        }
        ChatLine($"[RynthAi] Monster stats since login ({rows.Length} {(rows.Length == 1 ? "kind" : "kinds")}, most recent first):");
        for (int i = 0; i < rows.Length && i < 10; i++) ChatLine(MonsterStatsText.ListLine(rows[i]));
        if (rows.Length > 10) ChatLine($"[RynthAi]   ... and {rows.Length - 10} more (/ra mstats show).");
    }

    /// <summary>"show" / "hide" / "toggle": the Damage panel where it shows them, else the fallback window.</summary>
    private void SetMonsterStatsWindow(string verb)
    {
        if (!Host.HasSetPanelOpen)
        {
            ReplyFail("[RynthAi] Opening a panel needs RynthCore with plugin windows (plugin API 89+); /ra mstats <monster> prints the numbers.");
            return;
        }
        RynthPanelOpen mode = verb is "hide" or "close" ? RynthPanelOpen.Close : verb == "toggle" ? RynthPanelOpen.Toggle : RynthPanelOpen.Open;
        bool tab = EngineDrawsMonsterStats;
        string key = tab ? DamagePanelKey : MonsterStatsTitle;
        string what = tab ? "Damage panel (Monsters)" : "Monster Stats window";
        int open = Host.SetPanelOpen(key, mode);
        if (open < 0)
        {
            ReplyFail(tab
                ? "[RynthAi] RynthCore has no Damage panel to open."
                : "[RynthAi] The Monster Stats window isn't there yet (RynthCore adds it a few seconds after login). Try again in a moment.");
            return;
        }
        ReplyOk(open == 1
            ? $"[RynthAi] {what} open{(tab ? ": each monster's evades and resists are under its line." : ".")}"
            : $"[RynthAi] {what} closed.");
    }

    /// <summary>The window's Reset (queued from the render thread, run on the tick).</summary>
    private void ResetMonsterStatsFromWindow() => ResetMonsterStats("the Monster Stats window");

    /// <summary>The Session Stats view's "Monster stats" button (the fallback window only): open or closed. Any thread.</summary>
    internal void ToggleMonsterStatsWindow()
    {
        if (Host.HasSetPanelOpen) Host.SetPanelOpen(MonsterStatsTitle, RynthPanelOpen.Toggle);
    }

    // ── Fallback window (render thread) ──────────────────────────────────

    /// <summary>
    /// RynthPluginRenderEmbed "window:monsterstats": the Monster Stats window body, docked or popped
    /// out (the engine's current context). 1 drawn, 0 not ready, -2 failed (three draws in a row threw).
    /// </summary>
    internal int RenderMonsterStats(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || Host.ImGuiContext == IntPtr.Zero) return 0;
        Volatile.Write(ref _monsterStatsDrawnMs, Environment.TickCount64);
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _monsterStatsWindow ??= new MonsterStatsWindow(() => QueueDashAction(ResetMonsterStatsFromWindow));
            want = _monsterStatsWindow.Draw(_monsterStatsView);
            _monsterStatsFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderMonsterStats");
            return ++_monsterStatsFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    /// <summary>
    /// The fallback window goes in the sections manifest on an engine with plugin windows (v89) whose
    /// Damage panel doesn't show the numbers (no GetUiHookFlags bit 8: the owner's API-90 core).
    /// </summary>
    private bool MonsterStatsWindowWanted => Host.HasUiTheme && Host.HasSetPanelOpen && !EngineDrawsMonsterStats;
}
