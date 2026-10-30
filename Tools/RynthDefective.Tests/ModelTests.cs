using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthDefective;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthDefective.Tests
{
    internal static partial class Program
    {
        // ── Kinds ───────────────────────────────────────────────────────────

        private static void KindsClassify()
        {
            Eq(ItemKinds.Classify(Kit(1)), ItemKind.Kit, "a healer bit is a kit");
            var noBits = Item(2, "Excellent Stamina Kit");
            Eq(ItemKinds.Classify(noBits), ItemKind.Kit, "no bitfield read: a name ending in Kit");
            var kitNamedThing = Item(3, "Kit Bag"); kitNamedThing.Bitfield = 0x8000;
            Eq(ItemKinds.Classify(kitNamedThing), ItemKind.Other, "a bitfield without the healer bit is not a kit");
            Eq(ItemKinds.Classify(Potion(4)), ItemKind.Other, "a potion");
            Eq(ItemKinds.Classify(Item(5, "Scroll of Creature Enchantment Mastery")), ItemKind.Other, "a scroll");

            Eq(ItemKinds.Classify(Melee(10, "Sword")), ItemKind.Melee, "melee");
            Eq(ItemKinds.Classify(Bow(11)), ItemKind.Missile, "missile");
            Eq(ItemKinds.Classify(Arrows(12)), ItemKind.Ammo, "arrows are ammunition, not a missile weapon");
            Eq(ItemKinds.Classify(Wand(13, "Wand", 0x10)), ItemKind.WarWand, "a fire wand is war magic");
            Eq(ItemKinds.Classify(Wand(14, "Wand", 0x400)), ItemKind.VoidWand, "a nether damage type is void magic");
            var glowing = Wand(15, "Staff", 0); glowing.UiEffects = 0x1000;
            Eq(ItemKinds.Classify(glowing), ItemKind.VoidWand, "no damage type read: the icon's nether glow");
            var fireGlow = Wand(16, "Staff of Flame", 0); fireGlow.UiEffects = 0x0020;
            Eq(ItemKinds.Classify(fireGlow), ItemKind.WarWand, "no damage type: a fire glow is war");
            var named = Wand(17, "Nether Orb", 0);
            Eq(ItemKinds.Classify(named), ItemKind.VoidWand, "nothing known but the name: Nether");
            var plain = Wand(18, "Wand", 0);
            Eq(ItemKinds.Classify(plain), ItemKind.WarWand, "an unknown caster is war magic");
            var nameOnly = Item(19, "Crystal Scepter"); nameOnly.ValidLocations = 0x01000000;
            Eq(ItemKinds.Classify(nameOnly), ItemKind.WarWand, "no item type read: the name says wand");

            Eq(ItemKinds.Classify(Shield(20)), ItemKind.Shield, "shield by combat use");
            var oldShield = Item(21, "Buckler"); oldShield.ItemType = ItemKinds.TypeArmor; oldShield.ValidLocations = ItemKinds.ShieldSlot;
            Eq(ItemKinds.Classify(oldShield), ItemKind.Shield, "shield by slot when it is not a weapon");
            var helm = Item(22, "Helm"); helm.ItemType = ItemKinds.TypeArmor; helm.ValidLocations = 0x1;
            Eq(ItemKinds.Classify(helm), ItemKind.Armor, "armor");
            var ring = Item(23, "Ring"); ring.ItemType = ItemKinds.TypeJewelry; ring.ValidLocations = 0x30000;
            Eq(ItemKinds.Classify(ring), ItemKind.Jewelry, "jewelry");
            var clothes = Item(24, "Shirt"); clothes.ItemType = ItemKinds.TypeClothing; clothes.ValidLocations = 0x2;
            Eq(ItemKinds.Classify(clothes), ItemKind.Armor, "clothing is worn like armor");

            Eq(ItemKinds.Element(Wand(30, "Wand", 0x08)), "Cold", "damage type cold");
            Eq(ItemKinds.Element(Wand(31, "Wand", 0x20)), "Acid", "damage type acid");
            Eq(ItemKinds.Element(plain), "", "no element known");
            Eq(ItemNames.Full("Wand", 63), "Silver Wand", "material in front");
            Eq(ItemNames.Full("Silver Wand", 63), "Silver Wand", "not twice");
            Eq(ItemNames.Full("Wand", 0), "Wand", "no material");
            Eq(ItemNames.Full("Wand", 999), "Wand", "an unknown material id");
        }

        private static void KindsActions()
        {
            Eq(ItemKinds.DefaultAction(ItemKind.Kit), UseAction.UseOnSelf, "kits go on yourself");
            Eq(ItemKinds.DefaultAction(ItemKind.Other), UseAction.Use, "potions are used");
            Eq(ItemKinds.DefaultAction(ItemKind.Melee), UseAction.Equip, "weapons are wielded");
            Eq(ItemKinds.DefaultAction(ItemKind.Armor), UseAction.Equip, "armor is worn");
            Eq(UseModes.ActionFor(UseMode.OnSelected, ItemKind.Other), UseAction.UseOnSelected, "a slot can override");
            Eq(UseModes.ActionFor(UseMode.Use, ItemKind.Kit), UseAction.Use, "a kit used by itself if asked");
            Eq(UseModes.ActionFor(UseMode.Auto, ItemKind.Kit), UseAction.UseOnSelf, "automatic follows the kind");

            Eq(ItemKinds.GroupOf(ItemKind.WarWand), WeaponGroup.WarMagic, "war group");
            Check(ItemKinds.GroupOf(ItemKind.Kit) == null && ItemKinds.GroupOf(ItemKind.Armor) == null, "kits and armor are not in the picker");
            Check(ItemKinds.NeedsAmmo(Bow(1)) && !ItemKinds.NeedsAmmo(Item(2, "Dart", 50, 250)), "a launcher needs ammunition, a thrown stack does not");
        }

        // ── Matching ────────────────────────────────────────────────────────

        private static void MatchRebind()
        {
            var r = ItemRef.From(Kit(100));
            var items = new List<CarriedItem> { Kit(100), Kit(101) };
            CarriedItem? f = ItemMatcher.Find(r, items, out bool rebound);
            Check(f?.Id == 100 && !rebound, "the exact object while it is carried");

            items.RemoveAt(0);
            f = ItemMatcher.Find(r, items, out rebound);
            Check(f?.Id == 101 && rebound, "gone: another kit of the same name, and it says so");

            items = new List<CarriedItem> { Kit(102, "Gifted Healing Kit") };
            Check(ItemMatcher.Find(r, items, out _) == null, "a different name is a different item");
            Check(ItemMatcher.Find(new ItemRef(), items, out _) == null, "a blank reference finds nothing");

            // Name case, wcid and material.
            items = new List<CarriedItem> { Kit(103, "PEERLESS HEALING KIT") };
            Check(ItemMatcher.Find(r, items, out _)?.Id == 103, "the name matches in any case");
            items = new List<CarriedItem> { Kit(104, wcid: 999) };
            Check(ItemMatcher.Find(r, items, out _) == null, "the same name with another wcid is not the same item");
            var wandRef = ItemRef.From(Wand(200, "Wand", 0x10, material: 63));   // Silver Wand
            items = new List<CarriedItem> { Wand(201, "Wand", 0x10, material: 60), Wand(202, "Wand", 0x10, material: 63) };
            Check(ItemMatcher.Find(wandRef, items, out _)?.Id == 202, "another material is another wand: the silver one");
            var unknownMaterial = new ItemRef { Name = "Wand" };
            Check(ItemMatcher.Find(unknownMaterial, items, out _)?.Id == 201, "no material known: any wand of that name (the first)");

            // An id reused by a different item must not count as the same item.
            items = new List<CarriedItem> { Item(100, "Health Potion") };
            Check(ItemMatcher.Find(r, items, out _) == null, "an object id reused for something else is not the kit");

            // Not wielded before wielded, the smaller stack first, then the lower id.
            var potRef = ItemRef.From(Potion(1));
            items = new List<CarriedItem> { Potion(7, stack: 20), Potion(8, stack: 3), Potion(9, stack: 3) };
            Check(ItemMatcher.Find(potRef, items, out _)?.Id == 8, "the smaller stack, then the lower id");
            var swordRef = ItemRef.From(Melee(50, "Sword"));
            items = new List<CarriedItem> { Melee(51, "Sword", wielded: true), Melee(52, "Sword") };
            Check(ItemMatcher.Find(swordRef, items, out _)?.Id == 52, "not the one already in hand");
        }

        private static void MatchCounts()
        {
            var potRef = ItemRef.From(Potion(1));
            var items = new List<CarriedItem> { Potion(7, stack: 20), Potion(8, stack: 3), Kit(9) };
            SlotStatus s = ItemMatcher.Status(potRef, items);
            Check(s.Item?.Id == 8 && s.Count == 23 && s.Uses == 0, "stacks added up");
            Eq(RynthCore.Plugin.RynthDefective.Views.SlotText.Count(s, out _), "x23", "a stack's label");

            var kitRef = ItemRef.From(Kit(9));
            items = new List<CarriedItem> { Kit(9, uses: 10), Kit(10, uses: 28) };
            s = ItemMatcher.Status(kitRef, items);
            Check(s.Item?.Id == 9 && s.Count == 2 && s.Uses == 38, "kits: uses added up over both");
            Eq(RynthCore.Plugin.RynthDefective.Views.SlotText.Count(s, out _), "38", "uses left are the label");

            s = ItemMatcher.Status(kitRef, new List<CarriedItem> { Potion(2) });
            Check(s.Missing && s.Count == 0, "nothing carried: missing");
            Eq(RynthCore.Plugin.RynthDefective.Views.SlotText.Count(s, out uint color), "none", "missing says none");
            Check(color == RynthCore.Plugin.RynthDefective.Views.SlotText.Missing, "in red");

            var sword = Melee(60, "Sword");
            s = ItemMatcher.Status(ItemRef.From(sword), new List<CarriedItem> { sword });
            Eq(RynthCore.Plugin.RynthDefective.Views.SlotText.Count(s, out _), "", "one weapon: no label");
            sword.WieldedLocation = 0x00100000;
            s = ItemMatcher.Status(ItemRef.From(sword), new List<CarriedItem> { sword });
            Eq(RynthCore.Plugin.RynthDefective.Views.SlotText.Count(s, out color), "in hand", "a wielded weapon");
            Check(color == RynthCore.Plugin.RynthDefective.Views.SlotText.InHand, "in green");
            var dart = Item(61, "Dart", 1, 250);
            s = ItemMatcher.Status(ItemRef.From(dart), new List<CarriedItem> { dart });
            Eq(RynthCore.Plugin.RynthDefective.Views.SlotText.Count(s, out _), "x1", "the last of a stackable kind shows x1");
        }

        private static void MatchMaintain()
        {
            var slotItem = ItemRef.From(Kit(100));
            Check(!ItemMatcher.Maintain(slotItem, new List<CarriedItem> { Kit(100) }), "nothing changed: no save");

            // The kit is used up; another is carried.
            var other = Kit(101); other.Icon = 0x06009999;
            Check(ItemMatcher.Maintain(slotItem, new List<CarriedItem> { other }), "followed to the other kit: a save is due");
            Check(slotItem.Id == 101 && slotItem.Icon == 0x06009999, "id and picture follow");
            Eq(slotItem.Name, "Peerless Healing Kit", "the name stays");

            // Missing: nothing is touched.
            Check(!ItemMatcher.Maintain(slotItem, new List<CarriedItem> { Potion(5) }) && slotItem.Id == 101, "nothing carried: the slot keeps its identity");

            // Through the session: an inventory that is not loaded yet (empty) changes nothing.
            var session = new DefSession(new HotkeyLookup(() => Path.Combine(_temp, "none.txt")));
            session.Data.Slots[0].Item = ItemRef.From(Kit(100));
            session.Data.Favourites.Add(ItemRef.From(Wand(300, "Wand", 0x10, 63)));
            session.Data.Recent.Add(ItemRef.From(Melee(301, "Sword")));
            Check(!session.Maintain(new List<CarriedItem>()), "an empty inventory is not trusted");
            var inv = new List<CarriedItem> { Kit(110), Wand(310, "Wand", 0x10, 63), Melee(311, "Sword") };
            Check(session.Maintain(inv), "slots, favourites and recent all follow");
            Check(session.Data.Slots[0].Item!.Id == 110 && session.Data.Favourites[0].Id == 310 && session.Data.Recent[0].Id == 311, "their new ids");
        }

        private static void MatchByName()
        {
            var items = new List<CarriedItem>
            {
                Kit(1, "Peerless Healing Kit"), Kit(2, "Gifted Healing Kit"), Kit(3, "Lesser Stamina Kit"), Kit(4, "Greater Stamina Kit"),
                Wand(5, "Wand", 0x10, 63), Wand(6, "Wand", 0x10, 60), Potion(7),
            };
            Eq(ItemMatcher.FindByName("Peerless Healing Kit", items)?.Id, 1u, "the full name");
            Eq(ItemMatcher.FindByName("gifted healing kit", items)?.Id, 2u, "any case");
            Eq(ItemMatcher.FindByName("Silver Wand", items)?.Id, 5u, "with the material");
            Eq(ItemMatcher.FindByName("Wand", items)?.Id, 5u, "the plain name: the first of them (the lower id)");
            Eq(ItemMatcher.FindByName("Stamina Kit", items)?.Id, 3u, "words that are in several: the first");
            Eq(ItemMatcher.FindByName("greater stamina", items)?.Id, 4u, "words in any part of the name");
            Eq(ItemMatcher.FindByName("Health", items)?.Id, 7u, "a start of a name");
            Check(ItemMatcher.FindByName("Elixir", items) == null, "nothing by that name");
            Check(ItemMatcher.FindByName("  ", items) == null, "blank finds nothing");
        }

        // ── Picker ──────────────────────────────────────────────────────────

        private static List<CarriedItem> Armory()
        {
            return new List<CarriedItem>
            {
                Melee(1, "Sword"), Melee(2, "Axe", material: 62), Melee(3, "Mace", wielded: true),
                Bow(4, "Yumi"), Bow(5, "Shouyumi"), Arrows(6),
                Wand(7, "Wand", 0x10, material: 63), Wand(8, "Staff", 0x08), Wand(9, "Orb", 0x400), Wand(10, "Wand", 0x400, material: 60),
                Shield(11), Kit(12), Potion(13),
            };
        }

        private static void PickerGroups()
        {
            var items = Armory();
            PickerModel m = WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "", KindFilter.All);
            Eq(m.Total, 11, "weapons, wands, shields and ammunition counted; kits and potions not");
            Eq(m.Shown, 11, "all shown");
            Eq(string.Join(",", m.Groups.Select(g => g.Group)), "Melee,Missile,WarMagic,VoidMagic,Shield,Ammo", "groups in order");
            Eq(string.Join(",", m.Groups[0].Rows.Select(r => r.Item.FullName)), "Mace,Pyreal Axe,Sword", "A-Z by full name");
            Check(m.Groups[0].Rows.First(r => r.Item.Name == "Mace").InHand, "the weapon in hand is marked");
            Eq(string.Join(",", m.Groups[2].Rows.Select(r => r.Element)), "Fire,Cold", "war wands with their elements (Silver Wand = fire, Staff = cold)");
            Eq(string.Join(",", m.Groups[3].Rows.Select(r => r.Item.FullName)), "Gold Wand,Orb", "void wands");
            Check(m.Favourites.Count == 0 && m.Recent.Count == 0, "no shortcuts yet");

            m = WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "wand", KindFilter.All);
            Check(m.Shown == 2 && m.Groups.Count == 2, "search: 'wand' finds the silver and the gold wand, in two groups");
            m = WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "silver wand", KindFilter.All);
            Check(m.Shown == 1 && m.Groups[0].Rows[0].Item.Id == 7, "every word must match: 'silver wand'");
            m = WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "fire", KindFilter.All);
            Check(m.Shown == 1 && m.Groups[0].Rows[0].Item.Id == 7, "search finds the element");
            m = WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "void", KindFilter.All);
            Check(m.Shown == 2, "search finds the kind's label");
            m = WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "zzz", KindFilter.All);
            Check(m.Shown == 0 && m.Groups.Count == 0 && m.Total == 11, "nothing matches");

            Eq(WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "", KindFilter.Melee).Shown, 3, "filter: melee");
            m = WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "", KindFilter.Missile);
            Eq(string.Join(",", m.Groups.Select(g => g.Group)), "Missile,Ammo", "filter: missile keeps the arrows");
            Eq(WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "", KindFilter.War).Shown, 2, "filter: war");
            Eq(WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "", KindFilter.Void).Shown, 2, "filter: void");
            Eq(WeaponPicker.Build(items, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "axe", KindFilter.Void).Shown, 0, "search and filter together");

            // 60 weapons: still one list, sorted.
            var many = new List<CarriedItem>();
            for (uint i = 0; i < 60; i++) many.Add(Melee(1000 + i, "Blade " + (59 - i).ToString("00")));
            m = WeaponPicker.Build(many, Array.Empty<ItemRef>(), Array.Empty<ItemRef>(), "", KindFilter.All);
            Check(m.Groups.Count == 1 && m.Groups[0].Rows.Count == 60 && m.Groups[0].Rows[0].Item.Name == "Blade 00" && m.Groups[0].Rows[59].Item.Name == "Blade 59", "60 weapons sorted");
        }

        private static void PickerShortcuts()
        {
            var items = Armory();
            var favs = new List<ItemRef> { ItemRef.From(items[9]), ItemRef.From(items[0]) };   // Gold Wand (void), Sword
            var recent = new List<ItemRef> { ItemRef.From(items[3]), ItemRef.From(items[0]), new ItemRef { Id = 77, Name = "Sold Sword" } };
            PickerModel m = WeaponPicker.Build(items, favs, recent, "", KindFilter.All);
            Eq(string.Join(",", m.Favourites.Select(r => r.Item.FullName)), "Gold Wand,Sword", "favourites in the order the player made them");
            Eq(string.Join(",", m.Recent.Select(r => r.Item.FullName)), "Yumi,Sword", "recent newest first; one that is gone is left out");
            Check(m.Favourites[0].Favourite && m.Groups.SelectMany(g => g.Rows).First(r => r.Item.Name == "Sword").Favourite, "favourites are marked in their group too");
            Check(m.Groups.SelectMany(g => g.Rows).Count() == 11, "the groups still list everything");

            // A favourite follows its identity when the object changed.
            var swapped = new List<CarriedItem> { Melee(900, "Sword") };
            m = WeaponPicker.Build(swapped, favs, Array.Empty<ItemRef>(), "", KindFilter.All);
            Check(m.Favourites.Count == 1 && m.Favourites[0].Item.Id == 900, "a favourite finds its replacement by name");

            // The search narrows the shortcuts too.
            m = WeaponPicker.Build(items, favs, recent, "yumi", KindFilter.All);
            Check(m.Favourites.Count == 0 && m.Recent.Count == 1, "search narrows favourites and recent");

            // Data: favourites toggle, recent order and limit.
            var d = new DefData();
            ItemRef a = ItemRef.From(items[0]);
            Check(d.ToggleFavourite(a) && d.Favourites.Count == 1, "added");
            Check(!d.ToggleFavourite(ItemRef.From(items[0])) && d.Favourites.Count == 0, "the same weapon again takes it out");
            for (uint i = 0; i < 12; i++) d.NoteRecent(new ItemRef { Id = i + 1, Name = "W" + i });
            Check(d.Recent.Count == DefData.MaxRecent && d.Recent[0].Name == "W11", "recent is capped, newest first");
            d.NoteRecent(new ItemRef { Id = 10, Name = "W9" });
            Check(d.Recent.Count == DefData.MaxRecent && d.Recent[0].Name == "W9" && d.Recent.Count(r => r.Name == "W9") == 1, "wielding it again moves it up");
        }

        private static void PackLists()
        {
            var items = new List<CarriedItem>
            {
                Potion(1, "Health Potion", 5), Potion(2, "Health Potion", 3), Potion(3, "Mana Potion", 10),
                Kit(4), Kit(5), Melee(6, "Sword", wielded: true), Melee(7, "Sword"),
            };
            List<PackChoice> list = PackList.Build(items, "", 40, out int total);
            Eq(total, 4, "kinds: health potion, mana potion, kit, sword");
            Eq(string.Join(",", list.Select(c => c.Item.Name + "x" + c.Count)), "Health Potionx8,Mana Potionx10,Peerless Healing Kitx2,Swordx2", "A-Z with the stacks added");
            Check(list.First(c => c.Item.Name == "Sword").Item.Id == 7, "the representative is the one not in hand");
            Check(list.First(c => c.Item.Name == "Health Potion").Item.Id == 2, "and the smaller stack");
            list = PackList.Build(items, "potion", 40, out total);
            Check(total == 2 && list.Count == 2, "search");
            list = PackList.Build(items, "", 2, out total);
            Check(list.Count == 2 && total == 4, "capped, with the real total");
        }

        // ── Commands ────────────────────────────────────────────────────────

        private static void Commands()
        {
            Eq(DefCommandParser.Parse("").Verb, DefVerb.Toggle, "blank toggles the bar");
            Eq(DefCommandParser.Parse("help").Verb, DefVerb.Help, "help");
            DefCommand c = DefCommandParser.Parse("use 3");
            Check(c.Verb == DefVerb.Use && c.Slot == 3 && c.Text == "", "use 3");
            c = DefCommandParser.Parse("USE Peerless Healing Kit");
            Check(c.Verb == DefVerb.Use && c.Slot == 0 && c.Text == "Peerless Healing Kit", "use by name (case of the verb ignored, the name kept)");
            c = DefCommandParser.Parse("wield  Silver Wand ");
            Check(c.Verb == DefVerb.Use && c.Text == "Silver Wand", "wield is use");
            c = DefCommandParser.Parse("use 99");
            Check(c.Verb == DefVerb.Use && c.Slot == 0 && c.Text == "99", "a number past the slots is a name (nothing will match)");
            Eq(DefCommandParser.Parse("use").Verb, DefVerb.Error, "use needs something");
            c = DefCommandParser.Parse("Stamina Kit");
            Check(c.Verb == DefVerb.Use && c.Text == "Stamina Kit", "/rdef <name> is use");

            c = DefCommandParser.Parse("set 2");
            Check(c.Verb == DefVerb.Set && c.Slot == 2 && c.Text == "", "set 2: from the selected item");
            c = DefCommandParser.Parse("set 2 Health Potion");
            Check(c.Verb == DefVerb.Set && c.Slot == 2 && c.Text == "Health Potion", "set 2 by name");
            Eq(DefCommandParser.Parse("set").Verb, DefVerb.Error, "set needs a slot");
            Eq(DefCommandParser.Parse("set x").Verb, DefVerb.Error, "set needs a number");
            Eq(DefCommandParser.Parse("set 0").Verb, DefVerb.Error, "slots start at 1");
            c = DefCommandParser.Parse("clear 4");
            Check(c.Verb == DefVerb.Clear && c.Slot == 4, "clear");
            Eq(DefCommandParser.Parse("clear").Verb, DefVerb.Error, "clear needs a slot");
            c = DefCommandParser.Parse("bind 3 Ctrl+F9");
            Check(c.Verb == DefVerb.Bind && c.Slot == 3 && c.Text == "Ctrl+F9", "bind");
            Eq(DefCommandParser.Parse("bind 3").Verb, DefVerb.Error, "bind needs a key");
            c = DefCommandParser.Parse("unbind 3");
            Check(c.Verb == DefVerb.Unbind && c.Slot == 3, "unbind");
            c = DefCommandParser.Parse("weapons fire wand");
            Check(c.Verb == DefVerb.Weapons && c.Text == "fire wand", "weapons with a search");
            Eq(DefCommandParser.Parse("weapons").Verb, DefVerb.Weapons, "weapons");
            Eq(DefCommandParser.Parse("fav").Verb, DefVerb.Favourite, "fav");
            c = DefCommandParser.Parse("slots 12");
            Check(c.Verb == DefVerb.Slots && c.Slot == 12, "slots");
            Eq(DefCommandParser.Parse("slots 99").Verb, DefVerb.Error, "slots in range");
            Eq(DefCommandParser.Parse("list").Verb, DefVerb.List, "list");
            Eq(DefCommandParser.Parse("status").Verb, DefVerb.Status, "status");
            Eq(DefCommandParser.UseCommand(3), "/rdef use 3", "the command a hotkey runs for slot 3");
        }

        // ── Store ───────────────────────────────────────────────────────────

        private static void StoreRoundTrip()
        {
            var st = new DefStore(Path.Combine(_temp, "store"));
            var d = new DefData();
            d.Settings.SlotCount = 12;
            d.Settings.SlotsPerRow = 6;
            d.Settings.ButtonSize = 44;
            d.Settings.PickerIconSize = 28;
            d.Settings.ShowIcons = false;
            d.Settings.ShowCounts = false;
            d.Settings.PreferredKind = KindFilter.War;
            d.Settings.SwapWhileBotRuns = true;
            d.Settings.WeaponsButtonOnBar = true;
            d.Settings.BarOpen = true;
            d.Settings.PickerOpen = true;
            var kit = Kit(0x7000_0001u);
            kit.Underlay = 0x06000FFF; kit.Overlay = 0x06000EEE;
            d.Slots[0].Item = ItemRef.From(kit);
            d.Slots[0].Mode = UseMode.OnSelf;
            d.Slots[5].Item = ItemRef.From(Wand(0x7000_0002u, "Wand", 0x10, 63));
            d.Slots[23].Item = ItemRef.From(Potion(0x7000_0003u));
            d.Favourites.Add(ItemRef.From(Melee(5, "Sword")));
            d.Recent.Add(ItemRef.From(Bow(6)));

            Check(st.Save("Aelrynth", "Drak/kon", d) == null, "saved");
            string path = st.PathFor("Aelrynth", "Drak/kon");
            Check(File.Exists(path) && Path.GetFileName(path) == "Aelrynth_Drak_kon.json", "the file name is safe: " + path);
            DefData r = st.Load("Aelrynth", "Drak/kon", out string? problem);
            Check(problem == null, "no problem");
            DefSettings s = r.Settings;
            Check(s.SlotCount == 12 && s.SlotsPerRow == 6 && s.ButtonSize == 44 && s.PickerIconSize == 28, "sizes");
            Check(!s.ShowIcons && !s.ShowCounts && s.SwapWhileBotRuns && s.BarOpen && s.PickerOpen && s.PreferredKind == KindFilter.War, "switches");
            Check(s.WeaponsButtonOnBar, "the Weapons button on the bar, kept");
            Check(r.Slots.Count == DefSettings.SlotsMax, "all 42 slots");
            ItemRef a = r.Slots[0].Item!;
            Check(a.Id == 0x7000_0001u && a.Name == "Peerless Healing Kit" && a.Wcid == 628 && a.Icon == 0x06001234 && a.Underlay == 0x06000FFF && a.Overlay == 0x06000EEE, "a slot's item with its identity and picture");
            Check(r.Slots[0].Mode == UseMode.OnSelf && r.Slots[1].Item == null && r.Slots[1].Mode == UseMode.Auto, "modes and empty slots");
            Check(r.Slots[5].Item!.Material == 63 && r.Slots[5].Item!.FullName == "Silver Wand", "material kept");
            Check(r.Slots[23].Item!.Name == "Health Potion", "the last slot");
            Check(r.Favourites.Count == 1 && r.Favourites[0].Name == "Sword" && r.Recent.Count == 1 && r.Recent[0].Name == "Yumi", "favourites and recent");
            Check(!Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "no temp file left");
            string again = DefStore.ToJson(r);
            Eq(again, DefStore.ToJson(d), "writing what was read gives the same file");

            DefData fresh = st.Load("Aelrynth", "Nobody", out problem);
            Check(problem == null && fresh.Slots.All(x => x.Empty) && fresh.Settings.SlotCount == 8, "a new character gets defaults");
        }

        private static void StoreOddFiles()
        {
            var st = new DefStore(Path.Combine(_temp, "odd"));
            string own = st.PathFor("S", "A");
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);

            File.WriteAllText(own, "not json at all");
            DefData d = st.Load("S", "A", out string? problem);
            Check(problem != null && d.Slots.All(x => x.Empty), "a broken file reads as defaults and says so");

            File.WriteAllText(own, "[1,2,3]");
            st.Load("S", "A", out problem);
            Check(problem != null, "not an object");

            File.WriteAllText(own, "{}");
            d = st.Load("S", "A", out problem);
            Check(problem == null && d.Settings.SlotCount == 8 && d.Slots.Count == DefSettings.SlotsMax, "{} = defaults");
            Check(!d.Settings.WeaponsButtonOnBar, "a file from before has no Weapons button on the bar");

            File.WriteAllText(own, "{\"settings\":{\"slotCount\":99,\"slotsPerRow\":0,\"buttonSize\":1000,\"pickerIconSize\":-5,\"preferredKind\":42,\"showIcons\":\"yes\"},"
                                   + "\"slots\":[7,{\"item\":{\"id\":5.0,\"name\":\"Kit\"},\"mode\":9},{\"item\":{\"id\":1}},{\"item\":{\"name\":\"  \"}},{\"item\":\"x\"},null],"
                                   + "\"favourites\":[\"x\",{\"name\":\"Sword\"},{\"id\":3}],\"recent\":5}");
            d = st.Load("S", "A", out problem);
            Check(problem == null, "odd values are not a problem");
            Check(d.Settings.SlotCount == DefSettings.SlotCountMax && d.Settings.SlotsPerRow == DefSettings.PerRowMin && d.Settings.ButtonSize == DefSettings.ButtonMax
                  && d.Settings.PickerIconSize == DefSettings.PickerIconMin && d.Settings.PreferredKind == KindFilter.All && d.Settings.ShowIcons, "clamped, a string is not a bool");
            Check(d.Slots[0].Empty, "a non-object slot is empty");
            Check(d.Slots[1].Item?.Name == "Kit" && d.Slots[1].Item!.Id == 5 && d.Slots[1].Mode == UseMode.Auto, "2.0 is 2; an unknown mode is automatic");
            Check(d.Slots[2].Empty && d.Slots[3].Empty && d.Slots[4].Empty, "an item with no name can't be found again: empty");
            Check(d.Favourites.Count == 1 && d.Favourites[0].Name == "Sword" && d.Recent.Count == 0, "bad favourites are skipped; recent that isn't a list is none");
            Eq(d.Slots.Count, DefSettings.SlotsMax, "still 42 slots");

            // More slots than the maximum in a file are cut.
            var sb = new StringBuilder("{\"slots\":[");
            for (int i = 0; i < DefSettings.SlotsMax + 10; i++) sb.Append(i > 0 ? "," : "").Append("{\"item\":{\"id\":").Append(i + 1).Append(",\"name\":\"N").Append(i).Append("\"}}");
            sb.Append("]}");
            File.WriteAllText(own, sb.ToString());
            d = st.Load("S", "A", out _);
            Check(d.Slots.Count == DefSettings.SlotsMax && d.Slots[DefSettings.SlotsMax - 1].Item!.Name == "N" + (DefSettings.SlotsMax - 1), "slots past the 42nd are dropped");

            // A file from before 2026-10-27 (24 slots): its slots load, the rest are empty.
            sb.Clear().Append("{\"slots\":[");
            for (int i = 0; i < 24; i++) sb.Append(i > 0 ? "," : "").Append("{\"item\":{\"id\":").Append(i + 1).Append(",\"name\":\"Old").Append(i).Append("\"}}");
            sb.Append("]}");
            File.WriteAllText(own, sb.ToString());
            d = st.Load("S", "A", out _);
            Check(d.Slots.Count == DefSettings.SlotsMax && d.Slots[23].Item!.Name == "Old23" && d.Slots[24].Empty && d.Slots[DefSettings.SlotsMax - 1].Empty,
                "a 24-slot file loads into the first 24 of 42");

            Eq(DefStore.SafeFileName("CON"), "_CON", "reserved names");
            Eq(DefStore.FileStem("Dream Weave", "Ana:Lee"), "Dream Weave_Ana_Lee", "illegal characters become underscores");
        }

        // ── Hotkey file ─────────────────────────────────────────────────────

        private static void HotkeyFile()
        {
            string file = Path.Combine(_temp, "hotkeys.txt");
            File.WriteAllText(file,
                "# RynthCore hotkeys\nenabled=1\ndefaults=1\n"
                + "on\tF6\theal\t\n"
                + "on\tF9\tcommand\t/rdef use 3\n"
                + "off\tF10\tcommand\t/rdef  use   4\n"
                + "on\tCtrl+1\tcommand\t/RDEF USE Stamina Kit\n"
                + "on\tF11\tcommand\t/ra start\n"
                + "junk line\n");
            var lookup = new HotkeyLookup(() => file);
            long t = 0;
            Eq(lookup.KeyFor("/rdef use 3", t), "F9", "a slot's key");
            Eq(lookup.KeyFor("/rdef use 4", t), "F10 (off)", "a switched-off binding says so");
            Eq(lookup.KeyFor("/rdef use stamina kit", t), "Ctrl+1", "a name binding, any case");
            Check(lookup.KeyFor("/rdef use 5", t) == null, "no key");
            Check(lookup.KeyFor("/ra start", t) == "F11", "any chat command");
            Check(lookup.KeyFor("heal", t) == null, "built-in actions are not chat commands");

            // A change in the file is seen after the check interval.
            File.WriteAllText(file, "on\tF8\tcommand\t/rdef use 5\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            Eq(lookup.KeyFor("/rdef use 5", t + 100), null, "not looked at again within a second");
            Eq(lookup.KeyFor("/rdef use 5", t + 1500), "F8", "the new binding");
            Check(lookup.KeyFor("/rdef use 3", t + 1500) == null, "the old one is gone");

            // Missing file, then unreadable lines.
            var none = new HotkeyLookup(() => Path.Combine(_temp, "missing.txt"));
            Check(none.KeyFor("/rdef use 1", 0) == null, "no file: no keys");
            Eq(HotkeyLookup.Parse("on\tF1\tcommand\n").Count, 0, "a short line is skipped");
        }

        // ── Session ─────────────────────────────────────────────────────────

        private sealed class Rig
        {
            public readonly DefSession S;
            public readonly List<string> Chat = new();
            public int Saves;
            public uint Selected;
            public List<CarriedItem> Inv = new();
            public string HotkeyPath;

            public Rig(string name)
            {
                HotkeyPath = Path.Combine(_temp, "rig-" + name + ".txt");
                S = new DefSession(new HotkeyLookup(() => HotkeyPath))
                {
                    LoggedIn = true,
                    Character = "Test",
                };
                S.Items = () => Inv;
                S.SelectedId = () => Selected;
                S.Save = () => Saves++;
                S.RunChat = line => { Chat.Add(line); return true; };
            }
        }

        private static void SessionSlots()
        {
            var rig = new Rig("slots");
            rig.Inv = new List<CarriedItem> { Kit(10), Potion(11), Melee(12, "Sword") };
            string m = rig.S.SetFromSelected(1);
            Check(m.Contains("Select the item") && rig.S.Data.Slots[0].Empty, "nothing selected: told how");
            rig.Selected = 999;
            m = rig.S.SetFromSelected(1);
            Check(m.Contains("isn't in your pack") && rig.S.Data.Slots[0].Empty, "selected but not carried (a monster, a corpse)");
            rig.Selected = 10;
            m = rig.S.SetFromSelected(1);
            Check(rig.S.Data.Slots[0].Item?.Id == 10 && rig.Saves == 1, "set from the selected kit, saved");
            Check(m.Contains("Peerless Healing Kit") && m.Contains("used on you"), "and it says what pressing does: " + m);
            rig.Selected = 12;
            Check(rig.S.SetFromSelected(2).Contains("wielded"), "a sword is wielded");

            m = rig.S.SetFromName(3, "health");
            Check(rig.S.Data.Slots[2].Item?.Name == "Health Potion" && m.Contains("Slot 3"), "set by name");
            Check(rig.S.SetFromName(3, "elixir").Contains("No item named"), "a name that matches nothing");
            Check(rig.S.SetFromSelected(0).Contains("no slot") && rig.S.SetFromSelected(DefSettings.SlotsMax + 1).Contains("no slot"), "slot numbers are checked");

            rig.S.SetSlotMode(1, UseMode.Use);
            Eq(rig.S.Data.Slots[0].Mode, UseMode.Use, "mode");
            rig.S.SetSlot(1, rig.Inv[0]);
            Eq(rig.S.Data.Slots[0].Mode, UseMode.Auto, "a new item resets the mode");

            // Move.
            Check(rig.S.SwapSlots(1, 2), "swap");
            Check(rig.S.Data.Slots[0].Item!.Name == "Sword" && rig.S.Data.Slots[1].Item!.Name == "Peerless Healing Kit", "swapped");
            Check(!rig.S.SwapSlots(1, 0) && !rig.S.SwapSlots(8, 9) && !rig.S.SwapSlots(2, 2), "no swap past the ends (8 slots) or with itself");
            Eq(rig.S.ClearSlot(2), "Slot 2 cleared.", "clear");
            Check(rig.S.Data.Slots[1].Empty && rig.S.Data.Slots[1].Mode == UseMode.Auto, "cleared slot is empty");

            rig.S.SetSlotCount(100);
            Eq(rig.S.Settings.SlotCount, 24, "slot count clamped");
            rig.S.SetSlotCount(0);
            Eq(rig.S.Settings.SlotCount, 1, "slot count at least 1");

            // Pressing a slot.
            rig.S.SetSlotCount(8);
            Check(rig.S.UseSlot(2)!.Contains("empty"), "an empty slot says so");
            Check(rig.S.UseSlot(DefSettings.SlotsMax + 1)!.Contains("no slot"), "a slot that doesn't exist");
            Check(rig.S.UseSlot(1) == null && rig.S.Use.HasPending, "a set slot queues its item");
        }

        private static void SessionKeys()
        {
            var rig = new Rig("keys");
            rig.Inv = new List<CarriedItem> { Kit(10, "Greater Stamina Kit") };
            rig.S.SetSlot(3, rig.Inv[0]);
            string m = rig.S.BindKey(3, "F9", byName: false);
            Eq(rig.Chat.Count, 1, "one line to the engine (no key to take off yet)");
            Eq(rig.Chat[0], "/rc hotkey add F9 /rdef use 3", "the slot's command");
            Check(m.Contains("F9"), "message");

            // The engine's file now has it.
            File.WriteAllText(rig.HotkeyPath, "on\tF9\tcommand\t/rdef use 3\n");
            rig.S.Keys.Invalidate();
            Eq(rig.S.KeyOfSlot(3), "F9", "the slot shows its key");
            rig.Chat.Clear();
            rig.S.BindKey(3, "Ctrl+F9", byName: false);
            Eq(string.Join("|", rig.Chat), "/rc hotkey remove F9|/rc hotkey add Ctrl+F9 /rdef use 3", "re-binding takes the old key off first");

            File.WriteAllText(rig.HotkeyPath, "");
            rig.S.Keys.Invalidate();
            rig.Chat.Clear();
            rig.S.BindKey(3, "F10", byName: true);
            Eq(rig.Chat[^1], "/rc hotkey add F10 /rdef use Greater Stamina Kit", "by name: the item's full name");

            File.WriteAllText(rig.HotkeyPath, "on\tF10\tcommand\t/rdef use Greater Stamina Kit\n");
            rig.S.Keys.Invalidate();
            Eq(rig.S.KeyOfSlot(3), "F10", "the key of a name binding shows on the slot holding that item");
            rig.Chat.Clear();
            m = rig.S.UnbindKey(3);
            Eq(rig.Chat.Single(), "/rc hotkey remove F10", "unbind");
            Check(m.Contains("F10"), "says which key");
            File.WriteAllText(rig.HotkeyPath, "");
            rig.S.Keys.Invalidate();
            Check(rig.S.UnbindKey(3).Contains("no key"), "unbind with none");

            Check(rig.S.BindKey(3, "", false).Contains("Type the key"), "a blank key");
            Check(rig.S.BindKey(3, "Ctrl + F9", false).Contains("no spaces"), "spaces in a key");
            Check(rig.S.BindKey(4, "F9", true).Contains("empty"), "by name needs an item");
            Check(rig.S.BindKey(DefSettings.SlotsMax + 1, "F9", false).Contains("no slot"), "slot checked");

            rig.S.RunChat = _ => false;
            Check(rig.S.BindKey(3, "F11", false).Contains("/rc hotkey add F11 /rdef use 3"), "an engine that can't: the exact line to type");
        }

        private static void SessionMisc()
        {
            var rig = new Rig("misc");
            long now = 5000;
            rig.S.Now = () => now;
            rig.S.SetStatus("hello");
            Eq(rig.S.Status(out bool warn), "hello", "status shows");
            Check(!warn, "not a warning");
            now += DefSession.StatusShownMs + 1;
            Eq(rig.S.Status(out _), "", "and fades");
            rig.S.SetStatus("bad", true);
            Check(rig.S.Status(out warn) == "bad" && warn, "warning flag");

            rig.Inv = new List<CarriedItem> { Melee(5, "Sword") };
            Check(rig.S.ToggleSelectedFavourite().Contains("Select a weapon"), "no selection");
            rig.Selected = 5;
            Check(rig.S.ToggleSelectedFavourite().Contains("is a favourite") && rig.S.Data.Favourites.Count == 1, "favourite the selected weapon");
            Check(rig.S.ToggleSelectedFavourite().Contains("no longer") && rig.S.Data.Favourites.Count == 0, "and again takes it out");

            rig.S.UseByName("Sword");
            Check(rig.S.Use.HasPending && rig.S.Use.Pending!.Name == "Sword", "use by name queues a name request");
            rig.S.Wield(ItemRef.From(rig.Inv[0]));
            Check(rig.S.Use.Pending!.Mode == UseMode.Equip, "the picker's request wields");
        }
    }
}
