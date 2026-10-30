using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The Dungeon Patrol keeps out of portals (2026-10-23). A portal fires when the character touches
/// it, and the patrol walked into one: in Tiny Hive (0x0289, coverage Everything) waypoint 1 sat on
/// the Surface Portal at the back of a nook two cells north of the start, and a client that began
/// its lap there was teleported out. Here: the portals the client knows in the dungeon (live
/// objects, PatrolPortalsIn) go to the patrol builder (DungeonPathfinder.Portals.cs) as circles to
/// keep out of; a character standing in one walks out first; a portal that comes into view after
/// the build rebuilds the patrol, as a new hazard does (TickDunPatrolHazardReroute). One log line per
/// portal says what was done about it.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private const uint PatrolItemTypePortal = 0x00010000;
    /// <summary>A running patrol looks for portals it doesn't know this often.</summary>
    private const long PatrolPortalCheckMs = 1000;

    /// <summary>The portals the running patrol was built round.</summary>
    private readonly HashSet<uint> _dunPatrolPortalIds = new();
    private long _dunPatrolPortalCheckAt;

    /// <summary>The keep-out radius for the current nav settings: the touch distance plus the corner a
    /// point's reach (FollowNavMin) or the corner rounding (NavLookaheadYards) can cut.</summary>
    private double PatrolPortalRadius()
    {
        var s = _dashboard?.Settings;
        double reach = Math.Max(LegacyUiSettings.FollowNavMinLowest, s?.FollowNavMin ?? 1.5f);
        double cut = Math.Max(reach, NavigationEngine.CornerLookaheadYards(s?.NavLookaheadYards ?? 4.0f, reach));
        return DungeonPathfinder.PortalKeepOutRadius(cut);
    }

    /// <summary>
    /// Every portal the client knows in dungeon landblock <paramref name="landblock"/> (static and
    /// summoned: object class Portal, or an unclassified object whose item type says portal), with
    /// its world position and cell, sorted by id.
    /// </summary>
    private List<PatrolPortal> PatrolPortalsIn(uint landblock)
    {
        var list = new List<PatrolPortal>();
        if (_objectCache == null || !Host.HasGetObjectPosition) return list;
        double radius = PatrolPortalRadius();
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            bool portal = wo.ObjectClass == AcObjectClass.Portal
                || (wo.ObjectClass == AcObjectClass.Unknown && Host.TryGetItemType(uid, out uint type) && (type & PatrolItemTypePortal) != 0);
            if (!portal) continue;
            if (!Host.TryGetObjectPosition(uid, out uint cell, out float x, out float y, out float z) || cell == 0) continue;
            if ((cell >> 16) != landblock || (cell & 0xFFFF) < 0x0100) continue;
            list.Add(new PatrolPortal
            {
                Id = uid, Name = wo.Name ?? "", Cell = cell,
                X = ((cell >> 24) & 0xFF) * 192.0 + x, Y = ((cell >> 16) & 0xFF) * 192.0 + y, Z = z,
                Radius = radius,
            });
        }
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>
    /// The running patrol's dungeon has a portal it wasn't built round (one that arrived after the
    /// build: a dungeon's objects stream in after portal space, a summoned portal). Checked every
    /// PatrolPortalCheckMs.
    /// </summary>
    private bool PatrolPortalAppeared()
    {
        long now = Environment.TickCount64;
        if (now < _dunPatrolPortalCheckAt) return false;
        _dunPatrolPortalCheckAt = now + PatrolPortalCheckMs;
        foreach (var p in PatrolPortalsIn(_dunPatrolLandblock))
        {
            if (_dunPatrolPortalIds.Contains(p.Id)) continue;
            Host.Log($"[RynthAi] DunNav-Patrol: portal 0x{p.Id:X8} '{p.Name}' came into view at ({p.X:F1},{p.Y:F1},{p.Z:F1}) cell {p.Cell:X8}: rebuilding round it");
            return true;
        }
        return false;
    }

    /// <summary>Notes the portals a patrol was built round (so only a new one rebuilds it).</summary>
    private void MarkPatrolPortals(List<PatrolPortal> portals)
    {
        _dunPatrolPortalIds.Clear();
        foreach (var p in portals) _dunPatrolPortalIds.Add(p.Id);
        _dunPatrolPortalCheckAt = Environment.TickCount64 + PatrolPortalCheckMs;
    }

    /// <summary>
    /// Leads the character from where it stands (or from <paramref name="walkOut"/>, the spot it
    /// walks to out of a portal's circle) to the route's first point <paramref name="startIdx"/>
    /// without passing a portal: the walk-out spot and any corners round a circle in the way go in
    /// front of it, the legs joining them to the rest of the loop are kept out too. Returns the
    /// index to start at.
    /// </summary>
    private int LeadPatrolPastPortals(NavRouteParser route, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geometry,
                                      List<PatrolPortal> portals, List<PortalAvoidance>? report, NavPoint? walkOut,
                                      double playerWX, double playerWY, double playerWZ, int startIdx, double wall)
    {
        if (portals.Count == 0 || route.Points.Count == 0) return startIdx;
        if (report != null)
            foreach (var r in report)
                if (DungeonPathfinder.InsidePortal(r.Portal, playerWX, playerWY, playerWZ)) r.StartInside = true;
        startIdx = Math.Clamp(startIdx, 0, route.Points.Count - 1);
        var from = walkOut ?? new NavPoint
        {
            Type = NavPointType.Point,
            EW = DungeonGeometry.WorldToNav(playerWX), NS = DungeonGeometry.WorldToNav(playerWY), Z = playerWZ / 240.0,
        };
        var corners = DungeonPathfinder.PortalLeadIn(graph, geometry, portals, from, route.Points[startIdx], wall, report);
        var way = new List<NavPoint> { from };
        way.AddRange(corners);
        way.Add(route.Points[startIdx]);
        bool clear = true;
        for (int i = 0; i + 1 < way.Count && clear; i++) clear = DungeonPathfinder.PortalLegClear(null, portals, way[i], way[i + 1]);
        if (!clear)
        {
            // No way round to that one: the nearest waypoint the walk to keeps out (and is walkable).
            int best = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < route.Points.Count; i++)
            {
                var q = route.Points[i];
                double d = Math.Sqrt((q.NS - from.NS) * (q.NS - from.NS) + (q.EW - from.EW) * (q.EW - from.EW)) * 240.0;
                if (d < bestD && DungeonPathfinder.PortalLegClear(geometry, portals, from, q)) { bestD = d; best = i; }
            }
            if (best >= 0) { startIdx = best; corners.Clear(); }
            Host.Log(best >= 0
                ? $"[RynthAi] DunNav-Patrol: no way round a portal to the first waypoint in sight; starting at waypoint {best} instead"
                : "[RynthAi] DunNav-Patrol: no way to the patrol from here that keeps out of the portal(s)");
        }
        var lead = new List<NavPoint>();
        if (walkOut != null) lead.Add(walkOut);
        lead.AddRange(corners);
        if (lead.Count == 0) return startIdx;
        route.Points.InsertRange(startIdx, lead);
        DungeonPathfinder.KeepOutOfPortals(route.Points, closed: true, graph, geometry, portals, report, wall);
        for (int i = 0; i < route.Points.Count; i++)
            if (ReferenceEquals(route.Points[i], lead[0])) return i;
        return Math.Min(startIdx, route.Points.Count - 1);
    }

    /// <summary>One log line per portal the patrol was built round.</summary>
    private void LogPatrolPortals(List<PortalAvoidance>? report, List<PatrolPortal> portals, NavRouteParser route)
    {
        if (portals.Count == 0) return;
        try
        {
            if (report == null)
            {
                Host.Log($"[RynthAi] DunNav-Patrol: {portals.Count} portal(s) known, none reached by the patrol's dungeon");
                return;
            }
            foreach (var r in report)
            {
                r.Nearest = DungeonPathfinder.NearestApproach(route.Points, closed: true, r.Portal);
                Host.Log("[RynthAi] DunNav-Patrol " + r.Describe());
            }
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] DunNav-Patrol portal log failed: {ex.Message}");
        }
    }
}
