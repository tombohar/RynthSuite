using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi;

public enum VTankLootAction
{
    Keep = 1,
    Salvage = 2,
    Sell = 3,
    Read = 4,
    KeepUpTo = 10,
}

public sealed class VTankLootRule
{
    public string Name { get; set; } = string.Empty;
    public VTankLootAction Action { get; set; }
    public int KeepCount { get; set; }
    public string RawInfoLine { get; set; } = string.Empty;
    // Data lines for all requirements in order, length codes and CustomExpression already stripped.
    // Each requirement's data follows in sequence: for StringValueMatch it's [pattern, key];
    // for LongValKey* it's [value, key]; etc. — matching VTank's Write order.
    public List<string> RawDataLines { get; set; } = new();

    public bool IsMatch(WorldObject? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(RawInfoLine))
            return false;

        try
        {
            string[] parts = RawInfoLine.Split(';');
            if (parts.Length < 2)
                return false;

            // No requirement nodes → unconditional match (e.g. "Loot Everything").
            if (parts.Length == 2)
                return true;

            Queue<string> data = new(RawDataLines);

            for (int i = 2; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
                    continue;

                bool result = type switch
                {
                    0    => MatchSpellNamePattern(data),            // SpellNameMatch
                    1    => MatchStringValue(item, data),           // StringValueMatch
                    2    => MatchLongLE(item, data),                // LongValKeyLE
                    3    => MatchLongGE(item, data),                // LongValKeyGE
                    4    => MatchDoubleLE(item, data),              // DoubleValKeyLE
                    5    => MatchDoubleGE(item, data),              // DoubleValKeyGE
                    6    => SkipDouble(data),                       // DamagePercentGE (VTank returns false; skip + false)
                    7    => MatchObjectClass(item, data),           // ObjectClass
                    8    => MatchSpellCountGE(data),                // SpellCountGE
                    9    => MatchSpellMatch(data),                  // SpellMatch
                    10   => MatchMinDamageGE(item, data),           // MinDamageGE
                    11   => MatchLongFlagExists(item, data),        // LongValKeyFlagExists
                    12   => MatchLongE(item, data),                 // LongValKeyE
                    13   => MatchLongNE(item, data),                // LongValKeyNE
                    14   => SkipColorRule(data, 5),                 // AnySimilarColor
                    15   => SkipColorRule(data, 6),                 // SimilarColorArmorType
                    16   => SkipColorRule(data, 6),                 // SlotSimilarColor
                    17   => SkipColorRule(data, 2),                 // SlotExactPalette
                    1000 => MatchCharacterSkillGE(data),            // CharacterSkillGE
                    1001 => MatchCharacterPackSlotsGE(data),        // CharacterMainPackEmptySlotsGE
                    1002 => SkipInt(data),                          // CharacterLevelGE (no char level API; optimistic)
                    1003 => SkipInt(data),                          // CharacterLevelLE (optimistic)
                    1004 => MatchCharacterBaseSkill(data),          // CharacterBaseSkill
                    2000 => MatchBuffedMedianDamageGE(item, data),  // BuffedMedianDamageGE
                    2001 => SkipDouble(data),                       // BuffedMissileDamageGE (no missile calc; skip)
                    2003 => SkipDoubleAndInt(data),                 // BuffedLongValKeyGE (no buff calc; skip)
                    2005 => SkipDoubleAndInt(data),                 // BuffedDoubleValKeyGE (no buff calc; skip)
                    2006 => SkipDouble(data),                       // CalcdBuffedTinkedDamageGE (skip)
                    2007 => MatchTotalRatingsGE(item, data),        // TotalRatingsGE
                    2008 => SkipThreeDoubles(data),                 // CalcedBuffedTinkedTargetMeleeGE (skip)
                    9999 => MatchDisabledRule(data),                // DisabledRule (false = rule disabled)
                    _    => throw new InvalidOperationException($"Unsupported VTank loot node type {type}."),
                };

                if (!result)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Requirement matchers ──────────────────────────────────────────────

    // Type 0: SpellNameMatch — reads: pattern
    // We have no item-spell-list API, so always return true (optimistic).
    private static bool MatchSpellNamePattern(Queue<string> data)
    {
        data.Dequeue(); // pattern
        return true;
    }

    // Type 1: StringValueMatch — reads: pattern, key
    private static bool MatchStringValue(WorldObject item, Queue<string> data)
    {
        string pattern = data.Dequeue();
        int key = ReadInt(data);
        string value = item.Values((StringValueKey)key, string.Empty);
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Type 2: LongValKeyLE — reads: value, key
    private static bool MatchLongLE(WorldObject item, Queue<string> data)
    {
        int threshold = ReadInt(data);
        int key = ReadInt(data);
        return item.Values((LongValueKey)key, 0) <= threshold;
    }

    // Type 3: LongValKeyGE — reads: value, key
    private static bool MatchLongGE(WorldObject item, Queue<string> data)
    {
        int threshold = ReadInt(data);
        int key = ReadInt(data);
        return item.Values((LongValueKey)key, 0) >= threshold;
    }

    // Type 4: DoubleValKeyLE — reads: value, key
    private static bool MatchDoubleLE(WorldObject item, Queue<string> data)
    {
        double threshold = ReadDouble(data);
        int key = ReadInt(data);
        return item.Values((DoubleValueKey)key, 0.0) <= threshold;
    }

    // Type 5: DoubleValKeyGE — reads: value, key
    private static bool MatchDoubleGE(WorldObject item, Queue<string> data)
    {
        double threshold = ReadDouble(data);
        int key = ReadInt(data);
        return item.Values((DoubleValueKey)key, 0.0) >= threshold;
    }

    // Type 6: DamagePercentGE — reads: value. VTank's own implementation returns false; skip.
    private static bool SkipDouble(Queue<string> data)
    {
        data.Dequeue();
        return true; // optimistic skip
    }

    // Type 7: ObjectClass — reads: class value
    private static bool MatchObjectClass(WorldObject item, Queue<string> data)
    {
        int objectClass = ReadInt(data);
        return (int)item.ObjectClass == objectClass;
    }

    // Type 8: SpellCountGE — reads: count. No item-spell-list API; optimistic.
    private static bool MatchSpellCountGE(Queue<string> data)
    {
        data.Dequeue(); // count
        return true;
    }

    // Type 9: SpellMatch — reads: match_pattern, no_match_pattern, count. No item-spell API; optimistic.
    private static bool MatchSpellMatch(Queue<string> data)
    {
        data.Dequeue(); // rxDoesMatch
        data.Dequeue(); // rxDoesNotMatch
        data.Dequeue(); // count
        return true;
    }

    // Type 10: MinDamageGE — reads: value.
    // MinDamage = MaxDamage - (Variance * MaxDamage). STypeInt.MaxDamage=54, STypeFloat.DamageVariance=22.
    private static bool MatchMinDamageGE(WorldObject item, Queue<string> data)
    {
        double threshold = ReadDouble(data);
        int maxDamage = item.Values(54, 0);
        if (maxDamage == 0) return false;
        double variance = item.Values(DoubleValueKey.DamageVariance, 0.0);
        double minDamage = maxDamage - (variance * maxDamage);
        return minDamage >= threshold;
    }

    // Type 11: LongValKeyFlagExists — reads: flagValue, key. Match if (item[key] & flag) != 0.
    private static bool MatchLongFlagExists(WorldObject item, Queue<string> data)
    {
        int flagValue = ReadInt(data);
        int key = ReadInt(data);
        return (item.Values((LongValueKey)key, 0) & flagValue) != 0;
    }

    // Type 12: LongValKeyE — reads: value, key
    private static bool MatchLongE(WorldObject item, Queue<string> data)
    {
        int threshold = ReadInt(data);
        int key = ReadInt(data);
        return item.Values((LongValueKey)key, 0) == threshold;
    }

    // Type 13: LongValKeyNE — reads: value, key
    private static bool MatchLongNE(WorldObject item, Queue<string> data)
    {
        int threshold = ReadInt(data);
        int key = ReadInt(data);
        return item.Values((LongValueKey)key, 0) != threshold;
    }

    // Types 14-17: color palette rules — reads N lines. No palette API; optimistic.
    private static bool SkipColorRule(Queue<string> data, int lineCount)
    {
        for (int i = 0; i < lineCount; i++)
            if (data.Count > 0) data.Dequeue();
        return true;
    }

    // Type 1000: CharacterSkillGE — reads: value, skillId. No character context; optimistic.
    private static bool MatchCharacterSkillGE(Queue<string> data)
    {
        data.Dequeue(); // value
        data.Dequeue(); // skillId
        return true;
    }

    // Type 1001: CharacterMainPackEmptySlotsGE — reads: count. No character context; optimistic.
    private static bool MatchCharacterPackSlotsGE(Queue<string> data)
    {
        data.Dequeue();
        return true;
    }

    // Helper for CharacterLevelGE/LE (types 1002/1003): reads 1 int, returns true (optimistic).
    private static bool SkipInt(Queue<string> data)
    {
        if (data.Count > 0) data.Dequeue();
        return true;
    }

    // Type 1004: CharacterBaseSkill — reads: skillId, min, max. No character context; optimistic.
    private static bool MatchCharacterBaseSkill(Queue<string> data)
    {
        data.Dequeue(); // skillId
        data.Dequeue(); // min
        data.Dequeue(); // max
        return true;
    }

    // Type 2000: BuffedMedianDamageGE — reads: value. Uses unbuffed values as approximation.
    // Checks (minDamage + maxDamage) / 2 >= threshold using raw (un-buffed) item properties.
    private static bool MatchBuffedMedianDamageGE(WorldObject item, Queue<string> data)
    {
        double threshold = ReadDouble(data);
        int maxDamage = item.Values(54, 0);
        if (maxDamage == 0) return false;
        double variance = item.Values(DoubleValueKey.DamageVariance, 0.0);
        double minDamage = maxDamage - (variance * maxDamage);
        return (minDamage + maxDamage) / 2.0 >= threshold;
    }

    // Helper: skip 1 double + 1 int (types 2003, 2005)
    private static bool SkipDoubleAndInt(Queue<string> data)
    {
        if (data.Count > 0) data.Dequeue(); // value
        if (data.Count > 0) data.Dequeue(); // key
        return true;
    }

    // Helper: skip 3 doubles (type 2008)
    private static bool SkipThreeDoubles(Queue<string> data)
    {
        for (int i = 0; i < 3; i++)
            if (data.Count > 0) data.Dequeue();
        return true;
    }

    // Type 2007: TotalRatingsGE — reads: value. Sums all 8 rating properties.
    private static bool MatchTotalRatingsGE(WorldObject item, Queue<string> data)
    {
        double threshold = ReadDouble(data);
        int total =
            item.Values(370, 0) + // DamRating
            item.Values(371, 0) + // DamResRating
            item.Values(372, 0) + // CritRating
            item.Values(373, 0) + // CritResistRating
            item.Values(374, 0) + // CritDamRating
            item.Values(375, 0) + // CritDamResistRating
            item.Values(376, 0) + // HealBoostRating
            item.Values(379, 0);  // VitalityRating
        return total >= threshold;
    }

    // Type 9999: DisabledRule — reads: "true"/"false".
    // b=true means the rule IS disabled → Match returns !true = false → rule never fires.
    private static bool MatchDisabledRule(Queue<string> data)
    {
        string raw = data.Count > 0 ? data.Dequeue() : "false";
        bool isDisabled = string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return !isDisabled;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static int ReadInt(Queue<string> data)
        => int.Parse(data.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ReadDouble(Queue<string> data)
        => double.Parse(data.Dequeue(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
}

public sealed class VTankLootProfile
{
    public List<VTankLootRule> Rules { get; set; } = new();
}

public static class VTankLootParser
{
    public static VTankLootProfile Load(string filePath)
    {
        VTankLootProfile profile = new();
        if (!File.Exists(filePath))
            return profile;

        string[] lines = File.ReadAllLines(filePath);
        if (lines.Length < 3)
            throw new InvalidOperationException("Invalid UTL file: too short.");

        // Detect version. v0 files start with a rule count; v1+ start with "UTL".
        int fileVersion;
        int ruleCount;
        int lineIndex;

        if (string.Equals(lines[0].Trim(), "UTL", StringComparison.Ordinal))
        {
            if (!int.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fileVersion))
                throw new InvalidOperationException("Invalid UTL file: bad version.");
            if (!int.TryParse(lines[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ruleCount))
                throw new InvalidOperationException("Invalid UTL file: bad rule count.");
            lineIndex = 3;
        }
        else
        {
            // v0: first line is rule count, no "UTL" header.
            if (!int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ruleCount))
                throw new InvalidOperationException("Invalid UTL file: bad rule count.");
            fileVersion = 0;
            lineIndex = 1;
        }

        bool hasLengthCodes = fileVersion >= 1;
        bool hasCustomExpression = fileVersion >= 1;

        while (lineIndex < lines.Length && profile.Rules.Count < ruleCount)
        {
            VTankLootRule rule = new()
            {
                Name = lines[lineIndex++].TrimEnd(),
            };

            // v1+: skip the custom expression line (always present, may be blank).
            if (hasCustomExpression && lineIndex < lines.Length)
                lineIndex++;

            // Skip optional blank line separating name/expression from info line.
            if (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex]))
                lineIndex++;

            if (lineIndex >= lines.Length)
                break;

            rule.RawInfoLine = lines[lineIndex++].Trim();
            string[] parts = rule.RawInfoLine.Split(';');

            // parts[0] = priority, parts[1] = action, parts[2..] = requirement type codes.
            if (parts.Length >= 2
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int action))
            {
                rule.Action = (VTankLootAction)action;
            }

            // KeepUpTo stores its count on the line immediately after the info line.
            if (rule.Action == VTankLootAction.KeepUpTo && lineIndex < lines.Length)
            {
                if (int.TryParse(lines[lineIndex].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int keepCount))
                    rule.KeepCount = keepCount;
                lineIndex++;
            }

            // Read data for each requirement node.
            for (int i = 2; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeType))
                    continue;

                // v1+: skip the length-code line (character count prefix, not used for parsing).
                if (hasLengthCodes && lineIndex < lines.Length)
                    lineIndex++;

                int dataLines = GetDataLineCount(nodeType);
                if (dataLines < 0)
                {
                    // Unknown type — we can't determine arity so fall back to heuristic boundary detection.
                    while (lineIndex < lines.Length && !IsStartOfNextRule(lines, lineIndex, fileVersion))
                        rule.RawDataLines.Add(lines[lineIndex++].TrimEnd());
                    break;
                }

                for (int d = 0; d < dataLines && lineIndex < lines.Length; d++)
                    rule.RawDataLines.Add(lines[lineIndex++].TrimEnd());
            }

            profile.Rules.Add(rule);
        }

        return profile;
    }

    // Returns the number of data lines a given node type consumes (from VTank source Read methods).
    // Returns -1 for unknown types.
    private static int GetDataLineCount(int type) => type switch
    {
        0    => 1, // SpellNameMatch: pattern
        1    => 2, // StringValueMatch: pattern, key
        2    => 2, // LongValKeyLE: value, key
        3    => 2, // LongValKeyGE: value, key
        4    => 2, // DoubleValKeyLE: value, key
        5    => 2, // DoubleValKeyGE: value, key
        6    => 1, // DamagePercentGE: value
        7    => 1, // ObjectClass: class
        8    => 1, // SpellCountGE: count
        9    => 3, // SpellMatch: match_rx, no_match_rx, count
        10   => 1, // MinDamageGE: value
        11   => 2, // LongValKeyFlagExists: flagValue, key
        12   => 2, // LongValKeyE: value, key
        13   => 2, // LongValKeyNE: value, key
        14   => 5, // AnySimilarColor: r, g, b, maxDiffH, maxDiffSV
        15   => 6, // SimilarColorArmorType: r, g, b, maxDiffH, maxDiffSV, armorGroup
        16   => 6, // SlotSimilarColor: r, g, b, maxDiffH, maxDiffSV, slot
        17   => 2, // SlotExactPalette: slot, palette
        1000 => 2, // CharacterSkillGE: value, skillId
        1001 => 1, // CharacterMainPackEmptySlotsGE: count
        1002 => 1, // CharacterLevelGE: value
        1003 => 1, // CharacterLevelLE: value
        1004 => 3, // CharacterBaseSkill: skillId, min, max
        2000 => 1, // BuffedMedianDamageGE: value
        2001 => 1, // BuffedMissileDamageGE: value
        2003 => 2, // BuffedLongValKeyGE: value, key
        2005 => 2, // BuffedDoubleValKeyGE: value, key
        2006 => 1, // CalcdBuffedTinkedDamageGE: value
        2007 => 1, // TotalRatingsGE: value
        2008 => 3, // CalcedBuffedTinkedTargetMeleeGE: targetDoT, targetMeleeDef, targetAttack
        9999 => 1, // DisabledRule: "true"/"false"
        _    => -1,
    };

    // Heuristic boundary detection for unknown node types (fallback only).
    private static bool IsStartOfNextRule(string[] lines, int index, int fileVersion)
    {
        // Need at least: [name], [optional custom expr], [optional blank], [info line]
        int needed = fileVersion >= 1 ? 3 : 2;
        if (index + needed > lines.Length)
            return false;

        // In v1: [name], [custom expr], then info line (may skip blank after custom expr)
        // In v0: [name], [optional blank], then info line
        int infoOffset = fileVersion >= 1 ? 2 : 1;

        // Skip an optional blank line between name and info
        if (infoOffset < lines.Length - index && string.IsNullOrWhiteSpace(lines[index + infoOffset - 1]))
            infoOffset++;

        if (index + infoOffset >= lines.Length)
            return false;

        string infoLine = lines[index + infoOffset].Trim();
        if (!infoLine.Contains(';', StringComparison.Ordinal))
            return false;

        string[] parts = infoLine.Split(';');
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int act)
            && act is >= 1 and <= 10;
    }
}
