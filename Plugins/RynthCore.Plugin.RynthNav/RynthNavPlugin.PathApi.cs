using System;
using System.Collections.Generic;
using System.Diagnostics;
using RynthNav.Routing;
using NavPathApi = RynthCore.Plugin.Shared.RynthNavPathApiV1;

namespace RynthCore.Plugin.RynthNav;

// "RynthNav.Path" version 2 (Shared/RynthNavPathApi.cs): FindPathEx and Raycast, for RynthAi's
// outdoor hunting (a path to a monster that keeps off portals and walls like RynthNav's own walks,
// the reachable point nearest a monster it can't get to, the path's length, and whether a straight
// walk is clear). Called by another plugin from its OnTick (the pump thread, as our OnTick), on the
// same navmesh and query object the walker uses; the walking itself is the caller's. The planning
// steps are NavPathQuery's (host-free, shared with Replan); this part loads the tiles, says whether
// the start is on the landscape at all, and keeps FindPathEx's own portal set. Tick thread.
public sealed partial class RynthNavPlugin
{
    /// <summary>A start this close to the player is the player's: the player's cell says whether it's on the landscape.</summary>
    private const double PathStartIsPlayerUnits = 24.0;

    private readonly PathExResult _pathEx = new();

    // FindPathEx's portals (PathAvoidPortals): the walker's sources (every portal object the client
    // knows, the Atlas's where no live one stands) round the query, never a step's own portal excepted.
    // Its own set, so a query never changes the walker's (or logs its "avoid:" line).
    private readonly PortalAvoid _pathAvoid = new();
    private AvoidFilter? _pathAvoidFilter;
    private readonly List<PortalAvoid.Spot> _pathLive = new(), _pathLiveNear = new(), _pathMapNear = new();
    private long _pathLiveNextMs;

    private void ResetPathApi()
    {
        _pathLive.Clear(); _pathLiveNextMs = 0;
        _pathAvoid.Clear();
    }

    /// <summary>
    /// The start isn't on the landscape mesh: the player is in a dungeon (any world position then is a
    /// dungeon's, not the land's), or in a building's cells and the start is where the player stands
    /// (unless the caller says it is a spot outdoors, <paramref name="startOutdoors"/>: PathStartOutdoors, v3).
    /// </summary>
    private bool PathStartOffLandscape(double sx, double sy, bool startOutdoors = false)
    {
        if ((_cellId & 0xFFFF) < 0x100) return false;                 // outdoors
        if (InDungeon(_cellId) || !IsOnMap(_cellId)) return true;     // a dungeon (the Atlas's, or one it doesn't know)
        if (startOutdoors) return false;                              // a building's doorstep (RynthAi's hunt walking out)
        double dx = sx - _wx, dy = sy - _wy;
        return dx * dx + dy * dy <= PathStartIsPlayerUnits * PathStartIsPlayerUnits;
    }

    /// <summary>FindPathEx's portal circles for a query from (sx, sy) to (gx, gy); live objects read again at most once a second.</summary>
    private AvoidFilter PathPortals(double sx, double sy, double gx, double gy)
    {
        long now = NowMs;
        if (now >= _pathLiveNextMs)
        {
            _pathLiveNextMs = now + AvoidRefreshMs;
            _pathLive.Clear();
            LivePortalSpots(_pathLive, double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity);
        }
        double minX = Math.Min(sx, gx) - AvoidNearUnits, maxX = Math.Max(sx, gx) + AvoidNearUnits;
        double minY = Math.Min(sy, gy) - AvoidNearUnits, maxY = Math.Max(sy, gy) + AvoidNearUnits;
        _pathLiveNear.Clear(); _pathMapNear.Clear();
        foreach (PortalAvoid.Spot s in _pathLive)
            if (s.X >= minX && s.X <= maxX && s.Y >= minY && s.Y <= maxY) _pathLiveNear.Add(s);
        foreach (PortalAvoid.Spot s in AtlasPortalSpots())
            if (s.X >= minX && s.X <= maxX && s.Y >= minY && s.Y <= maxY) _pathMapNear.Add(s);
        _pathAvoid.Set(_pathLiveNear, _pathMapNear);
        return _pathAvoidFilter ??= new AvoidFilter(_pathAvoid);
    }

