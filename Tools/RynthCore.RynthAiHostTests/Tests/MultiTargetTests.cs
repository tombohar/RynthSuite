using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// One spell, several monsters: rings, walls, blasts and chain spells (Conquest). Each monster the
// spell hurts gets its own damage line naming it, the server sends health only for the selected
// target, and a kill notice names the monster that died. These tests drive CombatManager with
// those lines on the fake host: every line goes to the monster it names (never to the fight
// target), the other monsters get a health estimate, and kill notices from one cast keep the scan
// off each monster that died without dropping a fight target that is still alive.
internal static class MultiTargetTests
{
    private const uint Player = 0x50000C01;
    private const uint Lb = 0xA9B40000;
    private const uint SkulkerWcid = 1001, MosswartWcid = 1002;

    public static void Register(Runner r)
    {
        r.Add("damage text: the victim's name comes with the amount", VictimNames);
        r.Add("multi-target: each damage line goes to the monster it names", LinesGoToTheirMonsters);
        r.Add("multi-target: same-named monsters in one volley are told apart", SameNameVolley);
        r.Add("multi-target: other targets get a health estimate, the server's number wins", HealthEstimates);
        r.Add("multi-target: several kills from one cast keep the live fight target", KillsFromOneCast);
        r.Add("multi-target: the fight target's own kill notice still drops it", FightTargetKill);
    }

    // ── The parser ───────────────────────────────────────────────────────────

    private static string? Victim(string text) => CombatText.TryParseOutgoingHit(text, out string v, out _) ? v : null;

    private static void VictimNames()
    {
        Check.Eq(Victim("You blast Drudge Skulker for 153 points with Whirling Blade VII."), "Drudge Skulker", "ACE war magic");
        Check.Eq(Victim("You scorch the Mosswart Worshipper for 1,234 points of fire damage!"), "Mosswart Worshipper", "retail, 'the' dropped");
        Check.Eq(Victim("Critical hit! Sneak Attack! You gore Olthoi Slasher for 1638 points with Force Arc VII."), "Olthoi Slasher", "two prefixes");
        Check.Eq(Victim("Overpower! You frost Tusker Guard for 40 points with Frost Bolt VI."), "Tusker Guard", "Overpower prefix");
        Check.Eq(Victim("You slash Banderling Scout for 12 points of slashing damage!"), "Banderling Scout", "melee");
        Check.Eq(Victim("Olthoi Swarm Harvester blisters your lower leg for 12 points of acid damage!"), null, "a monster's hit on us");
        Check.Eq(Victim("You drain 45 points of mana from Drudge Skulker."), null, "a drain");
        Check.Eq(Victim("You heal yourself for 64 points."), null, "a heal");
        Check.Eq(CombatText.CorpseOwnerName("Corpse of Drudge Skulker"), "Drudge Skulker", "corpse name");
        Check.Eq(CombatText.CorpseOwnerName("Drudge Skulker"), null, "not a corpse");
    }

    // ── The rig ──────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { MonsterRange = 30 };
        public readonly MonsterDamageStore Store = new();
        public CombatManager C = null!;
        public RynthCore.Plugin.RynthAi.WorldObjectCache Cache = null!;
        private readonly List<uint> _ids = new();
        private uint _next = 0x80000301;

