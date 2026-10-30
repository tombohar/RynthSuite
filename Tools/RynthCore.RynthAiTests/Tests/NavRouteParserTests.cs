using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// Smoke tests for .nav route parsing (uTank2 NAV 1.2): each waypoint type's trailer lines,
// EW-before-NS order, a write/read round trip, the metres-to-nav-units Z repair, Follow
// routes, and stopping (not desyncing) on an unknown waypoint type.
internal static class NavRouteParserTests
{
    public static void Register(Runner r)
    {
        r.Add("nav: parses every waypoint type's trailer", ParsesEveryType);
        r.Add("nav: write then read gives the same route", RoundTrip);
        r.Add("nav: Z stored in metres is converted to nav units", MetresZRepair);
        r.Add("nav: Follow route names the leader, no points", FollowRoute);
        r.Add("nav: unknown waypoint type stops with a warning", UnknownTypeStops);
    }

    // One point of each kind. Layout per point: type, EW, NS, Z, flag, then the trailer.
    private static List<string> SampleLines() => new()
    {
        "uTank2 NAV 1.2",
        "1",            // Circular
        "8",
        // Point
        "0", "12.5", "-30.25", "0.1", "0",
        // Recall: spell id
        "2", "12.5", "-30.25", "0.1", "0", "48",
        // Pause: ms
        "3", "12.5", "-30.25", "0.1", "0", "2500",
        // Chat: command
        "4", "12.5", "-30.25", "0.1", "0", "/vt settings load hunt",
        // OpenVendor: id, name
        "5", "13", "-31", "0.1", "0", "2147483900", "Grocer Jon",
        // Portal: name, class, tie, EW, NS, Z of the portal object
        "6", "14", "-32", "0.2", "0", "Portal to Town Network", "14", "True", "14.01", "-32.02", "0.21",
        // Checkpoint: no trailer
        "8", "15", "-33", "0.3", "0",
        // Jump: heading, shift, ms
        "9", "16", "-34", "0.4", "0", "270", "True", "650",
    };

    private static void ParsesEveryType()
    {
        var warnings = new List<string>();
        var route = NavRouteParser.LoadFromLines(SampleLines(), warnings.Add);

        Check.Null(route.LoadWarning, "no load warning");
        Check.Eq(warnings.Count, 0, "warn callback not called");
        Check.Eq(route.RouteType, NavRouteType.Circular, "route type");
        Check.Eq(route.Points.Count, 8, "point count");
        if (route.Points.Count != 8) return;

        var p = route.Points;
        Check.Eq(p[0].Type, NavPointType.Point, "[0] type");
        Check.Near(p[0].EW, 12.5, 1e-9, "[0] EW is the first coordinate line");
        Check.Near(p[0].NS, -30.25, 1e-9, "[0] NS is the second coordinate line");
        Check.Near(p[0].Z, 0.1, 1e-9, "[0] Z");

        Check.Eq(p[1].Type, NavPointType.Recall, "[1] type");
        Check.Eq(p[1].SpellId, 48, "[1] recall spell id");
        Check.Eq(p[2].PauseTimeMs, 2500, "[2] pause ms");
        Check.Eq(p[3].ChatCommand, "/vt settings load hunt", "[3] chat command");
        Check.Eq(p[4].VendorId, 2147483900u, "[4] vendor id (above int.MaxValue)");
        Check.Eq(p[4].TargetName, "Grocer Jon", "[4] vendor name");

        Check.Eq(p[5].Type, NavPointType.PortalNPC, "[5] type");
        Check.Eq(p[5].TargetName, "Portal to Town Network", "[5] portal name");
        Check.Eq(p[5].ObjectClass, 14, "[5] object class");
        Check.True(p[5].IsTie, "[5] tie flag");
        Check.Near(p[5].PortalExitEW, 14.01, 1e-9, "[5] portal object EW");
        Check.Near(p[5].PortalExitNS, -32.02, 1e-9, "[5] portal object NS");

        Check.Eq(p[6].Type, NavPointType.Checkpoint, "[6] type");
        Check.True(NavRouteParser.IsPlainWaypoint(p[6].Type), "Checkpoint is walked like a Point");
        Check.False(NavRouteParser.IsPlainWaypoint(p[1].Type), "Recall is not a plain waypoint");

        Check.Eq(p[7].Type, NavPointType.Jump, "[7] type");
        Check.Near(p[7].JumpHeading, 270, 1e-9, "[7] jump heading");
        Check.True(p[7].JumpShift, "[7] jump shift");
        Check.Near(p[7].JumpMs, 650, 1e-9, "[7] jump ms");
    }

