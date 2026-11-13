using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Buff items (Use Buff Items, BuffManager.BuffItems.cs) on the fake engine: a blocked built-in is
// never used, a custom item's spell is read from the item (data id 28, else its appraisal) and its
// buff is matched against the enchantments, one with no spell is used on its repeat time, only
// pack items are used, and the lists are saved, loaded, copied and bridged like DisabledBuffs.
internal static class BuffItemsTests
{
    private const uint Player = 0x50000B01;
    private const uint Benediction = 0x80001001, Favor = 0x80001002, Elixir = 0x80001003, Gem = 0x80001004, Unappraised = 0x80001005;
    private const uint ElixirSpell = 1;   // Strength Other I: a buff with a duration in the spell catalog
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly MethodInfo TryUse = typeof(BuffManager).GetMethod("TryUseBuffItems", Priv)!;
    private static readonly FieldInfo LastCheck = typeof(BuffManager).GetField("_lastBuffItemCheck", Priv)!;

    public static void Register(Runner r)
    {
        r.Add("buff items (host): a blocked built-in isn't used, an allowed one is, only while its buff is off", BlockedBuiltIn);
        r.Add("buff items (host): a custom item's spell is read from the item; its buff on/off decides", CustomItemSpell);
        r.Add("buff items (host): an item with no spell is used on its repeat time; none in the pack, none used", TimerAndPack);
        r.Add("buff items (host): Add selected item / add by name / an unappraised item asks for its appraisal", AddPaths);
        r.Add("buff items (host): the lists default empty, save, load, copy and bridge (absent = unchanged)", ListsSetting);
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { UseBuffItems = true, EnableBuffing = false };
        public readonly BuffManager B;
        public readonly RynthCoreHost Host;
        public DateTime Now = new(2026, 11, 11, 12, 0, 0);

        public Rig(Action? setup = null)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;   // UseObject and RequestId
            FakeHost.Names[Benediction] = "Asheron's Benediction";
            FakeHost.Names[Favor] = "Blackmoor's Favor";
            FakeHost.Names[Elixir] = "Elixir of the Hunt";
            FakeHost.Names[Gem] = "Gem of Waking";
            FakeHost.DataIds[(Elixir, 28)] = ElixirSpell;   // the spell its use casts (CreateObject header)
            FakeHost.SpellIds[Gem] = Array.Empty<uint>();    // appraised: casts nothing
            setup?.Invoke();
            Host = FakeHost.Create(Player);
            var skills = new CharacterSkills(Host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(Host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            B = new BuffManager(Host, S, sm, new PlayerVitalsCache());
            B.SetCharacterSkills(skills);
            var ids = FakeHost.Names.Keys.ToArray();
            B.SetWorldObjectCache(FakeHost.MakeCache(Host, Player, ids));
            RynthClock.TestNow = () => Now;
        }

        /// <summary>One buff-item check, 3 s after the last one; the ids used in it.</summary>
        public List<uint> Check(params uint[] enchantments)
        {
            FakeHost.Enchantments.Clear();
            foreach (uint e in enchantments) FakeHost.Enchantments.Add((e, double.MaxValue));
            Now = Now.AddSeconds(3.1);
            int before = FakeHost.Uses.Count;
            TryUse.Invoke(B, null);
            return FakeHost.Uses.Skip(before).ToList();
        }

        public void Wait(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static void Run(Action body)
    {
        try { body(); }
        finally { RynthClock.TestNow = null; }
    }

    private static void BlockedBuiltIn() => Run(() =>
    {
        var rig = new Rig();
        rig.S.CustomBuffItems = new List<CustomBuffItem>();
        rig.S.SetBuffItemAllowed("Asheron's Benediction", false, out _);
        var used = rig.Check();
        Check.True(used.SequenceEqual(new[] { Favor }), $"Benediction blocked: Favor is used ({string.Join(",", used.Select(u => u.ToString("X")))})");
        rig.Wait(20);
        used = rig.Check(3811);
        Check.Eq(used.Count, 0, "Favor's buff on, Benediction blocked: nothing used");
        rig.S.SetBuffItemAllowed("asheron's benediction", true, out _);
        used = rig.Check(3811);
        Check.True(used.SequenceEqual(new[] { Benediction }), "allowed again: Benediction is used");

        rig.S.UseBuffItems = false;
        rig.Wait(60);
        Check.Eq(rig.Check().Count, 0, "Use Buff Items off: nothing at all");
    });

    private static void CustomItemSpell() => Run(() =>
    {
        var rig = new Rig();
        rig.S.SetBuffItemAllowed("Asheron's Benediction", false, out _);
        rig.S.SetBuffItemAllowed("Blackmoor's Favor", false, out _);
        rig.S.AddCustomBuffItem("Elixir of the Hunt", null, out _);   // by name: spells not read yet
        var used = rig.Check();
        Check.True(used.SequenceEqual(new[] { Elixir }), "its spell is read from the item (data id 28) and its buff is off: used");
        var item = rig.S.CustomBuffItems.Single();
        Check.True(item.SpellsRead && item.SpellIds.SequenceEqual(new[] { ElixirSpell }), "the spell is kept on the item (saved)");
        rig.Wait(20);
        Check.Eq(rig.Check(ElixirSpell).Count, 0, "its buff on: not used again");
        rig.Wait(60);
        Check.Eq(rig.Check(ElixirSpell).Count, 0, "still on a minute later: not used");
        Check.True(rig.Check().SequenceEqual(new[] { Elixir }), "the buff ran out: used again");

        item = rig.S.CustomBuffItems.Single();
        rig.S.SetBuffItemAllowed(item.Name, false, out _);
        rig.Wait(20);
        Check.Eq(rig.Check().Count, 0, "blocked: never used");
    });

    private static void TimerAndPack() => Run(() =>
    {
        var rig = new Rig();
        rig.S.SetBuffItemAllowed("Asheron's Benediction", false, out _);
        rig.S.SetBuffItemAllowed("Blackmoor's Favor", false, out _);
        rig.S.AddCustomBuffItem("Gem of Waking", null, out var gem);
        gem!.RepeatMinutes = 30;
        rig.S.AddCustomBuffItem("Potion Not Carried", null, out _);
        var used = rig.Check();
        Check.True(used.SequenceEqual(new[] { Gem }), "an appraised item that casts nothing: used on its timer");
        var saved = rig.S.CustomBuffItems.First(c => c.Name == "Gem of Waking");
        Check.True(saved.SpellsRead && saved.SpellIds.Count == 0 && saved.LastUsedUtc != null, "read as no spell; the use is kept");
        Check.Eq(rig.Check().Count, 0, "3 s later: not again");
        // The repeat time counts on the saved UTC time (a reload keeps it): put it 29 min back.
        saved.LastUsedUtc = DateTime.UtcNow.AddMinutes(-29);
        rig.Wait(20);
        Check.Eq(rig.Check().Count, 0, "29 min after the last use: not yet");
        saved.LastUsedUtc = DateTime.UtcNow.AddMinutes(-31);
        Check.True(rig.Check().SequenceEqual(new[] { Gem }), "31 min: used again");
        Check.False(FakeHost.Uses.Any(u => !FakeHost.Names.ContainsKey(u)), "nothing outside the pack was ever used");
    });

    private static void AddPaths() => Run(() =>
    {
        var rig = new Rig(() => FakeHost.Names[Unappraised] = "Odd Draught");
        FakeHost.SelectedItem = Elixir;
        Check.True(rig.B.AddSelectedBuffItem(out string reply), "Add selected item: added");
        Check.True(reply.Contains("Elixir of the Hunt") && reply.Contains("its buff counts as on"), $"says how it is checked: {reply}");
        var elixir = BuffItemRules.FindCustom(rig.S.CustomBuffItems, "elixir of the hunt");
        Check.True(elixir != null && elixir.SpellsRead && elixir.SpellIds.SequenceEqual(new[] { ElixirSpell }), "with its spell");

        FakeHost.SelectedItem = Benediction;
        Check.False(rig.B.AddSelectedBuffItem(out reply), "a built-in is refused");
        Check.True(reply.Contains("built in"), reply);
        FakeHost.SelectedItem = 0;
        Check.False(rig.B.AddSelectedBuffItem(out reply), "nothing selected");
        Check.True(reply.Contains("nothing selected"), reply);

        Check.True(rig.B.AddBuffItemByName("gem of waking", out reply), "add by name, in the pack");
        Check.True(BuffItemRules.FindCustom(rig.S.CustomBuffItems, "Gem of Waking") is { SpellsRead: true, SpellIds.Count: 0 } && reply.Contains("used every"),
            $"its own spelling, read as no spell: the timer ({reply})");

        int idsBefore = FakeHost.IdRequests.Count;
        Check.True(rig.B.AddBuffItemByName("Odd Draught", out reply), "an item with no spell header and no appraisal");
        Check.True(BuffItemRules.FindCustom(rig.S.CustomBuffItems, "Odd Draught") is { SpellsRead: false }, "added with its spell not read");
        Check.Eq(FakeHost.IdRequests.Count, idsBefore + 1, "its appraisal is asked for");
        Check.True(reply.Contains("read from the item"), reply);

        Check.True(rig.B.AddBuffItemByName("Not In My Pack", out reply), "a name not in the pack is added");
        Check.True(BuffItemRules.FindCustom(rig.S.CustomBuffItems, "Not In My Pack") is { SpellsRead: false }, "spells read later");

        var rows = rig.B.DescribeBuffItems();
        Check.Eq(rows.Count, 3 + 4, "the list: three built-ins and four of the player's");
        Check.True(rows.First(r => r.Name == "Not In My Pack").Status == "not in your pack", "a missing item says so");
        Check.True(rows.First(r => r.Name == "Odd Draught").Status == "reading its spell", "the unappraised one waits");
    });

    private static void ListsSetting()
    {
        var old = JsonSerializer.Deserialize("{\"EnableBuffing\":true,\"UseBuffItems\":true}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.BlockedBuffItems.Count == 0 && old.CustomBuffItems.Count == 0, "a profile saved before the setting: all built-ins allowed, none added");
        var nul = JsonSerializer.Deserialize("{\"BlockedBuffItems\":null,\"CustomBuffItems\":null}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(nul != null && nul.BlockedBuffItems.Count == 0 && nul.CustomBuffItems.Count == 0, "null lists load empty");

        var some = new LegacyUiSettings();
        some.SetBuffItemAllowed("Blackmoor's Favor", false, out _);
        some.AddCustomBuffItem("Elixir of the Hunt", new uint[] { ElixirSpell }, out var e);
        e!.RepeatMinutes = 45;
        e.LastUsedUtc = new DateTime(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc);
        some.AddCustomBuffItem("Gem of Waking", null, out _);
        some.SetBuffItemAllowed("Gem of Waking", false, out _);
        string json = JsonSerializer.Serialize(some, RynthAiJsonContext.Default.LegacyUiSettings);
        var back = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(back.BlockedBuffItems.SequenceEqual(new[] { "Blackmoor's Favor" }), "the blocked built-in is saved and loaded");
        var be = BuffItemRules.FindCustom(back.CustomBuffItems, "Elixir of the Hunt");
        Check.True(be != null && be.Allowed && be.SpellsRead && be.SpellIds.SequenceEqual(new[] { ElixirSpell }) && be.RepeatMinutes == 45
                   && be.LastUsedUtc == e.LastUsedUtc, "a custom item with its spell, repeat time and last use");
        Check.True(BuffItemRules.FindCustom(back.CustomBuffItems, "Gem of Waking") is { Allowed: false, SpellsRead: false }, "a blocked, unread one");

        // Profile switch/load copies them (CopySettings is the hand-written field list).
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { some, dst });
        Check.True(dst.BlockedBuffItems.Count == 1 && dst.CustomBuffItems.Count == 2, "a profile with them loads them");
        Check.False(ReferenceEquals(dst.CustomBuffItems[0], some.CustomBuffItems[0]), "as copies, not the other profile's objects");
        copy.Invoke(null, new object[] { old!, dst });
        Check.True(dst.BlockedBuffItems.Count == 0 && dst.CustomBuffItems.Count == 0, "an old profile loads none");

        // The settings bridge: sent, and an engine save without the fields leaves them alone.
        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.BlockedBuffItems == null && payload.CustomBuffItems == null, "an engine payload without them has none");
        var s = new LegacyUiSettings();
        s.SetBuffItemAllowed("Asheron's Benediction", false, out _);
        s.AddCustomBuffItem("Elixir of the Hunt", new uint[] { ElixirSpell }, out _);
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Priv)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", Priv)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", Priv)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent?.BlockedBuffItems != null && sent.BlockedBuffItems.SequenceEqual(new[] { "Asheron's Benediction" }), "the blocked list is sent");
        Check.True(sent?.CustomBuffItems is { Count: 1 } list && list[0].Name == "Elixir of the Hunt", "the custom list is sent");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.BlockedBuffItems.Count == 1 && s.CustomBuffItems.Count == 1, "a save without them leaves them alone");
        dash.ApplySettingsJson("{\"BlockedBuffItems\":[],\"CustomBuffItems\":[]}");
        Check.True(s.BlockedBuffItems.Count == 0 && s.CustomBuffItems.Count == 0, "a save with empty lists clears them");
    }
}
