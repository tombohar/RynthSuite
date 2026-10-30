using System;
using System.Collections.Generic;
using System.Linq;

namespace RynthCore.Plugin.RynthAi;

public enum WeaponCategory { Bow, Crossbow, Atlatl }

/// <summary>One fletching combine from the world database: the head is applied to the shaft.</summary>
public sealed class AmmoRecipe
{
    public string HeadName { get; }
    public string ShaftName { get; }
    public string OutputName { get; }
    public WeaponCategory Category { get; }

    /// <summary>
    /// The recipe's skill difficulty. Also the ammo's quality rank: the retail data makes
    /// better ammo harder (plain Arrowheads 5 … Deadly Prismatic 400).
    /// </summary>
    public int Difficulty { get; }

    /// <summary>
    /// What to craft or equip first: the Prismatic family above everything else — it is
    /// what players actually craft — then difficulty (Deadly > Greater > plain).
    /// </summary>
    public int Rank { get; }

    internal string HeadKey { get; }
    internal string ShaftKey { get; }

    public AmmoRecipe(string head, string shaft, string output, WeaponCategory category, int difficulty)
    {
        HeadName = head;
        ShaftName = shaft;
        OutputName = output;
        Category = category;
        Difficulty = difficulty;
        Rank = difficulty + (output.Contains("Prismatic", StringComparison.OrdinalIgnoreCase) ? AmmoRecipes.PrismaticFirst : 0);
        HeadKey = AmmoRecipes.Normalize(head);
        ShaftKey = AmmoRecipes.Normalize(shaft);
    }

    public bool IsHead(string itemName) => AmmoRecipes.Normalize(itemName) == HeadKey;
    public bool IsShaft(string itemName) => AmmoRecipes.Normalize(itemName) == ShaftKey;
}

/// <summary>
/// Ammo crafting data and the decisions made over it. No host calls, so the whole table
/// can be checked offline. The rows (AmmoRecipes.Data.cs) are generated from the ACE world
/// database by Tools/gen-ammo-recipes.py — regenerate rather than hand-edit.
/// </summary>
public static partial class AmmoRecipes
{
    /// <summary>
    /// Lowest success chance worth attempting. A failed fletching combine destroys both the
    /// head and the shaft bundles (Infinite heads: the shafts), so this wants skill about
    /// 73 over the recipe's difficulty.
    /// </summary>
    public const double MinSuccessChance = 0.90;

    /// <summary>Added to a Prismatic recipe's rank; above the hardest difficulty (400).</summary>
    internal const int PrismaticFirst = 1000;

    /// <summary>ACE's SkillCheck.GetSkillChance (default factor 0.03), which crafting uses.</summary>
    public static double SuccessChance(int skill, int difficulty) =>
        1.0 - 1.0 / (1.0 + Math.Exp(0.03 * (skill - difficulty)));

    /// <summary>
    /// The best (highest-ranked) recipe for <paramref name="category"/> whose head and shaft are
    /// both carried and that <paramref name="fletching"/> (buffed skill) makes reliably.
    /// <paramref name="tooHard"/> is the best one skipped for skill, so the caller can say so.
    /// </summary>
    public static AmmoRecipe? BestCraftable(IEnumerable<string> carriedNames, WeaponCategory category,
                                            int fletching, out AmmoRecipe? tooHard)
    {
        var carried = new HashSet<string>();
        foreach (string n in carriedNames)
            if (!string.IsNullOrEmpty(n)) carried.Add(Normalize(n));

        tooHard = null;
        foreach (var r in Index.ByRank)
        {
            if (r.Category != category) continue;
            if (!carried.Contains(r.HeadKey) || !carried.Contains(r.ShaftKey)) continue;
            if (SuccessChance(fletching, r.Difficulty) >= MinSuccessChance) return r;
            tooHard ??= r;
        }
        return null;
    }

    /// <summary>Rank of finished ammo on the recipes' scale (<see cref="AmmoRecipe.Rank"/>), or 1
    /// for ammo no recipe makes (quest arrows and the like) so crafted ammo outranks it.</summary>
    public static int OutputRank(string name, WeaponCategory category)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        return Index.OutputRank.TryGetValue((category, Normalize(name)), out int d) ? d : 1;
    }

    /// <summary>Finished ammo for the category — not a head, a shaft or a bundle.</summary>
    public static bool IsLooseAmmoName(string name, WeaponCategory category)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = Normalize(name);
        if (n.Contains("bundle") || n.Contains("wrapped")) return false;
        if (n.Contains("arrowhead") || n.Contains("arrowshaft")) return false;
        if (n.Contains("quarrelhead") || n.Contains("quarrelshaft")) return false;
        if (n.Contains("darthead") || n.Contains("dartshaft")) return false;
        return category switch
        {
            WeaponCategory.Bow      => n.Contains("arrow"),
            WeaponCategory.Crossbow => n.Contains("quarrel") || n.Contains("bolt"),
            WeaponCategory.Atlatl   => n.Contains("dart"),
            _                       => false,
        };
    }

    // Names come from the object cache, so the length is attacker-ish input as far as
    // this frame is concerned — an unbounded stackalloc on it can blow the stack
    // (2026-06-03 audit P2). Item names are far under this; anything longer heaps.
    private const int MaxStackNameChars = 256;

    /// <summary>Lower-case letters and digits only: "Frog-Crotch" and "Frog Crotch" compare equal.</summary>
    public static string Normalize(string value)
    {
        char[]? rented = value.Length > MaxStackNameChars ? new char[value.Length] : null;
        Span<char> buffer = rented is null ? stackalloc char[MaxStackNameChars] : rented;
        int count = 0;
        foreach (char ch in value)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;

            buffer[count++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..count]);
    }

    // Built on first use, in its own type so it can't initialise before All (the two
    // partial files' static initialisers run in no guaranteed order).
    private static class Index
    {
        public static readonly AmmoRecipe[] ByRank = All.OrderByDescending(r => r.Rank).ToArray();
        public static readonly Dictionary<(WeaponCategory, string), int> OutputRank = Build();

        private static Dictionary<(WeaponCategory, string), int> Build()
        {
            var d = new Dictionary<(WeaponCategory, string), int>();
            foreach (var r in All)
            {
                var key = (r.Category, Normalize(r.OutputName));
                if (!d.TryGetValue(key, out int have) || r.Rank > have) d[key] = r.Rank;
            }
            return d;
        }
    }
}
