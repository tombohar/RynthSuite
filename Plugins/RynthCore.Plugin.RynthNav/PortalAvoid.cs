using System;
using System.Collections.Generic;

namespace RynthNav.Routing;

/// <summary>
/// Portals a walk must not touch: walking into one teleports you (17:53 on 10-01: a goto from
/// Holtburg ran straight into the Aerlinthe island portal). Every portal near the player that isn't
/// the current step's own target is a circle to keep out of. The detailed planner leaves out
/// navmesh polygons that reach into a circle (AvoidFilter), and the steering check turns the walker
/// round a circle in its way, whatever it is heading for (a path corner, the coarse route's aim, a
/// blind push). World units (east, north); headings in degrees, 0 = north, 90 = east.
/// Host-free and Detour-free: the route tests drive it.
/// </summary>
internal sealed class PortalAvoid
{
    /// <summary>Around a portal the client knows (exact position): its reach (~1.4 m) + the body (~0.5 m) + room.</summary>
    public static double LiveRadius { get; set; } = 4.0;   // the "portals" avoidance setting (rynthnav.json avoidPortalMetres)
    /// <summary>Around a portal from the Atlas (positions to 0.01 degree, ±1.2 m each way): more room.</summary>
    public static double MapRadius => LiveRadius + 1.0;
    /// <summary>Steering keeps this much past a circle's edge.</summary>
    public const double SteerMargin = 0.5;

    public readonly struct Spot
    {
        public readonly double X, Y, R;
        public readonly string Name;
        public Spot(double x, double y, double r, string name) { X = x; Y = y; R = r; Name = name; }
    }

    private readonly List<Spot> _spots = new();
    public IReadOnlyList<Spot> Spots => _spots;
    public int Count => _spots.Count;
    public int Version { get; private set; }

    /// <summary>
    /// Replaces the set: <paramref name="live"/> first (exact), then <paramref name="map"/> points that
    /// no live portal stands within 5 m of. Spots within <paramref name="exceptRadius"/> of
    /// (<paramref name="exceptX"/>, <paramref name="exceptY"/>) are the step's own target: left out.
    /// </summary>
    public void Set(IEnumerable<Spot> live, IEnumerable<Spot> map, double exceptX = double.NaN, double exceptY = double.NaN, double exceptRadius = 0)
    {
        _spots.Clear();
        foreach (Spot s in live) if (!Excepted(s, exceptX, exceptY, exceptRadius)) _spots.Add(s);
        int liveCount = _spots.Count;
        foreach (Spot s in map)
        {
            if (Excepted(s, exceptX, exceptY, exceptRadius)) continue;
            bool dup = false;
            for (int i = 0; i < liveCount && !dup; i++) dup = Dist(_spots[i].X, _spots[i].Y, s.X, s.Y) < 5.0;
            if (!dup) _spots.Add(s);
        }
        Version++;
    }

    public void Clear() { if (_spots.Count > 0) { _spots.Clear(); Version++; } }

    private static bool Excepted(Spot s, double x, double y, double r) =>
        !double.IsNaN(x) && Dist(s.X, s.Y, x, y) <= r;

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>The spot whose circle (x, y) is inside, or -1.</summary>
    public int Inside(double x, double y, double extra = 0)
    {
        for (int i = 0; i < _spots.Count; i++)
            if (Dist(_spots[i].X, _spots[i].Y, x, y) < _spots[i].R + extra) return i;
        return -1;
    }

