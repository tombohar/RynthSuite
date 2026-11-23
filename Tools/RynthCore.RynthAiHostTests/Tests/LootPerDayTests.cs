using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Keep # per day (2026-11-22): a Keep # rule that takes at most its count of matching items picked
// up in a rolling window (24 h), counted by pickups, not by the pack (treasure maps, two a day,
// get used or sold). The counter (LootPerDayStore) on its own, then whole corpses looted with a
// .json and a .utl profile: the count, the leave at the count, the restart (each corpse is a fresh
// plugin reading the character's file), the window freeing up, and another character's count.
internal static class LootPerDayTests
{
    private const uint Player = 0x50000D62;
    private const uint Corpse = 0x80266200;
    private const uint Map1 = 0x80266201, Map2 = 0x80266202, Map3 = 0x80266203;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint LongDescKey = 16;
    private const string Me = "Test Mapper";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const long TickMs = 25;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);
    private static readonly DateTime T0 = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    public static void Register(Runner r)
    {
        r.Add("loot per day: the counter counts pickups in a rolling window and says when the next frees", CounterRollingWindow);
        r.Add("loot per day: the counter survives a restart, keeps characters apart, reads a broken file as none", CounterFiles);
        r.Add("loot per day: a .json Keep # per day loots its count, then leaves the rest (pack count ignored)", JsonCorpses);
        r.Add("loot per day: a .utl Keep # per day (RynthKeepPerDay block) does the same", UtlCorpse);
        r.Add("loot per day: another character has its own count", OtherCharacter);
        r.Add("loot per day: /ra loot today lists the counts and replies; clear forgets one", TodayCommand);
        r.Add("loot per day: the Loot Editor bridge sends the window and the count line, and resets", EditorBridge);
    }

    private static void EditorBridge()
    {
        string utl = UtlProfile();
        var resets = new List<string>();
        var bridge = new LootEditorBridge(() => utl, _ => { }, () => Array.Empty<string>())
        {
            Items = new LootItemHooks
            {
                PerDayStatus = (key, hours, cap) => $"{key}|{hours}|{cap}",
                PerDayRevision = () => 0,
                PerDayReset = key => { resets.Add(key); return "forgot " + key; },
            },
        };
        string state = bridge.StateJson();
        var st = System.Text.Json.JsonSerializer.Deserialize(state, RynthCore.Loot.Editing.LootEditJsonContext.Default.LootEditState)!;
        Check.Eq(st.Rules.Count, 1, "the profile's one rule");
        Check.Eq(st.Rules[0].PerDayHours, 24, "the row's window");
        Check.Eq(st.Rules[0].PerDayStatus, "Treasure Maps|24|2", "the row's count line, by the rule's key, window and count");
        var rule = System.Text.Json.JsonSerializer.Deserialize(bridge.RuleJson(0), RynthCore.Loot.Editing.LootEditJsonContext.Default.LootEditRule)!;
        Check.Eq(rule.PerDayHours, 24, "the rule view's window");
        Check.Eq(rule.PerDayStatus, "Treasure Maps|24|2", "and its count line");
        bridge.Command("{\"Op\":\"perday_reset\",\"Index\":0,\"Expect\":\"Treasure Maps\"}");
        Check.True(resets.SequenceEqual(new[] { "Treasure Maps" }), "Reset count forgets that rule's pickups");
        st = System.Text.Json.JsonSerializer.Deserialize(bridge.StateJson(), RynthCore.Loot.Editing.LootEditJsonContext.Default.LootEditState)!;
        Check.Eq(st.Message, "forgot Treasure Maps", "and says so in the editor");
        bridge.Command("{\"Op\":\"perday_reset\",\"Index\":0,\"Expect\":\"Other\"}");
        Check.Eq(resets.Count, 1, "a stale reset (the list changed) is refused");
    }

    // ── The counter ───────────────────────────────────────────────────────────

    private static void CounterRollingWindow()
    {
        var s = new LootPerDayStore();
        s.Load(string.Empty, T0);
        s.Record("Treasure Maps", 1, T0);
        s.Record("treasure maps", 1, T0.AddHours(3));   // any case: the same rule
        Check.Eq(s.Count("Treasure Maps", 24, T0.AddHours(4)), 2, "two in the last 24 h");
        Check.Eq(s.NextFree("Treasure Maps", 24, 2, T0.AddHours(4)), TimeSpan.FromHours(20), "at the count: free again 24 h after the first");
        Check.Eq(s.Status("Treasure Maps", 24, 2, T0.AddHours(4)), "2 of 2 picked up in the last 24 h, none until 20 h", "status at the count");
        Check.Eq(s.Count("Treasure Maps", 24, T0.AddHours(24).AddMinutes(1)), 1, "a minute past a day the first has dropped out");
        Check.Eq(s.Status("Treasure Maps", 24, 2, T0.AddHours(24).AddMinutes(1)), "1 of 2 picked up in the last 24 h, next frees in 2 h 59 min", "status under the count");
        Check.Eq(s.Count("Treasure Maps", 24, T0.AddHours(28)), 0, "both gone after 27 h");
        Check.Eq(s.Status("Treasure Maps", 24, 2, T0.AddHours(28)), "0 of 2 picked up in the last 24 h", "status with none");
        Check.Eq(s.Count("Treasure Maps", 48, T0.AddHours(28)), 2, "a 48 h window still holds both");
        s.Record("Stacks", 3, T0);
        Check.Eq(s.Count("Stacks", 24, T0), 3, "a stack counts its units");
        Check.Eq(s.NextFree("Stacks", 24, 2, T0.AddHours(1)), TimeSpan.FromHours(23), "over the count, the stack's expiry frees it");
        Check.True(s.Clear("Stacks"), "clear one rule");
        Check.Eq(s.Count("Stacks", 24, T0), 0, "it counts from 0");
        Check.False(s.Clear("Stacks"), "clearing again: nothing to clear");
        long rev = s.Revision;
        s.Record("Treasure Maps", 1, T0.AddDays(8));
        Check.True(s.Revision > rev, "a pickup moves the revision");
        Check.Eq(s.Rules().Single(x => x.Key == "Treasure Maps").Picks.Count, 1, "pickups older than a week are dropped");
        Check.Eq(LootPerDayStore.Duration(TimeSpan.FromSeconds(20)), "1 min", "durations round up to the minute");
        Check.Eq(LootPerDayStore.Duration(TimeSpan.Zero), "under a minute", "and zero says so");
    }

    private static void CounterFiles()
    {
        string a = Path.Combine(Program.TempRoot, "perday-store", "SrvA", "One", LootPerDayStore.FileName);
        string b = Path.Combine(Program.TempRoot, "perday-store", "SrvA", "Two", LootPerDayStore.FileName);
        var one = new LootPerDayStore();
        one.Load(a, T0);
        one.Record("Treasure Maps", 1, T0);
        one.Record("Treasure Maps", 1, T0.AddMinutes(5));
        Check.True(File.Exists(a), "saved at once, in the character's folder");
        var two = new LootPerDayStore();
        two.Load(b, T0);
        Check.Eq(two.Count("Treasure Maps", 24, T0.AddHours(1)), 0, "another character's file is its own");
        var restart = new LootPerDayStore();
        restart.Load(a, T0.AddHours(1));
        Check.Eq(restart.Count("Treasure Maps", 24, T0.AddHours(1)), 2, "after a restart the count is back");
        Check.Eq(restart.Serialize(), one.Serialize(), "and the same pickups");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllText(b, "{ not json");
        var broken = new LootPerDayStore();
        broken.Load(b, T0);
        Check.Eq(broken.Count("Treasure Maps", 24, T0), 0, "a broken file is no pickups");
        Check.True(broken.Problem.Length > 0, "with the problem noted: " + broken.Problem);
        broken.Record("Treasure Maps", 1, T0);
        Check.Eq(broken.Problem, string.Empty, "the next pickup writes a good file");
        var fixedUp = new LootPerDayStore();
        fixedUp.Load(b, T0);
        Check.Eq(fixedUp.Count("Treasure Maps", 24, T0), 1, "which reads back");
    }

    // ── Corpses ───────────────────────────────────────────────────────────────

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        var oldDay = RynthAiPlugin.PerDayClock;
        _now = 90_000_000;
        _day = T0;
        RynthAiPlugin.CorpseClock = () => _now;
        RynthAiPlugin.PerDayClock = () => _day.AddMilliseconds(_now - 90_000_000);
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; RynthAiPlugin.PerDayClock = oldDay; }
    }

    private static DateTime _day;

    private static string CharFolder(string who) => Path.Combine(Program.TempRoot, "perday", "SettingsProfiles", "TestServer", who);

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;
        public readonly LegacyDashboardRenderer Dash;
        private int _seenMoves;

        public Bot(RynthCoreHost host, string profile, string who)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            Dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(Dash, true);
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(Dash, S);
            typeof(LegacyDashboardRenderer).GetField("_charFolder", Any)!.SetValue(Dash, CharFolder(who));
            Set("_dashboard", Dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        public void Tick()
        {
            _now += TickMs;
            Set("_activity", BotActivity.Looting);
            Call("TickCorpseOpening");
            while (_seenMoves < FakeHost.ExternalMoves.Count)
            {
                var m = FakeHost.ExternalMoves[_seenMoves++];
                FakeHost.Containers[m.Id] = m.Target;
            }
        }

        public void Create(params uint[] ids)
        {
            foreach (uint id in ids) Cache.OnCreateObject(id);
            for (int i = 0; i < 5; i++) Cache.Tick();
        }

        public bool CorpseDone => Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)Corpse));
    }

    private static string JsonProfile()
    {
        string path = Path.Combine(Program.TempRoot, "loot-perday.json");
        var p = new LootProfile { Name = "per day" };
        var rule = new LootRule { Name = "Treasure Maps", Action = LootAction.KeepUpTo, KeepCount = 2, PerDayHours = 24 };
        rule.Conditions.Add(new StringValueCondition { Key = 1, Pattern = "^Treasure Map$" });
        p.Rules.Add(rule);
        p.Save(path);
        return path;
    }

    private static string UtlProfile()
    {
        string path = Path.Combine(Program.TempRoot, "loot-perday.utl");
        var p = new VTankLootProfile { FileVersion = 1 };
        p.Rules.Add(new VTankLootRule
        {
            Name = "Treasure Maps", CustomExpression = "", Action = VTankLootAction.KeepUpTo, KeepCount = 2, PerDayHours = 24,
            Conditions = { new VTankLootCondition(VTankNodeTypes.StringValueMatch, "0", new[] { "^Treasure Map$", "1" }) },
        });
        VTankLootWriter.Save(p, path);
        Check.True(File.ReadAllText(path).Contains(LootPerDay.UtlBlockName), "the .utl carries the per-day block");
        return path;
    }

    /// <summary>A corpse with <paramref name="maps"/> treasure maps (and <paramref name="inPack"/> in the pack), looted to its end by a fresh plugin.</summary>
    private static Bot LootCorpse(string profile, string who, uint[] maps, uint[]? inPack = null)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        FakeHost.WeaponCalls = true;
        FakeHost.CloseContainerCalls = true;
        FakeHost.ContentsCalls = true;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Cartographer";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        foreach (uint id in maps) AddMap(id, Corpse);
        foreach (uint id in inPack ?? Array.Empty<uint>()) AddMap(id, Player);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, profile, who);
        bot.Create(new[] { Corpse }.Concat(maps).Concat(inPack ?? Array.Empty<uint>()).ToArray());
        long end = _now + 30_000;
        bool opened = false;
        while (_now < end && !bot.CorpseDone)
        {
            bot.Tick();
            if (!opened && FakeHost.Uses.Contains(Corpse))
            {
                opened = true;
                FakeHost.UseDone(0);
                FakeHost.GroundContainer = Corpse;
                bot.P.OnViewObjectContents(Corpse);
                bot.P.OnUpdateObjectInventory(Corpse);
            }
        }
        Check.True(opened, "the corpse is opened");
        Check.True(bot.CorpseDone, "the corpse is finished");
        return bot;
    }

    private static void AddMap(uint id, uint container)
    {
        FakeHost.Names[id] = "Treasure Map";
        FakeHost.ItemTypes[id] = ItemTypeGem;
        FakeHost.Containers[id] = container;
        FakeHost.Appraised.Add(id);
    }

    private static int Looted() => FakeHost.ExternalMoves.Where(m => m.Target == Player).Select(m => m.Id).Distinct().Count();
    private static bool Said(string part) => FakeHost.Chat.Any(l => l.Contains(part, StringComparison.Ordinal));
    private static string ChatDump() => string.Join(" | ", FakeHost.Chat);

    private static void ClearFolder(string who)
    {
        string f = CharFolder(who);
        if (Directory.Exists(f)) Directory.Delete(f, recursive: true);
    }

    private static int FileCount(string who)
    {
        var s = new LootPerDayStore();
        s.Load(Path.Combine(CharFolder(who), LootPerDayStore.FileName), _day.AddMilliseconds(_now - 90_000_000));
        return s.Count("Treasure Maps", 24, _day.AddMilliseconds(_now - 90_000_000));
    }

    private static void JsonCorpses() => Clocked(() =>
    {
        ClearFolder("Mapper");
        string profile = JsonProfile();

        // Three maps, a count of 2: two are looted, the third left.
        LootCorpse(profile, "Mapper", new[] { Map1, Map2, Map3 });
        Check.Eq(Looted(), 2, $"two maps looted ({ChatDump()})");
        Check.True(Said("Keep # per day 'Treasure Maps': 2/2 picked up in the last 24 h"), $"the third is left at the count ({ChatDump()})");
        Check.Eq(FileCount("Mapper"), 2, "the character's file holds both pickups");

        // The maps were used up (none in the pack). A new corpse, a fresh plugin (a restart): still 2 today.
        _now += 3_600_000;
        LootCorpse(profile, "Mapper", new[] { Map1 });
        Check.Eq(Looted(), 0, $"an hour later the map is left: 2 picked up today, though the pack has none ({ChatDump()})");
        Check.True(Said("(the next in 23 h"), $"and when the next frees is said ({ChatDump()})");

        // A plain Keep # would have looted it (the pack is empty): this one counts pickups.
        _now += 23L * 3_600_000 + 60_000;
        LootCorpse(profile, "Mapper", new[] { Map1 }, inPack: new[] { Map2, Map3 });
        Check.Eq(Looted(), 1, $"24 h after the first two the map is looted again, with two maps in the pack ({ChatDump()})");
        Check.Eq(FileCount("Mapper"), 1, "one in the window now");
    });

    private static void UtlCorpse() => Clocked(() =>
    {
        ClearFolder("UtlMapper");
        string profile = UtlProfile();
        LootCorpse(profile, "UtlMapper", new[] { Map1, Map2, Map3 });
        Check.Eq(Looted(), 2, $".utl: two maps looted ({ChatDump()})");
        Check.True(Said("Keep # per day 'Treasure Maps': 2/2"), $".utl: the third is left ({ChatDump()})");
        Check.Eq(FileCount("UtlMapper"), 2, ".utl: counted in the character's file");
    });

    private static void OtherCharacter() => Clocked(() =>
    {
        ClearFolder("Mapper");
        ClearFolder("Alt");
        string profile = JsonProfile();
        LootCorpse(profile, "Mapper", new[] { Map1, Map2 });
        Check.Eq(Looted(), 2, "the first character loots its two");
        LootCorpse(profile, "Alt", new[] { Map1, Map2, Map3 });
        Check.Eq(Looted(), 2, $"another character on the same server loots its own two ({ChatDump()})");
        Check.Eq(FileCount("Mapper"), 2, "the first character's count");
        Check.Eq(FileCount("Alt"), 2, "the other's");
    });

    private static void TodayCommand() => Clocked(() =>
    {
        ClearFolder("Mapper");
        string profile = JsonProfile();
        Bot bot = LootCorpse(profile, "Mapper", new[] { Map1 });
        FakeHost.Chat.Clear();
        bot.Call("HandleLootCommand", new object[] { new[] { "/ra", "loot", "today" } });
        Check.True(Said("Keep # per day (1 rule)"), $"the reply ({ChatDump()})");
        Check.True(Said("'Treasure Maps': 1 of 2 picked up in the last 24 h, next frees in 24 h"), $"the rule's line ({ChatDump()})");
        FakeHost.Chat.Clear();
        bot.Call("HandleLootCommand", new object[] { new[] { "/ra", "loot", "today", "clear", "Treasure", "Maps" } });
        Check.True(Said("pickups forgotten"), $"clear replies ({ChatDump()})");
        Check.Eq(FileCount("Mapper"), 0, "and the file is cleared");
        FakeHost.Chat.Clear();
        bot.Call("HandleLootCommand", new object[] { new[] { "/ra", "loot", "today", "bogus" } });
        Check.True(Said("Usage: /ra loot today"), $"a wrong word says the usage ({ChatDump()})");

        // The Loot Editor's line and reset (LootEditorBridge hooks).
        Check.Eq((string)bot.Call("PerDayStatusFor", "Treasure Maps", 24, 2)!, "0 of 2 picked up in the last 24 h", "the editor's line after the clear");
        Check.True(((string)bot.Call("PerDayReset", "Treasure Maps")!).Contains("no pickups on record"), "the editor's reset with none");
    });
}
