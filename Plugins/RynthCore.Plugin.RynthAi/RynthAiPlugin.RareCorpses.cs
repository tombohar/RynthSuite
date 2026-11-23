// ============================================================================
//  RynthSuite - RynthAiPlugin.RareCorpses.cs
//  Loot Only Rare Corpses (2026-11-22: "we should know whether it's a rare corpse... vtank does
//  somehow"). Asked for on Discord for rare farming on Conquest: Rares loot mode opened every corpse,
//  and kills per hour went down. The setting was kept only so profiles round-trip, read by nothing.
//
//  How a corpse says it holds a rare: ACE (Corpse.TryGenerateRare, the same in DreamWeave's fork)
//  appends " This corpse generated a rare item!" to the corpse's long description, the text whose
//  "Killed by <name>." the ownership rules already read once the corpse is identified (the looter
//  asks for that ID for every corpse in range). It also broadcasts "<killer> has discovered the
//  <rare>!", but the description is on the corpse, so it holds for a corpse seen after a relog too.
//
//  With the setting on, a corpse is opened only when its description has that sentence. One whose
//  description hasn't arrived yet waits for it, as Mine Only does. Your own death corpse
//  (Recover My Corpse) is decided before this, so it is always recovered.
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The sentence ACE adds to a corpse that generated a rare (Corpse.TryGenerateRare).</summary>
    internal const string RareCorpseText = "This corpse generated a rare item";

    /// <summary>The corpse's long description says it generated a rare.</summary>
    internal static bool IsRareCorpse(string? longDesc)
        => !string.IsNullOrEmpty(longDesc) && longDesc.IndexOf(RareCorpseText, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Why Loot Only Rare Corpses leaves a corpse closed, or null when it is a rare corpse.</summary>
    internal static string? RareCorpseRefusal(string? longDesc)
    {
        if (string.IsNullOrWhiteSpace(longDesc))
            return "Loot Only Rare Corpses: waiting for its description (the ID) to see whether it generated a rare.";
        return IsRareCorpse(longDesc) ? null : "Loot Only Rare Corpses: it didn't generate a rare.";
    }
}
