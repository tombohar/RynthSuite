using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// 11-18: a goto sent a non-player killer through the PK-only Olthoi Tunnel (PortalBitmask 829), the
// back-off from it crossed a landblock line and was taken for its teleport, and the replan picked
// the same portal again: ~40 refusals. Portal rules (PortalRules), bans (a refused portal left out),
// the teleport test, and the other ways there (PortalRoute.PlanOptions).
internal static partial class Program
{
    private static void RunWays()
    {
        Run(nameof(PortalRulesAgainstTheCharacter), PortalRulesAgainstTheCharacter);
        Run(nameof(PkStatusFromTheObjectFlags), PkStatusFromTheObjectFlags);
        Run(nameof(ServerRefusals), ServerRefusals);
        Run(nameof(TeleportOrRun), TeleportOrRun);
        Run(nameof(PortalsTsvWithAndWithoutRules), PortalsTsvWithAndWithoutRules);
        Run(nameof(BannedPortalIsLeftOut), BannedPortalIsLeftOut);
        Run(nameof(OtherWaysThere), OtherWaysThere);
        Run(nameof(OtherWaysWhenWalkingIsBest), OtherWaysWhenWalkingIsBest);
        Run(nameof(OtherWaysFromADungeon), OtherWaysFromADungeon);
        Run(nameof(TravelPlanCarriesTheWays), TravelPlanCarriesTheWays);
        Run(nameof(TownNetworkPkRules), TownNetworkPkRules);
        Run(nameof(RealOlthoiTunnelTrip), RealOlthoiTunnelTrip);
    }

    private static readonly Traveller Npk = new(100, PortalRules.StatusNpk, false, false);
    private static readonly Traveller Pk = new(100, PortalRules.StatusPk, false, false);

    private static void PkStatusFromTheObjectFlags()
    {
        // 11-20: PropertyInt 134 wasn't there for the player; their object's description flags are.
        const uint Player = PortalRules.BfPlayer | 0x10;   // a player, attackable
        Check(PortalRules.StatusFromBitfield(Player) == PortalRules.StatusNpk, "a player with no PK flag is a non-player killer");
        Check(PortalRules.StatusFromBitfield(Player | PortalRules.BfPlayerKiller) == PortalRules.StatusPk, "the player killer flag: PK");
        Check(PortalRules.StatusFromBitfield(Player | PortalRules.BfPkLite) == PortalRules.StatusPkLite, "the PK lite flag: PK lite");
        Check(PortalRules.StatusFromBitfield(Player | PortalRules.BfFreePk) == PortalRules.StatusFree, "the free PK flag: free");
        Check(PortalRules.StatusFromBitfield(0) == 0, "no flags: not known");
        Check(PortalRules.StatusFromBitfield(PortalRules.BfPlayerKiller) == 0, "not a player: not known");
        const int OlthoiTunnel = 829;
        var npk = new Traveller(275, PortalRules.StatusFromBitfield(Player), false, false);
        Check(PortalRules.Refusal(0, 0, OlthoiTunnel, npk).Contains("non-player killers"), "an NPK read from the flags is kept out of the Olthoi Tunnel");
        Check(PortalRules.Refusal(0, 0, PortalRules.NoPk | 1, npk) == "", "...and may take a portal that only refuses player killers");
        Check(PortalRules.StatusText(PortalRules.StatusFree) == "PK free", "the log names the free status");
    }

