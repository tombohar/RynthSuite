using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>
/// Per-CHARACTER learned combat damage. Damage scales with the caster's
/// skill/buffs AND the weapon used AND the spell, so unlike CreatureProfileStore
/// (shared HP/resists — monster intrinsics) this is per character, stored next to
/// that character's settings:
///   ...\SettingsProfiles\ACEmulator\&lt;charName&gt;\monster_damage.txt
///
/// Learned, all as running averages:
///   • HpPool[wcid]                                  — total damage to kill (≈ HP), weapon-independent
///   • HpManual[wcid]                                — USER-entered HP override (UI edit); wins over everything
///   • Cast[weapon|wcid|element|tier].AvgDamage      — damage of one such cast (all hits)
///   • Cast[...].CritAvg / NonCritAvg                — split crit vs non-crit average damage
///   • Cast[...].AvgCastsToKill                      — how many such casts it takes to kill
///
/// AvgCastsToKill is what makes ONE-SHOTS work: a one-shot is simply "avg ≈ 1
/// cast", so combat can swap right after the first cast — the damage math can't,
/// because nothing has landed yet when the prediction is made.
///
/// Flat line-based text (not JSON) so it stays NativeAOT-trivial and hand-editable.
/// The monster NAME is included on every row so the file is readable without a
/// wcid lookup:
///   H|&lt;wcid&gt;|&lt;name&gt;|&lt;hpToKill&gt;|&lt;samples&gt;
///   M|&lt;wcid&gt;|&lt;name&gt;|&lt;hp&gt;                                   (manual HP override)
///   D|&lt;wcid&gt;|&lt;name&gt;|&lt;weaponId&gt;|&lt;element&gt;|&lt;tier&gt;|&lt;avgDamage&gt;|&lt;dmgSamples&gt;|&lt;avgCastsToKill&gt;|&lt;killSamples&gt;|&lt;critAvg&gt;|&lt;critSamples&gt;|&lt;nonCritAvg&gt;|&lt;nonCritSamples&gt;
/// (The loader also accepts the older 9/10-field name-less / crit-less D rows so existing data carries over.)
/// </summary>
internal sealed class MonsterDamageStore
{
    private const double Alpha = 0.25; // EMA weight for each new sample

    private sealed class CastStat
    {
        public double Avg;             // avg damage per cast (all hits)
        public int    Samples;         // damage observations
        public double AvgCastsToKill;  // avg number of these casts to kill the mob
        public int    KillSamples;     // kills observed with this cast as the finisher
        public double CritAvg;         // avg damage of CRITICAL hits
        public int    CritSamples;     // crit observations
        public double NonCritAvg;      // avg damage of non-crit hits
        public int    NonCritSamples;  // non-crit observations
    }

    private sealed class WcidProfile
    {
        public string Name = "";  // last-seen monster name (for the readable file + UI)
        public double HpPool;     // 0 = unknown (learned total damage to kill)
        public int    HpSamples;
        public double HpManual;   // 0 = unset; user-entered HP override (UI), authoritative when > 0
        public uint   WeaponManual;  // 0 = unset; user-picked weapon override for this wcid (Damage panel)
        public uint   OffhandManual; // 0 = unset; user-picked offhand override for this wcid (Damage panel; stored only)
        public int    LastTier = NoTier; // most-recent cast tier (negative = ring); NoTier = unset this session
        // key = "weaponId|element|tier"
        public readonly Dictionary<string, CastStat> Casts =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private const int NoTier = int.MinValue; // sentinel: no cast observed for this wcid yet

    private readonly object _lock = new();
    private readonly Dictionary<uint, WcidProfile> _byWcid = new();
    private string _filePath = string.Empty;
    private bool _dirty;
    // Per-character DEFAULT weapon: the fallback every monster without its own weapon override uses,
    // so editing it sweeps all "Default" monsters at once. 0 = unset (fall through to learned-best).
    private uint _defaultWeapon;

    private static string CastKey(uint weaponId, string element, int tier) =>
        weaponId.ToString(CultureInfo.InvariantCulture) + "|" + (element ?? "") + "|" +
        tier.ToString(CultureInfo.InvariantCulture);

    // Names can't contain '|' (the field separator); AC names never do, but be safe.
    private static string SafeName(string? name) =>
        string.IsNullOrEmpty(name) ? "" : name.Replace('|', '/').Trim();

