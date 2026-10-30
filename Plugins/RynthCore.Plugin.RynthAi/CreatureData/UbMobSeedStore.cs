using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RynthCore.CreatureSeed;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Read-only view of the UtilityBelt damage-type seed (<see cref="UbMobSeedFile"/>),
/// produced by the Monster Editor's "Import UB damage insights".
/// File: <c>&lt;SuiteDir&gt;\RynthAi\CreatureData\ub-mob-seed.json</c>.
/// <para>
/// Used only as a fallback for the "Auto" damage type when a monster has no appraised
/// resists: it ranks damage types by what players actually landed on that monster, which
/// reflects what they owned as much as the monster's weakness. Never written by the plugin
/// and never fed into the per-character <see cref="MonsterDamageStore"/>.
/// </para>
/// Lookups are made from the combat thread only; the lock guards the occasional reload.
/// </summary>
internal sealed class UbMobSeedStore
{
    /// <summary>Minimum time between file-change checks (the editor may rewrite the seed while playing).</summary>
    private static readonly TimeSpan ReloadCheckInterval = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly Dictionary<uint, UbMobSeedEntry> _byWcid = new();
    private readonly Dictionary<string, UbMobSeedEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedWriteUtc = DateTime.MinValue;
    private DateTime _nextCheckUtc = DateTime.MinValue;

    private static string FilePath =>
        Path.Combine(RynthInstallPaths.RynthAiDir, "CreatureData", UbMobSeedFile.FileName);

    /// <summary>Number of seeded monsters currently loaded (wcid + name-only).</summary>
    public int Count { get { lock (_lock) return _byWcid.Count + CountNameOnly(); } }

    /// <summary>Loads (or reloads) the seed when the file changed. Safe to call often.</summary>
    public void Load()
    {
        lock (_lock)
        {
            _nextCheckUtc = DateTime.UtcNow + ReloadCheckInterval;
            string path = FilePath;
            if (!File.Exists(path))
            {
                Clear();
                return;
            }

            DateTime writeUtc;
            try { writeUtc = File.GetLastWriteTimeUtc(path); }
            catch { return; }
            if (writeUtc == _loadedWriteUtc) return;

            try
            {
                var seed = JsonSerializer.Deserialize(File.ReadAllText(path), RynthAiJsonContext.Default.UbMobSeedFile);
                Clear();
                _loadedWriteUtc = writeUtc;
                if (seed == null || seed.Version > UbMobSeedFile.CurrentVersion) return;

                foreach (var e in seed.Entries)
                {
                    if (e.Elements == null || e.Elements.Count < 2) continue;
                    if (e.Wcid != 0) _byWcid[e.Wcid] = e;
                    IndexName(e);
                }
            }
            catch
            {
                // Corrupt or mid-write file: keep whatever was loaded before and retry on the next check.
            }
        }
    }

    /// <summary>
    /// Ranked damage types (best first) for a monster: exact wcid first, then name.
    /// Returns false when the seed has nothing comparable for it.
    /// </summary>
    public bool TryGetRanked(uint wcid, string? name, out IReadOnlyList<UbMobSeedElement> ranked)
    {
        lock (_lock)
        {
            // Checked under the lock: DateTime reads are not atomic on x86. Monitor is re-entrant.
            if (DateTime.UtcNow >= _nextCheckUtc) Load();

            UbMobSeedEntry? entry = null;
            if (wcid != 0) _byWcid.TryGetValue(wcid, out entry);
            if (entry == null && !string.IsNullOrWhiteSpace(name)) _byName.TryGetValue(name.Trim(), out entry);

            ranked = entry?.Elements ?? (IReadOnlyList<UbMobSeedElement>)Array.Empty<UbMobSeedElement>();
            return entry != null;
        }
    }

    /// <summary>
    /// Name index: a wcid entry beats a name-only one; among equals the most-hit entry wins
    /// (tier variants share a name, so the busiest variant is the best guess).
    /// </summary>
    private void IndexName(UbMobSeedEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Name)) return;
        string key = e.Name.Trim();
        if (_byName.TryGetValue(key, out var existing))
        {
            bool existingHasWcid = existing.Wcid != 0, newHasWcid = e.Wcid != 0;
            if (existingHasWcid && !newHasWcid) return;
            if (existingHasWcid == newHasWcid && existing.TotalHits >= e.TotalHits) return;
        }
        _byName[key] = e;
    }

    private int CountNameOnly()
    {
        int n = 0;
        foreach (var e in _byName.Values) if (e.Wcid == 0) n++;
        return n;
    }

    private void Clear()
    {
        _byWcid.Clear();
        _byName.Clear();
        _loadedWriteUtc = DateTime.MinValue;
    }
}
