// ============================================================================
//  RynthSuite - Combat/BuffManager.OneTouch.cs
//  One-touch heal / stamina / mana (/ra heal, /ra stam, /ra mana; RynthCore's
//  "One-touch heal" hotkeys): one press uses the best thing the character has
//  for that vital right now, whatever the Heal At / Top Off lines say and with
//  the macro on or off.
//
//  Nothing new decides what to use: it is the vitals step's own choice.
//    Health   a healing kit, Heal Self and a health potion (AttemptHealChain), with
//             the kit and potion before the spell when a weapon is out (no wand swap
//             at low health, as the emergency step); Stamina to Health Self last, when
//             its feature is on (Stamina To Health At over 0) and stamina is over its
//             floor. The kit Kit Min Success skipped is still the last resort.
//    Stamina  a stamina kit, a listed potion (out of Magic stance), Revitalize Self,
//             then any stamina potion (AttemptStaminaChain).
//    Mana     Stamina to Mana Self or a mana potion; with stamina at the floor, stamina
//             first (AttemptManaRefill).
//  A vital already full says so and does nothing. Kits and potions need Use Kits in
//  Magic Mode / Use Potions as always.
//
//  Busy: the request waits (up to OneTouchWaitMs) for the same things the vitals step
//  waits for: a kit or cast in flight, the server answering the last action, a gesture,
//  a busy count. A heal is let through like the emergency heals are (the pacer's heal
//  rules, a leftover busy count cleared, no stance settle, and a kit or potion - not a
//  spell - while a buff cast is still pending). Past OneTouchWaitMs it says why and stops.
//
//  Requested from any thread (chat commands run on AC's thread); the pass runs on the
//  next heartbeat (pump thread). Pressing the key again while a request waits does not
//  queue a second one.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Plugin.RynthAi;

internal enum OneTouchVital { Health = 0, Stamina = 1, Mana = 2 }

public partial class BuffManager
{
    /// <summary>The longest a request waits for the character to be free, or for a stance step (the
    /// wand swap, peace for a kit), before it gives up and says why.</summary>
    internal const long OneTouchWaitMs = 8000;

    private readonly long[] _oneTouchAt = new long[3];   // OneTouchVital -> Environment.TickCount64 of the request, 0 = none

    // True on the pump thread while a one-touch heal is being paced or attempted: the heal rules of the
    // pacer (HealNeedNow) and the stance settle (HealIsUrgent) apply whatever health is.
    private bool _oneTouchHealthScope;

    /// <summary>Asks for one pass for <paramref name="vital"/> on the next heartbeat. Any thread.</summary>
    internal void RequestOneTouch(OneTouchVital vital)
    {
        // A request already waiting keeps its time: pressing again is not a second heal.
        Interlocked.CompareExchange(ref _oneTouchAt[(int)vital], Math.Max(1, Environment.TickCount64), 0);
    }

    /// <summary>A request for <paramref name="vital"/> is waiting to run.</summary>
    internal bool OneTouchPending(OneTouchVital vital) => Volatile.Read(ref _oneTouchAt[(int)vital]) != 0;

    private static string OneTouchName(OneTouchVital v) => v switch
    {
        OneTouchVital.Health => "Heal",
        OneTouchVital.Stamina => "Stamina",
        _ => "Mana",
    };

    private enum OneTouchStep { Sent, InProgress, Nothing }

