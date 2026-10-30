// ============================================================================
//  RynthVision - Ui/VisionManifest.cs
//  RynthVision's sections manifest (RynthPluginGetSectionsJson), 2026-10-27:
//  the "Vision" window, which takes the engine's Vision panel's place
//  ("takeOver": its saved spot and size, open state, bar button, hotkeys and
//  /rc commands carry over) and RynthVision draws whole (Ui/VisionWindow.cs:
//  Terrain, Nameplates, Combat text and the Dungeon light). Owner's layer rule
//  (CLAUDE.md in both repos): the plugin owns its screens; the engine hosts the
//  window and stops drawing its own VisionFace while this one is declared, and
//  brings it back when RynthVision unloads.
//
//  Declared only on an engine with plugin API v91 (HostsWindow): that release
//  has the light hook and draws the Nameplates and Combat text tabs into this
//  window (RenderSettingsDrawer "nameplates" / "combattext"; those settings are
//  still RynthCore's). An older engine gets no window and keeps its own Vision
//  panel, all of it; /rv light still answers there. No bar icon of its own: ""
//  takes the engine bar's icon for "Vision" (the eye). Replaces the separate
//  "Dungeon Light" window of c769ab1 (its saved place is simply unused). Pure.
// ============================================================================

namespace RynthCore.Plugin.RynthVision.Ui;

internal static class VisionManifest
{
    /// <summary>The window's title: the engine's Vision panel's key (saved place, hotkeys, SetPanelOpen).</summary>
    public const string Title = "Vision";
    public const string Surface = "window:vision";

    /// <summary>The engine's VisionFace sizes (PanelSpec 320 x 560, at least 280 x 240).</summary>
    public const int Width = 320, Height = 560, MinWidth = 280, MinHeight = 240;

    /// <summary>The manifest with the Vision window (an engine that hosts it).</summary>
    public const string WithWindow =
        "{\"version\":1,\"windows\":[{\"id\":\"vision\",\"title\":\"Vision\",\"surface\":\"window:vision\",\"icon\":\"\"," +
        "\"width\":320,\"height\":560,\"minWidth\":280,\"minHeight\":240,\"bar\":true,\"edgeToEdge\":true,\"takeOver\":true}]}";

    /// <summary>No windows: an older engine keeps its own Vision panel.</summary>
    public const string NoWindow = "{\"version\":1}";

    /// <summary>
    /// True when the engine can host the window: plugin API v91 (the light hook, and the Nameplates and
    /// Combat text tab bodies), plugin windows with take-over (v89/v90) and the engine's drawers (v90).
    /// </summary>
    public static bool HostsWindow(bool hasAmbientLight, bool hasSetPanelOpen, bool hasSettingsDrawers, bool hasUiTheme) =>
        hasAmbientLight && hasSetPanelOpen && hasSettingsDrawers && hasUiTheme;

    public static string Json(bool hostsWindow) => hostsWindow ? WithWindow : NoWindow;
}
