using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>What <see cref="NewProfileFiles.FindOrCreateNav"/> / <see cref="NewProfileFiles.FindOrCreateMeta"/> did.</summary>
internal enum ProfileFileResult
{
    /// <summary>The file exists; nothing was written.</summary>
    Found,
    /// <summary>There was no such file: a blank one was written.</summary>
    Created,
    /// <summary>The name can't be a file name (the error says why); nothing was written.</summary>
    Rejected,
    /// <summary>The blank file couldn't be written (the error says why and the fix).</summary>
    Failed,
    /// <summary>Create only (/ra meta new): the file already exists; nothing was written.</summary>
    Exists,
}

/// <summary>
/// "Load makes a new one", as in VTank (2026-10-13): /ra nav load &lt;name&gt; (and /vt nav load)
/// and /ra meta load &lt;name&gt; (and /vt meta load) create a blank &lt;name&gt; when there is no
/// such file, then load it, so a route can be recorded into it or rules added to it. Here: the
/// name a player typed, made into a file name (or why it can't be one), the lookup in the data
/// folder and the blank files. Writes go through <see cref="DataWriteGuard"/>, so a folder the
/// client can't write is reported with the reason and the fix.
/// </summary>
internal static class NewProfileFiles
{
    public static readonly string[] NavExtensions = { ".nav" };
    public static readonly string[] MetaExtensions = { ".af", ".met" };

    /// <summary>The route type of a blank nav: VTank's default for a new route.</summary>
    public const NavRouteType BlankNavType = NavRouteType.Circular;

    /// <summary>Longest name accepted (well inside MAX_PATH with the folder).</summary>
    public const int MaxNameLength = 120;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The name as typed, tidied: whitespace trimmed, surrounding quotes dropped, one of
    /// <paramref name="extensions"/> stripped ("farm.af" -> "farm"). Not checked: use
    /// <see cref="Validate"/> before using it as a file name. <paramref name="extension"/> is the
    /// extension that was given (with its dot, lower case), else empty.
    /// </summary>
    public static string Tidy(string? raw, IReadOnlyList<string> extensions, out string extension)
    {
        extension = string.Empty;
        string s = (raw ?? string.Empty).Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
            s = s[1..^1].Trim();
        s = s.Trim('"').Trim();
        foreach (string ext in extensions)
        {
            if (s.Length > ext.Length && s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                extension = ext.ToLowerInvariant();
                s = s[..^ext.Length].TrimEnd();
                break;
            }
        }
        return s;
    }

    /// <summary>Null when <paramref name="name"/> (tidied) is safe as a file name in a data folder, else why not.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "no name given";
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return "no folders, just a name";
        if (name.Contains("..", StringComparison.Ordinal)) return "\"..\" isn't allowed in a name";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "a file name can't contain \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "a file name can't end with a dot or a space";
        if (name.Length > MaxNameLength) return $"longer than {MaxNameLength} characters";
        int dot = name.IndexOf('.');
        string stem = (dot >= 0 ? name[..dot] : name).TrimEnd();
        if (ReservedNames.Contains(stem)) return $"\"{stem}\" is a name Windows reserves";
        return null;
    }

    /// <summary>The empty route a new nav starts as.</summary>
    public static NavRouteParser BlankNav() => new() { RouteType = BlankNavType };

    /// <summary>
    /// <paramref name="folder"/>\&lt;name&gt;.nav for the name typed: found, or created blank
    /// (VTank format: header, route type Circular, 0 points). <paramref name="name"/> is the tidied
    /// name, <paramref name="path"/> the file (empty when rejected), <paramref name="error"/> why
    /// it was rejected or couldn't be written.
    /// </summary>
    public static ProfileFileResult FindOrCreateNav(string folder, string? raw,
        out string name, out string path, out string error)
    {
        name = Tidy(raw, NavExtensions, out _);
        path = string.Empty;
        error = Validate(name) ?? string.Empty;
        if (error.Length > 0) return ProfileFileResult.Rejected;

        path = Path.Combine(folder, name + ".nav");
        if (File.Exists(path)) return ProfileFileResult.Found;

        string target = path;
        NavRouteParser route = BlankNav();
        if (!DataWriteGuard.TrySave("the new nav", target, () => route.Save(target), out error, callerShows: true))
            return ProfileFileResult.Failed;
        return ProfileFileResult.Created;
    }

    /// <summary>
    /// The meta for the name typed in <paramref name="folder"/>: the extension given first
    /// ("farm.met"), else &lt;name&gt;.af, else &lt;name&gt;.met; when there is none, &lt;name&gt;.af
    /// is created blank (one state "Default", no rules). <paramref name="createOnly"/> (/ra meta new):
    /// an existing file is <see cref="ProfileFileResult.Exists"/> and nothing is written.
    /// </summary>
    public static ProfileFileResult FindOrCreateMeta(string folder, string? raw,
        out string name, out string path, out string error, bool createOnly = false)
    {
        name = Tidy(raw, MetaExtensions, out string given);
        path = string.Empty;
        error = Validate(name) ?? string.Empty;
        if (error.Length > 0) return ProfileFileResult.Rejected;

        var order = new List<string>(3);
        if (given.Length > 0) order.Add(given);
        foreach (string e in MetaExtensions)
            if (!order.Contains(e)) order.Add(e);
        foreach (string e in order)
        {
            string candidate = Path.Combine(folder, name + e);
            if (File.Exists(candidate))
            {
                path = candidate;
                return createOnly ? ProfileFileResult.Exists : ProfileFileResult.Found;
            }
        }

        path = Path.Combine(folder, name + ".af");   // a .met can't be written: new metas are .af
        string target = path;
        if (!DataWriteGuard.TrySave("the new meta", target, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
                File.WriteAllText(target, AfFileWriter.BlankMetaText());
            }, out error, callerShows: true))
            return ProfileFileResult.Failed;
        return ProfileFileResult.Created;
    }
}
