// NavBreadcrumbTracker.cs — position sampling for the Navigation window:
//
//   * Breadcrumb trail: where the character has walked (spacing / max points / teleport
//     breaks), drawn by NavOverlayRenderer and exportable as a route ("backtrack").
//   * Route recording (UB-IT KeyStoreRecorder style): while LegacyUiSettings.IsRecordingNav
//     is on, appends Point waypoints every RecordSpacingYards and, on a teleport, a Portal
//     step for the portal selected just before it (plus an optional pause).
//
// Runs on the plugin tick thread. The render thread only reads the published trail array
// (copy-on-write), so no locks are needed.
using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>One trail sample. Z is the raw world height (marker frame = Z, not Z/240).</summary>
internal readonly record struct NavCrumb(double NS, double EW, float Z, bool SegmentStart);

internal sealed class NavBreadcrumbTracker
{
    /// <summary>A position jump larger than this between ticks is a teleport (engine uses the same 50 yd).</summary>
    private const double TeleportYards = 50.0;
    /// <summary>A portal selected longer ago than this isn't credited for a recorded teleport.</summary>
    private const long PortalCreditMs = 20_000;
    /// <summary>Recording autosaves to the route file every this many added waypoints.</summary>
    private const int RecordAutosaveEvery = 10;

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    /// <summary>Object id → (name, Decal object class) when it is a portal, else null.</summary>
    private readonly Func<uint, (string Name, int ObjectClass)?> _describePortal;
    private readonly Action<string> _chat;

    // ── Trail (tick thread owns _crumbs; _snapshot is what the renderer reads) ──
    private readonly List<NavCrumb> _crumbs = new();
    private NavCrumb[] _snapshot = Array.Empty<NavCrumb>();
    private bool _breakNext = true;
    private double _trailYards;

    // ── Teleport detection ──
    private double _lastNS = double.NaN, _lastEW, _lastZ;
    private bool _wasPortaling;

    // ── Recording ──
    private bool _recordingActive;
    private double _recNS = double.NaN, _recEW;
    private int _recAdded, _recSinceSave;
    private (string Name, int Cls, double NS, double EW, double NavZ, long At)? _portalCandidate;

    public NavBreadcrumbTracker(RynthCoreHost host, LegacyUiSettings settings,
        Func<uint, (string Name, int ObjectClass)?> describePortal, Action<string> chat)
    {
        _host = host;
        _settings = settings;
        _describePortal = describePortal;
        _chat = chat;
    }

    /// <summary>Current trail, oldest first (safe to read from the render thread).</summary>
    public NavCrumb[] Trail => Volatile.Read(ref _snapshot);
    /// <summary>Walked length of the trail (teleport jumps excluded), yards.</summary>
    public double TrailYards => Volatile.Read(ref _trailYards);
    /// <summary>Waypoints added by the current recording session.</summary>
    public int RecordedCount => Volatile.Read(ref _recAdded);

    /// <summary>Clear request from the UI thread; applied on the next tick.</summary>
    public void RequestClear() => Interlocked.Exchange(ref _clearRequested, 1);
    private int _clearRequested;

    public void Tick()
    {
        var o = _settings.NavOverlay;
        if (Interlocked.Exchange(ref _clearRequested, 0) == 1) ClearTrail();

        // Portal space: no position worth recording, but remember we passed through it.
        if (_host.HasIsPortaling && _host.IsPortaling())
        {
            _wasPortaling = true;
            return;
        }
        if (!NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew)) return;
        if (!_host.TryGetPlayerPose(out uint cell, out _, out _, out float z, out _, out _, out _, out _)) return;
        if ((cell >> 16) == 0) return;

        bool hadLast = !double.IsNaN(_lastNS);
        bool teleported = _wasPortaling
            || (hadLast && NavRouteEditing.DistanceYards(_lastNS, _lastEW, ns, ew) > TeleportYards);
        _wasPortaling = false;
        double preNS = _lastNS, preEW = _lastEW, preZ = _lastZ;
        _lastNS = ns; _lastEW = ew; _lastZ = z;

