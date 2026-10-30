using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

// "Add to loot profile" from a clicked item (2026-10-04): the rule built from
// one item, in the VTank model (the native JSON format gets a converted copy),
// its preview text, and where it goes in the list. Pure data, no AC types:
// RynthAi fills LootItemFacts from its object cache and does the matching.

/// <summary>What RynthAi knows about one item, as plain numbers (no AC or plugin types).</summary>
public sealed class LootItemFacts
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>AcObjectClass (VTank/Decal numbering).</summary>
    public int ObjectClass { get; set; }
    public int StackSize { get; set; } = 1;
    public int MaxStackSize { get; set; } = 1;
    /// <summary>Int properties (STypeInt ids, the keys loot rules use). Missing = 0 / unknown.</summary>
    public Dictionary<int, int> Ints { get; set; } = new();
    /// <summary>Float properties (STypeFloat ids).</summary>
    public Dictionary<int, double> Doubles { get; set; } = new();
    /// <summary>The item's spell names (empty when none or not identified).</summary>
    public List<string> Spells { get; set; } = new();
    /// <summary>
    /// The item's T11 reading (ACECustom tier 11+ text), or null when it wasn't asked for.
    /// <see cref="T11ItemInfo.None"/> for an item that isn't T11 gear.
    /// </summary>
    public T11ItemInfo? T11 { get; set; }
    /// <summary>The plugin knows it for a summoning essence (by WCID, icon, use requirement; added 2026-10-18).</summary>
    public bool IsPetEssence { get; set; }

    public bool Stackable => MaxStackSize > 1 || StackSize > 1;

    public int Int(int key) => Ints.TryGetValue(key, out int v) ? v : 0;
    public double Double(int key) => Doubles.TryGetValue(key, out double v) ? v : 0;
}

/// <summary>How the rule recognises the item.</summary>
public enum LootItemMatch
{
    /// <summary>Exact name and object class (the default).</summary>
    NameAndClass = 0,
    /// <summary>Exact name only.</summary>
    Name = 1,
    /// <summary>"Items like this": the class plus key properties (material, workmanship, stats, spells); no name.</summary>
    Like = 2,
}

public sealed class LootItemRuleOptions
{
    public LootItemMatch Match { get; set; } = LootItemMatch.NameAndClass;
    /// <summary>Null: the item's default action (Read for scrolls, Keep # for stacks, else Keep).</summary>
    public VTankLootAction? Action { get; set; }
    /// <summary>Keep # count; null: one full stack.</summary>
    public int? KeepCount { get; set; }
    /// <summary>Null or blank: "Keep Copper Pea" and the like.</summary>
    public string? RuleName { get; set; }
    /// <summary>
    /// Also require the item's T11 attributes (tier, weapon / gear grade, damage %, each modifier,
    /// slot special, Cast on Strike), each at least as good as this item's. Works with any match.
    /// </summary>
    public bool IncludeT11 { get; set; }
    /// <summary>
    /// The popup's attribute checklist choices (LootItemAttributes); null: every row at its
    /// default, which is the rule "items like this" and IncludeT11 always made.
    /// </summary>
    public IReadOnlyList<LootEditItemChoice>? Choices { get; set; }
}

/// <summary>A built rule, ready to preview and insert.</summary>
public sealed class LootItemRuleDraft
{
    public VTankLootRule Rule { get; init; } = new();
    public LootItemMatch Match { get; init; }
    public VTankLootAction Action { get; init; }
    public int KeepCount { get; init; }
    public string DefaultName { get; init; } = string.Empty;
    /// <summary>Things the player should know (a broad rule, spells left out...).</summary>
    public List<string> Notes { get; init; } = new();
    /// <summary>The item's checklist rows as the rule used them (the popup shows them).</summary>
    public List<LootItemAttribute> Attributes { get; init; } = new();
}

public static class LootItemRules
{
    // Property ids (STypeInt / STypeFloat, as RynthAi's evaluators read them).
    public const int KeyArmorLevel = 28, KeyEquipSlots = 9, KeyDamage = 44, KeyWeaponSkill = 48,
        KeyWorkmanship = 105, KeyMaterial = 131, KeyWieldSkill = 159;
    public const int KeyElementalDamageMod = 152;
    /// <summary>The ratings TotalRatingsGE adds up (same list as both evaluators).</summary>
    public static readonly int[] RatingKeys = { 370, 371, 372, 373, 374, 375, 376, 379 };
    /// <summary>The int properties a rule from an item can use: what RynthAi reads into LootItemFacts.</summary>
    public static int[] IntKeys => LootItemAttributes.IntKeys;
    public static int[] DoubleKeys => LootItemAttributes.DoubleKeys;

