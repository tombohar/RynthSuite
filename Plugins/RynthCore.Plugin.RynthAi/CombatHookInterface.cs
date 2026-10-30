using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The script-provided combat hook (Shared/RynthAiCombatApi.cs): who holds it, for which kinds,
/// until when, and one request slot per kind. Static, like the interface table that serves it,
/// so it survives a re-Init of the plugin instance; everything runs on the plugin pump thread.
///
/// RynthAi asks (CombatManager.AttackWithMagic for an attack, BuffManager.CheckVitals for a heal)
/// with <see cref="Step"/>: the first call opens a request and says Wait; later calls say Wait
/// until the answer is in (Decided), the answer is a decline (Native), or the deadline passes
/// (Native). The hook is active only while its lease is live, so a script plugin that stops
/// renewing (unloaded, crashed, stopped) leaves RynthAi native within one lease.
/// </summary>
internal static class CombatHook
{
    /// <summary>What a consumer does this tick.</summary>
    internal enum Outcome { Native, Wait, Decided }

    /// <summary>One kind's request slot.</summary>
    internal sealed class Slot
    {
        public readonly uint Kind;
        public Slot(uint kind) => Kind = kind;

        public bool Open;
        public CombatRequestV1 Request;
        public bool Decided;
        public uint DecidedSpell, DecidedTarget, DecidedFlags;

        // The last decision consumed (handed back in the next request).
        public uint LastSeq, LastSpell, LastTarget;
        public long LastAtMs;
        // The cast in flight (PendingSpell 0 = none), with the UseDone count at its send, and the
        // most recent resolved cast with its final answer (ResolvedErr -1 = none could be read).
        public uint PendingSeq, PendingSpell, PendingTarget;
        public long PendingAtMs;
        public int PendingSeqAtSend = -1;
        // The UseDone count that answers it: one more than its send count, or than the answer of the
        // cast still in flight before it (both kinds, in send order); -1 = unknown (time limit only).
        public int PendingExpectSeq = -1;
        public uint ResolvedSeq, ResolvedSpell, ResolvedTarget;
        public long ResolvedAtMs;
        public int ResolvedErr = -1;

        /// <summary>The cast in flight is done: <paramref name="err"/> its answer (-1 = none read).</summary>
        public void Resolve(int err)
        {
            if (PendingSpell == 0) return;
            ResolvedSeq = PendingSeq; ResolvedSpell = PendingSpell; ResolvedTarget = PendingTarget;
            ResolvedAtMs = PendingAtMs; ResolvedErr = err;
            PendingSeq = PendingSpell = PendingTarget = 0; PendingAtMs = 0; PendingSeqAtSend = -1; PendingExpectSeq = -1;
            InFlight.Remove(this);
        }

        // Counters for /ra combat and the tests.
        public int Opened, Casts, Declines, Timeouts, Rejects;
        public long LastTimeoutLogMs = long.MinValue;
        public long LastRejectLogMs = long.MinValue;

        public void Reset()
        {
            Open = false; Decided = false; Request = default;
            DecidedSpell = DecidedTarget = DecidedFlags = 0;
            LastSeq = LastSpell = LastTarget = 0; LastAtMs = 0;
            PendingSeq = PendingSpell = PendingTarget = 0; PendingAtMs = 0; PendingSeqAtSend = -1; PendingExpectSeq = -1;
            InFlight.Remove(this);
            ResolvedSeq = ResolvedSpell = ResolvedTarget = 0; ResolvedAtMs = 0; ResolvedErr = -1;
            Opened = Casts = Declines = Timeouts = Rejects = 0;
            LastTimeoutLogMs = LastRejectLogMs = long.MinValue;
        }
    }

    /// <summary>Milliseconds clock (Environment.TickCount64; the host tests replace it).</summary>
    internal static Func<long> Clock = static () => Environment.TickCount64;
    internal static long Now => Clock();

    /// <summary>
    /// The last UseDone (count, error), or null when the engine can't say: RynthAi sets it at Init. A
    /// cast's answer is the first UseDone after its send (ctx.last.err).
    /// </summary>
    internal static Func<(int Seq, uint Err)?>? LastUseDone;

    // The casts in flight of both kinds, in send order: the UseDones answer them in that order.
    private static readonly List<Slot> InFlight = new(2);

