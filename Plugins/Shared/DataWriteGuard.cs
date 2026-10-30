using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.Shared;

/// <summary>Why a write in a plugin's data folders failed (see <see cref="DataWriteGuard.Classify"/>).</summary>
internal enum WriteFailureKind
{
    /// <summary>Windows refused: the folder or file isn't writable for this account, or Controlled folder access blocked acclient.exe.</summary>
    AccessDenied,
    /// <summary>The file carries the ReadOnly attribute and it couldn't be cleared.</summary>
    ReadOnlyFile,
    /// <summary>Another program holds the file open (editor, sync client, antivirus).</summary>
    InUse,
    DiskFull,
    /// <summary>A OneDrive (cloud files) placeholder whose sync provider isn't running.</summary>
    CloudProvider,
    /// <summary>The folder doesn't exist and couldn't be created.</summary>
    PathMissing,
    Other,
}

/// <summary>
/// Saves of metas, navs, loot profiles and settings profiles (2026-10-13; RynthAi, and RynthILT for
/// its own files and the copies it migrates from RynthAi; linked into each, so each plugin keeps
/// its own state and says its own name: <see cref="PluginName"/>). Players reported
/// that metas and navs "don't save" and that "the plugin folders are read only": every one of
/// those saves swallowed its exception, so a folder the client can't write looked like a save
/// that silently did nothing. Now:
///   - <see cref="PrepareOverwrite"/> clears the ReadOnly attribute on our own file before it
///     is overwritten (works whenever the folder itself is writable);
///   - <see cref="Report"/> says in chat, once per folder per session, what couldn't be saved,
///     where and why (access denied / read-only file / file in use / ...), with the fix (the
///     launcher's Recovery Actions > Fix folder permissions). Every failure is logged
///     (throttled per folder so a failing autosave can't flood the log);
///   - <see cref="CheckFolders"/> is the startup self-check: it tries to create a file in
///     each data folder.
/// Chat lines are queued here and posted by the plugin tick once the character is in game
/// (<see cref="DrainChat"/>), so a failure on any thread, or before login, still shows.
/// </summary>
internal static class DataWriteGuard
{
    public const string FixHint =
        "Fix: in the RynthCore launcher, Recovery Actions > Fix folder permissions (one administrator prompt).";

    /// <summary>A failing folder logs at most once per this many ms (plus a count of the skipped ones).</summary>
    public const long LogThrottleMs = 60_000;