    private static void PortalRulesAgainstTheCharacter()
    {
        const int OlthoiTunnel = 829;   // NoNPK | NoPKLite | NoVitae | NoNewAccounts | NoSummon | NoRecall
        Check(PortalRules.Refusal(0, 0, OlthoiTunnel, Npk).Contains("non-player killers"), "the Olthoi Tunnel refuses a non-player killer");
        Check(PortalRules.Refusal(0, 0, OlthoiTunnel, Pk) == "", "...and lets a player killer through");
        Check(PortalRules.Refusal(0, 0, OlthoiTunnel, new Traveller(100, PortalRules.StatusPkLite, false, false)) != "", "...not a PK lite");
        Check(PortalRules.Refusal(0, 0, OlthoiTunnel, new Traveller(100, PortalRules.StatusPk, false, true)).Contains("vitae"), "...nor a PK with vitae");
        Check(PortalRules.Refusal(0, 0, OlthoiTunnel, new Traveller(100, 0, false, false)).Contains("isn't known"), "PK status not known: left out");
        Check(PortalRules.Refusal(0, 0, PortalRules.NoVitae | 1, new Traveller(100, PortalRules.StatusNpk, false, null)) != "", "vitae not known: left out");
        Check(PortalRules.Refusal(0, 0, PortalRules.Unrestricted, new Traveller(0, 0, false, null)) == "", "an open portal needs nothing known");
        Check(PortalRules.Refusal(50, 0, 1, new Traveller(40, 2, false, false)).Contains("level 50"), "level 40 can't take a level 50 portal");
        Check(PortalRules.Refusal(50, 0, 1, new Traveller(0, 2, false, false)).Contains("isn't known"), "level not known: left out");
        Check(PortalRules.Refusal(50, 0, 1, new Traveller(60, 2, false, false)) == "", "level 60 can");
        Check(PortalRules.Refusal(0, 0, PortalRules.OnlyOlthoi | 1, Npk) == "Olthoi only" && PortalRules.Refusal(0, 0, PortalRules.OnlyOlthoi | 1, new Traveller(100, 2, true, false)) == "", "Olthoi only");
        Check(PortalRules.Refusal(0, 0, PortalRules.NoNewAccounts | 1, Npk) == "", "two weeks old: can't be checked, nearly everyone passes (a refusal is learned)");
        Check(PortalRules.Refusal(0, 0, PortalRules.Closed, Pk) != "", "closed");
        var link = new PortalLink(12.5, 0, 67.4, 80.3, "Olthoi Tunnel", 0, 0, OlthoiTunnel);
        Check(link.HasRules && PortalRules.Refusal(link, Npk) != "" && !new PortalLink(1, 1, 2, 2, "x").HasRules, "a PortalLink carries its rules");
    }

    private static void ServerRefusals()
    {
        Check(PortalRules.RefusalFromServer(0x045D)?.Contains("non-player killers") == true, "NonPKsMayNotUsePortal (11-18's ~40 refusals)");
        Check(PortalRules.RefusalFromServer(0x04AB) != null && PortalRules.RefusalFromServer(0x0591) != null && PortalRules.RefusalFromServer(0x0474) != null,
            "level, vitae and quest refusals");
        Check(PortalRules.RefusalFromServer(0x04A7) == null, "teleported too recently: try again, not a refusal");
        Check(PortalRules.RefusalFromServer(0x04CC) == null, "a PK battle too recently: try again");
        Check(PortalRules.RefusalFromServer(0x0402) == null, "a fizzled spell isn't a portal's");
    }

    private static void TeleportOrRun()
    {
        Check(!PortalRules.IsTeleportJump(25, 2.5), "11-18: backing off 25 u in 2.5 s across a landblock line is not a teleport");
        Check(!PortalRules.IsTeleportJump(1.2, 0.1), "a tick of running");
        Check(!PortalRules.IsTeleportJump(35, 3.0), "a 3 s freeze while running");
        Check(PortalRules.IsTeleportJump(3000, 0.05), "a portal across the map");
        Check(PortalRules.IsTeleportJump(40, 0.2), "a short hop");
        Check(!PortalRules.IsTeleportJump(10, 0.01), "too short to tell from a stutter");
    }

