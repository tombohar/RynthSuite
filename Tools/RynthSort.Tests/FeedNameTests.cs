using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // RynthSort 0.6.0: the activity feed names packs ("Mana Potion -> Satchel"), with the pack's
    // number when two carried packs share a name ("-> Satchel (3)"), as the cards' badges show it.
    internal static partial class Program
    {
        private static void FeedPackNames()
        {
            // The names themselves.
            string[] names = { "Sack", "Satchel", "Satchel", "" };
            Eq(PackNames.Label(names, 0), "main pack", "0 is the main pack");
            Eq(PackNames.Label(names, 1), "Sack", "a name no other pack has");
            Eq(PackNames.Label(names, 2), "Satchel (2)", "two packs called Satchel: the number too");
            Eq(PackNames.Label(names, 3), "Satchel (3)", "the other one");
            Eq(PackNames.Label(names, 4), "pack 4", "a pack the client hasn't named yet");
            Eq(PackNames.Label(names, 9), "pack 9", "past the packs carried");
            Eq(PackNames.Label(new[] { "Satchel", "satchel " }, 1), "Satchel (1)", "any case, spaces trimmed");
            Eq(PackNames.Label((SortInventory?)null, 2), "pack 2", "no packs read yet");

            // In the loop: the feed says the pack's name, the log (/rsort status) keeps the number.
            var w = new FakeWorld();
            SortInventory inv = Inv(3);
            inv.Packs[0].Name = "Sack";
            inv.Packs[1].Name = "Satchel";
            inv.Packs[2].Name = "Satchel";
            w.Inv = inv;
            inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            inv.Main.Items.Add(It(11, "Prismatic Taper", ObjectClasses.SpellComponent));
            SortController c = Controller(auto: true);
            c.Data.Rules = SortRules.Defaults();   // components 1, salvage 2, gems 3
            Ticks(c, w, 0, 4000);
            Check(inv.Packs[2].Items.Any(i => i.Id == 10) && inv.Packs[0].Items.Any(i => i.Id == 11), "both moved");
            FeedLine opal = c.Feed.First(f => f.ItemId == 10), taper = c.Feed.First(f => f.ItemId == 11);
            Eq(opal.Text, "Black Opal -> Satchel (3)", "two Satchels: the feed adds the number");
            Eq(taper.Text, "Prismatic Taper -> Sack", "a unique name: just the name");
            Check(c.Log.Contains("Black Opal -> pack 3") && c.Log.Contains("Prismatic Taper -> pack 1"), "the log keeps the numbers");

            // Sent from the window, to the main pack.
            c.StartSend(10, Me);
            Ticks(c, w, 4000, 6000);
            Eq(c.Feed[0].Text, "Black Opal -> main pack (you sent it)", "a send names the main pack");

            // Put in order inside a pack: one line that counts up, by name.
            var ow = new FakeWorld { Can = SortAbilities.All };
            SortInventory oi = Inv(1);
            oi.Packs[0].Name = "Hide Sack";
            oi.Packs[0].OrderKnown = true;
            oi.Packs[0].Items.Add(It(1, "Zircon", ObjectClasses.Gem));
            oi.Packs[0].Items.Add(It(2, "Amber", ObjectClasses.Gem));
            oi.Packs[0].Items.Add(It(3, "Jet", ObjectClasses.Gem));
            ow.Inv = oi;
            SortController co = Controller(auto: true);
            co.Data.Rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) };
            Ticks(co, ow, 0, 6000);
            Check(co.Feed.Count > 0 && co.Feed[0].Text.EndsWith("put in order in Hide Sack", StringComparison.Ordinal), $"reorders by name ({co.Feed[0].Text})");
            Check(co.Log[0].EndsWith("put in order in pack 1", StringComparison.Ordinal), "the log by number");

            // The window's sentences use the same names.
            SortController cs = ScreenController(inv);
            cs.Data.Rules = SortRules.Defaults();
            ScreenModel m = Screen(cs);
            Check(m.PackName(2) == "Satchel (2)" && m.PackName(3) == "Satchel (3)" && m.PackName(1) == "Sack" && m.PackName(0) == "main pack", "PackName for toasts and rows");
            Check(m.Packs[1].Name == "Satchel" && m.Packs[1].Label == "Satchel (2)", "the card keeps its name; its badge shows the number");
            SortView view = cs.Planner.View;
            EditResult r = RuleEdits.SendKind(cs.Data.Rules, view, 2, ObjectClasses.Plant);
            Check(r.Text.Contains("go to Satchel (2)"), $"a toast names the right Satchel ({r.Text})");
        }
    }
}
