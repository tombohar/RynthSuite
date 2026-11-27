using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // RynthSort 0.6.0: a full pack. Items spill into the next pack with the same rule (an overflow
    // pack); when there is none, the window's red banner and one chat line per sort say what didn't
    // fit and where, and the pack's fill bar turns red. Each pack's own capacity counts.
    internal static partial class Program
    {
        private static void FullPackSpill()
        {
            // Gems: the Satchel (pack 1, 2 slots) first, the Backpack (pack 2) when it is full.
            SortInventory inv = Inv(3);
            inv.Packs[0].Name = "Satchel";
            inv.Packs[0].Capacity = 2;
            inv.Packs[1].Name = "Backpack";
            inv.Packs[2].Name = "Sack";
            inv.Packs[0].Items.Add(It(1, "Fire Opal", ObjectClasses.Gem));
            for (uint i = 10; i < 14; i++) inv.Main.Items.Add(It(i, "Gem " + i, ObjectClasses.Gem));
            var rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1), SortRule.ForClass(ObjectClasses.Gem, 2) };
            Check(SortRules.RulesFor(rules, inv.Main.Items[0]).Count == 2 && SortRules.RulesFor(rules, inv.Main.Items[0])[0] == rules[0], "both rules match; the first is the gem's own");

            var planner = new SortPlanner();
            List<SortMove> moves = planner.Plan(inv, rules, SortScope.All, force: true, 0, 0);
            Check(moves.Count == 4 && moves[0].DestIndex == 1 && moves.Skip(1).All(m => m.DestIndex == 2), "one gem fills the Satchel, the rest spill into the Backpack");
            Check(planner.View.Full.Contains(1) && planner.View.NoFit.Count == 0, "the Satchel is full, nothing left over");

            // An item already in the overflow pack stays there while its own pack is full...
            foreach (SortMove m in moves) ServerApply(inv, m);
            Eq(planner.Plan(inv, rules, SortScope.All, force: true, 0, 0).Count, 0, "spilled items stay put");
            // ... and goes to its own pack when that has room again.
            inv.Packs[0].Items.RemoveAt(0);
            moves = planner.Plan(inv, rules, SortScope.All, force: true, 0, 0);
            Check(moves.Count == 1 && moves[0].DestIndex == 1 && moves[0].FromIndex == 2, "a slot free in the Satchel: one gem goes back to it");
            Check(planner.Plan(inv, rules, SortScope.Clean, force: true, 0, 0).All(m => m.FromIndex != 2 || m.DestIndex == 1), "Clean doesn't pull overflow gems out to the main pack");

            // No overflow with room: the items stay where they are, and the view names them.
            SortInventory tight = Inv(2, cap: 1);
            tight.Packs[0].Name = "Satchel";
            tight.Packs[0].Items.Add(It(1, "Fire Opal", ObjectClasses.Gem));
            tight.Packs[1].Items.Add(It(2, "Junk", ObjectClasses.Misc));
            tight.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            tight.Main.Items.Add(It(11, "Amethyst", ObjectClasses.Gem));
            moves = planner.Plan(tight, rules, SortScope.All, force: true, 0, 0);
            Eq(moves.Count, 0, "both packs full: nothing moves");
            Check(planner.View.NoFit.Count == 2 && planner.View.NoFit.All(l => l.Pack == 1 && l.From == 0), "two gems didn't fit the Satchel; they stay in the main pack");
            Check(planner.View.WaitFull == 2, "counted as waiting on a full pack");
            Eq(SortController.NoFitText(planner.View.NoFit, tight), "Satchel is full: Black Opal and Amethyst stay where they are.", "the words");
            var one = new List<NoFitLine> { planner.View.NoFit[0] };
            Eq(SortController.NoFitText(one, tight), "Satchel is full: Black Opal stays where it is.", "one item");
            var many = Enumerable.Range(0, 6).Select(i => new NoFitLine(It((uint)(50 + i), "Gem" + i, ObjectClasses.Gem), 1, 0)).ToList();
            Eq(SortController.NoFitText(many, tight), "Satchel is full: Gem0, Gem1, Gem2 and 3 more stay where they are.", "at most three names");

            // A stack it can join still takes it in a full pack (as before).
            SortInventory stack = Inv(1, cap: 1);
            stack.Packs[0].Items.Add(It(1, "Lead Scarab", ObjectClasses.SpellComponent, wcid: 7, stack: 5, max: 100));
            stack.Main.Items.Add(It(2, "Lead Scarab", ObjectClasses.SpellComponent, wcid: 7, stack: 3, max: 100));
            moves = planner.Plan(stack, SortRules.Defaults(), SortScope.All, force: true, 0, 0);
            Check(moves.Count == 1 && moves[0].MergeInto == 1 && planner.View.NoFit.Count == 0, "joins the stack there: not a no-fit");

            // Each pack's real capacity: a 30-slot pack with 24 items still takes more.
            SortInventory big = Inv(1, cap: 30);
            Fill(big.Packs[0], 24, 100);
            big.Main.Items.Add(It(5, "Black Opal", ObjectClasses.Gem));
            moves = planner.Plan(big, new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) }, SortScope.All, force: true, 0, 0);
            Check(moves.Count == 1 && planner.View.NoFit.Count == 0, "24 of 30: room");
        }

        private static void FullPackWarnings()
        {
            // In the loop: one chat line when items stop fitting, not one per tick.
            var w = new FakeWorld { Inv = Inv(1, cap: 1) };
            w.Inv.Packs[0].Name = "Satchel";
            w.Inv.Packs[0].Items.Add(It(1, "Fire Opal", ObjectClasses.Gem));
            w.Inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            SortController c = Controller(auto: true);
            c.Data.Rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) };
            var said = new List<string>();
            c.Warn = said.Add;
            Ticks(c, w, 0, 5000);
            Check(said.Count == 1 && said[0] == "Satchel is full: Black Opal stays where it is.", $"one line ({string.Join(" | ", said)})");
            Check(c.Feed.Any(f => f.Text == said[0]), "and in the feed");
            Ticks(c, w, 5000, 15_000);
            Eq(said.Count, 1, "ten seconds later: still one line");
            w.Inv.Main.Items.Add(It(11, "Amethyst", ObjectClasses.Gem));
            Ticks(c, w, 15_000, 18_000);
            Check(said.Count == 2 && said[1] == "Satchel is full: Black Opal and Amethyst stay where they are.", "a new item that doesn't fit: a new line naming them all");
            // Sort now says it once, at its end.
            c.StartJob(SortScope.All);
            Ticks(c, w, 18_000, 20_000);
            Eq(said.Count, 3, "Sort now: one line when it ends");
            // Room again: forgotten, so it is said again if it stops fitting.
            w.Inv.Packs[0].Capacity = 3;
            Ticks(c, w, 20_000, 24_000);
            Check(w.Inv.Packs[0].Items.Count == 3, "room: both gems went");
            w.Inv.Main.Items.Add(It(12, "Jet", ObjectClasses.Gem));
            Ticks(c, w, 24_000, 27_000);
            Check(said.Count == 4 && said[3].Contains("Jet"), "full again: a new line");
            // Auto-sort off: nothing sorts, nothing said.
            c.SetAuto(false);
            w.Inv.Main.Items.Add(It(13, "Agate", ObjectClasses.Gem));
            Ticks(c, w, 27_000, 32_000);
            Eq(said.Count, 4, "off: no chat");

            // The window: the red banner, the card's line and its red bar, the main pack's bar.
            SortInventory inv = Inv(2, cap: 2);
            inv.Packs[0].Name = "Satchel";
            inv.Packs[1].Name = "Backpack";
            inv.Packs[0].Items.Add(It(1, "Fire Opal", ObjectClasses.Gem));
            inv.Packs[0].Items.Add(It(2, "Jet", ObjectClasses.Gem));
            inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            SortController cs = ScreenController(inv);
            cs.Data.Rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) };
            ScreenModel m = Screen(cs);
            Check(m.NoFit.Length == 1 && m.NoFit[0] == "Satchel is full: Black Opal stays where it is.", $"the banner ({string.Join(" | ", m.NoFit)})");
            Check(m.Packs[0].Full && !m.Packs[1].Full && m.Packs[0].Warnings.Contains("Full: 1 item for it waits where it is."), "the Satchel's bar is red and its card says so");
            Check(!m.Main.Full, "the main pack isn't full");

            // An overflow pack: the banner goes, the card says the rest go on, the chip says overflow.
            EditResult r = RuleEdits.AddOverflow(cs.Data.Rules, cs.Planner.View, 2, ObjectClasses.Gem);
            Check(r.Changed && r.Text == "Gems go to Satchel, and to Backpack when it is full. Nothing has moved yet.", $"overflow ({r.Text})");
            Check(cs.Data.Rules.Count == 2 && cs.Data.Rules[1].HasClass(ObjectClasses.Gem), "a new rule after the first");
            Check(!RuleEdits.AddOverflow(cs.Data.Rules, cs.Planner.View, 2, ObjectClasses.Gem).Changed, "again: nothing");
            Check(!RuleEdits.AddOverflow(cs.Data.Rules, cs.Planner.View, 1, ObjectClasses.Gem).Changed, "the pack that has it can't be its own overflow");
            Check(!RuleEdits.AddOverflow(cs.Data.Rules, cs.Planner.View, 2, ObjectClasses.Scroll).Changed, "a kind no pack has: add it first");
            m = Screen(cs);
            Check(m.NoFit.Length == 0 && m.Packs[0].Warnings.Contains("Full: the next pack with the same rule takes the rest."), "spilling: no banner");
            ChipModel chip = m.Packs[1].Chips.Single();
            Check(chip.Overflow && chip.Label == "Gems (overflow)" && chip.Tip.StartsWith("Gems come here when Satchel is full", StringComparison.Ordinal), "the overflow chip");
            Check(!m.Packs[0].Chips.Single().Overflow, "the Satchel's chip is the kind's own");
            Check(m.Moves.Length == 1 && m.Moves[0].Right == "Backpack", "Next moves: to the Backpack");
            Eq(m.KindChoices.Single(k => k.Cls == ObjectClasses.Gem).InPack, 1, "the picker still names the Satchel as the kind's pack");

            // A full main pack: red too.
            SortInventory fullMain = Inv(1, mainCap: 2);
            fullMain.Main.Items.Add(It(1, "A", ObjectClasses.Misc));
            fullMain.Main.Items.Add(It(2, "B", ObjectClasses.Misc));
            Check(Screen(ScreenController(fullMain)).Main.Full, "a full main pack's bar is red");
        }
    }
}
