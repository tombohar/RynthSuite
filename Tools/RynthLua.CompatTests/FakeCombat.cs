using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthLua.CompatTests;

/// <summary>
/// A fake "RynthAi.Combat" table (Shared/RynthAiCombatApi.cs) for the compat tests: the same
/// lease, owner, poll and decide rules as RynthAi's CombatHook, served by FakeHost's
/// GetPluginInterface while <see cref="Enabled"/> is set. The test script drives it through
/// the harness (open a request, read the decisions, the claim and release counts).
/// </summary>
internal static unsafe class FakeCombat
{
    public sealed record Decision(uint Seq, uint Kind, uint Spell, uint Target, uint Flags, bool Taken);

    /// <summary>Served by GetPluginInterface (off: RynthAi without the hook, an older build).</summary>
    public static bool Enabled;
    public static string Owner = "";
    public static uint Kinds;
    public static long LeaseUntil;
    public static int Claims, Refusals, Releases;
    public static uint ReleasedKinds;   // every kind a Release named, or'ed
    public static readonly List<Decision> Decisions = new();
    public static readonly List<(uint Id, float Distance)> Scanned = new();

    private sealed class Req { public CombatRequestV1 R; public bool Open, Decided; }
    private static readonly Dictionary<uint, Req> Slots = new();
    private static uint _seq;
    private static RynthAiCombatApiV1* _table;

    private static long Now => Environment.TickCount64;

    /// <summary>Per kind, as RynthAi's CombatHook: the taken cast in flight, and the most recent resolved one (Err -1 = no answer read).</summary>
    private sealed class Cast { public uint Seq, Spell, Target; public long At; public int Err = -1; }
    private static readonly Dictionary<uint, Cast> InFlight = new(), Resolved = new();
    /// <summary>OlderRynthAi: a RynthAi from before the resolved/pending fields (writes none of them).</summary>
    public static bool OlderRynthAi;

    /// <summary>The server answers the cast in flight of <paramref name="kind"/> (err -1: no answer read).</summary>
    public static void ResolveInFlight(uint kind, int err)
    {
        if (!InFlight.Remove(kind, out var c)) return;
        c.Err = err;
        Resolved[kind] = c;
    }

    public static void Reset()
    {
        InFlight.Clear(); Resolved.Clear();
        OlderRynthAi = false;
        Enabled = false;
        Owner = "";
        Kinds = 0;
        LeaseUntil = 0;
        Claims = Refusals = Releases = 0;
        ReleasedKinds = 0;
        Decisions.Clear();
        Scanned.Clear();
        Slots.Clear();
    }

    /// <summary>RynthAi reloaded: its seq restarts and nothing is claimed or open (the table stays served).</summary>
    public static void Reload()
    {
        _seq = 0;
        Slots.Clear();
        Owner = "";
        Kinds = 0;
        LeaseUntil = 0;
    }

    public static IntPtr Table()
    {
        if (!Enabled) return IntPtr.Zero;
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

    /// <summary>Opens a request of <paramref name="kind"/> (RynthAi reaching a cast choice). Returns its seq.</summary>
    public static uint Open(uint kind, uint targetId, long timeoutMs = 2000)
    {
        uint seq = ++_seq;
        var r = new CombatRequestV1
        {
            StructSize = (uint)sizeof(CombatRequestV1), Seq = seq, Kind = kind, TargetId = targetId,
            TargetHealth = kind == RynthAiCombat.KindAttack ? 0.5f : -1f, HealthPct = 80, StaminaPct = 70, ManaPct = 60,
            HealLine = kind == RynthAiCombat.KindHeal ? 60 : 0, InCombat = 1, CombatMode = 8,
            IssuedAtMs = Now, DeadlineMs = Now + timeoutMs, ResolvedErr = -1,
        };
        if (Slots.TryGetValue(kind, out var old) && old.Decided)
        {
            r.LastSeq = old.R.Seq;
            Decision? d = Decisions.FindLast(x => x.Seq == old.R.Seq && x.Taken);
            r.LastSpell = d?.Spell ?? 0;
            r.LastTarget = d?.Target ?? 0;
            r.LastAtMs = Now;
            if (r.LastSpell != 0)
            {
                ResolveInFlight(kind, -1);   // the cast before it, still unanswered: no answer read
                InFlight[kind] = new Cast { Seq = old.R.Seq, Spell = r.LastSpell, Target = r.LastTarget, At = Now };
            }
        }
        if (Resolved.TryGetValue(kind, out var rc)) { r.ResolvedSeq = rc.Seq; r.ResolvedSpell = rc.Spell; r.ResolvedTarget = rc.Target; r.ResolvedAtMs = rc.At; r.ResolvedErr = rc.Err; }
        if (InFlight.TryGetValue(kind, out var pc)) { r.PendingSeq = pc.Seq; r.PendingSpell = pc.Spell; r.PendingTarget = pc.Target; r.PendingAtMs = pc.At; }
        Slots[kind] = new Req { R = r, Open = true };
        return seq;
    }

    private static bool Active(uint kind) => (Kinds & kind) != 0 && Now < LeaseUntil;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Claim(uint kinds, uint leaseMs, uint attackTimeoutMs, uint healTimeoutMs, byte* owner)
    {
        string name = owner == null ? "" : Marshal.PtrToStringUTF8((IntPtr)owner) ?? "";
        kinds &= RynthAiCombat.KindsAll;
        if (Kinds != 0 && Now < LeaseUntil && Owner != name) { Refusals++; return 0; }
        Claims++;
        Owner = kinds == 0 ? "" : name;
        Kinds = kinds;
        LeaseUntil = kinds == 0 ? 0 : Now + RynthAiCombat.ClampLease(leaseMs);
        return (int)kinds;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Release(uint kinds)
    {
        Releases++;
        ReleasedKinds |= kinds;
        Kinds &= ~kinds;
        if (Kinds == 0) { Owner = ""; LeaseUntil = 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Poll(uint kind, CombatRequestV1* req)
    {
        if (req == null || !Active(kind) || !Slots.TryGetValue(kind, out var s) || !s.Open || s.Decided || Now > s.R.DeadlineMs) return 0;
        uint room = req->StructSize == 0 || req->StructSize > (uint)sizeof(CombatRequestV1) ? (uint)sizeof(CombatRequestV1) : req->StructSize;
        if (OlderRynthAi) room = Math.Min(room, (uint)Marshal.OffsetOf<CombatRequestV1>(nameof(CombatRequestV1.ResolvedSeq)));   // writes none of the appended fields
        CombatRequestV1 r = s.R;
        r.StructSize = room;
        Buffer.MemoryCopy(&r, req, room, room);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Decide(uint seq, uint kind, uint spellId, uint targetId, uint flags)
    {
        bool taken = Active(kind) && Slots.TryGetValue(kind, out var s) && s.Open && !s.Decided && s.R.Seq == seq && Now <= s.R.DeadlineMs;
        if (taken) Slots[kind].Decided = true;
        Decisions.Add(new Decision(seq, kind, spellId, targetId, flags, taken));
        return taken ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetScanned(uint* ids, float* distances, int capacity)
    {
        if (ids != null)
            for (int i = 0; i < Scanned.Count && i < capacity; i++)
            {
                ids[i] = Scanned[i].Id;
                if (distances != null) distances[i] = Scanned[i].Distance;
            }
        return Scanned.Count;
    }
}
