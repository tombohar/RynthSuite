using System.Numerics;

namespace RynthNav.TownNet;

/// <summary>Small geometry helpers: 2D tests on the floor plan, 3D segment-through-polygon.</summary>
internal static class Geo
{
    /// <summary>Is (x, y) inside the polygon's outline seen from above (edges count as inside within eps)?</summary>
    public static bool InsideXY(IReadOnlyList<Vector3> poly, float x, float y, float eps = 0.01f)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector3 a = poly[i], b = poly[j];
            if ((a.Y > y) != (b.Y > y))
            {
                float xc = (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X;
                if (x < xc) inside = !inside;
            }
        }
        if (inside || eps <= 0) return inside;
        return DistToOutlineXY(poly, x, y) <= eps;
    }

    /// <summary>Distance from (x, y) to the polygon's outline seen from above (0 inside when filled).</summary>
    public static float DistXY(IReadOnlyList<Vector3> poly, float x, float y, bool filled)
    {
        if (filled && poly.Count >= 3 && AreaXY(poly) > 1e-4f && InsideXY(poly, x, y, 0)) return 0;
        return DistToOutlineXY(poly, x, y);
    }

    public static float DistToOutlineXY(IReadOnlyList<Vector3> poly, float x, float y)
    {
        float best = float.MaxValue;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
            best = MathF.Min(best, DistToSegXY(poly[j].X, poly[j].Y, poly[i].X, poly[i].Y, x, y));
        return best;
    }

    public static float DistToSegXY(float ax, float ay, float bx, float by, float px, float py)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        float t = len2 < 1e-12f ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        float ex = ax + t * dx - px, ey = ay + t * dy - py;
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    public static float AreaXY(IReadOnlyList<Vector3> poly)
    {
        float a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += poly[j].X * poly[i].Y - poly[i].X * poly[j].Y;
        return MathF.Abs(a) * 0.5f;
    }

    /// <summary>Height of a (non-vertical) planar polygon's plane at (x, y).</summary>
    public static float PlaneZ(IReadOnlyList<Vector3> poly, float x, float y)
    {
        Vector3 n = CellDat.Normal(poly);
        if (MathF.Abs(n.Z) < 1e-4f) return poly[0].Z;
        Vector3 p = poly[0];
        return p.Z - (n.X * (x - p.X) + n.Y * (y - p.Y)) / n.Z;
    }

    /// <summary>Does the segment a-b pass through the planar polygon (edges included within eps)?</summary>
    public static bool SegmentCrosses(IReadOnlyList<Vector3> poly, Vector3 a, Vector3 b, float eps = 0.02f)
    {
        if (poly.Count < 3) return false;
        Vector3 n = CellDat.Normal(poly);
        if (n == Vector3.Zero) return false;
        float da = Vector3.Dot(n, a - poly[0]), db = Vector3.Dot(n, b - poly[0]);
        if ((da > 0 && db > 0) || (da < 0 && db < 0)) return false;
        float denom = da - db;
        if (MathF.Abs(denom) < 1e-7f) return false;                 // in the plane: not a crossing
        Vector3 p = a + (b - a) * (da / denom);
        // Inside test in the polygon's plane: project away its dominant axis.
        int drop = MathF.Abs(n.X) > MathF.Abs(n.Y) ? (MathF.Abs(n.X) > MathF.Abs(n.Z) ? 0 : 2) : (MathF.Abs(n.Y) > MathF.Abs(n.Z) ? 1 : 2);
        var flat = poly.Select(v => Flatten(v, drop)).ToArray();
        Vector3 fp = Flatten(p, drop);
        return InsideXY(flat, fp.X, fp.Y, eps);
    }

    private static Vector3 Flatten(Vector3 v, int drop) => drop switch
    {
        0 => new Vector3(v.Y, v.Z, 0),
        1 => new Vector3(v.X, v.Z, 0),
        _ => new Vector3(v.X, v.Y, 0),
    };
}
