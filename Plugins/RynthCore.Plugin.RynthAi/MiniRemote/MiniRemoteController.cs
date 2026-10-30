// MiniRemoteController.cs — The Mini Remote's universal tabs (Pets, Crafting), the VTank .usd
// importer and the "/ra hub|quests|pets" commands. The universal half of the ILT Hub's
// IltHubController (SilentKelpie's ILT Hub; its Infinite Leaftide half went to the RynthILT plugin
// 2026-10-13). The Quests tab and its favorites HUD moved to RynthOracle (Quests tab, Quest Timers
// window) on 2026-10-13; "/ra quests [refresh]" (the meta tracker's quiet /myquests, which
// RynthILT's Temple tracker also asks for through RynthAi.Hub QuestRefresh) stays here.
//
// RynthAiPlugin creates one controller per login session and forwards:
//   OnTick → Tick()             (pump thread)
//   OnChatWindowText → OnChat() (pump thread)
//   OnRender → Render()         (render thread: confirmations)
//   /ra hub ..., /ra quests ..., /ra pets ... → HandleCommand()
//   OnLogout / teardown → OnLogout()
// The Mini Remote's tab bar (MiniRemoteHud) shows Remote, then Pets and Crafting (here, every
// world), then the active server plugin's tabs (ServerHubClient; RynthILT's on Infinite Leaftide). The
// "/ra hub" verbs RynthAi doesn't have ("/ra hub bank", "/ra hub open banking", ...) go to that
// plugin (RynthILT.Hub Command); without one they show the help, as on a retail world.
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

internal sealed class MiniRemoteController
{
    private const long SaveIntervalMs = 5000;
    private const string ConfirmPopupId = "Confirm##mrconfirm";
    /// <summary>The server answers one /myquests a minute; quiet refreshes keep to that.</summary>
    private const long QuestRefreshCooldownMs = 60_000;

    /// <summary>Where the quest UI went (chat reply for the retired "/ra quests" verbs and tab names).</summary>
    public const string QuestsMovedText =
        "[Mini Remote] Quests moved to RynthOracle: /ro (Quests tab: timers, tracking, rewards), /ro timers (Quest Timers window).";

    private readonly MiniRemoteContext _ctx;
    private readonly Func<QuestTracker?> _quests;
    private long _lastSaveAt;
    private long _lastQuestRefreshAt = -QuestRefreshCooldownMs;
    private MiniRemoteConfirm? _confirm;   // render thread
    private bool _confirmOpened;

    public PetsTab Pets { get; }
    /// <summary>The Crafting tab (tinker benches and server salvage; any world). SilentKelpie's.</summary>
    public CraftingTab Crafting { get; }
    public VTankUsdImporter Usd { get; }

    public MiniRemoteController(RynthCoreHost host, string charFolder, Func<WorldObjectCache?> cache,
                                Func<LegacyUiSettings?> settings, Func<QuestTracker?> quests, Action saveCombatSettings, ServerHubClient serverHub)
    {
        var store = new MiniRemoteStore(charFolder);
        var state = store.Load();
        var inventory = new MiniRemoteInventory(host, cache);
        _ctx = new MiniRemoteContext(host, state, store, inventory, serverHub, settings) { SaveCombatSettings = saveCombatSettings };
        _quests = quests;
        Pets = new PetsTab(_ctx);
        Crafting = new CraftingTab(_ctx);
        Usd = new VTankUsdImporter(_ctx);
        _ctx.ShowTab = tab => MiniRemoteTabCommand?.Invoke(tab, "show");
    }

    public MiniRemoteState State => _ctx.State;

    /// <summary>The server plugin's hub (its world check, tabs, sections, verbs; RynthILT on Infinite Leaftide).</summary>
    public ServerHubClient ServerHub => _ctx.ServerHub;

