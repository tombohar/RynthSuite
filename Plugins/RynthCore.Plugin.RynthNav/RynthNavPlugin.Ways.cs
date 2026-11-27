using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Who may use which portal, what to do when the server says no, and the other ways there.
//  - The planner leaves out a portal the server would refuse this character (PortalRules: its level,
//    player killer status, Olthoi, vitae; NavData's portals.tsv carries each portal's rules). 11-18 a
//    goto routed a non-player killer through the PK-only Olthoi Tunnel and asked it ~40 times.
//  - A portal the server refuses all the same (WeenieError, e.g. "Non-player killers may not interact
//    with that portal!") is left out for a while and the whole trip is planned again from where you
//    stand. So is a teleport that lands somewhere the route didn't expect.
//  - Other ways: each plan keeps up to MaxWays different ways there (PortalRoute.PlanOptions).
//    "/rnav route next" (the panel's Another way) shows the next, and switches a running goto to
//    it; later plans to the same place keep to it (the teleports it avoids stay left out).
// Tick thread.
public sealed partial class RynthNavPlugin
{
    private const uint StypePlayerKillerStatus = 134;
    private const int MaxWays = 4;
    private const int MaxTripReplans = 4;                 // a goto plans again at most this often, then stops
    private const long RefusedForgetMs = 30 * 60 * 1000;  // a refused portal is left out this long
    private const double LandingSlackUnits = 120.0;       // a teleport landing this close to the planned spot is it
    private const long WayChoiceKeepMs = 20 * 60 * 1000;  // a way chosen for a place is kept this long (and until you arrive)

    private const double PortalLevelUnits = 3.0;          // a portal this far above or below you is on another level
    private const long PortalOtherLevelGiveUpMs = 8000;   // at a portal on another level with no way to its floor: give up after this

    private readonly Dictionary<string, (string Name, string Why, long Ms)> _refused = new(StringComparer.Ordinal);

    // The current leg's portal, exactly (portals.tsv's cell and x, y, z): its height, whether it stands
    // indoors, and whether the navmesh reaches its floor from here (null = not looked yet).
    private double _legSpotZ = double.NaN;
    private bool _legSpotIndoor;
    private bool? _legFloorReach;
    private double _portalLiveZ = double.NaN;    // the live portal object's height (SnapToLivePortal)
    private long _portalWaitBeganMs;
    private int _tripReplans;

    // The other ways (the last plan's), and the one chosen.
    private List<TravelPlan.Option> _ways = new();
    private string _waysFor = "", _waysType = "";
    private double _waysNs = double.NaN, _waysEw = double.NaN;
    private int _wayIdx, _wayCount;
    private HashSet<string> _wayBans = new(StringComparer.Ordinal);   // the chosen way's own (beyond the rules)
    private bool _wayWalk;                                             // the chosen way is walking all the way
    private long _wayChosenMs;                                         // when it was chosen (0 = forget it)
    private bool _lbCrossSaid;                                         // "crossed a landblock line at the portal" logged
    private List<string> _wayAvoids = new();

    // ── Who is travelling ────────────────────────────────────────────────────

    /// <summary>The character as the server's portal checks see them. Tick thread.</summary>
    private Traveller ReadTraveller()
    {
        ReadCharacterRules(out int level, out bool olthoi);
        uint pk = 0;
        bool? vitae = null;
        uint me = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (me != 0)
        {
            if (Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(me, StypePlayerKillerStatus, out int s) && s > 0) pk = (uint)s;
            // No PropertyInt 134 (11-20, DreamWeave and Aelrynth): the PK flags every object is sent with.
            if (pk == 0 && Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(me, out uint bf)) pk = PortalRules.StatusFromBitfield(bf);
            if (Host.HasGetVitae) vitae = Host.GetVitae(me) < 0.995f;
        }
        return new Traveller(level, pk, olthoi, vitae);
    }

