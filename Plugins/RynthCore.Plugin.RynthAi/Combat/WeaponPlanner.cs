using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.CreatureData;

namespace RynthCore.Plugin.RynthAi;

/// <summary>One weapon from the user's Items list that combat may wield.</summary>
/// <param name="Kind">CombatMode.Melee / Missile / Magic.</param>
/// <param name="Element">Normalized element ("" = unknown).</param>
/// <param name="Rending">Element of a rending imbue ("" = none).</param>
/// <param name="SlayerType">The creature type it slays (PropertyInt 166 SlayerCreatureType, 0 = none).</param>
/// <param name="SlayerBonus">Its damage multiplier against that type (PropertyFloat 138 SlayerDamageBonus); 0 = not
/// known. ACE never sends float 138 on appraisal (it isn't an assessment property), so with a SlayerType it is
/// usually 0, and the weapon still slays (every slayer in the ACE world database has a bonus of 1.15 or more).</param>
internal sealed record WeaponCandidate(int Id, string Name, int Kind, string Element, string Rending, bool InHand,
    int SlayerType = 0, double SlayerBonus = 0);

/// <summary>What to fight a monster with: the weapon (0 = none), the element to cast or
/// debuff with, and why.</summary>
internal readonly record struct WeaponPlan(int WeaponId, string Element, string Source);

/// <summary>The kind of damage a character prefers to fight with (a Monsters rule's PreferredDamage).</summary>
internal enum DamageStyle { Auto, Melee, Missile, War, Void }

internal static class DamageStyles
{
    /// <summary>The names the Damage panel offers, in <see cref="DamageStyle"/> order.</summary>
    public static readonly string[] Names = { "Auto", "Melee", "Missile", "War Magic", "Void Magic" };

    /// <summary>Tolerant of case, spaces and the short forms ("war", "void"); anything unknown is Auto.</summary>
    public static DamageStyle Parse(string? text)
    {
        string t = (text ?? "").Replace(" ", "").Replace("_", "").Replace("-", "");
        if (t.Equals("Melee", StringComparison.OrdinalIgnoreCase)) return DamageStyle.Melee;
        if (t.Equals("Missile", StringComparison.OrdinalIgnoreCase)) return DamageStyle.Missile;
        if (t.Equals("War", StringComparison.OrdinalIgnoreCase) || t.Equals("WarMagic", StringComparison.OrdinalIgnoreCase)) return DamageStyle.War;
        if (t.Equals("Void", StringComparison.OrdinalIgnoreCase) || t.Equals("VoidMagic", StringComparison.OrdinalIgnoreCase)) return DamageStyle.Void;
        return DamageStyle.Auto;
    }

    public static string Name(DamageStyle s) => Names[(int)s];

    private static bool IsNether(string e) => e.Equals("Nether", StringComparison.OrdinalIgnoreCase);

    /// <summary>A weapon belongs to a style by its kind; a caster by its element (Nether = Void, anything else, or none, = War).</summary>
    public static bool Matches(WeaponCandidate c, DamageStyle s) => s switch
    {
        DamageStyle.Melee   => c.Kind == CombatMode.Melee,
        DamageStyle.Missile => c.Kind == CombatMode.Missile,
        DamageStyle.War     => c.Kind == CombatMode.Magic && !IsNether(c.Element),
        DamageStyle.Void    => c.Kind == CombatMode.Magic && IsNether(c.Element),
        _                   => true,
    };

    /// <summary>The cast test with the style's school applied: War never casts Nether, Void casts nothing else.</summary>
    public static Func<string, bool> Restrict(Func<string, bool> canCast, DamageStyle s) => s switch
    {
        DamageStyle.War  => e => !IsNether(e) && canCast(e),
        DamageStyle.Void => e => IsNether(e) && canCast(e),
        _                => canCast,
    };
}

