using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthSort;
using RynthCore.Plugin.RynthSort.Sorting;
using RynthCore.Plugin.RynthSort.Views;

namespace RynthSort.Tests
{
    // Rules tied to pack ids, the one-time fix of imported Lua pack numbers, the pack picker's labels
    // and spell component classification.
    internal static partial class Program
    {
        private const uint FocusA = 0x40000F01, FocusB = 0x40000F02;

        /// <summary>
        /// A main pack whose side slots are, in the client's list order: a focus, pack A, a focus,
        /// pack B, pack C. The Lua script numbered those 1-5; RynthSort's packs are A, B, C (1-3).
        /// </summary>
        private static SortInventory WithFoci()
        {
            var inv = new SortInventory();
            inv.Main.Id = Me;
            inv.Main.Name = "Main pack";
            inv.Main.Capacity = 102;
            inv.Slots.Add(new SortSlot(FocusA, 0));
            inv.Slots.Add(new SortSlot(0x40000001, 24));
            inv.Slots.Add(new SortSlot(FocusB, 0));
            inv.Slots.Add(new SortSlot(0x40000002, 24));
            inv.Slots.Add(new SortSlot(0x40000003, 24));
            foreach (SortSlot s in inv.Slots)
                if (s.IsPack) inv.Packs.Add(new SortPack { Id = s.Id, Name = "Pack", Capacity = 24 });
            inv.Packs[0].Items.Add(It(500, "Salvaged Iron", ObjectClasses.Salvage));
            inv.Packs[1].Items.Add(It(501, "Black Opal", ObjectClasses.Gem));
            inv.Packs[2].Items.Add(It(502, "Lead Scarab", ObjectClasses.SpellComponent));
            return inv;
        }

        private static void ClassifyComponents()
        {
            // ItemType SpellComponents (0x1000) on ACE: the scarabs (Lead 691 .. Platinum 8897, Mana
            // Scarab 37155) and Prismatic Taper (20631, weenie type SpellComponent) all have 4096.
            foreach (string name in new[] { "Lead Scarab", "Platinum Scarab", "Mana Scarab", "Dark Scarab", "Prismatic Taper", "Red Taper", "Hyssop", "Ink of Formation" })
            {
                Eq(InventoryReader.ClassifyFrom(0x00001000, 0, name, out bool certain), ObjectClasses.SpellComponent, name);
                Check(certain, name + ": certain");
                // An item's ordinary bitfield bits (stuck, attackable) don't change it.
                Eq(InventoryReader.ClassifyFrom(0x00001000, 0x00000014, name, out _), ObjectClasses.SpellComponent, name + " with bitfield bits");
            }
            var comps = SortRule.ForClass(ObjectClasses.SpellComponent, 1);
            Check(SortRules.Matches(comps, It(1, "Platinum Scarab", InventoryReader.ClassifyFrom(0x1000, 0, "Platinum Scarab", out _))), "a component rule takes scarabs");
            Check(SortRules.Matches(comps, It(2, "Prismatic Taper", InventoryReader.ClassifyFrom(0x1000, 0, "Prismatic Taper", out _))), "and tapers");
        }

