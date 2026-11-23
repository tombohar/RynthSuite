using System;
using System.Collections.Generic;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using RynthCore.Plugin.Shared;

namespace RynthNav.Routing;

/// <summary>
/// The navmesh steps a walk is planned with, shared by RynthNav's own walker (Replan) and the
/// "RynthNav.Path" interface (Shared/RynthNavPathApi.cs) v2: the start snapped close up first and
/// only then wide, the corridor kept off portals with the plain filter as the fallback, the corners
/// bent round portals and pushed out from walls, and (FindPathEx, Raycast) the finished path's
/// length and end. Host-free (Detour only): the graph tests drive it on baked floors. Tick thread
/// in the plugin. World units in and out: x east, y north, z height (Detour's x, z, y).
/// </summary>
internal static class NavPathQuery
{
    public const int MaxPolys = 512;
    /// <summary>FindPathEx's straight path before bends (FindPath v1's cap too).</summary>
    public const int MaxStraightCorners = 256;
    /// <summary>A path ending farther than this from the snapped goal didn't get there (partial).</summary>
    public const double GoalSlackUnits = 6.0;
    /// <summary>The tile box FindPath loads, and FindPathEx with maxTiles 0: at most a 5x5 block.</summary>
    public const int DefaultMaxTiles = 25;
    /// <summary>FindPathEx's maxTiles is held to this (a 7x7 block, ~14 MB of tile files the first time).</summary>
    public const int MaxTilesCap = 49;

    /// <summary>The ground we stand on: the mesh keeps the agent radius (2 m) from walls, so close up it's within that, at our height.</summary>
    public static readonly RcVec3f TightStartExtents = new(2.5f, 4, 2.5f);
    /// <summary>Then the wide search, which can pick ground on the far side of a thin wall or a roof overhead.</summary>
    public static readonly RcVec3f WideExtents = new(8, 64, 8);
    private static readonly RcVec3f OnMeshExtents = new(0.5f, 24, 0.5f);
    private static readonly RcVec3f PushExtents = new(0.3f, 24, 0.3f);

    /// <summary>
    /// Whether RynthPluginQueryInterface hands out the "RynthNav.Path" table for a request: any version
    /// from 1 up to the one built. The table is append-only, so a v1 caller gets the v2 table and uses
    /// only the fields it knows (callers check Version and StructSize before a later field).
    /// </summary>
    public static bool ServesInterface(string? iface, uint version) =>
        iface == RynthNavPathApiV1.InterfaceName && version >= 1 && version <= RynthNavPathApiV1.InterfaceVersion;

    /// <summary>FindPathEx's maxTiles: 0 (or less) = the default 25, else at most <see cref="MaxTilesCap"/>.</summary>
    public static int ClampMaxTiles(int maxTiles) => maxTiles <= 0 ? DefaultMaxTiles : Math.Min(maxTiles, MaxTilesCap);

    /// <summary>
    /// The landblock box to load for a query from (sx, sy) to (gx, gy): both ends' landblocks and every one
    /// between, plus a one-tile margin (room for a path that goes round). False when that is more than
    /// <paramref name="maxTiles"/>; with <paramref name="shrinkMargin"/> the box without the margin is
    /// tried first (FindPathEx, Raycast), without it the answer is no (FindPath v1).
    /// </summary>
    public static bool TileBox(double sx, double sy, double gx, double gy, int maxTiles, bool shrinkMargin,
                               out int minX, out int minY, out int maxX, out int maxY)
    {
        int sLbX = Lb(sx), sLbY = Lb(sy), gLbX = Lb(gx), gLbY = Lb(gy);
        for (int margin = 1; margin >= (shrinkMargin ? 0 : 1); margin--)
        {
            minX = Math.Max(0, Math.Min(sLbX, gLbX) - margin); maxX = Math.Min(255, Math.Max(sLbX, gLbX) + margin);
            minY = Math.Max(0, Math.Min(sLbY, gLbY) - margin); maxY = Math.Min(255, Math.Max(sLbY, gLbY) + margin);
            if ((maxX - minX + 1) * (maxY - minY + 1) <= maxTiles) return true;
        }
        minX = minY = maxX = maxY = 0;
        return false;
    }

