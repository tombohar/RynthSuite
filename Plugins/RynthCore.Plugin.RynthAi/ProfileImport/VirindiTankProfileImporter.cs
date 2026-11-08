using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.ProfileImport;

/// <summary>
/// Copies VirindiTank-style loot files (.utl) and merges Rynth-format monster JSON found under common VT install paths.
/// </summary>
public static class VirindiTankProfileImporter
{
    /// <summary>Standard VirindiTank install locations (wiki / community bundles).</summary>
    public static IReadOnlyList<string> DefaultVirindiTankRoots()
    {
        var list = new List<string>
        {
            @"C:\Games\VirindiPlugins\VirindiTank",
        };

        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs))
            {
                list.Add(Path.Combine(docs, "VirindiPlugins", "VirindiTank"));
                list.Add(Path.Combine(docs, "Decal", "VirindiPlugins", "VirindiTank"));
            }
        }
        catch { }

        try
        {
            string loc = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(loc))
                list.Add(Path.Combine(loc, "VirindiPlugins", "VirindiTank"));
        }
        catch { }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    public sealed class Result
    {
        public int LootFilesCopied;
        public int MonsterRulesAdded;
        /// <summary>Full paths of .utl files copied into the Rynth loot folder this run (for activating loot).</summary>
        public List<string> CopiedLootDestPaths { get; } = new();
        public List<string> LogLines { get; } = new();
        /// <summary>The ring options of each text .usd read (file name, values); see <see cref="ApplyRingSettings"/>.</summary>
        internal List<(string File, VtRingSettings Ring)> RingSettingsFound { get; } = new();
        /// <summary>"Min Ring Targets 2, Ring Range 5 (from the profile)" when they were set; else "".</summary>
        public string RingSettingsSet { get; internal set; } = string.Empty;
    }

    /// <param name="lootDestDir">RynthAi LootProfiles folder.</param>
    /// <param name="mergeMonsterRules">When true, deserialized rules are merged into <paramref name="settings"/>.</param>
    public static Result Import(string lootDestDir, LegacyUiSettings settings, bool mergeMonsterRules)
    {
        var result = new Result();
        if (string.IsNullOrEmpty(lootDestDir))
        {
            result.LogLines.Add("Loot destination folder is empty.");
            return result;
        }

        try { Directory.CreateDirectory(lootDestDir); }
        catch (Exception ex)
        {
            result.LogLines.Add($"Cannot create loot folder: {ex.Message}");
            return result;
        }

        var seenSrc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in DefaultVirindiTankRoots())
        {
            foreach (string utl in EnumerateFilesRecursive(root, "*.utl", maxDepth: 6))
            {
                if (!seenSrc.Add(utl)) continue;
                string baseName = Path.GetFileName(utl);
                string dest = Path.Combine(lootDestDir, baseName);
                int suffix = 0;
                while (File.Exists(dest))
                {
                    suffix++;
                    dest = Path.Combine(lootDestDir, $"VT_{suffix}_{baseName}");
                }

                try
                {
                    File.Copy(utl, dest, overwrite: false);
                    result.LootFilesCopied++;
                    result.CopiedLootDestPaths.Add(dest);
                    result.LogLines.Add($"Loot: {baseName} -> {Path.GetFileName(dest)}");
                }
                catch (Exception ex)
                {
                    result.LogLines.Add($"Loot skip {baseName}: {ex.Message}");
                }
            }

            if (mergeMonsterRules)
            {
                foreach (string jsonPath in EnumerateFilesRecursive(root, "monsters.json", maxDepth: 8))
                    TryMergeMonsterFile(jsonPath, settings, result);

                foreach (string usdPath in EnumerateFilesRecursive(root, "*.usd", maxDepth: 6))
                    TryMergeUsdMyMonstersScf(usdPath, settings, result);
            }
        }

        if (mergeMonsterRules)
            ApplyRingSettings(settings, result);

        if (result.LootFilesCopied == 0 && result.MonsterRulesAdded == 0)
            result.LogLines.Add("No .utl loot files, Rynth monsters.json, or text MyMonsters .usd found under VirindiTank paths.");

        return result;
    }

    /// <summary>SQLite-backed VT profiles are skipped here; use Monster Editor import for those.</summary>
    private static bool LooksLikeSqliteFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var hdr = new byte[16];
            int n = fs.Read(hdr, 0, hdr.Length);
            const string sig = "SQLite format 3\0";
            if (n < sig.Length)
                return false;
            for (int i = 0; i < sig.Length; i++)
            {
                if (hdr[i] != sig[i])
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Text SCF .usd with MyMonsters — same subset as Monster Editor import for the current character merge.</summary>
    internal static void TryMergeUsdMyMonstersScf(string usdPath, LegacyUiSettings settings, Result result)
    {
        if (string.IsNullOrWhiteSpace(usdPath) || !File.Exists(usdPath))
            return;
        if (LooksLikeSqliteFile(usdPath))
        {
            result.LogLines.Add($"Monsters: skip SQLite .usd (use Monster Editor): {Path.GetFileName(usdPath)}");
            return;
        }

        string allText;
        try
        {
            allText = File.ReadAllText(usdPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            result.LogLines.Add($"Monsters: skip .usd read {Path.GetFileName(usdPath)}: {ex.Message}");
            return;
        }

        var ring = VtRingSettings.Read(allText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
        if (ring.Any) result.RingSettingsFound.Add((Path.GetFileName(usdPath), ring));

        var tmp = new List<MonsterRule>();
        if (!VirindiTankUsdMyMonstersScfParser.TryParseMyMonsters(allText, tmp, out string? note) || tmp.Count == 0)
            return;

        int addedBefore = result.MonsterRulesAdded;
        MergeVtMonsterRows(tmp, settings, result);
        int newNamed = result.MonsterRulesAdded - addedBefore;
        result.LogLines.Add(
            $"Monsters: VT MyMonsters from {Path.GetFileName(usdPath)} — parsed {tmp.Count} row(s), +{newNamed} new rule(s).{(string.IsNullOrEmpty(note) ? "" : " " + note)}");
    }

    /// <summary>
    /// The ring options of the .usd profiles read (VtRingSettings) into Min Ring Targets and Ring
    /// Range: the import is the player's, so these two settings may change here and only here. This
    /// import reads every profile under the VirindiTank folders, so it sets them only when every
    /// profile that has ring options agrees; profiles that differ leave both alone and say so.
    /// None found: the settings are left as they are.
    /// </summary>
    internal static void ApplyRingSettings(LegacyUiSettings settings, Result result)
    {
        var found = result.RingSettingsFound;
        if (found.Count == 0) return;
        var first = found[0].Ring;
        foreach (var (file, ring) in found)
        {
            if (ring.SameAs(first)) continue;
            result.LogLines.Add($"Ring settings: the profiles differ ({found[0].File}: {first.Describe()}; {file}: {ring.Describe()}): " +
                                "Min Ring Targets and Ring Range left alone. Import one profile from the Hub's VTank import to take its values.");
            return;
        }
        foreach (string c in first.Clamped) result.LogLines.Add($"Ring settings: {c} (the settings' range)");
        first.ApplyTo(settings);
        result.RingSettingsSet = first.Describe();
        result.LogLines.Add($"Ring settings: set {result.RingSettingsSet}" +
                            (found.Count > 1 ? $", the same in {found.Count} profiles." : $", {found[0].File}."));
    }

    /// <summary>Merges VT-style rows into <paramref name="settings"/> like Monster Editor external import.</summary>
    private static void MergeVtMonsterRows(IReadOnlyList<MonsterRule> src, LegacyUiSettings settings, Result result)
    {
        settings.EnsureDefaultRule();
        List<MonsterRule> rules = settings.MonsterRules;

        MonsterRule? srcDefault = src.FirstOrDefault(x => x.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        if (srcDefault != null && rules.Count > 0 && rules[0].Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            CopyCombatOntoDefault(rules[0], srcDefault);

        var nameSet = new HashSet<string>(rules.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var exprSet = new HashSet<string>(
            rules.Where(x => !string.IsNullOrWhiteSpace(x.MatchExpression)).Select(x => x.MatchExpression),
            StringComparer.Ordinal);

        foreach (MonsterRule row in src)
        {
            if (row.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(row.Name))
            {
                if (nameSet.Contains(row.Name))
                    continue;
            }
            else if (!string.IsNullOrEmpty(row.MatchExpression))
            {
                if (exprSet.Contains(row.MatchExpression))
                    continue;
            }
            else
            {
                continue;
            }

            var c = CloneMonsterRule(row);
            if (!string.IsNullOrEmpty(c.Name))
            {
                if (!nameSet.Add(c.Name))
                    continue;
            }

            if (!string.IsNullOrEmpty(c.MatchExpression))
                exprSet.Add(c.MatchExpression);

            rules.Add(c);
            result.MonsterRulesAdded++;
        }
    }

    private static void CopyCombatOntoDefault(MonsterRule dst, MonsterRule src)
    {
        dst.Category = src.Category;
        dst.MatchExpression = src.MatchExpression;
        dst.Priority = src.Priority;
        dst.DamageType = src.DamageType;
        dst.WeaponId = src.WeaponId;
        dst.Fester = src.Fester;
        dst.Broadside = src.Broadside;
        dst.GravityWell = src.GravityWell;
        dst.Imperil = src.Imperil;
        dst.Yield = src.Yield;
        dst.Vuln = src.Vuln;
        dst.Corrosion = src.Corrosion;
        dst.Corruption = src.Corruption;
        dst.DestructiveCurse = src.DestructiveCurse;
        dst.WeakeningCurse = src.WeakeningCurse;
        dst.FesteringCurse = src.FesteringCurse;
        dst.UseArc = src.UseArc;
        dst.UseBolt = src.UseBolt;
        dst.UseRing = src.UseRing;
        dst.UseStreak = src.UseStreak;
        dst.ExVuln = src.ExVuln;
        dst.OffhandId = src.OffhandId;
        dst.PreferredAmmoItemId = src.PreferredAmmoItemId;
        dst.PetDamage = src.PetDamage;
        dst.AttackPower = src.AttackPower;
        dst.Kite = src.Kite;
    }

    private static MonsterRule CloneMonsterRule(MonsterRule row) =>
        new()
        {
            Name = row.Name,
            Category = row.Category,
            MatchExpression = row.MatchExpression,
            Priority = row.Priority,
            DamageType = row.DamageType,
            WeaponId = row.WeaponId,
            Fester = row.Fester,
            Broadside = row.Broadside,
            GravityWell = row.GravityWell,
            Imperil = row.Imperil,
            Yield = row.Yield,
            Vuln = row.Vuln,
            Corrosion = row.Corrosion,
            Corruption = row.Corruption,
            DestructiveCurse = row.DestructiveCurse,
            WeakeningCurse = row.WeakeningCurse,
            FesteringCurse = row.FesteringCurse,
            UseArc = row.UseArc,
            UseBolt = row.UseBolt,
            UseRing = row.UseRing,
            UseStreak = row.UseStreak,
            ExVuln = row.ExVuln,
            OffhandId = row.OffhandId,
            PreferredAmmoItemId = row.PreferredAmmoItemId,
            PetDamage = row.PetDamage,
            AttackPower = row.AttackPower,
            Kite = row.Kite,
            PreferredDamage = row.PreferredDamage,
        };

    private static void TryMergeMonsterFile(string jsonPath, LegacyUiSettings settings, Result result)
    {
        try
        {
            string json = File.ReadAllText(jsonPath);
            var imported = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonsterRuleList);
            if (imported == null || imported.Count == 0) return;

            int added = 0;
            foreach (var rule in imported)
            {
                if (string.IsNullOrWhiteSpace(rule.Name)) continue;
                if (rule.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                if (settings.MonsterRules.Any(m => m.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                settings.MonsterRules.Add(rule);
                added++;
            }

            if (added > 0)
            {
                result.MonsterRulesAdded += added;
                result.LogLines.Add($"Monsters: merged {added} rule(s) from {Path.GetFileName(jsonPath)}");
            }
        }
        catch (Exception ex)
        {
            result.LogLines.Add($"Monsters skip {jsonPath}: {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateFilesRecursive(string root, string pattern, int maxDepth)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch { continue; }

            foreach (string f in files)
                yield return f;

            if (depth >= maxDepth) continue;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (string sub in subdirs)
                stack.Push((sub, depth + 1));
        }
    }
}
