using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// <c>require("filesystem")</c>, UtilityBelt's shape, for scripts that keep their own files:
///
///   local fs = require("filesystem").GetData()     -- Data\&lt;Script&gt;\ (read/write)
///   local fs = require("filesystem").GetScript()   -- the script's own folder (read-only)
///   fs.WriteText(file, text), fs.AppendText(file, text), fs.ReadText(file), fs.ReadLines(file),
///   fs.FileExists(file), fs.DirectoryExists(dir), fs.CreateDirectory(dir), fs.DeleteFile(file),
///   fs.GetFiles([dir]), fs.GetDirectories([dir])   (names relative to the folder)
///
/// Like UB's (ref string error), every call returns its result plus an error message:
/// <c>local text, err = fs.ReadText("notes.txt")</c> gives nil and the reason on failure.
/// Paths are relative to the folder and can never leave it (LibraryPaths.Resolve).
/// UB's GetCustom (any folder, with a prompt) isn't offered.
/// </summary>
internal static class LibraryFileSystem
{
    private const long MaxReadBytes = 64L * 1024 * 1024;

    public static DynValue CreateModule(Script s, ScriptContext ctx, string dataRoot)
    {
        var m = new Table(s);
        DynValue? data = null, script = null;
        m.Set("GetData", DynValue.NewCallback((c, a) =>
            data ??= CreateAccess(s, LibraryPaths.ScriptDataFolder(dataRoot, ctx.Name), writable: true, "data folder")));
        m.Set("GetScript", DynValue.NewCallback((c, a) =>
        {
            if (ctx.Folder == null)
                throw new ScriptRuntimeException("filesystem.GetScript(): only folder scripts (Scripts\\<Name>\\index.lua) have a script folder; use GetData()");
            return script ??= CreateAccess(s, ctx.Folder, writable: false, "script folder");
        }));
        m.Set("GetCustom", DynValue.NewCallback((c, a) =>
            throw new ScriptRuntimeException("filesystem.GetCustom isn't available in RynthLua; use GetData() (the script's data folder)")));
        var mt = new Table(s);
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString("module filesystem")));
        m.MetaTable = mt;
        return DynValue.NewTable(m);
    }

    private static DynValue CreateAccess(Script s, string root, bool writable, string label)
    {
        var t = new Table(s);
        t.Set("IsApproved", DynValue.True);
        t.Set("IsReadOnly", DynValue.NewBoolean(!writable));

        DynValue Fail(DynValue failed, string error) => DynValue.NewTuple(failed, DynValue.NewString(error));
        DynValue Ok(DynValue v) => DynValue.NewTuple(v, DynValue.Nil);
        string Arg(CallbackArguments a, int i) => LuaArgs.Str(LuaArgs.At(LuaArgs.Of(a, t), i));

        // Resolves argument 0; for writes also checks the folder is writable.
        string? Path0(CallbackArguments a, bool write, bool allowRoot, out string error)
        {
            if (write && !writable) { error = $"the {label} is read-only; write to require(\"filesystem\").GetData()"; return null; }
            string rel = Arg(a, 0);
            if (!allowRoot && rel.Trim().Length == 0) { error = "needs a file name"; return null; }
            return LibraryPaths.Resolve(root, rel, out error);
        }

        void Add(string name, DynValue failed, bool write, bool allowRoot, Func<CallbackArguments, string, DynValue> body)
        {
            t.Set(name, DynValue.NewCallback((c, a) =>
            {
                string? full = Path0(a, write, allowRoot, out string error);
                if (full == null) return Fail(failed, $"{name}: {error}");
                try { return Ok(body(a, full)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    return Fail(failed, $"{name}: {ex.Message}");
                }
                catch (InvalidDataException ex) { return Fail(failed, $"{name}: {ex.Message}"); }
            }));
        }

        Add("ReadText", DynValue.Nil, write: false, allowRoot: false, (a, full) =>
            DynValue.NewString(ReadAll(full)));
        Add("ReadLines", DynValue.Nil, write: false, allowRoot: false, (a, full) =>
        {
            var list = new Table(s);
            foreach (string line in ReadAll(full).Replace("\r\n", "\n").Split('\n'))
                list.Append(DynValue.NewString(line));
            // A final newline doesn't make an extra empty line.
            int n = list.Length;
            if (n > 0 && list.Get(n).String.Length == 0) list.Remove(n);
            return DynValue.NewTable(list);
        });
        Add("WriteText", DynValue.False, write: true, allowRoot: false, (a, full) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string tmp = full + ".tmp";
            File.WriteAllText(tmp, Arg(a, 1), new UTF8Encoding(false));
            File.Move(tmp, full, overwrite: true);
            return DynValue.True;
        });
        Add("AppendText", DynValue.False, write: true, allowRoot: false, (a, full) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.AppendAllText(full, Arg(a, 1), new UTF8Encoding(false));
            return DynValue.True;
        });
        Add("FileExists", DynValue.False, write: false, allowRoot: false, (a, full) =>
            DynValue.NewBoolean(File.Exists(full)));
        Add("DirectoryExists", DynValue.False, write: false, allowRoot: true, (a, full) =>
            DynValue.NewBoolean(Directory.Exists(full)));
        Add("CreateDirectory", DynValue.False, write: true, allowRoot: true, (a, full) =>
        {
            Directory.CreateDirectory(full);
            return DynValue.True;
        });
        Add("DeleteFile", DynValue.False, write: true, allowRoot: false, (a, full) =>
        {
            if (!File.Exists(full)) throw new IOException("no such file");
            File.Delete(full);
            return DynValue.True;
        });
        Add("GetFiles", DynValue.Nil, write: false, allowRoot: true, (a, full) =>
            Listing(s, root, Directory.Exists(full) ? Directory.GetFiles(full) : Array.Empty<string>()));
        Add("GetDirectories", DynValue.Nil, write: false, allowRoot: true, (a, full) =>
            Listing(s, root, Directory.Exists(full) ? Directory.GetDirectories(full) : Array.Empty<string>()));

        var mt = new Table(s);
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString($"filesystem ({label})")));
        t.MetaTable = mt;
        return DynValue.NewTable(t);
    }

    private static string ReadAll(string full)
    {
        var info = new FileInfo(full);
        if (!info.Exists) throw new IOException("no such file");
        if (info.Length > MaxReadBytes) throw new InvalidDataException($"the file is over {MaxReadBytes / (1024 * 1024)} MB");
        return File.ReadAllText(full, Encoding.UTF8);
    }

    private static DynValue Listing(Script s, string root, string[] paths)
    {
        string fullRoot = Path.GetFullPath(root);
        var names = new List<string>(paths.Length);
        foreach (string p in paths)
        {
            // Our own temp files stay out of sight.
            if (p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(Path.GetRelativePath(fullRoot, p));
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var list = new Table(s);
        foreach (string n in names) list.Append(DynValue.NewString(n));
        return DynValue.NewTable(list);
    }
}
