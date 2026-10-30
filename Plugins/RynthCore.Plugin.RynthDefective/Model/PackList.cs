using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>One line of the "pick from your pack" list: an item kind and how many of it are carried.</summary>
internal sealed record PackChoice(CarriedItem Item, int Count);

internal static class PackList
{
    /// <summary>
    /// The carried items for a slot to choose from: one line per kind of item (the same name, wcid
    /// and material), with the stack sizes added up, A-Z, narrowed by the search words (all of them
    /// must be in the name). At most <paramref name="max"/> lines; <paramref name="total"/> says how many matched.
    /// </summary>
    public static List<PackChoice> Build(IReadOnlyList<CarriedItem> items, string search, int max, out int total)
    {
        string[] words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var byKind = new Dictionary<string, (CarriedItem Rep, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (CarriedItem it in items)
        {
            string full = it.FullName;
            bool all = true;
            foreach (string w in words)
                if (full.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
            if (!all) continue;
            string key = $"{full}|{it.Wcid}";
            if (byKind.TryGetValue(key, out var have))
            {
                // The representative: not in hand, then the smaller stack (what a slot would pick).
                CarriedItem rep = have.Rep;
                if ((rep.Wielded && !it.Wielded) || (rep.Wielded == it.Wielded && it.Stack < rep.Stack)) rep = it;
                byKind[key] = (rep, have.Count + Math.Max(1, it.Stack));
            }
            else byKind[key] = (it, Math.Max(1, it.Stack));
        }
        var list = new List<PackChoice>(byKind.Count);
        foreach (var kv in byKind) list.Add(new PackChoice(kv.Value.Rep, kv.Value.Count));
        list.Sort((a, b) => string.Compare(a.Item.FullName, b.Item.FullName, StringComparison.OrdinalIgnoreCase));
        total = list.Count;
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }
}
