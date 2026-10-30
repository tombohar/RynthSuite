using System;
using System.Collections.Generic;

namespace RynthNav.Routing;

// Shared, dependency-free travel-route planner. Compiled into BOTH the offline
// RynthNav.PortalGraph tool (for validation) and the in-process RynthNav plugin
// (the code that actually ships), so what we test offline is exactly what runs.
//
// Coordinates everywhere are AC /loc decimal degrees (NS, EW). Distances are in
// world units (1 /loc degree = 240 units). The planner runs a uniform-cost
// (Dijkstra) search over a graph of portal endpoints + START + GOAL and returns
// an ordered list of walk legs, each optionally ending in "use a portal".

/// <summary>One directed portal: walk to (SrcNs,SrcEw), teleport, arrive (DstNs,DstEw).</summary>
public readonly struct PortalLink
{
    public readonly double SrcNs, SrcEw, DstNs, DstEw;
    public readonly string Name;
    public PortalLink(double srcNs, double srcEw, double dstNs, double dstEw, string name)
    { SrcNs = srcNs; SrcEw = srcEw; DstNs = dstNs; DstEw = dstEw; Name = name; }
}

/// <summary>
/// A recall: usable from anywhere, lands at a fixed spot. CostUnits is what taking it
/// costs the planner (PortalRoute.RecallCostUnits of its cast time), so a slow recall
/// is only taken when it saves more walking. (Recall-aware routing is GoArrow's idea:
/// Digero 2006, Virindi 2011, MIT.)
/// </summary>
public readonly struct RecallLink
{
    public readonly double DstNs, DstEw;
    public readonly string Name;
    public readonly double CostUnits;
    public RecallLink(double dstNs, double dstEw, string name) : this(dstNs, dstEw, name, PortalRoute.RecallPenaltyUnits) { }
    public RecallLink(double dstNs, double dstEw, string name, double costUnits)
    { DstNs = dstNs; DstEw = dstEw; Name = name; CostUnits = costUnits; }
}

/// <summary>
/// A portal hub such as the Town Network: entry portals on the map that land you at an arrival
/// point inside, a walk inside from each arrival to each exit portal, and exit portals that lead
/// back onto the map. The planner adds it as entry -> arrival -> exit edges (about entries +
/// arrivals x exits + exits), not as every entry x exit pair.
/// </summary>
public sealed class HubLinks
{
    public string Name = "";
    /// <summary>How many arrival points there are inside (HubEntry.Arrival and HubExit.WalkUnits index them).</summary>
    public int Arrivals;
    public readonly List<HubEntry> Entries = new();
    public readonly List<HubExit> Exits = new();
}

/// <summary>A hub's entry portal on the map, and the arrival point inside it lands you on.</summary>
public readonly struct HubEntry
{
    public readonly double SrcNs, SrcEw;
    public readonly int Arrival;
    public readonly string Name;
    public HubEntry(double srcNs, double srcEw, int arrival, string name) { SrcNs = srcNs; SrcEw = srcEw; Arrival = arrival; Name = name; }
}

/// <summary>
/// A hub's exit portal: where it lands you on the map, and the walk to it from each arrival point
/// (units; NaN = no walk). Id is the caller's own number for it (RouteStep.HubExit).
/// </summary>
public readonly struct HubExit
{
    public readonly double DstNs, DstEw;
    public readonly string Name;
    public readonly int Id;
    public readonly double[] WalkUnits;
    public HubExit(double dstNs, double dstEw, string name, int id, double[] walkUnits)
    { DstNs = dstNs; DstEw = dstEw; Name = name; Id = id; WalkUnits = walkUnits; }
}

/// <summary>One leg of a route: walk to (Ns,Ew); if UsePortal, trigger the portal on arrival.</summary>
public readonly struct RouteStep
{
    public readonly double Ns, Ew;
    public readonly bool UsePortal;   // arriving here, walk into the portal and wait for teleport
    public readonly bool UseRecall;   // cast a recall here instead of walking into a portal
    public readonly string Label;
    /// <summary>A recall step: its index in the recalls passed to Plan (else -1).</summary>
    public readonly int RecallIndex;
    /// <summary>Where this leg's portal or recall lands you (NaN for a walk leg).</summary>
    public readonly double LandNs, LandEw;
    private readonly int _hubExit1;   // HubExit + 1, so default(RouteStep) has none
    /// <summary>
    /// A hub step (the Town Network): inside the hub, walk to this exit (HubExit.Id) and take it.
    /// Ns/Ew and LandNs/LandEw are where the exit lands you. -1 = not a hub step.
    /// </summary>
    public int HubExit => _hubExit1 - 1;
    public bool IsHub => _hubExit1 > 0;
    public RouteStep(double ns, double ew, bool usePortal, bool useRecall, string label)
        : this(ns, ew, usePortal, useRecall, label, -1, double.NaN, double.NaN) { }
    public RouteStep(double ns, double ew, bool usePortal, bool useRecall, string label, int recallIndex, double landNs, double landEw)
        : this(ns, ew, usePortal, useRecall, label, recallIndex, landNs, landEw, -1) { }
    public RouteStep(double ns, double ew, bool usePortal, bool useRecall, string label, int recallIndex, double landNs, double landEw, int hubExit)
    { Ns = ns; Ew = ew; UsePortal = usePortal; UseRecall = useRecall; Label = label; RecallIndex = recallIndex; LandNs = landNs; LandEw = landEw; _hubExit1 = hubExit + 1; }

    /// <summary>A hub step: walk inside to exit <paramref name="hubExit"/> and take it to (landNs, landEw).</summary>
    public static RouteStep Hub(double landNs, double landEw, string label, int hubExit)
        => new(landNs, landEw, false, false, label, -1, landNs, landEw, hubExit);
}

