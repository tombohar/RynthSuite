// ============================================================================
//  RynthNet - Characters/CharactersManifest.cs
//  RynthNet's sections manifest (RynthPluginGetSectionsJson): the Characters
//  window, which RynthCore hosts (plugin API v89: header, pop-out, bar button,
//  remembered place) and RynthNet draws (RynthPluginRenderEmbed with Surface).
//  Its title is the panel key players' saved places, /rc characters and hotkeys
//  already use. An engine before v89 ignores "windows". Pure.
// ============================================================================

using System.Globalization;
using RynthCore.Plugin.Shared.RynthUi;

namespace RynthCore.Plugin.RynthNet.Characters;

internal static class CharactersManifest
{
    public const string Title = "Characters";
    public const string Surface = "window:characters";

    /// <summary>The manifest JSON (built once).</summary>
    public static readonly string Json =
        "{\"version\":1,\"windows\":[{\"id\":\"characters\",\"title\":\"" + Title + "\",\"surface\":\"" + Surface +
        "\",\"icon\":\"\\u" + ((int)RynthIcons.UsersThree[0]).ToString("X4", CultureInfo.InvariantCulture) +
        "\",\"width\":420,\"height\":260,\"minWidth\":280,\"minHeight\":140,\"bar\":true,\"edgeToEdge\":true}]}";
}