    private static void RoundTrip()
    {
        var first = NavRouteParser.LoadFromLines(SampleLines());
        var lines = first.ToLines();
        var second = NavRouteParser.LoadFromLines(lines);

        Check.Null(second.LoadWarning, "re-read has no warning");
        Check.Eq(second.RouteType, first.RouteType, "route type survives");
        Check.Eq(second.Points.Count, first.Points.Count, "point count survives");
        for (int i = 0; i < first.Points.Count && i < second.Points.Count; i++)
            Check.Eq(second.Points[i].ToString(), first.Points[i].ToString(), $"point [{i}] survives");
        // The writer's output is stable: writing the re-read route gives the same lines.
        Check.Eq(string.Join("|", second.ToLines()), string.Join("|", lines), "second write matches the first");
    }

    private static void MetresZRepair()
    {
        var route = NavRouteParser.LoadFromLines(new[]
        {
            "uTank2 NAV 1.2", "2", "2",
            "0", "1", "2", "120", "0",   // 120 m: stored by the old UI in metres
            "0", "1", "2", "4.5", "0",   // 4.5 nav units = 1080 m: kept as is
        });
        Check.Eq(route.RouteType, NavRouteType.Linear, "route type");
        Check.Eq(route.Points.Count, 2, "point count");
        if (route.Points.Count != 2) return;
        Check.Near(route.Points[0].Z, 0.5, 1e-9, "120 (metres) becomes 0.5 nav units");
        Check.Near(route.Points[1].Z, 4.5, 1e-9, "4.5 is already nav units");
    }

    private static void FollowRoute()
    {
        var route = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "3", "Some Leader", "1342177281" });
        Check.Eq(route.RouteType, NavRouteType.Follow, "route type");
        Check.Eq(route.FollowTargetName, "Some Leader", "leader name");
        Check.Eq(route.FollowTargetId, 1342177281u, "leader id");
        Check.Eq(route.Points.Count, 0, "no points");
        Check.Eq(string.Join("|", route.ToLines()), "uTank2 NAV 1.2|3|Some Leader|1342177281", "written back unchanged");
    }

    private static void UnknownTypeStops()
    {
        var warnings = new List<string>();
        var route = NavRouteParser.LoadFromLines(new[]
        {
            "uTank2 NAV 1.2", "4", "3",
            "0", "1", "2", "0", "0",
            "1", "1", "2", "0", "0",     // type 1: layout unknown, must not be guessed
            "0", "5", "6", "0", "0",
        }, warnings.Add);
        Check.Eq(route.RouteType, NavRouteType.Once, "route type");
        Check.Eq(route.Points.Count, 1, "only the point before the unknown one is kept");
        Check.NotNull(route.LoadWarning, "load warning set");
        Check.True(route.LoadWarning?.Contains("unknown waypoint type 1") == true, "warning names the type");
        Check.Eq(warnings.Count, 1, "warn callback called once");

        var bad = NavRouteParser.LoadFromLines(new[] { "not a nav file", "1", "0" });
        Check.Eq(bad.Points.Count, 0, "wrong header: empty route");
        Check.Null(bad.LoadWarning, "wrong header: silently empty (no warning)");
    }
}
