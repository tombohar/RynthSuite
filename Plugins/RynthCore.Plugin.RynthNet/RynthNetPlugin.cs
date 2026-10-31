// ============================================================================
//  RynthNet - RynthNetPlugin.cs
//  UtilityBelt-style networking between the RynthCore clients on this PC:
//  broadcast / tagged / targeted chat commands, a client list with tags, and
//  shared vitals, position and bot state. Local only (same Windows user, named
//  pipes, no TCP, no server process): see Transport/.
//
//  Threads: the transport runs on its own threads and only queues. Everything
//  here (host calls, running commands, chat) happens on the plugin pump thread
//  in OnTick. OnChatBarEnter (AC's thread) and RynthPluginApplyRemoteCommand
//  (the caller's thread) only queue. Shutdown stops and joins the transport.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthNet.Transport;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthNet;

public sealed partial class RynthNetPlugin : RynthPluginBase
{
    public const string PluginVersion = "0.1.1";
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("RynthNet");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi(PluginVersion);

    private const int MaxScheduled = 256;
    private const int RateWindowMs = 5000;
    private const int RateMaxPerWindow = 20;

    private NetNode? _node;
    private string _offlineReason = "";
    private RynthNetSettings _settings = new();

    // Queued from other threads, drained in OnTick.
    // TypedUb: a /ub network command typed in chat, which RynthAi (when loaded) also sees and forwards.
    private readonly ConcurrentQueue<(string Line, bool TypedUb)> _localCommands = new();

    private sealed record Scheduled(long DueMs, string Text, string From, bool Remote);
    private readonly List<Scheduled> _scheduled = new();
    private readonly Dictionary<string, (long Start, int Count)> _rate = new();
    private readonly Dictionary<string, string> _peerNames = new();   // node id -> character last announced
    private readonly Dictionary<string, string> _sent = new();   // command id -> text, for refusal messages
    private readonly Queue<string> _sentOrder = new();
    private int _commandSeq;

    private uint _playerId;
    private bool _loggedIn;
    private string _character = "", _world = "", _account = "";

    private long _nextStatusMs, _nextSnapshotMs, _nextAiCheckMs, _nextLoginProbeMs, _nextSettingsCheckMs;
    private long _settingsStamp;
    private volatile bool _rynthAiPresent;
    private volatile string _snapshot = "{\"online\":false,\"clients\":[]}";

    public override int Initialize()
    {
        _ = Reply;
        // The Characters window draws with ImGui.NET bound to the engine's cimgui, in the engine's look.
        RynthCore.Plugin.Shared.ImGuiNativeBinding.Ensure(msg => Host.Log("[RynthNet] " + msg));
        RynthCore.Plugin.Shared.RynthUi.RynthUi.Init(Host);
        _settings = RynthNetSettings.Load(out string? settingsError);
        _settingsStamp = RynthNetSettings.FileStamp();
        if (settingsError != null) Host.Log("[RynthNet] " + settingsError);

        _node = new NetNode(new NetNodeOptions { App = "RynthNet/" + PluginVersion });
        if (!_node.Start(out string error))
        {
            _offlineReason = error;
            Host.Log("[RynthNet] offline: " + error);
        }
        PushIdentity();
        DrainLogs();
        Host.Log($"[RynthNet] {PluginVersion} initialized ({(_node.IsRunning ? "online" : "offline")}).");
        return 0;
    }

    public override void Shutdown()
    {
        NetNode? node = _node;
        _node = null;
        if (node != null)
        {
            bool clean = node.Stop(2000);
            while (node.TryDequeueLog(out string line)) Host.Log(line);
            Host.Log(clean
                ? "[RynthNet] stopped: every pipe closed, every thread and task ended."
                : $"[RynthNet] stop timed out: {node.RunningWorkers} worker(s) still running.");
        }
        _scheduled.Clear();
        _channels.Clear();
        _peerNames.Clear();
        _rate.Clear();
        while (_localCommands.TryDequeue(out _)) { }
    }

    // ── Game events ─────────────────────────────────────────────────────────

    public override void OnLoginComplete()
    {
        ResolveCharacter();
    }

    public override void OnLogout()
    {
        _loggedIn = false;
        _playerId = 0;
        _scheduled.RemoveAll(s => s.Remote);
        PushIdentity();
        try { _node?.SetStatus("{}"); } catch { }
    }

