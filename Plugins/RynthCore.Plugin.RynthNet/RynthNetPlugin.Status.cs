// ============================================================================
//  RynthNet - RynthNetPlugin.Status.cs
//  What this client shares (vitals, position, bot state; every
//  StatusIntervalMs), the JSON views of the network (snapshot export, the
//  RynthNet.Net interface) and channel subscriptions. Pump thread only.
// ============================================================================

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthNet.Transport;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNet;

public sealed unsafe partial class RynthNetPlugin
{
    private const int MaxChannelMessages = 256;
    private const int MaxChannels = 32;

    private readonly Dictionary<string, Queue<(string From, string Node, string Msg, long At)>> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Json(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
            body(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ── This client's status ────────────────────────────────────────────────

    private void SendStatus(NetNode node)
    {
        string? bot = BotStatusJson();
        string status = Json(w =>
        {
            w.WriteStartObject();
            if (Host.TryGetPlayerVitals(out uint hp, out uint hpMax, out uint st, out uint stMax, out uint mp, out uint mpMax))
            {
                w.WriteNumber("hp", hp); w.WriteNumber("hpMax", hpMax);
                w.WriteNumber("st", st); w.WriteNumber("stMax", stMax);
                w.WriteNumber("mp", mp); w.WriteNumber("mpMax", mpMax);
            }
            if (Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _))
            {
                w.WriteNumber("cell", cell);
                w.WriteNumber("x", Math.Round(x, 2)); w.WriteNumber("y", Math.Round(y, 2)); w.WriteNumber("z", Math.Round(z, 2));
            }
            if (Host.TryGetCurCoords(out double ns, out double ew))
            {
                w.WriteNumber("ns", Math.Round(ns, 3)); w.WriteNumber("ew", Math.Round(ew, 3));
            }
            if (Host.HasGetPlayerHeading && Host.TryGetPlayerHeading(out float heading))
                w.WriteNumber("heading", Math.Round(heading, 1));
            if (Host.HasIsPortaling) w.WriteBoolean("portal", Host.IsPortaling());
            if (Host.HasGetBusyState) w.WriteNumber("busy", Host.GetBusyState());
            if (Host.HasGetVitae && _playerId != 0) w.WriteNumber("vitae", Math.Round(Host.GetVitae(_playerId), 3));
            // 0.1.1: the character's object id (the same on every client: the engine's
            // Characters panel selects it when it is in view) and AC's combat mode
            // (1 peace, 2 melee, 4 missile, 8 magic). Older clients just don't send them.
            if (_playerId != 0) w.WriteNumber("id", _playerId);
            if (Host.HasGetCurrentCombatMode) w.WriteNumber("cmode", Host.GetCurrentCombatMode());
            if (bot != null)
            {
                try
                {
                    w.WritePropertyName("bot");
                    w.WriteRawValue(bot);   // validated: a bad string throws here, not in the peers
                }
                catch (Exception)
                {
                    w.WriteNullValue();
                }
            }
            w.WriteEndObject();
        });
        try { node.SetStatus(status); }
        catch (Exception ex) { Host.Log("[RynthNet] status not sent: " + ex.Message); }
    }

    /// <summary>
    /// RynthAi's state: its "RynthAi.Status" interface, else just the macro flag from
    /// "RynthAi.Script" (an older RynthAi), else null. Resolved again every call: never cached.
    /// </summary>
    private string? BotStatusJson()
    {
        if (!Host.HasGetPluginInterface) return null;
        try
        {
            var status = (RynthAiStatusApiV1*)Host.GetPluginInterface("RynthAi", RynthAiStatusApiV1.InterfaceName, RynthAiStatusApiV1.InterfaceVersion);
            if (status != null && status->Version >= 1 && status->StructSize >= (uint)sizeof(RynthAiStatusApiV1) && status->GetStatusJson != null)
            {
                byte* r = null;
                if (status->GetStatusJson(&r) != 0 && r != null)
                {
                    string json = Marshal.PtrToStringUTF8((IntPtr)r) ?? "";
                    if (json.StartsWith('{')) return json;
                }
                return null;
            }
            var script = (RynthAiScriptApiV1*)Host.GetPluginInterface("RynthAi", RynthAiScriptApiV1.InterfaceName, RynthAiScriptApiV1.InterfaceVersion);
            if (script != null && script->Version >= 1 && script->StructSize >= (uint)sizeof(RynthAiScriptApiV1) && script->IsMacroRunning != null)
                return script->IsMacroRunning() != 0 ? "{\"macro\":true}" : "{\"macro\":false}";
        }
        catch (Exception ex)
        {
            Host.Log("[RynthNet] RynthAi status failed: " + ex.Message);
        }
        return null;
    }

    // ── JSON views ──────────────────────────────────────────────────────────

    private static void WriteTags(Utf8JsonWriter w, string[] tags)
    {
        w.WriteStartArray("tags");
        foreach (string t in tags) w.WriteStringValue(t);
        w.WriteEndArray();
    }

    private static void WriteClients(Utf8JsonWriter w, NetNode node, long now)
    {
        var peers = new List<PeerInfo>(node.GetPeers());
        peers.Sort((a, b) => string.Compare(a.Identity.Character, b.Identity.Character, StringComparison.OrdinalIgnoreCase));
        w.WriteStartArray();
        foreach (PeerInfo p in peers)
        {
            w.WriteStartObject();
            w.WriteString("node", p.NodeId);
            w.WriteNumber("pid", p.Pid);
            w.WriteString("char", p.Identity.Character);
            w.WriteString("world", p.Identity.World);
            w.WriteBoolean("login", p.Identity.LoggedIn);
            WriteTags(w, p.Identity.Tags);
            w.WriteNumber("rtt", p.RttMs);
            w.WritePropertyName("status");
            if (p.StatusJson.Length > 2)
            {
                w.WriteRawValue(p.StatusJson);
                w.WriteNumber("age", now - p.StatusAtMs);
            }
            else
            {
                w.WriteNullValue();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>The other clients (RynthNet.Net GetClientsJson).</summary>
    internal string ClientsJson()
    {
        NetNode? node = _node;
        if (node == null) return "[]";
        long now = NetNode.NowMs;
        return Json(w => WriteClients(w, node, now));
    }

    /// <summary>This client (RynthNet.Net GetSelfJson).</summary>
    internal string SelfJson()
    {
        NetNode? node = _node;
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("node", node?.NodeId ?? "");
            w.WriteString("pipe", node?.PipeName ?? "");
            w.WriteString("char", _loggedIn ? _character : "");
            w.WriteString("world", _world);
            w.WriteBoolean("login", _loggedIn);
            WriteTags(w, new List<string>(MyTags).ToArray());
            w.WriteBoolean("online", node?.IsRunning == true);
            w.WriteEndObject();
        });
    }

    /// <summary>What RynthPluginGetSnapshotJson serves (rebuilt twice a second for panels).</summary>
    private string BuildSnapshotJson(NetNode node, long now) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("version", PluginVersion);
        w.WriteBoolean("online", node.IsRunning);
        if (!node.IsRunning) w.WriteString("offline", _offlineReason);
        w.WriteString("node", node.NodeId);
        w.WriteString("char", _loggedIn ? _character : "");
        w.WriteString("world", _world);
        w.WriteBoolean("login", _loggedIn);
        WriteTags(w, new List<string>(MyTags).ToArray());
        w.WriteBoolean("acceptRemote", MyPolicy.AcceptRemoteCommands);
        w.WritePropertyName("clients");
        WriteClients(w, node, now);
        w.WriteEndObject();
    });

