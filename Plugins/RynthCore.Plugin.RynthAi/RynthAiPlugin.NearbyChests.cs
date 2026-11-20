using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// Loot Nearby Chests (2026-11-14), the host side. NearbyChestLooter.cs is the state machine and has
// the rules; this file feeds it the game.
//
// Setting: Settings > Looting > Loot Nearby Chests (LootNearbyChests, off by default; the meta option
// lootnearbychests). Status: /ra chests (always answers).
//
// How it sits in the loot tick (CorpseOpenController.cs):
//   * HasLootWork: a chest it is working on keeps Looting; a chest to start on is loot work only
//     after corpses, and never while combat has targets (so the arbiter never gives a fight's tick
//     to a chest).
//   * TickCorpseOpeningCore: TickNearbyChests reads answers every tick; a new chest is started
//     only where no corpse is in range (the corpse claim found none), and while one is being worked
//     on no corpse is claimed (opening a corpse would close the chest).
//   * Each step is one use the server answers (ACE walks the character to the chest first, as for a
//     far corpse open); the open chest is taken by the chest looter (RynthAiPlugin.ChestLoot.cs)
//     and looted by the loot profile like any opened chest.
public sealed partial class RynthAiPlugin
{
    private NearbyChestLooter? _nearbyChests;
    private int _nearbyOpenedId;
    private long _nearbyOpenedAt;
    private long _nearbyBusySince;

    private const uint StypeSkillLockpick = 23;        // AC Skill.Lockpick
    private const uint StypeIntResistLockpick = 38;    // ACE PropertyInt.ResistLockpick
    private const uint StypeIntLockpickSuccessPct = 173; // ACE PropertyInt.AppraisalLockpickSuccessPercent

    private NearbyChestLooter NearbyChests => _nearbyChests ??= new NearbyChestLooter(new NearbyChestEnv(this));

    /// <summary>OnViewObjectContents for a non-corpse container: the proof a chest opened.</summary>
    private void NoteNearbyChestOpened(int id)
    {
        _nearbyOpenedId = id;
        _nearbyOpenedAt = CorpseNowMs;
    }

    /// <summary>OnChatWindowText: the lockpick's and the key's answers.</summary>
    private void NearbyChestsChat(string text) => _nearbyChests?.OnChat(text);

    /// <summary>The setting is on and nothing else owns chests now.</summary>
    private bool NearbyChestsEnabled(LegacyUiSettings? s)
        => s != null && s.LootNearbyChests && s.IsMacroRunning && s.EnableLooting
           && !BoostNavHoldsLoot(s) && _chestKey?.Running != true && _forcedLootContainerId == 0
           && !IsLootingBlockedByPack(s);

    private bool NearbyChestsFighting(LegacyUiSettings? s)
        => s != null && s.EnableCombat && _combatManager?.HasTargets == true;

    /// <summary>
    /// TickCorpseOpeningCore, every tick past the macro/Looting gate: the looter reads its answers.
    /// Off (or something else owns chests): whatever it was doing is dropped, nothing marked.
    /// </summary>
    private void TickNearbyChests(LegacyUiSettings settings)
    {
        if (_nearbyChests == null && !settings.LootNearbyChests) return;
        var looter = NearbyChests;
        looter.RelockWaitSec = settings.ChestRelockWaitSec;
        if (!NearbyChestsEnabled(settings))
        {
            looter.StandDown(!settings.LootNearbyChests ? "Loot Nearby Chests was turned off" : "something else has the chests (the Item Tool, /ra lootthis, a full pack or Boost Nav)");
            return;
        }
        looter.Tick(mayStart: false);
    }

    /// <summary>The macro or Looting went off: drop the chest being worked on.</summary>
    private void StandDownNearbyChests(string why) => _nearbyChests?.StandDown(why);

    /// <summary>
    /// TickCorpseOpeningCore, nothing claimed or open: a chest being worked on holds the tick (no corpse
    /// is claimed meanwhile); in its Looting step the chest looter has it and this lets go.
    /// </summary>
    private bool NearbyChestHoldsTick()
        => _nearbyChests is { Busy: true } n && n.State != NearbyChestLooter.Phase.Looting;

    /// <summary>TickCorpseOpeningCore, no corpse in range: start on a nearby chest. True when this tick is spent on it.</summary>
    private bool TryStartNearbyChest(LegacyUiSettings settings)
    {
        if (!settings.LootNearbyChests || !NearbyChestsEnabled(settings)) return false;
        var looter = NearbyChests;
        looter.Tick(mayStart: true);
        return looter.Busy;
    }

    /// <summary>HasLootWork: the looter is on a chest (no state changed but the scan).</summary>
    private bool NearbyChestBusy(LegacyUiSettings settings)
        => settings.LootNearbyChests && _nearbyChests is { Busy: true } && NearbyChestsEnabled(settings);

    /// <summary>HasLootWork, after corpses: a chest in loot range to start on (never while fighting).</summary>
    private bool HasNearbyChestWork(LegacyUiSettings settings)
        => settings.LootNearbyChests && NearbyChestsEnabled(settings) && NearbyChests.Candidate() != 0;

