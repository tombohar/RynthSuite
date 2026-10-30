// MiniRemoteHeader.cs - the Mini Remote's [V/H] [Options] [gear] header row: what a click does, and the
// button widths. Pure, so the host tests check both offline.
//
// Options opens RynthAi Settings (Advanced Settings) through RynthCore's "/rc ui open Settings" when the
// host has a chat parser. The Mini Remote's own popup (sections, layout, slot size) opens from the small
// gear button next to it, from a right-click on Options, and from a left-click on Options when there is
// no chat parser to run the command.
namespace RynthCore.Plugin.RynthAi.Huds;

internal static class MiniRemoteHeader
{
    /// <summary>[V/H]: one letter, so it doesn't need the width of the word buttons.</summary>
    public const float LayoutButtonWidth = 56f;
    public const float OptionsButtonWidth = 80f;
    /// <summary>The gear button right of Options: square-ish and small.</summary>
    public const float GearButtonWidth = 28f;

    /// <summary>
    /// The engine command a left-click on Options runs, or null when the host can't run chat commands
    /// (then the click opens the Mini Remote popup instead).
    /// </summary>
    public static string? OptionsLeftClickCommand(bool hasChatParser) => hasChatParser ? MovedTabs.Settings : null;

    /// <summary>Total width of the header row for the given spacing between its three buttons.</summary>
    public static float RowWidth(float itemSpacing) =>
        LayoutButtonWidth + OptionsButtonWidth + GearButtonWidth + 2f * itemSpacing;
}
