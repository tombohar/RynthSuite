using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;

namespace RynthOracle.Tests
{
    // The 0.3.0 port of SilentKelpie's RynthOracle 0.6.0 (RynthSuite PR 12): the merged Quests list
    // (Legendary, Markers), Go to and the CSV loaders. Ported from his tests where they fit, adapted where
    // our tree differs (the Refresh row, the Mini Remote keeping its tabs), and added for what the merge
    // must keep (favourites, click-to-print, the Markers tab's content). Its Infinite Leaftide parts (the
    // Dynamic Stamps, the Temple guardians, the Give Parchment gate, the RynthILT profile, Charms and Pet
    // Breeding) moved to RynthILT on 2026-10-21, their tests to RynthILT.Tests (StampTests).
    internal static partial class Program
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        private static uint _seq = 5000;

        private static string Body(UiWindow w) => Encoding.UTF8.GetString(w.Body);

        /// <summary>One draw of the main window, parsed by the engine's own parser (ops under the limit), body returned.</summary>
        private static string Frame(UiHost host, UiWindow main, OracleView view, string what, out int ops)
        {
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, what, out ops);
            string body = Body(main);
            main.MarkSubmitted(true);
            return body;
        }

        private static string Frame(UiHost host, UiWindow main, OracleView view, string what) => Frame(host, main, view, what, out _);

        private static void Click(UiHost host, UiWindow w, string id) => Apply(host, Event(1, ++_seq, w.Hash, w.KeyOf(id), new byte[] { 1, 0, 0 }));

        private static void Tick(UiHost host, UiWindow w, string id, bool on) => Apply(host, Event(2, ++_seq, w.Hash, w.KeyOf(id), new byte[] { (byte)(on ? 1 : 0) }));

        private static void Type(UiHost host, UiWindow w, string id, string text)
        {
            byte[] t = Encoding.UTF8.GetBytes(text);
            var payload = new byte[3 + t.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)t.Length);
            t.CopyTo(payload, 3);
            Apply(host, Event(5, ++_seq, w.Hash, w.KeyOf(id), payload));
        }

        /// <summary>A context on a given server (its id, or "" for retail) with its catalog loaded.</summary>
        private static OracleContext On(string id)
        {
            OracleContext c = Context();
            c.Server.Force(id, id.Length > 0 ? id : "Coldeve");
            c.Catalog.Load(TestPacks.ById(id), null);
            c.Catalog.SyncDiscoveries(c.Flags);
            return c;
        }

        // ── The CSV lists ────────────────────────────────────────────────────

        /// <summary>The Legendary list (legendary.csv) and its have / missing / timer rules.</summary>
        private static void Legendary()
        {
            Check(LegendaryList.All.Count == 40, $"legendary quests: {LegendaryList.All.Count} (40 expected)");
            Check(LegendaryList.Groups.SequenceEqual(new[] { "Graveyard", "Rynthid", "Hoshino", "Frozen Valley", "Neftet", "Random Locations" }),
                  "legendary groups: " + string.Join(",", LegendaryList.Groups));
            Check(LegendaryList.All.All(q => q.Flags.Length > 0 && q.Luminance > 0), "every legendary quest has a flag and luminance");
            var catalog = new QuestCatalog();
            catalog.Load(null, _ => { });
            var unknown = LegendaryList.All.SelectMany(q => q.Flags).Where(f => !catalog.TryGet(f, out _)).ToList();
            Check(unknown.Count == 0, "every legendary flag is a known quest flag: " + string.Join(",", unknown));
            LegendaryQuest rtw = LegendaryList.All.First(q => q.CommonName == "RTW");
            Check(rtw.Flags[0] == "rtwcompleted_1013" && rtw.Label == "Defeating the Curator of Torment (RTW)", "RTW row: " + rtw.Label);

            var lf = new QuestFlagStore();
            long t = 900_000;
            long done = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            long old = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
            // RTW done 2 h ago on a 20 h timer; End of Days done 3 days ago (ready); Lugian Assault never.
            lf.OnChatLine($"rtwcompleted_1013 - 4 solves ({done})\"RTW\" -1 72000", t);
            lf.OnChatLine($"eodcompleted_1013 - 1 solves ({old})\"EoD\" -1 72000", t + 1);
            lf.OnChatLine($"legendaryquestcounter_0913 - 12 solves ({old})\"John\" -1 0", t + 2);
            lf.Tick(t + 5_000);
            DateTime now = DateTime.UtcNow;
            TimeSpan? rtwLeft = rtw.TimeLeft(lf, now);
            Check(rtw.Have(lf) && rtwLeft is { } l && l.TotalHours > 17.9 && l.TotalHours < 18.1, $"RTW: done, ~18 h left ({rtwLeft})");
            LegendaryQuest eod = LegendaryList.All.First(q => q.CommonName == "EoD");
            Check(eod.Have(lf) && eod.Status(lf, now) == "ready", "EoD: done, timer up -> ready: " + eod.Status(lf, now));
            LegendaryQuest la = LegendaryList.All.First(q => q.CommonName == "LA");
            Check(!la.Have(lf) && la.TimeLeft(lf, now) == null && la.Status(lf, now) == "missing", "Lugian Assault: missing");
            Check(LegendaryList.JohnCount(lf) == 12, "John's counter read: " + LegendaryList.JohnCount(lf));
            Check(LegendaryList.JohnCount(new QuestFlagStore()) == null, "no counter on a fresh character");

            // A quest with a second timer flag: the longest wait of the two counts.
            LegendaryQuest erq = LegendaryList.All.First(q => q.Name == "Empyrean Rescue Quest");
            var ef = new QuestFlagStore();
            ef.OnChatLine($"empyreanrescuecompleted_1010 - 1 solves ({old})\"ERQ\" -1 0", t);
            ef.OnChatLine($"erqlegkeypickuptimer - 1 solves ({done})\"key\" -1 36000", t + 1);
            ef.Tick(t + 5_000);
            TimeSpan? erqLeft = erq.TimeLeft(ef, now);
            Check(erqLeft is { } e && e.TotalHours > 7.9 && e.TotalHours < 8.1, $"ERQ: key pickup timer decides (~8 h): {erqLeft}");
        }

        /// <summary>Coordinates in a quest's notes become RynthNav targets (Go to).</summary>
        private static void GoToParsing()
        {
            var grael = NavTargets.Stops("Give Progenitor's Ichor to Hoshar. - Speak with Hoshar ibn Jalaq 59.6S 88.0W - Kill Mukkir Progenitor 86.5N 57.3E"
                                         + " - Speak with Hoshar ibn Jalaq - Abyssal Totem Gateway 86.5N 57.3E - Abyssal Totem Temple 86.5N 57.3E");
            Check(string.Join(" | ", grael.ConvertAll(s => s.Display)) == "Speak with Hoshar ibn Jalaq - 59.6S, 88.0W | Kill Mukkir Progenitor - 86.5N, 57.3E",
                  "Splitting Grael stops: " + string.Join(" | ", grael.ConvertAll(s => s.Display)));
            Check(grael[0].Target == "59.6S, 88.0W Speak with Hoshar ibn Jalaq", "a stop's RynthNav target is the coordinates, then its label");
            var shadow = NavTargets.Stops("Xarabydun 41.8S 15.8E - Speak with Ciandra Arcanum Alchemist - Empyrean Workshop 66.9N 15.9E - Cragstone 26.0N 47.0E Speak with Bretself the Translator - Xarabydun - Shade of Farelaith 74.3N 58.6E");
            Check(string.Join(" | ", shadow.ConvertAll(s => s.Label)) == "Xarabydun | Empyrean Workshop | Cragstone | Shade of Farelaith",
                  "Shadowfire Stone labels: " + string.Join(" | ", shadow.ConvertAll(s => s.Label)));
            Check(NavTargets.Stops("Kill 20 drudges near Holtburg.").Count == 0, "no coordinates: no stops");
            Check(NavTargets.Stops(null).Count == 0 && NavTargets.Stops("").Count == 0, "no text: no stops");
            Check(NavTargets.Stops("Meet at 42.6n 62.4e").Count == 1 && NavTargets.Stops("Meet at 42.6n 62.4e")[0].Display == "Meet - 42.6N, 62.4E",
                  "lower-case letters; 'at' dropped from the label");
            Check(NavTargets.FirstCoords("go to 8.8S, 53.1E now") == "8.8S, 53.1E" && NavTargets.FirstCoords("nowhere") == null, "the first coordinates in a text");
            Check(NavTargets.WithoutCoords("Hall 8.8S, 53.1E door").Contains("Hall") && !NavTargets.WithoutCoords("Hall 8.8S, 53.1E door").Contains("53.1"),
                  "the text without its coordinates");
            Check(NavTargets.Place("  Holtburg ").Target == "Holtburg", "a named place is its own target");
        }

        /// <summary>The Legendary loader: counts from the shipped file, and a malformed file doesn't cost the good rows.</summary>
        private static void Loaders()
        {
            // The shipped files: counts, and they are embedded resources (the way the other lists are).
            Check(LegendaryList.All.Count == 40, "the shipped list loads: 40 Legendary quests");
            Check(MarkerList.All.Count == 100, "the shipped marker list still has its 100 markers");

            var rows = LegendaryList.Parse(new StringReader(
                "Group,Quest,CommonName,Luminance,Flags,Url,Note,Video\n"
                + "\"G\",\"Good\",\"GD\",5000,\"a_flag;B_Flag\",\"https://x\",\"a note, with a comma\",\"\"\n"
                + "\"G\",\"\",\"\",1,\"x\",\"\",\"\",\"\"\n"                     // no quest name: skipped
                + "\"G\",\"ShortRow\",\"\",abc\n"                              // short row, luminance isn't a number
                + "\n"
                + "just one field\n"
                + "\"G\",\"After\",\"\",7,\"c_flag\"\n"));
            Check(rows.Count == 3 && rows[0].Name == "Good" && rows[1].Name == "ShortRow" && rows[2].Name == "After",
                  "legendary: bad rows skipped, the rest kept: " + string.Join(",", rows.Select(r => r.Name)));
            Check(rows[0].Flags.SequenceEqual(new[] { "a_flag", "b_flag" }) && rows[0].Luminance == 5000 && rows[0].Note == "a note, with a comma"
                  && rows[0].Label == "Good (GD)", "legendary: flags lower-cased and split, quoted commas kept, the short name in the label");
            Check(rows[1].Luminance == 0 && rows[1].Flags.Length == 0 && rows[1].Url == "", "legendary: a short row reads as empty, luminance 0");
            Check(LegendaryList.Parse(new StringReader("")).Count == 0 && LegendaryList.Parse(new StringReader("Group,Quest\n")).Count == 0,
                  "legendary: an empty file is an empty list");
            Check(LegendaryList.Parse(new StringReader("Quest\r\nCRLF Quest\r\n")).Single().Name == "CRLF Quest", "legendary: CRLF lines");

            // The markers file is the existing loader; a marker row still parses to its flag bit.
            Marker m = MarkerList.All[0];
            Check(m.Number == 1 && m.Flag.StartsWith("explorationmarkersfoundingroup") && m.BitMask == 1, "marker 1 keeps its quest flag bit");
        }

        // ── The merged Quests list ───────────────────────────────────────────

        /// <summary>The "N shown" count in a page body.</summary>
        private static int Shown(string body)
        {
            int at = body.IndexOf(" shown", StringComparison.Ordinal);
            if (at < 0) return -1;
            int s = at;
            while (s > 0 && char.IsDigit(body[s - 1])) s--;
            return int.TryParse(body.AsSpan(s, at - s), out int n) ? n : -1;
        }

        /// <summary>Each source appears in the one list, per server, with its filters; Trackers only and Show narrow it.</summary>
        private static void MergedList()
        {
            // Retail: quests, Legendary quests and markers; no stamps or guardians.
            OracleContext c = On("");
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            c.Settings.TabName = OracleView.TabQuests;
            int quests = c.Catalog.Quests.Count;
            int leg = LegendaryList.All.Count, mrk = MarkerList.All.Count;

            Check(view.AvailableKinds().SequenceEqual(new[] { QuestKind.Quest, QuestKind.Legendary, QuestKind.Marker }), "retail kinds: quests, Legendary, markers");
            string body = Frame(host, main, view, "Quests, retail, everything");
            Check(Shown(body) == quests + leg + mrk, $"retail: quests + Legendary + markers in one list ({Shown(body)} shown, {quests + leg + mrk} expected)");
            Check(body.Contains("###q.trackers") && body.Contains("###q.kind") && body.Contains("###q.sort") && body.Contains("###q.timers"),
                  "Trackers only, Show, Sort and the Quest Timers button are on the page");
            Check(body.Contains("###refresh.Quests"), "the Refresh row every page has is still on the Quests page");

            // Trackers only: the quests drop out, the trackers' status shows above.
            Tick(host, main, "q.trackers", true);
            body = Frame(host, main, view, "Quests, trackers only");
            Check(Shown(body) == leg + mrk, $"Trackers only: Legendary + markers ({Shown(body)})");
            Check(body.Contains("toward John") && body.Contains("markers found") && body.Contains("LEG") && !body.Contains("###q.r.30minattributes"),
                  "Trackers only: John's counter and the markers' progress above, no quests");
            Check(!body.Contains("###dy.area") && !body.Contains("Dynamic stamps"), "retail: no stamp status or area picker");
            Tick(host, main, "q.trackers", false);

            // Show: one kind per source.
            foreach ((QuestKind kind, int want) in new[] { (QuestKind.Quest, quests), (QuestKind.Legendary, leg), (QuestKind.Marker, mrk) })
            {
                view.ShowTrackers(kind);
                body = Frame(host, main, view, "Quests, " + kind);
                Check(Shown(body) == want, $"Show {kind}: {Shown(body)} shown ({want} expected)");
            }

            // Done / Not done on markers: the Markers tab's "only the ones still to find".
            long at = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            c.Flags.OnChatLine($"explorationmarkersfoundingroupa - 3 solves ({at})\"Markers\" 1023 0", c.NowMs);
            c.Flags.Tick(c.NowMs + 5_000);
            view.ShowTrackers(QuestKind.Marker);
            body = Frame(host, main, view, "Markers, none filtered");
            Check(Shown(body) == 100 && body.Contains("2 / 100 markers found"), "markers: bits 1 and 2 of group A found: " + (body.Contains("markers found") ? "status shown" : "no status"));
            Tick(host, main, "q.notdone", true);
            body = Frame(host, main, view, "Markers, not done");
            Check(Shown(body) == 98, $"markers, Not done: the ones still to find ({Shown(body)})");
            Tick(host, main, "q.notdone", false);
            Tick(host, main, "q.done", true);
            body = Frame(host, main, view, "Markers, done");
            Check(Shown(body) == 2, $"markers, Done: the two found ({Shown(body)})");
            Tick(host, main, "q.done", false);

            // Repeatable / One-time: Legendary rows repeat, markers don't.
            view.ShowTrackers(null);
            Tick(host, main, "q.rep", true);
            body = Frame(host, main, view, "Trackers, repeatable");
            Check(Shown(body) == leg, $"Repeatable: the Legendary quests ({Shown(body)})");
            Tick(host, main, "q.rep", false);
            Tick(host, main, "q.one", true);
            body = Frame(host, main, view, "Trackers, one-time");
            Check(Shown(body) == mrk, $"One-time: the markers ({Shown(body)})");
            Tick(host, main, "q.one", false);

            // The quest-only filters leave the trackers out.
            Tick(host, main, "q.new", true);
            body = Frame(host, main, view, "Trackers, Not in list");
            Check(Shown(body) == 0, "Not in list is a quests-only filter: no tracker rows");
            Tick(host, main, "q.new", false);

            // Search reaches the trackers' text (the notes, places, flags).
            Type(host, main, "q.search", "mhoire");
            body = Frame(host, main, view, "Trackers, search");
            int wantSearch = LegendaryList.All.Count(l => $"{l.Label} {l.Group} {l.Note} {string.Join(" ", l.Flags)}".Contains("mhoire", StringComparison.OrdinalIgnoreCase))
                             + MarkerList.All.Count(m => $"#{m.Number} {m.Name} {m.Location} {m.Hint}".Contains("mhoire", StringComparison.OrdinalIgnoreCase));
            Check(Shown(body) == wantSearch && wantSearch > 0, $"Search \"mhoire\" over the trackers: {Shown(body)} shown ({wantSearch} expected)");
            Type(host, main, "q.search", "");

            // Favourites: tracking a quest (the * list) also marks the Legendary row of the same flag.
            LegendaryQuest rtw = LegendaryList.All.First(q => q.CommonName == "RTW");
            c.Book.SetTracked(rtw.Flags[0], true);
            Tick(host, main, "q.tracked", true);
            body = Frame(host, main, view, "Trackers, tracked");
            Check(Shown(body) == 1 && body.Contains("###q.r.lg:" + rtw.Name), $"Tracked: the tracked quest's Legendary row ({Shown(body)})");
            view.ShowTrackers(QuestKind.Quest);
            body = Frame(host, main, view, "Quests, tracked");
            Check(Shown(body) == 3 && body.Contains("###q.t.30minattributes"), $"Tracked quests are still the favourites (the * list): {Shown(body)} shown");
            Tick(host, main, "q.tracked", false);
            c.Book.SetTracked(rtw.Flags[0], false);

            // Sorts do not throw and keep every row.
            view.ShowTrackers(null);
            foreach (int sort in new[] { 1, 2, 3, 4, 5, 0 })
            {
                Apply(host, Event(3, ++_seq, main.Hash, main.KeyOf("q.sort"), BitConverter.GetBytes(sort)));
                body = Frame(host, main, view, "Trackers, sort " + sort);
                Check(Shown(body) == leg + mrk, $"sort {sort} keeps every row ({Shown(body)})");
            }

            // Infinite Leaftide's stamps and guardians are RynthILT's own page (2026-10-21): the list keeps the
            // same three kinds on every server, and nothing of theirs is drawn.
            Check(view.AvailableKinds().Count == 3 && Enum.GetValues<QuestKind>().Length == 3, "three kinds of row, on every server");

            // A saved tab name of an old tab opens Quests; the Markers / Legendary / Dynamic Stamps / Pet Breeding tabs are gone.
            foreach (string old in new[] { "Markers", "Dynamic Stamps", "Pet Breeding" })
            {
                c.Settings.TabName = old;
                Check(view.Tab == OracleView.TabQuests, $"a saved \"{old}\" tab opens Quests");
            }
            string[] tabs = view.VisibleTabs();
            Check(!tabs.Contains("Markers") && !tabs.Contains("Legendary") && !tabs.Contains("Dynamic Stamps") && !tabs.Contains("Pet Breeding"),
                  "no separate tracker tabs, no Pet Breeding: " + string.Join(",", tabs));
        }

        /// <summary>Everything the old Markers tab showed is reachable from the Quests list.</summary>
        private static void MarkersReachable()
        {
            OracleContext c = On("");
            var printed = new List<string>();
            c.PrintSink = printed.Add;
            var navs = new List<string>();
            c.SendNav = line => { navs.Add(line); return true; };
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            c.Settings.TabName = OracleView.TabQuests;
            long at = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            c.Flags.OnChatLine($"explorationmarkersfoundingroupa - 1 solves ({at})\"Markers\" 1023 0", c.NowMs);
            c.Flags.Tick(c.NowMs + 5_000);

            view.ShowTrackers(QuestKind.Marker);            // /ro markers
            string body = Frame(host, main, view, "Markers");
            Check(body.Contains("1 / 100 markers found"), "the progress bar: markers found of 100");
            Marker next = MarkerList.All[1];
            Check(body.Contains("Next marker:") && body.Contains($"#{next.Number} {next.Name} ({next.Location})"), "the next marker to run to");
            Check(body.Contains("###q.r.mk:1") && body.Contains("###q.r.mk:2"), "a row per marker");

            // "Next marker" opens that marker, and a click prints the directions in chat (the old tab did).
            Click(host, main, "m.next");
            Check(printed.Count >= 2 && printed[0].StartsWith($"Marker {next.Number}: {next.Name}") && printed.Contains(next.Hint), "a click prints the marker and its directions: " + string.Join(" | ", printed.Take(3)));
            body = Frame(host, main, view, "Marker opened");
            Check(body.Contains("Marker 2") && body.Contains("not found yet") && body.Contains("###q.nav.go") && body.Contains("###m.sel.print") && body.Contains("###m.sel.close"),
                  "the marker opens below the list: status, Go to, Print, Close");
            Click(host, main, "q.nav.go");
            Check(navs.Count == 1 && navs[0].StartsWith("go "), "Go to sends RynthNav a go: " + string.Join("|", navs));
            Click(host, main, "q.nav.route");
            Click(host, main, "q.nav.arrow");
            Check(navs.Count == 3 && navs[1].StartsWith("route ") && navs[2].StartsWith("arrow "), "Route and Arrow likewise");

            // Print in chat from the panel; a second click on the row closes it (and prints again).
            printed.Clear();
            Click(host, main, "m.sel.print");
            Check(printed.Count >= 2, "Print in chat from the opened marker");
            printed.Clear();
            Click(host, main, "q.r.mk:2");
            body = Frame(host, main, view, "Marker closed");
            Check(printed.Count >= 2 && !body.Contains("###m.sel.close"), "a second click on the row prints again and closes it");
        }

        /// <summary>Opening a Legendary quest, a marker or a quest with coordinates in its notes offers RynthNav's Go to.</summary>
        private static void GoToButtons()
        {
            OracleContext c = On("");
            var navs = new List<string>();
            c.SendNav = line => { navs.Add(line); return true; };
            var printed = new List<string>();
            c.PrintSink = printed.Add;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            c.Settings.TabName = OracleView.TabQuests;

            // The shipped Legendary notes carry no coordinates, so a row with them is added for the test
            // (first in the list, taken out again): several places give a picker first, picking plans the route.
            var fake = new LegendaryQuest
            {
                Group = "Testing", Name = "Test Route Quest", Luminance = 1, Flags = new[] { "testroutequest" },
                Note = "Speak with Hoshar 59.6S 88.0W - Kill Mukkir 86.5N 57.3E",
            };
            var one = new LegendaryQuest
            {
                Group = "Testing", Name = "Test One Place", Luminance = 1, Flags = new[] { "testoneplace" },
                Note = "Speak with Hoshar 59.6S 88.0W",
            };
            LegendaryList.All.Insert(0, fake);
            try
            {
                view.ShowTrackers(QuestKind.Legendary);
                Frame(host, main, view, "Legendary");
                Click(host, main, "q.r.lg:" + fake.Name);
                string body = Frame(host, main, view, "Legendary opened");
                Check(body.Contains("Go to (2 places in these notes)") && body.Contains("###q.nav.pick") && body.Contains("###q.nav.go"),
                      "a Legendary quest with 2 places in its notes: a picker and Go");
                Check(body.Contains("###lg.sel.print") && body.Contains("###lg.sel.close") && body.Contains("Testing"), "its area, Print and Close buttons");
                Apply(host, Event(3, ++_seq, main.Hash, main.KeyOf("q.nav.pick"), BitConverter.GetBytes(1)));
                Check(navs.Count == 1 && navs[0] == "route 86.5N, 57.3E Kill Mukkir", "picking the second place plans its route: " + string.Join("|", navs));
                Frame(host, main, view, "Legendary, second place picked");
                Click(host, main, "q.nav.go");
                Check(navs.Count == 2 && navs[1] == "go 86.5N, 57.3E Kill Mukkir", "Go walks the picked place: " + string.Join("|", navs));
                Click(host, main, "lg.sel.print");
                Check(printed.Count >= 1 && printed[0].Contains("Test Route Quest") && printed.Any(p => p.Contains("Speak with Hoshar")),
                      "Print in chat prints the quest and its notes: " + string.Join(" | ", printed.Take(3)));

                // One place: no picker.
                Click(host, main, "q.r.lg:" + fake.Name);   // close
                LegendaryList.All.Insert(0, one);
                Type(host, main, "q.search", "Test One");   // the row list is cached per filter: a new search rebuilds it
                Frame(host, main, view, "Legendary, with the one-place row");
                Click(host, main, "q.r.lg:" + one.Name);
                body = Frame(host, main, view, "Legendary, one place");
                Check(body.Contains("Go to") && !body.Contains("###q.nav.pick") && body.Contains("###q.nav.go"), "one place: Go / Route / Arrow, no picker");

                // A real Legendary quest (no coordinates in its notes): opens, no Go to.
                LegendaryQuest real = LegendaryList.All.First(q => q != fake && q != one);
                Click(host, main, "q.r.lg:" + one.Name);   // close
                Type(host, main, "q.search", "");
                Frame(host, main, view, "Legendary, all rows again");
                Click(host, main, "q.r.lg:" + real.Name);
                body = Frame(host, main, view, "Legendary, no place");
                Check(!body.Contains("###q.nav.go") && body.Contains("###lg.sel.close") && body.Contains("Area"), "no coordinates in the notes: no Go to");
            }
            finally
            {
                LegendaryList.All.Remove(fake);
                LegendaryList.All.Remove(one);
            }

            // A plain quest whose notes have coordinates: Go to under its texts, and the click still prints.
            Quest? withCoords = c.Catalog.Quests.FirstOrDefault(q => NavTargets.Stops(q.Info + " - " + q.Hint).Count > 0);
            Check(withCoords != null, "some quest in the list has coordinates in its notes");
            if (withCoords != null)
            {
                view.ShowTrackers(QuestKind.Quest);
                Type(host, main, "q.search", withCoords.Flag);
                Frame(host, main, view, "Quests, searched");
                printed.Clear();
                Click(host, main, "q.r." + withCoords.Flag);
                string body = Frame(host, main, view, "Quest with coordinates");
                Check(printed.Count >= 4, "a click on a quest still prints its texts in chat");
                Check(body.Contains("###q.nav.go") && body.Contains("Save rewards") && body.Contains("###q.sel.track"),
                      "the quest opens with Go to, Track and the rewards editor");
                Type(host, main, "q.search", "");
            }
        }

        /// <summary>The throttle line, and the Refresh bar on the Quests page.</summary>
        private static void Throttle()
        {
            OracleContext c = On("");
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            c.Settings.TabName = OracleView.TabQuests;
            c.Flags.Request(_ => true, c.NowMs);
            c.Flags.OnChatLine("[MyQuests] This command may only be run once every 60 seconds.", c.NowMs + 100);
            string body = Frame(host, main, view, "Quests, throttled");
            Check(body.Contains("The server answers one /myquests a minute: Refresh works again in 6"), "the throttle line says when Refresh works again");
            Click(host, main, "refresh.Quests");
            Check(Frame(host, main, view, "Quests, Refresh while throttled").Contains("try again in 6"), "Refresh while the server's minute runs says to wait, and sends nothing");
            c.NowMs += 61_000;
            Check(!Frame(host, main, view, "Quests, throttle over").Contains("Refresh works again in"), "the countdown line goes once the minute is up");
        }
    }
}
