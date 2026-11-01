// ============================================================================
//  RynthSuite - Combat/CombatManager.SelectedTarget.cs
//  "Fight the monster I select" (Settings > Ranges > Combat Targeting, off by default;
//  /ra fightselected [on|off]; the FightSelectedTarget setting key): the automatic form of
//  the target lock (CombatManager.TargetLock.cs). When the PLAYER selects an attackable monster
//  in game, it becomes the forced target: fought ahead of the Targeting setting and the
//  Priority tiers until it dies, can't be reached (blacklisted, or out of the scan for
//  ForcedTargetLostMs from the click), or the player selects another monster or deselects.
//
//  Player or bot? RynthAi's own selections are marked right before Host.SelectItem
//  (ItemInfo.SelfSelect: combat's native attack re-selecting its target, the looter's corpse
//  and item picks, a meta's selectitem). A selection change the bot didn't just make is the
//  player's: a click in the game world, the radar, or another plugin's window (RynthDefective's
//  Selection window, RynthLua scripts) all go through the same in-game selection and count.
//
//  Followed: monsters the combat scan accepts - a Monster, attackable, alive, not a player,
//  not a pet, not blacklisted, not Priority 0 ("ignore") and not on the never-attack list
//  (/ra targetlock still forces those). Not followed (nothing changes, the lock stays): items,
//  corpses, NPCs, players, the character itself.
//  Reach: like an engaged target, out to Disengage Range (Monster Range + 3 by default). A
//  monster selected farther away is said once in chat; it is fought if it comes within that
//  reach in time, never chased past it.
//  A /ra targetlock lock is the player's explicit pick: selections never move it. /ra targetlock
//  on a monster the setting is already fighting turns it into that explicit lock.
//  Only selections made while the macro runs with Combat on are followed.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager
{
    private const string SelectedSettingOff = "the setting was turned off";

    // The forced target came from the player's selection (Fight the monster I select), not from
    // /ra targetlock. Written with _forcedTargetId; the game thread sets it, the pump reads it.
    private volatile bool _forcedBySelection;
    private long _selectedAtTicks;           // TargetClock ticks of the click that set it
    private int _selectedFarNotedId;         // the "too far" chat line, once per monster

    /// <summary>The forced target is the monster the player selected (not a /ra targetlock).</summary>
    public bool ForcedBySelection => _forcedBySelection && ForcedTargetId != 0;

    private DateTime SelectedAt
    {
        get
        {
            long t = Interlocked.Read(ref _selectedAtTicks);
            return t == 0 ? DateTime.MinValue : new DateTime(t);
        }
    }

    /// <summary>Why the player's selection of <paramref name="id"/> isn't followed, or null.</summary>
    internal string? SelectedTargetRefusal(int id)
    {
        string? refusal = ForcedTargetRefusal(id);
        if (refusal != null) return refusal;
        var wo = _worldFilter[id]!;   // known: ForcedTargetRefusal checked it
        if (_host.HasGetObjectWcid && _host.TryGetObjectWcid(unchecked((uint)id), out uint wcid) && wcid != 0
            && CreatureData.PlayerWeenies.IsPlayerWcid(wcid))
            return $"{wo.Name} is a player";
        if (_blacklistManager.IsBlacklisted(id)) return $"{wo.Name} is blacklisted (unreachable)";
        if (IsUserBlacklistedName(wo.Name)) return $"{wo.Name} is on the never-attack list";
        if (PriorityOf(id) <= 0) return $"{wo.Name} is set to ignore (Priority 0)";
        return null;
    }

    /// <summary>
    /// The in-game selection changed to <paramref name="selected"/> (0 = deselected).
    /// <paramref name="ours"/>: RynthAi selected it itself (ItemInfo.SelfSelect), so it's not
    /// the player's choice. Returns what it did, for the log and tests (null = nothing).
    /// </summary>
    internal string? OnSelectionChanged(uint selected, bool ours)
    {
        if (!_settings.FightSelectedTarget || ours) return null;
        int fid = ForcedTargetId;
        if (fid != 0 && !_forcedBySelection) return null;   // a /ra targetlock lock: kept

        if (selected == 0)
        {
            if (fid == 0) return null;
            string gone = _worldFilter[fid]?.Name ?? $"0x{(uint)fid:X8}";
            ClearForcedTarget();
            _host.Log($"[CombatTarget] fight-selected: deselected, 0x{(uint)fid:X8} '{gone}' released");
            return "deselected";
        }

        int id = unchecked((int)selected);
        if (id == fid) return null;
        if (!_settings.IsMacroRunning || !_settings.EnableCombat) return null;

        string? refusal = SelectedTargetRefusal(id);
        if (refusal != null)
        {
            // Monsters only: the player's item and corpse clicks aren't worth a log line.
            var seen = _worldFilter[id];
            if (seen != null && (int)seen.ObjectClass == (int)AcObjectClass.Monster)
                _host.Log($"[CombatTarget] fight-selected: 0x{selected:X8} '{seen.Name}' not followed: {refusal}");
            return refusal;
        }

        var wo = _worldFilter[id]!;
        Interlocked.Exchange(ref _selectedAtTicks, TargetClock().Ticks);
        _forcedBySelection = true;
        Interlocked.Increment(ref _forcedSetSeq);
        Volatile.Write(ref _forcedTargetId, id);

        double reach = Math.Max(_settings.MonsterRange, DisengageDistance);
        double dist = _worldFilter.Distance(unchecked((int)_playerId), id);
        bool far = dist < double.MaxValue && dist > reach;
        _host.Log($"[CombatTarget] fight-selected: the player selected 0x{selected:X8} '{wo.Name}'" +
                  (dist < double.MaxValue ? $" at {dist:0.0} m" : "") + (far ? $" (past the {reach:0} m reach)" : "") + ": fighting it first");
        if (far && _selectedFarNotedId != id)
        {
            _selectedFarNotedId = id;
            _host.WriteToChat($"[RynthAi] {wo.Name} is {dist:0} yd away, past Disengage Range ({reach:0} yd): " +
                              $"fighting it if it comes that close in the next {ForcedTargetLostMs / 1000:0} s, not chasing it.", 1);
        }
        return far ? "far" : "locked";
    }

    /// <summary>
    /// /ra targetlock on the monster this setting already fights: it becomes the explicit lock
    /// (selections no longer move it). True when it did.
    /// </summary>
    internal bool PromoteSelectedToTargetLock(int id)
    {
        if (id == 0 || !_forcedBySelection || ForcedTargetId != id) return false;
        _forcedBySelection = false;
        return true;
    }
}
