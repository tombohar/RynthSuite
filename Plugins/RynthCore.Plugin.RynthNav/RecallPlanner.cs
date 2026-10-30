using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>One recall the route planner may use: a spell to cast, or a command to type.</summary>
internal sealed class RecallOption
{
    public string Name = "";
    /// <summary>The spell to cast (0 = a command).</summary>
    public int SpellId;
    /// <summary>The command to send (@lifestone, @house recall) when SpellId is 0.</summary>
    public string Command = "";
    public double Ns, Ew;
    public double CastSeconds;
    /// <summary>Learned in game (RecallMemory), not fixed by the world data.</summary>
    public bool Learned;

    public RecallLink ToLink() => new(Ns, Ew, Name, PortalRoute.RecallCostUnits(CastSeconds));
}

/// <summary>
/// Which recalls this character can use, and where they land: the fixed recall spells
/// from the Atlas (only the ones in the character's spellbook), plus the personal ones
/// RynthNav has learned (RecallMemory). A recall whose landing isn't known, or is off the
/// map (a dungeon), is left out with a note saying why. No host calls: the offline tests
/// compile this file.
/// </summary>
internal static class RecallPlanner
{
    public const int PrimaryPortalTieRecall = 48;
    public const int PrimaryPortalTie = 47;
    public const int LifestoneRecall = 1635;
    public const int LifestoneTie = 2644;
    public const int PortalRecall = 2645;
    public const int SecondaryPortalTie = 2646;
    public const int SecondaryPortalTieRecall = 2647;

    /// <summary>A recall spell: the magic stance (and a wand) plus the cast. Estimated.</summary>
    public const double SpellCastSeconds = 5.0;
    /// <summary>@lifestone, @house recall and the like: a long recall animation. Estimated.</summary>
    public const double CommandRecallSeconds = 14.0;

