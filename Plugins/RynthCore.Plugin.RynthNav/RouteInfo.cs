using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// What a saved nav route is (/rnav routeinfo, the "RynthNav.Routes" info op): its type, its points
/// by kind, how far it walks, the box it covers on the map and its centre, where it starts and how
/// far that is from the player, the landblocks it crosses, whether it is a dungeon route, and the
/// portals, recalls and commands a trip uses. Read with RynthAi's own reader (NavRouteParser).
///
///   yards      the walking stretches: from each point to the next (a circular route also from its last
///              point back to its first), except after a Portal or Recall step (a teleport) and across a
///              jump longer than <see cref="TeleportGapUnits"/> (a teleport the file doesn't say, a
///              command recall)
///   landblocks every landblock a point is in, and those a walking stretch crosses (sampled every 24 yd)
///   dungeon    more than half the walked-to points are in dungeon landblocks (the caller says which
///              landblocks are dungeons: the Atlas's, and those with a dungeon navmesh and no places on
///              the map); coordinates are all a nav file has, so a cellar under the landscape reads as
///              the landscape
/// Host-free: the route tests describe made-up and real files.
/// </summary>
internal sealed class RouteInfo
{
    /// <summary>Consecutive points farther apart than this (yards) are a teleport, not a walk.</summary>
    public const double TeleportGapUnits = 600.0;
    /// <summary>A walking stretch is sampled this often for the landblocks it crosses.</summary>
    private const double SampleUnits = 24.0;

    public string Name = "", File = "", Type = "", Warning = "", FollowTarget = "";
    public int Points, Waypoints, Portals, Recalls, Chats, Pauses, Others;
    public double Yards;
    public bool HasBox;
    public double MinNs, MaxNs, MinEw, MaxEw;
    public double CentreNs => (MinNs + MaxNs) / 2;
    public double CentreEw => (MinEw + MaxEw) / 2;
    public bool HasStart;
    public double StartNs, StartEw;
    /// <summary>From the player to the start, yards; NaN when the player isn't on the map.</summary>
    public double StartYards = double.NaN;
    /// <summary>The walked-to point nearest the player (RynthAi starts a circular or linear route there), -1 = none.</summary>
    public int NearestIndex = -1;
    public double NearestNs, NearestEw, NearestYards = double.NaN;
    public readonly List<uint> Landblocks = new();
    public readonly List<uint> DungeonLandblocks = new();
    public bool Dungeon;
    public readonly List<string> PortalNames = new(), RecallNames = new(), ChatCommands = new();

    public static string TypeName(NavRouteType t) => t switch
    {
        NavRouteType.Circular => "circular",
        NavRouteType.Linear => "linear",
        NavRouteType.Follow => "follow",
        NavRouteType.Once => "once",
        _ => "unknown",
    };

    /// <summary>A step after which the character is somewhere else (no walk to the next point).</summary>
    private static bool Teleports(NavPointType t) => t is NavPointType.PortalNPC or NavPointType.Recall;

    private static uint LandblockOf(double ns, double ew)
    {
        double x = NavCoords.WorldX(ew), y = NavCoords.WorldY(ns);
        int lx = (int)Math.Floor(x / 192.0), ly = (int)Math.Floor(y / 192.0);
        return lx is >= 0 and <= 255 && ly is >= 0 and <= 255 ? (uint)((lx << 8) | ly) : uint.MaxValue;
    }

