using System;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Typed world-cache placement (2026-10-05; spotted by Silentkelpie, rynth PR #1). A positioned,
// non-creature object with a known item type used to be filed only when it was a weapon, armor,
// caster or container, and then always into OUR inventory: other players' wielded gear and
// ground-dropped weapons counted as ours, and keys, gems, comps and food on the ground stayed
// Unknown and were re-probed every 2 s for good. Now they get the class their type gives and are
// filed by who has them. Pinned here: what goes where, that hazards (dynamic or static, typed or
// not) still register, that corpses are still found, and the loot classes (scrolls, slot-typed
// weapons) on objects that come this way.
internal static class WorldCacheTypedTests
{
    private const uint Player = 0x50000E01;
    private const uint OtherPlayer = 0x50000E99;
    private const uint Lb = 0xA9B40000;
    private const uint Here = Lb | 0x1C;
    private const uint Dungeon = 0x01D90105;

    private const uint TMelee = 0x1, TArmor = 0x2, TFood = 0x20, TMisc = 0x80, TContainer = 0x200, TGem = 0x800,
        TComps = 0x1000, TWritable = 0x2000, TKey = 0x4000, TManaStone = 0x80000, TLifeStone = 0x10000000;

    public static void Register(Runner r)
    {
        r.Add("world cache: typed ground items get their class and stay on the landscape", GroundItems);
        r.Add("world cache: another player's wielded gear is neither ours nor a ground item", OtherPlayersGear);
        r.Add("world cache: our wielded gear and positioned pack / side-pack items are inventory", OwnGear);
        r.Add("world cache: ground gear is landscape; unreadable ownership keeps the old filing", GroundGearAndUnknownOwnership);
        r.Add("world cache: hazards still register (dynamic Misc, static, untyped); lava-named gear does not", Hazards);
        r.Add("world cache: corpses: named at once, and a Container-typed one named late is rescued", Corpses);
        r.Add("world cache: an Unknown whose type arrives late is filed as the item it is", LateType);
        r.Add("world cache: scrolls, slot-typed weapons, chests, lifestones; signs stay scenery", LootClasses);
    }

    // ── Rig ───────────────────────────────────────────────────────────────

    private static uint _next;

    private static void World()
    {
        FakeHost.Reset();
        _next = 0x80310001;
        FakeHost.Names[Player] = "Cache Tester";
        FakeHost.Positions[Player] = (Here, 96f, 96f, 0f);
        FakeHost.PlayerPose = (Here, 96f, 96f, 0f);
        FakeHost.Containers[Player] = 0;   // installs GetObjectOwnershipInfo for every test
    }

    /// <summary>A positioned non-creature: on the ground (owner null = ownership unreadable).</summary>
    private static uint Obj(string name, uint type, uint? container = 0, uint? wielder = null, uint cell = Here, uint id = 0)
    {
        if (id == 0) id = _next++;
        if (name.Length > 0) FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (cell, 97f, 97f, 0f);
        if (type != 0) FakeHost.ItemTypes[id] = type;
        if (container != null) FakeHost.Containers[id] = container.Value;
        if (wielder != null) FakeHost.Wielders[id] = wielder.Value;
        FakeHost.NotAttackable.Add(id);
        return id;
    }

    /// <summary>Classifies <paramref name="ids"/> the way the game does: CreateObject, then ticks.</summary>
    private static WorldObjectCache Classify(params uint[] ids)
    {
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        foreach (uint id in ids) cache.OnCreateObject(id);
        for (int i = 0; i < 12; i++) cache.Tick();   // past the 8 same-tick retries
        return cache;
    }

    private static void Reclassify(WorldObjectCache cache)
        => typeof(WorldObjectCache).GetMethod("ReclassifyUnknownDynamics", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cache, null);

    private static AcObjectClass ClassOf(WorldObjectCache c, uint id)
        => c.AllKnownObjects().FirstOrDefault(w => w.Id == unchecked((int)id))?.ObjectClass ?? (AcObjectClass)(-1);
    private static bool InInventory(WorldObjectCache c, uint id) => c.GetInventory().Any(w => w.Id == unchecked((int)id));
    private static bool OnLandscape(WorldObjectCache c, uint id) => c.GetLandscapeObjects().Any(w => w.Id == unchecked((int)id));

    private static void Where(WorldObjectCache c, uint id, AcObjectClass cls, bool inv, bool land, string what)
    {
        Check.Eq(ClassOf(c, id), cls, what + ": class");
        Check.Eq(InInventory(c, id), inv, what + ": in the inventory");
        Check.Eq(OnLandscape(c, id), land, what + ": on the landscape");
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    private static void GroundItems()
    {
        World();
        uint key = Obj("Key to the Black Gate", TKey);
        uint gem = Obj("Black Opal", TGem);
        uint comp = Obj("Prismatic Taper", TComps);
        uint food = Obj("Apple", TFood);
        uint stone = Obj("Mana Stone", TManaStone);
        var c = Classify(key, gem, comp, food, stone);
        Where(c, key, AcObjectClass.Key, false, true, "ground key");
        Where(c, gem, AcObjectClass.Gem, false, true, "ground gem");
        Where(c, comp, AcObjectClass.SpellComponent, false, true, "ground comps");
        Where(c, food, AcObjectClass.Food, false, true, "ground food");
        Where(c, stone, AcObjectClass.ManaStone, false, true, "ground mana stone");
        Reclassify(c);
        Check.False(FakeHost.Logs.Any(l => l.Contains("stuck Unknown", StringComparison.Ordinal)), "nothing left for the Unknown re-probe");
    }

    private static void OtherPlayersGear()
    {
        World();
        uint sword = Obj("Fire Tachi", TMelee, container: 0, wielder: OtherPlayer);
        uint armor = Obj("Amuli Shadow Coat", TArmor, container: 0, wielder: OtherPlayer);
        var c = Classify(sword, armor);
        Where(c, sword, AcObjectClass.MeleeWeapon, false, false, "another player's sword");
        Where(c, armor, AcObjectClass.Armor, false, false, "another player's coat");
        Check.False(c.GetContainedItems(unchecked((int)Player)).Any(w => w.Id == unchecked((int)sword)), "not one of our items");
    }

    private static void OwnGear()
    {
        World();
        uint sword = Obj("Fire Tachi", TMelee, container: 0, wielder: Player);
        uint shield = Obj("Kite Shield", TArmor, container: Player);
        uint pack = 0x80310F00;
        FakeHost.Names[pack] = "Pack";
        FakeHost.ItemTypes[pack] = TContainer;
        FakeHost.Containers[pack] = Player;
        uint gem = Obj("Black Opal", TGem, container: pack);
        var c = Classify(sword, shield, gem);
        Where(c, sword, AcObjectClass.MeleeWeapon, true, false, "our wielded sword");
        Where(c, shield, AcObjectClass.Armor, true, false, "our shield");
        Where(c, gem, AcObjectClass.Gem, true, false, "a gem in our side pack");
    }

    private static void GroundGearAndUnknownOwnership()
    {
        World();
        uint dropped = Obj("Fire Tachi", TMelee, container: 0);
        uint unknownSword = Obj("Frost Tachi", TMelee, container: null);
        uint unknownGem = Obj("Black Opal", TGem, container: null);
        var c = Classify(dropped, unknownSword, unknownGem);
        Where(c, dropped, AcObjectClass.MeleeWeapon, false, true, "a sword on the ground");
        Where(c, unknownSword, AcObjectClass.MeleeWeapon, true, false, "a sword whose owner can't be read (as before)");
        Where(c, unknownGem, AcObjectClass.Gem, false, true, "a gem whose owner can't be read");
    }

    private static void Hazards()
    {
        World();
        uint lava = Obj("Lava", TMisc, container: 0, cell: Dungeon);
        uint lavaUnknownOwner = Obj("Pool of Acid", TMisc, container: null, cell: Dungeon + 1);
        uint staticAcid = Obj("Pool of Acid", TMisc, container: null, cell: Dungeon + 2, id: 0x71D90002);
        uint untyped = Obj("Magma", 0, container: null, cell: Dungeon + 3);
        uint gear = Obj("Lava Gauntlets", TArmor, container: 0, cell: Dungeon + 4);
        var c = Classify(lava, lavaUnknownOwner, staticAcid, untyped, gear);
        Check.True(c.IsHazardCell(Dungeon), "a dynamic Misc lava hotspot registers its cell");
        Where(c, lava, AcObjectClass.Misc, false, true, "the lava hotspot");
        Check.True(c.IsHazardCell(Dungeon + 1), "a hotspot whose owner can't be read registers");
        Check.True(c.IsHazardCell(Dungeon + 2), "a static hotspot registers");
        Where(c, staticAcid, AcObjectClass.Unknown, false, true, "the static hotspot stays scenery");
        Check.True(c.IsHazardCell(Dungeon + 3), "an untyped hotspot registers");
        Check.False(c.IsHazardCell(Dungeon + 4), "gauntlets named Lava on the floor are not a hazard (as before)");
        Check.True(System.IO.Directory.Exists(System.IO.Path.Combine(Program.TempRoot, "DungeonHazards")), "hazards persist to the scratch folder, not C:\\Games");
    }

    private static void Corpses()
    {
        World();
        uint named = Obj("Corpse of Drudge Skulker", TContainer, container: 0);
        uint late = Obj("", TContainer, container: 0);
        var c = Classify(named, late);
        Where(c, named, AcObjectClass.Corpse, false, true, "a named corpse");
        Where(c, late, AcObjectClass.Container, false, true, "a nameless container (before its name)");
        FakeHost.Names[late] = "Corpse of Drudge Ravener";
        Reclassify(c);
        Where(c, late, AcObjectClass.Corpse, false, true, "named late: rescued as a corpse");

        // The corpse flag alone, name never matching.
        World();
        uint flagged = Obj("Drudge Remains", TContainer, container: 0);
        FakeHost.Bitfields[flagged] = 0x2000;
        var c2 = Classify(flagged);
        Where(c2, flagged, AcObjectClass.Corpse, false, true, "a corpse by its weenie flag");
    }

    private static void LateType()
    {
        World();
        uint gem = Obj("Black Opal", 0, container: 0);
        var c = Classify(gem);
        Where(c, gem, AcObjectClass.Unknown, false, true, "no type yet: Unknown landscape");
        FakeHost.ItemTypes[gem] = TGem;
        FakeHost.Logs.Clear();
        Reclassify(c);
        Where(c, gem, AcObjectClass.Gem, false, true, "type arrived: a ground gem");
        Check.True(FakeHost.Logs.Any(l => l.Contains("typed 1 Unknown object", StringComparison.Ordinal)), "logged once");

        // Our own item whose type arrives late goes to the inventory.
        World();
        uint mine = Obj("Fire Tachi", 0, container: 0, wielder: Player);
        var c2 = Classify(mine);
        FakeHost.ItemTypes[mine] = TMelee;
        Reclassify(c2);
        Where(c2, mine, AcObjectClass.MeleeWeapon, true, false, "our sword typed late");

        // A creature still wins: a late Creature type promotes, it is not filed as an item.
        World();
        uint mob = Obj("Drudge Skulker", 0, container: 0);
        var c3 = Classify(mob);
        FakeHost.ItemTypes[mob] = 0x10;
        Reclassify(c3);
        Check.Eq(ClassOf(c3, mob), AcObjectClass.Monster, "a late creature type: Monster");
    }

    private static void LootClasses()
    {
        World();
        uint scroll = Obj("Scroll of Gift of Essence", TWritable, container: 0);
        FakeHost.DataIds[(scroll, 28)] = 2001;
        uint book = Obj("Tattered Journal", TWritable, container: 0);   // no "Scroll" name, no spell
        uint slayer = Obj("Driftwarden Greataxe", TMisc, container: 0);
        FakeHost.Ints[(slayer, 9)] = 0x00100000;   // ValidLocations: melee weapon slot
        uint chest = Obj("Chest", TContainer, container: null, id: 0x71D90010);
        uint stone = Obj("Life Stone", TLifeStone, container: null, id: 0x71D90011);
        uint sign = Obj("Sign", TMisc, container: null, id: 0x71D90012);
        var c = Classify(scroll, book, slayer, chest, stone, sign);
        Where(c, scroll, AcObjectClass.Scroll, false, true, "a spell scroll on the ground");
        Where(c, book, AcObjectClass.Book, false, true, "a journal on the ground");
        Where(c, slayer, AcObjectClass.MeleeWeapon, false, true, "a Misc-typed weapon with a weapon slot");
        Where(c, chest, AcObjectClass.Container, false, true, "a static chest");
        Where(c, stone, AcObjectClass.Lifestone, false, true, "a lifestone");
        Where(c, sign, AcObjectClass.Unknown, false, true, "a static sign stays scenery");
    }
}
