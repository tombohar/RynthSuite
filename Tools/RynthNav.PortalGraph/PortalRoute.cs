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

/// <summary>
/// Who is travelling, as the server's portal checks see them (ACE Portal.CheckUseRequirements):
/// their level, player killer status (PropertyInt 134), whether they're an Olthoi, and whether
/// vitae weakens them. 0 / null = not known yet.
/// </summary>
public readonly struct Traveller
{
    public readonly int Level;
    public readonly uint PkStatus;
    public readonly bool Olthoi;
    public readonly bool? Vitae;
    public Traveller(int level, uint pkStatus, bool olthoi, bool? vitae)
    { Level = level; PkStatus = pkStatus; Olthoi = olthoi; Vitae = vitae; }
    public override string ToString() =>
        $"level {(Level > 0 ? Level.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}, {PortalRules.StatusText(PkStatus)}"
        + (Olthoi ? ", Olthoi" : "") + (Vitae == null ? ", vitae ?" : Vitae.Value ? ", vitae" : "");
}

/// <summary>
/// The server's portal rules (ACE Portal.CheckUseRequirements and PortalBitmask), checked before a
/// plan may use a portal. A rule that can't be checked (the character's level or PK status not
/// known yet) leaves the portal out: a route that has to turn back costs more than one that goes
/// round. "Two weeks old" accounts (NoNewAccounts) can't be checked from the client at all and
/// nearly every account passes it, so it doesn't leave a portal out; a refusal is learned instead.
/// </summary>
public static class PortalRules
{
    // PortalBitmask (PropertyInt 111). No property = Unrestricted; 0 = no player may use it.
    public const int Closed = 0x0, Unrestricted = 0x1, NoPk = 0x2, NoPkLite = 0x4, NoNpk = 0x8,
        NoSummon = 0x10, NoRecall = 0x20, OnlyOlthoi = 0x40, NoOlthoi = 0x80, NoVitae = 0x100, NoNewAccounts = 0x200;
    // PlayerKillerStatus (PropertyInt 134).
    public const uint StatusNpk = 0x02, StatusPk = 0x04, StatusPkLite = 0x40;

    /// <summary>Why the server would refuse <paramref name="t"/> this portal ("" = it lets them through).</summary>
    public static string Refusal(int minLevel, int maxLevel, int bitmask, in Traveller t)
    {
        if (bitmask == Closed) return "closed to players";
        if (minLevel > 0 || maxLevel > 0)
        {
            if (t.Level <= 0) return minLevel > 0 ? $"needs level {minLevel} (your level isn't known yet)" : $"level {maxLevel} or lower (your level isn't known yet)";
            if (minLevel > 0 && t.Level < minLevel) return $"needs level {minLevel}";
            if (maxLevel > 0 && t.Level > maxLevel) return $"level {maxLevel} or lower";
        }
        if ((bitmask & OnlyOlthoi) != 0 && !t.Olthoi) return "Olthoi only";
        if ((bitmask & NoOlthoi) != 0 && t.Olthoi) return "no Olthoi";
        if ((bitmask & (NoPk | NoPkLite | NoNpk)) != 0)
        {
            if (t.PkStatus == 0) return "player killer rules (your PK status isn't known yet)";
            if ((bitmask & NoNpk) != 0 && t.PkStatus == StatusNpk) return "not for non-player killers";
            if ((bitmask & NoPk) != 0 && t.PkStatus == StatusPk) return "not for player killers";
            if ((bitmask & NoPkLite) != 0 && t.PkStatus == StatusPkLite) return "not for PK lites";
        }
        if ((bitmask & NoVitae) != 0)
        {
            if (t.Vitae == null) return "not with vitae (not known whether you have any)";
            if (t.Vitae.Value) return "not with vitae";
        }
        return "";
    }

    public static string Refusal(in PortalLink p, in Traveller t) => Refusal(p.MinLevel, p.MaxLevel, p.Bitmask, t);

    /// <summary>
    /// Between two looks at where you stand: a jump of <paramref name="units"/> in
    /// <paramref name="seconds"/> is a teleport when it is more than 15 u at more than 60 u a second
    /// (running is about 12). A landblock line crossed is not one: 11-18 backing off a portal 25 u
    /// across 7F8F -> 7F8E was taken for its teleport.
    /// </summary>
    public static bool IsTeleportJump(double units, double seconds) => units > 15.0 && units / Math.Max(0.001, seconds) > 60.0;