    // ── /ra chests ──────────────────────────────────────────────────────────

    private void HandleNearbyChestsCommand()
    {
        var s = _dashboard?.Settings;
        if (s == null) { ChatLine("[RynthAi] Loot Nearby Chests: settings not ready."); return; }
        string on = s.LootNearbyChests ? "on" : "off (Settings > Looting > Loot Nearby Chests)";
        string why = !s.LootNearbyChests ? "" : !s.IsMacroRunning ? " - waits for the macro"
            : !s.EnableLooting ? " - waits for Looting" : !NearbyChestsEnabled(s) ? " - something else has the chests now"
            : NearbyChestsFighting(s) ? " - waits for the fight" : "";
        var looter = _nearbyChests;
        ChatLine($"[RynthAi] Loot Nearby Chests: {on}{why}. {(looter == null ? "Nothing done yet." : looter.Status)}");
        if (looter == null) return;
        int n = 0;
        foreach (var (id, name, done, leftMs, reason) in looter.LeftAlone())
        {
            if (++n > 8) { ChatLine("[RynthAi]   ... and more."); break; }
            ChatLine($"[RynthAi]   0x{(uint)id:X8} '{name}': {(done ? "looted" : "skipped")} ({reason}); again in {Math.Max(1, leftMs / 1000)} s.");
        }
    }

    // ── The looter's view of the game ───────────────────────────────────────

    private sealed class NearbyChestEnv : INearbyChestEnv
    {
        private readonly RynthAiPlugin _p;
        public NearbyChestEnv(RynthAiPlugin p) => _p = p;
        private RynthCore.PluginSdk.RynthCoreHost Host => _p.Host;
        private LegacyUiSettings? S => _p._dashboard?.Settings;

        public long Now => CorpseNowMs;
        public bool Enabled => _p.NearbyChestsEnabled(S);
        public bool Fighting => _p.NearbyChestsFighting(S);

        public IReadOnlyList<NearbyChest> ScanChests()
        {
            var list = new List<NearbyChest>();
            var cache = _p._objectCache;
            var s = S;
            if (cache == null || s == null || _p._playerId == 0) return list;
            double range = GetCorpseApproachRangeMaxMeters(s);
            foreach (var wo in cache.GetLandscapeObjects())
            {
                if (wo.ObjectClass != AcObjectClass.Container) continue;   // the cheap test first
                if (wo.Id == _p._lootChestId || wo.Id == _p._chestCandidateId || wo.Id == _p._chestReopenId) continue;   // open already
                double d = cache.Distance(unchecked((int)_p._playerId), wo.Id);
                if (double.IsNaN(d) || d > range) continue;
                if (_p.NearbyChestRefusal(wo.Id) != null) continue;
                list.Add(new NearbyChest(wo.Id, wo.Name, d));
            }
            return list;
        }

        public bool InWorld(int id) => _p._objectCache?[id] != null;

        public bool StillEligible(int id, out string why)
        {
            why = "";
            var cache = _p._objectCache;
            var s = S;
            if (cache == null || s == null) { why = "not ready"; return false; }
            string? refusal = _p.NearbyChestRefusal(id);
            if (refusal != null) { why = refusal; return false; }
            double range = GetCorpseApproachRangeMaxMeters(s);
            double d = cache.Distance(unchecked((int)_p._playerId), id);
            if (double.IsNaN(d) || d > Math.Max(range * 1.5, range + 2.0))
            {
                why = $"it is {d:0.0} m away, past the loot range ({range:0.0} m)";
                return false;
            }
            return true;
        }

        public string? HoldReason()
        {
            if (ActionPacer.IsPortaling(Host)) return "portal space";
            if (_p._activity != BotActivity.Looting) return "Looting doesn't have the tick";
            if (_p._targetCorpseId != 0 || _p._openedContainerId != 0) return "the corpse looter is busy";
            if (_p.GroundPickupInFlight) return "a ground pickup is in flight";
            if (_p._salvageManager?.PanelPending == true) return "the salvage panel is opening";
            if (_p.ItemMoveHold() is string move) return move;   // R-20261117-230613-88c5: the client would refuse the use
            if (!ActionPacer.MayStart(Host, "Loot", HealNeed.None, out string why))
                return why.Length > 0 ? why : "the server owes an answer";
            long now = CorpseNowMs;
            if (_p._busyCount > 0)
            {
                if (_p._nearbyBusySince == 0) _p._nearbyBusySince = now;
                if (now - _p._nearbyBusySince < OpenBusyWaitMaxMs) return $"the client is busy ({_p._busyCount})";
            }
            else _p._nearbyBusySince = 0;
            return null;
        }

        public bool CanAppraise => Host.HasRequestId && (Host.HasGetLastIdTime || Host.HasHasAppraisalData);
        public bool RequestAppraisal(int chestId) => Host.RequestId(unchecked((uint)chestId));
        public long AppraisalStamp(int chestId)
        {
            uint id = unchecked((uint)chestId);
            if (Host.HasGetLastIdTime) return Host.GetLastIdTime(id);
            return Host.HasHasAppraisalData && Host.HasAppraisalData(id) ? 1 : 0;
        }

