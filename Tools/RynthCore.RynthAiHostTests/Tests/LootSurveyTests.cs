using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// A survey, not a check of one rule: real item types (bows and crossbows by element, wands,
// melee weapons, armor, jewelry, salvage of each material), built the way ACE makes them, run
// through the LootSnob profiles players use, with and without their identify data. It prints
// the rule each one gets (and writes a TSV when RYNTH_LOOT_SURVEY_OUT names a file), so a change
// to the evaluator shows which items it moves. The profiles are read from the install
// (C:\Games\RynthSuite\RynthAi\LootProfiles, read only); without them only the excerpt runs.
// The checks that matter are in LootRuleMatchTests; this one only checks that it ran.
internal static class LootSurveyTests
{
    private const uint Player = 0x50000A01;
    private const string ProfileDir = @"C:\Games\RynthSuite\RynthAi\LootProfiles";

    public static void Register(Runner r)
    {
        r.Add("loot survey: LootSnob profiles x real item types, with and without ID (prints the rule each gets)", Survey);
    }

    // CreateObject ints: what an item shows before its ID (RynthCore PwdLayout.IntStypes).
    private static readonly HashSet<uint> CreateInts = new() { 1, 4, 5, 6, 7, 9, 10, 11, 12, 16, 18, 19, 50, 51, 91, 92, 94, 95, 131, 133, 151, 152, 280 };

    internal sealed record SurveyItem(string Label, string Name, AcObjectClass Cls, Dictionary<int, int> Ints, Dictionary<int, double> Dbls, string[] Spells);

    private static int Spell(string name)
    {
        int id = SpellDatabase.GetIdByName(name);
        if (id == 0) throw new InvalidOperationException($"spell '{name}' is not in SpellData.txt");
        return id;
    }

    // ── ACE-like items ───────────────────────────────────────────────────────
    private const int ItemType = 1, Burden = 5, ValidLoc = 9, Value = 19, Damage = 44, DamageType = 45, WeaponSkill = 48,
        Workmanship = 105, Material = 131, WieldReq = 158, WieldSkill = 159, WieldDiff = 160, ElemBonus = 204,
        EquipSet = 265, WeaponType = 353, ArmorLevel = 28, Priority = 4, Tinkered = 171, Imbued = 179;
    private const int Variance = 22, DamageMod = 63, MeleeDef = 29, Offense = 62, MissileDef = 149, MagicDef = 150, ManaConv = 144, ElemVsMonster = 152;

    private static SurveyItem Launcher(string label, string name, int weaponType, int wield, int elem, double mod, double def, int material, int work, int dmgType, params string[] spells)
        => new(label, name, AcObjectClass.MissileWeapon,
            new() { [ItemType] = 0x100, [ValidLoc] = 0x00400000, [Value] = 9000, [Burden] = 500, [Material] = material, [Workmanship] = work,
                    [WeaponType] = weaponType, [WieldReq] = 2, [WieldSkill] = 47, [WieldDiff] = wield, [ElemBonus] = elem,
                    [DamageType] = dmgType, [WeaponSkill] = 47 },
            new() { [DamageMod] = mod, [MeleeDef] = def, [MissileDef] = 1.0, [MagicDef] = 1.0 },
            spells);

    private static SurveyItem Melee(string label, string name, int weaponType, int skill, int damage, double variance, double def, double off, int material, int work, params string[] spells)
        => new(label, name, AcObjectClass.MeleeWeapon,
            new() { [ItemType] = 0x1, [ValidLoc] = 0x00100000, [Value] = 9000, [Burden] = 400, [Material] = material, [Workmanship] = work,
                    [WeaponType] = weaponType, [WieldReq] = 2, [WieldSkill] = skill, [WieldDiff] = 430, [Damage] = damage, [DamageType] = 1, [WeaponSkill] = skill },
            new() { [Variance] = variance, [MeleeDef] = def, [Offense] = off, [MissileDef] = 1.0, [MagicDef] = 1.0 },
            spells);

