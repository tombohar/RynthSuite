// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiSessionClient.cs
//  A read-only reader of RynthAi's session stats through the "RynthAi.Hub" interface
//  (Shared/RynthAiHubApi.cs, SessionStats / SessionReset). Since 2026-10-13 RynthAi's
//  session is the one XP / luminance / kills session every tracker shows: the Mini Remote's
//  Session Stats, RynthOracle's Character tab, RynthILT's rates and RynthTracker (which
//  falls back to its own session only when RynthAi isn't loaded).
//  Linked into each reader (with RynthAiHubApi.cs); pure lookup, no state beyond the table.
//
//  The table is looked up through the host (API v68) at most every RecheckMs, and again at
//  once when RynthAi has shut down (Version 0): RynthAi loading later, reloading or missing
//  are all handled, every call then just answers "no session". Pump thread.
// ============================================================================

using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

/// <summary>RynthAi's session stats for another plugin (pump thread).</summary>
public sealed unsafe class RynthAiSessionClient
{
    private const long RecheckMs = 3000;

    private RynthAiHubApiV1* _table;
    private long _nextQueryAt;

    /// <summary>RynthAi answered with its hub table (last lookup).</summary>
    public bool Connected => _table != null && _table->Version >= 1;

    /// <summary>RynthAi is loaded and answers on its hub interface now (looked up again every few seconds while it is missing).</summary>
    public bool IsLoaded(RynthCoreHost host, long now) => Table(host, now) != null;

    /// <summary>Forget the table; the next call looks it up again (login, logout).</summary>
    public void Reset()
    {
        _table = null;
        _nextQueryAt = 0;
    }

    /// <summary>
    /// RynthAi's session now. False without RynthAi, before its login or before it has a session,
    /// so the caller can fall back to its own count.
    /// </summary>
    public bool TryGet(RynthCoreHost host, long now, out SessionStatsV1 stats)
    {
        stats = default;
        RynthAiHubApiV1* t = Table(host, now);
        if (t == null || t->SessionStats == null) return false;
        SessionStatsV1 s = default;
        try
        {
            if (t->SessionStats(&s) == 0) return false;
        }
        catch { return false; }
        if (s.StartUtcTicks <= 0) return false;
        stats = s;
        return true;
    }

    /// <summary>Starts RynthAi's session over (its Reset XP button). False without RynthAi.</summary>
    public bool ResetSession(RynthCoreHost host, long now)
    {
        RynthAiHubApiV1* t = Table(host, now);
        if (t == null || t->SessionReset == null) return false;
        try { t->SessionReset(); }
        catch { return false; }
        return true;
    }

    /// <summary>The table, re-queried when it is missing or RynthAi has shut down (Version 0).</summary>
    private RynthAiHubApiV1* Table(RynthCoreHost host, long now)
    {
        if (_table != null && _table->Version >= 1) return _table;
        _table = null;
        if (now < _nextQueryAt || !host.HasGetPluginInterface) return null;
        _nextQueryAt = now + RecheckMs;
        var p = (RynthAiHubApiV1*)host.GetPluginInterface(RynthAiHub.PluginName, RynthAiHubApiV1.InterfaceName, RynthAiHubApiV1.InterfaceVersion);
        // The base size, not sizeof: an older RynthAi without the appended functions still gives SessionStats.
        if (p != null && p->Version >= 1 && p->StructSize >= RynthAiHubApiV1.BaseStructSize)
            _table = p;
        return _table;
    }

    /// <summary>
    /// The rest of RynthAi's session (XP per kill, deaths, Radiance, level cap). False without
    /// RynthAi, before its login, or from a RynthAi older than SessionStatsMore (2026-10-13).
    /// </summary>
    public bool TryGetMore(RynthCoreHost host, long now, out SessionStatsMoreV1 more)
    {
        more = default;
        RynthAiHubApiV1* t = Table(host, now);
        if (!RynthAiHubApiV1.Has(t, RynthAiHubApiV1.SessionStatsMoreOffset) || t->SessionStatsMore == null) return false;
        SessionStatsMoreV1 m = default;
        try
        {
            if (t->SessionStatsMore(&m) == 0) return false;
        }
        catch { return false; }
        more = m;
        return true;
    }
}
