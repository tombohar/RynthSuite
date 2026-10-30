using System.Numerics;

namespace RynthNav.TownNet;

internal sealed record Waypoint(uint Cell, Vector3 P);

internal sealed class Walk
{
    public string From = "", To = "", ExitName = "";
    public List<Waypoint> Points = new();
    public float Length;
    public float MinClear;               // nearest wall / object / floor edge along the walk (centre of the walker)
    public float MinPortalClear;         // nearest other portal's edge along the walk
    public string MinClearAt = "";       // where MinClear is (for the build log)
    public int Cells;
    public string? Error;
}

/// <summary>Plans and checks the walks from an arrival point to an exit portal.</summary>
internal sealed class WalkPlanner
{
    private readonly Indoor _w;
    private readonly NavGrid _g;
    public readonly float PassClear, PassPortal, SmoothClear, MustClear, MustPortal;

    /// <summary>The grid's floor-edge distances are good to about one grid square; walls and objects are
    /// checked exactly, so this slack only matters at drops with no wall.</summary>
    public const float EdgeSlack = NavGrid.Cell;

    /// <summary>The margin the search keeps beyond the walker's body (m). A walk that is only the body's width from a
    /// wall or object gets the character stuck on it (steering is not exact): the patrol keeps about 1 m, so
    /// this is 0.5 and the search falls back to the old 0.25 for a walk with no room at 0.5 (see PlanFrom).</summary>
    public const float DefaultExtra = 0.5f;

    public WalkPlanner(Indoor w, NavGrid g, float extra = DefaultExtra)
    {
        _w = w; _g = g;
        float r = w.AgentRadius;
        PassClear = r + extra;        // grid search: keep this far from walls and objects
        PassPortal = r + extra + 0.25f;   // ... and from other portals (touching one uses it)
        SmoothClear = r + extra - 0.10f;  // straightened segments
        MustClear = r;                // the check: the walker's body never touches a wall or object
        MustPortal = r + 0.25f;       // ... and stays a step away from every other portal
    }

    private bool Passable(NavGrid.Node n) => n.Clear >= PassClear && n.PortalClear >= PassPortal;

    public List<Walk> PlanFrom(string arrivalId, uint cell, Vector3 at, IReadOnlyList<PortalZone> exits)
    {
        var walks = new List<Walk>();
        float? fz = _w.Cells.TryGetValue(cell, out var c) ? _w.FloorZ(c, at.X, at.Y, at.Z, 0.05f) : null;
        if (fz == null)
        {
            foreach (var e in exits) walks.Add(new Walk { From = arrivalId, To = e.Guid, ExitName = e.Name, Error = "the arrival point is not on a floor" });
            return walks;
        }
        var start = new Vector3(at.X, at.Y, fz.Value);
        // The first grid node: the nearest passable one that a straight step from the arrival reaches.
        var first = _g.Nearest(cell, at.X, at.Y, n => Passable(n) && SegmentOk(start, cell, Pos(n), null, SmoothClear, out _), 4f);
        if (first == null)
        {
            foreach (var e in exits) walks.Add(new Walk { From = arrivalId, To = e.Guid, ExitName = e.Name, Error = "no clear floor within 4 m of the arrival point" });
            return walks;
        }
        var (cost, prev) = _g.Dijkstra(first, Passable);
        foreach (var e in exits) walks.Add(PlanTo(arrivalId, cell, start, cost, prev, e));
        return walks;
    }

    private static Vector3 Pos(NavGrid.Node n) => new(n.X, n.Y, n.Z);

