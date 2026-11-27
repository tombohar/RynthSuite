using System;
using RynthCore.Plugin.RynthNav;

namespace RynthNav.RouteTests;

// 11-24, a player, on clicking coordinates in chat: "It puts it into the top part of this arrow
// window. The bottom part follows the rynthnav thing though." One target at a time, the newest wins
// (TargetSwitch): what a new arrow, plan or clear does to the route shown and the trip walked.
internal static partial class Program
{
    private static void RunTargetSwitch()
    {
        Run(nameof(TargetSamePlace), TargetSamePlace);
        Run(nameof(TargetArrowOverAPlan), TargetArrowOverAPlan);
        Run(nameof(TargetArrowWhileWalking), TargetArrowWhileWalking);
        Run(nameof(TargetPlanWhileWalking), TargetPlanWhileWalking);
        Run(nameof(TargetClear), TargetClear);
    }

    // Holtburg's lifestone-ish spot, and a place far from it.
    private const double HoltNs = 42.08, HoltEw = 33.60, FarNs = -12.3, FarEw = 45.6;

    private static HeldTarget Nothing => new(false, "", double.NaN, double.NaN, false, "", double.NaN, double.NaN);
    private static HeldTarget PlanTo(double ns, double ew) => new(true, "Holtburg", ns, ew, false, "", double.NaN, double.NaN);
    private static HeldTarget WalkingTo(double ns, double ew) => new(true, "Holtburg", ns, ew, true, "Holtburg", ns, ew);

    private static void TargetSamePlace()
    {
        Check(TargetSwitch.SamePlace(HoltNs, HoltEw, HoltNs, HoltEw), "a place is itself");
        // Chat coordinates have one decimal: 42.1N, 33.6E is Holtburg's 42.08N, 33.60E.
        Check(TargetSwitch.SamePlace(42.1, 33.6, HoltNs, HoltEw), "rounded chat coords are the same place");
        Check(!TargetSwitch.SamePlace(42.3, 33.6, HoltNs, HoltEw), "0.2 degrees away is another place (53 u)");
        Check(!TargetSwitch.SamePlace(FarNs, FarEw, HoltNs, HoltEw), "far away is another place");
        Check(!TargetSwitch.SamePlace(double.NaN, double.NaN, HoltNs, HoltEw), "an unknown spot is never the same place");
        Check(!TargetSwitch.SamePlace(HoltNs, HoltEw, double.NaN, 33.6), "nor is a route with no goal known");
    }

    private static void TargetArrowOverAPlan()
    {
        // The report: a route was planned to one place, then chat coords elsewhere were clicked.
        TargetChange c = TargetSwitch.For(NewTarget.Arrow, PlanTo(HoltNs, HoltEw), FarNs, FarEw);
        Check(c.DropPlan, "an arrow elsewhere drops the old plan (no second target under it)");
        Check(!c.StopTrip && !c.KeepArrow, "nothing walking: nothing to stop, the arrow moves");

        c = TargetSwitch.For(NewTarget.Arrow, PlanTo(HoltNs, HoltEw), 42.1, 33.6);
        Check(c == TargetChange.None, "an arrow at the planned place keeps the plan (it is the same target)");

        c = TargetSwitch.For(NewTarget.Arrow, Nothing, FarNs, FarEw);
        Check(c == TargetChange.None, "an arrow with nothing before it changes nothing else");

        // A plan whose goal isn't known (an older plan): another target can't be shown to match it.
        c = TargetSwitch.For(NewTarget.Arrow, PlanTo(double.NaN, double.NaN), HoltNs, HoltEw);
        Check(c.DropPlan, "a plan with no known goal goes when the arrow is set");

        // A plan replaces the shown route by itself: nothing to drop first.
        c = TargetSwitch.For(NewTarget.Plan, PlanTo(HoltNs, HoltEw), FarNs, FarEw);
        Check(c == TargetChange.None, "a new plan over an old plan: the plan replaces it");
    }

    private static void TargetArrowWhileWalking()
    {
        TargetChange c = TargetSwitch.For(NewTarget.Arrow, WalkingTo(HoltNs, HoltEw), FarNs, FarEw);
        Check(c.StopTrip, "an arrow elsewhere ends the walk (the newest target wins)");
        Check(c.DropPlan, "...and the walk's route goes with it");
        Check(!c.KeepArrow, "...and the arrow moves to the new place");

        c = TargetSwitch.For(NewTarget.Arrow, WalkingTo(HoltNs, HoltEw), 42.1, 33.6);
        Check(!c.StopTrip && !c.DropPlan, "the arrow at the place being walked to keeps the walk");
        Check(c.KeepArrow, "...and keeps the trip's own arrow (along its portals)");
    }

    private static void TargetPlanWhileWalking()
    {
        TargetChange c = TargetSwitch.For(NewTarget.Plan, WalkingTo(HoltNs, HoltEw), FarNs, FarEw);
        Check(c.StopTrip && !c.KeepArrow, "planning elsewhere ends the walk; the arrow follows the plan");
        Check(!c.DropPlan, "the plan replaces the walk's route itself");

        c = TargetSwitch.For(NewTarget.Plan, WalkingTo(HoltNs, HoltEw), HoltNs, HoltEw);
        Check(!c.StopTrip && c.KeepArrow, "planning the place being walked to: the walk and its arrow go on");
    }

    private static void TargetClear()
    {
        TargetChange c = TargetSwitch.For(NewTarget.Clear, WalkingTo(HoltNs, HoltEw));
        Check(c.StopTrip && c.DropPlan && !c.KeepArrow, "clear while walking: stop, drop the route, arrow off");
        c = TargetSwitch.For(NewTarget.Clear, PlanTo(HoltNs, HoltEw));
        Check(!c.StopTrip && c.DropPlan, "clear with a plan: the plan goes");
        Check(TargetSwitch.For(NewTarget.Clear, Nothing) == TargetChange.None, "clear with nothing: nothing to do");
    }
}
