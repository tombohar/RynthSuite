using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Jump steps for routes made by hand, and ranav[] (2026-11-18). Thread 0x00C1's pit, where the fighting is, can
// only be dropped into, and recording a route doesn't see a jump, so a script runs a route per Thread
// ("There are a lot of places jumps are required to reach"):
//   - /ra nav addjump [ms] [shift] adds a Jump step where you stand, facing your way (300 ms by default), saved;
//   - while recording, the next point waits until you have jumped and landed (never one in the air);
//   - ranav[] tells a script which route is loaded, where it is on it and whether a Once route has ended.
internal static class NavJumpStepTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("nav jump step: /ra nav addjump adds a Jump where you stand, facing your way, held 300 ms (or as asked), saved to the file", AddJump);
        r.Add("nav jump step: while recording, no point is laid until you have jumped and landed", RecordingHolds);
        r.Add("nav jump step: ranav[] gives the route's name, place on it, type and whether a Once route ended", RaNav);
    }

    private static DunRouteTests.Rig Rig(string folder, out string navPath)
    {
        var rig = new DunRouteTests.Rig(folder);
        var dash = (LegacyDashboardRenderer)rig.Get("_dashboard")!;
        typeof(LegacyDashboardRenderer).GetField("_navigationUi", Any)!.SetValue(dash, new LegacyNavigationUi(rig.S, new RynthCore.PluginSdk.RynthCoreHost(FakeHost.LastApi)));
        string dir = Path.Combine(Program.TempRoot, folder);
        Directory.CreateDirectory(dir);
        navPath = Path.Combine(dir, "PitIn.nav");
        rig.S.CurrentRoute = new NavRouteParser { RouteType = NavRouteType.Once };
        rig.S.CurrentRoute.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1, EW = 2, Z = 0 });
        rig.S.CurrentNavPath = navPath;
        rig.S.CurrentRoute.Save(navPath);
        return rig;
    }

    private static void AddJump()
    {
        var rig = Rig("NavProfiles-addjump", out string navPath);
        rig.At(12, 20, DunRouteTests.Lb << 16 | 0x0100);
        FakeHost.HeadingDeg = 135;
        var said = rig.Typed("/ra nav addjump");
        Check.True(said.Any(l => l.Contains("Added a jump [1] facing 135") && l.Contains("held 300 ms") && l.Contains("PitIn.nav")), "the reply: " + string.Join(" | ", said));
        var pts = rig.S.CurrentRoute.Points;
        Check.Eq(pts.Count, 2, "one step added");
        NavPoint j = pts[1];
        Check.True(j.Type == NavPointType.Jump && Math.Abs(j.JumpHeading - 135) < 0.2 && j.JumpMs == 300 && !j.JumpShift, $"a Jump facing 135, 300 ms: {j}");
        Check.True(NavCoordinateHelper.TryGetNavPosition(new RynthCore.PluginSdk.RynthCoreHost(FakeHost.LastApi), out double ns, out double ew, out _)
                   && Math.Abs(j.NS - ns) < 1e-6 && Math.Abs(j.EW - ew) < 1e-6, "where you stand");

        var onDisk = NavRouteParser.Load(navPath);
        Check.True(onDisk.Points.Count == 2 && onDisk.Points[1].Type == NavPointType.Jump && Math.Abs(onDisk.Points[1].JumpHeading - 135) < 0.2, "saved to the file");

        FakeHost.HeadingDeg = 10;
        said = rig.Typed("/ra nav addjump 650 shift");
        j = rig.S.CurrentRoute.Points[^1];
        Check.True(j.Type == NavPointType.Jump && j.JumpMs == 650 && j.JumpShift && Math.Abs(j.JumpHeading - 10) < 0.2, $"650 ms, walking: {j}");
        Check.True(said.Any(l => l.Contains("held 650 ms, walking")), "said so: " + string.Join(" | ", said));

        said = rig.Typed("/ra nav addjump 2000");
        Check.Eq(rig.S.CurrentRoute.Points[^1].JumpMs, 1000.0, "held 1000 ms at most");
        said = rig.Typed("/ra nav addjump far");
        Check.True(said.Any(l => l.Contains("Usage: /ra nav addjump")), "a word it doesn't know: the usage");
        Check.Eq(rig.S.CurrentRoute.Points.Count, 4, "and nothing added for it");
    }

    private static void RecordingHolds()
    {
        var rig = Rig("NavProfiles-addjump-rec", out _);
        var t = new NavBreadcrumbTracker(new RynthCore.PluginSdk.RynthCoreHost(FakeHost.LastApi), rig.S, _ => null, _ => { });
        long t0 = Environment.TickCount64;
        Check.False(t.HoldingForJump(0f, t0), "no jump added: no hold");

        t.JumpAdded();
        Check.True(t.HoldingForJump(-60f, t0 + 100), "just added: held");
        Check.True(t.HoldingForJump(-60f, t0 + 900), "standing still on the edge, still under 1.2 s: held");
        Check.True(t.HoldingForJump(-61.5f, t0 + 1300), "falling: held");
        Check.True(t.HoldingForJump(-64f, t0 + 1400), "falling: held");
        Check.True(t.HoldingForJump(-66f, t0 + 1500), "landed this tick: held until it stays");
        Check.True(t.HoldingForJump(-66f, t0 + 1700), "the same height: still from here");
        Check.True(t.HoldingForJump(-66f, t0 + 2100), "still for 0.4 s: held");
        Check.False(t.HoldingForJump(-66f, t0 + 2300), "still for 0.6 s: landed, recording goes on");
        Check.False(t.HoldingForJump(-66f, t0 + 2400), "and the hold is over");

        t.JumpAdded();
        t0 = Environment.TickCount64;
        float z = 0;
        bool held = true;
        long at = t0;
        for (int i = 0; held && i < 100; i++) { at = t0 + i * 100; z -= 0.5f; held = t.HoldingForJump(z, at); }
        Check.True(!held && at - t0 >= NavBreadcrumbTracker.JumpHoldMaxMs - 200 && at - t0 <= NavBreadcrumbTracker.JumpHoldMaxMs + 200,
                   $"never still (a long fall): let go after {NavBreadcrumbTracker.JumpHoldMaxMs} ms ({at - t0})");
    }

    private static void RaNav()
    {
        var rig = Rig("NavProfiles-ranav", out _);
        var e = ExpressionTests.Engine(settings: rig.S);
        rig.S.CurrentRoute.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1.1, EW = 2, Z = 0 });
        rig.S.ActiveNavIndex = 1;
        rig.S.EnableNavigation = true;
        rig.S.IsMacroRunning = true;
        using (var doc = JsonDocument.Parse(e.Evaluate("ranav[]")))
        {
            var j = doc.RootElement;
            Check.Eq(j.GetProperty("name").GetString(), "PitIn", "the file's name");
            Check.Eq(j.GetProperty("points").GetInt32(), 2, "points");
            Check.Eq(j.GetProperty("index").GetInt32(), 1, "index");
            Check.Eq(j.GetProperty("type").GetString(), "Once", "type");
            Check.False(j.GetProperty("finished").GetBoolean(), "not finished");
            Check.True(j.GetProperty("nav").GetBoolean() && j.GetProperty("macro").GetBoolean(), "navigation and the macro on");
        }
        rig.S.ActiveNavIndex = 2;
        using (var doc = JsonDocument.Parse(e.Evaluate("ranav[]")))
            Check.True(doc.RootElement.GetProperty("finished").GetBoolean(), "a Once route at its end: finished");

        rig.S.CurrentRoute = new NavRouteParser { RouteType = NavRouteType.Circular };
        rig.S.CurrentNavPath = string.Empty;
        rig.S.ActiveNavIndex = 0;
        using (var doc = JsonDocument.Parse(e.Evaluate("ranav[]")))
        {
            var j = doc.RootElement;
            Check.True(j.GetProperty("name").GetString() == "" && j.GetProperty("type").GetString() == "Circular" && !j.GetProperty("finished").GetBoolean(),
                       "no file, Circular, never finished: " + j.GetRawText());
        }
        rig.S.CurrentNavPath = @"C:\x\Pit ""odd"".nav";
        using (var doc = JsonDocument.Parse(e.Evaluate("ranav[]")))
            Check.Eq(doc.RootElement.GetProperty("name").GetString(), "Pit \"odd\"", "a name with quotes stays good JSON");
    }
}
