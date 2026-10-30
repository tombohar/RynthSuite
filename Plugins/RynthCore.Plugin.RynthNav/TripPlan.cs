using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// A point in RynthNav's world units: X = east, Y = north (landblock * 192 + local), H = height
/// (the pose z; NaN = not known).
/// </summary>
internal readonly record struct WorldPt(double X, double Y, double H)
{
    public static WorldPt FromMap(double ns, double ew, double h = double.NaN) => new(NavCoords.WorldX(ew), NavCoords.WorldY(ns), h);
    public double Ns => NavCoords.NsFromWorld(Y);
    public double Ew => NavCoords.EwFromWorld(X);
    public double FlatDistance(WorldPt o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    /// <summary>The landblock under the point (0xXXYY), or uint.MaxValue off the map.</summary>
    public uint Landblock
    {
        get
        {
            int x = (int)Math.Floor(X / 192.0), y = (int)Math.Floor(Y / 192.0);
            return x is >= 0 and <= 255 && y is >= 0 and <= 255 ? (uint)((x << 8) | y) : uint.MaxValue;
        }
    }
}

/// <summary>A recall of the plan, as the plan was made (RouteStep.RecallIndex indexes these).</summary>
internal readonly record struct TripRecall(string Name, int SpellId, string Command, double CastSeconds);

/// <summary>
/// One trip RynthNav planned (Route) or is walking (Go), frozen when it was planned: the steps,
/// where it started, the recalls it may cast and the portal a dungeon goto ends in. Never changed
/// after it's made, so the path builder (a worker), the markers and "save as nav route" all read
/// the same thing without locks. Host-free: the route tests build these.
///
/// What each step walks (<see cref="WalkFrom"/> to <see cref="Point"/>):
///   a walk step       from where the last step left you to its point;
///   a portal step     the same, its point being the portal's entrance (then the teleport);
///   a recall step     nothing: cast where you stand (its point is that spot);
///   a hub step        nothing on the map: the walk inside the Town Network (TownNet's walks).
/// After a portal or recall you stand at its landing (LandNs/LandEw); after a portal into a
/// dungeon or the Town Network there's no map position until a hub step or a recall.
/// </summary>
internal sealed class PlannedTrip
{
    /// <summary>A landblock this close to a leg (its centre, world units) counts as on the plan.</summary>
    public const double CoverUnits = 288.0;

    public readonly int Id;
    public readonly IReadOnlyList<RouteStep> Steps;
    public readonly string Goal;
    public readonly double GoalNs, GoalEw;
    public readonly bool StartOnMap;
    public readonly WorldPt Start;
    public readonly uint StartCell;
    public readonly IReadOnlyList<TripRecall> Recalls;
    /// <summary>The portal object the last step walks into (a dungeon goto); "" = none or not known.</summary>
    public readonly string EnterPortal;
    /// <summary>The dungeon (or portal) that last step goes into; "" = none.</summary>
    public readonly string EnterName;
    /// <summary>The running goto's plan (false: planned with Route, not walked).</summary>
    public readonly bool Live;

    /// <summary>Per step: where its walk starts (null = no walk: a recall, a hub step, or no map position).</summary>
    public readonly WorldPt?[] WalkFrom;
    /// <summary>Per step: where it happens (a walk's end, a portal's entrance, a recall's cast spot); null for a hub step.</summary>
    public readonly WorldPt?[] Point;

    public PlannedTrip(int id, IReadOnlyList<RouteStep> steps, string goal, double goalNs, double goalEw,
        bool startOnMap, WorldPt start, uint startCell, IReadOnlyList<TripRecall>? recalls,
        string enterPortal = "", string enterName = "", bool live = false)
    {
        Id = id;
        Steps = steps.Count == 0 ? new[] { new RouteStep(goalNs, goalEw, false, false, "walk") } : new List<RouteStep>(steps).ToArray();
        Goal = goal; GoalNs = goalNs; GoalEw = goalEw;
        StartOnMap = startOnMap; Start = start; StartCell = startCell;
        Recalls = recalls ?? Array.Empty<TripRecall>();
        EnterPortal = enterPortal ?? ""; EnterName = enterName ?? ""; Live = live;

        int n = Steps.Count;
        WalkFrom = new WorldPt?[n];
        Point = new WorldPt?[n];
        WorldPt? here = startOnMap ? start : null;
        for (int i = 0; i < n; i++)
        {
            RouteStep s = Steps[i];
            if (s.UseRecall)
            {
                // Cast where you stand: the trip's start for a first step (in a dungeon too).
                Point[i] = here ?? (i == 0 ? start : null);
                here = double.IsNaN(s.LandNs) ? null : WorldPt.FromMap(s.LandNs, s.LandEw);
                continue;
            }
            if (s.IsHub)
            {
                here = double.IsNaN(s.LandNs) ? null : WorldPt.FromMap(s.LandNs, s.LandEw);
                continue;
            }
            WorldPt to = WorldPt.FromMap(s.Ns, s.Ew);
            WalkFrom[i] = here;
            Point[i] = to;
            here = !s.UsePortal ? to : double.IsNaN(s.LandNs) ? null : WorldPt.FromMap(s.LandNs, s.LandEw);
        }
    }

    public int Count => Steps.Count;
    public bool HasHub { get { foreach (RouteStep s in Steps) if (s.IsHub) return true; return false; } }

    /// <summary>The last step is a portal into the place the goto was for (a dungeon).</summary>
    public bool EndsInEntrance => EnterName.Length > 0 && Steps.Count > 0 && Steps[^1].UsePortal;

    /// <summary>The portal object step <paramref name="i"/> walks into: its label, or for the dungeon entrance, the portal's own name.</summary>
    public string PortalName(int i)
    {
        if (i == Steps.Count - 1 && EndsInEntrance && EnterPortal.Length > 0) return EnterPortal;
        return Steps[i].Label ?? "";
    }

    /// <summary>The recall of step <paramref name="i"/>, or null when the plan doesn't know it.</summary>
    public TripRecall? RecallOf(int i)
    {
        int r = Steps[i].RecallIndex;
        return r >= 0 && r < Recalls.Count ? Recalls[r] : null;
    }

    /// <summary>
    /// Is <paramref name="landblock"/> on this trip: the start's own landblock, near one of its walks
    /// (the straight leg, or the path built for it), its steps or landings, or inside the Town
    /// Network on a trip that takes it? A teleport somewhere else isn't.
    /// </summary>
    public bool Covers(uint landblock, TripPath? path, Func<uint, bool>? inHub)
    {
        if (landblock == 0 || landblock == ((StartCell >> 16) & 0xFFFF)) return true;
        if (HasHub && inHub != null && inHub(landblock)) return true;
        double cx = ((landblock >> 8) & 0xFF) * 192.0 + 96.0, cy = (landblock & 0xFF) * 192.0 + 96.0;
        var c = new WorldPt(cx, cy, 0);
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Point[i] is WorldPt p && p.FlatDistance(c) <= CoverUnits) return true;
            if (WalkFrom[i] is WorldPt f && Point[i] is WorldPt t && SegmentDistance(c, f, t) <= CoverUnits) return true;
            RouteStep s = Steps[i];
            if (!double.IsNaN(s.LandNs) && WorldPt.FromMap(s.LandNs, s.LandEw).FlatDistance(c) <= CoverUnits) return true;
            WorldPt[]? w = path?.TripId == Id && i < path.Walks.Length ? path.Walks[i] : null;
            if (w != null)
                for (int k = 1; k < w.Length; k++)
                    if (SegmentDistance(c, w[k - 1], w[k]) <= CoverUnits) return true;
        }
        return false;
    }

    /// <summary>Flat distance from <paramref name="p"/> to the segment a→b.</summary>
    public static double SegmentDistance(WorldPt p, WorldPt a, WorldPt b) => SegmentDistance(p, a, b, out _);

    /// <summary>Flat distance from <paramref name="p"/> to the segment a→b, and how far along it the nearest point is (0..1).</summary>
    public static double SegmentDistance(WorldPt p, WorldPt a, WorldPt b, out double t)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, l2 = vx * vx + vy * vy;
        t = l2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / l2, 0, 1);
        double x = a.X + vx * t, y = a.Y + vy * t;
        return Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
    }

    /// <summary>
    /// The walk inside the Town Network for hub step <paramref name="i"/>, in world units (the
    /// network's landblock): from the arrival the entry portal before it lands on (or, for a trip
    /// that starts inside, from where it started) to the exit portal. Null, with a reason, when
    /// townnet.json doesn't have it.
    /// </summary>
    public List<WorldPt>? HubWalk(int i, TownNet? net, out string why)
    {
        why = "";
        if (net == null) { why = "the Town Network data (townnet.json) isn't loaded"; return null; }
        RouteStep s = Steps[i];
        int exit = s.HubExit;
        if (exit < 0 || exit >= net.Exits.Count) { why = "the Town Network exit isn't in townnet.json"; return null; }
        IReadOnlyList<TownNetPoint>? pts = null;
        if (i > 0 && Steps[i - 1].UsePortal)
        {
            // The entry portal walked into: the townnet.json entry at that spot, and its arrival.
            RouteStep e = Steps[i - 1];
            TownNetEntry? best = null;
            double bestD = 2.0;   // the plan's entry coordinates are the entry's own
            foreach (TownNetEntry en in net.Entries)
            {
                if (!en.OnMap) continue;
                double d = NavCoords.Distance(en.Ns, en.Ew, e.Ns, e.Ew);
                if (d < bestD) { bestD = d; best = en; }
            }
            if (best == null || best.Arrival < 0) { why = $"the entry portal {e.Label} isn't in townnet.json"; return null; }
            pts = net.Walk(best.Arrival, exit)?.Points;
            if (pts == null) { why = $"townnet.json has no walk from {best.Name}'s arrival to the {net.Exits[exit].Name}"; return null; }
        }
        else if (i == 0 && !StartOnMap && net.Contains((StartCell >> 16) & 0xFFFF))
        {
            double lx = Start.X - ((StartCell >> 24) & 0xFF) * 192.0, ly = Start.Y - ((StartCell >> 16) & 0xFF) * 192.0;
            pts = net.WayTo(exit, lx, ly, HubWalker.OffRouteUnits, out _);
            if (pts == null) { why = "no Town Network walk to that exit passes where the trip started"; return null; }
        }
        else { why = "the plan doesn't say how it gets into the Town Network"; return null; }

        var list = new List<WorldPt>(pts.Count);
        foreach (TownNetPoint p in pts)
            list.Add(new WorldPt(((p.Cell >> 24) & 0xFF) * 192.0 + p.X, ((p.Cell >> 16) & 0xFF) * 192.0 + p.Y, p.Z));
        return list;
    }

    /// <summary>Where the hub step's exit portal stands (world units), or null.</summary>
    public static WorldPt? HubExitPoint(TownNet? net, RouteStep s)
    {
        if (net == null || s.HubExit < 0 || s.HubExit >= net.Exits.Count) return null;
        TownNetExit x = net.Exits[s.HubExit];
        return new WorldPt(((x.Cell >> 24) & 0xFF) * 192.0 + x.X, ((x.Cell >> 16) & 0xFF) * 192.0 + x.Y, x.Z);
    }
}
