using System;
using System.Collections.Generic;
using RynthCore.Loot.T11;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// T11 (tier 11+ ACECustom) loot support for <see cref="WorldObject.Values(int, int)"/>:
/// answers the virtual T11 keys (<see cref="T11Keys"/>) and puts back the wield-requirement and
/// rating properties the server strips from T11 appraisals. Parsed item text is cached per
/// object and re-parsed only when the text changes. Holds the player's wield counters for the
/// "Can Wield" key; since 2026-10-20 the server plugin reports them (RynthILT reads ACECustom's
/// "/aug" reply and applies its item-aug override, then calls RynthAi.Hub SetPlayerCounter), so
/// without one they stay unknown, as on any retail-like server. One character per process, so
/// the state is static.
/// </summary>
internal static class T11ItemSupport
{
    /// <summary>The logged-in character's T11 wield counters.</summary>
    public static T11PlayerAugs Player { get; } = new();

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Entry> Parsed = new();

    // Bound so a long session's corpse items can't grow the cache without limit; a full
    // clear is fine because re-parsing is cheap and only happens for items still in use.
    private const int MaxEntries = 4096;

    private sealed class Entry
    {
        public string Name = string.Empty;
        public string LongDesc = string.Empty;
        public string Use = string.Empty;
        public T11ServerProps Server = T11ServerProps.None;
        public T11ItemInfo Info = T11ItemInfo.None;
    }

    /// <summary>The tier / quality properties the item's appraisal carried (ACECustom 50109, 9061, 9060).</summary>
    public static T11ServerProps ServerProps(WorldObject item) => new(
        item.Values(T11Catalog.PropZcTier, 0),
        item.Values(T11Catalog.PropWeaponAugScaleTier, 0),
        item.Values(T11Catalog.PropWeaponAugScaleQuality, -1));

    /// <summary>True when <paramref name="name"/> carries the server's T11 drop prefix.</summary>
    public static bool HasT11Name(string? name)
        => !string.IsNullOrEmpty(name) && name.StartsWith(T11Catalog.NamePrefix, StringComparison.Ordinal);

    /// <summary>The parsed T11 view of <paramref name="item"/> (<see cref="T11ItemInfo.None"/> for other items).</summary>
    public static T11ItemInfo Get(WorldObject item)
    {
        string name = item.Name.Length > 0 ? item.Name : item.Values(StringValueKey.Name, string.Empty);
        string longDesc = item.Values(StringValueKey.LongDesc, string.Empty);
        string use = item.Values(StringValueKey.Use, string.Empty);
        T11ServerProps server = ServerProps(item);

        lock (Gate)
        {
            if (Parsed.TryGetValue(item.Id, out Entry? e)
                && string.Equals(e.Name, name, StringComparison.Ordinal)
                && string.Equals(e.LongDesc, longDesc, StringComparison.Ordinal)
                && string.Equals(e.Use, use, StringComparison.Ordinal)
                && e.Server == server)
                return e.Info;
        }

        T11ItemInfo info = T11ItemInfo.Parse(name, longDesc, use, server);
        lock (Gate)
        {
            if (Parsed.Count >= MaxEntries) Parsed.Clear();
            Parsed[item.Id] = new Entry { Name = name, LongDesc = longDesc, Use = use, Server = server, Info = info };
        }
        return info;
    }

    /// <summary>A virtual T11 key's value, or <paramref name="defaultValue"/> for an unused id.</summary>
    public static int VirtualValue(WorldObject item, int key, int defaultValue)
    {
        try
        {
            return T11Keys.TryGetValue(Get(item), key, Player, out int value) ? value : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// The value a T11 appraisal stripped for a retail key (158/159/160, 370-379), else
    /// <paramref name="fallback"/>. Only T11-named or T11-stamped items are parsed, so other
    /// items pay a string compare and one property read.
    /// </summary>
    public static int StrippedValue(WorldObject item, int key, int fallback)
    {
        if (!T11Keys.IsStrippedKey(key)) return fallback;
        try
        {
            string name = item.Name.Length > 0 ? item.Name : item.Values(StringValueKey.Name, string.Empty);
            if (!HasT11Name(name) && ServerProps(item).Tier < T11Catalog.MinTier) return fallback;
            return T11Keys.TryGetStrippedValue(Get(item), key, out int value) ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// A wield counter from the server plugin (RynthAi.Hub SetPlayerCounter): <paramref name="propertyId"/>
    /// is the counter's Int64 property id (T11Counter), <paramref name="value"/> &lt; 0 = unknown again.
    /// False for an id that is no T11 counter. Any thread.
    /// </summary>
    public static bool SetCounter(uint propertyId, long value)
    {
        if (!Enum.IsDefined(typeof(T11Counter), unchecked((int)propertyId))) return false;
        var counter = (T11Counter)unchecked((int)propertyId);
        if (value < 0) Player.Forget(counter);
        else Player.Set(counter, value);
        return true;
    }

    /// <summary>Drops the cached parse of a deleted object.</summary>
    public static void Forget(int objectId)
    {
        lock (Gate) Parsed.Remove(objectId);
    }

    /// <summary>Forgets the character's counters and every cached item (logout).</summary>
    public static void Reset()
    {
        Player.Clear();
        lock (Gate) Parsed.Clear();
    }
}
