using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Views;

namespace RynthSort.Tests
{
    // "Add selected": the selected item's name goes into a pack's rules (rule text, no duplicates,
    // which pack already takes the item, nothing selected / not an item), and the window's button.
    internal static partial class Program
    {
        private static SelectedItem ItemSel(SortItem it) => new() { Kind = SelectionKind.Item, Id = it.Id, Name = it.Name, Item = it };

        private static void AddSelectedText()
        {
            Eq(AddSelectedRules.EntryFor("Black Opal"), "Black Opal", "a plain name stays");
            Eq(AddSelectedRules.EntryFor("Salvaged Iron (100)"), "Salvaged Iron", "a bag's count is left out");
            Eq(AddSelectedRules.EntryFor("  Salvaged Steel (3) "), "Salvaged Steel", "whatever the count, spaces too");
            Eq(AddSelectedRules.EntryFor("Sword (Flaming)"), "Sword (Flaming)", "a bracket that isn't a count stays");
            Eq(AddSelectedRules.EntryFor("(5)"), "(5)", "a name that is only a count stays");
            Eq(AddSelectedRules.EntryFor(new string('x', 100)).Length, SortRule.MaxNameLength, "a long name is cut like a typed one");
            Eq(AddSelectedRules.EntryFor(null), "", "no name, no entry");

            // The entry matches the item it came from, and the same item at another stack size.
            var iron100 = It(1, "Salvaged Iron (100)", ObjectClasses.Salvage);
            var iron37 = It(2, "Salvaged Iron (37)", ObjectClasses.Salvage);
            SortRule r = SortRule.ForName(AddSelectedRules.EntryFor(iron100.Name), 4);
            Check(SortRules.Matches(r, iron100) && SortRules.Matches(r, iron37), "the entry takes the bag and other bags of it");
            Eq(SortRules.Describe(r), "name has \"Salvaged Iron\"", "the rule's text");
        }

        private static void AddSelectedLookup()
        {
            var inv = Inv(2);
            inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            inv.Packs[1].Items.Add(It(20, "Lead Scarab", ObjectClasses.SpellComponent));
            inv.Slots.Add(new SortSlot(inv.Packs[0].Id, 24));
            inv.Slots.Add(new SortSlot(0x40000F01, 0));

            Eq(SelectionLookup.Find(inv, 0, "").Kind, SelectionKind.Nothing, "id 0: nothing selected");
            SelectedItem a = SelectionLookup.Find(inv, 10, "x");
            Check(a.Kind == SelectionKind.Item && a.Item!.Name == "Black Opal" && a.Name == "Black Opal", "an item in the main pack");
            Eq(SelectionLookup.Find(inv, 20, "").Kind, SelectionKind.Item, "an item in a side pack");
            SelectedItem pack = SelectionLookup.Find(inv, inv.Packs[0].Id, "Sack");
            Check(pack.Kind == SelectionKind.Pack && pack.Name == "Sack", "a pack is not an item");
            Eq(SelectionLookup.Find(inv, 0x40000F01, "Foci").Kind, SelectionKind.Pack, "a focus is not an item");
            SelectedItem far = SelectionLookup.Find(inv, 0x70000001, "Drudge");
            Check(far.Kind == SelectionKind.NotInPacks && far.Name == "Drudge", "an object that isn't in the packs");
        }

        private static void AddSelectedRefusals()
        {
            var inv = Inv(3);
            var c = Sorter();
            c.Planner.Plan(inv, c.Data.Rules, SortScope.Main, true, 0, 0);
            List<SortRule> rules = c.Data.Rules;
            string before = string.Join("|", rules.Select(SortRules.Describe));
            SortView v = c.Planner.View;

            foreach (SelectionKind kind in new[] { SelectionKind.Unavailable, SelectionKind.NoInventory, SelectionKind.Nothing, SelectionKind.Pack, SelectionKind.NotInPacks })
            {
                AddOutcome o = AddSelectedRules.Apply(rules, v, 1, SelectedItem.Of(kind, 7, "Thing"));
                Check(o.Status == AddStatus.Refused && o.Rule == null && o.Message.Contains("Nothing was added"), $"{kind}: refused, says nothing was added ({o.Message})");
            }
            Check(AddSelectedRules.Apply(rules, v, 1, SelectedItem.Of(SelectionKind.Nothing)).Message.Contains("Nothing is selected"), "nothing selected says so");
            Check(AddSelectedRules.Apply(rules, v, 1, SelectedItem.Of(SelectionKind.Pack, 7, "Sack")).Message.Contains("is a pack"), "a pack says so");
            Check(AddSelectedRules.Apply(rules, v, 1, SelectedItem.Of(SelectionKind.NotInPacks, 7, "Drudge")).Message.Contains("isn't an item in your packs"), "not in the packs says so");

            var opal = It(10, "Black Opal", ObjectClasses.Gem);
            Eq(AddSelectedRules.Apply(rules, v, 4, ItemSel(opal)).Status, AddStatus.Refused, "a pack you don't carry");
            Eq(AddSelectedRules.Apply(rules, v, 0, ItemSel(opal)).Status, AddStatus.Refused, "pack 0");
            Eq(AddSelectedRules.Apply(rules, v, 1, ItemSel(It(11, "", ObjectClasses.Gem))).Status, AddStatus.Refused, "an item with no name yet");
            Eq(AddSelectedRules.Apply(rules, v, 1, new SelectedItem { Kind = SelectionKind.Item, Id = 5 }).Status, AddStatus.Refused, "an item selection without its item");
            Eq(string.Join("|", rules.Select(SortRules.Describe)), before, "no refusal changed a rule");
        }

