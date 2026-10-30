using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Two things a walking leg must never do (Lucy, 10-01 17:53):
//  1. Walk into a portal that isn't its target. Every portal near the player is kept out of: the
//     detailed path goes round it (AvoidFilter) and the steering turns round it (PortalAvoid.Steer),
//     whatever the walker is heading for: a path corner, the route's aim or a blind push.
//  2. Head off toward a target that can't be walked to. With a route graph the walker waits for
//     the route search's answer before its first step; "no route" to another landmass re-plans
//     with portals and recalls once (when they're on) or stops and says why.
// Tick thread.
public sealed partial class RynthNavPlugin
{
    private const uint ItemTypePortal = 0x00010000;
    private const long AvoidRefreshMs = 1000;
    private const double AvoidNearUnits = 300.0;       // portals farther than this don't matter yet
    private const double AvoidTargetUnits = 15.0;      // the step's own portal (a portal step's target) isn't avoided
    private const int CoarseHoldMaxTicks = 90;         // ~3 s: never wait longer than this for the search

    private readonly PortalAvoid _avoid = new();
    private AvoidFilter? _avoidFilter;
    private long _nextAvoidMs;
    private bool _avoidFallbackSaid;
    private Atlas? _avoidAtlas;
    private List<PortalAvoid.Spot> _atlasPortals = new();
    private int _avoidLogged = -1;

    private int _coarseHoldTicks;                       // re-plans to wait for the route search (see StepGoto)
    private bool _landReplanned;                       // this goto already re-planned after "no route over land"
    private string _gotoType = "";
    private bool _walkHaveLast;                        // last tick's position on a walking leg (teleport check)
    private double _walkLastWx, _walkLastWy;

    private AvoidFilter WalkFilter() => _avoidFilter ??= new AvoidFilter(_avoid);

