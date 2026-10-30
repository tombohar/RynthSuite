using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// How a rule edited in the Meta panel replaces the running one (update_rule / duplicate_rule).
///
/// The engine's Meta editor saves every change on its own since 2026-10-05 (debounced), so an
/// update must not disturb a meta that is running:
///  - the rule keeps its runtime latch (HasFired) and LastFiredAt. A fresh MetaRule had
///    HasFired = false, so every save of a rule in the current state fired it again
///    (an IF Always DO EmbedNav rule restarted its route from point 0 on each save);
///  - the panel's DTO has no CallReturnState (CallState's return state) or ChatColors
///    (ChatCapture's chat types), so an update used to drop both from VTank-imported rules.
///    They are kept while the rule still has that action / condition.
/// Nothing here touches the current state, the state clocks, the watchdog or the call stack.
/// </summary>
internal static class MetaRuleEdits
{
    /// <summary>Puts <paramref name="edited"/> at <paramref name="index"/>, keeping the old rule's
    /// runtime latch and the fields the panel can't send. <paramref name="keepName"/>: the edit came
    /// from an engine without rule names, so the rule keeps its name. False when the index is out of range.</summary>
    public static bool Replace(List<MetaRule> rules, int index, MetaRule edited, bool keepName = false)
    {
        if (index < 0 || index >= rules.Count) return false;
        MetaRule old = rules[index];
        KeepHidden(old, edited);
        if (keepName) edited.Name = old.Name;
        edited.HasFired = old.HasFired;
        edited.LastFiredAt = old.LastFiredAt;
        rules[index] = edited;
        return true;
    }

    /// <summary>Copies CallReturnState / ChatColors from <paramref name="from"/> into an edited copy
    /// that still has the same action / condition and carries none of its own; children by position.</summary>
    public static void KeepHidden(MetaRule from, MetaRule to)
    {
        if (to.Action == MetaActionType.CallMetaState && from.Action == MetaActionType.CallMetaState
            && string.IsNullOrEmpty(to.CallReturnState))
            to.CallReturnState = from.CallReturnState;
        if (to.Condition == MetaConditionType.ChatMessageCapture && from.Condition == MetaConditionType.ChatMessageCapture
            && string.IsNullOrEmpty(to.ChatColors))
            to.ChatColors = from.ChatColors;

        if (from.Children != null && to.Children != null)
            for (int i = 0; i < from.Children.Count && i < to.Children.Count; i++)
                KeepHidden(from.Children[i], to.Children[i]);
        if (from.ActionChildren != null && to.ActionChildren != null)
            for (int i = 0; i < from.ActionChildren.Count && i < to.ActionChildren.Count; i++)
                KeepHidden(from.ActionChildren[i], to.ActionChildren[i]);
    }
}
