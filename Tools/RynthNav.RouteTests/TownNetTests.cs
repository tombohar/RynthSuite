using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// The Town Network: the planner's hub edges (made-up hubs and the real townnet.json), the
// restrictions (level, Olthoi), townnet.json's checks, the way inside, and the walker inside.
internal static partial class Program
{
    private static void RunTownNet()
    {
        Run(nameof(HubChosenWhenCheaper), HubChosenWhenCheaper);
        Run(nameof(HubSkippedWhenWalkingIsCheaper), HubSkippedWhenWalkingIsCheaper);
        Run(nameof(HubAfterAPortal), HubAfterAPortal);
        Run(nameof(TownNetFileChecks), TownNetFileChecks);
        Run(nameof(TownNetLevelAndOlthoi), TownNetLevelAndOlthoi);
        Run(nameof(HubStepInTheArrowAndEntrance), HubStepInTheArrowAndEntrance);
        Run(nameof(RealTownNetwork), RealTownNetwork);
        Run(nameof(RealTownNetworkLevelBlocks), RealTownNetworkLevelBlocks);
        Run(nameof(RealTownNetworkWays), RealTownNetworkWays);
        Run(nameof(AphusRecallThenTownNetworkToArwic), AphusRecallThenTownNetworkToArwic);
        Run(nameof(HubWalkerFollowsTheWalk), HubWalkerFollowsTheWalk);
        Run(nameof(HubWalkerStops), HubWalkerStops);
    }

    // A hub with one arrival: an entry beside (0, 0), an exit to (40, 40.1) after a 60 m walk.
    private static HubLinks FakeHub(double walk = 60)
    {
        var hub = new HubLinks { Name = "Town Network", Arrivals = 1 };
        hub.Entries.Add(new HubEntry(0.1, 0, 0, "Portal to Town Network"));
        hub.Exits.Add(new HubExit(40, 40.1, "Portal to Far Town", 7, new[] { walk }));
        return hub;
    }

    private static void HubChosenWhenCheaper()
    {
        var steps = PortalRoute.Plan(NoPortals, null, FakeHub(), 0, 0, 40, 40, out double est, out int used);
        Check(used == 2, "into the network and out: two teleports");
        Check(steps.Count == 3, "walk to the entry, the network, walk on");
        Check(steps[0].UsePortal && !steps[0].IsHub && steps[0].Label == "Portal to Town Network", "first: take the entry portal");
        Near(steps[0].Ns, 0.1, 1e-9, "the entry's position");
        Check(steps[1].IsHub && steps[1].HubExit == 7 && steps[1].Label == "Portal to Far Town", "then the exit, with its id");
        Near(steps[1].LandNs, 40, 1e-9, "the hub step knows where it lands");
        Check(!steps[2].UsePortal && !steps[2].IsHub, "last leg walks");
        double expected = 0.1 * 240 + PortalRoute.PortalPenaltyUnits + 60 + PortalRoute.PortalPenaltyUnits + 0.1 * 240;
        Near(est, expected, 0.01, "cost: walk, teleport, walk inside, teleport, walk");
        Check(!default(RouteStep).IsHub && default(RouteStep).HubExit == -1, "a default step isn't a hub step");
        Check(!new RouteStep(1, 1, true, false, "p").IsHub, "an ordinary portal step isn't one either");
    }

    private static void HubSkippedWhenWalkingIsCheaper()
    {
        // 2 degrees (480 units) is less than two teleports.
        var steps = PortalRoute.Plan(NoPortals, null, FakeHub(), 0, 0, 0, 2, out double est, out int used);
        Check(used == 0 && steps.Count == 1 && !steps[0].IsHub, "a short trip walks");
        Near(est, 480, 0.01, "cost is the walk");
        // A walk inside so long the network loses to walking.
        PortalRoute.Plan(NoPortals, null, FakeHub(walk: 20000), 0, 0, 40, 40, out _, out used);
        Check(used == 0, "an expensive walk inside is skipped");
        // An exit with no walk from the entry's arrival can't be reached.
        var noWalk = FakeHub(walk: double.NaN);
        PortalRoute.Plan(NoPortals, null, noWalk, 0, 0, 40, 40, out _, out used);
        Check(used == 0, "no walk inside: not used");
    }

