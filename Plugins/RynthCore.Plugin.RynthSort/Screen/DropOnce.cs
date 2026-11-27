namespace RynthCore.Plugin.RynthSort.Screen;

/// <summary>
/// One drop, one set of choices. An item dragged out of RynthCore's Inventory window and let go
/// over a pack card reaches the window twice: as the ImGui payload the Inventory publishes
/// ("RYNTH_INV_ITEM") and through the engine's drop target (the Inventory offers a drop outside
/// itself to every panel that takes items). A drag out of AC's own windows comes one way or the
/// other, depending on the engine. The second arrival of the same item within
/// <see cref="WindowSec"/> is the same drop. Pure; the window keeps one.
/// </summary>
internal sealed class DropOnce
{
    public const double WindowSec = 1.0;

    private uint _item;
    private double _at = double.MinValue;

    /// <summary>True when <paramref name="itemId"/> dropped at <paramref name="now"/> (seconds) is a new drop, not the echo of the last one.</summary>
    public bool Take(uint itemId, double now)
    {
        if (itemId == 0) return false;
        if (itemId == _item && now - _at < WindowSec && now >= _at) return false;
        _item = itemId;
        _at = now;
        return true;
    }

    public void Reset()
    {
        _item = 0;
        _at = double.MinValue;
    }
}
