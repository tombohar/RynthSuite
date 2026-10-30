// TinkerPlanner.cs - What a Crafting tab would apply to one item, and which bag to use.
//
// Pure: the scan reads the item into TinkerItemFacts and the runner re-plans from fresh facts,
// so these rules are the only place the apply order and caps live (and the host tests drive
// them directly). Server rows say which rules apply to them (TinkerMaterial, from the server
// plugin's profile); nothing here knows a server:
//   * rows run in TinkerMaterial.Order (imbues and server crit lines before stat tinks; with
//     "rend last" the rend moves to the end, the way UB-IT's foolproof-rend option does);
//   * a tink-slot row is cut to the free slots (10 minus the item's tinks); no-slot rows aren't;
//   * one imbue per item (the item's own, or the first planned one);
//   * a row with an applied int is skipped once that int reads 2 on the item;
//   * rows of one stack group stop at the group's cap combined;
//   * an after-cap row goes on last, once, and only when the tink slots are full;
//   * a prepared row needs a bag prepared at the chosen tier (its name says Minor, Moderate or
//     Major); a raw bag becomes one on that tier's Ammunition.
using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

/// <summary>What the planner needs to know about an item.</summary>
internal sealed class TinkerItemFacts
{
    public int Id;
    public string Name = string.Empty;
    public AcObjectClass Class;
    /// <summary>The item's workmanship (ItemWorkmanship 105; 0 = can't be tinkered).</summary>
    public double Workmanship;
    /// <summary>NumTimesTinkered (171).</summary>
    public int Tinks;
    /// <summary>ImbuedEffect (179) is set.</summary>
    public bool Imbued;
    /// <summary>DamageType (45): melee/missile damage, the caster's element.</summary>
    public int DamageType;
    /// <summary>Retained (bool 91).</summary>
    public bool Retained;
    /// <summary>ItemMaxMana (108).</summary>
    public int MaxMana;
    public bool Equipped;
    /// <summary>Reads an int property of the item (a row's applied int: 2 = that salvage is on).</summary>
    public Func<int, int> FlagInt = _ => 0;
}

/// <summary>One planned application: the row and the salvage it resolves to (a rend resolves per item).</summary>
internal readonly record struct TinkerStep(TinkerMaterial Row, int Material);

/// <summary>A bag a step could use (for <see cref="TinkerPlanner.SelectBag"/>).</summary>
internal readonly record struct TinkerBagChoice(int Id, double Workmanship, double? Chance);

internal static class TinkerPlanner
{
    /// <summary>
    /// The rows a tab offers: retail, then <paramref name="server"/> (the connected server's own
    /// salvage, from the plugin that owns it), then the player's custom rows.
    /// </summary>
    public static List<TinkerMaterial> Rows(TinkerBench bench, IEnumerable<TinkerMaterial>? server, IEnumerable<CustomTinker>? custom)
    {
        var rows = new List<TinkerMaterial>(TinkerCatalog.RetailFor(bench));
        if (server != null) rows.AddRange(server);
        if (custom != null)
        {
            foreach (var c in custom)
            {
                if (c.Bench != (int)bench || c.Material <= 0) continue;
                var kind = c.Imbue ? TinkerKind.Imbue : c.Once ? TinkerKind.Once : TinkerKind.Tink;
                rows.Add(new TinkerMaterial(c.Material, TinkerCatalog.MaterialName(c.Material), TinkerOrigin.Custom, kind,
                    string.IsNullOrWhiteSpace(c.Note) ? "custom" : c.Note, 900 + rows.Count,
                    UsesTinkSlot: c.UsesTinkSlot, NameMatch: c.NameMatch ?? string.Empty));
            }
        }
        return rows;
    }

    /// <summary>The order a row runs in under these settings.</summary>
    public static int OrderOf(TinkerMaterial row, TinkerBenchSettings s)
        => row.Id == TinkerCatalog.RendId && s.RendLast ? 990 : row.Order;

