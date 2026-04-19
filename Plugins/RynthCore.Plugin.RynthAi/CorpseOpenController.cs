using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private readonly struct CorpseLootDecision
    {
        public bool ShouldLoot { get; init; }
        public string ModeName { get; init; }
        public string Reason { get; init; }
        public string KillerName { get; init; }
        public string PlayerName { get; init; }
        public string LongDesc { get; init; }
        public bool InFellowship { get; init; }
        public bool KillerIsFellow { get; init; }
    }

    private readonly HashSet<int> _completedCorpses = new();
    private readonly HashSet<int> _processedCorpseItems = new();
    private readonly Dictionary<int, int> _corpseItemAttempts = new();
    private readonly Dictionary<int, long> _corpseCooldownUntil = new();
    private readonly HashSet<int> _ownershipSkipLogged = new();
    private readonly HashSet<int> _corpseIdRequested = new(); // corpses whose LongDesc ID was already requested
    private int _busyCount;
    private long _busyCountLastIncrementAt;
    private long _busyCountBecamePositiveAt; // when count first went 0→positive
    private const long BUSY_TIMEOUT_MS = 10_000; // safety: force-clear if stuck >10s
    private int _openedContainerId;
    private int _pendingLootCheckContainerId; // non-corpse container opened natively: run lootcheck on inventory arrival
    private int _corpseItemsEvaluated;  // items checked against loot profile this corpse session
    private int _corpseItemsMatched;    // items that matched a loot rule this corpse session
    private int _targetCorpseId;
    private int _currentLootItemId;
    private long _corpseTargetSince;
    private long _openedContainerAt;
    private long _openedContainerInventoryObservedAt;
    private long _lastCorpseOpenAttemptAt;
    private long _lastCorpseApproachHeartbeatAt;
    private long _lastLootActionAt;
    private long _currentLootItemRequestedAt;
    private long _currentLootItemMoveRequestedAt;
    private bool _currentLootItemRequested;
    private bool _currentLootItemMovePending;
    private bool _corpseAutorunActive;
    private int _currentLootItemMoveAttempts;
    private string _currentLootItemActionLabel = string.Empty;
    private string _currentLootItemName = string.Empty;
    private bool _currentLootItemIsSalvage;
    private bool _corpseIdsRequested;
    private long _corpseIdsRequestedAt;

    private static long CorpseNowMs => Environment.TickCount64;
    private void LootDiag(string message)
    {
        Log(message);
    }

    public override void OnBusyCountIncremented()
    {
        if (_busyCount == 0)
            _busyCountBecamePositiveAt = CorpseNowMs; // record first transition 0→positive
        _busyCount++;
        _busyCountLastIncrementAt = CorpseNowMs;
        if (_combatManager != null) _combatManager.BusyCount = _busyCount;
        if (_buffManager != null) _buffManager.BusyCount = _busyCount;
    }

    public override void OnBusyCountDecremented()
    {
        if (_busyCount > 0)
            _busyCount--;
        if (_busyCount == 0)
            _busyCountBecamePositiveAt = 0; // back to idle — reset the elevation timer
        if (_combatManager != null) _combatManager.BusyCount = _busyCount;
        if (_buffManager != null) _buffManager.BusyCount = _busyCount;
    }

    /// <summary>Safety valve: if busy count has been continuously positive for too long,
    /// force-reset it. Uses the "first went positive" timestamp so that a stream of
    /// new increments cannot keep refreshing the window indefinitely.</summary>
    private void CheckBusyTimeout()
    {
        if (_busyCount > 0 && _busyCountBecamePositiveAt != 0
            && CorpseNowMs - _busyCountBecamePositiveAt > BUSY_TIMEOUT_MS)
        {
            Log($"[RynthAi] Busy count stuck at {_busyCount} for >{BUSY_TIMEOUT_MS}ms — force-clearing.");
            _busyCount = 0;
            _busyCountLastIncrementAt = 0;
            _busyCountBecamePositiveAt = 0;
            if (_combatManager != null) _combatManager.BusyCount = 0;
            if (_buffManager != null) _buffManager.BusyCount = 0;
            // Also reset the engine's native count so accumulated increments don't
            // prevent the next action from being processed.
            if (Host.HasForceResetBusyCount) Host.ForceResetBusyCount();
        }

        PruneCorpseCollections();
    }

    private long _lastCorpsePruneAt;
    private const long CorpsePruneIntervalMs = 60_000; // prune once per minute
    private const int  CorpseSetCap          = 500;    // trim when either set exceeds this

    private void PruneCorpseCollections()
    {
        long now = CorpseNowMs;
        if (now - _lastCorpsePruneAt < CorpsePruneIntervalMs) return;
        _lastCorpsePruneAt = now;

        // _completedCorpses — keep last CorpseSetCap entries (oldest irrelevant; corpses despawn)
        if (_completedCorpses.Count > CorpseSetCap)
        {
            _completedCorpses.Clear();
            Log($"[RynthAi] Pruned _completedCorpses (was >{CorpseSetCap})");
        }

        // _ownershipSkipLogged / _corpseIdRequested — pure spam-prevention, safe to wipe periodically
        if (_ownershipSkipLogged.Count > 200)
        {
            _ownershipSkipLogged.Clear();
            Log($"[RynthAi] Pruned _ownershipSkipLogged");
        }
        if (_corpseIdRequested.Count > 200)
            _corpseIdRequested.Clear();

        // _corpseCooldownUntil — remove expired entries
        var expiredCooldowns = new List<int>();
        foreach (var kvp in _corpseCooldownUntil)
            if (kvp.Value <= now) expiredCooldowns.Add(kvp.Key);
        foreach (var id in expiredCooldowns)
            _corpseCooldownUntil.Remove(id);
    }

    public override void OnViewObjectContents(uint objectId)
    {
        int sid = unchecked((int)objectId);
        if (!IsCorpseLikeObject(sid))
        {
            // Non-corpse container (chest, bag, etc.) — queue a loot-check once inventory arrives.
            _pendingLootCheckContainerId = sid;
            return;
        }

        bool wasCompleted = _completedCorpses.Remove(sid);
        _corpseCooldownUntil.Remove(sid);
        bool sameCorpse = _targetCorpseId == sid;
        bool switchedTarget = _targetCorpseId != 0 && _targetCorpseId != sid;

        _openedContainerId = sid;
        _openedContainerAt = CorpseNowMs;
        _openedContainerInventoryObservedAt = 0;
        _lastLootActionAt = 0;
        _corpseItemsEvaluated = 0;
        _corpseItemsMatched = 0;
        _corpseIdsRequested = false;
        ResetCurrentLootItem();
        if (!sameCorpse || wasCompleted)
        {
            _processedCorpseItems.Clear();
            _corpseItemAttempts.Clear();
        }
        LootDiag($"[RynthAi] Corpse loot: opened container 0x{objectId:X8}.");
        if (wasCompleted)
            LootDiag($"[RynthAi] Corpse loot: manual open cleared completed state for 0x{objectId:X8}.");
        if (switchedTarget)
            LootDiag($"[RynthAi] Corpse loot: switching active corpse target from 0x{(uint)_targetCorpseId:X8} to 0x{objectId:X8}.");

        _targetCorpseId = sid;
        _corpseTargetSince = CorpseNowMs;
        _lastCorpseOpenAttemptAt = 0;
    }

    public override void OnUpdateObjectInventory(uint objectId)
    {
        _objectCache?.MarkInventoryDirty();
        _inventoryManager?.MarkDirty();

        int sid = unchecked((int)objectId);
        if (sid == 0)
            return;

        // Non-corpse container opened natively — run lootcheck on all its items.
        if (_pendingLootCheckContainerId != 0 && sid == _pendingLootCheckContainerId)
        {
            _pendingLootCheckContainerId = 0;
            if (_objectCache != null)
            {
                foreach (WorldObject item in _objectCache.GetContainedItems(sid))
                    InspectLootRuleForItem(item.Id, quiet: true);
            }
            return;
        }

        if (_openedContainerId != 0)
        {
            if (sid != _openedContainerId)
                return;
        }
        else if (_targetCorpseId == 0 || sid != _targetCorpseId || !IsCorpseLikeObject(sid))
        {
            return;
        }

        bool firstObservation = _openedContainerInventoryObservedAt == 0;
        _openedContainerInventoryObservedAt = CorpseNowMs;
        if (firstObservation)
            LootDiag($"[RynthAi] Corpse loot: inventory update observed for 0x{objectId:X8}.");
    }

    public override void OnStopViewingObjectContents(uint objectId)
    {
        int sid = unchecked((int)objectId);
        if (_pendingLootCheckContainerId == sid)
            _pendingLootCheckContainerId = 0;
        bool wasOpenCorpse = _openedContainerId == sid;
        if (_openedContainerId == sid)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
        }

        if (_targetCorpseId == sid)
        {
            if (_completedCorpses.Contains(sid))
            {
                ResetCorpseTarget(releaseState: true);
                return;
            }

            if (wasOpenCorpse)
            {
                _lastCorpseOpenAttemptAt = 0;
                _lastLootActionAt = CorpseNowMs;
                LootDiag($"[RynthAi] Corpse loot: container 0x{objectId:X8} closed before completion; will retry open until timeout.");
            }
        }
    }

    public override void OnVendorOpen(uint vendorId)
    {
        string label = Host.TryGetObjectName(vendorId, out string name) ? name : $"0x{vendorId:X8}";
        Host.WriteToChat($"[RynthAi] Vendor open: {label}", 1);
        _metaManager?.OnVendorOpen(vendorId);
    }

    public override void OnVendorClose(uint vendorId)
    {
        string label = Host.TryGetObjectName(vendorId, out string name) ? name : $"0x{vendorId:X8}";
        Host.WriteToChat($"[RynthAi] Vendor closed: {label}", 1);
        _metaManager?.OnVendorClose(vendorId);
    }

    private void TickCorpseOpening()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
            return;

        SyncCorpseContainerState();

        // ── Hard disable: macro off, looting off, nav-boost, or buffing ──
        // Buffing has absolute priority — halt all loot work until buffs are restored.
        // The current corpse target is retained so looting resumes immediately after.
        if (string.Equals(settings.BotAction, "Buffing", StringComparison.OrdinalIgnoreCase))
            return;

        if (!settings.IsMacroRunning || !settings.EnableLooting || settings.BoostNavPriority)
        {
            ResetCorpseTarget(releaseState: true);
            return;
        }

        // ── Combat takes priority over claiming NEW corpses ───────────────
        // But if we already have an opened container, finish looting it
        // (no movement, no conflict with combat).
        if (settings.EnableCombat
            && _combatManager?.HasTargets == true
            && !settings.BoostLootPriority
            && _openedContainerId == 0)
        {
            return; // Keep our target, just wait — don't ResetCorpseTarget
        }

        // ── Active corpse work — pause navigation ────────────────────────
        if (_openedContainerId != 0 || _targetCorpseId != 0)
            PauseNavigationForCorpse();

        if (_openedContainerId != 0)
        {
            StopCorpseMovement();
            TickOpenCorpseLooting(settings);
            return;
        }

        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        double minMeters = GetCorpseApproachRangeMinMeters(settings, maxMeters);
        if (maxMeters <= 0.25)
            return;

        long now = CorpseNowMs;
        if (_targetCorpseId == 0)
        {
            if (!TryFindNearestCorpse(maxMeters, out WorldObject? corpse, out _))
            {
                // No corpses in range. If we were holding "Looting" from a just-completed
                // corpse (MarkCorpseComplete intentionally skips ResetCorpseTarget so the
                // next tick can claim instantly), release it now so navigation can resume.
                if (settings.BotAction == "Looting")
                {
                    settings.BotAction = "Default";
                    _corpsePausedNav = false;
                }
                return;
            }

            if (corpse == null)
                return;

            _targetCorpseId = corpse.Id;
            _corpseTargetSince = now;
            _lastCorpseOpenAttemptAt = 0;
            LootDiag($"[RynthAi] Corpse loot: claimed corpse target 0x{(uint)corpse.Id:X8}.");
            PauseNavigationForCorpse();
        }

        if (HasCorpseTimedOut(settings, now))
        {
            AbandonCurrentCorpse(now, "timeout");
            return;
        }

        WorldObject? targetCorpse = _objectCache[_targetCorpseId];
        if (targetCorpse == null || targetCorpse.ObjectClass != AcObjectClass.Corpse)
        {
            MarkCorpseCooldown(_targetCorpseId, 3000);
            ResetCorpseTarget(releaseState: true);
            return;
        }

        // Set BotAction to Looting — but don't override Buffing, which is
        // handled by the buff system and just means "casting between loot actions"
        if (settings.BotAction == "Default" || settings.BotAction == "Navigating" || settings.BotAction == "Combat")
            settings.BotAction = "Looting";

        double distanceMeters = _objectCache.Distance(unchecked((int)_playerId), _targetCorpseId);
        if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters == double.MaxValue)
            return;

        if (distanceMeters > Math.Max(maxMeters * 1.5, maxMeters + 2.0))
        {
            MarkCorpseCooldown(_targetCorpseId, 3000);
            ResetCorpseTarget(releaseState: true);
            return;
        }

        if (distanceMeters > minMeters)
        {
            ApproachCorpse(_targetCorpseId);
            return;
        }

        StopCorpseMovement();

        if (_lastCorpseOpenAttemptAt == 0 || now - _lastCorpseOpenAttemptAt >= Math.Max(500, settings.LootOpenRetryMs))
        {
            AttemptOpenCorpse(_targetCorpseId);
            _lastCorpseOpenAttemptAt = now;
        }
    }

    private bool ShouldDriveCorpseOpening(LegacyUiSettings settings)
    {
        if (!settings.IsMacroRunning || !settings.EnableLooting)
            return false;

        if (settings.BoostNavPriority)
            return false;

        // Combat is #1 priority — block new corpse claims while combat is
        // enabled and has ANY targets (scanned or actively engaged).
        // If combat is disabled, targets don't matter — loot freely.
        // Exception: if we already have an opened container, finish looting it
        // since that involves no movement and doesn't conflict with combat.
        if (settings.EnableCombat
            && _combatManager?.HasTargets == true
            && !settings.BoostLootPriority)
        {
            if (_openedContainerId == 0)
                return false;
        }

        return settings.BotAction == "Default"
            || settings.BotAction == "Navigating"
            || settings.BotAction == "Looting"
            || settings.BotAction == "Combat";
    }

    private bool TryFindNearestCorpse(double maxMeters, out WorldObject? corpse, out double distanceMeters)
    {
        corpse = null;
        distanceMeters = double.MaxValue;
        if (_objectCache == null || _playerId == 0)
            return false;

        long now = CorpseNowMs;
        int playerId = unchecked((int)_playerId);
        foreach (WorldObject candidate in _objectCache.GetLandscapeObjects())
        {
            if (candidate.ObjectClass != AcObjectClass.Corpse)
                continue;

            if (candidate.Id == _targetCorpseId || _completedCorpses.Contains(candidate.Id))
                continue;

            if (_corpseCooldownUntil.TryGetValue(candidate.Id, out long blockedUntil))
            {
                if (blockedUntil > now)
                    continue;

                _corpseCooldownUntil.Remove(candidate.Id);
            }

            CorpseLootDecision decision = EvaluateCorpseLootDecision(candidate);
            if (!decision.ShouldLoot)
            {
                if (_ownershipSkipLogged.Add(candidate.Id))
                {
                    LootDiag($"[RynthAi] Corpse loot: ownership skip 0x{(uint)candidate.Id:X8} name='{candidate.Name}' mode='{decision.ModeName}' killer='{decision.KillerName}' me='{decision.PlayerName}' reason='{decision.Reason}'");
                }
                continue;
            }

            double dist = _objectCache.Distance(playerId, candidate.Id);
            if (dist > maxMeters || dist >= distanceMeters)
                continue;

            corpse = candidate;
            distanceMeters = dist;
        }

        return corpse != null;
    }

    private bool ShouldLootCorpse(WorldObject corpse)
    {
        return EvaluateCorpseLootDecision(corpse).ShouldLoot;
    }

    private CorpseLootDecision EvaluateCorpseLootDecision(WorldObject? corpse)
    {
        var settings = _dashboard?.Settings;
        if (corpse == null)
        {
            return new CorpseLootDecision
            {
                ShouldLoot = false,
                ModeName = "Unknown",
                Reason = "Corpse is not available."
            };
        }

        int lootOwnership = settings?.LootOwnership ?? 0;
        string modeName = GetLootOwnershipModeName(lootOwnership);
        string longDesc = corpse.Values(StringValueKey.LongDesc, string.Empty);

        // Corpse LongDesc is only available after the object is identified.
        // Request ID so the killer name is populated for the next check.
        if (string.IsNullOrWhiteSpace(longDesc) && Host.HasRequestId && _corpseIdRequested.Add(corpse.Id))
            Host.RequestId(unchecked((uint)corpse.Id));

        string killerName = ExtractCorpseKillerName(longDesc) ?? string.Empty;
        string myName = GetPlayerNameForLootOwnership();
        bool inFellowship = _fellowshipTracker?.IsInFellowship == true;
        bool killerIsFellow = !string.IsNullOrWhiteSpace(killerName) && _fellowshipTracker?.IsMember(killerName) == true;

        if (settings == null)
        {
            return new CorpseLootDecision
            {
                ShouldLoot = false,
                ModeName = modeName,
                Reason = "Settings are not ready yet.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        if (lootOwnership >= 2)
        {
            return new CorpseLootDecision
            {
                ShouldLoot = true,
                ModeName = modeName,
                Reason = "Loot From is set to All Corpses.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        if (lootOwnership <= 0)
        {
            if (string.IsNullOrWhiteSpace(killerName))
            {
                // LongDesc not loaded yet — ID was requested above.
                // Treat as tentatively lootable so the corpse gets claimed;
                // once the ID response arrives the next check will verify ownership.
                return new CorpseLootDecision
                {
                    ShouldLoot = true,
                    ModeName = modeName,
                    Reason = "Corpse not yet identified — treating as lootable pending ID response.",
                    KillerName = killerName,
                    PlayerName = myName,
                    LongDesc = longDesc,
                    InFellowship = inFellowship,
                    KillerIsFellow = killerIsFellow
                };
            }

            bool matchesPlayer = !string.IsNullOrWhiteSpace(myName)
                && NormalizeCharName(killerName).Equals(NormalizeCharName(myName), StringComparison.OrdinalIgnoreCase);
            return new CorpseLootDecision
            {
                ShouldLoot = matchesPlayer,
                ModeName = modeName,
                Reason = matchesPlayer
                    ? "Corpse killer matches the current character."
                    : "Corpse killer does not match the current character.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        if (!string.IsNullOrWhiteSpace(killerName)
            && !string.IsNullOrWhiteSpace(myName)
            && NormalizeCharName(killerName).Equals(NormalizeCharName(myName), StringComparison.OrdinalIgnoreCase))
        {
            return new CorpseLootDecision
            {
                ShouldLoot = true,
                ModeName = modeName,
                Reason = "Corpse killer matches the current character.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        if (inFellowship)
        {
            if (killerIsFellow)
            {
                return new CorpseLootDecision
                {
                    ShouldLoot = true,
                    ModeName = modeName,
                    Reason = "Corpse killer is a tracked fellowship member.",
                    KillerName = killerName,
                    PlayerName = myName,
                    LongDesc = longDesc,
                    InFellowship = inFellowship,
                    KillerIsFellow = killerIsFellow
                };
            }

            // When fellowship membership data lags behind the live client, allow the server
            // to be the final authority instead of skipping potentially valid fellowship corpses.
            return new CorpseLootDecision
            {
                ShouldLoot = true,
                ModeName = modeName,
                Reason = "In fellowship mode, RynthAi will let the server decide when killer ownership is not fully resolved yet.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        bool killerKnown = !string.IsNullOrWhiteSpace(killerName);
        bool fallbackMatchesPlayer = killerKnown
            && !string.IsNullOrWhiteSpace(myName)
            && NormalizeCharName(killerName).Equals(NormalizeCharName(myName), StringComparison.OrdinalIgnoreCase);
        bool tentative = !killerKnown; // LongDesc not loaded yet
        return new CorpseLootDecision
        {
            ShouldLoot = fallbackMatchesPlayer || tentative,
            ModeName = modeName,
            Reason = tentative
                ? "Corpse not yet identified — treating as lootable pending ID response."
                : fallbackMatchesPlayer
                    ? "Not in a fellowship, so the corpse is treated as your own kill."
                    : "Not in a fellowship and the corpse killer does not match the current character.",
            KillerName = killerName,
            PlayerName = myName,
            LongDesc = longDesc,
            InFellowship = inFellowship,
            KillerIsFellow = killerIsFellow
        };
    }

    private string GetPlayerNameForLootOwnership()
    {
        if (_playerId == 0)
            return string.Empty;

        return Host.TryGetObjectName(_playerId, out string playerName) && !string.IsNullOrWhiteSpace(playerName)
            ? playerName
            : string.Empty;
    }

    /// <summary>Strip GM/admin sigils (leading '+', surrounding whitespace) so admin characters
    /// whose player-object name is "+Buffi" match the "Killed by Buffi." string in corpse LongDesc.</summary>
    private static string NormalizeCharName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string trimmed = name.Trim();
        // Admin/GM sigils appear at the start of the display name (+, @, etc.) but
        // are stripped out of the "Killed by X" string the server writes to LongDesc.
        while (trimmed.Length > 0 && (trimmed[0] == '+' || trimmed[0] == '@' || trimmed[0] == '#'))
            trimmed = trimmed.Substring(1);
        return trimmed.Trim();
    }

    private static string? ExtractCorpseKillerName(string longDesc)
    {
        if (string.IsNullOrWhiteSpace(longDesc))
            return null;

        const string prefix = "Killed by ";
        int idx = longDesc.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        string remainder = longDesc[(idx + prefix.Length)..];
        remainder = remainder.TrimEnd('.', ' ', '\n', '\r');
        return string.IsNullOrWhiteSpace(remainder) ? null : remainder;
    }

    private static string GetLootOwnershipModeName(int lootOwnership)
    {
        return lootOwnership switch
        {
            <= 0 => "My Kills Only",
            1 => "Fellowship Kills",
            _ => "All Corpses"
        };
    }

    private void TickOpenCorpseLooting(LegacyUiSettings settings)
    {
        int corpseId = _openedContainerId;
        if (corpseId == 0)
            return;

        if (_targetCorpseId == 0)
        {
            _targetCorpseId = corpseId;
            _corpseTargetSince = CorpseNowMs;
        }

        if (_completedCorpses.Contains(corpseId))
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
            ResetCorpseTarget(releaseState: true);
            return;
        }

        long now = CorpseNowMs;
        if (_openedContainerAt == 0)
            _openedContainerAt = now;

        if (HasCorpseTimedOut(settings, now))
        {
            AbandonCurrentCorpse(now, "timeout");
            return;
        }

        long openAge = now - _openedContainerAt;

        // Brief settle after open for client to populate container
        if (openAge < Math.Max(50, settings.LootContentSettleMs))
            return;

        // Wait for inventory update event from server
        if (_openedContainerInventoryObservedAt == 0)
        {
            if (openAge < Math.Max(300, settings.LootEmptyCorpseMs))
                return;
            _openedContainerInventoryObservedAt = _openedContainerAt;
            LootDiag($"[RynthAi] Corpse loot: no inventory update for 0x{(uint)corpseId:X8}; probing after {openAge}ms.");
        }

        // Handle pending pickup confirmation (don't block evaluation — just check)
        if (_currentLootItemMovePending)
        {
            TickPendingCorpsePickup(settings);
            if (_currentLootItemMovePending)
                return; // Still waiting for this pickup to clear
        }

        // Pre-classify items and batch-request IDs only for items that need them.
        //
        // Each Host.RequestId() call increments ClientUISystem::m_cBusy (busy count).
        // After MarkCorpseCompleted, the _busyCount > 0 guard in TickCorpseOpening
        // blocks AttemptOpenCorpse until every ID response clears the count — which
        // takes 2-4 seconds for a full corpse batch.  Fix: classify items whose class
        // has no stat-based rules immediately (no ID needed); only request ID for items
        // where the profile has numeric/spell conditions that require appraisal data.
        if (!_corpseIdsRequested)
        {
            _corpseIdsRequested = true;
            _corpseIdsRequestedAt = now;
            int requested = 0;
            int preClassified = 0;

            foreach (WorldObject item in _objectCache.GetContainedItems(corpseId))
            {
                if (_processedCorpseItems.Contains(item.Id)) continue;

                bool hasData = Host.HasHasAppraisalData && Host.HasAppraisalData(unchecked((uint)item.Id));
                if (hasData) continue; // Already has full appraisal — will evaluate below

                bool hasName = !string.IsNullOrWhiteSpace(item.Name);

                // Items with a name whose class has no stat-based loot rules can be
                // classified immediately from name/class data — no ID request needed.
                if (hasName && !ItemNeedsAppraisalForLoot(item))
                {
                    // Pre-classify: if no match, mark processed so we never re-evaluate.
                    // Items that DO match (e.g. name-only rules) stay unprocessed and will
                    // be picked up in the eval loop below on this same tick.
                    if (!ClassifyItemAgainstProfile(item, out _, out _, out _))
                        _processedCorpseItems.Add(item.Id);
                    preClassified++;
                    continue; // No busy-count-inflating ID request
                }

                // Item needs appraisal for accurate classification — request ID.
                Host.RequestId(unchecked((uint)item.Id));
                requested++;
            }

            if (requested > 0)
            {
                LootDiag($"[RynthAi] Corpse loot: requested {requested} item ID(s) (pre-classified {preClassified}) for 0x{(uint)corpseId:X8}.");
                return; // One tick for server responses to start arriving
            }
            if (preClassified > 0)
                LootDiag($"[RynthAi] Corpse loot: pre-classified {preClassified} item(s) without ID for 0x{(uint)corpseId:X8}.");
            // Fall through: all items handled without ID requests — evaluate immediately
        }

        // Evaluate ALL items with data in one pass — do NOT gate evaluation on
        // busy count.  Only the actual UseObject pickup needs busy == 0.
        // This lets us classify every item on the corpse as soon as ID data
        // arrives, instead of waiting for each RequestId to fully clear.
        int visibleItemCount = 0;
        int pendingDataCount = 0;
        bool assessTimedOut = now - _corpseIdsRequestedAt >= Math.Max(100, settings.LootAssessWindowMs);

        int    firstMatchId     = 0;
        string matchItemName    = string.Empty;
        string matchActionLabel = string.Empty;
        string matchRuleLabel   = string.Empty;
        bool   matchIsSalvage   = false;

        foreach (WorldObject item in _objectCache.GetContainedItems(corpseId))
        {
            visibleItemCount++;
            if (_processedCorpseItems.Contains(item.Id))
                continue;

            bool hasAppraisalData = Host.HasHasAppraisalData && Host.HasAppraisalData(unchecked((uint)item.Id));
            bool hasName = !string.IsNullOrWhiteSpace(item.Name);

            if (!hasAppraisalData && !hasName)
            {
                if (!assessTimedOut)
                {
                    pendingDataCount++;
                    continue;
                }
                // Timed out waiting for data — skip this item
                _processedCorpseItems.Add(item.Id);
                continue;
            }

            // Evaluate against loot profile — pure classification, no UseObject.
            if (ClassifyItemAgainstProfile(item, out string actionLabel, out bool isSalvage, out string ruleLabel))
            {
                // First match wins — save it for pickup below
                if (firstMatchId == 0)
                {
                    firstMatchId     = item.Id;
                    matchItemName    = item.Name ?? string.Empty;
                    matchActionLabel = actionLabel;
                    matchRuleLabel   = ruleLabel;
                    matchIsSalvage   = isSalvage;
                }
                // Don't break — keep evaluating to mark non-matches processed
            }
            else
            {
                // No match — mark processed so we never re-evaluate
                _processedCorpseItems.Add(item.Id);
            }
        }

        // Empty corpse check
        if (visibleItemCount == 0)
        {
            if (openAge >= Math.Max(200, settings.LootEmptyCorpseMs))
            {
                LootDiag($"[RynthAi] Corpse loot: no items on 0x{(uint)corpseId:X8} after {openAge}ms; marking complete.");
                MarkCorpseCompleted(corpseId);
            }
            return;
        }

        // Matched item — attempt pickup (gated on busy count)
        if (firstMatchId != 0)
        {
            if (_busyCount > 0)
                return; // Wait for busy to clear before pickup only

            ChatLine($"[RynthAi] Loot rule: {matchItemName} -> [{matchActionLabel}] {matchRuleLabel}");
            _currentLootItemId = firstMatchId;
            Host.SelectItem(unchecked((uint)firstMatchId));
            LootDiag($"[RynthAi] Corpse loot: using 0x{(uint)firstMatchId:X8} '{matchItemName}' from corpse 0x{(uint)corpseId:X8}.");
            bool moved = Host.UseObject(unchecked((uint)firstMatchId));
            if (!moved)
            {
                LootDiag($"[RynthAi] Corpse loot: use request failed for 0x{(uint)firstMatchId:X8}.");
                if (IncrementCorpseItemAttempt(firstMatchId) >= 3)
                    _processedCorpseItems.Add(firstMatchId);
                ResetCurrentLootItem();
                _lastLootActionAt = now;
                return;
            }
            _currentLootItemMovePending      = true;
            _currentLootItemMoveRequestedAt  = now;
            _currentLootItemMoveAttempts     = 1;
            _currentLootItemActionLabel      = matchActionLabel;
            _currentLootItemName             = matchItemName;
            _currentLootItemIsSalvage        = matchIsSalvage;
            _lastLootActionAt                = now;
            return;
        }

        // All items evaluated, no matches left — mark corpse complete
        if (pendingDataCount == 0)
        {
            LootDiag($"[RynthAi] Corpse loot: all {visibleItemCount} items on 0x{(uint)corpseId:X8} evaluated; marking complete.");
            MarkCorpseCompleted(corpseId);
        }
    }

    private void TickPendingCorpsePickup(LegacyUiSettings settings)
    {
        if (_objectCache == null || _openedContainerId == 0 || _currentLootItemId == 0)
        {
            ResetCurrentLootItem();
            return;
        }

        long now = CorpseNowMs;
        WorldObject? currentItem = _objectCache[_currentLootItemId];
        int currentContainer = currentItem?.Container ?? 0;
        bool stillVisibleOnCorpse = IsCorpseItemVisible(_openedContainerId, _currentLootItemId);

        // Check every tick — confirm immediately as soon as the item leaves the corpse.
        // Don't gate this on a timer; the timer only applies to the RETRY path below.
        if (currentItem == null || !stillVisibleOnCorpse || (currentContainer != 0 && currentContainer != _openedContainerId))
        {
            ConfirmCurrentLootItemMoved(
                now,
                currentItem == null
                    ? "item left cache after use"
                    : (!stillVisibleOnCorpse
                        ? "item no longer listed on corpse"
                        : $"new container=0x{(uint)currentContainer:X8}"));
            return;
        }

        // Item still on corpse — wait before retrying the UseObject.
        long verifyWindowMs = Math.Max(150, settings.LootRetryTimeoutMs);
        if (now - _currentLootItemMoveRequestedAt < verifyWindowMs)
            return;

        if (_currentLootItemMoveAttempts >= 3)
        {
            LootDiag($"[RynthAi] Corpse loot: pickup failed for 0x{(uint)_currentLootItemId:X8}; container remained 0x{(uint)_openedContainerId:X8} after {_currentLootItemMoveAttempts} attempt(s).");
            _processedCorpseItems.Add(_currentLootItemId);
            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        if (_busyCount > 0)
            return; // Client is busy — don't queue retry

        Host.SelectItem(unchecked((uint)_currentLootItemId));
        bool retried = Host.UseObject(unchecked((uint)_currentLootItemId));
        _currentLootItemMoveAttempts++;
        _currentLootItemMoveRequestedAt = now;
        _lastLootActionAt = now;
        LootDiag($"[RynthAi] Corpse loot: retrying use for 0x{(uint)_currentLootItemId:X8} attempt {_currentLootItemMoveAttempts}.");

        if (!retried && _currentLootItemMoveAttempts >= 3)
        {
            _processedCorpseItems.Add(_currentLootItemId);
            ResetCurrentLootItem();
        }
    }

    private int FindNextCorpseItem(int corpseId, out int visibleItemCount)
    {
        visibleItemCount = 0;
        if (_objectCache == null)
            return 0;

        foreach (WorldObject item in _objectCache.GetContainedItems(corpseId))
        {
            visibleItemCount++;
            if (item.Id == 0 || _processedCorpseItems.Contains(item.Id))
                continue;

            return item.Id;
        }

        return 0;
    }

    private bool IsCorpseItemVisible(int corpseId, int itemId)
    {
        if (_objectCache == null || corpseId == 0 || itemId == 0)
            return false;

        foreach (WorldObject item in _objectCache.GetContainedItems(corpseId))
        {
            if (item.Id == itemId)
                return true;
        }

        return false;
    }

    private void ConfirmCurrentLootItemMoved(long now, string reason)
    {
        if (_currentLootItemId == 0)
            return;

        _processedCorpseItems.Add(_currentLootItemId);
        LootDiag($"[RynthAi] Corpse loot: pickup confirmed for 0x{(uint)_currentLootItemId:X8}; {reason}.");
        if (!string.IsNullOrWhiteSpace(_currentLootItemName))
            ChatLine($"[RynthAi] Looted [{_currentLootItemActionLabel}] {_currentLootItemName}");

        if (_currentLootItemIsSalvage)
            _salvageManager?.EnqueueItem(unchecked((uint)_currentLootItemId));

        ResetCurrentLootItem();
        _lastLootActionAt = now;
    }

    private bool IsCorpseNavigationClaimActive(LegacyUiSettings? settings = null)
    {
        settings ??= _dashboard?.Settings;
        if (settings == null || !settings.IsMacroRunning || !settings.EnableLooting)
            return false;

        if (settings.BoostNavPriority)
            return false;

        // Active corpse target or open container — always block nav
        if (_targetCorpseId != 0 || _openedContainerId != 0 || _corpseAutorunActive)
            return true;

        // No active target, but check if unlooted corpses exist within range.
        // This prevents navigation from moving the player away between corpses.
        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        if (maxMeters <= 0.25)
            return false;

        return HasUnlootedCorpsesInRange(maxMeters);
    }

    /// <summary>Returns true if at least one lootable, non-completed corpse is within range.</summary>
    private bool HasUnlootedCorpsesInRange(double maxMeters)
    {
        if (_objectCache == null || _playerId == 0)
            return false;

        long now = CorpseNowMs;
        int playerId = unchecked((int)_playerId);
        foreach (WorldObject candidate in _objectCache.GetLandscapeObjects())
        {
            if (candidate.ObjectClass != AcObjectClass.Corpse)
                continue;
            if (_completedCorpses.Contains(candidate.Id))
                continue;
            if (_corpseCooldownUntil.TryGetValue(candidate.Id, out long blockedUntil) && blockedUntil > now)
                continue;
            if (!ShouldLootCorpse(candidate))
                continue;

            double dist = _objectCache.Distance(playerId, candidate.Id);
            if (dist <= maxMeters)
                return true;
        }

        return false;
    }

    private void PauseNavigationForCorpse()
    {
        if (_corpsePausedNav)
            return;

        _navigationEngine?.Stop();
        if (Host.HasStopCompletely)
            Host.StopCompletely();
        _corpsePausedNav = true;

        int corpseId = _openedContainerId != 0 ? _openedContainerId : _targetCorpseId;
        var settings = _dashboard?.Settings;
        if (settings != null && !string.Equals(settings.BotAction, "Looting", StringComparison.OrdinalIgnoreCase))
            settings.BotAction = "Looting";

        if (corpseId != 0)
            LootDiag($"[RynthAi] Corpse loot: pausing navigation for corpse 0x{(uint)corpseId:X8}.");
    }

    /// <summary>
    /// Pure classification — evaluates an item against the active loot profile
    /// without performing any pickup or side effects.  Returns true if the item
    /// matched a loot/salvage rule.
    /// </summary>
    private bool ClassifyItemAgainstProfile(WorldObject item, out string actionLabel, out bool isSalvage, out string ruleLabel)
    {
        actionLabel = string.Empty;
        isSalvage   = false;
        ruleLabel   = string.Empty;

        if (TryLoadNativeLootProfile(out LootProfile nativeProfile, out _))
        {
            _corpseItemsEvaluated++;
            var (nativeAction, nativeRule) = LootEvaluator.Classify(nativeProfile, item, _charSkills);
            if (nativeRule == null)
            {
                LootDiag($"[RynthAi] Corpse loot: no loot rule matched 0x{(uint)item.Id:X8} '{item.Name}'.");
                return false;
            }
            _corpseItemsMatched++;
            actionLabel = nativeAction.ToString();
            isSalvage   = nativeAction == LootAction.Salvage;
            ruleLabel   = string.IsNullOrWhiteSpace(nativeRule.Name) ? "rule" : nativeRule.Name.Trim();
            return true;
        }

        // VTank .utl profile fallback
        if (!TryLoadLootProfile(string.Empty, out VTankLootProfile vtankProfile, out _))
            return false;

        _corpseItemsEvaluated++;
        VTankLootContext lootCtx = new(Host, _playerId) { Cache = _objectCache };
        VTankLootRule? matchedRule = null;
        int matchedRuleIndex      = -1;
        for (int ri = 0; ri < vtankProfile.Rules.Count; ri++)
        {
            if (vtankProfile.Rules[ri].IsMatch(item, lootCtx))
            {
                matchedRule      = vtankProfile.Rules[ri];
                matchedRuleIndex = ri;
                break;
            }
        }

        if (matchedRule == null)
        {
            LootDiag($"[RynthAi] Corpse loot: no loot rule matched 0x{(uint)item.Id:X8} '{item.Name}'.");
            return false;
        }

        _corpseItemsMatched++;
        ruleLabel = string.IsNullOrWhiteSpace(matchedRule.Name)
            ? $"#{matchedRuleIndex}"
            : matchedRule.Name.Trim();
        actionLabel = matchedRule.Action.ToString();
        isSalvage   = matchedRule.Action == VTankLootAction.Salvage;
        return true;
    }

    /// <summary>
    /// Returns true if the active loot profile has at least one rule that
    /// (a) could match this item's object class, AND
    /// (b) uses conditions that require appraisal data (numeric stats, spell counts, etc.)
    ///
    /// When false, the item can be definitively evaluated from name/class data alone,
    /// so we skip Host.RequestId() to avoid inflating the client busy count.
    /// </summary>
    private bool ItemNeedsAppraisalForLoot(WorldObject item)
    {
        if (TryLoadNativeLootProfile(out LootProfile nativeProfile, out _))
            return NativeProfileNeedsAppraisalForClass(nativeProfile, item.ObjectClass);

        if (TryLoadLootProfile(string.Empty, out VTankLootProfile vtankProfile, out _))
            return VTankProfileNeedsAppraisalForClass(vtankProfile, item.ObjectClass);

        return false; // No active profile — no ID request needed
    }

    private static bool NativeProfileNeedsAppraisalForClass(LootProfile profile, AcObjectClass cls)
    {
        foreach (var rule in profile.Rules)
        {
            if (!rule.Enabled) continue;

            // Determine whether the rule targets this class
            bool hasClassFilter = false;
            bool classMatches   = false;
            foreach (var cond in rule.Conditions)
            {
                if (cond is ObjectClassCondition occ)
                {
                    hasClassFilter = true;
                    if ((AcObjectClass)(int)occ.ObjectClass == cls)
                        classMatches = true;
                }
            }
            if (hasClassFilter && !classMatches) continue; // Rule filters out this class

            // Rule can match this class — does it require appraisal data?
            foreach (var cond in rule.Conditions)
            {
                if (cond is LongValKeyGECondition
                        or LongValKeyLECondition
                        or LongValKeyECondition
                        or LongValKeyNECondition
                        or LongValKeyFlagCondition
                        or DoubleValKeyGECondition
                        or DoubleValKeyLECondition
                        or TotalRatingsGECondition
                        or MinDamageGECondition
                        or DamagePercentGECondition)
                    return true;
            }
        }
        return false;
    }

    private static bool VTankProfileNeedsAppraisalForClass(VTankLootProfile profile, AcObjectClass cls)
    {
        // Requirement types that need appraisal data from the server
        static bool TypeNeedsAppraisal(int t) => t is
            2 or 3 or 4 or 5 or 6 or 8 or 9 or 10 or 11 or 12 or 13 or
            14 or 15 or 16 or 17 or 2000 or 2001 or 2003 or 2005 or 2006 or 2007 or 2008;

        // Number of data lines consumed by each requirement type (mirrors VTankLootParser)
        static int DataCount(int t) => t switch
        {
            0    => 1, 1    => 2, 2    => 2, 3    => 2, 4    => 2, 5    => 2,
            6    => 1, 7    => 1, 8    => 1, 9    => 3, 10   => 1, 11   => 2,
            12   => 2, 13   => 2, 14   => 5, 15   => 6, 16   => 6, 17   => 2,
            1000 => 2, 1001 => 1, 1002 => 1, 1003 => 1, 1004 => 3,
            2000 => 1, 2001 => 1, 2003 => 2, 2005 => 2, 2006 => 1, 2007 => 1, 2008 => 3,
            9999 => 1, _    => 0,
        };

        int targetClass = (int)cls;

        foreach (var rule in profile.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RawInfoLine)) continue;

            string[] parts = rule.RawInfoLine.Split(';');
            if (parts.Length < 3) continue; // 0 or 2 parts = no requirements

            bool hasClassReq   = false;
            bool classMatches  = false;
            bool needsAppraisal = false;

            var dataQueue = new System.Collections.Generic.Queue<string>(rule.RawDataLines);

            for (int i = 2; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int type))
                    continue;

                int dataCount = DataCount(type);

                if (type == 7) // ObjectClass requirement
                {
                    hasClassReq = true;
                    if (dataQueue.Count > 0)
                    {
                        if (int.TryParse(dataQueue.Peek(), System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int cls2)
                            && cls2 == targetClass)
                            classMatches = true;
                    }
                }
                else if (TypeNeedsAppraisal(type))
                {
                    needsAppraisal = true;
                }

                for (int d = 0; d < dataCount && dataQueue.Count > 0; d++)
                    dataQueue.Dequeue();
            }

            bool ruleApplies = !hasClassReq || classMatches;
            if (ruleApplies && needsAppraisal) return true;
        }

        return false;
    }

    private void MarkCorpseCompleted(int corpseId)
    {
        if (corpseId == 0)
            return;

        _completedCorpses.Add(corpseId);
        _corpseCooldownUntil.Remove(corpseId);
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        _corpseIdsRequested = false;
        ResetCurrentLootItem();

        // Clear container state immediately. Do NOT send UseObject(corpse) to close it —
        // that increments busy count and blocks the next open until the server acks the close.
        // The server will auto-close this corpse when the next UseObject open is sent.
        // SyncCorpseContainerState guards against re-claiming via _completedCorpses.
        if (_openedContainerId == corpseId)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
            LootDiag($"[RynthAi] Corpse loot: completed 0x{(uint)corpseId:X8}, cleared container state.");
        }

        if (_corpseItemsEvaluated > 0 && _corpseItemsMatched == 0)
        {
            string corpseName = _objectCache?[corpseId]?.Name ?? $"0x{(uint)corpseId:X8}";
            ChatLine($"[RynthAi] Nothing on {corpseName} matches the loot profile.");
        }
        LootDiag($"[RynthAi] Corpse loot: completed 0x{(uint)corpseId:X8}, releasing container.");

        // Lightweight target reset — keep BotAction="Looting" and _corpsePausedNav
        // so the next tick immediately claims the next corpse without a gap where
        // the proactive buff check or navigation could jump in.
        StopCorpseMovement();
        _targetCorpseId = 0;
        _corpseTargetSince = 0;
        _lastCorpseOpenAttemptAt = 0;
        _lastLootActionAt = 0;
    }

    private int IncrementCorpseItemAttempt(int itemId)
    {
        if (!_corpseItemAttempts.TryGetValue(itemId, out int attempts))
            attempts = 0;

        attempts++;
        _corpseItemAttempts[itemId] = attempts;
        return attempts;
    }

    private void ResetCurrentLootItem()
    {
        _currentLootItemId = 0;
        _currentLootItemRequested = false;
        _currentLootItemRequestedAt = 0;
        _currentLootItemMovePending = false;
        _currentLootItemMoveRequestedAt = 0;
        _currentLootItemMoveAttempts = 0;
        _currentLootItemActionLabel = string.Empty;
        _currentLootItemName = string.Empty;
        _currentLootItemIsSalvage = false;
    }

    private void ApproachCorpse(int corpseId)
    {
        if (!TryGetHeadingToObject(corpseId, out double desiredDeg, out double absError))
            return;

        if (absError > 18.0)
        {
            StopCorpseMovement();
            Host.TurnToHeading((float)desiredDeg);
            return;
        }

        long now = CorpseNowMs;
        if (!_corpseAutorunActive || now - _lastCorpseApproachHeartbeatAt >= 500)
        {
            Host.SetAutoRun(true);
            _corpseAutorunActive = true;
            _lastCorpseApproachHeartbeatAt = now;
        }
    }

    private void StopCorpseMovement()
    {
        if (!_corpseAutorunActive)
            return;

        Host.SetAutoRun(false);
        if (Host.HasStopCompletely)
            Host.StopCompletely();

        _corpseAutorunActive = false;
    }

    private void AttemptOpenCorpse(int corpseId)
    {
        StopCorpseMovement();
        Host.SelectItem(unchecked((uint)corpseId));
        Host.UseObject(unchecked((uint)corpseId));
        LootDiag($"[RynthAi] Corpse loot: attempting open for 0x{(uint)corpseId:X8}.");
    }

    private void SyncCorpseContainerState()
    {
        if (!Host.HasGetGroundContainerId)
            return;

        int rawOpen = unchecked((int)Host.GetGroundContainerId());
        if (rawOpen == _openedContainerId)
            return;

        long now = CorpseNowMs;
        if (_openedContainerId != 0 && rawOpen != _openedContainerId)
        {
            int previousOpen = _openedContainerId;
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;

            if (_targetCorpseId == previousOpen)
            {
                if (_completedCorpses.Contains(previousOpen))
                {
                    LootDiag($"[RynthAi] Corpse loot: observed close for completed corpse 0x{(uint)previousOpen:X8}.");
                    ResetCorpseTarget(releaseState: true);
                    return;
                }

                ResetCurrentLootItem();
                _lastCorpseOpenAttemptAt = 0;
                _lastLootActionAt = now;
                LootDiag($"[RynthAi] Corpse loot: reconciled closed corpse 0x{(uint)previousOpen:X8}; will reopen if still active.");
            }
        }

        if (_openedContainerId == 0 && rawOpen != 0 && IsCorpseLikeObject(rawOpen))
        {
            // Don't re-claim a corpse we've already completed — the server still
            // reports it as open, but we're done with it. It will close when we
            // open the next corpse or move away.
            if (_completedCorpses.Contains(rawOpen))
                return;

            bool switchedTarget = _targetCorpseId != 0 && _targetCorpseId != rawOpen;
            _openedContainerId = rawOpen;
            _openedContainerAt = now;
            _openedContainerInventoryObservedAt = 0;
            _lastLootActionAt = 0;
            ResetCurrentLootItem();
            if (switchedTarget)
            {
                _processedCorpseItems.Clear();
                _corpseItemAttempts.Clear();
            }

            _targetCorpseId = rawOpen;
            _corpseTargetSince = now;
            _lastCorpseOpenAttemptAt = 0;
            LootDiag($"[RynthAi] Corpse loot: reconciled open corpse 0x{(uint)rawOpen:X8} from client state.");
        }
    }

    private void ResetCorpseTarget(bool releaseState)
    {
        StopCorpseMovement();
        _targetCorpseId = 0;
        _corpseTargetSince = 0;
        _openedContainerAt = 0;
        _openedContainerInventoryObservedAt = 0;
        _lastCorpseOpenAttemptAt = 0;
        _lastLootActionAt = 0;
        _corpseIdsRequested = false;
        ResetCurrentLootItem();
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        _corpsePausedNav = false;

        if (!releaseState)
            return;

        var settings = _dashboard?.Settings;
        if (settings != null && string.Equals(settings.BotAction, "Looting", StringComparison.OrdinalIgnoreCase))
            settings.BotAction = "Default";
    }

    private void HandleCorpseObjectDeleted(uint objectId)
    {
        int sid = unchecked((int)objectId);
        _completedCorpses.Remove(sid);
        _processedCorpseItems.Remove(sid);
        _corpseItemAttempts.Remove(sid);
        _corpseCooldownUntil.Remove(sid);
        _ownershipSkipLogged.Remove(sid);
        _corpseIdRequested.Remove(sid);

        if (_targetCorpseId == sid)
            ResetCorpseTarget(releaseState: true);

        if (_openedContainerId == sid)
            _openedContainerId = 0;
    }

    private void MarkCorpseCooldown(int corpseId, int delayMs)
    {
        if (corpseId == 0)
            return;

        _corpseCooldownUntil[corpseId] = CorpseNowMs + Math.Max(500, delayMs);
    }

    private bool IsCorpseLikeObject(int objectId)
    {
        if (objectId == 0)
            return false;

        if ((_objectCache?[objectId]?.ObjectClass ?? AcObjectClass.Unknown) == AcObjectClass.Corpse)
            return true;

        if (_targetCorpseId != 0 && objectId == _targetCorpseId)
            return true;

        if (!Host.TryGetObjectName(unchecked((uint)objectId), out string name) || string.IsNullOrWhiteSpace(name))
            return false;

        return name.EndsWith(" corpse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("'s corpse", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("corpse of ", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetHeadingToObject(int targetId, out double desiredDeg, out double absError)
    {
        desiredDeg = 0;
        absError = 180.0;

        if (!Host.TryGetPlayerPose(out _, out float px, out float py, out _, out float qw, out _, out _, out float qz))
            return false;

        if (!Host.TryGetObjectPosition(unchecked((uint)targetId), out _, out float tx, out float ty, out _))
            return false;

        double dx = tx - px;
        double dy = ty - py;
        desiredDeg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
        if (desiredDeg < 0)
            desiredDeg += 360.0;

        double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        double currentDeg = ((-physYawDeg) % 360.0 + 720.0) % 360.0;

        double error = desiredDeg - currentDeg;
        while (error > 180.0) error -= 360.0;
        while (error < -180.0) error += 360.0;
        absError = Math.Abs(error);
        return true;
    }

    private static double GetCorpseApproachRangeMaxMeters(LegacyUiSettings settings)
    {
        return ConvertYardsToMeters(NormalizeCorpseRangeYards(settings.CorpseApproachRangeMax, 10.0));
    }

    private static double GetCorpseApproachRangeMinMeters(LegacyUiSettings settings, double maxMeters)
    {
        double minMeters = ConvertYardsToMeters(NormalizeCorpseRangeYards(settings.CorpseApproachRangeMin, 2.0));
        if (minMeters <= 0.25)
            minMeters = Math.Min(1.5, maxMeters);
        if (minMeters > maxMeters)
            minMeters = maxMeters;
        return minMeters;
    }

    private static int GetCorpseTimeoutMs(LegacyUiSettings settings)
    {
        return Math.Max(2000, settings.LootCorpseTimeoutMs);
    }

    private bool HasCorpseTimedOut(LegacyUiSettings settings, long now)
    {
        return _targetCorpseId != 0
            && _corpseTargetSince != 0
            && now - _corpseTargetSince >= GetCorpseTimeoutMs(settings);
    }

    private void AbandonCurrentCorpse(long now, string reason)
    {
        int corpseId = _targetCorpseId != 0 ? _targetCorpseId : _openedContainerId;
        if (corpseId == 0)
            return;

        long ageMs = _corpseTargetSince != 0 ? now - _corpseTargetSince : 0;
        LootDiag($"[RynthAi] Corpse loot: abandoning corpse 0x{(uint)corpseId:X8} after {ageMs}ms ({reason}).");
        ChatLine($"[RynthAi] Corpse timeout: giving up on 0x{(uint)corpseId:X8} after {ageMs}ms.");
        _completedCorpses.Add(corpseId);
        _corpseCooldownUntil.Remove(corpseId);

        // Force-clear container state — don't try UseObject to close.
        // The server will auto-close when we open the next corpse or move away.
        if (_openedContainerId == corpseId)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
        }

        ResetCorpseTarget(releaseState: true);
    }

    private static double NormalizeCorpseRangeYards(double value, double fallbackYards)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            return fallbackYards;

        return value <= 1.0 ? value * 240.0 : value;
    }

    private static double ConvertYardsToMeters(double yards)
    {
        return Math.Max(0.0, yards) * 0.9144;
    }

    private static double ConvertMetersToYards(double meters)
    {
        return meters / 0.9144;
    }

    private void HandleCorpseInfoCommand()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
        {
            ChatLine("[RynthAi] Corpse info not ready yet.");
            return;
        }

        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        double minMeters = GetCorpseApproachRangeMinMeters(settings, maxMeters);
        ChatLine($"[RynthAi] Corpse ranges: max={ConvertMetersToYards(maxMeters):F1} yd / {maxMeters:F1} m, min={ConvertMetersToYards(minMeters):F1} yd / {minMeters:F1} m");
        uint rawOpen = Host.HasGetGroundContainerId ? Host.GetGroundContainerId() : 0;
        ChatLine($"[RynthAi] Corpse state: target=0x{(uint)_targetCorpseId:X8}, corpse-open=0x{(uint)_openedContainerId:X8}, raw-open=0x{rawOpen:X8}, busy={_busyCount}, inv-update={(_openedContainerInventoryObservedAt != 0 ? 1 : 0)}, completed={_completedCorpses.Count}");

        if (TryFindNearestCorpse(maxMeters, out WorldObject? corpse, out double distanceMeters))
            ChatLine($"[RynthAi] Nearest corpse: {corpse!.Name} (0x{(uint)corpse.Id:X8}) at {ConvertMetersToYards(distanceMeters):F1} yd / {distanceMeters:F1} m");
        else
            ChatLine("[RynthAi] No unopened corpses are currently within corpse max range.");
    }

    private void HandleCorpseCheckCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
        {
            ChatLine("[RynthAi] Corpse ownership diagnostics are not ready yet.");
            return;
        }

        string selector = parts.Length >= 3 ? parts[2].Trim().ToLowerInvariant() : string.Empty;
        if (!TryResolveCorpseForDiagnostics(selector, out WorldObject? corpse, out string source))
        {
            ChatLine("[RynthAi] No corpse found for diagnostics. Use /ra corpsecheck [target|open|nearest].");
            return;
        }

        if (Host.HasRequestId)
            Host.RequestId(unchecked((uint)corpse!.Id));

        CorpseLootDecision decision = EvaluateCorpseLootDecision(corpse);
        ChatLine($"[RynthAi] Corpse check ({source}): {corpse!.Name} (0x{(uint)corpse.Id:X8})");
        ChatLine($"[RynthAi]   Loot From: {decision.ModeName}");
        ChatLine($"[RynthAi]   Would loot: {(decision.ShouldLoot ? "YES" : "NO")}");
        ChatLine($"[RynthAi]   Reason: {decision.Reason}");
        ChatLine($"[RynthAi]   Player: {(string.IsNullOrWhiteSpace(decision.PlayerName) ? "<unknown>" : decision.PlayerName)}");
        ChatLine($"[RynthAi]   Killer: {(string.IsNullOrWhiteSpace(decision.KillerName) ? "<unknown>" : decision.KillerName)}");
        ChatLine($"[RynthAi]   Fellowship: {(decision.InFellowship ? "yes" : "no")} | killer is fellow: {(decision.KillerIsFellow ? "yes" : "no")}");
        if (!string.IsNullOrWhiteSpace(decision.LongDesc))
            ChatLine($"[RynthAi]   LongDesc: {decision.LongDesc}");
        else
            ChatLine("[RynthAi]   LongDesc: <empty> (ID was requested; run the command again if the corpse was not identified yet)");
    }

    private void HandleFellowshipInfoCommand()
    {
        HandleFellowshipCommand(["/ra", "fellow", "status"]);
    }

    private void HandleFellowshipCommand(string[] parts)
    {
        if (_fellowshipTracker == null)
        {
            ChatLine("[RynthAi] Fellowship tracker is not ready yet.");
            return;
        }

        string fellowSub = parts.Length >= 3 ? parts[2].Trim().ToLowerInvariant() : "status";
        if (!_fellowshipTracker.IsInFellowship && fellowSub != "help")
        {
            ChatLine("[RynthAi] Not in a fellowship.");
            return;
        }

        switch (fellowSub)
        {
            case "status":
                PrintFellowshipStatus();
                break;

            case "leader":
                {
                    int leaderId = _fellowshipTracker.LeaderId;
                    string leaderName = FindFellowshipMemberNameById(leaderId);
                    ChatLine($"[RynthAi] Leader: {leaderName} (0x{unchecked((uint)leaderId):X8}){(_fellowshipTracker.IsLeader ? " (you)" : "")}");
                    break;
                }

            case "count":
                ChatLine($"[RynthAi] Fellowship member count: {_fellowshipTracker.MemberCount}");
                break;

            case "names":
                {
                    var names = new List<string>(_fellowshipTracker.GetMemberNames());
                    ChatLine($"[RynthAi] Members ({names.Count}): {string.Join(", ", names)}");
                    break;
                }

            case "name":
                ChatLine($"[RynthAi] Fellowship name: \"{_fellowshipTracker.FellowshipName}\"");
                break;

            case "open":
                ChatLine($"[RynthAi] Open: {_fellowshipTracker.IsOpen}");
                break;

            case "locked":
                ChatLine($"[RynthAi] Locked: {_fellowshipTracker.IsLocked}");
                break;

            case "sharexp":
                ChatLine($"[RynthAi] ShareXP: {_fellowshipTracker.ShareXP}");
                break;

            case "ismember":
                if (parts.Length >= 4)
                {
                    string checkName = string.Join(" ", parts, 3, parts.Length - 3);
                    bool found = _fellowshipTracker.IsMember(checkName);
                    ChatLine($"[RynthAi] IsMember(\"{checkName}\"): {found}");
                }
                else
                {
                    ChatLine("[RynthAi] Usage: /ra fellow ismember <name>");
                }
                break;

            case "help":
                ChatLine("[RynthAi] /ra fellow [status|leader|count|names|name|open|locked|sharexp|ismember <name>]");
                break;

            default:
                ChatLine($"[RynthAi] Unknown fellow subcommand: {fellowSub}. Try /ra fellow help");
                break;
        }
    }

    private void PrintFellowshipStatus()
    {
        if (_fellowshipTracker == null)
        {
            ChatLine("[RynthAi] Fellowship tracker is not ready yet.");
            return;
        }

        string playerName = GetPlayerNameForLootOwnership();
        ChatLine($"[RynthAi] Fellowship: \"{_fellowshipTracker.FellowshipName}\"");
        ChatLine($"[RynthAi] Player: {(string.IsNullOrWhiteSpace(playerName) ? "<unknown>" : playerName)}");
        ChatLine($"[RynthAi] Members: {_fellowshipTracker.MemberCount} | Leader: {(_fellowshipTracker.IsLeader ? "ME" : $"0x{unchecked((uint)_fellowshipTracker.LeaderId):X8}")} | Open: {_fellowshipTracker.IsOpen} | Locked: {_fellowshipTracker.IsLocked} | ShareXP: {_fellowshipTracker.ShareXP}");

        int index = 0;
        foreach (string memberName in _fellowshipTracker.GetMemberNames())
        {
            int memberId = _fellowshipTracker.GetMemberId(index);
            bool isLeader = memberId == _fellowshipTracker.LeaderId;
            ChatLine($"[RynthAi]   [{index}] {memberName} (0x{unchecked((uint)memberId):X8}){(isLeader ? " [LEADER]" : "")}");
            index++;
        }
    }

    private string FindFellowshipMemberNameById(int memberId)
    {
        if (_fellowshipTracker == null || memberId == 0)
            return "(unknown)";

        int index = 0;
        foreach (string memberName in _fellowshipTracker.GetMemberNames())
        {
            if (_fellowshipTracker.GetMemberId(index) == memberId)
                return memberName;

            index++;
        }

        return "(unknown)";
    }

    private bool TryResolveCorpseForDiagnostics(string selector, out WorldObject? corpse, out string source)
    {
        corpse = null;
        source = string.Empty;
        if (_objectCache == null || _playerId == 0)
            return false;

        if ((string.IsNullOrEmpty(selector) || selector == "target") &&
            _currentTargetId != 0 &&
            IsCorpseLikeObject(unchecked((int)_currentTargetId)))
        {
            corpse = _objectCache[unchecked((int)_currentTargetId)];
            if (corpse != null)
            {
                source = "target";
                return true;
            }
        }

        if ((string.IsNullOrEmpty(selector) || selector == "open") &&
            _openedContainerId != 0 &&
            IsCorpseLikeObject(_openedContainerId))
        {
            corpse = _objectCache[_openedContainerId];
            if (corpse != null)
            {
                source = "open";
                return true;
            }
        }

        if (selector == "target" || selector == "open")
            return false;

        double maxMeters = GetCorpseApproachRangeMaxMeters(_dashboard?.Settings ?? new LegacyUi.LegacyUiSettings());
        if (TryFindNearestCorpseForDiagnostics(maxMeters, out corpse, out _))
        {
            source = "nearest";
            return true;
        }

        return false;
    }

    private bool TryFindNearestCorpseForDiagnostics(double maxMeters, out WorldObject? corpse, out double distanceMeters)
    {
        corpse = null;
        distanceMeters = double.MaxValue;
        if (_objectCache == null || _playerId == 0)
            return false;

        int playerId = unchecked((int)_playerId);
        foreach (WorldObject candidate in _objectCache.GetLandscapeObjects())
        {
            if (candidate.ObjectClass != AcObjectClass.Corpse)
                continue;

            double dist = _objectCache.Distance(playerId, candidate.Id);
            if (dist > maxMeters || dist >= distanceMeters)
                continue;

            corpse = candidate;
            distanceMeters = dist;
        }

        return corpse != null;
    }

    private void HandleCorpseOpenCommand()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
        {
            ChatLine("[RynthAi] Corpse opening is not ready yet.");
            return;
        }

        if (_openedContainerId != 0)
        {
            ChatLine($"[RynthAi] A container is already open: 0x{(uint)_openedContainerId:X8}");
            return;
        }

        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        if (!TryFindNearestCorpse(maxMeters, out WorldObject? corpse, out double distanceMeters))
        {
            ChatLine("[RynthAi] No unopened corpse is within corpse max range.");
            return;
        }

        _targetCorpseId = corpse!.Id;
        _corpseTargetSince = CorpseNowMs;
        _lastCorpseOpenAttemptAt = 0;
        ChatLine($"[RynthAi] Corpse target set to {corpse.Name} at {ConvertMetersToYards(distanceMeters):F1} yd.");
        TickCorpseOpening();
    }
}