        private static void ResolveById()
        {
            SortInventory inv = Inv(3);
            var gem = SortRule.ForClass(ObjectClasses.Gem, 1);
            gem.PackId = 0x40000003;
            Eq(SortRules.Resolve(gem, inv, out bool fell), 3, "the pack its id names, not its number");
            Check(!fell, "not a fallback");

            inv.Main.Items.Add(It(10, "Black Opal", ObjectClasses.Gem));
            var p = new SortPlanner();
            List<SortMove> m = p.Plan(inv, new List<SortRule> { gem }, SortScope.Main, true, 0, 0);
            Check(m.Count == 1 && m[0].Dest.Id == 0x40000003 && m[0].DestIndex == 3, "moved to the pack with its id");
            Eq(p.View.Issues.Count, 0, "no warning");

            // A new pack shows up ahead of it in the list: the rule follows its pack.
            inv.Packs.Insert(0, new SortPack { Id = 0x40000009, Name = "New", Capacity = 24 });
            Eq(SortRules.Resolve(gem, inv, out _), 4, "its pack is now 4th: still the one used");
            m = p.Plan(inv, new List<SortRule> { gem }, SortScope.Main, true, 0, 0);
            Check(m.Count == 1 && m[0].Dest.Id == 0x40000003, "still moved to the same pack");
            Check(PackFix.Sync(new List<SortRule> { gem }, inv) && gem.Pack == 4, "its number follows (for the window and the fallback)");
            Check(!PackFix.Sync(new List<SortRule> { gem }, inv), "no change the second time");

            // Already in its pack: Tidy leaves it there.
            inv.Main.Items.Clear();
            inv.Packs[3].Items.Add(It(11, "Black Opal", ObjectClasses.Gem));
            Eq(p.Plan(inv, new List<SortRule> { gem }, SortScope.All, true, 0, 0).Count, 0, "in its pack: no move");

            // Its pack isn't carried: the number decides, and the window says so.
            SortInventory inv2 = Inv(3);
            inv2.Main.Items.Add(It(12, "Black Opal", ObjectClasses.Gem));
            var gone = SortRule.ForClass(ObjectClasses.Gem, 2);
            gone.PackId = 0x4000FFFF;
            gone.PackNote = "Sack - Gems";
            Eq(SortRules.Resolve(gone, inv2, out fell), 2, "not carried: its number");
            Check(fell, "a fallback");
            m = p.Plan(inv2, new List<SortRule> { gone }, SortScope.Main, true, 0, 0);
            Check(m.Count == 1 && m[0].DestIndex == 2, "moved by number");
            Check(p.View.Issues.Count == 1 && p.View.Issues[0].UsedPack == 2 && p.View.Missing.Count == 0, "reported as a fallback, not missing");

            // Not carried and no such number: nowhere.
            gone.Pack = 5;
            Eq(SortRules.Resolve(gone, inv2, out _), 0, "nowhere");
            m = p.Plan(inv2, new List<SortRule> { gone }, SortScope.Main, true, 0, 0);
            Check(m.Count == 0 && p.View.Missing.Contains(5) && p.View.Issues.Count == 1 && p.View.Issues[0].UsedPack == 0, "no move, reported missing");
            Eq(p.View.Waiting, 1, "the item waits");

            // A rule with no id is the number, as before; one that is off isn't reported.
            Eq(SortRules.Resolve(SortRule.ForClass(ObjectClasses.Gem, 3), inv2, out fell), 3, "no id: the number");
            Check(!fell, "no id: not a fallback");
            gone.On = false;
            p.Plan(inv2, new List<SortRule> { gone }, SortScope.Main, true, 0, 0);
            Eq(p.View.Issues.Count, 0, "a rule that is off isn't reported");
        }

        private static List<SortRule> LuaRules() => new()
        {
            SortRule.ForClass(ObjectClasses.SpellComponent, 1),   // a focus
            SortRule.ForClass(ObjectClasses.Salvage, 2),          // pack A
            SortRule.ForClass(ObjectClasses.Gem, 3),              // a focus
            SortRule.ForClass(ObjectClasses.Key, 4),              // pack B
            SortRule.ForClass(ObjectClasses.HealingKit, 5),       // pack C
            SortRule.ForClass(ObjectClasses.TradeNote, 6),        // nothing
        };

