// ============================================================================
//  RynthAi - MonsterStats/MonsterXpTable.cs
//  What a kill of each kind of monster gives and takes, learned and kept across
//  logins (2026-11-08): the experience per kill (the Outdoor Hunt's "Most XP"
//  choice reads it) and the time from the kill to its corpse, the death
//  animation (looting waits that long before moving on). The session's monster
//  stats start over at every login; this doesn't.
//
//  Per character, next to the character's settings and monster_damage.txt:
//    ...\SettingsProfiles\<server>\<character>\monster_xp.txt
//  one line per monster and number:
//    X|name|xpPerKill|kills        D|name|deathMs|kills
//  Each average is over the last AverageOver kills (a running average: the
//  first kills count fully, then each new one moves it by 1/AverageOver), so a
//  level's XP change or a server's bonus weekend shows within a few dozen kills.
//  Plain text, no System.Text.Json (plugin types and STJ leak across reloads).
//
//  Pump thread writes; the hunt and the looter read on the pump thread too; a
//  lock anyway.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

internal sealed class MonsterXpTable
{
    public const string FileName = "monster_xp.txt";
    /// <summary>The averages follow about this many of the latest kills.</summary>
    public const int AverageOver = 20;
    /// <summary>Kinds of monster kept; past it the one with the fewest kills goes.</summary>
    public const int MaxMonsters = 2000;

    private sealed class Entry
    {
        public string Name = "";
        public double Avg;        // XP per kill
        public int Kills;         // kills with XP counted
        public double DeathAvg;   // ms from the kill to its corpse
        public int DeathKills;    // kills whose corpse was timed
    }

    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private string _path = string.Empty;
    private bool _dirty;

    /// <summary>The file it reads and writes ("" while no character is set).</summary>
    public string FilePath { get { lock (_lock) return _path; } }

    /// <summary>Kinds of monster known.</summary>
    public int Count { get { lock (_lock) return _byName.Count; } }

    /// <summary>Points the table at a character's folder and loads its file ("" detaches it, empty).</summary>
    public void SetCharacter(string? charFolder)
    {
        lock (_lock)
        {
            _byName.Clear();
            _dirty = false;
            _path = string.IsNullOrWhiteSpace(charFolder) ? string.Empty : Path.Combine(charFolder, FileName);
            if (_path.Length == 0 || !File.Exists(_path)) return;
            try { Parse(File.ReadAllText(_path)); }
            catch { _byName.Clear(); }   // unreadable: start fresh, the next save rewrites it
        }
    }

    /// <summary>One kill of <paramref name="name"/> gave <paramref name="xp"/>.</summary>
    public void Record(string? name, long xp)
    {
        if (xp <= 0) return;
        lock (_lock)
        {
            if (EntryFor(name) is not Entry e) return;
            e.Kills++;
            e.Avg += (xp - e.Avg) / Math.Min(e.Kills, AverageOver);
            _dirty = true;
        }
    }

    /// <summary>One kill of <paramref name="name"/> left its corpse <paramref name="ms"/> after the kill notice.</summary>
    public void RecordDeath(string? name, long ms)
    {
        if (ms < 0) return;
        lock (_lock)
        {
            if (EntryFor(name) is not Entry e) return;
            e.DeathKills++;
            e.DeathAvg += (ms - e.DeathAvg) / Math.Min(e.DeathKills, AverageOver);
            _dirty = true;
        }
    }

    /// <summary>The learned experience per kill of <paramref name="name"/>, or null when none is known.</summary>
    public double? XpPerKill(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
            return _byName.TryGetValue(name.Trim(), out Entry? e) && e.Kills > 0 ? e.Avg : null;
    }

    /// <summary>Kills behind <see cref="XpPerKill"/> (0 when unknown).</summary>
    public int KillsCounted(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        lock (_lock)
            return _byName.TryGetValue(name.Trim(), out Entry? e) ? e.Kills : 0;
    }

