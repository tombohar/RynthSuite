using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// "Debuff every monster in range before attacking" (setting DebuffAllBeforeAttacking, off by
/// default). Which monster gets the next debuff cast, and when attacking may start. Pure state, no
/// host calls; CombatManager feeds it the scan (nearest first) and says which monsters still need a
/// debuff (the monster's own rule: Imperil, vulns, Void ones, typed-in ones, not on it and castable).
///
///   * A fight starts with the pass: the monsters in Monster Range that need a debuff get them,
///     nearest first, one cast at a time. Nothing is attacked until every one has them, or has
///     <see cref="MaxFailsPerMonster"/> failed tries (resists, fizzles, refusals) or
///     <see cref="MaxTriesPerMonster"/> casts in all, or the pass has run <see cref="PassSeconds"/>.
///     The fight target's own debuffs are cast by the usual per-target code just before this.
///   * A monster hitting the character in melee ("on you") stops the pass while it does: the fight
///     goes on as without the setting (the fight target's own debuffs, then the attack), and the pass
///     picks up again once nothing is on you (while its time lasts). No arrival is debuffed meanwhile.
///   * Once attacking has started, a monster that comes into range later (an arrival) gets its debuffs
///     too, one cast between two attacks on the fight target, for <see cref="ArrivalSeconds"/> after it
///     came. The fight target is never changed for it. Monsters of the first pass aren't renewed here
///     (the fight target's own code renews the target's).
///   * The pet summon waits while the pass runs (<see cref="Holding"/>), and for at most
///     <see cref="IdleHoldSeconds"/> before the fight's first cast begins it.
///   * The fight ends when nothing has been in range and nothing engaged for
///     <see cref="FightOverSeconds"/>: everything starts over for the next one.
/// </summary>
internal sealed class DebuffPass
{
    public const int MaxFailsPerMonster = 3;
    public const int MaxTriesPerMonster = 8;
    public const double PassSeconds = 30.0;
    public const double ArrivalSeconds = 30.0;
    public const double FightOverSeconds = 3.0;
    /// <summary>The pet summon's wait before the pass has begun (the fight's first cast is coming).</summary>
    public const double IdleHoldSeconds = 5.0;
    private const int MaxMonsters = 256;

    public enum Stage { Idle, Debuffing, Attacking }

    private sealed class Mon
    {
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public bool Arrival;
        public int Tries;
        public int Fails;
        public bool GaveUp;
    }

    private readonly Dictionary<int, Mon> _mons = new();
    private DateTime _emptySince = DateTime.MinValue;
    private bool _castSinceAttack;
    private bool _onYouLogged;

    public Stage Phase { get; private set; } = Stage.Idle;
    public DateTime StartedAt { get; private set; } = DateTime.MinValue;
    public DateTime EndedAt { get; private set; } = DateTime.MinValue;
    /// <summary>Why the last pass ended ("" while none has).</summary>
    public string EndReason { get; private set; } = "";
    /// <summary>Debuff casts the pass (or an arrival) sent this fight.</summary>
    public int Casts { get; private set; }

    /// <summary>One [DebuffPass] line (CombatManager: the RynthAi log).</summary>
    public Action<string>? Log { get; set; }
    /// <summary>A monster's name for the log lines.</summary>
    public Func<int, string>? NameOf { get; set; }

    /// <summary>
    /// Every scan: the monsters in Monster Range that passed the scan's filters, and whether a target
    /// is engaged. New ones are noted (an arrival once attacking has started); a fight with nothing in
    /// range and nothing engaged for <see cref="FightOverSeconds"/> is over.
    /// </summary>
    public void Observe(IReadOnlyList<int> inRange, bool engaged, DateTime now)
    {
        InRange = inRange.Count;
        if (inRange.Count > 0 && FightSeenAt == DateTime.MinValue) FightSeenAt = now;
        if (inRange.Count == 0 && !engaged)
        {
            if (Phase == Stage.Idle && _mons.Count == 0) return;
            if (_emptySince == DateTime.MinValue) _emptySince = now;
            else if ((now - _emptySince).TotalSeconds >= FightOverSeconds)
            {
                if (Phase != Stage.Idle)
                    Log?.Invoke($"[DebuffPass] fight over: {Casts} debuff casts; the next fight starts a new pass");
                Reset();
            }
            return;
        }
        _emptySince = DateTime.MinValue;
        for (int i = 0; i < inRange.Count; i++)
        {
            int id = inRange[i];
            if (_mons.TryGetValue(id, out Mon? m)) { m.LastSeen = now; continue; }
            if (_mons.Count >= MaxMonsters) Prune(now);
            _mons[id] = new Mon { FirstSeen = now, LastSeen = now, Arrival = Phase == Stage.Attacking };
        }
    }

