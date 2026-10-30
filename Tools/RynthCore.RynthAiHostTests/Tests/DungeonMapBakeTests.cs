using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Maps;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Dungeon floor-plan bake (2026-10-05): RynthAi writes <landblock>_<layer>.bin floor plans for
// the StatusAgent's /map (DrakRemote's dungeon map). The bake was the disk half of the old
// DungeonMapTexture, deleted with the D3D9 textures on 2026-09-30 (f78392b); this is it back,
// disk only. The plans already in C:\Games\RynthSuite\RynthAi\Maps are READ here, never
// written: every bake in these tests goes to the scratch folder.
internal static class DungeonMapBakeTests
{
    private const string AcDir = @"C:\Turbine\Asheron's Call";
    private const string LiveMaps = @"C:\Games\RynthSuite\RynthAi\Maps";
    private const uint Matron = 0x6346; // Matron Hive South (PatrolProof / LosProof fixtures)

    public static void Register(Runner r)
    {
        r.Add("map bake: file format is the StatusAgent's (header, ARGB pixels, north row first)", Format);
        r.Add("map bake: same bytes are not rewritten, changed bytes are, no temp files left", NoChurn);
        r.Add("map bake: file names use the short landblock id", Names);
        r.Add("map bake: 0x6346 from the real dats matches the plan already on disk (if dats and plan are here)", MatchesExisting6346);
        r.Add("map bake: the plans already on disk keep their names, most byte for byte (if the dats are here)", MatchesAllExisting);
    }

    private static string Dir(string name)
    {
        string d = Path.Combine(Program.TempRoot, "mapbake", name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Format()
    {
        // A 3x2 block of flat cells at gx 10..12, gy 4..5 plus one ramp cell north of it.
        var cells = new Dictionary<(int, int), DungeonMapUi.CellType>();
        for (int gx = 10; gx <= 12; gx++)
            for (int gy = 4; gy <= 5; gy++)
                cells[(gx, gy)] = DungeonMapUi.CellType.Flat;
        cells[(11, 6)] = DungeonMapUi.CellType.SlopeUp;

        byte[]? bytes = DungeonMapBake.BuildFile(cells);
        Check.NotNull(bytes, "built");
        if (bytes is null) return;
        int w = 3, h = 3;
        Check.Eq(bytes.Length, 20 + w * h * 4, "length = header + w*h*4");
        Check.Eq(BitConverter.ToUInt32(bytes, 0), 0x524D5458u, "magic XTMR");
        Check.Eq(BitConverter.ToInt32(bytes, 4), 10, "xMin");
        Check.Eq(BitConverter.ToInt32(bytes, 8), 4, "yMin");
        Check.Eq(BitConverter.ToInt32(bytes, 12), w, "w");
        Check.Eq(BitConverter.ToInt32(bytes, 16), h, "h");

        uint Px(int col, int row) => BitConverter.ToUInt32(bytes, 20 + (row * w + col) * 4);
        // Row 0 is the northernmost row (gy 6): only the ramp cell at gx 11, an edge cell.
        Check.Eq(Px(0, 0), 0u, "empty cell is transparent");
        Check.Eq(Px(1, 0) >> 24, 255u, "edge cell is opaque");
        Check.Eq((Px(1, 0) >> 16) & 0xFF, 255u, "ramp-up edge is red");
        // Row 1 (gy 5): (11,5) has neighbours on all four sides, so it is the translucent fill.
        Check.Eq(Px(1, 1) >> 24, (uint)(0.40f * 255f), "interior cell is the translucent fill");
        Check.Eq(Px(0, 1) >> 24, 255u, "border cell is an edge");
        // Row 2 (gy 4): southern edge.
        Check.Eq(Px(2, 2) >> 24, 255u, "south-east corner is an edge");
    }

    private static void NoChurn()
    {
        string dir = Dir("churn");
        var cells = new Dictionary<(int, int), DungeonMapUi.CellType> { [(0, 0)] = DungeonMapUi.CellType.Flat, [(1, 0)] = DungeonMapUi.CellType.Flat };
        byte[] a = DungeonMapBake.BuildFile(cells)!;
        string path = Path.Combine(dir, "00001234_2.bin");

        Check.Eq(DungeonMapBake.SaveToDisk(dir, 0x1234, 2, a), DungeonMapBake.SaveResult.Written, "first save writes");
        Check.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(a), "file holds the bytes");
        var past = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, past);

        Check.Eq(DungeonMapBake.SaveToDisk(dir, 0x1234, 2, a), DungeonMapBake.SaveResult.Unchanged, "same bytes: unchanged");
        Check.Eq(File.GetLastWriteTimeUtc(path), past, "same bytes: file not touched (mtime kept, so the agent's ETag holds)");

        cells[(2, 0)] = DungeonMapUi.CellType.SlopeDown;
        byte[] b = DungeonMapBake.BuildFile(cells)!;
        Check.Eq(DungeonMapBake.SaveToDisk(dir, 0x1234, 2, b), DungeonMapBake.SaveResult.Written, "changed bytes: written");
        Check.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(b), "file holds the new bytes");
        Check.Eq(Directory.GetFiles(dir).Length, 1, "no temp files left: " + string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

        // A reader holding the file open the way the agent does (ReadWrite share, no Delete)
        // makes the replace fail; the save must not throw or leave a temp file.
        cells[(3, 0)] = DungeonMapUi.CellType.Flat;
        byte[] c = DungeonMapBake.BuildFile(cells)!;
        DungeonMapBake.SaveResult held;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            held = DungeonMapBake.SaveToDisk(dir, 0x1234, 2, c);
        Console.WriteLine($"    save while the agent holds the file open: {held}");
        Check.Eq(Directory.GetFiles(dir).Length, 1, "no temp files left after a held file");
        Check.Eq(DungeonMapBake.SaveToDisk(dir, 0x1234, 2, c), DungeonMapBake.SaveResult.Written, "written once released");
    }

