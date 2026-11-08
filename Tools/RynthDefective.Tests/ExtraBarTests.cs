using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthDefective;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.ShortcutBar;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;
using RynthCore.Plugin.RynthDefective.Views;

namespace RynthDefective.Tests
{
    // The extra Shortcuts bars (2026-11-04, ExtraBar): adding and removing them (at most 3, the lowest
    // free number), their own slots filled and used like the main bar's own, the character's file, the
    // /rdef bars command, what an extra bar's window draws, and the settings window's rows for them.
    internal static partial class Program
    {
        private static void ExtraBarsAddRemoveCap()
        {
            var d = new DefData();
            Eq(d.ExtraBars.Count, 0, "none by default");
            var numbers = new List<int>();
            for (int i = 0; i < ExtraBar.Max; i++) numbers.Add(d.AddExtraBar()?.Number ?? 0);
            Eq(string.Join(",", numbers), "2,3,4", "three bars, numbered 2-4 (the main bar is 1)");
            Check(d.AddExtraBar() == null && d.ExtraBars.Count == ExtraBar.Max, "a fourth is refused: the cap");
            Check(ExtraBar.Max + 5 <= 8, "the cap fits the engine's 8 windows for a plugin (RynthDefective draws 5 others, Day/Night included)");
            Check(d.RemoveExtraBar(3) && d.Extra(3) == null && d.ExtraBars.Count == 2, "remove bar 3");
            Check(!d.RemoveExtraBar(3), "removing it again: nothing to remove");
            Eq(d.AddExtraBar()?.Number, 3, "the next added takes the free number (its window comes back where it was)");
            Eq(string.Join(",", d.ExtraBars.Select(b => b.Number)), "2,3,4", "kept in number order");
            ExtraBar b2 = d.Extra(2)!;
            Eq(b2.SlotCount, ExtraBar.DefaultSlots, "10 slots to start");
            Eq(b2.Slots.Count, ExtraBar.SlotsMax, "always 24 slots kept (fewer shown hides them)");
            Eq(b2.Title, "Shortcuts 2", "its window's title");
            Eq(ExtraBar.SurfaceOf(4), "window:bar4", "its surface");

            // The session's words for it, and the saves.
            var rig = new Rig("xbars");
            string m = rig.S.AddBar(out int n);
            Check(n == 2 && rig.S.Data.Extra(2) != null && rig.Saves == 1, "session: bar 2 added and saved");
            Check(m.Contains("Shortcuts bar 2 added") && m.Contains("drag items"), "it says how to fill it: " + m);
            for (int i = 0; i < 2; i++) rig.S.AddBar(out _);
            int saves = rig.Saves;
            m = rig.S.AddBar(out n);
            Check(n == 0 && rig.Saves == saves && rig.S.Data.ExtraBars.Count == 3, "at the cap: not added, not saved");
            Check(m.Contains("Remove one first") && m.Contains("8 windows"), "and it says why: " + m);

            m = rig.S.RemoveBar(1);
            Check(m.Contains("can't be removed") && rig.S.Data.ExtraBars.Count == 3, "bar 1 is the Shortcuts bar itself: " + m);
            m = rig.S.RemoveBar(9);
            Check(m.StartsWith("There is no Shortcuts bar 9", StringComparison.Ordinal) && m.Contains("Extra Shortcuts bars: 2"), "no such bar: it lists them: " + m);
            rig.Inv = new List<CarriedItem> { Kit(0x7400_0001u) };
            rig.S.SetFromId(1, 0x7400_0001u, bar: 4);
            m = rig.S.RemoveBar(4);
            Check(rig.S.Data.Extra(4) == null && m.Contains("1 of its slots") && m.Contains("forgotten"), "a bar that held something: says it's forgotten: " + m);
            m = rig.S.RemoveBar(3);
            Check(m == "Shortcuts bar 3 removed.", "an empty one: " + m);
            Check(rig.S.DescribeBars().Contains("2 (10 slots, 0 set)") && !rig.S.DescribeBars().Contains("3 (10 slots") && rig.S.DescribeBars().Contains("2 more can be added"), "the list: " + rig.S.DescribeBars());

            m = rig.S.SetBarSlots(2, 12);
            Check(rig.S.Data.Extra(2)!.SlotCount == 12 && m == "Shortcuts bar 2 shows 12 slots.", "bar 2 shows 12: " + m);
            m = rig.S.SetBarSlots(2, 99);
            Check(rig.S.Data.Extra(2)!.SlotCount == ExtraBar.SlotsMax && m.Contains("1 to 24"), "too many: the most: " + m);
            Check(rig.S.SetBarSlots(7, 3).StartsWith("There is no Shortcuts bar 7", StringComparison.Ordinal), "slots of a bar that isn't there");

            var none = new Rig("xbars-none");
            Check(none.S.DescribeBars().Contains("/rdef bars add"), "no bars: how to add one");
        }

