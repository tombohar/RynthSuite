using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Safety stops for bots running unattended. Every stop is an option; each one
/// says in chat why the macro stopped. Most metas handle a death themselves (run
/// back from the lifestone), so Stop Macro On Death is off by default.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private bool _deathSeen;
    private readonly Queue<long> _noComponentFails = new();
    private long _packFullCheckedAt;
    private bool _packFull;
    private bool _packFullAnnounced;

    private const int  NoComponentFailsToStop = 3;
    private const long NoComponentWindowMs    = 60_000;
    private const long PackFullCheckMs        = 3_000;

    /// <summary>Death = health read as 0 (edge). Runs every tick after the vitals poll.</summary>
    private void CheckPlayerDeath(LegacyUiSettings s)
    {
        bool dead = _vitals.MaxHealth > 0 && _vitals.CurrentHealth == 0;
        if (dead && !_deathSeen)
        {
            _deathSeen = true;
            Log("[RynthAi] Death detected (health 0).");
            try { RecordDeathSpot(); }   // where the corpse will be (RynthAiPlugin.OwnCorpse.cs)
            catch (Exception ex) { Log($"[RynthAi] Death spot not recorded: {ex.Message}"); }
            ArmDeathRecall(s);           // before Stop Macro On Death: it reads whether the macro ran
            if (s.StopMacroOnDeath && s.IsMacroRunning)
            {
                HandleMacroRunCommand("stop", Array.Empty<string>());
                ChatLine("[RynthAi] You died — macro stopped (Stop Macro On Death is on).");
            }
        }
        else if (!dead && _vitals.CurrentHealth > 0)
        {
            _deathSeen = false;
        }
    }

    private long _deathHoldUntil;
    private bool _deathHolding;
    private const long DeathSettleMs = 5_000;

    /// <summary>
    /// True from a death until the character is alive, out of portal space and
    /// has had 5 s to settle. While it holds, the bot does nothing: no buffing,
    /// combat, weapon swaps, looting, busy clears or navigation. Both death-portal
    /// crashes (2026-09-28 12:04 and 18:06, inside AC's code while it tore down and
    /// reloaded the world) came with the bot still casting and dequipping through
    /// the death. Reads the vitals every tick (the regular poll is every 30).
    /// </summary>
    private bool _wasPortalingForPacer;

    private bool _wasMacroForPacer;

    /// <summary>
    /// The macro is switched on (off to on, whoever did it): a UseDone that arrived while it was off, a
    /// player's own refused action, is not news. Without this the pacer's observer took it for a fresh
    /// 'too busy' answer at the first check and held every start for the probe wait (~1.5 s).
    /// </summary>
    private void NoteMacroForPacer(bool running)
    {
        if (running && !_wasMacroForPacer) RynthCore.Plugin.Shared.ActionPacer.NoteMacroStart();
        _wasMacroForPacer = running;
    }

    /// <summary>Portal space starts: the server dropped what was open (no breaker on a teleport's gap).</summary>
    private void NotePortalForPacer()
    {
        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        if (portaling && !_wasPortalingForPacer)
        {
            RynthCore.Plugin.Shared.ActionPacer.Forget("portal space");
            _buffManager?.ForgetPendingWield("portal space");
        }
        _wasPortalingForPacer = portaling;
    }

    private bool HoldForDeath()
    {
        long now = Environment.TickCount64;
        bool dead = Host.HasGetPlayerVitals
                    && Host.TryGetPlayerVitals(out uint hp, out uint maxHp, out _, out _, out _, out _)
                    && maxHp > 0 && hp == 0;
        if (dead)
        {
            _deathHoldUntil = long.MaxValue;
            _metaManager?.OnPlayerDeath();   // the meta's Death condition sees it after the hold
        }
        else if (_deathHoldUntil == long.MaxValue)
        {
            // Alive again: wait out the portal to the lifestone, then settle.
            if (!(Host.HasIsPortaling && Host.IsPortaling()))
                _deathHoldUntil = now + DeathSettleMs;
        }

        bool hold = now < _deathHoldUntil;
        if (hold != _deathHolding)
        {
            _deathHolding = hold;
            if (hold)
            {
                Log("[RynthAi] Died — holding all actions until respawned and out of portal space.");
                _combatManager?.ReleaseHeldTurn();
                _navigationEngine?.Stop();
                RynthCore.Plugin.Shared.ActionPacer.Forget("death");   // the server dropped whatever was open
                _buffManager?.CancelPendingRecall("death");                // no /lifestone or macro restart after the respawn
                _buffManager?.ForgetPendingWield("death");                 // a swap the death cut short is not a failed swap
            }
            else
            {
                Log("[RynthAi] Respawned — resuming.");
                try
                {
                    // Recall On Death first; the corpse trip then starts from where it lands (EndDeathRecall).
                    if (!StartDeathRecall()) OnRespawnedForOwnCorpse();   // Travel Back To My Corpse
                }
                catch (Exception ex) { Log($"[RynthAi] Death recall / own corpse travel not started: {ex.Message}"); }
            }
        }
        return hold;
    }

    /// <summary>Chat hook: repeated "don't have all the components" stops the macro when enabled.</summary>
    private void CheckChatForSafetyStops(string text)
    {
        var s = _dashboard?.Settings;
        if (s == null || !s.StopMacroOnNoComponents || !s.IsMacroRunning) return;
        if (text.IndexOf("have all the components for this spell", StringComparison.OrdinalIgnoreCase) < 0) return;

        long now = Environment.TickCount64;
        _noComponentFails.Enqueue(now);
        while (_noComponentFails.Count > 0 && now - _noComponentFails.Peek() > NoComponentWindowMs)
            _noComponentFails.Dequeue();
        if (_noComponentFails.Count < NoComponentFailsToStop) return;

        _noComponentFails.Clear();
        HandleMacroRunCommand("stop", Array.Empty<string>());
        ChatLine("[RynthAi] Out of spell components — macro stopped (Stop Macro On No Components is on).");
    }

    /// <summary>
    /// True when no pack (main pack included) has a free slot. Checked at most
    /// every 3 s. The first time it turns true: a chat notice, and the macro
    /// stops if Stop Macro When Pack Full is on.
    /// </summary>
    internal bool IsPackFull()
    {
        long now = Environment.TickCount64;
        if (now - _packFullCheckedAt < PackFullCheckMs) return _packFull;
        _packFullCheckedAt = now;

        bool full = _objectCache != null && _playerId != 0
                    && WorldObjectCache.FindPackFor(Host, _objectCache, includeMainPack: true, requireFree: 1) == 0;
        _packFull = full;
        if (!full) { _packFullAnnounced = false; return false; }

        if (!_packFullAnnounced)
        {
            _packFullAnnounced = true;
            var s = _dashboard?.Settings;
            if (s != null && s.StopMacroWhenPackFull && s.IsMacroRunning)
            {
                HandleMacroRunCommand("stop", Array.Empty<string>());
                ChatLine("[RynthAi] Your packs are full — macro stopped (Stop Macro When Pack Full is on).");
            }
            else
            {
                ChatLine("[RynthAi] Your packs are full — not looting until there's room.");
            }
        }
        return true;
    }

    private long _lootReserveCheckedAt;
    private bool _belowLootReserve;

    /// <summary>
    /// True when fewer slots are free than looting keeps for weapon swaps
    /// (LootReserveFreeSlots). The pickup gate refuses every item then, so a new
    /// corpse would only be walked to, opened and given up on. Checked at most every
    /// 3 s; -1 (side-pack capacity unknown) is not treated as low.
    /// </summary>
    internal bool IsBelowLootReserve()
    {
        long now = Environment.TickCount64;
        if (now - _lootReserveCheckedAt < PackFullCheckMs) return _belowLootReserve;
        _lootReserveCheckedAt = now;

        int free = _playerId != 0 ? WorldObjectCache.CountFreeItemSlots(Host, _objectCache) : -1;
        _belowLootReserve = free >= 0 && free < WorldObjectCache.LootReserveFreeSlots;
        if (_belowLootReserve)
            WorldObjectCache.WarnPackFull(Host, $"not looting with {free} free slot(s) so weapon swaps still work");
        return _belowLootReserve;
    }
}