    /// <summary>HeartbeatCore, every tick (macro on or off): the waiting requests, health first, one per tick.</summary>
    private void TickOneTouchRequest()
    {
        for (int i = 0; i < _oneTouchAt.Length; i++)
        {
            long at = Volatile.Read(ref _oneTouchAt[i]);
            if (at == 0) continue;
            var vital = (OneTouchVital)i;
            string name = OneTouchName(vital);
            long waited = Environment.TickCount64 - at;

            ReleaseStaleVitalCastMacroOff();
            string busy = OneTouchBusyReason(vital, out bool itemsOnly);
            string why = busy.Length > 0 ? $"the character stayed busy ({busy})" : "";
            if (busy.Length == 0)
            {
                OneTouchStep step = OneTouchStepNow(vital, itemsOnly, out string said);
                if (step != OneTouchStep.InProgress)
                {
                    Interlocked.Exchange(ref _oneTouchAt[i], 0);
                    _host.WriteToChat($"[RynthAi] {name}: {said}", 1);
                    return;
                }
                why = said;
            }
            if (waited < OneTouchWaitMs) return;   // the lower vitals wait too: one action at a time
            Interlocked.Exchange(ref _oneTouchAt[i], 0);
            _host.WriteToChat($"[RynthAi] {name}: not done, {why}.", 1);
            _host.Log($"[RynthAi] One-touch {name.ToLowerInvariant()}: gave up after {waited} ms: {why}");
            return;
        }
    }

    /// <summary>With the macro off nothing else releases a vital cast the server never confirmed
    /// (PendingGiveUpReached, as the macro's tick does); without that the next press would wait on it.</summary>
    private void ReleaseStaleVitalCastMacroOff()
    {
        if (_settings.IsMacroRunning || _pendingSpellId == 0 || _pendingSpellId != _pendingVitalSpellId) return;
        if (!PendingGiveUpReached(SelfBuffGiveUpMs)) return;
        _host.Log($"[BuffDiag] vital cast id={_pendingSpellId} not confirmed by chat ({GiveUpWhy(SelfBuffGiveUpMs)}) — released, no strike (macro off).");
        _pendingSpellId = 0;
        _pendingVitalSpellId = 0;
        _onCastResolved?.Invoke("vital cast unconfirmed");
    }

