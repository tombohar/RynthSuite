using System;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Mana stone tapping state machine.
///
/// Each think tick from Idle picks one of two actions:
///   1. If any worn item has CurrentMana below the threshold AND we have a
///      charged stone in inventory, use the stone on the player.
///   2. Otherwise, if we have an empty stone and an inventory item with at
///      least the threshold of CurrentMana, drain it.
///
/// Single-shot semantics:
///   - Using an empty stone on an item destroys the item and charges the stone.
///   - Using a charged stone on the player may also destroy the stone — if so
///     we just loot another next corpse.
/// </summary>
internal sealed class ManaStoneManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _objectCache;

    private enum TapState { Idle, TappingItem, UsingOnPlayer }

    private TapState _state        = TapState.Idle;
    private int      _activeStoneId;
    private int      _activeItemId;
    private long     _actionIssuedAt;
    private long     _lastThinkAt;

    private const long ThinkIntervalMs  = 600;
    private const long ActionTimeoutMs  = 8000;

    private static long NowMs => Environment.TickCount64;

    public ManaStoneManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache objectCache)
    {
        _host        = host;
        _settings    = settings;
        _objectCache = objectCache;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void OnHeartbeat(int busyCount)
    {
        if (!_settings.EnableManaTapping) { Reset(); return; }
        if (!_settings.IsMacroRunning)    { Reset(); return; }
        if (!_host.HasUseObjectOn)        return;
        if (busyCount > 0)                return;

        long now = NowMs;
        if (now - _lastThinkAt < ThinkIntervalMs) return;
        _lastThinkAt = now;

        switch (_state)
        {
            case TapState.Idle:
                TryBeginAction();
                break;

            case TapState.TappingItem:
            case TapState.UsingOnPlayer:
                // Timeout guard — server didn't respond in time
                if (now - _actionIssuedAt > ActionTimeoutMs)
                {
                    _host.Log($"[RynthAi] ManaStone: action timeout in state {_state}, resetting.");
                    Reset();
                }
                break;
        }
    }

    public void OnChatWindowText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_state == TapState.TappingItem)
        {
            // "The Mana Stone drains X points of mana from the ..." — single drain,
            // item is destroyed, stone is now charged. Back to idle so the next think
            // tick can decide whether worn gear needs the charge yet.
            if (text.Contains("The Mana Stone drains")
                || text.Contains("is destroyed")
                || text.Contains("has been destroyed"))
            {
                GoIdle();
                return;
            }
            return;
        }

        if (_state == TapState.UsingOnPlayer)
        {
            // "The Mana Stone gives X points of mana to ..." — transfer complete.
            // The stone may also have been destroyed in the process; either way
            // reset and let the next tick pick a fresh stone.
            if (text.Contains("The Mana Stone gives")
                || text.Contains("The Mana Stone is destroyed"))
            {
                Reset();
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void TryBeginAction()
    {
        int threshold = _settings.ManaTapMinMana;

        // Priority 1: dump a charged stone onto the player when worn gear needs it.
        if (WornEquipmentNeedsMana(threshold))
        {
            int chargedStone = FindChargedStone();
            if (chargedStone != 0)
            {
                _activeStoneId = chargedStone;
                _activeItemId  = 0;
                IssueUseOnPlayer();
                return;
            }
        }

        // Priority 2: drain a tappable item into an empty stone.
        int stoneId = FindEmptyStone();
        if (stoneId == 0) return;

        int targetId = FindTapTarget(threshold);
        if (targetId == 0) return;

        _activeStoneId = stoneId;
        _activeItemId  = targetId;

        _host.Log($"[RynthAi] ManaStone: draining 0x{(uint)targetId:X8} with stone 0x{(uint)stoneId:X8}");
        IssueTapItem(stoneId, targetId);
    }

    private void IssueTapItem(int stoneId, int itemId)
    {
        _state           = TapState.TappingItem;
        _actionIssuedAt  = NowMs;
        _host.UseObjectOn(unchecked((uint)stoneId), unchecked((uint)itemId));
    }

    private void IssueUseOnPlayer()
    {
        uint playerId = _host.GetPlayerId();
        if (playerId == 0) { Reset(); return; }

        // Confirm the stone still exists in inventory
        if (_objectCache[_activeStoneId] == null) { Reset(); return; }

        _host.Log($"[RynthAi] ManaStone: using charged stone 0x{(uint)_activeStoneId:X8} on player");
        _state          = TapState.UsingOnPlayer;
        _actionIssuedAt = NowMs;
        _host.UseObjectOn(unchecked((uint)_activeStoneId), playerId);
    }

    private void GoIdle()
    {
        _state          = TapState.Idle;
        _actionIssuedAt = 0;
    }

    private void Reset()
    {
        _state          = TapState.Idle;
        _activeStoneId  = 0;
        _activeItemId   = 0;
        _actionIssuedAt = 0;
    }

    /// <summary>True only when the item's name matches a configured ManaStone consumable.</summary>
    private bool IsManaStone(WorldObject item)
    {
        foreach (var rule in _settings.ConsumableRules)
        {
            if (!rule.Type.Equals("ManaStone", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(rule.Name)) continue;
            if (rule.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Find a stone with no charge (CurrentMana == 0) — only those can drain an item.</summary>
    private int FindEmptyStone()
    {
        if (!_host.HasGetObjectIntProperty) return 0;

        foreach (var item in _objectCache.GetInventory())
        {
            if (!IsManaStone(item)) continue;

            // Treat a stone as empty if CurrentMana is 0 or unreadable.
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int cur)
                && cur > 0)
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>Find the most-charged mana stone in inventory (any positive charge).</summary>
    private int FindChargedStone()
    {
        if (!_host.HasGetObjectIntProperty) return 0;

        int bestId   = 0;
        int bestMana = 0;

        foreach (var item in _objectCache.GetInventory())
        {
            if (!IsManaStone(item)) continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int cur))
                continue;
            if (cur <= 0) continue;

            if (cur > bestMana) { bestMana = cur; bestId = item.Id; }
        }

        return bestId;
    }

    private int FindTapTarget(int threshold)
    {
        if (!_host.HasGetObjectIntProperty) return 0;

        foreach (var item in _objectCache.GetInventory())
        {
            if (IsManaStone(item)) continue;

            // Skip equipped items
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                && wield != 0)
                continue;

            // Must currently hold at least the threshold worth of mana to be worth a drain
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana)
                || curMana < threshold)
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>True when at least one currently-worn item has CurrentMana below threshold.</summary>
    private bool WornEquipmentNeedsMana(int threshold)
    {
        if (!_host.HasGetObjectIntProperty) return false;

        foreach (var item in _objectCache.GetInventory())
        {
            if (IsManaStone(item)) continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                || wield == 0)
                continue;

            // Only items that actually consume mana matter
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.MaxMana, out int maxMana)
                || maxMana <= 0)
                continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana))
                continue;

            if (curMana < threshold) return true;
        }

        return false;
    }

}
