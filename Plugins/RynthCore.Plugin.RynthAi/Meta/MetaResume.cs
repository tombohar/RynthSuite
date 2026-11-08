// MetaResume.cs - The meta's runtime, carried over a hot reload (2026-11-07).
//
// Tom: "a hot reload setting where it remembers which meta state you're in and restarts in that state so
// Drakkon doesn't recall back every time" - and then: "there might be some variables set in default of
// other states". Until now a hot reload (RynthCore or RynthAi updated while playing) built a new
// MetaManager: LoadSettings put the state back to Default, every Default rule fired again (a meta that
// recalls in Default recalled), and every $variable, the call stack, the watchdog and an embedded route
// were gone. Restoring the state name alone would skip what Default set up, so the whole runtime goes over:
//
//   the state and whether the macro ran; seconds in the state (both clocks); which rules had fired (so the
//   state's one-shot rules don't fire again); the call stack; the watchdog; every $variable and dictionary;
//   the loaded route, its waypoint and whether nav was on.
//
// Saved at the plugin's Shutdown (a hot reload unloads it), restored at the next OnLoginComplete in the SAME
// client process for the SAME character, within 10 minutes, with the SAME meta (path and rules) loaded, and
// only when it's a carried-over session (RynthAiPlugin.IsCarriedOverSession). A real logout deletes it, so a
// real login always starts in Default as before. Setting: Resume Meta After Reload (on by default).
//
// One file per client and character in %LOCALAPPDATA%\RynthCore\rynthai-meta (TrackerSessionStore's folder
// and file rules). Stopwatches go over too (their elapsed time and whether they run): a variable kept
// without its stopwatch read 0 s for ever, and a map rule waiting for "stopwatchelapsedseconds[$MapSW] >= 30"
// never fired again (2026-11-07). Not kept: delayexec queues, pending chat lines (VTank drops them on reload
// too), a Linear route's walking direction (it starts forward). @pvars and &gvars are on disk already.
//
// Tests: RynthCore.RynthAiHostTests MetaResumeTests (a reload modelled as export, a new settings object and
// MetaManager, restore), and the file format and checks.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>The meta's runtime at a hot reload.</summary>
internal sealed class MetaResumeState
{
    public int ProcessId;
    public long ProcessStartUtcTicks;
    public uint CharacterId;
    public long SavedUtcTicks;

    /// <summary>The meta file and a signature of its rules: a different meta is never resumed.</summary>
    public string MetaPath = "";
    public string RulesSignature = "";

    public bool MacroRunning;
    public string State = "Default";
    /// <summary>The state was entered (or a reset asked for) but not applied yet: the restore enters it afresh.</summary>
    public bool EntryPending;
    public double SecondsInState;
    public double SecondsInStateP;
    /// <summary>The call stack, bottom first: the state a Return goes to, and the P seconds it had (null: none).</summary>
    public List<(string State, double? PSeconds)> CallStack = new();
    /// <summary>Indexes (in the meta's top-level rule list) of the rules that had fired since their state was entered.</summary>
    public List<int> FiredRules = new();

    public bool WatchdogActive;
    public string WatchdogState = "";
    public float WatchdogMeters;
    public double WatchdogSeconds;
    public double WatchdogSecondsLeft;

    public Dictionary<string, string> Variables = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> Dicts = new(StringComparer.Ordinal);
    public int NextDictId;
    /// <summary>Stopwatch handle ("SW:4") → its elapsed seconds and whether it runs.</summary>
    public Dictionary<string, (double Seconds, bool Running)> Stopwatches = new(StringComparer.Ordinal);
    public int NextStopwatchId;

    public string NavPath = "";
    public List<string> RouteLines = new();
    public int NavIndex;
    public bool NavEnabled;
}

internal static class MetaResumeStore
{
    public const string Header = "RynthAi meta resume v1";

    /// <summary>A save older than this is a different session.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static string DefaultDirectory() => RynthCore.Plugin.Shared.TrackerSessionStore.DirectoryFor("rynthai-meta");

    public static string FileName(int processId, uint characterId)
        => "meta-" + processId.ToString(Ic) + "-" + characterId.ToString("X8", Ic) + ".txt";

