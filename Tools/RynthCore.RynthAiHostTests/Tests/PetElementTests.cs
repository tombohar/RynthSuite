using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The pet element choice and a server's holder of two summons (2026-11-22, a players' request:
// "set the elemental type for our pets ... these new Duo Pets don't seem to work right"):
//   * the Summons settings get a Pet element (Any = the per-monster choice, as before);
//   * a server plugin's profile names its holders ("summons"): an item that holds two summoning
//     items, is no "... Essence" by name, whose description lists the two, and whose own element
//     is the second's. Before, RynthAi saw one element (the second), never listed it from the pack,
//     used it before it held both (refused, then parked 45 s), and did not know its pets after a
//     reload (they are named for the held items, not the holder).
internal static class PetElementTests
{
    private const uint Player = 0x50000E21;
    private const uint Mob = 0x80330010;
    private const uint MobWcid = 41002;
    private const uint Acid = 0x80330001;      // a plain Acid essence (listed first)
    private const uint Duo = 0x80330002;       // the holder
    private const uint Cold = 0x80330003;      // a plain Frost essence
    private const uint DuoWcid = 1007741;
    private const uint StructureKey = 92, MaxStructureKey = 91, UiEffectsKey = 18, LongDescKey = 16;
    private const int FxFire = 0x20, FxFrost = 0x80, FxAcid = 0x100;
    private const uint Lb = 0xA9B40000;

    /// <summary>The holder row a server plugin publishes (its profile's "summons").</summary>
    private const string ProfileJson =
        "{\"schema\":1,\"id\":\"x\",\"summons\":[" +
        "{\"wcid\":1007741,\"nameMatch\":\"Duo Summon Gem\",\"slotPrefix\":\"Summon \",\"slots\":2,\"elementIsLast\":true}," +
        "{\"nameMatch\":\"\",\"slotPrefix\":\"Summon \"}," +          // no wcid, no name: skipped
        "{\"wcid\":5,\"nameMatch\":\"x\"}" +                          // no slot prefix: skipped
        "]}";

    private const string BoundDesc =
        "Summon 1: Fire Moar Essence (180)\r\n  Level 180\r\n  Ratings: Damage 0, Damage Resist 0, Crit 0, Crit Resist 0, Crit Damage 0, Crit Damage Resist 0\r\n" +
        "Summon 2: Frost K'nath Essence (200)\r\n  Level 200\r\n  Ratings: Damage 0, Damage Resist 0, Crit 0, Crit Resist 0, Crit Damage 0, Crit Damage Resist 0\r\n" +
        "Element: Frost";
    private const string HalfDesc = "Summon 1: Fire Moar Essence (180)\n  Level 180\n  Ratings: Damage 0";
    private const string BlankDesc = "Absorbs two summoning items into one.";

    private static IReadOnlyList<ServerSummonView> Rows => ServerSummonView.ParseList(ProfileJson);

    public static void Register(Runner r)
    {
        r.Add("pet element: retail essences: one element each, from UiEffects, else the name (golem: Bludgeon)", RetailEssences);
        r.Add("pet element: the profile's summons rows parse leniently; a holder is told by wcid, else by name", ProfileRows);
        r.Add("pet element: a full holder deals both held items' elements, in summon order; the second's from its UiEffects", BoundHolder);
        r.Add("pet element: a holder holding 0 or 1 of 2 can't summon; one not appraised yet with charges can", UnboundHolder);
        r.Add("pet element: no server row: a holder reads as before (one element, its UiEffects)", NoServerRow);
        r.Add("pet element: Rank - every pet of it, then the first pet, then a later one, then none", RankOrder);
        r.Add("pet element: the setting picks its element first; Any keeps the weakness pick (as before)", SettingPicks);
        r.Add("pet element: a monster's own pick or rule beats the setting; the setting beats the Default row", Precedence);
        r.Add("pet element: no essence of the chosen element: Auto as before, and the reason says so", NoneOfIt);
        r.Add("pet element: setting choices round-trip (Any = \"\"); the default is Any", SettingRoundTrip);
        r.Add("pet element: Cold set: the holder with a Cold pet is summoned before a listed Acid essence (before: the Acid one)", SummonerUsesHolder);
        r.Add("pet element: a listed holder that holds 1 of 2 is skipped, logged once; the next essence is summoned (before: refused, parked 45 s)", SummonerSkipsUnboundHolder);
        r.Add("pet element: after a reload, a pet named for a holder's held item counts as yours; no resummon (before: summoned over it)", HolderPetHoldsSlot);
        r.Add("pet element: pack fill lists a holder as a Pet (no 'Essence' in its name); not without the server row", FillListsHolder);
    }

