using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Buff profiles (Spells window / Loaded Files → Buffs): when one is chosen, its enabled
/// spells replace the built-in buff list, in the profile's order. Each entry is a base name
/// ("Strength Self") resolved to the best tier the character knows like the built-in list;
/// spells without a tier ladder (server-custom or lore names) fall back to the best known
/// spell of the same ACE spell family.
/// </summary>
public partial class BuffManager
{
    // Re-checking the file's timestamp every call would hit the disk ~30x a second.
    private const int BuffProfileRecheckMs = 3000;

    private string _buffProfilePath = string.Empty;
    private DateTime _buffProfileWriteUtc;
    private long _buffProfileCheckedAt = long.MinValue / 2;
    private List<string>? _profileKeys;
    private readonly Dictionary<string, BuffProfileEntry> _profileByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Name of the buff profile in use, or null when buffing uses its built-in list.</summary>
    public string? ActiveBuffProfileName { get; private set; }

    /// <summary>Spells (enabled entries) in the active buff profile; 0 with no profile.</summary>
    public int ActiveBuffProfileCount => _profileKeys?.Count ?? 0;

    /// <summary>
    /// Follows <see cref="LegacyUiSettings.CurrentBuffProfilePath"/>: loads the profile when the
    /// path changes or the file is re-saved (the Spells window's Save), drops it when the path is
    /// cleared. A missing or unreadable file means the built-in list, with one chat line.
    /// </summary>
    private void SyncBuffProfile()
    {
        string path = _settings.CurrentBuffProfilePath ?? string.Empty;
        long now = Environment.TickCount64;
        bool pathChanged = !path.Equals(_buffProfilePath, StringComparison.OrdinalIgnoreCase);
        if (!pathChanged && now - _buffProfileCheckedAt < BuffProfileRecheckMs) return;
        _buffProfileCheckedAt = now;

        if (path.Length == 0)
        {
            if (pathChanged) ClearBuffProfile("[RynthAi] Buffing: built-in buff list.");
            _buffProfilePath = path;
            return;
        }

        DateTime writeUtc;
        try { writeUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { writeUtc = DateTime.MinValue; }
        if (!pathChanged && writeUtc == _buffProfileWriteUtc) return;

        _buffProfilePath = path;
        _buffProfileWriteUtc = writeUtc;
        SpellCatalog.EnsureLoaded(m => _host.Log(m));
        if (!BuffProfileStore.TryLoad(path, out BuffProfile profile, out string error))
        {
            ClearBuffProfile($"[RynthAi] Buff profile '{Path.GetFileNameWithoutExtension(path)}' could not be read ({error}) - using the built-in buff list.");
            return;
        }
        ApplyBuffProfile(profile);
        _host.WriteToChat($"[RynthAi] Buffing: profile '{profile.Name}' ({ActiveBuffProfileCount} spell(s)).", 5);
    }

    /// <summary>Uses <paramref name="profile"/>'s enabled entries as the buff list (tests call this directly).</summary>
    internal void ApplyBuffProfile(BuffProfile profile)
    {
        _profileByKey.Clear();
        var keys = new List<string>(profile.Entries.Count);
        foreach (var e in profile.Entries)
        {
            if (!e.Enabled || string.IsNullOrWhiteSpace(e.Key)) continue;
            // One cast per base name: two tiers of the same line would fight over one family.
            if (_profileByKey.ContainsKey(e.Key)) continue;
            _profileByKey[e.Key] = e;
            keys.Add(e.Key);
        }
        _profileKeys = keys;
        ActiveBuffProfileName = profile.Name;
    }

    private void ClearBuffProfile(string chatLine)
    {
        bool had = _profileKeys != null;
        _profileKeys = null;
        _profileByKey.Clear();
        ActiveBuffProfileName = null;
        if (had || chatLine.Contains("could not be read", StringComparison.Ordinal))
            _host.WriteToChat(chatLine, 5);
    }

    /// <summary>The active profile's base names, or null to use the built-in list.</summary>
    private List<string>? ProfileBuffList()
    {
        SyncBuffProfile();
        return _profileKeys is { Count: > 0 } keys ? new List<string>(keys) : null;
    }

    /// <summary>Casting skill for a buff: the catalog school for profile spells, else by name.</summary>
    internal AcSkillType SkillFor(string baseName) =>
        _profileByKey.TryGetValue(baseName, out var e) && e.School.Length > 0
            ? SpellCatalog.SkillForSchool(e.School)
            : SkillForBuff(baseName);

    /// <summary>
    /// Profile spells whose base name has no tier ladder: the best spell of the same family the
    /// character knows, capped by the buffing tier for its skill. Self and item spells win over
    /// Other ones at the same level. 0 when the spellbook snapshot is cold or nothing is known.
    /// </summary>
    private int ResolveProfileFallback(string baseName, AcSkillType skill)
    {
        if (!_profileByKey.TryGetValue(baseName, out var entry) || !_spellManager.IsKnownSnapshotWarm) return 0;

        int maxTier = _spellManager.GetHighestBuffSpellTier(skill);
        SpellCatalogEntry? best = null;
        foreach (var c in SpellCatalog.Family(entry.FamilyId))
        {
            if (!c.IsBuff || c.Level > maxTier || !_spellManager.IsKnownSpellId(c.Id)) continue;
            if (best == null || Rank(c) > Rank(best)) best = c;
        }
        if (best != null) return best.Id;
        // Family unknown to the catalog: the exact spell picked in the Spells window.
        return entry.SpellId != 0 && _spellManager.IsKnownSpellId(entry.SpellId) ? entry.SpellId : 0;

        static long Rank(SpellCatalogEntry c) =>
            (long)c.Level * 1_000_000 + (c.IsSelf || c.IsItemTarget ? 100_000 : 0) + c.Power;
    }
}
