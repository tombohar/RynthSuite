// ============================================================================
//  RynthSuite - Plugins/RynthCore.Plugin.RynthAi/Combat/BuffCastChain.cs
//  Buff cast chaining: the next buff of a pass goes out on the last buff's UseDone,
//  without waiting for the client to finish playing the cast's recoil.
//
//  Why: in a buff pass every buff waited on the animation gate (ActionPacer.GestureHolds
//  and CastGateWatchdog.CanCastNow, both the client's CMotionInterp queue) after the
//  server's UseDone for the previous buff: "[Pace] Buff waits: the character is still
//  animating" after each UseDone, and the next buff ~550 ms later (UseDone to next buff:
//  median 553 ms over 74 buffs, bimodal at ~50 and ~550 ms). On ACE the server clears
//  IsBusy just before it sends that UseDone (Player_Magic.cs: IsBusy = false;
//  SendUseDoneEvent()), and VerifyBusy only checks IsBusy, so it takes the next cast at
//  once; the client is still showing the recoil the server already finished.
//
//  The fast path (LegacyUiSettings.FastBuffChain, default on) opens ONLY the animation
//  gate, and only when all of these hold:
//    * the last action RynthAi sent was one of BuffManager's own buff casts
//      (ActionPacer.Started and AttacksSent unchanged since it went out);
//    * the very next UseDone after it is the last one (no other UseDone since) and its
//      error is 0;
//    * that UseDone was seen less than WindowMs ago;
//    * no movement call reached the client since the buff went out (MoveAudit.MovementSends
//      unchanged): a stop, a start or a turn starts a motion the client still has to play, so
//      the next buff waits on the animation gate for it (a buff went out 50 ms after a nav stop);
//    * no refusal has turned the fast path off for this buff pass.
//  Every other gate stays: the pacer's outstanding-action gate, the pending cast, the
//  server-resolution wait, the busy count, the cast interval, the stance (EnsureMagicMode),
//  the mana floor, the too-busy backoff. BuffManager also takes the fast path only while
//  no vital is under any of its lines (the mana reserve included), and drops it for a buff
//  that would need Stamina to Mana first, so no kit, potion or vital cast rides it.
//
//  Refusals (the crash this protects against: a tight retry loop on "You're too busy!"
//  re-enters AC's AddTextToScroll -> AV 0x00460D1D, see BuffManager.OnChatWindowText and
//  CastGateWatchdog): the throttle, the park and the escalating backoff are untouched, and
//  any refusal of a buff cast that went out on the fast path (the client's CastSpell
//  returning false, a "too busy" line for it, or an error in its UseDone) turns the fast
//  path off for the rest of the pass, logged. Every refusal during buffing is counted for
//  as a running total since load, logged one line each, and each pass ends with a summary line.
//
//  Vital casts in the between-fights top-off (2026-11-20): Heal Self, Revitalize and Stamina to
//  Mana after one another waited on the same animation gate after each UseDone (200-750 ms in the
//  logs: Revitalize UseDone 20:52:40.784, the next one sent 40.996; Executor's Blessing UseDone
//  11:55:01.293, Stamina to Mana sent 02.049), on top of the server's 2.2 s cast and 1.0 s recoil.
//  The same ACE rule holds for them (one Player_Magic path). SentVital records a vital cast;
//  EligibleVital lets the NEXT vital cast skip the animation gate when the last action was our
//  own vital cast answered UseDone(0), with every condition above. BuffManager asks only with no
//  monster within Monster Range (in a fight nothing changes), in Magic stance, and lets only a
//  vital cast ride it (no kit, potion, stance change or buff). A refusal of a fast vital cast
//  turns the vital fast path off for VitalOffMs. A buff never chains on a vital's UseDone, and a
//  vital never on a buff's (Eligible / EligibleVital check which kind went last).
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthAi;

internal sealed class BuffCastChain
{
    /// <summary>The fast path is only taken this long after the last buff's UseDone was seen.</summary>
    public const double WindowMs = 1500;

