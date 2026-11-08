// ============================================================================
//  RynthAi - MonsterStats/MonsterCombatStats.cs
//  The combat since login (or the last reset) between you and each kind of
//  monster (by name: every Drudge Skulker together), in both directions:
//
//    You at it     swings = hits + its evades (melee and missile), with the
//                  killing blow counted as a hit; your spells that reached it
//                  = landed + resisted + no effect; kills; damage dealt.
//    It at you     attacks = hits + your evades; its spells that reached you
//                  = landed + resisted + no effect; damage taken.
//    Rates         it evades (its evades / your swings), it resists (its
//                  resists / your spells), you evade, you resist.
//    Experience    (2026-11-08) the XP of the kills whose rise was seen
//                  (KillXpMatcher) and how many: XP per kill.
//    Death         (2026-11-08) the time from each kill to its corpse (the
//                  death animation; CorpseTimer), summed, and how many.
//
//  Fed by CombatEvents (typed melee/missile events), CombatLines (spell lines)
//  and the kill notice; shown under each monster in the Damage panel (the row's
//  "mstats"), else by MonsterStatsWindow; printed by /ra mstats,
//  kept across a hot reload by MonsterStatsStore. Pure: every call takes the
//  time, so the tests drive it. One thread (the plugin pump); the window reads
//  the immutable rows of Snapshot().
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

/// <summary>How the killing blow was struck (the player's combat mode at the kill notice).</summary>
internal enum KillBlow
{
    /// <summary>Not known (peace mode): the kill is counted, the blow isn't.</summary>
    None,
    /// <summary>Melee or missile: a hit.</summary>
    Physical,
    /// <summary>Magic: a landed spell.</summary>
    Spell,
}

/// <summary>One monster's numbers, as of one moment (immutable).</summary>
internal sealed record MonsterStatsRow(
    string Name,
    int Hits, int Evaded, int SpellsLanded, int Resisted, int NoEffect, int Kills, long DamageDealt,
    int HitsTaken, int YouEvaded, int SpellsTaken, int YouResisted, int NoEffectOnYou, long DamageTaken,
    long FirstUtcTicks, long LastUtcTicks,
    long Xp = 0, int XpKills = 0,
    long CorpseMs = 0, int CorpseKills = 0)
{
    /// <summary>The experience per kill, over the kills whose XP was seen (KillXpMatcher), or null.</summary>
    public double? XpPerKill => XpKills > 0 ? (double)Xp / XpKills : null;
    /// <summary>The time from the kill to its corpse (the death animation), on average in ms, or null (CorpseTimer).</summary>
    public double? CorpseAvgMs => CorpseKills > 0 ? (double)CorpseMs / CorpseKills : null;

    /// <summary>Your melee and missile attacks at it: hits and the ones it evaded.</summary>
    public int Swings => Hits + Evaded;
    /// <summary>Your spells that reached it.</summary>
    public int SpellsAtIt => SpellsLanded + Resisted + NoEffect;
    /// <summary>Its melee and missile attacks at you.</summary>
    public int Attacks => HitsTaken + YouEvaded;
    /// <summary>Its spells that reached you.</summary>
    public int SpellsAtYou => SpellsTaken + YouResisted + NoEffectOnYou;

    public double? ItEvadesRate => MonsterCombatStats.Rate(Evaded, Swings);
    public double? ItResistsRate => MonsterCombatStats.Rate(Resisted, SpellsAtIt);
    public double? YouEvadeRate => MonsterCombatStats.Rate(YouEvaded, Attacks);
    public double? YouResistRate => MonsterCombatStats.Rate(YouResisted, SpellsAtYou);

    /// <summary>Nothing counted (a fresh row).</summary>
    public bool IsEmpty => Swings == 0 && SpellsAtIt == 0 && Kills == 0 && Attacks == 0 && SpellsAtYou == 0;
}

internal sealed class MonsterCombatStats
{
    /// <summary>Kinds of monster kept at once; past it the one seen longest ago goes.</summary>
    public const int MaxMonsters = 400;

    private sealed class Entry
    {
        public string Name = "";
        public int Hits, Evaded, SpellsLanded, Resisted, NoEffect, Kills;
        public long DamageDealt;
        public int HitsTaken, YouEvaded, SpellsTaken, YouResisted, NoEffectOnYou;
        public long DamageTaken;
        public long FirstUtcTicks, LastUtcTicks;
        public long Xp;          // experience from the kills whose rise was seen (2026-11-08)
        public int XpKills;      // those kills
        public long CorpseMs;    // kill notice to corpse, summed (the death animation, 2026-11-08)
        public int CorpseKills;  // the kills whose corpse was timed

