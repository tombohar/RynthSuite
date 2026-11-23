// KitPlanner.cs - "Make kits": which kits the pack's ingredients make, how many, and the next
// combine. Pure (System only), so RynthCore.RynthAiTests links it; KitRunner does the work.
//
// Retail data (ACE World Database v0.9.293, recipes 4022-4025; ACE RecipeManager):
//   Treated Mandrake (22448) on Treated Hyssop (22447), either way round
//       -> Combined Hyssop and Mandrake (22446)   Healing, difficulty 100
//   Combined Hyssop and Mandrake (22446) on Soft Bandages (22445), either way round
//       -> Plentiful Healing Kit (22449, 100 uses) Healing, difficulty 200
// Each combine uses one of each item, success or failure (a failure makes nothing). The chance is
// ACE's SkillCheck.GetSkillChance(buffed skill, difficulty) = 1 - 1/(1 + e^(0.03 (skill - diff))),
// plus the Craftsman augmentation's skill (which only raises it: our figure is the floor). An
// untrained skill is refused by the server.
// That is the only Plentiful kit in the retail data: there is no Plentiful mana or stamina kit to
// make there. Another kit chain is one more KitRecipe in Retail.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

/// <summary>An item a kit recipe uses or makes: its weenie class id and its name (the name is the fallback match).</summary>
internal sealed record KitPart(uint Wcid, string Name);

/// <summary>One combine: <see cref="Source"/> used on <see cref="Target"/> makes <see cref="Product"/>.</summary>
internal sealed record KitCombine(
    string Label, KitPart Source, KitPart Target, KitPart Product, int SkillId, int Difficulty,
    string SuccessText, string FailText);

/// <summary>A kit and the combines that make it (the last one makes the kit; earlier ones make its parts).</summary>
internal sealed record KitRecipe(string Id, string Name, KitPart Product, KitCombine[] Steps)
{
    public KitCombine Final => Steps[^1];

    /// <summary>The combine that makes <paramref name="part"/>, or null for an ingredient you carry.</summary>
    public KitCombine? Producer(KitPart part) => Steps.FirstOrDefault(s => s.Product.Wcid == part.Wcid);

    /// <summary>The items you carry for it (not made by an earlier step), in step order.</summary>
    public IEnumerable<KitPart> Ingredients()
    {
        var seen = new HashSet<uint>();
        foreach (var s in Steps)
            foreach (var p in new[] { s.Source, s.Target })
                if (seen.Add(p.Wcid)) yield return p;
    }
}

/// <summary>A stack in the pack as the planner sees it (Wcid 0 = unknown: the name decides).</summary>
internal readonly record struct KitStack(int Id, uint Wcid, string Name, int Count);

/// <summary>The crafting skill: its buffed value and training (2 trained, 3 specialized); Known false when the engine can't read it.</summary>
internal readonly record struct KitSkill(bool Known, int Buffed, int Training)
{
    public static readonly KitSkill Unknown = new(false, 0, 0);
    public bool Trained => Training >= 2;
}

/// <summary>One step's chance in a plan (-1 percent = unknown).</summary>
internal readonly record struct KitStepChance(KitCombine Step, int Percent, bool UnderFloor);

/// <summary>What the pack makes of one kit.</summary>
internal sealed class KitPlanRow
{
    public KitRecipe Recipe = null!;
    /// <summary>Kits the pack makes if every combine works.</summary>
    public int CanMake;
    /// <summary>Each item the recipe uses (and makes on the way), with the count carried.</summary>
    public List<(KitPart Part, int Count)> Parts = new();
    public List<KitStepChance> Chances = new();
    /// <summary>Why no combine can go now ("" when one can).</summary>
    public string Blocked = string.Empty;
}

/// <summary>The next combine: the stacks to use, or why none (Step null).</summary>
internal readonly record struct KitNext(KitCombine? Step, int SourceId, int TargetId, int Percent, string Why)
{
    public static KitNext None(string why) => new(null, 0, 0, -1, why);
}

internal static class KitPlanner
{
    public const int SkillHealing = 21;
    /// <summary>The Min chance % a new character starts with.</summary>
    public const int DefaultFloorPercent = 90;

    public static readonly KitPart SoftBandages = new(22445, "Soft Bandages");
    public static readonly KitPart CombinedHerbs = new(22446, "Combined Hyssop and Mandrake");
    public static readonly KitPart TreatedHyssop = new(22447, "Treated Hyssop");
    public static readonly KitPart TreatedMandrake = new(22448, "Treated Mandrake");
    public static readonly KitPart PlentifulHealingKit = new(22449, "Plentiful Healing Kit");