    /// <summary>
    /// Resolves the casts in flight (each tick, at Poll, and before the next cast goes out), in send
    /// order: each one's answer is the UseDone after its send, or after the answer of the cast sent
    /// before it (an attack and a heal both in flight). A later count with that one unseen, or no
    /// UseDone within RynthAiCombat.ResolveTimeoutMs, resolves it with no answer (-1).
    /// </summary>
    public static void ObserveUseDone()
    {
        if (InFlight.Count == 0) return;
        (int Seq, uint Err)? last;
        try { last = LastUseDone?.Invoke(); } catch { last = null; }
        long now = Now;
        for (int i = 0; i < InFlight.Count;)
        {
            Slot s = InFlight[i];
            if (last is { } u && s.PendingExpectSeq >= 0 && u.Seq >= s.PendingExpectSeq)
            {
                s.Resolve(u.Seq == s.PendingExpectSeq ? unchecked((int)u.Err) : -1);   // removes it
                continue;
            }
            if (now - s.PendingAtMs >= RynthAiCombat.ResolveTimeoutMs) { s.Resolve(-1); continue; }
            i++;
        }
    }
    /// <summary>A timeout is logged at most this often per kind.</summary>
    internal const long TimeoutLogIntervalMs = 60_000;
    private const long RejectLogIntervalMs = 5_000;
    /// <summary>A request this long past its deadline when RynthAi next asks is dropped and asked again.</summary>
    internal const long StaleGraceMs = 1_000;

    public static string Owner { get; private set; } = "";
    public static uint Kinds { get; private set; }
    public static long LeaseUntilMs { get; private set; }
    public static uint AttackTimeoutMs { get; private set; } = RynthAiCombat.DefaultAttackTimeoutMs;
    public static uint HealTimeoutMs { get; private set; } = RynthAiCombat.DefaultHealTimeoutMs;

    public static readonly Slot Attack = new(RynthAiCombat.KindAttack);
    public static readonly Slot Heal = new(RynthAiCombat.KindHeal);

    private static uint _seq;

    /// <summary>Forget everything (plugin shutdown, host tests).</summary>
    public static void Reset()
    {
        Owner = "";
        Kinds = 0;
        LeaseUntilMs = 0;
        AttackTimeoutMs = RynthAiCombat.DefaultAttackTimeoutMs;
        HealTimeoutMs = RynthAiCombat.DefaultHealTimeoutMs;
        Attack.Reset();
        Heal.Reset();
        _seq = 0;
    }

    public static Slot? SlotFor(uint kind) => kind == RynthAiCombat.KindAttack ? Attack : kind == RynthAiCombat.KindHeal ? Heal : null;

    /// <summary>True while <paramref name="kind"/> is held and the lease is live.</summary>
    public static bool IsActive(uint kind) => (Kinds & kind) != 0 && Now < LeaseUntilMs;

    private static bool LeaseLive => Kinds != 0 && Now < LeaseUntilMs;

    /// <summary>Takes or renews the hook. Returns the kinds granted (0 = another owner's lease is live).</summary>
    public static uint Claim(uint kinds, uint leaseMs, uint attackTimeoutMs, uint healTimeoutMs, string owner)
    {
        owner = (owner ?? "").Trim();
        if (owner.Length > RynthAiCombat.MaxOwnerLength) owner = owner.Substring(0, RynthAiCombat.MaxOwnerLength);
        kinds &= RynthAiCombat.KindsAll;
        long now = Now;
        bool live = LeaseLive;
        if (live && !string.Equals(Owner, owner, StringComparison.Ordinal)) return 0;

        // Kinds this owner no longer claims: their open requests are dropped (native that tick).
        uint dropped = live ? Kinds & ~kinds : RynthAiCombat.KindsAll;
        if ((dropped & RynthAiCombat.KindAttack) != 0) Cancel(Attack);
        if ((dropped & RynthAiCombat.KindHeal) != 0) Cancel(Heal);

        if (kinds == 0)
        {
            Owner = "";
            Kinds = 0;
            LeaseUntilMs = 0;
            return 0;
        }
        Owner = owner;
        Kinds = kinds;
        LeaseUntilMs = now + RynthAiCombat.ClampLease(leaseMs);
        AttackTimeoutMs = RynthAiCombat.ClampTimeout(attackTimeoutMs, RynthAiCombat.DefaultAttackTimeoutMs);
        HealTimeoutMs = RynthAiCombat.ClampTimeout(healTimeoutMs, RynthAiCombat.DefaultHealTimeoutMs);
        return kinds;
    }