    /// <summary>
    /// What holds a pass back right now ("" = nothing). <paramref name="itemsOnly"/>: a buff cast is
    /// still pending but the server has finished it, so a health kit or potion may go and a spell may not
    /// (it would replace the pending cast; TryEmergencyHealWhilePending's rule).
    /// </summary>
    internal string OneTouchBusyReason(OneTouchVital vital, out bool itemsOnly)
    {
        itemsOnly = false;
        if (_vitals.MaxHealth <= 0) return "vitals not read yet";
        if (KitPending()) return "a kit is in flight";
        if (_pendingSpellId != 0)
        {
            if (vital != OneTouchVital.Health || _pendingSpellId == _pendingVitalSpellId) return "a cast is in flight";
            if (IsAwaitingCastResolution()) return "the server hasn't finished the last cast";
            itemsOnly = true;
        }
        if (vital != OneTouchVital.Health && (RynthClock.Now - _lastCastAttempt).TotalMilliseconds < _settings.SpellCastIntervalMs)
            return "inside the spell interval";

        bool pacerAllows;
        string pacerWhy;
        _oneTouchHealthScope = vital == OneTouchVital.Health;
        try { pacerAllows = PacerAllows(out pacerWhy); }
        finally { _oneTouchHealthScope = false; }
        if (!pacerAllows) return "server answering the last action: " + pacerWhy;
        if (!CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s))) return "a gesture is animating";
        // A heal goes ahead of a busy count the server has already finished (BusyHoldsHeal), or one the
        // pacer let a heal ahead of; the other vitals wait for the count to drop.
        if (BusyCount > 0 && !_pacerLetHealAhead && (vital != OneTouchVital.Health || BusyHoldsHeal()))
            return $"busy count {BusyCount}";
        return "";
    }

    /// <summary>
    /// One attempt for <paramref name="vital"/> (pump thread; internal for the host tests). Sent: something
    /// went out, or the vital is full or has no option at all (<paramref name="said"/> says which); InProgress:
    /// a step toward it is in flight (peace for the kit, the wand swap, a spell that has to wait for a
    /// pending cast) and the next tick tries again.
    /// </summary>
    private OneTouchStep OneTouchStepNow(OneTouchVital vital, bool itemsOnly, out string said)
    {
        int hp = _vitals.HealthPct, st = _vitals.StaminaPct, mn = _vitals.ManaPct;
        string vitals = $"health {hp}%, stamina {st}%, mana {mn}%";
        var before = (kit: _kitUsedAt, potion: _lastPotionAt, spell: _pendingSpellId);
        bool handled;
        bool staminaFirst = false;
        string noWhy;
        _vitalWhy = "";
        _healWaitKind = "";
        _oneTouchHealthScope = vital == OneTouchVital.Health;
        try
        {
            switch (vital)
            {
                case OneTouchVital.Health:
                    if (_vitals.CurrentHealth >= _vitals.MaxHealth) { said = $"health is full ({vitals})."; return OneTouchStep.Nothing; }
                    handled = AttemptHealChain(itemsBeforeSpell: CurrentCombatMode != CombatMode.Magic, allowSpell: !itemsOnly,
                        out string kitWhy, out string spellWhy, out string potionWhy);
                    string stamToHealthWhy = "not on (Stamina To Health At is 0)";
                    if (!handled && _settings.StaminaToHealthAt > 0 && !itemsOnly)
                    {
                        if (st > _settings.StaminaToHealthMinStamina)
                        {
                            _vitalWhy = "";
                            handled = AttemptVitalCast("Stamina to Health Self");
                            stamToHealthWhy = Why(_vitalWhy);
                        }
                        else stamToHealthWhy = $"stamina {st}% is not over {_settings.StaminaToHealthMinStamina}%";
                    }
                    noWhy = $"kit: {Why(kitWhy)}; Heal Self: {Why(spellWhy)}; potion: {Why(potionWhy)}; Stamina to Health: {stamToHealthWhy}";
                    break;
                case OneTouchVital.Stamina:
                    if (_vitals.CurrentStamina >= _vitals.MaxStamina) { said = $"stamina is full ({vitals})."; return OneTouchStep.Nothing; }
                    handled = AttemptStaminaChain();
                    noWhy = Why(_vitalWhy);
                    break;
                default:
                    if (_vitals.CurrentMana >= _vitals.MaxMana) { said = $"mana is full ({vitals})."; return OneTouchStep.Nothing; }
                    handled = AttemptManaRefill(st, out staminaFirst);
                    noWhy = Why(_vitalWhy);
                    break;
            }
        }
        finally { _oneTouchHealthScope = false; }

        string name = OneTouchName(vital).ToLowerInvariant();
        if (!handled)
        {
            // A spell held back for a pending buff cast: that cast resolves in a moment, then the spell may go.
            if (itemsOnly)
            {
                said = "waiting for the pending cast to end (" + noWhy + ")";
                return OneTouchStep.InProgress;
            }
            said = $"nothing could {(vital == OneTouchVital.Health ? "heal" : vital == OneTouchVital.Stamina ? "restore stamina" : "restore mana")} " +
                   $"({vitals}): {noWhy}.";
            _host.Log($"[RynthAi] One-touch {name}: {said}");
            return OneTouchStep.Nothing;
        }

        string how;
        if (_kitUsedAt != before.kit) how = vital == OneTouchVital.Health ? "using a healing kit" : "using a stamina kit";
        else if (_lastPotionAt != before.potion) how = "drinking a potion";
        else if (_pendingSpellId != 0 && _pendingSpellId == _pendingVitalSpellId && _pendingSpellId != before.spell)
            how = "casting " + (SpellTableStub.GetById(_pendingSpellId)?.Name ?? "a spell");
        else
        {
            // Handled without sending: a stance step is in flight (_healWaitKind says which).
            said = _healWaitKind.Length > 0 ? _healWaitKind : "getting ready";
            return OneTouchStep.InProgress;
        }
        said = how + (staminaFirst ? " (stamina first, for Stamina to Mana)" : "") + $" ({vitals}).";
        _host.Log($"[RynthAi] One-touch {name}: {said}");
        return OneTouchStep.Sent;
    }
}
