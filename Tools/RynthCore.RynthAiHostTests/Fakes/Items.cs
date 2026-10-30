using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;

namespace RynthCore.RynthAiHostTests.Fakes;

/// <summary>
/// Builds loot-test items: a WorldObject whose property reads come only from a fixed overlay
/// (no cache, no live fallback), so a key the item was not given reads as the caller's default.
/// This is exactly how AutoVendor feeds vendor-list items to the same evaluators.
/// </summary>
internal sealed class Item
{
    private static int _nextId = 0x50000001;

    private readonly ItemPropertyOverlay _overlay = new() { LiveFallback = false };
    public WorldObject Wo { get; }

    public Item(string name, AcObjectClass cls = AcObjectClass.Misc, int? id = null)
    {
        Wo = new WorldObject(id ?? _nextId++, name, cls) { Overlay = _overlay };
    }

    public Item Int(int key, int value) { _overlay.Ints[unchecked((uint)key)] = value; return this; }
    public Item Int(AcIntProperty key, int value) => Int((int)key, value);
    public Item Dbl(int key, double value) { _overlay.Doubles[unchecked((uint)key)] = value; return this; }
    public Item Str(int key, string value) { _overlay.Strings[unchecked((uint)key)] = value; return this; }

    public static implicit operator WorldObject(Item i) => i.Wo;
}
