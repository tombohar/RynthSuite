// ============================================================================
//  RynthSuite - Plugins/Shared/ActionPacer.cs
//  One server action at a time: RynthAi's pacing of uses, casts and combat-mode
//  changes on the server's answers (2026-10-06).
//
//  Why: Drakkon's "can't use anything" and "stuck in the cast animation" stalls on
//  DreamWeave (ops/overnight/2026-10-06-action-pacing.md). Every stall began with a
//  combat-mode change sent while a corpse use was still unanswered by the server
//  (4 of 4 onsets; 0 stalls after ~920 uses with no such overlap). Once in that state
//  the server finishes nothing that waits on an animation until a relog, and the bot
//  made it worse by firing the next cast/use every 0.4-2.5 s because nothing waited
//  for the server's answer: the client busy count it paced on was zeroed and repaired
//  hundreds of times an hour, and "cast resolved" meant the enchantment, not the
//  server's UseDone.
//
//  The rule here:
//    * A use, a use-on, a cast or a combat-mode change that RynthAi sends is the ONE
//      outstanding server action until the server answers it:
//        - use / use-on / cast: the next UseDone (GameEvent 0x01C7) after the send,
//          whatever its error (a refusal is an answer too); a wield also counts as
//          answered when the item reads wielded (a wield gets no UseDone);
//        - combat mode: the client reads the new mode and ModeSettleMs has passed
//          (the stance animation; the server sends no UseDone for it);
//      or its time limit passes (logged; nothing is force-cleared).
//    * While one is outstanding, RynthAi starts no other one (MayStart; mode changes
//      are refused right at ChangeCombatModeFor) and moves or turns the character
//      only to stop real movement (MoveAudit asks HoldsMovement).
//    * Heals keep some priority: at Emergency Heal At a heal may go ahead of a corpse/door
//      use the server hasn't answered after EmergencyAfterWorldUseMs, and one heal may
//      follow a cast whose result is already in (the server queues one cast behind the
//      recoil). A heal under the plain Heal line waits for the use's answer (2026-10-06,
//      below: an action during the use's server turn stops that turn for good).
//    * And nothing starts while the character is still animating: the client's own motion
//      queue (CMotionInterp pending motions, the engine's cast gate: GetCastBusyState) must
//      be empty, for uses, kits, potions, casts, stance changes, wields and the item
//      managers' moves (GestureHolds). Melee/missile attack repeats are exempt (MayStart
//      with animationGate false). Time limit GestureHoldMaxMs (GestureEmergencyMaxMs for an
//      emergency heal), logged, like Combat/CastGateWatchdog for casts.
//
//  Why the animation gate (2026-10-06 afternoon, ops/overnight/2026-10-06-turnto-stall.md,
//  replayed through ACE's own physics code): on ACE a use in reach is finished by the
//  server's MoveToManager turning the character (TurnRight on the server's copy of the
//  motion state). Every server motion for the character (a swing, a kit, a cast gesture,
//  a stance) is applied with MotionInterp.apply_raw_movement, which rewrites the
//  interpreted TurnCommand from the raw state (none) and so stops that TurnRight; the
//  MoveToManager is never told (its FailProgressCount is counted but never acted on) and
//  stays Initialized, which keeps the player "moving or animating", so Player_Tick never
//  runs the use's callback either. The use is answered only when the next use fails it:
//  "UseDone with no error, nothing opens", the stall's signature. Starting nothing while
//  the character animates, and nothing while a use's turn is unanswered, keeps every
//  server motion out of every server turn.
//
//  The player's turn (2026-10-29, RynthAi.Yield): another plugin acting for the player (the
//  Shortcuts bar's key press) holds every start (MayStart, GestureHolds, ChangeCombatModeFor) for
//  a moment, so the player's action goes next instead of waiting behind RynthAi's own. What is
//  already out still finishes. Each ask lasts at most PlayerHoldMaxMs; the asker asks again
//  every tick while its action waits or is in flight.
//
//  Opt-in (Enabled, RynthAi sets it at Init); off = every call goes through as before.
//  Linked into RynthAi only. UseAudit reports its sends through UseAudit.AfterSend.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal enum PacedKind
{
    None,
    /// <summary>A door, corpse, portal or NPC: the server walks/turns the character to it first.</summary>
    WorldUse,
    /// <summary>A kit, potion, stone, wand or weapon (wield), scroll... in the character's own packs.</summary>
    ItemUse,
    /// <summary>A spell cast.</summary>
    Cast,
    /// <summary>A combat-mode (stance) change.</summary>
    CombatMode,
}

internal enum HealNeed
{
    None,
    /// <summary>Health under a heal line.</summary>
    Heal,
    /// <summary>Health at or under Emergency Heal At.</summary>
    Emergency,
}

/// <summary>The one server action RynthAi is waiting on.</summary>
internal sealed class PacedAction
{
    public PacedKind Kind;
    public uint Id;
    public string Label = "";
    public string Who = "";
    public long SentMs;
    public int SeqAtSend;
    public int ModeTarget;
    public uint Target;          // a cast's target (the held-cast turn faces it)
    public bool Script;          // a cast a script chose (the combat hook): the breaker waits ScriptCastMinBreakMs
    public bool WieldedAtSend;
    public bool OnSelf;          // an item used on the character (a kit): the heal grace may go ahead of it
    public bool ResultSeen;      // a cast's result is in (enchantment / chat); the server is in its recoil
    public bool HealQueued;      // the one heal allowed behind that recoil went out
    public long ModeSeenMs;      // when the client first read the requested mode
    public long WieldSeenMs;     // when a wield first read done
    public readonly HashSet<string> LoggedWaits = new();
}

internal static class ActionPacer
{
    /// <summary>Off = nothing is paced (every call goes through as before). RynthAi turns it on.</summary>
    public static volatile bool Enabled;

    /// <summary>Clock (ms); tests replace it.</summary>
    public static Func<long> NowMs = () => Environment.TickCount64;

    // Time limits: the longest the server took to answer in 3.7 h of healthy logs (10-05/10-06,
    // p99 / max): corpse use 3.0 / 3.7 s, kit 2.5 s, buff cast 3.6 s, vital cast 4.7 / 5.0 s.
    public const long WorldUseMaxMs = 5000;
    public const long ItemUseMaxMs = 3500;
    public const long CastMaxMs = 7000;
    public const long ModeMaxMs = 2000;
    /// <summary>The stance animation after the client reads the new mode: no action before it ends.</summary>
    public const long ModeSettleMs = 800;
    /// <summary>A wield that reads done: a short settle for the wield motion.</summary>
    public const long WieldSettleMs = 150;
    /// <summary>At Emergency Heal At a heal may go ahead of a door/corpse use unanswered this long. It stops
    /// the server's walk/turn to that object (the use is then answered by the next one); a plain heal waits.</summary>
    public const long EmergencyAfterWorldUseMs = 300;
    /// <summary>The client's motion queue reading busy this long is taken as stuck: the action goes (logged).
    /// Same backstop as Combat/CastGateWatchdog (no real gesture, stance or swing lasts this long).</summary>
    public const long GestureHoldMaxMs = 4000;
    /// <summary>An emergency heal waits for the character's animation at most this long.</summary>
    public const long GestureEmergencyMaxMs = 1500;
    /// <summary>A heal behind a stance change: once the client has read the new mode this long.</summary>
    public const long HealAfterModeReadMs = 300;
    /// <summary>
    /// A melee/missile swing behind the stance change into that mode: once the client has read the
    /// mode this long, not the full ModeSettleMs (2026-10-14: the first swing of every fight came
    /// 0.8 s after the stance; the settle is there for uses, whose server turn a stance stops, and a
    /// swing is no use: the server plays it after the stance, as when a player attacks from peace).
    /// </summary>
    public const long AttackAfterModeReadMs = 300;
    /// <summary>An engine without UseDone: an action counts as answered after this (no real pacing).</summary>
    public const long NoUseDoneFallbackMs = 600;
    /// <summary>The longest one ask for the player's turn holds RynthAi (RynthAiYieldApiV1.MaxHoldMs).</summary>
    public const long PlayerHoldMaxMs = 3000;
    /// <summary>
    /// The heal grace (2026-11-07): the longest a heal under a heal line waits on a use of an item on
    /// the character (a kit) the server hasn't answered, and between the 'too busy' probes. Past it the
    /// heal goes (a refusal is an answer too, and the next heal retries). An item on yourself has no
    /// server turn to stop; corpse/door uses, casts, wields and the animation gate keep their rules.
    /// </summary>
    public const long HealGraceMs = 2000;

    private static readonly object Gate = new();
    private static PacedAction? _cur;

    // Counters for /ra pace and the periodic summary.
    private static long _started, _answered, _timedOut, _sentOverOutstanding, _modeRefused, _modeSkipped;
    private static long _heldStarts, _heldMoves, _healsAhead;
    private static long _gestureHolds, _gestureLimits;
    private static long _gestureBusySince;      // when the client's motion queue was first seen busy (0 = clear)
    private static bool _gestureOverride;       // the hold limit passed: open until the queue reads clear
    private static readonly HashSet<string> _gestureLogged = new();
    private static long _maxWaitMs;
    private static string _maxWaitWhat = "";
    private static readonly Dictionary<string, int> _holdsByWho = new();
    private static string _lastTimeout = "";

