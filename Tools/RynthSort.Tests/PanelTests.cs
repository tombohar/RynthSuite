using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Ui;
using RynthCore.Plugin.RynthSort.Views;
using RynthCore.Plugin.Shared;

namespace RynthSort.Tests
{
    // The redesigned window (#rynth-issues "The bag sort feature is lacking in UX"): what the status
    // line says, the four pages, starter layouts through the window, the moves preview, items left
    // alone, the kind picker, Undo and Put back, all driven by widget key as the engine sends them;
    // and op level 3 (tables, a bold status) only when the engine has it.
    internal static partial class Program
    {
        /// <summary>A window on a pretend game: the controller has read the packs once (auto-sort off).</summary>
        private static (SortController C, SortPanel Panel, UiHost Host, UiWindow W, FakeWorld World) Live(bool rules = true)
        {
            var world = new FakeWorld();
            world.Inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            world.Inv.Main.Items.Add(It(11, "Prismatic Taper", ObjectClasses.SpellComponent));
            world.Inv.Packs[1].Items.Add(It(12, "Salvaged Iron", ObjectClasses.Salvage));
            SortController c = Controller(auto: false);
            if (!rules) c.Data.Rules.Clear();
            Ticks(c, world, 0, 1000);
            var panel = new SortPanel(c, () => { }) { Character = () => "Tester", Now = () => c.Clock };
            var host = new UiHost();
            UiWindow w = host.Add(new UiWindow("RynthSort/Main", "RynthSort") { Visible = true });
            return (c, panel, host, w, world);
        }

        private static Action<ushort, string, byte[]> Sender(UiHost host, UiWindow w, SortPanel panel)
        {
            uint seq = 100;
            return (type, id, payload) =>
            {
                Apply(host, Event(type, ++seq, w.Hash, w.KeyOf(id), payload));
                Draw(w, panel);
            };
        }

        private static void PanelStatusWords()
        {
            SortController c = Sorter();
            var v = new SortView();
            Eq(SortPanel.Headline(c, v, false).Text, "Off: nothing moves by itself", "off");
            Eq(SortPanel.Headline(c, v, false).Color, UiColors.Grey, "off is grey");
            c.Data.Settings.Auto = true;
            var w = new FakeWorld();
            c.Tick(w, 0);
            Eq(SortPanel.Headline(c, c.Planner.View, false).Text, "On: all tidy", "on, nothing to do");
            Eq(SortPanel.Headline(c, c.Planner.View, false).Color, UiColors.Green, "green");
            Eq(SortPanel.Headline(c, c.Planner.View, true).Text, "On: no rules yet", "on without rules says so");
            v.WaitSettle = 2;
            v.NextSettleMs = 2100;
            Eq(SortPanel.Headline(c, v, false).Text, "On: 2 new items move in 3 s", "settling, with the time");
            v.WaitSettle = 1;
            Eq(SortPanel.Headline(c, v, false).Text, "On: 1 new item moves in 3 s", "one settling");
            v.WaitFull = 3;
            var (stuck, color) = SortPanel.Headline(c, v, false);
            Check(stuck == "On: 3 items can't move" && color == UiColors.Amber, "stuck items: amber");
            c.StartJob(SortScope.Clean);
            Eq(SortPanel.Headline(c, v, false).Text, "Cleaning packs", "a job says which");
            Eq(SortPanel.Headline(c, v, false).Color, UiColors.Blue, "blue while working");

            var paused = new FakeWorld { Gate = new GateInputs { Looting = true } };
            SortController p = Controller();
            p.Tick(paused, 0);
            Eq(SortPanel.Headline(p, p.Planner.View, false).Text, "Paused: a corpse or chest is open", "paused, in plain words");
            Eq(SortPanel.PausedWords("RynthAi is buffing"), "RynthAi is buffing", "RynthAi's reasons as they are");
            Eq(SortPanel.PausedWords("busy"), "you are casting or using something", "busy");

            var d = new SortView { WaitFull = 1, WaitNoPack = 2 };
            List<string> lines = SortPanel.Details(c, d, null, true);
            Check(lines[0].StartsWith("No rules yet", StringComparison.Ordinal), "no rules: said first");
            Check(lines.Contains("1 item waits for a full pack; 2 items for a pack you don't carry."), $"what waits, in one line ({string.Join(" | ", lines)})");
            Eq(SortPanel.Count(1, "item"), "1 item", "one");
            Eq(SortPanel.Ago(30_000) + "," + SortPanel.Ago(150_000), "just now,2 min ago", "how long ago");
        }

