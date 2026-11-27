using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // Starter layouts (#rynth-issues "The bag sort feature is lacking in UX": complex rules, no
    // common-sense default) fitted to the packs a character carries, and the preview of what a
    // sort would move.
    internal static partial class Program
    {
        private static void LayoutCatalog()
        {
            Check(StarterLayouts.All.Length >= 3, "a few layouts to pick from");
            Eq(StarterLayouts.All[0].Key, "everyday", "the one to suggest first");
            Check(StarterLayouts.All.Select(l => l.Key).Distinct().Count() == StarterLayouts.All.Length
                  && StarterLayouts.All.Select(l => l.Name).Distinct().Count() == StarterLayouts.All.Length, "keys and names are unique");
            foreach (StarterLayout l in StarterLayouts.All)
            {
                Check(l.Groups.Length >= 3 && l.Blurb.Length > 0 && l.Blurb.Length <= 120, $"{l.Name}: kinds and a short line ({l.Blurb.Length})");
                var seen = new HashSet<int>();
                foreach (StarterGroup g in l.Groups)
                    foreach (int c in g.Classes)
                    {
                        Check(seen.Add(c), $"{l.Name}: {ObjectClasses.Label(c)} is in one kind only");
                        Check(ObjectClasses.ChoiceIndex(c) >= 0, $"{l.Name}: {ObjectClasses.Label(c)} is a class the rule editor offers");
                    }
            }
            Check(StarterLayouts.Find("HUNTING") == StarterLayouts.Hunting && StarterLayouts.Find("nope") == null && StarterLayouts.Find(null) == null, "found by key");
        }

        private static void LayoutFit()
        {
            // No side packs: every kind stays in the main pack.
            LayoutPlan none = StarterLayouts.Fit(StarterLayouts.Everyday, Inv(0));
            Check(none.Placed.Count == 0 && none.Rules.Count == 0 && none.Left.Count == StarterLayouts.Everyday.Groups.Length, "no packs: nothing placed");

            // Three empty packs: the first three kinds, top pack first.
            SortInventory three = Inv(3);
            LayoutPlan p3 = StarterLayouts.Fit(StarterLayouts.Everyday, three);
            Eq(string.Join(",", p3.Placed.Select(x => x.Group.Key + x.Pack)), "components1,gems2,salvage3", "most wanted kinds on the top packs");
            Eq(string.Join(",", p3.Left.Select(g => g.Key)), "notes,supplies,scrolls", "the rest stay in the main pack");
            Check(p3.Spare.Count == 0 && p3.AlreadyThere == 0, "no spare pack, nothing in place yet");
            Check(p3.Rules.Count == 3 && p3.Rules[0].PackId == 0x40000001 && p3.Rules[0].Pack == 1 && p3.Rules[0].HasClass(ObjectClasses.SpellComponent)
                  && p3.Rules[1].HasClass(ObjectClasses.Gem) && p3.Rules[1].HasClass(ObjectClasses.ManaStone) && p3.Rules[1].HasClass(ObjectClasses.Key),
                "one rule per kind, tied to its pack's id");
            Check(p3.Rules[0].PackNote == "Pack 1 - empty", "and noted as the pack was");

            // Packs that already hold a kind keep it (the fewest moves); the rest go to the roomiest packs.
            SortInventory four = Inv(4);
            Fill(four.Packs[0], 20, 1000);                                             // pack 1: 20 other things
            for (uint i = 0; i < 5; i++) four.Packs[2].Items.Add(It(2000 + i, "Salvaged Iron", ObjectClasses.Salvage));
            for (uint i = 0; i < 8; i++) four.Packs[3].Items.Add(It(3000 + i, "Prismatic Taper", ObjectClasses.SpellComponent));
            LayoutPlan p4 = StarterLayouts.Fit(StarterLayouts.Basics, four);
            Eq(string.Join(",", p4.Placed.Select(x => x.Group.Key + x.Pack)), "gems2,salvage3,components4", "components stay in 4, salvage in 3, gems to the empty pack 2");
            Eq(p4.AlreadyThere, 13, "13 items already where their kind goes");
            Eq(string.Join(",", p4.Spare), "1", "pack 1 (full of other things) is left free");

            // More packs than kinds: spares, top first.
            LayoutPlan p7 = StarterLayouts.Fit(StarterLayouts.Basics, Inv(5));
            Check(p7.Placed.Count == 3 && p7.Left.Count == 0 && string.Join(",", p7.Spare) == "4,5", "five packs, three kinds: packs 4 and 5 spare");
            LayoutPlan all = StarterLayouts.Fit(StarterLayouts.Everyday, Inv(7));
            Check(all.Placed.Count == 6 && all.Left.Count == 0 && all.Spare.Count == 1, "seven packs: every kind placed, one spare");

            // The rules mean what the plan says.
            SortInventory inv = Inv(3);
            inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            inv.Main.Items.Add(It(11, "Black Opal", ObjectClasses.Gem));
            inv.Main.Items.Add(It(12, "Trade Note (250,000)", ObjectClasses.TradeNote));
            LayoutPlan fit = StarterLayouts.Fit(StarterLayouts.Everyday, inv);
            Eq(SortRules.Resolve(SortRules.RuleFor(fit.Rules, inv.Main.Items[0])!, inv, out _), 1, "the taper goes to pack 1");
            Eq(SortRules.Resolve(SortRules.RuleFor(fit.Rules, inv.Main.Items[1])!, inv, out _), 2, "the opal to pack 2");
            Check(SortRules.RuleFor(fit.Rules, inv.Main.Items[2]) == null, "the trade note's kind had no pack: it stays");
        }

        private static void PreviewMoves()
        {
            SortInventory inv = Inv(3);
            inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            inv.Main.Items.Add(It(11, "Lead Scarab", ObjectClasses.SpellComponent));
            inv.Packs[1].Items.Add(It(12, "Black Opal", ObjectClasses.Gem));
            inv.Packs[2].Items.Add(It(13, "Junk", ObjectClasses.Misc));
            int before = inv.Main.Items.Count + inv.Packs.Sum(p => p.Items.Count);

            SortPreview p = SortPreview.Build(inv, Defaults, PlanOptions.Full, null);
            Eq(p.Moves.Count, 3, "three items would move");
            Check(p.Into[1] == 2 && p.Into[3] == 1 && p.OutOf[0] == 2 && p.OutOf[2] == 1, "counted by pack in and out");
            Check(p.Moves.All(m => m.Kind == MoveKind.Route) && !p.Empty, "only moves to other packs");
            Eq(inv.Main.Items.Count + inv.Packs.Sum(x => x.Items.Count), before, "the preview moves nothing");
            Check(inv.Main.Items.Any(i => i.Id == 10), "the inventory is as it was");

            SortPreview clean = SortPreview.Build(inv, Defaults, PlanOptions.Full, null, clean: true);
            Check(clean.Moves.Count == 4 && clean.Into[0] == 1, "Clean also sends the stray home");

            SortPreview held = SortPreview.Build(inv, Defaults, PlanOptions.Full, id => id == 10);
            Check(held.Moves.Count == 2 && held.View.Held.Count == 1 && held.View.Held[0].Item.Id == 10, "a held item is shown apart, not as a move");

            // Settle times and blocks of the real planner don't matter to the preview, and it doesn't touch them.
            var real = new SortPlanner();
            real.Plan(inv, Defaults, SortScope.All, force: false, 0, 60_000);
            Eq(real.View.WaitSettle, 2, "the real planner: both new items settling");
            SortPreview again = SortPreview.Build(inv, Defaults, PlanOptions.Full, null);
            Check(again.Moves.Count == 3 && real.View.WaitSettle == 2, "the preview moves them now; the real view is unchanged");

            // Nothing to move: empty, and how much tidying inside the packs follows.
            SortInventory tidy = Inv(1);
            tidy.Packs[0].Items.Add(It(20, "Zircon", ObjectClasses.Gem));
            tidy.Packs[0].Items.Add(It(21, "Amber", ObjectClasses.Gem));
            tidy.Packs[0].OrderKnown = true;
            SortPreview t = SortPreview.Build(tidy, new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) }, PlanOptions.Full, null);
            Check(t.Empty && t.ToOrder == 1, "nothing to move; one item to put in order");
            Eq(SortPreview.Build(tidy, Defaults, default, null).ToOrder, 0, "ordering off: nothing to order");

            // A layout's preview: its rules on the packs as they are.
            LayoutPlan fit = StarterLayouts.Fit(StarterLayouts.Everyday, inv);
            SortPreview lp = SortPreview.Build(inv, fit.Rules, PlanOptions.Full, null);
            Check(lp.Into[1] == 2 && lp.Moves.Count == 2, $"Everyday on these packs: two components to pack 1, the opal stays in pack 2, which gets the gems ({string.Join(",", lp.Into)})");
        }
    }
}
