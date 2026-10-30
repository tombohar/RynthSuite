using System;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// RynthAi's "RynthAi.Script" interface (Shared/RynthAiScriptApi.cs), resolved through the
/// host (API v68) and wrapped as plain C# calls. Everything here runs on the plugin pump
/// thread. The table is looked up lazily, and RynthLuaPlugin.OnTick resets it every tick:
/// RynthAi may load after RynthLua, be reloaded (its code unloaded), or not be installed at all (then every call reports
/// "RynthAi isn't loaded" and scripts that only use chat, timers and the game keep working).
/// </summary>
internal sealed unsafe class RynthAiBridge
{
    public const string NotLoaded = "RynthAi isn't loaded (needs RynthAi and RynthCore 2026.9.29 or newer)";

    private readonly RynthCoreHost _host;
    private RynthAiScriptApiV1* _t;
    private bool _checked;
    private long _checkedAt;
    private const long RecheckMs = 3000;

    public RynthAiBridge(RynthCoreHost host) => _host = host;

    /// <summary>True when RynthAi's interface is there right now.</summary>
    public bool Available => Table != null;

    private RynthAiScriptApiV1* Table
    {
        get
        {
            long now = Environment.TickCount64;
            if (_checked && now - _checkedAt < RecheckMs) return _t;
            _checked = true;
            _checkedAt = now;
            IntPtr p = _host.GetPluginInterface("RynthAi", RynthAiScriptApiV1.InterfaceName, RynthAiScriptApiV1.InterfaceVersion);
            var t = (RynthAiScriptApiV1*)p;
            _t = t != null && t->Version >= 1 && t->StructSize >= (uint)sizeof(RynthAiScriptApiV1) ? t : null;
            return _t;
        }
    }

    /// <summary>Forget the tables (logout, RynthAi reload); the next call looks them up again.</summary>
    public void Reset()
    {
        _t = null;
        _checked = false;
        _c = null;
        _cChecked = false;
    }

    // -- RynthAi.Combat (Shared/RynthAiCombatApi.cs): the script-provided combat hook --

    private RynthAiCombatApiV1* _c;
    private bool _cChecked;
    private long _cCheckedAt;

    private RynthAiCombatApiV1* Combat
    {
        get
        {
            long now = Environment.TickCount64;
            if (_cChecked && now - _cCheckedAt < RecheckMs) return _c;
            _cChecked = true;
            _cCheckedAt = now;
            IntPtr p = _host.GetPluginInterface("RynthAi", RynthAiCombatApiV1.InterfaceName, RynthAiCombatApiV1.InterfaceVersion);
            var t = (RynthAiCombatApiV1*)p;
            _c = t != null && t->Version >= 1 && t->StructSize >= (uint)sizeof(RynthAiCombatApiV1) ? t : null;
            return _c;
        }
    }

    /// <summary>True when RynthAi serves the combat hook right now.</summary>
    public bool CombatAvailable => Combat != null;

    /// <summary>Takes or renews the hook; the kinds granted (0 = refused or no RynthAi).</summary>
    public uint CombatClaim(uint kinds, uint leaseMs, uint attackTimeoutMs, uint healTimeoutMs, string owner)
    {
        var t = Combat;
        if (t == null) return 0;
        using var o = new Utf8(owner);
        return unchecked((uint)t->Claim(kinds, leaseMs, attackTimeoutMs, healTimeoutMs, o.P));
    }

    public void CombatRelease(uint kinds)
    {
        var t = Combat;
        if (t != null) t->Release(kinds);
    }

    /// <summary>True when a request of <paramref name="kind"/> is open and unanswered.</summary>
    public bool CombatPoll(uint kind, out CombatRequestV1 request)
    {
        request = default;
        var t = Combat;
        if (t == null) return false;
        CombatRequestV1 r = default;
        r.StructSize = (uint)sizeof(CombatRequestV1);
        if (t->Poll(kind, &r) == 0) return false;
        request = r;
        return true;
    }

