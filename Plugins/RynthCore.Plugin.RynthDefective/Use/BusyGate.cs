// ============================================================================
//  RynthDefective - Use/BusyGate.cs
//  Whether the client's "busy" readings really hold a use back (2026-10-28).
//
//  Why: the busy count the engine reports (GetBusyState) can stay above zero
//  with nothing at all in flight: after a death (the engine's own resync waits
//  out the death portal and 5 s more), after a logout and a login (the engine
//  keeps its count across the session and can't clear it again), after an
//  interrupted action. Holding every use on it made the bar "not work at all
//  after logging in" until the client was restarted, and kits wait seconds
//  after every death. RynthAi copes the same way (Combat/CombatBusyGate: a
//  leftover count doesn't hold a heal), and RynthAi's "waiting on the server"
//  bit includes its copy of the same count, so it sticks with it.
//
//  The rule (pure: BusyRule.Decide; BusyGate keeps the clocks):
//    * a gesture animating holds, for at most AnimationMaxMs without a break
//      (RynthAi's ActionPacer.GestureHoldMaxMs: no real gesture lasts longer);
//    * the busy count holds while it is above the count already judged a
//      leftover AND it moved within CountTrustMs or our own last use went out
//      within OwnTrustMs. A count that has read the same for longer, with
//      nothing of ours in flight, is a leftover: it holds nothing, and only a
//      count above it holds again (our own next use raises it and the server's
//      answer brings it back down to the leftover);
//    * RynthAi's "waiting on the server" bit holds while it is fresh:
//      BotTrustMs since it came on (RynthAi's own actions are answered or let
//      go well inside it), or only CountTrustMs when a leftover count explains it.
//  After that the use is sent: if the server really is busy it answers
//  "You're too busy" (UseDone 0x1D) and the use controller tries once more.
//
//  With RynthAi holding for the press (RynthAi.Yield, 2026-10-29), RynthAi says
//  exactly what it has in flight (its pacer's one outstanding action), so the
//  rule is simpler and doesn't wait seconds for the count to sit still:
//    * RynthAi's own unanswered action holds, for at most BotTrustMs;
//    * the count holds only right after our own use (OwnTrustMs);
//    * anything else in the count is a leftover (a death, a login, a corpse
//      open's +1) or a click in the game's own windows: the use goes.
//  After a death or a login RynthAi's stream of rebuffs kept the count moving,
//  so it never read "steady" and a press waited until it gave up.
//
//  RynthDefective never resets the client's count itself (ForceResetBusyCount):
//  the count is the engine's (its reconciler and watchdogs own it), RynthAi
//  stopped resetting it on 2026-10-06 because a reset zeroes actions the server
//  hasn't answered yet (each later answer then drives it negative), and nothing
//  here needs it at zero: ignoring a leftover is enough to send.
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthDefective.Use;

/// <summary>What holds a use back now; None = send.</summary>
internal enum BusyHold { None, Busy, Animation, Bot }

/// <summary>The readings that were ignored as leftovers (each logged once).</summary>
[Flags]
internal enum BusyStale { None = 0, Count = 1, Animation = 2, Bot = 4 }

/// <summary>The readings at one moment, and how long each has read so. Pure data.</summary>
/// <param name="Count">The client's busy count (GetBusyState).</param>
/// <param name="CountSteadyMs">How long it has read this value.</param>
/// <param name="Leftover">The count already judged a leftover (0 = none): a reading at or under it holds nothing.</param>
/// <param name="SinceOwnSendMs">Since RynthDefective's own last use or cast went out.</param>
/// <param name="Animating">A cast or use gesture is animating.</param>
/// <param name="AnimatingMs">How long it has animated without a break.</param>
/// <param name="BotWaiting">RynthAi is waiting on the server (null = RynthAi isn't there).</param>
/// <param name="BotSteadyMs">How long that bit has read its current value.</param>
/// <param name="BotPaced">
/// RynthAi holds for the press and reports its own unanswered action (RynthAi.Yield); null = it
/// doesn't (absent, from before, its pacing off): the rules above apply.
/// </param>
/// <param name="BotPacedMs">How long RynthAi's own action has been unanswered (0 when none).</param>
internal readonly record struct BusyFacts(
    int Count, long CountSteadyMs, int Leftover, long SinceOwnSendMs,
    bool Animating, long AnimatingMs, bool? BotWaiting, long BotSteadyMs,
    bool? BotPaced = null, long BotPacedMs = 0);

