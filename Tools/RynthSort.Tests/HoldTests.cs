using System;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // "Leave my items alone" (#rynth-issues "The bag sort feature is lacking in UX": it moves the items
    // you're working with): what counts as the player touching an item, that sorting leaves those
    // items where they are for the hold time, and the setting in the file.
    internal static partial class Program
    {
        private const long Min = 60_000;

        private static void HoldWatch()
        {
            SortInventory inv = Inv(2);
            SortItem taper = It(10, "Prismatic Taper", ObjectClasses.SpellComponent);
            SortItem opal = It(11, "Black Opal", ObjectClasses.Gem);
            inv.Main.Items.Add(taper);
            inv.Packs[0].Items.Add(opal);
            inv.Worn.Add(20);
            var t = new TouchWatch();

            t.Observe(inv, 0);
            Check(!t.IsHeld(10, 0, 3 * Min) && !t.IsHeld(11, 0, 3 * Min), "the first read only learns where things are");

            // The player moves the opal to pack 2: held, as moved, for the hold time.
            inv.Packs[0].Items.Remove(opal);
            inv.Packs[1].Items.Add(opal);
            t.Observe(inv, 1000);
            Check(t.TryGetHeld(11, 1000, 3 * Min, out long at, out TouchKind kind) && at == 1000 && kind == TouchKind.Moved, "a pack change RynthSort didn't make: held, as moved");
            Check(t.IsHeld(11, 1000 + 3 * Min - 1, 3 * Min) && !t.IsHeld(11, 1000 + 3 * Min, 3 * Min), "until the hold time is up");
            Check(!t.IsHeld(11, 2000, 0), "a hold of 0 holds nothing");

            // RynthSort's own move isn't the player's.
            t.NoteOwnMove(10, inv.Packs[0].Id, 2000);
            inv.Main.Items.Remove(taper);
            inv.Packs[0].Items.Add(taper);
            t.Observe(inv, 2500);
            Check(!t.IsHeld(10, 2500, 3 * Min), "RynthSort's own move: not held");
            // ... but the same item moved again by the player is.
            inv.Packs[0].Items.Remove(taper);
            inv.Main.Items.Add(taper);
            t.Observe(inv, 3000);
            Check(t.IsHeld(10, 3000, 3 * Min), "moved back by the player: held");
            // An own move to somewhere else doesn't cover a move the player made.
            t.ReleaseAll();
            Check(!t.IsHeld(10, 3000, 3 * Min) && !t.IsHeld(11, 3000, 3 * Min), "Sort them too: every hold dropped");
            t.NoteOwnMove(10, inv.Packs[1].Id, 3100);
            inv.Main.Items.Remove(taper);
            inv.Packs[0].Items.Add(taper);
            t.Observe(inv, 3200);
            Check(t.IsHeld(10, 3200, 3 * Min), "RynthSort sent it to pack 2 but it is in pack 1: the player's");

            // Taken off: worn at the last read, in a pack now.
            inv.Worn.Remove(20);
            SortItem shield = It(20, "Shield", ObjectClasses.Armor);
            inv.Main.Items.Add(shield);
            t.Observe(inv, 4000);
            Check(t.TryGetHeld(20, 4000, 3 * Min, out _, out kind) && kind == TouchKind.TakenOff, "taken off: held");

            // New items: only just after a drop (picked up, a stack split off).
            inv.Main.Items.Add(It(30, "Pyreal Mote", ObjectClasses.Misc));
            t.Observe(inv, 5000);
            Check(!t.IsHeld(30, 5000, 3 * Min), "new loot is not the player's");
            t.NoteDrop(10, 6000);
            Check(t.TryGetHeld(10, 6000, 3 * Min, out _, out kind) && kind == TouchKind.Dragged, "the dropped item itself: held");
            inv.Main.Items.Add(It(31, "Prismatic Taper", ObjectClasses.SpellComponent, stack: 5, max: 100));
            t.Observe(inv, 6500);
            Check(t.TryGetHeld(31, 6500, 3 * Min, out _, out kind) && kind == TouchKind.PickedUp, "new just after a drop (a split stack): held");
            inv.Main.Items.Add(It(32, "Prismatic Taper", ObjectClasses.SpellComponent));
            t.Observe(inv, 6000 + TouchWatch.DropWindowMs + 1);
            Check(!t.IsHeld(32, 6000 + TouchWatch.DropWindowMs + 1, 3 * Min), "new well after the drop: not held");

            // Drags and selections.
            t.NoteDrag(11, 7000);
            Check(t.TryGetHeld(11, 7000, 3 * Min, out _, out kind) && kind == TouchKind.Dragged, "dragged: held");
            t.NoteSelected(30, 8000);
            Check(t.TryGetHeld(30, 8000, 3 * Min, out at, out kind) && kind == TouchKind.Selected && at == 8000, "selected: held");
            t.NoteSelected(30, 9000);
            Check(t.TryGetHeld(30, 9000, 3 * Min, out at, out _) && at == 9000, "still selected: the hold counts from the latest tick");
            t.NoteSelected(0x7000_0001, 9000);
            t.NoteSelected(20 + 1000, 9000);
            Eq(t.HeldCount(9000, 3 * Min), 5, "a selected monster or unknown object isn't counted");
            t.NoteSelected(inv.Packs[0].Id, 9000);
            Check(!t.IsHeld(inv.Packs[0].Id, 9000, 3 * Min), "a selected pack isn't an item");

            // A pack missing from a read for a moment: its items are remembered.
            SortItem salvage = It(40, "Salvaged Iron", ObjectClasses.Salvage);
            inv.Packs[1].Items.Add(salvage);
            t.Observe(inv, 10_000);
            inv.Packs[1].Items.Remove(salvage);
            t.Observe(inv, 11_000);
            inv.Main.Items.Add(salvage);
            t.Observe(inv, 12_000);
            Check(t.TryGetHeld(40, 12_000, 3 * Min, out _, out kind) && kind == TouchKind.Moved, "missing from one read, then in another pack: moved");

            // After a reset the next read only learns again.
            t.Reset();
            inv.Main.Items.Remove(salvage);
            inv.Packs[1].Items.Add(salvage);
            t.Observe(inv, 13_000);
            Check(!t.IsHeld(40, 13_000, 3 * Min), "after a reset: learning again");

            Eq(TouchWatch.Words(TouchKind.TakenOff), "you took it off", "plain words");
        }

        private static void HoldPlan()
        {
            SortInventory inv = Inv(3);
            inv.Main.Items.Add(It(10, "Prismatic Taper", ObjectClasses.SpellComponent));
            inv.Main.Items.Add(It(11, "Lead Scarab", ObjectClasses.SpellComponent));
            inv.Packs[1].Items.Add(It(12, "Black Opal", ObjectClasses.Gem));          // gems go to pack 3
            inv.Packs[2].Items.Add(It(13, "Junk", ObjectClasses.Misc));               // no rule: Clean evicts it
            var held = new System.Collections.Generic.HashSet<uint> { 10, 12, 13 };
            var p = new SortPlanner { Held = id => held.Contains(id) };

            var moves = p.Plan(inv, Defaults, SortScope.All, force: true, 0, 0);
            Check(moves.Count == 1 && moves[0].Item.Id == 11, "only the scarab moves: the taper and the opal are held");
            Eq(moves[0].FromIndex, 0, "a move says where it came from (main pack)");
            Eq(moves[0].FromId, Me, "and its pack's object");
            Check(p.View.Held.Count == 2 && p.View.Held.Any(h => h.Item.Id == 10 && h.From == 0 && h.To == 1)
                  && p.View.Held.Any(h => h.Item.Id == 12 && h.From == 2 && h.To == 3), "held items listed with where they'd go");

            moves = p.Plan(inv, Defaults, SortScope.Clean, force: true, 0, 0);
            Check(moves.All(m => m.Item.Id != 13) && p.View.Held.Any(h => h.Item.Id == 13 && h.To == 0), "Clean leaves a held stray where it is too");

            // Not held any more: they move.
            held.Clear();
            moves = p.Plan(inv, Defaults, SortScope.All, force: true, 0, 0);
            Eq(moves.Count, 3, "nothing held: all three move");
            Eq(p.View.Held.Count, 0, "and none are listed");

            // Stacks: a held stack is never joined into (or joined from), another one still is.
            SortInventory st = Inv(1);
            st.Packs[0].Items.Add(It(20, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 7, stack: 50, max: 100));
            st.Packs[0].Items.Add(It(21, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 7, stack: 5, max: 100));
            st.Packs[0].Items.Add(It(22, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 7, stack: 3, max: 100));
            held.Add(20);
            moves = p.Plan(st, Defaults, SortScope.All, force: true, 0, 0, PlanOptions.Full);
            Check(moves.Count == 1 && moves[0].Kind == MoveKind.Stack && moves[0].Item.Id == 22 && moves[0].MergeInto == 21,
                "the held big stack is left alone: the two others join");
            held.Add(21);
            moves = p.Plan(st, Defaults, SortScope.All, force: true, 0, 0, PlanOptions.Full);
            Check(moves.All(m => m.Kind != MoveKind.Stack), "two of three held: nothing to join");
            // A new item isn't joined into a held stack in its pack either.
            Eq(SortPlanner.StackTarget(It(23, "Prismatic Taper", ObjectClasses.SpellComponent, wcid: 7, stack: 1, max: 100), st.Packs[0], id => held.Contains(id)), 22u,
                "a route joins a stack the player isn't holding");

            // Order: a held item isn't put in order (the others are, around it).
            SortInventory ord = Inv(1);
            ord.Packs[0].Items.Add(It(30, "Zircon", ObjectClasses.Gem));
            ord.Packs[0].Items.Add(It(31, "Amber", ObjectClasses.Gem));
            ord.Packs[0].OrderKnown = true;
            held.Clear();
            held.Add(30);
            var rules = new System.Collections.Generic.List<SortRule> { SortRule.ForClass(ObjectClasses.Gem, 1) };
            moves = p.Plan(ord, rules, SortScope.All, force: true, 0, 0, PlanOptions.Full);
            Check(moves.Count == 1 && moves[0].Kind == MoveKind.Reorder && moves[0].Item.Id == 31 && moves[0].Placement == 0, "Amber goes before the held Zircon, Zircon stays");
            held.Clear();
            held.Add(31);
            moves = p.Plan(ord, rules, SortScope.All, force: true, 0, 0, PlanOptions.Full);
            Eq(moves.Count, 0, "the item out of place is held: that pack waits for the hold to end");

            // Unpack ignores holds (the player asked for everything).
            held.Add(12);
            Check(p.PlanUnpack(inv, null, 0).Any(m => m.Item.Id == 12), "Unpack moves a held item too");
        }

        private static void HoldLoop()
        {
            // Components go to pack 1; the player takes a taper out to the main pack to use it.
            var w = new FakeWorld();
            SortItem taper = It(10, "Prismatic Taper", ObjectClasses.SpellComponent);
            w.Inv.Packs[0].Items.Add(taper);
            SortController c = Controller(settleSec: 5);
            c.Data.Rules = SortRules.Defaults();
            Ticks(c, w, 0, 2000);
            Eq(w.Sent.Count, 0, "in its pack: nothing to do");

            w.Inv.Packs[0].Items.Remove(taper);
            w.Inv.Main.Items.Add(taper);
            Ticks(c, w, 2000, 60_000);
            Eq(w.Sent.Count, 0, "taken out by the player: left in the main pack");
            Check(c.Planner.View.Held.Any(h => h.Item.Id == 10 && h.To == 1), "the window can say it is left alone and where it would go");
            Check(c.IsHeld(10), "the controller says it is held");
            c.StartJob(SortScope.All);
            Ticks(c, w, 60_000, 70_000);
            Eq(w.Sent.Count, 0, "Sort now leaves it alone too");
            Check(c.Job == null, "and finishes");

            // After the hold (3 minutes by default) auto-sort takes it back.
            Ticks(c, w, 70_000, 2000 + 3 * Min + 8000);
            Check(w.Sent.Count == 1 && w.Sent[0].Item.Id == 10 && w.Sent[0].DestIndex == 1, "after the hold it goes back to its pack");
            Check(!c.IsHeld(10), "no longer held");

            // RynthSort's own move back didn't make it held.
            long t0 = 2000 + 3 * Min + 8000;
            Ticks(c, w, t0, t0 + 5000);
            Check(!c.IsHeld(10), "RynthSort's own move doesn't count as the player's");

            // A selected item in the main pack (just looted) stays while selected.
            SortItem scarab = It(11, "Lead Scarab", ObjectClasses.SpellComponent);
            w.Inv.Main.Items.Add(scarab);
            long t1 = t0 + 5000;
            for (long t = t1; t <= t1 + 30_000; t += 16)
            {
                c.Touch.NoteSelected(11, t);
                c.Tick(w, t);
            }
            Eq(w.Sent.Count(m => m.Item.Id == 11), 0, "selected: not moved");
            // "Sort them too".
            c.Touch.ReleaseAll();
            Ticks(c, w, t1 + 30_016, t1 + 40_000);
            Eq(w.Sent.Count(m => m.Item.Id == 11), 1, "released: it moves");

            // Hold 0 = off: a moved item goes back at once.
            c.Data.Settings.HoldMin = 0;
            SortItem moved = w.Inv.Packs[0].Items.First(i => i.Id == 11);
            w.Inv.Packs[0].Items.Remove(moved);
            w.Inv.Main.Items.Add(moved);
            int before = w.Sent.Count;
            Ticks(c, w, t1 + 40_000, t1 + 50_000);
            Check(w.Sent.Count == before + 1 && w.Sent[^1].Item.Id == 11, "hold off: moved straight back");
        }

        private static void HoldStore()
        {
            SortStore st = TempStore("hold");
            CharacterSortData d = st.Load("S", "A", out _);
            Eq(d.Settings.HoldMin, SortSettings.HoldMinDefault, "a new character: the default hold");
            Eq(SortSettings.HoldMinDefault, 3, "three minutes");
            d.Settings.HoldMin = 7;
            st.Save("S", "A", d);
            Check(File.ReadAllText(st.PathFor("S", "A")).Contains("\"holdMin\": 7"), "written");
            Eq(st.Load("S", "A", out _).Settings.HoldMin, 7, "read back");

            // A file from before the setting: the default. Out of range: clamped. A Lua double: rounded.
            File.WriteAllText(st.PathFor("S", "A"), "{\"version\":3,\"rules\":[],\"settings\":{\"auto\":true,\"settleSec\":5}}");
            CharacterSortData old = st.Load("S", "A", out string? problem);
            Check(problem == null && old.Settings.HoldMin == SortSettings.HoldMinDefault && old.Settings.Auto, "an older file: the default hold, its own settings kept");
            File.WriteAllText(st.PathFor("S", "A"), "{\"settings\":{\"holdMin\":99}}");
            Eq(st.Load("S", "A", out _).Settings.HoldMin, SortSettings.HoldMinMax, "clamped to the most");
            File.WriteAllText(st.PathFor("S", "A"), "{\"settings\":{\"holdMin\":-2}}");
            Eq(st.Load("S", "A", out _).Settings.HoldMin, 0, "clamped to off");
            File.WriteAllText(st.PathFor("S", "A"), "{\"settings\":{\"holdMin\":2.0}}");
            Eq(st.Load("S", "A", out _).Settings.HoldMin, 2, "2.0 = 2");

            // The Lua script's file has no hold: the default.
            SortStore imp = TempStore("hold-import");
            string lua = imp.LuaPathFor("DreamWeave", "Tester");
            Directory.CreateDirectory(Path.GetDirectoryName(lua)!);
            File.WriteAllText(lua, LuaFile);
            Eq(imp.Load("DreamWeave", "Tester", out _).Settings.HoldMin, SortSettings.HoldMinDefault, "a Lua import: the default hold");
        }
    }
}