    private static void RetailEssences()
    {
        var fire = PetElements.Describe("Fire Moar Essence (50)", 0, FxFire, null, true, null);
        Check.Eq(fire.ElementText, "Fire", "UiEffects Fire");
        Check.False(fire.IsHolder, "a plain essence is no holder");
        Check.True(fire.Ready, "and can summon");
        Check.Eq(PetElements.Describe("Frost K'nath Essence (200)", 0, FxFrost, null, true, null).ElementText, "Cold", "UiEffects Frost reads Cold");
        Check.Eq(PetElements.Describe("Mukkir Essence", 0, FxAcid, null, true, null).ElementText, "Acid", "UiEffects wins over a name with no element word");
        Check.Eq(PetElements.Describe("Blizzard Wisp Essence", 0, 0, null, true, null).ElementText, "Cold", "no UiEffects: the name");
        Check.Eq(PetElements.Describe("Diamond Golem Essence", 0, 0, null, true, null).ElementText, "Bludgeon", "golems are bludgeon");
        Check.Eq(PetElements.Describe("Pet Essence", 0, 0, null, true, null).Elements.Count, 0, "no element known");
        Check.Eq(PetElements.FromUiEffects(0x1000), "Nether", "Nether bit");
        Check.Eq(PetElements.FromUiEffects(0x0400), "Slash", "Slash bit");
        Check.Eq(PetElements.FromUiEffects(0x0800), "Pierce", "Pierce bit");
        Check.Eq(PetElements.FromUiEffects(0x0200), "Bludgeon", "Bludgeon bit");
        Check.Eq(PetElements.FromUiEffects(0x0040), "Lightning", "Lightning bit");
        // A server's rows never make a retail essence a holder.
        Check.False(PetElements.Describe("Fire Moar Essence (50)", 49001, FxFire, null, true, Rows).IsHolder, "a retail essence with the rows: still plain");
    }

    private static void ProfileRows()
    {
        var rows = Rows;
        Check.Eq(rows.Count, 1, "the two unusable rows are skipped");
        var r0 = rows[0];
        Check.Eq(r0.Wcid, DuoWcid, "wcid");
        Check.Eq(r0.SlotPrefix, "Summon ", "slot prefix kept with its space");
        Check.Eq(r0.Slots, 2, "slots");
        Check.True(r0.ElementIsLast, "elementIsLast");
        Check.True(r0.Matches(DuoWcid, "anything"), "by wcid");
        Check.False(r0.Matches(1007740, "Duo Summon Gem"), "another wcid is not it, whatever its name");
        Check.True(r0.Matches(0, "Duo Summon Gem"), "wcid not known: by name");
        Check.False(r0.Matches(0, "Fire Moar Essence"), "not by another name");
        Check.Eq(ServerSummonView.ParseList("{\"schema\":1}").Count, 0, "an older plugin: none");
        Check.Eq(ServerSummonView.ParseList("not json").Count, 0, "unusable text: none");
        Check.Eq(ServerSummonView.ParseList("{\"summons\":[{\"nameMatch\":\"Gem\",\"slotPrefix\":\"Held \"}]}")[0].Slots, 2, "slots default to 2");
        Check.Eq(string.Join("|", PetElements.HeldItems("Summon 1: A Essence\nSummoning is fun\nSummon 2 B Essence\n  Summon 10:  C Essence  \nSummon x: D", "Summon ")),
                 "A Essence|C Essence", "held lines: a number and a colon after the prefix");
    }

