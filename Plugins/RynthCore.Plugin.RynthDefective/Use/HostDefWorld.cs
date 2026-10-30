using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective.Use;

/// <summary>
/// The controller's view of the game through the engine host. Plugin pump thread only (the
/// plugin's tick), which is also where RynthAi's interfaces may be called.
/// </summary>
internal sealed unsafe class HostDefWorld : IDefWorld
{
    private const long SnapshotMaxAgeMs = 250;

    private readonly RynthCoreHost _host;
    private readonly HostInventory _reader = new();
    private List<CarriedItem> _items = new();
    private long _itemsAtMs = long.MinValue / 4;
    private int _revision;

    public HostDefWorld(RynthCoreHost host) => _host = host;

    public long NowMs => Environment.TickCount64;

    public bool InWorld => _host.HasGetPlayerId && _host.GetPlayerId() != 0 && !(_host.HasIsPortaling && _host.IsPortaling());
    public uint PlayerId => _host.HasGetPlayerId ? _host.GetPlayerId() : 0;
    public uint SelectedId => _host.HasGetSelectedItemId ? _host.GetSelectedItemId() : 0;
    public int BusyCount => _host.HasGetBusyState ? Math.Max(0, _host.GetBusyState()) : 0;
    public bool Animating => _host.HasGetCastBusyState && _host.GetCastBusyState() != 0;
    public int UseDoneSeq => _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0;

    public uint UseDoneError(int seqAtSend) =>
        _host.TryGetLastUseDone(out int seq, out uint error) && seq == seqAtSend + 1 ? error : 0;
    public int CombatMode => _host.GetCurrentCombatMode();

    public bool? BotWaitingOnServer => BotActivity.Read(_host) is { } a ? (a & RynthAiActivity.ServerAction) != 0 : null;
    public bool? BotMacroRunning => BotActivity.Read(_host) is { } a ? (a & RynthAiActivity.MacroRunning) != 0 : null;
    public bool? BotPaced => BotYield.Available(_host) && BotActivity.Read(_host) is { } a ? (a & RynthAiActivity.PacedAction) != 0 : null;
    public bool HoldBot(uint ms) => BotYield.Hold(_host, ms);
    public void ReleaseBot() => BotYield.Release(_host);

    public int RefusalSeq => _host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int seq, out _, out _, out _) ? seq : 0;

    public bool RefusalNamed(uint objectId) =>
        _host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out _, out _, out _, out uint obj) && obj == objectId;

    /// <summary>The inventory, re-read when it is older than a quarter second.</summary>
    public IReadOnlyList<CarriedItem> Items() => Snapshot(SnapshotMaxAgeMs);

    /// <summary>The inventory, re-read when it is older than <paramref name="maxAgeMs"/>.</summary>
    public List<CarriedItem> Snapshot(long maxAgeMs)
    {
        long now = NowMs;
        if (now - _itemsAtMs >= maxAgeMs)
        {
            _items = _reader.Read(_host, now);
            _itemsAtMs = now;
            _revision++;
        }
        return _items;
    }

    /// <summary>Counts re-reads (the plugin redraws and re-binds slots when it moved).</summary>
    public int Revision => _revision;

    public CarriedItem? Live(uint id) => _reader.ReadOne(_host, id, NowMs);

    public bool UseObject(uint id) => _host.HasUseObject && _host.UseObject(id);
    public bool UseObjectOn(uint id, uint target) => _host.HasUseObjectOn && _host.UseObjectOn(id, target);
    public bool SetCombatMode(int mode) => _host.HasChangeCombatMode && _host.ChangeCombatMode(mode);

    // ── Spells (spell slots) ────────────────────────────────────────────────

    /// <summary>The character's spells are re-read when older than this (a spell scribed since shows within it).</summary>
    public const long KnownMaxAgeMs = 5000;
    private uint[] _knownBuf = new uint[2048];
    private HashSet<uint>? _known;
    private SpellInfo[] _knownList = Array.Empty<SpellInfo>();
    private long _knownAtMs = long.MinValue / 4;

    public bool CanCast => _host.HasCastSpell;

    public bool CastSpell(uint target, uint spellId) =>
        _host.HasCastSpell && spellId != 0 && spellId <= int.MaxValue && _host.CastSpell(target, (int)spellId);

    /// <summary>
    /// From the spellbook the engine reads (ReadKnownSpells; RynthAi's note: the engine's IsSpellKnown
    /// answers "known" for spells the character doesn't have, so it isn't asked). A "no" is checked
    /// against a fresh read before it is believed. Null when the spellbook can't be read.
    /// </summary>
    public bool? KnowsSpell(uint spellId)
    {
        HashSet<uint>? known = KnownIds(KnownMaxAgeMs);
        if (known == null) return null;
        if (known.Contains(spellId)) return true;
        known = KnownIds(0);
        return known == null ? null : known.Contains(spellId);
    }

    /// <summary>The character's spells with names, A-Z (the bar's spell search); empty when they can't be read.</summary>
    public SpellInfo[] KnownSpells(long maxAgeMs)
    {
        KnownIds(maxAgeMs);
        return _knownList;
    }

    private HashSet<uint>? KnownIds(long maxAgeMs)
    {
        long now = NowMs;
        if (now - _knownAtMs < maxAgeMs) return _known;
        _knownAtMs = now;
        if (!_host.HasReadKnownSpells)
        {
            _known = null;
            _knownList = Array.Empty<SpellInfo>();
            return null;
        }
        int n = _host.ReadKnownSpells(_knownBuf, _knownBuf.Length);
        if (n > _knownBuf.Length && n <= 65536)
        {
            _knownBuf = new uint[n];
            n = _host.ReadKnownSpells(_knownBuf, _knownBuf.Length);
        }
        if (n <= 0)
        {
            // Not readable yet (before the character's description arrives): can't tell.
            _known = null;
            _knownList = Array.Empty<SpellInfo>();
            return null;
        }
        n = Math.Min(n, _knownBuf.Length);
        var set = new HashSet<uint>(n);
        for (int i = 0; i < n; i++) if (_knownBuf[i] != 0) set.Add(_knownBuf[i]);
        if (_known == null || !set.SetEquals(_known)) _knownList = SpellTable.Known(_knownBuf.AsSpan(0, n));
        _known = set;
        return set;
    }

    public void Reset()
    {
        _reader.Reset();
        _items = new List<CarriedItem>();
        _itemsAtMs = long.MinValue / 4;
        _known = null;
        _knownList = Array.Empty<SpellInfo>();
        _knownAtMs = long.MinValue / 4;
    }
}

