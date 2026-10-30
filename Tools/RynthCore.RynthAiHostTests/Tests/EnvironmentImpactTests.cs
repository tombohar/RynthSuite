using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Environment-impact throttle and per-monster attack power (2026-10-12). Arrows into a wall
// ("Your missile attack hit the environment.") or war bolts stopping short of the target count
// against that target; at the limit within the window, combat stops attacking it and moves closer,
// switches target (this one skipped for a while), or moves closer once and then switches, as chosen
// separately for missile and magic combat. The only monster in reach is never skipped. A monster
// rule's Power picks High / Reckless / Variable / Reckless + Variable per monster.
internal static class EnvironmentImpactTests
{
    private const uint Player = 0x50000F01;
    private const uint Lb = 0xA9B40000;
    private const uint A = 0x80000401, B = 0x80000402, Bolt = 0x80000499;

    public static void Register(Runner r)
    {
        r.Add("env impact: the response decision (move closer, new target, closer then new, the only monster)", Decide);
        r.Add("env impact: hits count per target inside the window; approaches are remembered", Throttle);
        r.Add("env impact: a projectile is short only when it stopped away from the target and nearer the caster", Judge);
        r.Add("env impact: missile lines trip New target: the attack stops, the target is skipped, the next is picked", MissileNewTarget);
        r.Add("env impact: Move closer runs toward the target and stops at the goal; the only monster is never skipped", MissileApproach);
        r.Add("env impact: Off, or not in Missile mode: the line is ignored", MissileOff);
        r.Add("env impact: a player's copy of the missile line (their chat type, or their name in front) is not a hit", MissileSpoof);
        r.Add("env impact: a bolt stopping short with no hit counts; one with a hit line, or reaching the target, doesn't", MagicShort);
        r.Add("env impact: an approach Think stops driving lets go of autorun", ApproachOrphan);
        r.Add("env impact (flight 63260): 3 magic casts landing nothing -> one short real sidestep; a hit resets; 30 s apart", ResyncSidestep);
        r.Add("env impact: the throttle switching target restarts the re-sync's miss count", ResyncCountResetByThrottle);
        r.Add("env impact: moving closer drops a wanted re-sync sidestep", ResyncDroppedByApproach);
        r.Add("env impact: a re-sync sidestep holding its key finishes before the throttle runs", ResyncStepFinishesFirst);
        r.Add("env impact: a re-sync sidestep Think stops driving, or a macro stop, lets go of the key", ResyncKeyReleased);
        r.Add("attack power: Default/High/Reckless/Variable/Reckless + Variable, the DEFAULT line, untrained Recklessness", Power);
        r.Add("attack power: an engine without the Power column keeps each rule's power", PowerCarriedOver);
    }

    // ── Pure rules ───────────────────────────────────────────────────────────

    private static void Decide()
    {
        var M = EnvironmentResponse.MoveCloser; var N = EnvironmentResponse.NewTarget; var C = EnvironmentResponse.CloserThenNewTarget;
        EnvironmentAction D(EnvironmentResponse r, int done, double dist, bool alt) => EnvironmentImpactPlanner.Decide(r, done, dist, 3, alt);

        Check.Eq(D(EnvironmentResponse.Off, 0, 20, true), EnvironmentAction.None, "Off: nothing");
        Check.Eq(D(M, 0, 20, true), EnvironmentAction.Approach, "Move closer, far: approach");
        Check.Eq(D(M, 3, 20, false), EnvironmentAction.Approach, "Move closer approaches every time it trips");
        Check.Eq(D(M, 0, 3.2, true), EnvironmentAction.NewTarget, "Move closer, already at the closest: new target");
        Check.Eq(D(M, 0, 3.2, false), EnvironmentAction.None, "... but not the only monster: keep attacking");
        Check.Eq(D(N, 0, 20, true), EnvironmentAction.NewTarget, "New target with another in reach: switch");
        Check.Eq(D(N, 0, 20, false), EnvironmentAction.Approach, "New target, nothing else: approach instead of idling");
        Check.Eq(D(N, 0, 2, false), EnvironmentAction.None, "New target, nothing else, already close: keep attacking");
        Check.Eq(D(C, 0, 20, true), EnvironmentAction.Approach, "Closer then new: the first trip approaches");
        Check.Eq(D(C, 1, 14, true), EnvironmentAction.NewTarget, "... the second switches");
        Check.Eq(D(C, 1, 14, false), EnvironmentAction.Approach, "... unless it's the only monster");
        Check.Eq(D(M, 0, double.NaN, true), EnvironmentAction.NewTarget, "unknown distance: no approach");

        Check.Near(EnvironmentImpactPlanner.ApproachGoal(20, 6, 3), 14, 1e-9, "goal: 6 m nearer");
        Check.Near(EnvironmentImpactPlanner.ApproachGoal(7, 6, 3), 3, 1e-9, "goal: never inside the closest distance");
        Check.Eq(EnvironmentImpactPlanner.Parse(9), EnvironmentResponse.Off, "an out-of-range saved value is Off");
        Check.Eq(EnvironmentImpactPlanner.Name(3), "Move closer, then new target", "labels follow the enum");
    }

