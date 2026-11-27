// ============================================================================
//  RynthAi - AcMaps/AcMapIndex.cs
//  The player's own ACMAPS dungeon map pack, indexed (2026-11-26, a player's
//  request: show the ACMAPS maps in game, as GoArrow did). Nothing here ships
//  a map: the player points RynthAi at the folder they already have.
//
//  What a pack looks like (GoArrow's "Dungeon Map Cache" folder, read from
//  GoArrow's MIT-licensed source, Huds/DungeonHud.cs):
//    - one image per dungeon, named by the dungeon's four-hex landblock, the
//      top half of the /loc cell id: "02CA.gif" (GoArrow fetched
//      www.acmaps.com/maps/{0:X4}.gif). PNG and JPG are taken too, and names
//      like "0x02CA.png", "02CA - Mosswart Maze.gif" or "02CA0100.gif";
//    - "dungeons.txt" (ACMAPS' dungeon_ids.csv): "02CA;Mosswart Maze", one
//      dungeon a line, the landblock in hex then the name;
//    - optionally a GoArrow locations database (data_cod.xml, GAlocations.xml):
//      <location> records whose <type>Dungeon</type> ones carry <dungeon_id>
//      (the same landblock), <name>, <restrictions>, <description> and the
//      entrance's <latitude>/<longitude> (north and west negative).
//  An image whose name has no landblock is matched to a name from those files.
//
//  Pure apart from Scan (the disk listing); tests feed Build file names and text.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.AcMaps;

/// <summary>One map image: its dungeon's landblock (0 = not known), name and the file, relative to the folder.</summary>
internal sealed record AcMapEntry(ushort Landblock, string Name, string RelativePath, string Notes)
{
    /// <summary>"Mosswart Maze (02CA)", or the file name when nothing names it.</summary>
    public string Label => Landblock != 0
        ? (Name.Length > 0 ? $"{Name} ({Landblock:X4})" : $"Dungeon {Landblock:X4}")
        : (Name.Length > 0 ? Name : Path.GetFileName(RelativePath));
}

internal sealed class AcMapIndex
{
    /// <summary>The image types WIC decodes that map packs use.</summary>
    internal static readonly string[] ImageExtensions = { ".gif", ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>The index files read (a dungeon list or a locations database).</summary>
    internal static readonly string[] IndexExtensions = { ".txt", ".csv", ".xml" };

    /// <summary>Scan limits: a pack is a few hundred images; a wrong folder (C:\) must not stall.</summary>
    internal const int MaxFiles = 20000, MaxDepth = 4;
    internal const long MaxIndexFileBytes = 16L * 1024 * 1024;

    public static readonly AcMapIndex Empty = new(string.Empty, new List<AcMapEntry>(), new Dictionary<ushort, string>(),
        new List<string>(), new List<string>());

    public string Folder { get; }
    /// <summary>Every map, by label.</summary>
    public IReadOnlyList<AcMapEntry> Maps { get; }
    /// <summary>Dungeon names by landblock, from the index files.</summary>
    public IReadOnlyDictionary<ushort, string> Names { get; }
    /// <summary>The index files that named at least one dungeon (relative paths).</summary>
    public IReadOnlyList<string> IndexFiles { get; }
    /// <summary>What went wrong reading the folder (shown in the window).</summary>
    public IReadOnlyList<string> Problems { get; }

    private readonly Dictionary<ushort, List<AcMapEntry>> _byLandblock = new();

    private AcMapIndex(string folder, List<AcMapEntry> maps, Dictionary<ushort, string> names, List<string> indexFiles, List<string> problems)
    {
        Folder = folder;
        Maps = maps;
        Names = names;
        IndexFiles = indexFiles;
        Problems = problems;
        foreach (AcMapEntry e in maps)
        {
            if (e.Landblock == 0) continue;
            if (!_byLandblock.TryGetValue(e.Landblock, out var list)) _byLandblock[e.Landblock] = list = new List<AcMapEntry>();
            list.Add(e);
        }
    }

    /// <summary>The maps of a dungeon (most packs have one; a few have extra parts), first by file name.</summary>
    public IReadOnlyList<AcMapEntry> ForLandblock(ushort landblock) =>
        _byLandblock.TryGetValue(landblock, out var list) ? list : (IReadOnlyList<AcMapEntry>)Array.Empty<AcMapEntry>();

    public bool Has(ushort landblock) => _byLandblock.ContainsKey(landblock);

    /// <summary>The dungeon's name from the index files, or "".</summary>
    public string NameOf(ushort landblock) => Names.TryGetValue(landblock, out string? n) ? n : string.Empty;

    /// <summary>"123 maps, 456 dungeon names (dungeons.txt)".</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append(Maps.Count == 1 ? "1 map" : $"{Maps.Count} maps");
        sb.Append(", ").Append(Names.Count == 1 ? "1 dungeon name" : $"{Names.Count} dungeon names");
        if (IndexFiles.Count > 0) sb.Append(" (").Append(string.Join(", ", IndexFiles)).Append(')');
        return sb.ToString();
    }