    /// <summary>The learned time from a kill of <paramref name="name"/> to its corpse (ms), or null when none is known.</summary>
    public double? DeathMs(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
            return _byName.TryGetValue(name.Trim(), out Entry? e) && e.DeathKills > 0 ? e.DeathAvg : null;
    }

    /// <summary>Kills behind <see cref="DeathMs"/> (0 when unknown).</summary>
    public int DeathsTimed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        lock (_lock)
            return _byName.TryGetValue(name.Trim(), out Entry? e) ? e.DeathKills : 0;
    }

    /// <summary>Writes the file when something changed. Cheap to call from a tick.</summary>
    public void SaveIfDirty()
    {
        string text, path;
        lock (_lock)
        {
            if (!_dirty || _path.Length == 0) return;
            _dirty = false;
            text = Serialize();
            path = _path;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            lock (_lock) _dirty = true;   // tried again at the next save
        }
    }

    // ── Format ───────────────────────────────────────────────────────────

    internal string Serialize()
    {
        var sb = new StringBuilder(128 + _byName.Count * 60);
        sb.Append("# RynthAi per monster, learned from your kills (auto-generated); averages over the last ")
          .Append(AverageOver.ToString(Ic)).Append(" kills.\n");
        sb.Append("# X|name|xpPerKill|kills counted\n");
        sb.Append("# D|name|ms from the kill to its corpse (the death animation)|kills timed\n");
        var names = new List<string>(_byName.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string k in names)
        {
            Entry e = _byName[k];
            if (e.Kills > 0)
                sb.Append("X|").Append(e.Name).Append('|').Append(e.Avg.ToString("0.##", Ic)).Append('|').Append(e.Kills.ToString(Ic)).Append('\n');
            if (e.DeathKills > 0)
                sb.Append("D|").Append(e.Name).Append('|').Append(e.DeathAvg.ToString("0", Ic)).Append('|').Append(e.DeathKills.ToString(Ic)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Reads the lines it knows; anything else (a comment, a later version's line, a bad number) is skipped.</summary>
    private void Parse(string text)
    {
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('|');
            if (f.Length < 4 || (f[0] != "X" && f[0] != "D")) continue;
            string n = Clean(f[1]);
            if (n.Length == 0) continue;
            if (!double.TryParse(f[2], NumberStyles.Float, Ic, out double avg) || !double.IsFinite(avg) || avg < 0) continue;
            if (!int.TryParse(f[3], NumberStyles.None, Ic, out int kills) || kills <= 0) continue;
            if (!_byName.TryGetValue(n, out Entry? e))
            {
                if (_byName.Count >= MaxMonsters) continue;
                e = new Entry { Name = n };
                _byName[n] = e;
            }
            if (f[0] == "X" && e.Kills == 0 && avg > 0) { e.Avg = avg; e.Kills = kills; }
            else if (f[0] == "D" && e.DeathKills == 0) { e.DeathAvg = avg; e.DeathKills = kills; }
        }
        // A name whose every line was bad leaves nothing.
        var empty = new List<string>();
        foreach (var kv in _byName) if (kv.Value.Kills == 0 && kv.Value.DeathKills == 0) empty.Add(kv.Key);
        foreach (string k in empty) _byName.Remove(k);
    }

    /// <summary>The monster's entry (made on first sight); null for no name. Caller holds _lock.</summary>
    private Entry? EntryFor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = Clean(name);
        if (n.Length == 0) return null;
        if (!_byName.TryGetValue(n, out Entry? e))
        {
            if (_byName.Count >= MaxMonsters) DropFewest();
            e = new Entry { Name = n };
            _byName[n] = e;
        }
        return e;
    }

    private void DropFewest()
    {
        string? fewest = null;
        int k = int.MaxValue;
        foreach (Entry e in _byName.Values)
            if (e.Kills + e.DeathKills < k) { k = e.Kills + e.DeathKills; fewest = e.Name; }
        if (fewest != null) _byName.Remove(fewest);
    }

    private static string Clean(string name)
    {
        string n = name.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return n.Length > 64 ? n.Substring(0, 64) : n;
    }
}
