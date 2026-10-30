using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Loot.VTank;

/// <summary>
/// Reads a VTank/LootSnob ".utl" file into a VTankLootProfile. Pairs with
/// VTankLootWriter for full round-trip — parse + write of an unmodified file
/// produces identical bytes (modulo line endings, which are normalised to LF).
/// </summary>
public static class VTankLootParser
{
    public static VTankLootProfile Load(string filePath)
    {
        if (!File.Exists(filePath)) return new VTankLootProfile();
        return LoadFromText(File.ReadAllText(filePath));
    }

    public static VTankLootProfile LoadFromText(string text)
    {
        var profile = new VTankLootProfile();

        // Split preserving every line as-is (no automatic newline conversion);
        // we trim trailing CR so this works on both LF and CRLF source.
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith("\r", StringComparison.Ordinal))
                lines[i] = lines[i].Substring(0, lines[i].Length - 1);

        if (lines.Length < 3)
            throw new InvalidOperationException("Invalid UTL file: too short.");

        int fileVersion;
        int ruleCount;
        int idx;

        if (string.Equals(lines[0].Trim(), "UTL", StringComparison.Ordinal))
        {
            if (!int.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fileVersion))
                throw new InvalidOperationException("Invalid UTL file: bad version.");
            if (!int.TryParse(lines[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ruleCount))
                throw new InvalidOperationException("Invalid UTL file: bad rule count.");
            idx = 3;
        }
        else
        {
            if (!int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ruleCount))
                throw new InvalidOperationException("Invalid UTL file: bad rule count.");
            fileVersion = 0;
            idx = 1;
        }
        profile.FileVersion = fileVersion;
        profile.DeclaredRuleCount = ruleCount;

        bool v1Plus = fileVersion >= 1;

        // Past the header's count, keep going while the next lines still look like a
        // rule: an undercounted header (T10locky.utl declared 134 of its 136 rules)
        // would otherwise drop the last rules silently. The SalvageCombine block never
        // matches, its third line having no ';'.
        while (idx < lines.Length
               && (profile.Rules.Count < ruleCount || IsStartOfNextRule(lines, idx, fileVersion)))
        {
            var rule = new VTankLootRule
            {
                Name = lines[idx++],
            };

            // v1+: custom expression line is ALWAYS present (may be blank).
            if (v1Plus && idx < lines.Length)
                rule.CustomExpression = lines[idx++];

            if (idx >= lines.Length) break;

            string infoLine = lines[idx++].Trim();
            string[] parts = infoLine.Split(';');

            if (parts.Length >= 1
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prio))
                rule.Priority = prio;

            if (parts.Length >= 2
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int actionInt))
                rule.Action = (VTankLootAction)actionInt;

            // KeepUpTo carries its count on the next line.
            if (rule.Action == VTankLootAction.KeepUpTo && idx < lines.Length)
            {
                if (int.TryParse(lines[idx].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kc))
                    rule.KeepCount = kc;
                idx++;
            }

            // Read each condition's data block.
            for (int i = 2; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeType))
                    continue;

                string lengthCode = "0";
                if (v1Plus && idx < lines.Length)
                    lengthCode = lines[idx++];

                int dataLineCount = VTankNodeTypes.GetDataLineCount(nodeType);
                var data = new List<string>();
                if (dataLineCount < 0)
                {
                    // Unknown type. The v1+ length code is the byte length of the data
                    // block (CRLF per line), so it bounds the block exactly; the old
                    // "slurp until something looks like a rule" fallback swallowed the
                    // rest of a multi-condition rule and the rules after it, and a
                    // trailing unknown node ate the SalvageCombine block.
                    if (!TryReadByLengthCode(lines, ref idx, lengthCode, data))
                    {
                        while (idx < lines.Length && !IsStartOfNextRule(lines, idx, fileVersion))
                            data.Add(lines[idx++]);
                    }
                }
                else
                {
                    for (int d = 0; d < dataLineCount && idx < lines.Length; d++)
                        data.Add(lines[idx++]);
                }
                rule.Conditions.Add(new VTankLootCondition(nodeType, lengthCode, data));
            }

            profile.Rules.Add(rule);
        }

        // Optional SalvageCombine block at the end.
        while (idx < lines.Length)
        {
            if (string.Equals(lines[idx].Trim(), "SalvageCombine", StringComparison.OrdinalIgnoreCase))
            {
                idx++;
                profile.SalvageCombine = ParseSalvageCombineBlock(lines, ref idx);
                break;
            }
            idx++;
        }

        return profile;
    }

    private static SalvageCombineSettings ParseSalvageCombineBlock(string[] lines, ref int idx)
    {
        var cfg = new SalvageCombineSettings { DefaultBands = string.Empty };
        if (idx >= lines.Length) return cfg;

        cfg.RawVersion = lines[idx].Trim();
        idx++;

        if (idx < lines.Length
            && int.TryParse(lines[idx].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int en))
        { cfg.Enabled = en != 0; idx++; }

        if (idx < lines.Length)
        { cfg.DefaultBands = lines[idx].Trim(); idx++; }

        if (idx < lines.Length
            && int.TryParse(lines[idx].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            idx++;
            for (int i = 0; i < n && idx + 1 < lines.Length; i++)
            {
                if (!int.TryParse(lines[idx].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int matId))
                    break;
                idx++;
                cfg.PerMaterial[matId] = lines[idx].Trim();
                idx++;
            }
        }

        return cfg;
    }

    /// <summary>
    /// Reads an unknown node's data lines using its length code: each line counts its
    /// length plus 2 for the CRLF the writer emits. Succeeds only when the lines add up
    /// to a positive code exactly (as characters for ANSI files, or UTF-8 bytes); otherwise
    /// <paramref name="idx"/> and <paramref name="data"/> are left untouched so the
    /// caller can fall back to the boundary heuristic.
    /// </summary>
    private static bool TryReadByLengthCode(string[] lines, ref int idx, string lengthCode, List<string> data)
    {
        // 0 is what VTankLootWriter emits for a condition built in code, so it says
        // nothing about the data's size.
        if (!int.TryParse(lengthCode.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int target)
            || target <= 0)
            return false;

        int charTotal = 0, byteTotal = 0, i = idx;
        // UTF-8 bytes >= chars, so the char total is the last one to reach the target.
        while (i < lines.Length && charTotal < target)
        {
            charTotal += lines[i].Length + 2;
            byteTotal += System.Text.Encoding.UTF8.GetByteCount(lines[i]) + 2;
            i++;
            if (charTotal == target || byteTotal == target)
            {
                for (int j = idx; j < i; j++)
                    data.Add(lines[j]);
                idx = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Heuristic boundary detection for unknown node types — true if the lines
    /// starting at `index` could plausibly be a new rule (name + custom-expr +
    /// info-line-with-semicolon).
    /// </summary>
    private static bool IsStartOfNextRule(string[] lines, int index, int fileVersion)
    {
        int infoOffset = fileVersion >= 1 ? 2 : 1;
        if (index + infoOffset >= lines.Length) return false;
        string infoLine = lines[index + infoOffset].Trim();
        if (!infoLine.Contains(';')) return false;
        string[] parts = infoLine.Split(';');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[0].Trim(), out _)
            && int.TryParse(parts[1].Trim(), out _);
    }
}
