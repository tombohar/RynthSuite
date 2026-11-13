using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// Chest looting (2026-10-12). With Looting on, a chest that is open - the player clicked it, or a
// meta/nav "use" opened it - is looted by the corpse looter: the chest becomes the open loot
// container (_openedContainerId), and TickOpenCorpseLooting does the item work exactly as for a
// corpse (rule matching, ID requests, Keep #, pack space, mana stones, salvage without a UST,
// retries, timeout). Only the approach and the open are skipped (the chest is already open and in
// reach), and nothing marks a chest done for good: chests regenerate, so the next open loots again.
//
// Eligible: an open container lying in the world (no container, no wielder) that is not a corpse
// (the corpse path), a vendor, a player, house storage, or anything you or someone else carries.
// House storage: on ACE every house-linked object (Storage chests, hooks) gets HouseOwner from its
// house (House.SetLinkProperties) and the server sends it in the object's PublicWeenieDesc only
// while the house is owned - and a storage chest only opens for someone the owner allows, so an
// open one always has it. The engine reads it (STypeIID 32, API v73). Without that call, and as a
// second check, a container named like storage or a hook is skipped too.
public sealed partial class RynthAiPlugin
{
    private const uint ChestIidHouseOwner = 32;
    private const uint ChestBfPlayer = 0x00000008, ChestBfVendor = 0x00000200, ChestBfCorpse = 0x00002000;

    /// <summary>A non-corpse container the client opened, not looted yet (0 = none).</summary>
    private int _chestCandidateId;
    private long _chestCandidateSince;
    private long _chestCandidateInvSeenAt;
    private int _chestSkipLoggedFor;
    private int _chestHeldLoggedFor;

    /// <summary>The chest being looted as the open loot container (0 = the open container, if any, is a corpse).</summary>
    private int _lootChestId;

    /// <summary>The log prefix of the loot pipeline: "Chest loot" while a chest is the loot container.</summary>
    private string LootTag => _lootChestId != 0 && (_openedContainerId == _lootChestId || _targetCorpseId == _lootChestId)
        ? "Chest loot" : "Corpse loot";

    private bool IsChestLoot(int id) => id != 0 && id == _lootChestId;

    /// <summary>The last chest looted to the end (every item judged), for the meta's ralastchestlooted[] (0 = none).</summary>
    private uint _lastChestLootedId;

    internal enum ChestVerdict { Loot, Wait, Skip }

