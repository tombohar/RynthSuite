using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// /ra dunnav-explore (2026-11-13): one walk through every room of the dungeon the character is in,
/// then stop. Laid like the patrol (TryBuildDunPatrol: out of lava/acid and portal circles first, the
/// first point in sight, round the portals), walked by the navigation engine as a Once route with the
/// macro on, so fighting, looting and buffing go on as the settings say. The walk itself
/// (DungeonPathfinder.Explore.cs) enters each room it hasn't entered yet, nearest first.
/// Here: the rooms entered (the cells the character stands in, each tick), the reroutes the patrol
/// makes (a hazard sighted, a portal come into view, the dungeon map loaded after the cell walk was
/// laid) laid over the rooms still to go, a second look for rooms the walk got past without entering,
/// and the end: done (the walk is over), or stopped (the command, another route, out of the dungeon).
/// Navigation goes off at the end; the macro is left as it is. radunexplore[] and the two counts
/// (ExpressionEngine.DunExploreStatus) tell a meta or a Lua script how far it is.
///
/// Shut doors (2026-11-14): a door the character can't use (ItemUseable without Remote: in ACE's data
/// every door a lever opens) or one the door opener gave up on (locked, or still shut after its uses)
/// is a shut door: its opening is closed for the walk, which goes round it, and the rooms behind it are
/// counted. When everything else is walked and rooms are left behind shut doors, the explore waits at
/// the doors (state 5) for a lever: /ra dunnav-explore lever &lt;id&gt; walks to it (through no shut door),
/// uses it (or steps onto a pressure plate) and watches the door. Opened: the walk is laid again with
/// the rooms behind that door first (it shuts again on its own); not opened: back to waiting, the
/// result in radunexploreinfo[]. Which lever is the caller's choice (a script or a meta);
/// RynthAi only walks, uses and watches. A shut door that opens by itself does the same as a lever.
///
/// Stuck (2026-11-14, Tom on DreamWeave: "a spider web thing, definitely not in the dats" in a doorway of
/// 0x5456): the map is the cells' walls and openings only, so a thing spawned in an opening isn't on it, and
/// the navigation engine's detours (planned on the same map) went back through it; after its last detour it
/// only tries its escapes, for good. When every detour for the waypoint is used up and it is still stuck
/// (NavigationEngine.DetoursUsedUpAndStuck), the explore closes the opening it was heading through (the first
/// step from the character's cell towards the next waypoint in another cell) and lays the rest round it; rooms
/// only that way are left out (couldn't be reached). At most DunExploreMaxStuckOpenings a run.
/// </summary>
public sealed partial class RynthAiPlugin
{
    internal enum DunExploreState { None = 0, Exploring = 1, Paused = 2, Done = 3, Stopped = 4, AtDoors = 5, Lever = 6 }

    /// <summary>The explore starts at the first of this many points in sight (the patrol looks along its whole lap).</summary>
    private const int ExploreSightLookahead = 4;
    /// <summary>The walk over with rooms still not entered: laid again for them this many times (each needs a new room entered since).</summary>
    private const int DunExploreMaxSweeps = 2;
    /// <summary>How often a running explore looks for a new portal, a shut door or the dungeon map.</summary>
    private const long DunExploreCheckMs = 1000;
    /// <summary>Openings closed because the character couldn't get through them, at most this many a run.</summary>
    private const int DunExploreMaxStuckOpenings = 6;
    /// <summary>Stuck for good farther than this from the doorway on its way: the thing in the way is where it stands.</summary>
    internal const double StuckAtDoorwayMeters = 3.0;
    /// <summary>The spot walked round then: this far ahead of it, towards its waypoint, and this big.</summary>
    internal const double StuckSpotAhead = 1.5, StuckSpotRadius = 1.0;
    /// <summary>How far along the route the next waypoint in another cell is looked for.</summary>
    private const int StuckLookAheadPoints = 8;

    /// <summary>Tests: the navigation engine's "every detour used up and still stuck" (null: the engine's own).
    /// The explore and the patrol both ask it.</summary>
    internal Func<bool>? ExploreStuckProbe;

    private bool StuckForGood() => ExploreStuckProbe?.Invoke() ?? _navigationEngine?.DetoursUsedUpAndStuck ?? false;

    /// <summary>ItemUseable's Remote bit (ACE Usable.Remote): a door without it can't be used from where you stand.</summary>
    private const int UsableRemote = 0x20;
    /// <summary>A lever is used from this close (the server walks the character the last bit); a plate is stepped on.</summary>
    private const double LeverReachMeters = 2.5, LeverUseFromMeters = 6.0, PlateReachMeters = 0.9;
    /// <summary>The door is watched this long after a lever (a plate) before it counts as not opened.</summary>
    // A door shows open about 4.5 s after its lever is pulled (DreamWeave Thread 0x5873, 2026-11-16: lever 0x75873008,
    // door 0x75873009). A second pull before that flips it shut again (2.5 s here before: it "opened" and shut 5 s
    // later, twice), so the lever is pulled again only when the door still hasn't opened well after that.
    private const long LeverWatchMs = 12_000, PlateWatchMs = 4000, LeverReuseMs = 8000, LeverErrandMaxMs = 120_000;

    internal sealed class ExploreDoor
    {
        public uint Id;
        public string Name = "";
        public uint Cell;
        /// <summary>World metres.</summary>
        public double X, Y, Z;
        public readonly HashSet<ulong> Edges = new();
        /// <summary>"can't be used", "locked", "wouldn't open".</summary>
        public string Why = "";
        /// <summary>The cells walkable to with this door open but not with it shut, and their rooms.</summary>
        public HashSet<uint> CellsBehind = new();
        public int RoomsBehind;
    }

    internal sealed class LeverErrand
    {
        public uint Lever, Door;
        public string LeverName = "";
        public bool Plate;
        /// <summary>World metres.</summary>
        public double X, Y, Z;
        public string Phase = "walking";
        public long StartedAt, PhaseAt;
        public int Uses;
    }

    internal sealed class DunExploreRun
    {
        public DunExploreState State = DunExploreState.Exploring;
        public uint Landblock;
        public NavRouteParser? Route;
        public ExplorePlan Plan = new();
        /// <summary>The cells the character has stood in on this explore (what a reroute leaves out).</summary>
        public readonly HashSet<uint> VisitedCells = new();
        /// <summary>The rooms of <see cref="Plan"/> those cells are in.</summary>
        public readonly HashSet<int> EnteredRooms = new();
        public readonly HashSet<uint> PortalIds = new();
        /// <summary>Openings it couldn't get through (closed for the walk), and what was said about each.</summary>
        public readonly HashSet<ulong> StuckEdges = new();
        /// <summary>Spots in a room it got stuck at for good, away from any doorway: walked round like a portal's circle.</summary>
        public readonly List<PatrolPortal> StuckSpots = new();
        public readonly List<string> StuckNotes = new();
        /// <summary>Shut doors the walk goes round, and their openings (closed in the graph).</summary>
        public readonly Dictionary<uint, ExploreDoor> Doors = new();
        public readonly HashSet<ulong> DoorEdges = new();
        /// <summary>Doors found in no opening (left alone: logged once).</summary>
        public readonly HashSet<uint> DoorsNowhere = new();
        /// <summary>Things standing in a doorway that the map doesn't have (a spider web), id → name, and the doorways'
        /// cell pairs (closed for the walk); things in no doorway (left alone); names of things it got stuck on in a
        /// doorway this run (each other one of that name in a doorway is walked round too).</summary>
        public readonly Dictionary<uint, string> Blockers = new();
        public readonly HashSet<ulong> BlockedEdges = new();
        public readonly HashSet<uint> BlockersNowhere = new();
        public readonly HashSet<string> BlockerNames = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Monsters still in the dungeon when the walk was over, walked to one at a time (each once), and how many.</summary>
        public readonly HashSet<int> HuntTried = new();
        public int HuntWalks;
        /// <summary>Behind a door that just opened: walked first, until the character is through.</summary>
        public readonly HashSet<uint> PreferCells = new();
        public LeverErrand? Errand;
        /// <summary>The last lever's result: "opened &lt;door&gt; &lt;lever&gt;", "notopened ...", "unreachable ...".</summary>
        public string LastLever = "";
        public uint LastCell;
        public int HazardVersion;
        public int Sweeps, EnteredAtSweep = -1;
        public long StartedAt, EndedAt, NextCheckAt;
        public string EndNote = "";
        /// <summary>The shut doors (and rooms behind) last said in chat when it came to wait at them.</summary>
        public string AtDoorsSaid = "";