public static class PortalRoute
{
    public const double UnitsPerDegree = 240.0;

    // Tuning. A portal/recall "costs" this many world units of equivalent effort,
    // so the planner only takes one when it saves more walking than the penalty.
    public const double PortalPenaltyUnits = 360.0;   // ~1.5 deg
    public const double RecallPenaltyUnits = 1200.0;  // a recall with no cast time given; ~5 deg
    // Running speed used to turn seconds into units, and what any teleport costs on top
    // of its cast (portal space, the landing). RecallCostUnits = overhead + cast time.
    public const double RunUnitsPerSecond = 10.0;
    public const double TeleportOverheadUnits = PortalPenaltyUnits;

    /// <summary>The planner cost of a recall that takes <paramref name="castSeconds"/> to cast.</summary>
    public static double RecallCostUnits(double castSeconds) => TeleportOverheadUnits + Math.Max(0, castSeconds) * RunUnitsPerSecond;
    // Only chain one portal's exit to another portal's entrance if they're within
    // this far apart (same town/area). START->any-portal and any-portal->GOAL are
    // always allowed so the graph can't disconnect.
    public const double ChainWalkRadiusUnits = 4000.0; // ~16.7 deg

    private static double Dist(double aNs, double aEw, double bNs, double bEw)
    {
        double dn = (aNs - bNs) * UnitsPerDegree, de = (aEw - bEw) * UnitsPerDegree;
        return Math.Sqrt(dn * dn + de * de);
    }

    private enum EdgeKind { Walk, Portal, Recall, HubIn, HubOut }
    private readonly struct Edge { public readonly int To; public readonly double Cost; public readonly EdgeKind Kind; public readonly string Label; public readonly int Index;
        public Edge(int to, double cost, EdgeKind kind, string label, int index = -1) { To = to; Cost = cost; Kind = kind; Label = label; Index = index; } }

    /// <summary>
    /// Plan a route from start to goal. Returns the ordered walk legs. The first leg(s)
    /// may be portal/recall hops; the final leg is always a plain walk to the goal.
    /// estUnits = total estimated cost; portalsUsed = how many teleports the plan takes.
    /// </summary>
    public static List<RouteStep> Plan(
        IReadOnlyList<PortalLink> portals,
        IReadOnlyList<RecallLink>? recalls,
        double startNs, double startEw,
        double goalNs, double goalEw,
        out double estUnits, out int portalsUsed)
        => Plan(portals, recalls, null, startNs, startEw, goalNs, goalEw, out estUnits, out portalsUsed, null);

