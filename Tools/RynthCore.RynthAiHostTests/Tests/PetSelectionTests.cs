using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The player's pet choice (2026-11-08, R-20261108-194528-ab84: "combat pets are not autosummoning
// anymore", a drudge the player never chose standing next to them):
//   * any "<me>'s ..." creature counted as the pet out, so a pet that is none of the player's combat
//     pets (a passive pet never despawns) held every summon for good, with no log line;
//   * a Damage-panel pick of an essence that was replaced by a same-named one fell back to Auto,
//     which could summon an essence the player had not picked for that monster.
internal static class PetSelectionTests
{
    private const uint Player = 0x50000D21;
    private const uint Mob = 0x80320010;
    private const uint MobWcid = 41001;
    private const uint Essence = 0x80320001;    // Fire Moar Essence (listed)
    private const uint Essence2 = 0x80320002;   // its same-named successor
    private const uint Frost = 0x80320003;      // Frost Moar Essence (listed first)
    private const uint Other = 0x80320020;      // a pet out that is none of the player's
    private const uint Ours = 0x80320021;       // the pet our summon brings
    private const uint StructureKey = 92;       // AcIntProperty.Structure
    private const uint Lb = 0xA9B40000;

    public static void Register(Runner r)
    {
        r.Add("pet choice: a pet out that is none of your combat pets does not hold summons; yours is summoned, logged once (before: never)", OtherPetDoesNotHold);
        r.Add("pet choice: the other pet turns out a combat pet ('is already active'): it holds the slot, no Use every 45 s", OtherPetAlreadyActive);
        r.Add("pet choice: a pet from our own summon holds the slot whatever it is named (a summon gem's pet)", OwnSummonHoldsWhateverItsName);
        r.Add("pet choice: a Damage-panel pick follows its replaced essence by name, not Auto's other essence (before: Auto)", PickFollowsReplacedEssence);
        // R-20261108-215240-651b (2026.11.8.1): a listed "Common Golem Essence" (a passive pet: no
        // charges, no Summoning requirement) was summoned ahead of the two combat pets ticked, and
        // its pet, from our own summon, held the slot for good.
        r.Add("pet choice: a listed essence that is no combat pet device (appraised) is never summoned; the combat pet is (before: the golem)", PassiveEssenceSkipped);
        r.Add("pet choice: an essence used before its appraisal that turns out passive is learned, and its pet holds nothing (before: held for good)", PassiveLearnedAfterSummon);
        r.Add("pet choice: the Pets tab shows a listed passive essence under Cosmetic, unticked, and the combat pick skips it", PetsTabShowsPassiveAsCosmetic);
        r.Add("pet choice: pack fill does not list an appraised passive essence as a Pet; a combat essence and an unappraised one still are", FillSkipsPassiveEssence);
    }

    private const uint Golem = 0x80320030;      // Common Golem Essence: a passive pet's essence
    private const uint Living = 0x80320031;     // Living Weapon Essence: a combat essence
    private const uint GolemPet = 0x80320032;
    private const uint UseRequiresSkillKey = 366;