    // The last buff cast BuffManager sent.
    private int _spellId;
    private string _name = "";
    private int _seqAtSend;
    private long _startedAfter;
    private long _attacksAfter;
    private long _movesAfter;
    private bool _moveCounted;    // MovementWaits counted for this send
    private bool _wasFast;
    private bool _wasVital;       // the last cast recorded was a vital (SentVital), not a buff
    private DateTime _vitalOffUntil = DateTime.MinValue;   // a refused fast vital cast: no vital fast path until then
    private bool _heldForAnimation;   // the next buff goes through the animation gate (HoldForAnimation)
    private bool _answerSeen;     // the first UseDone after it was read
    private bool _answerOk;       // ... and it was UseDone(0), the next UseDone after the send
    private DateTime _answerSeenAt = DateTime.MinValue;

    // This pass.
    private bool _inPass;
    private bool _offThisPass;
    private int _passCasts, _passFast, _passClient, _passServer, _passTooBusy;

    // Running totals since load.
    public int ClientRefusals { get; private set; }
    public int ServerRefusals { get; private set; }
    public int TooBusyLines { get; private set; }
    public int FastCasts { get; private set; }
    public int BuffCasts { get; private set; }
    public int PassesSwitchedOff { get; private set; }
    /// <summary>Times the fast path was refused because a movement call went out after the last buff.</summary>
    public int MovementWaits { get; private set; }
    /// <summary>Vital casts recorded (SentVital), and those that went on the vital fast path.</summary>
    public int VitalCasts { get; private set; }
    public int VitalFastCasts { get; private set; }
    /// <summary>Times a refusal turned the vital fast path off for VitalOffMs.</summary>
    public int VitalSwitchOffs { get; private set; }
    /// <summary>How long a refused fast vital cast keeps the vital fast path off.</summary>
    public const double VitalOffMs = 60_000;

    /// <summary>True when a refusal turned the fast path off for the rest of this pass.</summary>
    public bool OffThisPass => _offThisPass;
    public bool InPass => _inPass;

    public void PassStart()
    {
        if (_inPass) return;
        _inPass = true;
        _offThisPass = false;
        _passCasts = _passFast = _passClient = _passServer = _passTooBusy = 0;
    }

    /// <summary>The pass is over (complete or cancelled): one summary line.</summary>
    public void PassEnd(string how, Action<string> log)
    {
        if (!_inPass) return;
        _inPass = false;
        log($"[BuffChain] buff pass {how}: {_passCasts} buff cast(s), {_passFast} on the fast path" +
            $"{(_offThisPass ? " (fast path switched off by a refusal)" : "")}; refusals this pass: client {_passClient}, " +
            $"server {_passServer}, too-busy lines {_passTooBusy}; totals: client {ClientRefusals}, server {ServerRefusals}, " +
            $"too-busy lines {TooBusyLines}, {FastCasts} of {BuffCasts} buff casts on the fast path");
        _offThisPass = false;
    }

    /// <summary>A buff cast went out. <paramref name="seqAtSend"/> is the UseDone count read before the send;
    /// <paramref name="started"/> / <paramref name="attacks"/> are ActionPacer's counts and <paramref name="moves"/>
    /// MoveAudit.MovementSends, all read after it.</summary>
    public void Sent(int spellId, string name, int seqAtSend, long started, long attacks, long moves, bool fast)
    {
        _spellId = spellId;
        _name = name;
        _seqAtSend = seqAtSend;
        _startedAfter = started;
        _attacksAfter = attacks;
        _movesAfter = moves;
        _moveCounted = false;
        _wasFast = fast;
        _wasVital = false;
        _heldForAnimation = false;
        _answerSeen = false;
        _answerOk = false;
        _answerSeenAt = DateTime.MinValue;
        BuffCasts++;
        _passCasts++;
        if (fast) { FastCasts++; _passFast++; }
    }

