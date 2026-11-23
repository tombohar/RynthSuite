// PlacedSolid: the space a placed object fills, column by column (2026-11-18).
//
// The dungeon map's placed objects (an altar, a table, a statue: the EnvCells' stabs, as physics triangles)
// were cut at three heights over their floor (0.5, 1.0 and 1.5 m). What lay between or above the cuts was
// missed: the tops of the 0x01003694 tables (1.5 of their 2.4 m2), the 0x020003B3 rails at 0.79-0.92 m,
// beams between 1.5 m and the body's top. And a cut that came out as an open line (a lone plane, a fence, a
// web) read as filled on one side: 0x02001A58 was 31.5 m2 inside against an 11.3 m2 real footprint.
//
// This is the object as solid columns instead: the ground under it in 0.1 m squares, each square with the
// height ranges where something is. They come from
//   - a cross-section every 0.1 m up the object: a closed one (a box's sides cut round) is filled, an open
//     one (a plane, a web's strands) counts as its line only and is never filled;
//   - every sloped or flat polygon at its own height over each square it covers (a table top, a ramp).
// Something is in a body's way where its column has anything between the body's feet + 0.45 and its top.
//
// A model is made once per setup and kept: the same object placed again, turned about the vertical and moved,
// is the same solid in its own frame, so later copies only turn a query point into that frame. A copy that is
// tilted or scaled otherwise (the triangles don't match once turned) gets a model of its own.
//
// Any triangle set is taken, closed or open; the frame's axes follow the object's own edges (the commonest
// edge direction), so a box's sides fall along the columns however it is turned in the world.

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

/// <summary>One object's solid model, in its own frame (metres from its first vertex, turned by <see cref="Yaw"/>).</summary>
internal sealed class PlacedSolid
{
    /// <summary>Column size and cross-section spacing (metres).</summary>
    internal const double Res = 0.1;
    // Bigger objects than this many columns (50 x 50 m) get coarser columns; taller than this many sections, coarser sections.
    private const int MaxColumns = 250_000, MaxLevels = 1200;
    // A copy is the same model when every vertex is within this of the model's, once turned and moved.
    private const double MatchTolerance = 0.02;
    // A walkable face (WalkableNz either way up) is exposed when nothing of the object is this far over it.
    private const double ExposedFrom = 0.15, ExposedTo = 0.6;

    /// <summary>The frame's turn from world east, for the copy it was made from (radians).</summary>
    public readonly double Yaw;
    /// <summary>Column size (<see cref="Res"/> unless the object is huge).</summary>
    public readonly double Step;
    /// <summary>The raster's corner in the model frame, and its size in columns (with one empty column round).</summary>
    public readonly double U0, V0;
    public readonly int NU, NV;
    /// <summary>Its lowest and highest point (model frame: height over the first vertex).</summary>
    public readonly double MinW, MaxW;
    /// <summary>A walkable face at its top (a bench's seat, a dais), for the step-over class.</summary>
    public readonly bool WalkableTop;

    private readonly int[] _start;     // per column, its first range in _ranges (NU * NV + 1)
    private readonly float[] _ranges;  // lo, hi pairs, model frame
    private readonly float[] _ref;     // the triangles' vertices in the model frame (u, v, w), to match a copy
    private readonly int _far;         // the vertex farthest from the first, across: sets a copy's turn
    private readonly int[] _walkFaces; // exposed walkable faces (triangle index)
    private readonly float[] _walkArea;
    private readonly Dictionary<long, PlacedBand> _bands = new();

    public int VertexCount => _ref.Length / 3;
    public int WalkFaceCount => _walkFaces.Length;
    internal double WalkFaceArea(int k) => _walkArea[k];

    /// <summary>Roughly what the model holds in memory (bytes).</summary>
    public long Bytes => 160 + 4L * (_start.Length + _ranges.Length + _ref.Length + _walkFaces.Length + _walkArea.Length);

    // ── Cache ───────────────────────────────────────────────────────────────

    private static readonly Dictionary<uint, List<PlacedSolid>> _cache = new();
    private static long _cacheBytes;
    // Past this the cache starts again (a long session through many dungeons): the maps made keep their models.
    private const long CacheBytesMax = 64L << 20;

    internal static int CachedModels { get { lock (_cache) { int n = 0; foreach (var l in _cache.Values) n += l.Count; return n; } } }
    internal static long CachedBytes { get { lock (_cache) return _cacheBytes; } }
    internal static void ClearCache() { lock (_cache) { _cache.Clear(); _cacheBytes = 0; } }

