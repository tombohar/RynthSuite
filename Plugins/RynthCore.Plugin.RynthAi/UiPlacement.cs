// UiPlacement.cs - Shared first-use placement and off-screen rescue for RynthAi's floating
// ImGui windows (render thread only).
//
// Windows with no imgui.ini entry open near the middle of the game view instead of ImGui's
// default (60,60), where a popped-out panel above the game frame would hide them. A window
// whose saved position leaves it (almost) off the game view is moved back to the middle.
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi;

internal static class UiPlacement
{
    /// <summary>Pixels of a window that must stay inside the game view to count as on-screen.</summary>
    private const float MinVisible = 40f;

    private static readonly Vector2 CenterPivot = new(0.5f, 0.5f);

    /// <summary>
    /// Before Begin: if the window has no saved position, centre it in the game view, shifted by
    /// <paramref name="offset"/> so windows opened together don't stack exactly.
    /// </summary>
    public static void CenterFirstUse(Vector2 offset = default)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= 1 || display.Y <= 1) return; // no display size yet: ImGui's default spot
        ImGui.SetNextWindowPos(display * 0.5f + offset, ImGuiCond.FirstUseEver, CenterPivot);
    }

    /// <summary>
    /// Between Begin and End, once per show (<paramref name="checkedThisShow"/> is cleared by the
    /// caller while the window is hidden): moves the window to the middle of the game view if
    /// less than 40 px of it is on screen. Returns true when it moved the window.
    /// </summary>
    public static bool RescueOncePerShow(ref bool checkedThisShow)
    {
        if (checkedThisShow) return false;
        checkedThisShow = true;

        Vector2 pos = ImGui.GetWindowPos();
        Vector2 size = ImGui.GetWindowSize();
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= MinVisible * 2 || display.Y <= MinVisible * 2) return false;
        if (IsOnScreen(pos, size, display)) return false;

        ImGui.SetWindowPos(Vector2.Max(Vector2.Zero, (display - size) * 0.5f));
        return true;
    }

    /// <summary>True when at least 40 px of the window overlaps the game view on each axis.</summary>
    public static bool IsOnScreen(Vector2 pos, Vector2 size, Vector2 display) =>
        pos.X + size.X >= MinVisible && pos.Y + size.Y >= MinVisible
        && pos.X <= display.X - MinVisible && pos.Y <= display.Y - MinVisible;
}
