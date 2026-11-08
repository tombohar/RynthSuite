// MetaManager.Resume.cs - The meta's runtime out of and back into a MetaManager, for Resume Meta After
// Reload (MetaResume.cs says what goes over and why). Plugin-tick thread (Shutdown and OnLoginComplete
// run on the pump, as Think does).
using System;
using System.Collections.Generic;
using System.Numerics;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed partial class MetaManager
{
    /// <summary>Copies the running meta into <paramref name="s"/> (everything but the identity fields).</summary>
    public void ExportRuntime(MetaResumeState s)
    {
        DateTime now = Clock();
        s.MetaPath = _settings.CurrentMetaPath ?? "";
        s.MacroRunning = _settings.IsMacroRunning;
        s.State = _settings.CurrentState ?? "Default";
        // A state entered (or a reset asked for) that the tick hasn't applied yet: the rules' fired marks
        // belong to the old state, so the restore enters the state afresh, as the next tick would have.
        s.EntryPending = _settings.ForceStateReset
            || !string.Equals(_settings.CurrentState, _lastState, StringComparison.OrdinalIgnoreCase)
            || _settings.MetaCallStackReset;
        s.SecondsInState = Math.Max(0, (now - _stateStartTime).TotalSeconds);
        s.SecondsInStateP = Math.Max(0, (now - _statePStartTime).TotalSeconds);

        // The stacks enumerate top first; saved bottom first so pushing them back keeps the order.
        string[] states = _stateStack.ToArray();
        double?[] paused = _statePPaused.ToArray();
        s.CallStack.Clear();
        for (int i = states.Length - 1; i >= 0; i--)
            s.CallStack.Add((states[i], i < paused.Length ? paused[i] : null));

        s.FiredRules.Clear();
        lock (_settings.MetaRulesLock)
        {
            s.RulesSignature = MetaResumeStore.Signature(_settings.MetaRules);
            if (!s.EntryPending && _settings.MetaRules != null)
                for (int i = 0; i < _settings.MetaRules.Count; i++)
                    if (_settings.MetaRules[i].HasFired) s.FiredRules.Add(i);
        }

        s.WatchdogActive = _watchdogActive;
        s.WatchdogState = _watchdogState;
        s.WatchdogMeters = _watchdogMetersRequired;
        s.WatchdogSeconds = _watchdogSecondsConfig;
        s.WatchdogSecondsLeft = _watchdogActive ? Math.Max(0, (_watchdogExpiration - now).TotalSeconds) : 0;

        _expressions?.ExportVariables(s);

        s.NavPath = _settings.CurrentNavPath ?? "";
        s.RouteLines = _settings.CurrentRoute != null && _settings.CurrentRoute.Points.Count > 0
            ? new List<string>(_settings.CurrentRoute.ToLines())
            : new List<string>();
        s.NavIndex = _settings.ActiveNavIndex;
        s.NavEnabled = _settings.EnableNavigation;
    }

    /// <summary>
    /// Puts a saved runtime back (the caller checked it is this character's, this client's and this meta's):
    /// the next Think carries on in the saved state without entering it again, so nothing that already ran
    /// there (a recall, a route load, a setvar in Default) runs twice.
    /// </summary>
    public void RestoreRuntime(MetaResumeState s)
    {
        DateTime now = Clock();
        _settings.CurrentState = s.State;
        _settings.IsMacroRunning = s.MacroRunning;
        _lastState = s.State;
        _lastMacroRunning = s.MacroRunning;   // no "macro started": that would re-arm every rule
        _settings.MetaCallStackReset = false;
        _settings.ForceStateReset = s.EntryPending;
        _stateStartTime = now.AddSeconds(-s.SecondsInState);
        _statePStartTime = now.AddSeconds(-s.SecondsInStateP);
        _statePResume = null;

        _stateStack.Clear();
        _statePPaused.Clear();
        foreach (var (state, p) in s.CallStack)
        {
            _stateStack.Push(state);
            _statePPaused.Push(p);
        }

        lock (_settings.MetaRulesLock)
        {
            _replayRules.Clear();
            if (_settings.MetaRules != null)
            {
                foreach (MetaRule r in _settings.MetaRules) r.HasFired = false;
                foreach (int i in s.FiredRules)
                    if (i >= 0 && i < _settings.MetaRules.Count) _settings.MetaRules[i].HasFired = true;
            }
        }

        _watchdogActive = s.WatchdogActive && s.WatchdogState.Length > 0;
        _watchdogState = s.WatchdogState;
        _watchdogMetersRequired = s.WatchdogMeters;
        _watchdogSecondsConfig = s.WatchdogSeconds;
        _watchdogExpiration = now.AddSeconds(s.WatchdogSecondsLeft);
        // Measured from where the character stands now (it didn't move during the reload).
        if (_host.HasGetPlayerPose && _host.TryGetPlayerPose(out _, out float x, out float y, out float z, out _, out _, out _, out _))
            _lastWatchdogPos = new Vector3(x, y, z);

        Expressions.RestoreVariables(s);

        _settings.CurrentNavPath = s.NavPath;
        _settings.CurrentRoute = s.RouteLines.Count > 0 ? NavRouteParser.LoadFromLines(s.RouteLines) : new NavRouteParser();
        _settings.ActiveNavIndex = Math.Clamp(s.NavIndex, 0, Math.Max(0, _settings.CurrentRoute.Points.Count));
        _settings.EnableNavigation = s.NavEnabled;
    }
}