    /// <summary>
    /// The monster to debuff now, or 0 to attack (or when there is nothing to do). Starts the pass at
    /// a fight's first call and ends it when every monster has its debuffs, is given up on, or time is
    /// up. <paramref name="nearestFirst"/>: the scan's monsters in Monster Range, nearest first;
    /// <paramref name="needsDebuff"/>: one of its debuffs isn't on it and this character can cast it.
    /// </summary>
    public int Next(IReadOnlyList<int> nearestFirst, int fightTarget, Func<int, bool> needsDebuff, bool monsterOnYou, DateTime now)
    {
        Begin(nearestFirst, now);
        if (Phase == Stage.Idle) return 0;

        if (Phase == Stage.Debuffing)
        {
            if (monsterOnYou)
            {
                if (!_onYouLogged)
                {
                    _onYouLogged = true;
                    Log?.Invoke("[DebuffPass] a monster is hitting you in melee: the fight target is debuffed and attacked first; the pass waits");
                }
                return 0;
            }
            if (_onYouLogged)
            {
                _onYouLogged = false;
                Log?.Invoke("[DebuffPass] nothing on you now: the pass goes on");
            }
            if ((now - StartedAt).TotalSeconds >= PassSeconds)
            {
                End(now, $"time cap ({PassSeconds:0} s)");
                return 0;
            }
            foreach (int id in nearestFirst)
            {
                if (id == fightTarget || !Eligible(id)) continue;
                if (needsDebuff(id)) return id;
            }
            End(now, "every monster in range has its debuffs or was given up on");
            return 0;
        }

        // Attacking: newcomers only, one debuff between two attacks on the fight target; none while
        // something is hitting you.
        if (_castSinceAttack || monsterOnYou) return 0;
        foreach (int id in nearestFirst)
        {
            if (id == fightTarget || !Eligible(id)) continue;
            Mon m = _mons[id];
            if (!m.Arrival || (now - m.FirstSeen).TotalSeconds > ArrivalSeconds) continue;
            if (needsDebuff(id)) return id;
        }
        return 0;
    }

    /// <summary>
    /// A fight's first attack decision (Magic mode): two or more monsters in range start the pass
    /// (logged); one alone starts the fight attacking (a later one is an arrival). Nothing otherwise.
    /// </summary>
    public void Begin(IReadOnlyList<int> nearestFirst, DateTime now)
    {
        if (Phase != Stage.Idle || nearestFirst.Count == 0) return;
        foreach (int id in nearestFirst)
            if (!_mons.ContainsKey(id)) _mons[id] = new Mon { FirstSeen = now, LastSeen = now };
        foreach (var m in _mons.Values) m.Arrival = false;
        StartedAt = now;
        EndReason = "";
        Casts = 0;
        if (nearestFirst.Count < 2)
        {
            // One monster: nothing to wait for (its own debuffs come first as always).
            Phase = Stage.Attacking;
            EndedAt = now;
            EndReason = "one monster";
            return;
        }
        Phase = Stage.Debuffing;
        Log?.Invoke($"[DebuffPass] start: {nearestFirst.Count} monsters in range, debuffing each before attacking");
    }

    private bool Eligible(int id)
    {
        if (!_mons.TryGetValue(id, out Mon? m))
        {
            // Not observed yet (the caller's list is newer than the last scan): treat it as seen now.
            _mons[id] = m = new Mon { FirstSeen = DateTime.MinValue, LastSeen = DateTime.MinValue, Arrival = false };
        }
        return !m.GaveUp;
    }

