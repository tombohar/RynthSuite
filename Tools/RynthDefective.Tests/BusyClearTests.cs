using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthDefective;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;
using RynthCore.Plugin.RynthDefective.Views;

namespace RynthDefective.Tests
{
    // "Shortcuts clear a stuck busy state" (2026-11-04, DefSettings.ClearStuckBusy): when a press clears
    // the busy count first (BusyClearRule: only the count holds it, nothing real in flight), the use
    // controller's clear-then-use (once per press, logged), off by default, the setting, the command.
    internal static partial class Program
    {
        private static void BusyClearDecisions()
        {
            Check(!new DefSettings().ClearStuckBusy, "off by default");

            // A count of 2 that moved a second ago, nothing of ours for a long time, no gesture, no RynthAi.
            var f = new BusyFacts(Count: 2, CountSteadyMs: 1000, Leftover: 0, SinceOwnSendMs: 60_000,
                Animating: false, AnimatingMs: 0, BotWaiting: null, BotSteadyMs: 0);
            bool Clear(in BusyFacts x, BusyHold hold = BusyHold.Busy, bool on = true, bool inFlight = false, bool answered = true, long sinceBusy = 60_000) =>
                BusyClearRule.ShouldClear(on, hold, x, inFlight, answered, sinceBusy);

            Check(Clear(f), "only the count holds it: clear");
            Check(!Clear(f, on: false), "the option off: never");
            Check(!Clear(f, BusyHold.Animation) && !Clear(f, BusyHold.Bot) && !Clear(f, BusyHold.None), "held by something else, or not held: no");
            Check(!Clear(f with { Count = 0 }), "no count: nothing to clear");
            Check(!Clear(f, inFlight: true), "our own use in flight: no");
            Check(!Clear(f with { SinceOwnSendMs = 1000 }, answered: false), "our own last use, unanswered, just now: no (it may be the count)");
            Check(Clear(f with { SinceOwnSendMs = 1000 }, answered: true), "our own last use was answered: the count isn't it");
            Check(Clear(f with { SinceOwnSendMs = BusyRule.OwnTrustMs }, answered: false), "unanswered but long ago: not ours any more");
            Check(!Clear(f with { Animating = true, AnimatingMs = 100 }), "a gesture animating: no");
            Check(!Clear(f with { Animating = true, AnimatingMs = 60_000 }), "even one that has 'animated' for a minute: no");
            Check(!Clear(f with { BotPaced = true, BotPacedMs = 100 }), "RynthAi's own action unanswered: no");
            Check(!Clear(f with { BotPaced = true, BotPacedMs = 60_000 }), "even a stale one: no");
            Check(Clear(f with { BotPaced = false, BotWaiting = true, BotSteadyMs = 100 }),
                "RynthAi says it has nothing in flight: its 'waiting' bit is its copy of the count");
            Check(!Clear(f with { BotWaiting = true, BotSteadyMs = 1000 }), "a RynthAi that can't say, waiting on the server just now: no");
            Check(Clear(f with { BotWaiting = true, BotSteadyMs = BusyRule.BotTrustMs }), "its bit stuck for longer than any of its actions: yes");
            Check(!Clear(f, sinceBusy: 2000), "the server said 'too busy' 2 s ago: it really is busy, no");
            Check(Clear(f, sinceBusy: BusyClearRule.ServerBusyRecentMs), "5 s later: yes");
        }

        private static void UseClearStuckBusy()
        {
            ItemRef kit = ItemRef.From(Kit(0x10));

            // Off (the default): a login's count holds the press as before, and nothing is reset.
            Harness h = With(Kit(0x10));
            h.World.BusyCount = 2;
            h.Tick();
            h.Slot(kit);
            h.Ticks(3, 1000);
            Check(h.World.Sent.Count == 0 && h.World.Clears == 0, "off: held, nothing cleared");
            h.Tick(1000);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "off: it goes once the count is a leftover, as before");
            Eq(h.World.Clears, 0, "off: the count is never reset");