    /// <summary>
    /// A signature of the meta's rules (states, conditions, actions, their data and children, in order):
    /// a meta edited or swapped between save and restore has another, and isn't resumed. FNV-1a, so it is
    /// the same in every process.
    /// </summary>
    public static string Signature(IReadOnlyList<MetaRule>? rules)
    {
        if (rules == null) return "none";
        ulong h = 14695981039346656037UL;
        void Mix(string s)
        {
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            h ^= 0x1F; h *= 1099511628211UL;
        }
        void Rule(MetaRule r)
        {
            Mix(r.State ?? ""); Mix(((int)r.Condition).ToString(Ic)); Mix(r.ConditionData ?? "");
            Mix(((int)r.Action).ToString(Ic)); Mix(r.ActionData ?? ""); Mix(r.CallReturnState ?? "");
            Mix(r.Enabled ? "1" : "0");
            Mix("C" + (r.Children?.Count ?? 0).ToString(Ic));
            if (r.Children != null) foreach (MetaRule c in r.Children) Rule(c);
            Mix("A" + (r.ActionChildren?.Count ?? 0).ToString(Ic));
            if (r.ActionChildren != null) foreach (MetaRule c in r.ActionChildren) Rule(c);
        }
        foreach (MetaRule r in rules) Rule(r);
        return rules.Count.ToString(Ic) + ":" + h.ToString("X16", Ic);
    }

    // ── Format: "key=value" lines, values URL-escaped (variables may hold anything) ──

    public static string Serialize(MetaResumeState s)
    {
        var sb = new StringBuilder(1024);
        sb.Append(Header).Append('\n');
        Line(sb, "pid", s.ProcessId.ToString(Ic));
        Line(sb, "pstart", s.ProcessStartUtcTicks.ToString(Ic));
        Line(sb, "char", s.CharacterId.ToString(Ic));
        Line(sb, "saved", s.SavedUtcTicks.ToString(Ic));
        Line(sb, "meta", Esc(s.MetaPath));
        Line(sb, "sig", Esc(s.RulesSignature));
        Line(sb, "running", s.MacroRunning ? "1" : "0");
        Line(sb, "state", Esc(s.State));
        Line(sb, "entry", s.EntryPending ? "1" : "0");
        Line(sb, "secs", s.SecondsInState.ToString("R", Ic));
        Line(sb, "psecs", s.SecondsInStateP.ToString("R", Ic));
        foreach (var (state, p) in s.CallStack)
            Line(sb, "call", Esc(state) + "|" + (p is double d ? d.ToString("R", Ic) : ""));
        Line(sb, "fired", string.Join(",", s.FiredRules.ConvertAll(i => i.ToString(Ic))));
        Line(sb, "wd", (s.WatchdogActive ? "1" : "0") + "|" + Esc(s.WatchdogState) + "|" + s.WatchdogMeters.ToString("R", Ic)
            + "|" + s.WatchdogSeconds.ToString("R", Ic) + "|" + s.WatchdogSecondsLeft.ToString("R", Ic));
        foreach (var kv in s.Variables) Line(sb, "var", Esc(kv.Key) + "|" + Esc(kv.Value));
        foreach (var d in s.Dicts)
        {
            var parts = new List<string>(d.Value.Count);
            foreach (var kv in d.Value) parts.Add(Esc(kv.Key) + ":" + Esc(kv.Value));
            Line(sb, "dict", Esc(d.Key) + "|" + string.Join(",", parts));
        }
        Line(sb, "nextdict", s.NextDictId.ToString(Ic));
        foreach (var sw in s.Stopwatches)
            Line(sb, "sw", Esc(sw.Key) + "|" + sw.Value.Seconds.ToString("R", Ic) + "|" + (sw.Value.Running ? "1" : "0"));
        Line(sb, "nextsw", s.NextStopwatchId.ToString(Ic));
        Line(sb, "navpath", Esc(s.NavPath));
        foreach (string l in s.RouteLines) Line(sb, "route", Esc(l));
        Line(sb, "navindex", s.NavIndex.ToString(Ic));
        Line(sb, "navon", s.NavEnabled ? "1" : "0");
        Line(sb, "end", "1");   // a torn write never parses
        return sb.ToString();
    }

