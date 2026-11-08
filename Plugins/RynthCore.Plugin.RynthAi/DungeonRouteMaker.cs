// DungeonRouteMaker: dungeon nav routes made on demand (2026-10-30), for players (/ra dunroute,
// /ra dunpatrol-save) and for other plugins (the "RynthAi.Routes" interface, Shared/RynthAiRoutesApi.cs,
// whose header has the JSON). One implementation: RynthAiPlugin.DunRoutes.cs gathers where the
// character is (DunRouteWorld) and runs the request through here; the chat command and the
// interface only word the answer differently.
//
// A route is laid with DungeonPathfinder's own paths: A* over the cell graph (no drops, no lava/acid
// cells), then the points every dungeon path gets (BuildPathPoints: centred on narrow openings,
// straight across rooms, off the walls; without the dungeon map, cell-centre midpoints). The
// character's spot is the route's first point, so a saved route says where it starts. Portals the
// route isn't going to are kept out of (DungeonPathfinder.Portals.cs); a route to a portal stops at
// its edge and ends with a Portal step that uses it. Saved as a Once route, VTank's format, in
// NavProfiles, where RynthAi's Nav picker lists it and /ra nav load runs it.
//
// The doorway flag (NavPoint.Doorway: a tight reach, no corner cut) isn't in the .nav format, which
// stays VTank's: the flagged points are listed in NavProfiles\.rynthai-routes.json (DunRouteSidecar),
// and RynthAi puts the flags back when the unchanged file is loaded in its dungeon.
//
// Host-free: everything comes in through DunRouteWorld, so the host tests build worlds from fixtures.
// Pump thread only (DungeonPathfinder's caches).

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>What a dungeon route goes to.</summary>
internal enum DunTargetKind { Exit, Portal, Position, Room, Far }

/// <summary>A route's target: the exit, a portal by name, a spot (landblock-local metres), a room, the far end.</summary>
internal sealed record DunTarget(DunTargetKind Kind, string Portal = "", double X = 0, double Y = 0, double Z = 0, int Room = -1)
{
    public static readonly DunTarget Exit = new(DunTargetKind.Exit);
    public static readonly DunTarget Far = new(DunTargetKind.Far);

    public static string KindName(DunTargetKind k) => k switch
    {
        DunTargetKind.Exit     => "exit",
        DunTargetKind.Portal   => "portal",
        DunTargetKind.Position => "position",
        DunTargetKind.Room     => "room",
        _                      => "far",
    };

    /// <summary>The target as the command takes it ("portal Surface Portal", "110.5 -3.2 -24").</summary>
    public string Words() => Kind switch
    {
        DunTargetKind.Portal   => "portal " + Portal,
        DunTargetKind.Position => string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} {2:0.##}", X, Y, Z),
        DunTargetKind.Room     => "room " + Room.ToString(CultureInfo.InvariantCulture),
        _                      => KindName(Kind),
    };

    /// <summary>
    /// The words after "to" in /ra dunroute: exit, far, portal &lt;name&gt;, &lt;x&gt; &lt;y&gt; &lt;z&gt;
    /// (landblock-local metres; brackets and commas round them are fine), room &lt;n&gt;.
    /// </summary>
    public static bool TryParseWords(IReadOnlyList<string> words, out DunTarget target, out string error)
    {
        target = Exit;
        error = "";
        if (words.Count == 0) { error = "no target after \"to\""; return false; }
        string first = words[0].ToLowerInvariant();
        if (words.Count == 1 && first == "exit") { target = Exit; return true; }
        if (words.Count == 1 && first == "far") { target = Far; return true; }
        if (first == "portal")
        {
            string name = string.Join(" ", words.Skip(1)).Trim().Trim('"', '\'').Trim();
            if (name.Length == 0) { error = "\"portal\" needs the portal's name (/ra dunroute where lists them)"; return false; }
            if (name.Length > DunRouteRequest.MaxPortalName) { error = $"a portal name is at most {DunRouteRequest.MaxPortalName} characters"; return false; }
            target = new DunTarget(DunTargetKind.Portal, Portal: name);
            return true;
        }
        if (first == "room")
        {
            if (words.Count != 2 || !int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
            { error = "\"room\" needs a room number, 0 or more (/ra dunroute where shows yours)"; return false; }
            target = new DunTarget(DunTargetKind.Room, Room: n);
            return true;
        }
        // Coordinates: three numbers, as /ra navdebug's local=(x,y,z) or "[110.5 -3.2 -24]".
        var nums = new List<double>();
        foreach (string w in words)
            foreach (string piece in w.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string s = piece.Trim('[', ']', '(', ')', ' ');
                if (s.Length == 0) continue;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                { error = $"'{string.Join(" ", words)}' isn't a target: exit, far, portal <name>, <x> <y> <z> or room <n>"; return false; }
                nums.Add(v);
            }
        if (nums.Count != 3) { error = $"a spot is three numbers, x y z (landblock-local metres); got {nums.Count}"; return false; }
        if (nums.Any(v => Math.Abs(v) > DunRouteRequest.MaxCoordinate)) { error = $"x, y and z are landblock-local metres (within {DunRouteRequest.MaxCoordinate:0})"; return false; }
        target = new DunTarget(DunTargetKind.Position, X: nums[0], Y: nums[1], Z: nums[2]);
        return true;
    }
}

/// <summary>The four requests: a route to somewhere, the patrol, a saved route described, where the character is.</summary>
internal enum DunRouteOp { DunRoute, DunPatrol, Info, Where }

/// <summary>One request, from the chat command or the interface's JSON (checked: <see cref="TryParseJson"/>).</summary>
internal sealed class DunRouteRequest
{
    public const int MaxPortalName = 120;
    public const double MaxCoordinate = 100_000;

    public DunRouteOp Op;
    /// <summary>The route's file name in NavProfiles, without ".nav" (checked by <see cref="TryName"/>).</summary>
    public string Name = "";
    public DunTarget? To;
    public bool Overwrite;

    public static string OpName(DunRouteOp op) => op switch
    {
        DunRouteOp.DunRoute  => "dunroute",
        DunRouteOp.DunPatrol => "dunpatrol",
        DunRouteOp.Info      => "info",
        _                    => "where",
    };

    /// <summary>The name rules of every nav RynthAi saves (NewProfileFiles): tidied, then checked.</summary>
    public static bool TryName(string? raw, out string name, out string error)
    {
        name = NewProfileFiles.Tidy(raw, NewProfileFiles.NavExtensions, out _);
        error = NewProfileFiles.Validate(name) ?? "";
        if (error.Length > 0) { error = $"'{name}' can't be a route name: {error}"; return false; }
        return true;
    }

    private static readonly Dictionary<DunRouteOp, string[]> Fields = new()
    {
        [DunRouteOp.DunRoute]  = new[] { "op", "name", "to", "overwrite" },
        [DunRouteOp.DunPatrol] = new[] { "op", "name", "overwrite" },
        [DunRouteOp.Info]      = new[] { "op", "name" },
        [DunRouteOp.Where]     = new[] { "op" },
    };

