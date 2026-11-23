// ============================================================================
//  NavRecovery.cs — the planning half of nav stuck / off-track recovery.
//
//  NavigationEngine decides WHEN to recover (stuck watchdog, off-track check)
//  and walks the result. This class answers two questions for it:
//
//   1. TryPlan: a walkable way from the character to a route waypoint.
//        Indoors (the character is in an EnvCell and the waypoint is in the same
//        cell graph): DungeonPathfinder A* over the EnvCell portal graph, laid out
//        by DungeonPathfinder.BuildPathPoints: approach and exit points centred on
//        the real doorway openings (DungeonGeometry), rooms crossed directly.
//        Outdoors: RynthNav's navmesh through the "RynthNav.Path" plugin interface
//        (Shared/RynthNavPathApi.cs), resolved again on every call.
//        Neither: no plan, and the engine falls back to the jump / side-step ladder.
//
//   2. CheckArea: is the route waypoint in a different area from the character
//        (a dungeon vs the landscape, or another dungeon)? No pathfinder can join
//        those, so the engine holds navigation instead of running the ladder.
//
//  Units: nav coordinates (NS/EW, 1.0 = 240 world units) and world units, which
//  the rest of the nav code calls yards. Everything runs on the plugin pump thread.
//  All caches are instance fields; the engine drops this object at session teardown.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>What a recovery plan looks like to the engine.</summary>
internal sealed class NavRecoveryPlan
{
    /// <summary>Detour points to walk, in order. The route waypoint itself is not included.</summary>
    public List<NavPoint> Points = new();
    /// <summary>Straight-line length of player → points → route waypoint, in yards.</summary>
    public double LengthYd;
    /// <summary>"dungeon map" or "RynthNav navmesh".</summary>
    public string Planner = string.Empty;
    /// <summary>Doorways (dungeon portals) the path goes through.</summary>
    public int Doorways;
    /// <summary>The points are RynthNav navmesh corners: the engine turns a corner early only where the
    /// navmesh shows a straight walk to the next one (NavigationEngine's corner gate).</summary>
    public bool NavmeshCorners;
    /// <summary>The route waypoint stands inside something placed (a statue's middle): the last point is the nearest
    /// spot clear of it, and reaching that reaches the waypoint (nobody gets nearer).</summary>
    public bool TargetMoved;
}

/// <summary>Result of <see cref="NavRecoveryPlanner.CheckArea"/>.</summary>
internal enum NavAreaCheck
{
    /// <summary>Same area, or nothing contradicts it.</summary>
    Match,
    /// <summary>Can't tell yet (dats not loaded, no pose). Treated as a match.</summary>
    Unknown,
    /// <summary>The waypoint is in another dungeon, or dungeon vs landscape.</summary>
    Mismatch,
}

/// <summary>The two questions NavigationEngine asks a planner (lets tests supply a fake).</summary>
internal interface INavRecoveryPlanner
{
    NavRecoveryPlan? TryPlan(NavPoint target, out string why);
    NavAreaCheck CheckArea(uint playerCell, double tNs, double tEw, out string routeArea, out string playerArea);
}

internal sealed class NavRecoveryPlanner : INavRecoveryPlanner
{
    // A dungeon waypoint is always inside some cell; its distance to that cell's centre
    // is at most about half the biggest room. Used to decide "this waypoint is in that
    // cell graph" for both planning and the area check.
    private const double DungeonCellReachYd  = 30.0;
    // Building interiors on the landscape are small; a waypoint farther than this from
    // every building cell is outdoors, not inside.
    private const double BuildingCellReachYd = 8.0;
    // Area check: dungeon cells can sit outside their landblock's 192-unit square, so a
    // waypoint that maps to a neighbouring landblock still counts as "in this dungeon"
    // when it is this close to one of its cells.
    internal const double AreaCellReachYd    = 60.0;
    private const int    MaxNavmeshCorners   = 128;
    // A partial navmesh way must end at least this far from the character to be worth walking.
    private const double PartialMinGainYd    = 2.0;

    private readonly RynthCoreHost _host;
    private readonly Func<MainLogic?> _raycast;
    private readonly Func<IReadOnlySet<uint>?> _hazards;

    // Per-session caches (dropped with this object).
    private readonly Dictionary<uint, bool> _dungeonLandblock = new();

