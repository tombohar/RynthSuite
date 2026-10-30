using System.Numerics;

namespace RynthNav.TownNet;

/// <summary>
/// A walk grid over the indoor floors: one node per (cell, 0.25 m square) whose centre is on that
/// cell's floor. Neighbouring nodes in the same cell connect; nodes in two cells connect only where
/// the step between them passes through a doorway (cell portal) of the two. Every node knows its
/// clearance: the distance to the nearest wall, solid object or floor edge.
/// </summary>
internal sealed class NavGrid
{
    public const float Cell = 0.25f;
    private const float ClearMax = 3.0f;

    public sealed class Node
    {
        public int Index;
        public uint CellId;
        public int Ix, Iy;
        public float X, Y, Z;
        public float Clear = ClearMax;   // min(wall / object / floor edge)
        public float PortalClear = float.MaxValue;
        public string? NearPortal;
        public int[] Nbr = Array.Empty<int>();
    }

    public readonly List<Node> Nodes = new();
    private readonly Dictionary<long, List<int>> _at = new();
    private readonly Indoor _w;

    private static long Key(int ix, int iy) => ((long)ix << 32) | (uint)iy;
    public static int Idx(float v) => (int)MathF.Floor(v / Cell);

    public NavGrid(Indoor world, IEnumerable<uint> cells)
    {
        _w = world;
        foreach (uint id in cells) AddCell(world.Cells[id]);
        Link();
        Clearance();
    }

    private void AddCell(EnvCell c)
    {
        var mine = new Dictionary<long, Node>();
        foreach (var f in c.Floors)
        {
            int x0 = Idx(f.Min(v => v.X)), x1 = Idx(f.Max(v => v.X)), y0 = Idx(f.Min(v => v.Y)), y1 = Idx(f.Max(v => v.Y));
            for (int ix = x0; ix <= x1; ix++)
                for (int iy = y0; iy <= y1; iy++)
                {
                    float x = (ix + 0.5f) * Cell, y = (iy + 0.5f) * Cell;
                    if (!Geo.InsideXY(f, x, y, 0)) continue;
                    long k = Key(ix, iy);
                    if (mine.ContainsKey(k)) continue;
                    var n = new Node { Index = Nodes.Count, CellId = c.Id, Ix = ix, Iy = iy, X = x, Y = y, Z = Geo.PlaneZ(f, x, y) };
                    mine[k] = n;
                    Nodes.Add(n);
                    if (!_at.TryGetValue(k, out var list)) _at[k] = list = new List<int>();
                    list.Add(n.Index);
                }
        }
    }

    private static readonly (int dx, int dy)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    private void Link()
    {
        var edgeNodes = new List<int>();
        foreach (var n in Nodes)
        {
            var nbr = new List<int>(8);
            int dirsCovered = 0;
            var cell = _w.Cells[n.CellId];
            foreach (var (dx, dy) in Dirs)
            {
                if (!_at.TryGetValue(Key(n.Ix + dx, n.Iy + dy), out var list)) continue;
                bool any = false;
                foreach (int mi in list)
                {
                    var m = Nodes[mi];
                    if (MathF.Abs(m.Z - n.Z) > Indoor.StepZ) continue;
                    if (m.CellId != n.CellId)
                    {
                        bool through = false;
                        foreach (var door in _w.Doorways(cell, m.CellId))
                            if (Geo.SegmentCrosses(door, new Vector3(n.X, n.Y, n.Z + 0.6f), new Vector3(m.X, m.Y, m.Z + 0.6f))) { through = true; break; }
                        if (!through) continue;
                    }
                    nbr.Add(mi);
                    any = true;
                }
                if (any) dirsCovered++;
            }
            n.Nbr = nbr.ToArray();
            if (dirsCovered < 8) n.Clear = Cell * 0.5f;     // a floor edge (wall, drop or pit) next to it
        }
    }

