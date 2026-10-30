// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>The spell groups the status summary and the Void tracker look for (upstream's lists).</summary>
internal static class SpellIds
{
    /// <summary>Prodigal (3679-3744 and later) and Spectral rare buffs.</summary>
    public static readonly HashSet<uint> Rare = new()
    {
        3679, 3680, 3681, 3682, 3683, 3684, 3685, 3686, 3687, 3688, 3689, 3690, 3691, 3692, 3693, 3694,
        3695, 3696, 3697, 3698, 3699, 3700, 3701, 3702, 3703, 3704, 3705, 3706, 3707, 3708, 3709, 3710,
        3711, 3712, 3713, 3714, 3715, 3716, 3717, 3718, 3719, 3720, 3721, 3722, 3723, 3724, 3725, 3726,
        3727, 3728, 3729, 3730, 3731, 3732, 3733, 3734, 3735, 3736, 3737, 3738, 3739, 3740, 3741, 3742,
        3743, 3744, 5025, 5026, 5436, 5903, 5905, 5907, 5909, 5911,
        4131, 4132, 4133, 4134, 4135, 4136, 4137, 4138, 4139, 4140, 4141, 4142, 4208, 4221, 5023, 5435,
        5904, 5906, 5908, 5910, 5912,
    };

    /// <summary>House, mansion and other long "place" buffs (Dark Equilibrium, Ride the Lightning, ...).</summary>
    public static readonly HashSet<uint> House = new()
    {
        3896, 3894, 3897, 3895, 6146, 4099, 4025, 3323, 3324, 3325, 3237, 3831, 3830, 2995, 2993, 2997,
        3829, 3977, 3978, 3979,
    };

    /// <summary>Long enchantments that aren't buffs you refresh (Blazing Heart, meads, rare armor damage, ...).</summary>
    public static readonly HashSet<uint> NotBuff = new() { 3204, 5127, 5131, 5132, 5978, 5192, 6170, 5966, 5122 };

    /// <summary>Beers (Bobo's Quickening and friends).</summary>
    public static readonly HashSet<uint> Beer = new() { 3531, 3533, 3862, 3864, 3530, 3863 };

    /// <summary>Pages of Salt and Ash (Incantation of the Black Book).</summary>
    public static readonly HashSet<uint> Pages = new() { 3869 };

    // Aetheria surges
    public const uint SurgeOfDestruction = 5204;
    public const uint SurgeOfProtection = 5206;
    public const uint SurgeOfRegeneration = 5208;
    public const uint CloakedInSkill = 5753;

    // Void damage-over-time spells
    public static readonly HashSet<uint> Corrosion = new() { 5387, 5388, 5389, 5390, 5391, 5392, 5393, 5394 };
    public static readonly HashSet<uint> Corruption = new() { 5395, 5396, 5397, 5398, 5399, 5400, 5401, 5402 };
    public static readonly HashSet<uint> Curse = new() { 5339, 5340, 5341, 5342, 5343, 5344, 5337, 5338 };
}