        private static void PackFixImported()
        {
            SortInventory inv = WithFoci();
            List<SortRule> rules = LuaRules();
            List<string> lines = PackFix.Apply(rules, inv, imported: true, LuaRules(), out int changed);
            Eq(changed, 3, "three rules pointed at a pack");
            Eq(lines.Count, 6, "a line for every rule");
            Check(rules[0].PackId == 0 && rules[0].Pack == 1 && lines[0].Contains("a focus") && lines[0].Contains("left as pack 1"), "Lua pack 1 was a focus: left, and said so");
            Check(rules[1].PackId == 0x40000001 && rules[1].Pack == 1, "Lua pack 2 = pack A = RynthSort's pack 1");
            Check(lines[1].Contains("the Lua script's pack 2 -> pack 1") && lines[1].Contains("Pack - Salvage") && lines[1].Contains("0x40000001"), $"logged ({lines[1]})");
            Check(rules[2].PackId == 0 && rules[2].Pack == 3 && lines[2].Contains("a focus"), "Lua pack 3 was a focus");
            Check(rules[3].PackId == 0x40000002 && rules[3].Pack == 2, "Lua pack 4 = pack B = 2");
            Check(rules[4].PackId == 0x40000003 && rules[4].Pack == 3 && rules[4].PackNote == "Pack - Components", "Lua pack 5 = pack C = 3, remembered with its contents");
            Check(rules[5].PackId == 0 && rules[5].Pack == 6 && lines[5].Contains("doesn't exist") && lines[5].Contains("3 packs and 2 foci"), $"Lua pack 6 doesn't exist ({lines[5]})");

            // Every rule with an id now: a second run changes nothing.
            List<string> again = PackFix.Apply(rules, inv, true, LuaRules(), out int changed2);
            Eq(changed2, 0, "rules with ids are skipped");
            Eq(again.Count, 3, "only the three still without one are looked at");

            // The Lua file unreadable (null): every rule is taken as the Lua script's.
            List<SortRule> r2 = LuaRules();
            PackFix.Apply(r2, inv, true, null, out int c3);
            Check(c3 == 3 && r2[3].PackId == 0x40000002, "no Lua file to compare with: all as imported");

            // No foci: Lua numbers and RynthSort's agree; the fix only adds the ids.
            SortInventory plain = Inv(3);
            foreach (SortPack pk in plain.Packs) plain.Slots.Add(new SortSlot(pk.Id, 24));
            List<SortRule> r3 = SortRules.Defaults();
            PackFix.Apply(r3, plain, true, SortRules.Defaults(), out int c4);
            Check(c4 == 3 && r3[0].PackId == 0x40000001 && r3[0].Pack == 1 && r3[2].PackId == 0x40000003 && r3[2].Pack == 3, "no foci: same numbers, ids added");

            // Rules RynthSort saved (not imported): each keeps its number and takes that pack's id.
            List<SortRule> own = SortRules.Defaults();
            List<string> ownLines = PackFix.Apply(own, inv, imported: false, null, out int c5);
            Check(c5 == 3 && own[0].PackId == 0x40000001 && own[1].PackId == 0x40000002 && own[2].PackId == 0x40000003, "RynthSort's numbers count real packs: pinned as they are");
            Check(ownLines[0].Contains("pack 1 kept"), $"logged ({ownLines[0]})");

            Check(PackFix.Ready(inv), "every capacity read: ready");
            inv.Slots[2] = new SortSlot(FocusB, -1);
            Check(!PackFix.Ready(inv), "a capacity not readable yet: not ready (could be a pack)");
        }

        // Drakkon's rules as RynthSort's file had them on 2026-10-11 (imported, then edited in the
        // window), against his Lua script's file (2026-10-07): only rules 5 and 6 are still the Lua
        // script's (keys 3, kits 4); the rest were picked in RynthSort's window, in its numbers.
        private static List<SortRule> DrakkonNow() => new()
        {
            SortRule.ForClass(ObjectClasses.SpellComponent, 4),
            SortRule.ForClass(ObjectClasses.Salvage, 1),
            SortRule.ForClass(ObjectClasses.Gem, 4),
            SortRule.ForClass(ObjectClasses.ManaStone, 4),
            SortRule.ForClass(ObjectClasses.Key, 3),
            SortRule.ForClass(ObjectClasses.HealingKit, 4),
            SortRule.ForClass(ObjectClasses.TradeNote, 4),
            SortRule.ForName("Ink of", 2),
            SortRule.ForName("Prismatic Taper", 4),
        };

