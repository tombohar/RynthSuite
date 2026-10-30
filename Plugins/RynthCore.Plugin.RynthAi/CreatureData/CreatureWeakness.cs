using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Which damage elements hurt a monster most. ACE never sends a creature's resists on
/// appraisal (ResistSlash 64 … ResistNether 166 aren't assessment properties), so RynthAi
/// ships them from the Aelrynth world database, keyed on the wcid (in CreateObject):
///   Data\creature_weakness.json — ops/overnight/creature-weakness.json; loaded once, lazily.
///                          A copy in C:\Games\RynthSuite\RynthAi\Data wins over the built-in one.
///   creature_resists.tsv — the same rows from ops/rynth-creature-resists.py; every creature with resist data, by wcid (name must match, so a
///                          custom monster that reuses a wcid on another server isn't mistaken)
///   creature_types.txt   — per creature type, elements ranked by the type's average resists,
///                          plus name keywords: the fallback for custom monsters.
/// A copy of creature_types.txt in C:\Games\RynthSuite\RynthAi\CreatureData overrides lines
/// by type id. Multipliers are damage taken: 1.0 = full, 0.5 = half, so higher = weaker.
/// </summary>
internal static class CreatureWeakness
{
    public static readonly string[] Elements = { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Acid", "Lightning", "Nether" };

    public sealed class Ranking
    {
        /// <summary>Most damage first. Mult is NaN when only the order is known (learned).</summary>
        public List<(string Element, double Mult)> Order = new();
        /// <summary>"server data", "learned", "Olthoi type", …</summary>
        public string Source = "";

        /// <summary>"Bludgeon 1.0, Pierce 1.0, Fire 0.75" (first <paramref name="n"/>).</summary>
        public string Describe(int n = 3)
        {
            var parts = new List<string>();
            for (int i = 0; i < Order.Count && parts.Count < n; i++)
                parts.Add(double.IsNaN(Order[i].Mult) ? Order[i].Element
                    : Order[i].Element + " " + Order[i].Mult.ToString("0.0#", CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }
    }

    private sealed class TypeInfo
    {
        public int Id;
        public string Name = "";
        public List<(string Element, double Mult)> Order = new();
    }

    private static readonly object Sync = new();
    private static bool _loaded;
    private static readonly Dictionary<uint, (string Name, int Type, double[] Mult)> ByWcid = new();
    private static readonly Dictionary<string, uint> ByName = new(StringComparer.OrdinalIgnoreCase);   // first wcid per name
    private static readonly Dictionary<int, TypeInfo> Types = new();
    private static readonly List<(string Keyword, int Type)> Keywords = new();   // longest first

    /// <summary>
    /// The elements that hurt this monster most, or null when nothing is known. Order of
    /// sources: the server table (wcid + name), then what the Damage tab learned
    /// (<paramref name="learnedBest"/>) ahead of the creature-type ranking, then the
    /// UtilityBelt damage seed (<paramref name="seeded"/>, best first) ahead of the type
    /// ranking, then the type ranking alone. <paramref name="creatureType"/> 0 = unknown
    /// (the name keywords decide).
    /// </summary>
    /// <param name="seeded">
    /// Damage types other players landed best on this monster (Monster Editor "Import UB
    /// damage insights"), best first; null or empty = no seed. It reflects what players
    /// owned as much as the monster's weakness, so it never outranks the server table or
    /// this character's learned element; it only beats the generic creature-type average.
    /// </param>
    public static Ranking? Rank(uint wcid, string? name, int creatureType, string? learnedBest,
        IReadOnlyList<string>? seeded = null)
    {
        EnsureLoaded();
        name ??= "";
        // No wcid (a meta asking by name): the first creature of that name in the table.
        if (wcid == 0 && name.Length > 0 && ByName.TryGetValue(name, out uint byName)) wcid = byName;
        if (wcid != 0 && ByWcid.TryGetValue(wcid, out var row)
            && (name.Length == 0 || row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            var r = new Ranking { Source = "server data" };
            r.Order = Ordered(row.Mult);
            return r;
        }

        TypeInfo? type = FindType(creatureType, name);
        string learned = Normalize(learnedBest);
        if (learned.Length > 0)
        {
            var r = new Ranking { Source = type != null ? $"learned, then {Pretty(type.Name)} type" : "learned" };
            r.Order.Add((learned, double.NaN));
            if (type != null)
                foreach (var e in type.Order)
                    if (!e.Element.Equals(learned, StringComparison.OrdinalIgnoreCase)) r.Order.Add(e);
            return r;
        }
        Ranking? seed = SeedRanking(seeded, type);
        if (seed != null)
            return seed;
        if (type != null)
            return new Ranking { Source = Pretty(type.Name) + " type", Order = new(type.Order) };
        return null;
    }

    /// <summary>
    /// The seed's elements (normalised, de-duplicated, no multiplier: only the order is known),
    /// then the creature type's remaining elements. Null when the seed names no element.
    /// </summary>
    private static Ranking? SeedRanking(IReadOnlyList<string>? seeded, TypeInfo? type)
    {
        if (seeded == null || seeded.Count == 0) return null;
        var r = new Ranking();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in seeded)
        {
            string e = Normalize(raw);
            if (e.Length > 0 && seen.Add(e)) r.Order.Add((e, double.NaN));
        }
        if (r.Order.Count == 0) return null;
        r.Source = type != null ? $"UB seed, then {Pretty(type.Name)} type" : "UB seed";
        if (type != null)
            foreach (var e in type.Order)
                if (seen.Add(e.Element)) r.Order.Add(e);
        return r;
    }

    /// <summary>
    /// The creature type the world-database table gives this monster (0 = not in it). Same match
    /// as <see cref="Rank"/>: the wcid's row when its name agrees (or no name is given); no wcid,
    /// the first row of that name. Never guessed from name keywords.
    /// </summary>
    public static int TableCreatureType(uint wcid, string? name)
    {
        EnsureLoaded();
        name ??= "";
        if (wcid == 0 && name.Length > 0 && ByName.TryGetValue(name, out uint byName)) wcid = byName;
        if (wcid != 0 && ByWcid.TryGetValue(wcid, out var row)
            && (name.Length == 0 || row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return row.Type > 0 ? row.Type : 0;
        return 0;
    }

    /// <summary>Damage multiplier for one element from the server table or the type average; false when unknown.</summary>
    public static bool TryGetMultiplier(uint wcid, string? name, int creatureType, string element, out double mult)
    {
        mult = 1.0;
        Ranking? r = Rank(wcid, name, creatureType, null);
        if (r == null) return false;
        string e = Normalize(element);
        foreach (var x in r.Order)
            if (x.Element.Equals(e, StringComparison.OrdinalIgnoreCase) && !double.IsNaN(x.Mult))
            {
                mult = x.Mult;
                return true;
            }
        return false;
    }

    /// <summary>Element names as the rest of RynthAi spells them ("Electric" → "Lightning"); "" when not an element.</summary>
    public static string Normalize(string? element)
    {
        if (string.IsNullOrWhiteSpace(element)) return "";
        string t = element.Trim();
        if (t.Equals("Electric", StringComparison.OrdinalIgnoreCase)) return "Lightning";
        if (t.Equals("Blade", StringComparison.OrdinalIgnoreCase)) return "Slash";
        foreach (string e in Elements)
            if (e.Equals(t, StringComparison.OrdinalIgnoreCase)) return e;
        return "";
    }

    private static string Pretty(string typeName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in typeName)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<(string, double)> Ordered(double[] mult)
    {
        var idx = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7 };
        idx.Sort((a, b) =>
        {
            int c = Math.Round(mult[b], 3).CompareTo(Math.Round(mult[a], 3));
            return c != 0 ? c : a.CompareTo(b);
        });
        var list = new List<(string, double)>(8);
        foreach (int i in idx) list.Add((Elements[i], mult[i]));
        return list;
    }

    private static TypeInfo? FindType(int creatureType, string name)
    {
        if (creatureType > 0 && Types.TryGetValue(creatureType, out var t)) return t;
        if (name.Length == 0) return null;
        foreach (var (kw, type) in Keywords)
            if (ContainsWord(name, kw) && Types.TryGetValue(type, out var k)) return k;
        return null;
    }

    private static bool ContainsWord(string text, string word)
    {
        int i = 0;
        while ((i = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = i == 0 || !char.IsLetter(text[i - 1]);
            int end = i + word.Length;
            bool endOk = end >= text.Length || !char.IsLetter(text[end]);
            if (startOk && endOk) return true;
            i = end;
        }
        return false;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loaded) return;
            try
            {
                foreach (string line in ReadResource("creature_resists.tsv"))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 11 || !uint.TryParse(f[0], out uint wcid)) continue;
                    var m = new double[8];
                    for (int i = 0; i < 8; i++)
                        m[i] = double.TryParse(f[3 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 1.0;
                    int.TryParse(f[2], out int ct);
                    ByWcid[wcid] = (f[1], ct, m);
                    ByName.TryAdd(f[1], wcid);
                }

                // The weakness table (wcid -> multipliers), generated from the world database
                // (ops/overnight/creature-weakness.json). A copy in the RynthAi folder wins over
                // the one built into the DLL, so the table can be refreshed without a rebuild.
                // Its rows replace the TSV's by wcid.
                string? json = null;
                try { if (File.Exists(WeaknessFile)) json = File.ReadAllText(WeaknessFile); } catch { json = null; }
                json ??= ReadResourceText("Data.creature_weakness.json", rooted: true);
                if (!string.IsNullOrEmpty(json)) LoadWeaknessJson(json);

                foreach (string line in ReadResource("creature_types.txt")) ParseTypeLine(line);
                string user = Path.Combine(CreatureProfileStore.Folder, "creature_types.txt");
                if (File.Exists(user))
                    foreach (string line in File.ReadAllLines(user)) ParseTypeLine(line);
            }
            catch { }

            Keywords.Sort((a, b) => b.Keyword.Length.CompareTo(a.Keyword.Length));
            _loaded = true;
        }
    }

    // id|Type|keywords|Element mult,Element mult,...
    private static void ParseTypeLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] == '#') return;
        string[] f = line.Split('|');
        if (f.Length < 4 || !int.TryParse(f[0], out int id)) return;
        var info = new TypeInfo { Id = id, Name = f[1].Trim() };
        foreach (string part in f[3].Split(','))
        {
            string[] p = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            string e = Normalize(p[0]);
            if (e.Length == 0) continue;
            double m = p.Length > 1 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
            info.Order.Add((e, m));
        }
        if (info.Order.Count == 0) return;
        Types[id] = info;   // a later line (the user's file) replaces the shipped one
        Keywords.RemoveAll(k => k.Type == id);
        foreach (string kw in f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Keywords.Add((kw, id));
    }

    /// <summary>The on-disk copy of the weakness table next to RynthAi (optional; the DLL has one built in).</summary>
    internal static readonly string WeaknessFile = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "Data", "creature_weakness.json");

