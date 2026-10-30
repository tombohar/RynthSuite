using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace RynthCore.Plugin.RynthAi;

public static class SpellDatabase
{
    private static readonly Dictionary<int, string> _spellNames = new();
    // The data's category (ACE SpellCategory, column 3) and power (difficulty, column 7) per id,
    // and the ids of each category. Read for wall spells (2026-10-08): they have no tier in their
    // names ("Slithering Flames"), so a wall is found by its category and ranked by its power.
    private static readonly Dictionary<int, (int Category, int Power)> _spellMeta = new();
    private static readonly Dictionary<int, List<int>> _idsByCategory = new();
    // How long an enchantment lasts, in seconds before Archmage's Endurance (column 6): the
    // debuff renewal times landed debuffs by it (2026-10-20). Absent or 0: no timer.
    private static readonly Dictionary<int, int> _durations = new();
    private static bool _loaded = false;

    private static readonly Dictionary<int, string> _builtinSpells = new Dictionary<int, string>
    {
        { 47, "Primary Portal Tie" }, { 48, "Primary Portal Recall" },
        { 157, "Summon Primary Portal I" }, { 158, "Summon Primary Portal II" },
        { 1634, "Portal Sending" }, { 1635, "Lifestone Recall" },
        { 1636, "Lifestone Sending" }, { 1637, "Summon Primary Portal III" },
        { 2023, "Recall the Sanctuary" }, { 2041, "Aerlinthe Recall" },
        { 2358, "Lyceum Recall" }, { 2644, "Lifestone Tie" },
        { 2645, "Portal Recall" }, { 2646, "Secondary Portal Tie" },
        { 2647, "Secondary Portal Recall" }, { 2648, "Summon Secondary Portal I" },
        { 2649, "Summon Secondary Portal II" }, { 2650, "Summon Secondary Portal III" },
        { 2813, "Mount Lethe Recall" }, { 2931, "Recall Aphus Lassel" },
        { 2941, "Ulgrim's Recall" }, { 2943, "Recall to the Singularity Caul" },
        { 3865, "Glenden Wood Recall" }, { 3929, "Rossu Morta Chapterhouse Recall" },
        { 3930, "Whispering Blade Chapterhouse Recall" }, { 4084, "Bur Recall" },
        { 4198, "Paradox-touched Olthoi Infested Area Recall" }, { 4213, "Colosseum Recall" },
        { 4907, "Celestial Hand Stronghold Recall" }, { 4908, "Eldrytch Web Stronghold Recall" },
        { 4909, "Radiant Blood Stronghold Recall" }, { 5175, "Facility Hub Recall" },
        { 5330, "Gear Knight Invasion Area Camp Recall" }, { 5541, "Lost City of Neftet Recall" },
        { 6150, "Rynthid Recall" }, { 6321, "Viridian Rise Recall" },
        { 6322, "Viridian Rise Great Tree Recall" },
    };

    public static bool IsLoaded => _loaded;

    public static void Load(Action<string>? log = null)
    {
        _spellNames.Clear();
        _spellMeta.Clear();
        _idsByCategory.Clear();
        _durations.Clear();
        _loaded = false;

        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourceName = "RynthCore.Plugin.RynthAi.Combat.SpellData.txt";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                log?.Invoke($"[RynthAi] ERROR: Could not find embedded resource: {resourceName}");
                return;
            }