    private static SurveyItem Caster(string label, string name, int wield, double elemVsMonster, double def, double manaConv, int material, int work, params string[] spells)
        => new(label, name, AcObjectClass.WandStaffOrb,
            new() { [ItemType] = 0x8000, [ValidLoc] = 0x01000000, [Value] = 9000, [Burden] = 50, [Material] = material, [Workmanship] = work,
                    [WieldReq] = 2, [WieldSkill] = 34, [WieldDiff] = wield },
            new() { [ElemVsMonster] = elemVsMonster, [MeleeDef] = def, [ManaConv] = manaConv, [MagicDef] = 1.0 },
            spells);

    private static SurveyItem Armor(string label, string name, int al, int set, int material, int work, params string[] spells)
        => new(label, name, AcObjectClass.Armor,
            new() { [ItemType] = 0x2, [ValidLoc] = 0x00000200, [Priority] = 0x400, [Value] = 9000, [Burden] = 700, [Material] = material,
                    [Workmanship] = work, [ArmorLevel] = al, [EquipSet] = set },
            new(), spells);

    private static SurveyItem Jewel(string label, string name, int material, int work, params string[] spells)
        => new(label, name, AcObjectClass.Jewelry,
            new() { [ItemType] = 0x8, [ValidLoc] = 0x00040000, [Value] = 7000, [Burden] = 30, [Material] = material, [Workmanship] = work },
            new(), spells);