    // ObjectDescriptionFlags (the object's PublicWeenieDesc bitfield).
    public const uint BfPlayer = 0x8, BfPlayerKiller = 0x20, BfFreePk = 0x200000, BfPkLite = 0x2000000;
    public const uint StatusFree = 0x20;

    /// <summary>
    /// A player's PK status from their object's description flags (0 = not known: no flags, or not a
    /// player). 11-20 PropertyInt 134 wasn't there for the player on DreamWeave or Aelrynth, so every
    /// portal with PK rules was left out ("PK status ?"); the flags are always sent with the object.
    /// </summary>
    public static uint StatusFromBitfield(uint bitfield)
    {
        if ((bitfield & BfPlayer) == 0) return 0;
        if ((bitfield & BfPkLite) != 0) return StatusPkLite;
        if ((bitfield & BfPlayerKiller) != 0) return StatusPk;
        if ((bitfield & BfFreePk) != 0) return StatusFree;
        return StatusNpk;
    }

    /// <summary>"NPK", "PK", "PK lite", "PK status ?" for the log.</summary>
    public static string StatusText(uint status) => status switch
    {
        0 => "PK status ?",
        StatusNpk => "NPK",
        StatusPk => "PK",
        StatusPkLite => "PK lite",
        StatusFree => "PK free",
        _ => "PK status 0x" + status.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// A server refusal (WeenieError) that says this portal won't take this character, with the
    /// reason; null for anything else. "Teleported too recently" and "in a PK battle too recently"
    /// are not: they pass, and the portal is tried again.
    /// </summary>
    public static string? RefusalFromServer(uint weenieError) => weenieError switch
    {
        0x045C => "player killers may not use it",
        0x045D => "non-player killers may not use it",
        0x04F3 => "PK lites may not use it",
        0x04AA => "players may not use it",
        0x04AB => "you're not powerful enough (its level)",
        0x04AC => "you're too powerful (its level)",
        0x058F => "only Olthoi may use it",
        0x0590 => "Olthoi may not use it",
        0x0591 => "not while vitae weakens you",
        0x0592 => "the character or account must be two weeks old",
        0x0554 => "it needs Throne of Destiny",
        0x04A8 => "advocates only",
        0x0474 => "it needs a quest",   // YouMustCompleteQuestToUsePortal
        0x0446 => "the quest it needs was done too long ago",   // QuestSolvedTooLongAgo
        _ => null,
    };
}

/// <summary>
/// One directed portal: walk to (SrcNs,SrcEw), teleport, arrive (DstNs,DstEw). MinLevel / MaxLevel
/// (0 = none) and Bitmask (ACE's PortalBitmask; 1 = anyone) say who the server lets through: NavData's
/// portals.tsv carries them (RynthCore tools/navdata/gen_locations.py), and PortalRules checks them
/// against the character before a plan may use the portal. Cell and X, Y, Z (landblock-local) are
/// exactly where it stands (0 / NaN when the file doesn't say): a portal in a building or a cellar
/// (Indoor) has a floor of its own height, which the walker has to reach.
/// </summary>
public readonly struct PortalLink
{
    public readonly double SrcNs, SrcEw, DstNs, DstEw;
    public readonly string Name;
    public readonly int MinLevel, MaxLevel, Bitmask;
    public readonly uint Cell;
    public readonly double X, Y, Z;
    public PortalLink(double srcNs, double srcEw, double dstNs, double dstEw, string name)
        : this(srcNs, srcEw, dstNs, dstEw, name, 0, 0, PortalRules.Unrestricted) { }
    public PortalLink(double srcNs, double srcEw, double dstNs, double dstEw, string name, int minLevel, int maxLevel, int bitmask)
        : this(srcNs, srcEw, dstNs, dstEw, name, minLevel, maxLevel, bitmask, 0, double.NaN, double.NaN, double.NaN) { }
    public PortalLink(double srcNs, double srcEw, double dstNs, double dstEw, string name, int minLevel, int maxLevel, int bitmask,
        uint cell, double x, double y, double z)
    {
        SrcNs = srcNs; SrcEw = srcEw; DstNs = dstNs; DstEw = dstEw; Name = name; MinLevel = minLevel; MaxLevel = maxLevel; Bitmask = bitmask;
        Cell = cell; X = x; Y = y; Z = z;
    }

    /// <summary>The file says exactly where it stands (Cell, X, Y, Z).</summary>
    public bool HasSpot => Cell != 0 && !double.IsNaN(Z);
    /// <summary>It stands in a building's or a cellar's cell, not on the open ground.</summary>
    public bool Indoor => (Cell & 0xFFFF) >= 0x100;
    /// <summary>World coordinates of the spot (east, north; RynthNav's world units), NaN without one.</summary>
    public double WorldEast => HasSpot ? ((Cell >> 24) & 0xFF) * 192.0 + X : double.NaN;
    public double WorldNorth => HasSpot ? ((Cell >> 16) & 0xFF) * 192.0 + Y : double.NaN;

    /// <summary>Has a rule that may refuse someone (a level, or a PortalBitmask other than "anyone").</summary>
    public bool HasRules => MinLevel > 0 || MaxLevel > 0 || Bitmask != PortalRules.Unrestricted;

    /// <summary>
    /// One line of NavData's portals.tsv: srcNs, srcEw, dstNs, dstEw, name, then (NavData from 11-19 on)
    /// minLevel, maxLevel, bitmask, and cell (hex), x, y, z. An older line has no rules: the portal is
    /// open to anyone, as planned before. <paramref name="hasRules"/>: the line had the rule columns.
    /// </summary>
    public static bool TryParseTsv(string line, out PortalLink link, out bool hasRules)
    {
        link = default; hasRules = false;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var f = line.TrimEnd('\r', '\n').Split('\t');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles num = System.Globalization.NumberStyles.Float;
        if (f.Length < 4) return false;
        if (!double.TryParse(f[0], num, ci, out double sns) || !double.TryParse(f[1], num, ci, out double sew)
            || !double.TryParse(f[2], num, ci, out double dns) || !double.TryParse(f[3], num, ci, out double dew)) return false;
        string name = f.Length > 4 ? f[4] : "";
        int minLv = 0, maxLv = 0, mask = PortalRules.Unrestricted;
        if (f.Length > 7 && int.TryParse(f[5], System.Globalization.NumberStyles.Integer, ci, out int a5)
            && int.TryParse(f[6], System.Globalization.NumberStyles.Integer, ci, out int a6)
            && int.TryParse(f[7], System.Globalization.NumberStyles.Integer, ci, out int a7))
        { minLv = a5; maxLv = a6; mask = a7; hasRules = true; }
        uint cell = 0; double x = double.NaN, y = double.NaN, z = double.NaN;
        if (f.Length > 11 && uint.TryParse(f[8], System.Globalization.NumberStyles.HexNumber, ci, out uint c)
            && double.TryParse(f[9], num, ci, out double px) && double.TryParse(f[10], num, ci, out double py)
            && double.TryParse(f[11], num, ci, out double pz))
        { cell = c; x = px; y = py; z = pz; }
        link = new PortalLink(sns, sew, dns, dew, name, minLv, maxLv, mask, cell, x, y, z);
        return true;
    }
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

/// <summary>
/// How long a trip's parts take (11-19, wave 2): the planner's teleport costs and the trip's time
/// estimate. The planner still counts in units, running distance: a teleport costs its seconds at
/// RunSpeed, so a trip's cost divided by RunSpeed is how long it takes. Without one (null) the planner
/// keeps the flat costs it had (PortalPenaltyUnits, RecallCostUnits), as the older tests expect.
/// The defaults are from a trip in the logs (11-18): running 11.7 u/s; at a portal, 6.5 s from 14 u out
/// (switching to the portal step) to the jump: slowing, aiming, the use and its firing, about 5 s on top
/// of the walk; portal space 7.7 s from the jump to standing (a recall the same); a recall spell 1.7 s
/// with the magic stance up, so 5 s with a stance change and a wand. RynthNav measures its own as it
/// goes (TripClock).
/// </summary>
public sealed class TripTimes
{
    public double RunSpeed = 11.7;
    /// <summary>At a portal: slowing, aiming, the use and its firing, on top of the walk to it.</summary>
    public double PortalSeconds = 5.0;
    /// <summary>Portal space, any teleport: from the jump to standing on the other side.</summary>
    public double SpaceSeconds = 7.5;
    /// <summary>A recall spell: the stance, a wand, the cast (to the jump).</summary>
    public double SpellCastSeconds = 5.0;
    /// <summary>@lifestone, @house recall and the like: their long animation (to the jump).</summary>
    public double CommandCastSeconds = 14.0;

    public TripTimes Copy() => (TripTimes)MemberwiseClone();
    /// <summary>A portal's cost in units: its time at the portal and in portal space, at RunSpeed.</summary>
    public double PortalUnits => (PortalSeconds + SpaceSeconds) * RunSpeed;
    public double RecallUnits(bool spell) => ((spell ? SpellCastSeconds : CommandCastSeconds) + SpaceSeconds) * RunSpeed;
    public double Seconds(double units) => units / RunSpeed;

    /// <summary>"about 45 s", "about 4 min", "about 1 h 10 min" ("" for none).</summary>
    public static string About(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "";
        if (seconds < 60) return $"about {Math.Max(5, Math.Round(seconds / 5) * 5):F0} s";
        double min = Math.Round(seconds / 60);
        if (min < 60) return $"about {min:F0} min";
        return $"about {Math.Floor(min / 60):F0} h {min % 60:F0} min";
    }
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

    /// <summary>
    /// The teleport this step takes, as the planner's key for it (PortalRoute.PortalKey and the like;
    /// null for a walk): a refused portal is left out of the next plan by it, and "another way" leaves
    /// out the teleports of the way before.
    /// </summary>
    public string? Via { get; init; }

    /// <summary>
    /// The plan's cost (units) from the start to the end of this step, its teleport and portal space
    /// included: what is left of the trip after it is (the plan's Est minus this). 0 = not known.
    /// </summary>
    public double EstAfter { get; init; }
}

/// <summary>
/// One way to make a trip, from <see cref="PortalRoute.PlanOptions"/>: its legs, its cost, how many
/// teleports it takes, and what it leaves out that the cheaper ways took (Avoids: their names; Bans:
/// their keys, <see cref="RouteStep.Via"/>), so asking for it again from somewhere else on the way
/// gives the same kind of way ("not through the Olthoi Tunnel").
/// </summary>
public sealed class RouteOption
{
    public List<RouteStep> Steps = new();
    public double Est = double.PositiveInfinity;
    public int Teleports;
    public readonly List<string> Avoids = new();
    public readonly HashSet<string> Bans = new(StringComparer.Ordinal);
    /// <summary>Its teleports in order (their keys): two options with the same one are the same way.</summary>
    public string Signature = "";
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

    // ── Teleport keys (RouteStep.Via): the same teleport has the same key in every plan of a session ──
    /// <summary>The key of portal <paramref name="index"/> of the portal list passed to the planner.</summary>
    public static string PortalKey(int index) => "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>The key of a recall (by name: the list of recalls ready to cast changes from plan to plan).</summary>
    public static string RecallKey(string name) => "r:" + name;
    /// <summary>The key of a hub's entry portal (by name and place).</summary>
    public static string HubEntryKey(in HubEntry e) => "in:" + e.Name + "@" + e.SrcNs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                                                        + "," + e.SrcEw.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>The key of a hub's exit (HubExit.Id).</summary>
    public static string HubExitKey(int id) => "out" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>
    /// The key of walking straight from the start to the goal: banned, every way takes a teleport
    /// (a portal's entrance or a recall is the only way on from the start then).
    /// </summary>
    public const string WalkKey = "walk";

    // ── Real walk costs (11-19) ──
    // A walk is costed as a straight line unless the caller passes walkCost (fromNs, fromEw, toNs, toEw):
    // the walking distance on the route graph, NaN when it can't tell (the straight line stays), +infinity
    // when there is no walk. The straight line is never more than the walk, so the planner plans with it,
    // costs the plan's own walks for real, and plans again until the plan holds (lazy shortest path): the
    // few walks a trip takes are searched, not the thousands the planner weighs. 11-19, a Conquest
    // player's goto to the Paradox-touched Olthoi tents took Mountain Shortcut: its landing is 300-700 u
    // from them in a straight line, about 8,500 u on foot round the mountains; the recall about 1,000 u.
    /// <summary>At most this many walks are costed for real in one plan (each one a route graph search).</summary>
    public const int MaxRealWalks = 24;
    /// <summary>Walks shorter than this keep the straight line.</summary>
    public const double RealWalkMinUnits = 40.0;
    /// <summary>
    /// Walking all the way (PlanOptions' walk) is costed for real only when its straight line is under
    /// this many times the best way's cost; further, it can't come near and keeps the straight line (a
    /// lower bound). 11-20 live: a 39,000 u walk took 130-190 ms on the route graph, the plan's whole budget.
    /// </summary>
    public const double WalkAllRealFactor = 2.0;

    private static double Dist(double aNs, double aEw, double bNs, double bEw)
    {
        double dn = (aNs - bNs) * UnitsPerDegree, de = (aEw - bEw) * UnitsPerDegree;
        return Math.Sqrt(dn * dn + de * de);
    }

    private enum EdgeKind { Walk, Portal, Recall, HubIn, HubOut }
    private readonly struct Edge
    {
        public readonly int To; public readonly double Cost; public readonly EdgeKind Kind; public readonly string Label; public readonly int Index;
        public readonly string? Key;   // the teleport's key (RouteStep.Via); null for a walk
        public readonly int WalkId;    // a walk's cost slot in the graph (it can be costed for real); -1 for a teleport
        public Edge(int to, double cost, EdgeKind kind, string label, int index = -1, string? key = null, int walkId = -1)
        { To = to; Cost = cost; Kind = kind; Label = label; Index = index; Key = key; WalkId = walkId; }
    }

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
    /// <paramref name="bans"/>: teleports (their keys: PortalKey, RecallKey, HubEntryKey, HubExitKey)
    /// the plan may not take: a portal the server refuses this character, one it refused already.
    /// </summary>
    public static List<RouteStep> Plan(
        IReadOnlyList<PortalLink> portals,
        IReadOnlyList<RecallLink>? recalls,
        HubLinks? hub,
        double startNs, double startEw,
        double goalNs, double goalEw,
        out double estUnits, out int portalsUsed,
        Func<double, double, double, double, bool>? canWalk = null,
        bool startOnMap = true,
        ISet<string>? bans = null,
        Func<double, double, double, double, double>? walkCost = null,
        TripTimes? times = null)
    {
        var g = new Graph(portals, recalls, hub, startNs, startEw, goalNs, goalEw, canWalk, startOnMap, walkCost, times);
        return g.Search(bans, null, out estUnits, out portalsUsed);
    }

    /// <summary>
    /// Up to <paramref name="max"/> different ways to make the trip, cheapest first: the plan, then
    /// the best way without each of its teleports in turn, then without theirs (Yen's k shortest paths,
    /// with "different" meaning a different set of teleports), and walking all the way when that can
    /// be done. Each option says what it avoids. One graph is built and searched several times, so
    /// asking for four costs about the same as one plan. Empty when there is no way at all.
    /// </summary>
    public static List<RouteOption> PlanOptions(
        IReadOnlyList<PortalLink> portals,
        IReadOnlyList<RecallLink>? recalls,
        HubLinks? hub,
        double startNs, double startEw,
        double goalNs, double goalEw,
        int max,
        Func<double, double, double, double, bool>? canWalk = null,
        bool startOnMap = true,
        ISet<string>? bans = null,
        Func<double, double, double, double, double>? walkCost = null,
        TripTimes? times = null,
        Action? bestPlanned = null)
    {
        var g = new Graph(portals, recalls, hub, startNs, startEw, goalNs, goalEw, canWalk, startOnMap, walkCost, times);
        var result = new List<RouteOption>();
        RouteOption? best = g.Option(bans, null);
        // The best way is planned (its walks costed): the other ways' walks come after (RealWalk's budgets).
        bestPlanned?.Invoke();
        if (best == null) return result;
        result.Add(best);
        if (max <= 1) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal) { best.Signature };
        var candidates = new List<RouteOption>();
        // Walking all the way, when it can be done and the plan teleports.
        if (best.Teleports > 0 && g.CanWalkStraight)
        {
            var walk = new RouteOption { Est = g.WalkAllTheWay(best.Est), Signature = "" };
            walk.Steps.Add(new RouteStep(goalNs, goalEw, false, false, "walk"));
            walk.Avoids.Add("every portal and recall");
            candidates.Add(walk);
            seen.Add("");
        }
        RouteOption last = best;
        while (result.Count < max)
        {
            // The next ways: the last one found, without one of its teleports. A walk has none: the
            // way after it is the best that teleports (walking straight there left out).
            IEnumerable<string> keys = last.Teleports == 0 ? new[] { WalkKey } : Teleports(last);
            foreach (string key in keys)
            {
                if (last.Bans.Contains(key)) continue;
                var extra = new HashSet<string>(last.Bans, StringComparer.Ordinal) { key };
                RouteOption? o = g.Option(bans, extra);
                if (o == null || !seen.Add(o.Signature)) continue;
                candidates.Add(o);
            }
            if (candidates.Count == 0) break;
            int pick = 0;
            for (int i = 1; i < candidates.Count; i++) if (candidates[i].Est < candidates[pick].Est) pick = i;
            last = candidates[pick];
            candidates.RemoveAt(pick);
            result.Add(last);
        }
        return result;
    }

    private static IEnumerable<string> Teleports(RouteOption o)
    {
        foreach (RouteStep s in o.Steps) if (s.Via != null) yield return s.Via;
    }

    /// <summary>The planner's graph for one start and goal: built once, searched with different bans.</summary>
    private sealed class Graph
    {
        private readonly double[] _ns, _ew;
        private readonly List<Edge>[] _adj;
        private readonly double _startNs, _startEw;
        private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
        public readonly bool CanWalkStraight;
        // The walks' costs (Edge.WalkId): the straight line until costed for real (MakeReal).
        private readonly Func<double, double, double, double, double>? _walkCost;
        private readonly List<double> _wCost = new();
        private readonly List<int> _wFrom = new(), _wTo = new();
        private readonly List<bool> _wReal = new();
        private int _realLeft = MaxRealWalks;
        private readonly int _walkAllId = -1;

        public Graph(IReadOnlyList<PortalLink> portals, IReadOnlyList<RecallLink>? recalls, HubLinks? hub,
            double startNs, double startEw, double goalNs, double goalEw,
            Func<double, double, double, double, bool>? canWalk, bool startOnMap,
            Func<double, double, double, double, double>? walkCost = null, TripTimes? times = null)
        {
            _walkCost = walkCost;
            // A portal's cost: its real time with TripTimes, else the flat penalty (the Town Network's too).
            double portalUnits = times?.PortalUnits ?? PortalPenaltyUnits;
            recalls ??= Array.Empty<RecallLink>();
            _startNs = startNs; _startEw = startEw;
            int hubEntries = hub?.Entries.Count ?? 0, hubArrivals = hub?.Arrivals ?? 0, hubExits = hub?.Exits.Count ?? 0;

            // Node layout: 0=START, 1=GOAL, then per-portal [Src,Dst] pairs, then recall dsts, then the
            // hub's entries, arrivals (inside: no map position) and exits' landings.
            int recallBase = 2 + portals.Count * 2;
            int entryBase = recallBase + recalls.Count, arrivalBase = entryBase + hubEntries, exitBase = arrivalBase + hubArrivals;
            int n = exitBase + hubExits;
            var ns = _ns = new double[n];
            var ew = _ew = new double[n];
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

            var adj = _adj = new List<Edge>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<Edge>();
            bool AddWalk(int from, int to, double cost, string? key = null)
            {
                if (from == 0 && !startOnMap) return false;   // in a dungeon: no walking from where you stand
                if (canWalk != null && !canWalk(ns[from], ew[from], ns[to], ew[to])) return false;
                int id = _wCost.Count;
                _wCost.Add(cost); _wFrom.Add(from); _wTo.Add(to); _wReal.Add(false);
                adj[from].Add(new Edge(to, cost, EdgeKind.Walk, "walk", -1, key, id));
                return true;
            }

            // Direct walk fallback START -> GOAL (WalkKey: "another way" than walking bans it).
            CanWalkStraight = AddWalk(0, 1, Dist(startNs, startEw, goalNs, goalEw), WalkKey);
            if (CanWalkStraight) _walkAllId = _wCost.Count - 1;
            _labels[WalkKey] = "walking all the way";

            for (int i = 0; i < portals.Count; i++)
            {
                int s = 2 + i * 2, d = s + 1;
                string name = portals[i].Name;
                string key = PortalKey(i);
                _labels[key] = name;
                // The teleport itself.
                adj[s].Add(new Edge(d, portalUnits, EdgeKind.Portal, name, -1, key));
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
                string key = RecallKey(recalls[i].Name);
                _labels[key] = recalls[i].Name;
                adj[0].Add(new Edge(r, recalls[i].CostUnits, EdgeKind.Recall, recalls[i].Name, i, key));
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
                    {
                        string key = HubEntryKey(he);
                        _labels[key] = he.Name;
                        adj[en].Add(new Edge(arrivalBase + he.Arrival, portalUnits, EdgeKind.HubIn, he.Name, -1, key));
                    }
                }
                for (int x = 0; x < hubExits; x++)
                {
                    int xn = exitBase + x;
                    HubExit hx = hub.Exits[x];
                    string key = HubExitKey(hx.Id);
                    _labels[key] = hx.Name;
                    for (int a = 0; a < hubArrivals && a < hx.WalkUnits.Length; a++)
                    {
                        double walk = hx.WalkUnits[a];
                        if (double.IsNaN(walk) || walk < 0) continue;
                        adj[arrivalBase + a].Add(new Edge(xn, walk + portalUnits, EdgeKind.HubOut, hx.Name, hx.Id, key));
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
        }

        /// <summary>The best way with these left out, as an option (null: no way at all).</summary>
        public RouteOption? Option(ISet<string>? bans, ISet<string>? extra)
        {
            List<RouteStep> steps = Search(bans, extra, out double est, out int used);
            if (double.IsPositiveInfinity(est)) return null;
            var o = new RouteOption { Steps = steps, Est = est, Teleports = used };
            var sig = new System.Text.StringBuilder();
            foreach (RouteStep s in steps)
                if (s.Via != null) { if (sig.Length > 0) sig.Append('|'); sig.Append(s.Via); }
            o.Signature = sig.ToString();
            if (extra != null)
                foreach (string k in extra)
                {
                    o.Bans.Add(k);
                    string label = _labels.TryGetValue(k, out string? l) ? l : k;
                    if (!o.Avoids.Contains(label)) o.Avoids.Add(label);
                }
            return o;
        }

        /// <summary>
        /// The best way, never taking a banned teleport. With real walk costs: plan, cost the plan's walks
        /// for real, and plan again while that made it dearer (a walk's real cost is never under its
        /// straight line, so a plan whose walks are all costed is the best there is). Each walk is costed
        /// once per graph, so the other ways (PlanOptions) reuse what the first plan found.
        /// </summary>
        public List<RouteStep> Search(ISet<string>? bans, ISet<string>? extra, out double estUnits, out int portalsUsed)
        {
            var walks = new List<int>();
            while (true)
            {
                walks.Clear();
                List<RouteStep> steps = SearchOnce(bans, extra, out estUnits, out portalsUsed, walks);
                if (_walkCost == null || walks.Count == 0) return steps;
                bool dearer = false;
                foreach (int id in walks) dearer |= MakeReal(id);
                if (!dearer) return steps;
            }
        }

        /// <summary>
        /// Walking all the way: its cost (+infinity when it can't be walked), costed for real when its straight
        /// line is under <see cref="WalkAllRealFactor"/> times <paramref name="bestEst"/> (the best way's cost).
        /// </summary>
        public double WalkAllTheWay(double bestEst = double.PositiveInfinity)
        {
            if (_walkAllId < 0) return double.PositiveInfinity;
            if (!(_wCost[_walkAllId] >= WalkAllRealFactor * bestEst)) MakeReal(_walkAllId);
            return _wCost[_walkAllId];
        }

        /// <summary>Costs walk <paramref name="id"/> for real, once (within MaxRealWalks); true when that made it dearer.</summary>
        private bool MakeReal(int id)
        {
            if (_walkCost == null || _wReal[id] || _realLeft <= 0) return false;
            _wReal[id] = true;
            double straight = _wCost[id];
            if (straight < RealWalkMinUnits) return false;
            int a = _wFrom[id], b = _wTo[id];
            if (double.IsNaN(_ns[a]) || double.IsNaN(_ns[b])) return false;
            _realLeft--;
            double real = _walkCost(_ns[a], _ew[a], _ns[b], _ew[b]);
            if (double.IsNaN(real) || real <= straight + 1) return false;   // can't tell, or no dearer: the straight line stays
            _wCost[id] = real;
            return true;
        }

        /// <summary>Dijkstra (uniform-cost; optimal with positive edge costs), never taking a banned teleport.
        /// <paramref name="walks"/> gets the plan's walks not yet costed for real.</summary>
        private List<RouteStep> SearchOnce(ISet<string>? bans, ISet<string>? extra, out double estUnits, out int portalsUsed, List<int> walks)
        {
            int n = _ns.Length;
            var dist = new double[n];
            var prev = new int[n];
            var prevKind = new EdgeKind[n];
            var prevLabel = new string[n];
            var prevIndex = new int[n];
            var prevKey = new string?[n];
            var prevWalk = new int[n];
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
                foreach (var e in _adj[u])
                {
                    if (done[e.To]) continue;
                    if (e.Key != null && ((bans != null && bans.Contains(e.Key)) || (extra != null && extra.Contains(e.Key)))) continue;
                    double nd = cu + (e.WalkId >= 0 ? _wCost[e.WalkId] : e.Cost);
                    if (nd < dist[e.To])
                    {
                        if (!double.IsPositiveInfinity(dist[e.To])) pq.Remove((dist[e.To], e.To));
                        dist[e.To] = nd; prev[e.To] = u; prevKind[e.To] = e.Kind; prevLabel[e.To] = e.Label; prevIndex[e.To] = e.Index; prevKey[e.To] = e.Key;
                        prevWalk[e.To] = e.WalkId;
                        pq.Add((nd, e.To));
                    }
                }
            }

            estUnits = dist[1];
            portalsUsed = 0;
            var steps = new List<RouteStep>();
            if (double.IsPositiveInfinity(dist[1])) return steps; // unreachable (an island, or a dungeon start with no recall)

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
                if (kind == EdgeKind.Walk && prevWalk[to] >= 0 && !_wReal[prevWalk[to]]) walks.Add(prevWalk[to]);
                if (kind == EdgeKind.Walk)
                {
                    steps.Add(new RouteStep(_ns[to], _ew[to], false, false, prevLabel[to]) { EstAfter = dist[to] });
                }
                else if (kind == EdgeKind.HubIn)
                {
                    portalsUsed++;
                    // Into the hub: the previous walk leg ends at the entry portal; take it. Where it
                    // lands (inside) has no map position.
                    if (steps.Count > 0)
                        steps[steps.Count - 1] = new RouteStep(steps[^1].Ns, steps[^1].Ew, true, false, prevLabel[to]) { Via = prevKey[to], EstAfter = dist[to] };
                }
                else if (kind == EdgeKind.HubOut)
                {
                    portalsUsed++;
                    steps.Add(RouteStep.Hub(_ns[to], _ew[to], prevLabel[to], prevIndex[to]) with { Via = prevKey[to], EstAfter = dist[to] });
                }
                else if (kind == EdgeKind.Portal)
                {
                    portalsUsed++;
                    // 'to' is a portal Dst reached by teleport; the entrance is the prior node,
                    // which the previous walk leg already targets. Flag that leg as a portal use.
                    if (steps.Count > 0)
                        steps[steps.Count - 1] = new RouteStep(steps[^1].Ns, steps[^1].Ew, true, false, prevLabel[to], -1, _ns[to], _ew[to]) { Via = prevKey[to], EstAfter = dist[to] };
                }
                else // Recall
                {
                    portalsUsed++;
                    // Recall is cast from START (current position); insert a recall step at START.
                    steps.Add(new RouteStep(_startNs, _startEw, false, true, prevLabel[to], prevIndex[to], _ns[to], _ew[to]) { Via = prevKey[to], EstAfter = dist[to] });
                }
            }
            return steps;
        }
    }
}
