using System;
using System.Collections.Generic;
using System.IO;
using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace RynthNav.Routing;

/// <summary>
/// A dungeon's navmesh, one file (NavData\dun_LLLL.tile) holding all its Detour tiles.
///
/// A dungeon's cells are not kept inside their landblock: they run into negative landblock-local x/y
/// (down to about -360 m) and out past 192 m (to about 390 m), over 190 m tiles that are no real
/// landblocks. So a dungeon is its own navmesh, laid out in the landblock-local frame shifted by
/// (ShiftX, ShiftY) metres, a multiple of 192 that makes every coordinate positive:
///
///     mesh position = landblock-local position + shift      (x east, up, y north; the pose's x, y, z)
///
/// Tiles are 192 m squares on that frame (tile index = floor(mesh / 192), in each tile's header).
///
/// File: "RNDG" (0x47444E52), version 1, int shiftX, int shiftY, int count, then per tile
/// int length and the tile as DotRecast's DtMeshDataWriter writes it (little endian).
/// Host-free: the plugin, the baker and the tests share it.
/// </summary>
internal sealed class DungeonTileFile
{
    public const string Prefix = "dun_";
    public const string Extension = ".tile";
    private const uint Magic = 0x47444E52;   // "RNDG"
    private const int Version = 1;
    public const float TileSize = 192f;

    public int ShiftX { get; }
    public int ShiftY { get; }
    public IReadOnlyList<DtMeshData> Tiles { get; }

    public DungeonTileFile(int shiftX, int shiftY, IReadOnlyList<DtMeshData> tiles) { ShiftX = shiftX; ShiftY = shiftY; Tiles = tiles; }

    public static string FileName(uint lb) => $"{Prefix}{lb:X4}{Extension}";

    public static void Write(string path, int shiftX, int shiftY, IReadOnlyList<DtMeshData> tiles)
    {
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Magic); bw.Write(Version); bw.Write(shiftX); bw.Write(shiftY); bw.Write(tiles.Count);
            foreach (DtMeshData md in tiles)
            {
                using var ms = new MemoryStream();
                using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                    new DtMeshDataWriter().Write(tw, md, RcByteOrder.LITTLE_ENDIAN, false);
                bw.Write((int)ms.Length);
                bw.Write(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
        File.Move(tmp, path, overwrite: true);   // a bake stopped half way never leaves a truncated file
    }

    /// <summary>Reads a dungeon file; null when it isn't one (or is cut short).</summary>
    public static DungeonTileFile? Read(string path, int vertsPerPoly = 6)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != Magic || br.ReadInt32() != Version) return null;
            int sx = br.ReadInt32(), sy = br.ReadInt32(), n = br.ReadInt32();
            if (n < 0 || n > 4096) return null;
            var tiles = new List<DtMeshData>(n);
            for (int i = 0; i < n; i++)
            {
                int len = br.ReadInt32();
                if (len <= 0 || len > fs.Length - fs.Position) return null;
                using var ms = new MemoryStream(br.ReadBytes(len));
                using var tr = new BinaryReader(ms);
                tiles.Add(new DtMeshDataReader().Read(tr, vertsPerPoly));
            }
            return new DungeonTileFile(sx, sy, tiles);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>A Detour navmesh of just these tiles (mesh frame: tile (x, y) at x * 192, y * 192).</summary>
    public DtNavMesh BuildNavMesh()
    {
        var nav = new DtNavMesh();
        var p = new DtNavMeshParams
        {
            orig = new DotRecast.Core.Numerics.RcVec3f(0, 0, 0), tileWidth = TileSize, tileHeight = TileSize,
            maxTiles = Math.Max(1, Tiles.Count), maxPolys = 1 << 16,
        };
        nav.Init(ref p, 6);
        foreach (DtMeshData md in Tiles) nav.AddTile(md, 0, 0, out _);
        return nav;
    }
}
