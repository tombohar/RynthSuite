using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthDefective;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.ShortcutBar;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;
using RynthCore.Plugin.RynthDefective.Views;

namespace RynthDefective.Tests
{
    // The Shortcuts bar's own logic (2026-10-27): which slot a bar slot is (usual / independent),
    // a healing kit in one of AC's slots used on yourself, copying AC's 18 slots into an independent
    // bar, the setting in the character's file, and the settings window.
    internal static partial class Program
    {
        /// <summary>A session with AC's 18 slots scripted and every own-slot key bind recorded.</summary>
        private sealed class BarRig
        {
            public readonly Rig R;
            public DefSession S => R.S;
            public uint[] RetailIds = new uint[DefSettings.RetailSlots], RetailSpells = new uint[DefSettings.RetailSlots];
            public bool RetailReadable = true;
            public readonly Dictionary<string, string> CommandKeys = new();
            public readonly Dictionary<uint, string> Names = new();
            public readonly List<(int Own, string Key)> Bound = new();

            public BarRig(string name)
            {
                R = new Rig(name);
                S.ReadRetail = (ids, spells) =>
                {
                    if (!RetailReadable) return false;
                    RetailIds.CopyTo(ids, 0);
                    RetailSpells.CopyTo(spells, 0);
                    return true;
                };
                S.ObjectName = id => Names.TryGetValue(id, out string? n) ? n : null;
                S.CommandKeys = c => CommandKeys.TryGetValue(c, out string? k) ? k : string.Empty;
                S.BindOwnKey = (own, key) => { Bound.Add((own, key)); return true; };
            }
        }

        private static void BarMappingUsualAndIndependent()
        {
            // The usual bar: 1-18 AC's, 18+N own N.
            Check(BarMapping.IsRetail(1, false) && BarMapping.IsRetail(18, false) && !BarMapping.IsRetail(19, false), "usual: 1-18 are the game's");
            Eq(BarMapping.OwnSlot(5, false), 0, "usual: a game slot is no own slot");
            Eq(BarMapping.OwnSlot(19, false), 1, "usual: bar 19 is own 1");
            Eq(BarMapping.OwnSlot(DefSettings.BarSlotsMax, false), DefSettings.BarSlotsMax - DefSettings.RetailSlots, "usual: the last bar slot");
            Eq(BarMapping.KeyAction(3, false), "UseQuickSlot_3", "usual: a game slot's keys are AC's keymap action");
            Eq(BarMapping.KeyAction(20, false), "/rdef use 2", "usual: bar 20's keys run /rdef use 2");
            Eq(BarMapping.BarNumber(1, false), 19, "usual: own 1 shows at 19");

            // Independent: no AC slot is mapped; bar N is own N, keys /rdef use N.
            for (int n = 1; n <= DefSettings.BarSlotsMax; n++)
            {
                if (BarMapping.IsRetail(n, true)) { Check(false, $"independent: bar {n} must not be a game slot"); break; }
                if (BarMapping.OwnSlot(n, true) != n || BarMapping.BarNumber(n, true) != n) { Check(false, $"independent: bar {n} is own {n}"); break; }
            }
            Eq(BarMapping.KeyAction(1, true), "/rdef use 1", "independent: bar 1's keys are an engine hotkey");
            Eq(BarMapping.KeyAction(18, true), "/rdef use 18", "independent: bar 18 too");
            Check(DefSettings.SlotsMax >= DefSettings.BarSlotsMax, "an independent bar as long as the usual one has an own slot for every bar slot");
            for (int own = 1; own <= DefSettings.BarSlotsMax - DefSettings.RetailSlots; own++)
                if (BarMapping.OwnSlot(BarMapping.BarNumber(own, false), false) != own) { Check(false, "usual: own -> bar -> own round trip " + own); break; }
            Check(true, "mapping round trips");
        }