    private static void PortalsTsvWithAndWithoutRules()
    {
        Check(PortalLink.TryParseTsv("12.5\t-0.0\t67.4\t80.3\tOlthoi Tunnel\t0\t0\t829\r", out PortalLink t, out bool rules)
              && rules && t.Bitmask == 829 && t.Name == "Olthoi Tunnel" && Math.Abs(t.DstEw - 80.3) < 1e-9, "a line with its rules");
        Check(PortalLink.TryParseTsv("42.1\t33.6\t-21.5\t-1.8\tYaraq Portal", out PortalLink o, out bool none)
              && !none && o.Bitmask == PortalRules.Unrestricted && !o.HasRules, "an old line: open to anyone");
        Check(PortalLink.TryParseTsv("1\t2\t3\t4\tHigh\t50\t0\t1", out PortalLink h, out _) && h.MinLevel == 50 && !h.HasSpot, "a level rule (no spot)");
        // From 11-19: exactly where it stands. Zaikhal's Olthoi Tunnel, in a cell under the street.
        Check(PortalLink.TryParseTsv("12.5\t-0.0\t67.4\t80.3\tOlthoi Tunnel\t0\t0\t829\t7F8F0123\t83.92\t21.12\t118.34", out PortalLink z, out _)
              && z.HasSpot && z.Indoor && z.Cell == 0x7F8F0123 && Math.Abs(z.Z - 118.34) < 1e-9
              && Math.Abs(z.WorldEast - (0x7F * 192 + 83.92)) < 1e-9 && Math.Abs(z.WorldNorth - (0x8F * 192 + 21.12)) < 1e-9, "a portal's exact spot, indoors");
        Check(PortalLink.TryParseTsv("42.1\t33.6\t-21.5\t-1.8\tYaraq Portal\t0\t0\t1\tA9B40020\t90.00\t20.00\t42.00", out PortalLink o2, out _)
              && o2.HasSpot && !o2.Indoor, "an outdoor one");
        Check(!PortalLink.TryParseTsv("", out _, out _) && !PortalLink.TryParseTsv("a\tb\tc\td", out _, out _), "junk lines");
    }

    // Start 0,0; goal 40,40. Portal A is right here; B a little farther; walking is ~13,600 u.
    private static readonly List<PortalLink> TwoWays = new()
    {
        new(0.1, 0, 39.9, 40, "Portal A"),
        new(0.5, 0.5, 39.5, 39.6, "Portal B"),
    };

    private static void BannedPortalIsLeftOut()
    {
        var a = PortalRoute.Plan(TwoWays, null, null, 0, 0, 40, 40, out double estA, out int usedA);
        Check(usedA == 1 && a.Exists(s => s.UsePortal && s.Label == "Portal A" && s.Via == PortalRoute.PortalKey(0)), "the plan takes A, and says which portal it is");
        var b = PortalRoute.Plan(TwoWays, null, null, 0, 0, 40, 40, out double estB, out int usedB, null, true,
            new HashSet<string> { PortalRoute.PortalKey(0) });
        Check(usedB == 1 && b.Exists(s => s.UsePortal && s.Label == "Portal B") && !b.Exists(s => s.Label == "Portal A"), "A refused: B");
        Check(estB > estA, "B costs more");
        var walk = PortalRoute.Plan(TwoWays, null, null, 0, 0, 40, 40, out double estW, out int usedW, null, true,
            new HashSet<string> { PortalRoute.PortalKey(0), PortalRoute.PortalKey(1) });
        Check(usedW == 0 && Math.Abs(estW - NavCoords.Distance(0, 0, 40, 40)) < 1e-6, "both refused: walk");
        var none = PortalRoute.Plan(TwoWays, null, null, 0, 0, 40, 40, out double estN, out _, (_, _, _, _) => false, true,
            new HashSet<string> { PortalRoute.PortalKey(0), PortalRoute.PortalKey(1) });
        Check(none.Count == 0 && double.IsPositiveInfinity(estN), "both refused and no walking: no way");
    }

    private static void OtherWaysThere()
    {
        List<RouteOption> ways = PortalRoute.PlanOptions(TwoWays, null, null, 0, 0, 40, 40, 4);
        Console.WriteLine("     ways: " + string.Join("; ", ways.Select(w => $"~{w.Est:F0} [{w.Signature}] avoids {string.Join(", ", w.Avoids)}")));
        Check(ways.Count == 3, $"three ways: A, B, walking ({ways.Count})");
        Check(ways.Select(w => w.Signature).Distinct().Count() == ways.Count, "each different");
        Check(ways.Zip(ways.Skip(1)).All(p => p.First.Est <= p.Second.Est), "cheapest first");
        Check(ways[0].Steps.Exists(s => s.Label == "Portal A") && ways[0].Avoids.Count == 0, "1: the plan (A)");
        Check(ways[1].Steps.Exists(s => s.Label == "Portal B") && ways[1].Avoids.Contains("Portal A"), "2: B, avoiding A");
        Check(ways[2].Teleports == 0 && ways[2].Avoids.Count > 0, "3: walking all the way");
        // Asked again with way 2's bans (from somewhere else on the way): the same kind of way.
        var again = PortalRoute.Plan(TwoWays, null, null, 0.05, 0.05, 40, 40, out _, out _, null, true, ways[1].Bans);
        Check(again.Exists(s => s.Label == "Portal B") && !again.Exists(s => s.Label == "Portal A"), "way 2's bans keep to B");
        Check(PortalRoute.PlanOptions(TwoWays, null, null, 0, 0, 40, 40, 1).Count == 1, "one asked for, one given");
    }

