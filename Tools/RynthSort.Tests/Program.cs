using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.RynthSort.Views;
using RynthCore.Plugin.Shared;

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

namespace RynthSort.Tests
{
    internal static partial class Program
    {
        private static int _pass, _fail;
        private static string _temp = "";

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Write")]
        private static extern int Write(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_submit")]
        private static extern ref byte[] SubmitBuffer(UiHost h);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Apply")]
        private static extern void Apply(UiHost h, ReadOnlySpan<byte> data);

        private static int Main()
        {
            _temp = Path.Combine(Path.GetTempPath(), "RynthSort.Tests." + Environment.ProcessId);
            Directory.CreateDirectory(_temp);
            try
            {
                Run("rules: class and name matching, first match wins", RuleMatching);
                Run("classify: item classes as RynthLua sees them", Classify);
                Run("classify: scarabs, tapers and other spell components", ClassifyComponents);
                Run("rules: a rule's pack by id, else its number", ResolveById);
                Run("pack fix: imported Lua numbers (foci counted)", PackFixImported);
                Run("pack fix: Drakkon's rules (edited after the import)", PackFixDrakkon);
                Run("pack fix: in the loop (held until the packs settle)", PackFixLoop);
                Run("window: pack labels and warnings", PackLabels);
                Run("plan: settle time, force", PlanSettle);
                Run("plan: pack choice (full pack, stacks, capacity)", PlanPackChoice);
                Run("plan: worn, packs, foci and blocked items never move", PlanNeverMoves);
                Run("plan: missing pack", PlanMissingPack);
                Run("plan: tidy and clean", PlanTidyClean);
                Run("plan: unpack", PlanUnpack);
                Run("gate: stay out of the way", Gates);
                Run("gate: RynthAi activity", GatesRynthAi);
                Run("gate: quiet time after a pause", GateQuiet);
                Run("gate: looting watch", LootWatching);
                Run("gate: older RynthAi status JSON", StatusJsonFallback);
                Run("moves: answers and back-off", MoveAnswers);
                Run("loop: nothing is sent while paused", LoopPaused);
                Run("loop: one move at a time, then the gap", LoopOneAtATime);
                Run("loop: refusals block the item and back off", LoopRefusals);
                Run("loop: too busy and no answer", LoopBusyAndTimeout);
                Run("loop: jobs (sort now, tidy, clean, unpack)", LoopJobs);
                Run("store: save and load", StoreRoundTrip);
                Run("store: Lua script import", StoreLuaImport);
                Run("store: odd files", StoreOddFiles);
                Run("store: pack ids, the fix flag and the backup", StorePackIds);
                Run("lua autostart warning", LuaAutostart);
                Run("window: display lists parse in the engine", WindowParses);
                Run("window: a bar button of its own", WindowBarButton);
                Run("window: rule editing through events", WindowEvents);
                Run("rules: several classes and names in one rule", MultiRules);
                Run("store: old rules migrate to version 3", RuleMigration);
                Run("order: inside a pack (A-Z, fewest moves, every order of up to 7)", InPackOrder);
                Run("order: slot orders and the pack column", SlotOrders);
                Run("pack column: which containers are packs (foci, wielded, unloaded)", PackColumnKinds);
                Run("pack column: order with foci, wielded and unloaded packs, and why it isn't known", PackColumnOrder);
                Run("pack column: what a pack header says (place, count, why not known)", PackColumnLabels);
                Run("pack column: icons and Select in game in the window", PackColumnWindow);
                Run("plan: stacks and order inside packs", PlanInPack);
                Run("moves: answers for moves within a pack", MoveAnswersInPlace);
                Run("replies: every /rsort word answers", Replies);
                Run("plan: a fixed inventory runs out of moves", Converges);
                Run("loop: auto-sort tidies every pack, then stays still", ConvergesInTheLoop);
                Run("remote: another plugin's \"rsort\" command reaches /rsort, anything else is not ours", RemoteCommand);
            }
            finally
            {
                try { Directory.Delete(_temp, true); } catch { }
            }
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            if (_pass == 0) { Console.WriteLine("ABORT: zero assertions ran."); return 1; }
            return _fail == 0 ? 0 : 1;
        }

        // RynthAi sends ub-IT's /ub packsort as SendPluginCommand("RynthSort", "rsort", "now").
        private static void RemoteCommand()
        {
            Eq(RynthSortPlugin.RemoteSortArgument("rsort", "now"), "now", "rsort now");
            Eq(RynthSortPlugin.RemoteSortArgument("RSort", "status"), "status", "case-insensitive action");
            Eq(RynthSortPlugin.RemoteSortArgument("rsort", null), "", "no value is an empty /rsort");
            Check(RynthSortPlugin.RemoteSortArgument("sort", "now") == null, "another action is not ours");
            Check(RynthSortPlugin.RemoteSortArgument(null, "now") == null, "no action is not ours");
            Check(RynthSortPlugin.RemoteSortArgument("", "now") == null, "empty action is not ours");
        }

        private static void Run(string name, Action test)
        {
            int before = _fail;
            try { test(); }
            catch (Exception ex) { _fail++; Console.WriteLine($"FAIL: {name} threw {ex}"); }
            Console.WriteLine($"  {(before == _fail ? "ok  " : "FAIL")} {name}");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Console.WriteLine("FAIL: " + what); }
        }

        private static void Eq<T>(T actual, T expected, string what) =>
            Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

        // ── Sample data ─────────────────────────────────────────────────────

        private const uint Me = 0x50000001;

        private static SortInventory Inv(int packs, int cap = 24, int mainCap = 102)
        {
            var inv = new SortInventory();
            inv.Main.Id = Me;
            inv.Main.Name = "Main pack";
            inv.Main.Capacity = mainCap;
            for (int i = 1; i <= packs; i++)
                inv.Packs.Add(new SortPack { Id = 0x40000000u + (uint)i, Name = "Pack " + i, Capacity = cap });
            return inv;
        }

        private static SortItem It(uint id, string name, int cls, uint wcid = 0, int stack = 1, int max = 1) =>
            new() { Id = id, Name = name, ObjectClass = cls, Wcid = wcid, Stack = stack, MaxStack = max };

        private static void Fill(SortPack p, int count, uint firstId)
        {
            for (int i = 0; i < count; i++) p.Items.Add(It(firstId + (uint)i, "Junk " + i, ObjectClasses.Misc));
        }

        private static readonly List<SortRule> Defaults = SortRules.Defaults();

        // ── Rules ───────────────────────────────────────────────────────────

        private static void RuleMatching()
        {
            SortItem comp = It(1, "Prismatic Taper", ObjectClasses.SpellComponent);
            SortItem salvage = It(2, "Salvaged Iron (100)", ObjectClasses.Salvage);
            SortItem gem = It(3, "Black Opal", ObjectClasses.Gem);
            SortItem misc = It(4, "Ashcoal", ObjectClasses.Misc);

            Check(SortRules.Matches(SortRule.ForClass(ObjectClasses.SpellComponent, 1), comp), "class rule matches its class");
            Check(!SortRules.Matches(SortRule.ForClass(ObjectClasses.SpellComponent, 1), salvage), "class rule ignores other classes");
            Check(SortRules.Matches(SortRule.ForName("taper", 4), comp), "name rule: part of the name, any case");
            Check(!SortRules.Matches(SortRule.ForName("", 4), comp), "an empty name rule matches nothing");
            var off = SortRule.ForClass(ObjectClasses.Gem, 3);
            off.On = false;
            Check(!SortRules.Matches(off, gem), "a rule that is off matches nothing");

            var rules = new List<SortRule> { SortRule.ForName("taper", 5), SortRule.ForClass(ObjectClasses.SpellComponent, 1) };
            Eq(SortRules.RuleFor(rules, comp)?.Pack, 5, "a name match beats a class match");
            (rules[0], rules[1]) = (rules[1], rules[0]);
            Eq(SortRules.RuleFor(rules, comp)?.Pack, 5, "whatever the order");
            var twoClass = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 2), SortRule.ForClass(ObjectClasses.Gem, 4) };
            Eq(SortRules.RuleFor(twoClass, gem)?.Pack, 2, "two class matches: the rule saved first");
            Check(SortRules.RuleFor(Defaults, misc) == null, "no rule wants misc");
            Eq(SortRules.RuleFor(Defaults, salvage)?.Pack, 2, "default: salvage to pack 2");
            Eq(SortRules.RuleFor(Defaults, gem)?.Pack, 3, "default: gems to pack 3");