    /// <summary>The retail kit recipes (ACE World Database).</summary>
    public static readonly KitRecipe[] Retail =
    {
        new("plentiful-healing", "Plentiful Healing Kit", PlentifulHealingKit, new[]
        {
            new KitCombine("Combine the herbs", TreatedMandrake, TreatedHyssop, CombinedHerbs, SkillHealing, 100,
                "You crumble the dried and treated leaves together.",
                "You crumble the leaves and they become useless."),
            new KitCombine("Pack the kit", CombinedHerbs, SoftBandages, PlentifulHealingKit, SkillHealing, 200,
                "You pack the bandages with the herbs. The odor is pungent and heady. The kit appears to be bulkier than any others you have seen.",
                "The herbs spill out of the bandages onto the ground. The bandages are ruined as they unravel onto the dirt."),
        }),
    };

    public static string SkillName(int skillId) => skillId switch
    {
        SkillHealing => "Healing",
        _ => $"skill {skillId}",
    };

    // ── Chance ──────────────────────────────────────────────────────────────

    /// <summary>ACE SkillCheck.GetSkillChance (factor 0.03), 0-1.</summary>
    public static double Chance(int skill, int difficulty)
    {
        double c = 1.0 - 1.0 / (1.0 + Math.Exp(0.03 * (skill - difficulty)));
        return Math.Clamp(c, 0.0, 1.0);
    }

    /// <summary>The chance as the server's prompt shows it (a rounded percent).</summary>
    public static int Percent(int skill, int difficulty) => (int)Math.Round(Chance(skill, difficulty) * 100.0, MidpointRounding.AwayFromZero);

    /// <summary>A step's percent for this skill (-1 unknown).</summary>
    public static int StepPercent(KitCombine step, Func<int, KitSkill> skill)
    {
        var s = skill(step.SkillId);
        return s.Known ? Percent(s.Buffed, step.Difficulty) : -1;
    }

    // ── Counting ────────────────────────────────────────────────────────────

