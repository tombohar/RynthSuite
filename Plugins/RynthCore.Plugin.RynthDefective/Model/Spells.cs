// ============================================================================
//  RynthDefective - Model/Spells.cs
//  Spell slots (2026-10-28): what a spell is called, whom it is cast on, and
//  finding one of the character's spells by name. Pure (the tests run it).
//
//  The engine's plugin API has no spell names or targets and no way to read
//  the spell selected in AC's spellbook or spell bar, so the names and the
//  "self-targeted" / "harmful" flags come from RynthAi's spell table
//  (Combat\SpellData.txt, linked in as RynthInventory and RynthOracle link it:
//  id, name, category, family, words, duration, difficulty, then three flags,
//  the second harmful, the third self-targeted), and the player picks a spell
//  by name from the ones the character knows (ReadKnownSpells).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>One spell: its name, and whether it is cast on yourself only (recalls, Self buffs) or harms its target (war, debuffs).</summary>
internal readonly record struct SpellInfo(uint Id, string Name, bool SelfTargeted, bool Harmful);

internal static class SpellTable
{
    private const string ResourceName = "RynthDefective.SpellData.txt";
    private static Dictionary<uint, SpellInfo>? _byId;

    public static bool TryGet(uint id, out SpellInfo info)
    {
        EnsureLoaded();
        return _byId!.TryGetValue(id, out info);
    }

    /// <summary>The spell's name, or "Spell 1234" for one the table doesn't have (a server's own).</summary>
    public static string Name(uint id) => TryGet(id, out SpellInfo s) ? s.Name : "Spell " + id.ToString(CultureInfo.InvariantCulture);

    public static int Count
    {
        get { EnsureLoaded(); return _byId!.Count; }
    }

    private static void EnsureLoaded()
    {
        if (_byId != null) return;
        Dictionary<uint, SpellInfo> map;
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s == null) map = new Dictionary<uint, SpellInfo>();
            else
            {
                using var reader = new StreamReader(s, detectEncodingFromByteOrderMarks: true);
                map = Parse(reader);
            }
        }
        catch
        {
            map = new Dictionary<uint, SpellInfo>();   // a missing table costs names ("Spell 1234") and targets (by the selection)
        }
        _byId = map;
    }

    /// <summary>The table's lines (the tests read their own).</summary>
    internal static Dictionary<uint, SpellInfo> Parse(TextReader reader)
    {
        var map = new Dictionary<uint, SpellInfo>(6400);
        while (reader.ReadLine() is string line)
        {
            string[] c = line.Split('\t');
            if (c.Length < 2 || !uint.TryParse(c[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0) continue;
            string name = c[1].Trim();
            if (name.Length == 0) continue;
            bool harmful = c.Length > 8 && c[8].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            bool self = c.Length > 9 && c[9].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            map[id] = new SpellInfo(id, name, self, harmful);
        }
        return map;
    }

    /// <summary>The character's spells (ids) with names, A-Z; ones the table doesn't name are listed by number.</summary>
    public static SpellInfo[] Known(ReadOnlySpan<uint> ids)
    {
        var list = new List<SpellInfo>(ids.Length);
        var seen = new HashSet<uint>();
        foreach (uint id in ids)
        {
            if (id == 0 || !seen.Add(id)) continue;
            list.Add(TryGet(id, out SpellInfo s) ? s : new SpellInfo(id, Name(id), false, false));
        }
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        return list.ToArray();
    }

    /// <summary>
    /// The spells in <paramref name="known"/> that match what the player typed, best first: the exact
    /// name, then names starting with it, then names holding every word of it ("heal self", "vi heal").
    /// At most <paramref name="max"/>; <paramref name="total"/> is how many matched. Blank = all of them.
    /// </summary>
    public static List<SpellInfo> Search(IReadOnlyList<SpellInfo> known, string? query, int max, out int total)
    {
        string q = (query ?? string.Empty).Trim();
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var exact = new List<SpellInfo>();
        var starts = new List<SpellInfo>();
        var contains = new List<SpellInfo>();
        foreach (SpellInfo s in known)
        {
            if (q.Length == 0) { contains.Add(s); continue; }
            if (s.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) exact.Add(s);
            else if (s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) starts.Add(s);
            else if (AllWords(s.Name, words)) contains.Add(s);
        }
        total = exact.Count + starts.Count + contains.Count;
        var result = new List<SpellInfo>(Math.Min(total, Math.Max(0, max)));
        foreach (List<SpellInfo> group in new[] { exact, starts, contains })
            foreach (SpellInfo s in group)
            {
                if (result.Count >= max) return result;
                result.Add(s);
            }
        return result;
    }

    private static bool AllWords(string name, string[] words)
    {
        if (words.Length == 0) return false;
        foreach (string w in words)
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }
}

/// <summary>Whom a spell slot's cast goes to.</summary>
internal enum CastAt { Yourself, Selection, Refused }

internal static class SpellTargeting
{
    /// <summary>
    /// The target for casting <paramref name="spell"/> now. A self-targeted spell (a recall, a Self buff)
    /// is cast on yourself whatever is selected. Any other spell goes on the selection; with nothing
    /// selected a beneficial one (Heal Other, a buff, an item spell) is cast on yourself, a harmful one
    /// (war, void, a debuff) is refused, and so is a harmful one with yourself selected. A spell the table
    /// doesn't know goes on the selection, else on yourself. <paramref name="why"/> says why it was refused.
    /// </summary>
    public static CastAt Decide(SpellInfo? spell, uint selected, uint player, out uint target, out string why)
    {
        why = string.Empty;
        target = 0;
        if (player == 0)
        {
            why = "the character isn't ready";
            return CastAt.Refused;
        }
        if (spell is { SelfTargeted: true })
        {
            target = player;
            return CastAt.Yourself;
        }
        bool harmful = spell is { Harmful: true };
        if (selected != 0 && selected != player)
        {
            target = selected;
            return CastAt.Selection;
        }
        if (harmful)
        {
            why = selected == player ? "select a monster first (you have yourself selected)" : "select a target first";
            return CastAt.Refused;
        }
        target = player;
        return CastAt.Yourself;
    }

    /// <summary>"cast on you" / "cast on the selection (else on you)" / "cast on the selected monster", for the slot's tooltip.</summary>
    public static string Describe(SpellInfo? spell) => spell switch
    {
        { SelfTargeted: true } => "cast on you",
        { Harmful: true } => "cast on the selected target",
        _ => "cast on the selected target, or on you with nothing selected",
    };
}
