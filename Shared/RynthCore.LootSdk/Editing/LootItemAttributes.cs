using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

// "Add to loot profile"'s attribute checklist (2026-10-18): every attribute a clicked item
// has that a loot rule can require, grouped for the popup, each with the item's value, the
// comparisons it offers and whether it is ticked by default. LootItemRules.Build turns the
// ticked rows into conditions, in row order. With no choices from the popup (an older engine,
// /ra loot add) the defaults reproduce the rules "items like this" and "Include T11
// attributes" always made.

/// <summary>How a checklist row is required (LootEditItemAttribute.Op).</summary>
public enum LootItemAttributeOp
{
    AtLeast = 0,
    Exactly = 1,
    AtMost = 2,
    Not = 3,
    /// <summary>A spell by its exact name.</summary>
    SpellExact = 10,
    /// <summary>This spell at this tier or a higher one (Major Strength: Major, Epic or Legendary).</summary>
    SpellOrBetter = 11,
    /// <summary>This spell at any tier.</summary>
    SpellAnyTier = 12,
}

/// <summary>One checklist row while a rule is built (the wire form is LootEditItemAttribute).</summary>
public sealed class LootItemAttribute
{
    internal enum Shape { Long, Double, TotalRatings, Spell, CantripTier, SpellCount }

    public string Id { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Tip { get; init; } = string.Empty;
    internal Shape Kind { get; init; }
    /// <summary>The property key (long or double rows), or the cantrip tier (CantripTier rows).</summary>
    internal int Key { get; init; }
    internal double ItemValue { get; init; }
    public string ItemText { get; init; } = string.Empty;
    public LootItemAttributeOp[] Ops { get; init; } = Array.Empty<LootItemAttributeOp>();
    /// <summary>The item's value can be replaced by a typed number.</summary>
    public bool Editable { get; init; }
    /// <summary>Spell rows: the spell's name.</summary>
    internal string Spell { get; init; } = string.Empty;

    // Where the row stands now (defaults, then the player's choice).
    public bool On { get; set; }
    public LootItemAttributeOp Op { get; set; }
    internal double Value { get; set; }
    /// <summary>The value as the player typed it (empty: the item's).</summary>
    internal string Typed { get; set; } = string.Empty;

    public LootEditItemAttribute ToWire() => new()
    {
        Id = Id, Group = Group, Label = Label, ItemValue = ItemText, Tip = Tip, Editable = Editable, On = On,
        Op = (int)Op, Ops = new List<int>(Array.ConvertAll(Ops, o => (int)o)),
        Value = Editable ? LootItemAttributes.Number(Value) : ItemText,
    };
}

public static class LootItemAttributes
{
    public const string GroupItem = "Item", GroupGear = "Weapon / Armor", GroupRatings = "Ratings", GroupSpells = "Spells",
        GroupPet = "Pet essence", GroupT11 = "T11", GroupRequirements = "Requirements";

    /// <summary>Cantrip tiers, lowest first: "Major Strength" is tier 2.</summary>
    public static readonly string[] CantripTiers = { "Minor", "Major", "Epic", "Legendary" };
    private static readonly string[] Roman = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII" };

    // Pet essence property ids (ACECustom; the Mini Remote's PetStats reads the same).
    public const int KeyBond = 9053, KeyCapturedDamageType = 9054, KeyPotencyStored = 9056, KeyBreedingCharges = 9057,
        KeyMutations = 9075, KeySummoningMastery = 362, KeyUseLevelRequirement = 364, KeyUseRequiresSkill = 366,
        KeyUseRequiresSkillSpec = 368, KeyUseRequiresLevel = 369;
    private const int SkillSummoning = 54;

    private enum Cmp { Min, Max, Named }

    private readonly record struct Spec(string Group, int Key, bool IsDouble, string Label, Cmp Cmp, string Tip = "");