    /// <summary>
    /// The teleports a plan for <paramref name="t"/> may not take: portals whose rules refuse them, and
    /// portals the server refused them lately. Says why in <paramref name="notes"/>.
    /// </summary>
    private HashSet<string> PlanBans(in Traveller t, List<string>? notes)
    {
        var bans = new HashSet<string>(StringComparer.Ordinal);
        List<PortalLink>? portals = _portals;
        if (portals != null)
        {
            var why = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < portals.Count; i++)
            {
                string w = PortalRules.Refusal(portals[i], t);
                if (w.Length == 0) continue;
                bans.Add(PortalRoute.PortalKey(i));
                why[w] = why.TryGetValue(w, out int c) ? c + 1 : 1;
            }
            if (why.Count > 0)
                notes?.Add($"{bans.Count} portal(s) left out for you ({t}): " + string.Join(", ", why.Select(kv => $"{kv.Key} x{kv.Value}")));
        }
        long now = NowMs;
        List<string>? stale = null;
        foreach (var kv in _refused)
        {
            if (now - kv.Value.Ms >= RefusedForgetMs) { (stale ??= new()).Add(kv.Key); continue; }
            bans.Add(kv.Key);
            notes?.Add($"left out: '{kv.Value.Name}' refused you {(now - kv.Value.Ms) / 60000} min ago ({kv.Value.Why})");
        }
        if (stale != null) foreach (string k in stale) _refused.Remove(k);
        return bans;
    }

    // ── The server says no ───────────────────────────────────────────────────

    /// <summary>
    /// A WeenieError while a goto takes a portal: a refusal that won't change (PortalRules.RefusalFromServer)
    /// leaves that portal out and plans the trip again from here. True when it was ours to handle.
    /// </summary>
    private bool OnPortalRefused(uint error)
    {
        string? why = PortalRules.RefusalFromServer(error);
        if (why == null || !_gotoActive || _route == null || _routeIdx >= _route.Count) return false;
        RouteStep step = _route[_routeIdx];
        bool taking = _portalWait || step.IsHub;
        if (!taking) return false;
        string name = PortalStepName();
        Host.Log($"[RynthNav] portal refused: '{name}' (0x{error:X4}: {why}){(step.Via != null ? $", key {step.Via}" : "")}");
        bool entrance = _enterName.Length > 0 && _routeIdx >= _route.Count - 1;
        if (entrance || step.Via == null)
        {
            FinishGoto($"stopped: the server won't let you through '{name}' ({why})");
            return true;
        }
        _refused[step.Via] = (name, why, NowMs);
        ReplanTrip($"the server won't let you through '{name}' ({why})");
        return true;
    }

    /// <summary>
    /// Plans the running goto again from where you stand (portals refused meanwhile are left out, the
    /// chosen way kept), at most MaxTripReplans times; then stops and says why.
    /// </summary>
    private void ReplanTrip(string why)
    {
        string goal = _gotoName, type = _gotoType, enter = _enterName, enterType = _enterType;
        double gns = NavCoords.NsFromWorld(_finalTns), gew = NavCoords.EwFromWorld(_finalTew);
        if (_tripReplans >= MaxTripReplans)
        {
            FinishGoto($"stopped: {why}, and I've planned the trip again {MaxTripReplans} times already");
            return;
        }
        _tripReplans++;
        Host.Log($"[RynthNav] replan {_tripReplans}/{MaxTripReplans}: {why}");
        Say($"goto: {why}; planning another way from here");
        if (_recallWait) EndRecall();
        StartWalk(goal.Length > 0 ? goal : NavCoords.Fmt(gns, gew), type, gns, gew, enter, enterType, landReplan: true);
    }

    // ── Teleports: did one happen, and did it land where planned ─────────────

    private bool _tjHave;
    private double _tjWx, _tjWy;
    private long _tjMs;

    /// <summary>Forget the last position: the next TeleportJump measures from there.</summary>
    private void ResetTeleportJump() => _tjHave = false;

    /// <summary>
    /// A jump since the last tick too far and too fast to be running: a teleport. A landblock line
    /// crossed while backing off a portal is not one (11-18: 25 u across 7F8F -> 7F8E was taken for
    /// the teleport, and the next leg planned from the wrong side of the world).
    /// </summary>
    private bool TeleportJump(out double jump)
    {
        long now = NowMs;
        jump = 0;
        bool teleport = false;
        if (_tjHave)
        {
            jump = Math.Sqrt((_wx - _tjWx) * (_wx - _tjWx) + (_wy - _tjWy) * (_wy - _tjWy));
            teleport = PortalRules.IsTeleportJump(jump, (now - _tjMs) / 1000.0);
        }
        _tjHave = true; _tjWx = _wx; _tjWy = _wy; _tjMs = now;
        return teleport;
    }

    /// <summary>
    /// After a portal or recall step's teleport: true when it landed where the plan said (or the plan
    /// doesn't say: the Town Network's entry lands inside). Else where it did land.
    /// </summary>
    private bool LandedWherePlanned(out string where)
    {
        where = "";
        if (_route == null || _routeIdx >= _route.Count) return true;
        RouteStep s = _route[_routeIdx];
        if (!(s.UsePortal || s.UseRecall) || double.IsNaN(s.LandNs) || double.IsNaN(s.LandEw)) return true;
        if (!IsOnMap(_cellId)) { where = $"a dungeon (landblock 0x{CurrentLandblock:X4})"; return false; }
        double ns = NavCoords.NsFromWorld(_wy), ew = NavCoords.EwFromWorld(_wx);
        double off = NavCoords.Distance(ns, ew, s.LandNs, s.LandEw);
        if (off <= LandingSlackUnits) return true;
        where = $"{NavCoords.Fmt(ns, ew)}, {off:F0} yd from {NavCoords.Fmt(s.LandNs, s.LandEw)}";
        return false;
    }

    // ── A portal on another level (a cellar, a tunnel under a town) ──────────

    /// <summary>The portal a leg walks to, exactly, when portals.tsv says where it stands.</summary>
    private bool LegPortal(RouteStep s, out PortalLink portal)
    {
        portal = default;
        if (!s.UsePortal || s.Via == null || !s.Via.StartsWith("p", StringComparison.Ordinal) || _portals == null) return false;
        if (!int.TryParse(s.Via.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) || i < 0 || i >= _portals.Count) return false;
        portal = _portals[i];
        return portal.HasSpot;
    }

    /// <summary>A new leg: aim at its portal's exact spot (the map coordinates are 0.1 out, up to 24 u), and its height.</summary>
    private void SetLegSpot(RouteStep s)
    {
        _legSpotZ = double.NaN; _legSpotIndoor = false; _legFloorReach = null; _portalLiveZ = double.NaN;
        if (!LegPortal(s, out PortalLink p)) return;
        _gotoTew = p.WorldEast; _gotoTns = p.WorldNorth;
        _legSpotZ = p.Z; _legSpotIndoor = p.Indoor;
    }

    /// <summary>
    /// The goal polygon for a leg to a portal in a building or below the street: its own floor, when a
    /// walk from here reaches it (down the cellar stairs). Else false: the goal is looked up as before (the
    /// ground nearest, from your height), and taking it is left to using it from there (StepPortalWait
    /// gives up on it after PortalOtherLevelGiveUpMs). Tick thread.
    /// </summary>
    private bool PortalFloorGoal(long sRef, RcVec3f sPt, IDtQueryFilter filter, out long gRef, out RcVec3f gPt)
    {
        gRef = 0; gPt = default;
        if (double.IsNaN(_legSpotZ) || _query == null) return false;
        if (!_legSpotIndoor && Math.Abs(_legSpotZ - _wz) <= PortalLevelUnits) return false;   // out on the ground: as before
        long f = NavPathQuery.PortalFloor(_query, filter, _gotoTew, _legSpotZ, _gotoTns, out RcVec3f fPt);
        bool reach = f != 0 && NavPathQuery.Reaches(_query, sRef, sPt, f, fPt, filter);
        if (_legFloorReach != reach)
        {
            double dz = _legSpotZ - _wz;
            Host.Log(reach
                ? $"[RynthNav] portal '{PortalStepName()}' stands {Math.Abs(dz):F1}u {(dz < 0 ? "below" : "above")} you: walking to its own floor"
                : $"[RynthNav] portal '{PortalStepName()}' stands {Math.Abs(dz):F1}u {(dz < 0 ? "below" : "above")} you{(f == 0 ? " (no navmesh at its floor)" : ", and the navmesh doesn't reach its floor from here")}: going as near as it lets me and using it");
        }
        _legFloorReach = reach;
        if (!reach) return false;
        gRef = f; gPt = fPt;
        return true;
    }

    /// <summary>A portal leg whose portal is on another level, its floor reached by the walk: not there until on that floor.</summary>
    private bool PortalOnOtherLevel => _legFloorReach == true && !double.IsNaN(_legSpotZ) && Math.Abs(_wz - _legSpotZ) > PortalLevelUnits;

    /// <summary>
    /// At the portal, waiting: it stands on another level right under (or over) you, with no way to its
    /// floor, and using it hasn't taken you for PortalOtherLevelGiveUpMs. 11-18 the walker stood 1 u from
    /// Zaikhal's Olthoi Tunnel, 7.7 u above it on the street, and circled it until it was taken over by hand.
    /// Leave the portal out and plan another way (a dungeon's own entrance: stop and say so). True when it gave up.
    /// </summary>
    private bool GiveUpOnOtherLevel(double horizontal)
    {
        double pz = !double.IsNaN(_portalLiveZ) ? _portalLiveZ : _legSpotZ;
        if (double.IsNaN(pz) || horizontal > PortalContactUnits || Math.Abs(pz - _wz) <= PortalLevelUnits) return false;
        if (NowMs - _portalWaitBeganMs < PortalOtherLevelGiveUpMs) return false;
        double dz = pz - _wz;
        string name = PortalStepName();
        string why = $"'{name}' is {Math.Abs(dz):F0} yd {(dz < 0 ? "below" : "above")} you and I can't find the way {(dz < 0 ? "down" : "up")} to it";
        Host.Log($"[RynthNav] portal on another level: {why} ({(NowMs - _portalWaitBeganMs) / 1000.0:F0} s without a teleport)");
        bool entrance = _enterName.Length > 0 && _route != null && _routeIdx >= _route.Count - 1;
        RouteStep? step = _route != null && _routeIdx < _route.Count ? _route[_routeIdx] : null;
        if (entrance || step?.Via == null) { FinishGoto($"stopped: {why}; take it by hand"); return true; }
        _refused[step.Value.Via!] = (name, $"{Math.Abs(dz):F0} yd {(dz < 0 ? "below" : "above")} the ground I can walk", NowMs);
        ReplanTrip(why);
        return true;
    }

    // ── Other ways ───────────────────────────────────────────────────────────

    private bool SameWayTarget(double ns, double ew) =>
        !double.IsNaN(_waysNs) && Math.Abs(ns - _waysNs) < 0.02 && Math.Abs(ew - _waysEw) < 0.02;

    /// <summary>The teleports a route takes from step <paramref name="from"/> on, as PlanOptions' signature.</summary>
    private static string WaySignature(List<RouteStep>? steps, int from = 0)
    {
        if (steps == null) return "";
        var sb = new StringBuilder();
        for (int i = Math.Max(0, from); i < steps.Count; i++)
            if (steps[i].Via != null) { if (sb.Length > 0) sb.Append('|'); sb.Append(steps[i].Via); }
        return sb.ToString();
    }

    /// <summary>A plan to a new place forgets the way chosen for the last one.</summary>
    private void ForgetWayChoice(double goalNs, double goalEw)
    {
        if (SameWayTarget(goalNs, goalEw) && _wayChosenMs > 0 && NowMs - _wayChosenMs < WayChoiceKeepMs) return;
        if (SameWayTarget(goalNs, goalEw) && _wayIdx == 0 && _wayBans.Count == 0 && !_wayWalk) return;   // nothing chosen
        _wayBans = new HashSet<string>(StringComparer.Ordinal);
        _wayAvoids = new List<string>();
        _wayWalk = false;
        _wayIdx = 0; _wayCount = 0;
    }

    /// <summary>Keeps the ways a plan found (PlanRoute), for "another way".</summary>
    private void KeepWays(TravelPlan.Result r, string name, double goalNs, double goalEw, bool chosen)
    {
        _ways = r.Options;
        _waysFor = name; _waysNs = goalNs; _waysEw = goalEw;
        if (!chosen) { _wayIdx = 0; _wayCount = r.Options.Count; }
        _routeVersion++;   // the panel's "way n of m"
    }

    /// <summary>"way 2 of 4 (avoids Olthoi Tunnel)", or "" with one way.</summary>
    private string WayText()
    {
        if (_wayCount <= 1) return "";
        string avoids = _wayIdx > 0 && _wayAvoids.Count > 0 ? $" (avoids {string.Join(", ", _wayAvoids)})" : "";
        return $"way {_wayIdx + 1} of {_wayCount}{avoids}";
    }

    /// <summary>
    /// /rnav route next: the next of the ways to the last place planned for, planned again from here.
    /// A running goto to it switches to that way; otherwise it is shown (the panel, the arrow, chat).
    /// </summary>
    private void CmdRouteNext()
    {
        if (!_hasPose) { Note("no player pose yet"); return; }
        if (double.IsNaN(_waysNs) || _waysFor.Length == 0) { Note("another way to where? Plan a route first (/rnav route <place>, or Route here on the map)"); return; }
        if (_townNet?.Contains(CurrentLandblock) == true) { Note("you're in the Town Network: pick another way once you're out of it"); return; }
        bool walking = _gotoActive && SameWayTarget(NavCoords.NsFromWorld(_finalTns), NavCoords.EwFromWorld(_finalTew));
        string current = walking ? WaySignature(_route, _routeIdx) : WaySignature(_shownRoute);
        string name = _waysFor, type = _waysType;
        double ns = _waysNs, ew = _waysEw;

        // Every way from here, the choice left out.
        TravelPlan.Result all = PlanRoute(name, ns, ew, out _, ignoreChoice: true);
        if (all.Refusal.Length > 0 || all.Options.Count == 0) { Note($"route to {name}: {(all.Refusal.Length > 0 ? all.Refusal : "no way there")}"); return; }
        if (all.Options.Count == 1)
        {
            Note($"route to {name}: there's only the one way from here");
            _wayBans = new HashSet<string>(StringComparer.Ordinal); _wayAvoids = new List<string>(); _wayWalk = false;
            KeepWays(all, name, ns, ew, chosen: false);
            return;
        }
        int at = all.Options.FindIndex(o => o.Signature == current);
        int next = at < 0 ? (current == all.Options[0].Signature ? 1 : 0) : (at + 1) % all.Options.Count;
        TravelPlan.Option pick = all.Options[next];
        _ways = all.Options;
        _wayIdx = next; _wayCount = all.Options.Count;
        _wayBans = new HashSet<string>(pick.Bans, StringComparer.Ordinal);
        _wayAvoids = new List<string>(pick.Avoids);
        _wayWalk = next > 0 && pick.Teleports == 0;
        _wayChosenMs = NowMs;
        string text = WayText();
        Host.Log($"[RynthNav] another way to {name}: {(text.Length > 0 ? text : "way 1")}, ~{pick.Est:F0}u, {pick.Teleports} teleport(s)");

        if (walking)
        {
            Say($"goto: switching to {(text.Length > 0 ? text : "the first way")}");
            _nextEnterPortal = _enterPortal;   // the same entrance portal (a saved nav route names it)
            StartWalk(name, _gotoType, ns, ew, _enterName, _enterType);
            return;
        }
        ShowRoute(name, type, ns, ew);
    }

    /// <summary>/rnav route go: walk the way shown (the last place planned for, the way chosen).</summary>
    private void CmdRouteGo()
    {
        if (double.IsNaN(_waysNs) || _waysFor.Length == 0) { Note("go which way? Plan a route first (/rnav route <place>)"); return; }
        StartGoto(_waysFor, _waysType, _waysNs, _waysEw);
    }

    /// <summary>The route's lines in chat, and the way it is, after a plan or "another way".</summary>
    private void SayRoute(string name, double ns, double ew, List<RouteStep>? steps, int used, double est)
    {
        string way = WayText();
        if (steps == null || used == 0)
        {
            Note(_portalsEnabled
                ? $"route to {name}: walk ({NavCoords.FmtDistance(est)}, {AboutTime(_planTimes, est)}{(way.Length > 0 ? "; " + way : "; no portal or recall helps")})"
                : $"route to {name}: walk ({NavCoords.FmtDistance(est)}; portals and recalls are off)");
            if (_wayCount > 1) Say($"  {_wayCount} ways in all: /rnav route next for another");
            return;
        }
        _goNote = $"route to {name}: {used} teleport(s), {steps.Count} step(s)";
        string eta = AboutTime(_planTimes, est);
        Say($"route to {name} {NavCoords.Fmt(ns, ew)}: {(eta.Length > 0 ? eta + ", " : "")}{used} teleport(s) (~{est:F0} yd-equivalent){(way.Length > 0 ? ", " + way : "")}:");
        for (int i = 0; i < steps.Count; i++)
        {
            RouteStep s = steps[i];
            string act = s.UseRecall ? $"recall: {s.Label}"
                : s.IsHub ? $"in the Town Network, walk to the {s.Label} and take it to {NavCoords.Fmt(s.LandNs, s.LandEw)}"
                : s.UsePortal ? $"walk to {NavCoords.Fmt(s.Ns, s.Ew)}, take '{Trunc(s.Label)}'" : $"walk to {NavCoords.Fmt(s.Ns, s.Ew)}";
            Say($"  {i + 1}. {act}");
        }
        if (_wayCount > 1) Say($"  {_wayCount} ways in all: /rnav route next for another, /rnav route go to walk this one");
    }

    /// <summary>
    /// Plans (with the way chosen) and shows the route to a place: the panel, the arrow, chat. The plan
    /// is the target now (RynthNavPlugin.Target.cs): the arrow points along it, and a walk to somewhere
    /// else ends. A walk to the same place goes on with its own arrow.
    /// </summary>
    private void ShowRoute(string name, string type, double ns, double ew)
    {
        HeldTarget held = Held();
        TargetChange change = TargetSwitch.For(NewTarget.Plan, held, ns, ew);
        string ended = ApplyTargetChange(change, held);
        List<RouteStep>? steps = PlanAndShow(name, ns, ew, out int used, out double est, out string refusal);
        _waysType = type;
        if (refusal.Length > 0) { Note($"route to {name}: {refusal}{(ended.Length > 0 ? " (" + ended + ")" : "")}"); return; }
        if (!change.KeepArrow)
        {
            if (steps != null && used > 0) _arrow.SetRoute(steps, name, type, ns, ew);
            else _arrow.SetTarget(name, type, ns, ew);
        }
        if (ended.Length > 0) Say($"route to {name}: {ended}");
        SayRoute(name, ns, ew, steps, used, est);
    }

    /// <summary>The route JSON's way fields: which of how many, and what it avoids.</summary>
    private void AppendWayJson(StringBuilder sb)
    {
        sb.Append(",\"way\":").Append(_wayIdx + 1).Append(",\"ways\":").Append(_wayCount)
          .Append(",\"avoids\":\"").Append(Esc(_wayIdx > 0 ? string.Join(", ", _wayAvoids) : "")).Append('"');
    }
}
