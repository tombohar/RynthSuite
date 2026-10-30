using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// SilentKelpie's combat additions (RynthSuite PR 12, taken 2026-10-19), through CombatManager and
// the settings bridges rather than the pure planners (EnvironmentImpactTests has those):
//  - per-monster attack power reaches the attack the server is sent, and with no rule asking for
//    one the global Melee/Missile Power is sent exactly as before;
//  - the per-monster Kite choice (Default / Off / PullAway) is kept by the Monsters bridge, an
//    older Monsters panel can't wipe it, and with nothing set combat never runs or turns;
//  - the new settings ride the Settings bridge, an engine without them leaves them alone;
//  - the environment-hit throttle trips only after N hits inside its window.
internal static class SkCombatAddonTests
{
    private const uint Player = 0x50000F01;
    private const uint Lb = 0xA9B40000;
    private const uint A = 0x80000401, B = 0x80000402;
    private const string MissileLine = "Your missile attack hit the environment.";

    public static void Register(Runner r)
    {
        r.Add("sk combat: no rule asks for a power: the global Melee Power is sent exactly as before", PowerDefaultsUnchanged);
        r.Add("sk combat: a rule's Power reaches the attack sent to the server (High, Reckless, Variable)", PowerReachesAttack);
        r.Add("sk combat: Reckless needs Recklessness trained; untrained it sends High, Reckless + Variable sends Variable", PowerRespectsTraining);
        r.Add("sk combat: Variable power changes attack to attack and stays inside Min..Max", PowerVariableRange);
        r.Add("sk combat: Kite Default/Off never runs or turns; PullAway tags (or not), runs, and gives up when it makes no ground", KiteFlow);
        r.Add("sk combat: the Kite and Power choices survive the Monsters bridge; an older panel can't wipe them", KiteBridgePersisted);
        r.Add("sk combat: the new settings ride the Settings bridge; an engine without them leaves them alone", SettingsBridge);
        r.Add("sk combat: every new setting is off / unchanged by default", SettingsDefaults);
        r.Add("sk combat: environment hits trip only after N inside the window; older ones age out; a response then does what it says", EnvironmentWindow);
    }

    // ── A melee rig ──────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager C;
        public readonly WorldObjectCache Cache;
        public DateTime Now = new(2026, 10, 19, 12, 0, 0);

        public Rig(int mode, Action<LegacyUiSettings> set, params (uint Id, string Name, float X, float Y)[] mobs)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.AttackSendCalls = true;
            FakeHost.CombatModeValue = mode;
            Place(Player, "Me", 96, 96, creature: false);
            foreach (var m in mobs) Place(m.Id, m.Name, m.X, m.Y);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.HeadingDeg = 0;
            S = new LegacyUiSettings { MonsterRange = 50, IsMacroRunning = true, EnableCombat = true };
            set(S);
            var host = FakeHost.Create(Player);
            Cache = FakeHost.MakeCache(host, Player, mobs.Select(m => m.Id));
            C = new CombatManager(host, S, Cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            C.EnvClock = () => Now;
            C.KiteClock = () => Now;
            C.ScanNearbyTargets();
            C.HandleCombatTrigger();
            FakeHost.AttackCalls.Clear();
        }

        /// <summary>Thinks until an attack reaches the server (false if none does in 30 ticks).</summary>
        public bool ThinkToAttack()
        {
            int before = FakeHost.AttackCalls.Count;
            for (int i = 0; i < 30 && FakeHost.AttackCalls.Count == before; i++)
            {
                LastAttackCmd.SetValue(C, DateTime.MinValue);   // the attack cadence runs on the real clock
                C.Think();
                Now = Now.AddMilliseconds(400);
            }
            return FakeHost.AttackCalls.Count > before;
        }

        public float LastPower => FakeHost.AttackCalls[^1].Power;

