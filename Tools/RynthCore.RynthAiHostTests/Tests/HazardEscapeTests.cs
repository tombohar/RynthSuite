using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-07, Drakkon: two deaths in an acid pool in an hour. The patrol left him in a pool the
// hazard list didn't know, and topping up with healing kits held him there. Now the first
// "You suffer N damage from acid!" stops everything and walks him back out along his footsteps.
internal static class HazardEscapeTests
{
    private const uint Player = 0x50000180;
    private const uint CellA = 0x63470160, CellB = 0x6347016C;   // dungeon cells, one landblock
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("hazard: the hotspot damage line is read (amount, source); other lines are not", Parse);
        r.Add("hazard: a hit stops everything and walks back along the footsteps, then hands back", EscapeAlongFootsteps);
        r.Add("hazard: hurt again where it stopped - it goes further back", FurtherBack);
        r.Add("hazard: macro off - nothing moves, nothing learned", MacroOff);
        r.Add("hazard: Escape Acid and Lava off - no escape and nothing learned (some walk through acid)", SettingOff);
    }

    private static void Parse()
    {
        Check.True(RynthAiPlugin.TryParseHotspotDamage("You suffer 32 damage from acid!", out int a, out string s) && a == 32 && s == "acid", "acid");
        Check.True(RynthAiPlugin.TryParseHotspotDamage("You suffer 11 damage from the lava.", out a, out s) && a == 11 && s == "lava", "lava, with 'the'");
        Check.False(RynthAiPlugin.TryParseHotspotDamage("You say, \"Malar Zhapaj\"", out _, out _), "speech");
        Check.False(RynthAiPlugin.TryParseHotspotDamage("Olthoi Swarm Worker bites you for 20 points of damage!", out _, out _), "a monster's hit");
    }

    private sealed class Clock { public long Now = 1_000_000; }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new() { IsMacroRunning = true };
        public readonly WorldObjectCache Cache;
        public readonly Clock C = new();

        public Bot()
        {
            FakeHost.Reset();
            FakeHost.StopCalls = true;
            var host = FakeHost.Create(Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(P, dash);
            Cache = new WorldObjectCache(host);
            typeof(RynthAiPlugin).GetField("_objectCache", Any)!.SetValue(P, Cache);
            var clock = C;
            RynthAiPlugin.HazardClock = () => clock.Now;
        }

        /// <summary>Stands at (x, y), lets time pass and records the footstep.</summary>
        public void At(uint cell, float x, float y, long ms = 300)
        {
            FakeHost.PlayerPose = (cell, x, y, -24f);
            C.Now += ms;
            P.RecordHazardTrail();
        }

        public bool Hold() => P.HoldForHazardEscape();
        public void Hit() => P.NoteHotspotChat("You suffer 32 damage from acid!", 0);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
    }

    private static (float X, float Y) Target(Bot b) => (b.Get<float>("_hazardTargetX"), b.Get<float>("_hazardTargetY"));

    private static void EscapeAlongFootsteps()
    {
        var b = new Bot();
        // An L: north up x=60 from y=-185 to y=-175, then east to x=66 - into the acid.
        for (int i = 0; i <= 10; i++) b.At(CellA, 60f, -185f + i);
        for (int i = 1; i <= 6; i++) b.At(CellB, 60f + i, -175f);

        b.Hit();
        Check.True(b.P.HazardEscaping, "the hit starts the escape");
        Check.False(b.Cache.IsHazardCell(CellB), "the cell it was hurt in is not a patrol hazard (patrol as before 10-07)");
        Check.True(b.Hold(), "the escape owns the tick (nothing else runs)");
        Check.True(FakeHost.AutoRun, "running");

        var path = b.Get<System.Collections.Generic.List<(uint Cell, float X, float Y)>>("_hazardPath");
        Check.True(path.Count >= 3, $"a path of footsteps, not one straight line ({path.Count} points)");
        Check.True(path.Take(path.Count - 1).Any(p => p.Y == -175f && p.X > 60f), "it first walks back east-west along the way it came");
        var t = Target(b);
        Check.True(t.X == 60f && t.Y <= -175f, $"and ends back on the north-south leg, outside the hurt cell ({t.X}, {t.Y})");

        // Walk the footsteps: each tick at the next one.
        foreach (var p in path)
        {
            FakeHost.PlayerPose = (p.Cell, p.X, p.Y, -24f);
            b.C.Now += 300;
            Check.True(b.Hold(), "still holding while it walks out");
        }
        Check.False(FakeHost.AutoRun, "stopped at the target");
        b.C.Now += RynthAiPlugin.HazardQuietMs;
        Check.False(b.Hold(), "no hit for a while at the target: the tick is handed back");
        Check.False(b.P.HazardEscaping, "escape over");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Hazard: out of it")), "and it says so");
    }

    private static void FurtherBack()
    {
        var b = new Bot();
        for (int i = 0; i <= 12; i++) b.At(CellA, 60f, -190f + i);
        b.Hit();
        var first = Target(b);
        FakeHost.PlayerPose = (CellA, first.X, first.Y, -24f);
        b.C.Now += 300;
        foreach (var _ in Enumerable.Range(0, 20)) { b.Hold(); FakeHost.PlayerPose = (CellA, first.X, first.Y, -24f); }
        Check.False(FakeHost.AutoRun, "arrived at the first spot");

        b.C.Now += 2500;
        b.Hit();   // the pool reaches further back
        var second = Target(b);
        Check.True(second.Y < first.Y - 1f, $"it goes further back ({first.Y} -> {second.Y})");
        Check.True(b.Hold(), "and keeps the tick");
        Check.True(FakeHost.AutoRun, "running again");
    }

    private static void MacroOff()
    {
        var b = new Bot();
        b.S.IsMacroRunning = false;
        for (int i = 0; i <= 6; i++) b.At(CellA, 60f, -185f + i);
        b.Hit();
        Check.False(b.Cache.IsHazardCell(CellA), "the cell is not learned");
        Check.False(b.P.HazardEscaping, "but nothing moves the character while you play it");
        Check.False(b.Hold(), "and the tick is not held");
    }

    private static void SettingOff()
    {
        var b = new Bot();
        b.S.EscapeHazards = false;
        for (int i = 0; i <= 6; i++) b.At(CellA, 60f, -185f + i);
        b.Hit();
        Check.False(b.P.HazardEscaping, "no escape");
        Check.False(b.Cache.IsHazardCell(CellA), "and the cell is not learned (the patrol keeps its way through)");
        Check.False(b.Hold(), "the tick is not held");

        // Turned off mid-escape: it lets go at once.
        b.S.EscapeHazards = true;
        b.Hit();
        Check.True(b.P.HazardEscaping, "on again: the next hit escapes");
        b.S.EscapeHazards = false;
        Check.False(b.Hold(), "turned off mid-escape: the tick is handed back");
        Check.False(b.P.HazardEscaping, "escape over");
        Check.False(new LegacyUiSettings().EscapeHazards == false, "on by default");
    }
}
