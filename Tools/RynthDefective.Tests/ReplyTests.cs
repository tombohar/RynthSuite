using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.Shared;

namespace RynthDefective.Tests
{
    // Every /rdef command answers in chat (2026-10-28). The commands themselves need the game (the
    // plugin's host), so these check the shared reply queue and, verb by verb, the parse and the line
    // the plugin says for it.
    internal static partial class Program
    {
        /// <summary>The shared queue every plugin answers through (Plugins/Shared/CommandReply.cs).</summary>
        private static void ReplyQueue()
        {
            var chat = new List<(string Line, int Type)>();
            var log = new List<string>();
            bool accept = true;
            var r = new CommandReply("[RynthTest]", (t, c) => { if (!accept) return false; chat.Add((t, c)); return true; }, log.Add);

            // Nothing waiting: written at once.
            r.Ok("Bar shown.");
            r.Fail("Which slot? e.g. /rdef clear 3");
            r.Ok("[Mini Remote] already tagged");
            Eq(r.Pending, 0, "the host took every line at once");
            Eq(r.Count, 3, "three replies counted");
            Eq(chat.Count, 3, "all three written");
            Eq(chat[0], ("[RynthTest] Bar shown.", CommandReply.OkChatType), "ok: the tag in front, green");
            Eq(chat[1], ("[RynthTest] Which slot? e.g. /rdef clear 3", CommandReply.FailChatType), "fail: its own colour");
            Eq(chat[2].Line, "[Mini Remote] already tagged", "a line with its own tag keeps it");
            Check(log.Contains("[RynthTest] Which slot? e.g. /rdef clear 3"), "a failure is logged too");
            Check(!r.Flush(), "nothing left");

            // The host refuses (another plugin wrote within 100 ms): the line waits, and so does every
            // line after it (they stay in order); Flush offers them again, one per tick.
            chat.Clear();
            accept = false;
            r.Ok("Using Stamina Kit (slot 1).");
            r.Ok("second");
            for (int i = 0; i < 5; i++) r.Flush();
            Eq(r.Pending, 2, "refused lines wait");
            accept = true;
            r.Ok("third");
            Eq(chat.Count, 0, "a new line waits behind the refused ones, even when the host would take it");
            Check(r.Flush(), "the first goes out when the host takes it");
            Eq(r.Pending, 2, "one line per flush (the host drops a second one within 100 ms)");
            r.Flush(); r.Flush();
            Eq(string.Join("|", chat.ConvertAll(c => c.Line)), "[RynthTest] Using Stamina Kit (slot 1).|[RynthTest] second|[RynthTest] third", "in order");

            // A line the host never takes goes to the log after MaxTries, and the next one moves up.
            chat.Clear();
            accept = false;
            log.Clear();
            r.Ok("first");
            r.Ok("next");
            for (int i = 0; i < CommandReply.MaxTries; i++) r.Flush();
            Eq(r.Pending, 1, "the stuck line is dropped after MaxTries");
            Check(log.Exists(l => l.Contains("[RynthTest] first", StringComparison.Ordinal)), "and logged");
            accept = true;
            r.Flush();
            Eq(chat[^1].Line, "[RynthTest] next", "the next line moves up");

            // Line breaks: one chat line each, each tagged. Quiet: logged only. Blank: nothing.
            chat.Clear();
            log.Clear();
            int before = r.Count;
            r.Ok("line one\nline two");
            r.Ok("a script's success", quiet: true);
            r.Fail("  ");
            while (r.Flush()) { }
            Eq(chat.Count, 2, "two lines for a text with a line break");
            Eq(chat[1].Line, "[RynthTest] line two", "the second line tagged too");
            Check(log.Contains("[RynthTest] a script's success"), "quiet: in the log");
            Check(!chat.Exists(c => c.Line.Contains("script's success", StringComparison.Ordinal)), "quiet: not in chat");
            Eq(r.Count - before, 2, "quiet counts as an answer, a blank text doesn't");
            accept = false;
            r.Ok("x"); r.Clear();
            Eq(r.Pending, 0, "Clear forgets the waiting lines");
            Eq(CommandReply.Shown("Weapon picker", false), "Weapon picker hidden.", "the shared shown/hidden wording");
        }

