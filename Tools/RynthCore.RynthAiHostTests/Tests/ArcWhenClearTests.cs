using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Arc when clear (2026-10-04): a rule with Arc on casts an Arc only when the arc's flight path
// to the target is clear; otherwise the rule's other shape (Streak, an unconditional Blast,
// Bolt), and an Arc-only rule casts Bolt. Ring and Blast overrides keep their precedence and
// skip the arc test. The verdict is kept 300 ms per target. The ray test itself is proven
// against the dats in Tools/RynthCore.LosProof; here it is replaced by ArcBlockedOverride.
internal static class ArcWhenClearTests
{
    private const uint Player = 0x50000D01;
    private const int Target = 0x7000A001;

    public static void Register(Runner r)
    {
        r.Add("arc when clear: clear = Arc, blocked = the other shape, Arc only = Bolt, unknown = Arc", ShapeChoice);
        r.Add("arc when clear: ring and blast overrides first, and they skip the arc test", OverridePrecedence);
        r.Add("arc when clear: the spell cast (Frost Arc / Streak / Bolt), never an arc into the obstacle", SpellsCast);
        r.Add("arc when clear: the verdict is worked out once per 300 ms per target", VerdictCache);
        r.Add("arc when clear: Magic Arc speed is ACE's 40 m/s; the old default 25 migrates", MagicArcSpeedSetting);
    }

    private static MonsterRule R(bool arc = false, bool ring = false, bool streak = false, bool bolt = false, bool blast = false)
        => new() { Name = "Mob", UseArc = arc, UseRing = ring, UseStreak = streak, UseBolt = bolt, UseBlast = blast };

    private static int Choose(MonsterRule rule, bool? blocked, out bool gaveWay, bool blastConditional = false,
        bool blastFanMet = false, bool ringDue = false) =>
        CombatManager.ChooseCastShape(rule, blastConditional, blastFanMet, ringDue, () => blocked, out gaveWay);

    private static string N(int shape) => CombatManager.ShapeName(shape);

    private static void ShapeChoice()
    {
        Check.Eq(N(Choose(R(arc: true, bolt: true), false, out bool g)), "Arc", "Arc + Bolt, clear: Arc");
        Check.False(g, "clear: the arc didn't give way");
        Check.Eq(N(Choose(R(arc: true, bolt: true), true, out g)), "Bolt", "Arc + Bolt, blocked: Bolt");
        Check.True(g, "blocked: the arc gave way");
        Check.Eq(N(Choose(R(arc: true, streak: true), true, out _)), "Streak", "Arc + Streak, blocked: Streak");
        Check.Eq(N(Choose(R(arc: true, streak: true, bolt: true), true, out _)), "Streak", "Arc + Streak + Bolt, blocked: Streak (as the base order)");
        Check.Eq(N(Choose(R(arc: true, blast: true), true, out _)), "Blast", "Arc + Blast (Blast Range off), blocked: Blast");
        Check.Eq(N(Choose(R(arc: true, blast: true, bolt: true), true, out _)), "Blast", "Arc + Blast + Bolt, blocked: Blast before Bolt");
        Check.Eq(N(Choose(R(arc: true), true, out g)), "Bolt", "Arc only, blocked: Bolt (like ring-only between rings)");
        Check.True(g, "Arc only, blocked: gave way");
        Check.Eq(N(Choose(R(arc: true), false, out _)), "Arc", "Arc only, clear: Arc");
        Check.Eq(N(Choose(R(arc: true, bolt: true), null, out g)), "Arc", "can't tell (raycasting off): Arc as before");
        Check.False(g, "can't tell: no give-way");
        Check.Eq(N(CombatManager.ChooseCastShape(R(arc: true, bolt: true), false, false, false, null, out _)), "Arc",
            "no arc test asked for (element probes): Arc");
        Check.Eq(Choose(R(), true, out _), -1, "nothing on: no cast, as before");
        Check.Eq(N(CombatManager.ArcFallbackShape(null, false)), "Bolt", "no rule: Bolt");
        Check.Eq(N(CombatManager.ArcFallbackShape(R(arc: true, blast: true), blastConditional: true)), "Bolt",
            "conditional Blast is no fallback (it's an override): Bolt");
    }