    /// <summary>Shows / hides / toggles the Mini Remote ("show", "hide", "toggle"). Set by RynthAiPlugin; returns the chat reply.</summary>
    public Func<string, string>? MiniRemoteCommand { get; set; }

    /// <summary>
    /// Puts the Mini Remote on a tab (tab id, "" = the remote itself; mode "show" / "hide" /
    /// "toggle", or "select"). Set by RynthAiPlugin; returns the chat reply. Any thread.
    /// </summary>
    public Func<string, string, string>? MiniRemoteTabCommand { get; set; }

    /// <summary>True when the server plugin already says /myquests is off on its server (skip the login refresh).</summary>
    public bool SkipLoginQuestRefresh => _ctx.ServerHub.ServerActive && _ctx.ServerHub.IsOff(RynthServerHub.FeatureQuests);

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Login: remember the character and hook PetManager to the Pets tab.</summary>
    public void OnLoginComplete(string charName, PetManager? petManager)
    {
        _ctx.CharName = charName ?? string.Empty;
        _ctx.Inventory.Reset();
        if (petManager != null)
        {
            petManager.AllowSummonOnEmpty = Pets.AllowSummonOnEmpty;
            petManager.HoldSummons = Pets.HoldCombatSummons;
            petManager.YieldPetForCombat = Pets.YieldPetForCombat;
        }
    }

