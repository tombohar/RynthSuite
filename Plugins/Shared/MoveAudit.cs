// ============================================================================
//  RynthSuite - Plugins/Shared/MoveAudit.cs
//  Movement calls with a caller tag: host.SetAutoRunBy("Nav", true) and friends.
//  Linked into the plugins that move the character, like UseAudit.cs.
//
//  Why (2026-10-05, Drakkon's stuck casts on DreamWeave): a movement packet sent
//  while a cast winds up can cancel the cast, or leave the server holding it open.
//  The engine logged only one movement call in eight and never said who sent it,
//  so "which subsystem sent SetAutoRun during Buffing" could not be answered.
//  Every call is now one log line:
//
//    [Move] RynthAi/Nav SetAutoRun(False)
//
//  Identical calls from the same subsystem in a row (the nav servo turns every
//  tick; any angle counts as identical for turns) are counted instead of written
//  one by one, and the count goes out with the next line that is written (the
//  next different call, or the next repeat after RepeatWindowMs):
//
//    [Move] RynthAi/Arbiter StopCompletely() (+14 more TurnToHeading, last 230.5 before this, over 480 ms)
//
//  The engine logs the same calls (with the plugin's name) behind its own move
//  trace switch; this line adds the subsystem, which only the plugin knows.
// ============================================================================

