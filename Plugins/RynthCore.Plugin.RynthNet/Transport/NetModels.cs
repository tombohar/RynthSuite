// ============================================================================
//  RynthNet - Transport/NetModels.cs
//  Plain data the transport hands to its consumer (the plugin, or the test
//  harness). Everything here is immutable once published, so the pump thread
//  can read it while the I/O threads keep running.
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthNet.Transport;

/// <summary>Who a client is. Sent in hello/ident; replaced as a whole, never mutated.</summary>
public sealed class NodeIdentity
{
    public string Character { get; init; } = "";
    public string World { get; init; } = "";
    public string Account { get; init; } = "";
    public bool LoggedIn { get; init; }
    public string[] Tags { get; init; } = Array.Empty<string>();
    public string App { get; init; } = "";

    public bool HasAnyTag(string[] wanted)
    {
        foreach (string w in wanted)
            foreach (string t in Tags)
                if (string.Equals(t, w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>A connected client as the pump thread sees it (a copy taken under the node's lock).</summary>
public sealed class PeerInfo
{
    public string NodeId { get; init; } = "";
    public int Pid { get; init; }
    public bool Outbound { get; init; }
    public NodeIdentity Identity { get; init; } = new();
    /// <summary>The last status object the client sent (raw JSON), or "" before the first one.</summary>
    public string StatusJson { get; init; } = "";
    public long StatusAtMs { get; init; }
    public long ConnectedAtMs { get; init; }
    public int RttMs { get; init; } = -1;
}

/// <summary>Who a command or message is for. Receivers check it again against themselves.</summary>
public enum TargetKind { All = 0, Tags = 1, Character = 2 }

public readonly record struct NetTarget(TargetKind Kind, string Value)
{
    public static readonly NetTarget All = new(TargetKind.All, "");

    public string KindName => Kind switch { TargetKind.Tags => "tags", TargetKind.Character => "char", _ => "all" };

    public static NetTarget Parse(string? kind, string? value) => kind switch
    {
        "tags" => new NetTarget(TargetKind.Tags, value ?? ""),
        "char" => new NetTarget(TargetKind.Character, value ?? ""),
        _ => All,
    };

    /// <summary>The comma-separated tag list of a Tags target.</summary>
    public string[] TagList => Kind == TargetKind.Tags
        ? Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>();

    public bool Matches(NodeIdentity id) => Kind switch
    {
        TargetKind.All => true,
        TargetKind.Tags => id.HasAnyTag(TagList),
        TargetKind.Character => string.Equals(id.Character, Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}

/// <summary>What happened on the network, queued for the pump thread.</summary>
public enum NetEventKind { PeerJoined, PeerLeft, PeerUpdated, Command, Ack, Data }

public sealed class NetEvent
{
    public NetEventKind Kind { get; init; }
    public string NodeId { get; init; } = "";
    /// <summary>The sender's character name when the event was queued.</summary>
    public string From { get; init; } = "";
    public string Id { get; init; } = "";
    public NetTarget Target { get; init; }
    public string Text { get; init; } = "";
    public int DelayMs { get; init; }
    public bool Ok { get; init; }
    public string Channel { get; init; } = "";
    public long AtMs { get; init; }
}
