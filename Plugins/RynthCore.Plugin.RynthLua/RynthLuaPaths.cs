using System.IO;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// Where RynthLua keeps its files. The plugin always uses the defaults; the offline test
/// harness (Tools\RynthLua.CompatTests) points <see cref="Root"/> and
/// <see cref="LegacyScripts"/> at a temporary folder before it loads the plugin, so a test
/// run never touches the real game folders.
/// </summary>
internal static class RynthLuaPaths
{
    /// <summary>RynthLua's folder: Scripts\, Data\, settings.json, editor.lua (&lt;SuiteDir&gt;\RynthLua, C:\Games\RynthSuite\RynthLua by default).</summary>
    public static string Root { get; set; } = Path.Combine(RynthCore.Install.RynthInstallPaths.SuiteDir, "RynthLua");

    /// <summary>Where scripts lived while Lua was part of RynthAi (copied over once, then left alone).</summary>
    public static string LegacyScripts { get; set; } = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "LuaScripts");

    public static string Scripts => Path.Combine(Root, "Scripts");
    public static string Data => Path.Combine(Root, "Data");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Editor => Path.Combine(Root, "editor.lua");
}