    /// <summary>
    /// The object <paramref name="setup"/> as placed: its triangles (world metres, three vertices a triangle) as a
    /// model turned and moved into place, the model made now or taken from the cache. Null for fewer than one triangle.
    /// </summary>
    internal static PlacedShape? Place(uint setup, Vector3[] tris)
    {
        if (tris == null || tris.Length < 3) return null;
        int n = tris.Length - tris.Length % 3;
        double ax = tris[0].X, ay = tris[0].Y, az = tris[0].Z;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int k = 0; k < n; k++)
        {
            var p = tris[k];
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        PlacedSolid? model = null;
        double yaw = 0;
        lock (_cache)
        {
            if (_cache.TryGetValue(setup, out var list))
                foreach (var m in list)
                    if (m.VertexCount == n && m.Match(tris, n, ax, ay, az, out yaw)) { model = m; break; }
        }
        if (model == null)
        {
            model = Build(tris, n, ax, ay, az);
            yaw = model.Yaw;
            lock (_cache)
            {
                if (_cacheBytes + model.Bytes > CacheBytesMax) { _cache.Clear(); _cacheBytes = 0; }
                if (!_cache.TryGetValue(setup, out var list)) _cache[setup] = list = new List<PlacedSolid>(1);
                list.Add(model);
                _cacheBytes += model.Bytes;
            }
        }
        return new PlacedShape(model, ax, ay, az, yaw, minX, minY, minZ, maxX, maxY, maxZ);
    }

    // The copy's turn from the far vertex, then every vertex checked.
    private bool Match(Vector3[] t, int n, double ax, double ay, double az, out double yaw)
    {
        double rx = t[_far].X - ax, ry = t[_far].Y - ay;
        double fu = _ref[3 * _far], fv = _ref[3 * _far + 1];
        yaw = fu * fu + fv * fv > 0.0025 && rx * rx + ry * ry > 0.0025 ? Math.Atan2(ry, rx) - Math.Atan2(fv, fu) : Yaw;
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        for (int k = 0; k < n; k++)
        {
            double dx = t[k].X - ax, dy = t[k].Y - ay;
            if (Math.Abs(dx * c + dy * s - _ref[3 * k]) > MatchTolerance
                || Math.Abs(-dx * s + dy * c - _ref[3 * k + 1]) > MatchTolerance
                || Math.Abs(t[k].Z - az - _ref[3 * k + 2]) > MatchTolerance) return false;
        }
        return true;
    }

    private static PlacedSolid Build(Vector3[] t, int n, double ax, double ay, double az)
    {
        // The frame follows the commonest horizontal edge direction (mod 90 degrees): a box's sides along the columns.
        double cs = 0, sn = 0;
        for (int i = 0; i + 2 < n; i += 3)
            for (int e = 0; e < 3; e++)
            {
                var p = t[i + e]; var q = t[i + (e + 1) % 3];
                double dx = q.X - p.X, dy = q.Y - p.Y, h = Math.Sqrt(dx * dx + dy * dy);
                if (h < 1e-4) continue;
                double c = dx / h, s = dy / h, c2 = c * c - s * s, s2 = 2 * c * s;
                cs += h * (c2 * c2 - s2 * s2);
                sn += h * 2 * c2 * s2;
            }
        double yaw = cs * cs + sn * sn > 1e-12 ? Math.Atan2(sn, cs) / 4 : 0;
        double co = Math.Cos(yaw), si = Math.Sin(yaw);
        var refv = new float[n * 3];
        for (int k = 0; k < n; k++)
        {
            double dx = t[k].X - ax, dy = t[k].Y - ay;
            refv[3 * k] = (float)(dx * co + dy * si);
            refv[3 * k + 1] = (float)(-dx * si + dy * co);
            refv[3 * k + 2] = (float)(t[k].Z - az);
        }
        return new PlacedSolid(refv, yaw);
    }

    // ── Making the columns ──────────────────────────────────────────────────

    private readonly struct Piece : IComparable<Piece>
    {
        public readonly int Col; public readonly float Lo, Hi;
        public Piece(int col, float lo, float hi) { Col = col; Lo = lo; Hi = hi; }
        public int CompareTo(Piece o) => Col != o.Col ? Col.CompareTo(o.Col) : Lo.CompareTo(o.Lo);
    }

