using System;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Mana stone tapping state machine.
///
/// Two independent responsibilities:
///   1. Tap inventory items that have MaxMana >= threshold by using a mana stone on them.
///      The stone drains mana out of the item (eventually destroying it).
///   2. After each drain, use the now-charged stone on the player to recharge their gear.
///
/// Triggered from OnHeartbeat; chat messages drive state transitions.
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
    private int      _tapCyclesThisStone;

    private const long ThinkIntervalMs  = 600;
    private const long ActionTimeoutMs  = 8000;
    private const int  MaxTapsPerStone  = 60;   // safety cap — one stone per mana pool

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
                TryBeginTap();
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
            // "The Mana Stone drains X points of mana from the ..." — stone filled
            if (text.Contains("The Mana Stone drains"))
            {
                _tapCyclesThisStone++;
                IssueUseOnPlayer();
                return;
            }
            // Item was destroyed by over-tapping — stone should still have mana
            if (text.Contains("is destroyed") || text.Contains("has been destroyed"))
            {
                _activeItemId = 0;
                IssueUseOnPlayer();
                return;
            }
            // Stone had no mana to give (shouldn't happen mid-tap, but handle it)
            if (text.Contains("The Mana Stone has no mana"))
            {
                Reset();
                return;
            }
        }

        if (_state == TapState.UsingOnPlayer)
        {
            // "The Mana Stone gives X points of mana to ..." — transfer complete
            if (text.Contains("The Mana Stone gives"))
            {
                // If the source item is still alive and has mana, tap it again
                if (_activeItemId != 0 && _tapCyclesThisStone < MaxTapsPerStone && StillHasMana(_activeItemId))
                {
                    IssueTapItem(_activeStoneId, _activeItemId);
                }
                else
                {
                    Reset();
                }
                return;
            }
            // Timeout handled by OnHeartbeat
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void TryBeginTap()
    {
        // Find a mana stone in inventory (prefer one from ConsumableRules, any ManaStone otherwise)
        int stoneId = FindInventoryStone();
        if (stoneId == 0) return;

        // Find a tappable item: MaxMana >= threshold, CurrentMana > 0, not a stone, not equipped
        int targetId = FindTapTarget();
        if (targetId == 0) return;

        _activeStoneId     = stoneId;
        _activeItemId      = targetId;
        _tapCyclesThisStone = 0;

        _host.Log($"[RynthAi] ManaStone: tapping 0x{(uint)targetId:X8} with stone 0x{(uint)stoneId:X8}");
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

        _state          = TapState.UsingOnPlayer;
        _actionIssuedAt = NowMs;
        _host.UseObjectOn(unchecked((uint)_activeStoneId), playerId);
    }

    private void Reset()
    {
        _state              = TapState.Idle;
        _activeStoneId      = 0;
        _activeItemId       = 0;
        _actionIssuedAt     = 0;
        _tapCyclesThisStone = 0;
    }

    private int FindInventoryStone()
    {
        // Prefer stones from the user's ConsumableRules list (specific ones they added)
        foreach (var rule in _settings.ConsumableRules)
        {
            if (!rule.Type.Equals("ManaStone", StringComparison.OrdinalIgnoreCase)) continue;
            if (_objectCache[rule.Id] != null)
                return rule.Id;
        }

        // Fall back to any mana stone in inventory
        foreach (var item in _objectCache.GetInventory())
        {
            if (item.ObjectClass == AcObjectClass.ManaStone &&
                item.Name.Contains("Mana Stone", StringComparison.OrdinalIgnoreCase))
                return item.Id;
        }

        return 0;
    }

    private int FindTapTarget()
    {
        int threshold = _settings.ManaTapMinMana;

        foreach (var item in _objectCache.GetInventory())
        {
            if (item.ObjectClass == AcObjectClass.ManaStone) continue;

            // Skip equipped items
            if (_host.HasGetObjectIntProperty &&
                _host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                && wield != 0)
                continue;

            if (!_host.HasGetObjectIntProperty) continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.MaxMana, out int maxMana)
                || maxMana < threshold)
                continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana)
                || curMana <= 0)
                continue;

            return item.Id;
        }

        return 0;
    }

    private bool StillHasMana(int itemId)
    {
        if (!_host.HasGetObjectIntProperty) return false;
        if (!_host.TryGetObjectIntProperty(unchecked((uint)itemId), (uint)AcIntProperty.CurrentMana, out int cur))
            return false;
        return cur > 0;
    }
}
