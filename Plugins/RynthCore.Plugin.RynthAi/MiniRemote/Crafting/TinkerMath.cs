// TinkerMath.cs - Retail tinkering success chance, as ACE computes it
// (RecipeManager.GetTinkerChance, GetMaterialMod, TinkeringDifficulty; SkillCheck.GetSkillChance).
//
//   difficulty = floor((salvageMod*5 + itemWork*salvageMod*2 - toolWork*wkMod*salvageMod/5) * attemptMod)
//   chance     = 1 - 1 / (1 + e^(0.03 * (skill - difficulty)))      imbue: chance / 3
//
// toolWork is the bag's average workmanship (ItemWorkmanship / NumItemsInMaterial), wkMod is 2
// when the bag is at least as good as the item, attemptMod grows with the item's tinker count.
// Foolproof bags always succeed. Server extras (ILT recipes, augmentation, charms) are not
// modelled: the server's confirmation percent is the final word (ConfirmationResponder).
using System;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

internal static class TinkerMath
{
    /// <summary>Difficulty multiplier by how many tinks the item already has (index 0-9).</summary>
    private static readonly double[] AttemptMod = { 1.0, 1.1, 1.3, 1.6, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5 };

    /// <summary>Salvage difficulty factor (GetMaterialMod).</summary>
    public static double SalvageMod(int material) => material switch
    {
        TinkerCatalog.Gold or TinkerCatalog.Oak => 10.0,
        TinkerCatalog.Alabaster or TinkerCatalog.Armoredillo or TinkerCatalog.Brass or TinkerCatalog.Bronze
            or TinkerCatalog.Ceramic or TinkerCatalog.Granite or TinkerCatalog.Linen or TinkerCatalog.Marble
            or TinkerCatalog.Moonstone or TinkerCatalog.Opal or TinkerCatalog.Pine or TinkerCatalog.Reedshark
            or TinkerCatalog.Velvet or TinkerCatalog.Wool => 11.0,
        TinkerCatalog.Ebony or TinkerCatalog.GreenGarnet or TinkerCatalog.Iron or TinkerCatalog.Mahogany
            or TinkerCatalog.Porcelain or TinkerCatalog.Satin or TinkerCatalog.Steel or TinkerCatalog.Teak => 12.0,
        TinkerCatalog.Bloodstone or TinkerCatalog.Carnelian or TinkerCatalog.Citrine or TinkerCatalog.Hematite
            or TinkerCatalog.LavenderJade or TinkerCatalog.Malachite or TinkerCatalog.RedJade or TinkerCatalog.RoseQuartz => 25.0,
        _ => 20.0,
    };

    /// <summary>The recipe difficulty for one attempt (an item at 10 tinks can't be tinkered: int.MaxValue).</summary>
    public static int Difficulty(int material, double itemWorkmanship, double toolWorkmanship, int tinks)
    {
        if (tinks < 0) tinks = 0;
        if (tinks >= AttemptMod.Length) return int.MaxValue;
        double salvageMod = SalvageMod(material);
        double wkMod = toolWorkmanship >= itemWorkmanship ? 2.0 : 1.0;
        return (int)Math.Floor(((salvageMod * 5.0) + (itemWorkmanship * salvageMod * 2.0)
                                - (toolWorkmanship * wkMod * salvageMod / 5.0)) * AttemptMod[tinks]);
    }

    /// <summary>SkillCheck.GetSkillChance: the logistic curve AC uses for every skill check.</summary>
    public static double SkillChance(int skill, int difficulty)
    {
        if (difficulty == int.MaxValue) return 0.0;
        double chance = 1.0 - (1.0 / (1.0 + Math.Exp(0.03 * (skill - difficulty))));
        return Math.Clamp(chance, 0.0, 1.0);
    }

    /// <summary>
    /// The retail chance (0-1) of one application: <paramref name="skill"/> is the buffed
    /// tinkering skill, <paramref name="imbue"/> divides by 3, <paramref name="foolproof"/> is 1.
    /// </summary>
    public static double Chance(int material, int skill, double itemWorkmanship, double toolWorkmanship,
                                int tinks, bool imbue, bool foolproof)
    {
        if (foolproof) return 1.0;
        double c = SkillChance(skill, Difficulty(material, itemWorkmanship, toolWorkmanship, tinks));
        return imbue ? c / 3.0 : c;
    }

    /// <summary>A bag's average workmanship: ItemWorkmanship over NumItemsInMaterial.</summary>
    public static double BagWorkmanship(int itemWorkmanship, int numItemsInMaterial)
        => numItemsInMaterial > 0 ? itemWorkmanship / (double)numItemsInMaterial : itemWorkmanship;

    /// <summary>Whole percent the way the server's dialog rounds it ("You determine that you have a N percent chance").</summary>
    public static int Percent(double chance) => (int)Math.Round(Math.Clamp(chance, 0.0, 1.0) * 100.0, MidpointRounding.AwayFromZero);
}
