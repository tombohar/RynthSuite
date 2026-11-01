using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Vendor;

// Pure judging of where a stack split went, for AutoVendorManager's PendingSplit. No host calls:
// the manager hands in what it saw (objects the server created since the send, the stacks of the
// item now and their counts at the send). Dependency-free so the AutoVendor test project can link it.

internal enum SplitOutcomeKind : byte
{
    Pending = 0,
    /// <summary>The server created a stack of the item after the send: the split-off stack.</summary>
    NewStack = 1,
    /// <summary>No new object, but a stack of the item that was already there grew by the amount
    /// while the source shrank: the split went onto that stack (it sat at the split's target slot).</summary>
    JoinedStack = 2,
    /// <summary>Neither showed up in time.</summary>
    Failed = 3,
}

internal readonly record struct SplitOutcome(SplitOutcomeKind Kind, uint StackId, int StackCount);

internal static class AutoVendorSplits
{
    /// <param name="amount">Units split off.</param>
    /// <param name="sourceBefore">The source stack's count when the split was sent.</param>
    /// <param name="sourceNow">Its count now (0 = gone).</param>
    /// <param name="siblingsBefore">Every stack of the item at the send (the source included) and its count.</param>
    /// <param name="created">Objects the server created since the send.</param>
    /// <param name="stacksNow">Every stack of the item now: id, count, and whether it existed at the send.</param>
    /// <param name="timedOut">The wait is over.</param>
    public static SplitOutcome Resolve(int amount, int sourceBefore, int sourceNow,
        IReadOnlyDictionary<uint, int> siblingsBefore, IReadOnlySet<uint> created,
        IEnumerable<(uint Id, int Count, bool Existed)> stacksNow, bool timedOut)
    {
        var now = new List<(uint Id, int Count, bool Existed)>(stacksNow);

        // 1. A create the server sent after the split, of this item: the split-off stack. The right
        //    count first (a hand split of another amount could land in the same wait).
        (uint Id, int Count)? createdAny = null;
        foreach (var s in now)
        {
            if (s.Existed || !created.Contains(s.Id)) continue;
            if (s.Count == amount) return new SplitOutcome(SplitOutcomeKind.NewStack, s.Id, s.Count);
            createdAny ??= (s.Id, s.Count);
        }
        // Another count: its count may not be readable yet. It is the split's once the source shrank by
        // the amount, or when the wait is over and nothing better showed up.
        if (createdAny is { } ca && (sourceBefore - sourceNow == amount || timedOut))
            return new SplitOutcome(SplitOutcomeKind.NewStack, ca.Id, ca.Count);

        // 2. The create event was missed: a stack that wasn't there, holding exactly the amount.
        foreach (var s in now)
            if (!s.Existed && s.Count == amount)
                return new SplitOutcome(SplitOutcomeKind.NewStack, s.Id, s.Count);

        // 3. No new object: the units went onto a stack that was there (the source shrank by them).
        if (sourceNow > 0 && sourceNow <= sourceBefore - amount)
        {
            foreach (var s in now)
            {
                if (!s.Existed || !siblingsBefore.TryGetValue(s.Id, out int before)) continue;
                if (s.Count - before == amount && sourceBefore - sourceNow == amount)
                    return new SplitOutcome(SplitOutcomeKind.JoinedStack, s.Id, s.Count);
            }
        }

        return timedOut ? new SplitOutcome(SplitOutcomeKind.Failed, 0, 0) : new SplitOutcome(SplitOutcomeKind.Pending, 0, 0);
    }
}
