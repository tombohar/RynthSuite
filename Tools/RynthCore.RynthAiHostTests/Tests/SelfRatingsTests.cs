using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Item Info on yourself (/ra iteminfo me, select or click yourself): the rating totals the server
// puts in a self-identify (AppraiseInfo.AddRatings) and the login-sent luminance augs, as chat lines.
internal static class SelfRatingsTests
{
    public static void Register(Runner r)
    {
        r.Add("self ratings: totals, other ratings and lum augs in Item Info's tags", Totals);
        r.Add("self ratings: zeros are left out; nothing known still prints a header", Empty);
        r.Add("self ratings: not identified is flagged; one-line layout joins with |", NotIdentifiedOneLine);
    }

    private static System.Func<uint, int> Reader(Dictionary<uint, int> ints)
        => stype => ints.TryGetValue(stype, out int v) ? v : 0;

    private static void Totals()
    {
        var ints = new Dictionary<uint, int>
        {
            [307] = 45, [308] = 30, [313] = 10, [314] = 20, [315] = 5, [316] = 15,
            [323] = 8, [331] = 12, [351] = 4,
            [333] = 5, [334] = 5, [341] = 50,
        };
        List<string> lines = SelfRatings.Build("Silentkelpie", Reader(ints), identified: true, oneLine: false);
        Check.Eq(lines.Count, 3, "header, other ratings, lum augs (no PK line)");
        Check.Eq(lines[0], "Silentkelpie's ratings: D 45, DR 30, C 10, CD 20, CR 5, CDR 15", "combat totals in Item Info order");
        Check.Eq(lines[1], "HB 8, NR 12, LR 4", "healing and resists");
        Check.Eq(lines[2], "Lum augs (in the totals): D +5, DR +5, V +50", "lum augs signed");
        foreach (string l in lines)
            foreach (char c in l)
                Check.True(c >= 0x20 && c <= 0x7E, "plain ASCII: " + l);
    }

    private static void Empty()
    {
        List<string> lines = SelfRatings.Build("", Reader(new Dictionary<uint, int> { [307] = 0 }), identified: true, oneLine: false);
        Check.Eq(lines.Count, 1, "only the header");
        Check.Eq(lines[0], "Your ratings: no combat ratings", "no name: 'Your'");

        List<string> pk = SelfRatings.Build("Pk", Reader(new Dictionary<uint, int> { [381] = 3, [382] = -2 }), true, false);
        Check.Eq(pk[1], "PK: PKD 3, PKDR -2", "PK line, negative kept");
    }

    private static void NotIdentifiedOneLine()
    {
        var ints = new Dictionary<uint, int> { [307] = 7, [337] = 2 };
        List<string> lines = SelfRatings.Build("Me", Reader(ints), identified: false, oneLine: true);
        Check.Eq(lines.Count, 1, "one line");
        Check.Eq(lines[0], "Me's ratings: D 7 (not identified - totals may be incomplete) | Lum augs (in the totals): SE +2",
                 "flag on the header, parts joined");
    }
}
