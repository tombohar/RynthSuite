// ServerHubClient.cs - RynthAi's side of a server plugin's hub ("RynthServer.Hub",
// Plugins/Shared/RynthServerHubApi.cs; was IltClient until 2026-10-20): the tabs, sub-tabs and sections
// the Mini Remote hosts for the server you're on, its feature bits (the Quests tab's /myquests bit),
// its texts and settings, its "/ra" verbs, and its pet gate. RynthAi knows no server's content: it
// shows what the plugin offers.
//
// The plugin is found through RynthCore's server list (plugin API v86, GetServerStateJson): the
// active server's owner plugin first, else the owners of the other servers it lists (a server plugin
// may follow its own world check or override), preferring one that says this world is its server.
// Without one (not installed, too old, not loaded yet, an engine before v86: any retail-like world)
// every call answers "nothing": no tab, no section, unknown feature bits, no verbs. The table is
// looked up on the pump thread (Tick) at most every RecheckMs and kept in a volatile field for the
// render thread; the plugin never frees it (Version goes to 0 on its shutdown), so reading a stale
// pointer is safe and a Version of 0 means "look again".
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

/// <summary>One tab as the server plugin describes it (a copy).</summary>
internal readonly record struct ServerTabInfo(string Id, string Label, float Width, float Height, bool Shown, bool Available);

internal sealed unsafe class ServerHubClient
{
    private const long RecheckMs = 3000;
    private const int MaxTabs = 16;

    private readonly RynthCoreHost _host;
    private IntPtr _table;          // volatile reads/writes: the render thread uses what the pump found
    private long _checkedAt;
    private bool _checkedOnce;      // false: look now (a sentinel time would overflow "now - _checkedAt")
    private readonly ServerHubTabV1[] _tabBuf = new ServerHubTabV1[MaxTabs];
    private readonly List<ServerTabInfo> _tabs = new();

    private bool _pinned;           // tests: a table handed in, never looked up

    public ServerHubClient(RynthCoreHost host) => _host = host;

    /// <summary>Tests only: use <paramref name="table"/> (a fake server plugin's table) and never look it up.</summary>
    internal void UseTableForTests(IntPtr table)
    {
        _pinned = true;
        _checkedOnce = true;
        System.Threading.Volatile.Write(ref _table, table);
    }

    /// <summary>Pump thread: look the table up again when it's due, missing or shut down.</summary>
    public void Tick()
    {
        if (_pinned) return;
        long now = Environment.TickCount64;
        var t = (RynthServerHubApiV1*)System.Threading.Volatile.Read(ref _table);
        if (!LookupDue(_checkedOnce, _checkedAt, now, haveTable: t != null, tableLive: t != null && t->Version >= 1)) return;
        _checkedOnce = true;
        _checkedAt = now;
        IntPtr found = IntPtr.Zero;
        if (_host.HasGetPluginInterface)
        {
            EngineServerState? st = EngineState();
            IReadOnlyList<string> owners = OwnerOrder(st);
            bool activeFirst = st != null && st.ActivePlugin.Length > 0;
            IntPtr first = IntPtr.Zero;
            for (int i = 0; i < owners.Count && found == IntPtr.Zero; i++)
            {
                IntPtr p = Look(owners[i]);
                if (p == IntPtr.Zero) continue;
                // The active server's own plugin is the one; another server's only when it claims this world.
                var hub = (RynthServerHubApiV1*)p;
                if ((i == 0 && activeFirst) || (hub->IsServerWorld != null && hub->IsServerWorld() != 0)) found = p;
                else if (first == IntPtr.Zero) first = p;
            }
            // None claims this world: the first that serves a hub (its tabs say they're hidden).
            if (found == IntPtr.Zero) found = first;
        }
        System.Threading.Volatile.Write(ref _table, found);
    }

    /// <summary>A hub table <paramref name="plugin"/> serves, or 0.</summary>
    private IntPtr Look(string plugin)
    {
        var p = (RynthServerHubApiV1*)_host.GetPluginInterface(plugin, RynthServerHubApiV1.InterfaceName, RynthServerHubApiV1.InterfaceVersion);
        // Any table at least as big as the first release's: newer functions are checked with Has.
        return p != null && p->Version >= 1 && p->StructSize >= RynthServerHubApiV1.BaseStructSize ? (IntPtr)p : IntPtr.Zero;
    }

    /// <summary>RynthCore's server list (null when none, or the engine is pre-v86). Pump thread.</summary>
    private EngineServerState? EngineState()
    {
        try
        {
            if (!_host.HasGetServerStateJson || !_host.TryGetServerStateJson(out string json)) return null;
            return EngineServerState.Parse(json);
        }
        catch { return null; }
    }

