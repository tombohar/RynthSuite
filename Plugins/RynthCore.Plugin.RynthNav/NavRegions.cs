using DotRecast.Detour;

namespace RynthNav.Routing;

/// <summary>
/// The connected walkable regions of one navmesh tile: polygons joined by the tile's own
/// (internal) polygon links. Two islands in the same landblock (a roof, a ledge, the far
/// bank of a river) are different regions.
///
/// The numbering is deterministic (a region's id is the order of its lowest-numbered
/// polygon), so the offline graph tool (RynthNav.Baker --graph) and the plugin, which
/// computes it again for the tiles it loads, always agree.
/// </summary>
internal static class NavRegions
{
    /// <summary>Region id per polygon (0..count-1), from the tile's internal links only.</summary>
    public static int[] Build(DtMeshData md, out int count)
    {
        int n = md.header.polyCount;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }

        for (int i = 0; i < n; i++)
        {
            DtPoly p = md.polys[i];
            if (p.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND) continue;
            for (int j = 0; j < p.vertCount; j++)
            {
                int nei = p.neis[j];
                if (nei == 0 || (nei & DtDetour.DT_EXT_LINK) != 0) continue;   // a wall, or another tile
                int k = nei - 1;
                if (k < 0 || k >= n) continue;
                int a = Find(i), b = Find(k);
                if (a != b) { if (a < b) parent[b] = a; else parent[a] = b; }
            }
        }

        // Number the roots in polygon order.
        var region = new int[n];
        var id = new int[n];
        for (int i = 0; i < n; i++) id[i] = -1;
        count = 0;
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (id[r] < 0) id[r] = count++;
            region[i] = id[r];
        }
        return region;
    }

    /// <summary>What the graph file records to recognise a tile: its polygon, vertex and detail-triangle counts.</summary>
    public static (int Polys, int Verts, int DetailTris) Fingerprint(DtMeshData md)
        => (md.header.polyCount, md.header.vertCount, md.header.detailTriCount);
}
