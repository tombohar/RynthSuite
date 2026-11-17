using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// /ra dunnav-to <object id> (2026-11-15): a walk on the dungeon map to an object's own cell (any
// floor), for a script's walks to the monsters still to be killed after an explore.
// On DunRouteTests' sample dungeon (the start in the hall's middle, the west room two rooms away).
internal static class DunNavToTests
{
    public static void Register(Runner r)
    {
        r.Add("dunnav-to: walks to an object's cell on the dungeon map, the macro on; refuses an unknown one or one outside the dungeon", WalksToObject);
    }

    private const uint Rat = 0x7E7E9101, Outside = 0x7E7E9102, Unknown = 0x7E7E9103;

    private static void WalksToObject()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-dunnav-to");
        FakeHost.Names[Rat] = "Threadbound Rat";
        FakeHost.Positions[Rat] = (rig.D.West, -17f, 25f, 0f);
        rig.S.IsMacroRunning = false;

        var said = rig.Typed("/ra dunnav-to 0x7E7E9101");
        Check.True(said.Count == 1 && said[0].StartsWith("[RynthAi] DunNav-To: 'Threadbound Rat' 0x7E7E9101") && said[0].Contains("walking there"),
                   "it answers: " + string.Join(" | ", said));
        var pts = rig.S.CurrentRoute.Points;
        Check.True(pts.Count >= 2, $"a walk ({pts.Count} points)");
        Check.Eq(rig.S.CurrentRoute.RouteType, NavRouteType.Once, "a Once walk");
        Check.True(rig.S.IsMacroRunning && rig.S.EnableNavigation && rig.S.ActiveNavIndex == 0, "macro and navigation on, from its start");
        var last = pts[^1];
        double lx = DungeonGeometry.NavToWorld(last.EW) - DunRouteTests.Ox, ly = DungeonGeometry.NavToWorld(last.NS) - DunRouteTests.Oy;
        Check.True(Math.Abs(lx - -17) < 3 && Math.Abs(ly - 25) < 3, $"it ends at the rat ({lx:F1}, {ly:F1})");
        // Through w1 (x -10..0, y 22.35..27.65), the one way into the west room: a leg crosses x = -5 inside it.
        bool throughW1 = false;
        double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW) - DunRouteTests.Ox;
        double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS) - DunRouteTests.Oy;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double ax = X(pts[i]), bx = X(pts[i + 1]);
            if ((ax - -5) * (bx - -5) > 0 || Math.Abs(bx - ax) < 1e-6) continue;
            double y = Y(pts[i]) + (Y(pts[i + 1]) - Y(pts[i])) * ((-5 - ax) / (bx - ax));
            throughW1 |= y > 22.35 && y < 27.65;
        }
        Check.True(throughW1, "through w1, the way to the west room");

        // Decimal ids work too; an unknown object, or one outside the dungeon, is refused (nothing changed).
        var before = rig.S.CurrentRoute;
        said = rig.Typed("/ra dunnav-to " + Unknown.ToString());
        Check.True(said.Count == 1 && said[0].Contains("has no position the client knows"), "unknown: " + string.Join(" | ", said));
        FakeHost.Positions[Outside] = (0xA9B40020, 10f, 10f, 0f);
        said = rig.Typed("/ra dunnav-to 0x7E7E9102");
        Check.True(said.Count == 1 && said[0].Contains("isn't in this dungeon"), "outside: " + string.Join(" | ", said));
        said = rig.Typed("/ra dunnav-to");
        Check.True(said.Count == 1 && said[0].Contains("Usage: /ra dunnav-to <object id>"), "no id: the usage");
        Check.True(ReferenceEquals(rig.S.CurrentRoute, before), "the walk in hand is left alone");

        // In a Thread on DreamWeave it is off, as the patrol and the explore are.
        var thread = new DunRouteTests.Rig("NavProfiles-dunnav-to-thread", world: "DreamWeave");
        FakeHost.Names[Rat] = "Threadbound Rat";
        FakeHost.Positions[Rat] = (thread.D.West, -17f, 25f, 0f);
        thread.S.IsMacroRunning = false;
        ServerPolicy.NoteChat("[REALM] id=0 inst=7841 eph=1 name=Base World", thread.D.Start);
        var routeBefore = thread.S.CurrentRoute;
        said = thread.Typed("/ra dunnav-to 0x7E7E9101");
        Check.True(said.Count == 1 && said[0].Contains("turned off in Threads"), "in a Thread on DreamWeave: refused: " + string.Join(" | ", said));
        Check.False(thread.S.IsMacroRunning || thread.S.EnableNavigation, "nothing started");
        Check.True(ReferenceEquals(thread.S.CurrentRoute, routeBefore), "no walk laid");
        ServerPolicy.Forget();
    }
}
