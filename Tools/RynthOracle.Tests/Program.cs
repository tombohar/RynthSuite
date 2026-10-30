using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthOracle;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.RynthOracle.Views;

// The engine parser's only outside dependency: the registry's limits (the engine's values).
namespace RynthCore.Engine.UI.ScriptWindows
{
    internal static class ScriptWindowRegistry
    {
        public const int MaxOpsPerWindow = 1500;
        public const int MaxBytesPerWindow = 64 * 1024;
        public const int MaxWindowsPerOwner = 32;
        public const int MaxBytesPerSubmit = 256 * 1024;
    }
}

namespace RynthOracle.Tests
{
    internal static partial class Program
    {
        private static int _pass, _fail;

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Write")]
        private static extern int Write(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_submit")]
        private static extern ref byte[] SubmitBuffer(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Apply")]
        private static extern void Apply(UiHost h, ReadOnlySpan<byte> data);

        private static int Main()
        {
            // Tab clicks save the settings: into a scratch file, never the player's %APPDATA% copy.
            OracleSettings.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N") + "-settings.json");
            Lists();
            QuestLines();
            Catalog();
            Servers();
            EngineServers();
            QuestPackHook();
            NoServerNames();
            QuestNotes();
            Void();
            TimerAlerts();
            Pages();
            CharacterExtras();
            Legendary();
            GoToParsing();
            Loaders();
            MergedList();
            MarkersReachable();
            GoToButtons();
            Throttle();
            Events();
            QuestTexts();
            Refreshes();
            SkillsTabs();
            ServerMove();
            Replies();
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Console.WriteLine("FAIL: " + what); }
        }

        private static void Lists()
        {
            Check(AugmentationList.All.Count == 88, $"augmentations: {AugmentationList.All.Count} rows (88 expected)");
            Check(AugmentationList.Quests.Count == 6, $"aug gem quests: {AugmentationList.Quests.Count}");
            Check(SocietyQuestList.All.Count == 54, $"society rows: {SocietyQuestList.All.Count}");
            Check(TitleList.All.Count == 831, $"titles: {TitleList.All.Count}");
            Check(MarkerList.All.Count == 100, $"markers: {MarkerList.All.Count}");
            var mf = new QuestFlagStore();
            mf.OnChatLine("explorationmarkersfoundingroupa - 129 solves (1719432000)\"Markers\" 1023 0", 0);
            mf.Tick(5000);
            Check(MarkerList.All[0].IsFound(mf) && MarkerList.All[1].IsFound(mf) && !MarkerList.All[2].IsFound(mf), "marker bits 1 and 128 found, the rest not");
            Check(SpellTable.Count > 6000, $"spell table: {SpellTable.Count} spells");
            Check(SpellTable.Name(5393) == "Corrosion VII", "spell 5393 is Corrosion VII: " + SpellTable.Name(5393));
            Check(SpellTable.TryGet(5395, out SpellInfo s) && s.IsDebuff && s.Duration == 31, "Corruption I is a 31 s debuff");
            Check(SpellTable.TryGet(4305, out SpellInfo f) && !f.IsDebuff && f.Duration == 5400, "Incantation of Focus Self is a 5400 s buff");
        }

        private static void TimerAlerts()
        {
            // "hourly" has a 1 h timer finished at t0; "daily" finished long ago; "never" is one-time.
            DateTime t0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            long done = new DateTimeOffset(t0).ToUnixTimeSeconds();
            var store = new QuestFlagStore();
            store.OnChatLine($"hourly - 1 solves ({done})\"Hourly\" -1 3600", 1000);
            store.OnChatLine($"daily - 1 solves ({done - 100000})\"Daily\" -1 3600", 1000);
            store.OnChatLine($"never - 1 solves ({done})\"Once\" 1 0", 1000);
            store.Tick(5000);
            var cat = new QuestCatalog();
            var tracked = new List<string> { "hourly", "daily", "never", "unknown" };
            var w = new QuestTimerWatcher();

            Check(w.Check(store, cat, tracked, t0.AddMinutes(10)).Count == 0, "no alert for what was ready when first seen");
            Check(w.Check(store, cat, tracked, t0.AddMinutes(59)).Count == 0, "no alert while on cooldown");
            var up = w.Check(store, cat, tracked, t0.AddMinutes(61));
            Check(up.Count == 1 && up[0].Flag == "hourly", "alert when the timer comes up");
            Check(w.Check(store, cat, tracked, t0.AddMinutes(62)).Count == 0, "alert only once");

            var w2 = new QuestTimerWatcher();
            w2.Check(store, cat, new List<string>(), t0.AddMinutes(10));
            Check(w2.Check(store, cat, new List<string>(), t0.AddMinutes(61)).Count == 0, "an untracked quest is silent");
            var w3 = new QuestTimerWatcher();
            w3.Check(store, cat, null, t0.AddMinutes(10));
            Check(w3.Check(store, cat, null, t0.AddMinutes(61)).Count == 1, "watching everything alerts on the repeatable only");

            store.OnChatLine($"hourly - 2 solves ({done + 4000})\"Hourly\" -1 3600", 9000);
            store.Tick(12000);
            Check(w.Check(store, cat, tracked, t0.AddMinutes(80)).Count == 0, "a fresh completion waits again");
            Check(w.Check(store, cat, tracked, t0.AddMinutes(140)).Count == 1, "and alerts again when it comes up");

            // Not read yet: nothing to say.
            Check(new QuestTimerWatcher().Check(new QuestFlagStore(), cat, tracked, t0).Count == 0, "no alerts before the flags are read");
        }

        private static void QuestLines()
        {
            // ACE PlayerCommands.HandleQuests: no space between ")" and the quote.
            QuestFlag? q = QuestFlagStore.Parse("stipendtimer_0812 - 3 solves (1759276800)\"Monthly stipend\" -1 2592000");
            Check(q != null && q.Key == "stipendtimer_0812" && q.Solves == 3 && q.MaxSolves == -1
                  && q.RepeatTime == TimeSpan.FromSeconds(2592000) && q.CompletedOnUtc == DateTime.UnixEpoch.AddSeconds(1759276800)
                  && q.Description == "Monthly stipend", "ACE /myquests line");
            QuestFlag? t = QuestFlagStore.Parse("[12:34:56] blankquestflag - 1 solves (1719432000)\"Some Quest\" 5 82800");
            Check(t != null && t.Key == "blankquestflag" && t.MaxSolves == 5, "line with a chat timestamp");
            Check(QuestFlagStore.Parse("Bob says, \"somequest - 1 solves (0)\"") == null, "a chat line quoting a flag is not a flag");
            QuestFlag? capped = QuestFlagStore.Parse("onetimer - 1 solves (1719432000)\"One time\" 1 0");
            Check(capped != null && capped.AtSolveLimit, "solve limit reached");

            var store = new QuestFlagStore();
            long now = 100_000;
            Check(store.OnChatLine("alpha - 2 solves (1719432000)\"A\" -1 72000", now) == MyQuestsLine.Quest, "burst line 1");
            Check(store.OnChatLine("beta - 1 solves (1719432000)\"B\" 1 0", now + 100) == MyQuestsLine.Quest, "burst line 2");
            store.Tick(now + 500);
            Check(store.Flags.Count == 0 && store.Loading, "not complete while lines are still arriving");
            store.Tick(now + 2000);
            Check(store.Flags.Count == 2 && !store.Loading && store.LastReadUtc != DateTime.MinValue, "burst completes after a quiet spell");
            Check(store.OnChatLine("Quest list is empty.", now + 3000) == MyQuestsLine.Empty, "empty reply");
            store.Tick(now + 3001);
            Check(store.Flags.Count == 0, "an empty reply clears the flags");
            Check(store.OnChatLine(QuestFlagStore.DisabledText, now + 4000) == MyQuestsLine.Disabled && store.ServerDisabled, "disabled reply");
            Check(store.OnChatLine("You say, \"hello\"", now) == MyQuestsLine.None, "ordinary chat ignored");

            // Our own request: the typed-command bookkeeping.
            var s2 = new QuestFlagStore();
            int sent = 0;
            Check(s2.Request(cmd => { sent++; return cmd == "/myquests"; }, 50_000) && s2.AwaitingOurs, "request sends /myquests");
            Check(!s2.Request(_ => { sent++; return true; }, 51_000) && sent == 1, "a second request within 5 s is skipped");
            s2.NoteTypedCommand(); // our own send reaching the chat bar
            s2.Tick(51_100);
            Check(s2.AwaitingOurs, "our own send doesn't count as typed by the player");
            s2.NoteTypedCommand(); // the player typing it
            s2.Tick(51_200);
            Check(!s2.AwaitingOurs, "a typed /myquests shows its reply");

            // A second reply that lost most of its lines on the way (the engine's chat queue dropped
            // the head of the burst) updates the flags instead of blanking them.
            var s3 = new QuestFlagStore();
            long t3 = 200_000;
            for (int i = 0; i < 100; i++)
                s3.OnChatLine($"flag{i:D3} - 1 solves (1719432000)\"Q{i}\" -1 0", t3 + i);
            s3.Tick(t3 + 5_000);
            Check(s3.Flags.Count == 100, "first full reply: 100 flags");
            long t4 = t3 + 120_000;
            for (int i = 90; i < 100; i++)
                s3.OnChatLine($"flag{i:D3} - 2 solves (1719500000)\"Q{i}\" -1 0", t4 + i);
            s3.Tick(t4 + 5_000);
            Check(s3.Flags.Count == 100 && s3.LastPartialReply is { Got: 10, Had: 100 },
                  "a cut-short reply (10 of 100) is merged, not a replacement");
            Check(s3.TryGet("flag095", out QuestFlag f95) && f95.Solves == 2 && s3.TryGet("flag001", out _),
                  "merged reply updates the lines it had and keeps the rest");
            long t5 = t4 + 120_000;
            for (int i = 0; i < 95; i++)
                s3.OnChatLine($"flag{i:D3} - 3 solves (1719600000)\"Q{i}\" -1 0", t5 + i);
            s3.Tick(t5 + 5_000);
            Check(s3.Flags.Count == 95 && !s3.TryGet("flag099", out _), "a near-full reply still replaces (a dropped flag goes away)");

            // ACECustom's once-a-minute limit: the throttle line ends "Reading..." and keeps the flags.
            int had = s3.Flags.Count;
            long t6 = t5 + 30_000;
            Check(s3.Request(_ => true, t6) && s3.AwaitingOurs, "refresh sent");
            Check(s3.OnChatLine("[MyQuests] This command may only be run once every 60 seconds.", t6 + 200) == MyQuestsLine.Throttled
                  && !s3.AwaitingOurs && s3.Flags.Count == had, "throttle reply: not loading, flags kept");
            Check(s3.ThrottleSecondsLeft(t6 + 1_000) > 50 && !s3.Request(_ => true, t6 + 10_000), "no resend while the server's minute runs");
            Check(s3.ThrottleSecondsLeft(t6 + 61_000) == 0 && s3.Request(_ => true, t6 + 61_000), "refresh allowed again after the minute");
        }

        private static void Catalog()
        {
            var retail = new QuestCatalog();
            retail.Load(null, msg => Console.WriteLine(msg));
            bool bundled = retail.UsingBundled;
            Check(bundled || System.IO.File.Exists(QuestCatalog.DownloadedPath), "bundled list used (no download present)");
            if (bundled)
            {
                Check(retail.RetailCount == 3565 && retail.ServerCount == 0 && retail.PackServer == "", $"another server: retail only ({retail.RetailCount}+{retail.ServerCount})");
                // The same quests on the same servers as before the packs moved into their server plugins.
                var lev = new QuestCatalog();
                lev.Load(TestPacks.Levistras, null);
                Check(lev.RetailCount == 3565 && lev.ServerCount == 464 && lev.PackServer == "Levistras" && lev.PackSource == "from RynthLevistras",
                      $"Levistras pack (RynthLevistras's): {lev.RetailCount}+{lev.ServerCount}");
                var cq = new QuestCatalog();
                cq.Load(TestPacks.Conquest, null);
                Check(cq.RetailCount == 3565 && cq.ServerCount == 546, $"Conquest pack (RynthConquest's): {cq.RetailCount}+{cq.ServerCount}");
                Check(!retail.TryGet("trigram4", out _) && cq.TryGet("trigram4", out Quest tq) && tq.Server == "Conquest", "a Conquest-only quest only loads with Conquest's pack");
            }

            var cat = new QuestCatalog();
            cat.Load(TestPacks.Aelrynth, msg => Console.WriteLine(msg));
            if (bundled)
            {
                Check(cat.PackServer == "Aelrynth" && cat.ServerCount == 712 && cat.HiddenCount == 38 && cat.RetailCount == 3565 - 38,
                    $"Aelrynth pack: {cat.RetailCount} retail + {cat.ServerCount}, {cat.HiddenCount} hidden");
                Check(cat.TryGet("30minattributes", out Quest baseQuest) && baseQuest.Name == "Attribute and Skill Redistribution"
                      && baseQuest.Server == "" && baseQuest.MinDelta == 1800 && baseQuest.Source.StartsWith("set by", StringComparison.Ordinal),
                    "a base quest keeps Advis's name and gains Aelrynth's timer and source");
                Check(cat.TryGet("aelrynthcitadelbelowwarlord", out Quest own) && own.Server == "Aelrynth" && own.DisplayName.Contains("Kazrok"),
                    "Aelrynth's own content is in the pack");
                Check(!retail.TryGet("aelrynthcitadelbelowwarlord", out _), "Aelrynth's pack doesn't load elsewhere");
            }

            var flags = new QuestFlagStore();
            flags.OnChatLine("zzzz_custom_aelrynth_flag - 1 solves (1719432000)\"Custom\" -1 3600", 0);
            flags.OnChatLine("30minattributes - 4 solves (1719432000)\"x\" -1 1800", 0);
            flags.Tick(5000);
            cat.SyncDiscoveries(flags);
            Check(cat.TryGet("zzzz_custom_aelrynth_flag", out Quest nq) && nq.IsNew && nq.Name == "Custom", "unknown flag added as new");
            Check(cat.TryGet("30minattributes", out Quest known) && !known.IsNew && known.IsComplete(flags) && known.IsRepeatable(flags), "known flag is complete and repeatable");
            Check(QuestCatalog.Validate(string.Join("\n", Enumerable.Repeat("x", 3)), out _) == false, "validation rejects junk");
        }

        /// <summary>A RynthCore GetServerStateJson answer (schema 1) with the given servers.</summary>
        private static string EngineJson(string active, string activeName, string activePlugin, string pick, string world, params string[] servers)
            => "{\"schema\":1,\"revision\":3,\"host\":\"h\",\"world\":\"" + world + "\",\"pick\":\"" + pick + "\",\"active\":\"" + active
               + "\",\"activeName\":\"" + activeName + "\",\"activePlugin\":\"" + activePlugin + "\",\"servers\":[" + string.Join(",", servers) + "]}";

        private static string EngineRow(string id, bool active, string force, string plugin = "", string reason = "")
            => "{\"id\":\"" + id + "\",\"name\":\"" + id.ToUpperInvariant() + "\",\"active\":" + (active ? "true" : "false")
               + ",\"detected\":" + (active && force == "auto" ? "true" : "false") + ",\"force\":\"" + force
               + "\",\"builtIn\":false,\"plugin\":\"" + plugin + "\",\"reason\":\"" + reason + "\"}";

        // RynthCore's server list (API v86) drives the identity: overrides and the Diagnostics pick included.
        private static void EngineServers()
        {
            // The shared parser.
            Check(RynthCore.Plugin.Shared.EngineServerState.Parse(null) == null && RynthCore.Plugin.Shared.EngineServerState.Parse("junk") == null, "engine state: junk is null");
            Check(RynthCore.Plugin.Shared.EngineServerState.Parse("{\"schema\":2}") == null, "engine state: another schema is null");
            var st = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("ilt", "Infinite Leaftide", "RynthILT", "auto", "InfiniteLeaftide",
                EngineRow("ilt", true, "auto", "RynthILT", "host matches"), EngineRow("aelrynth", false, "auto")));
            Check(st != null && st.HasActive && st.IsActive("ILT") && st.ActivePlugin == "RynthILT" && !st.Picked, "engine state: active ILT, detected");
            Check(st!.Servers.Count == 2 && st.Find("aelrynth") != null && st.Find("x") == null && st.Revision == 3, "engine state: rows");

            // Identity from it: the active server, its name and the plugin asked for its quest pack.
            ServerIdentity.FromEngine(st, "Launcher Profile", out string id, out string name, out string plugin, out string world, out string source);
            Check(id == "ilt" && name == "Infinite Leaftide" && plugin == "RynthILT" && world == "InfiniteLeaftide", "engine: the active server and its plugin: " + source);
            Check(source.Contains("host matches"), "engine: the reason is shown: " + source);

            var picked = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("conquest", "ConquestAC", "RynthConquest", "conquest", "",
                EngineRow("ilt", false, "off"), EngineRow("conquest", true, "on", "RynthConquest")))!;
            ServerIdentity.FromEngine(picked, "Leaftide Box", out id, out _, out plugin, out world, out source);
            Check(id == "conquest" && plugin == "RynthConquest" && world == "Leaftide Box", "engine: a pick beats the world name (and the world falls back): " + source);
            Check(source.Contains("picked by hand"), "engine: a pick says so: " + source);

