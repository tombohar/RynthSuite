using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthDefective;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;
using RynthCore.Plugin.RynthDefective.Views;

namespace RynthDefective.Tests
{
    internal static partial class Program
    {
        private sealed class Windows
        {
            public Rig R;
            public DefSession S => R.S;
            public BarView Bar;
            public PickerView Picker;
            public UiHost Host = new();
            public UiWindow BarWin, PickerWin;
            public uint Seq;

            public Windows(string name, bool icons = true)
            {
                R = new Rig(name);
                S.IconsAvailable = icons;
                Bar = new BarView(S);
                Picker = new PickerView(S);
                BarWin = Host.Add(new UiWindow("RynthDefective/Bar", "Defective") { Visible = true });
                PickerWin = Host.Add(new UiWindow("RynthDefective/Weapons", "Defective Weapons") { Visible = true });
            }

            public void DrawBar() { BarWin.Begin(); Bar.Draw(BarWin); BarWin.End(); }
            public void DrawPicker() { PickerWin.Begin(); Picker.Draw(PickerWin); PickerWin.End(); }
            public string BarText => Encoding.UTF8.GetString(BarWin.Body);
            public string PickerText => Encoding.UTF8.GetString(PickerWin.Body);

            public void Click(UiWindow w, string id) => Apply(Host, EventFor(1, w, id, new byte[] { 1, 0 }));
            public void SetInt(UiWindow w, string id, int v) => Apply(Host, EventFor(3, w, id, BitConverter.GetBytes(v)));
            public void SetBool(UiWindow w, string id, bool v) => Apply(Host, EventFor(2, w, id, new byte[] { (byte)(v ? 1 : 0) }));
            public void SetText(UiWindow w, string id, string text)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(text);
                var p = new byte[3 + utf8.Length];
                p[0] = 0;
                BitConverter.GetBytes((ushort)utf8.Length).CopyTo(p, 1);
                utf8.CopyTo(p, 3);
                Apply(Host, EventFor(5, w, id, p));
            }

            private byte[] EventFor(ushort type, UiWindow w, string id, byte[] payload)
            {
                var e = new byte[16 + payload.Length];
                BitConverter.GetBytes(type).CopyTo(e, 0);
                BitConverter.GetBytes((ushort)e.Length).CopyTo(e, 2);
                BitConverter.GetBytes(++Seq).CopyTo(e, 4);
                BitConverter.GetBytes(w.Hash).CopyTo(e, 8);
                BitConverter.GetBytes(w.KeyOf(id)).CopyTo(e, 12);
                payload.CopyTo(e, 16);
                return e;
            }
        }

        private static Windows Stocked(string name, bool icons = true)
        {
            var w = new Windows(name, icons);
            w.R.Inv = new List<CarriedItem>(Armory());
            w.R.Inv.Add(Potion(20, "Health Potion", 23));
            w.R.Inv.Add(Kit(21, uses: 10));
            w.R.Inv.Add(Kit(22, uses: 28));
            w.S.LoggedIn = true;
            w.S.SetSlot(1, Kit(21));
            w.S.SetSlot(2, Potion(20));
            w.S.SetSlot(3, Melee(3, "Mace", wielded: true));
            w.S.SetSlot(4, new CarriedItem { Id = 99, Name = "Mana Stone", Icon = 0x06004444 });   // not carried
            File.WriteAllText(w.R.HotkeyPath, "on\tF9\tcommand\t/rdef use 1\non\tCtrl+F2\tcommand\t/rdef use 3\n");
            w.S.Keys.Invalidate();
            return w;
        }

        private static unsafe List<ParsedWindow> ParseAll(UiHost host, string what, out int maxOps)
        {
            int n = Write(host);
            byte[] buf = SubmitBuffer(host);
            int rc;
            List<ParsedWindow> windows;
            string error;
            fixed (byte* p = buf) rc = DisplayListParser.Parse(p, n, 4000f, out _, out windows, out error);
            Check(rc == 0, $"{what}: engine parser refused the submit ({rc}): {error}");
            maxOps = windows.Where(x => x.List != null).Select(x => x.List!.OpCount).DefaultIfEmpty(0).Max();
            return windows;
        }