    /// <summary>
    /// Describes <paramref name="route"/>. <paramref name="isDungeon"/> says whether a landblock is a
    /// dungeon (null: none is); the player's map position when <paramref name="hasPlayer"/>;
    /// <paramref name="spellName"/> names a recall's spell (null: "spell N").
    /// </summary>
    public static RouteInfo Describe(NavRouteParser route, string name, string file, Func<uint, bool>? isDungeon,
        bool hasPlayer, double playerNs, double playerEw, Func<int, string?>? spellName)
    {
        var info = new RouteInfo { Name = name, File = file, Type = TypeName(route.RouteType), Warning = route.LoadWarning ?? "" };
        if (route.RouteType == NavRouteType.Follow && route.Points.Count == 0) info.FollowTarget = route.FollowTargetName;
        List<NavPoint> pts = route.Points;
        info.Points = pts.Count;
        var lbs = new SortedSet<uint>();
        int walkedTo = 0, inDungeon = 0;
        var dungeonCache = new Dictionary<uint, bool>();
        bool IsDun(uint lb)
        {
            if (isDungeon == null || lb == uint.MaxValue) return false;
            if (!dungeonCache.TryGetValue(lb, out bool d)) dungeonCache[lb] = d = isDungeon(lb);
            return d;
        }
        void AddLb(double ns, double ew) { uint lb = LandblockOf(ns, ew); if (lb != uint.MaxValue) lbs.Add(lb); }

        for (int i = 0; i < pts.Count; i++)
        {
            NavPoint p = pts[i];
            switch (p.Type)
            {
                case NavPointType.Point: case NavPointType.Checkpoint: case NavPointType.Jump: info.Waypoints++; break;
                case NavPointType.PortalNPC:
                    info.Portals++;
                    if (p.TargetName.Length > 0 && !info.PortalNames.Contains(p.TargetName)) info.PortalNames.Add(p.TargetName);
                    break;
                case NavPointType.Recall:
                    info.Recalls++;
                    string rn = spellName?.Invoke(p.SpellId) ?? "";
                    rn = rn.Length > 0 ? rn : "spell " + p.SpellId.ToString(CultureInfo.InvariantCulture);
                    if (!info.RecallNames.Contains(rn)) info.RecallNames.Add(rn);
                    break;
                case NavPointType.Chat:
                    info.Chats++;
                    if (p.ChatCommand.Length > 0 && !info.ChatCommands.Contains(p.ChatCommand)) info.ChatCommands.Add(p.ChatCommand);
                    break;
                case NavPointType.Pause: info.Pauses++; break;
                default: info.Others++; break;
            }
            if (!double.IsFinite(p.NS) || !double.IsFinite(p.EW)) continue;
            if (!info.HasBox) { info.HasBox = true; info.MinNs = info.MaxNs = p.NS; info.MinEw = info.MaxEw = p.EW; }
            info.MinNs = Math.Min(info.MinNs, p.NS); info.MaxNs = Math.Max(info.MaxNs, p.NS);
            info.MinEw = Math.Min(info.MinEw, p.EW); info.MaxEw = Math.Max(info.MaxEw, p.EW);
            if (!info.HasStart) { info.HasStart = true; info.StartNs = p.NS; info.StartEw = p.EW; }
            AddLb(p.NS, p.EW);
            if (NavRouteParser.IsPlainWaypoint(p.Type))
            {
                walkedTo++;
                uint lb = LandblockOf(p.NS, p.EW);
                if (IsDun(lb)) { inDungeon++; if (!info.DungeonLandblocks.Contains(lb)) info.DungeonLandblocks.Add(lb); }
                if (hasPlayer)
                {
                    double d = NavCoords.Distance(playerNs, playerEw, p.NS, p.EW);
                    if (info.NearestIndex < 0 || d < info.NearestYards) { info.NearestIndex = i; info.NearestNs = p.NS; info.NearestEw = p.EW; info.NearestYards = d; }
                }
            }
        }
        foreach (var (a, b) in WalkingStretches(route))
        {
            double d = NavCoords.Distance(a.NS, a.EW, b.NS, b.EW);
            info.Yards += d;
            int n = (int)Math.Ceiling(d / SampleUnits);
            for (int k = 1; k < n; k++) AddLb(a.NS + (b.NS - a.NS) * k / n, a.EW + (b.EW - a.EW) * k / n);
        }
        info.Landblocks.AddRange(lbs);
        info.DungeonLandblocks.Sort();
        info.Dungeon = walkedTo > 0 && inDungeon * 2 > walkedTo;
        if (hasPlayer && info.HasStart && !info.Dungeon) info.StartYards = NavCoords.Distance(playerNs, playerEw, info.StartNs, info.StartEw);
        if (info.Dungeon) { info.NearestIndex = -1; info.NearestYards = double.NaN; }   // dungeon coordinates aren't the player's map position
        return info;
    }

    /// <summary>The walking length of a route in yards (see the class notes).</summary>
    public static double Length(NavRouteParser route)
    {
        double total = 0;
        foreach (var (a, b) in WalkingStretches(route)) total += NavCoords.Distance(a.NS, a.EW, b.NS, b.EW);
        return total;
    }

    /// <summary>Each pair of points walked between, in order (a circular route's closing pair last).</summary>
    private static IEnumerable<(NavPoint A, NavPoint B)> WalkingStretches(NavRouteParser route)
    {
        List<NavPoint> pts = route.Points;
        int n = pts.Count;
        int pairs = route.RouteType == NavRouteType.Circular && n > 1 ? n : n - 1;
        for (int i = 0; i < pairs; i++)
        {
            NavPoint a = pts[i], b = pts[(i + 1) % n];
            if (Teleports(a.Type)) continue;
            if (!double.IsFinite(a.NS) || !double.IsFinite(a.EW) || !double.IsFinite(b.NS) || !double.IsFinite(b.EW)) continue;
            if (NavCoords.Distance(a.NS, a.EW, b.NS, b.EW) > TeleportGapUnits) continue;
            yield return (a, b);
        }
    }

    // ── Out ──────────────────────────────────────────────────────────────

