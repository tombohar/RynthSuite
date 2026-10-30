using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The ILT Hub left RynthAi for the RynthILT plugin (2026-10-13). What stayed: the Mini Remote with its
// universal Quests and Pets tabs (their settings now in miniremote.json, read once from the old
// ilt-hub.json), the "/ra hub|quests|pets" commands (the ILT verbs go to RynthILT, or say it's
// needed), the RynthAi.Hub interface RynthILT borrows from, and the profile's old Server Features
// fields kept for RynthILT's one-time migration.
internal static class MiniRemoteMoveTests
{
    private const uint Player = 0x50000E01;
    /// <summary>A server plugin's Pets sub-tab id (RynthILT's Breeding; RynthAi itself never names it).</summary>
    private const string BreedingSection = "pets-breeding";

    private const string LegacyHubJson = """
        {
          "Bank": { "Pyreals": 99 },
          "Pet": { "HealingEnabled": true, "HealPetName": "Healing Buddy", "RosterKind": 1, "UsePyrealRefillPath": true,
                   "Assignments": [ { "Name": "Fire Banshee Essence", "Kind": 2 }, { "Name": "", "Kind": 1 } ], "PetLog": ["x"] },
          "Character": { "QuestFilter": "temple", "QuestFavorites": ["foo", ""], "ShowQuestFavoritesHud": true, "AttrKeepXp": 5 },
          "Gear": { "LastUsdPath": "C:\\vt\\me.usd", "AutoClap": true }
        }
        """;

    public static void Register(Runner r)
    {
        r.Add("mini remote: Pets / Quests settings come over from ilt-hub.json once, which is never rewritten", LegacyState);
        r.Add("mini remote: without RynthILT there are no ILT tabs, bits or verbs", NoRynthIlt);
        r.Add("mini remote: /ra hub|quests|pets stay RynthAi's; quest windows point to RynthOracle; other hub verbs are a server plugin's", Commands);
        r.Add("mini remote: the pyreal refill path never spends without RynthILT's confirmation", RefillNeedsIlt);
        r.Add("pets: ACECustom's charms, capture, refill gate and Breeding come from the server plugin (none without it)", PetsFromServerPlugin);
        r.Add("mini remote: the session report's tail and the menu's server come from the server plugin", RemoteTextsFromServerPlugin);
        r.Add("vaulted dungeons: the master switch is RynthILT's; RynthAi mirrors it for the engine row (none without RynthILT)", VaultedMasterSwitch);
        r.Add("profile: the old Server Features fields still round-trip (RynthILT migrates them)", LegacyProfileFields);
        r.Add("RynthAi.Hub: table, spell names, nothing before login", HubTable);
        r.Add("mini remote: the server hub is asked of the active server's owner, then of each listed owner once", HubOwnerOrder);
    }

    /// <summary>
    /// Whom ServerHubClient asks for "RynthServer.Hub" (2026-10-21: it names no plugin itself, RynthILT
    /// included): RynthCore's active server's owner first, then every other server's owner it lists.
    /// </summary>
    private static void HubOwnerOrder()
    {
        Check.Eq(ServerHubClient.OwnerOrder(null).Count, 0, "no server list (an engine before v86): nobody is asked");
        var st = EngineServerState.Parse("{\"schema\":1,\"active\":\"b\",\"activeName\":\"B\",\"activePlugin\":\"PluginB\",\"servers\":["
            + "{\"id\":\"a\",\"plugin\":\"PluginA\"},{\"id\":\"b\",\"active\":true,\"plugin\":\"PluginB\"},"
            + "{\"id\":\"c\",\"plugin\":\"\"},{\"id\":\"d\",\"plugin\":\"plugina\"}]}");
        Check.Eq(string.Join(",", ServerHubClient.OwnerOrder(st)), "PluginB,PluginA",
                 "the active server's owner first, then the others once each (none for a server without one)");
        var none = EngineServerState.Parse("{\"schema\":1,\"active\":\"\",\"servers\":[{\"id\":\"a\",\"plugin\":\"PluginA\"}]}");
        Check.Eq(string.Join(",", ServerHubClient.OwnerOrder(none)), "PluginA",
                 "no active server: the listed owners (a plugin following its own world check or override)");
    }

