using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Wall spells (2026-10-08): a monster rule with Wall on casts its element's wall (picked by spell
// category, 229-235) when Wall Range is on and enough monsters are in the wall's path toward the
// target. Priority: Ring, then Wall, then Blast, then the base shape; a wall-only rule casts Bolt
// between walls. A wall is untargeted (ACE flies it along the caster's facing and never turns the
// caster for it), so the bot snaps its heading to the target first and holds the cast until the
// new heading has reached the server; already facing, it casts at once without turning. Walls are
// area casts for the damage bookkeeping. Old profiles and engine payloads load with walls off.
internal static class WallTests
{
    private const uint Player = 0x50000D01;
    private const int Target = 0x7000A001;

    public static void Register(Runner r)
    {
        r.Add("walls: Ring, then Wall, then Blast, then the base shape; wall-only casts Bolt between walls", ShapePriority);
        r.Add("walls: each element's wall by category, the tier limit, none for Nether", WallPerElement);
        r.Add("walls: Wall Range off, too few in the path, or no wall known: the rule's other shape", WallOverride);
        r.Add("walls: the path is a corridor toward the target, ahead of the caster", WallPathGeometry);
        r.Add("walls: far off the target, it turns first and casts after the settle; facing it, no turn", WallFacing);
        r.Add("walls: the cast itself waits for the turn, then goes as an area cast", WallCastThroughAttack);
        r.Add("walls: UseWall, Wall Range and Min Wall Targets load off / 0 / 3 from old files", OldSettings);
        r.Add("rings: a ring is due only when one is known; Void has none, so a Void caster casts its bolt (#rynth-issues 2026-10-23)", RingNeedsAKnownRing);
    }

    // A Void caster on DreamWeave with Use Ring on stood in the fight once 3+ monsters were near:
    // the ring override picked "Nether Ring", which doesn't exist, and nothing else was tried
    // ("No spell found: elem=Nether warTier=1 voidTier=7").
    private static void RingNeedsAKnownRing()
    {
        var rig = new Rig(7, "Nether Bolt VII", "Nether Streak VII", "Frost Bolt VI", "Halo of Frost", "Frost Ring II");
        rig.S.RingRange = 20; rig.S.MinRingTargets = 3; rig.S.WallRange = 0;
        int near = 4;
        rig.C.RingCountOverride = () => near;
        var boltRing = new MonsterRule { Name = "Gilded Shade", UseBolt = true, UseRing = true };

        Check.Eq(rig.Cast("Nether", boltRing), ("Nether Bolt VII", "Bolt"), "Void, 4 near (3 needed), no Void ring exists: Nether Bolt");
        var streakRing = new MonsterRule { Name = "Gilded Shade", UseBolt = false, UseStreak = true, UseRing = true };
        Check.Eq(rig.Cast("Nether", streakRing), ("Nether Streak VII", "Streak"), "Void, Streak + Ring: Nether Streak");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no Nether ring spell known")), "logged once");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("no Nether ring spell known")), 1, "only once");

        Check.Eq(rig.Cast("Cold", boltRing).Shape, "Ring", "War with a cold ring known: still the ring");
        near = 2;
        Check.Eq(rig.Cast("Cold", boltRing), ("Frost Bolt VI", "Bolt"), "too few near: Bolt");

        var noRing = new Rig(7, "Flame Bolt VI");
        noRing.S.RingRange = 20; noRing.S.MinRingTargets = 3; noRing.S.WallRange = 0;
        noRing.C.RingCountOverride = () => 5;
        Check.Eq(noRing.Cast("Fire", boltRing), ("Flame Bolt VI", "Bolt"), "War with no fire ring known: Flame Bolt, not nothing");
    }

    private static int Id(string name) => SpellTable.Named(name).Select(r => r.Id).First();
    private static string NameOf(int id) => id == 0 ? "(none)" : SpellTable.ById(id)?.Name ?? $"#{id}";

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public int InPath = 5;
        public DateTime Now = new(2026, 10, 8, 12, 0, 0);

