// TinkerCatalog.cs - The salvage each Crafting tinker tab can apply.
//
// Retail: ACE's stock tinkering (RecipeManager Tinkering_ModifyItem): armor protections and
// imbues, weapon tinks and imbues, caster tinks, jewelry cantrip gems, item value / burden /
// mana / retain. Every retail tinker (imbues too) uses one of the item's 10 tinker slots and the
// success chance follows RecipeManager.GetTinkerChance (TinkerMath).
//
// Server: the connected server's own recipes come from the plugin that owns it (its
// "RynthServer.Profile" JSON's "salvage" list, read by FromServerPlugin): a server plugin, or
// RynthILT for Infinite Leaftide. Nothing server-specific lives here. Their chances are the
// server's: the Crafting runner relies on the confirmation's percent.
// The player adds their own rows per world (CraftingTabState.CustomByWorld).
using System;
using System.Collections.Generic;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

/// <summary>A Crafting tinker tab.</summary>
internal enum TinkerBench
{
    Armor,
    Melee,
    Missile,
    Caster,
    Jewelry,
    Items,
    /// <summary>Trinkets (compass, goggles, scarab...): server salvage, often tier-prepared.</summary>
    Trinkets,
}

/// <summary>Where a material's recipe comes from.</summary>
internal enum TinkerOrigin
{
    /// <summary>Stock ACE (retail) tinkering.</summary>
    Retail,
    /// <summary>A row the player added for this server.</summary>
    Custom,
    /// <summary>A row the server's own plugin defines (its profile's "salvage" list, ServerPackInfo.Salvage).</summary>
    Plugin,
}

/// <summary>How often a material can go on one item.</summary>
internal enum TinkerKind
{
    /// <summary>Repeatable stat tink (count = how many times).</summary>
    Tink,
    /// <summary>One imbue per item (ImbuedEffect): a second imbue of any kind is refused.</summary>
    Imbue,
    /// <summary>One application (retain, heritage, a server's flag recipes...).</summary>
    Once,
}

/// <summary>One salvage a tab can apply.</summary>
/// <param name="Id">MaterialType (131); <see cref="TinkerCatalog.RendId"/> = the weapon's damage-type rend.</param>
/// <param name="UsesTinkSlot">Counts toward the 10-tink cap.</param>
/// <param name="AppliedInt">An int property of the item: the material is on it when it reads 2 (0 = none).</param>
/// <param name="NameMatch">The bag must have this in its name (server bags that share a material id).</param>
/// <param name="Order">Apply order (lower first).</param>
/// <param name="Prepared">The bag must first be prepared on tier Ammunition: its name then carries the tier.</param>
/// <param name="AfterCap">Goes on last, and only once the item's tink slots are full.</param>
/// <param name="StackGroup">Rows with the same group share <paramref name="StackCap"/> per item ("" = none).</param>
/// <param name="StackCap">The group's combined cap per item.</param>
internal sealed record TinkerMaterial(
    int Id, string Name, TinkerOrigin Origin, TinkerKind Kind, string Effect, int Order,
    bool UsesTinkSlot = true, int AppliedInt = 0, string NameMatch = "", int Skill = 0, bool Prepared = false,
    bool AfterCap = false, string StackGroup = "", int StackCap = 0);

internal static class TinkerCatalog
{
    /// <summary>Pseudo material: the rend gem that matches the weapon's damage type.</summary>
    public const int RendId = -1;

    // Tinkering skills (Skill enum).
    public const int SkillItem = 18;
    public const int SkillWeapon = 28;
    public const int SkillArmor = 29;
    public const int SkillMagicItem = 30;

