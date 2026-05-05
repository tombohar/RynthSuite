using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Thread-safe disk-backed store of CreatureProfile records.
/// File: C:\Games\RynthSuite\RynthAi\CreatureData\creatures.json
/// Shared across all characters; engine writes, optional external editor reads.
/// Keyed by composite "name|wcid" so tier variants don't collide.
/// </summary>
internal sealed class CreatureProfileStore
{
    private const string Folder = @"C:\Games\RynthSuite\RynthAi\CreatureData";
    private const string FileName = "creatures.json";

    private readonly object _lock = new();
    private readonly Dictionary<string, CreatureProfile> _byKey
        = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSavedJson = string.Empty;
    private bool _dirty;

    private string FilePath => Path.Combine(Folder, FileName);

    public void Load()
    {
        lock (_lock)
        {
            _byKey.Clear();
            string path = FilePath;
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return;

                var dict = JsonSerializer.Deserialize(
                    json, RynthAiJsonContext.Default.CreatureProfileDict);

                if (dict != null)
                {
                    foreach (var kv in dict)
                        _byKey[kv.Key] = kv.Value;
                }
                _lastSavedJson = json;
            }
            catch
            {
                // Corrupt file — start fresh.
            }
        }
    }

    /// <summary>Persist if anything changed since last save. Cheap to call from a tick.</summary>
    public void SaveIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;

            try
            {
                Directory.CreateDirectory(Folder);
                string json = JsonSerializer.Serialize(
                    _byKey, RynthAiJsonContext.Default.CreatureProfileDict);

                if (json == _lastSavedJson) return;

                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Copy(tmp, FilePath, overwrite: true);
                try { File.Delete(tmp); } catch { }
                _lastSavedJson = json;
            }
            catch
            {
            }
        }
    }

    /// <summary>Lookup by exact composite key.</summary>
    public bool TryGet(string name, uint wcid, out CreatureProfile? profile)
    {
        lock (_lock)
        {
            return _byKey.TryGetValue(MakeKey(name, wcid), out profile);
        }
    }

    /// <summary>
    /// Lookup by name only. Prefers an exact-name match with the highest sample count.
    /// Returns false if no record matches.
    /// </summary>
    public bool TryGetByName(string name, out CreatureProfile? profile)
    {
        profile = null;
        if (string.IsNullOrEmpty(name)) return false;

        lock (_lock)
        {
            CreatureProfile? best = null;
            foreach (var kv in _byKey)
            {
                if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (best == null || kv.Value.Samples > best.Samples)
                        best = kv.Value;
                }
            }
            profile = best;
            return best != null;
        }
    }

    /// <summary>
    /// Merge a new observation into the store. Fields with value 0 / empty are
    /// ignored so partial samples don't clobber better data.
    /// </summary>
    public void Upsert(CreatureProfile sample)
    {
        if (string.IsNullOrEmpty(sample.Name)) return;

        lock (_lock)
        {
            string key = MakeKey(sample.Name, sample.Wcid);
            if (!_byKey.TryGetValue(key, out var existing))
            {
                existing = new CreatureProfile
                {
                    Name = sample.Name,
                    Wcid = sample.Wcid,
                };
                _byKey[key] = existing;
            }

            if (sample.CreatureType != 0) existing.CreatureType = sample.CreatureType;

            // Vitals: take the max observed (handles partial samples + minor variation).
            if (sample.MaxHealth > existing.MaxHealth) existing.MaxHealth = sample.MaxHealth;
            if (sample.MaxStamina > existing.MaxStamina) existing.MaxStamina = sample.MaxStamina;
            if (sample.MaxMana > existing.MaxMana) existing.MaxMana = sample.MaxMana;

            if (sample.ArmorLevel > 0) existing.ArmorLevel = sample.ArmorLevel;

            // Resists: overwrite when sample is plausibly a fresh appraisal (non-default).
            if (sample.ResistSlash != 1.0)    existing.ResistSlash    = sample.ResistSlash;
            if (sample.ResistPierce != 1.0)   existing.ResistPierce   = sample.ResistPierce;
            if (sample.ResistBludgeon != 1.0) existing.ResistBludgeon = sample.ResistBludgeon;
            if (sample.ResistFire != 1.0)     existing.ResistFire     = sample.ResistFire;
            if (sample.ResistCold != 1.0)     existing.ResistCold     = sample.ResistCold;
            if (sample.ResistAcid != 1.0)     existing.ResistAcid     = sample.ResistAcid;
            if (sample.ResistElectric != 1.0) existing.ResistElectric = sample.ResistElectric;

            if (sample.KnownSpellIds.Count > 0)
            {
                var seen = new HashSet<uint>(existing.KnownSpellIds);
                foreach (uint id in sample.KnownSpellIds)
                {
                    if (seen.Add(id))
                        existing.KnownSpellIds.Add(id);
                }
            }

            existing.Samples++;
            existing.LastSeen = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            _dirty = true;
        }
    }

    /// <summary>
    /// Returns the lowest resist (the creature's weakest damage type). 1.0 means "no data".
    /// </summary>
    public static (string Type, double Resist) GetWeakest(CreatureProfile p)
    {
        var pairs = new (string, double)[]
        {
            ("slash",    p.ResistSlash),
            ("pierce",   p.ResistPierce),
            ("bludgeon", p.ResistBludgeon),
            ("fire",     p.ResistFire),
            ("cold",     p.ResistCold),
            ("acid",     p.ResistAcid),
            ("electric", p.ResistElectric),
        };
        string bestType = "slash";
        double best = double.MaxValue;
        foreach (var pair in pairs)
        {
            if (pair.Item2 < best)
            {
                best = pair.Item2;
                bestType = pair.Item1;
            }
        }
        return (bestType, best);
    }

    private static string MakeKey(string name, uint wcid)
    {
        return wcid == 0
            ? name.Trim()
            : name.Trim() + "|" + wcid.ToString();
    }
}