        private static int CountOps(ParsedWindow w, ScriptOp op)
        {
            int n = 0;
            if (w.List == null) return 0;
            for (int i = 0; i < w.List.OpCount; i++)
                if (w.List.Ops[i].Code == op) n++;
            return n;
        }

        private static void WindowBar()
        {
            Windows w = Stocked("bar");
            w.DrawBar();
            List<ParsedWindow> parsed = ParseAll(w.Host, "bar", out int ops);
            Check(ops > 10 && ops < 1500, $"ops in range ({ops})");
            string body = w.BarText;
            Check(body.Contains("66") && body.Contains("x28") && body.Contains("in hand") && body.Contains("none"), "uses left, a stack's count, the weapon in hand, and the missing one");
            Check(body.Contains("F9") && body.Contains("C+F2"), "the keys under their slots: " + body);
            Check(body.Contains("Edit slots") && body.Contains("Settings") && body.Contains("Weapons"), "the toolbar");
            Check(body.Contains("Click a slot, or press its key."), "the idle status line");
            ParsedWindow bar = parsed.First(p => p.Key == "RynthDefective/Bar");
            Eq(CountOps(bar, ScriptOp.ImageButton), 4, "four set slots drawn as image buttons (the missing one from its stored picture)");
            Eq(CountOps(bar, ScriptOp.Button), 4 + 3, "four empty slots (+ signs) and the three toolbar buttons");

            // Icons off or unavailable: text buttons.
            Windows t = Stocked("bar-text", icons: false);
            t.DrawBar();
            parsed = ParseAll(t.Host, "bar without icons", out _);
            bar = parsed.First(p => p.Key == "RynthDefective/Bar");
            Check(CountOps(bar, ScriptOp.ImageButton) == 0 && CountOps(bar, ScriptOp.Button) >= 8, "no pictures: text buttons");
            Check(t.BarText.Contains("PHK"), "named by initials: " + "PHK");

            t.S.IconsAvailable = true;
            t.S.Settings.ShowIcons = false;
            t.DrawBar();
            Eq(CountOps(ParseAll(t.Host, "icons off", out _).First(p => p.Key == "RynthDefective/Bar"), ScriptOp.ImageButton), 0, "ShowIcons off: no pictures either");

            // Counts and keys can be switched off.
            Windows c = Stocked("bar-nocounts");
            c.S.Settings.ShowCounts = false;
            c.DrawBar();
            ParseAll(c.Host, "no counts", out _);
            Check(!c.BarText.Contains("x28") && !c.BarText.Contains("C+F2"), "no counts or keys under the slots");

            // Pending and status lines.
            Windows p = Stocked("bar-status");
            p.S.Use.Request(new UseRequest(null, "Health Potion", UseMode.Auto, "t", 0));
            p.DrawBar();
            Check(p.BarText.Contains("Waiting to use Health Potion..."), "a waiting request shows");
            p.S.Use.Reset();
            p.S.SetStatus("Slot 1 cleared.");
            p.DrawBar();
            Check(p.BarText.Contains("Slot 1 cleared."), "the status line shows");

            // Not logged in.
            var out1 = new Windows("bar-out");
            out1.S.LoggedIn = false;
            out1.DrawBar();
            ParseAll(out1.Host, "logged out", out _);
            Check(out1.BarText.Contains("Log in to use the item bar."), "before login");

            // Window size that fits.
            (float ww, float hh) = w.Bar.WantedSize();
            Check(ww >= 250f && hh > 80f, $"a fitting size ({ww} x {hh})");
            w.S.Settings.SlotCount = 24; w.S.Settings.SlotsPerRow = 6;
            (float w2, float h2) = w.Bar.WantedSize();
            Check(h2 > hh, "more rows, taller");
        }

