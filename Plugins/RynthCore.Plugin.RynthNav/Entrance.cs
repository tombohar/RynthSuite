using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// Going INTO a place whose map position is a portal: a dungeon (the Atlas places a dungeon at
/// its entrance portal) or a portal with a destination. The goto's last leg becomes a portal
/// step, so it walks into the portal and waits for the teleport (the same step routes use),
/// instead of stopping beside it. Host-free (the route tests cover it).
/// </summary>
internal static class Entrance
{
    /// <summary>
    /// A wrecked portal ("Destroyed Portal to Redspire", "Destroyed Mayoi Portal"): still in the world
    /// data, but it takes you nowhere (10-02: routes and dungeon gotos tried to use them).
    /// </summary>
    public static bool IsDestroyed(string name) => name.StartsWith("Destroyed", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an Atlas place that you reach by walking into a portal.</summary>
    public static bool IsEntrance(AtlasLocation l) =>
        l.Type.Equals("Dungeon", StringComparison.Ordinal) || (l.Type.Equals("Portal", StringComparison.Ordinal) && l.HasDest);

    /// <summary>
    /// The portal to walk into for <paramref name="place"/>. A dungeon with several entrances: the
    /// nearest open one on the map (one that needs no quest first, when there is a choice), else the
    /// dungeon's own position (its entrance). Null, with <paramref name="why"/>, when the entrance
    /// has no position on the map (it's inside another dungeon): nothing is guessed.
    /// </summary>
    public static AtlasLocation? PortalFor(Atlas atlas, AtlasLocation place, bool hasPos, double ns, double ew, out string why)
    {
        why = "";
        if (place.Type.Equals("Portal", StringComparison.Ordinal))
        {
            if (IsDestroyed(place.Name)) { why = $"{place.Name} is destroyed: it doesn't take you anywhere"; return null; }
            if (place.OnMap) return place;
            why = $"{place.Name} is inside a dungeon (its portal has no position on the map), so I can't walk there";
            return null;
        }

        AtlasLocation? best = null;
        double bestD = double.MaxValue;
        bool bestQuest = true, sawDestroyed = false;
        foreach (AtlasLocation l in atlas.Locations)
        {
            if (!l.OnMap || l.Closed || !l.HasDest || l.DestOnMap) continue;
            if (!l.Type.Equals("Portal", StringComparison.Ordinal)) continue;
            if (!l.DestName.Equals(place.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsDestroyed(l.Name)) { sawDestroyed = true; continue; }
            double d = hasPos ? NavCoords.Distance(ns, ew, l.Ns, l.Ew) : 0;
            bool better = best == null || (bestQuest && !l.Quest) || (bestQuest == l.Quest && d < bestD);
            if (!better) continue;
            best = l; bestD = d; bestQuest = l.Quest;
        }
        if (best != null) return best;
        if (sawDestroyed)
        {
            why = $"the only portal into {place.Name} on the map is destroyed, so I can't walk in";
            return null;
        }
        if (place.OnMap) return place;
        why = $"{place.Name}'s entrance has no position on the map (it's reached from inside another dungeon), so I can't walk there";
        return null;
    }

    /// <summary>
    /// The route, ending with walking into the portal at (<paramref name="ns"/>, <paramref name="ew"/>):
    /// a plain walk becomes one portal step; a route (portals, recalls) whose last leg walks there
    /// gets that leg turned into a portal step.
    /// </summary>
    public static List<RouteStep> EndingInPortal(List<RouteStep>? route, double ns, double ew, string label)
    {
        var steps = route == null ? new List<RouteStep>() : new List<RouteStep>(route);
        if (steps.Count > 0)
        {
            RouteStep last = steps[^1];
            if (!last.UsePortal && !last.UseRecall && !last.IsHub && NavCoords.Distance(last.Ns, last.Ew, ns, ew) < 1.0)
                steps.RemoveAt(steps.Count - 1);
        }
        steps.Add(new RouteStep(ns, ew, usePortal: true, useRecall: false, label));
        return steps;
    }

    /// <summary>The goto's line when the portal takes you in.</summary>
    public static string ArrivedText(string name, string type) =>
        type.Equals("Dungeon", StringComparison.Ordinal) ? $"arrived inside {name}" : $"went through {name}";

    /// <summary>The goto's stop line when the portal doesn't fire.</summary>
    public static string NoTeleportText(string name) =>
        $"stopped: {name}'s portal didn't take you in (level or quest requirement?)";
}