    private Walk PlanTo(string arrivalId, uint startCell, Vector3 start, float[] cost, int[] prev, PortalZone exit)
    {
        var walk = new Walk { From = arrivalId, To = exit.Guid, ExitName = exit.Name };
        // Where the walk ends: the portal's centre on the floor (walking into it uses it).
        var ec = _w.Cells[exit.Cell];
        float? ez = _w.FloorZ(ec, exit.Pos.X, exit.Pos.Y, exit.Pos.Z, 0.05f);
        Vector3 end;
        uint endCell = exit.Cell;
        if (ez != null) end = new Vector3(exit.Pos.X, exit.Pos.Y, ez.Value);
        else
        {
            // Off the floor (in a niche): end at the nearest floor inside the portal's reach.
            var n = _g.Nearest(exit.Cell, exit.Pos.X, exit.Pos.Y, _ => true, exit.Radius);
            if (n == null) { walk.Error = "the portal does not stand on a floor"; return walk; }
            end = Pos(n);
        }
        // The last grid node: reached from the arrival, with a clear straight step into the portal. A roomy
        // approach first (so the walker comes in square, not grazing the alcove's corner), then a tight one.
        NavGrid.Node? goal = null;
        foreach (float need in new[] { SmoothClear, MustClear })
        {
            float best = float.MaxValue;
            foreach (var n in _g.Around(end.X, end.Y, 5f))
            {
                if (cost[n.Index] == float.MaxValue) continue;
                float d = Vector2.Distance(new Vector2(n.X, n.Y), new Vector2(end.X, end.Y));
                if (cost[n.Index] + d >= best) continue;
                if (!SegmentOk(Pos(n), n.CellId, end, exit, need, out _)) continue;
                best = cost[n.Index] + d; goal = n;
            }
            if (goal != null) break;
        }
        if (goal == null) { walk.Error = "no clear way to the portal from the walkable floor"; return walk; }

        var raw = new List<Waypoint> { new(startCell, start) };
        raw.AddRange(_g.PathTo(goal.Index, prev).Select(n => new Waypoint(n.CellId, Pos(n))));
        raw.Add(new Waypoint(endCell, end));
        walk.Points = Straighten(raw, exit).Select(Rounded).ToList();
        Check(walk, exit);
        return walk;
    }

    /// <summary>A waypoint as the file stores it (centimetres), its height put back on its cell's floor.</summary>
    private Waypoint Rounded(Waypoint w)
    {
        float x = MathF.Round(w.P.X, 2), y = MathF.Round(w.P.Y, 2);
        float z = _w.FloorZ(_w.Cells[w.Cell], x, y, w.P.Z, 0.02f) ?? w.P.Z;
        return new Waypoint(w.Cell, new Vector3(x, y, MathF.Round(z, 2)));
    }

    /// <summary>Greedy string pulling: from each kept point, the furthest later point a clear straight segment reaches.</summary>
    private List<Waypoint> Straighten(List<Waypoint> raw, PortalZone exit)
    {
        var kept = new List<Waypoint> { raw[0] };
        int i = 0;
        while (i < raw.Count - 1)
        {
            int j = i + 1;
            // Search from the far end down in coarse steps, then refine: long clear segments are the common case.
            int lo = i + 1, hi = raw.Count - 1;
            for (int k = hi; k > lo; k = Math.Max(lo, k - Math.Max(1, (k - lo) / 2)))
            {
                if (SegmentOk(raw[i].P, raw[i].Cell, raw[k].P, exit, SmoothClear, out _)) { j = k; break; }
                if (k == lo + 1) break;
            }
            // Walk forward from there while it still holds.
            while (j + 1 < raw.Count && SegmentOk(raw[i].P, raw[i].Cell, raw[j + 1].P, exit, SmoothClear, out _)) j++;
            kept.Add(raw[j]);
            i = j;
        }
        return kept;
    }

