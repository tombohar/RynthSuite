// ============================================================================
//  RynthSuite - Combat/BuffManager.HpFailsafe.cs
//  The HP failsafe: at or under Emergency Heal At, a character whose heals keep
//  failing (refused, fizzled, never sent, or out-healed) must not sit and die.
//
//  Measured by health, not by counting heal attempts: a round of FailsafeRoundMs
//  with no rise of FailsafeRiseMinPct counts as a failed round (whatever the
//  reason: a stuck cast, 'too busy', no mana, no kits).
//    1. FailsafeRoundsToBreak failed rounds (FailsafeRoundsToRecallBreak with
//       HpFailsafeRecall on): ActionPacer.ForceBreak (a jump; on ACE's fast tick it
//       fails the cast in progress, Player.HandleActionJump). Starts are held until
//       landed, then the vitals step heals again.
//    2. HpFailsafeRecall on: at once, with the jump, the Lifestone Recall SPELL
//       (RecallSpellId), not "/lifestone" (2026-10-22: /lifestone's long kneel was
//       killing characters, and a move during it cancels it). The macro is switched
//       off first so nothing else casts, swings or moves over it; the recall goes on
//       the next heartbeat, through the emergency heal's Magic stance step, and is
//       cast again every RecallResendMs until portal space shows it went. No recall
//       for a character without the spell or the mana for it: it keeps healing.
//       Recall off: FailsafeRoundsToRecall more failed rounds are only logged.
//    3. No portal space after RecallMaxSends casts, or by RecallGiveUpMs (refused:
//       busy, out of mana, a fizzle run): the macro is switched back on, so the
//       heals and kits run again, and the failsafe re-arms; the next recall waits
//       RecallCooldownMs. A failed recall must not leave the character standing at
//       low health with nothing running. Only a macro the failsafe itself stopped is
//       switched back on, never past a VTank yield (MayRestartMacro), and a death
//       drops the pending recall (the player's Stop Macro On Death stands).
//  Health back over the line (or 100%) ends the episode. Nothing counts in portal space.
//  The failsafe (jump included) is for a character that can heal: it arms only with a heal that went
//  out in the last minute, or a heal spell / kit / health potion at hand (HealCapableNow). A melee
//  character with nothing to heal with is not jumped or recalled. HpFailsafeRecall defaults to off.
// ============================================================================

