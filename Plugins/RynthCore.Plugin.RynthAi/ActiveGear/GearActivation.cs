// ============================================================================
//  RynthAi - ActiveGear/GearActivation.cs
//  Is a worn item's magic on, and how long its mana lasts: the logic behind the
//  Active Gear window and /ra gear (RynthAiPlugin.ActiveGear.cs), MagTools'
//  mana tracker in RynthAi.
//
//  An item's spells turn on once it is wielded and the wearer meets its
//  requirements (Arcane Lore for its difficulty, an activation skill), while it
//  has mana. Two places show that they are on:
//    * the player's enchantments: a spell an item casts on its wearer is an
//      enchantment with no end time (the engine reads it as double.MaxValue);
//      a buff the player cast has an end time;
//    * the item's appraisal: its spell book lists the enchantments on the item
//      itself with bit 31 set (the client's "Enchantments:" list), which is
//      where the item's own spells go (armor's Impenetrability, a weapon's
//      Blood Drinker). That list is as old as the last appraisal.
//  The engine doesn't say which item cast an enchantment (its caster field is
//  not read), so a wearer spell is matched by spell id.
//
//  Mana: CurrentMana and MaxMana come from the last appraisal too, and the
//  server sends no change while gear burns mana, so the time left is worked out
//  from the read's age: current / |ManaRate| seconds (ManaRate, float 5, is the
//  mana per second, negative), less the seconds since the appraisal.
//
//  Pure (System only): linked into Tools/RynthCore.RynthAiTests.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthAi.ActiveGear;

/// <summary>Whether an item's magic is on.</summary>
internal enum GearActive { Unknown, Yes, No }

/// <summary>What is known about one worn item (read from the engine by RynthAiPlugin.ActiveGear.cs).</summary>
internal sealed record GearFacts
{
    public string Name { get; init; } = string.Empty;
    /// <summary>Where it is worn (EquipMask bits).</summary>
    public int Slot { get; init; }
    /// <summary>The item has appraisal data (it was identified at some point).</summary>
    public bool Identified { get; init; }
    /// <summary>The appraisal's spell book as read: bit 31 set = an enchantment on the item itself.</summary>
    public IReadOnlyList<uint> RawSpellIds { get; init; } = Array.Empty<uint>();
    public int? CurMana { get; init; }
    public int? MaxMana { get; init; }
    /// <summary>ManaRate (float 5): mana per second, negative while it burns; null = not read.</summary>
    public double? ManaRate { get; init; }
    /// <summary>Seconds since the appraisal the reads come from; null = unknown.</summary>
    public double? ReadAgeSeconds { get; init; }
    /// <summary>Seconds since the item was first seen worn this session; null = unknown.</summary>
    public double? WornForSeconds { get; init; }
    /// <summary>The player's enchantments could be read (the list below is real, even if empty).</summary>
    public bool EnchantmentsReadable { get; init; }
    /// <summary>Spell ids on the player with no end time (cast by worn items).</summary>
    public IReadOnlyCollection<int> PlayerItemSpells { get; init; } = Array.Empty<int>();
    /// <summary>ItemDifficulty (int 109): the Arcane Lore it needs; 0 = none.</summary>
    public int ArcaneLoreNeeded { get; init; }
    /// <summary>The player's Arcane Lore (buffed); null = not read.</summary>
    public int? ArcaneLore { get; init; }
    /// <summary>The activation skill's name ("Heavy Weapons"), its level (int 115; 0 = none) and the player's level of it.</summary>
    public string ActivationSkillName { get; init; } = string.Empty;
    public int ActivationLevel { get; init; }
    public int? ActivationSkill { get; init; }
    /// <summary>ProcSpell (data id 55): the spell it casts when it triggers; 0 = none or not read.</summary>
    public int ProcSpellId { get; init; }
}

/// <summary>The answer for one item.</summary>
internal sealed record GearVerdict(
    GearActive Active,
    string Reason,
    IReadOnlyList<int> SpellIds,
    IReadOnlyList<bool> SpellOn,
    double? SecondsLeft,
    int? ManaPercent)
{
    public int SpellsOn
    {
        get { int n = 0; foreach (bool b in SpellOn) if (b) n++; return n; }
    }
}