    private static void BoundHolder()
    {
        var info = PetElements.Describe("Duo Summon Gem", DuoWcid, FxFrost, BoundDesc, true, Rows);
        Check.True(info.IsHolder, "a holder");
        Check.True(info.Ready, "full: it can summon");
        Check.Eq(string.Join("|", info.Held), "Fire Moar Essence (180)|Frost K'nath Essence (200)", "both held items, in order");
        Check.Eq(string.Join("|", info.Elements), "Fire|Cold", "first pet Fire, second Cold");
        Check.Eq(info.ElementText, "Fire+Cold", "shown as Fire+Cold");
        Check.True(info.Has("Fire") && info.Has("Cold") && !info.Has("Acid"), "Has");
        // The server writes the second item's element into the holder's UiEffects: it wins over the name.
        var acidLast = PetElements.Describe("Duo Summon Gem", DuoWcid, FxAcid, BoundDesc, true, Rows);
        Check.Eq(string.Join("|", acidLast.Elements), "Fire|Acid", "the last slot's element is the holder's UiEffects");
        // A golem second: no element bits, so its name.
        var golem = PetElements.Describe("Duo Summon Gem", DuoWcid, 0,
            "Summon 1: Frost K'nath Essence (200)\nSummon 2: Diamond Golem Essence (150)", true, Rows);
        Check.Eq(string.Join("|", golem.Elements), "Cold|Bludgeon", "no UiEffects element: the held item's name");
        // Matched by name when the wcid is not known.
        Check.True(PetElements.Describe("Duo Summon Gem", 0, FxFrost, BoundDesc, true, Rows).IsHolder, "by name with no wcid");
    }

    private static void UnboundHolder()
    {
        var blank = PetElements.Describe("Duo Summon Gem", DuoWcid, 0, BlankDesc, false, Rows);
        Check.True(blank.IsHolder && !blank.Ready, "blank: a holder that can't summon");
        Check.Eq(blank.Held.Count, 0, "holds nothing");
        var half = PetElements.Describe("Duo Summon Gem", DuoWcid, 0, HalfDesc, false, Rows);
        Check.True(!half.Ready && half.Held.Count == 1, "one of two: can't summon");
        Check.Eq(half.ElementText, "Fire", "its one held item's element shows");
        var blankNoDesc = PetElements.Describe("Duo Summon Gem", DuoWcid, 0, null, false, Rows);
        Check.False(blankNoDesc.Ready, "not appraised and no charges: not ready");
        var full = PetElements.Describe("Duo Summon Gem", DuoWcid, FxAcid, null, true, Rows);
        Check.True(full.Ready, "not appraised yet but with charges (the server gives them only when it binds): ready");
        Check.Eq(string.Join("|", full.Elements), "|Acid", "first pet not known yet, the second's from UiEffects");
    }

    private static void NoServerRow()
    {
        var info = PetElements.Describe("Duo Summon Gem", DuoWcid, FxFrost, BoundDesc, true, null);
        Check.False(info.IsHolder, "no row: no holder");
        Check.Eq(info.ElementText, "Cold", "its UiEffects only, as before");
    }

    private static PetDeviceInfo Plain(string elem) => PetElements.Describe(elem + " Thing Essence", 0,
        elem switch { "Fire" => FxFire, "Cold" => FxFrost, "Acid" => FxAcid, _ => 0 }, null, true, null);

    private static PetDeviceInfo Holder(string a, string b) => new(new[] { a, b }, new[] { a + " Essence", b + " Essence" }, true, true, 2);

