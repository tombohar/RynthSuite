// ============================================================================
//  RynthSuite - Plugins/Shared/RynthServerHubApi.cs
//  The "RynthServer.Hub" interface: a server plugin's part of RynthAi's Mini
//  Remote. The Mini Remote stays RynthAi's (its window, tab bar, Remote, Pets and
//  Crafting tabs) and knows no server's content: the active server's plugin
//  supplies its tabs and the Pets tab's sub-tabs, its sections drawn inside
//  RynthAi's surfaces (Remote, Pets, Options, menu), its feature bits, texts and
//  mirrored settings, its "/ra" verbs and the pet summon-from-empty gate.
//
//  RynthAi asks the plugin that owns RynthCore's active server (plugin API v86,
//  GetServerStateJson "activePlugin") for this interface, else the owners of the
//  other servers RynthCore lists (a server plugin may follow its own world check).
//  A server plugin answers it with the table below; RynthILT also serves the same
//  table under its own older name (RynthIltHubApi.cs).
//
//  The ids and the layout are the wire format: they kept their values and order
//  when the interface was split out of RynthIltHubApi.cs (2026-10-21), so every
//  copy of RynthILT since its first release still fits.
//  Compiled into RynthAi and the server plugins that serve it (linked source), so
//  the layout can't drift.
//
//  Rules (as the RynthAi.* interfaces, Shared/RynthAiScriptApi.cs):
//  - Append-only: new functions go at the end, and Version goes up.
//  - Strings are UTF-8, NUL-terminated.
//  - Threads: each function says where it may be called. The render-thread ones
//    draw into the caller's current ImGui window and context (the engine's cimgui,
//    shared by both plugins).
//  - Every function returns safely (no exception crosses the boundary), and returns
//    0 / "unknown" once the serving plugin has shut down.
//  - The table is allocated once per loaded copy of the serving plugin and never
//    freed: its functions stay callable from the render thread while plugins shut
//    down (a plugin DLL is never unloaded, see the engine's
//    PluginManager.PluginsUnloaded). Shutdown sets Version to 0; a caller
//    re-queries (host GetPluginInterface) when it sees that, after a plugin
//    reload, or every few seconds.
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

/// <summary>Constants of the "RynthServer.Hub" interface (section, tab, text, feature and setting ids).</summary>
public static class RynthServerHub
{
    /// <summary>Tab flag: the Mini Remote shows this tab now (the plugin's server, or forced, and the tab is usable).</summary>
    public const uint TabShown = 1;
    /// <summary>Tab flag: the server reports the tab's feature on.</summary>
    public const uint TabAvailable = 2;

    // Sections RynthAi draws through RenderSection, each in one of its own surfaces. A plugin draws
    // nothing (returns 0) for a section it doesn't have, or off its server.
    /// <summary>Remote tab, above the rebuff line: the server's bank balances.</summary>
    public const string SectionRemoteBank = "remote-bank";
    /// <summary>Remote tab, under the translate line: a line of the server's own (e.g. a quest NPC's last answer).</summary>
    public const string SectionRemoteGuardian = "remote-guardian";
    /// <summary>Remote tab, Stats section, under RynthAi's per-hour lines: the server's own session rates.</summary>
    public const string SectionRemoteStats = "remote-stats";
    /// <summary>
    /// The Remote tab's Options popup, under RynthAi's own section switches: the switches of the server's
    /// Remote sections (saved by the plugin, which then draws or skips SectionRemoteBank / SectionRemoteGuardian).
    /// </summary>
    public const string SectionRemoteOptions = "remote-options";
    /// <summary>Pets tab, under the roster: the server's pet extras (charms, refills). Draws its own leading separator.</summary>
    public const string SectionPetCharms = "pets-charms";
    /// <summary>Pets tab, under SectionPetCharms: the server's monster capture (as SectionPetCharms).</summary>
    public const string SectionPetCapture = "pets-capture";
    /// <summary>Pets tab, last: the server's pet logs.</summary>
    public const string SectionPetLogs = "pets-logs";
    /// <summary>
    /// Mini Remote menu, under the plugin's server (TextServerId) in RynthCore's server list: the plugin's
    /// status (why no feature is on, its override, Refresh). The id keeps its first owner's name.
    /// </summary>
    public const string SectionServerStatus = "ilt-status";
    /// <summary>Mini Remote menu without RynthCore's server list (an older RynthCore): the plugin's own menu items.</summary>
    public const string SectionServerMenu = "server-menu";

    /// <summary>GetSubTabs parent: RynthAi's Pets tab (its first sub-tab, the roster, is RynthAi's own).</summary>
    public const string TabParentPets = "pets";

    /// <summary>GetText id: the tail RynthAi adds to its one-line session report ("" or ", Coins 1.2k/h, Pyr 4.5m/h").</summary>
    public const string TextSessionReport = "session-report";
    /// <summary>GetText id: the RynthCore server id this plugin's tabs and menu entries belong to.</summary>
    public const string TextServerId = "server-id";

    /// <summary>Feature key RynthAi reads (GetFeature): the server's /myquests (0 = off: no login refresh).</summary>
    public const string FeatureQuests = "quests";

    /// <summary>
    /// GetSetting / SetSetting key: the server plugin's Vaulted Dungeons switch (0 off, 1 on), which RynthAi
    /// mirrors into the engine's Settings > Navigation row; -1 (no row) when the plugin doesn't offer it.
    /// </summary>
    public const string SettingVaultedDungeons = "vaulted-dungeons";
}

