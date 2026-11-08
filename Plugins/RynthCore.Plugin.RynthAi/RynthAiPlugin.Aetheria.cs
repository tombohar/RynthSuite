using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi;

// Unbox Aetheria When Idle (a player's request, 2026-11-01). The unboxing lives in
// Loot/AetheriaUnboxer.cs; this is the plugin side: when it is safe to unbox, and
// /ra aetheria [unbox|status|on|off].
public sealed partial class RynthAiPlugin
{
    private AetheriaUnboxer CreateAetheriaUnboxer(LegacyUiSettings settings) =>
        new(Host, settings, _objectCache)
        {
            PlayerId = () => _playerId,
            Chat = s => Host.WriteToChat(s, 1),
        };

    /// <summary>
    /// A safe moment to unbox: the server puts the character in peace mode for the clap, so with the
    /// macro on it is the scroll reader's moment (no monster within Monster Range nor on top of us,
    /// not buffing, looting, salvaging or in a portal), and no fight even with Enable Combat off.
    /// With the macro off (an unbox asked for by hand) only no corpse, cast, salvage or portal.
    /// Never while a scroll is read or missiles are crafted (both hold the bot themselves).
    /// </summary>
    internal bool IsSafeToUnboxAetheria(LegacyUiSettings s)
    {
        if (s.IsMacroRunning)
        {
            if (!IsSafeToReadScroll(s)) return false;
            var combat = _combatManager;
            if (combat != null && (combat.HasEngageableTarget || combat.IsUnderCloseAttack)) return false;
        }
        else
        {
            if (_openedContainerId != 0 || _targetCorpseId != 0) return false;
            if (_buffManager != null && _buffManager.PendingSpellId != 0) return false;
            if (_salvageManager?.IsBusy == true) return false;
            if (_navigationEngine?.IsInPortalAction == true) return false;
        }
        try { if (Host.HasIsPortaling && Host.IsPortaling()) return false; } catch { }
        if (_scrollLearner?.IsReading == true) return false;
        if (_missileCraftingManager?.IsCrafting == true) return false;
        return true;
    }

    /// <summary>One unboxer tick; true while an unbox is in flight and the bot should hold still.</summary>
    private bool TickAetheriaUnbox(LegacyUiSettings settings)
    {
        var unboxer = _aetheriaUnboxer;
        if (unboxer == null) return false;
        try
        {
            return unboxer.Tick(IsSafeToUnboxAetheria(settings), PacedBusy);
        }
        catch (Exception ex)
        {
            if (Environment.TickCount64 - _lastAetheriaErrorLogAt > 10_000)
            {
                _lastAetheriaErrorLogAt = Environment.TickCount64;
                Host.Log($"[Aetheria] tick threw: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    private long _lastAetheriaErrorLogAt = -100_000;

    internal const string AetheriaUsage = "Usage: /ra aetheria [status | unbox | on | off]";

    /// <summary>/ra aetheria: status (the default), unbox (every coalesced piece now), on|off (the setting).</summary>
    private void HandleAetheriaCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        var unboxer = _aetheriaUnboxer;
        if (settings == null || unboxer == null) { ReplyFail("[RynthAi] Aetheria: not ready yet (log in first)."); return; }

        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
        switch (verb)
        {
            case "status":
                ChatLine(unboxer.Describe());   // the status asked for
                break;
            case "unbox":
            case "now":
                if (!unboxer.RequestManual(out string reply)) { ReplyFail(reply); break; }
                if (settings.IsMacroRunning && !IsSafeToUnboxAetheria(settings))
                    reply += " It starts once nothing else needs the moment (a fight, a corpse, a buff).";
                ReplyOk(reply);
                break;
            case "on":
            case "off":
                settings.UnboxAetheriaWhenIdle = verb == "on";
                _dashboard?.SaveSettings();
                ReplyOk($"[RynthAi] Unbox Aetheria When Idle: {verb}" +
                        (verb == "on" && !settings.IsMacroRunning ? " (it works while the macro runs; /ra aetheria unbox does it now)." : "."));
                break;
            default:
                ReplyFail($"[RynthAi] Aetheria: unknown option '{parts[2]}'. {AetheriaUsage}");
                break;
        }
    }
}
