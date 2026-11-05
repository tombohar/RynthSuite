using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// One server action at a time (2026-10-06, ops\overnight\2026-10-06-action-pacing.md).
// Drakkon's stuck uses and stuck casts on DreamWeave: every stall began with a combat-mode change
// sent while a corpse use was still unanswered, and once the server held an action RynthAi fired
// the next cast or use every 0.4-2.5 s on top of it (it paced on a busy count it kept zeroing, and
// on the enchantment rather than the server's UseDone). These tests replay those sequences against
// Plugins/Shared/ActionPacer.cs and the RynthAi code wired to it:
//  - the pacer itself: answers (UseDone, a refusal, a wield, a stance read), time limits, the heal
//    rules, and that nothing is ever force-cleared;
//  - the onset sequence (corpse use -> idle peace swap 3-677 ms later) and the movement hold;
//  - BuffManager: a cast whose result is in but whose UseDone isn't waits (one cast, not a flood),
//    heals still go first, and the emergency stance settle is unchanged;
//  - looting: one open per answer; Drakkon's stall is still found and reported once;
//  - CombatManager's idle peace swap waits for an unanswered kit;
//  - the busy mirror is reset without touching the client's count.
internal static class ActionPacingTests
{
    private const uint Player = 0x50000E01;
    private const uint Corpse = 0x80068ACE;
    private const uint CorpseB = 0x80072023;
    private const uint Kit = 0x8004BEF8;
    private const uint Wand = 0x8007467A;
    private const uint Pack = 0x50000E01;   // the player's main pack is the player
    private const uint ItemTypeContainer = 0x200;
    private const uint LongDescKey = 16;
    private const uint TooBusy = 0x1D;
    private const string Me = "Drakkon";
    private static readonly (uint Cell, float X, float Y, float Z) Me0 = (0xA9B40019u, 100f, 100f, 0f);
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static void Register(Runner r)
    {
        r.Add("pacing: a use is the server action until its UseDone; a refusal answers it too; a wield answers by wielding", UseAnswers);
        r.Add("pacing: the player's turn (RynthAi.Yield) holds every start, mode change and move, ends by itself, and lets what is out finish", PlayersTurn);
        r.Add("pacing: the stall onset replayed - the idle peace swap waits for the corpse use's answer, then goes; no swap to the mode you're in", OnsetModeChangeHeld);
        r.Add("pacing: moves and turns wait while the server answers; a stop with nothing of ours moving waits too, is never dropped; a real stop and the cast nudge go through", MovesHeld);
        r.Add("pacing (stop burst, flight 63260): a held stop goes out once the server answers, before the next start", HeldStopSentAfterAnswer);
        r.Add("pacing (stop burst): no cast goes out while a stop is held - the stop is sent first", CastWaitsForHeldStop);
        r.Add("pacing (stop burst): a stop for our own autorun goes through however long ago it was turned on", OldAutorunStopGoesThrough);
        r.Add("pacing: no answer - each kind's time limit lets the next action go, logged, and nothing is force-cleared", TimeLimits);
        r.Add("pacing: heals (a plain heal waits for a corpse use's answer, 300 ms at Emergency, one behind a cast's result, 300 ms after the stance reads)", HealRules);
        r.Add("pacing: the first swing goes 300 ms after the client reads Melee, not the full stance settle; a use still waits for it", FirstSwingAfterStance);
        r.Add("pacing (vitals): Drakkon 07:03 - a cast whose result is in waits for its UseDone: one cast, not a too-busy flood", CastWaitsForUseDone);
        r.Add("refusal: a refused mode change begins no paced action, and the next one is not held by it", RefusedBeginsNothing);
        r.Add("refusal: the engine refusing for 5 s burns nothing; a streak past 30 s counts as a failure again, with one log line each end", RefusalStreakFailsOpen);
        r.Add("refusal: 20 s of refusals with no wand never reach _magicModeBlocked; past 30 s the failure counting resumes and the vitals fall to the potion path", NoWandRefusalLoop);
        r.Add("refusal: wand wielded, 9 s of refused stance changes - no re-equip UseObject, no burnt re-equip; the first accepted request starts the clock", WandWieldedRefusalNoReEquip);
        r.Add("refusal: a refused peace-before-open is asked again inside the 2.5 s ceiling; exactly one accepted request goes out before the open", PeaceBeforeOpenRefusedRetried);
        r.Add("refusal: a streak that went quiet for 10 s+ (a death, a logout) is over - 600 s later a refusal starts a fresh streak, and Forget ends one", RefusalStreakExpires);
        r.Add("refusal: wand wielded, a request accepted then dropped (a teleport), 9 s of refusals - the stuck clock restarts, no re-equip UseObject", StuckClockRestartsOnRefusal);
        r.Add("stance clock (Drakkon 10-23): Magic seen ends the episode; a request a minute after the last starts a new one (no 'stuck 1123s')", StuckClockEndsInMagicAndAfterAGap);
        r.Add("refusal: the stall watchdog's refused mode toggle spends no ladder step (Unfire) and is asked again next tick", StallToggleRefusedSpendsNoStep);
        r.Add("refusal: missile crafting asks for peace until accepted, and combines after the 2.5 s ceiling; already in peace it combines on the first tick with no mode request", CraftingPeaceRefusal);
        r.Add("pacing (stance): a refused ChangeCombatMode burns no try and no backoff step, and the next tick asks again (pacing on and off)", RefusedStanceBurnsNothing);
        r.Add("corpse id: a refused RequestId is not recorded as asked - the next evaluation asks again and the accepted one goes out", CorpseIdRefusedRetried);
        r.Add("corpse id: an accepted request with no data is asked again after 3 s (not before), once per 3 s, at most 3 re-asks per corpse", CorpseIdReaskedWithoutData);
        r.Add("corpse id: Loot From All Corpses needs no killer name - the first request goes out, no re-ask ever follows; My Kills Only gives up after 3 re-asks, a new corpse starts over", CorpseIdAllCorpsesNoReask);
        r.Add("corpse id: two corpses in range, no data for either - only the targeted one re-asks; the other stays at one accepted request (a refused one is still retried)", CorpseIdOnlyCandidateReasks);
        r.Add("corpse id: 20 uncached corpses, a refusal window, then accept - at most 2 requests in the first accepted tick, the target among them; refused retries are 250 ms apart", CorpseIdBurstCapped);
        r.Add("corpse id: the open container is a candidate and re-asks; delete and the >200 prune clear every id map", CorpseIdOpenContainerAndCleanup);
        r.Add("pacing (vitals): a heal waits for an unanswered corpse use's answer, then goes at once; the emergency stance settle is unchanged", HealLatencyKept);
        r.Add("pacing (looting): one open per server answer, never re-sent on top of an unanswered one", LootOneOpenPerAnswer);
        r.Add("pacing (looting): Drakkon's stall is still found and said once with pacing on", LootStallStillDetected);
        r.Add("pacing (combat): the idle peace swap waits while a kit is unanswered", CombatPeaceWaitsForKit);
        r.Add("pacing (arbiter): the activity-change StopCompletely only when RynthAi itself has the character moving", ArbiterStopOnlyWhenMoving);
        r.Add("busy mirror: follows the engine's count (1.5 s), never reset while the engine says busy; old engines: the mirror only; never a force-clear", MirrorResetOnly);
        r.Add("motion gate (14:29): a kit waits for the swing's animation; the corpse open waits for the kit's answer and then its animation", MotionGate1429);
        r.Add("pacing (motion): after a swing is sent, nothing else starts until it has played (16:45 wand on a swing)", SwingNotStarted);
        r.Add("motion gate (13:58): wand wield -> the cast and the Magic stance wait until the wield's animation is over", MotionGate1358);
        r.Add("motion gate (morning): corpse use -> the idle peace swap waits for the UseDone and then the turn's animation", MotionGateMorning);
        r.Add("motion gate: 4 s of 'animating' is taken as stuck (logged once, open until it reads clear); an emergency heal waits 1.5 s at most", MotionGateLimits);
        r.Add("motion gate: melee/missile attack repeats are not held by the animation, peace/magic combat starts are; off with the pacing", MotionGateAttackExempt);
        r.Add("motion gate (looting): a corpse open waits while the character animates, then goes", MotionGateLooting);
        r.Add("motion gate (items): the item managers' busy count includes the animation", MotionGateItems);
    }

    // ── Pacer rig ───────────────────────────────────────────────────────────────

    private static long _t;

    /// <summary>Turns the pacing on with RynthAi's use recorder and a hand-moved clock.</summary>
    private static void PaceOn()
    {
        _t = 10_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _t;
        UseAudit.Reset("RynthAi", () => true);
        UseAudit.NowMs = () => _t;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _t;
        var rec = typeof(RynthAiPlugin).GetMethod("RecordUseForPacing", Any)!;
        UseAudit.AfterSend = (h, u) => rec.Invoke(null, new object[] { h, u });
    }

