using System.Globalization;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// The loot mode (Settings > Looting, Loot Mode; meta setting LootMode; /ra loot mode). Profile is
/// the loot profile with RynthAi's built-in keeps, as always. Rares (owner, 2026-10-31: "a loot mode
/// that only loots mana stones when it needs them and rares") ignores the loot profile: corpses,
/// chests and ground items give up their rares and the mana stones the mana stone keep count still
/// wants, nothing else.
/// </summary>
internal static class LootModes
{
    public const int Profile = 0, Rares = 1;

    /// <summary>The Loot Mode combo's entries, by mode.</summary>
    public static readonly string[] Labels = { "Profile", "Rares and needed mana stones only" };

    /// <summary>A rule label in rares mode starts with this (LootChatLine shows it bare).</summary>
    public const string RulePrefix = "Rares mode: ";
    public const string RareRule = RulePrefix + "rare";
    public const string StoneRule = RulePrefix + "mana stone (needed)";

    public static int Clamp(int mode) => mode == Rares ? Rares : Profile;

    public static bool IsRares(int mode) => mode == Rares;

    /// <summary>The command word: "profile" or "rares".</summary>
    public static string Word(int mode) => IsRares(mode) ? "rares" : "profile";

    /// <summary>The mode for a chat line: "Profile (the loot profile)" or "Rares and needed mana stones only".</summary>
    public static string Describe(int mode) => IsRares(mode) ? Labels[Rares] : "Profile (the loot profile)";

    /// <summary>profile | rares (also rare, and the numbers 0 and 1 a meta sends), any case.</summary>
    public static bool TryParse(string? text, out int mode)
    {
        mode = Profile;
        string t = (text ?? string.Empty).Trim().ToLowerInvariant();
        switch (t)
        {
            case "profile": mode = Profile; return true;
            case "rares": case "rare": mode = Rares; return true;
        }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && (n == Profile || n == Rares))
        {
            mode = (int)n;
            return true;
        }
        return false;
    }
}

/// <summary>
/// What makes an item a rare, on the client. Two marks, the same ones AutoVendor's never-sell
/// check uses:
///   * IconUnderlay (PropertyDataId 52) = 0x06005B0C, the rare plate behind the icon (UtilityBelt's
///     test). It is in the item's PublicWeenieDesc, so the create message carries it: a rare on a
///     corpse is known before any ID.
///   * RareId (PropertyInt 17) above 0. ACE sends it only with an appraisal (an AssessmentProperty),
///     so it decides only for an identified item.
/// </summary>
internal static class RareItems
{
    public const int RareIdKey = 17;
    public const uint IconUnderlayKey = 52;
    public const uint RareUnderlay = 23308;   // 0x5B0C, the low 24 bits of 0x06005B0C

    /// <summary>True for a rare; <paramref name="how"/> says which mark showed it.</summary>
    public static bool IsRare(RynthCoreHost host, WorldObject item, out string how)
    {
        how = string.Empty;
        int rareId = item.Values(RareIdKey, 0);
        if (rareId > 0)
        {
            how = "RareId " + rareId.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (host.HasGetObjectDataIdProperty
            && host.TryGetObjectDataIdProperty(unchecked((uint)item.Id), IconUnderlayKey, out uint underlay)
            && (underlay & 0x00FFFFFFu) == RareUnderlay)
        {
            how = "rare icon";
            return true;
        }
        return false;
    }
}
