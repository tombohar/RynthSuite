using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Combat's wand swaps and the first cast after one (2026-11-11). Pure: the caller passes what is in
/// hand, the kinds (CombatMode values) and the times.
///
/// The report (Outdoor Hunt, a wand user): "wand swapping can't make up its mind to do in combat or
/// peacemode then there is a long pause between picking up the next target". Two causes in combat's
/// own equip, both for a new target that wants another wand than the one in hand:
///
///   * The swap. Only a swap away from a caster to a melee weapon or a launcher went through peace
///     (HandSwapSequence, 2026-10-23). A swap from one caster to another was the client's own use of
///     the new one. On ACE the old caster leaves the Held slot first (a Held caster needs both hands
///     empty), and a wielded item put into a pack in a combat mode first plays a forced change to bare
///     hands; the new caster then puts the character back into Magic. Every stance change goes through
///     peace, so the character went Magic, peace, bare hands, peace, Magic. From peace (Peace Mode When
///     Idle between hunt targets) combat also asked for Magic beside the use, which added a leg in front.
///     Now, with Fast Weapon Swap on, a swap away from a caster goes through peace whatever it is for:
///     peace (when not in it), the old caster into a pack (answered at once in peace), the new one into
///     the Held slot, then Magic, one leg each (<see cref="UseHandsSwap"/>).
///
///   * The first cast. AttackWithMagic held every cast for a flat 3 s after any wield, and each held
///     tick counted as an attack, so the next try came one attack interval later: the first spell at a
///     new target went 3-4.5 s after the wield although the wand was in hand and the stance up in about
///     1 s (07:38:08.540 wield, Magic asked 07:38:08.924, casts skipped 09.814 and 11.314, cast 12.840,
///     "lock->attack 4734 ms ... interval 2954"). Now the cast goes once the client has read Magic for
///     <see cref="StanceSettleMs"/> since a change after the wield, and <see cref="StanceMinAfterRequestMs"/>
///     after the wield or after our own Magic request that followed it (BuffManager's stance settle,
///     2026-10-05), never later than the old 3 s (<see cref="EquipCastHoldMs"/>), and a held tick is not
///     an attack.
/// </summary>
internal static class WandSwapRules
{
    /// <summary>The old flat wait after a wield, still the longest the first cast waits.</summary>
    public const double EquipGateMaxMs = 3000;
    /// <summary>The client reads Magic this long before the first cast (BuffManager.StanceSettleMs).</summary>
    public const double StanceSettleMs = 300;
    /// <summary>The wield, or our own Magic request after it, this long before the first cast (BuffManager.StanceMinAfterRequestMs).</summary>
    public const double StanceMinAfterRequestMs = 1000;

    /// <summary>CombatMode.Magic (the kind a caster puts the character in).</summary>
    private const int Magic = 8;

    /// <summary>
    /// True: combat's swap from <paramref name="inHand"/> to <paramref name="chosen"/> is the hands swap
    /// through peace. Fast Weapon Swap on, a wield allowed (the swap tracker said swap), a caster in hand
    /// (<paramref name="inHandKind"/> Magic) and another weapon chosen: a melee weapon, a launcher, or
    /// (since 2026-11-11) another caster.
    /// </summary>
    public static bool UseHandsSwap(bool fastWeaponSwap, bool allowWield, int inHand, int chosen, int inHandKind)
        => fastWeaponSwap && allowWield && inHand != 0 && chosen != 0 && inHand != chosen && inHandKind == Magic;

    /// <summary>
    /// How long the first cast after a wield still waits (0 = cast now). Called only with the chosen
    /// wand in hand and the client reading Magic. <paramref name="sinceEquipMs"/>: since the last wield
    /// (negative or at least <see cref="EquipGateMaxMs"/>: no wait). <paramref name="sinceModeChangeMs"/>:
    /// since the client's combat mode last changed (negative: never seen). <paramref name="sinceMagicRequestMs"/>:
    /// since combat last asked for a stance (negative: never).
    /// No mode change seen since the wield: the client may not have caught up with the server's stance
    /// change yet, so the old wait stands.
    /// </summary>
    public static double EquipCastHoldMs(double sinceEquipMs, double sinceModeChangeMs, double sinceMagicRequestMs)
    {
        if (sinceEquipMs < 0 || sinceEquipMs >= EquipGateMaxMs) return 0;
        double cap = EquipGateMaxMs - sinceEquipMs;
        if (sinceModeChangeMs < 0 || sinceModeChangeMs >= sinceEquipMs) return cap;
        // The client's mode field can flip before the server's stance animation has run (2026-10-05):
        // the floor runs from the wield, or from our own request when one followed it.
        double floorFrom = sinceMagicRequestMs >= 0 && sinceMagicRequestMs < sinceEquipMs ? sinceMagicRequestMs : sinceEquipMs;
        double hold = Math.Max(StanceSettleMs - sinceModeChangeMs, StanceMinAfterRequestMs - floorFrom);
        return Math.Clamp(hold, 0, cap);
    }
}
