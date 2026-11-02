using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Outdoor hunting (2026-10-23): walk from one monster to the next around obstacles. Pure: the plugin
/// (RynthAiPlugin.OutdoorHunt.cs) passes the character's position, the monsters combat would fight
/// (line of sight not needed: the walk goes round), a path function (RynthNav's navmesh, which holds
/// buildings, their insides, trees, rocks and slopes too steep to climb) and the clock, and installs the
/// route a <see cref="Decision"/> asks for. Combat takes over by itself once a monster is within its
/// range and in sight (the arbiter ranks it above navigation); this is asked again only when nothing
/// else wants the character.
///
/// The next monster is the one with the shortest walk (a higher Priority first), among the few nearest
/// in a straight line. A monster with no walkable way to it (on a cliff, in water, inside a closed
/// building) is left out for <see cref="SkipSec"/>. Monsters are looked for out to <see cref="RangeYd"/>
/// (Hunt Range); <see cref="RadiusYd"/> (Hunt Radius) keeps the picks within that far of where it started
/// (0 = anywhere). With nothing in range it waits where it is.
///
/// Buildings (2026-10-25, Tom: "the entire radar range, also going inside houses"): RynthNav's map ends
/// at a building's walls, so a walk into one is RynthNav's way to the outside of one of its doors, then
/// the building's own rooms (<see cref="IBuildings"/>: BuildingMap on cell.dat) to the monster; the door
/// giving the shortest walk in all is taken. From inside a building: a monster in the same building
/// through its rooms, one outdoors (or in another building) out through a door first. A building with
/// no door known is walked out of the way it was walked into (<see cref="Indoors"/>, the breadcrumbs).
/// </summary>
internal sealed class OutdoorHunt
{
    internal enum PathStatus { Ok, Partial, NoPath, NoMesh, Unavailable }

    /// <summary>A planned walk: the corners after the start (the last is the goal), its length, and for a
    /// partial path how far its end is from the goal.</summary>
    internal readonly record struct PathResult(PathStatus Status, IReadOnlyList<Vec> Corners, double Length, double Gap);

