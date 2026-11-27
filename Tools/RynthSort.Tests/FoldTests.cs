using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // RynthSort 0.6.1: pack cards fold down to their header and rules (by the pack's object id, saved
    // with the character), and "+ Add a rule" takes a typed name (no item needed).
    internal static partial class Program
    {
        private static void FoldedPacks()
        {
            var s = new SortSettings();
            Check(s.CollapsedPacks.Count == 0 && !s.MainCollapsed, "nothing folded for a new character");
            Check(s.SetCollapsed(0x500, true) && !s.SetCollapsed(0x500, true) && s.CollapsedPacks.SequenceEqual(new uint[] { 0x500 }), "folded once");
            Check(!s.SetCollapsed(0, true), "no pack id: nothing");
            Check(s.SetCollapsed(0x500, false) && s.CollapsedPacks.Count == 0 && !s.SetCollapsed(0x500, false), "opened");
            for (uint i = 1; i <= SortSettings.MaxCollapsed + 5; i++) s.SetCollapsed(i, true);
            Check(s.CollapsedPacks.Count == SortSettings.MaxCollapsed && s.CollapsedPacks[0] == 6 && s.CollapsedPacks[^1] == SortSettings.MaxCollapsed + 5,
                "at most 64, the oldest dropped");

            // The file: saved and read back; an older file has none; odd values are skipped.
            var d = new CharacterSortData();
            d.Settings.MainCollapsed = true;
            d.Settings.SetCollapsed(0x80001234, true);
            d.Settings.SetCollapsed(7, true);
            using var doc1 = System.Text.Json.JsonDocument.Parse(SortStore.ToJson(d));
            CharacterSortData back = SortStore.Parse(doc1.RootElement);
            Check(back.Settings.MainCollapsed && back.Settings.CollapsedPacks.SequenceEqual(new uint[] { 0x80001234, 7 }), "saved and read back");
            using var doc0 = System.Text.Json.JsonDocument.Parse("{\"rules\":[],\"settings\":{\"auto\":true}}");
            CharacterSortData old = SortStore.Parse(doc0.RootElement);
            Check(!old.Settings.MainCollapsed && old.Settings.CollapsedPacks.Count == 0, "an older file: nothing folded");
            using var doc2 = System.Text.Json.JsonDocument.Parse("{\"settings\":{\"mainCollapsed\":\"yes\",\"collapsed\":[5,5,-1,\"x\",1.5,9]}}");
            CharacterSortData odd = SortStore.Parse(doc2.RootElement);
            Check(!odd.Settings.MainCollapsed && odd.Settings.CollapsedPacks.SequenceEqual(new uint[] { 5, 9 }), "odd values skipped");

            // The window's model: the folded pack by its id, wherever it sits in the column.
            SortInventory inv = Inv(3);
            SortController c = ScreenController(inv);
            c.Data.Settings.SetCollapsed(inv.Packs[1].Id, true);
            ScreenModel m = Screen(c);
            Check(!m.Packs[0].Collapsed && m.Packs[1].Collapsed && !m.Packs[2].Collapsed && !m.Main.Collapsed, "pack 2 folded, the rest open");
            c.Data.Settings.MainCollapsed = true;
            Check(Screen(c).Main.Collapsed, "the main pack folded");
        }

        private static void TypedNameRule()
        {
            SortInventory inv = Inv(3);
            inv.Main.Items.Add(It(1, "Pyreal Scarab", ObjectClasses.Misc));
            inv.Main.Items.Add(It(2, "Copper Scarab", ObjectClasses.Misc));
            var rules = new List<SortRule>();
            SortView view = SortPreview.Build(inv, rules, default, null).View;

            EditResult r = RuleEdits.SendNameText(rules, view, 2, "  Scarab ");
            Check(r.Changed && rules.Count == 1 && rules[0].HasName("Scarab") && rules[0].PackId == inv.Packs[1].Id, $"a new name rule tied to the pack: {r.Text}");
            Check(inv.Main.Items.All(i => SortRules.RuleFor(rules, i) == rules[0]), "both scarabs follow it");
            Check(!RuleEdits.SendNameText(rules, view, 2, "scarab").Changed, "again (any case): nothing changes");
            Check(!RuleEdits.SendNameText(rules, view, 2, "   ").Changed && !RuleEdits.SendNameText(rules, view, 9, "Gem").Changed, "blank, or a pack not carried");

            // The same name typed on another pack moves it there; the emptied rule goes.
            r = RuleEdits.SendNameText(rules, view, 3, "Scarab");
            Check(r.Changed && rules.Count == 1 && Views.SortPanelProxy.Shown(view, rules[0]) == 3, $"moved to pack 3: {r.Text}");

            // A second name joins the pack's name rule; too long a name is cut to the limit.
            RuleEdits.SendNameText(rules, view, 3, "Pea");
            Check(rules.Count == 1 && rules[0].Names.Count == 2, "joins the same rule");
            RuleEdits.SendNameText(rules, view, 1, new string('a', SortRule.MaxNameLength + 10));
            Check(rules.Any(x => x.Names.Any(n => n.Length == SortRule.MaxNameLength)), "cut to the name limit");

            // A name in a rule of this pack that is off: says so, changes nothing.
            SortRule here = rules.First(x => x.HasName("Pea"));
            here.On = false;
            r = RuleEdits.SendNameText(rules, view, 3, "Pea");
            Check(!r.Changed && r.Text.Contains("turned off"), $"off: {r.Text}");
        }
    }
}