    private static void PaceOff()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        UseAudit.Reset("?", null);
        UseAudit.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
    }

    private static void Paced(Action body)
    {
        PaceOn();
        try { body(); }
        finally { PaceOff(); }
    }

    private static RynthCoreHost World(int combatMode = 2)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;      // UseObject, GetCurrentCombatMode
        FakeHost.SalvageCalls = true;     // GetObjectWielderInfo
        FakeHost.ServerGates = true;      // GetUseDoneSeq
        FakeHost.LastUseDoneCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.BusyCalls = true;
        FakeHost.CombatModeValue = combatMode;
        FakeHost.Names[Player] = Me;
        FakeHost.Names[Corpse] = "Corpse of Olthoi Swarm Worker";
        FakeHost.Names[Kit] = "Peerless Healing Kit";
        FakeHost.Names[Wand] = "Academy Wand";
        FakeHost.Bitfields[Corpse] = 0x2000;
        FakeHost.Containers[Kit] = Pack;
        FakeHost.Containers[Wand] = Pack;
        return FakeHost.Create(Player);
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));

    // ── The pacer ───────────────────────────────────────────────────────────────

    private static void PlayersTurn()
    {
        PaceOff();
        var off = World();
        Check.False(ActionPacer.HoldForPlayer(off, 1500), "pacing off: RynthAi can't hold (it doesn't know what it has in flight)");
        Check.True(ActionPacer.MayStart(off, "Combat", HealNeed.None, out _), "and nothing is held");

        Paced(() =>
        {
            var host = World();
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "free before the ask");
            Check.True(ActionPacer.HoldForPlayer(host, 1500), "the ask is taken");
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out string why), "a buff cast waits");
            Check.True(why.Contains("key press"), "and says why: " + why);
            Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.Emergency, out _), "an emergency heal waits too (the press is the player's choice)");
            Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true), "an attack repeat waits");
            Check.True(ActionPacer.GestureHolds(host, "Loot", HealNeed.None, out _), "the item managers' moves wait");
            FakeHost.CombatModeRequests.Clear();
            Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand"), "a stance change is held");
            Check.Eq(FakeHost.CombatModeRequests.Count, 0, "and not sent");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("the player's turn: a key press")), 1, "the hold logged once");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Pace] Buff waits: the player's key press goes first")), 1, "each subsystem's wait logged once");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Pace] mode:Buff waits: the player's key press")), 1, "the held stance change its own line");
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "asked again: still held");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Pace] Buff waits: the player's key press")), 1, "not logged again");

            // Renewed: the newest ask sets the end.
            _t += 1000;
            Check.True(ActionPacer.HoldForPlayer(host, 1500), "asked again a second later");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("the player's turn: a key press")), 1, "a renewal is the same hold");
            _t += 1400;
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "2.4 s in, 1.4 s after the renewal: held");
            _t += 200;
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "the asker stopped asking: the hold ends by itself");
            Check.Eq(ActionPacer.PlayerHolds, 1L, "one hold counted");

            // Capped at PlayerHoldMaxMs whatever is asked.
            Check.True(ActionPacer.HoldForPlayer(host, 60_000), "a long ask");
            _t += ActionPacer.PlayerHoldMaxMs - 1;
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "held up to the cap");
            _t += 2;
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "free past it");

            // Released early: free at once, logged.
            Check.True(ActionPacer.HoldForPlayer(host, 1500), "a third hold");
            ActionPacer.ReleasePlayerHold(host);
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "released: free at once");
            Check.True(Logged("the player's turn is over"), "the release logged");
            int lines = FakeHost.Logs.Count;
            ActionPacer.ReleasePlayerHold(host);
            Check.Eq(FakeHost.Logs.Count, lines, "a second release says nothing");

            // What is already out finishes: a hold doesn't touch the outstanding action.
            Check.True(host.UseFor(Corpse, "Loot", "open the claimed corpse"), "a corpse open goes out");
            Check.True(ActionPacer.HoldForPlayer(host, 1500), "then the press takes the turn");
            Check.True(ActionPacer.Current(host) != null, "the corpse open is still RynthAi's outstanding action");
        });
    }

    private static void UseAnswers() => Paced(() =>
    {
        var host = World();
        Check.True(host.UseFor(Corpse, "Loot", "open the claimed corpse"), "the open goes out");
        var a = ActionPacer.Current(host);
        Check.True(a != null && a.Kind == PacedKind.WorldUse, "a corpse is a world use the server walks to");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out string why), "nothing else starts meanwhile");
        Check.True(why.Contains("Corpse of Olthoi Swarm Worker") && why.Contains("RynthAi/Loot"), "why names the corpse and the sender: " + why);
        _t += 690;   // the healthy median answer
        Check.True(ActionPacer.Current(host) != null, "still unanswered at 690 ms");
        FakeHost.UseDone(0);
        Check.True(ActionPacer.Current(host) == null, "its UseDone answers it");
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "the next action may go");

        // A kit on yourself is an item use; "You're too busy" (0x1D) is an answer as well.
        Check.True(host.UseOnFor(Kit, Player, "Buff", "vitals: healing kit on yourself"), "the kit goes out");
        Check.True(ActionPacer.Current(host)?.Kind == PacedKind.ItemUse, "a kit on yourself is an item use");
        FakeHost.UseDone(TooBusy);
        Check.True(ActionPacer.Current(host) == null, "a refusal answers it (the server is busy with something else; the caller backs off)");

        // A wield gets no UseDone at all (0 of 18 wand wields on 10-05/10-06): it answers by wielding.
        Check.True(host.UseFor(Wand, "Buff", "buffing: wield the wand"), "the wield goes out");
        Check.True(ActionPacer.Current(host)?.Kind == PacedKind.ItemUse, "an item use");
        _t += 400;
        Check.True(ActionPacer.Current(host) != null, "not wielded yet: waiting");
        FakeHost.Wielded[Wand] = 0x1000000;
        Check.True(ActionPacer.Current(host) != null, "wielded: a short settle first");
        _t += ActionPacer.WieldSettleMs;
        Check.True(ActionPacer.Current(host) == null, "wielded and settled: answered");
        Check.Eq(ActionPacer.TimedOut, 0L, "no time limit was needed");
    });

    private static void OnsetModeChangeHeld() => Paced(() =>
    {
        // 06:18:44.549 corpse use in melee stance, 06:18:44.552 RequestCombatMode(peace).
        var host = World(combatMode: 2);
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        _t += 3;
        Check.False(host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle: no target"), "3 ms later: the peace swap is held");
        _t += 674;
        Check.False(host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle: no target"), "677 ms later (10-05 13:18:31): still held");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no mode change reached the server while the use was unanswered");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("change to Peace mode held")), 1, "logged once");

        FakeHost.UseDone(0);
        Check.True(host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle: no target"), "answered: the swap goes out");
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "one request, to peace");
        Check.True(ActionPacer.Current(host)?.Kind == PacedKind.CombatMode, "the stance change is now the action the server is answering");

        // The next corpse waits for the stance: the client reads peace, then the animation.
        FakeHost.CombatModeValue = 1;
        _t += 500;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "500 ms: the stance animation is still running");
        _t += ActionPacer.ModeSettleMs - 500;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "800 ms: settled, the next open may go");

        // No request for the mode the client already reads (the engine pushed a raw 0x53 anyway).
        FakeHost.CombatModeRequests.Clear();
        Check.False(host.ChangeCombatModeFor(CombatMode.NonCombat, "Loot/Wedge", "loot wedge reset"), "already in peace: nothing to send");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "nothing sent");
        Check.True(host.ChangeCombatModeFor(CombatMode.NonCombat, "Command", "/ra panic", force: true), "a typed command forces it");
        Check.Eq(FakeHost.CombatModeRequests.Count, 1, "forced: sent");
    });

    private static void MovesHeld() => Paced(() =>
    {
        var host = World();
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        Check.False(host.SetAutoRunBy("Nav", true), "nav can't start running");
        Check.False(host.TurnToHeadingBy("Nav", 90f), "nor turn");
        Check.False(host.SetMotionBy("Combat", 0x6500000D, true), "nor start a turn motion");
        Check.False(host.StopCompletelyBy("Arbiter"), "a stop with nothing of ours moving waits for the answer");
        Check.False(host.SetMotionBy("Combat", 0x6500000D, false), "Combat's turn-off with no turn on: waits too");
        Check.True(MoveAudit.HasPendingStop, "the stops are kept, not dropped");
        Check.Eq(FakeHost.AutoRunCalls.Count, 0, "no autorun reached the client");
        Check.Eq(FakeHost.Stops, 0, "no stop yet either");
        Check.True(Logged("held (move/turn): the server is still answering the use of"), "the hold is logged");
        Check.True(Logged("StopCompletely() waits (nothing of ours is moving; sent once the server has answered, before any cast)"), "the wait is logged");

        Check.True(host.SetAutoRunBy("Buff/Nudge", true) && host.SetAutoRunBy("Buff/Nudge", false), "the held-cast nudge is deliberate: it goes through");
        Check.True(host.StopCompletelyBy("MacroStop"), "a macro stop goes through");
        Check.False(MoveAudit.HasPendingStop, "the macro StopCompletely covered the held ones");

        FakeHost.UseDone(0);
        Check.True(host.SetAutoRunBy("Nav", true), "answered: nav runs again");
        host.UseFor(CorpseB, "Loot", "open the claimed corpse");
        FakeHost.Names[CorpseB] = "Corpse of Olthoi Swarm Drone";
        Check.True(host.StopCompletelyBy("Arbiter"), "while we really run, a stop always goes through");
        Check.False(MoveAudit.OwnMovementActive, "and we are not moving any more");
    });

    private static void HeldStopSentAfterAnswer() => Paced(() =>
    {
        World();
        FakeHost.SetMotionCalls = true;
        var host = FakeHost.Create(Player);
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        Check.False(host.SetAutoRunBy("Nav", false), "nav's stop with nothing of ours moving waits");
        Check.False(host.SetMotionBy("Nav", 0x6500000D, false), "and its turn-off");
        Check.False(MoveAudit.FlushPendingStops(host, "test"), "nothing is sent while the use is unanswered");
        Check.Eq(FakeHost.Sent.Count, 0, "nothing reached the client");
        FakeHost.UseDone(0);
        Check.True(MoveAudit.FlushPendingStops(host, "once the server answered"), "answered: the held stops go out (RynthAi's tick)");
        Check.True(FakeHost.Sent.SequenceEqual(new[] { "autorun off", "motion 0x6500000D off" }), $"autorun off, then the turn-off ({string.Join(", ", FakeHost.Sent)})");
        Check.True(Logged("held stop(s) sent once the server answered"), "logged");
        Check.False(MoveAudit.HasPendingStop, "nothing left");

        // A start that goes through sends any held stop first, so the order is kept.
        host.UseFor(CorpseB, "Loot", "open");
        FakeHost.Names[CorpseB] = "Corpse of Olthoi Swarm Drone";
        host.StopCompletelyBy("Arbiter");
        FakeHost.UseDone(0);
        FakeHost.Sent.Clear();
        Check.True(host.SetAutoRunBy("Nav", true), "nav runs again");
        Check.True(FakeHost.Sent.SequenceEqual(new[] { "stop", "autorun on" }), $"the held stop, then the start ({string.Join(", ", FakeHost.Sent)})");
    });

    private static void CastWaitsForHeldStop() => Paced(() =>
    {
        var host = World();
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        Check.False(host.StopCompletelyBy("Arbiter"), "held while the use is unanswered");
        // A player command (asked) casts over the unanswered use: the stop still goes first.
        host.CastSpellFor(Corpse, 5367, "Command", "spell 5367 (/ra cast)", asked: true);
        Check.True(FakeHost.Sent.SequenceEqual(new[] { "stop", "cast 5367" }), $"stop, then the cast ({string.Join(", ", FakeHost.Sent)})");
        Check.False(MoveAudit.HasPendingStop, "nothing held after the cast");
        Check.True(Logged("held stop(s) sent before a cast"), "logged");
    });

    private static void OldAutorunStopGoesThrough() => Paced(() =>
    {
        var host = World();
        Check.True(host.SetAutoRunBy("Nav", true), "nav runs");
        _t += 10_000;   // nav's later autorun re-sends were held, or it simply ran on: well past the 3 s "fresh" window
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        Check.True(host.SetAutoRunBy("Nav", false), "our autorun is still on in AC: the stop goes through at once");
        Check.True(FakeHost.AutoRunCalls.SequenceEqual(new[] { true, false }), "on, then off reached the client");
        Check.False(MoveAudit.HasPendingStop, "nothing held");
    });

    private static void TimeLimits() => Paced(() =>
    {
        var host = World();
        void Expect(PacedKind kind, long limit, Action send)
        {
            long before = ActionPacer.TimedOut;
            send();
            Check.True(ActionPacer.Current(host)?.Kind == kind, $"{kind}: outstanding");
            _t += limit - 1;
            Check.True(ActionPacer.Current(host) != null, $"{kind}: still waiting 1 ms before {limit} ms");
            _t += 1;
            Check.True(ActionPacer.Current(host) == null, $"{kind}: the time limit lets the next action go");
            Check.Eq(ActionPacer.TimedOut, before + 1, $"{kind}: counted");
        }
        Expect(PacedKind.WorldUse, ActionPacer.WorldUseMaxMs, () => host.UseFor(Corpse, "Loot", "open"));
        Expect(PacedKind.ItemUse, ActionPacer.ItemUseMaxMs, () => host.UseOnFor(Kit, Player, "Buff", "kit"));
        Expect(PacedKind.Cast, ActionPacer.CastMaxMs, () => host.CastSpellFor(Player, 1426, "Buff", "Focus Self VI"));
        FakeHost.CombatModeValue = 2;
        Expect(PacedKind.CombatMode, ActionPacer.ModeMaxMs, () => host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand"));
        Check.True(Logged("no answer from the server to the cast of Focus Self VI") && Logged("nothing cleared"), "each is logged, and says nothing was cleared");
        Check.Eq(FakeHost.ForceResets, 0, "the client's busy count was never force-cleared");
    });

    private static void HealRules() => Paced(() =>
    {
        var host = World();
        host.UseFor(Corpse, "Loot", "open");
        _t += 900;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "a heal 900 ms behind an unanswered corpse use waits");
        _t += 3000;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _),
            "a plain heal still waits at 3.9 s: on ACE any motion during the server's turn to the corpse stops that turn for good");
        FakeHost.UseDone(0);
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "the use is answered: the heal goes");

        host.UseFor(Corpse, "Loot", "open");
        _t += 299;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "at Emergency Heal At: not before 300 ms");
        _t += 1;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "at Emergency Heal At: 300 ms");
        Check.True(Logged("the heal goes ahead of the use of"), "logged");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "anything else still waits");

        FakeHost.UseDone(0);
        host.CastSpellFor(Player, 1426, "Buff", "Focus Self VI");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "a cast before its result: the heal waits (the server would refuse it)");
        ActionPacer.NoteCastResult();
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "its result is in: one heal may queue behind the recoil");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "only one");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "a buff waits for the UseDone");

        FakeHost.UseDone(0);
        FakeHost.CombatModeValue = 2;
        host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand wielded: Magic mode");
        _t += 60;
        FakeHost.CombatModeValue = 8;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "the client reads Magic: not yet");
        _t += 299;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "299 ms after the read: not yet");
        _t += 1;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "300 ms after the read: the heal goes");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "a buff waits for the full 800 ms settle");
    });

    // 2026-10-14: every fight's first swing came 0.8 s after the stance change (ModeSettleMs).
    private static void FirstSwingAfterStance() => Paced(() =>
    {
        var host = World(combatMode: 1);
        host.ChangeCombatModeFor(CombatMode.Melee, "Combat", "target: Melee mode");
        _t += 60;
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, attackRepeat: true), "the client hasn't read Melee: no swing");
        FakeHost.CombatModeValue = 2;
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, attackRepeat: true), "Melee just read: not yet");
        _t += 299;
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, attackRepeat: true), "299 ms after the read: not yet");
        _t += 1;
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true),
            "300 ms after the read: the swing goes");
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "a use still waits for the full 800 ms settle");
        _t += 500;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "settled: the use goes");
    });

    // ── BuffManager ──────────────────────────────────────────────────────────────

    private sealed class BuffRig
    {
        public DateTime T0 = new(2026, 10, 6, 7, 3, 23);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public RynthCoreHost Host;

        public BuffRig(int combatMode = CombatMode.Magic)
        {
            Host = World(combatMode);
            FakeHost.Containers.Clear();
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, 33)] = (400, 2);
            Host = FakeHost.Create(Player);
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false,
                EnableBuffing = false, SpellCastIntervalMs = 0,
            };
            var skills = new CharacterSkills(Host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(Host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 10 };
            B = new BuffManager(Host, S, sm, V);
            B.SetCharacterSkills(skills);
            NowAt = T0;
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();
            _t = 10_000_000;
            ActionPacer.NowMs = () => _t + (long)(NowAt - T0).TotalMilliseconds;
            MoveAudit.NowMs = ActionPacer.NowMs;
        }

        public void At(double seconds) => NowAt = T0.AddSeconds(seconds);
        public void Chat(string text, int type = 0) => B.OnChatWindowText(text, type);
        public string CastName => SpellTable.ById(FakeHost.Casts.Last().SpellId)?.Name ?? "";
    }

    private static void CastWaitsForUseDone()
    {
        // Without the pacing (as before): the result line ends the cast, and with mana still low the
        // next Stamina to Mana goes straight out - on a server holding the first one, "too busy".
        int Script(bool pacing)
        {
            PaceOn();
            ActionPacer.Enabled = pacing;
            try
            {
                var r = new BuffRig();
                r.At(0); r.B.OnHeartbeat();
                Check.Eq(FakeHost.Casts.Count, 1, $"pacing={pacing}: Stamina to Mana goes out");
                string name = r.CastName;
                r.At(0.1); r.Chat("You say, \"Puish Zharil\"", 17);
                // 07:03:26.799: the result is in 3.3 s later; the server's UseDone never comes.
                r.At(2.3); r.Chat($"You cast {name} on yourself and lose 35 points of stamina and also gain 47 points of mana");
                for (double s = 2.4; s < 4.0; s += 0.1) { r.At(s); r.B.OnHeartbeat(); }
                int before = FakeHost.Casts.Count;
                if (pacing)
                {
                    Check.True(r.B.PacerHolds > 0, "the vitals step waited for the server's answer");
                    FakeHost.UseDone(0);
                    r.At(4.0); r.B.OnHeartbeat();
                    Check.Eq(FakeHost.Casts.Count, 2, "the UseDone arrives: the next cast goes at once");
                }
                return before;
            }
            finally { PaceOff(); }
        }
        int unpaced = Script(pacing: false);
        int paced = Script(pacing: true);
        Check.True(unpaced >= 2, $"without pacing a second cast went out before the server answered ({unpaced} casts by 4 s)");
        Check.Eq(paced, 1, "with pacing: one cast until the server answers it");
    }

    private static void HealLatencyKept()
    {
        PaceOn();
        try
        {
            // A heal behind a corpse use the server walks/turns to: it waits for the use's answer (a cast
            // during the server's turn stops the turn for good on ACE), then goes at once.
            var r = new BuffRig();
            r.S.HealAt = 60; r.S.TopOffHP = 60; r.S.GetManaAt = 0; r.S.TopOffMana = 0;
            r.V.CurrentHealth = 50; r.V.CurrentMana = 100;
            r.At(0);
            r.Host.UseFor(Corpse, "Loot", "open the claimed corpse");
            r.B.BusyCount = 1;   // the open's +1 on the client's count, as the engine reports it
            for (double s = 0; s < 1.45; s += 0.1) { r.At(s); r.B.OnHeartbeat(); }
            Check.Eq(FakeHost.Casts.Count, 0, "behind the unanswered open: no heal");
            FakeHost.UseDone(0);
            r.B.BusyCount = 0;   // the engine's UseDone decrement
            r.At(1.5); r.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 1, "the open is answered: the heal goes on the next heartbeat");
            string healName = SpellTable.ById(FakeHost.Casts[0].SpellId)?.Name ?? "";
            Check.True(healName.StartsWith("Heal Self") || healName == "Adja's Intervention", "and it is the heal spell: " + healName);

            // Nothing outstanding: the heal goes on the first heartbeat, as before.
            PaceOff(); PaceOn();
            var q = new BuffRig();
            q.S.HealAt = 60; q.S.TopOffHP = 60; q.S.GetManaAt = 0; q.S.TopOffMana = 0;
            q.V.CurrentHealth = 50; q.V.CurrentMana = 100;
            q.At(0); q.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 1, "nothing outstanding: Heal Self on the first heartbeat");

            // The emergency settle after a stance change (CastSafetyTests.EmergencyStanceWait) with the
            // pacing on: the stance change is the outstanding action, and the heal still goes 300 ms
            // after the client reads Magic.
            PaceOff(); PaceOn();
            var e = new BuffRig(CombatMode.NonCombat);
            e.S.EmergencyHealAt = 30; e.S.HealAt = 60; e.S.TopOffHP = 60; e.S.GetManaAt = 0; e.S.TopOffMana = 0;
            e.V.CurrentHealth = 20; e.V.CurrentMana = 100;
            e.At(0); e.B.OnHeartbeat();
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", "out of Magic: the stance request goes out");
            Check.Eq(FakeHost.Casts.Count, 0, "no cast before the stance");
            FakeHost.CombatModeValue = CombatMode.Magic;
            e.At(0.06); e.B.OnHeartbeat();
            e.At(0.30); e.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 0, "240 ms after Magic: still settling");
            e.At(0.37); e.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count, 1, "310 ms after Magic (370 ms after the request): Heal Self goes, as without pacing");
        }
        finally { PaceOff(); }
    }

    private static void RefusedStanceBurnsNothing()
    {
        foreach (bool pacing in new[] { true, false })
        {
            PaceOff();
            if (pacing) PaceOn();
            try
            {
                var r = new BuffRig(CombatMode.NonCombat);
                r.S.EmergencyHealAt = 30; r.S.HealAt = 60; r.S.TopOffHP = 60; r.S.GetManaAt = 0; r.S.TopOffMana = 0;
                r.V.CurrentHealth = 20; r.V.CurrentMana = 100;
                var fails = typeof(BuffManager).GetField("_buffStanceConsecutiveFails", Any)!;
                var last = typeof(BuffManager).GetField("_lastBuffStanceAttempt", Any)!;
                DateTime lastBefore = (DateTime)last.GetValue(r.B)!;
                FakeHost.CombatModeRefuse = true;   // the engine turns the stance change away (a teleport, a death)

                for (int i = 0; i < 6; i++) { r.At(i * 0.2); r.B.OnHeartbeat(); }
                Check.Eq(FakeHost.CombatModeRefusals, 6, $"pacing={pacing}: every tick asks again - a refusal is no try and sets no backoff");
                Check.Eq((int)fails.GetValue(r.B)!, 0, $"pacing={pacing}: _buffStanceConsecutiveFails unchanged");
                Check.True((DateTime)last.GetValue(r.B)! == lastBefore, $"pacing={pacing}: _lastBuffStanceAttempt unchanged");
                Check.Eq(FakeHost.CombatModeRequests.Count, 0, $"pacing={pacing}: nothing reached the server");

                FakeHost.CombatModeRefuse = false;
                r.At(1.4); r.B.OnHeartbeat();
                Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", $"pacing={pacing}: the engine accepts again and the very next tick sends Magic");
                Check.Eq((int)fails.GetValue(r.B)!, 1, $"pacing={pacing}: only a sent request counts as a try");
            }
            finally { PaceOff(); }
        }
    }

    private static void RefusedBeginsNothing() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.CombatModeRefuse = true;
        Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "refused"), "the engine refused: false");
        Check.True(ActionPacer.Current(host) == null, "no paced action was begun");
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "so nothing is held behind it");
        FakeHost.CombatModeRefuse = false;
        Check.True(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "accepted"), "accepted: true");
        Check.True(ActionPacer.Current(host)?.Kind == PacedKind.CombatMode, "and now the stance change is outstanding");
    });

    private static void RefusalStreakFailsOpen() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.CombatModeRefuse = true;
        for (int s = 0; s <= 5; s++) { _t = 10_000_000 + s * 1000; Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), $"{s} s into a refusal streak: false"); }
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("the engine refused the change")), 1, "one line when the streak starts");
        FakeHost.CombatModeRefuse = false;
        _t = 10_000_000 + 6_000;
        Check.True(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), "accepted");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("the engine accepts mode changes again")), 1, "one line when it ends");
        FakeHost.UseDone(0); ActionPacer.Reset(); ActionPacer.Enabled = true; ActionPacer.NowMs = () => _t;

        FakeHost.CombatModeRefuse = true;
        long start = _t + 1000;
        bool sawOpen = false;
        for (int s = 0; s <= 40; s++)
        {
            _t = start + s * 1000L;
            bool r = host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x");
            if (s <= 30) Check.False(r, $"{s} s into the streak: still false");
            else sawOpen |= r;
        }
        Check.True(sawOpen, "past 30 s a refusal counts as a failure again (true)");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("the engine refused the change")), 2, "the second streak logged once");
        FakeHost.CombatModeRefuse = false;
        _t += 1000;
        Check.True(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), "any accepted call ends the streak");
        FakeHost.CombatModeRefuse = true;
        _t += 5000; ActionPacer.Reset(); ActionPacer.Enabled = true; ActionPacer.NowMs = () => _t;
        Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), "a fresh streak starts at false again");
    });

    private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void SetupBuffRigVitals(BuffRig r)
    {
        r.S.EmergencyHealAt = 30; r.S.HealAt = 60; r.S.TopOffHP = 60; r.S.GetManaAt = 0; r.S.TopOffMana = 0;
        r.V.CurrentHealth = 20; r.V.CurrentMana = 100;
    }

    private static void NoWandRefusalLoop()
    {
        PaceOff();
        try
        {
            var r = new BuffRig(CombatMode.NonCombat);
            SetupBuffRigVitals(r);
            var blocked = typeof(BuffManager).GetField("_magicModeBlocked", Priv)!;
            var fails = typeof(BuffManager).GetField("_buffStanceConsecutiveFails", Priv)!;
            var swapUntil = typeof(BuffManager).GetField("_vitalSwapBlockedUntil", Priv)!;
            FakeHost.CombatModeRefuse = true;
            bool everBlocked = false;
            int ticks = 0;
            for (double s = 0; s <= 20; s += 0.5) { r.At(s); r.B.OnHeartbeat(); ticks++; everBlocked |= (bool)blocked.GetValue(r.B)!; }
            Check.True(FakeHost.CombatModeRefusals >= ticks / 2, $"20 s: the stance is asked again throughout ({FakeHost.CombatModeRefusals} refusals in {ticks} heartbeats; other vitals gates skip some ticks)");
            Check.False(everBlocked, "_magicModeBlocked never set by refusals");
            Check.Eq((int)fails.GetValue(r.B)!, 0, "no failure counted");
            Check.True((DateTime)swapUntil.GetValue(r.B)! < DateTime.Now, "and the vitals did not fall through to the potion path");

            // Past 30 s of refusals the call counts again. The backoff gates run on the real clock, so
            // stand in for three earlier counted tries: the very next counted try (the first past 30 s)
            // is the fourth, and the one after it finds the flip blocked and hands over to the potion.
            fails.SetValue(r.B, 3);
            for (double s = 20.5; s <= 40; s += 0.5) { r.At(s); r.B.OnHeartbeat(); everBlocked |= (bool)blocked.GetValue(r.B)!; }
            Check.True(everBlocked, "40 s: the failure counting resumed and _magicModeBlocked was reached");
            Check.True((DateTime)swapUntil.GetValue(r.B)! > DateTime.Now, "the vitals chain reached the potion/kit fallback (swap blocked, retry later)");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("the engine refused the change")), 1, "one streak, one line");
        }
        finally { PaceOff(); }
    }

    private static void WandWieldedRefusalNoReEquip()
    {
        PaceOff();
        try
        {
            var r = new BuffRig(CombatMode.NonCombat);
            SetupBuffRigVitals(r);
            FakeHost.Names[Wand] = "Academy Wand";
            FakeHost.ItemTypes[Wand] = 0x8000;
            FakeHost.Ints[(Wand, 10)] = 0x01000000;   // CurrentWieldedLocation: in hand
            r.S.WieldUnlistedWandWhenNoneListed = true;
            r.B.SetWorldObjectCache(FakeHost.MakeCache(r.Host, Player, new[] { Wand }));
            var reEquips = typeof(BuffManager).GetField("_buffStanceReEquips", Priv)!;
            var since = typeof(BuffManager).GetField("_buffStanceStuckSince", Priv)!;
            FakeHost.CombatModeRefuse = true;
            r.At(0); r.B.OnHeartbeat();
            Check.True(FakeHost.CombatModeRefusals >= 1, "set-up: the wielded-wand branch asked for the stance and was refused");
            Check.True((DateTime)since.GetValue(r.B)! == DateTime.MinValue, "the stuck clock has not started");
            System.Threading.Thread.Sleep(9_000);   // BuffStanceStuckRecoverMs (8 s) runs on the real clock
            r.At(9); r.B.OnHeartbeat();
            r.At(9.3); r.B.OnHeartbeat();
            Check.Eq((int)reEquips.GetValue(r.B)!, 0, "9 s of refusals: _buffStanceReEquips stays 0");
            Check.False(FakeHost.Uses.Contains(Wand), "and no UseObject(wand) - a MOVE on a wielded wand - went out");
            Check.True((DateTime)since.GetValue(r.B)! == DateTime.MinValue, "the clock still has not started");

            FakeHost.CombatModeRefuse = false;
            r.At(9.6); r.B.OnHeartbeat();
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", "accepted: the Magic request goes out");
            Check.True((DateTime)since.GetValue(r.B)! != DateTime.MinValue, "and only now the stuck clock starts");
            Check.Eq((int)reEquips.GetValue(r.B)!, 0, "still no re-equip");
            Check.False(FakeHost.Uses.Contains(Wand), "still no UseObject(wand)");
        }
        finally { PaceOff(); }
    }

    private static void PeaceBeforeOpenRefusedRetried()
    {
        LootWorld();
        FakeHost.CombatModeCalls = true;
        FakeHost.CombatModeValue = CombatMode.Melee;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        var cache = bot.Get<WorldObjectCache>("_objectCache");
        var cm = new CombatManager(host, bot.S, cache);
        cm.SetPlayerId(Player);
        bot.Set("_combatManager", cm);
        bot.Set("_targetCorpseId", unchecked((int)Corpse));
        bot.S.PeaceModeWhenIdle = true;
        bool Wants(long now) => (bool)bot.Call("WantsPeaceBeforeOpen", bot.S, now)!;
        FakeHost.CombatModeRefuse = true;
        Check.True(Wants(1_000), "first ask (refused): the open waits");
        Check.True(Wants(1_500), "asked again (refused): still waiting");
        Check.Eq(FakeHost.CombatModeRefusals, 2, "both asks reached the engine: the refusal did not use up the one shot");
        FakeHost.CombatModeRefuse = false;
        Check.True(Wants(2_000), "accepted within the ceiling: waiting for the swap");
        Check.True(Wants(2_300), "waiting");
        Check.True(Wants(3_000), "waiting");
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "exactly one peace request went out");
        Check.False(Wants(3_600), "the ceiling counts from the FIRST ask (1000 + 2500): the open goes on");
        Check.Eq(FakeHost.CombatModeRequests.Count, 1, "and still exactly one");
    }
    private static void RefusalStreakExpires() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.CombatModeRefuse = true;
        Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), "t0: a refusal opens a streak (false)");
        _t += 600_000;   // a death, a logout: ten minutes of nothing, no accepted call in between
        Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), "a refusal 600 s later is a NEW streak (false), not a 600 s streak failing open");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("the engine refused the change")), 2, "and it logged its own start line");

        // Refusals closer than the gap are one streak: it still fails open at 30 s.
        ActionPacer.Reset(); ActionPacer.Enabled = true; ActionPacer.NowMs = () => _t;
        long start = _t;
        bool open = false;
        for (int s = 0; s <= 40; s += 5) { _t = start + s * 1000L; open |= host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"); }
        Check.True(open, "refusals 5 s apart stay one streak and fail open past 30 s");

        // Forget (portal / death / logout) ends the streak too.
        ActionPacer.Reset(); ActionPacer.Enabled = true; ActionPacer.NowMs = () => _t;
        // Refusals 9 s apart are one streak (the gap is 10 s). Without the Forget the refusal at +36 s would be 36 s into it and fail open.
        Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), "streak begins");
        _t += 9_000; Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), "+9 s: same streak");
        ActionPacer.Forget("death");
        for (int s = 0; s < 3; s++) { _t += 9_000; Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "x"), $"after Forget a fresh streak, refusal {s + 1} (false)"); }
        Check.True(ActionPacer.LastEngineRefusalMs > 0, "LastEngineRefusalMs records the newest refusal");
        ActionPacer.Reset();
        Check.Eq(ActionPacer.LastEngineRefusalMs, 0L, "Reset clears LastEngineRefusalMs");
    });

    private static void StuckClockRestartsOnRefusal()
    {
        PaceOff();
        try
        {
            var r = new BuffRig(CombatMode.NonCombat);
            SetupBuffRigVitals(r);
            FakeHost.Names[Wand] = "Academy Wand";
            FakeHost.ItemTypes[Wand] = 0x8000;
            FakeHost.Ints[(Wand, 10)] = 0x01000000;   // in hand
            r.S.WieldUnlistedWandWhenNoneListed = true;
            r.B.SetWorldObjectCache(FakeHost.MakeCache(r.Host, Player, new[] { Wand }));
            var reEquips = typeof(BuffManager).GetField("_buffStanceReEquips", Priv)!;
            var since = typeof(BuffManager).GetField("_buffStanceStuckSince", Priv)!;
            var lastTry = typeof(BuffManager).GetField("_lastBuffStanceAttempt", Priv)!;

            r.At(0); r.B.OnHeartbeat();
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", "set-up: the first request is accepted and starts the stuck clock");
            Check.True((DateTime)since.GetValue(r.B)! != DateTime.MinValue, "set-up: the clock is running");

            // The request was dropped by a teleport; the engine now refuses. The backoff gate (BuffManager's clock) is opened by back-dating.
            lastTry.SetValue(r.B, r.T0.AddSeconds(-1));
            FakeHost.CombatModeRefuse = true;
            r.At(2); r.B.OnHeartbeat();
            Check.True(FakeHost.CombatModeRefusals >= 1, "set-up: a later request was refused");

            // 9 s of that: back-date the stuck clock 9 s.
            since.SetValue(r.B, r.T0);
            r.At(9); r.B.OnHeartbeat();
            r.At(9.3); r.B.OnHeartbeat();
            Check.Eq((int)reEquips.GetValue(r.B)!, 0, "a refusal newer than the clock's start restarted it: _buffStanceReEquips stays 0");
            Check.False(FakeHost.Uses.Contains(Wand), "and no UseObject(wand) - a MOVE on a wielded wand - went out");
            Check.True((r.NowAt - (DateTime)since.GetValue(r.B)!).TotalSeconds < 3, "the clock was restarted at now");

            FakeHost.CombatModeRefuse = false;
            lastTry.SetValue(r.B, r.T0.AddSeconds(7));   // the backoff after the restarted clock's request is over
            r.At(9.6); r.B.OnHeartbeat();
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8,8", "accepted again");
            Check.Eq((int)reEquips.GetValue(r.B)!, 0, "still no re-equip");
            Check.False(FakeHost.Uses.Contains(Wand), "still no UseObject(wand)");
        }
        finally { PaceOff(); }
    }

    // Drakkon 10-23 22:30: the launcher killed him on "[BuffStance] ChangeCombatMode(Magic) retry #4 (stuck 1123s)".
    // The clock had started on a Magic request 18 minutes before and never ended: Magic was reached while
    // EnsureMagicMode wasn't looking (combat took the stance back to its weapons first).
    private static void StuckClockEndsInMagicAndAfterAGap()
    {
        PaceOff();
        try
        {
            var r = new BuffRig(CombatMode.NonCombat);
            SetupBuffRigVitals(r);
            FakeHost.Names[Wand] = "Academy Wand";
            FakeHost.ItemTypes[Wand] = 0x8000;
            FakeHost.Ints[(Wand, 10)] = 0x01000000;   // in hand
            r.S.WieldUnlistedWandWhenNoneListed = true;
            r.B.SetWorldObjectCache(FakeHost.MakeCache(r.Host, Player, new[] { Wand }));
            var since = typeof(BuffManager).GetField("_buffStanceStuckSince", Priv)!;
            var lastTry = typeof(BuffManager).GetField("_lastBuffStanceAttempt", Priv)!;
            var noteMode = typeof(BuffManager).GetMethod("NoteModeChange", Priv)!;

            r.At(0); r.B.OnHeartbeat();
            Check.True((DateTime)since.GetValue(r.B)! != DateTime.MinValue, "set-up: a Magic request started the stuck clock");

            // The client reaches Magic and combat swaps back to its weapons before the buff step looks.
            FakeHost.CombatModeValue = CombatMode.Magic;
            noteMode.Invoke(r.B, null);
            Check.True((DateTime)since.GetValue(r.B)! == DateTime.MinValue, "seen in Magic: the episode is over, the clock stopped");
            FakeHost.CombatModeValue = CombatMode.Melee;
            noteMode.Invoke(r.B, null);

            // A clock left running anyway (any other way out): 20 minutes on, the next request starts afresh.
            since.SetValue(r.B, r.T0.AddMinutes(-20));
            lastTry.SetValue(r.B, r.T0.AddMinutes(-20));
            FakeHost.CombatModeValue = CombatMode.NonCombat;
            noteMode.Invoke(r.B, null);
            FakeHost.Logs.Clear();
            r.At(1); r.B.OnHeartbeat();
            string retry = FakeHost.Logs.FirstOrDefault(l => l.Contains("[BuffStance] ChangeCombatMode(Magic) retry")) ?? "";
            Check.True(retry.Contains("retry #1 (stuck 0s"), "a new episode: retry #1, stuck 0 s, not 1200 s: " + retry);
            Check.True((r.NowAt - (DateTime)since.GetValue(r.B)!).TotalSeconds < 3, "the clock starts now");
        }
        finally { PaceOff(); }
    }

    private static void StallToggleRefusedSpendsNoStep()
    {
        PaceOff();
        var host = World(combatMode: 2);
        var s = new LegacyUiSettings { BotAction = "Combat" };
        var cm = new CombatManager(host, s, new WorldObjectCache(host));
        cm.SetPlayerId(Player);
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(CombatManager).GetField("_activeTargetId", F)!.SetValue(cm, unchecked((int)Corpse));
        var watch = typeof(CombatManager).GetField("_stallWatch", F)!.GetValue(cm)!;
        var wt = watch.GetType();
        DateTime now = DateTime.Now;
        // The ladder stands at step 2 (re-select done 4 s ago); the next step due is the mode toggle.
        wt.GetField("_eligibleSince", F)!.SetValue(watch, now.AddSeconds(-12));
        wt.GetField("_lastTick", F)!.SetValue(watch, now.AddMilliseconds(-100));
        wt.GetField("_step", F)!.SetValue(watch, 2);
        wt.GetField("_lastStepAt", F)!.SetValue(watch, now.AddSeconds(-4));
        wt.GetField("_stallRef", F)!.SetValue(watch, now.AddSeconds(-12));
        int Step() => (int)wt.GetProperty("CurrentStep")!.GetValue(watch)!;
        var run = typeof(CombatManager).GetMethod("RunStallWatchdog", F)!;
        var lastAttack = typeof(CombatManager).GetField("lastAttackCmd", F)!;
        lastAttack.SetValue(cm, DateTime.MinValue);

        FakeHost.CombatModeRefuse = true;
        Check.True((bool)run.Invoke(cm, new object?[] { null, 1.0 })!, "the toggle step holds the tick");
        Check.Eq(FakeHost.CombatModeRefusals, 1, "the toggle reached the engine and was refused");
        Check.Eq(Step(), 2, "a refusal spends no ladder step: still at step 2");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "nothing was sent");
        Check.True((DateTime)lastAttack.GetValue(cm)! == DateTime.MinValue, "a refusal does not stamp the attack clock");

        FakeHost.CombatModeRefuse = false;
        Check.True((bool)run.Invoke(cm, new object?[] { null, 1.0 })!, "next tick: the toggle step is due again at once (not 3 s later)");
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "and the accepted toggle sent Peace");
        Check.Eq(Step(), 3, "now the ladder is at step 3");
        Check.True((DateTime)lastAttack.GetValue(cm)! != DateTime.MinValue, "the accepted toggle stamps the attack clock");
    }

    private static void CraftingPeaceRefusal()
    {
        PaceOff();
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        MissileCraftingManager Rig(int mode, bool refuse, out Action<double> backdateAsk)
        {
            var host = World(combatMode: mode);
            FakeHost.CombatModeRefuse = refuse;
            var cm = new MissileCraftingManager(host, new LegacyUiSettings { EnableMissileCrafting = true });
            var t = typeof(MissileCraftingManager);
            t.GetField("_currentRecipe", F)!.SetValue(cm, AmmoRecipes.All[0]);
            t.GetField("_headBundleId", F)!.SetValue(cm, 0x5001);
            t.GetField("_shaftBundleId", F)!.SetValue(cm, 0x5002);
            t.GetField("_peaceSent", F)!.SetValue(cm, false);
            t.GetField("_peaceAskedAt", F)!.SetValue(cm, DateTime.Now);
            t.GetMethod("TryPeaceForCrafting", F)!.Invoke(cm, null);   // StartCrafting's first step
            t.GetMethod("SetState", F)!.Invoke(cm, new object[] { MissileCraftingManager.CraftState.Combining });
            backdateAsk = sec => t.GetField("_peaceAskedAt", F)!.SetValue(cm, DateTime.Now.AddSeconds(-sec));
            return cm;
        }

        // (a) the engine always refuses: no combine inside the ceiling, the combine after it.
        var a = Rig(CombatMode.Melee, true, out var backdate);
        a.ProcessCrafting();
        Check.Eq(FakeHost.UsesOn.Count, 0, "(a) refused, inside 2.5 s: the combine waits");
        a.ProcessCrafting();
        Check.Eq(FakeHost.UsesOn.Count, 0, "(a) still waiting");
        Check.True(FakeHost.CombatModeRefusals >= 2, $"(a) peace was asked again each tick ({FakeHost.CombatModeRefusals} refusals)");
        backdate(2.6);
        a.ProcessCrafting();
        Check.Eq(FakeHost.UsesOn.Count, 1, "(a) past the 2.5 s ceiling the combine goes out anyway");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "(a) no mode request ever reached the server");

        // (b) already in Peace: no request at all, the combine on the first tick.
        var b = Rig(CombatMode.NonCombat, true, out _);
        b.ProcessCrafting();
        Check.Eq(FakeHost.UsesOn.Count, 1, "(b) already in Peace: the combine goes out on the first tick");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "(b) and no mode request was made");
        Check.Eq(FakeHost.CombatModeRefusals, 0, "(b) nor asked");
    }

    private static (Bot Bot, WorldObject Corpse) CorpseIdRig()
    {
        LootWorld();
        FakeHost.Strings.Remove((Corpse, LongDescKey));   // nothing appraised yet: no killer name
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Set("_targetCorpseId", unchecked((int)Corpse));   // the corpse the bot is going for: the only one that re-asks
        var obj = bot.Get<WorldObjectCache>("_objectCache")[unchecked((int)Corpse)]!;
        return (bot, obj);
    }

    private static void CorpseIdRefusedRetried() => Paced(() =>
    {
        var (bot, obj) = CorpseIdRig();
        FakeHost.RequestIdAccepts = false;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(FakeHost.RefusedIdRequests, 1, "the engine refused the first RequestId");
        Check.Eq(FakeHost.IdRequests.Count(i => i == Corpse), 0, "so nothing went out");
        _t += 250;
        FakeHost.RequestIdAccepts = true;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(FakeHost.IdRequests.Count(i => i == Corpse), 1, "the next evaluation asked again and it was accepted");
        _t += 100;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(FakeHost.IdRequests.Count(i => i == Corpse), 1, "an accepted request is not repeated at once");
    });

    private static void CorpseIdReaskedWithoutData() => Paced(() =>
    {
        var (bot, obj) = CorpseIdRig();
        int Asked() => FakeHost.IdRequests.Count(i => i == Corpse);
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 1, "t=0: the first request goes out");
        _t += 2_999;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 1, "2.999 s, no data: not yet");
        _t += 1;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 2, "3 s, no data: asked again");
        _t += 1_000;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 2, "1 s after the re-ask: waits");
        _t += 2_000;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 3, "one re-ask per 3 s");
        _t += 3_000;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 4, "the third re-ask is the last");
        for (int i = 0; i < 10; i++) { _t += 3_000; bot.Call("EvaluateCorpseLootDecision", obj); }
        Check.Eq(Asked(), 4, "ten more 3 s periods, no more asks: the corpse is given up on");
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        _t += 3_000;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 4, "and once the data has arrived nothing is asked either");
    });

    private static void CorpseIdAllCorpsesNoReask() => Paced(() =>
    {
        // All Corpses: the killer name decides nothing, so no stream of requests (each accepted one
        // raises the client's busy count). The one first request still goes out: the radar colours
        // your own kills from the LongDesc it brings.
        var (bot, obj) = CorpseIdRig();
        bot.S.LootOwnership = 2;
        int Asked() => FakeHost.IdRequests.Count(i => i == Corpse);
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 1, "All Corpses: the first request goes out");
        for (int i = 0; i < 6; i++) { _t += 3_000; bot.Call("EvaluateCorpseLootDecision", obj); }
        Check.Eq(Asked(), 1, "All Corpses: no re-ask in 18 s without data");
        object dec = bot.Call("EvaluateCorpseLootDecision", obj)!;
        Check.True((bool)dec.GetType().GetProperty("ShouldLoot")!.GetValue(dec)!, "the corpse is looted either way");

        // The same corpse in My Kills Only: the cap applies, the count is per corpse and a deleted
        // corpse starts over.
        bot.S.LootOwnership = 0;
        _t += 3_000; bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 2, "My Kills Only: the name is needed, so it re-asks");
        for (int i = 0; i < 10; i++) { _t += 3_000; bot.Call("EvaluateCorpseLootDecision", obj); }
        Check.Eq(Asked(), 4, "and stops after the third re-ask");
        bot.Call("HandleCorpseObjectDeleted", Corpse);
        _t += 3_000; bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 5, "a corpse that comes back (deleted and created again) starts over");
        var counts = bot.Get<System.Collections.Generic.Dictionary<int, int>>("_corpseIdAskCount");
        bot.Call("HandleCorpseObjectDeleted", Corpse);
        Check.False(counts.ContainsKey(unchecked((int)Corpse)), "delete clears _corpseIdAskCount");
    });
    private static void CorpseIdOnlyCandidateReasks() => Paced(() =>
    {
        LootWorld();
        FakeHost.Strings.Remove((Corpse, LongDescKey));
        FakeHost.Strings.Remove((CorpseB, LongDescKey));
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        var cache = bot.Get<WorldObjectCache>("_objectCache");
        var a = cache[unchecked((int)Corpse)]!;
        var b = cache[unchecked((int)CorpseB)]!;
        bot.Set("_targetCorpseId", unchecked((int)Corpse));
        int Asked(uint id) => FakeHost.IdRequests.Count(i => i == id);
        void Both() { bot.Call("EvaluateCorpseLootDecision", a); bot.Call("EvaluateCorpseLootDecision", b); }

        FakeHost.RequestIdAccepts = false;
        Both();
        FakeHost.RequestIdAccepts = true;
        _t += 250; Both();
        Check.Eq(Asked(Corpse), 1, "the candidate: refused once, then one accepted request");
        Check.Eq(Asked(CorpseB), 1, "the other corpse: a refused request is retried, one accepted request");
        for (int i = 0; i < 4; i++) { _t += 3_000; Both(); }
        Check.Eq(Asked(Corpse), 4, "no data: the candidate re-asks once per 3 s, three times at most");
        Check.Eq(Asked(CorpseB), 1, "the other corpse never asks a second time");
        bot.Set("_targetCorpseId", unchecked((int)CorpseB));
        _t += 3_000; Both();
        Check.Eq(Asked(CorpseB), 2, "once it becomes the candidate it re-asks");
    });
    private static void CorpseIdBurstCapped() => Paced(() =>
    {
        LootWorld();
        FakeHost.Strings.Remove((Corpse, LongDescKey));
        FakeHost.Strings.Remove((CorpseB, LongDescKey));
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        var cache = bot.Get<WorldObjectCache>("_objectCache");
        var ids = new System.Collections.Generic.List<uint> { Corpse, CorpseB };
        for (uint i = 0; i < 18; i++)
        {
            uint id = 0x80090000 + i;
            FakeHost.Names[id] = "Corpse of Olthoi Swarm Worker";
            FakeHost.ItemTypes[id] = ItemTypeContainer;
            FakeHost.NotAttackable.Add(id);
            FakeHost.Positions[id] = (Me0.Cell, Me0.X + 2.0f + i * 0.1f, Me0.Y, Me0.Z);
            cache.OnCreateObject(id);
            ids.Add(id);
        }
        cache.Tick();
        uint target = ids[^1];   // created last: the scan reaches it last, so only the candidate-first rule puts it among the first two
        bot.Set("_targetCorpseId", unchecked((int)target));
        void Scan() => bot.Call("HasUnlootedCorpsesInRange", 30.0);

        FakeHost.RequestIdAccepts = false;
        for (int k = 0; k < 3; k++) { _t += 300; Scan(); }
        Check.Eq(FakeHost.IdRequests.Count, 0, "the refusal window: nothing accepted");
        Check.True(FakeHost.RefusedIdRequests >= 20, $"every corpse was tried ({FakeHost.RefusedIdRequests} refusals)");
        int refusals = FakeHost.RefusedIdRequests;
        _t += 100; Scan();
        Check.Eq(FakeHost.RefusedIdRequests, refusals, "100 ms later: refused corpses are not retried yet (250 ms apart)");

        FakeHost.RequestIdAccepts = true;
        _t += 300; Scan();
        Check.True(FakeHost.IdRequests.Count <= 2, $"the first accepted tick sends at most 2 ({FakeHost.IdRequests.Count})");
        Check.True(FakeHost.IdRequests.Contains(target), "and the candidate is among them");
        for (int k = 0; k < 12; k++)
        {
            int before = FakeHost.IdRequests.Count;
            _t += 200; Scan();
            Check.True(FakeHost.IdRequests.Count - before <= 2, $"tick {k + 2}: at most 2 new requests");
        }
        Check.Eq(FakeHost.IdRequests.Distinct().Count(), 20, "within a few ticks every corpse has been asked once");
    });

    private static void CorpseIdOpenContainerAndCleanup() => Paced(() =>
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        var (bot, obj) = CorpseIdRig();
        bot.Set("_targetCorpseId", 0);
        bot.Set("_openedContainerId", unchecked((int)Corpse));
        int Asked() => FakeHost.IdRequests.Count(i => i == Corpse);
        bot.Call("EvaluateCorpseLootDecision", obj);
        _t += 3_000;
        bot.Call("EvaluateCorpseLootDecision", obj);
        Check.Eq(Asked(), 2, "the open container is a candidate: it re-asks after 3 s");

        var asked = bot.Get<System.Collections.Generic.Dictionary<int, long>>("_corpseIdAskedAt");
        var refused = bot.Get<System.Collections.Generic.Dictionary<int, long>>("_corpseIdRefusedAt");
        var requested = bot.Get<System.Collections.Generic.HashSet<int>>("_corpseIdRequested");
        int sid = unchecked((int)Corpse);
        refused[sid] = _t;
        Check.True(asked.ContainsKey(sid) && requested.Contains(sid), "set-up: the corpse is recorded");
        bot.Call("HandleCorpseObjectDeleted", Corpse);
        Check.False(asked.ContainsKey(sid), "delete clears _corpseIdAskedAt");
        Check.False(refused.ContainsKey(sid), "delete clears _corpseIdRefusedAt");
        Check.False(requested.Contains(sid), "delete clears _corpseIdRequested");

        for (int i = 0; i < 201; i++) { requested.Add(i + 1); asked[i + 1] = _t; refused[i + 1] = _t; }
        bot.Set("_lastCorpsePruneAt", 0L);
        bot.Call("PruneCorpseCollections");
        Check.Eq(requested.Count, 0, "the >200 prune clears _corpseIdRequested");
        Check.Eq(asked.Count, 0, "the >200 prune clears _corpseIdAskedAt");
        Check.Eq(refused.Count, 0, "the >200 prune clears _corpseIdRefusedAt");
    });
    // ── Looting ──────────────────────────────────────────────────────────────────

    private static void LootWorld()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        FakeHost.WeaponCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.BusyCalls = true;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        foreach ((uint id, float dx) in new[] { (Corpse, 1.0f), (CorpseB, 1.5f) })
        {
            FakeHost.Names[id] = "Corpse of Olthoi Swarm Worker";
            FakeHost.ItemTypes[id] = ItemTypeContainer;
            FakeHost.Strings[(id, LongDescKey)] = $"Killed by {Me}.";
            FakeHost.NotAttackable.Add(id);
            FakeHost.Positions[id] = (Me0.Cell, Me0.X + dx, Me0.Y, Me0.Z);
        }
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
        };

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            cache.OnCreateObject(Corpse);
            cache.OnCreateObject(CorpseB);
            cache.Tick();
            string profile = Path.Combine(Program.TempRoot, "action-pacing-test.json");
            new LootProfile { Name = "nothing" }.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public void Tick() { Set("_activity", BotActivity.Looting); Call("TickCorpseOpening"); }
    }

    /// <summary>The loot clock and the pacer clock on one fake time.</summary>
    private static void WithClock(Action<Func<long>, Action<long>> body)
    {
        long now = 50_000_000;
        var saved = RynthAiPlugin.CorpseClock;
        RynthAiPlugin.CorpseClock = () => now;
        ActionPacer.NowMs = () => now;
        UseAudit.NowMs = () => now;
        MoveAudit.NowMs = () => now;
        try { body(() => now, ms => now += ms); }
        finally { RynthAiPlugin.CorpseClock = saved; }
    }

    private static void LootOneOpenPerAnswer()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                for (int i = 0; i < 10 && FakeHost.Uses.Count == 0; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 1, "the open goes out");
                // A long walk: no answer for 3 s. Before, the open was re-sent every LootOpenRetryMs,
                // and each re-send restarted the server's walk and failed the first open's callback.
                for (int i = 0; i < 30; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 1, "not re-sent while the server hasn't answered");
                Check.True(Logged("[Pace] Loot waits: the use of"), "the wait is logged once");
                FakeHost.UseDone(0);
                bot.P.OnViewObjectContents(FakeHost.Uses.Last());
                Check.True(ActionPacer.Current(host) == null, "answered: nothing outstanding");
                // The empty corpse is done; the next one is opened - once, and only now.
                for (int i = 0; i < 20; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 2, "the next corpse's open went out after the answer, once");
                Check.Eq(FakeHost.Uses.Last(), CorpseB, "it is the other corpse");
                Check.True(ActionPacer.Current(host)?.Id == CorpseB, "and it is the action the server is answering now");
                Check.Eq(FakeHost.ForceResets, 0, "no force-clear");
            });
        }
        finally { PaceOff(); }
    }

    private static void LootStallStillDetected()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                long answerAt = 0;
                int maxOutstanding = 0;
                void Step()
                {
                    int uses = FakeHost.Uses.Count;
                    bot.Tick();
                    if (FakeHost.Uses.Count > uses) answerAt = now() + 60;
                    maxOutstanding = Math.Max(maxOutstanding, FakeHost.Uses.Count - FakeHost.UseDoneSeq);
                    advance(100);
                    if (answerAt != 0 && now() >= answerAt) { FakeHost.UseDone(0); answerAt = 0; }
                }
                int steps = 0;
                while (FakeHost.Chat.Count(c => c.Contains("isn't letting this character use objects")) == 0 && steps++ < 900) Step();
                Check.Eq(FakeHost.Chat.Count(c => c.Contains("isn't letting this character use objects")), 1,
                    $"the stall is found and said once ({steps / 10.0} s)");
                Check.True(steps <= 600, $"within a minute ({steps / 10.0} s)");
                Check.True(maxOutstanding <= 1, $"never more than one open unanswered (max {maxOutstanding})");
            });
        }
        finally { PaceOff(); }
    }

    // ── Combat ───────────────────────────────────────────────────────────────────

    private static void CombatPeaceWaitsForKit() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        var s = new LegacyUiSettings { IsMacroRunning = true, EnableCombat = true, PeaceModeWhenIdle = true, MonsterRange = 50 };
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        var combat = new CombatManager(host, s, cache);
        combat.SetPlayerId(Player);

        // 07:00:11.8: a kit on yourself in melee stance; the server answers 2.4 s later.
        host.UseOnFor(Kit, Player, "Buff", "vitals: healing kit on yourself");
        for (int i = 0; i < 5; i++) { combat.Think(); _t += 400; }
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no peace swap while the kit is unanswered");
        FakeHost.UseDone(0);
        combat.Think();
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "the kit answered: the idle swap goes out");
    });

    private static void ArbiterStopOnlyWhenMoving()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                bot.Call("StopNavFor", BotActivity.Looting);
                Check.Eq(FakeHost.Stops, 0, "nothing of ours moving: no StopCompletely (another plugin's movement isn't cut off)");
                host.SetAutoRunBy("Nav", true);
                bot.Call("StopNavFor", BotActivity.Buffing);
                Check.Eq(FakeHost.Stops, 1, "our autorun on: the stop goes out");
                ActionPacer.Enabled = false;
                bot.Call("StopNavFor", BotActivity.Combat);
                Check.Eq(FakeHost.Stops, 2, "pacing off: as before, always");
            });
        }
        finally { PaceOff(); }
    }

    // ── Busy mirror ──────────────────────────────────────────────────────────────

    private static void MirrorResetOnly()
    {
        // The engine's count is the owner: a mirror left up follows it down after 1.5 s.
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            FakeHost.BusyStateValue = 0;   // the engine has seen the UseDones
            bot.Set("_busyCount", 2);
            bot.Set("_busyCountBecamePositiveAt", now());
            bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 2, "a fresh disagreement waits (events may be on their way)");
            advance(1_600);
            bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 0, "1.6 s later: the mirror follows the engine's 0");
            Check.True(Logged("following the engine's count"), "logged");

            FakeHost.BusyStateValue = 1;   // the server owes an answer: the mirror follows that too
            bot.Call("CheckBusyTimeout"); advance(1_600); bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 1, "and up, while the server still owes an answer");
            advance(30_000); bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 1, "never reset on a timer while the engine says busy");
            Check.Eq(FakeHost.ForceResets, 0, "the client's busy count is left to the client");
        });

        // An engine without GetBusyState: the old timer, resetting the mirror only.
        LootWorld();
        FakeHost.BusyCalls = false;
        host = FakeHost.Create(Player);
        bot = new Bot(host);
        WithClock((now, advance) =>
        {
            bot.Set("_busyCount", 2);
            bot.Set("_busyCountBecamePositiveAt", now());
            advance(10_500);
            bot.Call("CheckBusyTimeout");
            Check.Eq(bot.Get<int>("_busyCount"), 0, "old engine: our mirror is reset after 10 s");
            Check.True(Logged("resetting the mirror only"), "and the line says so");
        });
    }

    // ── The motion gate (2026-10-06 afternoon, ops\overnight\2026-10-06-turnto-stall.md) ──────────
    // Nothing starts while the character is still animating (the client's motion queue, the engine's
    // GetCastBusyState); FakeHost.CastBusy = 1 stands for "animating".

    // 16:45:37: the wand's use went 110 ms after a MeleeAttack, before the swing animated.
    private static void SwingNotStarted() => Paced(() =>
    {
        var host = World();
        FakeHost.CastBusy = 0;
        ActionPacer.NoteAttackSent();
        _t += 110;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out string why), "the wand waits: the swing hasn't animated yet");
        Check.True(why.Contains("swing"), "why: " + why);
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true), "the next attack isn't held");
        _t += 200;
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "swinging: still waits");
        _t += 700;
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "the swing played: the wand goes");

        ActionPacer.NoteAttackSent();
        _t += 600;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "an emergency heal waits at most 500 ms");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "anything else still waits");
        _t += 1000;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "a swing that never animates holds at most 1.5 s");
        Check.True(ActionPacer.SwingHoldCount >= 2, "holds counted");
    });

    private static void MotionGate1429() => Paced(() =>
    {
        var host = World();
        // 14:29:21.435 the swing; 22.380 AttackDone (the attack sequence is over, the swing still plays).
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out string why), "22.41: the kit waits for the swing's animation");
        Check.True(why.Contains("still animating"), "why says so: " + why);
        Check.True(Logged("[Pace] Buff waits: the character is still animating"), "logged once");
        _t += 300;
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "the swing is over: the kit goes");
        host.UseOnFor(Kit, Player, "Buff", "vitals: healing kit on yourself");
        FakeHost.CastBusy = 1;   // the kit's SkillHealSelf
        _t += 2400;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "the corpse open waits for the kit's answer");
        FakeHost.UseDone(0);     // 24.794 the kit's UseDone; the client still plays the kit's last frames
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out why), "answered, but still animating: the open waits");
        Check.True(why.Contains("still animating"), "why: " + why);
        _t += 120;
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "at rest: the corpse open goes");
        Check.True(ActionPacer.GestureHoldCount >= 2, "holds counted");
    });

    private static void MotionGate1358() => Paced(() =>
    {
        var host = World(combatMode: 2);
        // 13:58:54.210 the wand is wielded in melee stance (its use is an item use, answered by wielding).
        host.UseFor(Wand, "Buff", "buffing: switch to the wand");
        FakeHost.CastBusy = 1;                       // the unwield/wield and stance motion
        FakeHost.Wielded[Wand] = 0x01000000;
        _t += 50;
        ActionPacer.Current(host);                   // the wield is first seen done
        _t += 200;
        Check.True(ActionPacer.Current(host) == null, "the wield reads done: answered");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "the cast waits: still animating");
        Check.False(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand wielded: Magic mode"), "the Magic stance waits too");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no mode request sent");
        Check.True(Logged("[Pace] mode:Buff waits: the character is still animating"), "logged");
        _t += 900;
        FakeHost.CastBusy = 0;
        Check.True(host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand wielded: Magic mode"), "at rest: Magic mode");
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", "sent");
        FakeHost.CombatModeValue = 8;
        FakeHost.CastBusy = 1;                       // the stance animation
        _t += ActionPacer.ModeSettleMs;
        Check.True(ActionPacer.Current(host) == null, "the stance is read and settled");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "but the client still animates it: the cast waits");
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "at rest: the cast goes");
    });

    private static void MotionGateMorning() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        var s = new LegacyUiSettings { IsMacroRunning = true, EnableCombat = true, PeaceModeWhenIdle = true, MonsterRange = 50 };
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        var combat = new CombatManager(host, s, cache);
        combat.SetPlayerId(Player);

        // 06:18:44.549 the corpse use in melee stance; the idle peace swap came 3 ms later.
        host.UseFor(Corpse, "Loot", "open the claimed corpse");
        _t += 3;
        combat.Think();
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "3 ms later: no peace swap (the use is unanswered)");
        FakeHost.CastBusy = 1;   // the turn to the corpse
        _t += 400;
        FakeHost.UseDone(0);
        typeof(CombatManager).GetField("_lastPeaceAttempt", Any)!.SetValue(combat, DateTime.MinValue);
        combat.Think();
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "answered, but the character still animates: no peace swap");
        _t += 200;
        FakeHost.CastBusy = 0;
        typeof(CombatManager).GetField("_lastPeaceAttempt", Any)!.SetValue(combat, DateTime.MinValue);
        combat.Think();
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "at rest: the idle swap goes out");
    });

    private static void MotionGateLimits() => Paced(() =>
    {
        var host = World();
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "animating: held");
        _t += 1499;
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "an emergency heal waits up to 1.5 s");
        _t += 1;
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "at 1.5 s the emergency heal goes");
        Check.True(Logged("emergency heal after 1500 ms of animation - going on"), "logged");
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "anything else still waits");
        _t += ActionPacer.GestureHoldMaxMs - 1500;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "4 s: taken as stuck, the action goes");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("taken as stuck, going on")), 1, "logged once");
        _t += 5000;
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "still open while it reads busy");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("taken as stuck, going on")), 1, "not logged again");
        Check.Eq(ActionPacer.GestureLimits, 2L, "two limits counted");
        FakeHost.CastBusy = 0;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "clear");
        Check.True(Logged("animation queue reads clear again"), "the recovery is logged");
        FakeHost.CastBusy = 1;
        Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "a new animation: held again (a fresh 4 s)");
    });

    private static void MotionGateAttackExempt() => Paced(() =>
    {
        var host = World(combatMode: 2);
        FakeHost.CastBusy = 1;   // the last swing still plays
        Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true), "a melee attack repeat is not held by the animation");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "anything gated is");
        host.UseFor(Corpse, "Loot", "open");
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true), "but an attack still waits for an unanswered corpse use (the server's turn)");
        FakeHost.UseDone(0);
        Check.True(ActionPacer.Current(host) == null, "the use is answered");
        ActionPacer.Enabled = false;
        Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "pacing off: no gate");
        Check.False(ActionPacer.GestureHolds(host, "Loot", HealNeed.None, out _), "pacing off: no animation hold");
        ActionPacer.Enabled = true;
        FakeHost.ServerGates = false;
        var old = FakeHost.Create(Player);
        Check.True(ActionPacer.MayStart(old, "Loot", HealNeed.None, out _), "an engine without the gate: no opinion");
    });

    private static void MotionGateLooting()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            WithClock((now, advance) =>
            {
                FakeHost.CastBusy = 1;
                for (int i = 0; i < 20; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 0, "no open while the character animates");
                Check.True(Logged("[Pace] Loot waits: the character is still animating"), "logged");
                FakeHost.CastBusy = 0;
                for (int i = 0; i < 10 && FakeHost.Uses.Count == 0; i++) { bot.Tick(); advance(100); }
                Check.Eq(FakeHost.Uses.Count, 1, "at rest: the open goes");
            });
        }
        finally { PaceOff(); }
    }

    private static void MotionGateItems()
    {
        LootWorld();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        PaceOn();
        try
        {
            int Busy() => (int)typeof(RynthAiPlugin).GetProperty("PacedBusy", Any)!.GetValue(bot.P)!;
            Check.Eq(Busy(), 0, "at rest, nothing outstanding: 0");
            FakeHost.CastBusy = 1;
            Check.Eq(Busy(), 1, "animating: +1 (a pack move of a wielded item, a stone, a summon waits)");
            FakeHost.CastBusy = 0;
            Check.Eq(Busy(), 0, "at rest again: 0");
        }
        finally { PaceOff(); }
    }
}