internal static class GearActivation
{
    /// <summary>Bit 31 of an appraisal spell id: an enchantment on the item itself.</summary>
    public const uint ActiveOnItemBit = 0x80000000u;

    /// <summary>An enchantment ending this late has no end (the engine reads "no end" as double.MaxValue).</summary>
    public static bool IsEndless(double expiry) => double.IsInfinity(expiry) || expiry >= double.MaxValue / 2;

    /// <summary>The ammunition slot: arrows and bolts carry no spells.</summary>
    public const int AmmoSlot = 0x00800000;

    /// <summary>
    /// True when the item belongs in the list: not identified yet (it may have spells), or it has spells
    /// or mana. Never ammunition.
    /// </summary>
    public static bool Listed(GearFacts f) =>
        f.Slot != AmmoSlot && (!f.Identified || BaseSpells(f.RawSpellIds).Count > 0 || f.MaxMana is > 0);

    /// <summary>The item's own spells (bit 31 stripped, each once, in order) and which carry the on-the-item bit.</summary>
    public static List<int> BaseSpells(IReadOnlyList<uint> raw) => BaseSpells(raw, out _);

    private static List<int> BaseSpells(IReadOnlyList<uint> raw, out HashSet<int> onItem)
    {
        onItem = new HashSet<int>();
        var own = new List<int>();
        var seen = new HashSet<int>();
        foreach (uint r in raw)
        {
            int id = unchecked((int)(r & ~ActiveOnItemBit));
            if (id == 0) continue;
            if ((r & ActiveOnItemBit) != 0) onItem.Add(id);
            else if (seen.Add(id)) own.Add(id);
        }
        // An enchantment someone cast on the item (not one of its own spells) isn't listed;
        // only when the book has nothing but marked ids do those stand for its spells.
        if (own.Count == 0)
            foreach (int id in onItem)
                if (seen.Add(id)) own.Add(id);
        return own;
    }

    /// <summary>
    /// Seconds of mana left: current / |rate|, less the read's age, never below 0. Null without a
    /// current mana or a burning rate.
    /// </summary>
    public static double? SecondsLeft(int? curMana, double? manaRate, double? readAgeSeconds)
    {
        if (curMana is not int cur || manaRate is not double rate) return null;
        if (double.IsNaN(rate) || rate == 0) return null;
        double left = cur / Math.Abs(rate) - Math.Max(0, readAgeSeconds ?? 0);
        return Math.Max(0, left);
    }

    /// <summary>The cloak slot and the three aetheria slots: their one spell is a proc.</summary>
    public const int ProcSlots = 0x08000000 | 0x10000000 | 0x20000000 | 0x40000000;

    /// <summary>
    /// The item's proc spell: ProcSpell when read, else a cloak's or aetheria's only spell (the server
    /// puts ProcSpell in the appraisal's spell book). 0 for none.
    /// </summary>
    public static int ProcSpellOf(GearFacts f, List<int> spells)
    {
        if (f.ProcSpellId != 0) return spells.Contains(f.ProcSpellId) ? f.ProcSpellId : 0;
        return (f.Slot & ProcSlots) != 0 && spells.Count == 1 ? spells[0] : 0;
    }

