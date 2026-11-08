// GroundLootController.cs — picks up loose ground items that match the active loot profile
// (UB-IT AutoGroundLoot, driven by RynthAi's own loot rules and corpse range).
//
//   * Scan: every GroundScanIntervalMs, objects lying in the world (readable ownership with
//     no container / wielder, finite distance) within the corpse max range, minus scenery /
//     creatures / doors / corpses, are classified with the same ClassifyItemAgainstProfile
//     the corpse looter uses. Stat-gated rules get a server appraisal first (a few per scan),
//     then a best-effort classify once GroundAppraiseWaitMs passes.
//   * Pickup: only once the activity arbiter has handed the tick to Looting (HasLootWork sees
//     the matches, so navigation is already stopped). MoveItemExternal(item → player); ACE
//     walks the character to the item and picks it up. Confirmed when the item leaves the
//     ground, retried up to GroundMaxPickupAttempts, then put on a long cooldown.
//   * Corpses always win: ground loot only runs when no corpse is claimed or open.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private const long GroundScanIntervalMs = 300;
    /// <summary>Classify best-effort after waiting this long for an item's appraisal.</summary>
    private const long GroundAppraiseWaitMs = 3_000;
    /// <summary>Server move-to plus pickup animation must finish inside this window before a retry.</summary>
    private const long GroundPickupVerifyMs = 5_000;
    /// <summary>A claimed item whose pickup could never be sent (client stayed busy) is dropped after this.</summary>
    private const long GroundClaimStallMs = 10_000;
    /// <summary>Items that matched no rule are re-checked after this (profile edits, late appraisal).</summary>
    private const long GroundLeaveCooldownMs = 60_000;
    private const long GroundFailCooldownMs = 300_000;
    private const int GroundMaxPickupAttempts = 3;
    private const int GroundAppraisalsPerScan = 2;

    private long _groundLastScanAt;
    private int _groundMatchesInRange;
    private int _groundTargetId;
    private long _groundTargetSince;
    private long _groundPickupRequestedAt;
    private int _groundPickupAttempts;
    private int _groundErrSeqAtSend;   // the refusal counter at the last send (a quest refusal since then)
    private string _groundTargetName = string.Empty;
    private string _groundTargetAction = string.Empty;
    private string _groundTargetRule = string.Empty;
    private bool _groundTargetIsSalvage;
    /// <summary>id → ms before which the item is not re-evaluated (left, failed or gone).</summary>
    private readonly Dictionary<int, long> _groundSkipUntil = new();
    /// <summary>id → ms of the first appraisal request (bounds the wait for stat data).</summary>
    private readonly Dictionary<int, long> _groundAppraiseAt = new();

    /// <summary>A pickup request is out and not yet confirmed — corpse claims wait for it.</summary>
    private bool GroundPickupInFlight => _groundTargetId != 0 && _groundPickupRequestedAt != 0;

    /// <summary>Arbiter input: a claimed ground item, or matching ground items seen by the last scan.</summary>
    private bool HasGroundLootWork(LegacyUiSettings settings)
        => settings.EnableGroundLoot && (_groundTargetId != 0 || _groundMatchesInRange > 0);

    /// <summary>
    /// One ground-loot step (pump thread, called from TickCorpseOpening when no corpse is
    /// claimed or open). Gating for macro / looting / buffing / combat is done by the caller.
    /// </summary>
    private void TickGroundLoot(LegacyUiSettings settings, double maxMeters)
    {
        if (!settings.EnableGroundLoot || _objectCache == null || _playerId == 0)
        {
            ResetGroundLoot(clearCaches: false);
            return;
        }

        long now = CorpseNowMs;
        if (_groundTargetId != 0)
        {
            TickGroundTarget(maxMeters, now);
            return;
        }

        if (now - _groundLastScanAt < GroundScanIntervalMs)
            return;
        _groundLastScanAt = now;
        PruneGroundCollections(now);

        WorldObject? best = ScanGroundLoot(settings, maxMeters, now, out double bestDist,
                                           out string action, out string rule, out bool salvage);
        if (best == null)
            return;

        // HasLootWork reports the match; wait until the arbiter actually gives Looting the tick.
        if (_activity != BotActivity.Looting)
            return;

        _groundTargetId = best.Id;
        _groundTargetSince = now;
        _groundPickupRequestedAt = 0;
        _groundPickupAttempts = 0;
        _groundTargetName = best.Name ?? string.Empty;
        _groundTargetAction = action;
        _groundTargetRule = rule;
        _groundTargetIsSalvage = salvage;
        if (!(_navStopIssued && _navStoppedFor == BotActivity.Looting))
            StopNavFor(BotActivity.Looting);
        LootDiag($"[RynthAi] Ground loot: claimed 0x{(uint)best.Id:X8} '{_groundTargetName}' dist={bestDist:F1}m [{action}] rule='{rule}'.");
        TryRequestGroundPickup(now);
    }

    /// <summary>
    /// Finds the nearest ground item that matches the loot profile and counts all matches
    /// in range (for <see cref="HasGroundLootWork"/>). Requests appraisal for stat-gated items.
    /// </summary>
    private WorldObject? ScanGroundLoot(LegacyUiSettings settings, double maxMeters, long now,
                                        out double bestDist, out string bestAction, out string bestRule, out bool bestSalvage)
    {
        bestDist = double.MaxValue;
        bestAction = bestRule = string.Empty;
        bestSalvage = false;
        _groundMatchesInRange = 0;
        if (_objectCache == null || maxMeters <= 0.25)
            return null;
        // The profile loaders chat on every failed load; a 300 ms scan must not trigger that.
        // Rares mode loads no profile (rares and needed mana stones only).
        string profilePath = (settings.CurrentLootPath ?? string.Empty).Trim().Trim('"');
        if (!Loot.LootModes.IsRares(settings.LootMode) && (profilePath.Length == 0 || !System.IO.File.Exists(profilePath)))
            return null;

        int playerId = unchecked((int)_playerId);
        int appraisalsSent = 0;
        WorldObject? best = null;

        foreach (WorldObject wo in _objectCache.AllKnownObjects())
        {
            if (wo.Id == playerId || IsGroundLootExcludedClass(wo.ObjectClass))
                continue;
            if (_groundSkipUntil.TryGetValue(wo.Id, out long until))
            {
                if (until > now) continue;
                _groundSkipUntil.Remove(wo.Id);
            }
            if (string.IsNullOrWhiteSpace(wo.Name))
                continue;

            // Carried / contained items have no world position, so Distance is MaxValue for them.
            double dist = _objectCache.Distance(playerId, wo.Id);
            if (dist > maxMeters)
                continue;
            if (!_objectCache.IsOnGround(wo.Id))
                continue;

            uint uid = unchecked((uint)wo.Id);
            bool appraised = Host.HasHasAppraisalData && Host.HasAppraisalData(uid);
            if (!appraised && ItemNeedsAppraisalForLoot(wo))
            {
                if (!_groundAppraiseAt.TryGetValue(wo.Id, out long firstAsk))
                {
                    if (appraisalsSent < GroundAppraisalsPerScan && _busyCount == 0 && Host.RequestId(uid))
                    {
                        _groundAppraiseAt[wo.Id] = now;
                        appraisalsSent++;
                    }
                    continue;
                }
                if (now - firstAsk < GroundAppraiseWaitMs)
                    continue;
                // The appraisal never arrived. A rule that decides the item needs it, so judged now
                // its stats read 0 and a later rule (Salvage on its material) could take a keeper:
                // leave it for now and ask again later (until 2026-10-31 it was judged best-effort).
                // An engine that can't say whether the ID is in still judges it best-effort.
                if (Host.HasHasAppraisalData)
                {
                    _groundAppraiseAt.Remove(wo.Id);
                    _groundSkipUntil[wo.Id] = now + GroundLeaveCooldownMs;
                    LootDiag($"[LootEval] ground '{wo.Name}' cls={wo.ObjectClass} dist={dist:F1}m appraised=False -> left for now: no ID came, and a rule that decides it needs one");
                    continue;
                }
            }

            bool keep = ClassifyItemAgainstProfile(wo, settings, out string action, out bool salvage, out string rule);
            LootDiag($"[LootEval] ground '{wo.Name}' cls={wo.ObjectClass} dist={dist:F1}m appraised={appraised} -> " +
                     (keep ? $"KEEP [{action}] rule='{rule}'" : "leave"));
            if (!keep)
            {
                _groundSkipUntil[wo.Id] = now + GroundLeaveCooldownMs;
                continue;
            }

            _groundMatchesInRange++;
            if (dist < bestDist)
            {
                best = wo;
                bestDist = dist;
                bestAction = action;
                bestRule = rule;
                bestSalvage = salvage;
            }
        }

        return best;
    }

    /// <summary>Follows a claimed item: confirm pickup, retry, or give up.</summary>
    private void TickGroundTarget(double maxMeters, long now)
    {
        if (_objectCache == null)
            return;

        int id = _groundTargetId;
        WorldObject? item = _objectCache[id];
        if (item == null)
        {
            // Deleted from the world: decayed or another player took it.
            LootDiag($"[RynthAi] Ground loot: 0x{(uint)id:X8} '{_groundTargetName}' is gone.");
            FinishGroundTarget(now, GroundFailCooldownMs);
            return;
        }

        if (!_objectCache.IsOnGround(id) && _objectCache.GetContainerId(id) != 0)
        {
            LootDiag($"[RynthAi] Ground loot: pickup confirmed for 0x{(uint)id:X8} '{_groundTargetName}'.");
            if (DescribeLootPickup(id, _groundTargetAction, _groundTargetName, _groundTargetRule,
                    _groundTargetIsSalvage, IsStackAtSend(id), where: "ground") is { } described)
                WriteItemInfoLine(described.Line, described.ChatType);
            else
                ChatLine($"[RynthAi] Looted [{_groundTargetAction}] {_groundTargetName} (ground)");
            if (_groundTargetIsSalvage)
                _salvageManager?.EnqueueItem(unchecked((uint)id));
            FinishGroundTarget(now, GroundFailCooldownMs);
            return;
        }

        int playerId = unchecked((int)_playerId);
        double dist = _objectCache.Distance(playerId, id);
        if (dist > Math.Max(maxMeters * 1.5, maxMeters + 2.0))
        {
            LootDiag($"[RynthAi] Ground loot: 0x{(uint)id:X8} '{_groundTargetName}' drifted out of range ({dist:F1}m); releasing.");
            FinishGroundTarget(now, GroundLeaveCooldownMs);
            return;
        }

        if (_groundPickupRequestedAt == 0)
        {
            if (now - _groundTargetSince > GroundClaimStallMs)
            {
                LootDiag($"[RynthAi] Ground loot: could not send pickup for 0x{(uint)id:X8} (client busy); releasing.");
                FinishGroundTarget(now, GroundLeaveCooldownMs);
                return;
            }
            TryRequestGroundPickup(now);
            return;
        }

        // The server won't hand it over yet (a pickup timer, a quest flag: 2026-11-01, #rynth-issues
        // "Stopped looting"): no retry will do better for minutes, and an answer naming no object
        // leaves the client's request slot full (see LeaveQuestRefusedLootItem).
        if (QuestPickupRefusedSince(_groundErrSeqAtSend, id, out uint questErr, out uint questObj))
        {
            LootDiag($"[RynthAi] Ground loot: the server won't hand over 0x{(uint)id:X8} '{_groundTargetName}': {QuestPickupRefusalText(questErr)} (error 0x{questErr:X}, object 0x{questObj:X8}); leaving it.");
            EmptyClientRequestAfterAnonymousRefusal(questObj);
            FinishGroundTarget(now, GroundFailCooldownMs);
            return;
        }

        if (now - _groundPickupRequestedAt < GroundPickupVerifyMs)
            return;

        if (_groundPickupAttempts >= GroundMaxPickupAttempts)
        {
            LootDiag($"[RynthAi] Ground loot: pickup failed for 0x{(uint)id:X8} '{_groundTargetName}' after {_groundPickupAttempts} attempt(s).");
            FinishGroundTarget(now, GroundFailCooldownMs);
            return;
        }

        TryRequestGroundPickup(now);
    }

    /// <summary>Sends the pickup (item → player's main pack) when the client isn't busy.</summary>
    private void TryRequestGroundPickup(long now)
    {
        if (_busyCount > 0 || _groundTargetId == 0)
            return;

        uint uid = unchecked((uint)_groundTargetId);
        Host.SelectItem(uid);
        _groundErrSeqAtSend = LastWeenieErrorSeq();
        bool sent = Host.HasMoveItemExternal
            ? Host.MoveItemExternal(uid, _playerId, 0)
            : Host.UseObject(uid);
        _groundPickupAttempts++;
        _groundPickupRequestedAt = now;
        LootDiag($"[RynthAi] Ground loot: pickup {(sent ? "sent" : "rejected")} for 0x{uid:X8} attempt {_groundPickupAttempts}.");
    }

    /// <summary>Releases the claimed item and parks it for <paramref name="cooldownMs"/>.</summary>
    private void FinishGroundTarget(long now, long cooldownMs)
    {
        if (_groundTargetId != 0)
        {
            _groundSkipUntil[_groundTargetId] = now + cooldownMs;
            _groundAppraiseAt.Remove(_groundTargetId);
        }
        _groundTargetId = 0;
        _groundTargetSince = 0;
        _groundPickupRequestedAt = 0;
        _groundPickupAttempts = 0;
        _groundTargetName = _groundTargetAction = _groundTargetRule = string.Empty;
        _groundTargetIsSalvage = false;
        _groundLastScanAt = 0; // rescan straight away for the next item
    }

    /// <summary>Drops the claim (and optionally the per-item caches, e.g. on logout or /ra groundloot scan).</summary>
    private void ResetGroundLoot(bool clearCaches)
    {
        _groundTargetId = 0;
        _groundTargetSince = 0;
        _groundPickupRequestedAt = 0;
        _groundPickupAttempts = 0;
        _groundTargetName = _groundTargetAction = _groundTargetRule = string.Empty;
        _groundTargetIsSalvage = false;
        _groundMatchesInRange = 0;
        if (!clearCaches)
            return;
        _groundSkipUntil.Clear();
        _groundAppraiseAt.Clear();
        _groundLastScanAt = 0;
    }

    /// <summary>Keeps the per-item maps bounded.</summary>
    private void PruneGroundCollections(long now)
    {
        if (_groundSkipUntil.Count > 512)
        {
            var expired = new List<int>();
            foreach (var kv in _groundSkipUntil)
                if (kv.Value <= now) expired.Add(kv.Key);
            foreach (int k in expired) _groundSkipUntil.Remove(k);
        }
        if (_groundAppraiseAt.Count > 256)
        {
            var stale = new List<int>();
            foreach (var kv in _groundAppraiseAt)
                if (now - kv.Value > GroundLeaveCooldownMs) stale.Add(kv.Key);
            foreach (int k in stale) _groundAppraiseAt.Remove(k);
        }
    }

    /// <summary>Scenery, creatures, fixtures and containers are never picked up from the ground.</summary>
    private static bool IsGroundLootExcludedClass(AcObjectClass cls) => cls is
        AcObjectClass.Unknown or AcObjectClass.Monster or AcObjectClass.Player or AcObjectClass.Npc
        or AcObjectClass.Vendor or AcObjectClass.Portal or AcObjectClass.Door or AcObjectClass.Corpse
        or AcObjectClass.Lifestone or AcObjectClass.Sign or AcObjectClass.Housing or AcObjectClass.Container
        or AcObjectClass.CombatPet or AcObjectClass.Services;

    /// <summary>/ra groundloot [on|off|status|scan]</summary>
    private void HandleGroundLootCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Not logged in."); return; }

        string verb = parts.Length >= 3 ? parts[2].Trim().ToLowerInvariant() : "status";
        switch (verb)
        {
            case "on":
                settings.EnableGroundLoot = true;
                _dashboard?.SaveSettings();
                ReplyOk("[RynthAi] Ground loot ON (uses the loot profile and the corpse max range).");
                break;
            case "off":
                settings.EnableGroundLoot = false;
                ResetGroundLoot(clearCaches: true);
                _dashboard?.SaveSettings();
                ReplyOk("[RynthAi] Ground loot OFF.");
                break;
            case "scan":
                ResetGroundLoot(clearCaches: true);
                ReplyOk("[RynthAi] Ground loot: cleared skip list; rescanning.");
                break;
            default:
                double yards = settings.CorpseApproachRangeMax;
                string target = _groundTargetId != 0 ? $" | target '{_groundTargetName}' attempts={_groundPickupAttempts}" : string.Empty;
                ChatLine($"[RynthAi] Ground loot {(settings.EnableGroundLoot ? "ON" : "OFF")} | range {yards:0.#} yd | " +
                         $"looting {(settings.EnableLooting ? "on" : "off")} | matches in range {_groundMatchesInRange} | " +
                         $"skipped {_groundSkipUntil.Count}{target}");
                break;
        }
    }
}