    /// <summary>
    /// True when a convex polygon (2D vertices, east/north) reaches into any circle: a vertex inside,
    /// the centre inside the polygon, or an edge passing closer than the radius.
    /// </summary>
    public bool Touches(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        if (_spots.Count == 0 || xs.Length < 3) return false;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < xs.Length; i++)
        {
            minX = Math.Min(minX, xs[i]); maxX = Math.Max(maxX, xs[i]);
            minY = Math.Min(minY, ys[i]); maxY = Math.Max(maxY, ys[i]);
        }
        foreach (Spot s in _spots)
        {
            if (s.X < minX - s.R || s.X > maxX + s.R || s.Y < minY - s.R || s.Y > maxY + s.R) continue;
            if (PointInPolygon(s.X, s.Y, xs, ys)) return true;
            for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
                if (SegmentDistance(s.X, s.Y, xs[j], ys[j], xs[i], ys[i]) < s.R) return true;
        }
        return false;
    }

    private static bool PointInPolygon(double x, double y, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        bool inside = false;
        for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
            if ((ys[i] > y) != (ys[j] > y) && x < (xs[j] - xs[i]) * (y - ys[i]) / (ys[j] - ys[i]) + xs[i]) inside = !inside;
        return inside;
    }

    public static double SegmentDistance(double x, double y, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1);
        return Dist(x, y, ax + dx * t, ay + dy * t);
    }

    /// <summary>
    /// The heading to take instead of <paramref name="heading"/> so the next
    /// <paramref name="lookahead"/> units don't enter a circle: unchanged when the way is clear;
    /// the tangent past the nearest circle in the way, on the side nearer the wanted heading; or,
    /// already inside a circle, straight out of it (leaning toward the wanted heading).
    /// <paramref name="spot"/> is the circle steered round (-1 = none).
    /// </summary>
    public double Steer(double x, double y, double heading, double lookahead, out int spot)
    {
        spot = -1;
        if (_spots.Count == 0) return heading;
        double h = heading * Math.PI / 180.0, ux = Math.Sin(h), uy = Math.Cos(h);

        // Inside one or more circles (a row of portals overlaps into one wall: Holtburg's four hub
        // portals stand ~5 m apart, and the lifestone drops you beside them): out along the summed
        // outward directions of every circle you're in, so the way out doesn't flip from one
        // circle to the next each tick (10-01 19:46, "a full on spazz attack").
        double sx = 0, sy = 0;
        for (int i = 0; i < _spots.Count; i++)
        {
            Spot s = _spots[i];
            double ox = x - s.X, oy = y - s.Y, ol = Math.Sqrt(ox * ox + oy * oy);
            if (ol >= s.R) continue;
            if (spot < 0) spot = i;
            if (ol < 1e-6) { ox = -ux; oy = -uy; ol = 1; }
            double depth = (s.R - ol) / s.R + 0.1;
            sx += ox / ol * depth; sy += oy / ol * depth;
        }
        if (spot >= 0)
        {
            double sl = Math.Sqrt(sx * sx + sy * sy);
            if (sl < 1e-6) { sx = -ux; sy = -uy; sl = 1; }
            sx /= sl; sy /= sl;
            // Out, plus whatever of the wanted heading doesn't lead back in.
            double along = ux * sx + uy * sy;
            double tx = ux - along * sx, ty = uy - along * sy;
            return Norm(Math.Atan2(sx + 0.5 * tx, sy + 0.5 * ty) * 180.0 / Math.PI);
        }

        spot = Blocking(x, y, heading, lookahead);
        if (spot < 0)
        {
            if (++_clearCalls > 15) _side = 0;   // clear for half a second: free to pick a side again
            return heading;
        }
        _clearCalls = 0;

        // Pick a side once and keep it while something is in the way, then turn further that way
        // until the new heading clears every circle (overlapping circles = one obstacle).
        if (_side == 0)
        {
            double l0 = Tangent(x, y, _spots[spot], -1), r0 = Tangent(x, y, _spots[spot], +1);
            _side = AngleDiff(l0, heading) <= AngleDiff(r0, heading) ? -1 : +1;
        }
        double h2 = heading;
        for (int k = 0; k < 8; k++)
        {
            int b = Blocking(x, y, h2, lookahead);
            if (b < 0) break;
            spot = b;
            double t = Tangent(x, y, _spots[b], _side);
            // Never turn back the other way: only take the tangent if it turns further toward _side.
            double turn = Norm(t - h2); if (turn > 180) turn -= 360;
            h2 = (_side < 0 ? turn < 0 : turn > 0) ? t : Norm(h2 + _side * 10.0);
        }
        return h2;
    }

    private int _side;          // -1 left, +1 right, 0 not chosen: the side kept while something is in the way
    private int _clearCalls;

    /// <summary>The nearest circle the stretch from (x, y) along <paramref name="heading"/> passes through, or -1.</summary>
    private int Blocking(double x, double y, double heading, double lookahead)
    {
        double h = heading * Math.PI / 180.0, ux = Math.Sin(h), uy = Math.Cos(h);
        int spot = -1;
        double best = double.MaxValue;
        for (int i = 0; i < _spots.Count; i++)
        {
            Spot s = _spots[i];
            double dx = s.X - x, dy = s.Y - y;
            double along = dx * ux + dy * uy;
            if (along <= 0 || along > lookahead + s.R) continue;
            double perp = Math.Abs(dx * uy - dy * ux);
            if (perp >= s.R + SteerMargin * 0.5) continue;
            if (along < best) { best = along; spot = i; }
        }
        return spot;
    }

    /// <summary>The heading that just grazes circle <paramref name="c"/> on <paramref name="side"/> (-1 left, +1 right).</summary>
    private static double Tangent(double x, double y, Spot c, int side)
    {
        double cx = c.X - x, cy = c.Y - y, d = Math.Sqrt(cx * cx + cy * cy);
        double toCentre = Math.Atan2(cx, cy) * 180.0 / Math.PI;
        double off = Math.Asin(Math.Min(1.0, (c.R + SteerMargin) / Math.Max(d, 1e-6))) * 180.0 / Math.PI;
        return Norm(toCentre + side * off);
    }

    /// <summary>
    /// A path (east, north corners) bent round the circles it passes through: a corner is added
    /// beside each circle (on the side the path passes, or the other side), until no segment enters
    /// a circle. A corner is only taken when it is <paramref name="onMesh"/> AND both new legs (to
    /// it and from it) are <paramref name="legOnMesh"/>; when neither side qualifies the path is
    /// left as it was there (the steering check keeps the walker out of the circle). Before
    /// 2026-10-02 a corner only needed navmesh within 1.5 m, so a bend could put the walker in a
    /// tree's no-go ring beside a portal (C9A8, 10-01 21:03). A circle the path starts or ends
    /// inside is left alone (the steering gets out of the first; the second is the goal's).
    /// Big navmesh polygons on open ground let a straight path cut across a portal even when the
    /// filter leaves out every polygon it can; this bends it.
    /// </summary>
    public List<(double X, double Y)> Bend(IReadOnlyList<(double X, double Y)> path, Func<double, double, bool>? onMesh = null,
        int maxInserts = 12, Func<double, double, double, double, bool>? legOnMesh = null)
    {
        var pts = new List<(double X, double Y)>(path);
        if (_spots.Count == 0 || pts.Count < 2) return pts;
        // Circles already found impossible to bend round on one leg: don't try them again there.
        var tried = new HashSet<(int, int)>();
        for (int pass = 0; pass < maxInserts; pass++)
        {
            bool changed = false;
            for (int i = 1; i < pts.Count && !changed; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                for (int k = 0; k < _spots.Count; k++)
                {
                    Spot s = _spots[k];
                    if (Dist(a.X, a.Y, s.X, s.Y) < s.R || Dist(b.X, b.Y, s.X, s.Y) < s.R) continue;
                    double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
                    double t = l2 < 1e-12 ? 0 : Math.Clamp(((s.X - a.X) * dx + (s.Y - a.Y) * dy) / l2, 0, 1);
                    double px = a.X + dx * t, py = a.Y + dy * t;
                    if (Dist(px, py, s.X, s.Y) >= s.R) continue;
                    if (tried.Contains((HashCorner(a, b), k))) continue;
                    double nx = px - s.X, ny = py - s.Y, nl = Math.Sqrt(nx * nx + ny * ny);
                    if (nl < 1e-6) { double l = Math.Sqrt(l2); nx = -dy / l; ny = dx / l; nl = 1; }
                    nx /= nl; ny /= nl;
                    double off = s.R + SteerMargin + 0.5;
                    (double X, double Y) c1 = (s.X + nx * off, s.Y + ny * off), c2 = (s.X - nx * off, s.Y - ny * off);
                    bool Good((double X, double Y) c) =>
                        (onMesh == null || onMesh(c.X, c.Y)) &&
                        (legOnMesh == null || (legOnMesh(a.X, a.Y, c.X, c.Y) && legOnMesh(c.X, c.Y, b.X, b.Y)));
                    (double X, double Y)? pick = Good(c1) ? c1 : Good(c2) ? c2 : null;
                    if (pick == null) { tried.Add((HashCorner(a, b), k)); continue; }   // keep this leg as it is
                    pts.Insert(i, pick.Value);
                    changed = true;
                    break;
                }
            }
            if (!changed) break;
        }
        return pts;
    }

    private static int HashCorner((double X, double Y) a, (double X, double Y) b) =>
        HashCode.Combine(Math.Round(a.X, 2), Math.Round(a.Y, 2), Math.Round(b.X, 2), Math.Round(b.Y, 2));

    private static double Norm(double deg) { deg %= 360.0; return deg < 0 ? deg + 360.0 : deg; }
    private static double AngleDiff(double a, double b) { double d = Math.Abs(Norm(a) - Norm(b)); return d > 180 ? 360 - d : d; }
}
