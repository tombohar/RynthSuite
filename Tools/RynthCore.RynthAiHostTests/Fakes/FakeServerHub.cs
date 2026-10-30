using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.Shared;

namespace RynthCore.RynthAiHostTests.Fakes;

/// <summary>
/// A stand-in for a server plugin's "RynthServer.Hub" table (Plugins/Shared/RynthServerHubApi.cs), so the
/// tests can check what RynthAi does WITH a server plugin (RynthILT) as well as without one. Its
/// functions answer from the static state below; RynthAi's ServerHubClient takes the table through
/// UseTableForTests. The render functions draw nothing (no ImGui frame in the tests): they only
/// record which section was asked for. Reset() before each test that uses it.
/// </summary>
internal static unsafe class FakeServerHub
{
    /// <summary>IsIltWorld's answer.</summary>
    public static bool Active;
    /// <summary>GetFeature: key -> -1 / 0 / 1 (missing = -1).</summary>
    public static readonly Dictionary<string, int> Features = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>AllowSummonOnEmpty's answer.</summary>
    public static bool AllowEmpty;
    /// <summary>GetSubTabs(parent): parent -> (id, label) list.</summary>
    public static readonly Dictionary<string, List<(string Id, string Label)>> SubTabs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Command lines the plugin took, and which it takes (null = none).</summary>
    public static readonly List<string> Commands = new();
    public static Func<string, bool>? TakesCommand;
    /// <summary>RenderSection ids asked for, and the ones that "draw" (return 1).</summary>
    public static readonly List<string> SectionsAsked = new();
    public static readonly HashSet<string> SectionsDrawn = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>GetSetting / SetSetting: key -> value (missing = not offered, -1; SetSetting only takes known keys).</summary>
    public static readonly Dictionary<string, int> Settings = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>GetText: id -> text (missing = "").</summary>
    public static readonly Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase);

    private static RynthServerHubApiV1* _table;

    public static void Reset()
    {
        Active = false;
        Features.Clear();
        AllowEmpty = false;
        SubTabs.Clear();
        Commands.Clear();
        TakesCommand = null;
        SectionsAsked.Clear();
        SectionsDrawn.Clear();
        Settings.Clear();
        Texts.Clear();
    }

    /// <summary>The table (allocated once, never freed: like the real one).</summary>
    public static IntPtr Table
    {
        get
        {
            if (_table == null)
            {
                var t = (RynthServerHubApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthServerHubApiV1));
                t->StructSize = (uint)sizeof(RynthServerHubApiV1);
                t->IsServerWorld = &IsIltWorld;
                t->GetFeature = &GetFeature;
                t->GetTabs = &GetTabs;
                t->RenderTab = &RenderTab;
                t->RenderSection = &RenderSection;
                t->Command = &Command;
                t->AllowSummonOnEmpty = &AllowSummonOnEmpty;
                t->GetSubTabs = &GetSubTabs;
                t->GetSetting = &GetSetting;
                t->SetSetting = &SetSetting;
                t->GetText = &GetText;
                t->Version = RynthServerHubApiV1.InterfaceVersion;
                _table = t;
            }
            return (IntPtr)_table;
        }
    }

    private static string? Utf8(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    private static void Write(string s, byte* dest, int capacity)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(b.Length, capacity - 1);
        for (int i = 0; i < n; i++) dest[i] = b[i];
        dest[n] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsIltWorld() => Active ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetFeature(byte* key)
    {
        string? k = Utf8(key);
        return k != null && Features.TryGetValue(k, out int v) ? v : -1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTabs(ServerHubTabV1* tabs, int capacity) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RenderTab(byte* id) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int RenderSection(byte* id)
    {
        string s = Utf8(id) ?? string.Empty;
        SectionsAsked.Add(s);
        return SectionsDrawn.Contains(s) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Command(byte* text)
    {
        string line = Utf8(text) ?? string.Empty;
        if (TakesCommand?.Invoke(line) != true) return 0;
        Commands.Add(line);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSetting(byte* key)
    {
        string? k = Utf8(key);
        return k != null && Settings.TryGetValue(k, out int v) ? v : -1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetSetting(byte* key, int value)
    {
        string? k = Utf8(key);
        if (k == null || !Settings.ContainsKey(k)) return 0;
        Settings[k] = value;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetText(byte* id, byte* buffer, int capacity)
    {
        string? k = Utf8(id);
        if (k == null || !Texts.TryGetValue(k, out string? text) || text.Length == 0) return 0;
        Write(text, buffer, capacity);
        return Encoding.UTF8.GetByteCount(text);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int AllowSummonOnEmpty() => AllowEmpty ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSubTabs(byte* parent, ServerHubTabV1* tabs, int capacity)
    {
        string p = Utf8(parent) ?? string.Empty;
        if (!SubTabs.TryGetValue(p, out var list)) return 0;
        if (tabs != null)
            for (int i = 0; i < list.Count && i < capacity; i++)
            {
                Write(list[i].Id, tabs[i].Id, 24);
                Write(list[i].Label, tabs[i].Label, 32);
                tabs[i].Width = 0;
                tabs[i].Height = 0;
                tabs[i].Flags = RynthServerHub.TabShown | RynthServerHub.TabAvailable;
            }
        return list.Count;
    }
}