    private static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Point the store at a character's folder and load its file. Safe to call again on character change.</summary>
    public void SetCharacter(string charFolder)
    {
        lock (_lock)
        {
            _byWcid.Clear();
            _defaultWeapon = 0;
            _dirty = false;
            _filePath = string.IsNullOrWhiteSpace(charFolder)
                ? string.Empty
                : Path.Combine(charFolder, "monster_damage.txt");
        }
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            _byWcid.Clear();
            _defaultWeapon = 0;
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;

            try
            {
                foreach (string raw in File.ReadAllLines(_filePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split('|');

                    if (f.Length >= 2 && f[0] == "DEF")
                    {
                        // DEF|weaponId — per-character default weapon (sweeping fallback)
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dw))
                            _defaultWeapon = dw;
                    }
                    else if (f.Length >= 4 && f[0] == "H")
                    {
                        // New: H|wcid|name|hp|samples ;  Old: H|wcid|hp|samples
                        bool hasName = f.Length >= 5;
                        int hi = hasName ? 3 : 2;
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint hw)
                            && double.TryParse(f[hi], NumberStyles.Float, CultureInfo.InvariantCulture, out double hp)
                            && int.TryParse(f[hi + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hs))
                        {
                            var p = Get(hw);
                            p.HpPool = hp;
                            p.HpSamples = hs;
                            if (hasName && f[2].Length > 0) p.Name = f[2];
                        }
                    }
                    else if (f.Length >= 4 && f[0] == "M")
                    {
                        // M|wcid|name|hp  — manual HP override
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint mw)
                            && double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double mhp))
                        {
                            var p = Get(mw);
                            p.HpManual = mhp < 0 ? 0 : mhp;
                            if (f[2].Length > 0) p.Name = f[2];
                        }
                    }
                    else if (f.Length >= 4 && f[0] == "W")
                    {
                        // W|wcid|name|weaponId  — per-monster weapon override (Damage panel)
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ww)
                            && uint.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wid))
                        {
                            var p = Get(ww);
                            p.WeaponManual = wid;
                            if (f[2].Length > 0) p.Name = f[2];
                        }
                    }
                    else if (f.Length >= 4 && f[0] == "O")
                    {
                        // O|wcid|name|offhandId  — per-monster offhand override (Damage panel; stored only)
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ow)
                            && uint.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint oid))
                        {
                            var p = Get(ow);
                            p.OffhandManual = oid;
                            if (f[2].Length > 0) p.Name = f[2];
                        }
                    }
                    else if (f.Length >= 9 && f[0] == "D")
                    {
                        // New: D|wcid|name|weaponId|element|tier|avg|dmgS|avgCasts|killS|critAvg|critS|nonCritAvg|nonCritS  (14 fields)
                        // Mid: D|wcid|name|weaponId|element|tier|avg|dmgS|avgCasts|killS                                    (10 fields)
                        // Old: D|wcid|weaponId|element|tier|avg|dmgS|avgCasts|killS                                          (9 fields)
                        bool hasName = f.Length >= 10;
                        int b = hasName ? 3 : 2; // index of weaponId
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dw)
                            && uint.TryParse(f[b], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wid)
                            && int.TryParse(f[b + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier)
                            && double.TryParse(f[b + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out double avg)
                            && int.TryParse(f[b + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ds)
                            && double.TryParse(f[b + 5], NumberStyles.Float, CultureInfo.InvariantCulture, out double ack)
                            && int.TryParse(f[b + 6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ks))
                        {
                            var p = Get(dw);
                            if (hasName && f[2].Length > 0) p.Name = f[2];
                            var cast = new CastStat
                            {
                                Avg = avg, Samples = ds, AvgCastsToKill = ack, KillSamples = ks,
                            };
                            // Crit split appended in the 14-field form (only present when hasName).
                            if (hasName && f.Length >= 14)
                            {
                                if (double.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double ca)) cast.CritAvg = ca;
                                if (int.TryParse(f[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cs)) cast.CritSamples = cs;
                                if (double.TryParse(f[12], NumberStyles.Float, CultureInfo.InvariantCulture, out double nca)) cast.NonCritAvg = nca;
                                if (int.TryParse(f[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ncs)) cast.NonCritSamples = ncs;
                            }
                            p.Casts[CastKey(wid, f[b + 1], tier)] = cast;
                        }
                    }
                }
            }
            catch
            {
                // Corrupt file — start fresh; it rewrites on next save.
            }
        }
    }

    /// <summary>Persist if anything changed. Cheap to call from a tick.</summary>
    public void SaveIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty || string.IsNullOrEmpty(_filePath)) return;
            _dirty = false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var sb = new StringBuilder();
                sb.Append("# RynthAi per-character monster damage learning (auto-generated).\n");
                sb.Append("# H|wcid|name|hpToKill|samples\n");
                sb.Append("# M|wcid|name|hp   (manual HP override, set in the Damage panel)\n");
                sb.Append("# W|wcid|name|weaponId   (per-monster weapon override, set in the Damage panel)\n");
                sb.Append("# O|wcid|name|offhandId  (per-monster offhand override, set in the Damage panel)\n");
                sb.Append("# D|wcid|name|weaponId|element|tier|avgDamage|dmgSamples|avgCastsToKill|killSamples|critAvg|critSamples|nonCritAvg|nonCritSamples\n");
                sb.Append("# DEF|weaponId   (per-character default weapon — fallback for every monster without its own override)\n");
                if (_defaultWeapon != 0)
                    sb.Append("DEF|").Append(_defaultWeapon).Append('\n');
                foreach (var kv in _byWcid)
                {
                    var p = kv.Value;
                    string name = p.Name;
                    if (p.HpSamples > 0)
                        sb.Append("H|").Append(kv.Key).Append('|').Append(name).Append('|')
                          .Append(Num(p.HpPool)).Append('|').Append(p.HpSamples).Append('\n');
                    if (p.HpManual > 0)
                        sb.Append("M|").Append(kv.Key).Append('|').Append(name).Append('|')
                          .Append(Num(p.HpManual)).Append('\n');
                    if (p.WeaponManual != 0)
                        sb.Append("W|").Append(kv.Key).Append('|').Append(name).Append('|')
                          .Append(p.WeaponManual).Append('\n');
                    if (p.OffhandManual != 0)
                        sb.Append("O|").Append(kv.Key).Append('|').Append(name).Append('|')
                          .Append(p.OffhandManual).Append('\n');
                    foreach (var c in p.Casts)
                    {
                        string[] kp = c.Key.Split('|'); // weaponId|element|tier
                        if (kp.Length < 3) continue;
                        var v = c.Value;
                        sb.Append("D|").Append(kv.Key).Append('|').Append(name).Append('|')
                          .Append(kp[0]).Append('|').Append(kp[1]).Append('|').Append(kp[2]).Append('|')
                          .Append(Num(v.Avg)).Append('|').Append(v.Samples).Append('|')
                          .Append(Num(v.AvgCastsToKill)).Append('|').Append(v.KillSamples).Append('|')
                          .Append(Num(v.CritAvg)).Append('|').Append(v.CritSamples).Append('|')
                          .Append(Num(v.NonCritAvg)).Append('|').Append(v.NonCritSamples).Append('\n');
                    }
                }

                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                File.Copy(tmp, _filePath, overwrite: true);
                try { File.Delete(tmp); } catch { }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Fold one observed cast's damage into the (weapon, wcid, element, tier) running
    /// average, split by crit vs non-crit. <paramref name="crit"/> comes from the
    /// AttackerNotification crit flag (melee/missile) or the combat-log parse (magic).
    /// </summary>
    public void RecordHit(uint weaponId, uint wcid, string name, string element, int tier, double damage, bool crit)
    {
        if (wcid == 0 || damage <= 0) return;
        lock (_lock)
        {
            SetNameLocked(wcid, name);
            Get(wcid).LastTier = tier;
            var s = GetCast(wcid, weaponId, element, tier);
            s.Avg = s.Samples == 0 ? damage : s.Avg + Alpha * (damage - s.Avg);
            s.Samples++;
            if (crit)
            {
                s.CritAvg = s.CritSamples == 0 ? damage : s.CritAvg + Alpha * (damage - s.CritAvg);
                s.CritSamples++;
            }
            else
            {
                s.NonCritAvg = s.NonCritSamples == 0 ? damage : s.NonCritAvg + Alpha * (damage - s.NonCritAvg);
                s.NonCritSamples++;
            }
            _dirty = true;
        }
    }

    /// <summary>
    /// Fold a confirmed kill into both the wcid's HP-to-kill estimate (from total
    /// damage) and the finishing cast's casts-to-kill estimate (from the fight's
    /// cast count). For a one-shot that finishing cast is the single cast, so
    /// AvgCastsToKill → 1.
    /// </summary>
    public void RecordKill(uint weaponId, uint wcid, string name, string element, int tier, int castCount, double totalDamage)
    {
        if (wcid == 0) return;
        lock (_lock)
        {
            var p = Get(wcid);
            SetNameLocked(wcid, name);
            p.LastTier = tier;
            if (totalDamage > 0)
            {
                p.HpPool = p.HpSamples == 0 ? totalDamage : p.HpPool + Alpha * (totalDamage - p.HpPool);
                p.HpSamples++;
            }
            if (castCount > 0)
            {
                var s = GetCast(wcid, weaponId, element, tier);
                s.AvgCastsToKill = s.KillSamples == 0 ? castCount : s.AvgCastsToKill + Alpha * (castCount - s.AvgCastsToKill);
                s.KillSamples++;
            }
            _dirty = true;
        }
    }

    /// <summary>Note the tier of the most-recent cast at a wcid (negative = ring) so the Damage tab's
    /// collapsed row shows the LATEST tier used. In-memory only (not persisted) — resets per session.</summary>
    public void NoteCast(uint wcid, int tier, string? name = null)
    {
        if (wcid == 0) return;
        lock (_lock)
        {
            var p = Get(wcid);
            p.LastTier = tier;
            if (!string.IsNullOrEmpty(name)) SetNameLocked(wcid, name);
        }
    }

    /// <summary>The latest tier used against this wcid (negative = ring). Falls back to the tier of the
    /// most-killed cast entry when nothing was cast this session; 0 if unknown.</summary>
    public int GetLastTier(uint wcid)
    {
        lock (_lock)
        {
            if (!_byWcid.TryGetValue(wcid, out var p)) return 0;
            if (p.LastTier != NoTier) return p.LastTier;
            int bestTier = 0, bestKills = -1;
            foreach (var c in p.Casts)
            {
                if (c.Value.KillSamples > bestKills)
                {
                    bestKills = c.Value.KillSamples;
                    string[] kp = c.Key.Split('|'); // weaponId|element|tier
                    if (kp.Length >= 3 && int.TryParse(kp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t))
                        bestTier = t;
                }
            }
            return bestTier;
        }
    }

    /// <summary>Average damage of one (weapon, element, tier) cast on this wcid. samples=0 when unlearned.</summary>
    public double GetAvgDamage(uint weaponId, uint wcid, string element, int tier, out int samples)
    {
        samples = 0;
        if (wcid == 0) return 0;
        lock (_lock)
        {
            if (_byWcid.TryGetValue(wcid, out var p) && p.Casts.TryGetValue(CastKey(weaponId, element, tier), out var s))
            {
                samples = s.Samples;
                return s.Avg;
            }
        }
        return 0;
    }

    /// <summary>Average number of (weapon, element, tier) casts to kill this wcid. killSamples=0 when unlearned (≈1 ⇒ one-shot).</summary>
    public double GetAvgCastsToKill(uint weaponId, uint wcid, string element, int tier, out int killSamples)
    {
        killSamples = 0;
        if (wcid == 0) return 0;
        lock (_lock)
        {
            if (_byWcid.TryGetValue(wcid, out var p) && p.Casts.TryGetValue(CastKey(weaponId, element, tier), out var s))
            {
                killSamples = s.KillSamples;
                return s.AvgCastsToKill;
            }
        }
        return 0;
    }

    /// <summary>Learned total-damage-to-kill for this wcid, or 0 if not yet learned.</summary>
    public double GetLearnedHp(uint wcid)
    {
        if (wcid == 0) return 0;
        lock (_lock)
            return _byWcid.TryGetValue(wcid, out var p) ? p.HpPool : 0;
    }

    /// <summary>User-entered HP override for this wcid (0 = none). Authoritative when &gt; 0.</summary>
    public double GetManualHp(uint wcid)
    {
        if (wcid == 0) return 0;
        lock (_lock)
            return _byWcid.TryGetValue(wcid, out var p) ? p.HpManual : 0;
    }

    /// <summary>Set (or clear, with hp &lt;= 0) the manual HP override for a wcid. Persists on next save.</summary>
    public void SetManualHp(uint wcid, double hp, string? name = null)
    {
        if (wcid == 0) return;
        lock (_lock)
        {
            var p = Get(wcid);
            p.HpManual = hp <= 0 ? 0 : hp;
            if (!string.IsNullOrEmpty(name)) SetNameLocked(wcid, name);
            _dirty = true;
        }
    }

    /// <summary>User-picked weapon override for this wcid (0 = none, fall back to GetBestWeapon).</summary>
    public uint GetManualWeapon(uint wcid)
    {
        if (wcid == 0) return 0;
        lock (_lock)
            return _byWcid.TryGetValue(wcid, out var p) ? p.WeaponManual : 0;
    }

    /// <summary>Set (0 clears) the per-monster weapon override. Persists on next save.</summary>
    public void SetManualWeapon(uint wcid, uint weaponId, string? name = null)
    {
        if (wcid == 0) return;
        lock (_lock)
        {
            var p = Get(wcid);
            p.WeaponManual = weaponId;
            if (!string.IsNullOrEmpty(name)) SetNameLocked(wcid, name);
            _dirty = true;
        }
    }

    /// <summary>User-picked offhand override for this wcid (0 = none). Stored only — combat does not equip it.</summary>
    public uint GetManualOffhand(uint wcid)
    {
        if (wcid == 0) return 0;
        lock (_lock)
            return _byWcid.TryGetValue(wcid, out var p) ? p.OffhandManual : 0;
    }

    /// <summary>Set (0 clears) the per-monster offhand override. Persists on next save.</summary>
    public void SetManualOffhand(uint wcid, uint offhandId, string? name = null)
    {
        if (wcid == 0) return;
        lock (_lock)
        {
            var p = Get(wcid);
            p.OffhandManual = offhandId;
            if (!string.IsNullOrEmpty(name)) SetNameLocked(wcid, name);
            _dirty = true;
        }
    }

    /// <summary>
    /// The weapon the system "thinks is best" for this wcid, from learned data:
    /// fewest average casts-to-kill (weapons with KillSamples ≥ 2), else highest
    /// average damage-per-cast (weapons with Samples ≥ 3). Aggregated per weaponId
    /// across the wcid's element/tier rows. Returns 0 when there isn't enough data.
    /// Fewest-casts naturally captures element effectiveness empirically (the wand
    /// that kills a fire-weak mob fastest tends to be the fire wand).
    /// </summary>
    public uint GetBestWeapon(uint wcid)
    {
        if (wcid == 0) return 0;
        lock (_lock)
        {
            if (!_byWcid.TryGetValue(wcid, out var p) || p.Casts.Count == 0) return 0;

            // Aggregate per weaponId (sample-weighted) across element/tier rows.
            var byWeapon = new Dictionary<uint, (double castsW, int kills, double dmgW, int dmg)>();
            foreach (var c in p.Casts)
            {
                string[] kp = c.Key.Split('|'); // weaponId|element|tier
                if (kp.Length < 1
                    || !uint.TryParse(kp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wid)
                    || wid == 0) continue;
                var v = c.Value;
                byWeapon.TryGetValue(wid, out var a);
                a.castsW += v.AvgCastsToKill * v.KillSamples;
                a.kills  += v.KillSamples;
                a.dmgW   += v.Avg * v.Samples;
                a.dmg    += v.Samples;
                byWeapon[wid] = a;
            }

            uint bestByCasts = 0; double bestCasts = double.MaxValue;
            uint bestByDmg   = 0; double bestDmg   = 0;
            foreach (var kv in byWeapon)
            {
                var a = kv.Value;
                if (a.kills >= 2)
                {
                    double avgCasts = a.castsW / a.kills;
                    if (avgCasts > 0 && avgCasts < bestCasts) { bestCasts = avgCasts; bestByCasts = kv.Key; }
                }
                if (a.dmg >= 3)
                {
                    double avgDmg = a.dmgW / a.dmg;
                    if (avgDmg > bestDmg) { bestDmg = avgDmg; bestByDmg = kv.Key; }
                }
            }
            return bestByCasts != 0 ? bestByCasts : bestByDmg;
        }
    }

    /// <summary>The per-character default weapon (0 = unset). Every monster without its own
    /// override falls back to this, so editing it sweeps all "Default" monsters at once.</summary>
    public uint GetDefaultWeapon()
    {
        lock (_lock) return _defaultWeapon;
    }

    public void SetDefaultWeapon(uint weaponId)
    {
        lock (_lock) { if (_defaultWeapon == weaponId) return; _defaultWeapon = weaponId; _dirty = true; }
    }

    /// <summary>Weapon combat should wield for this wcid: per-monster override if set, else the
    /// per-character default weapon, else the learned best (0 = none of those).</summary>
    public uint GetEffectiveWeapon(uint wcid)
    {
        uint manual = GetManualWeapon(wcid);
        if (manual != 0) return manual;
        uint def; lock (_lock) def = _defaultWeapon;
        return def != 0 ? def : GetBestWeapon(wcid);
    }

    /// <summary>Delete one learned (weapon, element, tier) row for a wcid. Drops the wcid entirely if nothing's left.</summary>
    public bool DeleteRow(uint wcid, uint weaponId, string element, int tier)
    {
        lock (_lock)
        {
            if (!_byWcid.TryGetValue(wcid, out var p)) return false;
            bool removed = p.Casts.Remove(CastKey(weaponId, element, tier));
            if (removed)
            {
                if (p.Casts.Count == 0 && p.HpSamples == 0 && p.HpManual <= 0 && p.HpPool <= 0
                    && p.WeaponManual == 0 && p.OffhandManual == 0)
                    _byWcid.Remove(wcid);
                _dirty = true;
            }
            return removed;
        }
    }

    /// <summary>Delete ALL learned history for a wcid (every weapon/spell + HP). For a character upgrade reset.</summary>
    public bool DeleteWcid(uint wcid)
    {
        lock (_lock)
        {
            bool removed = _byWcid.Remove(wcid);
            if (removed) _dirty = true;
            return removed;
        }
    }

    /// <summary>
    /// Master reset: zero every LEARNED statistic (damage averages, crit/non-crit,
    /// casts-to-kill, kills, learned HP pool) while KEEPING monster names, the cast
    /// rows themselves (so the table still lists the monsters), and the user's manual
    /// overrides (HpManual, WeaponManual, OffhandManual). Re-fighting re-learns from scratch.
    /// </summary>
    public void ClearAllStats()
    {
        lock (_lock)
        {
            foreach (var p in _byWcid.Values)
            {
                p.HpPool = 0;
                p.HpSamples = 0;
                foreach (var c in p.Casts.Values)
                {
                    c.Avg = 0; c.Samples = 0;
                    c.AvgCastsToKill = 0; c.KillSamples = 0;
                    c.CritAvg = 0; c.CritSamples = 0;
                    c.NonCritAvg = 0; c.NonCritSamples = 0;
                }
            }
            _dirty = true;
        }
    }

    /// <summary>One learned row for the UI: a (wcid, weapon, element, tier) cast stat.</summary>
    public readonly record struct DamageRow(
        uint Wcid, string Name, double HpPool, double HpManual,
        uint WeaponId, string Element, int Tier,
        double AvgDamage, int DmgSamples,
        double AvgCritDamage, int CritSamples,
        double AvgNonCritDamage, int NonCritSamples,
        double AvgCastsToKill, int KillSamples);

    /// <summary>Snapshot all learned rows for live display. Cheap; copies under lock.</summary>
    public List<DamageRow> Snapshot()
    {
        var list = new List<DamageRow>();
        lock (_lock)
        {
            foreach (var kv in _byWcid)
            {
                var p = kv.Value;
                foreach (var c in p.Casts)
                {
                    string[] kp = c.Key.Split('|'); // weaponId|element|tier
                    uint weaponId = kp.Length >= 1 && uint.TryParse(kp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint w) ? w : 0;
                    string element = kp.Length >= 2 ? kp[1] : "";
                    int tier = kp.Length >= 3 && int.TryParse(kp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) ? t : 0;
                    var v = c.Value;
                    list.Add(new DamageRow(kv.Key, p.Name, p.HpPool, p.HpManual, weaponId, element, tier,
                        v.Avg, v.Samples, v.CritAvg, v.CritSamples, v.NonCritAvg, v.NonCritSamples,
                        v.AvgCastsToKill, v.KillSamples));
                }
            }
        }
        return list;
    }

    private void SetNameLocked(uint wcid, string name)
    {
        string n = SafeName(name);
        if (n.Length == 0) return;
        Get(wcid).Name = n;
    }

    private WcidProfile Get(uint wcid)
    {
        if (!_byWcid.TryGetValue(wcid, out var p))
        {
            p = new WcidProfile();
            _byWcid[wcid] = p;
        }
        return p;
    }

    private CastStat GetCast(uint wcid, uint weaponId, string element, int tier)
    {
        var casts = Get(wcid).Casts;
        string key = CastKey(weaponId, element, tier);
        if (!casts.TryGetValue(key, out var s))
        {
            s = new CastStat();
            casts[key] = s;
        }
        return s;
    }
}
