using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthSort.Tests
{
    // The window drawn with ImGui (2026-11-26): what the engine is told about it (the sections
    // manifest), and the pop-in of tiles that just arrived in another pack. Nothing native runs here.
    internal static partial class Program
    {
        private static void WindowManifest()
        {
            using JsonDocument doc = JsonDocument.Parse(RynthSortPlugin.Manifest);
            JsonElement root = doc.RootElement;
            Eq(root.GetProperty("version").GetInt32(), 1, "manifest version");
            JsonElement[] windows = root.GetProperty("windows").EnumerateArray().ToArray();
            Eq(windows.Length, 1, "one window");
            JsonElement w = windows[0];
            Check(w.GetProperty("title").GetString() == "RynthSort" && w.GetProperty("surface").GetString() == RynthSortPlugin.WindowSurface,
                "the RynthSort title (the old script window's, which it replaces) and its surface");
            Check(w.GetProperty("chrome").GetString() == "none" && w.GetProperty("scale").GetString() == "draw" && w.GetProperty("edgeToEdge").GetBoolean(),
                "frameless, drawn at its own size (Ctrl+wheel), edge to edge");
            Check(!w.TryGetProperty("fitHeight", out _), "players size it: not fitted to its content");
            Check(!w.GetProperty("bar").GetBoolean(), "no bar button (the Inventory's Sort button and /rsort open it, as before)");
            Check(w.GetProperty("minWidth").GetInt32() <= 360 && w.GetProperty("width").GetInt32() >= 1000, "it can be narrow (one column) and starts wide (three)");
            Check(w.GetProperty("icon").GetString()!.Length == 1, "one glyph for an icon");
            Eq(new RynthSortPlugin().SectionsJson(), "{\"version\":1}", "an engine without frameless windows is told of none (the script window stays)");
        }

        private static void WindowArrivals()
        {
            SortInventory inv = DesignPacks();
            SortController c = ScreenController(inv);
            var a = new Arrivals();
            ScreenModel m1 = Screen(c);
            a.Update(m1, 10.0);
            uint taper = inv.Main.Items[1].Id;
            Eq(a.Progress(taper, 10.0), 1f, "the first view pops nothing in");

            // The taper moves to the Sack: in the next view it has just arrived.
            SortItem item = inv.Main.Items[1];
            inv.Main.Items.Remove(item);
            inv.Packs[0].Items.Add(item);
            ScreenModel m2 = Screen(c);
            a.Update(m2, 20.0);
            Eq(a.Progress(taper, 20.0), 0f, "it arrived just now");
            Check(a.Progress(taper, 20.3) > 0.4f && a.Progress(taper, 20.3) < 0.6f, "half way through the pop-in");
            Eq(a.Progress(taper, 21.0), 1f, "and done after it");
            Eq(a.Progress(inv.Main.Items[0].Id, 20.0), 1f, "an item that stayed doesn't pop");
            a.Update(m2, 30.0);
            Eq(a.Progress(taper, 30.0), 1f, "the same view again changes nothing");

            // Another character's view starts fresh.
            var other = new ScreenModel { Ready = true, Who = "Someone else", Version = m2.Version + 1000 };
            other.TilesById[taper] = new TileModel { Id = taper, Where = 3 };
            a.Update(other, 40.0);
            Eq(a.Progress(taper, 40.0), 1f, "another character: nothing pops");
        }
    }
}