using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal static class MoveAudit
{
    /// <summary>Off = the calls go through without a line. On by default.</summary>
    public static volatile bool Enabled = true;

    /// <summary>The plugin's name in every line (UseAudit.PluginName is set at Init too).</summary>
    public static string PluginName => UseAudit.PluginName;

    /// <summary>Clock (ms); tests replace it.</summary>
    public static Func<long> NowMs = () => Environment.TickCount64;

    // Identical calls closer than this to the last WRITTEN line are only counted.
    private const long RepeatWindowMs = 1000;

    private static readonly object Gate = new();
    private static string _lastKey = "";
    private static long _lastWrittenMs = long.MinValue;
    private static int _repeats;

    // Every movement call that reached the client (a start, a stop, a turn, a held stop sent later).
    private static long _movementSends;

    /// <summary>Movement calls that reached the client so far: a start, a stop, a turn, or a held stop
    /// sent by FlushPendingStops. Only ever grows (Reset aside); a reader compares two values to know
    /// whether anything moved in between (BuffCastChain: no fast-path buff right after a movement change).</summary>
    internal static long MovementSends { get { lock (Gate) return _movementSends; } }
    private static string _repeatLastArg = "";
    private static long _repeatFirstMs;

    public static bool SetAutoRunBy(this RynthCoreHost host, string who, bool on)
    {
        if (on && HeldForUse(host, who, "SetAutoRun(True)")) return false;
        if (HeldForAction(host, who, on ? "SetAutoRun(True)" : "SetAutoRun(False)", isStop: !on, deferAs: on ? null : PendingStop.AutoRunOff())) return false;
        if (on) FlushPendingStops(host, "before a start");
        Note(host, who, "SetAutoRun", on ? "True" : "False", collapseByKindOnly: false);
        lock (Gate) { _autoRunOn = on; if (on) _lastStartMs = NowMs(); else _pendingAutoRunOff = null; _movementSends++; }
        Changed(on ? "SetAutoRun(True)" : "SetAutoRun(False)");
        return host.SetAutoRun(on);
    }

    public static bool StopCompletelyBy(this RynthCoreHost host, string who)
    {
        if (HeldForAction(host, who, "StopCompletely()", isStop: true, deferAs: PendingStop.Complete())) return false;
        Note(host, who, "StopCompletely", "", collapseByKindOnly: false);
        lock (Gate) { _autoRunOn = false; _motionsOn.Clear(); ClearPendingLocked(); _movementSends++; }   // clears everything AC has on
        Changed("StopCompletely()");
        return host.StopCompletely();
    }

    public static bool SetMotionBy(this RynthCoreHost host, string who, uint motion, bool on)
    {
        if (on && HeldForUse(host, who, $"SetMotion(0x{motion:X8}, True)")) return false;
        if (HeldForAction(host, who, $"SetMotion(0x{motion:X8}, {(on ? "True" : "False")})", isStop: !on, motion, on ? null : PendingStop.MotionOff(motion))) return false;
        if (on) FlushPendingStops(host, "before a start");
        Note(host, who, "SetMotion", $"0x{motion:X8}, {(on ? "True" : "False")}", collapseByKindOnly: false);
        lock (Gate) { if (on) { _motionsOn.Add(motion); _lastStartMs = NowMs(); } else { _motionsOn.Remove(motion); _pendingMotionOff.Remove(motion); } _movementSends++; }
        Changed($"SetMotion(0x{motion:X8}, {(on ? "True" : "False")})");
        return host.SetMotion(motion, on);
    }

    public static bool DoMovementBy(this RynthCoreHost host, string who, uint motion, float speed, int holdKey)
    {
        if (HeldForUse(host, who, $"DoMovement(0x{motion:X8})")) return false;
        if (HeldForAction(host, who, $"DoMovement(0x{motion:X8})", isStop: false)) return false;
        FlushPendingStops(host, "before a start");
        Note(host, who, "DoMovement", $"0x{motion:X8}, {speed:0.##}, {holdKey}", collapseByKindOnly: false);
        lock (Gate) { _motionsOn.Add(motion); _lastStartMs = NowMs(); _movementSends++; }
        Changed($"DoMovement(0x{motion:X8})");
        return host.DoMovement(motion, speed, holdKey);
    }

    // -- Hold while any paced server action is unanswered (2026-10-06, ActionPacer) ----------
    // A use, cast or stance change the server is still animating is what a movement packet
    // disturbs: on ACE every client MoveToState is applied to the server's physics, a mode change
    // or a stop cancels the walk/turn to a used object, and a cast's end waits for its gesture.
    // While ActionPacer has one outstanding:
    //   * starting to move or turning is held (returns false, one log line per action and caller);
    //   * a stop goes through at once when RynthAi itself turned that movement on (autorun, or the
    //     motion it turned on) and has not turned it off since - however long ago (2026-10-12: the
    //     3 s "fresh" window used to apply to stops too, so a nav stop more than 3 s after the
    //     last autorun-on that got through was taken as redundant while AC's autorun was still on);
    //   * any other stop waits, but is never dropped (2026-10-12): it is kept as a pending stop and
    //     sent as soon as nothing is outstanding (FlushPendingStops, every RynthAi tick, before the
    //     next start, and always before the next cast - CastSpellFor). RynthAi can't see AC's own
    //     motion state, and a stop left out leaves AC's autorun/motion on for the cast's
    //     FreeHandsAndCastSpell -> MaybeStopCompletely to flush in the cast's frame: the stray
    //     stop burst that came before every run of casts that never landed (flight 63260);
    //   * the deliberate kick for a held cast (Buff/Nudge), a macro stop and typed commands pass.
    // Off unless ActionPacer.Enabled (RynthAi only).

    private static bool _autoRunOn;
    private static long _lastStartMs = long.MinValue / 2;
    /// <summary>Our "moving" state counts only this long after the last start we sent: nav re-sends
    /// autorun every second while it drives, and AC can stop on its own (arrival, a portal) without
    /// RynthAi sending a stop, so an old "on" must not hold casts back forever.</summary>
    internal const long OwnMovementFreshMs = 3000;
    private static readonly System.Collections.Generic.HashSet<uint> _motionsOn = new();
    private static readonly System.Collections.Generic.HashSet<string> _actionHoldLogged = new();
    private static long _actionHoldLoggedFor = long.MinValue;

    /// <summary>Callers whose movement is never held: the cast nudge, a macro stop, typed commands.</summary>
    internal static bool IsExemptFromActionHold(string who) =>
        who == "Buff/Nudge" || who == "MacroStop" || who == "Command";

    /// <summary>True when RynthAi itself has the character moving (autorun or a motion it turned on).</summary>
    internal static bool OwnMovementActive
    {
        get { lock (Gate) return (_autoRunOn || _motionsOn.Count > 0) && NowMs() - _lastStartMs < OwnMovementFreshMs; }
    }

    /// <summary>
    /// True when the call must wait for an unanswered paced action (internal for the tests). A stop
    /// that waits is kept as <paramref name="deferAs"/> and sent later (FlushPendingStops): a stop is
    /// never dropped.
    /// </summary>
    internal static bool HeldForAction(RynthCoreHost host, string who, string call, bool isStop, uint motion = 0, PendingStop? deferAs = null)
    {
        if (!ActionPacer.Enabled || IsExemptFromActionHold(who)) return false;
        if (isStop)
        {
            bool moving;
            lock (Gate) moving = motion != 0 ? _motionsOn.Contains(motion) : (_autoRunOn || _motionsOn.Count > 0);
            if (moving) return false;   // a real stop always goes through, however old the start
        }
        var pending = ActionPacer.Current(host);
        if (pending == null) return false;
        if (!ActionPacer.HoldsMovement(host, out string what)) return false;
        if (isStop && deferAs != null)
            lock (Gate) AddPendingLocked(deferAs, who);
        bool log;
        lock (Gate)
        {
            if (_actionHoldLoggedFor != pending.SentMs) { _actionHoldLoggedFor = pending.SentMs; _actionHoldLogged.Clear(); }
            log = Enabled && _actionHoldLogged.Add(who + (isStop ? "|stop" : "|move"));
        }
        if (log)
        {
            string line = isStop
                ? $"[Move] {PluginName}/{who} {call} waits (nothing of ours is moving; sent once the server has answered, before any cast): the server is still answering {what}"
                : $"[Move] {PluginName}/{who} {call} held (move/turn): the server is still answering {what}";
            try { host.Log(line); }
            catch { }
        }
        return true;
    }

    // ── Pending stops (2026-10-12) ──────────────────────────────────────────────
    // A stop held by HeldForAction waits here until FlushPendingStops sends it. A StopCompletely
    // covers everything else; an autorun-off and each motion-off are kept once (the last caller
    // wins). A stop that goes through for real clears what it covers; a start that goes through
    // sends the pending stops first, so the order the bot asked for is kept.

    internal enum PendingStopKind { AutoRunOff, MotionOff, StopMovement, Complete }

    /// <summary>One stop waiting to be sent.</summary>
    internal sealed class PendingStop
    {
        public PendingStopKind Kind;
        public uint Motion;
        public int HoldKey;
        public static PendingStop AutoRunOff() => new() { Kind = PendingStopKind.AutoRunOff };
        public static PendingStop MotionOff(uint motion) => new() { Kind = PendingStopKind.MotionOff, Motion = motion };
        public static PendingStop StopMovement(uint motion, int holdKey) => new() { Kind = PendingStopKind.StopMovement, Motion = motion, HoldKey = holdKey };
        public static PendingStop Complete() => new() { Kind = PendingStopKind.Complete };
    }

    private static string? _pendingComplete;                                   // who
    private static string? _pendingAutoRunOff;                                 // who
    private static readonly System.Collections.Generic.Dictionary<uint, string> _pendingMotionOff = new();
    private static readonly System.Collections.Generic.Dictionary<uint, (int holdKey, string who)> _pendingStopMovement = new();
    private static long _pendingSinceMs;
    private static long _deferredStopsSent;

    /// <summary>Held stops sent later by FlushPendingStops (status, tests).</summary>
    internal static long DeferredStopsSent { get { lock (Gate) return _deferredStopsSent; } }

    /// <summary>True while a held stop is waiting to be sent.</summary>
    internal static bool HasPendingStop
    {
        get { lock (Gate) return HasPendingLocked(); }
    }

    private static bool HasPendingLocked() =>
        _pendingComplete != null || _pendingAutoRunOff != null || _pendingMotionOff.Count > 0 || _pendingStopMovement.Count > 0;

    private static void AddPendingLocked(PendingStop stop, string who)
    {
        if (!HasPendingLocked()) _pendingSinceMs = NowMs();
        switch (stop.Kind)
        {
            case PendingStopKind.Complete: _pendingComplete = who; break;
            case PendingStopKind.AutoRunOff: _pendingAutoRunOff = who; break;
            case PendingStopKind.MotionOff: _pendingMotionOff[stop.Motion] = who; break;
            case PendingStopKind.StopMovement: _pendingStopMovement[stop.Motion] = (stop.HoldKey, who); break;
        }
    }

    private static void ClearPendingLocked()
    {
        _pendingComplete = null;
        _pendingAutoRunOff = null;
        _pendingMotionOff.Clear();
        _pendingStopMovement.Clear();
    }

    /// <summary>
    /// Sends the held stops once nothing is outstanding (or at once with <paramref name="force"/>:
    /// a cast is about to go out, and no cast goes out with a stop still held). RynthAi calls it
    /// every tick; the starts and CastSpellFor call it first. Returns true when something was sent.
    /// </summary>
    internal static bool FlushPendingStops(RynthCoreHost host, string why, bool force = false)
    {
        lock (Gate) { if (!HasPendingLocked()) return false; }
        if (!force && ActionPacer.Current(host) != null) return false;

        string? complete, autoRunOff;
        System.Collections.Generic.KeyValuePair<uint, string>[] motionOff;
        System.Collections.Generic.KeyValuePair<uint, (int holdKey, string who)>[] stopMovement;
        long waited;
        lock (Gate)
        {
            if (!HasPendingLocked()) return false;
            complete = _pendingComplete;
            autoRunOff = _pendingAutoRunOff;
            motionOff = new System.Collections.Generic.KeyValuePair<uint, string>[_pendingMotionOff.Count];
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<uint, string>>)_pendingMotionOff).CopyTo(motionOff, 0);
            stopMovement = new System.Collections.Generic.KeyValuePair<uint, (int, string)>[_pendingStopMovement.Count];
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<uint, (int, string)>>)_pendingStopMovement).CopyTo(stopMovement, 0);
            waited = NowMs() - _pendingSinceMs;
            ClearPendingLocked();
        }

        var sent = new System.Collections.Generic.List<string>();
        try
        {
            if (complete != null)
            {
                // StopCompletely clears autorun and every motion in AC: nothing else is needed.
                if (host.HasStopCompletely)
                {
                    host.StopCompletely();
                    lock (Gate) { _autoRunOn = false; _motionsOn.Clear(); _deferredStopsSent++; _movementSends++; }
                    sent.Add($"StopCompletely() for {complete}");
                }
                else
                {
                    autoRunOff ??= complete;   // no StopCompletely on this engine: at least autorun off
                }
            }
            if (complete == null || !host.HasStopCompletely)
            {
                if (autoRunOff != null)
                {
                    host.SetAutoRun(false);
                    lock (Gate) { _autoRunOn = false; _deferredStopsSent++; _movementSends++; }
                    sent.Add($"SetAutoRun(False) for {autoRunOff}");
                }
                foreach (var m in motionOff)
                {
                    if (!host.HasSetMotion) break;
                    host.SetMotion(m.Key, false);
                    lock (Gate) { _motionsOn.Remove(m.Key); _deferredStopsSent++; _movementSends++; }
                    sent.Add($"SetMotion(0x{m.Key:X8}, False) for {m.Value}");
                }
                foreach (var s in stopMovement)
                {
                    if (!host.HasStopMovement) break;
                    host.StopMovement(s.Key, s.Value.holdKey);
                    lock (Gate) { _motionsOn.Remove(s.Key); _deferredStopsSent++; _movementSends++; }
                    sent.Add($"StopMovement(0x{s.Key:X8}) for {s.Value.who}");
                }
            }
        }
        catch { }

        // A held stop sent now is a movement change like any other: the settle wait applies to it.
        if (sent.Count > 0) Changed(sent[sent.Count - 1]);

        if (sent.Count > 0 && Enabled)
        {
            try { host.Log($"[Move] {PluginName} held stop(s) sent {why} after {waited} ms: {string.Join(", ", sent)}"); }
            catch { }
        }
        return sent.Count > 0;
    }

    // ── Hold while a door/corpse use is in flight (2026-10-06) ─────────────────────
    // A move or a turn sent while the server is still walking/turning the character to a used
    // door or corpse cancels that use (ACE stops its move-to chain on a client MoveToState).
    // UseAudit knows when one is in flight (opt-in, UseAudit.TrackWorldUses); until the server
    // answers (UseDone) or UseAudit.WorldUseInFlightMaxMs passes, starting to move or turning
    // is skipped and the call returns false. Stops always go through. One line per held use.

    private static uint _heldLoggedFor;
    private static long _heldLoggedAt;

    /// <summary>True when the call must wait for an in-flight door/corpse use (internal for the tests).</summary>
    internal static bool HeldForUse(RynthCoreHost host, string who, string call)
    {
        if (!UseAudit.WorldUseInFlight(host, out uint objectId)) return false;
        bool log;
        lock (Gate)
        {
            long now = NowMs();
            log = Enabled && (objectId != _heldLoggedFor || now - _heldLoggedAt > UseAudit.WorldUseInFlightMaxMs);
            if (log) { _heldLoggedFor = objectId; _heldLoggedAt = now; }
        }
        if (log)
        {
            try { host.Log($"[Move] {PluginName}/{who} {call} held: the server is still answering the use of 0x{objectId:X8} (moving now would cancel it)"); }
            catch { }
        }
        return true;
    }

    public static bool StopMovementBy(this RynthCoreHost host, string who, uint motion, int holdKey)
    {
        if (HeldForAction(host, who, $"StopMovement(0x{motion:X8})", isStop: true, motion, PendingStop.StopMovement(motion, holdKey))) return false;
        Note(host, who, "StopMovement", $"0x{motion:X8}, {holdKey}", collapseByKindOnly: false);
        lock (Gate) { _motionsOn.Remove(motion); _pendingStopMovement.Remove(motion); _movementSends++; }
        Changed($"StopMovement(0x{motion:X8})");
        return host.StopMovement(motion, holdKey);
    }

    /// <summary>A heading snap. Successive turns from one subsystem collapse whatever the angle.</summary>
    public static bool TurnToHeadingBy(this RynthCoreHost host, string who, float headingDegrees)
    {
        if (HeldForUse(host, who, "TurnToHeading")) return false;
        if (HeldForAction(host, who, "TurnToHeading", isStop: false)) return false;
        Note(host, who, "TurnToHeading", headingDegrees.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), collapseByKindOnly: true);
        Changed("TurnToHeading");
        lock (Gate) _movementSends++;
        return host.TurnToHeading(headingDegrees);
    }

    /// <summary>Writes (or counts) one call. Internal for the tests.</summary>
    internal static string? Note(RynthCoreHost host, string who, string call, string arg, bool collapseByKindOnly)
    {
        if (!Enabled) return null;
        string? line = null;
        lock (Gate)
        {
            long now = NowMs();
            string key = collapseByKindOnly ? $"{who}|{call}" : $"{who}|{call}({arg})";
            if (key == _lastKey && now - _lastWrittenMs < RepeatWindowMs)
            {
                if (_repeats == 0) _repeatFirstMs = now;
                _repeats++;
                _repeatLastArg = arg;
                return null;
            }

            string more = _repeats > 0
                ? $" (+{_repeats} more {RepeatName()} before this, over {now - _repeatFirstMs} ms)"
                : "";
            line = $"[Move] {PluginName}/{who} {call}({arg}){more}";
            _lastKey = key;
            _lastWrittenMs = now;
            _repeats = 0;
            _repeatLastArg = "";
        }
        try { host.Log(line); } catch { }
        return line;
    }

    private static string RepeatName()
    {
        int bar = _lastKey.IndexOf('|');
        string last = bar >= 0 ? _lastKey.Substring(bar + 1) : _lastKey;
        return _lastKey.EndsWith(")", StringComparison.Ordinal) ? last : $"{last}, last {_repeatLastArg}";
    }

    /// <summary>Back to a fresh load (Init, tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _lastKey = "";
            _lastWrittenMs = long.MinValue;
            _repeats = 0;
            _repeatLastArg = "";
            _heldLoggedFor = 0;
            _heldLoggedAt = 0;
            _autoRunOn = false;
            _motionsOn.Clear();
            _lastStartMs = long.MinValue / 2;
            _actionHoldLogged.Clear();
            _actionHoldLoggedFor = long.MinValue;
            ClearPendingLocked();
            _pendingSinceMs = 0;
            _deferredStopsSent = 0;
            _lastChangeMs = long.MinValue / 2;
            _lastChangeWhat = "";
            _movementSends = 0;
        }
    }

    // -- Settle after a movement change -------------------------------------------------
    // A Bolt VII went out in the same tick as nav's SetAutoRun(False), after nav's turns; on ACE's
    // fast tick the movement packet cancelled the cast's turn and the server held the cast 40.8 s
    // (ActionPacer's stuck-cast notes). After any movement call RynthAi sends (a start, a stop, a
    // turn), nothing starts until MoveSettleMinMs has passed and the client's motion queue reads
    // clear (GetCastBusyState), or MoveSettleMaxMs. ActionPacer.MayStart asks (not for attack repeats).
    internal const long MoveSettleMinMs = 150;
    internal const long MoveSettleMaxMs = 600;
    private static long _lastChangeMs = long.MinValue / 2;
    private static string _lastChangeWhat = "";

    private static void Changed(string what)
    {
        lock (Gate) { _lastChangeMs = NowMs(); _lastChangeWhat = what; }
    }

    /// <summary>True (with why) while the last movement change has not settled.</summary>
    internal static bool SettleHolds(RynthCoreHost host, out string why)
    {
        why = "";
        long age; string what;
        lock (Gate) { age = NowMs() - _lastChangeMs; what = _lastChangeWhat; }
        if (age >= MoveSettleMaxMs) return false;
        bool queueBusy;
        try { queueBusy = host.HasGetCastBusyState && host.GetCastBusyState() != 0; } catch { queueBusy = false; }
        if (age >= MoveSettleMinMs && !queueBusy) return false;
        why = $"the movement change {what} sent {age} ms ago has not settled";
        return true;
    }
}