        /// <summary>The rooms it means to walk through, and those of them (or cut off since) entered.</summary>
        public int Rooms => Plan.PlanRooms.Count + EnteredRooms.Count(r => !Plan.PlanRooms.Contains(r));
        public int Entered => EnteredRooms.Count;
        public int RoomsBehindDoors => Doors.Values.Sum(d => d.RoomsBehind);
        public bool Active => State is DunExploreState.Exploring or DunExploreState.Paused or DunExploreState.AtDoors or DunExploreState.Lever;

        /// <summary>A cell stood in: its room counts as entered; past a door that opened, the walk stops preferring it.</summary>
        public void Enter(uint cell)
        {
            if (PreferCells.Contains(cell)) PreferCells.Clear();
            if (!VisitedCells.Add(cell)) return;
            int r = Plan.RoomOf(cell);
            if (r >= 0) EnteredRooms.Add(r);
        }

        /// <summary>After a new plan: the rooms entered, in its numbering.</summary>
        public void Recount()
        {
            EnteredRooms.Clear();
            foreach (uint c in VisitedCells)
            {
                int r = Plan.RoomOf(c);
                if (r >= 0) EnteredRooms.Add(r);
            }
        }

        public void Shut(ExploreDoor d)
        {
            Doors[d.Id] = d;
            DoorEdges.UnionWith(d.Edges);
        }

        public void Unshut(uint id)
        {
            if (!Doors.Remove(id)) return;
            DoorEdges.Clear();
            foreach (var d in Doors.Values) DoorEdges.UnionWith(d.Edges);
        }
    }

    private DunExploreRun? _dunExplore;
    /// <summary>Doors the door opener gave up on (id → why), for the explore's next look.</summary>
    private readonly Dictionary<uint, string> _exploreRefusedDoors = new();

    /// <summary>The door opener gave up on a door (DoorInteractionController): a shut door to an explore.</summary>
    private void NoteExploreDoorRefused(uint doorId, string why)
    {
        if (_dunExplore is not { Active: true } || doorId == 0) return;
        _exploreRefusedDoors[doorId] = why;
        _dunExplore.NextCheckAt = 0;   // looked at on the next tick
    }

    /// <summary>For radunexplore[], radunexplorerooms[], radunexplorevisited[].</summary>
    private (int State, int Rooms, int Visited) DunExploreStatus()
    {
        var run = _dunExplore;
        return run == null ? (0, 0, 0) : ((int)run.State, run.Rooms, run.Entered);
    }

    private const string DunExploreUsage = "[RynthAi] Usage: /ra dunnav-explore [stop|status|resume|done] | lever <id> [door <id>] [at <x> <y> <z>] [plate]  — walk through every room of this dungeon once, then stop";

    private void HandleDunExploreCommand(string[] parts)
    {
        string sub = parts.Length > 2 ? parts[2].Trim().ToLowerInvariant() : "";
        var run = _dunExplore;
        switch (sub)
        {
            case "":
            case "start":
                if (run is { Active: true })
                {
                    ReplyOk($"[RynthAi] DunNav-Explore: already exploring, {run.Entered} of {run.Rooms} rooms. /ra dunnav-explore stop ends it.");
                    return;
                }
                StartDunExplore();
                return;
            case "resume":
                ResumeDunExplore(run);
                return;
            case "stop":
                if (run is not { Active: true }) { ReplyFail("[RynthAi] DunNav-Explore: nothing to stop (no explore running)."); return; }
                EndDunExplore(run, DunExploreState.Stopped, "you stopped it", clearRoute: true);
                return;
            case "done":
                if (run is not { State: DunExploreState.AtDoors }) { ReplyFail("[RynthAi] DunNav-Explore: \"done\" ends an explore waiting at shut doors; none is."); return; }
                EndDunExplore(run, DunExploreState.Done, "", clearRoute: true);
                return;
            case "lever":
                StartLeverErrand(run, parts.Skip(3).ToArray());
                return;
            case "status":
                ReplyOk(DunExploreStatusLine());
                return;
            default:
                ReplyFail(DunExploreUsage);
                return;
        }
    }

    private string DunExploreStatusLine()
    {
        var run = _dunExplore;
        if (run == null) return "[RynthAi] DunNav-Explore: none yet. /ra dunnav-explore walks through every room of the dungeon you are in.";
        string took = Took(run);
        string doors = run.Doors.Count > 0 ? $", {run.RoomsBehindDoors} room(s) behind {run.Doors.Count} shut door(s)" : "";
        return run.State switch
        {
            DunExploreState.Exploring => $"[RynthAi] DunNav-Explore: exploring dungeon 0x{run.Landblock:X4}, {run.Entered} of {run.Rooms} rooms{doors}, {took}.",
            DunExploreState.Paused    => $"[RynthAi] DunNav-Explore: paused (macro or navigation off), {run.Entered} of {run.Rooms} rooms{doors}. Start the macro to carry on.",
            DunExploreState.AtDoors   => $"[RynthAi] DunNav-Explore: waiting at shut doors, {run.Entered} of {run.Rooms} rooms{doors}. /ra dunnav-explore lever <id> pulls one; /ra dunnav-explore done ends it.",
            DunExploreState.Lever     => $"[RynthAi] DunNav-Explore: at a lever ({run.Errand?.LeverName} 0x{run.Errand?.Lever:X8}, {run.Errand?.Phase}), {run.Entered} of {run.Rooms} rooms{doors}.",
            DunExploreState.Done      => $"[RynthAi] DunNav-Explore: done in dungeon 0x{run.Landblock:X4}, {run.Entered} of {run.Rooms} rooms in {took}{doors}.",
            _                         => $"[RynthAi] DunNav-Explore: stopped ({run.EndNote}), {run.Entered} of {run.Rooms} rooms{doors}. /ra dunnav-explore resume carries on.",
        };
    }

    private static string Took(DunExploreRun run)
    {
        long end = run.EndedAt != 0 ? run.EndedAt : Environment.TickCount64;
        var t = TimeSpan.FromMilliseconds(Math.Max(0, end - run.StartedAt));
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";
    }

    private void StartDunExplore()
    {
        var run = new DunExploreRun { StartedAt = Environment.TickCount64 };
        ScanShutDoorsInto(run, CurrentLandblockOrZero());
        ScanBlockersInto(run, CurrentLandblockOrZero());
        if (!BuildDunExplore(run, isRebuild: false, why: "")) return;
        _dunExplore = run;
    }

    /// <summary>/ra dunnav-explore resume: the last explore, in this dungeon, carries on (the rooms entered and the shut doors kept).</summary>
    private void ResumeDunExplore(DunExploreRun? run)
    {
        uint lb = CurrentLandblockOrZero();
        if (run == null || run.Landblock != lb || lb == 0)
        {
            ReplyFail("[RynthAi] DunNav-Explore: nothing to resume in this dungeon; /ra dunnav-explore starts one.");
            return;
        }
        if (run.State is DunExploreState.Exploring or DunExploreState.Paused or DunExploreState.Lever)
        {
            ReplyOk($"[RynthAi] DunNav-Explore: already exploring, {run.Entered} of {run.Rooms} rooms.");
            return;
        }
        run.Errand = null;
        run.EndedAt = 0;
        run.Sweeps = 0;
        run.EnteredAtSweep = -1;
        ScanShutDoorsInto(run, lb);
        ScanBlockersInto(run, lb);
        BuildDunExplore(run, isRebuild: false, why: "resume");
    }

