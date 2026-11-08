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
        r.Add("autostack: name guesses match pyreals exactly and the rest as whole words (Pyreal Mote, Spear, Pearl don't stack)", NameGuess);
        r.Add("autostack: a guessed group the server refuses 3 times running is given up; a merge between resets it", Refusals);
    }

    private static void NameGuess()
    {
        Check.Eq(25000, InventoryManager.NameFallbackMaxStack("Pyreal"), "pyreal coins");
        Check.Eq(25000, InventoryManager.NameFallbackMaxStack("Pyreals"), "pyreal coins, plural");
        Check.Eq(1, InventoryManager.NameFallbackMaxStack("Pyreal Mote"), "2026-11-08: motes don't stack");
        Check.Eq(1, InventoryManager.NameFallbackMaxStack("Quality Pyreal Ingot"), "ingots don't stack");
        Check.Eq(1, InventoryManager.NameFallbackMaxStack("Spear"), "'pea' inside a word");
        Check.Eq(1, InventoryManager.NameFallbackMaxStack("Black Pearl"), "'pea' inside a word");
        Check.Eq(1, InventoryManager.NameFallbackMaxStack("Arrowheads"), "'arrow' inside a word");
        Check.Eq(250, InventoryManager.NameFallbackMaxStack("Greater Deadly Armor Piercing Arrow"), "arrows");
        Check.Eq(250, InventoryManager.NameFallbackMaxStack("Deadly Fire Quarrel"), "quarrels");
        Check.Eq(100, InventoryManager.NameFallbackMaxStack("Iron Scarab"), "scarabs");
        Check.Eq(100, InventoryManager.NameFallbackMaxStack("Prismatic Taper"), "tapers");
        Check.Eq(1, InventoryManager.NameFallbackMaxStack(null), "no name");
        Check.Eq(1, InventoryManager.NameFallbackMaxStack("  "), "blank name");
    }

    private static void Refusals()
    {
        var r = new StackGroupRefusals();
        Check.False(r.Refused("w:6353"), "1st refusal");
        Check.False(r.Refused("w:6353"), "2nd refusal");
        Check.False(r.GivenUp("w:6353"), "not yet");
        Check.True(r.Refused("w:6353"), "3rd refusal gives it up, once");
        Check.True(r.GivenUp("w:6353"), "given up");
        Check.False(r.Refused("w:6353"), "already given up: no second notice");

        Check.False(r.Refused("w:1"), "other group 1st");
        Check.False(r.Refused("w:1"), "other group 2nd");
        r.Merged("w:1");
        Check.False(r.Refused("w:1"), "a merge between resets the count");
        Check.False(r.GivenUp("w:1"), "the other group goes on");
        Check.False(r.Refused(""), "an empty group never counts");
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
