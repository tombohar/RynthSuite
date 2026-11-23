using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Loot.VTank;

/// <summary>
/// Serializes a VTankLootProfile back to ".utl" text in VTank's exact format.
/// Pairs with VTankLootParser — round-tripping a parsed profile produces the
/// same logical content (line endings normalised to LF).
/// </summary>
public static class VTankLootWriter
{
    public static void Save(VTankLootProfile profile, string filePath)
    {
        File.WriteAllText(filePath, Serialize(profile));
    }

    public static string Serialize(VTankLootProfile profile)
    {
        var sb = new StringBuilder();
        bool v1Plus = profile.FileVersion >= 1;

        if (v1Plus)
        {
            sb.Append("UTL\n");
            sb.Append(profile.FileVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        sb.Append(profile.Rules.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var rule in profile.Rules)
        {
            sb.Append(rule.Name).Append('\n');

            if (v1Plus)
                sb.Append(rule.CustomExpression ?? string.Empty).Append('\n');

            sb.Append(BuildInfoLine(rule)).Append('\n');

            if (rule.Action == VTankLootAction.KeepUpTo)
                sb.Append((rule.KeepCount ?? 0).ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (var cond in rule.Conditions)
            {
                if (v1Plus)
                    sb.Append(cond.LengthCode ?? "0").Append('\n');
                foreach (var line in cond.DataLines)
                    sb.Append(line ?? string.Empty).Append('\n');
            }
        }

        if (profile.SalvageCombine != null)
        {
            sb.Append("SalvageCombine\n");
            sb.Append(profile.SalvageCombine.RawVersion ?? "0").Append('\n'); // VTank version/checksum
            sb.Append(profile.SalvageCombine.Enabled ? "1\n" : "0\n");        // enabled flag
            sb.Append(profile.SalvageCombine.DefaultBands ?? string.Empty).Append('\n');
            sb.Append(profile.SalvageCombine.PerMaterial.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var kv in profile.SalvageCombine.PerMaterial)
            {
                sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(kv.Value ?? string.Empty).Append('\n');
            }
            sb.Append("0\n"); // terminator
        }

        // Last: VTank skips a block it doesn't know by its length (LootPerDay).
        AppendPerDayBlock(sb, profile);

        return sb.ToString();
    }

    /// <summary>The RynthKeepPerDay block (LootPerDay), when any Keep # rule has the option on.</summary>
    private static void AppendPerDayBlock(StringBuilder sb, VTankLootProfile profile)
    {
        var data = new List<string>();
        for (int i = 0; i < profile.Rules.Count; i++)
        {
            VTankLootRule r = profile.Rules[i];
            int hours = Loot.LootPerDay.Clamp(r.PerDayHours);
            if (r.Action != VTankLootAction.KeepUpTo || hours == 0) continue;
            data.Add(i.ToString(CultureInfo.InvariantCulture));
            data.Add(r.Name ?? string.Empty);
            data.Add(hours.ToString(CultureInfo.InvariantCulture));
        }
        if (data.Count == 0) return;
        data.Insert(0, (data.Count / 3).ToString(CultureInfo.InvariantCulture));
        data.Insert(0, Loot.LootPerDay.UtlBlockFormat);

        // VTank reads the block's length in characters, each line ending in CRLF as its own
        // writer counts them. A file saved with LF is shorter than that; as the last block, the
        // skip then stops at the end of the file, which VTank's reader allows.
        int length = 0;
        foreach (string line in data) length += line.Length + 2;
        sb.Append(Loot.LootPerDay.UtlBlockName).Append('\n');
        sb.Append(length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (string line in data) sb.Append(line).Append('\n');
    }

    private static string BuildInfoLine(VTankLootRule rule)
    {
        var sb = new StringBuilder();
        sb.Append(rule.Priority.ToString(CultureInfo.InvariantCulture));
        sb.Append(';');
        sb.Append(((int)rule.Action).ToString(CultureInfo.InvariantCulture));
        foreach (var cond in rule.Conditions)
        {
            sb.Append(';');
            sb.Append(cond.NodeType.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