            // On: the clear, then the use as soon as the reset reads.
            h = With(Kit(0x10));
            h.Use.Settings.ClearStuckBusy = true;
            h.World.BusyCount = 2;
            h.Tick();
            h.Slot(kit);
            h.Tick(50);
            Check(h.World.Clears == 1 && h.World.Sent.Count == 0, "on: cleared first, nothing sent yet");
            Check(h.Logged.Any(l => l.Contains("held only by the client's busy count (2") && l.Contains("as /ra clearbusy does")), "the clear is logged: " + string.Join(";", h.Logged));
            Check(h.Said.Any(s => s.Contains("clearing a stuck busy state")), "and on the status line");
            h.Tick(50);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "the reset read: the kit goes at once");
            Eq(h.World.Clears, 1, "once for the press");
            Eq(h.Warned.Count, 0, "no warning");

            // The next press gets its own (once per press, not once ever).
            h.World.Answer();
            h.Tick(100);
            h.World.BusyCount = 1;   // something raises it again
            h.Tick(300);
            h.Slot(kit);
            h.Tick(50);
            Eq(h.World.Clears, 2, "a new press held by the count: one more clear");
            h.Tick(50);
            Eq(h.World.Sent.Count, 2, "and it goes");

            // The reset doesn't take: the press waits the settle time, then the count is a leftover and it goes.
            h = With(Kit(0x10));
            h.Use.Settings.ClearStuckBusy = true;
            h.World.ClearLands = false;
            h.World.BusyCount = 2;
            h.Tick();
            h.Slot(kit);
            h.Tick(50);
            h.Tick(100);
            Check(h.World.Clears == 1 && h.World.Sent.Count == 0, "the count still reads 2: waiting for the reset");
            h.Tick(UseController.ClearSettleMs);
            Eq(string.Join("|", h.World.Sent), "useon 10 me", "after the settle time it goes: what is left is a leftover");
            Eq(h.World.Clears, 1, "not cleared twice");
            Eq(h.Use.Busy.Leftover, 2, "2 is the leftover");

            // An engine that can't reset: logged, and the usual rules.
            h = With(Kit(0x10));
            h.Use.Settings.ClearStuckBusy = true;
            h.World.CanClear = false;
            h.World.BusyCount = 2;
            h.Tick();
            h.Slot(kit);
            h.Ticks(3, 1000);
            Check(h.World.Sent.Count == 0 && h.Logged.Count(l => l.Contains("can't reset it")) == 1, "can't clear: said once in the log, held as usual");
            h.Tick(1000);
            Eq(h.World.Sent.Count, 1, "and goes when the usual rules let it");

            // Never with something real in flight: a gesture, RynthAi's own action, the server's 'too busy'.
            h = With(Kit(0x10));
            h.Use.Settings.ClearStuckBusy = true;
            h.World.BusyCount = 2;
            h.World.Animating = true;
            h.Tick();
            h.Slot(kit);
            h.Ticks(3, 1000);
            Eq(h.World.Clears, 0, "a gesture animating: nothing cleared");

            h = With(Kit(0x10));
            h.Use.Settings.ClearStuckBusy = true;
            h.World.YieldOn = true;
            h.World.BotOwnAction = true;
            h.World.BusyCount = 2;
            h.Tick();
            h.Slot(kit);
            h.Ticks(4, 1000);
            Eq(h.World.Clears, 0, "RynthAi's own action in flight: nothing cleared");

            h = With(Kit(0x10));
            h.Use.Settings.ClearStuckBusy = true;
            h.Slot(kit);
            h.Tick();
            Eq(h.World.Sent.Count, 1, "sent");
            h.World.Answer(UseController.TooBusyError);
            h.World.BusyCount = 1;
            h.Tick(100);
            Check(h.Use.HasPending, "the server said 'too busy': one more try queued");
            h.Ticks(3, 1000);
            Eq(h.World.Clears, 0, "the server really is busy: nothing cleared for the retry");

