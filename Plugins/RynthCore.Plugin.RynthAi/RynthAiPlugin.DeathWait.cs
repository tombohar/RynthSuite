// RynthAiPlugin.DeathWait.cs - the death animation, learned per monster and waited out (2026-11-08).
//
// Tom: "the bot is running away from a monster it's killed and then turning around and running back
// because there is a second or two for the corpse to show up due to a death animation", and "with
// looting on I'd like it to wait that duration before running away, and maybe an extra second to let
// the corpse spawn and be looted. As soon as the corpse is looted the bot can run away, if no corpse
// spawns it needs to continue."
//
// Learning: ACE creates the corpse once the death animation has played, so the time from our kill
// notice to its "Corpse of <name>" object (CorpseTimer) is that monster's death. It goes to the
// session's monster stats ("cd", "cn") and to the learned table (MonsterXpTable, kept across logins).
//
// The wait: with the macro and Looting on, our kill starts a wait of the monster's learned death (or
// DeathWaitDefaultMs while it isn't learned) plus DeathWaitExtraMs. While it runs, looting counts as
// work, the arbiter gives Looting the tick over combat (ArbiterInputs.DeathWait, as Boost Loot does)
// and combat locks no new target (CombatManager.HoldNewTarget), so neither a fight's walk, nor nav,
// nor the Outdoor Hunt takes the character away. A monster within CloseAttackYards is still fought
// (HoldNewTarget and CombatEngaged both let it through). It ends when the corpse is looted, when no
// corpse came in the time, or DeathWaitDecideMs after a corpse came that the looter hasn't claimed
// (the loot rules don't take it, or the looter leaves it for the fight: then the fight goes on and the
// looter comes back for it; holding combat there stood the character still, both waiting on the other).
// Pump thread throughout.
using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.MonsterStats;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>A monster's death while it isn't learned yet (most AC deaths take 1.5-3 s).</summary>
    internal const long DeathWaitDefaultMs = 2500;
    /// <summary>Waited past the death, for the corpse to come and be seen.</summary>
    internal const long DeathWaitExtraMs = 1000;
    /// <summary>After the corpse came: this long for the loot rules to say whether it is looted (its ID).</summary>
    internal const long DeathWaitDecideMs = 1000;
    /// <summary>A corpse being looted holds the wait at most this long past it (never a wedge).</summary>
    internal const long DeathWaitLootMaxMs = 20000;
    /// <summary>A learned death longer than this is no death animation (the corpse came late for some other reason).</summary>
    internal const long DeathWaitMaxDeathMs = 8000;
    /// <summary>A corpse farther than this from the character isn't its kill's.</summary>
    private const double DeathCorpseNearYd = 80;

    private readonly CorpseTimer _corpseTimer = new();
    private long _deathWaitAt;        // CorpseNowMs of the kill being waited for; 0 = none
    private long _deathWaitMs;        // how long the wait is
    private string _deathWaitName = "";
    private int _deathCorpseId;       // its corpse, once it came
    private long _deathCorpseAt;
    private bool _deathWaitHeld;      // HasLootWork's answer this tick (the arbiter's DeathWait input)

    /// <summary>The death wait can run: the macro and Looting on, and the looter able to loot now.</summary>
    private bool DeathWaitCanRun(LegacyUiSettings settings)
        => settings.IsMacroRunning && settings.EnableLooting
           && !BoostNavHoldsLoot(settings)
           && !settings.LootOnlyRareCorpses            // rare-only: most corpses have nothing to wait for
           && !LootModes.IsRares(settings.LootMode)
           && !IsLootingBlockedByPack(settings)
           && !_useRefusals.HoldsOpens(CorpseNowMs)
           && GetCorpseApproachRangeMaxMeters(settings) > 0.25;

    /// <summary>Our kill of <paramref name="name"/> (the kill notice's monster, null when none was named): its corpse is on its way.</summary>
    private void StartDeathWait(string? name)
    {
        var s = _dashboard?.Settings;
        if (s == null || !DeathWaitCanRun(s)) return;
        double? learned = string.IsNullOrWhiteSpace(name) ? null : _xpTable.DeathMs(name);
        long death = learned is double d ? (long)Math.Round(Math.Clamp(d, 0, DeathWaitMaxDeathMs)) : DeathWaitDefaultMs;
        _deathWaitMs = death + DeathWaitExtraMs;
        _deathWaitAt = CorpseNowMs;
        _deathWaitName = name?.Trim() ?? "";
        _deathCorpseId = 0;
        _deathCorpseAt = 0;
        string who = _deathWaitName.Length > 0 ? $"'{_deathWaitName}'" : "the kill";
        string why = learned != null
            ? $"its death takes {death / 1000.0:0.0} s, learned over {_xpTable.DeathsTimed(name)} {(_xpTable.DeathsTimed(name) == 1 ? "kill" : "kills")}"
            : $"its death isn't learned yet: {DeathWaitDefaultMs / 1000.0:0.0} s";
        Host.Log($"[RynthAi] Kill: waiting up to {_deathWaitMs / 1000.0:0.0} s for the corpse of {who} ({why}, and {DeathWaitExtraMs / 1000.0:0.0} s more) before moving on.");
    }

    /// <summary>The wait is over: says why, once.</summary>
    private void EndDeathWait(string why)
    {
        if (_deathWaitAt == 0) return;
        long took = CorpseNowMs - _deathWaitAt;
        string who = _deathWaitName.Length > 0 ? $"'{_deathWaitName}'" : "the kill";
        Host.Log($"[RynthAi] Kill: done waiting for the corpse of {who} after {took / 1000.0:0.0} s: {why}.");
        _deathWaitAt = 0;
        _deathCorpseId = 0;
        _deathCorpseAt = 0;
        _deathWaitName = "";
        _deathWaitHeld = false;
    }

    /// <summary>
    /// The wait holds the character now (and ends it, with its line, when it is over). Before the
    /// corpse: until the wait's time is up. With the corpse: while it is claimed, open or lootable in
    /// range (up to DeathWaitLootMaxMs), else DeathWaitDecideMs for the loot rules' look.
    /// </summary>
    private bool DeathWaitHolds(LegacyUiSettings settings)
    {
        if (_deathWaitAt == 0) return false;
        if (!DeathWaitCanRun(settings)) { EndDeathWait("looting stopped or can't loot now"); return false; }
        long now = CorpseNowMs, since = now - _deathWaitAt;
        if (_deathCorpseId == 0)
        {
            if (since < _deathWaitMs) return true;
            EndDeathWait($"no corpse came in {_deathWaitMs / 1000.0:0.0} s, carrying on");
            return false;
        }
        if (_completedCorpses.ContainsKey(_deathCorpseId)) { EndDeathWait("its corpse is looted"); return false; }
        // Only a corpse the looter has claimed or opened holds the character. One that is merely
        // lootable in range while the looter defers to the fight ("waits for combat") must not: the
        // wait would hold combat's next target while looting waits for combat, and the character
        // stood still until the looter's own 8 s rule (2026-11-08: 16 s for a 1.2 s death).
        bool looting = _targetCorpseId == _deathCorpseId || _openedContainerId == _deathCorpseId;
        if (looting)
        {
            if (since < _deathWaitMs + DeathWaitLootMaxMs) return true;
            EndDeathWait("looting its corpse is taking too long, carrying on");
            return false;
        }
        if (now - _deathCorpseAt < DeathWaitDecideMs) return true;
        EndDeathWait(_lootableInRange.Contains(_deathCorpseId)
            ? "its corpse came but the looter leaves it for the fight (it comes back for it), carrying on"
            : "its corpse came but the loot rules don't take it, carrying on");
        return false;
    }

    /// <summary>A corpse was looted (MarkCorpseCompleted): when it is the kill's (or the first seen since the kill), the wait is over.</summary>
    private void EndDeathWaitFor(int corpseId)
    {
        if (_deathWaitAt == 0) return;
        bool kills = _deathCorpseId != 0
            ? corpseId == _deathCorpseId
            : !_corpseWaitingSince.TryGetValue(corpseId, out long seen) || seen >= _deathWaitAt;
        if (kills) EndDeathWait("its corpse is looted");
    }

    /// <summary>
    /// OnCreateObject: a "Corpse of X" near the character. Times X's death (CorpseTimer: the session's
    /// stats and the learned table), and is the waited-for kill's corpse when the names match.
    /// </summary>
    private void NoteCorpseCreated(uint objectId)
    {
        if (_corpseTimer.Waiting == 0 && _deathWaitAt == 0) return;
        if (!Host.HasGetObjectName || !Host.TryGetObjectName(objectId, out string name)) return;
        string? killed = CorpseTimer.KilledName(name);
        if (killed == null || !CorpseNearMe(objectId)) return;
        if (_corpseTimer.OnCorpse(name, CorpseNowMs) is var (who, ms) && ms <= DeathWaitMaxDeathMs)
        {
            _monsterStats.OnCorpseTime(who, ms, DateTime.UtcNow);
            _xpTable.RecordDeath(who, ms);
        }
        if (_deathWaitAt != 0 && _deathCorpseId == 0
            && (_deathWaitName.Length == 0 || killed.Equals(_deathWaitName, StringComparison.OrdinalIgnoreCase)))
        {
            _deathCorpseId = unchecked((int)objectId);
            _deathCorpseAt = CorpseNowMs;
        }
    }

    /// <summary>Within DeathCorpseNearYd of the character (true when either position can't be read).</summary>
    private bool CorpseNearMe(uint objectId)
    {
        if (!Host.HasGetObjectPosition || !Host.HasGetPlayerPose) return true;
        if (!Host.TryGetObjectPosition(objectId, out uint cell, out float x, out float y, out _) || cell == 0) return true;
        if (!Host.TryGetPlayerPose(out uint me, out float mx, out float my, out _, out _, out _, out _, out _) || me == 0) return true;
        double ax = ((cell >> 24) & 0xFF) * 192.0 + x, ay = ((cell >> 16) & 0xFF) * 192.0 + y;
        double bx = ((me >> 24) & 0xFF) * 192.0 + mx, by = ((me >> 16) & 0xFF) * 192.0 + my;
        // A dungeon's cells share one landblock origin: the local positions compare directly there too.
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy) <= DeathCorpseNearYd;
    }

    /// <summary>CombatManager.HoldNewTarget: Boost Loot Priority's hold, or the death wait's.</summary>
    private bool LootHoldsNewTarget()
    {
        if (BoostLootHoldsNewTarget()) return true;
        var s = _dashboard?.Settings;
        return s != null && DeathWaitHolds(s);
    }

    /// <summary>CombatManager.HoldNewTargetWhy: the words for its one log line.</summary>
    private string LootHoldWhy()
        => _deathWaitAt != 0 && !BoostLootHoldsNewTarget()
            ? "the kill's corpse comes first (its death animation, then the loot)"
            : "Boost Loot Priority loots the kill first";
}