    /// <summary>Names AC/ACE give house storage and hooks ("Storage", "Wall Hook", ...).</summary>
    internal static bool IsHouseStorageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.Trim();
        return n.Contains("storage", StringComparison.OrdinalIgnoreCase)
            || n.Equals("hook", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(" hook", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an open non-corpse container is a chest to loot. Wait: not decidable yet (the
    /// engine can't read who holds it yet); Skip: never this open (why says what it is).
    /// </summary>
    private ChestVerdict JudgeChest(int id, out string why)
    {
        why = string.Empty;
        if (id == 0 || _objectCache == null || _playerId == 0) { why = "not ready"; return ChestVerdict.Wait; }
        uint uid = unchecked((uint)id);
        if (uid == _playerId) { why = "your main pack"; return ChestVerdict.Skip; }
        if (IsCorpseLikeObject(id)) { why = "a corpse"; return ChestVerdict.Skip; }

        if (Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(uid, out uint bf))
        {
            if ((bf & ChestBfCorpse) != 0) { why = "a corpse"; return ChestVerdict.Skip; }
            if ((bf & ChestBfVendor) != 0) { why = "a vendor"; return ChestVerdict.Skip; }
            if ((bf & ChestBfPlayer) != 0) { why = "a player"; return ChestVerdict.Skip; }
        }

        if (Host.HasGetObjectInstanceIdProperty
            && Host.TryGetObjectInstanceIdProperty(uid, ChestIidHouseOwner, out uint houseOwner) && houseOwner != 0)
        {
            why = $"house storage (house owner 0x{houseOwner:X8})";
            return ChestVerdict.Skip;
        }
        string name = _objectCache[id]?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) && Host.TryGetObjectName(uid, out string hostName))
            name = hostName;
        if (IsHouseStorageName(name)) { why = $"house storage (named '{name}')"; return ChestVerdict.Skip; }

        if (!Host.HasGetObjectOwnershipInfo) { why = "the engine can't tell who holds it"; return ChestVerdict.Skip; }
        if (!Host.TryGetObjectOwnershipInfo(uid, out uint container, out uint wielder, out _))
        {
            why = "who holds it isn't readable yet";
            return ChestVerdict.Wait;
        }
        if (container == _playerId || wielder == _playerId) { why = "in your inventory"; return ChestVerdict.Skip; }
        if (container != 0)
        {
            bool inSidePack = Host.TryGetObjectOwnershipInfo(container, out uint outer, out _, out _) && outer == _playerId;
            why = inSidePack ? "in your inventory (inside a side pack)" : $"held by 0x{container:X8}";
            return ChestVerdict.Skip;
        }
        if (wielder != 0) { why = $"held by 0x{wielder:X8}"; return ChestVerdict.Skip; }
        return ChestVerdict.Loot;
    }

    /// <summary>OnViewObjectContents for a non-corpse container: remember it as a chest to look at.</summary>
    private void NoteContainerOpened(int id)
    {
        if (id != 0 && id == _chestReopenId)
            ClearChestReopen();   // it is open again (the looter's use, a meta's or the player's)
        if (id == 0 || unchecked((uint)id) == _playerId || IsChestLoot(id))
            return;
        _chestCandidateId = id;
        _chestCandidateSince = CorpseNowMs;
        _chestCandidateInvSeenAt = 0;
        _chestSkipLoggedFor = 0;
        _chestHeldLoggedFor = 0;
    }

    /// <summary>
    /// The pending loot-check of a natively opened container is left out when the looter takes
    /// this container (it says per item what it does); everything else keeps it.
    /// </summary>
    private bool ChestLooterTakesContainer(int id)
    {
        var settings = _dashboard?.Settings;
        if (settings == null || !settings.IsMacroRunning || !settings.EnableLooting || id != _chestCandidateId)
            return false;
        _chestCandidateInvSeenAt = CorpseNowMs;
        return JudgeChest(id, out _) == ChestVerdict.Loot;
    }

    /// <summary>HasLootWork: an open, eligible chest waits to be looted (no state changed).</summary>
    private bool HasChestLootWork(LegacyUiSettings settings)
    {
        if (_chestCandidateId == 0 || _targetCorpseId != 0 || _openedContainerId != 0)
            return false;
        if (JudgeChest(_chestCandidateId, out _) != ChestVerdict.Loot)
            return false;
        _lootStarved = CorpseNowMs - _chestCandidateSince >= LootStarveMs;
        return true;
    }

    /// <summary>Once per open: an eligible chest waits because combat holds the tick.</summary>
    private void LogChestHeldByCombat()
    {
        int id = _chestCandidateId;
        if (id == 0 || _chestHeldLoggedFor == id || _openedContainerId != 0 || _targetCorpseId != 0)
            return;
        if (JudgeChest(id, out _) != ChestVerdict.Loot)
            return;
        _chestHeldLoggedFor = id;
        LootDiag($"[RynthAi] Chest loot: chest 0x{(uint)id:X8} waits for combat (targets in range); it is looted when the fight ends if it is still open.");
    }

    // ── An open chest goes before a fight further off (2026-11-07) ──────────────────────────────
    // Unlike a corpse, a chest closes as soon as you walk away from it. A meta opened a treasure
    // cache with a golem 32 m off: combat had the tick, walked to the golem, the cache closed and
    // nothing was taken. So an open chest RynthAi is to loot gets the tick ahead of combat (the
    // loot-starvation turn, without its 8 s wait), unless a monster is within
    // LootStarveCloseYards: that one is fought first, next to the chest.
    private int _chestFirstLoggedFor;

    /// <summary>An open chest RynthAi is to loot: the loot container, or one opened and not taken yet.</summary>
    private bool OpenChestWaiting()
    {
        if (IsChestLoot(_openedContainerId)) return true;
        return _chestCandidateId != 0 && _openedContainerId == 0 && _targetCorpseId == 0
            && JudgeChest(_chestCandidateId, out _) == ChestVerdict.Loot;
    }

    /// <summary>The arbiter's loot-first input for an open chest (see above). Logs once per chest.</summary>
    private bool IsOpenChestTurn(bool wantLoot, bool wantCombat)
    {
        if (!wantLoot || !wantCombat || !OpenChestWaiting()) return false;
        if (_combatManager?.HasCloseThreat(LootStarveCloseYards) == true) return false;
        int id = IsChestLoot(_openedContainerId) ? _openedContainerId : _chestCandidateId;
        if (_chestFirstLoggedFor != id)
        {
            _chestFirstLoggedFor = id;
            LootDiag($"[RynthAi] Chest loot: chest 0x{(uint)id:X8} is open, so it is looted before the fight " +
                     $"(no monster within {LootStarveCloseYards:0} yd; a chest closes when you walk away).");
        }
        return true;
    }

    /// <summary>
    /// TickCorpseOpening, nothing claimed or open: takes an open eligible chest as the loot
    /// container (no walk, no open: it is open and in reach). True when this tick is spent on it.
    /// </summary>
    private bool TryTakeOpenChest(LegacyUiSettings settings, long now)
    {
        int id = _chestCandidateId;
        if (id == 0 || _targetCorpseId != 0 || _openedContainerId != 0)
            return false;

        ChestVerdict verdict = JudgeChest(id, out string why);
        if (verdict == ChestVerdict.Skip)
        {
            if (_chestSkipLoggedFor != id)
            {
                _chestSkipLoggedFor = id;
                string name = _objectCache?[id]?.Name ?? string.Empty;
                LootDiag($"[RynthAi] Chest loot: not looting 0x{(uint)id:X8} '{name}' - {why}.");
            }
            _chestCandidateId = 0;
            return false;
        }
        if (verdict == ChestVerdict.Wait)
            return false;

        if (IsLootingBlockedByPack(settings))
        {
            LootDiag($"[RynthAi] Chest loot: not looting 0x{(uint)id:X8} - the pack is full (Stop Looting When Pack Full).");
            _chestCandidateId = 0;
            return false;
        }

        // The arbiter gives Looting the tick (HasChestLootWork) unless something outranks it.
        if (_activity != BotActivity.Looting)
            return true;

        TakeChestAsLootContainer(id, now);
        PauseNavigationForCorpse();
        StopCorpseMovement();
        TickOpenCorpseLooting(settings);
        return true;
    }

    /// <summary>Makes open chest <paramref name="id"/> the loot container (no walk, no open: it is open and in reach).</summary>
    private void TakeChestAsLootContainer(int id, long now)
    {
        _chestCandidateId = 0;
        _lootChestId = id;
        _openedContainerId = id;
        _openedContainerAt = _chestCandidateSince != 0 ? _chestCandidateSince : now;
        _openedContainerInventoryObservedAt = _chestCandidateInvSeenAt;
        _lastLootActionAt = 0;
        ResetLootTally();
        _corpseIdsRequested = false;
        ResetCurrentLootItem();
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        _pendingManaStoneIds.Clear(); _pendingManaTapIds.Clear(); _pendingKeepUpTo.Clear();
        _targetCorpseId = id;
        _corpseTargetSince = now;
        _lastCorpseOpenAttemptAt = 0;
        _corpseHoldFrozenAge = -1;

        string chestName = _objectCache?[id]?.Name ?? string.Empty;
        LootDiag($"[RynthAi] Chest loot: looting open chest 0x{(uint)id:X8} '{chestName}' (already open and in reach: no walk, no open).");
    }

    /// <summary>
    /// Ends the chest session. Unlike a corpse the chest is never put in _completedCorpses (it
    /// regenerates; the next open loots again). <paramref name="close"/>: close the window, as a
    /// finished corpse is (ACE holds an open chest for its viewer, and some chests regenerate on
    /// close); false when it was closed already.
    /// </summary>
    private void FinishChestLoot(int chestId, string why, bool close, bool sayNothingMatched)
    {
        if (why == "completed")
            _lastChestLootedId = unchecked((uint)chestId);   // ralastchestlooted[]
        // Pickup with macro off > Leave the chest open: a chest the player opened stays open, and chat says so.
        bool leaveOpen = close && chestId == _manualChestLootId && (_dashboard?.Settings?.LootChestsMacroOffLeaveOpen ?? false);
        if (chestId == _manualChestLootId)
            _manualChestLootId = 0;
        if (close && !leaveOpen)
            CloseCorpseContainer(chestId, why);

        string chestName = _objectCache?[chestId]?.Name is { Length: > 0 } cn ? cn : "the chest";
        if (sayNothingMatched && NothingLootedLine(chestName, chest: true) is { } nothingLine)
            ChatLine(nothingLine);
        if (leaveOpen)
        {
            string done = why == "completed"
                ? _corpseItemsLooted > 0 ? $"done with {chestName}: took {_corpseItemsLooted} item(s)." : $"done with {chestName}."
                : $"stopped on {chestName} ({why}).";
            ChatLine($"[RynthAi] Pickup with macro off: {done} Left it open: close it when you're done.");
        }
        LootDiag($"[RynthAi] Chest loot: done with chest 0x{(uint)chestId:X8} ({why}); not marked, so opening it again loots it again.");

        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        _pendingManaStoneIds.Clear(); _pendingManaTapIds.Clear(); _pendingKeepUpTo.Clear();
        _corpseIdsRequested = false;
        ResetCurrentLootItem();
        if (_openedContainerId == chestId)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
        }
        if (_targetCorpseId == chestId)
        {
            StopCorpseMovement();
            _targetCorpseId = 0;
            _corpseTargetSince = 0;
            _lastCorpseOpenAttemptAt = 0;
            _lastLootActionAt = 0;
        }
        if (_chestCandidateId == chestId)
            _chestCandidateId = 0;
        _lootChestId = 0;
    }