    private static void HubAfterAPortal()
    {
        // A portal from the start takes you near the entry; the network then takes you on.
        var portals = new List<PortalLink> { new(-60, -60, 0.2, 0.2, "First Portal") };
        var hub = FakeHub();
        var steps = PortalRoute.Plan(portals, null, hub, -60, -60.05, 40, 40, out _, out int used);
        Check(used == 3, "portal, into the network, out");
        Check(steps.Count == 4 && steps[0].UsePortal && steps[0].Label == "First Portal", "the portal first");
        Check(steps[1].UsePortal && steps[1].Label == "Portal to Town Network", "then walk to the entry and take it");
        Check(steps[2].IsHub && !steps[3].UsePortal, "then the network, then the walk");
    }

    // ── townnet.json ─────────────────────────────────────────────────────────

    /// <summary>A small townnet.json: one arrival, an entry (noOlthoi), an exit with minLevel, its walk.</summary>
    private static string SmallTownNet(int version = 1, int minLevel = 80, bool fixId = true)
    {
        string text =
            "{\n" +
            $"  \"version\": {version},\n" +
            "  \"setId\": \"\",\n" +
            "  \"generated\": \"2026-10-01T00:00:00Z\",\n" +
            "  \"landblocks\": [\"0007\"],\n" +
            "  \"arrivals\": [\n" +
            "    {\"id\":\"A1\",\"cell\":\"00070133\",\"x\":60.0,\"y\":-70.0,\"z\":0.005}\n" +
            "  ],\n" +
            "  \"entries\": [\n" +
            "    {\"guid\":\"7A0B0001\",\"name\":\"Portal to Town Network\",\"cell\":\"A9B40001\",\"place\":\"outdoor\",\"ns\":0.1,\"ew\":0.0,\"noOlthoi\":true,\"arrival\":\"A1\"},\n" +
            "    {\"guid\":\"701AC00F\",\"name\":\"Portal to Town Network\",\"cell\":\"01AC0104\",\"place\":\"dungeon\",\"arrival\":\"A1\"}\n" +
            "  ],\n" +
            "  \"exits\": [\n" +
            "    {\"guid\":\"70007084\",\"name\":\"Portal to Eastwatch\",\"label\":\"Eastwatch\",\"cell\":\"0007016B\",\"x\":86.5,\"y\":-70.0,\"z\":0,\"dest\":{\"cell\":\"49F00013\",\"place\":\"outdoor\",\"ns\":40.0,\"ew\":40.1}" + (minLevel > 0 ? $",\"minLevel\":{minLevel}" : "") + "},\n" +
            "    {\"guid\":\"7000707E\",\"name\":\"The Marketplace of Dereth\",\"label\":\"The Marketplace of Dereth\",\"cell\":\"00070165\",\"x\":60,\"y\":-90,\"z\":0,\"dest\":{\"cell\":\"016C01BC\",\"place\":\"dungeon\"},\"noOlthoi\":true}\n" +
            "  ],\n" +
            "  \"walks\": [\n" +
            "    {\"from\":\"A1\",\"to\":\"70007084\",\"length\":26.5,\"points\":[[\"00070133\",60,-70,0],[\"00070140\",70,-70,0],[\"0007016B\",86.5,-70,0]]},\n" +
            "    {\"from\":\"A1\",\"to\":\"7000707E\",\"length\":20,\"points\":[[\"00070133\",60,-70,0],[\"00070165\",60,-90,0]]}\n" +
            "  ]\n" +
            "}\n";
        if (!fixId) return text;
        return text.Replace("\"setId\": \"\"", "\"setId\": \"" + TownNet.ComputeSetId(text) + "\"");
    }