        private static void WindowEditor()
        {
            Windows w = Stocked("editor");
            w.Bar.EditOpen = true;
            w.Bar.SettingsOpen = true;
            w.Bar.Editor.Select(1);
            w.DrawBar();
            ParseAll(w.Host, "editor", out int ops);
            string body = w.BarText;
            Check(body.Contains("Set from selected") && body.Contains("Pick from pack") && body.Contains("Clear") && body.Contains("Move up"), "the slot buttons");
            Check(body.Contains("Slot 1: Peerless Healing Kit") && body.Contains("1.  Peerless Healing Kit   [F9]"), "the list shows the item and its key");
            Check(body.Contains("Pressing it") && body.Contains("It is a healing or stamina kit: pressing the slot has it used on you."), "how it is used: " + body);
            Check(body.Contains("Key: F9") && body.Contains("Key to bind") && body.Contains("Bind to this slot") && body.Contains("Bind to this item") && body.Contains("Unbind"), "the key controls");
            Check(body.Contains("Selected in the game: nothing"), "and what is selected");
            Check(body.Contains("Slots per row") && body.Contains("Button size") && body.Contains("Hazridi") && body.Contains("no code from it"), "settings and the credit");

            // The missing slot's editor says so.
            w.Bar.Editor.Select(4);
            w.DrawBar();
            ParseAll(w.Host, "editor, missing", out _);
            Check(w.BarText.Contains("Not in your pack right now."), "a missing item is explained");

            // The pack list.
            w.Bar.Editor.Select(5);
            w.DrawBar();
            w.Click(w.BarWin, "ed.pick");
            w.DrawBar();
            ParseAll(w.Host, "pack list", out _);
            body = w.BarText;
            Check(w.Bar.Editor.Picking && body.Contains("Search your pack") && body.Contains("Health Potion  x28") && body.Contains("Peerless Healing Kit  x3"), "the pack list with counts: " + body);
            Check(!body.Contains("Pressing it"), "an empty slot has no use setting");
            w.SetText(w.BarWin, "ed.packsearch", "potion");
            w.DrawBar();
            Check(w.BarText.Contains("Health Potion") && !w.BarText.Contains("Yumi"), "the pack list narrows");
        }