        private static void AddSelectedAdds()
        {
            var inv = Inv(4);
            var c = Sorter();
            c.Planner.Plan(inv, c.Data.Rules, SortScope.Main, true, 0, 0);
            List<SortRule> rules = c.Data.Rules;
            SortView v = c.Planner.View;
            int count = rules.Count;

            // A new rule (pack 4 has none): one name, the pack's id kept.
            var tapers = It(30, "Prismatic Taper", ObjectClasses.SpellComponent, stack: 80, max: 100);
            AddOutcome o = AddSelectedRules.Apply(rules, v, 4, ItemSel(tapers));
            Check(o.Status == AddStatus.Added && o.Rule != null && rules.Contains(o.Rule!), "added as a new rule");
            Eq(rules.Count, count + 1, "one rule more");
            Check(o.Rule!.Pack == 4 && o.Rule.PackId == inv.Packs[3].Id, "the rule is pack 4's, by id");
            Check(o.Rule.Names.Count == 1 && o.Rule.HasName("Prismatic Taper") && o.Rule.Classes.Count == 0, "one name, no class");
            Check(o.Message.Contains("Added \"Prismatic Taper\" to pack 4 (Pack 4) as a new rule"), $"says what it added ({o.Message})");
            Check(o.Message.Contains("It was going to pack 1") && o.Message.Contains("Now it goes here"), $"says the class rule had it, and that it goes here now ({o.Message})");
            Eq(SortRules.RuleFor(rules, tapers)?.Pack, 4, "the name beats the class: it goes to pack 4 now");

            // A second item goes into the same rule (it already lists names), not a rule each.
            var opal = It(31, "Black Opal", ObjectClasses.Gem);
            AddOutcome o2 = AddSelectedRules.Apply(rules, v, 4, ItemSel(opal));
            Check(o2.Status == AddStatus.Added && ReferenceEquals(o2.Rule, o.Rule), "the next name joins that rule");
            Eq(rules.Count, count + 1, "no rule more");
            Check(o.Rule.HasName("Black Opal") && o.Rule.Names.Count == 2, "both names are in it");
            Check(!o2.Message.Contains("as a new rule") && o2.Message.Contains("name has \"Black Opal\", name has \"Prismatic Taper\""), $"shows the rule ({o2.Message})");

            // A bag of salvage (no rule wants it): its name without the count, no "was going to".
            var iron = It(32, "Salvaged Iron (100)", ObjectClasses.Misc);
            AddOutcome o3 = AddSelectedRules.Apply(rules, v, 4, ItemSel(iron));
            Check(o3.Status == AddStatus.Added && o3.Rule!.HasName("Salvaged Iron") && !o3.Rule.HasName("Salvaged Iron (100)"), "a bag is added without its count");
            Check(!o3.Message.Contains("was going to"), "no rule had it before");
            Eq(SortRules.RuleFor(rules, It(33, "Salvaged Iron (12)", ObjectClasses.Misc))?.Pack, 4, "another bag of it goes there too");

            // A rule that is off isn't joined.
            o.Rule.On = false;
            AddOutcome o4 = AddSelectedRules.Apply(rules, v, 4, ItemSel(It(34, "Buckler", ObjectClasses.Armor)));
            Check(o4.Status == AddStatus.Added && !ReferenceEquals(o4.Rule, o.Rule) && o4.Message.Contains("as a new rule"), "an off rule is left as it is");
            o.Rule.On = true;

            // Full rules (16 names) aren't joined: a new rule.
            foreach (SortRule r in rules.Where(r => r.Pack == 4))
                for (int i = 0; r.Names.Count < SortRule.MaxNames; i++) r.AddName("filler " + r.Uid + " " + i);
            AddOutcome o5 = AddSelectedRules.Apply(rules, v, 4, ItemSel(It(35, "Ruby", ObjectClasses.Gem)));
            Check(o5.Status == AddStatus.Added && o5.Rule != null && o5.Rule.Names.Count == 1 && o5.Message.Contains("as a new rule"), "full rules: a new one");

            // At the rule limit the name goes into a rule of the pack that has room; none: refused.
            var many = new List<SortRule>();
            for (int i = 0; i < SortStore.MaxRules - 1; i++) many.Add(SortRule.ForClass(ObjectClasses.Armor, 1));
            var roomy = SortRule.ForClass(ObjectClasses.Food, 2);
            many.Add(roomy);
            AddOutcome o6 = AddSelectedRules.Apply(many, v, 2, ItemSel(It(36, "Apple", ObjectClasses.Misc)));
            Check(o6.Status == AddStatus.Added && ReferenceEquals(o6.Rule, roomy) && roomy.HasName("Apple") && many.Count == SortStore.MaxRules, "at the limit: into the pack's rule with room");
            AddOutcome o7 = AddSelectedRules.Apply(many, v, 3, ItemSel(It(37, "Pear", ObjectClasses.Misc)));
            Check(o7.Status == AddStatus.Refused && many.Count == SortStore.MaxRules && o7.Message.Contains("no room"), $"at the limit, pack without a rule: refused ({o7.Message})");
        }