    private static void RankOrder()
    {
        Check.Eq(PetChoice.Rank(Plain("Cold"), "Cold"), 0, "a plain Cold essence: every pet");
        Check.Eq(PetChoice.Rank(Holder("Cold", "Cold"), "Cold"), 0, "a holder of two Cold: every pet");
        Check.Eq(PetChoice.Rank(Holder("Cold", "Fire"), "Cold"), 1, "Cold first");
        Check.Eq(PetChoice.Rank(Holder("Fire", "Cold"), "Cold"), 2, "Cold second");
        Check.Eq(PetChoice.Rank(Plain("Fire"), "Cold"), 3, "none");
        Check.Eq(PetChoice.Rank(new PetDeviceInfo(Array.Empty<string>(), Array.Empty<string>(), false, true, 1), "Cold"), 3, "nothing known");
    }

    private static List<(int Id, string Name, PetDeviceInfo Info)> Devices(params (int Id, PetDeviceInfo Info)[] d)
        => d.Select(x => (x.Id, "item" + x.Id, x.Info)).ToList();

    private static string Ids(List<(int Id, string Name, string Element)> l) => string.Join(",", l.Select(x => x.Id));

    private static void SettingPicks()
    {
        var list = Devices((1, Plain("Acid")), (2, Holder("Fire", "Cold")), (3, Plain("Fire")), (4, Holder("Cold", "Fire")), (5, Plain("Cold")));
        var ordered = PetChoice.Order(list, "", null, out string why, preferElement: "Cold");
        Check.Eq(Ids(ordered), "5,4,2,1,3", "Cold: the plain Cold, the holder with Cold first, the one with Cold second, then the rest in order");
        Check.True(why.Contains("Pet element setting"), $"the reason names the setting ({why})");
        Check.Eq(ordered[1].Element, "Cold+Fire", "a holder's elements are shown joined");
        // Any: the weakness pick, as before the setting.
        var weak = new CreatureWeakness.Ranking { Source = "server data" };
        weak.Order.Add(("Fire", 1.5));
        weak.Order.Add(("Cold", 1.2));
        var auto = PetChoice.Order(list, "", weak, out string autoWhy);
        Check.Eq(auto[0].Id, 3, "Any: Fire (the weakness) first, the plain Fire essence");
        Check.Eq(auto[1].Id, 2, "then the holder with Fire first");
        Check.True(autoWhy.StartsWith("Auto: Fire (server data"), $"Any's reason is the weakness ({autoWhy})");
        var anyNoData = PetChoice.Order(list, "", null, out string noWhy, preferElement: "Any");
        Check.Eq(Ids(anyNoData), "1,2,3,4,5", "'Any' with no data: the Items order");
        Check.True(noWhy.StartsWith("Auto: no element data"), "and the reason as before");
    }

    private static void Precedence()
    {
        var list = Devices((1, Plain("Acid")), (2, Plain("Fire")), (3, Plain("Cold")));
        Check.Eq(PetChoice.Order(list, "E:Fire", null, out _, preferElement: "Cold")[0].Id, 2, "a Damage-panel element pick beats the setting");
        Check.Eq(PetChoice.Order(list, "I:1", null, out _, preferElement: "Cold")[0].Id, 1, "a Damage-panel essence pick beats the setting");
        Check.Eq(PetChoice.Order(list, "", null, out _, ruleElement: "Fire", preferElement: "Cold")[0].Id, 2, "the monster's own rule beats the setting");
        Check.Eq(PetChoice.Order(list, "", null, out _, preferElement: "Cold", defaultElement: "Fire")[0].Id, 3, "the setting beats the Default row");
        Check.Eq(PetChoice.Order(list, "", null, out _, defaultElement: "Fire")[0].Id, 2, "Any: the Default row, as before");
        Check.Eq(PetChoice.Order(Devices((1, Plain("Acid")), (2, Holder("Fire", "Cold"))), "E:Cold", null, out string why)[0].Id, 2,
                 "an element pick takes a holder with a pet of it");
        Check.Eq(why, "Cold chosen", "and says it was chosen");
    }