    /// <summary>The property rows, in display (and condition) order. Spells, T11 and the rating total are built separately.</summary>
    private static readonly Spec[] Specs =
    {
        new(GroupItem, LootItemRules.KeyMaterial, false, "Material", Cmp.Named),
        new(GroupItem, LootItemRules.KeyWorkmanship, false, "Workmanship", Cmp.Min),
        new(GroupItem, 19, false, "Value", Cmp.Min),
        new(GroupItem, 5, false, "Burden", Cmp.Max),
        new(GroupItem, 106, false, "Spellcraft", Cmp.Min),
        new(GroupItem, 108, false, "Max mana", Cmp.Min),
        new(GroupItem, 109, false, "Arcane Lore to use", Cmp.Max, "The Arcane Lore the item's spells need (lower is easier)."),
        new(GroupItem, 92, false, "Uses left", Cmp.Min),
        new(GroupItem, 91, false, "Max uses", Cmp.Min),
        new(GroupItem, 265, false, "Armor set", Cmp.Named, "The set id (the same set's pieces share it)."),
        new(GroupItem, 353, false, "Imbue", Cmp.Named),
        new(GroupItem, 166, false, "Slayer", Cmp.Named),
        new(GroupItem, 45, false, "Damage type", Cmp.Named),

        new(GroupGear, LootItemRules.KeyWieldSkill, false, "Wield skill", Cmp.Named),
        new(GroupGear, LootItemRules.KeyWeaponSkill, false, "Weapon skill", Cmp.Named),
        new(GroupGear, LootItemRules.KeyDamage, false, "Max damage", Cmp.Min),
        new(GroupGear, 22, true, "Damage variance", Cmp.Max, "Lower is steadier damage."),
        new(GroupGear, 63, true, "Damage modifier", Cmp.Min, "Missile weapons' damage multiplier."),
        new(GroupGear, 62, true, "Attack modifier", Cmp.Min),
        new(GroupGear, 29, true, "Melee defense modifier", Cmp.Min),
        new(GroupGear, 149, true, "Missile defense modifier", Cmp.Min),
        new(GroupGear, 150, true, "Magic defense modifier", Cmp.Min),
        new(GroupGear, 144, true, "Mana conversion modifier", Cmp.Min),
        new(GroupGear, LootItemRules.KeyElementalDamageMod, true, "Elemental damage vs monsters", Cmp.Min),
        new(GroupGear, LootItemRules.KeyEquipSlots, false, "Equip slots", Cmp.Named, "Where it is worn or wielded."),
        new(GroupGear, LootItemRules.KeyArmorLevel, false, "Armor level", Cmp.Min),

        // The rating total comes first in this group (built separately); then each rating.
        new(GroupRatings, 370, false, "Damage rating", Cmp.Min),
        new(GroupRatings, 371, false, "Damage resist rating", Cmp.Min),
        new(GroupRatings, 372, false, "Crit rating", Cmp.Min),
        new(GroupRatings, 373, false, "Crit resist rating", Cmp.Min),
        new(GroupRatings, 374, false, "Crit damage rating", Cmp.Min),
        new(GroupRatings, 375, false, "Crit damage resist rating", Cmp.Min),
        new(GroupRatings, 376, false, "Heal boost rating", Cmp.Min),
        new(GroupRatings, 377, false, "Nether resist rating", Cmp.Min),
        new(GroupRatings, 379, false, "Vitality rating", Cmp.Min),

        new(GroupPet, KeyBond, false, "Bond level", Cmp.Min),
        new(GroupPet, KeyUseRequiresLevel, false, "Level", Cmp.Min),
        new(GroupPet, KeyUseLevelRequirement, false, "Level requirement", Cmp.Min),
        new(GroupPet, KeyPotencyStored, false, "Potency stored", Cmp.Min),
        new(GroupPet, KeySummoningMastery, false, "Summoning mastery", Cmp.Named),
        new(GroupPet, KeyMutations, false, "Mutations", Cmp.Min),
        new(GroupPet, 9070, false, "Damage mutations", Cmp.Min),
        new(GroupPet, 9071, false, "Damage resist mutations", Cmp.Min),
        new(GroupPet, 9072, false, "Crit mutations", Cmp.Min),
        new(GroupPet, 9073, false, "Vitality mutations", Cmp.Min),
        new(GroupPet, 9074, false, "Potency mutations", Cmp.Min),
        new(GroupPet, KeyCapturedDamageType, false, "Captured damage type", Cmp.Named),
        new(GroupPet, 307, false, "Pet damage rating", Cmp.Min),
        new(GroupPet, 308, false, "Pet damage resist rating", Cmp.Min),
        new(GroupPet, 313, false, "Pet crit rating", Cmp.Min),
        new(GroupPet, 314, false, "Pet crit damage rating", Cmp.Min),
        new(GroupPet, 315, false, "Pet crit resist rating", Cmp.Min),
        new(GroupPet, 316, false, "Pet crit damage resist rating", Cmp.Min),
        new(GroupPet, KeyBreedingCharges, false, "Breeding charges", Cmp.Min),

        new(GroupRequirements, 158, false, "Wield requirement", Cmp.Named),
        new(GroupRequirements, 160, false, "Wield difficulty", Cmp.Max, "Lower is easier to wield."),
    };