    // JSON keys → Elements index (the table spells Lightning "electric").
    private static readonly (string Key, int Index)[] JsonKeys =
    {
        ("slash", 0), ("pierce", 1), ("bludgeon", 2), ("fire", 3), ("cold", 4), ("acid", 5),
        ("electric", 6), ("lightning", 6), ("nether", 7),
    };

    /// <summary>
    /// {"creatures": {"&lt;wcid&gt;": {"slash": 0.75, ..., "electric": 0.25, "nether": 1.0, "name": "..."}}}.
    /// A missing element is 1.0 (normal damage). Returns the rows read.
    /// </summary>
    internal static int LoadWeaknessJson(string json)
    {
        int n = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("creatures", out var creatures)
                || creatures.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
            foreach (var c in creatures.EnumerateObject())
            {
                if (!uint.TryParse(c.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wcid) || wcid == 0) continue;
                if (c.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var m = new double[8];
                for (int i = 0; i < 8; i++) m[i] = 1.0;
                string name = "";
                int type = 0;
                foreach (var p in c.Value.EnumerateObject())
                {
                    if (p.NameEquals("name")) { name = p.Value.GetString() ?? ""; continue; }
                    if (p.NameEquals("type") && p.Value.TryGetInt32(out int t)) { type = t; continue; }
                    if (p.Value.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                    foreach (var (key, idx) in JsonKeys)
                        if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) { m[idx] = p.Value.GetDouble(); break; }
                }
                if (type == 0 && ByWcid.TryGetValue(wcid, out var old)) type = old.Type;
                if (name.Length == 0 && ByWcid.TryGetValue(wcid, out var old2)) name = old2.Name;
                ByWcid[wcid] = (name, type, m);
                if (name.Length > 0) ByName.TryAdd(name, wcid);
                n++;
            }
        }
        catch { }
        return n;
    }