    /// <summary>
    /// True while the pass holds the pet summon: debuffing, inside its time, nothing on you; or, before
    /// the fight's first attack decision, for <see cref="IdleHoldSeconds"/> after two or more monsters
    /// came into range (the caller checks the pass can run at all).
    /// </summary>
    public bool Holding(DateTime now) => Phase switch
    {
        Stage.Debuffing => (now - StartedAt).TotalSeconds < PassSeconds && !_onYouLogged,
        Stage.Idle => InRange >= 2 && FightSeenAt != DateTime.MinValue && (now - FightSeenAt).TotalSeconds < IdleHoldSeconds,
        _ => false,
    };

    /// <summary>Monsters in range at the last <see cref="Observe"/>.</summary>
    public int InRange { get; private set; }
    /// <summary>When this fight's first monster came into range (MinValue between fights).</summary>
    public DateTime FightSeenAt { get; private set; } = DateTime.MinValue;

    /// <summary>A pass debuff went out at <paramref name="id"/>.</summary>
    public void NoteCast(int id, DateTime now)
    {
        Casts++;
        if (Phase == Stage.Attacking) _castSinceAttack = true;
        if (!_mons.TryGetValue(id, out Mon? m)) _mons[id] = m = new Mon { FirstSeen = now, LastSeen = now };
        if (++m.Tries >= MaxTriesPerMonster && !m.GaveUp)
        {
            m.GaveUp = true;
            Log?.Invoke($"[DebuffPass] 0x{(uint)id:X8} '{Name(id)}': {MaxTriesPerMonster} debuff casts this fight, no more from the pass");
        }
    }

    /// <summary>A pass debuff at <paramref name="id"/> was resisted, fizzled or refused. True when that gave it up.</summary>
    public bool NoteFailed(int id)
    {
        if (!_mons.TryGetValue(id, out Mon? m)) return false;
        if (m.GaveUp || ++m.Fails < MaxFailsPerMonster) return false;
        m.GaveUp = true;
        Log?.Invoke($"[DebuffPass] 0x{(uint)id:X8} '{Name(id)}': {MaxFailsPerMonster} failed tries, skipped (the pass doesn't wait on it)");
        return true;
    }

    /// <summary>An attack (war/void cast, swing or shot) went out at the fight target.</summary>
    public void NoteAttack() => _castSinceAttack = false;

    /// <summary>The monster died or went away.</summary>
    public void Forget(int id) => _mons.Remove(id);

    public bool GaveUp(int id) => _mons.TryGetValue(id, out Mon? m) && m.GaveUp;
    public int Tries(int id) => _mons.TryGetValue(id, out Mon? m) ? m.Tries : 0;
    public bool IsArrival(int id) => _mons.TryGetValue(id, out Mon? m) && m.Arrival;
    public int Known => _mons.Count;

    public void Reset()
    {
        _mons.Clear();
        Phase = Stage.Idle;
        _emptySince = DateTime.MinValue;
        _castSinceAttack = false;
        _onYouLogged = false;
        StartedAt = DateTime.MinValue;
        FightSeenAt = DateTime.MinValue;
        InRange = 0;
        Casts = 0;
    }

    private void End(DateTime now, string why)
    {
        Phase = Stage.Attacking;
        EndedAt = now;
        EndReason = why;
        int gaveUp = 0;
        foreach (var m in _mons.Values) if (m.GaveUp) gaveUp++;
        Log?.Invoke($"[DebuffPass] done after {(now - StartedAt).TotalSeconds:0.0} s ({why}): {Casts} debuff casts" +
                    (gaveUp > 0 ? $", {gaveUp} monster{(gaveUp == 1 ? "" : "s")} skipped" : "") + "; attacking now");
    }

    private string Name(int id)
    {
        try { return NameOf?.Invoke(id) ?? ""; } catch { return ""; }
    }

    private void Prune(DateTime now)
    {
        List<int>? drop = null;
        foreach (var kv in _mons)
            if ((now - kv.Value.LastSeen).TotalSeconds > 60) (drop ??= new()).Add(kv.Key);
        if (drop != null) foreach (int id in drop) _mons.Remove(id);
        while (_mons.Count >= MaxMonsters)
        {
            int oldest = 0;
            DateTime best = DateTime.MaxValue;
            foreach (var kv in _mons)
                if (kv.Value.LastSeen < best) { best = kv.Value.LastSeen; oldest = kv.Key; }
            _mons.Remove(oldest);
        }
    }
}