    /// <summary>A spot (world units: x east, y north, z up). <see cref="Door"/>: a point lined up on a
    /// doorway (walked through straight, NavPoint.Doorway).</summary>
    internal readonly record struct Vec(double X, double Y, double Z, bool Door = false)
    {
        public double DistanceTo(Vec o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
        public double FlatDistanceTo(Vec o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    }

    /// <summary>A monster combat would fight. <see cref="Cell"/>: its landcell (a building's cells are 0x0100 up).</summary>
    internal readonly record struct Candidate(int Id, string Name, Vec Pos, double Distance, int Priority, uint Cell = 0);

    /// <summary>
    /// A building's door to the outside, in <see cref="Cell"/>: <see cref="At"/> its centre at floor level,
    /// <see cref="Inner"/> a step inside it (in Cell), <see cref="Outside"/> a step out of it (on the
    /// landscape, where RynthNav's way to or from the door ends or starts).
    /// </summary>
    internal readonly record struct Door(uint Cell, Vec Inner, Vec At, Vec Outside);

    /// <summary>What the hunt knows of the buildings round it: BuildingMap (cell.dat) in the plugin, fakes in the tests.</summary>
    internal interface IBuildings
    {
        /// <summary>The building holding <paramref name="cell"/> (any id but 0); 0 for a cell outdoors or one no known building holds.</summary>
        int BuildingOf(uint cell);
        /// <summary>The building's doors to the outside that can be walked through (none known: empty).</summary>
        IReadOnlyList<Door> Doors(int building);
        /// <summary>
        /// The walk inside one building from <paramref name="from"/> (in fromCell) to <paramref name="to"/>
        /// (in toCell), through its rooms and doorways and kept out of portals: corners after the start
        /// (the last is the goal, or for Partial the nearest it gets), its length. NoPath when the two
        /// don't join inside.
        /// </summary>
        PathResult Inside(uint fromCell, Vec from, uint toCell, Vec to);
        /// <summary>A short name for log lines.</summary>
        string Name(int building);
    }

    /// <summary>A building's (or a dungeon's) cell, not the landscape's.</summary>
    internal static bool IsIndoorCell(uint cell) => (cell & 0xFFFF) >= 0x0100;

    /// <summary>A walk planned by <see cref="PlanWalk"/>: the path, the door it leaves a building by and
    /// the one it goes into a building by (null: none), the buildings (0: outdoors), why it failed.</summary>
    internal readonly record struct HuntWalk(PathResult P, Door? Out = null, Door? In = null, int From = 0, int To = 0, string Why = "");

    internal enum Act
    {
        /// <summary>Keep doing what the last decision said.</summary>
        None,
        /// <summary>Walk <see cref="Decision.Corners"/> (replacing the hunt's route).</summary>
        Walk,
        /// <summary>Stand (take the hunt's route off): nothing to hunt and nowhere to go, or already at the
        /// end of the walk to <see cref="Decision.TargetId"/>.</summary>
        Idle,
    }

    internal readonly record struct Decision(Act Act, IReadOnlyList<Vec>? Corners = null, int TargetId = 0, string Why = "");

    // Settings (the plugin copies them in before each Tick).
    /// <summary>Hunt Range: 192 by default since 2026-10-25, the radar's own default range (Tom: "the entire radar range").</summary>
    public double RangeYd { get; set; } = DefaultRangeYd;
    public const double DefaultRangeYd = 192;
    public double RadiusYd { get; set; } = 0;

    /// <summary>A partial path whose end is within this of the monster still counts (the last step is combat's).</summary>
    public double ReachYd { get; set; } = 3;
    /// <summary>
    /// A walk longer than this times Hunt Range is too far round: the first test hunt (10-23) chose a buff bot
    /// 12 yd away inside a building, a 143 yd walk; the walk led her past Hunt Range from it, she gave up,
    /// turned back, and chose it again, every second.
    /// </summary>
    public double MaxWalkFactor { get; set; } = 2.0;
    /// <summary>
    /// The plugin asks for monsters out to this times Hunt Range: new picks stay within Hunt Range, but the
    /// one being walked to is kept while the walk round an obstacle takes the character further from it.
    /// </summary>
    public const double KeepFactor = 2.0;
    /// <summary>A monster with no way to it is left out this long.</summary>
    public double SkipSec { get; set; } = 120;
    /// <summary>How many of the nearest monsters get a path asked for when choosing.</summary>
    public int PickCandidates { get; set; } = 6;
    public double PickEverySec { get; set; } = 1.0;
    /// <summary>Plan again when the monster has moved this far from where the walk was planned to.</summary>
    public double ReplanMovedYd { get; set; } = 4;
    public double ReplanMinSec { get; set; } = 1.0;
    /// <summary>Plan again at least this often while walking to a monster (it may have turned).</summary>
    public double ReplanMaxSec { get; set; } = 8;
    /// <summary>A walk this short: the character is already where the walk to the monster ends.</summary>
    public double ArrivedYd { get; set; } = 2;
    /// <summary>
    /// At the end of the walk to a monster this long and combat hasn't taken it (the hunt is only asked
    /// when nothing else wants the character): something stops combat, a wall or a fence between, and the
    /// monster is left out for <see cref="SkipSec"/>. a test character (10-23) stood at a wall re-walking 1 yd
    /// every half second to a monster on its far side: the walk's end, 1.3 yd away, counted as reached.
    /// </summary>
    public double NoFightSec { get; set; } = 2;

    public bool Active { get; private set; }
    public Vec Anchor { get; private set; }
    public int TargetId { get; private set; }
    /// <summary>The last thing worth a log line (the plugin logs it when it changes).</summary>
    public string Note
    {
        get => _note;
        private set { _note = value; _notes.Add(value); }
    }
    private string _note = "";
    private readonly List<string> _notes = new();
    /// <summary>Every note since <see cref="ClearNotes"/>: one tick can have several (one left out, then the next chosen).</summary>
    internal IReadOnlyList<string> PendingNotes => _notes;
    internal void ClearNotes() => _notes.Clear();

    private Vec _plannedTo;
    private DateTime _lastPlan = DateTime.MinValue, _lastPick = DateTime.MinValue;
    private DateTime _arrivedSince = DateTime.MinValue;   // at the walk's end with no fight since (NoFightSec)
    private readonly Dictionary<int, DateTime> _skip = new();
    private readonly List<(Candidate C, HuntWalk W)> _viable = new();
    private Func<Vec, Vec, PathResult> _path = (_, _) => new PathResult(PathStatus.Unavailable, Array.Empty<Vec>(), 0, 0);
    private IBuildings? _buildings;
    private int _meBuilding;   // the building the character is in this tick (0: outdoors)

    public void Start(DateTime now, Vec at)
    {
        Active = true;
        Anchor = at;
        TargetId = 0;
        _skip.Clear();
        _lastPlan = _lastPick = _arrivedSince = DateTime.MinValue;
        Note = $"started at {at.X:0},{at.Y:0}";
    }

    public void Stop()
    {
        Active = false;
        TargetId = 0;
        WalkingOut = false;
        _outTries = 0;
    }

    /// <summary>Left out of the hunt for a while (no way to it, or combat gave up on it).</summary>
    public void Skip(int id, DateTime now, double sec = -1) => _skip[id] = now.AddSeconds(sec < 0 ? SkipSec : sec);

    public bool IsSkipped(int id, DateTime now)
    {
        if (!_skip.TryGetValue(id, out var until)) return false;
        if (now < until) return true;
        _skip.Remove(id);
        return false;
    }

    /// <summary>
    /// The next step. <paramref name="routeDone"/>: the last walk ran out (arrived, or navigation stopped it).
    /// <paramref name="candidates"/>: sorted nearest first. Called only when nothing else wants the character.
    /// </summary>
    public Decision Tick(DateTime now, Vec me, bool routeDone, IReadOnlyList<Candidate> candidates, Func<Vec, Vec, PathResult> path)
        => Tick(now, me, 0, routeDone, candidates, path, null);

    /// <summary>
    /// The next step, with the character's cell <paramref name="meCell"/> and the buildings round it
    /// (<paramref name="buildings"/>, null = none known: monsters in buildings are left alone). In a
    /// building no door of which is known, the walk back out the way in (<see cref="Indoors"/>).
    /// </summary>
    public Decision Tick(DateTime now, Vec me, uint meCell, bool routeDone, IReadOnlyList<Candidate> candidates,
                         Func<Vec, Vec, PathResult> path, IBuildings? buildings)
    {
        if (!Active) return new Decision(Act.None);
        _path = path;
        _buildings = buildings;
        _now = now;
        _meBuilding = 0;
        if (IsIndoorCell(meCell))
        {
            int inB = buildings?.BuildingOf(meCell) ?? 0;
            if (inB == 0 || buildings!.Doors(inB).Count == 0) return Indoors(now, me, routeDone);
            _meBuilding = inB;
            WalkingOut = false;   // the doors take it out from here
        }

        // Still walking to a monster that is still there: plan again when it moved, the walk ran out, or a while passed.
        if (TargetId != 0)
        {
            Candidate? t = null;
            foreach (var c in candidates)
                if (c.Id == TargetId) { t = c; break; }
            if (t is Candidate target && InArea(target.Pos) && !IsSkipped(target.Id, now) && Plannable(target))
            {
                double since = (now - _lastPlan).TotalSeconds;
                bool moved = target.Pos.DistanceTo(_plannedTo) > ReplanMovedYd && since >= ReplanMinSec;
                bool due = (routeDone && since >= 0.5) || since >= ReplanMaxSec;
                if (!moved && !due) return new Decision(Act.None, TargetId: TargetId);
                var w = PlanWalk(me, meCell, target.Pos, target.Cell);
                var p = w.P;
                if (Reaches(p))
                {
                    _plannedTo = target.Pos;
                    _lastPlan = now;
                    if (p.Length > ArrivedYd)
                    {
                        _arrivedSince = DateTime.MinValue;
                        return new Decision(Act.Walk, p.Corners, target.Id, $"to '{target.Name}' again ({p.Length:0} yd)");
                    }
                    // Already at the walk's end: stand, no 1 yd walk (it ends at once and is planned
                    // again every half second); combat has NoFightSec to take the monster.
                    if (_arrivedSince == DateTime.MinValue) _arrivedSince = now;
                    if ((now - _arrivedSince).TotalSeconds < NoFightSec) return new Decision(Act.Idle, TargetId: target.Id);
                    Skip(target.Id, now);
                    Note = $"'{target.Name}': at the end of the walk to it for {NoFightSec:0} s and combat hasn't taken it "
                         + $"(a wall or a fence between?): left out for {SkipSec:0} s";
                }
                else
                {
                    Skip(target.Id, now);
                    Note = $"'{target.Name}' can't be reached any more ({Describe(p, w.Why)}): left out for {SkipSec:0} s";
                }
            }
            TargetId = 0;
            _arrivedSince = DateTime.MinValue;
        }

        // Choosing (at most once every PickEverySec): the few nearest, by the walk to them.
        if ((now - _lastPick).TotalSeconds < PickEverySec)
            return new Decision(Act.Idle);
        _lastPick = now;
        _viable.Clear();
        int asked = 0;
        foreach (var c in candidates)
        {
            if (asked >= PickCandidates) break;
            if (c.Distance > RangeYd) break;   // nearest first
            if (!InArea(c.Pos) || IsSkipped(c.Id, now) || !Plannable(c)) continue;
            asked++;
            var w = PlanWalk(me, meCell, c.Pos, c.Cell);
            var p = w.P;
            if (p.Status == PathStatus.Unavailable) return new Decision(Act.Idle, Why: "no pathfinder");
            if (Reaches(p)) _viable.Add((c, w));
            else
            {
                Skip(c.Id, now);
                Note = $"'{c.Name}' left out for {SkipSec:0} s: {Describe(p, w.Why)}";
            }
        }
        if (_viable.Count > 0)
        {
            var best = _viable[0];
            for (int i = 1; i < _viable.Count; i++)
            {
                var v = _viable[i];
                if (v.C.Priority > best.C.Priority || (v.C.Priority == best.C.Priority && v.W.P.Length < best.W.P.Length)) best = v;
            }
            TargetId = best.C.Id;
            _plannedTo = best.C.Pos;
            _lastPlan = now;
            Note = NextNote(best.C, best.W);
            if (best.W.P.Length > ArrivedYd)
            {
                _arrivedSince = DateTime.MinValue;
                return new Decision(Act.Walk, best.W.P.Corners, best.C.Id, Note);
            }
            _arrivedSince = now;   // it's right here and combat hasn't taken it: NoFightSec, then left out
            return new Decision(Act.Idle, TargetId: best.C.Id, Why: Note);
        }

        // Nothing to hunt: wait where it is (no walk back to the start: Tom 10-23, "it shouldn't bother").
        string wait = $"nothing within Hunt Range ({RangeYd:0} yd){(RadiusYd > 0 ? $" inside Hunt Radius ({RadiusYd:0} yd)" : "")}: waiting here";
        if (_note != wait) Note = wait;
        return new Decision(Act.Idle);
    }

    // ── Indoors ──────────────────────────────────────────────────────────────────────────────────
    // A building's rooms are off RynthNav's landscape map: it plans no walk from in there, and every
    // monster came back "not on the map" (a test hunt 10-23, after combat chased one inside: the hunt left
    // them all out and stood there). Combat still fights in there; for the walk back out, the way in.
    // Since 2026-10-25 this is the fallback: Tick walks out through a door of a building it knows
    // (IBuildings), and comes here only for one with no door known (or no building map at all).

    private readonly List<Vec> _wayIn = new();   // [0] the last spot outdoors, then the steps inside
    private bool _knowsOutdoors;
    private Vec _lastOutdoors;
    private DateTime _outPlannedAt = DateTime.MinValue;
    private Vec _outEnd;
    private int _outTries;

    /// <summary>A step of the way in is kept every this far.</summary>
    public double StepYd { get; set; } = 1.5;
    /// <summary>The walk out ends this far past the last spot outdoors (out of the doorway).</summary>
    public double OutPastYd { get; set; } = 3;
    /// <summary>Walks out that ended still inside, before giving up (waiting for the character to come out).</summary>
    public int OutTriesMax { get; set; } = 3;
    internal const int MaxSteps = 256;

    public bool WalkingOut { get; private set; }
    internal int WayInSteps => _wayIn.Count;

    /// <summary>Every tick, whatever the character is doing: where it is, and whether that is in a building.</summary>
    public void Track(Vec me, bool indoors)
    {
        if (!indoors)
        {
            _wayIn.Clear();
            _lastOutdoors = me;
            _knowsOutdoors = true;
            WalkingOut = false;
            _outTries = 0;
            return;
        }
        if (_wayIn.Count == 0)
        {
            if (!_knowsOutdoors) return;
            _wayIn.Add(_lastOutdoors);
        }
        // Back near a step already on the way: the loop since is cut, so the way out stays the shortest known.
        for (int i = 0; i < _wayIn.Count - 1; i++)
        {
            if (_wayIn[i].FlatDistanceTo(me) < StepYd)
            {
                _wayIn.RemoveRange(i + 1, _wayIn.Count - i - 1);
                return;
            }
        }
        if (_wayIn[^1].FlatDistanceTo(me) >= StepYd && _wayIn.Count < MaxSteps) _wayIn.Add(me);
    }

    /// <summary>The walk back out: the steps in, newest first, the last spot outdoors, and <see cref="OutPastYd"/> past it; null when no way in is known.</summary>
    internal List<Vec>? WayOut(Vec me)
    {
        if (_wayIn.Count == 0) return null;
        var way = new List<Vec>(_wayIn.Count + 1);
        for (int i = _wayIn.Count - 1; i >= 1; i--)
            if (_wayIn[i].FlatDistanceTo(me) >= 1.0) way.Add(_wayIn[i]);
        Vec door = _wayIn[0];
        way.Add(door);
        Vec from = way.Count >= 2 ? way[^2] : me;
        double dx = door.X - from.X, dy = door.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 0.1) way.Add(new Vec(door.X + dx / len * OutPastYd, door.Y + dy / len * OutPastYd, door.Z));
        return way;
    }

    /// <summary>
    /// The hunt's step while the character is in a building (instead of <see cref="Tick"/>, which plans on
    /// the landscape): walk back out the way in. <paramref name="routeDone"/>: the last walk ran out.
    /// </summary>
    public Decision Indoors(DateTime now, Vec me, bool routeDone)
    {
        if (!Active) return new Decision(Act.None);
        if (WalkingOut)
        {
            if (!routeDone || (now - _outPlannedAt).TotalSeconds < 1) return new Decision(Act.None);
            // The walk ran out. At its end and still inside counts against OutTriesMax; anywhere else
            // (a fight took the character elsewhere, the walk was cut short) it is just planned again.
            if (me.FlatDistanceTo(_outEnd) <= 2) _outTries++;
        }
        var way = _outTries >= OutTriesMax ? null : WayOut(me);
        if (way == null)
        {
            string wait = _outTries >= OutTriesMax
                ? $"inside a building: {OutTriesMax} walks out ended still inside: waiting"
                : "inside a building and no way out known (the hunt never saw it outdoors): waiting";
            if (Note != wait) Note = wait;
            WalkingOut = false;
            return new Decision(Act.Idle);
        }
        WalkingOut = true;
        _outPlannedAt = now;
        _outEnd = way[^1];
        Note = $"inside a building: back out the way in ({way.Count} steps)";
        return new Decision(Act.Walk, way, TargetId, Note);
    }

    private bool InArea(Vec p) => RadiusYd <= 0 || p.FlatDistanceTo(Anchor) <= RadiusYd;

    private bool Reaches(PathResult p) =>
        p.Corners.Count > 0 && (p.Status == PathStatus.Ok || (p.Status == PathStatus.Partial && p.Gap <= ReachYd))
        && p.Length <= RangeYd * MaxWalkFactor;

    private string Describe(PathResult p, string why = "") => p.Status switch
    {
        _ when p.Corners.Count > 0 && p.Length > RangeYd * MaxWalkFactor
                           && (p.Status == PathStatus.Ok || p.Status == PathStatus.Partial) => $"a {p.Length:0} yd walk, too far round",
        _ when why.Length > 0 => why,
        PathStatus.Partial => $"the walk ends {p.Gap:0} yd short",
        PathStatus.NoPath => "no walkable way",
        PathStatus.NoMesh => "not on the map (indoors, or no map there)",
        _ => p.Status.ToString(),
    };

    /// <summary>A monster in a building is hunted only when that building is known: its rooms, and a door
    /// into it (or the character is in it already).</summary>
    private bool Plannable(Candidate c)
    {
        if (!IsIndoorCell(c.Cell)) return true;
        int b = _buildings?.BuildingOf(c.Cell) ?? 0;
        return b != 0 && (b == _meBuilding || _buildings!.Doors(b).Count > 0);
    }

    private string NextNote(Candidate c, HuntWalk w)
    {
        var b = _buildings;
        string len = $"a {w.P.Length:0} yd walk";
        string away = $"({c.Distance:0} yd away)";
        static string At(Door d) => $"{d.At.X:0},{d.At.Y:0}";
        if (b == null || (w.From == 0 && w.To == 0)) return $"next: '{c.Name}', {len} {away}";
        if (w.Out is Door o && w.In is Door i)
            return $"next: '{c.Name}', inside {b.Name(w.To)} (cell {c.Cell:X8}), {len} out of {b.Name(w.From)} through the door at {At(o)}, "
                 + $"then in through the door at {At(i)} {away}";
        if (w.In is Door din) return $"next: '{c.Name}', inside {b.Name(w.To)} (cell {c.Cell:X8}), {len} through the door at {At(din)} {away}";
        if (w.Out is Door dout) return $"next: '{c.Name}', {len} out of {b.Name(w.From)} through the door at {At(dout)} {away}";
        return $"next: '{c.Name}', inside {b.Name(w.To)} (cell {c.Cell:X8}), {len} through its rooms {away}";
    }

    // ── Walks into, out of and between buildings ───────────────────────────────────────────────────

    /// <summary>Most door pairs tried for one walk between two buildings (each a RynthNav path).</summary>
    internal const int MaxDoorPairs = 16;
    /// <summary>A door whose step out is this far above or below RynthNav's ground there doesn't open
    /// onto the ground (a balcony, the top of a wall).</summary>
    public double DoorstepRiseYd { get; set; } = 1.5;
    /// <summary>How long a doorstep's answer is kept.</summary>
    public double DoorstepKeepSec { get; set; } = 60;
    private readonly Dictionary<(uint, long, long), (bool Ok, DateTime Until)> _doorsteps = new();
    private DateTime _now;

    /// <summary>
    /// The door's step out is on RynthNav's ground: the map's nearest point to it (a path from it to
    /// itself) within ReachYd across and DoorstepRiseYd in height. Asked once per DoorstepKeepSec.
    /// </summary>
    private bool OnGround(Door d)
    {
        var key = (d.Cell, (long)Math.Round(d.Outside.X * 10), (long)Math.Round(d.Outside.Y * 10));
        if (_doorsteps.TryGetValue(key, out var known) && _now < known.Until) return known.Ok;
        var p = _path(d.Outside, d.Outside);
        if (p.Status == PathStatus.Unavailable) return true;   // no pathfinder: the walk itself says so
        bool ok = p.Status is PathStatus.Ok or PathStatus.Partial && p.Corners.Count > 0
               && p.Corners[^1].FlatDistanceTo(d.Outside) <= ReachYd
               && Math.Abs(p.Corners[^1].Z - d.Outside.Z) <= DoorstepRiseYd;
        if (_doorsteps.Count > 512) _doorsteps.Clear();
        _doorsteps[key] = (ok, _now.AddSeconds(DoorstepKeepSec));
        return ok;
    }

    /// <summary>
    /// The walk from <paramref name="me"/> (in meCell) to <paramref name="goal"/> (in goalCell): on
    /// RynthNav's map when both are outdoors; through the building's rooms when both are in one; else
    /// out through a door of the building it is in and/or in through a door of the goal's, the door (or
    /// pair) with the shortest walk in all. The corners run through each door's inner point, centre and
    /// outer point (flagged Door). The walk's end (status, gap) is its last part's.
    /// </summary>
    internal HuntWalk PlanWalk(Vec me, uint meCell, Vec goal, uint goalCell)
    {
        var b = _buildings;
        int from = b != null && IsIndoorCell(meCell) ? b.BuildingOf(meCell) : 0;
        int to = b != null && IsIndoorCell(goalCell) ? b.BuildingOf(goalCell) : 0;
        if (from == 0 && to == 0) return new HuntWalk(_path(me, goal));
        string why = "";
        if (from != 0 && from == to)
        {
            var inside = b!.Inside(meCell, me, goalCell, goal);
            if (inside.Status is PathStatus.Ok or PathStatus.Partial && inside.Corners.Count > 0)
                return new HuntWalk(inside, From: from, To: to);
            why = $"no way to it through the rooms of {b.Name(from)}";
            // Its parts don't join inside: out by one door and in by another, as two buildings.
        }

        // The ways out of the character's building (to each door's outer point) and into the goal's (from each door's outer point).
        var outs = new List<(Door? D, PathResult P, double Cost)>();
        var ins = new List<(Door? D, PathResult P, double Cost)>();
        var none = new PathResult(PathStatus.Ok, Array.Empty<Vec>(), 0, 0);
        if (from == 0) outs.Add((null, none, 0));
        else
            foreach (var d in b!.Doors(from))
            {
                var p = b.Inside(meCell, me, d.Cell, d.Inner);
                if (p.Status != PathStatus.Ok || !OnGround(d)) continue;
                outs.Add((d, p, p.Length + d.Inner.DistanceTo(d.At) + d.At.DistanceTo(d.Outside)));
            }
        if (to == 0) ins.Add((null, none, 0));
        else
            foreach (var d in b!.Doors(to))
            {
                var p = b.Inside(d.Cell, d.Inner, goalCell, goal);
                if (p.Status is not (PathStatus.Ok or PathStatus.Partial) || p.Corners.Count == 0 || !OnGround(d)) continue;
                ins.Add((d, p, d.Outside.DistanceTo(d.At) + d.At.DistanceTo(d.Inner) + p.Length));
            }
        if (outs.Count == 0 || ins.Count == 0)
        {
            if (why.Length == 0)
                why = outs.Count == 0 ? $"no door of {b!.Name(from)} it can walk to" : $"no door of {b!.Name(to)} leads to it";
            return new HuntWalk(new PathResult(PathStatus.NoPath, Array.Empty<Vec>(), 0, 0), From: from, To: to, Why: why);
        }

        // Door pairs by the least their walk can be (the outdoor part at least the straight line): a
        // pair that can't beat the best so far isn't asked for (each one is a RynthNav path).
        var pairs = new List<(int O, int I, double Bound)>();
        for (int o = 0; o < outs.Count; o++)
            for (int i = 0; i < ins.Count; i++)
            {
                Vec a = outs[o].D?.Outside ?? me, z = ins[i].D?.Outside ?? goal;
                pairs.Add((o, i, outs[o].Cost + a.FlatDistanceTo(z) + ins[i].Cost));
            }
        pairs.Sort((x, y) => x.Bound.CompareTo(y.Bound));

        HuntWalk? best = null;
        double bestLen = double.MaxValue;
        bool unavailable = false;
        int tried = 0;
        foreach (var (o, i, bound) in pairs)
        {
            if (bound >= bestLen || tried >= MaxDoorPairs) break;
            tried++;
            var (dOut, pOut, cOut) = outs[o];
            var (dIn, pIn, cIn) = ins[i];
            Vec a = dOut?.Outside ?? me, z = dIn?.Outside ?? goal;
            var land = _path(a, z);
            if (land.Status == PathStatus.Unavailable) { unavailable = true; break; }
            if (land.Status is not (PathStatus.Ok or PathStatus.Partial) || land.Corners.Count == 0) continue;
            // Into a door: the way there must end at its doorstep (a door above the ground, on a
            // balcony, leaves it the height of the floor short).
            if (dIn != null && land.Gap > ReachYd) continue;
            double len = cOut + land.Length + cIn;
            if (len >= bestLen) continue;
            var corners = new List<Vec>(pOut.Corners.Count + land.Corners.Count + pIn.Corners.Count + 6);
            if (dOut is Door x) { AddCorners(corners, pOut.Corners); AddCorner(corners, x.At); AddCorner(corners, x.Outside); }
            AddCorners(corners, land.Corners);
            PathResult whole;
            if (dIn is Door y)
            {
                AddCorner(corners, y.Outside); AddCorner(corners, y.At); AddCorner(corners, y.Inner);
                AddCorners(corners, pIn.Corners);
                whole = new PathResult(pIn.Status, corners, len, pIn.Gap);
            }
            else whole = new PathResult(land.Status, corners, len, land.Gap);
            best = new HuntWalk(whole, dOut, dIn, from, to);
            bestLen = len;
        }
        if (best is HuntWalk found) return found;
        if (unavailable) return new HuntWalk(new PathResult(PathStatus.Unavailable, Array.Empty<Vec>(), 0, 0));
        if (why.Length == 0)
            why = to != 0 ? $"no way to a door of {b!.Name(to)}" : $"no way to it from a door of {b!.Name(from)}";
        return new HuntWalk(new PathResult(PathStatus.NoPath, Array.Empty<Vec>(), 0, 0), From: from, To: to, Why: why);
    }

    /// <summary>Corners closer than this to the one before are one (RynthNav's way to a doorstep ends on it).</summary>
    private const double SameCornerYd = 0.3;

    private static void AddCorner(List<Vec> corners, Vec v)
    {
        if (corners.Count > 0 && corners[^1].DistanceTo(v) < SameCornerYd)
        {
            if (v.Door && !corners[^1].Door) corners[^1] = corners[^1] with { Door = true };
            return;
        }
        corners.Add(v);
    }

    private static void AddCorners(List<Vec> corners, IReadOnlyList<Vec> more)
    {
        foreach (var v in more) AddCorner(corners, v);
    }
}
