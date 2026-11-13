using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// The file side of the Loaded files drawer's profile buttons (Save As, Rename, Move to Deleted) and
/// their /ra verbs, for settings profiles, navs, metas and loot profiles (2026-11-11): the name
/// typed checked as a file name (<see cref="NewProfileFiles"/>' rules), whether it would replace
/// another file, and the Deleted folder. Nothing is ever deleted: a profile removed from its picker
/// is moved into a "Deleted" folder beside it, and a file replaced by Save As has its old copy
/// kept there first. The pickers list only their own folder, so nothing in Deleted shows in them;
/// moving a file back out of Deleted (and dropping the time from its name) brings it back.
/// Pure file work, so the offline tests cover it.
/// </summary>
internal static class ProfileFileOps
{
    /// <summary>The folder, inside each profile folder, that removed and replaced profiles go to.</summary>
    public const string DeletedFolderName = "Deleted";

    /// <summary>
    /// The name typed, tidied (whitespace, quotes and one of <paramref name="extensions"/> dropped)
    /// into <paramref name="name"/>. Null when it is safe as a file name, else why not.
    /// </summary>
    public static string? CleanName(string? raw, IReadOnlyList<string> extensions, out string name)
    {
        name = NewProfileFiles.Tidy(raw, extensions, out _);
        return NewProfileFiles.Validate(name);
    }

    /// <summary>The same file (full paths, case ignored)? False when either is empty or not a path.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a.Trim()), Path.GetFullPath(b.Trim()), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// Saving to <paramref name="target"/> would replace another file: it exists and it isn't
    /// <paramref name="current"/> (saving a profile onto its own file is a plain save).
    /// </summary>
    public static bool WouldReplace(string target, string? current) =>
        File.Exists(target) && !SamePath(target, current);

    /// <summary>&lt;folder of path&gt;\Deleted.</summary>
    public static string DeletedFolderFor(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", DeletedFolderName);

    /// <summary>
    /// Where <paramref name="path"/> goes in the Deleted folder: its name with the time before the
    /// extension ("farm.nav" -> Deleted\farm.2026-11-11_153012.nav), and " (2)", " (3)" ... when
    /// that is taken, so an earlier copy is never replaced.
    /// </summary>
    public static string DeletedPathFor(string path, DateTime now)
    {
        string folder = DeletedFolderFor(path);
        string ext = Path.GetExtension(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string stamp = now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string candidate = Path.Combine(folder, $"{stem}.{stamp}{ext}");
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem}.{stamp} ({n}){ext}");
        return candidate;
    }

    /// <summary>
    /// Moves <paramref name="path"/> into its Deleted folder (made when missing). Never deletes:
    /// on any failure the file stays where it was. <paramref name="moved"/> is where it went.
    /// </summary>
    public static bool MoveToDeleted(string path, DateTime now, out string moved, out string error)
    {
        moved = string.Empty;
        error = string.Empty;
        try
        {
            if (!File.Exists(path)) { error = $"{Path.GetFileName(path)} isn't there any more"; return false; }
            string target = DeletedPathFor(path, now);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(path, target, overwrite: false);
            moved = target;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Keeps a copy of <paramref name="path"/> in its Deleted folder before it is replaced (Save As
    /// over another profile). Nothing to keep (no such file) is a success with an empty
    /// <paramref name="copy"/>.
    /// </summary>
    public static bool CopyToDeleted(string path, DateTime now, out string copy, out string error)
    {
        copy = string.Empty;
        error = string.Empty;
        try
        {
            if (!File.Exists(path)) return true;
            string target = DeletedPathFor(path, now);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: false);
            copy = target;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Renames <paramref name="from"/> to <paramref name="to"/> (same folder). Refused when
    /// <paramref name="to"/> is another file that exists (a change of case only is allowed).
    /// </summary>
    public static bool Rename(string from, string to, out string error)
    {
        error = string.Empty;
        try
        {
            if (!File.Exists(from)) { error = $"{Path.GetFileName(from)} isn't there any more"; return false; }
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.Ordinal))
            {
                error = "that is already its name";
                return false;
            }
            if (File.Exists(to) && !SamePath(from, to))
            {
                error = $"{Path.GetFileName(to)} already exists";
                return false;
            }
            if (SamePath(from, to))
            {
                // Only the case changes: Windows sees one file, so go through a temporary name.
                string tmp = from + "." + Environment.ProcessId + ".rename";
                File.Move(from, tmp);
                try { File.Move(tmp, to); }
                catch
                {
                    try { File.Move(tmp, from); } catch { }
                    throw;
                }
            }
            else
            {
                File.Move(from, to, overwrite: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
