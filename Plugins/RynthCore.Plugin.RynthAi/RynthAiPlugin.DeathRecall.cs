using System;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Recall On Death (DeathRecallMode): after a death while the macro was running, once respawned
/// and settled (HoldForDeath), cast a recall spell (DeathRecallSpellId) or use a recall item from
/// the pack (DeathRecallItem: a gem, a scroll, a portal token). RynthAi does nothing else until the
/// teleport lands (or the attempt gives up), then picks the closest nav waypoint and, with Travel
/// Back To My Corpse on, walks to the corpse from there. Off by default: most metas handle a death
/// themselves. Runs whatever the macro does after the death, so Stop Macro On Death still recalls
/// first. A new death cancels it.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private bool _deathRecallArmed;
    private DeathRecallRun? _deathRecall;
    private string _deathRecallWhat = string.Empty;

    /// <summary>The death edge (CheckPlayerDeath): arm the recall for the respawn when it is on and the macro ran.</summary>
    private void ArmDeathRecall(LegacyUiSettings s)
    {
        if (_deathRecall != null)
        {
            Log("[RynthAi] Death recall: died again - the recall in progress is cancelled.");
            _deathRecall = null;
        }
        _deathRecallArmed = s.DeathRecallMode is 1 or 2 && s.IsMacroRunning;
    }

    /// <summary>
    /// The respawn edge (HoldForDeath): start the armed recall. False when nothing was started
    /// (off, not armed, no such item), so the caller goes straight on to the corpse trip.
    /// </summary>
    private bool StartDeathRecall()
    {
        if (!_deathRecallArmed) return false;
        _deathRecallArmed = false;
        var s = _dashboard?.Settings;
        if (s == null || s.DeathRecallMode is not (1 or 2)) return false;

        bool useItem = s.DeathRecallMode == 2;
        if (useItem)
        {
            string name = (s.DeathRecallItem ?? string.Empty).Trim();
            if (name.Length == 0) { ChatLine("[RynthAi] Recall On Death: no item name set (Settings > On Death)."); return false; }
            WorldObject? item = FindRecallItem(name);
            if (item == null) { ChatLine($"[RynthAi] Recall On Death: no \"{name}\" in your inventory - not recalling."); return false; }
            if (!Host.HasUseObject) { ChatLine("[RynthAi] Recall On Death: this engine can't use items - not recalling."); return false; }
            _deathRecallWhat = item.Name;
        }
        else
        {
            if (!Host.HasCastSpell) { ChatLine("[RynthAi] Recall On Death: this engine can't cast spells - not recalling."); return false; }
            _deathRecallWhat = NavPoint.RecallName(s.DeathRecallSpellId);
        }

        _deathRecall = new DeathRecallRun(useItem, Environment.TickCount64, CurrentLandblockForRecall());
        ChatLine($"[RynthAi] Recall On Death: {(useItem ? "using" : "casting")} {_deathRecallWhat}.");
        return true;
    }

    /// <summary>
    /// Every tick after HoldForDeath. True while a recall is in progress: the rest of the tick
    /// (combat, buffing, looting, nav) waits for it.
    /// </summary>
    private bool TickDeathRecall()
    {
        var run = _deathRecall;
        if (run == null) return false;
        var s = _dashboard?.Settings;
        bool useItem = s?.DeathRecallMode == 2;

        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        // A spell needs a wand and magic mode first (the same step a nav Recall waypoint takes).
        bool ready = useItem || portaling || (_combatManager?.EnsureMagicReady() ?? true);
        switch (run.Tick(Environment.TickCount64, portaling, CurrentLandblockForRecall(), ready))
        {
            case DeathRecallRun.Step.Fire:
                if (!FireDeathRecall(s, useItem)) EndDeathRecall("the item is gone from your inventory", success: false);
                return true;
            case DeathRecallRun.Step.Done:
                EndDeathRecall("recalled", success: true);
                return false;
            case DeathRecallRun.Step.Failed:
                EndDeathRecall(run.FailReason, success: false);
                return false;
            default:
                return true;
        }
    }

    private bool FireDeathRecall(LegacyUiSettings? s, bool useItem)
    {
        if (s == null) return false;
        if (useItem)
        {
            WorldObject? item = FindRecallItem((s.DeathRecallItem ?? string.Empty).Trim());
            if (item == null) return false;
            Host.UseFor(unchecked((uint)item.Id), "DeathRecall", $"recall item '{item.Name}' (Recall On Death)");
            Log($"[RynthAi] Death recall: used {item.Name} (0x{(uint)item.Id:X8}).");
            return true;
        }
        uint me = _playerId != 0 ? _playerId : (uint)Host.GetPlayerId();
        if (me == 0) return true;   // retried on the next fire
        Host.CastSpellFor(me, s.DeathRecallSpellId, "DeathRecall", $"{_deathRecallWhat} (Recall On Death)");
        Log($"[RynthAi] Death recall: cast {_deathRecallWhat} ({s.DeathRecallSpellId}).");
        return true;
    }

    private void EndDeathRecall(string why, bool success)
    {
        _deathRecall = null;
        ChatLine(success
            ? $"[RynthAi] Recall On Death: {why} with {_deathRecallWhat}."
            : $"[RynthAi] Recall On Death: {_deathRecallWhat} didn't work ({why}) - carrying on from here.");
        // Wherever we are now, nav rejoins its route at the closest waypoint.
        _navigationEngine?.ResumeFromNearestWaypoint(always: true);
        try { OnRespawnedForOwnCorpse(); }   // Travel Back To My Corpse, from the recall's landing spot
        catch (Exception ex) { Log($"[RynthAi] Own corpse travel not started: {ex.Message}"); }
    }

    /// <summary>The carried or worn item named <paramref name="name"/> (ignoring case), else the first containing it.</summary>
    private WorldObject? FindRecallItem(string name)
    {
        if (_objectCache == null || name.Length == 0) return null;
        var inv = _objectCache.GetDirectInventory(forceRefresh: true).Where(w => w.Name.Length > 0).ToList();
        return inv.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? inv.FirstOrDefault(w => w.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private uint CurrentLandblockForRecall()
    {
        if (!Host.HasGetPlayerPose) return 0;
        return Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) ? cell >> 16 : 0;
    }
}
