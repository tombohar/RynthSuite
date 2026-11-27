using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // RynthSort 0.6.0: "Keep handy items at the front of the main pack". Healing kits, mana stones,
    // food, potions and elixirs (the player's list) sit in the main pack's first slots. A move to a
    // slot is the engine's PutItemInContainer (plugin API v80), the same move that puts items in
    // order inside packs; without it the option does nothing. Items left alone are never moved for it.
    internal static partial class Program
    {
        private static SortInventory HandyPack(params (string Name, int Cls)[] items)
        {
            SortInventory inv = Inv(2);
            inv.Main.OrderKnown = true;
            uint id = 1;
            foreach (var (n, cls) in items) inv.Main.Items.Add(It(id++, n, cls));
            return inv;
        }

        private static string Names(SortPack p) => string.Join(",", p.Items.Select(i => i.Name));

        private static Func<SortItem, bool> DefaultHandy() =>
            it => HandyItems.Matches(HandyItems.DefaultClasses, HandyItems.DefaultNames, it);

        /// <summary>Plans and applies moves as the server would until none is left; returns how many.</summary>
        private static int RunOut(SortInventory inv, List<SortRule> rules, PlanOptions opt, Func<uint, bool>? held = null, int max = 200)
        {
            var planner = new SortPlanner { Held = held };
            int n = 0;
            for (; n < max; n++)
            {
                List<SortMove> moves = planner.Plan(inv, rules, SortScope.All, force: true, 0, 0, opt);
                if (moves.Count == 0) return n;
                ServerApply(inv, moves[0]);
            }
            return n;
        }

        private static void HandyFront()
        {
            Func<SortItem, bool> handy = DefaultHandy();
            Check(handy(It(1, "Treated Healing Kit", ObjectClasses.HealingKit)) && handy(It(2, "Mana Stone", ObjectClasses.ManaStone))
                  && handy(It(3, "Peppermint Stick", ObjectClasses.Food)), "kits, mana stones and food by kind");
            Check(handy(It(4, "Mana Potion", ObjectClasses.Misc)) && handy(It(5, "health elixir", ObjectClasses.Unknown)), "potions and elixirs by name, any case");
            Check(!handy(It(6, "Black Opal", ObjectClasses.Gem)) && !handy(It(7, "", ObjectClasses.Misc)), "gems aren't handy; a nameless misc item isn't either");
            Eq(HandyItems.Describe(HandyItems.DefaultClasses, HandyItems.DefaultNames), "healing kits, mana stones, food, potions and elixirs", "the list in words");

            // The front run, and one move per handy item behind it (ordering off: the rest keep their order).
            SortInventory inv = HandyPack(("Black Opal", ObjectClasses.Gem), ("Amuli Coat", ObjectClasses.Armor), ("Healing Kit", ObjectClasses.HealingKit),
                ("Zircon", ObjectClasses.Gem), ("Mana Potion", ObjectClasses.Misc), ("Celdon Girth", ObjectClasses.Armor));
            Func<SortItem, bool> any = _ => true;
            Eq(PackOrder.FrontRun(inv.Main.Items, handy, any), 0, "nothing handy in front yet");
            Eq(PackOrder.HandyBehind(inv.Main.Items, handy, any), 2, "two handy items behind");
            Check(PackOrder.FrontMove(inv.Main.Items, handy, any, out SortItem first, out int at) && first.Name == "Healing Kit" && at == 0, "the first handy item goes to slot 0");
            var opt = new PlanOptions { Place = true, Merge = true, Handy = handy };
            int moves = RunOut(inv, new List<SortRule>(), opt);
            Eq(Names(inv.Main), "Healing Kit,Mana Potion,Black Opal,Amuli Coat,Zircon,Celdon Girth", "handy in front, the rest as they were");
            Eq(moves, 2, "one move per handy item");

            // Ordering on: the whole main pack in order, handy items first.
            SortInventory ord = HandyPack(("Zircon", ObjectClasses.Gem), ("Mana Potion", ObjectClasses.Misc), ("Amber", ObjectClasses.Gem),
                ("Healing Kit", ObjectClasses.HealingKit), ("Apple", ObjectClasses.Food));
            RunOut(ord, new List<SortRule>(), new PlanOptions { Order = true, Place = true, Merge = true, Handy = handy });
            Eq(Names(ord.Main), "Apple,Healing Kit,Mana Potion,Amber,Zircon", "handy first (A-Z), then the rest A-Z");
            Eq(RunOut(ord, new List<SortRule>(), new PlanOptions { Order = true, Place = true, Merge = true, Handy = handy }), 0, "then it stays still");

            // Left alone: a held item is never moved for it, and handy items go after a held one in front.
            SortInventory hold = HandyPack(("Black Opal", ObjectClasses.Gem), ("Zircon", ObjectClasses.Gem), ("Healing Kit", ObjectClasses.HealingKit),
                ("Mana Stone", ObjectClasses.ManaStone));
            uint opalId = hold.Main.Items[0].Id, stoneId = hold.Main.Items[3].Id;
            RunOut(hold, new List<SortRule>(), opt, held: id => id == opalId || id == stoneId);
            Eq(Names(hold.Main), "Black Opal,Healing Kit,Zircon,Mana Stone", "the opal (held) keeps slot 0; the kit goes after it; the held stone stays");

            // No moves to a slot (an engine before API v80): nothing happens for it.
            SortInventory old = HandyPack(("Black Opal", ObjectClasses.Gem), ("Healing Kit", ObjectClasses.HealingKit));
            Eq(RunOut(old, new List<SortRule>(), new PlanOptions { Merge = true, Handy = handy }), 0, "no slot moves: nothing moves");
            SortInventory unknown = HandyPack(("Black Opal", ObjectClasses.Gem), ("Healing Kit", ObjectClasses.HealingKit));
            unknown.Main.OrderKnown = false;
            Eq(RunOut(unknown, new List<SortRule>(), opt), 0, "the main pack's order not known: nothing moves");

            // A kind a side pack's rule names goes to that pack instead.
            SortInventory ruled = HandyPack(("Black Opal", ObjectClasses.Gem), ("Healing Kit", ObjectClasses.HealingKit), ("Mana Stone", ObjectClasses.ManaStone));
            RunOut(ruled, new List<SortRule> { SortRule.ForClass(ObjectClasses.HealingKit, 2) }, opt);
            Check(ruled.Packs[1].Items.Any(i => i.Name == "Healing Kit") && Names(ruled.Main) == "Mana Stone,Black Opal", $"the kit to its pack, the stone to the front ({Names(ruled.Main)})");

            // An item coming back to the main pack (Clean) lands in the handy run when it is handy.
            SortInventory back = HandyPack(("Healing Kit", ObjectClasses.HealingKit), ("Black Opal", ObjectClasses.Gem));
            back.Packs[0].Items.Add(It(50, "Mana Potion", ObjectClasses.Misc));
            var cp = new SortPlanner();
            List<SortMove> clean = cp.Plan(back, new List<SortRule>(), SortScope.Clean, force: true, 0, 0, opt);
            Check(clean.Count == 1 && clean[0].Item.Id == 50 && clean[0].DestIndex == 0 && clean[0].Placement == 1, "a potion cleaned out of a pack goes right after the kit");

            // New loot settling: the main pack waits (as for ordering).
            SortInventory fresh = HandyPack(("Black Opal", ObjectClasses.Gem), ("Healing Kit", ObjectClasses.HealingKit));
            var fp = new SortPlanner();
            Eq(fp.Plan(fresh, new List<SortRule>(), SortScope.All, force: false, 0, 5000, opt).Count, 0, "items still settling: the main pack isn't touched");
            Eq(fp.Plan(fresh, new List<SortRule>(), SortScope.All, force: false, 6000, 5000, opt).Count, 1, "settled: the kit moves");
            Check(fp.View.Handy && fp.View.HandyBehind == 1, "the view counts what is left");
        }

        private static void HandyLoopAndStore()
        {
            // In the loop: auto-sort brings them to the front, then stays still; off by default.
            var w = new FakeWorld { Can = SortAbilities.All };
            w.Inv.Main.OrderKnown = true;
            w.Inv.Main.Items.Add(It(1, "Black Opal Ring", ObjectClasses.Jewelry));
            w.Inv.Main.Items.Add(It(2, "Celdon Girth", ObjectClasses.Armor));
            w.Inv.Main.Items.Add(It(3, "Treated Healing Kit", ObjectClasses.HealingKit));
            w.Inv.Main.Items.Add(It(4, "Mana Potion", ObjectClasses.Misc));
            SortController c = Controller(auto: true);
            c.Data.Rules.Clear();
            c.Data.Settings.OrderInPacks = false;
            Check(!c.Data.Settings.HandyFront, "off for a new character");
            Ticks(c, w, 0, 4000);
            Eq(w.Sent.Count, 0, "off: nothing moves");
            c.Data.Settings.HandyFront = true;
            Ticks(c, w, 4000, 12_000);
            Eq(Names(w.Inv.Main), "Treated Healing Kit,Mana Potion,Black Opal Ring,Celdon Girth", "on: the kit and the potion in front");
            int sent = w.Sent.Count;
            Ticks(c, w, 12_000, 20_000);
            Eq(w.Sent.Count, sent, "then nothing more moves");
            Check(w.Sent.All(m => m.Kind == MoveKind.Reorder && m.Dest.Id == Me), "only moves inside the main pack");

            // The player picks up the potion (selects it): it stays where they put it.
            SortItem potion = w.Inv.Main.Items.First(i => i.Id == 4);
            PackOrder.Apply(w.Inv.Main.Items, potion, 3);
            c.Touch.NoteSelected(4, c.Clock);
            Ticks(c, w, 20_000, 26_000);
            Eq(w.Inv.Main.Items.FindIndex(i => i.Id == 4), 3, "held: not moved back to the front");

            // The window: the switch, the list as chips, the picker, the note under the main pack.
            SortController cs = ScreenController(w.Inv);
            cs.Tick(new FakeWorld { Inv = w.Inv, Can = SortAbilities.All }, 100);
            cs.Data.Settings.HandyFront = true;
            ScreenModel m = Screen(cs);
            Check(m.Settings.HandyOn && m.Settings.HandyChips.Length == 5, "five chips: three kinds, two names");
            Check(m.Settings.HandyChips[0].Label == "Healing kits" && m.Settings.HandyChips[3].IsName && m.Settings.HandyChips[3].Label == "named \"Potion\"", "kinds, then names");
            Check(!m.Settings.HandyChoices.Any(k => k.Cls == ObjectClasses.ManaStone) && m.Settings.HandyChoices.Any(k => k.Cls == ObjectClasses.Gem), "the picker offers what isn't on the list");
            Eq(m.Main.Handy, "Handy at the front: healing kits, mana stones, food, potions and elixirs.", "the main pack says so");
            cs.Data.Settings.HandyFront = false;
            Eq(Screen(cs).Main.Handy, "", "off: nothing said");

            // Editing the list.
            var s = new SortSettings();
            Check(HandyItems.AddClass(s, ObjectClasses.Scroll) && !HandyItems.AddClass(s, ObjectClasses.Scroll) && !HandyItems.AddClass(s, ObjectClasses.Monster),
                "a kind once; only kinds the picker offers");
            Check(HandyItems.AddName(s, "  Draught ") && s.HandyNames.Contains("Draught") && !HandyItems.AddName(s, "draught") && !HandyItems.AddName(s, " "),
                "a name trimmed, once (any case), never blank");
            Check(HandyItems.RemoveName(s, "DRAUGHT") && !s.HandyNames.Contains("Draught"), "taken off, any case");
            s.HandyClasses.Clear();
            s.HandyNames.Clear();
            s.HandyFront = true;
            Check(HandyItems.For(s) == null, "an empty list: off");

            // The file: off and the default list for files from before it; kept as set.
            using var doc0 = System.Text.Json.JsonDocument.Parse("{\"rules\":[],\"settings\":{\"auto\":true,\"holdMin\":3}}");
            CharacterSortData old = SortStore.Parse(doc0.RootElement);
            Check(!old.Settings.HandyFront && old.Settings.HandyClasses.SequenceEqual(HandyItems.DefaultClasses) && old.Settings.HandyNames.SequenceEqual(HandyItems.DefaultNames),
                "an older file: off, with the default list");
            var d = new CharacterSortData();
            d.Settings.HandyFront = true;
            d.Settings.HandyClasses = new List<int> { ObjectClasses.HealingKit, ObjectClasses.Scroll };
            d.Settings.HandyNames = new List<string> { "Elixir" };
            using var doc1 = System.Text.Json.JsonDocument.Parse(SortStore.ToJson(d));
            CharacterSortData back = SortStore.Parse(doc1.RootElement);
            Check(back.Settings.HandyFront && back.Settings.HandyClasses.SequenceEqual(new[] { ObjectClasses.HealingKit, ObjectClasses.Scroll })
                  && back.Settings.HandyNames.SequenceEqual(new[] { "Elixir" }), "saved and read back");
            using var doc2 = System.Text.Json.JsonDocument.Parse("{\"settings\":{\"handyFront\":\"yes\",\"handyClasses\":[29,29,-4,\"x\",5],\"handyNames\":[\"\",7,\"Tonic\"]}}");
            CharacterSortData odd = SortStore.Parse(doc2.RootElement);
            Check(!odd.Settings.HandyFront && odd.Settings.HandyClasses.SequenceEqual(new[] { ObjectClasses.HealingKit }) && odd.Settings.HandyNames.SequenceEqual(new[] { "Tonic" }),
                "odd values: off, each kind once, only real kinds and names");
        }
    }
}