    internal static List<SurveyItem> Items()
    {
        const int Gold = 60, Iron = 61, Steel = 64, Silver = 63, Mahogany = 74, Oak = 75, Pine = 76, Ebony = 73, Teak = 77, Brass = 57,
                  Granite = 67, Satin = 5;
        const int Slash = 1, Pierce = 2, Bludge = 4, Cold = 8, Fire = 16, Acid = 32, Elec = 64;
        var items = new List<SurveyItem>
        {
            // Crossbows (WeaponType 9): LootSnob's missile damage = mod% / 3 + elemental + cantrip.
            Launcher("xbow 385 +22 mod 2.62 Legendary Blood Thirst (OD +9)", "Blunt Crossbow", 9, 385, 22, 2.62, 1.15, Gold, 6, Bludge, "Legendary Blood Thirst"),
            Launcher("xbow 385 +22 mod 2.65 (Max 385)", "Fire Crossbow", 9, 385, 22, 2.65, 1.12, Iron, 5, Fire),
            Launcher("xbow 385 +22 mod 2.62 no cantrip (76, under Max 385)", "Blunt Crossbow", 9, 385, 22, 2.62, 1.15, Gold, 6, Bludge),
            Launcher("xbow 385 +21 mod 2.65 Epic Blood Thirst (OD +6)", "Acid Compound Crossbow", 9, 385, 21, 2.65, 1.10, Mahogany, 7, Acid, "Epic Blood Thirst"),
            Launcher("xbow 335 +9 mod 2.55 (a '+9' elemental)", "Electric Crossbow", 9, 335, 9, 2.55, 1.10, Gold, 5, Elec),
            Launcher("xbow 375 +19 MD 1.20 Legendary Defender (29MD)", "Frost Crossbow", 9, 375, 19, 2.60, 1.20, Steel, 6, Cold, "Legendary Defender"),
            Launcher("xbow 375 +19 MD 1.22 (22MD)", "Slashing Crossbow", 9, 375, 19, 2.60, 1.22, Silver, 6, Slash),
            // Bows (8)
            Launcher("bow 385 +22 mod 2.40 (Max 385)", "Fire Compound Bow", 8, 385, 22, 2.40, 1.10, Oak, 5, Fire),
            Launcher("bow 385 +22 mod 2.40 Legendary Blood Thirst (OD +10)", "Piercing Bow", 8, 385, 22, 2.40, 1.10, Pine, 5, Pierce, "Legendary Blood Thirst"),
            Launcher("bow 375 +19 mod 2.37 (64.7, rules off)", "Electric Bow", 8, 375, 19, 2.37, 1.10, Ebony, 6, Elec),
            Launcher("bow 360 +16 mod 2.35 MD 1.21 (21MD)", "Blunt Bow", 8, 360, 16, 2.35, 1.21, Teak, 5, Bludge),
            Launcher("bow 335 +9 mod 2.30 Iron", "Acid Bow", 8, 335, 9, 2.30, 1.05, Iron, 5, Acid),
            // Atlatls (10)
            Launcher("atlatl 385 +22 mod 2.60 Epic Blood Thirst (OD +7)", "Electric Atlatl", 10, 385, 22, 2.60, 1.10, Mahogany, 5, Elec, "Epic Blood Thirst"),
            Launcher("atlatl 385 +22 mod 2.60 (75.3, Max 385 off)", "Fire Atlatl", 10, 385, 22, 2.60, 1.10, Mahogany, 5, Fire),
            Launcher("atlatl 360 +16 MD 1.29 (29MD)", "Blunt Atlatl", 10, 360, 16, 2.45, 1.29, Gold, 6, Bludge),
            // Wands
            Caster("void wand 1.18 Legendary Spirit Thirst (OD +7)", "Nether Baton", 385, 1.18, 1.10, 0.05, Silver, 6, "Legendary Spirit Thirst"),
            Caster("void wand 385 1.18 (Max 385)", "Nether Sceptre", 385, 1.18, 1.10, 0.05, Silver, 6),
            Caster("war sceptre 1.18 MD 1.15 Major Defender (18pvm + 20md)", "Sceptre", 385, 1.18, 1.15, 0.05, Brass, 5, "Major Defender"),
            Caster("war staff 1.15 MD 1.20 (no rule)", "Staff", 375, 1.12, 1.18, 0.05, Teak, 5),
            Caster("orb no wield MD 1.21 Minor Defender (24MD)", "Orb", 0, 1.0, 1.21, 0.10, Granite, 5, "Minor Defender"),
            // Melee (heavy axe 44, light sword 45, finesse dagger 46)
            Melee("heavy axe 74 (Max 430)", "Silifi", 3, 44, 74, 0.45, 1.12, 1.12, Iron, 6),
            Melee("heavy axe 74 Legendary Blood Thirst (OD +10)", "Lugian Axe", 3, 44, 74, 0.45, 1.12, 1.12, Iron, 6, "Legendary Blood Thirst"),
            Melee("heavy axe 72 MD 1.26 Minor Defender (MD +8)", "War Axe", 3, 44, 66, 0.45, 1.26, 1.10, Steel, 6, "Minor Defender"),
            Melee("heavy mace 80 var .25 def/atk 1.17 (430 Insane)", "Mace", 4, 44, 80, 0.25, 1.17, 1.17, Brass, 7),
            Melee("light sword 68 (OD +10)", "Broad Sword", 2, 45, 68, 0.40, 1.10, 1.10, Steel, 5),
            Melee("light sword 60 Epic Blood Thirst (67, OD +9)", "Shamshir", 2, 45, 60, 0.40, 1.10, 1.10, Steel, 5, "Epic Blood Thirst"),
            Melee("finesse dagger 59 (OD >= +1)", "Poniard", 6, 46, 59, 0.40, 1.10, 1.10, Gold, 5),
            Melee("2h spear 57 (OD +9)", "Assagai", 5, 41, 57, 0.40, 1.10, 1.10, Iron, 5),
            // Armor and jewelry
            Armor("Archer's set coat Legendary Missile Weapon + Epic Impen", "Amuli Coat", 600, 15, Satin, 6, "Legendary Missile Weapon Aptitude", "Epic Impenetrability"),
            Armor("plain breastplate AL 400 Steel w5", "Breastplate", 400, 0, Steel, 5),
            Armor("plain coat AL 300 Gold w4", "Chainmail Coat", 300, 0, Gold, 4),
            Jewel("ring Legendary Strength", "Ring", Silver, 5, "Legendary Strength"),
            Jewel("necklace no spells Gold w4", "Necklace", Gold, 4),
        };
        // Salvage of each material: a plain armor piece, workmanship 5.
        (string Name, int Id)[] mats =
        {
            ("Alabaster", 66), ("Armoredillo Hide", 53), ("Bronze", 58), ("Ceramic", 1), ("Marble", 68), ("Peridot", 34), ("Reed Shark Hide", 55),
            ("Steel", 64), ("Wool", 8), ("Yellow Topaz", 49), ("Zircon", 50), ("Amber", 11), ("Copper", 59), ("Diamond", 20), ("Ebony", 73),
            ("Gold", 60), ("Gromnie Hide", 54), ("Linen", 4), ("Moonstone", 31), ("Pine", 76), ("Porcelain", 2), ("Pyreal", 62), ("Ruby", 38),
            ("Sapphire", 39), ("Satin", 5), ("Silver", 63), ("Teak", 77), ("Agate", 10), ("Azurite", 14), ("Black Opal", 16), ("Bloodstone", 17),
            ("Carnelian", 18), ("Citrine", 19), ("Fire Opal", 22), ("Green Garnet", 23), ("Hematite", 25), ("Lapis Lazuli", 28), ("Lavender Jade", 29),
            ("Malachite", 30), ("Opal", 33), ("Red Jade", 36), ("Rose Quartz", 37), ("Smokey Quartz", 40), ("Sunstone", 41), ("Aquamarine", 13),
            ("Black Garnet", 15), ("Brass", 57), ("Emerald", 21), ("Granite", 67), ("Imperial Topaz", 26), ("Iron", 61), ("Jet", 27),
            ("Mahogany", 74), ("Oak", 75), ("Red Garnet", 35), ("Velvet", 7), ("White Sapphire", 47), ("Ivory", 51), ("Leather", 52),
            ("Sandstone", 70), ("Silk", 6), ("Amethyst", 12), ("Green Jade", 24), ("Obsidian", 69), ("Onyx", 32), ("Serpentine", 71),
            ("Tiger Eye", 42), ("Tourmaline", 43), ("Turquoise", 44), ("White Jade", 45), ("White Quartz", 46), ("Yellow Garnet", 48),
        };
        foreach (var (n, id) in mats)
            items.Add(Armor($"salvage: {n} w5 (plain armor)", "Leather Leggings", 150, 0, id, 5));
        return items;
    }