        private static void PackFixDrakkon()
        {
            SortStore st = TempStore("drakkon");
            string lua = st.LuaPathFor("DreamWeave", "Drakkon");
            Directory.CreateDirectory(Path.GetDirectoryName(lua)!);
            File.WriteAllText(lua, LuaFile);
            var d = new CharacterSortData { Rules = DrakkonNow(), ImportedFrom = lua };
            Check(st.Save("DreamWeave", "Drakkon", d) == null, "saved like the old file");
            // The old file had no packsFixed and version 1.
            string own = st.PathFor("DreamWeave", "Drakkon");
            File.WriteAllText(own, File.ReadAllText(own).Replace("\"packsFixed\": false", "\"x\": 0").Replace("\"version\": 3", "\"version\": 1"));
            CharacterSortData loaded = st.Load("DreamWeave", "Drakkon", out string? problem);
            Check(problem == null && !loaded.PacksFixed && loaded.LuaRules != null && loaded.LuaRules.Count == 6, "imported, not fixed: the Lua file's rules read to compare with");

            // A layout with one focus: slot 1 pack A, slot 2 a focus, slot 3 pack B, slot 4 pack C.
            var inv = new SortInventory();
            inv.Main.Id = Me;
            inv.Slots.Add(new SortSlot(0x40000001, 24));
            inv.Slots.Add(new SortSlot(FocusA, 0));
            inv.Slots.Add(new SortSlot(0x40000002, 24));
            inv.Slots.Add(new SortSlot(0x40000003, 24));
            foreach (SortSlot s in inv.Slots)
                if (s.IsPack) inv.Packs.Add(new SortPack { Id = s.Id, Name = "Pack", Capacity = 24 });
            List<SortRule> r = loaded.Rules;
            List<string> lines = PackFix.Apply(r, inv, true, loaded.LuaRules, out int changed);
            Check(r[4].PackId == 0x40000002 && r[4].Pack == 2, "keys: the Lua script's pack 3 (after the focus) -> pack 2");
            Check(r[5].PackId == 0x40000003 && r[5].Pack == 3, "kits: the Lua script's pack 4 -> pack 3");
            Check(r[1].PackId == 0x40000001 && r[1].Pack == 1, "salvage (picked in RynthSort): pack 1 kept");
            Check(r[7].PackId == 0x40000002 && r[7].Pack == 2, "Ink of (picked in RynthSort): pack 2 kept");
            Check(r[0].PackId == 0 && r[0].Pack == 4 && lines[0].Contains("which you don't have"), "components to RynthSort's pack 4: there are only 3, left (and said)");
            Check(r[8].PackId == 0 && r[8].Pack == 4, "Prismatic Taper to pack 4: the same");
            Eq(changed, 4, "four rules tied to their packs");
            Eq(lines.Count, 9, "a line per rule");
        }

        private static void PackFixLoop()
        {
            var w = new FakeWorld { Inv = WithFoci() };
            w.Inv.Main.Items.Add(It(10, "Platinum Scarab", ObjectClasses.SpellComponent));
            w.Inv.Main.Items.Add(It(11, "Healing Kit", ObjectClasses.HealingKit));
            SortController c = Controller();
            c.Data.Rules = LuaRules();
            c.Data.ImportedFrom = "lua.json";
            c.Data.LuaRules = LuaRules();
            List<string>? fixLines = null;
            int fixChanged = -1;
            c.PacksFixed = (l, n) => { fixLines = l; fixChanged = n; };

            Ticks(c, w, 0, SortController.FixSettleMs - 100);
            Eq(w.Sent.Count, 0, "imported rules not fixed yet: nothing moves");
            Eq(c.Status, "reading your packs", "status says why");
            Check(!c.Data.PacksFixed && fixLines == null, "not before the packs have read the same for a while");

            Ticks(c, w, SortController.FixSettleMs - 100 + 16, SortController.FixSettleMs + 1500);
            Check(c.Data.PacksFixed && c.Data.LuaRules == null, "fixed once settled");
            Check(fixLines != null && fixLines.Count == 6 && fixChanged == 3, "the plugin hears it (to log and save)");
            Check(c.Log.Any(x => x.Contains("Imported pack numbers fixed: 3 of 6")), "noted in the window");
            Check(w.Sent.Any(m => m.Item.Id == 11 && m.Dest.Id == 0x40000003), "kits (the Lua script's pack 5) go to pack C");
            // Components named the Lua script's pack 1, a focus: left as it was, "pack 1", which is
            // RynthSort's first real pack (the log line says so; Tom picks the right one).
            Check(w.Sent.Any(m => m.Item.Id == 10 && m.Dest.Id == 0x40000001), "a rule left as it was keeps its number");
            Check(c.Data.Rules[0].PackId == 0, "and has no id until one is picked");
        }