    // ── Channels (RynthNet.Net Publish / Subscribe / PollChannel) ───────────

    internal bool Subscribe(string channel, bool on)
    {
        channel = (channel ?? "").Trim();
        if (channel.Length == 0 || channel.Length > Wire.MaxName) return false;
        if (!on)
        {
            _channels.Remove(channel);
            return true;
        }
        if (_channels.ContainsKey(channel)) return true;
        if (_channels.Count >= MaxChannels) return false;
        _channels[channel] = new Queue<(string, string, string, long)>();
        return true;
    }

    private void OnChannelMessage(NetEvent e)
    {
        NetNode? node = _node;
        if (node == null || !_channels.TryGetValue(e.Channel, out var queue)) return;
        if (!e.Target.Matches(node.Identity)) return;
        if (queue.Count >= MaxChannelMessages) queue.Dequeue();
        queue.Enqueue((e.From, e.NodeId, e.Text, e.AtMs));
    }

    internal string? PollChannel(string channel)
    {
        if (!_channels.TryGetValue((channel ?? "").Trim(), out var queue)) return null;
        string json = Json(w =>
        {
            w.WriteStartArray();
            foreach (var m in queue)
            {
                w.WriteStartObject();
                w.WriteString("from", m.From);
                w.WriteString("node", m.Node);
                w.WriteString("msg", m.Msg);
                w.WriteNumber("at", m.At);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        queue.Clear();
        return json;
    }

    internal string TagsText => string.Join("\n", MyTags);

    internal bool IsOnline => _node?.IsRunning == true;
}
