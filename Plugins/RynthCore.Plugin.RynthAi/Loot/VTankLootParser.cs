using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi;

public enum LootAction
{
    Keep = 0,
    Salvage = 1,
    Sell = 2,
    Read = 3,
    User1 = 4,
}

public sealed class LootRule
{
    public string Name { get; set; } = string.Empty;
    public LootAction Action { get; set; }
    public int KeepCount { get; set; }
    public string RawInfoLine { get; set; } = string.Empty;
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

            // Rules with only Action;KeepCount are unconditional.
            if (parts.Length == 2)
                return true;

            Queue<string> data = new(RawDataLines);
            bool evaluatedAnyCondition = false;

            for (int i = 2; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
                    continue;

                bool result = type switch
                {
                    1 => MatchStringValue(item, data),
                    2 => MatchLongLessEqual(item, data),
                    3 => MatchLongGreaterEqual(item, data),
                    4 => MatchDoubleLessEqual(item, data),
                    5 => MatchDoubleGreaterEqual(item, data),
                    7 => MatchObjectClass(item, data),
                    9998 => MatchEnabledFlag(data),
                    9999 => MatchEnabledFlag(data),
                    12 => MatchLongEqual(item, data),
                    13 => MatchLongNotEqual(item, data),
                    _ => throw new InvalidOperationException($"Unsupported VTank loot node type {type}."),
                };

                evaluatedAnyCondition = true;
                if (!result)
                    return false;
            }

            return evaluatedAnyCondition;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchStringValue(WorldObject item, Queue<string> data)
    {
        int key = ReadInt(data);
        string pattern = data.Dequeue();
        ConsumeStringMatchModeIfPresent(data);
        string value = item.Values((StringValueKey)key, string.Empty);
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool MatchLongLessEqual(WorldObject item, Queue<string> data)
    {
        int key = ReadInt(data);
        int value = ReadInt(data);
        return item.Values((LongValueKey)key, 0) <= value;
    }

    private static bool MatchLongGreaterEqual(WorldObject item, Queue<string> data)
    {
        int key = ReadInt(data);
        int value = ReadInt(data);
        return item.Values((LongValueKey)key, 0) >= value;
    }

    private static bool MatchDoubleLessEqual(WorldObject item, Queue<string> data)
    {
        int key = ReadInt(data);
        double value = ReadDouble(data);
        return item.Values((DoubleValueKey)key, 0.0) <= value;
    }

    private static bool MatchDoubleGreaterEqual(WorldObject item, Queue<string> data)
    {
        int key = ReadInt(data);
        double value = ReadDouble(data);
        return item.Values((DoubleValueKey)key, 0.0) >= value;
    }

    private static bool MatchObjectClass(WorldObject item, Queue<string> data)
    {
        int objectClass = ReadInt(data);
        if (objectClass == 3 && data.Count > 0 && int.TryParse(data.Peek(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacyObjectClass))
        {
            data.Dequeue();
            objectClass = legacyObjectClass;
        }

        return (int)item.ObjectClass == objectClass;
    }

    private static bool MatchLongEqual(WorldObject item, Queue<string> data)
    {
        int key = ReadInt(data);
        int value = ReadInt(data);
        return item.Values((LongValueKey)key, 0) == value;
    }

    private static bool MatchLongNotEqual(WorldObject item, Queue<string> data)
    {
        int key = ReadInt(data);
        int value = ReadInt(data);
        return item.Values((LongValueKey)key, 0) != value;
    }

    private static int ReadInt(Queue<string> data)
        => int.Parse(data.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ReadDouble(Queue<string> data)
        => double.Parse(data.Dequeue(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

    private static void ConsumeStringMatchModeIfPresent(Queue<string> data)
    {
        if (data.Count == 0)
            return;

        string next = data.Peek();
        if (string.Equals(next, "0", StringComparison.Ordinal)
            || string.Equals(next, "1", StringComparison.Ordinal)
            || string.Equals(next, "2", StringComparison.Ordinal))
        {
            data.Dequeue();
        }
    }

    private static bool MatchEnabledFlag(Queue<string> data)
    {
        if (data.Count >= 2
            && int.TryParse(data.Peek(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int marker)
            && marker == 6)
        {
            data.Dequeue();
            string enabledRaw = data.Dequeue();
            return !bool.TryParse(enabledRaw, out bool enabled) || enabled;
        }

        return true;
    }
}

public sealed class VTankLootProfile
{
    public List<LootRule> Rules { get; set; } = new();
}

public static class VTankLootParser
{
    public static VTankLootProfile Load(string filePath)
    {
        VTankLootProfile profile = new();
        if (!File.Exists(filePath))
            return profile;

        string[] lines = File.ReadAllLines(filePath);
        if (lines.Length < 3 || !string.Equals(lines[0].Trim(), "UTL", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid UTL file format.");

        int ruleCount = int.Parse(lines[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        int lineIndex = 3;

        while (lineIndex < lines.Length && profile.Rules.Count < ruleCount)
        {
            LootRule rule = new()
            {
                Name = lines[lineIndex++].TrimEnd(),
            };

            if (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex]))
                lineIndex++;

            if (lineIndex < lines.Length)
            {
                rule.RawInfoLine = lines[lineIndex++].Trim();
                string[] parts = rule.RawInfoLine.Split(';');
                if (parts.Length >= 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int action)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int keepCount))
                {
                    rule.Action = (LootAction)action;
                    rule.KeepCount = keepCount;
                }
            }

            while (lineIndex < lines.Length && !IsStartOfNextRule(lines, lineIndex))
                rule.RawDataLines.Add(lines[lineIndex++].TrimEnd());

            profile.Rules.Add(rule);
        }

        return profile;
    }

    private static bool IsStartOfNextRule(string[] lines, int index)
    {
        if (index + 2 >= lines.Length)
            return false;

        if (!string.IsNullOrWhiteSpace(lines[index + 1]))
            return false;

        string infoLine = lines[index + 2].Trim();
        if (!infoLine.Contains(';', StringComparison.Ordinal))
            return false;

        string[] parts = infoLine.Split(';');
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int action)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && action is >= 0 and <= 10;
    }
}