    /// <summary>
    /// The plugins to ask for a hub, in order: the active server's owner, then every other server's
    /// owner RynthCore lists (each once). Empty without a server list. Pure.
    /// </summary>
    internal static IReadOnlyList<string> OwnerOrder(EngineServerState? st)
    {
        var list = new List<string>();
        if (st == null) return list;
        if (st.ActivePlugin.Length > 0) list.Add(st.ActivePlugin);
        foreach (EngineServer s in st.Servers)
        {
            if (s.Plugin.Length == 0) continue;
            bool seen = false;
            foreach (string p in list)
                if (p.Equals(s.Plugin, StringComparison.OrdinalIgnoreCase)) { seen = true; break; }
            if (!seen) list.Add(s.Plugin);
        }
        return list;
    }

    /// <summary>Forget the table (login, logout); the next Tick looks again.</summary>
    public void Reset()
    {
        _pinned = false;
        System.Threading.Volatile.Write(ref _table, IntPtr.Zero);
        _checkedOnce = false;
    }

    /// <summary>
    /// Whether Tick looks the table up again. Pure. Always on the first call (and after Reset), at
    /// once when the table the plugin gave is shut down (Version 0), else every <see cref="RecheckMs"/>.
    /// </summary>
    internal static bool LookupDue(bool checkedOnce, long checkedAt, long now, bool haveTable, bool tableLive)
    {
        if (!checkedOnce) return true;
        if (haveTable && !tableLive) return true;
        return now - checkedAt >= RecheckMs;
    }

    private RynthServerHubApiV1* T
    {
        get
        {
            var t = (RynthServerHubApiV1*)System.Threading.Volatile.Read(ref _table);
            return t != null && t->Version >= 1 ? t : null;
        }
    }

    /// <summary>A server plugin answers (any thread).</summary>
    public bool Loaded => T != null;

    /// <summary>This world is the plugin's server (or forced there), as it sees it. Any thread.</summary>
    public bool ServerActive
    {
        get
        {
            var t = T;
            return t != null && t->IsServerWorld != null && t->IsServerWorld() != 0;
        }
    }

    /// <summary>The server's bit for a feature: -1 unknown (and always off its server / without a plugin), 0 off, 1 on. Any thread.</summary>
    public int Feature(string key)
    {
        var t = T;
        if (t == null || t->GetFeature == null) return -1;
        using var k = new Utf8(key);
        return t->GetFeature(k.P);
    }

    /// <summary>The server positively reported the feature off.</summary>
    public bool IsOff(string key) => Feature(key) == 0;

    /// <summary>The server positively reported the feature on.</summary>
    public bool IsOn(string key) => Feature(key) == 1;

    /// <summary>
    /// A short text from the server plugin (RynthServerHub.Text*: the session report's tail, its server id);
    /// "" without one, from an older one, or for nothing. Any thread.
    /// </summary>
    public string Text(string id)
    {
        var t = T;
        if (t == null || !RynthServerHubApiV1.Has(t, RynthServerHubApiV1.GetTextOffset) || t->GetText == null) return string.Empty;
        byte* buf = stackalloc byte[256];
        buf[0] = 0;
        int n;
        using (var k = new Utf8(id)) n = t->GetText(k.P, buf, 256);
        if (n <= 0) return string.Empty;
        buf[255] = 0;
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? string.Empty;
    }

    /// <summary>
    /// This character forces the plugin's server (its own override; set there or in
    /// RynthCore's Options > Diagnostics, never by RynthAi). Any thread.
    /// </summary>
    public bool ServerForced
    {
        get
        {
            var t = T;
            return t != null && t->GetForceServer != null && t->GetForceServer() != 0;
        }
    }

    /// <summary>Render thread: the plugin's tabs with their flags (empty without one). The list is reused.</summary>
    public IReadOnlyList<ServerTabInfo> Tabs()
    {
        _tabs.Clear();
        var t = T;
        if (t == null || t->GetTabs == null) return _tabs;
        int n;
        fixed (ServerHubTabV1* buf = _tabBuf) n = t->GetTabs(buf, MaxTabs);
        n = Math.Min(n, MaxTabs);
        for (int i = 0; i < n; i++)
        {
            ref ServerHubTabV1 tab = ref _tabBuf[i];
            string id, label;
            fixed (byte* pid = tab.Id) id = Marshal.PtrToStringUTF8((IntPtr)pid) ?? string.Empty;
            fixed (byte* plabel = tab.Label) label = Marshal.PtrToStringUTF8((IntPtr)plabel) ?? string.Empty;
            if (id.Length == 0) continue;
            _tabs.Add(new ServerTabInfo(id, label.Length > 0 ? label : id, tab.Width, tab.Height,
                (tab.Flags & RynthServerHub.TabShown) != 0, (tab.Flags & RynthServerHub.TabAvailable) != 0));
        }
        return _tabs;
    }