    /// <summary>A rig whose Items list is the golem first, then the combat essence (the pack fill's name order).</summary>
    private static Rig PassiveRig(bool golemAppraised, bool listLiving = true)
    {
        var rig = new Rig(essenceName: "Living Weapon Essence");
        rig.S.ConsumableRules.Clear();
        FakeHost.Names.Remove(Essence);
        FakeHost.Containers.Remove(Essence);
        // The golem: appraised (or not yet), with no charge count and no Summoning requirement.
        FakeHost.Names[Golem] = "Common Golem Essence";
        FakeHost.Containers[Golem] = Player;
        if (golemAppraised) FakeHost.Appraised.Add(Golem);
        FakeHost.Names[Living] = "Living Weapon Essence";
        FakeHost.Containers[Living] = Player;
        FakeHost.Ints[(Living, StructureKey)] = 40;
        FakeHost.Ints[(Living, UseRequiresSkillKey)] = 54;
        FakeHost.Appraised.Add(Living);
        _ = rig.Cache[unchecked((int)Golem)];    // filed in the pack
        _ = rig.Cache[unchecked((int)Living)];
        rig.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Golem), Name = "Common Golem Essence", Type = "Pet" });
        if (listLiving) ListLiving(rig);
        return rig;
    }

    private static void ListLiving(Rig rig)
        => rig.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Living), Name = "Living Weapon Essence", Type = "Pet" });

    private static void PassiveEssenceSkipped()
    {
        using var rig = PassiveRig(golemAppraised: true);
        rig.Run(1_500);
        Check.Eq(Rig.UsesOf(Golem), 0, "the passive pet's essence is not summoned (before: it was, first in the list)");
        Check.Eq(Rig.UsesOf(Living), 1, "the ticked combat pet is");
        Check.True(Logged("is no combat pet device") && Logged("0x80320030"), "the skipped essence is logged with its id");
        rig.Run(60_000);
        Check.Eq(Rig.UsesOf(Golem), 0, "still never");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("is no combat pet device")), 1, "logged once");
    }

    private static void PassiveLearnedAfterSummon()
    {
        using var rig = PassiveRig(golemAppraised: false, listLiving: false);
        rig.Run(1_500);
        Check.True(FakeHost.IdRequests.Contains(Golem), "the unappraised essence is asked for its appraisal first");
        Check.Eq(Rig.UsesOf(Golem), 0, "and waits for it a moment");
        rig.Run(3_000);
        Check.Eq(Rig.UsesOf(Golem), 1, "no appraisal came: it is used, as before");
        rig.ShowPet(GolemPet, "Pet Tester's Common Pet Golem");
        rig.Run(1_000);
        FakeHost.Appraised.Add(Golem);   // the appraisal lands: no charges, no Summoning requirement
        rig.Run(1_000);
        Check.True(Logged("brought out 1 pet(s) but is no combat pet device"), "learned and logged");
        ListLiving(rig);                 // the player's combat pet
        rig.Run(1_500);
        Check.Eq(Rig.UsesOf(Living), 1, "the golem holds nothing: the combat pet is summoned beside it (before: never while it stood)");
        rig.Run(60_000);
        Check.Eq(Rig.UsesOf(Golem), 1, "the golem's essence is not used again this session");
    }

    private static void PetsTabShowsPassiveAsCosmetic()
    {
        using var rig = PassiveRig(golemAppraised: true);
        var host = FakeHost.Create(Player);
        string dir = System.IO.Path.Combine(Program.TempRoot, "pet-selection-petstab");
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        System.IO.Directory.CreateDirectory(dir);
        var store = new RynthCore.Plugin.RynthAi.MiniRemote.MiniRemoteStore(dir);
        var cache = rig.Cache;
        var ctx = new RynthCore.Plugin.RynthAi.MiniRemote.MiniRemoteContext(host, store.Load(), store,
            new RynthCore.Plugin.RynthAi.MiniRemote.MiniRemoteInventory(host, () => cache),
            new RynthCore.Plugin.RynthAi.MiniRemote.ServerHubClient(host), () => rig.S);
        var tab = new RynthCore.Plugin.RynthAi.MiniRemote.PetsTab(ctx);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(RynthCore.Plugin.RynthAi.MiniRemote.PetsTab).GetMethod("RebuildPetSnapshot", Any)!.Invoke(tab, new object[] { false });
        var pets = (RynthCore.Plugin.RynthAi.MiniRemote.PetStats[])typeof(RynthCore.Plugin.RynthAi.MiniRemote.PetsTab).GetField("_pets", Any)!.GetValue(tab)!;
        var golem = pets.FirstOrDefault(p => p.Id == unchecked((int)Golem));
        Check.True(golem != null, "the golem's essence is in the roster (before: not shown at all, no charges)");
        Check.Eq(golem?.Kind, RynthCore.Plugin.RynthAi.MiniRemote.PetKind.Cosmetic, "under Cosmetic");
        Check.False(golem?.Selected ?? true, "not ticked (the cosmetic pick is empty)");
        var living = pets.FirstOrDefault(p => p.Id == unchecked((int)Living));
        Check.Eq(living?.Kind, RynthCore.Plugin.RynthAi.MiniRemote.PetKind.Combat, "the combat essence stays Combat");
        typeof(RynthCore.Plugin.RynthAi.MiniRemote.PetsTab).GetMethod("UpdateHudPetLine", Any)!.Invoke(tab, new object?[] { null });
        Check.Eq(tab.HudPetName, "Living Weapon Essence", "the Mini Remote's next combat pet skips the golem");
    }

    private static void FillSkipsPassiveEssence()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;                         // HasAppraisalData
        var host = FakeHost.Create(Player);
        FakeHost.Appraised.Add(Golem);                       // appraised: no charges, no Summoning requirement
        FakeHost.Ints[(Living, StructureKey)] = 40;          // a combat essence: a charge count
        const uint Unread = 0x80320033;                      // not appraised yet: listed as before
        var s = new LegacyUiSettings();
        var ui = new LegacyWeaponsUi(s, host);
        var inv = new List<WorldObject>
        {
            new(unchecked((int)Golem), "Common Golem Essence", AcObjectClass.Misc),
            new(unchecked((int)Living), "Living Weapon Essence", AcObjectClass.Misc),
            new(unchecked((int)Unread), "Dark Shaman Essence", AcObjectClass.Misc),
        };
        ui.FillFromPack(inv, Player);
        var pets = s.ConsumableRules.Where(r => r.Type == "Pet").Select(r => r.Name).ToList();
        Check.False(pets.Contains("Common Golem Essence"), "the passive essence is not listed as a combat pet (before: it was)");
        Check.True(pets.Contains("Living Weapon Essence"), "the combat essence is");
        Check.True(pets.Contains("Dark Shaman Essence"), "an essence not appraised yet still is (no verdict without its properties)");
    }

    private sealed class Rig : IDisposable
    {
        public long Now = 2_000_000;
        public readonly LegacyUiSettings S = new() { SummonPets = true, MonsterRange = 20, CustomPetRange = 5 };
        public readonly PetManager Pets;
        public readonly WorldObjectCache Cache;
        public readonly MonsterDamageStore Store = new();

        public Rig(string essenceName = "Fire Moar Essence (50)", string? otherPet = null, bool frostFirst = false, bool successor = false)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;   // UseObject (summon)
            Place(Player, "Pet Tester", 96, 96, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            Place(Mob, "Practice Dummy", 96, 99, creature: true);   // 3 m north; no creature-type weakness
            FakeHost.Wcids[Mob] = MobWcid;
            var known = new List<uint> { Mob };
            Carry(Essence, essenceName, known);
            if (successor) Carry(Essence2, essenceName, known);
            if (frostFirst)
            {
                Carry(Frost, "Frost Moar Essence (50)", known);
                S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Frost), Name = "Frost Moar Essence (50)", Type = "Pet" });
            }
            S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Essence), Name = essenceName, Type = "Pet" });

            var host = FakeHost.Create(Player);
            Cache = FakeHost.MakeCache(host, Player, known);
            S.IsMacroRunning = true;
            var combat = new CombatManager(host, S, Cache);
            combat.SetPlayerId(Player);
            combat.SetDamageStores(null, Store);
            combat.ScanNearbyTargets();
            Check.True(combat.ScannedTargets.Count == 1, "rig: the monster is scanned");
            if (otherPet != null) ShowPet(Other, otherPet);
            PetManager.Clock = () => Now;
            Pets = new PetManager(host, S, Cache, combat, null);
        }

        private static void Carry(uint id, string name, List<uint> known)
        {
            FakeHost.Names[id] = name;
            FakeHost.Containers[id] = Player;
            FakeHost.Ints[(id, StructureKey)] = 50;
            known.Add(id);
        }

        public void ShowPet(uint id, string name)
        {
            Place(id, name, 96, 97, creature: true);
            _ = Cache[unchecked((int)id)];   // filed on the landscape as a creature
            Check.True(Cache.GetLandscape().Any(w => w.Id == unchecked((int)id)), "rig: the pet is in the landscape cache");
        }

        private static void Place(uint id, string name, float x, float y, bool creature)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
            if (creature) FakeHost.ItemTypes[id] = 0x10;
        }

        public void Run(long ms)
        {
            for (long t = 0; t < ms; t += 100)
            {
                Now += 100;
                Pets.OnHeartbeat(0);
            }
        }

        public static int UsesOf(uint id) => FakeHost.Uses.Count(u => u == id);

        public void Dispose() => PetManager.Clock = static () => Environment.TickCount64;
    }

    private static bool Logged(string text) => FakeHost.Logs.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static void OtherPetDoesNotHold()
    {
        using var rig = new Rig(otherPet: "Pet Tester's Common Pet Drudge");
        rig.Run(1_500);
        Check.Eq(Rig.UsesOf(Essence), 1, "the drudge is none of the player's combat pets: the listed essence is summoned (before: never)");
        Check.True(Logged("none of your combat pets") && Logged("0x80320020"), "the other pet is logged with its id");

        // The server puts the passive pet away and the combat pet comes.
        FakeHost.Names.Remove(Other);
        FakeHost.Positions.Remove(Other);
        rig.ShowPet(Ours, "Pet Tester's Fire Moar");
        rig.Run(3 * 60_000);
        Check.Eq(Rig.UsesOf(Essence), 1, "the player's own pet is out: no second summon");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("none of your combat pets")), 1, "logged once");
    }

    private static void OtherPetAlreadyActive()
    {
        using var rig = new Rig(otherPet: "Pet Tester's Acid Maiden");   // summoned by hand from an unlisted essence
        rig.Run(1_500);
        Check.Eq(Rig.UsesOf(Essence), 1, "a pet not from the list: one summon is tried");
        rig.Pets.OnChatWindowText("Pet Tester's Acid Maiden is already active");
        rig.Run(4 * 60_000);
        Check.Eq(Rig.UsesOf(Essence), 1, "the server says it is a combat pet: it holds the slot, no Use every 45 s");
    }

    private static void OwnSummonHoldsWhateverItsName()
    {
        using var rig = new Rig(essenceName: "Duo Summon Gem");
        rig.Run(1_500);
        Check.Eq(Rig.UsesOf(Essence), 1, "no pet out: summon");
        rig.ShowPet(Ours, "Pet Tester's Olthoi Hunter");    // not named after the gem
        rig.Run(3 * 60_000);
        Check.Eq(Rig.UsesOf(Essence), 1, "the pet our summon brought holds the slot: no second summon");
        Check.False(Logged("none of your combat pets"), "and it is not called someone else's pet");
    }

    private static void PickFollowsReplacedEssence()
    {
        using var rig = new Rig(frostFirst: true, successor: true);
        rig.Store.SetManualPet(MobWcid, "I:" + unchecked((int)Essence));   // the player picked Fire Moar for this monster
        FakeHost.Names.Remove(Essence);       // gone (a death corpse); a same-named one is in the pack
        FakeHost.Containers.Remove(Essence);
        rig.Run(1_500);
        Check.Eq(Rig.UsesOf(Frost), 0, "the essence Auto would take first is not used (before: Frost Moar, 'picked essence not in pack')");
        Check.Eq(Rig.UsesOf(Essence2), 1, "the pick's same-named successor is summoned");
        Check.True(Logged("picked Fire Moar Essence"), "the choice line names the pick");
    }
}
