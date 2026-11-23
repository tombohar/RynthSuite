using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// Make kits (MiniRemote/Crafting/KitPlanner.cs): the retail Plentiful Healing Kit chain from the ACE
// World Database (recipes 4022-4025), ACE's skill chance, and the planner: pack -> how many kits,
// and the next combine (the kit when its parts are carried, else the herbs; nothing the kit can't
// use; nothing under the floor, untrained, or unread with a floor set).
internal static class KitPlannerTests
{
    public static void Register(Runner r)
    {
        r.Add("kits: the retail chain (items, WCIDs, order, Healing, difficulties, result lines)", RetailData);
        r.Add("kits: ACE's skill chance and the prompt's rounded percent", ChanceFormula);
        r.Add("kits: what the pack makes (carried parts, herbs still to combine, several stacks)", Counting);
        r.Add("kits: the next combine - the kit first, then the herbs, never herbs with no bandages", NextOrder);
        r.Add("kits: the floor, an untrained skill and an unread skill stop it before any combine", Gates);
        r.Add("kits: name match when the WCID can't be read; a different WCID with the same name is not it", NameFallback);
        r.Add("kits: the plan rows and the chat lines", PlanText);
    }

    private const int Bandages = 1, Combined = 2, Hyssop = 3, Mandrake = 4, Kit = 5;

    private static KitStack S(int id, KitPart part, int count) => new(id, part.Wcid, part.Name, count);

    private static readonly KitRecipe R = KitPlanner.Retail[0];

    private static Func<int, KitSkill> Healing(int buffed, int training = 2)
        => id => id == KitPlanner.SkillHealing ? new KitSkill(true, buffed, training) : KitSkill.Unknown;

    private static readonly Func<int, KitSkill> Unread = _ => KitSkill.Unknown;

    private static void RetailData()
    {
        Check.Eq(KitPlanner.Retail.Length, 1, "one retail kit chain (no Plentiful mana or stamina kit in the data)");
        Check.Eq(R.Product.Wcid, 22449u, "Plentiful Healing Kit is WCID 22449");
        Check.Eq(R.Product.Name, "Plentiful Healing Kit", "its name");
        Check.Eq(R.Steps.Length, 2, "two combines");
        var herbs = R.Steps[0];
        Check.Eq(herbs.Source.Wcid, 22448u, "Treated Mandrake 22448");
        Check.Eq(herbs.Target.Wcid, 22447u, "on Treated Hyssop 22447");
        Check.Eq(herbs.Product.Wcid, 22446u, "makes Combined Hyssop and Mandrake 22446");
        Check.Eq(herbs.Difficulty, 100, "herbs: difficulty 100");
        var kit = R.Final;
        Check.Eq(kit.Source.Wcid, 22446u, "the combined herbs");
        Check.Eq(kit.Target.Wcid, 22445u, "on Soft Bandages 22445");
        Check.Eq(kit.Difficulty, 200, "kit: difficulty 200");
        Check.True(R.Steps.All(s => s.SkillId == KitPlanner.SkillHealing), "both use Healing (skill 21)");
        Check.True(R.Producer(KitPlanner.CombinedHerbs) == herbs, "the herbs step makes the combined herbs");
        Check.True(R.Producer(KitPlanner.SoftBandages) == null, "bandages are carried, not made");
        Check.Eq(string.Join(",", R.Ingredients().Select(p => p.Wcid)), "22448,22447,22446,22445", "every item the chain touches");
        Check.True(kit.SuccessText.StartsWith("You pack the bandages with the herbs.", StringComparison.Ordinal), "the kit's success line");
        Check.True(herbs.FailText == "You crumble the leaves and they become useless.", "the herbs' failure line");
    }

    private static void ChanceFormula()
    {
        Check.Eq(KitPlanner.Percent(200, 200), 50, "skill = difficulty: 50%");
        Check.Eq(KitPlanner.Percent(300, 200), 95, "100 over: 95%");
        Check.Eq(KitPlanner.Percent(250, 200), 82, "50 over: 82%");
        Check.Eq(KitPlanner.Percent(150, 200), 18, "50 under: 18%");
        Check.Eq(KitPlanner.Percent(400, 200), 100, "200 over rounds to 100%");
        Check.True(KitPlanner.Chance(400, 200) < 1.0, "but is never quite certain");
        Check.Eq(KitPlanner.Percent(0, 200), 0, "no skill: 0%");
        Check.True(Math.Abs(KitPlanner.Chance(230, 200) - (1 - 1 / (1 + Math.Exp(0.9)))) < 1e-12, "ACE's 1 - 1/(1 + e^(0.03 d))");
    }