    // Material ids used by the rules below (MaterialType).
    public const int Ceramic = 1, Porcelain = 2, Linen = 4, Satin = 5, Silk = 6, Velvet = 7, Wool = 8;
    public const int Agate = 10, Aquamarine = 13, Azurite = 14, BlackGarnet = 15, BlackOpal = 16, Bloodstone = 17,
        Carnelian = 18, Citrine = 19, Emerald = 21, FireOpal = 22, GreenGarnet = 23, GreenJade = 24, Hematite = 25,
        ImperialTopaz = 26, Jet = 27, LapisLazuli = 28, LavenderJade = 29, Malachite = 30, Moonstone = 31, Onyx = 32,
        Opal = 33, Peridot = 34, RedGarnet = 35, RedJade = 36, RoseQuartz = 37, SmokeyQuartz = 40, Sunstone = 41,
        TigerEye = 42, Tourmaline = 43, Turquoise = 44, WhiteJade = 45, WhiteQuartz = 46, WhiteSapphire = 47,
        YellowGarnet = 48, YellowTopaz = 49, Zircon = 50;
    public const int Ivory = 51, Leather = 52, Armoredillo = 53, Reedshark = 55, Brass = 57, Bronze = 58, Copper = 59,
        Gold = 60, Iron = 61, Silver = 63, Steel = 64, Alabaster = 66, Granite = 67, Marble = 68, Sandstone = 70,
        Serpentine = 71, Ebony = 73, Mahogany = 74, Oak = 75, Pine = 76, Teak = 77;

    public const int Diamond = 20, Ruby = 38, GromnieHide = 54, Pyreal = 62;

    /// <summary>Prepared salvage tiers: the word in the Ammunition's and the prepared bag's name.</summary>
    public static readonly string[] TrinketTiers = { "Minor", "Moderate", "Major" };

    /// <summary>The ordinary tinker slots on an item.</summary>
    public const int MaxTinks = 10;