    /// <summary>The answer to request <paramref name="seq"/>; false = stale (or no RynthAi).</summary>
    public bool CombatDecide(uint seq, uint kind, uint spellId, uint targetId, uint flags)
    {
        var t = Combat;
        return t != null && t->Decide(seq, kind, spellId, targetId, flags) != 0;
    }

    /// <summary>RynthAi's scanned targets (id, metres), nearest first.</summary>
    public (uint Id, float Distance)[] CombatScanned()
    {
        var t = Combat;
        if (t == null) return Array.Empty<(uint, float)>();
        const int Cap = 64;
        uint* ids = stackalloc uint[Cap];
        float* dist = stackalloc float[Cap];
        int n = t->GetScanned(ids, dist, Cap);
        if (n <= 0) return Array.Empty<(uint, float)>();
        n = Math.Min(n, Cap);
        var list = new (uint, float)[n];
        for (int i = 0; i < n; i++) list[i] = (ids[i], dist[i]);
        return list;
    }

    private static string Copy(byte* s) => s == null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)s) ?? string.Empty;

    /// <summary>A UTF-8 copy of <paramref name="s"/> for the duration of one call.</summary>
    private readonly ref struct Utf8
    {
        public readonly IntPtr Ptr;
        public Utf8(string s) => Ptr = Marshal.StringToCoTaskMemUTF8(s ?? string.Empty);
        public byte* P => (byte*)Ptr;
        public void Dispose() => Marshal.FreeCoTaskMem(Ptr);
    }

    public bool Evaluate(string expression, out string result)
    {
        var t = Table;
        if (t == null) { result = NotLoaded; return false; }
        using var e = new Utf8(expression);
        byte* r = null;
        int ok = t->Evaluate(e.P, &r);
        result = Copy(r);
        return ok != 0;
    }

    public string? GetSetting(string name)
    {
        var t = Table;
        if (t == null) return null;
        using var n = new Utf8(name);
        byte* r = null;
        return t->GetSetting(n.P, &r) != 0 ? Copy(r) : null;
    }

    public bool SetSetting(string name, string value)
    {
        var t = Table;
        if (t == null) return false;
        using var n = new Utf8(name);
        using var v = new Utf8(value);
        return t->SetSetting(n.P, v.P) != 0;
    }

    public string[] SettingNames()
    {
        var t = Table;
        if (t == null) return Array.Empty<string>();
        byte* r = null;
        return t->GetSettingNames(&r) != 0 ? Copy(r).Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    }

    public bool MacroRunning
    {
        get { var t = Table; return t != null && t->IsMacroRunning() != 0; }
        set { var t = Table; if (t != null) t->SetMacroRunning(value ? 1 : 0); }
    }

    public bool GoTo(double ns, double ew)
    {
        var t = Table;
        return t != null && t->NavGoTo(ns, ew) != 0;
    }

    public void Stop()
    {
        var t = Table;
        if (t != null) t->NavStop();
    }

    public bool FindNearest(string name, bool portalOrNpcOnly, out uint id, out double distance)
    {
        id = 0; distance = -1;
        var t = Table;
        if (t == null) return false;
        using var n = new Utf8(name);
        uint found = 0; double d = -1;
        if (t->FindNearest(n.P, portalOrNpcOnly ? 1 : 0, &found, &d) == 0) return false;
        id = found; distance = d;
        return true;
    }

    public double DistanceTo(uint id)
    {
        var t = Table;
        return t == null ? -1 : t->DistanceTo(id);
    }

    public bool IsPathClear(uint id)
    {
        var t = Table;
        return t == null || t->IsPathClear(id) != 0;
    }

    /// <summary>A chat line as if typed. Without RynthAi, falls back to AC's own parser.</summary>
    public void SubmitCommand(string text)
    {
        var t = Table;
        if (t != null)
        {
            using var c = new Utf8(text);
            if (t->SubmitCommand(c.P) != 0) return;
        }
        if (_host.HasInvokeChatParser) _host.InvokeChatParser(text);
    }
}