            using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] columns = line.Split('\t');
                if (columns.Length >= 2 && int.TryParse(columns[0].Trim(), out int spellId))
                {
                    _spellNames[spellId] = columns[1].Trim();
                    if (columns.Length >= 7 && int.TryParse(columns[2].Trim(), out int category)
                        && int.TryParse(columns[6].Trim(), out int power))
                    {
                        _spellMeta[spellId] = (category, power);
                        if (!_idsByCategory.TryGetValue(category, out List<int>? ids))
                            _idsByCategory[category] = ids = new List<int>();
                        ids.Add(spellId);
                    }
                    if (columns.Length >= 6 && int.TryParse(columns[5].Trim(), out int seconds) && seconds > 0)
                        _durations[spellId] = seconds;
                }
            }

            _loaded = _spellNames.Count > 0;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[RynthAi] Spell Load Error: {ex.Message}");
        }
    }

    public static string GetSpellName(int spellId)
    {
        if (_spellNames.TryGetValue(spellId, out string? name)) return name;
        if (_builtinSpells.TryGetValue(spellId, out string? builtin)) return builtin;
        return $"Unknown Spell ({spellId})";
    }

    /// <summary>Spell family and difficulty (power). False when the spell isn't in SpellData.txt.</summary>
    public static bool TryGetSpellMeta(int spellId, out int family, out int difficulty)
    {
        if (_spellMeta.TryGetValue(spellId, out var meta))
        {
            family = meta.Category;
            difficulty = meta.Power;
            return true;
        }
        family = 0;
        difficulty = 0;
        return false;
    }

    /// <summary>True when the spell id is in the embedded table or the built-in recall list.</summary>
    public static bool HasSpell(int spellId) =>
        _spellNames.ContainsKey(spellId) || _builtinSpells.ContainsKey(spellId);

    public static string GetSpellNameOrId(int spellId)
    {
        if (_spellNames.TryGetValue(spellId, out string? name)) return name;
        if (_builtinSpells.TryGetValue(spellId, out string? builtin)) return builtin;
        return spellId.ToString();
    }

    /// <summary>
    /// Returns true if the spell exists in the database.
    /// TODO: Replace with real "is spell known" check from AC memory.
    /// </summary>
    public static bool IsSpellKnown(int spellId)
    {
        return _spellNames.ContainsKey(spellId) || _builtinSpells.ContainsKey(spellId);
    }

    /// <summary>The spell's category (ACE SpellCategory) and power from the data; false when the
    /// spell isn't in it.</summary>
    public static bool TryGetCategoryAndPower(int spellId, out int category, out int power)
    {
        if (_spellMeta.TryGetValue(spellId, out var m)) { category = m.Category; power = m.Power; return true; }
        category = 0; power = 0;
        return false;
    }

    /// <summary>How long the spell's enchantment lasts in seconds (before Archmage's Endurance), 0 when it has none.</summary>
    public static int DurationSeconds(int spellId) => _durations.TryGetValue(spellId, out int s) ? s : 0;

    /// <summary>Every spell id of a category (ACE SpellCategory), in data order.</summary>
    public static IReadOnlyList<int> IdsInCategory(int category)
        => _idsByCategory.TryGetValue(category, out List<int>? ids) ? ids : Array.Empty<int>();

    /// <summary>
    /// A spell's level by ACE's server formula (Spell.Level): the highest level whose minimum power
    /// it reaches, 1/50/100/150/200/250/300/400 for levels 1-8 (SpellFormula.MinPower). The tiered
    /// war lines have exactly those powers (Flame Bolt V 200, the tier-7 lore 300, Incantations
    /// 400; blasts +25), so it is the same tier limit the other shapes use. 0 = no power.
    /// </summary>
    public static int LevelFromPower(int power) => power switch
    {
        >= 400 => 8, >= 300 => 7, >= 250 => 6, >= 200 => 5, >= 150 => 4, >= 100 => 3, >= 50 => 2, >= 1 => 1, _ => 0,
    };

    /// <summary>Builds a name→id reverse map for SpellManager initialisation.</summary>
    public static Dictionary<string, int> BuildNameToIdMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _spellNames)
            map[kvp.Value] = kvp.Key;
        foreach (var kvp in _builtinSpells)
            map.TryAdd(kvp.Value, kvp.Key);
        return map;
    }

    /// <summary>
    /// Every id per name. Some names have several ids (Missile Weapon Mastery Self VI is
    /// 472/496/544, Light Weapon Mastery has five per tier), and BuildNameToIdMap keeps
    /// only the last, which a character may not know.
    /// </summary>
    public static Dictionary<string, List<int>> BuildNameToIdsMap()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _spellNames)
        {
            if (!map.TryGetValue(kvp.Value, out var ids)) map[kvp.Value] = ids = new List<int>();
            ids.Add(kvp.Key);
        }
        return map;
    }

    /// <summary>Looks up a spell ID by exact name (case-insensitive).</summary>
    public static int GetIdByName(string name)
    {
        foreach (var kvp in _spellNames)
            if (kvp.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        foreach (var kvp in _builtinSpells)
            if (kvp.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        return 0;
    }
}