    private PlacedSolid(float[] refv, double yaw)
    {
        _ref = refv;
        Yaw = yaw;
        int nv = refv.Length / 3, nt = nv / 3;
        double minU = double.MaxValue, minV = double.MaxValue, minW = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue, maxW = double.MinValue, far = -1;
        for (int k = 0; k < nv; k++)
        {
            double u = refv[3 * k], v = refv[3 * k + 1], w = refv[3 * k + 2];
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            minW = Math.Min(minW, w); maxW = Math.Max(maxW, w);
            if (u * u + v * v > far) { far = u * u + v * v; _far = k; }
        }
        MinW = minW; MaxW = maxW;
        double step = Res;
        while (((maxU - minU) / step + 3) * ((maxV - minV) / step + 3) > MaxColumns) step *= 2;
        Step = step;
        U0 = Math.Floor(minU / step) * step - step;
        V0 = Math.Floor(minV / step) * step - step;
        NU = (int)Math.Floor((maxU - U0) / step) + 2;
        NV = (int)Math.Floor((maxV - V0) / step) + 2;
        int ncol = NU * NV;
        var pieces = new List<Piece>();

        // Cross-sections at z_k = (k + 0.5) * lstep, the ones with a vertex under and one at or over them.
        double lstep = step;
        if ((maxW - minW) / lstep > MaxLevels) lstep = (maxW - minW) / MaxLevels;
        int kFirst = (int)Math.Floor(minW / lstep - 0.5) + 1, kLast = (int)Math.Floor(maxW / lstep - 0.5);
        int nl = Math.Max(0, kLast - kFirst + 1);
        if (nl > 0)
        {
            var off = new int[nl + 1];
            void Span(int tri, out int k0, out int k1)
            {
                double z0 = Math.Min(refv[9 * tri + 2], Math.Min(refv[9 * tri + 5], refv[9 * tri + 8]));
                double z1 = Math.Max(refv[9 * tri + 2], Math.Max(refv[9 * tri + 5], refv[9 * tri + 8]));
                k0 = Math.Max(kFirst, (int)Math.Floor(z0 / lstep - 0.5) + 1);
                k1 = Math.Min(kLast, (int)Math.Floor(z1 / lstep - 0.5));
            }
            for (int tri = 0; tri < nt; tri++)
            {
                Span(tri, out int k0, out int k1);
                for (int k = k0; k <= k1; k++) off[k - kFirst + 1]++;
            }
            for (int l = 0; l < nl; l++) off[l + 1] += off[l];
            var byLevel = new int[off[nl]];
            var fillAt = (int[])off.Clone();
            for (int tri = 0; tri < nt; tri++)
            {
                Span(tri, out int k0, out int k1);
                for (int k = k0; k <= k1; k++) byLevel[fillAt[k - kFirst]++] = tri;
            }

            var runLo = new float[ncol];
            var runHi = new float[ncol];
            for (int c = 0; c < ncol; c++) runHi[c] = float.NaN;
            var stamp = new int[ncol];
            var marked = new List<int>();
            var section = new Section(this);
            for (int l = 0; l < nl; l++)
            {
                double z = (kFirst + l + 0.5) * lstep;
                section.Begin(l + 1, stamp, marked);
                for (int q = off[l]; q < off[l + 1]; q++) section.Cut(refv, byLevel[q], z);
                section.Mark();
                float zf = (float)z;
                foreach (int c in marked)
                {
                    if (!float.IsNaN(runHi[c]) && zf - runHi[c] <= lstep * 1.01) { runHi[c] = zf; continue; }
                    if (!float.IsNaN(runHi[c])) pieces.Add(new Piece(c, runLo[c], runHi[c]));
                    runLo[c] = runHi[c] = zf;
                }
            }
            for (int c = 0; c < ncol; c++)
                if (!float.IsNaN(runHi[c])) pieces.Add(new Piece(c, runLo[c], runHi[c]));
        }

        // Sloped and flat polygons at their own height over every column centre they cover.
        for (int tri = 0; tri < nt; tri++)
        {
            double au = refv[9 * tri], av = refv[9 * tri + 1], aw = refv[9 * tri + 2];
            double bu = refv[9 * tri + 3], bv = refv[9 * tri + 4], bw = refv[9 * tri + 5];
            double cu = refv[9 * tri + 6], cv = refv[9 * tri + 7], cw = refv[9 * tri + 8];
            double d = (bv - cv) * (au - cu) + (cu - bu) * (av - cv);
            if (Math.Abs(d) < 1e-9) continue;   // upright: its cross-sections have it
            int i0 = Math.Max(0, (int)Math.Ceiling((Math.Min(au, Math.Min(bu, cu)) - U0) / step - 0.5));
            int i1 = Math.Min(NU - 1, (int)Math.Floor((Math.Max(au, Math.Max(bu, cu)) - U0) / step - 0.5));
            int j0 = Math.Max(0, (int)Math.Ceiling((Math.Min(av, Math.Min(bv, cv)) - V0) / step - 0.5));
            int j1 = Math.Min(NV - 1, (int)Math.Floor((Math.Max(av, Math.Max(bv, cv)) - V0) / step - 0.5));
            for (int j = j0; j <= j1; j++)
            {
                double y = V0 + (j + 0.5) * step;
                for (int i = i0; i <= i1; i++)
                {
                    double x = U0 + (i + 0.5) * step;
                    double l1 = ((bv - cv) * (x - cu) + (cu - bu) * (y - cv)) / d;
                    double l2 = ((cv - av) * (x - cu) + (au - cu) * (y - cv)) / d;
                    double l3 = 1 - l1 - l2;
                    if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
                    float w = (float)(l1 * aw + l2 * bw + l3 * cw);
                    pieces.Add(new Piece(j * NU + i, w, w));
                }
            }
        }

        // Each column's pieces sorted and joined where less than a section apart (a body can't be between).
        pieces.Sort();
        _start = new int[ncol + 1];
        var ranges = new List<float>(pieces.Count);
        double gap = lstep * 1.01 + 1e-4;
        int at = 0;
        for (int c = 0; c < ncol; c++)
        {
            _start[c] = ranges.Count / 2;
            if (at >= pieces.Count || pieces[at].Col != c) continue;
            float lo = pieces[at].Lo, hi = pieces[at].Hi;
            for (at++; at < pieces.Count && pieces[at].Col == c; at++)
            {
                if (pieces[at].Lo <= hi + gap) { if (pieces[at].Hi > hi) hi = pieces[at].Hi; continue; }
                ranges.Add(lo); ranges.Add(hi);
                lo = pieces[at].Lo; hi = pieces[at].Hi;
            }
            ranges.Add(lo); ranges.Add(hi);
        }
        _start[ncol] = ranges.Count / 2;
        _ranges = ranges.ToArray();

        // Walkable faces (steep enough to stand on, either way up) with nothing of the object just over them.
        var walk = new List<int>();
        var walkArea = new List<float>();
        double topArea = 0;
        for (int tri = 0; tri < nt; tri++)
        {
            double au = refv[9 * tri], av = refv[9 * tri + 1], aw = refv[9 * tri + 2];
            double bu = refv[9 * tri + 3] - au, bv = refv[9 * tri + 4] - av, bw = refv[9 * tri + 5] - aw;
            double cu = refv[9 * tri + 6] - au, cv = refv[9 * tri + 7] - av, cw = refv[9 * tri + 8] - aw;
            double nx = bv * cw - bw * cv, ny = bw * cu - bu * cw, nz = bu * cv - bv * cu;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9 || Math.Abs(nz) / len < DungeonGeometry.WalkableNz) continue;
            double mu = au + (bu + cu) / 3, mv = av + (bv + cv) / 3, mw = aw + (bw + cw) / 3;
            if (SolidAt(mu, mv, mw + ExposedFrom, mw + ExposedTo)) continue;
            walk.Add(tri);
            walkArea.Add((float)(Math.Abs(nz) / 2));
            double top = Math.Max(aw, Math.Max(aw + bw, aw + cw));
            if (top >= maxW - 0.1) topArea += Math.Abs(nz) / 2;
        }
        _walkFaces = walk.ToArray();
        _walkArea = walkArea.ToArray();
        WalkableTop = topArea >= 0.05;
    }

    // One cross-section: its segments, which of them close round (filled) and which are open lines.
    private sealed class Section
    {
        private readonly PlacedSolid _m;
        private readonly List<double> _seg = new();          // u0, v0, u1, v1 per segment
        private readonly List<int> _segNode = new();         // the two node ids per segment
        private readonly Dictionary<long, int> _nodeAt = new();
        private readonly List<double> _nodeU = new(), _nodeV = new();
        private readonly List<int> _parent = new(), _degree = new();
        private readonly List<double> _cross = new();
        private int _stampValue;
        private int[] _stamp = null!;
        private List<int> _marked = null!;
        private const double NodeSnap = 1e-3;   // ends this close are one point (a T-joint's rounding)

        public Section(PlacedSolid m) { _m = m; }

        public void Begin(int stampValue, int[] stamp, List<int> marked)
        {
            _stampValue = stampValue; _stamp = stamp; _marked = marked;
            marked.Clear();
            _seg.Clear(); _segNode.Clear(); _nodeAt.Clear(); _nodeU.Clear(); _nodeV.Clear(); _parent.Clear(); _degree.Clear();
        }

        // Where triangle `tri` crosses z (a vertex at z counts as over it). Each edge is interpolated from its lower
        // end, so the two triangles sharing an edge meet at the very same point.
        public void Cut(float[] r, int tri, double z)
        {
            double au = 0, av = 0, bu = 0, bv = 0;
            int n = 0;
            for (int e = 0; e < 3; e++)
            {
                int p = 9 * tri + 3 * e, q = 9 * tri + 3 * ((e + 1) % 3);
                bool pUp = r[p + 2] >= z, qUp = r[q + 2] >= z;
                if (pUp == qUp) continue;
                if (pUp) (p, q) = (q, p);
                double t = (z - r[p + 2]) / (r[q + 2] - r[p + 2]);
                double u = r[p] + (r[q] - r[p]) * t, v = r[p + 1] + (r[q + 1] - r[p + 1]) * t;
                if (n == 0) { au = u; av = v; } else { bu = u; bv = v; }
                n++;
            }
            if (n != 2 || (au - bu) * (au - bu) + (av - bv) * (av - bv) < 1e-10) return;
            int a = Node(au, av), b = Node(bu, bv);
            if (a == b) return;
            _seg.Add(au); _seg.Add(av); _seg.Add(bu); _seg.Add(bv);
            _segNode.Add(a); _segNode.Add(b);
            _degree[a]++; _degree[b]++;
            Union(a, b);
        }

        private int Node(double u, double v)
        {
            long qu = (long)Math.Floor(u / NodeSnap), qv = (long)Math.Floor(v / NodeSnap);
            for (long du = -1; du <= 1; du++)
                for (long dv = -1; dv <= 1; dv++)
                    if (_nodeAt.TryGetValue(Key(qu + du, qv + dv), out int id)
                        && Math.Abs(_nodeU[id] - u) <= NodeSnap && Math.Abs(_nodeV[id] - v) <= NodeSnap) return id;
            int nid = _nodeU.Count;
            _nodeU.Add(u); _nodeV.Add(v); _parent.Add(nid); _degree.Add(0);
            _nodeAt[Key(qu, qv)] = nid;
            return nid;
        }

        private static long Key(long a, long b) => (a << 32) ^ (b & 0xFFFFFFFFL);

        private int Find(int a)
        {
            while (_parent[a] != a) { _parent[a] = _parent[_parent[a]]; a = _parent[a]; }
            return a;
        }

        private void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) _parent[ra] = rb; }

        // Closed pieces filled (each on its own, so two overlapping parts don't cancel out), open ones drawn as lines.
        public void Mark()
        {
            int ns = _segNode.Count / 2;
            if (ns == 0) return;
            var open = new HashSet<int>();
            for (int i = 0; i < _degree.Count; i++) if ((_degree[i] & 1) != 0) open.Add(Find(i));
            var byComp = new Dictionary<int, List<int>>();
            for (int s = 0; s < ns; s++)
            {
                int root = Find(_segNode[2 * s]);
                if (!byComp.TryGetValue(root, out var l)) byComp[root] = l = new List<int>();
                l.Add(s);
            }
            foreach (var (root, segs) in byComp)
            {
                if (open.Contains(root)) { foreach (int s in segs) Line(s); continue; }
                double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
                foreach (int s in segs)
                {
                    u0 = Math.Min(u0, Math.Min(_seg[4 * s], _seg[4 * s + 2])); u1 = Math.Max(u1, Math.Max(_seg[4 * s], _seg[4 * s + 2]));
                    v0 = Math.Min(v0, Math.Min(_seg[4 * s + 1], _seg[4 * s + 3])); v1 = Math.Max(v1, Math.Max(_seg[4 * s + 1], _seg[4 * s + 3]));
                }
                int filled = Fill(segs, v0, v1);
                // A closed piece thinner than two columns (a rail, a thin slab's edge) may cover no column centre: its line too.
                if (filled == 0 || Math.Min(u1 - u0, v1 - v0) < 2 * _m.Step) foreach (int s in segs) Line(s);
            }
        }

        // Even-odd over the piece's own segments, by column centre; how many centres are inside.
        private int Fill(List<int> segs, double vMin, double vMax)
        {
            var m = _m;
            int count = 0;
            int j0 = Math.Max(0, (int)Math.Ceiling((vMin - m.V0) / m.Step - 0.5)), j1 = Math.Min(m.NV - 1, (int)Math.Floor((vMax - m.V0) / m.Step - 0.5));
            for (int j = j0; j <= j1; j++)
            {
                double vc = m.V0 + (j + 0.5) * m.Step;
                _cross.Clear();
                foreach (int s in segs)
                {
                    double sv0 = _seg[4 * s + 1], sv1 = _seg[4 * s + 3];
                    if ((sv0 > vc) == (sv1 > vc)) continue;
                    double su0 = _seg[4 * s], su1 = _seg[4 * s + 2];
                    _cross.Add(su0 + (vc - sv0) * (su1 - su0) / (sv1 - sv0));
                }
                if (_cross.Count < 2) continue;
                _cross.Sort();
                for (int c = 0; c + 1 < _cross.Count; c += 2)
                {
                    int i0 = Math.Max(0, (int)Math.Ceiling((_cross[c] - m.U0) / m.Step - 0.5));
                    int i1 = Math.Min(m.NU - 1, (int)Math.Floor((_cross[c + 1] - m.U0) / m.Step - 0.5));
                    for (int i = i0; i <= i1; i++) { Mark(j * m.NU + i); count++; }
                }
            }
            return count;
        }

        // Every column the segment passes through (sampled at a quarter column).
        private void Line(int s)
        {
            var m = _m;
            double u0 = _seg[4 * s], v0 = _seg[4 * s + 1], u1 = _seg[4 * s + 2], v1 = _seg[4 * s + 3];
            double len = Math.Sqrt((u1 - u0) * (u1 - u0) + (v1 - v0) * (v1 - v0));
            int n = Math.Max(1, (int)Math.Ceiling(len / (m.Step * 0.25)));
            for (int k = 0; k <= n; k++)
            {
                double u = u0 + (u1 - u0) * k / n, v = v0 + (v1 - v0) * k / n;
                int i = Math.Clamp((int)Math.Floor((u - m.U0) / m.Step), 0, m.NU - 1), j = Math.Clamp((int)Math.Floor((v - m.V0) / m.Step), 0, m.NV - 1);
                Mark(j * m.NU + i);
            }
        }

        private void Mark(int c)
        {
            if (_stamp[c] == _stampValue) return;
            _stamp[c] = _stampValue;
            _marked.Add(c);
        }
    }

    // ── Queries (model frame) ───────────────────────────────────────────────

    /// <summary>The column holding (u, v), or -1 off the raster.</summary>
    public int ColumnAt(double u, double v)
    {
        int i = (int)Math.Floor((u - U0) / Step), j = (int)Math.Floor((v - V0) / Step);
        return i < 0 || j < 0 || i >= NU || j >= NV ? -1 : j * NU + i;
    }

    /// <summary>Something solid in the column at (u, v) between heights lo and hi (model frame).</summary>
    public bool SolidAt(double u, double v, double lo, double hi)
    {
        int c = ColumnAt(u, v);
        return c >= 0 && SolidIn(c, lo, hi);
    }

    private bool SolidIn(int c, double lo, double hi)
    {
        for (int r = _start[c]; r < _start[c + 1]; r++)
            if (_ranges[2 * r] <= hi && _ranges[2 * r + 1] >= lo) return true;
        return false;
    }

    /// <summary>The highest top of something solid in the column at (u, v) that is no higher than wMax (model
    /// frame), or -infinity: what a body could stand on there.</summary>
    public double TopAtMost(double u, double v, double wMax)
    {
        int c = ColumnAt(u, v);
        double best = double.NegativeInfinity;
        if (c < 0) return best;
        for (int r = _start[c]; r < _start[c + 1]; r++)
            if (_ranges[2 * r + 1] <= wMax && _ranges[2 * r + 1] > best) best = _ranges[2 * r + 1];
        return best;
    }

    /// <summary>The centre of column c (model frame).</summary>
    public void ColumnCentre(int c, out double u, out double v)
    {
        u = U0 + (c % NU + 0.5) * Step;
        v = V0 + (c / NU + 0.5) * Step;
    }

    /// <summary>Walkable face k's three corners (model frame).</summary>
    public void WalkFace(int k, out double au, out double av, out double aw, out double bu, out double bv, out double bw, out double cu, out double cv, out double cw)
    {
        int t = 9 * _walkFaces[k];
        au = _ref[t]; av = _ref[t + 1]; aw = _ref[t + 2];
        bu = _ref[t + 3]; bv = _ref[t + 4]; bw = _ref[t + 5];
        cu = _ref[t + 6]; cv = _ref[t + 7]; cw = _ref[t + 8];
    }

    /// <summary>
    /// What is in a body's way between heights lo and hi (model frame): the columns, and their outline as
    /// segments (model frame) round every filled patch. Remembered per band (copies on like floors share it).
    /// </summary>
    public PlacedBand Band(double lo, double hi)
    {
        long key = ((long)Math.Round(lo * 100) << 32) ^ (uint)(int)Math.Round(hi * 100);
        lock (_bands) if (_bands.TryGetValue(key, out var have)) return have;
        int ncol = NU * NV;
        var mask = new bool[ncol];
        var cells = new List<int>();
        for (int c = 0; c < ncol; c++)
            if (SolidIn(c, lo, hi)) { mask[c] = true; cells.Add(c); }
        var band = new PlacedBand(cells.ToArray(), cells.Count == 0 ? Array.Empty<double>() : Outline(mask), cells.Count * Step * Step);
        lock (_bands) if (_bands.Count < 32) _bands[key] = band;
        return band;
    }

    // Marching squares over the column centres (each filled patch's edge runs half way between a filled centre and
    // an empty one: the patch's own side), chained into loops and thinned to within 0.4 of a column.
    private double[] Outline(bool[] mask)
    {
        int nu = NU;
        bool M(int i, int j) => mask[j * nu + i];
        int H(int i, int j) => 2 * (j * nu + i);       // the edge between centres (i, j) and (i + 1, j)
        int Vt(int i, int j) => 2 * (j * nu + i) + 1;  // between (i, j) and (i, j + 1)
        var link = new Dictionary<int, (int A, int B)>();
        void Link(int a, int b)
        {
            link[a] = link.TryGetValue(a, out var la) ? (la.A, b) : (b, -1);
            link[b] = link.TryGetValue(b, out var lb) ? (lb.A, a) : (a, -1);
        }
        for (int j = 0; j + 1 < NV; j++)
            for (int i = 0; i + 1 < NU; i++)
            {
                int code = (M(i, j) ? 1 : 0) | (M(i + 1, j) ? 2 : 0) | (M(i + 1, j + 1) ? 4 : 0) | (M(i, j + 1) ? 8 : 0);
                if (code == 0 || code == 15) continue;
                int b = H(i, j), r = Vt(i + 1, j), t = H(i, j + 1), l = Vt(i, j);
                switch (code)
                {
                    case 1: case 14: Link(l, b); break;
                    case 2: case 13: Link(b, r); break;
                    case 3: case 12: Link(l, r); break;
                    case 4: case 11: Link(r, t); break;
                    case 6: case 9: Link(b, t); break;
                    case 7: case 8: Link(t, l); break;
                    case 5: Link(l, b); Link(r, t); break;    // two corners apart: each its own loop
                    case 10: Link(b, r); Link(t, l); break;
                }
            }
        void At(int id, out double u, out double v)
        {
            int idx = id >> 1, i = idx % nu, j = idx / nu;
            if ((id & 1) == 0) { u = U0 + (i + 1) * Step; v = V0 + (j + 0.5) * Step; }
            else { u = U0 + (i + 0.5) * Step; v = V0 + (j + 1) * Step; }
        }
        var outSegs = new List<double>();
        var done = new HashSet<int>();
        var loopU = new List<double>(); var loopV = new List<double>();
        foreach (int start in link.Keys)
        {
            if (done.Contains(start)) continue;
            loopU.Clear(); loopV.Clear();
            int prev = -1, cur = start;
            while (true)
            {
                done.Add(cur);
                At(cur, out double u, out double v);
                loopU.Add(u); loopV.Add(v);
                var (a, b) = link[cur];
                int next = a != prev ? a : b;
                if (next < 0 || next == start || done.Contains(next)) break;
                prev = cur; cur = next;
            }
            Thin(loopU, loopV, 0.4 * Step, outSegs);
        }
        return outSegs.ToArray();
    }

    // A closed loop thinned (Douglas-Peucker on its two halves) and added as segments.
    private static void Thin(List<double> u, List<double> v, double tol, List<double> outSegs)
    {
        int n = u.Count;
        if (n < 2) return;
        if (n <= 3)
        {
            for (int i = 0; i < n; i++) { int k = (i + 1) % n; outSegs.Add(u[i]); outSegs.Add(v[i]); outSegs.Add(u[k]); outSegs.Add(v[k]); }
            return;
        }
        int far = 0; double best = -1;
        for (int i = 1; i < n; i++) { double d = (u[i] - u[0]) * (u[i] - u[0]) + (v[i] - v[0]) * (v[i] - v[0]); if (d > best) { best = d; far = i; } }
        var keep = new bool[n];
        keep[0] = keep[far] = true;
        void Dp(int a, int b)   // indices along the loop, b > a (b may be n: the start again)
        {
            if (b - a < 2) return;
            double ax = u[a % n], ay = v[a % n], bx = u[b % n], by = v[b % n];
            double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
            int at = -1; double worst = tol;
            for (int i = a + 1; i < b; i++)
            {
                double px = u[i % n] - ax, py = v[i % n] - ay;
                double d = len < 1e-12 ? Math.Sqrt(px * px + py * py) : Math.Abs(px * dy - py * dx) / len;
                if (d > worst) { worst = d; at = i; }
            }
            if (at < 0) return;
            keep[at % n] = true;
            Dp(a, at); Dp(at, b);
        }
        Dp(0, far); Dp(far, n);
        int first = -1, last = -1;
        for (int i = 0; i < n; i++)
        {
            if (!keep[i]) continue;
            if (first < 0) first = i;
            if (last >= 0) { outSegs.Add(u[last]); outSegs.Add(v[last]); outSegs.Add(u[i]); outSegs.Add(v[i]); }
            last = i;
        }
        if (first >= 0 && last != first) { outSegs.Add(u[last]); outSegs.Add(v[last]); outSegs.Add(u[first]); outSegs.Add(v[first]); }
    }
}