    /// <summary>A vital cast went out (Heal Self, Revitalize, Stamina to Mana ... out of a fight): the
    /// same tracking as <see cref="Sent"/>, counted as a vital, not a buff of the pass.</summary>
    public void SentVital(int spellId, string name, int seqAtSend, long started, long attacks, long moves, bool fast)
    {
        _spellId = spellId;
        _name = name;
        _seqAtSend = seqAtSend;
        _startedAfter = started;
        _attacksAfter = attacks;
        _movesAfter = moves;
        _moveCounted = false;
        _wasFast = fast;
        _wasVital = true;
        _heldForAnimation = false;
        _answerSeen = false;
        _answerOk = false;
        _answerSeenAt = DateTime.MinValue;
        VitalCasts++;
        if (fast) VitalFastCasts++;
    }

    /// <summary>The client refused a vital cast (nothing went out). On the vital fast path: off for VitalOffMs.</summary>
    public void VitalClientRefused(int spellId, string name, bool fast, DateTime now, Action<string> log)
    {
        ClientRefusals++;
        log($"[BuffChain] refusal (client): CastSpell refused vital '{name}' (id {spellId}){(fast ? " on the fast path" : "")}" +
            $" - totals client {ClientRefusals}, server {ServerRefusals}, too-busy lines {TooBusyLines}");
        if (fast) VitalOff("the client refused a fast-path vital cast", now, log);
        _spellId = 0;
    }

    /// <summary>A "You're too busy!" line while <paramref name="pendingSpellId"/> (a vital of ours) waited for its result.</summary>
    public void TooBusyLineVital(int pendingSpellId, DateTime now, Action<string> log)
    {
        if (pendingSpellId == 0 || pendingSpellId != _spellId || !_wasVital) return;
        TooBusyLines++;
        log($"[BuffChain] refusal (too-busy line) while vital '{_name}' was pending{(_wasFast ? " (sent on the fast path)" : "")}" +
            $" - totals client {ClientRefusals}, server {ServerRefusals}, too-busy lines {TooBusyLines}");
        if (_wasFast) VitalOff("a fast-path vital cast drew 'You're too busy!'", now, log);
    }

    private void VitalOff(string why, DateTime now, Action<string> log)
    {
        _vitalOffUntil = now.AddMilliseconds(VitalOffMs);
        VitalSwitchOffs++;
        log($"[BuffChain] {why}: vital casts wait for the animation again for {VitalOffMs / 1000:0} s");
    }

    /// <summary>The client refused a buff cast (CastSpell returned false: nothing went out).</summary>
    public void ClientRefused(int spellId, string name, bool fast, Action<string> log)
    {
        ClientRefusals++;
        _passClient++;
        log($"[BuffChain] refusal (client): CastSpell refused '{name}' (id {spellId}){(fast ? " on the fast path" : "")}" +
            $" - totals client {ClientRefusals}, server {ServerRefusals}, too-busy lines {TooBusyLines}");
        if (fast) SwitchOff("the client refused a fast-path cast", log);
        _spellId = 0;   // nothing went out: nothing to chain on
    }

    /// <summary>
    /// Each tick: reads the first UseDone after the last buff cast. An error in it is a server refusal
    /// (counted, logged; on the fast path the fast path is off for the pass).
    /// </summary>
    public void Observe(bool hasLast, int lastSeq, uint lastErr, int seqNow, DateTime now, Action<string> log)
    {
        if (_spellId == 0 || _answerSeen || !hasLast) return;
        if (lastSeq <= _seqAtSend) return;   // not answered yet
        _answerSeen = true;
        _answerSeenAt = now;
        bool next = lastSeq == _seqAtSend + 1 && seqNow == lastSeq;
        _answerOk = next && lastErr == 0;
        if (next && lastErr != 0)
        {
            ServerRefusals++;
            if (!_wasVital) _passServer++;
            log($"[BuffChain] refusal (server): UseDone error 0x{lastErr:X} for {(_wasVital ? "vital " : "")}'{_name}'{(_wasFast ? " on the fast path" : "")}" +
                $" - totals client {ClientRefusals}, server {ServerRefusals}, too-busy lines {TooBusyLines}");
            if (_wasFast)
            {
                if (_wasVital) VitalOff($"the server refused a fast-path vital cast (0x{lastErr:X})", now, log);
                else SwitchOff($"the server refused a fast-path cast (0x{lastErr:X})", log);
            }
        }
    }

