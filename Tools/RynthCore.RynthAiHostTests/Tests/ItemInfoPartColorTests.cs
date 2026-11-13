using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-11, a player asked for the one-line item info with each part (name, set, damage,
// spells, value ...) in its own colour. "Colour each part" sends the parts' ranges just before the
// line (SendPluginCommand "RynthCore.Chat" nextspans, engine UI/Data/ChatOwnColors.cs), after the
// whole-line Info text colour, so an engine without runs still shows the line in that colour.
internal static class ItemInfoPartColorTests
{
    private const uint Player = 0x50000E52;
    private const uint Sword = 0x80266101;
    private const uint ItemTypeMelee = 0x1;
    private const string Target = "RynthCore.Chat";
    private const string Prefix = "[RynthAi Item] ";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("item info parts: off by default, nothing asked, the line as before", DefaultOff);
        r.Add("item info parts: on, one-line: the ranges asked for just before the line", OneLine);
        r.Add("item info parts: with Info text colour, the colour first and then the ranges", WithTextColor);
        r.Add("item info parts: the pet-style layout is left alone", PetLayout);
        r.Add("item info parts: an engine without runs is asked once; the line keeps one colour", OlderEngine);
        r.Add("item info parts: a refused payload is logged, not taken for an older engine", RefusedPayload);
        r.Add("item info parts: the Test button", TestButton);
        r.Add("item info parts: saved with the profile, reset, the settings map, /ra iteminfo parts", Settings);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            cache.OnCreateObject(Sword);
            cache.OnCreateObject(Player);
            cache.Tick();
            S.ItemInfoSettings.Layout = MagItemInfoSettings.LayoutOneLine;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public void Call(string method, params object[] args) => typeof(RynthAiPlugin).GetMethod(method, Any, null,
            args.Select(a => a.GetType()).ToArray(), null)!.Invoke(P, args);
        public void Print() => Call("PrintItemInfo", unchecked((int)Sword), true);
        public void Cmd(params string[] words) => Call("HandleItemInfoCommand", new object[] { new[] { "ra", "iteminfo" }.Concat(words).ToArray() });
    }

    /// <summary>A gold long sword with a set, a spell, damage, workmanship, value and burden.</summary>
    private static Bot World(Func<string, string, string, bool>? answer = null)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.PluginCommandCalls = true;
        FakeHost.PluginCommandAnswer = answer;
        FakeHost.Names[Player] = "Colour Tester";
        FakeHost.Names[Sword] = "Long Sword";
        FakeHost.ItemTypes[Sword] = ItemTypeMelee;
        FakeHost.Containers[Sword] = Player;
        foreach (var (k, v) in new (uint, int)[] { (1, (int)ItemTypeMelee), (19, 9000), (5, 300), (131, 60), (105, 6), (353, 2), (45, 1), (44, 236), (265, 13) })
            FakeHost.Ints[(Sword, k)] = v;
        FakeHost.SpellIds[Sword] = new[] { (uint)SpellDatabase.GetIdByName("Legendary Blood Thirst") };
        FakeHost.Appraised.Add(Sword);
        var bot = new Bot(FakeHost.Create(Player));
        bot.S.ItemInfoSettings.ShowValueAndBurden = true;
        return bot;
    }

    private static List<int> ItemLines() =>
        Enumerable.Range(0, FakeHost.Chat.Count).Where(i => FakeHost.Chat[i].StartsWith(Prefix, StringComparison.Ordinal)).ToList();

    private static List<int> Asks(string action) =>
        Enumerable.Range(0, FakeHost.PluginCommands.Count)
            .Where(i => FakeHost.PluginCommands[i].Plugin == Target && FakeHost.PluginCommands[i].Action == action).ToList();

    private static string All() => string.Join(" | ", FakeHost.Chat) + " || " + string.Join(" | ", FakeHost.PluginCommands);

    /// <summary>The payload's ranges as (text, colour) in <paramref name="line"/>; null if a range doesn't fit.</summary>
    private static List<(string Text, string Color)>? Read(string payload, string line)
    {
        var list = new List<(string, string)>();
        foreach (string run in payload.Split(';'))
        {
            string[] f = run.Split(':');
            if (f.Length != 3) return null;
            int start = int.Parse(f[0]), len = int.Parse(f[1]);
            if (start < 0 || len < 1 || start + len > line.Length) return null;
            list.Add((line.Substring(start, len), "#" + f[2]));
        }
        return list;
    }

    private static void DefaultOff()
    {
        Check.False(new MagItemInfoSettings().PartColors, "off by default");
        Bot bot = World();
        bot.Print();
        Check.Eq(ItemLines().Count, 1, "one line: " + All());
        Check.Eq(FakeHost.PluginCommands.Count, 0, "nothing sent to the engine");
    }

    private static void OneLine()
    {
        Bot bot = World();
        bot.Print();
        string plain = FakeHost.Chat[ItemLines().Single()];

        bot = World();
        bot.S.ItemInfoSettings.PartColors = true;
        bot.Print();
        List<int> lines = ItemLines();
        Check.Eq(lines.Count, 1, "one line: " + All());
        string line = FakeHost.Chat[lines[0]];
        Check.Eq(line, plain, "the same text as without part colours");
        Check.Eq(FakeHost.ChatTypes[lines[0]], 1, "in its chat type");
        List<int> asks = Asks("nextspans");
        Check.Eq(asks.Count, 1, "the ranges asked for once: " + All());
        Check.Eq(Asks("nextcolor").Count, 0, "no Info text colour set: no nextcolor");
        if (asks.Count != 1) return;
        Check.Eq(FakeHost.PluginCommandChatCounts[asks[0]], lines[0], "just before the line");
        var runs = Read(FakeHost.PluginCommands[asks[0]].Value, line);
        Check.NotNull(runs, "every range inside the line: " + FakeHost.PluginCommands[asks[0]].Value);
        if (runs == null) return;
        Check.True(runs[0].Text.StartsWith("Gold Long Sword", StringComparison.Ordinal), "the first range is the name: " + runs[0].Text);
        Check.Eq(runs[0].Color, ItemInfoPartColors.ColorOf(ItemInfoPart.Name), "in the name's colour");
        Check.True(runs.Any(r => r.Text == "Legendary Blood Thirst" && r.Color == ItemInfoPartColors.ColorOf(ItemInfoPart.Spells)), "the spell in the spells' colour: " + string.Join(" | ", runs));
        Check.True(runs.Any(r => r.Text == "Craft 6" && r.Color == ItemInfoPartColors.ColorOf(ItemInfoPart.Craft)), "workmanship in its colour");
        Check.True(runs.Any(r => r.Text == "Value 9,000, BU 300" && r.Color == ItemInfoPartColors.ColorOf(ItemInfoPart.ValueBurden)), "value and burden, one range");
        Check.True(runs.Any(r => r.Color == ItemInfoPartColors.ColorOf(ItemInfoPart.Set)), "the set in its colour");
        Check.True(runs.All(r => !r.Text.StartsWith("[RynthAi", StringComparison.Ordinal)), "the prefix keeps the line's colour");
    }

    private static void WithTextColor()
    {
        Bot bot = World();
        bot.S.ItemInfoSettings.PartColors = true;
        bot.S.ItemInfoSettings.TextColor = "#EE921E";
        bot.Print();
        int line = ItemLines().Single();
        List<int> colours = Asks("nextcolor"), spans = Asks("nextspans");
        Check.Eq(colours.Count, 1, "the whole-line colour once: " + All());
        Check.Eq(spans.Count, 1, "the ranges once");
        if (colours.Count == 1 && spans.Count == 1)
        {
            Check.True(colours[0] < spans[0], "the colour first (an engine without runs still uses it)");
            Check.Eq(FakeHost.PluginCommands[colours[0]].Value, "#EE921E", "the Info text colour");
            Check.Eq(FakeHost.PluginCommandChatCounts[spans[0]], line, "both just before the line");
        }
    }

    private static void PetLayout()
    {
        Bot bot = World();
        bot.S.ItemInfoSettings.PartColors = true;
        bot.S.ItemInfoSettings.Layout = MagItemInfoSettings.LayoutPetLines;
        bot.Print();
        Check.True(ItemLines().Count >= 2, "pet-style lines: " + All());
        Check.Eq(Asks("nextspans").Count, 0, "no ranges for them");
    }

    private static void OlderEngine()
    {
        // An engine from 2026-11-05..11-10: "nextcolor" yes, "nextspans" (and its "none") no.
        Bot bot = World((p, a, v) => p == Target && a == "nextcolor");
        bot.S.ItemInfoSettings.PartColors = true;
        bot.S.ItemInfoSettings.TextColor = "#EE921E";
        bot.Print();
        bot.Print();
        Check.Eq(ItemLines().Count, 2, "both lines written: " + All());
        Check.Eq(Asks("nextcolor").Count, 2, "each still in the Info text colour");
        List<int> spans = Asks("nextspans");
        Check.Eq(spans.Count, 2, "asked once, plus the \"none\" that tells why");
        if (spans.Count == 2) Check.Eq(FakeHost.PluginCommands[spans[1]].Value, "none", "the second ask is \"none\"");
        Check.True(Asks("nextcolor").All(i => FakeHost.PluginCommandChatCounts[i] == 0 || FakeHost.PluginCommandChatCounts[i] == ItemLines()[1]),
            "the colour asked for just before each line");

        // An engine before 2026-11-05 (no "RynthCore.Chat" at all): nextcolor refused, no runs asked.
        bot = World((p, a, v) => false);
        bot.S.ItemInfoSettings.PartColors = true;
        bot.S.ItemInfoSettings.TextColor = "#EE921E";
        bot.Print();
        bot.Print();
        Check.Eq(ItemLines().Count, 2, "both lines written");
        Check.Eq(Asks("nextcolor").Count, 1, "the colour asked once");
        Check.Eq(Asks("nextspans").Count, 0, "no runs asked of an engine without line colours");

        // Same, with no Info text colour: the ranges are asked once (and "none"), then never.
        bot = World((p, a, v) => false);
        bot.S.ItemInfoSettings.PartColors = true;
        bot.Print();
        bot.Print();
        Check.Eq(ItemLines().Count, 2, "both lines written (the chat type's colour)");
        Check.Eq(Asks("nextspans").Count, 2, "asked once, plus \"none\"");
    }

    private static void RefusedPayload()
    {
        // The engine knows "nextspans" but refuses this payload: logged, still on for the next line.
        Bot bot = World((p, a, v) => a != "nextspans" || v == "none");
        bot.S.ItemInfoSettings.PartColors = true;
        bot.Print();
        bot.Print();
        Check.Eq(ItemLines().Count, 2, "both lines written: " + All());
        Check.Eq(Asks("nextspans").Count, 4, "each line asks (and \"none\"): not taken for an older engine");
    }

    private static void TestButton()
    {
        Bot bot = World();
        bot.S.ItemInfoSettings.PartColors = true;
        bot.Call("TestItemInfoPartColors");
        string line = FakeHost.Chat.LastOrDefault() ?? "";
        Check.True(line.StartsWith(Prefix + "Name, Set, ", StringComparison.Ordinal), "a sample line naming the parts: " + line);
        Check.Eq(FakeHost.ChatTypes.LastOrDefault(), 1, "in the Item Info chat type");
        List<int> spans = Asks("nextspans");
        Check.Eq(spans.Count, 1, "with the ranges: " + All());
        if (spans.Count != 1) return;
        var runs = Read(FakeHost.PluginCommands[spans[0]].Value, line);
        Check.NotNull(runs, "inside the line");
        if (runs == null) return;
        Check.Eq(runs.Count, ItemInfoPartColors.Defaults.Length, "one per part");
        Check.True(runs.Select(r => r.Text).SequenceEqual(ItemInfoPartColors.Defaults.Select(d => d.Label)), "each part's label");
        Check.True(runs.Select(r => r.Color).SequenceEqual(ItemInfoPartColors.Defaults.Select(d => d.Color)), "in its colour");
    }

    private static void Settings()
    {
        var saved = new LegacyUiSettings();
        saved.ItemInfoSettings.PartColors = true;
        string json = JsonSerializer.Serialize(saved, RynthAiJsonContext.Default.LegacyUiSettings);
        var live = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.Static | Any)!
            .Invoke(null, new object[] { JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!, live });
        Check.True(live.ItemInfoSettings.PartColors, "survives a save and load");
        Check.Eq(JsonSerializer.Serialize(live, RynthAiJsonContext.Default.LegacyUiSettings), json, "and saves back unchanged");

        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var info = doc[doc.Select(kv => kv.Key).Single(k => k.Equals("ItemInfoSettings", StringComparison.OrdinalIgnoreCase))]!.AsObject();
        string? key = info.Select(kv => kv.Key).SingleOrDefault(k => k.Equals("PartColors", StringComparison.OrdinalIgnoreCase));
        Check.True(key != null, "the field is in the profile JSON");
        if (key != null) info.Remove(key);
        var old = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.Static | Any)!
            .Invoke(null, new object[] { JsonSerializer.Deserialize(doc.ToJsonString(), RynthAiJsonContext.Default.LegacyUiSettings)!, old });
        Check.False(old.ItemInfoSettings.PartColors, "a profile without it: off");

        var s = new MagItemInfoSettings { PartColors = true };
        s.ResetToDefaults();
        Check.False(s.PartColors, "reset: off");

        var mapSettings = new LegacyUiSettings();
        var e = ExpressionTests.Engine(settings: mapSettings);
        e.Evaluate("rasetsetting[ItemInfoPartColors, 1]");
        Check.True(mapSettings.ItemInfoSettings.PartColors, "set through the settings map");
        Check.Eq(e.Evaluate("ragetsetting[ItemInfoPartColors]"), "1", "map reads it");
        e.Evaluate("rasetsetting[ItemInfoPartColors, 0]");
        Check.False(mapSettings.ItemInfoSettings.PartColors, "off through the map");

        Bot bot = World();
        bot.Cmd("parts", "on");
        Check.True(bot.S.ItemInfoSettings.PartColors, "/ra iteminfo parts on");
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Item info colour per part: ON"), "replies with the value: " + All());
        FakeHost.Chat.Clear();
        bot.Cmd("parts");
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Item info colour per part: ON"), "no argument: says the value now");
        FakeHost.Chat.Clear();
        bot.Cmd("parts", "maybe");
        Check.True(bot.S.ItemInfoSettings.PartColors, "a bad argument changes nothing");
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Usage: /ra iteminfo parts on|off"), "and gets the usage: " + All());
        FakeHost.Chat.Clear();
        bot.S.ItemInfoSettings.Layout = MagItemInfoSettings.LayoutPetLines;
        bot.Cmd("parts", "on");
        Check.True(FakeHost.Chat.Any(c => c.Contains("the one-line layout only")), "on with the pet layout: says it needs the one-line layout");
        FakeHost.Chat.Clear();
        bot.Cmd("parts", "off");
        Check.False(bot.S.ItemInfoSettings.PartColors, "/ra iteminfo parts off");
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Item info colour per part: OFF"), "replies off");
    }
}