        /// <summary>Each /rdef verb, typed right and typed wrong: what it answers.</summary>
        private static void ReplyEveryVerb()
        {
            // Wrong words: the parser's error is the reply, with an example or the usage.
            string[] wrong = { "use", "set", "set x", "set 0", "spell 3", "spell", "clear", "clear x", "bind 3", "bind", "unbind", "unbind x", "slots", "slots 99",
                "qty 0", "qty lots", "split 0", "give someone", "give -1", "buy 0 Mana Scarab", "bars remove", "bars slots 2", "bars frob", "shortcuts", "shortcuts clearbusy maybe", "daynight later" };
            foreach (string w in wrong)
            {
                DefCommand c = DefCommandParser.Parse(w);
                Check(c.Verb == DefVerb.Error && c.Error.Length > 0, $"'/rdef {w}' answers why");
                Check(c.Error.Contains("e.g.", StringComparison.Ordinal) || c.Error.Contains("Usage", StringComparison.Ordinal)
                      || c.Error.Contains("How many", StringComparison.Ordinal), $"'/rdef {w}' shows how it's typed: {c.Error}");
            }

            // Right words: every verb the parser knows maps to an answer.
            var right = new (string Typed, DefVerb Verb)[]
            {
                ("", DefVerb.Toggle), ("help", DefVerb.Help), ("list", DefVerb.List), ("status", DefVerb.Status),
                ("panels", DefVerb.Panels), ("settings", DefVerb.Settings), ("selection", DefVerb.Selection),
                ("selection 0x80001234", DefVerb.Selection), ("fav", DefVerb.Favourite), ("weapons", DefVerb.Weapons),
                ("weapons fire", DefVerb.Weapons), ("use 1", DefVerb.Use), ("use Stamina Kit", DefVerb.Use),
                ("set 2", DefVerb.Set), ("spell 3 Heal Self VI", DefVerb.Spell), ("clear 4", DefVerb.Clear),
                ("bind 3 F9", DefVerb.Bind), ("unbind 3", DefVerb.Unbind), ("slots 12", DefVerb.Slots),
                ("Stamina Kit", DefVerb.Use), ("corpses", DefVerb.Corpses), ("corpses clear", DefVerb.Corpses),
                ("qty", DefVerb.Quantity), ("qty 25", DefVerb.Quantity), ("split", DefVerb.Split), ("split 25 into Sack", DefVerb.Split),
                ("give 10", DefVerb.Give), ("buy 25 Mana Scarab", DefVerb.Buy), ("buy max", DefVerb.Buy),
                ("daynight", DefVerb.DayNight), ("daynight show", DefVerb.DayNight), ("daynight hide", DefVerb.DayNight),
                ("bars", DefVerb.Bars), ("bars add", DefVerb.Bars), ("bars remove 2", DefVerb.Bars), ("bars slots 2 12", DefVerb.Bars),
                ("bars show 3", DefVerb.Bars), ("shortcuts clearbusy on", DefVerb.Shortcuts), ("shortcuts clearbusy", DefVerb.Shortcuts),
            };
            var seen = new HashSet<DefVerb>();
            foreach (var (typed, verb) in right)
            {
                Eq(DefCommandParser.Parse(typed).Verb, verb, $"'/rdef {typed}'");
                seen.Add(verb);
            }
            foreach (DefVerb v in Enum.GetValues<DefVerb>())
                Check(v == DefVerb.Error || seen.Contains(v), $"the table covers /rdef's {v}");

            // The lines for the verbs that used to say nothing.
            Eq(DefReplies.Window("Shortcuts bar", 1), (true, "Shortcuts bar shown."), "toggle: shown");
            Eq(DefReplies.Window("Shortcuts bar", 0), (true, "Shortcuts bar hidden."), "toggle: hidden");
            Check(!DefReplies.Window("Selection window", -1).Ok, "a window the engine doesn't have: why not");
            var item = new DefSlot { Item = new ItemRef { Name = "Stamina Kit" } };
            Eq(DefReplies.UseSlot(item, 1), "Using Stamina Kit (slot 1).", "use: an item slot");
            var spell = new DefSlot { SpellId = 1, SpellName = "Strength Other I" };
            Check(DefReplies.UseSlot(spell, 2).StartsWith("Casting ", StringComparison.Ordinal), "use: a spell slot casts");
            Eq(DefReplies.UseName(" Peerless Healing Kit "), "Using Peerless Healing Kit.", "use by name");
            Eq(DefReplies.Weapons(true, "fire"), "Weapon picker shown, searching 'fire'.", "weapons with a search");
            Eq(DefReplies.Weapons(false, ""), "Weapon picker hidden.", "weapons: hidden");
            Check(DefReplies.TryParseSelection("0x80001234", out uint id, out _) && id == 0x80001234, "selection: a hex id");
            Check(!DefReplies.TryParseSelection("bob", out _, out string why) && why.Contains("Usage", StringComparison.Ordinal), "selection: not an id, the usage");
            Check(DefReplies.Selected(0x80001234, true, "Pack").Ok, "selection: selected");
            Check(!DefReplies.Selected(0x80001234, false, null).Ok, "selection: the game said no");
            Check(DefReplies.NoStack.Length > 0, "selection stack: none to select");
        }
    }
}
