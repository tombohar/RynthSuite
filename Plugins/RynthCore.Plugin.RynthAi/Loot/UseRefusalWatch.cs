// ============================================================================
//  RynthAi - Loot/UseRefusalWatch.cs
//  Notices when the server answers every corpse open but nothing opens.
//
//  Why (2026-10-06, Drakkon on DreamWeave): from 06:18:44 every use of a corpse
//  or door got the server's "use done" (UseDone, no error) about one round trip
//  after it was sent, and nothing opened. Fighting, healing kits and spells kept
//  working. Macro off, walking, jumping and /rc unlockactions changed nothing; a
//  relog fixed it at once, for the very same corpses. The bot hammered corpses
//  for 13 minutes: 33 abandons, 11 "LOOT WEDGE" resets, a chat line each.
//
//  What this does: each corpse open is remembered with the server's UseDone
//  count at the time. A UseDone that arrives after it with no error, with no
//  corpse opening within AnswerGraceMs, is a refusal. A successful open of any
//  corpse clears everything.
//    - RefusalsBeforeRetry refusals in a row: stop all movement once, pause a
//      moment, try once more (CleanStopAndRetry).
//    - The retry refused too, with refusals on two different corpses since the
//      last open: the server refuses every use. Say so once and back off
//      (Stuck), then try one corpse every BackoffMs (doubling, capped). Each
//      probe that is refused again is StillStuck (log only); one that opens is
//      Recovered.
//    - The retry refused but only one corpse has ever refused: that corpse can't
//      be opened (its loot rights, say). Give up on it (DropCorpse); the next
//      corpse's first refusal decides.
//
//  A UseDone with an error (0x1D "too busy", ...) says nothing either way, and
//  an open the server never answers is left to the corpse timeout as before.
//  No game calls here: the controller acts on the verdicts.
//
//  The jump (2026-10-06 afternoon): the stuck state is the server's animation
//  bookkeeping for the character (ACE: MotionInterp.PendingMotions/IsAnimating
//  and the MotionTableManager queue never drain, so the server's turn to the
//  corpse never starts and the use's callback never runs). Besides leaving the
//  world, the only thing on ACE that empties those queues is the player's jump
//  (Player.HandleActionJump: RemoveLinkAnimations drains the motion table queue,
//  PendingMotions.Clear, IsAnimating = false; the jump also cancels the pending
//  walk/turn). So the controller jumps at the clean stop, and once more before
//  each probe after a back-off (TakeProbeJump).
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Loot;

internal sealed class UseRefusalWatch
{
    /// <summary>After the server's answer, a corpse that opens does so within a few frames.</summary>
    public const long AnswerGraceMs = 1500;
    /// <summary>An open the server hasn't answered after this long is forgotten (the corpse timeout handles it).</summary>
    public const long UnansweredForgetMs = 15_000;
    public const int RefusalsBeforeRetry = 3;
    /// <summary>The pause after the clean stop, before the one retry.</summary>
    public const long CleanStopPauseMs = 1500;
    public const long FirstBackoffMs = 5 * 60_000;
    public const long MaxBackoffMs = 20 * 60_000;

    public enum Phase { Normal, Retrying, Stuck }

    public enum Verdict
    {
        None,
        /// <summary>Several refusals in a row: stop all movement, pause CleanStopPauseMs, try once more.</summary>
        CleanStopAndRetry,
        /// <summary>The retry was refused but only this corpse ever refused: give up on it.</summary>
        DropCorpse,
        /// <summary>The server refuses every use: tell the player once, back off until BackoffUntil.</summary>
        Stuck,
        /// <summary>A probe after the back-off was refused too: back off again (longer), quietly.</summary>
        StillStuck,
        /// <summary>A corpse opened after Stuck: uses work again.</summary>
        Recovered,
    }

    public Phase State { get; private set; } = Phase.Normal;
    public int Refusals { get; private set; }
    public long BackoffUntil { get; private set; }
    public long BackoffMs { get; private set; }
    /// <summary>The corpses refused since the last successful open.</summary>
    public IReadOnlyCollection<int> RefusedCorpses => _refused;
    /// <summary>The corpse of the last refusal (what a verdict is about).</summary>
    public int LastRefusedCorpse { get; private set; }