    /// <summary>
    /// The portals to keep out of, near the player: every portal object the client knows (exact),
    /// and the Atlas's portals on the map (to 0.01 degree) where no live one stands. The current
    /// step's own portal (walking into a portal or a dungeon's entrance) is left out.
    /// </summary>
    private void RefreshAvoid(bool force)
    {
        long now = NowMs;
        if (!force && now < _nextAvoidMs) return;
        _nextAvoidMs = now + AvoidRefreshMs;
        if (!_avoidPortals) { _avoid.Clear(); return; }

        IReadOnlyList<PortalAvoid.Spot> atlasPortals = AtlasPortalSpots();
        var map = new List<PortalAvoid.Spot>();
        foreach (PortalAvoid.Spot sp in atlasPortals)
            if (Math.Abs(sp.X - _wx) < AvoidNearUnits && Math.Abs(sp.Y - _wy) < AvoidNearUnits) map.Add(sp);

        var live = new List<PortalAvoid.Spot>();
        if (Host.HasGetLiveObjectIds)
        {
            foreach (uint id in Host.GetLiveObjectIds())
            {
                if (!Host.TryGetItemType(id, out uint type) || (type & ItemTypePortal) == 0) continue;
                if (!Host.TryGetObjectPosition(id, out uint cell, out float x, out float y, out _) || cell == 0) continue;
                double wx = ((cell >> 24) & 0xFF) * 192.0 + x, wy = ((cell >> 16) & 0xFF) * 192.0 + y;
                if (Math.Abs(wx - _wx) > AvoidNearUnits || Math.Abs(wy - _wy) > AvoidNearUnits) continue;
                Host.TryGetObjectName(id, out string name);
                live.Add(new PortalAvoid.Spot(wx, wy, PortalAvoid.LiveRadius, name ?? ""));
            }
        }

        // The step's own target: walking into a portal (a route's portal step, a dungeon's entrance).
        bool portalLeg = _route != null && _routeIdx < _route.Count && _route[_routeIdx].UsePortal;
        if (portalLeg) _avoid.Set(live, map, _gotoTew, _gotoTns, AvoidTargetUnits);
        else _avoid.Set(live, map);
        if (_avoid.Count != _avoidLogged)
        {
            _avoidLogged = _avoid.Count;
            Host.Log($"[RynthNav] avoid: {_avoid.Count} portal(s) near you to keep out of ({live.Count} seen, {map.Count} from the Atlas){(portalLeg ? "; the step's own portal excepted" : "")}");
        }
    }

    /// <summary>The heading to take instead of <paramref name="desired"/>, round any portal in the next <paramref name="lookahead"/> units.</summary>
    private double SteerRoundPortals(double desired, double lookahead)
    {
        if (_avoid.Count == 0) return desired;
        double h = _avoid.Steer(_wx, _wy, desired, lookahead, out int spot);
        if (spot >= 0 && Math.Abs(h - desired) > 0.5 && _blindLogTicks-- <= 0)
        {
            _blindLogTicks = 30;
            Host.Log($"[RynthNav] avoid: steering round {(_avoid.Spots[spot].Name.Length > 0 ? _avoid.Spots[spot].Name : "a portal")} ({desired:F0} -> {h:F0} deg)");
        }
        return h;
    }
    private int _blindLogTicks;

    /// <summary>
    /// A route in one readable line for the log: "recall Recall Aphus Lassel → walk 12 yd to the Town
    /// Network portal → Town Network (exit Arwic) → walk 118 yd; cost ~1265u; walking straight 14210
    /// yd". So a strange route can be read from the log afterwards.
    /// </summary>
    private string RouteLine(List<RouteStep>? steps, double startNs, double startEw, double goalNs, double goalEw, double est, bool? land)
    {
        var parts = new List<string>();
        double pns = startNs, pew = startEw;
        if (steps != null)
            for (int i = 0; i < steps.Count; i++)
            {
                RouteStep st = steps[i];
                bool nextHub = i + 1 < steps.Count && steps[i + 1].IsHub;
                if (st.UseRecall) parts.Add("recall " + st.Label);
                else if (st.IsHub) parts.Add($"Town Network (exit {HubWhere(HubExitOf(i) ?? (_townNet != null && st.HubExit >= 0 && st.HubExit < _townNet.Exits.Count ? _townNet.Exits[st.HubExit] : null))})");
                else
                {
                    double d = NavCoords.Distance(pns, pew, st.Ns, st.Ew);
                    parts.Add(nextHub ? $"walk {d:F0} yd to the Town Network portal"
                        : st.UsePortal ? $"walk {d:F0} yd, portal {st.Label}"
                        : $"walk {d:F0} yd");
                }
                if (st.UseRecall || st.IsHub || (st.UsePortal && !double.IsNaN(st.LandNs))) { pns = st.LandNs; pew = st.LandEw; }
                else { pns = st.Ns; pew = st.Ew; }
                if (double.IsNaN(pns)) { pns = st.Ns; pew = st.Ew; }
            }
        if (parts.Count == 0) parts.Add($"walk {NavCoords.Distance(startNs, startEw, goalNs, goalEw):F0} yd");
        double straight = NavCoords.Distance(startNs, startEw, goalNs, goalEw);
        return string.Join(" → ", parts) + $"; cost ~{(double.IsInfinity(est) ? "none" : est.ToString("F0"))}u; walking straight {straight:F0} yd"
             + (land == false ? " (not possible: another landmass)" : "");
    }

    /// <summary>
    /// The route search says the target is on another landmass. With portals and recalls on, plan
    /// the whole trip again from here, once (walks over water are then left out, so the plan takes
    /// a portal, a recall or the Town Network); otherwise, or when that finds nothing, stop and say why.
    /// </summary>
    private void NoLandRoute()
    {
        string name = _gotoName.Length > 0 ? _gotoName : "there";
        double ns = NavCoords.NsFromWorld(_finalTns), ew = NavCoords.EwFromWorld(_finalTew);
        string type = _gotoType, enter = _enterName, enterType = _enterType;
        bool onFinalLeg = _route == null || _routeIdx >= _route.Count - 1;
        if (_portalsEnabled && !_landReplanned)
        {
            _landReplanned = true;
            Host.Log($"[RynthNav] no route over land to {name}: planning again with portals and recalls");
            Say($"goto: no walking route to {name} from here (water or cliffs in the way); planning with portals and recalls");
            StartWalk(name, type, ns, ew, enter, enterType, landReplan: true);
            return;
        }
        string why = $"no walking route to {(onFinalLeg ? name : "the next step")} (water or cliffs in the way)";
        FinishGoto(_portalsEnabled
            ? $"stopped: {why}, and no portal, recall or the Town Network I know of gets there"
            : $"stopped: {why}; turn on Use portals and recalls");
    }
}
