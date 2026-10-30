using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace RynthNav.Baker;

// --buildings-check LLLL[,LLLL...] --tiles <dir>
// Can nav walk into buildings? For each building cell that has a doorway to the outside (an EnvCell portal to
// 0xFFFF), is there walkable mesh at the cell, and is it joined to the main outdoor ground around it?
internal static class BuildingCheck
{
    private sealed record Entrance(uint Cell, float X, float Y, float Z);

    private static List<Entrance> ReadEntrances(RynthCore2.Raycast.GeometryLoader geo, uint lb)
    {
        var list = new List<Entrance>();
        float offX = ((lb >> 8) & 0xFF) * 192f, offY = (lb & 0xFF) * 192f;
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
                if ((flags & 0x01) == 0) continue;                       // a dungeon cell
                ms.Seek(numSurf * 2L, SeekOrigin.Current);
                r.ReadUInt16(); r.ReadUInt16();
                float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle();
                bool outside = false;
                for (int i = 0; i < numPortals; i++) { r.ReadUInt16(); r.ReadUInt16(); ushort other = r.ReadUInt16(); r.ReadUInt16(); if (other == 0xFFFF) outside = true; }
                if (outside) list.Add(new Entrance(cellId, x + offX, y + offY, z));
            }
            catch { }
        }
        return list;
    }

    /// <summary>--buildings-list: every landblock with a building that has a doorway to the outside (one hex landblock per line to stdout).</summary>
    public static int List(RynthCore2.Raycast.GeometryLoader geo, Action<string> log)
    {
        int lbs = 0, entrances = 0;
        for (uint lb = 0; lb <= 0xFFFF; lb++)
        {
            int n = ReadEntrances(geo, lb).Count;
            if (n == 0) continue;
            lbs++; entrances += n;
            Console.WriteLine($"{lb:X4}");
        }
        log($"{lbs} landblocks have a building with an outside doorway ({entrances} entrance cells)");
        return 0;
    }

    public static int Run(RynthCore2.Raycast.GeometryLoader geo, string spec, string tilesDir, Action<string> log)
    {
        int total = 0, noMesh = 0, isolated = 0, joined = 0;
        foreach (uint lb in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => Convert.ToUInt32(s, 16)))
        {
            // The landblock and its neighbours, as the plugin loads them.
            var nav = new DtNavMesh();
            var p = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 64, maxPolys = 1 << 16 };
            nav.Init(ref p, 6);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    uint t = (uint)((((int)(lb >> 8) + dx) << 8) | ((int)(lb & 0xFF) + dy));
                    string path = Path.Combine(tilesDir, $"nav_{t:X4}.tile");
                    if (!File.Exists(path)) continue;
                    using var fr = File.OpenRead(path); using var br = new BinaryReader(fr);
                    nav.AddTile(new DtMeshDataReader().Read(br, 6), 0, 0, out _);
                }
            var piece = new Dictionary<long, int>(); var size = new List<int>();
            for (int ti = 0; ti < nav.GetMaxTiles(); ti++)
            {
                DtMeshTile? tile = nav.GetTile(ti);
                if (tile?.data?.header == null) continue;
                long baseRef = nav.GetPolyRefBase(tile);
                for (int pi = 0; pi < tile.data.header.polyCount; pi++)
                {
                    long start = baseRef | (long)pi;
                    if (piece.ContainsKey(start)) continue;
                    int id = size.Count, n = 0; size.Add(0);
                    var stack = new Stack<long>(); stack.Push(start); piece[start] = id;
                    while (stack.Count > 0)
                    {
                        long rf = stack.Pop(); n++;
                        if (nav.GetTileAndPolyByRef(rf, out DtMeshTile t2, out DtPoly pl).Failed()) continue;
                        for (int k = pl.firstLink; k != DtDetour.DT_NULL_LINK; k = t2.links[k].next)
                        { long nb = t2.links[k].refs; if (nb != 0 && !piece.ContainsKey(nb)) { piece[nb] = id; stack.Push(nb); } }
                    }
                    size[id] = n;
                }
            }
            if (size.Count == 0) { log($"  0x{lb:X4}: no tiles in {tilesDir}"); continue; }
            int main = size.IndexOf(size.Max());
            var q = new DtNavMeshQuery(nav); var filter = new DtQueryDefaultFilter();
            int tn = 0, tj = 0, ti2 = 0, tt = 0;
            foreach (Entrance e in ReadEntrances(geo, lb))
            {
                tt++;
                q.FindNearestPoly(new RcVec3f(e.X, e.Z, e.Y), new RcVec3f(2.5f, 3f, 2.5f), filter, out long rf, out _, out _);
                if (rf == 0 || !piece.TryGetValue(rf, out int pc)) { tn++; continue; }
                if (pc == main) tj++; else ti2++;
            }
            total += tt; noMesh += tn; isolated += ti2; joined += tj;
            log($"  0x{lb:X4}: {tt} building entrance cells: {tj} joined to the main ground, {ti2} on an isolated piece, {tn} with no mesh at the cell");
        }
        log($"entrance cells: {total}; joined {joined}, isolated {isolated}, no mesh {noMesh}");
        return 0;
    }
}