    // ── Running one item ─────────────────────────────────────────────────────

    private static readonly MethodInfo? DecideMethod = typeof(VTankLootEvaluator).GetMethod("Decide");

    /// <summary>The rule the looter gives the item: with its ID, or (hasId false) from what it shows before one.</summary>
    internal static string Classify(VTankLootProfile profile, SurveyItem s, bool hasId)
    {
        var (item, ctx) = Build(s, hasId);

        int index;
        string verdict;
        if (DecideMethod != null)
        {
            object?[] args = { profile, item.Wo, ctx, hasId, -1 };
            verdict = DecideMethod.Invoke(null, args)!.ToString()!;
            index = (int)args[4]!;
        }
        else
        {
            // Before 2026-10-31: no wait for the ID past the timeout, the rules judged what was there.
            VTankLootRule? hit = VTankLootEvaluator.FirstMatch(profile, item.Wo, ctx, out index);
            verdict = hit != null ? "Match" : "NoMatch";
        }
        if (verdict == "NoMatch" || index < 0) return "leave";
        string name = profile.Rules[index].Name.Trim();
        return verdict == "NeedsId" ? $"hold for ID ({name})" : $"{profile.Rules[index].Action}: {name}";
    }

    /// <summary>The survey item as a test item: with its ID, or only what the CreateObject shows.</summary>
    private static (Item Item, VTankLootContext Ctx) Build(SurveyItem s, bool hasId)
    {
        var item = new Item(s.Name, s.Cls);
        foreach (var (k, v) in s.Ints)
            if (hasId || CreateInts.Contains(unchecked((uint)k))) item.Int(k, v);
        if (hasId)
            foreach (var (k, v) in s.Dbls) item.Dbl(k, v);
        uint id = unchecked((uint)item.Wo.Id);
        var spellIds = new List<uint>();
        if (hasId) foreach (string sp in s.Spells) spellIds.Add((uint)Spell(sp));
        FakeHost.SpellIds[id] = spellIds.ToArray();
        return (item, new VTankLootContext(FakeHost.Create(), Player));
    }

