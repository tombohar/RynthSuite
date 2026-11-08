// DunRouteSidecar: the doorway points of the dungeon routes RynthAi's route maker saved
// (/ra dunroute, /ra dunpatrol-save, RynthAi.Routes), kept beside them (2026-10-30).
//
// Why: the route maker lays the points dunnav-patrol walks, and the ones centred in front of and
// beyond a narrow opening carry NavPoint.Doorway (a 1 yd reach, no corner cut: the character
// lines up and goes through straight). The VTank .nav format has no field for it, and the format
// stays as it is (VTank and other tools read these files), so a route run from its file lost the
// flag and cut doorway corners as routes did before the doorway work.
//
// So NavProfiles\.rynthai-routes.json lists the routes the route maker wrote: the file name, its
// SHA-256 when it was saved, the dungeon's landblock, its point count and the indexes of its
// doorway points. VTank ignores it (not a .nav). When a nav is loaded (any way: the Nav picker,
// /ra nav load, /vt nav load, a meta), RynthAi's tick (RynthAiPlugin.DunRoutes.cs, TickDunDoorways)
// looks it up: the same name and hash, and the character in that dungeon, and the flags go back
// on those points. A file edited since (another hash) gets none, and one log line says why. Navs
// the route maker didn't write are never touched. Saving a route again replaces its entry; an
// entry whose file is gone is dropped at the next save (RynthAi has no nav delete). A missing,
// locked or corrupt list only means no flags: loading never depends on it.
//
// Several clients share NavProfiles, so the read-merge-write holds the list open exclusively
// (a few tries if another client has it). Pump thread.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

internal static class DunRouteSidecar
{
    public const string FileName = ".rynthai-routes.json";
    public const int Version = 1;
    /// <summary>The list is ignored past this size (it holds a few hundred bytes a route).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private const int OpenTries = 6, OpenRetryMs = 15;

    /// <summary>One route the route maker wrote.</summary>
    internal sealed class Entry
    {
        public string File = "";
        public string Sha256 = "";
        public uint Landblock;
        public int Points;
        public List<int> Doorways = new();
    }