        public Rig()
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;   // installs GetCurrentCombatMode
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.Names[Player] = "Me";
            FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
        }

        public uint Mob(string name, uint wcid, float north, float east)
        {
            uint id = _next++;
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Lb | 0x1C, 96f + east, 96f + north, 0f);
            FakeHost.ItemTypes[id] = 0x10;
            FakeHost.Wcids[id] = wcid;
            _ids.Add(id);
            return id;
        }

        public void Build(uint fightTarget, IReadOnlyDictionary<uint, float>? health = null)
        {
            var host = FakeHost.Create();
            Cache = FakeHost.MakeCache(host, Player, _ids, health);
            C = new CombatManager(host, S, Cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            C.EngageForTest(unchecked((int)fightTarget));
        }

        public float Hp(uint id) => Cache.GetHealthRatio(unchecked((int)id));

        /// <summary>Learned non-crit average damage per monster type (the Damage tab's rows).</summary>
        public Dictionary<uint, (double Avg, int Samples)> Learned()
            => Store.Snapshot().GroupBy(r => r.Wcid)
                    .ToDictionary(g => g.Key, g => (g.First().AvgDamage, g.Sum(r => r.DmgSamples)));
    }

    // ── The tests ────────────────────────────────────────────────────────────

    private static void LinesGoToTheirMonsters()
    {
        var rig = new Rig();
        uint skulker = rig.Mob("Drudge Skulker", SkulkerWcid, 5, 0);
        uint moss = rig.Mob("Mosswart Worshipper", MosswartWcid, 6, 2);
        rig.Build(skulker);

        rig.C.HandleChatForDamage("You blast Drudge Skulker for 100 points with Lightning Chain VI.");
        rig.C.HandleChatForDamage("You blast Mosswart Worshipper for 40 points with Lightning Chain VI.");

        var learned = rig.Learned();
        Check.True(learned.ContainsKey(MosswartWcid), "the Mosswart's hit is learned against the Mosswart");
        Check.Near(learned.GetValueOrDefault(MosswartWcid).Avg, 40, 1e-6, "the Mosswart's damage is its own 40");
        Check.Near(learned.GetValueOrDefault(SkulkerWcid).Avg, 100, 1e-6, "the Skulker keeps only its own 100 (not 140, not 70)");
        Check.Eq(learned.GetValueOrDefault(SkulkerWcid).Samples, 1, "one hit learned for the Skulker");
        Check.True(rig.Hp(moss) > 0f && rig.Hp(moss) < 1f, $"the Mosswart is marked hurt ({rig.Hp(moss):0.00})");
        Check.True(rig.Cache.IsHealthEstimated(unchecked((int)moss)), "and its health is our estimate");
        Check.True(!rig.Cache.IsHealthEstimated(unchecked((int)skulker)), "the fight target gets no estimate (the server sends its health)");
    }

    private static void SameNameVolley()
    {
        var rig = new Rig();
        uint target = rig.Mob("Olthoi Swarm Soldier", SkulkerWcid, 5, 0);
        uint near = rig.Mob("Olthoi Swarm Soldier", SkulkerWcid, 7, 0);
        uint far = rig.Mob("Olthoi Swarm Soldier", SkulkerWcid, 20, 0);
        rig.Build(target);
        rig.Store.SetManualHp(SkulkerWcid, 200);

        // One chain: the target, then the nearest other soldier, then the next.
        rig.C.HandleChatForDamage("You blast Olthoi Swarm Soldier for 100 points with Lightning Chain VI.");
        rig.C.HandleChatForDamage("You blast Olthoi Swarm Soldier for 50 points with Lightning Chain VI.");
        rig.C.HandleChatForDamage("You blast Olthoi Swarm Soldier for 20 points with Lightning Chain VI.");

        Check.Near(rig.Hp(near), 0.75, 1e-4, "second line -> the nearest other soldier (200 hp, -50)");
        Check.Near(rig.Hp(far), 0.90, 1e-4, "third line -> the next one (-20)");
        Check.True(!rig.Cache.IsHealthEstimated(unchecked((int)target)), "the first line was the fight target's");
    }

    private static void HealthEstimates()
    {
        var rig = new Rig();
        uint target = rig.Mob("Drudge Skulker", SkulkerWcid, 5, 0);
        uint moss = rig.Mob("Mosswart Worshipper", MosswartWcid, 6, 2);
        rig.Build(target);
        rig.Store.SetManualHp(MosswartWcid, 100);
        int m = unchecked((int)moss);

        Check.Near(rig.Hp(moss), -1, 1e-6, "no health known before the hit");
        rig.C.HandleChatForDamage("You blast Mosswart Worshipper for 30 points with Flame Ring VI.");
        Check.Near(rig.Hp(moss), 0.70, 1e-4, "100 hp, hit for 30 -> 0.70");
        System.Threading.Thread.Sleep(900);   // next volley
        rig.C.HandleChatForDamage("You blast Mosswart Worshipper for 500 points with Flame Ring VI.");
        Check.Near(rig.Hp(moss), RynthCore.Plugin.RynthAi.WorldObjectCache.EstimatedHealthFloor, 1e-6,
            "an estimate never reads dead (0): only the server says dead");
        rig.Cache.OnUpdateHealth(moss, 0.40f);
        Check.Near(rig.Hp(moss), 0.40, 1e-6, "the server's number replaces the estimate");
        Check.True(!rig.Cache.IsHealthEstimated(m), "and it is no longer an estimate");
        rig.Cache.ApplyDamageEstimate(m, 0.0);
        Check.Near(rig.Hp(moss), 0.40, 1e-6, "an unknown-max hit never raises a known value");
    }

    private static void KillsFromOneCast()
    {
        var rig = new Rig();
        uint target = rig.Mob("Olthoi Swarm Soldier", SkulkerWcid, 5, 0);
        uint twin = rig.Mob("Olthoi Swarm Soldier", SkulkerWcid, 7, 0);
        uint moss = rig.Mob("Mosswart Worshipper", MosswartWcid, 6, 2);
        rig.Build(target);

        // A cast hits all three; the server has the fight target at 60%.
        rig.C.HandleChatForDamage("You blast Olthoi Swarm Soldier for 100 points with Flame Ring VI.");
        rig.C.HandleChatForDamage("You blast Olthoi Swarm Soldier for 100 points with Flame Ring VI.");
        rig.C.HandleChatForDamage("You blast Mosswart Worshipper for 100 points with Flame Ring VI.");
        rig.Cache.OnUpdateHealth(target, 0.60f);

        // The next cast kills the twin and the Mosswart (one notice each), not the target.
        rig.C.OnKillNotification("Olthoi Swarm Soldier's death is preceded by a sharp, stabbing pain!");
        rig.C.OnKillNotification("You slay Mosswart Worshipper viciously enough to impart death several times over!");

        Check.True(rig.C.IsKillSuppressed(unchecked((int)twin)), "the same-named twin is the one that died");
        Check.True(rig.C.IsKillSuppressed(unchecked((int)moss)), "the Mosswart died");
        Check.True(!rig.C.IsKillSuppressed(unchecked((int)target)), "the live fight target is not suppressed");
        Check.Eq(rig.C.activeTargetId, unchecked((int)target), "and is still the target");
    }

    private static void FightTargetKill()
    {
        var rig = new Rig();
        uint target = rig.Mob("Olthoi Swarm Soldier", SkulkerWcid, 5, 0);
        uint twin = rig.Mob("Olthoi Swarm Soldier", SkulkerWcid, 7, 0);
        rig.Build(target);

        rig.C.HandleChatForDamage("You blast Olthoi Swarm Soldier for 100 points with Flame Ring VI.");
        rig.C.HandleChatForDamage("You blast Olthoi Swarm Soldier for 100 points with Flame Ring VI.");
        rig.Cache.OnUpdateHealth(target, 0f);   // ACE: the 0 comes with the lethal hit, before the notice
        rig.C.OnKillNotification("Olthoi Swarm Soldier's death is preceded by a sharp, stabbing pain!");

        Check.True(rig.C.IsKillSuppressed(unchecked((int)target)), "the fight target died and is suppressed");
        Check.Eq(rig.C.activeTargetId, 0, "and dropped");
        Check.True(!rig.C.IsKillSuppressed(unchecked((int)twin)), "the twin is untouched");
    }
}
