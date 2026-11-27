using System;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Window;

namespace RynthSort.Tests
{
    // RynthSort 0.6.0: an item dragged out of RynthCore's Inventory window (its ImGui payload
    // "RYNTH_INV_ITEM") and let go on a pack card gets the same three choices as one dragged inside
    // the window. The engine also offers that drop through its drop target, so the window takes the
    // first of the two and ignores the echo.
    internal static partial class Program
    {
        private static void DropFromInventory()
        {
            // One drop, one set of choices.
            var once = new DropOnce();
            Check(once.Take(42, 10.0), "the first arrival opens the choices");
            Check(!once.Take(42, 10.2), "the same item a moment later (the other path) is the same drop");
            Check(once.Take(43, 10.3), "another item is another drop");
            Check(once.Take(42, 10.4), "back to the first item: a new drop (another item came between)");
            Check(!once.Take(42, 11.0) && once.Take(42, 11.5), "the same item again after a second: a new drop");
            Check(!once.Take(0, 20.0), "no item, no drop");
            once.Reset();
            Check(once.Take(42, 11.6), "after a reset anything is new");

            // The payload type is the engine's, and RynthAi's Mini Remote takes the same one.
            Eq(SortWindow.InvPayloadType, "RYNTH_INV_ITEM", "the payload type");
            string root = RepoRoot();
            string engine = Path.Combine(root, "..", "RynthCore", "src", "RynthCore.Engine", "ImGui");
            string bridge = Path.Combine(engine, "ItemDragBridge.cs"), face = Path.Combine(engine, "Panels", "InventoryFace.cs");
            if (File.Exists(bridge) && File.Exists(face))
            {
                Check(File.ReadAllText(bridge).Contains("PayloadType = \"" + SortWindow.InvPayloadType + "\""), "the engine's drag bridge publishes it");
                Check(File.ReadAllText(face).Contains("ItemPayloadType = \"" + SortWindow.InvPayloadType + "\""), "the engine's Inventory publishes it");
            }
            else Console.WriteLine("  (no RynthCore checkout beside the repo: the engine's payload names weren't compared)");
            string remote = Path.Combine(root, "Plugins", "RynthCore.Plugin.RynthAi", "Huds", "MiniRemoteHud.cs");
            if (File.Exists(remote)) Check(File.ReadAllText(remote).Contains("ItemPayloadType = \"" + SortWindow.InvPayloadType + "\""), "RynthAi's Mini Remote takes the same payload");

            // Dragging the item held it (the player's hands on it). Picking "All gems" on the drop
            // lets it go again, so the sort the player asked for moves it.
            var w = new FakeWorld();
            w.Inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            SortController c = Controller(auto: true);
            c.Data.Rules.Clear();
            Ticks(c, w, 0, 500);
            c.Touch.NoteDrag(10, c.Clock);
            Check(c.IsHeld(10), "dragged: left alone");
            EditResult r = RuleEdits.SendKind(c.Data.Rules, c.Planner.View, 2, ObjectClasses.Gem);
            Check(r.Changed, "All gems to pack 2");
            Ticks(c, w, 500, 2000);
            Check(w.Inv.Main.Items.Any(i => i.Id == 10), "still held: it waits where it is");
            c.Touch.Release(10);   // the drop's choice (ISortWindowActions.Unhold)
            Ticks(c, w, 2000, 4000);
            Check(w.Inv.Packs[1].Items.Any(i => i.Id == 10), "let go: it goes to the pack it was dropped on");
        }

        /// <summary>The RynthSuite checkout these tests run from (the folder with RynthSuite.slnx).</summary>
        private static string RepoRoot()
        {
            string? d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "RynthSuite.slnx"))) d = Path.GetDirectoryName(d);
            return d ?? Directory.GetCurrentDirectory();
        }
    }
}
