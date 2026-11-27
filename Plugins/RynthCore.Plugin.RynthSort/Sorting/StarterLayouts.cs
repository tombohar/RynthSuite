using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>One kind of thing a starter layout gives a pack of its own ("Spell components").</summary>
internal sealed class StarterGroup
{
    public readonly string Key;
    /// <summary>What the window calls it.</summary>
    public readonly string Label;
    public readonly int[] Classes;

    public StarterGroup(string key, string label, params int[] classes)
    {
        Key = key;
        Label = label;
        Classes = classes;
    }

    public bool Takes(SortItem it) => Array.IndexOf(Classes, it.ObjectClass) >= 0;
}

/// <summary>A one-click set of rules: kinds of things, most wanted first, one side pack each.</summary>
internal sealed class StarterLayout
{
    public readonly string Key;
    public readonly string Name;
    /// <summary>One plain line: who it suits and what it does.</summary>
    public readonly string Blurb;
    public readonly StarterGroup[] Groups;

    public StarterLayout(string key, string name, string blurb, params StarterGroup[] groups)
    {
        Key = key;
        Name = name;
        Blurb = blurb;
        Groups = groups;
    }
}

/// <summary>A layout fitted to the packs a character carries: which kind goes in which pack, and the rules that say so.</summary>
internal sealed class LayoutPlan
{
    public StarterLayout Layout = null!;
    /// <summary>Each kind that got a pack, and the pack (1-based, as the window numbers them), top pack first.</summary>
    public readonly List<(StarterGroup Group, int Pack)> Placed = new();
    /// <summary>Kinds with no pack left (they stay in the main pack).</summary>
    public readonly List<StarterGroup> Left = new();
    /// <summary>Packs no kind went to (free for the player's own rules), 1-based.</summary>
    public readonly List<int> Spare = new();
    /// <summary>The rules the layout makes, each tied to its pack's id.</summary>
    public readonly List<SortRule> Rules = new();
    /// <summary>Items already in the pack their kind gets (they won't move).</summary>
    public int AlreadyThere;
}

/// <summary>
/// Starter layouts (#rynth-issues "The bag sort feature is lacking in UX": the rules are complex
/// and there is no common-sense default). A layout is a list of kinds, most wanted first; fitted to
/// a character it gives each kind one side pack while packs last. Kinds a pack already holds come
/// first, wherever they are in the list: each goes to the pack that holds most of it (so the fewest
/// items move; 2026-11-26, the approved window design: a pack already holding kits and food keeps
/// them in Everyday). Then the kinds no pack holds, most wanted first, into the emptiest packs left,
/// top first. Kinds past the packs carried stay in the main pack, as does anything no kind names
/// (loot gear, unusual items).
/// Nothing is applied here: the window shows the fit and a preview first. Pure.
/// </summary>
internal static class StarterLayouts
{
    public static readonly StarterGroup Components = new("components", "Spell components", ObjectClasses.SpellComponent);
    public static readonly StarterGroup Salvage = new("salvage", "Salvage", ObjectClasses.Salvage);
    public static readonly StarterGroup GemsKeys = new("gems", "Gems, mana stones and keys", ObjectClasses.Gem, ObjectClasses.ManaStone, ObjectClasses.Key);
    public static readonly StarterGroup Notes = new("notes", "Trade notes and coins", ObjectClasses.TradeNote, ObjectClasses.Money);
    public static readonly StarterGroup Supplies = new("supplies", "Healing kits, lockpicks and food", ObjectClasses.HealingKit, ObjectClasses.Lockpick, ObjectClasses.Food);
    public static readonly StarterGroup Scrolls = new("scrolls", "Scrolls and books", ObjectClasses.Scroll, ObjectClasses.Book, ObjectClasses.Journal);
    public static readonly StarterGroup Crafting = new("crafting", "Crafting supplies",
        ObjectClasses.BaseAlchemy, ObjectClasses.BaseCooking, ObjectClasses.BaseFletching,
        ObjectClasses.CraftedAlchemy, ObjectClasses.CraftedCooking, ObjectClasses.CraftedFletching, ObjectClasses.Plant, ObjectClasses.Ust);
    public static readonly StarterGroup Gear = new("gear", "Armor, weapons and jewelry",
        ObjectClasses.Armor, ObjectClasses.Clothing, ObjectClasses.Jewelry, ObjectClasses.MeleeWeapon, ObjectClasses.MissileWeapon, ObjectClasses.WandStaffOrb);

