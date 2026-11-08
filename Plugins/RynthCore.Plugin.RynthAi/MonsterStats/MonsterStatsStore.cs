// ============================================================================
//  RynthAi - MonsterStats/MonsterStatsStore.cs
//  Keeps the monster stats (since login, or their last reset) across a plugin
//  reload (engine hot reload, RynthSuite update), by TrackerSessionStore's rules:
//  a small file saved every ~30 s, at a reset and on Shutdown, picked up by the
//  next copy only for the same client process (id and start time), the same
//  character and a recent save. A real logout deletes the file, so the next
//  login starts fresh. The Mini Remote session's Reset XP doesn't touch it.
//
//  %LOCALAPPDATA%\RynthCore\monster-stats\monsters-<pid>-<character>.txt, plain
//  "key=value" lines with one "m=" line per monster (tab-separated), and an end
//  marker so a torn write never loads. No System.Text.Json (plugin types and
//  STJ leak across reloads). Pure apart from the file calls.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

/// <summary>What a save holds. Times are UTC ticks.</summary>
internal sealed class MonsterStatsSave
{
    public int ProcessId;
    public long ProcessStartUtcTicks;
    public uint CharacterId;
    public long SavedUtcTicks;
    /// <summary>When these numbers started (the login, or their last reset).</summary>
    public long SinceUtcTicks;
    public List<MonsterStatsRow> Rows = new();
}

internal static class MonsterStatsStore
{
    public const string Header = "RynthAi monster stats v2";

    /// <summary>A save older than this is from a client gone (the session store's rule).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Files untouched for this long are left over from closed clients.</summary>
    public static readonly TimeSpan PurgeAge = TimeSpan.FromDays(1);

    private const string FilePrefix = "monsters-";
    private const string FileSuffix = ".txt";
    private const int Fields = 16;   // the name and 15 numbers
    // 2026-11-08: two more at the end, XP and kills with XP counted. The header stays v2: an older
    // copy reads the first 16 and leaves the rest, and a row from an older save has XP 0.
    private const int FieldsWithXp = 18;
    // Then two more (same day): the kill-to-corpse ms summed and how many (the death animation).
    private const int FieldsWithCorpse = 20;

    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    /// <summary>One file per client process and character.</summary>
    public static string FileName(int processId, uint characterId)
        => FilePrefix + processId.ToString(Ic) + "-" + characterId.ToString("X8", Ic) + FileSuffix;

    /// <summary>%LOCALAPPDATA%\RynthCore\monster-stats.</summary>
    public static string DefaultDirectory() => RynthCore.Plugin.Shared.TrackerSessionStore.DirectoryFor("monster-stats");

    // ── Format ───────────────────────────────────────────────────────────

    public static string Serialize(MonsterStatsSave s)
    {
        var sb = new StringBuilder(256 + s.Rows.Count * 96);
        sb.Append(Header).Append('\n');
        Line(sb, "pid", s.ProcessId.ToString(Ic));
        Line(sb, "pstart", s.ProcessStartUtcTicks.ToString(Ic));
        Line(sb, "char", s.CharacterId.ToString(Ic));
        Line(sb, "saved", s.SavedUtcTicks.ToString(Ic));
        Line(sb, "since", s.SinceUtcTicks.ToString(Ic));
        foreach (MonsterStatsRow r in s.Rows)
        {
            sb.Append("m=").Append(Clean(r.Name));
            foreach (long v in new long[]
                     {
                         r.Hits, r.Evaded, r.SpellsLanded, r.Resisted, r.NoEffect, r.Kills, r.DamageDealt,
                         r.HitsTaken, r.YouEvaded, r.SpellsTaken, r.YouResisted, r.NoEffectOnYou, r.DamageTaken,
                         r.FirstUtcTicks, r.LastUtcTicks, r.Xp, r.XpKills, r.CorpseMs, r.CorpseKills,
                     })
                sb.Append('\t').Append(v.ToString(Ic));
            sb.Append('\n');
        }
        Line(sb, "end", "1");   // a torn write never parses
        return sb.ToString();
    }