    /// <summary>OnStopViewingObjectContents / DeleteObject for a chest. True when it was the chest being looted.</summary>
    private bool OnChestGone(int id, string how)
    {
        bool wasCandidate = _chestCandidateId == id;
        if (wasCandidate)
            _chestCandidateId = 0;
        if (!IsChestLoot(id))
        {
            // Open and eligible, but closed before the looter took it (it was on a corpse, or in a fight).
            if (wasCandidate && how == ChestClosedHow)
                ConsiderChestReopen(id, taken: false);
            return false;
        }
        bool active = _openedContainerId == id || _targetCorpseId == id;
        if (!active)
        {
            _lootChestId = 0;
            return true;
        }
        // Not a chest the player opened with the macro off: that one is the player's to close.
        bool reopens = how == ChestClosedHow && id != _manualChestLootId && ConsiderChestReopen(id, taken: true);
        if (!reopens)
            LootDiag($"[RynthAi] Chest loot: chest 0x{(uint)id:X8} {how} before looting finished; not reopening it (open it again to loot the rest).");
        FinishChestLoot(id, how, close: false, sayNothingMatched: false);
        return true;
    }

    // ── A chest that closes before the looter is done is opened again (2026-11-12) ──────────────
    // 8 of 134 chest loots in a week of logs ended "was closed before looting finished", with
    // items still to take. What closes it is the client: while its loot window shows a container it
    // checks once a second that the character stands within that container's use radius, and when
    // it doesn't it hides the window, which uses the container - and a use of a container you are
    // viewing closes it on the server (read from acclient.exe; RynthCore's LootWindowProbe logs
    // each one). A jump-loot hop, the server's walk back for the next pickup, a step toward a
    // monster or an open that needed a walk-up is enough. Opening a corpse closes an open chest
    // too (the server shows one container at a time).
    //
    // A corpse the looter was on is opened again (OnStopViewingObjectContents); a chest was not,
    // so the rest stayed in it until someone opened it again. Now the looter uses the chest again,
    // at most ChestReopenMax times a minute per chest: a chest that keeps closing (locked again,
    // a player closing it on purpose) is left after that. Only with the macro and Looting on, and
    // never for a chest the player opened with the macro off.

