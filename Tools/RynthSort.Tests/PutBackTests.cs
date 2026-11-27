using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // "Put back": the last sort's moves undone (items back to the packs they came from), with
    // auto-sort off first so nothing moves straight back again.
    internal static partial class Program
    {
        private static void PutBackLoop()
        {
            var w = new FakeWorld();
            w.Inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            w.Inv.Main.Items.Add(It(11, "Salvaged Iron", ObjectClasses.Salvage));
            w.Inv.Packs[0].Items.Add(It(12, "Black Opal", ObjectClasses.Gem));
            SortController c = Controller();
            c.Data.Rules = SortRules.Defaults();
            Ticks(c, w, 0, 5000);
            Eq(w.Sent.Count, 3, "the sort moved three items");
            Eq(c.PutBackCount, 3, "all three can be put back");
            Check(c.History.Any(h => h.ItemId == 12 && h.FromId == 0x40000001 && h.ToId == 0x40000003), "each with where it came from");

            Check(c.StartPutBack(), "Put back starts");
            Check(!c.Data.Settings.Auto && c.Job == SortScope.PutBack, "auto-sort off first, the job asked for");
            Ticks(c, w, 5000, 10_000);
            Check(w.Inv.Main.Items.Any(i => i.Id == 10) && w.Inv.Main.Items.Any(i => i.Id == 11) && w.Inv.Packs[0].Items.Any(i => i.Id == 12),
                "every item is back where it was");
            Eq(w.Sent.Count, 6, "three moves back");
            Check(c.Job == null && c.PutBackCount == 0, "done, nothing left to put back");
            Eq(c.Log[0], "Put back 3 items", "the log says so");
            Check(!c.StartPutBack(), "nothing to put back: it doesn't start");
            Ticks(c, w, 10_000, 15_000);
            Eq(w.Sent.Count, 6, "and nothing moves again (auto-sort is off)");

            // Moved since by the player: that one stays where the player put it.
            c.SetAuto(true);
            Ticks(c, w, 15_000, 20_000);
            Eq(c.PutBackCount, 3, "sorted again");
            SortItem opal = w.Inv.Packs[2].Items.First(i => i.Id == 12);
            w.Inv.Packs[2].Items.Remove(opal);
            w.Inv.Packs[1].Items.Add(opal);
            c.StartPutBack();
            Ticks(c, w, 20_000, 26_000);
            Check(w.Inv.Packs[1].Items.Any(i => i.Id == 12), "the opal the player moved stays");
            Check(w.Inv.Main.Items.Any(i => i.Id == 10) && w.Inv.Main.Items.Any(i => i.Id == 11), "the other two went back");
            Check(c.Log[0].StartsWith("Put back 2 items (1 gone", StringComparison.Ordinal), $"the log counts the one left ({c.Log[0]})");

            // A new sort after a pause starts a new list; an item moved twice goes back to its first pack.
            var w2 = new FakeWorld();
            w2.Inv.Main.Items.Add(It(20, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c2 = Controller();
            c2.Data.Rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.SpellComponent, 1) };
            Ticks(c2, w2, 0, 3000);
            Check(c2.History.Count == 1 && c2.History[0].FromId == Me, "to pack 1 from the main pack");
            c2.Data.Rules[0].Pack = 2;
            c2.Data.Rules[0].PackId = 0x40000002;
            Ticks(c2, w2, 3000, 6000);
            Check(c2.History.Count == 1 && c2.History[0].FromId == Me && c2.History[0].ToId == 0x40000002, "moved again: still goes back to the main pack");
            w2.Inv.Main.Items.Add(It(21, "Lead Scarab", ObjectClasses.SpellComponent));
            Ticks(c2, w2, 6000 + SortController.RunGapMs + 1000, 6000 + SortController.RunGapMs + 5000);
            Check(c2.History.Count == 1 && c2.History[0].ItemId == 21, "a sort after a pause: only its moves");

            // Stack joins aren't kept (the item is gone), and old moves fall off.
            var w3 = new FakeWorld { Can = SortAbilities.All };
            w3.Inv.Packs[0].Items.Add(It(30, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 7, stack: 10, max: 100));
            w3.Inv.Main.Items.Add(It(31, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 7, stack: 2, max: 100));
            SortController c3 = Controller();
            c3.Data.Rules = SortRules.Defaults();
            Ticks(c3, w3, 0, 3000);
            Check(w3.Sent.Count >= 1 && w3.Sent[0].MergeInto == 30, "joined the stack in pack 1");
            Eq(c3.PutBackCount, 0, "a join can't be put back");
            w3.Inv.Main.Items.Add(It(32, "Salvaged Iron", ObjectClasses.Salvage));
            Ticks(c3, w3, 3000, 6000);
            Eq(c3.PutBackCount, 1, "a plain move can");
            Ticks(c3, w3, 6000, 6000 + SortController.HistoryKeepMs + 2000);
            Eq(c3.PutBackCount, 0, "after half an hour it falls off");
        }

        private static void PutBackPlan()
        {
            SortInventory inv = Inv(2, cap: 1, mainCap: 1);
            inv.Main.Items.Add(It(1, "Filler", ObjectClasses.Misc));
            inv.Packs[0].Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            inv.Packs[1].Items.Add(It(11, "Black Opal", ObjectClasses.Gem));
            var made = new List<MadeMove>
            {
                new(10, "Prismatic Taper", Me, 0x40000001, 0),
                new(11, "Black Opal", 0x4000AAAA, 0x40000002, 1),
                new(99, "Gone", Me, 0x40000001, 2),
            };
            var p = new SortPlanner();
            List<SortMove> moves = p.PlanPutBack(inv, made, 0);
            Eq(moves.Count, 0, "main pack full, the other's pack not carried, the third gone: nothing moves");
            Check(p.View.MainFull && p.View.WaitMainFull == 1 && p.View.WaitNoPack == 1, "and the view says why");

            inv.Main.Capacity = 5;
            moves = p.PlanPutBack(inv, made, 0);
            Check(moves.Count == 1 && moves[0].Item.Id == 10 && moves[0].DestIndex == 0 && moves[0].MergeInto == 0 && moves[0].FromIndex == 1,
                "room now: the taper goes back as a stack of its own");
            Check(p.PlanUnpack(inv, null, 0).All(m => m.FromIndex > 0), "unpack moves say which pack they came from");
        }
    }
}