    /// <summary>Strict: wrong header, a missing or malformed field, or no end marker = nothing (start fresh).
    /// Unknown keys are ignored so a later version can add some.</summary>
    public static bool TryParse(string? text, out MonsterStatsSave save)
    {
        save = new MonsterStatsSave();
        if (string.IsNullOrEmpty(text)) return false;
        string[] lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0] != Header) return false;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<MonsterStatsRow>();
        bool end = false;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            string key = line.Substring(0, eq), value = line.Substring(eq + 1);
            if (key == "m")
            {
                if (!TryParseRow(value, out MonsterStatsRow? row)) return false;
                rows.Add(row!);
                continue;
            }
            if (key == "end") { end = value == "1"; continue; }
            map[key] = value;
        }
        if (!end) return false;

        var s = save;
        bool ok = Int(map, "pid", out s.ProcessId)
               && Long(map, "pstart", out s.ProcessStartUtcTicks)
               && UInt(map, "char", out s.CharacterId)
               && Long(map, "saved", out s.SavedUtcTicks)
               && Long(map, "since", out s.SinceUtcTicks);
        if (!ok || !ValidTicks(s.SavedUtcTicks) || !ValidTicks(s.SinceUtcTicks)) return false;
        s.Rows = rows;
        return true;
    }

    private static bool TryParseRow(string value, out MonsterStatsRow? row)
    {
        row = null;
        string[] f = value.Split('\t');
        if (f.Length < Fields || f[0].Trim().Length == 0) return false;
        var n = new long[Fields - 1];
        for (int i = 1; i < Fields; i++)
            if (!long.TryParse(f[i], NumberStyles.None, Ic, out n[i - 1])) return false;
        for (int i = 0; i < 13; i++)
            if (i != 6 && i != 12 && n[i] > int.MaxValue) return false;   // counts are ints; damage is long
        if (!ValidTicks(n[13]) || !ValidTicks(n[14])) return false;
        long xp = 0, xpKills = 0;
        if (f.Length >= FieldsWithXp)
        {
            if (!long.TryParse(f[16], NumberStyles.None, Ic, out xp) || !long.TryParse(f[17], NumberStyles.None, Ic, out xpKills)
                || xpKills > int.MaxValue) return false;
        }
        long corpseMs = 0, corpseKills = 0;
        if (f.Length >= FieldsWithCorpse)
        {
            if (!long.TryParse(f[18], NumberStyles.None, Ic, out corpseMs) || !long.TryParse(f[19], NumberStyles.None, Ic, out corpseKills)
                || corpseKills > int.MaxValue) return false;
        }
        row = new MonsterStatsRow(f[0].Trim(),
            (int)n[0], (int)n[1], (int)n[2], (int)n[3], (int)n[4], (int)n[5], n[6],
            (int)n[7], (int)n[8], (int)n[9], (int)n[10], (int)n[11], n[12],
            n[13], n[14], xp, (int)xpKills, corpseMs, (int)corpseKills);
        return true;
    }

    /// <summary>
    /// Carry the saved stats on only for the same client process (id and start time) and character,
    /// and a recent save (a real logout deleted it, so a save that is there is a reload's). A save
    /// "from the future" (clock moved back) is treated like a stale one; a minute of slack covers
    /// small clock adjustments.
    /// </summary>
    public static bool ShouldRestore(MonsterStatsSave s, int processId, long processStartUtcTicks, uint characterId, DateTime nowUtc)
    {
        if (characterId == 0) return false;
        if (s.ProcessId != processId || s.ProcessStartUtcTicks != processStartUtcTicks) return false;
        if (s.CharacterId != characterId) return false;
        if (s.SinceUtcTicks > s.SavedUtcTicks) return false;
        TimeSpan age = nowUtc - new DateTime(s.SavedUtcTicks, DateTimeKind.Utc);
        return age <= MaxAge && age >= TimeSpan.FromMinutes(-1);
    }

    // ── Files ────────────────────────────────────────────────────────────

    /// <summary>Writes to a temp file and swaps it in, so a crash mid-write leaves the last good save.</summary>
    public static void Save(string directory, MonsterStatsSave s)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName(s.ProcessId, s.CharacterId));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(s));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool TryLoad(string directory, int processId, uint characterId, out MonsterStatsSave save)
    {
        save = new MonsterStatsSave();
        string path = Path.Combine(directory, FileName(processId, characterId));
        if (!File.Exists(path)) return false;
        return TryParse(File.ReadAllText(path), out save);
    }

    public static void Delete(string directory, int processId, uint characterId)
    {
        string path = Path.Combine(directory, FileName(processId, characterId));
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Deletes this process's saves for any character and files left untouched past
    /// <see cref="PurgeAge"/> by clients that have closed. Returns how many went.</summary>
    public static int Cleanup(string directory, int processId, DateTime nowUtc)
    {
        if (!Directory.Exists(directory)) return 0;
        string mine = FilePrefix + processId.ToString(Ic) + "-";
        int removed = 0;
        foreach (string path in Directory.GetFiles(directory, FilePrefix + "*"))
        {
            string name = Path.GetFileName(path);
            bool remove = name.StartsWith(mine, StringComparison.Ordinal) || nowUtc - File.GetLastWriteTimeUtc(path) > PurgeAge;
            if (!remove) continue;
            try { File.Delete(path); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    // The client's start time, so a new client that gets a closed one's process id never picks up
    // its save. 0 if Windows won't say (both sides then match on the id alone).
    private static long? _processStartTicks;
    public static long ProcessStartTicks
    {
        get
        {
            if (_processStartTicks is long t) return t;
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                t = p.StartTime.ToUniversalTime().Ticks;
            }
            catch { t = 0; }
            _processStartTicks = t;
            return t;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void Line(StringBuilder sb, string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');

    /// <summary>A name with no tab or line break in it (AC names have none; be safe).</summary>
    private static string Clean(string name) => name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool ValidTicks(long t) => t >= DateTime.MinValue.Ticks && t <= DateTime.MaxValue.Ticks;

    private static bool Long(Dictionary<string, string> m, string k, out long v)
    {
        v = 0;
        return m.TryGetValue(k, out string? s) && long.TryParse(s, NumberStyles.AllowLeadingSign, Ic, out v);
    }

    private static bool Int(Dictionary<string, string> m, string k, out int v)
    {
        v = 0;
        return m.TryGetValue(k, out string? s) && int.TryParse(s, NumberStyles.AllowLeadingSign, Ic, out v);
    }

    private static bool UInt(Dictionary<string, string> m, string k, out uint v)
    {
        v = 0;
        return m.TryGetValue(k, out string? s) && uint.TryParse(s, NumberStyles.None, Ic, out v);
    }
}
