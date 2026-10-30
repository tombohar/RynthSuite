using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>
/// Ties rules to their packs' ids. Rules saved before RynthSort kept pack ids only had a number,
/// and the Lua script's numbers counted foci as packs (game.Character.Containers: every container
/// in the main pack, in the same order), so after a focus every imported number pointed one pack
/// too far. Once per character, on a settled read of the packs (<see cref="Apply"/>):
/// <list type="bullet">
/// <item>a rule still as the Lua script had it: Lua number n is the n-th container, foci counted.
/// A pack: the rule takes its id and its number among the real packs. A focus, or nothing there:
/// left as it was, and said so.</item>
/// <item>any other rule (picked in RynthSort's window, or the defaults): its number already counts
/// real packs only, so it takes that pack's id.</item>
/// </list>
/// Pure: the tests drive it with hand-made inventories.
/// </summary>
internal static class PackFix
{
    /// <summary>Every side-slot entry's capacity was readable (a focus and a pack can be told apart).</summary>
    public static bool Ready(SortInventory inv)
    {
        foreach (SortSlot s in inv.Slots)
            if (s.Capacity < 0) return false;
        return true;
    }

    /// <summary>The side-slot layout (ids and capacities): two reads alike mean the packs have settled.</summary>
    public static string Layout(SortInventory inv)
    {
        var sb = new StringBuilder(inv.Slots.Count * 12);
        foreach (SortSlot s in inv.Slots) sb.Append(s.Id.ToString("X8")).Append(':').Append(s.Capacity).Append(';');
        return sb.ToString();
    }

    /// <summary>"Sack - Salvage, Gems".</summary>
    public static string NoteOf(SortPack p) => $"{(p.Name.Length > 0 ? p.Name : "Pack")} - {p.Summary()}";

    /// <summary>
    /// The one-time fix over <paramref name="rules"/> (only rules without a pack id are looked at).
    /// <paramref name="imported"/>: the rules came from the Lua script. <paramref name="luaRules"/>:
    /// the Lua script's rules as they are in its file (null when it can't be read: every rule is
    /// taken as imported). Returns one line per rule it looked at, saying what it did; changed
    /// counts the rules it changed.
    /// </summary>
    public static List<string> Apply(IReadOnlyList<SortRule> rules, SortInventory inv, bool imported, IReadOnlyList<SortRule>? luaRules, out int changed)
    {
        var lines = new List<string>();
        changed = 0;
        for (int i = 0; i < rules.Count; i++)
        {
            SortRule r = rules[i];
            if (r.PackId != 0) continue;
            string who = $"rule {i + 1} ({SortRules.Describe(r)})";
            if (imported && (luaRules == null || Contains(luaRules, r)))
            {
                int n = r.Pack;
                if (n < 1 || n > inv.Slots.Count)
                {
                    lines.Add($"{who}: the Lua script's pack {n} doesn't exist ({Count(inv)}); left as pack {n}");
                    continue;
                }
                SortSlot slot = inv.Slots[n - 1];
                int idx = inv.IndexOf(slot.Id);
                if (!slot.IsPack || idx == 0)
                {
                    lines.Add($"{who}: the Lua script's pack {n} is {(slot.IsPack ? "not a side pack" : "a focus")} (0x{slot.Id:X8}); left as pack {n}");
                    continue;
                }
                SortPack p = inv.Packs[idx - 1];
                r.PackId = p.Id;
                r.Pack = idx;
                r.PackNote = NoteOf(p);
                changed++;
                lines.Add($"{who}: the Lua script's pack {n} -> pack {idx}, {r.PackNote} (0x{p.Id:X8})");
            }
            else
            {
                if (r.Pack < 1 || r.Pack > inv.Packs.Count)
                {
                    lines.Add($"{who}: pack {r.Pack}, which you don't have; left as it is");
                    continue;
                }
                SortPack p = inv.Packs[r.Pack - 1];
                r.PackId = p.Id;
                r.PackNote = NoteOf(p);
                changed++;
                lines.Add($"{who}: pack {r.Pack} kept, now tied to {r.PackNote} (0x{p.Id:X8})");
            }
        }
        return lines;
    }

    /// <summary>
    /// Keeps each rule's number (and note) on its pack when that pack is carried, so the number
    /// the window shows and the fallback stay right as packs come and go. True when one changed.
    /// </summary>
    public static bool Sync(IReadOnlyList<SortRule> rules, SortInventory inv)
    {
        bool any = false;
        foreach (SortRule r in rules)
        {
            int idx = inv.IndexOf(r.PackId);
            if (idx == 0) continue;
            if (r.Pack != idx || r.PackNote.Length == 0)
            {
                r.Pack = idx;
                r.PackNote = NoteOf(inv.Packs[idx - 1]);
                any = true;
            }
        }
        return any;
    }

    private static bool Contains(IReadOnlyList<SortRule> list, SortRule r)
    {
        foreach (SortRule o in list)
            if (o.SameAs(r)) return true;
        return false;
    }

    private static string Count(SortInventory inv)
    {
        int foci = 0;
        foreach (SortSlot s in inv.Slots) if (!s.IsPack) foci++;
        return $"{inv.Packs.Count} pack{(inv.Packs.Count == 1 ? "" : "s")} and {foci} foc{(foci == 1 ? "us" : "i")}";
    }
}