/// <summary>What of an object is in a body's way at one band: its columns, outline (u0, v0, u1, v1 per
/// segment, model frame) and area.</summary>
internal sealed class PlacedBand
{
    public readonly int[] Cells;
    public readonly double[] Outline;
    public readonly double Area;
    public PlacedBand(int[] cells, double[] outline, double area) { Cells = cells; Outline = outline; Area = area; }
    public bool Empty => Cells.Length == 0;
}

/// <summary>One placed copy of a <see cref="PlacedSolid"/>: the model turned by its yaw and moved to its first
/// vertex, with the world box of its triangles.</summary>
internal sealed class PlacedShape
{
    public readonly PlacedSolid Model;
    public readonly double Ox, Oy, Oz, Yaw, Cos, Sin;
    public readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

    public PlacedShape(PlacedSolid model, double ox, double oy, double oz, double yaw,
                       double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        Model = model; Ox = ox; Oy = oy; Oz = oz; Yaw = yaw; Cos = Math.Cos(yaw); Sin = Math.Sin(yaw);
        MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
    }

    public void ToModel(double x, double y, out double u, out double v)
    {
        double dx = x - Ox, dy = y - Oy;
        u = dx * Cos + dy * Sin;
        v = -dx * Sin + dy * Cos;
    }

    public void ToWorld(double u, double v, out double x, out double y)
    {
        x = Ox + u * Cos - v * Sin;
        y = Oy + u * Sin + v * Cos;
    }