    private readonly HashSet<int> _refused = new();
    private bool _probeJumpTaken;
    private bool _pending;
    private int _pendingId;
    private long _sentAt;
    private int _seqAtSend;
    private long _answeredAt;

    /// <summary>While Stuck: no opens until the back-off ends (then one probe).</summary>
    public bool HoldsOpens(long now) => State == Phase.Stuck && now < BackoffUntil;

    /// <summary>The back-off is over and the probe is next: true once per back-off, meaning
    /// "jump first, then send the probe after the pause".</summary>
    public bool TakeProbeJump(long now)
    {
        if (State != Phase.Stuck || now < BackoffUntil || _probeJumpTaken)
            return false;
        _probeJumpTaken = true;
        return true;
    }

    /// <summary>Call before sending the next open: the previous one, answered with nothing opened,
    /// counts as refused now (the next attempt came before its grace ran out). A verdict other than
    /// None means: act on it and don't send this open.</summary>
    public Verdict SettleBeforeOpen(long now)
    {
        if (!_pending || _answeredAt == 0)
            return Verdict.None;
        _pending = false;
        return Refused(_pendingId, now);
    }

    /// <summary>An open of <paramref name="corpseId"/> went out; <paramref name="useDoneSeq"/> is the
    /// server UseDone count read just before it was sent.</summary>
    public void OnOpenSent(int corpseId, long now, int useDoneSeq)
    {
        _pending = true;
        _pendingId = corpseId;
        _sentAt = now;
        _seqAtSend = useDoneSeq;
        _answeredAt = 0;
    }

    /// <summary>Every tick: <paramref name="useDoneSeq"/> is the server UseDone count now,
    /// <paramref name="lastError"/> the error of the latest one when the engine can say
    /// (<paramref name="haveLastError"/>).</summary>
    public Verdict OnTick(long now, int useDoneSeq, uint lastError, bool haveLastError)
    {
        if (!_pending)
            return Verdict.None;

        if (_answeredAt == 0)
        {
            if (useDoneSeq != _seqAtSend)
            {
                if (haveLastError && lastError != 0)
                {
                    _pending = false;      // "too busy" or another error: says nothing about refusals
                    return Verdict.None;
                }
                _answeredAt = now;
            }
            else if (now - _sentAt >= UnansweredForgetMs)
            {
                _pending = false;
            }
            return Verdict.None;
        }

        if (now - _answeredAt < AnswerGraceMs)
            return Verdict.None;

        _pending = false;
        return Refused(_pendingId, now);
    }

    /// <summary>A corpse opened (the server sent its contents): uses work.</summary>
    public Verdict OnOpened(long now)
    {
        bool wasStuck = State == Phase.Stuck;
        Reset();
        return wasStuck ? Verdict.Recovered : Verdict.None;
    }

    /// <summary>Back to normal: a relog, a login, a successful open.</summary>
    public void Reset()
    {
        State = Phase.Normal;
        Refusals = 0;
        BackoffUntil = 0;
        BackoffMs = 0;
        LastRefusedCorpse = 0;
        _refused.Clear();
        _probeJumpTaken = false;
        _pending = false;
        _answeredAt = 0;
    }

    private Verdict Refused(int corpseId, long now)
    {
        _refused.Add(corpseId);
        LastRefusedCorpse = corpseId;
        Refusals++;

        switch (State)
        {
            case Phase.Normal:
                if (Refusals < RefusalsBeforeRetry)
                    return Verdict.None;
                State = Phase.Retrying;
                return Verdict.CleanStopAndRetry;

            case Phase.Retrying:
                if (_refused.Count < 2)
                    return Verdict.DropCorpse;   // stay Retrying: the next corpse's refusal decides
                State = Phase.Stuck;
                BackoffMs = FirstBackoffMs;
                BackoffUntil = now + BackoffMs;
                _probeJumpTaken = false;
                return Verdict.Stuck;

            default: // Stuck: a probe after the back-off
                BackoffMs = Math.Min(MaxBackoffMs, Math.Max(FirstBackoffMs, BackoffMs * 2));
                BackoffUntil = now + BackoffMs;
                _probeJumpTaken = false;
                return Verdict.StillStuck;
        }
    }
}