    /// <summary>
    /// As above, with a portal hub (the Town Network): its entry portals are walked to like any
    /// portal's entrance (and chained to from portal exits and recall landings), each lands at its
    /// arrival point, an arrival reaches each exit for the walk inside plus a teleport, and an
    /// exit's landing walks on or chains like any portal exit. A hub trip is a portal step to the
    /// entry followed by a hub step (RouteStep.IsHub) for the exit.
    /// <paramref name="canWalk"/> (fromNs, fromEw, toNs, toEw): false for a walk that can't be done
    /// over land (an island, the far side of water), so the plan never walks it; null = every walk
    /// is possible. With no possible plan the result is empty and estUnits is +infinity.
    /// <paramref name="startOnMap"/> false: the start is in a dungeon (its coordinates aren't map
    /// coordinates), so nothing walks from it; only a recall leaves it.
    /// </summary>
    public static List<RouteStep> Plan(
        IReadOnlyList<PortalLink> portals,
        IReadOnlyList<RecallLink>? recalls,
        HubLinks? hub,
        double startNs, double startEw,
        double goalNs, double goalEw,
        out double estUnits, out int portalsUsed,
        Func<double, double, double, double, bool>? canWalk = null,
        bool startOnMap = true)
    {
        recalls ??= Array.Empty<RecallLink>();
        int hubEntries = hub?.Entries.Count ?? 0, hubArrivals = hub?.Arrivals ?? 0, hubExits = hub?.Exits.Count ?? 0;

        // Node layout: 0=START, 1=GOAL, then per-portal [Src,Dst] pairs, then recall dsts, then the
        // hub's entries, arrivals (inside: no map position) and exits' landings.
        int recallBase = 2 + portals.Count * 2;
        int entryBase = recallBase + recalls.Count, arrivalBase = entryBase + hubEntries, exitBase = arrivalBase + hubArrivals;
        int n = exitBase + hubExits;
        var ns = new double[n];
        var ew = new double[n];
        ns[0] = startNs; ew[0] = startEw;
        ns[1] = goalNs; ew[1] = goalEw;
        for (int i = 0; i < portals.Count; i++)
        {
            int s = 2 + i * 2, d = s + 1;
            ns[s] = portals[i].SrcNs; ew[s] = portals[i].SrcEw;
            ns[d] = portals[i].DstNs; ew[d] = portals[i].DstEw;
        }
        for (int i = 0; i < recalls.Count; i++) { ns[recallBase + i] = recalls[i].DstNs; ew[recallBase + i] = recalls[i].DstEw; }
        for (int i = 0; i < hubEntries; i++) { ns[entryBase + i] = hub!.Entries[i].SrcNs; ew[entryBase + i] = hub.Entries[i].SrcEw; }
        for (int i = 0; i < hubArrivals; i++) { ns[arrivalBase + i] = double.NaN; ew[arrivalBase + i] = double.NaN; }
        for (int i = 0; i < hubExits; i++) { ns[exitBase + i] = hub!.Exits[i].DstNs; ew[exitBase + i] = hub.Exits[i].DstEw; }

        var adj = new List<Edge>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<Edge>();
        void AddWalk(int from, int to, double cost)
        {
            if (from == 0 && !startOnMap) return;   // in a dungeon: no walking from where you stand
            if (canWalk == null || canWalk(ns[from], ew[from], ns[to], ew[to]))
                adj[from].Add(new Edge(to, cost, EdgeKind.Walk, "walk"));
        }

        // Direct walk fallback START -> GOAL.
        AddWalk(0, 1, Dist(startNs, startEw, goalNs, goalEw));

        for (int i = 0; i < portals.Count; i++)
        {
            int s = 2 + i * 2, d = s + 1;
            string name = portals[i].Name;
            // The teleport itself.
            adj[s].Add(new Edge(d, PortalPenaltyUnits, EdgeKind.Portal, name));
            // Walk from START to this portal entrance, and from this portal exit to GOAL.
            AddWalk(0, s, Dist(startNs, startEw, ns[s], ew[s]));
            AddWalk(d, 1, Dist(ns[d], ew[d], goalNs, goalEw));
        }

        // Chain portals: walk from one exit to a nearby other entrance.
        for (int i = 0; i < portals.Count; i++)
        {
            int di = 3 + i * 2;
            for (int j = 0; j < portals.Count; j++)
            {
                if (i == j) continue;
                int sj = 2 + j * 2;
                double w = Dist(ns[di], ew[di], ns[sj], ew[sj]);
                if (w <= ChainWalkRadiusUnits) AddWalk(di, sj, w);
            }
        }

        // Recalls: from START (anywhere) to a fixed landing, then walk/chain like an exit.
        for (int i = 0; i < recalls.Count; i++)
        {
            int r = recallBase + i;
            adj[0].Add(new Edge(r, recalls[i].CostUnits, EdgeKind.Recall, recalls[i].Name, i));
            AddWalk(r, 1, Dist(ns[r], ew[r], goalNs, goalEw));
            for (int j = 0; j < portals.Count; j++)
            {
                int sj = 2 + j * 2;
                double w = Dist(ns[r], ew[r], ns[sj], ew[sj]);
                if (w <= ChainWalkRadiusUnits) AddWalk(r, sj, w);
            }
        }

        // The hub: walk to an entry (from the start, a portal's exit or a recall's landing), its
        // teleport to the arrival inside, the walk inside plus the exit's teleport, then on from
        // the exit's landing like any portal exit.
        if (hub != null)
        {
            for (int e = 0; e < hubEntries; e++)
            {
                int en = entryBase + e;
                HubEntry he = hub.Entries[e];
                AddWalk(0, en, Dist(startNs, startEw, he.SrcNs, he.SrcEw));
                for (int i = 0; i < portals.Count; i++)
                {
                    int di = 3 + i * 2;
                    double w = Dist(ns[di], ew[di], he.SrcNs, he.SrcEw);
                    if (w <= ChainWalkRadiusUnits) AddWalk(di, en, w);
                }
                for (int i = 0; i < recalls.Count; i++)
                {
                    int r = recallBase + i;
                    double w = Dist(ns[r], ew[r], he.SrcNs, he.SrcEw);
                    if (w <= ChainWalkRadiusUnits) AddWalk(r, en, w);
                }
                if (he.Arrival >= 0 && he.Arrival < hubArrivals)
                    adj[en].Add(new Edge(arrivalBase + he.Arrival, PortalPenaltyUnits, EdgeKind.HubIn, he.Name));
            }
            for (int x = 0; x < hubExits; x++)
            {
                int xn = exitBase + x;
                HubExit hx = hub.Exits[x];
                for (int a = 0; a < hubArrivals && a < hx.WalkUnits.Length; a++)
                {
                    double walk = hx.WalkUnits[a];
                    if (double.IsNaN(walk) || walk < 0) continue;
                    adj[arrivalBase + a].Add(new Edge(xn, walk + PortalPenaltyUnits, EdgeKind.HubOut, hx.Name, hx.Id));
                }
                AddWalk(xn, 1, Dist(hx.DstNs, hx.DstEw, goalNs, goalEw));
                for (int j = 0; j < portals.Count; j++)
                {
                    int sj = 2 + j * 2;
                    double w = Dist(hx.DstNs, hx.DstEw, ns[sj], ew[sj]);
                    if (w <= ChainWalkRadiusUnits) AddWalk(xn, sj, w);
                }
            }
        }

        // Dijkstra (uniform-cost; optimal with positive edge costs).
        var dist = new double[n];
        var prev = new int[n];
        var prevKind = new EdgeKind[n];
        var prevLabel = new string[n];
        var prevIndex = new int[n];
        var done = new bool[n];
        for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; prev[i] = -1; }
        dist[0] = 0;
        var pq = new SortedSet<(double cost, int node)>();
        pq.Add((0, 0));
        while (pq.Count > 0)
        {
            var (cu, u) = pq.Min; pq.Remove(pq.Min);
            if (done[u]) continue;
            done[u] = true;
            if (u == 1) break;
            foreach (var e in adj[u])
            {
                if (done[e.To]) continue;
                double nd = cu + e.Cost;
                if (nd < dist[e.To])
                {
                    if (!double.IsPositiveInfinity(dist[e.To])) pq.Remove((dist[e.To], e.To));
                    dist[e.To] = nd; prev[e.To] = u; prevKind[e.To] = e.Kind; prevLabel[e.To] = e.Label; prevIndex[e.To] = e.Index;
                    pq.Add((nd, e.To));
                }
            }
        }