    /// <summary>Every int property a checklist can use: what RynthAi reads into LootItemFacts.</summary>
    public static readonly int[] IntKeys = BuildKeys(isDouble: false);
    /// <summary>Every float property a checklist can use.</summary>
    public static readonly int[] DoubleKeys = BuildKeys(isDouble: true);

    private static int[] BuildKeys(bool isDouble)
    {
        var keys = new List<int>();
        foreach (Spec s in Specs)
            if (s.IsDouble == isDouble && !keys.Contains(s.Key)) keys.Add(s.Key);
        if (!isDouble)
            foreach (int k in new[] { KeyUseRequiresSkill, KeyUseRequiresSkillSpec })
                if (!keys.Contains(k)) keys.Add(k);
        return keys.ToArray();
    }

    /// <summary>
    /// A combat / summoning essence: the plugin says so, or the item carries ACECustom's bond or
    /// potency, or using it needs Summoning.
    /// </summary>
    public static bool IsPetEssence(LootItemFacts f)
        => f.IsPetEssence || f.Int(KeyBond) > 0 || f.Ints.ContainsKey(KeyPotencyStored)
           || f.Int(KeyUseRequiresSkill) == SkillSummoning || f.Int(KeyUseRequiresSkillSpec) == SkillSummoning
           || f.ObjectClass == (int)AcObjectClass.CombatPet;

    /// <summary>
    /// The item's rows with their defaults: ticked for "items like this" (<paramref name="like"/>)
    /// what that match always required, the T11 rows when <paramref name="includeT11"/>.
    /// </summary>
    public static List<LootItemAttribute> For(LootItemFacts f, bool like, bool includeT11)
    {
        var rows = new List<LootItemAttribute>();
        bool pet = IsPetEssence(f);
        HashSet<int> likeKeys = like ? LikeKeys(f, pet) : new HashSet<int>();

        foreach (Spec s in Specs)
        {
            if (s.Group == GroupPet && !pet) continue;
            if (s.Group == GroupRatings && s.Key == 370) AddTotalRatings(rows, f, like);
            double v = s.IsDouble ? f.Double(s.Key) : f.Int(s.Key);
            if (s.Cmp == Cmp.Named ? v == 0 : v <= 0) continue;
            // A pet's level comes from 369 when it has one (PetStats does the same).
            if (s.Key == KeyUseLevelRequirement && f.Int(KeyUseRequiresLevel) > 0) continue;
            rows.Add(PropertyRow(s, v, likeKeys.Contains(s.IsDouble ? -s.Key : s.Key)));
        }
        // Spells and T11 go after the ratings and before Requirements.
        int req = rows.FindIndex(r => r.Group == GroupRequirements);
        var tail = req >= 0 ? rows.GetRange(req, rows.Count - req) : new List<LootItemAttribute>();
        if (req >= 0) rows.RemoveRange(req, rows.Count - req);
        AddSpellRows(rows, f, like);
        AddT11Rows(rows, f, includeT11);
        rows.AddRange(tail);
        return rows;
    }