    // The player's turn (RynthAi.Yield): starts are held until _playerHoldUntil (0 = no hold).
    private static long _playerHoldUntil, _playerHoldSince, _playerHolds, _playerHeldStarts;
    private static readonly HashSet<string> _playerHoldLogged = new();

    public static long Started => _started;
    public static long Answered => _answered;
    public static long TimedOut => _timedOut;
    public static long SentOverOutstanding => _sentOverOutstanding;
    public static long ModeChangesRefused => _modeRefused;
    public static long ModeChangesSkipped => _modeSkipped;
    public static long HeldStarts => _heldStarts;
    public static long HeldMoves => _heldMoves;
    public static long HealsAhead => _healsAhead;
    public static long GestureHoldCount => _gestureHolds;
    public static long GestureLimits => _gestureLimits;
    /// <summary>Times another plugin took the player's turn, and the starts it held.</summary>
    public static long PlayerHolds => _playerHolds;
    public static long PlayerHeldStarts => _playerHeldStarts;

    /// <summary>Back to a fresh load (Init, logout, tests). Enabled is left as it is.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _cur = null;
            _started = _answered = _timedOut = _sentOverOutstanding = _modeRefused = _modeSkipped = 0;
            _refuseSince = 0; _refuseLast = 0; _refuseCount = 0; _engineRefusals = 0; _lastEngineRefusalMs = 0;
            _heldStarts = _heldMoves = _healsAhead = 0;
            _gestureHolds = _gestureLimits = 0;
            _swingSentMs = 0; _swingSeenBusy = false; _swingHolds = 0; _swingLogged.Clear();
            _gestureBusySince = 0;
            _gestureOverride = false;
            _gestureLogged.Clear();
            _maxWaitMs = 0;
            _maxWaitWhat = "";
            _holdsByWho.Clear();
            _playerHoldUntil = _playerHoldSince = _playerHolds = _playerHeldStarts = 0;
            _playerHoldLogged.Clear();
            _lastTimeout = "";
            ClearBusyLocked();
            _stuckBreaks = 0;
            _refusalHolds = 0;
            _heldTurns = 0;
            _forcedBreaks = 0;
            _jumpAt = 0;
            _lootJumpAt = 0;
            _cycleBySpell.Clear();
            _lastSeenSeq = int.MinValue;
            _lastFrameMs = 0;
            _gapEnd = 0;
            _gapLen = 0;
            _errorAnswers.Clear();
            _staleDrops = _latchExpiries = _burstHolds = _emergencyThroughLatch = 0;
            _healWaitAt = 0;
        }
    }

    /// <summary>
    /// The plugin's heartbeat, every frame whatever the macro does. A gap of PollGapMs or more between
    /// two calls (the client froze or loaded) makes a cast sent before the gap ENDED stale at the next
    /// check (a cast sent in the tick just before a freeze is sent after that tick's NoteFrame), so
    /// a teleport's or a freeze's gap is never mistaken for a stuck cast and broken by a jump. Never
    /// called (a plugin without it, the tests that don't): no gap detection, only StaleCastMs.
    /// </summary>
    public static void NoteFrame()
    {
        long now = NowMs();
        lock (Gate)
        {
            if (_lastFrameMs != 0 && now - _lastFrameMs >= PollGapMs) { _gapEnd = now; _gapLen = now - _lastFrameMs; }
            _lastFrameMs = now;
        }
    }

    /// <summary>Forget the outstanding action (logout, death, portal): the server dropped it.</summary>
    public static void Forget(string why)
    {
        // A death, portal or logout also ends a refusal streak: a refusal before it must not make one after it look like a long streak.
        lock (Gate) { _cur = null; ClearBusyLocked(); _refuseSince = 0; _refuseLast = 0; _refuseCount = 0; }
    }

    // ── Recording what went out ──────────────────────────────────────────────

    /// <summary>
    /// Records an action that just went out. <paramref name="seqAtSend"/> is the UseDone count read
    /// BEFORE the send. Logs once when it went out over another unanswered action (a player command,
    /// a meta, or a heal let ahead).
    /// </summary>
    public static void Begin(RynthCoreHost host, PacedKind kind, uint id, string label, string who, int seqAtSend, int modeTarget = 0,
        bool onSelf = false)
    {
        if (!Enabled || kind == PacedKind.None) return;
        PacedAction? prev = Current(host);
        long now = NowMs();
        var a = new PacedAction
        {
            Kind = kind, Id = id, Label = label, Who = who, SentMs = now, SeqAtSend = seqAtSend, ModeTarget = modeTarget,
            OnSelf = onSelf && kind == PacedKind.ItemUse,
        };
        if (kind == PacedKind.ItemUse) a.WieldedAtSend = IsWieldedByPlayer(host, id);
        string? over = null;
        lock (Gate)
        {
            _started++;
            if (prev != null)
            {
                _sentOverOutstanding++;
                over = $"[Pace] {who}: {Describe(kind, label)} went out while {Describe(prev.Kind, prev.Label)} ({prev.Who}) was unanswered for {now - prev.SentMs} ms";
                // Our cast is still the server's: a 0x1D to this send says it is open (the stuck-cast rules).
                if (prev.Kind == PacedKind.Cast && _shadow == null && _hung == null) _shadow = prev;
            }
            _cur = a;
        }
        if (over != null) Log(host, over);
    }

    /// <summary>A cast's result (enchantment landed, "You cast ...") is in: the server is in its recoil.</summary>
    public static void NoteCastResult()
    {
        lock (Gate)
        {
            if (_cur is { Kind: PacedKind.Cast }) _cur.ResultSeen = true;
        }
    }

    // ── The outstanding action ───────────────────────────────────────────────

    /// <summary>
    /// The action still waiting for the server's answer, or null. Each call settles answers and
    /// time limits first, so it is always current.
    /// </summary>
    public static PacedAction? Current(RynthCoreHost host)
    {
        if (!Enabled) return null;
        PacedAction? a;
        lock (Gate) a = _cur;
        if (a == null) return null;

        long now = NowMs();
        long age = now - a.SentMs;
        string? answeredHow = null;
        bool timedOut = false;

        switch (a.Kind)
        {
            case PacedKind.WorldUse:
            case PacedKind.ItemUse:
            case PacedKind.Cast:
            {
                bool hasSeq = false;
                int seq = 0;
                try { hasSeq = host.HasUseDoneSeq; if (hasSeq) seq = host.GetUseDoneSeq(); } catch { hasSeq = false; }
                if (hasSeq && seq != a.SeqAtSend)
                {
                    answeredHow = "UseDone";
                    if (a.Kind == PacedKind.Cast)
                    {
                        uint err = TooBusyErr;
                        try { if (host.TryGetLastUseDone(out int ls, out uint le) && ls == a.SeqAtSend + 1) err = le; } catch { }
                        if (err != TooBusyErr) NoteCycle(a.Id, age);
                    }
                }
                else if (a.Kind == PacedKind.ItemUse && IsWieldedByPlayer(host, a.Id) != a.WieldedAtSend)
                {
                    if (a.WieldSeenMs == 0) a.WieldSeenMs = now;
                    if (now - a.WieldSeenMs >= WieldSettleMs) answeredHow = "wield";
                }
                else if (!hasSeq && age >= NoUseDoneFallbackMs)
                {
                    answeredHow = "time (no UseDone on this engine)";
                }
                long max = a.Kind switch { PacedKind.WorldUse => WorldUseMaxMs, PacedKind.Cast => CastMaxMs, _ => ItemUseMaxMs };
                if (answeredHow == null && age >= max) timedOut = true;
                break;
            }
            case PacedKind.CombatMode:
            {
                int mode = -1;
                try { if (host.HasGetCurrentCombatMode) mode = host.GetCurrentCombatMode(); } catch { mode = -1; }
                if (mode == a.ModeTarget || mode < 0)
                {
                    if (a.ModeSeenMs == 0) a.ModeSeenMs = now;
                    if (age >= ModeSettleMs) answeredHow = "mode";
                }
                if (answeredHow == null && age >= ModeMaxMs) timedOut = true;
                break;
            }
        }

        if (answeredHow == null && !timedOut) return a;

        string? line = null;
        lock (Gate)
        {
            if (!ReferenceEquals(_cur, a)) return _cur;   // replaced meanwhile
            _cur = null;
            if (timedOut)
            {
                _timedOut++;
                _lastTimeout = $"{Describe(a.Kind, a.Label)} ({a.Who}) after {age} ms";
                if (a.Kind == PacedKind.Cast && _hung == null) _hung = a;   // still the server's: see the stuck-cast hold
                line = $"[Pace] no answer from the server to {Describe(a.Kind, a.Label)} ({a.Who}) in {age} ms - going on (nothing cleared)";
            }
            else
            {
                _answered++;
            }
        }
        if (line != null) Log(host, line);
        return null;
    }

    /// <summary>True while an action is waiting for the server (out: it).</summary>
    public static bool Outstanding(RynthCoreHost host, out PacedAction? action)
    {
        action = Current(host);
        return action != null;
    }

    // ── The player's turn ───────────────────────────────────────────────────

    /// <summary>
    /// Another plugin acting for the player (RynthAi.Yield): no start for <paramref name="ms"/> from now
    /// (at most <see cref="PlayerHoldMaxMs"/>), the newest ask setting the end. False when pacing is off:
    /// RynthAi then knows nothing about what it has in flight, so the asker goes by its old rules.
    /// </summary>
    public static bool HoldForPlayer(RynthCoreHost host, long ms)
    {
        if (!Enabled) return false;
        long now = NowMs();
        bool began;
        lock (Gate)
        {
            began = _playerHoldUntil == 0 || now >= _playerHoldUntil;
            if (began)
            {
                _playerHoldSince = now;
                _playerHolds++;
                _playerHoldLogged.Clear();
            }
            _playerHoldUntil = now + Math.Clamp(ms, 1, PlayerHoldMaxMs);
        }
        if (began) Log(host, "[Pace] the player's turn: a key press from another plugin goes first; RynthAi starts nothing new until it has gone");
        return true;
    }

    /// <summary>Ends the player's turn now (the asker's action was answered, or dropped).</summary>
    public static void ReleasePlayerHold(RynthCoreHost host)
    {
        long held;
        lock (Gate)
        {
            if (_playerHoldUntil == 0) return;
            held = NowMs() - _playerHoldSince;
            _playerHoldUntil = 0;
            _playerHoldLogged.Clear();
        }
        Log(host, $"[Pace] the player's turn is over after {held} ms; RynthAi goes on");
    }

    /// <summary>True (with why) while another plugin has the player's turn. One log line per hold and subsystem.</summary>
    private static bool PlayerHoldsStart(RynthCoreHost host, string who, out string why)
    {
        why = "";
        if (!Enabled) return false;
        long now = NowMs();
        bool log;
        lock (Gate)
        {
            if (_playerHoldUntil == 0) return false;
            if (now >= _playerHoldUntil)
            {
                // The asker stopped asking (unloaded, stuck): the hold ends by itself.
                _playerHoldUntil = 0;
                _playerHoldLogged.Clear();
                return false;
            }
            _playerHeldStarts++;
            log = _playerHoldLogged.Add(who);
        }
        why = "the player's key press goes first";
        if (log) Log(host, $"[Pace] {who} waits: {why}");
        return true;
    }

    // ── Gates ───────────────────────────────────────────────────────────────

    /// <summary>
    /// May <paramref name="who"/> send a use, a cast or a mode change now? False (with why) while
    /// another action waits for the server, except the heal rules in the file header, and while
    /// the character is still animating (<see cref="GestureHolds"/>; skipped with
    /// <paramref name="animationGate"/> false: melee/missile attack repeats). One log line per
    /// outstanding action (or animation) and subsystem.
    /// </summary>
    /// <param name="animationGate">False: skip <see cref="GestureHolds"/> (melee/missile attack repeats).</param>
    /// <param name="settleGate">False: skip the movement settle (<see cref="MoveAudit.SettleHolds"/>; attack repeats only).</param>
    /// <param name="attackRepeat">
    /// A melee/missile attack repeat: never held by the 'too busy' latch. ACE sends no UseDone for a
    /// swing, so a latch set by anything else (a refused pickup) would otherwise leave combat only the
    /// probe slots.
    /// </param>
    public static bool MayStart(RynthCoreHost host, string who, HealNeed heal, out string why,
        bool animationGate = true, bool settleGate = true, bool attackRepeat = false)
    {
        if (!Enabled) { why = ""; return true; }
        if (PlayerHoldsStart(host, who, out why)) return false;
        // A buff, heal, kit or potion in the air is refused by ACE: wait for the landing. Combat checks
        // it itself, at the attack, so a stance change is not held; loot and doors never.
        if (who == "Buff" && LootJumpAirborne(host, out why)) return false;
        if (ServerBusyHolds(host, who, attackRepeat, out why, out bool tryAfterBusy, heal)) return false;
        // An emergency heal does not wait for the movement settle (150-600 ms): at Emergency Heal At
        // the heal comes first; it still obeys the in-flight rules below (OutstandingAllows, GestureHolds).
        if (settleGate && heal != HealNeed.Emergency && MoveAudit.SettleHolds(host, out why))
        {
            bool log;
            lock (Gate) { _settleHolds++; log = _settleLogged.Add(who); }
            if (log) Log(host, $"[Pace] {who} waits: {why}");
            return false;
        }
        lock (Gate) _settleLogged.Clear();
        if (!OutstandingAllows(host, who, heal, out why, attackRepeat)) return false;
        if (animationGate && GestureHolds(host, who, heal, out why)) return false;
        // The one try after a 'too busy' answer is spent only by a start that goes: one held by the checks
        // above left it for the next asker. A Conquest report (10-25): after a corpse pickup and a cast went
        // out together, the buff step took all three tries and sent nothing (still animating), combat never
        // got one, and the fight stood for the latch's 15 s.
        if (tryAfterBusy) SpendTryAfterBusy(host, who);
        return true;
    }

    private static void SpendTryAfterBusy(RynthCoreHost host, string who)
    {
        int n;
        lock (Gate)
        {
            _probes++;
            _lastProbeAt = NowMs();
            n = _probes;
        }
        Log(host, $"[Pace] {who}: one try (#{n}) after the server's 'too busy' answer; no cast of ours is open");
    }

    private static bool OutstandingAllows(RynthCoreHost host, string who, HealNeed heal, out string why, bool attack = false)
    {
        why = "";
        PacedAction? a = Current(host);
        if (a == null) return true;
        long age = NowMs() - a.SentMs;

        // The first swing after the change into Melee/Missile mode (see AttackAfterModeReadMs).
        if (attack && a.Kind == PacedKind.CombatMode && a.ModeSeenMs != 0 && NowMs() - a.ModeSeenMs >= AttackAfterModeReadMs)
            return true;

        if (heal != HealNeed.None)
        {
            if (a.Kind == PacedKind.WorldUse && heal == HealNeed.Emergency && age >= EmergencyAfterWorldUseMs)
            {
                lock (Gate) _healsAhead++;
                Log(host, $"[Pace] {who}: the heal goes ahead of {Describe(a.Kind, a.Label)} (unanswered for {age} ms)");
                return true;
            }
            // A stance change (most often the one this heal asked for: the wand swap, or peace for
            // a kit): the heal goes once the client has read the new mode for HealAfterModeReadMs,
            // like BuffManager's emergency settle, not the full ModeSettleMs.
            if (a.Kind == PacedKind.CombatMode && a.ModeSeenMs != 0 && NowMs() - a.ModeSeenMs >= HealAfterModeReadMs)
                return true;
            if (a.Kind == PacedKind.Cast && a.ResultSeen && !a.HealQueued)
            {
                a.HealQueued = true;
                lock (Gate) _healsAhead++;
                return true;
            }
            // The heal grace: an item used on the character (a kit) still unanswered after HealGraceMs.
            // A lost or swallowed kit is sent again instead of holding every heal to ItemUseMaxMs.
            if (a.OnSelf && age >= HealGraceMs)
            {
                bool logAhead;
                lock (Gate) { _healsAhead++; logAhead = a.LoggedWaits.Add(who + "/heal-grace"); }
                if (logAhead) Log(host, $"[Pace] {who}: the heal goes ahead of {Describe(a.Kind, a.Label)} (unanswered for {age} ms, heal grace {HealGraceMs} ms)");
                return true;
            }
        }

        why = $"{Describe(a.Kind, a.Label)} sent {age} ms ago by {a.Who} has no answer yet";
        bool log;
        lock (Gate)
        {
            _heldStarts++;
            _holdsByWho[who] = _holdsByWho.TryGetValue(who, out int n) ? n + 1 : 1;
            if (age > _maxWaitMs) { _maxWaitMs = age; _maxWaitWhat = Describe(a.Kind, a.Label); }
            log = a.LoggedWaits.Add(who);
        }
        if (log) Log(host, $"[Pace] {who} waits: {why}");
        return false;
    }

    /// <summary>
    /// True (with why) while the character is still animating: the client's own motion queue
    /// (CMotionInterp pending motions, read by the engine as GetCastBusyState) is not empty.
    /// The client animates exactly the motions the server sends for the character, a moment
    /// after the server applies them, so an empty queue means the server's copy is done too.
    /// Past GestureHoldMaxMs (GestureEmergencyMaxMs for an emergency heal) the action goes,
    /// logged once per busy spell. No opinion (false) on an engine without the gate.
    /// </summary>
    public static bool GestureHolds(RynthCoreHost host, string who, HealNeed heal, out string why)
    {
        why = "";
        if (!Enabled) return false;
        if (PlayerHoldsStart(host, who, out why)) return true;   // the item managers' moves wait too
        bool busy;
        try { busy = host.HasGetCastBusyState && host.GetCastBusyState() != 0; } catch { busy = false; }
        long now = NowMs();
        if (SwingHolds(host, who, heal, busy, now, out why)) return true;
        string? line = null;
        lock (Gate)
        {
            if (!busy)
            {
                if (_gestureOverride)
                    line = "[Pace] the character's animation queue reads clear again";
                _gestureBusySince = 0;
                _gestureOverride = false;
                _gestureLogged.Clear();
            }
            else
            {
                if (_gestureBusySince == 0) _gestureBusySince = now;
                long age = now - _gestureBusySince;
                if (!_gestureOverride && age >= GestureHoldMaxMs)
                {
                    _gestureOverride = true;
                    _gestureLimits++;
                    line = $"[Pace] {who}: the character's animation queue has read busy for {age} ms - taken as stuck, going on";
                }
                if (_gestureOverride)
                {
                    busy = false;
                }
                else if (heal == HealNeed.Emergency && age >= GestureEmergencyMaxMs)
                {
                    _gestureLimits++;
                    line = $"[Pace] {who}: emergency heal after {age} ms of animation - going on";
                    busy = false;
                }
                else
                {
                    _gestureHolds++;
                    _holdsByWho[who] = _holdsByWho.TryGetValue(who, out int n) ? n + 1 : 1;
                    why = $"the character is still animating ({age} ms)";
                    if (_gestureLogged.Add(who))
                        line = $"[Pace] {who} waits: {why}";
                }
            }
        }
        if (line != null) Log(host, line);
        return busy;
    }

    // A swing sent but not animating yet (Drakkon 2026-10-06 16:45:37): the wand's use went 110 ms
    // after a MeleeAttack, before the swing showed in the client's motion queue, so the motion gate
    // let it through; the switch landed on the swing the server was starting and the next cast was
    // held open. After CombatManager sends an attack, nothing else starts until the client's motion
    // queue has read busy and then clear (the swing played), or SwingStartMaxMs passes.
    public const long SwingStartMaxMs = 1500;
    public const long SwingEmergencyMaxMs = 500;
    private static long _swingSentMs;
    private static bool _swingSeenBusy;
    private static long _swingHolds;
    private static readonly HashSet<string> _swingLogged = new();
    public static long SwingHoldCount => _swingHolds;

    /// <summary>Attacks CombatManager reported (NoteAttackSent): a caller that remembers the count can
    /// tell whether a swing went out since (BuffCastChain). Not reset, only compared.</summary>
    public static long AttacksSent { get; private set; }

    /// <summary>CombatManager sent a melee or missile attack.</summary>
    public static void NoteAttackSent()
    {
        lock (Gate)
        {
            AttacksSent++;
            _swingSentMs = NowMs();
            _swingSeenBusy = false;
            _swingLogged.Clear();
        }
    }

    private static bool SwingHolds(RynthCoreHost host, string who, HealNeed heal, bool busy, long now, out string why)
    {
        why = "";
        string? line = null;
        bool hold = false;
        lock (Gate)
        {
            if (_swingSentMs == 0) return false;
            long age = now - _swingSentMs;
            if (busy) _swingSeenBusy = true;
            if ((_swingSeenBusy && !busy) || age >= SwingStartMaxMs)
            {
                _swingSentMs = 0;
            }
            else if (!_swingSeenBusy && !(heal == HealNeed.Emergency && age >= SwingEmergencyMaxMs))
            {
                hold = true;
                _swingHolds++;
                why = $"a swing sent {age} ms ago hasn't started animating yet";
                if (_swingLogged.Add(who)) line = $"[Pace] {who} waits: {why}";
            }
        }
        if (line != null) Log(host, line);
        return hold;
    }

    /// <summary>
    /// True while the outstanding action is one the server animates (all of them: the walk/turn
    /// to a used object, a heal or wield motion, a cast, a stance change). MoveAudit holds
    /// RynthAi's movement starts and turns meanwhile.
    /// </summary>
    public static bool HoldsMovement(RynthCoreHost host, out string what)
    {
        what = "";
        PacedAction? a = Current(host);
        if (a == null) return false;
        what = $"{Describe(a.Kind, a.Label)} ({a.Who}, {NowMs() - a.SentMs} ms)";
        lock (Gate) _heldMoves++;
        return true;
    }

    // -- A cast the server holds open, and a server that answers "too busy" ----------------
    // Two hangs in a client log, both ACE's fast-tick turn-to-cast wait (Player_Magic
    // TurnTo_Magic / PendingTurnRelease: the cast waits until the character faces its target within
    // spellcast_max_angle, with no time limit): a Bolt VII sent in the same tick as nav's autorun stop
    // (answered 40.8 s later) and a Drain at a moving target (its
    // incantation 2865 ms after the send, fizzled 37 s later on a client mode change). The server
    // kept IsBusy, so every cast after it came back UseDone 0x1D, and the vitals path re-sent a heal
    // every 2.5 s. So:
    //   * a cast that times out unanswered (CastMaxMs) is kept as the server's (hung);
    //   * a UseDone with 0x1D latches "server busy": nothing starts (MayStart) until a UseDone with
    //     another error arrives. With no cast of ours open, one probe goes after BusyProbeMs, then
    //     after twice that each time, up to BusyProbeMaxMs (the busy thing may have no UseDone of its
    //     own, e.g. a pickup). No timer re-sends into a server busy with our own cast;
    //   * a cast of ours still unanswered TurnAfterMs past its normal cycle: turn to face its target
    //     (the server's angle check then lets it finish), once, when RynthAi is not moving us;
    //   * still unanswered JumpAfterMs past its normal cycle (capped by StuckCastMs), or a 0x1D to
    //     another send while it is open past RefusalBreakMinMs: the breaker, a jump (0xF61B, TapJump):
    //     on fast tick ACE's HandleActionJump clears the pending motions and calls FailCast (origin/
    //     master Player.cs ~1195-1214), a 0x402 fizzle. Nothing starts until the character has landed
    //     (a cast in the air is refused). No UseDone BreakerWaitMaxMs after landing: a Peace mode
    //     request (HandleActionChangeCombatMode_Inner also calls FailCast, fast tick or not); then
    //     BreakerWaitMaxMs more and it goes on (logged).

    /// <summary>ACE's YoureTooBusy, as a UseDone error.</summary>
    public const uint TooBusyErr = 0x1D;
    /// <summary>With no cast of ours open, one start is let through this long after a 0x1D (doubling per probe).</summary>
    public const long BusyProbeMs = 1500;
    public const long BusyProbeMaxMs = 6000;
    /// <summary>A cast's normal send-to-UseDone time when none was seen for its spell, and the floor of a learned one.</summary>
    public const long DefaultCycleMs = 3000;
    /// <summary>Past its normal cycle: turn to face the target after this, jump after this.</summary>
    public const long TurnAfterMs = 1000;
    public const long JumpAfterMs = 2000;
    /// <summary>
    /// After a held-cast turn goes out, no jump for this long: the turn (up to ~180 deg) and the cast
    /// it lets finish. Drakkon (2026-10-16) was turned 91 deg at 5.2 s and jumped 1 s later, while
    /// hunting; the cast's UseDone came 0.2 s after the jump.
    /// </summary>
    public const long TurnGraceMs = 2500;
    /// <summary>
    /// A 0x1D to another send breaks our open cast only once it is this old, and RefusalPastCycleMs
    /// past its normal cycle (a recoil refusal is normal: a follow-up heal sent as the first one
    /// landed was refused, and the landed heal was jumped at 3.0 s, its UseDone due at 3.5 s).
    /// </summary>
    public const long RefusalBreakMinMs = 3000;
    public const long RefusalPastCycleMs = 1000;
    /// <summary>After the jump: landed once the height is back (and this long passed), or at the max.</summary>
    public const long JumpLandMinMs = 400;
    public const long JumpLandMaxMs = 1500;
    /// <summary>After landing (or after the Peace request), the longest wait for a UseDone before the next step.</summary>
    public const long BreakerWaitMaxMs = 3000;
    /// <summary>The stuck-cast limit (ms from the send; 0 = no turn, no breaker). RynthAi sets it from its settings.</summary>
    public static long StuckCastMs = 8000;
    /// <summary>
    /// A script-chosen cast is not broken by a jump before this (ms from the send), whatever its
    /// cycle, Stuck Cast Break or a 'too busy' answer say: a script's slower spells (a siphon
    /// answered about 5.5 s after the send) get 6 s from the send before the first jump; the
    /// held-cast nudge, which counts from the incantation, comes later.
    /// </summary>
    public const long ScriptCastMinBreakMs = 6000;
    /// <summary>
    /// A cast of ours this old (or one sent before a gap of PollGapMs between NoteFrame calls: a
    /// client freeze, a long load) is stale: dropped with the latch, never broken. Bounds every hold,
    /// the breaker off (StuckCastMs 0) included.
    /// </summary>
    public const long StaleCastMs = 3 * (CastMaxMs > BusyProbeMaxMs ? CastMaxMs : BusyProbeMaxMs);
    public const long PollGapMs = 5000;
    /// <summary>A 'too busy' latch with no cast of ours open is cleared after this long (logged).</summary>
    public const long LatchMaxMs = 15000;
    /// <summary>'Too busy' UseDones in the last second at which every start but a heal waits (bounds refusal chat lines).</summary>
    public const int RefusalBurstMax = 5;
    public const int PeaceMode = 1, MagicMode = 8;

    private enum BreakStage { None, Turned, Jumped, Landed, ModeSent }

    private static PacedAction? _hung;           // a cast past CastMaxMs with no UseDone
    private static PacedAction? _shadow;         // our cast still unanswered when another action went out over it
    private static long _busySince;              // first 0x1D of the latch (0 = no latch)
    private static int _busyRefusals;            // 0x1D answers in this latch
    private static int _probes;
    private static long _lastProbeAt;
    private static BreakStage _stage;
    private static long _stageAt;
    private static PacedAction? _stageFor;
    private static long _turnedAt;               // when the held-cast turn for _stageFor went out (0 = none)
    private static long _jumpAt;                 // 0 = not in the air after a breaker jump
    private static float _jumpZ;
    private static int _lastSeenSeq = int.MinValue;
    private static long _lastFrameMs;                // the last NoteFrame (gap detection; 0 = never)
    private static long _gapEnd, _gapLen;            // the frame that ended the last gap, and its length (0 = none)
    private static readonly Queue<long> _errorAnswers = new();   // times of error UseDones (burst limiter)
    private static long _staleDrops, _latchExpiries, _burstHolds, _emergencyThroughLatch;
    private static long _healWaitAt;                 // when a plain heal was last held by the 'too busy' latch (0 = none)
    /// <summary>A heal held by the latch this recently keeps the latch's probe slot from starts that are no heal.</summary>
    public const long HealWaitFreshMs = 500;
    public static long EmergencyThroughLatch => _emergencyThroughLatch;
    public static long StaleDrops => _staleDrops;
    public static long LatchExpiries => _latchExpiries;
    public static long BurstHolds => _burstHolds;
    private static long _stuckBreaks, _refusalHolds, _heldTurns, _forcedBreaks, _settleHolds;
    private static readonly HashSet<string> _settleLogged = new();
    public static long SettleHoldCount => _settleHolds;
    private static readonly HashSet<string> _busyLogged = new();
    private static readonly Dictionary<uint, long> _cycleBySpell = new();
    public static long StuckBreaks => _stuckBreaks;
    public static long RefusalHolds => _refusalHolds;
    public static long HeldCastTurns => _heldTurns;
    /// <summary>True while the server is taken as busy (a 0x1D latch or a hung cast).</summary>
    public static bool ServerBusy { get { lock (Gate) return _busySince != 0 || _hung != null; } }
    /// <summary>True while the character is in the air after a breaker jump.</summary>
    public static bool BreakerAirborne { get { lock (Gate) return _jumpAt != 0; } }

    private static void ClearBusyLocked()
    {
        _hung = null;
        _shadow = null;
        _busySince = 0;
        _busyRefusals = 0;
        _probes = 0;
        _lastProbeAt = 0;
        _stage = BreakStage.None;
        _stageAt = 0;
        _stageFor = null;
        _turnedAt = 0;
        _busyLogged.Clear();
    }

    /// <summary>The normal send-to-UseDone time of <paramref name="spellId"/>: the longest of its last answers, at least DefaultCycleMs.</summary>
    internal static long NormalCycleMs(uint spellId)
    {
        lock (Gate) return _cycleBySpell.TryGetValue(spellId, out long c) ? Math.Max(DefaultCycleMs, c) : DefaultCycleMs;
    }

    /// <summary>A cast of <paramref name="spellId"/> was answered (not 0x1D) <paramref name="ms"/> after its send.</summary>
    private static void NoteCycle(uint spellId, long ms)
    {
        if (ms <= 0 || ms > CastMaxMs) return;
        lock (Gate)
        {
            // The longest recent answer, easing down: a slow one counts at once, old slow ones fade.
            _cycleBySpell[spellId] = _cycleBySpell.TryGetValue(spellId, out long c) ? Math.Max(ms, (c * 7 + ms) / 8) : ms;
        }
    }

    /// <summary>
    /// The macro was switched on. The host gives no time for a UseDone, only its count, so a UseDone that
    /// came while nothing was checking (the macro off) cannot be told from a new one; the next check takes
    /// the current count as its baseline instead, so a stale 0x1D read at the start latches nothing.
    /// </summary>
    public static void NoteMacroStart()
    {
        lock (Gate) _lastSeenSeq = int.MinValue;
    }

    /// <summary>Reads the last UseDone: a 0x1D starts or feeds the latch, any other answer clears it.</summary>
    private static void ObserveUseDone(RynthCoreHost host, long now, out string? line)
    {
        line = null;
        int seq; uint err;
        try
        {
            if (!host.HasUseDoneSeq || !host.TryGetLastUseDone(out seq, out err)) return;
        }
        catch { return; }
        lock (Gate)
        {
            if (_lastSeenSeq == int.MinValue) { _lastSeenSeq = seq; return; }
            if (seq == _lastSeenSeq) return;
            _lastSeenSeq = seq;
            if (err == TooBusyErr) _errorAnswers.Enqueue(now);   // the burst counts 'too busy' only (0x401 retries are paced on their own)
            if (err == TooBusyErr)
            {
                if (_busySince == 0) { _busySince = now; if (_hung == null) _busyLogged.Clear(); }
                _busyRefusals++;
                return;
            }
            if (_busySince != 0 || _hung != null || _stage != BreakStage.None)
                line = $"[Pace] the server answered again (UseDone 0x{err:X}): casting resumes" +
                       (_hung != null ? $" ({Describe(_hung.Kind, _hung.Label)} ended {now - _hung.SentMs} ms after its send)" : "");
            ClearBusyLocked();
        }
    }

    /// <summary>
    /// True (with why) while the server is taken as busy (a 0x1D answer with no other answer since,
    /// a hung cast) or the character is in the air after a breaker jump. Runs the held-cast turn and
    /// the stuck-cast breaker. Called by MayStart.
    /// </summary>
    private static bool ServerBusyHolds(RynthCoreHost host, string who, bool attackRepeat, out string why, out bool tryAfterBusy, HealNeed heal = HealNeed.None)
    {
        why = "";
        tryAfterBusy = false;
        bool healStart = heal != HealNeed.None;
        long now = NowMs();
        long gapEnd, gapLen;
        lock (Gate) { gapEnd = _gapEnd; gapLen = _gapLen; }
        ObserveUseDone(host, now, out string? line);
        if (line != null) Log(host, line);

        if (AirborneHolds(host, now, out why)) return true;

        PacedAction? open = Current(host);
        PacedAction? stuck;
        bool probe = false;
        long age = 0;
        string? staleLine = null;
        lock (Gate)
        {
            if (_shadow != null && _hung == null && now - _shadow.SentMs >= CastMaxMs) { _hung = _shadow; _shadow = null; }
            stuck = _hung ?? _shadow ?? (open is { Kind: PacedKind.Cast } ? open : null);
            if (stuck != null) age = now - stuck.SentMs;
            // Stale: too old to still be the server's, or sent before a gap with no checks (a freeze,
            // portal space, the macro off). Dropped with the latch, never broken.
            bool beforeGap = stuck != null && gapEnd != 0 && stuck.SentMs < gapEnd;
            if (stuck != null && (age >= StaleCastMs || beforeGap))
            {
                staleLine = $"[Pace] {Describe(stuck.Kind, stuck.Label)} ({stuck.Who}) is stale ({age} ms" +
                            (beforeGap ? $", sent before a {gapLen} ms gap with no check" : "") + "): dropped, the server is taken as free";
                if (ReferenceEquals(_cur, stuck)) _cur = null;
                ClearBusyLocked();
                _staleDrops++;
                stuck = null;
            }
            // A 'too busy' latch with no cast of ours open: bounded, not probed for ever.
            else if (stuck == null && _busySince != 0 && now - _busySince >= LatchMaxMs && _stage == BreakStage.None)
            {
                staleLine = $"[Pace] no answer cleared the 'too busy' latch in {now - _busySince} ms and no cast of ours is open: going on";
                ClearBusyLocked();
                _latchExpiries++;
            }
            if (_stageFor != null && !ReferenceEquals(_stageFor, stuck)) { _stage = BreakStage.None; _stageAt = 0; _stageFor = null; }
            // A forced break with no cast of ours open (stage for nothing): once on the ground, it is
            // over. Left set it would block the latch expiry and the probe for good.
            else if (_stageFor == null && _stage != BreakStage.None && _jumpAt == 0) { _stage = BreakStage.None; _stageAt = 0; }
            // A plain heal held by the latch (an emergency heal is never held by it): the probe slot is its.
            // While one waited a moment ago, a start that is no heal does not take the slot (a door or corpse
            // use would send first every time and the heal would see the next slot, up to BusyProbeMaxMs later).
            if (heal == HealNeed.Heal && _busySince != 0) _healWaitAt = now;
            bool healWaiting = heal != HealNeed.Heal && _healWaitAt != 0 && now - _healWaitAt < HealWaitFreshMs;
            // A waiting heal's probe comes at least every HealGraceMs (the doubling would reach BusyProbeMaxMs).
            long probeWait = Math.Min(BusyProbeMs << Math.Min(_probes, 8), BusyProbeMaxMs);
            if (heal == HealNeed.Heal) probeWait = Math.Min(probeWait, HealGraceMs);
            if (!attackRepeat && stuck == null && _busySince != 0 && _stage == BreakStage.None && !healWaiting
                && now - Math.Max(_busySince, _lastProbeAt) >= probeWait)
            {
                probe = true;   // spent in MayStart once the start passes its other checks (SpendTryAfterBusy)
            }
        }

        if (staleLine != null) Log(host, staleLine);
        if (stuck != null && !IsPortaling(host)) StepBreaker(host, stuck, age, now);
        if (AirborneHolds(host, now, out why)) return true;

        // Any source: RefusalBurstMax 'too busy' answers in the last second hold every start but a heal for the rest
        // of that second (each refusal is a chat line; a burst of them is the 0x00460D1D crash class).
        int burst;
        lock (Gate)
        {
            while (_errorAnswers.Count > 0 && now - _errorAnswers.Peek() >= 1000) _errorAnswers.Dequeue();
            burst = _errorAnswers.Count;
            if (burst >= RefusalBurstMax && !healStart) _burstHolds++;
        }
        // A heal is never held by the burst (an emergency must not wait on refusals of other starts).
        if (burst >= RefusalBurstMax && !healStart)
        {
            why = $"{burst} refusals from the server in the last second";
            return true;
        }

        // Attack repeats are never held by the latch (no UseDone ever comes for a swing).
        if (attackRepeat) return false;

        bool latched;
        lock (Gate) latched = _busySince != 0 || _hung != null;
        // Our cast still inside CastMaxMs with no 0x1D: OutstandingAllows holds it (heal rules included).
        if (!latched) return false;
        // At Emergency Heal At the heal is not held by the latch, whatever set it (any refused use or
        // pickup): a refusal of the heal costs a chat line, a held heal can cost the character. The jump
        // wait (AirborneHolds, above) and the in-flight rules (MayStart) still apply.
        if (heal == HealNeed.Emergency)
        {
            bool logEm;
            lock (Gate) { _emergencyThroughLatch++; logEm = _busyLogged.Add(who + "/emergency"); }
            if (logEm) Log(host, $"[Pace] {who}: the emergency heal goes ahead of the server's 'too busy' answer");
            return false;
        }
        if (probe)
        {
            tryAfterBusy = true;
            return false;
        }

        int refusals;
        BreakStage stage;
        lock (Gate)
        {
            _refusalHolds++;
            _holdsByWho[who] = _holdsByWho.TryGetValue(who, out int n) ? n + 1 : 1;
            refusals = _busyRefusals;
            stage = _stage;
        }
        string what = stuck != null ? $"{Describe(stuck.Kind, stuck.Label)} ({stuck.Who}) has had no answer for {age} ms" : "the server answered 'too busy'";
        why = stage >= BreakStage.Jumped
            ? $"{what}; stuck-cast break sent, waiting for its UseDone"
            : $"{what}{(refusals > 0 ? $" ({refusals} 'too busy' answer(s))" : "")}: nothing goes until the server answers";
        bool log;
        lock (Gate) log = _busyLogged.Add(who);
        if (log) Log(host, $"[Pace] {who} waits: {why}");
        return true;
    }

    /// <summary>The held-cast turn and the breaker steps for <paramref name="stuck"/> (one step per call at most).</summary>
    private static void StepBreaker(RynthCoreHost host, PacedAction stuck, long age, long now)
    {
        if (StuckCastMs <= 0) return;
        long cycle = NormalCycleMs(stuck.Id);
        long jumpAt = Math.Min(cycle + JumpAfterMs, StuckCastMs);
        long turnAt = Math.Min(cycle + TurnAfterMs, jumpAt);
        long refusalBreakAt = Math.Max(RefusalBreakMinMs, cycle + RefusalPastCycleMs);
        if (stuck.Script) { jumpAt = Math.Max(jumpAt, ScriptCastMinBreakMs); refusalBreakAt = Math.Max(refusalBreakAt, ScriptCastMinBreakMs); }
        BreakStage stage; long stageAt; int refusals; long turnedAt;
        lock (Gate) { stage = _stage; stageAt = _stageAt; refusals = _busyRefusals; turnedAt = ReferenceEquals(_stageFor, stuck) ? _turnedAt : 0; }

        bool refusalBreak = refusals > 0 && age >= refusalBreakAt;
        if (stage == BreakStage.None && age >= turnAt && age < jumpAt && !refusalBreak)
        {
            SetStage(BreakStage.Turned, now, stuck);
            if (TurnToFace(host, stuck, age)) lock (Gate) _turnedAt = now;
            return;
        }
        // The turn gets its time before the timer's jump (StuckCastMs still caps it).
        if (turnedAt != 0)
            jumpAt = Math.Max(jumpAt, Math.Min(turnedAt - stuck.SentMs + TurnGraceMs, StuckCastMs));
        if (stage <= BreakStage.Turned && (age >= jumpAt || refusalBreak))
        {
            string why = age >= jumpAt ? $"{age} ms with no answer ({cycle} ms is normal for it)" : $"a send was refused 'too busy' while it was open {age} ms";
            Jump(host, stuck, age, why, forced: false);
            return;
        }
        if (stage == BreakStage.Landed && now - stageAt >= BreakerWaitMaxMs)
        {
            SetStage(BreakStage.ModeSent, now, stuck);
            PeaceBreak(host, stuck, age);
            return;
        }
        if (stage == BreakStage.ModeSent && now - stageAt >= BreakerWaitMaxMs)
        {
            lock (Gate) ClearBusyLocked();
            Log(host, $"[Pace] no UseDone after the stuck-cast break ({Describe(stuck.Kind, stuck.Label)}, {age} ms) - going on");
        }
    }

    private static void SetStage(BreakStage stage, long now, PacedAction? forAction)
    {
        lock (Gate) { if (!ReferenceEquals(_stageFor, forAction)) _turnedAt = 0; _stage = stage; _stageAt = now; _stageFor = forAction; }
    }

    /// <summary>
    /// Landed after a jump at <paramref name="jumpAt"/> from height <paramref name="z0"/>: the height
    /// is back (looked at from JumpLandMinMs on), or JumpLandMaxMs has passed whatever it says.
    /// </summary>
    private static bool Landed(RynthCoreHost host, long now, long jumpAt, float z0)
    {
        long air = now - jumpAt;
        bool landed = air >= JumpLandMaxMs;
        if (!landed && air >= JumpLandMinMs)
        {
            try { landed = !host.HasGetPlayerPose || (host.TryGetPlayerPose(out _, out _, out _, out float z, out _, out _, out _, out _) && Math.Abs(z - z0) < 0.1f); }
            catch { landed = true; }
        }
        return landed;
    }

    // Jump-loot (CorpseOpenController): the corpse pickup's tap jump. ACE refuses a cast, a swing, a
    // shot, a kit, food or a potion in the air (Player_Magic/Melee/Missile, Healer, Food: IsJumping ->
    // YouCantDoThatWhileInTheAir, 0x4EB), so combat and buffs wait until the character has landed
    // instead of sending into a certain refusal. Loot does not: ACE takes a pickup and a corpse use in
    // the air, so this never holds MayStart("Loot"). The hold is a pure wait, capped at JumpLandMaxMs
    // from the jump, so a missed landing cannot stall a heal for longer than that.
    private static long _lootJumpAt;   // 0 = not in the air after a loot jump
    private static float _lootJumpZ;

    /// <summary>A loot jump went out (the client took it): the character is in the air from now.</summary>
    public static void NoteLootJump(RynthCoreHost host)
    {
        float z = 0;
        try { if (host.HasGetPlayerPose) host.TryGetPlayerPose(out _, out _, out _, out z, out _, out _, out _, out _); } catch { }
        long now = NowMs();
        lock (Gate) { _lootJumpAt = now; _lootJumpZ = z; }
    }

    /// <summary>
    /// True (with why) while the character is in the air after a loot jump: until the height is back
    /// (from JumpLandMinMs) or JumpLandMaxMs at most. Pure wait: nothing is counted, refused or
    /// retried. Independent of <see cref="Enabled"/>.
    /// </summary>
    public static bool LootJumpAirborne(RynthCoreHost host, out string why)
    {
        why = "";
        long jumpAt; float z0;
        lock (Gate) { jumpAt = _lootJumpAt; z0 = _lootJumpZ; }
        if (jumpAt == 0) return false;
        long now = NowMs();
        if (!Landed(host, now, jumpAt, z0))
        {
            why = $"in the air after the loot jump ({now - jumpAt} ms)";
            return true;
        }
        lock (Gate) { if (_lootJumpAt == jumpAt) _lootJumpAt = 0; }
        return false;
    }

    /// <summary>
    /// Ms since the last loot jump went out, or -1 when none is noted (never jumped, landed and
    /// cleared by <see cref="LootJumpAirborne"/>, or forgotten). Read it BEFORE LootJumpAirborne
    /// when the answer matters: a landing seen there clears the jump.
    /// </summary>
    public static long LootJumpAgeMs()
    {
        long jumpAt;
        lock (Gate) jumpAt = _lootJumpAt;
        return jumpAt == 0 ? -1 : NowMs() - jumpAt;
    }

    /// <summary>No loot jump in the air any more (death, portal space, logout: RynthAiPlugin).</summary>
    public static void ForgetLootJump() { lock (Gate) _lootJumpAt = 0; }

    /// <summary>In the air after a breaker jump: hold until landed (height back, or JumpLandMaxMs).</summary>
    private static bool AirborneHolds(RynthCoreHost host, long now, out string why)
    {
        why = "";
        long jumpAt; float z0;
        lock (Gate) { jumpAt = _jumpAt; z0 = _jumpZ; }
        if (jumpAt == 0) return false;
        long air = now - jumpAt;
        bool landed = Landed(host, now, jumpAt, z0);
        if (!landed)
        {
            why = $"in the air after the stuck-cast jump ({air} ms)";
            return true;
        }
        lock (Gate)
        {
            _jumpAt = 0;
            if (_stage == BreakStage.Jumped)
            {
                if (_stageFor == null) { _stage = BreakStage.None; _stageAt = 0; }   // a forced break with no cast of ours open
                else { _stage = BreakStage.Landed; _stageAt = now; }
            }
        }
        return false;
    }

    /// <summary>The breaker jump (TapJump: the keyboard jump, 0xF61B). No TapJump: the Peace request instead.</summary>
    private static void Jump(RynthCoreHost host, PacedAction? stuck, long age, string why, bool forced)
    {
        long now = NowMs();
        string what = stuck != null ? Describe(stuck.Kind, stuck.Label) : "the server's busy action";
        if (!host.HasTapJump)
        {
            SetStage(BreakStage.ModeSent, now, stuck);
            Log(host, $"[Pace] stuck cast: no TapJump on this engine; {what}: {why}");
            PeaceBreak(host, stuck, age);
            return;
        }
        float z = 0;
        try { if (host.HasGetPlayerPose) host.TryGetPlayerPose(out _, out _, out _, out z, out _, out _, out _, out _); } catch { }
        bool ok;
        try { ok = host.TapJump(); } catch { ok = false; }
        lock (Gate)
        {
            _stuckBreaks++;
            if (forced) _forcedBreaks++;
            _stage = BreakStage.Jumped;
            _stageAt = now;
            _stageFor = stuck;
            if (ok) { _jumpAt = now; _jumpZ = z; }
        }
        Log(host, $"[Pace] stuck cast broken by jump after {age} ms: {what} - {why}{(ok ? "" : " (the jump was refused by the client)")}; nothing starts until landed and answered");
        // Refused by the client: nothing in the air. For no cast of ours, nothing to wait for either.
        if (!ok) { SetStage(stuck == null ? BreakStage.None : BreakStage.Landed, now, stuck); }
    }

    /// <summary>The Peace mode request (ACE fails a cast in progress on a mode change). Only from Magic.</summary>
    private static void PeaceBreak(RynthCoreHost host, PacedAction? stuck, long age)
    {
        int mode = -1;
        try { if (host.HasGetCurrentCombatMode) mode = host.GetCurrentCombatMode(); } catch { mode = -1; }
        string what = stuck != null ? Describe(stuck.Kind, stuck.Label) : "the server's busy action";
        if (mode == MagicMode && host.HasChangeCombatMode)
        {
            int seq = UseDoneSeqNow(host);
            bool ok;
            try { ok = host.ChangeCombatMode(PeaceMode); } catch { ok = false; }
            lock (Gate) _stuckBreaks++;
            if (ok)
            {
                PacedAction? keep;
                lock (Gate) keep = _hung;
                Begin(host, PacedKind.CombatMode, 0, ModeName(PeaceMode), "StuckCast", seq, PeaceMode);
                lock (Gate) _hung = keep;
            }
            Log(host, $"[Pace] stuck cast broken by Peace mode after {age} ms: {what} - no answer after the jump; Peace {(ok ? "requested" : "refused by the client")}");
        }
        else
        {
            Log(host, $"[Pace] stuck cast: {what} still unanswered after the jump, not in Magic mode ({ModeName(mode)}): nothing more to cancel");
        }
    }

    /// <summary>
    /// Turn to face a held cast's target (once per cast), when RynthAi is not moving the character:
    /// ACE's fast-tick cast waits for the facing to come within spellcast_max_angle.
    /// </summary>
    /// <returns>True when the turn went out.</returns>
    private static bool TurnToFace(RynthCoreHost host, PacedAction stuck, long age)
    {
        uint target = stuck.Target;
        uint player = 0;
        try { if (host.HasGetPlayerId) player = host.GetPlayerId(); } catch { }
        if (target == 0 || target == player || !host.HasTurnToHeading || MoveAudit.OwnMovementActive) return false;
        if (!TryHeadingTo(host, target, out double want, out double off)) return false;
        if (off < 10.0) return false;   // already facing it: the turn is not what holds it
        try { host.TurnToHeading((float)want); } catch { return false; }
        lock (Gate) _heldTurns++;
        Log(host, $"[Pace] held cast: {Describe(stuck.Kind, stuck.Label)} unanswered {age} ms; turning {off:0} deg to face 0x{target:X8} (heading {want:0}); no jump for {TurnGraceMs / 1000.0:0.#} s");
        return true;
    }

    /// <summary>The heading to <paramref name="target"/> (AC degrees: 0 north, 90 east) and how far off the player faces.</summary>
    internal static bool TryHeadingTo(RynthCoreHost host, uint target, out double heading, out double offDeg)
    {
        heading = 0; offDeg = 180;
        try
        {
            if (!host.HasGetPlayerPose || !host.HasGetObjectPosition) return false;
            if (!host.TryGetPlayerPose(out uint pcell, out float px, out float py, out _, out float qw, out _, out _, out float qz)) return false;
            if (!host.TryGetObjectPosition(target, out uint tcell, out float tx, out float ty, out _)) return false;
            double dx = (((tcell >> 24) & 0xFF) - (double)((pcell >> 24) & 0xFF)) * 192.0 + (tx - px);
            double dy = (((tcell >> 16) & 0xFF) - (double)((pcell >> 16) & 0xFF)) * 192.0 + (ty - py);
            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return false;
            heading = Math.Atan2(dx, dy) * (180.0 / Math.PI);
            if (heading < 0) heading += 360.0;
            double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
            double cur = ((-yaw) % 360.0 + 720.0) % 360.0;
            double e = heading - cur;
            while (e > 180) e -= 360;
            while (e < -180) e += 360;
            offDeg = Math.Abs(e);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The HP failsafe's break (BuffManager): a jump now, whatever is open (on fast tick it fails a
    /// cast in progress and clears the pending motions). False when one is already under way or
    /// this engine has no TapJump.
    /// </summary>
    public static bool ForceBreak(RynthCoreHost host, string why)
    {
        if (!Enabled || !host.HasTapJump || IsPortaling(host)) return false;
        PacedAction? stuck;
        lock (Gate)
        {
            if (_jumpAt != 0 || _stage == BreakStage.Jumped) return false;
            stuck = _hung ?? _shadow ?? (_cur is { Kind: PacedKind.Cast } ? _cur : null);
        }
        long age = stuck != null ? NowMs() - stuck.SentMs : 0;
        Jump(host, stuck, age, why, forced: true);
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>In portal space: no breaker step, no forced break (the server is moving us).</summary>
    internal static bool IsPortaling(RynthCoreHost host)
    {
        try { return host.HasIsPortaling && host.IsPortaling(); } catch { return false; }
    }

    internal static string Describe(PacedKind kind, string label) => kind switch
    {
        PacedKind.WorldUse => $"the use of {label}",
        PacedKind.ItemUse => $"the use of {label}",
        PacedKind.Cast => $"the cast of {label}",
        PacedKind.CombatMode => $"the change to {label} mode",
        _ => label,
    };

    internal static string ModeName(int mode) => mode switch
    {
        1 => "Peace", 2 => "Melee", 4 => "Missile", 8 => "Magic", _ => $"mode {mode}",
    };

    internal static bool IsWieldedByPlayer(RynthCoreHost host, uint id)
    {
        try
        {
            if (id == 0 || !host.HasGetObjectWielderInfo || !host.HasGetPlayerId) return false;
            uint player = host.GetPlayerId();
            return player != 0 && host.TryGetObjectWielderInfo(id, out uint wielder, out _) && wielder == player;
        }
        catch { return false; }
    }

    /// <summary>World object (no container, no wielder) = the server walks to it; else an item of ours.</summary>
    internal static PacedKind ClassifyUse(RynthCoreHost host, uint id, bool doorOrCorpse)
    {
        if (doorOrCorpse) return PacedKind.WorldUse;
        try
        {
            if (host.HasGetObjectOwnershipInfo && host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _))
                return container == 0 && wielder == 0 ? PacedKind.WorldUse : PacedKind.ItemUse;
        }
        catch { }
        return PacedKind.ItemUse;
    }

    internal static int UseDoneSeqNow(RynthCoreHost host)
    {
        try { return host.HasUseDoneSeq ? host.GetUseDoneSeq() : 0; } catch { return 0; }
    }

    private static void Log(RynthCoreHost host, string line)
    {
        try { host.Log(line); } catch { }
    }

    /// <summary>Lines for /ra pace.</summary>
    public static string[] DescribeLines(RynthCoreHost host)
    {
        var lines = new List<string>();
        PacedAction? a = Current(host);
        lines.Add($"Action pacing: {(Enabled ? "on" : "off")} (one server action at a time; see ops note 2026-10-06-action-pacing)");
        lines.Add(a == null
            ? "  Waiting on: nothing"
            : $"  Waiting on: {Describe(a.Kind, a.Label)} from {a.Who}, sent {NowMs() - a.SentMs} ms ago{(a.ResultSeen ? " (result in, recoil)" : "")}");
        lock (Gate)
        {
            lines.Add($"  Sent {_started}, answered {_answered}, no answer in time {_timedOut}{(_lastTimeout.Length > 0 ? $" (last: {_lastTimeout})" : "")}");
            lines.Add($"  Starts held {_heldStarts}, moves held {_heldMoves}, heals let ahead {_healsAhead}, sent over an unanswered one {_sentOverOutstanding}");
            lines.Add($"  The player's turn (key presses from other plugins): {_playerHolds}, starts held for them {_playerHeldStarts}");
            lines.Add($"  Mode changes refused while waiting {_modeRefused}, skipped (already in that mode) {_modeSkipped}");
            lines.Add($"  Held while the character was animating {_gestureHolds}, animation time limits {_gestureLimits}");
            lines.Add($"  Held after a 'too busy' answer {_refusalHolds}, held casts turned to {_heldTurns}, stuck casts broken {_stuckBreaks} ({_forcedBreaks} by the HP failsafe; limit {StuckCastMs} ms)");
            if (_maxWaitMs > 0) lines.Add($"  Longest wait seen: {_maxWaitMs} ms ({_maxWaitWhat})");
            if (_holdsByWho.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in _holdsByWho) parts.Add($"{kv.Key} {kv.Value}");
                lines.Add("  Holds by subsystem: " + string.Join(", ", parts));
            }
        }
        return lines.ToArray();
    }

    // ── The paced sends that are not uses ────────────────────────────────────

    /// <summary>
    /// CastSpell, recorded as the outstanding action. RynthAi's callers check MayStart first;
    /// <paramref name="asked"/> (a player command or meta) is never held, only recorded.
    /// </summary>
    public static bool CastSpellFor(this RynthCoreHost host, uint targetId, int spellId, string who, string label, bool asked = false)
    {
        // No cast goes out with a held stop still waiting (2026-10-12): the stop is sent first. The
        // engine runs a stop queued before a cast ahead of it, and sends its own pre-cast stop a
        // server answer ahead of the cast, so the stop never rides in the cast's frame.
        MoveAudit.FlushPendingStops(host, "before a cast", force: true);
        int seq = UseDoneSeqNow(host);
        bool ok = host.CastSpell(targetId, spellId);
        if (ok)
        {
            CastsSent++;
            LastCastSeqAtSend = host.HasUseDoneSeq ? seq : -1;
            Begin(host, PacedKind.Cast, unchecked((uint)spellId), label, who, seq);
            lock (Gate) { if (_cur is { Kind: PacedKind.Cast } c && c.Id == unchecked((uint)spellId)) c.Target = targetId; }
        }
        return ok;
    }

    /// <summary>The open cast of <paramref name="spellId"/> was chosen by a script (see <see cref="ScriptCastMinBreakMs"/>).</summary>
    public static void MarkScriptCast(int spellId)
    {
        lock (Gate) { if (_cur is { Kind: PacedKind.Cast } c && c.Id == unchecked((uint)spellId)) c.Script = true; }
    }

    /// <summary>
    /// The UseDone count read just before the last cast <see cref="CastSpellFor"/> sent (its answer is
    /// the next UseDone after it); -1 when the engine has no UseDone count.
    /// </summary>
    public static int LastCastSeqAtSend { get; private set; } = -1;

    /// <summary>
    /// Casts sent through <see cref="CastSpellFor"/> in this plugin (every caller: combat, buffs,
    /// nav, commands, meta): a caller that remembers the count at its own cast can tell whether
    /// another cast went out since (a refusal then may be that one's).
    /// </summary>
    public static long CastsSent { get; private set; }

    /// <summary>
    /// ChangeCombatMode with the pacing rules: nothing is sent when the client already reads that
    /// mode (the engine would push a raw 0x53 to the server anyway, and on ACE every mode change
    /// cancels the walk/turn of a pending use and fails a cast in progress) unless
    /// <paramref name="force"/>; and nothing while another server action is unanswered unless
    /// <paramref name="force"/>. Returns false when nothing went out: the pacing held the request back,
    /// the client already reads that mode, or the engine refused the call. True only when the request
    /// was made (or the host has no ChangeCombatMode, where there is nothing to send or pace). Callers
    /// count a try, a backoff step or a log line only on true.
    /// </summary>
    public static bool ChangeCombatModeFor(this RynthCoreHost host, int mode, string who, string why, bool force = false)
    {
        if (!host.HasChangeCombatMode) return true;   // nothing to send or pace (the callers went on before too)
        if (Enabled && !force)
        {
            int current = -1;
            try { if (host.HasGetCurrentCombatMode) current = host.GetCurrentCombatMode(); } catch { current = -1; }
            if (current == mode)
            {
                lock (Gate) _modeSkipped++;
                return false;
            }
            if (PlayerHoldsStart(host, "mode:" + who, out _)) return false;
            PacedAction? a = Current(host);
            if (a != null)
            {
                bool log;
                lock (Gate)
                {
                    _modeRefused++;
                    log = a.LoggedWaits.Add("mode:" + who);
                }
                if (log)
                    Log(host, $"[Pace] {who}: change to {ModeName(mode)} mode held ({why}): {Describe(a.Kind, a.Label)} sent {NowMs() - a.SentMs} ms ago by {a.Who} has no answer yet (a mode change now would cancel it on the server)");
                return false;
            }
            if (GestureHolds(host, "mode:" + who, HealNeed.None, out _))
            {
                lock (Gate) _modeRefused++;
                return false;
            }
        }
        int seq = UseDoneSeqNow(host);
        if (!host.ChangeCombatMode(mode))
            return NoteEngineRefusal(host, mode);
        NoteEngineAccepted(host);
        Begin(host, PacedKind.CombatMode, 0, ModeName(mode), who, seq, mode);
        return true;
    }

    // -- The engine turning a mode change away -----------------------------------------------
    // ChangeCombatMode returns false when the engine refused: it turns non-teardown actions away for
    // a few seconds around a teleport or a death, its action ring can be full, or the hook is
    // missing. Nothing was sent, so no action is outstanding and the callers must not count a try
    // or a backoff step: ChangeCombatModeFor returns false and they ask again next tick. A refusal
    // that lasts longer than EngineRefusalFailOpenMs is no longer a transition (a missing hook or a
    // stuck ring never recovers): the call then returns true again, which puts the callers back on
    // their own failure counting (NoWandFlipMaxFails and the potion fallbacks). One log line when a
    // streak starts, one when it ends; any accepted call ends it.

    /// <summary>A streak of consecutive engine refusals this long makes ChangeCombatModeFor return true again.</summary>
    public const long EngineRefusalFailOpenMs = 30_000;
    /// <summary>Refusals further apart than this are separate streaks (the streak never outlives a quiet spell).</summary>
    public const long RefuseGapMs = 10_000;
    private static long _refuseSince;     // start of the current streak of engine refusals (0 = none)
    private static long _refuseLast;      // time of the newest refusal in the streak
    private static long _lastEngineRefusalMs;
    /// <summary>NowMs of the newest engine refusal (0 = none since Reset). Stuck-stance clocks restart when this is newer than their start.</summary>
    public static long LastEngineRefusalMs { get { lock (Gate) return _lastEngineRefusalMs; } }
    private static int _refuseCount;
    private static long _engineRefusals;  // lifetime
    public static long EngineModeRefusals => _engineRefusals;

    private static bool NoteEngineRefusal(RynthCoreHost host, int mode)
    {
        bool startLine = false, failOpen;
        long now = NowMs();
        lock (Gate)
        {
            _engineRefusals++;
            if (_refuseSince != 0 && now - _refuseLast > RefuseGapMs) _refuseSince = 0;
            if (_refuseSince == 0) { _refuseSince = now; _refuseCount = 0; startLine = true; }
            _refuseLast = now; _lastEngineRefusalMs = now;
            _refuseCount++;
            failOpen = now - _refuseSince > EngineRefusalFailOpenMs;
        }
        if (startLine)
            Log(host, $"[Pace] the engine refused the change to {ModeName(mode)} mode: asking again each tick (no try counted), counted as a failure after {EngineRefusalFailOpenMs / 1000} s");
        return failOpen;
    }

    private static void NoteEngineAccepted(RynthCoreHost host)
    {
        int count; long span;
        lock (Gate)
        {
            if (_refuseSince == 0) return;
            count = _refuseCount; span = NowMs() - _refuseSince;
            _refuseSince = 0; _refuseLast = 0; _refuseCount = 0;
        }
        Log(host, $"[Pace] the engine accepts mode changes again after {count} refusal(s) over {span} ms");
    }
}
