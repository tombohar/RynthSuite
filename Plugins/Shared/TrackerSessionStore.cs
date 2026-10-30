using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Plugin.Shared;

// Keeps a hunting session alive across a plugin reload (engine hot reload, RynthSuite
// update: plugins swap in place and the new copy starts from zero). The plugin saves its
// session to a small file every ~30 s and on Shutdown; the next copy picks it up in
// OnLoginComplete when it is the same client process, the same character and the save is
// recent. A real logout deletes the file, so the next login is a new session.
//
// Shared (2026-10-11): RynthTracker and RynthAi's Mini Remote both keep their session through
// SessionStatsTracker, each in its own folder (DirectoryFor), so the two never read each other's file.
//
// Dependency-free on purpose (no plugin SDK types): Tools\RynthCore.TrackerSessionTests
// compiles this file directly. Plain "key=value" lines, no System.Text.Json (plugin types
// and STJ leak across reloads).

/// <summary>How much a property went up since the last read. A drop (spending, a server-side
/// reset of a session total) only moves the baseline; the first read is the baseline.</summary>
internal struct TrackerRise
{
    private long _last;
    private bool _seen;

    public long Take(long value)
    {
        long up = _seen && value > _last ? value - _last : 0;
        _last = value;
        _seen = true;
        return up;
    }

    public void Clear() => _seen = false;

    /// <summary>The last value read, or null before the first read.</summary>
    public readonly long? Baseline => _seen ? _last : null;

    /// <summary>Puts back a saved baseline: the next read counts everything above it,
    /// including what was earned while the plugin was unloaded. Null = no baseline yet.</summary>
    public void Restore(long? baseline)
    {
        _seen = baseline.HasValue;
        _last = baseline ?? 0;
    }
}

/// <summary>Everything a session needs to carry on after a reload. Times are UTC ticks.</summary>
internal sealed class TrackerSessionState
{
    public int ProcessId;
    // Process ids get reused once a client closes; the start time tells two apart.
    public long ProcessStartUtcTicks;
    public uint CharacterId;
    public long SavedUtcTicks;
    public long SessionStartUtcTicks;

    public long XpTotal;
    public long LumTotal;
    public long RadTotal;
    public int KillsTotal;
    public int DeathsTotal;

    public long KillXp;
    public int XpKills;
    public int OpenKills;
    public long KillWindowUntilUtcTicks;
    public bool MaxLevel;

    // Rise baselines, saved together with the totals so the first read after a reload
    // counts exactly what came in since this save (null = not read yet).
    public long? XpBase;
    public long? LumBase;
    public long? RadBase;
    public long? BankedBase;
    public long? DrawnBase;

    // Added 2026-10-11 (SessionStats). Optional in the file: a save from before them loads
    // with these at their defaults instead of being thrown away.
    /// <summary>ACECustom bank luminance (PropertyInt64 9005) baseline.</summary>
    public long? BankLumBase;
    /// <summary>Own kill / quest luminance from "You've banked N Luminance" lines.</summary>
    public long LumOwn;
    /// <summary>Bank luminance income (deposits and transfers taken out) since the bank was first read.</summary>
    public long BankIncome;
    /// <summary>Own luminance chat seen since the bank was first read (pass-up = BankIncome - this).</summary>
    public long OwnSinceBank;
    /// <summary>Luminance of the last five minutes, oldest first: (UTC ticks, amount).</summary>
    public List<(long AtTicks, long Amount)> RecentLum = new();
}

internal static class TrackerSessionStore
{
    public const string Header = "RynthTracker session v1";

    /// <summary>A save older than this is a different session (or a dead client).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Files untouched for this long are left over from closed clients.</summary>
    public static readonly TimeSpan PurgeAge = TimeSpan.FromDays(1);

    private const string FilePrefix = "session-";
    private const string FileSuffix = ".txt";

    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    /// <summary>One file per client process and character: another client, or another
    /// character in this one, never reads it.</summary>
    public static string FileName(int processId, uint characterId)
        => FilePrefix + processId.ToString(Ic) + "-" + characterId.ToString("X8", Ic) + FileSuffix;

    /// <summary>RynthTracker's folder.</summary>
    public static string DefaultDirectory() => DirectoryFor("tracker");

    /// <summary>%LOCALAPPDATA%\RynthCore\&lt;name&gt;: one folder per plugin that keeps a session.</summary>
    public static string DirectoryFor(string name)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RynthCore", name);

