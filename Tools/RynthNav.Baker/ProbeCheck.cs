using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using RynthCore.TerrainData;

namespace RynthNav.Baker;

// --probe wx,wy[,radius[,step]] --tiles <dir>
// A top-down look at one spot (world metres: east, north): for each sample, is the terrain's slope one a
// character can walk (TerrainSampler.FloorZ) and is there navmesh under it?
//   .  walkable slope, mesh          x  walkable slope, NO mesh   <- the mesh lost ground it should have
//   m  steep slope, mesh             #  steep slope, no mesh      (the mesh is right)
//   @  the centre
internal static class ProbeCheck
{
    /// <summary>
    /// --probe-dungeon LLLL:x,y[,z[,radius]] [x,y ...] --tiles dir : in a baked dungeon (dun_LLLL.tile), the walkable
    /// surface heights found at each landblock-local point (within 0.4 m sideways, 3 m up and down).
    /// </summary>
    public static int Dungeon(string spec, string[] points, string tilesDir, Action<string> log)
    {
        uint lb = Convert.ToUInt32(spec, 16);
        var f = RynthNav.Routing.DungeonTileFile.Read(Path.Combine(tilesDir, RynthNav.Routing.DungeonTileFile.FileName(lb)));
        if (f == null) { log($"no readable dungeon tile for 0x{lb:X4} in {tilesDir}"); return 1; }
        var nav = f.BuildNavMesh(); var q = new DtNavMeshQuery(nav); var filter = new DtQueryDefaultFilter();
        foreach (string pt in points)
        {
            string[] a = pt.Split(',');
            float x = float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture), y = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
            float z = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
            // Heights of every polygon under the point: sample the detail surface by querying at several heights.
            var found = new SortedSet<float>();
            for (float zz = z - 3f; zz <= z + 3f; zz += 0.25f)
            {
                q.FindNearestPoly(new RcVec3f(x + f.ShiftX, zz, y + f.ShiftY), new RcVec3f(0.4f, 0.12f, 0.4f), filter, out long rf, out RcVec3f near, out bool over);
                if (rf != 0 && over) found.Add((float)Math.Round(near.Y, 1));
            }
            log($"  ({x:F1}, {y:F1}, around z {z:F1}): " + (found.Count == 0 ? "NO walkable mesh here (an obstacle, or off the floor)" : "walkable surface at z " + string.Join(", ", found)));
        }
        return 0;
    }

    public static int Run(TerrainSampler sampler, string spec, string tilesDir, Action<string> log)
    {
        string[] a = spec.Split(',', StringSplitOptions.TrimEntries);
        double cx = double.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture), cy = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
        double radius = a.Length > 2 ? double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 30;
        double step = a.Length > 3 ? double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        int lbx = (int)Math.Floor(cx / 192), lby = (int)Math.Floor(cy / 192);

        var nav = new DtNavMesh();
        var p = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 64, maxPolys = 1 << 16 };
        nav.Init(ref p, 6);
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                uint t = (uint)(((lbx + dx) << 8) | (lby + dy));
                string path = Path.Combine(tilesDir, $"nav_{t:X4}.tile");
                if (!File.Exists(path)) continue;
                using var fr = File.OpenRead(path); using var br = new BinaryReader(fr);
                nav.AddTile(new DtMeshDataReader().Read(br, 6), 0, 0, out _);
            }
        var q = new DtNavMeshQuery(nav); var filter = new DtQueryDefaultFilter();

        // Connected pieces of the loaded mesh (polygon links, across tiles): size and an id per polygon.
        var piece = new Dictionary<long, int>(); var pieceSize = new List<int>();
        for (int ti = 0; ti < nav.GetMaxTiles(); ti++)
        {
            DtMeshTile? tile = nav.GetTile(ti);
            if (tile?.data?.header == null) continue;
            long baseRef = nav.GetPolyRefBase(tile);
            for (int pi = 0; pi < tile.data.header.polyCount; pi++)
            {
                long start = baseRef | (long)pi;
                if (piece.ContainsKey(start)) continue;
                int id = pieceSize.Count, cnt = 0; pieceSize.Add(0);
                var stack = new Stack<long>(); stack.Push(start); piece[start] = id;
                while (stack.Count > 0)
                {
                    long rf0 = stack.Pop(); cnt++;
                    if (nav.GetTileAndPolyByRef(rf0, out DtMeshTile t2, out DtPoly pl).Failed()) continue;
                    for (int k = pl.firstLink; k != DtDetour.DT_NULL_LINK; k = t2.links[k].next)
                    { long nb = t2.links[k].refs; if (nb != 0 && !piece.ContainsKey(nb)) { piece[nb] = id; stack.Push(nb); } }
                }
                pieceSize[id] = cnt;
            }
        }
        int centrePiece = -1;
        {
            double cz = 0; { int bx = (int)Math.Floor(cx / 192), by = (int)Math.Floor(cy / 192); }
        }

        var lands = new Dictionary<uint, RynthCore.Plugin.RynthAi.Raycasting.LandblockData?>();
        float Z(double wx, double wy)
        {
            int bx = (int)Math.Floor(wx / 192), by = (int)Math.Floor(wy / 192);
            uint key = (uint)((bx << 8) | by);
            if (!lands.TryGetValue(key, out var land)) lands[key] = land = sampler.LoadLandblock(key);
            return land == null ? float.NaN : TerrainSampler.GetTerrainZ(land, (float)(wx - bx * 192), (float)(wy - by * 192));
        }

        {
            q.FindNearestPoly(new RcVec3f((float)cx, Z(cx, cy), (float)cy), new RcVec3f(3f, 3f, 3f), filter, out long crf, out _, out _);
            if (crf != 0 && piece.TryGetValue(crf, out int cp)) centrePiece = cp;
        }
        var letters = new Dictionary<int, char>();
        char LetterFor(int id) { if (id == centrePiece) return '.'; if (!letters.TryGetValue(id, out char c)) { c = (char)('a' + letters.Count % 26); letters[id] = c; } return c; }
        int n = (int)(radius / step);
        int walkMesh = 0, walkNo = 0, steepMesh = 0, steepNo = 0;
        double maxDrop = 0;
        log($"probe around ({cx:F1}, {cy:F1}) lb 0x{(lbx << 8) | lby:X4} local ({cx - lbx * 192:F1}, {cy - lby * 192:F1}); {step} m per character; north is up; FloorZ {TerrainSampler.FloorZ:F3}");
        for (int j = n; j >= -n; j--)
        {
            var row = new System.Text.StringBuilder();
            for (int i = -n; i <= n; i++)
            {
                double wx = cx + i * step, wy = cy + j * step;
                float z = Z(wx, wy);
                if (float.IsNaN(z)) { row.Append(' '); continue; }
                const double h = 0.75;
                double gx = (Z(wx + h, wy) - Z(wx - h, wy)) / (2 * h), gy = (Z(wx, wy + h) - Z(wx, wy - h)) / (2 * h);
                double nz = 1.0 / Math.Sqrt(1 + gx * gx + gy * gy);
                bool walk = nz >= TerrainSampler.FloorZ;
                q.FindNearestPoly(new RcVec3f((float)wx, z, (float)wy), new RcVec3f(0.6f, 1.2f, 0.6f), filter, out long rf, out _, out _);
                bool mesh = rf != 0;
                if (i == 0 && j == 0) { row.Append('@'); continue; }
                if (walk && mesh) { row.Append(piece.TryGetValue(rf, out int pid) ? LetterFor(pid) : '?'); walkMesh++; }
                else if (walk) { row.Append('x'); walkNo++; }
                else if (mesh) { row.Append('m'); steepMesh++; }
                else { row.Append('#'); steepNo++; }
            }
            log(row.ToString());
        }
        log("legend: '.' = the centre's connected piece; a,b,c... = other pieces " + string.Join(", ", letters.Select(kv => $"{kv.Value}={pieceSize[kv.Key]} polys")) + $"; centre piece {(centrePiece >= 0 ? pieceSize[centrePiece] : 0)} polys");
        log($"walkable slope with mesh {walkMesh}, walkable slope WITHOUT mesh {walkNo}, steep with mesh {steepMesh}, steep without {steepNo}");
        // Terrain height profile west-east and south-north through the centre, to see the rises.
        var prof = new System.Text.StringBuilder("west->east z: ");
        for (int i = -n; i <= n; i += Math.Max(1, (int)(3 / step))) prof.Append($"{Z(cx + i * step, cy):F1} ");
        log(prof.ToString());
        return 0;
    }
}
