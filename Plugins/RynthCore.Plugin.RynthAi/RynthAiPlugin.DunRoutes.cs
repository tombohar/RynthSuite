using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Dungeon routes on demand (2026-10-30). Players and plugins (an AI layer driving characters) save
/// RynthAi nav routes inside a dungeon, from where the character stands:
///   /ra dunroute &lt;name&gt; to exit|far|portal &lt;name&gt;|&lt;x&gt; &lt;y&gt; &lt;z&gt;|room &lt;n&gt; [overwrite]
///   /ra dunpatrol-save &lt;name&gt; [overwrite]    the patrol /ra dunnav-patrol would walk now, as a circular nav
///   /ra dunroute info &lt;name&gt;                 a saved nav described (points, length, cells, landblock, this dungeon's?)
///   /ra dunroute where                       where the character is, and the portals known here
/// and the "RynthAi.Routes" interface (Shared/RynthAiRoutesApi.cs) does the same with JSON. One
/// implementation: <see cref="RunDunRouteRequest"/> (the route itself is DungeonRouteMaker's, the patrol
/// TryBuildDunPatrol's, the one dunnav-patrol installs). The chat command runs it at once, on the thread
/// commands run on (as dunnav-patrol always has); an interface job runs it from OnTick, one a tick
/// (TickDunRoutes), and waits while the client isn't ready (portal space, the dats loading).
/// Nothing is loaded or started: the route is a file in NavProfiles, run with /ra nav load.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private const string DunRouteUsage =
        "[RynthAi] Usage: /ra dunroute <name> to exit|far|portal <portal name>|<x> <y> <z>|room <n> [overwrite] — /ra dunroute info <name> — /ra dunroute where";
    private const string DunPatrolSaveUsage =
        "[RynthAi] Usage: /ra dunpatrol-save <name> [overwrite]  (the patrol /ra dunnav-patrol would walk from here, saved as a circular nav)";

    private DunRouteJobs? _routeJobs;

    /// <summary>The interface's jobs (RoutesInterface; the host tests read them too).</summary>
    internal DunRouteJobs RouteJobs => _routeJobs ??= new DunRouteJobs();

    // Where the character came into the dungeon it is in: the exit portal is usually by it.
    private uint _dunLastLandblock;
    private (uint Landblock, uint Cell, double X, double Y, double Z)? _dunArrival;

    /// <summary>What a request came to: done (Json is the result), failed, or waiting on the client (Wait).
    /// Detail is the one line chat shows (without the "[RynthAi] " tag); Lines go under it.</summary>
    internal sealed record DunRouteOutcome(bool Ok, bool Wait, string Detail, string? Json, IReadOnlyList<string> Lines)
    {
        public static DunRouteOutcome Fail(string why) => new(false, false, Bare(why), null, Array.Empty<string>());
        public static DunRouteOutcome Waiting(string why) => new(false, true, Bare(why), null, Array.Empty<string>());

        /// <summary>A line without the chat tags the shared build lines start with.</summary>
        internal static string Bare(string s)
        {
            s = (s ?? "").Trim();
            foreach (string tag in new[] { "[RynthAi] ", "DunNav-Patrol: ", "DunRoute: " })
                if (s.StartsWith(tag, StringComparison.Ordinal)) s = s[tag.Length..];
            return s;
        }
    }

    // ── The interface's side ─────────────────────────────────────────────────

    /// <summary>RynthAi.Routes Request: the job id, or 0 with the reason (bad JSON, not logged in, too many jobs).</summary>
    internal uint RouteRequest(string json, out string error)
    {
        if (!DunRouteRequest.TryParseJson(json, out var request, out error)) return 0;
        if (!_loginComplete) { error = "RynthAi isn't logged in: there is no character in the world"; return 0; }
        uint id = RouteJobs.Add(request, out error);
        if (id != 0) Host.Log($"[RynthAi] Routes: job {id} queued ({DunRouteRequest.OpName(request.Op)}{(request.Name.Length > 0 ? " '" + request.Name + "'" : "")})");
        return id;
    }

    // The route the doorway check last looked at (and its point list: an edit replaces it), and the
    // sidecar entry waiting for the character to be in its dungeon.
    private NavRouteParser? _doorwayRoute;
    private List<NavPoint>? _doorwayPoints;
    private DunRouteSidecar.Entry? _doorwayPending;

    /// <summary>
    /// A nav just loaded that the route maker wrote (DunRouteSidecar: the same name and hash): its
    /// doorway points get their flag back once the character is in that dungeon, so it walks the
    /// doorways as dunnav-patrol does. However it was loaded (the Nav picker, /ra nav load, /vt nav load,
    /// a meta): checked when the current route changes. An edited file, another dungeon, any other
    /// nav: nothing. Before navigation in the same tick.
    /// </summary>
    private void TickDunDoorways()
    {
        var s = _dashboard?.Settings;
        if (s == null) return;
        var route = s.CurrentRoute;
        if (!ReferenceEquals(route, _doorwayRoute))
        {
            _doorwayRoute = route;
            _doorwayPoints = route?.Points;
            string? log = null;
            _doorwayPending = route == null ? null : DunRouteSidecar.Match(DungeonRouteMaker.Folder, s.CurrentNavPath, route, out log);
            if (log != null) Host.Log(log);
        }
        var e = _doorwayPending;
        if (e == null || route == null) return;
        if (!ReferenceEquals(route.Points, _doorwayPoints))
        {
            Host.Log($"[RynthAi] DunRoute: '{e.File}' was edited before you were in its dungeon: its doorway points aren't restored");
            _doorwayPending = null;
            return;
        }
        if (!_loginComplete || !Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)) return;
        if ((cell >> 16) != e.Landblock || (cell & 0xFFFF) < 0x0100) return;   // not in that dungeon (yet)
        int n = DunRouteSidecar.Apply(route, e);
        _doorwayPending = null;
        Host.Log($"[RynthAi] DunRoute: '{e.File}': {n} doorway point(s) restored (saved by the route maker in dungeon {e.Landblock:X4})");
    }

    /// <summary>OnTick (every tick): the doorway flags of a route just loaded, where the character came into a
    /// dungeon, and the oldest job.</summary>
    private void TickDunRoutes()
    {
        TickDunDoorways();
        if (_loginComplete) TickDunArrival();
        var jobs = _routeJobs;
        var job = jobs?.Next();
        if (jobs == null || job == null) return;
        if (!_loginComplete)
        {
            jobs.Finish(job, false, "the character left the world before the job ran", null);
            return;
        }
        jobs.Working(job, "working");
        DunRouteOutcome o;
        try { o = RunDunRouteRequest(job.Request); }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] Routes: job {job.Id} threw: {ex}");
            o = DunRouteOutcome.Fail($"failed ({ex.GetType().Name}: {ex.Message})");
        }
        if (o.Wait)
        {
            if (jobs.WaitedTooLong(job)) jobs.Finish(job, false, $"gave up after {DunRouteJobs.WaitLimitMs / 1000} s: {o.Detail}", null);
            else jobs.Working(job, "waiting: " + o.Detail);
            return;
        }
        jobs.Finish(job, o.Ok, o.Detail, o.Json);
        Host.Log($"[RynthAi] Routes: job {job.Id} {(o.Ok ? "done" : "failed")}: {o.Detail}");
    }

    /// <summary>Where the character lands in a new landblock (after portal space) is where it came in.
    /// Not where RynthAi first sees it (a login, a reload): that isn't an arrival.</summary>
    private void TickDunArrival()
    {
        if (Host.HasIsPortaling && Host.IsPortaling()) return;
        if (!Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _)) return;
        uint lb = cell >> 16;
        if (lb == 0 || lb == _dunLastLandblock) return;
        bool first = _dunLastLandblock == 0;
        _dunLastLandblock = lb;
        _dunArrival = null;
        if (first || (cell & 0xFFFF) < 0x0100) return;
        _dunArrival = (lb, cell, ((cell >> 24) & 0xFF) * 192.0 + x, ((cell >> 16) & 0xFF) * 192.0 + y, z);
        Host.Log($"[RynthAi] DunRoute: came into dungeon {lb:X4} at cell {cell:X8} ({x:F1}, {y:F1}, {z:F1})");
    }

    private void ForgetDunArrival()
    {
        _dunLastLandblock = 0;
        _dunArrival = null;
    }

    /// <summary>What a route needs beyond the map: where the character came in, the explored share, portal destinations.</summary>
    private void FillDunWorldExtras(DunRouteWorld w)
    {
        if (_dunArrival is { } a && a.Landblock == w.Landblock) w.Arrival = (a.Cell, a.X, a.Y, a.Z);
        try { w.Explored = _dashboard?.ExploredShare(w.Landblock); } catch { w.Explored = null; }
        const uint STypePortalDest = 38u;   // the appraised destination ("Destination: ..."), when the client has it
        w.PortalDestination = id => Host.HasGetObjectStringProperty && Host.TryGetObjectStringProperty(id, STypePortalDest, out string d) ? d : null;
    }

    // ── One implementation ───────────────────────────────────────────────────

    /// <summary>Runs one request (the chat command's or a job's). Pump thread (DungeonPathfinder's caches).</summary>
    internal DunRouteOutcome RunDunRouteRequest(DunRouteRequest r)
    {
        if (r.Op != DunRouteOp.Info && Host.HasIsPortaling && Host.IsPortaling())
            return DunRouteOutcome.Waiting("you're in portal space");
        return r.Op switch
        {
            DunRouteOp.Where     => RunDunWhere(),
            DunRouteOp.Info      => RunDunInfo(r),
            DunRouteOp.DunPatrol => RunDunPatrolSave(r),
            _                    => RunDunRoute(r),
        };
    }

    private DunRouteOutcome RunDunRoute(DunRouteRequest r)
    {
        if (r.To == null) return DunRouteOutcome.Fail("no target: to exit|far|portal <name>|<x> <y> <z>|room <n>");
        if (!r.Overwrite && File.Exists(DungeonRouteMaker.PathFor(r.Name))) return DunRouteOutcome.Fail(DungeonRouteMaker.AlreadyThere(r.Name));
        var w = TryGetDunWorld("dunroute", "DunRoute", out _, out string error, out var refusal);
        if (w == null) return refusal == DunBuildRefusal.NotReady ? DunRouteOutcome.Waiting(error) : DunRouteOutcome.Fail(error);
        FillDunWorldExtras(w);
        var b = DungeonRouteMaker.TryBuildRoute(w, r.To, out error);
        if (b == null) return DunRouteOutcome.Fail(error);
        if (!DungeonRouteMaker.TrySave(r.Name, b.Route, r.Overwrite, w.Landblock, out string path, out bool overwrote, out error, b.Notes))
            return DunRouteOutcome.Fail(error);
        Host.Log($"[RynthAi] DunRoute: {path}: {b.Route.Points.Count} points, {b.Yards:F0} yd, cells {b.StartCell:X8} -> {b.GoalCell:X8} ({b.PathCells} on the path), {r.To.Words()} = {b.TargetText}");
        string detail = $"{(overwrote ? "replaced" : "saved")} {r.Name}.nav: {b.Route.Points.Count} points, {b.Yards:0} yd from cell {w.PlayerCell:X8} to {b.TargetText}. Run it: /ra nav load {r.Name}";
        return new DunRouteOutcome(true, false, detail, DungeonRouteMaker.RouteJson(w, b, r.Name, path, overwrote), Under(b.LeftOut, b.Notes));
    }

    private DunRouteOutcome RunDunPatrolSave(DunRouteRequest r)
    {
        if (!r.Overwrite && File.Exists(DungeonRouteMaker.PathFor(r.Name))) return DunRouteOutcome.Fail(DungeonRouteMaker.AlreadyThere(r.Name));
        var b = TryBuildDunPatrol("dunpatrol-save", out string error, out var refusal);
        if (b == null) return refusal == DunBuildRefusal.NotReady ? DunRouteOutcome.Waiting(error) : DunRouteOutcome.Fail(error);
        var w = b.World;
        FillDunWorldExtras(w);
        var route = new NavRouteParser { RouteType = NavRouteType.Circular, Points = b.Loop };
        if (route.Points.Count == 0) return DunRouteOutcome.Fail("no walkable patrol from here");
        var leftOut = new List<string>();
        var notes = new List<string>();
        if (!DungeonRouteMaker.TrySave(r.Name, route, r.Overwrite, w.Landblock, out string path, out bool overwrote, out error, notes))
            return DunRouteOutcome.Fail(error);

        // What the lap doesn't walk, and why.
        int cells = w.Graph.Count;
        int reachable = DungeonRouteMaker.ReachableCount(w.Graph, b.StartCell, w.Hazards);
        var rooms = w.Geo != null && w.Geo.DoorwayCount > 0 ? DungeonPathfinder.BuildRooms(w.Graph, w.Geo, w.Hazards) : null;
        int covered = DungeonPathfinder.GetMainRouteNodes(w.Graph, b.StartCell, w.Hazards, b.Options, rooms).Count;
        if (reachable < cells) leftOut.Add($"{cells - reachable} of {cells} cells can't be walked to from here (behind drops or lava/acid, or not joined)");
        if (covered < reachable)
            leftOut.Add($"{reachable - covered} reachable cells left out by the coverage ({DungeonPatrolOptions.CoverageName(b.Options.Coverage)}"
                        + (b.Options.Coverage == PatrolCoverage.Everything ? "" : $", dead ends of up to {b.Options.MaxDeadEndCells} cells") + "; Settings > Dungeon Patrol)");
        int hz = w.HazardsHere;
        if (hz > 0) leftOut.Add($"{hz} lava/acid cell{(hz == 1 ? "" : "s")} kept out of");
        foreach (var a in b.PortalReport ?? new List<PortalAvoidance>())
            if (a.Closed.Count + a.CutCells.Count > 0)
                leftOut.Add($"beyond portal '{a.Portal.Name}': {a.Closed.Count} opening(s) closed" + (a.CutCells.Count > 0 ? $", {a.CutCells.Count} cell(s) cut off" : "") + " (a portal fires at a touch)");
        var inLoop = new HashSet<NavPoint>(b.Loop, ReferenceEqualityComparer.Instance);
        int lead = b.Route.Points.Count(p => !inLoop.Contains(p));
        if (lead > 0 || b.Evacuated || b.WalkOut != null)
            leftOut.Add($"the walk from where you stand onto the lap ({Math.Max(lead, 1)} point(s): {(b.Evacuated ? "out of lava/acid" : b.WalkOut != null ? "out of a portal's reach" : "round a portal")}) isn't saved: only needed from here");
        if (w.Geo == null) notes.Add("the dungeon map isn't loaded yet: a cell-centre patrol (save it again in a moment for one lined up on doorways)");
        DungeonRouteMaker.AddKnownNotes(w, notes);

        double yards = DungeonRouteMaker.RouteYards(route);
        Host.Log($"[RynthAi] DunPatrol-Save: {path}: {route.Points.Count} points, {yards:F0} yd a lap, from {b.StartCell:X8}; {b.Options.Describe()}");
        string detail = $"{(overwrote ? "replaced" : "saved")} {r.Name}.nav: the circular patrol dunnav-patrol walks from here, {route.Points.Count} points, {yards:0} yd a lap ({b.Options.Describe()}). Run it: /ra nav load {r.Name}";
        string json = DungeonRouteMaker.Json(j =>
        {
            j.WriteStartObject();
            j.WriteString("file", path);
            j.WriteString("name", r.Name);
            DungeonRouteMaker.WriteCounts(j, route, "circular");
            j.WriteString("landblock", DungeonRouteMaker.Hex(w.Landblock, 4));
            j.WritePropertyName("from");
            DungeonRouteMaker.WriteSpot(j, w, w.PlayerCell, w.X, w.Y, w.Z);
            j.WriteString("startCell", DungeonRouteMaker.Hex(b.StartCell, 8));
            j.WriteNumber("cells", cells);
            j.WriteNumber("reachable", reachable);
            j.WriteNumber("covered", covered);
            j.WriteNumber("doorwayPoints", DungeonRouteMaker.DoorwayPoints(route));
            j.WriteBoolean("overwrote", overwrote);
            j.WriteString("coverage", b.Options.Describe());
            j.WriteStartArray("leftOut");
            foreach (string s in leftOut) j.WriteStringValue(s);
            j.WriteEndArray();
            j.WriteStartArray("notes");
            foreach (string s in notes) j.WriteStringValue(s);
            j.WriteEndArray();
            j.WriteEndObject();
        });
        return new DunRouteOutcome(true, false, detail, json, Under(leftOut, notes));
    }

    private DunRouteOutcome RunDunInfo(DunRouteRequest r)
    {
        string path = DungeonRouteMaker.PathFor(r.Name);
        if (!File.Exists(path)) return DunRouteOutcome.Fail($"no route named '{r.Name}' in NavProfiles (/ra nav list shows them)");
        string? warning = null;
        var route = NavRouteParser.Load(path, s => warning = s);
        if (route.LoadWarning != null) warning = route.LoadWarning;

        uint playerCell = Host.TryGetPlayerPose(out uint pc, out _, out _, out _, out _, out _, out _, out _) ? pc : 0;
        uint playerLb = playerCell >> 16;
        bool inCell = (playerCell & 0xFFFF) >= 0x0100;
        var cellDat = DungeonMapOverride == null ? _raycast?.GeometryLoader?.CellDat : null;
        bool datOk = cellDat != null && cellDat.IsLoaded;
        var dungeonOf = new Dictionary<uint, bool?>();
        bool? IsDungeon(uint lb)
        {
            if (lb == playerLb && inCell) return true;   // the character is in one of its cells
            if (dungeonOf.TryGetValue(lb, out bool? known)) return known;
            bool? v = DungeonMapOverride != null ? DungeonMapOverride(lb) != null
                    : datOk ? NavRecoveryPlanner.ComputeIsDungeon(lb, cellDat!) : null;
            return dungeonOf[lb] = v;
        }
        Dictionary<uint, DungeonNavNode>? GraphOf(uint lb)
            => DungeonMapOverride != null ? DungeonMapOverride(lb)?.Graph : datOk ? DungeonPathfinder.GraphOf(lb, cellDat!) : null;

        var info = DungeonRouteMaker.DescribeRoute(r.Name, path, route, playerCell, GraphOf, IsDungeon);
        if (warning != null) info.Notes.Insert(0, warning);
        var inv = CultureInfo.InvariantCulture;
        string where = info.Landblock is uint rl
            ? $"landblock {rl:X4}" + (info.Dungeon == true ? " (a dungeon)" : info.Dungeon == false ? " (not a dungeon)" : "")
            : "no points";
        string mine = info.ForThisDungeon switch
        {
            "yes"    => "made for this dungeon: yes",
            "partly" => $"made for this dungeon: partly ({info.PointsHere} of {info.Waypoints} points are in it)",
            "no"     => "made for this dungeon: no",
            _        => "made for this dungeon: can't tell (you aren't in one)",
        };
        string detail = $"{r.Name}.nav: {DungeonRouteMaker.TypeName(info.Type)}, {info.Points} points ({info.Waypoints} walked), {info.Yards.ToString("0", inv)} yd, {where}"
                        + (info.Cells is int c ? $", {c} cells" + (info.StartCell != 0 ? $" ({info.StartCell:X8} to {info.EndCell:X8})" : "") : "")
                        + $"; {mine}";
        var lines = new List<string>();
        var steps = info.Steps.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}").ToList();
        if (steps.Count > 0) lines.Add("steps: " + string.Join(", ", steps));
        if (info.Landblocks.Count > 1) lines.Add("its points fall in landblocks " + string.Join(", ", info.Landblocks.Select(l => l.ToString("X4", inv))));
        if (info.Notes.Count > 0) lines.Add("note: " + string.Join("; ", info.Notes));
        return new DunRouteOutcome(true, false, detail, DungeonRouteMaker.InfoJson(info), lines);
    }

    private DunRouteOutcome RunDunWhere()
    {
        if (!Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _))
            return DunRouteOutcome.Waiting("Cannot get player position.");
        uint lb = cell >> 16;
        NavCoordinateHelper.TryConvertPoseToCoords(cell, x, y, out double ns, out double ew);
        var inv = CultureInfo.InvariantCulture;
        if ((cell & 0xFFFF) < 0x0100)
        {
            string outside = DungeonRouteMaker.Json(j =>
            {
                j.WriteStartObject();
                j.WriteBoolean("inDungeon", false);
                j.WriteString("landblock", DungeonRouteMaker.Hex(lb, 4));
                j.WriteString("cell", DungeonRouteMaker.Hex(cell, 8));
                j.WriteNumber("x", Math.Round(x, 2)); j.WriteNumber("y", Math.Round(y, 2)); j.WriteNumber("z", Math.Round(z, 2));
                j.WriteNumber("ns", Math.Round(ns, 4)); j.WriteNumber("ew", Math.Round(ew, 4));
                j.WriteEndObject();
            });
            return new DunRouteOutcome(true, false, string.Format(inv, "not in a dungeon: landblock {0:X4}, cell {1:X8} at ({2:0.0}, {3:0.0}, {4:0.0})", lb, cell, x, y, z), outside, Array.Empty<string>());
        }

        var w = TryGetDunWorld("dunroute where", "DunRoute", out _, out string error, out var refusal);
        if (w == null) return refusal == DunBuildRefusal.NotReady ? DunRouteOutcome.Waiting(error) : DunRouteOutcome.Fail(error);
        FillDunWorldExtras(w);
        int reachable = DungeonRouteMaker.ReachableCount(w.Graph, w.StartCell, w.Hazards);
        DungeonRooms? rooms = w.Geo != null ? DungeonPathfinder.BuildRooms(w.Graph, w.Geo) : null;
        int room = rooms != null && rooms.RoomOf.TryGetValue(w.StartCell, out int rr) ? rr : -1;
        var portals = DungeonRouteMaker.PortalsWhere(w);
        int hz = w.HazardsHere;

        string json = DungeonRouteMaker.Json(j =>
        {
            j.WriteStartObject();
            j.WriteBoolean("inDungeon", true);
            j.WriteString("landblock", DungeonRouteMaker.Hex(lb, 4));
            j.WriteString("cell", DungeonRouteMaker.Hex(cell, 8));
            j.WriteNumber("x", Math.Round(x, 2)); j.WriteNumber("y", Math.Round(y, 2)); j.WriteNumber("z", Math.Round(z, 2));
            j.WriteNumber("ns", Math.Round(ns, 4)); j.WriteNumber("ew", Math.Round(ew, 4));
            j.WriteNumber("cells", w.Graph.Count);
            j.WriteNumber("reachable", reachable);
            j.WriteBoolean("map", w.Geo != null);
            if (rooms != null) j.WriteNumber("rooms", rooms.Cells.Count); else j.WriteNull("rooms");
            if (room >= 0) j.WriteNumber("room", room); else j.WriteNull("room");
            if (w.Explored is double e) j.WriteNumber("explored", Math.Round(e, 3)); else j.WriteNull("explored");
            j.WriteNumber("hazards", hz);
            if (w.Arrival is { } a)
            {
                j.WritePropertyName("arrival");
                DungeonRouteMaker.WriteSpot(j, w, a.Cell, a.X, a.Y, a.Z);
            }
            else j.WriteNull("arrival");
            j.WriteStartArray("portals");
            foreach (var p in portals)
            {
                j.WriteStartObject();
                j.WriteString("name", p.Portal.Name);
                j.WriteString("id", DungeonRouteMaker.Hex(p.Portal.Id, 8));
                j.WriteString("cell", DungeonRouteMaker.Hex(p.Portal.Cell, 8));
                j.WriteNumber("x", Math.Round(p.Portal.X - w.OriginX, 2));
                j.WriteNumber("y", Math.Round(p.Portal.Y - w.OriginY, 2));
                j.WriteNumber("z", Math.Round(p.Portal.Z, 2));
                j.WriteNumber("yards", Math.Round(p.Yards, 1));
                if (p.WalkYards is double wy) j.WriteNumber("walkYards", Math.Round(wy, 1)); else j.WriteNull("walkYards");
                if (p.Exit != null) j.WriteString("exit", p.Exit); else j.WriteNull("exit");
                if (p.Destination != null) j.WriteString("destination", p.Destination); else j.WriteNull("destination");
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteEndObject();
        });

        string detail = string.Format(inv, "in dungeon {0:X4}, cell {1:X8} at ({2:0.0}, {3:0.0}, {4:0.0}) (landblock-local); {5} cells, {6} walkable from here",
                            lb, cell, x, y, z, w.Graph.Count, reachable)
                        + (rooms != null ? $"; {rooms.Cells.Count} rooms, you're in room {room}" : "; the dungeon map isn't loaded yet (no rooms)")
                        + (w.Explored is double ex ? string.Format(inv, "; {0:0}% explored", ex * 100) : "")
                        + (hz > 0 ? $"; {hz} lava/acid cell(s) known" : "");
        var lines = new List<string>();
        if (portals.Count == 0) lines.Add("portals: none known here yet (the client only knows the ones it has been sent)");
        foreach (var p in portals.Take(10))
            lines.Add($"portal '{p.Portal.Name}': {p.Yards:0} yd"
                      + (p.WalkYards is double wy ? $" ({wy:0} yd on foot)" : " (no walkable way)")
                      + (p.Exit != null ? $"; exit: {p.Exit}" : "")
                      + (p.Destination != null ? $"; goes to {p.Destination}" : ""));
        if (portals.Count > 10) lines.Add($"... and {portals.Count - 10} more");
        lines.Add(w.Arrival is { } arr
            ? string.Format(inv, "you came in at cell {0:X8} ({1:0.0}, {2:0.0}, {3:0.0})", arr.Cell, arr.X - w.OriginX, arr.Y - w.OriginY, arr.Z)
            : "RynthAi didn't see you come in (it was loaded after): the exit is picked by its name");
        return new DunRouteOutcome(true, false, detail, json, lines);
    }

    private static List<string> Under(List<string> leftOut, List<string> notes)
    {
        var lines = new List<string>();
        if (leftOut.Count > 0) lines.Add("left out: " + string.Join("; ", leftOut));
        if (notes.Count > 0) lines.Add("note: " + string.Join("; ", notes));
        return lines;
    }

    // ── The chat commands ────────────────────────────────────────────────────

    /// <summary>/ra dunroute &lt;name&gt; to &lt;target&gt; [overwrite] | info &lt;name&gt; | where.</summary>
    private void HandleDunRouteCommand(string[] parts)
    {
        if (parts.Length < 3)
        {
            ChatLine(DunRouteUsage);
            ChatLine("[RynthAi]   targets: exit (the exit portal), far (the farthest spot on foot), portal <name>, <x> <y> <z> (landblock-local metres), room <n>");
            return;
        }
        string sub = parts[2].ToLowerInvariant();
        if (sub == "where" && parts.Length == 3)
        {
            RunDunRouteForChat(new DunRouteRequest { Op = DunRouteOp.Where });
            return;
        }
        if (sub == "info" && (parts.Length < 4 || !parts[3].Equals("to", StringComparison.OrdinalIgnoreCase)))
        {
            if (parts.Length < 4) { ReplyFail("[RynthAi] Usage: /ra dunroute info <name>  (a nav in NavProfiles)"); return; }
            if (!DunRouteRequest.TryName(string.Join(" ", parts, 3, parts.Length - 3), out string name, out string nameError))
            { ReplyFail($"[RynthAi] DunRoute: {nameError}."); return; }
            RunDunRouteForChat(new DunRouteRequest { Op = DunRouteOp.Info, Name = name });
            return;
        }
        if (!TryParseDunRouteWords(parts.Skip(2).ToList(), out var request, out string error))
        {
            ReplyFail($"[RynthAi] DunRoute: {error}. {DunRouteUsage[10..]}");
            return;
        }
        RunDunRouteForChat(request);
    }

    /// <summary>/ra dunpatrol-save &lt;name&gt; [overwrite].</summary>
    private void HandleDunPatrolSaveCommand(string[] parts)
    {
        var words = parts.Skip(2).ToList();
        bool overwrite = words.Count > 0 && words[^1].Equals("overwrite", StringComparison.OrdinalIgnoreCase);
        if (overwrite) words.RemoveAt(words.Count - 1);
        if (words.Count == 0) { ReplyFail(DunPatrolSaveUsage); return; }
        if (!DunRouteRequest.TryName(string.Join(" ", words), out string name, out string error))
        {
            ReplyFail($"[RynthAi] DunPatrol-Save: {error}. {DunPatrolSaveUsage[10..]}");
            return;
        }
        RunDunRouteForChat(new DunRouteRequest { Op = DunRouteOp.DunPatrol, Name = name, Overwrite = overwrite });
    }

    /// <summary>
    /// The words after "/ra dunroute": &lt;name&gt; to &lt;target&gt; [overwrite]. A name may hold "to"
    /// itself ("Way to the hub to exit"): each "to" is tried from the left, and the first split whose
    /// name and target both work is taken. False with the reason.
    /// </summary>
    internal static bool TryParseDunRouteWords(IReadOnlyList<string> raw, out DunRouteRequest request, out string error)
    {
        request = new DunRouteRequest { Op = DunRouteOp.DunRoute };
        error = "";
        var words = raw.ToList();
        if (words.Count > 0 && words[^1].Equals("overwrite", StringComparison.OrdinalIgnoreCase))
        {
            request.Overwrite = true;
            words.RemoveAt(words.Count - 1);
        }
        string? firstError = null;
        for (int i = 1; i < words.Count; i++)
        {
            if (!words[i].Equals("to", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DunRouteRequest.TryName(string.Join(" ", words.Take(i)), out string name, out string nameError)) { firstError ??= nameError; continue; }
            if (!DunTarget.TryParseWords(words.Skip(i + 1).ToList(), out var target, out string targetError)) { firstError ??= targetError; continue; }
            request.Name = name;
            request.To = target;
            return true;
        }
        error = firstError ?? (words.Count == 0 ? "no name given" : "no \"to\": name the route, then where it goes");
        return false;
    }

    /// <summary>A request run for a chat command: one line, and the left-out / notes lines under it.</summary>
    private void RunDunRouteForChat(DunRouteRequest r)
    {
        string label = r.Op == DunRouteOp.DunPatrol ? "DunPatrol-Save" : "DunRoute";
        var o = RunDunRouteRequest(r);
        if (!o.Ok)
        {
            // A job waits for the client and tries again; a typed command says so instead.
            ReplyFail($"[RynthAi] {label}: {o.Detail}" + (o.Wait ? " (try again in a moment)" : ""));
            return;
        }
        bool query = r.Op is DunRouteOp.Info or DunRouteOp.Where;
        if (query) ChatLine($"[RynthAi] {label}: {o.Detail}");
        else ReplyOk($"[RynthAi] {label}: {o.Detail}");
        foreach (string line in o.Lines)
        {
            if (query) ChatLine($"[RynthAi]   {line}");
            else ReplyOk($"[RynthAi]   {line}");
        }
    }
}