    private static void TownNetFileChecks()
    {
        TownNet? t = TownNet.Parse(SmallTownNet(), out string why);
        Check(t != null && why == "", "a good file loads: " + why);
        Check(t != null && t.Contains(0x0007) && !t.Contains(0xA9B4), "the network's landblock");
        Check(t != null && t.Arrivals.Count == 1 && t.Entries.Count == 2 && t.Exits.Count == 2 && t.Walks.Count == 2, "its lists");
        Check(t?.Walk(0, 0)?.Points.Length == 3, "a walk by arrival and exit");
        Check(t != null && t.Exits[0].Where == "Eastwatch" && t.Exits[0].DestLandblock == 0x49F0, "an exit's place and landblock");
        Check(t != null && t.Exits[0].Rules.MinLevel == 80 && t.Entries[0].Rules.NoOlthoi, "restrictions read");
        string crlf = SmallTownNet().Replace("\n", "\r\n");
        Check(TownNet.Parse(crlf, out _) != null, "a CRLF copy has the same set id");

        Check(TownNet.Parse(SmallTownNet(fixId: false), out why) == null && why.Contains("set id"), "no set id: ignored (" + why + ")");
        string edited = SmallTownNet().Replace("\"length\":26.5", "\"length\":2.5");
        Check(TownNet.Parse(edited, out why) == null && why.Contains("doesn't match"), "an edited file is ignored (" + why + ")");
        Check(TownNet.Parse(SmallTownNet(version: 2), out why) == null && why.Contains("version 2"), "another version is ignored (" + why + ")");
    }

    private static void TownNetLevelAndOlthoi()
    {
        TownNet t = TownNet.Parse(SmallTownNet(), out _)!;
        var notes = new List<string>();
        HubLinks low = t.Links(45, false, notes);
        Check(low.Entries.Count == 1, "the outdoor entry is on the map (the dungeon one isn't)");
        Check(low.Exits.Count == 0, "level 45: the level 80 exit is left out (and the Marketplace is in a dungeon)");
        Check(notes.Exists(n => n.Contains("Portal to Eastwatch") && n.Contains("needs level 80")), "...with a note");
        HubLinks high = t.Links(90, false, null);
        Check(high.Exits.Count == 1 && high.Exits[0].Id == 0 && high.Exits[0].WalkUnits[0] == 26.5, "level 90: the exit, with its walk");
        notes.Clear();
        HubLinks unknown = t.Links(0, false, notes);
        Check(unknown.Exits.Count == 0 && notes.Exists(n => n.Contains("isn't known")), "level not known yet: a level rule leaves it out");
        HubLinks olthoi = t.Links(90, true, notes);
        Check(olthoi.Entries.Count == 0, "an Olthoi can't use the entry portals");

        // Planned: level 45 walks, level 90 takes the network.
        PortalRoute.Plan(NoPortals, null, low, 0, 0, 40, 40, out _, out int used);
        Check(used == 0, "level 45: the route doesn't use the blocked exit");
        var steps = PortalRoute.Plan(NoPortals, null, high, 0, 0, 40, 40, out _, out used);
        Check(used == 2 && steps.Exists(s => s.IsHub && s.HubExit == 0), "level 90: the network to Eastwatch");
        PortalRoute.Plan(NoPortals, null, olthoi, 0, 0, 40, 40, out _, out used);
        Check(used == 0, "an Olthoi walks");

        var rules = new TownNetRules { MaxLevel = 50 };
        Check(rules.Blocks(60, false).Contains("50 or lower") && rules.Blocks(40, false) == "", "max level");
        Check(new TownNetRules { Closed = true }.Blocks(90, false) == "closed", "closed");
        Check(new TownNetRules { Quest = "SomeFlag" }.Blocks(90, false).Contains("quest"), "a quest flag can't be checked: left out");
        Check(new TownNetRules { OnlyOlthoi = true }.Blocks(90, false) != "" && new TownNetRules { OnlyOlthoi = true }.Blocks(90, true) == "", "Olthoi only");
        Check(new TownNetRules { AccountRequirements = 1 }.Blocks(90, false) == "", "account requirement 1 restricts nobody");
    }