    private void Clearance()
    {
        // Solid things: exact distance for the nodes near each one.
        foreach (var f in _w.Solids)
        {
            int x0 = Idx(f.MinX - ClearMax), x1 = Idx(f.MaxX + ClearMax), y0 = Idx(f.MinY - ClearMax), y1 = Idx(f.MaxY + ClearMax);
            for (int ix = x0; ix <= x1; ix++)
                for (int iy = y0; iy <= y1; iy++)
                {
                    if (!_at.TryGetValue(Key(ix, iy), out var list)) continue;
                    foreach (int i in list)
                    {
                        var n = Nodes[i];
                        if (!f.Blocks(n.Z)) continue;
                        float d = f.Dist(n.X, n.Y);
                        if (d < n.Clear) n.Clear = d;
                    }
                }
        }
        // Floor edges: spread the distance over the grid (Dijkstra from every node's own value).
        var pq = new PriorityQueue<int, float>();
        var dist = Nodes.Select(n => n.Clear).ToArray();
        for (int i = 0; i < Nodes.Count; i++) if (dist[i] < ClearMax) pq.Enqueue(i, dist[i]);
        while (pq.TryDequeue(out int i, out float d))
        {
            if (d > dist[i]) continue;
            var n = Nodes[i];
            foreach (int j in n.Nbr)
            {
                var m = Nodes[j];
                float nd = d + Vector2.Distance(new Vector2(n.X, n.Y), new Vector2(m.X, m.Y));
                if (nd < dist[j]) { dist[j] = nd; pq.Enqueue(j, nd); }
            }
        }
        for (int i = 0; i < Nodes.Count; i++) Nodes[i].Clear = MathF.Min(Nodes[i].Clear, dist[i]);
        // Portal objects (kept apart: a walk ignores its own exit portal).
        foreach (var n in Nodes)
        {
            n.PortalClear = _w.PortalDist(n.X, n.Y, n.Z, null, out var p);
            n.NearPortal = p?.Guid;
        }
    }

    /// <summary>The node of cell <paramref name="cell"/> nearest to (x, y) that passes <paramref name="ok"/>.</summary>
    public Node? Nearest(uint cell, float x, float y, Func<Node, bool> ok, float within = 3f)
    {
        Node? best = null; float bd = within;
        int r = (int)MathF.Ceiling(within / Cell);
        int cx = Idx(x), cy = Idx(y);
        for (int ix = cx - r; ix <= cx + r; ix++)
            for (int iy = cy - r; iy <= cy + r; iy++)
            {
                if (!_at.TryGetValue(Key(ix, iy), out var list)) continue;
                foreach (int i in list)
                {
                    var n = Nodes[i];
                    if (n.CellId != cell && cell != 0) continue;
                    float d = Vector2.Distance(new Vector2(n.X, n.Y), new Vector2(x, y));
                    if (d < bd && ok(n)) { bd = d; best = n; }
                }
            }
        return best;
    }

    public IEnumerable<Node> Around(float x, float y, float within)
    {
        int r = (int)MathF.Ceiling(within / Cell);
        int cx = Idx(x), cy = Idx(y);
        for (int ix = cx - r; ix <= cx + r; ix++)
            for (int iy = cy - r; iy <= cy + r; iy++)
                if (_at.TryGetValue(Key(ix, iy), out var list))
                    foreach (int i in list) yield return Nodes[i];
    }

    /// <summary>Shortest walking distances from one node over the nodes that pass <paramref name="ok"/>,
    /// with a mild extra cost near walls (so walks keep to the middle of corridors).</summary>
    public (float[] cost, int[] prev) Dijkstra(Node start, Func<Node, bool> ok)
    {
        var cost = new float[Nodes.Count];
        var prev = new int[Nodes.Count];
        Array.Fill(cost, float.MaxValue);
        Array.Fill(prev, -1);
        var pq = new PriorityQueue<int, float>();
        cost[start.Index] = 0;
        pq.Enqueue(start.Index, 0);
        while (pq.TryDequeue(out int i, out float c))
        {
            if (c > cost[i]) continue;
            var n = Nodes[i];
            foreach (int j in n.Nbr)
            {
                var m = Nodes[j];
                if (!ok(m)) continue;
                float step = Vector3.Distance(new Vector3(n.X, n.Y, n.Z), new Vector3(m.X, m.Y, m.Z));
                float near = MathF.Max(0, 1.5f - MathF.Min(m.Clear, m.PortalClear));
                float nc = c + step * (1 + near);
                if (nc < cost[j]) { cost[j] = nc; prev[j] = i; pq.Enqueue(j, nc); }
            }
        }
        return (cost, prev);
    }

    public List<Node> PathTo(int goal, int[] prev)
    {
        var path = new List<Node>();
        for (int i = goal; i >= 0; i = prev[i]) path.Add(Nodes[i]);
        path.Reverse();
        return path;
    }

    /// <summary>The clearance of the grid node under p (at about its height), 0 where there is none.</summary>
    public float ClearAt(Vector3 p)
    {
        Node? m = null;
        if (_at.TryGetValue(Key(Idx(p.X), Idx(p.Y)), out var list))
            foreach (int k in list) if (m == null || MathF.Abs(Nodes[k].Z - p.Z) < MathF.Abs(m.Z - p.Z)) m = Nodes[k];
        return m == null || MathF.Abs(m.Z - p.Z) > 1.0f ? 0 : m.Clear;
    }
}