        private static void PackLabels()
        {
            var inv = new SortInventory();
            inv.Main.Id = Me;
            for (int i = 1; i <= 3; i++) inv.Packs.Add(new SortPack { Id = 0x40000000u + (uint)i, Name = "Pack", Capacity = 24 });
            for (uint i = 0; i < 10; i++) inv.Packs[0].Items.Add(It(100 + i, "Salvaged Iron", ObjectClasses.Salvage));
            for (uint i = 0; i < 5; i++) inv.Packs[0].Items.Add(It(200 + i, "Black Opal", ObjectClasses.Gem));
            inv.Packs[0].Items.Add(It(300, "Legendary Key", ObjectClasses.Key));
            inv.Packs[0].Items.Add(It(301, "Pyreal Mote", ObjectClasses.Misc));
            inv.Packs[1].Items.Add(It(400, "Lead Scarab", ObjectClasses.SpellComponent));
            inv.Packs[1].Items.Add(It(401, "Pyreal Mote", ObjectClasses.Misc));
            inv.Packs[1].Items.Add(It(402, "Pyreal Mote", ObjectClasses.Misc));

            Eq(inv.Packs[0].Summary(), "Salvage, Gems, Keys", "the three commonest kinds");
            Eq(inv.Packs[1].Summary(), "Pyreal Mote, Components", "misc items by name");
            Eq(inv.Packs[2].Summary(), "empty", "empty");

            var p = new SortPlanner();
            p.Plan(inv, SortRules.Defaults(), SortScope.Main, true, 0, 0);
            List<string> choices = SortPanel.PackChoices(p.View, 1);
            Eq(choices[0], "Pack 1: Pack (17/24) - Salvage, Gems, Keys", "picker label: number, name, room, contents");
            Eq(choices[1], "Pack 2: Pack (3/24) - Pyreal Mote, Components", "bags with one name told apart by contents");
            Eq(choices[2], "Pack 3: Pack (0/24) - empty", "an empty pack");
            Eq(choices[3], "Pack 4 (none)", "then numbers you don't have");

            // Picking stores the id; the row shows the pack it resolves to.
            var rule = SortRule.ForClass(ObjectClasses.Gem, 1);
            SortPanel.SetPack(rule, p.View, 2);
            Check(rule.Pack == 2 && rule.PackId == 0x40000002 && rule.PackNote == "Pack - Pyreal Mote, Components", "picked: number, id and note");
            Eq(SortPanel.ShownPack(p.View, rule), 2, "the rule is listed under pack 2");
            SortPanel.SetPack(rule, p.View, 6);
            Check(rule.Pack == 6 && rule.PackId == 0 && rule.PackNote == "", "a number you don't have: no id");
            Check(!p.View.PackOrderKnown && SortPanel.PackTooltip(p.View).Contains("list order") && !SortPanel.PackTooltip(p.View).Contains("top of the pack column"),
                "pack order unknown: the tooltip doesn't claim screen order");
            inv.PackOrderKnown = true;
            p.Plan(inv, SortRules.Defaults(), SortScope.Main, true, 0, 0);
            Check(SortPanel.PackTooltip(p.View).Contains("top of the pack column"), "pack order known: numbered from the top");

            // Warnings sit with their pack: a rule that fell back to a number is listed (and warned
            // about) under the pack it uses; rules whose pack isn't carried are grouped by that pack.
            var gems = SortRule.ForClass(ObjectClasses.Gem, 7);
            gems.PackId = 0x4000AAAA; gems.PackNote = "Sack - Gems";
            var keys = SortRule.ForClass(ObjectClasses.Key, 7);
            keys.PackId = 0x4000AAAA; keys.PackNote = "Sack - Gems";
            var salvage = SortRule.ForClass(ObjectClasses.Salvage, 1);
            salvage.PackId = 0x4000BBBB; salvage.PackNote = "Bag - Salvage";
            var rules = new List<SortRule> { SortRule.ForClass(ObjectClasses.SpellComponent, 1), gems, keys, SortRule.ForClass(ObjectClasses.ManaStone, 6), salvage };
            inv.PackOrderKnown = false;
            p.Plan(inv, rules, SortScope.Main, true, 0, 0);
            Eq(SortPanel.FallbackWarnings(p.View, 1).Count, 1, "one fallback warning, under pack 1");
            Eq(SortPanel.FallbackWarnings(p.View, 1)[0], "Salvage: its pack (Bag - Salvage) isn't carried, so this one is used", "with what the pack was");
            Eq(SortPanel.FallbackWarnings(p.View, 2).Count, 0, "none under pack 2");
            Dictionary<int, List<SortRule>> groups = SortPanel.Group(rules, p.View);
            Check(groups[1].Count == 2 && groups[1].Contains(salvage), "pack 1 lists its own rule and the fallback");
            Check(groups[0].Count == 3 && groups[0].Contains(gems) && groups[0].Contains(keys), "rules for packs you don't have, together");
        }