/// <summary>One tab as GetTabs / GetSubTabs hand it out. Blittable.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ServerHubTabV1
{
    /// <summary>The tab id (UTF-8, NUL-terminated): saved as the Mini Remote's tab, "/ra hub open &lt;id&gt;".</summary>
    public fixed byte Id[24];
    /// <summary>The tab bar label (UTF-8, NUL-terminated).</summary>
    public fixed byte Label[32];
    /// <summary>The size of the tab body (the child it draws in).</summary>
    public float Width;
    public float Height;
    /// <summary>RynthServerHub.Tab* bits.</summary>
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthServerHubApiV1
{
    public const string InterfaceName = "RynthServer.Hub";
    public const uint InterfaceVersion = 1;

    /// <summary>
    /// The table's size up to and including Command, as the interface's first release filled it.
    /// Readers check StructSize against this (not sizeof, which grows as functions are appended) and
    /// check for the appended functions with <see cref="Has"/>.
    /// </summary>
    public static uint BaseStructSize => (uint)(2 * sizeof(uint) + 9 * sizeof(nint));

    /// <summary>Whether a table is big enough to hold the function at <paramref name="offset"/>.</summary>
    public static bool Has(RynthServerHubApiV1* t, int offset) => t != null && t->StructSize >= (uint)(offset + sizeof(nint));

    /// <summary>Byte offset of the n-th function appended after the base table (0 = AllowSummonOnEmpty).</summary>
    private static int Appended(int n) => (int)BaseStructSize + n * sizeof(nint);

    /// <summary>Offset of <see cref="AllowSummonOnEmpty"/> (for <see cref="Has"/>).</summary>
    public static int AllowSummonOnEmptyOffset => Appended(0);
    /// <summary>Offset of <see cref="GetSubTabs"/>.</summary>
    public static int GetSubTabsOffset => Appended(1);
    /// <summary>Offset of <see cref="GetSetting"/>.</summary>
    public static int GetSettingOffset => Appended(2);
    /// <summary>Offset of <see cref="SetSetting"/>.</summary>
    public static int SetSettingOffset => Appended(3);
    /// <summary>Offset of <see cref="GetText"/>.</summary>
    public static int GetTextOffset => Appended(4);

    public uint Version;      // InterfaceVersion of the table the plugin filled; 0 once it has shut down
    public uint StructSize;   // sizeof(RynthServerHubApiV1) as the plugin built it

    /// <summary>IsServerWorld(): 1 when this world is the plugin's server (or forced there). Any thread.</summary>
    public delegate* unmanaged[Cdecl]<int> IsServerWorld;

    /// <summary>GetFeature(key): the server's bit for a feature, -1 unknown, 0 off, 1 on (-1 off its server). Any thread.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> GetFeature;

    /// <summary>
    /// GetRates(first, second): two session rates of the server's (per hour); 0 (nothing written) off its
    /// server. Any thread. RynthAi no longer reads it (SectionRemoteStats draws them); kept for the layout.
    /// </summary>
    public delegate* unmanaged[Cdecl]<double*, double*, int> GetRates;

    /// <summary>GetForceServer(): this character's "treat this world as the plugin's server" override (1 on). Any thread.</summary>
    public delegate* unmanaged[Cdecl]<int> GetForceServer;

    /// <summary>SetForceServer(on): sets that override. Any thread (applied and saved on the plugin's next tick).</summary>
    public delegate* unmanaged[Cdecl]<int, void> SetForceServer;

    /// <summary>
    /// GetTabs(tabs, capacity): every tab of the plugin's, in tab-bar order with its flags. Writes up to
    /// capacity, returns how many there are (tabs null: just the count). Render thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<ServerHubTabV1*, int, int> GetTabs;

    /// <summary>RenderTab(id): draws that tab's body into the current (child) window; 1 = drawn. Render thread.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> RenderTab;

    /// <summary>RenderSection(id): draws a RynthServerHub.Section* (or a sub-tab's id) into the current window; 1 = drawn. Render thread.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int> RenderSection;

    /// <summary>
    /// Command(text): one of the plugin's "/ra" verbs, as typed after "/ra" ("hub bank", "rates reset").
    /// The plugin answers in chat itself. 1 = handled, 0 = not one of its verbs. Pump thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int> Command;

    // ── Appended 2026-10-20 (check with Has before calling) ─────────────────

    /// <summary>
    /// AllowSummonOnEmpty(): 1 when a pet may be summoned from an EMPTY essence because the server
    /// refills it (the player opted in and the server confirmed the feature). RynthAi's PetManager
    /// hook asks this; 0 off the plugin's server and whenever unsure (an unknown state never spends
    /// pyreals). Any thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<int> AllowSummonOnEmpty;

    /// <summary>
    /// GetSubTabs(parent, tabs, capacity): the sub-tabs the plugin adds to one of RynthAi's tabs (parent
    /// RynthServerHub.TabParentPets). Each tab's Id is a section id drawn with RenderSection; Width /
    /// Height are 0 (the parent's size). Writes up to capacity, returns how many (tabs null: the count);
    /// 0 off its server or for an unknown parent. Render thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, ServerHubTabV1*, int, int> GetSubTabs;

    /// <summary>
    /// GetSetting(key): one of the plugin's settings that another plugin shows (RynthServerHub.Setting*;
    /// RynthAi mirrors them into the engine's Settings window). The value, or -1 when the plugin has no
    /// such setting to offer now (unknown key, before login, off its server). Any thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, int> GetSetting;

    /// <summary>SetSetting(key, value): changes that setting (saved by the plugin); 1 = taken, 0 = not ours / not now. Any thread.</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, int> SetSetting;

    /// <summary>
    /// GetText(id, buffer, capacity): a short text RynthAi shows (RynthServerHub.Text*), UTF-8, truncated to
    /// capacity. Returns its length; 0 for nothing (unknown id, off its server). Any thread.
    /// </summary>
    public delegate* unmanaged[Cdecl]<byte*, byte*, int, int> GetText;
}
