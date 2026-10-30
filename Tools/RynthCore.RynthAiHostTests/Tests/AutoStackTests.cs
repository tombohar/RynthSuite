using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// AutoStack (Loot/InventoryManager.cs): the group key and the merge-progress rule. From Silentkelpie's
// PR 12 (group by WCID: two stacks with the same name but different weenies never merge, and AutoStack
// retried them forever), plus the partial fill: a pair whose total is over the max is still merged
// (the server fills the target, the source survives smaller) and that counts as progress.
internal static class AutoStackTests
{
    public static void Register(Runner r)
    {
        r.Add("autostack: stacks group by WCID; the name is only the fallback while the WCID is unknown", GroupKey);
        r.Add("autostack: a partial fill (target grew, source shrank) is progress; an untouched pair is not", Progress);
    }

    private static void GroupKey()
    {
        Check.True(InventoryManager.StackGroupKey(42517, "Coalesced Mana") != InventoryManager.StackGroupKey(42518, "Coalesced Mana"),
            "same name, different WCIDs: separate groups");
        Check.Eq(InventoryManager.StackGroupKey(42517, "Coalesced Mana"), InventoryManager.StackGroupKey(42517, "Coalesced Mana (renamed)"),
            "same WCID: one group whatever the label says");
        Check.Eq(InventoryManager.StackGroupKey(0, "Taper"), InventoryManager.StackGroupKey(0, "Taper"), "no WCID: the name groups");
        Check.True(InventoryManager.StackGroupKey(0, "Taper") != InventoryManager.StackGroupKey(5, "Taper"), "known and unknown WCID do not mix");
        Check.True(InventoryManager.StackGroupKey(7, null).Length > 0, "a null name is safe");
    }

    private static void Progress()
    {
        // 90 + 20 over a max of 100: the server fills the target to 100 and leaves 10 in the source.
        Check.True(InventoryManager.MergeProgressed(90, 20, 100, 10), "partial fill");
        Check.True(InventoryManager.MergeProgressed(30, 20, 50, 20), "target grew (source count read late)");
        Check.True(InventoryManager.MergeProgressed(30, 20, 30, 0), "source emptied");
        Check.False(InventoryManager.MergeProgressed(30, 20, 30, 20), "nothing changed: not progress");
        Check.False(InventoryManager.MergeProgressed(0, 20, 30, 20), "no recorded counts: not progress");
    }
}
