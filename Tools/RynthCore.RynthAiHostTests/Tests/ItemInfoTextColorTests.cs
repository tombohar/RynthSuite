using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-05, a player asked to colour the item info lines (AutoID on select, /ra iteminfo, ...)
// without changing their chat type: picking "allegiance" as the chat type for its colour also
// moved the lines to where allegiance lines go. Item Info's "Info text colour" asks the engine
// (SendPluginCommand "RynthCore.Chat" nextcolor, engine UI/Data/ChatOwnColors.cs) to draw the next
// line in that colour just before each line is written; the line keeps its chat type. Unset, the
// lines go out exactly as before. An engine without it answers "no" once and is not asked again.
internal static class ItemInfoTextColorTests
{
    private const uint Player = 0x50000E52;
    private const uint Sword = 0x80266101;
    private const uint ItemTypeMelee = 0x1;
    private const string Target = "RynthCore.Chat";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("item info colour: unset, the lines go out as before and nothing is asked", DefaultUnchanged);
        r.Add("item info colour: set, each line asks for it just before it is written, its chat type unchanged", ColouredLines);
        r.Add("item info colour: an engine without it is asked once, then the lines go out plain", OlderEngine);
        r.Add("item info colour: the Test button and /ra iteminfo me use it too", TestButtonAndSelf);
        r.Add("item info colour: saved with the profile, reset, sanitized, the settings map", SettingsRoundTrip);
        r.Add("item info colour: /ra iteminfo color always answers", Command);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            cache.OnCreateObject(Sword);
            cache.OnCreateObject(Player);
            cache.Tick();
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public void Call(string method, params object[] args) => typeof(RynthAiPlugin).GetMethod(method, Any)!.Invoke(P, args);
        public void Print() => Call("PrintItemInfo", unchecked((int)Sword), true);
        public void Cmd(params string[] words) => Call("HandleItemInfoCommand", new object[] { new[] { "ra", "iteminfo" }.Concat(words).ToArray() });
    }

    /// <summary>A gold long sword, identified, with a spell: pet-style output is several lines.</summary>
    private static Bot World(bool pluginCommands = true, bool delivered = true)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;            // RequestId, HasAppraisalData
        FakeHost.PluginCommandCalls = pluginCommands;
        FakeHost.PluginCommandDelivered = delivered;
        FakeHost.Names[Player] = "Colour Tester";
        FakeHost.Names[Sword] = "Long Sword";
        FakeHost.ItemTypes[Sword] = ItemTypeMelee;
        FakeHost.Containers[Sword] = Player;
        foreach (var (k, v) in new (uint, int)[] { (1, (int)ItemTypeMelee), (19, 9000), (5, 300), (131, 60), (105, 6), (353, 2), (45, 1), (204, 0) })
            FakeHost.Ints[(Sword, k)] = v;
        FakeHost.SpellIds[Sword] = new[] { (uint)SpellDatabase.GetIdByName("Legendary Blood Thirst") };
        FakeHost.Appraised.Add(Sword);
        return new Bot(FakeHost.Create(Player));
    }

    private static List<int> ItemLines() =>
        Enumerable.Range(0, FakeHost.Chat.Count).Where(i => FakeHost.Chat[i].StartsWith("[RynthAi Item] ", StringComparison.Ordinal)).ToList();

    private static List<int> Asks() =>
        Enumerable.Range(0, FakeHost.PluginCommands.Count).Where(i => FakeHost.PluginCommands[i].Plugin == Target).ToList();

    private static void DefaultUnchanged()
    {
        var fresh = new MagItemInfoSettings();
        Check.Eq(fresh.TextColor, "", "Info text colour: the chat type's own colour by default");

        Bot bot = World();
        bot.S.ItemInfoSettings.DetailChatType = 7;
        bot.Print();
        List<int> lines = ItemLines();
        Check.True(lines.Count >= 2, "pet-style: a name line and detail lines: " + string.Join(" | ", FakeHost.Chat));
        Check.Eq(Asks().Count, 0, "no colour asked for");
        Check.Eq(FakeHost.PluginCommands.Count, 0, "nothing sent to the engine at all");
        if (lines.Count >= 2)
        {
            Check.Eq(FakeHost.ChatTypes[lines[0]], 1, "the name line in the Item Info chat type (1)");
            Check.True(lines.Skip(1).All(i => FakeHost.ChatTypes[i] == 7), "detail lines in theirs (7)");
            Check.True(FakeHost.Chat[lines[0]].StartsWith("[RynthAi Item] Gold Long Sword", StringComparison.Ordinal), "the text: " + FakeHost.Chat[lines[0]]);
        }
    }

    private static void ColouredLines()
    {
        Bot bot = World();
        bot.S.ItemInfoSettings.DetailChatType = 7;
        bot.Print();
        List<string> plain = ItemLines().Select(i => FakeHost.Chat[i]).ToList();
        List<int> plainTypes = ItemLines().Select(i => FakeHost.ChatTypes[i]).ToList();

        bot = World();
        bot.S.ItemInfoSettings.DetailChatType = 7;
        bot.S.ItemInfoSettings.TextColor = "#EE921E";
        bot.Print();
        List<int> lines = ItemLines();
        List<int> asks = Asks();
        Check.Eq(lines.Count, plain.Count, "as many lines as without a colour");
        Check.True(lines.Select(i => FakeHost.Chat[i]).SequenceEqual(plain), "the same text");
        Check.True(lines.Select(i => FakeHost.ChatTypes[i]).SequenceEqual(plainTypes), "the same chat types (the same tab and AC chat window)");
        Check.Eq(asks.Count, lines.Count, "one colour asked for per line");
        Check.True(asks.All(i => FakeHost.PluginCommands[i] == (Target, "nextcolor", "#EE921E")), "nextcolor #EE921E: " + string.Join(" | ", FakeHost.PluginCommands));
        Check.True(asks.Select(i => FakeHost.PluginCommandChatCounts[i]).SequenceEqual(lines),
            "each asked for just before its own line (nothing written in between)");
    }

    private static void OlderEngine()
    {
        Bot bot = World(delivered: false);    // an engine from before 2026-11-05: no "RynthCore.Chat"
        bot.S.ItemInfoSettings.TextColor = "#EE921E";
        bot.Print();
        bot.Print();
        Check.Eq(Asks().Count, 1, "asked once");
        Check.Eq(ItemLines().Count % 2, 0, "every line of both prints written");
        Check.True(ItemLines().Count >= 4, "in its chat type's colour: " + string.Join(" | ", FakeHost.Chat));

        bot = World(pluginCommands: false);   // an engine before API v64
        bot.S.ItemInfoSettings.TextColor = "#EE921E";
        bot.Print();
        Check.True(ItemLines().Count >= 2, "no SendPluginCommand at all: the lines still go out");
    }

    private static void TestButtonAndSelf()
    {
        Bot bot = World();
        bot.S.ItemInfoSettings.TextColor = "#3FBFFF";
        bot.Call("TestItemInfoChatType", 18);
        Check.Eq(FakeHost.Chat.LastOrDefault(), "[RynthAi Item] Item info chat test (type 18, colour #3FBFFF)", "the Test line names both");
        Check.Eq(FakeHost.ChatTypes.LastOrDefault(), 18, "in the chat type tested");
        Check.Eq(Asks().Count, 1, "in the colour");

        bot.Call("PrintSelfRatings", Player, true);
        int selfLines = ItemLines().Count - 1;
        Check.True(selfLines >= 1, "your ratings print: " + string.Join(" | ", FakeHost.Chat));
        Check.Eq(Asks().Count, 1 + selfLines, "each ratings line in the colour too");
    }

    private static void SettingsRoundTrip()
    {
        // Profile: save, load (CopySettings), save again unchanged.
        var saved = new LegacyUiSettings();
        saved.ItemInfoSettings.TextColor = "#EE921E";
        string json = JsonSerializer.Serialize(saved, RynthAiJsonContext.Default.LegacyUiSettings);
        var live = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.Static | Any)!
            .Invoke(null, new object[] { JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!, live });
        Check.Eq(live.ItemInfoSettings.TextColor, "#EE921E", "the colour survives a save and load");
        Check.Eq(JsonSerializer.Serialize(live, RynthAiJsonContext.Default.LegacyUiSettings), json, "and saves back unchanged");

        // A profile from before the setting: the chat type's own colour.
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var info = doc[doc.Select(kv => kv.Key).Single(k => k.Equals("ItemInfoSettings", StringComparison.OrdinalIgnoreCase))]!.AsObject();
        string? key = info.Select(kv => kv.Key).SingleOrDefault(k => k.Equals("TextColor", StringComparison.OrdinalIgnoreCase));
        Check.True(key != null, "the field is in the profile JSON");
        if (key != null) info.Remove(key);
        var old = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.Static | Any)!
            .Invoke(null, new object[] { JsonSerializer.Deserialize(doc.ToJsonString(), RynthAiJsonContext.Default.LegacyUiSettings)!, old });
        Check.Eq(old.ItemInfoSettings.TextColor, "", "a profile without it: the chat type's colour");

        // Sanitize tidies a hand-edited value; Reset all to defaults clears it.
        var s = new MagItemInfoSettings { TextColor = " ee921e " };
        s.Sanitize();
        Check.Eq(s.TextColor, "#EE921E", "a code without # is tidied");
        s.TextColor = "orange"; s.Sanitize();
        Check.Eq(s.TextColor, "", "a word that isn't a code: the chat type's colour");
        s.TextColor = "#12345"; s.Sanitize();
        Check.Eq(s.TextColor, "", "a short code: cleared");
        s.TextColor = "#3FBFFF";
        s.ResetToDefaults();
        Check.Eq(s.TextColor, "", "reset: the chat type's colour");

        // The named colours are codes; the first is the default.
        Check.Eq(MagItemInfoCatalog.TextColors[0].Color, "", "the first choice is the chat type's own colour");
        Check.True(MagItemInfoCatalog.TextColors.Skip(1).All(c => MagItemInfoCatalog.NormalizeTextColor(c.Color) == c.Color), "every named colour is a #RRGGBB code");
        Check.Eq(MagItemInfoCatalog.TextColors.Select(c => c.Word).Distinct(StringComparer.OrdinalIgnoreCase).Count(), MagItemInfoCatalog.TextColors.Length, "names are unique");
        Check.Eq(MagItemInfoCatalog.TextColorFromRgb(MagItemInfoCatalog.TextColorRgb("#EE921E")), "#EE921E", "the colour picker round-trips");

        // The settings map (metas: rasetsetting / ragetsetting).
        var mapSettings = new LegacyUiSettings();
        var e = ExpressionTests.Engine(settings: mapSettings);
        e.Evaluate("rasetsetting[ItemInfoTextColor, allegiance]");
        Check.Eq(mapSettings.ItemInfoSettings.TextColor, "#EE921E", "set by name through the map");
        Check.Eq(e.Evaluate("ragetsetting[ItemInfoTextColor]"), "#EE921E", "map reads it");
        e.Evaluate("rasetsetting[ItemInfoTextColor, default]");
        Check.Eq(mapSettings.ItemInfoSettings.TextColor, "", "default through the map");
    }

    private static void Command()
    {
        Bot bot = World();
        bot.Cmd("color", "allegiance");
        Check.Eq(bot.S.ItemInfoSettings.TextColor, "#EE921E", "/ra iteminfo color allegiance");
        Check.True(FakeHost.Chat.Any(c => c.StartsWith("[RynthAi] Item info text colour: Allegiance (orange) #EE921E in RynthCore's chat", StringComparison.Ordinal)),
            "replies with the colour: " + string.Join(" | ", FakeHost.Chat));

        FakeHost.Chat.Clear();
        bot.Cmd("colour", "#12ab34");
        Check.Eq(bot.S.ItemInfoSettings.TextColor, "#12AB34", "/ra iteminfo colour #12ab34");
        Check.True(FakeHost.Chat.Any(c => c.Contains("text colour: #12AB34")), "replies: " + string.Join(" | ", FakeHost.Chat));

        FakeHost.Chat.Clear();
        bot.Cmd("color");
        Check.Eq(bot.S.ItemInfoSettings.TextColor, "#12AB34", "no argument changes nothing");
        Check.True(FakeHost.Chat.Any(c => c.Contains("text colour: #12AB34")), "and says the colour now");

        FakeHost.Chat.Clear();
        bot.Cmd("color", "plaid");
        Check.Eq(bot.S.ItemInfoSettings.TextColor, "#12AB34", "a bad word changes nothing");
        Check.True(FakeHost.Chat.Any(c => c.StartsWith("[RynthAi] Usage: /ra iteminfo color default|allegiance|", StringComparison.Ordinal)),
            "and gets the usage: " + string.Join(" | ", FakeHost.Chat));

        FakeHost.Chat.Clear();
        bot.Cmd("color", "default");
        Check.Eq(bot.S.ItemInfoSettings.TextColor, "", "/ra iteminfo color default");
        Check.True(FakeHost.Chat.Any(c => c.StartsWith("[RynthAi] Item info text colour: the chat type's own colour.", StringComparison.Ordinal)),
            "replies: " + string.Join(" | ", FakeHost.Chat));
    }
}