/// <summary>
/// The element-and-weapon rule, pure (no host). The weakness decides the weapon, never the
/// other way round, except for a slayer (owner's rule, 2026-10-05: a monster is always
/// attacked with a slayer of it before an element choice, even when another weapon's element
/// meets a lower resistance; only a weapon the player set for that monster trumps it):
///   1. The weapon set on THIS monster's own Damage tab row, else on the Monsters rule that
///      matched it (not the Default row), is used as set, slayer or not (2026-10-22: the
///      rule's weapon used to be ignored, report R-20261022-193411-fb72). A Damage tab pick
///      must be in the Items list; a Monsters tab weapon need not be (CombatManager adds it to
///      the candidates). Its element is the rule's, else its own, else the weakest the
///      character can cast.
///   2. A slayer: a listed weapon of the character's main kind whose SlayerCreatureType is
///      the monster's CreatureType and whose SlayerDamageBonus is above 1 or not known (ACE
///      multiplies the whole hit by it, so it beats any element choice, and never sends the
///      bonus on appraisal). Several: the biggest bonus (an unknown one counts as
///      UnknownSlayerBonus), then the element order (the rule's element, else the weakest
///      group), then a matching rending, then the one in hand, then list order. Its element
///      is the wand's own, else a melee/missile weapon's own, else the rule's, else the
///      weakest the character can cast. Creature type unknown: no slayer step.
///      A slayer of another kind (a melee slayer for a mage) is not a candidate.
///   3. The Damage tab DEFAULT row's weapon (listed only), else the Monsters tab Default row's:
///      above the element steps, below a slayer.
///   4. A Monsters rule damage type (not Auto) is the element; wield the listed weapon of that
///      element (a matching rending first, then the one in hand, then list order).
///   Whatever is chosen, a wand casts its own element when it has one (owner's rule,
///   2026-09-30: "the wand should always cast its element, especially when rended").
///   5. Auto with a known weakness: walk the monster's elements from weakest (highest
///      multiplier) down; the first element a listed weapon of the character's main kind has
///      wins, and that weapon is wielded. Elements tied on the multiplier count as one step,
///      and within a step the weapon in hand wins, then a rending, then the weakness order.
///      A caster only counts for elements the character can cast. The weapon in hand is kept
///      when the winner is of another element and not at least minSwapGain more damage
///      (KeepInHand; both multipliers must be known).
///      No listed weapon has any of them: keep the one in hand (else the learned best, else
///      the first), and cast the weakest element the character can.
///   6. Weakness unknown: keep the weapon in hand (else the learned best, else the first) and
///      use its element; nothing known at all: Slash (or the first element the character can cast).
/// "Main kind" = the kind of the first usable weapon in the list, so a bow character with a
/// buff wand listed isn't switched to magic, and a melee character isn't swapped to a wand.
/// A preferred damage type (a Monsters rule's PreferredDamage: Melee, Missile, War Magic,
/// Void Magic) replaces that: the pool is the listed weapons of that style, for a character
/// who uses two kinds and wants to choose. With none listed (or none the character can use,
/// e.g. Void without Void Magic) it falls back to Auto, the main kind, rather than not fighting.
/// The monster's own Damage tab or Monsters rule weapon (step 1) still wins; the DEFAULT row's
/// weapon only when it is of the preferred style; War Magic never casts Nether and Void Magic nothing else.
/// Which fixed weapon is passed in (and whether it is a step-1 pick or a DEFAULT one) is
/// CombatManager.ComputePlan's: this monster's Damage tab pick, its Monsters rule's weapon, the
/// Damage tab DEFAULT row's, the Monsters Default row's, the first one usable and listed.
/// </summary>
internal static class WeaponPlanner
{
    /// <summary>Two multipliers this close are a tie.</summary>
    public const double TieEpsilon = 0.005;

    /// <summary>
    /// The bonus a slayer whose SlayerDamageBonus isn't known is ranked at: the smallest in the
    /// ACE world database (1.15), so a slayer with a known bigger bonus wins over it.
    /// </summary>
    public const double UnknownSlayerBonus = 1.15;

    /// <summary>Fallback element order when nothing is known ("Slash" first).</summary>
    public static readonly string[] FallbackOrder = { "Slash", "Fire", "Cold", "Lightning", "Acid", "Pierce", "Bludgeon", "Nether" };