        private static void ExtraBarsSlots()
        {
            var rig = new Rig("xbar-slots");
            var kit = Kit(0x7410_0001u);
            var potion = Potion(0x7410_0002u);
            rig.Inv = new List<CarriedItem> { kit, potion };
            rig.S.AddBar(out _);

            rig.Selected = kit.Id;
            string m = rig.S.SetFromSelected(1, bar: 2);
            Check(rig.S.Data.Extra(2)!.Slots[0].Item?.Id == kit.Id && rig.S.Data.Slots[0].Empty, "the selected kit goes in bar 2's slot 1, not the main bar's");
            Check(m.StartsWith("Bar 2 slot 1: Peerless Healing Kit", StringComparison.Ordinal), "named by its bar: " + m);
            m = rig.S.SetFromId(3, potion.Id, bar: 2);
            Check(rig.S.Data.Extra(2)!.Slots[2].Item?.Id == potion.Id && m.StartsWith("Bar 2 slot 3:", StringComparison.Ordinal), "an item dropped on bar 2's slot 3");
            m = rig.S.SetSpell(2, 1, bar: 2);   // the spellbook can't be read here: any spell id goes in
            Check(rig.S.Data.Extra(2)!.Slots[1].IsSpell && m.StartsWith("Bar 2 slot 2:", StringComparison.Ordinal), "a spell in bar 2's slot 2: " + m);
            Check(rig.S.SetFromId(1, potion.Id, bar: 3).StartsWith("There is no Shortcuts bar 3", StringComparison.Ordinal), "a bar that isn't there");
            Check(rig.S.SetFromId(25, potion.Id, bar: 2) == "Bar 2 has no slot 25.", "a slot past 24");
            Check(rig.S.Data.Slots.All(s => s.Empty), "the main bar's own slots are untouched");

            // Pressing: through the use controller, as the main bar's own slots.
            Check(rig.S.UseSlot(1, bar: 2) == null && rig.S.Use.Pending?.Item?.Id == kit.Id && rig.S.Use.Pending?.Source == "bar 2 slot 1", "a click uses bar 2's kit: " + rig.S.Use.Pending?.Source);
            rig.S.Use.Reset();
            Check(rig.S.UseSlot(2, bar: 2) == null && rig.S.Use.Pending?.Spell == 1, "a spell slot casts");
            rig.S.Use.Reset();
            m = rig.S.UseSlot(4, bar: 2)!;
            Check(m.StartsWith("Bar 2 slot 4 is empty", StringComparison.Ordinal) && m.Contains("right-click"), "an empty one says how to fill it: " + m);
            Check(rig.S.UseSlot(1, bar: 5)!.StartsWith("There is no Shortcuts bar 5", StringComparison.Ordinal), "a bar that isn't there");

            m = rig.S.ClearSlot(3, bar: 2);
            Check(rig.S.Data.Extra(2)!.Slots[2].Empty && m == "Bar 2 slot 3 cleared.", "cleared: " + m);

            // The slots follow their items: the kit used up, the next of its name is the one.
            var kit2 = Kit(0x7410_0009u);
            rig.Inv = new List<CarriedItem> { kit2, potion };
            Check(rig.S.Maintain(rig.Inv) && rig.S.Data.Extra(2)!.Slots[0].Item?.Id == kit2.Id, "an extra bar's slot follows its item like the main bar's");

            // The main bar's own slots still read as before.
            Check(rig.S.SetFromId(1, potion.Id) == "Slot 1: Health Potion (used when pressed).", "the main bar's words unchanged: " + rig.S.SetFromId(1, potion.Id));
            Check(rig.S.UseSlot(43) == "There is no slot 43.", "and its limits");
        }

