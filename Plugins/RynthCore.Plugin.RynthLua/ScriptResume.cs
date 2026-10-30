using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// Resume after a hot reload (design doc §8 decision 3). Shutdown writes resume.json with the
/// scripts the player started by hand (from a file, no autostart); the next Initialize keeps
/// that list only when the file comes from this same process and is under 60 s old, which
/// means a reload, not a new client; OnLoginComplete starts them after autostart.
/// Utf8JsonWriter / JsonDocument only (no serialized types), every file step in try/catch.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private const long ResumeMaxAgeMs = 60_000;
    /// <summary>How long after Initialize the list waits for a login. A reload at character select
    /// doesn't start the scripts on whichever character logs in minutes later.</summary>
    private const long ResumeWaitMs = 120_000;
    private const int ResumeMaxScripts = 100;

    private List<string>? _resumePending;
    private long _resumePendingAt;
    /// <summary>Offline tests only: stands in for DataFolder\resume.json when set.</summary>
    private string? ResumeFileOverride { get; set; }

    private string ResumeFile => ResumeFileOverride ?? Path.Combine(DataFolder, "resume.json");

    // ── Shutdown ────────────────────────────────────────────────────────────

    /// <summary>Before StopAll: the running scripts to bring back after the reload.</summary>
    private List<string> CaptureResume()
        => ResumeCandidates(_scripts.Values, name =>
        {
            try { return AutostartOf(name); }
            catch { return "none"; }   // login skips anything already running, so no double start
        });

    /// <summary>Running, from a file (not the editor or /lua exec), and not autostart (those restart on their own).</summary>
    internal static List<string> ResumeCandidates(IEnumerable<ScriptContext> scripts, Func<string, string> autostartOf)
    {
        var list = new List<string>();
        foreach (ScriptContext ctx in scripts)
        {
            if (!ctx.Host.Loaded || ctx.Transient) continue;
            if (autostartOf(ctx.Name) != "none") continue;
            if (list.Exists(n => n.Equals(ctx.Name, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(ctx.Name);
            if (list.Count >= ResumeMaxScripts) break;
        }
        return list;
    }

    /// <summary>{"pid": n, "savedAtUtcMs": n, "scripts": [...]}, written even when empty so an older file can't bring scripts back.</summary>
    internal static bool WriteResumeFile(string path, IReadOnlyList<string> names, int pid, long savedAtUtcMs, Action<string> log)
    {
        string tmp = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("pid", pid);
                w.WriteNumber("savedAtUtcMs", savedAtUtcMs);
                w.WriteStartArray("scripts");
                foreach (string n in names) w.WriteStringValue(n);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log("[RynthLua] can't write resume.json: " + ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Reads and deletes the file. Returns the scripts when it is from process <paramref name="pid"/>
    /// and 0-60 s old, otherwise null (a new client, a stale or bad file, or no file).
    /// </summary>
    internal static List<string>? ReadResumeFile(string path, int pid, long nowUtcMs, Action<string> log)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch { return null; }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(bytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("pid", out JsonElement p) || p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out int filePid) || filePid != pid)
                return null;
            if (!root.TryGetProperty("savedAtUtcMs", out JsonElement t) || t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out long savedAt))
                return null;
            long age = nowUtcMs - savedAt;
            if (age < 0 || age > ResumeMaxAgeMs) return null;
            if (!root.TryGetProperty("scripts", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) continue;
                string n = (e.GetString() ?? string.Empty).Trim();
                if (n.Length == 0 || list.Exists(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(n);
                if (list.Count >= ResumeMaxScripts) break;
            }
            return list;
        }
        catch (Exception ex)
        {
            log("[RynthLua] ignoring a bad resume.json: " + ex.Message);
            return null;
        }
    }

    // ── Initialize ──────────────────────────────────────────────────────────

    private void LoadResume()
    {
        List<string>? list = ReadResumeFile(ResumeFile, Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), s => Log(s));
        if (list == null || list.Count == 0) return;
        _resumePending = list;
        _resumePendingAt = Environment.TickCount64;
        Log($"[RynthLua] resuming {list.Count} script(s) after a reload");
    }

    // ── OnLoginComplete (after RunAutostart) ────────────────────────────────

    private void ResumeScripts()
    {
        List<string>? list = _resumePending;
        _resumePending = null;
        if (list == null || list.Count == 0) return;
        if (Environment.TickCount64 - _resumePendingAt > ResumeWaitMs)
        {
            Log($"[RynthLua] not resuming {list.Count} script(s): the login came too long after the reload");
            return;
        }
        RunResume(list, name => _scripts.TryGetValue(name, out var c) && c.Host.Loaded, StartScript, s => Log(s));
    }

    /// <summary>Starts each script that isn't already running, each on its own. Returns how many started.</summary>
    internal static int RunResume(IReadOnlyList<string> names, Func<string, bool> isRunning, Func<string, string?> start, Action<string> log)
    {
        int started = 0;
        foreach (string name in names)
        {
            try
            {
                if (isRunning(name)) continue;   // autostart (or something else) already brought it back
                string? err = start(name);
                if (err == null) started++;
                log(err == null ? $"[RynthLua] Resumed {name}." : $"[RynthLua] Resume {name}: {err}");
            }
            catch (Exception ex)
            {
                log($"[RynthLua] Resume {name}: {ex.Message}");
            }
        }
        return started;
    }
}
