using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Settings > Navigation > Dungeon Patrol (DungeonPatrolOptions, 2026-10-12): which cells the
// patrol walks for each Coverage, the dead-end limit, lava/acid floors on or off; the defaults
// give the patrol as it was before the settings, but for the dead-end limit (whole dead-end areas
// since 2026-10-13, default 10; PatrolPocketTests); the settings reach the options in range and
// round-trip through the engine payload and profiles; a change mid-patrol rebuilds it once it
// settles. The look-into-rooms size and the wall distance are in PatrolDoorwayTests (they need
// the map geometry).
//
// The fixture: a 10-cell loop (cells 1..10) with, off cell 1, a 1-cell closet (0x10) and a
// 3-cell spur (0x20-0x22), and off cell 2 a 5-cell branch (0x30-0x34). 19 cells, 2-core 10.
internal static class PatrolSettingsTests
{
    public static void Register(Runner r)
    {
        r.Add("patrol settings: the defaults (whole dungeon, dead-end areas of <= 10 cells skipped)", DefaultsAreToday);
        r.Add("patrol settings: Loops only walks the 2-core", LoopsOnly);
        r.Add("patrol settings: Everything walks every reachable cell", Everything);
        r.Add("patrol settings: Skip Dead Ends 0 / 1 / 3 / 4 / 5 / 10 / 99", SpurLimits);
        r.Add("patrol settings: a linear dungeon walks everything whatever the coverage", LinearWalksEverything);
        r.Add("patrol settings: Avoid Lava/Acid Floors off walks the floor cells, sighted/marked cells still avoided", HazardFloors);
        r.Add("patrol settings: settings to options, defaults equal, out-of-range values clamped", OptionsFromSettings);
        r.Add("patrol settings: sent to the engine face, applied from it, absent = unchanged", EnginePayload);
        r.Add("patrol settings: a change rebuilds the patrol once it has settled for 1 s", RebuildWhenSettled);
    }

    private const uint Closet = 0x10;
    private static readonly uint[] Spur3 = { 0x20, 0x21, 0x22 };
    private static readonly uint[] Branch5 = { 0x30, 0x31, 0x32, 0x33, 0x34 };
    private static readonly uint[] Loop = Enumerable.Range(1, 10).Select(i => (uint)i).ToArray();

    private static Dictionary<uint, DungeonNavNode> Fixture()
    {
        var g = new Dictionary<uint, DungeonNavNode>();
        void Link(uint a, uint b)
        {
            foreach (uint c in new[] { a, b })
                if (!g.ContainsKey(c)) g[c] = new DungeonNavNode { CellId = c, NS = (c & 0x0F) * 0.01, EW = (c >> 4 & 0x0F) * 0.01 };
            g[a].Neighbors.Add(b); g[b].Neighbors.Add(a);
        }
        for (int i = 0; i < Loop.Length; i++) Link(Loop[i], Loop[(i + 1) % Loop.Length]);
        Link(1, Closet);
        Link(1, Spur3[0]); Link(Spur3[0], Spur3[1]); Link(Spur3[1], Spur3[2]);
        Link(2, Branch5[0]);
        for (int i = 0; i + 1 < Branch5.Length; i++) Link(Branch5[i], Branch5[i + 1]);
        return g;
    }

    private static HashSet<uint> Cells(DungeonPatrolOptions? o, IReadOnlySet<uint>? hazards = null, uint start = 5)
        => DungeonPathfinder.GetMainRouteNodes(Fixture(), start, hazards, o);

    private static string Ids(IEnumerable<uint> c) => string.Join(",", c.OrderBy(x => x).Select(x => x.ToString("X")));

