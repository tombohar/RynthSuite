using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// The route maker (2026-10-30): RynthAi nav routes made on demand, on real walkable paths, for the
// player and for another plugin (the "RynthNav.Routes" interface, Shared/RynthNavRoutesApi.cs, which
// has the JSON). One implementation for both: a /rnav command and an interface Request each become a
// RouteRequest and then a job in the same table, run here.
//   /rnav makeroute <name> to <place|coords> [overwrite]       = route, then saveroute, as one job
//   /rnav makeloop <name> [at <coords>|here] radius <yd> [points <n>] [overwrite]   a hunting loop (LoopMaker)
//   /rnav makepath <name> [loop|line|once] <coords>; <coords>; ... [overwrite]       a nav through points
//   /rnav routeinfo <name>                                                        what a saved nav is (RouteInfo)
// and for the interface also plan, atlas, go, stop and where.
//
// Threads, as the trip path for the markers and "save as nav route" (RynthNavPlugin.Markers.cs): the
// tick plans (TravelPlan, the recalls, the switches, the pose) and freezes what a worker needs; the
// worker works the walks out in a TilePathFinder of its own (never the walker's navmesh), writes the
// file (NavRouteExport.Save: a temp file, then moved in) and hands a RouteWork back; the tick takes it
// on a later tick and finishes the job (and answers in chat when it was typed). One worker at a time;
// atlas, where, stop and go are quick and run on the tick as soon as they come. The interface's
// Request and Poll come on the plugin pump thread, the tick's own; the job table is locked anyway.
// Names follow saveroute's rules (NavRouteExport.FileName), into the same folder (RynthAi's
// NavProfiles), and a file that's there is never replaced unless asked. RynthAi re-lists NavProfiles
// when the folder changes (every 3 s, LegacyDashboardRenderer.TickNavFolder), and /ra nav list and
// /ra nav load list it again, so a new route shows without a restart.
public sealed partial class RynthNavPlugin
{
    private const long RouteWorkTimeoutMs = 180000;    // a worker job still going after this has failed (its late result is dropped)
    private const double LoopLivePortalUnits = 300.0;   // live portals this far past a loop's radius are kept away from too

    private RouteJobTable? _routeJobs;
    private RouteJobTable RouteJobs => _routeJobs ??= new RouteJobTable(FirstJobId());
    // The one worker job (tick thread).
    private RouteJob? _workJob;
    private Task<RouteWork>? _workTask;
    private long _workStartMs;
    private bool _workTimedOut;
    // Building outlines (NavData\worldmap.vec), read by the first loop's worker.
    private BuildingOutlines? _buildings;
    private bool _buildingsTried;
    // The goto a go job started (tick thread): its job, and how the last goto ended (FinishGoto).
    private int _goJobId;
    private string _gotoEndStatus = "";
    private int _gotoEndSeq;
    private readonly Dictionary<int, GoInfo> _goInfo = new();

    /// <summary>Job ids start from the clock (seconds since boot), so a RynthNav reload doesn't hand a caller's old ids to new jobs.</summary>
    private static int FirstJobId() => (int)(Environment.TickCount64 / 1000 % 2_000_000) * 1000 + 1;

    /// <summary>What a worker hands back to the tick.</summary>
    private sealed class RouteWork
    {
        public bool Ok;
        public string Detail = "";
        public string Json = "null";
        /// <summary>The chat answer (a job typed in chat): the first line is the result.</summary>
        public readonly List<string> Lines = new();
        public readonly List<string> Log = new();
        /// <summary>A plan's or makeroute's trip and its path (the markers show it when it's still the trip shown).</summary>
        public PlannedTrip? Trip;
        public TripPath? Path;
        /// <summary>A loop's worker read the building outlines: kept for the next one.</summary>
        public bool BuildingsRead;
        public BuildingOutlines? Buildings;
    }

    private sealed class GoInfo
    {
        public string Name = "", Enter = "";
        public double Ns, Ew;
        public int Steps, Teleports;
    }

    private static bool IsWorkerOp(RouteOp op) => op is RouteOp.Plan or RouteOp.MakeRoute or RouteOp.MakeLoop or RouteOp.MakePath or RouteOp.Info;

    // ── In: chat and the interface ───────────────────────────────────────

    /// <summary>/rnav makeroute | makeloop | makepath | routeinfo: queued; the answer comes when the job is done. Tick thread.</summary>
    private void CmdRouteMaker(string cmd, string rest)
    {
        RouteOp op = cmd switch { "makeroute" => RouteOp.MakeRoute, "makeloop" => RouteOp.MakeLoop, "makepath" => RouteOp.MakePath, _ => RouteOp.Info };
        RouteRequest? req = RouteRequest.FromCommand(cmd, rest, out string error);
        if (req == null) { SayFail($"{cmd}: {error} — {RouteRequest.Usage(op, chat: true)}"); return; }
        RouteJob? job = RouteJobs.Add(req, NowMs, out error);
        if (job == null) { SayFail($"{cmd}: {error}"); return; }
        Host.Log($"[RynthNav] job {job.Id} {cmd} queued (chat)");
        // It starts on this tick unless another route is being worked out: then say it waits.
        if (_workTask != null) Say($"{cmd}: queued; another route is being worked out first ({RouteJobs.Queued().Count} waiting)");
    }

