using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// A weapon's full retail name: the material (MaterialType, int 131, in CreateObject) in
/// front of the base name the client reports ("Wand" -> "Silver Wand"), and optionally the
/// element after it ("Silver Wand (Fire)"). Display only: rules are matched and saved by id.
/// Spellings are ACE's MaterialType names split into words ("Black Garnet", "Imperial Topaz").
/// </summary>
internal static class WeaponNames
{
    public const uint PropMaterialType = 131;

    private static readonly Dictionary<int, string> Materials = new()
    {
        [1] = "Ceramic", [2] = "Porcelain", [3] = "Cloth", [4] = "Linen", [5] = "Satin", [6] = "Silk",
        [7] = "Velvet", [8] = "Wool", [9] = "Gem", [10] = "Agate", [11] = "Amber", [12] = "Amethyst",
        [13] = "Aquamarine", [14] = "Azurite", [15] = "Black Garnet", [16] = "Black Opal", [17] = "Bloodstone",
        [18] = "Carnelian", [19] = "Citrine", [20] = "Diamond", [21] = "Emerald", [22] = "Fire Opal",
        [23] = "Green Garnet", [24] = "Green Jade", [25] = "Hematite", [26] = "Imperial Topaz", [27] = "Jet",
        [28] = "Lapis Lazuli", [29] = "Lavender Jade", [30] = "Malachite", [31] = "Moonstone", [32] = "Onyx",
        [33] = "Opal", [34] = "Peridot", [35] = "Red Garnet", [36] = "Red Jade", [37] = "Rose Quartz",
        [38] = "Ruby", [39] = "Sapphire", [40] = "Smokey Quartz", [41] = "Sunstone", [42] = "Tiger Eye",
        [43] = "Tourmaline", [44] = "Turquoise", [45] = "White Jade", [46] = "White Quartz",
        [47] = "White Sapphire", [48] = "Yellow Garnet", [49] = "Yellow Topaz", [50] = "Zircon",
        [51] = "Ivory", [52] = "Leather", [53] = "Armoredillo Hide", [54] = "Gromnie Hide",
        [55] = "Reed Shark Hide", [56] = "Metal", [57] = "Brass", [58] = "Bronze", [59] = "Copper",
        [60] = "Gold", [61] = "Iron", [62] = "Pyreal", [63] = "Silver", [64] = "Steel", [65] = "Stone",
        [66] = "Alabaster", [67] = "Granite", [68] = "Marble", [69] = "Obsidian", [70] = "Sandstone",
        [71] = "Serpentine", [72] = "Wood", [73] = "Ebony", [74] = "Mahogany", [75] = "Oak", [76] = "Pine",
        [77] = "Teak",
    };

    /// <summary>The material's display name, "" when unknown.</summary>
    public static string Material(int materialType) => Materials.TryGetValue(materialType, out string? n) ? n : "";

    /// <summary>"Silver Wand": the material in front, unless the name already starts with it.</summary>
    public static string Full(string? baseName, int materialType)
    {
        string name = (baseName ?? "").Trim();
        string mat = Material(materialType);
        if (mat.Length == 0 || name.Length == 0) return name;
        if (name.StartsWith(mat + " ", StringComparison.OrdinalIgnoreCase) || name.Equals(mat, StringComparison.OrdinalIgnoreCase))
            return name;
        return mat + " " + name;
    }

    /// <summary>"Silver Wand (Fire)"; the bare name when the element is unknown.</summary>
    public static string WithElement(string name, string? element)
        => string.IsNullOrWhiteSpace(element) ? name : $"{name} ({element})";

    /// <summary>
    /// An object's full name from the client (name and material read live through the host,
    /// which is safe from the panels' threads; the object cache is not), else <paramref name="fallback"/>.
    /// </summary>
    public static string For(RynthCoreHost host, WorldObjectCache? cache, int id, string? fallback)
    {
        _ = cache;   // kept for callers; the cache's lazy lookup isn't thread-safe
        string baseName = "";
        if (host.HasGetObjectName)
        {
            try { host.TryGetObjectName(unchecked((uint)id), out baseName); } catch { baseName = ""; }
        }
        if (string.IsNullOrEmpty(baseName)) baseName = fallback ?? "";
        int mat = 0;
        if (host.HasGetObjectIntProperty && host.TryGetObjectIntProperty(unchecked((uint)id), PropMaterialType, out int m)) mat = m;
        return Full(baseName, mat);
    }
}

/// <summary>The Items list's weapons: their elements and the wand lookups shared by combat and buffing.</summary>
internal static class WeaponList
{
    /// <summary>ItemRule.ElementSource when the user picked the element in the Items panel.</summary>
    public const string SourceSet = "set";
    /// <summary>ItemRule.Action of a listed shield (the off hand); weapons are "Weapon".</summary>
    public const string ShieldAction = "Shield";

