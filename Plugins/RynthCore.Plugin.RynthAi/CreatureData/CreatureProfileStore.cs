using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Thread-safe disk-backed store of CreatureProfile records.
/// Shared across all characters; engine writes, optional external editor reads.
/// Keyed by composite "name|wcid" so tier variants don't collide.
///
/// Per server (2026-10-03): wcids are only unique within one server's world database, and
/// servers' custom content reuses numbers, so each server keeps its own file:
///   C:\Games\RynthSuite\RynthAi\CreatureData\&lt;server&gt;\creatures.json
/// &lt;server&gt; is <see cref="DifficultyTier.ServerKey"/>: the folder that already holds this
/// server's data (by RynthCore's server id, its name or the world name), else the server's id,
/// else (no server known) the world name the server announced, made safe for a folder (the
/// SettingsProfiles rule). Folders made before 2026-10-21 are read where they are; nothing moves.
/// The shared file from before, CreatureData\creatures.json, is the legacy store: COPIED (not
/// moved) into a server's folder the first time that server is played, so every server starts
/// from exactly what it had - the same precedent as the per-server SettingsProfiles
/// (MigrateToServerFolder). It is only read and written while no server is known (before the
/// first tick of a login, or a server that announces no name), as before.
///
/// Difficulty tiers (a server plugin's, DifficultyTier): appraised max health above tier 0 goes to
/// <see cref="CreatureProfile.TierMaxHealth"/>; MaxHealth stays the tier-0 value. Everything
/// else in a profile is the same at every tier.
/// </summary>
internal sealed class CreatureProfileStore
{
    /// <summary>Where creatures.json lives (the offline tests point it at a scratch folder).</summary>
    internal static string Folder = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "CreatureData");
    private const string FileName = "creatures.json";

    private readonly object _lock = new();
    private readonly Dictionary<string, CreatureProfile> _byKey
        = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSavedJson = string.Empty;
    private bool _dirty;
    private string _serverKey = string.Empty;   // "" = the legacy shared file

    private string FilePath => FileFor(_serverKey);

    private static string FileFor(string serverKey) =>
        string.IsNullOrEmpty(serverKey) ? Path.Combine(Folder, FileName) : Path.Combine(Folder, serverKey, FileName);

    /// <summary>The server whose file is loaded ("" = the legacy shared file).</summary>
    public string ServerKey { get { lock (_lock) return _serverKey; } }

    /// <summary>
    /// The name, as on disk, of the per-server folder matching <paramref name="serverKey"/> (any case)
    /// that holds a creatures.json; null when there is none. Never throws.
    /// </summary>
    public static string? ExistingServerFolder(string serverKey)
    {
        if (string.IsNullOrWhiteSpace(serverKey)) return null;
        try
        {
            if (!Directory.Exists(Folder)) return null;
            foreach (string dir in Directory.EnumerateDirectories(Folder))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals(serverKey.Trim(), StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, FileName)))
                    return name;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Switch to <paramref name="serverKey"/>'s file (saving the current one first). The first
    /// time a server is seen its file is a copy of the legacy shared file. "" or the same server
    /// is a no-op. Returns a line for the log when a copy was made, else null.
    /// </summary>
    public string? SetServer(string serverKey)
    {
        serverKey = serverKey?.Trim() ?? string.Empty;
        string? note = null;
        lock (_lock)
        {
            if (serverKey.Length == 0 || string.Equals(serverKey, _serverKey, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        SaveIfDirty();
        try
        {
            string legacy = FileFor("");
            string target = FileFor(serverKey);
            if (!File.Exists(target) && File.Exists(legacy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(legacy, target);
                note = $"creature data is now kept per server: copied the shared creatures.json into {serverKey}";
            }
        }
        catch (Exception ex)
        {
            note = $"copying creatures.json into {serverKey} failed ({ex.Message}); starting {serverKey} empty";
        }
        lock (_lock) _serverKey = serverKey;
        Load();
        return note;
    }

    public void Load()
    {
        lock (_lock)
        {
            _byKey.Clear();
            _lastSavedJson = string.Empty;
            _dirty = false;
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
                    {
                        // Player characters appraised before e52ccc8 (wcid 1 / 4) are not
                        // creatures: dropped, and the next save writes the file without them.
                        if (kv.Value != null && PlayerWeenies.IsPlayerWcid(kv.Value.Wcid)) { _dirty = true; continue; }
                        _byKey[kv.Key] = kv.Value!;
                    }
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
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string json = JsonSerializer.Serialize(
                    _byKey, RynthAiJsonContext.Default.CreatureProfileDict);

                if (json == _lastSavedJson) return;

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Copy(tmp, path, overwrite: true);
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

    /// <summary>Lookup by wcid (for UI joins). Returns the first profile with this Wcid.</summary>
    public bool TryGetByWcid(uint wcid, out CreatureProfile? profile)
    {
        profile = null;
        if (wcid == 0) return false;
        lock (_lock)
        {
            foreach (var kv in _byKey)
            {
                if (kv.Value.Wcid == wcid) { profile = kv.Value; return true; }
            }
        }
        return false;
    }

    /// <summary>
    /// A profile's appraised max health at a difficulty tier (0 = the unscaled world, and every
    /// tier on servers without tiers); 0 when that tier hasn't been seen.
    /// </summary>
    public uint MaxHealthAt(CreatureProfile? profile, int difficulty)
    {
        if (profile == null) return 0;
        if (difficulty <= 0) return profile.MaxHealth;
        lock (_lock)
            return profile.TierMaxHealth != null && profile.TierMaxHealth.TryGetValue(difficulty, out uint hp) ? hp : 0;
    }

    /// <summary>
    /// Merge a new observation into the store. Fields with value 0 / empty are
    /// ignored so partial samples don't clobber better data. <paramref name="difficulty"/>
    /// above 0 (a server's scaled world or copy, DifficultyTier) files the max health under that tier
    /// and leaves the tier-0 vitals alone; the rest merges as always.
    /// </summary>
    public void Upsert(CreatureProfile sample, int difficulty = 0)
    {
        if (string.IsNullOrEmpty(sample.Name)) return;
        if (PlayerWeenies.IsPlayerWcid(sample.Wcid)) return;   // a player character, never creature data

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

            if (difficulty > 0)
            {
                // Health, skills and damage scale with the tier; only health is kept (kill prediction).
                if (sample.MaxHealth > 0)
                {
                    existing.TierMaxHealth ??= new Dictionary<int, uint>();
                    if (!existing.TierMaxHealth.TryGetValue(difficulty, out uint had) || sample.MaxHealth > had)
                        existing.TierMaxHealth[difficulty] = sample.MaxHealth;
                }
            }
            else
            {
                // Vitals: take the max observed (handles partial samples + minor variation).
                if (sample.MaxHealth > existing.MaxHealth) existing.MaxHealth = sample.MaxHealth;
                if (sample.MaxStamina > existing.MaxStamina) existing.MaxStamina = sample.MaxStamina;
                if (sample.MaxMana > existing.MaxMana) existing.MaxMana = sample.MaxMana;
            }

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
    /// The damage type the creature takes the most damage from: resists are damage multipliers
    /// (1.0 full, 0.5 half), so that's the HIGHEST value (this took the lowest until 2026-09-29).
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
        double best = double.MinValue;
        foreach (var pair in pairs)
        {
            if (pair.Item2 > best)
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