        private static void PanelLayouts()
        {
            var (c, panel, host, w, _) = Live(rules: false);
            var send = Sender(host, w, panel);
            Draw(w, panel);
            ParseAll(host, "no rules", out _);
            string body = BodyText(w);
            Eq(panel.Tab, PanelTab.Layouts, "no rules: the window opens on the layouts");
            Check(body.Contains("Everyday  (a good start)") && body.Contains("Hunting") && body.Contains("Crafting") && body.Contains("Just the basics"), "the layouts listed");
            Check(body.Contains("No rules yet"), "and says there are no rules");
            Check(!body.Contains("###layout.use"), "nothing to use until one is picked");
            w.MarkSubmitted(true);

            send(1, "layout.everyday", Click);
            ParseAll(host, "a layout's preview", out _);
            body = BodyText(w);
            Check(panel.PickedLayout == "everyday", "picked");
            Check(body.Contains("Everyday on your 3 packs") && body.Contains("Spell components") && body.Contains("Gems, mana stones and keys"), "where each kind goes");
            Check(body.Contains("No pack left for trade notes and coins; healing kits, lockpicks and food; scrolls and books: they stay in the main pack."), "what has no pack");
            Check(body.Contains("Pack 2") && body.Contains("Salvage") , "salvage stays in pack 2, which holds it");
            Check(body.Contains("2 items would move; 1 already in place."), "how much would move");
            Check(body.Contains("Use this layout###layout.use") && body.Contains("Cancel###layout.cancel"), "use or cancel");
            Check(!body.Contains("This replaces"), "no rules to replace");
            Eq(c.Data.Rules.Count, 0, "looking changes nothing");

            send(1, "layout.cancel", Click);
            Check(panel.PickedLayout == null && !BodyText(w).Contains("###layout.use"), "Cancel closes it");
            send(1, "layout.everyday", Click);
            send(1, "layout.use", Click);
            Eq(c.Data.Rules.Count, 3, "Use this layout: one rule per pack");
            Check(c.Data.Rules.Any(r => r.PackId == 0x40000002 && r.HasClass(ObjectClasses.Salvage)), "salvage tied to the pack that holds it");
            Check(!c.Data.Settings.Auto && c.Job == null, "nothing moves: auto-sort stays off, no job");
            Eq(panel.Tab, PanelTab.Packs, "back to the packs, to see the rules");
            body = BodyText(w);
            Check(body.Contains("Rules set from Everyday") && body.Contains("Nothing has moved yet"), "it says what happened");
            Check(body.Contains("Undo: the Everyday layout###undo"), "and offers Undo");
            Check(body.Contains("Spell Component  (1 to move in)") || body.Contains("Spell Component  (1"), "a rule says what it would bring in");

            send(1, "undo", Click);
            Eq(c.Data.Rules.Count, 0, "Undo: the rules as they were (none)");
            Check(!BodyText(w).Contains("Undo:"), "Undo is used up");

            // With rules already: picking says they are replaced.
            c.Data.Rules.AddRange(SortRules.Defaults());
            send(1, "tab.layouts", Click);
            send(1, "layout.basics", Click);
            Check(BodyText(w).Contains("This replaces your 3 rules (Undo brings them back)."), "replacing rules is said first");

            // From the packs page with no rules: a button to the layouts.
            c.Data.Rules.Clear();
            send(1, "tab.packs", Click);
            Check(BodyText(w).Contains("Pick a starter layout###go.layouts"), "no rules: a way to the layouts on the packs page");
            send(1, "go.layouts", Click);
            Eq(panel.Tab, PanelTab.Layouts, "and it goes there");

            // No side packs: nowhere to put things.
            var bare = new FakeWorld { Inv = Inv(0) };
            bare.Inv.Main.Items.Add(It(1, "Black Opal", ObjectClasses.Gem));
            SortController c0 = Controller(auto: false);
            c0.Data.Rules.Clear();
            Ticks(c0, bare, 0, 1000);
            var p0 = new SortPanel(c0, () => { }) { Character = () => "Someone" };
            var h0 = new UiHost();
            UiWindow w0 = h0.Add(new UiWindow("RynthSort/Main", "RynthSort") { Visible = true });
            Draw(w0, p0);
            ParseAll(h0, "no packs", out _);
            Check(BodyText(w0).Contains("You carry no side packs"), "no packs: says so");
        }

