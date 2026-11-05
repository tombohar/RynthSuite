using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;

namespace RynthCore.RynthAiHostTests.Tests;

// The Dungeon Patrol keeps out of portals (DungeonPathfinder.Portals.cs, 2026-10-23). Tom, Tiny Hive
// (0x0289): with coverage Everything the patrol "takes me right into the portal": waypoint 1 was the
// exit point 1.2 m into the dead-end nook 028902BA, 0.02 m from the Surface Portal, and a client that
// began its lap there was teleported out. On small hand-built dungeons (world metres, floors at z 0,
// as in PatrolDoorwayTests):
//   - a patrol across a room with a portal in it: every point and leg stays outside the circle, the
//     crossing bends round it, and every doorway is still walked through;
//   - a portal at the back of a dead-end nook (Tiny Hive's layout): the nook's doorway is closed, the
//     patrol turns round in the corridor before it (without the portal a point was on it); the cell
//     walk without map geometry keeps out too;
//   - a start next to a portal: the character walks straight away from it first, then round it to the
//     first point; a portal across the start's corridor leaves the patrol the side the character is on;
//   - a portal on another level changes nothing;
//   - Tiny Hive itself on the real dats (when they are on this PC);
//   - the plugin: the client's portal objects in the dungeon are the circles, and one that comes into
//     view after the build makes the running patrol rebuild.
internal static class PatrolPortalTests
{
    public static void Register(Runner r)
    {
        r.Add("patrol portals: a patrol through a room with a portal keeps its points and legs outside the circle, doorways all walked", RoomWithPortal);
        r.Add("patrol portals: a portal in a dead end (Tiny Hive's nook) is turned round before; it used to get a point on it", DeadEndTurnedRoundBefore);
        r.Add("patrol portals: without the map geometry (cell walk) the patrol keeps out too", CellWalkKeepsOut);
        r.Add("patrol portals: a start next to a portal walks straight away from it first, then round it", StartNextToPortalWalksAway);
        r.Add("patrol portals: a portal across the start's corridor leaves the patrol the side the character is on", PortalSplitsStartCorridor);
        r.Add("patrol portals: a portal on another level changes nothing", OtherLevelIgnored);
        r.Add("patrol portals: Tiny Hive 0x0289 on the real dats keeps out of the Surface Portal (if the dats are here)", TinyHiveRealDats);
        r.Add("patrol portals: the client's portals in the dungeon are the circles; one coming into view rebuilds the patrol", PluginCollectsPortals);
    }

    // ── Fixture builder ──────────────────────────────────────────────────────

    private sealed class Dungeon
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public readonly List<(uint, V3[])> Portals = new();
        public readonly List<(uint, V3[])> Floors = new();
        public readonly List<(uint, V3[])> Walls = new();
        private uint _next = 0x7E7E0100;

        /// <summary>A cell whose floor is the rectangle (x0,y0)-(x1,y1) at height z; its origin is the middle.</summary>
        public uint Cell(double x0, double y0, double x1, double y1, double z = 0)
        {
            uint id = _next++;
            Graph[id] = new DungeonNavNode
            {
                CellId = id,
                EW = DungeonGeometry.WorldToNav((x0 + x1) / 2),
                NS = DungeonGeometry.WorldToNav((y0 + y1) / 2),
                Z = (float)z,
            };
            Floors.Add((id, Rect(x0, y0, x1, y1, z)));
            return id;
        }

        public void OpenX(uint a, uint b, double x, double y0, double y1) => Open(a, b, Quad(x, y0, x, y1));
        public void OpenY(uint a, uint b, double y, double x0, double x1) => Open(a, b, Quad(x0, y, x1, y));

        private void Open(uint a, uint b, V3[] quad)
        {
            Graph[a].Neighbors.Add(b);
            Graph[b].Neighbors.Add(a);
            Portals.Add((a, quad));
            Portals.Add((b, quad.Reverse().ToArray()));
        }

        public DungeonGeometry Geo() => DungeonGeometry.Build(Graph, Portals, Floors, Walls);
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