    /// <summary>Decides one item (see the file note).</summary>
    public static GearVerdict Judge(GearFacts f)
    {
        List<int> spells = BaseSpells(f.RawSpellIds, out HashSet<int> onItem);
        var on = new bool[spells.Count];
        // A proc (a cloak's or aetheria's surge, a weapon's cast on strike) is cast when the item
        // triggers: never a lasting enchantment on the wearer or the item, so it isn't looked for.
        // It shows as on whenever the item is (2026-11-12: cloak and aetheria read "No").
        int proc = ProcSpellOf(f, spells);
        int procAt = proc != 0 ? spells.IndexOf(proc) : -1;
        int needed = spells.Count - (procAt >= 0 ? 1 : 0);
        int seenOnPlayer = 0, seenOnItem = 0;
        for (int i = 0; i < spells.Count; i++)
        {
            if (i == procAt) continue;
            bool p = f.EnchantmentsReadable && Contains(f.PlayerItemSpells, spells[i]);
            bool it = onItem.Contains(spells[i]);
            on[i] = p || it;
            if (p) seenOnPlayer++;
            else if (it) seenOnItem++;
        }
        double? left = f.Identified ? SecondsLeft(f.CurMana, f.ManaRate, f.ReadAgeSeconds) : null;
        int? pct = f.Identified && f.CurMana is int c && f.MaxMana is int m && m > 0
            ? (int)Math.Clamp(c * 100L / m, 0, 100) : null;

        GearVerdict V(GearActive a, string why) => new(a, why, spells, on, left, pct);

        if (!f.Identified)
            return V(GearActive.Unknown, "not identified yet");

        int seen = seenOnPlayer + seenOnItem;
        if (seen > 0)
        {
            // Seen only in the appraisal's list: as old as the read, so a read that has since run dry can't say yes.
            if (seenOnPlayer == 0 && left is double l && l <= 0 && f.ManaRate is double)
                return V(GearActive.Unknown, "its mana may have run out since it was identified");
            string why = seen == needed ? string.Empty : $"{seen} of {needed} spells seen";
            if (f.MaxMana is > 0 && f.CurMana == 0)
                why = Join(why, "the mana read is older than its spells");
            if (procAt >= 0) on[procAt] = true;
            return V(GearActive.Yes, why);
        }

        if (f.MaxMana is > 0 && f.CurMana == 0)
            return V(GearActive.No, "out of mana");
        if (f.ArcaneLoreNeeded > 0 && f.ArcaneLore is int lore && lore < f.ArcaneLoreNeeded)
            return V(GearActive.No, $"needs Arcane Lore {f.ArcaneLoreNeeded}, you have {lore}");
        if (f.ActivationLevel > 0 && f.ActivationSkill is int skill && skill < f.ActivationLevel)
            return V(GearActive.No, $"needs {(f.ActivationSkillName.Length > 0 ? f.ActivationSkillName : "its skill")} {f.ActivationLevel}, you have {skill}");
        // Only a proc: worn, with its mana and requirements met, it is on (it casts when it triggers).
        if (procAt >= 0 && needed == 0)
        {
            on[procAt] = true;
            return V(GearActive.Yes, "casts its spell when it triggers");
        }
        if (spells.Count == 0)
            return V(GearActive.Unknown, "no spells read");
        if (!f.EnchantmentsReadable)
            return V(GearActive.Unknown, "your enchantments can't be read yet");
        // An appraisal from while it was worn would list its own spells on it: none on it or on you is off.
        // An older one (from before it was put on) can't tell.
        if (f.ReadAgeSeconds is double age && f.WornForSeconds is double worn && age < worn)
            return V(GearActive.No, "none of its spells is on you or on it");
        return V(GearActive.Unknown, "its spells aren't seen (Check items reads it again)");
    }

