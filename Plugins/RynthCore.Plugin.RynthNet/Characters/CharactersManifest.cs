// ============================================================================
//  RynthNet - Characters/CharactersManifest.cs
//  RynthNet's sections manifest (RynthPluginGetSectionsJson): the Characters
//  window, which RynthCore hosts (plugin API v89: header, pop-out, bar button,
//  remembered place) and RynthNet draws (RynthPluginRenderEmbed with Surface).
//  Its title is the panel key players' saved places, /rc characters and hotkeys
//  already use. An engine before v89 ignores "windows". Pure.
//
//  On an engine with frameless windows (v90, HasFramelessWindows) the window
//  is declared "chrome": "none" with "fitHeight" (2026-10-31): RynthNet draws
//  both of its looks itself (CharactersLook.cs), the minimal one without any
//  frame or background, the full one with its own frame and title row (drag,
//  pop-out, close), and the window is as tall as what it shows. On an older
//  engine it keeps the engine's frame (Json, as before) and the minimal look
//  draws inside it.
// ============================================================================

using System.Globalization;
using RynthCore.Plugin.Shared.RynthUi;

namespace RynthCore.Plugin.RynthNet.Characters;

internal static class CharactersManifest
{
    public const string Title = "Characters";
    public const string Surface = "window:characters";

    private static readonly string Head =
        "{\"version\":1,\"windows\":[{\"id\":\"characters\",\"title\":\"" + Title + "\",\"surface\":\"" + Surface +
        "\",\"icon\":\"\\u" + ((int)RynthIcons.UsersThree[0]).ToString("X4", CultureInfo.InvariantCulture) + "\",";

    /// <summary>The manifest JSON for an engine without frameless windows: the engine's frame (built once).</summary>
    public static readonly string Json =
        Head + "\"width\":420,\"height\":260,\"minWidth\":280,\"minHeight\":140,\"bar\":true,\"edgeToEdge\":true}]}";

    /// <summary>The manifest JSON for an engine with frameless windows (v90): RynthNet draws every frame (built once).</summary>
    public static readonly string FramelessJson =
        Head + "\"width\":420,\"height\":120,\"minWidth\":160,\"minHeight\":20,\"bar\":true,\"edgeToEdge\":true," +
        "\"chrome\":\"none\",\"fitHeight\":true}]}";

    /// <summary>The manifest for this engine: frameless when it has frameless windows.</summary>
    public static string For(bool framelessWindows) => framelessWindows ? FramelessJson : Json;
}
