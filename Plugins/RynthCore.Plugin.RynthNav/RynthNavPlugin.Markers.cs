using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// The planned trip on the ground: RynthNav's path markers (PathMarkers), drawn on the engine's
// Nav3D buffer the way RynthAi draws its nav routes. When Route or Go plans a trip, a worker works
// out the whole walk ahead of time (TilePathFinder: the route graph and the tiles, in its own
// navmesh), and the markers show it: rings at the waypoints near you, lines between them, the
// portal / recall / Town Network / goal markers in their own colours, and (switches) labels on
// those and a guide line from your feet to the next waypoint. The same path is what "save as nav
// route" writes (RynthNavPlugin.SaveRoute.cs).
//
// The markers go away when the trip ends (arrived, stopped), is cancelled (Stop, the d-pad), or you
// land in a landblock that isn't on it (a teleport elsewhere). Switches in rynthnav.json
// (markers, markerLabels, markerGuide); /rnav markers and the panel's Go tab change them.
//
// Threads: everything here runs on the tick, except the path worker, which reads only the frozen
// trip (PlannedTrip), the immutable route graph and Atlas portal list, and the tile files. The
// emit reads only the frozen MarkerSnapshot, never the trip, the route or the walker.
public sealed partial class RynthNavPlugin
{
    private volatile bool _markersOn = true, _markerLabels = true, _markerGuide;

    // The last trip planned (Route or Go): kept for "save as nav route" after it ends.
    private volatile PlannedTrip? _trip;
    private int _tripSeq;
    private volatile bool _tripShown;                 // its markers are on the ground (not ended / cancelled / left)
    private TripPath? _tripPath;                      // its path, once worked out
    private Task<(TripPath Path, long Ms, int Tiles)>? _pathTask;
    private PlannedTrip? _pathTaskTrip;
    private string _nextEnterPortal = "", _enterPortal = "";

    // Markers (tick thread).
    private MarkerSnapshot _markerSnap = MarkerSnapshot.Empty;
    private PlannedTrip? _snapTrip;
    private TripPath? _snapPath;
    private int _markNext = -1, _markStep = -2, _markTrip;
    private uint _markLb;
    private HostMarkerSink? _markerSink;
    private volatile string _markerStatus = "";

    private void LoadMarkerSettings()
    {
        _markersOn = NavDataConfig.ReadSwitch("markers", true);
        _markerLabels = NavDataConfig.ReadSwitch("markerLabels", true);
        _markerGuide = NavDataConfig.ReadSwitch("markerGuide", false);
    }

    // ── The walking path, for the engine's World Map ─────────────────────
    // The trip's walks (and the walks inside the Town Network) as the status JSON's "path": an array of
    // segments, each a flat list of ns, ew pairs in map degrees (4 decimals: 0.02 yd), thinned to about
    // MaxPathPoints in all (a segment keeps its ends). Built once per TripPath and kept; empty (the field
    // is left out) while the markers are off the ground or the path isn't worked out yet. Pump thread.
    private const int MaxPathPoints = 800;
    private TripPath? _pathJsonFor;
    private string _pathJson = "";

    private string PathJson()
    {
        TripPath? p = _tripPath;
        if (p == null || !_tripShown) return "";
        lock (_gate)
        {
            if (ReferenceEquals(p, _pathJsonFor)) return _pathJson;
            var segs = new List<WorldPt[]>();
            for (int i = 0; i < p.Walks.Length; i++)
            {
                if (p.Walks[i] is { Length: >= 2 } w) segs.Add(w);
                if (i < p.Hub.Length && p.Hub[i] is { Length: >= 2 } h) segs.Add(h);
            }
            int total = 0;
            foreach (WorldPt[] s in segs) total += s.Length;
            int stride = Math.Max(1, (total + MaxPathPoints - 1) / MaxPathPoints);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(total * 20 + 8);
            sb.Append('[');
            bool firstSeg = true;
            foreach (WorldPt[] s in segs)
            {
                if (!firstSeg) sb.Append(',');
                firstSeg = false;
                sb.Append('[');
                for (int i = 0; i < s.Length; i++)
                {
                    if (i % stride != 0 && i != s.Length - 1) continue;
                    if (i > 0) sb.Append(',');
                    sb.Append(NavCoords.NsFromWorld(s[i].Y).ToString("F4", ci)).Append(',').Append(NavCoords.EwFromWorld(s[i].X).ToString("F4", ci));
                }
                sb.Append(']');
            }
            sb.Append(']');
            _pathJsonFor = p;
            _pathJson = segs.Count == 0 ? "" : sb.ToString();
            return _pathJson;
        }
    }

