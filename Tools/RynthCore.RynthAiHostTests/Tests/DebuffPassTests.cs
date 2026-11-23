using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-22: "Debuff every monster in range before attacking" (DebuffAllBeforeAttacking, off by
// default). With several monsters in Monster Range each one's Monsters-rule debuffs go on all of
// them, nearest first, before any attack (so pets and the void spells that follow hit debuffed
// monsters). DebuffPass decides the order and the caps; CombatManager casts what it picks.
internal static class DebuffPassTests
{
    private const uint Player = 0x50000E31;
    private const uint Lb = 0xA9B40000;
    private const uint A = 0x80000501, B = 0x80000502, C = 0x80000503, D = 0x80000504;

    public static void Register(Runner r)
    {
        r.Add("debuff pass: nearest first, the fight target skipped, then attacking", PassOrder);
        r.Add("debuff pass: a monster that has its debuffs is skipped; one monster alone holds nothing", PassSkip);
        r.Add("debuff pass: failed tries and casts per monster are capped, and so is the pass's time", PassCaps);
        r.Add("debuff pass: a monster arriving mid-fight is debuffed between attacks, the target kept", PassArrival);
        r.Add("debuff pass: a monster hitting you in melee stops the pass while it does", PassOnYou);
        r.Add("debuff pass: a fight that ends starts the next one over", PassFightOver);
        r.Add("debuff pass: before the first cast the pet waits 5 s at most, and only for two or more", PassIdleHold);
        r.Add("debuff pass (combat): every monster gets Imperil and its vuln, nearest first, before the first bolt", CombatOrder);
        r.Add("debuff pass (combat): one that already has them is skipped; three resists skip a monster", CombatSkipAndCap);
        r.Add("debuff pass (combat): a new arrival mid-fight gets its debuffs between bolts at the same target", CombatArrival);
        r.Add("debuff pass (combat): a monster on you is debuffed and attacked first; the pet summon waits for the pass", CombatOnYouAndPets);
        r.Add("debuff pass: the setting is off by default, copied, sent and set like the others", Setting);
    }

    private static readonly DateTime T0 = new(2026, 11, 22, 20, 0, 0);

    // ── DebuffPass alone ──────────────────────────────────────────────────────

    private static void PassOrder()
    {
        var p = new DebuffPass();
        var need = new HashSet<int> { 1, 2, 3 };
        var near = new List<int> { 1, 2, 3 };   // 1 is the fight target, nearest
        p.Observe(near, engaged: true, T0);
        Check.Eq(p.Next(near, 1, need.Contains, false, T0), 2, "the nearest after the fight target (its own debuffs are the target code's)");
        Check.True(p.Holding(T0), "the pass holds attacks and the pet summon");
        p.NoteCast(2, T0);
        need.Remove(2);
        Check.Eq(p.Next(near, 1, need.Contains, false, T0.AddSeconds(2)), 3, "then the next nearest");
        p.NoteCast(3, T0.AddSeconds(2));
        need.Remove(3);
        Check.Eq(p.Next(near, 1, need.Contains, false, T0.AddSeconds(4)), 0, "all done: attack");
        Check.Eq(p.Phase, DebuffPass.Stage.Attacking, "attacking now");
        Check.False(p.Holding(T0.AddSeconds(4)), "nothing held");
        Check.Eq(p.Casts, 2, "two casts counted");
    }

    private static void PassSkip()
    {
        var p = new DebuffPass();
        var near = new List<int> { 1, 2, 3 };
        var need = new HashSet<int> { 3 };   // 2 already has them
        Check.Eq(p.Next(near, 1, need.Contains, false, T0), 3, "2 has its debuffs: straight to 3");

        var alone = new DebuffPass();
        Check.Eq(alone.Next(new List<int> { 1 }, 1, _ => true, false, T0), 0, "one monster: nothing to wait for");
        Check.Eq(alone.Phase, DebuffPass.Stage.Attacking, "attacking from the start");
        Check.False(alone.Holding(T0), "the pet isn't held");

        var empty = new DebuffPass();
        Check.Eq(empty.Next(new List<int>(), 0, _ => true, false, T0), 0, "nothing in range: nothing");
        Check.Eq(empty.Phase, DebuffPass.Stage.Idle, "and no pass");
    }

    private static void PassCaps()
    {
        var p = new DebuffPass();
        var near = new List<int> { 1, 2, 3 };
        var need = new HashSet<int> { 2, 3 };
        for (int i = 0; i < DebuffPass.MaxFailsPerMonster; i++)
        {
            Check.Eq(p.Next(near, 1, need.Contains, false, T0.AddSeconds(i)), 2, $"try {i + 1} at the resisting monster");
            p.NoteCast(2, T0.AddSeconds(i));
            bool gaveUp = p.NoteFailed(2);
            Check.Eq(gaveUp, i == DebuffPass.MaxFailsPerMonster - 1, $"resist {i + 1}: given up only at the last");
        }
        Check.True(p.GaveUp(2), "skipped after three failed tries");
        Check.Eq(p.Next(near, 1, need.Contains, false, T0.AddSeconds(4)), 3, "the pass goes on to the next one");

        // Casts that never fail (no result line, taken as landed) but never take: capped too.
        var q = new DebuffPass();
        int casts = 0;
        while (q.Next(near, 1, id => id == 2, false, T0.AddSeconds(casts)) == 2 && casts < 50) { q.NoteCast(2, T0); casts++; }
        Check.Eq(casts, DebuffPass.MaxTriesPerMonster, "at most 8 casts at one monster");

        // The pass's time: 30 s at most, whatever is left.
        var t = new DebuffPass();
        Check.Eq(t.Next(near, 1, _ => true, false, T0), 2, "started");
        Check.True(t.Holding(T0.AddSeconds(29)), "held at 29 s");
        Check.Eq(t.Next(near, 1, _ => true, false, T0.AddSeconds(DebuffPass.PassSeconds)), 0, "at 30 s: attack");
        Check.True(t.EndReason.StartsWith("time cap"), $"ended by the time cap ({t.EndReason})");
        Check.False(t.Holding(T0.AddSeconds(30)), "nothing held");
    }

    private static void PassArrival()
    {
        var p = new DebuffPass();
        var near = new List<int> { 1, 2 };
        var need = new HashSet<int>();
        p.Observe(near, true, T0);
        Check.Eq(p.Next(near, 1, need.Contains, false, T0), 0, "2 already debuffed: the pass ends at once");
        Check.Eq(p.Phase, DebuffPass.Stage.Attacking, "attacking");

        // 3 comes into range while the fight goes on.
        var later = new List<int> { 1, 3, 2 };
        p.Observe(later, true, T0.AddSeconds(5));
        Check.True(p.IsArrival(3), "an arrival");
        need.Add(3);
        need.Add(2);   // 2's debuffs ran out: not renewed by the pass (it isn't an arrival)
        Check.Eq(p.Next(later, 1, need.Contains, true, T0.AddSeconds(5)), 0, "not while something is hitting you");
        Check.Eq(p.Next(later, 1, need.Contains, false, T0.AddSeconds(5)), 3, "the arrival is debuffed");
        p.NoteCast(3, T0.AddSeconds(5));
        Check.Eq(p.Next(later, 1, need.Contains, false, T0.AddSeconds(6)), 0, "then one attack at the fight target");
        p.NoteAttack();
        Check.Eq(p.Next(later, 1, need.Contains, false, T0.AddSeconds(7)), 3, "then its next debuff");
        p.NoteCast(3, T0.AddSeconds(7));
        p.NoteAttack();
        need.Remove(3);
        Check.Eq(p.Next(later, 1, need.Contains, false, T0.AddSeconds(8)), 0, "done: attacks only; 2 of the first pass isn't renewed here");

        need.Add(3);
        Check.Eq(p.Next(later, 1, need.Contains, false, T0.AddSeconds(5 + DebuffPass.ArrivalSeconds + 1)), 0,
            "past 30 s since it came, an arrival is left to the target code");
    }

    private static void PassOnYou()
    {
        var p = new DebuffPass();
        var near = new List<int> { 1, 2, 3 };
        Check.Eq(p.Next(near, 1, _ => true, monsterOnYou: true, T0), 0, "something hitting you: attack (the target's own debuffs first)");
        Check.Eq(p.Phase, DebuffPass.Stage.Debuffing, "the pass is still on");
        Check.False(p.Holding(T0), "and the pet isn't held while you're hit");
        Check.Eq(p.Next(near, 1, _ => true, monsterOnYou: false, T0.AddSeconds(3)), 2, "nothing on you: the pass goes on");
        Check.True(p.Holding(T0.AddSeconds(3)), "and holds the pet again");
    }

    private static void PassIdleHold()
    {
        var p = new DebuffPass();
        p.Observe(new List<int> { 1, 2 }, engaged: false, T0);
        Check.True(p.Holding(T0.AddSeconds(1)), "two in range, the first cast not yet out: the pet waits");
        Check.False(p.Holding(T0.AddSeconds(DebuffPass.IdleHoldSeconds)), "but 5 s at most (a fight that never casts)");
        var one = new DebuffPass();
        one.Observe(new List<int> { 1 }, engaged: false, T0);
        Check.False(one.Holding(T0.AddSeconds(1)), "one monster: no wait");
    }

    private static void PassFightOver()
    {
        var p = new DebuffPass();
        var near = new List<int> { 1, 2 };
        p.Observe(near, true, T0);
        p.Next(near, 1, _ => false, false, T0);
        Check.Eq(p.Phase, DebuffPass.Stage.Attacking, "attacking");
        p.Observe(new List<int>(), false, T0.AddSeconds(1));
        p.Observe(new List<int>(), false, T0.AddSeconds(2));
        Check.Eq(p.Phase, DebuffPass.Stage.Attacking, "a gap of 1 s isn't the end");
        p.Observe(new List<int>(), false, T0.AddSeconds(4.5));
        Check.Eq(p.Phase, DebuffPass.Stage.Idle, "3 s with nothing: the fight is over");
        Check.Eq(p.Known, 0, "and its monsters forgotten");
        Check.Eq(p.Next(near, 1, _ => true, false, T0.AddSeconds(10)), 2, "the next fight starts a new pass");
    }

    // ── In combat ─────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager Cm;
        private readonly MethodInfo _attack = typeof(CombatManager).GetMethod("AttackWithMagic", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo LastScan = typeof(CombatManager).GetField("_lastScanTime", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private readonly WorldObject _target;

        public Rig(bool on = true, double dOff = 40)
        {
            FakeHost.Reset();
            foreach (string n in new[] { "Imperil Other VI", "Fire Vulnerability Other VI", "Flame Bolt VI" })
                foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
            S = new LegacyUiSettings { IsMacroRunning = true, EnableCombat = true, RingRange = 0, MonsterRange = 20, TargetSelection = 1, DebuffAllBeforeAttacking = on };
            S.MinSkillLevelTier1 = 0; S.MinSkillLevelTier2 = 0; S.MinSkillLevelTier3 = 0; S.MinSkillLevelTier4 = 0;
            S.MinSkillLevelTier5 = 0; S.MinSkillLevelTier6 = 0; S.MinSkillLevelTier7 = 10000; S.MinSkillLevelTier8 = 10000;
            S.MonsterRules.Add(new MonsterRule { Name = "Drudge", DamageType = "Fire", Imperil = true, Vuln = true, UseBolt = true });
            FakeHost.Names[Player] = "Me";
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.Positions[Player] = FakeHost.PlayerPose;
            Place(A, "Drudge Slinker", 5);
            Place(B, "Drudge Prowler", 10);
            Place(C, "Drudge Ravener", 15);
            Place(D, "Drudge Skulker", dOff);
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.WeaponCalls = true;   // the combat mode is read (Melee: no pet wait)
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { A, B, C, D });
            Cm = new CombatManager(host, S, cache);
            Cm.SetPlayerId(Player);
            Cm.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            Cm.LosBlockedOverride = _ => false;
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.RefreshKnownSpells();
            Cm.SetSpellManager(sm);
            Scan();
            Cm.activeTargetId = unchecked((int)A);
            _target = new WorldObject(unchecked((int)A), "Drudge Slinker");
        }

        public static void Place(uint id, string name, double north)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Lb | 0x1C, 96f, (float)(96 + north), 0f);
            FakeHost.ItemTypes[id] = 0x10;
        }

        public void Scan()
        {
            LastScan.SetValue(Cm, DateTime.MinValue);
            Cm.ScanNearbyTargets();
        }

        /// <summary>One attack decision; the cast it sent (target, spell name), or (0, "") for none.</summary>
        public (uint Target, string Spell) Step()
        {
            int before = FakeHost.Casts.Count;
            _attack.Invoke(Cm, new object[] { _target });
            if (FakeHost.Casts.Count == before) return (0, "");
            var cast = FakeHost.Casts[^1];
            return (cast.Target, SpellTableStub.GetById(cast.SpellId)?.Name ?? "");
        }

        /// <summary>The pending debuff's result line.</summary>
        public void Chat(string line) => Cm.HandleChatForDebuffs(line);

        /// <summary>Steps until <paramref name="max"/> casts or a bolt; every debuff lands.</summary>
        public List<(uint Target, string Spell)> RunUntilBolt(int max = 20)
        {
            var seen = new List<(uint, string)>();
            for (int i = 0; i < max; i++)
            {
                var c = Step();
                if (c.Target == 0) break;
                seen.Add(c);
                if (c.Spell.StartsWith("Flame Bolt", StringComparison.Ordinal)) break;
                Chat($"You cast {c.Spell} on {FakeHost.Names[c.Target]}");
            }
            return seen;
        }
    }

    private static string Describe(IEnumerable<(uint Target, string Spell)> casts) =>
        string.Join(", ", casts.Select(c => $"{(c.Target == A ? "A" : c.Target == B ? "B" : c.Target == C ? "C" : c.Target == D ? "D" : "?")}:{c.Spell}"));

    private static void CombatOrder()
    {
        var r = new Rig();
        var casts = r.RunUntilBolt();
        Check.Eq(Describe(casts),
            "A:Imperil Other VI, A:Fire Vulnerability Other VI, B:Imperil Other VI, B:Fire Vulnerability Other VI, " +
            "C:Imperil Other VI, C:Fire Vulnerability Other VI, A:Flame Bolt VI",
            "the target's own, then the others nearest first, then the first bolt at the target");
        Check.Eq(r.Cm.activeTargetId, unchecked((int)A), "the fight target never changed");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[DebuffPass] start: 3 monsters in range", StringComparison.Ordinal)), "the start is logged");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[DebuffPass] casting Imperil (T6) on 0x80000502 'Drudge Prowler' at 10.0 yd", StringComparison.Ordinal)),
            "each pass cast is logged with the monster and its distance");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[DebuffPass] done after", StringComparison.Ordinal) && l.Contains("4 debuff casts") && l.Contains("attacking now")),
            "and the end, with the count");

        var off = new Rig(on: false);
        Check.Eq(Describe(off.RunUntilBolt()),
            "A:Imperil Other VI, A:Fire Vulnerability Other VI, A:Flame Bolt VI", "setting off: only the target is debuffed, as before");
        Check.False(FakeHost.Logs.Any(l => l.StartsWith("[DebuffPass]", StringComparison.Ordinal)), "and the pass says nothing");
    }

    private static void CombatSkipAndCap()
    {
        var r = new Rig();
        // B has both from an earlier fight: its timers are still running.
        var timers = (DebuffTimers)typeof(CombatManager).GetField("_debuffs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Cm)!;
        timers.Landed(unchecked((int)B), "Imperil", 240, RynthClock.Now);
        timers.Landed(unchecked((int)B), "Vuln:Fire", 240, RynthClock.Now);

        var own = r.RunUntilBolt(max: 2);
        Check.Eq(Describe(own), "A:Imperil Other VI, A:Fire Vulnerability Other VI", "the target's own first");
        // C resists every try.
        var cCasts = new List<(uint, string)>();
        for (int i = 0; i < 10; i++)
        {
            var c = r.Step();
            if (c.Target == 0) break;
            cCasts.Add(c);
            if (c.Target != C) break;
            r.Chat("Drudge Ravener resists your spell");
        }
        Check.Eq(Describe(cCasts), "C:Imperil Other VI, C:Imperil Other VI, C:Imperil Other VI, A:Flame Bolt VI",
            "B skipped (it has them); C resists three times and is skipped; then the bolt");
        Check.True(r.Cm.DebuffPassState.GaveUp(unchecked((int)C)), "C is given up for this fight");
        Check.True(FakeHost.Logs.Any(l => l.Contains("'Drudge Ravener': 3 failed tries, skipped", StringComparison.Ordinal)), "and the log says so");
    }

    private static void CombatArrival()
    {
        var r = new Rig();
        r.RunUntilBolt();   // the pass and the first bolt
        Check.Eq(r.Cm.DebuffPassState.Phase, DebuffPass.Stage.Attacking, "attacking");
        Check.Eq(r.Step().Spell, "Flame Bolt VI", "the next is a bolt too");

        // D walks into range (8 yd) mid-fight.
        Rig.Place(D, "Drudge Skulker", 8);
        r.Scan();
        Check.True(r.Cm.DebuffPassState.IsArrival(unchecked((int)D)), "seen as an arrival");
        var seq = new List<(uint, string)>();
        for (int i = 0; i < 6; i++)
        {
            var c = r.Step();
            seq.Add(c);
            if (!c.Spell.StartsWith("Flame Bolt", StringComparison.Ordinal)) r.Chat($"You cast {c.Spell} on Drudge Skulker");
        }
        Check.Eq(Describe(seq),
            "D:Imperil Other VI, A:Flame Bolt VI, D:Fire Vulnerability Other VI, A:Flame Bolt VI, A:Flame Bolt VI, A:Flame Bolt VI",
            "one debuff, one bolt at the same target, the next debuff, then bolts only");
        Check.Eq(r.Cm.activeTargetId, unchecked((int)A), "the current kill is kept");
        Check.True(FakeHost.Logs.Any(l => l.Contains("on 0x80000504 'Drudge Skulker'", StringComparison.Ordinal) && l.Contains("(came into range mid-fight)")),
            "the log names it as an arrival");
    }

    private static void CombatOnYouAndPets()
    {
        var r = new Rig();
        r.Cm.NoteHitOnMeForTests("Drudge Slinker");   // A, 5 yd off, just hit us
        Check.True(r.Cm.MonsterOnYou(), "A is on you");
        Check.Eq(Describe(r.RunUntilBolt()), "A:Imperil Other VI, A:Fire Vulnerability Other VI, A:Flame Bolt VI",
            "the one on you: its own debuffs, then attacked; the others wait");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[DebuffPass] a monster is hitting you in melee", StringComparison.Ordinal)), "logged once");
        Check.False(r.Cm.DebuffPassHoldsPets, "pets aren't held while you're being hit");

        var far = new Rig();
        far.Cm.NoteHitOnMeForTests("Drudge Ravener");   // C hits from 15 yd: a spell, not melee
        Check.False(far.Cm.MonsterOnYou(), "a hit from 15 yd isn't a monster on you");

        var pets = new Rig();
        Check.True(pets.Cm.DebuffPassHoldsPets, "three in range in Magic mode, before the first cast: the pet waits");
        FakeHost.CombatModeValue = CombatMode.Melee;
        Check.False(pets.Cm.DebuffPassHoldsPets, "with a melee weapon out the pass can't run: not held");
        FakeHost.CombatModeValue = CombatMode.Magic;
        pets.Step();   // A's Imperil
        pets.Chat("You cast Imperil Other VI on Drudge Slinker");
        pets.Step();   // A's vuln
        pets.Chat("You cast Fire Vulnerability Other VI on Drudge Slinker");
        pets.Step();   // B's Imperil: the pass is on
        Check.True(pets.Cm.DebuffPassHoldsPets, "the pet summon waits while the pass runs");
        pets.Chat("You cast Imperil Other VI on Drudge Prowler");
        pets.RunUntilBolt();
        Check.False(pets.Cm.DebuffPassHoldsPets, "done: the pet comes out");
        Check.True(FakeHost.Logs.Any(l => l == "[DebuffPass] the pet summon waits for the debuff pass"), "the wait is logged");

        var off = new Rig(on: false);
        off.Step();
        Check.False(off.Cm.DebuffPassHoldsPets, "setting off: never held");
    }

    private static void Setting()
    {
        Check.False(new LegacyUiSettings().DebuffAllBeforeAttacking, "off by default");
        var old = System.Text.Json.JsonSerializer.Deserialize("{\"MonsterRange\":7}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && !old.DebuffAllBeforeAttacking, "a profile saved before the setting loads it as off");
        var saved = System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings { DebuffAllBeforeAttacking = true }, RynthAiJsonContext.Default.LegacyUiSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(saved, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(back != null && back.DebuffAllBeforeAttacking, "saved and loaded with the profile");
        Check.True(RynthCore.Plugin.RynthAi.Meta.MetaManager.TryMapVtOption("debuffallbeforeattacking", out string ra) && ra == "DebuffAllBeforeAttacking",
            "the meta option debuffallbeforeattacking is the setting");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { DebuffAllBeforeAttacking = true }, dst });
        Check.True(dst.DebuffAllBeforeAttacking, "a profile's on is copied");

        var s = new LegacyUiSettings { DebuffAllBeforeAttacking = true };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = System.Text.Json.JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.DebuffAllBeforeAttacking == true, "sent to the engine's Settings window");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.DebuffAllBeforeAttacking, "an engine without the row leaves it alone");
        dash.ApplySettingsJson("{\"DebuffAllBeforeAttacking\":false}");
        Check.False(s.DebuffAllBeforeAttacking, "the row's tick sets it");
    }
}