    private static int Lb(double world) => (int)Math.Clamp(Math.Floor(world / 192.0), 0, 255);

    /// <summary>A portal's own floor: within 2.5 u across, and 3 u up or down, of where it stands.</summary>
    public static readonly RcVec3f PortalFloorExtents = new(2.5f, 3f, 2.5f);

    /// <summary>
    /// The polygon a portal stands on, at its own height (Detour: east, height, north), 0 when the navmesh
    /// has none there. A portal in a cellar or a tunnel under a town (11-18: Zaikhal's Olthoi Tunnel, 7.7 u
    /// under the street) is found by its height, not by the walker's: looked for from the street, the
    /// nearest polygon is the street right above it.
    /// </summary>
    public static long PortalFloor(DtNavMeshQuery q, IDtQueryFilter filter, double east, double height, double north, out RcVec3f pt)
    {
        q.FindNearestPoly(new RcVec3f((float)east, (float)height, (float)north), PortalFloorExtents, filter, out long r, out pt, out _);
        return r;
    }

    /// <summary>Whether a walk from <paramref name="sRef"/> reaches <paramref name="gRef"/>: a whole corridor that ends on it.</summary>
    public static bool Reaches(DtNavMeshQuery q, long sRef, RcVec3f sPt, long gRef, RcVec3f gPt, IDtQueryFilter filter)
    {
        if (sRef == 0 || gRef == 0) return false;
        if (sRef == gRef) return true;
        Span<long> polys = new long[MaxPolys];
        DtStatus st = q.FindPath(sRef, gRef, sPt, gPt, filter, polys, out int pc, polys.Length);
        return !st.Failed() && pc > 0 && polys[pc - 1] == gRef;
    }

    /// <summary>The polygon under <paramref name="p"/> (Detour axes): close up first, then the wide search.</summary>
    public static long SnapStart(DtNavMeshQuery q, RcVec3f p, IDtQueryFilter filter, out RcVec3f pt)
    {
        q.FindNearestPoly(p, TightStartExtents, filter, out long r, out pt, out _);
        if (r == 0) q.FindNearestPoly(p, WideExtents, filter, out r, out pt, out _);
        return r;
    }

    /// <summary>
    /// How far past a circle's edge the plain corridor's straight path must stay, everywhere, for it to be
    /// taken over the corridor round the circles (<see cref="Corridor"/>): the corners pushed off the walls
    /// afterwards move up to the wall clearance (1 m by default).
    /// </summary>
    public const double PlainClearMargin = 2.0;

    /// <summary>
    /// The polygon corridor from sRef to gRef through <paramref name="first"/>; when that doesn't reach
    /// the goal and <paramref name="fallback"/> is set (the first filter keeps out of portals), planned
    /// again through <paramref name="plain"/> (<paramref name="fellBack"/>). <paramref name="status"/> is
    /// the last FindPath's (its partial flag included). When the way round the circles does reach the goal,
    /// the plain corridor is taken instead if its straight path is shorter and keeps clear of every circle
    /// (<paramref name="plainTaken"/>).
    /// </summary>
    public static int Corridor(DtNavMeshQuery q, long sRef, long gRef, RcVec3f sPt, RcVec3f gPt,
                               IDtQueryFilter first, IDtQueryFilter plain, bool fallback, Span<long> polys,
                               out bool fellBack, out DtStatus status, out bool plainTaken)
    {
        fellBack = false; plainTaken = false;
        status = q.FindPath(sRef, gRef, sPt, gPt, first, polys, out int pc, polys.Length);
        if ((pc == 0 || polys[pc - 1] != gRef) && fallback)
        {
            // No way round the portals to there: plan as before (the caller's steering keeps out of them).
            status = q.FindPath(sRef, gRef, sPt, gPt, plain, polys, out pc, polys.Length);
            fellBack = true;
            return pc;
        }
        // The way round the circles can go far round for nothing: every polygon a circle touches is left out,
        // and on open ground one polygon can be a whole town square. 2026-11-18, a DreamWeave Thread in Yaraq:
        // the Thread Exit's 4 m circle touched the edge of the 73 x 161 m polygon that is the town's middle
        // (7D64), so every hunt walk west went round the north edge of town, 262-380 m for 72-119 m, the hunt
        // called them all "too far round" and stood waiting. The plain corridor goes 40-97 m clear of the Exit.
        if (fallback && pc > 0 && first is AvoidFilter round && round.Portals.Count > 0)
        {
            Span<long> alt = new long[polys.Length];
            DtStatus st = q.FindPath(sRef, gRef, sPt, gPt, plain, alt, out int apc, alt.Length);
            if (!st.Failed() && apc > 0 && alt[apc - 1] == gRef
                && StraightLength(q, sPt, gPt, alt[..apc], round.Portals, out double plainLen, out bool clear) && clear
                && StraightLength(q, sPt, gPt, polys[..pc], null, out double roundLen, out _) && plainLen < roundLen - 0.5)
            {
                alt[..apc].CopyTo(polys);
                pc = apc; status = st; plainTaken = true;
            }
        }
        return pc;
    }

