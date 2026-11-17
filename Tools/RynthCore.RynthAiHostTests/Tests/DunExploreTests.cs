using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The dungeon explore (2026-11-13): /ra dunnav-explore walks through every room of the dungeon once,
// then stops (DungeonPathfinder.Explore.cs, RynthAiPlugin.DunExplore.cs). On DunRouteTests' hand-built
// dungeon (a 3x3 hall, a corridor north to a nook with the Surface Portal, east to a side room with the
// Portal to the Depths, west to a room):
//   - the walk is a Once route that enters every room it can walk to, once, and ends in the last one
//     (no walk back), keeping out of both portals (the nook behind the Surface Portal is left out);
//   - rooms already entered are left out when it is laid again; without the map it goes cell by cell;
//   - a closet next door is entered before moving on;
//   - the plugin: the command starts it with the macro on, rooms count as the character walks in, a
//     portal coming into view lays the rest again, rooms passed by are gone back for, the end is done
//     with navigation off and the macro left on; stop, another route and leaving the dungeon end it;
//   - radunexplore[] and the counts;
//   - Tiny Hive on the real dats, when they are on this PC.
internal static class DunExploreTests
{
    public static void Register(Runner r)
    {
        r.Add("dun explore: a Once walk into every room once, ending in the last (no walk back), out of both portals", EveryRoomOnce);
        r.Add("dun explore: rooms already entered are left out when the walk is laid again", EnteredLeftOut);
        r.Add("dun explore: without the dungeon map the walk goes cell by cell, every cell once", CellWalk);
        r.Add("dun explore: a closet next door is entered before moving on", ClosetFirst);
        r.Add("dun explore: plugin: started with the macro on, rooms counted, relaid round a new portal, missed rooms gone back for, done", PluginRun);
        r.Add("dun explore: plugin: stop, another route, leaving the dungeon and the server's word end it", PluginEnds);
        r.Add("dun explore: plugin: stuck for good in an opening (something not on the map in it): the opening is closed, the rest laid round it", PluginStuckOpening);
        r.Add("dun patrol: stuck for good in an opening: the opening is closed and the patrol laid round it; kept in that dungeon", PatrolStuckOpening);
        r.Add("dun explore (2026-11-16, a hall 0x01F5): stuck for good in a room, away from the doorway on its way: no opening closed, the spot ahead of it walked round", PluginStuckSpot);
        r.Add("dun explore: plugin: a spider web in a doorway closes it before the walk; one in the open is left alone; one seen mid-walk relays it", PluginWebInDoorway);
        r.Add("dun explore: plugin: stuck in a doorway, what stands in it is named and learned: another of that name in a doorway is walked round", PluginStuckLearnsName);
        r.Add("dun explore: plugin: enter and scan - waits at room middles; when the walk is over a monster still in the dungeon is walked to, then done", PluginLeftoverMonster);
        r.Add("dun explore: Thread 0x00B5 on the real dats - the walk goes into the boss's 6-cell dead end and waits at its middle (if the dats are here)", Real00B5BossRoom);
        r.Add("dun explore: a door you can't use is shut: walked round, the rooms behind it counted; the walk ends waiting at it", PluginShutDoor);
        r.Add("dun explore: lever errands: one behind the door can't be walked to, a button that opens nothing, a lever that opens it (through it first)", PluginLeverErrands);
        r.Add("dun explore: a Thread puzzle gate is a shut door to the walk; its puzzle lever is never pulled", PluginPuzzleGate);
        r.Add("dun explore: done at the doors, a door that opens by itself, the door opener giving up on a door, resume", PluginDoorsMore);
        r.Add("dun explore: radunexplore[], radunexplorerooms[], radunexplorevisited[]", Expressions);
        r.Add("dun explore: Tiny Hive 0x0289 on the real dats: every room, out of the Surface Portal (if the dats are here)", TinyHiveRealDats);
    }

    private const double Ox = DunRouteTests.Ox, Oy = DunRouteTests.Oy;

    private static (double X, double Y, double Z) Mid(DunRouteTests.Sample d, uint cell)
    {
        var n = d.Graph[cell];
        return (DungeonGeometry.NavToWorld(n.EW), DungeonGeometry.NavToWorld(n.NS), n.Z);
    }