    /// <param name="known">The character's spellbook; null while it hasn't been read.</param>
    public static List<RecallOption> Build(Atlas atlas, ISet<int>? known, RecallMemory memory, string charKey, List<string>? notes)
    {
        var list = new List<RecallOption>();

        // Fixed-destination recall spells (Aerlinthe Recall, Lyceum Recall, ...).
        if (known == null)
        {
            notes?.Add("Spellbook not read yet: recall spells left out.");
        }
        else
        {
            var seen = new HashSet<int>();
            foreach (AtlasRecall r in atlas.Recalls)
            {
                if (!known.Contains(r.SpellId) || !seen.Add(r.SpellId)) continue;
                if (!r.OnMap)
                {
                    notes?.Add(r.Name + " lands " + (r.In.Length > 0 ? "in " + r.In : "in a dungeon") + ": not used for routes.");
                    continue;
                }
                list.Add(new RecallOption { Name = r.Name, SpellId = r.SpellId, Ns = r.Ns, Ew = r.Ew, CastSeconds = SpellCastSeconds });
            }
        }

        // Personal recall spells: the landing is the character's own.
        if (known != null)
        {
            AddLearnedSpell(list, notes, known, memory, charKey, LifestoneRecall, "Lifestone Recall", RecallSlot.Lifestone,
                "cast Lifestone Tie on a lifestone, cast Lifestone Recall once, or /rnav tie lifestone at the tied lifestone");
            AddLearnedSpell(list, notes, known, memory, charKey, PortalRecall, "Portal Recall", RecallSlot.LastPortal,
                "walk through a portal");
            AddLearnedSpell(list, notes, known, memory, charKey, PrimaryPortalTieRecall, "Primary Portal Tie Recall", RecallSlot.Tie1,
                "cast Primary Portal Tie on a portal, cast Primary Portal Tie Recall once, or /rnav tie 1 at the tied portal");
            AddLearnedSpell(list, notes, known, memory, charKey, SecondaryPortalTieRecall, "Secondary Portal Tie Recall", RecallSlot.Tie2,
                "cast Secondary Portal Tie on a portal, cast Secondary Portal Tie Recall once, or /rnav tie 2 at the tied portal");
        }

        // Command recalls (no spell): @lifestone always; house, mansion and hometown
        // once the character has used them (only then is it known that they have one).
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.Sanctuary, "Lifestone (@lifestone)", alwaysAvailable: true,
            "use a lifestone");
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.House, "House recall", alwaysAvailable: false, "");
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.Mansion, "Allegiance mansion recall", alwaysAvailable: false, "");
        AddLearnedCommand(list, notes, memory, charKey, RecallSlot.Hometown, "Allegiance hometown recall", alwaysAvailable: false, "");
        return list;
    }

    private static void AddLearnedSpell(List<RecallOption> list, List<string>? notes, ISet<int> known, RecallMemory memory,
        string charKey, int spellId, string name, RecallSlot slot, string howToLearn)
    {
        if (!known.Contains(spellId)) return;
        RecallSpot? spot = memory.Get(charKey, slot);
        if (spot == null) { notes?.Add(name + ": where it lands isn't known yet (" + howToLearn + ")."); return; }
        if (!spot.OnMap) { notes?.Add(name + " lands " + Where(spot) + ": not used for routes."); return; }
        list.Add(new RecallOption
        {
            Name = name, SpellId = spellId, Ns = spot.Ns, Ew = spot.Ew, CastSeconds = SpellCastSeconds, Learned = true,
        });
    }

    private static void AddLearnedCommand(List<RecallOption> list, List<string>? notes, RecallMemory memory, string charKey,
        RecallSlot slot, string name, bool alwaysAvailable, string howToLearn)
    {
        RecallSpot? spot = memory.Get(charKey, slot);
        if (spot == null)
        {
            if (alwaysAvailable) notes?.Add(name + ": where it lands isn't known yet (" + howToLearn + ").");
            return;
        }
        if (!spot.OnMap) { notes?.Add(name + " lands " + Where(spot) + ": not used for routes."); return; }
        list.Add(new RecallOption
        {
            Name = name,
            Command = spot.Command.Length > 0 ? spot.Command : RecallMemory.DefaultCommand(slot),
            Ns = spot.Ns, Ew = spot.Ew, CastSeconds = CommandRecallSeconds, Learned = true,
        });
    }

    private static string Where(RecallSpot s) => s.Label.Length > 0 ? "in " + s.Label : "in a dungeon";

    // ── Learning from a recall cast by hand ──────────────────────────────────

    /// <summary>Recall the Sanctuary (Lifestone Sending): lands where @lifestone does.</summary>
    public const int RecallTheSanctuary = 2023;
    /// <summary>WeenieError YouMustLinkToLifestoneToRecall / YouMustLinkToPortalToRecall.</summary>
    public const uint ErrorMustLinkLifestone = 0x049E, ErrorMustLinkPortal = 0x04A3;

    /// <summary>What a teleport right after your own spell words teaches.</summary>
    public enum HandCast
    {
        /// <summary>Nothing new (already known, a fixed recall, or can't tell); <c>why</c> says which.</summary>
        Nothing,
        /// <summary>The landing of <c>slot</c>.</summary>
        Learn,
        /// <summary>A portal tie recall, but not which: ask (/rnav tie 1|2).</summary>
        AskTie,
    }

    /// <summary>
    /// You spoke spell words, stood still and were teleported: a recall cast by hand. Which
    /// one, from where it landed: on a lifestone, Lifestone Recall; anywhere else, a portal tie
    /// recall (Portal Recall's landing is the last portal's, already known). A landing that
    /// matches a recall already known teaches nothing. When the spellbook leaves more than one
    /// tie recall (or Portal Recall with no last portal known), ask. No host calls.
    /// </summary>
    /// <param name="atLifestone">The landing is on a lifestone (the Atlas's).</param>
    /// <param name="nearUnits">Two landings this close are the same place.</param>
    public static HandCast GuessHandCast(RecallSpot landing, bool atLifestone, ISet<int>? known, RecallMemory memory,
        string charKey, double nearUnits, out RecallSlot slot, out string why)
    {
        slot = RecallSlot.Lifestone;
        if (known == null) { why = "the spellbook isn't read yet"; return HandCast.Nothing; }
        if (atLifestone)
        {
            if (!known.Contains(LifestoneRecall)) { why = "on a lifestone, but Lifestone Recall isn't in the spellbook"; return HandCast.Nothing; }
            if (Same(memory.Get(charKey, RecallSlot.Lifestone), landing, nearUnits)) { why = "Lifestone Recall, already known"; return HandCast.Nothing; }
            if (known.Contains(RecallTheSanctuary) && Same(memory.Get(charKey, RecallSlot.Sanctuary), landing, nearUnits))
            { why = "on your @lifestone lifestone: Lifestone Recall or Recall the Sanctuary, can't tell"; return HandCast.Nothing; }
            why = "on a lifestone: Lifestone Recall";
            return HandCast.Learn;
        }
        if (Same(memory.Get(charKey, RecallSlot.LastPortal), landing, nearUnits)) { why = "where the last portal went: Portal Recall"; return HandCast.Nothing; }
        var ties = new List<RecallSlot>(2);
        if (known.Contains(PrimaryPortalTieRecall)) ties.Add(RecallSlot.Tie1);
        if (known.Contains(SecondaryPortalTieRecall)) ties.Add(RecallSlot.Tie2);
        foreach (RecallSlot t in ties)
            if (Same(memory.Get(charKey, t), landing, nearUnits)) { slot = t; why = RecallMemory.SlotName(t) + ", already known"; return HandCast.Nothing; }
        if (ties.Count == 0) { why = "no portal tie recall in the spellbook (a spell's teleport RynthNav doesn't know)"; return HandCast.Nothing; }
        bool maybePortalRecall = known.Contains(PortalRecall) && memory.Get(charKey, RecallSlot.LastPortal) == null;
        if (ties.Count == 1 && !maybePortalRecall)
        {
            slot = ties[0];
            why = "the only portal tie recall in the spellbook";
            return HandCast.Learn;
        }
        slot = ties[0];
        why = maybePortalRecall ? "a portal tie recall or Portal Recall (no last portal known yet)" : "a portal tie recall, primary or secondary";
        return HandCast.AskTie;
    }

    /// <summary>Two landings are the same place: on the map within <paramref name="nearUnits"/>; off it, the same label.</summary>
    public static bool Same(RecallSpot? a, RecallSpot b, double nearUnits)
    {
        if (a == null || a.OnMap != b.OnMap) return false;
        if (a.OnMap) return NavCoords.Distance(a.Ns, a.Ew, b.Ns, b.Ew) < nearUnits;
        return a.Label.Equals(b.Label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The recall the server says isn't linked: "You must link to a lifestone to recall it" is
    /// always Lifestone Recall; "...to a portal..." is whichever portal recall was just cast
    /// (<paramref name="castSpell"/>, 0 = not known: nothing).
    /// </summary>
    public static RecallSlot? SlotUnlinked(uint error, int castSpell) => error switch
    {
        ErrorMustLinkLifestone => RecallSlot.Lifestone,
        ErrorMustLinkPortal => castSpell switch
        {
            PrimaryPortalTieRecall => RecallSlot.Tie1,
            SecondaryPortalTieRecall => RecallSlot.Tie2,
            PortalRecall => RecallSlot.LastPortal,
            _ => null,
        },
        _ => null,
    };

    /// <summary>The recall spell whose landing a slot holds (0 for the command recalls).</summary>
    public static int SpellFor(RecallSlot slot) => slot switch
    {
        RecallSlot.Lifestone => LifestoneRecall,
        RecallSlot.LastPortal => PortalRecall,
        RecallSlot.Tie1 => PrimaryPortalTieRecall,
        RecallSlot.Tie2 => SecondaryPortalTieRecall,
        _ => 0,
    };

    /// <summary>A slot's name for players ("Primary Portal Tie Recall", "@lifestone").</summary>
    public static string Title(RecallSlot slot) => slot switch
    {
        RecallSlot.Sanctuary => "@lifestone",
        RecallSlot.Lifestone => "Lifestone Recall",
        RecallSlot.LastPortal => "Portal Recall",
        RecallSlot.Tie1 => "Primary Portal Tie Recall",
        RecallSlot.Tie2 => "Secondary Portal Tie Recall",
        RecallSlot.House => "House recall",
        RecallSlot.Mansion => "Allegiance mansion recall",
        _ => "Allegiance hometown recall",
    };

    public static List<RecallLink> Links(List<RecallOption> options)
    {
        var links = new List<RecallLink>(options.Count);
        foreach (var o in options) links.Add(o.ToLink());
        return links;
    }
}