    private static double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
    private static double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);
    private static NavPoint At(double x, double y, double z = 0) => new()
    {
        Type = NavPointType.Point, EW = DungeonGeometry.WorldToNav(x), NS = DungeonGeometry.WorldToNav(y), Z = z / 240.0,
    };
    private static PatrolPortal Portal(double x, double y, double z = 0, double r = DungeonPathfinder.PortalKeepOutDefault)
        => new() { Id = 0x7E7E9001, Name = "Test Portal", X = x, Y = y, Z = z, Radius = r };
    private static string Pts(List<NavPoint> pts) => string.Join(" ", pts.Select(p => $"({X(p):F1},{Y(p):F1})"));

    private static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        double cx = ax + dx * t - px, cy = ay + dy * t - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    // The nearest any point, and any leg of the loop, comes to (px, py): checked here independently of
    // the code under test.
    private static (double Point, double Leg) Nearest(List<NavPoint> pts, double px, double py, bool closed = true)
    {
        double point = double.MaxValue, leg = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            point = Math.Min(point, Math.Sqrt((X(pts[i]) - px) * (X(pts[i]) - px) + (Y(pts[i]) - py) * (Y(pts[i]) - py)));
            if (i + 1 < pts.Count || (closed && pts.Count > 1))
            {
                var b = pts[(i + 1) % pts.Count];
                leg = Math.Min(leg, SegDist(px, py, X(pts[i]), Y(pts[i]), X(b), Y(b)));
            }
        }
        return (point, leg);
    }

    private static void KeptOut(NavRouteParser route, PatrolPortal p, string what)
    {
        var (point, leg) = Nearest(route.Points, p.X, p.Y);
        Check.True(route.Points.Count >= 2, $"{what}: a patrol ({route.Points.Count} points)");
        Check.True(point >= p.Radius - 0.02, $"{what}: every point {p.Radius} m or more from the portal (nearest {point:F2} m): {Pts(route.Points)}");
        Check.True(leg >= p.Radius - 0.02, $"{what}: every leg {p.Radius} m or more from the portal (nearest {leg:F2} m)");
    }

    // ── A room with a portal in it ───────────────────────────────────────────

    // PatrolDoorwayTests' loop dungeon: a 3x3 room of 10 m cells (x 0..30, y 0..30, wide openings
    // inside) entered by a doorway on its west side (y 25) and left by one on its east side (y 5),
    // and a corridor loop round its north back to the west corridor. The crossing from door to door
    // runs straight through the room's middle.
    private static (Dungeon D, uint West) LoopDungeon()
    {
        var d = new Dungeon();
        var c = new uint[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = d.Cell(i * 10, j * 10, i * 10 + 10, j * 10 + 10);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                if (i < 2) d.OpenX(c[i, j], c[i + 1, j], i * 10 + 10, j * 10, j * 10 + 10);
                if (j < 2) d.OpenY(c[i, j], c[i, j + 1], j * 10 + 10, i * 10, i * 10 + 10);
            }
        uint west = d.Cell(-10, 22.35, 0, 27.65);
        uint east = d.Cell(30, 2.35, 40, 7.65);
        d.OpenX(west, c[0, 2], 0, 22.35, 27.65);
        d.OpenX(c[2, 0], east, 30, 2.35, 7.65);
        uint up1 = d.Cell(-10, 27.65, -4.7, 37.65);
        d.OpenY(west, up1, 27.65, -10, -4.7);
        uint top1 = d.Cell(-10, 37.65, 10, 42.95);
        d.OpenY(up1, top1, 37.65, -10, -4.7);
        uint top2 = d.Cell(10, 37.65, 30, 42.95);
        d.OpenX(top1, top2, 10, 37.65, 42.95);
        uint top3 = d.Cell(30, 37.65, 45.3, 42.95);
        d.OpenX(top2, top3, 30, 37.65, 42.95);
        uint down1 = d.Cell(40, 7.65, 45.3, 37.65);
        d.OpenY(down1, top3, 37.65, 40, 45.3);
        uint corner = d.Cell(40, 2.35, 45.3, 7.65);
        d.OpenY(corner, down1, 7.65, 40, 45.3);
        d.OpenX(east, corner, 40, 2.35, 7.65);
        return (d, west);
    }

    private static void RoomWithPortal()
    {
        var (d, west) = LoopDungeon();
        var geo = d.Geo();
        var p = Portal(15, 15);

        var before = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo);
        Check.True(Nearest(before.Points, p.X, p.Y).Leg < 1.0, $"without the portal the crossing runs through the room's middle ({Nearest(before.Points, p.X, p.Y).Leg:F2} m)");

        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, null, new[] { p });
        KeptOut(route, p, "portal in the room");
        var report = DungeonPathfinder.LastPortalAvoidance;
        Check.True(report != null && report.Count == 1, "one line for the one portal");
        Check.True(report![0].Bent >= 1, $"the crossing is bent round it ({report[0].Describe()})");
        Check.True(report[0].Closed.Count == 0 && report[0].CutCells.Count == 0, "nothing closed: the room is wide enough to go round");
        Check.True(report[0].Clear, "the report says clear: " + report[0].Describe());

        // Every doorway (narrow opening) still passed through near its centre.
        foreach (var (a, b) in d.Graph.Values.SelectMany(n => n.Neighbors.Select(m => (n.CellId, m))).Where(e => e.CellId < e.m))
        {
            if (!geo.TryGetDoorway(a, b, out var door) || door.IsWide) continue;
            double best = double.MaxValue;
            for (int i = 0; i < route.Points.Count; i++)
            {
                var q = route.Points[i]; var s = route.Points[(i + 1) % route.Points.Count];
                best = Math.Min(best, SegDist(door.X, door.Y, X(q), Y(q), X(s), Y(s)));
            }
            Check.True(best < 0.3, $"still through the opening at ({door.X:F1}, {door.Y:F1}) ({best:F2} m off its centre)");
        }
        // The corners round it are on the room's floor.
        foreach (var q in route.Points)
            Check.True(geo.OnWalkableFloor(X(q), Y(q), q.Z * 240.0), $"({X(q):F1}, {Y(q):F1}) is on walkable floor");

        // A bigger keep-out (a longer nav point reach) keeps further out.
        var far = Portal(15, 15, 0, DungeonPathfinder.PortalKeepOutRadius(3.0));
        Check.Near(far.Radius, 5.0, 1e-9, "reach 3 yd: keep-out 1.9 + 3.1 m");
        KeptOut(DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, null, new[] { far }), far, "a 6 m keep-out");
    }

    // ── A portal at the back of a dead-end nook (Tiny Hive) ──────────────────

    // A 3x3 room of 10 m cells (x 0..30, y -30..0); north of its middle the start cell, a corridor piece
    // 5.3 m wide (y 0..10, Tiny Hive's 028902A5), then another (y 10..20, the ramp 028902BB) and a nook
    // 4 m deep (y 20..24, 028902BA) with the portal 1.5 m in.
    private static (Dungeon D, uint Start, uint Corridor, uint Nook, PatrolPortal P) NookDungeon()
    {
        var d = new Dungeon();
        var c = new uint[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = d.Cell(i * 10, -30 + j * 10, i * 10 + 10, -20 + j * 10);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                if (i < 2) d.OpenX(c[i, j], c[i + 1, j], i * 10 + 10, -30 + j * 10, -20 + j * 10);
                if (j < 2) d.OpenY(c[i, j], c[i, j + 1], -20 + j * 10, i * 10, i * 10 + 10);
            }
        uint start = d.Cell(12.35, 0, 17.65, 10);
        uint corridor = d.Cell(12.35, 10, 17.65, 20);
        uint nook = d.Cell(12.35, 20, 17.65, 24);
        d.OpenY(c[1, 2], start, 0, 12.35, 17.65);
        d.OpenY(start, corridor, 10, 12.35, 17.65);
        d.OpenY(corridor, nook, 20, 12.35, 17.65);
        return (d, start, corridor, nook, Portal(15, 21.5));
    }

    private static readonly DungeonPatrolOptions Everything = new() { Coverage = PatrolCoverage.Everything, LookInRoomMeters = 20 };

    private static void DeadEndTurnedRoundBefore()
    {
        var (d, start, corridor, nook, p) = NookDungeon();
        var geo = d.Geo();

        var before = DungeonPathfinder.BuildPatrolRoute(d.Graph, start, null, geo, Everything);
        Check.True(Nearest(before.Points, p.X, p.Y).Point < DungeonPathfinder.PortalTouchMeters,
                   $"without the portal a point is within touching distance of it ({Nearest(before.Points, p.X, p.Y).Point:F2} m): {Pts(before.Points)}");

        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, start, null, geo, Everything, new[] { p });
        KeptOut(route, p, "portal in the nook");
        var r = DungeonPathfinder.LastPortalAvoidance![0];
        Check.True(r.Closed.Any(e => (e.A == corridor && e.B == nook) || (e.A == nook && e.B == corridor)),
                   "the nook's doorway is closed: " + r.Describe());
        double north = route.Points.Max(Y);
        Check.True(north > 10 && north < p.Y - p.Radius,
                   $"it turns round in the corridor before the portal (northmost point y {north:F1}; corridor 10..20, portal at {p.Y})");
        Check.True(r.Nearest < p.Radius + 4.0, $"close up to it, not a whole cell short ({r.Nearest:F1} m)");
        Check.True(route.Points.Any(q => Y(q) < -5), "the room south of the start is still walked");
    }

    private static void CellWalkKeepsOut()
    {
        // Without the map geometry (DungeonLOS not up yet) the old cell walk: points 30% / 50% of the
        // way between cell centres. A portal 1.5 m past the start's north opening.
        var (d, start, corridor, _, _) = NookDungeon();
        var p = Portal(15, 11.5);
        var before = DungeonPathfinder.BuildPatrolRoute(d.Graph, start, null, null, Everything);
        Check.True(Nearest(before.Points, p.X, p.Y).Point < DungeonPathfinder.PortalTouchMeters,
                   $"the cell walk turned round within touching distance of it ({Nearest(before.Points, p.X, p.Y).Point:F2} m): {Pts(before.Points)}");
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, start, null, null, Everything, new[] { p });
        KeptOut(route, p, "cell walk, portal past the start's opening");
        var r = DungeonPathfinder.LastPortalAvoidance![0];
        Check.True(r.Closed.Contains((start, corridor)) || r.Closed.Contains((corridor, start)), "the opening by it is closed: " + r.Describe());
    }

    // ── A start next to a portal ─────────────────────────────────────────────

    private static void StartNextToPortalWalksAway()
    {
        // An open room (x 0..20, y 0..20) with doorways west and east (y 10) and a loop round its
        // north; the portal in its middle, the character 2 m north of it (inside the circle).
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 20, 20);
        uint w = d.Cell(-10, 7.35, 0, 12.65), e = d.Cell(20, 7.35, 30, 12.65);
        d.OpenX(w, room, 0, 7.35, 12.65);
        d.OpenX(room, e, 20, 7.35, 12.65);
        uint wn = d.Cell(-10, 12.65, -4.7, 30), top = d.Cell(-10, 30, 30, 35.3), en = d.Cell(24.7, 12.65, 30, 30);
        d.OpenY(w, wn, 12.65, -10, -4.7);
        d.OpenY(wn, top, 30, -10, -4.7);
        d.OpenY(en, top, 30, 24.7, 30);
        d.OpenY(e, en, 12.65, 24.7, 30);
        var geo = d.Geo();
        var p = Portal(10, 10);
        double px = 10, py = 12;
        var portals = new[] { p };

        var walk = DungeonPathfinder.PortalWalkOut(geo, portals, px, py, 0, 1.0);
        Check.NotNull(walk, "standing inside the circle: a spot to walk out to");
        double wd = Math.Sqrt((X(walk!) - p.X) * (X(walk) - p.X) + (Y(walk) - p.Y) * (Y(walk) - p.Y));
        Check.True(wd >= p.Radius, $"the spot is outside the circle ({wd:F2} m)");
        Check.True((X(walk) - px) * (px - p.X) + (Y(walk) - py) * (py - p.Y) > 0, $"straight away from the portal: ({X(walk):F1}, {Y(walk):F1})");
        // The walk there never gets nearer the portal than the character already is.
        for (int i = 0; i <= 20; i++)
        {
            double t = i / 20.0, x = px + (X(walk) - px) * t, y = py + (Y(walk) - py) * t;
            Check.True(Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y)) >= 2.0 - 1e-6, $"walking out, {t:F2} of the way: no nearer the portal");
        }
        Check.Null(DungeonPathfinder.PortalWalkOut(geo, portals, 3, 3, 0, 1.0), "outside the circle: nothing to walk out of");

        // The patrol from there keeps out, and the way from the walk-out spot to a first point on the
        // portal's other side (south) goes round it.
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, room, null, geo, null, portals, (X(walk), Y(walk), 0));
        KeptOut(route, p, "start next to a portal");
        var first = At(10, 3);
        var lead = DungeonPathfinder.PortalLeadIn(d.Graph, geo, portals, walk, first, 1.0);
        Check.True(lead.Count >= 1, "corners round the portal to the first point");
        var way = new List<NavPoint> { walk };
        way.AddRange(lead);
        way.Add(first);
        var (_, leg) = Nearest(way, p.X, p.Y, closed: false);
        Check.True(leg >= p.Radius - 0.02, $"the way to the first point keeps out ({leg:F2} m): {Pts(way)}");
        Check.True(DungeonPathfinder.PortalLeadIn(d.Graph, geo, portals, At(3, 3), At(3, 17), 1.0).Count == 0, "a clear walk needs no corners");
    }

    private static void PortalSplitsStartCorridor()
    {
        // A corridor (x 0..5.3, y 0..20) between a room south (y -10..0) and a room north (y 20..30),
        // nothing else joining them; the portal across the corridor's middle.
        var d = new Dungeon();
        uint south = d.Cell(-5, -10, 10.3, 0), corr = d.Cell(0, 0, 5.3, 20), north = d.Cell(-5, 20, 10.3, 30);
        uint sw = d.Cell(-15, -7.65, -5, -2.35), nw = d.Cell(-15, 22.35, -5, 27.65);
        d.OpenY(south, corr, 0, 0, 5.3);
        d.OpenY(corr, north, 20, 0, 5.3);
        d.OpenX(sw, south, -5, -7.65, -2.35);
        d.OpenX(nw, north, -5, 22.35, 27.65);
        var geo = d.Geo();
        var p = Portal(2.65, 10);
        var portals = new[] { p };

        var n = DungeonPathfinder.BuildPatrolRoute(d.Graph, corr, null, geo, Everything, portals, (2.65, 16, 0));
        KeptOut(n, p, "character north of the portal");
        Check.True(n.Points.All(q => Y(q) > p.Y), $"only the north side: {Pts(n.Points)}");
        Check.True(n.Points.Any(q => Y(q) > 20), "the room north is walked");

        var s = DungeonPathfinder.BuildPatrolRoute(d.Graph, corr, null, geo, Everything, portals, (2.65, 4, 0));
        KeptOut(s, p, "character south of the portal");
        Check.True(s.Points.All(q => Y(q) < p.Y), $"only the south side: {Pts(s.Points)}");
        Check.True(s.Points.Any(q => Y(q) < 0), "the room south is walked");
    }

    private static void OtherLevelIgnored()
    {
        var (d, west) = LoopDungeon();
        var geo = d.Geo();
        var plain = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo);
        var up = Portal(15, 15, 6.0);   // a floor 6 m above
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, null, new[] { up });
        Check.Eq(Pts(route.Points), Pts(plain.Points), "the same patrol as without it");
        var r = DungeonPathfinder.LastPortalAvoidance![0];
        Check.True(double.IsPositiveInfinity(r.Nearest) && r.Closed.Count == 0 && r.Bent == 0, "nothing done about it: " + r.Describe());
    }

    // ── Tiny Hive on the real dats ───────────────────────────────────────────

    private const string AcDir = @"C:\Turbine\Asheron's Call";

    private static void TinyHiveRealDats()
    {
        if (!Directory.Exists(AcDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(AcDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x0289, start = 0x028902A5, nook = 0x028902BA, ramp = 0x028902BB;
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
            Check.True(graph.Count > 400 && geo != null, $"Tiny Hive loaded ({graph.Count} cells)");
            // +Buffy's settings on 2026-10-23: coverage Everything, floors walked, rooms looked into from 20 m.
            var opt = new DungeonPatrolOptions { Coverage = PatrolCoverage.Everything, AvoidHazardFloors = false, LookInRoomMeters = 20 };
            // The Surface Portal (weenie 11218, 0x7028905D): cell 028902BA, local (110, -3.77927, -23.995).
            var p = new PatrolPortal { Id = 0x7028905D, Name = "Surface Portal", Cell = nook, X = 2 * 192 + 110, Y = 0x89 * 192 - 3.77927, Z = -23.995 };

            var before = DungeonPathfinder.BuildPatrolRoute(graph, start, null, geo, opt);
            Check.Eq(before.Points.Count, 355, "the 355-point patrol of the report");
            Check.True(Nearest(before.Points, p.X, p.Y).Point < 0.1, $"its waypoint 1 sat on the portal ({Nearest(before.Points, p.X, p.Y).Point:F2} m)");

            var route = DungeonPathfinder.BuildPatrolRoute(graph, start, null, geo, opt, new[] { p });
            KeptOut(route, p, "Tiny Hive");
            var r = DungeonPathfinder.LastPortalAvoidance![0];
            Check.True(r.Closed.Contains((nook, ramp)) || r.Closed.Contains((ramp, nook)), "the nook's doorway closed: " + r.Describe());
            Check.True(route.Points.Count > 300, $"the rest of the dungeon still patrolled ({route.Points.Count} points)");
            Console.WriteLine("    " + r.Describe());
        }
        finally { DungeonPathfinder.InvalidateCache(); }
    }

    // ── The plugin side ──────────────────────────────────────────────────────

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const uint Player = 0x50000001;

    private static void PluginCollectsPortals()
    {
        FakeHost.Reset();
        const uint surface = 0x7028905D, other = 0x7028905E, lamp = 0x70289060, outside = 0x7029A001;
        FakeHost.Names[Player] = "Tester";
        FakeHost.Positions[Player] = (0x028902A5, 110f, 92f, -30f);
        FakeHost.PlayerPose = (0x028902A5, 110f, 92f, -30f);
        FakeHost.Names[surface] = "Surface Portal";
        FakeHost.ItemTypes[surface] = 0x00010000;
        FakeHost.Positions[surface] = (0x028902BA, 110f, -3.77927f, -23.995f);
        FakeHost.Names[lamp] = "Lamp";
        FakeHost.ItemTypes[lamp] = 0x00000080;
        FakeHost.Positions[lamp] = (0x028902BA, 111f, -3f, -24f);
        FakeHost.Names[outside] = "Tiny Hive Portal";
        FakeHost.ItemTypes[outside] = 0x00010000;
        FakeHost.Positions[outside] = (0x24BD002F, 50f, 50f, 0f);
        var host = FakeHost.Create(Player);

        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(plugin, new object[] { FakeHost.LastApi });
        var settings = new LegacyUiSettings();
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, settings);
        void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(plugin, v);
        object? Call(string m, params object?[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(plugin, a);
        Set("_dashboard", dash);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        Set("_objectCache", cache);
        foreach (uint id in new[] { surface, lamp, outside }) cache.OnCreateObject(id);
        cache.Tick();

        var portals = (List<PatrolPortal>)Call("PatrolPortalsIn", 0x0289u)!;
        Check.Eq(portals.Count, 1, "one portal in Tiny Hive (not the lamp, not the one outside)");
        var p = portals[0];
        Check.Eq(p.Id, surface, "the Surface Portal");
        Check.Eq(p.Cell, 0x028902BAu, "its cell");
        Check.Near(p.X, 494.0, 1e-3, "world x = 2 x 192 + 110");
        Check.Near(p.Y, 26300.22, 1e-2, "world y = 0x89 x 192 - 3.78");
        Check.Near(p.Z, -23.995, 1e-3, "its height");
        Check.Near(p.Radius, DungeonPathfinder.PortalKeepOutDefault, 1e-9, "keep-out 3.5 m at the default nav point reach");
        settings.FollowNavMin = 3.0f;
        Check.True(((List<PatrolPortal>)Call("PatrolPortalsIn", 0x0289u)!)[0].Radius > 5.5, "a 3 yd reach keeps further out");
        settings.FollowNavMin = 1.5f;

        // Built round it: no rebuild. Another one comes into view: rebuild.
        Set("_dunPatrolLandblock", 0x0289u);
        Call("MarkPatrolPortals", portals);
        Set("_dunPatrolPortalCheckAt", 0L);
        Check.False((bool)Call("PatrolPortalAppeared")!, "the portals it was built round: nothing to do");
        FakeHost.Names[other] = "Portal to the Hive Depths";
        FakeHost.ItemTypes[other] = 0x00010000;
        FakeHost.Positions[other] = (0x02890270, 100f, 50f, -36f);
        cache.OnCreateObject(other);
        cache.Tick();
        Set("_dunPatrolPortalCheckAt", 0L);
        Check.True((bool)Call("PatrolPortalAppeared")!, "a new portal in the dungeon: rebuild");
        Check.True(FakeHost.Logs.Any(l => l.Contains("came into view") && l.Contains("0x7028905E")), "and it says which");
        Check.False((bool)Call("PatrolPortalAppeared")!, "checked at most once a second");
    }
}
