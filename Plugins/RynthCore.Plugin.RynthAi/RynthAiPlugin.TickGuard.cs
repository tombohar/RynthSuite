using System;

namespace RynthCore.Plugin.RynthAi;

// Each OnTick step runs under its own guard (Diagnostics/TickGuard.cs): one that throws every tick
// is logged and, unless it is essential, switched off for a while; the rest of the tick still runs,
// healing and combat above all (structural audit 2026-10-21, finding 4).
public sealed partial class RynthAiPlugin
{
    private TickGuard? _tickGuard;

    /// <summary>The steps' fault counters (internal for the host tests).</summary>
    internal TickGuard TickGuards => _tickGuard ??= new TickGuard(m => Host.Log(m), m => Host.WriteToChat(m, 2));

    /// <summary>Runs one OnTick step: an exception is logged and counted, and the tick goes on.</summary>
    private void Guard(TickStep step, Action run)
    {
        TickGuard g = TickGuards;
        if (!g.MayRun(step)) return;
        try
        {
            run();
            g.Succeeded(step);
        }
        catch (Exception ex) { g.Failed(step, ex); }
    }

    /// <summary>
    /// Runs one OnTick step that answers "hold the rest of the tick". A fault, or the step switched
    /// off, answers <paramref name="whenFaulted"/> (false: the tick goes on).
    /// </summary>
    private bool GuardHold(TickStep step, Func<bool> run, bool whenFaulted = false)
    {
        TickGuard g = TickGuards;
        if (!g.MayRun(step)) return whenFaulted;
        try
        {
            bool hold = run();
            g.Succeeded(step);
            return hold;
        }
        catch (Exception ex)
        {
            g.Failed(step, ex);
            return whenFaulted;
        }
    }

    /// <summary>The meta's tick, in every hold and at the end of the tick, under one guard.</summary>
    private void ThinkMeta() => Guard(TickStep.Meta, () => _metaManager?.Think());
}
