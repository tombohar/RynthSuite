using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LiteDB;
using RynthCore.CreatureSeed;
// LiteDB ships its own JsonSerializer; the seed is written with System.Text.Json (source-generated).
using StjSerializer = System.Text.Json.JsonSerializer;

namespace RynthCore.MonsterEditor;

/// <summary>
/// Converts UtilityBelt's <c>mob_damage_insights.ldb</c> (LiteDB 5) into the RynthAi
/// damage-type seed (<see cref="UbMobSeedFile"/>). Runs here rather than in the plugin
/// because LiteDB is reflection-heavy and the RynthAi plugin is NativeAOT.
/// <para>
/// UB rows record damage players dealt TO each monster, per damage type. The
/// per-character databases are pushed into the shared server database every 12 hours,
/// so they are mostly a subset of it: for each monster the single row with the most hits
/// wins (summing would double-count).
/// </para>
/// </summary>
internal static class UbMobSeedImport
{
    /// <summary>Hits a damage type needs before it is ranked.</summary>
    public const int DefaultMinHitsPerType = 20;

    private const string DbFileName = "mob_damage_insights.ldb";
    private const string WcidCollection = "mob_damage_agg";
    private const string MagCollection = "mob_mag_name_agg";

    /// <summary>UB damage-type flags (ACE <c>DamageType</c>) mapped to RynthAi element names.
    /// Normal (0) and combined flags are not castable choices and are skipped.</summary>
    private static readonly Dictionary<string, string> FlagToElement = new()
    {
        ["1"] = "Slash",
        ["2"] = "Pierce",
        ["4"] = "Bludgeon",
        ["8"] = "Cold",
        ["16"] = "Fire",
        ["32"] = "Acid",
        ["64"] = "Lightning",
        ["1024"] = "Nether",
    };

    /// <summary>Outcome of an import run.</summary>
    public sealed record Result(bool Ok, string Message, UbMobSeedFile? Seed);

    /// <summary>UB's per-world folder for InfiniteLeaftide (holds the shared server database).</summary>
    public static string DefaultServerFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Decal Plugins", "UtilityBelt", "InfiniteLeaftide");

    /// <summary>Where the plugin reads the seed: <c>&lt;SuiteDir&gt;\RynthAi\CreatureData\ub-mob-seed.json</c>.</summary>
    public static string DefaultOutputPath => Path.Combine(
        RynthCore.Install.RynthInstallPaths.RynthAiDir, "CreatureData", UbMobSeedFile.FileName);

    /// <summary>
    /// Lists the shared server database (first) and every per-character database
    /// one level below it.
    /// </summary>
    public static List<string> FindDatabases(string serverFolder)
    {
        var list = new List<string>();
        if (!Directory.Exists(serverFolder)) return list;

        string shared = Path.Combine(serverFolder, DbFileName);
        if (File.Exists(shared)) list.Add(shared);

        foreach (string dir in Directory.GetDirectories(serverFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string perChar = Path.Combine(dir, DbFileName);
            if (File.Exists(perChar)) list.Add(perChar);
        }
        return list;
    }

    /// <summary>
    /// Reads every database under <paramref name="serverFolder"/>, ranks damage types per
    /// monster and writes the seed to <paramref name="outputPath"/> atomically.
    /// </summary>
    public static Result Run(string serverFolder, string outputPath, int minHitsPerType = DefaultMinHitsPerType)
    {
        List<string> dbs = FindDatabases(serverFolder);
        if (dbs.Count == 0)
            return new Result(false, $"No {DbFileName} found in {serverFolder}.", null);

        // Best row per wcid / per normalized Mag-Tools name, across all sources.
        var bestWcid = new Dictionary<uint, BsonDocument>();
        var bestMag = new Dictionary<string, BsonDocument>(StringComparer.OrdinalIgnoreCase);
        var used = new List<string>();
        var errors = new List<string>();

        foreach (string db in dbs)
        {
            try
            {
                ReadDatabase(db, bestWcid, bestMag);
                used.Add(db);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(Path.GetDirectoryName(db))}: {ex.Message}");
            }
        }
        if (used.Count == 0)
            return new Result(false, "Could not read any UB database: " + string.Join("; ", errors), null);

        var seed = new UbMobSeedFile
        {
            GeneratedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            MinHitsPerType = minHitsPerType,
            Sources = used,
            World = MostCommonWorld(bestWcid.Values),
        };

        // Live UB rows keyed by wcid.
        var wcidNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (wcid, doc) in bestWcid.OrderBy(kv => kv.Key))
        {
            string name = Str(doc, "LastSeenName");
            if (name.Length > 0) wcidNames.Add(name);

            var elements = RankElements(doc, minHitsPerType, hasPeaks: true);
            if (elements.Count < 2) continue; // one type alone says nothing about weakness

            seed.Entries.Add(new UbMobSeedEntry
            {
                Wcid = wcid,
                Name = name,
                Source = "ub",
                TotalHits = Num(doc, "TotalHits"),
                Elements = elements,
            });
        }

