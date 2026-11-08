using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-31, a player asked for loot details in chat "like magtools does": the looter's "Looted"
// line of an item a loot rule took names the rule and the item's Mag-style details, in one line
// (Item Info's "Describe looted items in chat", on by default). Salvage stays short unless
// "Include salvage" is on, stackables (pyreals, notes, comps) keep the plain line, and the head stays
// "[RynthAi] Looted [<action>] <name>" for the self-test meta. One corpse: a crossbow (Keep rule),
// a helm (Salvage rule) and a stack of pyreals (Keep rule). The loot clock (CorpseClock) is moved by hand.
internal static class LootChatLineTests
{
    private const uint Player = 0x50000E51;
    private const uint Corpse = 0x80265100;
    private const uint Xbow = 0x80265101, Helm = 0x80265102, Coins = 0x80265103;
    private const uint ItemTypeContainer = 0x200, ItemTypeMissile = 0x100, ItemTypeArmor = 0x2, ItemTypeMoney = 0x40;
    private const uint LongDescKey = 16;
    private const string Me = "Loot Tester";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("loot chat: a looted keeper prints one line with its rule and its Mag-style details", KeeperDescribed);
        r.Add("loot chat: salvage is quiet by default, and short (material, workmanship) with Include salvage", SalvageQuietByDefault);
        r.Add("loot chat: pyreals, notes, comps and other stacks print the plain line only", StackablePlain);
        r.Add("loot chat: Describe looted items off prints the plain lines", SettingOffPlain);
        r.Add("loot chat: the settings survive a profile save and load, the settings map and /ra iteminfo loot", SettingsRoundTrip);
        r.Add("loot chat: Info text colour colours the described line only, in its own chat type", DescribedLineInTextColor);
    }

    // 2026-11-05: Item Info's "Info text colour" covers the described "Looted" line too; the plain
    // lines (salvage by default, pyreals) are not item info and never ask for a colour.
    private static void DescribedLineInTextColor() => Clocked(() =>
    {
        List<string> looted = LootCorpse(s => { s.TextColor = "#EE921E"; s.LootChatType = 18; }, pluginCommands: true);
        string line = LineFor(looted, "Blunt Crossbow");
        int at = FakeHost.Chat.IndexOf(line);
        var asks = Enumerable.Range(0, FakeHost.PluginCommands.Count).Where(i => FakeHost.PluginCommands[i].Plugin == "RynthCore.Chat").ToList();
        Check.Eq(asks.Count, 1, "one colour asked for (the described keeper): " + string.Join(" | ", FakeHost.PluginCommands));
        if (asks.Count == 1)
        {
            Check.Eq(FakeHost.PluginCommands[asks[0]], ("RynthCore.Chat", "nextcolor", "#EE921E"), "the colour");
            Check.Eq(FakeHost.PluginCommandChatCounts[asks[0]], at, "asked for just before the line is written");
        }
        Check.Eq(FakeHost.ChatTypes[at], 18, "the line keeps its loot chat type");
        Check.True(line.Contains("(rule \"Keep bows\"): Gold"), "and its text: " + line);

        looted = LootCorpse(pluginCommands: true);
        Check.False(FakeHost.PluginCommands.Any(c => c.Plugin == "RynthCore.Chat"), "no colour set: nothing asked (as before)");
    });

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 70_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;          // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;           // RequestId, HasAppraisalData
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Olthoi Swarm Drone";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);

        // A gold crossbow, identified: workmanship 6, crossbow, pierce, +22, +162%, 15% melee defense,
        // Legendary Blood Thirst.
        Item(Xbow, "Blunt Crossbow", ItemTypeMissile, (19, 12000), (5, 600), (131, 60), (105, 6), (353, 9), (45, 2), (204, 22));
        FakeHost.Doubles[(Xbow, 63)] = 2.62;
        FakeHost.Doubles[(Xbow, 29)] = 1.15;
        FakeHost.SpellIds[Xbow] = new[] { (uint)SpellDatabase.GetIdByName("Legendary Blood Thirst") };
        FakeHost.Appraised.Add(Xbow);

        // A steel helm for salvage, identified: workmanship 5, AL 300.
        Item(Helm, "Helm", ItemTypeArmor, (19, 900), (5, 300), (131, 64), (105, 5), (28, 300));
        FakeHost.Appraised.Add(Helm);

        // A stack of 50 pyreals.
        Item(Coins, "Pyreal", ItemTypeMoney, (19, 50), (11, 25000), (12, 50));
    }

    private static void Item(uint id, string name, uint itemType, params (uint Key, int Value)[] ints)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = itemType;
        FakeHost.Containers[id] = Corpse;
        FakeHost.Ints[(id, 1)] = unchecked((int)itemType);
        foreach (var (k, v) in ints) FakeHost.Ints[(id, k)] = v;
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
            LootAssessWindowMs = 200, LootCorpseTimeoutMs = 12_000,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });

            var p = new VTankLootProfile();
            p.Rules.Add(Rule("Keep bows", VTankLootAction.Keep, AcObjectClass.MissileWeapon));
            p.Rules.Add(Rule("Salvage armor", VTankLootAction.Salvage, AcObjectClass.Armor));
            p.Rules.Add(Rule("Coins", VTankLootAction.Keep, AcObjectClass.Money));
            string path = Path.Combine(Program.TempRoot, "loot-chat-line.utl");
            File.WriteAllText(path, VTankLootWriter.Serialize(p));
            S.CurrentLootPath = path;
        }

        private static VTankLootRule Rule(string name, VTankLootAction action, AcObjectClass cls)
        {
            var rule = new VTankLootRule { Name = name, Action = action };
            rule.Conditions.Add(new VTankLootCondition(VTankNodeTypes.ObjectClass, "0", new[] { ((int)cls).ToString() }));
            return rule;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public void Tick()
        {
            Set("_activity", BotActivity.Looting);
            typeof(RynthAiPlugin).GetMethod("TickCorpseOpening", Any)!.Invoke(P, null);
        }
        public bool Completed(uint corpse) =>
            Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)corpse));
    }

    /// <summary>Opens the corpse and loots it, each item landing in the main pack as soon as its move goes out.</summary>
    private static List<string> LootCorpse(Action<MagItemInfoSettings>? configure = null, bool pluginCommands = false)
    {
        World();
        FakeHost.PluginCommandCalls = pluginCommands;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        configure?.Invoke(bot.S.ItemInfoSettings);
        bot.Cache.OnCreateObject(Corpse);
        foreach (uint id in new[] { Xbow, Helm, Coins }) bot.Cache.OnCreateObject(id);
        bot.Cache.Tick();
        bot.Tick();
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");
        FakeHost.UseDone(0);
        bot.P.OnViewObjectContents(Corpse);

        var landed = new HashSet<uint>();
        for (int i = 0; i < 3000 && !(bot.Completed(Corpse) && landed.Count == 3); i++)
        {
            foreach (var m in FakeHost.ExternalMoves)
                if (landed.Add(m.Id)) FakeHost.Containers[m.Id] = Player;
            _now += 20;
            bot.Tick();
        }
        List<string> looted = FakeHost.Chat.Where(c => c.StartsWith("[RynthAi] Looted [", StringComparison.Ordinal)).ToList();
        Check.Eq(looted.Count, 3, "one Looted line per item: " + string.Join(" | ", looted));
        Check.False(FakeHost.Chat.Any(c => c.StartsWith("[RynthAi Item]", StringComparison.Ordinal)), "no separate item info lines");
        return looted;
    }

    private static string LineFor(List<string> looted, string name) =>
        looted.SingleOrDefault(l => l.Contains("] " + name, StringComparison.Ordinal)) ?? "(none)";

    private static void KeeperDescribed() => Clocked(() =>
    {
        List<string> looted = LootCorpse();
        string line = LineFor(looted, "Blunt Crossbow");
        Check.True(line.StartsWith("[RynthAi] Looted [Keep] Blunt Crossbow (rule \"Keep bows\"): Gold, Pierce Crossbow, ", StringComparison.Ordinal),
            "the head, the rule, material and type: " + line);
        foreach (string part in new[] { "+22", "+162%", "+15%md", "Legendary Blood Thirst", "Craft 6" })
            Check.True(line.Contains(part, StringComparison.Ordinal), $"'{part}' in: {line}");
        Check.False(line.Contains('\n'), "one line");
        Check.False(line.Contains("not IDed"), "identified");
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("Looted [Keep] Blunt Crossbow")), 1, "said once");

        // The Item Info field settings apply: spells hidden, no spells on the loot line either.
        looted = LootCorpse(s => s.SetHidden(MagItemInfoField.Spells, true));
        line = LineFor(looted, "Blunt Crossbow");
        Check.False(line.Contains("Blood Thirst"), "a hidden field stays hidden: " + line);
        Check.True(line.Contains("+22"), "the rest still shows: " + line);
    });

    private static void SalvageQuietByDefault() => Clocked(() =>
    {
        List<string> looted = LootCorpse();
        Check.Eq(LineFor(looted, "Helm"), "[RynthAi] Looted [Salvage] Helm", "salvage keeps the short line by default");

        looted = LootCorpse(s => s.LootChatSalvage = true);
        Check.Eq(LineFor(looted, "Helm"), "[RynthAi] Looted [Salvage] Helm (rule \"Salvage armor\"): Steel, Craft 5",
            "Include salvage: the rule, material and workmanship only (no AL)");
    });

    private static void StackablePlain() => Clocked(() =>
    {
        List<string> looted = LootCorpse(s => s.LootChatSalvage = true);
        Check.Eq(LineFor(looted, "Pyreal"), "[RynthAi] Looted [Keep] Pyreal", "a pyreal stack keeps the plain line");

        // Notes and comps by class, any other stack by its stack size; a single unstackable item is described.
        var s = new MagItemInfoSettings { LootChatSalvage = true };
        Check.Eq(LootChatLine.DetailFor(s, "Keep", false, false, AcObjectClass.TradeNote), LootChatLine.Detail.None, "trade note");
        Check.Eq(LootChatLine.DetailFor(s, "Keep", false, false, AcObjectClass.SpellComponent), LootChatLine.Detail.None, "spell component");
        Check.Eq(LootChatLine.DetailFor(s, "Keep", false, true, AcObjectClass.Gem), LootChatLine.Detail.None, "a stack of gems");
        Check.Eq(LootChatLine.DetailFor(s, "KeepUpTo", false, false, AcObjectClass.Gem), LootChatLine.Detail.Full, "one gem (Keep #)");
        Check.Eq(LootChatLine.DetailFor(s, "Recover", false, false, AcObjectClass.Armor), LootChatLine.Detail.None, "own corpse recovery: no rule, plain");
        Check.Eq(LootChatLine.DetailFor(s, "ManaTap", false, false, AcObjectClass.Armor), LootChatLine.Detail.None, "mana tap built-in: plain");
    });

    private static void SettingOffPlain() => Clocked(() =>
    {
        List<string> looted = LootCorpse(s => { s.LootChat = false; s.LootChatSalvage = true; });
        Check.Eq(LineFor(looted, "Blunt Crossbow"), "[RynthAi] Looted [Keep] Blunt Crossbow", "keeper: plain");
        Check.Eq(LineFor(looted, "Helm"), "[RynthAi] Looted [Salvage] Helm", "salvage: plain");
        Check.Eq(LineFor(looted, "Pyreal"), "[RynthAi] Looted [Keep] Pyreal", "pyreals: plain");
    });

    private static void SettingsRoundTrip()
    {
        var fresh = new MagItemInfoSettings();
        Check.True(fresh.LootChat, "Describe looted items in chat is on by default");
        Check.False(fresh.LootChatSalvage, "Include salvage is off by default");
        Check.Eq(fresh.LootChatType, MagItemInfoSettings.SameAsHeader, "loot lines use the Item Info chat type by default");
        Check.Eq(LootChatLine.ChatTypeOf(new MagItemInfoSettings { ChatType = 7 }), 7, "...which is ChatType");

        // Profile: save, load (CopySettings), save again unchanged.
        var saved = new LegacyUiSettings();
        saved.ItemInfoSettings.LootChat = false;
        saved.ItemInfoSettings.LootChatSalvage = true;
        saved.ItemInfoSettings.LootChatType = 24;
        string json = JsonSerializer.Serialize(saved, RynthAiJsonContext.Default.LegacyUiSettings);
        var live = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.Static | Any)!
            .Invoke(null, new object[] { JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!, live });
        Check.False(live.ItemInfoSettings.LootChat, "loot chat off survives");
        Check.True(live.ItemInfoSettings.LootChatSalvage, "include salvage survives");
        Check.Eq(live.ItemInfoSettings.LootChatType, 24, "loot chat type survives");
        Check.Eq(JsonSerializer.Serialize(live, RynthAiJsonContext.Default.LegacyUiSettings), json, "and saves back unchanged");

        // A profile from before the setting: on.
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var infoKey = doc.Select(kv => kv.Key).Single(k => k.Equals("ItemInfoSettings", StringComparison.OrdinalIgnoreCase));
        var info = doc[infoKey]!.AsObject();
        var lootKey = info.Select(kv => kv.Key).SingleOrDefault(k => k.Equals("LootChat", StringComparison.OrdinalIgnoreCase));
        Check.True(lootKey != null, "the field is in the profile JSON");
        if (lootKey != null) info.Remove(lootKey);
        string old = doc.ToJsonString();
        var oldLoaded = JsonSerializer.Deserialize(old, RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(oldLoaded.ItemInfoSettings.LootChat, "a profile without it loads it on");

        // Reset all to defaults covers them; Sanitize clamps a hand-edited chat type.
        saved.ItemInfoSettings.ResetToDefaults();
        Check.True(saved.ItemInfoSettings.LootChat && !saved.ItemInfoSettings.LootChatSalvage
                   && saved.ItemInfoSettings.LootChatType == MagItemInfoSettings.SameAsHeader, "reset restores the loot options");
        saved.ItemInfoSettings.LootChatType = 99;
        saved.ItemInfoSettings.Sanitize();
        Check.Eq(saved.ItemInfoSettings.LootChatType, MagItemInfoSettings.SameAsHeader, "an out-of-range chat type is cleared");

        // The settings map (metas: vtsetsetting / ragetsetting).
        var mapSettings = new LegacyUiSettings();
        var e = ExpressionTests.Engine(settings: mapSettings);
        Check.Eq(e.Evaluate("ragetsetting[ItemInfoLootChat]"), "1", "map reads it");
        Check.Eq(e.Evaluate("rasetsetting[ItemInfoLootChat, 0]"), "1", "map sets it");
        Check.False(mapSettings.ItemInfoSettings.LootChat, "off through the map");
        e.Evaluate("rasetsetting[ItemInfoLootSalvage, 1]");
        Check.True(mapSettings.ItemInfoSettings.LootChatSalvage, "include salvage through the map");

        // /ra iteminfo loot on|off answers, and changes the setting.
        FakeHost.Reset();
        var bot = new Bot(FakeHost.Create(Player));
        void Cmd(params string[] words) => typeof(RynthAiPlugin).GetMethod("HandleItemInfoCommand", Any)!
            .Invoke(bot.P, new object[] { new[] { "ra", "iteminfo" }.Concat(words).ToArray() });
        Cmd("loot", "off");
        Check.False(bot.S.ItemInfoSettings.LootChat, "/ra iteminfo loot off");
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Item info on loot: OFF (salvage OFF)"), "replies: " + string.Join(" | ", FakeHost.Chat));
        Cmd("loot", "on");
        Check.True(bot.S.ItemInfoSettings.LootChat, "/ra iteminfo loot on");
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Item info on loot: ON (salvage OFF)"), "replies on");
        Cmd("loot", "salvage", "on");
        Check.True(bot.S.ItemInfoSettings.LootChatSalvage, "/ra iteminfo loot salvage on");
        Check.True(FakeHost.Chat.Any(c => c == "[RynthAi] Item info on loot, salvage: ON"), "replies salvage");
        FakeHost.Chat.Clear();
        Cmd("loot", "maybe");
        Check.True(bot.S.ItemInfoSettings.LootChat, "a bad word changes nothing");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Usage: /ra iteminfo loot on|off")), "and gets the usage: " + string.Join(" | ", FakeHost.Chat));
    }
}
