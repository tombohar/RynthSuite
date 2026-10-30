// ============================================================================
//  RynthSuite - Combat/BuffManager.Recharge.cs
//  Recharge (VTank's "Recharge" hotkey, /ra recharge): one vitals pass now, with
//  the current settings, whether the macro runs or not.
//
//  The pass is CheckVitals with the idle lines (Top Off HP / Top Off Stam / Top
//  Off Mana) whatever is in range, so a character between fights tops off now
//  instead of waiting for the next idle moment; the emergency lines still come
//  first. It uses what the vitals step uses (kits, Heal Self / Revitalize /
//  Stamina to Mana, listed potions; Stamina to Health at its own line) and does
//  one thing: a heal, a stamina or a mana recharge. Nothing under a line: it says
//  so and does nothing.
//
//  Requested from any thread (chat commands run on AC's thread); the pass runs on
//  the next heartbeat (pump thread) once the server has answered the last action
//  and no cast or kit is in flight, waiting up to RechargeWaitMs.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    internal const long RechargeWaitMs = 5000;

    private int _rechargeRequested;
    private long _rechargeRequestedAt;

    /// <summary>Asks for one recharge pass on the next heartbeat. Any thread.</summary>
    public void RequestRecharge()
    {
        Interlocked.Exchange(ref _rechargeRequestedAt, Environment.TickCount64);
        Interlocked.Exchange(ref _rechargeRequested, 1);
    }

    /// <summary>A requested pass is waiting to run.</summary>
    internal bool RechargePending => Volatile.Read(ref _rechargeRequested) != 0;

    /// <summary>HeartbeatCore, every tick (macro on or off): runs a requested pass when the character is free.</summary>
    private void TickRechargeRequest()
    {
        if (Volatile.Read(ref _rechargeRequested) == 0) return;
        long waited = Environment.TickCount64 - Interlocked.Read(ref _rechargeRequestedAt);

        string busy = RechargeBusyReason();
        if (busy.Length > 0)
        {
            if (waited < RechargeWaitMs) return;
            Interlocked.Exchange(ref _rechargeRequested, 0);
            _host.WriteToChat($"[RynthAi] Recharge: not done, the character stayed busy ({busy}).", 1);
            return;
        }
        Interlocked.Exchange(ref _rechargeRequested, 0);
        _host.WriteToChat("[RynthAi] Recharge: " + RechargeOnceNow(), 1);
    }

    /// <summary>What holds a recharge pass back right now ("" = nothing).</summary>
    private string RechargeBusyReason()
    {
        if (_vitals.MaxHealth <= 0) return "vitals not read yet";
        if (_pendingSpellId != 0) return "a cast is in flight";
        if (KitPending()) return "a kit is in flight";
        if (!PacerAllows(out string why)) return "server answering the last action: " + why;
        if (!CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s))) return "a gesture is animating";
        if (BusyCount > 0) return $"busy count {BusyCount}";
        return "";
    }

    /// <summary>
    /// One vitals pass with the idle (Top Off) lines. Returns what it did, for chat. Pump thread
    /// (internal for the host tests).
    /// </summary>
    internal string RechargeOnceNow()
    {
        int hp = _vitals.HealthPct, st = _vitals.StaminaPct, mn = _vitals.ManaPct;
        Func<bool>? saved = InCombatOverride;
        InCombatOverride = static () => false;   // the Top Off lines, monsters or not
        bool acted;
        try { acted = CheckVitals(); }
        finally { InCombatOverride = saved; }

        string vitals = $"health {hp}%, stamina {st}%, mana {mn}%";
        _host.Log($"[RynthAi] Recharge (one pass, Top Off lines): {vitals}; acted={acted} heal={_isHealingSelf} stam={_isRechargingStamina} mana={_isRechargingMana} why='{_vitalWhy}'");
        if (!acted)
        {
            bool under = hp < _settings.TopOffHP || st < _settings.TopOffStam || mn < _settings.TopOffMana;
            return under
                ? $"nothing could recharge ({vitals}; Top Off {_settings.TopOffHP}/{_settings.TopOffStam}/{_settings.TopOffMana}%{(_vitalWhy.Length > 0 ? "; " + _vitalWhy : "")})."
                : $"nothing to do ({vitals}, all at or over Top Off {_settings.TopOffHP}/{_settings.TopOffStam}/{_settings.TopOffMana}%).";
        }
        if (_isHealingSelf) return $"healing ({vitals}).";
        if (_isRechargingStamina) return $"restoring stamina ({vitals}).";
        if (_isRechargingMana) return $"restoring mana ({vitals}).";
        return $"vitals step busy ({vitals}).";
    }
}