    private static void HubStepInTheArrowAndEntrance()
    {
        var route = PortalRoute.Plan(NoPortals, null, FakeHub(), 0, 0, 40, 40, out _, out _);
        var a = new ArrowGuide();
        a.SetRoute(route, "Far Town", "Town", 40, 40);
        Check(a.Count == 3, "entry portal, the network's exit, the place");
        Check(a.Current!.Kind == ArrowStepKind.Portal && a.Current.OnMap, "first: the entry portal on the map");
        Check(a.Update(true, 0.1, 0, true) == ArrowEvent.Advanced, "into the network");
        Check(a.Current!.Kind == ArrowStepKind.Portal && !a.Current.OnMap && a.Current.Hint.Contains("Town Network"), "then the exit inside (no map position)");
        Check(a.Update(false, 0, 0, true) == ArrowEvent.Advanced && a.Current!.Kind == ArrowStepKind.Place, "out: the place");

        // A dungeon behind the network's exit: the last walk becomes its portal; the hub step stays.
        var withEntrance = Entrance.EndingInPortal(route, 40, 40, "Some Dungeon");
        Check(withEntrance.Count == 3 && withEntrance[1].IsHub && withEntrance[2].UsePortal && !withEntrance[2].IsHub,
            "the last walk becomes the dungeon's portal; the hub step stays");
        var endsInHub = new List<RouteStep> { route[0], route[1] };
        Check(Entrance.EndingInPortal(endsInHub, 40, 40.1, "X").Count == 3, "a hub step is never taken for the last walk");
    }

    // ── The real townnet.json (RynthCore's tools/navdata/NavData), when it's beside this repo ──

