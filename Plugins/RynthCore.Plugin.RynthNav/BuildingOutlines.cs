using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// The ground outlines of Dereth's buildings, from NavData\worldmap.vec (RynthNav.Baker
/// --map-vectors; the engine's World Map draws the same file): each building's convex outline, in
/// world units. A building's cells are walkable on the landscape navmesh (the baker includes their
/// floors), so the route maker keeps a hunting loop's points out of these outlines, and its legs off
/// their floors where there is another way. Read once (on a worker), then never changed: any thread
/// may ask. Host-free: the route tests read a made-up file.
///
/// File (little-endian): "RVEC", u32 version (1), u32 building count, u32 road segment count; per
/// building a u8 corner count, then that many (f32 x, f32 y) in map cells (24 yards; x east, y north,
/// from the map's south-west corner); the road segments follow (not read here).
/// </summary>
internal sealed class BuildingOutlines
{
    public const string FileName = "worldmap.vec";
    private const double CellUnits = 24.0;
    /// <summary>The widest margin <see cref="Inside"/> is asked for: buildings are filed under every landblock this near.</summary>
    public const double MaxMargin = 12.0;

    private readonly double[][] _xs, _ys;
    private readonly double[] _minX, _minY, _maxX, _maxY;
    private readonly Dictionary<uint, List<int>> _byLandblock = new();

    public int Count => _xs.Length;

    private BuildingOutlines(List<(double X, double Y)[]> polys)
    {
        int n = polys.Count;
        _xs = new double[n][]; _ys = new double[n][];
        _minX = new double[n]; _minY = new double[n]; _maxX = new double[n]; _maxY = new double[n];
        for (int i = 0; i < n; i++)
        {
            var p = polys[i];
            _xs[i] = new double[p.Length]; _ys[i] = new double[p.Length];
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int k = 0; k < p.Length; k++)
            {
                _xs[i][k] = p[k].X; _ys[i][k] = p[k].Y;
                x0 = Math.Min(x0, p[k].X); y0 = Math.Min(y0, p[k].Y); x1 = Math.Max(x1, p[k].X); y1 = Math.Max(y1, p[k].Y);
            }
            _minX[i] = x0; _minY[i] = y0; _maxX[i] = x1; _maxY[i] = y1;
            int lx0 = Lb(x0 - MaxMargin), lx1 = Lb(x1 + MaxMargin), ly0 = Lb(y0 - MaxMargin), ly1 = Lb(y1 + MaxMargin);
            for (int x = lx0; x <= lx1; x++)
                for (int y = ly0; y <= ly1; y++)
                {
                    uint lb = (uint)((x << 8) | y);
                    if (!_byLandblock.TryGetValue(lb, out List<int>? list)) _byLandblock[lb] = list = new List<int>();
                    list.Add(i);
                }
        }
    }

    private static int Lb(double world) => (int)Math.Clamp(Math.Floor(world / 192.0), 0, 255);

    /// <summary>Outlines given in world units (the tests).</summary>
    public static BuildingOutlines FromPolygons(IEnumerable<(double X, double Y)[]> polys)
    {
        var list = new List<(double X, double Y)[]>();
        foreach (var p in polys) if (p.Length >= 3) list.Add(p);
        return new BuildingOutlines(list);
    }

    /// <summary>The file at <paramref name="path"/>; null when it isn't there or isn't one (the caller says so).</summary>
    public static BuildingOutlines? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(fs);
        }
        catch { return null; }
    }

    /// <summary>Reads the buildings of a worldmap.vec stream. Throws InvalidDataException on a file that isn't one.</summary>
    public static BuildingOutlines Read(Stream s)
    {
        using var r = new BinaryReader(s, System.Text.Encoding.ASCII, leaveOpen: true);
        if (r.ReadUInt32() != 0x4345_5652u) throw new InvalidDataException("not a vector file");   // "RVEC"
        if (r.ReadUInt32() != 1u) throw new InvalidDataException("a newer vector file version");
        uint buildings = r.ReadUInt32();
        r.ReadUInt32();   // road segments: not needed
        if (buildings > 200_000) throw new InvalidDataException("too many buildings");
        var polys = new List<(double X, double Y)[]>((int)buildings);
        for (uint b = 0; b < buildings; b++)
        {
            int n = r.ReadByte();
            var p = new (double X, double Y)[n];
            for (int k = 0; k < n; k++)
            {
                float x = r.ReadSingle(), y = r.ReadSingle();
                p[k] = (x * CellUnits, y * CellUnits);
            }
            bool finite = true;
            foreach (var (x, y) in p) finite &= double.IsFinite(x) && double.IsFinite(y);
            if (n >= 3 && finite) polys.Add(p);
        }
        return new BuildingOutlines(polys);
    }

    /// <summary>
    /// The building whose outline holds (x, y), or comes within <paramref name="margin"/> yards of it
    /// (at most <see cref="MaxMargin"/>); -1 when none does.
    /// </summary>
    public int Inside(double x, double y, double margin = 0)
    {
        margin = Math.Clamp(margin, 0, MaxMargin);
        if (x < 0 || y < 0) return -1;
        uint lb = (uint)((Lb(x) << 8) | Lb(y));
        if (!_byLandblock.TryGetValue(lb, out List<int>? list)) return -1;
        foreach (int i in list)
        {
            if (x < _minX[i] - margin || x > _maxX[i] + margin || y < _minY[i] - margin || y > _maxY[i] + margin) continue;
            if (PointInPolygon(x, y, _xs[i], _ys[i])) return i;
            if (margin > 0)
            {
                double[] xs = _xs[i], ys = _ys[i];
                for (int a = 0, b = xs.Length - 1; a < xs.Length; b = a++)
                    if (SegmentDistance(x, y, xs[b], ys[b], xs[a], ys[a]) < margin) return i;
            }
        }
        return -1;
    }

    private static bool PointInPolygon(double x, double y, double[] xs, double[] ys)
    {
        bool inside = false;
        for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
            if ((ys[i] > y) != (ys[j] > y) && x < (xs[j] - xs[i]) * (y - ys[i]) / (ys[j] - ys[i]) + xs[i]) inside = !inside;
        return inside;
    }

    private static double SegmentDistance(double x, double y, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1);
        double px = ax + dx * t - x, py = ay + dy * t - y;
        return Math.Sqrt(px * px + py * py);
    }
}