    public static bool IsShieldRule(ItemRule r) => string.Equals(r.Action, ShieldAction, StringComparison.OrdinalIgnoreCase);

    private static int Rank(string source) => source switch
    {
        SourceSet    => 9,
        "properties" => 4,
        "icon"       => 3,
        "rending"    => 2,
        "name"       => 1,
        _            => 0,
    };

    /// <summary>
    /// Brings each listed weapon's element up to date from what's known about it. An element
    /// the user set is kept. A better source replaces a worse one (properties beat the icon,
    /// the icon beats a rending, a rending beats the name). An entry from before this (no
    /// source) keeps a non-Slash element as the user's; its old "Slash" default is replaced as
    /// soon as anything is known. Returns the entries that changed.
    /// </summary>
    public static int RefreshElements(List<ItemRule> rules, Func<int, string?, WeaponElements.Info> read, Func<int, string?>? nameOf = null)
    {
        int changed = 0;
        foreach (var r in rules)
        {
            if (r.ElementSource == SourceSet || IsShieldRule(r)) continue;   // a shield has no element
            if (r.ElementSource.Length == 0 && r.Element.Length > 0
                && !r.Element.Equals("Slash", StringComparison.OrdinalIgnoreCase))
            {
                r.ElementSource = SourceSet;
                changed++;
                continue;
            }
            var info = read(r.Id, nameOf?.Invoke(r.Id) ?? r.Name);
            if (info.Unknown) continue;
            bool better = Rank(info.Source) > Rank(r.ElementSource);
            bool same = Rank(info.Source) == Rank(r.ElementSource);
            if (better || (same && !info.Element.Equals(r.Element, StringComparison.OrdinalIgnoreCase)))
            {
                r.Element = info.Element;
                r.ElementSource = info.Source;
                changed++;
            }
        }
        return changed;
    }

    /// <summary>What the Items panel shows for an element nobody knows yet.</summary>
    public const string UnknownLabel = "Unknown";

    /// <summary>
    /// Copies of the weapons for the Items panel: the full name (material in front) and
    /// "Unknown" for an element not known yet. The panel sends the copies back on an edit;
    /// <see cref="MergeEdited"/> maps them onto the saved entries by id.
    /// </summary>
    public static List<ItemRule> ForDisplay(IEnumerable<ItemRule> rules, Func<ItemRule, string> fullName, Func<ItemRule, string>? kind = null)
    {
        var list = new List<ItemRule>();
        foreach (var r in rules)
            list.Add(new ItemRule
            {
                Id = r.Id, Name = fullName(r), Action = r.Action, KeepBuffed = r.KeepBuffed,
                Kind = kind?.Invoke(r) ?? "",
                // A shield shows "Shield" in the element column (it has no element).
                Element = IsShieldRule(r) ? ShieldAction : r.Element.Length > 0 ? r.Element : UnknownLabel,
                ElementSource = r.ElementSource,
            });
        return list;
    }

    /// <summary>Appended to a listed item's name in the pickers when it isn't carried (<see cref="MissingIds"/>).</summary>
    public const string MissingSuffix = " (missing)";

    /// <summary>
    /// The listed items the character no longer carries: neither in the pack
    /// (<paramref name="inventory"/>, WorldObjectCache.GetInventory) nor wielded. The client
    /// can still know an object that left (sold, given, on a mule), so the saved name alone
    /// looked fine while combat couldn't use it (2026-10-11, Drakkon's buff wand 0x8000EF07,
    /// shown as "Wand"). An empty inventory means it isn't known yet (logging in): nothing is
    /// reported missing then. For the Items panel's warning and the pickers' "(missing)".
    /// </summary>
    public static HashSet<int> MissingIds(IEnumerable<ItemRule> rules, IEnumerable<WorldObject> inventory,
        Func<int, WorldObject?> lookup, Func<WorldObject, bool> isWielded)
    {
        var missing = new HashSet<int>();
        var carried = new HashSet<int>();
        foreach (var wo in inventory) carried.Add(wo.Id);
        if (carried.Count == 0) return missing;
        foreach (var r in rules)
        {
            if (r.Id == 0 || carried.Contains(r.Id)) continue;
            var wo = lookup(r.Id);
            if (wo != null && isWielded(wo)) continue;
            missing.Add(r.Id);
        }
        return missing;
    }

