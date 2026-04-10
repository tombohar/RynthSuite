using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;

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
    private int _busyCount;
    private int _openedContainerId;
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

    private static long CorpseNowMs => Environment.TickCount64;
    private static readonly string CorpseLootLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "RynthAi-loot.log");

    private void LootDiag(string message)
    {
        try
        {
            File.AppendAllText(CorpseLootLogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }

        Log(message);
    }

    public override void OnBusyCountIncremented()
    {
        _busyCount++;
    }

    public override void OnBusyCountDecremented()
    {
        if (_busyCount > 0)
            _busyCount--;
    }

    public override void OnViewObjectContents(uint objectId)
    {
        int sid = unchecked((int)objectId);
        if (!IsCorpseLikeObject(sid))
            return;

        bool wasCompleted = _completedCorpses.Remove(sid);
        _corpseCooldownUntil.Remove(sid);
        bool sameCorpse = _targetCorpseId == sid;
        bool switchedTarget = _targetCorpseId != 0 && _targetCorpseId != sid;

        _openedContainerId = sid;
        _openedContainerAt = CorpseNowMs;
        _openedContainerInventoryObservedAt = 0;
        _lastLootActionAt = 0;
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

    private void TickCorpseOpening()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
            return;

        SyncCorpseContainerState();

        if (!ShouldDriveCorpseOpening(settings))
        {
            ResetCorpseTarget(releaseState: true);
            return;
        }

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
                return;

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

        if (settings.CurrentState == "Idle" || settings.CurrentState == "Navigating")
            settings.CurrentState = "Looting";

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

        if (_combatManager?.HasTargets == true && !settings.BoostLootPriority)
            return false;

        return settings.CurrentState == "Idle"
            || settings.CurrentState == "Navigating"
            || settings.CurrentState == "Looting";
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

            if (!ShouldLootCorpse(candidate))
                continue;

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
        if (string.IsNullOrWhiteSpace(longDesc) && Host.HasRequestId)
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
                && killerName.Equals(myName, StringComparison.OrdinalIgnoreCase);
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
            && killerName.Equals(myName, StringComparison.OrdinalIgnoreCase))
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
            && killerName.Equals(myName, StringComparison.OrdinalIgnoreCase);
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

        WorldObject? openedCorpse = _objectCache?[corpseId];
        if (openedCorpse != null && !ShouldLootCorpse(openedCorpse))
        {
            LootDiag($"[RynthAi] Corpse loot: skipping corpse 0x{(uint)corpseId:X8} due to loot ownership settings.");
            MarkCorpseCompleted(corpseId);
            return;
        }

        if (_completedCorpses.Contains(corpseId))
        {
            if (_lastLootActionAt == 0 || CorpseNowMs - _lastLootActionAt >= Math.Max(200, settings.LootClosingDelayMs))
            {
                Host.SelectItem(unchecked((uint)corpseId));
                Host.UseObject(unchecked((uint)corpseId));
                LootDiag($"[RynthAi] Corpse loot: closing completed corpse 0x{(uint)corpseId:X8}.");
                _lastLootActionAt = CorpseNowMs;
            }
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
        if (openAge < Math.Max(350, settings.LootContentSettleMs))
            return;

        if (_openedContainerInventoryObservedAt == 0)
        {
            if (openAge < Math.Max(1000, settings.LootEmptyCorpseMs + 300))
                return;

            _openedContainerInventoryObservedAt = _openedContainerAt;
            LootDiag($"[RynthAi] Corpse loot: no inventory update seen for 0x{(uint)corpseId:X8}; probing after {openAge}ms.");
        }
        else if (now - _openedContainerInventoryObservedAt < 250)
        {
            return;
        }

        if (_currentLootItemMovePending)
        {
            TickPendingCorpsePickup(settings);
            return;
        }

        int nextItemId = FindNextCorpseItem(corpseId, out int visibleItemCount);
        if (visibleItemCount <= 0)
        {
            if (now - _openedContainerAt >= Math.Max(200, settings.LootEmptyCorpseMs))
            {
                LootDiag($"[RynthAi] Corpse loot: no cached child items found for 0x{(uint)corpseId:X8} after {openAge}ms; marking complete.");
                MarkCorpseCompleted(corpseId);
            }
            return;
        }

        if (_lastLootActionAt != 0 && now - _lastLootActionAt < Math.Max(50, settings.LootInterItemDelayMs))
            return;

        if (nextItemId == 0)
        {
            LootDiag($"[RynthAi] Corpse loot: all {visibleItemCount} cached child item(s) for 0x{(uint)corpseId:X8} are already processed.");
            MarkCorpseCompleted(corpseId);
            return;
        }

        if (_currentLootItemId != nextItemId)
            ResetCurrentLootItem();

        if (!_currentLootItemRequested)
        {
            _currentLootItemId = nextItemId;
            LootDiag($"[RynthAi] Corpse loot: requesting ID for 0x{(uint)nextItemId:X8} from corpse 0x{(uint)corpseId:X8}.");
            _currentLootItemRequested = Host.RequestId(unchecked((uint)nextItemId));
            _currentLootItemRequestedAt = now;
            _lastLootActionAt = now;

            if (!_currentLootItemRequested && IncrementCorpseItemAttempt(nextItemId) >= 2)
                _processedCorpseItems.Add(nextItemId);

            return;
        }

        if (now - _currentLootItemRequestedAt < Math.Max(100, settings.LootAssessWindowMs))
            return;

        ProcessCorpseItem(nextItemId);
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

        long verifyWindowMs = Math.Max(250, settings.LootRetryTimeoutMs);
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

        ResetCurrentLootItem();
        _lastLootActionAt = now;
    }

    private bool IsCorpseNavigationClaimActive(LegacyUiSettings? settings = null)
    {
        settings ??= _dashboard?.Settings;
        if (settings == null || !ShouldDriveCorpseOpening(settings))
            return false;

        return _targetCorpseId != 0 || _openedContainerId != 0 || _corpseAutorunActive;
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
        if (settings != null && !string.Equals(settings.CurrentState, "Looting", StringComparison.OrdinalIgnoreCase))
            settings.CurrentState = "Looting";

        if (corpseId != 0)
            LootDiag($"[RynthAi] Corpse loot: pausing navigation for corpse 0x{(uint)corpseId:X8}.");
    }

    private void ProcessCorpseItem(int itemId)
    {
        if (_objectCache == null)
            return;

        long now = CorpseNowMs;
        WorldObject? item = _objectCache[itemId];
        if (item == null || string.IsNullOrWhiteSpace(item.Name))
        {
            if (IncrementCorpseItemAttempt(itemId) >= 2)
                _processedCorpseItems.Add(itemId);

            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        if (_openedContainerId != 0 && item.Container != _openedContainerId)
        {
            _processedCorpseItems.Add(itemId);
            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        if (!TryLoadLootProfile(string.Empty, out VTankLootProfile profile, out _))
        {
            _processedCorpseItems.Add(itemId);
            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        LootRule? matchedRule = null;
        foreach (LootRule rule in profile.Rules)
        {
            if (rule.IsMatch(item))
            {
                matchedRule = rule;
                break;
            }
        }

        if (matchedRule == null)
        {
            LootDiag($"[RynthAi] Corpse loot: no loot rule matched 0x{(uint)itemId:X8} '{item.Name}'.");
            _processedCorpseItems.Add(itemId);
            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        ChatLine($"[RynthAi] Loot rule: {item.Name} -> [{matchedRule.Action}] {matchedRule.Name}");
        Host.SelectItem(unchecked((uint)itemId));
        LootDiag($"[RynthAi] Corpse loot: using 0x{(uint)itemId:X8} '{item.Name}' from corpse 0x{(uint)_openedContainerId:X8}.");
        bool moved = Host.UseObject(unchecked((uint)itemId));
        if (!moved)
        {
            LootDiag($"[RynthAi] Corpse loot: use request failed immediately for 0x{(uint)itemId:X8}.");
            if (IncrementCorpseItemAttempt(itemId) >= 3)
                _processedCorpseItems.Add(itemId);

            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        _currentLootItemMovePending = true;
        _currentLootItemMoveRequestedAt = now;
        _currentLootItemMoveAttempts = 1;
        _currentLootItemActionLabel = matchedRule.Action.ToString();
        _currentLootItemName = item.Name;
        _lastLootActionAt = now;
    }

    private void MarkCorpseCompleted(int corpseId)
    {
        if (corpseId == 0)
            return;

        _completedCorpses.Add(corpseId);
        _corpseCooldownUntil.Remove(corpseId);
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        ResetCurrentLootItem();
        _lastLootActionAt = CorpseNowMs;
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
        ResetCurrentLootItem();
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        _corpsePausedNav = false;

        if (!releaseState)
            return;

        var settings = _dashboard?.Settings;
        if (settings != null && string.Equals(settings.CurrentState, "Looting", StringComparison.OrdinalIgnoreCase))
            settings.CurrentState = "Idle";
    }

    private void HandleCorpseObjectDeleted(uint objectId)
    {
        int sid = unchecked((int)objectId);
        _completedCorpses.Remove(sid);
        _processedCorpseItems.Remove(sid);
        _corpseItemAttempts.Remove(sid);
        _corpseCooldownUntil.Remove(sid);

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

        if (_openedContainerId == corpseId)
        {
            Host.SelectItem(unchecked((uint)corpseId));
            Host.UseObject(unchecked((uint)corpseId));
            LootDiag($"[RynthAi] Corpse loot: requested close for timed out corpse 0x{(uint)corpseId:X8}.");
            _lastLootActionAt = now;
            ResetCurrentLootItem();
            return;
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
            ChatLine("[RynthAi] No corpse found for diagnostics. Use /na corpsecheck [target|open|nearest].");
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
        HandleFellowshipCommand(["/na", "fellow", "status"]);
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
                    ChatLine("[RynthAi] Usage: /na fellow ismember <name>");
                }
                break;

            case "help":
                ChatLine("[RynthAi] /na fellow [status|leader|count|names|name|open|locked|sharexp|ismember <name>]");
                break;

            default:
                ChatLine($"[RynthAi] Unknown fellow subcommand: {fellowSub}. Try /na fellow help");
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