    /// <summary>Drops <paramref name="kinds"/> now; their open requests are cancelled.</summary>
    public static void Release(uint kinds)
    {
        kinds &= RynthAiCombat.KindsAll;
        if ((kinds & RynthAiCombat.KindAttack) != 0) Cancel(Attack);
        if ((kinds & RynthAiCombat.KindHeal) != 0) Cancel(Heal);
        Kinds &= ~kinds;
        if (Kinds == 0) { Owner = ""; LeaseUntilMs = 0; }
    }

    /// <summary>1 when a request of <paramref name="kind"/> is open and unanswered (copied to <paramref name="request"/>).</summary>
    public static bool Poll(uint kind, out CombatRequestV1 request)
    {
        request = default;
        Slot? s = SlotFor(kind);
        if (s == null || !IsActive(kind) || !s.Open || s.Decided || Now > s.Request.DeadlineMs) return false;
        request = s.Request;
        request.LastSeq = s.LastSeq;
        request.LastSpell = s.LastSpell;
        request.LastTarget = s.LastTarget;
        request.LastAtMs = s.LastAtMs;
        ObserveUseDone();
        request.ResolvedSeq = s.ResolvedSeq;
        request.ResolvedSpell = s.ResolvedSpell;
        request.ResolvedTarget = s.ResolvedTarget;
        request.ResolvedAtMs = s.ResolvedAtMs;
        request.ResolvedErr = s.ResolvedSpell != 0 ? s.ResolvedErr : -1;
        request.PendingSeq = s.PendingSeq;
        request.PendingSpell = s.PendingSpell;
        request.PendingTarget = s.PendingTarget;
        request.PendingAtMs = s.PendingAtMs;
        return true;
    }

    /// <summary>The answer to request <paramref name="seq"/>. False = stale.</summary>
    public static bool Decide(uint seq, uint kind, uint spellId, uint targetId, uint flags)
    {
        Slot? s = SlotFor(kind);
        if (s == null || !IsActive(kind) || !s.Open || s.Decided || s.Request.Seq != seq || Now > s.Request.DeadlineMs) return false;
        s.Decided = true;
        s.DecidedSpell = spellId;
        s.DecidedTarget = targetId;
        s.DecidedFlags = flags;
        return true;
    }

    private static void Cancel(Slot s)
    {
        s.Open = false;
        s.Decided = false;
    }

