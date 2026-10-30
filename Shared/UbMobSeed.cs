using System.Collections.Generic;

namespace RynthCore.CreatureSeed;

/// <summary>
/// Monster damage-type seed derived from UtilityBelt's <c>mob_damage_insights.ldb</c>.
/// Written by the Monster Editor ("Import UB damage insights"); read (never written)
/// by the RynthAi plugin as a fallback for the "Auto" damage type when a monster
/// has no appraised resists yet.
/// File: <c>&lt;SuiteDir&gt;\RynthAi\CreatureData\ub-mob-seed.json</c>.
/// Compiled into both projects so the schema cannot drift; plain properties with
/// defaults keep it source-generator (NativeAOT) friendly.
/// </summary>
public sealed class UbMobSeedFile
{
    /// <summary>Current schema version written by the importer.</summary>
    public const int CurrentVersion = 1;

    /// <summary>File name inside <c>RynthAi\CreatureData</c>.</summary>
    public const string FileName = "ub-mob-seed.json";

    /// <summary>Schema version of this file; readers ignore files with a newer major version.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>UTC time the importer produced the file (ISO-8601).</summary>
    public string GeneratedUtc { get; set; } = string.Empty;

    /// <summary>Most common UB world name across the imported rows (e.g. "InfiniteLeaftide").</summary>
    public string World { get; set; } = string.Empty;

    /// <summary>Hits a damage type needs before it is ranked at all.</summary>
    public int MinHitsPerType { get; set; }

    /// <summary>Source databases that contributed rows (full paths, for traceability).</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>One entry per monster with at least two comparable damage types.</summary>
    public List<UbMobSeedEntry> Entries { get; set; } = new();
}

/// <summary>Seeded damage-type ranking for one monster.</summary>
public sealed class UbMobSeedEntry
{
    /// <summary>Weenie class id; 0 for name-only rows imported from Mag-Tools.</summary>
    public uint Wcid { get; set; }

    /// <summary>Last display name UB saw for this monster.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"ub" (live UB wcid rows) or "magtools" (name-keyed Mag-Tools XML imports).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>All hits UB recorded against this monster (every type, including unranked ones).</summary>
    public long TotalHits { get; set; }

    /// <summary>Ranked damage types, best first. Only types meeting the hit threshold are listed.</summary>
    public List<UbMobSeedElement> Elements { get; set; } = new();
}

/// <summary>Observed outgoing-damage statistics for one damage type against one monster.</summary>
public sealed class UbMobSeedElement
{
    /// <summary>RynthAi element name: Slash, Pierce, Bludgeon, Fire, Cold, Lightning, Acid or Nether.</summary>
    public string Element { get; set; } = string.Empty;

    /// <summary>Hits recorded with this damage type.</summary>
    public int Hits { get; set; }

    /// <summary>Average damage per hit with this damage type.</summary>
    public double AvgDamage { get; set; }

    /// <summary>Largest single hit (0 for Mag-Tools rows, which do not record it).</summary>
    public long MaxHit { get; set; }

    /// <summary>Relative effectiveness 0..1 (1 = best type for this monster); used for the ranking.</summary>
    public double Score { get; set; }
}