    /// <summary>The rooms the walk's legs pass through (each leg looked at every half metre).</summary>
    private static HashSet<int> RoomsWalked(List<NavPoint> pts, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo, ExplorePlan plan)
    {
        var rooms = new HashSet<int>();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = i + 1 < pts.Count ? pts[i + 1] : a;
            double len = Math.Sqrt(Math.Pow(DungeonGeometry.NavToWorld(b.EW) - DungeonGeometry.NavToWorld(a.EW), 2)
                                 + Math.Pow(DungeonGeometry.NavToWorld(b.NS) - DungeonGeometry.NavToWorld(a.NS), 2));
            int n = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (int k = 0; k <= n; k++)
            {
                double t = (double)k / n;
                var q = new NavPoint { NS = a.NS + (b.NS - a.NS) * t, EW = a.EW + (b.EW - a.EW) * t, Z = a.Z + (b.Z - a.Z) * t };
                int r = plan.RoomOf(DungeonPathfinder.CellAt(graph, geo, q));
                if (r >= 0) rooms.Add(r);
            }
        }
        return rooms;
    }

    private static void EveryRoomOnce()
    {
        var d = new DunRouteTests.Sample();
        var geo = d.Geo();
        var portals = new List<PatrolPortal> { d.Surface(), d.Depths() };
        var route = DungeonPathfinder.BuildExploreRoute(d.Graph, d.Start, null, geo, null, portals, Mid(d, d.Start), null, out var plan);

        Check.Eq(route.RouteType, NavRouteType.Once, "a Once route");
        Check.True(plan.ByRoom, "laid on the map's rooms");
        Check.True(route.Points.Count >= 6, $"points laid ({route.Points.Count})");
        int hall = plan.RoomOf(d.Start);
        Check.True(Enumerable.Range(0, 3).All(i => Enumerable.Range(0, 3).All(j => plan.RoomOf(d.Hall[i, j]) == hall)), "the 3x3 hall is one room");
        Check.False(plan.PlanRooms.Contains(plan.RoomOf(d.Nook)), "the nook behind the Surface Portal is left out");
        foreach (uint c in new[] { d.C1, d.E1, d.Side, d.W1, d.West })
            Check.True(plan.PlanRooms.Contains(plan.RoomOf(c)), $"room of cell {c:X8} is in the plan");

        var walked = RoomsWalked(route.Points, d.Graph, geo, plan);
        Check.True(plan.PlanRooms.IsSubsetOf(walked), $"every room is walked into: plan {string.Join(",", plan.PlanRooms)}, walked {string.Join(",", walked)}");
        Check.Eq(plan.Order.Count, plan.PlanRooms.Count - 1, "each room but the start once in the order");
        Check.Eq(plan.Order.Distinct().Count(), plan.Order.Count, "none twice");
        int lastRoom = plan.RoomOf(DungeonPathfinder.CellAt(d.Graph, geo, route.Points[^1]));
        Check.Eq(lastRoom, plan.Order[^1], "it ends in the last new room, not back at the start");
        foreach (var p in portals)
        {
            double near = DungeonPathfinder.NearestApproach(route.Points, closed: false, p);
            Check.True(near >= p.Radius - 0.05, $"keeps out of {p.Name} ({near:F1} m, radius {p.Radius:F1})");
        }
    }

    private static void EnteredLeftOut()
    {
        var d = new DunRouteTests.Sample();
        var geo = d.Geo();
        var portals = new List<PatrolPortal> { d.Surface(), d.Depths() };
        var visited = new HashSet<uint> { d.Start, d.W1, d.West };
        var route = DungeonPathfinder.BuildExploreRoute(d.Graph, d.Start, null, geo, null, portals, Mid(d, d.Start), visited, out var plan);
        int west = plan.RoomOf(d.West), w1 = plan.RoomOf(d.W1);
        Check.True(plan.DoneRooms.Contains(west) && plan.DoneRooms.Contains(w1), "the west rooms count as done");
        Check.False(plan.Order.Contains(west) || plan.Order.Contains(w1), "and aren't in the order: " + string.Join(",", plan.Order));
        Check.False(RoomsWalked(route.Points, d.Graph, geo, plan).Contains(west), "the walk doesn't go into the west room");
        Check.True(plan.Order.Contains(plan.RoomOf(d.Side)) && plan.Order.Contains(plan.RoomOf(d.C1)), "the rest still is");

        var all = new HashSet<uint>(d.Graph.Keys);
        var none = DungeonPathfinder.BuildExploreRoute(d.Graph, d.Start, null, geo, null, portals, Mid(d, d.Start), all, out var p2);
        Check.Eq(none.Points.Count, 0, "everything entered: nothing to walk");
        Check.Eq(p2.Order.Count, 0, "and no rooms in the order");
    }

    private static void CellWalk()
    {
        var d = new DunRouteTests.Sample();
        var route = DungeonPathfinder.BuildExploreRoute(d.Graph, d.Start, null, null, null, null, null, null, out var plan);
        Check.False(plan.ByRoom, "no map: cells");
        Check.Eq(route.RouteType, NavRouteType.Once, "a Once route");
        Check.Eq(plan.PlanRooms.Count, d.Graph.Count, "every cell (no portals given: the nook too)");
        Check.Eq(plan.Order.Count, d.Graph.Count - 1, "every cell but the start once");
        Check.True(route.Points.Count > 0, "points laid");
    }

    private static void ClosetFirst()
    {
        // A hub H with a closet K, and a branch B1-B2-B3 off it; start in H.
        var g = new Dictionary<uint, DungeonNavNode>();
        void Link(uint a, uint b)
        {
            foreach (uint c in new[] { a, b })
                if (!g.ContainsKey(c)) g[c] = new DungeonNavNode { CellId = c, NS = (c & 0xFF) * 0.01, EW = (c >> 8 & 0xFF) * 0.01 };
            g[a].Neighbors.Add(b); g[b].Neighbors.Add(a);
        }
        const uint H = 0x7E7E0101, K = 0x7E7E0202, B1 = 0x7E7E0303, B2 = 0x7E7E0404, B3 = 0x7E7E0505;
        Link(H, B1); Link(B1, B2); Link(B2, B3); Link(H, K);
        DungeonPathfinder.BuildExploreRoute(g, H, null, null, null, null, null, null, out var plan);
        var order = plan.Order.Select(r => plan.Rooms.Cells[r][0]).ToList();
        Check.Eq(string.Join(",", order.Select(c => c.ToString("X8"))), string.Join(",", new[] { K, B1, B2, B3 }.Select(c => c.ToString("X8"))),
                 "closet first, then the branch to its end (no walk back for the closet)");
    }

    // ── The plugin ───────────────────────────────────────────────────────────

    private static object Status(DunRouteTests.Rig rig) => rig.Call("DunExploreStatus")!;
    private static (int State, int Rooms, int Visited) St(DunRouteTests.Rig rig) => ((int State, int Rooms, int Visited))Status(rig);
    private static void Tick(DunRouteTests.Rig rig) => rig.Call("TickDunExplore");
    private static string Show(List<string> lines) => lines.Count == 0 ? "(nothing)" : string.Join(" | ", lines);

    private static List<string> Said(Action a)
    {
        int before = FakeHost.Chat.Count;
        a();
        return FakeHost.Chat.Skip(before).ToList();
    }

    private static void StandIn(DunRouteTests.Rig rig, uint cell)
    {
        var (x, y, _) = Mid(rig.D, cell);
        rig.At(x - Ox, y - Oy, cell);
    }

    /// <summary>In cell <paramref name="a"/>, a metre short of its doorway into <paramref name="b"/> (stuck in that opening).</summary>
    private static void StandAtDoorway(DunRouteTests.Rig rig, uint a, uint b)
    {
        var geo = rig.D.Geo()!;
        Check.True(geo.TryGetDoorway(a, b, out var d), $"a doorway {a:X8}>{b:X8}");
        var (nx, ny) = d.NormalFrom(a);
        rig.At(d.X - nx * 1.0 - Ox, d.Y - ny * 1.0 - Oy, a);
    }

    private static void Finish(DunRouteTests.Rig rig)
    {
        // What NavigationEngine.CompleteOnceRoute does at the end of a Once route.
        rig.S.ActiveNavIndex = rig.S.CurrentRoute.Points.Count;
        rig.S.EnableNavigation = false;
    }

    private static void PluginRun()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore");
        // Only the Depths portal known at the start: the Surface Portal comes into view later.
        rig.Cache.OnDeleteObject(DunRouteTests.SurfaceId);
        rig.Cache.Tick();
        var said = rig.Typed("/ra dunnav-explore");
        Check.True(said.Count == 1 && said[0].StartsWith("[RynthAi] DunNav-Explore: ") && said[0].Contains("room(s) to walk through") && said[0].Contains("exploring"),
                   "it answers: " + Show(said));
        Check.True(said[0].Contains("kept out of 1 portal(s)"), "with the one portal known: " + said[0]);
        var first = rig.S.CurrentRoute;
        Check.Eq(first.RouteType, NavRouteType.Once, "a Once route installed");
        Check.True(rig.S.IsMacroRunning && rig.S.EnableNavigation, "macro and navigation on");
        var s = St(rig);
        Check.Eq(s.State, 1, "exploring");
        Check.Eq(s.Visited, 1, "the start room counts as entered");
        int rooms = s.Rooms;
        Check.True(rooms >= 5, $"rooms to walk through ({rooms})");

        said = rig.Typed("/ra dunnav-explore");
        Check.True(said.Count == 1 && said[0].Contains("already exploring, 1 of"), "again: already exploring: " + Show(said));
        Check.True(ReferenceEquals(rig.S.CurrentRoute, first), "and the walk is the same");

        StandIn(rig, rig.D.W1);
        Tick(rig);
        StandIn(rig, rig.D.West);
        Tick(rig);
        Check.Eq(St(rig).Visited, 3, "w1 and the west room entered");

        // The Surface Portal comes into view: the rest is laid again round it (the west rooms left out).
        rig.Cache.OnCreateObject(DunRouteTests.SurfaceId);
        rig.Cache.Tick();
        var run = rig.Get("_dunExplore")!;
        run.GetType().GetField("NextCheckAt")!.SetValue(run, 0L);
        Tick(rig);
        Check.False(ReferenceEquals(rig.S.CurrentRoute, first), "relaid when the portal came into view");
        Check.Eq(rig.S.CurrentRoute.RouteType, NavRouteType.Once, "still a Once route");
        Check.Eq(St(rig).Visited, 3, "the rooms entered are kept");
        Check.True(St(rig).Rooms <= rooms, $"the nook is out now ({St(rig).Rooms} rooms)");
        var surface = rig.D.Surface();
        surface.Radius = 3.5;
        Check.True(DungeonPathfinder.NearestApproach(rig.S.CurrentRoute.Points, closed: false, surface) >= surface.Radius - 0.1, "kept out of the Surface Portal");

        // The walk ends with rooms not entered (the character was carried past them): it goes back once.
        Finish(rig);
        said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("passed by without going in → going back for them")), "missed rooms gone back for: " + Show(said));
        Check.True(rig.S.EnableNavigation && rig.S.CurrentRoute.Points.Count > 0, "a walk for them");
        Check.Eq(St(rig).State, 1, "still exploring");

        // Walk into every room, then the walk ends: done.
        foreach (uint c in rig.D.Graph.Keys.Where(c => c != rig.D.Nook)) { StandIn(rig, c); Tick(rig); }
        s = St(rig);
        Check.Eq(s.Visited, s.Rooms, $"every room entered ({s.Visited} of {s.Rooms})");
        Finish(rig);
        said = Said(() => Tick(rig));
        Check.True(said.Count == 1 && said[0].Contains($"DunNav-Explore: done, {s.Rooms} of {s.Rooms} rooms") && said[0].Contains("the macro is still on"),
                   "done: " + Show(said));
        Check.Eq(St(rig).State, 3, "done");
        Check.True(!rig.S.EnableNavigation && rig.S.CurrentRoute.Points.Count == 0, "navigation off, the walk taken off");
        Check.True(rig.S.IsMacroRunning, "the macro left on");
        said = rig.Typed("/ra dunnav-explore status");
        Check.True(said[0].Contains("done in dungeon 0x7E7E") && said[0].Contains($"{s.Rooms} of {s.Rooms} rooms"), "status: " + Show(said));
    }

    private static void PluginEnds()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore-ends");
        var said = rig.Typed("/ra dunnav-explore stop");
        Check.True(said.Count == 1 && said[0].Contains("nothing to stop"), "stop with none running: " + Show(said));
        said = rig.Typed("/ra dunnav-explore sideways");
        Check.True(said.Count == 1 && said[0].Contains("Usage: /ra dunnav-explore"), "a wrong word: the usage: " + Show(said));

        rig.Typed("/ra dunnav-explore");
        said = rig.Typed("/ra dunnav-explore stop");
        Check.True(said.Count == 1 && said[0].Contains("DunNav-Explore stopped (you stopped it): 1 of"), "stopped: " + Show(said));
        Check.True(!rig.S.EnableNavigation && rig.S.CurrentRoute.Points.Count == 0, "navigation off, the walk taken off");
        Check.Eq(St(rig).State, 4, "stopped");

        rig.Typed("/ra dunnav-explore");
        var other = new NavRouteParser { RouteType = NavRouteType.Circular };
        other.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1, EW = 1 });
        rig.S.CurrentRoute = other;
        said = Said(() => Tick(rig));
        Check.True(said.Count == 1 && said[0].Contains("stopped (another route is the route now)"), "another route: " + Show(said));
        Check.True(ReferenceEquals(rig.S.CurrentRoute, other), "that route stays");

        rig.Typed("/ra dunnav-explore");
        rig.At(42.1, 33.6, 0xA9B4002Fu);
        said = Said(() => Tick(rig));
        Check.True(said.Count == 1 && said[0].Contains("stopped (you left the dungeon)"), "left the dungeon: " + Show(said));
        Check.True(!rig.S.EnableNavigation, "navigation off");
        said = rig.Typed("/ra dunnav-explore");
        Check.True(said.Count == 1 && said[0].Contains("dunnav-explore requires you to be inside a dungeon"), "outdoors: refused: " + Show(said));

        var thread = new DunRouteTests.Rig("NavProfiles-explore-thread", world: "DreamWeave");
        ServerPolicy.NoteChat("[REALM] id=0 inst=7841 eph=1 name=Base World", thread.D.Start);
        said = thread.Typed("/ra dunnav-explore");
        Check.True(said.Count == 1 && said[0].Contains("turned off in Threads"), "in a Thread on DreamWeave: refused with the policy's line: " + Show(said));
        Check.False(thread.S.IsMacroRunning || thread.S.EnableNavigation, "nothing started");
        ServerPolicy.Forget();
    }

    // 2026-11-14 on DreamWeave, 0x5456: a spider web in a doorway (then not on the map); the engine's detours
    // went back through it and then it only tried its escapes. Here: heading down the east corridor for the
    // side room when every detour is used up and it is still stuck.
    private static void PluginStuckOpening()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore-stuck");
        rig.Typed("/ra dunnav-explore");
        int rooms = St(rig).Rooms;
        StandAtDoorway(rig, rig.D.E1, rig.D.Side);
        Tick(rig);
        var before = rig.S.CurrentRoute;
        var geo = rig.D.Geo();
        int toSide = before.Points.FindIndex(p => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side);
        Check.True(toSide >= 0, "the walk goes into the side room");
        rig.S.ActiveNavIndex = toSide;

        Check.True(Said(() => Tick(rig)).Count == 0 && ReferenceEquals(rig.S.CurrentRoute, before), "not stuck: nothing happens");
        bool stuck = true;
        rig.P.ExploreStuckProbe = () => stuck;
        var said = Said(() => Tick(rig));
        stuck = false;
        Check.True(said.Any(l => l.Contains($"couldn't get past something between cells {rig.D.E1:X8} and {rig.D.Side:X8}") && l.Contains("going round it")),
                   "it says which opening it gives up: " + Show(said));
        Check.False(ReferenceEquals(rig.S.CurrentRoute, before), "the rest laid again");
        Check.Eq(St(rig).State, 1, "still exploring");
        Check.Eq(St(rig).Rooms, rooms - 1, "the side room (only that way) is left out");
        var run = rig.Get("_dunExplore")!;
        Check.Eq(((HashSet<ulong>)run.GetType().GetField("StuckEdges")!.GetValue(run)!).Count, 1, "one opening closed");
        Check.False(rig.S.CurrentRoute.Points.Any(p => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side), "and the new walk doesn't go in there");
        Check.True(Said(() => Tick(rig)).Count == 0, "unstuck: nothing more");
    }

    // 2026-11-16, a DreamWeave hall (0x01F5): stuck for good 5 and 11 m from the doorway on its way, three openings
    // were closed one after another and it stood stuck as before. Now the spot just ahead of it is walked round.
    private static void PluginStuckSpot()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore-stuckspot");
        rig.Typed("/ra dunnav-explore");
        int rooms = St(rig).Rooms;
        StandIn(rig, rig.D.E1);
        Tick(rig);
        var before = rig.S.CurrentRoute;
        var geo = rig.D.Geo();
        int toSide = before.Points.FindIndex(p => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side);
        Check.True(toSide >= 0, "the walk goes into the side room");
        rig.S.ActiveNavIndex = toSide;
        var (mx, my, _) = Mid(rig.D, rig.D.E1);
        Check.True(geo!.TryGetDoorway(rig.D.E1, rig.D.Side, out var d) && Math.Sqrt((d.X - mx) * (d.X - mx) + (d.Y - my) * (d.Y - my)) > RynthAiPlugin.StuckAtDoorwayMeters,
                   "it stands in the middle of its cell, away from the doorway");

        bool stuck = true;
        rig.P.ExploreStuckProbe = () => stuck;
        int logs = FakeHost.Logs.Count;
        var said = Said(() => Tick(rig));
        stuck = false;
        Check.True(said.Any(l => l.Contains($"couldn't get past something at a spot in cell {rig.D.E1:X8}") && l.Contains("walking round that spot")),
                   "it says it walks round the spot: " + Show(said));
        Check.True(Logged(logs, "from the doorway on its way: walking round the spot"), "and logs where");
        var run = rig.Get("_dunExplore")!;
        Check.Eq(((HashSet<ulong>)run.GetType().GetField("StuckEdges")!.GetValue(run)!).Count, 0, "no opening closed");
        var spots = (List<PatrolPortal>)run.GetType().GetField("StuckSpots")!.GetValue(run)!;
        Check.Eq(spots.Count, 1, "one spot");
        Check.Eq(St(rig).Rooms, rooms, "no room left out");
        Check.False(ReferenceEquals(rig.S.CurrentRoute, before), "the rest laid again");
        var sp = spots[0];
        var pts = rig.S.CurrentRoute.Points;
        double Dist(double x, double y) => Math.Sqrt((x - sp.X) * (x - sp.X) + (y - sp.Y) * (y - sp.Y));
        bool inside = false;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double ax = DungeonGeometry.NavToWorld(pts[i].EW), ay = DungeonGeometry.NavToWorld(pts[i].NS);
            double bx = DungeonGeometry.NavToWorld(pts[i + 1].EW), by = DungeonGeometry.NavToWorld(pts[i + 1].NS);
            for (int k = 0; k <= 20; k++)
            {
                double t = k / 20.0;
                if (Dist(ax + (bx - ax) * t, ay + (by - ay) * t) < sp.Radius - 0.05) inside = true;
            }
        }
        Check.False(inside, "the new walk keeps out of the spot's circle");
        Check.True(pts.Any(p => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side), "and still goes into the side room");
    }

    // The same web, for the dungeon patrol (Tom: "This same change needs to be made in patrol").
    private static void PatrolStuckOpening()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-patrol-stuck");
        var said = rig.Typed("/ra dunnav-patrol");
        Check.True(said.Any(l => l.Contains("circular patrol started")), "the patrol starts: " + Show(said));
        var before = rig.S.CurrentRoute;
        var geo = rig.D.Geo();
        bool InSide(NavPoint p) => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side;
        Check.Eq(before.RouteType, NavRouteType.Circular, "a circular patrol");
        int toSide = before.Points.FindIndex(InSide);
        Check.True(toSide >= 0, "its lap goes into the side room (coverage Everything)");
        StandAtDoorway(rig, rig.D.E1, rig.D.Side);
        rig.S.ActiveNavIndex = toSide;

        Check.True(Said(() => rig.Call("TickDunPatrolHazardReroute")).Count == 0 && ReferenceEquals(rig.S.CurrentRoute, before), "not stuck: nothing happens");
        bool stuck = true;
        rig.P.ExploreStuckProbe = () => stuck;
        said = Said(() => rig.Call("TickDunPatrolHazardReroute"));
        stuck = false;
        Check.True(said.Any(l => l.Contains($"DunNav-Patrol: couldn't get past something between cells {rig.D.E1:X8} and {rig.D.Side:X8}") && l.Contains("patrolling round it")),
                   "it says which opening it gives up: " + Show(said));
        Check.False(ReferenceEquals(rig.S.CurrentRoute, before), "the patrol laid again");
        Check.Eq(rig.S.CurrentRoute.RouteType, NavRouteType.Circular, "still a circular patrol");
        Check.False(rig.S.CurrentRoute.Points.Any(InSide), "and its lap keeps out of the side room");
        Check.True(rig.S.IsMacroRunning && rig.S.EnableNavigation, "still running");
        Check.True(Said(() => rig.Call("TickDunPatrolHazardReroute")).Count == 0, "unstuck: nothing more");

        rig.At(15, 15, rig.D.Start);
        rig.Typed("/ra dunnav-patrol");
        Check.False(rig.S.CurrentRoute.Points.Any(InSide), "started again in this dungeon: the opening stays closed");
    }

    // ── Things in doorways (2026-11-16) ──────────────────────────────────────
    // A web a server puts in a doorway is on no map (Thread 0x5456's are the cells' own: BlockedDoorwayTests). One named so
    // closes its doorway before the walk is laid; one in the open is left alone; one that comes into view
    // mid-walk gets the walk laid again; and the name of a thing in a doorway it got stuck in is learned.

    private const uint WebId = 0x80E0E001, OpenWebId = 0x80E0E002, LateWebId = 0x80E0E003, StrandsId = 0x80E0E004, Strands2Id = 0x80E0E005;

    private static void Place(DunRouteTests.Rig rig, uint id, string name, uint cell, float lx, float ly)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = 0x80;   // TYPE_MISC
        FakeHost.Positions[id] = (cell, lx, ly, 0f);
        rig.Cache.OnCreateObject(id);
        for (int i = 0; i < 3; i++) rig.Cache.Tick();
    }

    private static bool Logged(int from, string text) => FakeHost.Logs.Skip(from).Any(l => l.Contains(text));

    private static void PluginWebInDoorway()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore-web");
        var geo = rig.D.Geo();
        bool InSide(NavPoint p) => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side;
        Place(rig, WebId, "Spider Web", rig.D.E1, 39.6f, 5.2f);          // in the doorway e1 > side room (x 40)
        Place(rig, OpenWebId, "Spider Web", rig.D.Start, 15f, 15f);      // the hall's middle: no doorway there
        int logs = FakeHost.Logs.Count;
        var said = rig.Typed("/ra dunnav-explore");
        Check.True(said.Any(l => l.Contains($"Spider Web in the doorway between cells") && l.Contains("going round it")),
                   "it says which doorway the web closes: " + Show(said));
        Check.True(said.Any(l => l.Contains("exploring")), "and explores: " + Show(said));
        Check.False(rig.S.CurrentRoute.Points.Any(InSide), "the walk keeps out of the side room (only through the web's doorway)");
        Check.True(Logged(logs, $"0x{OpenWebId:X8} 'Spider Web'") && Logged(logs, "stands in no doorway: left alone"), "the web in the open is left alone");
        var run = Run(rig);
        Check.Eq(((Dictionary<uint, string>)run.GetType().GetField("Blockers")!.GetValue(run)!).Count, 1, "one web in a doorway");

        // A web that comes into view mid-walk, in the doorway hall > w1 (x 0): the walk is laid again round it.
        var first = rig.S.CurrentRoute;
        bool InWest(NavPoint p) { uint c = DungeonPathfinder.CellAt(rig.D.Graph, geo, p); return c == rig.D.W1 || c == rig.D.West; }
        Check.True(first.Points.Any(InWest), "the walk goes into the west rooms");
        Place(rig, LateWebId, "Spider Web", rig.D.Hall[0, 2], 0.4f, 25f);
        run.GetType().GetField("NextCheckAt")!.SetValue(run, 0L);
        said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("Spider Web in the doorway")), "the new web is said: " + Show(said));
        Check.False(ReferenceEquals(rig.S.CurrentRoute, first), "the walk laid again");
        Check.False(rig.S.CurrentRoute.Points.Any(InWest), "round the new web (the west rooms are only that way)");
        Check.Eq(St(rig).State, 1, "still exploring");
        Check.True(Said(() => { run.GetType().GetField("NextCheckAt")!.SetValue(run, 0L); Tick(rig); }).Count == 0, "the same webs again: nothing new");
    }

    private static void PluginStuckLearnsName()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore-strands");
        var geo = rig.D.Geo();
        // Not named like a web: not known to block until it got stuck on one.
        Place(rig, StrandsId, "Thick Strands", rig.D.E1, 39.6f, 5.2f);
        Place(rig, Strands2Id, "Thick Strands", rig.D.Hall[0, 2], 0.4f, 25f);
        rig.Typed("/ra dunnav-explore");
        StandAtDoorway(rig, rig.D.E1, rig.D.Side);
        Tick(rig);
        var before = rig.S.CurrentRoute;
        bool InWest(NavPoint p) { uint c = DungeonPathfinder.CellAt(rig.D.Graph, geo, p); return c == rig.D.W1 || c == rig.D.West; }
        Check.True(before.Points.Any(InWest), "at first the walk goes through both (no web by name)");
        int toSide = before.Points.FindIndex(p => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side);
        Check.True(toSide >= 0, "the walk goes into the side room");
        rig.S.ActiveNavIndex = toSide;

        bool stuck = true;
        rig.P.ExploreStuckProbe = () => stuck;
        int logs = FakeHost.Logs.Count;
        Tick(rig);
        stuck = false;
        Check.True(Logged(logs, $"in it: 0x{StrandsId:X8} 'Thick Strands'"), "the stuck line names what stands in the doorway");
        Check.True(Logged(logs, "'Thick Strands' stood in the doorway it couldn't get through"), "and learns its name");

        var run = Run(rig);
        run.GetType().GetField("NextCheckAt")!.SetValue(run, 0L);
        var said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("Thick Strands in the doorway between cells")), "the other one of that name closes its doorway: " + Show(said));
        Check.False(rig.S.CurrentRoute.Points.Any(InWest), "and the walk keeps out of the west rooms");
    }

    // ── Enter and scan (2026-11-16) ─────────────────────────────────────────
    // DreamWeave Thread 0x00B5: the boss stood in a 6-cell dead end; the walk touched its doorway and turned back, the
    // client had no position for the boss yet, and the explore went on without it. The walk now goes into every dead
    // end and waits at the middles it walks to; when the walk is over, a monster still known in the dungeon is walked to.

    private const uint LeftMobId = 0x80E0E101;

    private static void PluginLeftoverMonster()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore-leftover");
        rig.Typed("/ra dunnav-explore");
        Check.True(rig.S.CurrentRoute.Points.Any(p => p.Type == NavPointType.Pause && p.PauseTimeMs == DungeonPathfinder.ExploreScanMs),
                   "the walk waits at the room middles it walks to");
        foreach (uint c in rig.D.Graph.Keys.Where(c => c != rig.D.Nook)) { StandIn(rig, c); Tick(rig); }

        // A monster the walk went past, still in the west room when the walk is over.
        FakeHost.Names[LeftMobId] = "Dread Test Commander";
        FakeHost.ItemTypes[LeftMobId] = 0x10;
        FakeHost.Wcids[LeftMobId] = 99001;
        FakeHost.Positions[LeftMobId] = (rig.D.West, -18f, 25f, 0f);
        rig.Cache.OnCreateObject(LeftMobId);
        for (int i = 0; i < 3; i++) rig.Cache.Tick();
        StandIn(rig, rig.D.Start);
        FakeHost.Positions[DunRouteTests.Player] = FakeHost.PlayerPose;   // combat's hunt list reads the player's own position
        // Combat's hunt list (alive, attackable, on the monster list; the rig has no combat manager): the monster, while alive.
        bool alive = true;
        rig.P.LeftoverMonstersProbe = () => alive
            ? new List<OutdoorHunt.Candidate> { new(unchecked((int)LeftMobId), "Dread Test Commander", new OutdoorHunt.Vec(Ox - 18, Oy + 25, 0), 30, 1, rig.D.West) }
            : new List<OutdoorHunt.Candidate>();
        Finish(rig);
        var said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("Dread Test Commander is still in the dungeon") && l.Contains("going after it")),
                   "the walk over, it goes after the monster still here: " + Show(said));
        Check.Eq(St(rig).State, 1, "still exploring");
        var geo = rig.D.Geo();
        var pts = rig.S.CurrentRoute.Points;
        Check.True(rig.S.EnableNavigation && pts.Count > 0 && DungeonPathfinder.CellAt(rig.D.Graph, geo, pts[^1]) == rig.D.West,
                   "a walk to it, ending in the west room");

        // Walked there, the monster dead: nothing left, done.
        alive = false;
        StandIn(rig, rig.D.West);
        FakeHost.Positions[DunRouteTests.Player] = FakeHost.PlayerPose;
        Finish(rig);
        said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("DunNav-Explore: done,")), "then done: " + Show(said));
        Check.Eq(St(rig).State, 3, "done");
    }

    // On the real dats: the walk from where that run started goes to the middle of the boss's dead end and waits there.
    private static void Real00B5BossRoom()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x00B5;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            var route = DungeonPathfinder.BuildExploreRoute(graph, 0x00B501FF, null, geo, null, null, null, null, out var plan);
            int bossRoom = plan.RoomOf(0x00B5012A);
            var cells = plan.Rooms.Cells[bossRoom];
            var mid = DungeonPathfinder.RoomMiddle(graph, geo, cells, DungeonPathfinder.WallClearance, out _)!;
            var pts = route.Points;
            int at = pts.FindIndex(p => p.Type == NavPointType.Point && Math.Abs(p.NS - mid.NS) * 240 < 0.3 && Math.Abs(p.EW - mid.EW) * 240 < 0.3);
            Console.WriteLine($"    0x00B5: {pts.Count} points, {pts.Count(p => p.Type == NavPointType.Pause)} waits; the boss room ({cells.Count} cells) middle at [{at}]; {DungeonPathfinder.MainRouteNote}");
            DungeonPathfinder.MainRouteNote = null;
            Check.True(at >= 0, "the walk goes to the middle of the boss's dead end");
            Check.True(at >= 0 && at + 1 < pts.Count && pts[at + 1].Type == NavPointType.Pause, "and waits there");
        }
        finally
        {
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }

    // ── Shut doors and levers (2026-11-14) ───────────────────────────────────
    // A door in the opening between the hall and the west corridor (x 0, y 25) that can't be used (ItemUseable
    // No, as every lever door in ACE's data), a lever in the hall that opens it, a button in the hall that
    // opens nothing.

    private const uint DoorId = 0x7E7E9003, LeverId = 0x7E7E9004, ButtonId = 0x7E7E9005, FarLeverId = 0x7E7E9006;
    private const uint BfDoor = 0x1000, DoorClosed = 0x00010018, DoorEthereal = 0x4;

    // A Thread puzzle gate (DreamWeave wcid 1006850: a spawned door, locked until its levers' puzzle is solved) in the
    // doorway e1 > side room: a shut door to the walk. Its lever (wcid 1006851) is never pulled: three wrong pulls in an
    // hour ban the connection from Threads.
    private const uint PuzzleGateId = 0x80E0E201, PuzzleLeverId = 0x80E0E202;

    private static void PluginPuzzleGate()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-explore-puzzle", beforeCreate: () =>
        {
            FakeHost.Wcids[PuzzleGateId] = 1006850;
            FakeHost.Wcids[PuzzleLeverId] = 1006851;
            FakeHost.ObjectStates[PuzzleGateId] = DoorClosed;
        });
        FakeHost.Names[PuzzleGateId] = "Warded Gate";
        FakeHost.ItemTypes[PuzzleGateId] = 0x80;
        FakeHost.Positions[PuzzleGateId] = (rig.D.E1, 39.7f, 5f, 0f);
        FakeHost.Names[PuzzleLeverId] = "Puzzle Lever";
        FakeHost.ItemTypes[PuzzleLeverId] = 0x80;
        FakeHost.Positions[PuzzleLeverId] = (rig.D.E1, 35f, 5f, 0f);
        foreach (uint id in new[] { PuzzleGateId, PuzzleLeverId }) rig.Cache.OnCreateObject(id);
        for (int i = 0; i < 3; i++) rig.Cache.Tick();

        int logs = FakeHost.Logs.Count;
        rig.Typed("/ra dunnav-explore");
        Check.True(Logged(logs, $"shut door 0x{PuzzleGateId:X8} 'Warded Gate' (a Thread puzzle gate)"), "the puzzle gate is a shut door to the walk");
        var geo = rig.D.Geo();
        Check.False(rig.S.CurrentRoute.Points.Any(p => DungeonPathfinder.CellAt(rig.D.Graph, geo, p) == rig.D.Side), "the walk keeps out of the room behind it");

        int uses = FakeHost.Uses.Count;
        var said = rig.Typed($"/ra dunnav-explore lever {PuzzleLeverId:X8} door {PuzzleGateId:X8}");
        Check.True(said.Any(l => l.Contains("is a Thread puzzle lever: the explore never pulls one")), "a puzzle lever is refused: " + Show(said));
        Check.False(FakeHost.Uses.Skip(uses).Contains(PuzzleLeverId), "and never used");
        Check.True(Info(rig).GetProperty("last").GetString()!.StartsWith($"puzzle {PuzzleLeverId:X8}"), "puzzle, in radunexploreinfo[]");
    }

    private static DunRouteTests.Rig LeverRig(string folder, int doorUsable = 1)
    {
        // UseObject, the object bitfields and the physics states are the host's only when asked for at Create().
        var rig = new DunRouteTests.Rig(folder, beforeCreate: () =>
        {
            FakeHost.WeaponCalls = true;
            FakeHost.Bitfields[DoorId] = BfDoor;
            FakeHost.ObjectStates[DoorId] = DoorClosed;
        });
        FakeHost.Names[DoorId] = "Door";
        FakeHost.Positions[DoorId] = (rig.D.Hall[0, 2], 0.3f, 25f, 0f);
        FakeHost.Ints[(DoorId, 16u)] = doorUsable;
        FakeHost.Names[LeverId] = "Lever";
        FakeHost.Positions[LeverId] = (rig.D.Hall[0, 2], 5f, 25f, 0f);
        FakeHost.Names[ButtonId] = "Button";
        FakeHost.Positions[ButtonId] = (rig.D.Hall[2, 2], 25f, 25f, 0f);
        foreach (uint id in new[] { DoorId, LeverId, ButtonId }) rig.Cache.OnCreateObject(id);
        for (int i = 0; i < 3; i++) rig.Cache.Tick();
        return rig;
    }

    private static System.Text.Json.JsonElement Info(DunRouteTests.Rig rig)
        => System.Text.Json.JsonDocument.Parse((string)rig.Call("DunExploreInfoJson")!).RootElement.Clone();

    private static object Run(DunRouteTests.Rig rig) => rig.Get("_dunExplore")!;
    private static string Pts(IEnumerable<NavPoint> pts) => string.Join(" ", pts.Select(p => $"({DungeonGeometry.NavToWorld(p.EW) - Ox:F1},{DungeonGeometry.NavToWorld(p.NS) - Oy:F1})"));

    /// <summary>Stands in every cell but those given (and the nook), a tick in each.</summary>
    private static void WalkAllBut(DunRouteTests.Rig rig, params uint[] skip)
    {
        foreach (uint c in rig.D.Graph.Keys.Where(c => c != rig.D.Nook && !skip.Contains(c))) { StandIn(rig, c); Tick(rig); }
    }

    private static void PluginShutDoor()
    {
        var rig = LeverRig("NavProfiles-explore-door");
        var said = rig.Typed("/ra dunnav-explore");
        Check.True(said.Count == 1 && said[0].Contains("round 1 shut door(s)"), "it starts round the shut door: " + Show(said));
        var geo = rig.D.Geo();
        bool Behind(NavPoint p) { uint c = DungeonPathfinder.CellAt(rig.D.Graph, geo, p); return c == rig.D.W1 || c == rig.D.West; }
        Check.False(rig.S.CurrentRoute.Points.Any(Behind), "the walk doesn't go through it");
        var info = Info(rig);
        Check.Eq(info.GetProperty("state").GetInt32(), 1, "exploring");
        var door = info.GetProperty("doors")[0];
        Check.Eq(door.GetProperty("id").GetString(), "7E7E9003", "the door in radunexploreinfo[]");
        Check.Eq(door.GetProperty("why").GetString(), "can't be used", "why it's shut");
        Check.Eq(door.GetProperty("rooms").GetInt32(), 2, "two rooms behind it (the west corridor and room)");
        Check.Near(door.GetProperty("x").GetDouble(), 0.3, 0.01, "landblock-local x");

        WalkAllBut(rig, rig.D.W1, rig.D.West);
        Finish(rig);
        said = Said(() => Tick(rig));
        Check.True(said.Count == 1 && said[0].Contains("every room it can walk to is done") && said[0].Contains("behind 1 shut door(s): Door 0x7E7E9003 (2)"),
                   "the walk over: it waits at the door: " + Show(said));
        Check.Eq(St(rig).State, 5, "waiting at the doors");
        Check.True(!rig.S.EnableNavigation && rig.S.CurrentRoute.Points.Count == 0, "navigation off, an empty route of its own");
        Check.True(rig.S.IsMacroRunning, "the macro left on");
        Check.True(Said(() => Tick(rig)).Count == 0, "it waits quietly");
        said = rig.Typed("/ra dunnav-explore status");
        Check.True(said[0].Contains("waiting at shut doors") && said[0].Contains("2 room(s) behind 1 shut door(s)"), "status: " + Show(said));
    }

    private static void PluginLeverErrands()
    {
        var rig = LeverRig("NavProfiles-explore-lever");
        rig.Typed("/ra dunnav-explore");
        WalkAllBut(rig, rig.D.W1, rig.D.West);
        Finish(rig);
        Tick(rig);
        Check.Eq(St(rig).State, 5, "at the door");

        // A lever on the far side: no way there through the shut door.
        var said = rig.Typed("/ra dunnav-explore lever 7E7E9006 door 7E7E9003 at -17 25 0");
        Check.True(said.Any(l => l.Contains("can't be walked to from here")), "a lever behind the door: " + Show(said));
        Check.True(Info(rig).GetProperty("last").GetString()!.StartsWith("unreachable 7E7E9006 7E7E9003"), "unreachable, in radunexploreinfo[]");
        Check.Eq(St(rig).State, 5, "still at the door");

        // The button: walked to, used, nothing opens.
        int usesBefore = FakeHost.Uses.Count;
        said = rig.Typed("/ra dunnav-explore lever 7E7E9005 door 7E7E9003");
        Check.True(said.Count == 1 && said[0].Contains("going to pull Button 0x7E7E9005 for Door 0x7E7E9003 (2 room(s) behind it)"), "the errand starts: " + Show(said));
        Check.Eq(St(rig).State, 6, "at a lever");
        Check.True(rig.S.EnableNavigation && rig.S.CurrentRoute.RouteType == NavRouteType.Once && rig.S.CurrentRoute.Points.Count > 0, "walking to it");
        Check.Eq(Info(rig).GetProperty("errand").GetProperty("phase").GetString(), "walking", "walking, in radunexploreinfo[]");
        rig.At(24.5, 25, rig.D.Hall[2, 2]);
        Tick(rig);
        Check.True(FakeHost.Uses.Skip(usesBefore).Contains(ButtonId), "used when there");
        Check.False(rig.S.EnableNavigation, "navigation off while it watches");
        Check.Eq(Info(rig).GetProperty("errand").GetProperty("phase").GetString(), "waiting", "watching the door");
        var errand = Run(rig).GetType().GetField("Errand")!.GetValue(Run(rig))!;
        // One pull, then the door watched: a door shows open ~4.5 s after (Thread 0x5873, 2026-11-16), and a second
        // pull before then flips it shut. Not again at 3 s; again after 8 s; given up after 12 s.
        int pulls() => FakeHost.Uses.Skip(usesBefore).Count(u => u == ButtonId);
        errand.GetType().GetField("PhaseAt")!.SetValue(errand, Environment.TickCount64 - 3000);
        Tick(rig);
        Check.Eq(pulls(), 1, "not pulled again 3 s on (the door may still be opening)");
        errand.GetType().GetField("PhaseAt")!.SetValue(errand, Environment.TickCount64 - 9000);
        Tick(rig);
        Check.Eq(pulls(), 2, "pulled again 9 s on, the door still shut");
        errand.GetType().GetField("PhaseAt")!.SetValue(errand, Environment.TickCount64 - 13000);
        said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("Button 0x7E7E9005 didn't open door 0x7E7E9003")), "nothing opened: " + Show(said));
        Check.Eq(Info(rig).GetProperty("last").GetString(), "notopened 7E7E9005 7E7E9003", "notopened, in radunexploreinfo[]");
        Check.Eq(St(rig).State, 5, "back at the door");

        // The lever: used, the door opens, the walk goes through it first.
        said = rig.Typed("/ra dunnav-explore lever 7E7E9004 door 7E7E9003");
        Check.True(said[0].Contains("going to pull Lever 0x7E7E9004"), "the lever: " + Show(said));
        rig.At(5.5, 25, rig.D.Hall[0, 2]);
        Tick(rig);
        Check.True(FakeHost.Uses.Contains(LeverId), "pulled");
        FakeHost.ObjectStates[DoorId] = DoorClosed | DoorEthereal;
        said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("Lever 0x7E7E9004 opened Door 0x7E7E9003 → through it first (2 room(s) behind it)")), "opened: " + Show(said));
        Check.Eq(St(rig).State, 1, "exploring again");
        Check.Eq(Info(rig).GetProperty("last").GetString(), "opened 7E7E9003 7E7E9004", "opened, in radunexploreinfo[]");
        Check.Eq(Info(rig).GetProperty("doors").GetArrayLength(), 0, "no shut door left");
        var geo = rig.D.Geo();
        // Along its legs (the west corridor is one run between two openings in line: no point in it).
        var pts = rig.S.CurrentRoute.Points;
        var cells = new HashSet<uint>();
        for (int i = 0; i + 1 < pts.Count; i++)
            for (int k = 0; k <= 20; k++)
            {
                double f = k / 20.0;
                cells.Add(DungeonPathfinder.CellAt(rig.D.Graph, geo, new NavPoint { NS = pts[i].NS + (pts[i + 1].NS - pts[i].NS) * f, EW = pts[i].EW + (pts[i + 1].EW - pts[i].EW) * f }));
            }
        Check.True(cells.Contains(rig.D.W1) && cells.Contains(rig.D.West), "the walk goes through the door, the corridor and into the west room: " + Pts(pts));
        Check.True(rig.S.EnableNavigation && rig.S.IsMacroRunning, "walking");
        StandIn(rig, rig.D.W1); Tick(rig);
        StandIn(rig, rig.D.West); Tick(rig);
        Finish(rig);
        said = Said(() => Tick(rig));
        var s = St(rig);
        Check.True(said.Count == 1 && said[0].Contains($"done, {s.Rooms} of {s.Rooms} rooms") && !said[0].Contains("shut door"), "done, every room: " + Show(said));
    }

    private static void PluginDoorsMore()
    {
        // Done at the doors: the rooms behind them said.
        var rig = LeverRig("NavProfiles-explore-doors-done");
        rig.Typed("/ra dunnav-explore");
        WalkAllBut(rig, rig.D.W1, rig.D.West);
        Finish(rig);
        Tick(rig);
        var said = rig.Typed("/ra dunnav-explore done");
        Check.True(said.Count == 1 && said[0].Contains("2 more behind 1 shut door(s)"), "done at the doors: " + Show(said));
        Check.Eq(St(rig).State, 3, "done");
        said = rig.Typed("/ra dunnav-explore done");
        Check.True(said[0].Contains("none is"), "done again: nothing waits: " + Show(said));

        // A shut door that opens by itself while it waits: through it.
        rig = LeverRig("NavProfiles-explore-doors-self");
        rig.Typed("/ra dunnav-explore");
        WalkAllBut(rig, rig.D.W1, rig.D.West);
        Finish(rig);
        Tick(rig);
        FakeHost.ObjectStates[DoorId] = DoorClosed | DoorEthereal;
        Run(rig).GetType().GetField("NextCheckAt")!.SetValue(Run(rig), 0L);
        said = Said(() => Tick(rig));
        Check.True(said.Any(l => l.Contains("Door 0x7E7E9003 is open → through it first")), "an open door: " + Show(said));
        Check.Eq(St(rig).State, 1, "exploring again");

        // A door it could use, that the door opener gave up on (locked, no picks): shut from then on.
        rig = LeverRig("NavProfiles-explore-doors-locked", doorUsable: 0x20);
        said = rig.Typed("/ra dunnav-explore");
        Check.False(said[0].Contains("shut door"), "a door you can use isn't shut: " + Show(said));
        rig.Call("NoteExploreDoorRefused", DoorId, "locked");
        Tick(rig);
        var info = Info(rig);
        Check.True(info.GetProperty("doors").GetArrayLength() == 1 && info.GetProperty("doors")[0].GetProperty("why").GetString() == "locked",
                   "the door opener gave up on it: shut (locked): " + info.GetRawText());

        // Resume: stopped, then carried on with the rooms entered kept.
        int entered = St(rig).Visited;
        StandIn(rig, rig.D.E1); Tick(rig);
        rig.Typed("/ra dunnav-explore stop");
        Check.Eq(St(rig).State, 4, "stopped");
        said = rig.Typed("/ra dunnav-explore resume");
        Check.True(said.Count == 1 && said[0].Contains("exploring again"), "resumed: " + Show(said));
        Check.True(St(rig).State == 1 && St(rig).Visited >= entered + 1, "the rooms entered kept");
        said = rig.Typed("/ra dunnav-explore resume");
        Check.True(said[0].Contains("already exploring"), "resume while exploring: " + Show(said));
    }

    private static void Expressions()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var m = new MetaManager(new LegacyUiSettings(), host, new PlayerVitalsCache());
        Check.Eq(m.Expressions.Evaluate("radunexplore[]"), "0", "not wired: 0");
        Check.Eq(m.Expressions.Evaluate("radunexplorerooms[]"), "0", "rooms 0");
        m.SetDunExploreStatus(() => (1, 12, 5));
        Check.Eq(m.Expressions.Evaluate("radunexplore[]"), "1", "exploring");
        Check.Eq(m.Expressions.Evaluate("radunexplorerooms[]"), "12", "rooms");
        Check.Eq(m.Expressions.Evaluate("radunexplorevisited[]"), "5", "entered");
    }

    private static void TinyHiveRealDats()
    {
        if (!Directory.Exists(DunRouteTests.AcDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(DunRouteTests.AcDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x0289, start = 0x028902A5, nook = 0x028902BA;
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
            Check.True(graph.Count > 400 && geo != null, $"Tiny Hive loaded ({graph.Count} cells)");
            var s = graph[start];
            var p = new PatrolPortal { Id = 0x7028905D, Name = "Surface Portal", Cell = nook, X = 2 * 192 + 110, Y = 0x89 * 192 - 3.77927, Z = -23.995 };
            var at = (DungeonGeometry.NavToWorld(s.EW), DungeonGeometry.NavToWorld(s.NS), (double)s.Z);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var route = DungeonPathfinder.BuildExploreRoute(graph, start, null, geo, null, new List<PatrolPortal> { p }, at, null, out var plan);
            double ms = clock.Elapsed.TotalMilliseconds;
            var walked = RoomsWalked(route.Points, graph, geo, plan);
            var missed = plan.PlanRooms.Where(r => !walked.Contains(r)).ToList();
            double yards = 0;
            for (int i = 0; i + 1 < route.Points.Count; i++)
                yards += Math.Sqrt(Math.Pow(route.Points[i + 1].NS - route.Points[i].NS, 2) + Math.Pow(route.Points[i + 1].EW - route.Points[i].EW, 2)) * 240.0;
            var lap = DungeonPathfinder.BuildPatrolRoute(graph, start, null, geo, new DungeonPatrolOptions { Coverage = PatrolCoverage.Everything }, new List<PatrolPortal> { p }, at);
            double lapYards = 0;
            for (int i = 0; i < lap.Points.Count; i++)
            {
                var a = lap.Points[i]; var b = lap.Points[(i + 1) % lap.Points.Count];
                lapYards += Math.Sqrt(Math.Pow(b.NS - a.NS, 2) + Math.Pow(b.EW - a.EW, 2)) * 240.0;
            }
            Console.WriteLine($"    explore: {plan.PlanRooms.Count} rooms, {route.Points.Count} points, {yards:F0} yd, laid in {ms:F0} ms; "
                              + $"the patrol's Everything lap: {lap.Points.Count} points, {lapYards:F0} yd; rooms the legs miss: {missed.Count}");
            Check.True(plan.ByRoom && plan.PlanRooms.Count > 10, $"rooms ({plan.PlanRooms.Count})");
            Check.Eq(plan.Order.Count, plan.PlanRooms.Count - 1, "each room but the start in the order");
            Check.True(missed.Count == 0, "every room walked into; missed: " + string.Join(",", missed.Select(r => plan.Rooms.Cells[r][0].ToString("X8"))));
            double near = DungeonPathfinder.NearestApproach(route.Points, closed: false, p);
            Check.True(near >= p.Radius - 0.05, $"keeps out of the Surface Portal ({near:F1} m)");
        }
        finally { DungeonPathfinder.InvalidateCache(); }
    }
}