    /// <summary>
    /// One consumer step for <paramref name="slot"/>. <paramref name="forTarget"/> is what the request
    /// is about (the attack target; for a heal the fight target, informational, 0 when none): an open
    /// attack request about another target is dropped and a fresh one opened; a heal request is not. <paramref name="build"/> fills a new request's context (RynthAi's
    /// view of the fight); Seq, Kind, TargetId and the times are set here.
    /// </summary>
    public static Outcome Step(Slot slot, uint forTarget, Func<CombatRequestV1> build, Action<string>? log,
        out uint spellId, out uint targetId, out uint flags)
    {
        spellId = targetId = flags = 0;
        if (!IsActive(slot.Kind))
        {
            Cancel(slot);
            return Outcome.Native;
        }
        long now = Now;
        // An attack about another target: the target moved on, ask afresh. A heal's target is only
        // informational (the fight target), so a heal request keeps its deadline when the fight
        // target changes; restarting it would let a target swap every few hundred ms hold the
        // heal past its 750 ms deadline for ever. Its answer is still checked against the targets
        // as they are when it comes (BuffManager.ScriptHealProblem).
        if (slot.Open && slot.Kind == RynthAiCombat.KindAttack && slot.Request.TargetId != forTarget) Cancel(slot);
        // RynthAi stopped asking for a while (its gates held the cast, or health came back over the
        // line): an answer, or a missing one, from back then is stale. Ask afresh, no timeout counted.
        if (slot.Open && now > slot.Request.DeadlineMs + StaleGraceMs) Cancel(slot);

        if (!slot.Open)
        {
            CombatRequestV1 r = build();
            r.StructSize = (uint)Unsafe.SizeOf<CombatRequestV1>();
            if (++_seq == 0) _seq = 1;
            r.Seq = _seq;
            r.Kind = slot.Kind;
            r.TargetId = forTarget;
            r.IssuedAtMs = now;
            r.DeadlineMs = now + (slot.Kind == RynthAiCombat.KindAttack ? AttackTimeoutMs : HealTimeoutMs);
            slot.Request = r;
            slot.Open = true;
            slot.Decided = false;
            slot.Opened++;
            return Outcome.Wait;
        }

        if (slot.Decided)
        {
            if (slot.DecidedSpell == 0)
            {
                Consume(slot, 0, 0);
                slot.Declines++;
                return Outcome.Native;
            }
            spellId = slot.DecidedSpell;
            targetId = slot.DecidedTarget;
            flags = slot.DecidedFlags;
            return Outcome.Decided;   // the consumer validates, then Cast or Reject
        }

        if (now > slot.Request.DeadlineMs)
        {
            Cancel(slot);
            slot.Timeouts++;
            if (slot.LastTimeoutLogMs == long.MinValue || now - slot.LastTimeoutLogMs >= TimeoutLogIntervalMs)
            {
                slot.LastTimeoutLogMs = now;
                log?.Invoke($"[CombatHook] {RynthAiCombat.KindNames(slot.Kind)} request {slot.Request.Seq} unanswered after " +
                            $"{slot.Request.DeadlineMs - slot.Request.IssuedAtMs} ms ('{Owner}'): RynthAi chose instead " +
                            $"({slot.Timeouts} timeout(s) so far; logged at most once a minute)");
            }
            return Outcome.Native;
        }
        return Outcome.Wait;
    }

    /// <summary>
    /// The decided spell went out (or was waited on): the request is done. <paramref name="seqAtSend"/>
    /// is the UseDone count read just before the send (ActionPacer.LastCastSeqAtSend); -1 reads the
    /// last UseDone now instead.
    /// </summary>
    public static void Cast(Slot slot, uint spellId, uint targetId, int seqAtSend = -1)
    {
        Consume(slot, spellId, targetId, seqAtSend);
        if (spellId != 0) slot.Casts++;   // 0: handled without a cast (a wand swap step in flight)
    }

    /// <summary>The decided spell failed RynthAi's checks: the request is done, RynthAi chooses.</summary>
    public static void Reject(Slot slot, string why, Action<string>? log)
    {
        uint seq = slot.Request.Seq, spell = slot.DecidedSpell, target = slot.DecidedTarget;
        Consume(slot, 0, 0);
        slot.Rejects++;
        long now = Now;
        if (slot.LastRejectLogMs == long.MinValue || now - slot.LastRejectLogMs >= RejectLogIntervalMs)
        {
            slot.LastRejectLogMs = now;
            log?.Invoke($"[CombatHook] {RynthAiCombat.KindNames(slot.Kind)} request {seq}: spell {spell} at 0x{target:X8} rejected ({why}); RynthAi chose instead");
        }
    }

    private static void Consume(Slot slot, uint spellId, uint targetId, int seqAtSend = -1)
    {
        slot.LastSeq = slot.Request.Seq;
        slot.LastSpell = spellId;
        slot.LastTarget = targetId;
        slot.LastAtMs = Now;
        if (spellId != 0)
        {
            // The cast before it resolves first (its answer if one came, else none).
            ObserveUseDone();
            slot.Resolve(-1);
            slot.PendingSeq = slot.Request.Seq;
            slot.PendingSpell = spellId;
            slot.PendingTarget = targetId;
            slot.PendingAtMs = slot.LastAtMs;
            // The count read before the send: a UseDone that lands between the send and this call is
            // then still this cast's answer, not taken as the baseline.
            if (seqAtSend >= 0) slot.PendingSeqAtSend = seqAtSend;
            else try { slot.PendingSeqAtSend = LastUseDone?.Invoke()?.Seq ?? -1; } catch { slot.PendingSeqAtSend = -1; }
            Slot? before = InFlight.Count > 0 ? InFlight[InFlight.Count - 1] : null;
            if (slot.PendingSeqAtSend < 0) slot.PendingExpectSeq = -1;
            else if (before != null && before.PendingExpectSeq < 0) slot.PendingExpectSeq = -1;   // can't tell which answer is whose
            else slot.PendingExpectSeq = Math.Max(slot.PendingSeqAtSend, before?.PendingExpectSeq ?? int.MinValue) + 1;
            InFlight.Add(slot);
        }
        slot.Open = false;
        slot.Decided = false;
    }

