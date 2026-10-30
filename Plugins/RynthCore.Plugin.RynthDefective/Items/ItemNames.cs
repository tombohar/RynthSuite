using System;

namespace RynthCore.Plugin.RynthDefective.Items;

/// <summary>Display names: a weapon's material in front of the base name the client reports.</summary>
internal static class ItemNames
{
    /// <summary>ACE's MaterialType names, index = the id (0 = none).</summary>
    private static readonly string[] Materials =
    {
        "", "Ceramic", "Porcelain", "Cloth", "Linen", "Satin", "Silk", "Velvet", "Wool", "Gem",
        "Agate", "Amber", "Amethyst", "Aquamarine", "Azurite", "Black Garnet", "Black Opal", "Bloodstone", "Carnelian", "Citrine",
        "Diamond", "Emerald", "Fire Opal", "Green Garnet", "Green Jade", "Hematite", "Imperial Topaz", "Jet", "Lapis Lazuli", "Lavender Jade",
        "Malachite", "Moonstone", "Onyx", "Opal", "Peridot", "Red Garnet", "Red Jade", "Rose Quartz", "Ruby", "Sapphire",
        "Smokey Quartz", "Sunstone", "Tiger Eye", "Tourmaline", "Turquoise", "White Jade", "White Quartz", "White Sapphire", "Yellow Garnet", "Yellow Topaz",
        "Zircon", "Ivory", "Leather", "Armoredillo Hide", "Gromnie Hide", "Reed Shark Hide", "Metal", "Brass", "Bronze", "Copper",
        "Gold", "Iron", "Pyreal", "Silver", "Steel", "Stone", "Alabaster", "Granite", "Marble", "Obsidian",
        "Sandstone", "Serpentine", "Wood", "Ebony", "Mahogany", "Oak", "Pine", "Teak",
    };

    public static string Material(int id) => id > 0 && id < Materials.Length ? Materials[id] : string.Empty;

    /// <summary>"Silver Wand": the material in front, unless the name already starts with it.</summary>
    public static string Full(string? baseName, int material)
    {
        string name = (baseName ?? string.Empty).Trim();
        string mat = Material(material);
        if (mat.Length == 0 || name.Length == 0) return name;
        if (name.StartsWith(mat + " ", StringComparison.OrdinalIgnoreCase) || name.Equals(mat, StringComparison.OrdinalIgnoreCase))
            return name;
        return mat + " " + name;
    }
}
