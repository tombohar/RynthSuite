using System;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>How a move in flight ended (or that it hasn't yet).</summary>
internal enum MoveOutcome
{
    Pending,
    /// <summary>The item is in the destination, or merged into a stack there.</summary>
    Done,
    /// <summary>The server refused it (InventoryServerSaveFailed, or a refusal naming the item).</summary>
    Refused,
    /// <summary>The server said "You're too busy" (or too many actions): try again later.</summary>
    TooBusy,
    /// <summary>No answer in <see cref="MoveTracker.TimeoutMs"/>.</summary>
    TimedOut,
}

/// <summary>What the client says about the moved item and the server's last refusal, read each tick.</summary>
internal struct MoveObservation
{
    public bool Exists;
    public uint Container;
    public uint Wielder;
    public int Stack;
    /// <summary>Its slot in its container (the engine's slot order), or -1 when unknown. Read for reorders only.</summary>
    public int Slot;
    /// <summary>The engine's refusal counter (TryGetLastWeenieError seq; 0 when unknown).</summary>
    public int ErrorSeq;
    public uint Error;
    public uint ErrorEventType;
    public uint ErrorObject;
}

/// <summary>The one move RynthSort has out.</summary>
internal sealed class MoveInFlight
{
    public SortMove Move;
    public long SentMs;
    public int ErrorSeqAtSend;
    public int StackAtSend;
}

/// <summary>
/// One move at a time: what counts as its answer, and how long to hold off after a refusal.
/// Pure (no host calls), so the tests drive it with observations.
/// </summary>
internal static class MoveTracker
{
    /// <summary>A move with no answer by then has failed (the Lua script's ObjectMove time limit).</summary>
    public const long TimeoutMs = 5000;
    /// <summary>An item that couldn't be moved is left alone this long.</summary>
    public const long BlockMs = 60_000;
    /// <summary>The longest hold after refusals in a row.</summary>
    public const long MaxBackoffMs = 30_000;

    /// <summary>The container reads as this when the engine can't say (no ownership info).</summary>
    public const uint UnknownContainer = 0xFFFFFFFF;
    public const uint EventInventoryServerSaveFailed = 0x00A0;
    public const uint ErrorYoureTooBusy = 0x001D;
    public const uint ErrorTooManyActions = 0x0045;

    /// <summary>
    /// What the client says about the move so far. A move within one pack (a stack joined, a slot
    /// changed) can't be judged by the item's container, which it never left: a join is answered
    /// when the item is gone or smaller, a reorder when the item sits at its slot (or left the pack).
    /// </summary>
    public static MoveOutcome Observe(MoveInFlight m, in MoveObservation o, long nowMs)
    {
        SortMove mv = m.Move;
        if (!o.Exists) return MoveOutcome.Done;                                   // merged on arrival
        switch (mv.Kind)
        {
            case MoveKind.Stack:
                if (o.Stack < m.StackAtSend) return MoveOutcome.Done;
                break;
            case MoveKind.Reorder:
                if (o.Container == mv.Dest.Id && o.Slot == mv.Placement) return MoveOutcome.Done;
                if (o.Container != mv.Dest.Id && o.Container != UnknownContainer) return MoveOutcome.Done;   // someone moved it away
                break;
            default:
                if (o.Container == mv.Dest.Id && o.Wielder == 0) return MoveOutcome.Done;
                if (mv.MergeInto != 0 && o.Stack < m.StackAtSend) return MoveOutcome.Done; // part of it joined the stack
                break;
        }
        if (o.ErrorSeq != 0 && o.ErrorSeq != m.ErrorSeqAtSend)
        {
            if (o.Error == ErrorYoureTooBusy || o.Error == ErrorTooManyActions) return MoveOutcome.TooBusy;
            if (o.ErrorObject == mv.Item.Id || o.ErrorEventType == EventInventoryServerSaveFailed) return MoveOutcome.Refused;
            // Someone else's refusal (a fizzle, RynthAi's use): not ours; keep waiting.
        }
        return nowMs - m.SentMs >= TimeoutMs ? MoveOutcome.TimedOut : MoveOutcome.Pending;
    }

    /// <summary>
    /// The hold before the next move after <paramref name="failuresInARow"/> failures: 2 s, 4 s,
    /// 8 s ... up to <see cref="MaxBackoffMs"/>; 0 after a success.
    /// </summary>
    public static long BackoffMs(int failuresInARow)
    {
        if (failuresInARow <= 0) return 0;
        int shift = Math.Min(failuresInARow - 1, 10);
        return Math.Min(2000L << shift, MaxBackoffMs);
    }
}
