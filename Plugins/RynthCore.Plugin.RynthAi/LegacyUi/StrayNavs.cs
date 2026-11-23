using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>A VirindiTank-format nav outside NavProfiles, where the nav picker can't see it.</summary>
internal readonly record struct StrayNav(string Path, string Folder, string Name);

/// <summary>What copying the stray navs did.</summary>
internal readonly record struct StrayCopyResult(int Copied, int AlreadyThere, int Failed, string FirstError);

/// <summary>
/// Navs put in the wrong folder (2026-11-22). Players moving VirindiTank navs over put them in
/// "the nav folder", and the one called that is RynthNav's map data folder (C:\Games\RynthCore\
/// NavData); others land in the RynthAi or RynthSuite folder itself. RynthAi lists NavProfiles
/// (and its subfolders) only, so they never showed and nothing said why. Here: the folders worth a
/// look, the VirindiTank navs at the top of each that NavProfiles has no file of that name for, a
/// line that says where they are and where they go, and the copy into NavProfiles (never moved,
/// never over a file; the originals stay). System and System.Text.Json only (the tests compile it in).
/// </summary>
internal static class StrayNavs
{
    /// <summary>RynthNav's own default for its map data folder (NavDataConfig.DefaultDir).</summary>
    public const string DefaultNavDataDir = @"C:\Games\RynthCore\NavData";

    /// <summary>
    /// The folders to look in: RynthNav's map data folder (as RynthNav resolves it: the
    /// RYNTHNAV_NAVDATA variable, "navDataDir" in %APPDATA%\RynthCore\rynthnav.json, else
    /// &lt;CoreDir&gt;\NavData and the default), the RynthCore folder, the RynthSuite folder, RynthAi's
    /// own folder and RynthNav's plugin folder. Never NavProfiles. Duplicates dropped.
    /// </summary>
    public static List<string> CandidateFolders(string coreDir, string suiteDir, string rynthAiDir,
        string? navDataEnv, string? rynthNavSettingsPath)
    {
        var list = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !System.IO.Path.IsPathFullyQualified(dir)) return;
            string full;
            try { full = System.IO.Path.GetFullPath(dir).TrimEnd('\\', '/'); } catch { return; }
            foreach (string have in list) if (have.Equals(full, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(full);
        }
        Add(navDataEnv);
        try
        {
            if (!string.IsNullOrEmpty(rynthNavSettingsPath) && File.Exists(rynthNavSettingsPath))
                Add(ReadNavDataDir(File.ReadAllText(rynthNavSettingsPath)));
        }
        catch { }
        if (!string.IsNullOrEmpty(coreDir)) Add(System.IO.Path.Combine(coreDir, "NavData"));
        Add(DefaultNavDataDir);
        Add(coreDir);
        Add(suiteDir);
        Add(rynthAiDir);
        if (!string.IsNullOrEmpty(suiteDir)) Add(System.IO.Path.Combine(suiteDir, "RynthNav"));
        return list;
    }

