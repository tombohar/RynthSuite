// RynthAiPlugin.MetaResume.cs - Resume Meta After Reload (2026-11-07): the meta's runtime is saved when
// the plugin unloads in game (a hot reload) and put back when the same character comes back in the same
// client, so the meta carries on where it was instead of starting Default again (a meta that recalls in
// Default recalled on every reload). What goes over, and the checks: Meta/MetaResume.cs.
using System;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.RynthAi.MonsterStats;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private static readonly string MetaResumeDirectory = MetaResumeStore.DefaultDirectory();

    /// <summary>Shutdown (before the session is torn down): saves the running meta for the next copy of RynthAi.</summary>
    private void SaveMetaResume()
    {
        if (!_loginComplete || _playerId == 0 || _metaManager == null || _dashboard?.Settings == null) return;
        try
        {
            if (!_dashboard.Settings.ResumeMetaOnReload)
            {
                MetaResumeStore.Delete(MetaResumeDirectory, Environment.ProcessId, _playerId);
                return;
            }
            var s = new MetaResumeState
            {
                ProcessId = Environment.ProcessId,
                ProcessStartUtcTicks = MonsterStatsStore.ProcessStartTicks,
                CharacterId = _playerId,
                SavedUtcTicks = DateTime.UtcNow.Ticks,
            };
            _metaManager.ExportRuntime(s);
            MetaResumeStore.Save(MetaResumeDirectory, s);
            Log($"RynthAi: meta saved for the reload: state '{s.State}', macro {(s.MacroRunning ? "on" : "off")}, " +
                $"{s.Variables.Count} variable(s), {s.FiredRules.Count} rule(s) fired, call depth {s.CallStack.Count}, " +
                $"route {(s.RouteLines.Count > 0 ? $"{s.NavPath} at [{s.NavIndex}]" : "none")}.");
        }
        catch (Exception ex)
        {
            Log($"RynthAi: couldn't save the meta for the reload ({ex.GetType().Name}: {ex.Message}); it will start in Default.");
        }
    }

    /// <summary>A real logout: the next login starts the meta in Default, as always.</summary>
    private void ForgetMetaResume()
    {
        if (_playerId == 0) return;
        try { MetaResumeStore.Delete(MetaResumeDirectory, Environment.ProcessId, _playerId); } catch { }
    }

    /// <summary>
    /// OnLoginComplete, right after the new MetaManager is wired: on a hot reload of this character in this
    /// client, with Resume Meta After Reload on and the same meta loaded, puts the saved runtime back.
    /// True when it did (the macro's on/off then comes from the save, not Start Macro On Login).
    /// </summary>
    private bool TryResumeMeta()
    {
        if (_metaManager == null || _dashboard?.Settings == null || _playerId == 0) return false;
        var settings = _dashboard.Settings;
        try
        {
            MetaResumeStore.Cleanup(MetaResumeDirectory, DateTime.UtcNow);
            if (!IsCarriedOverSession(_playerId))
            {
                // A real login (or another character): an old save must never come back later.
                MetaResumeStore.Delete(MetaResumeDirectory, Environment.ProcessId, _playerId);
                return false;
            }
            if (!MetaResumeStore.TryLoad(MetaResumeDirectory, Environment.ProcessId, _playerId, out MetaResumeState s))
                return false;
            // Read once: the next reload saves afresh.
            MetaResumeStore.Delete(MetaResumeDirectory, Environment.ProcessId, _playerId);
            if (!settings.ResumeMetaOnReload)
            {
                Log("RynthAi: hot reload - Resume Meta After Reload is off; the meta starts in Default.");
                return false;
            }
            string signature;
            lock (settings.MetaRulesLock) signature = MetaResumeStore.Signature(settings.MetaRules);
            string? why = MetaResumeStore.WhyNot(s, Environment.ProcessId, MonsterStatsStore.ProcessStartTicks, _playerId,
                settings.CurrentMetaPath ?? "", signature, DateTime.UtcNow);
            if (why != null)
            {
                Log($"RynthAi: hot reload - the meta starts in Default ({why}).");
                return false;
            }
            _metaManager.RestoreRuntime(s);
            _patrolOnLoginPending = false;   // the meta's own route (or patrol) carries on instead
            Log($"RynthAi: hot reload - the meta carries on in state '{s.State}' (macro {(s.MacroRunning ? "on" : "off")}, " +
                $"{s.Variables.Count} variable(s), {s.FiredRules.Count} rule(s) already fired, call depth {s.CallStack.Count}, " +
                $"route {(s.RouteLines.Count > 0 ? $"at [{s.NavIndex}]" : "none")}).");
            Host.WriteToChat($"[RynthAi] Reloaded: the meta carries on in state '{s.State}'.", 1);
            return true;
        }
        catch (Exception ex)
        {
            Log($"RynthAi: couldn't put the meta back after the reload ({ex.GetType().Name}: {ex.Message}); it starts in Default.");
            return false;
        }
    }
}