        private static void WindowPicker()
        {
            Windows w = Stocked("picker");
            w.S.Data.Favourites.Add(ItemRef.From(w.R.Inv[9]));   // Gold Wand
            w.S.Data.Recent.Add(ItemRef.From(w.R.Inv[0]));       // Sword
            w.DrawPicker();
            List<ParsedWindow> parsed = ParseAll(w.Host, "picker", out int ops);
            string body = w.PickerText;
            Check(ops > 20 && ops < 1500, $"ops in range ({ops})");
            Check(body.Contains("Favourites") && body.Contains("Recent") && body.Contains("Melee (3)") && body.Contains("War Magic (2)")
                  && body.Contains("Void Magic (2)") && body.Contains("Shields (1)") && body.Contains("Ammunition (1)"), "sections: " + body);
            Check(body.Contains("Mace   [in hand]") && body.Contains("In hand: Mace"), "the weapon in hand is marked");
            Check(body.Contains("Silver Wand  (Fire)") && body.Contains("Orb  (Nether)"), "casters with their element");
            Check(body.IndexOf("Favourites", StringComparison.Ordinal) < body.IndexOf("Recent", StringComparison.Ordinal)
                  && body.IndexOf("Recent", StringComparison.Ordinal) < body.IndexOf("Melee (3)", StringComparison.Ordinal), "favourites, recent, then the kinds");
            ParsedWindow pk = parsed.First(p => p.Key == "RynthDefective/Weapons");
            Check(CountOps(pk, ScriptOp.Image) >= 12 && CountOps(pk, ScriptOp.Selectable) >= 12, "a picture and a row for each weapon");
            Check(CountOps(pk, ScriptOp.Child) == 1, "one scrolling list");

            // Clicking a row wields it.
            w.Click(w.PickerWin, "pkg1");   // the Sword in the Melee group
            Check(w.S.Use.HasPending && w.S.Use.Pending!.Mode == UseMode.Equip && w.S.Use.Pending.Item!.Name == "Sword", "one click on a row queues the wield");

            // The star: favourites.
            w.Click(w.PickerWin, "favg1");
            Check(w.S.Data.Favourites.Any(f => f.Name == "Sword"), "+ adds a favourite");
            w.DrawPicker();
            Check(w.PickerText.Contains("-"), "and shows - for it");

            // Kind buttons and search.
            w.Click(w.PickerWin, "pk.kind" + (int)KindFilter.Void);
            Eq(w.S.Settings.PreferredKind, KindFilter.Void, "the kind button sets the preferred kind");
            w.DrawPicker();
            ParseAll(w.Host, "void filter", out _);
            Check(w.PickerText.Contains("Void Magic (2)") && !w.PickerText.Contains("Melee ("), "only void shown");
            Check(w.PickerText.Contains("2 of 11 shown"), "how many of how many: " + w.PickerText);
            w.Click(w.PickerWin, "pk.kind" + (int)KindFilter.All);
            w.SetText(w.PickerWin, "pk.search", "mace");
            w.DrawPicker();
            ParseAll(w.Host, "search", out _);
            Check(w.PickerText.Contains("Melee (1)") && !w.PickerText.Contains("Void Magic ("), "search narrows");
            w.SetText(w.PickerWin, "pk.search", "zzz");
            w.DrawPicker();
            Check(w.PickerText.Contains("Nothing matches."), "and says when nothing does");
            w.Click(w.PickerWin, "pk.clear");
            Eq(w.Picker.Search, "", "clear");

            // No weapons / not logged in.
            var none = new Windows("picker-none") { };
            none.S.LoggedIn = true;
            none.DrawPicker();
            ParseAll(none.Host, "empty picker", out _);
            Check(none.PickerText.Contains("No weapons or wands in your pack.") && none.PickerText.Contains("In hand: nothing"), "an empty pack");
            var out2 = new Windows("picker-out");
            out2.S.LoggedIn = false;
            out2.DrawPicker();
            Check(out2.PickerText.Contains("Log in to pick a weapon."), "before login");

            // Icons off: rows without pictures.
            Windows t = Stocked("picker-text", icons: false);
            t.DrawPicker();
            Eq(CountOps(ParseAll(t.Host, "picker, text", out _).First(p => p.Key == "RynthDefective/Weapons"), ScriptOp.Image), 0, "no pictures when the engine can't draw them");
        }

        private static void WindowHeader()
        {
            Eq(UiFormat.FlagBarButton, WindowFlags.BarButton, "the plugin's bit is the engine's");
            Windows w = Stocked("header");
            w.DrawBar();
            w.DrawPicker();
            List<ParsedWindow> parsed = ParseAll(w.Host, "header", out _);
            Check(parsed.Count == 2 && parsed[0].Title == "Defective" && parsed[1].Title == "Defective Weapons", "two windows, titled for their bar buttons");
            Check(parsed.All(p => (p.Flags & WindowFlags.ShowInBar) != 0 && (p.Flags & WindowFlags.BarButton) != 0), "each has a button of its own on the bar");
            Check(float.IsNaN(parsed[0].RequestSize.X), "no size request by default");

            w.BarWin.RequestWidth = 320f;
            w.BarWin.RequestHeight = 210f;
            parsed = ParseAll(w.Host, "size request", out _);
            Check(parsed[0].RequestSize.X == 320f && parsed[0].RequestSize.Y == 210f, "a size request goes out");
            w.BarWin.MarkSubmitted(true);
            w.PickerWin.MarkSubmitted(true);
            Check(float.IsNaN(w.BarWin.RequestWidth), "and is forgotten once sent (a standing request would undo the player's resizing)");
            Check(!w.BarWin.HeaderDirty, "so the header is clean again");
        }