    // ── The trip ─────────────────────────────────────────────────────────

    /// <summary>A new plan (Route) or goto (Go): its trip, shown, its path worked out. Tick thread.</summary>
    private void SetTrip(List<RouteStep>? steps, string goal, double goalNs, double goalEw, List<RecallOption>? recalls,
        string enterName, bool live)
    {
        var tr = new List<TripRecall>();
        if (recalls != null) foreach (RecallOption o in recalls) tr.Add(new TripRecall(o.Name, o.SpellId, o.Command, o.CastSeconds));
        _trip = new PlannedTrip(++_tripSeq, steps ?? new List<RouteStep>(), goal, goalNs, goalEw,
            IsOnMap(_cellId), new WorldPt(_wx, _wy, _wz), _cellId, tr,
            enterName.Length > 0 ? _enterPortal : "", enterName, live);
        _tripPath = null;
        _tripShown = true;
        _markNext = -1; _markStep = -2;
        _markLb = CurrentLandblock;
        _saveConflict = "";
        _saveDefault = NavRouteExport.SafeName("To " + goal);
        if (_markersOn) StartPathBuild();
        _markerStatus = "working out the path…";
    }

    /// <summary>Takes the markers off the ground (the trip stays for "save as nav route"). Tick thread.</summary>
    private void HideMarkers(string why)
    {
        if (!_tripShown) return;
        _tripShown = false;
        _markNext = -1;
        _markerStatus = why;
        Host.Log($"[RynthNav] path markers: off ({why})");
    }

    private void StartPathBuild()
    {
        PlannedTrip? trip = _trip;
        if (trip == null || (_tripPath != null && _tripPath.TripId == trip.Id)) return;
        if (_pathTask != null && ReferenceEquals(_pathTaskTrip, trip)) return;
        NavGraph? g = UsableGraph();
        IReadOnlyList<PortalAvoid.Spot> portals = AtlasPortalSpots();
        TownNet? net = _townNet;
        string dir = NavDataDir;
        _pathTaskTrip = trip;
        _pathTask = Task.Run(() =>
        {
            long t0 = Environment.TickCount64;
            var finder = new TilePathFinder(dir, g, portals);
            TripPath p = TripPathBuilder.Build(trip,
                (WorldPt a, WorldPt b, bool intoPortal, out string note) => finder.Walk(a, b, intoPortal, out note), net);
            return (p, Environment.TickCount64 - t0, finder.TilesRead);
        });
    }

    private void TakePathResult()
    {
        if (_pathTask is not { IsCompleted: true } task) return;
        PlannedTrip? forTrip = _pathTaskTrip;
        _pathTask = null; _pathTaskTrip = null;
        if (task.IsFaulted)
        {
            Host.Log($"[RynthNav] trip path failed: {task.Exception?.GetBaseException().Message}");
            _markerStatus = "the path couldn't be worked out (see the log)";
            OnPathReady(forTrip, null);
            return;
        }
        var (path, ms, tiles) = task.Result;
        if (_trip == null || path.TripId != _trip.Id) { if (_markersOn || _pendingSave != null) StartPathBuild(); return; }   // planned for an older trip
        _tripPath = path;
        int points = 0;
        foreach (WorldPt[]? w in path.Walks) points += w?.Length ?? 0;
        foreach (WorldPt[]? w in path.Hub) points += w?.Length ?? 0;
        Host.Log($"[RynthNav] trip path to {_trip.Goal}: {points} waypoints over {_trip.Count} step(s), {tiles} tile(s) read, {ms} ms");
        foreach (string n in path.Notes) Host.Log($"[RynthNav] trip path: {n}");
        _markerStatus = path.Notes.Count == 0 ? $"{points} waypoints" : $"{points} waypoints; {path.Notes[0]}";
        OnPathReady(_trip, path);
    }

    // ── Tick ─────────────────────────────────────────────────────────────

