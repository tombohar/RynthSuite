// ============================================================================
//  RynthSuite - Plugins/Shared/RynthAiCombatApi.cs
//  The "RynthAi.Combat" interface: lets a script plugin (RynthLua) choose the
//  spell for RynthAi's next attack cast or vitals heal, while RynthAi keeps
//  targeting, pacing, stance and the cast itself. Compiled into both plugins
//  (linked source), so the layout and the constants can't drift between them.
//
//  The exchange is polled, never a callback:
//    1. The script side calls Claim every tick for the kinds it handles (a
//       lease: when it stops renewing, RynthAi goes native on its own).
//    2. When RynthAi reaches a cast choice it opens a request (a new Seq) and
//       waits, holding that one cast, up to the request's deadline.
//    3. The script side sees it through Poll, decides, and answers with Decide.
//    4. RynthAi validates the answer and casts it, or (spell 0, an invalid
//       answer, or no answer by the deadline) makes its own choice that tick.
//
//  Rules (as RynthAi.Script, Shared/RynthAiScriptApi.cs):
//  - Its own interface, so RynthAi.Script v1 (exact-version checked) and
//    RynthAi.Status stay as they are.
//  - Append-only: new functions go at the end, and Version goes up. New
//    request fields also go at the end but do not bump Version: they are gated
//    by StructSize. The caller sets CombatRequestV1.StructSize before Poll;
//    RynthAi never writes past it and writes back how much it filled, so a
//    reader uses an appended field only when that StructSize covers it.
//  - Strings are UTF-8, NUL-terminated.
//  - Call only from the plugin pump thread (OnTick), never from AC's thread or Render.
//  - Every function returns safely (no exception crosses the boundary).
//  - Times are Environment.TickCount64 milliseconds (one clock per process).
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

/// <summary>Constants of the "RynthAi.Combat" exchange: kinds, decision flags, lease and timeout bounds.</summary>
public static class RynthAiCombat
{
    /// <summary>Kind bit: the spell of RynthAi's next attack cast (Magic mode, after its debuffs).</summary>
    public const uint KindAttack = 1;
    /// <summary>Kind bit: the spell of the next vitals heal (health under Heal At / Top Off HP), asked before kits.</summary>
    public const uint KindHeal = 2;
    /// <summary>Every kind this version knows.</summary>
    public const uint KindsAll = KindAttack | KindHeal;

    /// <summary>Decision flag: an area cast (ring, wall, chain): no blacklist or pending-offensive judgement on it.</summary>
    public const uint FlagArea = 1;

    /// <summary>
    /// Decision flag: a single-target cast that is not meant to damage (a debuff, a drain, a
    /// utility spell): no no-damage blacklist judgement on it. Flags are append-only; RynthAi
    /// ignores bits it does not know.
    /// </summary>
    public const uint FlagNoJudge = 2;

    /// <summary>Lease length when Claim passes 0, and its bounds (clamped).</summary>
    public const uint DefaultLeaseMs = 1500;
    public const uint MinLeaseMs = 500;
    public const uint MaxLeaseMs = 3000;

    /// <summary>How long RynthAi waits for an answer before casting its own choice (0 = default), and the bounds.</summary>
    public const uint DefaultAttackTimeoutMs = 1000;
    public const uint DefaultHealTimeoutMs = 750;
    public const uint MinTimeoutMs = 100;
    public const uint MaxTimeoutMs = 3000;

    /// <summary>
    /// A cast with no answer (UseDone) this long after its send is resolved with no answer (error -1).
    /// The pacing's own cast limit (ActionPacer.CastMaxMs).
    /// </summary>
    public const long ResolveTimeoutMs = 7000;

    /// <summary>Longest owner name RynthAi keeps (characters).</summary>
    public const int MaxOwnerLength = 64;

    /// <summary>"attack", "heal", "attack+heal" or "none".</summary>
    public static string KindNames(uint kinds)
    {
        kinds &= KindsAll;
        if (kinds == 0) return "none";
        if (kinds == KindsAll) return "attack+heal";
        return kinds == KindAttack ? "attack" : "heal";
    }

    /// <summary>A lease length within the bounds (0 = the default).</summary>
    public static uint ClampLease(uint ms) => ms == 0 ? DefaultLeaseMs : ms < MinLeaseMs ? MinLeaseMs : ms > MaxLeaseMs ? MaxLeaseMs : ms;

    /// <summary>A request timeout within the bounds (0 = <paramref name="fallback"/>).</summary>
    public static uint ClampTimeout(uint ms, uint fallback) => ms == 0 ? fallback : ms < MinTimeoutMs ? MinTimeoutMs : ms > MaxTimeoutMs ? MaxTimeoutMs : ms;
}

