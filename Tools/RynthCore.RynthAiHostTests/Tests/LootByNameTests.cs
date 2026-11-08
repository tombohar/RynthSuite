using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Loot by name (2026-11-02): the list, how a name matches, the file, and the attempt bookkeeping that keeps a
// chest from being opened over and over or an unreachable item from being chased forever.
internal static class LootByNameTests
{
    public static void Register(Runner r)
    {
        r.Add("loot by name: a plain name is the whole name, ignoring case; * makes it a part", Matching);
        r.Add("loot by name: the list adds, removes, refuses duplicates and empties; the radius is clamped", ListEdits);
        r.Add("loot by name: the settings string round-trips, tidies what was typed, and an empty one is an empty list", SettingsString);
        r.Add("loot by name: a use is tried a few times, a gap apart, then the object is left alone for a while", Attempts);
        r.Add("loot by name: an opened object stays finished until it is gone, then a new one is looted again", DoneUntilGone);
        r.Add("meta options: /vt opt set pickupmacrooff (and lootchestsmacrooff) turn Pickup with macro off on and off", PickupMacroOffOption);
    }

    private static void Matching()
    {
        Check.True(LootByNameList.Matches("Unearthed Cache", "Unearthed Cache"), "exact");
        Check.True(LootByNameList.Matches("unearthed cache", "Unearthed Cache"), "case");
        Check.False(LootByNameList.Matches("Cache", "Unearthed Cache"), "a plain word is not part of a longer name");
        Check.True(LootByNameList.Matches("*cache*", "Unearthed Cache"), "contains");
        Check.True(LootByNameList.Matches("Unearthed*", "Unearthed Cache"), "starts with");
        Check.True(LootByNameList.Matches("*Cache", "Unearthed Cache"), "ends with");
        Check.False(LootByNameList.Matches("*", "Unearthed Cache"), "a lone * matches nothing");
        Check.False(LootByNameList.Matches("**", "Unearthed Cache"), "** matches nothing");
        Check.False(LootByNameList.Matches("", "Unearthed Cache"), "empty pattern");
        Check.False(LootByNameList.Matches("Unearthed Cache", ""), "empty name");
    }

    private static void ListEdits()
    {
        var l = new LootByNameList();
        Check.True(l.IsEmpty, "starts empty");
        Check.True(l.Add("  \"Unearthed Cache\"  "), "added, quotes and spaces trimmed");
        Check.False(l.Add("unearthed cache"), "a duplicate in another case is refused");
        Check.False(l.Add("   "), "blank refused");
        Check.Eq(l.Names.Count, 1, "one name");
        Check.True(l.MatchesAny("Unearthed Cache"), "matches");
        Check.False(l.MatchesAny("Chest"), "other names don't");
        Check.True(l.Remove("UNEARTHED CACHE"), "removed ignoring case");
        Check.False(l.Remove("Unearthed Cache"), "removing it again says no");
        for (int i = 0; i < LootByNameList.MaxNames; i++) l.Add("Thing " + i);
        Check.False(l.Add("One too many"), "the list is full");
        l.Clear();
        Check.True(l.IsEmpty, "cleared");
        l.SetRadius(5000);
        Check.Near(l.Radius, LootByNameList.MaxRadius, 0.001, "radius capped");
        l.SetRadius(-4);
        Check.Near(l.Radius, LootByNameList.MinRadius, 0.001, "radius floored");
    }

    private static void SettingsString()
    {
        var l = new LootByNameList();
        l.Add("Unearthed Cache");
        l.Add("*Doubloon*");
        l.SetRadius(45);
        string saved = l.ToSettingString();
        Check.Eq(saved, "Unearthed Cache | *Doubloon*", "how the settings keep the list");
        var back = LootByNameList.Parse(saved, 45);
        Check.Eq(back.Names.Count, 2, "two names back");
        Check.True(back.MatchesAny("Marae Lassel Doubloon"), "the wildcard came back");
        Check.Near(back.Radius, 45, 0.001, "the radius is what was given");

        Check.Eq(LootByNameList.Normalize("  a |b|| A |  \"c\" "), "a | b | c", "blanks, repeats (any case) and quotes tidied, order kept");
        Check.True(LootByNameList.Parse("", 30).IsEmpty, "an empty string is an empty list");
        Check.True(LootByNameList.Parse(null, 30).IsEmpty, "so is null");
        Check.Near(LootByNameList.Parse("x", 0).Radius, LootByNameList.DefaultRadius, 0.001, "a zero radius means the default");
        Check.Near(LootByNameList.Parse("x", 9999).Radius, LootByNameList.MaxRadius, 0.001, "a huge radius is capped");
    }

    private static void Attempts()
    {
        var t = new LootNameTracker();
        const int id = 77;
        long now = 1000;
        Check.True(t.CanUse(id, now), "first use is allowed");
        t.NoteUse(id, now);
        Check.False(t.CanUse(id, now + 1000), "not again inside the gap (the client is walking there)");
        for (int i = 2; i <= LootNameTracker.MaxUses; i++)
        {
            now += LootNameTracker.UseGapMs;
            Check.True(t.CanUse(id, now), $"use {i} after the gap");
            t.NoteUse(id, now);
        }
        now += LootNameTracker.UseGapMs;
        Check.False(t.CanUse(id, now), "the budget is spent");
        Check.True(t.Exhausted(id, now), "exhausted once the last gap has passed");
        t.GiveUp(id, now);
        Check.True(t.IsBlocked(id, now + 1000), "left alone");
        Check.False(t.CanUse(id, now + 1000), "no use while left alone");
        Check.False(t.IsBlocked(id, now + LootNameTracker.GiveUpMs + 1), "tried again after the wait");
        Check.True(t.CanUse(id, now + LootNameTracker.GiveUpMs + 1), "with a fresh budget");
    }

    private static void DoneUntilGone()
    {
        var t = new LootNameTracker();
        t.NoteUse(5, 0);
        t.MarkDone(5);
        Check.True(t.IsBlocked(5, 999_999), "finished stays finished");
        Check.False(t.CanUse(5, 999_999), "and is not used again");
        t.Prune(id => id != 5);   // it left the world
        Check.False(t.IsBlocked(5, 999_999), "forgotten once gone");
        Check.True(t.CanUse(5, 999_999), "the same id coming back is looted again");
        t.MarkDone(6);
        t.Prune(id => true);
        Check.True(t.IsBlocked(6, 0), "objects still there keep their state");
    }

    // A meta that loots a chest with Looting off (the Dreamweave dig's cache) turns this on and leaves it on.
    private static void PickupMacroOffOption()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var settings = new LegacyUiSettings();
        var meta = new MetaManager(settings, host, new PlayerVitalsCache());
        Check.True(settings.LootChestsMacroOff, "on by default");
        meta.TryHandleVtCommand("/vt opt set pickupmacrooff false");
        Check.False(settings.LootChestsMacroOff, "the option turns it off");
        meta.TryHandleVtCommand("/vt opt set pickupmacrooff true");
        Check.True(settings.LootChestsMacroOff, "and on");
        meta.TryHandleVtCommand("/vt opt set lootchestsmacrooff false");
        Check.False(settings.LootChestsMacroOff, "the long name does the same");
    }
}