    private static readonly object Gate = new();
    private static readonly HashSet<string> ReportedFolders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long At, int Skipped)> LastLog = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> PendingChat = new();

    /// <summary>The plugin's name in chat and log lines ("[RynthAi] ..."); Initialize sets it.</summary>
    public static string PluginName = "RynthAi";

    /// <summary>What the startup self-check says won't be kept when a folder can't be written.</summary>
    public static string DataDescription = "Metas, navs, loot and settings";

    /// <summary>The plugin's log (Initialize sets it). Null: nothing is logged.</summary>
    public static Action<string>? LogSink;

    /// <summary>Milliseconds clock; tests replace it.</summary>
    internal static Func<long> Clock = () => Environment.TickCount64;

    /// <summary>Forgets what was reported (a new session; tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            ReportedFolders.Clear();
            LastLog.Clear();
            PendingChat.Clear();
        }
    }

    /// <summary>Chat lines waiting to be posted.</summary>
    public static int PendingCount { get { lock (Gate) return PendingChat.Count; } }

    /// <summary>Posts the queued chat lines (plugin tick, in game).</summary>
    public static void DrainChat(Action<string> chat)
    {
        while (true)
        {
            string line;
            lock (Gate)
            {
                if (PendingChat.Count == 0) return;
                line = PendingChat.Dequeue();
            }
            try { chat(line); } catch { }
        }
    }

    /// <summary>
    /// Clears the ReadOnly attribute of an existing file we are about to overwrite. Returns
    /// true when it was set and is now cleared. Never throws: when the folder isn't writable
    /// either, the write that follows fails and is reported.
    /// </summary>
    public static bool PrepareOverwrite(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            FileAttributes a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) == 0) return false;
            File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            Log($"[{PluginName}] {path} was marked read-only; cleared that so it can be saved.");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> for <paramref name="path"/> (ReadOnly cleared first).
    /// False when it threw; the failure is then reported (<see cref="Report"/>).
    /// </summary>
    public static bool TrySave(string what, string path, Action write) => TrySave(what, path, write, out _);

    /// <inheritdoc cref="TrySave(string, string, Action)"/>
    /// <param name="reason">When it failed, the reason and the fix in plain words ("access denied (...). Fix: ..."), else empty.</param>
    /// <param name="callerShows">The caller puts the failure in chat itself (an explicit save the
    /// player asked for): nothing is queued, the folder still counts as reported.</param>
    public static bool TrySave(string what, string path, Action write, out string reason, bool callerShows = false)
    {
        reason = string.Empty;
        try
        {
            PrepareOverwrite(path);
            write();
            return true;
        }
        catch (Exception ex)
        {
            reason = Report(what, path, ex, callerShows);
            return false;
        }
    }

    // ERROR_* as HRESULTs (0x8007xxxx).
    private const int HrAccessDenied = unchecked((int)0x80070005);
    private const int HrWriteProtect = unchecked((int)0x80070013);
    private const int HrSharingViolation = unchecked((int)0x80070020);
    private const int HrLockViolation = unchecked((int)0x80070021);
    private const int HrHandleDiskFull = unchecked((int)0x80070027);
    private const int HrDiskFull = unchecked((int)0x80070070);
    private const int HrCloudProviderNotRunning = unchecked((int)0x8007016A);
    private const int HrCloudNotInSync = unchecked((int)0x80070185);
    private const int HrUserMappedFile = unchecked((int)0x800704C8);

    /// <summary>What kind of failure <paramref name="ex"/> is, for a write of <paramref name="path"/>.</summary>
    public static WriteFailureKind Classify(Exception ex, string? path)
    {
        switch (ex)
        {
            case UnauthorizedAccessException:
                return IsReadOnlyFile(path) ? WriteFailureKind.ReadOnlyFile : WriteFailureKind.AccessDenied;
            case DirectoryNotFoundException:
                return WriteFailureKind.PathMissing;
            case IOException io:
                switch (io.HResult)
                {
                    case HrSharingViolation:
                    case HrLockViolation:
                    case HrUserMappedFile:
                        return WriteFailureKind.InUse;
                    case HrDiskFull:
                    case HrHandleDiskFull:
                        return WriteFailureKind.DiskFull;
                    case HrCloudProviderNotRunning:
                    case HrCloudNotInSync:
                        return WriteFailureKind.CloudProvider;
                    case HrAccessDenied:
                    case HrWriteProtect:
                        return IsReadOnlyFile(path) ? WriteFailureKind.ReadOnlyFile : WriteFailureKind.AccessDenied;
                }
                return WriteFailureKind.Other;
            default:
                return WriteFailureKind.Other;
        }
    }

    private static bool IsReadOnlyFile(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch { return false; }
    }

    /// <summary>The reason in plain words.</summary>
    public static string Describe(WriteFailureKind kind, Exception? ex) => kind switch
    {
        WriteFailureKind.AccessDenied =>
            "access denied (Windows doesn't let this game client write there: the folder's permissions, " +
            "or Windows Security's Controlled folder access blocking acclient.exe)",
        WriteFailureKind.ReadOnlyFile => $"the file is marked read-only and {PluginName} couldn't clear that",
        WriteFailureKind.InUse => "the file is open in another program (an editor, OneDrive or backup sync, antivirus)",
        WriteFailureKind.DiskFull => "the disk is full",
        WriteFailureKind.CloudProvider => "the folder is synced by OneDrive (or another cloud app) that isn't running",
        WriteFailureKind.PathMissing => "the folder doesn't exist and couldn't be created",
        _ => ex?.Message ?? "unknown error",
    };

    /// <summary>What the player can do about it.</summary>
    public static string Hint(WriteFailureKind kind) => kind switch
    {
        WriteFailureKind.AccessDenied or WriteFailureKind.ReadOnlyFile or WriteFailureKind.PathMissing => FixHint,
        WriteFailureKind.InUse => "Close the program that has it open and save again.",
        WriteFailureKind.DiskFull => "Free some disk space and save again.",
        WriteFailureKind.CloudProvider => "Start OneDrive, or install RynthSuite outside the synced folder.",
        _ => FixHint,
    };

    /// <summary>
    /// A failed save of <paramref name="what"/> ("the meta", "the nav", ...) to
    /// <paramref name="path"/>: logged (throttled per folder) and, the first time for that
    /// folder this session, queued for chat. Returns the reason and the fix in plain words.
    /// </summary>
    public static string Report(string what, string path, Exception ex, bool callerShows = false)
    {
        WriteFailureKind kind = Classify(ex, path);
        string reason = Describe(kind, ex);
        string folder = FolderOf(path);
        bool chat;
        string? logLine = null;
        lock (Gate)
        {
            chat = !AlreadyReported(folder);
            ReportedFolders.Add(folder);
            long now = Clock();
            if (!LastLog.TryGetValue(folder, out var last) || now - last.At >= LogThrottleMs || chat)
            {
                string skipped = last.Skipped > 0 ? $" ({last.Skipped} more failure(s) in this folder since the last line)" : "";
                logLine = $"[{PluginName}] Save failed: {what} -> {path}: {reason} [{ex.GetType().Name}: {ex.Message}]{skipped}";
                LastLog[folder] = (now, 0);
            }
            else
            {
                LastLog[folder] = (last.At, last.Skipped + 1);
            }
            if (chat && !callerShows)
                PendingChat.Enqueue($"[{PluginName}] Couldn't save {what} to {path}: {reason}. {Hint(kind)} " +
                                    "(Shown once per folder; later failures there go to the log.)");
        }
        if (logLine != null) Log(logLine);
        return $"{reason}. {Hint(kind)}";
    }

    /// <summary>One data folder the self-check couldn't write.</summary>
    internal readonly record struct FolderProblem(string Folder, WriteFailureKind Kind, string Reason);

    /// <summary>
    /// The startup self-check: creates (if missing) and writes a throw-away file in each of
    /// <paramref name="folders"/>. Returns the folders that failed; for those, one line goes
    /// to the log and one summary line is queued for chat.
    /// </summary>
    public static List<FolderProblem> CheckFolders(IEnumerable<string> folders)
    {
        var problems = new List<FolderProblem>();
        foreach (string folder in folders)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            string probe = Path.Combine(folder, $".{PluginName.ToLowerInvariant()}-write-check-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(folder);
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                    fs.WriteByte(0);
            }
            catch (Exception ex)
            {
                WriteFailureKind kind = Classify(ex, null);
                problems.Add(new FolderProblem(folder, kind, Describe(kind, ex)));
                Log($"[{PluginName}] Data folder not writable: {folder}: {Describe(kind, ex)} [{ex.GetType().Name}: {ex.Message}]");
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            }
        }

        if (problems.Count > 0)
        {
            lock (Gate)
            {
                foreach (var p in problems) ReportedFolders.Add(p.Folder);   // the save itself needn't repeat it
                string list = problems.Count == 1
                    ? problems[0].Folder
                    : $"{problems.Count} folders ({string.Join(", ", problems.ConvertAll(p => Path.GetFileName(p.Folder)))})";
                PendingChat.Enqueue($"[{PluginName}] Can't write to {list}: {problems[0].Reason}. {DataDescription} " +
                                    $"saved there will NOT be kept. {Hint(problems[0].Kind)}");
            }
        }
        return problems;
    }

    /// <summary>This folder, or one it is inside (e.g. SettingsProfiles for a character's folder), was reported. Under Gate.</summary>
    private static bool AlreadyReported(string folder)
    {
        foreach (string f in ReportedFolders)
            if (folder.Equals(f, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>The folder a failure is counted against.</summary>
    internal static string FolderOf(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(dir) ? path : dir.TrimEnd('\\', '/');
        }
        catch { return path; }
    }

    private static void Log(string line)
    {
        try { LogSink?.Invoke(line); } catch { }
    }
}