    /// <summary>
    /// A ranking from a creature's own appraisal resist floats (ResistSlash 64 … ResistElectric
    /// 70, ResistNether 166), when the server sent any. Stock ACE doesn't mark them as assessment
    /// properties, so this is usually null and the table decides. Missing elements count 1.0.
    /// </summary>
    public static Ranking? FromAppraisal(double?[] mult)
    {
        if (mult == null || mult.Length < 8) return null;
        bool any = false;
        var m = new double[8];
        for (int i = 0; i < 8; i++)
        {
            if (mult[i] is double v && v >= 0 && v < 10) { m[i] = v; any = true; }
            else m[i] = 1.0;
        }
        return any ? new Ranking { Source = "appraisal", Order = Ordered(m) } : null;
    }

    private static string? ReadResourceText(string file, bool rooted)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string res = "RynthCore.Plugin.RynthAi." + (rooted ? file : "CreatureData." + file);
            using Stream? s = asm.GetManifestResourceStream(res);
            if (s == null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    private static IEnumerable<string> ReadResource(string file)
    {
        var asm = Assembly.GetExecutingAssembly();
        using Stream? s = asm.GetManifestResourceStream("RynthCore.Plugin.RynthAi.CreatureData." + file);
        if (s == null) yield break;
        using var reader = new StreamReader(s);
        string? line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }
}
