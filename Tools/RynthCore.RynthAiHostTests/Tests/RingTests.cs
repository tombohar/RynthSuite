using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// "It doesn't like to use rings even when set" (2026-11-06). A rule with Ring on casts a ring only
// when Min Ring Targets monsters are within Ring Range of the character and a ring of the cast's
// element is known; otherwise the rule's other shape. Nothing said which of those held a ring
// back, so a player saw bolts and no reason. Now the log says it (the count against the setting,
// Ring Range 0, or the ring that isn't known and why), for the cast itself only and never every
// attack. And a War caster whose rule says Nether (its bolts are Fire's) casts Fire's rings: they
// used to be looked up under Nether, which War has none of.
internal static class RingTests
{
    private const uint Player = 0x50000D01;
    private const uint Lb = 0x5B480000;
    private const int Target = unchecked((int)0x80000301);

    public static void Register(Runner r)
    {
        r.Add("rings: the count is the live monsters within Ring Range of the character (dead and unattackable left out)", LiveCount);
        r.Add("rings: too few near is logged with the count and the setting, at most every 30 s per rule", NotDueLogged);
        r.Add("rings: Ring Range 0 turns rings off and says so once", RingRangeZero);
        r.Add("rings: a ring that isn't known says why (tier, rings tried), for the cast only, not element probes", NoRingKnownReason);
        r.Add("rings: a War caster whose rule says Nether casts Fire's ring, as its bolts are Fire", WarCasterNetherRule);
    }

    private static MonsterRule BoltRing(string name = "Default") => new() { Name = name, UseBolt = true, UseRing = true };

    private static string NameOf(int id) => id == 0 ? "(none)" : SpellTable.ById(id)?.Name ?? $"#{id}";

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public DateTime Now = new(2026, 11, 6, 12, 0, 0);
        private uint _next = 0x80000301;