    private static void Counting()
    {
        Check.Eq(KitPlanner.CanMake(R, new List<KitStack>()), 0, "empty pack: none");
        var carried = new List<KitStack> { S(Bandages, KitPlanner.SoftBandages, 5), S(Combined, KitPlanner.CombinedHerbs, 3) };
        Check.Eq(KitPlanner.CanMake(R, carried), 3, "3 combined herbs, 5 bandages: 3");
        var raw = new List<KitStack>
        {
            S(Bandages, KitPlanner.SoftBandages, 10), S(Hyssop, KitPlanner.TreatedHyssop, 4), S(Mandrake, KitPlanner.TreatedMandrake, 7),
        };
        Check.Eq(KitPlanner.CanMake(R, raw), 4, "hyssop 4, mandrake 7, bandages 10: 4 (hyssop runs out)");
        var mixed = new List<KitStack>(raw) { S(Combined, KitPlanner.CombinedHerbs, 2) };
        Check.Eq(KitPlanner.CanMake(R, mixed), 6, "plus 2 combined: 6");
        var fewBandages = new List<KitStack>(mixed) { };
        fewBandages[0] = S(Bandages, KitPlanner.SoftBandages, 3);
        Check.Eq(KitPlanner.CanMake(R, fewBandages), 3, "3 bandages cap it at 3");
        var twoStacks = new List<KitStack>
        {
            S(10, KitPlanner.SoftBandages, 2), S(11, KitPlanner.SoftBandages, 3), S(Combined, KitPlanner.CombinedHerbs, 9),
        };
        Check.Eq(KitPlanner.Count(KitPlanner.SoftBandages, twoStacks), 5, "two bandage stacks: 5");
        Check.Eq(KitPlanner.CanMake(R, twoStacks), 5, "so 5 kits");
        Check.Eq(KitPlanner.FirstStack(KitPlanner.SoftBandages, twoStacks), 10, "the first stack is used first");
    }

    private static void NextOrder()
    {
        var pack = new List<KitStack>
        {
            S(Bandages, KitPlanner.SoftBandages, 2), S(Combined, KitPlanner.CombinedHerbs, 1),
            S(Hyssop, KitPlanner.TreatedHyssop, 5), S(Mandrake, KitPlanner.TreatedMandrake, 5),
        };
        var n = KitPlanner.Next(R, pack, Healing(350), 90);
        Check.True(n.Step == R.Final, "combined herbs and bandages carried: the kit first");
        Check.Eq(n.SourceId, Combined, "the combined herbs...");
        Check.Eq(n.TargetId, Bandages, "...on the bandages");
        Check.Eq(n.Percent, 99, "at Healing 350 the kit is 99%");

        pack.RemoveAt(1);   // no combined herbs left
        n = KitPlanner.Next(R, pack, Healing(350), 90);
        Check.True(n.Step == R.Steps[0], "no combined herbs: combine the herbs");
        Check.Eq(n.SourceId, Mandrake, "mandrake...");
        Check.Eq(n.TargetId, Hyssop, "...on hyssop");

        pack.RemoveAt(0);   // and no bandages
        n = KitPlanner.Next(R, pack, Healing(350), 90);
        Check.True(n.Step == null, "no bandages: no herb combine either (nothing the kit can use)");
        Check.Eq(n.Why, "out of Soft Bandages", "says what ran out");

        var noHyssop = new List<KitStack> { S(Bandages, KitPlanner.SoftBandages, 4), S(Mandrake, KitPlanner.TreatedMandrake, 4) };
        n = KitPlanner.Next(R, noHyssop, Healing(350), 90);
        Check.Eq(n.Why, "out of Treated Hyssop", "the herb that ran out, not the combined herbs");
    }