    private static void Throttle()
    {
        var t = new EnvironmentImpactThrottle();
        var t0 = new DateTime(2026, 10, 12, 12, 0, 0);
        Check.Eq(t.Record(1, t0, 20), 1, "first hit");
        Check.Eq(t.Record(2, t0, 20), 1, "per target");
        Check.Eq(t.Record(1, t0.AddSeconds(5), 20), 2, "second hit inside the window");
        Check.Eq(t.Count(1, t0.AddSeconds(21), 20), 1, "the first hit ages out");
        Check.Eq(t.Count(1, t0.AddSeconds(30), 20), 0, "both aged out");
        t.Record(1, t0.AddSeconds(31), 20);
        t.ResetHits(1);
        Check.Eq(t.Count(1, t0.AddSeconds(31), 20), 0, "reset after acting");
        t.NoteApproach(1);
        Check.Eq(t.Approaches(1), 1, "approach remembered");
        t.Forget(1);
        Check.Eq(t.Approaches(1), 0, "a deleted id forgets everything");
        Check.Eq(t.Record(0, t0, 20), 0, "no target: not counted");
    }

    private static void Judge()
    {
        Check.True(ProjectileImpactJudge.StoppedShort(impactToTarget: 10, casterToImpact: 10, casterToTarget: 20), "into a wall halfway");
        Check.False(ProjectileImpactJudge.StoppedShort(1.5, 18.5, 20), "at the target's surface");
        Check.False(ProjectileImpactJudge.StoppedShort(3.5, 17, 20), "a big monster's surface (within 4 m)");
        Check.False(ProjectileImpactJudge.StoppedShort(6, 21, 20), "past the target (a miss beyond, not a wall in front)");
        Check.False(ProjectileImpactJudge.StoppedShort(double.NaN, 10, 20), "unknown: never short");
    }

    // ── Through CombatManager ────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager C;
        public readonly WorldObjectCache Cache;
        public DateTime Now = new(2026, 10, 12, 12, 0, 0);

        /// <summary>The player at (96, 96) facing north; <paramref name="mobs"/> placed after the reset.</summary>
        public Rig(int mode, Action<LegacyUiSettings> set, params (uint Id, string Name, float X, float Y)[] mobs)
            : this(mode, set, setMotion: false, mobs) { }