        // Mag-Tools name-only rows, skipped when a wcid row already covers the name.
        foreach (var doc in bestMag.Values)
        {
            string name = Str(doc, "LastSeenDisplayName");
            if (name.Length == 0) name = Str(doc, "NormalizedMobName");
            if (name.Length == 0 || wcidNames.Contains(name)) continue;

            var elements = RankElements(doc, minHitsPerType, hasPeaks: false);
            if (elements.Count < 2) continue;

            seed.Entries.Add(new UbMobSeedEntry
            {
                Wcid = 0,
                Name = name,
                Source = "magtools",
                TotalHits = elements.Sum(e => (long)e.Hits),
                Elements = elements,
            });
        }

        WriteAtomic(outputPath, StjSerializer.Serialize(seed, EditorJsonContext.Default.UbMobSeedFile));

        int ub = seed.Entries.Count(e => e.Source == "ub");
        int mag = seed.Entries.Count - ub;
        string msg = $"UB seed written: {ub} monster(s) by wcid + {mag} by name (Mag-Tools) from {used.Count} database(s) → {outputPath}";
        if (errors.Count > 0) msg += $"  Skipped: {string.Join("; ", errors)}";
        return new Result(true, msg, seed);
    }

    /// <summary>
    /// Opens a snapshot copy of <paramref name="dbPath"/> (UB may have the live file open)
    /// and folds its rows into the best-row maps.
    /// </summary>
    private static void ReadDatabase(string dbPath, Dictionary<uint, BsonDocument> bestWcid,
        Dictionary<string, BsonDocument> bestMag)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RynthUbSeed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string copy = Path.Combine(tempDir, DbFileName);
            CopyShared(dbPath, copy);
            // LiteDB 5 keeps uncommitted pages in "<name>-log.ldb"; copy it so recent hits are included.
            string log = Path.Combine(Path.GetDirectoryName(dbPath)!, Path.GetFileNameWithoutExtension(dbPath) + "-log.ldb");
            if (File.Exists(log))
                CopyShared(log, Path.Combine(tempDir, Path.GetFileNameWithoutExtension(DbFileName) + "-log.ldb"));

            // Writable connection on the private copy so LiteDB can replay the log; the original is untouched.
            using var db = new LiteDatabase(new ConnectionString { Filename = copy, Connection = ConnectionType.Direct });

            if (db.CollectionExists(WcidCollection))
            {
                foreach (var doc in db.GetCollection(WcidCollection).FindAll())
                {
                    long wcid = Num(doc, "Wcid");
                    // wcid 1 is the player weenie (self/PvP lines), not a monster.
                    if (wcid <= 1 || wcid > uint.MaxValue) continue;
                    KeepBest(bestWcid, (uint)wcid, doc, Num(doc, "TotalHits"));
                }
            }

            if (db.CollectionExists(MagCollection))
            {
                foreach (var doc in db.GetCollection(MagCollection).FindAll())
                {
                    string key = Str(doc, "NormalizedMobName");
                    if (key.Length == 0) continue;
                    KeepBest(bestMag, key, doc, SumValues(doc, "HitsByType"));
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>Keeps the row with the most hits per key (sources overlap; see class remarks).</summary>
    private static void KeepBest<TKey>(Dictionary<TKey, BsonDocument> map, TKey key, BsonDocument doc, long hits)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var existing) || hits > HitsOf(existing))
            map[key] = doc;
    }

    /// <summary>Total hits of a row in either collection's shape.</summary>
    private static long HitsOf(BsonDocument doc)
    {
        long total = Num(doc, "TotalHits");
        return total > 0 ? total : SumValues(doc, "HitsByType");
    }

    /// <summary>
    /// Ranks the castable damage types with at least <paramref name="minHits"/> hits.
    /// Score blends average damage (vs the best average) with UB's own effectiveness
    /// measure, peak hit vs the best peak, when peaks are recorded.
    /// </summary>
    private static List<UbMobSeedElement> RankElements(BsonDocument doc, int minHits, bool hasPeaks)
    {
        var hits = Doc(doc, "HitsByType");
        var sums = Doc(doc, "SumDamageByType");
        var peaks = hasPeaks ? Doc(doc, "MaxDamageByType") : null;
        if (hits == null || sums == null) return new List<UbMobSeedElement>();

        var list = new List<UbMobSeedElement>();
        foreach (string flag in hits.Keys)
        {
            if (!FlagToElement.TryGetValue(flag, out string? element)) continue;
            int n = (int)Math.Min(int.MaxValue, Num(hits, flag));
            if (n < minHits || !sums.ContainsKey(flag)) continue;

            list.Add(new UbMobSeedElement
            {
                Element = element,
                Hits = n,
                AvgDamage = Math.Round(Num(sums, flag) / (double)n, 2),
                MaxHit = peaks != null && peaks.ContainsKey(flag) ? Num(peaks, flag) : 0,
            });
        }
        if (list.Count == 0) return list;

        double bestAvg = list.Max(e => e.AvgDamage);
        long bestPeak = list.Max(e => e.MaxHit);
        foreach (var e in list)
        {
            double avgRatio = bestAvg > 0 ? e.AvgDamage / bestAvg : 0;
            double score = bestPeak > 0
                ? 0.5 * avgRatio + 0.5 * (e.MaxHit / (double)bestPeak)
                : avgRatio;
            e.Score = Math.Round(score, 4);
        }

        return list.OrderByDescending(e => e.Score).ThenByDescending(e => e.Hits).ToList();
    }

    /// <summary>Most frequent <c>WorldName</c> among the rows (empty when none recorded).</summary>
    private static string MostCommonWorld(IEnumerable<BsonDocument> rows) =>
        rows.Select(r => Str(r, "WorldName"))
            .Where(w => w.Length > 0)
            .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? string.Empty;

    /// <summary>Copies a file another process may hold open for writing.</summary>
    private static void CopyShared(string source, string dest)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    /// <summary>Writes via a temp file so the plugin never reads a half-written seed.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    // ── BSON helpers (schema-tolerant: UB may store numbers as Int32, Int64 or Double) ──

    private static BsonDocument? Doc(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var v) && v.IsDocument ? v.AsDocument : null;

    private static string Str(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var v) && v.IsString ? v.AsString.Trim() : string.Empty;

    private static long Num(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var v) && v.IsNumber ? Convert.ToInt64(v.RawValue, CultureInfo.InvariantCulture) : 0;

    private static long SumValues(BsonDocument doc, string field)
    {
        var d = Doc(doc, field);
        if (d == null) return 0;
        long total = 0;
        foreach (string k in d.Keys) total += Num(d, k);
        return total;
    }
}
