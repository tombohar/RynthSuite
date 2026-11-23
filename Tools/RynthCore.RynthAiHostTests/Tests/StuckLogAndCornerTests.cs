using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using T = RynthCore.Plugin.RynthAi.LegacyUi.NavPointType;
using RT = RynthCore.Plugin.RynthAi.LegacyUi.NavRouteType;

namespace RynthCore.RynthAiHostTests.Tests;

// Wave 1 of "any object in the world that may stop the bot" (2026-11-18), the runtime half:
//   - walks along RynthNav's corners (hunt walks, planned GoTo walks, navmesh detours) turn a corner early only
//     where RynthNav's raycast to the next corner is clear, else at the doorway reach (1 yd); a meta's route is
//     unchanged;
//   - every "Nav: stuck xN" line, the hunt's snag line and the explore's stuck-for-good line carry where it was
//     (cell, local x/y/z, heading, desired heading, target and its coordinates, metres moved): only 2 logged
//     dungeon stucks had a position, and the 0x01F5 and A9B4 replays failed for want of z;
//   - the stuck recovery outdoors asks FindPathEx with the hunt's flags (portals kept out of, 1 m off walls);
//   - a script's ai.GoTo is planned (RynthNav outdoors, the dungeon map in a dungeon), straight when neither answers.
// RynthNav is faked behind the host's GetPluginInterface (FakeRynthNav below).
internal static unsafe class StuckLogAndCornerTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("corner gate (11-18): a hunt walk with RynthNav's raycast blocked keeps the 1 yd reach; clear, it turns at 1.5 yd", HuntWalkGate);
        r.Add("corner gate (11-18): no lookahead blend toward the next corner while the raycast is blocked (a 3 yd reach)", HuntWalkBlend);
        r.Add("corner gate (11-18): a hunt walk's last point is reached at 1.5 yd, no raycast asked", HuntWalkEnd);
        r.Add("corner gate (11-18): a meta / VTank route turns at 1.5 yd as before and never asks the raycast", MetaRouteUnchanged);
        r.Add("corner gate (11-18): a navmesh detour holds its corners while the raycast is blocked; another planner's doesn't", DetourGate);
        r.Add("stuck log (11-18): every Nav: stuck line ends with cell, local x/y/z, headings, target and its spot, metres moved", StuckLineFields);
        r.Add("stuck log (11-18): stuck on a detour, the line names the detour point it was heading for", StuckOnDetourNamesPoint);
        r.Add("stuck log (11-18): the hunt's snag line carries the stuck spot", HuntSnagLine);
        r.Add("nav recovery outdoors (11-18): FindPathEx with the hunt's flags; a partial way walks to its end", RecoveryFindPathEx);
        r.Add("script GoTo (11-18): outdoors, RynthNav's way is the route, ending on the point asked for", GoToPlanned);
        r.Add("script GoTo (11-18): a partial way ends with the point asked for; no RynthNav way: the straight route as before", GoToFallbacks);
        r.Add("script GoTo (11-18): in a dungeon, the dungeon map's walk to the point", GoToDungeon);
    }

    // ── Rig: the nav engine on a fake clock, the character where each test puts it ──────────────

    private const double NS0 = 42.0, EW0 = 33.0;   // nav coordinates of the origin (the landscape near Holtburg)
    private static long _now;

    private sealed class Nav
    {
        public readonly LegacyUiSettings S;
        public readonly NavigationEngine E;
        public int RayCalls;
        public bool RayBlocked;
        public (double Ax, double Ay, double Az, double Bx, double By, double Bz) LastRay;

        /// <summary>Call FakeHost.Reset() and At(...) first.</summary>
        public Nav(NavRouteParser route, INavRecoveryPlanner? planner = null, bool raycast = true)
        {
            _now = 1_000_000;
            NavigationEngine.Clock = static () => _now;
            var host = FakeHost.Create();
            S = new LegacyUiSettings { IsMacroRunning = true, EnableNavigation = true, CurrentRoute = route, ActiveNavIndex = 0 };
            E = new NavigationEngine(host, S);
            if (planner != null) E.SetRecoveryPlanner(planner);
            if (raycast)
                E.NavmeshRaycast = (ax, ay, az, bx, by, bz) =>
                {
                    RayCalls++;
                    LastRay = (ax, ay, az, bx, by, bz);
                    return RayBlocked ? RynthNavPathApiV1.RayBlocked : RynthNavPathApiV1.RayClear;
                };
        }

        public void Tick(int ms = 40) { _now += ms; E.Tick(); }
        public void Run(double seconds) { int n = (int)Math.Round(seconds * 10); for (int i = 0; i < n; i++) Tick(100); }
        public int Idx => S.ActiveNavIndex;
        public string Recovery => E.GetStateSnapshot().Recovery;
    }

    /// <summary>The character (north, east) yards from the origin, at height z.</summary>
    private static void At(double north, double east, float z = 0f) => FakeHost.SetNavPosition(NS0 + north / 240.0, EW0 + east / 240.0, z);

    private static NavPoint Pt(double north, double east, double zYd = 0)
        => new() { Type = T.Point, NS = NS0 + north / 240.0, EW = EW0 + east / 240.0, Z = zYd / 240.0 };

    private static NavRouteParser Route(RT type, bool hunt, params NavPoint[] pts)
    {
        var r = new NavRouteParser { RouteType = type, OutdoorHuntWalk = hunt };
        r.Points.AddRange(pts);
        return r;
    }

    private static double World(double nav) => (nav * 10.0 + 1019.5) * 24.0;

    private static List<string> StuckLines() => FakeHost.Logs.Where(l => l.StartsWith("Nav: stuck x", StringComparison.Ordinal)).ToList();

    private static string Field(string line, string key)
    {
        int i = line.IndexOf(" " + key + "=", StringComparison.Ordinal);
        if (i < 0 && line.Contains("| " + key + "=")) i = line.IndexOf("| " + key + "=", StringComparison.Ordinal);
        if (i < 0) return "";
        int s = i + key.Length + 2;
        int e = line.IndexOf(' ', s);
        return e < 0 ? line[s..] : line[s..e];
    }

    private static (double X, double Y, double Z) Triple(string v)
    {
        var p = v.Trim('(', ')').Split(',');
        return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture));
    }

    /// <summary>A planner that answers with the given points (nav coordinates), marked as navmesh corners or not.</summary>
    private sealed class PointsPlanner : INavRecoveryPlanner
    {
        public readonly List<NavPoint> Points = new();
        public bool Navmesh;
        public int Plans;

        public NavRecoveryPlan? TryPlan(NavPoint target, out string why)
        {
            Plans++;
            why = string.Empty;
            var plan = new NavRecoveryPlan { Planner = Navmesh ? "RynthNav navmesh" : "dungeon map", LengthYd = 20, NavmeshCorners = Navmesh };
            plan.Points.AddRange(Points.Select(p => new NavPoint { Type = p.Type, NS = p.NS, EW = p.EW, Z = p.Z }));
            return plan;
        }

        public NavAreaCheck CheckArea(uint playerCell, double tNs, double tEw, out string routeArea, out string playerArea)
        {
            routeArea = playerArea = string.Empty;
            return NavAreaCheck.Match;
        }
    }

    // ── Corner gate ────────────────────────────────────────────────────────────────────────────

    // An L-shaped hunt walk: north 10 yd, then east 10 yd, then north 10 yd.
    private static NavRouteParser LWalk(bool hunt) => Route(RT.Once, hunt, Pt(10, 0), Pt(10, 10), Pt(20, 10));

    private static void HuntWalkGate()
    {
        FakeHost.Reset();
        At(0, 0);
        var nav = new Nav(LWalk(hunt: true)) { RayBlocked = true };
        nav.Tick();
        Check.Eq(nav.RayCalls, 0, "far from the corner: no raycast");
        At(8.8, 0); nav.Tick();
        Check.Eq(nav.Idx, 0, "1.2 yd from the corner, the raycast to the next one blocked: not turned yet");
        Check.True(nav.RayCalls > 0, "the raycast was asked");
        var r = nav.LastRay;
        var next = nav.S.CurrentRoute.Points[1];
        Check.True(Math.Abs(r.Bx - World(next.EW)) < 1e-6 && Math.Abs(r.By - World(next.NS)) < 1e-6, "from the character to the next corner (world x east, y north)");
        var pose = FakeHost.PlayerPose;
        Check.True(Math.Abs(r.Ax - (((pose.Cell >> 24) & 0xFF) * 192.0 + pose.X)) < 1e-3, "from where the character stands");
        At(9.2, 0); nav.Tick();
        Check.Eq(nav.Idx, 1, "within the 1 yd doorway reach: on to the next corner");
        Check.True(FakeHost.Logs.Any(l => l.Contains("arrived at [0]") && l.Contains("corner held to 1.0yd")), "the arrival line says the corner was held");

        nav.RayBlocked = false;
        int before = nav.RayCalls;
        At(10, 8.8); nav.Tick();
        Check.True(nav.RayCalls > before, "asked again at the next corner");
        Check.Eq(nav.Idx, 2, "the raycast clear: turned at 1.2 yd, inside the 1.5 yd reach, as before");
        Check.False(FakeHost.Logs.Any(l => l.Contains("arrived at [1]") && l.Contains("corner held")), "nothing held there");
    }

    private static void HuntWalkBlend()
    {
        // A 3 yd reach blends the aim toward the next corner from 4 yd out (NavigationEngine.CornerLookaheadYards).
        double DesiredAt(bool blocked)
        {
            FakeHost.Reset();
            At(0, 0);
            FakeHost.HeadingDeg = 0;
            var nav = new Nav(LWalk(hunt: true)) { RayBlocked = blocked };
            nav.S.FollowNavMin = 3f;
            nav.S.NavLookaheadYards = 4f;
            nav.Tick();
            At(6.5, 0); nav.Tick();   // 3.5 yd short: outside the reach, inside the blend
            Check.Eq(nav.Idx, 0, "still heading for the corner");
            return (double)typeof(NavigationEngine).GetField("_lastDesiredDeg", Any)!.GetValue(nav.E)!;
        }
        double held = DesiredAt(blocked: true), open = DesiredAt(blocked: false);
        Check.True(Math.Abs(held) < 0.01 || Math.Abs(held - 360) < 0.01, $"blocked: straight at the corner, due north ({held:F1})");
        Check.True(open > 1.0 && open < 45.0, $"clear: blended toward the next corner, east of north ({open:F1})");
    }

    private static void HuntWalkEnd()
    {
        FakeHost.Reset();
        At(0, 0);
        var nav = new Nav(Route(RT.Once, hunt: true, Pt(10, 0))) { RayBlocked = true };
        nav.Tick();
        At(8.8, 0); nav.Tick();
        Check.Eq(nav.Idx, 1, "the walk's last point at 1.2 yd: arrived, as before");
        Check.Eq(nav.RayCalls, 0, "no corner after it, no raycast");
    }

    private static void MetaRouteUnchanged()
    {
        foreach (var type in new[] { RT.Once, RT.Circular, RT.Linear })
        {
            FakeHost.Reset();
            At(0, 0);
            var route = Route(type, hunt: false, Pt(10, 0), Pt(10, 10), Pt(20, 10));
            var nav = new Nav(route) { RayBlocked = true };
            nav.Tick();
            At(8.8, 0); nav.Tick();
            Check.Eq(nav.Idx, 1, $"a {type} route (a meta's, VTank's or a saved one) turns at 1.2 yd as before");
            At(10, 8.8); nav.Tick();
            Check.Eq(nav.Idx, 2, $"and at the next corner");
            Check.Eq(nav.RayCalls, 0, "the raycast is never asked");
        }
        // A loaded route that once was a hunt walk (saved to a file and loaded) is a plain route.
        FakeHost.Reset();
        At(0, 0);
        var saved = Route(RT.Once, hunt: false, Pt(10, 0), Pt(10, 10));
        var n2 = new Nav(saved) { RayBlocked = true };
        n2.Tick(); At(8.8, 0); n2.Tick();
        Check.Eq(n2.Idx, 1, "no flag, no gate");
    }

    private static void DetourGate()
    {
        foreach (bool navmesh in new[] { true, false })
        {
            FakeHost.Reset();
            At(0, 0);
            var planner = new PointsPlanner { Navmesh = navmesh };
            planner.Points.Add(Pt(0, 10));
            planner.Points.Add(Pt(10, 10));
            var nav = new Nav(Route(RT.Once, hunt: false, Pt(30, 0)), planner) { RayBlocked = true };
            nav.Run(11);   // stuck x1 (jump), x2: the detour
            Check.True(nav.Recovery.Contains("point 1/2"), "a detour, at its first point: " + nav.Recovery);
            int calls = nav.RayCalls;
            At(0, 8.8); nav.Tick();
            if (navmesh)
            {
                Check.True(nav.Recovery.Contains("point 1/2"), "navmesh detour, raycast blocked: the corner is held at 1.2 yd: " + nav.Recovery);
                Check.True(nav.RayCalls > calls, "the raycast was asked");
                At(0, 9.2); nav.Tick();
                Check.True(nav.Recovery.Contains("point 2/2"), "within 1 yd: on to the next: " + nav.Recovery);
            }
            else
            {
                Check.True(nav.Recovery.Contains("point 2/2"), "another planner's detour turns at 1.2 yd as before: " + nav.Recovery);
                Check.Eq(nav.RayCalls, calls, "and never asks the raycast");
            }
        }
    }

    // ── Stuck lines ────────────────────────────────────────────────────────────────────────────

    private static void StuckLineFields()
    {
        FakeHost.Reset();
        At(0, 0, 12.5f);
        FakeHost.HeadingDeg = 0;
        var nav = new Nav(Route(RT.Circular, hunt: false, Pt(30, 0, 12.5)), raycast: false);
        nav.Run(16);   // frozen with autorun on: three watchdog windows, three stucks (jump, escape, escape)
        var lines = StuckLines();
        Check.True(lines.Count >= 3, "a stuck line per stuck: " + string.Join(" || ", lines));
        Check.True(lines[0].Contains(" jump | cell="), "the jump rung has a line of its own now: " + lines[0]);
        Check.True(lines.Any(l => l.Contains("escape burst")), "the escape lines too");

        var pose = FakeHost.PlayerPose;
        double tx = World(EW0) - ((pose.Cell >> 24) & 0xFF) * 192.0, ty = World(NS0 + 30 / 240.0) - ((pose.Cell >> 16) & 0xFF) * 192.0;
        foreach (string l in lines)
        {
            Check.True(l.Contains(" | cell=0x"), "the spot after ' | ': " + l);
            Check.Eq(Field(l, "cell"), $"0x{pose.Cell:X8}", "the cell id: " + l);
            var at = Triple(Field(l, "at"));
            Check.True(Math.Abs(at.X - pose.X) < 0.01 && Math.Abs(at.Y - pose.Y) < 0.01 && Math.Abs(at.Z - 12.5) < 0.01, "local x/y/z: " + l);
            Check.True(Field(l, "heading").Length > 0 && Field(l, "heading") != "?", "the heading: " + l);
            Check.True(Field(l, "desired") == "0", "the heading nav wanted (due north to the point): " + l);
            Check.Eq(Field(l, "target"), "[0]", "the target index: " + l);
            var to = Triple(Field(l, "to"));
            Check.True(Math.Abs(to.X - tx) < 0.01 && Math.Abs(to.Y - ty) < 0.01 && Math.Abs(to.Z - 12.5) < 0.01, $"the target's local x/y/z ({tx:F2},{ty:F2}): " + l);
            Check.True(l.Contains(" 30.0m moved="), "how far off the target is: " + l);
            string moved = Field(l, "moved");
            Check.True(moved.StartsWith("0.00m/5.", StringComparison.Ordinal), "metres moved in the watchdog window: " + l);
            Check.Eq(Field(l, "walk"), "Circular", "whose walk: " + l);
        }
        var where = nav.E.LastStuckWhere;
        Check.True(where.Length > 0 && lines[^1].EndsWith(where, StringComparison.Ordinal), "LastStuckWhere is the last line's spot");
        Check.True(nav.E.LastStuckNote().Contains("last nav stuck x"), "and a note for later lines: " + nav.E.LastStuckNote());
    }

    private static void StuckOnDetourNamesPoint()
    {
        FakeHost.Reset();
        At(0, 0);
        var planner = new PointsPlanner();
        planner.Points.Add(Pt(5, 5));
        planner.Points.Add(Pt(10, 5));
        var nav = new Nav(Route(RT.Once, hunt: false, Pt(30, 0)), planner, raycast: false);
        nav.Run(11);   // x1 jump, x2 detour
        Check.True(nav.Recovery.StartsWith("detour #1", StringComparison.Ordinal), "a detour started: " + nav.Recovery);
        Check.True(StuckLines().Any(l => l.Contains(" detour, recovery #1 | cell=")), "the stuck that started it has its line: " + string.Join(" || ", StuckLines()));
        nav.Run(5.5);  // still frozen: stuck on the detour
        var last = StuckLines().Last();
        Check.Eq(Field(last, "target"), "[0]/detour#1:1/2", "the detour point it was heading for: " + last);
        var to = Triple(Field(last, "to"));
        var pose = FakeHost.PlayerPose;
        double tx = World(EW0 + 5 / 240.0) - ((pose.Cell >> 24) & 0xFF) * 192.0;
        Check.True(Math.Abs(to.X - tx) < 0.01, "at the detour point's spot: " + last);
    }

    private sealed class HuntRig
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyDashboardRenderer Dash;
        public readonly NavigationEngine E;
        public LegacyUiSettings S => Dash.Settings;

        public HuntRig()
        {
            FakeHost.Reset();
            _now = 1_000_000;
            NavigationEngine.Clock = static () => _now;
            var host = FakeHost.Create();
            typeof(RynthCore.PluginCore.RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            Dash = new LegacyDashboardRenderer(host);
            Set("_dashboard", Dash);
            E = new NavigationEngine(host, Dash.Settings);
            Set("_navigationEngine", E);
        }

        public object? Get(string f) => typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public void Call(string m) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, null);
    }

    private static void HuntSnagLine()
    {
        var g = new HuntRig();
        At(0, 0, 30f);
        g.S.IsMacroRunning = true;
        var hunt = (OutdoorHunt)g.Get("_hunt")!;
        var me = FakeHost.PlayerPose;
        hunt.Start(DateTime.UtcNow, new OutdoorHunt.Vec(((me.Cell >> 24) & 0xFF) * 192.0 + me.X, ((me.Cell >> 16) & 0xFF) * 192.0 + me.Y, 30));
        var walk = Route(RT.Once, hunt: true, Pt(40, 0, 30));
        g.Set("_huntRoute", walk);
        g.S.CurrentNavPath = string.Empty;
        g.S.CurrentRoute = walk;
        g.S.ActiveNavIndex = 0;
        g.S.EnableNavigation = true;
        for (int i = 0; i < 160; i++) { _now += 100; g.E.Tick(); }   // 16 s frozen: three stucks at one spot
        Check.True(g.E.StuckSpotHits >= NavigationEngine.StuckSpotRepeatHits, $"stuck {g.E.StuckSpotHits} times at the spot");
        g.Call("TickOutdoorHunt");
        var snag = FakeHost.Logs.FirstOrDefault(l => l.StartsWith("[RynthAi] Hunt: snag x", StringComparison.Ordinal));
        Check.NotNull(snag, "a snag line: " + string.Join(" || ", FakeHost.Logs.TakeLast(8)));
        Check.True(snag!.EndsWith("| " + g.E.LastStuckWhere, StringComparison.Ordinal), "with the stuck spot: " + snag);
        Check.True(Field(snag, "cell").StartsWith("0x", StringComparison.Ordinal) && Field(snag, "at").Contains(",30.00)") && Field(snag, "walk") == "hunt",
                   "cell, z and the walk's kind: " + snag);
    }

    // ── A fake RynthNav behind GetPluginInterface ──────────────────────────────────────────────

    private const uint OutdoorCell = 0xA9B40021;   // Holtburg's landblock, an outdoor cell
    private static double NavOf(double world) => (world / 24.0 - 1019.5) / 10.0;

    internal static class FakeRynthNav
    {
        public static bool Loaded = true;
        public static uint Version = 3;
        public static int Code = RynthNavPathApiV1.ResultOk;
        /// <summary>The corners FindPathEx / FindPath answer with, the start first (world x east, y north, z).</summary>
        public static readonly List<(double X, double Y, double Z)> Corners = new();
        public static int PathExCalls, PathCalls, RayCalls, LastFlags;
        public static (double Sx, double Sy, double Sz, double Gx, double Gy, double Gz) LastPath;
        private static RynthNavPathApiV1* _table;

        public static void Reset()
        {
            Loaded = true; Version = 3; Code = RynthNavPathApiV1.ResultOk;
            Corners.Clear();
            PathExCalls = PathCalls = RayCalls = LastFlags = 0;
            LastPath = default;
        }

        /// <summary>Hooks GetPluginInterface into the next FakeHost.Create (call after FakeHost.Reset).</summary>
        public static void Install() => FakeHost.ConfigureApi = (ref RynthCoreApiNative api) =>
        {
            api.GetPluginInterfaceFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr>)&GetIface;
            api.Version = Math.Max(api.Version, 68u);
        };

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr GetIface(IntPtr plugin, IntPtr iface, uint version)
        {
            if (!Loaded) return IntPtr.Zero;
            if (Marshal.PtrToStringAnsi(plugin) != RynthNavPathApiV1.PluginName || Marshal.PtrToStringAnsi(iface) != RynthNavPathApiV1.InterfaceName)
                return IntPtr.Zero;
            if (_table == null)
            {
                _table = (RynthNavPathApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthNavPathApiV1));
                _table->FindPath = &FindPath;
                _table->FindPathEx = &FindPathEx;
                _table->Raycast = &Raycast;
            }
            _table->Version = Version;
            _table->StructSize = Version >= 2 ? (uint)sizeof(RynthNavPathApiV1) : RynthNavPathApiV1.SizeV1;
            return (IntPtr)_table;
        }

        private static int Write(double* points, int maxPoints, int* count)
        {
            int n = Code is RynthNavPathApiV1.ResultOk or RynthNavPathApiV1.ResultPartial ? Math.Min(Corners.Count, maxPoints) : 0;
            for (int i = 0; i < n; i++) { points[i * 3] = Corners[i].X; points[i * 3 + 1] = Corners[i].Y; points[i * 3 + 2] = Corners[i].Z; }
            *count = n;
            return n;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int FindPath(double sx, double sy, double sz, double gx, double gy, double gz, double* points, int maxPoints, int* count)
        {
            PathCalls++;
            LastPath = (sx, sy, sz, gx, gy, gz);
            Write(points, maxPoints, count);
            return Code == RynthNavPathApiV1.ResultPartial ? RynthNavPathApiV1.ResultNoPath : Code;   // v1 has no partial
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int FindPathEx(double sx, double sy, double sz, double gx, double gy, double gz, int flags, int maxTiles,
                                      double* points, int maxPoints, int* count, double* info)
        {
            PathExCalls++;
            LastFlags = flags;
            LastPath = (sx, sy, sz, gx, gy, gz);
            int n = Write(points, maxPoints, count);
            if (info != null)
            {
                for (int i = 0; i < RynthNavPathApiV1.InfoLength; i++) info[i] = 0;
                if (n > 0)
                {
                    double len = 0;
                    for (int i = 1; i < n; i++) len += Math.Sqrt(Math.Pow(Corners[i].X - Corners[i - 1].X, 2) + Math.Pow(Corners[i].Y - Corners[i - 1].Y, 2));
                    var e = Corners[n - 1];
                    info[0] = len;
                    info[1] = Code == RynthNavPathApiV1.ResultPartial ? 1 : 0;
                    info[2] = e.X; info[3] = e.Y; info[4] = e.Z;
                    info[5] = Math.Sqrt((gx - e.X) * (gx - e.X) + (gy - e.Y) * (gy - e.Y));
                }
            }
            return Code;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int Raycast(double sx, double sy, double sz, double gx, double gy, double gz)
        {
            RayCalls++;
            return RynthNavPathApiV1.RayClear;
        }
    }

    // ── Recovery outdoors ──────────────────────────────────────────────────────────────────────

    private static void RecoveryFindPathEx()
    {
        FakeHost.Reset();
        FakeRynthNav.Reset();
        FakeRynthNav.Install();
        var host = FakeHost.Create();
        FakeHost.PlayerPose = (OutdoorCell, 100f, 100f, 50f);
        var planner = new NavRecoveryPlanner(host, () => null, () => null);
        double sx = 0xA9 * 192.0 + 100, sy = 0xB4 * 192.0 + 100;
        var target = new NavPoint { Type = T.Point, NS = NavOf(sy + 40), EW = NavOf(sx), Z = 50 / 240.0 };

        FakeRynthNav.Corners.AddRange(new[] { (sx, sy, 50.0), (sx + 8, sy + 20, 50.0), (sx, sy + 40, 50.0) });
        var plan = planner.TryPlan(target, out string why);
        Check.NotNull(plan, "a plan: " + why);
        Check.Eq(FakeRynthNav.PathExCalls, 1, "FindPathEx, not the v1 FindPath");
        Check.Eq(FakeRynthNav.PathCalls, 0, "v1 not asked");
        Check.Eq(FakeRynthNav.LastFlags, RynthAiPlugin.HuntPathFlags, "the hunt's flags (portals, wall clearance, partial, quiet), no doorstep start");
        Check.True(plan!.NavmeshCorners && plan.Planner == "RynthNav navmesh", "navmesh corners: " + plan.Planner);
        Check.Eq(plan.Points.Count, 1, "the turn only (the waypoint is the route's)");

        // Partial: the way ends short of the waypoint; its end is a detour point too.
        FakeRynthNav.Code = RynthNavPathApiV1.ResultPartial;
        FakeRynthNav.Corners.Clear();
        FakeRynthNav.Corners.AddRange(new[] { (sx, sy, 50.0), (sx + 8, sy + 20, 50.0), (sx + 2, sy + 30, 50.0) });
        plan = planner.TryPlan(target, out why);
        Check.NotNull(plan, "a partial way is walked: " + why);
        Check.Eq(plan!.Points.Count, 2, "its turn and its end");
        Check.True(Math.Abs(plan.Points[1].NS - NavOf(sy + 30)) < 1e-9 && plan.Planner.Contains("partial"), "ending where the way ends: " + plan.Planner);

        // A partial way that ends where the character stands: no plan (the escapes, as before).
        FakeRynthNav.Corners.Clear();
        FakeRynthNav.Corners.AddRange(new[] { (sx, sy, 50.0), (sx + 0.5, sy + 0.5, 50.0) });
        plan = planner.TryPlan(target, out why);
        Check.Null(plan, "nowhere to go");
        Check.True(why.Contains("ends where you stand"), why);

        // An older RynthNav (v1): FindPath as before.
        FakeRynthNav.Version = 1;
        FakeRynthNav.Code = RynthNavPathApiV1.ResultOk;
        FakeRynthNav.Corners.Clear();
        FakeRynthNav.Corners.AddRange(new[] { (sx, sy, 50.0), (sx + 8, sy + 20, 50.0), (sx, sy + 40, 50.0) });
        int ex = FakeRynthNav.PathExCalls;
        plan = planner.TryPlan(target, out why);
        Check.NotNull(plan, "v1: a plan: " + why);
        Check.True(FakeRynthNav.PathCalls == 1 && FakeRynthNav.PathExCalls == ex, "v1: FindPath");
        Check.Eq(plan!.Points.Count, 1, "v1: the turn");
    }

    // ── Script GoTo ────────────────────────────────────────────────────────────────────────────

    private static DunRouteTests.Rig GoToRig(string folder)
    {
        FakeRynthNav.Reset();
        var rig = new DunRouteTests.Rig(folder, beforeCreate: FakeRynthNav.Install);
        rig.S.IsMacroRunning = true;   // the stand-in dashboard can't start the macro
        return rig;
    }

    private static void GoToPlanned()
    {
        var rig = GoToRig("NavProfiles-goto-planned");
        FakeHost.PlayerPose = (OutdoorCell, 100f, 100f, 50f);
        double sx = 0xA9 * 192.0 + 100, sy = 0xB4 * 192.0 + 100;
        double gx = sx + 60, gy = sy + 40;
        FakeRynthNav.Corners.AddRange(new[] { (sx, sy, 50.0), (sx + 20, sy + 5, 50.5), (sx + 45, sy + 30, 51.0), (gx + 0.4, gy - 0.3, 51.2) });
        double ns = NavOf(gy), ew = NavOf(gx);

        Check.True(rig.P.ScriptGoTo(ns, ew), "GoTo answers true");
        Check.Eq(FakeRynthNav.PathExCalls, 1, "RynthNav's FindPathEx was asked");
        Check.Eq(FakeRynthNav.LastFlags, RynthAiPlugin.HuntPathFlags | RynthNavPathApiV1.PathStartOutdoors, "with the hunt's flags (as /ra navheading)");
        Check.True(Math.Abs(FakeRynthNav.LastPath.Sx - sx) < 1e-6 && Math.Abs(FakeRynthNav.LastPath.Gx - gx) < 1e-6 && Math.Abs(FakeRynthNav.LastPath.Gy - gy) < 1e-6,
                   "from the character to the point");
        var route = rig.S.CurrentRoute;
        Check.Eq(route.RouteType, RT.Once, "a Once route");
        Check.Eq(route.Points.Count, 3, "RynthNav's corners (the start left off): " + string.Join(" ", route.Points));
        Check.True(route.NavmeshCorners && !route.OutdoorHuntWalk, "marked as navmesh corners (the corner gate), not a hunt walk");
        Check.True(Math.Abs(route.Points[0].EW - NavOf(sx + 20)) < 1e-9 && Math.Abs(route.Points[0].NS - NavOf(sy + 5)) < 1e-9, "the first corner");
        var last = route.Points[^1];
        Check.True(last.NS == ns && last.EW == ew, "the walk ends on the point asked for, exactly");
        Check.True(Math.Abs(last.Z * 240.0 - 51.2) < 1e-6, "at the height RynthNav found there");
        Check.True(rig.S.CurrentNavPath.Length == 0 && rig.S.ActiveNavIndex == 0 && rig.S.EnableNavigation, "no file (ranav[] name \"\"), from [0], nav on");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[RynthAi] GoTo:") && l.Contains("round obstacles")), "logged: " + string.Join(" || ", FakeHost.Logs.TakeLast(3)));

        // Walked to its end, it is finished as the straight route was (a script checks the distance; ranav[] says finished).
        rig.S.ActiveNavIndex = route.Points.Count;
        Check.True(rig.S.NavRouteFinished(), "a Once route at its end: finished");
    }

    private static void GoToFallbacks()
    {
        var rig = GoToRig("NavProfiles-goto-fallback");
        FakeHost.PlayerPose = (OutdoorCell, 100f, 100f, 50f);
        double sx = 0xA9 * 192.0 + 100, sy = 0xB4 * 192.0 + 100;
        double gx = sx + 80, gy = sy;
        double ns = NavOf(gy), ew = NavOf(gx);

        // Partial: the way ends 30 yd short (the point across water, say): its corners, then the point.
        FakeRynthNav.Code = RynthNavPathApiV1.ResultPartial;
        FakeRynthNav.Corners.AddRange(new[] { (sx, sy, 50.0), (sx + 20, sy + 10, 50.0), (sx + 50, sy, 49.0) });
        Check.True(rig.P.ScriptGoTo(ns, ew), "true");
        var route = rig.S.CurrentRoute;
        Check.Eq(route.Points.Count, 3, "two corners and the point: " + string.Join(" ", route.Points));
        Check.True(route.Points[^1].NS == ns && route.Points[^1].EW == ew, "ending on the point asked for");
        Check.True(Math.Abs(route.Points[1].EW - NavOf(sx + 50)) < 1e-9, "the way's end kept as a point");

        // No navmesh here, no way, RynthNav not loaded: the one-point straight route, as before.
        foreach (var (code, loaded, what) in new[] { (RynthNavPathApiV1.ResultNoMesh, true, "no mesh"), (RynthNavPathApiV1.ResultNoPath, true, "no way"), (RynthNavPathApiV1.ResultOk, false, "not loaded") })
        {
            FakeRynthNav.Code = code;
            FakeRynthNav.Loaded = loaded;
            Check.True(rig.P.ScriptGoTo(ns, ew), what + ": true");
            route = rig.S.CurrentRoute;
            Check.True(route.Points.Count == 1 && route.Points[0].NS == ns && route.Points[0].EW == ew && !route.NavmeshCorners,
                       what + ": one straight point: " + string.Join(" ", route.Points));
            Check.True(Math.Abs(route.Points[0].Z * 240.0 - 50.0) < 1e-6, what + ": at the character's height, as before");
            Check.Eq(route.RouteType, RT.Once, what + ": a Once route");
        }
        FakeRynthNav.Loaded = true;

        // In a building's rooms (an indoor cell of a landblock that isn't a dungeon): straight, RynthNav not asked.
        int calls = FakeRynthNav.PathExCalls;
        FakeRynthNav.Code = RynthNavPathApiV1.ResultOk;
        FakeHost.PlayerPose = (OutdoorCell & 0xFFFF0000 | 0x0105, 100f, 100f, 50f);
        Check.True(rig.P.ScriptGoTo(ns, ew), "indoors: true");
        // Without the dats an indoor cell counts as a dungeon (InDungeon): the dungeon map is asked, and it has
        // no map for 0xA9B4 here, so the straight route.
        Check.Eq(rig.S.CurrentRoute.Points.Count, 1, "indoors with no map: the straight route");
        Check.Eq(FakeRynthNav.PathExCalls, calls, "RynthNav isn't asked from inside");
    }

    private static void GoToDungeon()
    {
        var rig = GoToRig("NavProfiles-goto-dungeon");
        // The sample dungeon (DunRouteTests): the character in the hall's middle, the point in the nook north.
        rig.At(15, 15, rig.D.Start);
        double ns = DungeonGeometry.WorldToNav(DunRouteTests.Oy + 52), ew = DungeonGeometry.WorldToNav(DunRouteTests.Ox + 15);
        Check.True(rig.P.ScriptGoTo(ns, ew), "true");
        var route = rig.S.CurrentRoute;
        Check.Eq(route.RouteType, RT.Once, "a Once route");
        Check.True(route.Points.Count > 1, "the dungeon map's walk through the doorways: " + string.Join(" ", route.Points));
        Check.True(route.Points.Any(p => p.Doorway), "doorway points on it");
        Check.True(route.Points[^1].NS == ns && route.Points[^1].EW == ew, "ending on the point asked for");
        Check.Eq(FakeRynthNav.PathExCalls, 0, "RynthNav isn't asked in a dungeon");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[RynthAi] GoTo:") && l.Contains("by the dungeon map")), "logged");

        // A point far from every room of this dungeon: the straight route.
        double farNs = DungeonGeometry.WorldToNav(DunRouteTests.Oy + 400);
        Check.True(rig.P.ScriptGoTo(farNs, ew), "true");
        Check.Eq(rig.S.CurrentRoute.Points.Count, 1, "a point outside the dungeon: straight, as before");
    }
}