    public static WeaponPlan Choose(
        IReadOnlyList<WeaponCandidate> listed,
        string? ruleElement,
        CreatureWeakness.Ranking? weak,
        Func<string, bool> canCast,
        int fixedWeaponId = 0,
        string fixedSource = "Damage tab",
        int learnedBestId = 0,
        int creatureType = 0,
        bool fixedIsDefault = false,
        double minSwapGain = 0,
        string? preferredDamage = null)
    {
        var plan = ChooseCore(listed, ruleElement, weak, canCast, fixedWeaponId, fixedSource, learnedBestId, creatureType,
            fixedIsDefault, minSwapGain, DamageStyles.Parse(preferredDamage), out var applied);
        return applied == DamageStyle.Auto ? plan : plan with { Source = plan.Source + ", prefers " + DamageStyles.Name(applied) };
    }

    private static WeaponPlan ChooseCore(
        IReadOnlyList<WeaponCandidate> listed,
        string? ruleElement,
        CreatureWeakness.Ranking? weak,
        Func<string, bool> canCast,
        int fixedWeaponId,
        string fixedSource,
        int learnedBestId,
        int creatureType,
        bool fixedIsDefault,
        double minSwapGain,
        DamageStyle style,
        out DamageStyle applied)
    {
        applied = DamageStyle.Auto;
        string rule = WeaponElements.Normalize(ruleElement);
        bool ruleSet = rule.Length > 0;
        WeaponCandidate? fixedPick = fixedWeaponId != 0 ? Find(listed, fixedWeaponId) : null;

        // 1. The weapon the player set on this monster's own Damage tab row, or on the Monsters
        //    rule that matched it (a candidate; see the class note): honoured, slayer or not (owner, 2026-10-05: "the
        //    only time a slayer property gets trumped is if the player manually inputs a weapon
        //    without it"). A DEFAULT row's weapon (Damage tab or Monsters tab) is not a pick for
        //    this monster: a slayer of it beats that one (step 3).
        if (fixedPick != null && !fixedIsDefault)
            return FixedPlan(fixedPick, Slays(fixedPick, creatureType)
                ? fixedSource + ", " + SlayerSource(creatureType, fixedPick.SlayerBonus) : fixedSource, rule, weak, canCast);

        if (listed.Count == 0)
        {
            // Nothing listed: no weapon; the element still follows the rule / weakness.
            string elem = ruleSet ? rule : WeakestCastable(weak, canCast, null) ?? Fallback(canCast);
            return new WeaponPlan(0, elem, "none");
        }

        var pool = new List<WeaponCandidate>();
        if (style != DamageStyle.Auto)
        {
            // The preferred style, when the character has a weapon of it it can use: Void needs the
            // Nether school, War any school but Nether. Otherwise Auto, below.
            var cast = DamageStyles.Restrict(canCast, style);
            bool schoolOpen = false;
            foreach (string e in FallbackOrder) if (cast(e)) { schoolOpen = true; break; }
            if (style is DamageStyle.Melee or DamageStyle.Missile || schoolOpen)
                foreach (var c in listed) if (DamageStyles.Matches(c, style)) pool.Add(c);
            if (pool.Count > 0) { applied = style; canCast = cast; }
        }
        if (pool.Count == 0)
        {
            int mainKind = listed[0].Kind;
            foreach (var c in listed) if (c.Kind == mainKind) pool.Add(c);
        }
        bool styled = applied != DamageStyle.Auto;

        // 2. A slayer of this monster's creature type.
        if (creatureType > 0)
        {
            var s = BestSlayer(pool, creatureType, rule, weak, canCast);
            if (s != null)
            {
                string elem = WandElement(s, canCast)
                    ?? (s.Kind != CombatMode.Magic && s.Element.Length > 0 ? s.Element
                        : ruleSet ? rule
                        : WeakestCastable(weak, canCast, s) ?? ElementOrFallback(s, canCast));
                return new WeaponPlan(s.Id, elem, SlayerSource(creatureType, s.SlayerBonus));
            }
        }

        // 3. The Damage tab DEFAULT row's weapon, else the Monsters Default row's:
        //    no slayer of this monster is listed.
        if (fixedPick != null && (!styled || pool.Contains(fixedPick)))
            return FixedPlan(fixedPick, fixedSource, rule, weak, canCast);

        // 4. An explicit damage type: the listed weapon of that element (main kind first).
        if (ruleSet)
        {
            var w = BestOf(pool, rule, null) ?? (styled ? null : BestOf(listed, rule, null));
            if (w != null) return new WeaponPlan(w.Id, rule, "Monsters rule " + rule);
            var keep = InHand(pool) ?? pool[0];
            // A wand casts its own element: the rule's element only when the weapon has none.
            string keepElem = WandElement(keep, canCast) ?? rule;
            return new WeaponPlan(keep.Id, keepElem, keep.InHand ? "Monsters rule " + rule + " (none listed; in hand)" : "Monsters rule " + rule + " (none listed)");
        }

        // 5. Auto, weakness known: weakest element a listed weapon has.
        if (weak != null && weak.Order.Count > 0)
        {
            string handElem = InHand(pool)?.Element ?? "";
            foreach (var group in TieGroups(weak))
            {
                // Within a tie: the element of the weapon in hand (no needless swap), else
                // the weakness order; for one element: a matching rending, then the weapon
                // in hand, then list order.
                bool handInGroup = handElem.Length > 0 && group.Contains(handElem);
                WeaponCandidate? best = null;
                int bestRank = int.MaxValue;
                for (int i = 0; i < pool.Count; i++)
                {
                    var c = pool[i];
                    int idx = group.IndexOf(c.Element);
                    if (idx < 0) continue;
                    if (c.Kind == CombatMode.Magic && !canCast(c.Element)) continue;
                    int elemRank = handInGroup && c.Element == handElem ? 0 : 1 + idx;
                    int rank = elemRank * 100000
                             + (c.Rending.Length > 0 && c.Rending == c.Element ? 0 : 10000)
                             + (c.InHand ? 0 : 1000)
                             + i;
                    if (rank < bestRank) { bestRank = rank; best = c; }
                }
                if (best != null)
                {
                    var held = KeepInHand(pool, best, weak, canCast, minSwapGain);
                    if (held != null)
                        return new WeaponPlan(held.Id, held.Element,
                            $"weak to {best.Element} ({weak.Source}); {held.Element} in hand is within {minSwapGain * 100:0}%");
                    return new WeaponPlan(best.Id, best.Element, $"weak to {best.Element} ({weak.Source})");
                }
            }

            // No listed weapon has any element of the ranking: keep what we have.
            var keep = InHand(pool) ?? Find(pool, learnedBestId) ?? pool[0];
            // A wand whose element the character can't cast (no Void Magic for a Nether
            // wand, no spells of it) casts the weakest element the character can, like a
            // wand with no element; only a melee/missile weapon keeps its own element here.
            string elem = WandElement(keep, canCast)
                ?? (keep.Element.Length == 0 || keep.Kind == CombatMode.Magic
                    ? WeakestCastable(weak, canCast, keep) ?? Fallback(canCast)
                    : keep.Element);
            return new WeaponPlan(keep.Id, elem, $"weak to {elem} ({weak.Source}); no listed weapon of it");
        }

        // 6. Weakness unknown: the weapon in hand and its element.
        var hand = InHand(pool);
        if (hand != null)
            return new WeaponPlan(hand.Id, ElementOrFallback(hand, canCast), "in hand (weakness unknown)");
        var learned = Find(pool, learnedBestId);
        if (learned != null)
            return new WeaponPlan(learned.Id, ElementOrFallback(learned, canCast), "Damage tab learned");
        return new WeaponPlan(pool[0].Id, ElementOrFallback(pool[0], canCast), "ItemRules");
    }

