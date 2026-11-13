using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// Buff items (Combat/BuffItemPicker.cs): which item buffing uses now. The built-ins can be blocked,
// the player's own items are matched by their spells or, with none to check, a repeat time; only
// items in the pack are picked; never the same item within 15 s; two uses whose buff never showed
// rest the item 10 minutes. Times are passed in.
internal static class BuffItemPickerTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Local);
    private static readonly DateTime U0 = new(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<uint> NoBuffs = new();

    private const uint Benediction = 3810, LesserBenediction = 4024, Favor = 3811, CustomSpell = 6123;

    public static void Register(Runner r)
    {
        r.Add("buff items: a built-in whose buff is off is used, one that is on is not", BuiltInOnOff);
        r.Add("buff items: a blocked built-in is never used, the next item is", BlockedBuiltIn);
        r.Add("buff items: a custom item is matched by its spells (buff on / off)", CustomBuffOnOff);
        r.Add("buff items: an item not in the pack is never picked", MissingFromPack);
        r.Add("buff items: no spell to check - the repeat timer, kept across reloads", FallbackTimer);
        r.Add("buff items: a custom item whose spells aren't read yet waits", SpellsNotReadWait);
        r.Add("buff items: 15 s between uses of one item, the other Benediction counts as used", RetryGuardAndShared);
        r.Add("buff items: two uses whose buff never showed rest the item 10 min", RestAfterTwoUses);
        r.Add("buff items: allow / block / add / remove make new lists", ListEdits);
        r.Add("buff items: what the list says for each line", Describe);
    }

    private static List<BuffItemInPack> Pack(params (int Id, string Name)[] items)
    {
        var p = new List<BuffItemInPack>();
        foreach (var (id, name) in items) p.Add(new BuffItemInPack(id, name));
        return p;
    }

    private static HashSet<uint> On(params uint[] ids) => new(ids);

    private static CustomBuffItem Custom(string name, uint[]? spells, int repeat = 60, bool allowed = true) => new()
    {
        Name = name,
        Allowed = allowed,
        SpellIds = spells != null ? new List<uint>(spells) : new List<uint>(),
        SpellsRead = spells != null,
        RepeatMinutes = repeat,
    };

    private static void BuiltInOnOff()
    {
        var entries = BuffItemEntry.Build(null, null);
        Check.Eq(entries.Count, 3, "three built-ins");
        var pick = new BuffItemPicker().Choose(entries, Pack((7, "Blackmoor's Favor")), NoBuffs, T0, U0);
        Check.True(pick is { ItemId: 7 }, "Favor's buff is off: use it");
        Check.True(pick?.Entry.BuiltIn == true, "the built-in line");

        var none = new BuffItemPicker().Choose(entries, Pack((7, "Blackmoor's Favor")), On(Favor), T0, U0);
        Check.Null(none, "Favor's buff is on: nothing");
        var lesser = new BuffItemPicker().Choose(entries, Pack((8, "asheron's lesser benediction")), On(Benediction), T0, U0);
        Check.Null(lesser, "the Lesser Benediction counts as on with the full one's spell (and names match without case)");
    }

    private static void BlockedBuiltIn()
    {
        var entries = BuffItemEntry.Build(new List<string> { "Asheron's Benediction" }, null);
        var pack = Pack((1, "Asheron's Benediction"), (2, "Blackmoor's Favor"));
        var pick = new BuffItemPicker().Choose(entries, pack, NoBuffs, T0, U0);
        Check.True(pick is { ItemId: 2 }, "the blocked Benediction is skipped, Favor goes");

        var onlyBlocked = new BuffItemPicker().Choose(entries, Pack((1, "Asheron's Benediction")), NoBuffs, T0, U0);
        Check.Null(onlyBlocked, "only the blocked one in the pack: nothing");
        Check.False(BuffItemEntry.Find(entries, "asheron's benediction")!.Allowed, "listed, not allowed");
    }

    private static void CustomBuffOnOff()
    {
        var customs = new List<CustomBuffItem> { Custom("Elixir of the Hunt", new[] { CustomSpell }) };
        var entries = BuffItemEntry.Build(null, customs);
        var pack = Pack((5, "Elixir of the Hunt"));
        var pick = new BuffItemPicker().Choose(entries, pack, NoBuffs, T0, U0);
        Check.True(pick is { ItemId: 5 }, "its spell isn't on: use it");
        Check.True(pick?.Entry.Custom == customs[0], "the custom line");
        Check.Null(new BuffItemPicker().Choose(entries, pack, On(CustomSpell), T0, U0), "its spell is on: nothing");
        Check.True(new BuffItemPicker().Choose(entries, pack, On(CustomSpell + 1), T0, U0) is { ItemId: 5 }, "another spell on: still used");

        customs[0].Allowed = false;
        Check.Null(new BuffItemPicker().Choose(BuffItemEntry.Build(null, customs), pack, NoBuffs, T0, U0), "a blocked custom item: nothing");
    }

    private static void MissingFromPack()
    {
        var customs = new List<CustomBuffItem> { Custom("Elixir of the Hunt", new[] { CustomSpell }), Custom("Timer Gem", Array.Empty<uint>()) };
        var entries = BuffItemEntry.Build(null, customs);
        Check.Null(new BuffItemPicker().Choose(entries, Pack(), NoBuffs, T0, U0), "an empty pack: nothing");
        Check.Null(new BuffItemPicker().Choose(entries, Pack((9, "Mana Potion"), (10, "Elixir of the Hunter")), NoBuffs, T0, U0),
            "only other items (a longer name doesn't match): nothing");
        var pick = new BuffItemPicker().Choose(entries, Pack((9, "Mana Potion"), (11, "Timer Gem")), NoBuffs, T0, U0);
        Check.True(pick is { ItemId: 11 }, "the one that is in the pack goes");
    }

    private static void FallbackTimer()
    {
        var gem = Custom("Timer Gem", Array.Empty<uint>(), repeat: 30);
        var customs = new List<CustomBuffItem> { gem };
        var entries = BuffItemEntry.Build(null, customs);
        var pack = Pack((3, "Timer Gem"));
        var picker = new BuffItemPicker();

        Check.True(entries[3].UsesTimer, "no spell to check: the timer");
        var first = picker.Choose(entries, pack, On(Favor, CustomSpell), T0, U0);
        Check.True(first is { ItemId: 3 }, "never used: used now, whatever buffs are on");
        picker.NoteUsed(first!.Value, entries, T0, U0);
        Check.Eq(gem.LastUsedUtc, (DateTime?)U0, "the use is kept on the item (saved with the profile)");

        Check.Null(picker.Choose(entries, pack, NoBuffs, T0.AddMinutes(29), U0.AddMinutes(29)), "29 min later: not yet");
        Check.True(picker.Choose(entries, pack, NoBuffs, T0.AddMinutes(30), U0.AddMinutes(30)) is { ItemId: 3 }, "30 min later: due");

        // A reload: a new picker, the item's saved last use still counts.
        var reloaded = new BuffItemPicker();
        var entries2 = BuffItemEntry.Build(null, customs);
        Check.Null(reloaded.Choose(entries2, pack, NoBuffs, T0.AddMinutes(5), U0.AddMinutes(5)), "after a reload the timer still runs (5 min in)");

        // A use that didn't go out doesn't start the timer.
        var gem2 = Custom("Other Gem", Array.Empty<uint>(), repeat: 30);
        var e3 = BuffItemEntry.Build(null, new List<CustomBuffItem> { gem2 });
        var p3 = new BuffItemPicker();
        var c3 = p3.Choose(e3, Pack((4, "Other Gem")), NoBuffs, T0, U0);
        p3.NoteUsed(c3!.Value, e3, T0, U0, sent: false);
        Check.Null(gem2.LastUsedUtc, "not sent: no last use");
        Check.Null(p3.Choose(e3, Pack((4, "Other Gem")), NoBuffs, T0.AddSeconds(10), U0.AddSeconds(10)), "but not again within 15 s");
        Check.True(p3.Choose(e3, Pack((4, "Other Gem")), NoBuffs, T0.AddSeconds(16), U0.AddSeconds(16)) is { ItemId: 4 }, "tried again after 15 s");

        Check.Eq(BuffItemRules.ClampRepeatMinutes(0), 1, "repeat at least 1 min");
        Check.Eq(BuffItemRules.ClampRepeatMinutes(1_000_000), BuffItemRules.MaxRepeatMinutes, "and at most a week");
    }

    private static void SpellsNotReadWait()
    {
        var byName = Custom("Unknown Potion", null);
        var entries = BuffItemEntry.Build(null, new List<CustomBuffItem> { byName });
        Check.False(entries[3].SpellsKnown, "added by name: spells not read");
        Check.Null(new BuffItemPicker().Choose(entries, Pack((6, "Unknown Potion")), NoBuffs, T0, U0),
            "not read yet: not used (no use on a guess)");
    }

    private static void RetryGuardAndShared()
    {
        var entries = BuffItemEntry.Build(null, null);
        var pack = Pack((1, "Asheron's Benediction"), (2, "Asheron's Lesser Benediction"));
        var picker = new BuffItemPicker();
        var c = picker.Choose(entries, pack, NoBuffs, T0, U0);
        Check.True(c is { ItemId: 1 }, "the first in the pack");
        picker.NoteUsed(c!.Value, entries, T0, U0);
        Check.Null(picker.Choose(entries, pack, NoBuffs, T0.AddSeconds(14), U0.AddSeconds(14)),
            "14 s later: neither (the Lesser shares the spell, so it counts as used too)");
        Check.True(picker.Choose(entries, pack, NoBuffs, T0.AddSeconds(15), U0.AddSeconds(15)) is { ItemId: 1 }, "15 s later: again");

        var withFavor = Pack((1, "Asheron's Benediction"), (3, "Blackmoor's Favor"));
        var p2 = new BuffItemPicker();
        p2.NoteUsed(p2.Choose(entries, withFavor, NoBuffs, T0, U0)!.Value, entries, T0, U0);
        Check.True(p2.Choose(entries, withFavor, NoBuffs, T0.AddSeconds(3), U0.AddSeconds(3)) is { ItemId: 3 },
            "another buff's item isn't held by it (Favor next check)");
    }

    private static void RestAfterTwoUses()
    {
        var potion = Custom("Potion of Might", new[] { CustomSpell });
        var entries = BuffItemEntry.Build(null, new List<CustomBuffItem> { potion });
        var pack = Pack((5, "Potion of Might"));
        var picker = new BuffItemPicker();
        DateTime t = T0;
        for (int i = 0; i < BuffItemRules.UsesBeforeRest; i++)
        {
            var c = picker.Choose(entries, pack, NoBuffs, t, U0);
            Check.True(c is { ItemId: 5 }, $"use {i + 1}");
            picker.NoteUsed(c!.Value, entries, t, U0);
            t = t.AddSeconds(16);
        }
        Check.Null(picker.Choose(entries, pack, NoBuffs, t, U0), "a third use with no buff ever: rested");
        var notes = picker.TakeNotes();
        Check.Eq(notes.Count, 1, "the player is told once");
        Check.True(notes.Count == 1 && notes[0].Contains("Potion of Might") && notes[0].Contains("10 minutes"), "with the item and the wait");
        Check.Null(picker.Choose(entries, pack, NoBuffs, t.AddMinutes(9), U0), "9 min into the rest: nothing");
        Check.True(picker.Choose(entries, pack, NoBuffs, t.AddMinutes(10), U0) is { ItemId: 5 }, "after 10 min: tried again");

        // A buff that shows resets the count.
        var p2 = new BuffItemPicker();
        p2.NoteUsed(p2.Choose(entries, pack, NoBuffs, T0, U0)!.Value, entries, T0, U0);
        Check.Null(p2.Choose(entries, pack, On(CustomSpell), T0.AddSeconds(20), U0), "its buff landed");
        p2.NoteUsed(p2.Choose(entries, pack, NoBuffs, T0.AddMinutes(60), U0)!.Value, entries, T0.AddMinutes(60), U0);
        Check.True(p2.Choose(entries, pack, NoBuffs, T0.AddMinutes(60).AddSeconds(16), U0) is { ItemId: 5 },
            "an hour later its first miss isn't counted with the old one: used again");
    }

    private static void ListEdits()
    {
        var blocked = new List<string>();
        var customs = new List<CustomBuffItem>();

        var r = BuffItemRules.SetAllowed(blocked, customs, "blackmoor's favor", false, out var nb, out var nc, out string shown);
        Check.Eq(r, BuffItemRules.EditResult.Changed, "block a built-in");
        Check.Eq(shown, "Blackmoor's Favor", "its own spelling");
        Check.True(nb.Count == 1 && blocked.Count == 0, "a new blocked list; the old one is untouched");
        Check.Eq(BuffItemRules.SetAllowed(nb, nc, "Blackmoor's Favor", false, out _, out _, out _), BuffItemRules.EditResult.Unchanged, "blocking again: unchanged");
        Check.Eq(BuffItemRules.SetAllowed(nb, nc, "Blackmoor's Favor", true, out var nb2, out _, out _), BuffItemRules.EditResult.Changed, "allow it again");
        Check.Eq(nb2.Count, 0, "the blocked list is empty again");
        Check.Eq(BuffItemRules.SetAllowed(blocked, customs, "Nothing Like It", true, out _, out _, out _), BuffItemRules.EditResult.NotFound, "an unknown name");

        Check.Eq(BuffItemRules.Add(customs, "  Elixir of the Hunt ", null, out var c1, out var added), BuffItemRules.EditResult.Added, "add by name");
        Check.True(c1.Count == 1 && customs.Count == 0 && added!.Name == "Elixir of the Hunt" && !added.SpellsRead && added.Allowed,
            "a new list, trimmed name, allowed, spells not read");
        Check.Eq(BuffItemRules.Add(c1, "elixir of the hunt", null, out _, out _), BuffItemRules.EditResult.AlreadyListed, "the same name again");
        Check.Eq(BuffItemRules.Add(c1, "Elixir of the Hunt", new uint[] { CustomSpell }, out var c2, out var upd), BuffItemRules.EditResult.Updated, "added again from the item: spells refreshed");
        Check.True(upd!.SpellsRead && upd.SpellIds.Count == 1 && !c1[0].SpellsRead, "on a copy; the old list's item is untouched");
        Check.Eq(BuffItemRules.Add(c2, "Asheron's Benediction", null, out _, out _), BuffItemRules.EditResult.BuiltIn, "a built-in name is refused");
        Check.Eq(BuffItemRules.Add(c2, "   ", null, out _, out _), BuffItemRules.EditResult.EmptyName, "no name");

        Check.Eq(BuffItemRules.SetAllowed(nb, c2, "ELIXIR OF THE HUNT", false, out _, out var c3, out _), BuffItemRules.EditResult.Changed, "block a custom item");
        Check.True(!c3[0].Allowed && c2[0].Allowed, "on a copy");

        Check.Eq(BuffItemRules.Remove(c3, "Asheron's Benediction", out _, out _), BuffItemRules.EditResult.BuiltIn, "a built-in can't be removed");
        Check.Eq(BuffItemRules.Remove(c3, "elixir of the hunt", out var c4, out string removed), BuffItemRules.EditResult.Removed, "remove a custom item");
        Check.True(c4.Count == 0 && c3.Count == 1 && removed == "Elixir of the Hunt", "a new list");
        Check.Eq(BuffItemRules.Remove(c4, "Elixir of the Hunt", out _, out _), BuffItemRules.EditResult.NotFound, "already gone");

        // A custom item named like a built-in (a hand-edited profile) is not listed twice.
        var odd = BuffItemEntry.Build(null, new List<CustomBuffItem> { Custom("Blackmoor's Favor", null) });
        Check.Eq(odd.Count, 3, "a custom copy of a built-in name is ignored");
    }

    private static void Describe()
    {
        var gem = Custom("Timer Gem", Array.Empty<uint>(), repeat: 30);
        var entries = BuffItemEntry.Build(new List<string> { "Blackmoor's Favor" }, new List<CustomBuffItem> { gem, Custom("Unread", null) });
        var p = new BuffItemPicker();
        BuffItemEntry Get(string n) => BuffItemEntry.Find(entries, n)!;
        Check.Eq(p.Describe(Get("Blackmoor's Favor"), 1, NoBuffs, T0, U0), "blocked", "blocked");
        Check.Eq(p.Describe(Get("Asheron's Benediction"), 0, NoBuffs, T0, U0), "not in your pack", "missing");
        Check.Eq(p.Describe(Get("Asheron's Benediction"), 1, On(LesserBenediction), T0, U0), "buff on", "on");
        Check.Eq(p.Describe(Get("Asheron's Benediction"), 1, NoBuffs, T0, U0), "buff off: used at the next check", "off");
        Check.Eq(p.Describe(Get("Unread"), 1, NoBuffs, T0, U0), "reading its spell", "unread");
        Check.True(p.Describe(Get("Timer Gem"), 1, NoBuffs, T0, U0).Contains("used now, then every 30 min"), "timer, never used");
        gem.LastUsedUtc = U0;
        Check.True(p.Describe(Get("Timer Gem"), 1, NoBuffs, T0, U0.AddMinutes(10)).Contains("next in 20 min"), "timer, 10 min after a use");
    }
}