        private static void ExtraBarsStore()
        {
            var st = new DefStore(Path.Combine(_temp, "xbar-store"));
            var d = new DefData();
            ExtraBar a = d.AddExtraBar()!;
            a.SlotCount = 12;
            a.Slots[0].Item = ItemRef.From(Kit(0x7420_0001u));
            a.Slots[0].Mode = UseMode.OnSelf;
            a.Slots[11] = new DefSlot { SpellId = 1, SpellName = "Strength Other I" };
            a.Slots[23].Item = ItemRef.From(Potion(0x7420_0002u));   // past the shown count: kept
            d.AddExtraBar();
            ExtraBar c = d.AddExtraBar()!;
            c.SlotCount = 3;
            d.RemoveExtraBar(3);   // bars 2 and 4 are left
            d.Slots[0].Item = ItemRef.From(Potion(0x7420_0003u));

            Check(st.Save("TestServer", "Test", d) == null, "saved");
            DefData r = st.Load("TestServer", "Test", out string? problem);
            Check(problem == null, "read back");
            Eq(string.Join(",", r.ExtraBars.Select(b => b.Number)), "2,4", "bars 2 and 4");
            ExtraBar ra = r.Extra(2)!, rc = r.Extra(4)!;
            Check(ra.SlotCount == 12 && rc.SlotCount == 3, "their slot counts");
            Check(ra.Slots[0].Item?.Id == 0x7420_0001u && ra.Slots[0].Mode == UseMode.OnSelf, "an item and its mode");
            Check(ra.Slots[11].SpellId == 1 && ra.Slots[11].SpellName.Length > 0, "a spell slot");
            Check(ra.Slots[23].Item?.Id == 0x7420_0002u, "a slot past the shown count is kept");
            Check(ra.Slots.Count == ExtraBar.SlotsMax && rc.Filled == 0, "24 slots each; bar 4 empty");
            Check(r.Slots[0].Item?.Id == 0x7420_0003u, "the main bar's own slots alongside");
            Eq(DefStore.ToJson(r), DefStore.ToJson(d), "the round trip writes the same file");

            // A file from before the extra bars: none. Odd entries: skipped, clamped, capped.
            var old = DefStore.Parse(JsonDocument.Parse("{\"slots\":[]}").RootElement);
            Eq(old.ExtraBars.Count, 0, "a file from before has none");
            string odd = "{\"extraBars\":[\"x\", {\"number\":1}, {\"number\":9}, {\"number\":3,\"slotCount\":99,\"slots\":[{\"item\":{\"id\":5,\"name\":\"Arrow\"}}]},"
                         + "{\"number\":3}, {\"number\":2,\"slotCount\":0}, {\"number\":4,\"slots\":\"none\"}, {\"number\":5}, {\"number\":4}]}";
            DefData o = DefStore.Parse(JsonDocument.Parse(odd).RootElement);
            Eq(string.Join(",", o.ExtraBars.Select(b => b.Number)), "2,3,4", "bad numbers (1, 9 and 5) and repeats skipped, sorted, at most 3");
            Check(o.Extra(3)!.SlotCount == ExtraBar.SlotsMax && o.Extra(3)!.Slots[0].Item?.Name == "Arrow", "too many slots: the most; its item read");
            Check(o.Extra(2)!.SlotCount == ExtraBar.SlotsMin, "too few: one");
            Check(o.Extra(4)!.Slots.All(s => s.Empty) && o.Extra(4)!.SlotCount == ExtraBar.DefaultSlots, "slots that aren't a list: empty, the default count");
        }