        private static void BarRetailKit()
        {
            var kit = Kit(0x7100_0001u);
            var potion = Potion(0x7100_0002u);
            var sword = Melee(0x7100_0003u, "Sword");
            var nameKit = Item(0x7100_0004u, "Greater Stamina Kit");   // the engine couldn't read the bitfield: RynthAi's name test
            var items = new List<CarriedItem> { kit, potion, sword, nameKit };

            Check(BarMapping.RetailKit(kit.Id, items) == kit, "a healing kit in a game slot is the use-on-self path");
            Check(BarMapping.RetailKit(nameKit.Id, items) == nameKit, "a kit known by its name only, too");
            Check(BarMapping.RetailKit(potion.Id, items) == null, "a potion stays AC's");
            Check(BarMapping.RetailKit(sword.Id, items) == null, "a weapon stays AC's");
            Check(BarMapping.RetailKit(0x7100_00FFu, items) == null, "an object not in the pack stays AC's");
            Check(BarMapping.RetailKit(0, items) == null, "an empty slot (or a spell) stays AC's");

            // Through the session: the click becomes a use on yourself in the use controller.
            var rig = new Rig("retail-kit");
            rig.Inv = items;
            Check(!rig.S.UseRetailSlot(2, potion.Id) && !rig.S.Use.HasPending, "a potion click is handed to AC (nothing queued here)");
            Check(rig.S.UseRetailSlot(1, kit.Id), "a kit click is taken");
            UseRequest? req = rig.S.Use.Pending;
            Check(req != null && req.Mode == UseMode.OnSelf && req.Item!.Id == kit.Id && req.Source.Contains("game slot 1"), "queued as a use on yourself");

            var world = new FakeWorld { Inventory = items };
            rig.S.Use.Tick(world);
            Check(world.Sent.Count == 1 && world.Sent[0] == $"useon {kit.Id:X} me", "sent as UseObjectOn(kit, you): " + string.Join(";", world.Sent));
        }