        private static void PanelMoves()
        {
            var (c, panel, host, w, world) = Live();
            var send = Sender(host, w, panel);
            Draw(w, panel);
            Eq(panel.Tab, PanelTab.Packs, "with rules: the packs page first");
            string body = BodyText(w);
            Check(body.Contains("Moves (2)###tab.moves"), "the page button counts the moves");
            Check(body.Contains("2 items would move when you sort (see Moves)."), "the status says what a sort would do");
            send(1, "tab.moves", Click);
            ParseAll(host, "moves page", out _);
            body = BodyText(w);
            Check(body.Contains("A sort would move 2 items:"), "how many");
            Check(body.Contains("Black Opal") && body.Contains("main pack -> pack 3"), "the opal: from and to");
            Check(body.Contains("Prismatic Taper") && body.Contains("main pack -> pack 1") && !body.Contains("Salvaged Iron"), "the taper to pack 1; the salvage is in its pack already");
            Check(body.Contains("Nothing moves until you press Sort now or turn on Auto-sort."), "and that nothing moves yet");
            Check(body.Contains("Its rule: Gem, Key, Mana Stone."), "a move's tooltip says which rule");
            Check(!ContainsOp(w.Body, UiFormat.OpTable) && !ContainsOp(w.Body, UiFormat.OpPushFont), "op level 2: no tables, no fonts");

            // The player selects the opal: it is left alone, and the page says so.
            c.Touch.NoteSelected(10, 1100);
            Ticks(c, world, 1100, 2200);
            Draw(w, panel);
            ParseAll(host, "left alone", out _);
            body = BodyText(w);
            Check(body.Contains("Left alone") && body.Contains("you selected it just now; would go to pack 3"), "left alone, how and where it would go");
            Check(body.Contains("A sort would move 1 item:"), "and it isn't counted as a move");
            Check(body.Contains("for 3 minutes (Settings)"), "for how long");
            Check(body.Contains("1 item left alone because you used it lately (see Moves)."), "the status says so too");
            send(1, "hold.release", Click);
            Check(!c.IsHeld(10) && !BodyText(w).Contains("Left alone"), "Sort them too: no longer held");

            // A sort, then the log and Put back.
            send(1, "job.all", Click);
            Ticks(c, world, 2200, 6000);
            Draw(w, panel);
            body = BodyText(w);
            Check(body.Contains("Nothing to move: every item is in its pack."), "sorted: nothing left to move");
            Check(body.Contains("Black Opal -> pack 3") && body.Contains("Packs sorted"), "the moves in Done lately");
            Check(body.Contains("Put back###job.back") && body.Contains("Put back 2 items###moves.back"), "Put back beside Sort now and on this page");
            send(1, "moves.back", Click);
            Check(c.Job == SortScope.PutBack && !c.Data.Settings.Auto, "Put back starts (auto-sort off)");
            Ticks(c, world, 6000, 10_000);
            Check(world.Inv.Main.Items.Any(i => i.Id == 10) && world.Inv.Main.Items.Any(i => i.Id == 11), "the two items are back in the main pack");
            Draw(w, panel);
            Check(!BodyText(w).Contains("Put back###job.back"), "nothing left to put back: no button");

            // Op level 3: the list is a table and the status bold; still accepted by the engine.
            panel.OpLevel = () => 3;
            Draw(w, panel);
            ParseAll(host, "op level 3", out _);
            Check(ContainsOp(w.Body, UiFormat.OpTable) && ContainsOp(w.Body, UiFormat.OpTableNextRow) && ContainsOp(w.Body, UiFormat.OpPushFont), "op level 3: a table and the bold status");
            body = BodyText(w);
            Check(body.Contains("Item") && body.Contains("From") && body.Contains("To"), "the table's columns");
            int engineLevel = DisplayListParser.OpLevel;   // 2 with the sibling checkout, 3 with -p:EngineParser=<an op level 3 engine>
            if (engineLevel >= 3)
            {
                List<ParsedWindow> parsed = ParseWindows(host);
                var ops = parsed[0].List!.Ops.Take(parsed[0].List!.OpCount).Select(o => (byte)o.Code).ToList();
                Check(ops.Contains(UiFormat.OpTable) && ops.Count(o => o == UiFormat.OpTableNextRow) == 2 && ops.Contains(UiFormat.OpPushFont),
                    "an op level 3 engine keeps the table, its two rows and the font");
            }
        }

