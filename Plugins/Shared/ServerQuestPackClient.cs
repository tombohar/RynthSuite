// ============================================================================
//  RynthSuite - Plugins/Shared/ServerQuestPackClient.cs
//  A quest reader's side of "RynthServer.QuestPack" (RynthServerQuestPackApi.cs):
//  the active server's own quest list, from the plugin RynthCore names as its
//  owner (GetServerStateJson "activePlugin", ServerStateClient.cs). The reader
//  never names a server: whatever pack the active server's plugin offers is
//  the pack, and a plugin that offers none (or an engine older than v86, with
//  no server list) means retail quests only.
//
//  The info JSON is polled (every 2 s, cheap); the CSV is read only when the
//  owner, its table or the info changes. Revision goes up when the pack does.
//  Needs, linked into the reader: RynthServerQuestPackApi.cs, EngineServerState.cs.
//  Pump thread only.
// ============================================================================

using System;
using System.Text;
using System.Text.Json;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

/// <summary>A server plugin's quest pack as a reader took it.</summary>
/// <param name="Plugin">The plugin that offered it.</param>
/// <param name="ServerId">The server's RynthCore id (the reader's override file name).</param>
/// <param name="Tag">What quest lists call the server (the master list's Server column; the label shown).</param>
/// <param name="Rows">Data rows, as the plugin counted them.</param>
/// <param name="Revision">The plugin's pack revision.</param>
/// <param name="Csv">The pack itself.</param>
internal sealed record ServerQuestPack(string Plugin, string ServerId, string Tag, int Rows, int Revision, string Csv);

/// <summary>The "RynthServer.QuestPack" info JSON, read leniently.</summary>
internal sealed record ServerQuestPackInfo(string ServerId, string Tag, string Plugin, int Rows, int Bytes, int Revision)
{
    /// <summary>The info, or null for junk, another schema, or a pack without a size.</summary>
    public static ServerQuestPackInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!r.TryGetProperty("schema", out JsonElement s) || s.ValueKind != JsonValueKind.Number || s.GetInt32() != RynthServerQuestPack.Schema) return null;
            int bytes = Int(r, "bytes");
            if (bytes <= 0) return null;
            return new ServerQuestPackInfo(Str(r, "server"), Str(r, "tag"), Str(r, "plugin"), Int(r, "rows"), bytes, Int(r, "revision"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? string.Empty).Trim() : string.Empty;

    private static int Int(JsonElement r, string name) =>
        r.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : 0;
}

internal sealed unsafe class ServerQuestPackClient
{
    private const long PollMs = 2000;
    private const long RequeryMs = 10_000;
    private const int MaxInfoBytes = 64 * 1024;

    private string _owner = string.Empty;
    private uint* _tableVersion;
    private delegate* unmanaged[Cdecl]<byte*, int, int> _getInfo;
    private delegate* unmanaged[Cdecl]<byte*, int, int> _getCsv;
    private long _nextPollAt, _nextQueryAt;
    private string _infoJson = string.Empty;

    /// <summary>The pack the owner offers now; null when it offers none (or there is no owner).</summary>
    public ServerQuestPack? Pack { get; private set; }

    /// <summary>Goes up whenever <see cref="Pack"/> changes.</summary>
    public int Revision { get; private set; }

    /// <summary>The plugin last asked ("" = none).</summary>
    public string Owner => _owner;

    /// <summary>
    /// The pack to merge: the one <paramref name="offered"/> only while its plugin owns the active server
    /// in <paramref name="state"/> (RynthCore's server list), else none. Pure: the readers' tests use it.
    /// </summary>
    public static ServerQuestPack? ForActive(EngineServerState? state, ServerQuestPack? offered) =>
        offered != null && state is { HasActive: true } && state.ActivePlugin.Length > 0
        && state.ActivePlugin.Equals(offered.Plugin, StringComparison.OrdinalIgnoreCase)
            ? offered
            : null;

    /// <summary>The plugin to ask: the owner of the active server, "" with none (or no server list).</summary>
    public static string OwnerOf(EngineServerState? state) => state is { HasActive: true } ? state.ActivePlugin : string.Empty;