    public NavRecoveryPlanner(RynthCoreHost host, Func<MainLogic?> raycast, Func<IReadOnlySet<uint>?> hazards)
    {
        _host    = host;
        _raycast = raycast;
        _hazards = hazards;
    }

    private DatDatabase? CellDat
    {
        get
        {
            var ray = _raycast();
            var dat = ray?.GeometryLoader?.CellDat;
            return dat != null && dat.IsLoaded ? dat : null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  PLANNING
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a walkable way from the character to <paramref name="target"/>. Returns null
    /// with <paramref name="why"/> set when no pathfinder can help (the caller then uses
    /// the old escape ladder).
    /// </summary>
    public NavRecoveryPlan? TryPlan(NavPoint target, out string why)
    {
        why = string.Empty;
        if (!_host.HasGetPlayerPose ||
            !_host.TryGetPlayerPose(out uint cell, out float lx, out float ly, out float lz, out _, out _, out _, out _) ||
            !NavCoordinateHelper.TryConvertPoseToCoords(cell, lx, ly, out double pNs, out double pEw))
        {
            why = "no player position";
            return null;
        }

        // Route Z is stored as world z / 240; 0 means "not recorded", so use ours.
        float tz = target.Z != 0.0 ? (float)(target.Z * 240.0) : lz;
        bool inEnvCell = (cell & 0xFFFF) >= 0x0100;

        string indoorWhy = string.Empty;
        if (inEnvCell)
        {
            var plan = PlanIndoor(cell, pNs, pEw, lz, target.NS, target.EW, tz, out bool applicable, out indoorWhy);
            if (plan != null) return plan;
            if (applicable) { why = indoorWhy; return null; }
            // In a dungeon the landscape navmesh is meaningless: a tile baked for this
            // landblock (the bake-ahead watcher bakes around wherever the player is) is
            // flat ground at height 0, and a path on it walks through the dungeon walls.
            var cellDat = CellDat;
            if (cellDat != null && IsDungeonLandblock(cell >> 16, cellDat))
            {
                why = indoorWhy.Length > 0 ? indoorWhy : "no dungeon map path";
                return null;
            }
            // Inside a building with the waypoint outdoors: try the navmesh.
        }

        var outdoor = PlanOutdoor(cell, lx, ly, lz, target.NS, target.EW, tz, out string outdoorWhy);
        if (outdoor != null) return outdoor;
        why = indoorWhy.Length > 0 ? $"{indoorWhy}; {outdoorWhy}" : outdoorWhy;
        return null;
    }

    private NavRecoveryPlan? PlanIndoor(uint playerCell, double pNs, double pEw, float pz,
                                        double tNs, double tEw, float tz,
                                        out bool applicable, out string why)
    {
        applicable = false;
        why = string.Empty;
        var cellDat = CellDat;
        if (cellDat == null) { why = "dungeon data not loaded yet"; return null; }

        uint lb = playerCell >> 16;
        var graph = DungeonPathfinder.GetGraph(lb, cellDat);
        if (graph.Count == 0) { why = "no cell graph here"; return null; }

        // The waypoint's room is the cell whose FLOOR contains it. The nearest cell centre was
        // wrong for a waypoint just behind a wall in a big room: it named the player's own room,
        // "already in the waypoint's room, the map has no way around", and the bot ran into the
        // wall for an hour (Lucy, 6346, 2026-10-01). Nearest centre is only the fallback.
        uint goal = ContainingCell(lb, NavToWorld(tEw), NavToWorld(tNs), tz);
        if (goal == 0 || !graph.ContainsKey(goal))
            goal = DungeonPathfinder.NearestCell(graph, tNs, tEw, tz);
        if (goal == 0 || !graph.TryGetValue(goal, out var goalNode)) { why = "waypoint not on the dungeon map"; return null; }
        bool dungeon = IsDungeonLandblock(lb, cellDat);
        double reach = dungeon ? DungeonCellReachYd : BuildingCellReachYd;
        if (NavYd(goalNode.NS - tNs, goalNode.EW - tEw) > reach)
        {
            why = dungeon ? "waypoint isn't near any room of this dungeon" : "waypoint is outside this building";
            return null;   // not applicable: let the navmesh try
        }

        applicable = true;
        uint start = graph.ContainsKey(playerCell) ? playerCell : DungeonPathfinder.NearestCell(graph, pNs, pEw, pz);
        if (start == 0) { why = "can't place you on the dungeon map"; return null; }
        var geo = DungeonPathfinder.GetGeometry(lb, cellDat, _raycast()?.GeometryLoader?.DungeonLOS);
        if (start == goal)
        {
            // The same cell: what it stuck on is in the room with it (a table, a statue), and the cell graph has
            // nothing to say. The dungeon map walks round it.
            string roomWhy = string.Empty;
            var inRoom = geo != null ? PlanInRoom(geo, pNs, pEw, pz, tNs, tEw, tz, out roomWhy) : null;
            if (inRoom != null) return inRoom;
            why = geo == null ? "already in the waypoint's room, the map has no way around"
                              : $"already in the waypoint's room, the map has no way around ({roomWhy})";
            return null;
        }

        var path = DungeonPathfinder.FindPath(start, goal, graph, _hazards());
        if (path.Count < 2) { why = "no walkable way on the dungeon map"; return null; }

        // The same points the dungeon patrol uses: an approach and an exit point centred on
        // every narrow opening (flagged, so the engine goes through straight), rooms crossed
        // directly. Without the map geometry, cell-centre midpoints.
        var plan = new NavRecoveryPlan { Planner = "dungeon map" };
        plan.Points = DungeonPathfinder.BuildPathPoints(path, graph, geo, _hazards(), pNs, pEw, pz, tNs, tEw, tz, out int doorways);
        plan.Doorways = doorways;
        if (plan.Points.Count == 0) { why = "empty path"; return null; }
        plan.LengthYd = PathLengthYd(pNs, pEw, plan.Points, tNs, tEw);
        return plan;
    }

    // ── In one room (2026-11-18) ────────────────────────────────────────────
    // 9 of the 29 logged "no plan" recoveries were "already in the waypoint's room": the character and the
    // waypoint in one cell, with a table (0x0190 room 88, 0x020002FB), a statue (0x00C1 room 81, 0x020014A3: the
    // waypoint in its middle), an altar (0x000A) or a slab (0x01F5) between them, and the stuck escapes ran blind
    // until they gave up (about 3 minutes in 0x00C1). The dungeon map knows those objects now.

    /// <summary>A waypoint inside something placed is moved out to this much room ...</summary>
    internal const double InRoomTargetClearance = 1.0;
    /// <summary>... at most this far (0x00C1's statue is 9 x 9 m: its waypoint came 2.5 m out).</summary>
    internal const double InRoomTargetPushMax = 8.0;

    /// <summary>
    /// The walk from the character (pNs, pEw, pz) to the waypoint (tNs, tEw, tz) within one room, on the dungeon map:
    /// a waypoint inside a placed object is moved out of it first (PushClear to InRoomTargetClearance, at most
    /// InRoomTargetPushMax), then the straight walk is walked round the placed objects on the floor
    /// (DungeonPathfinder.WalkRoundHoles, objects only). A "dungeon map (room)" plan when that put points in (the
    /// moved waypoint is its last point, and <see cref="NavRecoveryPlan.TargetMoved"/> is set); null, with
    /// <paramref name="why"/>, when the straight walk is clear (the snag isn't on the map) or there is no way round.
    /// Only an obstacle's waypoint is moved: one up on something placed (at its own height <paramref name="tz"/>), or in a
    /// step-over or a connector, gets no plan.
    /// </summary>
    internal static NavRecoveryPlan? PlanInRoom(DungeonGeometry geo, double pNs, double pEw, double pz,
                                                double tNs, double tEw, double tz, out string why)
    {
        why = string.Empty;
        double px = DungeonGeometry.NavToWorld(pEw), py = DungeonGeometry.NavToWorld(pNs);
        double tx = DungeonGeometry.NavToWorld(tEw), ty = DungeonGeometry.NavToWorld(tNs);
        if (!geo.TryFloorHeight(px, py, pz, 3.0, out double ph)) ph = pz;
        if (!geo.TryFloorHeight(tx, ty, tz, 3.0, out double th)) th = tz;

        bool moved = false;
        if (geo.InsidePlaced(tx, ty, th))
        {
            // Inside it at the map's floor is not inside it for a waypoint up on it: one recorded on a dais's top, half way
            // up a ramp or on a platform. Moved down beside the object and taken as reached (TargetMoved), it was skipped,
            // and the next waypoint, up there too, ran the character into the object's side again, stuck after stuck. Nor
            // is a step or a ramp (a step-over, a connector) something to move a waypoint out of: it is the way on. Both are
            // left to the escapes, as before the room planner (review of wf/obst-int).
            if (geo.OnTopOfPlaced(tx, ty, tz, th))
            {
                why = "the waypoint is up on something placed (the map has no floor on its top)";
                return null;
            }
            if (geo.InsidePlaced(tx, ty, Math.Max(th, tz), c => c is PlacedClass.StepOver or PlacedClass.Connector))
            {
                why = "the waypoint is in a step or a ramp placed there (climbed, not walked round)";
                return null;
            }
            double ox = tx, oy = ty;
            geo.PushClear(ref tx, ref ty, th, InRoomTargetClearance, InRoomTargetPushMax);
            if (geo.InsidePlaced(tx, ty, th) || !geo.OnWalkableFloor(tx, ty, th))
            {
                why = "the waypoint is inside something placed, with no floor clear of it within " + InRoomTargetPushMax.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m";
                return null;
            }
            moved = Math.Abs(tx - ox) > 1e-6 || Math.Abs(ty - oy) > 1e-6;
        }

        var target = new NavPoint { Type = NavPointType.Point, NS = DungeonGeometry.WorldToNav(ty), EW = DungeonGeometry.WorldToNav(tx), Z = th / 240.0 };
        var pts = new List<NavPoint>
        {
            new NavPoint { Type = NavPointType.Point, NS = pNs, EW = pEw, Z = ph / 240.0 },
            target,
        };
        using var count = DungeonPathfinder.CountHoles();
        int left0 = DungeonPathfinder.HolesLeft;
        int round = DungeonPathfinder.WalkRoundHoles(pts, geo, objectsOnly: true);
        if (round == 0 && DungeonPathfinder.HolesLeft > left0) { why = "no way round what is placed there"; return null; }
        if (round == 0 && !moved) { why = "the straight walk is clear on the map (the snag isn't on it)"; return null; }

        var plan = new NavRecoveryPlan { Planner = moved ? "dungeon map (room, waypoint moved out of an object)" : "dungeon map (room)", TargetMoved = moved };
        // The character's own spot is not walked to; the waypoint is the engine's, unless it was moved.
        for (int i = 1; i < pts.Count; i++)
            if (i + 1 < pts.Count || moved) plan.Points.Add(pts[i]);
        plan.LengthYd = PathLengthYd(pNs, pEw, plan.Points, tNs, tEw);
        return plan;
    }

    /// <summary>
    /// NavigationEngine.EscapeRoom: on the dungeon map, how far (metres, up to 2) the character at (wx, wy, wz) (world
    /// metres, in <paramref name="cell"/>) gets along a compass heading (0 north, clockwise) before it runs into
    /// something; NaN outside a dungeon's cell, without the map, or with no floor under the character on it.
    /// </summary>
    internal double EscapeRoom(uint cell, double wx, double wy, double wz, double headingDeg)
    {
        try
        {
            if ((cell & 0xFFFF) < 0x0100) return double.NaN;
            var cellDat = CellDat;
            if (cellDat == null || !IsDungeonLandblock(cell >> 16, cellDat)) return double.NaN;
            var geo = DungeonPathfinder.GetGeometry(cell >> 16, cellDat, _raycast()?.GeometryLoader?.DungeonLOS);
            return geo == null ? double.NaN : EscapeRoomOn(geo, wx, wy, wz, headingDeg);
        }
        catch { return double.NaN; }
    }

    /// <summary>Along an escape heading, the character has run into something where it comes this near a wall, a floor's
    /// edge or something placed (half its 0.48 m body over it): a burst sliding along a face it stuck on gets on, one
    /// head-on into it doesn't.</summary>
    internal const double EscapeTouchMeters = 0.25;
    internal const double EscapeLookMeters = 2.0;

    /// <summary>The map half of <see cref="EscapeRoom"/>: looked at every 0.1 m, following the floor.</summary>
    internal static double EscapeRoomOn(DungeonGeometry geo, double wx, double wy, double wz, double headingDeg)
    {
        if (!geo.TryFloorHeight(wx, wy, wz, 3.0, out double h)) return double.NaN;
        double a = headingDeg * Math.PI / 180.0, ux = Math.Sin(a), uy = Math.Cos(a);
        const double step = 0.1;
        for (double d = step; d <= EscapeLookMeters + 1e-9; d += step)
        {
            double x = wx + ux * d, y = wy + uy * d;
            if (!geo.TryFloorHeight(x, y, h, 0.6, out double hh) || !geo.OnWalkableFloor(x, y, hh)
                || geo.ClearanceMemo(x, y, hh, EscapeTouchMeters + 0.1) < EscapeTouchMeters)
                return d - step;
            h = hh;
        }
        return EscapeLookMeters;
    }

    private NavRecoveryPlan? PlanOutdoor(uint cell, float lx, float ly, float lz,
                                         double tNs, double tEw, float tz, out string why)
    {
        why = string.Empty;
        if (!_host.HasGetPluginInterface) { why = "RynthNav needs a newer RynthCore"; return null; }

        double sx = ((cell >> 24) & 0xFF) * 192.0 + lx;
        double sy = ((cell >> 16) & 0xFF) * 192.0 + ly;
        double gx = (tEw * 10.0 + 1019.5) * 24.0;
        double gy = (tNs * 10.0 + 1019.5) * 24.0;

        int result;
        int count = 0;
        double[] buf = new double[MaxNavmeshCorners * 3];
        double[] info = new double[RynthNavPathApiV1.InfoLength];
        bool ex;
        unsafe
        {
            // Resolved on every call: RynthNav may be missing, or reloaded since last time.
            IntPtr p = _host.GetPluginInterface(RynthNavPathApiV1.PluginName, RynthNavPathApiV1.InterfaceName, 1);
            var t = (RynthNavPathApiV1*)p;
            if (t == null || t->Version < 1 || t->StructSize < RynthNavPathApiV1.SizeV1 || t->FindPath == null)
            {
                why = "RynthNav isn't loaded";
                return null;
            }
            // Version 2: FindPathEx with the Outdoor Hunt's flags, so the way back keeps out of portals and
            // 1 m off the walls as the hunt's walks do (v1 FindPath does neither: a detour could run along a
            // building's wall or through a portal's circle). No PathStartOutdoors: the start is where the
            // character stands, so from inside a building RynthNav refuses it as before. A RynthNav from
            // before v2: FindPath.
            ex = t->Version >= 2 && t->StructSize >= (uint)sizeof(RynthNavPathApiV1) && t->FindPathEx != null;
            fixed (double* pts = buf)
            fixed (double* inf = info)
                result = ex
                    ? t->FindPathEx(sx, sy, lz, gx, gy, tz, RynthAiPlugin.HuntPathFlags, 0, pts, MaxNavmeshCorners, &count, inf)
                    : t->FindPath(sx, sy, lz, gx, gy, tz, pts, MaxNavmeshCorners, &count);
        }

        if (result == RynthNavPathApiV1.ResultNoMesh) { why = "no RynthNav navmesh here"; return null; }
        // A partial way (the waypoint off the mesh, or past a seam between two tiles that don't link) ends at
        // the reachable point nearest the waypoint: that point is walked to as well, then the route's own leg
        // from there, as RynthNav's own walks push straight on over a seam.
        bool partial = ex && result == RynthNavPathApiV1.ResultPartial;
        if ((result != RynthNavPathApiV1.ResultOk && !partial) || count < 2) { why = "RynthNav found no walkable way"; return null; }
        // A partial way that ends where the character stands gets it nowhere: no detour, the escapes (as v1 said).
        double endX = buf[(count - 1) * 3] - sx, endY = buf[(count - 1) * 3 + 1] - sy;
        if (partial && endX * endX + endY * endY < PartialMinGainYd * PartialMinGainYd)
        {
            why = "RynthNav's way ends where you stand (the waypoint isn't reachable on its map)";
            return null;
        }

        var plan = new NavRecoveryPlan
        {
            Planner = partial ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RynthNav navmesh (partial, ends {info[5]:F0}yd short)") : "RynthNav navmesh",
            NavmeshCorners = true,
        };
        // Corners 1..count-2 are the turns; the first is our position and the last the waypoint (for a
        // partial way the last is where it ends, a point of the detour too).
        int last = partial ? count : count - 1;
        for (int i = 1; i < last; i++)
        {
            double wx = buf[i * 3], wy = buf[i * 3 + 1], wz = buf[i * 3 + 2];
            plan.Points.Add(new NavPoint
            {
                Type = NavPointType.Point,
                EW   = (wx / 24.0 - 1019.5) / 10.0,
                NS   = (wy / 24.0 - 1019.5) / 10.0,
                Z    = wz / 240.0,
            });
        }
        if (plan.Points.Count == 0) { why = "RynthNav sees a clear straight line (the snag isn't on its map)"; return null; }

        double pNs = (sy / 24.0 - 1019.5) / 10.0, pEw = (sx / 24.0 - 1019.5) / 10.0;
        plan.LengthYd = PathLengthYd(pNs, pEw, plan.Points, tNs, tEw);
        return plan;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  AREA CHECK (route in another dungeon / on the landscape)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compares where the character is with where route waypoint (tNs, tEw) is. On a
    /// mismatch, <paramref name="routeArea"/> and <paramref name="playerArea"/> name the
    /// two places for the chat line ("dungeon 0x01D9", "the landscape").
    /// </summary>
    public NavAreaCheck CheckArea(uint playerCell, double tNs, double tEw, out string routeArea, out string playerArea)
    {
        routeArea = string.Empty;
        playerArea = string.Empty;
        var cellDat = CellDat;
        if (cellDat == null || playerCell == 0) return NavAreaCheck.Unknown;

        return ClassifyArea(playerCell, tNs, tEw,
            lb => IsDungeonLandblock(lb, cellDat),
            (lb, ns, ew) => NearAnyCell(lb, cellDat, ns, ew),
            out routeArea, out playerArea);
    }

    /// <summary>
    /// The decision half of <see cref="CheckArea"/>, given the two dat lookups: is a
    /// landblock a dungeon, and is (ns, ew) within reach of one of that landblock's cells.
    /// </summary>
    internal static NavAreaCheck ClassifyArea(uint playerCell, double tNs, double tEw,
        Func<uint, bool> isDungeonLandblock, Func<uint, double, double, bool> nearAnyCell,
        out string routeArea, out string playerArea)
    {
        routeArea = string.Empty;
        playerArea = string.Empty;

        double wx = NavToWorld(tEw), wy = NavToWorld(tNs);
        if (wx < 0 || wy < 0 || wx >= 256 * 192.0 || wy >= 256 * 192.0) return NavAreaCheck.Unknown;
        uint tLb = ((uint)(wx / 192.0) << 8) | (uint)(wy / 192.0);
        uint pLb = playerCell >> 16;
        bool playerInDungeon = (playerCell & 0xFFFF) >= 0x0100 && isDungeonLandblock(pLb);

        if (playerInDungeon)
        {
            if (tLb == pLb) return NavAreaCheck.Match;
            if (nearAnyCell(pLb, tNs, tEw)) return NavAreaCheck.Match;
            routeArea  = isDungeonLandblock(tLb) ? $"dungeon 0x{tLb:X4}" : "the landscape";
            playerArea = $"in dungeon 0x{pLb:X4}";
            return NavAreaCheck.Mismatch;
        }

        // Inside a cell of a landblock that doesn't pass the dungeon test: a building, or a
        // dungeon the test misses (the excluded north-west corner, one with terrain). Its
        // rooms can map onto a neighbouring dungeon landblock, so nothing is provable.
        if ((playerCell & 0xFFFF) >= 0x0100) return NavAreaCheck.Match;

        // On the landscape.
        if (!isDungeonLandblock(tLb)) return NavAreaCheck.Match;
        if (!nearAnyCell(tLb, tNs, tEw)) return NavAreaCheck.Match;   // not provably in a dungeon cell
        routeArea  = $"dungeon 0x{tLb:X4}";
        playerArea = "on the landscape";
        return NavAreaCheck.Mismatch;
    }

    private static bool NearAnyCell(uint lb, DatDatabase cellDat, double ns, double ew)
    {
        var graph = DungeonPathfinder.GetGraph(lb, cellDat);
        foreach (var node in graph.Values)
            if (NavYd(node.NS - ns, node.EW - ew) <= AreaCellReachYd) return true;
        return false;
    }

    /// <summary>
    /// A landblock with no landscape of its own: every terrain height 0, at least one
    /// EnvCell, and no buildings (the same test ACE uses). The far north-west corner is
    /// excluded because some real landscape there is flat at height 0.
    /// </summary>
    public bool IsDungeonLandblock(uint lb, DatDatabase cellDat)
    {
        if (_dungeonLandblock.TryGetValue(lb, out bool cached)) return cached;
        bool result = ComputeIsDungeon(lb, cellDat);
        if (_dungeonLandblock.Count > 512) _dungeonLandblock.Clear();
        _dungeonLandblock[lb] = result;
        return result;
    }

    internal static bool ComputeIsDungeon(uint lb, DatDatabase cellDat)
    {
        try
        {
            uint x = (lb >> 8) & 0xFF, y = lb & 0xFF;
            if (x < 0x08 && y > 0xF8) return false;

            byte[]? info = cellDat.GetFileData((lb << 16) | 0xFFFE);
            if (info == null || info.Length < 12) return false;
            uint numCells = BitConverter.ToUInt32(info, 4);
            if (numCells == 0) return false;
            uint numObjects = BitConverter.ToUInt32(info, 8);
            long buildingsAt = 12 + (long)numObjects * 32;
            if (buildingsAt + 2 <= info.Length && BitConverter.ToUInt16(info, (int)buildingsAt) > 0) return false;

            // CellLandblock: id, hasObjects, ushort terrain[81], byte height[81].
            byte[]? land = cellDat.GetFileData((lb << 16) | 0xFFFF);
            const int heightsAt = 8 + 81 * 2;
            if (land == null || land.Length < heightsAt + 81) return false;
            for (int i = 0; i < 81; i++)
                if (land[heightsAt + i] != 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static double NavToWorld(double navCoord) => (navCoord * 10.0 + 1019.5) * 24.0;

    /// <summary>The cell whose floor polygon contains (wx, wy) at height wz (world space), from
    /// the dungeon map geometry DungeonLOS caches; 0 when unknown.</summary>
    private uint ContainingCell(uint lb, double wx, double wy, float wz)
    {
        var los = _raycast()?.GeometryLoader?.DungeonLOS;
        if (los == null) return 0;
        try
        {
            // The physics floor polygons. GetDungeonMapPolygons (render geometry) never sets
            // IsFloor, so filtering it on IsFloor matched nothing and the nearest-centre fallback
            // always decided: the b66e790 fix never ran in game. That list holds ceilings too;
            // only the polygons facing up are floors.
            var polys = los.GetDungeonMapFloorPolygons(lb);
            var floors = new List<(uint, Vector3[])>(polys.Count);
            foreach (var p in polys)
                if (!p.IsPortal && p.Vertices != null && DungeonGeometry.IsUpwardFloor(p.Vertices)) floors.Add((p.CellId, p.Vertices));
            return PickContainingCell(floors, wx, wy, wz);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Of the floor polygons, the cell of the one under (wx, wy): the point inside it (2D) with
    /// the floor at most 1 m above and 4 m below wz (stacked floors: the closest one under the
    /// feet wins). 0 when no floor contains it.
    /// </summary>
    internal static uint PickContainingCell(IReadOnlyList<(uint CellId, Vector3[] Verts)> floors, double wx, double wy, float wz)
    {
        uint best = 0;
        float bestDz = float.MaxValue;
        foreach (var (cellId, v) in floors)
        {
            if (v.Length < 3 || !InsidePolygon2D(v, wx, wy)) continue;
            float avgZ = 0;
            foreach (var p in v) avgZ += p.Z;
            avgZ /= v.Length;
            float dz = wz - avgZ;                       // feet above the floor
            if (!float.IsNaN(wz) && (dz < -1f || dz > 4f)) continue;
            float score = float.IsNaN(wz) ? 0f : MathF.Abs(dz);
            if (score < bestDz) { bestDz = score; best = cellId; }
        }
        return best;
    }

    private static bool InsidePolygon2D(Vector3[] v, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            double xi = v[i].X, yi = v[i].Y, xj = v[j].X, yj = v[j].Y;
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static double NavYd(double dNs, double dEw) => Math.Sqrt(dNs * dNs + dEw * dEw) * 240.0;

    private static double PathLengthYd(double fromNs, double fromEw, List<NavPoint> pts, double toNs, double toEw)
    {
        double len = 0, ns = fromNs, ew = fromEw;
        foreach (var p in pts) { len += NavYd(p.NS - ns, p.EW - ew); ns = p.NS; ew = p.EW; }
        return len + NavYd(toNs - ns, toEw - ew);
    }
}
