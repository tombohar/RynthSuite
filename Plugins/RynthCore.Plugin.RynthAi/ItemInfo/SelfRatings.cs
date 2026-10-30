using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

/// <summary>
/// The character's own ratings for Item Info's "select yourself" output. Pure: every value comes
/// in through a reader, so the tests format it without a client.
///
/// Where the numbers come from (ACE / ACECustom, nothing server-specific):
///   * Totals (PropertyInt 307-316, 323, 331, 350, 351, 381, 382) are what the server resolves
///     into an identify of a creature (AppraiseInfo.AddRatings): base + gear + augmentations +
///     buffs, the same numbers the client's Ratings page shows. They arrive only with an identify,
///     so the plugin asks for one on itself first (they are AssessmentProperty, not SendOnLogin).
///   * Luminance augmentations (333-338, 341, 342) are SendOnLogin: always on the player.
/// Zero or absent ratings are left out (the server omits zeros too). Plain ASCII.
/// </summary>
internal static class SelfRatings
{
    /// <summary>One rating: the property id, the Item Info tag and a readable name.</summary>
    internal readonly record struct Rating(uint Stype, string Tag, string Name);

    /// <summary>The combat totals, in Item Info's rating order (D, DR, C, CD, CR, CDR).</summary>
    internal static readonly Rating[] Combat =
    {
        new(307, "D",   "Damage"),
        new(308, "DR",  "Damage Resist"),
        new(313, "C",   "Crit"),
        new(314, "CD",  "Crit Damage"),
        new(315, "CR",  "Crit Resist"),
        new(316, "CDR", "Crit Damage Resist"),
    };

    /// <summary>Healing and resistance totals.</summary>
    internal static readonly Rating[] Other =
    {
        new(323, "HB",   "Healing Boost"),
        new(331, "NR",   "Nether Resist"),
        new(350, "DoTR", "DoT Resist"),
        new(351, "LR",   "Life Resist"),
    };

    /// <summary>PK totals (only non-zero on PK characters).</summary>
    internal static readonly Rating[] Pk =
    {
        new(381, "PKD",  "PK Damage"),
        new(382, "PKDR", "PK Damage Resist"),
    };

    /// <summary>Luminance augmentation ratings (already part of the totals above).</summary>
    internal static readonly Rating[] LumAugs =
    {
        new(333, "D",  "Damage"),
        new(334, "DR", "Damage Reduction"),
        new(335, "CD", "Crit Damage"),
        new(336, "CR", "Crit Reduction"),
        new(337, "SE", "Surge Effect"),
        new(338, "SC", "Surge Chance"),
        new(341, "V",  "Vitality"),
        new(342, "HB", "Healing"),
    };

    /// <summary>
    /// The output lines. <paramref name="readInt"/> returns a property's value (0 when absent);
    /// <paramref name="identified"/> is false when the self-identify didn't come back in time, so
    /// the totals may be missing. <paramref name="oneLine"/> joins everything Mag-style.
    /// </summary>
    public static List<string> Build(string name, Func<uint, int> readInt, bool identified, bool oneLine)
    {
        var lines = new List<string>(4);

        string combat = Join(Combat, readInt, plus: false);
        string header = (string.IsNullOrWhiteSpace(name) ? "Your" : name.Trim() + "'s") + " ratings: "
                        + (combat.Length > 0 ? combat : "no combat ratings");
        if (!identified) header += " (not identified - totals may be incomplete)";
        lines.Add(header);

        string other = Join(Other, readInt, plus: false);
        if (other.Length > 0) lines.Add(other);

        string pk = Join(Pk, readInt, plus: false);
        if (pk.Length > 0) lines.Add("PK: " + pk);

        string lum = Join(LumAugs, readInt, plus: true);
        if (lum.Length > 0) lines.Add("Lum augs (in the totals): " + lum);

        return oneLine ? new List<string> { string.Join(" | ", lines) } : lines;
    }

    /// <summary>"D 45, DR 30, ..." for the non-zero ratings in <paramref name="set"/>; "" when none.</summary>
    private static string Join(Rating[] set, Func<uint, int> readInt, bool plus)
    {
        var sb = new StringBuilder();
        foreach (Rating r in set)
        {
            int v = readInt(r.Stype);
            if (v == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(r.Tag).Append(' ');
            if (plus && v > 0) sb.Append('+');
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