        private static void AddSelectedDuplicates()
        {
            var inv = Inv(4);
            var c = Sorter();
            c.Planner.Plan(inv, c.Data.Rules, SortScope.Main, true, 0, 0);
            List<SortRule> rules = c.Data.Rules;   // components 1, salvage 2, gems/stones/keys 3
            SortView v = c.Planner.View;
            rules.Add(SortRule.For(4, Array.Empty<int>(), "Opal"));
            SortRule four = rules[^1];
            string Dump() => string.Join("|", rules.Select(r => SortRules.Describe(r) + ">" + r.Pack + (r.On ? "" : "off")));
            string before = Dump();

            var blackOpal = It(40, "Black Opal", ObjectClasses.Gem);
            AddOutcome same = AddSelectedRules.Apply(rules, v, 4, ItemSel(It(41, "Opal", ObjectClasses.Gem)));
            Check(same.Status == AddStatus.AlreadyThere && same.Message.Contains("already in pack 4"), $"the same name: already there ({same.Message})");
            AddOutcome inside = AddSelectedRules.Apply(rules, v, 4, ItemSel(blackOpal));
            Check(inside.Status == AddStatus.AlreadyThere && inside.Message.Contains("name has \"Opal\""), $"a shorter name in the pack covers it ({inside.Message})");
            AddOutcome anyCase = AddSelectedRules.Apply(rules, v, 4, ItemSel(It(42, "OPAL", ObjectClasses.Gem)));
            Eq(anyCase.Status, AddStatus.AlreadyThere, "any case");
            Eq(Dump(), before, "duplicates change nothing");

            // Taken by a class rule of this pack (and no name elsewhere): already goes here.
            AddOutcome byClass = AddSelectedRules.Apply(rules, v, 3, ItemSel(It(43, "Ruby", ObjectClasses.Gem)));
            Check(byClass.Status == AddStatus.AlreadyThere && byClass.Message.Contains("already goes to pack 3") && byClass.Message.Contains("Gem"), $"a class rule of the pack: already goes there ({byClass.Message})");
            Eq(Dump(), before, "and nothing changes");

            // A rule that is off doesn't take it, but a second copy isn't added either.
            four.On = false;
            string beforeOff = Dump();
            AddOutcome off = AddSelectedRules.Apply(rules, v, 4, ItemSel(blackOpal));
            Check(off.Status == AddStatus.AlreadyThere && off.Message.Contains("turned off"), $"an off rule: say so ({off.Message})");
            Eq(Dump(), beforeOff, "nothing changes");
            four.On = true;

            // Another pack's name rule wins over this pack's covering rule: said, nothing added.
            var wins = SortRule.For(1, Array.Empty<int>(), "Black");
            rules.Insert(0, wins);
            AddOutcome loses = AddSelectedRules.Apply(rules, v, 4, ItemSel(blackOpal));
            Check(loses.Status == AddStatus.AlreadyThere && loses.Message.Contains("rule for pack 1") && loses.Message.Contains("wins"), $"already there but another pack's rule wins ({loses.Message})");
            rules.Remove(wins);
        }

