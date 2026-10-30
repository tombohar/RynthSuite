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
///   ...\SettingsProfiles\&lt;server&gt;\&lt;charName&gt;\monster_damage.txt
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
///
/// Difficulty tiers (2026-10-03, <see cref="DifficultyTier"/>: the active server plugin reports
/// them): a server's scaled worlds and copies hold the same monsters (same wcids) with health,
/// skills and damage scaled by the tier. What a tier changes is kept per (wcid, tier): the HP pool, casts-to-kill, hits and
/// misses, seconds per kill (per weapon and per summon) and the damage the monster does to us.
/// What it does not change stays shared: per-cast damage (resistances and armour don't scale)
/// and every rule (weapon, offhand, pet, manual HP). Tier 0 - the unscaled world, and every
/// server without tiers, where the tier is always 0 - is today's data in today's rows, untouched. Tiers above 0
/// are extra rows an older loader skips:
///   AH|&lt;tier&gt;|&lt;wcid&gt;|&lt;name&gt;|&lt;hpToKill&gt;|&lt;samples&gt;
///   AD|&lt;tier&gt;|&lt;wcid&gt;|&lt;name&gt;|&lt;weaponId&gt;|&lt;element&gt;|&lt;spellTier&gt;|&lt;avgCastsToKill&gt;|&lt;killSamples&gt;
///   AU|&lt;tier&gt;|&lt;wcid&gt;|&lt;name&gt;|&lt;weaponId&gt;|&lt;hits&gt;|&lt;misses&gt;|&lt;secAvg&gt;|&lt;secSamples&gt;
///   APK|&lt;tier&gt;|&lt;wcid&gt;|&lt;name&gt;|&lt;element&gt;|&lt;kills&gt;|&lt;secAvg&gt;
///   AT|&lt;tier&gt;|&lt;wcid&gt;|&lt;name&gt;|&lt;element&gt;|&lt;hits&gt;|&lt;avg&gt;|&lt;max&gt;
/// Records go to <see cref="Difficulty"/> (the tier where the player stands, set each tick);
/// readers without a tier argument read it too, and the Damage panel passes the tier it shows.
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

    /// <summary>One weapon (or wand) against one monster: accuracy and fight length.</summary>
    private sealed class WeaponUse
    {
        public int    Hits;        // landed attacks / damaging casts
        public int    Misses;      // "X evaded your attack", "X resists your spell"
        public double SecAvg;      // avg seconds from engaging to the kill (fights we timed)
        public int    SecSamples;
    }

    /// <summary>Kills with a summon of one element out ("" key = no summon).</summary>
    private sealed class PetUse
    {
        public int    Kills;
        public double SecAvg;
    }

    /// <summary>Damage the monster did to us, per element.</summary>
    private sealed class TakenStat
    {
        public int    Hits;
        public double Avg;
        public double Max;
    }

    private sealed class WcidProfile
    {
        public string Name = "";  // last-seen monster name (for the readable file + UI)
        public double HpPool;     // 0 = unknown (learned total damage to kill)
        public int    HpSamples;
        public double HpManual;   // 0 = unset; user-entered HP override (UI), authoritative when > 0
        public uint   WeaponManual;  // 0 = unset; user-picked weapon override for this wcid (Damage panel)
        public uint   OffhandManual; // 0 = unset; user-picked offhand override for this wcid (Damage panel; stored only)
        public string PetManual = ""; // "" = Auto; "E:<element>" or "I:<essence id>" (Damage panel)
        public int    LastTier = NoTier; // most-recent cast tier (negative = ring); NoTier = unset this session
        // key = "weaponId|element|tier"
        public readonly Dictionary<string, CastStat> Casts =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<uint, WeaponUse> Weapons = new();
        public readonly Dictionary<string, PetUse> Pets = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, TakenStat> Taken = new(StringComparer.OrdinalIgnoreCase);
    }

    private const int NoTier = int.MinValue; // sentinel: no cast observed for this wcid yet

    /// <summary>Casts-to-kill for one (weapon, element, spell tier) at one difficulty tier.</summary>
    private sealed class KillStat
    {
        public double AvgCastsToKill;
        public int    KillSamples;
    }

    /// <summary>What one difficulty tier above 0 changes about one monster (see the class comment).</summary>
    private sealed class TierProfile
    {
        public double HpPool;
        public int    HpSamples;
        // key = "weaponId|element|tier" (the spell tier), as WcidProfile.Casts
        public readonly Dictionary<string, KillStat> Kills = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<uint, WeaponUse> Weapons = new();
        public readonly Dictionary<string, PetUse> Pets = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, TakenStat> Taken = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _lock = new();
    private readonly Dictionary<uint, WcidProfile> _byWcid = new();
    /// <summary>Difficulty tiers above 0 only; tier 0 is <see cref="_byWcid"/>.</summary>
    private readonly Dictionary<(uint Wcid, int Difficulty), TierProfile> _byTier = new();
    private volatile int _difficulty;

    /// <summary>
    /// The difficulty tier new observations are recorded under, and the one the tier-less
    /// readers answer for: the tier where the player stands (DifficultyTier.Current, set every
    /// tick). Always 0 on a server without tiers.
    /// </summary>
    public int Difficulty
    {
        get => _difficulty;
        set => _difficulty = value < 0 ? 0 : value;
    }
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

    /// <summary>Nothing is recorded under wcid 0 or a player character's weenie (<see cref="PlayerWeenies"/>).</summary>
    private static bool NotMonster(uint wcid) => wcid == 0 || PlayerWeenies.IsPlayerWcid(wcid);

    /// <summary>Point the store at a character's folder and load its file. Safe to call again on character change.</summary>
    public void SetCharacter(string charFolder)
    {
        lock (_lock)
        {
            _byWcid.Clear();
            _byTier.Clear();
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
            _byTier.Clear();
            _defaultWeapon = 0;
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;

            try
            {
                foreach (string raw in File.ReadAllLines(_filePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split('|');

                    if (f.Length >= 2 && f[0].Length >= 2 && f[0][0] == 'A' && f[0] != "A")
                    {
                        LoadTierRow(f);
                    }
                    else if (f.Length >= 2 && f[0] == "DEF")
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
                    else if (f.Length >= 4 && f[0] == "P")
                    {
                        // P|wcid|name|choice  — per-monster pet choice (Damage panel)
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pw))
                        {
                            var p = Get(pw);
                            p.PetManual = f[3];
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
                    else if (f.Length >= 8 && f[0] == "U")
                    {
                        // U|wcid|name|weaponId|hits|misses|secAvg|secSamples
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uw)
                            && uint.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wid))
                        {
                            var p = Get(uw);
                            if (f[2].Length > 0) p.Name = f[2];
                            var u = new WeaponUse();
                            int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out u.Hits);
                            int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out u.Misses);
                            double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out u.SecAvg);
                            int.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out u.SecSamples);
                            p.Weapons[wid] = u;
                        }
                    }
                    else if (f.Length >= 6 && f[0] == "PK")
                    {
                        // PK|wcid|name|element (none = no summon)|kills|secAvg
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint kw))
                        {
                            var p = Get(kw);
                            if (f[2].Length > 0) p.Name = f[2];
                            var k = new PetUse();
                            int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out k.Kills);
                            double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out k.SecAvg);
                            p.Pets[f[3] == "none" ? "" : f[3]] = k;
                        }
                    }
                    else if (f.Length >= 7 && f[0] == "T")
                    {
                        // T|wcid|name|element|hits|avg|max
                        if (uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint tw))
                        {
                            var p = Get(tw);
                            if (f[2].Length > 0) p.Name = f[2];
                            var t = new TakenStat();
                            int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out t.Hits);
                            double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out t.Avg);
                            double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out t.Max);
                            p.Taken[f[3]] = t;
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

            // Rows a player character left behind (an appraised player or your own character
            // listed as a monster) go, and the next save writes the file without them.
            if (DropPlayerRowsLocked()) _dirty = true;
        }
    }

    /// <summary>Removes every row under a player weenie. True when any went. Caller holds _lock.</summary>
    private bool DropPlayerRowsLocked()
    {
        var gone = new List<uint>();
        foreach (uint w in _byWcid.Keys) if (PlayerWeenies.IsPlayerWcid(w)) gone.Add(w);
        foreach (uint w in gone) _byWcid.Remove(w);
        var tierGone = new List<(uint, int)>();
        foreach (var k in _byTier.Keys) if (PlayerWeenies.IsPlayerWcid(k.Wcid)) tierGone.Add(k);
        foreach (var k in tierGone) _byTier.Remove(k);
        return gone.Count > 0 || tierGone.Count > 0;
    }

    /// <summary>One AH/AD/AU/APK/AT row (a difficulty tier above 0). Caller holds _lock.</summary>
    private void LoadTierRow(string[] f)
    {
        var inv = CultureInfo.InvariantCulture;
        // f[1] = tier, f[2] = wcid, f[3] = name, then the row's own fields.
        if (f.Length < 5
            || !int.TryParse(f[1], NumberStyles.Integer, inv, out int d) || d <= 0
            || !uint.TryParse(f[2], NumberStyles.Integer, inv, out uint wcid) || wcid == 0)
            return;
        if (f[3].Length > 0) Get(wcid).Name = f[3];   // the name lives on the tier-0 profile
        var tp = Tier(wcid, d);
        switch (f[0])
        {
            case "AH" when f.Length >= 6:
                double.TryParse(f[4], NumberStyles.Float, inv, out tp.HpPool);
                int.TryParse(f[5], NumberStyles.Integer, inv, out tp.HpSamples);
                break;
            case "AD" when f.Length >= 9:
                if (uint.TryParse(f[4], NumberStyles.Integer, inv, out uint wid)
                    && int.TryParse(f[6], NumberStyles.Integer, inv, out int spellTier))
                {
                    var k = new KillStat();
                    double.TryParse(f[7], NumberStyles.Float, inv, out k.AvgCastsToKill);
                    int.TryParse(f[8], NumberStyles.Integer, inv, out k.KillSamples);
                    tp.Kills[CastKey(wid, f[5], spellTier)] = k;
                }
                break;
            case "AU" when f.Length >= 9:
                if (uint.TryParse(f[4], NumberStyles.Integer, inv, out uint uw))
                {
                    var u = new WeaponUse();
                    int.TryParse(f[5], NumberStyles.Integer, inv, out u.Hits);
                    int.TryParse(f[6], NumberStyles.Integer, inv, out u.Misses);
                    double.TryParse(f[7], NumberStyles.Float, inv, out u.SecAvg);
                    int.TryParse(f[8], NumberStyles.Integer, inv, out u.SecSamples);
                    tp.Weapons[uw] = u;
                }
                break;
            case "APK" when f.Length >= 7:
            {
                var k = new PetUse();
                int.TryParse(f[5], NumberStyles.Integer, inv, out k.Kills);
                double.TryParse(f[6], NumberStyles.Float, inv, out k.SecAvg);
                tp.Pets[f[4] == "none" ? "" : f[4]] = k;
                break;
            }
            case "AT" when f.Length >= 8:
            {
                var t = new TakenStat();
                int.TryParse(f[5], NumberStyles.Integer, inv, out t.Hits);
                double.TryParse(f[6], NumberStyles.Float, inv, out t.Avg);
                double.TryParse(f[7], NumberStyles.Float, inv, out t.Max);
                tp.Taken[f[4]] = t;
                break;
            }
        }
    }

    /// <summary>The tier rows of the file (empty when there are none). Caller holds _lock.</summary>
    private void AppendTierRows(StringBuilder sb)
    {
        if (_byTier.Count == 0) return;
        sb.Append("# Above tier 0: the server's difficulty tiers (scaled worlds, copies); tier 0 is the rows above:\n");
        sb.Append("# AH|tier|wcid|name|hpToKill|samples\n");
        sb.Append("# AD|tier|wcid|name|weaponId|element|spellTier|avgCastsToKill|killSamples\n");
        sb.Append("# AU|tier|wcid|name|weaponId|hits|misses|secToKillAvg|secSamples\n");
        sb.Append("# APK|tier|wcid|name|summonElement|kills|secToKillAvg\n");
        sb.Append("# AT|tier|wcid|name|element|hitsTaken|avgDamageTaken|maxDamageTaken\n");
        var keys = new List<(uint Wcid, int Difficulty)>(_byTier.Keys);
        keys.Sort((a, b) => a.Difficulty != b.Difficulty ? a.Difficulty.CompareTo(b.Difficulty) : a.Wcid.CompareTo(b.Wcid));
        foreach (var key in keys)
        {
            var tp = _byTier[key];
            string head = key.Difficulty.ToString(CultureInfo.InvariantCulture) + "|" + key.Wcid.ToString(CultureInfo.InvariantCulture)
                        + "|" + (_byWcid.TryGetValue(key.Wcid, out var p) ? p.Name : "");
            if (tp.HpSamples > 0)
                sb.Append("AH|").Append(head).Append('|').Append(Num(tp.HpPool)).Append('|').Append(tp.HpSamples).Append('\n');
            foreach (var k in tp.Kills)
            {
                string[] kp = k.Key.Split('|'); // weaponId|element|tier
                if (kp.Length < 3 || k.Value.KillSamples <= 0) continue;
                sb.Append("AD|").Append(head).Append('|').Append(kp[0]).Append('|').Append(kp[1]).Append('|').Append(kp[2]).Append('|')
                  .Append(Num(k.Value.AvgCastsToKill)).Append('|').Append(k.Value.KillSamples).Append('\n');
            }
            foreach (var u in tp.Weapons)
                sb.Append("AU|").Append(head).Append('|').Append(u.Key).Append('|')
                  .Append(u.Value.Hits).Append('|').Append(u.Value.Misses).Append('|')
                  .Append(Num(u.Value.SecAvg)).Append('|').Append(u.Value.SecSamples).Append('\n');
            foreach (var k in tp.Pets)
                sb.Append("APK|").Append(head).Append('|').Append(k.Key.Length == 0 ? "none" : k.Key).Append('|')
                  .Append(k.Value.Kills).Append('|').Append(Num(k.Value.SecAvg)).Append('\n');
            foreach (var t in tp.Taken)
                sb.Append("AT|").Append(head).Append('|').Append(t.Key).Append('|')
                  .Append(t.Value.Hits).Append('|').Append(Num(t.Value.Avg)).Append('|').Append(Num(t.Value.Max)).Append('\n');
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
                sb.Append("# P|wcid|name|E:element or I:essenceId  (per-monster pet choice; none = Auto)\n");
                sb.Append("# D|wcid|name|weaponId|element|tier|avgDamage|dmgSamples|avgCastsToKill|killSamples|critAvg|critSamples|nonCritAvg|nonCritSamples\n");
                sb.Append("# DEF|weaponId   (per-character default weapon — fallback for every monster without its own override)\n");
                sb.Append("# U|wcid|name|weaponId|hits|misses|secToKillAvg|secSamples   (accuracy and fight length per weapon)\n");
                sb.Append("# PK|wcid|name|summonElement (none = no summon)|kills|secToKillAvg\n");
                sb.Append("# T|wcid|name|element|hitsTaken|avgDamageTaken|maxDamageTaken\n");
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
                    if (p.PetManual.Length > 0)
                        sb.Append("P|").Append(kv.Key).Append('|').Append(name).Append('|')
                          .Append(p.PetManual).Append('\n');
                    foreach (var u in p.Weapons)
                        sb.Append("U|").Append(kv.Key).Append('|').Append(name).Append('|').Append(u.Key).Append('|')
                          .Append(u.Value.Hits).Append('|').Append(u.Value.Misses).Append('|')
                          .Append(Num(u.Value.SecAvg)).Append('|').Append(u.Value.SecSamples).Append('\n');
                    foreach (var k in p.Pets)
                        sb.Append("PK|").Append(kv.Key).Append('|').Append(name).Append('|')
                          .Append(k.Key.Length == 0 ? "none" : k.Key).Append('|')
                          .Append(k.Value.Kills).Append('|').Append(Num(k.Value.SecAvg)).Append('\n');
                    foreach (var t in p.Taken)
                        sb.Append("T|").Append(kv.Key).Append('|').Append(name).Append('|').Append(t.Key).Append('|')
                          .Append(t.Value.Hits).Append('|').Append(Num(t.Value.Avg)).Append('|').Append(Num(t.Value.Max)).Append('\n');
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
                AppendTierRows(sb);

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
        if (NotMonster(wcid) || damage <= 0) return;
        int d = Difficulty;
        lock (_lock)
        {
            SetNameLocked(wcid, name);
            Get(wcid).LastTier = tier;
            if (weaponId != 0) Use(wcid, weaponId, d).Hits++;   // hit rate: the monster's defence scales
            var s = GetCast(wcid, weaponId, element, tier);     // damage per cast: shared by every tier
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
        if (NotMonster(wcid)) return;
        int d = Difficulty;
        lock (_lock)
        {
            var p = Get(wcid);
            SetNameLocked(wcid, name);
            p.LastTier = tier;
            if (d > 0)
            {
                // A scaled tier: its own HP pool and casts-to-kill. The shared cast row is made
                // (empty) when missing so the Damage panel lists this weapon/spell.
                var tp = Tier(wcid, d);
                if (totalDamage > 0)
                {
                    tp.HpPool = tp.HpSamples == 0 ? totalDamage : tp.HpPool + Alpha * (totalDamage - tp.HpPool);
                    tp.HpSamples++;
                }
                if (castCount > 0)
                {
                    GetCast(wcid, weaponId, element, tier);
                    string key = CastKey(weaponId, element, tier);
                    if (!tp.Kills.TryGetValue(key, out var k)) tp.Kills[key] = k = new KillStat();
                    k.AvgCastsToKill = k.KillSamples == 0 ? castCount : k.AvgCastsToKill + Alpha * (castCount - k.AvgCastsToKill);
                    k.KillSamples++;
                }
                _dirty = true;
                return;
            }
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

    /// <summary>An attack or cast that did nothing: evaded, or the spell was resisted.</summary>
    public void RecordMiss(uint weaponId, uint wcid, string name)
    {
        if (NotMonster(wcid) || weaponId == 0) return;
        int d = Difficulty;
        lock (_lock)
        {
            SetNameLocked(wcid, name);
            Use(wcid, weaponId, d).Misses++;
            _dirty = true;
        }
    }

    /// <summary>
    /// A timed fight ended in our kill: <paramref name="seconds"/> from engaging it, with
    /// <paramref name="summonElement"/>: "none" = no summon out, "" = a summon of unknown element, else its element.
    /// </summary>
    public void RecordFightTime(uint weaponId, uint wcid, string name, double seconds, string? summonElement)
    {
        if (NotMonster(wcid) || seconds <= 0 || seconds > 600) return;
        int d = Difficulty;
        lock (_lock)
        {
            SetNameLocked(wcid, name);
            if (weaponId != 0)
            {
                var u = Use(wcid, weaponId, d);
                u.SecAvg = u.SecSamples == 0 ? seconds : u.SecAvg + Alpha * (seconds - u.SecAvg);
                u.SecSamples++;
            }
            // "none" = no summon out (kept, to compare against); "" = a summon of unknown element (skipped).
            if (summonElement != null && summonElement.Length > 0)
            {
                string key = summonElement == "none" ? "" : summonElement;
                var pets = d > 0 ? Tier(wcid, d).Pets : Get(wcid).Pets;
                if (!pets.TryGetValue(key, out var k)) pets[key] = k = new PetUse();
                k.SecAvg = k.Kills == 0 ? seconds : k.SecAvg + Alpha * (seconds - k.SecAvg);
                k.Kills++;
            }
            _dirty = true;
        }
    }

    /// <summary>The monster hit us for <paramref name="damage"/> of <paramref name="element"/>.</summary>
    public void RecordTaken(uint wcid, string name, string element, double damage)
    {
        if (NotMonster(wcid) || damage <= 0) return;
        int d = Difficulty;
        lock (_lock)
        {
            SetNameLocked(wcid, name);
            var taken = d > 0 ? Tier(wcid, d).Taken : Get(wcid).Taken;   // monster damage scales
            string key = string.IsNullOrEmpty(element) ? "Physical" : element;
            if (!taken.TryGetValue(key, out var t)) taken[key] = t = new TakenStat();
            t.Avg = t.Hits == 0 ? damage : t.Avg + Alpha * (damage - t.Avg);
            t.Hits++;
            if (damage > t.Max) t.Max = damage;
            _dirty = true;
        }
    }

    /// <summary>Seconds per kill over every timed fight with this monster (kills-weighted), and hit rate
    /// over every weapon (-1 when unknown). For the Damage tab's main row.</summary>
    public (double SecPerKill, double HitRate) GetSummary(uint wcid) => GetSummary(wcid, Difficulty);

    /// <summary><see cref="GetSummary(uint)"/> for one difficulty tier (0 = real Dereth).</summary>
    public (double SecPerKill, double HitRate) GetSummary(uint wcid, int difficulty)
    {
        lock (_lock)
        {
            var weapons = WeaponsAt(wcid, difficulty);
            if (weapons == null) return (-1, -1);
            double secW = 0; int secN = 0, hits = 0, misses = 0;
            foreach (var u in weapons.Values)
            {
                secW += u.SecAvg * u.SecSamples; secN += u.SecSamples;
                hits += u.Hits; misses += u.Misses;
            }
            return (secN > 0 ? secW / secN : -1, hits + misses > 0 ? (double)hits / (hits + misses) : -1);
        }
    }

    public readonly record struct WeaponUseRow(uint WeaponId, int Hits, int Misses, double SecAvg, int SecSamples);
    public readonly record struct PetUseRow(string Element, int Kills, double SecAvg);
    public readonly record struct TakenRow(string Element, int Hits, double Avg, double Max);

    /// <summary>The detail panel's extra tables for one monster.</summary>
    public (List<WeaponUseRow> Weapons, List<PetUseRow> Pets, List<TakenRow> Taken) GetDetail(uint wcid) => GetDetail(wcid, Difficulty);

    /// <summary>The detail tables at one difficulty tier (0 = real Dereth).</summary>
    public (List<WeaponUseRow> Weapons, List<PetUseRow> Pets, List<TakenRow> Taken) GetDetail(uint wcid, int difficulty)
    {
        var w = new List<WeaponUseRow>(); var pl = new List<PetUseRow>(); var tl = new List<TakenRow>();
        lock (_lock)
        {
            Dictionary<uint, WeaponUse>? weapons = null;
            Dictionary<string, PetUse>? pets = null;
            Dictionary<string, TakenStat>? taken = null;
            if (difficulty > 0)
            {
                if (_byTier.TryGetValue((wcid, difficulty), out var tp)) { weapons = tp.Weapons; pets = tp.Pets; taken = tp.Taken; }
            }
            else if (_byWcid.TryGetValue(wcid, out var p)) { weapons = p.Weapons; pets = p.Pets; taken = p.Taken; }
            if (weapons != null)
                foreach (var u in weapons) w.Add(new(u.Key, u.Value.Hits, u.Value.Misses, u.Value.SecAvg, u.Value.SecSamples));
            if (pets != null)
                foreach (var k in pets) pl.Add(new(k.Key, k.Value.Kills, k.Value.SecAvg));
            if (taken != null)
                foreach (var t in taken) tl.Add(new(t.Key, t.Value.Hits, t.Value.Avg, t.Value.Max));
        }
        return (w, pl, tl);
    }

    /// <summary>One weapon's accuracy/fight-length record at a difficulty tier (made when missing). Caller holds _lock.</summary>
    private WeaponUse Use(uint wcid, uint weaponId, int difficulty)
    {
        var d = difficulty > 0 ? Tier(wcid, difficulty).Weapons : Get(wcid).Weapons;
        if (!d.TryGetValue(weaponId, out var u)) d[weaponId] = u = new WeaponUse();
        return u;
    }

    /// <summary>The per-weapon records at a difficulty tier, or null when there are none. Caller holds _lock.</summary>
    private Dictionary<uint, WeaponUse>? WeaponsAt(uint wcid, int difficulty)
    {
        if (difficulty > 0)
            return _byTier.TryGetValue((wcid, difficulty), out var tp) ? tp.Weapons : null;
        return _byWcid.TryGetValue(wcid, out var p) ? p.Weapons : null;
    }

    /// <summary>A monster's record at a difficulty tier above 0 (made when missing). Caller holds _lock.</summary>
    private TierProfile Tier(uint wcid, int difficulty)
    {
        if (!_byTier.TryGetValue((wcid, difficulty), out var tp))
        {
            tp = new TierProfile();
            _byTier[(wcid, difficulty)] = tp;
            Get(wcid);   // the monster's name and rules live on its tier-0 profile
        }
        return tp;
    }

    /// <summary>The difficulty tiers above 0 with anything learned, ascending (empty on a server without tiers).</summary>
    public List<int> KnownDifficulties()
    {
        var set = new SortedSet<int>();
        lock (_lock)
            foreach (var k in _byTier.Keys) set.Add(k.Difficulty);
        return new List<int>(set);
    }

    /// <summary>Note the tier of the most-recent cast at a wcid (negative = ring) so the Damage tab's
    /// collapsed row shows the LATEST tier used. In-memory only (not persisted) — resets per session.</summary>
    public void NoteCast(uint wcid, int tier, string? name = null)
    {
        if (NotMonster(wcid)) return;
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
        int d = Difficulty;
        lock (_lock)
        {
            if (d > 0)
            {
                // Only this tier's own kills: a tier-0 one-shot can take two casts at tier 10.
                if (_byTier.TryGetValue((wcid, d), out var tp) && tp.Kills.TryGetValue(CastKey(weaponId, element, tier), out var k))
                {
                    killSamples = k.KillSamples;
                    return k.AvgCastsToKill;
                }
                return 0;
            }
            if (_byWcid.TryGetValue(wcid, out var p) && p.Casts.TryGetValue(CastKey(weaponId, element, tier), out var s))
            {
                killSamples = s.KillSamples;
                return s.AvgCastsToKill;
            }
        }
        return 0;
    }

    /// <summary>Learned total-damage-to-kill for this wcid, or 0 if not yet learned.</summary>
    public double GetLearnedHp(uint wcid) => GetLearnedHp(wcid, Difficulty);

    /// <summary>Learned total-damage-to-kill at one difficulty tier (0 = real Dereth), or 0.</summary>
    public double GetLearnedHp(uint wcid, int difficulty)
    {
        if (wcid == 0) return 0;
        lock (_lock)
        {
            if (difficulty > 0)
                return _byTier.TryGetValue((wcid, difficulty), out var tp) ? tp.HpPool : 0;
            return _byWcid.TryGetValue(wcid, out var p) ? p.HpPool : 0;
        }
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
        if (NotMonster(wcid)) return;
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
        if (NotMonster(wcid)) return;
        lock (_lock)
        {
            var p = Get(wcid);
            p.WeaponManual = weaponId;
            if (!string.IsNullOrEmpty(name)) SetNameLocked(wcid, name);
            _dirty = true;
        }
    }

    /// <summary>Per-monster pet choice: "" = Auto, "E:&lt;element&gt;", or "I:&lt;essence id&gt;".</summary>
    public string GetManualPet(uint wcid)
    {
        if (wcid == 0) return "";
        lock (_lock)
            return _byWcid.TryGetValue(wcid, out var p) ? p.PetManual : "";
    }

    /// <summary>Set ("" = Auto) the per-monster pet choice. Persists on next save.</summary>
    public void SetManualPet(uint wcid, string choice, string? name = null)
    {
        if (NotMonster(wcid)) return;
        lock (_lock)
        {
            var p = Get(wcid);
            p.PetManual = (choice ?? "").Replace('|', ' ').Trim();
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
        if (NotMonster(wcid)) return;
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
    /// <summary>
    /// The damage element that has worked best on this monster (fewest casts to
    /// kill, else highest average damage), from the Damage tab's learned rows.
    /// "" when nothing is learned yet.
    /// </summary>
    public string GetBestElement(uint wcid)
    {
        if (wcid == 0) return "";
        lock (_lock)
        {
            if (!_byWcid.TryGetValue(wcid, out var p) || p.Casts.Count == 0) return "";
            var byElem = new Dictionary<string, (double castsW, int kills, double dmgW, int dmg)>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in p.Casts)
            {
                string[] kp = c.Key.Split('|'); // weaponId|element|tier
                if (kp.Length < 2 || string.IsNullOrWhiteSpace(kp[1])) continue;
                var v = c.Value;
                byElem.TryGetValue(kp[1], out var a);
                a.castsW += v.AvgCastsToKill * v.KillSamples;
                a.kills  += v.KillSamples;
                a.dmgW   += v.Avg * v.Samples;
                a.dmg    += v.Samples;
                byElem[kp[1]] = a;
            }
            string bestByCasts = ""; double bestCasts = double.MaxValue;
            string bestByDmg   = ""; double bestDmg   = 0;
            foreach (var kv in byElem)
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
            return bestByCasts.Length > 0 ? bestByCasts : bestByDmg;
        }
    }

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
            string key = CastKey(weaponId, element, tier);
            bool removed = p.Casts.Remove(key);
            // The row's casts-to-kill at every difficulty tier goes with it.
            bool anyTier = false;
            foreach (var kv in _byTier)
            {
                if (kv.Key.Wcid != wcid) continue;
                if (kv.Value.Kills.Remove(key)) removed = true;
                anyTier = true;
            }
            if (removed)
            {
                if (!anyTier && p.Casts.Count == 0 && p.HpSamples == 0 && p.HpManual <= 0 && p.HpPool <= 0
                    && p.WeaponManual == 0 && p.OffhandManual == 0 && p.PetManual.Length == 0
                    && p.Weapons.Count == 0 && p.Pets.Count == 0 && p.Taken.Count == 0)
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
            var tierKeys = new List<(uint, int)>();
            foreach (var k in _byTier.Keys) if (k.Wcid == wcid) tierKeys.Add(k);
            foreach (var k in tierKeys) _byTier.Remove(k);
            removed |= tierKeys.Count > 0;
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
                p.Weapons.Clear();
                p.Pets.Clear();
                p.Taken.Clear();
                foreach (var c in p.Casts.Values)
                {
                    c.Avg = 0; c.Samples = 0;
                    c.AvgCastsToKill = 0; c.KillSamples = 0;
                    c.CritAvg = 0; c.CritSamples = 0;
                    c.NonCritAvg = 0; c.NonCritSamples = 0;
                }
            }
            _byTier.Clear();   // every difficulty tier's numbers are learned statistics too
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
    public List<DamageRow> Snapshot() => Snapshot(0);

    /// <summary>
    /// The rows as seen at one difficulty tier: damage per cast is shared, while the HP pool and
    /// casts-to-kill are that tier's own (0 / no kills where it has none). Tier 0 = today's rows.
    /// </summary>
    public List<DamageRow> Snapshot(int difficulty)
    {
        var list = new List<DamageRow>();
        lock (_lock)
        {
            foreach (var kv in _byWcid)
            {
                var p = kv.Value;
                TierProfile? tp = null;
                if (difficulty > 0) _byTier.TryGetValue((kv.Key, difficulty), out tp);
                double hpPool = difficulty > 0 ? tp?.HpPool ?? 0 : p.HpPool;
                foreach (var c in p.Casts)
                {
                    string[] kp = c.Key.Split('|'); // weaponId|element|tier
                    uint weaponId = kp.Length >= 1 && uint.TryParse(kp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint w) ? w : 0;
                    string element = kp.Length >= 2 ? kp[1] : "";
                    int tier = kp.Length >= 3 && int.TryParse(kp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) ? t : 0;
                    var v = c.Value;
                    double casts = v.AvgCastsToKill; int kills = v.KillSamples;
                    if (difficulty > 0)
                    {
                        if (tp != null && tp.Kills.TryGetValue(c.Key, out var k)) { casts = k.AvgCastsToKill; kills = k.KillSamples; }
                        else { casts = 0; kills = 0; }
                    }
                    list.Add(new DamageRow(kv.Key, p.Name, hpPool, p.HpManual, weaponId, element, tier,
                        v.Avg, v.Samples, v.CritAvg, v.CritSamples, v.NonCritAvg, v.NonCritSamples,
                        casts, kills));
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