/// <summary>
/// RynthAi's "RynthAi.Yield" interface (2026-10-29): the player's key press takes RynthAi's turn, so
/// RynthAi starts nothing new until the press has gone. Resolved again every call (a RynthAi reload
/// frees the table). Absent on a RynthAi from before it: nothing is held then.
/// </summary>
internal static unsafe class BotYield
{
    private static RynthAiYieldApiV1* Table(RynthCoreHost host)
    {
        if (!host.HasGetPluginInterface) return null;
        try
        {
            var t = (RynthAiYieldApiV1*)host.GetPluginInterface("RynthAi", RynthAiYieldApiV1.InterfaceName, RynthAiYieldApiV1.InterfaceVersion);
            return t != null && t->Version >= 1 && t->StructSize >= (uint)sizeof(RynthAiYieldApiV1) ? t : null;
        }
        catch
        {
            return null;   // a RynthAi mid-reload
        }
    }

    public static bool Available(RynthCoreHost host) => Table(host) != null;

    /// <summary>Asks for the player's turn for <paramref name="ms"/>; true when RynthAi holds (its pacing is on).</summary>
    public static bool Hold(RynthCoreHost host, uint ms)
    {
        var t = Table(host);
        if (t == null || t->HoldForPlayer == null) return false;
        try { return t->HoldForPlayer(ms) != 0; }
        catch { return false; }
    }

    public static void Release(RynthCoreHost host)
    {
        var t = Table(host);
        if (t == null || t->ReleaseHold == null) return;
        try { t->ReleaseHold(); }
        catch { }
    }
}

/// <summary>
/// RynthAi's activity bits: its "RynthAi.Activity" interface; on an older RynthAi without it, the
/// macro bit from "RynthAi.Status"; null when RynthAi isn't loaded or not ready. Resolved again every
/// call: a RynthAi reload frees the tables. Read only: nothing here changes what RynthAi does.
/// </summary>
internal static unsafe class BotActivity
{
    public static RynthAiActivity? Read(RynthCoreHost host)
    {
        if (!host.HasGetPluginInterface) return null;
        try
        {
            var act = (RynthAiActivityApiV1*)host.GetPluginInterface("RynthAi", RynthAiActivityApiV1.InterfaceName, RynthAiActivityApiV1.InterfaceVersion);
            if (act != null && act->Version >= 1 && act->StructSize >= (uint)sizeof(RynthAiActivityApiV1) && act->GetActivityFlags != null)
            {
                uint flags = 0;
                return act->GetActivityFlags(&flags) != 0 ? (RynthAiActivity)flags : null;
            }
            var status = (RynthAiStatusApiV1*)host.GetPluginInterface("RynthAi", RynthAiStatusApiV1.InterfaceName, RynthAiStatusApiV1.InterfaceVersion);
            if (status != null && status->Version >= 1 && status->StructSize >= (uint)sizeof(RynthAiStatusApiV1) && status->GetStatusJson != null)
            {
                byte* r = null;
                if (status->GetStatusJson(&r) != 0 && r != null)
                    return FromStatusJson(Marshal.PtrToStringUTF8((IntPtr)r));
            }
        }
        catch
        {
            // A RynthAi mid-reload: treat as not there this tick.
        }
        return null;
    }

    /// <summary>The macro bit an older RynthAi's status JSON ({macro, action, ...}) gives (no server-wait bit).</summary>
    internal static RynthAiActivity? FromStatusJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || json[0] != '{') return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            bool macro = doc.RootElement.TryGetProperty("macro", out JsonElement m) && m.ValueKind == JsonValueKind.True;
            return macro ? RynthAiActivity.MacroRunning : RynthAiActivity.None;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