using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    internal const double FailsafeRoundMs = 2500;
    internal const int FailsafeRoundsToBreak = 3;
    /// <summary>With HpFailsafeRecall on, the jump (and the recall with it) comes sooner: 5 s, not 7.5 s.</summary>
    internal const int FailsafeRoundsToRecallBreak = 2;
    internal const int FailsafeRoundsToRecall = 3;
    // A rise this large (health %) means a heal landed; natural regeneration is smaller.
    private const int FailsafeRiseMinPct = 3;
    /// <summary>Lifestone Recall, the spell (SpellDatabase).</summary>
    internal const int RecallSpellId = 1635;
    // The recall is cast again this often until portal space shows it went (a fizzle, a refusal),
    // at most RecallMaxSends times, and given up RecallGiveUpMs after the macro stop.
    internal const double RecallResendMs = 3000;
    internal const int RecallMaxSends = 3;
    internal const double RecallGiveUpMs = 12_000;
    /// <summary>After a recall that never started, the next one waits this long (the heals run meanwhile).</summary>
    internal const double RecallCooldownMs = 30_000;

    private bool _fsActive;
    private DateTime _fsRoundStart;
    private int _fsLowHp;
    private int _fsFailedRounds;
    private bool _fsBroken;
    private bool _fsRecallLogged;
    private DateTime _fsRecallNotBefore = DateTime.MinValue;

    private bool _recallPending;
    private int _recallSends;
    private DateTime _recallNextAt;
    private DateTime _recallStartedAt;
    private string _recallWait = "";
    private bool _fsNoRecallLogged;

    /// <summary>Set by the plugin: the rest of a /ra stop (nav stop, pause cancel) for the recall.</summary>
    internal Action? MacroStopForRecall { get; set; }
    /// <summary>Set by the plugin: false when the macro must not be started now (VTank yield). Null = may.</summary>
    internal Func<bool>? MayRestartMacro { get; set; }

    // The macro is off because the failsafe stopped it for a recall (only then does it switch it back on).
    private bool _macroStoppedByFailsafe;

    internal int HpFailsafeBreaks { get; private set; }
    internal int HpFailsafeRecalls { get; private set; }
    internal int HpFailsafeRecallsFailed { get; private set; }

    private bool Portaling()
    {
        try { return _host.HasIsPortaling && _host.IsPortaling(); } catch { return false; }
    }

    /// <summary>Once per heartbeat while the macro runs, before the pacing gate.</summary>
    private void TickHpFailsafe()
    {
        int at = _settings.EmergencyHealAt;
        if (at <= 0 || _vitals.MaxHealth <= 0 || _recallPending || Portaling()) { ResetHpFailsafe(); return; }
        int hp = _vitals.HealthPct;
        DateTime now = Now;
        if (hp > at || hp >= 100)
        {
            if (_fsActive && (_fsFailedRounds > 0 || _fsBroken))
                _host.Log($"[Vitals] HP failsafe: health back to {hp}% (over Emergency Heal At {at}%).");
            ResetHpFailsafe();
            return;
        }
        // Only a character that can heal at all: a melee character with no spell, kit or potion
        // (resting, or out-healed) has nothing for the failsafe to retry, and a recall there is wrong.
        if (!HealCapableNow(now)) { ResetHpFailsafe(); return; }
        if (!_fsActive)
        {
            _fsActive = true;
            _fsRoundStart = now;
            _fsLowHp = hp;
            _fsFailedRounds = 0;
            _fsBroken = false;
            return;
        }
        if (hp >= _fsLowHp + FailsafeRiseMinPct)
        {
            // A heal landed: the heals work, start counting again from here.
            _fsLowHp = hp;
            _fsRoundStart = now;
            _fsFailedRounds = 0;
            return;
        }
        if (hp < _fsLowHp) _fsLowHp = hp;
        if ((now - _fsRoundStart).TotalMilliseconds < FailsafeRoundMs) return;

        _fsFailedRounds++;
        _fsRoundStart = now;
        double failedSec = _fsFailedRounds * FailsafeRoundMs / 1000.0;
        bool recallOn = _settings.HpFailsafeRecall;
        if (!_fsBroken)
        {
            if (_fsFailedRounds < (recallOn ? FailsafeRoundsToRecallBreak : FailsafeRoundsToBreak)) return;
            string why = $"HP failsafe: health {hp}% at or under Emergency Heal At {at}%, no heal landed in {failedSec:0.#} s";
            bool jumped = ActionPacer.ForceBreak(_host, why);
            if (jumped) HpFailsafeBreaks++;
            _fsBroken = true;
            _fsFailedRounds = 0;
            string? noRecall = recallOn ? RecallUnavailable(now) : null;
            if (recallOn && noRecall == null)
            {
                // No more waiting: the jump frees a stuck cast and the recall goes on the next heartbeat.
                _host.Log($"[Vitals] {why}: {(jumped ? "breaking the cast with a jump" : "no jump available (or one under way)")}, then Lifestone Recall at once.");
                StartFailsafeRecall(now, hp, failedSec);
                return;
            }
            _host.Log($"[Vitals] {why}: {(jumped ? "breaking the cast with a jump, then healing again" : "no jump available (or one under way), healing again")}" +
                      (noRecall != null ? $"; no recall: {noRecall}" : "") + ".");
            if (noRecall != null) _fsNoRecallLogged = true;
            return;
        }
        if (_fsFailedRounds < FailsafeRoundsToRecall) return;
        if (!recallOn)
        {
            if (!_fsRecallLogged)
            {
                _fsRecallLogged = true;
                _host.Log($"[Vitals] HP failsafe: health {hp}%, heals still failing {failedSec:0.#} s after the break; HP Failsafe Recall is off, no recall.");
            }
            return;
        }
        if (now < _fsRecallNotBefore)
        {
            // The last recall never started: heal on and break again, the next recall after the cooldown.
            _fsBroken = false;
            _fsFailedRounds = 0;
            return;
        }
        string? cannot = RecallUnavailable(now);
        if (cannot != null)
        {
            if (!_fsNoRecallLogged)
            {
                _fsNoRecallLogged = true;
                _host.Log($"[Vitals] HP failsafe: health {hp}%, heals still failing {failedSec:0.#} s after the break; no recall: {cannot}.");
            }
            return;
        }
        StartFailsafeRecall(now, hp, failedSec);
    }

    /// <summary>
    /// Why the Lifestone Recall spell can't go now (null = it can): the cooldown after one that never
    /// started, a spellbook that hasn't got it, or less mana than it costs. The character keeps healing.
    /// </summary>
    private string? RecallUnavailable(DateTime now)
    {
        if (now < _fsRecallNotBefore)
            return $"the last recall never started, the next in {(_fsRecallNotBefore - now).TotalSeconds:0} s";
        if (_spellManager.IsKnownSnapshotWarm && !_spellManager.IsKnownSpellId(RecallSpellId))
            return "this character doesn't know Lifestone Recall";
        int cost = SpellManaCost(RecallSpellId);
        if (cost > 0 && _vitals.MaxMana > 0 && _vitals.CurrentMana < cost)
            return $"mana {_vitals.CurrentMana} is under Lifestone Recall's cost ({cost})";
        return null;
    }

    // A heal (spell, kit or potion) that actually went out: the failsafe's proof the character can heal.
    private DateTime _lastHealSentAt = DateTime.MinValue;
    private DateTime _healCapableCheckAt = DateTime.MinValue;
    private bool _healCapableCached;
    /// <summary>A heal sent this recently proves the character can heal (kit used up since, a script's drain).</summary>
    internal const double HealSentProofMs = 60_000;

    /// <summary>Every heal that goes out (cast, kit, potion) records itself here, then feeds the latency tracker.</summary>
    private void NoteHealSent(string action)
    {
        _lastHealSentAt = Now;
        _healLatency.HealSent(Now, action);
    }

    /// <summary>
    /// Can this character heal at all? A heal went out in the last HealSentProofMs, or one could now: a known
    /// Heal Self / Stamina to Health Self with Life Magic trained, a healing kit, or a health potion
    /// (Use Potions on and one listed or in the pack). Read at most once a second (it walks the pack).
    /// </summary>
    private bool HealCapableNow(DateTime now)
    {
        if (_lastHealSentAt != DateTime.MinValue && (now - _lastHealSentAt).TotalMilliseconds < HealSentProofMs) return true;
        if ((now - _healCapableCheckAt).TotalMilliseconds < 1000 && _healCapableCheckAt != DateTime.MinValue) return _healCapableCached;
        _healCapableCheckAt = now;
        bool capable = false;
        try
        {
            if (IsSkillUsable(AcSkillType.LifeMagic))
            {
                foreach (string spell in new[] { "Heal Self", "Stamina to Health Self" })
                {
                    if (_spellManager.GetDynamicSelfBuffId(spell, AcSkillType.LifeMagic) != 0
                        || _spellManager.GetSelfSpellIdAboveAllowedTier(spell, AcSkillType.LifeMagic) != 0) { capable = true; break; }
                }
            }
            if (!capable && _worldObjectCache != null)
            {
                if (BestPackHealingKit() != null) capable = true;
                else
                {
                    foreach (var r in _settings.ConsumableRules)
                        if (r.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase)
                            && !(r.Name ?? "").Contains("Stamina Kit", StringComparison.OrdinalIgnoreCase)
                            && ConsumableLookup.Resolve(_worldObjectCache, r, KitUsedUp) is WorldObject wo && IsUseOnSelfKit(wo)) { capable = true; break; }
                }
                if (!capable && _settings.UsePotions && HasListedPotion("HealthPotion")) capable = true;
            }
        }
        catch { capable = false; }
        _healCapableCached = capable;
        return capable;
    }

    private void ResetHpFailsafe()
    {
        _fsActive = false;
        _fsFailedRounds = 0;
        _fsBroken = false;
        _fsRecallLogged = false;
        _fsNoRecallLogged = false;
    }

    private void StartFailsafeRecall(DateTime now, int hp, double failedSec)
    {
        HpFailsafeRecalls++;
        _settings.IsMacroRunning = false;
        _macroStoppedByFailsafe = true;
        MacroStopForRecall?.Invoke();
        // The heal the jump broke is not waited on, here or by the pacer: a stuck cast may never be
        // answered, and the recall must not wait for its breaker steps (seconds). It still waits to
        // land from the jump (ActionPacer's airborne hold is kept).
        if (_pendingSpellId != 0 && _pendingSpellId == _pendingVitalSpellId) _pendingSpellId = 0;
        ActionPacer.Forget("HP failsafe recall");
        _recallPending = true;
        _recallSends = 0;
        _recallStartedAt = now;
        _recallNextAt = now;   // the next heartbeat: the macro-stop edge has run by then
        _recallWait = "";
        string line = $"HP failsafe: health {hp}%, no heal landed in {failedSec:0.#} s: casting Lifestone Recall (macro off until it goes; back on if it can't).";
        _host.Log("[Vitals] " + line);
        _host.WriteToChat("[RynthAi] " + line, 2);
        ResetHpFailsafe();
    }

    /// <summary>Every heartbeat, macro running or not: casts a pending recall, or gives up on it.</summary>
    private void TickFailsafeRecall()
    {
        if (!_recallPending) return;
        DateTime now = Now;
        if (Portaling())
        {
            _recallPending = false;
            _macroStoppedByFailsafe = false;
            _host.Log($"[Vitals] HP failsafe: the recall went (portal space, {(now - _recallStartedAt).TotalMilliseconds:0} ms after the stop, {_recallSends} cast(s)).");
            return;
        }
        if (_vitals.MaxHealth > 0 && _vitals.CurrentHealth == 0)
        {
            CancelPendingRecall("dead");
            return;
        }
        if (_settings.IsMacroRunning)
        {
            // Someone switched the macro back on: their call, the recall is dropped.
            _recallPending = false;
            _macroStoppedByFailsafe = false;
            _host.Log("[Vitals] HP failsafe: the macro was started again; the recall is dropped.");
            return;
        }
        bool spent = _recallSends >= RecallMaxSends && now >= _recallNextAt;
        bool late = (now - _recallStartedAt).TotalMilliseconds >= RecallGiveUpMs;
        if (spent || late || !_host.HasCastSpell)
        {
            _recallPending = false;
            HpFailsafeRecallsFailed++;
            _fsRecallNotBefore = now.AddMilliseconds(RecallCooldownMs);
            string why = !_host.HasCastSpell ? "this engine can't cast, so no recall"
                : _recallSends == 0 ? $"Lifestone Recall couldn't be cast in {RecallGiveUpMs / 1000:0} s ({(_recallWait.Length > 0 ? _recallWait : "no reason seen")})"
                : $"no portal space after {_recallSends} cast(s) of Lifestone Recall (refused or fizzled)";
            string what;
            if (!_macroStoppedByFailsafe) what = "the macro stays as it is";
            else if (MayRestartMacro != null && !MayRestartMacro()) what = "the macro stays off (VTank yield)";
            else
            {
                _settings.IsMacroRunning = true;   // the heals and kits run again; the failsafe re-arms
                what = "macro back on to keep healing";
            }
            _macroStoppedByFailsafe = false;
            string line = $"HP failsafe: {why}: {what}; next recall in {RecallCooldownMs / 1000:0} s at the earliest.";
            _host.Log("[Vitals] " + line);
            _host.WriteToChat("[RynthAi] " + line, 2);
            return;
        }
        if (now < _recallNextAt || _recallSends >= RecallMaxSends) return;
        string wait = RecallCastHolds();
        if (wait.Length > 0)
        {
            if (wait != _recallWait) _host.Log($"[Vitals] HP failsafe: Lifestone Recall waits: {wait}.");
            _recallWait = wait;
            return;
        }
        _recallWait = "";
        uint me = unchecked((uint)_host.GetPlayerId());
        bool sent = me != 0 && _host.CastSpellFor(me, RecallSpellId, "HpFailsafe", "Lifestone Recall");
        _lastCastAttempt = RynthClock.Now;
        if (!sent)
        {
            _recallWait = "the client refused the cast";
            _recallNextAt = now.AddMilliseconds(500);
            return;
        }
        _recallSends++;
        _recallNextAt = now.AddMilliseconds(RecallResendMs);
        _host.Log($"[Vitals] HP failsafe: Lifestone Recall cast ({_recallSends}/{RecallMaxSends}, {(now - _recallStartedAt).TotalMilliseconds:0} ms after the stop).");
    }

    /// <summary>
    /// What holds the recall cast back now ("" = nothing): the Magic stance (the emergency heal's step,
    /// wand swap included) and the pacer at emergency health (in the air after the jump, a server
    /// answer it must wait for). Nothing else of RynthAi runs meanwhile: the macro is off.
    /// </summary>
    private string RecallCastHolds()
    {
        if (!EnsureMagicMode(urgentHeal: true))
            return _magicModeBlocked ? "can't switch to Magic mode" : "wand swap / Magic stance";
        if (!PacerAllows(out string pacerWhy)) return "the server: " + pacerWhy;
        return "";
    }

    /// <summary>
    /// The plugin's death hold (and a 0-health read): a pending recall is dropped and the macro is
    /// left off, so nothing casts the recall again or restarts the macro after the respawn.
    /// </summary>
    /// <summary>
    /// A death or a portal dropped whatever the server had open: a wand use in flight is forgotten,
    /// not judged a failed swap. RynthAiPlugin calls it with the death hold and on entering portal space.
    /// </summary>
    internal void ForgetPendingWield(string why)
    {
        DropWandSwap(why);
        if (_pendingWieldId == 0) return;
        _pendingWieldId = 0;
        _host.Log($"[WieldGate] the wand's wield in flight was cut short ({why}): not counted as a failed swap");
    }

    internal void CancelPendingRecall(string why)
    {
        if (!_recallPending) return;
        _recallPending = false;
        _macroStoppedByFailsafe = false;
        _host.Log($"[Vitals] HP failsafe: the pending recall is dropped ({why}); the macro stays off.");
    }
}