        private static void WindowEvents()
        {
            Windows w = Stocked("events");
            w.DrawBar();
            w.BarWin.MarkSubmitted(true);

            // A slot button runs the slot.
            w.Click(w.BarWin, "slot2");
            Check(w.S.Use.HasPending && w.S.Use.Pending!.Item!.Name == "Health Potion", "clicking the slot queues its item");
            w.S.Use.Reset();
            w.Click(w.BarWin, "slot5");   // an empty slot: opens the editor on it
            Check(w.Bar.EditOpen && w.Bar.Editor.SelectedSlot == 5, "an empty slot opens the editor on it");
            w.Click(w.BarWin, "slot4");   // the missing one
            Check(w.S.Use.HasPending, "a missing slot still queues (and the controller reports it)");
            w.S.Use.Reset();

            // Toolbar.
            w.Click(w.BarWin, "tb.settings");
            Check(w.Bar.SettingsOpen, "Settings toggles");
            bool shown = false;
            w.Bar.ShowPicker = () => shown = true;
            w.Click(w.BarWin, "tb.weapons");
            Check(shown, "Weapons asks for the picker");
            int fits = 0;
            w.Bar.RequestFit = () => fits++;
            w.Click(w.BarWin, "tb.edit");
            Check(fits == 1 && !w.Bar.EditOpen, "toggling a section asks the window to fit");

            // Editor: set from the game's selection, mode, key.
            w.Bar.EditOpen = true;
            w.Bar.Editor.Select(5);
            w.R.Selected = 20;   // the Health Potion
            w.DrawBar();
            Check(w.BarText.Contains("Selected in the game: Health Potion"), "the selection is named");
            w.Click(w.BarWin, "ed.set");
            Check(w.S.Data.Slots[4].Item?.Name == "Health Potion", "Set from selected fills the slot");
            w.DrawBar();
            w.SetInt(w.BarWin, "ed.mode", (int)UseMode.OnSelected);
            Eq(w.S.Data.Slots[4].Mode, UseMode.OnSelected, "the use mode");
            w.SetText(w.BarWin, "ed.key", "F7");
            w.DrawBar();
            w.Click(w.BarWin, "ed.bind");
            Check(w.R.Chat.Contains("/rc hotkey add F7 /rdef use 5"), "Bind to this slot types the engine's hotkey command: " + string.Join("|", w.R.Chat));
            w.Click(w.BarWin, "ed.bindname");
            Check(w.R.Chat.Contains("/rc hotkey add F7 /rdef use Health Potion"), "and by name");
            w.Click(w.BarWin, "ed.clear");
            Check(w.S.Data.Slots[4].Empty, "Clear");
            w.Bar.Editor.Select(2);
            w.DrawBar();
            w.Click(w.BarWin, "ed.down");
            Check(w.S.Data.Slots[2].Item?.Name == "Health Potion" && w.S.Data.Slots[1].Item?.Name == "Mace" && w.Bar.Editor.SelectedSlot == 3, "Move down swaps and follows");
            w.DrawBar();
            w.Click(w.BarWin, "ed.up");
            Check(w.S.Data.Slots[1].Item?.Name == "Health Potion" && w.Bar.Editor.SelectedSlot == 2, "Move up swaps back");
            w.DrawBar();
            w.Click(w.BarWin, "row6");
            Eq(w.Bar.Editor.SelectedSlot, 6, "picking a row selects the slot");

            // Pack list click assigns.
            w.Bar.Editor.Select(6);
            w.DrawBar();
            w.Click(w.BarWin, "ed.pick");
            w.DrawBar();
            w.Click(w.BarWin, "pk" + 4);   // Yumi, id 4
            Check(w.S.Data.Slots[5].Item?.Name == "Yumi" && !w.Bar.Editor.Picking, "choosing from the pack list sets the slot and closes the list");

            // Settings.
            w.Bar.SettingsOpen = true;
            w.DrawBar();
            fits = 0;
            w.SetInt(w.BarWin, "set.slots", 12);
            Check(w.S.Settings.SlotCount == 12 && fits == 1, "slot count slider, window refits");
            w.SetInt(w.BarWin, "set.perrow", 4);
            Eq(w.S.Settings.SlotsPerRow, 4, "per row");
            w.SetInt(w.BarWin, "set.button", 48);
            Eq(w.S.Settings.ButtonSize, 48, "button size");
            w.SetInt(w.BarWin, "set.picker", 40);
            Eq(w.S.Settings.PickerIconSize, 40, "picker icon size");
            w.SetBool(w.BarWin, "set.icons", false);
            Check(!w.S.Settings.ShowIcons, "icons off");
            w.SetBool(w.BarWin, "set.counts", false);
            Check(!w.S.Settings.ShowCounts, "counts off");
            w.SetBool(w.BarWin, "set.swap", true);
            Check(w.S.Settings.SwapWhileBotRuns, "swap switch");
            w.SetInt(w.BarWin, "set.slots", 500);
            Eq(w.S.Settings.SlotCount, 24, "the slider's value is clamped to the maximum");
            Check(w.R.Saves > 5, "every change asks for a save");
        }