        estUnits = dist[1];
        portalsUsed = 0;
        var steps = new List<RouteStep>();
        if (double.IsPositiveInfinity(dist[1])) return steps; // unreachable (shouldn't happen — direct walk always exists)

        // Rebuild node path START..GOAL.
        var nodes = new List<int>();
        for (int v = 1; v != -1; v = prev[v]) nodes.Add(v);
        nodes.Reverse();

        // Convert node hops into walk legs, folding portal/recall edges into the
        // preceding walk leg's "use on arrival" flag.
        for (int k = 1; k < nodes.Count; k++)
        {
            int to = nodes[k];
            EdgeKind kind = prevKind[to];
            if (kind == EdgeKind.Walk)
            {
                steps.Add(new RouteStep(ns[to], ew[to], false, false, prevLabel[to]));
            }
            else if (kind == EdgeKind.HubIn)
            {
                portalsUsed++;
                // Into the hub: the previous walk leg ends at the entry portal; take it. Where it
                // lands (inside) has no map position.
                if (steps.Count > 0)
                    steps[steps.Count - 1] = new RouteStep(steps[^1].Ns, steps[^1].Ew, true, false, prevLabel[to]);
            }
            else if (kind == EdgeKind.HubOut)
            {
                portalsUsed++;
                steps.Add(RouteStep.Hub(ns[to], ew[to], prevLabel[to], prevIndex[to]));
            }
            else if (kind == EdgeKind.Portal)
            {
                portalsUsed++;
                // 'to' is a portal Dst reached by teleport; the entrance is the prior node,
                // which the previous walk leg already targets. Flag that leg as a portal use.
                if (steps.Count > 0)
                    steps[steps.Count - 1] = new RouteStep(steps[^1].Ns, steps[^1].Ew, true, false, prevLabel[to], -1, ns[to], ew[to]);
            }
            else // Recall
            {
                portalsUsed++;
                // Recall is cast from START (current position); insert a recall step at START.
                steps.Add(new RouteStep(startNs, startEw, false, true, prevLabel[to], prevIndex[to], ns[to], ew[to]));
            }
        }
        return steps;
    }
}
