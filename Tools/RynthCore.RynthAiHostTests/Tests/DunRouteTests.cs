using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;

namespace RynthCore.RynthAiHostTests.Tests;

// Dungeon routes made on demand (2026-10-30): /ra dunroute <name> to exit|far|portal <name>|<x> <y> <z>|room <n>,
// /ra dunpatrol-save, /ra dunroute info|where, and the "RynthAi.Routes" interface (Shared/RynthAiRoutesApi.cs)
// that does the same with JSON. One implementation (RynthAiPlugin.DunRoutes.cs, DungeonRouteMaker.cs), so:
//   - routes on a hand-built dungeon (world metres in landblock 0x7E7E, floors at z 0, as PatrolPortalTests):
//     to the exit portal (named like one), to a portal by name, to a spot, to a room, to the far end; what
//     isn't walkable (lava/acid, a drop) or isn't there is refused with the reason; other portals kept out of;
//   - the saved files: VTank format, read back by RynthAi's own parser, run by NavigationEngine to the end, in
//     the dungeon by the engine's own area check; the patrol saved is the lap /ra dunnav-patrol installs;
//   - name rules, the JSON requests (bad ones refused), the jobs (queued, working, done, failed, kept 32);
//   - the plugin: chat commands answer, the interface's jobs run on the tick and wait out portal space;
//   - Tiny Hive on the real dats, when they are on this PC.
internal static class DunRouteTests
{
    public static void Register(Runner r)
    {
        r.Add("dun routes: to the exit portal (named like one): stops at its edge, a Portal step uses it, other portals kept out of", ToExit);
        r.Add("dun routes: the exit is picked by its destination, its name, where you came in, or as the only portal", ExitPick);
        r.Add("dun routes: to a portal by name (exact, else contained, the nearest of several); an unknown name lists the known", ToPortalByName);
        r.Add("dun routes: to a spot in dungeon coordinates ends on it; a spot outside the dungeon is refused", ToPosition);
        r.Add("dun routes: to a room by number, and to the farthest spot on foot", ToRoomAndFar);
        r.Add("dun routes: lava/acid, a drop, no portal known and no map are said as such", Refusals);
        r.Add("dun routes: a route that passes another portal is bent round it", BentRoundAnotherPortal);
        r.Add("dun routes: the saved route is VTank's format, reads back the same, and NavigationEngine runs it to the end in the dungeon", SavedRouteRuns);
        r.Add("dun routes: info says points, length, cells, landblock and whether it is this dungeon's", Info);
        r.Add("dun routes: name rules (the nav rules: no folders, no reserved names, .nav dropped); never overwrite unless asked", NameRules);
        r.Add("dun routes: the command words: <name> to <target> [overwrite], names holding 'to', portal names holding 'to'", CommandWords);
        r.Add("dun routes: the interface's JSON requests: the four ops parse, bad input is refused with the reason", JsonRequests);
        r.Add("dun routes: jobs: queued, working, done, failed; 32 kept, the oldest finished dropped; unknown ids; ids never reused", JobStates);
        r.Add("dun routes: the RynthAi.Routes table answers only its own name and versions; nothing queued without RynthAi", RoutesTable);
        r.Add("dun routes: plugin: /ra dunroute saves to NavProfiles and answers; again without overwrite refused, with it replaced", PluginDunRoute);
        r.Add("dun routes: plugin: /ra dunpatrol-save writes the lap /ra dunnav-patrol installs, read back by RynthAi's nav parser", PluginPatrolSave);
        r.Add("dun routes: plugin: the patrol save asks the server policy as dunnav-patrol does; dunroute isn't refused", PluginPatrolPolicy);
        r.Add("dun routes: plugin: interface jobs run on the tick, wait out portal space, give up after 30 s, answer JSON", PluginJobs);
        r.Add("dun routes: plugin: where lists the portals with distances and the exit; arrival is where the character landed", PluginWhere);
        r.Add("dun routes: Tiny Hive 0x0289 on the real dats: a route to the Surface Portal and to the far end (if the dats are here)", TinyHiveRealDats);
        r.Add("dun doorways: the doorway flags survive save and reload (NavProfiles\\.rynthai-routes.json); the .nav stays VTank's", DoorwaysSurviveReload);
        r.Add("dun doorways: an edited file gets no flags (one log line); a nav the route maker didn't write is never touched", DoorwaysEditedFile);
        r.Add("dun doorways: plugin: restored on load in its dungeon only (another dungeon: none, until you are in it)", DoorwaysPluginLoad);
        r.Add("dun doorways: a missing or corrupt sidecar never breaks loading; the next save writes a good one", DoorwaysCorruptSidecar);
        r.Add("dun doorways: an overwrite updates the entry; an entry whose file is gone is pruned", DoorwaysOverwriteAndPrune);
        r.Add("dun doorways: NavigationEngine reaches the restored doorway points within 1 yd (a plain point at 1.5)", DoorwaysEngineReach);
    }

    // ── The sample dungeon ───────────────────────────────────────────────────

    private const uint Lb = 0x7E7E;
    private const double Ox = 0x7E * 192.0, Oy = 0x7E * 192.0;
    private const uint SurfaceId = 0x7E7E9001, DepthsId = 0x7E7E9002;

    /// <summary>A hand-built dungeon in landblock 0x7E7E; positions are landblock-local metres.</summary>
    private sealed class Sample
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public readonly List<(uint, V3[])> Portals = new(), Floors = new(), Walls = new();
        private uint _next = (Lb << 16) | 0x0100;
        public readonly uint[,] Hall = new uint[3, 3];
        public uint C1, C2, Nook, E1, Side, W1, West;

        /// <summary>A cell whose floor is the rectangle (x0,y0)-(x1,y1) (local) at height z; its origin is the middle.</summary>
        public uint Cell(double x0, double y0, double x1, double y1, double z = 0)
        {
            uint id = _next++;
            Graph[id] = new DungeonNavNode
            {
                CellId = id,
                EW = DungeonGeometry.WorldToNav(Ox + (x0 + x1) / 2),
                NS = DungeonGeometry.WorldToNav(Oy + (y0 + y1) / 2),
                Z = (float)z,
            };
            Floors.Add((id, Rect(Ox + x0, Oy + y0, Ox + x1, Oy + y1, z)));
            return id;
        }

        public void OpenX(uint a, uint b, double x, double y0, double y1) => Open(a, b, Quad(Ox + x, Oy + y0, Ox + x, Oy + y1));
        public void OpenY(uint a, uint b, double y, double x0, double x1) => Open(a, b, Quad(Ox + x0, Oy + y, Ox + x1, Oy + y));

        private void Open(uint a, uint b, V3[] quad)
        {
            Graph[a].Neighbors.Add(b);
            Graph[b].Neighbors.Add(a);
            Portals.Add((a, quad));
            Portals.Add((b, quad.Reverse().ToArray()));
        }

        public DungeonGeometry Geo() => DungeonGeometry.Build(Graph, Portals, Floors, Walls);

