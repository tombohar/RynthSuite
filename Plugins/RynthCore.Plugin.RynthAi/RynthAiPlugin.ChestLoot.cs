using System;
using RynthCore.Plugin.RynthAi.LegacyUi;

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
        _corpseItemsEvaluated = 0;
        _corpseItemsMatched = 0;
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
        if (close)
            CloseCorpseContainer(chestId, why);

        if (sayNothingMatched && _corpseItemsEvaluated > 0 && _corpseItemsMatched == 0)
        {
            string chestName = _objectCache?[chestId]?.Name ?? $"0x{(uint)chestId:X8}";
            ChatLine($"[RynthAi] Nothing in {chestName} matches the loot profile.");
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
        if (_chestCandidateId == id)
            _chestCandidateId = 0;
        if (!IsChestLoot(id))
            return false;
        bool active = _openedContainerId == id || _targetCorpseId == id;
        if (!active)
        {
            _lootChestId = 0;
            return true;
        }
        LootDiag($"[RynthAi] Chest loot: chest 0x{(uint)id:X8} {how} before looting finished; not reopening it (open it again to loot the rest).");
        FinishChestLoot(id, how, close: false, sayNothingMatched: false);
        return true;
    }
}