        if (teleported) _breakNext = true;

        // While tracking is off, keep a break pending so the trail resumes as a new segment
        // instead of joining across the untracked stretch.
        if (o.TrackBreadcrumbs) SampleCrumb(ns, ew, z, o);
        else _breakNext = true;
        TickRecording(ns, ew, z, o, teleported && hadLast, preNS, preEW, preZ);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Breadcrumb trail
    // ═══════════════════════════════════════════════════════════════════

    private void SampleCrumb(double ns, double ew, float z, NavOverlaySettings o)
    {
        if (_crumbs.Count > 0 && !_breakNext)
        {
            var last = _crumbs[^1];
            double d = NavRouteEditing.DistanceYards(last.NS, last.EW, ns, ew);
            if (d < o.BreadcrumbSpacingYards) return;
            _trailYards += d;
        }
        _crumbs.Add(new NavCrumb(ns, ew, z, _breakNext || _crumbs.Count == 0));
        _breakNext = false;

        int over = _crumbs.Count - o.BreadcrumbMaxPoints;
        if (over > 0)
        {
            _crumbs.RemoveRange(0, over);
            if (_crumbs.Count > 0 && !_crumbs[0].SegmentStart)
                _crumbs[0] = _crumbs[0] with { SegmentStart = true };
            _trailYards = MeasureTrail(_crumbs);
        }
        Volatile.Write(ref _snapshot, _crumbs.ToArray());
    }

    private void ClearTrail()
    {
        _crumbs.Clear();
        _trailYards = 0;
        _breakNext = true;
        Volatile.Write(ref _snapshot, Array.Empty<NavCrumb>());
    }

    private static double MeasureTrail(List<NavCrumb> crumbs)
    {
        double total = 0;
        for (int i = 1; i < crumbs.Count; i++)
            if (!crumbs[i].SegmentStart)
                total += NavRouteEditing.DistanceYards(crumbs[i - 1].NS, crumbs[i - 1].EW, crumbs[i].NS, crumbs[i].EW);
        return total;
    }