    /// <summary>OnChestGone's "how" for the server's close (the other is "left range": the object is gone).</summary>
    internal const string ChestClosedHow = "was closed";
    internal const int ChestReopenMax = 2;
    internal const long ChestReopenWindowMs = 60_000;
    /// <summary>After the close, before the use: the close settles (the client's window, the server's viewer).</summary>
    internal const long ChestReopenDelayMs = 600;
    /// <summary>The use is answered within this (a walk back included) or the chest is left.</summary>
    internal const long ChestReopenAnswerMs = 5_000;
    /// <summary>A reopen that got no turn in this long (a fight, the macro off) is dropped.</summary>
    internal const long ChestReopenStaleMs = 30_000;

    /// <summary>The chest to open again (0 = none).</summary>
    private int _chestReopenId;
    private long _chestReopenDueAt;
    /// <summary>When the use went out (0 = not yet).</summary>
    private long _chestReopenSentAt;
    private readonly Dictionary<int, (long FirstAt, int Count)> _chestReopens = new();

    private void ClearChestReopen()
    {
        _chestReopenId = 0;
        _chestReopenDueAt = 0;
        _chestReopenSentAt = 0;
    }

    /// <summary>
    /// Chest <paramref name="id"/> was closed with loot work left: books a reopen and logs it. False
    /// (nothing booked) with the macro or Looting off, for a container that isn't a chest to loot,
    /// or once it has been reopened ChestReopenMax times in the last minute (logged).
    /// <paramref name="taken"/>: the looter was on it; false: it was open but not taken yet.
    /// </summary>
    private bool ConsiderChestReopen(int id, bool taken)
    {
        var settings = _dashboard?.Settings;
        if (settings == null || !settings.IsMacroRunning || !settings.EnableLooting)
            return false;
        if (_objectCache?[id] == null)
            return false;
        if (!taken && JudgeChest(id, out _) != ChestVerdict.Loot)
            return false;

        long now = CorpseNowMs;
        int count = 0;
        long firstAt = now;
        if (_chestReopens.TryGetValue(id, out var seen) && now - seen.FirstAt < ChestReopenWindowMs)
        {
            count = seen.Count;
            firstAt = seen.FirstAt;
        }
        string before = taken ? "before looting finished" : "before the looter got to it";
        if (count >= ChestReopenMax)
        {
            LootDiag($"[RynthAi] Chest loot: chest 0x{(uint)id:X8} {ChestClosedHow} {before}, again: opened {count} times in the last minute already, so it is left (open it again to loot the rest).");
            return false;
        }
        if (_chestReopens.Count > 32)
            _chestReopens.Clear();
        _chestReopens[id] = (firstAt, count + 1);
        _chestReopenId = id;
        _chestReopenDueAt = now + ChestReopenDelayMs;
        _chestReopenSentAt = 0;
        LootDiag($"[RynthAi] Chest loot: chest 0x{(uint)id:X8} {ChestClosedHow} {before}; opening it again ({count + 1} of {ChestReopenMax}).");
        return true;
    }