    internal static string PathIn(string folder) => Path.Combine(folder, FileName);

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>The entries in <paramref name="folder"/>'s list; empty when there is none. A list that
    /// can't be read or isn't one: empty, <paramref name="problem"/> says why. Never throws.</summary>
    internal static List<Entry> Read(string folder, out string? problem)
    {
        problem = null;
        try
        {
            string path = PathIn(folder);
            if (!File.Exists(path)) return new List<Entry>();
            byte[]? bytes = null;
            for (int i = 0; i < OpenTries && bytes == null; i++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > MaxBytes) { problem = $"{FileName} is over {MaxBytes / 1024 / 1024} MB: ignored"; return new List<Entry>(); }
                    bytes = new byte[fs.Length];
                    fs.ReadExactly(bytes);
                }
                catch (IOException) when (i + 1 < OpenTries) { Thread.Sleep(OpenRetryMs); }
            }
            return Parse(bytes ?? Array.Empty<byte>(), out problem);
        }
        catch (Exception ex)
        {
            problem = $"{FileName} couldn't be read ({ex.GetType().Name}: {ex.Message}): ignored";
            return new List<Entry>();
        }
    }

    /// <summary>The list's JSON: {"version":1,"routes":[{file, sha256, landblock, points, doorways}]}.
    /// An entry with a wrong field is skipped; anything else wrong: empty, with the reason.</summary>
    internal static List<Entry> Parse(byte[] bytes, out string? problem)
    {
        problem = null;
        var list = new List<Entry>();
        if (bytes.Length == 0) return list;
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
            {
                problem = $"{FileName} isn't RynthAi's route list: ignored";
                return list;
            }
            int skipped = 0;
            foreach (var r in routes.EnumerateArray())
            {
                if (TryEntry(r, out var e)) list.Add(e);
                else skipped++;
            }
            if (skipped > 0) problem = $"{FileName}: {skipped} entry(ies) not understood, skipped";
        }
        catch (Exception ex)
        {
            problem = $"{FileName} is corrupt ({ex.Message}): ignored";
            list.Clear();
        }
        return list;
    }

    private static bool TryEntry(JsonElement r, out Entry e)
    {
        e = new Entry();
        if (r.ValueKind != JsonValueKind.Object) return false;
        if (!r.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String) return false;
        if (!r.TryGetProperty("sha256", out var h) || h.ValueKind != JsonValueKind.String) return false;
        if (!r.TryGetProperty("landblock", out var lb) || lb.ValueKind != JsonValueKind.String
            || !uint.TryParse(lb.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out e.Landblock) || e.Landblock > 0xFFFF) return false;
        if (!r.TryGetProperty("points", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out e.Points) || e.Points < 0) return false;
        if (!r.TryGetProperty("doorways", out var d) || d.ValueKind != JsonValueKind.Array) return false;
        foreach (var i in d.EnumerateArray())
        {
            if (i.ValueKind != JsonValueKind.Number || !i.TryGetInt32(out int k) || k < 0 || k >= e.Points) return false;
            e.Doorways.Add(k);
        }
        e.File = f.GetString() ?? "";
        e.Sha256 = (h.GetString() ?? "").ToLowerInvariant();
        return e.File.Length > 0 && e.Sha256.Length == 64;
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the route just written to <paramref name="folder"/>\<paramref name="fileName"/>
    /// (<paramref name="bytes"/>: the file as written): its entry replaces any of that name, and
    /// entries whose file is gone are dropped. False with the reason when the list couldn't be
    /// written (the route itself is saved; from its file it runs without doorway flags).
    /// </summary>
    internal static bool Record(string folder, string fileName, byte[] bytes, uint landblock, NavRouteParser route, out string? problem)
    {
        problem = null;
        var entry = new Entry
        {
            File = fileName,
            Sha256 = Hash(bytes),
            Landblock = landblock & 0xFFFF,
            Points = route.Points.Count,
            Doorways = Enumerable.Range(0, route.Points.Count).Where(i => route.Points[i].Doorway && NavRouteParser.IsPlainWaypoint(route.Points[i].Type)).ToList(),
        };
        string path = PathIn(folder);
        try
        {
            Directory.CreateDirectory(folder);
            FileStream? fs = null;
            for (int i = 0; i < OpenTries && fs == null; i++)
            {
                try { fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (i + 1 < OpenTries) { Thread.Sleep(OpenRetryMs); }
            }
            using (fs!)
            {
                var old = new byte[Math.Min(fs!.Length, MaxBytes)];
                fs.ReadExactly(old);
                var list = Parse(old, out string? readProblem);
                if (readProblem != null) problem = readProblem + " (written again)";
                list.RemoveAll(e => e.File.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                                 || !File.Exists(Path.Combine(folder, e.File)));
                list.Add(entry);
                list.Sort((a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
                byte[] json = Write(list);
                fs.SetLength(0);
                fs.Position = 0;
                fs.Write(json);
            }
            return true;
        }
        catch (Exception ex)
        {
            problem = $"{FileName} couldn't be written ({ex.GetType().Name}: {ex.Message})";
            return false;
        }
    }

    private static byte[] Write(List<Entry> list)
    {
        var buffer = new ArrayBufferWriter<byte>(256 + list.Count * 160);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("about", "RynthAi's dungeon route maker: the doorway points of the routes it saved here (/ra dunroute, /ra dunpatrol-save). Not a nav file.");
            w.WriteNumber("version", Version);
            w.WriteStartArray("routes");
            foreach (var e in list)
            {
                w.WriteStartObject();
                w.WriteString("file", e.File);
                w.WriteString("sha256", e.Sha256);
                w.WriteString("landblock", e.Landblock.ToString("X4", CultureInfo.InvariantCulture));
                w.WriteNumber("points", e.Points);
                w.WriteStartArray("doorways");
                foreach (int i in e.Doorways) w.WriteNumberValue(i);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // ── A loaded route ───────────────────────────────────────────────────────

    /// <summary>
    /// The entry for the route loaded from <paramref name="navPath"/> when the route maker wrote that
    /// file and it hasn't changed since (same name in the list's folder, same SHA-256, same point
    /// count); else null. <paramref name="log"/>: one line when it is in the list but doesn't match
    /// (edited), or the list couldn't be read; null when it simply isn't one of the route maker's.
    /// Never throws.
    /// </summary>
    internal static Entry? Match(string folder, string? navPath, NavRouteParser route, out string? log)
    {
        log = null;
        try
        {
            if (string.IsNullOrEmpty(navPath) || route.Points.Count == 0) return null;
            string full = Path.GetFullPath(navPath);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(PathIn(folder)) || !File.Exists(full)) return null;
            string name = Path.GetFileName(full);
            var entries = Read(folder, out string? problem);
            var e = entries.FirstOrDefault(x => x.File.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e == null)
            {
                if (problem != null) log = $"[RynthAi] DunRoute: {problem}; '{name}' runs without doorway points";
                return null;
            }
            byte[] bytes = File.ReadAllBytes(full);
            // The route in hand must be this file's (/ra dunnav puts a route of its own in and leaves the
            // last file's path): line for line what the file holds. Not this file: nothing to say.
            var lines = Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (!route.ToLines().SequenceEqual(lines)) return null;
            string hash = Hash(bytes);
            if (hash != e.Sha256)
            {
                log = $"[RynthAi] DunRoute: '{name}' was changed since the route maker saved it: its doorway points aren't restored (it runs with the ordinary point reach)";
                return null;
            }
            if (e.Points != route.Points.Count)
            {
                log = $"[RynthAi] DunRoute: '{name}' has {route.Points.Count} points, the route maker saved {e.Points}: its doorway points aren't restored";
                return null;
            }
            return e;
        }
        catch (Exception ex)
        {
            log = $"[RynthAi] DunRoute: doorway points of '{Path.GetFileName(navPath ?? "")}' not restored ({ex.GetType().Name}: {ex.Message})";
            return null;
        }
    }

    /// <summary>Puts the doorway flag back on the entry's points (plain waypoints only). How many.</summary>
    internal static int Apply(NavRouteParser route, Entry e)
    {
        int n = 0;
        foreach (int i in e.Doorways)
        {
            if (i < 0 || i >= route.Points.Count || !NavRouteParser.IsPlainWaypoint(route.Points[i].Type)) continue;
            route.Points[i].Doorway = true;
            n++;
        }
        return n;
    }
}