    private uint CurrentLandblockOrZero()
        => Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) && (cell & 0xFFFF) >= 0x0100 ? cell >> 16 : 0;

    /// <summary>
    /// Lays the explore's walk from where the character stands over the rooms it hasn't entered and
    /// installs it. False when nothing was installed: refused (said), or nothing left to walk to (the
    /// explore waits at shut doors, or is done).
    /// </summary>
    private bool BuildDunExplore(DunExploreRun run, bool isRebuild, string why)
    {
        bool resume = why == "resume";
        if (Host.TryGetPlayerPose(out uint here, out _, out _, out _, out _, out _, out _, out _)
            && (here & 0xFFFF) >= 0x0100 && (!isRebuild || (here >> 16) == run.Landblock))
            run.VisitedCells.Add(here);

        var b = TryBuildDunPatrol("dunnav-explore", out string error, out var refusal, explore: run);
        if (b == null)
        {
            if (!isRebuild)
            {
                if (refusal == DunBuildRefusal.Policy) ChatLine(error); else ReplyFail(error);
                return false;
            }
            // The rebuild couldn't be laid. Out of the dungeon, or the server says no: it ends. The client
            // not ready (no position, the dats): the old walk goes on, and the next change tries again.
            if (refusal is DunBuildRefusal.NotInDungeon or DunBuildRefusal.Policy or DunBuildRefusal.EmptyRoute)
                EndDunExplore(run, DunExploreState.Stopped, refusal == DunBuildRefusal.Policy ? "the server asks for no dungeon patrols here" : "no walk from here", clearRoute: true);
            Host.Log($"[RynthAi] DunNav-Explore: rebuild ({why}) not laid: {error}");
            return false;
        }

        var settings = _dashboard!.Settings;
        run.Plan = b.Plan ?? new ExplorePlan();
        run.Landblock = b.World.Landblock;
        run.HazardVersion = _objectCache?.HazardVersion ?? 0;
        run.PortalIds.Clear();
        foreach (var p in b.World.Portals) run.PortalIds.Add(p.Id);
        run.NextCheckAt = Environment.TickCount64 + DunExploreCheckMs;
        run.Recount();
        if (here != 0) run.LastCell = here;
        MeasureDoorsBehind(run, b.World, b.StartCell);

        var route = b.Route;
        if (route.Points.Count == 0)
        {
            // Every room it can walk to is entered: done, or waiting at the shut doors.
            if (!isRebuild && !resume && run.Doors.Values.All(d => d.RoomsBehind == 0))
            {
                run.State = DunExploreState.Done;
                run.EndedAt = Environment.TickCount64;
                _dunExplore = run;
                ReplyOk($"[RynthAi] DunNav-Explore: nothing to walk to: {run.Rooms} room(s) here, and you are in {(run.Rooms == 1 ? "it" : "all of them")}.");
                return false;
            }
            _dunExplore = run;
            FinishWalk(run);
            return false;
        }

        if (!isRebuild)
        {
            if (!settings.IsMacroRunning && RefuseMacroStartForVTank()) return false;   // one bot per client (Decal bridge)
            settings.IsMacroRunning = true;
            // As the patrol: the explore picked its own start; the macro-start "nearest waypoint" pick
            // on the next tick must not move it (on a walk that ends it would skip rooms).
            _macroWasRunning = true;
            // A dungeon patrol running stands down without its "ended" line: the explore is the route now.
            _dunPatrolActive = false;
            _dunPatrolRoute = null;
        }
        settings.CurrentNavPath   = string.Empty;
        settings.CurrentRoute     = route;
        settings.ActiveNavIndex   = b.StartIdx;
        settings.EnableNavigation = true;
        run.Route = route;
        run.Errand = null;
        run.State = DunExploreState.Exploring;

        int left = run.Rooms - run.Entered;
        string layout = run.Plan.ByRoom ? "" : " (dungeon map not loaded yet: cell by cell, rooms once it is)";
        Host.Log($"[RynthAi] DunNav-Explore: {(isRebuild ? "relaid (" + why + ")" : resume ? "resumed" : "started")} in 0x{run.Landblock:X4}: {route.Points.Count} points from {b.StartIdx}, "
                 + $"{run.Entered} of {run.Rooms} rooms entered, {run.Plan.Order.Count} to go{layout}, sweep {run.Sweeps}, "
                 + $"{run.Doors.Count} shut door(s) ({run.RoomsBehindDoors} rooms behind){(run.PreferCells.Count > 0 ? ", behind the opened door first" : "")}");
        if (!isRebuild)
        {
            int hazardCount = b.World.Hazards?.Count ?? 0;
            string hazardNote = hazardCount > 0 ? $", {hazardCount} lava/acid cell(s) left out" : "";
            string portalNote = b.World.Portals.Count > 0 ? $", kept out of {b.World.Portals.Count} portal(s)" : "";
            string doorNote = run.Doors.Count > 0 ? $", round {run.Doors.Count} shut door(s)" : "";
            ReplyOk($"[RynthAi] DunNav-Explore: {left} room(s) to walk through{layout}, {route.Points.Count} waypoints{hazardNote}{portalNote}{doorNote} → "
                    + (resume ? "exploring again" : "exploring") + "; it stops at the end. Fighting, looting and buffing follow your settings.");
        }
        return true;
    }

    /// <summary>The walk is over: monsters still in the dungeon walked to (enter and scan's sweep), then waiting at the
    /// shut doors when rooms are behind them, else done.</summary>
    private void FinishWalk(DunExploreRun run)
    {
        if (WalkToLeftoverMonster(run)) return;
        var blocking = run.Doors.Values.Where(d => d.RoomsBehind > 0).OrderByDescending(d => d.RoomsBehind).ToList();
        if (blocking.Count == 0)
        {
            EndDunExplore(run, DunExploreState.Done, "", clearRoute: true);
            return;
        }
        var settings = _dashboard?.Settings;
        if (settings != null)
        {
            // Navigation off, and an empty route of its own, so the explore keeps owning it while it waits.
            settings.EnableNavigation = false;
            settings.CurrentNavPath = string.Empty;
            settings.CurrentRoute = new NavRouteParser();
            settings.ActiveNavIndex = 0;
            _navigationEngine?.Stop();
            run.Route = settings.CurrentRoute;
        }
        run.State = DunExploreState.AtDoors;
        run.Errand = null;
        // Said once per set of doors (a lever that didn't do it comes back here without a new line).
        string said = string.Join(",", blocking.Select(d => $"{d.Id:X8}:{d.RoomsBehind}"));
        if (said == run.AtDoorsSaid) return;
        run.AtDoorsSaid = said;
        string list = string.Join(", ", blocking.Take(4).Select(d => $"{d.Name} 0x{d.Id:X8} ({d.RoomsBehind})"));
        if (blocking.Count > 4) list += ", ...";
        Host.Log($"[RynthAi] DunNav-Explore: at the shut doors in 0x{run.Landblock:X4}: {string.Join("; ", blocking.Select(d => $"0x{d.Id:X8} '{d.Name}' {d.Why}, {d.RoomsBehind} rooms, cell {d.Cell:X8}"))}");
        ChatLine($"[RynthAi] DunNav-Explore: every room it can walk to is done ({run.Entered} of {run.Rooms}); {run.RoomsBehindDoors} room(s) are behind "
                 + $"{blocking.Count} shut door(s): {list}. /ra dunnav-explore lever <id> pulls a lever; /ra dunnav-explore done ends it.");
    }

    /// <summary>At most this many walks to monsters left in the dungeon, a run.</summary>
    private const int DunExploreMaxHunts = 12;
    /// <summary>The monsters looked for at the end are this close (yards; the dungeon is one landblock).</summary>
    private const double DunExploreHuntYd = 400;

    /// <summary>
    /// The sweep at the end of enter and scan (2026-11-16): a monster the fight would take (combat's hunt list: alive,
    /// attackable, not blacklisted, on the monster list) still in this dungeon with a position the client knows (one in
    /// a corner the walk only looked past; Thread 0x00B5's boss) is walked to, the nearest first, by the dungeon map;
    /// the fight takes it on when it's in range. Each once; one it can't be walked to is left. True: a walk installed.
    /// </summary>
    /// <summary>Tests: the monsters combat would fight (null: combat's own hunt list).</summary>
    internal Func<List<OutdoorHunt.Candidate>>? LeftoverMonstersProbe;

    private bool WalkToLeftoverMonster(DunExploreRun run)
    {
        if (run.HuntWalks >= DunExploreMaxHunts || run.State != DunExploreState.Exploring) return false;
        var settings = _dashboard?.Settings;
        if (settings == null || !settings.IsMacroRunning) return false;
        var found = LeftoverMonstersProbe?.Invoke();
        if (found == null)
        {
            if (_combatManager == null) return false;
            found = new List<OutdoorHunt.Candidate>();
            _combatManager.HuntCandidates(DunExploreHuntYd, found);
        }
        var left = found.Where(c => (c.Cell >> 16) == run.Landblock && (c.Cell & 0xFFFF) >= 0x0100 && !run.HuntTried.Contains(c.Id)).ToList();
        if (left.Count == 0) return false;
        DunRouteWorld? w = null;
        foreach (var c in left.Take(4))
        {
            run.HuntTried.Add(c.Id);
            if (!Host.TryGetObjectPosition(unchecked((uint)c.Id), out uint cell, out float x, out float y, out float z) || (cell >> 16) != run.Landblock) continue;
            if (w == null)
            {
                w = TryGetDunWorld("dunnav-explore", "DunNav-Explore", out _, out _, out _);
                if (w == null) return false;
                foreach (var closed in new[] { run.DoorEdges, run.StuckEdges, run.BlockedEdges })
                    if (closed.Count > 0) w.Graph = DungeonPathfinder.WithoutOpenings(w.Graph, closed, w.StartCell);
            }
            var b = DungeonRouteMaker.TryBuildRoute(w, new DunTarget(DunTargetKind.Position, X: x, Y: y, Z: z), out string error);
            if (b == null || b.Route.Points.Count == 0)
            {
                Host.Log($"[RynthAi] DunNav-Explore: '{c.Name}' 0x{c.Id:X8} in cell {cell:X8} is still here but can't be walked to ({error}): left");
                continue;
            }
            run.HuntWalks++;
            settings.CurrentNavPath = string.Empty;
            settings.CurrentRoute = b.Route;
            settings.ActiveNavIndex = 0;
            settings.EnableNavigation = true;
            run.Route = b.Route;
            int more = left.Count - 1;
            Host.Log($"[RynthAi] DunNav-Explore: the walk is over and '{c.Name}' 0x{c.Id:X8} is still here, {c.Distance:F0} yd away in cell {cell:X8}: "
                     + $"walking to it ({b.Route.Points.Count} points; {more} more known)");
            ChatLine($"[RynthAi] DunNav-Explore: {c.Name} is still in the dungeon ({c.Distance:F0} yd) → going after it"
                     + (more > 0 ? $"; {more} more after that." : "."));
            return true;
        }
        return false;
    }

    private void ClearExploreRoute(DunExploreRun run)
    {
        var settings = _dashboard?.Settings;
        if (settings == null || !ReferenceEquals(settings.CurrentRoute, run.Route)) return;
        settings.EnableNavigation = false;
        settings.CurrentRoute = new NavRouteParser();
        settings.ActiveNavIndex = 0;
        _navigationEngine?.Stop();
    }

    /// <summary>Ends the explore: done or stopped, one chat line. <paramref name="clearRoute"/>: its walk
    /// is taken off and navigation switched off (not when another route has taken its place).</summary>
    private void EndDunExplore(DunExploreRun run, DunExploreState state, string note, bool clearRoute)
    {
        if (!run.Active && run.State == state) return;
        run.State = state;
        run.EndedAt = Environment.TickCount64;
        run.EndNote = note;
        run.Errand = null;
        if (clearRoute) ClearExploreRoute(run);
        var settings = _dashboard?.Settings;
        bool macro = settings?.IsMacroRunning == true;
        int left = run.Rooms - run.Entered;
        Host.Log($"[RynthAi] DunNav-Explore: {state} ({note}) in 0x{run.Landblock:X4}: {run.Entered} of {run.Rooms} rooms, {Took(run)}, sweeps {run.Sweeps}, "
                 + $"{run.Doors.Count} shut door(s) ({run.RoomsBehindDoors} rooms behind)");
        if (state == DunExploreState.Done)
        {
            string missed = left > 0 ? $" ({left} couldn't be reached)" : "";
            string doors = run.RoomsBehindDoors > 0 ? $"; {run.RoomsBehindDoors} more behind {run.Doors.Count(d => d.Value.RoomsBehind > 0)} shut door(s)" : "";
            ChatLine($"[RynthAi] DunNav-Explore: done, {run.Entered} of {run.Rooms} rooms in {Took(run)}{missed}{doors}. Navigation is off; "
                     + (macro ? "the macro is still on (it fights and loots as set)." : "the macro is off."));
        }
        else
        {
            ChatLine($"[RynthAi] DunNav-Explore stopped ({note}): {run.Entered} of {run.Rooms} rooms entered."
                     + (clearRoute && macro ? " Navigation is off; the macro is still on." : ""));
        }
    }

    // ── Shut doors ───────────────────────────────────────────────────────────

    /// <summary>
    /// The doors in <paramref name="landblock"/> that are shut to the character: closed, and either not
    /// usable from where you stand (a lever opens them) or given up on by the door opener. New ones are
    /// added to the run (and their openings closed); ones that have opened come back as
    /// <paramref name="opened"/>. True when anything changed.
    /// </summary>
    private bool ScanShutDoorsInto(DunExploreRun run, uint landblock, List<ExploreDoor>? opened = null)
    {
        if (_objectCache == null || landblock == 0 || !Host.HasGetObjectPosition) return false;
        bool changed = false;
        Dictionary<uint, DungeonNavNode>? graph = null;
        DungeonGeometry? geo = null;
        bool mapRead = false;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            bool puzzle = IsPuzzleGate(uid);
            if (!puzzle && !IsDoorObject(wo, uid)) continue;
            if (!Host.TryGetObjectPosition(uid, out uint cell, out float x, out float y, out float z) || cell == 0) continue;
            if ((cell >> 16) != landblock || (cell & 0xFFFF) < 0x0100) continue;
            bool open = IsDoorOpen(wo.Id);
            if (run.Doors.TryGetValue(uid, out var known))
            {
                if (open)
                {
                    opened?.Add(known);
                    run.Unshut(uid);
                    _exploreRefusedDoors.Remove(uid);
                    changed = true;
                    Host.Log($"[RynthAi] DunNav-Explore: shut door 0x{uid:X8} '{known.Name}' is open now");
                }
                continue;
            }
            if (open || run.DoorsNowhere.Contains(uid)) continue;
            string? why = null;
            int usable = _objectCache.GetIntProperty(wo.Id, 16, -1);   // ItemUseable
            if (puzzle) why = "a Thread puzzle gate";
            else if (usable >= 0 && (usable & UsableRemote) == 0) why = "can't be used";
            else if (_exploreRefusedDoors.TryGetValue(uid, out string? refused)) why = refused;
            if (why == null) continue;

            if (!mapRead)
            {
                mapRead = true;
                var map = DungeonMapOverride?.Invoke(landblock);
                var cellDat = DungeonMapOverride == null ? _raycast?.GeometryLoader.CellDat : null;
                graph = map?.Graph ?? (cellDat is { IsLoaded: true } ? DungeonPathfinder.GetGraph(landblock, cellDat) : null);
                geo = map != null ? map.Value.Geo : cellDat is { IsLoaded: true } ? DungeonPathfinder.GetGeometry(landblock, cellDat, _raycast?.GeometryLoader?.DungeonLOS) : null;
            }
            if (graph == null) return changed;
            double wx = ((cell >> 24) & 0xFF) * 192.0 + x, wy = ((cell >> 16) & 0xFF) * 192.0 + y;
            var door = new ExploreDoor { Id = uid, Name = wo.Name ?? "Door", Cell = cell, X = wx, Y = wy, Z = z, Why = why };
            door.Edges.UnionWith(DungeonPathfinder.DoorOpenings(graph, geo, cell, wx, wy, z));
            if (door.Edges.Count == 0)
            {
                run.DoorsNowhere.Add(uid);
                Host.Log($"[RynthAi] DunNav-Explore: door 0x{uid:X8} '{door.Name}' ({why}) stands in no opening the map knows: left alone");
                continue;
            }
            run.Shut(door);
            changed = true;
            Host.Log($"[RynthAi] DunNav-Explore: shut door 0x{uid:X8} '{door.Name}' ({why}) at ({x:F1},{y:F1},{z:F1}) cell {cell:X8}: "
                     + $"{door.Edges.Count} opening(s) closed for the walk");
        }
        return changed;
    }

    // DreamWeave's Thread puzzle gates (PuzzleGateTunables: door, garrison, reinforced, Olthoi, barrier panel, arch hall):
    // spawned (not a static id), locked, opened or destroyed only by solving their levers' puzzle. A wall to the walk.
    // Their levers (wcid 1006851) are never pulled: three wrong pulls in an hour ban the connection from Threads.
    private static readonly HashSet<uint> PuzzleGateWcids = new() { 1006850, 1006853, 1006854, 1006855, 1006856, 1007507 };
    private const uint PuzzleLeverWcid = 1006851;

    private bool IsPuzzleGate(uint uid)
        => Host.HasGetObjectWcid && Host.TryGetObjectWcid(uid, out uint wcid) && PuzzleGateWcids.Contains(wcid);

    /// <summary>A door as the door opener tells one (DoorInteractionController): a static object whose weenie
    /// bitfield says door (BF_DOOR 0x1000), or by its name when the engine can't read bitfields.</summary>
    private bool IsDoorObject(WorldObject wo, uint uid)
    {
        if (uid >= 0x80000000u) return false;
        if (Host.HasGetObjectBitfield) return Host.TryGetObjectBitfield(uid, out uint bf) && (bf & 0x1000u) != 0;
        string n = (wo.Name ?? string.Empty).ToLowerInvariant();
        return n.Contains("door") || n.Contains("gate") || n.Contains("hatch") || n.Contains("portcullis");
    }

    /// <summary>For each shut door, the cells (and rooms) behind it: walkable to with it open, not with it shut.</summary>
    private static void MeasureDoorsBehind(DunExploreRun run, DunRouteWorld w, uint startCell)
    {
        if (run.Doors.Count == 0) return;
        var baseGraph = w.Graph;
        var now = DungeonPathfinder.ReachableCells(DungeonPathfinder.WithoutOpenings(baseGraph, run.DoorEdges, startCell), startCell, w.Hazards);
        foreach (var d in run.Doors.Values)
        {
            var others = new HashSet<ulong>(run.Doors.Values.Where(o => o.Id != d.Id).SelectMany(o => o.Edges));
            var with = DungeonPathfinder.ReachableCells(DungeonPathfinder.WithoutOpenings(baseGraph, others, startCell), startCell, w.Hazards);
            with.ExceptWith(now);
            d.CellsBehind = with;
            var rooms = new HashSet<int>();
            foreach (uint c in with) { int r = run.Plan.RoomOf(c); rooms.Add(r >= 0 ? r : -(int)(c & 0x7FFF) - 1); }
            d.RoomsBehind = rooms.Count;
        }
    }

    // ── Levers ───────────────────────────────────────────────────────────────

    /// <summary>/ra dunnav-explore lever &lt;id&gt; [door &lt;id&gt;] [at &lt;x&gt; &lt;y&gt; &lt;z&gt;] [plate]</summary>
    private void StartLeverErrand(DunExploreRun? run, string[] args)
    {
        if (run is not { Active: true }) { ReplyFail("[RynthAi] DunNav-Explore: a lever is pulled for an explore; none is running (/ra dunnav-explore starts one)."); return; }
        if (run.State == DunExploreState.Lever) { ReplyFail($"[RynthAi] DunNav-Explore: already at a lever (0x{run.Errand?.Lever:X8})."); return; }
        if (args.Length == 0 || !TryParseId(args[0], out uint lever))
        {
            ReplyFail("[RynthAi] Usage: /ra dunnav-explore lever <id> [door <id>] [at <x> <y> <z>] [plate]  (ids in hex; x y z landblock-local metres)");
            return;
        }
        uint door = 0;
        bool plate = false;
        double? ax = null, ay = null, az = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "door" && i + 1 < args.Length && TryParseId(args[i + 1], out uint d)) { door = d; i++; }
            else if (a == "plate") plate = true;
            else if (a == "at" && i + 3 < args.Length
                     && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double px)
                     && double.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out double py)
                     && double.TryParse(args[i + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out double pz))
            { ax = px; ay = py; az = pz; i += 3; }
            else { ReplyFail($"[RynthAi] DunNav-Explore: \"{args[i]}\" isn't part of a lever command. " + DunExploreUsage); return; }
        }

        // A Thread puzzle lever is never pulled: a wrong pull resets the puzzle, and three in an hour ban the
        // connection from Threads (DreamWeave ThreadPuzzleFailPolicy).
        if (Host.HasGetObjectWcid && Host.TryGetObjectWcid(lever, out uint leverWcid) && leverWcid == PuzzleLeverWcid)
        {
            LeverResult(run, $"puzzle {lever:X8} {door:X8}", $"0x{lever:X8} is a Thread puzzle lever: the explore never pulls one (wrong pulls ban you from Threads)");
            return;
        }

        // Where the lever is: the client's own position for it, else the one given (ACE's data).
        double lx, ly, lz;
        string name = _objectCache?[unchecked((int)lever)]?.Name is { Length: > 0 } n ? n : (plate ? "Pressure Plate" : "Lever");
        if (Host.TryGetObjectPosition(lever, out uint lcell, out float ox, out float oy, out float oz) && lcell != 0 && (lcell >> 16) == run.Landblock)
        { lx = ox; ly = oy; lz = oz; }
        else if (ax.HasValue) { lx = ax.Value; ly = ay!.Value; lz = az!.Value; }
        else
        {
            LeverResult(run, $"notfound {lever:X8} {door:X8}", $"lever 0x{lever:X8} isn't in view and no position was given (at <x> <y> <z>)");
            return;
        }

        var w = TryGetDunWorld("dunnav-explore lever", "DunNav-Explore", out _, out string error, out _);
        if (w == null) { ReplyFail(error); return; }
        if (run.DoorEdges.Count > 0) w.Graph = DungeonPathfinder.WithoutOpenings(w.Graph, run.DoorEdges, w.StartCell);
        var b = DungeonRouteMaker.TryBuildRoute(w, new DunTarget(DunTargetKind.Position, X: lx, Y: ly, Z: lz), out error);
        if (b == null || b.Route.Points.Count == 0)
        {
            LeverResult(run, $"unreachable {lever:X8} {door:X8}", $"{name} 0x{lever:X8} can't be walked to from here ({error})");
            return;
        }

        var settings = _dashboard!.Settings;
        if (!settings.IsMacroRunning)
        {
            if (RefuseMacroStartForVTank()) return;
            settings.IsMacroRunning = true;
            _macroWasRunning = true;
        }
        settings.CurrentNavPath = string.Empty;
        settings.CurrentRoute = b.Route;
        settings.ActiveNavIndex = 0;
        settings.EnableNavigation = true;
        run.Route = b.Route;
        long now = Environment.TickCount64;
        uint lb = run.Landblock;
        run.Errand = new LeverErrand
        {
            Lever = lever, Door = door, LeverName = name, Plate = plate,
            X = ((lb >> 8) & 0xFF) * 192.0 + lx, Y = (lb & 0xFF) * 192.0 + ly, Z = lz,
            StartedAt = now, PhaseAt = now,
        };
        run.State = DunExploreState.Lever;
        string forDoor = door != 0 && run.Doors.TryGetValue(door, out var sd) ? $" for {sd.Name} 0x{door:X8} ({sd.RoomsBehind} room(s) behind it)" : door != 0 ? $" for door 0x{door:X8}" : "";
        Host.Log($"[RynthAi] DunNav-Explore: lever errand: {name} 0x{lever:X8}{forDoor} at ({lx:F1},{ly:F1},{lz:F1}){(plate ? " (a plate: stepped on)" : "")}, {b.Route.Points.Count} points, {b.Yards:F0} yd");
        ReplyOk($"[RynthAi] DunNav-Explore: going to {(plate ? "step on" : "pull")} {name} 0x{lever:X8}{forDoor}, {b.Yards:F0} yd.");
    }

    private static bool TryParseId(string s, out uint id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    /// <summary>A lever that didn't do it: the result kept for radunexploreinfo[], and the walk goes on (or waits at the doors).</summary>
    private void LeverResult(DunExploreRun run, string result, string said)
    {
        run.LastLever = result;
        run.Errand = null;
        ChatLine($"[RynthAi] DunNav-Explore: {said}.");
        Host.Log($"[RynthAi] DunNav-Explore: lever result: {result} ({said})");
        if (run.State == DunExploreState.Lever || run.State == DunExploreState.AtDoors)
        {
            // Carry on with whatever is left to walk (nothing: back to waiting at the doors).
            run.State = DunExploreState.Exploring;
            BuildDunExplore(run, isRebuild: true, why: "after a lever");
        }
    }

    /// <summary>The lever errand, once a tick: walk, use (or stand on the plate), watch the door.</summary>
    private void TickLeverErrand(DunExploreRun run, LegacyUiSettings settings, uint cell, double px, double py, double pz)
    {
        var e = run.Errand!;
        long now = Environment.TickCount64;
        double d2 = Math.Sqrt((px - e.X) * (px - e.X) + (py - e.Y) * (py - e.Y));
        double dz = Math.Abs(pz - e.Z);
        if (now - e.StartedAt > LeverErrandMaxMs)
        {
            LeverResult(run, $"unreachable {e.Lever:X8} {e.Door:X8}", $"couldn't get to {e.LeverName} 0x{e.Lever:X8} in {LeverErrandMaxMs / 1000} s");
            return;
        }
        if (e.Phase == "walking")
        {
            if (!settings.IsMacroRunning) return;   // paused with the macro
            bool routeOver = run.Route == null || settings.ActiveNavIndex >= run.Route.Points.Count;
            double reach = e.Plate ? PlateReachMeters : LeverReachMeters;
            if ((d2 > reach || dz > 2.5) && !routeOver) return;
            settings.EnableNavigation = false;
            _navigationEngine?.Stop();
            if (e.Plate)
            {
                if (d2 > 1.5 || dz > 2.5)
                {
                    LeverResult(run, $"unreachable {e.Lever:X8} {e.Door:X8}", $"couldn't step onto {e.LeverName} 0x{e.Lever:X8} ({d2:F1} m off)");
                    return;
                }
            }
            else
            {
                if (d2 > LeverUseFromMeters || dz > 3.0)
                {
                    LeverResult(run, $"unreachable {e.Lever:X8} {e.Door:X8}", $"couldn't get to {e.LeverName} 0x{e.Lever:X8} ({d2:F1} m off)");
                    return;
                }
                e.Uses++;
                Host.UseFor(e.Lever, "Explore", $"pull the lever for shut door 0x{e.Door:X8}", UseKind.Asked);
            }
            e.Phase = "waiting";
            e.PhaseAt = now;
            return;
        }

        // Waiting: the door (or, with none named, any shut door) open?
        ExploreDoor? openedDoor = null;
        if (e.Door != 0)
        {
            if (IsDoorOpen(unchecked((int)e.Door)))
                openedDoor = run.Doors.TryGetValue(e.Door, out var kd) ? kd : new ExploreDoor { Id = e.Door, Name = "door" };
        }
        else openedDoor = run.Doors.Values.FirstOrDefault(d => IsDoorOpen(unchecked((int)d.Id)));
        if (openedDoor != null)
        {
            var behind = new HashSet<uint>(openedDoor.CellsBehind);
            run.Unshut(openedDoor.Id);
            _exploreRefusedDoors.Remove(openedDoor.Id);
            run.LastLever = $"opened {openedDoor.Id:X8} {e.Lever:X8}";
            run.Errand = null;
            run.PreferCells.Clear();
            run.PreferCells.UnionWith(behind);
            ChatLine($"[RynthAi] DunNav-Explore: {e.LeverName} 0x{e.Lever:X8} opened {openedDoor.Name} 0x{openedDoor.Id:X8} → through it first ({openedDoor.RoomsBehind} room(s) behind it).");
            run.State = DunExploreState.Exploring;
            BuildDunExplore(run, isRebuild: true, why: "a lever opened a door");
            return;
        }
        if (!e.Plate && e.Uses < 2 && now - e.PhaseAt > LeverReuseMs)
        {
            e.Uses++;
            Host.UseFor(e.Lever, "Explore", $"pull the lever for shut door 0x{e.Door:X8} (again)", UseKind.Asked);
        }
        if (now - e.PhaseAt > (e.Plate ? PlateWatchMs : LeverWatchMs))
            LeverResult(run, $"notopened {e.Lever:X8} {e.Door:X8}", $"{e.LeverName} 0x{e.Lever:X8} didn't open {(e.Door != 0 ? $"door 0x{e.Door:X8}" : "a shut door")}");
    }

    // ── The tick ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Once per tick while an explore runs: the room the character is in counts as entered; the walk
    /// over means a second look for rooms missed, then waiting at the shut doors or done; another route
    /// or the dungeon left ends it; a hazard, a new portal, a shut door (or one opening) or the map
    /// loading lays the rest again; a lever errand walks, uses and watches.
    /// </summary>
    private void TickDunExplore()
    {
        var run = _dunExplore;
        if (run is not { Active: true }) return;
        var settings = _dashboard?.Settings;
        if (settings == null) return;

        if (ServerPolicy.PatrolOff(Host))
        {
            EndDunExplore(run, DunExploreState.Stopped, "the server asks for no dungeon patrols here", clearRoute: true);
            ChatLine(ServerPolicy.PatrolOffMessage);
            return;
        }

        // Another route took the explore's place (a nav file, a meta's route, the patrol): it ends, that route stays.
        if (!ReferenceEquals(settings.CurrentRoute, run.Route) || !string.IsNullOrEmpty(settings.CurrentNavPath))
        {
            string what = _dunPatrolActive && _dunPatrolRoute != null && ReferenceEquals(settings.CurrentRoute, _dunPatrolRoute)
                ? "the dungeon patrol" : PatrolReplacementName(settings);
            EndDunExplore(run, DunExploreState.Stopped, $"{what} is the route now", clearRoute: false);
            return;
        }

        if (!Host.TryGetPlayerPose(out uint cell, out float lx, out float ly, out float pz, out _, out _, out _, out _) || cell == 0) return;
        if ((cell >> 16) != run.Landblock || (cell & 0xFFFF) < 0x0100)
        {
            EndDunExplore(run, DunExploreState.Stopped, "you left the dungeon", clearRoute: true);
            return;
        }
        if (cell != run.LastCell)
        {
            run.LastCell = cell;
            run.Enter(cell);
        }

        if (run.State == DunExploreState.Lever && run.Errand != null)
        {
            TickLeverErrand(run, settings, cell, ((cell >> 24) & 0xFF) * 192.0 + lx, ((cell >> 16) & 0xFF) * 192.0 + ly, pz);
            return;
        }

        long now = Environment.TickCount64;
        if (run.State == DunExploreState.AtDoors)
        {
            // A shut door that opens (someone pulled its lever, it opened on its own): the walk goes through it.
            if (now < run.NextCheckAt) return;
            run.NextCheckAt = now + DunExploreCheckMs;
            var opened = new List<ExploreDoor>();
            ScanShutDoorsInto(run, run.Landblock, opened);
            if (opened.Count > 0)
            {
                run.PreferCells.Clear();
                foreach (var d in opened) run.PreferCells.UnionWith(d.CellsBehind);
                ChatLine($"[RynthAi] DunNav-Explore: {opened[0].Name} 0x{opened[0].Id:X8} is open → through it first.");
                run.State = DunExploreState.Exploring;
                BuildDunExplore(run, isRebuild: true, why: "a shut door opened");
            }
            return;
        }

        var route = run.Route!;
        if (route.Points.Count == 0 || settings.ActiveNavIndex >= route.Points.Count)
        {
            // The walk is over (the navigation engine switched navigation off at its end).
            int left = run.Rooms - run.Entered;
            if (left > 0 && run.Sweeps < DunExploreMaxSweeps && run.Entered > run.EnteredAtSweep)
            {
                run.Sweeps++;
                run.EnteredAtSweep = run.Entered;
                ChatLine($"[RynthAi] DunNav-Explore: {left} room(s) passed by without going in → going back for them.");
                if (BuildDunExplore(run, isRebuild: true, why: "rooms missed")) return;
                if (!run.Active || run.State == DunExploreState.AtDoors) return;
            }
            FinishWalk(run);
            return;
        }

        bool running = settings.IsMacroRunning && settings.EnableNavigation;
        run.State = running ? DunExploreState.Exploring : DunExploreState.Paused;
        if (!running) return;

        // Stuck for good where the map says the way is open: close that opening and go round it.
        if (StuckForGood()
            && run.StuckNotes.Count < DunExploreMaxStuckOpenings
            && CloseStuckOpening(run, settings, cell, lx, ly, pz))
        {
            BuildDunExplore(run, isRebuild: true, why: "couldn't get through");
            return;
        }

        bool hazards = _objectCache != null && _objectCache.HazardVersion != run.HazardVersion;
        bool portal = false, map = false, doors = false, blocked = false;
        if (now >= run.NextCheckAt)
        {
            run.NextCheckAt = now + DunExploreCheckMs;
            portal = ExplorePortalAppeared(run);
            map = !run.Plan.ByRoom && ExploreMapReady(run.Landblock);
            var opened = new List<ExploreDoor>();
            doors = ScanShutDoorsInto(run, run.Landblock, opened);
            if (opened.Count > 0)
            {
                run.PreferCells.Clear();
                foreach (var d in opened) run.PreferCells.UnionWith(d.CellsBehind);
            }
            blocked = ScanBlockersInto(run, run.Landblock);
        }
        if (!hazards && !portal && !map && !doors && !blocked) return;

        string why = hazards ? "new hazard" : doors ? "shut door" : blocked ? "something in a doorway" : portal ? "portal came into view" : "dungeon map loaded";
        int before = run.Entered;
        if (BuildDunExplore(run, isRebuild: true, why) && hazards)
            ChatLine($"[RynthAi] DunNav-Explore: new hazard sighted → rerouted around it ({before} of {run.Rooms} rooms entered).");
    }

    /// <summary>The explore's side of a way it can't get through: closed for its walk, said in chat.</summary>
    private bool CloseStuckOpening(DunExploreRun run, LegacyUiSettings settings, uint cell, float lx, float ly, float z)
    {
        var closed = run.BlockedEdges.Count == 0 ? run.StuckEdges : new HashSet<ulong>(run.StuckEdges.Concat(run.BlockedEdges));
        if (!TryFindStuckOpening(run.Route, run.Landblock, closed, run.StuckNotes.Count, settings.ActiveNavIndex, cell, lx, ly, z,
                                 "DunNav-Explore", out var keys, out string note, out var inIt, out var spot))
            return false;
        if (spot != null)
        {
            run.StuckSpots.Add(spot);
            run.StuckNotes.Add(note);
            ChatLine($"[RynthAi] DunNav-Explore: couldn't get past something at {note} (not on the dungeon map) → walking round that spot.");
            return true;
        }
        run.StuckEdges.UnionWith(keys);
        run.StuckNotes.Add(note);
        // What stands in that doorway (named in the log): any other one of its name in a doorway is walked round.
        foreach (string n in inIt)
            if (run.BlockerNames.Add(n))
                Host.Log($"[RynthAi] DunNav-Explore: '{n}' stood in the doorway it couldn't get through: each other '{n}' in a doorway is walked round");
        ChatLine($"[RynthAi] DunNav-Explore: couldn't get past something between {note} (not on the dungeon map) → going round it; rooms only that way are left out.");
        return true;
    }

    /// <summary>
    /// The opening the character was heading through when it got stuck for good (the explore and the patrol):
    /// the first step from its cell towards the next waypoint in another cell (within StuckLookAheadPoints of
    /// <paramref name="activeIdx"/>), not one of <paramref name="closed"/>. False when there is none (the next
    /// points are all in this cell, no map, no way). <paramref name="keys"/>: the opening's cell pairs not closed
    /// yet; <paramref name="stuckCount"/>: the openings closed before it (for the log).
    /// </summary>
    private bool TryFindStuckOpening(NavRouteParser? route, uint landblock, IReadOnlySet<ulong> closed, int stuckCount, int activeIdx,
                                     uint cell, float lx, float ly, float z, string label, out HashSet<ulong> keys, out string note,
                                     out List<string> inDoorway, out PatrolPortal? spot)
    {
        keys = new HashSet<ulong>();
        note = "";
        inDoorway = new List<string>();
        spot = null;
        if (route == null || route.Points.Count == 0) return false;
        var map = DungeonMapOverride?.Invoke(landblock);
        var cellDat = DungeonMapOverride == null ? _raycast?.GeometryLoader.CellDat : null;
        var graph = map?.Graph ?? (cellDat is { IsLoaded: true } ? DungeonPathfinder.GetGraph(landblock, cellDat) : null);
        if (graph == null || graph.Count == 0) return false;
        var geo = map != null ? map.Value.Geo : DungeonPathfinder.GetGeometry(landblock, cellDat!, _raycast?.GeometryLoader?.DungeonLOS);
        double wx = ((cell >> 24) & 0xFF) * 192.0 + lx, wy = ((cell >> 16) & 0xFF) * 192.0 + ly;
        uint from = graph.ContainsKey(cell) ? cell : DungeonPathfinder.NearestCell(graph, DungeonGeometry.WorldToNav(wy), DungeonGeometry.WorldToNav(wx), z);
        if (from == 0) return false;

        uint toward = 0;
        // A Circular route (the patrol) goes on past its end; a Once route (the explore) stops there.
        int n = route.Points.Count;
        int at = Math.Clamp(activeIdx, 0, n - 1);
        bool loop = route.RouteType == NavRouteType.Circular;
        for (int k = 0; k < StuckLookAheadPoints && (loop ? k < n : at + k < n); k++)
        {
            uint c = DungeonPathfinder.CellAt(graph, geo, route.Points[(at + k) % n]);
            if (c != 0 && c != from) { toward = c; break; }
        }
        if (toward == 0)
        {
            if (geo != null && StuckSpot(route, at, from, wx, wy, z, label, lx, ly, "with the next points in its cell too", out spot, out note)) return true;
            Host.Log($"[RynthAi] {label}: stuck in cell {from:X8} with the next {StuckLookAheadPoints} points in it too: nothing to close");
            return false;
        }
        var open = closed.Count > 0 ? DungeonPathfinder.WithoutOpenings(graph, new HashSet<ulong>(closed), from) : graph;
        var path = DungeonPathfinder.FindPath(from, toward, open, null);
        if (path.Count < 2) return false;
        uint a = path[0], b = path[1];
        // Stuck in a room, away from the doorway on its way (2026-11-16, a DreamWeave hall 0x01F5: 5 and 11 m from it;
        // three openings were closed one after another and it was stuck as before): the thing in the way is where it
        // stands, not in that doorway. A spot just ahead of it is walked round instead.
        if (geo != null && geo.TryGetDoorway(a, b, out var onWay))
        {
            double doorDist = Math.Sqrt((onWay.X - wx) * (onWay.X - wx) + (onWay.Y - wy) * (onWay.Y - wy));
            if (doorDist > StuckAtDoorwayMeters
                && StuckSpot(route, at, from, wx, wy, z, label, lx, ly, $"{doorDist:F0} m from the doorway on its way", out spot, out note))
                return true;
        }
        // Every cell pair through that opening (a cell stacked on another at a room's edge gives it two:
        // DungeonPathfinder.OpeningPairs), or the walk laid again goes through the same gap.
        keys = DungeonPathfinder.OpeningPairs(graph, geo, a, b);
        keys.ExceptWith(closed);
        if (keys.Count == 0) return false;
        note = $"cells {a:X8} and {b:X8}";
        string twins = keys.Count > 1 ? $" and {keys.Count - 1} more cell pair(s) through the same opening" : "";
        string seen = geo != null && geo.TryGetDoorway(a, b, out var dw) ? ThingsInDoorway(landblock, dw, inDoorway) : "";
        Host.Log($"[RynthAi] {label}: stuck for good at ({lx:F1},{ly:F1},{z:F1}) heading for waypoint {at} (cell {toward:X8}): "
                 + $"closing the opening between {note}{twins} ({stuckCount + 1} closed){seen}");
        return true;
    }

    /// <summary>The spot just ahead of the character (StuckSpotAhead towards the waypoint it heads for, StuckSpotRadius
    /// across) it got stuck at, to walk round (a PatrolPortal: the walk keeps out of its circle). False with the waypoint
    /// on top of it.</summary>
    private bool StuckSpot(NavRouteParser route, int at, uint cell, double wx, double wy, double z, string label, float lx, float ly,
                           string where, out PatrolPortal? spot, out string note)
    {
        spot = null;
        note = "";
        var p = route.Points[at];
        double tx = DungeonGeometry.NavToWorld(p.EW) - wx, ty = DungeonGeometry.NavToWorld(p.NS) - wy, tl = Math.Sqrt(tx * tx + ty * ty);
        if (tl < 0.5) return false;
        double ahead = Math.Min(StuckSpotAhead, tl);
        spot = new PatrolPortal
        {
            Name = "a spot it got stuck at", Cell = cell,
            X = wx + tx / tl * ahead, Y = wy + ty / tl * ahead, Z = z, Radius = StuckSpotRadius,
        };
        note = $"a spot in cell {cell:X8}";
        Host.Log($"[RynthAi] {label}: stuck for good at ({lx:F1},{ly:F1},{z:F1}) heading for waypoint {at}, {where}: "
                 + $"walking round the spot ({spot.X:F1},{spot.Y:F1}) {StuckSpotRadius:F1} m round it");
        return true;
    }

    // ── Things in doorways ───────────────────────────────────────────────────
    // Things a server puts in a doorway are on no map: the walk goes through and the character runs into one until
    // the stuck fallback gives the doorway up a minute later (TryFindStuckOpening). (Thread 0x5456's spider webs,
    // 2026-11-14 and 11-16, turned out to be the cells' own placed objects: the map closes those itself,
    // DungeonPathfinder.Blocked.cs.) A thing named like a web standing in a doorway, and in this run anything of a
    // name it got stuck on in a doorway, closes that doorway before it is walked. The rooms only that way are left
    // out, as behind a doorway it couldn't get through.

    /// <summary>A doorway's middle is this close to a thing (horizontally) for it to stand in the doorway.</summary>
    private const double BlockerInDoorwayMeters = 1.5;

    private static bool NamedLikeWeb(string name) => name.IndexOf("web", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Not a thing left standing in a doorway: a creature, a person, a door (the shut-door scan has those),
    /// a portal, a corpse, a lifestone, a pet.</summary>
    private static bool CanBlockDoorway(WorldObject wo) => wo.ObjectClass is not (AcObjectClass.Monster or AcObjectClass.Player
        or AcObjectClass.Npc or AcObjectClass.Vendor or AcObjectClass.Door or AcObjectClass.Portal or AcObjectClass.Corpse
        or AcObjectClass.Lifestone or AcObjectClass.CombatPet);

    /// <summary>
    /// The things in <paramref name="landblock"/> that stand in a doorway the walk would go through: named like a web,
    /// or a name in run.BlockerNames. New ones close their doorway (every cell pair of it) for the walk. True when
    /// anything was added.
    /// </summary>
    private bool ScanBlockersInto(DunExploreRun run, uint landblock)
    {
        if (_objectCache == null || landblock == 0 || !Host.HasGetObjectPosition) return false;
        bool changed = false;
        Dictionary<uint, DungeonNavNode>? graph = null;
        DungeonGeometry? geo = null;
        bool mapRead = false;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (run.Blockers.ContainsKey(uid) || run.BlockersNowhere.Contains(uid)) continue;
            string name = wo.Name ?? "";
            if (name.Length == 0 || !CanBlockDoorway(wo) || !(NamedLikeWeb(name) || run.BlockerNames.Contains(name))) continue;
            if (!Host.TryGetObjectPosition(uid, out uint cell, out float x, out float y, out float z) || cell == 0) continue;
            if ((cell >> 16) != landblock || (cell & 0xFFFF) < 0x0100) continue;
            if (!mapRead)
            {
                mapRead = true;
                var map = DungeonMapOverride?.Invoke(landblock);
                var cellDat = DungeonMapOverride == null ? _raycast?.GeometryLoader.CellDat : null;
                graph = map?.Graph ?? (cellDat is { IsLoaded: true } ? DungeonPathfinder.GetGraph(landblock, cellDat) : null);
                geo = map != null ? map.Value.Geo : cellDat is { IsLoaded: true } ? DungeonPathfinder.GetGeometry(landblock, cellDat, _raycast?.GeometryLoader?.DungeonLOS) : null;
            }
            if (graph == null || geo == null) return changed;
            double wx = ((cell >> 24) & 0xFF) * 192.0 + x, wy = ((cell >> 16) & 0xFF) * 192.0 + y;
            var edges = DungeonPathfinder.DoorwayAt(graph, geo, wx, wy, z, BlockerInDoorwayMeters, out var dw);
            if (edges.Count == 0 || dw == null)
            {
                run.BlockersNowhere.Add(uid);
                Host.Log($"[RynthAi] DunNav-Explore: 0x{uid:X8} '{name}' at ({x:F1},{y:F1},{z:F1}) cell {cell:X8} stands in no doorway: left alone");
                continue;
            }
            run.Blockers[uid] = name;
            run.BlockedEdges.UnionWith(edges);
            changed = true;
            Host.Log($"[RynthAi] DunNav-Explore: 0x{uid:X8} '{name}' ({wo.ObjectClass}) at ({x:F1},{y:F1},{z:F1}) cell {cell:X8} stands in the doorway "
                     + $"between cells {dw.CellA:X8} and {dw.CellB:X8} ({edges.Count} cell pair(s)): closed for the walk");
            ChatLine($"[RynthAi] DunNav-Explore: {name} in the doorway between cells {dw.CellA:X8} and {dw.CellB:X8} → going round it; rooms only that way are left out.");
        }
        return changed;
    }

    /// <summary>For the stuck log: the things (not creatures, people, doors...) standing in <paramref name="dw"/>;
    /// the names of those within BlockerInDoorwayMeters of its middle go to <paramref name="names"/>.</summary>
    private string ThingsInDoorway(uint landblock, DungeonDoorway dw, List<string> names)
    {
        if (_objectCache == null || !Host.HasGetObjectPosition) return "";
        var parts = new List<string>();
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (!CanBlockDoorway(wo) || !Host.TryGetObjectPosition(uid, out uint cell, out float x, out float y, out float z) || (cell >> 16) != landblock) continue;
            double wx = ((cell >> 24) & 0xFF) * 192.0 + x, wy = ((cell >> 16) & 0xFF) * 192.0 + y;
            double h = Math.Sqrt((wx - dw.X) * (wx - dw.X) + (wy - dw.Y) * (wy - dw.Y));
            if (h > 3.0 || Math.Abs(z - dw.Z) > 3.0) continue;
            string name = wo.Name ?? "";
            parts.Add($"0x{uid:X8} '{name}' ({wo.ObjectClass}) {h:F1} m from its middle");
            if (h <= BlockerInDoorwayMeters && name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
        return parts.Count == 0 ? "; nothing the client knows stands in it" : "; in it: " + string.Join(", ", parts.Take(6));
    }

    private bool ExplorePortalAppeared(DunExploreRun run)
    {
        foreach (var p in PatrolPortalsIn(run.Landblock))
        {
            if (run.PortalIds.Contains(p.Id)) continue;
            Host.Log($"[RynthAi] DunNav-Explore: portal 0x{p.Id:X8} '{p.Name}' came into view at ({p.X:F1},{p.Y:F1},{p.Z:F1}) cell {p.Cell:X8}: laying the rest round it");
            return true;
        }
        return false;
    }

    private bool ExploreMapReady(uint landblock)
    {
        if (DungeonMapOverride != null) return DungeonMapOverride(landblock)?.Geo is { DoorwayCount: > 0 };
        var cellDat = _raycast?.GeometryLoader?.CellDat;
        if (cellDat == null || !cellDat.IsLoaded) return false;
        return DungeonPathfinder.GetGeometry(landblock, cellDat, _raycast?.GeometryLoader?.DungeonLOS) is { DoorwayCount: > 0 };
    }

    // ── radunexploreinfo[] ───────────────────────────────────────────────────

    /// <summary>The explore as JSON for radunexploreinfo[] (landblock-local metres for positions).</summary>
    private string DunExploreInfoJson()
    {
        var run = _dunExplore;
        if (run == null) return "{}";
        var inv = CultureInfo.InvariantCulture;
        uint lb = run.Landblock;
        double ox = ((lb >> 8) & 0xFF) * 192.0, oy = (lb & 0xFF) * 192.0;
        var sb = new StringBuilder(256);
        sb.Append("{\"state\":").Append((int)run.State)
          .Append(",\"rooms\":").Append(run.Rooms)
          .Append(",\"entered\":").Append(run.Entered)
          .Append(",\"landblock\":\"").Append(lb.ToString("X4", inv)).Append('"')
          .Append(",\"doors\":[");
        bool first = true;
        foreach (var d in run.Doors.Values.OrderByDescending(d => d.RoomsBehind).ThenBy(d => d.Id))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":\"").Append(d.Id.ToString("X8", inv)).Append("\",\"name\":").Append(JsonText(d.Name))
              .Append(",\"why\":").Append(JsonText(d.Why))
              .Append(",\"rooms\":").Append(d.RoomsBehind)
              .Append(",\"cell\":\"").Append(d.Cell.ToString("X8", inv)).Append('"')
              .Append(",\"x\":").Append((d.X - ox).ToString("0.###", inv))
              .Append(",\"y\":").Append((d.Y - oy).ToString("0.###", inv))
              .Append(",\"z\":").Append(d.Z.ToString("0.###", inv)).Append('}');
        }
        sb.Append(']');
        var e = run.Errand;
        if (e != null)
            sb.Append(",\"errand\":{\"lever\":\"").Append(e.Lever.ToString("X8", inv)).Append("\",\"door\":\"").Append(e.Door.ToString("X8", inv))
              .Append("\",\"plate\":").Append(e.Plate ? "true" : "false").Append(",\"phase\":\"").Append(e.Phase).Append("\"}");
        sb.Append(",\"last\":").Append(JsonText(run.LastLever)).Append('}');
        return sb.ToString();
    }

    private static string JsonText(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
