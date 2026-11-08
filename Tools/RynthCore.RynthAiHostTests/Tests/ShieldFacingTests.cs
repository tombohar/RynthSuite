using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Damage taken by shield and facing (2026-11-08): does a shield actually cut a boss's hits? ACE
// counts a shield only in a combat stance and only when the attacker is within 90 degrees of the
// defender's facing (Creature_Combat.GetShieldMod, effectiveAngle 180). Each monster hit line is
// learned split by both, saved as TS rows in monster_damage.txt, and sent with the Damage panel row.
internal static class ShieldFacingTests
{
    private const uint Player = 0x50000C01;
    private const uint Boss = 0x80001001, BossTwin = 0x80001002;
    private const uint BossWcid = 70001;
    private const uint Sword = 0x80000401, Shield = 0x80000404, Dagger = 0x80000402;
    private const int MeleeSlot = 0x00100000, ShieldSlot = 0x00200000;
    private const string BossName = "Hive Boss";
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("shield facing: in front within 90 degrees either side, behind past it, at every heading", FacingMath);
        r.Add("shield facing: unknown when nothing can be measured (on top, no frame, too far, NaN)", FacingUnknown);
        r.Add("shield facing: the compass heading comes from the pose quaternion (not GetPlayerHeading)", HeadingFromQuaternion);
        r.Add("shield facing: a monster's hit line is split by shield and where it stood", HitLinesSplit);
        r.Add("shield facing: peace, a dual-wield off hand, an unknown off-hand item, two of the name that disagree", HitLinesEdgeCases);
        r.Add("shield facing: TS rows save and load (tier rows too), old files load, unknown kinds are skipped", SaveLoadRoundTrip);
        r.Add("shield facing: the [Taken] summary is one line per monster, not one per hit", SummaryLine);
        r.Add("shield facing: the Damage panel row's mstats carries the split, even with no session numbers", DamageRowJson);
    }

    // ── The math ─────────────────────────────────────────────────────────────

    /// <summary>The point <paramref name="dist"/> m away at compass bearing <paramref name="bearing"/> (0 = North, clockwise).</summary>
    private static (double X, double Y) At(double bearing, double dist)
    {
        double r = bearing * Math.PI / 180.0;
        return (Math.Sin(r) * dist, Math.Cos(r) * dist);
    }

    private static HitFacing F(double heading, double relBearing, double dist = 5)
    {
        var (x, y) = At(heading + relBearing, dist);
        return ShieldFacing.Classify(heading, 0, 0, x, y);
    }

    private static void FacingMath()
    {
        foreach (double h in new[] { 0.0, 45.0, 90.0, 135.0, 180.0, 225.0, 270.0, 315.0, 359.0, 1.0 })
        {
            Check.Eq(F(h, 0), HitFacing.Front, $"heading {h}: straight ahead is in front");
            Check.Eq(F(h, 180), HitFacing.Behind, $"heading {h}: straight behind");
            Check.Eq(F(h, 45), HitFacing.Front, $"heading {h}: 45 right is in front");
            Check.Eq(F(h, -45), HitFacing.Front, $"heading {h}: 45 left is in front");
            Check.Eq(F(h, 89), HitFacing.Front, $"heading {h}: 89 right is in front");
            Check.Eq(F(h, -89), HitFacing.Front, $"heading {h}: 89 left is in front");
            Check.Eq(F(h, 91), HitFacing.Behind, $"heading {h}: 91 right is behind");
            Check.Eq(F(h, -91), HitFacing.Behind, $"heading {h}: 91 left is behind");
            Check.Eq(F(h, 135), HitFacing.Behind, $"heading {h}: back right");
            Check.Eq(F(h, -135), HitFacing.Behind, $"heading {h}: back left");
        }
        // Exactly at the side: ACE's test is |angle| > 90 for no shield, so 90 still counts.
        Check.Eq(ShieldFacing.Classify(0, 0, 0, 5, 0), HitFacing.Front, "facing north, the attacker due east (90): in front");
        Check.Eq(ShieldFacing.Classify(0, 0, 0, -5, 0), HitFacing.Front, "due west (-90): in front");
        Check.Eq(ShieldFacing.Classify(90, 0, 0, 0, -5), HitFacing.Front, "facing east, due south: in front (side)");
        Check.Eq(ShieldFacing.Classify(90, 0, 0, -5, 0.01), HitFacing.Behind, "facing east, due west: behind");
        // The compass basis: facing north, a monster to the north-east is in front, to the south-west behind.
        Check.Eq(ShieldFacing.Classify(0, 10, 10, 13, 13), HitFacing.Front, "north-east of us, facing north");
        Check.Eq(ShieldFacing.Classify(0, 10, 10, 7, 7), HitFacing.Behind, "south-west of us, facing north");
        Check.Eq(ShieldFacing.Classify(270, 10, 10, 5, 10), HitFacing.Front, "facing west, a monster west");
        Check.Eq(ShieldFacing.Classify(270, 10, 10, 15, 10), HitFacing.Behind, "facing west, a monster east");
        Check.Eq(Math.Round(ShieldFacing.AngleTo(350, 1, 10), 0), 16.0, "angle wraps past north (350 -> 5.7)");

        // Landblock-local positions: across an outdoor landblock edge they are one frame.
        Check.Eq(ShieldFacing.Classify(90, 0xA9B40001, 191f, 50f, 0xAAB40001, 1f, 50f), HitFacing.Front,
            "outdoors, the attacker 2 m east in the next landblock, facing east");
        Check.Eq(ShieldFacing.Classify(270, 0xA9B40001, 191f, 50f, 0xAAB40001, 1f, 50f), HitFacing.Behind,
            "the same, facing west");
        Check.Eq(ShieldFacing.Classify(0, 0x01D90105, 10f, 10f, 0x01D9010A, 10f, 20f), HitFacing.Front,
            "in a dungeon, two cells of one landblock share its frame");

        // Several of the name: they must all agree.
        Check.Eq(ShieldFacing.Agree(new[] { HitFacing.Front, HitFacing.Front }), HitFacing.Front, "all in front: front");
        Check.Eq(ShieldFacing.Agree(new[] { HitFacing.Front, HitFacing.Behind }), HitFacing.Unknown, "one each side: unknown");
        Check.Eq(ShieldFacing.Agree(new[] { HitFacing.Behind, HitFacing.Unknown }), HitFacing.Unknown, "one not measurable: unknown");
        Check.Eq(ShieldFacing.Agree(Array.Empty<HitFacing>()), HitFacing.Unknown, "none: unknown");
    }

    private static void FacingUnknown()
    {
        Check.Eq(ShieldFacing.Classify(0, 10, 10, 10, 10), HitFacing.Unknown, "on top of us: no direction");
        Check.Eq(ShieldFacing.Classify(double.NaN, 0, 0, 5, 5), HitFacing.Unknown, "no heading");
        Check.Eq(ShieldFacing.Classify(0, 0, 0, double.NaN, 5), HitFacing.Unknown, "a garbage position");
        Check.Eq(ShieldFacing.Classify(0, 0x01D90105, 10f, 10f, 0x01DA0105, 10f, 20f), HitFacing.Unknown,
            "two dungeon landblocks: no shared frame");
        Check.Eq(ShieldFacing.Classify(0, 0xA9B40001, 10f, 10f, 0xA9B40001, 10f, 100f), HitFacing.Unknown,
            "90 m away: too far to be the one that hit");
        Check.Eq(ShieldFacing.Classify(0, 0xA9B40001, 10f, 10f, 0xA9B40001, 10f, 80f), HitFacing.Front, "70 m: measured");
        Check.True(ShieldFacing.TryParse("behind", out var b) && b == HitFacing.Behind, "parse behind");
        Check.False(ShieldFacing.TryParse("sideways", out _), "an unknown word does not parse");
    }

    private static void HeadingFromQuaternion()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (0xA9B40001, 10f, 10f, 0f);
        var host = FakeHost.Create(Player);
        foreach (double h in new[] { 0.0, 13.9, 90.0, 180.0, 270.0, 300.0 })
        {
            FakeHost.HeadingDeg = h;
            Check.True(PlayerCompass.TryGet(host, out float c), "pose read");
            Check.Eq(Math.Round(c, 1), Math.Round(h, 1), $"compass {h} from the quaternion");
            double half = -h * Math.PI / 360.0;
            Check.Eq(Math.Round(ShieldFacing.CompassFromQuaternion(Math.Cos(half), Math.Sin(half)), 1), Math.Round(h % 360.0, 1),
                $"ShieldFacing reads {h} the same way");
        }
    }

    // ── The rig: a boss hits us ──────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public readonly MonsterDamageStore Store = new();

        public Rig(bool shieldOn, double heading, (float X, float Y) boss, (float X, float Y)? twin = null, bool offhandUnknown = false,
                   bool dagger = false, int mode = CombatMode.Melee)
        {
            FakeHost.Reset();
            FakeHost.EquippedCalls = true;                 // the engine's EquipmentTracker (v87)
            FakeHost.WeaponCalls = true;                   // GetCurrentCombatMode
            FakeHost.CombatModeValue = mode;
            FakeHost.PlayerPose = (0xA9B40001, 50f, 50f, 0f);
            FakeHost.HeadingDeg = heading;
            Item(Sword, "Long Sword", 0x1, MeleeSlot, 1);
            Item(Shield, "Kite Shield", 0x2, ShieldSlot, 4);
            Item(Dagger, "Dagger", 0x1, MeleeSlot, 1);
            FakeHost.Equipped[Sword] = (uint)MeleeSlot;
            if (shieldOn) FakeHost.Equipped[offhandUnknown ? 0x80000999u : Shield] = (uint)ShieldSlot;
            if (dagger) FakeHost.Equipped[Dagger] = (uint)ShieldSlot;
            Monster(Boss, boss);
            var ids = new List<uint> { Sword, Shield, Dagger, Boss };
            var health = new Dictionary<uint, float> { [Boss] = 1f };
            if (twin is { } t) { Monster(BossTwin, t); ids.Add(BossTwin); health[BossTwin] = 1f; }
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, ids, health);
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            string folder = Path.Combine(Program.TempRoot, "shield-facing", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            Store.SetCharacter(folder);
            C.SetDamageStores(null, Store);
            C.OffhandReadMs = 0;
            EquippedState.Invalidate();
            var seen = (Dictionary<string, uint>)typeof(CombatManager).GetField("_seenMonsterNameToWcid", Any)!.GetValue(C)!;
            seen[BossName] = BossWcid;   // what the scan learns when it sees the boss
        }

        private static void Item(uint id, string name, uint itemType, int validSlots, int combatUse)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = itemType;
            FakeHost.Ints[(id, 9)] = validSlots;
            FakeHost.Ints[(id, 51)] = combatUse;
        }

        private static void Monster(uint id, (float X, float Y) at)
        {
            FakeHost.Names[id] = BossName;
            FakeHost.ItemTypes[id] = 0x10;
            FakeHost.Wcids[id] = BossWcid;
            FakeHost.Positions[id] = (0xA9B40001, at.X, at.Y, 0f);
        }

        public void Hit(int amount) => C.HandleChatForDamage($"{BossName} slashes your upper leg for {amount} points of slashing damage!");

        public Dictionary<(bool, HitFacing), MonsterDamageStore.ShieldTakenRow> Split()
            => Store.GetShieldTaken(BossWcid, 0).ToDictionary(r => (r.Shield, r.Facing));
    }

    private static void HitLinesSplit()
    {
        // Facing north, shield up, the boss 2 m north: in front, the shield counts.
        var rig = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f));
        rig.Hit(10);
        rig.Hit(14);
        var s = rig.Split();
        Check.True(s.TryGetValue((true, HitFacing.Front), out var sf), "shield + front recorded");
        Check.Eq(sf.Hits, 2, "two hits");
        Check.Eq(sf.Avg, 12.0, "their mean");
        Check.Eq(sf.Max, 14.0, "the biggest");
        Check.Eq(s.Count, 1, "nothing else");
        var t = rig.Store.GetDetail(BossWcid, 0).Taken;
        Check.True(t.Any(x => x.Element == "Slash" && x.Hits == 2), "the T row (per element) still learns the hits");

        // It walks behind us.
        FakeHost.Positions[Boss] = (0xA9B40001, 50f, 48f, 0f);
        rig.Hit(60);
        Check.Eq(rig.Split()[(true, HitFacing.Behind)].Hits, 1, "shield + behind");

        // We turn round to face it: in front again.
        FakeHost.HeadingDeg = 180;
        rig.Hit(20);
        Check.Eq(rig.Split()[(true, HitFacing.Front)].Hits, 3, "turned to face it: in front");

        // No shield in the off hand.
        var bare = new Rig(shieldOn: false, heading: 90, boss: (53f, 50f));
        bare.Hit(54);
        bare.Hit(54);
        var b = bare.Split();
        Check.Eq(b[(false, HitFacing.Front)].Hits, 2, "no shield, in front");
        Check.False(b.ContainsKey((true, HitFacing.Front)), "and no shield cell");
        FakeHost.Positions[Boss] = (0xA9B40001, 47f, 50f, 0f);
        bare.Hit(70);
        Check.Eq(bare.Split()[(false, HitFacing.Behind)].Max, 70.0, "no shield, behind");

        // A line for a monster the bot never saw (no wcid) learns nothing.
        bare.C.HandleChatForDamage("Some Stranger slashes your upper leg for 9 points of slashing damage!");
        Check.Eq(bare.Split().Values.Sum(x => x.Hits), 3, "an unknown monster's line is not counted");
    }

    private static void HitLinesEdgeCases()
    {
        var peace = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f), mode: CombatMode.NonCombat);
        peace.Hit(30);
        Check.True(peace.Split().ContainsKey((false, HitFacing.Front)), "in peace ACE ignores the shield: shield 0");

        var dw = new Rig(shieldOn: false, heading: 0, boss: (50f, 52f), dagger: true);
        dw.Hit(30);
        Check.True(dw.Split().ContainsKey((false, HitFacing.Front)), "a dagger in the off hand is no shield");

        var unknown = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f), offhandUnknown: true);
        unknown.Hit(30);
        Check.Eq(unknown.Split().Count, 0, "an off-hand item the cache doesn't know: not split (no guess)");
        Check.True(unknown.Store.GetDetail(BossWcid, 0).Taken.Any(x => x.Hits == 1), "the T row still learns it");

        var twins = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f), twin: (50f, 47f));
        twins.Hit(30);
        Check.True(twins.Split().ContainsKey((true, HitFacing.Unknown)), "two of the name, one each side: unknown");

        var agree = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f), twin: (52f, 53f));
        agree.Hit(30);
        Check.True(agree.Split().ContainsKey((true, HitFacing.Front)), "two of the name, both in front: front");

        var far = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f), twin: (50f, 0f));
        FakeHost.Positions[BossTwin] = (0xA9B40001, 50f, -60f, 0f);   // 110 m behind: not the one that hit
        far.Hit(30);
        Check.True(far.Split().ContainsKey((true, HitFacing.Front)), "one of the name 110 m away is left out");

        var dead = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f), twin: (50f, 47f));
        var cache = (WorldObjectCache)typeof(CombatManager).GetField("_worldFilter", Any)!.GetValue(dead.C)!;
        cache.OnUpdateHealth(BossTwin, 0f);
        dead.Hit(30);
        Check.True(dead.Split().ContainsKey((true, HitFacing.Front)), "a dead one of the name is left out");

        var noPose = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f));
        FakeHost.HasPose = false;
        noPose.Hit(30);
        Check.True(noPose.Split().ContainsKey((true, HitFacing.Unknown)), "no pose: unknown");
    }

    // ── Save and load ────────────────────────────────────────────────────────

    private static string Folder(string name)
    {
        string folder = Path.Combine(Program.TempRoot, "shield-facing-file", name);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void SaveLoadRoundTrip()
    {
        string folder = Folder("roundtrip");
        var s = new MonsterDamageStore();
        s.SetCharacter(folder);
        s.RecordTaken(BossWcid, BossName, "Slash", 40);
        s.RecordTakenSplit(BossWcid, BossName, true, HitFacing.Front, 10);
        s.RecordTakenSplit(BossWcid, BossName, true, HitFacing.Front, 13);
        s.RecordTakenSplit(BossWcid, BossName, true, HitFacing.Behind, 274);
        s.RecordTakenSplit(BossWcid, BossName, false, HitFacing.Unknown, 54);
        s.Difficulty = 3;
        s.RecordTakenSplit(BossWcid, BossName, false, HitFacing.Front, 90);
        s.Difficulty = 0;
        s.SaveIfDirty();

        string file = Path.Combine(folder, "monster_damage.txt");
        string[] lines = File.ReadAllLines(file);
        Check.True(lines.Any(l => l.StartsWith("# TS|wcid|name|shield|facing|hitsTaken|avgDamageTaken|maxDamageTaken")), "the header names the TS row");
        Check.True(lines.Any(l => l.StartsWith("# ATS|tier|wcid|name|shield|facing")), "and the tier row");
        Check.True(lines.Contains($"TS|{BossWcid}|{BossName}|1|front|2|11.5|13"), "shield front: 2 hits, mean 11.5, max 13");
        Check.True(lines.Contains($"TS|{BossWcid}|{BossName}|1|behind|1|274|274"), "shield behind");
        Check.True(lines.Contains($"TS|{BossWcid}|{BossName}|0|unknown|1|54|54"), "no shield, facing unknown");
        Check.True(lines.Contains($"ATS|3|{BossWcid}|{BossName}|0|front|1|90|90"), "a difficulty tier's row");
        int ts = Array.FindIndex(lines, l => l.StartsWith("TS|"));
        Check.True(ts > Array.FindIndex(lines, l => l.StartsWith("T|")), "TS rows follow the T rows");

        var again = new MonsterDamageStore();
        again.SetCharacter(folder);
        var rows = again.GetShieldTaken(BossWcid, 0);
        Check.Eq(string.Join(";", rows.Select(r => $"{(r.Shield ? 1 : 0)}{ShieldFacing.Word(r.Facing)}{r.Hits}/{r.Avg}/{r.Max}")),
            "1front2/11.5/13;1behind1/274/274;0unknown1/54/54", "loaded back the same, shield first, front before behind");
        Check.Eq(again.GetShieldTaken(BossWcid, 3).Single().Avg, 90.0, "the tier's row loaded");
        Check.Eq(again.GetDetail(BossWcid, 0).Taken.Single().Hits, 1, "the T row loaded beside them");
        again.RecordTakenSplit(BossWcid, BossName, true, HitFacing.Front, 12.5);
        Check.Eq(Math.Round(again.GetShieldTaken(BossWcid, 0)[0].Avg, 2), 11.83, "a loaded mean goes on being a mean ((10+13+12.5)/3)");

        // An older file (no TS rows), with unknown row kinds a newer RynthAi might write: they load, the
        // unknown kinds are skipped, and nothing breaks.
        string old = Folder("old");
        File.WriteAllLines(Path.Combine(old, "monster_damage.txt"), new[]
        {
            "# RynthAi per-character monster damage learning (auto-generated).",
            $"H|{BossWcid}|{BossName}|5000|2",
            $"T|{BossWcid}|{BossName}|Slash|7|55|274",
            $"TX|{BossWcid}|{BossName}|future|1|2|3|4",
            $"TS|{BossWcid}|{BossName}|2|front|1|2|3",
            $"TS|{BossWcid}|{BossName}|1|sideways|1|2|3",
            $"TS|{BossWcid}|{BossName}|1|front|zero|2|3",
            $"TS|{BossWcid}|{BossName}|1",
            $"AZZ|2|{BossWcid}|{BossName}|1|2",
            $"D|{BossWcid}|{BossName}|0|Fire|7|120|20|3.5|4|200|4|100|16",
        });
        var o = new MonsterDamageStore();
        o.SetCharacter(old);
        Check.Eq(o.GetLearnedHp(BossWcid), 5000.0, "the H row loads");
        Check.Eq(o.GetDetail(BossWcid, 0).Taken.Single().Max, 274.0, "the T row loads");
        Check.Eq(o.Snapshot().Count, 1, "the D row loads");
        Check.Eq(o.GetShieldTaken(BossWcid, 0).Count, 0, "unknown kinds and bad TS rows are skipped");

        // What an older RynthAi does with a TS row: its loader compares the kind exactly (H, M, W, O,
        // P, U, PK, T, D, DEF, and A* tier rows), so "TS" matches none and is skipped; this loader
        // does the same with a kind it doesn't know (TX above).
        Check.True(lines.All(l => !l.StartsWith("TS") || l.StartsWith("TS|")), "TS is its own kind, never a T row");
    }

    private static void SummaryLine()
    {
        var rig = new Rig(shieldOn: true, heading: 0, boss: (50f, 52f));
        for (int i = 0; i < 5; i++) rig.Hit(10 + i);
        FakeHost.Positions[Boss] = (0xA9B40001, 50f, 48f, 0f);
        rig.Hit(274);
        Check.False(FakeHost.Logs.Any(l => l.StartsWith("[Taken]")), "no line per hit");
        List<string> lines = rig.C.TakenSummaryLines(RynthClock.Now);
        Check.Eq(lines.Count, 1, "one line for the one monster");
        string line = lines[0];
        Check.True(line.StartsWith($"[Taken] {BossName}: 6 hits in "), "who and how many: " + line);
        Check.True(line.Contains("shield front 5 avg 12 max 14"), "the front cell: " + line);
        Check.True(line.Contains("shield behind 1 avg 274 max 274"), "the behind cell: " + line);
        Check.Eq(rig.C.TakenSummaryLines(RynthClock.Now).Count, 0, "the tally starts over");
    }

    // ── The Damage panel row ─────────────────────────────────────────────────

    private static void DamageRowJson()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (0xA9B40001, 10f, 10f, 0f);
        var host = FakeHost.Create(Player);
        var p = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(p, v);
        Set("_objectCache", cache);
        Set("_playerId", Player);
        var store = new MonsterDamageStore();
        store.SetCharacter(Folder("row"));
        Set("_damageStore", store);
        Set("_creatureStore", new CreatureProfileStore());

        store.RecordKill(0, BossWcid, BossName, "Fire", 7, 3, 5000);
        for (int i = 0; i < 19; i++) store.RecordTakenSplit(BossWcid, BossName, true, HitFacing.Front, 11);
        store.RecordTakenSplit(BossWcid, BossName, true, HitFacing.Behind, 274);
        store.RecordTakenSplit(BossWcid, BossName, false, HitFacing.Front, 54);
        store.RecordTakenSplit(BossWcid, BossName, false, HitFacing.Front, 55);

        using var doc = JsonDocument.Parse(p.BuildMonsterDamageJson());
        JsonElement row = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == BossName);
        Check.True(row.TryGetProperty("mstats", out JsonElement m), "mstats is sent with only learned numbers (none since login)");
        Check.Eq(m.GetProperty("hsf").GetInt32(), 19, "shield front hits");
        Check.Eq(m.GetProperty("dsf").GetInt64(), 209L, "shield front total damage (19 x 11)");
        Check.Eq(m.GetProperty("msf").GetInt64(), 11L, "shield front biggest");
        Check.Eq(m.GetProperty("hsb").GetInt32(), 1, "shield behind hits");
        Check.Eq(m.GetProperty("msb").GetInt64(), 274L, "shield behind biggest");
        Check.Eq(m.GetProperty("hnf").GetInt32(), 2, "no shield front hits");
        Check.Eq(m.GetProperty("dnf").GetInt64(), 109L, "no shield front total");
        Check.False(m.TryGetProperty("hsu", out _), "an empty cell isn't sent");
        Check.False(m.TryGetProperty("sw", out _), "no session numbers: none sent");

        // With session numbers too: both, one object.
        string both = RynthAiPlugin.MonsterStatsJson(null, p.ShieldTakenFor(new[] { BossWcid }, 0));
        Check.True(both.StartsWith("\"mstats\":{\"hsf\":19,\"dsf\":209,\"msf\":11"), "the split alone: " + both);
        Check.Eq(p.MonsterStatsJsonFor("Nobody", new uint[] { 12345 }, 0), "", "a monster with nothing: no mstats");
    }
}