    private static void DefaultsAreToday()
    {
        var expected = new HashSet<uint>(Loop);   // the closet, the 3-cell spur and the 5-cell branch go
        Check.True(Cells(null).SetEquals(expected), $"no options: the loop ({Ids(Cells(null))})");
        Check.True(Cells(DungeonPatrolOptions.Default).SetEquals(expected), "DungeonPatrolOptions.Default: the same");
        Check.True(Cells(RynthAiPlugin.PatrolOptionsFrom(new LegacyUiSettings())).SetEquals(expected), "a fresh profile's settings: the same");

        // And the route itself: no options = default options = a fresh profile's settings, point for point.
        var g = Fixture();
        var a = DungeonPathfinder.BuildPatrolRoute(g, 5, null, null);
        var b = DungeonPathfinder.BuildPatrolRoute(g, 5, null, null, DungeonPatrolOptions.Default);
        var c = DungeonPathfinder.BuildPatrolRoute(g, 5, null, null, RynthAiPlugin.PatrolOptionsFrom(new LegacyUiSettings()));
        Check.True(a.Points.Count > 0, "a patrol");
        Check.True(Same(a, b) && Same(a, c), "the same points in the same order");
    }

    private static bool Same(NavRouteParser a, NavRouteParser b)
        => a.Points.Count == b.Points.Count
        && a.Points.Zip(b.Points).All(p => p.First.NS == p.Second.NS && p.First.EW == p.Second.EW && p.First.Z == p.Second.Z);

    private static void LoopsOnly()
    {
        var cells = Cells(new DungeonPatrolOptions { Coverage = PatrolCoverage.LoopsOnly });
        Check.True(cells.SetEquals(Loop), $"just the loop ({Ids(cells)})");
    }

    private static void Everything()
    {
        var cells = Cells(new DungeonPatrolOptions { Coverage = PatrolCoverage.Everything });
        Check.Eq(cells.Count, 19, "all 19 cells");
        Check.True(cells.Contains(Closet) && cells.IsSupersetOf(Spur3), "the closet and the short spur too");
        var anySpurs = Cells(new DungeonPatrolOptions { Coverage = PatrolCoverage.Everything, MaxDeadEndCells = 10 });
        Check.Eq(anySpurs.Count, 19, "the dead-end limit doesn't apply to Everything");
    }

    private static void SpurLimits()
    {
        DungeonPatrolOptions Spurs(int n) => new() { MaxDeadEndCells = n };
        Check.Eq(Cells(Spurs(0)).Count, 19, "0: every dead end entered");
        // A dead end longer than the limit is walked until only the limit is left ahead.
        Check.True(Cells(Spurs(3)).SetEquals(Loop.Concat(Branch5.Take(2))), $"3: closet and 3-cell spur skipped, 2 cells into the branch ({Ids(Cells(Spurs(3)))})");
        Check.True(Cells(Spurs(4)).SetEquals(Loop.Concat(Branch5.Take(1))), "4: 1 cell into the 5-cell branch");
        Check.True(Cells(Spurs(5)).SetEquals(Loop), "5: the 5-cell branch skipped too");
        Check.True(Cells(Spurs(10)).SetEquals(Loop), "10: the same");
        Check.Eq(Cells(Spurs(99)).Count, Cells(Spurs(10)).Count, "99: as 10 here (BuildPatrolRoute clamps it to 30)");
        var one = Cells(Spurs(1));
        Check.True(!one.Contains(Closet) && !one.Contains(Spur3[2]) && one.IsSupersetOf(Spur3.Take(2)) && !one.Contains(Branch5[4]),
                   $"1: the closet and the last cell of each dead end skipped ({Ids(one)})");
    }

    private static void LinearWalksEverything()
    {
        var g = new Dictionary<uint, DungeonNavNode>();
        for (uint i = 1; i <= 12; i++) g[i] = new DungeonNavNode { CellId = i, NS = i * 0.01, EW = 0 };
        for (uint i = 1; i < 12; i++) { g[i].Neighbors.Add(i + 1); g[i + 1].Neighbors.Add(i); }
        foreach (PatrolCoverage cov in Enum.GetValues<PatrolCoverage>())
            Check.Eq(DungeonPathfinder.GetMainRouteNodes(g, 6, null, new DungeonPatrolOptions { Coverage = cov }).Count, 12,
                     $"{cov}: all 12 cells of a corridor");
    }

