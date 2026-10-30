using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthDefective.Items;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>One row of the weapon picker.</summary>
internal sealed record PickerRow(CarriedItem Item, WeaponGroup Group, string Element, bool InHand, bool Favourite);

/// <summary>A picker group: a kind of weapon and the rows under it.</summary>
internal sealed record PickerGroup(WeaponGroup Group, List<PickerRow> Rows);

/// <summary>What the picker shows: favourites and recent weapons first, then every kind.</summary>
internal sealed class PickerModel
{
    public List<PickerRow> Favourites = new();
    public List<PickerRow> Recent = new();
    public List<PickerGroup> Groups = new();
    /// <summary>Weapons carried (before the search and kind filter).</summary>
    public int Total;
    /// <summary>Rows that passed the search and the kind filter.</summary>
    public int Shown;
}

/// <summary>
/// The weapon picker's list, pure: the carried weapons and wands grouped by kind (melee, missile,
/// war magic, void magic, then shields and ammunition), narrowed by the search words and the kind
/// filter. Favourites and the recently wielded come first, as shortcuts: they are also listed under
/// their kind, so a weapon is always where its kind says. Inside a group, A-Z by full name.
/// </summary>
internal static class WeaponPicker
{
    public static PickerModel Build(IReadOnlyList<CarriedItem> items, IReadOnlyList<ItemRef> favourites,
        IReadOnlyList<ItemRef> recent, string search, KindFilter filter)
    {
        var model = new PickerModel();
        string[] words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var rows = new List<PickerRow>();
        foreach (CarriedItem it in items)
        {
            ItemKind kind = ItemKinds.Classify(it);
            if (ItemKinds.GroupOf(kind) is not { } group) continue;
            model.Total++;
            if (!PassesFilter(group, filter)) continue;
            if (!MatchesSearch(it, group, words)) continue;
            rows.Add(new PickerRow(it, group, ItemKinds.Element(it), it.Wielded, IsFavourite(it, favourites)));
        }
        model.Shown = rows.Count;
        rows.Sort(CompareRows);

        foreach (WeaponGroup g in Enum.GetValues<WeaponGroup>())
        {
            var inGroup = rows.Where(r => r.Group == g).ToList();
            if (inGroup.Count > 0) model.Groups.Add(new PickerGroup(g, inGroup));
        }

        // Shortcuts keep their own order: favourites as the player listed them, recent newest first.
        foreach (ItemRef f in favourites)
            AddFirstMatch(model.Favourites, rows, f);
        foreach (ItemRef r in recent)
            AddFirstMatch(model.Recent, rows, r);
        return model;
    }

    private static void AddFirstMatch(List<PickerRow> into, List<PickerRow> rows, ItemRef r)
    {
        PickerRow? best = null;
        foreach (PickerRow row in rows)
        {
            if (!r.Matches(row.Item)) continue;
            if (row.Item.Id == r.Id) { best = row; break; }
            best ??= row;
        }
        if (best != null && !into.Contains(best)) into.Add(best);
    }

    public static bool IsFavourite(CarriedItem it, IReadOnlyList<ItemRef> favourites)
    {
        foreach (ItemRef f in favourites)
            if (f.Id == it.Id || f.Matches(it)) return true;
        return false;
    }

    private static bool PassesFilter(WeaponGroup g, KindFilter f) => f switch
    {
        KindFilter.Melee => g == WeaponGroup.Melee,
        KindFilter.Missile => g == WeaponGroup.Missile || g == WeaponGroup.Ammo,
        KindFilter.War => g == WeaponGroup.WarMagic,
        KindFilter.Void => g == WeaponGroup.VoidMagic,
        _ => true,
    };

    /// <summary>Every search word is in the weapon's full name, its element or its kind's label.</summary>
    private static bool MatchesSearch(CarriedItem it, WeaponGroup group, string[] words)
    {
        if (words.Length == 0) return true;
        string full = it.FullName;
        string element = ItemKinds.Element(it);
        string label = ItemKinds.Label(group);
        foreach (string w in words)
        {
            if (full.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (element.Length > 0 && element.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (label.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) continue;
            return false;
        }
        return true;
    }

    private static int CompareRows(PickerRow a, PickerRow b)
    {
        int c = string.Compare(a.Item.FullName, b.Item.FullName, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : a.Item.Id.CompareTo(b.Item.Id);
    }

    public static string KindLabel(KindFilter f) => f switch
    {
        KindFilter.Melee => "Melee",
        KindFilter.Missile => "Missile",
        KindFilter.War => "War",
        KindFilter.Void => "Void",
        _ => "All",
    };
}
