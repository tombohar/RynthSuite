// DungeonRouteJobs: the "RynthAi.Routes" interface's jobs (Shared/RynthAiRoutesApi.cs). Request
// queues one; RynthAi's tick works the oldest unfinished one (RynthAiPlugin.DunRoutes.cs,
// TickRouteJobs); Poll reads its state. The last MaxJobs are kept: a new job drops the oldest finished
// one, and with every kept job unfinished a request is refused. Ids count up from 1 and are never
// reused while this object lives (one per plugin instance: a reload forgets every job).
// Pump thread (the interface's rule); the lock only guards against a caller that breaks it.

using System;
using System.Collections.Generic;
using System.Text.Json;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

internal enum DunJobState { Queued, Working, Done, Failed }

internal sealed class DunRouteJob
{
    public uint Id;
    public DunRouteRequest Request = null!;
    public DunJobState State = DunJobState.Queued;
    /// <summary>One line: what it waits for, or the answer (the chat command's line).</summary>
    public string Detail = "queued";
    /// <summary>The result object (JSON) once done.</summary>
    public string? ResultJson;
    public long QueuedAt, StartedAt;

    public bool Finished => State is DunJobState.Done or DunJobState.Failed;
}

internal sealed class DunRouteJobs
{
    public const int MaxJobs = RynthAiRoutesApiV1.MaxJobs;
    /// <summary>A working job that still waits on the client after this long fails.</summary>
    public const long WaitLimitMs = 30_000;

    private readonly object _gate = new();
    private readonly List<DunRouteJob> _jobs = new();
    private uint _nextId = 1;

    /// <summary>Milliseconds clock; tests replace it.</summary>
    internal Func<long> Clock = static () => Environment.TickCount64;

    public int Count { get { lock (_gate) return _jobs.Count; } }

    /// <summary>Queues a checked request: its id, or 0 with the reason (every kept job unfinished).</summary>
    public uint Add(DunRouteRequest request, out string error)
    {
        error = "";
        lock (_gate)
        {
            if (_jobs.Count >= MaxJobs)
            {
                int oldest = _jobs.FindIndex(j => j.Finished);
                if (oldest < 0) { error = $"{MaxJobs} jobs are still unfinished: poll them until they are done, then ask again"; return 0; }
                _jobs.RemoveAt(oldest);
            }
            var job = new DunRouteJob { Id = _nextId++, Request = request, QueuedAt = Clock() };
            if (_nextId == 0) _nextId = 1;
            _jobs.Add(job);
            return job.Id;
        }
    }

    /// <summary>The job to work this tick: the oldest unfinished one (a working job first, it is older).</summary>
    public DunRouteJob? Next()
    {
        lock (_gate)
        {
            foreach (var j in _jobs) if (!j.Finished) return j;
            return null;
        }
    }

    /// <summary>The job's work began (or still waits on the client: <paramref name="detail"/> says what).</summary>
    public void Working(DunRouteJob job, string detail)
    {
        lock (_gate)
        {
            if (job.Finished) return;
            if (job.State == DunJobState.Queued) { job.State = DunJobState.Working; job.StartedAt = Clock(); }
            job.Detail = detail;
        }
    }

    /// <summary>True when a working job has waited on the client longer than WaitLimitMs.</summary>
    public bool WaitedTooLong(DunRouteJob job) => job.State == DunJobState.Working && Clock() - job.StartedAt > WaitLimitMs;

    public void Finish(DunRouteJob job, bool ok, string detail, string? resultJson)
    {
        lock (_gate)
        {
            job.State = ok ? DunJobState.Done : DunJobState.Failed;
            job.Detail = detail;
            job.ResultJson = ok ? resultJson : null;
        }
    }

    public DunRouteJob? Find(uint id)
    {
        lock (_gate)
            foreach (var j in _jobs) if (j.Id == id) return j;
        return null;
    }

    public static string StateName(DunJobState s) => s switch
    {
        DunJobState.Queued  => "queued",
        DunJobState.Working => "working",
        DunJobState.Done    => "done",
        _                   => "failed",
    };

    /// <summary>Poll's answer: {"id","op","state","detail","result"}; false (and state "unknown") for an id not kept.</summary>
    public bool TryPoll(uint id, out string json)
    {
        DunRouteJob? job;
        string state, detail, op;
        string? result;
        lock (_gate)
        {
            job = null;
            foreach (var j in _jobs) if (j.Id == id) { job = j; break; }
            state = job != null ? StateName(job.State) : "unknown";
            detail = job?.Detail ?? (id == 0 ? "0 is never a job id (Request answers 0 when it refuses)"
                : "no such job: only the last 32 are kept, and a RynthAi reload forgets them all; ask again");
            op = job != null ? DunRouteRequest.OpName(job.Request.Op) : "";
            result = job?.ResultJson;
        }
        json = DungeonRouteMaker.Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            if (job != null) w.WriteString("op", op);
            w.WriteString("state", state);
            w.WriteString("detail", detail);
            if (job != null)
            {
                w.WritePropertyName("result");
                if (result != null) w.WriteRawValue(result, skipInputValidation: true);
                else w.WriteNullValue();
            }
            w.WriteEndObject();
        });
        return job != null;
    }

    /// <summary>Request's answer: {"id":N,"state":"queued"} or {"error":"..."}.</summary>
    public static string QueuedJson(uint id) => DungeonRouteMaker.Json(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("id", id);
        w.WriteString("state", "queued");
        w.WriteEndObject();
    });

    public static string ErrorJson(string error) => DungeonRouteMaker.Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("error", error);
        w.WriteEndObject();
    });
}