    /// <summary>A straight segment the walker can follow: on the floor, through doorways only, clear of walls,
    /// objects, floor edges and of every portal but its own (inside its own portal's reach the walker has
    /// already touched it, so nothing there counts).</summary>
    public bool SegmentOk(Vector3 a, uint cell, Vector3 b, PortalZone? target, float clear, out string why)
    {
        if (_w.Trace(a, b, cell, out why) == null) return false;
        float len = Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
        int n = Math.Max(1, (int)MathF.Ceiling(len / 0.1f));
        for (int i = 0; i <= n; i++)
        {
            Vector3 p = Vector3.Lerp(a, b, i / (float)n);
            if (InReach(p, target)) continue;
            float d = _w.SolidDist(p.X, p.Y, p.Z, clear + 0.01f, out var f);
            if (d < clear) { why = $"({p.X:F2},{p.Y:F2}) {d:F2} m from {f!.Source}"; return false; }
            float pd = _w.PortalDist(p.X, p.Y, p.Z, target?.Guid, out var pz);
            if (pd < MustPortal) { why = $"({p.X:F2},{p.Y:F2}) {pd:F2} m from portal {pz!.Name}"; return false; }
            if (_g.ClearAt(p) < clear - EdgeSlack) { why = $"({p.X:F2},{p.Y:F2}) near a floor edge"; return false; }
        }
        why = "";
        return true;
    }

    /// <summary>Within the target portal's reach: the walker's body already touches it.</summary>
    private bool InReach(Vector3 p, PortalZone? target) =>
        target != null && Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(target.Pos.X, target.Pos.Y)) < target.Radius + _w.AgentRadius;

    /// <summary>The offline check of a finished walk: every waypoint on a floor, every segment on the floor and
    /// through doorways only, the walker never touching a wall, object or another portal.</summary>
    public void Check(Walk walk, PortalZone target)
    {
        walk.Length = 0; walk.MinClear = float.MaxValue; walk.MinPortalClear = float.MaxValue; walk.Error = null;
        var cells = new HashSet<uint>();
        for (int i = 0; i < walk.Points.Count; i++)
        {
            var wp = walk.Points[i];
            if (_w.FloorZ(_w.Cells[wp.Cell], wp.P.X, wp.P.Y, wp.P.Z, 0.02f) is not float z || MathF.Abs(z - wp.P.Z) > 0.05f)
            { walk.Error = $"waypoint {i} ({wp.P.X:F2},{wp.P.Y:F2}) is not on the floor of {wp.Cell:X8}"; return; }
            cells.Add(wp.Cell);
            if (i == 0) continue;
            var a = walk.Points[i - 1];
            var trace = _w.Trace(a.P, wp.P, a.Cell, out string why, 0.05f);
            if (trace == null) { walk.Error = $"segment {i}: {why}"; return; }
            if (trace[^1] != wp.Cell) { walk.Error = $"segment {i} ends in {trace[^1]:X8}, its waypoint says {wp.Cell:X8}"; return; }
            foreach (var c in trace) cells.Add(c);
            float len = Vector2.Distance(new Vector2(a.P.X, a.P.Y), new Vector2(wp.P.X, wp.P.Y));
            int n = Math.Max(1, (int)MathF.Ceiling(len / 0.05f));
            for (int k = 0; k <= n; k++)
            {
                Vector3 p = Vector3.Lerp(a.P, wp.P, k / (float)n);
                float pd = _w.PortalDist(p.X, p.Y, p.Z, target.Guid, out var pz);
                walk.MinPortalClear = MathF.Min(walk.MinPortalClear, pd);
                if (pd < MustPortal) { walk.Error = $"segment {i} passes {pd:F2} m from portal {pz!.Name}"; return; }
                if (InReach(p, target)) continue;
                float d = _w.SolidDist(p.X, p.Y, p.Z, 3f, out var f);
                if (d < walk.MinClear)
                {
                    walk.MinClear = d;
                    float toPortal = Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(target.Pos.X, target.Pos.Y));
                    walk.MinClearAt = $"segment {i}/{walk.Points.Count - 1} at ({p.X:F2},{p.Y:F2}), {toPortal:F1} m from the exit, by {f?.Source}";
                }
                if (d < MustClear) { walk.Error = $"segment {i} passes {d:F2} m from {f!.Source}"; return; }
                float edge = _g.ClearAt(p);
                if (edge < MustClear - EdgeSlack) { walk.Error = $"segment {i} passes {edge:F2} m from a floor edge"; return; }
            }
            walk.Length += Vector3.Distance(a.P, wp.P);
        }
        walk.Cells = cells.Count;
    }
}
