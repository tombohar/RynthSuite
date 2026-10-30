using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// A buff profile: the spells buffing keeps up, in cast order. Built in the Spells window
/// (/ra spells) and chosen under Loaded Files (Buffs). Without one, buffing uses its
/// built-in list.
/// </summary>
public sealed class BuffProfile
{
    public int Schema = BuffProfileStore.CurrentSchema;
    public string Name = "";
    public string Notes = "";
    public List<BuffProfileEntry> Entries = new();
}

/// <summary>One spell line in a <see cref="BuffProfile"/>.</summary>
public sealed class BuffProfileEntry
{
    /// <summary>The spell picked in the Spells window (shown in the profile list).</summary>
    public int SpellId;
    public string Name = "";
    /// <summary>
    /// Base name buffing resolves to the best tier the character knows ("Strength Self");
    /// see <see cref="SpellCatalog.BuffKeyFor"/>.
    /// </summary>
    public string Key = "";
    /// <summary>ACE spell family: the fallback when the base name has no tier ladder.</summary>
    public int FamilyId;
    public string School = "";
    public string Target = "";
    public int Level;
    public bool Enabled = true;
}

/// <summary>
/// Buff profile files: <c>&lt;SuiteDir&gt;\RynthAi\BuffProfiles\&lt;name&gt;.json</c>, shared by
/// every character like the Nav, Loot and Meta folders.
/// </summary>
internal static class BuffProfileStore
{
    public const int CurrentSchema = 1;
    public const string FileExtension = ".json";

    /// <summary>Tests point the folder at a temp directory.</summary>
    internal static string? FolderOverride;

    public static string Folder => FolderOverride ?? Path.Combine(RynthInstallPaths.RynthAiDir, "BuffProfiles");

    /// <summary>Full path for a profile name (sanitised for Windows file names).</summary>
    public static string PathFor(string profileName) =>
        Path.Combine(Folder, SanitizeFileName(profileName) + FileExtension);

    /// <summary>Profile file names (with extension) in the folder, sorted.</summary>
    public static List<string> ListFileNames()
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (string f in Directory.GetFiles(Folder, "*" + FileExtension))
                list.Add(Path.GetFileName(f));
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { /* folder unreadable: no profiles */ }
        return list;
    }

    public static bool TryLoad(string path, out BuffProfile profile, out string error)
    {
        profile = new BuffProfile();
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { error = "file not found"; return false; }
            var p = JsonSerializer.Deserialize(File.ReadAllText(path), RynthAiJsonContext.Default.BuffProfile);
            if (p == null) { error = "empty file"; return false; }
            if (p.Schema > CurrentSchema) { error = $"made by a newer RynthAi (schema {p.Schema})"; return false; }
            Normalize(p, Path.GetFileNameWithoutExtension(path));
            profile = p;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Writes the profile (temp file + move, so a crash never leaves half a file).</summary>
    public static void Save(BuffProfile profile, string path)
    {
        Normalize(profile, Path.GetFileNameWithoutExtension(path));
        profile.Schema = CurrentSchema;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, RynthAiJsonContext.Default.BuffProfile));
        File.Move(tmp, path, overwrite: true);
    }

    public static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>An entry for a catalog spell, with its buffing key worked out.</summary>
    public static BuffProfileEntry EntryFor(SpellCatalogEntry e) => new()
    {
        SpellId = e.Id,
        Name = e.Name,
        Key = SpellCatalog.BuffKeyFor(e),
        FamilyId = e.FamilyId,
        School = e.School,
        Target = e.Target,
        Level = e.Level,
        Enabled = true,
    };

    /// <summary>True when an entry with the same buffing key is already in the profile.</summary>
    public static bool ContainsKey(BuffProfile p, string key)
    {
        foreach (var e in p.Entries)
            if (e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Catalog spell for one of buffing's built-in base names ("Strength Self", "Blade Bane",
    /// "Blood Drinker Self"), used to start a profile from the built-in list. Null when the
    /// catalog has no tier ladder for it.
    /// </summary>
    public static SpellCatalogEntry? CatalogSpellForBaseName(string baseName)
    {
        string[] candidates =
        {
            baseName + " I", "Aura of " + baseName + " I", baseName + " II", "Aura of " + baseName + " II",
            baseName, "Incantation of " + baseName,
        };
        foreach (string c in candidates)
        {
            var e = SpellCatalog.GetByName(c);
            if (e != null) return e;
        }
        return null;
    }

    /// <summary>Repairs nulls, blank keys and the name after loading or before saving.</summary>
    public static void Normalize(BuffProfile p, string fallbackName)
    {
        p.Name = string.IsNullOrWhiteSpace(p.Name) ? fallbackName : p.Name.Trim();
        p.Notes ??= "";
        p.Entries ??= new List<BuffProfileEntry>();
        p.Entries.RemoveAll(e => e == null || (e.SpellId == 0 && string.IsNullOrWhiteSpace(e.Key)));
        foreach (var e in p.Entries)
        {
            e.Name ??= "";
            e.School ??= "";
            e.Target ??= "";
            e.Key ??= "";
            // Hand-edited files may hold only a spell id: fill the rest from the catalog.
            var c = e.SpellId != 0 ? SpellCatalog.Get(e.SpellId) : null;
            if (c != null)
            {
                if (e.Name.Length == 0) e.Name = c.Name;
                if (e.Key.Length == 0) e.Key = SpellCatalog.BuffKeyFor(c);
                if (e.FamilyId == 0) e.FamilyId = c.FamilyId;
                if (e.School.Length == 0) e.School = c.School;
                if (e.Target.Length == 0) e.Target = c.Target;
                if (e.Level == 0) e.Level = c.Level;
            }
            if (e.Key.Length == 0) e.Key = SpellCatalog.StripTier(e.Name);
        }
    }

    /// <summary>Strips characters Windows forbids in file names; "Default" when nothing is left.</summary>
    public static string SanitizeFileName(string name)
    {
        string t = (name ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) t = t.Replace(c, '_');
        if (t.Length > 80) t = t.Substring(0, 80);
        return t.Length == 0 ? "Default" : t;
    }
}
