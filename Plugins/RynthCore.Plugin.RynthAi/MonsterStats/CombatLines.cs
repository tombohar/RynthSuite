// ============================================================================
//  RynthAi - MonsterStats/CombatLines.cs
//  The spell lines of the combat log that say what became of a spell between
//  you and a monster: it landed, it was resisted, or it could do nothing. The
//  server reports spells only as text (Magic chat, type 7); swings, hits and
//  evades come as typed game events (CombatEvents.cs), so no melee or missile
//  line is read here.
//
//  One table, the server's exact strings (ACE's, which follow retail; the ACE
//  source line is next to each row). MonsterStatsTests checks every row and the
//  near-misses below. Lines whose form is also used for items ("Frost Hand
//  Wraps cast ... on you") are marked MayBeItem: the plugin counts them only
//  for a creature (RynthAiPlugin.MonsterStats.cs).
//
//  Not counted, on purpose:
//    "You cast X on yourself..."              your own buffs
//    "<item> cast X on you"                   an item's spell (MayBeItem rows)
//    "X tried and failed to cast a spell at you!"   a fizzle: it never reached you
//    "You <verb> X for N points of periodic nether damage!"  a damage-over-time tick, not a cast
//    "You receive N points of periodic ..."   the same, on you
//    "With S you restore N points of V to X." / "X casts S and restores ..."  heals
//    "You lose N points of V due to casting S on X"  an infusion you cast (it gives)
//    "You gain N points of V due to X casting S on you"  a gift to you
//    "Your cloak reduced the damage ..." / "The cloak of X reduced ..."  a cloak (player gear)
//    "X evaded your attack." / "You evaded X!" / "You slash X for N points of slashing damage!"
//                                             the client's own lines for the typed events
//  Absorbs and partial resists: ACE reduces magic damage by absorb gear without a
//  word (the damage line just shows less), and a resist is all or nothing; there
//  is no line to count.
//
//  Pure (no host), so the host tests drive it directly.
// ============================================================================
using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

/// <summary>What became of one spell.</summary>
internal enum SpellOutcome
{
    /// <summary>It reached its target and took hold (damage, a drain, an enchantment).</summary>
    Landed,
    /// <summary>The target's magic defense stopped it.</summary>
    Resisted,
    /// <summary>It passed the resist check but could do nothing (the target is immune to it).</summary>
    NoEffect,
}

/// <summary>
/// One spell line: whose spell (<see cref="ByYou"/>: yours at <see cref="Name"/>, else
/// <see cref="Name"/>'s at you), what became of it, the health damage it did (0 when the line
/// gives none), and whether the name may be an item's (<see cref="MayBeItem"/>).
/// </summary>
internal readonly record struct CombatLine(string Rule, bool ByYou, SpellOutcome Outcome, string Name, int Damage, bool MayBeItem);

internal static class CombatLines
{
    /// <summary>One form of line: its id, whose spell and its outcome, a word it must contain, and the pattern.</summary>
    internal sealed record Row(string Id, bool ByYou, SpellOutcome Outcome, bool MayBeItem, string Hint, string Source, Regex Pattern);

    private const RegexOptions Opt = RegexOptions.CultureInvariant;
    // "Critical hit! ", "Overpower! ", "Sneak Attack! " in front of a damage line (SpellProjectile.cs critMsg etc.).
    private const string Prefix = "(?:(?:Critical hit|Overpower|Sneak Attack)! )*";
    private const string Num = @"(?<n>\d[\d,]*)";
    private const string Vital = "(?<vital>health|stamina|mana)";
    // What follows an enchantment that met one already there (WorldObject_Magic.cs suffix).
    private const string EnchantTail = ", (?:refreshing|surpassing|but it is surpassed by) .+";