    /// <summary>The info op's result (Shared/RynthNavRoutesApi.cs has the fields).</summary>
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("name", Name);
        w.WriteString("file", File);
        w.WriteString("type", Type);
        if (FollowTarget.Length > 0) w.WriteString("follows", FollowTarget);
        w.WriteNumber("points", Points);
        w.WriteStartObject("counts");
        w.WriteNumber("waypoints", Waypoints); w.WriteNumber("portals", Portals); w.WriteNumber("recalls", Recalls);
        w.WriteNumber("chats", Chats); w.WriteNumber("pauses", Pauses); w.WriteNumber("other", Others);
        w.WriteEndObject();
        RouteJson.Num(w, "yards", Yards, 0);
        if (HasBox)
        {
            w.WriteStartObject("box");
            RouteJson.Num(w, "minNs", MinNs, 4); RouteJson.Num(w, "maxNs", MaxNs, 4);
            RouteJson.Num(w, "minEw", MinEw, 4); RouteJson.Num(w, "maxEw", MaxEw, 4);
            w.WriteEndObject();
            RouteJson.Place(w, "centre", CentreNs, CentreEw);
        }
        else { w.WriteNull("box"); w.WriteNull("centre"); }
        if (HasStart)
        {
            w.WriteStartObject("start");
            RouteJson.Num(w, "ns", StartNs, 4); RouteJson.Num(w, "ew", StartEw, 4); RouteJson.Num(w, "yards", StartYards, 0);
            w.WriteEndObject();
        }
        else w.WriteNull("start");
        if (NearestIndex >= 0)
        {
            w.WriteStartObject("nearest");
            w.WriteNumber("index", NearestIndex);
            RouteJson.Num(w, "ns", NearestNs, 4); RouteJson.Num(w, "ew", NearestEw, 4); RouteJson.Num(w, "yards", NearestYards, 0);
            w.WriteEndObject();
        }
        else w.WriteNull("nearest");
        w.WriteStartArray("landblocks");
        foreach (uint lb in Landblocks) w.WriteStringValue(lb.ToString("X4", CultureInfo.InvariantCulture));
        w.WriteEndArray();
        w.WriteBoolean("dungeon", Dungeon);
        w.WriteStartArray("dungeonLandblocks");
        foreach (uint lb in DungeonLandblocks) w.WriteStringValue(lb.ToString("X4", CultureInfo.InvariantCulture));
        w.WriteEndArray();
        RouteJson.Strings(w, "portals", PortalNames);
        RouteJson.Strings(w, "recalls", RecallNames);
        RouteJson.Strings(w, "chats", ChatCommands);
        if (Warning.Length > 0) w.WriteString("warning", Warning);
        w.WriteEndObject();
    }

    /// <summary>/rnav routeinfo's lines.</summary>
    public List<string> ChatLines()
    {
        var lines = new List<string>();
        string kinds = $"{Waypoints} waypoint(s)"
            + (Portals > 0 ? $", {Portals} portal(s)" : "") + (Recalls > 0 ? $", {Recalls} recall(s)" : "")
            + (Chats > 0 ? $", {Chats} command(s)" : "") + (Pauses > 0 ? $", {Pauses} pause(s)" : "") + (Others > 0 ? $", {Others} other" : "");
        lines.Add(FollowTarget.Length > 0
            ? $"routeinfo {Name}: follow, follows {FollowTarget}"
            : $"routeinfo {Name}: {Type}, {Points} point(s) ({kinds}), {NavCoords.FmtDistance(Yards)} of walking{(Type == "circular" ? " a lap" : "")}");
        if (HasBox)
            lines.Add($"  covers {NavCoords.FmtOne(MinNs, 'N', 'S')} to {NavCoords.FmtOne(MaxNs, 'N', 'S')}, {NavCoords.FmtOne(MinEw, 'E', 'W')} to {NavCoords.FmtOne(MaxEw, 'E', 'W')}; centre {NavCoords.Fmt(CentreNs, CentreEw)}");
        if (HasStart)
            lines.Add($"  starts at {NavCoords.Fmt(StartNs, StartEw)}"
                + (double.IsNaN(StartYards) ? "" : $", {NavCoords.FmtDistance(StartYards)} from you")
                + (NearestIndex > 0 && !double.IsNaN(NearestYards) && (Type is "circular" or "linear") ? $"; nearest waypoint #{NearestIndex + 1}, {NavCoords.FmtDistance(NearestYards)} away" : ""));
        if (Landblocks.Count > 0)
        {
            var hex = new List<string>();
            for (int i = 0; i < Landblocks.Count && i < 12; i++) hex.Add(Landblocks[i].ToString("X4", CultureInfo.InvariantCulture));
            lines.Add($"  {Landblocks.Count} landblock(s): {string.Join(" ", hex)}{(Landblocks.Count > 12 ? $" (+{Landblocks.Count - 12} more)" : "")}");
        }
        if (Dungeon) lines.Add($"  a dungeon route ({string.Join(" ", DungeonLandblocks.ConvertAll(l => l.ToString("X4", CultureInfo.InvariantCulture)))})");
        else if (DungeonLandblocks.Count > 0) lines.Add($"  partly in a dungeon ({string.Join(" ", DungeonLandblocks.ConvertAll(l => l.ToString("X4", CultureInfo.InvariantCulture)))})");
        if (PortalNames.Count > 0) lines.Add($"  portals: {string.Join(", ", PortalNames)}");
        if (RecallNames.Count > 0) lines.Add($"  recalls: {string.Join(", ", RecallNames)}");
        if (ChatCommands.Count > 0) lines.Add($"  commands: {string.Join(", ", ChatCommands)}");
        if (Warning.Length > 0) lines.Add("  " + Warning);
        return lines;
    }
}
