using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The native attack wedge (2026-11-06, DreamWeave, a melee character dual-wielding two Hand
// Wraps): a buff-wand swap came 0.5 s after a native attack, and from then on every native
// attack was queued by the client behind its attack-in-progress flag, which no AttackDone would
// ever clear (the client "attacked" in Peace, sending nothing). 37 minutes of no swings;
// turning Use Native Attack off fought at once. Now:
//  * a stance change mid-fight: the first swing back is the regular attack (its AttackDone
//    resets the client), native from the next one;
//  * native attacks with no hit, miss or kill for 10 s: the regular attack for that fight,
//    native again from the next target, logged once each way; the setting is never changed;
//  * Use Native Attack off: nothing of this.
internal static class NativeAttackTests
{
    private const uint Player = 0x50000180;
    private const uint Lb = 0x11BA0000;
    private const uint Mob = 0x802307B1, Mob2 = 0x802307B2;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("native attack: a buff-wand swap mid-fight and back: the first swing back is the regular attack, then native again", SwapMidFightResets);
        r.Add("native attack: a stance change long after the last swing (between fights): native straight away", SwapBetweenFightsStaysNative);
        r.Add("native attack: 10 s of native attacks with no hit, miss or kill: the regular attack for this fight, native again next fight", StallFallsBackThenNativeNextFight);
        r.Add("native attack: hits keep it native (the quiet clock starts over on every hit, miss or kill)", EvidenceKeepsNative);
        r.Add("native attack off: regular attacks only, a swap changes nothing, nothing logged", NonNativeUnchanged);
    }

    // ── A melee rig with native attack, ticked the way OnTick ticks it ────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager C;

        public Rig(bool native = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.AttackSendCalls = true;
            FakeHost.NativeAttackCalls = true;
            FakeHost.CancelAttackCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.CombatModeValue = CombatMode.Melee;
            Place(Player, "Me", 96, 96, creature: false);
            Place(Mob, "Drumtaken Kithless Siraluun", 96, 98.2f);
            Place(Mob2, "Drumtaken Rabid Carenzi", 96, 99.8f);
            FakeHost.PlayerPose = (Lb | 0x36, 96f, 96f, 0f);
            FakeHost.HeadingDeg = 0;
            S = new LegacyUiSettings
            {
                MonsterRange = 50, IsMacroRunning = true, EnableCombat = true,
                UseNativeAttack = native,
                MonsterRules = new List<MonsterRule> { new() { Name = "Default" } },
            };
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { Mob, Mob2 });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            C.ScanNearbyTargets();
        }

        /// <summary>A combat tick (Combat has the arbiter's tick). The attack cadence runs on the
        /// real clock, so it is reset.</summary>
        public void Tick()
        {
            S.BotAction = "Combat";
            LastAttackCmd.SetValue(C, DateTime.MinValue);
            C.OnHeartbeat();
        }

        /// <summary>A tick while buffing has it (the wand swap): combat only watches.</summary>
        public void BuffTick(int mode)
        {
            FakeHost.CombatModeValue = mode;
            S.BotAction = "Buffing";
            C.OnHeartbeat();
        }

        /// <summary>The wand swap BuffManager makes: Peace, Magic (casts), Peace, back to Melee.</summary>
        public void WandSwapAndBack()
        {
            BuffTick(CombatMode.NonCombat);
            BuffTick(CombatMode.Magic);
            BuffTick(CombatMode.Magic);
            BuffTick(CombatMode.NonCombat);
            FakeHost.CombatModeValue = CombatMode.Melee;
        }

        /// <summary>Ticks until an attack reaches the server; its kind ("native" / "melee"), or null.</summary>
        public string? NextAttack()
        {
            int before = FakeHost.AttackCalls.Count;
            for (int i = 0; i < 40 && FakeHost.AttackCalls.Count == before; i++) Tick();
            return FakeHost.AttackCalls.Count > before ? FakeHost.AttackCalls[^1].Kind : null;
        }

        /// <summary>Moves one of the combat manager's private clocks back by <paramref name="ms"/>.</summary>
        public void Age(string field, double ms)
        {
            FieldInfo f = typeof(CombatManager).GetField(field, Any)!;
            var t = (DateTime)f.GetValue(C)!;
            f.SetValue(C, t.AddMilliseconds(-ms));
        }

        private static readonly FieldInfo LastAttackCmd = typeof(CombatManager).GetField("lastAttackCmd", Any)!;
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x36, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static int Logged(string part) => FakeHost.Logs.Count(l => l.Contains(part, StringComparison.Ordinal));

    // ── 1. The swap mid-fight ─────────────────────────────────────────────────

    private static void SwapMidFightResets()
    {
        var rig = new Rig();
        Check.Eq(rig.NextAttack(), "native", "the fight starts native");
        Check.Eq(FakeHost.AttackCalls[^1].Target, Mob, "at the locked mob (selected for the client's pipeline)");
        Check.Eq(rig.NextAttack(), "native", "and stays native");

        rig.WandSwapAndBack();   // 0.5 s after a native attack, as at 01:28:51
        Check.True(rig.C.NativeResetPending, "the stance left Melee right after a native attack: a reset is due");

        Check.Eq(rig.NextAttack(), "melee", "the first swing back is the regular attack");
        Check.Eq(FakeHost.AttackCalls[^1].Target, Mob, "at the same mob");
        Check.Eq(Logged("goes out as the regular attack"), 1, "logged once");
        Check.False(rig.C.NativeResetPending, "one swing only");
        Check.Eq(rig.NextAttack(), "native", "then native again");
        Check.Eq(rig.NextAttack(), "native", "and stays native");
        Check.True(rig.S.UseNativeAttack, "the setting is untouched");
        Check.False(rig.C.NativeFallbackActive, "no fallback");
    }

    // ── 2. A stance change between fights ─────────────────────────────────────

    private static void SwapBetweenFightsStaysNative()
    {
        var rig = new Rig();
        Check.Eq(rig.NextAttack(), "native", "native");
        rig.Age("_lastNativeAttackAt", CombatManager.NativeResetWindowMs + 1000);   // the last swing 6 s ago
        rig.WandSwapAndBack();
        Check.False(rig.C.NativeResetPending, "no attack was in flight when the stance left: no reset");
        Check.Eq(rig.NextAttack(), "native", "native straight away");
        Check.Eq(Logged("[Combat] native attack"), 0, "nothing logged");
    }

    // ── 3. The safety net ─────────────────────────────────────────────────────

    private static void StallFallsBackThenNativeNextFight()
    {
        var rig = new Rig();
        for (int i = 0; i < CombatManager.NativeFallbackAttacks; i++)
            Check.Eq(rig.NextAttack(), "native", $"native attack {i + 1}, no hit, miss or kill");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "rig: the first mob is the target");

        rig.Age("_nativeQuietSince", CombatManager.NativeFallbackMs + 500);   // 10.5 s of them
        rig.Age("_lastNativeAttackAt", 900);                                 // the last one a cycle ago (no gap)
        Check.Eq(rig.NextAttack(), "melee", "10 s with nothing: the regular attack");
        Check.True(rig.C.NativeFallbackActive, "for this fight");
        Check.Eq(Logged("native attack isn't landing"), 1, "logged");
        Check.True(FakeHost.Logs.Any(l => l.Contains("using the regular attack until the next fight")), "with what happens");
        Check.Eq(rig.NextAttack(), "melee", "the next swing at the same mob: regular");
        Check.Eq(rig.NextAttack(), "melee", "and the next");
        Check.Eq(Logged("native attack isn't landing"), 1, "logged once, not per swing");
        Check.True(rig.S.UseNativeAttack, "the setting is untouched");

        // The fight ends (the mob gone), the next one starts: native again.
        rig.C.MarkGhost(unchecked((int)Mob), "Drumtaken Kithless Siraluun", 900_000, "test: the fight is over");
        Thread.Sleep(80);   // the scan runs at most every 50 ms
        rig.C.ScanNearbyTargets();
        Check.Eq(rig.NextAttack(), "native", "the next fight: native again");
        Check.Eq((uint)rig.C.activeTargetId, Mob2, "at the next mob");
        Check.False(rig.C.NativeFallbackActive, "the fallback is over");
        Check.Eq(Logged("native attack again: a new fight"), 1, "logged");
        Check.Eq(rig.NextAttack(), "native", "and stays native (the quiet clock started over)");
    }

    private static void EvidenceKeepsNative()
    {
        var rig = new Rig();
        for (int i = 0; i < CombatManager.NativeFallbackAttacks + 2; i++)
            Check.Eq(rig.NextAttack(), "native", $"native attack {i + 1}");
        rig.Age("_nativeQuietSince", CombatManager.NativeFallbackMs + 500);
        rig.C.OnCombatDamage(120, 1, false, isAttacker: true);   // a hit lands
        Check.Eq(rig.NextAttack(), "native", "a hit since: still native");
        Check.False(rig.C.NativeFallbackActive, "no fallback");

        // A long pause between native attacks (a loot run) starts the clock over too.
        for (int i = 0; i < CombatManager.NativeFallbackAttacks; i++) rig.NextAttack();
        rig.Age("_nativeQuietSince", CombatManager.NativeFallbackMs + 500);
        rig.Age("_lastNativeAttackAt", CombatManager.NativeQuietGapMs + 500);
        Check.Eq(rig.NextAttack(), "native", "the last native attack 5.5 s ago: a new quiet clock, native");
        Check.Eq(Logged("native attack isn't landing"), 0, "never fell back");
    }

    // ── 4. Native attack off ──────────────────────────────────────────────────

    private static void NonNativeUnchanged()
    {
        var rig = new Rig(native: false);
        Check.Eq(rig.NextAttack(), "melee", "regular");
        rig.WandSwapAndBack();
        Check.False(rig.C.NativeResetPending, "no reset pending");
        for (int i = 0; i < 6; i++)
            Check.Eq(rig.NextAttack(), "melee", $"regular attack {i + 1}");
        Check.False(rig.C.NativeFallbackActive, "no fallback");
        Check.Eq(Logged("[Combat] native attack"), 0, "nothing logged");
        Check.False(FakeHost.AttackCalls.Any(a => a.Kind == "native"), "never a native attack");
    }
}