    private static string Folder(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "miniremote-" + name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void LegacyState()
    {
        string dir = Folder("legacy");
        string legacyPath = Path.Combine(dir, MiniRemoteStore.LegacyFileName);
        File.WriteAllText(legacyPath, LegacyHubJson);

        var store = new MiniRemoteStore(dir);
        var s = store.Load();
        Check.True(s.Pet.HealingEnabled && s.Pet.HealPetName == "Healing Buddy", "healing pet settings");
        Check.Eq(s.Pet.RosterKind, 1, "roster view");
        Check.True(s.Pet.UsePyrealRefillPath, "the refill opt-in");
        Check.Eq(s.Pet.Assignments.Count, 1, "type overrides (the blank one dropped)");
        Check.Eq(s.Quests.QuestFilter, "temple", "quest filter");
        Check.Eq(string.Join(",", s.Quests.QuestFavorites), "foo", "favorites (blanks dropped)");
        Check.True(s.Quests.ShowQuestFavoritesHud, "favorites HUD");
        Check.Eq(s.LastUsdPath, @"C:\vt\me.usd", "the .usd importer's last file");

        store.SaveIfDirty(s);
        Check.True(File.Exists(Path.Combine(dir, MiniRemoteStore.FileName)), "saved as miniremote.json");
        Check.Eq(File.ReadAllText(legacyPath), LegacyHubJson, "ilt-hub.json is left exactly as it was (RynthILT copies it)");
        string saved = File.ReadAllText(Path.Combine(dir, MiniRemoteStore.FileName));
        Check.False(saved.Contains("Pyreals") || saved.Contains("PetLog") || saved.Contains("AttrKeepXp"), "no ILT data in RynthAi's file");

        s.Quests.QuestFilter = "changed";
        store.SaveIfDirty(s);
        var again = new MiniRemoteStore(dir).Load();
        Check.Eq(again.Quests.QuestFilter, "changed", "miniremote.json wins once it exists");

        var fresh = new MiniRemoteStore(Folder("fresh")).Load();
        Check.False(fresh.Pet.HealingEnabled, "no files at all: defaults");
    }

    private static MiniRemoteController NewController(string folder, out RynthCoreHost host, out LegacyUiSettings settings, bool serverPlugin = false)
    {
        FakeHost.Reset();
        FakeServerHub.Reset();
        host = FakeHost.Create(Player);
        var s = new LegacyUiSettings();
        settings = s;
        var client = new ServerHubClient(host);
        if (serverPlugin) client.UseTableForTests(FakeServerHub.Table);
        var c = new MiniRemoteController(host, Folder(folder), () => null, () => s, () => null, () => { }, client);
        string? lastTab = null;
        c.MiniRemoteCommand = mode => "[Mini Remote] remote " + mode;
        c.MiniRemoteTabCommand = (tab, mode) => { lastTab = tab + ":" + mode; return "tab " + lastTab; };
        return c;
    }

    private static void NoRynthIlt()
    {
        var c = NewController("noilt", out var host, out _);
        var ilt = c.ServerHub;
        ilt.Tick();
        Check.False(ilt.Loaded, "no RynthILT (no plugin interfaces on this host)");
        Check.False(ilt.ServerActive, "so never an ILT world");
        Check.Eq(ilt.Feature("pet_refill"), -1, "feature bits unknown");
        Check.False(ilt.IsOff(RynthServerHub.FeatureQuests), "unknown is not off (/myquests still refreshes)");
        Check.Eq(ilt.Tabs().Count, 0, "no ILT tabs");
        Check.False(ilt.RenderSection(RynthServerHub.SectionRemoteBank), "no ILT sections");
        Check.False(ilt.Command("hub bank"), "no ILT verbs taken");
        Check.Eq(ilt.Text(RynthServerHub.TextSessionReport), string.Empty, "no server rates in the session report");
        Check.Eq(ilt.Text(RynthServerHub.TextServerId), string.Empty, "no server of a plugin's in the menu");
        Check.Eq(ilt.Setting(RynthServerHub.SettingVaultedDungeons), -1, "no server setting offered");
        Check.False(c.SkipLoginQuestRefresh, "the login /myquests refresh isn't skipped");
    }

    private static void Commands()
    {
        var c = NewController("cmds", out _, out _);
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "show" }), "hub show");
        Check.True(FakeHost.Chat.Any(x => x.Contains("remote show")), "hub show drives the Mini Remote");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "open", "pets" }), "hub open pets");
        Check.True(FakeHost.Chat.Any(x => x.Contains("tab pets:toggle")), "opens RynthAi's Pets tab");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("pets", new[] { "show" }), "pets show");
        Check.True(FakeHost.Chat.Any(x => x.Contains("tab pets:show")), "same tab");
        FakeHost.Chat.Clear();
        // The Pets tab's Breeding sub-tab is RynthILT's (Infinite Leaftide only): without RynthILT there is none.
        Check.Eq(c.Pets.SubTabs().Count, 0, "no Pets sub-tabs without a server plugin");
        Check.True(c.HandleCommand("hub", new[] { "open", "breeding" }), "hub open breeding is answered");
        Check.False(FakeHost.Chat.Any(x => x.Contains("tab pets:")), "and opens nothing");
        Check.True(c.Pets.PendingSubTab == null, "no sub-tab request");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "tab", "Breed" }), "hub tab breed (the dashboard)");
        Check.False(FakeHost.Chat.Any(), "tab switches quietly");
        Check.True(c.Pets.PendingSubTab == null, "still no sub-tab request without a server plugin");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "bank" }), "hub bank is handled (answered)");
        Check.True(FakeHost.Chat.Any(x => x.Contains("/ra hub open pets|crafting [show|hide|toggle]")), "with the help: what RynthAi has");
        Check.False(FakeHost.Chat.Any(x => x.Contains("RynthILT") || x.Contains("Leaftide")), "and nothing about ILT (a retail world's answer)");
        FakeHost.Chat.Clear();
        // The Quests tab moved to RynthOracle (2026-10-13): its verbs and tab names say so.
        Check.True(c.HandleCommand("hub", new[] { "open", "quests" }), "hub open quests is answered");
        Check.True(FakeHost.Chat.Any(x => x.Contains("RynthOracle") && x.Contains("/ro timers")), "it points to RynthOracle");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("quests", new[] { "window" }), "quests window is answered");
        Check.True(FakeHost.Chat.Any(x => x.Contains("RynthOracle")), "with the same note");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("quests", Array.Empty<string>()), "quests (refresh) stays RynthAi's");
        Check.True(FakeHost.Chat.Any(x => x.Contains("Quest tracker not ready")), "the meta tracker's refresh (none in this test)");
        FakeHost.Chat.Clear();
        Check.False(c.HandleCommand("guardian", new[] { "handin" }), "guardian isn't the Mini Remote's (a server plugin's verb)");
        Check.False(c.HandleCommand("bountys", Array.Empty<string>()), "nor bounty");
        Check.False(c.HandleCommand("start", Array.Empty<string>()), "other verbs aren't the Mini Remote's");
        Check.False(FakeHost.Chat.Any(), "and it says nothing about them");

        // With a server plugin, a hub verb RynthAi doesn't have is the plugin's.
        var ilt = NewController("cmds-ilt", out _, out _, serverPlugin: true);
        FakeServerHub.TakesCommand = line => line.StartsWith("hub bank", StringComparison.Ordinal);
        FakeHost.Chat.Clear();
        Check.True(ilt.HandleCommand("hub", new[] { "bank" }), "hub bank");
        Check.Eq(FakeServerHub.Commands.Count, 1, "went to the server plugin");
        Check.False(FakeHost.Chat.Any(), "RynthAi adds nothing (the plugin answers itself)");
    }

    private static void RefillNeedsIlt()
    {
        var c = NewController("refill", out _, out _);
        c.State.Pet.UsePyrealRefillPath = true;            // the old (obsolete) opt-in in miniremote.json
        FakeHost.Bools[(Player, 9049)] = true;              // ACECustom's refill player bool
        Check.False(c.Pets.AllowSummonOnEmpty(), "not an ILT world (no RynthILT): an empty essence is never summoned on pyreals");
    }

    /// <summary>
    /// The Remote tab's server texts with a server plugin (2026-10-20): the session report's tail and the
    /// server its menu entries belong to come from the plugin, as they are.
    /// </summary>
    private static void RemoteTextsFromServerPlugin()
    {
        var c = NewController("texts", out _, out _, serverPlugin: true);
        Check.Eq(c.ServerHub.Text(RynthServerHub.TextSessionReport), string.Empty, "nothing until the plugin says something");
        FakeServerHub.Texts[RynthServerHub.TextSessionReport] = ", Coins 12/h, Pyr 3.4m/h";
        FakeServerHub.Texts[RynthServerHub.TextServerId] = "ilt";
        Check.Eq(c.ServerHub.Text(RynthServerHub.TextSessionReport), ", Coins 12/h, Pyr 3.4m/h", "the report's tail is the plugin's");
        Check.Eq(c.ServerHub.Text(RynthServerHub.TextServerId), "ilt", "and the server its menu entries belong to");
    }

    /// <summary>
    /// The Pets tab with and without a server plugin (2026-10-20): ACECustom's pet charms, siphon
    /// auto-capture, the summon-from-empty gate and the Breeding sub-tab are RynthILT's. Without it the
    /// tab asks nothing of the kind; with it the gate is RynthILT's answer and its sub-tab opens by name.
    /// </summary>
    private static void PetsFromServerPlugin()
    {
        var plain = NewController("pets-plain", out _, out _);
        Check.False(plain.ServerHub.Loaded, "no server plugin");
        Check.False(plain.Pets.AllowSummonOnEmpty(), "no summon from empty");
        Check.Eq(plain.Pets.FindSubTab("breeding"), null, "no Breeding");

        var c = NewController("pets-ilt", out _, out _, serverPlugin: true);
        FakeServerHub.Active = true;
        Check.True(c.ServerHub.Loaded && c.ServerHub.ServerActive, "the fake RynthILT is there, on its world");
        Check.False(c.Pets.AllowSummonOnEmpty(), "RynthILT says no: no summon from empty");
        FakeServerHub.AllowEmpty = true;
        Check.True(c.Pets.AllowSummonOnEmpty(), "RynthILT says yes (charm on, opted in, confirmed): summon from empty");
        Check.Eq(c.Pets.SubTabs().Count, 0, "no sub-tabs until RynthILT offers one");
        FakeServerHub.SubTabs[RynthServerHub.TabParentPets] = new() { (BreedingSection, "Breeding") };
        Check.Eq(c.Pets.SubTabs().Count, 1, "RynthILT's Breeding sub-tab");
        Check.Eq(c.Pets.FindSubTab("breeding"), BreedingSection, "found by its short id");
        Check.Eq(c.Pets.FindSubTab("Breed"), BreedingSection, "and the 'breed' alias");
        Check.Eq(c.Pets.FindSubTab("roster"), null, "the roster isn't a server sub-tab");
        FakeHost.Chat.Clear();
        Check.True(c.HandleCommand("hub", new[] { "open", "breeding" }), "hub open breeding");
        Check.True(FakeHost.Chat.Any(x => x.Contains("tab pets:show")), "shows the Pets tab");
        Check.Eq(c.Pets.PendingSubTab, BreedingSection, "on the Breeding sub-tab");
        Check.Eq(FakeServerHub.Commands.Count, 0, "RynthAi handled it itself (nothing forwarded)");
    }

    private static void LegacyProfileFields()
    {
        var s = JsonSerializer.Deserialize("{\"FeatureServerNames\":[\"MyShard\"],\"ForceServerFeatures\":true}",
            RynthAiJsonContext.Default.LegacyUiSettings)!;
        string json = JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(json.Contains("MyShard"), "the old server list is written back unchanged");
        Check.True(json.Contains("\"ForceServerFeatures\": true"), "and the old override");
        var fresh = JsonSerializer.Serialize(new LegacyUiSettings(), RynthAiJsonContext.Default.LegacyUiSettings);
        Check.False(fresh.Contains("InfiniteLeaftide"), "a fresh profile no longer gets an ILT server list");
    }

    /// <summary>
    /// "Vaulted Dungeons" (the engine's Settings > Navigation row) is RynthILT's keeper master switch, and
    /// since 2026-10-20 RynthILT's own setting (copied once from the profile, whose old field still loads
    /// and is carried, unused). RynthAi only mirrors it: without a server plugin offering it the row gets
    /// null (hidden) and a save changes nothing; with one, the row shows its value and a save goes to it.
    /// </summary>
    private static void VaultedMasterSwitch()
    {
        var oldProfile = JsonSerializer.Deserialize("{\"EnableVaultedDungeons\":true}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(oldProfile.EnableVaultedDungeons, "an old profile's switch still loads (RynthILT copies it once)");
        string json = JsonSerializer.Serialize(oldProfile, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(json.Contains("\"EnableVaultedDungeons\": true"), "and is written back unchanged");

        var s = new LegacyUiSettings();
        var dash = (LegacyDashboardRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(LegacyDashboardRenderer).GetField("_settings", flags)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", flags)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));

        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(sent.EnableVaultedDungeons == null, "no server plugin: the row is sent nothing (it hides)");
        dash.ApplySettingsJson("{\"EnableVaultedDungeons\":true}");
        Check.False(s.EnableVaultedDungeons, "and a save doesn't touch RynthAi's old field");

        // A server plugin offering the switch (RynthILT, through the Mini Remote's server client).
        FakeHost.Reset();
        FakeServerHub.Reset();
        var client = new ServerHubClient(FakeHost.Create(Player));
        client.UseTableForTests(FakeServerHub.Table);
        dash.ServerSettingGet = key => client.Setting(key);
        dash.ServerSettingSet = (key, value) => client.SetSetting(key, value);
        sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(sent.EnableVaultedDungeons == null, "RynthILT not offering it (off ILT): still hidden");
        FakeServerHub.Settings[RynthServerHub.SettingVaultedDungeons] = 0;
        sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(sent.EnableVaultedDungeons == false, "offered: the row shows RynthILT's value (off)");
        dash.ApplySettingsJson("{\"EnableVaultedDungeons\":true}");
        Check.Eq(FakeServerHub.Settings[RynthServerHub.SettingVaultedDungeons], 1, "the checkbox turns RynthILT's switch on");
        dash.ApplySettingsJson("{\"NavRingThickness\":6}");
        Check.Eq(FakeServerHub.Settings[RynthServerHub.SettingVaultedDungeons], 1, "an older engine's save (no such field) leaves it alone");
        Check.False(s.EnableVaultedDungeons, "RynthAi's old field is never written");
    }

    private static unsafe void HubTable()
    {
        Check.Eq(HubInterface.Query("RynthILT.Hub", 1), IntPtr.Zero, "not ours");
        var t = (RynthAiHubApiV1*)HubInterface.Query(RynthAiHubApiV1.InterfaceName, 1);
        Check.True(t != null && t->Version == 1 && t->StructSize == (uint)sizeof(RynthAiHubApiV1), "v1 served");
        Check.True(t->MiniRemoteTab != null && t->MiniRemoteVisible != null && t->QuestFlag != null && t->QuestStatus != null
                   && t->QuestRefresh != null && t->SpellName != null && t->LootKeeps != null && t->RenderSection != null
                   && t->SessionStats != null && t->SessionReset != null, "every function filled in");

        byte* buf = stackalloc byte[64];
        int n = t->SpellName(1, buf, 64);
        Check.Eq(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buf, n), "Strength Other I", "spell names from RynthAi's table");
        Check.Eq(t->SpellName(0, buf, 64), 0, "spell 0 has no name");
        QuestStatusV1 st;
        Check.Eq(t->QuestStatus(&st), 0, "no quest tracker before login");
        Check.Eq(t->MiniRemoteVisible(), 0, "no Mini Remote before login");
        Check.True(t->VaultedDungeonsEnabled != null, "the Vaulted Dungeons master switch is served");
        Check.Eq(t->VaultedDungeonsEnabled(), 0, "the master switch is off before login (and by default)");
        Check.True(RynthAiHubApiV1.Has(t, RynthAiHubApiV1.BackAwayFromOffset) && t->BackAwayFrom != null, "the back-away is served (appended 2026-10-20)");
        Check.Eq(t->BackAwayFrom(1, 12f, 750), RynthAiHub.BackAwayNotNow, "not now before login");
        Check.True(RynthAiHubApiV1.Has(t, RynthAiHubApiV1.SetPlayerCounterOffset) && t->SetPlayerCounter != null, "the wield counters are served (appended 2026-10-20)");
        RynthCore.Plugin.RynthAi.Loot.T11ItemSupport.Reset();
        Check.Eq(t->SetPlayerCounter(9008, 3100), 1, "item augmentations taken");
        Check.Eq(RynthCore.Plugin.RynthAi.Loot.T11ItemSupport.Player.Get(RynthCore.Loot.T11.T11Counter.ItemAugmentations), 3100L, "and the loot rules see them");
        Check.Eq(t->SetPlayerCounter(42, 1), 0, "not a counter: refused");
        RynthCore.Plugin.RynthAi.Loot.T11ItemSupport.Reset();
        SessionStatsV1 ss;
        Check.Eq(t->SessionStats(&ss), 0, "no session before login");
    }
}