    private static void HazardFloors()
    {
        // 0x31 has a lava floor texture; 0x07 (on the loop) is a sighted pool.
        var floor = new HashSet<uint> { 0x31 };
        var marked = new HashSet<uint> { 0x07 };
        var live = new HashSet<uint> { 0x31, 0x07 };
        var on = new DungeonPatrolOptions();
        var off = new DungeonPatrolOptions { AvoidHazardFloors = false };

        var hzOn = DungeonPathfinder.PatrolHazards(live, floor, marked, on);
        Check.True(hzOn!.SetEquals(live), "on: every hazard cell avoided");
        var cellsOn = Cells(on, hzOn);
        Check.False(cellsOn.Contains(0x31) || cellsOn.Contains(0x32), "on: the lava cell and the branch beyond it are not walked");

        var hzOff = DungeonPathfinder.PatrolHazards(live, floor, marked, off);
        Check.True(hzOff!.SetEquals(new uint[] { 0x07 }), $"off: only the sighted pool left ({Ids(hzOff!)})");
        var cellsOff = Cells(off with { Coverage = PatrolCoverage.Everything }, hzOff);
        Check.True(cellsOff.IsSupersetOf(Branch5), "off: the lava cell and the branch beyond it are walked");
        Check.False(cellsOff.Contains(0x07), "off: the sighted pool is still avoided");

        var both = DungeonPathfinder.PatrolHazards(live, floor, new HashSet<uint> { 0x07, 0x31 }, off);
        Check.True(both!.Contains(0x31), "off: a floor cell that was also marked by hand stays avoided");
        Check.True(DungeonPathfinder.PatrolHazards(null, floor, marked, off) == null, "no hazards: none");
    }

    private static void OptionsFromSettings()
    {
        var d = RynthAiPlugin.PatrolOptionsFrom(new LegacyUiSettings());
        Check.True(d.Equals(DungeonPatrolOptions.Default), "a fresh profile = the defaults: " + d.Describe());
        var s = new LegacyUiSettings
        {
            DunPatrolCoverage = 7, DunPatrolSkipDeadEnds = 99, DunPatrolAvoidFloors = false,
            DunPatrolWallDistance = 9f, DunPatrolLookInSize = -4f, DunPatrolSightYd = 200f,
        };
        var o = RynthAiPlugin.PatrolOptionsFrom(s);
        Check.Eq(o.Coverage, PatrolCoverage.Sweep, "coverage 7 -> 3 (sweep, the last)");
        Check.Near(o.SightMeters, 60.0, 1e-9, "sight 200 -> 60 m");
        Check.Eq(o.MaxDeadEndCells, 30, "dead ends 99 -> 30");
        Check.False(o.AvoidHazardFloors, "floors off");
        Check.Near(o.WallClearance, 2.0, 1e-9, "walls 9 -> 2 m");
        Check.Near(o.LookInRoomMeters, 3.0, 1e-9, "look-in -4 -> 3 m");
        s.DunPatrolCoverage = 1; s.DunPatrolSkipDeadEnds = 5; s.DunPatrolWallDistance = 1.5f; s.DunPatrolLookInSize = 20f; s.DunPatrolSightYd = 25f;
        o = RynthAiPlugin.PatrolOptionsFrom(s);
        Check.True(o.Coverage == PatrolCoverage.LoopsOnly && o.MaxDeadEndCells == 5 && o.WallClearance == 1.5 && o.LookInRoomMeters == 20 && o.SightMeters == 25,
                   "in-range values pass through: " + o.Describe());
        s.DunPatrolCoverage = 3;
        o = RynthAiPlugin.PatrolOptionsFrom(s);
        Check.True(o.Coverage == PatrolCoverage.Sweep && o.Describe().Contains("sight 25 m"), "sweep, and its sight in the description: " + o.Describe());
    }

