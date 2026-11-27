// Trade Shop: what counts as payment, by weenie class id (WCID) only.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here. His version read
// payment by name and took any item's Value as pyreals, so a junk item with a high Value counted as
// money. Here an item is payment only when its WCID is in this table, and a trade note is worth its
// WCID's face value. Anything else is worth 0.
//
// The WCIDs are the retail weenies, as in the ACE world database (ACEmulator, AGPL; read for the
// numbers only): ACE.Entity/Enum/WeenieClassName.cs (W_COINSTACK_CLASS, W_TRADENOTE*_CLASS) and the
// weenie tables of ACE-World-Database v0.9.293 (name, use count, bonded / attuned flags).
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>What one payment WCID is.</summary>
public enum TradeShopCurrency
{
    /// <summary>Not payment: worth 0.</summary>
    None = 0,
    Pyreals = 1,
    /// <summary>A trade note (MMD is the 250,000 one): worth its face value in pyreals.</summary>
    TradeNote = 2,
    /// <summary>A single-use Legendary Key: priced by the shop's own pyreals-per-key rate.</summary>
    LegendaryKey = 3,
}

/// <summary>The payment WCIDs and what each is worth.</summary>
public static class TradeShopCurrencyTable
{
    /// <summary>W_COINSTACK_CLASS: a stack of pyreals (each is 1 pyreal).</summary>
    public const uint PyrealWcid = 273;

    /// <summary>W_TRADENOTE250000_CLASS: the 250,000 trade note (an MMD).</summary>
    public const uint MmdWcid = 20630;

    /// <summary>Pyreals in one MMD note.</summary>
    public const long PyrealsPerMmd = 250_000;

    /// <summary>Trade note WCID -> face value (W_TRADENOTE&lt;value&gt;_CLASS, all fourteen).</summary>
    private static readonly Dictionary<uint, long> NoteFaces = new()
    {
        [2621] = 100,
        [2622] = 500,
        [2623] = 1_000,
        [2624] = 5_000,
        [2625] = 10_000,
        [7374] = 15_000,
        [7375] = 20_000,
        [7376] = 25_000,
        [2626] = 50_000,
        [7377] = 75_000,
        [2627] = 100_000,
        [20628] = 150_000,
        [20629] = 200_000,
        [MmdWcid] = 250_000,
    };

    /// <summary>
    /// "Legendary Key" weenies that open a Legendary Armor, Magic, or Weapon Chest
    /// (key code legarmormagicweaponchest), have ONE use (MaxStructure 1) and are neither bonded nor
    /// attuned, so they can be traded and one key is always one key. Keys with more uses (48748-48750,
    /// 51586, 51648, 51963, 52010, 72338, 72807, 87168) are left out: a used one has fewer uses left than
    /// its weenie says. Bonded ones (72474, 72628, 72635, 72669) can't be traded. The Aged Legendary Key
    /// (48746) opens a different chest.
    /// </summary>
    private static readonly HashSet<uint> LegendaryKeyWcids = new() { 48747, 48914, 51558, 72048, 72600 };

    /// <summary>What <paramref name="wcid"/> is as payment (None: not payment).</summary>
    public static TradeShopCurrency Classify(uint wcid)
    {
        if (wcid == PyrealWcid) return TradeShopCurrency.Pyreals;
        if (NoteFaces.ContainsKey(wcid)) return TradeShopCurrency.TradeNote;
        if (LegendaryKeyWcids.Contains(wcid)) return TradeShopCurrency.LegendaryKey;
        return TradeShopCurrency.None;
    }

    /// <summary>A trade note's face value in pyreals, from its WCID (0 when it isn't a note).</summary>
    public static long NoteFaceValue(uint wcid) => NoteFaces.TryGetValue(wcid, out long v) ? v : 0;

    /// <summary>Every payment WCID (the table, for tests and the window's help).</summary>
    public static IEnumerable<uint> AllWcids()
    {
        yield return PyrealWcid;
        foreach (uint w in NoteFaces.Keys) yield return w;
        foreach (uint w in LegendaryKeyWcids) yield return w;
    }
}
