using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>Everything the "stay out of the way" check looks at, read once per pass.</summary>
internal struct GateInputs
{
    public bool InPortalSpace;
    /// <summary>A corpse or chest is being looted (<see cref="LootWatch"/>).</summary>
    public bool Looting;
    public bool VendorOpen;
    public bool TradeOpen;
    /// <summary>The client's busy count is above zero (a use, a cast, or anyone's item move in flight).</summary>
    public bool ClientBusy;
    /// <summary>The character's motion queue isn't empty (a gesture, a cast animation, a stance change).</summary>
    public bool Animating;
    /// <summary>RynthAi's activity bits (RynthAi.Activity), or null when RynthAi isn't loaded or not ready.</summary>
    public RynthAiActivity? Bot;
    /// <summary>The "Sort during fights" setting: moves may go while RynthAi fights (still never mid-action).</summary>
    public bool SortInCombat;
}

/// <summary>
/// Why no item may move right now, or null when it's fine. Never while a corpse or chest is
/// being looted, a vendor or trade window is open, the character is busy with a use or cast or
/// still animating, or in portal space; and never while RynthAi is looting, salvaging, buffing,
/// healing, making ammo or waiting on the server for something it sent. While RynthAi fights,
/// nothing moves either unless "Sort during fights" is on. After any pause, moves wait
/// <see cref="QuietMs"/> more (the next corpse, cast or buff is often a moment away).
/// </summary>
internal sealed class SortGate
{
    /// <summary>How long the way must have been clear before the next move.</summary>
    public const long QuietMs = 1500;

    private long _pausedAt = long.MinValue / 2;
    private string _lastReason = string.Empty;

    /// <summary>The reason without the quiet time: pure, for the tests and the window.</summary>
    public static string? Reason(in GateInputs g)
    {
        if (g.InPortalSpace) return "portal space";
        if (g.Looting) return "looting";
        if (g.VendorOpen) return "vendor open";
        if (g.TradeOpen) return "trading";
        if (g.ClientBusy || g.Animating) return "busy";
        if (g.Bot is RynthAiActivity a)
        {
            if ((a & RynthAiActivity.Looting) != 0) return "RynthAi is looting";
            if ((a & RynthAiActivity.Salvaging) != 0) return "RynthAi is salvaging";
            if ((a & RynthAiActivity.Buffing) != 0) return "RynthAi is buffing";
            if ((a & RynthAiActivity.Crafting) != 0) return "RynthAi is making ammo";
            if ((a & RynthAiActivity.ServerAction) != 0) return "RynthAi is waiting on the server";
            if ((a & RynthAiActivity.Combat) != 0 && !g.SortInCombat) return "RynthAi is fighting";
        }
        return null;
    }

    /// <summary>The reason, held for <see cref="QuietMs"/> after it clears.</summary>
    public string? Evaluate(in GateInputs g, long nowMs)
    {
        string? why = Reason(in g);
        if (why != null)
        {
            _pausedAt = nowMs;
            _lastReason = why;
            return why;
        }
        return nowMs - _pausedAt < QuietMs ? _lastReason : null;
    }

    public void Reset()
    {
        _pausedAt = long.MinValue / 2;
        _lastReason = string.Empty;
    }
}

/// <summary>
/// Whether the open corpse or chest is being looted. The client keeps reporting the last corpse
/// as open after it has been looted (until another opens or you walk away), so an open container
/// only counts while something is happening to it: it just opened, or its contents changed,
/// within <see cref="ActiveMs"/>; and for <see cref="AfterMs"/> after that (the next corpse is
/// often a moment away). The Lua script's rule, unchanged.
/// </summary>
internal sealed class LootWatch
{
    public const long ActiveMs = 4000;
    public const long AfterMs = 1500;

    private uint _id;
    private int _count = -1;
    private long _changedAt = long.MinValue / 2;
    private long _lastLootingAt = long.MinValue / 2;

    /// <param name="containerId">The open ground container (0 = none).</param>
    /// <param name="itemCount">How many items it holds now.</param>
    public bool Update(uint containerId, int itemCount, long nowMs)
    {
        if (containerId != 0)
        {
            // An unreadable count (-1) is not a change: a count read that flickered between -1 and
            // the real number made a corpse left open look like it was being looted for ever
            // ("paused: looting" while standing still, 2026-10-11).
            if (containerId != _id || (itemCount >= 0 && itemCount != _count))
            {
                _id = containerId;
                _count = itemCount;
                _changedAt = nowMs;
            }
            if (nowMs - _changedAt < ActiveMs)
            {
                _lastLootingAt = nowMs;
                return true;
            }
        }
        return nowMs - _lastLootingAt < AfterMs;
    }

    public void Reset()
    {
        _id = 0;
        _count = -1;
        _changedAt = long.MinValue / 2;
        _lastLootingAt = long.MinValue / 2;
    }
}
