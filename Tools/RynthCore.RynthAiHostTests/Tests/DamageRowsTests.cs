using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Damage tab's monster rows (2026-10-11, a player's screenshot):
//  * the player's own character was a row (no HP, no "weak to", 0 kills). The object cache files
//    any creature whose type reads first as a Monster - other players, and the character itself
//    once something looked it up through the cache - and players are attackable, so the class
//    and attackable checks let them into the nearby rows and the appraisal store;
//  * two "Rampager" rows, one with the kills and one without: retail has five Rampager weenies
//    (10810, 10811, 16916, 21526, 22599), and each wcid was a row. Now one row per name.
internal static class DamageRowsTests
{
    private const uint Player = 0x50000C01, OtherPlayer = 0x50000C02, AdminPlayer = 0x50000C03;
    private const uint Pet = 0x80001003, MonsterA = 0x80001001, MonsterB = 0x80001002;
    private const uint RampagerA = 10810, RampagerB = 10811, PetWcid = 48000;
    private const uint ItemTypeCreature = 0x10, BfPlayer = 0x8, BfAttackable = 0x10;
    private const uint Wand = 0x80000401;
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("damage rows: no player (you, another player, an admin, a pet) is listed or recorded", NoPlayerRows);
        r.Add("damage rows: player rows already saved are dropped when the stores load", StoredPlayerRowsDropped);
        r.Add("damage rows: two wcids sharing a name are one row, and an edit on it applies to both", SameNameOneRow);
        r.Add("damage rows: one wcid from every source and difficulty tier is one row", OneWcidOneRow);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly MonsterDamageStore Store = new();
        public readonly CreatureProfileStore Creatures = new();

        public Bot(RynthCoreHost host, string name)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            string folder = Path.Combine(Program.TempRoot, "damage-rows", name);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            Store.SetCharacter(folder);
            Set("_damageStore", Store);
            Set("_creatureStore", Creatures);
            foreach (uint id in new[] { OtherPlayer, AdminPlayer, Pet, MonsterA, MonsterB })
                cache.OnCreateObject(id);
            cache.Tick();
            // What put the character in the table: a lookup of it through the cache files it as
            // a landscape Monster (the indexer's lazy classification doesn't skip the player).
            _ = cache[unchecked((int)Player)];
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public HashSet<uint> Seen => (HashSet<uint>)typeof(RynthAiPlugin).GetField("_seenMonstersThisSession", Any)!.GetValue(P)!;

