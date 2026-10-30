// LegacyDashboardRenderer.Instrument.cs — what RynthAi's own dashboard window shows (Dashboard/
// InstrumentDashboard.cs, 2026-10-21): the fields the engine's dashboard read from the snapshot JSON,
// read directly, plus the vitals refresh that the engine's snapshot poll used to drive.
using System;
using System.IO;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// One frame of the dashboard: the macro, the subsystems, the bot's activity, the target and the vitals.
/// The target is the selection, or the monster RynthAi is fighting (TargetIsFight: combat doesn't
/// select it). TargetHealth is 0..1, or -1 while the server hasn't said.
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
    bool TargetIsFight = false);

/// <summary>The loaded-files pickers: each list, and the entry in use.</summary>
internal sealed record DashboardFiles(
    string[] Profiles, int ProfileIdx,
    string[] Navs, int NavIdx,
    string[] Loots, int LootIdx,
    string[] Metas, int MetaIdx,
    string[] Buffs, int BuffIdx,
    string ProfileName, string NavName, string LootName, string MetaName, string BuffName);

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
            !string.IsNullOrEmpty(_vendorProfilePath?.Invoke()) ? _vendorProfilePath!.Invoke() : _settings.CurrentLootPath ?? string.Empty);
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
            string.IsNullOrEmpty(_settings.CurrentLootPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentLootPath),
            string.IsNullOrEmpty(_settings.CurrentMetaPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentMetaPath),
            string.IsNullOrEmpty(_settings.CurrentBuffProfilePath) ? BuiltInBuffProfileLabel : Path.GetFileNameWithoutExtension(_settings.CurrentBuffProfilePath));
    }

    /// <summary>
    /// The player's vitals from the engine, for the dashboard. The engine's snapshot poll did this
    /// (BuildSnapshotJson) while its dashboard was open; RynthAi's own window has no poll. Pump thread.
    /// </summary>
    internal void RefreshDashboardVitals() => RefreshPlayerVitals();
}