    private static void EnginePayload()
    {
        var s = new LegacyUiSettings { DunPatrolCoverage = 1, DunPatrolSkipDeadEnds = 6, DunPatrolAvoidFloors = false, DunPatrolWallDistance = 1.25f, DunPatrolLookInSize = 15f, DunPatrolSightYd = 30f };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(sent.DunPatrolCoverage == 1 && sent.DunPatrolSkipDeadEnds == 6 && sent.DunPatrolAvoidFloors == false
                   && sent.DunPatrolWallDistance == 1.25f && sent.DunPatrolLookInSize == 15f && sent.DunPatrolSightYd == 30f, "every patrol setting sent to the engine face");

        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.DunPatrolCoverage == 1 && s.DunPatrolSkipDeadEnds == 6 && !s.DunPatrolAvoidFloors && s.DunPatrolWallDistance == 1.25f
                   && s.DunPatrolLookInSize == 15f && s.DunPatrolSightYd == 30f, "a save from an engine without them leaves them alone");
        dash.ApplySettingsJson("{\"DunPatrolCoverage\":3,\"DunPatrolSightYd\":40}");
        Check.True(s.DunPatrolCoverage == 3 && s.DunPatrolSightYd == 40f, "sweep and its sight applied");
        dash.ApplySettingsJson("{\"DunPatrolSightYd\":1}");
        Check.True(s.DunPatrolSightYd == 5f, "sight 1 -> 5 (clamped)");
        dash.ApplySettingsJson("{\"DunPatrolCoverage\":2,\"DunPatrolSkipDeadEnds\":0,\"DunPatrolAvoidFloors\":true,\"DunPatrolWallDistance\":0.75,\"DunPatrolLookInSize\":30}");
        Check.True(s.DunPatrolCoverage == 2 && s.DunPatrolSkipDeadEnds == 0 && s.DunPatrolAvoidFloors && s.DunPatrolWallDistance == 0.75f
                   && s.DunPatrolLookInSize == 30f, "a save with them applies them");
        dash.ApplySettingsJson("{\"DunPatrolCoverage\":9,\"DunPatrolSkipDeadEnds\":40,\"DunPatrolWallDistance\":0.1,\"DunPatrolLookInSize\":100}");
        Check.True(s.DunPatrolCoverage == 3 && s.DunPatrolSkipDeadEnds == 30 && s.DunPatrolWallDistance == 0.5f && s.DunPatrolLookInSize == 40f,
                   $"out of range clamped ({s.DunPatrolCoverage}, {s.DunPatrolSkipDeadEnds}, {s.DunPatrolWallDistance}, {s.DunPatrolLookInSize})");
    }

    private static void RebuildWhenSettled()
    {
        var w = new PatrolSettingsWatch();
        var a = DungeonPatrolOptions.Default;
        var b = a with { MaxDeadEndCells = 5 };
        var c = a with { MaxDeadEndCells = 6 };
        Check.False(w.ShouldRebuild(b, 0), "no patrol built yet: nothing to rebuild");
        w.MarkBuilt(a);
        Check.False(w.ShouldRebuild(a, 100), "unchanged: no rebuild");
        Check.False(w.ShouldRebuild(b, 1000), "changed: wait for it to settle");
        Check.False(w.ShouldRebuild(b, 1999), "still within 1 s");
        Check.False(w.ShouldRebuild(c, 2100), "changed again (a second click): the wait starts over");
        Check.False(w.ShouldRebuild(c, 3000), "0.9 s after the last change");
        Check.True(w.ShouldRebuild(c, 3100), "1 s unchanged: rebuild");
        w.MarkBuilt(c);
        Check.False(w.ShouldRebuild(c, 5000), "rebuilt with it: no more rebuilds");
        Check.False(w.ShouldRebuild(b, 6000), "changed");
        Check.False(w.ShouldRebuild(c, 6500), "and changed back before it settled: nothing to do");
        Check.False(w.ShouldRebuild(c, 9000), "still nothing");
    }
}