    private static void NoneOfIt()
    {
        var list = Devices((1, Plain("Acid")), (2, Plain("Fire")));
        var weak = new CreatureWeakness.Ranking { Source = "learned" };
        weak.Order.Add(("Fire", double.NaN));
        var ordered = PetChoice.Order(list, "", weak, out string why, preferElement: "Cold");
        Check.Eq(ordered[0].Id, 2, "no Cold essence: the weakness pick");
        Check.True(why.Contains("no Cold essence") && why.Contains("Auto: Fire"), $"the reason says both ({why})");
    }

    private static void SettingRoundTrip()
    {
        Check.Eq(PetChoice.SettingChoices[0], "Any", "Any first");
        for (int i = 0; i < PetChoice.SettingChoices.Length; i++)
            Check.Eq(PetChoice.SettingIndex(PetChoice.SettingValue(i)), i, $"choice {PetChoice.SettingChoices[i]} round-trips");
        Check.Eq(PetChoice.SettingValue(0), "", "Any is stored as \"\"");
        Check.Eq(PetChoice.SettingIndex("electric"), Array.IndexOf(PetChoice.SettingChoices, "Lightning"), "Electric reads Lightning");
        Check.Eq(PetChoice.SettingIndex("banana"), 0, "an unknown value reads Any");
        Check.Eq(new LegacyUiSettings().PetElement, "", "the default is Any");
        foreach (string e in PetChoice.Elements)
            Check.True(PetChoice.SettingChoices.Contains(e), $"every element is a choice ({e})");
    }

    // ── The summoner (PetManager) ───────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public long Now = 3_000_000;
        public readonly LegacyUiSettings S = new() { SummonPets = true, MonsterRange = 20, CustomPetRange = 5 };
        public readonly PetManager Pets;
        public readonly WorldObjectCache Cache;