        /// <summary>The monster rows of the Damage tab (the Default line left out).</summary>
        public List<JsonElement> Rows()
        {
            using var doc = JsonDocument.Parse(P.BuildMonsterDamageJson());
            return doc.RootElement.EnumerateArray()
                .Where(e => !(e.TryGetProperty("isDefault", out var d) && d.GetBoolean()))
                .Select(e => e.Clone()).ToList();
        }
    }

    private static void World(bool withPet = true)
    {
        FakeHost.Reset();
        var here = (Cell: 0xA9B40001u, X: 10f, Y: 10f, Z: 0f);
        FakeHost.PlayerPose = here;
        void Creature(uint id, string name, uint wcid, uint bitfield, float dx)
        {
            FakeHost.Names[id] = name;
            FakeHost.Wcids[id] = wcid;
            FakeHost.ItemTypes[id] = ItemTypeCreature;
            FakeHost.Bitfields[id] = bitfield;
            FakeHost.Positions[id] = (here.Cell, here.X + dx, here.Y, here.Z);
        }
        Creature(Player, "Drakkon", PlayerWeenies.Human, BfPlayer | BfAttackable, 0f);
        Creature(OtherPlayer, "Lucy McJuicy", PlayerWeenies.Human, BfPlayer | BfAttackable, 2f);
        Creature(AdminPlayer, "+Buffy", PlayerWeenies.Admin, BfPlayer | BfAttackable, 3f);
        Creature(MonsterA, "Rampager", RampagerA, BfAttackable, 5f);
        Creature(MonsterB, "Rampager", RampagerB, BfAttackable, 6f);
        if (withPet)
        {
            Creature(Pet, "Fire Spirit", PetWcid, BfAttackable, 1f);
            FakeHost.InstanceIds[(Pet, 44)] = Player;   // PetOwner
        }
    }

    private static uint[] Wcids(JsonElement row)
        => row.TryGetProperty("wcids", out var w) ? w.EnumerateArray().Select(x => (uint)x.GetInt64()).ToArray()
                                                    : new[] { (uint)row.GetProperty("wcid").GetInt64() };

    // ── Tests ────────────────────────────────────────────────────────────────

    private static void NoPlayerRows()
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, "no-player");

        // Appraise everyone: each answer carries max health, which is what records a creature.
        foreach (uint id in new[] { Player, OtherPlayer, AdminPlayer, Pet, MonsterA })
            bot.P.OnUpdateHealth(id, 1f, 400, 435);

        var rows = bot.Rows();
        string[] names = rows.Select(r => r.GetProperty("name").GetString() ?? "").ToArray();
        foreach (string who in new[] { "Drakkon", "Lucy McJuicy", "+Buffy", "Fire Spirit" })
            Check.False(names.Contains(who), $"'{who}' is not a Damage-tab row (rows: {string.Join(", ", names)})");
        Check.False(rows.SelectMany(Wcids).Any(PlayerWeenies.IsPlayerWcid), "no row under a player weenie (1, 4)");
        Check.True(names.Contains("Rampager"), "the monsters nearby are rows");

        Check.False(bot.Creatures.TryGetByWcid(PlayerWeenies.Human, out _), "no player in creatures.json (wcid 1)");
        Check.False(bot.Creatures.TryGetByWcid(PlayerWeenies.Admin, out _), "no admin in creatures.json (wcid 4)");
        Check.False(bot.Creatures.TryGetByName("Fire Spirit", out _), "no pet in creatures.json");
        Check.True(bot.Creatures.TryGetByWcid(RampagerA, out _), "the monster is recorded");
        Check.False(bot.Seen.Contains(PlayerWeenies.Human) || bot.Seen.Contains(PetWcid), "nor in this session's appraised list");

        // The learned store refuses a player weenie whatever records it.
        bot.Store.RecordHit(Wand, PlayerWeenies.Human, "Lucy McJuicy", "Fire", 7, 120, false);
        bot.Store.RecordKill(Wand, PlayerWeenies.Human, "Lucy McJuicy", "Fire", 7, 3, 400);
        bot.Store.RecordTaken(PlayerWeenies.Human, "Lucy McJuicy", "Fire", 50);
        bot.Store.SetManualHp(PlayerWeenies.Admin, 999);
        bot.Store.NoteCast(PlayerWeenies.Human, 7, "Lucy McJuicy");
        Check.False(bot.Store.Snapshot().Any(r => PlayerWeenies.IsPlayerWcid(r.Wcid)), "monster_damage.txt never records a player");
        Check.Eq(bot.Store.GetManualHp(PlayerWeenies.Admin), 0.0, "nor a setting on one");
    }

    private static void StoredPlayerRowsDropped()
    {
        World(withPet: false);
        var host = FakeHost.Create(Player);

        // monster_damage.txt with the character's own row and a monster's.
        string folder = Path.Combine(Program.TempRoot, "damage-rows", "stored");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "monster_damage.txt");
        File.WriteAllLines(file, new[]
        {
            "# RynthAi per-character monster damage learning (auto-generated).",
            "M|1|Drakkon|0",
            "D|1|Drakkon|0|Fire|0|0|0|1|2|0|0|0|0",
            "T|4|+Buffy|Fire|3|20|30",
            "H|10810|Rampager|420|4",
            "D|10810|Rampager|2147487513|Fire|7|120|20|3.5|4|200|4|100|16",
        });
        var store = new MonsterDamageStore();
        store.SetCharacter(folder);
        Check.Eq(string.Join(",", store.Snapshot().Select(r => r.Wcid).Distinct()), "10810", "only the monster loads");
        store.SaveIfDirty();
        string saved = File.ReadAllText(file);
        Check.False(saved.Contains("Drakkon") || saved.Contains("+Buffy"), "the file is saved without the player rows");
        Check.True(saved.Contains("Rampager"), "and with the monster");

        // creatures.json (Program points CreatureProfileStore.Folder at the scratch folder).
        string cfile = Path.Combine(CreatureProfileStore.Folder, "creatures.json");
        Directory.CreateDirectory(CreatureProfileStore.Folder);
        File.WriteAllText(cfile,
            "{\"Lucy McJuicy|1\":{\"Name\":\"Lucy McJuicy\",\"Wcid\":1,\"CreatureType\":31,\"MaxHealth\":340,\"KnownSpellIds\":[]},"
          + "\"+Buffy|4\":{\"Name\":\"+Buffy\",\"Wcid\":4,\"CreatureType\":31,\"MaxHealth\":335,\"KnownSpellIds\":[]},"
          + "\"Rampager|10810\":{\"Name\":\"Rampager\",\"Wcid\":10810,\"CreatureType\":0,\"MaxHealth\":280,\"KnownSpellIds\":[]}}");
        var creatures = new CreatureProfileStore();
        creatures.Load();
        Check.False(creatures.TryGetByWcid(1, out _) || creatures.TryGetByWcid(4, out _), "players are not loaded as creatures");
        Check.True(creatures.TryGetByWcid(RampagerA, out _), "the monster is");
        creatures.SaveIfDirty();
        string csaved = File.ReadAllText(cfile);
        Check.False(csaved.Contains("Lucy McJuicy") || csaved.Contains("+Buffy"), "creatures.json is saved without them");
        Check.True(csaved.Contains("Rampager"), "and keeps the monster");
        File.Delete(cfile);

        // And a bot that has them in memory lists none of them.
        var bot = new Bot(host, "stored-bot");
        bot.Set("_damageStore", store);
        Check.False(bot.Rows().Any(r => Wcids(r).Any(PlayerWeenies.IsPlayerWcid)), "no player row in the Damage tab");
    }

    private static void SameNameOneRow()
    {
        World(withPet: false);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, "same-name");

        // Rampager 10810: four kills. Rampager 10811: appraised and standing next to it, never killed.
        // Both had a manual HP of 435 (the screenshot), set one row at a time.
        for (int i = 0; i < 4; i++)
        {
            bot.Store.RecordHit(Wand, RampagerA, "Rampager", "Fire", 7, 145, false);
            bot.Store.RecordKill(Wand, RampagerA, "Rampager", "Fire", 7, 3, 435);
        }
        bot.Store.SetManualHp(RampagerA, 435);
        bot.Store.SetManualHp(RampagerB, 435);
        bot.P.OnUpdateHealth(MonsterB, 1f, 435, 435);

        var rows = bot.Rows().Where(r => r.GetProperty("name").GetString() == "Rampager").ToList();
        Check.Eq(rows.Count, 1, "one Rampager row");
        var row = rows[0];
        Check.Eq(string.Join(",", Wcids(row)), "10810,10811", "listing both kinds");
        Check.Eq((uint)row.GetProperty("wcid").GetInt64(), RampagerA, "the row is the kind with the kills");
        Check.Eq(row.GetProperty("kills").GetInt32(), 4, "with its kills");
        Check.Eq(row.GetProperty("hp").GetInt32(), 435, "and the HP");
        Check.True(row.GetProperty("hpManual").GetBoolean(), "set by hand");
        Check.True(row.GetProperty("nearby").GetBoolean(), "nearby: both stand here");

        // Edits on the row reach every kind.
        bot.P.SetMonsterHp(RampagerA, 500);
        Check.Eq(bot.Store.GetManualHp(RampagerB), 500.0, "an HP set on the row applies to the other kind");
        bot.P.SetMonsterWeapon(RampagerA, Wand);
        Check.Eq(bot.Store.GetManualWeapon(RampagerB), Wand, "so does a weapon");
        bot.P.SetMonsterOffhand(RampagerA, 77);
        Check.Eq(bot.Store.GetManualOffhand(RampagerB), 77u, "and an offhand");
        bot.P.SetMonsterPet(RampagerA, "E:Fire");
        Check.Eq(bot.Store.GetManualPet(RampagerB), "E:Fire", "and a pet");
        Check.Eq(bot.Store.GetManualHp(RampagerA), 500.0, "the row's own kind too");

        // Two kinds both fought: the kills add up, one drawer line per weapon/spell.
        bot.Store.RecordKill(Wand, RampagerB, "Rampager", "Fire", 7, 5, 435);
        row = bot.Rows().Single(r => r.GetProperty("name").GetString() == "Rampager");
        Check.Eq(row.GetProperty("kills").GetInt32(), 5, "kills of both kinds");
        var tiers = row.GetProperty("tiers").EnumerateArray().ToList();
        Check.Eq(tiers.Count, 1, "one drawer line for the shared weapon and spell");
        Check.Eq(tiers[0].GetProperty("kills").GetInt32(), 5, "with both kinds' kills");
        Check.Eq(tiers[0].GetProperty("casts").GetDouble(), 3.4, "casts to kill weighted by kills ((4*3+5)/5)");

        // Removing the row removes every kind.
        Check.True(bot.P.DeleteMonsterRow(RampagerA.ToString()), "the row is removed");
        Check.False(bot.Store.Snapshot().Any(r => r.Wcid == RampagerA || r.Wcid == RampagerB), "both kinds' learned data went");
        Check.False(bot.Seen.Contains(RampagerB), "and the appraised kind with it");
    }

    private static void OneWcidOneRow()
    {
        World(withPet: false);
        FakeHost.Positions.Remove(MonsterB);   // only Rampager 10810 here
        FakeHost.Names.Remove(MonsterB);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, "one-wcid");

        // The same Rampager from every source: learned at real Dereth and at a server's
        // difficulty tier (kept per (wcid, tier)), appraised this session, standing nearby,
        // and in creatures.json under a differently cased name.
        bot.Store.RecordKill(Wand, RampagerA, "Rampager", "Fire", 7, 3, 435);
        bot.Store.Difficulty = 2;
        bot.Store.RecordKill(Wand, RampagerA, "Rampager", "Fire", 7, 4, 480);
        bot.Store.Difficulty = 0;
        bot.Creatures.Upsert(new CreatureProfile { Name = "rampager", Wcid = RampagerA, MaxHealth = 280 });
        bot.P.OnUpdateHealth(MonsterA, 1f, 435, 435);

        var rows = bot.Rows();
        Check.Eq(rows.Count, 1, $"one row (rows: {string.Join(", ", rows.Select(r => r.GetProperty("name").GetString() + "/" + r.GetProperty("wcid")))})");
        Check.Eq((uint)rows[0].GetProperty("wcid").GetInt64(), RampagerA, "the Rampager");
        Check.False(rows[0].TryGetProperty("wcids", out _), "of one kind");
        Check.Eq(rows[0].GetProperty("kills").GetInt32(), 1, "real Dereth's kill (the tier's are its own)");
    }
}
