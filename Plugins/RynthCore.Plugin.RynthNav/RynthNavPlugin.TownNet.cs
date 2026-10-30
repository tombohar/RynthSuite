using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// The Town Network on a goto: the route planner takes it like a portal hub (entry portal → an
// arrival point inside → the walk to an exit portal → out), and here the walker follows the stored
// walk inside (NavData\townnet.json), where there is no navmesh. The entry portal is an ordinary
// portal step (StepPortalWait); the hub step is everything from landing inside to landing at the
// exit's destination. Without townnet.json (or with one that fails its checks) routes don't use the
// network and everything walks as before. Tick thread, except the background load.
public sealed partial class RynthNavPlugin
{
    private const uint StypeLevel = 25, StypeHeritageGroup = 188;
    private const int HeritageOlthoi = 12, HeritageOlthoiAcid = 13;

    private const double HubOffRouteUnits = HubWalker.OffRouteUnits;
    private const long HubLandTimeoutMs = 20000;     // the entry portal's teleport never lands inside
    private const long HubPortalSpaceTimeoutMs = 60000; // ...or never ends
    private const long HubSettleMs = 1500;           // out of portal space and inside this long = landed

    private volatile TownNet? _townNet;
    private volatile string _townNetStatus = "loading…";
    private HubLinks? _hubLinks;
    private (TownNet? Net, int Level, bool Olthoi) _hubLinksKey;

    // The hub step being walked (tick thread).
    private bool _hubActive, _hubLanded, _hubWaitSaid;
    private long _hubSettleSince;                   // when the landing settled (inside, out of portal space); 0 = not yet
    private long _hubStepMs;
    private HubWalker? _hubWalker;
    private bool HubIsWaiting => _hubWalker?.Waiting == true;