        private static void BarIndependentCopy()
        {
            var b = new BarRig("copy");
            var kit = Kit(0x7200_0001u);
            var potion = Potion(0x7200_0002u);
            var sword = Melee(0x7200_0003u, "Sword");
            var own1 = Potion(0x7200_0010u, "Mana Potion", 3);
            var own2 = Melee(0x7200_0011u, "Axe");
            b.R.Inv = new List<CarriedItem> { kit, potion, sword, own1, own2 };
            // The usual bar: AC's slots 1, 2, 5 hold items, 7 a spell, 9 an item not in the pack (named), 10 one nobody can name.
            b.RetailIds[0] = kit.Id; b.RetailIds[1] = potion.Id; b.RetailIds[4] = sword.Id;
            b.RetailSpells[6] = 1234;
            b.RetailIds[8] = 0x7200_0099u; b.Names[0x7200_0099u] = "Lucky Gold Letter";
            b.RetailIds[9] = 0x7200_00AAu;
            // Own slots 1 and 2 (bar 19 and 20), with keys on own 1.
            b.S.SetSlot(1, own1);
            b.S.SetSlot(2, own2);
            b.S.Data.Slots[1].Mode = UseMode.Equip;
            b.CommandKeys["/rdef use 1"] = "F9, Ctrl+F9";
            DefData before = DefStore.Parse(System.Text.Json.JsonDocument.Parse(DefStore.ToJson(b.S.Data)).RootElement);

            // Not independent yet: no copy.
            Check(b.S.CopyRetailSlots().Contains("independent first") && b.S.Data.Slots[0].Item!.Id == own1.Id, "the copy needs the independent bar");

            int saves = b.R.Saves;
            string m = b.S.SetIndependent(true);
            Check(b.S.Settings.IndependentBar && b.R.Saves > saves, "turned on, saved");
            Eq(b.S.CopyOffer, 6, "the offer counts the game's slots with an item or a spell");
            Check(m.Contains("independent") && m.Contains("6 shortcuts") && m.Contains("offers to copy"), "and says so: " + m);
            Check(b.S.Data.Slots[0].Item!.Id == own1.Id && b.S.Data.Slots[1].Item!.Id == own2.Id && b.S.Data.Slots[2].Empty,
                "turning it on copies nothing by itself");

            m = b.S.CopyRetailSlots();
            Eq(b.S.CopyOffer, 0, "the offer is answered");
            List<DefSlot> s = b.S.Data.Slots;
            Check(s[0].Item?.Id == kit.Id && s[1].Item?.Id == potion.Id && s[4].Item?.Id == sword.Id, "the game's items are in own 1, 2, 5");
            Check(s[0].Mode == UseMode.Auto && s[0].Item!.Name == "Peerless Healing Kit" && s[0].Item!.Wcid == 628, "copied with their identity, automatic use");
            Check(s[6].IsSpell && s[6].SpellId == 1234 && s[6].Item == null, "a spell goes in as a spell slot");
            Check(s[8].Item?.Name == "Lucky Gold Letter" && s[8].Item!.Id == 0x7200_0099u, "an item the pack read missed goes by the name the client knows");
            Check(s[9].Empty, "one nobody can name stays empty");
            Check(s[2].Empty && s[3].Empty && s[17].Empty, "the game's empty slots stay empty");
            Check(s[18].Item?.Id == own1.Id && s[19].Item?.Id == own2.Id && s[19].Mode == UseMode.Equip, "the own slots moved to 19 and 20, modes and all");
            Check(s.Skip(20).All(x => x.Empty), "nothing past them");
            Eq(s.Count, DefSettings.SlotsMax, "still 42 own slots");
            Check(b.Bound.Count == 2 && b.Bound.Contains((19, "F9")) && b.Bound.Contains((19, "Ctrl+F9")), "own 1's keys follow it to 19: " + string.Join(";", b.Bound));
            Check(m.Contains("Copied 5") && m.Contains("1 couldn't") && m.Contains("2 slots moved") && m.Contains("2 keys"), "and it says what it did: " + m);
            for (int n = 1; n <= 20; n++)
            {
                // The independent bar shows what the usual one did: bar N is own N.
                ItemRef? shown = s[BarMapping.OwnSlot(n, true) - 1].Item;
                ItemRef? was = n <= DefSettings.RetailSlots ? null : before.Slots[BarMapping.OwnSlot(n, false) - 1].Item;
                if (n > DefSettings.RetailSlots && shown?.Id != was?.Id) { Check(false, $"bar {n} keeps its item"); break; }
            }

            // Declining, turning off, refusing.
            b.S.SetIndependent(false);
            b.S.SetIndependent(true);
            b.S.DeclineCopy();
            Eq(b.S.CopyOffer, 0, "no thanks clears the offer");
            m = b.S.SetIndependent(false);
            Check(!b.S.Settings.IndependentBar && b.S.CopyOffer == 0 && m.Contains("19"), "turned off: no offer, and says where the own slots are");
            Check(s[0].Item?.Id == kit.Id && s[18].Item?.Id == own1.Id, "turning off changes no slot");

            var full = new BarRig("copy-full");
            full.R.Inv = new List<CarriedItem> { kit, own1 };
            full.RetailIds[0] = kit.Id;
            full.S.SetSlot(30, own1);
            full.S.SetIndependent(true);
            m = full.S.CopyRetailSlots();
            Check(m.Contains("slot 30") && m.Contains("no room") && full.S.Data.Slots[0].Empty && full.S.Data.Slots[29].Item?.Id == own1.Id,
                "an own slot past 24 in use: refused, nothing changed: " + m);

            var unread = new BarRig("copy-unread") { RetailReadable = false };
            m = unread.S.SetIndependent(true);
            Check(unread.S.CopyOffer == 0 && !m.Contains("offers"), "AC's slots unreadable: no offer");
            Check(unread.S.CopyRetailSlots().Contains("can't be read"), "and no copy");

            // The pure copy on its own: an empty bar gets the game's items only.
            var own = DefData.NewSlots();
            BarCopyResult r = BarMapping.CopyRetail(own, new uint[] { kit.Id }, new uint[1], id => id == kit.Id ? ItemRef.From(kit) : null);
            Check(r.Refused == null && r.Copied == 1 && r.Moved.Count == 0 && own[0].Item?.Id == kit.Id && own.Skip(1).All(x => x.Empty), "an empty bar: just the copy");
        }

        private static void BarStoreIndependent()
        {
            var st = new DefStore(Path.Combine(_temp, "indep"));
            var d = new DefData();
            Check(!d.Settings.IndependentBar, "off by default");
            d.Settings.IndependentBar = true;
            Check(st.Save("S", "Indie", d) == null, "saved");
            string json = File.ReadAllText(st.PathFor("S", "Indie"));
            Check(json.Contains("\"independentBar\": true"), "written in the file");
            Check(st.Load("S", "Indie", out string? problem).Settings.IndependentBar && problem == null, "read back on");

            // A file from before the setting existed: off, everything else as it was.
            string old = st.PathFor("S", "Old");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(old, "{\"version\":1,\"settings\":{\"slotCount\":8,\"barSlots\":20,\"swapWhileBotRuns\":true},"
                                   + "\"slots\":[{\"item\":{\"id\":5,\"name\":\"Kit\"},\"mode\":0}]}");
            DefData o = st.Load("S", "Old", out problem);
            Check(problem == null && !o.Settings.IndependentBar && o.Settings.BarSlots == 20 && o.Settings.SwapWhileBotRuns && o.Slots[0].Item?.Name == "Kit",
                "an old file loads with the independent bar off");
            File.WriteAllText(old, "{\"settings\":{\"independentBar\":\"yes\"}}");
            Check(!st.Load("S", "Old", out _).Settings.IndependentBar, "a string is not a bool: off");
        }