    /// <summary>Pump-thread tick.</summary>
    public void Tick()
    {
        long now = MiniRemoteContext.NowMs;
        _ctx.DrainPosted();
        try { _ctx.PollServer(now); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Crafting, ex, "MiniRemoteContext.PollServer"); }
        try { Pets.Tick(now); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Pets, ex, "PetsTab.Tick"); }
        try { Crafting.Tick(now); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Crafting, ex, "CraftingTab.Tick"); }
        if (now - _lastSaveAt >= SaveIntervalMs)
        {
            _lastSaveAt = now;
            _ctx.Store.SaveIfDirty(_ctx.State);
        }
    }

    /// <summary>Pump-thread chat line (the heal pet's heal). Never eats.</summary>
    public void OnChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Pets.OnChat(text.TrimEnd('\r', '\n')); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Pets, ex, "PetsTab.OnChat"); }
        try { Crafting.OnChat(text.TrimEnd('\r', '\n')); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Crafting, ex, "CraftingTab.OnChat"); }
    }

    /// <summary>Logout / teardown: stop the pet automation and flush to disk.</summary>
    public void OnLogout()
    {
        try { Pets.OnLogout(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Pets, ex, "PetsTab.OnLogout"); }
        try { Crafting.OnLogout(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Crafting, ex, "CraftingTab.OnLogout"); }
        _ctx.ClearServer();
        _ctx.Store.SaveIfDirty(_ctx.State);
    }

    /// <summary>
    /// A quiet /myquests refresh of the meta tracker with a 60 s client-side cooldown (the server's
    /// throttle): "/ra quests refresh" and RynthILT's Temple tracker (RynthAi.Hub QuestRefresh).
    /// Pump thread. Returns the chat reply.
    /// </summary>
    public string RefreshQuests()
    {
        RynthLog.Trace(LogCat.Quests, "RefreshQuests()");
        QuestTracker? t = _quests();
        if (t == null) return "Quest tracker not ready.";
        if (_ctx.ServerHub.IsOff(RynthServerHub.FeatureQuests) || t.Disabled) return "/myquests is disabled on this server.";
        long now = MiniRemoteContext.NowMs;
        if (now - _lastQuestRefreshAt < QuestRefreshCooldownMs)
            return $"Wait {(QuestRefreshCooldownMs - (now - _lastQuestRefreshAt)) / 1000}s (server allows one /myquests per minute).";
        _lastQuestRefreshAt = now;
        t.Refresh(quiet: true);
        return "Refreshing quests...";
    }

    // ── Rendering (render thread) ───────────────────────────────────────────

    /// <summary>One of the Mini Remote's own tabs ("pets", "crafting"); false for anything else.</summary>
    public bool RenderTab(string id)
    {
        switch (id)
        {
            case MiniRemoteTabs.Pets: Pets.Render(); return true;
            case MiniRemoteTabs.Crafting: Crafting.Render(); return true;
            default: return false;
        }
    }

    /// <summary>The floating extras: the confirmation modal.</summary>
    public void Render()
    {
        RenderConfirm();
    }

    private void RenderConfirm()
    {
        if (_confirm == null)
        {
            if (!_ctx.TryTakeConfirm(out var next)) return;
            _confirm = next;
            _confirmOpened = false;
        }
        if (!_confirmOpened)
        {
            ImGui.OpenPopup(ConfirmPopupId);
            _confirmOpened = true;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
        bool keepOpen = true;
        if (ImGui.BeginPopupModal(ConfirmPopupId, ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            SafeImGui.TextColored(LegacyDashboardRenderer.ColAmber, _confirm.Title);
            ImGui.Separator();
            SafeImGui.TextWrapped(_confirm.Message);
            ImGui.Spacing();
            if (ImGui.Button(_confirm.YesLabel + "##mryes", new Vector2(120, 0)))
            {
                var act = _confirm.OnYes;
                if (act != null) _ctx.Post(act);
                _confirm = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##mrno", new Vector2(120, 0)))
            {
                _confirm = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        else
        {
            // Closed with the title-bar X (or never opened): treat as cancel.
            _confirm = null;
        }
        if (!keepOpen) _confirm = null;
    }

    // ── Commands ────────────────────────────────────────────────────────────

    /// <summary>
    /// "/ra hub [show|hide|toggle|open pets|crafting [show|hide|toggle]|open|tab &lt;a server plugin's Pets
    /// sub-tab&gt;|tab pets|crafting|remote]", "/ra quests [refresh]", "/ra pets [show|hide|toggle]"; every
    /// other "/ra hub ..." verb goes to the server plugin (RynthILT on Infinite Leaftide: "/ra hub bank",
    /// "/ra hub open banking", ...), and without one it shows the help. The retired quest verbs and tab
    /// names ("/ra quests window", "/ra hub open quests", ...) say where quests went, and "/ra hub
    /// open|tab character" opens RynthOracle's Character tab (Huds/MovedTabs.cs). "/ra guardian",
    /// "/ra bounty" and the like aren't the Mini Remote's (RynthAiPlugin passes them to the server
    /// plugin). Returns false if not handled.
    /// </summary>
    public bool HandleCommand(string verb, string[] args)
    {
        if (verb.Equals("quests", StringComparison.OrdinalIgnoreCase))
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "refresh";
            if (sub == "refresh") _ctx.Chat("[Mini Remote] " + RefreshQuests());
            else _ctx.Chat(QuestsMovedText);
            return true;
        }
        if (verb.Equals("pets", StringComparison.OrdinalIgnoreCase))
        {
            // "/ra pets [show|hide|toggle]": same as "/ra hub open pets".
            OpenTab(MiniRemoteTabs.Pets, args.Length > 0 ? args[0] : "toggle");
            return true;
        }
        if (!verb.Equals("hub", StringComparison.OrdinalIgnoreCase)) return false;

        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "toggle";
            // "/ra hub open|tab quests": the tab is RynthOracle's window now.
            if (cmd is "open" or "tab" && args.Length > 1 && IsQuestsName(args[1]))
            {
                _ctx.Chat(QuestsMovedText);
                return true;
            }
            // "/ra hub open|tab character": the Character tab left the Mini Remote (2026-10-13);
            // HudController.HandleTabCommand opens RynthOracle's Character tab.
            if (cmd is "open" or "tab" && args.Length > 1 && Huds.MovedTabs.CommandFor(args[1]) != null)
            {
                string? reply = MiniRemoteTabCommand?.Invoke(args[1].Trim().ToLowerInvariant(), "show");
                if (cmd == "open" && reply != null) _ctx.Chat("[Mini Remote] " + reply);
                return true;
            }
            switch (cmd)
            {
                case "show":
                case "hide":
                case "toggle":
                    // The Mini Remote is the Hub's main window.
                    _ctx.Chat(MiniRemoteCommand != null ? MiniRemoteCommand(cmd) : "[Mini Remote] The Mini Remote is not available yet.");
                    return true;
                case "open" or "tab" when args.Length > 1 && Pets.FindSubTab(args[1]) is { } subTab:
                    OpenPetsSubTab(cmd, subTab);
                    return true;
                case "open" when args.Length > 1 && MiniRemoteTabs.Parse(args[1]) is { } openTab:
                    OpenTab(openTab, args.Length > 2 ? args[2] : "toggle");
                    return true;
                case "tab" when args.Length < 2 || MiniRemoteTabs.Parse(args[1]) != null || IsRemoteTabName(args[1]):
                    // The engine's dashboard: it opens its Mini Remote window itself, so only switch tabs.
                    MiniRemoteTabCommand?.Invoke(args.Length > 1 ? MiniRemoteTabs.Parse(args[1]) ?? MiniRemoteTabs.Remote : MiniRemoteTabs.Remote, "select");
                    return true;
            }
        }

        // Everything else is the server plugin's (RynthILT's "/ra hub bank", "/ra hub open banking", ...).
        string line = (verb + " " + string.Join(" ", args)).Trim();
        if (_ctx.ServerHub.Command(line)) return true;
        if (args.Length > 0 && args[0].Equals("tab", StringComparison.OrdinalIgnoreCase))
        {
            // A tab no plugin offers (the engine's dashboard): back to the remote rather than nothing.
            MiniRemoteTabCommand?.Invoke(MiniRemoteTabs.Remote, "select");
            return true;
        }
        PrintHelp();
        return true;
    }

    private void PrintHelp()
    {
        _ctx.Chat("[Mini Remote] /ra hub [show|hide|toggle] (Mini Remote)   /ra hub open pets|crafting [show|hide|toggle]");
        _ctx.Chat("[Mini Remote] /ra quests refresh (quiet /myquests)   /ra pets [show|hide]   Quest list, timers and rewards: RynthOracle (/ro).");
        _ctx.Chat("[Mini Remote] A server plugin adds its own tabs and /ra hub verbs here once it is loaded for your world.");
        _ctx.Chat("[Mini Remote] Character overview and session: RynthOracle (/ro character). Force a server: /rc diagnostics.");
    }

    /// <summary>
    /// "/ra hub open|tab &lt;sub-tab&gt;": the Pets tab on a sub-tab the server plugin adds (RynthILT's
    /// Breeding). "open" shows the Mini Remote; "tab" (the engine's dashboard) only switches tabs.
    /// </summary>
    private void OpenPetsSubTab(string cmd, string subTab)
    {
        Pets.SelectSubTab(subTab);
        if (cmd == "tab")
        {
            MiniRemoteTabCommand?.Invoke(MiniRemoteTabs.Pets, "select");
            return;
        }
        OpenTab(MiniRemoteTabs.Pets, "show");
    }

    private static bool IsRemoteTabName(string text)
        => text.Equals("remote", StringComparison.OrdinalIgnoreCase) || text.Length == 0;

    /// <summary>"quests" / "quest": the retired Quests tab's names.</summary>
    private static bool IsQuestsName(string text)
        => text.Equals("quests", StringComparison.OrdinalIgnoreCase) || text.Equals("quest", StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies show / hide / toggle to one of the Mini Remote's own tabs. Says what happened.</summary>
    private void OpenTab(string tab, string mode)
    {
        string reply = MiniRemoteTabCommand?.Invoke(tab, mode) ?? "The Mini Remote is not available yet.";
        _ctx.Chat("[Mini Remote] " + reply);
    }
}