        public bool? Locked(int chestId)
            => Host.HasGetObjectBoolProperty && Host.TryGetObjectBoolProperty(unchecked((uint)chestId), StypeBoolLocked, out bool v) ? v : null;
        public int? LockResistance(int chestId)
            => Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(unchecked((uint)chestId), StypeIntResistLockpick, out int v) ? v : null;
        public int? PickSuccessPercent(int chestId)
            => Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(unchecked((uint)chestId), StypeIntLockpickSuccessPct, out int v) ? v : null;

        public bool LockpickSkill(out int buffed, out bool trained)
        {
            buffed = 0; trained = false;
            if (_p._playerId == 0 || !Host.HasGetObjectSkill
                || !Host.TryGetObjectSkill(_p._playerId, StypeSkillLockpick, out buffed, out int training))
                return false;
            trained = training >= 2;   // 2 trained, 3 specialized
            return true;
        }

        public IReadOnlyList<ChestTool> Keys()
        {
            var keys = new List<ChestTool>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var wo in _p.NearbyCarried())
                if (wo.ObjectClass == AcObjectClass.Key && names.Add(wo.Name))
                    keys.Add(new ChestTool(wo.Id, wo.Name));
            return keys;
        }

        public ChestTool? Lockpick()
        {
            // The Consumable Items list first (as the door logic), then any lockpick carried.
            if (_p.FindLockpickInConsumables() is WorldObject listed && _p.IsCarriedByPlayer(listed.Id))
                return new ChestTool(listed.Id, listed.Name);
            foreach (var wo in _p.NearbyCarried())
                if (wo.ObjectClass == AcObjectClass.Lockpick)
                    return new ChestTool(wo.Id, wo.Name);
            return null;
        }

        public bool HasUseDone => Host.HasUseDoneSeq;
        public int UseDoneSeq() => Host.HasUseDoneSeq ? Host.GetUseDoneSeq() : 0;
        public bool LastUseDone(out int seq, out uint error)
        {
            seq = 0; error = 0;
            return Host.HasGetLastUseDone && Host.TryGetLastUseDone(out seq, out error);
        }

        public bool SendUseWith(int toolId, int chestId, string why)
        {
            _p.PauseNavigationForCorpse();
            _p.StopCorpseMovement();
            return Host.UseOnFor(unchecked((uint)toolId), unchecked((uint)chestId), "NearbyChests", why);
        }

        public bool SendOpen(int chestId)
        {
            _p.PauseNavigationForCorpse();
            _p.StopCorpseMovement();
            return Host.UseFor(unchecked((uint)chestId), "NearbyChests", "Loot Nearby Chests: open the chest");
        }

        public long OpenedAt(int chestId) => _p._nearbyOpenedId == chestId ? _p._nearbyOpenedAt : 0;

        public bool Looting(int chestId)
            => _p._chestCandidateId == chestId || _p._chestReopenId == chestId
               || (_p.IsChestLoot(chestId) && (_p._openedContainerId == chestId || _p._targetCorpseId == chestId));

        public void Log(string text) => _p.LootDiag(text);
    }

    /// <summary>Why the world object <paramref name="id"/> is not a chest to loot (null = it is one).</summary>
    private string? NearbyChestRefusal(int id)
    {
        var wo = _objectCache?[id];
        if (wo == null) return "it is gone";
        if (unchecked((uint)id) == _playerId) return "your main pack";
        uint uid = unchecked((uint)id);
        uint bf = 0;
        bool bfKnown = Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(uid, out bf);
        uint houseOwner = 0;
        if (Host.HasGetObjectInstanceIdProperty && Host.TryGetObjectInstanceIdProperty(uid, ChestIidHouseOwner, out uint ho)) houseOwner = ho;
        string name = wo.Name;
        if (string.IsNullOrWhiteSpace(name) && Host.TryGetObjectName(uid, out string hostName)) name = hostName;
        uint container = 0, wielder = 0;
        bool ownKnown = Host.HasGetObjectOwnershipInfo && Host.TryGetObjectOwnershipInfo(uid, out container, out wielder, out _);
        AcObjectClass cls = IsCorpseLikeObject(id) ? AcObjectClass.Corpse : wo.ObjectClass;
        return NearbyChestLooter.ChestRefusal(cls, bfKnown, bf, houseOwner, name, ownKnown, container, wielder);
    }

    /// <summary>What the character carries (the pack walk, else the cache's inventory).</summary>
    private IEnumerable<WorldObject> NearbyCarried()
    {
        var cache = _objectCache;
        if (cache == null) yield break;
        IReadOnlyList<WorldObject> direct = cache.GetDirectInventory();
        IEnumerable<WorldObject> items = direct.Count > 0 ? direct : cache.GetInventory();
        foreach (var wo in items)
            if (wo != null && IsCarriedByPlayer(wo.Id) && !WorldObjectCache.IsWieldedByPlayer(Host, wo))
                yield return wo;
    }
}
