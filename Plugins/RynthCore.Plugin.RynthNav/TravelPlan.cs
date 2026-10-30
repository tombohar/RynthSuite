using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// What a goto (or the panel's Route, or the arrow's route) may use, and the plan: the switches,
/// where you stand, and what the land allows, in one place. Host-free: the route tests drive it.
///  - "Use portals and recalls" off: walking only. Route and Go both respect it (Route used to plan
///    with portals either way).
///  - "Recalls" is part of that: it only counts when "Use portals and recalls" is on.
///  - In a dungeon (no map position) there is no walking start: the route must begin with a recall
///    (your dungeon's coordinates aren't map coordinates; 10-01 a route "walked" from 0,0 to a
///    portal at 86S). With no recall to cast, it refuses and says why.
///  - Walks between landmasses are left out (canWalk); a target only reachable over water needs a
///    portal, a recall or the Town Network.
/// </summary>
internal static class TravelPlan
{
    public sealed class Result
    {
        /// <summary>The legs; null = walk straight there (no teleport helps, or none may be used).</summary>
        public List<RouteStep>? Steps;
        public double Est;
        public int Teleports;
        /// <summary>Why there is no way at all ("" = there is one: Steps, or a plain walk).</summary>
        public string Refusal = "";
        /// <summary>One line on what was allowed, for the log.</summary>
        public string Basis = "";
    }

    public static Result Plan(IReadOnlyList<PortalLink>? portals, IReadOnlyList<RecallLink>? recalls, HubLinks? hub,
        bool portalsOn, bool recallsOn, bool startOnMap,
        double startNs, double startEw, double goalNs, double goalEw,
        Func<double, double, double, double, bool>? canWalk, bool? landReachable, string goalName)
    {
        var r = new Result { Est = NavCoords.Distance(startNs, startEw, goalNs, goalEw) };
        recalls ??= Array.Empty<RecallLink>();
        var usableRecalls = portalsOn && recallsOn ? recalls : Array.Empty<RecallLink>();
        r.Basis = $"portals {(portalsOn ? "on" : "off")}, recalls {(portalsOn && recallsOn ? $"on ({recalls.Count} known)" : recallsOn ? "on but unused (portals off)" : "off")}, "
                + $"start {(startOnMap ? "on the map" : "in a dungeon")}, over land {(landReachable == null ? "unknown" : landReachable.Value ? "yes" : "no")}";

        if (!startOnMap)
        {
            // No walking start: only a recall gets you out.
            if (!portalsOn) { r.Refusal = "you're in a dungeon, so the way out is a recall: turn on Use portals and recalls"; return r; }
            if (!recallsOn) { r.Refusal = "you're in a dungeon, so the way out is a recall: turn on Recalls"; return r; }
            if (usableRecalls.Count == 0) { r.Refusal = "you're in a dungeon and no recall is ready to use (/rnav recalls says why)"; return r; }
            r.Steps = PortalRoute.Plan(portals ?? Array.Empty<PortalLink>(), usableRecalls, hub, startNs, startEw, goalNs, goalEw,
                out r.Est, out r.Teleports, canWalk, startOnMap: false);
            if (double.IsPositiveInfinity(r.Est) || r.Steps.Count == 0)
            {
                r.Steps = null;
                r.Refusal = $"you're in a dungeon and none of your recalls leads on to {goalName}";
            }
            return r;
        }

        if (!portalsOn)
        {
            if (landReachable == false) r.Refusal = $"no walking route to {goalName}: it's on another landmass (water or cliffs in the way); turn on Use portals and recalls";
            return r;   // walk
        }

        var steps = PortalRoute.Plan(portals ?? Array.Empty<PortalLink>(), usableRecalls, hub, startNs, startEw, goalNs, goalEw,
            out double est, out int used, canWalk);
        if (double.IsPositiveInfinity(est))
        {
            r.Refusal = $"no walking route to {goalName}: it's on another landmass (water or cliffs in the way), and none of your portals, "
                      + (recallsOn ? "recalls " : "") + "or the Town Network gets there" + (recallsOn ? "" : " (Recalls are off)");
            return r;
        }
        r.Est = est;
        if (used == 0)
        {
            if (landReachable == false) r.Refusal = $"no walking route to {goalName}: it's on another landmass (water or cliffs in the way)";
            return r;   // walk
        }
        r.Steps = steps;
        r.Teleports = used;
        return r;
    }
}
