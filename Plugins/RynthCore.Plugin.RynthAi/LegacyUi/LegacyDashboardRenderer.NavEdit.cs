// LegacyDashboardRenderer.NavEdit.cs — route editor commands from the engine Nav panel:
// multi-select move / duplicate / delete, set active, move a step to the player, nearest,
// reverse, snap to path, pause / portal / point-at-selection steps, simplify, recording and
// trail -> route.
//
// Commands arrive with indices into the route the panel last saw. Structural edits go
// through NavRouteEditing.Apply (copy-on-write) so the nav engine never sees a half-edited list.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed partial class LegacyDashboardRenderer
{
    /// <summary>How long an edit's result stays in the Nav panel's status line.</summary>
    private const long NavEditStatusMs = 8_000;

    /// <summary>Spot closer than this to an existing waypoint is refused by "Snap to path", yards.</summary>
    private const double SnapMinGapYards = 0.5;

    /// <summary>Decal ObjectClass.Portal, as VTank writes it in Portal steps.</summary>
    private const int PortalObjectClass = 14;

    private string _navEditStatus = string.Empty;
    private long _navEditStatusAt;

    /// <summary>Breadcrumb trail (set by the plugin once the nav overlay exists; null before login).</summary>
    internal NavBreadcrumbTracker? NavBreadcrumbs { get; set; }

    private List<NavPoint> RoutePoints => _settings.CurrentRoute.Points;

    /// <summary>The last edit's result while it is fresh, else empty.</summary>
    private string CurrentNavEditStatus() =>
        _navEditStatus.Length > 0 && Environment.TickCount64 - _navEditStatusAt < NavEditStatusMs ? _navEditStatus : string.Empty;

    private void SetNavEditStatus(string text)
    {
        _navEditStatus = text;
        _navEditStatusAt = Environment.TickCount64;
        RynthLog.Trace(LogCat.Navigation, "[NavEdit] " + text);
    }

    private void HandleNavEditCommand(NavCommand cmd)
    {
        switch (cmd.Cmd)
        {
            case "movePoints":
            {
                var sel = PointsAt(cmd.Indices);
                if (sel.Count == 0 || cmd.Delta == 0) return;
                EditRoute(l => NavRouteEditing.Move(l, sel, Math.Sign(cmd.Delta)));
                break;
            }
            case "duplicatePoints":
            {
                var sel = PointsAt(cmd.Indices);
                if (sel.Count == 0) return;
                var copies = new List<NavPoint>();
                EditRoute(l => NavRouteEditing.Duplicate(l, sel, copies));
                SetNavEditStatus($"Duplicated {copies.Count} step(s).");
                break;
            }
            case "deletePoints":
            {
                var sel = PointsAt(cmd.Indices);
                if (sel.Count == 0) return;
                EditRoute(l => NavRouteEditing.Remove(l, sel));
                SetNavEditStatus($"Deleted {sel.Count} step(s).");
                break;
            }
            case "setActive":
                if (cmd.Index < 0 || cmd.Index >= RoutePoints.Count) return;
                _settings.ActiveNavIndex = cmd.Index;
                SaveSettings();
                SetNavEditStatus($"Active waypoint: {cmd.Index}.");
                break;

            case "movePointHere":
            {
                if (cmd.Index < 0 || cmd.Index >= RoutePoints.Count || !TryNavHere(out NavPoint here)) return;
                NavPoint p = RoutePoints[cmd.Index];
                p.NS = here.NS; p.EW = here.EW; p.Z = here.Z;
                _navigationUi.TryAutoSaveNav();
                SetNavEditStatus($"Step {cmd.Index} moved to your position.");
                break;
            }
            case "nearestActive":
            {
                int i = FindNearestWaypoint(_settings.CurrentRoute);
                if (RoutePoints.Count == 0) return;
                _settings.ActiveNavIndex = i;
                SaveSettings();
                SetNavEditStatus($"Active waypoint: {i} (nearest).");
                break;
            }
            case "reverseRoute":
                if (RoutePoints.Count < 2) return;
                EditRoute(NavRouteEditing.Reverse);
                SetNavEditStatus("Route reversed.");
                break;

            case "snapToPath":
            {
                if (!TryNavHere(out NavPoint here)) return;
                NavPoint? inserted = null;
                string error = string.Empty;
                EditRoute(l => { NavRouteEditing.TrySnapInsert(l, here.NS, here.EW, SnapMinGapYards, out inserted, out error); return l; });
                SetNavEditStatus(inserted != null ? $"Inserted a point on the path at step {RoutePoints.IndexOf(inserted)}." : error);
                break;
            }
            case "addPause":
            {
                if (!TryNavHere(out NavPoint p)) return;
                p.Type = NavPointType.Pause;
                p.PauseTimeMs = (int)(Math.Clamp(cmd.Seconds, 0.1, 600.0) * 1000);
                InsertNavPoint(p, cmd.AddMode, cmd.InsertAt);
                SetNavEditStatus($"Added a {p.PauseTimeMs / 1000.0:0.#} s pause.");
                break;
            }
            case "addPortal":
            {
                if (!TryDescribeNavSelection(out string name, out double ons, out double oew, out double oz) || !TryNavHere(out NavPoint p)) return;
                p.Type = NavPointType.PortalNPC;
                p.TargetName = name;
                p.ObjectClass = PortalObjectClass;
                p.PortalExitNS = ons; p.PortalExitEW = oew; p.PortalExitZ = oz;
                InsertNavPoint(p, cmd.AddMode, cmd.InsertAt);
                SetNavEditStatus($"Added Portal step '{name}'.");
                break;
            }
            case "addPointAtSelected":
            {
                if (!TryDescribeNavSelection(out string name, out double ns, out double ew, out double z)) return;
                InsertNavPoint(new NavPoint { Type = NavPointType.Point, NS = ns, EW = ew, Z = z }, cmd.AddMode, cmd.InsertAt);
                SetNavEditStatus($"Added a point at '{name}'.");
                break;
            }
            case "simplifyRoute":
            {
                double tol = Math.Clamp(cmd.Yards, 0.2, 10.0);
                int ai = _settings.ActiveNavIndex;
                NavPoint? keep = ai >= 0 && ai < RoutePoints.Count ? RoutePoints[ai] : null;
                int removed = 0;
                EditRoute(l => { removed = NavRouteEditing.Simplify(l, tol, keep); return l; });
                SetNavEditStatus($"Simplify ({tol:0.#} yd) removed {removed} point(s).");
                break;
            }
            case "setRecording":
                _settings.IsRecordingNav = cmd.On;
                SetNavEditStatus(cmd.On ? "Recording: walk the route; points are added for you." : "Recording stopped.");
                break;

            case "trailToRoute":
            {
                NavCrumb[] trail = NavBreadcrumbs?.Trail ?? Array.Empty<NavCrumb>();
                List<NavPoint> pts = NavBreadcrumbTracker.TrailToPoints(trail, _settings.NavOverlay.RecordSpacingYards, cmd.Reverse);
                if (pts.Count < 2) { SetNavEditStatus("The trail is too short to make a route."); return; }
                _settings.IsRecordingNav = false;
                // A new unsaved route: the loaded file on disk is not changed (Save As keeps it).
                _settings.CurrentRoute = new NavRouteParser { RouteType = NavRouteType.Once, Points = pts };
                _settings.CurrentNavPath = string.Empty;
                _settings.ActiveNavIndex = 0;
                RefreshNavFiles();
                SaveSettings();
                SetNavEditStatus($"{(cmd.Reverse ? "Backtrack" : "Trail")} route created: {pts.Count} points (unsaved).");
                break;
            }
        }
    }

    /// <summary>Copy-on-write route edit, then the route file autosave.</summary>
    private void EditRoute(Func<List<NavPoint>, List<NavPoint>> edit)
    {
        NavRouteEditing.Apply(_settings, edit);
        _navigationUi.TryAutoSaveNav();
    }

    /// <summary>The route's points at <paramref name="indices"/>; out-of-range indices are skipped.</summary>
    private HashSet<NavPoint> PointsAt(List<int>? indices)
    {
        var sel = new HashSet<NavPoint>();
        if (indices == null) return sel;
        var pts = RoutePoints;
        foreach (int i in indices)
            if (i >= 0 && i < pts.Count) sel.Add(pts[i]);
        return sel;
    }

    /// <summary>A Point at the player's position (Z in nav units), or false when the position is unknown.</summary>
    private bool TryNavHere(out NavPoint p)
    {
        p = new NavPoint();
        if (!NavCoordinateHelper.TryGetNavPosition(_host, out double ns, out double ew, out double z))
        {
            SetNavEditStatus("Position unavailable.");
            return false;
        }
        p.NS = ns; p.EW = ew; p.Z = z;
        return true;
    }

    /// <summary>Name and nav position of the selected world object.</summary>
    private bool TryDescribeNavSelection(out string name, out double ns, out double ew, out double navZ)
    {
        name = string.Empty; ns = ew = navZ = 0;
        if (!_host.HasGetSelectedItemId) { SetNavEditStatus("Selection unavailable."); return false; }
        uint sel = _host.GetSelectedItemId();
        if (sel == 0) { SetNavEditStatus("Select an object first."); return false; }
        if (!_host.HasGetObjectName || !_host.TryGetObjectName(sel, out name) || string.IsNullOrWhiteSpace(name))
        {
            SetNavEditStatus("The selected object has no name yet.");
            return false;
        }
        if (_host.HasGetObjectPosition
            && _host.TryGetObjectPosition(sel, out uint cell, out float x, out float y, out float z)
            && NavCoordinateHelper.TryConvertPoseToCoords(cell, x, y, out ns, out ew))
        {
            navZ = z / NavCoordinateHelper.NavZScale;
            return true;
        }
        SetNavEditStatus($"Could not read the position of '{name}'.");
        return false;
    }
}