    private static void OverridePrecedence()
    {
        int asked = 0;
        bool? Blocked() { asked++; return true; }

        int s = CombatManager.ChooseCastShape(R(arc: true, ring: true, bolt: true), false, false, ringDue: true, Blocked, out bool g);
        Check.Eq(N(s), "Ring", "ring count met: Ring");
        Check.Eq(asked, 0, "ring: the arc isn't tested");
        Check.False(g, "ring: no give-way");

        s = CombatManager.ChooseCastShape(R(arc: true, ring: true, bolt: true), false, false, ringDue: false, Blocked, out _);
        Check.Eq(N(s), "Bolt", "ring count not met, arc blocked: Bolt between rings");
        Check.Eq(asked, 1, "then the arc is tested");

        asked = 0;
        s = CombatManager.ChooseCastShape(R(arc: true, blast: true), blastConditional: true, blastFanMet: true, ringDue: false, Blocked, out _);
        Check.Eq(N(s), "Blast", "blast fan met: Blast");
        Check.Eq(asked, 0, "blast: the arc isn't tested");

        s = CombatManager.ChooseCastShape(R(arc: true, blast: true), blastConditional: true, blastFanMet: false, ringDue: false, Blocked, out _);
        Check.Eq(N(s), "Bolt", "blast fan not met, arc blocked: Bolt (the conditional Blast is not the fallback)");
        s = CombatManager.ChooseCastShape(R(arc: true, blast: true), true, false, false, () => false, out _);
        Check.Eq(N(s), "Arc", "blast fan not met, arc clear: Arc between blasts (as before)");

        s = CombatManager.ChooseCastShape(R(arc: true, ring: true, blast: true), true, true, ringDue: true, Blocked, out _);
        Check.Eq(N(s), "Ring", "ring beats blast (unchanged)");

        asked = 0;
        s = CombatManager.ChooseCastShape(R(streak: true, bolt: true), false, false, false, Blocked, out _);
        Check.Eq(N(s), "Streak", "a rule without Arc: unchanged");
        Check.Eq(asked, 0, "a rule without Arc: no arc test");
    }

    // ── The cast itself (fake host, real spell data) ────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public bool? Blocked;
        public int Asked;