    /// <summary>
    /// Trail → waypoints (Z converted to nav units), thinned to <paramref name="spacingYards"/>.
    /// <paramref name="reverse"/> builds a backtrack (newest first). Teleport breaks end the
    /// export so the route never asks the bot to walk across a portal jump.
    /// </summary>
    public static List<NavPoint> TrailToPoints(NavCrumb[] trail, double spacingYards, bool reverse)
    {
        var pts = new List<NavPoint>();
        if (trail.Length == 0) return pts;

        // Newest unbroken segment only: from the last segment start to the end.
        int start = 0;
        for (int i = trail.Length - 1; i >= 0; i--)
            if (trail[i].SegmentStart) { start = i; break; }

        NavCrumb? lastKept = null;
        for (int i = start; i < trail.Length; i++)
        {
            var c = trail[i];
            bool isLast = i == trail.Length - 1;
            if (lastKept is NavCrumb k && !isLast
                && NavRouteEditing.DistanceYards(k.NS, k.EW, c.NS, c.EW) < spacingYards)
                continue;
            pts.Add(new NavPoint { Type = NavPointType.Point, NS = c.NS, EW = c.EW, Z = c.Z / NavCoordinateHelper.NavZScale });
            lastKept = c;
        }
        if (reverse) pts.Reverse();
        return pts;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Route recording
    // ═══════════════════════════════════════════════════════════════════

    private void TickRecording(double ns, double ew, float z, NavOverlaySettings o,
        bool teleported, double preNS, double preEW, double preZ)
    {
        if (!_settings.IsRecordingNav)
        {
            if (_recordingActive) StopRecordingSession();
            return;
        }
        if (!_recordingActive)
        {
            _recordingActive = true;
            _recNS = double.NaN;
            _recAdded = 0;
            _recSinceSave = 0;
            _portalCandidate = null;
            RynthLog.Write(LogCat.Navigation, "[NavRecord] recording started.");
        }

        TrackPortalSelection();

        if (teleported)
        {
            // Portal used just before the jump → Portal step (+ pause); the arrival point
            // then records immediately because _recNS is reset.
            if (o.RecordPortals && _portalCandidate is { } pc && Environment.TickCount64 - pc.At <= PortalCreditMs)
            {
                AddRecorded(new NavPoint
                {
                    Type = NavPointType.PortalNPC,
                    NS = preNS, EW = preEW, Z = preZ / NavCoordinateHelper.NavZScale,
                    TargetName = pc.Name, ObjectClass = pc.Cls,
                    PortalExitNS = pc.NS, PortalExitEW = pc.EW, PortalExitZ = pc.NavZ,
                });
                if (o.RecordPauseAfterPortalMs > 0)
                    AddRecorded(new NavPoint { Type = NavPointType.Pause, NS = ns, EW = ew, Z = z / NavCoordinateHelper.NavZScale, PauseTimeMs = o.RecordPauseAfterPortalMs });
                _chat($"[RynthAi] Nav record: added Portal step '{pc.Name}'.");
            }
            else
            {
                _chat("[RynthAi] Nav record: teleport detected — add a Portal or Recall step for it if the route needs one.");
            }
            _portalCandidate = null;
            _recNS = double.NaN;
        }

        if (!double.IsNaN(_recNS) && NavRouteEditing.DistanceYards(_recNS, _recEW, ns, ew) < o.RecordSpacingYards)
            return;

        AddRecorded(new NavPoint { Type = NavPointType.Point, NS = ns, EW = ew, Z = z / NavCoordinateHelper.NavZScale });
        _recNS = ns;
        _recEW = ew;
    }

    /// <summary>Remembers the last selected portal (name, class, position) for the teleport step.</summary>
    private void TrackPortalSelection()
    {
        if (!_host.HasGetSelectedItemId) return;
        uint sel = _host.GetSelectedItemId();
        if (sel == 0) return;
        if (_portalCandidate is { } cur && Environment.TickCount64 - cur.At < 1000) return; // refresh ~1/s
        if (_describePortal(sel) is not { } info) return;

        double pns = 0, pew = 0, pz = 0;
        if (_host.HasGetObjectPosition
            && _host.TryGetObjectPosition(sel, out uint pcell, out float ox, out float oy, out float oz)
            && NavCoordinateHelper.TryConvertPoseToCoords(pcell, ox, oy, out pns, out pew))
            pz = oz / NavCoordinateHelper.NavZScale;
        _portalCandidate = (info.Name, info.ObjectClass, pns, pew, pz, Environment.TickCount64);
    }

    private void AddRecorded(NavPoint p)
    {
        NavRouteEditing.Apply(_settings, list => { list.Add(p); return list; });
        Interlocked.Increment(ref _recAdded);
        if (++_recSinceSave >= RecordAutosaveEvery)
        {
            _recSinceSave = 0;
            SaveRouteFile();
        }
    }

    /// <summary>Session teardown: saves a recording in progress (no chat — the session is ending).</summary>
    public void Shutdown()
    {
        if (_recordingActive) StopRecordingSession(announce: false);
    }

    private void StopRecordingSession(bool announce = true)
    {
        _recordingActive = false;
        _portalCandidate = null;
        SaveRouteFile();
        RynthLog.Write(LogCat.Navigation, $"[NavRecord] recording stopped ({_recAdded} waypoints added).");
        if (announce)
            _chat($"[RynthAi] Nav record stopped: {_recAdded} waypoints added" +
                  (string.IsNullOrEmpty(_settings.CurrentNavPath) ? " (route not saved yet — use Save As)." : " and saved."));
    }

    private void SaveRouteFile()
    {
        if (string.IsNullOrEmpty(_settings.CurrentNavPath)) return;
        try { _settings.CurrentRoute.Save(_settings.CurrentNavPath); }
        catch (Exception ex) { RynthLog.Write(LogCat.Navigation, $"[NavRecord] save failed: {ex.Message}"); }
    }
}