    /// <summary>One line for /ra combat.</summary>
    public static string Describe()
    {
        long now = Now;
        bool live = LeaseLive;
        string head = live
            ? $"hook: '{Owner}' holds {RynthAiCombat.KindNames(Kinds)} (lease {Math.Max(0, LeaseUntilMs - now) / 1000.0:0.0}s)"
            : "hook: none (RynthAi chooses every spell)";
        static string S(Slot s) => $"{s.Opened} asked, {s.Casts} cast, {s.Declines} declined, {s.Timeouts} timed out, {s.Rejects} rejected" +
                                   (s.Open ? $", request {s.Request.Seq} open" : "");
        return $"{head}  attack: {S(Attack)}  heal: {S(Heal)}";
    }
}

/// <summary>
/// RynthAi's side of the "RynthAi.Combat" interface: the table RynthPluginQueryInterface hands
/// out, and the functions behind it (pump thread, current plugin instance).
/// </summary>
internal static unsafe class CombatHookInterface
{
    private static RynthAiCombatApiV1* _table;

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiCombatApiV1.InterfaceName || version == 0 || version > RynthAiCombatApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiCombatApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiCombatApiV1));
            t->Version = RynthAiCombatApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthAiCombatApiV1);
            t->Claim = &Claim;
            t->Release = &Release;
            t->Poll = &Poll;
            t->Decide = &Decide;
            t->GetScanned = &GetScanned;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Frees the table and drops the hook (plugin shutdown). Callers resolve it every tick.</summary>
    public static void Shutdown()
    {
        var t = _table;
        _table = null;
        if (t != null) NativeMemory.Free(t);
        CombatHook.Reset();
        CombatHook.LastUseDone = null;   // the lambda captured the host: don't keep a shut-down instance's host alive
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Claim(uint kinds, uint leaseMs, uint attackTimeoutMs, uint healTimeoutMs, byte* owner)
    {
        try
        {
            string name = owner == null ? "" : Marshal.PtrToStringUTF8((IntPtr)owner) ?? "";
            return (int)CombatHook.Claim(kinds, leaseMs, attackTimeoutMs, healTimeoutMs, name);
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Release(uint kinds)
    {
        try { CombatHook.Release(kinds); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Poll(uint kind, CombatRequestV1* req)
    {
        try
        {
            if (req == null || !CombatHook.Poll(kind, out CombatRequestV1 r)) return 0;
            uint room = req->StructSize;
            uint size = (uint)sizeof(CombatRequestV1);
            if (room == 0 || room > size) room = size;   // an older caller passes less; never write past it
            r.StructSize = room;
            Buffer.MemoryCopy(&r, req, room, room);
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Decide(uint seq, uint kind, uint spellId, uint targetId, uint flags)
    {
        try { return CombatHook.Decide(seq, kind, spellId, targetId, flags) ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetScanned(uint* ids, float* distances, int capacity)
    {
        try { return PluginExports.Runtime.Plugin?.CombatHookScanned(ids, distances, capacity) ?? 0; }
        catch { return 0; }
    }
}

public sealed partial class RynthAiPlugin
{
    /// <summary>RynthAi.Combat GetScanned: the combat scan list, nearest first as scanned.</summary>
    internal unsafe int CombatHookScanned(uint* ids, float* distances, int capacity)
    {
        var list = _combatManager?.ScannedTargets;
        if (list == null) return 0;
        int n = list.Count;
        if (ids != null)
            for (int i = 0; i < n && i < capacity; i++)
            {
                ids[i] = unchecked((uint)list[i].Id);
                if (distances != null) distances[i] = (float)list[i].Distance;
            }
        return n;
    }
}