    private static void Gates()
    {
        var pack = new List<KitStack>
        {
            S(Bandages, KitPlanner.SoftBandages, 3), S(Hyssop, KitPlanner.TreatedHyssop, 3), S(Mandrake, KitPlanner.TreatedMandrake, 3),
        };
        // Healing 250: herbs 99%, kit 82%.
        var n = KitPlanner.Next(R, pack, Healing(250), 90);
        Check.True(n.Step == null, "the kit step is under 90%: no combine, not even the herbs (before the kit could be tried)");
        Check.True(n.Why.Contains("82%") && n.Why.Contains("90% floor") && n.Why.Contains("Healing 250"), "says the chance, skill and floor: " + n.Why);
        n = KitPlanner.Next(R, pack, Healing(250), 80);
        Check.True(n.Step == R.Steps[0], "at an 80% floor it goes");
        n = KitPlanner.Next(R, pack, Healing(250), 0);
        Check.True(n.Step != null, "floor 0: any chance");

        n = KitPlanner.Next(R, pack, Healing(400, training: 1), 0);
        Check.True(n.Step == null && n.Why.Contains("isn't trained"), "untrained Healing: refused (the server would)");

        n = KitPlanner.Next(R, pack, Unread, 90);
        Check.True(n.Step == null && n.Why.Contains("can't read your Healing"), "skill unread with a floor: no guess");
        n = KitPlanner.Next(R, pack, Unread, 0);
        Check.True(n.Step != null && n.Percent == -1, "skill unread at floor 0: goes, chance unknown");

        Check.True(KitPlanner.Next(R, pack, Healing(300), 150).Step == null, "floor clamped to 100: 95% is under it");
    }

    private static void NameFallback()
    {
        var pack = new List<KitStack>
        {
            new(Bandages, 0, "Soft Bandages", 2), new(Combined, 0, "combined hyssop and mandrake", 2),
        };
        Check.Eq(KitPlanner.CanMake(R, pack), 2, "no WCID read: matched by name (any case)");
        var wrong = new List<KitStack> { new(Bandages, 99999, "Soft Bandages", 2), S(Combined, KitPlanner.CombinedHerbs, 2) };
        Check.Eq(KitPlanner.CanMake(R, wrong), 0, "a known WCID that isn't 22445 is not Soft Bandages, whatever its name");
    }

    private static void PlanText()
    {
        var pack = new List<KitStack>
        {
            S(Bandages, KitPlanner.SoftBandages, 3), S(Hyssop, KitPlanner.TreatedHyssop, 2), S(Mandrake, KitPlanner.TreatedMandrake, 2),
            S(Kit, KitPlanner.PlentifulHealingKit, 1),
        };
        var rows = KitPlanner.Plan(KitPlanner.Retail, pack, Healing(300), 90);
        Check.Eq(rows.Count, 1, "one row");
        var row = rows[0];
        Check.Eq(row.CanMake, 2, "2 kits");
        Check.Eq(row.Blocked, "", "can start");
        Check.Eq(KitPlanner.PartsText(row), "Treated Mandrake 2, Treated Hyssop 2, Combined Hyssop and Mandrake 0, Soft Bandages 3", "the parts");
        Check.Eq(KitPlanner.ChancesText(row), "Combine the herbs 100%, Pack the kit 95%", "the chances");
        Check.False(row.Chances.Any(c => c.UnderFloor), "none under 90%");
        Check.Eq(KitPlanner.Line(row),
            "Plentiful Healing Kit: 2 kits (Treated Mandrake 2, Treated Hyssop 2, Combined Hyssop and Mandrake 0, Soft Bandages 3; chance Combine the herbs 100%, Pack the kit 95%)",
            "the /ra kits line");

        rows = KitPlanner.Plan(KitPlanner.Retail, pack, Healing(250), 90);
        Check.True(rows[0].Chances[1].UnderFloor && !rows[0].Chances[0].UnderFloor, "at 250 the kit step is under the floor");
        Check.True(KitPlanner.Line(rows[0]).Contains(" - can't start: Pack the kit: 82%"), "and the line says so");

        var lost = new Dictionary<string, int> { ["Combined Hyssop and Mandrake"] = 1, ["Soft Bandages"] = 1 };
        Check.Eq(KitPlanner.Summary("Plentiful Healing Kit", 3, 1, lost, "out of Soft Bandages"),
            "made 3 Plentiful Healing Kits, 1 combine failed (lost 1 Combined Hyssop and Mandrake, 1 Soft Bandages). Stopped: out of Soft Bandages.",
            "the summary");
        Check.Eq(KitPlanner.Summary("Plentiful Healing Kit", 1, 0, new Dictionary<string, int>(), "made the 1 asked for"),
            "made 1 Plentiful Healing Kit, no combine failed. Stopped: made the 1 asked for.", "one kit, no failures");
    }
}