    /// <summary>HasLootWork: a chest waits to be opened again (no state changed).</summary>
    private bool HasChestReopenWork() =>
        _chestReopenId != 0 && _targetCorpseId == 0 && _openedContainerId == 0;

    /// <summary>
    /// TickCorpseOpening, nothing claimed or open: uses the chest booked by ConsiderChestReopen once
    /// its delay is over, then waits for it to open (OnViewObjectContents makes it the candidate and
    /// TryTakeOpenChest takes it). True while this tick belongs to it, so no corpse is claimed and
    /// opened meanwhile (that would close the chest again).
    /// </summary>
    private bool TryReopenChest(LegacyUiSettings settings, long now)
    {
        int id = _chestReopenId;
        if (id == 0 || _targetCorpseId != 0 || _openedContainerId != 0)
            return false;
        if (_objectCache?[id] == null)
        {
            ClearChestReopen();
            return false;
        }

        if (_chestReopenSentAt != 0)
        {
            if (now - _chestReopenSentAt < ChestReopenAnswerMs)
                return true;
            LootDiag($"[RynthAi] Chest loot: chest 0x{(uint)id:X8} didn't open again within {ChestReopenAnswerMs / 1000} s of the use; it is left (open it again to loot the rest).");
            ClearChestReopen();
            return false;
        }
        if (now - _chestReopenDueAt >= ChestReopenStaleMs)
        {
            LootDiag($"[RynthAi] Chest loot: the reopen of chest 0x{(uint)id:X8} got no turn in {ChestReopenStaleMs / 1000} s; dropped (open it again to loot the rest).");
            ClearChestReopen();
            return false;
        }
        if (now < _chestReopenDueAt || _activity != BotActivity.Looting)
            return true;

        if (IsLootingBlockedByPack(settings))
        {
            LootDiag($"[RynthAi] Chest loot: not reopening chest 0x{(uint)id:X8} - the pack is full (Stop Looting When Pack Full).");
            ClearChestReopen();
            return false;
        }
        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        double distance = _objectCache.Distance(unchecked((int)_playerId), id);
        if (double.IsNaN(distance) || distance > Math.Max(maxMeters * 1.5, maxMeters + 2.0))
        {
            LootDiag($"[RynthAi] Chest loot: not reopening chest 0x{(uint)id:X8} - it is {distance:0.0} m away, past the loot range ({maxMeters:0.0} m).");
            ClearChestReopen();
            return false;
        }

        PauseNavigationForCorpse();
        StopCorpseMovement();
        bool sent = Host.UseFor(unchecked((uint)id), "Loot", "open the chest again: it closed before the looter was done");
        if (!sent)
        {
            LootDiag($"[RynthAi] Chest loot: the use to reopen chest 0x{(uint)id:X8} wasn't taken; it is left (open it again to loot the rest).");
            ClearChestReopen();
            return false;
        }
        _chestReopenSentAt = now;
        LootDiag($"[RynthAi] Chest loot: used chest 0x{(uint)id:X8} to open it again.");
        return true;
    }