    /// <summary>Logout: forget everything; the next poll reads fresh.</summary>
    public void Clear()
    {
        DropTable();
        _owner = string.Empty;
        _nextPollAt = _nextQueryAt = 0;
        SetPack(null);
    }

    /// <summary>Polls <paramref name="owner"/>'s pack when due; <paramref name="force"/> polls now. Pump thread.</summary>
    public void Poll(RynthCoreHost host, string? owner, long now, bool force = false)
    {
        if (!force && now < _nextPollAt) return;
        _nextPollAt = now + PollMs;
        owner = (owner ?? string.Empty).Trim();
        if (!owner.Equals(_owner, StringComparison.OrdinalIgnoreCase))
        {
            DropTable();
            _owner = owner;
            _nextQueryAt = 0;
            SetPack(null);
        }
        if (owner.Length == 0) return;
        if (_tableVersion != null && *_tableVersion == 0)
        {
            // The plugin shut down (a reload): ask the new copy.
            DropTable();
            _nextQueryAt = 0;
        }
        if (_getInfo == null)
        {
            if (force) _nextQueryAt = 0;
            if (now < _nextQueryAt || !host.HasGetPluginInterface) return;
            _nextQueryAt = now + RequeryMs;
            if (!Query(host, owner))
            {
                SetPack(null);   // this server's plugin offers no pack
                return;
            }
        }

        string info = Read(_getInfo, 1024, MaxInfoBytes);
        if (info == _infoJson && (Pack != null || info.Length == 0)) return;
        _infoJson = info;
        ServerQuestPackInfo? parsed = ServerQuestPackInfo.Parse(info);
        if (parsed == null || parsed.Bytes > RynthServerQuestPack.MaxCsvBytes)
        {
            SetPack(null);
            return;
        }
        string csv = Read(_getCsv, parsed.Bytes + 1, RynthServerQuestPack.MaxCsvBytes + 1);
        if (csv.Length == 0)
        {
            _infoJson = string.Empty;   // try again on the next poll
            SetPack(null);
            return;
        }
        SetPack(new ServerQuestPack(owner, parsed.ServerId, parsed.Tag, parsed.Rows, parsed.Revision, csv));
    }

    private bool Query(RynthCoreHost host, string owner)
    {
        var p = (RynthServerQuestPackApiV1*)host.GetPluginInterface(owner, RynthServerQuestPackApiV1.InterfaceName, RynthServerQuestPackApiV1.InterfaceVersion);
        if (p == null || p->Version < 1 || p->StructSize < (uint)sizeof(RynthServerQuestPackApiV1) || p->GetPackInfoJson == null || p->GetPackCsv == null)
            return false;
        _tableVersion = &p->Version;
        _getInfo = p->GetPackInfoJson;
        _getCsv = p->GetPackCsv;
        return true;
    }

    /// <summary>Copies a string out, growing the buffer once when the plugin says it didn't fit ("" = none).</summary>
    private static string Read(delegate* unmanaged[Cdecl]<byte*, int, int> fn, int first, int max)
    {
        if (fn == null) return string.Empty;
        var buffer = new byte[Math.Max(16, Math.Min(first, max))];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int len;
            fixed (byte* b = buffer) len = fn(b, buffer.Length);
            if (len <= 0) return string.Empty;
            if (len < buffer.Length) return Encoding.UTF8.GetString(buffer, 0, len);
            if (len + 1 > max) return string.Empty;
            buffer = new byte[len + 1];
        }
        return string.Empty;
    }

    private void SetPack(ServerQuestPack? pack)
    {
        if (pack == null && Pack == null) return;
        if (pack != null && Pack != null && pack.Plugin == Pack.Plugin && pack.Revision == Pack.Revision && pack.Csv == Pack.Csv) return;
        Pack = pack;
        if (pack == null) _infoJson = string.Empty;
        Revision++;
    }

    private void DropTable()
    {
        _tableVersion = null;
        _getInfo = null;
        _getCsv = null;
        _infoJson = string.Empty;
    }
}