        private static void StorePackIds()
        {
            SortStore st = TempStore("packids");
            CharacterSortData d = st.Load("S", "A", out _);
            Check(!d.PacksFixed, "new character: the defaults get tied to packs on the first read");
            d.Rules[0].PackId = 0x8000ABCD;
            d.Rules[0].PackNote = "Sack - Components";
            d.PacksFixed = true;
            st.Save("S", "A", d);
            string json = File.ReadAllText(st.PathFor("S", "A"));
            Check(json.Contains("\"version\": 3") && json.Contains("\"packId\": 2147527629") && json.Contains("\"packsFixed\": true"), "version 2 with pack ids");
            Check(!json.Contains("\"packId\": 0"), "no packId written for rules without one");
            CharacterSortData back = st.Load("S", "A", out string? problem);
            Check(problem == null && back.PacksFixed && back.Rules[0].PackId == 0x8000ABCD && back.Rules[0].PackNote == "Sack - Components" && back.Rules[1].PackId == 0, "read back");
            Check(back.LuaRules == null, "not imported: nothing to compare with");

            // A version 1 file: rules by number, not fixed.
            File.WriteAllText(st.PathFor("S", "A"), "{\"version\":1,\"rules\":[{\"kind\":\"class\",\"class\":11,\"pack\":3}]}");
            CharacterSortData v1 = st.Load("S", "A", out problem);
            Check(problem == null && !v1.PacksFixed && v1.Rules[0].PackId == 0 && v1.Rules[0].Pack == 3, "version 1 loads: number only, fix pending");
            File.WriteAllText(st.PathFor("S", "A"), "{\"rules\":[{\"pack\":3,\"packId\":-5},{\"pack\":2,\"packId\":3.5},{\"pack\":1,\"packId\":1073741825.0}]}");
            CharacterSortData odd = st.Load("S", "A", out _);
            Check(odd.Rules[0].PackId == 0 && odd.Rules[1].PackId == 0 && odd.Rules[2].PackId == 0x40000001, "odd ids read as none; a whole double is fine");

            // A fresh import: the Lua rules kept to compare with, not fixed.
            SortStore imp = TempStore("packids-import");
            string lua = imp.LuaPathFor("DreamWeave", "Drakkon");
            Directory.CreateDirectory(Path.GetDirectoryName(lua)!);
            File.WriteAllText(lua, LuaFile);
            CharacterSortData fresh = imp.Load("DreamWeave", "Drakkon", out _);
            Check(!fresh.PacksFixed && fresh.LuaRules != null && fresh.LuaRules.Count == 6 && !ReferenceEquals(fresh.LuaRules[0], fresh.Rules[0]), "fresh import: Lua rules copied, fix pending");

            // Saved with a backup: the old file kept beside it.
            imp.Save("DreamWeave", "Drakkon", fresh);
            string before = File.ReadAllText(imp.PathFor("DreamWeave", "Drakkon"));
            fresh.PacksFixed = true;
            Check(imp.Save("DreamWeave", "Drakkon", fresh, backup: true) == null, "saved with a backup");
            string bak = imp.PathFor("DreamWeave", "Drakkon") + ".bak";
            Check(File.Exists(bak) && File.ReadAllText(bak) == before, ".bak holds the file as it was");
            CharacterSortData fixedNow = imp.Load("DreamWeave", "Drakkon", out _);
            Check(fixedNow.PacksFixed && fixedNow.LuaRules == null, "once fixed, the Lua file isn't read again");
        }
    }
}
