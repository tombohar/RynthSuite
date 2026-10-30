using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// One weapon swap as a short list of server steps, each sent the moment the server has answered
/// the last (2026-10-22). Pure: the caller reads the hands (EquippedState, id -> EquipMask), the
/// combat mode and the clock, does what <see cref="Next"/> says and reports it with <see cref="Sent"/>.
///
/// Why (Drakkon, DreamWeave, 2026-10-22 21:25): Acid + Frost Hand Wraps -> Sceptre took 7.2 s to
/// the first cast and about 3 s with no weapon in hand. The off hand went into the pack (answered in
/// 1.24 s), then 1.8 s of nothing, the main hand (answered in 2 ms), 2.9 s of nothing, the wand. The
/// waits were the swap gate's flat 3 s after every pack move. The client's own use of the wand was
/// no better while dual wielding: it takes the main hand first, one server answer per step (3.7 s).
///
/// What ACE does (Player_Inventory.cs, Player_Combat.cs, Creature_Combat.cs; the stance lengths from
/// the human motion table, 0x09000001):
///   * Every stance change goes through peace: melee -> Magic is melee -> peace (0.55 s) plus
///     peace -> Magic (0.52 s). A change to or from peace is one leg.
///   * Putting a wielded weapon into a pack in a combat mode first plays a forced change to bare
///     hands (1.15 s) and moves the item when it ends; after the move the server picks a stance for
///     what is left in hand, another change (and two weapons bounce between stances). In peace the
///     move is answered at once, with no stance change.
///   * A Held-slot caster needs both hands empty (CheckWeaponCollision); a wield runs at once.
///   * A cast that arrives while a stance change is still queued is cancelled by it; on a PK
///     character (DreamWeave casts that way) a cast can be left hanging behind an overlapping stance
///     animation (the 2 min 45 s hold on 10-06 13:58).
/// So, from a combat mode: peace first (one stance leg), both hands into the pack (each answered in a
/// round trip), the wand, then Magic (one leg). Magic is up about 0.6 s in instead of 2.4 s (the best
/// order in melee mode) or 6 s (before), and no stance change is ever queued behind another. Peace
/// drops weapon defense, for about 0.6 s; the melee route keeps it up to the last weapon but gets
/// to Magic 1.2-3.6 s later, and the old route went without a weapon in hand for about 3 s.
///
/// Bounded: a step unanswered in <see cref="StepResolveMs"/> is sent again, at most
/// <see cref="MaxSendsPerStep"/> times; the whole swap gives up after <see cref="GiveUpMs"/>.
/// </summary>
internal sealed class HandSwapSequence
{
    public const int MeleeSlot = 0x00100000, ShieldSlot = 0x00200000, MissileSlot = 0x00400000,
                     AmmoSlot = 0x00800000, HeldSlot = 0x01000000, TwoHandedSlot = 0x02000000;
    /// <summary>The slots that hold a hand (ammo is not one: arrows stay on).</summary>
    public const int HandSlots = MeleeSlot | ShieldSlot | MissileSlot | HeldSlot | TwoHandedSlot;
    /// <summary>AC combat modes (CombatMode).</summary>
    public const int NonCombat = 1, Melee = 2, Missile = 4, Magic = 8;

    public enum Act
    {
        /// <summary>A step is on its way to the server: nothing to send.</summary>
        Wait,
        /// <summary>Ask for combat mode <see cref="Step.Slot"/> (peace before the hands are cleared).</summary>
        Stance,
        /// <summary>Put <see cref="Step.Id"/> into a pack.</summary>
        MoveToPack,
        /// <summary>Wield <see cref="Step.Id"/> into <see cref="Step.Slot"/>.</summary>
        Wield,
        /// <summary>Everything wanted is in hand (or the sequence isn't running).</summary>
        Done,
        /// <summary>It can't finish now (<see cref="Step.Why"/>); the sequence has stopped.</summary>
        Failed,
    }

    public readonly record struct Step(Act Act, int Id = 0, int Slot = 0, string Why = "");

    /// <summary>A step's answer: the engine may hold an item action while the character animates (up to
    /// 2.5 s, AcMainThreadQueue), then the server's answer (a forced stance change, about 1.2 s, if it
    /// isn't in peace) and the round trip.</summary>
    public double StepResolveMs { get; set; } = 4000;
    /// <summary>The peace step: the mode reads peace within this, or the swap goes on without it.</summary>
    public double PeaceResolveMs { get; set; } = 1500;
    public int MaxSendsPerStep { get; set; } = 2;
    public double GiveUpMs { get; set; } = 12_000;

