using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthSort;

namespace RynthSort.Tests
{
    // Every /rsort command answers in chat (2026-10-28). RunCommand needs the plugin's host, so the parse
    // of every /rsort word, which ones wait for the login, and the lines are checked here.
    internal static partial class Program
    {
        private static void Replies()
        {
            var words = new (string Typed, SortVerb Verb, bool Quiet, int? Pack)[]
            {
                ("", SortVerb.Window, false, null), ("on", SortVerb.On, false, null), ("off", SortVerb.Off, false, null),
                ("on quiet", SortVerb.On, true, null), ("OFF Quiet", SortVerb.Off, true, null),
                ("now", SortVerb.Now, false, null), ("tidy", SortVerb.Now, false, null), ("clean", SortVerb.Clean, false, null),
                ("unpack", SortVerb.Unpack, false, null), ("unpack 2", SortVerb.Unpack, false, 2), ("unpack x", SortVerb.Unpack, false, null),
                ("unpack 0", SortVerb.Unpack, false, null), ("status", SortVerb.Status, false, null), ("help", SortVerb.Help, false, null),
                ("?", SortVerb.Help, false, null), ("sortt", SortVerb.Unknown, false, null),
            };
            var seen = new HashSet<SortVerb>();
            foreach (var (typed, verb, quiet, pack) in words)
            {
                SortCommand c = SortCommand.Parse(typed);
                Check(c.Verb == verb && c.Quiet == quiet && c.Pack == pack, $"'/rsort {typed}': {verb} quiet={quiet} pack={pack} (got {c.Verb} {c.Quiet} {c.Pack})");
                seen.Add(c.Verb);
            }
            foreach (SortVerb v in Enum.GetValues<SortVerb>()) Check(seen.Contains(v), $"the table covers /rsort's {v}");
            Check(!SortCommand.Parse("").NeedsLogin && !SortCommand.Parse("help").NeedsLogin, "the window and help work before login");
            Check(SortCommand.Parse("now").NeedsLogin && SortCommand.Parse("sortt").NeedsLogin, "the rest says 'Log in first.' before login");

            Eq(SortCommand.Window(true), "RynthSort window shown.", "/rsort: shown");
            Eq(SortCommand.Started(SortVerb.Now), "Sorting every pack now.", "now / tidy");
            Check(SortCommand.Started(SortVerb.Clean).StartsWith("Cleaning", StringComparison.Ordinal), "clean");
            Check(SortCommand.Unpacking(3).StartsWith("Emptying pack 3", StringComparison.Ordinal), "unpack 3");
            Check(SortCommand.Unpacking(null).StartsWith("Emptying your side packs", StringComparison.Ordinal), "unpack");
            string unknown = SortCommand.Unknown("sortt");
            Check(unknown.StartsWith("Unknown '/rsort sortt'.", StringComparison.Ordinal) && unknown.Contains(SortCommand.Usage), "an unknown word: named, with the usage");
        }
    }
}
