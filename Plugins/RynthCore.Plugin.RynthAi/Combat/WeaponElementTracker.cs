using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Reads each listed weapon's element from its properties (<see cref="WeaponElements"/>) and
/// identifies the ones whose DamageType isn't known yet: one identify at a time, at most one
/// every <see cref="RequestGapMs"/>, at most <see cref="MaxRequestsPerItem"/> per weapon. The
/// engine (2026.9.30.5+) keeps every identified property, so a weapon is identified once.
/// Before the identify, CreateObject gives the icon's element glow (UiEffects) and the name.
/// </summary>
internal sealed class WeaponElementTracker
{
    private readonly RynthCoreHost _host;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<int, int> _requests = new();
    private int _inFlightId;
    private DateTime _inFlightAt = DateTime.MinValue;
    private DateTime _lastRequestAt = DateTime.MinValue;

    public double RequestGapMs { get; set; } = 2000;
    public double RequestTimeoutMs { get; set; } = 5000;
    public int MaxRequestsPerItem { get; set; } = 2;
    /// <summary>Identify requests sent (diagnostics and tests).</summary>
    public int RequestsSent { get; private set; }

    public WeaponElementTracker(RynthCoreHost host, Func<DateTime>? now = null)
    {
        _host = host;
        _now = now ?? (() => RynthClock.Now);
    }

    /// <summary>What is known about <paramref name="id"/>'s element right now.</summary>
    public WeaponElements.Info Read(int id, string? name) => ReadFrom(_host, id, name);

    /// <summary>The same, for callers without a tracker (the Items panel's Add buttons).</summary>
    public static WeaponElements.Info ReadFrom(RynthCoreHost host, int id, string? name)
    {
        uint uid = unchecked((uint)id);
        int? ReadInt(uint stype) => host.HasGetObjectIntProperty && host.TryGetObjectIntProperty(uid, stype, out int v) ? v : null;
        double? mod = null;
        if (host.HasGetObjectDoubleProperty && host.TryGetObjectDoubleProperty(uid, WeaponElements.PropElementalDamageMod, out double m))
            mod = m;
        return WeaponElements.Detect(ReadInt(WeaponElements.PropDamageType), ReadInt(WeaponElements.PropImbuedEffect),
            ReadInt(WeaponElements.PropUiEffects), name, mod);
    }

    /// <summary>True once the weapon has been identified (its DamageType, if it has one, is in).</summary>
    public bool IsIdentified(int id)
    {
        uint uid = unchecked((uint)id);
        if (_host.HasHasAppraisalData) return _host.HasAppraisalData(uid);
        return ReadInt(uid, WeaponElements.PropDamageType) is int dt && dt != 0;
    }

    /// <summary>
    /// Sends at most one identify, for the first of <paramref name="ids"/> that isn't identified
    /// yet. Call it every few hundred milliseconds; it paces itself. Returns the id asked for (0 = none).
    /// </summary>
    public int Pump(IEnumerable<int> ids)
    {
        if (!_host.HasRequestId) return 0;
        DateTime now = _now();
        if (_inFlightId != 0)
        {
            if (!IsIdentified(_inFlightId) && (now - _inFlightAt).TotalMilliseconds < RequestTimeoutMs) return 0;
            _inFlightId = 0;
        }
        if ((now - _lastRequestAt).TotalMilliseconds < RequestGapMs) return 0;
        foreach (int id in ids)
        {
            if (id == 0 || IsIdentified(id)) continue;
            int n = _requests.TryGetValue(id, out int c) ? c : 0;
            if (n >= MaxRequestsPerItem) continue;
            bool taken = false;
            try { taken = _host.RequestId(unchecked((uint)id)); } catch { }
            _lastRequestAt = now;   // paces a retry as it paces a request
            if (!taken) return 0;   // the engine refused (a death or a portal): not counted, asked again after the gap
            _requests[id] = n + 1;
            _inFlightId = id;
            _inFlightAt = now;
            RequestsSent++;
            return id;
        }
        return 0;
    }

    /// <summary>Forget the request counts (a relog: the ids are new).</summary>
    public void Reset()
    {
        _requests.Clear();
        _inFlightId = 0;
    }

    private int? ReadInt(uint id, uint stype)
    {
        if (!_host.HasGetObjectIntProperty) return null;
        return _host.TryGetObjectIntProperty(id, stype, out int v) ? v : (int?)null;
    }
}
