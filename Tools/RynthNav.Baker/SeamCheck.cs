using System.Text;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace RynthNav.Baker;

// --seams <dir>: do neighbouring tiles link at their shared edge, the way the plugin
// loads them? For every pair of east/north neighbours that both have real tiles, the two
// are added to a fresh navmesh and Detour's own cross-tile links are counted:
//  - an edge is a polygon edge on the shared side that Detour marks as a tile border;
//  - a seam "can link" when both tiles have such edges facing each other;
//  - a seam is "linked" when at least one link crosses it.
// Also reports how many tiles sit exactly on the landblock grid (header bmin = lb*192).
internal static class SeamCheck
{
    private const int VertsPerPoly = NavBake.VertsPerPoly;

    private static DtMeshData Read(string dir, uint lb)
    {
        using var fr = File.OpenRead(Path.Combine(dir, $"nav_{lb:X4}.tile"));
        using var br = new BinaryReader(fr);
        return new DtMeshDataReader().Read(br, VertsPerPoly);
    }

    public static string Run(string dir, string? area)
    {
        int ax0 = 0, ax1 = 255, ay0 = 0, ay1 = 255;
        if (area != null)
        {
            var p = area.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            ax0 = Convert.ToInt32(p[0], 16); ax1 = Convert.ToInt32(p[1], 16); ay0 = Convert.ToInt32(p[2], 16); ay1 = Convert.ToInt32(p[3], 16);
        }
        // Index: polygon count and grid offset of every tile.
        var polys = new Dictionary<uint, int>();
        int onGrid = 0, offGrid = 0, single = 0, unreadable = 0;
        foreach (string f in Directory.GetFiles(dir, "nav_*.tile"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.Length != 8) continue;
            uint lb;
            try { lb = Convert.ToUInt32(name[4..], 16); } catch { continue; }
            int x = (int)(lb >> 8), y = (int)(lb & 0xFF);
            if (x < ax0 || x > ax1 || y < ay0 || y > ay1) continue;
            try
            {
                var md = Read(dir, lb);
                polys[lb] = md.header.polyCount;
                if (md.header.polyCount <= 1) single++;
                bool g = Math.Abs(md.header.bmin.X - x * 192f) < 0.01f && Math.Abs(md.header.bmin.Z - y * 192f) < 0.01f
                         && md.header.x == x && md.header.y == y;
                if (g) onGrid++; else offGrid++;
            }
            catch { unreadable++; }
        }

        int pairs = 0, canLink = 0, linked = 0;
        long edgesA = 0, edgesLinkedA = 0;
        var unlinked = new List<string>();
        var param = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 4, maxPolys = 1 << 16 };
        foreach (var (lb, pc) in polys)
        {
            if (pc <= 1) continue;
            int x = (int)(lb >> 8), y = (int)(lb & 0xFF);
            foreach (var (nx, ny, side) in new[] { (x + 1, y, 0), (x, y + 1, 2) })   // Detour sides: 0 = +x (east), 2 = +z (north)
            {
                if (nx > 255 || ny > 255) continue;
                uint nb = (uint)((nx << 8) | ny);
                if (!polys.TryGetValue(nb, out int npc) || npc <= 1) continue;
                pairs++;
                var nav = new DtNavMesh();
                nav.Init(ref param, VertsPerPoly);
                nav.AddTile(Read(dir, lb), 0, 0, out long ra);
                nav.AddTile(Read(dir, nb), 0, 0, out long rb);
                DtMeshTile ta = nav.GetTileByRef(ra), tb = nav.GetTileByRef(rb);
                int aEdges = BorderEdges(ta, side, out int aLinked, tb, nav);
                int bEdges = BorderEdges(tb, (side + 4) & 7, out _, ta, nav);
                if (aEdges > 0 && bEdges > 0)
                {
                    canLink++;
                    edgesA += aEdges; edgesLinkedA += aLinked;
                    if (aLinked > 0) linked++;
                    else if (unlinked.Count < 40) unlinked.Add($"0x{lb:X4}|0x{nb:X4}");
                }
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine($"tiles: {polys.Count} ({single} single-polygon, {unreadable} unreadable); on the landblock grid: {onGrid}, off it: {offGrid}");
        sb.AppendLine($"neighbour pairs of real tiles: {pairs}; with walkable edges facing each other: {canLink}; linked: {linked} ({(canLink == 0 ? 0 : 100.0 * linked / canLink):F1}%)");
        sb.AppendLine($"border edges linked: {edgesLinkedA}/{edgesA} ({(edgesA == 0 ? 0 : 100.0 * edgesLinkedA / edgesA):F1}%)");
        if (unlinked.Count > 0) sb.AppendLine("unlinked (first 40): " + string.Join(" ", unlinked));
        return sb.ToString().TrimEnd();
    }

    // Polygon edges of tile t on Detour side `side` (a tile-border edge), and how many of
    // them have a link into tile `other`.
    private static int BorderEdges(DtMeshTile t, int side, out int linkedToOther, DtMeshTile other, DtNavMesh nav)
    {
        linkedToOther = 0;
        int edges = 0;
        DtMeshData d = t.data;
        for (int i = 0; i < d.header.polyCount; i++)
        {
            DtPoly p = d.polys[i];
            for (int j = 0; j < p.vertCount; j++)
            {
                int nei = p.neis[j];
                if ((nei & DtDetour.DT_EXT_LINK) == 0 || (nei & 0xff) != side) continue;
                edges++;
                bool hit = false;
                for (int k = p.firstLink; k != DtDetour.DT_NULL_LINK; k = t.links[k].next)
                {
                    DtLink link = t.links[k];
                    if (link.edge != j) continue;
                    nav.GetTileAndPolyByRefUnsafe(link.refs, out DtMeshTile lt, out _);
                    if (lt == other) { hit = true; break; }
                }
                if (hit) linkedToOther++;
            }
        }
        return edges;
    }
}