        private static void WindowLimits()
        {
            // A big pack: 24 slots, editor with the pack list (40 rows), settings.
            Windows w = Stocked("limits");
            for (uint i = 0; i < 300; i++) w.R.Inv.Add(Potion(1000 + i, "Potion Number " + i, 3));
            w.S.Settings.SlotCount = 24;
            w.S.Settings.SlotsPerRow = 8;
            for (int i = 0; i < 24; i++) w.S.SetSlot(i + 1, w.R.Inv[Math.Min(w.R.Inv.Count - 1, 14 + i)]);
            w.Bar.EditOpen = true;
            w.Bar.SettingsOpen = true;
            w.Bar.Editor.Select(3);
            w.DrawBar();
            w.Click(w.BarWin, "ed.pick");
            w.DrawBar();
            ParseAll(w.Host, "full bar", out int ops);
            Check(ops < 1500 && w.BarWin.Body.Length < UiFormat.MaxBodyBytes, $"the full bar fits the engine's limits ({ops} ops, {w.BarWin.Body.Length} bytes)");
            Check(w.BarText.Contains("more - type to narrow the list."), "the pack list says when it was cut");

            // 400 weapons in the picker: cut with a note, still accepted.
            Windows p = new Windows("limits-picker");
            p.S.LoggedIn = true;
            for (uint i = 0; i < 400; i++) p.R.Inv.Add(Melee(5000 + i, "Blade " + i.ToString("000")));
            for (uint i = 0; i < 40; i++) p.S.Data.Favourites.Add(new ItemRef { Id = 5000 + i, Name = "Blade " + i.ToString("000"), Wcid = 300 });
            p.DrawPicker();
            ParseAll(p.Host, "400 weapons", out ops);
            Check(ops < 1500 && p.PickerWin.Body.Length < UiFormat.MaxBodyBytes, $"400 weapons stay inside the limits ({ops} ops, {p.PickerWin.Body.Length} bytes)");
            Check(p.PickerText.Contains("more than fits in one window"), "and it says the list was cut");
            // ... and a search brings the one wanted back into the list.
            p.Picker.Search = "Blade 399";
            p.DrawPicker();
            ParseAll(p.Host, "search in 400", out _);
            Check(p.PickerText.Contains("Blade 399") && !p.PickerText.Contains("more than fits"), "a search finds it");
        }
    }
}