internal static class BusyRule
{
    /// <summary>A count that moved this recently is an action under way (a kit 2.5 s, a corpse use 3.7 s at the most seen).</summary>
    public const long CountTrustMs = 4000;
    /// <summary>After our own use went out the count is ours this long.</summary>
    public const long OwnTrustMs = 4000;
    /// <summary>A gesture that lasts longer is a stuck motion queue (RynthAi's ActionPacer.GestureHoldMaxMs).</summary>
    public const long AnimationMaxMs = 4000;
    /// <summary>RynthAi's bit this long: its corpse uses are let go at 5 s (ActionPacer.WorldUseMaxMs).</summary>
    public const long BotTrustMs = 5500;

    /// <summary>What holds a use back; <paramref name="stale"/> names the readings ignored as leftovers.</summary>
    public static BusyHold Decide(in BusyFacts f, out BusyStale stale)
    {
        stale = BusyStale.None;
        if (f.Animating)
        {
            if (f.AnimatingMs < AnimationMaxMs) return BusyHold.Animation;
            stale |= BusyStale.Animation;
        }
        if (f.BotPaced is bool paced)
        {
            // RynthAi holds for the press and says what it has in flight: that, and our own last use,
            // are all that hold it. The rest of the count is a leftover or a click in the game's own
            // windows; a really busy server answers "You're too busy" (one more try).
            if (paced)
            {
                if (f.BotPacedMs < BotTrustMs) return BusyHold.Bot;
                stale |= BusyStale.Bot;
            }
            if (f.Count > 0)
            {
                if (f.Count > f.Leftover && f.SinceOwnSendMs < OwnTrustMs) return BusyHold.Busy;
                stale |= BusyStale.Count;
            }
            return BusyHold.None;
        }
        if (f.Count > 0)
        {
            bool fresh = f.CountSteadyMs < CountTrustMs || f.SinceOwnSendMs < OwnTrustMs;
            if (f.Count > f.Leftover && fresh) return BusyHold.Busy;
            stale |= BusyStale.Count;
        }
        if (f.BotWaiting == true)
        {
            // RynthAi's bit includes its copy of the same count: a leftover count explains it.
            long trust = (stale & BusyStale.Count) != 0 ? CountTrustMs : BotTrustMs;
            if (f.BotSteadyMs < trust) return BusyHold.Bot;
            stale |= BusyStale.Bot;
        }
        return BusyHold.None;
    }
}

/// <summary>
/// The clocks behind <see cref="BusyRule"/>: how long each reading has read the same, our own last
/// send, and the leftover count. <see cref="Observe"/> every tick (in or out of the world: a count
/// left by a death has read the same all through the death portal), <see cref="Judge"/> when a use
/// wants to go. Plugin pump thread only.
/// </summary>
internal sealed class BusyGate
{
    private bool _started;
    private int _count, _leftover;
    private long _countSinceMs;
    private bool _animating;
    private long _animatingSinceMs;
    private bool? _bot;
    private long _botSinceMs;
    private bool _paced;
    private long _pacedSinceMs;
    private long _ownSendMs = long.MinValue / 4;
    private BusyStale _logged;

    /// <summary>The count judged a leftover (0 = none).</summary>
    public int Leftover => _leftover;

    public void Observe(int count, bool animating, bool? botWaiting, long now) => Observe(count, animating, botWaiting, null, now);

