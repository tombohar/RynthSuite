using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>One inventory file as found on disk (for the change check and the reload).</summary>
internal readonly record struct InventoryFileStamp(string Path, long Length, DateTime WriteUtc);

/// <summary>
/// The files: &lt;root&gt;\&lt;server&gt;\&lt;account&gt;\&lt;character&gt;.json, root
/// %APPDATA%\RynthCore\inventory. Each client writes only its own character's file, through a
/// temp file in the same folder and an atomic replace (MoveFileEx REPLACE_EXISTING), so a
/// reader in another client sees the old file or the new one, never half of one. Readers open
/// with share read/write/delete so they never block a writer's replace.
/// </summary>
internal sealed class InventoryStore
{
    public const string ForgottenFolder = "_forgotten";

    public string Root { get; }
    private static int _tmpSeq;
    private const int MoveAttempts = 10;

    public InventoryStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "inventory");
    }

    /// <summary>A name made safe for one path segment ("unknown" when empty).</summary>
    public static string Segment(string? name)
    {
        string s = (name ?? string.Empty).Trim();
        var sb = new StringBuilder(s.Length);
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (char c in s)
            sb.Append(Array.IndexOf(bad, c) >= 0 || c < 0x20 ? '_' : c);
        string r = sb.ToString().Trim().TrimEnd('.', ' ');
        if (r.Length == 0 || r == "." || r == "..") return "unknown";
        if (r.Length > 64) r = r.Substring(0, 64);
        string stem = r.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            r = "_" + r;
        if (r.StartsWith("_forgotten", StringComparison.OrdinalIgnoreCase)) r = "-" + r;
        return r;
    }

    public string PathFor(string server, string account, string character) =>
        System.IO.Path.Combine(Root, Segment(server), Segment(account), Segment(character) + ".json");

    /// <summary>Writes the snapshot to its file (atomic replace). Returns the path, or throws IOException.</summary>
    public string Save(CharacterSnapshot s)
    {
        string path = PathFor(s.Server, s.Account, s.Character);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Unique per process and per write, so two writes can never share a temp file.
        string tmp = path + "." + Environment.ProcessId.ToString() + "." + Interlocked.Increment(ref _tmpSeq).ToString() + ".tmp";
        byte[] bytes = new UTF8Encoding(false).GetBytes(InventoryJson.Write(s));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        // Windows refuses to replace a file another process has open at that instant (a
        // reader in another client): try again for up to about a quarter of a second, then
        // give up until the next save. Runs on a worker, except the final save at logout.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                break;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < MoveAttempts)
            {
                Thread.Sleep(5 * attempt);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
        s.FilePath = path;
        return path;
    }

    /// <summary>Reads one file. Null (with a reason) when missing, unreadable or not ours.</summary>
    public static CharacterSnapshot? Load(string path, out string? error)
    {
        error = null;
        try
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();
            CharacterSnapshot? s = InventoryJson.Read(text, out error);
            if (s != null) s.FilePath = path;
            return s;
        }
        catch (FileNotFoundException) { error = "missing"; return null; }
        catch (DirectoryNotFoundException) { error = "missing"; return null; }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    /// <summary>Every character file under the root (server\account\character.json), forgotten ones excluded.</summary>
    public List<InventoryFileStamp> ListFiles()
    {
        var list = new List<InventoryFileStamp>();
        try
        {
            if (!Directory.Exists(Root)) return list;
            foreach (string serverDir in Directory.EnumerateDirectories(Root))
            {
                if (System.IO.Path.GetFileName(serverDir).Equals(ForgottenFolder, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string accountDir in SafeDirs(serverDir))
                {
                    foreach (string file in SafeFiles(accountDir))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            list.Add(new InventoryFileStamp(file, fi.Length, fi.LastWriteTimeUtc));
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.json"); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Loads every file. Broken ones are skipped and counted.</summary>
    public List<CharacterSnapshot> LoadAll(out int broken)
    {
        broken = 0;
        var result = new List<CharacterSnapshot>();
        foreach (InventoryFileStamp f in ListFiles())
        {
            CharacterSnapshot? s = Load(f.Path, out _);
            if (s != null) result.Add(s);
            else broken++;
        }
        return result;
    }

    /// <summary>
    /// Forgets a character: its file moves to &lt;root&gt;\_forgotten\... (kept, not deleted,
    /// so a mistake can be undone by moving it back). True when it moved.
    /// </summary>
    public bool Forget(string filePath, out string? error)
    {
        error = null;
        try
        {
            string full = System.IO.Path.GetFullPath(filePath);
            string root = System.IO.Path.GetFullPath(Root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error = "not an inventory file";
                return false;
            }
            if (!File.Exists(full))
            {
                error = "already gone";
                return false;
            }
            string dest = System.IO.Path.Combine(Root, ForgottenFolder, full.Substring(root.Length));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Move(full, dest, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
