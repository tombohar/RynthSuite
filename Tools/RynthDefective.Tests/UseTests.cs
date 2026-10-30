using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthDefective.Tests
{
    internal static partial class Program
    {
        private static Harness With(params CarriedItem[] items)
        {
            var h = new Harness();
            h.World.Inventory.AddRange(items);
            return h;
        }

        private static void UseKit()
        {
            Harness h = With(Kit(0x10));
            Check(h.Slot(ItemRef.From(Kit(0x10))), "queued");
            Check(h.Use.HasPending && !h.Use.InFlight, "waiting for the next tick");
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "a kit is used on yourself");
            Check(h.Use.InFlight && !h.Use.HasPending, "in flight");
            Check(h.Said.Count == 1 && h.Said[0] == "Using Peerless Healing Kit...", "the status line says so: " + string.Join(";", h.Said));
            Check(h.Logged.Count == 1 && h.Logged[0].Contains("slot") && h.Logged[0].Contains("use on you"), "and the log: " + string.Join(";", h.Logged));

            h.Tick(100);
            Check(h.Use.InFlight, "no answer yet: still in flight");
            h.World.Answer();
            h.Tick(100);
            Check(!h.Use.InFlight && !h.Use.HasPending, "the server's UseDone ends it");
            Eq(h.World.Sent.Count, 1, "nothing else sent");
            Eq(h.Warned.Count, 0, "no warnings");

            // A potion is used by itself.
            h = With(Potion(0x20));
            h.Slot(ItemRef.From(Potion(0x20)));
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "use 20", "a potion is used with UseObject");
        }

        private static void UseQueue()
        {
            Harness h = With(Kit(0x10), Potion(0x20), Item(0x30, "Mana Potion", 3, 100));
            ItemRef kit = ItemRef.From(Kit(0x10)), health = ItemRef.From(Potion(0x20)), mana = ItemRef.From(h.World.Inventory[2]);
            h.Slot(kit);
            h.Tick();
            Check(h.Slot(health), "a second request while the first is in flight waits");
            Check(h.Slot(mana), "a third replaces it: the newest wish wins");
            Check(!h.Slot(mana), "the same request again (a key held down) does not queue twice");
            h.Tick(100);
            Eq(h.World.Sent.Count, 1, "nothing goes out while the first is unanswered");
            h.World.Answer();
            h.Tick(10);
            Eq(h.World.Sent.Count, 1, "and not in the same tick it was answered (a short gap)");
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "useon 10 me|use 30", "then the newest request, not the one it replaced");

            // The same item pressed again while it is in flight is a new use afterwards, once.
            h = With(Kit(0x10));
            h.Slot(kit);
            h.Tick();
            Check(h.Slot(kit), "pressed again during the use: one more is queued");
            Check(!h.Slot(kit), "but only one");
            h.World.Answer();
            h.Tick(10);
            h.Tick(300);
            Eq(h.World.Sent.Count, 2, "so a second kit goes out after the first");
        }

        private static void UseBusy()
        {
            Harness h = With(Kit(0x10));
            ItemRef kit = ItemRef.From(Kit(0x10));
            // A count that keeps moving is an action under way (the player's own, another plugin's): it holds.
            h.Tick();   // the first look: idle
            h.Slot(kit);
            for (int i = 0; i < 5; i++)
            {
                h.World.BusyCount = 1 + (i % 2);
                h.Tick(1000);
            }
            Eq(h.World.Sent.Count, 0, "a moving busy count holds it");
            Check(h.Use.HasPending && h.Warned.Count == 0, "still waiting inside the limit");
            h.World.BusyCount = 2;
            h.Tick(1000);
            Check(!h.Use.HasPending && h.Warned.Count == 1 && h.Warned[0] == "Peerless Healing Kit: not used, the character is busy.", "past the limit it gives up and says why: " + string.Join(";", h.Warned));
            Eq(h.World.Sent.Count, 0, "and sent nothing");

            h = With(Kit(0x10));
            h.World.Animating = true;
            h.Slot(kit);
            h.Ticks(3, 1000);
            Eq(h.World.Sent.Count, 0, "a gesture holds it");
            h.World.Animating = false;
            h.Tick(100);
            Eq(h.World.Sent.Count, 1, "and it goes when the gesture ends");

            h = With(Kit(0x10));
            h.World.Animating = true;
            h.Slot(kit);
            h.Ticks(4, 1000);
            Eq(h.World.Sent.Count, 0, "a gesture 3 s long still holds it");
            h.Tick(1000);
            Check(h.World.Sent.Count == 1 && h.Warned.Count == 0, "one that never ends (a stuck motion queue) is let go after 4 s");
            Eq(h.Logged.Count(l => l.Contains("'animating' for")), 1, "logged once");

            h = With(Kit(0x10));
            h.World.BotWaitingOnServer = true;
            h.Slot(kit);
            h.Ticks(3, 1000);
            Eq(h.World.Sent.Count, 0, "RynthAi waiting on the server holds it");
            h.World.BotWaitingOnServer = false;
            h.Tick(100);
            Eq(h.World.Sent.Count, 1, "and it goes the moment RynthAi is done");

            // Busy clears in time: sent, no warning.
            h = With(Kit(0x10));
            h.World.ClientBusy = true;
            h.Slot(kit);
            h.Ticks(3, 1000);
            h.World.ClientBusy = false;
            h.Tick(100);
            Check(h.World.Sent.Count == 1 && h.Warned.Count == 0, "busy cleared inside the limit: used");

            // RynthAi absent (null) never holds anything.
            h = With(Kit(0x10));
            h.World.BotWaitingOnServer = null;
            h.Slot(kit);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "no RynthAi: nothing to wait for");
        }

        private static BusyFacts Facts(int count = 0, long steady = 0, int leftover = 0, long own = long.MaxValue / 4,
            bool anim = false, long animMs = 0, bool? bot = null, long botMs = 0) =>
            new(count, steady, leftover, own, anim, animMs, bot, botMs);

        private static void BusyRuleDecisions()
        {
            Eq(BusyRule.Decide(Facts(), out BusyStale st), BusyHold.None, "idle: send");
            Eq(st, BusyStale.None, "nothing ignored");

            Eq(BusyRule.Decide(Facts(count: 1, steady: 10_000), out st), BusyHold.None, "busy with nothing in flight for longer than the bound: send");
            Eq(st, BusyStale.Count, "the count was ignored as a leftover");
            Eq(BusyRule.Decide(Facts(count: 1, steady: BusyRule.CountTrustMs), out _), BusyHold.None, "exactly the bound: a leftover");
            Eq(BusyRule.Decide(Facts(count: 1, steady: BusyRule.CountTrustMs - 1), out _), BusyHold.Busy, "just under it: still trusted");

            Eq(BusyRule.Decide(Facts(count: 1, steady: 10_000, own: 500), out _), BusyHold.Busy, "busy just after our own use: wait");
            Eq(BusyRule.Decide(Facts(count: 1, steady: 10_000, own: BusyRule.OwnTrustMs), out _), BusyHold.None, "our own use long enough ago: a leftover");

            Eq(BusyRule.Decide(Facts(anim: true, animMs: 300), out _), BusyHold.Animation, "animating: wait");
            Eq(BusyRule.Decide(Facts(count: 1, steady: 10_000, anim: true, animMs: 300), out _), BusyHold.Animation, "animating holds over a leftover count");
            Eq(BusyRule.Decide(Facts(anim: true, animMs: BusyRule.AnimationMaxMs), out st), BusyHold.None, "a gesture past the bound is a stuck motion queue");
            Eq(st, BusyStale.Animation, "and ignored as such");
            Eq(BusyRule.Decide(Facts(count: 1, steady: 200, anim: true, animMs: 9000), out _), BusyHold.Busy, "a stuck gesture doesn't hide a fresh count");

            Eq(BusyRule.Decide(Facts(count: 1, steady: 200), out _), BusyHold.Busy, "a count that just changed: wait");
            Eq(BusyRule.Decide(Facts(count: 3, steady: 200, leftover: 2), out _), BusyHold.Busy, "a count that just rose above the leftover: wait");
            Eq(BusyRule.Decide(Facts(count: 2, steady: 200, leftover: 2), out st), BusyHold.None, "back at the leftover (our use answered): holds nothing though it just moved");
            Eq(st, BusyStale.Count, "the leftover is still a leftover");
            Eq(BusyRule.Decide(Facts(count: 3, steady: 5000, leftover: 2), out st), BusyHold.None, "above the leftover but unchanged for longer than the bound: a bigger leftover");
            Eq(st, BusyStale.Count, "ignored");

            Eq(BusyRule.Decide(Facts(bot: true, botMs: 1000), out _), BusyHold.Bot, "RynthAi waiting on the server, fresh: wait");
            Eq(BusyRule.Decide(Facts(bot: true, botMs: BusyRule.BotTrustMs), out st), BusyHold.None, "RynthAi's bit longer than its own action limits: send");
            Eq(st, BusyStale.Bot, "ignored");
            Eq(BusyRule.Decide(Facts(count: 1, steady: 10_000, bot: true, botMs: 10_000), out st), BusyHold.None, "RynthAi's copy of a leftover count (its macro off after a death)");
            Eq(st, BusyStale.Count | BusyStale.Bot, "both ignored");
            Eq(BusyRule.Decide(Facts(count: 1, steady: 10_000, bot: true, botMs: BusyRule.CountTrustMs), out _), BusyHold.None, "with a leftover count the bit gets the count's bound");
            Eq(BusyRule.Decide(Facts(count: 1, steady: 10_000, bot: true, botMs: 1000), out _), BusyHold.Bot, "a bit that just came on holds even over a leftover (RynthAi just sent something)");
            Eq(BusyRule.Decide(Facts(bot: false, botMs: 50), out _), BusyHold.None, "RynthAi idle");
        }

        // RynthAi holds for the press (RynthAi.Yield, 2026-10-29) and says what it has in flight.
        private static BusyFacts Paced(int count = 0, long steady = 0, int leftover = 0, long own = long.MaxValue / 4,
            bool anim = false, long animMs = 0, bool? botWait = null, bool paced = false, long pacedMs = 0) =>
            new(count, steady, leftover, own, anim, animMs, botWait, 0, paced, pacedMs);

        private static void BusyRulePaced()
        {
            Eq(BusyRule.Decide(Paced(), out BusyStale st), BusyHold.None, "idle: send");
            Eq(BusyRule.Decide(Paced(count: 2, steady: 100), out st), BusyHold.None,
                "a count that just moved, with nothing of ours or RynthAi's in flight (a death's, a corpse open's): send");
            Eq(st, BusyStale.Count, "ignored as a leftover");
            Eq(BusyRule.Decide(Paced(count: 1, steady: 100, own: 500), out _), BusyHold.Busy, "right after our own use: wait");
            Eq(BusyRule.Decide(Paced(count: 1, steady: 100, own: BusyRule.OwnTrustMs), out _), BusyHold.None, "our own use long enough ago: send");
            Eq(BusyRule.Decide(Paced(count: 3, steady: 100, leftover: 3, own: 500), out _), BusyHold.None, "our use's count back at the leftover: send");
            Eq(BusyRule.Decide(Paced(paced: true, pacedMs: 300), out _), BusyHold.Bot, "RynthAi's own action unanswered: wait for its answer");
            Eq(BusyRule.Decide(Paced(paced: true, pacedMs: BusyRule.BotTrustMs), out st), BusyHold.None, "at most BotTrustMs");
            Eq(st, BusyStale.Bot, "then ignored");
            Eq(BusyRule.Decide(Paced(count: 1, steady: 50, botWait: true), out _), BusyHold.None,
                "RynthAi's 'waiting on the server' bit (its copy of the busy count) holds nothing while it holds for us");
            Eq(BusyRule.Decide(Paced(anim: true, animMs: 300), out _), BusyHold.Animation, "a gesture still holds");
            Eq(BusyRule.Decide(Facts(count: 1, steady: 100), out _), BusyHold.Busy, "without the hold (a RynthAi from before): the old rule, a fresh count holds");
        }

        private static void UsePlayersTurn()
        {
            ItemRef kit = ItemRef.From(Kit(0x10));

            // After a death: the death left 2 in the count, and RynthAi's rebuffs keep moving it.
            Harness h = With(Kit(0x10));
            h.World.YieldOn = true;
            h.World.BusyCount = 2;
            h.Tick();
            h.World.BusyCount = 3; h.Tick(400);   // a cast of RynthAi's, answered
            h.World.BusyCount = 2; h.Tick(400);
            Eq(h.World.HoldAsks, 0, "nothing asked while nothing waits");
            h.Slot(kit);
            h.Tick(50);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "the press goes at once: nothing of ours or RynthAi's is in flight");
            Check(h.World.HoldAsks == 1 && h.World.BotHeldNow && h.Use.BotHeld, "RynthAi was asked to hold, and holds through the kit's flight");
            Eq(h.Warned.Count, 0, "no warning");
            Eq(h.Logged.Count(l => l.Contains("nothing of ours or RynthAi's in flight")), 1, "the leftover is logged once");
            h.Tick(100);
            Check(h.World.HoldAsks == 2 && h.World.BotHeldNow, "asked again every tick of the flight");
            h.World.Answer();
            h.Tick(100);
            Check(!h.Use.InFlight && !h.World.BotHeldNow && h.World.Releases == 1 && !h.Use.BotHeld, "answered: RynthAi is let go that tick");
            h.Tick(100);
            Eq(h.World.Releases, 1, "and only once");

            // RynthAi's own cast is still out: the press waits for its answer, and goes before anything new of RynthAi's.
            h = With(Kit(0x10));
            h.World.YieldOn = true;
            h.World.BotOwnAction = true;
            h.World.BusyCount = 1;
            h.Tick();
            h.Slot(kit);
            h.Ticks(3, 500);
            Eq(h.World.Sent.Count, 0, "RynthAi's cast has no answer yet: the press waits");
            Check(h.World.BotHeldNow && h.World.HoldAsks == 3, "while RynthAi holds its next one (asked every tick)");
            h.World.BotOwnAction = false;
            h.World.BusyCount = 0;
            h.Tick(100);
            Eq(h.World.Sent.Count, 1, "answered: the press goes next");

            // A RynthAi action that never gets an answer holds the press at most BotTrustMs.
            h = With(Kit(0x10));
            h.World.YieldOn = true;
            h.World.BotOwnAction = true;
            h.Tick();
            h.Slot(kit);
            h.Ticks(5, 1000);
            Eq(h.World.Sent.Count, 0, "5 s: still waiting for RynthAi's answer");
            h.Tick(600);
            Check(h.World.Sent.Count == 1 && h.Warned.Count == 0, "past 5.5 s the press goes anyway, no warning");
            Check(h.Logged.Any(l => l.Contains("RynthAi's own action has had no answer")), "and the log says why");

            // Our use gets no answer: RynthAi is let go after FlightHoldMs though the use is still out.
            h = With(Kit(0x10));
            h.World.YieldOn = true;
            h.Tick();
            h.Slot(kit);
            h.Tick(50);
            h.World.BusyCount = 1;   // the client holds our kit busy (so it isn't let go as quiet)
            h.Tick(2000);
            Check(h.Use.InFlight && h.World.BotHeldNow, "2 s into the flight: RynthAi still holds");
            h.Tick(1100);
            Check(h.Use.InFlight && !h.World.BotHeldNow && h.World.Releases == 1, "past 3 s: RynthAi goes on while our use waits for its answer");

            // Portal space drops the press and lets RynthAi go.
            h = With(Kit(0x10));
            h.World.YieldOn = true;
            h.World.BotOwnAction = true;
            h.Tick();
            h.Slot(kit);
            h.Tick(100);
            Check(h.World.BotHeldNow, "held while the press waits");
            h.World.InWorld = false;
            h.Tick(100);
            Check(!h.Use.HasPending && !h.World.BotHeldNow && h.World.Releases == 1, "portal space: dropped, RynthAi let go");

            // A RynthAi from before RynthAi.Yield: nothing to ask, nothing to let go, the old rule.
            h = With(Kit(0x10));
            h.World.BusyCount = 1;
            h.Tick();
            h.Slot(kit);
            h.Tick(100);
            Check(h.World.Sent.Count == 0 && h.World.HoldAsks == 0 && h.World.Releases == 0 && !h.Use.BotHeld,
                "no hold taken; a count that may be real still holds the press");
        }

        // A report (10-29): after portalling in, four presses of a weapon slot in three seconds each said
        // "the client didn't take the request (nothing was sent)": the engine takes no game action until
        // 5 s after portal space ends. The press now waits for it.
        private static Harness OutOfPortal(params CarriedItem[] items)
        {
            Harness h = With(items);
            h.World.InWorld = false;   // portal space
            h.Tick(100);
            h.World.InWorld = true;    // out of it: the engine's gate holds game actions a while yet
            h.World.RefuseSends = true;
            h.Tick(100);
            return h;
        }

        private static void UseAfterPortal()
        {
            ItemRef kit = ItemRef.From(Kit(0x10));
            Harness h = OutOfPortal(Kit(0x10));
            h.Slot(kit);
            h.Tick(50);
            Check(h.World.Sent.Count == 0 && h.Warned.Count == 0 && h.Use.HasPending, "refused just after the portal: it waits, no warning");
            Eq(h.Said.Count(s => s.Contains("waiting for the game to finish the portal")), 1, "says what it waits for");
            h.Ticks(8, 250);
            Check(h.Warned.Count == 0 && h.Use.HasPending, "2 s more of refusals: still waiting");
            Eq(h.Said.Count(s => s.Contains("waiting for the game")), 1, "said once");
            Eq(h.Logged.Count(l => l.Contains("doesn't take") && l.Contains("after the portal")), 1, "logged once");
            h.World.RefuseSends = false;   // the engine's gate opens
            h.Tick(250);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "it goes the moment the client takes it");
            Check(h.Use.InFlight && h.Warned.Count == 0, "in flight, never a warning");

            // A second press of the same slot while it waits doesn't queue twice.
            h = OutOfPortal(Kit(0x10));
            h.Slot(kit);
            h.Tick(50);
            Check(!h.Slot(kit), "the same press again: already waiting");

            // The client never takes it: once the portal is 6 s behind, the refusal is said as before.
            h = OutOfPortal(Kit(0x10));
            h.Slot(kit);
            h.Ticks(24, 250);
            Check(!h.Use.HasPending && h.Warned.Count == 1 && h.Warned[0].Contains("didn't take the request"),
                "6 s after the portal it gives up and says so: " + string.Join("|", h.Warned));

            // Long after a portal a refusal is said at once, as before.
            h = OutOfPortal(Kit(0x10));
            h.Tick(UseController.PortalRetryWindowMs);
            h.Slot(kit);
            h.Tick(50);
            Check(h.Warned.Count == 1 && h.Warned[0].Contains("didn't take the request") && !h.Use.HasPending, "well after the portal: said at once");

            // A plugin loaded in the world saw no arrival: said at once.
            h = With(Kit(0x10));
            h.World.RefuseSends = true;
            h.Tick();
            h.Slot(kit);
            h.Tick(50);
            Check(h.Warned.Count == 1 && !h.Use.HasPending, "no portal seen: said at once");

            // A spell slot: the cast waits the same way, after Magic mode.
            h = OutOfPortal();
            h.World.Known = new HashSet<uint> { 6 };
            h.World.CombatMode = Stance.Magic;
            Check(h.Use.Request(UseRequest.Cast(6, "Heal Self I", "slot", h.World.NowMs)), "a cast queued");
            h.Tick(50);
            h.Ticks(4, 250);
            Check(h.Use.InFlight && h.World.Sent.Count == 0 && h.Warned.Count == 0, "the cast is refused and waits");
            Eq(h.Said.Count(s => s.Contains("waiting for the game to finish the portal")), 1, "said once");
            h.World.RefuseSends = false;
            h.Tick(250);
            Eq(string.Join("|", h.World.Sent), "cast 6 me", "cast the moment the client takes it");
        }

        private static void UseStaleBusy()
        {
            ItemRef kit = ItemRef.From(Kit(0x10));

            // A login with a count the last session left: nothing moves it.
            Harness h = With(Kit(0x10));
            h.World.BusyCount = 2;
            h.Tick();   // the first look
            h.Slot(kit);
            h.Ticks(3, 1000);
            Eq(h.World.Sent.Count, 0, "right after the first look the count may be real: held");
            h.Tick(1000);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "unchanged for 4 s with nothing in flight: a leftover, the kit goes");
            Eq(h.Warned.Count, 0, "no warning");
            Eq(h.Logged.Count(l => l.Contains("busy count has read 2")), 1, "the leftover is logged once");
            Check(h.Logged.Any(l => l.Contains("left alone")), "and the log says the count isn't touched");
            Eq(h.Use.Busy.Leftover, 2, "2 is the leftover");

            // Our own kit raises it; the answer brings it back to the leftover; the next kit goes at once.
            h.World.BusyCount = 3;
            h.Tick(100);
            Check(h.Use.InFlight, "our kit is in flight");
            h.World.BusyCount = 2;
            h.World.Answer();
            h.Tick(100);
            Check(!h.Use.InFlight, "answered");
            h.Slot(kit);
            h.Tick(300);
            Eq(h.World.Sent.Count, 2, "the next kit goes at once: the count is back at the leftover");
            Eq(h.Logged.Count(l => l.Contains("busy count has read")), 1, "nothing new logged");
            h.World.Answer();
            h.Tick(100);

            // Something else raises it above the leftover: real again, until it too sits unchanged.
            h.World.BusyCount = 3;
            h.Tick(300);
            h.Slot(kit);
            h.Ticks(3, 1000);
            Eq(h.World.Sent.Count, 2, "a count above the leftover that just rose holds the kit");
            h.Tick(1000);
            Eq(h.World.Sent.Count, 3, "until it has sat unchanged for 4 s too");
            Eq(h.Logged.Count(l => l.Contains("busy count has read 3")), 1, "a bigger leftover gets its own line");
            h.World.Answer();
            h.Tick(100);
            h.World.BusyCount = 0;
            h.Tick(100);
            Eq(h.Use.Busy.Leftover, 0, "idle again: no leftover");

            // A death: the count and RynthAi's copy of it sit through the death portal.
            h = With(Kit(0x10));
            h.World.BusyCount = 1;
            h.World.BotWaitingOnServer = true;
            h.Tick();
            h.World.InWorld = false;
            h.Ticks(8, 1000);
            h.World.InWorld = true;
            h.Slot(kit);
            h.Tick(100);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "landed: the count and RynthAi's bit that sat through the portal hold nothing");
            Check(h.Logged.Any(l => l.Contains("RynthAi has read 'waiting on the server'")), "RynthAi's bit is logged as ignored too");

            // A request made in the portal is dropped and said so.
            h = With(Kit(0x10));
            h.World.InWorld = false;
            h.Slot(kit);
            h.Tick();
            Check(!h.Use.HasPending && h.World.Sent.Count == 0 && h.Warned.Count == 1 && h.Warned[0].Contains("portal space"), "a key in portal space: not used, and says why: " + string.Join(";", h.Warned));

            // A login starts the clocks again (ResetSession), portal space doesn't.
            h = With(Kit(0x10));
            h.World.BusyCount = 1;
            h.Tick();
            h.Ticks(6, 1000);
            h.Use.ResetSession();
            h.Slot(kit);
            h.Tick(100);
            Eq(h.World.Sent.Count, 0, "after a login the count is new again: held for now");
        }

        private static void UseTooBusyRetry()
        {
            ItemRef kit = ItemRef.From(Kit(0x10));
            Harness h = With(Kit(0x10));
            h.Slot(kit);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "sent");
            h.World.Answer(UseController.TooBusyError);
            h.Tick(100);
            Check(!h.Use.InFlight && h.Use.HasPending, "answered 'too busy': queued once more");
            h.Tick(500);
            Eq(h.World.Sent.Count, 1, "not straight away");
            h.Tick(600);
            Eq(h.World.Sent.Count, 2, "a second later, sent again");
            Check(h.Logged.Any(l => l.Contains("one more try")), "the log says it is the retry");
            h.World.Answer(UseController.TooBusyError);
            h.Tick(100);
            Check(!h.Use.InFlight && !h.Use.HasPending && h.Warned.Count == 1 && h.Warned[0].Contains("the server says the character is busy"),
                "busy again: said once, not retried again: " + string.Join(";", h.Warned));
            h.Ticks(5, 1000);
            Eq(h.World.Sent.Count, 2, "no loop");

            // Another answer: no retry.
            h = With(Kit(0x10));
            h.Slot(kit);
            h.Tick();
            h.World.Answer(0x0402);
            h.Tick(100);
            h.Ticks(3, 1000);
            Check(h.World.Sent.Count == 1 && !h.Use.HasPending, "another error is not retried");

            // A newer request while the first was in flight wins over the retry.
            h = With(Kit(0x10), Potion(0x20));
            h.Slot(kit);
            h.Tick();
            h.Slot(ItemRef.From(Potion(0x20)));
            h.World.Answer(UseController.TooBusyError);
            h.Tick(100);
            Check(h.Use.Pending?.Item?.Name == "Health Potion", "the newest wish wins over the retry");
            h.Tick(1100);
            Eq(string.Join("|", h.World.Sent), "useon 10 me|use 20", "the potion goes, the kit isn't sent again");
        }

        private static void UseUnanswered()
        {
            Harness h = With(Potion(0x20));
            ItemRef p = ItemRef.From(Potion(0x20));
            h.Slot(p);
            h.Tick();
            h.Tick(1000);
            Check(h.Use.InFlight, "1 s with no UseDone: still waiting");
            h.Tick(600);
            Check(!h.Use.InFlight, "the client is quiet and 1.5 s have passed: let go (a refusal sends no UseDone)");

            h = With(Potion(0x20));
            h.World.Animating = true;
            h.Slot(p);
            h.Tick();   // animating holds the send
            h.World.Animating = false;
            h.Tick(100);
            h.World.ClientBusy = true;
            h.Tick(2000);
            Check(h.Use.InFlight, "busy count up: not let go early");
            h.Tick(6500);
            Check(!h.Use.InFlight, "the 8 s limit lets go");
            Check(h.Logged.Any(l => l.Contains("no UseDone")), "and logs it");
        }

        private static void UseRebind()
        {
            ItemRef r = ItemRef.From(Kit(0x10));
            Harness h = With(Kit(0x11), Potion(0x20));
            h.Slot(r);
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "useon 11 me", "the slot's object is gone: the next kit of that name is used");

            h = With(Potion(0x20));
            h.Slot(r);
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Warned.Count == 1 && h.Warned[0] == "Peerless Healing Kit: you aren't carrying one.", "none left: says so, sends nothing: " + string.Join(";", h.Warned));
            Check(!h.Use.HasPending, "and doesn't wait for one");
        }

        private static void UseByNameAndTargets()
        {
            Harness h = With(Kit(3, "Lesser Stamina Kit"), Kit(4, "Greater Stamina Kit"));
            h.Named("greater stamina");
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "useon 4 me", "by the name typed");

            h = With(Kit(3));
            h.Named("Elixir");
            h.Tick();
            Check(h.Warned.Count == 1 && h.Warned[0] == "No item named 'Elixir' in your pack.", "no such item: " + string.Join(";", h.Warned));

            // Use on the selected target.
            h = With(Potion(0x20, "Mana Charge"));
            ItemRef r = ItemRef.From(h.World.Inventory[0]);
            h.Slot(r, UseMode.OnSelected);
            h.Tick();
            Check(h.Warned.Count == 1 && h.Warned[0].Contains("select a target first") && h.World.Sent.Count == 0, "no target selected: nothing sent");
            h.World.SelectedId = 0x1234;
            h.Slot(r, UseMode.OnSelected);
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "useon 20 1234", "used on the selected object");

            // A kit used by itself when the slot says so.
            h = With(Kit(0x10));
            h.Slot(ItemRef.From(Kit(0x10)), UseMode.Use);
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "use 10", "slot mode 'use it' overrides the kit rule");

            // The client turns the request away.
            h = With(Kit(0x10));
            h.World.RefuseSends = true;
            h.Slot(ItemRef.From(Kit(0x10)));
            h.Tick();
            Check(h.Warned.Count == 1 && h.Warned[0].Contains("didn't take") && !h.Use.InFlight, "a refused send is reported and not waited on");
        }

        private static void UseWield()
        {
            Harness h = With(Melee(0x32, "Sword"));
            ItemRef sword = ItemRef.From(h.World.Inventory[0]);
            h.Slot(sword);
            h.Tick();
            Eq(string.Join("|", h.World.Sent), "use 32", "a weapon is wielded with UseObject (the client's double-click)");
            Check(h.Said[0] == "Wielding Sword...", "status: " + h.Said[0]);
            h.Tick(100);
            Check(h.Use.InFlight && h.Wielded.Count == 0, "not in hand yet: waits for it (a wield sends no UseDone)");
            h.World.Answer();
            h.Tick(100);
            Check(h.Use.InFlight, "a UseDone does not end a wield; the item in hand does");
            h.World.Inventory[0].WieldedLocation = 0x00100000;
            h.Tick(100);
            Check(!h.Use.InFlight && h.Wielded.Count == 1 && h.Wielded[0].Id == 0x32, "in hand: done, and reported (the picker's recent list)");
            Check(h.Said[^1] == "Sword is in your hands.", "status: " + h.Said[^1]);

            // Already in hand: nothing is sent (a use would take it off).
            h = With(Melee(0x32, "Sword", wielded: true));
            h.Slot(ItemRef.From(h.World.Inventory[0]));
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Said.Count == 1 && h.Said[0] == "Sword is already in your hands.", "already wielded: no use sent: " + string.Join(";", h.Said));

            // Armor and rings are worn the same way and take no stance.
            var helm = Item(0x40, "Helm"); helm.ItemType = ItemKinds.TypeArmor; helm.ValidLocations = 1;
            h = With(helm);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(helm));
            h.Tick();
            helm.WieldedLocation = 1;
            h.Tick(100);
            Check(h.World.Sent.SequenceEqual(new[] { "use 40" }) && !h.Use.InFlight, "armor: worn, no stance change");

            // Pressing the weapon again while it is being wielded does not queue another wield.
            h = With(Melee(0x32, "Sword"));
            h.Slot(sword);
            h.Tick();
            Check(!h.Slot(sword) && !h.Use.HasPending, "the same wield again is ignored");
        }

        private static void UseWieldFails()
        {
            Harness h = With(Melee(0x32, "Sword"));
            ItemRef sword = ItemRef.From(h.World.Inventory[0]);
            h.Slot(sword);
            h.Tick();
            h.Tick(3000);
            Check(h.Use.InFlight, "3 s: still waiting");
            h.Tick(600);
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("not in your hands after"), "never wielded: reported once and released: " + string.Join(";", h.Warned));
            Eq(h.World.Sent.Count, 1, "no automatic retry loop");

            h = With(Melee(0x32, "Sword"));
            h.Slot(sword);
            h.Tick();
            h.World.RefusalSeq++;
            h.World.RefusedObject = 0x99;
            h.Tick(100);
            Check(h.Use.InFlight, "a refusal about another object is not ours");
            h.World.RefusedObject = 0x32;
            h.World.RefusalSeq++;
            h.Tick(100);
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("wouldn't let you wield"), "the server refused this wield: said at once: " + string.Join(";", h.Warned));
        }

        private static void UseBot()
        {
            Harness h = With(Melee(0x32, "Sword"), Kit(0x10));
            h.World.BotMacroRunning = true;
            ItemRef sword = ItemRef.From(h.World.Inventory[0]);
            h.Slot(sword);
            h.Tick();
            Check(h.World.Sent.Count == 0 && h.Warned.Count == 1 && h.Warned[0].Contains("RynthAi's macro is running"), "a weapon is not wielded under RynthAi's macro: " + string.Join(";", h.Warned));

            h.Slot(ItemRef.From(h.World.Inventory[1]));
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "a kit still works (that is the point of hands-on)");

            h = With(Melee(0x32, "Sword"));
            h.World.BotMacroRunning = true;
            h.Use.Settings.SwapWhileBotRuns = true;
            h.Slot(sword);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "allowed in the settings: wielded");

            h = With(Melee(0x32, "Sword"));
            h.World.BotMacroRunning = false;
            h.Slot(sword);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "macro stopped: wielded");

            var helm = Item(0x40, "Helm"); helm.ItemType = ItemKinds.TypeArmor; helm.ValidLocations = 1;
            h = With(helm);
            h.World.BotMacroRunning = true;
            h.Slot(ItemRef.From(helm));
            h.Tick();
            Eq(h.World.Sent.Count, 1, "armor is not a weapon swap: allowed");
        }

        private static void UseStance()
        {
            static void Wield(Harness h, CarriedItem item) => h.World.Inventory.First(i => i.Id == item.Id).WieldedLocation = 0x1;

            Check(Stance.Wanted(ItemKind.Melee, false, false, Stance.NonCombat) == 0, "peace stays peace");
            Check(Stance.Wanted(ItemKind.Melee, false, false, 0) == 0, "mode unknown: left alone");
            Eq(Stance.Wanted(ItemKind.WarWand, false, false, Stance.Melee), Stance.Magic, "a wand from melee: magic");
            Eq(Stance.Wanted(ItemKind.VoidWand, false, false, Stance.Missile), Stance.Magic, "a void wand: magic");
            Eq(Stance.Wanted(ItemKind.Melee, false, false, Stance.Magic), Stance.Melee, "melee from magic");
            Eq(Stance.Wanted(ItemKind.Missile, true, true, Stance.Melee), Stance.Missile, "a bow with arrows in hand: missile");
            Eq(Stance.Wanted(ItemKind.Missile, true, false, Stance.Melee), Stance.NonCombat, "a bow without arrows: peace, not a stance it can't use");
            Eq(Stance.Wanted(ItemKind.Missile, false, false, Stance.Melee), Stance.Missile, "thrown weapons need no ammunition");
            Eq(Stance.Wanted(ItemKind.Shield, false, false, Stance.Melee), 0, "a shield leaves the stance");
            Eq(Stance.Wanted(ItemKind.Armor, false, false, Stance.Magic), 0, "so does armor");

            // Melee -> wand.
            var wand = Wand(0x50, "Wand");
            Harness h = With(wand);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(wand));
            h.Tick();
            Wield(h, wand);
            h.Tick(100);
            Check(h.Use.InFlight && h.World.Sent.Count == 1, "wielded; the stance waits for the character to settle");
            h.Tick(300);
            Eq(h.World.Sent.Count, 1, "still settling");
            h.Tick(300);
            Eq(string.Join("|", h.World.Sent), "use 50|mode 8", "then magic");
            Check(!h.Use.InFlight && h.Said[^1] == "Wand is in your hands, magic stance.", "done: " + h.Said[^1]);

            // Busy delays the stance; the time limit reports.
            h = With(wand);
            wand.WieldedLocation = 0;
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(wand));
            h.Tick();
            Wield(h, wand);
            h.World.ClientBusy = true;
            h.Tick(100);
            h.Tick(1000);
            Eq(h.World.Sent.Count, 1, "busy: no stance request");
            h.World.ClientBusy = false;
            h.Tick(100);
            Eq(h.World.Sent.Last(), "mode 8", "the stance goes as soon as the character is free");

            h = With(wand);
            wand.WieldedLocation = 0;
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(wand));
            h.Tick();
            Wield(h, wand);
            h.World.Animating = true;
            h.Tick(100);
            h.Tick(4500);
            Check(!h.Use.InFlight && h.Warned.Count == 1 && h.Warned[0].Contains("magic stance wasn't taken"), "never free: reported, not stuck: " + string.Join(";", h.Warned));

            // Magic -> melee weapon.
            var sword = Melee(0x51, "Sword");
            h = With(sword);
            h.World.CombatMode = Stance.Magic;
            h.Slot(ItemRef.From(sword));
            h.Tick();
            Wield(h, sword);
            h.Tick(100);
            h.Tick(600);
            Eq(h.World.Sent.Last(), "mode 2", "from magic to melee");

            // Already in the right stance: no request.
            h = With(sword);
            sword.WieldedLocation = 0;
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(sword));
            h.Tick();
            Wield(h, sword);
            h.Tick(100);
            h.Tick(600);
            Check(h.World.Sent.SequenceEqual(new[] { "use 51" }) && !h.Use.InFlight, "the server already put us in melee: nothing sent");

            // Bow without arrows, then with.
            var bow = Bow(0x52);
            h = With(bow);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(bow));
            h.Tick();
            Wield(h, bow);
            h.Tick(100);
            h.Tick(600);
            Eq(h.World.Sent.Last(), "mode 1", "no arrows in hand: back to peace");
            Check(h.Said[^1].Contains("no ammunition"), "and it says why: " + h.Said[^1]);

            bow.WieldedLocation = 0;
            var arrows = Arrows(0x53, wielded: true);
            h = With(bow, arrows);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(bow));
            h.Tick();
            Wield(h, bow);
            h.Tick(100);
            h.Tick(600);
            Eq(h.World.Sent.Last(), "mode 4", "arrows in hand: missile");

            // At peace the character stays at peace.
            h = With(sword);
            sword.WieldedLocation = 0;
            h.World.CombatMode = Stance.NonCombat;
            h.Slot(ItemRef.From(sword));
            h.Tick();
            Wield(h, sword);
            h.Tick(100);
            Check(h.World.Sent.SequenceEqual(new[] { "use 51" }) && !h.Use.InFlight, "peace: no stance request");

            // A shield from melee keeps melee.
            var shield = Shield(0x54);
            h = With(shield);
            h.World.CombatMode = Stance.Melee;
            h.Slot(ItemRef.From(shield));
            h.Tick();
            Wield(h, shield);
            h.Tick(100);
            Check(h.World.Sent.SequenceEqual(new[] { "use 54" }), "a shield: stance untouched");
        }

        private static void UseReset()
        {
            Harness h = With(Kit(0x10));
            ItemRef kit = ItemRef.From(Kit(0x10));
            h.Slot(kit);
            h.Tick();
            h.Slot(kit);
            Check(h.Use.InFlight && h.Use.HasPending, "one in flight, one waiting");
            h.World.InWorld = false;
            h.Tick(100);
            Check(!h.Use.InFlight && !h.Use.HasPending, "portal space / logout: both dropped");
            h.World.InWorld = true;
            h.Tick(1000);
            Eq(h.World.Sent.Count, 1, "and nothing is sent afterwards");

            h = With(Kit(0x10));
            h.World.InWorld = false;
            h.Slot(kit);
            h.Tick();
            Check(!h.Use.HasPending && h.World.Sent.Count == 0, "a request made out of the world is dropped, not held for later");
        }
    }
}