    private void TickMarkers()
    {
        TakePathResult();
        PlannedTrip? trip = _trip;
        if (trip == null) return;

        // A landblock that isn't on the trip (a teleport somewhere else): the markers go.
        if (_hasPose && _tripShown)
        {
            uint lb = CurrentLandblock;
            if (lb != _markLb && lb != 0)
            {
                _markLb = lb;
                TownNet? net = _townNet;
                if (!trip.Covers(lb, _tripPath, net == null ? null : net.Contains))
                    HideMarkers($"landblock 0x{lb:X4} isn't on the trip");
            }
        }
        if (!_markersOn || !_tripShown || !Host.HasNav3D || !_hasPose) return;

        if (!ReferenceEquals(trip, _snapTrip) || !ReferenceEquals(_tripPath, _snapPath))
        {
            _snapTrip = trip; _snapPath = _tripPath;
            _markerSnap = PathMarkers.Build(trip, _tripPath, _townNet);
            _markNext = -1; _markStep = -2;
        }
        MarkerSnapshot snap = _markerSnap;
        if (snap.Marks.Length == 0) return;
        if ((_cellId >> 16) == 0 || (Host.HasIsPortaling && Host.IsPortaling())) return;

        // The marker the walk heads for: on the running goto's current step.
        int next = -1;
        if (_gotoActive && trip.Live && !_settling)
        {
            int step = _route != null ? _routeIdx : 0;
            if (step != _markStep || snap.TripId != _markTrip) { _markStep = step; _markTrip = snap.TripId; _markNext = -1; }
            _markNext = next = PathMarkers.Advance(snap, _markNext, step, _wx, _wy);
        }
        var view = new MarkerView(_wx, _wy, _wz, next, _markerLabels && Host.HasNav3DTriangle, _markerGuide);
        try { PathMarkers.Emit(snap, view, _markerSink ??= new HostMarkerSink(Host)); }
        catch (Exception ex) { Host.Log($"[RynthNav] path markers: {ex.Message}"); }
    }

    // ── /rnav markers ────────────────────────────────────────────────────

    /// <summary>/rnav markers [on|off | labels on|off | guide on|off]</summary>
    private void CmdMarkers(string rest)
    {
        string[] a = rest.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 1 && a[0] is "on" or "off")
        {
            _markersOn = a[0] == "on";
            NavDataConfig.WriteSwitch("markers", _markersOn);
            if (_markersOn) StartPathBuild();
        }
        else if (a.Length == 2 && a[0] == "labels" && a[1] is "on" or "off")
        { _markerLabels = a[1] == "on"; NavDataConfig.WriteSwitch("markerLabels", _markerLabels); }
        else if (a.Length == 2 && a[0] == "guide" && a[1] is "on" or "off")
        { _markerGuide = a[1] == "on"; NavDataConfig.WriteSwitch("markerGuide", _markerGuide); }
        else if (a.Length > 0) { Say("/rnav markers on|off, markers labels on|off, markers guide on|off"); return; }
        Say($"path markers {(_markersOn ? "ON" : "OFF")}, labels {(_markerLabels ? "on" : "off")}, guide line {(_markerGuide ? "on" : "off")}"
            + (_trip == null ? "" : _tripShown ? $" — showing the trip to {_trip.Goal}" : $" — the trip to {_trip.Goal} isn't shown ({_markerStatus})"));
    }

    /// <summary>The Atlas's portals on the map (positions to 0.01 degree): the circles a walk keeps out of. Tick thread.</summary>
    private IReadOnlyList<PortalAvoid.Spot> AtlasPortalSpots()
    {
        Atlas atlas = _atlas;
        if (!ReferenceEquals(atlas, _avoidAtlas))
        {
            var list = new List<PortalAvoid.Spot>();
            foreach (AtlasLocation l in atlas.Locations)
                if (l.OnMap && l.Type.Equals("Portal", StringComparison.Ordinal))
                    list.Add(new PortalAvoid.Spot(NavCoords.WorldX(l.Ew), NavCoords.WorldY(l.Ns), PortalAvoid.MapRadius, l.Name));
            _atlasPortals = list;
            _avoidAtlas = atlas;
        }
        return _atlasPortals;
    }

    /// <summary>The engine's Nav3D buffer (the engine clears it each tick; every plugin adds to it).</summary>
    private sealed class HostMarkerSink : IMarkerSink
    {
        private readonly RynthCore.PluginSdk.RynthCoreHost _host;
        public HostMarkerSink(RynthCore.PluginSdk.RynthCoreHost host) => _host = host;
        public void Ring(float x, float y, float z, float radius, float thickness, uint colorArgb) => _host.Nav3DAddRing(x, y, z, radius, thickness, colorArgb);
        public void Line(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb)
            => _host.Nav3DAddLine(x1, y1, z1, x2, y2, z2, thickness, colorArgb);
        public void Triangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint colorArgb)
            => _host.Nav3DAddTriangle(x1, y1, z1, x2, y2, z2, x3, y3, z3, colorArgb);
    }
}
