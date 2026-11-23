using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// The loot profile pickers' list and selection: the dashboard's Loaded files drawer (LOOT), the
/// Classic look's Loot tab, Settings > Looting (the engine's and RynthAi's own) and the Mini Remote's
/// Loot row all show this one list, built by LegacyDashboardRenderer.RefreshLootFiles (at load, and
/// within 3 s of the LootProfiles folder changing). Pure, apart from listing the folder.
/// </summary>
internal static class LootProfileList
{
    /// <summary>The first entry: no loot profile.</summary>
    public const string None = "None";

    /// <summary>
    /// "None", then the *.utl and *.json files in <paramref name="folder"/> (file names, sorted, any case).
    /// No such folder: "None" alone.
    /// </summary>
    public static List<string> Build(string? folder)
    {
        var files = new List<string>();
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            try
            {
                foreach (string f in Directory.GetFiles(folder, "*.utl")) files.Add(Path.GetFileName(f));
                foreach (string f in Directory.GetFiles(folder, "*.json")) files.Add(Path.GetFileName(f));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>(files.Count + 1) { None };
        list.AddRange(files);
        return list;
    }

    /// <summary>
    /// The entry the pickers select for <paramref name="currentPath"/> (the loot profile in use):
    /// 0 (None) when there is none; its file's entry when it is in <paramref name="folder"/>; else
    /// (a profile a meta loaded from elsewhere) <paramref name="previous"/>, kept in the list's range.
    /// </summary>
    public static int SelectedIndex(IReadOnlyList<string> list, string? folder, string? currentPath, int previous)
    {
        string path = (currentPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0 || list.Count == 0) return 0;
        string name = Path.GetFileName(path);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && SameFolder(dir, folder))
            for (int i = 1; i < list.Count; i++)
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return Math.Clamp(previous, 0, list.Count - 1);
    }

    /// <summary>The name a picker shows for the profile in use: its file name, or None.</summary>
    public static string ShownName(string? currentPath)
    {
        string path = (currentPath ?? string.Empty).Trim().Trim('"');
        return path.Length == 0 ? None : Path.GetFileName(path);
    }

    /// <summary>The index of <paramref name="fileName"/> in <paramref name="list"/> (any case), or -1.</summary>
    public static int IndexOf(IReadOnlyList<string> list, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return -1;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], fileName, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The chat line after a picker switched the loot profile (empty path: None).</summary>
    public static string SwitchedLine(string? newPath) =>
        string.IsNullOrWhiteSpace(newPath)
            ? "[RynthAi] Loot profile: None (no loot profile in use)."
            : $"[RynthAi] Loot profile: {Path.GetFileName(newPath.Trim())}.";

    private static bool SameFolder(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