    /// <summary>
    /// The interface's request (RynthAiRoutesApi.cs): one JSON object, its fields checked by op. A wrong
    /// type, an unknown or repeated field, a bad name or target: false with the reason.
    /// </summary>
    public static bool TryParseJson(string? json, out DunRouteRequest request, out string error)
    {
        request = new DunRouteRequest();
        error = "";
        if (string.IsNullOrWhiteSpace(json)) { error = "no request: send a JSON object, e.g. {\"op\":\"where\"}"; return false; }
        if (Encoding.UTF8.GetByteCount(json) > RynthAiRoutesApiV1.MaxRequestBytes) { error = $"the request is longer than {RynthAiRoutesApiV1.MaxRequestBytes} bytes"; return false; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (Exception ex) { error = "not JSON: " + ex.Message; return false; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "the request must be a JSON object, e.g. {\"op\":\"where\"}"; return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
                if (!seen.Add(p.Name)) { error = $"\"{p.Name}\" is given twice"; return false; }

            if (!root.TryGetProperty("op", out var opEl)) { error = "\"op\" is missing: dunroute, dunpatrol, info or where"; return false; }
            if (opEl.ValueKind != JsonValueKind.String) { error = "\"op\" must be a string: dunroute, dunpatrol, info or where"; return false; }
            string opText = opEl.GetString() ?? "";
            DunRouteOp? op = opText switch
            {
                "dunroute" => DunRouteOp.DunRoute,
                "dunpatrol" => DunRouteOp.DunPatrol,
                "info" => DunRouteOp.Info,
                "where" => DunRouteOp.Where,
                _ => null,
            };
            if (op is not DunRouteOp o) { error = $"\"op\" must be dunroute, dunpatrol, info or where (got \"{Clip(opText)}\")"; return false; }
            request.Op = o;

            string[] allowed = Fields[o];
            foreach (var p in root.EnumerateObject())
                if (Array.IndexOf(allowed, p.Name) < 0)
                { error = $"\"{Clip(p.Name)}\" isn't a field of {opText} (it takes {string.Join(", ", allowed)})"; return false; }

            if (o != DunRouteOp.Where)
            {
                if (!root.TryGetProperty("name", out var nameEl)) { error = $"\"name\" is missing: {opText} needs the route's name"; return false; }
                if (nameEl.ValueKind != JsonValueKind.String) { error = "\"name\" must be a string"; return false; }
                if (!TryName(nameEl.GetString(), out request.Name, out error)) return false;
            }
            if (root.TryGetProperty("overwrite", out var owEl))
            {
                if (owEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = "\"overwrite\" must be true or false"; return false; }
                request.Overwrite = owEl.GetBoolean();
            }
            if (o == DunRouteOp.DunRoute)
            {
                if (!root.TryGetProperty("to", out var toEl)) { error = "\"to\" is missing: \"exit\", \"far\", {\"portal\":name}, {\"x\":..,\"y\":..,\"z\":..} or {\"room\":n}"; return false; }
                if (!TryParseTarget(toEl, out var to, out error)) return false;
                request.To = to;
            }
        }
        return true;
    }

    private static bool TryParseTarget(JsonElement el, out DunTarget target, out string error)
    {
        target = DunTarget.Exit;
        error = "";
        const string forms = "\"exit\", \"far\", {\"portal\":name}, {\"x\":..,\"y\":..,\"z\":..} or {\"room\":n}";
        if (el.ValueKind == JsonValueKind.String)
        {
            string s = (el.GetString() ?? "").Trim().ToLowerInvariant();
            if (s == "exit") { target = DunTarget.Exit; return true; }
            if (s == "far") { target = DunTarget.Far; return true; }
            error = $"\"to\" must be {forms} (got \"{Clip(el.GetString() ?? "")}\")";
            return false;
        }
        if (el.ValueKind != JsonValueKind.Object) { error = $"\"to\" must be {forms}"; return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
        {
            if (!seen.Add(p.Name)) { error = $"\"to\".\"{p.Name}\" is given twice"; return false; }
            if (p.Name is not ("portal" or "x" or "y" or "z" or "room")) { error = $"\"to\" doesn't take \"{Clip(p.Name)}\": {forms}"; return false; }
        }
        bool hasPortal = seen.Contains("portal"), hasRoom = seen.Contains("room");
        bool hasXyz = seen.Contains("x") || seen.Contains("y") || seen.Contains("z");
        if ((hasPortal ? 1 : 0) + (hasRoom ? 1 : 0) + (hasXyz ? 1 : 0) != 1) { error = $"\"to\" is one of {forms}"; return false; }
        if (hasPortal)
        {
            var p = el.GetProperty("portal");
            string name = p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
            if (name.Length == 0) { error = "\"to\".\"portal\" must be the portal's name (a string)"; return false; }
            if (name.Length > MaxPortalName) { error = $"a portal name is at most {MaxPortalName} characters"; return false; }
            target = new DunTarget(DunTargetKind.Portal, Portal: name);
            return true;
        }
        if (hasRoom)
        {
            var r = el.GetProperty("room");
            if (r.ValueKind != JsonValueKind.Number || !r.TryGetInt32(out int n) || n < 0) { error = "\"to\".\"room\" must be a room number, 0 or more"; return false; }
            target = new DunTarget(DunTargetKind.Room, Room: n);
            return true;
        }
        var v = new double[3];
        string[] axes = { "x", "y", "z" };
        for (int i = 0; i < 3; i++)
        {
            if (!el.TryGetProperty(axes[i], out var a)) { error = $"\"to\" needs all of x, y and z (\"{axes[i]}\" is missing)"; return false; }
            if (a.ValueKind != JsonValueKind.Number || !a.TryGetDouble(out v[i]) || !double.IsFinite(v[i]))
            { error = $"\"to\".\"{axes[i]}\" must be a number"; return false; }
            if (Math.Abs(v[i]) > MaxCoordinate) { error = $"\"to\".\"{axes[i]}\" is landblock-local metres (within {MaxCoordinate:0})"; return false; }
        }
        target = new DunTarget(DunTargetKind.Position, X: v[0], Y: v[1], Z: v[2]);
        return true;
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "...";
}

/// <summary>Where the character is, for a route: the dungeon's map and what is known in it. Built by the plugin.</summary>
internal sealed class DunRouteWorld
{
    public uint Landblock;
    /// <summary>The engine's cell for the character.</summary>
    public uint PlayerCell;
    /// <summary>The cell the route starts in: the character's when the map has it, else the nearest.</summary>
    public uint StartCell;
    /// <summary>The character, world metres (x east, y north, z up).</summary>
    public double X, Y, Z;
    public Dictionary<uint, DungeonNavNode> Graph = new();
    public DungeonGeometry? Geo;
    /// <summary>Lava/acid cells kept out of (the patrol's set: Settings > Dungeon Patrol decides about floors).</summary>
    public IReadOnlySet<uint>? Hazards;
    /// <summary>Every portal the client knows in this dungeon (their keep-out circles from the nav settings).</summary>
    public List<PatrolPortal> Portals = new();
    /// <summary>A portal's appraised destination text (string property 38), when the client has it.</summary>
    public Func<uint, string?>? PortalDestination;
    /// <summary>Where RynthAi saw the character come into this dungeon (world metres), when it did.</summary>
    public (uint Cell, double X, double Y, double Z)? Arrival;
    /// <summary>The share of the floor the radar has painted as visited (null: not known).</summary>
    public double? Explored;
    public double Wall = DungeonPathfinder.WallClearance;

    public double OriginX => ((Landblock >> 8) & 0xFF) * 192.0;
    public double OriginY => (Landblock & 0xFF) * 192.0;

    /// <summary>The hazard cells in this landblock.</summary>
    public int HazardsHere => Hazards?.Count(c => c >> 16 == Landblock) ?? 0;
}

/// <summary>A route laid to a target (before it is saved).</summary>
internal sealed class DunRouteBuild
{
    public NavRouteParser Route = new() { RouteType = NavRouteType.Once };
    public DunTargetKind Kind;
    /// <summary>"the exit portal 'Surface Portal' (named like an exit)", for chat.</summary>
    public string TargetText = "";
    public string Why = "";
    public PatrolPortal? Portal;
    public int Room = -1;
    public uint StartCell, GoalCell;
    /// <summary>Where it ends (world metres): the spot, the room's middle, the portal.</summary>
    public double GoalX, GoalY, GoalZ;
    public int PathCells, Doorways;
    public double Yards;
    public readonly List<string> LeftOut = new();
    public readonly List<string> Notes = new();
}

/// <summary>A saved route described (/ra dunroute info, the interface's "info").</summary>
internal sealed class DunRouteInfo
{
    public string Name = "", File = "";
    public NavRouteType Type;
    public int Points, Waypoints;
    public readonly SortedDictionary<string, int> Steps = new(StringComparer.Ordinal)
    {
        ["portal"] = 0, ["recall"] = 0, ["pause"] = 0, ["chat"] = 0, ["jump"] = 0, ["npc"] = 0, ["vendor"] = 0, ["checkpoint"] = 0,
    };
    public double Yards;
    public uint? Landblock;
    public readonly List<uint> Landblocks = new();
    public bool? Dungeon;
    public int? Cells;
    public uint StartCell, EndCell;
    /// <summary>"yes", "partly", "no", "unknown" (the character isn't in a dungeon).</summary>
    public string ForThisDungeon = "unknown";
    public int PointsHere;
    public readonly List<string> Notes = new();
}

/// <summary>One portal the client knows, as "where" lists it.</summary>
internal sealed class DunWherePortal
{
    public PatrolPortal Portal = null!;
    public double Yards;
    public double? WalkYards;
    public string? Exit;
    public string? Destination;
}

internal static class DungeonRouteMaker
{
    /// <summary>A position target this far (yd) from every cell centre isn't in the dungeon (as NavRecovery's reach).</summary>
    internal const double PositionReachYd = 30.0;
    /// <summary>The exit is the portal nearest where the character came in, when it is this close (metres) to it.</summary>
    internal const double ArrivalReachMeters = 40.0;
    /// <summary>The object class a Portal step names (Decal's ObjectClass.Portal; VTank writes it, as RynthNav's routes).</summary>
    internal const int PortalObjectClass = 14;
    // Points closer than this (yd) to the one before are dropped.
    private const double MinSpacingYd = 0.3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Names an exit portal has: "Surface Portal", "Exit Portal", "Portal Out", "Leave the Hive".</summary>
    internal static readonly Regex ExitWords = new(@"\b(exit|surface|leave|outside|out|return)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An appraised destination that is a spot on the landscape: "12.3N, 45.6E".</summary>
    internal static readonly Regex LandscapeCoords = new(@"\d+(\.\d+)?\s*[NS]\s*,?\s*\d+(\.\d+)?\s*[EW]\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── Targets ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Why each known portal would be the exit, best first: rank 0 its destination is a spot on the
    /// landscape, 1 named like an exit, 2 the nearest to where the character came in (within
    /// ArrivalReachMeters), 3 the only portal known. Wrecked portals ("Destroyed ...") never are.
    /// Ties: the nearest to the character.
    /// </summary>
    internal static List<(PatrolPortal Portal, int Rank, string Why)> ExitCandidates(DunRouteWorld w)
    {
        var usable = w.Portals.Where(p => !p.Name.StartsWith("Destroyed", StringComparison.OrdinalIgnoreCase)).ToList();
        PatrolPortal? byArrival = null;
        if (w.Arrival is { } a)
        {
            double best = ArrivalReachMeters;
            foreach (var p in usable)
            {
                double d = Dist3(p.X, p.Y, p.Z, a.X, a.Y, a.Z);
                if (d <= best) { best = d; byArrival = p; }
            }
        }
        var list = new List<(PatrolPortal, int, string)>();
        foreach (var p in usable)
        {
            string? dest = Destination(w, p);
            if (dest != null && LandscapeCoords.IsMatch(dest)) list.Add((p, 0, $"its destination is on the landscape ({dest})"));
            else if (ExitWords.IsMatch(p.Name)) list.Add((p, 1, "named like an exit"));
            else if (ReferenceEquals(p, byArrival)) list.Add((p, 2, "the portal nearest where you came in"));
            else if (usable.Count == 1) list.Add((p, 3, "the only portal known here"));
        }
        list.Sort((x, y) => x.Item2 != y.Item2 ? x.Item2.CompareTo(y.Item2)
                          : DistTo(w, x.Item1).CompareTo(DistTo(w, y.Item1)));
        return list;
    }

    private static string? Destination(DunRouteWorld w, PatrolPortal p)
    {
        try
        {
            string? d = w.PortalDestination?.Invoke(p.Id);
            return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        }
        catch { return null; }
    }

    private static PatrolPortal? PickExit(DunRouteWorld w, out string why, out string error)
    {
        why = error = "";
        var c = ExitCandidates(w);
        if (c.Count > 0) { why = c[0].Why; return c[0].Portal; }
        error = w.Portals.Count == 0
            ? "no portal is known in this dungeon yet. The client only knows the objects it has been sent: walk on until the exit portal shows (the radar draws portals), then try again"
            : $"can't tell which portal is the exit: none is named like one, and RynthAi didn't see where you came in. Portals known: {PortalNames(w)}. Ask for one: to portal <name>";
        return null;
    }

    private static PatrolPortal? PickPortal(DunRouteWorld w, string name, out string why, out string error)
    {
        why = error = "";
        var exact = w.Portals.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var list = exact.Count > 0 ? exact : w.Portals.Where(p => p.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (list.Count == 0)
        {
            error = w.Portals.Count == 0
                ? $"no portal named '{name}' is known: the client knows no portal in this dungeon yet (it only knows the objects it has been sent)"
                : $"no portal named '{name}' is known here. Portals known: {PortalNames(w)}";
            return null;
        }
        var pick = list.OrderBy(p => DistTo(w, p)).First();
        why = list.Count > 1 ? $"{list.Count} portals match '{name}': the nearest" : exact.Count > 0 ? "" : $"its name has '{name}' in it";
        return pick;
    }

    private static string PortalNames(DunRouteWorld w)
        => string.Join(", ", w.Portals.OrderBy(p => DistTo(w, p)).Select(p => $"'{p.Name}' ({DistTo(w, p):0} yd)"));

    private static double DistTo(DunRouteWorld w, PatrolPortal p) => Dist3(p.X, p.Y, p.Z, w.X, w.Y, w.Z);

    private static uint CellOfPortal(DunRouteWorld w, PatrolPortal p)
        => p.Cell != 0 && w.Graph.ContainsKey(p.Cell) ? p.Cell : DungeonPathfinder.CellAt(w.Graph, w.Geo, WorldPt(p.X, p.Y, p.Z));

    /// <summary>
    /// The cell farthest from <paramref name="start"/> on foot (Dijkstra over the walkable edges: no
    /// drops, no hazard cells; centre to centre, climbs counted), and how far. 0 when the start has no
    /// way anywhere.
    /// </summary>
    internal static uint FarthestCell(Dictionary<uint, DungeonNavNode> graph, uint start, IReadOnlySet<uint>? hazards, out double yards)
    {
        yards = 0;
        if (!graph.ContainsKey(start)) return 0;
        var dist = WalkDistances(graph, start, hazards);
        uint best = 0;
        foreach (var (c, d) in dist)
            if (c != start && (d > yards || (d == yards && c < best))) { yards = d; best = c; }
        return best;
    }

    /// <summary>Walking distance (yd, centre to centre, climbs counted) from start to every cell it reaches.</summary>
    internal static Dictionary<uint, double> WalkDistances(Dictionary<uint, DungeonNavNode> graph, uint start, IReadOnlySet<uint>? hazards)
    {
        var dist = new Dictionary<uint, double> { [start] = 0 };
        var pq = new PriorityQueue<uint, double>();
        pq.Enqueue(start, 0);
        while (pq.TryDequeue(out uint c, out double d))
        {
            if (d > dist[c] || !graph.TryGetValue(c, out var n)) continue;
            foreach (uint nb in n.Neighbors)
            {
                if (!graph.TryGetValue(nb, out var o) || DungeonPathfinder.IsDropEdge(n, o)) continue;
                if (hazards != null && hazards.Contains(nb)) continue;
                double nd = d + Math.Sqrt(Sq((o.NS - n.NS) * 240.0) + Sq((o.EW - n.EW) * 240.0)) + Math.Abs(o.Z - n.Z);
                if (dist.TryGetValue(nb, out double old) && old <= nd) continue;
                dist[nb] = nd;
                pq.Enqueue(nb, nd);
            }
        }
        return dist;
    }

    // ── Laying the route ─────────────────────────────────────────────────────

    /// <summary>
    /// The route from the character to <paramref name="t"/>: a Once route, the character's spot first;
    /// null with the reason when there is no target or no walkable way.
    /// </summary>
    internal static DunRouteBuild? TryBuildRoute(DunRouteWorld w, DunTarget t, out string error)
    {
        error = "";
        if (w.Graph.Count == 0) { error = "this dungeon has no cells on the map"; return null; }
        if (!w.Graph.ContainsKey(w.StartCell)) { error = "can't place you on the dungeon map"; return null; }
        var b = new DunRouteBuild { Kind = t.Kind, StartCell = w.StartCell };
        uint goal;
        PatrolPortal? portal = null;
        switch (t.Kind)
        {
            case DunTargetKind.Exit:
            case DunTargetKind.Portal:
            {
                portal = t.Kind == DunTargetKind.Exit ? PickExit(w, out string why, out error) : PickPortal(w, t.Portal, out why, out error);
                if (portal == null) return null;
                b.Portal = portal;
                b.Why = why;
                goal = CellOfPortal(w, portal);
                (b.GoalX, b.GoalY, b.GoalZ) = (portal.X, portal.Y, portal.Z);
                b.TargetText = (t.Kind == DunTargetKind.Exit ? "the exit portal" : "portal") + $" '{portal.Name}'" + (why.Length > 0 ? $" ({why})" : "");
                break;
            }
            case DunTargetKind.Position:
            {
                double gx = w.OriginX + t.X, gy = w.OriginY + t.Y, gz = t.Z;
                var spot = WorldPt(gx, gy, gz);
                goal = DungeonPathfinder.CellAt(w.Graph, w.Geo, spot);
                if (goal == 0 || !w.Graph.TryGetValue(goal, out var gn)) { error = "can't place that spot on the dungeon map"; return null; }
                double off = NavYd(gn.NS - spot.NS, gn.EW - spot.EW);
                bool onFloor = w.Geo != null && w.Geo.OnCellFloor(goal, gx, gy, gz);
                if (!onFloor && off > PositionReachYd)
                {
                    error = string.Format(Inv, "({0:0.##}, {1:0.##}, {2:0.##}) isn't inside this dungeon: its nearest cell is {3:0} yd away", t.X, t.Y, t.Z, off);
                    return null;
                }
                if (w.Geo != null && w.Geo.FloorCellCount > 0 && !w.Geo.OnWalkableFloor(gx, gy, gz))
                    b.Notes.Add("that spot isn't on a floor the dungeon map knows (in a wall, or another height): the route still ends on it");
                (b.GoalX, b.GoalY, b.GoalZ) = (gx, gy, gz);
                b.TargetText = string.Format(Inv, "({0:0.##}, {1:0.##}, {2:0.##}) in cell {3:X8}", t.X, t.Y, t.Z, goal);
                break;
            }
            case DunTargetKind.Room:
            {
                if (w.Geo == null) { error = "rooms are numbered from the dungeon map, which isn't loaded yet: try again in a moment"; return null; }
                var rooms = DungeonPathfinder.BuildRooms(w.Graph, w.Geo);
                if (t.Room >= rooms.Cells.Count) { error = $"there is no room {t.Room}: this dungeon's rooms are 0 to {rooms.Cells.Count - 1} (/ra dunroute where says which you are in)"; return null; }
                var mid = DungeonPathfinder.RoomMiddle(w.Graph, w.Geo, rooms.Cells[t.Room], w.Wall, out goal);
                if (mid == null || !w.Graph.ContainsKey(goal)) { error = $"room {t.Room} has no point to go to"; return null; }
                (b.GoalX, b.GoalY, b.GoalZ) = (X(mid), Y(mid), mid.Z * 240.0);
                b.Room = t.Room;
                b.TargetText = $"room {t.Room} ({rooms.Cells[t.Room].Count} cell{(rooms.Cells[t.Room].Count == 1 ? "" : "s")}), its middle";
                break;
            }
            default:
            {
                uint far = FarthestCell(w.Graph, w.StartCell, w.Hazards, out double walk);
                if (far == 0) { error = "there is nowhere to walk to from here (no walkable way out of this cell)"; return null; }
                goal = far;
                var node = w.Graph[far];
                var mid = w.Geo != null ? DungeonPathfinder.RoomMiddle(w.Graph, w.Geo, new List<uint> { far }, w.Wall, out _) : null;
                (b.GoalX, b.GoalY, b.GoalZ) = mid != null ? (X(mid), Y(mid), mid.Z * 240.0)
                    : (DungeonGeometry.NavToWorld(node.EW), DungeonGeometry.NavToWorld(node.NS), (double)node.Z);
                b.TargetText = string.Format(Inv, "the farthest spot on foot, cell {0:X8} (about {1:0} yd away walking)", far, walk);
                break;
            }
        }
        b.GoalCell = goal;
        if (!w.Graph.ContainsKey(goal)) { error = "the target isn't on the dungeon map"; return null; }

        // The cell path: A* without drops or hazard cells (the goal may be one).
        var path = goal == w.StartCell ? new List<uint> { goal } : DungeonPathfinder.FindPath(w.StartCell, goal, w.Graph, w.Hazards);
        if (path.Count == 0)
        {
            bool viaHazard = w.Hazards is { Count: > 0 } && DungeonPathfinder.FindPath(w.StartCell, goal, w.Graph, null).Count > 0;
            error = $"{b.TargetText} can't be walked to from here: " + (viaHazard
                ? "the only ways go through lava/acid cells, which routes keep out of"
                : "it is only reached by a drop (a jump down the bot can't walk), or isn't joined to where you stand");
            return null;
        }
        b.PathCells = path.Count;

        // The points DungeonPathfinder lays on every dungeon path (as /ra dunnav, BuildNavRoute).
        double sNs = DungeonGeometry.WorldToNav(w.Y), sEw = DungeonGeometry.WorldToNav(w.X);
        double dNs = DungeonGeometry.WorldToNav(b.GoalY), dEw = DungeonGeometry.WorldToNav(b.GoalX);
        var pts = new List<NavPoint> { WorldPt(w.X, w.Y, w.Z) };
        if (w.Geo != null && w.Geo.DoorwayCount > 0)
        {
            var laid = DungeonPathfinder.BuildPathPoints(path, w.Graph, w.Geo, w.Hazards, sNs, sEw, w.Z, dNs, dEw, b.GoalZ, out int doorways);
            foreach (var p in laid) Append(pts, p);
            b.Doorways = doorways;
            Append(pts, WorldPt(b.GoalX, b.GoalY, b.GoalZ));
        }
        else
        {
            var cellRoute = DungeonPathfinder.BuildNavRoute(path, w.Graph, dNs, dEw);
            if (cellRoute.Points.Count > 0) cellRoute.Points[^1].Z = b.GoalZ / 240.0;   // the cell walk leaves the end's height out
            foreach (var p in cellRoute.Points) Append(pts, p);
            if (w.Geo == null) b.Notes.Add("the dungeon map isn't loaded yet: the points are cell-centre midpoints, not lined up on doorways (make it again in a moment for a better one)");
        }
        if (pts.Count == 1) pts.Add(WorldPt(b.GoalX, b.GoalY, b.GoalZ));

        // A portal target: the route stops at its edge, and a Portal step uses it.
        NavPoint? step = null;
        if (portal != null)
        {
            Append(pts, WorldPt(portal.X, portal.Y, portal.Z));   // the centre (already the end when the map laid the path)
            var approach = StopAtPortal(pts, portal);
            step = new NavPoint
            {
                Type = NavPointType.PortalNPC, NS = approach.NS, EW = approach.EW, Z = approach.Z,
                TargetName = portal.Name, ObjectClass = PortalObjectClass, IsTie = true,
                PortalExitNS = DungeonGeometry.WorldToNav(portal.Y), PortalExitEW = DungeonGeometry.WorldToNav(portal.X), PortalExitZ = portal.Z / 240.0,
            };
        }

        // The portals it isn't going to: kept out of, as the patrol is (a portal fires at a touch).
        var others = w.Portals.Where(p => !ReferenceEquals(p, portal)).ToList();
        if (others.Count > 0)
        {
            var reports = others.Select(p => new PortalAvoidance { Portal = p }).ToList();
            var unresolved = DungeonPathfinder.KeepOutOfPortals(pts, closed: false, w.Graph, w.Geo, others, reports, w.Wall);
            foreach (var r in reports)
                if (r.Moved + r.Dropped + r.Bent > 0) b.LeftOut.Add($"kept out of portal '{r.Portal.Name}' ({r.Bent} leg(s) bent round it, {r.Moved + r.Dropped} point(s) moved)");
            foreach (var (k, _) in unresolved.GroupBy(u => u.Portal).Select(g => g.First()))
                b.Notes.Add($"a leg passes portal '{others[k].Name}' closer than {others[k].Radius:0.#} m: no way round was found, and touching it takes you through");
        }
        if (step != null) pts.Add(step);

        b.Route = new NavRouteParser { RouteType = NavRouteType.Once, Points = pts };
        b.Yards = RouteYards(b.Route);
        int hz = w.HazardsHere;
        if (hz > 0) b.LeftOut.Add($"{hz} lava/acid cell{(hz == 1 ? "" : "s")} kept out of");
        AddKnownNotes(w, b.Notes);
        return b;
    }

    /// <summary>
    /// Cuts <paramref name="pts"/> (the character first, the portal's centre last) where it first comes
    /// to the portal's circle on its level, and returns the spot: the route ends there and the Portal
    /// step uses the portal (the client walks the last metres itself).
    /// </summary>
    internal static NavPoint StopAtPortal(List<NavPoint> pts, PatrolPortal p)
    {
        if (DungeonPathfinder.InsidePortal(p, X(pts[0]), Y(pts[0]), pts[0].Z * 240.0))
        {
            pts.RemoveRange(1, pts.Count - 1);
            return pts[0];
        }
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            if (DungeonPathfinder.LegDistance(p, a, b, out _) >= p.Radius - 0.01) continue;
            double ax = X(a), ay = Y(a), az = a.Z * 240.0, bx = X(b), by = Y(b), bz = b.Z * 240.0;
            double len = Math.Sqrt(Sq(bx - ax) + Sq(by - ay));
            int n = Math.Max(1, (int)Math.Ceiling(len / 0.1));
            NavPoint last = a;
            for (int k = 1; k <= n; k++)
            {
                double s = (double)k / n;
                double x = ax + (bx - ax) * s, y = ay + (by - ay) * s, z = az + (bz - az) * s;
                if (DungeonPathfinder.InsidePortal(p, x, y, z)) break;
                last = WorldPt(x, y, z);
            }
            pts.RemoveRange(i + 1, pts.Count - i - 1);
            if (!ReferenceEquals(last, a)) Append(pts, last);
            return pts[^1];
        }
        pts.RemoveAt(pts.Count - 1);   // never in the circle (another level): the route ends where it got to
        return pts[^1];
    }

    /// <summary>The notes every answer carries about what isn't known yet.</summary>
    internal static void AddKnownNotes(DunRouteWorld w, List<string> notes)
    {
        notes.Add(w.Portals.Count == 0
            ? "no portal is known here yet (the client only knows the ones it has been sent)"
            : $"{w.Portals.Count} portal{(w.Portals.Count == 1 ? "" : "s")} known here (the client only knows the ones it has been sent)");
        if (w.Explored is double e && e < 0.995)
            notes.Add(string.Format(Inv, "{0:0}% of this dungeon's floor explored: lava/acid not sighted yet isn't kept out of", e * 100));
    }

    /// <summary>The walked length of a route (yd): legs between plain waypoints, a step (portal, recall,
    /// chat...) breaks the walk; a Circular route's closing leg counted.</summary>
    internal static double RouteYards(NavRouteParser route)
    {
        double total = 0;
        var p = route.Points;
        for (int i = 0; i + 1 < p.Count; i++)
            if (NavRouteParser.IsPlainWaypoint(p[i].Type) && NavRouteParser.IsPlainWaypoint(p[i + 1].Type))
                total += Leg3(p[i], p[i + 1]);
        if (route.RouteType == NavRouteType.Circular && p.Count > 2
            && NavRouteParser.IsPlainWaypoint(p[^1].Type) && NavRouteParser.IsPlainWaypoint(p[0].Type))
            total += Leg3(p[^1], p[0]);
        return total;
    }

    /// <summary>Cells the walkable graph reaches from <paramref name="start"/> (no drops, no hazard cells).</summary>
    internal static int ReachableCount(Dictionary<uint, DungeonNavNode> graph, uint start, IReadOnlySet<uint>? hazards)
        => graph.ContainsKey(start) ? WalkDistances(graph, start, hazards).Count : 0;

    // ── Info ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A saved route described: its points and steps, walked length, the landblock(s) its points are
    /// in, the cells under them, and whether it was made for the dungeon the character is in
    /// (<paramref name="playerCell"/>: 0 or a landscape cell = not in one), judged as NavigationEngine's
    /// area hold judges each waypoint (NavRecoveryPlanner.ClassifyArea). <paramref name="graphOf"/>:
    /// a landblock's cell graph (null: the dats aren't loaded); <paramref name="isDungeon"/>: whether
    /// a landblock is a dungeon (null: can't tell).
    /// </summary>
    internal static DunRouteInfo DescribeRoute(string name, string file, NavRouteParser route, uint playerCell,
        Func<uint, Dictionary<uint, DungeonNavNode>?> graphOf, Func<uint, bool?> isDungeon)
    {
        var info = new DunRouteInfo { Name = name, File = file, Type = route.RouteType, Points = route.Points.Count };
        foreach (var p in route.Points)
        {
            if (NavRouteParser.IsPlainWaypoint(p.Type)) info.Waypoints++;
            string? key = p.Type switch
            {
                NavPointType.PortalNPC  => "portal",
                NavPointType.Recall     => "recall",
                NavPointType.Pause      => "pause",
                NavPointType.Chat       => "chat",
                NavPointType.Jump       => "jump",
                NavPointType.Npc        => "npc",
                NavPointType.OpenVendor => "vendor",
                NavPointType.Checkpoint => "checkpoint",
                _                       => null,
            };
            if (key != null) info.Steps[key]++;
        }
        info.Yards = RouteYards(route);
        if (route.RouteType == NavRouteType.Follow) info.Notes.Add($"a Follow route: it follows '{route.FollowTargetName}' and has no points");

        var walk = route.Points.Where(p => NavRouteParser.IsPlainWaypoint(p.Type)).ToList();
        var byLb = new Dictionary<uint, int>();
        foreach (var p in walk)
            if (LandblockOf(p) is uint lb) byLb[lb] = byLb.GetValueOrDefault(lb) + 1;
        foreach (var kv in byLb.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)) info.Landblocks.Add(kv.Key);

        // The character's dungeon, as the engine's area hold sees it (the same landblock, or within reach of its cells).
        uint pLb = playerCell >> 16;
        bool playerInDungeon = playerCell != 0 && (playerCell & 0xFFFF) >= 0x0100 && isDungeon(pLb) == true;
        Dictionary<uint, DungeonNavNode>? playerGraph = playerInDungeon ? graphOf(pLb) : null;
        if (playerInDungeon && playerGraph != null)
        {
            foreach (var p in walk)
            {
                var a = NavRecoveryPlanner.ClassifyArea(playerCell, p.NS, p.EW, lb => lb == pLb || isDungeon(lb) == true,
                    (lb, ns, ew) => lb == pLb && NearCell(playerGraph, ns, ew, NavRecoveryPlanner.AreaCellReachYd), out _, out _);
                if (a == NavAreaCheck.Match) info.PointsHere++;
            }
            info.ForThisDungeon = walk.Count == 0 ? "unknown" : info.PointsHere == walk.Count ? "yes" : info.PointsHere == 0 ? "no" : "partly";
        }
        else if (playerCell != 0 && (playerCell & 0xFFFF) >= 0x0100 && playerGraph == null && isDungeon(pLb) == true)
            info.Notes.Add("the dungeon data isn't loaded yet: whether it is this dungeon's can't be told");
        else info.Notes.Add("you aren't in a dungeon: \"forThisDungeon\" is unknown");

        // The route's landblock: the character's dungeon when its points are there. Else the dungeon whose
        // cells most of its points are near, of the landblocks they fall in and the ones round them (a
        // dungeon's cells can lie outside its own 192 m square: Tiny Hive 0x0289's are in 0x0288's), else
        // where most of them are (a landscape route).
        uint? routeLb = null;
        Dictionary<uint, DungeonNavNode>? g = null;
        if (playerGraph != null && info.PointsHere > 0 && info.PointsHere * 2 >= walk.Count) { routeLb = pLb; g = playerGraph; }
        else
        {
            var candidates = new SortedSet<uint>();
            foreach (uint lb in info.Landblocks)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int x = (int)(lb >> 8) + dx, y = (int)(lb & 0xFF) + dy;
                        if (x >= 0 && x < 256 && y >= 0 && y < 256) candidates.Add(((uint)x << 8) | (uint)y);
                    }
            // Most points in one of its cells (height counted: dungeons next to each other overlap in plan,
            // at other depths), and at least half of them.
            int bestNear = 0;
            foreach (uint c in candidates)
            {
                if (isDungeon(c) != true) continue;
                var cg = c == pLb && playerGraph != null ? playerGraph : graphOf(c);
                if (cg == null || cg.Count == 0) continue;
                int near = walk.Count(p => InCellOf(cg, p, out _));
                if (near > bestNear && near * 2 >= walk.Count) { bestNear = near; routeLb = c; g = cg; }
            }
            if (routeLb == null && info.Landblocks.Count > 0) routeLb = info.Landblocks[0];
        }
        info.Landblock = routeLb;
        if (routeLb is uint rl)
        {
            info.Dungeon = g != null ? true : isDungeon(rl);
            if (g != null && g.Count > 0)
            {
                var cells = new HashSet<uint>();
                uint first = 0, last = 0;
                foreach (var p in walk)
                {
                    if (!InCellOf(g, p, out uint c)) continue;
                    cells.Add(c);
                    if (first == 0) first = c;
                    last = c;
                }
                info.Cells = cells.Count;
                info.StartCell = first;
                info.EndCell = last;
            }
            if (info.Dungeon == false) info.Notes.Add($"landblock {rl:X4} isn't a dungeon: a landscape (or town) route");
            if (info.ForThisDungeon is "no" or "partly" && rl != pLb)
                info.Notes.Add($"it was made in landblock {rl:X4}; you are in {pLb:X4}");
        }
        return info;
    }

    /// <summary>The landblock a nav point's coordinates fall in (null: off the map).</summary>
    internal static uint? LandblockOf(NavPoint p)
    {
        double wx = DungeonGeometry.NavToWorld(p.EW), wy = DungeonGeometry.NavToWorld(p.NS);
        if (wx < 0 || wy < 0 || wx >= 256 * 192.0 || wy >= 256 * 192.0) return null;
        return ((uint)(wx / 192.0) << 8) | (uint)(wy / 192.0);
    }

    // A point is in a cell when the nearest cell origin on its level is this close (a big cell's corner is
    // ~15 m from its origin) and within this much height. Z 0 is "not recorded" (old routes): plan only.
    private const double InCellReachYd = 20.0, InCellRise = 8.0;

    /// <summary>The cell of <paramref name="graph"/> a route point is in, by its origin's distance and height.</summary>
    private static bool InCellOf(Dictionary<uint, DungeonNavNode> graph, NavPoint p, out uint cell)
    {
        cell = DungeonPathfinder.NearestCell(graph, p.NS, p.EW, p.Z != 0 ? (float)(p.Z * 240.0) : float.NaN);
        if (cell == 0 || !graph.TryGetValue(cell, out var n)) return false;
        if (NavYd(n.NS - p.NS, n.EW - p.EW) > InCellReachYd) return false;
        return p.Z == 0 || Math.Abs(n.Z - p.Z * 240.0) <= InCellRise;
    }

    private static bool NearCell(Dictionary<uint, DungeonNavNode> graph, double ns, double ew, double reachYd)
    {
        foreach (var n in graph.Values)
            if (NavYd(n.NS - ns, n.EW - ew) <= reachYd) return true;
        return false;
    }

    // ── Where ────────────────────────────────────────────────────────────────

    /// <summary>Every known portal with its distance (straight, and on foot along the cell path), why it
    /// would be the exit, and its appraised destination; nearest first.</summary>
    internal static List<DunWherePortal> PortalsWhere(DunRouteWorld w)
    {
        var exits = new Dictionary<PatrolPortal, string>();   // PatrolPortal is compared by reference
        foreach (var c in ExitCandidates(w)) exits[c.Portal] = c.Why;
        var list = new List<DunWherePortal>();
        foreach (var p in w.Portals)
        {
            var e = new DunWherePortal { Portal = p, Yards = DistTo(w, p), Destination = Destination(w, p) };
            if (exits.TryGetValue(p, out string? why)) e.Exit = why;
            uint cell = CellOfPortal(w, p);
            if (w.Graph.ContainsKey(w.StartCell) && w.Graph.ContainsKey(cell))
            {
                var path = cell == w.StartCell ? new List<uint> { cell } : DungeonPathfinder.FindPath(w.StartCell, cell, w.Graph, w.Hazards);
                if (path.Count > 0)
                {
                    double len = 0, px = w.X, py = w.Y, pz = w.Z;
                    for (int i = 1; i < path.Count; i++)
                    {
                        var n = w.Graph[path[i]];
                        double nx = DungeonGeometry.NavToWorld(n.EW), ny = DungeonGeometry.NavToWorld(n.NS);
                        len += Dist3(px, py, pz, nx, ny, n.Z);
                        (px, py, pz) = (nx, ny, n.Z);
                    }
                    e.WalkYards = len + Dist3(px, py, pz, p.X, p.Y, p.Z);
                }
            }
            list.Add(e);
        }
        list.Sort((a, b) => a.Yards.CompareTo(b.Yards));
        return list;
    }

    // ── Files ────────────────────────────────────────────────────────────────

    /// <summary>RynthAi's route folder (what its Nav picker lists). Settable: the host tests point it at their scratch folder.</summary>
    internal static string Folder = LegacyNavigationUi.NavFolder;

    internal static string PathFor(string name) => Path.Combine(Folder, name + ".nav");

    /// <summary>
    /// Writes <paramref name="route"/> to NavProfiles\&lt;name&gt;.nav. An existing file is replaced only
    /// with <paramref name="overwrite"/>; otherwise nothing is written and the error says so. A failed
    /// write says why (DataWriteGuard: access denied, read-only, in use... and the fix). Then its
    /// doorway points (and <paramref name="landblock"/>, the dungeon) go into NavProfiles'
    /// .rynthai-routes.json (DunRouteSidecar), which the .nav format can't hold; a problem there goes
    /// to <paramref name="notes"/>, the route is saved either way.
    /// </summary>
    internal static bool TrySave(string name, NavRouteParser route, bool overwrite, uint landblock, out string path, out bool overwrote,
                                 out string error, List<string>? notes = null)
    {
        path = "";
        overwrote = false;
        if (!DunRouteRequest.TryName(name, out name, out error)) return false;
        path = PathFor(name);
        overwrote = File.Exists(path);
        if (overwrote && !overwrite) { error = AlreadyThere(name); return false; }
        string target = path;
        if (!DataWriteGuard.TrySave("the route", target, () => route.Save(target), out string why, callerShows: true))
        {
            error = $"{name}.nav NOT saved: {why}";
            overwrote = false;
            return false;
        }
        string? problem;
        try
        {
            if (!DunRouteSidecar.Record(Folder, Path.GetFileName(target), File.ReadAllBytes(target), landblock, route, out problem))
                problem = $"its doorway points couldn't be recorded ({problem}): from the file it runs with the ordinary point reach";
        }
        catch (Exception ex) { problem = $"its doorway points couldn't be recorded ({ex.Message}): from the file it runs with the ordinary point reach"; }
        if (problem != null) notes?.Add(problem);
        return true;
    }

    /// <summary>The route's doorway points (the ones the sidecar keeps).</summary>
    internal static int DoorwayPoints(NavRouteParser route) => route.Points.Count(p => p.Doorway && NavRouteParser.IsPlainWaypoint(p.Type));

    internal static string AlreadyThere(string name) => $"{name}.nav is already in NavProfiles: add overwrite to replace it";

    // ── JSON answers (RynthAiRoutesApi.cs has the shapes) ────────────────────

    // Text as written ("isn't", "Settings > Dungeon Patrol"): the answers go to another plugin, not into a web page.
    // Quotes, backslashes and control characters are still escaped.
    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, JsonOptions)) write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string Hex(uint v, int digits) => v.ToString(digits == 4 ? "X4" : "X8", Inv);

    private static double R2(double v) => Math.Round(v, 2);

    /// <summary>{"cell","x","y","z"} for a world spot, x/y/z landblock-local.</summary>
    internal static void WriteSpot(Utf8JsonWriter j, DunRouteWorld w, uint cell, double x, double y, double z)
    {
        j.WriteStartObject();
        j.WriteString("cell", Hex(cell, 8));
        j.WriteNumber("x", R2(x - w.OriginX));
        j.WriteNumber("y", R2(y - w.OriginY));
        j.WriteNumber("z", R2(z));
        j.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter j, string key, IEnumerable<string> items)
    {
        j.WriteStartArray(key);
        foreach (string s in items) j.WriteStringValue(s);
        j.WriteEndArray();
    }

    internal static void WriteCounts(Utf8JsonWriter j, NavRouteParser route, string type)
    {
        j.WriteString("type", type);
        j.WriteNumber("points", route.Points.Count);
        j.WriteNumber("waypoints", route.Points.Count(p => NavRouteParser.IsPlainWaypoint(p.Type)));
        j.WriteNumber("yards", Math.Round(RouteYards(route), 1));
    }

    internal static string RouteJson(DunRouteWorld w, DunRouteBuild b, string name, string file, bool overwrote) => Json(j =>
    {
        j.WriteStartObject();
        j.WriteString("file", file);
        j.WriteString("name", name);
        WriteCounts(j, b.Route, "once");
        j.WriteString("landblock", Hex(w.Landblock, 4));
        j.WritePropertyName("from");
        WriteSpot(j, w, w.PlayerCell, w.X, w.Y, w.Z);
        j.WriteStartObject("to");
        j.WriteString("kind", DunTarget.KindName(b.Kind));
        if (b.Portal != null) { j.WriteString("portal", b.Portal.Name); j.WriteString("id", Hex(b.Portal.Id, 8)); }
        else { j.WriteNull("portal"); j.WriteNull("id"); }
        if (b.Room >= 0) j.WriteNumber("room", b.Room);
        j.WriteString("cell", Hex(b.GoalCell, 8));
        j.WriteNumber("x", R2(b.GoalX - w.OriginX));
        j.WriteNumber("y", R2(b.GoalY - w.OriginY));
        j.WriteNumber("z", R2(b.GoalZ));
        j.WriteString("why", b.Why);
        j.WriteEndObject();
        j.WriteNumber("cells", b.PathCells);
        j.WriteNumber("doorways", b.Doorways);
        j.WriteNumber("doorwayPoints", DoorwayPoints(b.Route));
        j.WriteBoolean("overwrote", overwrote);
        WriteList(j, "leftOut", b.LeftOut);
        WriteList(j, "notes", b.Notes);
        j.WriteEndObject();
    });

    internal static string InfoJson(DunRouteInfo i) => Json(j =>
    {
        j.WriteStartObject();
        j.WriteString("file", i.File);
        j.WriteString("name", i.Name);
        j.WriteString("type", TypeName(i.Type));
        j.WriteNumber("points", i.Points);
        j.WriteNumber("waypoints", i.Waypoints);
        j.WriteStartObject("steps");
        foreach (var (k, v) in i.Steps) j.WriteNumber(k, v);
        j.WriteEndObject();
        j.WriteNumber("yards", Math.Round(i.Yards, 1));
        if (i.Landblock is uint lb) j.WriteString("landblock", Hex(lb, 4)); else j.WriteNull("landblock");
        WriteList(j, "landblocks", i.Landblocks.Select(l => Hex(l, 4)));
        if (i.Dungeon is bool d) j.WriteBoolean("dungeon", d); else j.WriteNull("dungeon");
        if (i.Cells is int c) j.WriteNumber("cells", c); else j.WriteNull("cells");
        if (i.StartCell != 0) j.WriteString("startCell", Hex(i.StartCell, 8)); else j.WriteNull("startCell");
        if (i.EndCell != 0) j.WriteString("endCell", Hex(i.EndCell, 8)); else j.WriteNull("endCell");
        j.WriteString("forThisDungeon", i.ForThisDungeon);
        j.WriteNumber("pointsHere", i.PointsHere);
        WriteList(j, "notes", i.Notes);
        j.WriteEndObject();
    });

    internal static string TypeName(NavRouteType t) => t switch
    {
        NavRouteType.Circular => "circular",
        NavRouteType.Linear   => "linear",
        NavRouteType.Follow   => "follow",
        NavRouteType.Once     => "once",
        _                     => ((int)t).ToString(Inv),
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Adds p unless it is on top of the last point.
    private static void Append(List<NavPoint> pts, NavPoint p)
    {
        if (pts.Count > 0 && NavYd(pts[^1].NS - p.NS, pts[^1].EW - p.EW) < MinSpacingYd && Math.Abs(pts[^1].Z - p.Z) * 240.0 < 0.5)
        {
            pts[^1].Doorway |= p.Doorway;
            return;
        }
        pts.Add(p);
    }

    internal static NavPoint WorldPt(double x, double y, double z) => new()
    {
        Type = NavPointType.Point,
        EW = DungeonGeometry.WorldToNav(x),
        NS = DungeonGeometry.WorldToNav(y),
        Z = z / 240.0,
    };

    private static double Leg3(NavPoint a, NavPoint b)
        => Math.Sqrt(Sq((a.NS - b.NS) * 240.0) + Sq((a.EW - b.EW) * 240.0) + Sq((a.Z - b.Z) * 240.0));

    private static double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
    private static double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);
    private static double NavYd(double dNs, double dEw) => Math.Sqrt(dNs * dNs + dEw * dEw) * 240.0;
    private static double Dist3(double ax, double ay, double az, double bx, double by, double bz) => Math.Sqrt(Sq(ax - bx) + Sq(ay - by) + Sq(az - bz));
    private static double Sq(double v) => v * v;
}
