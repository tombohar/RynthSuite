// LegacyDashboardRenderer.Instrument.cs — what RynthAi's own dashboard window shows (Dashboard/
// InstrumentDashboard.cs, 2026-10-21): the fields the engine's dashboard read from the snapshot JSON,
// read directly, plus the vitals refresh that the engine's snapshot poll used to drive.
using System;
using System.IO;
using RynthCore.Plugin.RynthAi.Dashboard;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// One frame of the dashboard: the macro, the subsystems, the bot's activity, the target and the vitals.
/// The target is the selection, or the monster RynthAi is fighting (TargetIsFight: combat doesn't
/// select it). TargetHealth is 0..1, or -1 while the server hasn't said. CombatStance is the
/// client's combat mode for the Combat key's icon (Melee, Missile or Magic; in peace, the last one).
/// NavKind is what the Navigation key's icon shows (the route's type, follow, patrol or hunt).
/// BoostLoot: Boost Loot Priority is on where it applies (the Looting key's chest turns gold).
/// NavPriority: Boost Nav Priority is on and the route isn't a finished Once route (the
/// Navigation key's trail turns green over a lane, for a route, follow or patrol).
/// MetaLoaded: a meta with rules is loaded (the Meta key's states are hollow otherwise).
/// </summary>
internal readonly record struct DashboardReadout(
    bool MacroRunning,
    string MetaState,
    string Activity,
    bool Combat, bool Buffing, bool Navigation, bool Looting, bool Meta,
    uint TargetId, string TargetLabel, float TargetHealth,
    bool ShowTargetStaminaMana, uint TargetStamina, uint TargetMaxStamina, uint TargetMana, uint TargetMaxMana,
    uint Health, uint MaxHealth, uint Stamina, uint MaxStamina, uint Mana, uint MaxMana,
    bool Locked, float Opacity,
    string LootPath,
    bool TargetIsFight = false,
    int LootMode = 0,
    bool BoostNav = false,
    int CombatStance = CombatMode.Melee,
    NavTrail.Kind NavKind = NavTrail.Kind.None,
    bool BoostLoot = false,
    bool NavPriority = false,
    bool MetaLoaded = false);

/// <summary>The loaded-files pickers: each list, and the entry in use.</summary>
internal sealed record DashboardFiles(
    string[] Profiles, int ProfileIdx,
    string[] Navs, int NavIdx,
    string[] Loots, int LootIdx,
    string[] Metas, int MetaIdx,
    string[] Buffs, int BuffIdx,
    string ProfileName, string NavName, string LootName, string MetaName, string BuffName,
    int LootMode = 0);

internal sealed partial class LegacyDashboardRenderer
{
    /// <summary>The dashboard's frame. Any thread: plain field reads (strings are replaced, never changed).</summary>
    internal DashboardReadout ReadDashboard()
    {
        string activity = ActivityArbiter.DisplayLabel(_settings.BotAction, _settings.BuffingLabel);
        if (string.IsNullOrWhiteSpace(activity) || activity == "Default") activity = "Idle";
        string state = string.IsNullOrWhiteSpace(_settings.CurrentState) ? "Default" : _settings.CurrentState;
        return new DashboardReadout(
            _settings.IsMacroRunning, state, activity,
            _settings.EnableCombat, _settings.EnableBuffing, _settings.EnableNavigation, _settings.EnableLooting, _settings.EnableMeta,
            _currentTargetId, _targetLabel ?? "NO TARGET", _targetHealthPercent,
            _settings.ShowTargetStaminaMana, _targetStamina, _targetMaxStamina, _targetMana, _targetMaxMana,
            _playerHealth, _playerMaxHealth, _playerStamina, _playerMaxStamina, _playerMana, _playerMaxMana,
            _isLocked, _bgOpacity,
            !string.IsNullOrEmpty(_vendorProfilePath?.Invoke()) ? _vendorProfilePath!.Invoke() : _settings.CurrentLootPath ?? string.Empty,
            LootMode: Loot.LootModes.Clamp(_settings.LootMode),
            BoostNav: BoostNavRule.Active(_settings),   // the flag counts only while a nav route runs
            // Boost Loot Priority waits for no corpse when only rares are looted (the looter's own rule,
            // CorpseOpenController.BoostLootCanRun), so the chest isn't gilded then.
            BoostLoot: _settings.BoostLootPriority && !_settings.LootOnlyRareCorpses && !Loot.LootModes.IsRares(_settings.LootMode),
            // Shown before RUN too (BoostNav above is only while the route runs); a Once route that
            // has walked off its end hunts instead (BoostNavRule), so it isn't shown then.
            NavPriority: _settings.BoostNavPriority && !_settings.NavRouteFinished());
    }

    /// <summary>The five pickers' lists (copied under the lists' lock) and the entry in use. Any thread.</summary>
    internal DashboardFiles ReadDashboardFiles()
    {
        string[] profiles, navs, loots, metas, buffs;
        lock (_profileListsLock)
        {
            profiles = _profiles.ToArray();
            navs = _navFiles.ToArray();
            loots = _lootFiles.ToArray();
            metas = _metaFiles.ToArray();
            buffs = _buffFiles.ToArray();
        }
        string profile = _settings.SelectedProfile ?? "Default";
        return new DashboardFiles(
            profiles, Math.Max(0, Array.IndexOf(profiles, profile)),
            navs, _selectedNavIdx,
            loots, _settings.LootProfileIdx,
            metas, _settings.MetaProfileIdx,
            buffs, _settings.BuffProfileIdx,
            profile,
            string.IsNullOrEmpty(_settings.CurrentNavPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentNavPath),
            LootLineName(),
            string.IsNullOrEmpty(_settings.CurrentMetaPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentMetaPath),
            string.IsNullOrEmpty(_settings.CurrentBuffProfilePath) ? BuiltInBuffProfileLabel : Path.GetFileNameWithoutExtension(_settings.CurrentBuffProfilePath),
            Loot.LootModes.Clamp(_settings.LootMode));
    }

    /// <summary>
    /// The LOOT line: the loot profile, or in rares mode what is looted instead (so a player can
    /// see why nothing else is taken): "Rares + needed mana stones (LootSnob not used)".
    /// </summary>
    internal string LootLineName()
    {
        string profile = string.IsNullOrEmpty(_settings.CurrentLootPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentLootPath);
        if (!Loot.LootModes.IsRares(_settings.LootMode)) return profile;
        return profile == "None" ? "Rares + needed mana stones" : $"Rares + needed mana stones ({profile} not used)";
    }

    /// <summary>
    /// The player's vitals from the engine, for the dashboard. The engine's snapshot poll did this
    /// (BuildSnapshotJson) while its dashboard was open; RynthAi's own window has no poll. Pump thread.
    /// </summary>
    internal void RefreshDashboardVitals() => RefreshPlayerVitals();
}