    internal unsafe int InterfaceFindPathEx(double sx, double sy, double sz, double gx, double gy, double gz,
                                            int flags, int maxTiles, double* points, int maxPoints, int* count, double* info)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (count != null) *count = 0;
        if (info != null) for (int i = 0; i < NavPathApi.InfoLength; i++) info[i] = 0;
        int tiles = NavPathQuery.ClampMaxTiles(maxTiles);
        PathExResult r = _pathEx;
        r.Reset();

        if (points == null || maxPoints < 2) r.Fail(NavPathApi.ResultNoPath, "no room for the corners");
        else if (!_hasPose) r.Fail(NavPathApi.ResultNoMesh, "no player position yet");
        else if (!IsFinite(sx) || !IsFinite(sy) || !IsFinite(sz) || !IsFinite(gx) || !IsFinite(gy) || !IsFinite(gz)) r.Fail(NavPathApi.ResultNoPath, "not a position");
        else if (PathStartOffLandscape(sx, sy, (flags & NavPathApi.PathStartOutdoors) != 0))
            r.Fail(NavPathApi.ResultNoMesh, InDungeon(_cellId) || !IsOnMap(_cellId) ? "in a dungeon" : "in a building");
        else if (!LoadPathBox(sx, sy, gx, gy, tiles, shrinkMargin: true)) r.Fail(NavPathApi.ResultNoPath, $"farther than {tiles} tiles hold");
        else
        {
            AvoidFilter? portals = (flags & NavPathApi.PathAvoidPortals) != 0 ? PathPortals(sx, sy, gx, gy) : null;
            NavPathQuery.FindPathEx(_query, sx, sy, sz, gx, gy, gz, flags, maxPoints, portals, _wallClearance, r);
        }

        int n = r.Code is NavPathApi.ResultOk or NavPathApi.ResultPartial ? r.Corners.Count : 0;
        for (int i = 0; i < n; i++)
        {
            points[i * 3]     = r.Corners[i].X;   // east
            points[i * 3 + 1] = r.Corners[i].Y;   // north
            points[i * 3 + 2] = r.Corners[i].Z;   // height
        }
        if (count != null) *count = n;
        if (info != null && n > 0)
        {
            info[0] = r.Length;
            info[1] = r.Partial ? 1 : 0;
            info[2] = r.Corners[n - 1].X; info[3] = r.Corners[n - 1].Y; info[4] = r.Corners[n - 1].Z;
            info[5] = r.EndDistance;
        }

        if ((flags & NavPathApi.PathQuiet) == 0)
        {
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            string what = r.Code switch
            {
                NavPathApi.ResultOk => $"{n} corners, {r.Polys} polys, {r.Length:F0}u, ends {r.EndDistance:F1}u from the goal",
                NavPathApi.ResultPartial => $"PARTIAL{(r.Truncated ? " (cut short)" : "")}: {n} corners, {r.Polys} polys, {r.Length:F0}u, ends {r.EndDistance:F0}u from the goal",
                NavPathApi.ResultNoMesh => $"no navmesh: {r.Note}",
                _ => $"no path: {r.Note}",
            };
            string extras = "";
            if (r.Portals > 0) extras += $"; {r.Portals} portal(s) avoided{(r.FellBack ? " (no way round them: plain path)" : "")}{(r.Bent > 0 ? $", bent round {r.Bent}" : "")}";
            if (r.Pushed > 0) extras += $"; {r.Pushed} corner(s) {_wallClearance:0.#} m off the walls";
            Host.Log($"[RynthNav] path request ex (flags 0x{flags:X}): {what}{extras}; ({sx:F0},{sy:F0}) -> ({gx:F0},{gy:F0}), {_tileCount} tiles, {ms:F1} ms");
        }
        return r.Code;
    }

    internal int InterfaceRaycast(double sx, double sy, double sz, double gx, double gy, double gz)
    {
        if (!_hasPose) return NavPathApi.ResultNoMesh;
        if (!IsFinite(sx) || !IsFinite(sy) || !IsFinite(sz) || !IsFinite(gx) || !IsFinite(gy) || !IsFinite(gz)) return NavPathApi.RayBlocked;
        if (PathStartOffLandscape(sx, sy)) return NavPathApi.ResultNoMesh;
        if (!LoadPathBox(sx, sy, gx, gy, NavPathQuery.DefaultMaxTiles, shrinkMargin: true)) return NavPathApi.RayBlocked;   // too far to tell
        return NavPathQuery.Raycast(_query, sx, sy, sz, gx, gy, gz, out _);
    }
}