        /// <summary>As above; <paramref name="setMotion"/> installs SetMotion (the re-sync sidestep needs it).</summary>
        public Rig(int mode, Action<LegacyUiSettings> set, bool setMotion, params (uint Id, string Name, float X, float Y)[] mobs)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.SetMotionCalls = setMotion;
            FakeHost.CombatModeValue = mode;
            Place(Player, "Me", 96, 96, creature: false);
            foreach (var m in mobs) Place(m.Id, m.Name, m.X, m.Y);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.HeadingDeg = 0;
            S = new LegacyUiSettings { MonsterRange = 50, IsMacroRunning = true, EnableCombat = true };
            set(S);
            var host = FakeHost.Create(Player);   // Think measures distances from GetPlayerId
            Cache = FakeHost.MakeCache(host, Player, mobs.Select(m => m.Id));
            C = new CombatManager(host, S, Cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            C.EnvClock = () => Now;
            C.ScanNearbyTargets();
            C.HandleCombatTrigger();
            FakeHost.MotionCalls.Clear();         // the trigger's turn-motion clear-out isn't under test
        }

        public double Dist(uint id)
        {
            var p = FakeHost.Positions[id];
            double dx = p.X - FakeHost.PlayerPose.X, dy = p.Y - FakeHost.PlayerPose.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public bool Run() => C.RunEnvironmentImpact(Cache[C.activeTargetId]!, Dist((uint)C.activeTargetId));

        public void MovePlayer(float x, float y)
        {
            FakeHost.PlayerPose = (Lb | 0x1C, x, y, 0f);
            FakeHost.Positions[Player] = FakeHost.PlayerPose;
        }
    }

    private const string MissileLine = "Your missile attack hit the environment.";
    // 20 m north of the player (the pick), and 24 m east.
    private static readonly (uint, string, float, float) Skulker = (A, "Drudge Skulker", 96f, 116f);
    private static readonly (uint, string, float, float) Slinker = (B, "Drudge Slinker", 120f, 96f);