    internal static VTankLootProfile WithSalvageOn(VTankLootProfile p)
    {
        foreach (var r in p.Rules)
            if (r.Action == VTankLootAction.Salvage)
                r.Conditions.RemoveAll(c => c.NodeType == VTankNodeTypes.DisabledRule);
        return p;
    }

    private static void Survey()
    {
        FakeHost.Reset();
        var profiles = new List<(string Name, VTankLootProfile Profile)>
        {
            ("lootsnob-excerpt (fixture)", VTankLootParser.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lootsnob-excerpt.utl"))),
        };
        string snob = Path.Combine(ProfileDir, "LootSnobV4.utl"), cq = Path.Combine(ProfileDir, "CQ_Lootsnob_lite.utl");
        if (File.Exists(snob))
        {
            profiles.Add(("LootSnobV4", VTankLootParser.Load(snob)));
            profiles.Add(("LootSnobV4 salvage on", WithSalvageOn(VTankLootParser.Load(snob))));
        }
        if (File.Exists(cq))
        {
            profiles.Add(("CQ_Lootsnob_lite", VTankLootParser.Load(cq)));
            profiles.Add(("CQ_Lootsnob_lite salvage on", WithSalvageOn(VTankLootParser.Load(cq))));
        }

        var tsv = new StringBuilder("profile\titem\tname\twith ID\twithout ID\n");
        foreach (var (pname, profile) in profiles)
        {
            Console.WriteLine($"  -- {pname} ({profile.Rules.Count} rules)");
            foreach (SurveyItem s in Items())
            {
                string withId = Classify(profile, s, hasId: true);
                string noId = Classify(profile, s, hasId: false);
                tsv.Append(pname).Append('\t').Append(s.Label).Append('\t').Append(s.Name).Append('\t').Append(withId).Append('\t').Append(noId).Append('\n');
                if (!s.Label.StartsWith("salvage:", StringComparison.Ordinal) || withId != "leave")
                    Console.WriteLine($"     {s.Label,-62} ID: {withId,-40} no ID: {noId}");
            }
        }
        // What /ra loot why says for the reported crossbow without its cantrip (76: Gold salvage).
        var (whyProfileName, whyProfile) = profiles[profiles.Count > 2 ? 2 : 0];
        SurveyItem plain = Items().Find(i => i.Label.StartsWith("xbow 385 +22 mod 2.62 no cantrip", StringComparison.Ordinal))!;
        var (whyItem, whyCtx) = Build(plain, hasId: true);
        LootWhy.Result why = LootWhy.Explain(whyProfile, whyItem.Wo, whyCtx, hasIdData: true);
        Console.WriteLine($"  -- /ra loot why, {whyProfileName}, '{plain.Label}': {why.Verdict} rule {why.Index + 1} '{why.RuleName}' [{why.Action}], {why.NearMisses.Count} came close:");
        foreach (var m in why.NearMisses)
            Console.WriteLine($"     #{m.Index + 1} '{m.RuleName}': {m.Failed}");

        string? outPath = Environment.GetEnvironmentVariable("RYNTH_LOOT_SURVEY_OUT");
        if (!string.IsNullOrWhiteSpace(outPath)) File.WriteAllText(outPath, tsv.ToString());
        Check.True(profiles[0].Profile.Rules.Count > 100, "the excerpt fixture loads");
    }
}
