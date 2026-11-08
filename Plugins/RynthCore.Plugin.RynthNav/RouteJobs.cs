using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthNav;

/// <summary>A route maker job's state, as Poll reports it.</summary>
internal enum RouteJobState { Queued, Working, Done, Failed }

/// <summary>
/// One request to the route maker (a /rnav command or the "RynthNav.Routes" interface's Request): what
/// was asked, where it is (queued, working, done, failed), a line saying so, and the result as JSON.
/// The job table changes it; everyone else reads it through the table.
/// </summary>
internal sealed class RouteJob
{
    public int Id;
    public RouteRequest Request = new();
    public RouteJobState State;
    public string Detail = "";
    /// <summary>The op's result (JSON), "null" until there is one.</summary>
    public string ResultJson = "null";
    public long CreatedMs, StartedMs, EndedMs;
    /// <summary>The go op: how many gotos had ended when its walk started (the tick's bookkeeping).</summary>
    public int Seq;
    public bool Finished => State is RouteJobState.Done or RouteJobState.Failed;
}

/// <summary>
/// The route maker's jobs: the last <see cref="Capacity"/> of them, oldest dropped first once
/// finished. A new job is refused while every one kept is still queued or working. Ids go up from
/// the first one given (the plugin starts them from the clock, so a reload doesn't reuse a caller's
/// old ids); an id not kept any more is unknown. Locked: the interface's Request and Poll and the
/// tick may all come at once. Host-free: the route tests drive it.
/// </summary>
internal sealed class RouteJobTable
{
    public const int Capacity = 32;

    private readonly object _lock = new();
    private readonly List<RouteJob> _jobs = new();
    private int _nextId;

    public RouteJobTable(int firstId) { _nextId = Math.Max(1, firstId); }

    public int Count { get { lock (_lock) return _jobs.Count; } }

    /// <summary>A new queued job for <paramref name="request"/>; null (and why) when all the room is taken by unfinished jobs.</summary>
    public RouteJob? Add(RouteRequest request, long nowMs, out string error)
    {
        error = "";
        lock (_lock)
        {
            if (_jobs.Count >= Capacity)
            {
                int old = _jobs.FindIndex(j => j.Finished);
                if (old < 0) { error = $"{Capacity} jobs are already waiting or working: poll them, then ask again"; return null; }
                _jobs.RemoveAt(old);
            }
            var job = new RouteJob { Id = _nextId, Request = request, State = RouteJobState.Queued, Detail = "queued", CreatedMs = nowMs };
            _nextId = _nextId == int.MaxValue ? 1 : _nextId + 1;
            _jobs.Add(job);
            return job;
        }
    }

    public RouteJob? Get(int id)
    {
        lock (_lock) return _jobs.Find(j => j.Id == id);
    }

    /// <summary>The queued jobs, oldest first.</summary>
    public List<RouteJob> Queued()
    {
        lock (_lock) return _jobs.FindAll(j => j.State == RouteJobState.Queued);
    }

    /// <summary>The working jobs, oldest first.</summary>
    public List<RouteJob> Working()
    {
        lock (_lock) return _jobs.FindAll(j => j.State == RouteJobState.Working);
    }

    public void Start(RouteJob job, string detail, long nowMs)
    {
        lock (_lock)
        {
            if (job.Finished) return;
            job.State = RouteJobState.Working;
            job.Detail = detail;
            job.StartedMs = nowMs;
        }
    }

    /// <summary>A working job's line (a walk's distance left); a result to show while it works.</summary>
    public void Update(RouteJob job, string detail, string? resultJson = null)
    {
        lock (_lock)
        {
            if (job.Finished) return;
            job.Detail = detail;
            if (resultJson != null) job.ResultJson = resultJson;
        }
    }

    /// <summary>The job is over: done (<paramref name="ok"/>) or failed, with its line and result. A finished job stays as it is.</summary>
    public void Finish(RouteJob job, bool ok, string detail, string? resultJson, long nowMs)
    {
        lock (_lock)
        {
            if (job.Finished) return;
            job.State = ok ? RouteJobState.Done : RouteJobState.Failed;
            job.Detail = detail;
            if (resultJson != null) job.ResultJson = resultJson;
            job.EndedMs = nowMs;
        }
    }

    /// <summary>
    /// How a go job's walk ended, from the goto's last line: "arrived" (FinishGoto's "arrived", a dungeon's
    /// "arrived inside X" / "went through X") and "stopped" (a stop: the stop op, /rnav stop, the panel)
    /// are done (true); anything else ("stopped: no progress...", "stopped: logged out") failed.
    /// </summary>
    public static bool GoOutcome(string status, out string outcome)
    {
        if (status.StartsWith("arrived", StringComparison.Ordinal) || status.StartsWith("went through", StringComparison.Ordinal)) { outcome = "arrived"; return true; }
        if (status == "stopped") { outcome = "stopped"; return true; }
        outcome = "failed";
        return false;
    }

    public static string StateName(RouteJobState s) => s switch
    {
        RouteJobState.Queued => "queued",
        RouteJobState.Working => "working",
        RouteJobState.Done => "done",
        _ => "failed",
    };

    /// <summary>Poll's answer: {id, op, state, detail, result}; for an id not kept, {id, state: "unknown", error}.</summary>
    public string PollJson(int id, out bool known)
    {
        lock (_lock)
        {
            RouteJob? j = _jobs.Find(x => x.Id == id);
            known = j != null;
            if (j == null)
                return RouteJson.Write(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", id);
                    w.WriteString("state", "unknown");
                    w.WriteString("error", $"no job {id}: only the last {Capacity} are kept, and a RynthNav reload forgets them");
                    w.WriteEndObject();
                });
            return JobJson(j);
        }
    }

    /// <summary>Request's answer for a job just queued (the same fields as Poll).</summary>
    public string Accepted(RouteJob job)
    {
        lock (_lock) return JobJson(job);
    }

    private static string JobJson(RouteJob j) => RouteJson.Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("id", j.Id);
        w.WriteString("op", RouteRequest.OpName(j.Request.Op));
        w.WriteString("state", StateName(j.State));
        w.WriteString("detail", j.Detail);
        w.WritePropertyName("result");
        w.WriteRawValue(j.ResultJson, skipInputValidation: false);
        w.WriteEndObject();
    });
}

/// <summary>Small helpers for writing the route maker's JSON (System.Text.Json's writer: no reflection, AOT-safe).</summary>
internal static class RouteJson
{
    /// <summary>One JSON value written by <paramref name="body"/>, as a string.</summary>
    public static string Write(Action<Utf8JsonWriter> body)
    {
        var buf = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buf))
            body(w);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>{"error": why, "usage": usage}: a refused request.</summary>
    public static string Error(string why, string usage) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("error", why);
        if (usage.Length > 0) w.WriteString("usage", usage);
        w.WriteEndObject();
    });

    /// <summary>A number rounded to <paramref name="digits"/>, or null when it isn't one (the writer refuses NaN).</summary>
    public static void Num(Utf8JsonWriter w, string name, double v, int digits)
    {
        if (double.IsFinite(v)) w.WriteNumber(name, Math.Round(v, digits));
        else w.WriteNull(name);
    }

    /// <summary>"name": {"ns": n, "ew": e} (4 decimals: 0.02 yd).</summary>
    public static void Place(Utf8JsonWriter w, string name, double ns, double ew)
    {
        w.WriteStartObject(name);
        Num(w, "ns", ns, 4);
        Num(w, "ew", ew, 4);
        w.WriteEndObject();
    }

    public static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (string s in values) w.WriteStringValue(s);
        w.WriteEndArray();
    }
}