    /// <summary>
    /// The flat length of the straight path along <paramref name="corridor"/>; with <paramref name="circles"/>,
    /// whether every leg keeps <see cref="PlainClearMargin"/> past every circle's edge (never, then, when a
    /// circle holds the start or the end). False when Detour gives no straight path (or more corners than it holds).
    /// </summary>
    internal static bool StraightLength(DtNavMeshQuery q, RcVec3f sPt, RcVec3f gPt, Span<long> corridor,
                                        PortalAvoid? circles, out double length, out bool clear)
    {
        length = 0; clear = false;
        Span<DtStraightPath> sp = new DtStraightPath[MaxStraightCorners];
        DtStatus st = q.FindStraightPath(sPt, gPt, corridor, corridor.Length, sp, out int n, MaxStraightCorners, 0);
        if (st.Failed() || n < 1 || st.Has(DtStatus.DT_BUFFER_TOO_SMALL)) return false;
        for (int i = 1; i < n; i++)
        {
            double dx = sp[i].pos.X - sp[i - 1].pos.X, dy = sp[i].pos.Z - sp[i - 1].pos.Z;
            length += Math.Sqrt(dx * dx + dy * dy);
        }
        if (circles == null) return true;
        clear = true;
        foreach (PortalAvoid.Spot s in circles.Spots)
        {
            double need = s.R + PlainClearMargin;
            double a0x = sp[0].pos.X, a0y = sp[0].pos.Z;
            if (n == 1 && PortalAvoid.SegmentDistance(s.X, s.Y, a0x, a0y, a0x, a0y) < need) { clear = false; break; }
            for (int i = 1; i < n; i++)
                if (PortalAvoid.SegmentDistance(s.X, s.Y, sp[i - 1].pos.X, sp[i - 1].pos.Z, sp[i].pos.X, sp[i].pos.Z) < need) { clear = false; break; }
            if (!clear) break;
        }
        return true;
    }

    /// <summary>On the mesh = inside a polygon (the nearest point on the mesh is the point itself), not merely near one.</summary>
    public static bool OnMesh(DtNavMeshQuery q, IDtQueryFilter filter, double x, double y, float z)
    {
        q.FindNearestPoly(new RcVec3f((float)x, z, (float)y), OnMeshExtents, filter, out long r, out RcVec3f np, out _);
        return r != 0 && Math.Abs(np.X - x) < 0.05 && Math.Abs(np.Z - y) < 0.05;
    }

