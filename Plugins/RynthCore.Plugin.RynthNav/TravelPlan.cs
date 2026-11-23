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
///  - Bans: teleports this plan may not take (PortalRoute's keys): portals the server would refuse
///    this character (PortalRules), and ones it has refused already.
///  - Options: up to that many different ways (PortalRoute.PlanOptions), cheapest first; the first is
///    the plan itself. "Another way" in the panel and /rnav route next walk through them.
///  - walkCost: walks costed on the route graph (PortalRoute's real walk costs); null = straight lines.
///  - times: teleports costed by how long they take (TripTimes); null = the flat costs.
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
        /// <summary>The ways there, cheapest first (Options[0] is this plan); empty when there is none.</summary>
        public readonly List<Option> Options = new();
    }

    /// <summary>One way there: as Result's Steps / Est / Teleports, plus what it leaves out that a cheaper way took.</summary>
    public sealed class Option
    {
        public List<RouteStep>? Steps;
        public double Est;
        public int Teleports;
        public List<string> Avoids = new();
        public HashSet<string> Bans = new(StringComparer.Ordinal);
        public string Signature = "";
    }

    private static Option FromRoute(RouteOption o) => new()
    {
        Steps = o.Teleports > 0 ? o.Steps : null, Est = o.Est, Teleports = o.Teleports,
        Avoids = new List<string>(o.Avoids), Bans = new HashSet<string>(o.Bans, StringComparer.Ordinal), Signature = o.Signature,
    };

    public static Result Plan(IReadOnlyList<PortalLink>? portals, IReadOnlyList<RecallLink>? recalls, HubLinks? hub,
        bool portalsOn, bool recallsOn, bool startOnMap,
        double startNs, double startEw, double goalNs, double goalEw,
        Func<double, double, double, double, bool>? canWalk, bool? landReachable, string goalName,
        ISet<string>? bans = null, int options = 1, Func<double, double, double, double, double>? walkCost = null,
        TripTimes? times = null, Action? bestPlanned = null)
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
            List<RouteOption> fromDungeon = PortalRoute.PlanOptions(portals ?? Array.Empty<PortalLink>(), usableRecalls, hub, startNs, startEw, goalNs, goalEw,
                Math.Max(1, options), canWalk, startOnMap: false, bans, walkCost, times, bestPlanned);
            if (fromDungeon.Count == 0 || fromDungeon[0].Steps.Count == 0)
            {
                r.Steps = null;
                r.Est = double.PositiveInfinity;
                r.Refusal = $"you're in a dungeon and none of your recalls leads on to {goalName}";
                return r;
            }
            foreach (RouteOption o in fromDungeon) r.Options.Add(new Option { Steps = o.Steps, Est = o.Est, Teleports = o.Teleports,
                Avoids = new List<string>(o.Avoids), Bans = new HashSet<string>(o.Bans, StringComparer.Ordinal), Signature = o.Signature });
            r.Steps = r.Options[0].Steps; r.Est = r.Options[0].Est; r.Teleports = r.Options[0].Teleports;
            return r;
        }

        if (!portalsOn)
        {
            if (landReachable == false) r.Refusal = $"no walking route to {goalName}: it's on another landmass (water or cliffs in the way); turn on Use portals and recalls";
            else r.Options.Add(new Option { Est = r.Est });
            return r;   // walk
        }

        List<RouteOption> ways = PortalRoute.PlanOptions(portals ?? Array.Empty<PortalLink>(), usableRecalls, hub, startNs, startEw, goalNs, goalEw,
            Math.Max(1, options), canWalk, startOnMap: true, bans, walkCost, times, bestPlanned);
        double est = ways.Count > 0 ? ways[0].Est : double.PositiveInfinity;
        int used = ways.Count > 0 ? ways[0].Teleports : 0;
        if (double.IsPositiveInfinity(est))
        {
            r.Refusal = $"no walking route to {goalName}: it's on another landmass (water or cliffs in the way), and none of your portals, "
                      + (recallsOn ? "recalls " : "") + "or the Town Network gets there" + (recallsOn ? "" : " (Recalls are off)");
            return r;
        }
        r.Est = est;
        if (used == 0 && landReachable == false)
        {
            r.Refusal = $"no walking route to {goalName}: it's on another landmass (water or cliffs in the way)";
            return r;
        }
        foreach (RouteOption o in ways) r.Options.Add(FromRoute(o));
        if (used == 0) return r;   // walk
        r.Steps = ways[0].Steps;
        r.Teleports = used;
        return r;
    }
}