    // ── Pickup with macro off (2026-11-02) ─────────────────────────────────────────────────────
    // A chest the player opens is looted by the loaded loot profile with the macro or Looting off
    // (LootChestsMacroOff, on by default), the way /ra lootthis would. With both on the macro's
    // own chest looting (above) takes it. Only a chest JudgeChest would loot: never a corpse,
    // a vendor, a player, house storage or a pack the character carries.

    private const long ManualChestWaitMs = 3000;   // for the chest's owner and name to be readable
    private int _manualChestId;
    private long _manualChestSince;
    /// <summary>The chest Pickup with macro off is looting (0 = none): FinishChestLoot leaves it open when set to.</summary>
    private int _manualChestLootId;

    /// <summary>OnViewObjectContents for a non-corpse container: a chest the player may have opened.</summary>
    private void NoteManualChestOpened(int id)
    {
        _manualChestId = id;
        _manualChestSince = CorpseNowMs;
    }

    /// <summary>TickCorpseOpening, every tick: hands a chest the player opened to the looter (see above).</summary>
    private void TakeManualChest(LegacyUiSettings settings)
    {
        int id = _manualChestId;
        if (id == 0) return;
        if (!settings.LootChestsMacroOff || (settings.IsMacroRunning && settings.EnableLooting)
            || _chestKey?.Running == true || _forcedLootContainerId == id)
        {
            _manualChestId = 0;   // off, the macro's own looting has it, or the chest looter does
            return;
        }
        ChestVerdict verdict = JudgeChest(id, out string why);
        if (verdict == ChestVerdict.Wait && CorpseNowMs - _manualChestSince < ManualChestWaitMs) return;
        _manualChestId = 0;
        if (verdict != ChestVerdict.Loot || LootThisRefusal(id) != null)
        {
            LootDiag($"[RynthAi] Pickup with macro off: not looting 0x{(uint)id:X8} ({(verdict == ChestVerdict.Loot ? LootThisRefusal(id) : why)}).");
            return;
        }
        LootDiag($"[RynthAi] Pickup with macro off: looting the opened chest 0x{(uint)id:X8} by the loot profile.");
        ForceLootOpenChest(id);
        _manualChestLootId = id;
    }
}