        private static readonly FieldInfo LastAttackCmd = typeof(CombatManager).GetField("lastAttackCmd", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static readonly (uint, string, float, float) Drudge = (A, "Drudge Skulker", 96f, 99f);   // 3 m north

    private static MonsterRule Default(string power = "Default", string kite = "Default") =>
        new() { Name = "Default", AttackPower = power, Kite = kite };

    // ── Attack power ─────────────────────────────────────────────────────────

    private static void PowerDefaultsUnchanged()
    {
        // Auto power, no Recklessness: 100%.
        var auto = new Rig(CombatMode.Melee, s => s.MonsterRules = new List<MonsterRule> { Default() }, Drudge);
        Check.True(auto.ThinkToAttack(), "auto: an attack goes out");
        Check.Near(auto.LastPower, 1.0, 1e-6, "auto: 100% as before");
        Check.Eq(FakeHost.AttackCalls[^1].Kind, "melee", "it is a melee attack");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[AttackPower]")), "auto: nothing logged about a power choice");

        // Auto + Use Recklessness, trained: 80% as before.
        var reck = new Rig(CombatMode.Melee, s => { s.UseRecklessness = true; s.MonsterRules = new List<MonsterRule> { Default() }; }, Drudge);
        reck.ThinkToAttack();
        Check.Near(reck.LastPower, 0.8, 1e-6, "auto + Use Recklessness (trained): 80% as before");

        // A set power: that power.
        var set = new Rig(CombatMode.Melee, s => { s.MeleeAttackPower = 65; s.MonsterRules = new List<MonsterRule> { Default() }; }, Drudge);
        set.ThinkToAttack();
        Check.Near(set.LastPower, 0.65, 1e-6, "a set Melee Power: that power");

        // No rules at all: the same.
        var none = new Rig(CombatMode.Melee, s => { s.MeleeAttackPower = 40; s.MonsterRules = new List<MonsterRule>(); }, Drudge);
        none.ThinkToAttack();
        Check.Near(none.LastPower, 0.4, 1e-6, "no rules: the global setting");

        // A new rule and a saved-before one are Default.
        Check.Eq(new MonsterRule().AttackPower, "Default", "a new rule is Default power");
        Check.Eq(new MonsterRule().Kite, "Default", "a new rule is Default kite");
        Check.Eq(JsonSerializer.Deserialize("{\"Name\":\"Old\"}", RynthAiJsonContext.Default.MonsterRule)!.AttackPower, "Default", "a rule saved before loads as Default power");
    }

    private static void PowerReachesAttack()
    {
        Rig R(MonsterRule rule, MonsterRule? def = null, Action<LegacyUiSettings>? more = null) =>
            new(CombatMode.Melee, s => { s.MonsterRules = new List<MonsterRule> { def ?? Default(), rule }; more?.Invoke(s); }, Drudge);

        var high = R(new MonsterRule { Name = "Drudge Skulker", AttackPower = "High" }, more: s => s.MeleeAttackPower = 30);
        high.ThinkToAttack();
        Check.Near(high.LastPower, 1.0, 1e-6, "High: 100% whatever the global");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[AttackPower]") && l.Contains("High")), "the first attack on it is logged");

        var reckless = R(new MonsterRule { Name = "Drudge Skulker", AttackPower = "Reckless" }, more: s => s.MeleeAttackPower = 30);
        reckless.ThinkToAttack();
        Check.Near(reckless.LastPower, 0.8, 1e-6, "Reckless: 80%");

        // The DEFAULT line's choice covers a Default rule; a rule's own wins.
        var viaDefault = R(new MonsterRule { Name = "Drudge Skulker" }, def: Default("Reckless"));
        viaDefault.ThinkToAttack();
        Check.Near(viaDefault.LastPower, 0.8, 1e-6, "a Default rule follows the DEFAULT line");
        var own = R(new MonsterRule { Name = "Drudge Skulker", AttackPower = "High" }, def: Default("Reckless"));
        own.ThinkToAttack();
        Check.Near(own.LastPower, 1.0, 1e-6, "a rule's own choice wins over the DEFAULT line");

        // Another monster's rule doesn't touch this one.
        var other = R(new MonsterRule { Name = "Banderling", AttackPower = "Reckless" }, more: s => s.MeleeAttackPower = 55);
        other.ThinkToAttack();
        Check.Near(other.LastPower, 0.55, 1e-6, "a rule for another monster: the global power");
    }

    private static void PowerRespectsTraining()
    {
        const uint Reckl = 50;   // AcSkillType.Recklessness
        FakeHost.Reset();
        // Trained (2) and specialized (3): the reckless band applies.
        foreach (int training in new[] { 2, 3 })
        {
            var rig = new Rig(CombatMode.Melee, s => s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", AttackPower = "Reckless" } }, Drudge);
            FakeHost.Skills[(Player, Reckl)] = (200, training);
            rig.C.SetCharacterSkills(MakeSkills());
            rig.ThinkToAttack();
            Check.Near(rig.LastPower, 0.8, 1e-6, $"Reckless with Recklessness at training {training}: 80%");
        }

        // Untrained (1): Reckless falls back to High, Reckless + Variable to Variable.
        var untrainedReckless = new Rig(CombatMode.Melee, s => s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", AttackPower = "Reckless" } }, Drudge);
        FakeHost.Skills[(Player, Reckl)] = (10, 1);
        untrainedReckless.C.SetCharacterSkills(MakeSkills());
        untrainedReckless.ThinkToAttack();
        Check.Near(untrainedReckless.LastPower, 1.0, 1e-6, "Reckless, Recklessness untrained: High (100%)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[AttackPower]") && l.Contains("untrained")), "and the log says why");

        var untrainedBoth = new Rig(CombatMode.Melee, s =>
        {
            s.VariablePowerMin = 30; s.VariablePowerMax = 100;
            s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", AttackPower = "Reckless + Variable" } };
        }, Drudge);
        FakeHost.Skills[(Player, Reckl)] = (10, 1);
        untrainedBoth.C.SetCharacterSkills(MakeSkills());
        untrainedBoth.C.PowerRandom = () => 0.999;
        untrainedBoth.ThinkToAttack();
        Check.Near(untrainedBoth.LastPower, 1.0, 1e-6, "Reckless + Variable, untrained: plain Variable (up to Max, past the 80% band)");

        // The global "Use Recklessness" with Auto power follows the training too.
        var autoUntrained = new Rig(CombatMode.Melee, s => { s.UseRecklessness = true; s.MonsterRules = new List<MonsterRule> { Default() }; }, Drudge);
        FakeHost.Skills[(Player, Reckl)] = (10, 1);
        autoUntrained.C.SetCharacterSkills(MakeSkills());
        autoUntrained.ThinkToAttack();
        Check.Near(autoUntrained.LastPower, 1.0, 1e-6, "Use Recklessness, untrained, Auto power: 100% as before");

        static RynthCore.Plugin.RynthAi.CharacterSkills MakeSkills()
        {
            var sk = new RynthCore.Plugin.RynthAi.CharacterSkills(FakeHost.Create(Player));
            sk.SetPlayerId(Player);
            return sk;
        }
    }

    private static void PowerVariableRange()
    {
        var rig = new Rig(CombatMode.Melee, s =>
        {
            s.VariablePowerMin = 60; s.VariablePowerMax = 90;
            s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", AttackPower = "Variable" } };
        }, Drudge);
        var draws = new Queue<double>(new[] { 0.0, 0.5, 0.999, 0.25, 0.75 });
        rig.C.PowerRandom = () => draws.Count > 0 ? draws.Dequeue() : 0.5;

        var seen = new List<float>();
        for (int i = 0; i < 5; i++)
        {
            Check.True(rig.ThinkToAttack(), $"attack {i + 1} goes out");
            seen.Add(rig.LastPower);
        }
        Check.True(seen.All(p => p >= 0.60f - 1e-6 && p <= 0.90f + 1e-6), "every attack inside 60..90%: " + string.Join(", ", seen.Select(p => p.ToString("0.00"))));
        Check.True(seen.Distinct().Count() >= 4, "and it differs from attack to attack");
        Check.Near(seen[0], 0.60, 1e-6, "a draw of 0 sends Min");
        Check.Near(seen[2], 0.90, 1e-6, "a draw near 1 sends Max");

        // Reckless + Variable stays in the 20..80 band and the range.
        var both = new Rig(CombatMode.Melee, s =>
        {
            s.VariablePowerMin = 60; s.VariablePowerMax = 100;
            s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", AttackPower = "Reckless + Variable" } };
        }, Drudge);
        both.C.PowerRandom = () => 0.999;
        both.ThinkToAttack();
        Check.Near(both.LastPower, 0.80, 1e-6, "Reckless + Variable: capped by the band's 80%");
        both.C.PowerRandom = () => 0.0;
        both.ThinkToAttack();
        Check.Near(both.LastPower, 0.60, 1e-6, "Reckless + Variable: floored by the range's Min (60%)");
    }

    // ── Kite ─────────────────────────────────────────────────────────────────

    private static void KiteFlow()
    {
        // Default and Off: combat attacks where the mob stands, never runs or turns away.
        foreach (string kite in new[] { "Default", "Off" })
        {
            var rig = new Rig(CombatMode.Melee, s => s.MonsterRules = new List<MonsterRule> { Default(kite: kite) }, Drudge);
            rig.ThinkToAttack();
            Check.False(FakeHost.AutoRunCalls.Any(on => on), $"Kite {kite}: never autoruns");
            Check.False(rig.C.KiteActive, $"Kite {kite}: not kiting");
            Check.False(FakeHost.Logs.Any(l => l.Contains("[Kite]")), $"Kite {kite}: nothing logged");
        }

        // PullAway without tagging: runs at once, no attack while running.
        var run = new Rig(CombatMode.Melee, s =>
        {
            s.KitePullMeters = 20; s.KiteTagFirst = false;
            s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", Kite = "PullAway" } };
        }, Drudge);
        run.C.Think();
        Check.True(run.C.KiteActive, "PullAway: the pull starts");
        Check.True(FakeHost.AutoRun, "it autoruns");
        Check.Eq(FakeHost.AttackCalls.Count, 0, "and sends no attack while it runs");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Kite] pull-away start")), "logged");
        Check.Near(FakeHost.HeadingDeg, 180.0, 1.0, "facing away from the mob (it is north, heading 0)");

        // The mob never follows: after the no-progress time the pull gives up and combat attacks in place.
        run.Now = run.Now.AddSeconds(5);
        run.C.Think();
        Check.False(run.C.KiteActive, "no ground gained for 4 s: the pull gives up");
        Check.False(FakeHost.AutoRun, "autorun off");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Kite] pull-away done") && l.Contains("no progress")), "logged as no progress");
        Check.True(run.ThinkToAttack(), "and it attacks where the mob stands");
        run.C.Think();
        Check.False(run.C.KiteActive, "it doesn't start a second pull on the same mob");

