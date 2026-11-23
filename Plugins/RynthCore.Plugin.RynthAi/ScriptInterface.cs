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
        if (on) _dashboard.TogglePanelMacro("a Lua script (ai.MacroRunning = true)");
        else HandleMacroRunCommand("stop", Array.Empty<string>(), "a Lua script (ai.MacroRunning = false)");
    }

    /// <summary>
    /// A script's ai.GoTo(ns, ew): a Once route to the point, the macro on. A script roaming a Thread in 80 m legs
    /// and edges back 40 m with it, and a one-point straight route walked into every tree, rock and wall on the way
    /// (2026-11-18). So the walk is planned as /ra navheading and /ra dunnav-to plan theirs: outdoors along RynthNav's
    /// path with the Outdoor Hunt's flags, in a dungeon by the dungeon map; when neither answers, the straight route
    /// as before. What the script sees is the same: true, a Once route ending on the point asked for (ranav[] name ""
    /// and finished at its end), and the character walking there.
    /// </summary>
    internal bool ScriptGoTo(double ns, double ew)
    {
        var dash = _dashboard;
        if (dash == null) return false;
        var st = dash.Settings;
        if (!st.IsMacroRunning && RefuseMacroStartForVTank()) return false;   // one bot per client (Decal bridge)
        uint cell = 0;
        float lx = 0, ly = 0, z = 0;
        bool pose = Host.HasGetPlayerPose && Host.TryGetPlayerPose(out cell, out lx, out ly, out z, out _, out _, out _, out _);
        NavRouteParser? route = null;
        string how = "straight (no position)";
        if (pose)
        {
            // Never worse than before: anything going wrong in planning leaves the straight route.
            try { route = PlanGoTo(cell, lx, ly, z, ns, ew, out how); }
            catch (Exception ex) { route = null; how = "straight (planning failed: " + ex.Message + ")"; }
        }
        if (route == null || route.Points.Count == 0)
        {
            route = new NavRouteParser { RouteType = NavRouteType.Once };
            route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = ns, EW = ew, Z = z / 240.0 });
        }
        Host.Log(string.Create(CultureInfo.InvariantCulture, $"[RynthAi] GoTo: NS {ns:0.0000} EW {ew:0.0000}, {how}"));
        st.CurrentRoute = route;
        st.CurrentNavPath = string.Empty;
        st.ActiveNavIndex = 0;
        st.EnableNavigation = true;
        if (!st.IsMacroRunning) dash.TogglePanelMacro("a Lua script (GoTo)");
        return true;
    }

    /// <summary>The planned walk for <see cref="ScriptGoTo"/>, or null (with <paramref name="how"/> saying why) for the
    /// straight route. The route always ends on (ns, ew).</summary>
    private NavRouteParser? PlanGoTo(uint cell, float lx, float ly, float z, double ns, double ew, out string how)
    {
        if ((cell & 0xFFFF) >= 0x0100)
        {
            if (!InDungeon(cell)) { how = "straight (in a building's rooms: no map plans from here)"; return null; }
            return PlanGoToDungeon(cell, lx, ly, z, ns, ew, out how);
        }

        var at = new OutdoorHunt.Vec(((cell >> 24) & 0xFF) * 192.0 + lx, ((cell >> 16) & 0xFF) * 192.0 + ly, z);
        var goal = new OutdoorHunt.Vec(DungeonGeometry.NavToWorld(ew), DungeonGeometry.NavToWorld(ns), z);
        var path = HuntPath(at, goal);
        if ((path.Status != OutdoorHunt.PathStatus.Ok && path.Status != OutdoorHunt.PathStatus.Partial) || path.Corners.Count == 0)
        {
            how = path.Status switch
            {
                OutdoorHunt.PathStatus.NoMesh => "straight (no RynthNav map here)",
                OutdoorHunt.PathStatus.Unavailable => "straight (RynthNav isn't loaded)",
                _ => "straight (RynthNav found no way)",
            };
            return null;
        }
        var route = HeadingRoute(path.Corners);
        route.NavmeshCorners = true;   // RynthNav's corners: turned early only where its mesh shows the next one (the corner gate)
        // The walk ends on the point asked for, as the straight route did: the last corner is moved onto it (RynthNav
        // put it on the mesh, its height kept), or, a way that ends short (partial, the point off the mesh), the
        // point follows it.
        var last = route.Points[^1];
        double shortYd = Math.Sqrt(Sq(DungeonGeometry.NavToWorld(last.EW) - goal.X) + Sq(DungeonGeometry.NavToWorld(last.NS) - goal.Y));
        if (shortYd <= GoToEndSnapYd) { last.NS = ns; last.EW = ew; }
        else route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = ns, EW = ew, Z = last.Z != 0 ? last.Z : z / 240.0 });
        var inv = CultureInfo.InvariantCulture;
        how = path.Status == OutdoorHunt.PathStatus.Ok
            ? string.Create(inv, $"round obstacles on RynthNav's map, {route.Points.Count} point(s), {path.Length:0} yd")
            : string.Create(inv, $"round obstacles on RynthNav's map as far as it goes ({shortYd:0} yd short), then straight, {route.Points.Count} point(s)");
        return route;
    }

    // RynthNav's last corner this close to the point asked for is the point (it snapped it onto the mesh).
    private const double GoToEndSnapYd = 1.0;
    // A dungeon point this far from every room's centre isn't in this dungeon (NavRecovery's DungeonCellReachYd).
    private const double GoToDungeonReachYd = 30.0;

    private static double Sq(double v) => v * v;

    /// <summary>A GoTo in a dungeon: the dungeon map's walk from the character's cell to the point's, as /ra dunnav-to.</summary>
    private NavRouteParser? PlanGoToDungeon(uint cell, float lx, float ly, float z, double ns, double ew, out string how)
    {
        uint lb = cell >> 16;
        var map = DungeonMapOverride?.Invoke(lb);
        var cellDat = _raycast?.GeometryLoader.CellDat;
        if (map == null && (cellDat == null || !cellDat.IsLoaded)) { how = "straight (the dungeon map isn't loaded yet)"; return null; }
        var graph = map?.Graph ?? DungeonPathfinder.GetGraph(lb, cellDat!);
        if (graph.Count == 0) { how = "straight (no dungeon map here)"; return null; }
        var geo = map != null ? map.Value.Geo : DungeonPathfinder.GetGeometry(lb, cellDat!, _raycast?.GeometryLoader?.DungeonLOS);
        double lbX = ((cell >> 24) & 0xFF) * 192.0, lbY = ((cell >> 16) & 0xFF) * 192.0;
        double pNs = DungeonGeometry.WorldToNav(lbY + ly), pEw = DungeonGeometry.WorldToNav(lbX + lx);
        uint start = graph.ContainsKey(cell) ? cell : DungeonPathfinder.NearestCell(graph, pNs, pEw, z);
        // The point's room: the cell whose floor holds it at the character's height (a script gives no z), else the nearest.
        uint goal = DungeonPathfinder.CellAt(graph, geo, new NavPoint { Type = NavPointType.Point, NS = ns, EW = ew, Z = z / 240.0 });
        if (start == 0 || goal == 0 || !graph.TryGetValue(goal, out var goalNode)) { how = "straight (not on the dungeon map)"; return null; }
        if (Math.Sqrt(Sq(goalNode.NS - ns) + Sq(goalNode.EW - ew)) * 240.0 > GoToDungeonReachYd)
        {
            how = "straight (the point isn't near any room of this dungeon)";
            return null;
        }
        var hazards = _objectCache?.GetHazardCells();
        var path = DungeonPathfinder.FindPath(start, goal, graph, hazards);
        if (path.Count == 0) { how = $"straight (no walk from {start:X8} to {goal:X8} on the dungeon map)"; return null; }
        var route = DungeonPathfinder.BuildNavRoute(path, graph, ns, ew, geo, hazards, pNs, pEw, z, z);
        how = $"by the dungeon map, {path.Count} cell(s), {route.Points.Count} point(s)";
        return route;
    }

    internal void ScriptStop()
    {
        if (_dashboard?.Settings.IsMacroRunning == true) HandleMacroRunCommand("stop", Array.Empty<string>(), "a Lua script (Stop)");
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
    {
        using (_dashboard?.Settings.MacroSource($"a Lua script's command '{text}'"))
            Reply.AsScript(() => { RunItemToolUntrusted(() => HandleRynthChatSubmit(text)); return true; });
    }
}
