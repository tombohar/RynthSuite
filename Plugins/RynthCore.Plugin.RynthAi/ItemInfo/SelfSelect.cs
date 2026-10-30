using System;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

/// <summary>
/// Items RynthAi selects by itself (the looter picking an item up, a meta's selectitem), so the
/// selection-change handler can tell them from the player's own clicks: Item Info on select
/// prints only for the player's. <see cref="Mark"/> goes right before Host.SelectItem.
/// </summary>
internal static class SelfSelect
{
    /// <summary>A selection change this soon after <see cref="Mark"/>, for the same id, is ours.</summary>
    internal const long WindowMs = 2000;

    internal static Func<long> Clock = () => Environment.TickCount64;

    private static uint _id;
    private static long _at;

    internal static void Mark(uint id)
    {
        _at = Clock();
        _id = id;
    }

    internal static bool IsOurs(uint id) => id != 0 && id == _id && Clock() - _at <= WindowMs;
}