    private static void OtherWaysWhenWalkingIsBest()
    {
        // 0.5 deg away: walking (170 u) beats the portal's 360 u; the way after it takes the portal.
        var near = new List<PortalLink> { new(0.05, 0, 0.5, 0.45, "Short Hop") };
        List<RouteOption> ways = PortalRoute.PlanOptions(near, null, null, 0, 0, 0.5, 0.5, 4);
        Check(ways.Count == 2 && ways[0].Teleports == 0 && ways[1].Teleports == 1, $"walking first, then the portal ({ways.Count})");
        Check(ways.Count == 2 && ways[1].Avoids.Contains("walking all the way") && ways[1].Bans.Contains(PortalRoute.WalkKey), "the portal way avoids walking all the way");
        var again = PortalRoute.Plan(near, null, null, 0, 0, 0.5, 0.5, out _, out int used, null, true, ways[1].Bans);
        Check(used == 1, "planned again with its bans it still takes the portal");
    }

    private static void OtherWaysFromADungeon()
    {
        var recalls = new List<RecallLink> { new(39.5, 39.5, "Near Recall", PortalRoute.RecallCostUnits(5)), new(30, 30, "Far Recall", PortalRoute.RecallCostUnits(5)) };
        List<RouteOption> ways = PortalRoute.PlanOptions(TwoWays, recalls, null, 0, 0, 40, 40, 4, null, startOnMap: false);
        Check(ways.Count >= 2 && ways.All(w => w.Steps.Count > 0 && w.Steps[0].UseRecall), "every way out of a dungeon starts with a recall");
        Check(ways.Count >= 2 && ways[0].Steps[0].Label == "Near Recall" && ways[1].Steps[0].Label == "Far Recall", "the nearer recall first, then the other");
        Check(ways[0].Steps[0].Via == PortalRoute.RecallKey("Near Recall"), "a recall's key is its name");
    }

    private static void TravelPlanCarriesTheWays()
    {
        var r = TravelPlan.Plan(TwoWays, null, null, true, true, true, 0, 0, 40, 40, null, true, "Goal", null, 4);
        Check(r.Options.Count == 3 && r.Steps != null && r.Steps.Exists(s => s.Label == "Portal A"), "the plan and its ways");
        Check(r.Options[2].Steps == null && r.Options[2].Teleports == 0, "a walking way has no steps (walk straight there), as Result does");
        var banned = TravelPlan.Plan(TwoWays, null, null, true, true, true, 0, 0, 40, 40, null, true, "Goal",
            new HashSet<string> { PortalRoute.PortalKey(0) }, 4);
        Check(banned.Steps != null && banned.Steps.Exists(s => s.Label == "Portal B") && banned.Options.Count == 2, "A banned: B, then walking");
        var off = TravelPlan.Plan(TwoWays, null, null, false, true, true, 0, 0, 40, 40, null, true, "Goal", null, 4);
        Check(off.Options.Count == 1 && off.Options[0].Teleports == 0, "portals off: the one way is walking");
    }

    private static void TownNetworkPkRules()
    {
        var npkOnly = new TownNetRules { NoPk = true, NoPkLite = true };
        Check(npkOnly.Blocks(Npk) == "", "a no-PK network portal lets a non-player killer through");
        Check(npkOnly.Blocks(Pk).Contains("player killers"), "...not a player killer");
        Check(npkOnly.Blocks(90, false).Contains("isn't known"), "PK status not known: left out, as before");
        Check(new TownNetRules { NoVitae = true }.Blocks(new Traveller(90, 2, false, true)).Contains("vitae")
              && new TownNetRules { NoVitae = true }.Blocks(new Traveller(90, 2, false, false)) == "", "vitae");
        Check(new TownNetRules { NoNewAccounts = true }.Blocks(Npk) == "", "two weeks old: not checked");
        Check(new TownNetRules { AccountRequirements = 2 }.Blocks(Npk) == "account rules", "an account requirement above 1: left out");
    }

