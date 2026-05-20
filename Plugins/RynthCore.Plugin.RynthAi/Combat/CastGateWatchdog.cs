using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Stuck-state backstop for the engine's CMotionInterp cast-gesture gate
/// (<c>_host.CanCastNow</c>). That gate reads <c>[CMI+0x80]</c>; if it ever
/// stays non-zero indefinitely — a server-refused "You're too busy!" can leave
/// a one-shot motion node that never pops, and the +0x80 offset was never 100%
/// pinned offline — BOTH the buff (<c>BuffManager.OnHeartbeat</c>) and combat
/// (<c>CombatManager</c> magic) paths hard-return on <c>!CanCastNow</c> every
/// tick and the bot freezes standing in place. The engine has no self-recovery
/// for this and there is no BusyCount-style watchdog.
///
/// Logic: pass the gate through untouched while it behaves; if it has been
/// continuously closed longer than any legitimate cast wind-up+release, declare
/// it stuck and report "clear", then keep reporting clear until the engine gate
/// genuinely recovers (raw == true again). When overriding we are exactly in
/// the documented "engine without the gate" mode (CanCastNow defaults true) —
/// the <c>SpellCastIntervalMs</c> throttle and the "too busy" PARK path remain
/// the anti-spam guarantees, so this cannot reintroduce the 0x00460D1D
/// refusal-loop AV.
/// </summary>
internal static class CastGateWatchdog
{
    // Longer than any real AC cast gesture (~1–3s). Continuously closed past
    // this ⇒ the gate is stuck, not a legitimate in-progress gesture.
    private const double StuckMs = 4000;

    private static DateTime _closedSince = DateTime.MinValue;
    private static bool _overriding;

    /// <summary>
    /// Effective "clear to cast": the raw engine gate plus a stuck backstop.
    /// Call with <c>_host.CanCastNow</c>; optionally pass a logger for the
    /// one-shot stuck/recovered transitions.
    /// </summary>
    public static bool CanCastNow(bool rawCanCastNow, Action<string>? log = null)
    {
        if (rawCanCastNow)
        {
            if (_overriding)
            {
                log?.Invoke("[CastGate] engine gate recovered — stuck override cleared.");
                _overriding = false;
            }
            _closedSince = DateTime.MinValue;
            return true;
        }

        // Raw gate reports a gesture in progress.
        if (_overriding)
            return true; // already declared stuck; stay open until the engine gate recovers

        if (_closedSince == DateTime.MinValue)
            _closedSince = DateTime.Now;

        if ((DateTime.Now - _closedSince).TotalMilliseconds > StuckMs)
        {
            _overriding = true;
            log?.Invoke($"[CastGate] CanCastNow stuck false >{StuckMs:F0}ms — overriding gate open " +
                        "(SpellCastIntervalMs throttle + 'too busy' PARK remain the anti-spam backstop).");
            return true;
        }

        return false;
    }
}
