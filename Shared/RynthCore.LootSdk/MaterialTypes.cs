using System.Collections.Generic;

namespace RynthCore.Loot;

/// <summary>
/// AC MaterialType id → human name. Used by the loot editor to populate the
/// per-material combine grid. IDs are the same values returned by AC's
/// MATERIAL_TYPE int property (STypeInt 131).
/// </summary>
public static class MaterialTypes
{
    public static readonly IReadOnlyDictionary<int, string> ByName = BuildMap();

    private static Dictionary<int, string> BuildMap() => new()
    {
        { 1,  "Ceramic" },          { 2,  "Porcelain" },
        { 3,  "Cloth" },            { 4,  "Linen" },         { 5,  "Satin" },
        { 6,  "Silk" },             { 7,  "Velvet" },        { 8,  "Wool" },
        { 9,  "Gem" },              { 10, "Agate" },         { 11, "Amber" },
        { 12, "Amethyst" },         { 13, "Aquamarine" },    { 14, "Azurite" },
        { 15, "Black Garnet" },     { 16, "Black Opal" },    { 17, "Bloodstone" },
        { 18, "Carnelian" },        { 19, "Citrine" },       { 20, "Diamond" },
        { 21, "Emerald" },          { 22, "Fire Opal" },     { 23, "Green Garnet" },
        { 24, "Green Jade" },       { 25, "Hematite" },      { 26, "Imperial Topaz" },
        { 27, "Jet" },              { 28, "Lapis Lazuli" },  { 29, "Lavender Jade" },
        { 30, "Malachite" },        { 31, "Moonstone" },     { 32, "Onyx" },
        { 33, "Opal" },             { 34, "Peridot" },       { 35, "Red Garnet" },
        { 36, "Red Jade" },         { 37, "Rose Quartz" },   { 38, "Ruby" },
        { 39, "Sapphire" },         { 40, "Smoky Quartz" },  { 41, "Sunstone" },
        { 42, "Tiger Eye" },        { 43, "Tourmaline" },    { 44, "Turquoise" },
        { 45, "White Jade" },       { 46, "White Quartz" },  { 47, "White Sapphire" },
        { 48, "Yellow Garnet" },    { 49, "Yellow Topaz" },  { 50, "Zircon" },
        { 51, "Ivory" },            { 52, "Leather" },       { 53, "Armoredillo Hide" },
        { 54, "Gromnie Hide" },     { 55, "Reedshark Hide" },{ 56, "Metal" },
        { 57, "Brass" },            { 58, "Bronze" },        { 59, "Copper" },
        { 60, "Gold" },             { 61, "Iron" },          { 62, "Pyreal" },
        { 63, "Silver" },           { 64, "Steel" },         { 65, "Stone" },
        { 66, "Alabaster" },        { 67, "Granite" },       { 68, "Marble" },
        { 69, "Obsidian" },         { 70, "Sandstone" },     { 71, "Serpentine" },
        { 72, "Wood" },             { 73, "Ebony" },         { 74, "Mahogany" },
        { 75, "Oak" },              { 76, "Pine" },          { 77, "Teak" },
    };

    public static string Name(int id) => ByName.TryGetValue(id, out string? n) ? n : $"Material#{id}";
}