    // ── Building ─────────────────────────────────────────────────────────

    /// <summary>
    /// The index of <paramref name="folder"/> from its files (<paramref name="relativeFiles"/>, relative paths)
    /// and <paramref name="readText"/> (an index file's text by relative path, null when it can't be read).
    /// </summary>
    public static AcMapIndex Build(string folder, IEnumerable<string> relativeFiles, Func<string, string?> readText)
    {
        var problems = new List<string>();
        var names = new Dictionary<ushort, string>();
        var notes = new Dictionary<ushort, string>();
        var indexFiles = new List<string>();
        var images = new List<string>();
        foreach (string rel in relativeFiles)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ImageExtensions.Contains(ext)) images.Add(rel);
            else if (IndexExtensions.Contains(ext))
            {
                string? text = readText(rel);
                if (text == null) { problems.Add($"Couldn't read {rel}."); continue; }
                int n = ext == ".xml" || text.Contains("<location", StringComparison.OrdinalIgnoreCase)
                    ? ParseLocationsXml(text, names, notes)
                    : ParseDungeonList(text, names);
                if (n > 0) indexFiles.Add(rel);
            }
        }

        // Names without a landblock in their file name: matched by dungeon name.
        var byName = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var (lb, name) in names)
        {
            string key = NameKey(name);
            if (key.Length > 0 && !byName.ContainsKey(key)) byName[key] = lb;
        }

        var maps = new List<AcMapEntry>(images.Count);
        foreach (string rel in images.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.GetFileNameWithoutExtension(rel);
            ushort lb;
            string name;
            // A whole file name that is a listed dungeon wins ("Dead End.png" is a name, not landblock DEAD).
            if (byName.TryGetValue(NameKey(file), out ushort named) && ParseLandblock(file) == 0)
            {
                lb = named;
                name = names[named];
            }
            else if (TryLandblockFromFileName(file, out lb, out string rest))
                name = names.TryGetValue(lb, out string? n) ? n : rest;
            else
            {
                name = file.Trim();
                lb = byName.TryGetValue(NameKey(name), out ushort found) ? found : (ushort)0;
                if (lb != 0 && names.TryGetValue(lb, out string? listed)) name = listed;
            }
            maps.Add(new AcMapEntry(lb, name, rel, lb != 0 && notes.TryGetValue(lb, out string? note) ? note : string.Empty));
        }
        maps.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        if (images.Count == 0) problems.Add("No map images (GIF, PNG or JPG) in this folder or the folders in it.");
        else if (names.Count == 0) problems.Add("No dungeon list (dungeons.txt) found: maps are shown by their landblock.");
        return new AcMapIndex(folder, maps, names, indexFiles, problems);
    }

    /// <summary>
    /// Lists <paramref name="folder"/> (and the folders in it, <see cref="MaxDepth"/> deep, <see cref="MaxFiles"/>
    /// files at most) and builds the index. Any thread; never throws.
    /// </summary>
    public static AcMapIndex Scan(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return new AcMapIndex(folder ?? string.Empty, new List<AcMapEntry>(), new Dictionary<ushort, string>(),
                    new List<string>(), new List<string> { "The folder isn't there." });
            string root = Path.GetFullPath(folder);
            var files = new List<string>();
            bool truncated = false;
            Walk(root, root, 0, files, ref truncated);
            AcMapIndex index = Build(root, files, rel =>
            {
                try
                {
                    string full = Path.Combine(root, rel);
                    if (new FileInfo(full).Length > MaxIndexFileBytes) return null;
                    return File.ReadAllText(full, Encoding.UTF8);
                }
                catch { return null; }
            });
            if (truncated) ((List<string>)index.Problems).Add($"Stopped after {MaxFiles} files: is this the map folder?");
            return index;
        }
        catch (Exception ex)
        {
            return new AcMapIndex(folder ?? string.Empty, new List<AcMapEntry>(), new Dictionary<ushort, string>(),
                new List<string>(), new List<string> { "Couldn't read the folder: " + ex.Message });
        }
    }

    private static void Walk(string root, string dir, int depth, List<string> files, ref bool truncated)
    {
        IEnumerable<string> here;
        try { here = Directory.EnumerateFiles(dir); }
        catch { return; }
        foreach (string f in here)
        {
            if (files.Count >= MaxFiles) { truncated = true; return; }
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ImageExtensions.Contains(ext) || IndexExtensions.Contains(ext)) files.Add(Path.GetRelativePath(root, f));
        }
        if (depth >= MaxDepth) return;
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir); }
        catch { return; }
        foreach (string d in subs)
        {
            if (files.Count >= MaxFiles) { truncated = true; return; }
            Walk(root, d, depth + 1, files, ref truncated);
        }
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    private static readonly Regex FileNameRx = new(@"^(?:0x)?([0-9A-Fa-f]{4})(?:[0-9A-Fa-f]{4})?(?![0-9A-Za-z])(.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// "02CA", "0x02CA", "02CA0100", "02CA - Mosswart Maze": the landblock and the rest as a name ("Mosswart Maze").
    /// False for a name that doesn't start with 4 (or 8) hex digits on their own ("Mosswart Maze", "02CAB").
    /// </summary>
    internal static bool TryLandblockFromFileName(string fileName, out ushort landblock, out string rest)
    {
        landblock = 0;
        rest = string.Empty;
        Match m = FileNameRx.Match(fileName.Trim());
        if (!m.Success) return false;
        landblock = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (landblock == 0) return false;
        rest = m.Groups[2].Value.Trim().TrimStart('-', '_', '.', ' ', '(').TrimEnd(')').Trim();
        return true;
    }

    /// <summary>A landblock written as "02CA", "0x02CA", "02CA0100" or "0x02CA0100"; 0 when it isn't one.</summary>
    internal static ushort ParseLandblock(string text)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if ((t.Length != 4 && t.Length != 8) || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return 0;
        return (ushort)(t.Length == 8 ? v >> 16 : v);
    }

    /// <summary>
    /// A dungeon list: "02CA;Mosswart Maze" a line (GoArrow's dungeons.txt, ACMAPS' dungeon_ids.csv); a comma or a
    /// tab works as the separator too, and "Mosswart Maze;02CA". Other lines are skipped. Returns the names added.
    /// </summary>
    internal static int ParseDungeonList(string text, Dictionary<ushort, string> names)
    {
        int added = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == '#') continue;
            char sep = line.Contains(';') ? ';' : line.Contains('\t') ? '\t' : ',';
            string[] parts = line.Split(sep);
            if (parts.Length < 2) continue;
            string a = Unquote(parts[0]), b = Unquote(parts[1]);
            ushort lb = ParseLandblock(a);
            string name = b;
            if (lb == 0) { lb = ParseLandblock(b); name = a; }
            if (lb == 0 || name.Length == 0 || ParseLandblock(name) != 0) continue;
            if (!names.ContainsKey(lb)) added++;
            names[lb] = name;
        }
        return added;
    }

    private static readonly Regex LocationRx = new(@"<location\b[^>]*>(.*?)</location>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// A GoArrow locations database: each &lt;location&gt; with a &lt;dungeon_id&gt; (or &lt;type&gt;Dungeon
    /// and an id) names its dungeon; its restrictions, entrance and description become the map's notes. A
    /// name the dungeon list already gave is kept. Returns the dungeons added.
    /// </summary>
    internal static int ParseLocationsXml(string text, Dictionary<ushort, string> names, Dictionary<ushort, string> notes)
    {
        int added = 0;
        foreach (Match m in LocationRx.Matches(text))
        {
            string body = m.Groups[1].Value;
            ushort lb = ParseLandblock(Tag(body, "dungeon_id"));
            if (lb == 0) continue;
            if (Tag(body, "retired").Equals("Y", StringComparison.OrdinalIgnoreCase)) continue;
            string name = Tag(body, "name");
            if (name.Length > 0 && !names.ContainsKey(lb)) { names[lb] = name; added++; }
            if (!notes.ContainsKey(lb))
            {
                string note = LocationNote(body);
                if (note.Length > 0) notes[lb] = note;
            }
        }
        return added;
    }

    /// <summary>"Entrance 25.2S, 19.4E. Level 40+. Quest(s): ...".</summary>
    private static string LocationNote(string body)
    {
        var parts = new List<string>();
        if (TryCoord(Tag(body, "latitude"), 'S', 'N', out string ns) && TryCoord(Tag(body, "longitude"), 'E', 'W', out string ew)
            && !(ns.StartsWith("0.0") && ew.StartsWith("0.0")))
            parts.Add($"Entrance {ns}, {ew}.");
        string restrictions = Tag(body, "restrictions");
        if (restrictions.Length > 0) parts.Add(restrictions.TrimEnd('.') + ".");
        string description = Tag(body, "description");
        if (description.Length > 0) parts.Add(description);
        return string.Join(" ", parts);
    }

    /// <summary>GoArrow's coordinates: south and east positive ("22.200" is 22.2S), north and west negative.</summary>
    internal static bool TryCoord(string text, char positive, char negative, out string coord)
    {
        coord = string.Empty;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
        coord = Math.Abs(v).ToString("0.0", CultureInfo.InvariantCulture) + (v < 0 ? negative : positive);
        return true;
    }

    private static string Tag(string body, string tag)
    {
        Match m = Regex.Match(body, "<" + tag + @"\b[^>]*>(.*?)</" + tag + ">",
            RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? Unescape(m.Groups[1].Value).Trim() : string.Empty;
    }

    private static string Unescape(string s)
    {
        if (s.StartsWith("<![CDATA[", StringComparison.Ordinal) && s.EndsWith("]]>", StringComparison.Ordinal)) return s[9..^3];
        if (s.IndexOf('&') < 0) return s;
        s = Regex.Replace(s, "&#(x?)([0-9A-Fa-f]+);", m =>
        {
            bool hex = m.Groups[1].Value.Length > 0;
            return int.TryParse(m.Groups[2].Value, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)
                   && c > 0 && c < 0x110000 ? char.ConvertFromUtf32(c) : string.Empty;
        });
        return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Replace("\"\"", "\"");
        return s.Trim();
    }

    /// <summary>A name for matching: letters and digits, lower case ("The Mosswart Maze!" is "themosswartmaze").</summary>
    internal static string NameKey(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ── Search ───────────────────────────────────────────────────────────

    /// <summary>The maps whose label has every word (any order, any case); every map for no words.</summary>
    public static List<AcMapEntry> Search(IReadOnlyList<AcMapEntry> maps, string words)
    {
        string[] w = words.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0) return maps.ToList();
        return maps.Where(m => w.All(x => m.Label.Contains(x, StringComparison.OrdinalIgnoreCase)
                                          || m.RelativePath.Contains(x, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