        public MonsterStatsRow ToRow() => new(Name, Hits, Evaded, SpellsLanded, Resisted, NoEffect, Kills, DamageDealt,
            HitsTaken, YouEvaded, SpellsTaken, YouResisted, NoEffectOnYou, DamageTaken, FirstUtcTicks, LastUtcTicks, Xp, XpKills,
            CorpseMs, CorpseKills);
    }

    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Goes up on every change (the window and the save follow it).</summary>
    public int Version { get; private set; }

    /// <summary>Kinds of monster with numbers.</summary>
    public int Count => _byName.Count;

    /// <summary>A share as 0..1, or null when there is nothing to divide by.</summary>
    public static double? Rate(int part, int whole) => whole > 0 ? (double)part / whole : null;

    /// <summary>"23%" (whole from 10% up), "4.5%" (one decimal below), or "-" with nothing to divide by.</summary>
    public static string Percent(double? rate)
    {
        if (rate is not double r || !double.IsFinite(r)) return "-";
        double p = Math.Clamp(r, 0, 1) * 100;
        p = p >= 10 ? Math.Round(p, MidpointRounding.AwayFromZero) : Math.Round(p, 1, MidpointRounding.AwayFromZero);
        return p.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>True when <paramref name="name"/> has numbers this session.</summary>
    public bool IsTracked(string? name) => !string.IsNullOrEmpty(name) && _byName.ContainsKey(name);

    // ── Events ───────────────────────────────────────────────────────────

    /// <summary>A melee or missile attack, either way (CombatEvents).</summary>
    public void OnPhysical(PhysicalEvent kind, string name, int damage, DateTime nowUtc)
    {
        Entry? e = Touch(name, nowUtc);
        if (e == null) return;
        int dmg = Math.Max(0, damage);
        switch (kind)
        {
            case PhysicalEvent.YouHit: e.Hits++; e.DamageDealt += dmg; break;
            case PhysicalEvent.ItEvaded: e.Evaded++; break;
            case PhysicalEvent.ItHit: e.HitsTaken++; e.DamageTaken += dmg; break;
            case PhysicalEvent.YouEvaded: e.YouEvaded++; break;
        }
        Version++;
    }

    /// <summary>A spell line (CombatLines), either way.</summary>
    public void OnSpell(CombatLine line, DateTime nowUtc)
    {
        Entry? e = Touch(line.Name, nowUtc);
        if (e == null) return;
        int dmg = Math.Max(0, line.Damage);
        if (line.ByYou)
        {
            switch (line.Outcome)
            {
                case SpellOutcome.Landed: e.SpellsLanded++; e.DamageDealt += dmg; break;
                case SpellOutcome.Resisted: e.Resisted++; break;
                case SpellOutcome.NoEffect: e.NoEffect++; break;
            }
        }
        else
        {
            switch (line.Outcome)
            {
                case SpellOutcome.Landed: e.SpellsTaken++; e.DamageTaken += dmg; break;
                case SpellOutcome.Resisted: e.YouResisted++; break;
                case SpellOutcome.NoEffect: e.NoEffectOnYou++; break;
            }
        }
        Version++;
    }

    /// <summary>
    /// The server's kill notice ("You killed Drudge Skulker!", "Drudge Skulker died!", ...): one kill
    /// for the monster it names, and the killing blow, which sent no hit or damage line, as a hit
    /// (<see cref="KillBlow.Physical"/>) or a landed spell (<see cref="KillBlow.Spell"/>). The monster
    /// is the longest name with numbers that the notice contains, else <paramref name="nameInNotice"/>'s
    /// answer (the plugin looks among the creatures around: a monster killed by its first blow has
    /// no numbers yet). Returns the name credited, or null when the notice names none.
    /// </summary>
    public string? OnKill(string? notice, KillBlow blow, DateTime nowUtc, Func<string, string?>? nameInNotice = null)
    {
        if (string.IsNullOrWhiteSpace(notice)) return null;
        string? name = LongestNameIn(notice);
        if (name == null && nameInNotice != null)
        {
            string? n = nameInNotice(notice);
            if (!string.IsNullOrWhiteSpace(n) && notice.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) name = n.Trim();
        }
        if (name == null) return null;
        Entry? e = Touch(name, nowUtc);
        if (e == null) return null;
        e.Kills++;
        if (blow == KillBlow.Physical) e.Hits++;
        else if (blow == KillBlow.Spell) e.SpellsLanded++;
        Version++;
        return e.Name;
    }

    /// <summary>
    /// A kill of <paramref name="name"/> gave <paramref name="xp"/> (KillXpMatcher paired the rise with
    /// its kill notice): added to its XP and its kills with XP counted. Kills whose rise wasn't seen
    /// stay out of both, so XP/kill is never diluted by them.
    /// </summary>
    public void OnKillXp(string? name, long xp, DateTime nowUtc)
    {
        if (xp <= 0) return;
        Entry? e = Touch(name, nowUtc);
        if (e == null) return;
        e.Xp += xp;
        e.XpKills++;
        Version++;
    }

    /// <summary>A kill of <paramref name="name"/> left its corpse <paramref name="ms"/> after the kill notice (CorpseTimer).</summary>
    public void OnCorpseTime(string? name, long ms, DateTime nowUtc)
    {
        if (ms < 0) return;
        Entry? e = Touch(name, nowUtc);
        if (e == null) return;
        e.CorpseMs += ms;
        e.CorpseKills++;
        Version++;
    }

    private string? LongestNameIn(string notice)
    {
        string? best = null;
        foreach (string n in _byName.Keys)
            if ((best == null || n.Length > best.Length) && notice.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                best = n;
        return best;
    }

    /// <summary>Starts every monster over.</summary>
    public void Reset()
    {
        _byName.Clear();
        Version++;
    }

    // ── Reading ──────────────────────────────────────────────────────────

    /// <summary>Every monster's numbers now, most recently fought first.</summary>
    public MonsterStatsRow[] Snapshot()
    {
        var rows = new MonsterStatsRow[_byName.Count];
        int i = 0;
        foreach (Entry e in _byName.Values) rows[i++] = e.ToRow();
        Array.Sort(rows, (a, b) =>
        {
            int c = b.LastUtcTicks.CompareTo(a.LastUtcTicks);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return rows;
    }

    /// <summary>One monster's numbers by its exact name (any case), or null.</summary>
    public MonsterStatsRow? Get(string? name)
        => !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out Entry? e) ? e.ToRow() : null;

    /// <summary>
    /// The monster <paramref name="query"/> means: its exact name (any case), else the one name that
    /// contains it. Null with the candidates (up to <paramref name="maxCandidates"/>, recent first)
    /// when several do, and with none when nothing does.
    /// </summary>
    public MonsterStatsRow? Find(string? query, out List<string> candidates, int maxCandidates = 8)
    {
        candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = query.Trim();
        if (_byName.TryGetValue(q, out Entry? exact)) return exact.ToRow();
        MonsterStatsRow? only = null;
        int matches = 0;
        foreach (MonsterStatsRow r in Snapshot())
        {
            if (r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            matches++;
            only = r;
            if (candidates.Count < maxCandidates) candidates.Add(r.Name);
        }
        if (matches == 1) { candidates.Clear(); return only; }
        return null;
    }

    // ── Saving (MonsterStatsStore) ───────────────────────────────────────

    /// <summary>Puts saved rows back in place of what there is (a hot reload's carried session).</summary>
    public void Load(IEnumerable<MonsterStatsRow> rows)
    {
        _byName.Clear();
        foreach (MonsterStatsRow r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || _byName.ContainsKey(r.Name)) continue;
            _byName[r.Name] = new Entry
            {
                Name = r.Name,
                Hits = Math.Max(0, r.Hits), Evaded = Math.Max(0, r.Evaded),
                SpellsLanded = Math.Max(0, r.SpellsLanded), Resisted = Math.Max(0, r.Resisted), NoEffect = Math.Max(0, r.NoEffect),
                Kills = Math.Max(0, r.Kills), DamageDealt = Math.Max(0, r.DamageDealt),
                HitsTaken = Math.Max(0, r.HitsTaken), YouEvaded = Math.Max(0, r.YouEvaded),
                SpellsTaken = Math.Max(0, r.SpellsTaken), YouResisted = Math.Max(0, r.YouResisted), NoEffectOnYou = Math.Max(0, r.NoEffectOnYou),
                DamageTaken = Math.Max(0, r.DamageTaken),
                FirstUtcTicks = r.FirstUtcTicks, LastUtcTicks = r.LastUtcTicks,
                Xp = Math.Max(0, r.Xp), XpKills = Math.Max(0, r.XpKills),
                CorpseMs = Math.Max(0, r.CorpseMs), CorpseKills = Math.Max(0, r.CorpseKills),
            };
            if (_byName.Count >= MaxMonsters) break;
        }
        Version++;
    }

    // ── Internals ────────────────────────────────────────────────────────

    /// <summary>The monster's entry (made on first sight), its last-seen time now; null for no name.</summary>
    private Entry? Touch(string? name, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = name.Trim();
        if (n.Length > 64) n = n.Substring(0, 64);
        long now = nowUtc.Ticks;
        if (!_byName.TryGetValue(n, out Entry? e))
        {
            if (_byName.Count >= MaxMonsters) DropOldest();
            e = new Entry { Name = n, FirstUtcTicks = now };
            _byName[n] = e;
        }
        e.LastUtcTicks = now;
        return e;
    }

    private void DropOldest()
    {
        string? oldest = null;
        long at = long.MaxValue;
        foreach (Entry e in _byName.Values)
            if (e.LastUtcTicks < at) { at = e.LastUtcTicks; oldest = e.Name; }
        if (oldest != null) _byName.Remove(oldest);
    }
}