    /// <summary>Something of it at (x, y) between world heights zLo and zHi.</summary>
    public bool SolidIn(double x, double y, double zLo, double zHi)
    {
        if (x < MinX - 0.1 || x > MaxX + 0.1 || y < MinY - 0.1 || y > MaxY + 0.1 || zHi < MinZ || zLo > MaxZ) return false;
        ToModel(x, y, out double u, out double v);
        return Model.SolidAt(u, v, zLo - Oz, zHi - Oz);
    }

    /// <summary>The highest top of it at (x, y) no higher than world height zMax, or -infinity.</summary>
    public double TopAtMost(double x, double y, double zMax)
    {
        if (x < MinX - 0.1 || x > MaxX + 0.1 || y < MinY - 0.1 || y > MaxY + 0.1 || zMax < MinZ) return double.NegativeInfinity;
        ToModel(x, y, out double u, out double v);
        return Model.TopAtMost(u, v, zMax - Oz) + Oz;
    }

    /// <summary>What of it is in the way between world heights zLo and zHi.</summary>
    public PlacedBand Band(double zLo, double zHi) => Model.Band(zLo - Oz, zHi - Oz);

    public void CellCentre(int c, out double x, out double y)
    {
        Model.ColumnCentre(c, out double u, out double v);
        ToWorld(u, v, out x, out y);
    }