    /// <summary>Why the scan leaves an item out ("" = it's eligible and the plan decides).</summary>
    public static string Excluded(TinkerItemFacts item, TinkerBenchSettings s)
    {
        if (item.Equipped) return "equipped";
        if (item.Workmanship <= 0) return "no workmanship";
        if (item.Tinks > Math.Clamp(s.MaxTinks, 0, TinkerCatalog.MaxTinks)) return $"{item.Tinks} tinks (max {s.MaxTinks})";
        if (s.MinFreeTinks > 0 && TinkerCatalog.MaxTinks - item.Tinks < s.MinFreeTinks) return $"fewer than {s.MinFreeTinks} free tinks";
        if (s.SkipImbued && item.Imbued) return "imbued";
        return string.Empty;
    }

    /// <summary>
    /// The ordered steps for one item (empty when nothing ticked applies).
    /// <paramref name="attempted"/> is how many times this run already tried a row key on the
    /// item: the item's tinks count failures too, but no-slot server rows and the ticked counts
    /// don't show on it, so a re-plan would otherwise repeat them.
    /// </summary>
    public static List<TinkerStep> Plan(TinkerBench bench, TinkerItemFacts item, TinkerBenchSettings s,
                                         IReadOnlyList<TinkerMaterial> rows, Func<string, int>? attempted = null)
    {
        var steps = new List<TinkerStep>();
        int free = Math.Max(0, TinkerCatalog.MaxTinks - item.Tinks);
        bool imbueTaken = item.Imbued;
        Dictionary<string, int>? stacked = null;
        List<TinkerStep>? afterCap = null;

        foreach (var row in rows.OrderBy(r => OrderOf(r, s)))
        {
            string key = TinkerCatalog.Key(row);
            int count = s.Counts.TryGetValue(key, out int c) ? c : 0;
            int tried = attempted?.Invoke(key) ?? 0;
            if (row.Kind != TinkerKind.Tink) count = Math.Min(count, 1);
            count -= tried;
            if (count <= 0) continue;
            int material = row.Id == TinkerCatalog.RendId ? TinkerCatalog.RendFor(item.DamageType, s.PierceForCombo) : row.Id;
            if (material <= 0 || !Applies(row, material, item)) continue;

            int n = row.Kind == TinkerKind.Tink ? count : 1;
            if (row.Kind == TinkerKind.Imbue)
            {
                if (imbueTaken) continue;
                imbueTaken = true;
            }
            if (row.AppliedInt != 0 && item.FlagInt(row.AppliedInt) == 2) continue;
            if (row.StackGroup.Length > 0 && row.StackCap > 0)
            {
                stacked ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int used = stacked.TryGetValue(row.StackGroup, out int u) ? u : 0;
                n = Math.Min(n, row.StackCap - used);
                stacked[row.StackGroup] = used + Math.Max(0, n);
            }
            if (row.AfterCap)
            {
                (afterCap ??= new List<TinkerStep>()).Add(new TinkerStep(row, material));
                continue;
            }
            if (row.UsesTinkSlot)
            {
                n = Math.Min(n, free);
                free -= Math.Max(0, n);
            }
            for (int i = 0; i < n; i++) steps.Add(new TinkerStep(row, material));
        }
        if (afterCap != null && free == 0) steps.AddRange(afterCap);
        return steps;
    }

    /// <summary>Row-level requirements the item's own properties decide.</summary>
    private static bool Applies(TinkerMaterial row, int material, TinkerItemFacts item)
    {
        if (row.Origin != TinkerOrigin.Retail) return true;
        return material switch
        {
            TinkerCatalog.Moonstone => item.MaxMana > 0,
            TinkerCatalog.Leather => !item.Retained,
            TinkerCatalog.Sandstone => item.Retained,
            _ => true,
        };
    }

