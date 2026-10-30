using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// Bug 5 (10-01): in a dungeon the panel planned from "0,0" and walked to a portal at 86S; and the
// Route button planned with portals while "Use portals and recalls" was off.
internal static partial class Program
{
    private static void RunTravelPlan()
    {
        Run(nameof(DungeonStartBeginsWithARecall), DungeonStartBeginsWithARecall);
        Run(nameof(DungeonStartRefusals), DungeonStartRefusals);
        Run(nameof(PortalsOffMeansWalkingOnly), PortalsOffMeansWalkingOnly);
        Run(nameof(RecallsSwitch), RecallsSwitch);
    }

    private static readonly List<PortalLink> NearPortal = new() { new(0.1, 0, 39.9, 40, "Handy Portal") };
    private static readonly List<RecallLink> OneRecall = new() { new(39.5, 39.5, "Some Recall", PortalRoute.RecallCostUnits(5)) };

    private static void DungeonStartBeginsWithARecall()
    {
        // A dungeon's coordinates look like 0,0 on the map: right beside "Handy Portal". It must
        // not walk there; it recalls out.
        var r = TravelPlan.Plan(NearPortal, OneRecall, null, true, true, startOnMap: false, 0, 0, 40, 40, null, null, "Goal");
        Check(r.Refusal == "" && r.Steps != null && r.Steps.Count >= 1 && r.Steps[0].UseRecall, "in a dungeon the route starts with a recall");
        Check(r.Steps != null && !r.Steps.Exists(s => s.UsePortal && s.Label == "Handy Portal"), "never a walk from the dungeon to a portal");

        // The real data: in a dungeon, knowing Recall Aphus Lassel, to Arwic.
        var (t, atlas, portals) = RealTownNetData();
        if (t == null || atlas == null || portals == null) { Console.WriteLine("     (no generated NavData beside this repo: real part skipped)"); return; }
        var arwic = atlas.FindBest("Arwic", false, 0, 0)!;
        var recalls = RecallPlanner.Links(RecallPlanner.Build(atlas, new HashSet<int> { 2931, RecallPlanner.LifestoneRecall }, new RecallMemory(), "Aelrynth|Lucy", null));
        var real = TravelPlan.Plan(portals, recalls, t.Links(100, false, null), true, true, startOnMap: false, -86.2, -0.7, arwic.Ns, arwic.Ew, null, null, "Arwic");
        Console.WriteLine($"     in a dungeon -> Arwic: ~{real.Est:F0} via {(real.Steps == null ? "walk" : Describe(real.Steps))}");
        Check(real.Steps != null && real.Steps.Count >= 3 && real.Steps[0].UseRecall && real.Steps[0].Label == "Recall Aphus Lassel"
              && real.Steps[1].UsePortal && real.Steps[2].IsHub && real.Steps[2].HubExit == ExitIndex(t, "Arwic"),
            "from a dungeon: Recall Aphus Lassel, the Town Network portal beside it, the Portal to Arwic");
    }

    private static void DungeonStartRefusals()
    {
        var off = TravelPlan.Plan(NearPortal, OneRecall, null, false, true, startOnMap: false, 0, 0, 40, 40, null, null, "Goal");
        Check(off.Steps == null && off.Refusal.Contains("dungeon") && off.Refusal.Contains("Use portals and recalls"), "portals off: refuses, says to turn them on (" + off.Refusal + ")");
        var noRecalls = TravelPlan.Plan(NearPortal, OneRecall, null, true, false, startOnMap: false, 0, 0, 40, 40, null, null, "Goal");
        Check(noRecalls.Steps == null && noRecalls.Refusal.Contains("turn on Recalls"), "recalls off: refuses, says to turn Recalls on (" + noRecalls.Refusal + ")");
        var none = TravelPlan.Plan(NearPortal, new List<RecallLink>(), null, true, true, startOnMap: false, 0, 0, 40, 40, null, null, "Goal");
        Check(none.Steps == null && none.Refusal.Contains("no recall"), "no recall ready: refuses (" + none.Refusal + ")");
    }

    private static void PortalsOffMeansWalkingOnly()
    {
        // The portal would save a lot; with the switch off the plan still walks.
        var on = TravelPlan.Plan(NearPortal, OneRecall, null, true, true, true, 0, 0, 40, 40, null, true, "Goal");
        Check(on.Steps != null && on.Teleports > 0, "switch on: the portal (or recall) is used");
        var off = TravelPlan.Plan(NearPortal, OneRecall, null, false, true, true, 0, 0, 40, 40, null, true, "Goal");
        Check(off.Steps == null && off.Teleports == 0 && off.Refusal == "", "switch off: walking only, no portal or recall steps");
        Check(Math.Abs(off.Est - NavCoords.Distance(0, 0, 40, 40)) < 1e-6, "its cost is the walk");
        // Off, and the target is across water: say so instead of walking into the sea.
        var island = TravelPlan.Plan(NearPortal, OneRecall, null, false, true, true, 0, 0, 40, 40, null, false, "Goal");
        Check(island.Steps == null && island.Refusal.Contains("another landmass") && island.Refusal.Contains("Use portals and recalls"), "off, across water: refuses with the switch to turn on");
    }

    private static void RecallsSwitch()
    {
        var recallOnly = new List<PortalLink>();
        var withRecalls = TravelPlan.Plan(recallOnly, OneRecall, null, true, true, true, 0, 0, 40, 40, null, true, "Goal");
        Check(withRecalls.Steps != null && withRecalls.Steps[0].UseRecall, "Recalls on: the recall is used");
        var noRecalls = TravelPlan.Plan(recallOnly, OneRecall, null, true, false, true, 0, 0, 40, 40, null, true, "Goal");
        Check(noRecalls.Steps == null && noRecalls.Refusal == "", "Recalls off: walks (no recall step)");
        var portalsStill = TravelPlan.Plan(NearPortal, OneRecall, null, true, false, true, 0, 0, 40, 40, null, true, "Goal");
        Check(portalsStill.Steps != null && !portalsStill.Steps.Exists(s => s.UseRecall) && portalsStill.Steps.Exists(s => s.UsePortal), "Recalls off: portals still used");
    }
}
