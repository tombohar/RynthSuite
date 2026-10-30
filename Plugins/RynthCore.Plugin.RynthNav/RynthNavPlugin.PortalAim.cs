using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

// Taking a portal: aim at the portal object the client actually shows (its exact position), not the
// Atlas spot (positions to 0.01 degree, a couple of metres out: "completely missing the portal by a
// few yards", 10-01 20:40), run straight into it, and if it doesn't fire, back off and try again
// rather than giving up. Timed by the clock, not by ticks, so a low frame rate doesn't cut it short.
public sealed partial class RynthNavPlugin
{
    private const long PortalTryMs = 12000;        // one run at the portal before backing off
    private const long PortalBackoffMs = 2500;     // back away this long, then run in again
    private const int PortalMaxTries = 5;          // about a minute in all
    private const double PortalSnapUnits = 25.0;   // a live portal this close to the planned spot is it
    private const double PortalUseUnits = 8.0;     // within this of the live portal, use it (the game walks you in)

    private long _portalTryStartMs, _portalBackoffUntilMs, _nextPortalSnapMs;
    private int _portalTries;
    private uint _portalLiveId;                    // the live portal object aimed at (0 = none seen)
    private long _portalLastUseMs;
    private bool _portalSnapped;

    private void BeginPortalAim()
    {
        _portalTryStartMs = NowMs; _portalBackoffUntilMs = 0; _nextPortalSnapMs = 0;
        _portalTries = 1; _portalSnapped = false; _portalLiveId = 0; _portalLastUseMs = 0;
    }

    /// <summary>The name the step's portal goes by (route step label, or the dungeon's entrance).</summary>
    private string PortalStepName()
    {
        if (_route != null && _routeIdx < _route.Count && _route[_routeIdx].Label.Length > 0) return _route[_routeIdx].Label;
        return _enterName;
    }

    /// <summary>Move the goal onto the live portal nearest the planned spot (a name match first).</summary>
    private void SnapToLivePortal()
    {
        long now = NowMs;
        if (now < _nextPortalSnapMs || !Host.HasGetLiveObjectIds) return;
        _nextPortalSnapMs = now + 500;
        string want = PortalStepName();
        double bestNamed = double.MaxValue, bestAny = double.MaxValue;
        double nx = 0, ny = 0, ax = 0, ay = 0; string nName = "", aName = ""; uint nId = 0, aId = 0;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!Host.TryGetItemType(id, out uint type) || (type & ItemTypePortal) == 0) continue;
            if (!Host.TryGetObjectPosition(id, out uint cell, out float x, out float y, out _) || cell == 0) continue;
            double wx = ((cell >> 24) & 0xFF) * 192.0 + x, wy = ((cell >> 16) & 0xFF) * 192.0 + y;
            double d = Math.Sqrt((wx - _gotoTew) * (wx - _gotoTew) + (wy - _gotoTns) * (wy - _gotoTns));
            if (d > PortalSnapUnits) continue;
            Host.TryGetObjectName(id, out string name);
            name ??= "";
            bool named = want.Length > 0 && name.Length > 0
                && (name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 || want.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (named && d < bestNamed) { bestNamed = d; nx = wx; ny = wy; nName = name; nId = id; }
            if (d < bestAny) { bestAny = d; ax = wx; ay = wy; aName = name; aId = id; }
        }
        bool haveNamed = bestNamed < double.MaxValue;
        if (!haveNamed && !(bestAny < 12.0)) return;   // an unnamed match only when it's clearly the one
        double tx = haveNamed ? nx : ax, ty = haveNamed ? ny : ay;
        double shift = Math.Sqrt((tx - _gotoTew) * (tx - _gotoTew) + (ty - _gotoTns) * (ty - _gotoTns));
        _gotoTew = tx; _gotoTns = ty;
        _portalLiveId = haveNamed ? nId : aId;
        if (!_portalSnapped)
        {
            _portalSnapped = true;
            Host.Log($"[RynthNav] portal: aiming at the live '{(haveNamed ? nName : aName)}' ({shift:F1}u from the planned spot)");
        }
    }

    /// <summary>
    /// Drive into the portal; after PortalTryMs without a teleport back off and run in again, up to
    /// PortalMaxTries. True when it gave up (the goto has been finished).
    /// </summary>
    private bool DriveIntoPortal(double dew, double dns, double d)
    {
        long now = NowMs;
        if (_portalBackoffUntilMs > now)
        {
            double away = Math.Atan2(-dew, -dns) * 180.0 / Math.PI;
            DriveTo(away);
            lock (_gate) _status = $"backing off to try the portal again ({_portalTries}/{PortalMaxTries})";
            return false;
        }
        if (_portalBackoffUntilMs != 0) { _portalBackoffUntilMs = 0; _portalTryStartMs = now; }

        if (now - _portalTryStartMs > PortalTryMs)
        {
            if (_portalTries >= PortalMaxTries)
            {
                Host.Log($"[RynthNav] portal did not fire after {_portalTries} tries — stopping");
                bool entrance = _enterName.Length > 0 && _route != null && _routeIdx >= _route.Count - 1;
                FinishGoto(entrance ? Entrance.NoTeleportText(_enterName) : $"stopped: the portal '{PortalStepName()}' didn't take you after {_portalTries} tries");
                return true;
            }
            _portalTries++;
            _portalBackoffUntilMs = now + PortalBackoffMs;
            Host.Log($"[RynthNav] portal didn't fire yet ({d:F1}u from it): backing off, try {_portalTries}/{PortalMaxTries}");
            return false;
        }

        // Close enough: use it, like a click - the game walks you the last few yards and in. Simpler
        // and surer than running into it (housing neighbourhood portals only fire when used, 10-01
        // 20:42). Within PortalUseUnits with the live portal seen: stand still and use it every 3 s.
        if (_portalLiveId != 0 && Host.HasUseObject && d <= PortalUseUnits)
        {
            AutoRun.Run(false);
            if (now - _portalLastUseMs > 3000)
            {
                _portalLastUseMs = now;
                Host.UseFor(_portalLiveId, "Goto", $"take the portal '{PortalStepName()}' on the route", UseKind.Asked);
                Host.Log($"[RynthNav] portal: using '{PortalStepName()}' from {d:F1}u (0x{_portalLiveId:X8})");
            }
            lock (_gate) _status = $"taking portal '{Trunc(PortalStepName())}'";
            return false;
        }

        // Straight at its centre: a portal fires on contact, so run right into it.
        double desired = Math.Atan2(dew, dns) * 180.0 / Math.PI;
        if (desired < 0) desired += 360.0;
        if (d > PortalContactUnits)
        {
            RefreshAvoid(force: false);
            desired = SteerRoundPortals(desired, Math.Min(10.0, d));
        }
        DriveTo(desired);
        lock (_gate) _status = $"taking portal '{Trunc(PortalStepName())}'";
        return false;
    }
}