    private void LoadTownNet()
    {
        string path = Path.Combine(NavDataDir, TownNet.FileName);
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) { _townNetStatus = "no townnet.json: routes don't use the Town Network"; return; }
                TownNet? t = TownNet.Parse(File.ReadAllText(path), out string why);
                if (t == null)
                {
                    _townNetStatus = $"townnet.json ignored ({why}): routes don't use the Town Network";
                    Host.Log($"[RynthNav] {_townNetStatus}");
                    return;
                }
                _townNet = t;
                _townNetStatus = $"{t.Entries.Count} entry portals, {t.Exits.Count} exits (set {t.SetId[..Math.Min(8, t.SetId.Length)]})";
                Host.Log($"[RynthNav] Town Network: {t}");
            }
            catch (Exception ex)
            {
                _townNetStatus = $"townnet.json failed to load ({ex.GetType().Name}: {ex.Message}): routes don't use the Town Network";
                Host.Log($"[RynthNav] {_townNetStatus}");
            }
        });
    }

    /// <summary>The character's level (0 = not known) and whether they're an Olthoi. Tick thread.</summary>
    private void ReadCharacterRules(out int level, out bool olthoi)
    {
        level = 0; olthoi = false;
        uint me = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (me == 0 || !Host.HasGetObjectIntProperty) return;
        if (Host.TryGetObjectIntProperty(me, StypeLevel, out int lv) && lv > 0) level = lv;
        if (Host.TryGetObjectIntProperty(me, StypeHeritageGroup, out int h)) olthoi = h is HeritageOlthoi or HeritageOlthoiAcid;
    }

    /// <summary>The Town Network as this character's route planner sees it; null without townnet.json.</summary>
    private HubLinks? CurrentHubLinks(List<string>? notes)
    {
        TownNet? t = _townNet;
        if (t == null) return null;
        ReadCharacterRules(out int level, out bool olthoi);
        if (notes == null && _hubLinks != null && ReferenceEquals(_hubLinksKey.Net, t) && _hubLinksKey.Level == level && _hubLinksKey.Olthoi == olthoi)
            return _hubLinks;
        HubLinks links = t.Links(level, olthoi, notes);
        _hubLinks = links;
        _hubLinksKey = (t, level, olthoi);
        return links;
    }

    private bool CurrentStepIsHub => _route != null && _routeIdx < _route.Count && _route[_routeIdx].IsHub;

    /// <summary>The exit the route's step <paramref name="i"/> takes (a hub step), or null.</summary>
    private TownNetExit? HubExitOf(int i)
    {
        TownNet? t = _townNet;
        if (t == null || _route == null || i < 0 || i >= _route.Count || !_route[i].IsHub) return null;
        int x = _route[i].HubExit;
        return x >= 0 && x < t.Exits.Count ? t.Exits[x] : null;
    }

    /// <summary>The next step takes the Town Network (this one walks to its entry portal).</summary>
    private bool NextStepIsHub => _route != null && _routeIdx + 1 < _route.Count && _route[_routeIdx + 1].IsHub;

    private static string HubWhere(TownNetExit? x) => x?.Where ?? "the next town";

    private void ResetHub()
    {
        _hubActive = false; _hubLanded = false; _hubWaitSaid = false; _hubSettleSince = 0;
        _hubWalker = null;
    }

    private double LocalX => _wx - ((_cellId >> 24) & 0xFF) * 192.0;
    private double LocalY => _wy - ((_cellId >> 16) & 0xFF) * 192.0;

    private void HubHalt() => AutoRun.Halt();

    /// <summary>One tick of a hub step: wait for the landing inside, walk to the exit, take it.</summary>
    private void StepHub()
    {
        long now = NowMs;
        TownNet? t = _townNet;
        TownNetExit? exit = HubExitOf(_routeIdx);
        if (t == null || exit == null) { FinishGoto("stopped: the Town Network data isn't loaded"); return; }
        if (!_hubActive)
        {
            ResetHub();
            _hubActive = true;
            _hubStepMs = now;
            lock (_gate) _status = $"Town Network → {exit.Where}";
        }
        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        uint lb = CurrentLandblock;
        bool inside = t.Contains(lb);

        if (!_hubLanded)
        {
            // Landed = inside the network, out of portal space, and settled for HubSettleMs. The
            // landblock jump comes BEFORE the client's portal space (10-01 20:19: jump at 38.7 s,
            // portal space 38.9-45.9 s): starting the walk at the jump ran its stuck clock through
            // seven seconds of portal space. RynthAi's nav waits for portal space to end the same way.
            if (portaling || !inside) _hubSettleSince = 0;
            else if (_hubSettleSince == 0) _hubSettleSince = now;
            if (portaling || !inside || now - _hubSettleSince < HubSettleMs)
            {
                HubHalt();
                if (!portaling && now - _hubStepMs > HubLandTimeoutMs)
                    FinishGoto($"stopped: the portal didn't take you into the Town Network (you're in landblock 0x{lb:X4})");
                else if (now - _hubStepMs > HubPortalSpaceTimeoutMs)
                    FinishGoto("stopped: still in portal space a minute after taking the Town Network portal");
                return;
            }
            _hubLanded = true;
            // Normally the landing is now. A goto started inside the network: the last teleport we
            // saw (none seen: assume the cooldown is long over).
            long landMs = _routeIdx > 0 ? _hubSettleSince : _trkLastTeleportMs > 0 ? Math.Min(now, _trkLastTeleportMs) : now - HubWalker.CooldownMs;
            double x = LocalX, y = LocalY;
            List<TownNetPoint>? way = t.WayTo(_route![_routeIdx].HubExit, x, y, HubOffRouteUnits, out string from);
            if (way == null)
            {
                FinishGoto($"stopped in the Town Network: you landed at {x:F1}, {y:F1}, away from every walk to the {exit.Name}");
                return;
            }
            _hubWalker = new HubWalker(way, landMs, now);
            Say($"Town Network: walking to the {exit.Name} ({_hubWalker.Length:F0} m).");
            Host.Log($"[RynthNav] Town Network: landed at {x:F1},{y:F1} (lb 0x{lb:X4}), walk from {from}: {way.Count} points, {_hubWalker.Length:F1} m to {exit.Name}");
        }

        // The exit portal's teleport (portal space can't last for ever: the walker's limit still runs).
        if (portaling)
        {
            HubHalt();
            _hubWalker!.Pause(now);   // portal space isn't being stuck
            if (now > _hubWalker!.LimitMs + HubPortalSpaceTimeoutMs) FinishGoto("stopped: still in portal space a minute after the Town Network's exit");
            return;
        }
        if (!inside)
        {
            HubHalt();
            RefreshTiles(lb);
            if (exit.DestCell != 0 && lb != exit.DestLandblock)
            {
                FinishGoto($"stopped: the Town Network sent you to landblock 0x{lb:X4}, not to {exit.Where} (0x{exit.DestLandblock:X4}): wrong portal");
                return;
            }
            Say($"Town Network: took the {exit.Name}.");
            Host.Log($"[RynthNav] Town Network: took {exit.Name} (lb 0x{lb:X4})");
            ResetHub();
            _lastLandMs = NowMs;   // the exit's own portal space may still follow (see StepGoto)
            if (_routeIdx >= _route!.Count - 1) { FinishGoto("arrived"); return; }
            AdvanceRoute();
            return;
        }

        switch (_hubWalker!.Step(LocalX, LocalY, now, out double heading, out string why))
        {
            case HubWalker.Act.Run:
                DriveTo(heading);
                return;
            case HubWalker.Act.Hold:
                HubHalt();
                if (_hubWalker.JustStartedWaiting && !_hubWaitSaid)
                {
                    _hubWaitSaid = true;
                    Say($"Town Network: waiting {_hubWalker.WaitSeconds} s for the portal to let you through.");
                }
                return;
            case HubWalker.Act.NoTeleport:
            {
                string need = exit.Rules.MinLevel > 0 ? $" (it needs level {exit.Rules.MinLevel})" : " (level or quest requirement?)";
                FinishGoto($"stopped in the Town Network: the {exit.Name} didn't take you{need}");
                return;
            }
            default:
                HubStop(exit, why);
                return;
        }
    }

    private void HubStop(TownNetExit exit, string why) =>
        FinishGoto($"stopped in the Town Network: {why} on the way to the {exit.Name}");

    /// <summary>Metres left on the walk inside (to the exit's centre); -1 before landing.</summary>
    private double HubLeft() => _hubLanded && _hubWalker != null ? _hubWalker.Left(LocalX, LocalY) : -1;

    /// <summary>The panel's status line during a hub step.</summary>
    private string HubLine(double left)
    {
        TownNetExit? exit = HubExitOf(_routeIdx);
        string name = exit?.Name ?? "exit portal";
        if (!_hubLanded) return $"taking the Town Network to {HubWhere(exit)}";
        if (HubIsWaiting) return $"Town Network: waiting for the {name} to let you through";
        return $"Town Network: walking to the {name}{(left >= 0 ? $", {left:F0} yd left" : "")}";
    }

    /// <summary>The route's first Town Network leg, for the goto's start line ("" = none).</summary>
    private string HubStartText()
    {
        if (_route == null) return "";
        for (int i = 0; i < _route.Count; i++)
        {
            if (!_route[i].IsHub) continue;
            TownNetExit? exit = HubExitOf(i);
            if (exit == null) return "";
            if (i == 0) return $"taking the Town Network to {exit.Where} (the {exit.Name})";
            RouteStep entry = _route[i - 1];
            return $"taking the Town Network to {exit.Where}: its portal at {NavCoords.Fmt(entry.Ns, entry.Ew)}, then the {exit.Name} inside";
        }
        return "";
    }

    /// <summary>
    /// A goto started inside the Town Network: there is no navmesh, so the only way on is an exit.
    /// Picks the exit that leaves the least to do (the walk inside, the teleport, then what it costs
    /// from its landing: a route with portals when portal routing is on, else the straight walk).
    /// Null when no exit is usable.
    /// </summary>
    private List<RouteStep>? PlanFromInsideHub(double goalNs, double goalEw, out double est, out int teleports)
    {
        est = 0; teleports = 0;
        TownNet? t = _townNet;
        HubLinks? links = CurrentHubLinks(null);
        if (t == null || links == null) return null;
        double px = LocalX, py = LocalY;
        List<RouteStep>? best = null;
        double bestCost = double.MaxValue;
        int bestUsed = 0;
        if (_portalsEnabled) EnsurePortalsLoaded();
        foreach (HubExit hx in links.Exits)
        {
            List<TownNetPoint>? way = t.WayTo(hx.Id, px, py, HubOffRouteUnits, out _);
            if (way == null) continue;
            double inside = TownNet.PathLength(way, 0) + PortalRoute.PortalPenaltyUnits;
            List<RouteStep> rest;
            double restCost;
            int used = 0;
            if (_portalsEnabled && _portals is { Count: > 0 })
                rest = PortalRoute.Plan(_portals, null, hx.DstNs, hx.DstEw, goalNs, goalEw, out restCost, out used);
            else
            {
                restCost = NavCoords.Distance(hx.DstNs, hx.DstEw, goalNs, goalEw);
                rest = new List<RouteStep> { new(goalNs, goalEw, false, false, "walk") };
            }
            if (inside + restCost >= bestCost) continue;
            bestCost = inside + restCost;
            best = new List<RouteStep> { RouteStep.Hub(hx.DstNs, hx.DstEw, hx.Name, hx.Id) };
            best.AddRange(rest);
            bestUsed = used + 1;
        }
        est = bestCost;
        teleports = bestUsed;
        return best;
    }
}
