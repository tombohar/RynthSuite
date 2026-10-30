using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // RynthSort v3: rules with several classes and names (and their migration), the order inside
    // packs, the side packs' top-to-bottom order, stacks joined in place, and that auto-sort runs out
    // of moves on a fixed inventory instead of moving items back and forth.
    internal static partial class Program
    {
        // ── Rules with several entries ──────────────────────────────────────

        private static void MultiRules()
        {
            var armor = It(1, "Gold Breastplate", ObjectClasses.Armor);
            var shirt = It(2, "Flared Shirt", ObjectClasses.Clothing);
            var sword = It(3, "Tachi", ObjectClasses.MeleeWeapon);
            var shield = It(4, "Kite Shield", ObjectClasses.Armor);
            var opal = It(5, "Black Opal", ObjectClasses.Gem);
            var gear = SortRule.For(4, new[] { ObjectClasses.Armor, ObjectClasses.Clothing, ObjectClasses.MeleeWeapon });
            Check(SortRules.Matches(gear, armor) && SortRules.Matches(gear, shirt) && SortRules.Matches(gear, sword), "one rule, three classes: any of them matches");
            Check(!SortRules.Matches(gear, opal), "not the others");
            var mixed = SortRule.For(5, new[] { ObjectClasses.Gem }, "Shield", "Pyreal");
            Check(SortRules.Matches(mixed, shield) && SortRules.Matches(mixed, opal), "classes and names in one rule: either matches");
            var rules = new List<SortRule> { gear, mixed };
            Eq(SortRules.RuleFor(rules, shield)?.Pack, 5, "the shield's name beats the armor class");
            Eq(SortRules.RuleFor(rules, armor)?.Pack, 4, "other armor by class");

            Check(!gear.AddClass(ObjectClasses.Armor) && gear.Classes.Count == 3, "a class is in a rule once");
            Check(mixed.AddName("  Mote ") && mixed.HasName("mote") && !mixed.AddName("MOTE") && !mixed.AddName("   "), "names trimmed, once in any case, never blank");
            Check(mixed.RemoveName("PYREAL") && !mixed.HasName("Pyreal"), "removed in any case");
            Check(SortRule.Clean(new string('x', 80)).Length == SortRule.MaxNameLength, "names cut to 64");
            Eq(SortRules.Describe(gear), "Armor, Clothing, Melee Weapon", "described A-Z");
            Eq(SortRules.Describe(mixed), "Gem, name has \"Mote\", name has \"Shield\"", "classes first, then names, A-Z");
            Eq(SortRules.Describe(new SortRule()), "(empty: add a class or a name)", "an empty rule");
            Check(!SortRules.Matches(new SortRule(), opal), "an empty rule matches nothing");

            Check(gear.SameAs(SortRule.For(4, new[] { ObjectClasses.MeleeWeapon, ObjectClasses.Armor, ObjectClasses.Clothing })), "same entries in another order: the same rule");
            Check(!gear.SameAs(SortRule.For(5, new[] { ObjectClasses.MeleeWeapon, ObjectClasses.Armor, ObjectClasses.Clothing })), "another pack: not the same");
            SortRule copy = mixed.Clone();
            copy.AddClass(ObjectClasses.Key);
            Check(!mixed.HasClass(ObjectClasses.Key) && copy.Uid != mixed.Uid, "a clone is its own rule");

            var list = new List<SortRule> { SortRule.ForName("Zircon", 1), new SortRule { Pack = 1 }, SortRule.ForClass(ObjectClasses.Armor, 1), SortRule.ForClass(ObjectClasses.Gem, 1) };
            list.Sort(SortRules.CompareForList);
            Check(SortRules.Describe(list[0]) == "Armor" && SortRules.Describe(list[1]) == "Gem" && list[2].HasName("Zircon") && list[3].IsEmpty, "listed A-Z under a pack, empty last");

            Eq(SortRules.Defaults().Count, 3, "defaults: components, salvage, and gems/stones/keys as one rule");
            Check(SortRules.Defaults()[2].Classes.Count == 3, "the third default rule holds three classes");
        }

        private static void RuleMigration()
        {
            SortStore st = TempStore("migration");
            string own = st.PathFor("S", "Old");
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            // A version 2 file (2026-10-11): one class or name per rule, ids, the fix done.
            File.WriteAllText(own, "{\"version\":2,\"rules\":["
                + "{\"on\":true,\"kind\":\"class\",\"class\":12,\"text\":\"\",\"pack\":1,\"packId\":1073741825,\"packNote\":\"Sack - Components\"},"
                + "{\"on\":false,\"kind\":\"name\",\"class\":12,\"text\":\"Ink of\",\"pack\":2,\"packId\":1073741826},"
                + "{\"on\":true,\"kind\":\"name\",\"class\":12,\"text\":\"\",\"pack\":3}],"
                + "\"settings\":{\"auto\":true},\"packsFixed\":true}");
            CharacterSortData d = st.Load("S", "Old", out string? problem);
            Check(problem == null && d.Rules.Count == 3 && d.PacksFixed, "version 2 loads");
            Check(d.Rules[0].Classes.SequenceEqual(new[] { ObjectClasses.SpellComponent }) && d.Rules[0].Names.Count == 0
                  && d.Rules[0].PackId == 0x40000001 && d.Rules[0].PackNote == "Sack - Components", "a class rule: one class, its pack kept");
            Check(d.Rules[1].Names.SequenceEqual(new[] { "Ink of" }) && d.Rules[1].Classes.Count == 0 && !d.Rules[1].On && d.Rules[1].PackId == 0x40000002,
                "a name rule: one name (not its unused class), off stays off");
            Check(d.Rules[2].IsEmpty, "a blank name rule matched nothing and still doesn't");
            Check(d.Settings.OrderInPacks && !d.Settings.OrderKindFirst, "the order settings at their defaults");

            // Saved as version 3, with the old fields too (an older RynthSort reads the first entry).
            d.Rules[0].AddClass(ObjectClasses.Salvage);
            d.Rules[0].AddName("Taper");
            Check(st.Save("S", "Old", d) == null, "saved");
            string json = File.ReadAllText(own);
            Check(json.Contains("\"version\": 3") && json.Contains("\"classes\"") && json.Contains("\"names\""), "version 3 with lists");
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                var r0 = doc.RootElement.GetProperty("rules")[0];
                Check(r0.GetProperty("kind").GetString() == "class" && r0.GetProperty("class").GetInt32() == ObjectClasses.SpellComponent, "old fields: the first class");
                var r2 = doc.RootElement.GetProperty("rules")[2];
                Check(r2.GetProperty("kind").GetString() == "name" && r2.GetProperty("text").GetString() == "", "an empty rule reads as a blank name rule to an older RynthSort");
                var r1 = doc.RootElement.GetProperty("rules")[1];
                Check(r1.GetProperty("kind").GetString() == "name" && r1.GetProperty("text").GetString() == "Ink of", "a name-only rule: its name the old way");
            }
            CharacterSortData back = st.Load("S", "Old", out problem);
            Check(problem == null && back.Rules[0].Classes.Count == 2 && back.Rules[0].HasClass(ObjectClasses.Salvage) && back.Rules[0].HasName("taper"), "version 3 reads back whole");

            // Odd lists: non-numbers and bad strings skipped, duplicates once, the old fields ignored when lists are there.
            File.WriteAllText(own, "{\"version\":3,\"rules\":[{\"classes\":[11,\"x\",11,-3,13.0],\"names\":[\"Opal\",5,\"opal\",\" \"],\"kind\":\"class\",\"class\":39,\"pack\":2}]}");
            CharacterSortData odd = st.Load("S", "Old", out _);
            Check(odd.Rules[0].Classes.SequenceEqual(new[] { ObjectClasses.Gem, ObjectClasses.Key }) && odd.Rules[0].Names.SequenceEqual(new[] { "Opal" }), "lists cleaned; old fields ignored");

            // The Lua script's rules read the same way (one entry each) and still compare as its own.
            SortStore imp = TempStore("migration-lua");
            string lua = imp.LuaPathFor("DreamWeave", "Drakkon");
            Directory.CreateDirectory(Path.GetDirectoryName(lua)!);
            File.WriteAllText(lua, LuaFile);
            CharacterSortData fresh = imp.Load("DreamWeave", "Drakkon", out _);
            Check(fresh.Rules.All(x => x.Classes.Count == 1 && x.Names.Count == 0) && fresh.Rules[0].SameAs(fresh.LuaRules![0]), "Lua rules: one class each, SameAs their file");
        }

        // ── Order inside a pack ─────────────────────────────────────────────

        private static List<SortItem> Items(params string[] names)
        {
            var list = new List<SortItem>();
            for (int i = 0; i < names.Length; i++) list.Add(It(100 + (uint)i, names[i], ObjectClasses.Misc));
            return list;
        }

        private static string Names(IEnumerable<SortItem> items) => string.Join(",", items.Select(i => i.Name));

        /// <summary>Runs NextMove (every item movable) until it stops; the moves it took.</summary>
        private static int SortByMoves(List<SortItem> items, bool kindFirst, Dictionary<uint, int>? perItem = null, int limit = 1000)
        {
            int moves = 0;
            while (moves < limit && PackOrder.NextMove(items, kindFirst, _ => true, out SortItem it, out int at))
            {
                PackOrder.Apply(items, it, at);
                moves++;
                if (perItem != null) perItem[it.Id] = perItem.TryGetValue(it.Id, out int n) ? n + 1 : 1;
            }
            return moves;
        }

        private static bool InOrder(IReadOnlyList<SortItem> items, bool kindFirst)
        {
            for (int i = 1; i < items.Count; i++)
                if (PackOrder.Compare(items[i - 1], items[i], kindFirst) > 0) return false;
            return true;
        }

        private static void InPackOrder()
        {
            // The order: A-Z any case; like names together; then the stack kind; then the id.
            var a = It(5, "black opal", ObjectClasses.Gem, wcid: 2);
            var b = It(4, "Black Opal", ObjectClasses.Gem, wcid: 2);
            var c2 = It(3, "Black Opal", ObjectClasses.Gem, wcid: 1);
            var d = It(9, "Amber", ObjectClasses.Misc);
            var list = new List<SortItem> { a, b, c2, d };
            list.Sort((x, y) => PackOrder.Compare(x, y, false));
            Eq(string.Join(",", list.Select(i => i.Id)), "9,3,4,5", "Amber; then the Black Opals: same name together, by kind, by id");
            var armor = It(20, "Zircon Plate", ObjectClasses.Armor);
            var gem = It(21, "Agate", ObjectClasses.Gem);
            Check(PackOrder.Compare(armor, gem, false) > 0 && PackOrder.Compare(armor, gem, true) < 0, "kind first: Armor before Gem whatever the names");

            // One item that arrived at the end: one move puts it in its place.
            var items = Items("Amber", "Bone", "Coal", "Dust", "Agate");
            Check(PackOrder.NextMove(items, false, _ => true, out SortItem it, out int at) && it.Name == "Agate" && at == 0, "Agate to slot 0");
            PackOrder.Apply(items, it, at);
            Check(InOrder(items, false) && !PackOrder.NextMove(items, false, _ => true, out _, out _), "then in order: no move");

            // One item at the top that belongs at the bottom: one move, not four.
            items = Items("Zinc", "Amber", "Bone", "Coal", "Dust");
            Eq(SortByMoves(items, false), 1, "Zinc to the end in one move");
            Eq(Names(items), "Amber,Bone,Coal,Dust,Zinc", "in order");

            // Reversed: n - 1 moves.
            items = Items("E", "D", "C", "B", "A");
            Eq(SortByMoves(items, false), 4, "reversed five: four moves");
            Check(InOrder(items, false), "in order");

            // A move "counted without it": moving down past others.
            items = Items("B", "A", "C");
            Check(PackOrder.NextMove(items, false, _ => true, out it, out at), "a move");
            PackOrder.Apply(items, it, at);
            Eq(Names(items), "A,B,C", $"{it.Name} to {at}");

            // Where an arriving item goes.
            items = Items("Amber", "Coal", "Dust");
            Eq(PackOrder.InsertAt(items, It(1, "Bone", ObjectClasses.Misc), false), 1, "Bone between Amber and Coal");
            Eq(PackOrder.InsertAt(items, It(1, "Zinc", ObjectClasses.Misc), false), 3, "Zinc at the end");
            Eq(PackOrder.InsertAt(items, It(1, "Aaa", ObjectClasses.Misc), false), 0, "Aaa first");
            Eq(PackOrder.InsertAt(new List<SortItem>(), It(1, "X", ObjectClasses.Misc), false), 0, "an empty pack");

            // An item that can't move now isn't picked; another is.
            items = Items("C", "B", "A");
            Check(PackOrder.NextMove(items, false, x => x.Name != "A", out it, out _) && it.Name != "A", "a blocked item is skipped");
            Check(!PackOrder.NextMove(Items("B", "A"), false, x => false, out _, out _), "nothing movable: no move");
            Eq(PackOrder.OutOfPlace(Items("Zinc", "Amber", "Bone"), false), 1, "one out of place");
            Eq(PackOrder.OutOfPlace(Items("Amber", "Bone"), false), 0, "in order");
            Check(!PackOrder.AllNamed(new List<SortItem> { It(1, "", ObjectClasses.Misc) }), "an unnamed item: wait for its name");

            // Every order of up to 7 items: in order after at most n - (longest run) moves, each item moved at most twice.
            int worstPerItem = 0;
            for (int n = 1; n <= 7; n++)
            {
                foreach (int[] perm in Permutations(n))
                {
                    var p = perm.Select(i => It(100 + (uint)i, ((char)('A' + i)).ToString(), ObjectClasses.Misc)).ToList();
                    bool[] kept = PackOrder.Kept(PackOrder.Ranks(p, false));
                    int bound = kept.Count(x => !x);
                    var per = new Dictionary<uint, int>();
                    int moves = SortByMoves(p, false, per);
                    if (!InOrder(p, false) || moves > bound) { Check(false, $"order {string.Join("", perm)}: {moves} moves (bound {bound}), in order {InOrder(p, false)}"); goto done; }
                    worstPerItem = Math.Max(worstPerItem, per.Values.DefaultIfEmpty(0).Max());
                }
            }
            Check(true, "every order of 1-7 items sorts within n - (longest run) moves");
            done:
            Check(worstPerItem <= 2, $"no item moved more than twice ({worstPerItem})");

            // Bigger packs, shuffled the same way every run.
            var rnd = new Random(1234);
            for (int round = 0; round < 40; round++)
            {
                int n = 10 + rnd.Next(93);
                var p = Enumerable.Range(0, n).Select(i => It(1000 + (uint)i, "Item " + rnd.Next(60).ToString("D2"), ObjectClasses.Misc, wcid: (uint)rnd.Next(3))).OrderBy(_ => rnd.Next()).ToList();
                int bound = PackOrder.OutOfPlace(p, round % 2 == 0);
                var per = new Dictionary<uint, int>();
                int moves = SortByMoves(p, round % 2 == 0, per);
                if (!InOrder(p, round % 2 == 0) || moves > bound || per.Values.DefaultIfEmpty(0).Max() >= SortPlanner.MaxReorders)
                {
                    Check(false, $"round {round}: {n} items, {moves} moves (bound {bound}), most per item {per.Values.DefaultIfEmpty(0).Max()}");
                    break;
                }
            }
            Check(true, "40 shuffled packs of 10-102 items sort within the bound, under the per-item backstop");
        }

        private static IEnumerable<int[]> Permutations(int n)
        {
            var a = Enumerable.Range(0, n).ToArray();
            yield return (int[])a.Clone();
            while (true)
            {
                int i = n - 2;
                while (i >= 0 && a[i] >= a[i + 1]) i--;
                if (i < 0) yield break;
                int j = n - 1;
                while (a[j] <= a[i]) j--;
                (a[i], a[j]) = (a[j], a[i]);
                Array.Reverse(a, i + 1, n - i - 1);
                yield return (int[])a.Clone();
            }
        }

        private static void SlotOrders()
        {
            var pack = new SortPack { Id = 0x40000001 };
            pack.Items.AddRange(Items("A", "B", "C"));   // ids 100, 101, 102 (id order)
            Check(pack.ApplyOrder(new uint[] { 102, 100, 101 }, 3) && pack.OrderKnown, "a list naming exactly the items read");
            Eq(string.Join(",", pack.Items.Select(i => i.Id)), "102,100,101", "items in slot order");
            Check(!pack.ApplyOrder(new uint[] { 102, 100 }, 2) && !pack.OrderKnown, "one short (a move behind): not known");
            Check(!pack.ApplyOrder(new uint[] { 102, 100, 999 }, 3) && !pack.OrderKnown, "an id not read: not known");
            Check(!pack.ApplyOrder(new uint[] { 102, 102, 100 }, 3) && !pack.OrderKnown, "an id twice: not known");
            Eq(pack.Items.Count, 3, "a refused list leaves the items");
            Check(pack.ApplyOrder(new uint[] { 100, 101, 102, 7 }, 3), "only the count given is read");

            // The side packs, top first; foci in the list are fine.
            SortInventory inv = Inv(3);   // ids ...01, 02, 03 (id order)
            Check(inv.ApplyPackOrder(new uint[] { 0x40000003, 0x40000F01, 0x40000001, 0x40000002 }, 4) && inv.PackOrderKnown, "pack column order");
            Eq(string.Join(",", inv.Packs.Select(p => p.Id & 0xF)), "3,1,2", "pack 1 is the top pack");
            SortInventory inv2 = Inv(3);
            Check(!inv2.ApplyPackOrder(new uint[] { 0x40000003, 0x40000001 }, 2) && !inv2.PackOrderKnown, "a pack missing from the list: not known");
            Eq(string.Join(",", inv2.Packs.Select(p => p.Id & 0xF)), "1,2,3", "id order kept");

            // A rule's number follows its pack to its new place; the Lua fix still reads id order.
            var rule = SortRule.ForClass(ObjectClasses.Gem, 1);
            rule.PackId = 0x40000003;
            Check(PackFix.Sync(new List<SortRule> { rule }, inv) && rule.Pack == 1, "the top pack is now pack 1");
        }

        // ── The planner keeping every pack tidy ─────────────────────────────

        private static void PlanInPack()
        {
            var rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) };

            // A route into a pack whose order is known names the item's slot there.
            SortInventory inv = Inv(1);
            inv.Packs[0].Items.AddRange(new[] { It(50, "Agate", ObjectClasses.Gem), It(51, "Opal", ObjectClasses.Gem) });
            inv.Packs[0].OrderKnown = true;
            inv.Main.Items.Add(It(10, "Jet", ObjectClasses.Gem));
            List<SortMove> m = new SortPlanner().Plan(inv, rules, SortScope.All, true, 0, 0, PlanOptions.Full);
            Check(m.Count == 1 && m[0].Kind == MoveKind.Route && m[0].Placement == 1, "Jet goes between Agate and Opal");
            inv.Packs[0].OrderKnown = false;
            m = new SortPlanner().Plan(inv, rules, SortScope.All, true, 0, 0, PlanOptions.Full);
            Check(m.Count == 1 && m[0].Placement == -1, "order unknown: the first empty slot");
            m = new SortPlanner().Plan(inv, rules, SortScope.All, true, 0, 0);
            Check(m.Count == 1 && m[0].Placement == -1, "without the options: as before");

            // Nothing to route: stacks first, then order.
            inv = Inv(1);
            inv.Packs[0].OrderKnown = true;
            inv.Packs[0].Items.AddRange(new[]
            {
                It(60, "Opal", ObjectClasses.Gem, wcid: 7, stack: 3, max: 10),
                It(61, "Agate", ObjectClasses.Gem),
                It(62, "Opal", ObjectClasses.Gem, wcid: 7, stack: 5, max: 10),
            });
            var p = new SortPlanner();
            m = p.Plan(inv, rules, SortScope.All, false, 0, 5000, PlanOptions.Full);
            Check(m.Count >= 1 && m[0].Kind == MoveKind.Stack && m[0].Item.Id == 60 && m[0].MergeInto == 62 && m[0].Dest.Id == inv.Packs[0].Id,
                "the smaller Opal stack joins the larger, in its pack");
            Check(m.Count == 1, "one tidy move per pack per pass");
            inv.Packs[0].Items.RemoveAt(0);
            inv.Packs[0].Items[1].Stack = 8;
            m = p.Plan(inv, rules, SortScope.All, false, 0, 5000, PlanOptions.Full);
            Check(m.Count == 0, "Agate, Opal: in order, one stack: nothing");
            inv.Packs[0].Items.Reverse();
            m = p.Plan(inv, rules, SortScope.All, false, 0, 5000, PlanOptions.Full);
            Check(m.Count == 1 && m[0].Kind == MoveKind.Reorder && m[0].InPlace && m[0].Placement >= 0, "out of order: a reorder in place");
            Eq(p.View.Packs[0].OutOfPlace, 1, "the view counts it");
            Check(p.View.Ordering && p.View.Packs[0].OrderKnown, "the view knows ordering is on");

            // Not without the options, not for Main or Unpack, not for an unknown order, not with an unnamed item.
            Eq(p.Plan(inv, rules, SortScope.All, false, 0, 5000).Count, 0, "no options: no tidying");
            Eq(p.Plan(inv, rules, SortScope.Main, false, 0, 5000, PlanOptions.Full).Count, 0, "Main: routes only");
            inv.Packs[0].OrderKnown = false;
            Eq(p.Plan(inv, rules, SortScope.All, false, 0, 5000, PlanOptions.Full).Count, 0, "order unknown: not reordered");
            inv.Packs[0].OrderKnown = true;
            inv.Packs[0].Items.Add(It(70, "", ObjectClasses.Gem));
            Eq(p.Plan(inv, rules, SortScope.All, false, 0, 5000, PlanOptions.Full).Count, 0, "an unnamed item: wait");
            inv.Packs[0].Items.RemoveAt(2);
            Eq(p.Plan(inv, rules, SortScope.All, false, 0, 5000, new PlanOptions { Order = false, Merge = true }).Count, 0, "ordering off: no reorder");

            // A route waits for nothing in place: routes first.
            inv.Main.Items.Add(It(11, "Zircon", ObjectClasses.Gem));
            m = p.Plan(inv, rules, SortScope.All, true, 0, 0, PlanOptions.Full);
            Check(m.Count == 1 && m[0].Kind == MoveKind.Route, "a route goes before any tidying");

            // The main pack is put in order only once nothing in it is new.
            inv = Inv(0);
            inv.Main.OrderKnown = true;
            inv.Main.Items.AddRange(new[] { It(80, "Zinc", ObjectClasses.Misc), It(81, "Amber", ObjectClasses.Misc) });
            p = new SortPlanner();
            Eq(p.Plan(inv, rules, SortScope.All, false, 0, 5000, PlanOptions.Full).Count, 0, "new items settling in the main pack: not yet");
            m = p.Plan(inv, rules, SortScope.All, false, 5000, 5000, PlanOptions.Full);
            Check(m.Count == 1 && m[0].Kind == MoveKind.Reorder && m[0].DestIndex == 0, "settled: the main pack is put in order");
            Check(m[0].Done == "put in order in main pack", "logged as such");

            // The backstop: an item put in order too often lately isn't again until the window passes.
            for (int i = 0; i < SortPlanner.MaxReorders; i++) p.NoteReorder(80, 6000 + i);
            Check(p.ReorderLimited(80, 7000), "limited after too many");
            m = p.Plan(inv, rules, SortScope.All, false, 7000, 5000, PlanOptions.Full);
            Check(m.All(x => x.Item.Id != 80), "not moved again");
            Check(!p.ReorderLimited(80, 6000 + SortPlanner.ReorderWindowMs), "the window passes");
        }

        private static void MoveAnswersInPlace()
        {
            var item = It(10, "Opal", ObjectClasses.Gem, stack: 5);
            var pack = new SortPack { Id = 0x40000001 };
            var reorder = new MoveInFlight { Move = new SortMove(item, pack, 1, 0, 2, MoveKind.Reorder), SentMs = 1000, ErrorSeqAtSend = 7, StackAtSend = 5 };
            Eq(MoveTracker.Observe(reorder, new MoveObservation { Exists = true, Container = pack.Id, Slot = 0, ErrorSeq = 7 }, 1500), MoveOutcome.Pending,
                "in its pack but not at its slot yet: still waiting (its container says nothing)");
            Eq(MoveTracker.Observe(reorder, new MoveObservation { Exists = true, Container = pack.Id, Slot = 2, ErrorSeq = 7 }, 1500), MoveOutcome.Done, "at its slot");
            Eq(MoveTracker.Observe(reorder, new MoveObservation { Exists = true, Container = Me, Slot = 0, ErrorSeq = 7 }, 1500), MoveOutcome.Done, "moved away by someone: stop waiting");
            Eq(MoveTracker.Observe(reorder, new MoveObservation { Exists = true, Container = pack.Id, Slot = -1, ErrorSeq = 7 }, 1000 + MoveTracker.TimeoutMs), MoveOutcome.TimedOut, "no answer");
            Eq(MoveTracker.Observe(reorder, new MoveObservation { Exists = true, Container = pack.Id, Slot = 0, ErrorSeq = 8, ErrorEventType = 0xA0, ErrorObject = 10 }, 1500),
                MoveOutcome.Refused, "refused");

            var join = new MoveInFlight { Move = new SortMove(item, pack, 1, 50, -1, MoveKind.Stack), SentMs = 1000, ErrorSeqAtSend = 7, StackAtSend = 5 };
            Eq(MoveTracker.Observe(join, new MoveObservation { Exists = true, Container = pack.Id, Stack = 5, ErrorSeq = 7 }, 1500), MoveOutcome.Pending, "a join: still whole");
            Eq(MoveTracker.Observe(join, new MoveObservation { Exists = true, Container = pack.Id, Stack = 2, ErrorSeq = 7 }, 1500), MoveOutcome.Done, "part of it joined");
            Eq(MoveTracker.Observe(join, new MoveObservation { Exists = false }, 1500), MoveOutcome.Done, "all of it joined");
        }

        // ── Convergence ─────────────────────────────────────────────────────

        /// <summary>A messy character: loot in the main pack, items in the wrong packs, packs out of order, split stacks.</summary>
        private static SortInventory Messy()
        {
            SortInventory inv = Inv(3, cap: 24);
            foreach (SortPack p in inv.Packs) p.OrderKnown = true;
            inv.Main.OrderKnown = true;
            uint id = 1000;
            SortItem Add(SortPack p, string name, int cls, uint wcid = 0, int stack = 1, int max = 1)
            {
                var it = It(id++, name, cls, wcid, stack, max);
                p.Items.Add(it);
                return it;
            }
            // Main pack: loot of every kind, and things no rule wants.
            Add(inv.Main, "Lead Scarab", ObjectClasses.SpellComponent, 691, 3, 100);
            Add(inv.Main, "Salvaged Iron", ObjectClasses.Salvage);
            Add(inv.Main, "Black Opal", ObjectClasses.Gem, 2401, 1, 1);
            Add(inv.Main, "Zircon Plate", ObjectClasses.Armor);
            Add(inv.Main, "Ashcoal", ObjectClasses.Misc);
            Add(inv.Main, "Healing Kit", ObjectClasses.HealingKit, 628, 1, 1);
            Add(inv.Main, "Prismatic Taper", ObjectClasses.SpellComponent, 20631, 40, 100);
            Add(inv.Main, "Amber Ring", ObjectClasses.Jewelry);
            // Pack 1 (components): out of order, split stacks, a gem that belongs in pack 3.
            Add(inv.Packs[0], "Prismatic Taper", ObjectClasses.SpellComponent, 20631, 70, 100);
            Add(inv.Packs[0], "Hyssop", ObjectClasses.SpellComponent, 650, 5, 100);
            Add(inv.Packs[0], "Agate", ObjectClasses.Gem, 2394, 1, 1);
            Add(inv.Packs[0], "Prismatic Taper", ObjectClasses.SpellComponent, 20631, 90, 100);
            Add(inv.Packs[0], "Copper Scarab", ObjectClasses.SpellComponent, 690, 1, 100);
            Add(inv.Packs[0], "Lead Scarab", ObjectClasses.SpellComponent, 691, 99, 100);
            Add(inv.Packs[0], "Birch", ObjectClasses.SpellComponent, 660, 10, 100);
            // Pack 2 (salvage): reversed.
            foreach (string s in new[] { "Salvaged Steel", "Salvaged Granite", "Salvaged Gold", "Salvaged Ebony", "Salvaged Copper" })
                Add(inv.Packs[1], s, ObjectClasses.Salvage);
            // Pack 3 (gems, stones, keys): a component that belongs in pack 1, and stray misc.
            Add(inv.Packs[2], "Mana Stone", ObjectClasses.ManaStone);
            Add(inv.Packs[2], "Legendary Key", ObjectClasses.Key);
            Add(inv.Packs[2], "Gold Scarab", ObjectClasses.SpellComponent, 693, 2, 100);
            Add(inv.Packs[2], "Pyreal Mote", ObjectClasses.Misc);
            Add(inv.Packs[2], "Diamond", ObjectClasses.Gem);
            return inv;
        }

        private static void Converges()
        {
            foreach (bool kindFirst in new[] { false, true })
            foreach (SortScope scope in new[] { SortScope.All, SortScope.Clean })
            {
                string what = $"{scope}{(kindFirst ? ", kind first" : "")}";
                SortInventory inv = Messy();
                List<SortRule> rules = SortRules.Defaults();
                var opt = PlanOptions.Full;
                opt.KindFirst = kindFirst;
                var planner = new SortPlanner();
                var seen = new HashSet<string>();
                int passes = 0, moved = 0;
                bool repeated = false;
                // The planner on the same inventory, each pass's first move done as the server does it.
                for (; passes < 500; passes++)
                {
                    List<SortMove> moves = planner.Plan(inv, rules, scope, false, 10_000 + passes, 0, opt);
                    if (moves.Count == 0) break;
                    SortMove m = moves[0];
                    string state = Snapshot(inv) + "|" + m.Item.Id + ">" + m.Dest.Id + "@" + m.Placement + "+" + m.MergeInto;
                    if (!seen.Add(state)) repeated = true;
                    ServerApply(inv, m);
                    if (m.Kind == MoveKind.Reorder) planner.NoteReorder(m.Item.Id, 10_000 + passes);
                    moved++;
                }
                Console.WriteLine($"         {what}: {moved} moves to tidy the messy inventory");
                Check(passes < 500, $"{what}: runs out of moves ({moved} moves)");
                Check(!repeated, $"{what}: never the same move from the same state twice");
                Check(moved <= 60, $"{what}: in a sensible number of moves ({moved})");
                for (int i = 0; i < 50; i++)
                    if (planner.Plan(inv, rules, scope, false, 20_000 + i * 1000, 0, opt).Count != 0) { Check(false, $"{what}: stays still afterwards"); break; }
                Check(true, $"{what}: 50 more passes, no move");

                // And it is tidy: every item in its pack, packs in order, one stack with room per kind.
                foreach (SortItem it in inv.Main.Items.Concat(inv.Packs.SelectMany(p => p.Items)))
                {
                    SortRule? r = SortRules.RuleFor(rules, it);
                    SortPack where = inv.Packs.Prepend(inv.Main).First(p => p.Items.Contains(it));
                    if (r != null) Check(where.Id == inv.Packs[r.Pack - 1].Id, $"{what}: {it.Name} in its rule's pack");
                    else if (scope == SortScope.Clean) Check(where == inv.Main, $"{what}: {it.Name} (no rule) back in the main pack");
                }
                foreach (SortPack pk in inv.Packs.Prepend(inv.Main))
                {
                    Check(InOrder(pk.Items, kindFirst), $"{what}: {pk.Name} in order ({Names(pk.Items)})");
                    Check(!SortPlanner.StackJoin(pk, _ => true, out _, out _), $"{what}: {pk.Name} has no two stacks to join");
                }
                Eq(inv.Packs[0].Items.Count(i => i.Name == "Prismatic Taper"), 2, $"{what}: tapers 200 = 100 + 100");
            }
        }

        private static string Snapshot(SortInventory inv) =>
            string.Join(";", inv.Packs.Prepend(inv.Main).Select(p => string.Join(",", p.Items.Select(i => i.Id + "x" + i.Stack))));

        private static void ConvergesInTheLoop()
        {
            // The controller with auto-sort on, against a pretend server that does what ACE does.
            var w = new FakeWorld { Inv = Messy(), Can = SortAbilities.All };
            SortController c = Controller(auto: true, gapMs: 300, settleSec: 1);
            Ticks(c, w, 0, 120_000);
            int sent = w.Sent.Count;
            if (Environment.GetEnvironmentVariable("RSORT_DEBUG") == "1") foreach (SortMove dm in w.Sent) Console.WriteLine($"           {dm.Kind} {dm.Item.Name} x{dm.Item.Stack} -> {dm.DestLabel} @{dm.Placement} +{dm.MergeInto}");
            Console.WriteLine($"         auto-sort: {sent} moves ({w.Sent.Count(m => m.Kind == MoveKind.Route)} to packs, {w.Sent.Count(m => m.Kind == MoveKind.Stack)} stacks joined, {w.Sent.Count(m => m.Kind == MoveKind.Reorder)} reorders)");
            Check(sent > 20 && sent < 80, $"auto-sort tidied everything ({sent} moves)");
            Eq(c.Status, "watching", "then watching");
            Eq(c.Failed, 0, "no move failed or timed out on the way");
            Check(c.Log.Any(l => l.Contains("put in order")), $"reorders logged ({string.Join(" / ", c.Log)})");
            Ticks(c, w, 120_000, 600_000);
            Eq(w.Sent.Count, sent, "eight minutes more: not one move (nothing moves back and forth)");
            foreach (SortPack pk in w.Inv.Packs.Prepend(w.Inv.Main)) Check(InOrder(pk.Items, false), $"{pk.Name} in order");

            // New loot: routed into its place after the settle time, nothing else stirs.
            w.Inv.Main.Items.Add(It(5000, "Cobalt Scarab", ObjectClasses.SpellComponent, 6000, 1, 100));
            Ticks(c, w, 600_000, 700_000);
            Eq(w.Sent.Count, sent + 1, "one new item: one move");
            SortMove last = w.Sent[^1];
            Check(last.Kind == MoveKind.Route && last.Placement >= 0 && last.Dest.Id == w.Inv.Packs[0].Id, "into pack 1 at its slot");
            Check(InOrder(w.Inv.Packs[0].Items, false), "pack 1 still in order (it landed in its place)");

            // An engine before API v80: items still go to their packs; nothing is reordered.
            var old = new FakeWorld { Inv = Messy(), Can = new SortAbilities { Merge = true } };
            SortController c2 = Controller(auto: true, settleSec: 0);
            Ticks(c2, old, 0, 120_000);
            Check(old.Sent.All(m => m.Kind != MoveKind.Reorder && m.Placement == -1), "no slots, no reorders");
            Check(old.Inv.Packs[2].Items.All(i => i.ObjectClass != ObjectClasses.SpellComponent), "but the misplaced component left pack 3");
            int oldSent = old.Sent.Count;
            Ticks(c2, old, 120_000, 300_000);
            Eq(old.Sent.Count, oldSent, "and it stays still too");
        }
    }
}