    private static string RealNavDataDir() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
        "RynthCore", "tools", "navdata", "NavData"));

    private static (TownNet? Net, Atlas? Atlas, List<PortalLink>? Portals) RealTownNetData()
    {
        string dir = RealNavDataDir();
        string tn = Path.Combine(dir, TownNet.FileName), locs = Path.Combine(dir, "locations.json"), tsv = Path.Combine(dir, "portals.tsv");
        if (!File.Exists(tn) || !File.Exists(locs) || !File.Exists(tsv)) return (null, null, null);
        TownNet? t = TownNet.Parse(File.ReadAllText(tn), out string why);
        Check(t != null, "the real townnet.json loads: " + why);
        var portals = new List<PortalLink>();
        foreach (string line in File.ReadAllLines(tsv))
        {
            string[] f = line.Split('\t');
            if (f.Length < 5) continue;
            portals.Add(new PortalLink(double.Parse(f[0], CultureInfo.InvariantCulture), double.Parse(f[1], CultureInfo.InvariantCulture),
                double.Parse(f[2], CultureInfo.InvariantCulture), double.Parse(f[3], CultureInfo.InvariantCulture), f[4]));
        }
        return (t, Atlas.Load(locs), portals);
    }

    private static int ExitIndex(TownNet t, string where)
    {
        for (int i = 0; i < t.Exits.Count; i++) if (t.Exits[i].Where == where) return i;
        return -1;
    }

    private static void RealTownNetwork()
    {
        var (t, atlas, portals) = RealTownNetData();
        if (t == null || atlas == null || portals == null) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        Check(t.Arrivals.Count == 4 && t.Walks.Count == 196 && t.Contains(0x0007), "4 arrivals, 196 walks, landblock 0007");
        var holt = atlas.FindBest("Holtburg", false, 0, 0)!;
        var yaraq = atlas.FindBest("Yaraq", false, 0, 0)!;
        HubLinks hub = t.Links(100, false, null);
        Check(hub.Entries.Count == 52 && hub.Exits.Count == 47, $"on the map: 52 entries, 47 exits (got {hub.Entries.Count}, {hub.Exits.Count})");

        double direct = NavCoords.Distance(holt.Ns, holt.Ew, yaraq.Ns, yaraq.Ew);
        var without = PortalRoute.Plan(NoPortals, null, null, holt.Ns, holt.Ew, yaraq.Ns, yaraq.Ew, out double estWalk, out _);
        var steps = PortalRoute.Plan(NoPortals, null, hub, holt.Ns, holt.Ew, yaraq.Ns, yaraq.Ew, out double est, out int used);
        Console.WriteLine($"     Holtburg -> Yaraq, Town Network only: {used} teleport(s), ~{est:F0} vs {direct:F0} walking");
        foreach (var s in steps) Console.WriteLine($"       {(s.IsHub ? "town  " : s.UsePortal ? "portal" : "walk  ")} {s.Label} {NavCoords.Fmt(s.Ns, s.Ew)}");
        int yx = ExitIndex(t, "Yaraq");
        Check(used == 2 && steps.Count == 3, "Holtburg to Yaraq: the network (two teleports)");
        Check(steps[0].UsePortal && NavCoords.Distance(steps[0].Ns, steps[0].Ew, 42.28, 33.31) < 1, "from Holtburg's Town Network portal");
        Check(steps[1].IsHub && steps[1].HubExit == yx && yx >= 0, "out through the Portal to Yaraq");
        Check(est < estWalk && est < 1500, $"far cheaper than walking ({est:F0} vs {estWalk:F0})");

        // With the real portals too: the network is still taken (no portal chain beats it).
        var all = PortalRoute.Plan(portals, null, hub, holt.Ns, holt.Ew, yaraq.Ns, yaraq.Ew, out double estAll, out _);
        PortalRoute.Plan(portals, null, null, holt.Ns, holt.Ew, yaraq.Ns, yaraq.Ew, out double estPortals, out _);
        Check(all.Exists(s => s.IsHub) && estAll <= estPortals + 1e-6, $"with the portals too ({estAll:F0} vs {estPortals:F0} portals only)");

        // Short trips stay walks: Holtburg to a spot 1 degree away.
        PortalRoute.Plan(portals, null, hub, holt.Ns, holt.Ew, holt.Ns + 1, holt.Ew, out _, out used);
        Check(used == 0, "a short trip near Holtburg walks");
    }

    private static void RealTownNetworkLevelBlocks()
    {
        var (t, atlas, _) = RealTownNetData();
        if (t == null || atlas == null) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        var holt = atlas.FindBest("Holtburg", false, 0, 0)!;
        int ex = ExitIndex(t, "Eastwatch");
        Check(ex >= 0 && t.Exits[ex].Rules.MinLevel == 80, "the Portal to Eastwatch needs level 80");
        TownNetExit e = t.Exits[ex];
        var hi = PortalRoute.Plan(NoPortals, null, t.Links(90, false, null), holt.Ns, holt.Ew, e.DestNs, e.DestEw, out double estHi, out _);
        var notes = new List<string>();
        var lo = PortalRoute.Plan(NoPortals, null, t.Links(45, false, notes), holt.Ns, holt.Ew, e.DestNs, e.DestEw, out double estLo, out _);
        Console.WriteLine($"     Holtburg -> Eastwatch: level 90 ~{estHi:F0} via {Describe(hi)}; level 45 ~{estLo:F0} via {Describe(lo)}");
        Check(hi.Exists(s => s.IsHub && s.HubExit == ex), "level 90: through the Portal to Eastwatch");
        Check(!lo.Exists(s => s.IsHub && s.HubExit == ex), "level 45: never through the Portal to Eastwatch");
        Check(estLo > estHi, "...so the trip costs more");
        Check(notes.Exists(n => n.Contains("Eastwatch") && n.Contains("80")), "...and the note says why");
        Check(notes.Exists(n => n.Contains("Facility Hub")) == false, "the Facility Hub (in a dungeon) isn't a map exit at all");
    }

    // Tom, 10-01: "for Arwic, which should be simple Aphus -> Town Network -> Arwic". Recall Aphus
    // Lassel lands 12 yd from a Town Network portal, so from anywhere far from a town with its own
    // network portal, the plan is: cast the recall, walk into that portal, the network to Arwic.
    private static void AphusRecallThenTownNetworkToArwic()
    {
        var (t, atlas, portals) = RealTownNetData();
        if (t == null || atlas == null || portals == null) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        AtlasRecall? aphus = null;
        foreach (AtlasRecall r in atlas.Recalls) if (r.SpellId == 2931) { aphus = r; break; }
        Check(aphus != null && aphus.OnMap && aphus.Name == "Recall Aphus Lassel", "the Atlas has Recall Aphus Lassel (2931) on the map");
        if (aphus == null) return;
        // It's in RecallPlanner's list when the spellbook has it (and not when it doesn't).
        var mem = new RecallMemory();
        var withIt = RecallPlanner.Build(atlas, new HashSet<int> { 2931 }, mem, "Aelrynth|Lucy", null);
        Check(withIt.Exists(o => o.SpellId == 2931 && o.Name == "Recall Aphus Lassel"), "a character who knows 2931 can plan with it");
        Check(!RecallPlanner.Build(atlas, new HashSet<int>(), mem, "Aelrynth|Lucy", null).Exists(o => o.SpellId == 2931), "one who doesn't, can't");

        var arwic = atlas.FindBest("Arwic", false, 0, 0)!;
        HubLinks hub = t.Links(100, false, null);
        var recalls = RecallPlanner.Links(withIt);
        int arwicExit = ExitIndex(t, "Arwic");
        Check(arwicExit >= 0, "the network has a Portal to Arwic");
        // Starts far from any town with a Town Network portal of its own.
        foreach (var (where, ns, ew) in new[] { ("the southern wilds", -65.0, 45.0), ("north of Ayan Baqur", -45.0, -80.0), ("the Direlands", 20.0, -60.0) })
        {
            var steps = PortalRoute.Plan(portals, recalls, hub, ns, ew, arwic.Ns, arwic.Ew, out double est, out int used);
            Console.WriteLine($"     {where} -> Arwic: ~{est:F0} via {Describe(steps)}");
            Check(steps.Count >= 4 && steps[0].UseRecall && steps[0].Label == "Recall Aphus Lassel", $"{where}: first, cast Recall Aphus Lassel");
            Check(steps.Count >= 4 && steps[1].UsePortal && NavCoords.Distance(steps[1].Ns, steps[1].Ew, aphus.Ns, aphus.Ew) < 30,
                $"{where}: then walk the few yards into the Town Network portal beside its landing");
            Check(steps.Count >= 4 && steps[2].IsHub && steps[2].HubExit == arwicExit, $"{where}: then the network's Portal to Arwic");
            Check(est < 2000, $"{where}: all for about {est:F0} (a recall, two portals and short walks)");
        }
        // In a town with its own network portal, walking to that one is cheaper than the recall.
        var yaraq = atlas.FindBest("Yaraq", false, 0, 0)!;
        var fromYaraq = PortalRoute.Plan(portals, recalls, hub, yaraq.Ns, yaraq.Ew, arwic.Ns, arwic.Ew, out _, out _);
        Check(fromYaraq.Count >= 2 && !fromYaraq[0].UseRecall && fromYaraq[0].UsePortal && fromYaraq[1].IsHub && fromYaraq[1].HubExit == arwicExit,
            $"from Yaraq: its own network portal, no recall needed ({Describe(fromYaraq)})");
    }

    private static string Describe(List<RouteStep> steps)
    {
        var parts = new List<string>();
        foreach (var s in steps) parts.Add(s.UseRecall ? "recall:" + s.Label : s.IsHub ? "town:" + s.Label : s.UsePortal ? "portal:" + s.Label : "walk");
        return string.Join(" > ", parts);
    }

    private static void RealTownNetworkWays()
    {
        var (t, _, _) = RealTownNetData();
        if (t == null) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        int yx = ExitIndex(t, "Yaraq");
        TownNetArrival a1 = t.Arrivals[0];
        var way = t.WayTo(yx, a1.X, a1.Y, HubWalker.OffRouteUnits, out string from);
        Check(way != null && from == "A1", "standing on A1: A1's walk");
        TownNetWalk w = t.Walk(0, yx)!;
        Check(way != null && way.Count == w.Points.Length && Math.Abs(way[^1].X - t.Exits[yx].X) < 0.05 && Math.Abs(way[^1].Y - t.Exits[yx].Y) < 0.05, "it ends on the exit's centre");
        Near(TownNet.PathLength(way!, 0), w.Length, 0.05, "and is as long as the stored walk");
        Check(t.ArrivalNear(a1.X + 2, a1.Y) == 0 && t.ArrivalNear(a1.X + 4, a1.Y) == -1, "an arrival counts within 3 m");

        // Off every arrival, 1 m beside the middle of a walk: joined there.
        TownNetPoint p0 = w.Points[1], p1 = w.Points[2];
        double mx = (p0.X + p1.X) / 2, my = (p0.Y + p1.Y) / 2, dx = p1.X - p0.X, dy = p1.Y - p0.Y, len = Math.Sqrt(dx * dx + dy * dy);
        var joined = t.WayTo(yx, mx - dy / len, my + dx / len, HubWalker.OffRouteUnits, out from);
        Check(joined != null && from.StartsWith("the nearest walk") && Math.Abs(joined[^1].X - t.Exits[yx].X) < 0.05
            && Math.Abs(joined[0].X - (mx - dy / len)) < 1e-9, "beside a walk: starts where you stand and joins it (" + from + ")");
        Check(t.WayTo(yx, 500, 500, HubWalker.OffRouteUnits, out _) == null, "far from every walk: none");

        // Every walk is sound for the walker: at least 2 points, ends on its exit, no zero-length jump.
        int bad = 0;
        foreach (TownNetWalk x in t.Walks)
            if (x.Points.Length < 2 || Math.Abs(x.Points[^1].X - t.Exits[x.Exit].X) > 0.05 || Math.Abs(x.Points[^1].Y - t.Exits[x.Exit].Y) > 0.05) bad++;
        Check(bad == 0, $"all {t.Walks.Count} walks end on their exit");
    }

    // ── The walker inside: ideal movement over the real walks ────────────────

    private const double RunMetresPerTick = 10.0 / 30.0;   // ~10 m/s at 30 ticks a second
    private const long TickMs = 33;

    /// <summary>
    /// Runs a HubWalker over a way with ideal movement. Returns when it would touch the exit portal
    /// (its reach plus the body, as the server checks) once the cooldown allows, or when it stops.
    /// </summary>
    private static (HubWalker.Act Last, long TouchMs, double MaxOff, int Holds, string Why) Simulate(List<TownNetPoint> way, long landMs,
        Func<long, (double dx, double dy)>? shove = null, bool portalFires = true, int maxTicks = 3000)
    {
        var walker = new HubWalker(way, landMs, 0);
        double x = way[0].X, y = way[0].Y, maxOff = 0;
        int holds = 0;
        for (int i = 0; i < maxTicks; i++)
        {
            long now = i * TickMs;
            if (shove != null) { var (sx, sy) = shove(now); x += sx; y += sy; }
            double dExit = Math.Sqrt((way[^1].X - x) * (way[^1].X - x) + (way[^1].Y - y) * (way[^1].Y - y));
            if (portalFires && dExit < 1.36 + 0.48 && now - landMs >= 3500) return (HubWalker.Act.Run, now, maxOff, holds, "");
            if (dExit < 1.36 + 0.48 && now - landMs < 3500 && portalFires)
                return (HubWalker.Act.Stop, now, maxOff, holds, "touched the portal inside the cooldown");
            var act = walker.Step(x, y, now, out double heading, out string why);
            if (act == HubWalker.Act.Stop || act == HubWalker.Act.NoTeleport) return (act, now, maxOff, holds, why);
            if (act == HubWalker.Act.Hold) { holds++; continue; }
            double h = heading * Math.PI / 180.0;
            x += Math.Sin(h) * RunMetresPerTick; y += Math.Cos(h) * RunMetresPerTick;
            double off = double.MaxValue;
            for (int k = 1; k < way.Count; k++) off = Math.Min(off, TownNet.SegmentDistance(x, y, way[k - 1], way[k], out _));
            maxOff = Math.Max(maxOff, off);
        }
        return (HubWalker.Act.Stop, -1, maxOff, holds, "ran out of ticks");
    }

    private static void HubWalkerFollowsTheWalk()
    {
        var (t, _, _) = RealTownNetData();
        if (t == null) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        int touched = 0, early = 0, waited = 0, worst = 0, tight = 0;
        double worstOff = 0, slowest = 0, worstOverSpare = double.MinValue;
        foreach (TownNetWalk w in t.Walks)
        {
            TownNetArrival a = t.Arrivals[w.Arrival];
            var way = t.WayTo(w.Exit, a.X, a.Y, HubWalker.OffRouteUnits, out _)!;
            var r = Simulate(way, 0);
            if (r.Last == HubWalker.Act.Run && r.TouchMs >= 0) touched++;
            else { early++; if (early <= 3) Console.WriteLine($"       {a.Id} -> {t.Exits[w.Exit].Name}: {r.Why}"); }
            if (r.Holds > 0) waited++;
            if (r.MaxOff > worstOff) { worstOff = r.MaxOff; worst = t.Walks.IndexOf(w); }
            // The walks were checked for a 0.48 m body: their spare room is MinClear - 0.48.
            double spare = w.MinClear - 0.48;
            if (w.MinClear > 0 && r.MaxOff > spare) tight++;
            if (w.MinClear > 0) worstOverSpare = Math.Max(worstOverSpare, r.MaxOff - spare);
            slowest = Math.Max(slowest, r.TouchMs / 1000.0);
        }
        Console.WriteLine($"     {touched}/{t.Walks.Count} walks reach their exit after the cooldown; {waited} held for it; slowest {slowest:F1} s; widest {worstOff:F2} m off the line; {tight} walk(s) stray past their tightest spare room (by up to {worstOverSpare:F2} m)");
        Check(touched == t.Walks.Count && early == 0, "every walk reaches its exit portal, never inside the 3.5 s cooldown");
        Check(waited > 0, "short walks hold for the cooldown");
        // Ideal movement steps ~0.33 m a tick, so a sharp corner can be overshot by part of a step.
        Check(worstOff < 0.25, $"never more than a quarter metre off the line ({worstOff:F3} m)");
    }

    private static void HubWalkerStops()
    {
        var (t, _, _) = RealTownNetData();
        if (t == null) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        TownNetWalk w = t.Walks[0];
        TownNetArrival a = t.Arrivals[w.Arrival];
        var way = t.WayTo(w.Exit, a.X, a.Y, HubWalker.OffRouteUnits, out _)!;

        // Held in place (a wall, a monster): stuck after 3 s, with where.
        var walker = new HubWalker(way, -10000, 0);
        HubWalker.Act act = HubWalker.Act.Run;
        string why = "";
        long stoppedAt = -1;
        for (int i = 0; i < 300 && act != HubWalker.Act.Stop; i++) { act = walker.Step(way[0].X, way[0].Y, i * TickMs, out _, out why); stoppedAt = i * TickMs; }
        Check(act == HubWalker.Act.Stop && why.StartsWith("stuck near"), "standing still: stuck (" + why + ")");
        Check(stoppedAt >= HubWalker.StuckMs && stoppedAt <= HubWalker.StuckMs + 200, $"after {HubWalker.StuckMs / 1000} s ({stoppedAt} ms)");

        // Knocked 8 m off the way: stops.
        walker = new HubWalker(way, -10000, 0);
        TownNetPoint p = way[1];
        act = walker.Step(p.X + 8, p.Y + 8, 0, out _, out why);
        Check(act == HubWalker.Act.Stop && why.Contains("off the way"), "8 m off the way: stops (" + why + ")");

        // 3 m off: steers back toward the line.
        walker = new HubWalker(way, -10000, 0);
        TownNetPoint s0 = way[0], s1 = way[1];
        double dx = s1.X - s0.X, dy = s1.Y - s0.Y, len = Math.Sqrt(dx * dx + dy * dy);
        double ox = s0.X + dx / len * 2 - dy / len * 3, oy = s0.Y + dy / len * 2 + dx / len * 3;
        walker.Step(ox, oy, 0, out double heading, out _);
        double h = heading * Math.PI / 180.0;
        double nx = ox + Math.Sin(h), ny = oy + Math.Cos(h);
        Check(TownNet.SegmentDistance(nx, ny, s0, s1, out _) < TownNet.SegmentDistance(ox, oy, s0, s1, out _) - 0.5, "3 m off: heads back to the line");

        // The portal never fires: holds on its centre, backs off, walks in once more, then gives up.
        var r = Simulate(way, -10000, portalFires: false);
        Check(r.Last == HubWalker.Act.NoTeleport, "a portal that doesn't take you: stops after one more try (" + r.Last + " " + r.Why + ")");
        Check(r.TouchMs > 2 * HubWalker.TouchWaitMs, "after waiting on it twice");

        // Too long in all: the limit.
        walker = new HubWalker(way, -10000, 0);
        act = walker.Step(way[0].X, way[0].Y, walker.LimitMs + 1, out _, out why);
        Check(act == HubWalker.Act.Stop && why.Contains("too long"), "the overall limit (" + why + ")");
        Check(walker.LimitMs >= (long)((walker.Length / 3 + 10) * 1000), "limit: length / 3 + 10 s at least");
    }
}