    public static bool Matches(KitPart part, KitStack stack)
        => stack.Wcid != 0 ? stack.Wcid == part.Wcid : string.Equals(stack.Name, part.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>How many of a part the pack holds (every matching stack).</summary>
    public static int Count(KitPart part, IReadOnlyList<KitStack> pack)
    {
        int n = 0;
        foreach (var s in pack) if (Matches(part, s)) n += Math.Max(1, s.Count);
        return n;
    }

    /// <summary>The first stack of a part (its object id), 0 when none.</summary>
    public static int FirstStack(KitPart part, IReadOnlyList<KitStack> pack)
    {
        foreach (var s in pack) if (Matches(part, s)) return s.Id;
        return 0;
    }

    /// <summary>Carried plus what the earlier steps can still make of it.</summary>
    private static int Available(KitRecipe r, KitPart part, IReadOnlyList<KitStack> pack, int depth)
    {
        int have = Count(part, pack);
        var producer = depth < 8 ? r.Producer(part) : null;
        return producer == null ? have : have + Makeable(r, producer, pack, depth + 1);
    }

    /// <summary>How many times a step can go with what the pack holds and makes.</summary>
    private static int Makeable(KitRecipe r, KitCombine step, IReadOnlyList<KitStack> pack, int depth = 0)
        => Math.Min(Available(r, step.Source, pack, depth), Available(r, step.Target, pack, depth));

    /// <summary>Kits the pack makes if every combine works.</summary>
    public static int CanMake(KitRecipe r, IReadOnlyList<KitStack> pack) => Makeable(r, r.Final, pack);

    // ── Planning ────────────────────────────────────────────────────────────

    /// <summary>What the pack makes of each recipe, with the chances at this skill and floor.</summary>
    public static List<KitPlanRow> Plan(IReadOnlyList<KitRecipe> recipes, IReadOnlyList<KitStack> pack,
                                        Func<int, KitSkill> skill, int floorPercent)
    {
        var rows = new List<KitPlanRow>();
        foreach (var r in recipes)
        {
            var row = new KitPlanRow { Recipe = r, CanMake = CanMake(r, pack) };
            foreach (var p in r.Ingredients()) row.Parts.Add((p, Count(p, pack)));
            foreach (var st in r.Steps)
            {
                int pct = StepPercent(st, skill);
                row.Chances.Add(new KitStepChance(st, pct, pct >= 0 && pct < floorPercent));
            }
            var next = Next(r, pack, skill, floorPercent);
            row.Blocked = next.Step == null ? next.Why : string.Empty;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// The next combine for <paramref name="r"/>: the kit itself when its two parts are carried, else
    /// the earlier step that makes the missing part. Nothing is combined that the kit can't use (no
    /// herbs without bandages), and nothing when a step it needs is under the floor, untrained, or
    /// its chance can't be read with a floor set.
    /// </summary>
    public static KitNext Next(KitRecipe r, IReadOnlyList<KitStack> pack, Func<int, KitSkill> skill, int floorPercent)
        => Next(r, r.Final, pack, skill, Math.Clamp(floorPercent, 0, 100), 0);

    private static KitNext Next(KitRecipe r, KitCombine step, IReadOnlyList<KitStack> pack, Func<int, KitSkill> skill, int floor, int depth)
    {
        if (Makeable(r, step, pack) <= 0)
            return KitNext.None("out of " + Missing(r, step, pack));

        var s = skill(step.SkillId);
        string skillName = SkillName(step.SkillId);
        if (s.Known && !s.Trained)
            return KitNext.None($"{skillName} isn't trained (the server refuses the combine)");
        int pct = s.Known ? Percent(s.Buffed, step.Difficulty) : -1;
        if (pct < 0 && floor > 0)
            return KitNext.None($"can't read your {skillName} skill to check the {floor}% floor (set Min chance to 0 to go without it)");
        if (pct >= 0 && pct < floor)
            return KitNext.None($"{step.Label}: {pct}% at {skillName} {s.Buffed} is under your {floor}% floor");

        int src = FirstStack(step.Source, pack), tgt = FirstStack(step.Target, pack);
        if (src != 0 && tgt != 0)
            return new KitNext(step, src, tgt, pct, string.Empty);

        // A part has to be made first.
        var missing = src == 0 ? step.Source : step.Target;
        var producer = depth < 8 ? r.Producer(missing) : null;
        if (producer == null) return KitNext.None("out of " + missing.Name);
        return Next(r, producer, pack, skill, floor, depth + 1);
    }

    /// <summary>The first item that runs out for a step (an ingredient you carry, not a part made on the way).</summary>
    private static string Missing(KitRecipe r, KitCombine step, IReadOnlyList<KitStack> pack, int depth = 0)
    {
        foreach (var part in new[] { step.Source, step.Target })
        {
            if (Available(r, part, pack, depth) > 0) continue;
            var producer = depth < 8 ? r.Producer(part) : null;
            return producer == null ? part.Name : Missing(r, producer, pack, depth + 1);
        }
        return step.Source.Name;
    }

    // ── Text ────────────────────────────────────────────────────────────────

    /// <summary>"Soft Bandages 3, Treated Hyssop 5, ..." (what the pack holds for a row).</summary>
    public static string PartsText(KitPlanRow row)
        => string.Join(", ", row.Parts.Select(p => $"{p.Part.Name} {p.Count}"));

    /// <summary>"Combine the herbs 99%, Pack the kit 95%" ("?" when the skill can't be read).</summary>
    public static string ChancesText(KitPlanRow row)
        => string.Join(", ", row.Chances.Select(c => $"{c.Step.Label} {(c.Percent >= 0 ? c.Percent + "%" : "?")}"));

    /// <summary>One chat line for a row (/ra kits).</summary>
    public static string Line(KitPlanRow row)
    {
        string make = row.CanMake == 1 ? "1 kit" : $"{row.CanMake} kits";
        string tail = row.Blocked.Length > 0 && row.CanMake > 0 ? $" - can't start: {row.Blocked}" : "";
        return $"{row.Recipe.Name}: {make} ({PartsText(row)}; chance {ChancesText(row)}){tail}";
    }

    /// <summary>The run's closing line.</summary>
    public static string Summary(string kitName, int made, int failed, IReadOnlyDictionary<string, int> lost, string why)
    {
        string madeText = made == 1 ? $"made 1 {kitName}" : $"made {made} {kitName}s";
        string failText = failed == 0 ? "no combine failed"
            : (failed == 1 ? "1 combine failed" : $"{failed} combines failed")
              + (lost.Count > 0 ? " (lost " + string.Join(", ", lost.Select(kv => $"{kv.Value} {kv.Key}")) + ")" : "");
        return $"{madeText}, {failText}. Stopped: {why}.";
    }
}