        public Rig(string? duoDesc, bool duoCharges, bool serverRow = true, string? petOut = null, bool listAcid = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            PetElements.ServerSummons = serverRow ? Rows : Array.Empty<ServerSummonView>();
            Place(Player, "Pet Tester", 96, 96, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            Place(Mob, "Practice Dummy", 96, 99, creature: true);
            FakeHost.Wcids[Mob] = MobWcid;
            var known = new List<uint> { Mob, Acid, Duo };
            Carry(Acid, "Acidic Moar Essence (50)", FxAcid);
            FakeHost.Names[Duo] = "Duo Summon Gem";
            FakeHost.Containers[Duo] = Player;
            FakeHost.Wcids[Duo] = DuoWcid;
            FakeHost.Appraised.Add(Duo);
            if (duoCharges)
            {
                FakeHost.Ints[(Duo, StructureKey)] = 50;
                FakeHost.Ints[(Duo, MaxStructureKey)] = 50;
                FakeHost.Ints[(Duo, 366u)] = 54;          // the held items' Summoning requirement
                FakeHost.Ints[(Duo, UiEffectsKey)] = FxFrost;
            }
            if (duoDesc != null) FakeHost.Strings[(Duo, LongDescKey)] = duoDesc;
            if (listAcid) S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Acid), Name = "Acidic Moar Essence (50)", Type = "Pet" });
            S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Duo), Name = "Duo Summon Gem", Type = "Pet" });

            var host = FakeHost.Create(Player);
            Cache = FakeHost.MakeCache(host, Player, known);
            S.IsMacroRunning = true;
            var combat = new CombatManager(host, S, Cache);
            combat.SetPlayerId(Player);
            combat.SetDamageStores(null, new MonsterDamageStore());
            combat.ScanNearbyTargets();
            Check.True(combat.ScannedTargets.Count == 1, "rig: the monster is scanned");
            if (petOut != null)
            {
                Place(0x80330021, petOut, 96, 97, creature: true);
                _ = Cache[unchecked((int)0x80330021)];
            }
            PetManager.Clock = () => Now;
            Pets = new PetManager(host, S, Cache, combat, null);
        }

        private static void Carry(uint id, string name, int fx)
        {
            FakeHost.Names[id] = name;
            FakeHost.Containers[id] = Player;
            FakeHost.Ints[(id, StructureKey)] = 50;
            FakeHost.Ints[(id, UiEffectsKey)] = fx;
            FakeHost.Appraised.Add(id);
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

        public void Dispose()
        {
            PetManager.Clock = static () => Environment.TickCount64;
            PetElements.ServerSummons = Array.Empty<ServerSummonView>();
        }
    }

    private static int UsesOf(uint id) => FakeHost.Uses.Count(u => u == id);
    private static bool Logged(string text) => FakeHost.Logs.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static void SummonerUsesHolder()
    {
        using (var before = new Rig(BoundDesc, duoCharges: true))
        {
            before.Run(1_500);
            Check.Eq(UsesOf(Acid), 1, "Any, no element data: the Items order, the Acid essence first (as before)");
        }
        using var rig = new Rig(BoundDesc, duoCharges: true);
        rig.S.PetElement = "Cold";
        rig.Run(1_500);
        Check.Eq(UsesOf(Duo), 1, "Cold set: the holder, whose second pet is Cold, is summoned");
        Check.Eq(UsesOf(Acid), 0, "not the Acid essence");
        Check.True(Logged("Pet element setting"), "the choice line names the setting");
        using var fire = new Rig(BoundDesc, duoCharges: true);
        fire.S.PetElement = "Fire";
        fire.Run(1_500);
        Check.Eq(UsesOf(Duo), 1, "Fire set: the holder too (its first pet is Fire; before, it read Cold only)");
    }

    private static void SummonerSkipsUnboundHolder()
    {
        using var rig = new Rig(HalfDesc, duoCharges: false);
        rig.S.ConsumableRules.Reverse();   // the holder listed first
        rig.Run(1_500);
        Check.Eq(UsesOf(Duo), 0, "the holder holding 1 of 2 is not used");
        Check.Eq(UsesOf(Acid), 1, "the next listed essence is");
        Check.True(Logged("holds 1 of the 2 summons"), "the skip is logged");
        rig.Run(60_000);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("holds 1 of the 2")), 1, "logged once");
        Check.Eq(UsesOf(Duo), 0, "and never used");
    }

    private static void HolderPetHoldsSlot()
    {
        using (var rig = new Rig(BoundDesc, duoCharges: true, petOut: "Pet Tester's Fire Moar", listAcid: false))
        {
            rig.Run(3_000);
            Check.Eq(UsesOf(Duo), 0, "a pet named for the holder's first held item is yours: no summon over it");
        }
        using var noRow = new Rig(BoundDesc, duoCharges: true, serverRow: false, petOut: "Pet Tester's Fire Moar", listAcid: false);
        noRow.Run(3_000);
        Check.Eq(UsesOf(Duo), 1, "without the server row it is none of yours and the holder is summoned (the old behaviour)");
    }

    private static void FillListsHolder()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        var host = FakeHost.Create(Player);
        FakeHost.Names[Duo] = "Duo Summon Gem";
        FakeHost.Wcids[Duo] = DuoWcid;
        var inv = new List<WorldObject> { new(unchecked((int)Duo), "Duo Summon Gem", AcObjectClass.Misc) };
        try
        {
            PetElements.ServerSummons = Rows;
            var s = new LegacyUiSettings();
            new LegacyWeaponsUi(s, host).FillFromPack(inv, Player);
            Check.True(s.ConsumableRules.Any(r => r.Name == "Duo Summon Gem" && r.Type == "Pet"), "listed as a Pet (before: skipped, no 'Essence')");
            PetElements.ServerSummons = Array.Empty<ServerSummonView>();
            var s2 = new LegacyUiSettings();
            new LegacyWeaponsUi(s2, host).FillFromPack(inv, Player);
            Check.False(s2.ConsumableRules.Any(r => r.Name == "Duo Summon Gem"), "no server row: not listed, as before");
        }
        finally
        {
            PetElements.ServerSummons = Array.Empty<ServerSummonView>();
        }
    }
}
