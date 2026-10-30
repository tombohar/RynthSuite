using System;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace RynthNav.Routing;

/// <summary>
/// Detour's default filter, minus every polygon that reaches into a portal's circle
/// (<see cref="PortalAvoid"/>), so the detailed path goes round portals that aren't the step's
/// target. The polygon under the player and the goal's polygon are always allowed (a path must
/// start and end somewhere); a plan that can't reach its goal this way is planned again with the
/// default filter and the steering check keeps the walker out of the circles. Tick thread only.
/// </summary>
internal sealed class AvoidFilter : IDtQueryFilter
{
    private readonly DtQueryDefaultFilter _inner = new();
    private readonly PortalAvoid _avoid;
    private readonly double[] _xs = new double[8], _ys = new double[8];
    public long AllowA, AllowB;

    public AvoidFilter(PortalAvoid avoid) { _avoid = avoid; }

    public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly)
    {
        if (!_inner.PassFilter(refs, tile, poly)) return false;
        if (_avoid.Count == 0 || refs == AllowA || refs == AllowB) return true;
        int n = Math.Min(poly.vertCount, _xs.Length);
        float[] v = tile.data.verts;
        for (int i = 0; i < n; i++)
        {
            int k = poly.verts[i] * 3;
            _xs[i] = v[k];        // east
            _ys[i] = v[k + 2];    // north
        }
        return !_avoid.Touches(_xs.AsSpan(0, n), _ys.AsSpan(0, n));
    }

    public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly,
        long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        => _inner.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
}