        private static void ExtraBarsCommands()
        {
            DefCommand c = DefCommandParser.Parse("bars");
            Check(c.Verb == DefVerb.Bars && c.Text == "list", "/rdef bars lists");
            Check(DefCommandParser.Parse("bars list").Text == "list", "/rdef bars list");
            Check(DefCommandParser.Parse("bars add").Text == "add", "/rdef bars add");
            c = DefCommandParser.Parse("bars remove 3");
            Check(c.Verb == DefVerb.Bars && c.Text == "remove" && c.Slot == 3, "/rdef bars remove 3");
            c = DefCommandParser.Parse("bars slots 2 12");
            Check(c.Verb == DefVerb.Bars && c.Text == "slots" && c.Slot == 2 && c.Count == 12, "/rdef bars slots 2 12");
            c = DefCommandParser.Parse("bars show 4");
            Check(c.Verb == DefVerb.Bars && c.Text == "show" && c.Slot == 4, "/rdef bars show 4");
            Check(DefCommandParser.Parse("bars hide 4").Text == "show", "hide is the same toggle");
            foreach (string wrong in new[] { "bars remove", "bars remove two", "bars slots 2", "bars slots 2 99", "bars slots 2 0", "bars show", "bars frob", "bars add 2" })
            {
                c = DefCommandParser.Parse(wrong);
                Check(c.Verb == DefVerb.Error && (c.Error.Contains("e.g.", StringComparison.Ordinal) || c.Error.Contains("Usage", StringComparison.Ordinal)),
                    $"'/rdef {wrong}' answers how it's typed: {c.Error}");
            }
            Check(DefCommandParser.Usage.Contains("bars"), "the usage line has it");

            // The surfaces the engine draws them by.
            Check(RynthDefectivePlugin.TryExtraSurface("window:bar2", out int s2) && s2 == 2, "window:bar2 is bar 2");
            Check(RynthDefectivePlugin.TryExtraSurface(ExtraBar.SurfaceOf(4), out int s5) && s5 == 4, "window:bar4 is bar 4");
            foreach (string not in new[] { "window:bar", "window:bar1", "window:bar6", "window:bar22", "window:panels", "window:barx" })
                Check(!RynthDefectivePlugin.TryExtraSurface(not, out _), $"{not} is no extra bar");
        }

        private static void ExtraBarSnapshot()
        {
            var rig = new Rig("xbar-snap");
            var kit = Kit(0x7430_0001u);
            rig.Inv = new List<CarriedItem> { kit };
            rig.S.AddBar(out _);
            rig.S.AddBar(out _);
            rig.S.SetFromId(2, kit.Id, bar: 3);
            rig.S.SetSpell(5, 1, bar: 3);

            BarSnapshot v = BarSnapshotBuilder.BuildExtra(rig.S, 3, rig.Inv, canCast: true);
            Check(v.Ready && v.Bar == 3 && v.Slots.Length == ExtraBar.DefaultSlots, "bar 3: ready, 10 slots");
            Check(v.MinSlots == ExtraBar.SlotsMin && v.MaxSlots == ExtraBar.SlotsMax, "its edit buttons go 1-24");
            Check(v.Slots.All(s => !s.Retail && s.Key.Length == 0 && s.AllKeys.Length == 0), "no game slots, no keys");
            Check(v.Slots[1].ObjectId == kit.Id && v.Slots[1].Count == "28", "slot 2: the kit and its uses left");
            Check(v.Slots[1].Tooltip.Contains("Click: use it on you") && !v.Slots[1].Tooltip.Contains("press the key") && !v.Slots[1].Tooltip.Contains("No key yet"),
                "its tooltip: clicks, no keys: " + v.Slots[1].Tooltip);
            Check(v.Slots[4].SpellId == 1 && !v.Slots[4].Tooltip.Contains("press the key"), "slot 5: the spell");
            Check(v.Slots[0].Empty && v.Slots[0].Tooltip.Contains("Drag an item"), "an empty slot says how to fill it");
            Eq(string.Join(",", v.Extras.Select(e => e.Number + ":" + e.Filled)), "2:0,3:2", "the bars there are, for the menus");
            Check(v.CanAddBar, "two more can be added");
            rig.S.SetBarSlots(3, 2);
            Eq(BarSnapshotBuilder.BuildExtra(rig.S, 3, rig.Inv, true).Slots.Length, 2, "fewer slots shown");
            Check(!BarSnapshotBuilder.BuildExtra(rig.S, 4, rig.Inv, true).Ready, "a bar that isn't there: not ready");
            rig.S.AddBar(out _);
            rig.S.AddBar(out _);
            Check(!BarSnapshotBuilder.BuildExtra(rig.S, 2, rig.Inv, true).CanAddBar, "at three: no more");
            Check(BarSnapshot.NotReady.Bar == 0 && BarSnapshot.NotReady.MaxSlots == DefSettings.BarSlotsMax, "the main bar's limits by default");
        }