        private static void BarSettingsWindow()
        {
            var b = new BarRig("settings-window");
            var kit = Kit(0x7300_0001u);
            b.R.Inv = new List<CarriedItem> { kit };
            b.RetailIds[0] = kit.Id;
            var host = new UiHost();
            UiWindow win = host.Add(new UiWindow("RynthDefective/Settings", "Defective Settings") { Visible = true, ShowInBar = false });
            var view = new BarSettingsView(b.S);
            var shown = new Dictionary<string, bool> { [BarSettingsView.BarTitle] = true, [BarSettingsView.SelectionTitle] = false };
            var told = new List<string>();
            view.WindowShown = t => shown.TryGetValue(t, out bool v) ? v : null;   // no Game Panels on this "engine"
            view.ShowWindow = (t, v) => shown[t] = v;
            view.Tell = told.Add;
            int pickers = 0;
            view.ShowWeapons = () => pickers++;
            void Draw() { win.Begin(); view.Draw(win); win.End(); }
            string Text() => Encoding.UTF8.GetString(win.Body);

            // The test window helpers send events by widget id.
            var w = new Windows("settings-window-events");
            w.Host.Add(win);
            void Click(string id) => w.Click(win, id);
            void SetBool(string id, bool v) => w.SetBool(win, id, v);

            Draw();
            ParseAll(host, "settings window", out int ops);
            Check(ops < 1500, "inside the engine's limits");
            Check(Text().Contains("Allow weapon swaps while RynthAi's macro runs"), "the weapon swap switch is reachable");
            Check(Text().Contains("Shortcuts bar") && Text().Contains("Selection") && !Text().Contains("Game panel buttons"),
                "the windows RynthDefective draws (Game Panels only where the engine has it)");
            Check(Text().Contains("RynthDefective draws the Shortcuts bar, the Game Panels buttons and the Selection window"), "it says what draws them");

            SetBool("set.win.sel", true);
            Check(shown[BarSettingsView.SelectionTitle], "the Selection box shows the window");
            SetBool("set.swap", true);
            Check(b.S.Settings.SwapWhileBotRuns, "the swap switch");
            Check(Text().Contains("Weapon picker...") && Text().Contains("Weapons button on the bar"), "the picker button and the bar-button switch are in the settings");
            Click("set.weapons");
            Eq(pickers, 1, "Weapon picker... opens the picker");
            Check(!b.S.Settings.WeaponsButtonOnBar, "the bar has no Weapons button by default");
            SetBool("set.weaponsbtn", true);
            Check(b.S.Settings.WeaponsButtonOnBar, "the switch puts it back");
            SetBool("set.weaponsbtn", false);
            Check(!b.S.Settings.WeaponsButtonOnBar, "and takes it off again");

            SetBool("set.indep", true);
            Check(b.S.Settings.IndependentBar && told.Count == 1 && told[0].Contains("independent"), "the independent switch, told in chat");
            Eq(b.S.CopyOffer, 1, "the copy is offered");
            Draw();
            ParseAll(host, "settings window with the offer", out _);
            Check(Text().Contains("Copy them") && Text().Contains("No thanks"), "the offer shows");
            Click("set.copy");
            Check(b.S.Data.Slots[0].Item?.Id == kit.Id && b.S.CopyOffer == 0 && told.Count == 2 && told[1].Contains("Copied 1"), "Copy them copies");
            Draw();
            Check(!Text().Contains("Copy them"), "and the offer is gone");

            SetBool("set.indep", false);
            SetBool("set.indep", true);
            Draw();
            Click("set.nocopy");
            Eq(b.S.CopyOffer, 0, "No thanks declines");

            b.S.LoggedIn = false;
            Draw();
            ParseAll(host, "settings window logged out", out _);
            Check(Text().Contains("Log in"), "logged out it says so");
        }
    }
}
