using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// "Add to loot profile" from a clicked item (2026-10-04): /ra loot add on the
// selected item, and the Loot Editor bridge's item_preview / item_add (what the
// engine's popup sends), against temp copies of a .utl and a native .json
// profile. Checks the chat line, the file, the reload into the evaluator that
// loots, and that the clicked item is then decided by the new rule.
internal static class LootAddItemTests
{
    private const uint Player = 0x50000E01;
    private const uint Opal = 0x80271101, Taper = 0x80271102, Coin = 0x80271103;
    private const uint Ring = 0x80271104, Stone = 0x80271105, Sceptre = 0x80271106;
    private const uint ItemTypeJewelry = 0x8, ItemTypeCaster = 0x8000, ItemTypeManaStone = 0x80000, CurrentMana = 107;
    private const uint ItemTypeGem = 0x800, ItemTypeComponent = 0x1000;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("loot add: /ra loot add puts 'Keep Black Opal' before the rule that sold it, saves, reloads (.utl)", UtlAddBeforeBroaderRule);
        r.Add("loot add: native .json profile, same command, the native evaluator keeps it", NativeAdd);
        r.Add("loot add: nothing takes the item -> appended at the end", AppendWhenNothingMatches);
        r.Add("loot add: options (sell name, Keep # N, like) and preview leaves the file alone", CommandOptions);
        r.Add("loot add: refusals (nothing selected, the same rule twice, no profile)", Refusals);
        r.Add("loot add: the popup's item_preview / item_add / item_close through the editor bridge", BridgePopupFlow);
        r.Add("loot add: unsaved edits in the Loot Editor block the add (nothing saved behind them)", BridgeRefusesUnderEdits);
        r.Add("loot add: the popup says when RynthAi's built-in looting takes the item (Mana Tap, mana stones)", BuiltInReasons);
        r.Add("loot: Mana Tap takes a high-mana wand like any other item (they drain)", ManaTapTakesWands);
    }

    // ── World and plugin ──────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;

        /// <summary><paramref name="world"/> adds objects (after the standard ones, before the host); list their ids in <paramref name="extra"/>.</summary>
        public Bot(Action? world = null, params uint[] extra)
        {
            FakeHost.Reset();
            FakeHost.Names[Player] = "Tester";
            FakeHost.Names[Opal] = "Black Opal";
            FakeHost.ItemTypes[Opal] = ItemTypeGem;
            FakeHost.Ints[(Opal, 131)] = 16;     // MaterialType Black Opal
            FakeHost.Ints[(Opal, 105)] = 6;      // workmanship
            FakeHost.Names[Taper] = "Prismatic Taper";
            FakeHost.ItemTypes[Taper] = ItemTypeComponent;
            FakeHost.Ints[(Taper, 11)] = 100;    // MaxStackSize
            FakeHost.Ints[(Taper, 12)] = 37;     // StackCount
            FakeHost.Names[Coin] = "Pyreal";
            world?.Invoke();
            var host = FakeHost.Create(Player);
            Host = host;
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = FakeHost.MakeCache(host, Player, new[] { Player, Opal, Taper, Coin }.Concat(extra));
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_charSkills", new CharacterSkills(host));
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T? Get<T>(string f) => (T?)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        public void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int eat = 0;
            object[] args = { parts[1].ToLowerInvariant(), parts, eat };
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(P, args);
        }
        public string ChatText => string.Join("\n", FakeHost.Chat);
        public WorldObject Item(uint id) => Cache[unchecked((int)id)]!;
    }

    private static string Utl(params string[] lines) => string.Join("\n", lines) + "\n";

    // Rule 1 sells every gem, rule 2 keeps pyreals.
    private static readonly string SellGems = Utl(
        "UTL", "1", "2",
        "Sell all gems", "", "0;3;7", "0", "11",
        "Keep pyreals", "", "0;1;1", "0", "^Pyreal$", "1");

    private static string Temp(string name, string text)
    {
        string path = Path.Combine(Program.TempRoot, "lootadd-" + Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.WriteAllText(path, text);
        return path;
    }

    // ── .utl ──────────────────────────────────────────────────────────────────

    private static void UtlAddBeforeBroaderRule()
    {
        var bot = new Bot();
        string path = Temp("sell-gems.utl", SellGems);
        bot.S.CurrentLootPath = path;
        object?[] load = { string.Empty, null, null };
        Check.True((bool)typeof(RynthAiPlugin).GetMethod("TryLoadLootProfile", Any)!.Invoke(bot.P, load)!, "the profile in use loads");
        Check.Eq(bot.Get<VTankLootProfile>("_loadedLootProfile")?.Rules.Count, 2, "profile in use: 2 rules");
        var before = VTankLootEvaluator.FirstMatch(bot.Get<VTankLootProfile>("_loadedLootProfile")!, bot.Item(Opal), null, out _);
        Check.Eq(before?.Action, VTankLootAction.Sell, "before: the gem rule sells it");

        FakeHost.SelectedItem = Opal;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add");

        Check.True(FakeHost.Chat.Contains($"[RynthAi] Added loot rule 'Keep Black Opal' to {Path.GetFileName(path)}."),
            "chat: [RynthAi] Added loot rule 'Keep Black Opal' to <profile>.\n" + bot.ChatText);
        Check.True(bot.ChatText.Contains("just before rule 1 'Sell all gems'"), "chat says where and why");
        VTankLootProfile onDisk = VTankLootParser.Load(path);
        Check.Eq(onDisk.Rules.Count, 3, "3 rules on disk");
        Check.Eq(onDisk.Rules[0].Name, "Keep Black Opal", "inserted first, before the broader gem rule");
        Check.Eq(string.Join(" ; ", onDisk.Rules[0].Conditions.Select(c => c.NodeType + ":" + string.Join("|", c.DataLines))),
            "7:11 ; 1:^Black Opal$|1", "exact name + class");
        Check.Eq(onDisk.Rules[1].Name, "Sell all gems", "the old rules follow, unchanged");
        Check.Eq(File.ReadAllText(path + ".bak"), SellGems, ".bak is the original");

        VTankLootProfile? live = bot.Get<VTankLootProfile>("_loadedLootProfile");
        Check.Eq(live?.Rules.Count, 3, "reloaded at once: the bot loots with 3 rules");
        var after = VTankLootEvaluator.FirstMatch(live!, bot.Item(Opal), null, out int idx);
        Check.Eq(after?.Name, "Keep Black Opal", "the clicked gem is now decided by the new rule");
        Check.Eq(after?.Action, VTankLootAction.Keep, "Keep");
        var coin = VTankLootEvaluator.FirstMatch(live!, bot.Item(Coin), null, out _);
        Check.Eq(coin?.Name, "Keep pyreals", "other items: same rule as before");

        FakeHost.Chat.Clear();
        bot.Ra("/ra lootcheck");
        Check.True(bot.ChatText.Contains("Black Opal: [Keep] Keep Black Opal"), "/ra lootcheck agrees\n" + bot.ChatText);
    }

    // ── .json ─────────────────────────────────────────────────────────────────

    private static void NativeAdd()
    {
        var bot = new Bot();
        var p = new LootProfile { Name = "native" };
        var sell = new LootRule { Name = "Sell gems", Action = LootAction.Sell };
        sell.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        p.Rules.Add(sell);
        string path = Path.Combine(Program.TempRoot, "lootadd-native-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        p.Save(path);
        string original = File.ReadAllText(path);
        bot.S.CurrentLootPath = path;
        Check.True((bool)typeof(RynthAiPlugin).GetMethod("TryLoadNativeLootProfile", Any)!.Invoke(bot.P, new object?[] { null, null })!, "native profile loads");

        FakeHost.SelectedItem = Opal;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add");
        Check.True(FakeHost.Chat.Contains($"[RynthAi] Added loot rule 'Keep Black Opal' to {Path.GetFileName(path)}."), "chat line\n" + bot.ChatText);
        LootProfile disk = LootProfile.Load(path);
        Check.Eq(disk.Rules.Count, 2, "2 rules on disk");
        Check.Eq(disk.Rules[0].Name, "Keep Black Opal", "before the gem rule");
        Check.Eq(File.ReadAllText(path + ".bak"), original, ".bak is the original");
        LootProfile? live = bot.Get<LootProfile>("_nativeLootProfile");
        Check.Eq(live?.Rules.Count, 2, "reloaded at once");
        var (action, rule) = LootEvaluator.Classify(live!, bot.Item(Opal), null);
        Check.Eq(rule?.Name, "Keep Black Opal", "the native evaluator picks the new rule");
        Check.Eq(action, LootAction.Keep, "Keep");
    }

    private static void AppendWhenNothingMatches()
    {
        var bot = new Bot();
        string path = Temp("coins-only.utl", Utl("UTL", "1", "1", "Keep pyreals", "", "0;1;1", "0", "^Pyreal$", "1"));
        bot.S.CurrentLootPath = path;
        FakeHost.SelectedItem = Opal;
        bot.Ra("/ra loot add");
        VTankLootProfile disk = VTankLootParser.Load(path);
        Check.Eq(disk.Rules.Count, 2, "2 rules");
        Check.Eq(disk.Rules[1].Name, "Keep Black Opal", "appended (nothing took the gem)");
        Check.True(bot.ChatText.Contains("at the end"), "chat says why\n" + bot.ChatText);
    }

    private static void CommandOptions()
    {
        var bot = new Bot();
        string path = Temp("options.utl", SellGems);
        bot.S.CurrentLootPath = path;

        FakeHost.SelectedItem = Taper;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add preview");
        Check.Eq(File.ReadAllText(path), SellGems, "preview: file untouched");
        Check.False(File.Exists(path + ".bak"), "preview: no save at all");
        Check.True(bot.ChatText.Contains("Rule: Keep 100 Prismatic Taper") && bot.ChatText.Contains("Action: Keep # 100"),
            "preview: a stack defaults to Keep # one full stack\n" + bot.ChatText);

        bot.Ra("/ra loot add 25");
        VTankLootProfile disk = VTankLootParser.Load(path);
        VTankLootRule kept = disk.Rules.Last();
        Check.Eq(kept.Name, "Keep 25 Prismatic Taper", "Keep # 25 by number");
        Check.Eq(kept.Action, VTankLootAction.KeepUpTo, "Keep #");
        Check.Eq(kept.KeepCount, (int?)25, "count 25");

        FakeHost.SelectedItem = Opal;
        bot.Ra("/ra loot add sell name");
        disk = VTankLootParser.Load(path);
        Check.Eq(disk.Rules[0].Name, "Sell Black Opal", "sell, name only (before the gem rule)");
        Check.Eq(disk.Rules[0].Conditions.Count, 1, "one condition: the name");

        bot.Ra("/ra loot add like");
        disk = VTankLootParser.Load(path);
        // The gem now hits "Sell Black Opal" first, so the like-rule goes before that.
        Check.Eq(disk.Rules[0].Name, "Keep like Black Opal", "like this: before the rule that takes it now");
        Check.Eq(string.Join(" ; ", disk.Rules[0].Conditions.Select(c => c.NodeType + ":" + string.Join("|", c.DataLines))),
            "7:11 ; 12:16|131 ; 3:6|105", "class + material + workmanship >=");
        Check.Eq(disk.Rules.Count, 5, "5 rules");

        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add bogus");
        Check.True(bot.ChatText.Contains("'bogus'?"), "an unknown word is refused");
        Check.Eq(VTankLootParser.Load(path).Rules.Count, 5, "and adds nothing");
    }

    private static void Refusals()
    {
        var bot = new Bot();
        string path = Temp("refuse.utl", SellGems);
        bot.S.CurrentLootPath = path;
        FakeHost.SelectedItem = 0;
        bot.Ra("/ra loot add");
        Check.True(bot.ChatText.Contains("Loot rule not added: No item selected"), "nothing selected\n" + bot.ChatText);
        Check.Eq(File.ReadAllText(path), SellGems, "untouched");

        FakeHost.SelectedItem = Opal;
        bot.Ra("/ra loot add");
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add");
        Check.True(bot.ChatText.Contains("Already there: rule 1 'Keep Black Opal'"), "the same rule twice is refused\n" + bot.ChatText);
        Check.Eq(VTankLootParser.Load(path).Rules.Count, 3, "one copy only");

        FakeHost.SelectedItem = 0x8027FFFF;   // not in the cache
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add");
        Check.True(bot.ChatText.Contains("doesn't know item"), "unknown item\n" + bot.ChatText);

        bot.S.CurrentLootPath = string.Empty;
        FakeHost.SelectedItem = Opal;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add");
        Check.True(bot.ChatText.Contains("No loot profile selected"), "no profile\n" + bot.ChatText);

        bot.S.CurrentLootPath = path.Replace(".utl", "-missing.utl");
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add");
        Check.True(bot.ChatText.Contains("not found"), "missing profile\n" + bot.ChatText);
        Check.False(File.Exists(bot.S.CurrentLootPath), "and not created");
    }

    // ── The engine's popup, through the bridge ───────────────────────────────

    private static LootEditState State(LootEditorBridge b) =>
        JsonSerializer.Deserialize(b.StateJson(), LootEditJsonContext.Default.LootEditState)!;

    private static void Send(LootEditorBridge b, LootEditCommand c) =>
        b.Command(JsonSerializer.Serialize(c, LootEditJsonContext.Default.LootEditCommand));

    private static void BridgePopupFlow()
    {
        var bot = new Bot();
        string path = Temp("popup.utl", SellGems);
        bot.S.CurrentLootPath = path;
        LootEditorBridge b = bot.P.LootEditor;
        Check.Eq(State(b).Rules.Count, 2, "editor shows the profile in use");
        int rev0 = b.Revision();

        Send(b, new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { ItemId = Taper, Seq = 7 } });
        Check.True(b.Revision() != rev0, "a preview moves the revision (the engine refetches)");
        LootEditState s = State(b);
        Check.Eq(s.Revision, (long)b.Revision(), "the state's revision is the one Revision() reports");
        LootEditItemDraft? d = s.ItemDraft;
        Check.True(d != null && d.Ok, "draft: " + d?.Error);
        Check.Eq(d?.Seq, 7, "echoes the popup's seq");
        Check.Eq(d?.ItemName, "Prismatic Taper", "item");
        Check.Eq(d?.ClassName, "SpellComponent", "class");
        Check.True(d?.Stackable == true, "stackable");
        Check.Eq(d?.Action, (int)VTankLootAction.KeepUpTo, "default Keep #");
        Check.Eq(d?.KeepCount, 100, "one full stack");
        Check.Eq(d?.RuleName, "Keep 100 Prismatic Taper", "name");
        Check.Eq(d?.InsertAt, 2, "nothing takes it: the end");
        Check.Eq(d?.Format, "utl", "format");
        Check.True(d?.TargetInUse == true, "target is the profile in use");
        Check.True(d?.Preview.Any(l => l.Contains("Class is SpellComponent")) == true, "preview lines");
        Check.Eq(File.ReadAllText(path), SellGems, "preview saves nothing");

        // The popup changes its mind: Keep 10, own name. Then Add.
        Send(b, new LootEditCommand { Op = "item_add", Item = new LootEditItemRequest
        {
            ItemId = Taper, Seq = 8, Action = (int)VTankLootAction.KeepUpTo, KeepCount = 10, RuleName = "Tapers", Match = (int)LootItemMatch.Name,
        } });
        s = State(b);
        Check.True(s.ItemDraft?.Added == true, "added: " + s.ItemDraft?.Error);
        Check.Eq(s.ItemDraft?.Seq, 8, "seq");
        Check.True(FakeHost.Chat.Contains($"[RynthAi] Added loot rule 'Tapers' to {Path.GetFileName(path)}."), "chat line\n" + bot.ChatText);
        Check.Eq(s.Rules.Count, 3, "the editor's list has it (same session)");
        Check.Eq(s.Focus, 2, "and focuses it");
        Check.Eq(s.Rules[2].Name, "Tapers", "named as asked");
        Check.False(s.Dirty, "saved");
        VTankLootRule r = VTankLootParser.Load(path).Rules[2];
        Check.True(r.Action == VTankLootAction.KeepUpTo && r.KeepCount == 10 && r.Conditions.Count == 1, "Keep # 10 by name only, on disk");
        Check.Eq(bot.Get<VTankLootProfile>("_loadedLootProfile")?.Rules.Count, 3, "RynthAi reloaded it");

        Send(b, new LootEditCommand { Op = "item_close" });
        Check.True(State(b).ItemDraft == null, "closed: no draft in the state");

        // ItemId 0 = the selected item.
        FakeHost.SelectedItem = Opal;
        Send(b, new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { Seq = 9, ToOpenProfile = true } });
        Check.Eq(State(b).ItemDraft?.ItemId, Opal, "0 = the selected item, resolved once");
        Check.Eq(State(b).ItemDraft?.InsertAt, 0, "before the gem rule");
    }

    private static void BridgeRefusesUnderEdits()
    {
        var bot = new Bot();
        string path = Temp("edits.utl", SellGems);
        bot.S.CurrentLootPath = path;
        LootEditorBridge b = bot.P.LootEditor;
        _ = State(b);
        Send(b, new LootEditCommand { Op = "rename", Index = 1, Value = "half-done", Expect = "Keep pyreals" });
        Check.True(State(b).Dirty, "the editor has an unsaved edit");
        Send(b, new LootEditCommand { Op = "item_add", Item = new LootEditItemRequest { ItemId = Opal, Seq = 1 } });
        LootEditItemDraft? d = State(b).ItemDraft;
        Check.True(d != null && !d.Added && d.Error.Contains("unsaved changes"), "refused: " + d?.Error);
        Check.True(d != null && d.Preview.Count > 0, "the preview still shows");
        Check.Eq(File.ReadAllText(path), SellGems, "nothing saved");
        Check.Eq(State(b).Rules[1].Name, "half-done", "the edit is still there, unsaved");
    }

    // ── Built-in keeps (2026-10-05) ───────────────────────────────────────────

    // A ring with 3,001 mana, an empty mana stone in the pack, tapping on at 2,500.
    private static Bot TapWorld()
    {
        var bot = new Bot(() =>
        {
            FakeHost.Names[Ring] = "Gold Ring";
            FakeHost.ItemTypes[Ring] = ItemTypeJewelry;
            FakeHost.Ints[(Ring, CurrentMana)] = 3001;
            FakeHost.Names[Stone] = "Mana Stone";
            FakeHost.ItemTypes[Stone] = ItemTypeManaStone;
            FakeHost.Ints[(Stone, CurrentMana)] = 0;
            FakeHost.Names[Sceptre] = "Piercing Sceptre";
            FakeHost.ItemTypes[Sceptre] = ItemTypeCaster;
            FakeHost.Ints[(Sceptre, CurrentMana)] = 3001;
        }, Ring, Stone, Sceptre);
        bot.S.EnableManaTapping = true;
        bot.S.ManaTapMinMana = 2500;
        bot.Set("_manaStoneManager", new ManaStoneManager(bot.Host, bot.S, bot.Cache));
        return bot;
    }

    /// <summary>The looter's own call (what [LootEval] logs): taken?, action tag, rule label.</summary>
    private static (bool Taken, string Action, string Rule) LootEval(Bot bot, uint id)
    {
        MethodInfo m = typeof(RynthAiPlugin).GetMethod("ClassifyItemAgainstProfile", Any)!;
        object?[] args = { bot.Item(id), bot.S, null, null, null };
        bool taken = (bool)m.Invoke(bot.P, args)!;
        return (taken, (string)args[2]!, (string)args[4]!);
    }

    private static void BuiltInReasons()
    {
        // Tom, 2026-10-05: "when I click on the wand it says no rule matches" - Mana Tap had
        // looted it. The popup now says which built-in keep takes an item and where to change it.
        var bot = TapWorld();
        string path = Temp("tap.utl", SellGems);
        bot.S.CurrentLootPath = path;
        LootEditorBridge b = bot.P.LootEditor;
        FakeHost.Chat.Clear();

        Send(b, new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { ItemId = Ring, Seq = 1 } });
        LootEditItemDraft? d = State(b).ItemDraft;
        Check.True(d != null && d.Ok, "draft: " + d?.Error);
        const string tap = "No rule in the loot profile in use takes it, but RynthAi keeps it anyway: "
            + "Mana Tap (3,001 mana >= 2,500), looted to drain into an empty mana stone - change in the Items panel, Mana Stone Tapping.";
        Check.Eq(d?.BuiltInReason, tap, "the reason, with the numbers and where to change it");
        Check.True(d != null && d.OrderNote.Contains("at the end") && d.OrderNote.EndsWith(tap, StringComparison.Ordinal),
            "OrderNote (what every engine's popup shows) carries it: " + d?.OrderNote);
        Check.True(d != null && d.OrderNote.All(c => c < 128), "ASCII only (the popup's font)");

        // The same function the looter runs: [LootEval] tags it [ManaTap].
        Check.Eq(bot.Get<System.Collections.Generic.HashSet<int>>("_pendingManaTapIds")?.Count, 0, "the preview recorded nothing");
        Check.False(bot.ChatText.Contains("Mana tap candidate"), "and said nothing in chat\n" + bot.ChatText);
        var eval = LootEval(bot, Ring);
        Check.True(eval.Taken && eval.Action == "ManaTap", "the looter takes it as [ManaTap]: " + eval.Action);
        Check.True(eval.Rule.StartsWith("ManaTap (mana=3001", StringComparison.Ordinal), "rule label as in the log: " + eval.Rule);

        // A profile rule decides: no built-in reason.
        Send(b, new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { ItemId = Opal, Seq = 2 } });
        d = State(b).ItemDraft;
        Check.Eq(d?.BuiltInReason, "", "the gem: rule 1 sells it, nothing built in");
        Check.True(d != null && d.OrderNote.Contains("just before rule 1 'Sell all gems'"), "order note as before: " + d?.OrderNote);

        // Mana stones are checked ahead of the profile.
        Send(b, new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { ItemId = Stone, Seq = 3 } });
        d = State(b).ItemDraft;
        Check.True(d != null && d.BuiltInReason.StartsWith("RynthAi takes it before any loot profile rule: a mana stone,", StringComparison.Ordinal),
            "mana stone: " + d?.BuiltInReason);
        Check.True(d != null && d.BuiltInReason.Contains("of 5 kept - change in the Items panel"), "with the keep count");

        // Under the threshold: not taken, nothing to explain.
        bot.S.ManaTapMinMana = 5000;
        Send(b, new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { ItemId = Ring, Seq = 4 } });
        Check.Eq(State(b).ItemDraft?.BuiltInReason, "", "3,001 mana under a 5,000 threshold: no reason");
        bot.S.ManaTapMinMana = 2500;

        // Already in the pack after a Mana Tap pickup, quota full now: still says why it was taken.
        bot.Get<System.Collections.Generic.HashSet<int>>("_pendingManaTapIds")!.Clear();   // (the LootEval call above approved it)
        bot.Get<ManaStoneManager>("_manaStoneManager")!.MarkLootedForTap(unchecked((int)Ring));
        FakeHost.Ints[(Stone, CurrentMana)] = 500;   // the only stone is charged: no free slot
        Send(b, new LootEditCommand { Op = "item_preview", Item = new LootEditItemRequest { ItemId = Ring, Seq = 5 } });
        Check.Eq(State(b).ItemDraft?.BuiltInReason,
            "No rule in the loot profile in use takes it, but RynthAi keeps it anyway: it was looted for Mana Tap, to drain into an empty mana stone - change in the Items panel, Mana Stone Tapping.",
            "looted for tapping earlier");

        // /ra loot add preview shows it in chat too.
        FakeHost.Ints[(Stone, CurrentMana)] = 0;
        FakeHost.SelectedItem = Ring;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot add preview");
        Check.True(bot.ChatText.Contains("RynthAi keeps it anyway: it was looted for Mana Tap"), "/ra loot add preview says it\n" + bot.ChatText);
        Check.Eq(File.ReadAllText(path), SellGems, "preview saves nothing");
    }

    private static void ManaTapTakesWands()
    {
        var bot = TapWorld();
        bot.S.CurrentLootPath = Temp("wand.utl", SellGems);
        var wand = LootEval(bot, Sceptre);
        Check.True(wand.Taken && wand.Action == "ManaTap", $"a 3,001-mana sceptre is tapped: [{wand.Action}] {wand.Rule}");
        var ring = LootEval(bot, Ring);
        Check.False(ring.Taken, "the one empty stone is now spoken for by the wand, so the ring waits: " + ring.Action);
    }
}