    /// <summary>
    /// The path (east, north corners) bent round any portal it still crosses (big open-ground polygons
    /// let it cut straight over one): <see cref="PortalAvoid.Bend"/> with corners and legs only on the mesh
    /// (a leg is on the mesh when every metre of it is). <paramref name="zAt"/>: the height to look at.
    /// </summary>
    public static List<(double X, double Y)> BendRoundPortals(DtNavMeshQuery q, PortalAvoid portals, List<(double X, double Y)> wps,
                                                              IDtQueryFilter filter, Func<double, double, float> zAt)
    {
        bool OnMeshAt(double x, double y) => OnMesh(q, filter, x, y, zAt(x, y));
        bool LegOnMesh(double ax, double ay, double bx, double by)
        {
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int n = Math.Max(1, (int)Math.Ceiling(len));
            for (int k = 1; k < n; k++)
                if (!OnMeshAt(ax + (bx - ax) * k / n, ay + (by - ay) * k / n)) return false;
            return true;
        }
        return portals.Bend(wps, OnMeshAt, 12, LegOnMesh);
    }

    /// <summary>
    /// Walls: each corner the path turns round sits on the mesh's edge (the agent radius from a building);
    /// push it <paramref name="clearance"/> out along the turn's outside bisector where the ground is
    /// walkable (inside a polygon, not merely near it: near it, the moved corner could sit closer to the
    /// wall than the body is wide). In place, first corner to last; the ends never move. Returns how many moved.
    /// </summary>
    public static int PushFromWalls(DtNavMeshQuery q, List<(double X, double Y)> wps, double clearance,
                                    IDtQueryFilter filter, Func<double, double, float> zAt)
    {
        if (clearance <= 0 || wps.Count <= 2) return 0;
        int moved = 0;
        for (int i = 1; i < wps.Count - 1; i++)
        {
            var (px, py) = wps[i]; var (ax, ay) = wps[i - 1]; var (bx, by) = wps[i + 1];
            double ux = ax - px, uy = ay - py, ul = Math.Sqrt(ux * ux + uy * uy);
            double vx = bx - px, vy = by - py, vl = Math.Sqrt(vx * vx + vy * vy);
            if (ul < 0.5 || vl < 0.5) continue;
            double ox = -(ux / ul + vx / vl), oy = -(uy / ul + vy / vl), ol = Math.Sqrt(ox * ox + oy * oy);
            if (ol < 0.05) continue;                   // straight on: no corner to clear
            double nx = px + ox / ol * clearance, ny = py + oy / ol * clearance;
            q.FindNearestPoly(new RcVec3f((float)nx, zAt(nx, ny), (float)ny), PushExtents, filter, out long rr, out RcVec3f rp, out _);
            if (rr != 0 && Math.Abs(rp.X - nx) < 0.05 && Math.Abs(rp.Z - ny) < 0.05) { wps[i] = (nx, ny); moved++; }
        }
        return moved;
    }