    /// <summary>"RynthNav.Routes" Request: a job id (and its JSON), or 0 and {error, usage}. Plugin pump thread.</summary>
    internal string RoutesRequest(string json, out int id)
    {
        id = 0;
        RouteRequest? req = RouteRequest.FromJson(json, out string error);
        if (req == null)
        {
            Host.Log($"[RynthNav] routes request refused: {error}");
            return RouteJson.Error(error, RouteRequest.UsageFor(json));
        }
        RouteJob? job = RouteJobs.Add(req, NowMs, out error);
        if (job == null) return RouteJson.Error(error, "");
        id = job.Id;
        Host.Log($"[RynthNav] job {job.Id} {RouteRequest.OpName(req.Op)} queued (interface)");
        return RouteJobs.Accepted(job);
    }

    /// <summary>"RynthNav.Routes" Poll. Any thread.</summary>
    internal string RoutesPoll(int id, out bool known) => RouteJobs.PollJson(id, out known);

    // ── The tick ─────────────────────────────────────────────────────────

    private void TickRouteJobs()
    {
        if (_routeJobs == null) return;
        long now = NowMs;
        TakeRouteWork(now);
        foreach (RouteJob j in _routeJobs.Working())
            if (j.Request.Op == RouteOp.Go) TickGoJob(j, now);
        foreach (RouteJob j in _routeJobs.Queued())
        {
            if (!IsWorkerOp(j.Request.Op)) { RunNow(j, now); continue; }
            if (_workTask == null) StartWork(j, now);
        }
    }

    /// <summary>A job is over: the table, the log, and the chat when it was typed.</summary>
    private void EndJob(RouteJob j, bool ok, string detail, string? json, long now, IReadOnlyList<string>? lines = null)
    {
        RouteJobs.Finish(j, ok, detail, json, now);
        Host.Log($"[RynthNav] job {j.Id} {RouteRequest.OpName(j.Request.Op)}: {(ok ? "done" : "failed")}: {detail} ({now - j.CreatedMs} ms)");
        if (!j.Request.FromChat) return;
        string cmd = j.Request.Command;
        if (lines != null && lines.Count > 0)
        {
            if (ok) Say(lines[0]); else SayFail($"{lines[0]} — {RouteRequest.Usage(j.Request.Op, chat: true)}");
            for (int i = 1; i < lines.Count; i++) Say(lines[i]);
        }
        else if (ok) Say($"{cmd}: {detail}");
        else SayFail($"{cmd}: {detail} — {RouteRequest.Usage(j.Request.Op, chat: true)}");
    }

    private void FailJob(RouteJob j, string why, long now) => EndJob(j, false, why, RouteJson.Error(why, ""), now);

    private void TakeRouteWork(long now)
    {
        if (_workTask == null || _workJob == null) return;
        if (!_workTask.IsCompleted)
        {
            if (!_workTimedOut && now - _workStartMs > RouteWorkTimeoutMs)
            {
                _workTimedOut = true;
                FailJob(_workJob, $"still working after {RouteWorkTimeoutMs / 1000} s: given up (a late result is dropped)", now);
            }
            return;
        }
        Task<RouteWork> task = _workTask;
        RouteJob job = _workJob;
        bool late = _workTimedOut;
        _workTask = null; _workJob = null; _workTimedOut = false;
        RouteWork w;
        if (task.IsFaulted)
        {
            string why = task.Exception?.GetBaseException().Message ?? "unknown";
            w = new RouteWork { Ok = false, Detail = "failed: " + why, Json = RouteJson.Error("failed: " + why, "") };
            w.Lines.Add($"{job.Request.Command}: failed ({why}); see the log");
        }
        else w = task.Result;
        foreach (string line in w.Log) Host.Log(line);
        if (w.BuildingsRead)
        {
            _buildings = w.Buildings; _buildingsTried = true;
            Host.Log(w.Buildings != null ? $"[RynthNav] building outlines: {w.Buildings.Count} from {BuildingOutlines.FileName}"
                                         : $"[RynthNav] building outlines: no {BuildingOutlines.FileName} in {NavDataDir}");
        }
        // A plan's path is the shown trip's too (makeroute typed in chat shows it): the markers draw it.
        if (w.Trip != null && w.Path != null && ReferenceEquals(_trip, w.Trip) && (_tripPath == null || _tripPath.TripId != w.Trip.Id))
        {
            _tripPath = w.Path;
            int points = 0;
            foreach (WorldPt[]? p in w.Path.Walks) points += p?.Length ?? 0;
            _markerStatus = $"{points} waypoints";
            OnPathReady(_trip, w.Path);
        }
        if (late) return;
        EndJob(job, w.Ok, w.Detail, w.Json, now, w.Lines);
    }

    // ── Quick ops (the tick) ─────────────────────────────────────────────

