using DotRecast.Core.Numerics;
using DotRecast.Detour;
using RynthNav.Routing;

namespace RynthNav.Baker;

// --dungeons-check LLLL[,LLLL...] --tiles <dir>
// Compares a baked dungeon with the game's own doorway graph (the EnvCell portal records, which is what
// RynthAi's DungeonPathfinder walks): every cell is put on the mesh at its position, and every doorway
// between two cells that isn't a drop should join two places of the same connected piece. Doorways whose
// two ends land in different pieces are the mesh's gaps.
internal static class DungeonCheck
{
    private sealed record Cell(uint Id, float X, float Y, float Z, List<uint> Doors);

    private static List<Cell> ReadCells(RynthCore2.Raycast.GeometryLoader geo, uint lb)
    {
        var cells = new List<Cell>();
        foreach (uint cellId in geo.CellDat.GetLandblockCellIds(lb))
        {
            byte[]? data = geo.CellDat.GetFileData(cellId);
            if (data == null || data.Length < 36) continue;
            try
            {
                using var ms = new MemoryStream(data);
                using var r = new BinaryReader(ms);
                r.ReadUInt32(); uint flags = r.ReadUInt32(); r.ReadUInt32();
                byte numSurf = r.ReadByte(), numPortals = r.ReadByte(); r.ReadUInt16();
                if ((flags & 0x01) != 0) continue;                       // a building cell
                ms.Seek(numSurf * 2L, SeekOrigin.Current);
                r.ReadUInt16(); r.ReadUInt16();
                float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle();
                var doors = new List<uint>();
                for (int i = 0; i < numPortals; i++)
                {
                    r.ReadUInt16(); r.ReadUInt16(); ushort other = r.ReadUInt16(); r.ReadUInt16();
                    if (other != 0xFFFF && other >= 0x100) doors.Add(((uint)lb << 16) | other);
                }
                cells.Add(new Cell(cellId, x, y, z, doors));
            }
            catch { }
        }
        return cells;
    }

    // Same rule as RynthAi's IsDropEdge: a step of 0.5 m or more at over 45 degrees is a drop, not a walk.
    private static bool IsDrop(Cell a, Cell b)
    {
        double dz = Math.Abs(b.Z - a.Z);
        if (dz < 0.5) return false;
        double dh = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        return dh < 1.0 || Math.Atan2(dz, dh) * 180.0 / Math.PI > 45.0;
    }

    public static int Run(RynthCore2.Raycast.GeometryLoader geo, string spec, string tilesDir, Action<string> log)
    {
        long cellsAll = 0, cellsOn = 0, edgesAll = 0, edgesBoth = 0, edgesSame = 0;
        int dungeons = 0;
        var worst = new List<(uint lb, double bad, long edges)>();
        foreach (uint lb in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => Convert.ToUInt32(s, 16)))
        {
            var f = DungeonTileFile.Read(Path.Combine(tilesDir, DungeonTileFile.FileName(lb)));
            if (f == null) { log($"  0x{lb:X4}: no readable dungeon tile in {tilesDir}"); continue; }
            DtNavMesh nav = f.BuildNavMesh();
            var q = new DtNavMeshQuery(nav);
            var filter = new DtQueryDefaultFilter();

            // Piece of every polygon.
            var piece = new Dictionary<long, int>(); int pieces = 0;
            for (int ti = 0; ti < nav.GetMaxTiles(); ti++)
            {
                DtMeshTile? tile = nav.GetTile(ti);
                if (tile?.data?.header == null) continue;
                long baseRef = nav.GetPolyRefBase(tile);
                for (int pi = 0; pi < tile.data.header.polyCount; pi++)
                {
                    long start = baseRef | (long)pi;
                    if (piece.ContainsKey(start)) continue;
                    int id = pieces++;
                    var stack = new Stack<long>(); stack.Push(start); piece[start] = id;
                    while (stack.Count > 0)
                    {
                        long rf = stack.Pop();
                        if (nav.GetTileAndPolyByRef(rf, out DtMeshTile t, out DtPoly p).Failed()) continue;
                        for (int k = p.firstLink; k != DtDetour.DT_NULL_LINK; k = t.links[k].next)
                        {
                            long n = t.links[k].refs;
                            if (n != 0 && !piece.ContainsKey(n)) { piece[n] = id; stack.Push(n); }
                        }
                    }
                }
            }

            List<Cell> cells = ReadCells(geo, lb);
            var at = new Dictionary<uint, int>();   // cell -> piece it stands on
            foreach (Cell c in cells)
            {
                // The cell's origin is near its floor: look for walkable ground close by (within a cell's reach sideways, a floor's height up and down).
                var centre = new RcVec3f(c.X + f.ShiftX, c.Z, c.Y + f.ShiftY);
                q.FindNearestPoly(centre, new RcVec3f(3f, 2.5f, 3f), filter, out long rf, out _, out _);
                if (rf != 0 && piece.TryGetValue(rf, out int pc)) at[c.Id] = pc;
            }
            var byId = cells.ToDictionary(c => c.Id);
            long e = 0, both = 0, same = 0;
            foreach (Cell a in cells)
                foreach (uint nb in a.Doors)
                {
                    if (nb <= a.Id || !byId.TryGetValue(nb, out Cell? b) || IsDrop(a, b)) continue;   // each doorway once
                    e++;
                    if (at.TryGetValue(a.Id, out int pa) && at.TryGetValue(b.Id, out int pb)) { both++; if (pa == pb) same++; }
                }
            cellsAll += cells.Count; cellsOn += at.Count; edgesAll += e; edgesBoth += both; edgesSame += same; dungeons++;
            double badShare = both == 0 ? 0 : 1.0 - (double)same / both;
            worst.Add((lb, badShare, both));
            if (spec.Count(ch => ch == ',') < 12)
                log($"  0x{lb:X4}: {cells.Count} cells, {at.Count} found on the mesh; {e} doorways, {both} with both ends found, {same} joined ({(both == 0 ? 0 : 100.0 * same / both):F0}%); mesh pieces {pieces}");
        }
        log($"checked {dungeons} dungeons: cells on the mesh {cellsOn}/{cellsAll} ({(cellsAll == 0 ? 0 : 100.0 * cellsOn / cellsAll):F0}%); doorways joined {edgesSame}/{edgesBoth} ({(edgesBoth == 0 ? 0 : 100.0 * edgesSame / edgesBoth):F1}%) of {edgesAll}");
        foreach (var w in worst.Where(w => w.edges >= 20).OrderByDescending(w => w.bad).Take(5)) log($"  worst: 0x{w.lb:X4}: {w.bad:P0} of {w.edges} doorways broken");
        return 0;
    }
}
