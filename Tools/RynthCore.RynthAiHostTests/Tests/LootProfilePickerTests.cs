using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-22: the loot profile dropdown in more places (the Mini Remote's Loot row, the top of Settings >
// Looting). Every picker shows one list (LootProfileList: "None", then LootProfiles' *.utl and *.json)
// and switches through SelectProfileAtIndex, which saves the choice and says so in chat.
internal static class LootProfilePickerTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("loot profile pickers: the list is None, then *.utl and *.json, sorted", Listing);
        r.Add("loot profile pickers: the profile in use is the one selected", Selection);
        r.Add("loot profile pickers: a pick switches, saves the index and says so in chat", Switching);
        r.Add("loot profile pickers: a file copied in is listed on the next re-list", RelistAfterCopy);
        r.Add("loot profile pickers: the Mini Remote's Loot row is on, also for an older huds.json", MiniRemoteFlag);
    }

    private static string Dir(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "loot-picker", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string dir, params string[] names)
    {
        foreach (string n in names) File.WriteAllText(Path.Combine(dir, n), "x");
    }

    private static void Listing()
    {
        string dir = Dir("list");
        Touch(dir, "zeta.utl", "Alpha.utl", "beta.json", "notes.txt", "old.utl.bak", "Mid.UTL");
        var list = LootProfileList.Build(dir);
        Check.Eq(string.Join("|", list), "None|Alpha.utl|beta.json|Mid.UTL|zeta.utl", "None first, then the loot files by name, any case");
        Check.Eq(string.Join("|", LootProfileList.Build(Path.Combine(dir, "missing"))), "None", "no folder: None alone");
        Check.Eq(string.Join("|", LootProfileList.Build(null)), "None", "no folder given: None alone");
        Check.Eq(LootProfileList.IndexOf(list, "MID.utl"), 3, "found by name, any case");
        Check.Eq(LootProfileList.IndexOf(list, "gone.utl"), -1, "not listed");
    }

    private static void Selection()
    {
        string dir = Dir("select");
        Touch(dir, "Alpha.utl", "Beta.utl");
        var list = LootProfileList.Build(dir);
        Check.Eq(LootProfileList.SelectedIndex(list, dir, "", 2), 0, "nothing in use: None");
        Check.Eq(LootProfileList.SelectedIndex(list, dir, Path.Combine(dir, "Beta.utl"), 0), 2, "the file in use");
        Check.Eq(LootProfileList.SelectedIndex(list, dir, "\"" + Path.Combine(dir, "ALPHA.utl") + "\"", 0), 1, "quotes and case");
        Check.Eq(LootProfileList.SelectedIndex(list, dir, Path.Combine(Program.TempRoot, "Beta.utl"), 1), 1,
            "a file of the same name in another folder isn't this one: the old index stays");
        Check.Eq(LootProfileList.SelectedIndex(list, dir, @"C:\elsewhere\x.utl", 9), 2, "...kept in the list's range");
        Check.Eq(LootProfileList.ShownName(Path.Combine(dir, "Beta.utl")), "Beta.utl", "shown: the file name");
        Check.Eq(LootProfileList.ShownName(" "), "None", "shown: None");
        Check.Eq(LootProfileList.SwitchedLine(Path.Combine(dir, "Beta.utl")), "[RynthAi] Loot profile: Beta.utl.", "the chat line");
        Check.True(LootProfileList.SwitchedLine("").Contains("None", StringComparison.Ordinal), "the chat line for None");
    }

    /// <summary>A dashboard whose LootProfiles folder is <paramref name="loot"/>, listed.</summary>
    private static LegacyDashboardRenderer Dash(string loot, out LegacyUiSettings s)
    {
        FakeHost.Reset();
        DataWriteGuard.Reset();
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        typeof(LegacyDashboardRenderer).GetField("_lootFolder", Any)!.SetValue(dash, loot);
        s = (LegacyUiSettings)typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.GetValue(dash)!;
        Relist(dash);
        return dash;
    }

    private static void Relist(LegacyDashboardRenderer dash) =>
        typeof(LegacyDashboardRenderer).GetMethod("RefreshLootFiles", Any)!.Invoke(dash, null);

    private static void Switching()
    {
        string loot = Dir("switch");
        Touch(loot, "Alpha.utl", "Beta.utl");
        var dash = Dash(loot, out var s);
        Check.Eq(string.Join("|", dash.LootProfileNames()), "None|Alpha.utl|Beta.utl", "the pickers' list");

        FakeHost.Chat.Clear();
        dash.SelectLootProfileByName("beta.utl");
        Check.Eq(s.CurrentLootPath, Path.Combine(loot, "Beta.utl"), "Beta in use (the Mini Remote picks by name)");
        Check.Eq(s.LootProfileIdx, 2, "the pickers show it");
        Check.Eq(dash.ReadDashboardFiles().Loots[s.LootProfileIdx], "Beta.utl", "the Loaded files drawer shows the same entry");
        Check.Eq(FakeHost.Chat.Count(l => l == "[RynthAi] Loot profile: Beta.utl."), 1, "chat says so");

        FakeHost.Chat.Clear();
        dash.SelectProfileAtIndex(1, 2);
        Check.False(FakeHost.Chat.Any(l => l.Contains("Loot profile:", StringComparison.Ordinal)), "picking the one in use again: nothing to say");

        dash.SelectProfileAtIndex(1, 1);   // the dashboard / Settings > Looting path (by index)
        Check.Eq(s.CurrentLootPath, Path.Combine(loot, "Alpha.utl"), "Alpha in use");
        Check.Eq(FakeHost.Chat.Count(l => l == "[RynthAi] Loot profile: Alpha.utl."), 1, "chat says so for every picker");

        dash.SelectProfileAtIndex(1, 0);
        Check.Eq(s.CurrentLootPath, string.Empty, "None: no loot profile");
        Check.Eq(s.LootProfileIdx, 0, "None shown");

        FakeHost.Chat.Clear();
        dash.SelectLootProfileByName("Gone.utl");
        Check.Eq(s.CurrentLootPath, string.Empty, "a name no longer listed changes nothing");
        Check.True(FakeHost.Chat.Any(l => l.Contains("no longer in LootProfiles", StringComparison.Ordinal)), "...and says why");
    }

    private static void RelistAfterCopy()
    {
        string loot = Dir("relist");
        Touch(loot, "Beta.utl");
        var dash = Dash(loot, out var s);
        dash.SelectLootProfileByName("Beta.utl");
        Check.Eq(s.LootProfileIdx, 1, "Beta is entry 1");

        Touch(loot, "Alpha.utl", "Gamma.json");
        Relist(dash);   // TickNavFolder does this within 3 s of the folder changing
        Check.Eq(string.Join("|", dash.LootProfileNames()), "None|Alpha.utl|Beta.utl|Gamma.json", "the new files are listed");
        Check.Eq(s.LootProfileIdx, 2, "the selection follows Beta to its new place");

        File.Delete(Path.Combine(loot, "Beta.utl"));
        Relist(dash);
        Check.True(s.LootProfileIdx >= 0 && s.LootProfileIdx < dash.LootProfileNames().Length, "the profile in use was removed: the index stays in range");
    }

    private static void MiniRemoteFlag()
    {
        Check.True(new HudState().MiniShowLootProfile, "on by default");
        HudState? old = JsonSerializer.Deserialize("{\"ShowMiniRemote\":true,\"MiniShowGems\":false}", RynthAiJsonContext.Default.HudState);
        Check.True(old != null && old.MiniShowLootProfile, "a huds.json from before it has the Loot row on");
        HudState? off = JsonSerializer.Deserialize("{\"MiniShowLootProfile\":false}", RynthAiJsonContext.Default.HudState);
        Check.True(off != null && !off.MiniShowLootProfile, "hidden from Options stays hidden");
    }
}
