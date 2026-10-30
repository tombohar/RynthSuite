using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// RynthLua's side of RynthAi's combat hook ("RynthAi.Combat", Shared/RynthAiCombatApi.cs):
/// scripts register an attack and/or heal handler (rynth.ai SetAttackHandler / SetHealHandler),
/// and every tick, before the scripts run, this pump renews RynthAi's lease for the kinds that
/// have a handler, polls each kind, and starts the handler as a script thread for a new request.
/// When the thread ends its answer goes back with Decide. The handler never runs inside RynthAi:
/// it is an ordinary script thread (it may yield; auto-yield keeps a runaway one from freezing the
/// client), and RynthAi casts what it answers. RynthAi waits up to the request's deadline, then
/// chooses for itself.
///
/// One script per kind (the first to register owns it; the same script registering again
/// replaces its handler). A handler error declines that request; three in a row unregister the
/// handler. A stopped script releases its kinds; an unloaded or stalled RynthLua stops renewing
/// and RynthAi goes native when the lease lapses.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    /// <summary>Consecutive handler errors that unregister a handler.</summary>
    internal const int CombatHandlerMaxErrors = 3;

    /// <summary>
    /// How long past its request's deadline a handler thread may run before it is aborted and
    /// counted as an error (a handler that never returns would otherwise hold its kind forever).
    /// </summary>
    internal const long CombatHandlerStallMs = 1_000;

    private const string CombatStallError = "the handler did not answer within the deadline";
    private const string CombatLateError = "the handler answered after the deadline (RynthAi had already chosen)";

    private sealed class CombatHandler
    {
        public required ScriptContext Ctx;
        public required uint Kind;
        public DynValue Fn = DynValue.Nil;
        public int Errors;
        public bool InFlight;
        public object? Thread;              // the in-flight handler thread (LuaScriptHost.Abort)
        public uint InFlightSeq;
        public long StartedDeadlineMs;      // the in-flight request's deadline (RynthAi's clock)
        // The last request started, by seq AND issue time: RynthAi's seq restarts when it reloads.
        public uint LastStartedSeq;
        public long LastStartedIssuedAtMs;
    }

    /// <summary>The pump's clock; must match RynthAi's (CombatHook.Clock, Environment.TickCount64).</summary>
    internal static Func<long> CombatClock = () => Environment.TickCount64;

    private readonly Dictionary<uint, CombatHandler> _combatHandlers = new();
    // Scripts whose KeepAlive / OnStop hooks are registered: once per run (ScriptContext.Clear
    // drops both lists when the script stops, and the OnStop hook takes the script out of here).
    private readonly HashSet<ScriptContext> _combatHooked = new();
    private uint _combatClaimedKinds;
    private string _combatClaimOwner = string.Empty;
    private bool _combatRefusedLogged;

    internal static string CombatKindName(uint kind) => kind == RynthAiCombat.KindAttack ? "attack" : "heal";

    /// <summary>The script holding <paramref name="kind"/>, or null.</summary>
    internal string? CombatOwner(uint kind) => _combatHandlers.TryGetValue(kind, out var h) ? h.Ctx.Name : null;

    /// <summary>True when RynthAi granted the kinds last claimed (this tick's claim).</summary>
    internal bool CombatClaimed => _combatClaimedKinds != 0;

    /// <summary>
    /// Sets (fn a function) or clears (fn nil) <paramref name="ctx"/>'s handler for <paramref name="kind"/>.
    /// Returns null on success, or why it was refused.
    /// </summary>
    internal string? SetCombatHandler(ScriptContext ctx, uint kind, DynValue fn)
    {
        if (fn.IsNil())
        {
            if (_combatHandlers.TryGetValue(kind, out var mine) && mine.Ctx == ctx) DropCombatHandler(kind, release: true);
            return null;
        }
        if (fn.Type != DataType.Function) return "the handler must be a function (or nil to clear it)";
        if (!_ai.CombatAvailable) return "RynthAi has no combat hook";
        if (_combatHandlers.TryGetValue(kind, out var held) && held.Ctx != ctx)
            return $"the {CombatKindName(kind)} handler is already set by {held.Ctx.Name}";

        // The replaced handler's request: already answered, skip it; still running (aborted here,
        // its answer no longer counts), start the new handler on it.
        bool restart = held != null && held.InFlight;
        _combatHandlers[kind] = new CombatHandler
        {
            Ctx = ctx, Kind = kind, Fn = fn,
            LastStartedSeq = restart ? 0 : held?.LastStartedSeq ?? 0,
            LastStartedIssuedAtMs = restart ? 0 : held?.LastStartedIssuedAtMs ?? 0,
        };
        if (restart) held!.Ctx.Host.Abort(held.Thread);
        if (_combatHooked.Add(ctx))
        {
            // A script with only handlers stays running; stopping it releases what it holds.
            ctx.KeepAlive.Add(() => _combatHandlers.Values.Count(h => h.Ctx == ctx));
            ctx.OnStop.Add(() =>
            {
                ClearCombatHandlers(ctx);
                _combatHooked.Remove(ctx);
            });
        }
        return null;
    }

    /// <summary>Drops every handler <paramref name="ctx"/> holds and releases those kinds in RynthAi.</summary>
    internal void ClearCombatHandlers(ScriptContext ctx)
    {
        foreach (uint kind in _combatHandlers.Where(kv => kv.Value.Ctx == ctx).Select(kv => kv.Key).ToArray())
            DropCombatHandler(kind, release: true);
    }

    /// <summary>Test harness only: how many KeepAlive / OnStop hooks a script has registered.</summary>
    internal (int KeepAlive, int OnStop) ScriptHookCounts(string name)
        => _scripts.TryGetValue(name, out var ctx) ? (ctx.KeepAlive.Count, ctx.OnStop.Count) : (0, 0);

    private void DropCombatHandler(uint kind, bool release)
    {
        if (_combatHandlers.TryGetValue(kind, out var h) && h.InFlight) h.Ctx.Host.Abort(h.Thread);
        _combatHandlers.Remove(kind);
        if (release)
        {
            try { _ai.CombatRelease(kind); } catch { }
            _combatClaimedKinds &= ~kind;
        }
    }

    /// <summary>Releases every kind (plugin shutdown); RynthAi goes native at once.</summary>
    private void ReleaseCombatHook()
    {
        _combatHandlers.Clear();
        _combatHooked.Clear();
        if (_combatClaimedKinds != 0)
        {
            try { _ai.CombatRelease(RynthAiCombat.KindsAll); } catch { }
            _combatClaimedKinds = 0;
        }
    }

    /// <summary>One pump pass (OnTick, before the scripts run).</summary>
    private void PumpCombatHook()
    {
        if (_combatHandlers.Count == 0) return;   // nothing registered: no per-tick work or allocation
        // A handler whose script is gone (stopped without its stop handlers, or never ran).
        foreach (var kv in _combatHandlers.Where(kv => !kv.Value.Ctx.Host.Loaded).ToArray())
            DropCombatHandler(kv.Key, release: true);
        if (_combatHandlers.Count == 0) return;
        if (!_ai.CombatAvailable) { _combatClaimedKinds = 0; return; }   // RynthAi not loaded (yet): nothing to renew

        // A handler past its request's deadline plus the stall budget never answered (a loop that
        // sleeps forever, an await that never finishes): abort it so the kind isn't held forever.
        long now = CombatClock();
        foreach (CombatHandler h in _combatHandlers.Values.ToArray())
            if (h.InFlight && now > h.StartedDeadlineMs + CombatHandlerStallMs)
            {
                h.Ctx.Host.Abort(h.Thread);
                OnCombatHandlerDone(h, h.InFlightSeq, null, CombatStallError);
            }
        if (_combatHandlers.Count == 0) return;

        uint kinds = 0;
        foreach (uint k in _combatHandlers.Keys) kinds |= k;
        string owner = CombatOwnerName();
        if (owner != _combatClaimOwner && _combatClaimedKinds != 0)
            _ai.CombatRelease(RynthAiCombat.KindsAll);   // same plugin, new name: RynthAi refuses a rename while the lease is live
        _combatClaimOwner = owner;
        uint before = _combatClaimedKinds;
        _combatClaimedKinds = _ai.CombatClaim(kinds, RynthAiCombat.DefaultLeaseMs, 0, 0, owner);
        // A kind granted again after a gap (a lapse, a refusal, RynthAi reloaded): its seq may have
        // restarted, so the last started seq no longer identifies a request already handled.
        foreach (CombatHandler h in _combatHandlers.Values)
            if ((before & h.Kind) == 0 && (_combatClaimedKinds & h.Kind) != 0)
                h.LastStartedSeq = 0;
        if (_combatClaimedKinds == 0)
        {
            if (!_combatRefusedLogged)
            {
                _combatRefusedLogged = true;
                Log($"[RynthLua] combat hook: RynthAi refused the claim for {RynthAiCombat.KindNames(kinds)} (another owner holds it); retrying every tick.");
            }
            return;
        }
        _combatRefusedLogged = false;

        foreach (CombatHandler h in _combatHandlers.Values.ToArray())
        {
            if ((_combatClaimedKinds & h.Kind) == 0 || h.InFlight) continue;
            if (!_ai.CombatPoll(h.Kind, out CombatRequestV1 req)) continue;
            if (req.Seq == h.LastStartedSeq && req.IssuedAtMs == h.LastStartedIssuedAtMs) continue;
            Script? s = h.Ctx.Host.Lua;
            if (s == null) continue;
            DynValue ctxTable;
            try { ctxTable = BuildCombatContext(s, req); }
            catch (Exception ex) { h.Ctx.Host.Print($"error: combat {CombatKindName(h.Kind)} context: {ex.Message}"); continue; }
            uint seq = req.Seq;
            CombatHandler started = h;
            h.LastStartedSeq = seq;
            h.LastStartedIssuedAtMs = req.IssuedAtMs;
            h.Thread = h.Ctx.Host.Start(h.Fn, new[] { ctxTable }, (result, error) => OnCombatHandlerDone(started, seq, result, error));
            h.InFlight = h.Thread != null;
            h.InFlightSeq = seq;
            h.StartedDeadlineMs = req.DeadlineMs;
        }
    }

    private string CombatOwnerName()
    {
        var names = _combatHandlers.Values.Select(h => h.Ctx.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        string owner = "RynthLua/" + string.Join("+", names);
        return owner.Length > RynthAiCombat.MaxOwnerLength ? owner.Substring(0, RynthAiCombat.MaxOwnerLength) : owner;
    }

    /// <summary>The handler's thread ended: decide with its answer, or decline on an error.</summary>
    private void OnCombatHandlerDone(CombatHandler h, uint seq, DynValue? result, string? error)
    {
        h.InFlight = false;
        h.Thread = null;
        // A Lua error the script host has already printed ("error: ..."): not printed twice.
        bool printed = error != null && error != CombatStallError;
        // An answer after the deadline: RynthAi has already chosen for itself, so the answer is
        // wasted. It counts as a stall, the same as no answer within the stall budget.
        if (error == null && CombatClock() > h.StartedDeadlineMs)
            error = CombatLateError;
        string? bad = null;
        if (error == null && TryReadCombatAnswer(result, out uint spell, out uint target, out uint flags, out bad))
        {
            h.Errors = 0;
            _ai.CombatDecide(seq, h.Kind, spell, target, flags);   // stale (0) when RynthAi moved on: harmless
            return;
        }
        error ??= bad;

        // An error (raised, or an answer that isn't spellId, targetId [, "area"]): decline at once
        // rather than make RynthAi wait out the deadline.
        _ai.CombatDecide(seq, h.Kind, 0, 0, 0);
        bool current = _combatHandlers.TryGetValue(h.Kind, out var now) && ReferenceEquals(now, h);
        if (!current) return;   // replaced or removed meanwhile: its errors don't count
        string kindName = CombatKindName(h.Kind);
        h.Errors++;
        h.Ctx.Host.Print(printed
            ? $"combat {kindName} handler failed (declined; {h.Errors}/{CombatHandlerMaxErrors} in a row)"
            : $"combat {kindName} handler: {error} (declined; {h.Errors}/{CombatHandlerMaxErrors} in a row)");
        if (h.Errors >= CombatHandlerMaxErrors)
        {
            DropCombatHandler(h.Kind, release: true);
            string what = error == CombatStallError || error == CombatLateError ? "did not answer within the deadline" : "failed";
            string msg = $"{h.Ctx.Name}: the {kindName} handler {what} {CombatHandlerMaxErrors} times in a row and was removed; RynthAi chooses the {kindName} spells again.";
            h.Ctx.Host.Print(msg);
            ChatLine(msg);
            Log("[RynthLua] combat hook: " + msg);
        }
    }
    /// <summary>
    /// Reads a handler's return: nil / false / nothing = decline; otherwise spellId (a number),
    /// targetId (a number or an object; nil = the request's target) and optional flags: "area"
    /// (or true), "nojudge", or a table { area = true, nojudge = true }.
    /// </summary>
    internal static bool TryReadCombatAnswer(DynValue? result, out uint spell, out uint target, out uint flags, out string? error)
    {
        spell = target = flags = 0;
        error = null;
        DynValue[] values = result == null ? Array.Empty<DynValue>()
            : result.Type == DataType.Tuple ? result.Tuple ?? Array.Empty<DynValue>()
            : new[] { result };
        DynValue first = values.Length > 0 ? values[0] : DynValue.Nil;
        if (first.IsNil() || first.Type == DataType.Void || (first.Type == DataType.Boolean && !first.Boolean)) return true;   // decline
        if (first.Type != DataType.Number || first.Number < 0 || first.Number > uint.MaxValue || first.Number != Math.Floor(first.Number))
        {
            error = $"returned {first.Type.ToString().ToLowerInvariant()} '{first.ToPrintString()}', expected a spell id (or nil to decline)";
            return false;
        }
        spell = (uint)first.Number;
        DynValue second = values.Length > 1 ? values[1] : DynValue.Nil;
        if (!second.IsNil() && second.Type != DataType.Void)
        {
            target = LuaArgs.Id(second);
            if (target == 0 && !(second.Type == DataType.Number && second.Number == 0))
            {
                error = $"returned target '{second.ToPrintString()}', expected an object id or object (or nil for the request's target)";
                return false;
            }
        }
        DynValue third = values.Length > 2 ? values[2] : DynValue.Nil;
        switch (third.Type)
        {
            case DataType.Nil:
            case DataType.Void:
                break;
            case DataType.Boolean:   // true = "area" (the v1 form); false = no flags
                if (third.Boolean) flags |= RynthAiCombat.FlagArea;
                break;
            case DataType.String:
                if (!TryCombatFlag(third.String, out uint f))
                {
                    error = $"returned flag '{third.String}', expected \"area\", \"nojudge\" or a table {{ area = true, nojudge = true }}";
                    return false;
                }
                flags |= f;
                break;
            case DataType.Table:
                foreach (TablePair p in third.Table.Pairs)
                {
                    if (p.Key.Type != DataType.String || !TryCombatFlag(p.Key.String, out uint tf))
                    {
                        error = $"returned flag '{p.Key.ToPrintString()}', expected the keys area and nojudge";
                        return false;
                    }
                    if (p.Value.CastToBool()) flags |= tf;
                }
                break;
            default:
                error = $"returned {third.Type.ToString().ToLowerInvariant()} flags, expected \"area\", \"nojudge\" or a table {{ area = true, nojudge = true }}";
                return false;
        }
        return true;
    }

    private static bool TryCombatFlag(string name, out uint flag)
    {
        flag = name.Trim().ToLowerInvariant() switch
        {
            "area" => RynthAiCombat.FlagArea,
            "nojudge" => RynthAiCombat.FlagNoJudge,
            _ => 0u,
        };
        return flag != 0;
    }

    /// <summary>The ctx table a handler gets.</summary>
    private DynValue BuildCombatContext(Script s, in CombatRequestV1 r)
    {
        var t = new Table(s);
        t["seq"] = (double)r.Seq;
        t["kind"] = CombatKindName(r.Kind);
        t["targetId"] = (double)r.TargetId;
        t["targetHealth"] = (double)r.TargetHealth;
        t["healthPct"] = (double)r.HealthPct;
        t["staminaPct"] = (double)r.StaminaPct;
        t["manaPct"] = (double)r.ManaPct;
        t["healLine"] = (double)r.HealLine;
        t["inCombat"] = r.InCombat != 0;
        t["combatMode"] = (double)r.CombatMode;
        t["issuedAt"] = (double)r.IssuedAtMs;
        t["deadline"] = (double)r.DeadlineMs;

        var monsters = new Table(s);
        foreach (var (id, distance) in _ai.CombatScanned())
        {
            var m = new Table(s);
            m["id"] = (double)id;
            m["distance"] = Math.Round(distance, 2);
            monsters.Append(DynValue.NewTable(m));
        }
        t["monsters"] = monsters;

        // ctx.last: the most recent RESOLVED cast, its err final (nil only when no answer could be
        // read); ctx.pending: the cast still in flight, or nil. From a RynthAi that writes the
        // appended fields; an older one: ctx.last is its last decision, no err, no pending.
        var last = new Table(s);
        uint newEnd = (uint)System.Runtime.InteropServices.Marshal.OffsetOf<CombatRequestV1>(nameof(CombatRequestV1.PendingAtMs)) + sizeof(long);
        if (r.StructSize >= newEnd)
        {
            last["seq"] = (double)r.ResolvedSeq;
            last["spell"] = (double)r.ResolvedSpell;
            last["target"] = (double)r.ResolvedTarget;
            last["at"] = (double)r.ResolvedAtMs;
            if (r.ResolvedSpell != 0 && r.ResolvedErr >= 0) last["err"] = (double)r.ResolvedErr;
            if (r.PendingSpell != 0)
            {
                var pending = new Table(s);
                pending["seq"] = (double)r.PendingSeq;
                pending["spell"] = (double)r.PendingSpell;
                pending["target"] = (double)r.PendingTarget;
                pending["at"] = (double)r.PendingAtMs;
                t["pending"] = pending;
            }
        }
        else
        {
            last["seq"] = (double)r.LastSeq;
            last["spell"] = (double)r.LastSpell;
            last["target"] = (double)r.LastTarget;
            last["at"] = (double)r.LastAtMs;
        }
        t["last"] = last;
        return DynValue.NewTable(t);
    }
}