        // PullAway with Tag Before Fleeing: attacks first (aggro sticks), runs once tagged or after 2.5 s.
        var tag = new Rig(CombatMode.Melee, s =>
        {
            s.KiteTagFirst = true;
            s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", Kite = "PullAway" } };
        }, Drudge);
        Check.True(tag.ThinkToAttack(), "tag first: the attack goes out");
        Check.True(tag.C.KiteActive, "... with the pull begun");
        Check.False(FakeHost.AutoRun, "... and no running yet");
        tag.Now = tag.Now.AddSeconds(3);
        tag.C.Think();
        Check.True(FakeHost.AutoRun, "after the tag window it runs");
        Check.True(FakeHost.Logs.Any(l => l.Contains("fleeing (tag timed out)")), "logged as fleeing after the tag");

        // Pulled far enough: the mob moves away from where it stood.
        var pull = new Rig(CombatMode.Melee, s =>
        {
            s.KitePullMeters = 10; s.KiteTagFirst = false;
            s.MonsterRules = new List<MonsterRule> { Default(), new MonsterRule { Name = "Drudge Skulker", Kite = "PullAway" } };
        }, Drudge);
        pull.C.Think();
        Check.True(pull.C.KiteActive, "second mob: pull starts");
        FakeHost.Positions[A] = (Lb | 0x1C, 96f, 112f, 0f);      // dragged 13 m from where it stood
        pull.Now = pull.Now.AddMilliseconds(500);
        pull.C.Think();
        Check.False(pull.C.KiteActive, "dragged past the pull distance: done");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Kite] pull-away done") && l.Contains("pulled")), "logged as pulled");

        // The macro stopping, or the target dropping, ends a pull and lets go of autorun.
        var stop = new Rig(CombatMode.Melee, s =>
        {
            s.KiteTagFirst = false;
            s.MonsterRules = new List<MonsterRule> { Default(kite: "PullAway") };
        }, Drudge);
        stop.C.Think();
        Check.True(stop.C.KiteActive && FakeHost.AutoRun, "DEFAULT line PullAway covers every monster");
        // Think stops driving it (another activity took over): the pump-thread watch lets go.
        stop.Now = stop.Now.AddSeconds(2);
        stop.C.PollKite();
        Check.False(stop.C.KiteActive, "Think stopped driving the pull for 1.5 s: it ends");
        Check.False(FakeHost.AutoRun, "autorun released");

        // The macro stopping ends one too (the heartbeat sees the transition).
        var macro = new Rig(CombatMode.Melee, s =>
        {
            s.KiteTagFirst = false;
            s.MonsterRules = new List<MonsterRule> { Default(kite: "PullAway") };
        }, Drudge);
        macro.C.OnHeartbeat();
        macro.C.Think();
        Check.True(macro.C.KiteActive && FakeHost.AutoRun, "pull under way");
        macro.S.IsMacroRunning = false;
        macro.C.OnHeartbeat();
        Check.False(macro.C.KiteActive, "macro stopped: the pull ends");
        Check.False(FakeHost.AutoRun, "autorun released");
    }

    // ── Bridges ──────────────────────────────────────────────────────────────

    private static LegacyDashboardRenderer Dash(LegacyUiSettings s)
    {
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        return dash;
    }

    private static void KiteBridgePersisted()
    {
        var s = new LegacyUiSettings
        {
            MonsterRules = new List<MonsterRule>
            {
                new() { Name = "Default", Kite = "Off", AttackPower = "High" },
                new() { Name = "Drudge", MatchExpression = "x", Kite = "PullAway", AttackPower = "Reckless + Variable" },
            },
        };

        // Saved file: both choices round-trip.
        string saved = JsonSerializer.Serialize(s.MonsterRules, RynthAiJsonContext.Default.MonsterRuleList);
        var back = JsonSerializer.Deserialize(saved, RynthAiJsonContext.Default.MonsterRuleList)!;
        Check.Eq(back[1].Kite, "PullAway", "the Kite choice is saved and loaded");
        Check.Eq(back[1].AttackPower, "Reckless + Variable", "the Power choice is saved and loaded");

        // An older Monsters panel (no Kite, no AttackPower in what it sends) keeps both.
        var dash = Dash(s);
        dash.ApplyMonstersJson("{\"rules\":[{\"Name\":\"Default\"},{\"Name\":\"Drudge\",\"MatchExpression\":\"x\",\"Priority\":3}]}");
        Check.Eq(s.MonsterRules.Count, 2, "the rules were taken");
        Check.Eq(s.MonsterRules[1].Priority, 3, "the panel's own edit was taken");
        Check.Eq(s.MonsterRules[1].Kite, "PullAway", "an older panel can't wipe a rule's Kite");
        Check.Eq(s.MonsterRules[1].AttackPower, "Reckless + Variable", "an older panel can't wipe a rule's Power");
        Check.Eq(s.MonsterRules[0].Kite, "Off", "nor the DEFAULT line's");

        // A panel that has the columns is believed.
        dash.ApplyMonstersJson("{\"rules\":[{\"Name\":\"Default\",\"Kite\":\"Default\",\"AttackPower\":\"Default\"},{\"Name\":\"Drudge\",\"MatchExpression\":\"x\",\"Kite\":\"Off\",\"AttackPower\":\"High\"}]}");
        Check.Eq(s.MonsterRules[1].Kite, "Off", "a newer panel's Kite is taken");
        Check.Eq(s.MonsterRules[1].AttackPower, "High", "a newer panel's Power is taken");
        Check.Eq(s.MonsterRules[0].Kite, "Default", "including a change back to Default");

        // The VTank importer's rule copy keeps both.
        var src = new MonsterRule { Name = "X", Kite = "PullAway", AttackPower = "Variable" };
        var field = typeof(RynthCore.Plugin.RynthAi.ProfileImport.VirindiTankProfileImporter).GetMethod("CloneMonsterRule", BindingFlags.NonPublic | BindingFlags.Static);
        if (field != null)
        {
            var clone = (MonsterRule)field.Invoke(null, new object[] { src })!;
            Check.Eq(clone.Kite, "PullAway", "the importer's clone keeps Kite");
            Check.Eq(clone.AttackPower, "Variable", "the importer's clone keeps Power");
        }
        else Check.True(false, "CloneMonsterRule not found");
    }

    private static void SettingsDefaults()
    {
        var s = new LegacyUiSettings();
        Check.Eq(s.MissileEnvironmentResponse, 0, "Missile Response: Off");
        Check.Eq(s.MagicEnvironmentResponse, 0, "Magic Response: Off");
        Check.Eq(s.VariablePowerMin, 50, "Variable Min 50");
        Check.Eq(s.VariablePowerMax, 100, "Variable Max 100");
        Check.Eq(s.EnvironmentImpactLimit, 2, "Hits to trip 2");
        Check.Eq(s.EnvironmentImpactWindowSec, 20, "Window 20 s");
        Check.Eq(s.EnvironmentApproachMeters, 6, "Move closer by 6 m");
        Check.Eq(s.EnvironmentMinDistance, 3, "Closest 3 m");
        Check.Eq(s.EnvironmentSkipSec, 30, "Skip target 30 s");
        Check.Eq(s.KitePullMeters, 20, "Pull distance 20 m");
        Check.True(s.KiteTagFirst, "Tag before fleeing on");
        Check.True(s.MonsterRules.All(r => r.Kite == "Default" && r.AttackPower == "Default"), "no stock rule asks for a power or a kite");
        Check.Eq(KiteModes.Effective(new MonsterRule { Name = "Default" }, new MonsterRule { Name = "Default" }), KiteModes.Off, "Default kite on the DEFAULT line is Off");
    }

    private static void SettingsBridge()
    {
        var s = new LegacyUiSettings
        {
            VariablePowerMin = 40, VariablePowerMax = 90, MissileEnvironmentResponse = 2, MagicEnvironmentResponse = 3,
            EnvironmentImpactLimit = 4, EnvironmentImpactWindowSec = 45, EnvironmentApproachMeters = 8, EnvironmentMinDistance = 5,
            EnvironmentSkipSec = 60, KitePullMeters = 33, KiteTagFirst = false,
        };
        var dash = Dash(s);

        // Sent to the engine face, nullable on the wire.
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(sent.VariablePowerMin == 40 && sent.VariablePowerMax == 90, "Variable range sent");
        Check.True(sent.MissileEnvironmentResponse == 2 && sent.MagicEnvironmentResponse == 3, "responses sent");
        Check.True(sent.EnvironmentImpactLimit == 4 && sent.EnvironmentImpactWindowSec == 45 && sent.EnvironmentApproachMeters == 8
                   && sent.EnvironmentMinDistance == 5 && sent.EnvironmentSkipSec == 60, "throttle numbers sent");
        Check.True(sent.KitePullMeters == 33 && sent.KiteTagFirst == false, "kite settings sent");

        // An engine without the rows saves without them: nothing changes.
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.VariablePowerMin == 40 && s.VariablePowerMax == 90 && s.MissileEnvironmentResponse == 2 && s.MagicEnvironmentResponse == 3, "an older engine's save leaves power and responses alone");
        Check.True(s.EnvironmentImpactLimit == 4 && s.EnvironmentImpactWindowSec == 45 && s.EnvironmentSkipSec == 60, "... and the throttle numbers");
        Check.True(s.KitePullMeters == 33 && !s.KiteTagFirst, "... and the kite settings");

        // The same save with explicit nulls (what the engine's mirror sends for a value it never got).
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false,\"VariablePowerMin\":null,\"KitePullMeters\":null,\"KiteTagFirst\":null,\"MagicEnvironmentResponse\":null}");
        Check.True(s.VariablePowerMin == 40 && s.KitePullMeters == 33 && !s.KiteTagFirst && s.MagicEnvironmentResponse == 3, "nulls leave the values alone");

        // An engine with the rows: its values are taken, kite distance clamped.
        dash.ApplySettingsJson("{\"VariablePowerMin\":10,\"VariablePowerMax\":20,\"MissileEnvironmentResponse\":1,\"KitePullMeters\":500,\"KiteTagFirst\":true,\"EnvironmentSkipSec\":15}");
        Check.True(s.VariablePowerMin == 10 && s.VariablePowerMax == 20, "a newer engine's Variable range is taken");
        Check.Eq(s.MissileEnvironmentResponse, 1, "its Missile Response is taken");
        Check.Eq(s.KitePullMeters, 100, "the pull distance is clamped to 100");
        Check.True(s.KiteTagFirst, "Tag before fleeing is taken");
        Check.Eq(s.EnvironmentSkipSec, 15, "Skip target is taken");
    }

    // ── Environment hits: the window ─────────────────────────────────────────

    private static void EnvironmentWindow()
    {
        var skulker = (A, "Drudge Skulker", 96f, 116f);
        var slinker = (B, "Drudge Slinker", 120f, 96f);

        // Two hits 25 s apart (window 20 s): the first aged out, the limit is not reached.
        var r = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 2; s.EnvironmentImpactLimit = 2; s.EnvironmentImpactWindowSec = 20; }, skulker, slinker);
        r.C.HandleChatForDamage(MissileLine);
        r.Now = r.Now.AddSeconds(25);
        r.C.HandleChatForDamage(MissileLine);
        Check.False(r.C.RunEnvironmentImpact(r.Cache[unchecked((int)A)]!, 20), "two hits 25 s apart do not trip a 20 s window");
        Check.Eq((uint)r.C.activeTargetId, A, "still on the Skulker");
        r.Now = r.Now.AddSeconds(5);
        r.C.HandleChatForDamage(MissileLine);
        Check.True(r.C.RunEnvironmentImpact(r.Cache[unchecked((int)A)]!, 20), "a second hit inside the window trips it");
        Check.Eq(r.C.activeTargetId, 0, "New target: the Skulker is dropped");

        // Move closer, then new target: the first trip approaches, the next switches (another monster is in reach).
        var c = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 3; s.EnvironmentImpactLimit = 1; s.EnvironmentApproachMeters = 6; s.EnvironmentMinDistance = 3; }, skulker, slinker);
        c.C.HandleChatForDamage(MissileLine);
        Check.True(c.C.RunEnvironmentImpact(c.Cache[unchecked((int)A)]!, 20), "Move closer then new: the first trip takes the tick");
        Check.True(FakeHost.AutoRun && c.C.EnvironmentApproachActive, "and approaches");
        Check.Eq((uint)c.C.activeTargetId, A, "keeping the target");
        c.Now = c.Now.AddSeconds(1);
        FakeHost.PlayerPose = (Lb | 0x1C, 96f, 102.5f, 0f);
        FakeHost.Positions[Player] = FakeHost.PlayerPose;
        c.C.RunEnvironmentImpact(c.Cache[unchecked((int)A)]!, 13.5);
        Check.False(c.C.EnvironmentApproachActive, "the approach ends at its goal");
        c.C.HandleChatForDamage(MissileLine);
        Check.True(c.C.RunEnvironmentImpact(c.Cache[unchecked((int)A)]!, 13.5), "the next trip");
        Check.Eq(c.C.activeTargetId, 0, "switches target after the one approach");

        // The only monster in reach is never skipped, even on New target.
        var one = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 2; s.EnvironmentImpactLimit = 1; s.EnvironmentMinDistance = 3; }, (A, "Drudge Skulker", 96f, 98.5f));
        one.C.HandleChatForDamage(MissileLine);
        Check.False(one.C.RunEnvironmentImpact(one.Cache[unchecked((int)A)]!, 2.5), "the only monster, already close: attacking on");
        Check.Eq((uint)one.C.activeTargetId, A, "never skipped");
    }
}