    private readonly List<(int Id, int Slot)> _wants = new();
    private readonly HashSet<int> _keep = new();
    private readonly Dictionary<int, int> _sends = new();
    private readonly StringBuilder _trail = new();
    private DateTime _startedAt, _flightAt;
    private Act _flightAct = Act.Wait;   // Wait = nothing in flight
    private int _flightId, _flightSlot;
    private bool _peaceWanted, _peaceDone;
    // The burst (SentBurst): peace, every move and the wield sent in one frame.
    private bool _burst;
    private DateTime _burstAt;
    private readonly HashSet<int> _burstPending = new();
    /// <summary>A burst's steps all land within this, or the rest goes one step at a time.</summary>
    public double BurstResolveMs { get; set; } = 2500;

    public bool Active { get; private set; }
    public string Label { get; private set; } = "";
    public DateTime StartedAt => _startedAt;
    /// <summary>The item of the step on its way (0 = none, or a stance step).</summary>
    public int InFlightId => _flightAct == Act.Wait ? 0 : _flightId;
    /// <summary>A step or a burst is on its way to the server (a new Start now could send it twice).</summary>
    public bool InFlight => _burst || _flightAct != Act.Wait;
    /// <summary>What the swap ends with, in order.</summary>
    public IReadOnlyList<(int Id, int Slot)> Wants => _wants;
    /// <summary>The item whose step went unanswered when the swap failed (0 = it failed some other way).</summary>
    public int FailedId { get; private set; }
    /// <summary>The steps so far with the server's answer times, for the log.</summary>
    public string Trail => _trail.ToString();
    /// <summary>The swap went through peace (the caller then asks for the stance its wants need).</summary>
    public bool WentThroughPeace { get; private set; }

    /// <summary>
    /// Begins a swap that ends with each of <paramref name="wants"/> wielded in its slot, in order.
    /// <paramref name="peaceFirst"/>: from a combat mode, ask for peace before anything leaves a hand.
    /// Items in <paramref name="keep"/> are never taken off: one blocking a want fails the swap.
    /// </summary>
    public void Start(string label, DateTime now, IReadOnlyList<(int Id, int Slot)> wants, bool peaceFirst = false, IEnumerable<int>? keep = null)
    {
        _wants.Clear();
        _wants.AddRange(wants);
        _keep.Clear();
        if (keep != null) foreach (int k in keep) if (k != 0) _keep.Add(k);
        _sends.Clear();
        _trail.Clear();
        _startedAt = now;
        _flightAct = Act.Wait;
        _flightId = _flightSlot = 0;
        _peaceWanted = peaceFirst;
        _peaceDone = false;
        _burst = false;
        _burstPending.Clear();
        WentThroughPeace = false;
        FailedId = 0;
        Label = label;
        Active = true;
    }

    public void Cancel()
    {
        Active = false;
        _flightAct = Act.Wait;
        _flightId = _flightSlot = 0;
    }

    /// <summary>What to do now, given what the server says is wielded (item id -> EquipMask) and the combat mode.</summary>
    public Step Next(IReadOnlyDictionary<int, int> wielded, int combatMode, DateTime now)
    {
        if (!Active) return new Step(Act.Done);

        if (_burst)
        {
            double bms = (now - _burstAt).TotalMilliseconds;
            if (combatMode == NonCombat) WentThroughPeace = true;
            foreach (int id in new List<int>(_burstPending))
            {
                int slot = WantedSlot(id);
                bool landed = slot != 0 ? wielded.TryGetValue(id, out int at) && (at & slot) != 0 : !wielded.ContainsKey(id);
                if (!landed) continue;
                Note($"{(slot != 0 ? "on" : "off")} 0x{(uint)id:X8} {bms / 1000:0.00} s");
                _burstPending.Remove(id);
            }
            if (_burstPending.Count > 0 && bms < BurstResolveMs) return new Step(Act.Wait);
            if (_burstPending.Count > 0) Note($"burst incomplete after {bms / 1000:0.0} s: the rest one step at a time");
            _burst = false;
        }

        if (_flightAct != Act.Wait)
        {
            double ms = (now - _flightAt).TotalMilliseconds;
            bool landed = _flightAct switch
            {
                Act.Stance => combatMode == _flightSlot,
                Act.MoveToPack => !wielded.ContainsKey(_flightId),
                _ => wielded.TryGetValue(_flightId, out int at) && (at & _flightSlot) != 0,
            };
            if (landed)
            {
                Note(_flightAct switch
                {
                    Act.Stance => $"peace {ms / 1000:0.00} s",
                    Act.MoveToPack => $"off 0x{(uint)_flightId:X8} {ms / 1000:0.00} s",
                    _ => $"on 0x{(uint)_flightId:X8} {ms / 1000:0.00} s",
                });
                if (_flightAct == Act.Stance) { _peaceDone = true; WentThroughPeace = true; }
                _flightAct = Act.Wait;
            }
            else if (_flightAct == Act.Stance)
            {
                if (ms < PeaceResolveMs) return new Step(Act.Wait, 0, _flightSlot);
                // Not read in time: go on without it (the moves still work in a combat mode, slower).
                Note($"peace not read in {ms / 1000:0.0} s");
                _peaceDone = true;
                _flightAct = Act.Wait;
            }
            else if (ms < StepResolveMs)
                return new Step(Act.Wait, _flightId, _flightSlot);
            else
            {
                Note($"0x{(uint)_flightId:X8} no answer in {ms / 1000:0.0} s");
                int id = _flightId;
                _flightAct = Act.Wait;
                if (_sends.TryGetValue(id, out int n) && n >= MaxSendsPerStep)
                {
                    FailedId = id;
                    return Fail($"0x{(uint)id:X8} wasn't answered after {n} sends");
                }
            }
        }

        if ((now - _startedAt).TotalMilliseconds > GiveUpMs)
            return Fail($"not done in {GiveUpMs / 1000:0} s");

        // Anything in a hand that isn't wanted there comes off first: the off hand before the rest.
        int blocker = 0;
        foreach (var kv in wielded)
        {
            if ((kv.Value & HandSlots) == 0 || WantedThere(kv.Key, kv.Value)) continue;
            if (blocker == 0 || (kv.Value & ShieldSlot) != 0) blocker = kv.Key;
            if ((kv.Value & ShieldSlot) != 0) break;
        }
        if (blocker != 0 && _keep.Contains(blocker))
            return Fail($"0x{(uint)blocker:X8} is in the way and may not be taken off");

        // Peace before the first item leaves a hand: the moves are then answered at once, and the
        // stance changes the server makes around a combat-mode unwield never happen.
        if (blocker != 0 && _peaceWanted && !_peaceDone)
        {
            if (combatMode == NonCombat) _peaceDone = true;
            else return new Step(Act.Stance, 0, NonCombat);
        }

        if (blocker != 0) return new Step(Act.MoveToPack, blocker, 0);

        foreach (var (id, slot) in _wants)
        {
            if (wielded.TryGetValue(id, out int at) && (at & slot) != 0) continue;
            return new Step(Act.Wield, id, slot);
        }

        Active = false;
        return new Step(Act.Done);
    }