    /// <summary>
    /// The keys "items like this" required before the checklist (doubles negated), plus a
    /// pet essence's bond, level, potency, mastery and mutations.
    /// </summary>
    private static HashSet<int> LikeKeys(LootItemFacts f, bool pet)
    {
        var k = new HashSet<int> { LootItemRules.KeyMaterial, LootItemRules.KeyWorkmanship };
        int wieldSkill = f.Int(LootItemRules.KeyWieldSkill);
        switch ((AcObjectClass)f.ObjectClass)
        {
            case AcObjectClass.MeleeWeapon:
                k.Add(wieldSkill != 0 ? LootItemRules.KeyWieldSkill : LootItemRules.KeyWeaponSkill);
                k.Add(LootItemRules.KeyDamage);
                break;
            case AcObjectClass.MissileWeapon:
                k.Add(wieldSkill != 0 ? LootItemRules.KeyWieldSkill : LootItemRules.KeyWeaponSkill);
                break;
            case AcObjectClass.WandStaffOrb:
                k.Add(LootItemRules.KeyWieldSkill);
                k.Add(-LootItemRules.KeyElementalDamageMod);
                break;
            case AcObjectClass.Armor:
            case AcObjectClass.Clothing:
                k.Add(LootItemRules.KeyEquipSlots);
                k.Add(LootItemRules.KeyArmorLevel);
                break;
            case AcObjectClass.Jewelry:
                k.Add(LootItemRules.KeyEquipSlots);
                break;
        }
        if (pet)
            foreach (int key in new[] { KeyBond, KeyUseRequiresLevel, KeyUseLevelRequirement, KeyPotencyStored, KeySummoningMastery, KeyMutations })
                k.Add(key);
        return k;
    }

    private static LootItemAttribute PropertyRow(Spec s, double v, bool on)
    {
        LootItemAttributeOp[] ops = s.Cmp switch
        {
            Cmp.Named => new[] { LootItemAttributeOp.Exactly, LootItemAttributeOp.Not },
            Cmp.Max => new[] { LootItemAttributeOp.AtMost, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtLeast },
            _ => new[] { LootItemAttributeOp.AtLeast, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtMost },
        };
        string text = s.IsDouble ? Number(v) : NamedOrNumber(s.Key, (int)v);
        return new LootItemAttribute
        {
            Id = (s.IsDouble ? "D:" : "L:") + s.Key.ToString(CultureInfo.InvariantCulture),
            Group = s.Group, Label = s.Label, Tip = s.Tip,
            Kind = s.IsDouble ? LootItemAttribute.Shape.Double : LootItemAttribute.Shape.Long,
            Key = s.Key, ItemValue = v, ItemText = text, Ops = ops, Editable = s.Cmp != Cmp.Named,
            On = on, Op = ops[0], Value = v,
        };
    }

    private static void AddTotalRatings(List<LootItemAttribute> rows, LootItemFacts f, bool like)
    {
        int total = 0;
        foreach (int k in LootItemRules.RatingKeys) total += Math.Max(0, f.Int(k));
        if (total <= 0 || rows.Exists(r => r.Kind == LootItemAttribute.Shape.TotalRatings)) return;
        rows.Add(new LootItemAttribute
        {
            Id = "ratings", Group = GroupRatings, Label = "Total ratings", Kind = LootItemAttribute.Shape.TotalRatings,
            Tip = "Damage, damage resist, crit, crit resist, crit damage, crit damage resist, heal boost and vitality ratings added up.",
            ItemValue = total, ItemText = Number(total), Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true,
            On = like, Op = LootItemAttributeOp.AtLeast, Value = total,
        });
    }

    // ── Spells ──────────────────────────────────────────────────────────────

    /// <summary>"Major Strength": tier 1 (CantripTiers index) and "Strength".</summary>
    public static bool TryCantrip(string spell, out int tier, out string rest)
    {
        for (int i = 0; i < CantripTiers.Length; i++)
        {
            string p = CantripTiers[i] + " ";
            if (spell.StartsWith(p, StringComparison.OrdinalIgnoreCase) && spell.Length > p.Length)
            {
                tier = i;
                rest = spell.Substring(p.Length);
                return true;
            }
        }
        tier = -1;
        rest = string.Empty;
        return false;
    }

    /// <summary>"Strength Self VI": level 6 and "Strength Self".</summary>
    public static bool TryLevel(string spell, out int level, out string rest)
    {
        int sp = spell.LastIndexOf(' ');
        if (sp > 0)
        {
            int idx = Array.IndexOf(Roman, spell.Substring(sp + 1).ToUpperInvariant());
            if (idx >= 0)
            {
                level = idx + 1;
                rest = spell.Substring(0, sp);
                return true;
            }
        }
        level = 0;
        rest = string.Empty;
        return false;
    }