        private static void PanelPacks()
        {
            var (c, panel, host, w, world) = Live();
            panel.Icons = () => true;
            var send = Sender(host, w, panel);
            Draw(w, panel);
            ParseAll(host, "packs page", out _);
            string body = BodyText(w);
            Check(body.Contains("Goes here:"), "what goes in each pack");
            Check(body.Contains("Salvage  (1 here)"), "a rule says how many items it keeps there");
            Check(body.Contains("Gem, Key, Mana Stone  (1 to move in)"), "and how many would move in");
            Check(body.Contains("###kind.40000001") && body.Contains("Add a kind of item..."), "a kind picker per pack");
            Check(body.Contains("Salvaged Iron"), "the pack's contents strip names its items");

            // Gems to pack 1 with the kind picker: taken from pack 3's rule.
            SortRule gems = c.Data.Rules.First(r => r.HasClass(ObjectClasses.Gem));
            int pick = ObjectClasses.Choices.Where(x => x != ObjectClasses.SpellComponent).ToList().IndexOf(ObjectClasses.Gem) + 1;
            send(3, "kind.40000001", BitConverter.GetBytes(pick));
            Check(!gems.HasClass(ObjectClasses.Gem) && gems.HasClass(ObjectClasses.Key), "pack 3's rule gave up Gem");
            Check(c.Data.Rules.Any(r => r.HasClass(ObjectClasses.Gem) && r.HasClass(ObjectClasses.SpellComponent) && SortPanel.ShownPack(c.Planner.View, r) == 1),
                "and pack 1's rule took it");
            Check(BodyText(w).Contains("Gems now go here (taken from pack 3). Nothing has moved yet."), "it says so under the pack");
            Check(BodyText(w).Contains("Undo: Gems to pack 1###undo"), "with Undo");
            send(1, "undo", Click);
            Check(c.Data.Rules.Count == 3 && c.Data.Rules.Single(r => r.HasClass(ObjectClasses.Gem)).HasClass(ObjectClasses.Key), "Undo: back as it was");
            send(3, "kind.40000001", BitConverter.GetBytes(0));
            Eq(c.Data.Rules.Count(r => r.HasClass(ObjectClasses.Gem)), 1, "the picker's first line does nothing");

            // A kind no pack had goes into the pack's rule; a pack with no rule gets a new one.
            send(3, "kind.40000003", BitConverter.GetBytes(ObjectClasses.Choices.Where(x => x != ObjectClasses.Gem && x != ObjectClasses.ManaStone && x != ObjectClasses.Key).ToList().IndexOf(ObjectClasses.Lockpick) + 1));
            Check(c.Data.Rules.Single(r => r.HasClass(ObjectClasses.Gem)).HasClass(ObjectClasses.Lockpick), "lockpicks join pack 3's rule");
            world.Inv.Packs.Add(new SortPack { Id = 0x40000004, Name = "Pack 4", Capacity = 24 });
            Ticks(c, world, 1000, 2500);
            Draw(w, panel);
            Check(BodyText(w).Contains("Nothing goes here yet."), "a new pack: nothing goes there yet");
            int before = c.Data.Rules.Count;
            send(3, "kind.40000004", BitConverter.GetBytes(ObjectClasses.ChoiceIndex(ObjectClasses.Food) + 1));
            Check(c.Data.Rules.Count == before + 1 && c.Data.Rules[^1].HasClass(ObjectClasses.Food) && c.Data.Rules[^1].PackId == 0x40000004, "a pack with no rule gets one");

            // Removing a rule can be undone.
            SortRule food = c.Data.Rules[^1];
            send(1, $"r{food.Uid}.row", Click);
            send(1, $"r{food.Uid}.remove", Click);
            Check(!c.Data.Rules.Contains(food) && BodyText(w).Contains("Undo: rule removed"), "removed, with Undo");
            send(1, "undo", Click);
            Check(c.Data.Rules.Any(r => r.HasClass(ObjectClasses.Food) && r.PackId == 0x40000004), "and back");

            // The setting for leaving items alone.
            send(1, "tab.settings", Click);
            send(3, "hold", BitConverter.GetBytes(12));
            Eq(c.Data.Settings.HoldMin, 12, "Leave alone (min)");
            send(3, "hold", BitConverter.GetBytes(500));
            Eq(c.Data.Settings.HoldMin, SortSettings.HoldMinMax, "clamped");
            Check(BodyText(w).Contains("stays put for 30 minutes"), "the line under it says what it does");
            send(3, "hold", BitConverter.GetBytes(0));
            Check(BodyText(w).Contains("Off: an item you take out of its pack goes back at the next sort."), "and when it is off");

            // Another character: the window starts fresh.
            panel.Character = () => "Someone";
            c.Data.Rules.Clear();
            Draw(w, panel);
            Eq(panel.Tab, PanelTab.Layouts, "another character with no rules: the layouts");
            Check(panel.UndoWhat.Length == 0, "and no Undo from the last one");
        }