    /// <summary>The caller sent <paramref name="step"/> (a stance request, a pack move or a wield).</summary>
    public void Sent(Step step, DateTime now)
    {
        if (step.Act is not (Act.Stance or Act.MoveToPack or Act.Wield)) return;
        _flightAct = step.Act;
        _flightId = step.Id;
        _flightSlot = step.Slot;
        _flightAt = now;
        if (step.Act != Act.Stance)
            _sends[step.Id] = (_sends.TryGetValue(step.Id, out int n) ? n : 0) + 1;
    }

    /// <summary>
    /// What would come off first, in order (the off hand before the rest), for a burst: every item in a
    /// hand that isn't wanted where it is. Empty when one may not be taken off.
    /// </summary>
    public List<int> Blockers(IReadOnlyDictionary<int, int> wielded)
    {
        var off = new List<int>();
        var rest = new List<int>();
        foreach (var kv in wielded)
        {
            if ((kv.Value & HandSlots) == 0 || WantedThere(kv.Key, kv.Value)) continue;
            if (_keep.Contains(kv.Key)) return new List<int>();
            ((kv.Value & ShieldSlot) != 0 ? off : rest).Add(kv.Key);
        }
        off.AddRange(rest);
        return off;
    }

    /// <summary>
    /// The caller sent peace, every move in <paramref name="moved"/> and the wield of each want in one
    /// frame (BuffManager's burst). Next waits for all of them to land (<see cref="BurstResolveMs"/>),
    /// then goes on one step at a time with whatever didn't.
    /// </summary>
    public void SentBurst(IEnumerable<int> moved, DateTime now)
    {
        _burst = true;
        _burstAt = now;
        _peaceDone = true;
        _burstPending.Clear();
        foreach (int id in moved) _burstPending.Add(id);
        foreach (var (id, _) in _wants) _burstPending.Add(id);
        foreach (int id in _burstPending) _sends[id] = (_sends.TryGetValue(id, out int n) ? n : 0) + 1;
        _flightAct = Act.Wait;
        Note("burst");
    }

    private int WantedSlot(int id)
    {
        foreach (var w in _wants) if (w.Id == id) return w.Slot;
        return 0;
    }

    /// <summary>The caller couldn't send the step (no pack with room, no wield call): stop here.</summary>
    public Step Abandon(string why) => Fail(why);

    private bool WantedThere(int id, int slot)
    {
        foreach (var w in _wants)
            if (w.Id == id && (slot & w.Slot) != 0) return true;
        return false;
    }

    private Step Fail(string why)
    {
        Note("failed: " + why);
        Cancel();
        return new Step(Act.Failed, Why: why);
    }

    private void Note(string s)
    {
        if (_trail.Length > 0) _trail.Append(", ");
        _trail.Append(s);
    }
}