    private static void Names()
    {
        Check.Eq(DungeonMapBake.FileName(0x6346, 1), "00006346_1.bin", "short id");
        Check.Eq(DungeonMapBake.FileName(0x63460000, 1), "00006346_1.bin", "long id is shortened");
        Check.Eq(DungeonMapBake.FileName(0x002B, 10), "0000002B_10.bin", "two-digit layer");
        Check.Eq(DungeonMapBake.ShortLandblock(0x63460000), 0x6346u, "ShortLandblock");
    }

    private static GeometryLoader? _geo;
    private static bool _geoTried;

    private static DungeonLOS? Los()
    {
        if (!_geoTried)
        {
            _geoTried = true;
            if (Directory.Exists(AcDir))
            {
                var g = new GeometryLoader();
                if (g.Initialize(AcDir)) _geo = g;
                else Console.WriteLine("    geometry loader failed: " + g.StatusMessage);
            }
        }
        return _geo?.DungeonLOS;
    }

    /// <summary>Bake one landblock into a scratch folder and compare each layer with the live plan.</summary>
    private static (int Same, int Differ, int OnlyNew, int OnlyLive, List<string> Notes) Compare(DungeonLOS los, uint lb, string outDir)
    {
        var notes = new List<string>();
        var (zLayers, cells) = DungeonMapUi.BuildFloorCells(los, lb);
        DungeonMapBake.BakeLayers(outDir, lb, zLayers, cells);
        string prefix = $"{lb:X8}_";
        var baked = Directory.GetFiles(outDir, prefix + "*.bin").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = Directory.Exists(LiveMaps)
            ? Directory.GetFiles(LiveMaps, prefix + "*.bin").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string?>();
        int same = 0, differ = 0, onlyNew = 0, onlyLive = 0;
        foreach (string? name in baked.Union(live).OrderBy(n => n))
        {
            if (name is null) continue;
            bool inNew = baked.Contains(name), inLive = live.Contains(name);
            if (inNew && !inLive) { onlyNew++; notes.Add($"{name}: new (no live plan)"); continue; }
            if (!inNew) { onlyLive++; notes.Add($"{name}: live plan has no new counterpart"); continue; }
            byte[] n = File.ReadAllBytes(Path.Combine(outDir, name));
            byte[] l = File.ReadAllBytes(Path.Combine(LiveMaps, name));
            if (n.AsSpan().SequenceEqual(l)) { same++; continue; }
            differ++;
            notes.Add($"{name}: differs - new {Header(n)} vs live {Header(l)}, {DiffPixels(n, l)}");
        }
        return (same, differ, onlyNew, onlyLive, notes);
    }