        private static void PanelBudget()
        {
            // A full character: eight packs of 24, a full main pack, 64 rules, icons, every page.
            var world = new FakeWorld { Inv = Inv(8, cap: 40) };
            uint id = 1;
            for (int p = 0; p < 8; p++)
                for (int i = 0; i < 24; i++) world.Inv.Packs[p].Items.Add(It(id++, $"Thing {p}-{i}", ObjectClasses.Choices[(p + i) % ObjectClasses.Choices.Length]));
            for (int i = 0; i < 102; i++) world.Inv.Main.Items.Add(It(id++, $"Loot {i}", ObjectClasses.Choices[i % ObjectClasses.Choices.Length]));
            SortController c = Controller(auto: false);
            c.Data.Rules.Clear();
            for (int i = 0; i < SortStore.MaxRules; i++)
                c.Data.Rules.Add(SortRule.For(1 + i % 8, new[] { ObjectClasses.Choices[i % ObjectClasses.Choices.Length] }, "x" + i));
            Ticks(c, world, 0, 1000);
            var panel = new SortPanel(c, () => { }) { Character = () => "Tester", Icons = () => true, CanSelect = () => true };
            var host = new UiHost();
            UiWindow w = host.Add(new UiWindow("RynthSort/Main", "RynthSort") { Visible = true });
            foreach (int level in new[] { 2, 3 })
            {
                panel.OpLevel = () => level;
                foreach (PanelTab tab in Enum.GetValues<PanelTab>())
                {
                    panel.ShowTab(tab);
                    Draw(w, panel);
                    ParseAll(host, $"level {level}, {tab}", out int ops);
                    Check(ops < 1500 && !BodyText(w).Contains("more than fits in one window"), $"level {level}, {tab}: fits ({ops} ops)");
                    Console.WriteLine($"         level {level}, {tab}: {ops} ops, {w.Body.Length} bytes");
                    w.MarkSubmitted(true);
                }
                panel.ShowTab(PanelTab.Layouts);
                Apply(host, Event(1, 1, w.Hash, w.KeyOf("layout.everyday"), Click));
                Draw(w, panel);
                ParseAll(host, $"level {level}, a layout preview", out int lops);
                Check(lops < 1500, $"level {level}: a layout preview fits ({lops} ops)");
                Apply(host, Event(1, 2, w.Hash, w.KeyOf("layout.cancel"), Click));
            }
        }
    }
}