            Eq(ObjectClasses.Label(ObjectClasses.ManaStone), "Mana Stone", "class label");
            Eq(ObjectClasses.ChoiceLabels[ObjectClasses.ChoiceIndex(ObjectClasses.WandStaffOrb)], "Wand Staff Orb", "choice label");
            Eq(SortRules.Describe(SortRule.ForName("Pyreal", 2)), "name has \"Pyreal\"", "describe name rule");
            // The Lua script's numbers mean the same classes (saved rules stay right).
            Eq(ObjectClasses.Names[12], "SpellComponent", "12 = SpellComponent");
            Eq(ObjectClasses.Names[39], "Salvage", "39 = Salvage");
            Eq(ObjectClasses.Names[29], "HealingKit", "29 = HealingKit");
        }

        private static void Classify()
        {
            Eq(InventoryReader.ClassifyFrom(0x40000000, 0, "Salvaged Iron", out bool c1), ObjectClasses.Salvage, "tinkering material = salvage");
            Check(c1, "certain with a type");
            Eq(InventoryReader.ClassifyFrom(0x00001000, 0, "Prismatic Taper", out _), ObjectClasses.SpellComponent, "spell component");
            Eq(InventoryReader.ClassifyFrom(0x00080000 | 0x00000080, 0, "Mana Stone", out _), ObjectClasses.ManaStone, "mana stone before misc");
            Eq(InventoryReader.ClassifyFrom(0x00000080, 0x10000, "Healing Kit", out _), ObjectClasses.HealingKit, "healer bit = healing kit");
            Eq(InventoryReader.ClassifyFrom(0x00000080, 0x20000, "Lockpick", out _), ObjectClasses.Lockpick, "lockpick bit");
            Eq(InventoryReader.ClassifyFrom(0x00002000, 0, "Scroll of Strength Self VII", out _), ObjectClasses.Scroll, "writable named Scroll = scroll");
            Eq(InventoryReader.ClassifyFrom(0x00000200, 0, "Foci of Enchantment", out _), ObjectClasses.Foci, "foci by name");
            Eq(InventoryReader.ClassifyFrom(0x00000080, 0x8000, "Apple", out _), ObjectClasses.Food, "misc with the food bit = food");
            Eq(InventoryReader.ClassifyFrom(0, 0, "", out bool c2), ObjectClasses.Unknown, "no data");
            Check(!c2, "not certain without data (read again later)");
        }

        // ── Planner ─────────────────────────────────────────────────────────

        private static void PlanSettle()
        {
            var p = new SortPlanner();
            SortInventory inv = Inv(3);
            inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            List<SortMove> m = p.Plan(inv, Defaults, SortScope.Main, false, 1000, 5000);
            Eq(m.Count, 0, "a new item waits for the settle time");
            Eq(p.View.Waiting, 1, "it is counted as waiting");
            m = p.Plan(inv, Defaults, SortScope.Main, false, 5999, 5000);
            Eq(m.Count, 0, "still settling just before");
            m = p.Plan(inv, Defaults, SortScope.Main, false, 6000, 5000);
            Eq(m.Count, 1, "moves once settled");
            Eq(m[0].DestIndex, 1, "to pack 1");
            Eq(m[0].Dest.Id, inv.Packs[0].Id, "pack 1's id");

            var q = new SortPlanner();
            m = q.Plan(inv, Defaults, SortScope.Main, true, 1000, 5000);
            Eq(m.Count, 1, "Sort now (force) skips the settle time");

            // An item that left the main pack and came back starts its settle time again.
            var r = new SortPlanner();
            r.Plan(inv, Defaults, SortScope.Main, false, 0, 5000);
            SortItem it = inv.Main.Items[0];
            inv.Main.Items.Clear();
            r.Plan(inv, Defaults, SortScope.Main, false, 3000, 5000);
            inv.Main.Items.Add(it);
            Eq(r.Plan(inv, Defaults, SortScope.Main, false, 6000, 5000).Count, 0, "re-seen item settles again");
            Eq(r.Plan(inv, Defaults, SortScope.Main, false, 11000, 5000).Count, 1, "then moves");

            // Settle 0: moves at once.
            Eq(new SortPlanner().Plan(inv, Defaults, SortScope.Main, false, 0, 0).Count, 1, "settle 0 moves at once");
        }

        private static void PlanPackChoice()
        {
            // A full pack refuses an item that can't stack there.
            SortInventory inv = Inv(3);
            Fill(inv.Packs[1], 24, 1000);
            inv.Main.Items.Add(It(10, "Salvaged Iron", ObjectClasses.Salvage, wcid: 20981, stack: 1, max: 1));
            var p = new SortPlanner();
            List<SortMove> m = p.Plan(inv, Defaults, SortScope.Main, true, 0, 5000);
            Eq(m.Count, 0, "full pack, no stack: refused");
            Check(p.View.Full.Contains(2), "pack 2 reported full");
            Eq(p.View.Waiting, 1, "waiting");

            // A full pack takes a stackable item when a stack of the same kind has room there.
            inv = Inv(3);
            Fill(inv.Packs[0], 23, 1000);
            inv.Packs[0].Items.Add(It(50, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 8897, stack: 40, max: 100));
            inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 8897, stack: 12, max: 100));
            p = new SortPlanner();
            m = p.Plan(inv, Defaults, SortScope.Main, true, 0, 5000);
            Eq(m.Count, 1, "full pack, joins a stack: moves");
            Eq(m.Count > 0 ? m[0].MergeInto : 0u, 50u, "merges into the stack there");
            Check(p.View.Full.Count == 0, "not reported full");

            // ... but not when that stack is itself full.
            inv.Packs[0].Items[23].Stack = 100;
            p = new SortPlanner();
            m = p.Plan(inv, Defaults, SortScope.Main, true, 0, 5000);
            Eq(m.Count, 0, "full pack whose stack is full: refused");
            Check(p.View.Full.Contains(1), "pack 1 full");

            // A different kind with the same name doesn't count when both have a class id (wcid).
            inv.Packs[0].Items[23].Stack = 40;
            inv.Packs[0].Items[23].Wcid = 9999;
            Eq(new SortPlanner().Plan(inv, Defaults, SortScope.Main, true, 0, 5000).Count, 0, "same name, other wcid: no stack");
            // Without wcids the name decides (the Lua script's rule).
            inv.Packs[0].Items[23].Wcid = 0;
            inv.Main.Items[0].Wcid = 0;
            Eq(new SortPlanner().Plan(inv, Defaults, SortScope.Main, true, 0, 5000).Count, 1, "no wcid: same name stacks");

            // Room for one: two items, only the first goes; the second waits as "full".
            inv = Inv(3);
            Fill(inv.Packs[2], 23, 1000);
            inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            inv.Main.Items.Add(It(11, "Mana Stone", ObjectClasses.ManaStone));
            p = new SortPlanner();
            m = p.Plan(inv, Defaults, SortScope.Main, true, 0, 5000);
            Eq(m.Count, 1, "one slot: one move planned");
            Check(p.View.Full.Contains(3), "then pack 3 counts as full");

            // A non-full pack merges into a stack with room when there is one (keeps stacks tidy).
            inv = Inv(1);
            inv.Packs[0].Items.Add(It(50, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 8897, stack: 40, max: 100));
            inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 8897, stack: 12, max: 100));
            m = new SortPlanner().Plan(inv, Defaults, SortScope.Main, true, 0, 5000);
            Eq(m.Count == 1 ? m[0].MergeInto : 0u, 50u, "merges into an existing stack");

            // Capacity unknown (0): a side pack holds 24.
            inv = Inv(1, cap: 0);
            Fill(inv.Packs[0], 24, 1000);
            inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            Eq(new SortPlanner().Plan(inv, Defaults, SortScope.Main, true, 0, 5000).Count, 0, "unknown capacity = 24");
        }

        private static void PlanNeverMoves()
        {
            SortInventory inv = Inv(3);
            var worn = It(10, "Prismatic Taper", ObjectClasses.SpellComponent);
            worn.Wielded = true;
            inv.Main.Items.Add(worn);
            inv.Main.Items.Add(It(11, "Backpack", ObjectClasses.Container));
            inv.Main.Items.Add(It(12, "Foci of Enchantment", ObjectClasses.Foci));
            var rules = new List<SortRule>
            {
                SortRule.ForClass(ObjectClasses.SpellComponent, 1),
                SortRule.ForClass(ObjectClasses.Container, 1),
                SortRule.ForClass(ObjectClasses.Foci, 1),
            };
            var p = new SortPlanner();
            Eq(p.Plan(inv, rules, SortScope.All, true, 0, 0).Count, 0, "worn item, pack and foci never move");

            inv.Main.Items.Clear();
            inv.Main.Items.Add(It(20, "Prismatic Taper", ObjectClasses.SpellComponent));
            p.Block(20, 60_000);
            Eq(p.Plan(inv, rules, SortScope.Main, true, 1000, 0).Count, 0, "a blocked item is left alone");
            Eq(p.BlockedCount(1000), 1, "counted as blocked");
            Eq(p.Plan(inv, rules, SortScope.Main, true, 60_000, 0).Count, 1, "after its minute it is tried again");
        }

        private static void PlanMissingPack()
        {
            SortInventory inv = Inv(2);
            inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            var p = new SortPlanner();
            Eq(p.Plan(inv, Defaults, SortScope.Main, true, 0, 0).Count, 0, "pack 3 doesn't exist: no move");
            Check(p.View.Missing.Contains(3), "pack 3 reported missing");
            Eq(p.View.Packs.Count, 2, "the view lists the two packs");
        }

        private static void PlanTidyClean()
        {
            SortInventory inv = Inv(3);
            inv.Packs[0].Items.Add(It(10, "Salvaged Iron", ObjectClasses.Salvage));     // belongs in pack 2
            inv.Packs[1].Items.Add(It(11, "Salvaged Gold", ObjectClasses.Salvage));     // right where it is
            inv.Packs[2].Items.Add(It(12, "Ashcoal", ObjectClasses.Misc));              // no rule wants it
            var p = new SortPlanner();
            Eq(p.Plan(inv, Defaults, SortScope.Main, true, 0, 0).Count, 0, "Sort now leaves side packs alone");
            List<SortMove> m = p.Plan(inv, Defaults, SortScope.All, false, 0, 5000);
            Eq(m.Count, 1, "Tidy: one misplaced item");
            Check(m.Count == 1 && m[0].Item.Id == 10 && m[0].DestIndex == 2, "Tidy moves it to pack 2 (no settle time)");

            m = p.Plan(inv, Defaults, SortScope.Clean, false, 0, 5000);
            Eq(m.Count, 2, "Clean: the misplaced one and the stray");
            Check(m.Any(x => x.Item.Id == 12 && x.DestIndex == 0 && x.Dest.Id == Me), "the stray goes to the main pack");

            // Main pack full: the stray stays, and Clean says so.
            inv = Inv(1, mainCap: 2);
            Fill(inv.Main, 2, 2000);
            inv.Packs[0].Items.Add(It(12, "Ashcoal", ObjectClasses.Misc));
            p = new SortPlanner();
            Eq(p.Plan(inv, Defaults, SortScope.Clean, false, 0, 0).Count, 0, "main pack full: stray stays");
            Check(p.View.MainFull, "main pack reported full");
            // ... unless it joins a stack in the main pack.
            inv.Main.Items[0] = It(2000, "Pyreal", ObjectClasses.Money, wcid: 273, stack: 10, max: 25000);
            inv.Packs[0].Items[0] = It(12, "Pyreal", ObjectClasses.Money, wcid: 273, stack: 5, max: 25000);
            Eq(new SortPlanner().Plan(inv, Defaults, SortScope.Clean, false, 0, 0).Count, 1, "joins a main-pack stack");
        }

        private static void PlanUnpack()
        {
            SortInventory inv = Inv(3);
            inv.Packs[0].Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            inv.Packs[1].Items.Add(It(11, "Salvaged Iron", ObjectClasses.Salvage));
            inv.Packs[2].Items.Add(It(12, "Black Opal", ObjectClasses.Gem));
            var p = new SortPlanner();
            List<SortMove> m = p.PlanUnpack(inv, null, 0);
            Eq(m.Count, 3, "unpack all: every side-pack item");
            Check(m.All(x => x.DestIndex == 0), "all to the main pack");
            m = p.PlanUnpack(inv, 2, 0);
            Check(m.Count == 1 && m[0].Item.Id == 11, "unpack 2: only pack 2");
            inv = Inv(1, mainCap: 1);
            Fill(inv.Main, 1, 2000);
            inv.Packs[0].Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            m = p.PlanUnpack(inv, null, 0);
            Eq(m.Count, 0, "main pack full: nothing");
            Check(p.View.MainFull && p.View.Waiting == 1, "main full reported");
        }

        // ── Gates ───────────────────────────────────────────────────────────

        private static void Gates()
        {
            Check(SortGate.Reason(new GateInputs()) == null, "nothing going on: fine");
            Eq(SortGate.Reason(new GateInputs { InPortalSpace = true }), "portal space", "portal space");
            Eq(SortGate.Reason(new GateInputs { Looting = true }), "looting", "looting a corpse or chest");
            Eq(SortGate.Reason(new GateInputs { VendorOpen = true }), "vendor open", "vendor");
            Eq(SortGate.Reason(new GateInputs { TradeOpen = true }), "trading", "trade window");
            Eq(SortGate.Reason(new GateInputs { ClientBusy = true }), "busy", "busy (a use or a cast)");
            Eq(SortGate.Reason(new GateInputs { Animating = true }), "busy", "animating");
            Eq(SortGate.Reason(new GateInputs { InPortalSpace = true, Looting = true }), "portal space", "portal space comes first");
        }

        private static void GatesRynthAi()
        {
            RynthAiActivity run = RynthAiActivity.MacroRunning;
            Check(SortGate.Reason(new GateInputs { Bot = run }) == null, "RynthAi idle: fine");
            Check(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Navigating }) == null, "RynthAi walking a route: fine");
            Eq(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Looting }), "RynthAi is looting", "looting");
            Eq(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Salvaging }), "RynthAi is salvaging", "salvaging");
            Eq(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Buffing }), "RynthAi is buffing", "buffing / healing");
            Eq(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Crafting }), "RynthAi is making ammo", "crafting");
            Eq(SortGate.Reason(new GateInputs { Bot = RynthAiActivity.ServerAction }), "RynthAi is waiting on the server", "an unanswered use/cast, macro off too");
            Eq(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Combat }), "RynthAi is fighting", "fighting, Sort during fights off");
            Check(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Combat, SortInCombat = true }) == null, "fighting, Sort during fights on");
            Eq(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Combat | RynthAiActivity.ServerAction, SortInCombat = true }),
                "RynthAi is waiting on the server", "fighting with a cast unanswered: still waits");
            Eq(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.Combat, SortInCombat = true, Animating = true }), "busy",
                "fighting while animating: waits");
            Check(SortGate.Reason(new GateInputs { Bot = run | RynthAiActivity.AutoCram }) == null, "AutoCram alone doesn't pause (it's a warning)");
            Check(SortGate.Reason(new GateInputs { Bot = null }) == null, "no RynthAi: the client's own signals only");
        }

        private static void GateQuiet()
        {
            var g = new SortGate();
            Eq(g.Evaluate(new GateInputs { ClientBusy = true }, 10_000), "busy", "busy");
            Eq(g.Evaluate(new GateInputs(), 10_100), "busy", "just cleared: still held");
            Eq(g.Evaluate(new GateInputs(), 10_000 + SortGate.QuietMs - 1), "busy", "held until the quiet time ends");
            Check(g.Evaluate(new GateInputs(), 10_000 + SortGate.QuietMs) == null, "then clear");
            Check(new SortGate().Evaluate(new GateInputs(), 0) == null, "never paused: clear at once");
        }

        private static void LootWatching()
        {
            var w = new LootWatch();
            Check(!w.Update(0, 0, 0), "nothing open");
            Check(w.Update(0x7000_0001, 5, 1000), "a corpse just opened");
            Check(w.Update(0x7000_0001, 4, 3000), "its contents changing");
            Check(w.Update(0x7000_0001, 4, 6999), "within 4 s of the last change");
            // 6999 was the last "looting" read: 1.5 s more from there, then a finished corpse the client
            // still shows open doesn't count.
            Check(w.Update(0x7000_0001, 4, 6999 + LootWatch.AfterMs - 1), "the 1.5 s after the last looting");
            Check(!w.Update(0x7000_0001, 4, 6999 + LootWatch.AfterMs), "a finished corpse still shown open doesn't count");
            Check(w.Update(0x7000_0002, 7, 20_000), "the next corpse");
            Check(w.Update(0, 0, 20_500), "closed: 1.5 s more (the next corpse is a moment away)");
            Check(!w.Update(0, 0, 21_600), "then clear");

            // An unreadable count (-1) between real reads is not a change (2026-10-11: paused for ever).
            var f = new LootWatch();
            Check(f.Update(0x7000_0003, 6, 30_000), "a corpse opened");
            for (long t = 30_500; t < 40_000; t += 500)
                f.Update(0x7000_0003, t % 1000 == 0 ? 6 : -1, t);
            Check(!f.Update(0x7000_0003, 6, 40_000), "a flickering unreadable count doesn't keep it 'looting'");
        }

        private static void StatusJsonFallback()
        {
            Eq(HostSortWorld.FromStatusJson("{\"macro\":true,\"action\":\"Looting\"}"), RynthAiActivity.MacroRunning | RynthAiActivity.Looting, "looting");
            Eq(HostSortWorld.FromStatusJson("{\"macro\":true,\"action\":\"Healing\"}"), RynthAiActivity.MacroRunning | RynthAiActivity.Buffing, "healing = buffing slot");
            Eq(HostSortWorld.FromStatusJson("{\"macro\":true,\"action\":\"Restoring mana\"}"), RynthAiActivity.MacroRunning | RynthAiActivity.Buffing, "restoring mana");
            Eq(HostSortWorld.FromStatusJson("{\"macro\":true,\"action\":\"Following\"}"), RynthAiActivity.MacroRunning | RynthAiActivity.Navigating, "following");
            Eq(HostSortWorld.FromStatusJson("{\"macro\":true,\"action\":\"Default\"}"), RynthAiActivity.MacroRunning, "default");
            Eq(HostSortWorld.FromStatusJson("{\"macro\":false,\"action\":\"Combat\"}"), RynthAiActivity.None, "macro off: nothing");
            Check(HostSortWorld.FromStatusJson("nope") == null && HostSortWorld.FromStatusJson(null) == null, "not JSON: null");
        }

        // ── Moves ───────────────────────────────────────────────────────────

        private static MoveInFlight Flight(uint item, uint dest, uint merge = 0, int stack = 1, int seq = 7, long sent = 1000)
        {
            var it = It(item, "Thing", ObjectClasses.Misc, stack: stack);
            var pack = new SortPack { Id = dest };
            return new MoveInFlight { Move = new SortMove(it, pack, 1, merge), SentMs = sent, ErrorSeqAtSend = seq, StackAtSend = stack };
        }

        private static void MoveAnswers()
        {
            MoveInFlight m = Flight(10, 0x40000001);
            Eq(MoveTracker.Observe(m, new MoveObservation { Exists = true, Container = Me, ErrorSeq = 7 }, 1500), MoveOutcome.Pending, "still in the main pack");
            Eq(MoveTracker.Observe(m, new MoveObservation { Exists = true, Container = 0x40000001, ErrorSeq = 7 }, 1500), MoveOutcome.Done, "landed");
            Eq(MoveTracker.Observe(m, new MoveObservation { Exists = false }, 1500), MoveOutcome.Done, "gone: merged on arrival");
            Eq(MoveTracker.Observe(m, new MoveObservation { Exists = true, Container = Me, ErrorSeq = 8, ErrorEventType = 0xA0, ErrorObject = 10 }, 1500),
                MoveOutcome.Refused, "InventoryServerSaveFailed for it");
            Eq(MoveTracker.Observe(m, new MoveObservation { Exists = true, Container = Me, ErrorSeq = 8, Error = 0x1D, ErrorEventType = 0x28A }, 1500),
                MoveOutcome.TooBusy, "You're too busy");
            Eq(MoveTracker.Observe(m, new MoveObservation { Exists = true, Container = Me, ErrorSeq = 8, Error = 0x402, ErrorEventType = 0x28A, ErrorObject = 99 }, 1500),
                MoveOutcome.Pending, "someone else's refusal (a fizzle) isn't ours");
            Eq(MoveTracker.Observe(m, new MoveObservation { Exists = true, Container = Me, ErrorSeq = 7 }, 1000 + MoveTracker.TimeoutMs),
                MoveOutcome.TimedOut, "no answer in 5 s");
            MoveInFlight merge = Flight(10, 0x40000001, merge: 50, stack: 12);
            Eq(MoveTracker.Observe(merge, new MoveObservation { Exists = true, Container = Me, Stack = 4, ErrorSeq = 7 }, 1500),
                MoveOutcome.Done, "a merge that took part of the stack");

            Eq(MoveTracker.BackoffMs(0), 0L, "no failures: no hold");
            Eq(MoveTracker.BackoffMs(1), 2000L, "one: 2 s");
            Eq(MoveTracker.BackoffMs(3), 8000L, "three: 8 s");
            Eq(MoveTracker.BackoffMs(30), MoveTracker.MaxBackoffMs, "capped at 30 s");
        }

        // ── The loop ────────────────────────────────────────────────────────

        /// <summary>A pretend game for the controller: packs, gate inputs and how the server answers.</summary>
        private sealed class FakeWorld : ISortWorld
        {
            public SortInventory Inv = Inv(3);
            public GateInputs Gate;
            public readonly List<SortMove> Sent = new();
            public bool EngineAccepts = true;
            /// <summary>What the pretend engine can do (none: as an engine before API v80 without merges).</summary>
            public SortAbilities Can;
            /// <summary>"land" moves the item at once; "refuse" answers with InventoryServerSaveFailed; "busy" with You're too busy; "silent" never answers.</summary>
            public string Server = "land";
            public int ErrorSeq = 3;
            private readonly HashSet<uint> _gone = new();

            public SortInventory? ReadInventory() => Inv;
            public GateInputs ReadGate(bool sortInCombat, long nowMs) { GateInputs g = Gate; g.SortInCombat = sortInCombat; return g; }
            public int ErrorSeqNow() => ErrorSeq;
            public SortAbilities Abilities => Can;

            public bool SendMove(in SortMove move)
            {
                Sent.Add(move);
                if (!EngineAccepts) return false;
                switch (Server)
                {
                    case "land":
                        if (ServerApply(Inv, move)) _gone.Add(move.Item.Id);
                        break;
                    case "refuse":
                        ErrorSeq++;
                        _refused = move.Item.Id;
                        break;
                    case "busy":
                        ErrorSeq++;
                        _busy = true;
                        break;
                }
                return true;
            }

            private uint _refused;
            private bool _busy;

            public MoveObservation Observe(in SortMove move)
            {
                uint itemId = move.Item.Id;
                var o = new MoveObservation { Exists = !_gone.Contains(itemId), Container = Me, ErrorSeq = ErrorSeq, Slot = -1, Stack = move.Item.Stack };
                foreach (SortPack p in Inv.Packs.Prepend(Inv.Main))
                {
                    int at = p.Items.FindIndex(i => i.Id == itemId);
                    if (at < 0) continue;
                    o.Container = p.Id;
                    o.Slot = at;
                    o.Stack = p.Items[at].Stack;
                }
                if (_refused == itemId) { o.ErrorEventType = 0xA0; o.ErrorObject = itemId; }
                if (_busy) { o.Error = 0x1D; o.ErrorEventType = 0x28A; }
                return o;
            }
        }

        /// <summary>
        /// What ACE does with a move: a merge adds what fits to the other stack (the rest stays; a
        /// stack emptied is gone: true); a move takes the item out and puts it in at its placement
        /// (clamped), or at the end when it names none. Items keep their objects (the planner's
        /// copies of stack sizes see the change, as a fresh read would).
        /// </summary>
        private static bool ServerApply(SortInventory inv, SortMove move)
        {
            uint id = move.Item.Id;
            uint destId = move.Dest.Id;
            SortPack? from = inv.Packs.Prepend(inv.Main).FirstOrDefault(p => p.Items.Any(i => i.Id == id));
            if (from == null) return false;
            SortItem item = from.Items.First(i => i.Id == id);
            SortPack dest = destId == inv.Main.Id ? inv.Main : inv.Packs.First(p => p.Id == destId);
            uint mergeInto = move.MergeInto;
            if (mergeInto != 0)
            {
                SortItem? into = dest.Items.FirstOrDefault(i => i.Id == mergeInto);
                if (into == null) return false;
                int max = into.MaxStack > 1 ? into.MaxStack : item.MaxStack;
                int amount = Math.Min(item.Stack, max - into.Stack);
                if (amount <= 0) return false;
                into.Stack += amount;
                item.Stack -= amount;
                if (item.Stack > 0) return false;
                from.Items.Remove(item);
                return true;
            }
            from.Items.Remove(item);
            int at = move.Placement >= 0 ? Math.Clamp(move.Placement, 0, dest.Items.Count) : dest.Items.Count;
            dest.Items.Insert(at, item);
            return false;
        }

        private static SortController Controller(bool auto = true, int gapMs = 300, int settleSec = 0)
        {
            var c = new SortController();
            c.Data.Settings.Auto = auto;
            c.Data.Settings.GapMs = gapMs;
            c.Data.Settings.SettleSec = settleSec;
            return c;
        }

        /// <summary>Ticks every 16 ms from <paramref name="from"/> to <paramref name="to"/>.</summary>
        private static void Ticks(SortController c, FakeWorld w, long from, long to)
        {
            for (long t = from; t <= to; t += 16) c.Tick(w, t);
        }

        private static void LoopPaused()
        {
            foreach (var (name, gate) in new (string, GateInputs)[]
            {
                ("looting", new GateInputs { Looting = true }),
                ("vendor", new GateInputs { VendorOpen = true }),
                ("trade", new GateInputs { TradeOpen = true }),
                ("busy", new GateInputs { ClientBusy = true }),
                ("animating", new GateInputs { Animating = true }),
                ("portal", new GateInputs { InPortalSpace = true }),
                ("RynthAi looting", new GateInputs { Bot = RynthAiActivity.MacroRunning | RynthAiActivity.Looting }),
                ("RynthAi salvaging", new GateInputs { Bot = RynthAiActivity.MacroRunning | RynthAiActivity.Salvaging }),
                ("RynthAi buffing", new GateInputs { Bot = RynthAiActivity.MacroRunning | RynthAiActivity.Buffing }),
                ("RynthAi waiting on the server", new GateInputs { Bot = RynthAiActivity.ServerAction }),
                ("RynthAi fighting", new GateInputs { Bot = RynthAiActivity.MacroRunning | RynthAiActivity.Combat }),
            })
            {
                var w = new FakeWorld { Gate = gate };
                w.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
                SortController c = Controller();
                Ticks(c, w, 0, 10_000);
                Eq(w.Sent.Count, 0, $"{name}: nothing sent");
                Check(c.Paused, $"{name}: status says paused ({c.Status})");
                c.StartJob(SortScope.All);
                Ticks(c, w, 10_000, 12_000);
                Eq(w.Sent.Count, 0, $"{name}: a job asked for waits too");
                // Clear: after the quiet time it goes (counted from the last paused read, which is at
                // most one poll before the clear).
                w.Gate = new GateInputs();
                long quietEnd = 12_000 + SortGate.QuietMs - SortController.PollMs - 50;
                Ticks(c, w, 12_000, quietEnd);
                Eq(w.Sent.Count, 0, $"{name}: not during the quiet time");
                Ticks(c, w, quietEnd + 16, 16_000);
                Eq(w.Sent.Count, 1, $"{name}: moves once clear");
            }
        }

        private static void LoopOneAtATime()
        {
            var w = new FakeWorld { Server = "silent" };
            for (uint i = 0; i < 3; i++) w.Inv.Main.Items.Add(It(10 + i, "Prismatic Taper " + i, ObjectClasses.SpellComponent));
            SortController c = Controller(gapMs: 300);
            Ticks(c, w, 0, 2000);
            Eq(w.Sent.Count, 1, "one move out, unanswered: no second one");
            Check(c.HasMoveOut, "the move is out");

            // Answered: the next goes only after the gap.
            w.Server = "land";
            var w2 = new FakeWorld();
            for (uint i = 0; i < 3; i++) w2.Inv.Main.Items.Add(It(10 + i, "Prismatic Taper " + i, ObjectClasses.SpellComponent));
            SortController c2 = Controller(gapMs: 300);
            c2.Tick(w2, 0);
            Eq(w2.Sent.Count, 1, "first move");
            c2.Tick(w2, 16);   // answer seen (landed)
            Check(!c2.HasMoveOut, "answered");
            c2.Tick(w2, 16 + 299);
            Eq(w2.Sent.Count, 1, "the gap holds the next one");
            c2.Tick(w2, 16 + 300);
            Eq(w2.Sent.Count, 2, "second move after the gap");
            Ticks(c2, w2, 400, 3000);
            Eq(w2.Sent.Count, 3, "all three moved");
            Eq(w2.Inv.Packs[0].Items.Count, 3, "all in pack 1");
            Eq(c2.Moved, 3, "three moved");
            Eq(c2.Status, "watching", "then watching");
            Check(c2.Log.Count == 3 && c2.Log[0].Contains("-> pack 1"), "moves logged, newest first");

            // Settle time: nothing before it.
            var w3 = new FakeWorld();
            w3.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c3 = Controller(settleSec: 5);
            Ticks(c3, w3, 0, 4900);
            Eq(w3.Sent.Count, 0, "settling");
            Ticks(c3, w3, 4900, 6000);
            Eq(w3.Sent.Count, 1, "moved after the settle time");

            // Auto-sort off: nothing, status off.
            var w4 = new FakeWorld();
            w4.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c4 = Controller(auto: false);
            Ticks(c4, w4, 0, 3000);
            Eq(w4.Sent.Count, 0, "auto-sort off: nothing moves");
            Eq(c4.Status, "off", "status off");
        }

        private static void LoopRefusals()
        {
            var w = new FakeWorld { Server = "refuse" };
            w.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            w.Inv.Main.Items.Add(It(11, "Salvaged Iron", ObjectClasses.Salvage));
            SortController c = Controller(gapMs: 300);
            c.Tick(w, 0);
            Eq(w.Sent.Count, 1, "first move");
            c.Tick(w, 16);
            Check(!c.HasMoveOut, "refusal seen");
            Check(c.Planner.IsBlocked(10, 17), "the refused item is left alone");
            Check(c.Log[0].Contains("not moved"), "logged as not moved");
            Ticks(c, w, 32, 16 + MoveTracker.BackoffMs(1) - 16);
            Eq(w.Sent.Count, 1, "held for the back-off");
            w.Server = "land";
            Ticks(c, w, 16 + MoveTracker.BackoffMs(1), 6000);
            Eq(w.Sent.Count, 2, "then the next item");
            Eq(w.Sent[1].Item.Id, 11u, "it is the other item, not the refused one");
            Ticks(c, w, 6000, 50_000);
            Eq(w.Sent.Count, 2, "the refused one waits out its minute");
            Ticks(c, w, 50_000, 62_000);
            Eq(w.Sent.Count, 3, "tried again after a minute");
            Eq(c.Failed, 1, "one failure counted");

            // The engine refusing the call counts as a refusal too.
            var w2 = new FakeWorld { EngineAccepts = false };
            w2.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c2 = Controller();
            c2.Tick(w2, 0);
            Check(c2.Planner.IsBlocked(10, 1) && c2.Log[0].Contains("engine refused"), "engine refusal blocks the item");

            // Failures in a row hold longer each time.
            var w3 = new FakeWorld { Server = "refuse" };
            for (uint i = 0; i < 4; i++) w3.Inv.Main.Items.Add(It(10 + i, "Taper " + i, ObjectClasses.SpellComponent));
            SortController c3 = Controller();
            var sentAt = new List<long>();
            for (long t = 0; t < 40_000; t += 16)
            {
                int before = w3.Sent.Count;
                c3.Tick(w3, t);
                if (w3.Sent.Count > before) sentAt.Add(t);
            }
            Check(sentAt.Count == 4, $"four tries ({sentAt.Count})");
            if (sentAt.Count == 4)
                Check(sentAt[2] - sentAt[1] > sentAt[1] - sentAt[0] && sentAt[3] - sentAt[2] > sentAt[2] - sentAt[1], "each hold longer than the last");
        }

        private static void LoopBusyAndTimeout()
        {
            var w = new FakeWorld { Server = "busy" };
            w.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c = Controller();
            c.Tick(w, 0);
            c.Tick(w, 16);
            Check(!c.Planner.IsBlocked(10, 17), "too busy: the item isn't blocked");
            Check(c.Log[0].Contains("busy"), "logged");
            w.Server = "land";
            Ticks(c, w, 32, 1900);
            Eq(w.Sent.Count, 1, "held for the back-off");
            Ticks(c, w, 1900, 3000);
            Eq(w.Sent.Count, 2, "then tried again");

            var w2 = new FakeWorld { Server = "silent" };
            w2.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c2 = Controller();
            Ticks(c2, w2, 0, MoveTracker.TimeoutMs - 16);
            Check(c2.HasMoveOut, "waiting for the answer");
            Ticks(c2, w2, MoveTracker.TimeoutMs, MoveTracker.TimeoutMs + 32);
            Check(!c2.HasMoveOut && c2.Planner.IsBlocked(10, MoveTracker.TimeoutMs + 32), "no answer in 5 s: blocked");
            Check(c2.Log[0].Contains("no answer"), "logged");
        }

        private static void LoopJobs()
        {
            // Tidy: misplaced side-pack items, as fast as the server answers (no auto gap).
            var w = new FakeWorld();
            w.Inv.Packs[0].Items.Add(It(10, "Salvaged Iron", ObjectClasses.Salvage));
            w.Inv.Packs[0].Items.Add(It(11, "Salvaged Gold", ObjectClasses.Salvage));
            SortController c = Controller(auto: false, gapMs: 3000);
            c.StartJob(SortScope.All);
            Ticks(c, w, 0, 600);
            Eq(w.Sent.Count, 2, "tidy moved both quickly (job gap, not the 3 s auto gap)");
            Eq(w.Inv.Packs[1].Items.Count, 2, "both in pack 2");
            Check(c.Job == null && c.Log[0] == "Packs sorted", "job ends with a note");

            // Unpack turns auto-sort off (and asks for a save) so nothing moves back.
            var w2 = new FakeWorld();
            w2.Inv.Packs[0].Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            w2.Inv.Packs[2].Items.Add(It(12, "Black Opal", ObjectClasses.Gem));
            SortController c2 = Controller(auto: true);
            int saves = 0;
            c2.SettingsChanged = () => saves++;
            c2.StartUnpack(null);
            Check(!c2.Data.Settings.Auto && saves == 1, "unpack: auto-sort off, saved");
            Ticks(c2, w2, 0, 1000);
            Eq(w2.Inv.Main.Items.Count, 2, "both in the main pack");
            Eq(c2.Log[0], "Side packs emptied", "noted");
            Ticks(c2, w2, 1000, 20_000);
            Eq(w2.Sent.Count, 2, "nothing moved back");

            // Unpack one pack.
            var w3 = new FakeWorld();
            w3.Inv.Packs[0].Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            w3.Inv.Packs[2].Items.Add(It(12, "Black Opal", ObjectClasses.Gem));
            SortController c3 = Controller();
            c3.StartUnpack(3);
            Ticks(c3, w3, 0, 1000);
            Check(w3.Inv.Main.Items.Count == 1 && w3.Inv.Main.Items[0].Id == 12, "only pack 3 emptied");

            // Clean: strays back to the main pack.
            var w4 = new FakeWorld();
            w4.Inv.Packs[1].Items.Add(It(12, "Ashcoal", ObjectClasses.Misc));
            SortController c4 = Controller(auto: false);
            c4.StartJob(SortScope.Clean);
            Ticks(c4, w4, 0, 1000);
            Check(w4.Inv.Main.Items.Count == 1 && c4.Log[0] == "Packs cleaned", "clean sent the stray home");

            // Sort now ignores the settle time.
            var w5 = new FakeWorld();
            w5.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c5 = Controller(settleSec: 30);
            c5.StartJob(SortScope.Main);
            Ticks(c5, w5, 0, 200);
            Eq(w5.Sent.Count, 1, "sort now: no settle time");
        }

        // ── Store ───────────────────────────────────────────────────────────

        private static SortStore TempStore(string name)
        {
            string root = Path.Combine(_temp, name);
            return new SortStore(Path.Combine(root, "RynthSort"), Path.Combine(root, "RynthLua", "Data", "RynthSort"));
        }

        private static void StoreRoundTrip()
        {
            SortStore st = TempStore("roundtrip");
            CharacterSortData d = st.Load("DreamWeave", "Drakkon", out string? problem);
            Check(problem == null && d.Rules.Count == 3 && d.ImportedFrom.Length == 0, "no file: the defaults");
            Check(!d.Settings.WindowOpen, "no file: the window starts closed (the bar button opens it)");
            d.Rules.Add(SortRule.ForName("Pyreal \"Mote\"", 6));
            d.Rules[0].On = false;
            d.Settings.Auto = false;
            d.Settings.SettleSec = 12;
            d.Settings.GapMs = 900;
            d.Settings.SortInCombat = true;
            d.Settings.WindowOpen = true;
            Check(st.Save("DreamWeave", "Drakkon", d) == null, "saved");
            Check(File.Exists(Path.Combine(st.Root, "characters", "DreamWeave_Drakkon.json")), "file named server_character");
            CharacterSortData back = st.Load("DreamWeave", "Drakkon", out problem);
            Check(problem == null, "read back");
            Eq(back.Rules.Count, 4, "rules");
            Check(!back.Rules[0].On && back.Rules[3].Classes.Count == 0 && back.Rules[3].Names.Count == 1 && back.Rules[3].Names[0] == "Pyreal \"Mote\"" && back.Rules[3].Pack == 6, "rule fields");
            Check(back.Rules[2].SameAs(SortRules.Defaults()[2]), "a rule with three classes comes back whole");
            Check(!back.Settings.Auto && back.Settings.SettleSec == 12 && back.Settings.GapMs == 900 && back.Settings.SortInCombat && back.Settings.WindowOpen, "settings");
            Eq(SortStore.FileStem("Dream:Weave", "Con"), "Dream_Weave_Con", "unsafe characters replaced like RynthLua");
            Eq(SortStore.SafeFileName("CON"), "_CON", "reserved names guarded");
        }

        // The Lua script's file for Drakkon, as it is on disk (2026-10-07).
        private const string LuaFile = "{\n  \"rules\": [{\"on\":true,\"kind\":\"class\",\"class\":12,\"text\":\"\",\"pack\":1},{\"on\":true,\"kind\":\"class\",\"class\":39,\"text\":\"\",\"pack\":2},{\"on\":true,\"kind\":\"class\",\"class\":11,\"text\":\"\",\"pack\":3},{\"on\":true,\"kind\":\"class\",\"class\":16,\"text\":\"\",\"pack\":3},{\"on\":true,\"kind\":\"class\",\"class\":13,\"text\":\"\",\"pack\":3},{\"on\":true,\"kind\":\"class\",\"class\":29,\"text\":\"\",\"pack\":4}],\n  \"settings\": {\"auto\":false,\"settleSec\":5,\"gapMs\":600}\n}\n";

        private static void StoreLuaImport()
        {
            SortStore st = TempStore("import");
            string lua = st.LuaPathFor("DreamWeave", "Drakkon");
            Directory.CreateDirectory(Path.GetDirectoryName(lua)!);
            File.WriteAllText(lua, LuaFile);
            CharacterSortData d = st.Load("DreamWeave", "Drakkon", out string? problem);
            Check(problem == null, "Lua file read");
            Eq(d.ImportedFrom, lua, "marked as imported");
            Eq(d.Rules.Count, 6, "six rules");
            Check(d.Rules[5].Names.Count == 0 && d.Rules[5].Classes.Count == 1 && d.Rules[5].Classes[0] == ObjectClasses.HealingKit && d.Rules[5].Pack == 4, "healing kits to pack 4 (one class)");
            Check(!d.Settings.Auto && d.Settings.SettleSec == 5 && d.Settings.GapMs == 600, "settings taken over");
            Check(!d.Settings.SortInCombat, "plugin-only settings at their defaults");
            Check(d.Settings.WindowOpen, "taken over from the Lua script: the window opens once to show the rules");

            // Once RynthSort has its own file, the Lua file is no longer read (and is left alone).
            d.Rules.RemoveAt(0);
            st.Save("DreamWeave", "Drakkon", d);
            CharacterSortData again = st.Load("DreamWeave", "Drakkon", out _);
            Eq(again.Rules.Count, 5, "own file wins over the Lua file");
            Check(File.Exists(lua) && File.ReadAllText(lua) == LuaFile, "the Lua file is untouched");

            // Another character with no Lua file: defaults.
            Eq(st.Load("DreamWeave", "Someone", out _).ImportedFrom, "", "nothing to import: defaults");
        }

        private static void StoreOddFiles()
        {
            SortStore st = TempStore("odd");
            string own = st.PathFor("S", "A");
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.WriteAllText(own, "{ not json");
            CharacterSortData d = st.Load("S", "A", out string? problem);
            Check(problem != null && d.Rules.Count == 3, "broken file: defaults, problem reported");

            // The Lua script saved an empty rule list as {}; numbers as doubles; out-of-range values clamped.
            File.WriteAllText(own, "{\"rules\":{},\"settings\":{\"auto\":true,\"settleSec\":99.0,\"gapMs\":5,\"x\":1}}");
            d = st.Load("S", "A", out problem);
            Check(problem == null && d.Rules.Count == 0, "{} = no rules");
            Check(d.Settings.SettleSec == SortSettings.SettleMax && d.Settings.GapMs == SortSettings.GapMin, "clamped");

            File.WriteAllText(own, "{\"rules\":[{\"kind\":\"name\",\"text\":\"Key\",\"pack\":2.0},{\"class\":11,\"pack\":-4},7,{\"on\":false}]}");
            d = st.Load("S", "A", out _);
            Eq(d.Rules.Count, 3, "a non-object entry is skipped");
            Check(d.Rules[0].Names.Count == 1 && d.Rules[0].Names[0] == "Key" && d.Rules[0].Classes.Count == 0 && d.Rules[0].Pack == 2 && d.Rules[0].On, "missing 'on' = on; 2.0 = 2");
            Eq(d.Rules[1].Pack, 1, "pack clamped to 1");
            Check(!d.Rules[2].On && d.Rules[2].Classes.Count == 1 && d.Rules[2].Classes[0] == ObjectClasses.SpellComponent, "defaults for missing fields");
        }

        private static void LuaAutostart()
        {
            static bool Starts(string json, string key)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return RynthSortPlugin.LuaScriptStarts(doc.RootElement, key);
            }
            Check(!Starts("{\"GlobalScripts\":[],\"CharacterScripts\":{\"DreamWeave|Drakkon\":[\"item-counter\"]}}", "DreamWeave|Drakkon"), "today's settings: not set to start");
            Check(Starts("{\"GlobalScripts\":[\"RynthSort\"]}", "DreamWeave|Drakkon"), "global autostart");
            Check(Starts("{\"CharacterScripts\":{\"DreamWeave|Drakkon\":[\"rynthsort\"]}}", "DreamWeave|Drakkon"), "this character's autostart");
            Check(!Starts("{\"CharacterScripts\":{\"DreamWeave|Other\":[\"RynthSort\"]}}", "DreamWeave|Drakkon"), "another character's");
        }

        // ── Window ──────────────────────────────────────────────────────────

        private static (SortController, SortPanel, UiHost, UiWindow) Window()
        {
            var c = new SortController();
            var panel = new SortPanel(c, () => { }) { Character = () => "Drakkon" };
            var host = new UiHost();
            UiWindow w = host.Add(new UiWindow("RynthSort/Main", "RynthSort") { Visible = true });
            var inv = Inv(4);
            Fill(inv.Packs[0], 24, 1000);
            inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            c.Planner.Plan(inv, c.Data.Rules, SortScope.Main, true, 0, 0);
            return (c, panel, host, w);
        }

        private static void Draw(UiWindow w, SortPanel panel)
        {
            w.Begin();
            panel.Draw(w);
            w.End();
        }

        private static string BodyText(UiWindow w) => Encoding.UTF8.GetString(w.Body);

        private static unsafe void ParseAll(UiHost host, string what, out int maxOps)
        {
            int n = Write(host);
            byte[] buf = SubmitBuffer(host);
            int rc;
            List<ParsedWindow> windows;
            string error;
            fixed (byte* p = buf) rc = DisplayListParser.Parse(p, n, 4000f, out _, out windows, out error);
            Check(rc == 0, $"{what}: engine parser refused the submit ({rc}): {error}");
            maxOps = windows.Where(x => x.List != null).Select(x => x.List!.OpCount).DefaultIfEmpty(0).Max();
        }

        private static unsafe List<ParsedWindow> ParseWindows(UiHost host)
        {
            int n = Write(host);
            byte[] buf = SubmitBuffer(host);
            List<ParsedWindow> windows;
            fixed (byte* p = buf) DisplayListParser.Parse(p, n, 4000f, out _, out windows, out _);
            return windows;
        }

        private static void WindowBarButton()
        {
            Eq(UiFormat.FlagBarButton, WindowFlags.BarButton, "the plugin's bit is the engine's");
            var (_, panel, host, w) = Window();
            Draw(w, panel);
            List<ParsedWindow> windows = ParseWindows(host);
            Check(windows.Count == 1 && windows[0].Title == "RynthSort", "one window, titled RynthSort (the button's tooltip)");
            uint f = windows.Count == 1 ? windows[0].Flags : 0;
            Check((f & WindowFlags.ShowInBar) != 0 && (f & WindowFlags.BarButton) != 0, "ShowInBar + BarButton survive the engine's parse");
            w.MarkSubmitted(true);

            w.ShowInBar = false;
            windows = ParseWindows(host);
            f = windows.Count == 1 ? windows[0].Flags : ~0u;
            Check((f & (WindowFlags.ShowInBar | WindowFlags.BarButton)) == 0, "not in the bar: no button either");
        }

        private static byte[] Event(ushort type, uint seq, uint window, uint key, byte[] payload)
        {
            var e = new byte[16 + payload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(e, type);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)e.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), seq);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), window);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), key);
            payload.CopyTo(e, 16);
            return e;
        }

        private static byte[] Click => new byte[] { 1, 0 };

        private static void WindowParses()
        {
            var (c, panel, host, w) = Window();
            Draw(w, panel);
            ParseAll(host, "default rules", out int ops);
            Check(ops > 10 && ops < 1500, $"ops in range ({ops})");
            string body = BodyText(w);
            Check(body.Contains("Auto-sort") && body.Contains("Sort now") && body.Contains("Clean packs") && body.Contains("Empty side packs"), "buttons and toggle");
            Check(body.Contains("Main pack (1/102)"), "the main pack's line");
            Check(body.Contains("Pack 1: Pack 1 (24/24) - Junk 0, Junk 1, Junk 2"), "a section per pack, named with its room and contents");
            Check(body.Contains("Pack 3: Pack 3 (0/24) - empty") && body.Contains("Gem, Key, Mana Stone"), "a rule with three classes as one row under its pack");
            Check(body.Contains("Pack 4: Pack 4 (0/24) - empty") && body.Contains("No rules send items here."), "a pack without rules says so");
            Check(body.IndexOf("Pack 1: ", StringComparison.Ordinal) < body.IndexOf("Spell Component", StringComparison.Ordinal)
                  && body.IndexOf("Spell Component", StringComparison.Ordinal) < body.IndexOf("Pack 2: ", StringComparison.Ordinal), "a rule sits under its pack");
            Check(!body.Contains("Classes:"), "no editor until a rule is picked");
            w.MarkSubmitted(true);

            // The gem rule's editor, the settings open, warnings and a log.
            SortRule gemRule = c.Data.Rules[2];
            Apply(host, Event(1, 1, w.Hash, w.KeyOf($"r{gemRule.Uid}.row"), Click));
            Apply(host, Event(6, 2, w.Hash, w.KeyOf("settings"), new byte[] { 1 }));
            c.Note("Prismatic Taper -> pack 1");
            panel.Warnings = () => new[] { "The RynthSort Lua script is set to start too" };
            Draw(w, panel);
            ParseAll(host, "rule editor", out _);
            body = BodyText(w);
            Check(body.Contains("Classes:") && body.Contains("Gem  x") && body.Contains("Key  x") && body.Contains("Mana Stone  x"), "the rule's classes as chips");
            Check(body.Contains("Add a class...") && body.Contains("Name has") && body.Contains("Remove rule"), "add a class, add a name, remove");
            Check(body.Contains("Pack 3: Pack 3 (0/24) - empty") && body.Contains("Pack 7 (none)"), "the pack picker");
            Check(body.Contains("Settle (s)") && body.Contains("Gap (ms)") && body.Contains("Sort during fights")
                  && body.Contains("Put items in order inside packs") && body.Contains("Group by kind first"), "settings shown");
            Check(body.Contains("plugin API 80"), "an engine that can't read orders: said in the settings");
            Check(body.Contains("Lua script is set to start"), "warning shown");
            w.MarkSubmitted(true);

            gemRule.AddName("Opal");
            gemRule.AddName("a##b");
            Draw(w, panel);
            ParseAll(host, "rule editor with names", out _);
            body = BodyText(w);
            Check(body.Contains("Names:") && body.Contains("\"Opal\"  x"), "names as chips");
            Check(body.Contains("Gem, Key, Mana Stone, name has \"a# #b\", name has \"Opal\""), "the row: classes, then names, A-Z (## shown apart)");

            // A rule whose pack isn't carried: its own section, grouped by what the pack was.
            var gone = SortRule.ForClass(ObjectClasses.Lockpick, 9);
            gone.PackId = 0x4000AAAA;
            gone.PackNote = "Sack - Gems";
            c.Data.Rules.Add(gone);
            var inv = Inv(4);
            Fill(inv.Packs[0], 24, 1000);
            inv.Packs[0].OrderKnown = true;
            c.Planner.Plan(inv, c.Data.Rules, SortScope.All, true, 0, 0, PlanOptions.Full);
            Draw(w, panel);
            ParseAll(host, "missing pack", out _);
            body = BodyText(w);
            Check(body.Contains("Rules for packs you don't have (1)") && body.Contains("A pack you don't have (it was Sack - Gems):") && body.Contains("Lockpick"), "missing-pack section");
            Check(body.Contains("Pack 1: Pack 1 (24/24)") && body.Contains("in order"), "a pack in order says so");

            // Two rules naming the same class for different packs: the losing pack says so.
            c.Data.Rules.Add(SortRule.ForClass(ObjectClasses.Gem, 4));
            Draw(w, panel);
            ParseAll(host, "conflict", out _);
            Check(BodyText(w).Contains("Gem: also in a rule for pack 3, which wins"), "conflict shown under the losing pack");

            // Many rules still fit.
            for (int i = 0; i < SortStore.MaxRules; i++) c.Data.Rules.Add(SortRule.For(1 + i % 8, new[] { ObjectClasses.Armor, ObjectClasses.Food }, "x" + i, "y" + i));
            Draw(w, panel);
            ParseAll(host, "64+ rules", out int many);
            Check(many < 1500, $"many rules fit ({many} ops)");

            // Logged out.
            panel.Character = () => "";
            Draw(w, panel);
            ParseAll(host, "logged out", out _);
            Check(BodyText(w).Contains("Log in"), "logged-out text");
        }

        private static byte[] TextEvent(string text, bool submitted)
        {
            byte[] t = Encoding.UTF8.GetBytes(text);
            var payload = new byte[3 + t.Length];
            payload[0] = submitted ? (byte)1 : (byte)0;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)t.Length);
            t.CopyTo(payload, 3);
            return payload;
        }

        private static void WindowEvents()
        {
            var (c, panel, host, w) = Window();
            Draw(w, panel);
            int rules = c.Data.Rules.Count;
            uint seq = 0;
            void Send(ushort type, string id, byte[] payload)
            {
                Apply(host, Event(type, ++seq, w.Hash, w.KeyOf(id), payload));
                Draw(w, panel);
            }

            // "+ Add rule" in pack 4's section: a new, empty rule for pack 4, its editor open.
            Send(1, "add.40000004", Click);
            Eq(c.Data.Rules.Count, rules + 1, "Add rule");
            SortRule r = panel.Selected!;
            Check(r != null && r.IsEmpty && r.PackId == 0x40000004 && r.Pack == 4, "the new rule is selected, empty, for that pack");
            Check(BodyText(w).Contains("(empty: add a class or a name)"), "an empty rule says what to do");
            string k = "r" + r!.Uid;

            // Classes through the picker (its first line is "Add a class...").
            Send(3, k + ".addclass", BitConverter.GetBytes(ObjectClasses.ChoiceIndex(ObjectClasses.Armor) + 1));
            Check(r.Classes.Count == 1 && r.HasClass(ObjectClasses.Armor), "picker adds Armor");
            int clothing = ObjectClasses.Choices.Where(x => x != ObjectClasses.Armor).ToList().IndexOf(ObjectClasses.Clothing) + 1;
            Send(3, k + ".addclass", BitConverter.GetBytes(clothing));
            Check(r.HasClass(ObjectClasses.Clothing) && r.Classes.Count == 2, "and Clothing (the picker leaves out what the rule has)");
            Send(3, k + ".addclass", BitConverter.GetBytes(0));
            Eq(r.Classes.Count, 2, "the picker's first line adds nothing");

            // Names: typed and Enter, or typed and Add.
            Send(5, k + ".draft", TextEvent("Shield", true));
            Check(r.Names.Count == 1 && r.HasName("Shield"), "Enter adds the name");
            Send(5, k + ".draft", TextEvent("Buckler", false));
            Eq(r.Names.Count, 1, "typing alone adds nothing");
            Send(1, k + ".addname", Click);
            Check(r.HasName("Buckler") && r.Names.Count == 2, "Add adds it");
            Send(5, k + ".draft", TextEvent("shield", true));
            Eq(r.Names.Count, 2, "the same name in another case isn't added twice");
            Check(BodyText(w).Contains("Armor, Clothing, name has \"Buckler\", name has \"Shield\""), "the row lists them");

            // Chips take entries out.
            Send(1, $"{k}.c{ObjectClasses.Clothing}", Click);
            Check(!r.HasClass(ObjectClasses.Clothing) && r.HasClass(ObjectClasses.Armor), "a class chip removes its class");
            Send(1, k + ".n0", Click);
            Check(!r.HasName("Buckler") && r.HasName("Shield"), "a name chip removes its name (A-Z: Buckler first)");

            // Another pack through the picker.
            Send(3, k + ".pack", BitConverter.GetBytes(1));
            Check(r.Pack == 2 && r.PackId == 0x40000002 && r.PackNote.StartsWith("Pack 2 - "), $"moved to pack 2, kept by its id ({r.PackNote})");
            string body = BodyText(w);
            Check(body.IndexOf("Pack 2: ", StringComparison.Ordinal) < body.IndexOf("Armor, name has", StringComparison.Ordinal)
                  && body.IndexOf("Armor, name has", StringComparison.Ordinal) < body.IndexOf("Pack 3: ", StringComparison.Ordinal), "now listed under pack 2");

            // On and off.
            Send(2, k + ".on", new byte[] { 0 });
            Check(!r.On, "the box turns the rule off");
            Send(2, k + ".on", new byte[] { 1 });
            Check(r.On, "and on");

            // Merge rules: a second rule for pack 3 joins the first.
            Send(1, "add.40000003", Click);
            SortRule second = panel.Selected!;
            Send(3, "r" + second.Uid + ".addclass", BitConverter.GetBytes(ObjectClasses.ChoiceIndex(ObjectClasses.Lockpick) + 1));
            Check(BodyText(w).Contains("Merge rules"), "two rules in a pack: Merge rules offered");
            int before = c.Data.Rules.Count;
            Send(1, "merge.40000003", Click);
            Eq(c.Data.Rules.Count, before - 1, "two rules became one");
            SortRule merged = c.Data.Rules.Single(x => x.PackId == 0x40000003 || x.Pack == 3);
            Check(merged.HasClass(ObjectClasses.Gem) && merged.HasClass(ObjectClasses.Key) && merged.HasClass(ObjectClasses.ManaStone) && merged.HasClass(ObjectClasses.Lockpick),
                "the merged rule has every class");

            // Remove.
            Send(1, k + ".row", Click);
            Check(ReferenceEquals(panel.Selected, r), "picked again");
            Send(1, k + ".remove", Click);
            Check(!c.Data.Rules.Contains(r) && panel.Selected == null, "Remove");

            // Settings and buttons.
            Send(6, "settings", new byte[] { 1 });
            Send(3, "settle", BitConverter.GetBytes(12));
            Send(3, "gap", BitConverter.GetBytes(50_000));
            Send(2, "combat", new byte[] { 1 });
            Check(c.Data.Settings.SettleSec == 12 && c.Data.Settings.GapMs == SortSettings.GapMax && c.Data.Settings.SortInCombat, "sliders (clamped) and the fights box");
            Send(2, "order", new byte[] { 0 });
            Send(2, "orderkind", new byte[] { 1 });
            Check(!c.Data.Settings.OrderInPacks && c.Data.Settings.OrderKindFirst, "the order boxes");
            Send(2, "auto", new byte[] { 0 });
            Check(!c.Data.Settings.Auto, "auto-sort box");
            Send(1, "job.all", Click);
            Eq(c.Job, SortScope.All, "Sort now button");
            Send(1, "job.unpack", Click);
            Check(c.Job == SortScope.Unpack && c.UnpackWhich == null, "Empty side packs button");
        }
    }
}