        /// <summary>A caster at combat tier <paramref name="tier"/> knowing exactly <paramref name="known"/>,
        /// with monsters at (north, east, up, health) metres from it; health null = never sent.</summary>
        public Rig(int tier, string[] known, (double N, double E, double Up, float? Health, bool Attackable)[]? mobs = null,
                   (int Buffed, int Training)? war = null, (int Buffed, int Training)? voidMagic = null)
        {
            FakeHost.Reset();
            foreach (string n in known)
            {
                var rows = SpellTable.Named(n).ToList();
                Check.True(rows.Count > 0, $"test setup: '{n}' is in the spell data");
                foreach (var row in rows) FakeHost.Spellbook.Add((uint)row.Id);
            }
            int T(int k) => k <= tier ? 0 : 10000;   // the tier reads the no-skills stub (250 buffed)
            S.MinSkillLevelTier1 = 0; S.MinSkillLevelTier2 = T(2); S.MinSkillLevelTier3 = T(3); S.MinSkillLevelTier4 = T(4);
            S.MinSkillLevelTier5 = T(5); S.MinSkillLevelTier6 = T(6); S.MinSkillLevelTier7 = T(7); S.MinSkillLevelTier8 = T(8);
            S.RingRange = 5; S.MinRingTargets = 3; S.WallRange = 0; S.BlastRange = 0;

            FakeHost.Names[Player] = "Me";
            FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            if (war is { } w) FakeHost.Skills[(Player, 34)] = w;
            if (voidMagic is { } v) FakeHost.Skills[(Player, 43)] = v;
            var ids = new List<uint>();
            var health = new Dictionary<uint, float>();
            foreach (var m in mobs ?? Array.Empty<(double, double, double, float?, bool)>())
            {
                uint id = _next++;
                FakeHost.Names[id] = "Drudge Skulker";
                FakeHost.Positions[id] = (Lb | 0x1C, (float)(96 + m.E), (float)(96 + m.N), (float)m.Up);
                FakeHost.ItemTypes[id] = 0x10;   // a creature
                if (!m.Attackable) FakeHost.NotAttackable.Add(id);
                if (m.Health is float h) health[id] = h;
                ids.Add(id);
            }
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, ids, health);
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            if (war != null || voidMagic != null)
            {
                var skills = new CharacterSkills(host);
                skills.SetPlayerId(Player);
                C.SetCharacterSkills(skills);
            }
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.RefreshKnownSpells();
            C.SetSpellManager(sm);
            C.TargetClock = () => Now;
        }

        /// <summary>The cast itself (a target id, as AttackWithMagic passes it).</summary>
        public (string Spell, string Shape) Cast(string element, MonsterRule rule)
        {
            int id = C.FindBestShapedSpell(element, rule, out _, out int shape, arcTargetId: Target);
            return (NameOf(id), CombatManager.ShapeName(shape));
        }

        /// <summary>An element probe (no target), as Auto's weakness pick and the wand check ask.</summary>
        public int Probe(string element, MonsterRule rule) => C.FindBestShapedSpell(element, rule, out _, out _);
    }

    private static int Lines(string text) => FakeHost.Logs.Count(l => l.Contains(text));

    // Three living, attackable monsters inside 5 m (one whose health was never sent, as ACE sends it
    // only for the selected target; one partly above), and around them one at 6 m, a dead one and an
    // unattackable one (an NPC) right next to the character: 3 count.
    private static void LiveCount()
    {
        var mobs = new (double, double, double, float?, bool)[]
        {
            (2, 0, 0, 0.8f, true),
            (0, 3, 0, null, true),
            (3, 0, 4, 1f, true),     // 3 north, 4 up: 5.0 m, on the edge (in)
            (6, 0, 0, 1f, true),     // out
            (1, 0, 0, 0f, true),     // dead
            (0, 1, 0, 1f, false),    // not attackable
        };
        var known = new[] { "Frost Bolt VI", "Halo of Frost" };

        var rig = new Rig(6, known, mobs);
        rig.S.MinRingTargets = 3;
        Check.Eq(rig.Cast("Cold", BoltRing()), ("Halo of Frost", "Ring"), "3 near, 3 needed: the ring");

        rig = new Rig(6, known, mobs);
        rig.S.MinRingTargets = 4;
        Check.Eq(rig.Cast("Cold", BoltRing()), ("Frost Bolt VI", "Bolt"), "3 near, 4 needed: the bolt");
        Check.Eq(Lines("3 monsters within Ring Range 5 and Min Ring Targets is 4"), 1, "the log gives the count and the setting");

        rig = new Rig(6, known, mobs);
        rig.S.MinRingTargets = 3; rig.S.RingRange = 4;
        Check.Eq(rig.Cast("Cold", BoltRing()).Shape, "Bolt", "Ring Range 4: the one at 5 m is out, 2 near");
        Check.Eq(Lines("2 monsters within Ring Range 4"), 1, "the count at Ring Range 4");
    }

    private static void NotDueLogged()
    {
        var rig = new Rig(6, new[] { "Frost Bolt VI", "Halo of Frost" });
        int near = 1;
        rig.C.RingCountOverride = () => near;
        rig.S.MinRingTargets = 3;
        var rule = BoltRing("Drudge");

        Check.Eq(rig.Cast("Cold", rule), ("Frost Bolt VI", "Bolt"), "1 near, 3 needed: the bolt");
        Check.Eq(Lines("rule 'Drudge' has Ring on: no ring yet, 1 monster within Ring Range 5 and Min Ring Targets is 3"), 1, "said, singular");
        for (int i = 0; i < 20; i++) { rig.Now = rig.Now.AddSeconds(1); rig.Cast("Cold", rule); }
        Check.Eq(Lines("has Ring on: no ring yet"), 1, "not every attack: once in 20 s");
        rig.Now = rig.Now.AddSeconds(11);
        near = 2;
        rig.Cast("Cold", rule);
        Check.Eq(Lines("has Ring on: no ring yet"), 2, "again after 30 s, with the new count");
        Check.Eq(Lines("2 monsters within Ring Range 5"), 1, "the new count");

        near = 3;
        Check.Eq(rig.Cast("Cold", rule), ("Halo of Frost", "Ring"), "3 near: the ring");
        Check.Eq(Lines("has Ring on: no ring yet"), 2, "a ring cast says nothing more (the cast line has shape=Ring)");

        var other = BoltRing("Mosswart");
        near = 1;
        rig.Cast("Cold", other);
        Check.Eq(Lines("rule 'Mosswart' has Ring on: no ring yet"), 1, "per rule");

        var noRing = new MonsterRule { Name = "Golem", UseBolt = true };
        rig.Cast("Cold", noRing);
        Check.Eq(Lines("rule 'Golem'"), 0, "a rule without Ring says nothing about rings");

        rig.Probe("Cold", BoltRing("Lugian"));
        Check.Eq(Lines("rule 'Lugian'"), 0, "an element probe says nothing");
    }

    private static void RingRangeZero()
    {
        var rig = new Rig(6, new[] { "Frost Bolt VI", "Halo of Frost" });
        rig.C.RingCountOverride = () => 8;
        rig.S.RingRange = 0;
        Check.Eq(rig.Cast("Cold", BoltRing()), ("Frost Bolt VI", "Bolt"), "Ring Range 0: no ring, however many are near");
        rig.Now = rig.Now.AddMinutes(5);
        rig.Cast("Cold", BoltRing());
        Check.Eq(Lines("has Ring on, but Ring Range is 0: rings are off"), 1, "said once");
        Check.Eq(rig.S.RingRange, 0, "the setting is left as it is");
    }

    private static void NoRingKnownReason()
    {
        // Tier 4: rings are level 5 and 7, so none is castable yet, known or not.
        var low = new Rig(4, new[] { "Frost Bolt IV", "Halo of Frost" });
        low.C.RingCountOverride = () => 5;
        Check.Eq(low.Cast("Cold", BoltRing()), ("Frost Bolt IV", "Bolt"), "tier 4: the bolt");
        Check.Eq(Lines("no Cold ring spell known (War Magic tier 4: rings are level 5 and 7)"), 1, "the log names the tier");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Ring spell not found for Cold (War Magic tier 4")), "and the chat");
        Check.Eq(Lines("has Ring on: no ring yet"), 0, "not a count problem: the count was met");

        // Tier 6 knowing only the II (level 7): the rings tried, and that the II waits for tier 7.
        var six = new Rig(6, new[] { "Frost Bolt VI", "Halo of Frost II" });
        six.C.RingCountOverride = () => 5;
        Check.Eq(six.Cast("Cold", BoltRing()).Shape, "Bolt", "tier 6 with only the II: the bolt");
        Check.Eq(Lines("tried: Halo of Frost; Halo of Frost II is level 7"), 1, "the rings tried");

        // Element probes (Auto's weakness pick asks every element) say nothing: the chat used to
        // report "Ring spell not found for Slash" while the bot cast Cold.
        var probe = new Rig(7, new[] { "Frost Bolt VI", "Halo of Frost II", "Whirling Blade VI" });
        probe.C.RingCountOverride = () => 5;
        foreach (string e in new[] { "Slash", "Fire", "Acid", "Pierce", "Bludgeon", "Lightning" })
            probe.Probe(e, BoltRing());
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("Ring spell not found")), 0, "no chat from the probes");
        Check.Eq(Lines("ring spell known"), 0, "no log from the probes");
        Check.Eq(probe.Cast("Cold", BoltRing()), ("Halo of Frost II", "Ring"), "the cast's own element has its ring");
        Check.Eq(probe.Cast("Slash", BoltRing()).Shape, "Bolt", "a cast of Slash, no Slash ring: the bolt");
        Check.Eq(Lines("no Slash ring spell known (tried: Horizon's Blades II, Horizon's Blades)"), 1, "said for the cast");
    }

    private static void WarCasterNetherRule()
    {
        var known = new[] { "Flame Bolt VI", "Cassius' Ring of Fire II", "Nether Bolt VI", "Clouded Soul II" };
        var rig = new Rig(7, known, war: (400, 3), voidMagic: (10, 1));
        rig.C.RingCountOverride = () => 5;
        Check.Eq(rig.Cast("Nether", BoltRing()), ("Cassius' Ring of Fire II", "Ring"), "War only, rule says Nether: Fire's ring");
        rig.C.RingCountOverride = () => 1;
        Check.Eq(rig.Cast("Nether", BoltRing()), ("Flame Bolt VI", "Bolt"), "too few near: Fire's bolt, as before");

        // Void trained: the Nether rule casts Void's ring, as before.
        var both = new Rig(7, known, war: (400, 3), voidMagic: (400, 3));
        both.C.RingCountOverride = () => 5;
        Check.Eq(both.Cast("Nether", BoltRing()), ("Clouded Soul II", "Ring"), "Void trained: Void's ring");

        // War only, Nether rule, no fire ring known: the reason names Fire's rings.
        var none = new Rig(7, new[] { "Flame Bolt VI" }, war: (400, 3), voidMagic: (10, 1));
        none.C.RingCountOverride = () => 5;
        Check.Eq(none.Cast("Nether", BoltRing()), ("Flame Bolt VI", "Bolt"), "no fire ring known: the bolt");
        Check.Eq(Lines("no Nether ring spell known (tried: Cassius' Ring of Fire II, Cassius' Ring of Fire, Flame Ring)"), 1, "the rings tried are Fire's");
    }
}