            var off = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("", "", "", "mixed", "InfiniteLeaftide", EngineRow("ilt", false, "off")))!;
            ServerIdentity.FromEngine(off, "", out id, out _, out plugin, out _, out source);
            Check(id == "" && plugin == "" && source.Contains("no server active"), "engine: ILT forced off stays off despite the Leaftide world name: " + source);

            var unknown = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("", "", "", "auto", "Levistras", EngineRow("ilt", false, "auto")))!;
            ServerIdentity.FromEngine(unknown, "", out id, out _, out _, out world, out source);
            Check(id == "" && world == "Levistras", "engine: a world the engine has no server for is no server (no plugin, so no pack): " + source);
            ServerIdentity.FromEngine(null, "Levistras", out id, out _, out _, out world, out source);
            Check(id == "" && world == "Levistras" && source.Contains("older than API v86"), "no server list (an older engine): no server: " + source);

            var coldeve = RynthCore.Plugin.Shared.EngineServerState.Parse(EngineJson("coldeve", "Coldeve", "RynthColdeve", "auto", "Coldeve", EngineRow("coldeve", true, "auto", "RynthColdeve")))!;
            ServerIdentity.FromEngine(coldeve, "", out id, out _, out plugin, out _, out source);
            Check(id == "coldeve" && plugin == "RynthColdeve" && source.Contains("Coldeve"), "engine: any active server is the server, with its plugin: " + source);

            // The owner's profile, read leniently (a server plugin's, and RynthILT's).
            var prof = RynthCore.Plugin.Shared.ServerProfileView.Parse(
                "{\"schema\":1,\"id\":\"coldeve\",\"name\":\"Coldeve\",\"plugin\":\"RynthColdeve\",\"ruleset\":\"PvE\",\"emulator\":\"ACE\",\"botting\":\"forbidden\",\"bottingNote\":\"no bots\",\"accountLimit\":\"3 accounts\"}");
            Check(prof != null && prof.Botting == "forbidden" && prof.Summary() == "PvE, ACE; botting forbidden - no bots; accounts: 3 accounts", "profile: server plugin: " + prof?.Summary());
            var iltProf = RynthCore.Plugin.Shared.ServerProfileView.Parse("{\"ilt\":true,\"features\":[{\"key\":\"bank\",\"state\":1}]}");
            Check(iltProf != null && iltProf.Summary() == "no details", "profile: a server's own fields are its plugin's, not the shared view's: " + iltProf?.Summary());
            Check(RynthCore.Plugin.Shared.ServerProfileView.Parse("") == null && RynthCore.Plugin.Shared.ServerProfileView.Parse("[1]") == null, "profile: junk is null");
        }

        private static void Servers()
        {
            // RynthOracle has no server tags or world-name rules of its own since 2026-10-21: RynthCore's list decides.
            Check(typeof(ServerIdentity).Assembly.GetType("RynthCore.Plugin.RynthOracle.Data.ServerTags") == null, "no server tags in RynthOracle");
            Check(typeof(ServerIdentity).GetMethod("TagForWorld", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) == null,
                  "no world-name detection in RynthOracle");
            var id = new ServerIdentity();
            id.Force("", "Coldeve");
            Check(id.ServerId == "" && id.DisplayName == "no server" && id.WorldName == "Coldeve" && id.Known, "no server active: retail");
            id.Force("conquest", "Conquest", "RynthConquest");
            Check(id.ServerId == "conquest" && id.Plugin == "RynthConquest" && id.DisplayName == "conquest", "a server and the plugin asked for its pack");
            int rev = id.Revision;
            id.Force("conquest", "Conquest", "RynthConquest");
            Check(id.Revision == rev, "the same identity again: no change");
            id.Reset();
            Check(!id.Known && id.ServerId == "", "logout: forgotten");

            // Tabs per server.
            var c = new OracleContext { InWorld = true };
            var view = new OracleView(c);
            c.Server.Force("", "Coldeve");
            string[] none = view.VisibleTabs();
            Check(!none.Contains("Leaderboards") && !none.Contains("Conquest"), "no server tabs on other servers: " + string.Join(",", none));
            // A server's own pages are its server plugin's since 2026-10-20 (RynthConquest, RynthAelrynth).
            c.Server.Force("aelrynth", "Aeshnidae");
            string[] ael = view.VisibleTabs();
            Check(!ael.Contains("Leaderboards") && !ael.Contains("Conquest"), "Aelrynth: no leaderboards in RynthOracle: " + string.Join(",", ael));
            c.Server.Force("conquest", "Conquest");
            string[] cq = view.VisibleTabs();
            Check(!cq.Contains("Leaderboards") && !cq.Contains("Conquest"), "Conquest: no Conquest tab and no leaderboards in RynthOracle: " + string.Join(",", cq));
            foreach (string old in new[] { "Conquest", "Leaderboards" })
            {
                c.Settings.TabName = old;
                Check(view.Tab == OracleView.TabQuests, $"a {old} tab saved by an older version falls back to Quests");
            }
        }

        // Quest notes: tracking and rewards saved per character, UB's reward format and import, kill-task timers.
        private static void QuestNotes()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var r = new QuestReward { XpPercent = 1.5, LumPercent = 2, RecordedLevel = 275, Coins = 3, Trinkets = 1, Medallions = 2,
                                          YellowPowder = 1, RedPowder = 1, BluePowder = 1, Mmd = 2 };
                Check(r.ShortSummary() == "1.5%XP 2%Lum 3C 1T 2M 1Y 1R 1B 2D L275", "UB's short summary: " + r.ShortSummary());
                Check(QuestReward.Percent(12.345) == "12.3" && QuestReward.Percent(0.1234) == "0.123" && QuestReward.Percent(1.234) == "1.23", "UB's percent format");
                Check(QuestReward.TryParsePercent("2,5%", out double pc) && pc == 2.5, "a percent with a comma and a % sign");
                Check(QuestReward.TryParsePercent("5000", out double big) && big == QuestReward.MaxPercent, "percent clamped to 1000");
                Check(!QuestReward.TryParsePercent("abc", out _) && !QuestReward.TryParseCount("1.5", out _), "junk is refused");
                Check(QuestReward.TryParseCount("-4", out int neg) && neg == 0 && QuestReward.TryParseCount("", out int blank) && blank == 0, "counts: negatives and blanks are 0");
                Check(new QuestReward().IsEmpty && !r.IsEmpty, "empty entry");

                // Track and record, then read it back as a new session would.
                var book = new QuestBook { RootOverride = root, UbRootOverride = "" };
                book.Load("Infinite Leaftide", "Test \"Char\"");
                Check(book.Loaded && book.Tracked.Count == 0 && book.RewardCount == 0, "a new character starts empty");
                book.SetTracked("killtaskdrudges", true);
                book.SetTracked("30minattributes", true);
                book.SetTracked("killtaskdrudges", true);
                book.SetReward("30minattributes", r);
                Check(book.Tracked.Count == 2 && book.IsTracked("KILLTASKDRUDGES"), "tracked once, case-insensitive");
                string file = book.PathFor("Infinite Leaftide", "Test \"Char\"");
                Check(System.IO.File.Exists(file) && System.IO.File.ReadAllText(file).All(ch => ch == '\n' || (ch >= 0x20 && ch <= 0x7E)), "saved at once, plain ASCII");

                var again = new QuestBook { RootOverride = root, UbRootOverride = "" };
                again.Load("Infinite Leaftide", "Test \"Char\"");
                Check(again.Tracked.SequenceEqual(new[] { "killtaskdrudges", "30minattributes" }), "tracked list kept in order");
                Check(again.TryGetReward("30minattributes", out QuestReward back) && back.ShortSummary() == r.ShortSummary(), "rewards read back");
                again.SetReward("30minattributes", new QuestReward());
                again.ToggleTracked("killtaskdrudges");
                Check(again.RewardCount == 0 && again.Tracked.Count == 1, "an empty entry removes the rewards; untrack");

                // UB ILT's quest_rewards.json is imported once for a character without notes.
                string ub = System.IO.Path.Combine(root, "ub");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(ub, "InfiniteLeaftide", "Kelp"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(ub, "InfiniteLeaftide", "Kelp", "quest_rewards.json"),
                    "{\"SchemaVersion\":2,\"Character\":\"Kelp\",\"Server\":\"InfiniteLeaftide\",\"Rewards\":{"
                    + "\"stipendtimer_0812\":{\"XpPercent\":1.25,\"LumPercent\":0,\"RecordedLevel\":300,\"Coins\":5},"
                    + "\"emptyone\":{\"XpPercent\":0}}}");
                var imported = new QuestBook { RootOverride = root, UbRootOverride = ub };
                imported.Load("Infinite Leaftide", "Kelp");
                Check(imported.RewardCount == 1 && imported.TryGetReward("stipendtimer_0812", out QuestReward ir) && ir.Coins == 5 && ir.RecordedLevel == 300
                      && imported.LoadNote.Contains("Imported 1"), "UB rewards imported (empty entries skipped): " + imported.LoadNote);
                var noReimport = new QuestBook { RootOverride = root, UbRootOverride = ub };
                noReimport.Load("Infinite Leaftide", "Kelp");
                Check(noReimport.RewardCount == 1 && noReimport.LoadNote.Length == 0, "imported once: the next load reads Oracle's own file");

                // Kill tasks: kills left, then the reset timer, then done (UB's rule).
                DateTime now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
                var kt = new QuestFlag { Key = "killtaskdrudges", Solves = 12, MaxSolves = 50, RepeatTime = TimeSpan.FromHours(20), CompletedOnUtc = now.AddHours(-1) };
                Check(QuestTimer.IsKillTask(kt) && QuestTimer.State(kt, true, now, out string t1) == QuestTimerState.InProgress && t1 == "38 left", "kill task counting");
                kt.Solves = 50;
                Check(QuestTimer.State(kt, true, now, out string t2) == QuestTimerState.Waiting && t2.StartsWith("19h"), "kill task waiting: " + t2);
                kt.CompletedOnUtc = now.AddDays(-2);
                Check(QuestTimer.State(kt, true, now, out string t3) == QuestTimerState.Done && t3 == "done", "kill task done");
                var timed = new QuestFlag { Key = "stipendtimer_0812", Solves = 3, RepeatTime = TimeSpan.FromHours(1), CompletedOnUtc = now.AddMinutes(-30) };
                QuestTimerState s4 = QuestTimer.State(timed, true, now, out string t4);
                Check(!QuestTimer.IsKillTask(timed) && s4 == QuestTimerState.Waiting && t4 == "30m 0s", "timer: " + t4);
                timed.CompletedOnUtc = now.AddHours(-2);
                Check(QuestTimer.State(timed, true, now, out string t5) == QuestTimerState.Ready && t5 == "ready", "timer ran out");
                Check(QuestTimer.State(timed, false, now, out string t6) == QuestTimerState.Done && t6 == "completed", "one-time quest");
            }
            finally
            {
                try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
            }
        }

        private static void Void()
        {
            var v = new VoidTracker();
            long now = 1_000_000;
            bool hit = v.OnChatLine("You cast Corrosion VII on Drudge Slinker", now, _ => 0x50000001u, () => true);
            Check(hit && v.Dots.Count == 1 && v.Dots[0].Family == VoidFamily.Corrosion && v.Dots[0].Destruction, "cast with the surge up");
            Check(v.Remaining(0x50000001u, "Drudge Slinker", VoidFamily.Corrosion, now + 1000, out bool d) == 16 && d, "15 s + 2 s before the first tick");
            v.OnChatLine("You scar Drudge Slinker for 120 points of periodic nether damage!", now + 2000, _ => 0, () => false);
            Check(v.Remaining(0x50000001u, "Drudge Slinker", VoidFamily.Corrosion, now + 3000, out _) == 14, "timed from the first tick");
            v.OnChatLine("You cast Corruption III on Mosswart", now, _ => 0, () => false);
            Check(v.Remaining(0x123u, "Mosswart", VoidFamily.Corruption, now + 1000, out _) == 31, "unknown id matches by name");
            Check(!v.OnChatLine("You cast Strength Self VI on yourself", now, _ => 0, () => false), "other spells ignored");
            v.RemoveExpired(now + 60_000);
            Check(v.Dots.Count == 0, "expired spells drop");
        }

        private static OracleContext Context()
        {
            var c = new OracleContext { InWorld = true, NowMs = 10_000_000, UtcNow = DateTime.UtcNow };
            c.Server.Force("aelrynth", "Aeshnidae", "RynthAelrynth");
            c.Catalog.Load(TestPacks.Aelrynth, null);
            c.Flags.OnChatLine("30minattributes - 4 solves (1719432000)\"x\" -1 1800", 0);
            c.Flags.OnChatLine("societyribbonsperdaytimer - 1 solves (" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ")\"x\" -1 72000", 0);
            c.Flags.OnChatLine("societyribbonsperdaycounter - 30 solves (1719432000)\"x\" 200 0", 0);
            c.Flags.Tick(5000);
            c.Catalog.SyncDiscoveries(c.Flags);
            CharacterSnapshot ch = c.Character;
            ch.Valid = true;
            ch.PlayerId = 0x50000001;
            ch.Name = "Test Character";
            ch.Level = 126;
            ch.TotalXp = 4_200_000_000;
            ch.Enchantments.Add(new ActiveEnchantment(4305, "Incantation of Focus Self", 3000, 5400, false));
            ch.Enchantments.Add(new ActiveEnchantment(3531, "Bobo's Quickening", 600, 1800, false));
            ch.Enchantments.Add(new ActiveEnchantment(SpellIds.SurgeOfDestruction, "Surge of Destruction", 5, 8, false));
            c.Void.OnChatLine("You cast Corrosion VII on Drudge Slinker", c.NowMs, _ => 0, () => true);
            // The quest book in a temp folder (never the player's %APPDATA%), with two tracked quests.
            c.Book.RootOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RynthOracleTests-" + Guid.NewGuid().ToString("N"));
            c.Book.UbRootOverride = "";
            c.Book.Load("Aeshnidae", ch.Name);
            c.Book.SetTracked("30minattributes", true);
            c.Book.SetTracked("societyribbonsperdaycounter", true);
            c.Book.SetReward("30minattributes", new QuestReward { XpPercent = 1.5, RecordedLevel = 126, Coins = 2 });
            return c;
        }

        private static void Pages()
        {
            OracleContext c = Context();
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true, DefaultWidth = 620, DefaultHeight = 560 });
            UiWindow voidW = host.Add(new UiWindow("RynthOracle/Void", "Void") { Visible = true });
            var vv = new VoidView(c);
            voidW.Begin(); vv.Draw(voidW); voidW.End();
            UiWindow timersW = host.Add(new UiWindow("RynthOracle/QuestTimers", "Quest Timers") { Visible = true });
            var tv = new QuestTimersView(c);
            timersW.Begin(); tv.Draw(timersW); timersW.End();
            string timersBody = Encoding.UTF8.GetString(timersW.Body);
            Check(timersBody.Contains("Attribute and Skill Redistribution") && timersBody.Contains("ready"), "the Quest Timers window lists tracked quests with their timers");

            // Every tab on each server, including the server-specific ones (with data in them).
            c.EarnedTitles.Add(1);
            c.TitlesKnown = true;
            foreach (string server in new[] { "aelrynth", "conquest", "" })
            {
                c.Server.Force(server, server);
                foreach (string tab in view.VisibleTabs())
                {
                    c.Settings.TabName = tab;
                    main.Begin();
                    view.Draw(main);
                    main.End();
                    ParseAll(host, $"{server}/{tab}", out int ops);
                    Check(ops > 5 && ops < 1400, $"{server}/{tab}: {ops} ops");
                    if (server == "aelrynth") Console.WriteLine($"  {(server.Length > 0 ? server : "other")}/{tab}: {ops} ops, {main.Body.Length} bytes");
                    main.MarkSubmitted(true);
                }
            }
            // The Quests tab with a quest opened (rewards editor) and the Tracked filter on.
            c.Server.Force("", "Coldeve");
            c.Settings.TabName = OracleView.TabQuests;
            main.Begin(); view.Draw(main); main.End();
            Apply(host, Event(2, 89, main.Hash, main.KeyOf("q.tracked"), new byte[] { 1 }));
            main.Begin(); view.Draw(main); main.End();
            Check(Encoding.UTF8.GetString(main.Body).Contains("2 shown"), "the Tracked filter lists the two tracked quests");
            Apply(host, Event(1, 90, main.Hash, main.KeyOf("q.r.30minattributes"), new byte[] { 1, 0, 0 }));
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "quests with a quest opened", out int openOps);
            string openBody = Encoding.UTF8.GetString(main.Body);
            Check(openOps > 5 && openOps < 1400, $"quests page with a quest opened: {openOps} ops");
            Check(openBody.Contains("Save rewards") && openBody.Contains("Level: 126"), "the opened quest shows the rewards editor");
            Check(openBody.Contains("1.5%XP 2C L126"), "the Rewards column shows the recorded rewards");
            Check(!openBody.Contains("/qb"), "no quest bonus list here (Infinite Leaftide's /qb is RynthILT's)");
            Console.WriteLine($"  Quests (opened): {openOps} ops, {main.Body.Length} bytes");
            main.MarkSubmitted(true);

            // A page that can't fit: everything in the catalog on one page is cut short, still valid.
            var big = new UiWindow("RynthOracle/Big", "Big") { Visible = true };
            var bigHost = new UiHost();
            bigHost.Add(big);
            big.Begin();
            for (int i = 0; i < 3000; i++) { big.Text("row " + i); big.SameLine(100); big.Text("x"); }
            big.End();
            ParseAll(bigHost, "oversized page", out int bigOps);
            Check(bigOps <= 1500, $"oversized page cut to {bigOps} ops");

            // Logged out: the main window says so and still parses.
            c.InWorld = false;
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "logged out", out _);
        }

        private static unsafe void ParseAll(UiHost host, string what, out int maxOps)
        {
            int n = Write(host);
            byte[] buf = SubmitBuffer(host);
            int rc;
            List<ParsedWindow> windows;
            string error;
            fixed (byte* p = buf) rc = DisplayListParser.Parse(p, n, 4000f, out _, out windows, out error);
            Check(rc == 0, $"{what}: engine parser refused the submit ({rc}): {error}");
            maxOps = windows.Where(w => w.List != null).Select(w => w.List!.OpCount).DefaultIfEmpty(0).Max();
        }

        /// <summary>
        /// The Character tab's RynthAi session (the generic part of the Mini Remote's old Character
        /// tab, moved here 2026-10-13). Infinite Leaftide's charms are RynthILT's and never drawn here.
        /// </summary>
        private static void CharacterExtras()
        {
            OracleContext c = Context();
            c.Server.Force("", "InfiniteLeaftide");
            c.Settings.TabName = OracleView.TabCharacter;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });

            // Nothing from RynthAi yet: the tab says what to load.
            main.Begin(); view.Draw(main); main.End();
            string empty = Encoding.UTF8.GetString(main.Body);
            Check(empty.Contains("Load RynthAi for the session rates"), "without RynthAi: says to load it");
            Check(!empty.Contains("Charms") && !empty.Contains("RynthILT"), "no charms section on any world (RynthILT's Charms tab)");
            main.MarkSubmitted(true);

            c.Session = new RynthCore.Plugin.Shared.SessionStatsV1
            {
                StartUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks, SessionSeconds = 3600, Xp = 1_234_567, XpPerHour = 1_234_567,
                Lum = 4000, LumPerHour = 4000, LumLast5Min = 250, Kills = 42, KillsPerHour = 42,
            };
            c.SessionKnown = true;
            main.Begin(); view.Draw(main); main.End();
            ParseAll(host, "Character with the session", out int ops);
            string body = Encoding.UTF8.GetString(main.Body);
            Check(ops > 5 && ops < 1400, $"Character with the session: {ops} ops");
            Check(body.Contains("1,234,567") && body.Contains("4,000") && body.Contains("42 (42/h)"), "the session shows RynthAi's XP, luminance and kills");
            Check(OracleView.SessionStatsCommand == "/rc miniremote section char", "Session Stats opens the section, not the moved tab");
            main.MarkSubmitted(true);
        }

        private static void Events()
        {
            OracleContext c = Context();
            c.Settings.TabName = OracleView.TabQuests;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            main.Begin(); view.Draw(main); main.End();

            // Click the "Society" tab button.
            Apply(host, Event(1, 1, main.Hash, main.KeyOf("tab." + OracleView.TabSociety), new byte[] { 1, 0, 0 }));
            Check(view.Tab == OracleView.TabSociety, "a click on a tab switches to it");

            // Type in the quest search box, then check the filtered page.
            c.Settings.TabName = OracleView.TabQuests;
            main.Begin(); view.Draw(main); main.End();
            byte[] text = Encoding.UTF8.GetBytes("attribute");
            var payload = new byte[3 + text.Length];
            payload[0] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)text.Length);
            text.CopyTo(payload, 3);
            Apply(host, Event(5, 2, main.Hash, main.KeyOf("q.search"), payload));
            Check(main.NeedsPass, "an event asks for a pass");
            main.Begin(); view.Draw(main); main.End();
            string body = Encoding.UTF8.GetString(main.Body);
            Check(body.Contains("Attribute and Skill Redistribution"), "search narrows the quest list");

            // The player closes the window with its X.
            Apply(host, Event(8, 3, main.Hash, 0, new byte[] { 0, 1 }));
            Check(!main.Visible, "a player close hides the window");
        }

        /// <summary>
        /// A click on a quest prints its texts in chat the way upstream's list "thinks" them: the info and
        /// hint, the link, and the flag's history ("Never completed" for a flag the character lacks).
        /// </summary>
        private static void QuestTexts()
        {
            OracleContext c = Context();
            var printed = new List<string>();
            c.PrintSink = printed.Add;

            Check(c.Catalog.TryGet("30minattributes", out Quest held), "the catalog has 30minattributes");
            List<string> lines = QuestText.Lines(held, c.Flags, c.Book, c.UtcNow);
            Check(lines[0].StartsWith("Attribute and Skill Redistribution (30minattributes) - "), "first line: name, flag, status: " + lines[0]);
            Check(lines[1] == "30minattributes: " + held.Details() && lines[1].Contains("30 Minutes to pick up Attribute Gems"), "the info and hint text: " + lines[1]);
            Check(lines[2] == "30minattributes: " + held.Url && held.Url.StartsWith("https://"), "the wiki link");
            Check(lines[3].StartsWith("30minattributes: x CompletedOn:") && lines[3].Contains("Solves:4 MaxSolves:-1 RepeatTime:30m"),
                  "a held flag prints its history (upstream's QuestFlag text): " + lines[3]);
            Check(c.Book.TryGetReward("30minattributes", out QuestReward rw) && lines[4] == "Rewards: " + rw.LongSummary(), "recorded rewards come last: " + lines[4]);

            // A flag no list knows and the character lacks: says so rather than staying silent.
            List<string> none = QuestText.Lines(new Quest { Flag = "madeupflag", IsNew = true }, c.Flags, c.Book, c.UtcNow);
            Check(none.Count == 4 && none[0] == "Not in the quest list (madeupflag) - not done", "unknown quest header: " + none[0]);
            Check(none[1] == "madeupflag: No hint available" && none[2] == "madeupflag: No url available" && none[3] == "madeupflag: Never completed",
                  "no hint, no link, never completed: " + string.Join(" | ", none));

            // The click itself, on the Quests tab and in the Quest Timers window.
            c.Settings.TabName = OracleView.TabQuests;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            main.Begin(); view.Draw(main); main.End();
            Apply(host, Event(1, 40, main.Hash, main.KeyOf("q.r.30minattributes"), new byte[] { 1, 0, 0 }));
            Check(printed.Count == lines.Count && printed[1] == lines[1], $"a click on a quest prints its texts ({printed.Count} lines)");
            main.Begin(); view.Draw(main); main.End();
            Check(Encoding.UTF8.GetString(main.Body).Contains("Save rewards"), "the click still opens the quest below the list");
            printed.Clear();
            Apply(host, Event(1, 41, main.Hash, main.KeyOf("q.r.30minattributes"), new byte[] { 1, 0, 0 }));
            Check(printed.Count == lines.Count, "a second click prints again (and closes it), as upstream's list did");

            UiWindow timersW = host.Add(new UiWindow("RynthOracle/QuestTimers", "Quest Timers") { Visible = true });
            var tv = new QuestTimersView(c);
            timersW.Begin(); tv.Draw(timersW); timersW.End();
            printed.Clear();
            Apply(host, Event(1, 42, timersW.Hash, timersW.KeyOf("qt.r.30minattributes"), new byte[] { 1, 0, 0 }));
            Check(printed.Count == lines.Count && printed[1] == lines[1], "a click on a tracked quest in the Quest Timers window prints its texts");
        }

        /// <summary>
        /// The Skills, Attributes and Progress tabs: three more tabs, the others untouched, and the
        /// /rc oracle-skills hand-off sent only when the wanted mode changes.
        /// </summary>
        private static void SkillsTabs()
        {
            // The tabs: after Character, on every server; every earlier tab is still there.
            string[] earlier = { OracleView.TabQuests, OracleView.TabCharacter, OracleView.TabAugs, OracleView.TabSociety,
                OracleView.TabTitles, OracleView.TabVoid, OracleView.TabFellowship, OracleView.TabAbout };
            OracleContext c = Context();
            var view = new OracleView(c);
            foreach (string server in new[] { "aelrynth", "conquest", "" })
            {
                c.Server.Force(server, server);
                string[] tabs = view.VisibleTabs();
                string label = server.Length > 0 ? server : "other";
                Check(earlier.All(tabs.Contains), $"{label}: every earlier tab is still there: " + string.Join(",", tabs));
                int ch = Array.IndexOf(tabs, OracleView.TabCharacter);
                Check(tabs.Skip(ch + 1).Take(3).SequenceEqual(new[] { OracleView.TabSkills, OracleView.TabAttributes, OracleView.TabProgress }),
                    $"{label}: Skills, Attributes, Progress follow Character");
            }
            Check((OracleView.TabSkills, OracleView.TabAttributes, OracleView.TabProgress) == ("Skills", "Attributes", "Progress"), "the tabs are named Skills, Attributes, Progress");

            // The page: the tab strip and nothing below it (the engine fills the rest), and a click selects the tab.
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            string Draw()
            {
                main.Begin(); view.Draw(main); main.End();
                string body = Encoding.UTF8.GetString(main.Body);
                main.MarkSubmitted(true);
                return body;
            }
            c.Settings.TabName = OracleView.TabQuests;
            Draw();
            Apply(host, Event(1, 500, main.Hash, main.KeyOf("tab." + OracleView.TabSkills), new byte[] { 1, 0, 0 }));
            Check(view.Tab == OracleView.TabSkills, "a click on the Skills tab selects it");
            main.Begin(); view.Draw(main); main.End();
            string skills = Encoding.UTF8.GetString(main.Body);
            ParseAll(host, "skills tab", out int ops);
            main.MarkSubmitted(true);
            Check(skills.Contains("tab.Attributes") && skills.Contains("tab.Progress"), "the Skills page still shows the whole tab strip");
            Check(!skills.Contains("Refresh###") && !skills.Contains("page.Skills") && !skills.Contains("Log in to a character"),
                "the Skills page has no Refresh row and no child covering the engine's region");
            Check(ops > 5, $"skills tab parses ({ops} ops)");
            c.InWorld = false;
            Check(Draw().Contains("Log in to a character"), "logged out, the Skills tab says to log in (the engine is told off)");
            c.InWorld = true;

            // The state machine.
            var sent = new List<string>();
            bool accept = true;
            var e = new SkillsEmbed(cmd => { sent.Add(cmd); return accept; });
            long now = 10_000;
            bool U(string tab, bool visible = true, bool inWorld = true) => e.Update(tab, visible, inWorld, now += 16);

            for (int i = 0; i < 50; i++) U(OracleView.TabSkills, inWorld: false);
            Check(sent.Count == 0 && !e.Announced, "logged out, before any command: nothing is sent");
            for (int i = 0; i < 50; i++) U(OracleView.TabQuests);
            Check(sent.SequenceEqual(new[] { "/rc oracle-skills off" }) && e.Announced,
                "in the world on another tab: one off, the announcement that Oracle has these tabs, and no more");
            sent.Clear();
            for (int i = 0; i < 100; i++) U(OracleView.TabSkills);
            Check(sent.SequenceEqual(new[] { "/rc oracle-skills skills" }), "Skills: one command, however many frames follow");
            U(OracleView.TabAttributes); U(OracleView.TabAttributes);
            U(OracleView.TabProgress); U(OracleView.TabProgress);
            U(OracleView.TabSkills);
            Check(sent.Skip(1).SequenceEqual(new[] { "/rc oracle-skills attrs", "/rc oracle-skills progress", "/rc oracle-skills skills" }),
                "moving between the three tabs sends the new mode once each: " + string.Join(" | ", sent));
            sent.Clear();
            U(OracleView.TabQuests);
            for (int i = 0; i < 20; i++) U(OracleView.TabQuests);
            U(OracleView.TabCharacter);
            Check(sent.SequenceEqual(new[] { "/rc oracle-skills off" }), "leaving for another tab sends off once");

            sent.Clear();
            U(OracleView.TabSkills);
            U(OracleView.TabSkills, visible: false); U(OracleView.TabSkills, visible: false);
            Check(sent.SequenceEqual(new[] { "/rc oracle-skills skills", "/rc oracle-skills off" }), "hiding the window (its X, /ro) sends off once");
            sent.Clear();
            U(OracleView.TabSkills);
            Check(sent.SequenceEqual(new[] { "/rc oracle-skills skills" }), "showing it again on the same tab sends the mode again");
            sent.Clear();
            U(OracleView.TabSkills, inWorld: false); U(OracleView.TabSkills, inWorld: false);
            Check(sent.SequenceEqual(new[] { "/rc oracle-skills off" }), "logging out sends off once");
            sent.Clear();
            U(OracleView.TabAbout, visible: false);
            Check(sent.Count == 0 && e.Sent == SkillsEmbed.Off, "already off: nothing more is sent");

            // The first command is the mode itself when the window opens on a Skills tab at login; Shutdown before any command sends nothing.
            var first = new List<string>();
            var fresh = new SkillsEmbed(cmd => { first.Add(cmd); return true; });
            fresh.Update(OracleView.TabAttributes, true, true, 1); fresh.Update(OracleView.TabAttributes, true, true, 2);
            Check(first.SequenceEqual(new[] { "/rc oracle-skills attrs" }), "a window that opens on Attributes sends attrs once, and no separate off");
            var never = new SkillsEmbed(cmd => { first.Add(cmd); return true; });
            first.Clear();
            Check(!never.Shutdown() && first.Count == 0, "Shutdown before any command sends nothing");

            // Shutdown: off if (and only if) a body is up.
            Check(!e.Shutdown() && sent.Count == 0, "Shutdown while off sends nothing");
            U(OracleView.TabProgress);
            sent.Clear();
            Check(e.Shutdown() && sent.SequenceEqual(new[] { "/rc oracle-skills off" }) && e.Sent == SkillsEmbed.Off, "Shutdown while a body is up sends off");
            Check(!e.Shutdown() && sent.Count == 1, "a second Shutdown sends nothing");

            // A refused send is retried, but not every frame.
            sent.Clear();
            accept = false;
            now = 50_000;
            Check(!e.Update(OracleView.TabSkills, true, true, now), "a refused send reports false");
            for (int i = 0; i < 30; i++) e.Update(OracleView.TabSkills, true, true, now + i * 16);
            Check(sent.Count == 1 && e.Sent == SkillsEmbed.Off, $"a refused send is not repeated every frame ({sent.Count} attempts)");
            accept = true;
            Check(!e.Update(OracleView.TabSkills, true, true, now + SkillsEmbed.RetryMs - 1) && sent.Count == 1, "no retry before the gap has passed");
            Check(e.Update(OracleView.TabSkills, true, true, now + SkillsEmbed.RetryMs) && e.Sent == "skills", "retried after the gap, and then recorded");
            accept = false;
            Check(!e.Shutdown() && e.Sent == "skills", "a refused Shutdown send keeps the state");
            accept = true;
            Check(e.Shutdown() && e.Sent == SkillsEmbed.Off, "and a later Shutdown tries again");

            // Which tab wants what.
            Check(SkillsEmbed.Wanted(OracleView.TabSkills, true, true) == "skills" && SkillsEmbed.Wanted(OracleView.TabAttributes, true, true) == "attrs"
                  && SkillsEmbed.Wanted(OracleView.TabProgress, true, true) == "progress", "the three modes are skills, attrs, progress");
            Check(SkillsEmbed.Wanted(OracleView.TabQuests, true, true) == "off" && SkillsEmbed.Wanted(OracleView.TabAbout, true, true) == "off", "every other tab is off");
        }

        /// <summary>Every page has a Refresh button; each does its own reload and says what it did.</summary>
        private static void Refreshes()
        {
            OracleContext c = Context();
            var sent = new List<string>();
            int quests = 0, chars = 0, titles = 0, lists = 0;
            c.Send = cmd => { sent.Add(cmd); return true; };
            c.RequestQuests = () => { quests++; return true; };
            c.RereadCharacter = () => chars++;
            c.RereadTitles = () => titles++;
            c.ReloadQuestList = () => lists++;
            var view = new OracleView(c);
            var host = new UiHost();
            UiWindow main = host.Add(new UiWindow("RynthOracle/Oracle", "Oracle") { Visible = true });
            uint seq = 100;

            string Draw()
            {
                main.Begin(); view.Draw(main); main.End();
                string body = Encoding.UTF8.GetString(main.Body);
                main.MarkSubmitted(true);
                return body;
            }
            void Click(string id)
            {
                Draw();
                Apply(host, Event(1, seq++, main.Hash, main.KeyOf(id), new byte[] { 1, 0, 0 }));
            }
            void Press(string scope) => Click("refresh." + scope);

            // A Refresh button on every tab, on every server.
            foreach (string server in new[] { "aelrynth", "conquest", "" })
            {
                c.Server.Force(server, server);
                foreach (string tab in view.VisibleTabs())
                {
                    if (SkillsEmbed.IsEmbedTab(tab)) continue;   // RynthCore draws these pages; see SkillsTabs()
                    c.Settings.TabName = tab;
                    string body = Draw();
                    Check(body.Contains("Refresh###refresh." + tab), $"{(server.Length > 0 ? server : "other")}/{tab} has a Refresh button");
                }
            }

            // Quests: /myquests, and the line waits for the flags.
            c.Server.Force("", "Coldeve");
            c.Settings.TabName = OracleView.TabQuests;
            Press("Quests");
            Check(quests == 1, "Quests: Refresh asks for the quest flags");
            string waiting = Draw();
            Check(waiting.Contains("Asked the server for your quest flags") && waiting.Contains("Waiting for the reply"), "Quests: the line says it is waiting");
            c.Flags.OnChatLine("freshflag - 1 solves (1719432000)\"x\" -1 0", c.NowMs);
            c.Flags.Tick(c.NowMs + 5000);
            Check(Draw().Contains("Updated "), "Quests: the line says Updated once the reply lands");
            c.NowMs += 20_000;
            Press("Quests");
            Check(Draw().Contains("Waiting for the reply"), "Quests: a second Refresh waits again");
            c.NowMs += 16_000;
            Check(Draw().Contains("No reply yet"), "Quests: no reply for 15 s says so");

            // Tabs built from the character: re-read it; those with quest timers ask for the flags too.
            foreach ((string tab, int wantChars, int wantQuests) in new[]
            {
                (OracleView.TabCharacter, 1, 0), (OracleView.TabAugs, 1, 1), (OracleView.TabSociety, 1, 1),
                (OracleView.TabVoid, 1, 0),
            })
            {
                chars = quests = 0;
                c.Settings.TabName = tab;
                Press(tab);
                Check(chars == wantChars && quests == wantQuests, $"{tab}: Refresh re-reads {wantChars}x character, {wantQuests}x quest flags (got {chars}, {quests})");
                Check(Draw().Contains(wantQuests > 0 ? "Waiting for the reply" : "Refreshed "), $"{tab}: the line says what happened");
            }

            // Titles, Fellowship, About: local reloads.
            c.EarnedTitles.Add(1);
            c.TitlesKnown = true;
            c.Settings.TabName = OracleView.TabTitles;
            Press("Titles");
            Check(titles == 1 && Draw().Contains("Re-read your titles from the game: 1 held."), "Titles: re-reads the titles the engine holds");
            c.Settings.TabName = OracleView.TabFellowship;
            Press("Fellowship");
            Check(Draw().Contains("Re-read the fellowship from the game."), "Fellowship: re-reads the member table");
            c.Settings.TabName = OracleView.TabAbout;
            c.NowMs += 10_000;
            Press("About");
            Check(lists == 1 && Draw().Contains("Reloaded the quest list"), "About: reloads the quest list files");
            Press("About");
            Check(lists == 1 && Draw().Contains("wait a few seconds"), "About: pressed again at once, nothing reloads");
            c.NowMs += 4000;
            Press("About");
            Check(lists == 2, "About: allowed again after the pause");
        }

        private static byte[] Event(ushort type, uint seq, uint window, uint key, byte[] payload)
        {
            var e = new byte[16 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(e, type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)e.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), window);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), key);
            payload.CopyTo(e, 16);
            return e;
        }
    }
}
