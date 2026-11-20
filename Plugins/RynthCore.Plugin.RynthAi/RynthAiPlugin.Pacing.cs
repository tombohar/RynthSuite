using System;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

// ============================================================================
//  RynthAi's side of the action pacing (Plugins/Shared/ActionPacer.cs, 2026-10-06).
//  Every use / use-on RynthAi sends is recorded as the server action it waits on;
//  casts and mode changes record themselves (CastSpellFor / ChangeCombatModeFor).
//  The managers that only see a busy count (inventory, salvage, mana stones, pets)
//  get PacedBusy: the count, plus one while a server action is unanswered.
// ============================================================================
public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// UseAudit.WorldUseHold: an automatic door or corpse use waits while the salvage panel is
    /// between the Ust use and the salvage send. A world use in that window is swallowed by the
    /// client, and the corpse loop then sat waiting for an open that never came.
    /// </summary>
    private string? SalvagePanelHold() =>
        _salvageManager?.PanelPending == true ? "the salvage panel is opening; the use waits for the salvage to go out" : null;

    /// <summary>
    /// AutoCram's or AutoStack's last move has no answer from the server yet (2026-11-18,
    /// R-20261117-230613-88c5). The client holds that move in its one pending item request, and a
    /// corpse, chest or door use sent now is refused with "You can only move or use one item at a
    /// time" and never sent, so the looter waited 5 s for an answer that could not come. Null when
    /// nothing is in flight.
    /// </summary>
    private string? ItemMoveHold() =>
        _inventoryManager?.MoveUnanswered(out string what) == true ? what : null;

    /// <summary>UseAudit.WorldUseHold: what an automatic door or corpse use waits for, or null.</summary>
    private string? WorldUseHoldReason() => SalvagePanelHold() ?? ItemMoveHold();

    /// <summary>UseAudit.AfterSend: a use or use-on went out - record it as the action the server is answering.</summary>
    private static void RecordUseForPacing(RynthCoreHost host, UseAudit.SentUse use)
    {
        try
        {
            // The salvage panel (a UST use) opens on the client; the server answered only 7 of 18
            // of them with a UseDone in the 10-05/10-06 logs, so it is not waited on.
            if (use.Subsystem == "Salvage" || use.What.Contains("'Ust'", StringComparison.OrdinalIgnoreCase))
                return;
            PacedKind kind;
            uint player = host.HasGetPlayerId ? host.GetPlayerId() : 0;
            if (use.TargetId != 0)
            {
                // Something on a target: on yourself (kit, stone, food) it is an item use; on a
                // door/corpse or anything else in the world the server walks there first.
                kind = use.TargetId == player
                    ? PacedKind.ItemUse
                    : ActionPacer.ClassifyUse(host, use.TargetId, use.DoorOrCorpse);
            }
            else
            {
                kind = ActionPacer.ClassifyUse(host, use.ObjectId, use.DoorOrCorpse);
            }
            string who = "RynthAi/" + use.Subsystem + (use.Kind == UseKind.Asked ? " (asked)" : "");
            bool onSelf = use.TargetId != 0 && use.TargetId == player;   // a kit on yourself (the pacer's heal grace)
            ActionPacer.Begin(host, kind, use.ObjectId, use.What, who, use.UseDoneSeqBefore, onSelf: onSelf);
        }
        catch { }
    }

    /// <summary>
    /// The busy count the item managers gate on (inventory, salvage, mana stones, pets): our mirror of
    /// AC's count, plus one while a server action is unanswered so they don't start one on top of it.
    /// </summary>
    private int PacedBusy
    {
        get
        {
            int busy = _busyCount;
            if (ActionPacer.Enabled
                && (ActionPacer.Current(Host) != null || ActionPacer.GestureHolds(Host, "Items", HealNeed.None, out _)))
                busy++;   // an unanswered server action, or the character still animating (a pack move of a wielded item, a stone, a summon would start on top)
            // AutoCram's or AutoStack's move has no answer yet: the client refuses any other item
            // action until it has one (2026-11-18, R-20261117-230613-88c5; ItemMoveHold).
            if (_inventoryManager?.MoveUnanswered(out _) == true)
                busy++;
            return busy;
        }
    }

    /// <summary>/ra pace: what the pacing is waiting on, and its counters.</summary>
    private void HandlePaceCommand()
    {
        foreach (string line in ActionPacer.DescribeLines(Host))
            ChatLine("[RynthAi] " + line);
        ChatLine($"[RynthAi]   Busy mirror {_busyCount} (shadow resets only; the client's count is never force-cleared by RynthAi now)");
    }
}