    /// <summary>
    /// Whether a salvage bag can serve a step: a full bag (100 units) of the material, server bags
    /// matched by name (Abyssal-Touched Gems share Black Garnet's id, Foolproof rend bags may not
    /// carry a material at all).
    /// </summary>
    public static bool BagMatches(TinkerStep step, string bagName, int bagMaterial, int bagUses, int trinketTier = 0)
    {
        if (bagUses < 100) return false;
        bagName ??= string.Empty;
        if (step.Row.Prepared)
            return bagMaterial == step.Material && TierOf(bagName) == Math.Clamp(trinketTier, 0, TinkerCatalog.TrinketTiers.Length - 1);
        bool abyssal = bagName.IndexOf("Abyssal", StringComparison.OrdinalIgnoreCase) >= 0;
        if (step.Row.NameMatch.Length > 0)
            return bagName.IndexOf(step.Row.NameMatch, StringComparison.OrdinalIgnoreCase) >= 0;
        if (abyssal) return false;
        if (bagMaterial == step.Material) return true;
        return step.Row.Id == TinkerCatalog.RendId && TinkerCatalog.RendFromFoolproofName(bagName) == step.Material;
    }

    /// <summary>The prepared tier a name carries (0 Minor, 1 Moderate, 2 Major), else -1.</summary>
    public static int TierOf(string name)
    {
        name ??= string.Empty;
        for (int i = 0; i < TinkerCatalog.TrinketTiers.Length; i++)
            if (name.IndexOf(TinkerCatalog.TrinketTiers[i], StringComparison.OrdinalIgnoreCase) >= 0) return i;
        return -1;
    }

    /// <summary>A full, not yet prepared bag of a prepared row's salvage (no tier in its name).</summary>
    public static bool IsRawTrinketBag(int material, string bagName, int bagMaterial, int bagUses)
        => bagUses >= 100 && bagMaterial == material && TierOf(bagName) < 0;

    /// <summary>The Ammunition that prepares a raw bag into a tier bag.</summary>
    public static bool IsTierAmmunition(string name, int tier)
        => (name ?? string.Empty).IndexOf("Ammunition", StringComparison.OrdinalIgnoreCase) >= 0
           && TierOf(name!) == Math.Clamp(tier, 0, TinkerCatalog.TrinketTiers.Length - 1);

    /// <summary>True for a bag that can't fail ("Foolproof ...").</summary>
    public static bool IsFoolproof(string bagName)
        => (bagName ?? string.Empty).IndexOf("Foolproof", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Picks the bag for a step (-1 = none qualifies). With a minimum percent: the lowest
    /// workmanship bag that reaches it, so good bags are kept for hard tinks. Without one: the
    /// best chance (lowest workmanship on a tie). A bag whose chance isn't known (server rows)
    /// is the best workmanship one; the server's own percent is checked when it asks.
    /// </summary>
    public static int SelectBag(IReadOnlyList<TinkerBagChoice> bags, int minPercent)
    {
        int best = -1;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b.Chance is not double c)
            {
                if (best < 0 || (bags[best].Chance == null && b.Workmanship > bags[best].Workmanship)) best = i;
                continue;
            }
            if (minPercent > 0)
            {
                if (TinkerMath.Percent(c) < minPercent) continue;
                if (best < 0 || bags[best].Chance == null || b.Workmanship < bags[best].Workmanship) best = i;
            }
            else
            {
                if (best < 0 || bags[best].Chance == null) { best = i; continue; }
                double bc = bags[best].Chance!.Value;
                if (c > bc + 1e-9 || (Math.Abs(c - bc) <= 1e-9 && b.Workmanship < bags[best].Workmanship)) best = i;
            }
        }
        return best;
    }

    /// <summary>"Iron x3, Brass x2" for a plan.</summary>
    public static string Summary(IReadOnlyList<TinkerStep> steps)
    {
        if (steps.Count == 0) return "-";
        var parts = new List<string>();
        int i = 0;
        while (i < steps.Count)
        {
            int j = i;
            while (j < steps.Count && steps[j].Material == steps[i].Material && steps[j].Row.Origin == steps[i].Row.Origin) j++;
            string name = TinkerCatalog.MaterialName(steps[i].Material);
            parts.Add(j - i > 1 ? $"{name} x{j - i}" : name);
            i = j;
        }
        return string.Join(", ", parts);
    }
}