    public static readonly StarterLayout Everyday = new("everyday", "Everyday",
        "A good start: components, gems and keys, salvage, then kits, notes or scrolls in a pack that already holds them.",
        Components, GemsKeys, Salvage, Notes, Supplies, Scrolls);

    public static readonly StarterLayout Hunting = new("hunting", "Hunting",
        "For hunts: loot gear gets a pack of its own, so the main pack stays free for new loot.",
        Components, Salvage, Gear, GemsKeys, Notes, Supplies);

    public static readonly StarterLayout CraftingLayout = new("crafting", "Crafting",
        "For tinkers and cooks: salvage and crafting supplies first, then components, gems and kits.",
        Salvage, Crafting, Components, GemsKeys, Supplies, Scrolls);

    public static readonly StarterLayout Basics = new("basics", "Just the basics",
        "Three packs only: components, salvage, and gems with keys. Everything else stays put.",
        Components, Salvage, GemsKeys);

    /// <summary>Every layout, the one to suggest first.</summary>
    public static readonly StarterLayout[] All = { Everyday, Hunting, CraftingLayout, Basics };

    public static StarterLayout? Find(string? key)
    {
        foreach (StarterLayout l in All)
            if (string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    /// <summary>The layout fitted to <paramref name="inv"/>'s side packs (see the class summary).</summary>
    public static LayoutPlan Fit(StarterLayout layout, SortInventory inv)
    {
        var plan = new LayoutPlan { Layout = layout };
        int packs = inv.Packs.Count;
        int kinds = layout.Groups.Length;
        var packOf = new int[kinds];          // 1-based, 0 = no pack
        var taken = new bool[packs + 1];

        // How many of each kind each pack holds now.
        var score = new int[kinds, packs + 1];
        for (int p = 1; p <= packs; p++)
            foreach (SortItem it in inv.Packs[p - 1].Items)
                for (int g = 0; g < kinds; g++)
                    if (layout.Groups[g].Takes(it)) { score[g, p]++; break; }

        // Kinds a pack already holds win their packs first, whatever their place in the list: the
        // pack that holds most of a kind gets it (more wanted kinds first on a tie, then the top pack).
        int placed = 0;
        while (placed < packs)
        {
            int bestG = -1, bestP = -1, best = 0;
            for (int g = 0; g < kinds; g++)
            {
                if (packOf[g] != 0) continue;
                for (int p = 1; p <= packs; p++)
                    if (!taken[p] && score[g, p] > best) { best = score[g, p]; bestG = g; bestP = p; }
            }
            if (bestG < 0) break;
            packOf[bestG] = bestP;
            taken[bestP] = true;
            placed++;
        }

        // Then the kinds no pack holds, most wanted first, into the packs with the most room (top first on a tie).
        var free = new List<int>();
        for (int p = 1; p <= packs; p++) if (!taken[p]) free.Add(p);
        free.Sort((a, b) =>
        {
            int ra = Room(inv.Packs[a - 1]), rb = Room(inv.Packs[b - 1]);
            return ra != rb ? rb.CompareTo(ra) : a.CompareTo(b);
        });
        int next = 0;
        for (int g = 0; g < kinds && next < free.Count; g++)
            if (packOf[g] == 0) packOf[g] = free[next++];
        for (; next < free.Count; next++) plan.Spare.Add(free[next]);
        plan.Spare.Sort();

        for (int g = 0; g < kinds; g++)
        {
            if (packOf[g] == 0) { plan.Left.Add(layout.Groups[g]); continue; }
            plan.Placed.Add((layout.Groups[g], packOf[g]));
            plan.AlreadyThere += score[g, packOf[g]];
        }
        plan.Placed.Sort((a, b) => a.Pack.CompareTo(b.Pack));

        foreach (var (group, pack) in plan.Placed)
        {
            SortPack sp = inv.Packs[pack - 1];
            var rule = new SortRule { Pack = pack, PackId = sp.Id, PackNote = PackFix.NoteOf(sp) };
            foreach (int c in group.Classes) rule.AddClass(c);
            plan.Rules.Add(rule);
        }
        return plan;
    }

    private static int Room(SortPack p) => SortPlanner.CapacityOf(p, SortPlanner.SideCapacityFallback) - p.Items.Count;
}
