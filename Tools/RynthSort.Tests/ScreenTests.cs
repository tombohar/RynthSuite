using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // The window drawn with ImGui (2026-11-26, the approved design for #rynth-issues "The bag sort
    // feature is lacking in UX"): its view-model (Screen/ScreenBuilder.cs), the rule changes it
    // makes (Screen/RuleEdits.cs), and what the controller does for it (an item sent to a pack,
    // the activity feed with times, settling items, items left alone and let go one at a time).
    internal static partial class Program
    {
        private static SortController ScreenController(SortInventory inv, bool auto = false)
        {
            var w = new FakeWorld { Inv = inv };
            SortController c = Controller(auto);
            c.WallClock = () => new DateTime(2026, 11, 26, 14, 5, 0);
            c.Tick(w, 0);   // one read: the controller's last inventory and the planner's view
            return c;
        }

        private static ScreenModel Screen(SortController c, bool layouts = false, ScreenSession? session = null) =>
            ScreenBuilder.Build(c, new ScreenInputs { Character = "Someone", Server = "Testworld", WantLayouts = layouts, Session = session, CanAddSelected = true });

        private static void ScreenFeedAndSettling()
        {
            var w = new FakeWorld();
            SortController c = Controller(auto: false, settleSec: 30);
            int minute = 0;
            c.WallClock = () => new DateTime(2026, 11, 26, 23, minute, 0);
            c.Data.Rules = SortRules.Defaults();
            c.SetAuto(true);
            Check(c.Feed.Count == 1 && c.Feed[0].Text == "Auto-sort on" && c.Feed[0].Time == "23:00", "auto-sort on is in the feed, with the time");
            Eq(c.Log.Count, 0, "but not in the log /rsort status reads");
            c.SetAuto(true);
            Eq(c.Feed.Count, 1, "turning it on again adds nothing");
            minute = 7;
            w.Inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            Ticks(c, w, 0, 2000);
            Check(c.Planner.View.Settling.Contains(10) && c.Planner.View.WaitSettle == 1, "a new item settling is named in the view");
            ScreenModel m = Screen(c);
            Check(m.TilesById[10].Settling, "and its tile shows the ring");
            Check(m.Status.Tone == PillTone.Waiting && m.Status.Line.StartsWith("1 new item settling", StringComparison.Ordinal), $"the status says so ({m.Status.Line})");
            Ticks(c, w, 2000, 33_000);
            Check(w.Inv.Packs[2].Items.Any(i => i.Id == 10) && !c.Planner.View.Settling.Contains(10), "settled and moved");
            Check(c.Feed[0].ItemId == 10 && c.Feed[0].Time == "23:07", "the move is in the feed with its item and time");
            for (int i = 0; i < SortController.FeedMax + 5; i++) c.NoteFeed("line " + i);
            Eq(c.Feed.Count, SortController.FeedMax, "the feed keeps the last few");
            Eq(c.Feed[0].Text, "line " + (SortController.FeedMax + 4), "newest first");
        }

        private static void ScreenModelPacks()
        {
            SortInventory inv = DesignPacks();
            SortController c = ScreenController(inv);
            c.Data.Rules = StarterLayouts.Fit(StarterLayouts.Everyday, inv).Rules;
            c.Data.Rules.Add(new SortRule { Pack = 3, PackId = inv.Packs[2].Id });
            c.Data.Rules[^1].AddName("Black Opal");
            c.Touch.Hold(inv.Main.Items[0].Id, c.Clock, TouchKind.Selected);   // the Black Opal, selected in the game
            ScreenModel m = Screen(c);

            Check(m.Ready && m.Who == "Someone · Testworld", "ready, and whose packs");
            Eq(m.Main.Count, "9 / 102", "main pack count");
            Check(m.Main.Wells == 9 && !m.Main.NearlyFull, "three rows of wells in all");
            Eq(m.Packs.Length, 4, "four side packs");
            PackModel sack = m.Packs[0], hide = m.Packs[3], satchel = m.Packs[2];
            Check(sack.Name == "Sack" && sack.Badge == "1" && sack.Where == "Top of your pack column", "the top pack");
            Check(m.Packs[1].Where == "Pack 2 from the top" && hide.Where == "Bottom of your pack column", "where the others sit");
            Eq(sack.Count, "3/24", "a pack's count");
            Check(sack.Ghosts.Length == 1 && sack.Incoming == "+1 coming", "the taper is coming to the Sack");
            Check(satchel.Ghosts.Length == 2 && satchel.Incoming == "+2 coming", "the mana stone and the key to the Satchel (the opal is left alone)");
            Check(hide.Incoming == "+1 coming" && hide.Chips.Select(x => x.Label).SequenceEqual(new[] { "Food", "Healing kits", "Lockpicks" }), "the Hide Sack's kinds, A-Z, and the kit coming");
            ChipModel name = satchel.Chips.Single(x => x.IsName);
            Check(name.Label == "named \"Black Opal\"" && name.Name == "Black Opal" && name.Pack == 3, "a name rule is its own chip");
            Check(satchel.Chips.Where(x => !x.IsName).Select(x => x.Label).SequenceEqual(new[] { "Gems", "Keys", "Mana stones" }), "the Satchel's kinds");

            TileModel opal = m.TilesById[inv.Main.Items[0].Id];
            Check(opal.Held && opal.HeldWhy == "You selected it just now" && !opal.Outgoing, "the opal is left alone, so not in the moves");
            Check(opal.Rule == TileRule.Pack && opal.RulePack == 3 && opal.ByName && opal.RuleText == "named \"Black Opal\"", "and it goes by its name rule");
            TileModel taper = m.TilesById[inv.Main.Items[1].Id];
            Check(taper.Qty == "320" && taper.Outgoing && !taper.Moving && taper.KindLabel == "Spell components", "a stack's count; outgoing but nothing runs, so no lift");
            Eq(m.TilesById[inv.Main.Items[8].Id].Qty, "2.4k", "thousands");
            TileModel coat = m.TilesById[inv.Main.Items[6].Id];
            Check(coat.Rule == TileRule.None && !coat.Outgoing, "the coat has no rule: it stays");

            Eq(m.MovesCount, 5, "taper, stone, steel, kit and key would move");
            RowModel first = m.Moves[0];
            Check(first.Name == "Prismatic Taper" && first.Sub == "Rule: Spell components" && first.Right == "Sack", $"a move row ({first.Name}, {first.Sub}, {first.Right})");
            Check(m.MovesHint.StartsWith("Nothing moves until", StringComparison.Ordinal), "auto-sort off: the hint");
            Check(m.Held.Length == 1 && m.Held[0].Sub == "You selected it just now · would go to Satchel", $"left alone, and where it would go ({m.Held[0].Sub})");
            Check(m.Status.Tone == PillTone.Off && m.Status.Pill == "Off" && m.Status.Line == "Nothing moves by itself. A sort would move 5 items. 1 left alone.", $"status ({m.Status.Line})");
            Check(m.HoldNote == "3 min" && m.Feed.Length == 0 && m.Orphans.Length == 0 && !m.NoRules, "the rest");
            Check(m.KindChoices.Single(k => k.Cls == ObjectClasses.Gem).InPack == 3 && m.KindChoices.Single(k => k.Cls == ObjectClasses.Scroll).InPack == 0,
                "the picker knows which pack has a kind");

            // Why it is where it is.
            string Why(TileModel t) => string.Concat(ScreenText.Why(t, m).Select(r => r.Text));
            Eq(Why(opal), "Goes to Satchel by the rule named \"Black Opal\", once it's no longer left alone.", "why: held, would go");
            Eq(Why(coat), "No rule names it, so it stays in the main pack.", "why: no rule");
            TileModel garnet = m.Packs[2].Tiles.Single(t => t.Name == "Black Garnet");
            Eq(Why(garnet), "In its pack: Satchel, by the rule Gems.", "why: in its pack");
            Check(ScreenText.Why(garnet, m).Count(r => r.Bold) == 2, "the pack and the rule in bold");
            Eq(ScreenText.KindAndPlace(garnet, m), "Gems · in Satchel", "kind and place");
            Check(ScreenText.Matches(garnet, "garn") && !ScreenText.Matches(garnet, "opal") && !ScreenText.Matches(garnet, ""), "find an item: any case; empty finds nothing");

            // Busy: the moves lift; tidy: the success pill; a pack nearly full turns amber.
            c.SetAuto(true);
            c.StartJob(SortScope.All);
            m = Screen(c);
            Check(m.Busy && m.Status.Tone == PillTone.Moving && m.TilesById[taper.Id].Moving, "a sort running: the taper lifts");
            var tidy = Inv(1);
            tidy.PackOrderKnown = true;
            tidy.Packs[0].Items.Add(It(1, "Fire Opal", ObjectClasses.Gem));
            for (uint i = 0; i < 21; i++) tidy.Packs[0].Items.Add(It(10 + i, "Black Garnet", ObjectClasses.Gem));
            SortController ct = ScreenController(tidy, auto: true);
            ct.Data.Rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) };
            ct.Tick(new FakeWorld { Inv = tidy }, 2000);
            ScreenModel mt = Screen(ct);
            Check(mt.Status.Tone == PillTone.Tidy && mt.Status.Pill == "All tidy" && mt.MovesCount == 0, $"all tidy ({mt.Status.Line})");
            Check(mt.Packs[0].NearlyFull && mt.Packs[0].Where == "Your only side pack", "22 of 24: amber");
        }

        private static void ScreenModelOrphansAndLayouts()
        {
            SortInventory inv = DesignPacks();
            SortController c = ScreenController(inv);
            c.Data.Rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.Scroll, 7) };
            ScreenModel m = Screen(c);
            Check(m.Orphans.Length == 1 && m.Orphans[0].Label == "Scrolls" && m.Orphans[0].Pack == 0, "a rule for a pack not carried");
            Check(m.KindChoices.Single(k => k.Cls == ObjectClasses.Scroll).InPack == -1, "the picker says its pack isn't carried");
            Eq(m.Layouts.Length, 0, "no layouts unless the popup is open");

            m = Screen(c, layouts: true);
            Eq(m.Layouts.Length, StarterLayouts.All.Length, "every layout");
            LayoutCard every = m.Layouts[0];
            Check(every.Key == "everyday" && every.Bars.Length == 4 && every.Bars[3].Length == 3 && every.Bars[3][0] == Kinds.Kits, "the Hide Sack's bar: kits, lockpicks, food");
            Eq(every.Meta, "6 would move", "what Everyday would move (the opal too: nothing is held here)");
            Check(every.Rows[3].PackName == "Hide Sack" && every.Rows[3].Chips.Length == 3, "the preview rows");
            Check(every.Left.StartsWith("Stay in the main pack: ", StringComparison.Ordinal) && every.Left.Contains("trade notes") && every.Left.Contains("armor"),
                $"what stays in the main pack ({every.Left})");
            Check(every.Replaces.Length > 0, "it replaces the rules there are");
            LayoutCard basics = m.Layouts.Single(l => l.Key == "basics");
            Check(basics.Bars[3].Length == 0 && basics.Rows[3].Chips.Length == 0, "Just the basics leaves the Hide Sack free");

            var none = ScreenBuilder.Build(c, new ScreenInputs());
            Check(!none.Ready && none.NotReady.StartsWith("Log in", StringComparison.Ordinal), "nobody logged in");
            Eq(ScreenBuilder.MainWells(0, 102), 18, "an empty main pack: three rows");
            Eq(ScreenBuilder.MainWells(20, 102), 4, "a row past the last item");
            Eq(ScreenBuilder.MainWells(101, 102), 1, "never past the capacity");
            Check(Kinds.Label(ObjectClasses.ManaStone) == "Mana stones" && Kinds.Label(ObjectClasses.MeleeWeapon) == "Melee weapons", "sentence-case kinds");
            Check(Kinds.Initials("Amuli Shadow Coat") == "AC" && Kinds.Qty(1) == "" && Kinds.Qty(12000) == "12k", "initials and counts");
        }

        private static void ScreenRuleEdits()
        {
            SortInventory inv = DesignPacks();
            SortController c = ScreenController(inv);
            List<SortRule> rules = c.Data.Rules;
            rules.Clear();
            rules.AddRange(StarterLayouts.Fit(StarterLayouts.Everyday, inv).Rules);
            SortView view = SortPreview.Build(inv, rules, default, null).View;

            // "All gems" dropped on the Hide Sack: gems leave the Satchel.
            EditResult r = RuleEdits.SendKind(rules, view, 4, ObjectClasses.Gem);
            Check(r.Changed && r.Text == "All gems now go to Hide Sack (taken from Satchel). Nothing has moved yet.", $"kind: {r.Text}");
            Check(rules.Count(x => x.HasClass(ObjectClasses.Gem)) == 1 && Views.SortPanelProxy.Shown(view, rules.First(x => x.HasClass(ObjectClasses.Gem))) == 4, "one pack has gems now");
            Check(RuleEdits.SendKind(rules, view, 4, ObjectClasses.Gem).Text == "Gems already go to Hide Sack.", "again: nothing changes");
            Check(!RuleEdits.SendKind(rules, view, 9, ObjectClasses.Gem).Changed, "a pack not carried");

            // A kind sent to a pack with no rules: a new rule there.
            var empty = new List<SortRule>();
            RuleEdits.SendKind(empty, view, 2, ObjectClasses.Salvage);
            Check(empty.Count == 1 && empty[0].PackId == inv.Packs[1].Id && empty[0].HasClass(ObjectClasses.Salvage), "a new rule tied to the pack");

            // "Only "Black Opal"" on the Backpack: a name rule; it leaves the Satchel's rules and beats the gems kind.
            rules.Add(new SortRule { Pack = 3, PackId = inv.Packs[2].Id });
            rules[^1].AddName("Black Opal");
            SortItem opal = inv.Main.Items[0];
            r = RuleEdits.SendName(rules, view, 2, opal);
            Check(r.Changed && r.Text == "\"Black Opal\" now goes to Backpack. Nothing has moved yet.", $"name: {r.Text}");
            Check(rules.Count(x => x.HasName("Black Opal")) == 1, "the name is in one pack's rules only (the Satchel's emptied rule went)");
            Eq(Views.SortPanelProxy.Shown(view, SortRules.RuleFor(rules, opal)!), 2, "the name beats the kind: the opal goes to the Backpack");
            Check(!RuleEdits.SendName(rules, view, 2, opal).Changed, "again: nothing changes");

            // A chip's x.
            SortRule gemRule = rules.First(x => x.HasClass(ObjectClasses.Gem));
            bool alone = gemRule.Classes.Count == 1 && gemRule.Names.Count == 0;
            r = RuleEdits.RemoveEntry(rules, view, gemRule.Uid, false, ObjectClasses.Gem, "");
            Check(r.Changed && r.Text == "Gems no longer go to Hide Sack.", $"x: {r.Text}");
            Check(!rules.Any(x => x.HasClass(ObjectClasses.Gem)) && (!alone || !rules.Contains(gemRule)), "gone, and an emptied rule with it");
            Check(!RuleEdits.RemoveEntry(rules, view, 123456, false, 1, "").Changed, "a rule that's gone");
            SortRule off = SortRule.ForClass(ObjectClasses.Scroll, 1);
            off.On = false;
            rules.Add(off);
            Check(RuleEdits.TurnOn(rules, off.Uid).Changed && off.On && !RuleEdits.TurnOn(rules, off.Uid).Changed, "a faded chip turns its rule on");

            // Toasts and the one Undo.
            var session = new ScreenSession();
            int before = rules.Count;
            string snapshot = string.Join("|", rules.Select(SortRules.Describe));
            Check(session.Edit(rules, l => RuleEdits.SendKind(l, view, 1, ObjectClasses.Plant), 1000), "a change");
            Check(session.Toast != null && session.Toast.CanUndo && session.Toast.UntilMs == 1000 + ScreenSession.ToastMs, "a toast with Undo");
            int seq = session.Toast!.Seq;
            Check(!session.Edit(rules, l => RuleEdits.SendKind(l, view, 1, ObjectClasses.Plant), 2000), "no change");
            Check(session.Toast != null && !session.Toast.CanUndo && session.Toast.Seq > seq && session.CanUndo, "says why, no Undo button, the Undo kept");
            Check(session.Undo(rules, 3000) && rules.Count == before && string.Join("|", rules.Select(SortRules.Describe)) == snapshot, "Undo puts the rules back");
            Check(!session.Undo(rules, 3000) && session.Toast!.Text == "Rules put back as they were.", "once");
            session.Expire(3000 + ScreenSession.ToastMs);
            Check(session.Toast == null, "the toast goes after its time");

            r = RuleEdits.UseLayout(rules, inv, StarterLayouts.Basics);
            Check(r.Changed && rules.Count == 3 && r.Text.StartsWith("Rules set from Just the basics", StringComparison.Ordinal), "a layout replaces the rules");
            Check(!RuleEdits.UseLayout(rules, null, StarterLayouts.Basics).Changed && !RuleEdits.UseLayout(rules, Inv(0), StarterLayouts.Basics).Changed, "no packs read, or none carried");
        }
    }
}

namespace RynthSort.Tests.Views
{
    internal static class SortPanelProxy
    {
        public static int Shown(RynthCore.Plugin.RynthSort.Sorting.SortView v, RynthCore.Plugin.RynthSort.Sorting.SortRule r) =>
            RynthCore.Plugin.RynthSort.Views.SortPanel.ShownPack(v, r);
    }
}