    private static string Header(byte[] b) => b.Length < 20 ? $"({b.Length} bytes)"
        : $"x{BitConverter.ToInt32(b, 4)} y{BitConverter.ToInt32(b, 8)} {BitConverter.ToInt32(b, 12)}x{BitConverter.ToInt32(b, 16)}";

    private static string DiffPixels(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length < 20 || a.AsSpan(4, 16).SequenceEqual(b.AsSpan(4, 16)) == false) return "different bounds";
        int diff = 0, total = (a.Length - 20) / 4;
        for (int i = 20; i < a.Length; i += 4)
            if (BitConverter.ToUInt32(a, i) != BitConverter.ToUInt32(b, i)) diff++;
        return $"{diff} of {total} pixels differ";
    }

    private static void MatchesExisting6346()
    {
        var los = Los();
        if (los is null) { Console.WriteLine("    skipped: no dats at " + AcDir); return; }
        if (!File.Exists(Path.Combine(LiveMaps, DungeonMapBake.FileName(Matron, 0))))
        { Console.WriteLine("    skipped: no live plan for 0x6346"); return; }
        var (same, differ, onlyNew, onlyLive, notes) = Compare(los, Matron, Dir("6346"));
        Console.WriteLine($"    0x6346: {same} identical, {differ} differ, {onlyNew} new only, {onlyLive} live only");
        foreach (string n in notes) Console.WriteLine("      " + n);
        Check.True(same > 0, "some layers baked");
        Check.Eq(differ + onlyNew + onlyLive, 0, "every 0x6346 layer is byte-identical to the live plan");
    }

    private static void MatchesAllExisting()
    {
        var los = Los();
        if (los is null) { Console.WriteLine("    skipped: no dats at " + AcDir); return; }
        if (!Directory.Exists(LiveMaps)) { Console.WriteLine("    skipped: no live maps folder"); return; }
        var rx = new Regex(@"^([0-9A-Fa-f]{8})_(\d+)\.bin$");
        var lbs = Directory.GetFiles(LiveMaps, "*.bin")
            .Select(f => rx.Match(Path.GetFileName(f)))
            .Where(m => m.Success)
            .Select(m => Convert.ToUInt32(m.Groups[1].Value, 16))
            .Distinct().OrderBy(x => x).ToList();
        string outDir = Dir("all");
        int same = 0, differ = 0, onlyNew = 0, onlyLive = 0;
        foreach (uint lb in lbs)
        {
            var c = Compare(los, lb, outDir);
            same += c.Same; differ += c.Differ; onlyNew += c.OnlyNew; onlyLive += c.OnlyLive;
            if (c.Notes.Count > 0)
            {
                Console.WriteLine($"    0x{lb:X4}: {c.Same} identical, {c.Differ} differ, {c.OnlyNew} new only, {c.OnlyLive} live only");
                foreach (string n in c.Notes) Console.WriteLine("      " + n);
            }
        }
        Console.WriteLine($"    {lbs.Count} landblocks: {same} plans identical, {differ} differ, {onlyNew} new only, {onlyLive} live only");
        // 2026-10-05: 59 of the 81 plans baked May-June are identical; 22 differ by a few
        // pixels (or a few cells of bounds) because the floor rasteriser has changed since
        // they were baked. Those 22 are refreshed once, on the next visit, to what the radar
        // draws today; the identical ones are left alone. What must hold: the layer numbering
        // is unchanged (every live plan has a counterpart under the same name), the header
        // format is the same, and most plans come out identical.
        Check.Eq(onlyLive, 0, "every live plan has a counterpart under the same file name (layer numbering unchanged)");
        Check.Eq(onlyNew, 0, "no extra layers compared with the live plans");
        Check.True(same > differ, $"most live plans are reproduced byte for byte ({same} identical, {differ} differ)");
    }
}