    /// <summary>"navDataDir" from rynthnav.json's text (the same key RynthNav reads), or null.</summary>
    internal static string? ReadNavDataDir(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("navDataDir", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// The VirindiTank-format navs (first line "uTank2 NAV") at the top of each of
    /// <paramref name="folders"/> that <paramref name="navFolder"/> has no file of that name for.
    /// VirindiTank's "--" state files are left out. A folder inside NavProfiles is skipped (it's listed).
    /// </summary>
    public static List<StrayNav> Find(IEnumerable<string> folders, string navFolder)
    {
        var found = new List<StrayNav>();
        string navFull = Full(navFolder);
        foreach (string folder in folders)
        {
            string f = Full(folder);
            if (f.Length == 0 || IsSameOrInside(f, navFull)) continue;
            string[] files;
            try { if (!Directory.Exists(f)) continue; files = Directory.GetFiles(f, "*.nav"); }
            catch { continue; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (!file.EndsWith(".nav", StringComparison.OrdinalIgnoreCase) || NavFileList.IsVTankStateFile(file)) continue;
                if (!IsVTankNav(file)) continue;
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (navFull.Length > 0 && File.Exists(System.IO.Path.Combine(navFull, name + ".nav"))) continue;
                found.Add(new StrayNav(file, f, name));
            }
        }
        return found;
    }

    /// <summary>True when the file's first line is a VirindiTank nav header.</summary>
    internal static bool IsVTankNav(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            string? first = reader.ReadLine();
            return first != null && first.Contains("uTank2 NAV", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Copies <paramref name="strays"/> into the top of <paramref name="navFolder"/>, where the picker
    /// lists them. A name NavProfiles already has is left alone (counted, not copied); the
    /// originals stay where they were. <paramref name="copy"/> does the write (DataWriteGuard in
    /// the plugin, File.Copy in the tests).
    /// </summary>
    public static StrayCopyResult CopyInto(IReadOnlyList<StrayNav> strays, string navFolder, Func<string, string, string?>? copy = null)
    {
        copy ??= DefaultCopy;
        int copied = 0, there = 0, failed = 0;
        string firstError = string.Empty;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { Directory.CreateDirectory(navFolder); } catch { }
        foreach (StrayNav s in strays)
        {
            string target = System.IO.Path.Combine(navFolder, s.Name + ".nav");
            if (!taken.Add(s.Name) || File.Exists(target)) { there++; continue; }
            string? error = copy(s.Path, target);
            if (error == null) copied++;
            else
            {
                failed++;
                if (firstError.Length == 0) firstError = error;
            }
        }
        return new StrayCopyResult(copied, there, failed, firstError);
    }

    private static string? DefaultCopy(string from, string to)
    {
        try { File.Copy(from, to, overwrite: false); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// The line that says where the stray navs are and where they go (chat and the Nav panel), or
    /// empty when there are none. Names the folder with the most of them; "and N more in other
    /// folders" for the rest.
    /// </summary>
    public static string Notice(IReadOnlyList<StrayNav> strays, string navFolder, string? navDataDir = null)
    {
        if (strays.Count == 0) return string.Empty;
        var byFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (StrayNav s in strays) byFolder[s.Folder] = byFolder.TryGetValue(s.Folder, out int n) ? n + 1 : 1;
        string top = string.Empty; int topCount = 0;
        foreach (var kv in byFolder) if (kv.Value > topCount) { top = kv.Key; topCount = kv.Value; }
        bool isNavData = IsNavDataFolder(top, navDataDir);
        string what = strays.Count == 1 ? "1 VirindiTank nav is" : $"{strays.Count} VirindiTank navs are";
        string where = byFolder.Count == 1
            ? $"in {top}"
            : $"in {top} ({topCount}) and {byFolder.Count - 1} other folder{(byFolder.Count == 2 ? "" : "s")}";
        string why = isNavData ? " (RynthNav's map data folder)" : string.Empty;
        return $"{what} {where}{why}, so the nav list doesn't show {(strays.Count == 1 ? "it" : "them")}. "
             + $"RynthAi's navs go in {navFolder} (subfolders are fine).";
    }

    /// <summary>The chat line after a copy (/ra nav import and the Nav panel's button).</summary>
    public static string CopyReply(StrayCopyResult r, string navFolder)
    {
        if (r.Copied == 0 && r.AlreadyThere == 0 && r.Failed == 0)
            return $"No VirindiTank navs outside NavProfiles to copy. The nav list shows {navFolder} and its subfolders.";
        var parts = new List<string>();
        if (r.Copied > 0) parts.Add($"Copied {r.Copied} nav{(r.Copied == 1 ? "" : "s")} into {navFolder}; {(r.Copied == 1 ? "it is" : "they are")} in the nav list now. The originals are still where they were");
        if (r.AlreadyThere > 0) parts.Add($"{r.AlreadyThere} left alone (NavProfiles has a nav of that name)");
        if (r.Failed > 0) parts.Add($"{r.Failed} couldn't be copied: {r.FirstError}");
        return string.Join("; ", parts) + ".";
    }

    private static bool IsNavDataFolder(string folder, string? navDataDir)
    {
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (name.Equals("NavData", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(navDataDir) && Full(navDataDir).Equals(Full(folder), StringComparison.OrdinalIgnoreCase);
    }

    private static string Full(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return string.Empty;
        try { return System.IO.Path.GetFullPath(dir).TrimEnd('\\', '/'); } catch { return string.Empty; }
    }

    private static bool IsSameOrInside(string dir, string parent)
    {
        if (parent.Length == 0) return false;
        return dir.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || dir.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