    private static readonly Dictionary<TinkerBench, TinkerMaterial[]> Retail = new()
    {
        [TinkerBench.Armor] = new TinkerMaterial[]
        {
            new(Peridot, "Peridot", TinkerOrigin.Retail, TinkerKind.Imbue, "Melee defense imbue", 105, Skill: SkillArmor),
            new(YellowTopaz, "Yellow Topaz", TinkerOrigin.Retail, TinkerKind.Imbue, "Missile defense imbue", 106, Skill: SkillArmor),
            new(Zircon, "Zircon", TinkerOrigin.Retail, TinkerKind.Imbue, "Magic defense imbue", 107, Skill: SkillArmor),
            new(Steel, "Steel", TinkerOrigin.Retail, TinkerKind.Tink, "+20 armor level", 300, Skill: SkillArmor),
            new(Alabaster, "Alabaster", TinkerOrigin.Retail, TinkerKind.Tink, "+0.2 pierce protection", 310, Skill: SkillArmor),
            new(Bronze, "Bronze", TinkerOrigin.Retail, TinkerKind.Tink, "+0.2 slash protection", 311, Skill: SkillArmor),
            new(Marble, "Marble", TinkerOrigin.Retail, TinkerKind.Tink, "+0.2 bludgeon protection", 312, Skill: SkillArmor),
            new(Armoredillo, "Armoredillo Hide", TinkerOrigin.Retail, TinkerKind.Tink, "+0.4 acid protection", 313, Skill: SkillArmor),
            new(Ceramic, "Ceramic", TinkerOrigin.Retail, TinkerKind.Tink, "+0.4 fire protection", 314, Skill: SkillArmor),
            new(Wool, "Wool", TinkerOrigin.Retail, TinkerKind.Tink, "+0.4 cold protection", 315, Skill: SkillArmor),
            new(Reedshark, "Reedshark Hide", TinkerOrigin.Retail, TinkerKind.Tink, "+0.4 lightning protection", 316, Skill: SkillArmor),
            new(Copper, "Copper", TinkerOrigin.Retail, TinkerKind.Once, "Activation: missile defense to melee defense", 380, Skill: SkillItem),
            new(Silver, "Silver", TinkerOrigin.Retail, TinkerKind.Once, "Activation: melee defense to missile defense", 381, Skill: SkillItem),
        },
        [TinkerBench.Melee] = new TinkerMaterial[]
        {
            new(BlackOpal, "Black Opal", TinkerOrigin.Retail, TinkerKind.Imbue, "Critical Strike imbue", 101, Skill: SkillWeapon),
            new(FireOpal, "Fire Opal", TinkerOrigin.Retail, TinkerKind.Imbue, "Crippling Blow imbue", 102, Skill: SkillWeapon),
            new(Sunstone, "Sunstone", TinkerOrigin.Retail, TinkerKind.Imbue, "Armor Rending imbue", 103, Skill: SkillWeapon),
            new(RendId, "Damage-type rend", TinkerOrigin.Retail, TinkerKind.Imbue, "Rend matching the weapon's damage type", 110, Skill: SkillWeapon),
            new(Brass, "Brass", TinkerOrigin.Retail, TinkerKind.Tink, "+1% melee defense", 170, Skill: SkillWeapon),
            new(Granite, "Granite", TinkerOrigin.Retail, TinkerKind.Tink, "-20% damage variance", 180, Skill: SkillWeapon),
            new(Iron, "Iron", TinkerOrigin.Retail, TinkerKind.Tink, "+1 maximum damage", 190, Skill: SkillWeapon),
            new(Oak, "Oak", TinkerOrigin.Retail, TinkerKind.Tink, "-50 weapon speed", 200, Skill: SkillWeapon),
            new(Velvet, "Velvet", TinkerOrigin.Retail, TinkerKind.Tink, "+1% attack", 210, Skill: SkillWeapon),
        },
        [TinkerBench.Missile] = new TinkerMaterial[]
        {
            new(BlackOpal, "Black Opal", TinkerOrigin.Retail, TinkerKind.Imbue, "Critical Strike imbue", 101, Skill: SkillWeapon),
            new(FireOpal, "Fire Opal", TinkerOrigin.Retail, TinkerKind.Imbue, "Crippling Blow imbue", 102, Skill: SkillWeapon),
            new(Sunstone, "Sunstone", TinkerOrigin.Retail, TinkerKind.Imbue, "Armor Rending imbue", 103, Skill: SkillWeapon),
            new(RendId, "Damage-type rend", TinkerOrigin.Retail, TinkerKind.Imbue, "Rend matching the weapon's damage type", 110, Skill: SkillWeapon),
            new(Brass, "Brass", TinkerOrigin.Retail, TinkerKind.Tink, "+1% melee defense", 170, Skill: SkillWeapon),
            new(Oak, "Oak", TinkerOrigin.Retail, TinkerKind.Tink, "-50 weapon speed", 200, Skill: SkillWeapon),
            new(Mahogany, "Mahogany", TinkerOrigin.Retail, TinkerKind.Tink, "+4% damage modifier", 220, Skill: SkillWeapon),
        },
        [TinkerBench.Caster] = new TinkerMaterial[]
        {
            new(BlackOpal, "Black Opal", TinkerOrigin.Retail, TinkerKind.Imbue, "Critical Strike imbue", 101, Skill: SkillMagicItem),
            new(FireOpal, "Fire Opal", TinkerOrigin.Retail, TinkerKind.Imbue, "Crippling Blow imbue", 102, Skill: SkillMagicItem),
            new(Sunstone, "Sunstone", TinkerOrigin.Retail, TinkerKind.Imbue, "Armor Rending imbue", 103, Skill: SkillMagicItem),
            new(RendId, "Element rend", TinkerOrigin.Retail, TinkerKind.Imbue, "Rend matching the caster's element", 110, Skill: SkillMagicItem),
            new(GreenGarnet, "Green Garnet", TinkerOrigin.Retail, TinkerKind.Tink, "+1% elemental damage", 150, Skill: SkillMagicItem),
            new(Opal, "Opal", TinkerOrigin.Retail, TinkerKind.Tink, "+1% mana conversion", 160, Skill: SkillMagicItem),
            new(Brass, "Brass", TinkerOrigin.Retail, TinkerKind.Tink, "+1% melee defense", 170, Skill: SkillWeapon),
        },
        [TinkerBench.Jewelry] = new TinkerMaterial[]
        {
            new(Agate, "Agate", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Focus", 120, Skill: SkillMagicItem),
            new(Azurite, "Azurite", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Wizard's Intellect", 121, Skill: SkillMagicItem),
            new(Bloodstone, "Bloodstone", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Endurance", 122, Skill: SkillMagicItem),
            new(Carnelian, "Carnelian", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Strength", 123, Skill: SkillMagicItem),
            new(Citrine, "Citrine", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Stamina Gain", 124, Skill: SkillMagicItem),
            new(Hematite, "Hematite", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Warrior's Vitality", 125, Skill: SkillMagicItem),
            new(LapisLazuli, "Lapis Lazuli", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Willpower", 126, Skill: SkillMagicItem),
            new(LavenderJade, "Lavender Jade", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Mana Gain", 127, Skill: SkillMagicItem),
            new(Malachite, "Malachite", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Warrior's Vigor", 128, Skill: SkillMagicItem),
            new(RedJade, "Red Jade", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Health Gain", 129, Skill: SkillMagicItem),
            new(RoseQuartz, "Rose Quartz", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Quickness", 130, Skill: SkillMagicItem),
            new(SmokeyQuartz, "Smokey Quartz", TinkerOrigin.Retail, TinkerKind.Imbue, "Cantrip: Coordination", 131, Skill: SkillMagicItem),
        },
        [TinkerBench.Items] = new TinkerMaterial[]
        {
            new(Gold, "Gold", TinkerOrigin.Retail, TinkerKind.Tink, "+25% value", 400, Skill: SkillItem),
            new(Pine, "Pine", TinkerOrigin.Retail, TinkerKind.Tink, "-25% value", 410, Skill: SkillItem),
            new(Linen, "Linen", TinkerOrigin.Retail, TinkerKind.Tink, "-25% burden", 420, Skill: SkillItem),
            new(Moonstone, "Moonstone", TinkerOrigin.Retail, TinkerKind.Tink, "+500 maximum mana", 430, Skill: SkillItem),
            new(Silk, "Silk", TinkerOrigin.Retail, TinkerKind.Once, "Allegiance rank requirement becomes spellcraft", 440, Skill: SkillItem),
            new(Teak, "Teak", TinkerOrigin.Retail, TinkerKind.Once, "Heritage requirement: Aluvian", 450, Skill: SkillItem),
            new(Ebony, "Ebony", TinkerOrigin.Retail, TinkerKind.Once, "Heritage requirement: Gharu'ndim", 451, Skill: SkillItem),
            new(Porcelain, "Porcelain", TinkerOrigin.Retail, TinkerKind.Once, "Heritage requirement: Sho", 452, Skill: SkillItem),
            new(Satin, "Satin", TinkerOrigin.Retail, TinkerKind.Once, "Heritage requirement: Viamontian", 453, Skill: SkillItem),
            new(Ivory, "Ivory", TinkerOrigin.Retail, TinkerKind.Once, "Removes attunement", 490, UsesTinkSlot: false, Skill: SkillItem),
            new(Leather, "Leather", TinkerOrigin.Retail, TinkerKind.Once, "Retained (can't be salvaged or sold)", 500, UsesTinkSlot: false, Skill: SkillItem),
            new(Sandstone, "Sandstone", TinkerOrigin.Retail, TinkerKind.Once, "Removes Retained", 510, UsesTinkSlot: false, Skill: SkillItem),
        },
        [TinkerBench.Trinkets] = Array.Empty<TinkerMaterial>(),
    };

    /// <summary>The retail rows of a tab.</summary>
    public static IReadOnlyList<TinkerMaterial> RetailFor(TinkerBench bench) => Retail[bench];

    /// <summary>
    /// A server plugin's salvage rows for one tab (its profile's "salvage" list). Rows for other
    /// tabs, unknown tab names and unknown kinds are left out. A row's own "order" decides where it
    /// runs; without one, imbues run before tinks and one-application rows after them, each in the
    /// plugin's list order.
    /// </summary>
    public static List<TinkerMaterial> FromServerPlugin(TinkerBench bench, IEnumerable<RynthCore.Plugin.Shared.ServerSalvageView>? salvage)
    {
        var rows = new List<TinkerMaterial>();
        if (salvage == null) return rows;
        string benchName = bench.ToString().ToLowerInvariant();
        int i = 0;
        foreach (var s in salvage)
        {
            if (s == null || s.Material <= 0 || !string.Equals(s.Bench, benchName, StringComparison.OrdinalIgnoreCase)) continue;
            TinkerKind? kind = s.Kind switch
            {
                "tink" or "" => TinkerKind.Tink,
                "imbue" => TinkerKind.Imbue,
                "once" => TinkerKind.Once,
                _ => null,
            };
            if (kind == null) continue;
            int order = kind switch
            {
                TinkerKind.Imbue => 115,
                TinkerKind.Once => 600,
                _ => 400,
            } + i++;
            if (s.Order != 0) order = s.Order;
            string group = s.StackCap > 0 ? (s.StackGroup ?? string.Empty).Trim() : string.Empty;
            rows.Add(new TinkerMaterial(s.Material, MaterialName(s.Material), TinkerOrigin.Plugin, kind.Value,
                string.IsNullOrWhiteSpace(s.Effect) ? "server recipe" : s.Effect.Trim(), order,
                UsesTinkSlot: s.UsesTinkSlot, AppliedInt: s.AppliedInt, NameMatch: s.NameMatch ?? string.Empty,
                Prepared: s.Prepared, AfterCap: s.AfterCap, StackGroup: group, StackCap: group.Length > 0 ? s.StackCap : 0));
        }
        return rows;
    }

    /// <summary>
    /// Settings key for a row: retail and server rows of one tab share ids (Wool, Steel...). Keys
    /// with the old "s" prefix (RynthAi's own Leaftide rows before they moved to RynthILT) match no row.
    /// </summary>
    public static string Key(TinkerMaterial m) => m.Origin switch
    {
        TinkerOrigin.Retail => "r" + m.Id,
        TinkerOrigin.Plugin => "p" + m.Id,
        _ => "c" + m.Id,
    };

    /// <summary>The object classes a tab scans.</summary>
    public static bool Targets(TinkerBench bench, AcObjectClass oc) => bench switch
    {
        TinkerBench.Armor => oc is AcObjectClass.Armor or AcObjectClass.Clothing,
        TinkerBench.Melee => oc == AcObjectClass.MeleeWeapon,
        TinkerBench.Missile => oc == AcObjectClass.MissileWeapon,
        TinkerBench.Caster => oc == AcObjectClass.WandStaffOrb,
        TinkerBench.Jewelry or TinkerBench.Trinkets => oc == AcObjectClass.Jewelry,
        TinkerBench.Items => oc is not (AcObjectClass.Salvage or AcObjectClass.Container or AcObjectClass.Unknown
            or AcObjectClass.Money or AcObjectClass.TradeNote or AcObjectClass.SpellComponent or AcObjectClass.Food),
        _ => false,
    };

    /// <summary>
    /// Trinkets are jewelry the Jewelry tab leaves alone: they take the Trinkets tab's recipes instead.
    /// Which jewelry is a trinket is the server plugin's (its profile's "trinkets": Leaftide's compasses,
    /// goggles, scarabs, ... come from RynthILT since 2026-10-20); without one, nothing is.
    /// </summary>
    public static bool IsTrinket(string name, RynthCore.Plugin.Shared.ServerTrinketRule? rule)
        => (rule ?? RynthCore.Plugin.Shared.ServerTrinketRule.None).IsTrinket(name);

    public static string Label(TinkerBench bench) => bench switch
    {
        TinkerBench.Armor => "Armor",
        TinkerBench.Melee => "Melee",
        TinkerBench.Missile => "Missile",
        TinkerBench.Caster => "Caster",
        TinkerBench.Jewelry => "Jewelry",
        TinkerBench.Items => "Items",
        TinkerBench.Trinkets => "Trinkets",
        _ => bench.ToString(),
    };

    /// <summary>The default tinkering skill a tab's custom rows use for the chance.</summary>
    public static int DefaultSkill(TinkerBench bench) => bench switch
    {
        TinkerBench.Armor => SkillArmor,
        TinkerBench.Melee or TinkerBench.Missile => SkillWeapon,
        TinkerBench.Caster or TinkerBench.Jewelry => SkillMagicItem,
        _ => SkillItem,
    };

    /// <summary>
    /// The rend gem for a weapon's damage type (melee/missile DamageType 45, the caster's element
    /// the same field): Slash+Pierce takes <paramref name="pierceForCombo"/>; Nether is Onyx.
    /// 0 when the type has no rend.
    /// </summary>
    public static int RendFor(int damageType, bool pierceForCombo)
    {
        if (damageType == 3) return pierceForCombo ? BlackGarnet : ImperialTopaz;
        if ((damageType & 0x400) != 0) return Onyx;
        return damageType switch
        {
            1 => ImperialTopaz,
            2 => BlackGarnet,
            4 => WhiteSapphire,
            8 => Aquamarine,
            16 => RedGarnet,
            32 => Emerald,
            64 => Jet,
            _ => 0,
        };
    }

    /// <summary>The rend gem a Foolproof bag's name advertises (server bags may not set Material), else 0.</summary>
    public static int RendFromFoolproofName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf("Foolproof", StringComparison.OrdinalIgnoreCase) < 0) return 0;
        // Longer names first: "Black Opal" must not read as "Black Garnet", "Onyx" before both.
        (string Text, int Id)[] map =
        {
            ("Imperial Topaz", ImperialTopaz), ("White Sapphire", WhiteSapphire), ("Onyx", Onyx),
            ("Black Garnet", BlackGarnet), ("Black Opal", BlackOpal), ("Aquamarine", Aquamarine),
            ("Red Garnet", RedGarnet), ("Emerald", Emerald), ("Jet", Jet),
        };
        foreach (var (text, id) in map)
            if (name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return id;
        return 0;
    }

    /// <summary>Material names for the custom-row picker and status text.</summary>
    public static readonly (int Id, string Name)[] MaterialNames =
    {
        (Ceramic, "Ceramic"), (Porcelain, "Porcelain"), (3, "Cloth"), (Linen, "Linen"), (Satin, "Satin"), (Silk, "Silk"),
        (Velvet, "Velvet"), (Wool, "Wool"), (Agate, "Agate"), (11, "Amber"), (12, "Amethyst"), (Aquamarine, "Aquamarine"),
        (Azurite, "Azurite"), (BlackGarnet, "Black Garnet"), (BlackOpal, "Black Opal"), (Bloodstone, "Bloodstone"),
        (Carnelian, "Carnelian"), (Citrine, "Citrine"), (Diamond, "Diamond"), (Emerald, "Emerald"), (FireOpal, "Fire Opal"),
        (GreenGarnet, "Green Garnet"), (GreenJade, "Green Jade"), (Hematite, "Hematite"), (ImperialTopaz, "Imperial Topaz"),
        (Jet, "Jet"), (LapisLazuli, "Lapis Lazuli"), (LavenderJade, "Lavender Jade"), (Malachite, "Malachite"),
        (Moonstone, "Moonstone"), (Onyx, "Onyx"), (Opal, "Opal"), (Peridot, "Peridot"), (RedGarnet, "Red Garnet"),
        (RedJade, "Red Jade"), (RoseQuartz, "Rose Quartz"), (Ruby, "Ruby"), (39, "Sapphire"), (SmokeyQuartz, "Smokey Quartz"),
        (Sunstone, "Sunstone"), (TigerEye, "Tiger Eye"), (Tourmaline, "Tourmaline"), (Turquoise, "Turquoise"),
        (WhiteJade, "White Jade"), (WhiteQuartz, "White Quartz"), (WhiteSapphire, "White Sapphire"),
        (YellowGarnet, "Yellow Garnet"), (YellowTopaz, "Yellow Topaz"), (Zircon, "Zircon"), (Ivory, "Ivory"),
        (Leather, "Leather"), (Armoredillo, "Armoredillo Hide"), (GromnieHide, "Gromnie Hide"), (Reedshark, "Reedshark Hide"),
        (Brass, "Brass"), (Bronze, "Bronze"), (Copper, "Copper"), (Gold, "Gold"), (Iron, "Iron"), (Pyreal, "Pyreal"),
        (Silver, "Silver"), (Steel, "Steel"), (Alabaster, "Alabaster"), (Granite, "Granite"), (Marble, "Marble"),
        (Sandstone, "Sandstone"), (Serpentine, "Serpentine"), (Ebony, "Ebony"), (Mahogany, "Mahogany"), (Oak, "Oak"),
        (Pine, "Pine"), (Teak, "Teak"),
    };

    /// <summary>A material's display name ("#id" when unknown).</summary>
    public static string MaterialName(int id)
    {
        foreach (var (mid, name) in MaterialNames)
            if (mid == id) return name;
        return "#" + id;
    }
}
