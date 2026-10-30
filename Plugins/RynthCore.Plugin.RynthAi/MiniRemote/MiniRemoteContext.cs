// MiniRemoteContext.cs — Shared services for the Mini Remote's universal tabs (Quests, Pets) and
// the VTank .usd importer. The universal half of the ILT Hub's IltHubContext (SilentKelpie's ILT
// Hub; its Infinite Leaftide half went to the RynthILT plugin 2026-10-13, whose feature bits and
// sections come through ServerHub).
//
// Threading contract (as before):
//   * Tick / OnChat run on the plugin pump thread and may call the host freely.
//   * Render runs on the ImGui render thread and must NOT call AC actions directly. It calls
//     Post(...) to run work on the next pump tick, or Confirm(...) to ask the player first (the
//     confirmed action is then posted).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

/// <summary>A yes/no question waiting for the player (a modal over the Mini Remote).</summary>
internal sealed class MiniRemoteConfirm
{
    public string Title = "Confirm";
    public string Message = string.Empty;
    public string YesLabel = "Yes";
    /// <summary>Runs on the pump thread after the player clicks Yes.</summary>
    public Action? OnYes;
}

internal sealed class MiniRemoteContext
{
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly ConcurrentQueue<MiniRemoteConfirm> _confirms = new();
    private readonly Func<LegacyUiSettings?> _settings;

    public MiniRemoteContext(RynthCoreHost host, MiniRemoteState state, MiniRemoteStore store, MiniRemoteInventory inventory,
                             ServerHubClient serverHub, Func<LegacyUiSettings?> settings)
    {
        Host = host;
        State = state;
        Store = store;
        Inventory = inventory;
        ServerHub = serverHub;
        _settings = settings;
    }

    public RynthCoreHost Host { get; }
    public MiniRemoteState State { get; }
    public MiniRemoteStore Store { get; }
    public MiniRemoteInventory Inventory { get; }

    /// <summary>RynthILT's view (ILT world, feature bits, ILT sections); "not ILT" without RynthILT.</summary>
    public ServerHubClient ServerHub { get; }

    /// <summary>The live combat settings (rule lists the Pets tab / USD importer edit). Null before login.</summary>
    public LegacyUiSettings? Settings => _settings();

    /// <summary>Set by the controller when combat settings were edited and must be saved.</summary>
    public Action? SaveCombatSettings;

    /// <summary>Logged-in character name (resolved at login).</summary>
    public string CharName = string.Empty;

    /// <summary>The connected world's name ("" before login or on an engine without it); keys the Crafting tab's custom rows.</summary>
    public string WorldName
        => Host.HasGetWorldName && Host.TryGetWorldName(out string world) ? (world ?? string.Empty).Trim() : string.Empty;

    // ── Active server (RynthCore's verdict and its server plugin's profile) ─────────────────────

    /// <summary>What the render thread reads about the active server: replaced whole, never edited.</summary>
    private sealed record ServerView(string Name, IReadOnlyList<ServerSalvageView> Salvage, ServerTrinketRule Trinkets)
    {
        public static readonly ServerView None = new(string.Empty, Array.Empty<ServerSalvageView>(), ServerTrinketRule.None);
    }

    private readonly ServerStateClient _server = new();
    private int _serverRevision = -1;
    private volatile ServerView _serverView = ServerView.None;

    /// <summary>The active server's display name from RynthCore ("" when none is active or the engine is pre-v86).</summary>
    public string ServerName => _serverView.Name;

    /// <summary>The active server plugin's own tinkering salvage (empty when it defines none). Any thread.</summary>
    public IReadOnlyList<ServerSalvageView> ServerSalvage => _serverView.Salvage;

    /// <summary>Which jewelry the active server plugin calls a trinket (none without one). Any thread.</summary>
    public ServerTrinketRule ServerTrinkets => _serverView.Trinkets;

