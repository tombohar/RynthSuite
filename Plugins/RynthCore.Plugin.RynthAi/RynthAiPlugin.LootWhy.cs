// ============================================================================
//  RynthAi - RynthAiPlugin.LootWhy.cs
//  The "Why this loot rule?" window (Loot/LootWhyWindow.cs): /ra loot why's
//  answer on the screen. Asked for by the remote command "lootwhy" (the engine's
//  Loot Editor and Classic Loot tab buttons; RynthDefective's Selection menu,
//  through SendPluginCommand) and by /ra loot why window. The tick builds the
//  report (BuildLootWhyReport, RynthAiCommands.cs); the render thread draws it.
// ============================================================================
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The remote command (action) that opens the window on the selected item's answer.</summary>
    internal const string LootWhyAction = "lootwhy";

    private LootWhyWindow? _lootWhyWindow;                 // render thread
    private volatile LootWhy.Report? _lootWhyReport;       // the tick writes, the render thread reads
    private volatile bool _lootWhyShown;

    /// <summary>Pump thread: the window shows <paramref name="report"/> (opening it if it was closed).</summary>
    private void ShowLootWhyWindow(LootWhy.Report report)
    {
        _lootWhyReport = report;
        _lootWhyShown = true;
    }

    /// <summary>Render thread, inside OnRender / OnRenderOverlay (the context is set).</summary>
    private void RenderLootWhyWindow()
    {
        if (!_lootWhyShown || _lootWhyReport is not { } report) return;
        _lootWhyWindow ??= new LootWhyWindow(() => EnqueueRemoteCommand(LootWhyAction, "show"));
        bool open = true;
        _lootWhyWindow.Render(report, ref open);
        if (!open) _lootWhyShown = false;
    }
}
