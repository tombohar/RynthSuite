// ============================================================================
//  RynthSuite - Plugins/Shared/ServerStateClient.cs
//  A plugin's live view of "which server is this, and what are its settings":
//    1. RynthCore's server list (plugin API v86 GetServerStateJson, parsed by
//       EngineServerState.cs): the active server, overrides and the Diagnostics
//       pick included, and the plugin that owns it;
//    2. that plugin's profile JSON through "RynthServer.Profile"
//       (RynthServerProfileApi.cs): a server plugin for its server, RynthILT
//       for "ilt".
//  Both are polled (every 2 s, cheap when not due) and Revision goes up when
//  either changes, so a reader re-applies its server-specific state then.
//
//  Needs, linked into the reader: EngineServerState.cs, RynthServerProfileApi.cs.
//  Engines older than v86 report Supported = false (the reader keeps its own
//  detection).
//  Pump thread only.
// ============================================================================

using System;
using System.Text;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal sealed unsafe class ServerStateClient
{
    private const long PollMs = 2000;
    private const long RequeryMs = 10_000;
    private const int MaxBuffer = 512 * 1024;

    private long _nextPollAt, _nextQueryAt;
    private string _stateJson = string.Empty;
    // The owner's "RynthServer.Profile" table.
    private string _tableOwner = string.Empty;
    private uint* _tableVersion;
    private delegate* unmanaged[Cdecl]<byte*, int, int> _getProfileJson;
    private byte[] _buffer = new byte[4096];

    /// <summary>The engine has GetServerStateJson (API v86+). False until the first poll.</summary>
    public bool Supported { get; private set; }
    /// <summary>The engine's server list, null before the first answer (or without v86).</summary>
    public EngineServerState? State { get; private set; }
    /// <summary>The active server owner's profile JSON ("" when none is active, or its plugin has none).</summary>
    public string ProfileJson { get; private set; } = string.Empty;
    /// <summary>The plugin <see cref="ProfileJson"/> came from ("" with none).</summary>
    public string ProfilePlugin { get; private set; } = string.Empty;
    /// <summary>Goes up whenever <see cref="State"/> or <see cref="ProfileJson"/> changes.</summary>
    public int Revision { get; private set; }

    /// <summary>Logout: forget everything; the next poll reads fresh.</summary>
    public void Clear()
    {
        _nextPollAt = 0;
        _nextQueryAt = 0;
        DropTable();
        bool changed = State != null || ProfileJson.Length > 0;
        _stateJson = string.Empty;
        State = null;
        ProfileJson = ProfilePlugin = string.Empty;
        if (changed) Revision++;
    }

    /// <summary>Polls when due; <paramref name="force"/> polls now (login). Pump thread.</summary>
    public void Poll(RynthCoreHost host, long now, bool force = false)
    {
        if (!force && now < _nextPollAt) return;
        _nextPollAt = now + PollMs;

        Supported = host.HasGetServerStateJson;
        bool changed = false;
        string json = Supported && host.TryGetServerStateJson(out string j) ? j : string.Empty;
        if (json != _stateJson)
        {
            _stateJson = json;
            State = EngineServerState.Parse(json);
            changed = true;
        }

        string owner = State?.ActivePlugin ?? string.Empty;
        string profile = owner.Length > 0 ? ReadProfile(host, owner, now) : string.Empty;
        if (owner.Length == 0) DropTable();
        if (profile != ProfileJson)
        {
            ProfileJson = profile;
            changed = true;
        }
        ProfilePlugin = profile.Length > 0 ? owner : string.Empty;
        if (changed) Revision++;
    }

    /// <summary>The owner's profile JSON, querying (or re-querying) its table as needed.</summary>
    private string ReadProfile(RynthCoreHost host, string owner, long now)
    {
        if (!owner.Equals(_tableOwner, StringComparison.OrdinalIgnoreCase))
        {
            DropTable();
            _tableOwner = owner;
            _nextQueryAt = 0;
        }
        if (_tableVersion != null && *_tableVersion == 0) DropTableKeepOwner();
        if (_getProfileJson == null)
        {
            if (now < _nextQueryAt || !host.HasGetPluginInterface) return string.Empty;
            _nextQueryAt = now + RequeryMs;
            if (!Query(host, owner)) return string.Empty;
        }
        return Read();
    }

    private bool Query(RynthCoreHost host, string owner)
    {
        var p = (RynthServerProfileApiV1*)host.GetPluginInterface(owner, RynthServerProfileApiV1.InterfaceName, RynthServerProfileApiV1.InterfaceVersion);
        if (p == null || p->Version < 1 || p->StructSize < (uint)sizeof(RynthServerProfileApiV1) || p->GetProfileJson == null) return false;
        _tableVersion = &p->Version;
        _getProfileJson = p->GetProfileJson;
        return true;
    }

    /// <summary>Copies the JSON out, growing the buffer once when the owner says it didn't fit.</summary>
    private string Read()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int len;
            fixed (byte* b = _buffer) len = _getProfileJson(b, _buffer.Length);
            if (len <= 0) return string.Empty;
            if (len < _buffer.Length) return Encoding.UTF8.GetString(_buffer, 0, len);
            if (len + 1 > MaxBuffer) return string.Empty;
            _buffer = new byte[len + 1024];
        }
        return string.Empty;
    }

    private void DropTable()
    {
        DropTableKeepOwner();
        _tableOwner = string.Empty;
    }

    private void DropTableKeepOwner()
    {
        _tableVersion = null;
        _getProfileJson = null;
    }
}