    /// <summary>
    /// Step 5's swap threshold: the weapon in hand, when <paramref name="best"/> is of another
    /// element and does less than <paramref name="minSwapGain"/> (0.2 = 20%) more damage to the
    /// monster than it. A swap costs seconds of not attacking (a launcher with other ammo about
    /// 4 s), so a small edge isn't worth it. Null (swap) when either multiplier isn't known
    /// (a learned or seeded ranking has the order only), the hand weapon is immune-resisted (0),
    /// or the threshold is off.
    /// </summary>
    public static WeaponCandidate? KeepInHand(IReadOnlyList<WeaponCandidate> pool, WeaponCandidate best,
        CreatureWeakness.Ranking weak, Func<string, bool> canCast, double minSwapGain)
    {
        if (minSwapGain <= 0) return null;
        var hand = InHand(pool);
        if (hand == null || hand.Id == best.Id || hand.Element.Length == 0 || hand.Element == best.Element) return null;
        if (hand.Kind == CombatMode.Magic && !canCast(hand.Element)) return null;
        double handMult = MultOf(weak, hand.Element), bestMult = MultOf(weak, best.Element);
        if (double.IsNaN(handMult) || double.IsNaN(bestMult) || handMult <= 0) return null;
        return bestMult < handMult * (1 + minSwapGain) - TieEpsilon ? hand : null;
    }

