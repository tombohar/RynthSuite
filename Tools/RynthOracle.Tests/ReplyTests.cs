using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Views;

namespace RynthOracle.Tests
{
    // Every /ro command answers in chat (2026-10-28). RunCommand needs the plugin's host (its windows),
    // so the parse of every /ro word and the line each verb says are checked here.
    internal static partial class Program
    {
        private static void Replies()
        {
            var words = new (string Typed, OracleVerb Verb, string Tab)[]
            {
                ("", OracleVerb.Window, ""), ("void", OracleVerb.Void, ""), ("timers", OracleVerb.Timers, ""), ("timer", OracleVerb.Timers, ""),
                ("quests", OracleVerb.Quests, ""), ("refresh", OracleVerb.Quests, ""),
                ("fellowship", OracleVerb.Tab, OracleView.TabFellowship), ("fellow", OracleVerb.Tab, OracleView.TabFellowship), ("fship", OracleVerb.Tab, OracleView.TabFellowship),
                ("character", OracleVerb.Tab, OracleView.TabCharacter), ("char", OracleVerb.Tab, OracleView.TabCharacter),
                ("legendary", OracleVerb.Trackers, OracleView.TabQuests), ("legend", OracleVerb.Trackers, OracleView.TabQuests), ("john", OracleVerb.Trackers, OracleView.TabQuests),
                ("markers", OracleVerb.Trackers, OracleView.TabQuests), ("marker", OracleVerb.Trackers, OracleView.TabQuests), ("trackers", OracleVerb.Trackers, OracleView.TabQuests),
                ("skills", OracleVerb.Tab, OracleView.TabSkills), ("skill", OracleVerb.Tab, OracleView.TabSkills),
                ("attributes", OracleVerb.Tab, OracleView.TabAttributes), ("attrs", OracleVerb.Tab, OracleView.TabAttributes),
                ("attribute", OracleVerb.Tab, OracleView.TabAttributes), ("vitals", OracleVerb.Tab, OracleView.TabAttributes),
                ("progress", OracleVerb.Tab, OracleView.TabProgress), ("progression", OracleVerb.Tab, OracleView.TabProgress),
                ("status", OracleVerb.Status, ""), ("about", OracleVerb.About, OracleView.TabAbout), ("help", OracleVerb.Help, ""), ("?", OracleVerb.Help, ""),
                ("STATUS", OracleVerb.Status, ""), ("questz", OracleVerb.Unknown, ""), ("dynamic", OracleVerb.Unknown, ""),
            };
            var seen = new HashSet<OracleVerb>();
            foreach (var (typed, verb, tab) in words)
            {
                OracleCommand c = OracleCommand.Parse(typed);
                Check(c.Verb == verb && c.Tab == tab, $"'/ro {typed}': {verb} {tab} (got {c.Verb} {c.Tab})");
                seen.Add(c.Verb);
            }
            foreach (OracleVerb v in Enum.GetValues<OracleVerb>()) Check(seen.Contains(v), $"the table covers /ro's {v}");
            Check(OracleCommand.Parse("legendary").Trackers == QuestKind.Legendary && OracleCommand.Parse("markers").Trackers == QuestKind.Marker
                  && OracleCommand.Parse("trackers").Trackers == null, "the trackers each word shows");
            Check(OracleCommand.Parse("void").ShowsWindow && !OracleCommand.Parse("status").ShowsWindow && !OracleCommand.Parse("quests").ShowsWindow,
                "which verbs show a window (they say why not without plugin windows)");

            // The lines.
            Check(OracleCommand.Shown("Oracle window", true) == "Oracle window shown.", "/ro: shown");
            Check(OracleCommand.Shown("Void window", false) == "Void window hidden.", "/ro void: hidden");
            Check(OracleCommand.OnTab(OracleView.TabSkills) == "Oracle window shown on its Skills tab.", "a tab");
            foreach (QuestKind? k in new QuestKind?[] { QuestKind.Legendary, QuestKind.Marker, null })
                Check(OracleCommand.OnTrackers(k).StartsWith("Oracle window shown on the Quests tab", StringComparison.Ordinal), $"trackers {k}");
            Check(OracleCommand.Quests(true, true, 0) == (true, "Reading your quest flags (/myquests)..."), "quests: sent");
            Check(!OracleCommand.Quests(false, false, 0).Ok, "quests: not in the world");
            var wait = OracleCommand.Quests(true, false, 42);
            Check(!wait.Ok && wait.Text.Contains("42s"), "quests: the server's minute, with the wait");
            Check(!OracleCommand.Quests(true, false, 0).Ok, "quests: one already out");
            string unknown = OracleCommand.Unknown("questz");
            Check(unknown.StartsWith("Unknown '/ro questz'.", StringComparison.Ordinal) && unknown.Contains("/ro status"), "an unknown word: named, with the usage");
            Check(OracleCommand.NoWindows.Contains("can't show plugin windows"), "no plugin windows: why");
        }
    }
}