    /// <summary>
    /// "RynthNav.Path" FindPathEx on the tiles already loaded: from (sx, sy, sz) to (gx, gy, gz) with the
    /// Path* <paramref name="flags"/>; <paramref name="portals"/> (its circles already set) for
    /// PathAvoidPortals, <paramref name="wallClearance"/> for PathWallClearance. At most
    /// <paramref name="maxCorners"/> corners. Fills <paramref name="r"/> and returns its code: ResultOk,
    /// ResultPartial (PathAllowPartial only), ResultNoPath, or ResultNoMesh (no mesh at either end).
    /// </summary>
    public static int FindPathEx(DtNavMeshQuery q, double sx, double sy, double sz, double gx, double gy, double gz,
                                 int flags, int maxCorners, AvoidFilter? portals, double wallClearance, PathExResult r)
    {
        r.Reset();
        var plain = new DtQueryDefaultFilter();
        long sRef = SnapStart(q, new RcVec3f((float)sx, (float)sz, (float)sy), plain, out RcVec3f sPt);
        if (sRef == 0) return r.Fail(RynthNavPathApiV1.ResultNoMesh, "no navmesh at the start");
        // The goal is looked up WITHOUT the portal circles; its polygon is always allowed (AllowB), as Replan.
        q.FindNearestPoly(new RcVec3f((float)gx, (float)gz, (float)gy), WideExtents, plain, out long gRef, out RcVec3f gPt, out _);
        if (gRef == 0) return r.Fail(RynthNavPathApiV1.ResultNoMesh, "no navmesh at the goal");

        bool avoid = (flags & RynthNavPathApiV1.PathAvoidPortals) != 0 && portals != null && portals.Portals.Count > 0;
        if (avoid) { portals!.AllowA = sRef; portals.AllowB = gRef; }
        r.Portals = avoid ? portals!.Portals.Count : 0;

        Span<long> polys = new long[MaxPolys];
        IDtQueryFilter first = avoid ? portals! : plain;
        int pc = Corridor(q, sRef, gRef, sPt, gPt, first, plain, avoid, polys, out bool fellBack, out DtStatus st, out bool plainTaken);
        r.Polys = pc; r.FellBack = fellBack; r.PlainClear = plainTaken;
        if (st.Failed() || pc == 0) return r.Fail(RynthNavPathApiV1.ResultNoPath, "no way on the navmesh");
        // Detour's partial result: the corridor ends at the polygon nearest the goal it could reach.
        bool partial = st.IsPartial() || polys[pc - 1] != gRef;

        Span<DtStraightPath> sp = new DtStraightPath[MaxStraightCorners];
        DtStatus sst = q.FindStraightPath(sPt, gPt, polys[..pc], pc, sp, out int spc, MaxStraightCorners, 0);
        if (sst.Failed() || spc < 1) return r.Fail(RynthNavPathApiV1.ResultNoPath, "no straight path");
        r.Truncated = sst.Has(DtStatus.DT_BUFFER_TOO_SMALL);
        RcVec3f end = sp[spc - 1].pos;
        double slackE = end.X - gPt.X, slackN = end.Z - gPt.Z;
        if (Math.Sqrt(slackE * slackE + slackN * slackN) > GoalSlackUnits) partial = true;
        if (partial && (flags & RynthNavPathApiV1.PathAllowPartial) == 0)
            return r.Fail(RynthNavPathApiV1.ResultNoPath, r.Truncated ? "more corners than it plans" : "the goal can't be reached");

        var wps = new List<(double X, double Y)>(spc + 4);
        var ox = new double[Math.Max(spc, 2)]; var oy = new double[ox.Length]; var oz = new double[ox.Length];
        for (int i = 0; i < spc; i++)
        {
            wps.Add((sp[i].pos.X, sp[i].pos.Z));
            ox[i] = sp[i].pos.X; oy[i] = sp[i].pos.Z; oz[i] = sp[i].pos.Y;
        }
        if (spc == 1) { wps.Add(wps[0]); ox[1] = ox[0]; oy[1] = oy[0]; oz[1] = oz[0]; }   // start and goal in one spot
        // Heights for the mesh tests at a bend or a pushed corner: the nearest straight-path corner's.
        float ZAt(double x, double y)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < ox.Length; i++)
            {
                double d = (ox[i] - x) * (ox[i] - x) + (oy[i] - y) * (oy[i] - y);
                if (d < bd) { bd = d; best = i; }
            }
            return (float)oz[best];
        }

        if (avoid)
        {
            int before = wps.Count;
            wps = BendRoundPortals(q, portals!.Portals, wps, plain, ZAt);
            r.Bent = wps.Count - before;
        }
        if ((flags & RynthNavPathApiV1.PathWallClearance) != 0) r.Pushed = PushFromWalls(q, wps, wallClearance, plain, ZAt);

        int limit = Math.Max(2, maxCorners);
        if (wps.Count > limit)
        {
            if ((flags & RynthNavPathApiV1.PathAllowPartial) == 0)
                return r.Fail(RynthNavPathApiV1.ResultNoPath, $"{wps.Count} corners, room for {limit}");
            wps.RemoveRange(limit, wps.Count - limit);   // the first stretch of the way; plan again from its end
            partial = true; r.Truncated = true;
        }

        for (int i = 0; i < wps.Count; i++)
        {
            var (x, y) = wps[i];
            double z = double.NaN;
            for (int k = 0; k < ox.Length && double.IsNaN(z); k++)
                if (ox[k] == x && oy[k] == y) z = oz[k];   // a corner Detour gave: its own height
            if (double.IsNaN(z)) z = HeightAt(q, plain, x, y, ZAt(x, y));
            r.Corners.Add((x, y, z));
            if (i > 0)
            {
                var a = r.Corners[i - 1];
                r.Length += Math.Sqrt((x - a.X) * (x - a.X) + (y - a.Y) * (y - a.Y) + (z - a.Z) * (z - a.Z));
            }
        }
        var last = r.Corners[^1];
        r.EndDistance = Math.Sqrt((gx - last.X) * (gx - last.X) + (gy - last.Y) * (gy - last.Y) + (gz - last.Z) * (gz - last.Z));
        r.Partial = partial;
        r.Code = partial ? RynthNavPathApiV1.ResultPartial : RynthNavPathApiV1.ResultOk;
        return r.Code;
    }

    /// <summary>The mesh's height under (x, y) near <paramref name="zGuess"/>; the guess when there's no polygon there.</summary>
    private static double HeightAt(DtNavMeshQuery q, IDtQueryFilter filter, double x, double y, float zGuess)
    {
        q.FindNearestPoly(new RcVec3f((float)x, zGuess, (float)y), OnMeshExtents, filter, out long r, out RcVec3f np, out _);
        return r != 0 ? np.Y : zGuess;
    }

    /// <summary>
    /// "RynthNav.Path" Raycast on the tiles already loaded: a Detour raycast along the mesh surface from
    /// the start's polygon (snapped as FindPathEx's start) toward the goal. RayClear when it reaches the
    /// goal's spot (Detour's hit parameter 1 or more: no wall in the way), RayBlocked when it hits the
    /// mesh's edge first (a wall, a building, a drop), ResultNoMesh when either end has no mesh.
    /// <paramref name="t"/>: how far along it got (0..1; float.MaxValue = all the way).
    /// </summary>
    public static int Raycast(DtNavMeshQuery q, double sx, double sy, double sz, double gx, double gy, double gz, out float t)
    {
        t = 0;
        var plain = new DtQueryDefaultFilter();
        long sRef = SnapStart(q, new RcVec3f((float)sx, (float)sz, (float)sy), plain, out RcVec3f sPt);
        if (sRef == 0) return RynthNavPathApiV1.ResultNoMesh;
        var goal = new RcVec3f((float)gx, (float)gz, (float)gy);
        q.FindNearestPoly(goal, WideExtents, plain, out long gRef, out _, out _);
        if (gRef == 0) return RynthNavPathApiV1.ResultNoMesh;
        // The visited polygons aren't needed; a full buffer only sets a flag, the ray goes on.
        Span<long> visited = stackalloc long[64];
        DtStatus st = q.Raycast(sRef, sPt, goal, plain, out t, out _, visited, out _, visited.Length);
        if (st.Failed()) return RynthNavPathApiV1.RayBlocked;
        return t >= 1f ? RynthNavPathApiV1.RayClear : RynthNavPathApiV1.RayBlocked;
    }
}

/// <summary>FindPathEx's answer: the corners (east, north, height) and what the info array and the log line say.</summary>
internal sealed class PathExResult
{
    public int Code;
    public readonly List<(double X, double Y, double Z)> Corners = new();
    /// <summary>The sum of the legs (3D), world units.</summary>
    public double Length;
    /// <summary>From the last corner to the requested goal (3D), world units.</summary>
    public double EndDistance;
    public bool Partial, Truncated, FellBack;
    /// <summary>The plain corridor was taken: shorter than the way round the circles, and clear of them all (NavPathQuery.Corridor).</summary>
    public bool PlainClear;
    public int Polys, Portals, Bent, Pushed;
    public string Note = "";

    public void Reset()
    {
        Code = RynthNavPathApiV1.ResultNoPath;
        Corners.Clear();
        Length = EndDistance = 0;
        Partial = Truncated = FellBack = PlainClear = false;
        Polys = Portals = Bent = Pushed = 0;
        Note = "";
    }

    public int Fail(int code, string note)
    {
        Corners.Clear();
        Length = EndDistance = 0;
        Partial = false;
        Code = code; Note = note;
        return code;
    }
}