            // Our own last use answered, the count still up a moment (the engine's count catches up later):
            // the next press may clear it.
            h = With(Kit(0x10), Potion(0x11));
            h.Use.Settings.ClearStuckBusy = true;
            h.Slot(kit);
            h.Tick();
            h.World.BusyCount = 1;
            h.Tick(100);
            h.World.Answer();
            h.Tick(100);
            Check(!h.Use.InFlight, "the kit was answered");
            h.Slot(ItemRef.From(Potion(0x11)));
            h.Tick(300);
            Eq(h.World.Clears, 1, "the potion: the count is no action of ours in flight, cleared");
            h.Tick(50);
            Eq(h.World.Sent.Count, 2, "and the potion goes");
        }

        private static void ClearBusySettingAndCommand()
        {
            // The character's file.
            var d = new DefData();
            d.Settings.ClearStuckBusy = true;
            Check(DefStore.Parse(JsonDocument.Parse(DefStore.ToJson(d)).RootElement).Settings.ClearStuckBusy, "saved and read: on");
            Check(!DefStore.Parse(JsonDocument.Parse("{\"settings\":{\"quantityBox\":true}}").RootElement).Settings.ClearStuckBusy, "a file from before: off");

            // The command.
            DefCommand c = DefCommandParser.Parse("shortcuts clearbusy on");
            Check(c.Verb == DefVerb.Shortcuts && c.Text == "on", "/rdef shortcuts clearbusy on");
            Check(DefCommandParser.Parse("shortcuts clearbusy off").Text == "off", "off");
            Check(DefCommandParser.Parse("shortcuts ClearBusy OFF").Text == "off", "any case");
            c = DefCommandParser.Parse("shortcuts clearbusy");
            Check(c.Verb == DefVerb.Shortcuts && c.Text == "", "alone: says what it is");
            foreach (string wrong in new[] { "shortcuts", "shortcuts clearbusy maybe", "shortcuts frob on", "shortcuts clearbusy on now" })
            {
                c = DefCommandParser.Parse(wrong);
                Check(c.Verb == DefVerb.Error && c.Error.Contains("Usage", StringComparison.Ordinal), $"'/rdef {wrong}' answers how it's typed: {c.Error}");
            }
            Check(DefReplies.ClearBusy(true).Contains(": on") && DefReplies.ClearBusy(true).Contains("/ra clearbusy"), "the on reply says what it does");
            Check(DefReplies.ClearBusy(false).Contains(": off (the default)"), "the off reply");

            // The settings window's box, and the script-window bar's.
            var b = new BarRig("clearbusy-settings");
            var host = new UiHost();
            UiWindow win = host.Add(new UiWindow("RynthDefective/Settings", "Defective Settings") { Visible = true, ShowInBar = false });
            var view = new BarSettingsView(b.S) { WindowShown = t => t == BarSettingsView.BarTitle ? true : null };
            win.Begin(); view.Draw(win); win.End();
            ParseAll(host, "settings window with the clear-busy box", out _);
            Check(Encoding.UTF8.GetString(win.Body).Contains("Shortcuts clear a stuck busy state"), "the box is in the settings");
            var w = new Windows("clearbusy-settings-events");
            w.Host.Add(win);
            int saves = b.R.Saves;
            w.SetBool(win, "set.clearbusy", true);
            Check(b.S.Settings.ClearStuckBusy && b.R.Saves > saves, "ticking it turns it on, saved");
            w.SetBool(win, "set.clearbusy", false);
            Check(!b.S.Settings.ClearStuckBusy, "and off");

            var t = new Windows("clearbusy-old-bar");
            var panel = new SettingsPanel(t.S, () => { });
            UiWindow pw = t.Host.Add(new UiWindow("RynthDefective/Old", "Old") { Visible = true });
            pw.Begin(); panel.Draw(pw); pw.End();
            Check(Encoding.UTF8.GetString(pw.Body).Contains("Shortcuts clear a stuck busy state"), "the script-window bar's settings have it too");
            t.SetBool(pw, "set.clearbusy", true);
            Check(t.S.Settings.ClearStuckBusy, "and it works there");
        }
    }
}