    /// <summary>AC's thread: only queue.</summary>
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        string t = (text ?? "").Trim();
        if (t.Equals("/rn", StringComparison.OrdinalIgnoreCase) || t.StartsWith("/rn ", StringComparison.OrdinalIgnoreCase))
        {
            _localCommands.Enqueue((t, false));
            eat = 1;
            return;
        }
        // UtilityBelt's network commands. Every plugin sees every line (eating doesn't stop the
        // others), so with RynthAi loaded it handles /ub and forwards these here itself. Whether
        // it is loaded is decided on the next tick, when the line is taken off the queue.
        if (IsUbNetCommand(t))
        {
            _localCommands.Enqueue((t, true));
            eat = 1;
        }
    }

    private static bool IsUbNetCommand(string t)
        => t.StartsWith("/ub bc ", StringComparison.OrdinalIgnoreCase)
           || t.StartsWith("/ub bct ", StringComparison.OrdinalIgnoreCase)
           || t.Equals("/ub netclients", StringComparison.OrdinalIgnoreCase)
           || t.StartsWith("/ub netclients ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Another plugin's command (host SendPluginCommand; any thread): ("ub", "bc ...") from
    /// RynthAi's /ub layer, ("rn", "list") for /rn. Only queued.
    /// </summary>
    internal void EnqueueForwarded(string action, string value)
    {
        string a = (action ?? "").Trim().ToLowerInvariant();
        string v = (value ?? "").Trim();
        if (a is "ub" or "rn") _localCommands.Enqueue(("/" + a + (v.Length > 0 ? " " + v : ""), false));
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        Reply.Flush();
        NetNode? node = _node;
        if (node == null) return;
        long now = NetNode.NowMs;

        DrainLogs();

        if (now >= _nextSettingsCheckMs)
        {
            _nextSettingsCheckMs = now + 5000;
            ReloadSettingsIfChanged();
        }
        if (now >= _nextAiCheckMs)
        {
            _nextAiCheckMs = now + 3000;
            _rynthAiPresent = RynthAiLoaded();
        }
        if (!_loggedIn && now >= _nextLoginProbeMs)
        {
            _nextLoginProbeMs = now + 2000;
            if (Host.GetPlayerId() != 0 && (!Host.HasIsPlayerReady || Host.IsPlayerReady()))
                ResolveCharacter();   // loaded (or reloaded) while already in the world
        }

        for (int i = 0; i < 64 && node.TryDequeueEvent(out NetEvent e); i++)
            HandleEvent(node, e, now);

        for (int i = 0; i < 16 && _localCommands.TryDequeue(out var queued); i++)
        {
            // A typed /ub bc: RynthAi, when loaded, saw the same line and forwards its own copy.
            if (queued.TypedUb && RynthAiLoaded()) continue;
            try { HandleLocal(node, queued.Line); }
            catch (Exception ex) { ChatFail($"'{queued.Line}' failed: {ex.Message}"); }
        }

        RunScheduled(now);

        if (now >= _nextStatusMs)
        {
            _nextStatusMs = now + _settings.StatusIntervalMs;
            if (_loggedIn && node.IsRunning) SendStatus(node);
        }
        if (now >= _nextSnapshotMs)
        {
            _nextSnapshotMs = now + 500;
            _snapshot = BuildSnapshotJson(node, now);
        }
        TickCharacters();
    }

    internal string Snapshot => _snapshot;

    /// <summary>RynthAi is loaded and initialised right now (pump thread).</summary>
    private bool RynthAiLoaded()
        => Host.HasGetPluginInterface && Host.GetPluginInterface("RynthAi", "RynthAi.Script", 1) != IntPtr.Zero;

    private void DrainLogs()
    {
        NetNode? node = _node;
        if (node == null) return;
        for (int i = 0; i < 32 && node.TryDequeueLog(out string line); i++) Host.Log(line);
    }

    private void ResolveCharacter()
    {
        uint id = Host.GetPlayerId();
        if (id == 0) return;
        if (!Host.TryGetObjectName(id, out string name) || string.IsNullOrWhiteSpace(name)) return;
        _playerId = id;
        _character = name.Trim();
        _world = Host.TryGetWorldName(out string world) ? world : "";
        _account = Host.TryGetAccountName(out string account) ? account : "";
        bool wasLoggedIn = _loggedIn;
        _loggedIn = true;
        PushIdentity();
        SyncCharactersView();
        if (!wasLoggedIn) _nextStatusMs = 0;
    }

    private string CharacterKey => RynthNetSettings.CharacterKey(_world, _character);

    private IReadOnlyList<string> MyTags => _loggedIn ? _settings.TagsFor(CharacterKey) : Array.Empty<string>();

    /// <summary>The command policy of the character logged in here (the defaults before login).</summary>
    private RynthNetPolicy MyPolicy => _loggedIn ? _settings.PolicyFor(CharacterKey) : _settings.Defaults;

    private void PushIdentity()
    {
        _node?.SetIdentity(new NodeIdentity
        {
            Character = _loggedIn ? _character : "",
            World = _world,
            Account = _account,
            LoggedIn = _loggedIn,
            Tags = MyTags.ToArray(),
            App = "RynthNet/" + PluginVersion,
        });
    }

    // Chat lines wait in the shared reply queue (Plugins/Shared/CommandReply.cs): the host drops a line
    // within 100 ms of another plugin's, so /rn help's and /rn list's later lines used to go missing.
    private CommandReply? _reply;
    private CommandReply Reply => _reply ??= new CommandReply("[RynthNet]",
        (t, c) => Host.HasWriteToChat ? Host.WriteToChat(t, c) : LogInstead(t), t => Host.Log(t));

    private bool LogInstead(string line)
    {
        Host.Log(line);
        return true;
    }

    private void ChatLine(string text) => Reply.Ok(text);

    /// <summary>A command that didn't work: why, and the usage (the failure colour).</summary>
    private void ChatFail(string text) => Reply.Fail(text);

    /// <summary>Applies one change to the shared settings file (merged with the other clients'), then pushes tags.</summary>
    private bool UpdateSettings(Action<RynthNetSettings> change)
    {
        if (!RynthNetSettings.Update(change, out RynthNetSettings current, out string? error))
        {
            ChatFail("couldn't save settings: " + error);
            return false;
        }
        _settings = current;
        _settingsStamp = RynthNetSettings.FileStamp();
        PushIdentity();
        return true;
    }

    /// <summary>Picks up the file when it changed (edited by hand, or another client saved).</summary>
    private void ReloadSettingsIfChanged()
    {
        long stamp = RynthNetSettings.FileStamp();
        if (stamp == _settingsStamp) return;
        _settingsStamp = stamp;
        _settings = RynthNetSettings.Load(out string? error);
        if (error != null) Host.Log("[RynthNet] " + error);
        PushIdentity();
        SyncCharactersView();   // the file edited by hand: its view too
    }

    // ── Network events ──────────────────────────────────────────────────────

    private void HandleEvent(NetNode node, NetEvent e, long now)
    {
        switch (e.Kind)
        {
            case NetEventKind.PeerJoined:
                _peerNames[e.NodeId] = e.From;
                if (_loggedIn && e.From.Length > 0) ChatLine($"{e.From} connected.");
                break;
            case NetEventKind.PeerUpdated:
            {
                // Clients connect at startup, before anyone is logged in: announce the character
                // when it appears (or changes, or logs out).
                string before = _peerNames.TryGetValue(e.NodeId, out string? b) ? b : "";
                if (before == e.From) break;
                _peerNames[e.NodeId] = e.From;
                if (!_loggedIn) break;
                if (before.Length == 0) ChatLine($"{e.From} connected.");
                else if (e.From.Length == 0) ChatLine($"{before} logged out.");
                else ChatLine($"{before} is now {e.From}.");
                break;
            }
            case NetEventKind.PeerLeft:
                if (_loggedIn && e.From.Length > 0) ChatLine($"{e.From} disconnected ({e.Text}).");
                _peerNames.Remove(e.NodeId);
                _rate.Remove(e.NodeId);
                break;
            case NetEventKind.Command:
                OnRemoteCommand(node, e, now);
                break;
            case NetEventKind.Ack:
                if (!e.Ok)
                {
                    string what = _sent.TryGetValue(e.Id, out string? t) ? $"'{t}'" : "the command";
                    ChatFail($"{e.From} didn't run {what}: {e.Text}");
                }
                break;
            case NetEventKind.Data:
                OnChannelMessage(e);
                break;
        }
    }

    private void OnRemoteCommand(NetNode node, NetEvent e, long now)
    {
        if (!e.Target.Matches(node.Identity)) { Ack(node, e, false, "not addressed to this client"); return; }
        if (!_loggedIn) { Ack(node, e, false, "not logged in"); return; }
        if (!CommandPolicy.Check(e.Text, e.From, MyPolicy, out string why))
        {
            Host.Log($"[RynthNet] refused {e.From}: '{e.Text}': {why}");
            Ack(node, e, false, why);
            return;
        }
        if (!_rate.TryGetValue(e.NodeId, out var window) || now - window.Start > RateWindowMs) window = (now, 0);
        if (window.Count >= RateMaxPerWindow)
        {
            Ack(node, e, false, $"too many commands (over {RateMaxPerWindow} in {RateWindowMs / 1000} s)");
            return;
        }
        _rate[e.NodeId] = (window.Start, window.Count + 1);
        if (_scheduled.Count >= MaxScheduled) { Ack(node, e, false, "command queue full"); return; }
        _scheduled.Add(new Scheduled(now + e.DelayMs, e.Text, e.From, Remote: true));
        Ack(node, e, true, "");
    }

    private static void Ack(NetNode node, NetEvent e, bool ok, string why) => node.SendTo(e.NodeId, Wire.Ack(e.Id, ok, why));

    private void RunScheduled(long now)
    {
        if (_scheduled.Count == 0) return;
        int ran = 0;
        for (int i = 0; i < _scheduled.Count && ran < 4; i++)
        {
            Scheduled s = _scheduled[i];
            if (s.DueMs > now) continue;
            _scheduled.RemoveAt(i--);
            ran++;
            Execute(s);
        }
    }

    private void Execute(Scheduled s)
    {
        if (s.Remote)
        {
            if (!_loggedIn) return;
            if (_settings.LogRemoteCommands) ChatLine($"{s.From}: {s.Text}");
            Host.Log($"[RynthNet] running for {s.From}: {s.Text}");
        }
        if (!Host.HasInvokeChatParser)
        {
            ChatFail("this engine can't run chat commands for plugins (no InvokeChatParser).");
            return;
        }
        // Same as typing it: plugins (/ra, /ub, /lua...) get it first, then AC.
        Host.InvokeChatParser(s.Text);
    }

    // ── Sending ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a command to the matching clients (sorted by name; the n-th gets n x delayMs) and,
    /// with <paramref name="includeSelf"/>, runs it here first when this client matches.
    /// Returns the names it went to ("you" for this client), or null with an error in chat.
    /// </summary>
    internal List<string>? SendCommand(NetTarget target, string command, int delayMs, bool includeSelf, bool quiet = false)
    {
        NetNode? node = _node;
        command = (command ?? "").Trim();
        if (command.Length == 0) { if (!quiet) ChatFail("no command given."); return null; }
        if (command.Length > Wire.MaxText) { if (!quiet) ChatFail($"command too long (max {Wire.MaxText})."); return null; }
        if (CommandPolicy.Rebroadcasts(command))
        {
            if (!quiet) ChatFail("a broadcast can't contain another broadcast (/rn, /ub bc, /ub bct).");
            return null;
        }
        delayMs = Math.Clamp(delayMs, 0, 600_000);
        var names = new List<string>();
        long now = NetNode.NowMs;
        if (includeSelf && node != null && target.Matches(node.Identity))
        {
            _scheduled.Add(new Scheduled(now, command, "you", Remote: false));
            names.Add("you");
        }
        if (node == null || !node.IsRunning) return names;

        var peers = new List<PeerInfo>(node.GetPeers());
        peers.Sort((a, b) => string.Compare(a.Identity.Character, b.Identity.Character, StringComparison.OrdinalIgnoreCase));
        int n = 0;
        foreach (PeerInfo p in peers)
        {
            if (!target.Matches(p.Identity)) continue;
            n++;
            string id = NewCommandId(node, command);
            if (node.SendTo(p.NodeId, Wire.Command(id, target, command, delayMs * n)))
                names.Add(p.Identity.Character.Length > 0 ? p.Identity.Character : "pid " + p.Pid);
        }
        return names;
    }

    private string NewCommandId(NetNode node, string command)
    {
        string id = node.NodeId + ":" + (++_commandSeq);
        _sent[id] = command;
        _sentOrder.Enqueue(id);
        while (_sentOrder.Count > 128) _sent.Remove(_sentOrder.Dequeue());
        return id;
    }

    /// <summary>Publishes a channel message to the matching clients. Returns how many, -1 when offline.</summary>
    internal int Publish(string channel, string message, NetTarget target)
    {
        NetNode? node = _node;
        if (node == null || !node.IsRunning) return -1;
        channel = (channel ?? "").Trim();
        if (channel.Length == 0 || channel.Length > Wire.MaxName) return -1;
        if ((message ?? "").Length > Wire.MaxMessage) return -1;
        return node.Broadcast(Wire.Data(channel, message ?? "", target), p => target.Matches(p.Identity));
    }
}
