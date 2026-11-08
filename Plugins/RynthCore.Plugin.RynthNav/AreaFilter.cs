using System;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace RynthNav.Routing;

/// <summary>
/// Another filter (the portal one, AvoidFilter), minus every polygon whose middle lies in ground to
/// keep off (<see cref="Inside"/>: a building's outline, BuildingOutlines), so the route maker's legs go
/// round buildings rather than through their rooms. The start's and the goal's polygons are always
/// allowed. TilePathFinder plans with it first and without it when there is no way round. Only the
/// middle: leaving out every polygon with a corner inside an outline (tried 2026-10-30) also left out
/// big open-ground polygons that reach under a roof's eaves (an outline is the whole model's hull), and
/// the real loops by Holtburg walked further for it and no less inside buildings; by the middle, the
/// loop in town kept every leg outside (0 yd inside, 51 yd without it) and a shorter lap. One worker
/// only (it keeps the last start and goal).
/// </summary>
internal sealed class AreaFilter : IDtQueryFilter
{
    private readonly IDtQueryFilter _inner;
    public Func<double, double, bool>? Inside;
    public long AllowA, AllowB;

    public AreaFilter(IDtQueryFilter inner) { _inner = inner; }

    public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly)
    {
        if (!_inner.PassFilter(refs, tile, poly)) return false;
        if (Inside == null || refs == AllowA || refs == AllowB || poly.vertCount == 0) return true;
        float[] v = tile.data.verts;
        double x = 0, y = 0;
        for (int i = 0; i < poly.vertCount; i++)
        {
            int k = poly.verts[i] * 3;
            x += v[k];
            y += v[k + 2];
        }
        return !Inside(x / poly.vertCount, y / poly.vertCount);
    }

    public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly,
        long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        => _inner.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
}