    /// <summary>The spell condition's pattern for a spell row's comparison.</summary>
    public static string SpellPattern(string spell, LootItemAttributeOp op)
    {
        if (op == LootItemAttributeOp.SpellOrBetter || op == LootItemAttributeOp.SpellAnyTier)
        {
            int from;
            if (TryCantrip(spell, out int tier, out string rest))
            {
                from = op == LootItemAttributeOp.SpellAnyTier ? 0 : tier;
                return "^(" + string.Join("|", CantripTiers, from, CantripTiers.Length - from) + ") " + LootItemRules.EscapeText(rest) + "$";
            }
            if (TryLevel(spell, out int level, out rest))
            {
                from = op == LootItemAttributeOp.SpellAnyTier ? 0 : level - 1;
                return "^" + LootItemRules.EscapeText(rest) + " (" + string.Join("|", Roman, from, Roman.Length - from) + ")$";
            }
        }
        return LootItemRules.ExactPattern(spell);
    }

    /// <summary>
    /// A row per spell (cantrips default to "or better", leveled spells to the exact spell, as
    /// "items like this" always did; the first <see cref="LootItemRules.MaxSpells"/> ticked
    /// for it), then "N cantrips of a tier or better" and the spell count.
    /// </summary>
    private static void AddSpellRows(List<LootItemAttribute> rows, LootItemFacts f, bool like)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tiers = new List<int>();
        int ticked = 0, count = 0;
        foreach (string raw in f.Spells)
        {
            string s = LootItemRules.OneLine(raw).Trim();
            if (s.Length == 0 || !seen.Add(s)) continue;
            count++;
            bool cantrip = TryCantrip(s, out int tier, out _);
            int level = 0;
            bool leveled = !cantrip && TryLevel(s, out level, out _);
            if (cantrip) tiers.Add(tier);
            LootItemAttributeOp[] ops = cantrip
                ? new[] { LootItemAttributeOp.SpellOrBetter, LootItemAttributeOp.SpellExact, LootItemAttributeOp.SpellAnyTier }
                : leveled
                    ? new[] { LootItemAttributeOp.SpellExact, LootItemAttributeOp.SpellOrBetter, LootItemAttributeOp.SpellAnyTier }
                    : new[] { LootItemAttributeOp.SpellExact };
            bool on = like && ticked < LootItemRules.MaxSpells;
            if (on) ticked++;
            rows.Add(new LootItemAttribute
            {
                Id = "spell:" + s, Group = GroupSpells, Label = s, Kind = LootItemAttribute.Shape.Spell, Spell = s,
                ItemText = cantrip ? CantripTiers[tier] + " cantrip" : leveled ? "level " + Roman[level - 1] : "spell",
                Tip = cantrip ? "Or better: this cantrip at this tier or higher. Any tier: Minor to Legendary."
                    : leveled ? "Or better: this spell at this level or higher. Any tier: any level."
                    : "The item has this spell.",
                Ops = ops, On = on, Op = ops[0],
            });
        }

        // "At least N Epic-or-better cantrips": one row per tier the item has, highest first.
        tiers.Sort();
        for (int t = CantripTiers.Length - 1; t >= 0; t--)
        {
            if (!tiers.Contains(t)) continue;
            int n = tiers.FindAll(x => x >= t).Count;
            string label = t == CantripTiers.Length - 1 ? CantripTiers[t] + " cantrips" : CantripTiers[t] + "-or-better cantrips";
            rows.Add(new LootItemAttribute
            {
                Id = "cantrips:" + CantripTiers[t], Group = GroupSpells, Label = label, Kind = LootItemAttribute.Shape.CantripTier,
                Key = t, ItemValue = n, ItemText = Number(n), Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true,
                Tip = "How many of the item's cantrips are this tier or higher.", Op = LootItemAttributeOp.AtLeast, Value = n,
            });
        }
        if (count > 0)
            rows.Add(new LootItemAttribute
            {
                Id = "spellcount", Group = GroupSpells, Label = "Spell count", Kind = LootItemAttribute.Shape.SpellCount,
                ItemValue = count, ItemText = Number(count), Ops = new[] { LootItemAttributeOp.AtLeast }, Editable = true,
                Op = LootItemAttributeOp.AtLeast, Value = count,
            });
    }