    /// <summary>Every line counted, in the order tried.</summary>
    internal static readonly Row[] Rows =
    {
        // ── Your spells at a monster ──
        new("you-damage", true, SpellOutcome.Landed, false, " points with ",
            "SpellProjectile.cs: {crit}{overpower}{sneak}You {verb} {target} for {amount} points with {spell}.{critProt}",
            new Regex("^" + Prefix + "You (?<verb>[a-z]+) (?<name>.+?) for " + Num + " points? with (?<spell>.+?)\\."
                + "(?: Your critical hit was avoided with their augmentation!)?$", Opt)),
        new("you-drain", true, SpellOutcome.Landed, false, " you drain ",
            "SpellProjectile.cs / WorldObject_Magic.cs: With {spell} you drain {amount} points of {vital} from {target}.",
            new Regex("^With (?<spell>.+?) you drain " + Num + " points? of " + Vital + " from (?<name>.+?)\\.$", Opt)),
        new("you-transfer", true, SpellOutcome.Landed, false, " due to casting ",
            "WorldObject_Magic.cs: You gain {n} points of {vital} due to casting {spell} on {target}",
            new Regex("^You gain " + Num + " points? of " + Vital + " due to casting (?<spell>.+?) on (?<name>.+)$", Opt)),
        new("you-enchant", true, SpellOutcome.Landed, true, "You cast ",
            "WorldObject_Magic.cs: You cast {spell} on {target}{suffix} (and the dispel forms)",
            new Regex("^You cast (?<spell>.+?) on (?<name>.+?)(?:" + EnchantTail + "| and dispel: .+|, but the dispel fails\\.)?$", Opt)),
        new("you-resisted", true, SpellOutcome.Resisted, false, " resists your spell",
            "WorldObject_Magic.cs: {target} resists your spell",
            new Regex("^(?<name>.+) resists your spell$", Opt)),
        new("you-noeffect", true, SpellOutcome.NoEffect, false, "You fail to affect ",
            "Player_Magic.cs / WorldObject_Magic.cs: You fail to affect {target} with {spell}",
            new Regex("^You fail to affect (?<name>.+?) with (?<spell>.+)$", Opt)),

        // ── A monster's spells at you ──
        new("it-damage", false, SpellOutcome.Landed, false, " you for ",
            "SpellProjectile.cs: {crit}{overpower}{sneak}{caster} {verbs} you for {amount} points with {spell}.{critProt}",
            new Regex("^" + Prefix + "(?<name>.+?) (?<verb>[a-z]+) you for " + Num + " points? with (?<spell>.+?)\\."
                + "(?: Your augmentation allows you to avoid a critical hit!)?$", Opt)),
        new("it-drain", false, SpellOutcome.Landed, false, " and drains ",
            "SpellProjectile.cs / WorldObject_Magic.cs: {caster} casts {spell} and drains {amount} points of your {vital}.",
            new Regex("^(?<name>.+?) casts (?<spell>.+?) and drains " + Num + " points? of your " + Vital + "\\.$", Opt)),
        new("it-transfer", false, SpellOutcome.Landed, false, " casting ",
            "WorldObject_Magic.cs: You lose {n} points of {vital} due to {caster} casting {spell} on you",
            new Regex("^You lose " + Num + " points? of " + Vital + " due to (?<name>.+?) casting (?<spell>.+?) on you$", Opt)),
        new("it-resisted", false, SpellOutcome.Resisted, false, "You resist the spell cast by ",
            "WorldObject_Magic.cs: You resist the spell cast by {caster}",
            new Regex("^You resist the spell cast by (?<name>.+)$", Opt)),
        new("it-enchant", false, SpellOutcome.Landed, true, " cast ",
            "WorldObject_Magic.cs: {caster} cast {spell} on you{suffix} / on your {item}{suffix}",
            new Regex("^(?<name>.+?) cast (?<spell>.+?) on (?:you|your (?<item>.+?))(?:" + EnchantTail + ")?$", Opt)),
        new("it-dispel", false, SpellOutcome.Landed, true, " casts ",
            "WorldObject_Magic.cs: {caster} casts {spell} on you and dispels: {list}. / , but the dispel fails.",
            new Regex("^(?<name>.+?) casts (?<spell>.+?) on you(?: and dispels: .+|, but the dispel fails\\.)$", Opt)),
        new("it-noeffect", false, SpellOutcome.NoEffect, false, " fails to affect you with ",
            "WorldObject_Magic.cs: {caster} fails to affect you with {spell}",
            new Regex("^(?<name>.+?) fails to affect you with (?<spell>.+)$", Opt)),
    };

    /// <summary>
    /// Reads one chat line. True for a spell line of the table, with whose spell it was, its
    /// outcome, the monster's name and the health damage; false for anything else (and for a
    /// line about yourself). The caller has already left out lines in a player's chat types.
    /// </summary>
    public static bool TryParse(string? text, out CombatLine line)
    {
        line = default;
        if (string.IsNullOrEmpty(text)) return false;
        string t = text.Trim();
        if (t.Length < 8) return false;
        foreach (Row row in Rows)
        {
            if (t.IndexOf(row.Hint, StringComparison.Ordinal) < 0) continue;
            Match m = row.Pattern.Match(t);
            if (!m.Success) continue;
            string name = m.Groups["name"].Value.Trim();
            if (!IsMonsterName(name)) return false;
            int damage = 0;
            Group n = m.Groups["n"];
            Group vital = m.Groups["vital"];
            // Damage is health taken: a damage line's amount, a health drain's; not stamina or mana,
            // and not what a transfer gave you (the line says what you gained, not what it lost).
            bool health = !vital.Success || vital.Value == "health";
            if (n.Success && health && row.Id is not "you-transfer") damage = ParseAmount(n.Value);
            line = new CombatLine(row.Id, row.ByYou, row.Outcome, name, damage, row.MayBeItem);
            return true;
        }
        return false;
    }

    /// <summary>Not you: "yourself" (and "yourself and lose ..."), "you", "your ...", or nothing.</summary>
    private static bool IsMonsterName(string name)
    {
        if (name.Length == 0) return false;
        if (name.Equals("You", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("yourself", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("your ", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>"1,234" as 1234; 0 for anything that isn't a number up to a million.</summary>
    internal static int ParseAmount(string s)
    {
        long v = 0;
        foreach (char c in s)
        {
            if (c == ',') continue;
            if (c < '0' || c > '9') return 0;
            v = v * 10 + (c - '0');
            if (v > 1_000_000) return 0;
        }
        return (int)v;
    }
}
