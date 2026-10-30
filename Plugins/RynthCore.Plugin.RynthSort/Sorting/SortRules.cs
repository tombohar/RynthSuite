using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>
/// One rule: items whose class is one of <see cref="Classes"/>, or whose name contains one of
/// <see cref="Names"/> (any case), go to the side pack <see cref="PackId"/> names while it is
/// carried, else to side pack number <see cref="Pack"/> (1 = the top side pack when the pack
/// order is known). "Armor, Clothing, name has Shield -> pack 4" is one rule.
/// A rule saved before rules held several entries (the Lua script's { kind, class | text }, and
/// RynthSort's file versions 1 and 2) loads as a rule with one entry.
/// </summary>
internal sealed class SortRule
{
    public const int MaxClasses = 32, MaxNames = 16, MaxNameLength = 64;

    private static int _nextUid;

    /// <summary>Stable for the session (never saved): the window's widget ids, so a rule keeps its row while the list resorts.</summary>
    public readonly int Uid = Interlocked.Increment(ref _nextUid);

    public bool On = true;
    /// <summary>ObjectClass numbers (<see cref="ObjectClasses"/>), each once.</summary>
    public readonly List<int> Classes = new();
    /// <summary>Parts of item names (any case), each once.</summary>
    public readonly List<string> Names = new();
    /// <summary>The pack's number: the fallback when <see cref="PackId"/> isn't carried (or is 0), and what the window shows.</summary>
    public int Pack = 1;
    /// <summary>The side pack's object id (0 = none yet: the number decides). Pack order and foci can't shift it.</summary>
    public uint PackId;
    /// <summary>What the pack was when it was picked ("Sack - Salvage, Gems"), for the warning when it's gone.</summary>
    public string PackNote = string.Empty;

    /// <summary>No class and no name: it matches nothing (a rule just added, before anything is put in it).</summary>
    public bool IsEmpty => Classes.Count == 0 && Names.Count == 0;

    public bool HasClass(int cls) => Classes.Contains(cls);

    public bool HasName(string text) => IndexOfName(text) >= 0;

