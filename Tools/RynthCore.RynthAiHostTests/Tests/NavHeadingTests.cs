using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// /ra navheading walks round what's in the way (owner, 2026-11-06: "nav route is not navving around
// obstacles"): RynthNav's corners become a Once route, in nav coordinates, doorways kept.
internal static class NavHeadingTests
{
    public static void Register(Runner r)
    {
        r.Add("navheading: RynthNav's corners become a Once route in nav coordinates, doorways kept", CornersToRoute);
    }

    private static double World(double navUnits) => (navUnits * 10.0 + 1019.5) * 24.0;

    private static void CornersToRoute()
    {
        var corners = new[]
        {
            new OutdoorHunt.Vec(World(56.0), World(42.4), 240.0),
            new OutdoorHunt.Vec(World(56.1), World(42.5), 480.0, Door: true),
        };
        var route = RynthAiPlugin.HeadingRoute(corners);
        Check.True(route.RouteType == NavRouteType.Once, "a Once route");
        Check.True(route.Points.Count == 2, "one point per corner");
        Check.True(Math.Abs(route.Points[0].EW - 56.0) < 1e-9 && Math.Abs(route.Points[0].NS - 42.4) < 1e-9, "x is EW, y is NS");
        Check.True(Math.Abs(route.Points[1].Z - 2.0) < 1e-9, "z in 240-yard units");
        Check.True(!route.Points[0].Doorway && route.Points[1].Doorway, "doorway corners stay doorways");
        Check.True(route.Points[0].Type == NavPointType.Point, "plain points");
    }
}
