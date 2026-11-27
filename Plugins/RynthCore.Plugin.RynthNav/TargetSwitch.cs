using System;

namespace RynthCore.Plugin.RynthNav;

/// <summary>What asks for a new target: the arrow (chat coords, the Atlas, /rnav arrow), a planned
/// route (/rnav route, Plan), or /rnav clear (the arrow window's x).</summary>
internal enum NewTarget { Arrow, Plan, Clear }

/// <summary>
/// What RynthNav holds when a new target comes: the route shown in the panels (a plan, or the
/// trip's own route), and the trip being walked (paused counts).
/// </summary>
internal readonly record struct HeldTarget(
    bool HasPlan, string PlanFor, double PlanNs, double PlanEw,
    bool Walking, string TripFor, double TripNs, double TripEw);

/// <summary>
/// What the new target does to the old one. DropPlan: forget the shown route. StopTrip: end the
/// walk. KeepArrow: leave the arrow as it is (the trip's own arrow, chained along its route).
/// </summary>
internal readonly record struct TargetChange(bool DropPlan, bool StopTrip, bool KeepArrow)
{
    public static readonly TargetChange None = new(false, false, false);
}

/// <summary>
/// One target at a time, the newest wins (11-24: the arrow window showed the clicked chat
/// coordinates on top and an older route under them). A new target elsewhere replaces the shown
/// route and ends a walk to somewhere else; the same place again leaves them be. Pure: the
/// offline tests compile this file.
/// </summary>
internal static class TargetSwitch
{
    /// <summary>Two targets this close are the same place (chat coordinates have one decimal: 0.05 degrees is 12 u).</summary>
    public const double SamePlaceUnits = 15.0;

    /// <summary>By position only: two places with one name ("Lifestone") are not the same place.</summary>
    public static bool SamePlace(double nsA, double ewA, double nsB, double ewB) =>
        !double.IsNaN(nsA) && !double.IsNaN(ewA) && !double.IsNaN(nsB) && !double.IsNaN(ewB)
        && NavCoords.Distance(nsA, ewA, nsB, ewB) <= SamePlaceUnits;

    public static TargetChange For(NewTarget what, in HeldTarget held, double ns = double.NaN, double ew = double.NaN)
    {
        if (what == NewTarget.Clear) return new TargetChange(held.HasPlan, held.Walking, false);
        if (held.Walking)
        {
            // Going there already: the trip (and its arrow, along its portals) stays.
            if (SamePlace(ns, ew, held.TripNs, held.TripEw)) return new TargetChange(false, false, true);
            // Somewhere else: the walk ends, its route with it (a plan makes its own).
            return new TargetChange(what == NewTarget.Arrow, true, false);
        }
        // A plan replaces the shown route itself; the arrow drops one that is for another place.
        if (what == NewTarget.Arrow && held.HasPlan && !SamePlace(ns, ew, held.PlanNs, held.PlanEw))
            return new TargetChange(true, false, false);
        return TargetChange.None;
    }
}