        private static void ExtraBarSettings()
        {
            var b = new BarRig("xbar-settings");
            var host = new UiHost();
            UiWindow win = host.Add(new UiWindow("RynthDefective/Settings", "Defective Settings") { Visible = true, ShowInBar = false });
            var view = new BarSettingsView(b.S);
            var shown = new Dictionary<string, bool> { [BarSettingsView.BarTitle] = true };
            view.WindowShown = t => shown.TryGetValue(t, out bool v) ? v : null;
            view.ShowWindow = (t, v) => shown[t] = v;
            int adds = 0;
            var removed = new List<int>();
            view.AddBar = () => { adds++; b.S.AddBar(out _); };
            view.RemoveBar = n => { removed.Add(n); b.S.RemoveBar(n); };
            void Draw() { win.Begin(); view.Draw(win); win.End(); }
            string Text() => Encoding.UTF8.GetString(win.Body);
            var w = new Windows("xbar-settings-events");
            w.Host.Add(win);

            Draw();
            ParseAll(host, "settings window with no extra bars", out _);
            Check(Text().Contains("More shortcut bars") && Text().Contains("Add a shortcut bar"), "the section and its Add button");
            w.Click(win, "set.xbar.add");
            Eq(adds, 1, "Add a shortcut bar adds one");
            Draw();
            Check(Text().Contains("Shortcuts bar 2: opening..."), "not hosted yet: opening");
            shown[ExtraBar.TitleOf(2)] = false;
            Draw();
            ParseAll(host, "settings window with bar 2", out int ops);
            Check(ops < 1500, "inside the engine's limits");
            w.SetBool(win, "set.xbar.2.shown", true);
            Check(shown[ExtraBar.TitleOf(2)], "its box shows it");
            w.SetInt(win, "set.xbar.2.slots", 15);
            Check(b.S.Data.Extra(2)!.SlotCount == 15, "its slider sets its slots");
            w.Click(win, "set.xbar.2.remove");
            Draw();
            Check(Text().Contains("Remove Shortcuts bar 2?") && removed.Count == 0, "Remove asks first");
            w.Click(win, "set.xbar.2.no");
            Draw();
            Check(!Text().Contains("Remove Shortcuts bar 2?") && b.S.Data.Extra(2) != null, "Keep it keeps it");
            w.Click(win, "set.xbar.2.remove");
            Draw();
            w.Click(win, "set.xbar.2.yes");
            Check(removed.SequenceEqual(new[] { 2 }) && b.S.Data.Extra(2) == null, "Remove it removes it");

            for (int i = 0; i < ExtraBar.Max; i++) b.S.AddBar(out _);
            Draw();
            Check(!Text().Contains("Add a shortcut bar") && Text().Contains("as many as there can be"), "at three: no Add button, and why");

            // An engine without the Shortcuts bar: no section.
            shown.Remove(BarSettingsView.BarTitle);
            Draw();
            Check(!Text().Contains("More shortcut bars"), "no Shortcuts bar on this engine: no extra bars either");
        }
    }
}
