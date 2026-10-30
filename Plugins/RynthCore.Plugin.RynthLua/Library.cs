using System;
using System.Runtime.InteropServices;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// Library: the built-in <c>require</c> modules and the enum globals (plus Vector2, Vector4
/// and the ImGui enums from LibraryImGuiValues).
///
///   "json"        LibraryJson        encode / decode
///   "storage"     LibraryStorage     saved values per script and per character
///   "enums"       LibraryEnums       ObjectClass, SkillId, ... (also globals)
///   "rynth.ai"    LibraryRynthAi.cs  RynthAi through RynthAiBridge
///   "filesystem"  LibraryFileSystem  UB-style file access in the script's data folder
///   "rynthlua"    this file          the plugin's own info
///   "views"       LibraryViews       script windows (huds)
///   "imgui"       LibraryImGui       the ImGui calls a hud's OnRender records
///
/// Any other name leaves <c>module</c> null, so require looks for a file in the script's folder.
/// A module is made once per script state (LuaScriptHost caches it).
/// </summary>
public sealed partial class RynthLuaPlugin
{
    /// <summary>Per-script data (storage, filesystem.GetData()): Data\&lt;Script&gt;\.</summary>
    internal static string LuaDataRoot => RynthLuaPaths.Data;

    /// <summary>Script.Registry key of this script's enums module (the globals can be overwritten by the script, the module can't).</summary>
    private const string EnumsRegistryKey = "rynth.enums";

    partial void RegisterLibraryGlobals(Script s, ScriptContext ctx)
    {
        DynValue enums = LibraryEnums.CreateModule(s);
        s.Registry.Set(EnumsRegistryKey, enums);
        foreach (TablePair p in enums.Table.Pairs)
            if (p.Key.Type == DataType.String && p.Value.Type == DataType.Table)
                s.Globals.Set(p.Key.String, p.Value);

        // MoonSharp's sandbox has its own global json (parse/serialize); replace it with the
        // same module require("json") returns, so json.encode works without the require.
        s.Globals["json"] = LibraryJson.CreateModule(s);

        // Script windows: Vector2, Vector4 and the ImGui enums (ImGuiWindowFlags, ImGuiCol, ...).
        LibraryImGuiValues.Register(s);
    }

    partial void ResolveLibraryModule(Script s, ScriptContext ctx, string name, ref DynValue? module)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "json":
                module = LibraryJson.CreateModule(s);
                break;
            case "storage":
                module = LibraryStorage.CreateModule(s, ctx, LuaDataRoot, CharacterKey);
                break;
            case "enums":
            {
                DynValue cached = s.Registry.Get(EnumsRegistryKey);
                module = cached.Type == DataType.Table ? cached : LibraryEnums.CreateModule(s);
                break;
            }
            case "rynth.ai":
                module = CreateRynthAiModule(s, ctx);
                break;
            case "filesystem":
                module = LibraryFileSystem.CreateModule(s, ctx, LuaDataRoot);
                break;
            case "rynthlua":
                module = CreateInfoModule(s, ctx);
                break;
            case "views":
                module = CreateViewsModule(s, ctx);
                break;
            case "imgui":
                module = CreateImGuiModule(s);
                break;
        }
    }

    /// <summary><c>require("rynthlua")</c>: { Name, Version, ScriptName, ScriptFolder, DataFolder }.</summary>
    private static DynValue CreateInfoModule(Script s, ScriptContext ctx)
    {
        var t = new Table(s);
        t.Set("Name", DynValue.NewString("RynthLua"));
        t.Set("Version", DynValue.NewString(Marshal.PtrToStringAnsi(VersionPointer) ?? string.Empty));
        t.Set("ScriptName", DynValue.NewString(ctx.Name));
        t.Set("ScriptFolder", ctx.Folder == null ? DynValue.Nil : DynValue.NewString(ctx.Folder));
        t.Set("DataFolder", DynValue.NewString(LibraryPaths.ScriptDataFolder(LuaDataRoot, ctx.Name)));
        return DynValue.NewTable(t);
    }
}