    private int IndexOfName(string text)
    {
        for (int i = 0; i < Names.Count; i++)
            if (string.Equals(Names[i], text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Adds a class (false when it's there already or the rule is full).</summary>
    public bool AddClass(int cls)
    {
        if (cls < 0 || Classes.Contains(cls) || Classes.Count >= MaxClasses) return false;
        Classes.Add(cls);
        return true;
    }

    public bool RemoveClass(int cls) => Classes.Remove(cls);

    /// <summary>Adds a name part, trimmed and cut to <see cref="MaxNameLength"/> (false when blank, there already, or the rule is full).</summary>
    public bool AddName(string? text)
    {
        string t = Clean(text);
        if (t.Length == 0 || IndexOfName(t) >= 0 || Names.Count >= MaxNames) return false;
        Names.Add(t);
        return true;
    }

    public bool RemoveName(string text)
    {
        int i = IndexOfName(text);
        if (i < 0) return false;
        Names.RemoveAt(i);
        return true;
    }

    public static string Clean(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length > MaxNameLength ? t[..MaxNameLength].TrimEnd() : t;
    }

    /// <summary>Everything of <paramref name="other"/>'s classes and names this rule doesn't have yet.</summary>
    public void TakeEntriesOf(SortRule other)
    {
        foreach (int c in other.Classes) AddClass(c);
        foreach (string n in other.Names) AddName(n);
    }

    public SortRule Clone()
    {
        var r = new SortRule { On = On, Pack = Pack, PackId = PackId, PackNote = PackNote };
        r.Classes.AddRange(Classes);
        r.Names.AddRange(Names);
        return r;
    }

    /// <summary>The same classes and names (in any order, names any case) and the same pack number: what an untouched imported rule looks like.</summary>
    public bool SameAs(SortRule o)
    {
        if (Pack != o.Pack || Classes.Count != o.Classes.Count || Names.Count != o.Names.Count) return false;
        foreach (int c in Classes) if (!o.Classes.Contains(c)) return false;
        foreach (string n in Names) if (!o.HasName(n)) return false;
        return true;
    }

    public static SortRule ForClass(int cls, int pack)
    {
        var r = new SortRule { Pack = pack };
        r.AddClass(cls);
        return r;
    }

    public static SortRule ForName(string text, int pack)
    {
        var r = new SortRule { Pack = pack };
        r.AddName(text);
        return r;
    }

    /// <summary>A rule with several classes (and names): "Gem, Mana Stone, Key -> pack 3".</summary>
    public static SortRule For(int pack, int[] classes, params string[] names)
    {
        var r = new SortRule { Pack = pack };
        foreach (int c in classes) r.AddClass(c);
        foreach (string n in names) r.AddName(n);
        return r;
    }
}

/// <summary>
/// The ObjectClass numbers, as RynthLua's ObjectClass enum (LuaEnums.ObjectClass: Decal/UtilityBelt
/// order), so rules saved by the Lua script mean the same thing here.
/// </summary>
internal static class ObjectClasses
{
    public static readonly string[] Names =
    {
        "Unknown", "MeleeWeapon", "Armor", "Clothing", "Jewelry", "Monster", "Food", "Money", "Misc",
        "MissileWeapon", "Container", "Gem", "SpellComponent", "Key", "Portal", "TradeNote", "ManaStone",
        "Plant", "BaseCooking", "BaseAlchemy", "BaseFletching", "CraftedCooking", "CraftedAlchemy",
        "CraftedFletching", "Player", "Vendor", "Door", "Corpse", "Lifestone", "HealingKit", "Lockpick",
        "WandStaffOrb", "Bundle", "Book", "Journal", "Sign", "Housing", "Npc", "Foci", "Salvage", "Ust",
        "Services", "Scroll",
    };

    public const int Unknown = 0, MeleeWeapon = 1, Armor = 2, Clothing = 3, Jewelry = 4, Monster = 5, Food = 6,
        Money = 7, Misc = 8, MissileWeapon = 9, Container = 10, Gem = 11, SpellComponent = 12, Key = 13, Portal = 14,
        TradeNote = 15, ManaStone = 16, Plant = 17, BaseCooking = 18, BaseAlchemy = 19, BaseFletching = 20,
        CraftedCooking = 21, CraftedAlchemy = 22, CraftedFletching = 23, Player = 24, Vendor = 25, Door = 26,
        Corpse = 27, Lifestone = 28, HealingKit = 29, Lockpick = 30, WandStaffOrb = 31, Bundle = 32, Book = 33,
        Journal = 34, Sign = 35, Housing = 36, Npc = 37, Foci = 38, Salvage = 39, Ust = 40, Services = 41, Scroll = 42;

    /// <summary>The classes the rule editor offers, in the Lua script's order.</summary>
    public static readonly int[] Choices =
    {
        SpellComponent, Salvage, Gem, ManaStone, Key, HealingKit, Lockpick, Food, Scroll, TradeNote, Money, Plant,
        BaseAlchemy, BaseCooking, BaseFletching, CraftedAlchemy, CraftedCooking, CraftedFletching, Jewelry, Armor,
        Clothing, MeleeWeapon, MissileWeapon, WandStaffOrb, Bundle, Book, Journal, Ust, Misc,
    };

    /// <summary>"Spell Component" for SpellComponent, as the Lua script showed them.</summary>
    public static readonly string[] ChoiceLabels = BuildLabels();

    private static string[] BuildLabels()
    {
        var labels = new string[Choices.Length];
        for (int i = 0; i < Choices.Length; i++) labels[i] = Spaced(Names[Choices[i]]);
        return labels;
    }

    /// <summary>A space before each capital that follows a small letter ("ManaStone" -> "Mana Stone").</summary>
    public static string Spaced(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>The index of <paramref name="cls"/> in <see cref="Choices"/>, or -1.</summary>
    public static int ChoiceIndex(int cls) => Array.IndexOf(Choices, cls);

    public static string Label(int cls) =>
        cls >= 0 && cls < Names.Length ? Spaced(Names[cls]) : "class " + cls;

    /// <summary>A class as a pack's contents read: "Components", "Gems", "Salvage".</summary>
    public static string Plural(int cls) => cls switch
    {
        SpellComponent => "Components",
        Salvage => "Salvage",
        Gem => "Gems",
        ManaStone => "Mana Stones",
        Key => "Keys",
        HealingKit => "Healing Kits",
        Lockpick => "Lockpicks",
        Food => "Food",
        Scroll => "Scrolls",
        TradeNote => "Trade Notes",
        Money => "Money",
        Plant => "Plants",
        Jewelry => "Jewelry",
        Armor => "Armor",
        Clothing => "Clothing",
        MeleeWeapon => "Melee Weapons",
        MissileWeapon => "Missile Weapons",
        WandStaffOrb => "Casters",
        Book => "Books",
        Journal => "Journals",
        Ust => "Ust",
        _ => Label(cls),
    };
}

internal static class SortRules
{
    /// <summary>The Lua script's defaults: components, salvage, then gems, stones and keys together (one rule).</summary>
    public static List<SortRule> Defaults() => new()
    {
        SortRule.ForClass(ObjectClasses.SpellComponent, 1),
        SortRule.ForClass(ObjectClasses.Salvage, 2),
        SortRule.For(3, new[] { ObjectClasses.Gem, ObjectClasses.ManaStone, ObjectClasses.Key }),
    };

    /// <summary>The item's name contains one of the rule's names (any case).</summary>
    public static bool MatchesName(SortRule r, SortItem item)
    {
        string name = item.Name ?? string.Empty;
        if (name.Length == 0) return false;
        foreach (string t in r.Names)
            if (t.Length > 0 && name.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool MatchesClass(SortRule r, SortItem item) => r.Classes.Contains(item.ObjectClass);

    public static bool Matches(SortRule r, SortItem item) => r.On && (MatchesName(r, item) || MatchesClass(r, item));

    /// <summary>
    /// The rule an item follows, or null. A name match beats a class match (it is the more
    /// specific: "Armor -> 4" and "name has Shield -> 5" send shields to 5, whatever the order);
    /// between two rules that match the same way, the one saved first wins (the window warns
    /// when two rules name the same class or name).
    /// </summary>
    public static SortRule? RuleFor(IReadOnlyList<SortRule> rules, SortItem item)
    {
        for (int i = 0; i < rules.Count; i++)
            if (rules[i].On && MatchesName(rules[i], item)) return rules[i];
        for (int i = 0; i < rules.Count; i++)
            if (rules[i].On && MatchesClass(rules[i], item)) return rules[i];
        return null;
    }

    /// <summary>"Gem, Key, Mana Stone, name has "Opal"": the classes (A-Z), then the names (A-Z).</summary>
    public static string Describe(SortRule r)
    {
        if (r.IsEmpty) return "(empty: add a class or a name)";
        var parts = new List<string>(r.Classes.Count + r.Names.Count);
        foreach (string l in ClassLabels(r)) parts.Add(l);
        foreach (string n in SortedNames(r)) parts.Add($"name has \"{n}\"");
        return string.Join(", ", parts);
    }

    /// <summary>The rule's classes as labels, A-Z.</summary>
    public static List<string> ClassLabels(SortRule r)
    {
        var labels = r.Classes.ConvertAll(ObjectClasses.Label);
        labels.Sort(StringComparer.OrdinalIgnoreCase);
        return labels;
    }

    /// <summary>The rule's classes ordered by label, A-Z.</summary>
    public static List<int> SortedClasses(SortRule r)
    {
        var list = new List<int>(r.Classes);
        list.Sort((a, b) => string.Compare(ObjectClasses.Label(a), ObjectClasses.Label(b), StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static List<string> SortedNames(SortRule r)
    {
        var list = new List<string>(r.Names);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>The order rules are listed in under their pack: by <see cref="Describe"/>, A-Z (empty rules last).</summary>
    public static int CompareForList(SortRule a, SortRule b)
    {
        if (a.IsEmpty != b.IsEmpty) return a.IsEmpty ? 1 : -1;
        int c = string.Compare(Describe(a), Describe(b), StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : a.Uid.CompareTo(b.Uid);
    }

    /// <summary>
    /// The side pack the rule sends items to: 1-based index into <paramref name="inv"/>'s packs, or 0
    /// when it has none. The pack <see cref="SortRule.PackId"/> names when that is carried; else
    /// pack number <see cref="SortRule.Pack"/> when there is one (<paramref name="fellBack"/>: its id was set but isn't carried).
    /// </summary>
    public static int Resolve(SortRule r, SortInventory inv, out bool fellBack)
    {
        fellBack = false;
        int byId = inv.IndexOf(r.PackId);
        if (byId > 0) return byId;
        if (r.Pack >= 1 && r.Pack <= inv.Packs.Count)
        {
            fellBack = r.PackId != 0;
            return r.Pack;
        }
        return 0;
    }
}