    /// <summary>Strict: a wrong header, a malformed line or no end marker = nothing (start in Default).</summary>
    public static bool TryParse(string? text, out MetaResumeState state)
    {
        state = new MetaResumeState();
        if (string.IsNullOrEmpty(text)) return false;
        string[] lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0] != Header) return false;
        var s = state;
        bool end = false;
        try
        {
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) return false;
                string key = line[..eq], v = line[(eq + 1)..];
                switch (key)
                {
                    case "pid": s.ProcessId = int.Parse(v, Ic); break;
                    case "pstart": s.ProcessStartUtcTicks = long.Parse(v, Ic); break;
                    case "char": s.CharacterId = uint.Parse(v, Ic); break;
                    case "saved": s.SavedUtcTicks = long.Parse(v, Ic); break;
                    case "meta": s.MetaPath = Un(v); break;
                    case "sig": s.RulesSignature = Un(v); break;
                    case "running": s.MacroRunning = v == "1"; break;
                    case "state": s.State = Un(v); break;
                    case "entry": s.EntryPending = v == "1"; break;
                    case "secs": s.SecondsInState = double.Parse(v, Ic); break;
                    case "psecs": s.SecondsInStateP = double.Parse(v, Ic); break;
                    case "call":
                    {
                        string[] p = v.Split('|');
                        if (p.Length != 2) return false;
                        s.CallStack.Add((Un(p[0]), p[1].Length == 0 ? null : double.Parse(p[1], Ic)));
                        break;
                    }
                    case "fired":
                        foreach (string n in v.Split(',', StringSplitOptions.RemoveEmptyEntries)) s.FiredRules.Add(int.Parse(n, Ic));
                        break;
                    case "wd":
                    {
                        string[] p = v.Split('|');
                        if (p.Length != 5) return false;
                        s.WatchdogActive = p[0] == "1";
                        s.WatchdogState = Un(p[1]);
                        s.WatchdogMeters = float.Parse(p[2], Ic);
                        s.WatchdogSeconds = double.Parse(p[3], Ic);
                        s.WatchdogSecondsLeft = double.Parse(p[4], Ic);
                        break;
                    }
                    case "var":
                    {
                        int bar = v.IndexOf('|');
                        if (bar < 0) return false;
                        s.Variables[Un(v[..bar])] = Un(v[(bar + 1)..]);
                        break;
                    }
                    case "dict":
                    {
                        int bar = v.IndexOf('|');
                        if (bar < 0) return false;
                        var d = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (string kv in v[(bar + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int c = kv.IndexOf(':');
                            if (c < 0) return false;
                            d[Un(kv[..c])] = Un(kv[(c + 1)..]);
                        }
                        s.Dicts[Un(v[..bar])] = d;
                        break;
                    }
                    case "nextdict": s.NextDictId = int.Parse(v, Ic); break;
                    case "sw":
                    {
                        string[] p = v.Split('|');
                        if (p.Length != 3) return false;
                        double secs = double.Parse(p[1], Ic);
                        if (!double.IsFinite(secs) || secs < 0) return false;
                        s.Stopwatches[Un(p[0])] = (secs, p[2] == "1");
                        break;
                    }
                    case "nextsw": s.NextStopwatchId = int.Parse(v, Ic); break;
                    case "navpath": s.NavPath = Un(v); break;
                    case "route": s.RouteLines.Add(Un(v)); break;
                    case "navindex": s.NavIndex = int.Parse(v, Ic); break;
                    case "navon": s.NavEnabled = v == "1"; break;
                    case "end": end = v == "1"; break;
                    default: break;   // a later version's key
                }
            }
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        if (!end || s.SavedUtcTicks < DateTime.MinValue.Ticks || s.SavedUtcTicks > DateTime.MaxValue.Ticks) return false;
        if (!double.IsFinite(s.SecondsInState) || !double.IsFinite(s.SecondsInStateP) || !double.IsFinite(s.WatchdogSecondsLeft)) return false;
        return true;
    }

    /// <summary>Why a save can't be resumed, or null when it can: the same client process and character,
    /// recent, and the same meta (path and rules) loaded now.</summary>
    public static string? WhyNot(MetaResumeState s, int processId, long processStartUtcTicks, uint characterId,
        string metaPath, string rulesSignature, DateTime nowUtc)
    {
        if (characterId == 0 || s.CharacterId != characterId) return "another character";
        if (s.ProcessId != processId || s.ProcessStartUtcTicks != processStartUtcTicks) return "another client";
        TimeSpan age = nowUtc - new DateTime(s.SavedUtcTicks, DateTimeKind.Utc);
        if (age > MaxAge || age < TimeSpan.FromMinutes(-1)) return $"saved {age.TotalMinutes:0} min ago";
        if (!string.Equals(s.MetaPath ?? "", metaPath ?? "", StringComparison.OrdinalIgnoreCase)) return "another meta is loaded";
        if (s.RulesSignature != rulesSignature) return "the meta's rules changed";
        return null;
    }

    // ── Files (TrackerSessionStore's rules: temp file swapped in) ──

    public static void Save(string directory, MetaResumeState s)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName(s.ProcessId, s.CharacterId));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(s));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool TryLoad(string directory, int processId, uint characterId, out MetaResumeState state)
    {
        state = new MetaResumeState();
        string path = Path.Combine(directory, FileName(processId, characterId));
        return File.Exists(path) && TryParse(File.ReadAllText(path), out state);
    }

    public static void Delete(string directory, int processId, uint characterId)
    {
        string path = Path.Combine(directory, FileName(processId, characterId));
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Files from clients closed more than a day ago.</summary>
    public static void Cleanup(string directory, DateTime nowUtc)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.GetFiles(directory, "meta-*"))
        {
            try { if (nowUtc - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Line(StringBuilder sb, string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');

    private static string Esc(string? s) => Uri.EscapeDataString(s ?? "");

    private static string Un(string s) => Uri.UnescapeDataString(s);
}
