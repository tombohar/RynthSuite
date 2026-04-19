using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace RynthCore.Plugin.RynthAi;

public static class SpellDatabase
{
    private static readonly Dictionary<int, string> _spellNames = new();
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
                    _spellNames[spellId] = columns[1].Trim();
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
