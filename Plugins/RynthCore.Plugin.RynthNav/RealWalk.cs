using System;
using System.Collections.Generic;
using System.Diagnostics;
using RynthCore.Plugin.RynthNav;

namespace RynthNav.Routing;

/// <summary>
/// Walking distances for the trip planner (PortalRoute's walkCost, 11-19): between two map points,
/// along the route graph (NavGraph.FindRoute), the way the walker will go round a ridge or a bay.
/// Each point's ground and region come from its tile file (NavCoarse.ProbeFile), cached for the plan;
/// the start's at the player's height, the rest the land under the point. NaN (the planner keeps the
/// straight line) when a point has no tile or ground, both ends are in one region (straight is near
/// enough there), the graph finds no route, or the plan has spent its time on it: the best way's walks
/// may take <see cref="BestBudgetMs"/>, the other ways' (after <see cref="BestPlanned"/>) a further
/// <see cref="OthersBudgetMs"/>. 11-20 live, one 60 ms budget for the lot: the first walk after a load (cold
/// tile files) or the 39,000 u walk-all-the-way spent it, and the best way kept its straight lines.
/// One per plan, on the thread that plans (the tick). Host-free: the probes use it too.
/// </summary>
internal sealed class RealWalk
{
    /// <summary>Time one plan may spend costing the best way's walks; after it, the rest keep the straight line.</summary>
    public const double BestBudgetMs = 150;
    /// <summary>Time on top for the other ways' walks (PlanOptions, after the best way).</summary>
    public const double OthersBudgetMs = 60;
    /// <summary>A route graph search gives up after this many expansions (the walker's own allows 2,000,000).</summary>
    public const int MaxExpand = 400_000;
    /// <summary>How far from a point its ground may be (a portal on a ledge, a place in a building).</summary>
    public const float GroundReach = 24f;

    private readonly NavGraph _g;
    private readonly string _dir;
    private readonly double _startNs, _startEw, _startUp, _othersMs;
    private double _limitMs;
    private bool _bestDone;
    private readonly Dictionary<(int X, int Y, int From), NavCoarse.Probe?> _probes = new();
    private readonly Stopwatch _clock = new();

    /// <summary>Walks asked about, and how many of those the route graph costed.</summary>
    public int Asked, Costed;
    public double Ms => _clock.Elapsed.TotalMilliseconds;
    /// <summary>Time spent on the best way's walks (all of it until <see cref="BestPlanned"/>).</summary>
    public double BestMs { get; private set; } = double.NaN;

    /// <param name="startUp">The player's height at the start (NaN: the land there, like any other point).</param>
    /// <param name="budgetMs">Time for the best way's walks (tools: +infinity).</param>
    /// <param name="othersMs">Time on top for the other ways' walks.</param>
    public RealWalk(NavGraph g, string navDataDir, double startNs, double startEw, double startUp,
                    double budgetMs = BestBudgetMs, double othersMs = OthersBudgetMs)
    {
        _g = g; _dir = navDataDir; _startNs = startNs; _startEw = startEw; _startUp = startUp; _limitMs = budgetMs; _othersMs = othersMs;
    }

    /// <summary>The best way is planned: the other ways' walks get <see cref="OthersBudgetMs"/> more (PortalRoute's bestPlanned).</summary>
    public void BestPlanned()
    {
        if (_bestDone) return;
        _bestDone = true;
        BestMs = Ms;
        if (!double.IsPositiveInfinity(_limitMs)) _limitMs = BestMs + _othersMs;
    }

    public double Cost(double fromNs, double fromEw, double toNs, double toEw)
    {
        Asked++;
        if (_clock.Elapsed.TotalMilliseconds > _limitMs) return double.NaN;
        _clock.Start();
        try
        {
            NavCoarse.Probe? a = Ground(fromNs, fromEw, -1);
            if (a == null) return double.NaN;
            NavCoarse.Probe? b = Ground(toNs, toEw, a.Region);
            if (b == null || b.Region == a.Region) return double.NaN;
            List<int>? route = _g.FindRoute(a.Region, a.Pt.X, a.Pt.Z, b.Region, b.Pt.X, b.Pt.Z, out float cost, out _, MaxExpand);
            if (route == null || !float.IsFinite(cost)) return double.NaN;
            Costed++;
            return cost;
        }
        catch (Exception) { return double.NaN; }   // a tile that won't read: the straight line
        finally { _clock.Stop(); }
    }

    /// <summary>The ground at a map point (a walk from <paramref name="from"/>'s region can end on, when given).</summary>
    private NavCoarse.Probe? Ground(double ns, double ew, int from)
    {
        double x = NavCoords.WorldX(ew), y = NavCoords.WorldY(ns);
        var key = ((int)Math.Round(x), (int)Math.Round(y), from);
        if (_probes.TryGetValue(key, out NavCoarse.Probe? p)) return p;
        bool isStart = Math.Abs(ns - _startNs) < 0.005 && Math.Abs(ew - _startEw) < 0.005;   // within about a unit
        p = isStart && double.IsFinite(_startUp)
            ? NavCoarse.ProbeFile(_g, _dir, x, _startUp, y, GroundReach, from, out _)
            : NavCoarse.ProbeFile(_g, _dir, x, 0, y, GroundReach, from, out _, fromBottom: true);
        _probes[key] = p;
        return p;
    }
}