    private static bool Contains(IReadOnlyCollection<int> set, int id)
    {
        if (set is ISet<int> s) return s.Contains(id);
        foreach (int x in set) if (x == id) return true;
        return false;
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "; " + b;

    // ── Words ────────────────────────────────────────────────────────────

    /// <summary>"12 min", "3 h 5 min", "2 d 4 h", "&lt;1 min"; "-" for none.</summary>
    public static string TimeText(double? seconds)
    {
        if (seconds is not double s || double.IsNaN(s)) return "-";
        if (s < 60) return s <= 0 ? "0 min" : "<1 min";
        long min = (long)(s / 60);
        if (min < 60) return min.ToString(CultureInfo.InvariantCulture) + " min";
        long h = min / 60, mm = min % 60;
        if (h < 48) return mm == 0 ? $"{h} h" : $"{h} h {mm} min";
        long d = h / 24, hh = h % 24;
        return hh == 0 ? $"{d} d" : $"{d} d {hh} h";
    }

    /// <summary>"Yes", "No" or "?".</summary>
    public static string ActiveText(GearActive a) => a switch { GearActive.Yes => "Yes", GearActive.No => "No", _ => "?" };

    // The slot a bit names, armor first (a coat covers chest and arms: "Chest"), then the rest.
    private static readonly (int Bit, string Name)[] Slots =
    {
        (0x00000200, "Chest"), (0x00000400, "Abdomen"), (0x00000800, "Upper Arms"), (0x00001000, "Lower Arms"),
        (0x00002000, "Upper Legs"), (0x00004000, "Lower Legs"),
        (0x00000001, "Head"), (0x00000020, "Hands"), (0x00000100, "Feet"),
        (0x00000002, "Shirt"), (0x00000004, "Shirt"), (0x00000008, "Shirt"), (0x00000010, "Shirt"),
        (0x00000040, "Pants"), (0x00000080, "Pants"),
        (0x00008000, "Neck"), (0x00010000, "Left Wrist"), (0x00020000, "Right Wrist"),
        (0x00040000, "Left Ring"), (0x00080000, "Right Ring"),
        (0x00100000, "Melee"), (0x00200000, "Shield"), (0x00400000, "Missile"), (0x00800000, "Ammo"),
        (0x01000000, "Held"), (0x02000000, "Two-handed"), (0x04000000, "Trinket"), (0x08000000, "Cloak"),
        (0x10000000, "Blue Aetheria"), (0x20000000, "Yellow Aetheria"), (0x40000000, "Red Aetheria"),
    };

    /// <summary>The slot's name ("Left Ring"); "Worn" for none known.</summary>
    public static string SlotName(int mask)
    {
        foreach (var (bit, name) in Slots)
            if ((mask & bit) != 0) return name;
        return "Worn";
    }

    /// <summary>The order the window lists slots in (the slot table's order; unknown last).</summary>
    public static int SlotOrder(int mask)
    {
        for (int i = 0; i < Slots.Length; i++)
            if ((mask & Slots[i].Bit) != 0) return i;
        return Slots.Length;
    }

    /// <summary>
    /// The summary line: "9 of 10 items active, lowest: Left Ring 12 min", with the unknown count and
    /// the items that are off ("off: Neck (out of mana)", two at most) when there are any.
    /// </summary>
    public static string Summary(IReadOnlyList<(GearFacts Facts, GearVerdict Verdict)> items)
    {
        if (items.Count == 0) return "No worn items with spells.";
        int yes = 0, unknown = 0;
        var off = new List<string>();
        string lowest = string.Empty;
        double lowestLeft = double.MaxValue;
        string lowestPct = string.Empty;
        int lowestPctValue = int.MaxValue;
        foreach (var (f, v) in items)
        {
            string slot = SlotName(f.Slot);
            switch (v.Active)
            {
                case GearActive.Yes: yes++; break;
                case GearActive.No: off.Add(v.Reason.Length > 0 ? $"{slot} ({v.Reason})" : slot); break;
                default: unknown++; break;
            }
            if (v.Active != GearActive.Yes) continue;
            if (v.SecondsLeft is double s && s < lowestLeft) { lowestLeft = s; lowest = $"{slot} {TimeText(s)}"; }
            if (v.ManaPercent is int p && p < lowestPctValue) { lowestPctValue = p; lowestPct = $"{slot} {p}% mana"; }
        }
        var sb = new StringBuilder();
        sb.Append(yes).Append(" of ").Append(items.Count).Append(items.Count == 1 ? " item active" : " items active");
        if (unknown > 0) sb.Append(", ").Append(unknown).Append(" unknown");
        if (off.Count > 0)
        {
            sb.Append(", off: ").Append(string.Join(", ", off.GetRange(0, Math.Min(2, off.Count))));
            if (off.Count > 2) sb.Append(" and ").Append(off.Count - 2).Append(" more");
        }
        if (lowest.Length > 0) sb.Append(", lowest: ").Append(lowest);
        else if (lowestPct.Length > 0) sb.Append(", lowest: ").Append(lowestPct);
        return sb.ToString();
    }
}