    /// <summary>How many spell rows there are (the "more than 4 spells" note).</summary>
    internal static int SpellRowCount(List<LootItemAttribute> rows) => rows.FindAll(r => r.Kind == LootItemAttribute.Shape.Spell).Count;

    /// <summary>True for the T11 group's rows.</summary>
    internal static bool IsT11Row(LootItemAttribute r) => r.Group == GroupT11;

    // ── T11 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The T11 rows on RynthAi's virtual T11 keys, in the order "Include T11 attributes" always
    /// added them (Is T11, tier, weapon grade, damage %, gear grade, each catalogued modifier,
    /// Has Slot Special for a value-less one, Cast on Strike count), then the summary keys,
    /// off by default. Left out on purpose: Can Wield (the character), Zone Locked (where it
    /// was appraised), property slots and Tainted (bag state, not quality).
    /// </summary>
    private static void AddT11Rows(List<LootItemAttribute> rows, LootItemFacts f, bool on)
    {
        T11ItemInfo? t = f.T11;
        if (t == null || !t.IsT11) return;
        void Row(string id, string label, int key, int v, bool named, bool def = true, bool equal = false, string tip = "")
        {
            LootItemAttributeOp[] ops = equal ? new[] { LootItemAttributeOp.Exactly }
                : new[] { LootItemAttributeOp.AtLeast, LootItemAttributeOp.Exactly, LootItemAttributeOp.AtMost };
            rows.Add(new LootItemAttribute
            {
                Id = "t11:" + id, Group = GroupT11, Label = label, Kind = LootItemAttribute.Shape.Long, Key = key, Tip = tip,
                ItemValue = v, ItemText = named ? NamedOrNumber(key, v) : equal ? "yes" : Number(v), Ops = ops,
                Editable = !named && !equal, On = on && def, Op = ops[0], Value = v,
            });
        }

        Row("is", "Is T11", T11Keys.IsT11, 1, named: false, equal: true);
        if (!t.HasText) return;
        if (t.Tier > 0) Row("tier", t.TierIsExact ? "Tier" : "Tier (estimated)", T11Keys.EstimatedTier, t.Tier, named: false);
        if (t.GradeRank > 0) Row("grade", "Weapon grade", T11Keys.WeaponGrade, t.GradeRank, named: true);
        if (t.DamagePercent > 0) Row("dmgpct", "Damage %", T11Keys.DamagePercent, t.DamagePercent, named: false);
        if (t.GearGradeRank > 0) Row("geargrade", "Gear grade", T11Keys.GearGrade, t.GearGradeRank, named: true);
        bool valuelessSpecial = false;
        foreach (T11Modifier m in t.Modifiers)
        {
            if (T11Catalog.ModifierByKey(m.Key) == null) continue;
            // A modifier's key reads 0 when it is absent, so a value-less one can't be required by value.
            if (m.Value > 0) Row("mod:" + m.Key.ToString(CultureInfo.InvariantCulture), "Mod: " + m.Name, T11Keys.ForModifier(m.Key), m.Value, named: false);
            else if (m.IsSlotSpecial) valuelessSpecial = true;
        }
        if (valuelessSpecial) Row("special", "Has slot special", T11Keys.HasSlotSpecial, 1, named: false, equal: true);
        if (t.Procs.Count > 0) Row("cos", "Cast on Strike count", T11Keys.CastOnStrikeCount, t.Procs.Count, named: false);

        foreach (var (key, label) in new[] { (T11Keys.ModifierCount, "Modifier count"), (T11Keys.ModifierRatingTotal, "Modifier rating total"), (T11Keys.BestRollPercent, "Best roll %") })
            if (T11Keys.TryGetValue(t, key, null, out int v) && v > 0)
                Row("sum:" + key.ToString(CultureInfo.InvariantCulture), label, key, v, named: false, def: false);
    }

    /// <summary>The catalogued-but-unknown T11 modifiers (the rule can't require them).</summary>
    internal static List<string> UnknownT11Modifiers(LootItemFacts f)
    {
        var unknown = new List<string>();
        if (f.T11 is { IsT11: true } t)
            foreach (T11Modifier m in t.Modifiers)
                if (T11Catalog.ModifierByKey(m.Key) == null) unknown.Add(m.Name);
        return unknown;
    }