/// <summary>
/// An open request, as Poll hands it out. Blittable; the caller sets StructSize to
/// sizeof(CombatRequestV1) before Poll and RynthAi fills at most that many bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CombatRequestV1
{
    /// <summary>Set by the caller before Poll: the size of the struct it passes.</summary>
    public uint StructSize;
    /// <summary>The request's id; Decide must quote it. Never 0.</summary>
    public uint Seq;
    /// <summary>RynthAiCombat.KindAttack or KindHeal.</summary>
    public uint Kind;
    /// <summary>
    /// Attack: RynthAi's locked target. Heal: RynthAi's current fight target, informational (0 when
    /// none); a heal decision's target 0 still means the player.
    /// </summary>
    public uint TargetId;
    /// <summary>That target's health, 0..1; -1 unknown or no target.</summary>
    public float TargetHealth;
    /// <summary>The player's vitals in percent; -1 unknown.</summary>
    public int HealthPct;
    public int StaminaPct;
    public int ManaPct;
    /// <summary>Heal: the health line it is under (Heal At in combat, Top Off HP idle). Attack: 0.</summary>
    public int HealLine;
    /// <summary>1 when a monster is engaged or within Monster Range.</summary>
    public int InCombat;
    /// <summary>AC's combat mode (1 NonCombat, 2 Melee, 4 Missile, 8 Magic).</summary>
    public int CombatMode;
    /// <summary>When the request was opened, and when RynthAi stops waiting for it.</summary>
    public long IssuedAtMs;
    public long DeadlineMs;
    /// <summary>The last decision of this kind RynthAi consumed: its Seq, the spell cast (0 = declined or rejected), the target and when.</summary>
    public uint LastSeq;
    public uint LastSpell;
    public uint LastTarget;
    public long LastAtMs;

    // Appended fields (present only when the StructSize RynthAi wrote back covers them; an older
    // RynthAi does not). A cast of this kind is IN FLIGHT from its send until its answer, the first
    // UseDone after the send, and RESOLVED after it. Declines and rejects are not casts.

    /// <summary>The most recent resolved cast: Seq of its request, spell, target, send time (0s before the first).</summary>
    public uint ResolvedSeq;
    public uint ResolvedSpell;
    public uint ResolvedTarget;
    public long ResolvedAtMs;
    /// <summary>
    /// Its answer, final: the UseDone error (0 = done, 0x1D = refused 'too busy', 0x401 = not enough
    /// mana, 0x402 = fizzled, ...); -1 = no answer could be read: no UseDone within
    /// RynthAiCombat.ResolveTimeoutMs of the send, or another answer came first.
    /// </summary>
    public int ResolvedErr;
    /// <summary>The cast still in flight (sent, no answer yet): Seq, spell, target, send time; Spell 0 = none.</summary>
    public uint PendingSeq;
    public uint PendingSpell;
    public uint PendingTarget;
    public long PendingAtMs;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthAiCombatApiV1
{
    public const string InterfaceName = "RynthAi.Combat";
    public const uint InterfaceVersion = 1;

    public uint Version;      // InterfaceVersion of the table RynthAi filled
    public uint StructSize;   // sizeof(RynthAiCombatApiV1) as RynthAi built it

    /// <summary>
    /// Claim(kinds, leaseMs, attackTimeoutMs, healTimeoutMs, owner): take or renew the hook for
    /// <c>kinds</c> (RynthAiCombat.Kind* bits) until now + leaseMs (clamped; 0 = default). Renew it
    /// every tick. The kinds passed replace the ones held. Returns the kinds granted: 0 while another
    /// owner's lease is live (the names compare case-sensitively).
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, uint, uint, uint, byte*, int> Claim;

    /// <summary>Release(kinds): drop those kinds now (an open request of them is cancelled: native that tick).</summary>
    public delegate* unmanaged[Cdecl]<uint, void> Release;

    /// <summary>Poll(kind, request): 1 when a request of that kind is open and unanswered (filled in), else 0.</summary>
    public delegate* unmanaged[Cdecl]<uint, CombatRequestV1*, int> Poll;

    /// <summary>
    /// Decide(seq, kind, spellId, targetId, flags): the answer to request <c>seq</c>. spellId 0 =
    /// decline (RynthAi chooses); targetId 0 = the request's target (the player for a heal);
    /// flags RynthAiCombat.Flag*. 1 = taken; 0 = stale (another seq, already answered, past its
    /// deadline, or the kind isn't held). Taken is not cast: RynthAi still validates it.
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, int> Decide;

    /// <summary>
    /// GetScanned(ids, distances, capacity): RynthAi's current target candidates (in range,
    /// attackable, line of sight), nearest first. Writes up to capacity and returns how many
    /// there are in all (ids null: just the count).
    /// </summary>
    public delegate* unmanaged[Cdecl]<uint*, float*, int, int> GetScanned;
}
