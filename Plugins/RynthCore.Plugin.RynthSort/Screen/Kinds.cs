using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthCore.Plugin.RynthSort.Screen;

/// <summary>
/// How the window names and colours a kind of item (an ObjectClass): the approved design's
/// sentence-case labels ("Spell components", "Mana stones") and its kind colours (ARGB), used for
/// rule chips, the layout bars, the plan rows' fallback squares and a tile whose picture isn't made
/// yet. Pure.
/// </summary>
internal static class Kinds
{
    // The design's palette (rynthsort-packs.html KINDS), ARGB.
    public const uint Components = 0xFF2F7FA6, Salvage = 0xFF9A7330, Gems = 0xFF7550B5, Keys = 0xFFA87B30,
        Stones = 0xFF2E95AD, Kits = 0xFF3F8F50, Picks = 0xFF68737F, Food = 0xFFA44A60, Notes = 0xFF8F8A33,
        Coins = 0xFFB18F30, Gear = 0xFF56606C, Weapons = 0xFF5D5568, Scrolls = 0xFF85563F, Crafting = 0xFF6A7F3A;

    /// <summary>The kind's colour (ARGB).</summary>
    public static uint Color(int cls) => cls switch
    {
        ObjectClasses.SpellComponent => Components,
        ObjectClasses.Salvage => Salvage,
        ObjectClasses.Gem => Gems,
        ObjectClasses.Key => Keys,
        ObjectClasses.ManaStone => Stones,
        ObjectClasses.HealingKit => Kits,
        ObjectClasses.Lockpick => Picks,
        ObjectClasses.Food => Food,
        ObjectClasses.TradeNote => Notes,
        ObjectClasses.Money => Coins,
        ObjectClasses.Armor or ObjectClasses.Clothing or ObjectClasses.Jewelry => Gear,
        ObjectClasses.MeleeWeapon or ObjectClasses.MissileWeapon or ObjectClasses.WandStaffOrb or ObjectClasses.Bundle => Weapons,
        ObjectClasses.Scroll or ObjectClasses.Book or ObjectClasses.Journal => Scrolls,
        ObjectClasses.Plant or ObjectClasses.BaseAlchemy or ObjectClasses.BaseCooking or ObjectClasses.BaseFletching
            or ObjectClasses.CraftedAlchemy or ObjectClasses.CraftedCooking or ObjectClasses.CraftedFletching or ObjectClasses.Ust => Crafting,
        _ => Gear,
    };

    private static readonly Dictionary<int, string> LabelCache = new();

    /// <summary>"Spell components", "Mana stones", "Melee weapons": what a chip or a layout row says.</summary>
    public static string Label(int cls)
    {
        lock (LabelCache)
        {
            if (LabelCache.TryGetValue(cls, out string? s)) return s;
            s = cls == ObjectClasses.SpellComponent ? "Spell components" : Sentence(ObjectClasses.Plural(cls));
            LabelCache[cls] = s;
            return s;
        }
    }

    /// <summary>"Mana Stones" to "Mana stones": the first letter as it is, the rest of each word's first letters small.</summary>
    internal static string Sentence(string text)
    {
        if (text.Length < 2) return text;
        char[] a = text.ToCharArray();
        for (int i = 1; i < a.Length; i++)
            if (a[i - 1] == ' ') a[i] = char.ToLowerInvariant(a[i]);
        return new string(a);
    }

    /// <summary>A name rule's chip: <c>named "Opal"</c>.</summary>
    public static string NameChip(string name) => "named \"" + name + "\"";

    private static readonly Dictionary<int, string> QtyCache = new();

    /// <summary>A stack's count as the tile's corner shows it: "" for one, "75", "2.4k" from 1000.</summary>
    public static string Qty(int stack)
    {
        if (stack <= 1) return string.Empty;
        lock (QtyCache)
        {
            if (QtyCache.TryGetValue(stack, out string? s)) return s;
            s = stack < 1000
                ? stack.ToString(CultureInfo.InvariantCulture)
                : (System.Math.Round(stack / 100.0) / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            if (QtyCache.Count < 4096) QtyCache[stack] = s;
            return s;
        }
    }

    /// <summary>"AO" for "Amuli Shadow Coat": the letters a tile shows while its picture is being made.</summary>
    public static string Initials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        string[] words = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2) return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0]));
        return name.Length <= 3 ? name : name[..3];
    }
}
