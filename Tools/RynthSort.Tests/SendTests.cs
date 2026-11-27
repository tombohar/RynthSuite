using System;
using System.Linq;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // What the controller does for the window drawn with ImGui (2026-11-26, the approved design for
    // #rynth-issues "The bag sort feature is lacking in UX"): a layout gives a pack the kind it
    // already holds before kinds no pack holds; one item left alone or let go; an item sent to a
    // pack from the window without a rule ("Just move it", "Send to"), then left alone.
    internal static partial class Program
    {
        /// <summary>The design's sample character: four packs, the main pack full of loot.</summary>
        private static SortInventory DesignPacks()
        {
            SortInventory inv = Inv(4);
            string[] names = { "Sack", "Backpack", "Satchel", "Hide Sack" };
            for (int i = 0; i < 4; i++) inv.Packs[i].Name = names[i];
            inv.PackOrderKnown = true;
            inv.OrderSource = PackOrderSource.Known;
            uint id = 100;
            void Main(string n, int cls, int stack = 1) => inv.Main.Items.Add(It(id++, n, cls, stack: stack, max: stack > 1 ? 5000 : 1));
            void In(int pack, string n, int cls, int stack = 1) => inv.Packs[pack].Items.Add(It(id++, n, cls, stack: stack, max: stack > 1 ? 5000 : 1));
            Main("Black Opal", ObjectClasses.Gem);
            Main("Prismatic Taper", ObjectClasses.SpellComponent, 320);
            Main("Mana Stone", ObjectClasses.ManaStone);
            Main("Salvaged Steel", ObjectClasses.Salvage);
            Main("Treated Healing Kit", ObjectClasses.HealingKit);
            Main("Trade Note (250,000)", ObjectClasses.TradeNote, 4);
            Main("Amuli Shadow Coat", ObjectClasses.Armor);
            Main("Legendary Key", ObjectClasses.Key);
            Main("Pyreal", ObjectClasses.Money, 2400);
            In(0, "Lead Scarab", ObjectClasses.SpellComponent, 40);
            In(0, "Hyssop", ObjectClasses.SpellComponent, 75);
            In(0, "Powdered Agate", ObjectClasses.SpellComponent, 60);
            In(1, "Salvaged Iron", ObjectClasses.Salvage);
            In(1, "Salvaged Granite", ObjectClasses.Salvage);
            In(2, "Fire Opal", ObjectClasses.Gem);
            In(2, "Black Garnet", ObjectClasses.Gem);
            In(3, "Health Elixir", ObjectClasses.Food, 6);
            In(3, "Peppermint Stick", ObjectClasses.Food, 20);
            In(3, "Intricate Lockpick", ObjectClasses.Lockpick);
            return inv;
        }

        private static void LayoutHeldKindsFirst()
        {
            // The design's Everyday: the Hide Sack already holds kits, lockpicks and food, so it keeps them.
            SortInventory inv = DesignPacks();
            LayoutPlan plan = StarterLayouts.Fit(StarterLayouts.Everyday, inv);
            Eq(string.Join(",", plan.Placed.Select(x => x.Group.Key + x.Pack)), "components1,salvage2,gems3,supplies4",
                "each pack keeps the kind it holds; the Hide Sack gets kits, lockpicks and food");
            Eq(string.Join(",", plan.Left.Select(g => g.Key)), "notes,scrolls", "trade notes and scrolls (held by no pack) stay in the main pack");
            Eq(plan.AlreadyThere, 10, "the ten items in the side packs are already where their kind goes");

            // A kind no pack holds still gets the roomiest pack left, most wanted first.
            SortInventory two = Inv(3);
            two.Packs[2].Items.Add(It(1, "Health Elixir", ObjectClasses.Food));
            LayoutPlan p2 = StarterLayouts.Fit(StarterLayouts.Everyday, two);
            Eq(string.Join(",", p2.Placed.Select(x => x.Group.Key + x.Pack)), "components1,gems2,supplies3",
                "the held kind (supplies, fifth in the list) wins its pack before salvage (third, held by none)");
            Eq(string.Join(",", p2.Left.Select(g => g.Key)), "salvage,notes,scrolls", "salvage waits for a pack");
            Check(StarterLayouts.Everyday.Blurb.Length <= 120, "the Everyday line still fits");
        }

        private static void HoldOneItem()
        {
            var t = new TouchWatch();
            t.Hold(7, 1000, TouchKind.Chosen);
            Check(t.IsHeld(7, 2000, 60_000), "asked to leave it: held");
            Check(t.TryGetHeld(7, 2000, 60_000, out _, out TouchKind k) && k == TouchKind.Chosen && TouchWatch.Words(k) == "you asked to leave it", "and says why");
            Check(!t.IsHeld(7, 2000, 0), "the hold setting at 0 holds nothing");
            Check(t.Release(7) && !t.IsHeld(7, 2000, 60_000), "Let RynthSort move it: no longer held");
            Check(!t.Release(7), "releasing again does nothing");
            t.Hold(0, 1000, TouchKind.Sent);
            Eq(t.HeldCount(1000, 60_000), 0, "no item, no hold");
        }

        private static void SendFromWindow()
        {
            var w = new FakeWorld();
            w.Inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            SortController c = Controller(auto: true);
            c.Data.Rules = SortRules.Defaults();   // gems go to pack 3
            c.WallClock = () => new DateTime(2026, 11, 26, 9, 30, 0);
            Check(c.StartSend(10, 0x40000001), "Just move it: to pack 1, no rule");
            Check(c.Send != null && c.IsHeld(10), "left alone from now, so auto-sort doesn't take it meanwhile");
            Ticks(c, w, 16, 3000);
            Check(w.Inv.Packs[0].Items.Any(i => i.Id == 10), "it went to pack 1");
            Check(w.Sent.Count == 1 && w.Sent[0].MergeInto == 0, "one plain move, and auto-sort didn't move it on to pack 3");
            Check(c.IsHeld(10) && c.Touch.TryGetHeld(10, c.Clock, c.HoldMs, out _, out TouchKind k) && k == TouchKind.Sent, "held as sent there");
            Eq(c.PutBackCount, 0, "not a sort: Put back leaves it");
            Check(c.Feed.Count > 0 && c.Feed[0].ItemId == 10 && c.Feed[0].Time == "09:30" && c.Feed[0].Text.Contains("you sent it"), $"the feed says so, with the time ({c.Feed[0].Text})");
            Check(c.Send == null && !c.HasMoveOut, "done");

            // Waits while the way isn't clear, and is dropped after the wait.
            w.Gate = new GateInputs { Looting = true };
            c.StartSend(10, Me);
            Ticks(c, w, 3000, 5000);
            Check(w.Inv.Packs[0].Items.Any(i => i.Id == 10) && c.Paused && c.Send != null, "looting: it waits");
            w.Gate = default;
            Ticks(c, w, 5000, 8000);
            Check(w.Inv.Main.Items.Any(i => i.Id == 10), "the way clear: back to the main pack");

            c.StartSend(10, 0x40000002);
            w.Gate = new GateInputs { Looting = true };
            Ticks(c, w, 8000, 8000 + SortController.SendWaitMs + 1000);
            Check(c.Send == null && w.Inv.Main.Items.Any(i => i.Id == 10), "too long: dropped, the item stays");
            Check(c.Log[0].StartsWith("A move you asked for was dropped", StringComparison.Ordinal), "and the log says so");

            // An item that isn't in the packs, a pack that isn't carried, a full pack.
            w.Gate = default;
            long t0 = 8000 + SortController.SendWaitMs + 2000;
            c.StartSend(999, 0x40000001);
            Ticks(c, w, t0, t0 + 2000);
            Check(c.Log[0].Contains("isn't in your packs"), "an item gone: dropped");
            c.StartSend(10, 0x4000FFFF);
            Ticks(c, w, t0 + 2000, t0 + 4000);
            Check(c.Log[0].Contains("that pack isn't carried"), "a pack not carried: not moved");
            Check(!c.StartSend(0, 0x40000001) && !c.StartSend(10, 0), "no item or no pack: nothing starts");

            var full = new FakeWorld { Inv = Inv(1, cap: 1) };
            full.Inv.Packs[0].Items.Add(It(1, "Filler", ObjectClasses.Misc));
            full.Inv.Main.Items.Add(It(2, "Black Opal", ObjectClasses.Gem));
            SortController cf = Controller(auto: false);
            cf.Tick(full, 0);
            cf.StartSend(2, 0x40000001);
            Ticks(cf, full, 16, 1000);
            Check(full.Sent.Count == 0 && cf.Log[0].Contains("is full"), "a full pack: not moved, and why");
        }
    }
}
