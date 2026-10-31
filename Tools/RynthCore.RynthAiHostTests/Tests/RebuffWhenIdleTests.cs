using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Rebuff When Idle (RebuffWhenIdle + RebuffIdleMarginMinutes, VTank's "idlebufftopoff"). The checkbox
// was saved and shown but nothing read it. What it means (the owner, 2026-10-30): an earlier timer for
// idle moments. With it on and the bot idle (no fight target, no monster within Monster Range, no
// corpse to loot, no salvage), a rebuff starts once a buff has Rebuff With + the margin (default 2
// minutes) left. The regular rebuff is unchanged, in a fight or not; an early top-off never starts in
// a fight and stops when one begins; margin 0 adds nothing.
internal static class RebuffWhenIdleTests
{
    private const uint Player = 0x50000F01;
    private const uint STypeCreature = 31, STypeLife = 33;

    private const string Strength = "Strength Self VI", Willpower = "Willpower Self VI";

    public static void Register(Runner r)
    {
        r.Add("rebuff when idle: idle, a buff inside Rebuff With + the margin is rebuffed early", IdleInsideMarginRebuffed);
        r.Add("rebuff when idle: in a fight, a buff inside the margin but over Rebuff With is not rebuffed", FightInsideMarginWaits);
        r.Add("rebuff when idle: in a fight, a buff at or under Rebuff With (or dispelled) is rebuffed as before", FightRegularRebuff);
        r.Add("rebuff when idle: margin 0, or the option off, adds nothing", MarginZeroAddsNothing);
        r.Add("rebuff when idle: an early top-off under way stops when a fight starts (the cast sent resolves); the regular rules take over", EarlyRunStopsForFight);
        r.Add("rebuff when idle: Force Buff in a fight is unchanged", ForceBuffUnchanged);
        r.Add("rebuff when idle: the margin defaults to 2, is saved, loaded, copied, bridged, in the settings map; idlebufftopoff imports the option", Setting);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private static int Id(string name) => SpellTable.Named(name).Select(r => r.Id).FirstOrDefault();
    private static string NameOf(int id) => SpellTable.ById(id)?.Name ?? $"#{id}";

    /// <summary>Creature and Life Magic 300 (tier 6), in Magic stance, knowing Strength and Willpower
    /// Self VI; the macro on, Rebuff With 300 s, no vital lines, full vitals. <see cref="Idle"/> is what
    /// the plugin's idle test answers (and not idle = a monster within Monster Range).</summary>
    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly BuffManager B;
        public bool Idle = true;
        private DateTime _now = new(2026, 10, 30, 12, 0, 0);

        public Rig(bool rebuffWhenIdle, int marginMinutes = 2)
        {
            FakeHost.Reset();
            foreach (string n in new[] { Strength, Willpower })
                foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeCreature)] = (300, 2);
            FakeHost.Skills[(Player, STypeLife)] = (300, 2);
            FakeHost.WeaponCalls = true;   // GetCurrentCombatMode answers CombatModeValue
            FakeHost.CombatModeValue = CombatMode.Magic;
            var host = FakeHost.Create();
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, EnableBuffing = true, SpellCastIntervalMs = 0,
                RebuffWhenIdle = rebuffWhenIdle, RebuffIdleMarginMinutes = marginMinutes, RebuffSecondsRemaining = 300,
                HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, UseBuffItems = false,
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
            B = new BuffManager(host, S, sm, v) { InCombatOverride = () => !Idle, IdleForTopOff = () => Idle };
            B.SetCharacterSkills(skills);
            FakeHost.ServerTime = 100_000;
            B.Clock = () => _now;
            B.MarkLiveBuffsReadyForTests();
        }

        /// <summary>The live enchantment registry (spell, seconds left), read.</summary>
        public void Live(params (string Name, double Left)[] enchantments)
        {
            FakeHost.Enchantments.Clear();
            foreach (var (name, left) in enchantments) FakeHost.Enchantments.Add(((uint)Id(name), FakeHost.ServerTime + left));
            B.RefreshFromLiveMemory();
        }

        /// <summary>A monster's dispel takes <paramref name="buff"/> off, as the engine reports it.</summary>
        public void Dispel(string buff)
        {
            FakeHost.Enchantments.RemoveAll(e => e.SpellId == (uint)Id(buff));
            B.OnEnchantmentRemoved((uint)Id(buff));
        }

        /// <summary>The server's answer to a buff cast: it lands (registry and result line).</summary>
        public void Answer(string name)
        {
            FakeHost.Enchantments.RemoveAll(e => e.SpellId == (uint)Id(name));
            FakeHost.Enchantments.Add(((uint)Id(name), FakeHost.ServerTime + 2700));
            B.OnChatWindowText($"You cast {name} on yourself", 7);
        }

        /// <summary>One heartbeat a second; a cast it sends is answered unless <paramref name="answer"/> is false. The spell sent, or "".</summary>
        public string Beat(bool answer = true)
        {
            _now = _now.AddSeconds(1);
            int before = FakeHost.Casts.Count;
            B.OnHeartbeat();
            if (FakeHost.Casts.Count == before) return "";
            string name = NameOf(FakeHost.Casts.Last().SpellId);
            if (answer) Answer(name);
            return name;
        }

        public List<string> Beats(int n)
        {
            var sent = new List<string>();
            for (int i = 0; i < n; i++) { string s = Beat(); if (s.Length > 0) sent.Add(s); }
            return sent;
        }

        /// <summary>The arbiter's choice, from the inputs RynthAiPlugin gives it (combat wants the tick when not idle).</summary>
        public BotActivity Arbiter()
        {
            bool wantBuff = B.PendingSpellId != 0 || B.WantsVitalRecharge || (S.EnableBuffing && B.NeedsAnyBuff());
            return ActivityArbiter.Decide(new ArbiterInputs(true, wantBuff, wantCombat: !Idle, wantLooting: false, wantSalvaging: false, wantNav: false,
                                                            combatEngaged: !Idle));
        }

        public bool Running => B.GetStateSnapshot().IsForceRebuffing;
        public int Logged(string part) => FakeHost.Logs.Count(l => l.Contains(part, StringComparison.Ordinal));
    }

    private static string Show(IEnumerable<string> sent) => sent.Any() ? string.Join(", ", sent) : "(none)";

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void IdleInsideMarginRebuffed()
    {
        var rig = new Rig(rebuffWhenIdle: true);
        Check.Eq(rig.B.RebuffLineSec(), 420, "idle: Rebuff With 300 s + 2 min");
        rig.Live((Strength, 400), (Willpower, 3000));   // 400 s: over 300, inside 420
        Check.True(rig.B.NeedsAnyBuff(), "Strength is due for an early top-off");
        Check.Eq(rig.Arbiter(), BotActivity.Buffing, "buffing takes the idle tick");
        Check.Eq(Show(rig.Beats(5)), Strength, "Strength is topped off (Willpower, 3000 s left, is left alone)");
        Check.Eq(rig.B.IdleTopOffRuns, 1, "one early top-off run");
        Check.Eq(rig.Logged("[Buff] Rebuff When Idle: idle, and a buff has under 420 s left"), 1, "logged");
        Check.False(rig.Running, "the run ends");
        Check.False(rig.B.NeedsAnyBuff(), "nothing due afterwards");

        // Just over the margin: nothing yet.
        var over = new Rig(rebuffWhenIdle: true);
        over.Live((Strength, 430), (Willpower, 3000));
        Check.Eq(Show(over.Beats(5)), "(none)", "430 s left, over the 420 s line: not yet");

        // Also Refresh Under at or below Rebuff With: the early run still casts the buff that started it, once.
        var narrow = new Rig(rebuffWhenIdle: true);
        narrow.S.RebuffTopOffSecondsRemaining = 300;
        narrow.Live((Strength, 400), (Willpower, 3000));
        Check.Eq(Show(narrow.Beats(8)), Strength, "the triggering buff is in the run, no loop");
        Check.Eq(narrow.B.IdleTopOffRuns, 1, "one run");
    }

    private static void FightInsideMarginWaits()
    {
        var rig = new Rig(rebuffWhenIdle: true) { Idle = false };
        Check.Eq(rig.B.RebuffLineSec(), 300, "in a fight: Rebuff With only");
        rig.Live((Strength, 400), (Willpower, 3000));
        Check.False(rig.B.NeedsAnyBuff(), "400 s left in a fight: not due");
        Check.Eq(rig.Arbiter(), BotActivity.Combat, "combat keeps the tick");
        Check.Eq(Show(rig.Beats(10)), "(none)", "no early top-off in a fight");
        Check.Eq(rig.B.IdleTopOffRuns, 0, "none started");

        rig.Idle = true;
        Check.Eq(Show(rig.Beats(5)), Strength, "the fight over: topped off then");
    }

    private static void FightRegularRebuff()
    {
        var low = new Rig(rebuffWhenIdle: true) { Idle = false };
        low.Live((Strength, 290), (Willpower, 3000));   // under Rebuff With 300
        Check.True(low.B.NeedsAnyBuff(), "290 s left: due, fight or not");
        Check.Eq(low.Arbiter(), BotActivity.Buffing, "buffing takes the tick, as before");
        Check.Eq(Show(low.Beats(5)), Strength, "rebuffed in the fight");
        Check.Eq(low.B.IdleTopOffRuns, 0, "a regular run, not an early top-off");

        var dispel = new Rig(rebuffWhenIdle: true);
        dispel.Live((Strength, 3000), (Willpower, 3000));
        dispel.Beats(2);
        dispel.Idle = false;
        dispel.Dispel(Strength);
        Check.Eq(Show(dispel.Beats(5)), Strength, "a buff dispelled mid-fight is recast at once, as before");

        var off = new Rig(rebuffWhenIdle: false) { Idle = false };
        off.Live((Strength, 290), (Willpower, 3000));
        Check.Eq(Show(off.Beats(5)), Strength, "the same with the option off");
    }

    private static void MarginZeroAddsNothing()
    {
        var zero = new Rig(rebuffWhenIdle: true, marginMinutes: 0);
        Check.Eq(zero.B.RebuffLineSec(), 300, "margin 0: Rebuff With only, idle too");
        zero.Live((Strength, 400), (Willpower, 3000));
        Check.Eq(Show(zero.Beats(5)), "(none)", "margin 0: 400 s left is not due");
        zero.Live((Strength, 290), (Willpower, 3000));
        Check.Eq(Show(zero.Beats(5)), Strength, "the regular rebuff still goes");
        Check.Eq(zero.B.IdleTopOffRuns, 0, "never an early top-off");

        var off = new Rig(rebuffWhenIdle: false, marginMinutes: 2);
        Check.Eq(off.B.RebuffLineSec(), 300, "option off: the margin is not read");
        off.Live((Strength, 400), (Willpower, 3000));
        Check.Eq(Show(off.Beats(5)), "(none)", "option off: not due early");

        var negative = new Rig(rebuffWhenIdle: true, marginMinutes: -5);
        Check.Eq(negative.B.RebuffLineSec(), 300, "a negative margin counts as 0");
    }

    private static void EarlyRunStopsForFight()
    {
        var rig = new Rig(rebuffWhenIdle: true);
        rig.Live((Strength, 400), (Willpower, 400));   // both inside the margin: one early run
        string first = rig.Beat(answer: false);
        Check.True(first == Strength || first == Willpower, $"idle: the early run starts ('{first}', hold: {rig.B.LastBuffSkipReason})");
        Check.True(rig.Running, "the run is under way");

        rig.Idle = false;   // a monster comes
        Check.Eq(rig.Arbiter(), BotActivity.Buffing, "the cast in flight keeps the slot until it resolves");
        rig.Answer(first);
        Check.Eq(rig.Beat(), "", "the next heartbeat casts nothing");
        Check.False(rig.Running, "the run stopped at that safe point (before the arbiter reads it)");
        Check.False(rig.B.NeedsAnyBuff(), "the early run no longer holds the tick");
        Check.Eq(rig.Arbiter(), BotActivity.Combat, "combat has it");
        Check.Eq(Show(rig.Beats(10)), "(none)", "nothing more is cast in the fight");
        Check.Eq(rig.Logged("the early top-off stops"), 1, "logged once");

        // The regular rules take over: the other buff dropping under Rebuff With is rebuffed in the fight.
        string other = first == Strength ? Willpower : Strength;
        rig.Live((first, 2700), (other, 290));
        Check.Eq(Show(rig.Beats(5)), other, "under Rebuff With in the fight: rebuffed as usual");

        // And an early run interrupted with the other buff still inside the margin finishes when idle again.
        var again = new Rig(rebuffWhenIdle: true);
        again.Live((Strength, 400), (Willpower, 400));
        string a = again.Beat(answer: false);
        again.Idle = false;
        again.Answer(a);
        again.Beats(5);
        again.Idle = true;
        Check.Eq(Show(again.Beats(5)), a == Strength ? Willpower : Strength, "idle again: the rest is topped off");
        Check.Eq(again.B.IdleTopOffRuns, 2, "a second early run");
    }

    private static void ForceBuffUnchanged()
    {
        var rig = new Rig(rebuffWhenIdle: true) { Idle = false };
        rig.Live((Strength, 3000), (Willpower, 3000));
        rig.B.RequestForceFullRebuff();
        var sent = rig.Beats(10);
        Check.True(sent.Contains(Strength) && sent.Contains(Willpower), "Force Buff recasts everything, in a fight too: " + Show(sent));
        Check.False(rig.Running, "and ends");
    }

    private static void Setting()
    {
        var fresh = new LegacyUiSettings();
        Check.False(fresh.RebuffWhenIdle, "a new profile: Rebuff When Idle off");
        Check.Eq(fresh.RebuffIdleMarginMinutes, 2, "the margin defaults to 2 minutes");
        var old = JsonSerializer.Deserialize("{\"EnableBuffing\":true,\"RebuffWhenIdle\":true,\"RebuffSecondsRemaining\":300}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.RebuffWhenIdle && old.RebuffIdleMarginMinutes == 2, "a profile saved before the margin: 2");
        var saved = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { RebuffWhenIdle = true, RebuffIdleMarginMinutes = 0 }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(saved != null && saved.RebuffWhenIdle && saved.RebuffIdleMarginMinutes == 0, "saved and loaded (0 stays 0)");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { RebuffIdleMarginMinutes = 7 }, dst });
        Check.Eq(dst.RebuffIdleMarginMinutes, 7, "a profile switch copies it");

        // The engine's Settings face: sent; a save without it (an engine that doesn't know it) leaves it alone.
        var s = new LegacyUiSettings { RebuffIdleMarginMinutes = 5 };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent?.RebuffIdleMarginMinutes == 5, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.Eq(s.RebuffIdleMarginMinutes, 5, "a save without it leaves it alone");
        dash.ApplySettingsJson("{\"RebuffIdleMarginMinutes\":0}");
        Check.Eq(s.RebuffIdleMarginMinutes, 0, "a save with 0 sets 0");

        var live = new LegacyUiSettings();
        var map = ExpressionTests.Engine(settings: live).BuildSettingsMapPublic();
        Check.True(map.TryGetValue("RebuffIdleMarginMinutes", out var entry), "RebuffIdleMarginMinutes is in the settings map");
        Check.Eq(entry.Get(), "2", "reads 2");
        entry.Set("4");
        Check.Eq(live.RebuffIdleMarginMinutes, 4, "and sets it");
        Check.True(map.TryGetValue("RebuffWhenIdle", out var on), "RebuffWhenIdle is in the settings map");
        on.Set("1");
        Check.True(live.RebuffWhenIdle, "and sets it on");

        // VTank import: idlebufftopoff is the option; VTank has no time field for it in our tables, so the
        // margin keeps its default on import.
        Check.True(MetaManager.TryMapVtOption("idlebufftopoff", out string ra) && ra == "RebuffWhenIdle", "VTank's idlebufftopoff imports Rebuff When Idle");
    }
}