        //   nook (12.35..17.65, 50..54): the Surface Portal at (15, 52)
        //   c2   (12.35..17.65, 40..50)
        //   c1   (12.35..17.65, 30..40)
        //   hall (0..30, 0..30): 3x3 cells of 10 m, the start in its middle (15, 15)
        //   west room (-25..-10, 15..35) - w1 (-10..0, y 22.35..27.65) - hall - e1 (30..40, y 2.35..7.65) - side room
        //   (40..50, -5..15): the Portal to the Depths in its middle (45, 5)
        public Sample()
        {
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Hall[i, j] = Cell(i * 10, j * 10, i * 10 + 10, j * 10 + 10);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    if (i < 2) OpenX(Hall[i, j], Hall[i + 1, j], i * 10 + 10, j * 10, j * 10 + 10);
                    if (j < 2) OpenY(Hall[i, j], Hall[i, j + 1], j * 10 + 10, i * 10, i * 10 + 10);
                }
            C1 = Cell(12.35, 30, 17.65, 40);
            C2 = Cell(12.35, 40, 17.65, 50);
            Nook = Cell(12.35, 50, 17.65, 54);
            OpenY(Hall[1, 2], C1, 30, 12.35, 17.65);
            OpenY(C1, C2, 40, 12.35, 17.65);
            OpenY(C2, Nook, 50, 12.35, 17.65);
            E1 = Cell(30, 2.35, 40, 7.65);
            Side = Cell(40, -5, 50, 15);
            OpenX(Hall[2, 0], E1, 30, 2.35, 7.65);
            OpenX(E1, Side, 40, 2.35, 7.65);
            W1 = Cell(-10, 22.35, 0, 27.65);
            West = Cell(-25, 15, -10, 35);
            OpenX(W1, Hall[0, 2], 0, 22.35, 27.65);
            OpenX(West, W1, -10, 22.35, 27.65);
        }

        public uint Start => Hall[1, 1];

        public PatrolPortal Surface(string name = "Surface Portal") => new() { Id = SurfaceId, Name = name, Cell = Nook, X = Ox + 15, Y = Oy + 52, Z = 0 };
        public PatrolPortal Depths(string name = "Portal to the Depths") => new() { Id = DepthsId, Name = name, Cell = Side, X = Ox + 45, Y = Oy + 5, Z = 0 };

        /// <summary>A world with the character at (lx, ly) local in <paramref name="cell"/> (default: the hall's middle).</summary>
        public DunRouteWorld World(double lx = 15, double ly = 15, uint cell = 0, bool geo = true, params PatrolPortal[] portals) => new()
        {
            Landblock = Lb,
            PlayerCell = cell != 0 ? cell : Start,
            StartCell = cell != 0 ? cell : Start,
            X = Ox + lx, Y = Oy + ly, Z = 0,
            Graph = Graph,
            Geo = geo ? Geo() : null,
            Portals = portals.Length > 0 ? portals.ToList() : new List<PatrolPortal> { Surface(), Depths() },
        };
    }

    private static V3[] Rect(double x0, double y0, double x1, double y1, double z) => new[]
    {
        new V3((float)x0, (float)y0, (float)z), new V3((float)x1, (float)y0, (float)z),
        new V3((float)x1, (float)y1, (float)z), new V3((float)x0, (float)y1, (float)z),
    };

    private static V3[] Quad(double x0, double y0, double x1, double y1) => new[]
    {
        new V3((float)x0, (float)y0, 0f), new V3((float)x1, (float)y1, 0f),
        new V3((float)x1, (float)y1, 3f), new V3((float)x0, (float)y0, 3f),
    };

    private static double LX(NavPoint p) => DungeonGeometry.NavToWorld(p.EW) - Ox;
    private static double LY(NavPoint p) => DungeonGeometry.NavToWorld(p.NS) - Oy;
    private static string Pts(IEnumerable<NavPoint> pts) => string.Join(" ", pts.Select(p => $"{(p.Type == NavPointType.Point ? "" : p.Type + "@")}({LX(p):F1},{LY(p):F1})"));

    private static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        double cx = ax + dx * t - px, cy = ay + dy * t - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>The nearest any walked point and leg of an open route comes to (lx, ly) local.</summary>
    private static double Nearest(List<NavPoint> pts, double lx, double ly)
    {
        double best = double.MaxValue;
        var walk = pts.Where(p => NavRouteParser.IsPlainWaypoint(p.Type)).ToList();
        for (int i = 0; i < walk.Count; i++)
        {
            best = Math.Min(best, Math.Sqrt((LX(walk[i]) - lx) * (LX(walk[i]) - lx) + (LY(walk[i]) - ly) * (LY(walk[i]) - ly)));
            if (i + 1 < walk.Count) best = Math.Min(best, SegDist(lx, ly, LX(walk[i]), LY(walk[i]), LX(walk[i + 1]), LY(walk[i + 1])));
        }
        return best;
    }

    private static DunRouteBuild Build(DunRouteWorld w, DunTarget t)
    {
        var b = DungeonRouteMaker.TryBuildRoute(w, t, out string error);
        Check.True(b != null, $"{t.Words()}: built ({error})");
        return b!;
    }

    // ── Routes ───────────────────────────────────────────────────────────────

    private static void ToExit()
    {
        var d = new Sample();
        var w = d.World();
        var b = Build(w, DunTarget.Exit);
        var pts = b.Route.Points;
        Check.Eq(b.Route.RouteType, NavRouteType.Once, "a Once route");
        Check.Eq(b.Portal?.Name, "Surface Portal", "the Surface Portal is the exit");
        Check.Eq(b.Why, "named like an exit", "why: its name");
        Check.Near(LX(pts[0]), 15, 1e-6, "it starts where the character stands (x)");
        Check.Near(LY(pts[0]), 15, 1e-6, "... (y)");
        var step = pts[^1];
        Check.Eq(step.Type, NavPointType.PortalNPC, "it ends with a Portal step: " + Pts(pts));
        Check.Eq(step.TargetName, "Surface Portal", "the step names the portal");
        Check.Eq(step.ObjectClass, DungeonRouteMaker.PortalObjectClass, "object class Portal (14), as VTank writes it");
        Check.Near(DungeonGeometry.NavToWorld(step.PortalExitEW) - Ox, 15, 1e-6, "the step keeps the portal's position (x)");
        Check.Near(DungeonGeometry.NavToWorld(step.PortalExitNS) - Oy, 52, 1e-6, "... (y)");
        var last = pts[^2];
        double r = d.Surface().Radius;
        double dEnd = Math.Sqrt((LX(last) - 15) * (LX(last) - 15) + (LY(last) - 52) * (LY(last) - 52));
        Check.True(dEnd >= r - 0.05 && dEnd <= r + 0.5, $"the walk stops at the portal's edge ({dEnd:F2} m, keep-out {r} m)");
        Check.True(Nearest(pts, 15, 52) >= r - 0.05, $"nothing walked comes inside the circle before ({Nearest(pts, 15, 52):F2} m)");
        Check.True(pts.Any(p => LY(p) > 30 && LY(p) < 50 && Math.Abs(LX(p) - 15) < 1.0), "it goes up the north corridor, on its centre line");
        Check.True(Nearest(pts, 45, 5) >= d.Depths().Radius, "nowhere near the other portal");
        Check.Eq(b.StartCell, d.Start, "start cell");
        Check.Eq(b.GoalCell, d.Nook, "goal cell: the nook the portal is in");
        Check.True(b.Doorways >= 2, $"lined up on the corridor's doorways ({b.Doorways})");
        Check.True(b.Yards > 30 && b.Yards < 45, $"about the walk's length ({b.Yards:F1} yd)");
        Check.True(b.Notes.Any(n => n.Contains("2 portals known")), "it says how many portals are known: " + string.Join(" | ", b.Notes));
        Check.True(b.TargetText.Contains("the exit portal 'Surface Portal'"), b.TargetText);
    }

    private static void ExitPick()
    {
        var d = new Sample();
        // Named like an exit, whichever is nearer.
        foreach (string name in new[] { "Exit Portal", "Portal Out", "Leave the Hive", "Return to the Surface" })
        {
            var w = d.World(portals: new[] { d.Surface(name), d.Depths() });
            Check.Eq(DungeonRouteMaker.ExitCandidates(w)[0].Portal.Name, name, $"'{name}' is named like an exit");
        }
        Check.False(DungeonRouteMaker.ExitWords.IsMatch("Portal to Outpost"), "'Outpost' isn't 'out'");
        Check.False(DungeonRouteMaker.ExitWords.IsMatch("Shoutout Portal"), "nor 'Shoutout'");

        // Neither named like one: where the character came in.
        var plain = d.World(portals: new[] { d.Surface("Portal to the Hub"), d.Depths() });
        Check.Eq(DungeonRouteMaker.ExitCandidates(plain).Count, 0, "no name, no arrival: no exit");
        Check.True(DungeonRouteMaker.TryBuildRoute(plain, DunTarget.Exit, out string err) == null, "no route");
        Check.True(err.Contains("can't tell which portal is the exit") && err.Contains("'Portal to the Hub'") && err.Contains("'Portal to the Depths'"),
                   "and it lists the portals known: " + err);
        plain.Arrival = (d.Side, Ox + 43, Oy + 0, 0);
        var c = DungeonRouteMaker.ExitCandidates(plain);
        Check.Eq(c[0].Portal.Name, "Portal to the Depths", "came in by the side room: the portal there");
        Check.Eq(c[0].Why, "the portal nearest where you came in", "why");
        plain.Arrival = (d.West, Ox - 17.5, Oy + 25, 0);
        Check.Eq(DungeonRouteMaker.ExitCandidates(plain).Count, 0, "came in 42 m and 66 m from them: neither within 40 m");

        // The appraised destination says the landscape: that one, whatever the names.
        var dest = d.World(portals: new[] { d.Surface("Exit Portal"), d.Depths("Portal to Holtburg") });
        dest.PortalDestination = id => id == DepthsId ? "42.1N, 33.6E" : null;
        Check.Eq(DungeonRouteMaker.ExitCandidates(dest)[0].Portal.Name, "Portal to Holtburg", "a destination on the landscape beats a name");
        Check.True(DungeonRouteMaker.ExitCandidates(dest)[0].Why.Contains("42.1N, 33.6E"), "and says it");

        // The only portal known; never a wrecked one.
        var one = d.World(portals: new[] { d.Depths("Portal to the Hub") });
        Check.Eq(DungeonRouteMaker.ExitCandidates(one)[0].Why, "the only portal known here", "the only one");
        var wreck = d.World(portals: new[] { d.Depths("Destroyed Exit Portal") });
        Check.Eq(DungeonRouteMaker.ExitCandidates(wreck).Count, 0, "a destroyed portal is never the exit");
    }

    private static void ToPortalByName()
    {
        var d = new Sample();
        var w = d.World();
        var b = Build(w, new DunTarget(DunTargetKind.Portal, Portal: "depths"));
        Check.Eq(b.Portal?.Name, "Portal to the Depths", "a name containing it");
        Check.Eq(b.Why, "its name has 'depths' in it", "why");
        Check.Eq(b.Route.Points[^1].Type, NavPointType.PortalNPC, "a Portal step at the end");
        Check.Eq(b.GoalCell, d.Side, "the side room");
        var last = b.Route.Points[^2];
        Check.True(LX(last) < 45 - d.Depths().Radius + 0.1 && LX(last) > 38, $"stops west of it, coming in by the doorway ({LX(last):F1}, {LY(last):F1})");
        Check.True(Nearest(b.Route.Points, 15, 52) >= d.Surface().Radius, "the other portal kept out of");

        var exact = Build(w, new DunTarget(DunTargetKind.Portal, Portal: "SURFACE PORTAL"));
        Check.Eq(exact.Portal?.Name, "Surface Portal", "an exact name, any case");
        Check.Eq(exact.Why, "", "nothing to explain");

        var two = d.World(portals: new[] { d.Surface("Portal to the Depths"), d.Depths("Portal to the Depths") });
        var near = Build(two, new DunTarget(DunTargetKind.Portal, Portal: "Portal to the Depths"));
        Check.Eq(near.Portal?.Id, DepthsId, "two of that name: the nearer (the side room's, 32 m against the nook's 37 m)");
        Check.True(near.Why.StartsWith("2 portals match"), near.Why);

        Check.True(DungeonRouteMaker.TryBuildRoute(w, new DunTarget(DunTargetKind.Portal, Portal: "Lifestone"), out string err) == null, "an unknown name");
        Check.True(err.Contains("no portal named 'Lifestone'") && err.Contains("'Surface Portal'"), "says so and lists the known: " + err);
    }

    private static void ToPosition()
    {
        var d = new Sample();
        var w = d.World();
        var b = Build(w, new DunTarget(DunTargetKind.Position, X: -17.5, Y: 25, Z: 0));
        var end = b.Route.Points[^1];
        Check.Eq(end.Type, NavPointType.Point, "it ends on a waypoint");
        Check.Near(LX(end), -17.5, 1e-6, "on the spot (x)");
        Check.Near(LY(end), 25, 1e-6, "on the spot (y)");
        Check.Near(end.Z, 0, 1e-9, "its height");
        Check.Eq(b.GoalCell, d.West, "in the west room");
        // Through the west corridor on its centre line: some leg crosses x = -5 (the corridor's middle) at y 25.
        var pts = b.Route.Points;
        bool through = false;
        for (int i = 0; i + 1 < pts.Count && !through; i++)
        {
            double ax = LX(pts[i]), bx = LX(pts[i + 1]);
            if ((ax - -5) * (bx - -5) > 0 || Math.Abs(bx - ax) < 1e-9) continue;
            double y = LY(pts[i]) + (LY(pts[i + 1]) - LY(pts[i])) * (-5 - ax) / (bx - ax);
            through = Math.Abs(y - 25) < 0.5;
        }
        Check.True(through, "through the west corridor, on its centre line: " + Pts(pts));
        Check.True(b.TargetText.Contains("(-17.5, 25, 0)"), b.TargetText);

        Check.True(DungeonRouteMaker.TryBuildRoute(w, new DunTarget(DunTargetKind.Position, X: 500, Y: 500, Z: 0), out string err) == null, "a spot far outside");
        Check.True(err.Contains("isn't inside this dungeon"), err);

        // Its own cell: the straight walk.
        var here = Build(w, new DunTarget(DunTargetKind.Position, X: 18, Y: 12, Z: 0));
        Check.Eq(here.Route.Points.Count, 2, "the character's spot and the target: " + Pts(here.Route.Points));
    }

    private static void ToRoomAndFar()
    {
        var d = new Sample();
        var w = d.World();
        var rooms = DungeonPathfinder.BuildRooms(d.Graph, w.Geo!);
        Check.Eq(rooms.Cells[rooms.RoomOf[d.Start]].Count, 9, "the hall is one room of 9 cells");
        int side = rooms.RoomOf[d.Side];
        var b = Build(w, new DunTarget(DunTargetKind.Room, Room: side));
        Check.Eq(b.Room, side, "the room asked for");
        Check.Eq(b.GoalCell, d.Side, "its cell");
        // The side room's middle is the Depths portal: the route ends just outside its reach.
        var end = b.Route.Points[^1];
        double dd = Math.Sqrt((LX(end) - 45) * (LX(end) - 45) + (LY(end) - 5) * (LY(end) - 5));
        Check.True(dd >= d.Depths().Radius - 0.05, $"the middle is in the portal's reach: the end moved out ({dd:F2} m)");
        Check.True(b.LeftOut.Any(l => l.Contains("kept out of portal 'Portal to the Depths'")), "and it says so: " + string.Join(" | ", b.LeftOut));
        Check.True(DungeonRouteMaker.TryBuildRoute(w, new DunTarget(DunTargetKind.Room, Room: rooms.Cells.Count), out string err) == null, "a room past the last");
        Check.True(err.Contains($"rooms are 0 to {rooms.Cells.Count - 1}"), err);
        var noMap = d.World(geo: false);
        Check.True(DungeonRouteMaker.TryBuildRoute(noMap, new DunTarget(DunTargetKind.Room, Room: 0), out err) == null && err.Contains("isn't loaded yet"), "rooms need the map: " + err);

        uint far = DungeonRouteMaker.FarthestCell(d.Graph, d.Start, null, out double yd);
        Check.Eq(far, d.West, $"the west room is the farthest on foot ({yd:F1} yd)");
        Check.Near(yd, 42.5, 0.01, "10 + 10 + 10 + 12.5");
        var f = Build(w, DunTarget.Far);
        Check.Eq(f.GoalCell, d.West, "far: the west room");
        Check.True(f.TargetText.Contains("farthest spot on foot"), f.TargetText);
        Check.Eq(DungeonRouteMaker.FarthestCell(d.Graph, d.Start, new HashSet<uint> { d.W1 }, out _), d.Side, "with the west corridor lava: the side room");
    }

    private static void Refusals()
    {
        var d = new Sample();
        var w = d.World();
        w.Hazards = new HashSet<uint> { d.C1 };
        Check.True(DungeonRouteMaker.TryBuildRoute(w, DunTarget.Exit, out string err) == null, "the only corridor to the exit is lava");
        Check.True(err.Contains("through lava/acid cells"), err);
        w.Hazards = new HashSet<uint> { d.Nook };
        var into = Build(w, DunTarget.Exit);
        Check.Eq(into.GoalCell, d.Nook, "the goal itself may be one (the player asked to go there)");
        Check.True(into.LeftOut.Any(l => l.Contains("1 lava/acid cell kept out of")), string.Join(" | ", into.LeftOut));

        // A pit 10 m below the second corridor: reached only by a drop.
        uint pit = d.Cell(12.35, 42, 17.65, 48, -10);
        d.Graph[d.C2].Neighbors.Add(pit);
        d.Graph[pit].Neighbors.Add(d.C2);
        var dw = d.World();
        Check.True(DungeonRouteMaker.TryBuildRoute(dw, new DunTarget(DunTargetKind.Position, X: 15, Y: 45, Z: -10), out err) == null, "the pit");
        Check.True(err.Contains("only reached by a drop"), err);

        var none = d.World(portals: Array.Empty<PatrolPortal>());
        none.Portals.Clear();
        Check.True(DungeonRouteMaker.TryBuildRoute(none, DunTarget.Exit, out err) == null && err.Contains("no portal is known in this dungeon yet"), err);
        Check.True(DungeonRouteMaker.TryBuildRoute(none, new DunTarget(DunTargetKind.Portal, Portal: "x"), out err) == null && err.Contains("knows no portal"), err);

        // Without the map geometry: the cell walk, said so.
        var noMap = Build(d.World(geo: false), DunTarget.Exit);
        Check.True(noMap.Notes.Any(n => n.Contains("dungeon map isn't loaded yet")), "said: " + string.Join(" | ", noMap.Notes));
        Check.Eq(noMap.Route.Points[^1].Type, NavPointType.PortalNPC, "still a Portal step at the end");
        Check.True(Nearest(noMap.Route.Points, 15, 52) >= d.Surface().Radius - 0.05, "and still stops at the circle");

        var explored = d.World();
        explored.Explored = 0.35;
        var e = Build(explored, DunTarget.Far);
        Check.True(e.Notes.Any(n => n.Contains("35% of this dungeon's floor explored")), "the explored share, when known: " + string.Join(" | ", e.Notes));
    }

    private static void BentRoundAnotherPortal()
    {
        // The character east of the Depths portal in the side room; the exit is west through its doorway, so
        // the straight walk to the doorway goes through the portal.
        var d = new Sample();
        var w = d.World(49, 5, d.Side);
        var b = Build(w, DunTarget.Exit);
        double r = d.Depths().Radius;
        Check.True(Nearest(b.Route.Points, 45, 5) >= r - 0.05, $"bent round the Depths portal ({Nearest(b.Route.Points, 45, 5):F2} m): {Pts(b.Route.Points)}");
        Check.True(b.LeftOut.Any(l => l.Contains("kept out of portal 'Portal to the Depths'")), "said: " + string.Join(" | ", b.LeftOut));
        Check.Eq(b.Route.Points[^1].TargetName, "Surface Portal", "and on to the exit");
    }

    // ── Files ────────────────────────────────────────────────────────────────

    private static string Fresh(string sub)
    {
        string dir = Path.Combine(Program.TempRoot, sub);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        DungeonRouteMaker.Folder = dir;
        return dir;
    }

    private static void SavedRouteRuns()
    {
        string dir = Fresh("NavProfiles-run");
        var d = new Sample();
        var w = d.World();
        var exit = Build(w, DunTarget.Exit);
        Check.True(DungeonRouteMaker.TrySave("Hive exit", exit.Route, false, Lb, out string path, out bool overwrote, out string err), "saved: " + err);
        Check.Eq(path, Path.Combine(dir, "Hive exit.nav"), "NavProfiles\\<name>.nav");
        Check.False(overwrote, "a new file");
        var lines = File.ReadAllLines(path);
        Check.Eq(lines[0], "uTank2 NAV 1.2", "VTank's header");
        Check.Eq(lines[1], "4", "route type Once (4)");
        Check.Eq(lines[2], exit.Route.Points.Count.ToString(), "the point count");

        var back = NavRouteParser.Load(path);
        Check.True(back.LoadWarning == null, "no load warning");
        Check.Eq(back.RouteType, NavRouteType.Once, "read back: Once");
        Check.Eq(back.Points.Count, exit.Route.Points.Count, "the same points");
        for (int i = 0; i < back.Points.Count; i++)
        {
            var a = exit.Route.Points[i]; var b = back.Points[i];
            Check.True(a.Type == b.Type && a.NS == b.NS && a.EW == b.EW && a.Z == b.Z, $"point {i} exactly as laid ({a} / {b})");
        }
        Check.Eq(back.Points[^1].TargetName, "Surface Portal", "the Portal step's name");
        Check.True(back.Points[^1].IsTie, "tied to the portal's spot");

        // Every point is in this dungeon, as NavigationEngine's area hold judges it.
        foreach (var p in back.Points)
        {
            var a = NavRecoveryPlanner.ClassifyArea(d.Start, p.NS, p.EW, lb => lb == Lb,
                (lb, ns, ew) => d.Graph.Values.Any(n => Math.Sqrt((n.NS - ns) * (n.NS - ns) + (n.EW - ew) * (n.EW - ew)) * 240 <= 60), out _, out _);
            Check.Eq(a, NavAreaCheck.Match, $"({LX(p):F1}, {LY(p):F1}) is in this dungeon for the engine");
        }

        // The spot route, run by NavigationEngine with a character walked onto each point: it gets to the end.
        var spot = Build(w, new DunTarget(DunTargetKind.Position, X: -17.5, Y: 25, Z: 0));
        Check.True(DungeonRouteMaker.TrySave("To the west room", spot.Route, false, Lb, out string spotPath, out _, out err), "saved: " + err);
        var route = NavRouteParser.Load(spotPath);
        FakeHost.Reset();
        long now = 1_000_000;
        NavigationEngine.Clock = () => now;
        try
        {
            FakeHost.PlayerPose = (d.Start, 15f, 15f, 0f);
            var host = FakeHost.Create();
            var s = new LegacyUiSettings { IsMacroRunning = true, EnableNavigation = true, CurrentRoute = route, ActiveNavIndex = 0 };
            Check.True(NavRouteParser.StartsAtFirstPoint(route.RouteType, s.NavResumeNearest), "a Once route starts at [0], the character's spot");
            var e = new NavigationEngine(host, s);
            for (int i = 0; i < route.Points.Count && s.EnableNavigation; i++)
            {
                var p = route.Points[i];
                FakeHost.PlayerPose = (d.Start, (float)LX(p), (float)LY(p), (float)(p.Z * 240.0));
                for (int k = 0; k < 3 && s.EnableNavigation && s.ActiveNavIndex <= i; k++) { now += 100; e.Tick(); }
                Check.True(s.ActiveNavIndex > i || !s.EnableNavigation, $"at point {i} ({LX(p):F1}, {LY(p):F1}) it moves on (index {s.ActiveNavIndex})");
            }
            Check.False(s.EnableNavigation, "the end of the Once route turns navigation off: it got there");
            Check.Eq(s.ActiveNavIndex, route.Points.Count, "the index is past the end");
        }
        finally { NavigationEngine.Clock = () => Environment.TickCount64; }
    }

    private static void Info()
    {
        string dir = Fresh("NavProfiles-info");
        var d = new Sample();
        var w = d.World();
        var exit = Build(w, DunTarget.Exit);
        DungeonRouteMaker.TrySave("Hive exit", exit.Route, false, Lb, out string path, out _, out _);
        var route = NavRouteParser.Load(path);
        Dictionary<uint, DungeonNavNode>? GraphOf(uint lb) => lb == Lb ? d.Graph : null;
        bool? IsDungeon(uint lb) => lb == Lb;

        var info = DungeonRouteMaker.DescribeRoute("Hive exit", path, route, d.Start, GraphOf, IsDungeon);
        Check.Eq(info.Type, NavRouteType.Once, "type");
        Check.Eq(info.Points, route.Points.Count, "points");
        Check.Eq(info.Waypoints, route.Points.Count - 1, "all but the Portal step are walked");
        Check.Eq(info.Steps["portal"], 1, "one Portal step");
        Check.Near(info.Yards, exit.Yards, 1e-6, "its length");
        Check.Eq(info.Landblock, Lb, "landblock 7E7E");
        Check.Eq(info.Dungeon, true, "a dungeon");
        Check.Eq(info.ForThisDungeon, "yes", "made for this dungeon");
        Check.Eq(info.PointsHere, info.Waypoints, "every point here");
        Check.True(info.Cells >= 2, $"the cells under its points ({info.Cells})");
        Check.Eq(info.StartCell, d.Start, "starts in the hall's middle");
        Check.Eq(info.EndCell, d.C2, "ends in the corridor before the nook: the walk stops at the portal's reach");
        var json = JsonDocument.Parse(DungeonRouteMaker.InfoJson(info)).RootElement;
        Check.Eq(json.GetProperty("forThisDungeon").GetString(), "yes", "JSON: forThisDungeon");
        Check.Eq(json.GetProperty("landblock").GetString(), "7E7E", "JSON: landblock hex");
        Check.Eq(json.GetProperty("steps").GetProperty("portal").GetInt32(), 1, "JSON: steps.portal");
        Check.Eq(json.GetProperty("startCell").GetString(), d.Start.ToString("X8"), "JSON: startCell");

        // In another dungeon (landblock 7E7F, its own cells): not this one's.
        var elsewhere = DungeonRouteMaker.DescribeRoute("Hive exit", path, route, 0x7E7F0105, lb => lb == 0x7E7F ? new Dictionary<uint, DungeonNavNode>
        {
            [0x7E7F0105] = new() { CellId = 0x7E7F0105, EW = DungeonGeometry.WorldToNav(Ox + 50), NS = DungeonGeometry.WorldToNav(Oy + 192 + 100) },
        } : GraphOf(lb), lb => lb is Lb or 0x7E7F);
        Check.Eq(elsewhere.ForThisDungeon, "no", "from another dungeon: no");
        Check.Eq(elsewhere.Landblock, Lb, "and it says where it was made");
        Check.True(elsewhere.Notes.Any(n => n.Contains("made in landblock 7E7E; you are in 7E7F")), string.Join(" | ", elsewhere.Notes));

        // On the landscape: can't tell.
        var outside = DungeonRouteMaker.DescribeRoute("Hive exit", path, route, 0xA9B40021, GraphOf, IsDungeon);
        Check.Eq(outside.ForThisDungeon, "unknown", "not in a dungeon: unknown");
        Check.Eq(outside.Cells, info.Cells, "its own landblock still described");

        // Points outside their dungeon's own 192 m square (the west room is in 7D7E's square, as Tiny Hive's
        // cells are in 0288's): the dungeon is the one whose cells they are by, not the square they fall in.
        var west = Build(w, new DunTarget(DunTargetKind.Position, X: -17.5, Y: 25, Z: 0));
        DungeonRouteMaker.TrySave("West", west.Route, false, Lb, out string westPath, out _, out _);
        var westInfo = DungeonRouteMaker.DescribeRoute("West", westPath, NavRouteParser.Load(westPath), 0xA9B40021, GraphOf, IsDungeon);
        Check.True(westInfo.Landblocks.Contains(0x7D7E), "some of its points fall in 7D7E's square: " + string.Join(",", westInfo.Landblocks.Select(l => l.ToString("X4"))));
        Check.Eq(westInfo.Landblock, Lb, "but it is 7E7E's route");
        Check.Eq(westInfo.Dungeon, true, "a dungeon");
        Check.True(westInfo.Cells >= 2 && westInfo.EndCell == d.West, $"its cells found ({westInfo.Cells}, ending in {westInfo.EndCell:X8})");

        // A patrol-shaped (Circular) file: the closing leg counts.
        var loop = new NavRouteParser { RouteType = NavRouteType.Circular };
        loop.Points.Add(DungeonRouteMaker.WorldPt(Ox + 5, Oy + 5, 0));
        loop.Points.Add(DungeonRouteMaker.WorldPt(Ox + 25, Oy + 5, 0));
        loop.Points.Add(DungeonRouteMaker.WorldPt(Ox + 25, Oy + 25, 0));
        Check.Near(DungeonRouteMaker.RouteYards(loop), 20 + 20 + Math.Sqrt(800), 1e-6, "a lap: 20 + 20 + the way back");
    }

    private static void NameRules()
    {
        string dir = Fresh("NavProfiles-names");
        Check.True(DunRouteRequest.TryName("Hive exit", out string n, out _) && n == "Hive exit", "a plain name");
        Check.True(DunRouteRequest.TryName("  \"Hive exit.nav\" ", out n, out _) && n == "Hive exit", "quotes, spaces and .nav dropped: " + n);
        foreach (string bad in new[] { "", "   ", "a/b", "a\\b", "..", "x..y", "a:b", "a*b", "a?b", "a<b", "a|b", "CON", "nul.txt", "LPT1", "trailing.", new string('x', 121) })
        {
            Check.False(DunRouteRequest.TryName(bad, out _, out string why), $"'{(bad.Length > 20 ? bad[..20] + "..." : bad)}' refused ({why})");
            Check.True(why.Contains("can't be a route name"), "with the reason: " + why);
        }
        Check.True(DunRouteRequest.TryName(new string('x', 120), out _, out _), "120 characters is fine");

        var route = new NavRouteParser { RouteType = NavRouteType.Once };
        route.Points.Add(DungeonRouteMaker.WorldPt(Ox + 5, Oy + 5, 0));
        Check.True(DungeonRouteMaker.TrySave("Mine", route, false, Lb, out string path, out bool ow, out string err), "first save: " + err);
        Check.False(ow, "nothing replaced");
        string before = File.ReadAllText(path);
        route.Points.Add(DungeonRouteMaker.WorldPt(Ox + 6, Oy + 6, 0));
        Check.False(DungeonRouteMaker.TrySave("Mine", route, false, Lb, out _, out ow, out err), "again without overwrite: refused");
        Check.True(err.Contains("already in NavProfiles") && err.Contains("overwrite"), err);
        Check.Eq(File.ReadAllText(path), before, "the file is untouched");
        Check.True(DungeonRouteMaker.TrySave("mine.nav", route, true, Lb, out string p2, out ow, out err), "with overwrite: " + err);
        Check.True(ow, "it says it replaced one");
        Check.Eq(NavRouteParser.Load(p2).Points.Count, 2, "the new route is in it");
        Check.False(DungeonRouteMaker.TrySave("../escape", route, true, Lb, out _, out _, out err), "no folders, overwrite or not");
        Check.False(File.Exists(Path.Combine(dir, "..", "escape.nav")), "nothing written outside NavProfiles");
    }

    private static void CommandWords()
    {
        static DunRouteRequest? P(string words, out string error)
            => RynthAiPlugin.TryParseDunRouteWords(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), out var r, out error) ? r : null;
        var r = P("Hive exit to exit", out string e);
        Check.True(r != null && r.Name == "Hive exit" && r.To == DunTarget.Exit && !r.Overwrite, "name, to exit: " + e);
        r = P("Way to the Hub to exit overwrite", out e);
        Check.True(r != null && r.Name == "Way to the Hub" && r.To!.Kind == DunTargetKind.Exit && r.Overwrite, "a name holding 'to', and overwrite: " + e);
        r = P("X to portal Portal to the Depths", out e);
        Check.True(r != null && r.Name == "X" && r.To!.Kind == DunTargetKind.Portal && r.To.Portal == "Portal to the Depths", "a portal name holding 'to': " + e);
        r = P("Spot to 110.5 -3.25 -24", out e);
        Check.True(r != null && r.To!.Kind == DunTargetKind.Position && r.To.X == 110.5 && r.To.Y == -3.25 && r.To.Z == -24, "a spot: " + e);
        r = P("Spot to [110.5, -3.25, -24]", out e);
        Check.True(r != null && r.To!.Kind == DunTargetKind.Position && r.To.Z == -24, "a spot in brackets with commas: " + e);
        r = P("R to room 3", out e);
        Check.True(r != null && r.To!.Kind == DunTargetKind.Room && r.To.Room == 3, "a room: " + e);
        r = P("F to FAR", out e);
        Check.True(r != null && r.To == DunTarget.Far, "far, any case: " + e);
        Check.Null(P("Spot to 1 2", out e), "two numbers");
        Check.True(e.Contains("three numbers"), e);
        Check.Null(P("R to room -1", out e), "a negative room");
        Check.Null(P("X to portal", out e), "portal with no name");
        Check.True(e.Contains("needs the portal's name"), e);
        Check.Null(P("X to the moon", out e), "an unknown target");
        Check.True(e.Contains("isn't a target"), e);
        Check.Null(P("No target here", out e), "no 'to'");
        Check.True(e.Contains("no \"to\""), e);
        Check.Null(P("a/b to exit", out e), "a bad name");
        Check.True(e.Contains("can't be a route name"), e);
    }

    // ── The interface ────────────────────────────────────────────────────────

    private static void JsonRequests()
    {
        static DunRouteRequest? Ok(string json)
        {
            bool ok = DunRouteRequest.TryParseJson(json, out var r, out string e);
            Check.True(ok, $"{json} accepted ({e})");
            return ok ? r : null;
        }
        var a = Ok("{\"op\":\"dunroute\",\"name\":\"Hive exit\",\"to\":\"exit\"}");
        Check.True(a!.Op == DunRouteOp.DunRoute && a.Name == "Hive exit" && a.To == DunTarget.Exit && !a.Overwrite, "dunroute to exit");
        a = Ok("{\"op\":\"dunroute\",\"name\":\"D\",\"to\":{\"portal\":\"Portal to the Depths\"},\"overwrite\":true}");
        Check.True(a!.To!.Kind == DunTargetKind.Portal && a.To.Portal == "Portal to the Depths" && a.Overwrite, "to a portal, overwrite");
        a = Ok("{\"op\":\"dunroute\",\"name\":\"S\",\"to\":{\"x\":110.5,\"y\":-3.25,\"z\":-24}}");
        Check.True(a!.To!.Kind == DunTargetKind.Position && a.To.X == 110.5 && a.To.Y == -3.25 && a.To.Z == -24, "to a spot");
        a = Ok("{\"op\":\"dunroute\",\"name\":\"F\",\"to\":\"far\"}");
        Check.True(a!.To == DunTarget.Far, "far");
        a = Ok("{\"op\":\"dunroute\",\"name\":\"R\",\"to\":{\"room\":12}}");
        Check.True(a!.To!.Kind == DunTargetKind.Room && a.To.Room == 12, "a room");
        a = Ok("{\"op\":\"dunpatrol\",\"name\":\"Hive patrol.nav\"}");
        Check.True(a!.Op == DunRouteOp.DunPatrol && a.Name == "Hive patrol", "dunpatrol (.nav dropped)");
        a = Ok("{\"op\":\"info\",\"name\":\"Hive patrol\"}");
        Check.True(a!.Op == DunRouteOp.Info, "info");
        a = Ok(" {\"op\":\"where\"} ");
        Check.True(a!.Op == DunRouteOp.Where, "where");

        (string Json, string Says)[] bad =
        {
            ("", "no request"),
            ("not json", "not JSON"),
            ("[1,2]", "must be a JSON object"),
            ("\"where\"", "must be a JSON object"),
            ("{}", "\"op\" is missing"),
            ("{\"op\":3}", "\"op\" must be a string"),
            ("{\"op\":\"teleport\"}", "must be dunroute, dunpatrol, info or where"),
            ("{\"op\":\"where\",\"name\":\"x\"}", "isn't a field of where"),
            ("{\"op\":\"where\",\"op\":\"info\"}", "given twice"),
            ("{\"op\":\"dunroute\",\"to\":\"exit\"}", "\"name\" is missing"),
            ("{\"op\":\"dunroute\",\"name\":5,\"to\":\"exit\"}", "\"name\" must be a string"),
            ("{\"op\":\"dunroute\",\"name\":\"a/b\",\"to\":\"exit\"}", "can't be a route name"),
            ("{\"op\":\"dunroute\",\"name\":\"CON\",\"to\":\"exit\"}", "Windows reserves"),
            ("{\"op\":\"dunroute\",\"name\":\"x\"}", "\"to\" is missing"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":\"moon\"}", "\"to\" must be"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":5}", "\"to\" must be"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{}}", "\"to\" is one of"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"portal\":\"\"}}", "the portal's name"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"portal\":\"a\",\"room\":1}}", "\"to\" is one of"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"x\":1,\"y\":2}}", "\"z\" is missing"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"x\":1,\"y\":\"2\",\"z\":3}}", "\"y\" must be a number"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"x\":1,\"y\":2,\"z\":1e9}}", "landblock-local metres"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"room\":-1}}", "a room number, 0 or more"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"room\":1.5}}", "a room number"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":{\"house\":1}}", "doesn't take \"house\""),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":\"exit\",\"overwrite\":\"yes\"}", "\"overwrite\" must be true or false"),
            ("{\"op\":\"dunroute\",\"name\":\"x\",\"to\":\"exit\",\"overwite\":true}", "isn't a field of dunroute"),
            ("{\"op\":\"info\",\"name\":\"x\",\"overwrite\":true}", "isn't a field of info"),
            ("{\"op\":\"dunpatrol\",\"name\":\"x\",\"to\":\"exit\"}", "isn't a field of dunpatrol"),
            ("{\"op\":\"where\"", "not JSON"),
            ("{\"op\":\"where\",\"x\":" + new string('[', 20) + new string(']', 20) + "}", "not JSON"),
            ("{\"op\":\"where\",\"pad\":\"" + new string('x', 9000) + "\"}", "longer than 8192 bytes"),
        };
        foreach (var (json, says) in bad)
        {
            Check.False(DunRouteRequest.TryParseJson(json, out _, out string e), $"refused: {(json.Length > 60 ? json[..60] + "..." : json)}");
            Check.True(e.Contains(says), $"  says \"{says}\": {e}");
        }
    }

    private static void JobStates()
    {
        var jobs = new DunRouteJobs();
        long now = 1000;
        jobs.Clock = () => now;
        var where = new DunRouteRequest { Op = DunRouteOp.Where };
        uint id = jobs.Add(where, out string err);
        Check.Eq(id, 1u, "the first id is 1: " + err);
        Check.True(jobs.TryPoll(id, out string json), "known");
        var j = JsonDocument.Parse(json).RootElement;
        Check.Eq(j.GetProperty("state").GetString(), "queued", "queued");
        Check.Eq(j.GetProperty("op").GetString(), "where", "its op");
        Check.Eq(j.GetProperty("result").ValueKind, JsonValueKind.Null, "no result yet");

        var job = jobs.Next()!;
        Check.Eq(job.Id, id, "the next job to work");
        jobs.Working(job, "waiting: you're in portal space");
        jobs.TryPoll(id, out json);
        j = JsonDocument.Parse(json).RootElement;
        Check.Eq(j.GetProperty("state").GetString(), "working", "working");
        Check.Eq(j.GetProperty("detail").GetString(), "waiting: you're in portal space", "detail says what it waits for");
        Check.False(jobs.WaitedTooLong(job), "not yet too long");
        now += DunRouteJobs.WaitLimitMs + 1;
        Check.True(jobs.WaitedTooLong(job), "30 s on: too long");
        jobs.Finish(job, true, "in dungeon 7E7E", "{\"inDungeon\":true}");
        jobs.TryPoll(id, out json);
        j = JsonDocument.Parse(json).RootElement;
        Check.Eq(j.GetProperty("state").GetString(), "done", "done");
        Check.True(j.GetProperty("result").GetProperty("inDungeon").GetBoolean(), "the result is the op's JSON object");
        Check.True(jobs.Next() == null, "nothing left to work");

        uint f = jobs.Add(where, out _);
        jobs.Finish(jobs.Next()!, false, "Cannot get player position.", "{\"ignored\":1}");
        jobs.TryPoll(f, out json);
        j = JsonDocument.Parse(json).RootElement;
        Check.Eq(j.GetProperty("state").GetString(), "failed", "failed");
        Check.Eq(j.GetProperty("detail").GetString(), "Cannot get player position.", "with the reason");
        Check.Eq(j.GetProperty("result").ValueKind, JsonValueKind.Null, "and no result");

        Check.False(jobs.TryPoll(999, out json), "an id never given");
        j = JsonDocument.Parse(json).RootElement;
        Check.Eq(j.GetProperty("state").GetString(), "unknown", "state unknown");
        Check.True(j.GetProperty("detail").GetString()!.Contains("only the last 32 are kept"), "says why");
        Check.False(jobs.TryPoll(0, out _), "0 is never a job");

        // 32 kept: the 33rd drops the oldest finished one.
        for (int i = 0; i < 30; i++) jobs.Add(where, out _);
        Check.Eq(jobs.Count, 32, "32 kept");
        uint next = jobs.Add(where, out err);
        Check.Eq(next, 33u, "the 33rd: " + err);
        Check.Eq(jobs.Count, 32, "still 32");
        Check.False(jobs.TryPoll(1, out _), "the oldest finished one (1) was dropped");
        Check.True(jobs.TryPoll(f, out _), "the next finished one (2) is still there");
        uint again = jobs.Add(where, out _);
        Check.False(jobs.TryPoll(f, out _), "then it goes too");
        Check.Eq(again, 34u, "ids keep counting up, never reused");
        // All 32 unfinished: refused.
        Check.Eq(jobs.Add(where, out err), 0u, "every kept job unfinished: refused");
        Check.True(err.Contains("32 jobs are still unfinished"), err);
        // Work is in order: the oldest unfinished first.
        Check.Eq(jobs.Next()!.Id, 3u, "the oldest unfinished is worked first");

        Check.Eq(JsonDocument.Parse(DunRouteJobs.QueuedJson(7)).RootElement.GetProperty("state").GetString(), "queued", "Request's answer: queued");
        Check.Eq(JsonDocument.Parse(DunRouteJobs.ErrorJson("bad \"x\"")).RootElement.GetProperty("error").GetString(), "bad \"x\"", "Request's error, escaped");
    }

    private static unsafe void RoutesTable()
    {
        try
        {
            Check.True(RoutesInterface.Query(RynthAiYieldApiV1.InterfaceName, 1) == IntPtr.Zero, "another interface's name");
            Check.True(RoutesInterface.Query(RynthAiRoutesApiV1.InterfaceName, 0) == IntPtr.Zero, "version 0");
            Check.True(RoutesInterface.Query(RynthAiRoutesApiV1.InterfaceName, 2) == IntPtr.Zero, "a newer version than RynthAi has");
            IntPtr p = RoutesInterface.Query(RynthAiRoutesApiV1.InterfaceName, 1);
            Check.True(p != IntPtr.Zero, "v1 answers");
            var t = (RynthAiRoutesApiV1*)p;
            Check.Eq(t->Version, 1u, "table version");
            Check.Eq(t->StructSize, (uint)sizeof(RynthAiRoutesApiV1), "table size");
            Check.True(t->Request != null && t->Poll != null, "functions filled in");
            Check.True(RoutesInterface.Query(RynthAiRoutesApiV1.InterfaceName, 1) == p, "the same table on every call");
            byte[] req = System.Text.Encoding.UTF8.GetBytes("{\"op\":\"where\"}\0");
            byte* answer = null;
            uint id;
            fixed (byte* r = req) id = t->Request(r, &answer);
            Check.Eq(id, 0u, "no plugin instance: refused");
            string text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)answer) ?? "";
            Check.True(text.Contains("\"error\""), "with an error (no plugin instance, or none logged in): " + text);
            byte* json = null;
            Check.Eq(t->Poll(5, &json), 0, "Poll: no such job");
            Check.True((System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)json) ?? "").Contains("\"unknown\""), "state unknown");
        }
        finally
        {
            RoutesInterface.Release();
        }
    }

    // ── The plugin ───────────────────────────────────────────────────────────

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const uint Player = 0x50000E01;

    private sealed class Rig
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();
        public readonly Sample D = new();
        public readonly DungeonGeometry Geo;
        public readonly WorldObjectCache Cache;

        public Rig(string folder, string world = "", double lx = 15, double ly = 15, bool namedExit = true)
        {
            FakeHost.Reset();
            ServerPolicy.Forget();
            Fresh(folder);
            Geo = D.Geo();
            FakeHost.WorldName = world;
            FakeHost.Names[Player] = "Tester";
            At(lx, ly, D.Start);
            FakeHost.Names[SurfaceId] = namedExit ? "Surface Portal" : "Portal to the Hub";
            FakeHost.ItemTypes[SurfaceId] = 0x00010000;
            FakeHost.Positions[SurfaceId] = (D.Nook, 15f, 52f, 0f);
            FakeHost.Names[DepthsId] = "Portal to the Depths";
            FakeHost.ItemTypes[DepthsId] = 0x00010000;
            FakeHost.Positions[DepthsId] = (D.Side, 45f, 5f, 0f);
            var host = FakeHost.Create(Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            foreach (uint id in new[] { SurfaceId, DepthsId }) Cache.OnCreateObject(id);
            Cache.Tick();
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            P.DungeonMapOverride = lb => lb == Lb ? (D.Graph, Geo) : null;
            S.DunPatrolCoverage = (int)PatrolCoverage.Everything;
        }

        public void At(double lx, double ly, uint cell) => FakeHost.PlayerPose = (cell, (float)lx, (float)ly, 0f);
        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Get(string f) => typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public void Tick() => Call("TickDunRoutes");

        /// <summary>A line typed in the chat bar; the chat lines it wrote.</summary>
        public List<string> Typed(string line)
        {
            int before = FakeHost.Chat.Count;
            int eat = 0;
            P.OnChatBarEnter(line, ref eat);
            Check.Eq(eat, 1, $"'{line}' is RynthAi's");
            return FakeHost.Chat.Skip(before).ToList();
        }

        public (uint Id, string Error) Request(string json)
        {
            uint id = P.RouteRequest(json, out string error);
            return (id, error);
        }

        public JsonElement Poll(uint id)
        {
            P.RouteJobs.TryPoll(id, out string json);
            return JsonDocument.Parse(json).RootElement.Clone();
        }
    }

    private static string Show(List<string> lines) => lines.Count == 0 ? "(nothing)" : string.Join(" | ", lines);

    private static void PluginDunRoute()
    {
        var rig = new Rig("NavProfiles-cmd");
        var said = rig.Typed("/ra dunroute Hive exit to exit");
        Check.True(said.Count >= 1 && said[0].StartsWith("[RynthAi] DunRoute: saved Hive exit.nav:"), "it answers: " + Show(said));
        Check.True(said[0].Contains("the exit portal 'Surface Portal' (named like an exit)") && said[0].Contains("/ra nav load Hive exit"), "where to and how to run it");
        Check.True(said.Any(l => l.Contains("note:") && l.Contains("2 portals known")), "and what isn't known: " + Show(said));
        string file = Path.Combine(DungeonRouteMaker.Folder, "Hive exit.nav");
        Check.True(File.Exists(file), "the file is in NavProfiles");
        var route = NavRouteParser.Load(file);
        Check.Eq(route.RouteType, NavRouteType.Once, "a Once route");
        Check.Eq(route.Points[^1].Type, NavPointType.PortalNPC, "ending at the portal");
        Check.True(rig.S.CurrentRoute.Points.Count == 0 && !rig.S.EnableNavigation, "nothing loaded or started");

        said = rig.Typed("/ra dunroute Hive exit to far");
        Check.True(said.Count == 1 && said[0].Contains("already in NavProfiles: add overwrite"), "again without overwrite: refused: " + Show(said));
        Check.Eq(NavRouteParser.Load(file).Points[^1].Type, NavPointType.PortalNPC, "the file is untouched");
        said = rig.Typed("/ra dunroute Hive exit to far overwrite");
        Check.True(said[0].Contains("replaced Hive exit.nav") && said[0].Contains("farthest spot on foot"), "with overwrite: replaced: " + Show(said));
        Check.Eq(NavRouteParser.Load(file).Points[^1].Type, NavPointType.Point, "the new route is in it");

        said = rig.Typed("/ra dunroute Way to the west to -17.5 25 0");
        Check.True(said[0].Contains("saved Way to the west.nav") && said[0].Contains("(-17.5, 25, 0)"), "a name holding 'to': " + Show(said));
        said = rig.Typed("/ra dunroute D to portal depths");
        Check.True(said[0].Contains("portal 'Portal to the Depths'"), "to a portal by name: " + Show(said));

        said = rig.Typed("/ra dunroute info Hive exit");
        Check.True(said[0].Contains("Hive exit.nav: once") && said[0].Contains("landblock 7E7E (a dungeon)") && said[0].Contains("made for this dungeon: yes"),
                   "info: " + Show(said));
        said = rig.Typed("/ra dunroute info Nothing Here");
        Check.True(said.Count == 1 && said[0].Contains("no route named 'Nothing Here'"), "info on a missing file: " + Show(said));
        said = rig.Typed("/ra dunroute");
        Check.True(said.Count >= 1 && said[0].Contains("Usage: /ra dunroute"), "bare: the usage");
        said = rig.Typed("/ra dunroute X to the moon");
        Check.True(said.Count == 1 && said[0].Contains("isn't a target") && said[0].Contains("Usage"), "a wrong target: why and the usage: " + Show(said));
        Check.Eq(FakeHost.ChatTypes[^1], CommandReply.FailColor, "in the failure colour");

        rig.At(42.1, 33.6, 0xA9B4002Fu);
        said = rig.Typed("/ra dunroute Out to exit");
        Check.True(said.Count == 1 && said[0].Contains("dunroute requires you to be inside a dungeon"), "outdoors: " + Show(said));
    }

    private static void PluginPatrolSave()
    {
        var rig = new Rig("NavProfiles-patrol");
        var said = rig.Typed("/ra dunpatrol-save Hive patrol");
        Check.True(said.Count >= 1 && said[0].StartsWith("[RynthAi] DunPatrol-Save: saved Hive patrol.nav: the circular patrol dunnav-patrol walks from here"),
                   "it answers: " + Show(said));
        Check.True(said[0].Contains("coverage everything"), "with the settings it was laid with");
        string file = Path.Combine(DungeonRouteMaker.Folder, "Hive patrol.nav");
        var lines = File.ReadAllLines(file);
        Check.Eq(lines[0], "uTank2 NAV 1.2", "VTank's header");
        Check.Eq(lines[1], "1", "route type Circular (1)");
        var saved = NavRouteParser.Load(file);
        Check.True(saved.LoadWarning == null, "RynthAi's parser reads it without a warning");
        Check.Eq(saved.RouteType, NavRouteType.Circular, "Circular");
        Check.True(saved.Points.Count >= 8 && saved.Points.All(p => p.Type == NavPointType.Point), $"a lap of waypoints ({saved.Points.Count})");
        Check.True(saved.Points.All(p => Nearest(new List<NavPoint> { p }, 15, 52) >= DungeonPathfinder.PortalKeepOutDefault - 0.05), "every point out of the Surface Portal's reach");
        Check.True(rig.S.CurrentRoute.Points.Count == 0 && !rig.S.EnableNavigation && !rig.S.IsMacroRunning, "nothing loaded or started");

        // The same lap /ra dunnav-patrol installs (in its order, from where it starts; its lead-in aside).
        rig.P.HandleDungeonNavPatrol();
        var installed = rig.S.CurrentRoute;
        Check.Eq(installed.RouteType, NavRouteType.Circular, "dunnav-patrol installed a patrol");
        int start = rig.S.ActiveNavIndex;
        var walked = new List<NavPoint>();
        for (int k = 0; k < installed.Points.Count; k++) walked.Add(installed.Points[(start + k) % installed.Points.Count]);
        int at = 0;
        foreach (var p in saved.Points)
        {
            while (at < walked.Count && !(walked[at].NS == p.NS && walked[at].EW == p.EW && walked[at].Z == p.Z)) at++;
            Check.True(at < walked.Count, $"saved point ({LX(p):F1}, {LY(p):F1}) is the installed patrol's, in its order");
            at++;
        }
        Check.True(installed.Points.Count - saved.Points.Count <= 3, $"all of it but a lead-in ({installed.Points.Count} installed, {saved.Points.Count} saved)");
        Check.True(saved.Points[0].NS == walked[0].NS && saved.Points[0].EW == walked[0].EW, "it starts where dunnav-patrol starts");

        said = rig.Typed("/ra dunpatrol-save Hive patrol");
        Check.True(said.Count == 1 && said[0].Contains("already in NavProfiles"), "again: refused without overwrite: " + Show(said));
        said = rig.Typed("/ra dunpatrol-save Hive patrol overwrite");
        Check.True(said[0].Contains("replaced Hive patrol.nav"), "with overwrite: " + Show(said));
        said = rig.Typed("/ra dunpatrol-save");
        Check.True(said.Count == 1 && said[0].Contains("Usage: /ra dunpatrol-save"), "no name: the usage");
        said = rig.Typed("/ra dunpatrol-save a|b");
        Check.True(said.Count == 1 && said[0].Contains("can't be a route name"), "a bad name: " + Show(said));

        said = rig.Typed("/ra dunroute info Hive patrol");
        Check.True(said[0].Contains("circular") && said[0].Contains("made for this dungeon: yes"), "info on the saved patrol: " + Show(said));
    }

    private static void PluginPatrolPolicy()
    {
        // The patrol is off in Threads on DreamWeave (ServerPolicy): the save builds the same patrol, so it asks too.
        var rig = new Rig("NavProfiles-policy", world: "DreamWeave");
        ServerPolicy.NoteChat("[REALM] id=0 inst=7841 eph=1 name=Base World", rig.D.Start);
        var said = rig.Typed("/ra dunpatrol-save Thread patrol");
        Check.True(said.Count == 1 && said[0].Contains("turned off in Threads"), "refused with the policy's line: " + Show(said));
        Check.False(File.Exists(Path.Combine(DungeonRouteMaker.Folder, "Thread patrol.nav")), "nothing written");
        said = rig.Typed("/ra dunroute Thread exit to exit");
        Check.True(said[0].Contains("saved Thread exit.nav"), "a route to the exit still works there: " + Show(said));
        ServerPolicy.Forget();
    }

    private static void PluginJobs()
    {
        var rig = new Rig("NavProfiles-jobs");
        var (id, err) = rig.Request("{\"op\":\"dunroute\",\"name\":\"Job exit\",\"to\":\"exit\"}");
        Check.True(id > 0, "queued: " + err);
        Check.Eq(rig.Poll(id).GetProperty("state").GetString(), "queued", "queued until the tick");
        Check.False(File.Exists(Path.Combine(DungeonRouteMaker.Folder, "Job exit.nav")), "nothing done before the tick");
        rig.Tick();
        var j = rig.Poll(id);
        Check.Eq(j.GetProperty("state").GetString(), "done", "done on the tick: " + j.GetProperty("detail").GetString());
        var res = j.GetProperty("result");
        Check.Eq(res.GetProperty("file").GetString(), Path.Combine(DungeonRouteMaker.Folder, "Job exit.nav"), "result.file");
        Check.Eq(res.GetProperty("type").GetString(), "once", "result.type");
        Check.True(res.GetProperty("points").GetInt32() >= 3 && res.GetProperty("yards").GetDouble() > 30, "points and yards");
        Check.Eq(res.GetProperty("to").GetProperty("kind").GetString(), "exit", "to.kind");
        Check.Eq(res.GetProperty("to").GetProperty("portal").GetString(), "Surface Portal", "to.portal");
        Check.Eq(res.GetProperty("to").GetProperty("id").GetString(), SurfaceId.ToString("X8"), "to.id");
        Check.Near(res.GetProperty("to").GetProperty("x").GetDouble(), 15, 0.01, "to.x landblock-local");
        Check.Near(res.GetProperty("from").GetProperty("y").GetDouble(), 15, 0.01, "from.y landblock-local");
        Check.Eq(res.GetProperty("landblock").GetString(), "7E7E", "landblock");
        Check.True(res.GetProperty("leftOut").ValueKind == JsonValueKind.Array && res.GetProperty("notes").GetArrayLength() >= 1, "leftOut and notes arrays");
        Check.True(File.Exists(Path.Combine(DungeonRouteMaker.Folder, "Job exit.nav")), "the file");
        Check.True(FakeHost.Chat.All(l => !l.Contains("Job exit")), "a job says nothing in chat");

        // A failure: the same name again.
        (id, _) = rig.Request("{\"op\":\"dunroute\",\"name\":\"Job exit\",\"to\":\"far\"}");
        rig.Tick();
        j = rig.Poll(id);
        Check.Eq(j.GetProperty("state").GetString(), "failed", "failed");
        Check.True(j.GetProperty("detail").GetString()!.Contains("already in NavProfiles"), "with the reason");
        Check.Eq(j.GetProperty("result").ValueKind, JsonValueKind.Null, "no result");

        // Portal space: it waits (working), then runs when the character lands; 30 s of it and it gives up.
        var jobs = rig.P.RouteJobs;
        long now = 5000;
        jobs.Clock = () => now;
        FakeHost.Portaling = true;
        (id, _) = rig.Request("{\"op\":\"dunpatrol\",\"name\":\"Job patrol\"}");
        rig.Tick();
        j = rig.Poll(id);
        Check.Eq(j.GetProperty("state").GetString(), "working", "in portal space: working");
        Check.True(j.GetProperty("detail").GetString()!.Contains("portal space"), "waiting for portal space: " + j.GetProperty("detail").GetString());
        now += 1000;
        FakeHost.Portaling = false;
        rig.Tick();
        j = rig.Poll(id);
        Check.Eq(j.GetProperty("state").GetString(), "done", "landed: done: " + j.GetProperty("detail").GetString());
        Check.Eq(j.GetProperty("result").GetProperty("type").GetString(), "circular", "the patrol");
        Check.True(j.GetProperty("result").GetProperty("cells").GetInt32() == rig.D.Graph.Count, "cells");

        FakeHost.Portaling = true;
        (id, _) = rig.Request("{\"op\":\"where\"}");
        rig.Tick();
        now += DunRouteJobs.WaitLimitMs + 1;
        rig.Tick();
        j = rig.Poll(id);
        Check.Eq(j.GetProperty("state").GetString(), "failed", "30 s in portal space: failed");
        Check.True(j.GetProperty("detail").GetString()!.StartsWith("gave up after 30 s"), j.GetProperty("detail").GetString()!);
        FakeHost.Portaling = false;

        // Refused at once.
        var (bad, why) = rig.Request("{\"op\":\"dunroute\",\"name\":\"x\"}");
        Check.Eq(bad, 0u, "a bad request: no job");
        Check.True(why.Contains("\"to\" is missing"), why);
        rig.Set("_loginComplete", false);
        (bad, why) = rig.Request("{\"op\":\"where\"}");
        Check.True(bad == 0 && why.Contains("isn't logged in"), "not logged in: " + why);
        rig.Set("_loginComplete", true);

        // info as a job.
        (id, _) = rig.Request("{\"op\":\"info\",\"name\":\"Job exit\"}");
        rig.Tick();
        j = rig.Poll(id);
        Check.Eq(j.GetProperty("result").GetProperty("forThisDungeon").GetString(), "yes", "info's result");
    }

    private static void PluginWhere()
    {
        var rig = new Rig("NavProfiles-where", namedExit: false);
        // RynthAi sees the character land: first where it was (outdoors), then in the side room after portal space.
        rig.At(42.1, 33.6, 0xA9B4002Fu);
        rig.Tick();
        FakeHost.Portaling = true;
        rig.At(43, 0, rig.D.Side);
        rig.Tick();
        FakeHost.Portaling = false;
        rig.Tick();
        rig.At(15, 15, rig.D.Start);
        Check.True(FakeHost.Logs.Any(l => l.Contains("came into dungeon 7E7E")), "the arrival is noted");

        var said = rig.Typed("/ra dunroute where");
        Check.True(said[0].Contains("in dungeon 7E7E") && said[0].Contains($"{rig.D.Graph.Count} cells"), "where: " + Show(said));
        Check.True(said[0].Contains("you're in room"), "the room it is in");
        Check.True(said.Any(l => l.Contains("portal 'Portal to the Depths'") && l.Contains("exit: the portal nearest where you came in")), "the exit by the arrival: " + Show(said));
        Check.True(said.Any(l => l.Contains("portal 'Portal to the Hub'") && l.Contains("yd on foot")), "the other, with its walk");
        Check.True(said.Any(l => l.Contains("you came in at cell")), "where it came in");

        var (id, _) = rig.Request("{\"op\":\"where\"}");
        rig.Tick();
        var res = rig.Poll(id).GetProperty("result");
        Check.True(res.GetProperty("inDungeon").GetBoolean(), "inDungeon");
        Check.Eq(res.GetProperty("cell").GetString(), rig.D.Start.ToString("X8"), "cell");
        Check.True(res.GetProperty("map").GetBoolean() && res.GetProperty("rooms").GetInt32() >= 8, "map and rooms");
        var portals = res.GetProperty("portals");
        Check.Eq(portals.GetArrayLength(), 2, "both portals");
        Check.Eq(portals[0].GetProperty("name").GetString(), "Portal to the Depths", "nearest first (32 m, then the Hub's 37 m)");
        Check.Eq(portals[1].GetProperty("name").GetString(), "Portal to the Hub", "then the other");
        Check.True(portals[1].GetProperty("walkYards").GetDouble() >= portals[1].GetProperty("yards").GetDouble(), "walk at least the straight line");
        Check.Eq(portals[0].GetProperty("exit").GetString(), "the portal nearest where you came in", "why it would be the exit");
        Check.Eq(portals[1].GetProperty("exit").ValueKind, JsonValueKind.Null, "the other wouldn't");
        Check.Eq(portals[0].GetProperty("id").GetString(), DepthsId.ToString("X8"), "its id");
        Check.Near(portals[0].GetProperty("x").GetDouble(), 45, 0.01, "landblock-local x");
        Check.Near(res.GetProperty("arrival").GetProperty("x").GetDouble(), 43, 0.01, "arrival x");

        // The exit, then, is that one.
        said = rig.Typed("/ra dunroute Out to exit");
        Check.True(said[0].Contains("the exit portal 'Portal to the Depths' (the portal nearest where you came in)"), "dunroute to exit: " + Show(said));

        // A logout forgets where it came in.
        rig.Call("ForgetDunArrival");
        said = rig.Typed("/ra dunroute Out2 to exit");
        Check.True(said.Count == 1 && said[0].Contains("can't tell which portal is the exit"), "not seen arriving: " + Show(said));

        rig.At(42.1, 33.6, 0xA9B4002Fu);
        (id, _) = rig.Request("{\"op\":\"where\"}");
        rig.Tick();
        res = rig.Poll(id).GetProperty("result");
        Check.False(res.GetProperty("inDungeon").GetBoolean(), "outdoors: inDungeon false");
        Check.Eq(res.GetProperty("landblock").GetString(), "A9B4", "and where");
    }

    // ── Doorway points across a save (DunRouteSidecar) ───────────────────────

    private static List<int> DoorIdx(NavRouteParser r)
        => Enumerable.Range(0, r.Points.Count).Where(i => r.Points[i].Doorway).ToList();

    /// <summary>The exit route on the sample, saved as <paramref name="name"/>; its doorway indexes.</summary>
    private static (Sample D, string Path, List<int> Doors) SavedExit(string folder, string name = "Hive exit")
    {
        string dir = Fresh(folder);
        var d = new Sample();
        var b = Build(d.World(), DunTarget.Exit);
        var doors = DoorIdx(b.Route);
        Check.True(doors.Count >= 1, $"the exit route has doorway points ({doors.Count}: {string.Join(",", doors)})");
        Check.True(DungeonRouteMaker.TrySave(name, b.Route, false, Lb, out string path, out _, out string err), "saved: " + err);
        return (d, path, doors);
    }

    private static void DoorwaysSurviveReload()
    {
        var (_, path, doors) = SavedExit("NavProfiles-door1");
        string side = DunRouteSidecar.PathIn(DungeonRouteMaker.Folder);
        Check.True(File.Exists(side), "NavProfiles\\.rynthai-routes.json written beside it");
        var json = JsonDocument.Parse(File.ReadAllBytes(side)).RootElement;
        var entry = json.GetProperty("routes")[0];
        Check.Eq(entry.GetProperty("file").GetString(), "Hive exit.nav", "the file name");
        Check.Eq(entry.GetProperty("sha256").GetString(), DunRouteSidecar.Hash(File.ReadAllBytes(path)), "its SHA-256 as saved");
        Check.Eq(entry.GetProperty("landblock").GetString(), "7E7E", "the dungeon");
        Check.Eq(string.Join(",", entry.GetProperty("doorways").EnumerateArray().Select(e => e.GetInt32())), string.Join(",", doors), "the doorway indexes");

        // The .nav is VTank's, flag line "0" on every point.
        var lines = File.ReadAllLines(path);
        int count = int.Parse(lines[2]);
        var plain = NavRouteParser.Load(path);
        Check.Eq(plain.Points.Count, count, "the file reads as before");
        Check.Eq(DoorIdx(plain).Count, 0, "a plain load has no doorway flags (the format has none)");

        var e = DunRouteSidecar.Match(DungeonRouteMaker.Folder, path, plain, out string? log);
        Check.NotNull(e, "the loaded file is the route maker's, unchanged: " + log);
        Check.Eq(DunRouteSidecar.Apply(plain, e!), doors.Count, "every doorway point restored");
        Check.Eq(string.Join(",", DoorIdx(plain)), string.Join(",", doors), "on the same points as when it was laid");
    }

    private static void DoorwaysEditedFile()
    {
        var (d, path, _) = SavedExit("NavProfiles-door2");
        var lines = File.ReadAllLines(path).ToList();
        lines[4] = (double.Parse(lines[4], System.Globalization.CultureInfo.InvariantCulture) + 0.001).ToString(System.Globalization.CultureInfo.InvariantCulture);   // the first point's EW
        File.WriteAllLines(path, lines);
        var edited = NavRouteParser.Load(path);
        Check.Null(DunRouteSidecar.Match(DungeonRouteMaker.Folder, path, edited, out string? log), "an edited file: no entry");
        Check.True(log != null && log.Contains("was changed since the route maker saved it"), "and one line saying so: " + log);
        Check.Eq(DoorIdx(edited).Count, 0, "no flags");

        // A nav the route maker didn't write, and a route in hand that isn't the file's (dunnav leaves the path).
        string other = Path.Combine(DungeonRouteMaker.Folder, "Recorded.nav");
        var rec = new NavRouteParser { RouteType = NavRouteType.Circular };
        rec.Points.Add(DungeonRouteMaker.WorldPt(Ox + 5, Oy + 5, 0));
        rec.Points.Add(DungeonRouteMaker.WorldPt(Ox + 25, Oy + 5, 0));
        rec.Save(other);
        Check.Null(DunRouteSidecar.Match(DungeonRouteMaker.Folder, other, NavRouteParser.Load(other), out log), "a recorded nav: not the route maker's");
        Check.Null(log, "and nothing logged");
        var (_, path3, _) = SavedExit("NavProfiles-door2b");
        Check.Null(DunRouteSidecar.Match(DungeonRouteMaker.Folder, path3, Build(d.World(), DunTarget.Far).Route, out log), "a route in hand that isn't the file's: nothing");
        Check.Null(log, "nothing logged");
        Check.Null(DunRouteSidecar.Match(DungeonRouteMaker.Folder, Path.Combine(Program.TempRoot, "Hive exit.nav"), NavRouteParser.Load(path3), out _), "the same name in another folder: not this list's");
    }

    private static void DoorwaysPluginLoad()
    {
        var rig = new Rig("NavProfiles-door3");
        var said = rig.Typed("/ra dunroute Hive exit to exit");
        Check.True(said[0].Contains("saved Hive exit.nav"), Show(said));
        string path = Path.Combine(DungeonRouteMaker.Folder, "Hive exit.nav");
        var saved = DunRouteSidecar.Read(DungeonRouteMaker.Folder, out _).Single();
        Check.True(saved.Doorways.Count >= 1, $"the command recorded its doorway points ({saved.Doorways.Count})");

        // Loaded while the character is in another dungeon: nothing, until it is in this one.
        rig.At(50, 100, 0x7E7F0105u);
        rig.S.CurrentNavPath = path;
        rig.S.CurrentRoute = NavRouteParser.Load(path);
        rig.Tick();
        Check.Eq(DoorIdx(rig.S.CurrentRoute).Count, 0, "in another dungeon: no flags");
        rig.Tick();
        Check.Eq(DoorIdx(rig.S.CurrentRoute).Count, 0, "still none there");
        rig.At(15, 15, rig.D.Start);
        rig.Tick();
        Check.Eq(string.Join(",", DoorIdx(rig.S.CurrentRoute)), string.Join(",", saved.Doorways), "in its dungeon: the doorway points restored");
        Check.True(FakeHost.Logs.Any(l => l.Contains("'Hive exit.nav':") && l.Contains("doorway point(s) restored")), "logged");

        // Loaded in its dungeon: at once (the next tick, before navigation).
        rig.S.CurrentRoute = NavRouteParser.Load(path);
        rig.Tick();
        Check.Eq(DoorIdx(rig.S.CurrentRoute).Count, saved.Doorways.Count, "loaded in the dungeon: restored on the next tick");

        // The patrol save keeps its doorway points too.
        said = rig.Typed("/ra dunpatrol-save Hive patrol");
        Check.True(said[0].Contains("saved Hive patrol.nav"), Show(said));
        string ppath = Path.Combine(DungeonRouteMaker.Folder, "Hive patrol.nav");
        rig.S.CurrentNavPath = ppath;
        rig.S.CurrentRoute = NavRouteParser.Load(ppath);
        rig.Tick();
        var entry = DunRouteSidecar.Read(DungeonRouteMaker.Folder, out _).Single(x => x.File == "Hive patrol.nav");
        Check.True(entry.Doorways.Count > 0 && DoorIdx(rig.S.CurrentRoute).Count == entry.Doorways.Count,
                   $"the saved patrol walks its doorways as dunnav-patrol does ({entry.Doorways.Count} points)");
        rig.P.HandleDungeonNavPatrol();
        Check.True(rig.S.CurrentRoute.Points.Count(p => p.Doorway) >= entry.Doorways.Count, "as many as dunnav-patrol's own lap has");

        // Edited after loading, before the character is in its dungeon: not restored.
        rig.At(50, 100, 0x7E7F0105u);
        rig.S.CurrentNavPath = path;
        rig.S.CurrentRoute = NavRouteParser.Load(path);
        rig.Tick();
        NavRouteEditing.Apply(rig.S, list => { list.RemoveAt(1); return list; });
        rig.At(15, 15, rig.D.Start);
        rig.Tick();
        Check.Eq(DoorIdx(rig.S.CurrentRoute).Count, 0, "edited in memory first: no flags");
        Check.True(FakeHost.Logs.Any(l => l.Contains("was edited before you were in its dungeon")), "said in the log");
    }

    private static void DoorwaysCorruptSidecar()
    {
        var (_, path, doors) = SavedExit("NavProfiles-door4");
        string side = DunRouteSidecar.PathIn(DungeonRouteMaker.Folder);
        foreach (string junk in new[] { "{ not json", "[1,2,3]", "{\"routes\":5}", "\u0000\u0001garbage" })
        {
            File.WriteAllText(side, junk);
            var r = NavRouteParser.Load(path);
            Check.Eq(r.Points.Count, int.Parse(File.ReadAllLines(path)[2]), "with a corrupt list the nav loads as ever");
            Check.Null(DunRouteSidecar.Match(DungeonRouteMaker.Folder, path, r, out string? log), "no entry");
            Check.True(log != null && log.Contains(DunRouteSidecar.FileName), "the problem logged: " + log);
            Check.Eq(DoorIdx(r).Count, 0, "no flags");
        }
        // A good entry beside a bad one: the good one still counts.
        string good = File.ReadAllText(side);
        File.WriteAllText(side, "{\"version\":1,\"routes\":[{\"file\":\"x.nav\",\"sha256\":\"nope\"}]}");
        Check.Eq(DunRouteSidecar.Read(DungeonRouteMaker.Folder, out string? p1).Count, 0, "a bad entry is skipped: " + p1);
        File.Delete(side);
        Check.Null(DunRouteSidecar.Match(DungeonRouteMaker.Folder, path, NavRouteParser.Load(path), out string? none), "no list: no entry");
        Check.Null(none, "and nothing to log");

        // The next save writes a good list again.
        File.WriteAllText(side, "{ not json");
        var d = new Sample();
        var notes = new List<string>();
        Check.True(DungeonRouteMaker.TrySave("Hive exit", Build(d.World(), DunTarget.Exit).Route, true, Lb, out _, out _, out _, notes), "saved over the corrupt list");
        Check.True(notes.Any(n => n.Contains("corrupt") && n.Contains("written again")), "and said: " + string.Join(" | ", notes));
        var r2 = NavRouteParser.Load(path);
        var e2 = DunRouteSidecar.Match(DungeonRouteMaker.Folder, path, r2, out _);
        Check.NotNull(e2, "the list is good again");
        Check.Eq(DunRouteSidecar.Apply(r2, e2!), doors.Count, "and restores");

        // The plugin's tick with a corrupt list: no exception, no flags.
        var rig = new Rig("NavProfiles-door4b");
        rig.Typed("/ra dunroute Hive exit to exit");
        File.WriteAllText(DunRouteSidecar.PathIn(DungeonRouteMaker.Folder), "\u0000garbage");
        rig.S.CurrentNavPath = Path.Combine(DungeonRouteMaker.Folder, "Hive exit.nav");
        rig.S.CurrentRoute = NavRouteParser.Load(rig.S.CurrentNavPath);
        rig.Tick();
        Check.Eq(DoorIdx(rig.S.CurrentRoute).Count, 0, "the tick with a corrupt list: no flags (and nothing threw)");
    }

    private static void DoorwaysOverwriteAndPrune()
    {
        var (d, path, _) = SavedExit("NavProfiles-door5");
        Check.True(DungeonRouteMaker.TrySave("Far end", Build(d.World(), DunTarget.Far).Route, false, Lb, out string farPath, out _, out _), "a second route");
        Check.Eq(DunRouteSidecar.Read(DungeonRouteMaker.Folder, out _).Count, 2, "two entries");
        string before = DunRouteSidecar.Read(DungeonRouteMaker.Folder, out _).Single(e => e.File == "Hive exit.nav").Sha256;
        var other = Build(d.World(), new DunTarget(DunTargetKind.Portal, Portal: "depths"));
        Check.True(DungeonRouteMaker.TrySave("Hive exit", other.Route, true, Lb, out _, out bool ow, out _), "overwritten");
        Check.True(ow, "it was an overwrite");
        var list = DunRouteSidecar.Read(DungeonRouteMaker.Folder, out _);
        var now = list.Single(e => e.File == "Hive exit.nav");
        Check.True(now.Sha256 != before && now.Sha256 == DunRouteSidecar.Hash(File.ReadAllBytes(path)), "the entry follows the new file");
        Check.Eq(string.Join(",", now.Doorways), string.Join(",", DoorIdx(other.Route)), "with the new route's doorway points");
        Check.Eq(list.Count, 2, "still one entry a file");

        File.Delete(farPath);
        DungeonRouteMaker.TrySave("Spot", Build(d.World(), new DunTarget(DunTargetKind.Position, X: -17.5, Y: 25, Z: 0)).Route, false, Lb, out _, out _, out _);
        list = DunRouteSidecar.Read(DungeonRouteMaker.Folder, out _);
        Check.False(list.Any(e => e.File == "Far end.nav"), "the entry of a file that's gone is pruned at the next save");
        Check.Eq(string.Join(",", list.Select(e => e.File)), "Hive exit.nav,Spot.nav", "the rest kept");
    }

    private static void DoorwaysEngineReach()
    {
        var (d, path, doors) = SavedExit("NavProfiles-door6");
        var restored = NavRouteParser.Load(path);
        DunRouteSidecar.Apply(restored, DunRouteSidecar.Match(DungeonRouteMaker.Folder, path, restored, out _)!);
        // A doorway point with a plain one before it at least 4 yd away.
        int k = doors.First(i => i > 0 && NavRouteParser.IsPlainWaypoint(restored.Points[i - 1].Type)
                                 && Dist(restored.Points[i - 1], restored.Points[i]) >= 4);
        var a = restored.Points[k - 1]; var p = restored.Points[k];
        double len = Dist(a, p), ux = (LX(p) - LX(a)) / len, uy = (LY(p) - LY(a)) / len;

        int Walk(NavRouteParser route, double shortBy)
        {
            FakeHost.Reset();
            long now = 1_000_000;
            NavigationEngine.Clock = () => now;
            try
            {
                FakeHost.PlayerPose = (d.Start, (float)(LX(p) - ux * 3.5), (float)(LY(p) - uy * 3.5), 0f);
                var host = FakeHost.Create();
                var s = new LegacyUiSettings { IsMacroRunning = true, EnableNavigation = true, CurrentRoute = route, ActiveNavIndex = k };
                var e = new NavigationEngine(host, s);
                now += 100; e.Tick();
                FakeHost.PlayerPose = (d.Start, (float)(LX(p) - ux * shortBy), (float)(LY(p) - uy * shortBy), 0f);
                now += 100; e.Tick();
                return s.ActiveNavIndex;
            }
            finally { NavigationEngine.Clock = () => Environment.TickCount64; }
        }

        Check.True(restored.Points[k].Doorway, $"point {k} is a restored doorway point");
        Check.Eq(Walk(restored, 1.2), k, "1.2 yd short of a restored doorway point: not there yet (its reach is 1 yd)");
        Check.Eq(Walk(restored, 0.9), k + 1, "0.9 yd: reached");
        var plain = NavRouteParser.Load(path);
        Check.Eq(Walk(plain, 1.2), k + 1, "the same file loaded plain (no list): reached at 1.2 yd, the ordinary 1.5 yd reach");
    }

    private static double Dist(NavPoint a, NavPoint b) => Math.Sqrt((LX(a) - LX(b)) * (LX(a) - LX(b)) + (LY(a) - LY(b)) * (LY(a) - LY(b)));

    // ── Tiny Hive on the real dats ───────────────────────────────────────────

    private const string AcDir = @"C:\Turbine\Asheron's Call";

    private static void TinyHiveRealDats()
    {
        if (!Directory.Exists(AcDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(AcDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x0289, start = 0x028902A5, nook = 0x028902BA;
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
            Check.True(graph.Count > 400 && geo != null, $"Tiny Hive loaded ({graph.Count} cells)");
            var s = graph[start];
            // The Surface Portal (weenie 11218, 0x7028905D): cell 028902BA, local (110, -3.77927, -23.995).
            var p = new PatrolPortal { Id = 0x7028905D, Name = "Surface Portal", Cell = nook, X = 2 * 192 + 110, Y = 0x89 * 192 - 3.77927, Z = -23.995 };
            var w = new DunRouteWorld
            {
                Landblock = lb, PlayerCell = start, StartCell = start,
                X = DungeonGeometry.NavToWorld(s.EW), Y = DungeonGeometry.NavToWorld(s.NS), Z = s.Z,
                Graph = graph, Geo = geo, Portals = new List<PatrolPortal> { p },
            };
            var exit = Build(w, DunTarget.Exit);
            Check.Eq(exit.Portal?.Name, "Surface Portal", "the exit");
            Check.Eq(exit.Route.Points[^1].Type, NavPointType.PortalNPC, "a Portal step at the end");
            var last = exit.Route.Points[^2];
            double d = Math.Sqrt(Math.Pow(DungeonGeometry.NavToWorld(last.EW) - p.X, 2) + Math.Pow(DungeonGeometry.NavToWorld(last.NS) - p.Y, 2));
            Check.True(d >= p.Radius - 0.05 && d < p.Radius + 1.0, $"the walk stops at its edge ({d:F2} m)");
            Console.WriteLine($"    exit: {exit.Route.Points.Count} points, {exit.Yards:F1} yd, {exit.PathCells} cells, {exit.Doorways} doorway(s)");

            var far = Build(w, DunTarget.Far);
            Check.True(far.Yards > 100 && far.PathCells > 10, $"the far end is a long way ({far.Yards:F0} yd, {far.PathCells} cells)");
            double near = DungeonPathfinder.NearestApproach(far.Route.Points.Where(q => NavRouteParser.IsPlainWaypoint(q.Type)).ToList(), false, p);
            Check.True(near >= p.Radius - 0.05, $"and keeps out of the Surface Portal on its level ({near:F1} m)");
            Console.WriteLine($"    far: cell {far.GoalCell:X8}, {far.Route.Points.Count} points, {far.Yards:F0} yd");
        }
        finally { DungeonPathfinder.InvalidateCache(); }
    }
}