    // ── Format ───────────────────────────────────────────────────────────────

    public static string Serialize(TrackerSessionState s)
    {
        var sb = new StringBuilder(512);
        sb.Append(Header).Append('\n');
        Line(sb, "pid", s.ProcessId.ToString(Ic));
        Line(sb, "pstart", s.ProcessStartUtcTicks.ToString(Ic));
        Line(sb, "char", s.CharacterId.ToString(Ic));
        Line(sb, "saved", s.SavedUtcTicks.ToString(Ic));
        Line(sb, "start", s.SessionStartUtcTicks.ToString(Ic));
        Line(sb, "xp", s.XpTotal.ToString(Ic));
        Line(sb, "lum", s.LumTotal.ToString(Ic));
        Line(sb, "rad", s.RadTotal.ToString(Ic));
        Line(sb, "kills", s.KillsTotal.ToString(Ic));
        Line(sb, "deaths", s.DeathsTotal.ToString(Ic));
        Line(sb, "killxp", s.KillXp.ToString(Ic));
        Line(sb, "xpkills", s.XpKills.ToString(Ic));
        Line(sb, "openkills", s.OpenKills.ToString(Ic));
        Line(sb, "killuntil", s.KillWindowUntilUtcTicks.ToString(Ic));
        Line(sb, "maxlevel", s.MaxLevel ? "1" : "0");
        Line(sb, "xpbase", Opt(s.XpBase));
        Line(sb, "lumbase", Opt(s.LumBase));
        Line(sb, "radbase", Opt(s.RadBase));
        Line(sb, "bankedbase", Opt(s.BankedBase));
        Line(sb, "drawnbase", Opt(s.DrawnBase));
        Line(sb, "bankbase", Opt(s.BankLumBase));
        Line(sb, "lumown", s.LumOwn.ToString(Ic));
        Line(sb, "bankincome", s.BankIncome.ToString(Ic));
        Line(sb, "ownsincebank", s.OwnSinceBank.ToString(Ic));
        Line(sb, "recentlum", Recent(s.RecentLum));
        Line(sb, "end", "1");   // a torn write never parses
        return sb.ToString();
    }