    /// <summary>The ranking's damage multiplier for <paramref name="element"/>; NaN when not listed or not known.</summary>
    private static double MultOf(CreatureWeakness.Ranking weak, string element)
    {
        foreach (var (e, m) in weak.Order)
            if (e == element) return m;
        return double.NaN;
    }

    /// <summary>
    /// The ranking's elements grouped by equal multiplier, weakest group first. A learned
    /// ranking (NaN multipliers) is one element per group.
    /// </summary>
    public static List<List<string>> TieGroups(CreatureWeakness.Ranking weak)
    {
        var groups = new List<List<string>>();
        double last = double.NaN;
        foreach (var (e, m) in weak.Order)
        {
            if (groups.Count > 0 && !double.IsNaN(m) && !double.IsNaN(last) && Math.Abs(m - last) <= TieEpsilon)
                groups[^1].Add(e);
            else
                groups.Add(new List<string> { e });
            last = m;
        }
        return groups;
    }

    /// <summary>The weakest element the character can cast; ties go to <paramref name="wand"/>'s element.</summary>
    public static string? WeakestCastable(CreatureWeakness.Ranking? weak, Func<string, bool> canCast, WeaponCandidate? wand)
    {
        if (weak == null) return null;
        foreach (var group in TieGroups(weak))
        {
            if (wand != null && wand.Element.Length > 0 && group.Contains(wand.Element) && canCast(wand.Element))
                return wand.Element;
            foreach (string e in group)
                if (canCast(e)) return e;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="c"/> slays <paramref name="creatureType"/> (ACE: SlayerCreatureType ==
    /// CreatureType). A bonus that is known must be above 1; an unknown one (0: ACE doesn't send
    /// float 138 on appraisal) counts.
    /// </summary>
    public static bool Slays(WeaponCandidate c, int creatureType) =>
        creatureType > 0 && c.SlayerType == creatureType && (c.SlayerBonus <= 0 || c.SlayerBonus > 1.0 + TieEpsilon);

    /// <summary>The bonus a slayer is ranked at: its own when known, else <see cref="UnknownSlayerBonus"/>.</summary>
    public static double RankBonus(WeaponCandidate c) => c.SlayerBonus > 0 ? c.SlayerBonus : UnknownSlayerBonus;

    /// <summary>The plan's source for a slayer: "slayer (Olthoi x2)", or "slayer (Olthoi)" when the bonus isn't known.</summary>
    public static string SlayerSource(int creatureType, double bonus) => bonus > 0
        ? $"slayer ({CreatureTypeNames.Name(creatureType)} x{bonus.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})"
        : $"slayer ({CreatureTypeNames.Name(creatureType)})";

    /// <summary>
    /// The matching slayer to wield: the biggest bonus (<see cref="RankBonus"/>; ties within <see cref="TieEpsilon"/>),
    /// then the element order (the rule's element, else the weakest group), then a matching
    /// rending, then the one in hand, then list order. Null when none matches.
    /// </summary>
    private static WeaponCandidate? BestSlayer(List<WeaponCandidate> pool, int creatureType, string rule,
        CreatureWeakness.Ranking? weak, Func<string, bool> canCast)
    {
        double top = 0;
        foreach (var c in pool) if (Slays(c, creatureType) && RankBonus(c) > top) top = RankBonus(c);
        if (top <= 0) return null;

        List<List<string>>? groups = rule.Length == 0 && weak != null && weak.Order.Count > 0 ? TieGroups(weak) : null;
        WeaponCandidate? best = null;
        int bestRank = int.MaxValue;
        for (int i = 0; i < pool.Count; i++)
        {
            var c = pool[i];
            if (!Slays(c, creatureType) || RankBonus(c) < top - TieEpsilon) continue;
            int elemRank = 0;
            if (rule.Length > 0) elemRank = c.Element == rule ? 0 : 1;
            else if (groups != null)
            {
                elemRank = groups.Count;
                if (c.Element.Length > 0 && (c.Kind != CombatMode.Magic || canCast(c.Element)))
                    for (int g = 0; g < groups.Count; g++)
                        if (groups[g].Contains(c.Element)) { elemRank = g; break; }
            }
            int rank = elemRank * 100000
                     + (c.Rending.Length > 0 && c.Rending == c.Element ? 0 : 10000)
                     + (c.InHand ? 0 : 1000)
                     + i;
            if (rank < bestRank) { bestRank = rank; best = c; }
        }
        return best;
    }

    /// <summary>A Damage tab weapon: the wand's own element, else the rule's, else its own / the weakest castable.</summary>
    private static WeaponPlan FixedPlan(WeaponCandidate f, string source, string rule, CreatureWeakness.Ranking? weak, Func<string, bool> canCast)
    {
        string elem = WandElement(f, canCast) ?? (rule.Length > 0 ? rule : ElementFor(f, weak, canCast));
        return new WeaponPlan(f.Id, elem, source);
    }

    /// <summary>
    /// The element for a weapon the user fixed (the Damage tab): its own element when known
    /// (the walk down the weakness list stops at the only candidate's element), else the
    /// weakest element the character can cast.
    /// </summary>
    private static string ElementFor(WeaponCandidate w, CreatureWeakness.Ranking? weak, Func<string, bool> canCast)
    {
        if (w.Element.Length > 0 && (w.Kind != CombatMode.Magic || canCast(w.Element))) return w.Element;
        return WeakestCastable(weak, canCast, w) ?? ElementOrFallback(w, canCast);
    }

    /// <summary>
    /// A wand (caster) always casts its own element when it has one (damage type, icon element
    /// or rending) and the character can cast it: its elemental bonus (and a rending) only
    /// works for spells of its element. Null for other weapons or an element not known.
    /// </summary>
    private static string? WandElement(WeaponCandidate w, Func<string, bool> canCast) =>
        w.Kind == CombatMode.Magic && w.Element.Length > 0 && canCast(w.Element) ? w.Element : null;

    private static string ElementOrFallback(WeaponCandidate w, Func<string, bool> canCast)
    {
        if (w.Element.Length > 0 && (w.Kind != CombatMode.Magic || canCast(w.Element))) return w.Element;
        return Fallback(canCast);
    }

    private static string Fallback(Func<string, bool> canCast)
    {
        foreach (string e in FallbackOrder)
            if (canCast(e)) return e;
        return "Slash";
    }

    /// <summary>The weapon of <paramref name="element"/> to wield: matching rending first, then in hand, then list order.</summary>
    private static WeaponCandidate? BestOf(IReadOnlyList<WeaponCandidate> list, string element, Func<string, bool>? canCast)
    {
        WeaponCandidate? best = null;
        int bestRank = int.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c.Element != element) continue;
            if (canCast != null && c.Kind == CombatMode.Magic && !canCast(element)) continue;
            int rank = (c.Rending == element ? 0 : 10000) + (c.InHand ? 0 : 1000) + i;
            if (rank < bestRank) { bestRank = rank; best = c; }
        }
        return best;
    }

    private static WeaponCandidate? InHand(IReadOnlyList<WeaponCandidate> list)
    {
        foreach (var c in list) if (c.InHand) return c;
        return null;
    }

    private static WeaponCandidate? Find(IReadOnlyList<WeaponCandidate> list, int id)
    {
        if (id == 0) return null;
        foreach (var c in list) if (c.Id == id) return c;
        return null;
    }
}