    /// <summary>A "You're too busy!" line while <paramref name="pendingSpellId"/> (a buff of ours) waited for its result.</summary>
    public void TooBusyLine(int pendingSpellId, Action<string> log)
    {
        if (!_inPass || pendingSpellId == 0) return;
        TooBusyLines++;
        _passTooBusy++;
        bool ours = pendingSpellId == _spellId;
        log($"[BuffChain] refusal (too-busy line) while '{(ours ? _name : $"spell {pendingSpellId}")}' was pending" +
            $"{(ours && _wasFast ? " (sent on the fast path)" : "")} - totals client {ClientRefusals}, server {ServerRefusals}, too-busy lines {TooBusyLines}");
        if (ours && _wasFast) SwitchOff("a fast-path cast drew 'You're too busy!'", log);
    }

    /// <summary>The next buff needs another cast first (Stamina to Mana for the mana reserve): no fast
    /// path until the next buff cast goes out.</summary>
    public void HoldForAnimation() => _heldForAnimation = true;

    private void SwitchOff(string why, Action<string> log)
    {
        if (_offThisPass) return;
        _offThisPass = true;
        PassesSwitchedOff++;
        log($"[BuffChain] {why}: the next buffs of this pass wait for the animation again");
    }

    /// <summary>
    /// May the next buff skip the animation gate? <paramref name="why"/> says why it may (for the log).
    /// The caller has checked the setting, the pacing and the vitals.
    /// </summary>
    public bool Eligible(long started, long attacks, long moves, bool hasLast, int lastSeq, uint lastErr, int seqNow, DateTime now, out string why)
    {
        why = "";
        if (_wasVital) return false;   // a buff never chains on a vital's UseDone (unchanged buff behaviour)
        return EligibleCore(started, attacks, moves, hasLast, lastSeq, lastErr, seqNow, now, out why);
    }

    /// <summary>
    /// May the next VITAL cast skip the animation gate? Only right after our own vital cast's UseDone(0),
    /// with every condition of <see cref="Eligible"/>, and not within VitalOffMs of a refused fast vital.
    /// The caller has checked the setting, the pacing, the stance and that no monster is within range.
    /// </summary>
    public bool EligibleVital(long started, long attacks, long moves, bool hasLast, int lastSeq, uint lastErr, int seqNow, DateTime now, out string why)
    {
        why = "";
        if (!_wasVital || now < _vitalOffUntil) return false;
        return EligibleCore(started, attacks, moves, hasLast, lastSeq, lastErr, seqNow, now, out why);
    }

    private bool EligibleCore(long started, long attacks, long moves, bool hasLast, int lastSeq, uint lastErr, int seqNow, DateTime now, out string why)
    {
        why = "";
        if (_offThisPass || _heldForAnimation || _spellId == 0 || !_answerSeen || !_answerOk) return false;
        if (started != _startedAfter || attacks != _attacksAfter) return false;   // something else went out since
        if (!hasLast || lastSeq != _seqAtSend + 1 || seqNow != lastSeq || lastErr != 0) return false;   // another UseDone since
        double age = (now - _answerSeenAt).TotalMilliseconds;
        if (age > WindowMs) return false;
        if (moves != _movesAfter)   // a stop, start or turn went out since: its motion must play first
        {
            if (!_moveCounted) { _moveCounted = true; MovementWaits++; }
            return false;
        }
        why = $"'{_name}' answered UseDone(0) {age:0} ms ago";
        return true;
    }

    public string Describe() =>
        $"Buff chain: {FastCasts} of {BuffCasts} buff casts on the fast path; refusals client {ClientRefusals}, " +
        $"server {ServerRefusals}, too-busy lines {TooBusyLines}; passes switched off {PassesSwitchedOff}; movement waits {MovementWaits}; " +
        $"vital casts {VitalFastCasts} of {VitalCasts} on the fast path, vital switch-offs {VitalSwitchOffs}";
}
