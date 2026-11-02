using System;
using System.IO;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// Offline tests for RynthNav's travel logic. Self-contained; fails on zero assertions.
// Run: dotnet run -c Release  (exit 0 = pass, 1 = fail)
internal static partial class Program
{
    private static int _asserts, _fails;

    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static void Near(double actual, double expected, double tol, string msg)
    {
        _asserts++;
        if (double.IsNaN(actual) || Math.Abs(actual - expected) > tol)
        { _fails++; Console.WriteLine($"  [FAIL] {msg}: expected {expected:0.####} ±{tol}, got {actual:0.####}"); }
    }

    private static int Main()
    {
        Console.WriteLine("=== RynthNav route tests ===");
        Run(nameof(DirectWalkWhenClose), DirectWalkWhenClose);
        Run(nameof(OnePortal), OnePortal);
        Run(nameof(TwoPortalsChained), TwoPortalsChained);
        Run(nameof(RecallBeatsPortal), RecallBeatsPortal);
        Run(nameof(SlowRecallLoses), SlowRecallLoses);
        Run(nameof(RecallThenPortal), RecallThenPortal);
        Run(nameof(RecallCostFromCastTime), RecallCostFromCastTime);
        Run(nameof(RecallListFromSpellbookAndMemory), RecallListFromSpellbookAndMemory);
        Run(nameof(RecallListColdSpellbook), RecallListColdSpellbook);
        Run(nameof(RecallMemoryRoundTrip), RecallMemoryRoundTrip);
        Run(nameof(RecallMessages), RecallMessages);
        Run(nameof(HandCastOnALifestone), HandCastOnALifestone);
        Run(nameof(HandCastPortalTie), HandCastPortalTie);
        Run(nameof(RecallNotLinked), RecallNotLinked);
        Run(nameof(DungeonLandblocksFromTheAtlas), DungeonLandblocksFromTheAtlas);
        Run(nameof(AtlasSearchAndBestMatch), AtlasSearchAndBestMatch);
        Run(nameof(AtlasRecallPlaces), AtlasRecallPlaces);
        Run(nameof(ArrowSingleTarget), ArrowSingleTarget);
        Run(nameof(ArrowAlongRoute), ArrowAlongRoute);
        Run(nameof(FavoritesAndRecent), FavoritesAndRecent);
        Run(nameof(CommandReplies), CommandReplies);
        Run(nameof(CoordinateParsing), CoordinateParsing);
        Run(nameof(BearingAndDistance), BearingAndDistance);
        Run(nameof(RealData), RealData);
        Run(nameof(NavDataFolder), NavDataFolder);
        Run(nameof(DungeonEntrance), DungeonEntrance);
        Run(nameof(RouteEndsOnAPortalStep), RouteEndsOnAPortalStep);
        Run(nameof(RealDungeonEntrances), RealDungeonEntrances);
        RunTownNet();
        RunAvoid();
        RunTravelPlan();
        RunTripExport();
        RunAutoRunGate();
        RunSteering();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 && _asserts > 0
            ? $"PASS ({_asserts} assertions)"
            : $"FAIL ({_fails} of {_asserts} assertions failed)");
        return _fails == 0 && _asserts > 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        int before = _fails;
        try { test(); }
        catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] {name} threw {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"{(_fails == before ? "ok  " : "FAIL")} {name}");
    }

    // ── Route planning over small fake portal graphs ─────────────────────────

    private static readonly List<PortalLink> NoPortals = new();

    private static void DirectWalkWhenClose()
    {
        var portals = new List<PortalLink> { new(0.5, 0.5, 39.5, 39.5, "Far Portal") };
        var steps = PortalRoute.Plan(portals, null, 0, 0, 0.3, 0.2, out double est, out int used);
        Check(used == 0, "no teleport for a short walk");
        Check(steps.Count == 1 && !steps[0].UsePortal && !steps[0].UseRecall, "one walk leg");
        Near(est, NavCoords.Distance(0, 0, 0.3, 0.2), 0.01, "cost is the walk");
    }

    private static void OnePortal()
    {
        var portals = new List<PortalLink> { new(0.5, 0.5, 39.5, 39.5, "Far Portal") };
        var steps = PortalRoute.Plan(portals, null, 0, 0, 40, 40, out _, out int used);
        Check(used == 1, "one portal");
        Check(steps.Count == 2, "walk to the portal, walk to the goal");
        Check(steps[0].UsePortal && steps[0].Label == "Far Portal", "first leg takes the portal");
        Near(steps[0].Ns, 0.5, 1e-9, "first leg ends at the entrance");
        Near(steps[0].LandNs, 39.5, 1e-9, "portal step knows where it lands");
        Check(!steps[1].UsePortal && !steps[1].UseRecall, "last leg walks");
        Near(steps[1].Ns, 40, 1e-9, "last leg ends at the goal");
    }

    private static void TwoPortalsChained()
    {
        var portals = new List<PortalLink>
        {
            new(0.2, 0.2, 20, 20, "First"),
            new(20.3, 20.1, 40, 40, "Second"),
            new(0.1, -0.1, -40, -40, "Wrong way"),
        };
        var steps = PortalRoute.Plan(portals, null, 0, 0, 40.2, 40.2, out _, out int used);
        Check(used == 2, "two portals");
        Check(steps.Count == 3 && steps[0].Label == "First" && steps[1].Label == "Second", "in order");
        Check(steps[0].UsePortal && steps[1].UsePortal && !steps[2].UsePortal, "portal, portal, walk");
    }

    private static void RecallBeatsPortal()
    {
        var portals = new List<PortalLink> { new(0.7, 0.7, 39.5, 39.5, "Far Portal") };
        var recalls = new List<RecallLink> { new(40.1, 40.1, "Quick Recall", PortalRoute.RecallCostUnits(5)) };
        var steps = PortalRoute.Plan(portals, recalls, 0, 0, 40, 40, out _, out int used);
        Check(used == 1, "one teleport");
        Check(steps.Count == 2 && steps[0].UseRecall, "starts with the recall");
        Check(steps[0].RecallIndex == 0 && steps[0].Label == "Quick Recall", "recall step names its recall");
        Near(steps[0].LandNs, 40.1, 1e-9, "recall step knows where it lands");
        Near(steps[0].Ns, 0, 1e-9, "recall is cast where you stand");
    }

    private static void SlowRecallLoses()
    {
        var portals = new List<PortalLink> { new(0.7, 0.7, 39.5, 39.5, "Far Portal") };
        var recalls = new List<RecallLink> { new(40.1, 40.1, "Slow Recall", PortalRoute.RecallCostUnits(1000)) };
        var steps = PortalRoute.Plan(portals, recalls, 0, 0, 40, 40, out _, out _);
        Check(steps.Count == 2 && steps[0].UsePortal && !steps[0].UseRecall, "the portal wins over a very slow recall");
    }

    private static void RecallThenPortal()
    {
        // A recall to a town, then that town's portal, beats walking or the far portal.
        var portals = new List<PortalLink> { new(-30.1, 70.1, 40, 40, "Town Portal") };
        var recalls = new List<RecallLink>
        {
            new(-30, 70, "Town Recall", PortalRoute.RecallCostUnits(5)),
            new(-80, -80, "Useless Recall", PortalRoute.RecallCostUnits(5)),
        };
        var steps = PortalRoute.Plan(portals, recalls, 0, 0, 40.2, 40.2, out _, out int used);
        Check(used == 2, "recall then portal");
        Check(steps.Count == 3 && steps[0].UseRecall && steps[0].RecallIndex == 0, "the town recall first");
        Check(steps[1].UsePortal && steps[1].Label == "Town Portal", "then the town's portal");
    }

    private static void RecallCostFromCastTime()
    {
        Near(PortalRoute.RecallCostUnits(0), PortalRoute.TeleportOverheadUnits, 1e-9, "no cast time = the teleport overhead");
        Near(PortalRoute.RecallCostUnits(10) - PortalRoute.RecallCostUnits(0), 10 * PortalRoute.RunUnitsPerSecond, 1e-9,
            "each second of casting costs a second of running");
        Near(new RecallLink(1, 1, "old").CostUnits, PortalRoute.RecallPenaltyUnits, 1e-9, "old constructor keeps the old penalty");
    }

    // ── The recall list ──────────────────────────────────────────────────────

    private const string AtlasJson = @"{
  ""version"": 1, ""generated"": ""2026-10-01T00:00:00Z"",
  ""locations"": [
    {""id"":1,""name"":""Holtburg"",""type"":""Town"",""cell"":""A9B40019"",""place"":""outdoor"",""ns"":42.08,""ew"":33.6},
    {""id"":2,""name"":""Holtburg Portal"",""type"":""Portal"",""wcid"":100,""cell"":""7D640010"",""place"":""outdoor"",""ns"":-21.0,""ew"":-1.6,""dest"":{""cell"":""A9B40019"",""place"":""outdoor"",""ns"":42.08,""ew"":33.6}},
    {""id"":3,""name"":""Holtburg"",""type"":""Portal"",""wcid"":101,""cell"":""01D201F7"",""place"":""dungeon"",""dest"":{""cell"":""A7B70037"",""place"":""outdoor"",""ns"":45.06,""ew"":32.3}},
    {""id"":4,""name"":""Holtburg Lifestone"",""type"":""Lifestone"",""cell"":""A9B40018"",""place"":""outdoor"",""ns"":42.1,""ew"":33.5},
    {""id"":5,""name"":""Yaraq"",""type"":""Town"",""cell"":""7D64000D"",""place"":""outdoor"",""ns"":-21.51,""ew"":-1.82},
    {""id"":6,""name"":""Glimmering Cave"",""type"":""Dungeon"",""cell"":""A9B30005"",""place"":""outdoor"",""ns"":41.0,""ew"":33.0,""lb"":""01AB"",""minLevel"":12},
    {""id"":7,""name"":""Shopkeeper Renald"",""type"":""Vendor"",""cell"":""A9B40150"",""place"":""building"",""ns"":42.2,""ew"":33.4,""desc"":""Shopkeeper.""},
    {""id"":8,""name"":""Cave Hermit"",""type"":""NPC"",""cell"":""01AB0110"",""place"":""dungeon"",""desc"":""In Glimmering Cave.""}
  ],
  ""recalls"": [
    {""spell"":2041,""name"":""Aerlinthe Recall"",""cell"":""BAE8001D"",""place"":""outdoor"",""ns"":84.09,""ew"":47.2},
    {""spell"":2358,""name"":""Lyceum Recall"",""cell"":""BC110014"",""place"":""outdoor"",""ns"":-88.05,""ew"":48.65},
    {""spell"":4213,""name"":""Colosseum Recall"",""cell"":""00AF0118"",""place"":""dungeon"",""in"":""The Colosseum""}
  ]
}";

    private static void RecallListFromSpellbookAndMemory()
    {
        Atlas atlas = Atlas.Parse(AtlasJson);
        var known = new HashSet<int> { 2041, 4213, RecallPlanner.LifestoneRecall, RecallPlanner.PrimaryPortalTieRecall };
        var mem = new RecallMemory();
        mem.Set("Aelrynth|Tom", RecallSlot.Lifestone, new RecallSpot { OnMap = true, Ns = 42.1, Ew = 33.5, Label = "Holtburg" });
        mem.Set("Aelrynth|Tom", RecallSlot.House, new RecallSpot { OnMap = true, Ns = 10, Ew = 10, Label = "a cottage" });
        mem.Set("Aelrynth|Other", RecallSlot.Sanctuary, new RecallSpot { OnMap = true, Ns = 1, Ew = 1 });
        var notes = new List<string>();
        var list = RecallPlanner.Build(atlas, known, mem, "Aelrynth|Tom", notes);
        var names = list.ConvertAll(o => o.Name);
        Check(names.Contains("Aerlinthe Recall"), "a known town recall");
        Check(!names.Contains("Lyceum Recall"), "an unknown spell is left out");
        Check(!names.Contains("Colosseum Recall"), "a dungeon landing is left out");
        Check(notes.Exists(n => n.Contains("Colosseum Recall") && n.Contains("The Colosseum")), "...with a note");
        var ls = list.Find(o => o.Name == "Lifestone Recall");
        Check(ls != null && ls.SpellId == RecallPlanner.LifestoneRecall && ls.Learned, "Lifestone Recall from memory");
        Check(!names.Contains("Primary Portal Tie Recall"), "a tie with no known portal is left out");
        Check(notes.Exists(n => n.StartsWith("Primary Portal Tie Recall")), "...with a note");
        Check(!names.Contains("Portal Recall"), "Portal Recall needs the spell");
        var house = list.Find(o => o.Name == "House recall");
        Check(house != null && house.SpellId == 0 && house.Command == "@house recall", "house recall as a command");
        Check(!names.Contains("Lifestone (@lifestone)"), "no sanctuary known for this character");
        Check(notes.Exists(n => n.StartsWith("Lifestone (@lifestone)")), "...with a note");
        Check(list.TrueForAll(o => o.CastSeconds > 0), "every recall has a cast time");
        var link = list.Find(o => o.Name == "Aerlinthe Recall")!.ToLink();
        Near(link.CostUnits, PortalRoute.RecallCostUnits(RecallPlanner.SpellCastSeconds), 1e-9, "a spell's cost");
        Near(house!.ToLink().CostUnits, PortalRoute.RecallCostUnits(RecallPlanner.CommandRecallSeconds), 1e-9, "a command's cost");
    }

    private static void RecallListColdSpellbook()
    {
        Atlas atlas = Atlas.Parse(AtlasJson);
        var mem = new RecallMemory();
        mem.Set("c", RecallSlot.Sanctuary, new RecallSpot { OnMap = true, Ns = 5, Ew = 6 });
        var notes = new List<string>();
        var list = RecallPlanner.Build(atlas, null, mem, "c", notes);
        Check(list.Count == 1 && list[0].Command == "@lifestone", "only @lifestone while the spellbook isn't read");
        Check(notes.Exists(n => n.Contains("Spellbook not read")), "says why");
    }

    private static void RecallMemoryRoundTrip()
    {
        var mem = new RecallMemory();
        mem.Set("Aelrynth|Tom", RecallSlot.Tie1, new RecallSpot { OnMap = true, Ns = -33.54, Ew = 72.8, Label = "Shoushi" });
        mem.Set("Aelrynth|Tom", RecallSlot.LastPortal, new RecallSpot { OnMap = false, Label = "Mines of Despair" });
        mem.Set("Aelrynth|Ann", RecallSlot.House, new RecallSpot { OnMap = true, Ns = 1.5, Ew = -2.5, Command = "@house recall" });
        Check(mem.Version == 3, "version moves");
        var back = RecallMemory.Parse(mem.Serialize());
        Check(back.Version == 0, "a fresh load isn't dirty");
        var t = back.Get("aelrynth|tom", RecallSlot.Tie1);
        Check(t != null && t.OnMap && t.Label == "Shoushi", "tie survives (names ignore case)");
        Near(t?.Ns ?? double.NaN, -33.54, 1e-9, "tie NS");
        var lp = back.Get("Aelrynth|Tom", RecallSlot.LastPortal);
        Check(lp != null && !lp.OnMap && lp.Label == "Mines of Despair", "an off-map spot survives");
        Check(back.Get("Aelrynth|Ann", RecallSlot.House)?.Command == "@house recall", "command survives");
        Check(back.Get("Aelrynth|Ann", RecallSlot.Tie1) == null, "per character");
    }

    private static void RecallMessages()
    {
        Check(RecallMemory.Classify("You have attuned your spirit to this Lifestone. You will resurrect here after you die.", "Tom")
              == RecallEvent.AttunedLifestone, "lifestone attune");
        Check(RecallMemory.Classify("You have successfully linked with the life stone.", "Tom") == RecallEvent.LinkedLifestone, "lifestone tie");
        Check(RecallMemory.Classify("You have successfully linked with the portal.", "Tom") == RecallEvent.LinkedPortal, "portal tie");
        Check(RecallMemory.Classify("Tom is recalling to the lifestone.", "Tom") == RecallEvent.RecallingLifestone, "@lifestone");
        Check(RecallMemory.Classify("Tom is recalling home.", "Tom") == RecallEvent.RecallingHome, "house");
        Check(RecallMemory.Classify("Tom is recalling to the Allegiance housing.", "Tom") == RecallEvent.RecallingMansion, "mansion");
        Check(RecallMemory.Classify("Tom is going to the Allegiance hometown.", "Tom") == RecallEvent.RecallingHometown, "hometown");
        Check(RecallMemory.Classify("Tom is recalling to the marketplace.", "Tom") == RecallEvent.RecallingMarketplace, "marketplace");
        Check(RecallMemory.Classify("Tommy is recalling home.", "Tom") == RecallEvent.None, "someone else nearby");
        Check(RecallMemory.Classify("Ann says, \"Tom is recalling home.\"", "Tom") == RecallEvent.None, "quoted in chat");
        Check(RecallMemory.Classify("", "Tom") == RecallEvent.None, "empty");
        Check(RecallMemory.SlotFor(RecallEvent.RecallingHome, out var slot) && slot == RecallSlot.House, "house slot");
        Check(!RecallMemory.SlotFor(RecallEvent.RecallingMarketplace, out _), "marketplace has no slot");
        // Your spell words (Spellcasting, 17) start a hand cast; the same words typed in local chat don't.
        Check(RecallMemory.Classify("You say, \"Zojak Ququ\"", "Tom", RecallMemory.ChatTypeSpellcasting) == RecallEvent.OwnSpellWords, "own spell words");
        Check(RecallMemory.Classify("You say, \"Zojak Ququ\"", "Tom", 2) == RecallEvent.None, "typed in local chat");
        Check(RecallMemory.Classify("You say, \"Zojak Ququ\"", "Tom") == RecallEvent.None, "chat type unknown");
        Check(RecallMemory.Classify("Ann says, \"Zojak Ququ\"", "Tom", RecallMemory.ChatTypeSpellcasting) == RecallEvent.None, "someone else casting");
    }

    // ── A recall cast by hand: which one, from where it landed ───────────────

    private const string Me = "Aelrynth|Tom";

    private static RecallSpot At(double ns, double ew, string label = "") => new() { OnMap = true, Ns = ns, Ew = ew, Label = label };

    private static void HandCastOnALifestone()
    {
        var mem = new RecallMemory();
        var known = new HashSet<int> { RecallPlanner.LifestoneRecall, RecallPlanner.PrimaryPortalTieRecall };
        var k = RecallPlanner.GuessHandCast(At(42.1, 33.6), atLifestone: true, known, mem, Me, 30, out var slot, out _);
        Check(k == RecallPlanner.HandCast.Learn && slot == RecallSlot.Lifestone, "on a lifestone: Lifestone Recall");

        mem.Set(Me, RecallSlot.Lifestone, At(42.1, 33.6));
        k = RecallPlanner.GuessHandCast(At(42.11, 33.6), true, known, mem, Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.Nothing, "the same lifestone again: nothing new");
        k = RecallPlanner.GuessHandCast(At(-30.0, 70.0), true, known, mem, Me, 30, out slot, out _);
        Check(k == RecallPlanner.HandCast.Learn && slot == RecallSlot.Lifestone, "tied somewhere else since: learned again");

        k = RecallPlanner.GuessHandCast(At(42.1, 33.6), true, new HashSet<int> { RecallPlanner.PrimaryPortalTieRecall }, new RecallMemory(), Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.Nothing, "no Lifestone Recall in the spellbook: not it");

        // On the @lifestone lifestone with Recall the Sanctuary known: either spell, can't tell.
        var mem2 = new RecallMemory();
        mem2.Set(Me, RecallSlot.Sanctuary, At(42.1, 33.6));
        var known2 = new HashSet<int> { RecallPlanner.LifestoneRecall, RecallPlanner.RecallTheSanctuary };
        k = RecallPlanner.GuessHandCast(At(42.1, 33.6), true, known2, mem2, Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.Nothing, "Recall the Sanctuary could land there too");
        k = RecallPlanner.GuessHandCast(At(10, 10), true, null, mem2, Me, 30, out _, out var why);
        Check(k == RecallPlanner.HandCast.Nothing && why.Contains("spellbook"), "a cold spellbook teaches nothing");
    }

    private static void HandCastPortalTie()
    {
        var oneTie = new HashSet<int> { RecallPlanner.SecondaryPortalTieRecall };
        var k = RecallPlanner.GuessHandCast(At(-33.5, 72.8), false, oneTie, new RecallMemory(), Me, 30, out var slot, out _);
        Check(k == RecallPlanner.HandCast.Learn && slot == RecallSlot.Tie2, "the only tie recall known");

        var both = new HashSet<int> { RecallPlanner.PrimaryPortalTieRecall, RecallPlanner.SecondaryPortalTieRecall };
        k = RecallPlanner.GuessHandCast(At(-33.5, 72.8), false, both, new RecallMemory(), Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.AskTie, "both tie recalls known: ask which");

        var mem = new RecallMemory();
        mem.Set(Me, RecallSlot.Tie1, At(-33.5, 72.8));
        k = RecallPlanner.GuessHandCast(At(-33.51, 72.8), false, both, mem, Me, 30, out slot, out _);
        Check(k == RecallPlanner.HandCast.Nothing && slot == RecallSlot.Tie1, "a known tie's landing: nothing new");

        // Portal Recall: lands where the last portal went (known: nothing; unknown: ask).
        var withPortalRecall = new HashSet<int> { RecallPlanner.PrimaryPortalTieRecall, RecallPlanner.PortalRecall };
        var mem2 = new RecallMemory();
        mem2.Set(Me, RecallSlot.LastPortal, At(12, 34));
        k = RecallPlanner.GuessHandCast(At(12, 34), false, withPortalRecall, mem2, Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.Nothing, "the last portal's landing: Portal Recall");
        k = RecallPlanner.GuessHandCast(At(-33.5, 72.8), false, withPortalRecall, mem2, Me, 30, out slot, out _);
        Check(k == RecallPlanner.HandCast.Learn && slot == RecallSlot.Tie1, "elsewhere, last portal known: the one tie");
        k = RecallPlanner.GuessHandCast(At(-33.5, 72.8), false, withPortalRecall, new RecallMemory(), Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.AskTie, "last portal unknown: could be Portal Recall, ask");

        k = RecallPlanner.GuessHandCast(At(1, 1), false, new HashSet<int> { RecallPlanner.LifestoneRecall }, new RecallMemory(), Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.Nothing, "off a lifestone with no tie recall: not one RynthNav knows");

        // A dungeon landing (off the map) is learned too, and matched by its label.
        var dungeon = new RecallSpot { OnMap = false, Label = "a dungeon (landblock 6145)" };
        k = RecallPlanner.GuessHandCast(dungeon, false, oneTie, new RecallMemory(), Me, 30, out slot, out _);
        Check(k == RecallPlanner.HandCast.Learn && slot == RecallSlot.Tie2, "a tie into a dungeon");
        var mem3 = new RecallMemory();
        mem3.Set(Me, RecallSlot.Tie2, new RecallSpot { OnMap = false, Label = "a dungeon (landblock 6145)" });
        k = RecallPlanner.GuessHandCast(dungeon, false, oneTie, mem3, Me, 30, out _, out _);
        Check(k == RecallPlanner.HandCast.Nothing, "the same dungeon again: nothing new");

        // Once learned, the planner uses it.
        var plannerMem = new RecallMemory();
        plannerMem.Set(Me, RecallSlot.Tie2, At(-33.5, 72.8, "Shoushi"));
        var list = RecallPlanner.Build(Atlas.Empty, oneTie, plannerMem, Me, new List<string>());
        Check(list.Exists(o => o.SpellId == RecallPlanner.SecondaryPortalTieRecall && Math.Abs(o.Ns + 33.5) < 1e-9), "the learned tie is planned");
    }

    private static void DungeonLandblocksFromTheAtlas()
    {
        // A dungeon landblock's tile is its surface (dungeon cells aren't baked): the walk must
        // know it's in one. Dungeon places and dungeon portal destinations count; a landblock
        // with places on the map (a town, its cellars) never does.
        Atlas atlas = Atlas.Parse(@"{""locations"":[
            {""name"":""Surface"",""type"":""Portal"",""cell"":""000102C8"",""place"":""dungeon"",""dest"":{""cell"":""26E5001D"",""place"":""outdoor"",""ns"":81.68,""ew"":-71.16}},
            {""name"":""Holtburg Dungeon Portal"",""type"":""Portal"",""cell"":""A9B40021"",""place"":""outdoor"",""ns"":42.0,""ew"":33.0,""dest"":{""cell"":""01F60230"",""place"":""dungeon""}},
            {""name"":""Shoushi"",""type"":""Town"",""cell"":""DA55001D"",""place"":""outdoor"",""ns"":-33.5,""ew"":72.8},
            {""name"":""Cellar Vendor"",""type"":""Vendor"",""cell"":""DA550105"",""place"":""dungeon""},
            {""name"":""Odd"",""type"":""Landmark"",""cell"":""zz"",""place"":""dungeon""}
        ]}");
        HashSet<uint> d = atlas.DungeonLandblocks();
        Check(d.Contains(0x0001), "a dungeon place's landblock");
        Check(d.Contains(0x01F6), "a portal's dungeon destination");
        Check(!d.Contains(0x26E5) && !d.Contains(0xA9B4), "outdoor landblocks aren't dungeons");
        Check(!d.Contains(0xDA55), "a town landblock with a dungeon cell isn't one");
        Check(d.Count == 2, "nothing else (a bad cell is skipped)");
    }

    private static void RecallNotLinked()
    {
        Check(RecallPlanner.SlotUnlinked(RecallPlanner.ErrorMustLinkLifestone, 0) == RecallSlot.Lifestone, "no lifestone tie: Lifestone Recall");
        Check(RecallPlanner.SlotUnlinked(RecallPlanner.ErrorMustLinkPortal, RecallPlanner.PrimaryPortalTieRecall) == RecallSlot.Tie1, "no primary tie");
        Check(RecallPlanner.SlotUnlinked(RecallPlanner.ErrorMustLinkPortal, RecallPlanner.SecondaryPortalTieRecall) == RecallSlot.Tie2, "no secondary tie");
        Check(RecallPlanner.SlotUnlinked(RecallPlanner.ErrorMustLinkPortal, RecallPlanner.PortalRecall) == RecallSlot.LastPortal, "no last portal");
        Check(RecallPlanner.SlotUnlinked(RecallPlanner.ErrorMustLinkPortal, 0) == null, "a portal recall cast by hand: can't tell which");
        Check(RecallPlanner.SlotUnlinked(0x0402, RecallPlanner.LifestoneRecall) == null, "a fizzle isn't a missing tie");
        Check(RecallPlanner.SpellFor(RecallSlot.Tie2) == RecallPlanner.SecondaryPortalTieRecall && RecallPlanner.SpellFor(RecallSlot.House) == 0, "slot spells");

        var mem = new RecallMemory();
        mem.Set(Me, RecallSlot.Lifestone, At(42.1, 33.6));
        int v = mem.Version;
        mem.Clear(Me, RecallSlot.Lifestone);
        Check(mem.Get(Me, RecallSlot.Lifestone) == null && mem.Version > v, "forgotten (and saved)");
        var notes = new List<string>();
        RecallPlanner.Build(Atlas.Empty, new HashSet<int> { RecallPlanner.LifestoneRecall }, mem, Me, notes);
        Check(notes.Exists(n => n.Contains("Lifestone Recall") && n.Contains("cast Lifestone Recall once")), "the note says how to teach it");
    }

    // ── Atlas ────────────────────────────────────────────────────────────────

    // The recalls' landing spots are found by name (2026-10-22, Skippy: "neft" found nothing).
    private static void AtlasRecallPlaces()
    {
        Atlas atlas = Atlas.Parse(@"{""locations"":[
            {""id"":1,""name"":""Holtburg"",""type"":""Town"",""cell"":""A9B40019"",""place"":""outdoor"",""ns"":42.1,""ew"":33.6}],
          ""recalls"":[
            {""spell"":5541,""name"":""Lost City of Neftet Recall"",""cell"":""8763000E"",""place"":""outdoor"",""ns"":-22.21,""ew"":6.22},
            {""spell"":1635,""name"":""Ulgrim's Recall"",""cell"":""01960118"",""place"":""dungeon""},
            {""spell"":1636,""name"":""Ulgrim's Recall"",""cell"":""01960118"",""place"":""dungeon""},
            {""spell"":2479,""name"":""Advance Camp Recall"",""cell"":""027B0200"",""place"":""dungeon"",""in"":""Vile-Smelling Refuse""},
            {""spell"":0,""name"":""  "",""cell"":""00000000"",""place"":""dungeon""}]}");
        Check(atlas.Locations.Count == 1, "the recalls stay out of Locations (the route planner's list)");
        Check(atlas.Recalls.Count == 5, "the recalls themselves unchanged");
        Check(atlas.RecallPlaces.Count == 3, "three recall places (Ulgrim's twice is one; no name, none)");
        bool allRecall = true;
        foreach (var p in atlas.RecallPlaces) allRecall &= p.Type == Atlas.RecallType && p.Id < 0;
        Check(allRecall, "type Recall, negative ids");

        var hits = atlas.Search("neft", null, 10);
        Check(hits.Count == 1 && hits[0].Name == "Lost City of Neftet Recall", "\"neft\" finds Lost City of Neftet Recall");
        Check(hits.Count == 1 && hits[0].OnMap && Math.Abs(hits[0].Ns + 22.21) < 1e-9 && Math.Abs(hits[0].Ew - 6.22) < 1e-9, "at 22.21S, 6.22E");
        var best = atlas.FindBest("neftet", false, 0, 0);
        Check(best != null && best.Type == Atlas.RecallType, "/rnav go neftet resolves to it");
        Check(atlas.FindBest("ulgrim", false, 0, 0) == null, "a dungeon recall is no arrow target");
        var camp = atlas.Search("advance camp", null, 10);
        Check(camp.Count == 1 && camp[0].Desc == "in Vile-Smelling Refuse", "a dungeon recall says where it is");
        Check(atlas.Search("", Atlas.RecallType, 50).Count == 3, "the Recall type filter");
        Check(atlas.Search("holtburg", null, 10).Count == 1, "other searches unchanged");
    }

    private static void AtlasSearchAndBestMatch()
    {
        Atlas atlas = Atlas.Parse(AtlasJson);
        Check(atlas.Locations.Count == 8 && atlas.Recalls.Count == 3, "parsed");
        var best = atlas.FindBest("holtburg", hasPos: true, ns: -21.0, ew: -1.6);
        Check(best != null && best.Type == "Town" && best.Name == "Holtburg", "the town beats its portals, even standing next to one");
        best = atlas.FindBest("holt", hasPos: false, ns: 0, ew: 0);
        Check(best != null && best.Type == "Town", "a prefix finds the town");
        best = atlas.FindBest("Cave Hermit", hasPos: false, ns: 0, ew: 0);
        Check(best == null, "a place with no map position is never an arrow target");
        best = atlas.FindBest("renald", hasPos: false, ns: 0, ew: 0);
        Check(best != null && best.Type == "Vendor", "a word inside a name");
        Check(atlas.FindBest("zzz", false, 0, 0) == null, "no match");

        var portals = atlas.Search("", "Portal", 50);
        Check(portals.Count == 2 && portals.TrueForAll(p => p.Type == "Portal"), "type filter");
        var hits = atlas.Search("Holtburg", null, 50);
        Check(hits.Count == 4 && hits[0].Type == "Town", "search: exact names first, town first");
        Check(atlas.Search("glimmering cave", null, 50).Count == 1, "case-insensitive");
        Check(atlas.Search("cave glimmering", null, 50).Count == 1, "every word, any order");

        // "a || b || c" (Oracle's Dynamic Stamps): the first candidate on the map, whole words only.
        Check(atlas.FirstOnMap("8.8S, 53.1E || Holtburg", false, 0, 0) == "8.8S, 53.1E", "coordinates first");
        Check(atlas.FirstOnMap("Nowhere Keep || Holtburg || Glimmering Cave", false, 0, 0) == "Holtburg", "the first place on the map");
        Check(atlas.FirstOnMap("Cave Hermit || Holtburg", false, 0, 0) == "Holtburg", "a place with no map position is skipped");
        Check(atlas.FirstOnMap("olt || Holtburg", false, 0, 0) == "Holtburg", "inside a word ('olt' in Holtburg) doesn't count");
        Check(atlas.FirstOnMap("zzz || qqq", false, 0, 0) == null, "none on the map");
        Check(atlas.FirstOnMap("  nowhere keep ||  || HOLTBURG  ", false, 0, 0) == "HOLTBURG", "extra spaces, empty candidates and case are fine");
        Check(atlas.FirstOnMap("", false, 0, 0) == null, "no candidates at all");

        var p2 = atlas.ById(2)!;
        Check(p2.HasDest && p2.DestOnMap, "portal destination");
        Near(p2.DestNs, 42.08, 1e-9, "portal destination NS");
        var p3 = atlas.ById(3)!;
        Check(!p3.OnMap && p3.HasDest && p3.DestOnMap, "a portal inside a dungeon");
        Check(atlas.ById(6)!.MinLevel == 12 && atlas.ById(6)!.Lb == "01AB", "dungeon fields");
        Check(atlas.Nearest("Lifestone", 42.0, 33.6, 500)?.Id == 4, "nearest lifestone");
        Check(atlas.Nearest("Lifestone", 0, 0, 500) == null, "none in range");
        Check(atlas.PortalByWcid(100, 0, 0)?.Id == 2, "portal by weenie class");
    }

    // ── Arrow ────────────────────────────────────────────────────────────────

    private static void ArrowSingleTarget()
    {
        var a = new ArrowGuide();
        Check(!a.Active, "starts empty");
        a.SetTarget("Holtburg", "Town", 42.08, 33.6);
        Check(a.Active && a.Current!.Name == "Holtburg", "set");
        Check(a.Update(true, 40, 30, false) == ArrowEvent.None, "far away: nothing");
        Check(a.Update(true, 42.08, 33.6 + 5 / 240.0, false) == ArrowEvent.Arrived, "within 10 yd: arrived");
        Check(!a.Active, "cleared on arrival");
        a.SetTarget("Somewhere", "", 1, 1);
        Check(a.Update(false, 1, 1, false) == ArrowEvent.None, "no position: no arrival");
        int v = a.Version;
        a.Clear();
        Check(!a.Active && a.Version > v, "clear");
    }

    private static void ArrowAlongRoute()
    {
        var portals = new List<PortalLink> { new(-30.1, 70.1, 40, 40, "Town Portal") };
        var recalls = new List<RecallLink> { new(-30, 70, "Town Recall", PortalRoute.RecallCostUnits(5)) };
        var route = PortalRoute.Plan(portals, recalls, 0, 0, 40.2, 40.2, out _, out _);
        var a = new ArrowGuide();
        a.SetRoute(route, "Goal", "Town", 40.2, 40.2);
        Check(a.Count == 3, "recall, portal, place");
        Check(a.Current!.Kind == ArrowStepKind.Recall && !a.Current.OnMap, "first: cast the recall");
        Check(a.Update(true, 0, 0, false) == ArrowEvent.None, "waits for the recall");
        Check(a.Update(true, -30, 70, true) == ArrowEvent.Advanced, "teleport: next step");
        Check(a.Current!.Kind == ArrowStepKind.Portal && a.Current.Name == "Town Portal", "then the portal");
        Check(a.Update(true, -30.1, 70.1, false) == ArrowEvent.None, "standing on the portal isn't through it");
        Check(a.Update(true, 40, 40, true) == ArrowEvent.Advanced, "through the portal");
        Check(a.Current!.Kind == ArrowStepKind.Place && a.Current.Name == "Goal", "then the place");
        Check(a.Update(true, 40.2, 40.2, false) == ArrowEvent.Arrived && !a.Active, "arrived");

        a.SetRoute(PortalRoute.Plan(NoPortals, null, 0, 0, 1, 1, out _, out _), "Near", "", 1, 1);
        Check(a.Count == 1 && a.Current!.Kind == ArrowStepKind.Place, "no teleports: just the place");
    }

    // ── Favorites / Recent ───────────────────────────────────────────────────

    private static void FavoritesAndRecent()
    {
        var s = new AtlasStore();
        var holt = new SavedPlace { Name = "Holtburg", Type = "Town", Ns = 42.08, Ew = 33.6 };
        Check(s.ToggleFavorite(holt) && s.IsFavorite(holt), "add favorite");
        Check(!s.ToggleFavorite(new SavedPlace { Name = "holtburg", Ns = 42.09, Ew = 33.61 }), "same place toggles off");
        Check(s.Favorites.Count == 0, "removed");
        s.ToggleFavorite(holt);
        for (int i = 0; i < 25; i++) s.Touch(new SavedPlace { Name = "P" + i, Ns = i, Ew = i });
        s.Touch(new SavedPlace { Name = "P5", Ns = 5, Ew = 5 });
        Check(s.Recent.Count == AtlasStore.MaxRecent, "recent is capped");
        Check(s.Recent[0].Name == "P5" && s.Recent[1].Name == "P24", "touched moves to the front");
        var back = AtlasStore.Parse(s.Serialize());
        Check(back.Favorites.Count == 1 && back.Favorites[0].Name == "Holtburg" && back.Favorites[0].Type == "Town", "favorites survive");
        Check(back.Recent.Count == AtlasStore.MaxRecent && back.Recent[0].Name == "P5", "recent survives in order");
        Near(back.Favorites[0].Ns, 42.08, 1e-9, "coordinates survive");
        Check(back.RemoveFavorite("HOLTBURG") && back.Favorites.Count == 0, "remove by name");

        // Every client saves atlas.txt: a save applies this client's changes to the file on disk
        // (MergeInto), so another client's favorite survives (2026-11-01).
        var exp = new SavedPlace { Name = "Abandoned Experiment (Conquest)", Type = "Dungeon", Ns = 42.45, Ew = 56.05 };
        var yaraq = new SavedPlace { Name = "Yaraq", Type = "Town", Ns = -21.5, Ew = -1.8 };
        var a = new AtlasStore();          // the client that adds the dungeon
        a.ToggleFavorite(exp);
        var disk = AtlasStore.Parse(a.Serialize());
        a.MergeInto(disk);
        var b = AtlasStore.Parse(new AtlasStore().Serialize());   // another client, loaded before that save
        b.Touch(yaraq);
        b.MergeInto(AtlasStore.Parse(a.Serialize()));
        Check(b.IsFavorite(exp), "another client's save keeps the favorite it never had");
        Check(b.Recent.Count == 1 && b.Recent[0].Name == "Yaraq", "and puts its own trip in Recent");
        var c = AtlasStore.Parse(b.Serialize());
        c.RemoveFavorite("Abandoned Experiment (Conquest)");
        c.MergeInto(AtlasStore.Parse(b.Serialize()));
        Check(!c.IsFavorite(exp), "a removal on one client sticks (the disk copy still had it)");
        var d = AtlasStore.Parse(b.Serialize());
        d.ClearRecent();
        d.MergeInto(AtlasStore.Parse(b.Serialize()));
        Check(d.Recent.Count == 0 && d.IsFavorite(exp), "Clear recent sticks; favorites stay");
        var e = new AtlasStore();
        e.MergeInto(AtlasStore.Parse(b.Serialize()));
        e.MergeInto(AtlasStore.Parse(e.Serialize()));
        Check(e.Favorites.Count == 1 && e.Recent.Count == 1, "merging the same file twice adds nothing");

        // Names find favorites, with or without a bracketed tag.
        Check(b.FindFavorite("abandoned experiment")?.SameAs(exp) == true, "a favorite by its name without the tag");
        Check(b.FindFavorite("Abandoned Experiment (Conquest)")?.SameAs(exp) == true, "and with it");
        Check(b.FindFavorite("Abandoned") == null, "not by part of a word");
        Check(b.FindFavorite("") == null, "nothing for nothing");
    }

    /// <summary>
    /// The /rnav words that used to answer nothing (2026-10-28: every command answers): "recent" without
    /// "clear", "arrive" alone or with a wrong number, an unknown word. The rest of /rnav already
    /// answered; RunCommand itself needs the game, so its lines are checked here.
    /// </summary>
    private static void CommandReplies()
    {
        var s = new AtlasStore();
        Check(NavReplies.Recent(s.Recent).StartsWith("no recent places", StringComparison.Ordinal), "recent: none yet");
        s.Touch(new SavedPlace { Name = "Holtburg", Ns = 42.08, Ew = 33.6 });
        s.Touch(new SavedPlace { Name = "Yaraq", Ns = -21.5, Ew = -1.8 });
        string recent = NavReplies.Recent(s.Recent);
        Check(recent.Contains("(2): Yaraq, Holtburg") && recent.Contains("/rnav recent clear"), "recent: newest first, and how to clear: " + recent);

        foreach (var (typed, ok) in new[] { ("10", true), ("2", true), ("200", true), ("1", false), ("201", false), ("far", false), ("", false) })
        {
            Check(NavReplies.TryArrive(typed, out double yd) == ok, $"arrive '{typed}' {(ok ? "taken" : "refused")}");
            if (ok) Check(NavReplies.Arrived(yd).Contains($"{yd:F0} yd"), $"arrive {typed}: the new distance");
            else Check(NavReplies.ArriveUsage(typed, 10).Contains("/rnav arrive <yards 2-200>"), $"arrive '{typed}': the usage");
        }
        Check(NavReplies.ArriveUsage("far", 10).StartsWith("'far' isn't 2-200 yd.", StringComparison.Ordinal), "arrive far: says why");
        Check(NavReplies.ArriveUsage("", 12).Contains("within 12 yd"), "arrive alone: the distance now");
        Check(NavReplies.Unknown("gogo") == "unknown 'gogo' — /rnav help", "an unknown word");
    }

    // ── Coordinates ──────────────────────────────────────────────────────────

    private static void CoordinateParsing()
    {
        Check(NavCoords.TryParseLoc("42.1N, 33.6E", out double ns, out double ew) && ns == 42.1 && ew == 33.6, "/loc style");
        Check(NavCoords.TryParseLoc("21.5S 1.8W", out ns, out ew) && ns == -21.5 && ew == -1.8, "south and west");
        Check(NavCoords.TryParseLoc("33.6E 42.1N", out ns, out ew) && ns == 42.1 && ew == 33.6, "letters set the order");
        Check(NavCoords.TryParseLoc("42.1 N, 33.6 E", out ns, out ew) && ns == 42.1 && ew == 33.6, "spaced letters");
        Check(!NavCoords.TryParseLoc("42.1N 33.6N", out _, out _), "two N/S refused");
        Check(!NavCoords.TryParseLoc("Holtburg", out _, out _), "a name");
        Check(!NavCoords.LooksLikeCoords("Area 51"), "a name with a number");
        Check(NavCoords.LooksLikeCoords("42.1, 33.6"), "bare numbers are coordinates");

        Check(NavCoords.TrySplitCoordsAndName("42.1N, 33.6E Holtburg Lifestone", out ns, out ew, out string name)
              && ns == 42.1 && ew == 33.6 && name == "Holtburg Lifestone", "coords then a name");
        Check(NavCoords.TrySplitCoordsAndName("42.1 N, 33.6 E Holtburg", out _, out _, out name) && name == "Holtburg", "spaced letters then a name");
        Check(NavCoords.TrySplitCoordsAndName("42.1N 33.6E Eastham", out _, out ew, out name) && ew == 33.6 && name == "Eastham",
            "a name starting with a compass letter");
        Check(NavCoords.TrySplitCoordsAndName("42.1N 33.6E", out _, out _, out name) && name == "", "no name");
        Check(NavCoords.Fmt(-21.51, -1.82) == "21.5S, 1.8W", "format");
    }

    // ── The generated data (RynthCore's tools/navdata/NavData), when it's beside this repo ──

    private static void RealData()
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "RynthCore", "tools", "navdata", "NavData"));
        string locs = Path.Combine(dir, "locations.json"), tsv = Path.Combine(dir, "portals.tsv");
        if (!File.Exists(locs) || !File.Exists(tsv)) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        Atlas atlas = Atlas.Load(locs);
        Check(atlas.Locations.Count > 1000 && atlas.Recalls.Count > 10, "the real Atlas loads");
        var holt = atlas.FindBest("Holtburg", false, 0, 0);
        Check(holt != null && holt.Type == "Town", "Holtburg is a town");
        Near(holt?.Ns ?? double.NaN, 42.1, 0.1, "Holtburg NS");
        Near(holt?.Ew ?? double.NaN, 33.6, 0.1, "Holtburg EW");
        var yaraq = atlas.FindBest("Yaraq", false, 0, 0)!;

        var portals = new List<PortalLink>();
        foreach (string line in File.ReadAllLines(tsv))
        {
            string[] f = line.Split('\t');
            if (f.Length < 5) continue;
            portals.Add(new PortalLink(double.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture), f[4]));
        }
        Check(portals.Count > 300, "the real portals.tsv loads");

        // Yaraq to Holtburg: portals only, then with recalls a character might know.
        double direct = NavCoords.Distance(yaraq.Ns, yaraq.Ew, holt!.Ns, holt.Ew);
        var steps = PortalRoute.Plan(portals, null, yaraq.Ns, yaraq.Ew, holt.Ns, holt.Ew, out double est, out int used);
        Check(est <= direct + 1e-6, "a plan is never worse than walking");
        Console.WriteLine($"     Yaraq -> Holtburg, portals only: {used} teleport(s), ~{est:F0} vs {direct:F0} walking");
        foreach (var s in steps) Console.WriteLine($"       {(s.UseRecall ? "recall" : s.UsePortal ? "portal" : "walk  ")} {s.Label} {NavCoords.Fmt(s.Ns, s.Ew)}");

        var mem = new RecallMemory();
        mem.Set("w|c", RecallSlot.Sanctuary, new RecallSpot { OnMap = true, Ns = 42.1, Ew = 33.5, Label = "Holtburg" });
        var known = new HashSet<int> { 2041, 2358, 3865 };   // Aerlinthe, Lyceum, Glenden Wood
        var notes = new List<string>();
        var recalls = RecallPlanner.Build(atlas, known, mem, "w|c", notes);
        steps = PortalRoute.Plan(portals, RecallPlanner.Links(recalls), yaraq.Ns, yaraq.Ew, holt.Ns, holt.Ew, out est, out used);
        Check(steps.Count > 0 && steps[0].UseRecall && recalls[steps[0].RecallIndex].Command == "@lifestone",
            "with a lifestone in Holtburg, @lifestone is the way home");
        Console.WriteLine($"     ... with recalls: {used} teleport(s), ~{est:F0}; first: {steps[0].Label}");
    }

    // ── Going into a dungeon: the goto ends by walking into its entrance portal ──────

    private const string EntranceJson = @"{
  ""locations"": [
    {""id"":1,""name"":""Holtburg"",""type"":""Town"",""ns"":42.08,""ew"":33.6},
    {""id"":2,""name"":""Deep Vault"",""type"":""Dungeon"",""cell"":""A9B30005"",""place"":""outdoor"",""ns"":41.0,""ew"":33.0,""lb"":""01AB""},
    {""id"":3,""name"":""Deep Vault"",""type"":""Portal"",""cell"":""A9B30005"",""place"":""outdoor"",""ns"":41.0,""ew"":33.0,""dest"":{""cell"":""01AB0100"",""place"":""dungeon"",""name"":""Deep Vault""}},
    {""id"":4,""name"":""Deep Vault"",""type"":""Portal"",""cell"":""B0B00005"",""place"":""outdoor"",""ns"":43.0,""ew"":34.0,""dest"":{""cell"":""01AB0100"",""place"":""dungeon"",""name"":""Deep Vault""},""quest"":true},
    {""id"":5,""name"":""Deep Vault"",""type"":""Portal"",""cell"":""B1B00005"",""place"":""outdoor"",""ns"":42.2,""ew"":33.7,""dest"":{""cell"":""01AB0100"",""place"":""dungeon"",""name"":""Deep Vault""},""closed"":true},
    {""id"":6,""name"":""Sunken Hall"",""type"":""Dungeon"",""cell"":""02CD0100"",""place"":""dungeon"",""lb"":""02CD""},
    {""id"":7,""name"":""Inner Gate"",""type"":""Portal"",""cell"":""01AB0110"",""place"":""dungeon"",""dest"":{""cell"":""02CD0100"",""place"":""dungeon"",""name"":""Sunken Hall""}},
    {""id"":8,""name"":""Lone Cave"",""type"":""Dungeon"",""cell"":""A7B70005"",""place"":""outdoor"",""ns"":45.0,""ew"":32.0,""lb"":""03EF""}
  ]
}";

    private static void DungeonEntrance()
    {
        Atlas atlas = Atlas.Parse(EntranceJson);
        AtlasLocation town = atlas.ById(1)!, vault = atlas.ById(2)!, sunken = atlas.ById(6)!, inner = atlas.ById(7)!, lone = atlas.ById(8)!;
        Check(!Entrance.IsEntrance(town) && Entrance.IsEntrance(vault) && Entrance.IsEntrance(atlas.ById(3)!), "dungeons and portals are entrances, towns aren't");

        // Standing at Holtburg (42.08, 33.6): the closed entrance (nearest) is skipped, and the open
        // entrance that needs no quest wins over the nearer quest one.
        AtlasLocation? p = Entrance.PortalFor(atlas, vault, true, 42.08, 33.6, out string why);
        Check(p != null && p.Id == 3 && why.Length == 0, $"nearest open, no-quest entrance (got {p?.Id})");
        Check(p!.Ns == 41.0 && p.Ew == 33.0, "the entrance's own map position, not the inside");

        p = Entrance.PortalFor(atlas, lone, true, 42, 33, out why);
        Check(p == lone, "a dungeon with no portal in the Atlas: its own position (its entrance)");

        p = Entrance.PortalFor(atlas, sunken, true, 42, 33, out why);
        Check(p == null && why.Contains("Sunken Hall") && why.Contains("no position on the map"), "an entrance inside another dungeon: refused, with the reason");

        p = Entrance.PortalFor(atlas, inner, true, 42, 33, out why);
        Check(p == null && why.Contains("inside a dungeon"), "a portal inside a dungeon: refused, with the reason");

        Check(Entrance.ArrivedText("Deep Vault", "Dungeon") == "arrived inside Deep Vault", "the arrival line for a dungeon");
        Check(Entrance.NoTeleportText("Deep Vault").StartsWith("stopped: Deep Vault's portal didn't take you in"), "the stop line when the portal doesn't fire");
    }

    // The real location database: dungeons sit at their entrance portal (outside), not inside.
    private static void RealDungeonEntrances()
    {
        string locs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "RynthCore", "tools", "navdata", "NavData", "locations.json"));
        if (!File.Exists(locs)) { Console.WriteLine("     (no generated NavData beside this repo: skipped)"); return; }
        Atlas atlas = Atlas.Load(locs);
        foreach (string name in new[] { "Matron Hive West", "Mines of Despair", "Glimmering Cave" })
        {
            AtlasLocation? d = null;
            foreach (var l in atlas.Locations) if (l.Type == "Dungeon" && l.Name == name) { d = l; break; }
            if (d == null) { Console.WriteLine($"     ({name} isn't in this database)"); continue; }
            Check(d.OnMap && d.Place != "dungeon", $"{name}: the Dungeon entry is on the map ({d.Place} cell {d.Cell})");
            AtlasLocation? p = Entrance.PortalFor(atlas, d, false, 0, 0, out string why);
            Check(p != null && p.Type == "Portal" && p.HasDest && !p.DestOnMap && p.DestName == name && p.Place != "dungeon",
                $"{name}: walks into an entrance portal on the map that leads inside ({p?.Ns:F2}, {p?.Ew:F2}, dest {p?.DestPlace})");
            Console.WriteLine($"     {name}: entrance {NavCoords.Fmt(p?.Ns ?? 0, p?.Ew ?? 0)} ({p?.Place} cell {p?.Cell}) -> inside {p?.DestPlace}");
        }
        int dungeons = 0, onMap = 0, withPortal = 0;
        foreach (var l in atlas.Locations)
        {
            if (l.Type != "Dungeon") continue;
            dungeons++;
            if (l.OnMap) onMap++;
            if (Entrance.PortalFor(atlas, l, false, 0, 0, out _) is { Type: "Portal" }) withPortal++;
        }
        Console.WriteLine($"     {dungeons} dungeons: {onMap} with a map position, {withPortal} with an entrance portal on the map");
        Check(onMap == dungeons, "every dungeon in the database has its entrance's map position");
    }

    private static void RouteEndsOnAPortalStep()
    {
        // A plain walk becomes one portal step at the entrance.
        List<RouteStep> steps = Entrance.EndingInPortal(null, 41.0, 33.0, "Deep Vault");
        Check(steps.Count == 1 && steps[0].UsePortal && !steps[0].UseRecall && steps[0].Ns == 41.0 && steps[0].Ew == 33.0, "walk -> one portal step");

        // A route (a portal, then a walk to the entrance): the last walk becomes the portal step.
        var route = new List<RouteStep> { new(-21.0, -1.6, true, false, "Holtburg Portal"), new(41.0, 33.0, false, false, "walk") };
        steps = Entrance.EndingInPortal(route, 41.0, 33.0, "Deep Vault");
        Check(steps.Count == 2 && steps[0].Label == "Holtburg Portal" && steps[1].UsePortal && steps[1].Label == "Deep Vault", "route -> its last walk is the entrance's portal step");
        Check(route[1].UsePortal == false, "the planner's own list isn't changed");

        // A route that ends somewhere else (a recall landing): the portal step is added after it.
        var viaRecall = new List<RouteStep> { new(42.1, 33.5, false, true, "Lifestone") };
        steps = Entrance.EndingInPortal(viaRecall, 41.0, 33.0, "Deep Vault");
        Check(steps.Count == 2 && steps[0].UseRecall && steps[1].UsePortal, "after a recall, walk on into the portal");
    }

    // Where the plugin reads its tiles and Atlas from: env var, then rynthnav.json, then the default.
    private static void NavDataFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rnav-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string settings = Path.Combine(dir, "rynthnav.json");
        try
        {
            var c = RynthCore.Plugin.RynthNav.NavDataConfig.Resolve(null, settings);
            Check(c.Dir == @"C:\Games\RynthCore\NavData" && c.Source == "default", "no settings: the default folder");
            File.WriteAllText(settings, "{ \"navDataDir\": \"D:\\\\AC\\\\NavData\\\\\" }");
            c = RynthCore.Plugin.RynthNav.NavDataConfig.Resolve(null, settings);
            Check(c.Dir == @"D:\AC\NavData" && c.Source == settings, "rynthnav.json's navDataDir, trailing slash trimmed");
            c = RynthCore.Plugin.RynthNav.NavDataConfig.Resolve(@"E:\Nav", settings);
            Check(c.Dir == @"E:\Nav" && c.Source == "RYNTHNAV_NAVDATA", "the environment variable wins");
            c = RynthCore.Plugin.RynthNav.NavDataConfig.Resolve(@"relative\dir", settings);
            Check(c.Dir == @"D:\AC\NavData" && c.Source.Contains("ignored"), "a relative env path is ignored, with a note");
            File.WriteAllText(settings, "{ \"navDataDir\": \"NavData\" }");
            c = RynthCore.Plugin.RynthNav.NavDataConfig.Resolve(null, settings);
            Check(c.Dir == @"C:\Games\RynthCore\NavData" && c.Source.Contains("ignored"), "a relative setting falls back to the default");
            File.WriteAllText(settings, "not json");
            c = RynthCore.Plugin.RynthNav.NavDataConfig.Resolve(null, settings);
            Check(c.Dir == @"C:\Games\RynthCore\NavData", "a broken file falls back to the default");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void BearingAndDistance()
    {
        Near(NavCoords.Distance(0, 0, 1, 0), 240, 1e-9, "a degree is 240 yd");
        Near(NavCoords.Bearing(0, 0, 1, 0), 0, 1e-9, "north");
        Near(NavCoords.Bearing(0, 0, 0, 1), 90, 1e-9, "east");
        Near(NavCoords.Bearing(0, 0, -1, 0), 180, 1e-9, "south");
        Near(NavCoords.Bearing(0, 0, 0, -1), 270, 1e-9, "west");
        Check(NavCoords.CompassPoint(44) == "NE" && NavCoords.CompassPoint(359) == "N" && NavCoords.CompassPoint(200) == "SSW", "compass points");
        NavCoords.FromCell(0xA9B40019, 84.0, 7.1, out double ns, out double ew);
        Near(ns, 42.08, 0.01, "Holtburg NS from its cell");
        Near(ew, 33.6, 0.01, "Holtburg EW from its cell");
        Near(NavCoords.NsFromWorld(NavCoords.WorldY(-33.54)), -33.54, 1e-9, "world round trip");
    }
}