    /// <summary>Render thread: one of the plugin's tabs into the current child window; false = nothing drawn.</summary>
    public bool RenderTab(string id)
    {
        var t = T;
        if (t == null || t->RenderTab == null) return false;
        using var s = new Utf8(id);
        return t->RenderTab(s.P) != 0;
    }

    /// <summary>Render thread: a server section (RynthServerHub.Section*); false = nothing drawn (not its server, or no plugin).</summary>
    public bool RenderSection(string id)
    {
        var t = T;
        if (t == null || t->RenderSection == null) return false;
        using var s = new Utf8(id);
        return t->RenderSection(s.P) != 0;
    }

    /// <summary>One of the plugin's "/ra" verbs ("hub bank", ...); false when it doesn't take it.</summary>
    public bool Command(string text)
    {
        var t = T;
        if (t == null || t->Command == null) return false;
        using var s = new Utf8(text);
        return t->Command(s.P) != 0;
    }

    /// <summary>
    /// The server plugin's answer to "may an EMPTY pet essence be summoned?" (the server refills it;
    /// e.g. a refill charm). False without one, or from an older one
    /// that doesn't answer: an empty essence is then never used. Any thread.
    /// </summary>
    public bool AllowSummonOnEmpty()
    {
        var t = T;
        return t != null && RynthServerHubApiV1.Has(t, RynthServerHubApiV1.AllowSummonOnEmptyOffset)
               && t->AllowSummonOnEmpty != null && t->AllowSummonOnEmpty() != 0;
    }

    /// <summary>One of the server plugin's mirrored settings (RynthServerHub.Setting*): its value, -1 when not offered. Any thread.</summary>
    public int Setting(string key)
    {
        var t = T;
        if (t == null || !RynthServerHubApiV1.Has(t, RynthServerHubApiV1.GetSettingOffset) || t->GetSetting == null) return -1;
        using var k = new Utf8(key);
        return t->GetSetting(k.P);
    }

    /// <summary>Changes one of those settings; false when the plugin doesn't take it. Any thread.</summary>
    public bool SetSetting(string key, int value)
    {
        var t = T;
        if (t == null || !RynthServerHubApiV1.Has(t, RynthServerHubApiV1.SetSettingOffset) || t->SetSetting == null) return false;
        using var k = new Utf8(key);
        return t->SetSetting(k.P, value) != 0;
    }

    private const int MaxSubTabs = 8;

    /// <summary>
    /// The sub-tabs the server plugin adds to one of the Mini Remote's tabs (<paramref name="parent"/>
    /// "pets": e.g. a Breeding sub-tab). Each id is a RenderSection id. Empty without one. A new list each
    /// call (the render thread draws them, a command looks one up). Any thread.
    /// </summary>
    public IReadOnlyList<ServerTabInfo> SubTabs(string parent)
    {
        var t = T;
        if (t == null || !RynthServerHubApiV1.Has(t, RynthServerHubApiV1.GetSubTabsOffset) || t->GetSubTabs == null)
            return Array.Empty<ServerTabInfo>();
        ServerHubTabV1* buf = stackalloc ServerHubTabV1[MaxSubTabs];
        int n;
        using (var p = new Utf8(parent)) n = t->GetSubTabs(p.P, buf, MaxSubTabs);
        n = Math.Min(n, MaxSubTabs);
        if (n <= 0) return Array.Empty<ServerTabInfo>();
        var list = new List<ServerTabInfo>(n);
        for (int i = 0; i < n; i++)
        {
            ServerHubTabV1* tab = &buf[i];
            tab->Id[23] = 0;      // never read past a buffer the other side filled to the brim
            tab->Label[31] = 0;
            string id = Marshal.PtrToStringUTF8((IntPtr)tab->Id) ?? string.Empty;
            string label = Marshal.PtrToStringUTF8((IntPtr)tab->Label) ?? string.Empty;
            if (id.Length == 0) continue;
            list.Add(new ServerTabInfo(id, label.Length > 0 ? label : id, tab->Width, tab->Height,
                (tab->Flags & RynthServerHub.TabShown) != 0, (tab->Flags & RynthServerHub.TabAvailable) != 0));
        }
        return list;
    }

    /// <summary>A NUL-terminated UTF-8 copy of a string for one call.</summary>
    private readonly struct Utf8 : IDisposable
    {
        public readonly byte* P;

        public Utf8(string? s)
        {
            s ??= string.Empty;
            int n = Encoding.UTF8.GetByteCount(s);
            P = (byte*)NativeMemory.Alloc((nuint)(n + 1));
            fixed (char* c = s) Encoding.UTF8.GetBytes(c, s.Length, P, n);
            P[n] = 0;
        }

        public void Dispose() => NativeMemory.Free(P);
    }
}
