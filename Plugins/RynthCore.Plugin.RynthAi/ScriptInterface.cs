using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's side of the "RynthAi.Script" interface (Shared/RynthAiScriptApi.cs): the
/// table RynthPluginQueryInterface hands out, and the functions behind it. They run on
/// the plugin pump thread (the caller's OnTick), between RynthAi's own ticks, and act on
/// the current plugin instance (Runtime.Plugin), so the table survives a re-Init.
/// </summary>
internal static unsafe class ScriptInterface
{
    private static RynthAiScriptApiV1* _table;
    private static IntPtr _out;   // the last string handed back (valid until the next call)

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiScriptApiV1.InterfaceName || version != RynthAiScriptApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiScriptApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiScriptApiV1));
            t->Version = RynthAiScriptApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthAiScriptApiV1);
            t->Evaluate = &Evaluate;
            t->GetSetting = &GetSetting;
            t->SetSetting = &SetSetting;
            t->GetSettingNames = &GetSettingNames;
            t->IsMacroRunning = &IsMacroRunning;
            t->SetMacroRunning = &SetMacroRunning;
            t->NavGoTo = &NavGoTo;
            t->NavStop = &NavStop;
            t->FindNearest = &FindNearest;
            t->DistanceTo = &DistanceTo;
            t->IsPathClear = &IsPathClear;
            t->SubmitCommand = &SubmitCommand;
            _table = t;
        }
        return (IntPtr)_table;
    }

    private static RynthAiPlugin? P => PluginExports.Runtime.Plugin;

    private static string In(byte* s) => s == null ? "" : Marshal.PtrToStringUTF8((IntPtr)s) ?? "";

    private static void Out(byte** dst, string value)
    {
        if (dst == null) return;
        IntPtr fresh = Marshal.StringToCoTaskMemUTF8(value ?? "");
        IntPtr old = _out;
        _out = fresh;
        if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
        *dst = (byte*)fresh;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Evaluate(byte* expr, byte** result)
    {
        try
        {
            var p = P;
            if (p == null) { Out(result, "RynthAi not ready"); return 0; }
            bool ok = p.ScriptEvaluate(In(expr), out string r);
            Out(result, r);
            return ok ? 1 : 0;
        }
        catch (Exception ex) { Out(result, ex.Message); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSetting(byte* name, byte** value)
    {
        try
        {
            string? v = P?.ScriptGetSetting(In(name));
            if (v == null) return 0;
            Out(value, v);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetSetting(byte* name, byte* value)
    {
        try { return P?.ScriptSetSetting(In(name), In(value)) == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSettingNames(byte** names)
    {
        try
        {
            string? all = P?.ScriptSettingNames();
            if (all == null) return 0;
            Out(names, all);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsMacroRunning()
    {
        try { return P?.ScriptMacroRunning == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetMacroRunning(int on)
    {
        try { P?.ScriptSetMacro(on != 0); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NavGoTo(double ns, double ew)
    {
        try { return P?.ScriptGoTo(ns, ew) == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void NavStop()
    {
        try { P?.ScriptStop(); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FindNearest(byte* name, int flags, uint* id, double* distance)
    {
        try
        {
            var p = P;
            if (p == null) return 0;
            if (!p.ScriptFindNearest(In(name), (flags & 1) != 0, out uint found, out double d)) return 0;
            if (id != null) *id = found;
            if (distance != null) *distance = d;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double DistanceTo(uint id)
    {
        try { return P?.ScriptDistanceTo(id) ?? -1; }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsPathClear(uint id)
    {
        try { return P?.ScriptIsPathClear(id) == false ? 0 : 1; }
        catch { return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SubmitCommand(byte* text)
    {
        try
        {
            string t = In(text).Trim();
            if (t.Length == 0 || P == null) return 0;
            P.ScriptSubmitCommand(t);
            return 1;
        }
        catch { return 0; }
    }
}

/// <summary>What the "RynthAi.Script" interface does, on the plugin instance.</summary>
public sealed partial class RynthAiPlugin
{
    internal bool ScriptEvaluate(string expression, out string result)
    {
        var engine = _metaManager?.Expressions;
        if (engine == null) { result = "not logged in yet (meta engine not ready)"; return false; }
        string r = engine.Evaluate(expression);
        if (r.StartsWith("ERR:", StringComparison.Ordinal)) { result = r.Substring(4).Trim(); return false; }
        result = r;
        return true;
    }

    internal string? ScriptGetSetting(string name)
    {
        var map = _metaManager?.Expressions.BuildSettingsMapPublic();
        return map != null && map.TryGetValue(name, out var entry) ? entry.Get() : null;
    }

    internal bool ScriptSetSetting(string name, string value)
    {
        var map = _metaManager?.Expressions.BuildSettingsMapPublic();
        if (map == null || !map.TryGetValue(name, out var entry)) return false;
        entry.Set(value);
        _dashboard?.SaveSettings();
        return true;
    }

    internal string? ScriptSettingNames()
    {
        var map = _metaManager?.Expressions.BuildSettingsMapPublic();
        return map == null ? null : string.Join("\n", map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
    }

    internal bool ScriptMacroRunning => _dashboard?.Settings.IsMacroRunning == true;

    internal void ScriptSetMacro(bool on)
    {
        if (_dashboard == null || _dashboard.Settings.IsMacroRunning == on) return;
        if (on && RefuseMacroStartForVTank()) return;   // one bot per client (Decal bridge)
        if (on) _dashboard.TogglePanelMacro();
        else HandleMacroRunCommand("stop", Array.Empty<string>());
    }

    internal bool ScriptGoTo(double ns, double ew)
    {
        var dash = _dashboard;
        if (dash == null) return false;
        var st = dash.Settings;
        if (!st.IsMacroRunning && RefuseMacroStartForVTank()) return false;   // one bot per client (Decal bridge)
        float z = 0;
        if (Host.HasGetPlayerPose) Host.TryGetPlayerPose(out _, out _, out _, out z, out _, out _, out _, out _);
        var route = new NavRouteParser { RouteType = NavRouteType.Once };
        route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = ns, EW = ew, Z = z / 240.0 });
        st.CurrentRoute = route;
        st.CurrentNavPath = string.Empty;
        st.ActiveNavIndex = 0;
        st.EnableNavigation = true;
        if (!st.IsMacroRunning) dash.TogglePanelMacro();
        return true;
    }

    internal void ScriptStop()
    {
        if (_dashboard?.Settings.IsMacroRunning == true) HandleMacroRunCommand("stop", Array.Empty<string>());
        else _navigationEngine?.Stop();
    }

    internal bool ScriptFindNearest(string name, bool portalOrNpcOnly, out uint id, out double distance)
    {
        id = 0;
        distance = -1;
        if (_objectCache == null || string.IsNullOrWhiteSpace(name) || _playerId == 0) return false;
        var wo = FindNearestPositioned(w => w.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
            && (!portalOrNpcOnly || IsPortalObject(w) || w.ObjectClass == AcObjectClass.Npc), out double d);
        if (wo == null) return false;
        id = unchecked((uint)wo.Id);
        distance = d == double.MaxValue ? -1 : Math.Round(d, 2);
        return true;
    }

    internal double ScriptDistanceTo(uint id)
    {
        if (_objectCache == null || _playerId == 0 || id == 0) return -1;
        double d = _objectCache.Distance(unchecked((int)_playerId), unchecked((int)id));
        return d == double.MaxValue ? -1 : Math.Round(d, 2);
    }

    internal bool ScriptIsPathClear(uint id)
    {
        if (id == 0 || _raycast == null || !_raycast.IsInitialized) return true;
        return !_raycast.IsTargetBlocked(Host, id, TargetingFSM.AttackType.Linear);
    }

    /// <summary>A RynthLua script's ai.Command(text): handled as a typed line, but it answers as a script's
    /// (quiet when it works, errors in chat; CommandReply.cs), and the Item Tool refuses it (a script's
    /// line is never the player at the keyboard).</summary>
    internal void ScriptSubmitCommand(string text)
        => Reply.AsScript(() => { RunItemToolUntrusted(() => HandleRynthChatSubmit(text)); return true; });
}
