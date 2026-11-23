using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// What the nav picker lists from NavProfiles (2026-11-22). It listed the top of the folder only,
/// so VirindiTank navs dropped in as a folder (NavProfiles\VirindiTank\*.nav, a folder per server
/// or per character) never showed. Now:
///   - the top of the folder first, as before ("Hunt");
///   - then the navs in its subfolders, as "Folder\Hunt" (up to <see cref="MaxDepth"/> deep),
///     sorted. Not the Deleted folder (the profile buttons move files there), not hidden or
///     dot folders, and not VirindiTank's "--&lt;character&gt;_&lt;server&gt;" state files, which a
///     copied VirindiTank folder has hundreds of (nearly all empty).
/// The names are paths relative to NavProfiles without ".nav", so Path.Combine(folder, name + ".nav")
/// is the file for every entry. System only (the tests compile it in).
/// </summary>
internal static class NavFileList
{
    /// <summary>How deep subfolders are looked into (NavProfiles\a\b\c\x.nav is the deepest).</summary>
    public const int MaxDepth = 3;

    /// <summary>Most entries listed (a whole Decal folder copied in can't flood the picker).</summary>
    public const int MaxEntries = 2000;

    /// <summary>The subfolder the profile buttons' Delete moves files into (ProfileFileOps.DeletedFolderName).</summary>
    public const string DeletedFolder = "Deleted";

    /// <summary>The navs in <paramref name="folder"/> (see the class summary); empty when it doesn't exist.</summary>
    public static List<string> List(string folder)
    {
        var top = new List<string>();
        var sub = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return top;
            foreach (string file in Directory.GetFiles(folder, "*.nav"))
                if (IsNavFile(file)) top.Add(Path.GetFileNameWithoutExtension(file));
            top.Sort(StringComparer.OrdinalIgnoreCase);
            AddSubfolders(folder, folder, 1, sub);
            sub.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { /* an unreadable folder lists what was read */ }
        top.AddRange(sub);
        if (top.Count > MaxEntries) top.RemoveRange(MaxEntries, top.Count - MaxEntries);
        return top;
    }

    private static void AddSubfolders(string root, string dir, int depth, List<string> into)
    {
        if (depth > MaxDepth || into.Count >= MaxEntries) return;
        string[] dirs;
        try { dirs = Directory.GetDirectories(dir); } catch { return; }
        foreach (string d in dirs)
        {
            if (!ListsFolder(d)) continue;
            try
            {
                foreach (string file in Directory.GetFiles(d, "*.nav"))
                {
                    if (!IsNavFile(file) || IsVTankStateFile(file)) continue;
                    into.Add(Path.GetRelativePath(root, file)[..^4]);
                    if (into.Count >= MaxEntries) return;
                }
            }
            catch { continue; }
            AddSubfolders(root, d, depth + 1, into);
        }
    }

    /// <summary>A subfolder whose navs are listed: not Deleted, not hidden or a dot folder.</summary>
    internal static bool ListsFolder(string dir)
    {
        string name = Path.GetFileName(dir);
        if (name.Length == 0 || name[0] == '.') return false;
        if (name.Equals(DeletedFolder, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var attrs = File.GetAttributes(dir);
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
            if ((attrs & FileAttributes.ReparsePoint) != 0) return false;   // never follow a junction
        }
        catch { return false; }
        return true;
    }

    /// <summary>A ".nav" file (the pattern alone would also take "x.nav~" on some systems).</summary>
    private static bool IsNavFile(string file) => file.EndsWith(".nav", StringComparison.OrdinalIgnoreCase);

    /// <summary>VirindiTank's own "--&lt;character&gt;_&lt;server&gt;.nav" (the route a character last had).</summary>
    internal static bool IsVTankStateFile(string file) => Path.GetFileName(file).StartsWith("--", StringComparison.Ordinal);

    /// <summary>
    /// When <paramref name="folder"/> or a listed subfolder last changed: the latest write time of
    /// them. Adding a file to a subfolder changes only that subfolder's time, so the folder's own
    /// time alone missed it. MinValue when the folder doesn't exist.
    /// </summary>
    public static DateTime Stamp(string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return DateTime.MinValue;
            DateTime latest = Directory.GetLastWriteTimeUtc(folder);
            StampSubfolders(folder, 1, ref latest);
            return latest;
        }
        catch { return DateTime.MinValue; }
    }

    private static void StampSubfolders(string dir, int depth, ref DateTime latest)
    {
        if (depth > MaxDepth) return;
        string[] dirs;
        try { dirs = Directory.GetDirectories(dir); } catch { return; }
        foreach (string d in dirs)
        {
            if (!ListsFolder(d)) continue;
            try
            {
                DateTime t = Directory.GetLastWriteTimeUtc(d);
                if (t > latest) latest = t;
            }
            catch { continue; }
            StampSubfolders(d, depth + 1, ref latest);
        }
    }

    /// <summary>
    /// The entry of <paramref name="names"/> that /ra nav load &lt;<paramref name="typed"/>&gt; means
    /// (typed already tidied: no quotes, no ".nav"): the entry itself, any case, with \ or /; else,
    /// for a name without a folder, the only subfolder entry with that file name. Null when there
    /// is none; <paramref name="matches"/> is how many subfolder entries had the name (2+: the
    /// player has to say which).
    /// </summary>
    public static string? Find(IReadOnlyList<string> names, string typed, out int matches)
    {
        matches = 0;
        string want = (typed ?? string.Empty).Trim().Replace('/', '\\').Trim('\\');
        if (want.Length == 0) return null;
        foreach (string n in names)
            if (n.Equals(want, StringComparison.OrdinalIgnoreCase)) { matches = 1; return n; }
        if (want.Contains('\\')) return null;

        string? found = null;
        foreach (string n in names)
        {
            int slash = n.LastIndexOf('\\');
            if (slash < 0) continue;
            if (!n.AsSpan(slash + 1).Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
            matches++;
            found ??= n;
        }
        return matches == 1 ? found : null;
    }

    /// <summary>The subfolder entries named <paramref name="name"/> (for the "which one?" reply).</summary>
    public static List<string> SameName(IReadOnlyList<string> names, string name)
    {
        var list = new List<string>();
        foreach (string n in names)
        {
            int slash = n.LastIndexOf('\\');
            if (slash >= 0 && n.AsSpan(slash + 1).Equals(name, StringComparison.OrdinalIgnoreCase)) list.Add(n);
        }
        return list;
    }
}