    /// <param name="botPaced">RynthAi's own action is unanswered (null = it can't say).</param>
    public void Observe(int count, bool animating, bool? botWaiting, bool? botPaced, long now)
    {
        bool paced = botPaced == true;
        if (paced && !_paced) _pacedSinceMs = now;
        _paced = paced;
        if (count < 0) count = 0;
        if (!_started)
        {
            _started = true;
            _count = count;
            _countSinceMs = now;
            _animating = animating;
            _animatingSinceMs = now;
            _bot = botWaiting;
            _botSinceMs = now;
        }
        if (count != _count)
        {
            _count = count;
            _countSinceMs = now;
        }
        if (_leftover > count) _leftover = count;   // part of it was answered or cleared: what is left is the leftover
        if (count == 0) _logged &= ~BusyStale.Count;
        if (animating != _animating)
        {
            _animating = animating;
            _animatingSinceMs = now;
            if (!animating) _logged &= ~BusyStale.Animation;
        }
        if (botWaiting != _bot)
        {
            _bot = botWaiting;
            _botSinceMs = now;
            if (botWaiting != true) _logged &= ~BusyStale.Bot;
        }
    }

    /// <summary>Our own use or cast went out now.</summary>
    public void NoteOwnSend(long now) => _ownSendMs = now;

    public BusyFacts Facts(long now) => Facts(now, false);

    /// <param name="pacedKnown">RynthAi holds for the press (its PacedAction bit is meaningful).</param>
    public BusyFacts Facts(long now, bool pacedKnown) => new(
        _count, now - _countSinceMs, _leftover, now - _ownSendMs,
        _animating, _animating ? now - _animatingSinceMs : 0, _bot, now - _botSinceMs,
        pacedKnown ? _paced : null, pacedKnown && _paced ? now - _pacedSinceMs : 0);

    /// <summary>
    /// What holds a use back now. A count found to be a leftover becomes the new leftover;
    /// <paramref name="newlyIgnored"/> names the leftovers not reported before (log them once).
    /// </summary>
    public BusyHold Judge(long now, out BusyStale newlyIgnored) => Judge(now, false, out newlyIgnored);

    /// <param name="pacedKnown">RynthAi holds for the press (RynthAi.Yield): its own action is what it says.</param>
    public BusyHold Judge(long now, bool pacedKnown, out BusyStale newlyIgnored)
    {
        BusyHold hold = BusyRule.Decide(Facts(now, pacedKnown), out BusyStale stale);
        if ((stale & BusyStale.Count) != 0 && _count > _leftover)
        {
            _leftover = _count;
            _logged &= ~BusyStale.Count;   // a new leftover is worth its own line
        }
        newlyIgnored = stale & ~_logged;
        _logged |= stale;
        return hold;
    }

    /// <summary>The words for the log line when a leftover is first ignored.</summary>
    public string Describe(BusyStale what, long now) => Describe(what, now, false);

    public string Describe(BusyStale what, long now, bool pacedKnown)
    {
        BusyFacts f = Facts(now, pacedKnown);
        var parts = new System.Collections.Generic.List<string>(3);
        if ((what & BusyStale.Count) != 0)
            parts.Add(f.BotPaced != null
                ? $"the client's busy count reads {f.Count} with nothing of ours or RynthAi's in flight"
                : $"the client's busy count has read {f.Count} for {f.CountSteadyMs / 1000.0:0.0} s with nothing of ours in flight");
        if ((what & BusyStale.Animation) != 0)
            parts.Add($"the character has read 'animating' for {f.AnimatingMs / 1000.0:0.0} s");
        if ((what & BusyStale.Bot) != 0 && f.BotPaced != null)
            parts.Add($"RynthAi's own action has had no answer for {f.BotPacedMs / 1000.0:0.0} s");
        else if ((what & BusyStale.Bot) != 0)
            parts.Add($"RynthAi has read 'waiting on the server' for {f.BotSteadyMs / 1000.0:0.0} s"
                      + ((what & BusyStale.Count) != 0 || (_logged & BusyStale.Count) != 0 ? " (its copy of the same count)" : ""));
        return "[RynthDefective] " + string.Join("; ", parts)
               + ": a leftover (a death, a login or an interrupted action), not waited on. The count itself is left alone.";
    }

    public void Reset()
    {
        _started = false;
        _leftover = 0;
        _paced = false;
        _logged = BusyStale.None;
        _ownSendMs = long.MinValue / 4;
    }
}
