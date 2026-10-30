using System.Globalization;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace RynthNav.Baker;

/// <summary>
/// Path queries against baked tiles, loaded the way the plugin loads them (one world-positioned
/// tiled navmesh, 192 m tiles), plus a test that the path's straight segments don't cross the
/// game's collision geometry. Used by --path and by the --check regression set.
/// </summary>
internal sealed class NavCheck
{
    private readonly DtNavMesh _nav;
    private readonly DtNavMeshQuery _q;
    private readonly DtQueryDefaultFilter _filter = new();

    public NavCheck(string tileDir, int x0, int x1, int y0, int y1)
    {
        _nav = new DtNavMesh();
        var p = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 256, maxPolys = 1 << 16 };
        _nav.Init(ref p, NavBake.VertsPerPoly);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                string path = Path.Combine(tileDir, $"nav_{(x << 8) | y:X4}.tile");
                if (!File.Exists(path)) continue;
                DtMeshData md;
                using (var fr = File.OpenRead(path)) using (var br = new BinaryReader(fr)) md = new DtMeshDataReader().Read(br, NavBake.VertsPerPoly);
                _nav.AddTile(md, 0, 0, out _);
            }
        _q = new DtNavMeshQuery(_nav);
    }

    public sealed record Result(bool StartOnMesh, bool GoalOnMesh, bool Complete, List<(float ew, float ns, float up)> Points, float Length, int Polys)
    {
        /// <summary>Horizontal distance from the route's end to (ew, ns).</summary>
        public float EndDistance(float ew, float ns) => Points.Count == 0 ? float.MaxValue : MathF.Sqrt(MathF.Pow(Points[^1].ew - ew, 2) + MathF.Pow(Points[^1].ns - ns, 2));
        public override string ToString() => !StartOnMesh ? "start not on the navmesh" : !GoalOnMesh ? "goal not on the navmesh" :
            string.Create(CultureInfo.InvariantCulture, $"{(Complete ? "complete" : "PARTIAL (no way there)")}, {Points.Count} waypoints, {Length:F0} m, {Polys} polys");
    }

    /// <summary>
    /// World metres, AC frame (ew = lbX*192 + local x, ns = lbY*192 + local y). <paramref name="aUp"/>
    /// and <paramref name="bUp"/> are the heights to search from (the ground there): the nearest
    /// polygon is nearest in 3D, so searching from height 0 picked low ground metres away on a slope.
    /// <paramref name="goalReach"/>: how far round the goal to look for walkable ground (the plugin
    /// uses 12 m sideways for a direct goal).
    /// </summary>
    public Result Route(float aEw, float aNs, float bEw, float bNs, float aUp = float.NaN, float bUp = float.NaN, float goalReach = 3f)
    {
        bool aKnown = !float.IsNaN(aUp), bKnown = !float.IsNaN(bUp);
        _q.FindNearestPoly(new RcVec3f(aEw, aKnown ? aUp : 0, aNs), new RcVec3f(3, aKnown ? 8 : 400, 3), _filter, out long sRef, out RcVec3f sPt, out _);
        _q.FindNearestPoly(new RcVec3f(bEw, bKnown ? bUp : 0, bNs), new RcVec3f(goalReach, bKnown ? 8 : 400, goalReach), _filter, out long gRef, out RcVec3f gPt, out _);
        if (sRef == 0 || gRef == 0) return new Result(sRef != 0, gRef != 0, false, new(), 0, 0);
        Span<long> polys = new long[4096];
        _q.FindPath(sRef, gRef, sPt, gPt, _filter, polys, out int pc, 4096);
        bool complete = pc > 0 && polys[pc - 1] == gRef;
        Span<DtStraightPath> sp = new DtStraightPath[2048];
        _q.FindStraightPath(sPt, gPt, polys[..pc], pc, sp, out int spc, 2048, 0);
        var pts = new List<(float, float, float)>(spc);
        float len = 0;
        for (int i = 0; i < spc; i++)
        {
            pts.Add((sp[i].pos.X, sp[i].pos.Z, sp[i].pos.Y));
            if (i > 0) len += MathF.Sqrt(MathF.Pow(sp[i].pos.X - sp[i - 1].pos.X, 2) + MathF.Pow(sp[i].pos.Z - sp[i - 1].pos.Z, 2));
        }
        return new Result(true, true, complete, pts, len, pc);
    }

    /// <summary>
    /// Where the path's straight legs pass through collision at body height: each leg in 1 m steps,
    /// raised 0.7, 1.1 and 1.6 m above the ground under it (the higher of the leg's own height and
    /// the terrain, so a leg over a hump doesn't dip underground). Returns the crossings.
    /// </summary>
    /// <summary>
    /// A walk to a portal planned the way RynthNav plans its last leg (StepGoto/PlanGoto): portals
    /// near the goal (from the Atlas) are circles to keep out of, the step's own portal (within 15 u
    /// of the goal) excepted; the goal polygon is looked up within 12 u; the path uses the avoid
    /// filter (start and goal polygons allowed) and falls back to the default filter.
    /// <paramref name="avoidGoalLookup"/> = true looks the goal up WITH the avoid filter, as
    /// RynthNav 0.6.4 did: a big polygon touching another portal's circle then hides the goal.
    /// </summary>
    public Result PortalWalk(float aEw, float aNs, float aUp, float gEw, float gNs, float gUp,
        IEnumerable<(double x, double y)> atlasPortals, bool avoidGoalLookup, out string how)
    {
        var avoid = new RynthNav.Routing.PortalAvoid();
        var map = atlasPortals.Where(p => Math.Abs(p.x - gEw) < 300 && Math.Abs(p.y - gNs) < 300)
            .Select(p => new RynthNav.Routing.PortalAvoid.Spot(p.x, p.y, RynthNav.Routing.PortalAvoid.MapRadius, "")).ToList();
        avoid.Set(Array.Empty<RynthNav.Routing.PortalAvoid.Spot>(), map, gEw, gNs, 15.0);
        var walk = new RynthNav.Routing.AvoidFilter(avoid);
        _q.FindNearestPoly(new RcVec3f(aEw, aUp, aNs), new RcVec3f(8, 64, 8), _filter, out long sRef, out RcVec3f sPt, out _);
        if (sRef == 0) { how = "start off the mesh"; return new Result(false, false, false, new(), 0, 0); }
        walk.AllowA = sRef; walk.AllowB = 0;
        _q.FindNearestPoly(new RcVec3f(gEw, gUp, gNs), new RcVec3f(12, 256, 12), avoidGoalLookup ? walk : _filter, out long gRef, out RcVec3f gPt, out _);
        if (gRef == 0) { how = $"no goal polygon within 12 u ({avoid.Count} portal circles near)"; return new Result(true, false, false, new(), 0, 0); }
        walk.AllowB = gRef;
        Span<long> polys = new long[4096];
        _q.FindPath(sRef, gRef, sPt, gPt, walk, polys, out int pc, 4096);
        how = "round the portals";
        if (pc == 0 || polys[pc - 1] != gRef) { _q.FindPath(sRef, gRef, sPt, gPt, _filter, polys, out pc, 4096); how = "no way round the portals: default filter"; }
        bool complete = pc > 0 && polys[pc - 1] == gRef;
        Span<DtStraightPath> sp = new DtStraightPath[2048];
        _q.FindStraightPath(sPt, gPt, polys[..pc], pc, sp, out int spc, 2048, 0);
        var pts = new List<(float, float, float)>(spc);
        float len = 0;
        for (int i = 0; i < spc; i++)
        {
            pts.Add((sp[i].pos.X, sp[i].pos.Z, sp[i].pos.Y));
            if (i > 0) len += MathF.Sqrt(MathF.Pow(sp[i].pos.X - sp[i - 1].pos.X, 2) + MathF.Pow(sp[i].pos.Z - sp[i - 1].pos.Z, 2));
        }
        return new Result(true, true, complete, pts, len, pc);
    }

    /// <summary>Heights above the ground tested: above the 0.6 m a character steps up, up to head height.</summary>
    private static readonly float[] BodyHeights = { 0.7f, 1.1f, 1.6f };

    public static List<(float ew, float ns, uint src)> Crossings(List<(float ew, float ns, float up)> pts, List<CollisionGeometry.Tri> tris, Func<float, float, float>? terrainZ = null)
    {
        var hits = new List<(float, float, uint)>();
        for (int i = 1; i < pts.Count; i++)
        {
            var (ax, ay, az) = pts[i - 1]; var (bx, by, bz) = pts[i];
            float minX = Math.Min(ax, bx) - 0.1f, maxX = Math.Max(ax, bx) + 0.1f, minY = Math.Min(ay, by) - 0.1f, maxY = Math.Max(ay, by) + 0.1f;
            var near = tris.Where(t => t.MaxX >= minX && t.MinX <= maxX && t.MaxY >= minY && t.MinY <= maxY).ToList();
            if (near.Count == 0) continue;
            float len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int steps = Math.Max(1, (int)MathF.Ceiling(len));
            System.Numerics.Vector3 P(float f)
            {
                float x = ax + (bx - ax) * f, y = ay + (by - ay) * f, z = az + (bz - az) * f;
                if (terrainZ != null) { float tz = terrainZ(x, y); if (!float.IsNaN(tz) && tz > z) z = tz; }
                return new System.Numerics.Vector3(x, y, z);
            }
            bool found = false;
            for (int s = 0; s < steps && !found; s++)
            {
                var q0 = P((float)s / steps); var q1 = P((float)(s + 1) / steps);
                foreach (var t in near)
                {
                    foreach (float h in BodyHeights)
                    {
                        var p0 = q0 with { Z = q0.Z + h }; var p1 = q1 with { Z = q1.Z + h };
                        if (t.Hit(p0, p1, out var at)) { hits.Add((at.X, at.Y, t.Src)); found = true; break; }
                    }
                    if (found) break;
                }
            }
        }
        return hits;
    }
}