    /// <summary>NavData with portal rules: RYNTHNAV_NAVDATA, else the one beside the repo when it has them.</summary>
    private static string? RulesNavDataDir()
    {
        string? env = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA");
        foreach (string? dir in new[] { env, RealNavDataDir() })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string tsv = Path.Combine(dir, "portals.tsv");
            if (!File.Exists(tsv) || !File.Exists(Path.Combine(dir, TownNet.FileName)) || !File.Exists(Path.Combine(dir, "locations.json"))) continue;
            string? first = File.ReadLines(tsv).FirstOrDefault();
            if (first != null && PortalLink.TryParseTsv(first, out _, out bool rules) && rules) return dir;
        }
        return null;
    }

    private static void RealOlthoiTunnelTrip()
    {
        // 11-18 18:10: from a dungeon (landblock 01C9) to 87.5S 67.1W, knowing Recall Aphus Lassel.
        string? dir = RulesNavDataDir();
        if (dir == null) { Console.WriteLine("     (no NavData with portal rules: set RYNTHNAV_NAVDATA to a generated one; skipped)"); return; }
        var portals = File.ReadAllLines(Path.Combine(dir, "portals.tsv"))
            .Select(l => PortalLink.TryParseTsv(l, out PortalLink p, out _) ? p : (PortalLink?)null).Where(p => p != null).Select(p => p!.Value)
            .Where(p => !Entrance.IsDestroyed(p.Name)).ToList();
        TownNet t = TownNet.Parse(File.ReadAllText(Path.Combine(dir, TownNet.FileName)), out _)!;
        var aphus = new List<RecallLink> { new(2.26, 95.46, "Recall Aphus Lassel", PortalRoute.RecallCostUnits(5)) };
        var bans = new HashSet<string>();
        for (int i = 0; i < portals.Count; i++) if (PortalRules.Refusal(portals[i], Npk) != "") bans.Add(PortalRoute.PortalKey(i));
        Console.WriteLine($"     {portals.Count} portals, {portals.Count(p => p.HasRules)} with rules, {bans.Count} refuse a level 100 non-player killer");
        Check(bans.Count >= 16, "the Olthoi Tunnels (and others) refuse a non-player killer");

        var old = PortalRoute.Plan(portals, aphus, t.Links(Npk, null), 0, 0, -87.5, -67.1, out double estOld, out _, null, startOnMap: false);
        var now = PortalRoute.Plan(portals, aphus, t.Links(Npk, null), 0, 0, -87.5, -67.1, out double estNow, out _, null, startOnMap: false, bans);
        Console.WriteLine($"     without rules ~{estOld:F0}: {Describe(old)}");
        Console.WriteLine($"     with rules    ~{estNow:F0}: {Describe(now)}");
        Check(old.Exists(s => s.UsePortal && s.Label == "Olthoi Tunnel"), "without rules: the Olthoi Tunnel (what happened 11-18)");
        Check(now.Count > 0 && !now.Exists(s => s.UsePortal && s.Label == "Olthoi Tunnel"), "with them: never the Olthoi Tunnel");
        Check(now.Count > 0 && now[0].UseRecall, "...still out by the recall");

        // A PK may take it.
        var pkBans = new HashSet<string>();
        for (int i = 0; i < portals.Count; i++) if (PortalRules.Refusal(portals[i], Pk) != "") pkBans.Add(PortalRoute.PortalKey(i));
        var pk = PortalRoute.Plan(portals, aphus, t.Links(Pk, null), 0, 0, -87.5, -67.1, out _, out _, null, startOnMap: false, pkBans);
        Check(pk.Exists(s => s.UsePortal && s.Label == "Olthoi Tunnel"), "a player killer still goes by the Olthoi Tunnel");

        List<RouteOption> ways = PortalRoute.PlanOptions(portals, aphus, t.Links(Npk, null), 0, 0, -87.5, -67.1, 4, null, startOnMap: false, bans);
        foreach (RouteOption w in ways) Console.WriteLine($"     way ~{w.Est:F0}{(w.Avoids.Count > 0 ? " avoiding " + string.Join(", ", w.Avoids) : "")}: {Describe(w.Steps)}");
        Check(ways.Count >= 2 && ways.Select(w => w.Signature).Distinct().Count() == ways.Count, "other ways there, each different");
    }
}