    /// <summary>
    /// The Items panel's edited list, mapped onto the saved one by id: the saved name stays
    /// (the panel shows the full name; entries are matched and saved by id), an element the
    /// user changed becomes theirs ("set"), and "Unknown" stays unknown. Order and deletions
    /// follow the panel.
    /// </summary>
    public static List<ItemRule> MergeEdited(IReadOnlyList<ItemRule> saved, IEnumerable<ItemRule> edited)
    {
        var byId = new Dictionary<int, ItemRule>();
        foreach (var r in saved) byId.TryAdd(r.Id, r);
        var list = new List<ItemRule>();
        foreach (var e in edited)
        {
            string elem = e.Element.Equals(UnknownLabel, StringComparison.OrdinalIgnoreCase) ? "" : e.Element;
            if (byId.TryGetValue(e.Id, out var old))
            {
                if (IsShieldRule(old)) elem = old.Element;   // a shield keeps no element; the column only says "Shield"
                bool changed = !elem.Equals(old.Element, StringComparison.OrdinalIgnoreCase);
                list.Add(new ItemRule
                {
                    // The panel carries only id, name and element: the rest stays as saved.
                    Id = old.Id, Name = old.Name, Action = old.Action, KeepBuffed = old.KeepBuffed,
                    Element = changed ? elem : old.Element,
                    ElementSource = changed ? (elem.Length > 0 ? SourceSet : "") : old.ElementSource,
                });
            }
            else
            {
                e.Element = elem;
                list.Add(e);
            }
        }
        return list;
    }

    /// <summary>
    /// The wand to wield for buffing or casting: a listed wand (the one already in hand first,
    /// then list order). When the list has no wand at all and <paramref name="allowUnlisted"/> is
    /// on (LegacyUiSettings.WieldUnlistedWandWhenNoneListed), a caster from the pack (by object
    /// class; never by name). 0 = none.
    /// </summary>
    public static int FindWand(IEnumerable<ItemRule> rules, Func<int, WorldObject?> lookup, Func<WorldObject, bool> isWielded,
        IEnumerable<WorldObject> inventory, bool allowUnlisted, out bool unlisted, Func<WorldObject, bool>? skip = null,
        Func<WorldObject, int>? rank = null)
    {
        // 2026-10-18 deep dive (Drakkon): the wand is the caster in hand, else a listed caster that is
        // carried and not skipped (lowest rank first, then list order). Any other caster from the pack
        // only when nothing listed is carried: no caster on the Items list, or every listed one was
        // dropped or lost (owner, 2026-10-18), and allowUnlisted. A listed caster that is carried but
        // can't be wielded never opens the pack: the old fallback had Drakkon trying casters he never
        // listed, and the same-name stand-in took any pack item named "Wand" for his lost one.
        unlisted = false;
        var pack = inventory as ICollection<WorldObject> ?? new List<WorldObject>(inventory);
        var carried = new HashSet<int>();
        foreach (var wo in pack) carried.Add(wo.Id);
        // A caster already in hand is the one to use, listed or not: it is wielded, so it can be.
        foreach (var wo in pack)
            if (IsWand(wo) && isWielded(wo)) return wo.Id;
        bool anyListedCarried = false;
        int best = 0, bestRank = int.MaxValue;
        foreach (var r in rules)
        {
            var wo = lookup(r.Id);
            if (wo == null || !IsWand(wo)) continue;
            if (isWielded(wo)) return r.Id;
            // Only a wand the character still carries: a listed wand that left the pack (sold, given,
            // on a mule) gets no answer from the server (2026-10-11, wand 0x8000EF07 shown as '?').
            if (!carried.Contains(wo.Id)) continue;
            anyListedCarried = true;
            if (skip != null && skip(wo)) continue;   // can't be wielded (requirement, not identified yet, refused)
            int k = rank?.Invoke(wo) ?? 0;
            if (k < bestRank) { best = r.Id; bestRank = k; }
        }
        if (best != 0 || anyListedCarried || !allowUnlisted) return best;
        foreach (var wo in pack)
        {
            if (wo.ObjectClass != AcObjectClass.WandStaffOrb || (skip != null && skip(wo))) continue;
            int k = rank?.Invoke(wo) ?? 0;
            if (k < bestRank) { best = wo.Id; bestRank = k; }
        }
        unlisted = best != 0;
        return best;
    }

    /// <summary>
    /// A caster: object class WandStaffOrb, or an unclassified item named like one. A melee
    /// weapon named "Quarter Staff" is not a wand (it used to be, and put the character in Magic mode).
    /// </summary>
    public static bool IsWand(WorldObject wo)
    {
        if (wo.ObjectClass == AcObjectClass.WandStaffOrb) return true;
        if (wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon) return false;
        return IsWandName(wo.Name);
    }

    public static bool IsWandName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("Orb",      StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Staff",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Wand",     StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Scepter",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Sceptre",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Baton",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Crozier",  StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
