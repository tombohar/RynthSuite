// ============================================================================
//  RynthSuite - RynthAiPlugin.LootEditorFailure.cs
//  The Loot Editor's calls into RynthAi say why they fail (2026-11-24).
//
//  R-20261124-085015-5215 (RynthAi 2026.11.23.2 on a 2026.11.12.1 core): "loot editor not loading".
//  The editor's exports (PluginExports: RynthPluginGetLootEditJson and the rest) caught every
//  failure and said nothing, so the window opened empty and closed with no line anywhere. The cause
//  was the one TickGuard now names for the looting tick: this RynthAi was built against a newer
//  RynthCore.LootSdk than the core's Runtime\PluginShared copy (the edit session's PerDay members).
//  Now such a failure says so once every 5 minutes in chat ("install the core update in the
//  launcher"), and any other failure is logged, at most every 10 s.
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private DateTime _lootEditorFailSaidAt = DateTime.MinValue;
    private DateTime _lootEditorFailLoggedAt = DateTime.MinValue;

    /// <summary>The chat line for a Loot Editor failure caused by an older RynthCore core, else null.</summary>
    internal static string? LootEditorNeedsCoreLine(Exception ex) =>
        TickGuard.NeedsNewerCore(ex)
            ? $"[RynthAi] The Loot Editor can't load: this RynthAi needs a newer RynthCore core: install the core update in the launcher ({ex.GetType().Name}: {ex.Message})."
            : null;

    /// <summary>A call from the engine's Loot Editor threw (PluginExports): say why, rate-limited.</summary>
    internal void NoteLootEditorFailure(Exception ex)
    {
        DateTime now = RynthClock.Now;
        if (LootEditorNeedsCoreLine(ex) is { } line)
        {
            if ((now - _lootEditorFailSaidAt).TotalMinutes < 5) return;
            _lootEditorFailSaidAt = now;
            Host.Log(line);
            try { ChatLine(line); } catch { }
            return;
        }
        if ((now - _lootEditorFailLoggedAt).TotalSeconds < 10) return;
        _lootEditorFailLoggedAt = now;
        Host.Log($"[RynthAi] A Loot Editor call failed: {ex.GetType().Name}: {ex.Message}");
    }
}