    /// <summary>Pump thread: polls RynthCore's server state (cheap until due) and rebuilds the view when it changed.</summary>
    public void PollServer(long now)
    {
        _server.Poll(Host, now);
        if (_server.Revision == _serverRevision) return;
        _serverRevision = _server.Revision;
        EngineServerState? st = _server.State;
        string name = st != null && st.HasActive ? st.ActiveName : string.Empty;
        _serverView = new ServerView(name, ServerSalvageView.ParseList(_server.ProfileJson), ServerTrinketRule.Parse(_server.ProfileJson));
    }

    /// <summary>Logout: forget the server; the next login polls fresh.</summary>
    public void ClearServer()
    {
        _server.Clear();
        _serverRevision = -1;
        _serverView = ServerView.None;
    }

    /// <summary>
    /// Shows the Mini Remote on one of its tabs (tab id; set by the controller). Any thread: it
    /// only records the tab and the show request, which the next frame applies.
    /// </summary>
    public Action<string>? ShowTab;

    /// <summary>Writes a local chat line (never sent to the server).</summary>
    public void Chat(string text)
    {
        if (Host.HasWriteToChat) Host.WriteToChat(text, 1);
    }

    /// <summary>Queues work for the next pump tick (safe from the render thread).</summary>
    public void Post(Action action) => _posted.Enqueue(action);

    /// <summary>Asks the player to confirm before <paramref name="onYes"/> runs on the pump thread.</summary>
    public void Confirm(string title, string message, Action onYes, string yesLabel = "Yes")
        => _confirms.Enqueue(new MiniRemoteConfirm { Title = title, Message = message, OnYes = onYes, YesLabel = yesLabel });

    /// <summary>Render thread: takes the next pending confirmation, if any.</summary>
    public bool TryTakeConfirm(out MiniRemoteConfirm request) => _confirms.TryDequeue(out request!);

    /// <summary>Pump thread: runs everything posted since the last tick.</summary>
    public void DrainPosted()
    {
        int guard = 0;
        while (guard++ < 64 && _posted.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { RynthLog.Exception(LogCat.UI, ex, "[MiniRemote] posted action"); }
        }
    }

    /// <summary>Elapsed-ms clock used for every timer.</summary>
    public static long NowMs => Environment.TickCount64;
}

/// <summary>Text helpers the universal tabs share (the ILT Hub's IltParse.Duration).</summary>
internal static class MiniRemoteText
{
    /// <summary>"ready", "45s", "3m 12s", "2h 5m 0s", "1d 4h 30m".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "ready";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1) return $"{span.Hours}h {span.Minutes}m {span.Seconds}s";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }
}

/// <summary>The Mini Remote's own tab ids (RynthILT supplies the Infinite Leaftide ones).</summary>
internal static class MiniRemoteTabs
{
    /// <summary>The Remote tab (the remote itself).</summary>
    public const string Remote = "";
    public const string Pets = "pets";
    public const string Crafting = "crafting";

    /// <summary>
    /// The universal tabs in tab-bar order (after Remote, before RynthILT's tabs). The Quests tab
    /// moved to RynthOracle on 2026-10-13 (MiniRemoteController.QuestsMovedText).
    /// </summary>
    public static readonly string[] Universal = { Pets, Crafting };

    public static string Label(string id) => id switch
    {
        Pets => "Pets",
        Crafting => "Crafting",
        _ => "Remote",
    };

    /// <summary>"pets" / "crafting" (and their short forms) to the tab id; null for anything else.</summary>
    public static string? Parse(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "pets" or "pet" => Pets,
        "crafting" or "craft" or "tinker" or "tinkering" => Crafting,
        _ => null,
    };

    /// <summary>Size of a universal tab's body (the old section window's default size).</summary>
    public static System.Numerics.Vector2 Size(string id) => id switch
    {
        Pets => new System.Numerics.Vector2(560, 580),
        Crafting => new System.Numerics.Vector2(680, 600),
        _ => new System.Numerics.Vector2(520, 440),
    };
}