    private void RunNow(RouteJob j, long now)
    {
        RouteRequest q = j.Request;
        switch (q.Op)
        {
            case RouteOp.Atlas:
            {
                Atlas atlas = _atlas;
                if (atlas.Locations.Count == 0) { FailJob(j, "the Atlas isn't loaded (" + _atlasStatus + ")", now); return; }
                bool hasPos = PlayerMapPos(out double pns, out double pew);
                List<AtlasLocation> hits = atlas.Search(q.Search, null, q.Max);
                string json = RouteJson.Write(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("search", q.Search);
                    w.WriteNumber("count", hits.Count);
                    w.WriteStartArray("places");
                    foreach (AtlasLocation l in hits)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", l.Name);
                        w.WriteString("kind", l.Type);
                        w.WriteBoolean("onMap", l.OnMap);
                        RouteJson.Num(w, "ns", l.OnMap ? l.Ns : double.NaN, 4);
                        RouteJson.Num(w, "ew", l.OnMap ? l.Ew : double.NaN, 4);
                        RouteJson.Num(w, "yards", l.OnMap && hasPos ? NavCoords.Distance(pns, pew, l.Ns, l.Ew) : double.NaN, 0);
                        if (!l.OnMap) w.WriteString("where", l.Desc.Length > 0 ? l.Desc : "in a dungeon");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
                EndJob(j, true, hits.Count == 0 ? $"no place matches \"{q.Search}\"" : $"{hits.Count} place(s)", json, now);
                return;
            }
            case RouteOp.Where:
            {
                if (!_hasPose) { FailJob(j, "no player position yet", now); return; }
                bool onMap = PlayerMapPos(out double ns, out double ew);
                double nearYd = double.NaN;
                AtlasLocation? near = onMap ? NearestPlace(ns, ew, out nearYd) : null;
                bool dungeon = InDungeon(_cellId), indoors = (_cellId & 0xFFFF) >= 0x100;
                string json = RouteJson.Write(w =>
                {
                    w.WriteStartObject();
                    w.WriteBoolean("onMap", onMap);
                    RouteJson.Num(w, "ns", onMap ? ns : double.NaN, 4);
                    RouteJson.Num(w, "ew", onMap ? ew : double.NaN, 4);
                    w.WriteString("landblock", CurrentLandblock.ToString("X4", CultureInfo.InvariantCulture));
                    w.WriteString("cell", _cellId.ToString("X8", CultureInfo.InvariantCulture));
                    w.WriteBoolean("indoors", indoors);
                    w.WriteBoolean("dungeon", dungeon);
                    w.WriteBoolean("walking", _gotoActive);
                    if (near != null)
                    {
                        w.WriteStartObject("nearest");
                        w.WriteString("name", near.Name);
                        w.WriteString("kind", near.Type);
                        RouteJson.Num(w, "ns", near.Ns, 4);
                        RouteJson.Num(w, "ew", near.Ew, 4);
                        RouteJson.Num(w, "yards", nearYd, 0);
                        w.WriteEndObject();
                    }
                    else w.WriteNull("nearest");
                    w.WriteEndObject();
                });
                string at = onMap ? NavCoords.Fmt(ns, ew) : dungeon ? "in a dungeon" : "off the map";
                EndJob(j, true, $"{at}, landblock 0x{CurrentLandblock:X4}{(near != null ? $", {NavCoords.FmtDistance(nearYd)} from {near.Name}" : "")}", json, now);
                return;
            }
            case RouteOp.Stop:
            {
                bool was = _gotoActive;
                DoMove(5);   // as /rnav stop: the walk ends on the next tick
                EndJob(j, true, was ? "stopping the walk" : "nothing was walking",
                    RouteJson.Write(w => { w.WriteStartObject(); w.WriteBoolean("wasWalking", was); w.WriteEndObject(); }), now);
                return;
            }
            case RouteOp.Go:
                StartGoJob(j, now);
                return;
        }
    }

    /// <summary>The Atlas place on the map nearest (ns, ew), and how far.</summary>
    private AtlasLocation? NearestPlace(double ns, double ew, out double yards)
    {
        AtlasLocation? best = null;
        yards = double.MaxValue;
        foreach (AtlasLocation l in _atlas.Locations)
        {
            if (!l.OnMap) continue;
            double d = NavCoords.Distance(ns, ew, l.Ns, l.Ew);
            if (d < yards) { yards = d; best = l; }
        }
        return best;
    }

    // ── go: walk there as /rnav go does, and follow the walk to its end ──

    private void StartGoJob(RouteJob j, long now)
    {
        if (!_hasPose) { FailJob(j, "no player position yet", now); return; }
        if (!ResolveGo(j.Request.To, out string name, out string type, out double ns, out double ew, out string enter, out string enterType, out string error))
        {
            EndJob(j, false, error, GoJson(null, false, "failed", error), now);
            return;
        }
        _store.Touch(new SavedPlace { Name = name, Type = type, Ns = ns, Ew = ew });
        _goNote = "";
        StartWalk(name, type, ns, ew, enter, enterType);   // says "walking to ..." in chat, or why it can't
        if (!_gotoActive)
        {
            string why = _goNote.Length > 0 ? _goNote : "the walk didn't start";
            EndJob(j, false, why, GoJson(null, false, "failed", why), now);
            return;
        }
        _goJobId = j.Id;
        j.Seq = _gotoEndSeq;
        int teleports = 0;
        if (_route != null) foreach (RouteStep s in _route) if (s.UsePortal || s.UseRecall || s.IsHub) teleports++;
        var info = new GoInfo { Name = enter.Length > 0 ? enter : name, Enter = enter, Ns = ns, Ew = ew, Steps = _route?.Count ?? 1, Teleports = teleports };
        _goInfo[j.Id] = info;
        string line; lock (_gate) line = _goLine;
        RouteJobs.Start(j, line.Length > 0 ? line : $"walking to {info.Name}", now);
        RouteJobs.Update(j, j.Detail, GoJson(info, true, "walking", ""));
    }

    private void TickGoJob(RouteJob j, long now)
    {
        _goInfo.TryGetValue(j.Id, out GoInfo? info);
        if (_goJobId != j.Id)
        {
            EndGo(j, info, true, "stopped", "stopped: another goto took over", now);
            return;
        }
        if (_gotoActive)
        {
            string line; lock (_gate) line = _goLine;
            if (line.Length > 0 && line != j.Detail) RouteJobs.Update(j, line);
            return;
        }
        string status = _gotoEndSeq != j.Seq ? _gotoEndStatus
            : !_hasPose ? "stopped: logged out"
            : _goNote.Length > 0 ? _goNote : "stopped: the walk ended";
        _goJobId = 0;
        bool ok = RouteJobTable.GoOutcome(status, out string outcome);
        EndGo(j, info, ok, outcome, status, now);
    }

    private void EndGo(RouteJob j, GoInfo? info, bool ok, string outcome, string status, long now)
    {
        _goInfo.Remove(j.Id);
        EndJob(j, ok, status, GoJson(info, true, outcome, status), now);
    }

    private static string GoJson(GoInfo? info, bool started, string outcome, string reason) => RouteJson.Write(w =>
    {
        w.WriteStartObject();
        w.WriteBoolean("started", started);
        if (info != null)
        {
            w.WriteStartObject("to");
            w.WriteString("name", info.Name);
            RouteJson.Num(w, "ns", info.Ns, 4);
            RouteJson.Num(w, "ew", info.Ew, 4);
            w.WriteEndObject();
            if (info.Enter.Length > 0) w.WriteString("enter", info.Enter); else w.WriteNull("enter");
            w.WriteNumber("steps", info.Steps);
            w.WriteNumber("teleports", info.Teleports);
        }
        w.WriteString("outcome", outcome);
        if (reason.Length > 0) w.WriteString("reason", reason);
        w.WriteEndObject();
    });

    // ── Worker ops: what the tick freezes, then the worker ──────────────

    private void StartWork(RouteJob j, long now)
    {
        RouteRequest q = j.Request;
        Func<RouteWork>? work = q.Op switch
        {
            RouteOp.Plan or RouteOp.MakeRoute => PrepareTrip(j, now),
            RouteOp.MakeLoop => PrepareLoop(j, now),
            RouteOp.MakePath => PreparePath(j, now),
            _ => PrepareInfo(j),
        };
        if (work == null) return;   // refused on the tick (already finished)
        _workJob = j;
        _workStartMs = now;
        _workTimedOut = false;
        _workTask = Task.Run(work);
    }

    /// <summary>A make op's name that is taken, unless overwrite was asked: refused before any work.</summary>
    private bool NameTaken(RouteJob j, long now)
    {
        RouteRequest q = j.Request;
        if (q.Overwrite || !File.Exists(Path.Combine(NavProfilesDir, q.Name + ".nav"))) return false;
        FailJob(j, $"{q.Name}.nav is already in RynthAi's NavProfiles; ask again with overwrite to replace it", now);
        return true;
    }

    /// <summary>The portal objects the client sees within <paramref name="range"/> of (x, y), as avoid circles. Tick thread.</summary>
    private List<PortalAvoid.Spot> LivePortalsNear(double x, double y, double range)
    {
        var list = new List<PortalAvoid.Spot>();
        if (!Host.HasGetLiveObjectIds) return list;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!Host.TryGetItemType(id, out uint type) || (type & ItemTypePortal) == 0) continue;
            if (!Host.TryGetObjectPosition(id, out uint cell, out float ox, out float oy, out _) || cell == 0 || (cell & 0xFFFF) >= 0x100) continue;
            double wx = ((cell >> 24) & 0xFF) * 192.0 + ox, wy = ((cell >> 16) & 0xFF) * 192.0 + oy;
            if (Math.Abs(wx - x) > range || Math.Abs(wy - y) > range) continue;
            Host.TryGetObjectName(id, out string name);
            list.Add(new PortalAvoid.Spot(wx, wy, PortalAvoid.LiveRadius, name ?? ""));
        }
        return list;
    }

    // plan, makeroute: plan on the tick (as /rnav route), the trip's path and the file on the worker.
    private Func<RouteWork>? PrepareTrip(RouteJob j, long now)
    {
        RouteRequest q = j.Request;
        if (q.Op == RouteOp.MakeRoute && NameTaken(j, now)) return null;
        if (!_hasPose) { FailJob(j, "no player position yet", now); return null; }
        if (!ResolveTarget(q.To, out string name, out _, out double ns, out double ew, out string error)) { FailJob(j, error, now); return null; }
        TravelPlan.Result plan = PlanRoute(name, ns, ew, out List<RecallOption> recalls, q.Portals);
        if (plan.Refusal.Length > 0) { FailJob(j, $"no way to {name}: {plan.Refusal}", now); return null; }
        bool portalsOn = q.Portals ?? _portalsEnabled;
        PlannedTrip trip;
        if (q.Show || q.FromChat)
        {
            // As /rnav route: the panel shows the plan and the markers its path (worked out below, once).
            _shownRoute = plan.Steps ?? new List<RouteStep> { new(ns, ew, false, false, "walk") };
            _shownRouteFor = name; _shownRouteEst = plan.Est; _shownRouteLive = false; _routeVersion++;
            trip = SetTrip(plan.Steps, name, ns, ew, recalls, "", live: false, buildPath: false);
        }
        else trip = NewTrip(plan.Steps, name, ns, ew, recalls, "", live: false);
        if (q.FromChat) Say($"{q.Command}: working out the walk to {name}…");
        RouteJobs.Start(j, $"working out the walk to {name}", now);

        NavGraph? g = UsableGraph();
        IReadOnlyList<PortalAvoid.Spot> portals = AtlasPortalSpots();
        TownNet? net = _townNet;
        string dir = NavDataDir, folder = NavProfilesDir;
        int teleports = plan.Teleports;
        return () => TripWork(q, trip, g, portals, net, dir, folder, teleports, portalsOn);
    }

    private static RouteWork TripWork(RouteRequest q, PlannedTrip trip, NavGraph? g, IReadOnlyList<PortalAvoid.Spot> portals,
        TownNet? net, string dir, string folder, int teleports, bool portalsOn)
    {
        var o = new RouteWork { Trip = trip };
        long t0 = Environment.TickCount64;
        var finder = new TilePathFinder(dir, g, portals);
        TripPath path = TripPathBuilder.Build(trip, (WorldPt a, WorldPt b, bool intoPortal, out string note) => finder.Walk(a, b, intoPortal, out note), net);
        o.Path = path;
        o.Log.Add($"[RynthNav] job: the walk to {trip.Goal}: {trip.Count} step(s), {finder.TilesRead} tile(s) read, {Environment.TickCount64 - t0} ms");
        foreach (string n in path.Notes) o.Log.Add($"[RynthNav] job: trip path: {n}");
        TripSummary sum = TripSummary.Of(trip, path);

        if (q.Op == RouteOp.Plan)
        {
            o.Ok = true;
            o.Detail = $"to {trip.Goal}: {sum.Steps.Count} step(s), {NavCoords.FmtDistance(sum.Yards)} walking, {sum.Teleports} teleport(s), about {sum.Minutes:F1} min";
            o.Json = RouteJson.Write(w =>
            {
                w.WriteStartObject();
                w.WriteStartObject("to");
                w.WriteString("name", trip.Goal);
                RouteJson.Num(w, "ns", trip.GoalNs, 4);
                RouteJson.Num(w, "ew", trip.GoalEw, 4);
                w.WriteEndObject();
                w.WriteBoolean("portals", portalsOn);
                sum.WriteFields(w);
                RouteJson.Strings(w, "notes", path.Notes);
                w.WriteEndObject();
            });
            return o;
        }

        // makeroute: as saveroute (RynthNavPlugin.SaveRoute.cs).
        NavRouteExport.Result r = NavRouteExport.Convert(trip, path, net);
        if (r.Route.Points.Count == 0)
            return Failed(o, q, $"nothing to save: {(r.StopReason.Length > 0 ? r.StopReason : "the trip has no steps")}");
        if (!SaveNav(o, q, folder, r.Route, out string file)) return o;
        double yards = RouteInfo.Length(r.Route);
        o.Ok = true;
        o.Detail = $"saved {q.Name}.nav: {r.Route.Points.Count} steps, {NavCoords.FmtDistance(yards)} walking";
        string line = $"{q.Command}: saved {q.Name}.nav to {folder}: {r.Route.Points.Count} steps ({r.Waypoints} waypoints"
            + (r.Portals > 0 ? $", {r.Portals} portal(s)" : "") + (r.Recalls + r.Chats > 0 ? $", {r.Recalls + r.Chats} recall(s)" : "")
            + $"). RynthAi: pick it in Nav, or /ra nav load {q.Name}.";
        if (r.StoppedAt >= 0) line += $" It stops short of {trip.Goal}: {r.StopReason}.";
        if (path.Notes.Count > 0) line += $" Check it: {path.Notes[0]}{(path.Notes.Count > 1 ? $" (+{path.Notes.Count - 1} more in the log)" : "")}.";
        o.Lines.Add(line);
        o.Log.Add($"[RynthNav] {line} ({file})");
        o.Json = MakeJson(q, file, r.Route, yards, null, w =>
        {
            w.WriteStartObject("to");
            w.WriteString("name", trip.Goal);
            RouteJson.Num(w, "ns", trip.GoalNs, 4);
            RouteJson.Num(w, "ew", trip.GoalEw, 4);
            w.WriteEndObject();
            w.WriteNumber("waypoints", r.Waypoints);
            w.WriteNumber("portals", r.Portals);
            w.WriteNumber("recalls", r.Recalls + r.Chats);
            w.WriteNumber("teleports", teleports);
            if (r.StoppedAt >= 0) w.WriteString("stoppedShort", r.StopReason); else w.WriteNull("stoppedShort");
        }, path.Notes);
        return o;
    }

    // makeloop: the centre and the portals on the tick, the loop and the file on the worker.
    private Func<RouteWork>? PrepareLoop(RouteJob j, long now)
    {
        RouteRequest q = j.Request;
        if (NameTaken(j, now)) return null;
        WorldPt centre;
        if (q.Here)
        {
            if (!_hasPose) { FailJob(j, "no player position yet", now); return null; }
            if (InDungeon(_cellId) || !IsOnMap(_cellId)) { FailJob(j, "you're in a dungeon: loops are made on the landscape (give at <coords>)", now); return null; }
            centre = new WorldPt(_wx, _wy, _wz);
        }
        else centre = WorldPt.FromMap(q.AtNs, q.AtEw, double.NaN);
        if (centre.Landblock == uint.MaxValue) { FailJob(j, $"{NavCoords.Fmt(centre.Ns, centre.Ew)} is off the map", now); return null; }
        if (!File.Exists(Path.Combine(NavDataDir, $"nav_{centre.Landblock:X4}.tile")))
        { FailJob(j, $"no navmesh at {NavCoords.Fmt(centre.Ns, centre.Ew)} (landblock 0x{centre.Landblock:X4} has no tile in {NavDataDir})", now); return null; }

        IReadOnlyList<PortalAvoid.Spot> atlasPortals = AtlasPortalSpots();
        List<PortalAvoid.Spot> live = _hasPose ? LivePortalsNear(centre.X, centre.Y, q.Radius + LoopLivePortalUnits) : new List<PortalAvoid.Spot>();
        int points = q.Points > 0 ? q.Points : RouteShape.DefaultPoints(q.Radius);
        var opt = new LoopMaker.LoopOptions { Radius = q.Radius, Points = points, WallClearance = _avoidWalls ? _wallClearance : 0 };
        if (q.FromChat) Say($"{q.Command}: choosing {points} points round {NavCoords.Fmt(centre.Ns, centre.Ew)} within {q.Radius:F0} yd…");
        RouteJobs.Start(j, $"choosing {points} points round {NavCoords.Fmt(centre.Ns, centre.Ew)}", now);
        string dir = NavDataDir, folder = NavProfilesDir;
        bool tried = _buildingsTried;
        BuildingOutlines? buildings = _buildings;
        return () => LoopWork(q, centre, opt, atlasPortals, live, dir, folder, tried, buildings);
    }

    private static RouteWork LoopWork(RouteRequest q, WorldPt centre, LoopMaker.LoopOptions opt, IReadOnlyList<PortalAvoid.Spot> atlasPortals,
        List<PortalAvoid.Spot> live, string dir, string folder, bool buildingsTried, BuildingOutlines? buildings)
    {
        var o = new RouteWork();
        if (!buildingsTried)
        {
            buildings = BuildingOutlines.Load(Path.Combine(dir, BuildingOutlines.FileName));
            o.BuildingsRead = true; o.Buildings = buildings;
        }
        long t0 = Environment.TickCount64;
        var all = new List<PortalAvoid.Spot>(live);
        all.AddRange(atlasPortals);
        var finder = new TilePathFinder(dir, null, atlasPortals, live);
        LoopMaker.Result r = LoopMaker.Loop(finder, centre, opt, buildings, all);
        o.Log.Add($"[RynthNav] job: loop round {NavCoords.Fmt(centre.Ns, centre.Ew)}, {opt.Radius:F0} yd: {(r.Ok ? $"{r.Points.Count} of {r.Asked} points, {r.Yards:F0} yd" : r.Error)}; "
                + $"{finder.TilesRead} tile(s) read, {Environment.TickCount64 - t0} ms");
        foreach (LoopMaker.Dropped d in r.DroppedPoints) o.Log.Add($"[RynthNav] job: loop point at {NavCoords.Fmt(d.Ns, d.Ew)} dropped: {d.Why}");
        if (!r.Ok || r.Route == null) { Failed(o, q, r.Error); o.Json = MakeJson(q, "", null, 0, r.DroppedPoints, null, r.Notes, r.Error); return o; }
        if (!SaveNav(o, q, folder, r.Route, out string file)) return o;
        o.Ok = true;
        o.Detail = $"saved {q.Name}.nav: {r.Points.Count} points, {NavCoords.FmtDistance(r.Yards)} a lap"
                 + (r.DroppedPoints.Count > 0 ? $", {r.DroppedPoints.Count} dropped" : "");
        string line = $"{q.Command}: saved {q.Name}.nav (circular): {r.Points.Count} points round {NavCoords.Fmt(r.Centre.Ns, r.Centre.Ew)}, "
            + $"radius {opt.Radius:F0} yd, {NavCoords.FmtDistance(r.Yards)} a lap, {r.Route.Points.Count} waypoints"
            + (r.DroppedPoints.Count > 0 ? $"; {r.DroppedPoints.Count} point(s) dropped ({r.DroppedPoints[0].Why}{(r.DroppedPoints.Count > 1 ? ", ..." : "")})" : "")
            + $". RynthAi: /ra nav load {q.Name}.";
        o.Lines.Add(line);
        foreach (string n in r.Notes) o.Lines.Add($"  {q.Command}: {n}");
        o.Log.Add($"[RynthNav] {line} ({file})");
        o.Json = MakeJson(q, file, r.Route, r.Yards, r.DroppedPoints, w =>
        {
            RouteJson.Place(w, "centre", r.Centre.Ns, r.Centre.Ew);
            RouteJson.Num(w, "radius", opt.Radius, 1);
            w.WriteNumber("loopPoints", r.Points.Count);
            w.WriteNumber("asked", r.Asked);
        }, r.Notes);
        return o;
    }

    // makepath: the points and the portals on the tick, the legs and the file on the worker.
    private Func<RouteWork>? PreparePath(RouteJob j, long now)
    {
        RouteRequest q = j.Request;
        if (NameTaken(j, now)) return null;
        var pts = new List<WorldPt>(q.Path.Count);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (ns, ew) in q.Path)
        {
            WorldPt p = WorldPt.FromMap(ns, ew, double.NaN);
            pts.Add(p);
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        IReadOnlyList<PortalAvoid.Spot> atlasPortals = _avoidPortals ? AtlasPortalSpots() : Array.Empty<PortalAvoid.Spot>();
        var live = new List<PortalAvoid.Spot>();
        if (_avoidPortals && _hasPose)
            foreach (PortalAvoid.Spot s in LivePortalsNear((minX + maxX) / 2, (minY + maxY) / 2, Math.Max(maxX - minX, maxY - minY) / 2 + AvoidNearUnits))
                live.Add(s);
        bool strict = _avoidPortals;
        double wall = _avoidWalls ? _wallClearance : 0;
        NavGraph? g = UsableGraph();
        if (q.FromChat) Say($"{q.Command}: walking out {q.Path.Count} points…");
        RouteJobs.Start(j, $"working out {q.Path.Count} points", now);
        string dir = NavDataDir, folder = NavProfilesDir;
        return () => PathWork(q, pts, g, atlasPortals, live, strict, wall, dir, folder);
    }

    private static RouteWork PathWork(RouteRequest q, List<WorldPt> pts, NavGraph? g, IReadOnlyList<PortalAvoid.Spot> atlasPortals,
        List<PortalAvoid.Spot> live, bool strict, double wall, string dir, string folder)
    {
        var o = new RouteWork();
        long t0 = Environment.TickCount64;
        var finder = new TilePathFinder(dir, g, atlasPortals, live) { StrictPortals = strict, WallClearance = wall };
        var all = new List<PortalAvoid.Spot>(live);
        all.AddRange(atlasPortals);
        LoopMaker.Result r = LoopMaker.Path(finder, pts, q.PathType, TripPathBuilder.MaxSpacing / 2, all);
        o.Log.Add($"[RynthNav] job: path through {pts.Count} points ({RouteInfo.TypeName(q.PathType)}): {(r.Ok ? $"{r.Legs.Count} leg(s), {r.Yards:F0} yd" : r.Error)}; "
                + $"{finder.TilesRead} tile(s) read, {Environment.TickCount64 - t0} ms");
        if (!r.Ok || r.Route == null) { Failed(o, q, r.Error); return o; }
        if (!SaveNav(o, q, folder, r.Route, out string file)) return o;
        o.Ok = true;
        string type = RouteInfo.TypeName(q.PathType);
        o.Detail = $"saved {q.Name}.nav: {pts.Count} points, {r.Legs.Count} leg(s), {NavCoords.FmtDistance(r.Yards)}";
        string line = $"{q.Command}: saved {q.Name}.nav ({type}): {pts.Count} points, {r.Legs.Count} leg(s), {NavCoords.FmtDistance(r.Yards)}"
            + $"{(q.PathType == NavRouteType.Circular ? " a lap" : "")}, {r.Route.Points.Count} waypoints. RynthAi: /ra nav load {q.Name}.";
        o.Lines.Add(line);
        o.Log.Add($"[RynthNav] {line} ({file})");
        o.Json = MakeJson(q, file, r.Route, r.Yards, r.DroppedPoints, w =>
        {
            w.WriteNumber("given", pts.Count);
            w.WriteNumber("legs", r.Legs.Count);
        }, r.Notes);
        return o;
    }

    // info: the file on the worker; what a landblock is (dungeon or not) and the spell names from the tick.
    private Func<RouteWork>? PrepareInfo(RouteJob j)
    {
        RouteRequest q = j.Request;
        bool hasPlayer = PlayerMapPos(out double pns, out double pew);
        HashSet<uint> dungeons = _dungeonLandblocks, map = _mapLandblocks;
        var spells = new Dictionary<int, string>
        {
            [RecallPlanner.LifestoneRecall] = "Lifestone Recall", [RecallPlanner.PortalRecall] = "Portal Recall",
            [RecallPlanner.PrimaryPortalTieRecall] = "Primary Portal Tie Recall", [RecallPlanner.SecondaryPortalTieRecall] = "Secondary Portal Tie Recall",
        };
        foreach (AtlasRecall r in _atlas.Recalls) spells.TryAdd(r.SpellId, r.Name);
        string dir = NavDataDir, folder = NavProfilesDir;
        RouteJobs.Start(j, $"reading {q.Name}.nav", NowMs);
        return () => InfoWork(q, folder, dir, hasPlayer, pns, pew, dungeons, map, spells);
    }

    private static RouteWork InfoWork(RouteRequest q, string folder, string dir, bool hasPlayer, double pns, double pew,
        HashSet<uint> dungeons, HashSet<uint> map, Dictionary<int, string> spells)
    {
        var o = new RouteWork();
        string file = Path.Combine(folder, q.Name + ".nav");
        if (!File.Exists(file)) return Failed(o, q, $"no route called {q.Name} in RynthAi's NavProfiles ({folder})");
        NavRouteParser route = NavRouteParser.Load(file);
        bool IsDungeon(uint lb) => dungeons.Contains(lb) || (!map.Contains(lb) && File.Exists(Path.Combine(dir, DungeonTileFile.FileName(lb))));
        RouteInfo info = RouteInfo.Describe(route, q.Name, file, IsDungeon, hasPlayer, pns, pew, id => spells.TryGetValue(id, out string? n) ? n : null);
        if (info.Points == 0 && info.FollowTarget.Length == 0)
            return Failed(o, q, $"{q.Name}.nav has no points{(info.Warning.Length > 0 ? $" ({info.Warning})" : "")}");
        o.Ok = true;
        o.Detail = $"{info.Type}, {info.Points} point(s), {NavCoords.FmtDistance(info.Yards)}{(info.Dungeon ? ", a dungeon route" : "")}";
        o.Lines.AddRange(info.ChatLines());
        o.Json = RouteJson.Write(info.WriteJson);
        return o;
    }

    // ── Shared by the workers ────────────────────────────────────────────

    private static RouteWork Failed(RouteWork o, RouteRequest q, string why)
    {
        o.Ok = false;
        o.Detail = why;
        o.Json = RouteJson.Error(why, "");
        o.Lines.Clear();
        o.Lines.Add($"{q.Command}: {why}");
        return o;
    }

    /// <summary>Writes the route (never over a file there, unless asked); false with the job failed when it can't.</summary>
    private static bool SaveNav(RouteWork o, RouteRequest q, string folder, NavRouteParser route, out string file)
    {
        if (NavRouteExport.Save(folder, q.Name, route, q.Overwrite, out file, out bool exists, out string error)) return true;
        Failed(o, q, exists ? $"{q.Name}.nav is already in RynthAi's NavProfiles; ask again with overwrite to replace it"
                            : $"couldn't write {q.Name}.nav: {error}");
        return false;
    }

    /// <summary>The make ops' result: file, name, type, points, yards, dropped points, notes, and the op's own fields.</summary>
    private static string MakeJson(RouteRequest q, string file, NavRouteParser? route, double yards, List<LoopMaker.Dropped>? dropped,
        Action<Utf8JsonWriter>? extra, IReadOnlyList<string> notes, string error = "") => RouteJson.Write(w =>
    {
        w.WriteStartObject();
        if (error.Length > 0) w.WriteString("error", error);
        w.WriteString("name", q.Name);
        if (file.Length > 0) w.WriteString("file", file); else w.WriteNull("file");
        w.WriteString("type", route != null ? RouteInfo.TypeName(route.RouteType) : q.Op == RouteOp.MakeLoop ? "circular" : RouteInfo.TypeName(q.PathType));
        w.WriteNumber("points", route?.Points.Count ?? 0);
        RouteJson.Num(w, "yards", yards, 0);
        w.WriteNumber("dropped", dropped?.Count ?? 0);
        w.WriteStartArray("droppedPoints");
        if (dropped != null)
            foreach (LoopMaker.Dropped d in dropped)
            {
                w.WriteStartObject();
                RouteJson.Num(w, "ns", d.Ns, 4);
                RouteJson.Num(w, "ew", d.Ew, 4);
                w.WriteString("why", d.Why);
                w.WriteEndObject();
            }
        w.WriteEndArray();
        extra?.Invoke(w);
        RouteJson.Strings(w, "notes", notes);
        w.WriteEndObject();
    });
}