    /// <summary>Spell conditions a "like this" rule takes at most (all must match: more makes it narrower).</summary>
    public const int MaxSpells = 4;

    /// <summary>Read for spell scrolls, Keep # for anything that stacks, else Keep.</summary>
    public static VTankLootAction DefaultAction(LootItemFacts f)
    {
        if (f.ObjectClass == (int)AcObjectClass.Scroll) return VTankLootAction.Read;
        if (f.Stackable) return VTankLootAction.KeepUpTo;
        return VTankLootAction.Keep;
    }

    /// <summary>Keep # default: one full stack (the item's own stack when the maximum isn't known).</summary>
    public static int DefaultKeepCount(LootItemFacts f) => Math.Max(1, f.MaxStackSize > 1 ? f.MaxStackSize : f.StackSize);

    /// <summary>
    /// A name as an exact, case-insensitive regex: ^name$ with the regex
    /// metacharacters escaped (spaces left alone, unlike Regex.Escape, so it reads well).
    /// </summary>
    public static string ExactPattern(string name) => "^" + EscapeText(name) + "$";

    /// <summary>Text with the regex metacharacters escaped (spaces left alone), for a pattern's middle.</summary>
    public static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char ch in text)
        {
            if ("\\*+?|{}[]()^$.#".IndexOf(ch) >= 0) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string ClassName(int objectClass) =>
        Enum.IsDefined(typeof(AcObjectClass), objectClass) ? ((AcObjectClass)objectClass).ToString() : "class " + objectClass.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Keep Copper Pea", "Keep 100 Copper Pea", "Salvage like Iron Mace".</summary>
    public static string DefaultRuleName(string itemName, VTankLootAction action, int keepCount, LootItemMatch match)
    {
        string verb = action == VTankLootAction.KeepUpTo
            ? "Keep " + keepCount.ToString(CultureInfo.InvariantCulture)
            : LootRuleText.ActionName(action);
        return match == LootItemMatch.Like ? $"{verb} like {itemName}" : $"{verb} {itemName}";
    }

    /// <summary>Builds the rule. Null (and <paramref name="error"/>) when the item can't make one.</summary>
    public static LootItemRuleDraft? Build(LootItemFacts f, LootItemRuleOptions o, out string error)
    {
        error = string.Empty;
        string name = OneLine(f.Name).Trim();
        if (name.Length == 0 && o.Match != LootItemMatch.Like)
        {
            error = "That item has no name yet; try again in a moment.";
            return null;
        }
        VTankLootAction action = o.Action ?? DefaultAction(f);
        if (!LootRuleText.IsKnownAction((int)action))
        {
            error = $"Unknown action {(int)action}.";
            return null;
        }
        int keep = o.KeepCount ?? DefaultKeepCount(f);
        if (action == VTankLootAction.KeepUpTo && keep < 0)
        {
            error = "Keep # needs a count of 0 or more.";
            return null;
        }

        var notes = new List<string>();
        var rule = new VTankLootRule
        {
            CustomExpression = string.Empty,
            Action = action,
            KeepCount = action == VTankLootAction.KeepUpTo ? keep : null,
        };
        bool hasClass = f.ObjectClass != (int)AcObjectClass.Unknown;
        LootItemMatch match = o.Match;
        bool likeBare = false;
        if (match == LootItemMatch.NameAndClass && !hasClass)
        {
            match = LootItemMatch.Name;
            notes.Add("The item's class isn't known yet, so the rule matches the name only.");
        }

        switch (match)
        {
            case LootItemMatch.NameAndClass:
                rule.Conditions.Add(Cond(VTankNodeTypes.ObjectClass, Num(f.ObjectClass)));
                rule.Conditions.Add(Cond(VTankNodeTypes.StringValueMatch, ExactPattern(name), "1"));
                break;
            case LootItemMatch.Name:
                rule.Conditions.Add(Cond(VTankNodeTypes.StringValueMatch, ExactPattern(name), "1"));
                break;
            case LootItemMatch.Like:
                if (!hasClass)
                {
                    error = "The item's class isn't known yet; \"items like this\" needs it.";
                    return null;
                }
                rule.Conditions.Add(Cond(VTankNodeTypes.ObjectClass, Num(f.ObjectClass)));
                likeBare = true;
                break;
        }

        // The attribute checklist: defaults for this match, then the popup's choices.
        List<LootItemAttribute> rows = LootItemAttributes.For(f, match == LootItemMatch.Like, o.IncludeT11);
        LootItemAttributes.Apply(rows, o.Choices, notes);
        int added = LootItemAttributes.AddConditions(rule, rows);
        AddRowNotes(f, o, rows, match, notes);
        if (likeBare && added == 0)
            notes.Add($"Nothing but the class to go on: this rule matches every {ClassName(f.ObjectClass)}."
                + (f.Ints.Count == 0 ? " (Assess the item first for its material, workmanship and stats.)" : string.Empty));

        string defaultName = DefaultRuleName(name.Length > 0 ? name : ClassName(f.ObjectClass), action, keep, match);
        string ruleName = OneLine(o.RuleName).Trim();
        rule.Name = ruleName.Length > 0 ? ruleName : defaultName;
        return new LootItemRuleDraft
        {
            Rule = rule, Match = match, Action = action, KeepCount = keep, DefaultName = defaultName, Notes = notes, Attributes = rows,
        };
    }

    /// <summary>
    /// What the player should know about the rows: spells "like this" left unticked, a T11 item
    /// not identified yet, T11 modifiers RynthAi has no key for, T11 asked of a non-T11 item.
    /// </summary>
    private static void AddRowNotes(LootItemFacts f, LootItemRuleOptions o, List<LootItemAttribute> rows, LootItemMatch match, List<string> notes)
    {
        if (match == LootItemMatch.Like && o.Choices == null && LootItemAttributes.SpellRowCount(rows) > MaxSpells)
            notes.Add($"The item has more than {MaxSpells} spells; the rule checks the first {MaxSpells} (the Add to loot profile popup can tick the rest).");

        bool t11Asked = o.IncludeT11 || rows.Exists(r => r.On && LootItemAttributes.IsT11Row(r));
        if (!t11Asked) return;
        T11ItemInfo? t = f.T11;
        if (t == null || !t.IsT11)
        {
            notes.Add("Not a T11 item, so there are no T11 attributes to add.");
            return;
        }
        if (!t.HasText)
        {
            notes.Add("The item hasn't been identified, so only \"Is T11\" could be added. Assess it and preview again for its tier, grade and modifiers.");
            return;
        }
        List<string> unknown = LootItemAttributes.UnknownT11Modifiers(f);
        if (unknown.Count > 0)
            notes.Add("Left out (RynthAi has no loot key for these modifiers yet): " + string.Join(", ", unknown) + ".");
    }

    private static VTankLootCondition Cond(int type, params string[] lines) => new(type, "0", lines);

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    internal static string OneLine(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    // =====================================================================
    //  Native (JSON) profiles
    // =====================================================================

    /// <summary>
    /// The rule in the native format. Conditions it has no class for (spells)
    /// are left out and named in <paramref name="dropped"/>; null when the rule
    /// would end up with none of its conditions (it would match everything).
    /// </summary>
    public static LootRule? ToNative(VTankLootRule r, List<string> dropped)
    {
        var rule = new LootRule
        {
            Name = r.Name,
            Enabled = r.Enabled,
            Action = r.Action switch
            {
                VTankLootAction.Sell => LootAction.Sell,
                VTankLootAction.Salvage => LootAction.Salvage,
                VTankLootAction.Read => LootAction.Read,
                VTankLootAction.KeepUpTo => LootAction.KeepUpTo,
                _ => LootAction.Keep,
            },
            KeepCount = r.Action == VTankLootAction.KeepUpTo ? r.KeepCount ?? 0 : 1,
        };
        int wanted = 0;
        foreach (VTankLootCondition c in r.Conditions)
        {
            if (c.NodeType == VTankNodeTypes.DisabledRule) continue;
            wanted++;
            LootCondition? n = NativeCondition(c);
            if (n != null) rule.Conditions.Add(n);
            else dropped.Add(LootRuleText.Condition(c));
        }
        return wanted > 0 && rule.Conditions.Count == 0 ? null : rule;
    }

    private static LootCondition? NativeCondition(VTankLootCondition c)
    {
        IList<string> d = c.DataLines;
        int I(int i) => int.Parse(d[i], NumberStyles.Integer, CultureInfo.InvariantCulture);
        double D(int i) => double.Parse(d[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        try
        {
            return c.NodeType switch
            {
                VTankNodeTypes.ObjectClass => new ObjectClassCondition { ObjectClass = (AcObjectClass)I(0) },
                VTankNodeTypes.StringValueMatch => new StringValueCondition { Pattern = d[0], Key = I(1) },
                VTankNodeTypes.LongValKeyE => new LongValKeyECondition { Value = I(0), Key = I(1) },
                VTankNodeTypes.LongValKeyNE => new LongValKeyNECondition { Value = I(0), Key = I(1) },
                VTankNodeTypes.LongValKeyGE => new LongValKeyGECondition { Value = I(0), Key = I(1) },
                VTankNodeTypes.LongValKeyLE => new LongValKeyLECondition { Value = I(0), Key = I(1) },
                VTankNodeTypes.LongValKeyFlagExists => new LongValKeyFlagCondition { FlagValue = I(0), Key = I(1) },
                VTankNodeTypes.DoubleValKeyGE => new DoubleValKeyGECondition { Value = D(0), Key = I(1) },
                VTankNodeTypes.DoubleValKeyLE => new DoubleValKeyLECondition { Value = D(0), Key = I(1) },
                VTankNodeTypes.TotalRatingsGE => new TotalRatingsGECondition { Value = (int)Math.Ceiling(D(0)) },
                VTankNodeTypes.MinDamageGE => new MinDamageGECondition { Value = D(0) },
                _ => null,
            };
        }
        catch (FormatException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // =====================================================================
    //  Preview and placement
    // =====================================================================

    /// <summary>The rule as the popup shows it: name, action, one line per condition.</summary>
    public static List<string> PreviewLines(VTankLootRule r)
    {
        var lines = new List<string>
        {
            "Rule: " + r.Name,
            "Action: " + (r.Action == VTankLootAction.KeepUpTo
                ? "Keep # " + (r.KeepCount ?? 0).ToString(CultureInfo.InvariantCulture)
                : LootRuleText.ActionName(r.Action)),
        };
        bool first = true;
        foreach (VTankLootCondition c in r.Conditions)
        {
            if (c.NodeType == VTankNodeTypes.DisabledRule) continue;
            lines.Add((first ? "If: " : "and ") + LootRuleText.Condition(c));
            first = false;
        }
        if (first) lines.Add("If: (every item)");
        return lines;
    }

    /// <summary>
    /// Where the rule goes: just before the first rule that matches the item now
    /// (rules run top to bottom and the first match wins, so this is the latest
    /// spot where the new rule still decides the item, and it moves no other
    /// rule's items but this item's look-alikes); the end when nothing matches it.
    /// <paramref name="matches"/> says whether rule i matches the item.
    /// </summary>
    public static int ChooseIndex(int ruleCount, Func<int, bool> matches)
    {
        for (int i = 0; i < ruleCount; i++)
            if (matches(i)) return i;
        return ruleCount;
    }

    /// <summary>Same action (and count) and the same conditions in the same order, names aside.</summary>
    public static bool SameRule(VTankLootRule a, VTankLootRule b)
    {
        if (a.Action != b.Action || a.Enabled != b.Enabled) return false;
        if (a.Action == VTankLootAction.KeepUpTo && (a.KeepCount ?? 0) != (b.KeepCount ?? 0)) return false;
        var ca = Live(a);
        var cb = Live(b);
        if (ca.Count != cb.Count) return false;
        for (int i = 0; i < ca.Count; i++)
        {
            if (ca[i].NodeType != cb[i].NodeType || ca[i].DataLines.Count != cb[i].DataLines.Count) return false;
            for (int j = 0; j < ca[i].DataLines.Count; j++)
                if (!string.Equals(ca[i].DataLines[j].Trim(), cb[i].DataLines[j].Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;

        static List<VTankLootCondition> Live(VTankLootRule r) => r.Conditions.FindAll(c => c.NodeType != VTankNodeTypes.DisabledRule);
    }

    /// <summary>Native rules: same action (and count) and the same conditions, names aside.</summary>
    public static bool SameRule(LootRule a, LootRule b)
    {
        if (a.Action != b.Action || a.Enabled != b.Enabled) return false;
        if (a.Action == LootAction.KeepUpTo && a.KeepCount != b.KeepCount) return false;
        if (a.Conditions.Count != b.Conditions.Count) return false;
        for (int i = 0; i < a.Conditions.Count; i++)
            if (!string.Equals(a.Conditions[i].ToString(), b.Conditions[i].ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