    // ── Choices and conditions ──────────────────────────────────────────────

    /// <summary>
    /// The player's choices onto the rows (unknown ids are ignored: the item may have changed).
    /// A typed value that isn't a number keeps the item's and says so in <paramref name="notes"/>.
    /// </summary>
    public static void Apply(List<LootItemAttribute> rows, IReadOnlyList<LootEditItemChoice>? choices, List<string> notes)
    {
        if (choices == null) return;
        foreach (LootEditItemChoice c in choices)
        {
            LootItemAttribute? r = rows.Find(x => x.Id == c.Id);
            if (r == null) continue;
            r.On = c.On;
            if (c.Op >= 0 && Array.IndexOf(r.Ops, (LootItemAttributeOp)c.Op) >= 0) r.Op = (LootItemAttributeOp)c.Op;
            string typed = (c.Value ?? string.Empty).Trim();
            if (!r.Editable || typed.Length == 0) continue;
            bool whole = r.Kind != LootItemAttribute.Shape.Double;
            if (double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 && (!whole || v == Math.Floor(v)))
            {
                r.Value = v;
                r.Typed = typed;
            }
            else
                notes.Add($"{r.Label}: '{typed}' isn't a{(whole ? " whole" : "")} number of 0 or more; the item's {r.ItemText} is used.");
        }
    }

    /// <summary>The ticked rows' conditions onto <paramref name="rule"/>, in row order; how many rows added one.</summary>
    public static int AddConditions(VTankLootRule rule, List<LootItemAttribute> rows)
    {
        int n = 0;
        foreach (LootItemAttribute r in rows)
        {
            if (!r.On) continue;
            n++;
            string key = r.Key.ToString(CultureInfo.InvariantCulture);
            switch (r.Kind)
            {
                case LootItemAttribute.Shape.Long:
                    string iv = ((long)r.Value).ToString(CultureInfo.InvariantCulture);
                    int type = r.Op switch
                    {
                        LootItemAttributeOp.Exactly => VTankNodeTypes.LongValKeyE,
                        LootItemAttributeOp.AtMost => VTankNodeTypes.LongValKeyLE,
                        LootItemAttributeOp.Not => VTankNodeTypes.LongValKeyNE,
                        _ => VTankNodeTypes.LongValKeyGE,
                    };
                    rule.Conditions.Add(Cond(type, iv, key));
                    break;
                case LootItemAttribute.Shape.Double:
                    string dv = Number(r.Value);
                    if (r.Op != LootItemAttributeOp.AtMost) rule.Conditions.Add(Cond(VTankNodeTypes.DoubleValKeyGE, dv, key));
                    if (r.Op != LootItemAttributeOp.AtLeast) rule.Conditions.Add(Cond(VTankNodeTypes.DoubleValKeyLE, dv, key));
                    break;
                case LootItemAttribute.Shape.TotalRatings:
                    rule.Conditions.Add(Cond(VTankNodeTypes.TotalRatingsGE, Number(r.Value)));
                    break;
                case LootItemAttribute.Shape.Spell:
                    rule.Conditions.Add(Cond(VTankNodeTypes.SpellNameMatch, SpellPattern(r.Spell, r.Op)));
                    break;
                case LootItemAttribute.Shape.CantripTier:
                    string tiers = string.Join("|", CantripTiers, r.Key, CantripTiers.Length - r.Key);
                    rule.Conditions.Add(Cond(VTankNodeTypes.SpellMatch, "^(" + tiers + ") ", string.Empty, Number(r.Value)));
                    break;
                case LootItemAttribute.Shape.SpellCount:
                    rule.Conditions.Add(Cond(VTankNodeTypes.SpellCountGE, Number(r.Value)));
                    break;
            }
        }
        return n;
    }

    private static VTankLootCondition Cond(int type, params string[] lines) => new(type, "0", lines);

    /// <summary>A number as rules write it: whole numbers plain, decimals to 4 places.</summary>
    public static string Number(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A long key's value by name when it has one ("Gold", "Light Weapons"), else the number.</summary>
    private static string NamedOrNumber(int key, int v)
        => LootRuleText.ValueName(key, v.ToString(CultureInfo.InvariantCulture)) ?? v.ToString(CultureInfo.InvariantCulture);
}
