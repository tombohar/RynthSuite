// RynthCore.DatReaderCheck - proves a dat reader change on the real dat files (read only).
// Written for RynthCore (MIT). See the .csproj for the modes; the README-style notes are there.
//
// Every check goes through the reader's public API only (Open, header properties, RecordCount,
// EnumerateEntries, FindFile, GetFileData, GetLandblockCellIds, GetSampleIds), so the same
// source builds against any reader that keeps that API, and two builds' manifests can be
// compared line by line.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using System.Security.Cryptography;
using System.Text;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.DatReaderCheck;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "manifest") return Manifest(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "geo") return Geo(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "diff") return Diff(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "bench") return Bench(args[1], args.Length > 2 ? int.Parse(args[2]) : 3);
        Console.WriteLine("usage: manifest <AC folder> <out> | geo <AC folder> <out> | diff <a> <b> | bench <AC folder> [rounds]");
        return 2;
    }

    // ------------------------------------------------------------------ manifest

    private static int Manifest(string acDir, string outPath)
    {
        using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
        w.NewLine = "\n";
        int rc = 0;
        rc |= DumpDat(w, Path.Combine(acDir, "client_portal.dat"), cellDat: false);
        rc |= DumpDat(w, Path.Combine(acDir, "client_cell_1.dat"), cellDat: true);
        var p = Process.GetCurrentProcess();
        p.Refresh();
        w.WriteLine($"#t process peak working set {p.PeakWorkingSet64 / 1048576.0:F1} MB, peak private {p.PeakPagedMemorySize64 / 1048576.0:F1} MB");
        Console.WriteLine($"peak working set {p.PeakWorkingSet64 / 1048576.0:F1} MB");
        return rc;
    }

    private static int DumpDat(StreamWriter w, string path, bool cellDat)
    {
        string name = Path.GetFileName(path);
        Console.WriteLine($"== {name}");
        w.WriteLine($"== {name}");

        var sw = Stopwatch.StartNew();
        var db = new DatDatabase();
        bool ok = db.Open(path);
        double openMs = sw.Elapsed.TotalMilliseconds;
        w.WriteLine($"open {ok}");
        w.WriteLine($"#t open {openMs:F1} ms");
        if (!ok)
        {
            foreach (var l in db.DiagLog) w.WriteLine("#t diag " + l);
            Console.WriteLine("open failed: " + string.Join(" | ", db.DiagLog));
            return 1;
        }
        w.WriteLine($"header FileType={db.FileType:X8} BlockSize={db.BlockSize} FileSize={db.FileSize} DataSet={db.DataSet:X8} BTreeRoot={db.BTreeRoot:X8} IsLoaded={db.IsLoaded}");

        sw.Restart();
        int count = db.RecordCount;
        w.WriteLine($"records {count}");
        w.WriteLine($"#t RecordCount {sw.Elapsed.TotalMilliseconds:F1} ms");
        w.WriteLine("samples " + string.Join(",", db.GetSampleIds(30).Select(i => i.ToString("X8"))));

        // 1. Enumerate (ids, flags, offsets, sizes) without reading any file.
        sw.Restart();
        var entries = new List<DatBTreeEntry>(count);
        foreach (var e in db.EnumerateEntries())
            entries.Add(new DatBTreeEntry { BitFlags = e.BitFlags, ObjectId = e.ObjectId, FileOffset = e.FileOffset, FileSize = e.FileSize });
        double enumMs = sw.Elapsed.TotalMilliseconds;
        w.WriteLine($"enumerated {entries.Count}");
        w.WriteLine($"#t EnumerateEntries {enumMs:F1} ms");
        bool ascending = true;
        for (int i = 1; i < entries.Count; i++) if (entries[i].ObjectId <= entries[i - 1].ObjectId) { ascending = false; break; }
        w.WriteLine($"ascending {ascending}");

        // 2. Every file by id: FindFile must return the same entry, GetFileData its bytes.
        sw.Restart();
        long bytes = 0;
        int nulls = 0, entryMismatch = 0;
        double findMs = 0, readMs = 0;
        var swOne = new Stopwatch();
        foreach (var e in entries)
        {
            swOne.Restart();
            var f = db.FindFile(e.ObjectId);
            findMs += swOne.Elapsed.TotalMilliseconds;
            bool same = f != null && f.ObjectId == e.ObjectId && f.FileOffset == e.FileOffset && f.FileSize == e.FileSize && f.BitFlags == e.BitFlags;
            if (!same) entryMismatch++;
            swOne.Restart();
            byte[] data = db.GetFileData(e.ObjectId);
            readMs += swOne.Elapsed.TotalMilliseconds;
            string digest;
            if (data == null) { nulls++; digest = "NULL"; }
            else { bytes += data.Length; digest = data.Length + " " + Convert.ToHexString(SHA256.HashData(data)); }
            w.WriteLine($"f {e.ObjectId:X8} {e.BitFlags:X8} {e.FileOffset:X8} {e.FileSize} {(same ? "=" : "!")} {digest}");
        }
        double allMs = sw.Elapsed.TotalMilliseconds;
        w.WriteLine($"files {entries.Count} read-null {nulls} find-mismatch {entryMismatch} bytes {bytes}");
        w.WriteLine($"#t all files {allMs:F0} ms (FindFile {findMs:F0} ms, GetFileData incl. its own find {readMs:F0} ms, hashing the rest), {bytes / 1048576.0:F0} MB");
        Console.WriteLine($"  {entries.Count} files, {nulls} null, {entryMismatch} find mismatches, {bytes / 1048576.0:F0} MB in {allMs:F0} ms");

        // 3. Misses. Ids next to every real id, and a fixed pseudo-random set.
        var idSet = new HashSet<uint>(entries.Select(e => e.ObjectId));
        sw.Restart();
        int probes = 0, misses = 0, wrongHits = 0, wrongMisses = 0;
        void Probe(uint id)
        {
            probes++;
            var f = db.FindFile(id);
            bool expect = idSet.Contains(id);
            if (f == null) { misses++; if (expect) wrongMisses++; }
            else if (!expect || f.ObjectId != id) wrongHits++;
        }
        foreach (var e in entries)
        {
            if (e.ObjectId != uint.MaxValue) Probe(e.ObjectId + 1);
            if (e.ObjectId != 0) Probe(e.ObjectId - 1);
        }
        var rng = new Random(20261005);
        for (int i = 0; i < 1_000_000; i++) Probe((uint)rng.NextInt64(0, 1L << 32));
        // The prefix the reader is used for: the portal types 0x01-0x40 near the low ids.
        for (uint t = 0; t <= 0x40; t++) for (uint lo = 0; lo < 0x400; lo++) Probe((t << 24) | lo);
        Probe(0); Probe(uint.MaxValue);
        w.WriteLine($"probes {probes} misses {misses} wrong-hits {wrongHits} wrong-misses {wrongMisses}");
        w.WriteLine($"#t probes {sw.Elapsed.TotalMilliseconds:F0} ms");

        // 4. Landblocks: the cell dat's per-landblock lookups.
        sw.Restart();
        int lbWithCells = 0, cellIds = 0, lbFound = 0, infoFound = 0;
        for (uint lb = 0; lb <= 0xFFFF; lb++)
        {
            var ids = db.GetLandblockCellIds(lb);
            bool hasLb = db.FindFile((lb << 16) | 0xFFFF) != null;
            bool hasInfo = db.FindFile((lb << 16) | 0xFFFE) != null;
            if (hasLb) lbFound++;
            if (hasInfo) infoFound++;
            if (ids.Count > 0) lbWithCells++;
            cellIds += ids.Count;
            if (cellDat || ids.Count > 0 || hasLb || hasInfo)
                w.WriteLine($"lb {lb:X4} {(hasLb ? 1 : 0)}{(hasInfo ? 1 : 0)} {ids.Count} {Hash(ids)}");
        }
        w.WriteLine($"landblocks with-FFFF {lbFound} with-FFFE {infoFound} with-cells {lbWithCells} cell-ids {cellIds}");
        w.WriteLine($"#t landblock scan {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Cross-check GetLandblockCellIds against the enumeration.
        var expected = new Dictionary<uint, int>();
        foreach (var e in entries)
        {
            uint low = e.ObjectId & 0xFFFF;
            if (low >= 0x0100 && low <= 0xFFFD) expected[e.ObjectId >> 16] = expected.TryGetValue(e.ObjectId >> 16, out int n) ? n + 1 : 1;
        }
        int lbMismatch = 0;
        foreach (var kv in expected) if (db.GetLandblockCellIds(kv.Key).Count != kv.Value) lbMismatch++;
        w.WriteLine($"landblock-vs-enumeration mismatches {lbMismatch}");

        db.Close();
        w.WriteLine($"closed IsLoaded={db.IsLoaded} find-after-close={(db.FindFile(0x0E00000E) == null ? "null" : "hit")} data-after-close={(db.GetFileData(0x0E00000E) == null ? "null" : "data")}");
        db.Dispose();
        return 0;
    }

    private static string Hash(List<uint> ids)
    {
        if (ids.Count == 0) return "-";
        var b = new byte[ids.Count * 4];
        for (int i = 0; i < ids.Count; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), ids[i]);
        return Convert.ToHexString(SHA256.HashData(b), 0, 12);
    }

    // ------------------------------------------------------------------ bench

    // Timings only (no hashing, no output file): run it a few times against warm OS file cache.
    private static int Bench(string acDir, int rounds)
    {
        foreach (string name in new[] { "client_portal.dat", "client_cell_1.dat" })
        {
            string path = Path.Combine(acDir, name);
            Console.WriteLine($"== {name}  (managed heap the open reader holds after the lookups below: {ReaderHeapKb(path):F0} KB)");
            for (int r = 0; r < rounds; r++)
            {
                var sw = Stopwatch.StartNew();
                var db = new DatDatabase();
                db.Open(path);
                double open = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); int count = db.RecordCount; double rcMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var ids = new List<uint>(count);
                foreach (var e in db.EnumerateEntries()) ids.Add(e.ObjectId);
                double enumMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                long bytes = 0;
                foreach (uint id in ids) bytes += db.GetFileData(id)?.Length ?? 0;
                double readMs = sw.Elapsed.TotalMilliseconds;
                // A fresh reader for the lookups, so they start cold (like a plugin after Open).
                db.Dispose();
                db = new DatDatabase();
                db.Open(path);
                var rng = new Random(7);
                sw.Restart();
                for (int i = 0; i < 200_000; i++) db.FindFile(ids[rng.Next(ids.Count)]);
                double findMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                int cells = 0;
                for (uint lb = 0; lb <= 0xFFFF; lb++) { cells += db.GetLandblockCellIds(lb).Count; db.FindFile((lb << 16) | 0xFFFF); db.FindFile((lb << 16) | 0xFFFE); }
                double lbMs = sw.Elapsed.TotalMilliseconds;
                db.Dispose();
                Console.WriteLine($"  round {r + 1}: open {open:F1} ms, RecordCount {rcMs:F0} ms, enumerate {enumMs:F0} ms, read all {ids.Count} files ({bytes / 1048576.0:F0} MB) {readMs:F0} ms, " +
                                  $"200k random FindFile {findMs:F0} ms, 65,536 landblocks (cell ids + FFFF + FFFE) {lbMs:F0} ms");
            }
        }
        var p = Process.GetCurrentProcess();
        p.Refresh();
        Console.WriteLine($"peak working set {p.PeakWorkingSet64 / 1048576.0:F1} MB");
        return 0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static double ReaderHeapKb(string path)
    {
        long before = GC.GetTotalMemory(true);
        var db = Warm(path);
        long after = GC.GetTotalMemory(true);
        GC.KeepAlive(db);
        db.Dispose();
        return (after - before) / 1024.0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static DatDatabase Warm(string path)
    {
        var db = new DatDatabase();
        db.Open(path);
        _ = db.RecordCount;
        for (uint lb = 0; lb <= 0xFFFF; lb++) { db.GetLandblockCellIds(lb); db.FindFile((lb << 16) | 0xFFFF); }
        return db;
    }

    // ------------------------------------------------------------------ geo

    private static int Geo(string acDir, string outPath)
    {
        using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
        w.NewLine = "\n";
        var sw = Stopwatch.StartNew();
        var geo = new GeometryLoader();
        if (!geo.Initialize(acDir)) { Console.WriteLine("Geometry loader failed: " + geo.StatusMessage); return 1; }
        w.WriteLine($"#t GeometryLoader.Initialize {sw.Elapsed.TotalMilliseconds:F0} ms");
        w.WriteLine("status " + geo.StatusMessage);

        var dungeonLbs = new SortedSet<uint>();
        foreach (var e in geo.CellDat.EnumerateEntries())
        {
            uint low = e.ObjectId & 0xFFFF;
            if (low >= 0x0100 && low <= 0xFFFD) dungeonLbs.Add(e.ObjectId >> 16);
        }
        w.WriteLine($"landblocks-with-cells {dungeonLbs.Count}");

        double dungeonMs = 0, outdoorMs = 0, slowest = 0;
        uint slowestLb = 0;
        int vols = 0, polys = 0;
        foreach (uint lb in dungeonLbs)
        {
            var t = Stopwatch.StartNew();
            var v = geo.GetLandblockGeometry((lb << 16) | 0x0100);
            var p = geo.DungeonLOS.GetDungeonMapPolygons(lb);
            var fl = geo.DungeonLOS.GetDungeonMapFloorPolygons(lb);
            var cells = geo.DungeonLOS.GetDungeonMapCells(lb);
            double ms = t.Elapsed.TotalMilliseconds;
            dungeonMs += ms;
            if (ms > slowest) { slowest = ms; slowestLb = lb; }
            vols += v.Count; polys += p.Count;
            w.WriteLine($"d {lb:X4} v{v.Count} {Digest(v)} p{p.Count} {Digest(p)} f{fl.Count} {Digest(fl)} c{cells.Count}");
            geo.FlushLandblock(lb);
        }
        w.WriteLine($"#t dungeon landblocks {dungeonLbs.Count}: {dungeonMs:F0} ms total, {dungeonMs / Math.Max(1, dungeonLbs.Count):F2} ms each, slowest {slowestLb:X4} {slowest:F1} ms");
        Console.WriteLine($"dungeons: {dungeonLbs.Count} landblocks, {vols} volumes, {polys} polygons, {dungeonMs:F0} ms");

        int outdoor = 0;
        for (uint x = 0; x < 256; x += 4)
            for (uint y = 0; y < 256; y += 4)
            {
                uint lb = (x << 8) | y;
                var t = Stopwatch.StartNew();
                var v = geo.GetLandblockGeometry((lb << 16) | 0x0001);
                float z = geo.GetTerrainZWorld(x * 192f + 96f, y * 192f + 96f);
                outdoorMs += t.Elapsed.TotalMilliseconds;
                outdoor++;
                w.WriteLine($"o {lb:X4} v{v.Count} {Digest(v)} z{BitConverter.SingleToInt32Bits(z):X8}");
                geo.FlushLandblock(lb);
            }
        w.WriteLine($"#t outdoor landblocks {outdoor}: {outdoorMs:F0} ms total, {outdoorMs / outdoor:F2} ms each");
        Console.WriteLine($"outdoor: {outdoor} landblocks, {outdoorMs:F0} ms");
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        w.WriteLine($"#t process peak working set {proc.PeakWorkingSet64 / 1048576.0:F1} MB");
        geo.Dispose();
        return 0;
    }

    private static string Digest(List<BoundingVolume> vs)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var v in vs)
        {
            Add(h, (int)v.Type); Add(h, v.Center); Add(h, v.Dimensions); Add(h, v.Min); Add(h, v.Max);
            Add(h, v.IsDoor ? 1 : 0); Add(h, v.NoAabbFallback ? 1 : 0);
            Add(h, v.Vertices); Add(h, v.MeshTriangles);
        }
        return Convert.ToHexString(h.GetHashAndReset(), 0, 12);
    }

    private static string Digest(List<DungeonLOS.MapPolygon> ps)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in ps)
        {
            Add(h, p.Vertices); Add(h, new Vector3(p.CellX, p.CellY, p.CellZ));
            Add(h, p.IsPortal ? 1 : 0); Add(h, p.IsFloor ? 1 : 0); Add(h, (int)p.CellId);
        }
        return Convert.ToHexString(h.GetHashAndReset(), 0, 12);
    }

    private static void Add(IncrementalHash h, int v) { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v); h.AppendData(b); }
    private static void Add(IncrementalHash h, Vector3 v)
    {
        Add(h, BitConverter.SingleToInt32Bits(v.X)); Add(h, BitConverter.SingleToInt32Bits(v.Y)); Add(h, BitConverter.SingleToInt32Bits(v.Z));
    }
    private static void Add(IncrementalHash h, Vector3[] vs)
    {
        if (vs == null) { Add(h, -1); return; }
        Add(h, vs.Length);
        foreach (var v in vs) Add(h, v);
    }

    // ------------------------------------------------------------------ diff

    private static int Diff(string a, string b)
    {
        var la = File.ReadLines(a).Where(l => !l.StartsWith("#t")).GetEnumerator();
        var lb = File.ReadLines(b).Where(l => !l.StartsWith("#t")).GetEnumerator();
        long n = 0, diffs = 0;
        while (true)
        {
            bool ha = la.MoveNext(), hb = lb.MoveNext();
            if (!ha && !hb) break;
            n++;
            string sa = ha ? la.Current : "<end>", sb = hb ? lb.Current : "<end>";
            if (sa != sb)
            {
                if (diffs < 40) Console.WriteLine($"line {n}:\n  a: {sa}\n  b: {sb}");
                diffs++;
            }
        }
        Console.WriteLine($"{n} lines compared, {diffs} differ");
        Console.WriteLine("timings a:");
        foreach (var l in File.ReadLines(a).Where(l => l.StartsWith("#t"))) Console.WriteLine("  " + l);
        Console.WriteLine("timings b:");
        foreach (var l in File.ReadLines(b).Where(l => l.StartsWith("#t"))) Console.WriteLine("  " + l);
        return diffs == 0 ? 0 : 1;
    }
}