        private static void AddSelectedOtherPack()
        {
            var inv = Inv(4);
            var c = Sorter();
            c.Planner.Plan(inv, c.Data.Rules, SortScope.Main, true, 0, 0);
            List<SortRule> rules = c.Data.Rules;
            SortView v = c.Planner.View;
            var opal = It(50, "Black Opal", ObjectClasses.Gem);   // gems go to pack 3

            // Claimed by pack 3's class rule: pressing Add for pack 4 adds there and says pack 3 had it.
            AddOutcome o = AddSelectedRules.Apply(rules, v, 4, ItemSel(opal));
            Check(o.Status == AddStatus.Added && o.Message.Contains("It was going to pack 3") && o.Message.Contains("Gem"), $"says which pack had it ({o.Message})");
            Check(o.Message.Contains("Now it goes here") && SortRules.RuleFor(rules, opal)?.Pack == 4, "and that the new name wins");
            Check(rules.Count(r => r.Pack == 3) == 1 && rules.Single(r => r.Pack == 3).HasClass(ObjectClasses.Gem), "pack 3's rule is untouched (nothing moved)");

            // Claimed by another pack's NAME rule that comes first: still added, and it says that rule still wins.
            var rules2 = new List<SortRule> { SortRule.For(2, Array.Empty<int>(), "Opal") };
            AddOutcome o2 = AddSelectedRules.Apply(rules2, v, 4, ItemSel(opal));
            Check(o2.Status == AddStatus.Added && o2.Message.Contains("It was going to pack 2") && o2.Message.Contains("still wins"), $"a name rule that wins is said ({o2.Message})");
            Eq(SortRules.RuleFor(rules2, opal)?.Pack, 2, "it really does still win");
            Check(rules2.Count == 2 && rules2[0].Names.Count == 1 && rules2[1].HasName("Black Opal"), "the other pack's rule is untouched");

            // A rule for a pack the character doesn't carry isn't a claim: this pack gets the item.
            var rules3 = new List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 9) };
            AddOutcome o3 = AddSelectedRules.Apply(rules3, v, 1, ItemSel(opal));
            Check(o3.Status == AddStatus.Added && SortRules.RuleFor(rules3, opal)?.Pack == 1, "a rule for a pack you don't have: this pack gets it");
        }

        private static void AddSelectedWindow()
        {
            var (c, panel, host, w) = Window();
            var opal = It(10, "Black Opal", ObjectClasses.Gem);
            SelectedItem current = SelectedItem.Of(SelectionKind.Nothing);
            panel.ReadSelected = () => current;
            Draw(w, panel);
            Check(BodyText(w).Contains("+ Add selected"), "every pack has an Add selected button");
            uint seq = 0;
            void Press(string id)
            {
                Apply(host, Event(1, ++seq, w.Hash, w.KeyOf(id), Click));
                Draw(w, panel);
            }
            int rules = c.Data.Rules.Count;

            Press("addsel.40000004");
            Eq(c.Data.Rules.Count, rules, "nothing selected: no rule");
            Check(BodyText(w).Contains("Nothing is selected"), "and the window says so");

            current = ItemSel(opal);
            Press("addsel.40000004");
            Eq(c.Data.Rules.Count, rules + 1, "an item: a rule");
            SortRule added = c.Data.Rules[^1];
            Check(added.HasName("Black Opal") && added.PackId == 0x40000004 && ReferenceEquals(panel.Selected, added), "its name, pack 4, editor open");
            string body = BodyText(w);
            Check(body.Contains("Added \"Black Opal\" to pack 4") && body.Contains("name has \"Black Opal\""), "the window says what was added and shows the rule");
            Check(body.IndexOf("Added \"Black Opal\"", StringComparison.Ordinal) > body.IndexOf("Pack 4: ", StringComparison.Ordinal), "the note sits in pack 4's section");

            Press("addsel.40000004");
            Eq(c.Data.Rules.Count, rules + 1, "again: no duplicate");
            Check(BodyText(w).Contains("already in pack 4"), "the window says it is there already");

            AddOutcome viaCommand = panel.AddSelected(2);
            Check(viaCommand.Status == AddStatus.Added && c.Data.Rules.Any(r => r.Pack == 2 && r.HasName("Black Opal")), "the command's path adds to the pack it names");
            Check(viaCommand.Message.Contains("pack 4"), $"and says pack 4 had it ({viaCommand.Message})");
            Eq(panel.AddSelected(9).Status, AddStatus.Refused, "a pack that doesn't exist");

            // The display list still goes through the engine's parser.
            ParseAll(host, "add selected", out _);
        }
    }
}