    /// <summary>Walkable face k: its world box and height range.</summary>
    public void WalkFace(int k, out double minX, out double minY, out double maxX, out double maxY, out double z0, out double z1)
    {
        Model.WalkFace(k, out double au, out double av, out double aw, out double bu, out double bv, out double bw, out double cu, out double cv, out double cw);
        ToWorld(au, av, out double ax, out double ay);
        ToWorld(bu, bv, out double bx, out double by);
        ToWorld(cu, cv, out double cx, out double cy);
        minX = Math.Min(ax, Math.Min(bx, cx)); maxX = Math.Max(ax, Math.Max(bx, cx));
        minY = Math.Min(ay, Math.Min(by, cy)); maxY = Math.Max(ay, Math.Max(by, cy));
        z0 = Oz + Math.Min(aw, Math.Min(bw, cw)); z1 = Oz + Math.Max(aw, Math.Max(bw, cw));
    }

    /// <summary>Walkable face k's centre (world).</summary>
    public void WalkFaceCentre(int k, out double x, out double y, out double z)
    {
        Model.WalkFace(k, out double au, out double av, out double aw, out double bu, out double bv, out double bw, out double cu, out double cv, out double cw);
        ToWorld((au + bu + cu) / 3, (av + bv + cv) / 3, out x, out y);
        z = Oz + (aw + bw + cw) / 3;
    }
}