    private static void MissileNewTarget()
    {
        var r = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 2; s.EnvironmentImpactLimit = 2; s.EnvironmentSkipSec = 30; }, Skulker, Slinker);
        Check.Eq((uint)r.C.activeTargetId, A, "the nearer Skulker is locked");

        r.C.HandleChatForDamage(MissileLine);
        Check.False(r.Run(), "one hit: attacking on");
        r.Now = r.Now.AddSeconds(3);
        r.C.HandleChatForDamage(MissileLine);
        Check.True(FakeHost.Logs.Any(l => l.Contains("[EnvImpact] missile") && l.Contains("2/2")), "both hits logged against the target");

        Check.True(r.Run(), "the limit: this tick belongs to the response");
        Check.Eq(r.C.activeTargetId, 0, "the Skulker is dropped");
        Check.True(FakeHost.Logs.Any(l => l.Contains("leaving") && l.Contains("skipping it 30s")), "logged with the skip");
        Rescan(r);
        Check.False(r.C.ScannedTargets.Any(t => (uint)t.Id == A), "the skipped Skulker is out of the scan");
        r.C.HandleCombatTrigger();
        Check.Eq((uint)r.C.activeTargetId, B, "the Slinker is fought next");

        r.Now = r.Now.AddSeconds(31);
        Rescan(r);
        Check.True(r.C.ScannedTargets.Any(t => (uint)t.Id == A), "after the skip the Skulker is back");
    }

    /// <summary>The scan runs at most every 50 ms of real time (SCAN_INTERVAL_MS).</summary>
    private static void Rescan(Rig r)
    {
        System.Threading.Thread.Sleep(60);
        r.C.ScanNearbyTargets();
    }

    private static void MissileApproach()
    {
        // The Skulker is the only monster.
        var r = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 2; s.EnvironmentImpactLimit = 2; s.EnvironmentApproachMeters = 6; s.EnvironmentMinDistance = 3; }, Skulker);
        r.C.HandleChatForDamage(MissileLine);
        r.C.HandleChatForDamage(MissileLine);

        Check.True(r.Run(), "New target with nothing else in reach: moves closer instead");
        Check.Eq((uint)r.C.activeTargetId, A, "the only monster is kept");
        Check.True(FakeHost.AutoRun, "running toward it");
        Check.True(r.C.EnvironmentApproachActive, "approach under way");
        Check.True(FakeHost.Logs.Any(l => l.Contains("moving closer") && l.Contains("stopping at 14.0m")), "goal: 6 m nearer");

        r.Now = r.Now.AddMilliseconds(500);
        r.MovePlayer(96, 99);                  // 17 m
        Check.True(r.Run(), "still short of the goal: no attack");
        r.Now = r.Now.AddMilliseconds(500);
        r.MovePlayer(96, 102.5f);              // 13.5 m
        Check.False(r.Run(), "reached: attacking again this tick");
        Check.False(FakeHost.AutoRun, "autorun off");
        Check.False(r.C.EnvironmentApproachActive, "approach over");

        // Two more hits: already approached once, still far enough: Move closer goes again.
        r.S.MissileEnvironmentResponse = 1;
        r.C.HandleChatForDamage(MissileLine);
        r.C.HandleChatForDamage(MissileLine);
        Check.True(r.Run(), "Move closer trips again");
        Check.True(FakeHost.AutoRun, "running again");

        // Blocked: no ground gained for 2.5 s.
        r.Now = r.Now.AddSeconds(3);
        Check.False(r.Run(), "no progress: the approach gives up and combat attacks");
        Check.False(FakeHost.AutoRun, "autorun off when blocked");
        Check.True(FakeHost.Logs.Any(l => l.Contains("ended: no progress")), "logged as blocked");

        // At the closest distance with nothing else: keep attacking.
        r.MovePlayer(96, 113.2f);              // 2.8 m
        r.C.HandleChatForDamage(MissileLine);
        r.C.HandleChatForDamage(MissileLine);
        Check.False(r.Run(), "already close and the only monster: attacking on");
        Check.Eq((uint)r.C.activeTargetId, A, "not dropped");
    }

    private static void MissileOff()
    {
        var off = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 0; s.EnvironmentImpactLimit = 1; }, Skulker);
        off.C.HandleChatForDamage(MissileLine);
        Check.False(off.Run(), "Off: nothing happens");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[EnvImpact]")), "Off: not even counted");

        var melee = new Rig(CombatMode.Melee, s => { s.MissileEnvironmentResponse = 2; s.EnvironmentImpactLimit = 1; }, Skulker);
        melee.C.HandleChatForDamage(MissileLine);
        Check.False(melee.Run(), "not in Missile mode: ignored");
    }

    private static void MissileSpoof()
    {
        var r = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 2; s.EnvironmentImpactLimit = 1; }, Skulker);
        r.C.HandleChatForDamage("Bob says, \"" + MissileLine + "\"", 2);
        r.C.HandleChatForDamage(MissileLine, 3);                      // a tell, whatever its text
        r.C.HandleChatForDamage("Bob " + MissileLine, 12);            // an emote
        r.C.HandleChatForDamage("[Allegiance] Bob says, \"" + MissileLine + "\"");   // type unknown: the anchor
        Check.False(r.Run(), "no spoofed line counts");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[EnvImpact]")), "nothing recorded");

        r.C.HandleChatForDamage(MissileLine, 0);                      // ACE's own line is a Broadcast
        Check.True(r.Run(), "the server's line still trips the limit");
    }

    private static void MagicShort()
    {
        var r = new Rig(CombatMode.Magic, s => { s.MagicEnvironmentResponse = 2; s.EnvironmentImpactLimit = 2; }, Skulker, Slinker);
        Check.Eq((uint)r.C.activeTargetId, A, "the Skulker is locked");

        void Cast(float stopY, bool hitLine = false) => CastBolt(r, stopY, hitLine);

        Cast(105);
        Check.True(FakeHost.Logs.Any(l => l.Contains("[EnvImpact] magic") && l.Contains("1/2")), "a bolt stopping 9 m out, no hit: one environment hit");
        Cast(115);
        Check.False(FakeHost.Logs.Any(l => l.Contains("[EnvImpact] magic") && l.Contains("2/2")), "a bolt reaching the target: not counted");
        Cast(105, hitLine: true);
        Check.False(FakeHost.Logs.Any(l => l.Contains("[EnvImpact] magic") && l.Contains("2/2")), "a hit line for the cast: not counted");
        Cast(105);
        Check.True(FakeHost.Logs.Any(l => l.Contains("[EnvImpact] magic") && l.Contains("2/2")), "a second short bolt: the limit");
        Check.True(r.Run(), "the response runs");
        Check.Eq(r.C.activeTargetId, 0, "the Skulker is left for the Slinker");

        // Rings and other casters' projectiles aren't ours to judge.
        Check.False(CombatManager.IsWatchedProjectileName("Flame Ring"), "a ring isn't watched");
        Check.True(CombatManager.IsWatchedProjectileName("Nether Bolt"), "void bolts are");
        Check.False(CombatManager.IsWatchedProjectileName("Drudge Skulker"), "a monster isn't");
    }

    private static void ApproachOrphan()
    {
        var r = new Rig(CombatMode.Missile, s => { s.MissileEnvironmentResponse = 1; s.EnvironmentImpactLimit = 1; }, Skulker);
        r.C.HandleChatForDamage(MissileLine);
        Check.True(r.Run(), "approach starts");
        Check.True(FakeHost.AutoRun, "running");
        r.Now = r.Now.AddSeconds(2);           // Think stopped calling (buffing took over)
        r.C.PollEnvironmentImpact();
        Check.False(FakeHost.AutoRun, "the heartbeat lets go of autorun");
        Check.False(r.C.EnvironmentApproachActive, "approach over");

        r.C.HandleChatForDamage(MissileLine);
        Check.True(r.Run(), "a new approach");
        r.C.OnHeartbeat();                     // a heartbeat sees the macro running
        Check.True(FakeHost.AutoRun, "still running while Think drives it");
        r.S.IsMacroRunning = false;
        r.C.OnHeartbeat();
        Check.False(FakeHost.AutoRun, "a macro stop ends it too");
    }

    // ── Attack power ─────────────────────────────────────────────────────────

    private static void Power()
    {
        float R(string mode, int global = -1, bool reck = false, bool trained = true, int min = 50, int max = 100, double rnd = 0.5)
            => AttackPowerModes.Resolve(mode, global, reck, trained, min, max, () => rnd);

        Check.Near(R("Default"), 1.0, 1e-6, "Default, Auto, no Recklessness: 100%");
        Check.Near(R("Default", reck: true), 0.8, 1e-6, "Default, Auto + Use Recklessness, trained: 80%");
        Check.Near(R("Default", reck: true, trained: false), 1.0, 1e-6, "... untrained: 100%");
        Check.Near(R("Default", global: 65), 0.65, 1e-6, "Default, a set power: that power");
        Check.Near(R("High", global: 40), 1.0, 1e-6, "High: 100% whatever the global");
        Check.Near(R("Reckless"), 0.8, 1e-6, "Reckless: 80%");
        Check.Near(R("Reckless", trained: false), 1.0, 1e-6, "Reckless untrained: High");
        Check.Near(R("Variable", rnd: 0), 0.5, 1e-6, "Variable: the min");
        Check.Near(R("Variable", rnd: 0.999), 1.0, 1e-6, "Variable: up to the max");
        Check.Near(R("Variable", min: 90, max: 30, rnd: 0), 0.3, 1e-6, "Variable: a reversed range is put in order");
        Check.Near(R("Reckless + Variable", rnd: 0), 0.5, 1e-6, "Reckless + Variable: the Variable min (inside the band)");
        Check.Near(R("Reckless + Variable", rnd: 0.999), 0.8, 1e-6, "... capped at the band's 80%");
        Check.Near(R("Reckless + Variable", min: 85, max: 100, rnd: 0), 0.2, 1e-6, "no overlap with the band: the whole band");
        Check.Near(R("Reckless + Variable", trained: false, rnd: 0.999), 1.0, 1e-6, "untrained: plain Variable");
        var seen = new HashSet<float>();
        foreach (double d in new[] { 0.1, 0.4, 0.7 }) seen.Add(R("Variable", rnd: d));
        Check.Eq(seen.Count, 3, "Variable changes with each draw");

        Check.Eq(AttackPowerModes.Normalize("reckless+variable"), AttackPowerModes.RecklessVariable, "hand-typed spelling");
        Check.Eq(AttackPowerModes.Normalize(null), AttackPowerModes.Default, "missing: Default");
        Check.Eq(AttackPowerModes.Normalize("Huge"), AttackPowerModes.Default, "unknown: Default");
        var def = new MonsterRule { Name = "Default", AttackPower = "Reckless" };
        Check.Eq(AttackPowerModes.Effective(new MonsterRule { Name = "Drudge" }, def), "Reckless", "a Default rule follows the DEFAULT line");
        Check.Eq(AttackPowerModes.Effective(new MonsterRule { Name = "Drudge", AttackPower = "High" }, def), "High", "its own choice wins");
        Check.Eq(AttackPowerModes.Effective(null, null), "Default", "no rules: the global settings");
        Check.Eq(new MonsterRule().AttackPower, "Default", "a new rule (and an old file) is Default");

        // Pull-away kite (2026-10-16): Default → DEFAULT row → Off on that row.
        Check.Eq(KiteModes.Normalize("pull away"), KiteModes.PullAway, "hand-typed PullAway");
        Check.Eq(KiteModes.Normalize(null), KiteModes.Default, "missing kite: Default");
        var kiteDef = new MonsterRule { Name = "Default", Kite = KiteModes.PullAway };
        Check.Eq(KiteModes.Effective(new MonsterRule { Name = "Drudge" }, kiteDef), KiteModes.PullAway, "a Default rule follows the DEFAULT kite");
        Check.Eq(KiteModes.Effective(new MonsterRule { Name = "Drudge", Kite = KiteModes.Off }, kiteDef), KiteModes.Off, "its own kite wins");
        Check.Eq(KiteModes.Effective(kiteDef, kiteDef), KiteModes.PullAway, "DEFAULT row PullAway is PullAway");
        Check.Eq(KiteModes.Effective(new MonsterRule { Name = "Default" }, new MonsterRule { Name = "Default" }), KiteModes.Off, "DEFAULT Default means Off");
        Check.True(KiteModes.WantsPullAway(new MonsterRule { Name = "X" }, kiteDef), "WantsPullAway follows DEFAULT");
        Check.Eq(new MonsterRule().Kite, "Default", "a new rule (and an old file) is Default kite");
    }

    private static void PowerCarriedOver()
    {
        var current = new List<MonsterRule>
        {
            new() { Name = "Default", AttackPower = "High" },
            new() { Name = "Drudge", AttackPower = "Variable" },
            new() { Name = "Drudge", MatchExpression = "x", AttackPower = "Reckless" },
        };
        var incoming = new List<MonsterRule>
        {
            new() { Name = "Default" }, new() { Name = "drudge" }, new() { Name = "Drudge", MatchExpression = "x" }, new() { Name = "Mosswart" },
        };
        LegacyDashboardRenderer.CarryAttackPower(current, incoming);
        Check.Eq(incoming[0].AttackPower, "High", "Default keeps its power");
        Check.Eq(incoming[1].AttackPower, "Variable", "matched by name, any case");
        Check.Eq(incoming[2].AttackPower, "Reckless", "and by expression");
        Check.Eq(incoming[3].AttackPower, "Default", "a new rule stays Default");
    }

    // ── Magic position re-sync (upstream 2026.10.12.1) and the throttle ──────

    private const uint StepRight = 0x6500000F, StepLeft = 0x65000010;

    /// <summary>Upstream's CastNoDamageTests.ResyncSidestep, on this file's rig.</summary>
    private static void ResyncSidestep()
    {
        var r = new Rig(CombatMode.Magic, s => { }, setMotion: true, Skulker, Slinker);
        var c = r.C;
        c.TargetClock = () => r.Now;
        MoveAudit.Reset();

        c.MagicMissForTest(unchecked((int)A));
        c.MagicMissForTest(unchecked((int)B));            // a swap to the other monster doesn't reset the count
        Check.False(c.ResyncActive, "two misses: nothing yet");
        c.ReportDamageOnTarget(unchecked((int)A));
        c.MagicMissForTest(unchecked((int)A));
        c.MagicMissForTest(unchecked((int)A));
        Check.False(c.ResyncActive, "a hit reset the count");
        c.MagicMissForTest(unchecked((int)A));
        Check.True(c.ResyncActive, "three in a row: a re-sync is wanted");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("one short sidestep to re-sync")), 1, "said once");

        Check.True(c.TickResyncStep(), "first tick: the step starts, no cast");
        Check.True(FakeHost.MotionCalls.SequenceEqual(new[] { (StepRight, true) }), "sidestep right key down");
        r.Now = r.Now.AddMilliseconds(200);
        Check.True(c.TickResyncStep(), "still stepping at 200 ms");
        r.Now = r.Now.AddMilliseconds(100);
        Check.True(c.TickResyncStep(), "300 ms: key up, settling");
        Check.True(FakeHost.MotionCalls.SequenceEqual(new[] { (StepRight, true), (StepRight, false) }), "key up");
        r.Now = r.Now.AddMilliseconds(400);
        Check.False(c.TickResyncStep(), "settled: casting may go on");
        Check.Eq(c.ResyncSteps, 1, "one step");

        for (int i = 0; i < 3; i++) c.MagicMissForTest(unchecked((int)A));
        Check.False(c.ResyncActive, "within 30 s: no second step");
        r.Now = r.Now.AddSeconds(30);
        for (int i = 0; i < 3; i++) c.MagicMissForTest(unchecked((int)A));
        Check.True(c.ResyncActive, "30 s later: another one");
        c.TickResyncStep();
        Check.True(FakeHost.MotionCalls[2] == (StepLeft, true), "the other side this time");
    }

    private static void ResyncCountResetByThrottle()
    {
        var r = new Rig(CombatMode.Magic, s => { s.MagicEnvironmentResponse = 2; s.EnvironmentImpactLimit = 2; }, setMotion: true, Skulker, Slinker);
        r.C.TargetClock = () => r.Now;
        r.C.MagicMissForTest(unchecked((int)A));
        r.C.MagicMissForTest(unchecked((int)A));
        CastBolt(r, 105);
        CastBolt(r, 105);
        Check.True(r.Run(), "two short bolts: the throttle switches target");
        Check.Eq(r.C.activeTargetId, 0, "the Skulker is left");

        r.C.MagicMissForTest(unchecked((int)B));
        r.C.MagicMissForTest(unchecked((int)B));
        Check.False(r.C.ResyncActive, "the misses the wall explained no longer count toward a sidestep");
        r.C.MagicMissForTest(unchecked((int)B));
        Check.True(r.C.ResyncActive, "three fresh misses on the new target still ask for one");
    }

    private static void ResyncDroppedByApproach()
    {
        var r = new Rig(CombatMode.Magic, s => { s.MagicEnvironmentResponse = 1; s.EnvironmentImpactLimit = 1; }, setMotion: true, Skulker);
        r.C.TargetClock = () => r.Now;
        for (int i = 0; i < 3; i++) r.C.MagicMissForTest(unchecked((int)A));
        Check.True(r.C.ResyncActive, "a sidestep is wanted");

        CastBolt(r, 105);
        Check.True(r.Run(), "the throttle moves closer");
        Check.True(FakeHost.AutoRun, "running toward the Skulker");
        Check.False(r.C.ResyncActive, "the wanted sidestep is dropped: the run moves us anyway");
        Check.True(FakeHost.Logs.Any(l => l.Contains("re-sync sidestep dropped: moving closer")), "logged");
        Check.Eq(FakeHost.MotionCalls.Count, 0, "no sidestep key was sent");
    }

    private static void ResyncStepFinishesFirst()
    {
        var r = new Rig(CombatMode.Magic, s => { s.MagicEnvironmentResponse = 1; s.EnvironmentImpactLimit = 1; }, setMotion: true, Skulker);
        r.C.TargetClock = () => r.Now;
        for (int i = 0; i < 3; i++) r.C.MagicMissForTest(unchecked((int)A));
        CastBolt(r, 105);                      // the throttle trips (its response waits for Think)
        Check.True(r.C.TickResyncStep(), "the sidestep key goes down");

        Check.True(r.Run(), "the throttle's tick: no attack");
        Check.False(r.C.EnvironmentApproachActive, "but no run while the sidestep key is held");
        Check.False(FakeHost.AutoRun, "autorun not sent");

        r.Now = r.Now.AddMilliseconds(300);
        Check.True(r.C.TickResyncStep(), "the sidestep key comes up");
        Check.True(r.Run(), "now the approach runs");
        Check.True(r.C.EnvironmentApproachActive, "approach under way");
        Check.True(FakeHost.MotionCalls.SequenceEqual(new[] { (StepRight, true), (StepRight, false) }), "the key went up before the run");
    }

    private static void ResyncKeyReleased()
    {
        var r = new Rig(CombatMode.Magic, s => { }, setMotion: true, Skulker);
        r.C.TargetClock = () => r.Now;
        for (int i = 0; i < 3; i++) r.C.MagicMissForTest(unchecked((int)A));
        r.C.TickResyncStep();
        Check.True(FakeHost.MotionCalls.SequenceEqual(new[] { (StepRight, true) }), "key down");

        r.Now = r.Now.AddSeconds(2);           // Think stopped ticking it (left magic mode)
        r.C.PollEnvironmentImpact();
        Check.True(FakeHost.MotionCalls.SequenceEqual(new[] { (StepRight, true), (StepRight, false) }), "the heartbeat lets go of the key");
        Check.False(r.C.ResyncActive, "the sidestep is over");
        Check.True(FakeHost.Logs.Any(l => l.Contains("re-sync sidestep stopped: combat stopped driving it")), "logged");

        r.C.OnHeartbeat();                     // a heartbeat sees the macro running
        r.C.MagicMissForTest(unchecked((int)A));
        int before = FakeHost.MotionCalls.Count;
        r.C.TickResyncStep();
        Check.True(FakeHost.MotionCalls.Count == before + 1 && FakeHost.MotionCalls[^1] == (StepLeft, true), "a new sidestep, key down");
        r.S.IsMacroRunning = false;
        r.C.OnHeartbeat();
        Check.True(FakeHost.MotionCalls[^1] == (StepLeft, false), "a macro stop lets go of it too");
        Check.False(r.C.ResyncActive, "and ends it");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>One cast at the active target whose bolt flies from the caster north to <paramref name="stopY"/> and stops.</summary>
    private static void CastBolt(Rig r, float stopY, bool hitLine = false)
    {
        r.C.RegisterCastWatch(r.C.activeTargetId, "Flame Bolt VI");
        r.Now = r.Now.AddMilliseconds(800);                     // the windup
        FakeHost.Names[Bolt] = "Flame Bolt";
        FakeHost.Positions[Bolt] = (Lb | 0x1C, 96f, 97f, 0f);   // spawns at the caster
        r.C.OnEnvironmentObjectCreated(Bolt);
        r.C.PollEnvironmentImpact();
        for (float y = 99; y <= stopY; y += 2)
        {
            r.Now = r.Now.AddMilliseconds(100);
            FakeHost.Positions[Bolt] = (Lb | 0x1C, 96f, y, 0f);
            r.C.PollEnvironmentImpact();
        }
        if (hitLine) r.C.HandleChatForDamage("You singe Drudge Skulker for 40 points of fire damage!");
        for (int i = 0; i < 15; i++) { r.Now = r.Now.AddMilliseconds(100); r.C.PollEnvironmentImpact(); }
        r.C.OnObjectDeleted(Bolt);
        FakeHost.Positions.Remove(Bolt);
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }
}