        public Rig(params string[] known)
        {
            FakeHost.Reset();
            foreach (string n in known)
                foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
            S.MinSkillLevelTier1 = 0; S.MinSkillLevelTier2 = 0; S.MinSkillLevelTier3 = 0; S.MinSkillLevelTier4 = 0;
            S.MinSkillLevelTier5 = 0; S.MinSkillLevelTier6 = 0; S.MinSkillLevelTier7 = 10000; S.MinSkillLevelTier8 = 10000;
            S.RingRange = 0;
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, Array.Empty<uint>());
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.RefreshKnownSpells();
            C.SetSpellManager(sm);
            C.ArcBlockedOverride = _ => { Asked++; return Blocked; };
        }

        public (string Spell, string Shape) Cast(MonsterRule rule, bool? blocked, int target = Target)
        {
            Blocked = blocked;
            int id = C.FindBestShapedSpell("Cold", rule, out _, out int shape, arcTargetId: target);
            // Each cast here is a fresh decision: step past the 300 ms verdict cache.
            ExpireCache();
            return (id == 0 ? "(none)" : SpellTable.ById(id)?.Name ?? $"#{id}", CombatManager.ShapeName(shape));
        }

        public void ExpireCache() => C.ForgetArcVerdict();
    }

    private static void SpellsCast()
    {
        string[] book = { "Frost Arc VI", "Frost Bolt VI", "Frost Streak VI" };
        var rig = new Rig(book);
        Check.Eq(rig.Cast(R(arc: true, bolt: true), false), ("Frost Arc VI", "Arc"), "Arc + Bolt, clear: Frost Arc VI");
        Check.Eq(rig.Cast(R(arc: true, bolt: true), true), ("Frost Bolt VI", "Bolt"), "Arc + Bolt, blocked: Frost Bolt VI");
        Check.Eq(rig.Cast(R(arc: true, streak: true), true), ("Frost Streak VI", "Streak"), "Arc + Streak, blocked: Frost Streak VI");
        Check.Eq(rig.Cast(R(arc: true), true), ("Frost Bolt VI", "Bolt"), "Arc only, blocked: Frost Bolt VI");
        Check.Eq(rig.Cast(R(arc: true), null), ("Frost Arc VI", "Arc"), "Arc only, can't tell: Frost Arc VI");
        Check.Eq(rig.Cast(R(bolt: true), true), ("Frost Bolt VI", "Bolt"), "Bolt rule: unchanged");

        int asked = rig.Asked;
        int id = rig.C.FindBestShapedSpell("Cold", R(arc: true, bolt: true), out _, out int probeShape);
        Check.Eq(CombatManager.ShapeName(probeShape), "Arc", "element probe (no target): Arc, no ray test");
        Check.Eq(rig.Asked, asked, "the probe didn't test the arc");
        Check.True(id != 0, "probe found a spell");

        var noStreak = new Rig("Frost Arc VI", "Frost Bolt VI");
        Check.Eq(noStreak.Cast(R(arc: true, streak: true), true), ("Frost Bolt VI", "Bolt"),
            "Arc + Streak, blocked, no streak known: the same element's Bolt");
        var arcsOnly = new Rig("Frost Arc VI");
        Check.Eq(arcsOnly.Cast(R(arc: true), true), ("(none)", "?"), "Arc only, blocked, no bolt known: nothing (never an arc into the obstacle)");
        Check.Eq(arcsOnly.Cast(R(arc: true), false), ("Frost Arc VI", "Arc"), "and clear: the arc");
    }

    private static void VerdictCache()
    {
        var rig = new Rig("Frost Arc VI", "Frost Bolt VI");
        rig.Blocked = true;
        int before = rig.C.ArcVerdictComputeCount;
        var rule = R(arc: true, bolt: true);
        for (int i = 0; i < 5; i++) rig.C.FindBestShapedSpell("Cold", rule, out _, out _, arcTargetId: Target);
        Check.Eq(rig.C.ArcVerdictComputeCount - before, 1, "five casts at one target within 300 ms: one ray test");
        Check.Eq(rig.Asked, 1, "the override (ray test) ran once");
        rig.Blocked = false;
        rig.C.FindBestShapedSpell("Cold", rule, out _, out int s, arcTargetId: Target);
        Check.Eq(CombatManager.ShapeName(s), "Bolt", "within 300 ms the cached verdict holds (still blocked)");
        rig.C.FindBestShapedSpell("Cold", rule, out _, out s, arcTargetId: Target + 1);
        Check.Eq(CombatManager.ShapeName(s), "Arc", "another target: its own verdict (clear)");
        Check.Eq(rig.C.ArcVerdictComputeCount - before, 2, "another target: tested");
        rig.ExpireCache();
        rig.C.FindBestShapedSpell("Cold", rule, out _, out s, arcTargetId: Target + 1);
        Check.Eq(rig.C.ArcVerdictComputeCount - before, 3, "after 300 ms: tested again");
        Check.Near(CombatManager.ArcVerdictCacheMs, 300, 0, "cache 300 ms");

        // Raycasting off and no override: can't tell, arc as before, nothing thrown.
        var off = new Rig("Frost Arc VI", "Frost Bolt VI");
        off.C.ArcBlockedOverride = null;
        off.S.EnableRaycasting = false;
        Check.Eq(off.C.IsArcBlockedTo(Target), null, "raycasting off: can't tell");
        off.ExpireCache();
        int idOff = off.C.FindBestShapedSpell("Cold", rule, out _, out int sOff, arcTargetId: Target);
        Check.Eq((SpellTable.ById(idOff)?.Name, CombatManager.ShapeName(sOff)), ("Frost Arc VI", "Arc"), "raycasting off: the arc, as before");

        var det = new TargetingFSM.LosDetail { Dungeon = true, ArcChecked = true, Checked = true };
        det.Arc.Blocked = true; det.Arc.HitAlong = 21.6f; det.Arc.HitZ = 2.0f;
        Check.Eq(CombatManager.DescribeArcBlock(in det), "ceiling 21.6 m out", "log text: ceiling and distance");
        det.LineBlocked = true;
        Check.Eq(CombatManager.DescribeArcBlock(in det), "wall in the straight line", "log text: wall");
    }

    private static void MagicArcSpeedSetting()
    {
        Check.Near(new LegacyUiSettings().MagicArcVelocity, 40, 0, "new settings: 40 m/s (ACE arc projectiles)");
        Check.Near(new TargetingFSM(new GeometryLoader(), new BlacklistManager()).MagicArcVelocity, 40, 0, "TargetingFSM default 40");
        Check.Near(LegacyUiSettings.MigrateMagicArcVelocity(25f), 40, 0, "saved 25 (the old flat-model default) -> 40");
        Check.Near(LegacyUiSettings.MigrateMagicArcVelocity(0f), 40, 0, "saved 0 -> 40");
        Check.Near(LegacyUiSettings.MigrateMagicArcVelocity(float.NaN), 40, 0, "saved NaN -> 40");
        Check.Near(LegacyUiSettings.MigrateMagicArcVelocity(33f), 33, 0, "a tuned value is kept");
        var old = JsonSerializer.Deserialize("{\"MagicArcVelocity\":25.0}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.Near(LegacyUiSettings.MigrateMagicArcVelocity(old.MagicArcVelocity), 40, 0, "an old profile with 25 loads as 40");
        var none = JsonSerializer.Deserialize("{}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.Near(none.MagicArcVelocity, 40, 0, "a profile without it: 40");
    }
}