    /// <summary>Strict: wrong header, a missing or malformed field, or no end marker = no
    /// state (start fresh). Unknown keys are ignored so a later version can add fields.</summary>
    public static bool TryParse(string? text, out TrackerSessionState state)
    {
        state = new TrackerSessionState();
        if (string.IsNullOrEmpty(text)) return false;

        string[] lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0] != Header) return false;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            map[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        if (!map.TryGetValue("end", out string? end) || end != "1") return false;

        var s = state;
        bool ok =
            Int(map, "pid", out s.ProcessId) &&
            Long(map, "pstart", out s.ProcessStartUtcTicks) &&
            UInt(map, "char", out s.CharacterId) &&
            Long(map, "saved", out s.SavedUtcTicks) &&
            Long(map, "start", out s.SessionStartUtcTicks) &&
            Long(map, "xp", out s.XpTotal) &&
            Long(map, "lum", out s.LumTotal) &&
            Long(map, "rad", out s.RadTotal) &&
            Int(map, "kills", out s.KillsTotal) &&
            Int(map, "deaths", out s.DeathsTotal) &&
            Long(map, "killxp", out s.KillXp) &&
            Int(map, "xpkills", out s.XpKills) &&
            Int(map, "openkills", out s.OpenKills) &&
            Long(map, "killuntil", out s.KillWindowUntilUtcTicks) &&
            Bool(map, "maxlevel", out s.MaxLevel) &&
            OptLong(map, "xpbase", out s.XpBase) &&
            OptLong(map, "lumbase", out s.LumBase) &&
            OptLong(map, "radbase", out s.RadBase) &&
            OptLong(map, "bankedbase", out s.BankedBase) &&
            OptLong(map, "drawnbase", out s.DrawnBase) &&
            // The 2026-10-11 keys may be absent (older save), never malformed.
            (!map.ContainsKey("bankbase") || OptLong(map, "bankbase", out s.BankLumBase)) &&
            (!map.ContainsKey("lumown") || Long(map, "lumown", out s.LumOwn)) &&
            (!map.ContainsKey("bankincome") || Long(map, "bankincome", out s.BankIncome)) &&
            (!map.ContainsKey("ownsincebank") || Long(map, "ownsincebank", out s.OwnSinceBank)) &&
            (!map.ContainsKey("recentlum") || ParseRecent(map["recentlum"], s.RecentLum));
        if (!ok) return false;

        // Ticks outside DateTime's range would throw when the plugin builds a DateTime.
        if (!ValidTicks(s.SavedUtcTicks) || !ValidTicks(s.SessionStartUtcTicks) || !ValidTicks(s.KillWindowUntilUtcTicks))
            return false;
        return true;
    }

    /// <summary>Carry the saved session on only for the same client process (id and start
    /// time) and character, and only when the save is recent. A save "from the future" (clock moved back) is
    /// treated like a stale one; a minute of slack covers small clock adjustments.</summary>
    public static bool ShouldRestore(TrackerSessionState s, int processId, long processStartUtcTicks, uint characterId, DateTime nowUtc)
    {
        if (characterId == 0) return false;
        if (s.ProcessId != processId || s.ProcessStartUtcTicks != processStartUtcTicks) return false;
        if (s.CharacterId != characterId) return false;
        TimeSpan age = nowUtc - new DateTime(s.SavedUtcTicks, DateTimeKind.Utc);
        if (age > MaxAge || age < TimeSpan.FromMinutes(-1)) return false;
        if (s.SessionStartUtcTicks > s.SavedUtcTicks) return false;
        return true;
    }

    // ── Files ────────────────────────────────────────────────────────────────

    /// <summary>Writes to a temp file and swaps it in, so a crash mid-write leaves the last
    /// good save (or nothing), never a half file.</summary>
    public static void Save(string directory, TrackerSessionState s)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName(s.ProcessId, s.CharacterId));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(s));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool TryLoad(string directory, int processId, uint characterId, out TrackerSessionState state)
    {
        state = new TrackerSessionState();
        string path = Path.Combine(directory, FileName(processId, characterId));
        if (!File.Exists(path)) return false;
        return TryParse(File.ReadAllText(path), out state);
    }

    public static void Delete(string directory, int processId, uint characterId)
    {
        string path = Path.Combine(directory, FileName(processId, characterId));
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Deletes this process's saves for any character (one character per client
    /// at a time, so they're all over) and any file left untouched past <see cref="PurgeAge"/>
    /// by a client that has since closed. Returns how many files went.</summary>
    public static int Cleanup(string directory, int processId, DateTime nowUtc)
    {
        if (!Directory.Exists(directory)) return 0;
        string mine = FilePrefix + processId.ToString(Ic) + "-";
        int removed = 0;
        foreach (string path in Directory.GetFiles(directory, FilePrefix + "*"))
        {
            string name = Path.GetFileName(path);
            bool remove = name.StartsWith(mine, StringComparison.Ordinal)
                       || nowUtc - File.GetLastWriteTimeUtc(path) > PurgeAge;
            if (!remove) continue;
            try { File.Delete(path); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Line(StringBuilder sb, string key, string value)
        => sb.Append(key).Append('=').Append(value).Append('\n');

    private static string Opt(long? v) => v.HasValue ? v.Value.ToString(Ic) : "-";

    // "ticks:amount;ticks:amount", or "-" for none.
    private static string Recent(List<(long AtTicks, long Amount)> entries)
    {
        if (entries.Count == 0) return "-";
        var sb = new StringBuilder(entries.Count * 28);
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(entries[i].AtTicks.ToString(Ic)).Append(':').Append(entries[i].Amount.ToString(Ic));
        }
        return sb.ToString();
    }

    private static bool ParseRecent(string text, List<(long AtTicks, long Amount)> into)
    {
        into.Clear();
        if (text == "-") return true;
        foreach (string part in text.Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) return false;
            if (!long.TryParse(part.AsSpan(0, colon), NumberStyles.None, Ic, out long at) || !ValidTicks(at)) return false;
            if (!long.TryParse(part.AsSpan(colon + 1), NumberStyles.AllowLeadingSign, Ic, out long amount)) return false;
            into.Add((at, amount));
        }
        return true;
    }

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

    private static bool Bool(Dictionary<string, string> m, string k, out bool v)
    {
        v = false;
        if (!m.TryGetValue(k, out string? s)) return false;
        if (s == "1") { v = true; return true; }
        return s == "0";
    }

    private static bool OptLong(Dictionary<string, string> m, string k, out long? v)
    {
        v = null;
        if (!m.TryGetValue(k, out string? s)) return false;
        if (s == "-") return true;
        if (!long.TryParse(s, NumberStyles.AllowLeadingSign, Ic, out long x)) return false;
        v = x;
        return true;
    }
}