        public Rig(int tier, params string[] known)
        {
            FakeHost.Reset();
            foreach (string n in known)
                foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
            int T(int k) => k <= tier ? 0 : 10000;   // the no-skills stub reads 250 buffed
            S.MinSkillLevelTier1 = 0; S.MinSkillLevelTier2 = T(2); S.MinSkillLevelTier3 = T(3); S.MinSkillLevelTier4 = T(4);
            S.MinSkillLevelTier5 = T(5); S.MinSkillLevelTier6 = T(6); S.MinSkillLevelTier7 = T(7); S.MinSkillLevelTier8 = T(8);
            S.RingRange = 0;
            S.WallRange = 20;
            S.MinWallTargets = 3;
            // The player at the origin, the target 10 yd north; the player faces north.
            FakeHost.PlayerPose = (0x0101001Bu, 0f, 0f, 0f);
            FakeHost.Positions[Player] = FakeHost.PlayerPose;
            FakeHost.Positions[(uint)Target] = (0x0101001Bu, 0f, 10f, 0f);
            FakeHost.Names[(uint)Target] = "Drudge Skulker";
            FakeHost.HeadingDeg = 0;
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { (uint)Target });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.RefreshKnownSpells();
            C.SetSpellManager(sm);
            C.WallPathCountOverride = () => InPath;
            C.WallClock = () => Now;
        }

        public (string Spell, string Shape) Cast(string element, MonsterRule rule)
        {
            int id = C.FindBestShapedSpell(element, rule, out _, out int shape, arcTargetId: Target);
            return (NameOf(id), CombatManager.ShapeName(shape));
        }
    }

    private static MonsterRule Wall(bool bolt = true) => new() { Name = "Drudge", UseBolt = bolt, UseWall = true };

    private static void ShapePriority()
    {
        MonsterRule R(bool ring = false, bool bolt = false, bool blast = false, bool wall = false, bool arc = false)
            => new() { UseArc = arc, UseRing = ring, UseBolt = bolt, UseBlast = blast, UseWall = wall };
        string N(int s) => CombatManager.ShapeName(s);
        int C(MonsterRule r, bool ringDue, bool wallDue, bool blastConditional = false, bool blastFanMet = false)
            => CombatManager.ChooseCastShape(r, blastConditional, blastFanMet, ringDue, wallDue, () => true, out _);

        var all = R(ring: true, bolt: true, blast: true, wall: true);
        Check.Eq(N(C(all, ringDue: true, wallDue: true, true, true)), "Ring", "all due: Ring");
        Check.Eq(N(C(all, ringDue: false, wallDue: true, true, true)), "Wall", "wall and blast due: Wall");
        Check.Eq(N(C(all, ringDue: false, wallDue: false, true, true)), "Blast", "only the blast due: Blast");
        Check.Eq(N(C(all, ringDue: false, wallDue: false, true, false)), "Bolt", "none due: the base shape (Bolt)");
        Check.Eq(N(C(R(wall: true), false, true)), "Wall", "wall-only, due: Wall");
        Check.Eq(N(C(R(wall: true), false, false)), "Bolt", "wall-only, not due: Bolt between walls (like ring-only)");
        Check.Eq(N(CombatManager.PickBaseShape(R(wall: true))), "Bolt", "wall-only base: Bolt");
        Check.Eq(N(CombatManager.PickBaseShape(R(wall: true, ring: true))), "Bolt", "ring + wall only: Bolt");
        Check.Eq(CombatManager.PickBaseShape(R()), -1, "nothing on: still no cast");

        int asked = 0;
        int s = CombatManager.ChooseCastShape(R(arc: true, wall: true), false, false, false, wallDue: true, () => { asked++; return true; }, out bool g);
        Check.Eq(N(s), "Wall", "Arc + Wall, wall due: Wall");
        Check.True(asked == 0 && !g, "a due wall skips the arc test");
        Check.Eq(N(CombatManager.ChooseCastShape(R(arc: true, wall: true), false, false, false, null, out _)), "Arc",
            "the old overload (no wall) is unchanged");
    }

    private static void WallPerElement()
    {
        var cases = new (string Element, string Wall)[]
        {
            ("Acid", "Blistering Creeper"), ("Bludgeon", "Hammering Crawler"), ("Cold", "Foon-Ki's Glacial Floe"),
            ("Lightning", "Os' Wall"), ("Fire", "Slithering Flames"), ("Pierce", "Spike Strafe"),
            ("Blade", "Bed of Blades"), ("Slash", "Bed of Blades"),
        };
        string[] retail = cases.Select(c => c.Wall).Distinct().ToArray();
        foreach (var (element, wall) in cases)
        {
            var rig = new Rig(7, retail);
            Check.Eq(NameOf(rig.C.FindBestWallSpell(element)), wall, $"{element}: {wall} (of all seven known)");
            Check.Eq(rig.Cast(element, Wall()), (wall, "Wall"), $"{element}: cast as the Wall shape");
        }
        var any = new Rig(7, retail);
        Check.Eq(any.C.FindBestWallSpell("Nether"), 0, "Nether: no walls");
        Check.Eq(any.Cast("Nether", Wall()).Shape, "?", "Nether, nothing else known: no wall cast");

        // Retail walls are power 200: level 5 by ACE's server formula, castable from tier 5.
        Check.Eq(SpellDatabase.LevelFromPower(SpellTable.ById(Id("Slithering Flames"))!.Power), 5, "Slithering Flames: level 5 (power 200)");
        Check.Eq(NameOf(new Rig(5, "Slithering Flames").C.FindBestWallSpell("Fire")), "Slithering Flames", "tier 5: the retail wall");
        Check.Eq(new Rig(4, "Slithering Flames").C.FindBestWallSpell("Fire"), 0, "tier 4: too hard (the same limit as Flame Bolt V)");

        // Several known in the category: the highest level the tier allows, then the power.
        var fire = new Rig(8, "Slithering Flames", "Burning Earth", "Flame Whip");
        Check.Eq(NameOf(fire.C.FindBestWallSpell("Fire")), "Burning Earth", "tier 8: Burning Earth (power 400, level 8)");
        var fire7 = new Rig(7, "Slithering Flames", "Burning Earth");
        Check.Eq(NameOf(fire7.C.FindBestWallSpell("Fire")), "Slithering Flames", "tier 7: not the level-8 one");
        Check.Eq(new Rig(7, "Flame Bolt VI").C.FindBestWallSpell("Fire"), 0, "no wall known: none");

        // The cold snapshot: never guess.
        FakeHost.Reset();
        var host = FakeHost.Create(Player);
        var c = new CombatManager(host, new LegacyUiSettings(), FakeHost.MakeCache(host, Player, Array.Empty<uint>()));
        var sm = new SpellManager(host, new LegacyUiSettings());
        sm.InitializeNatively();
        c.SetSpellManager(sm);
        Check.Eq(c.FindBestWallSpell("Fire"), 0, "spellbook snapshot cold: no wall");
    }

    private static void WallOverride()
    {
        string[] book = { "Foon-Ki's Glacial Floe", "Frost Bolt VI", "Frost Blast VI", "Frost Arc VI" };
        var rig = new Rig(7, book);
        Check.Eq(rig.Cast("Cold", Wall()), ("Foon-Ki's Glacial Floe", "Wall"), "5 in the path (3 needed): the wall");
        rig.InPath = 2;
        Check.Eq(rig.Cast("Cold", Wall()), ("Frost Bolt VI", "Bolt"), "2 in the path: Bolt");
        rig.InPath = 5;
        rig.S.WallRange = 0;
        Check.Eq(rig.Cast("Cold", Wall()), ("Frost Bolt VI", "Bolt"), "Wall Range 0: no walls");
        rig.S.WallRange = 20;
        Check.Eq(rig.Cast("Cold", new MonsterRule { UseArc = true, UseWall = true }).Shape, "Wall", "Arc + Wall, due: Wall");
        Check.Eq(rig.Cast("Cold", new MonsterRule { UseBlast = true, UseWall = true, UseBolt = false }).Shape, "Wall", "Blast + Wall, both due: Wall");
        rig.InPath = 1;
        Check.Eq(rig.Cast("Cold", new MonsterRule { UseBlast = true, UseWall = true, UseBolt = false }), ("Frost Blast VI", "Blast"),
            "Blast + Wall, wall not due: Blast");
        rig.InPath = 5;
        Check.Eq(rig.Cast("Cold", new MonsterRule { UseBolt = true }), ("Frost Bolt VI", "Bolt"), "Wall off in the rule: unchanged");
        Check.Eq(rig.C.FindBestShapedSpell("Cold", Wall(), out _, out int probeShape), Id("Frost Bolt VI"),
            "an element probe (no target) never picks a wall");
        Check.Eq(CombatManager.ShapeName(probeShape), "Bolt", "probe shape Bolt");

        var noWall = new Rig(7, "Frost Bolt VI");
        Check.Eq(noWall.Cast("Cold", Wall()), ("Frost Bolt VI", "Bolt"), "no cold wall known: Bolt (the wall isn't due)");

        Check.Eq(CombatManager.ChooseCastShape(new MonsterRule { UseRing = true, UseWall = true }, false, false, true, true, null, out _),
            CombatManager.ShapeRing, "ring and wall due: Ring");
    }

    private static void WallPathGeometry()
    {
        Check.True(CombatManager.InWallPath(10, 0), "the target itself");
        Check.True(CombatManager.InWallPath(15, 10), "15 yd, 10 deg off: 2.6 yd aside");
        Check.False(CombatManager.InWallPath(15, 25), "15 yd, 25 deg off: 6.3 yd aside, outside");
        Check.True(CombatManager.InWallPath(3, 60), "close, 60 deg off: 2.6 yd aside");
        Check.False(CombatManager.InWallPath(5, 120), "behind the caster: no");
        Check.True(CombatManager.InWallPath(5, -30), "the other side too");
        Check.Near(CombatManager.WallHalfWidth, 4.0, 1e-9, "half width 4 yd (5 projectiles across)");
    }

    private static void WallFacing()
    {
        var rig = new Rig(7, "Slithering Flames");
        // Facing the target (within 1 degree): cast at once, no turn.
        FakeHost.HeadingDeg = 0.5f;
        Check.False(rig.C.HoldForWallFacing(Target), "0.5 deg off: cast now");
        Check.Eq(FakeHost.Turns, 0, "no turn");

        // 90 degrees off: snap to the target, then hold for the settle.
        FakeHost.HeadingDeg = 90;
        Check.True(rig.C.HoldForWallFacing(Target), "90 deg off: hold");
        Check.Eq(FakeHost.Turns, 1, "one heading snap");
        Check.Near(FakeHost.HeadingDeg, 0, 0.5, "snapped to the target (north)");
        rig.Now = rig.Now.AddMilliseconds(500);
        Check.True(rig.C.HoldForWallFacing(Target), "500 ms later: still settling");
        rig.Now = rig.Now.AddMilliseconds(CombatManager.WallFaceSettleMs);
        Check.False(rig.C.HoldForWallFacing(Target), "after the settle: cast");
        Check.Eq(FakeHost.Turns, 1, "no second turn");

        // Turned, but the target walked far off during the settle: turn again.
        FakeHost.HeadingDeg = 180;
        Check.True(rig.C.HoldForWallFacing(Target), "180 deg off: hold");
        FakeHost.Positions[(uint)Target] = (0x0101001Bu, 10f, 0f, 0f);   // now due east
        rig.Now = rig.Now.AddMilliseconds(CombatManager.WallFaceSettleMs + 1);
        Check.True(rig.C.HoldForWallFacing(Target), "settled but 90 deg off now: turn again");
        Check.Eq(FakeHost.Turns, 3, "the second snap");
        Check.Near(FakeHost.HeadingDeg, 90, 0.5, "to the east");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatCast] wall: facing")), "the turn is logged");

        // The drift allowed after the settle shrinks with distance (1.8 m to the side).
        Check.Near(CombatManager.WallDriftAllowedDeg(64.0), 1.6, 0.1, "70 yd: about 1.6 degrees");
        Check.Near(CombatManager.WallDriftAllowedDeg(9.0), 5.0, 0.01, "10 yd: capped at 5 degrees");

        // Always turn: 6 degrees off is a snap too (a wall flies straight).
        var small = new Rig(7, "Slithering Flames");
        FakeHost.HeadingDeg = 6;
        Check.True(small.C.HoldForWallFacing(Target), "6 deg off: turn first");
        Check.Eq(FakeHost.Turns, 1, "one snap");

        // A target that keeps moving: at most WallFaceMaxSnaps turns, then the wall goes anyway.
        var chase = new Rig(7, "Slithering Flames");
        FakeHost.HeadingDeg = 180;
        Check.True(chase.C.HoldForWallFacing(Target), "first snap");
        float east = 10f;
        for (int i = 1; i < CombatManager.WallFaceMaxSnaps; i++)
        {
            FakeHost.HeadingDeg = 180;   // keeps facing away (the target ran round behind)
            chase.Now = chase.Now.AddMilliseconds(CombatManager.WallFaceSettleMs + 1);
            Check.True(chase.C.HoldForWallFacing(Target), $"snap {i + 1}");
        }
        FakeHost.HeadingDeg = 180;
        chase.Now = chase.Now.AddMilliseconds(CombatManager.WallFaceSettleMs + 1);
        Check.False(chase.C.HoldForWallFacing(Target), "after the last snap: cast anyway");
        _ = east;
    }

    private static void WallCastThroughAttack()
    {
        var rig = new Rig(7, "Slithering Flames", "Flame Bolt VI");
        rig.S.MonsterRules.Clear();
        rig.S.MonsterRules.Add(new MonsterRule { Name = "Drudge", DamageType = "Fire", UseBolt = true, UseWall = true });
        rig.C.activeTargetId = Target;
        var target = new WorldObject(Target, "Drudge Skulker");
        MethodInfo attack = typeof(CombatManager).GetMethod("AttackWithMagic", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo held = typeof(CombatManager).GetField("_wallFaceHeld", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo tier = typeof(CombatManager).GetField("_lastCastTier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo area = typeof(CombatManager).GetField("_lastCastWasRing", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo resolving = typeof(CombatManager).GetField("_awaitingCastResolution", BindingFlags.NonPublic | BindingFlags.Instance)!;

        FakeHost.HeadingDeg = 270;   // facing west, the target is north
        attack.Invoke(rig.C, new object[] { target });
        Check.Eq(FakeHost.Casts.Count, 0, "far off: no cast yet");
        Check.True((bool)held.GetValue(rig.C)!, "the wall is held for its turn");
        Check.Eq(FakeHost.Turns, 1, "turned first");

        rig.Now = rig.Now.AddMilliseconds(300);
        attack.Invoke(rig.C, new object[] { target });
        Check.Eq(FakeHost.Casts.Count, 0, "300 ms later: still no cast (the server catching up)");

        rig.Now = rig.Now.AddMilliseconds(CombatManager.WallFaceSettleMs);
        attack.Invoke(rig.C, new object[] { target });
        Check.Eq(FakeHost.Casts.Count, 1, "after the settle: cast");
        Check.Eq(NameOf(FakeHost.Casts[0].SpellId), "Slithering Flames", "the wall");
        Check.False((bool)held.GetValue(rig.C)!, "not held");
        Check.Eq(FakeHost.Turns, 1, "no turn with the cast");
        Check.True((bool)area.GetValue(rig.C)!, "an area cast for the bookkeeping (like a ring)");
        Check.Eq((int)tier.GetValue(rig.C)!, -5, "filed with the area tiers: level 5, negative");

        // Already facing: straight to the cast, no turn. (A fresh rig: no cast resolving.)
        var facing = new Rig(7, "Slithering Flames", "Flame Bolt VI");
        facing.S.MonsterRules.Add(new MonsterRule { Name = "Drudge", DamageType = "Fire", UseBolt = true, UseWall = true });
        facing.C.activeTargetId = Target;
        FakeHost.HeadingDeg = 0;
        attack.Invoke(facing.C, new object[] { target });
        Check.Eq(FakeHost.Casts.Count, 1, "facing: cast at once");
        Check.Eq(NameOf(FakeHost.Casts[0].SpellId), "Slithering Flames", "the wall");
        Check.Eq(FakeHost.Turns, 0, "no turn");
        Check.True((bool)resolving.GetValue(facing.C)!, "the cast arms the cadence guard (no turn until it resolves)");

        // A bolt (wall not due) is single-target as before.
        var bolt = new Rig(7, "Slithering Flames", "Flame Bolt VI");
        bolt.S.MonsterRules.Add(new MonsterRule { Name = "Drudge", DamageType = "Fire", UseBolt = true, UseWall = true });
        bolt.C.activeTargetId = Target;
        bolt.InPath = 1;
        FakeHost.HeadingDeg = 270;
        attack.Invoke(bolt.C, new object[] { target });
        Check.Eq(NameOf(FakeHost.Casts.Single().SpellId), "Flame Bolt VI", "wall not due: the bolt, no turn needed");
        Check.Eq(FakeHost.Turns, 0, "targeted casts don't turn (the server does)");
        Check.False((bool)area.GetValue(bolt.C)!, "single-target");
        Check.Eq((int)tier.GetValue(bolt.C)!, 6, "positive tier");
    }

    private static void OldSettings()
    {
        var old = JsonSerializer.Deserialize("{\"MinRingTargets\":4,\"BlastRange\":10}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.Eq(old.WallRange, 0, "old profile: Wall Range off");
        Check.Eq(old.MinWallTargets, 3, "old profile: Min Wall Targets 3");
        var fresh = new LegacyUiSettings();
        Check.True(fresh.WallRange == 0 && fresh.MinWallTargets == 3, "new settings: off / 3");

        var bridge = JsonSerializer.Deserialize("{\"MinRingTargets\":4}", RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(bridge.WallRange == null && bridge.MinWallTargets == null, "older engine's settings: no wall values (left alone)");
        var newer = JsonSerializer.Deserialize("{\"WallRange\":15,\"MinWallTargets\":4}", RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(newer.WallRange == 15 && newer.MinWallTargets == 4, "an engine with walls: carried");

        const string oldRules = "[{\"Name\":\"Default\",\"Priority\":1,\"UseBolt\":true},{\"Name\":\"Drudge\",\"UseRing\":true,\"UseBlast\":true}]";
        var rules = JsonSerializer.Deserialize(oldRules, RynthAiJsonContext.Default.MonsterRuleList)!;
        Check.False(rules[0].UseWall || rules[1].UseWall, "old monster file: Wall off");
        rules[1].UseWall = true;
        string json = JsonSerializer.Serialize(rules, RynthAiJsonContext.Default.MonsterRuleList);
        Check.True(json.Contains("\"UseWall\": true"), "saved with UseWall");
        var back = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonsterRuleList)!;
        Check.True(back[1].UseWall && !back[0].UseWall, "round trip keeps it");
        var payload = JsonSerializer.Deserialize("{\"rules\":[{\"Name\":\"Mite\",\"UseWall\":true}]}", RynthAiJsonContext.Default.MonstersBridgePayload)!;
        Check.True(payload.Rules[0].UseWall, "the engine's rules JSON carries UseWall");
    }
}
