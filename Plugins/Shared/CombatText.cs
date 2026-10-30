// ============================================================================
//  RynthSuite - Plugins/Shared/CombatText.cs
//  Reading YOUR hits out of the combat log: who was hit and for how much.
//  Compiled into RynthAi and RynthTracker (linked source), so both read the
//  same lines the same way.
//
//  A spell that hits several monsters (a ring, a wall, a blast, or a chain
//  spell on servers that have them) sends one line per monster it hurt, each
//  naming that monster:
//      ACE:     "You blast Drudge Skulker for 153 points with Whirling Blade VII."
//      retail:  "You scorch Mosswart Worshipper for 1,234 points of fire damage!"
//  with optional prefixes ("Critical hit! ", "Sneak Attack! ", "Overpower! ").
//  The victim's name is what tells the lines apart; nothing else in them does.
//
//  Note (ACE SpellProjectile.DamageTarget): the line is sent only while the
//  target is still alive. The killing blow sends the killer notification
//  instead, so a monster killed by its first hit has no damage line at all.
// ============================================================================
using System;

namespace RynthCore.Plugin.Shared;

internal static class CombatText
{
    /// <summary>
    /// Reads one of OUR damage lines. True with the victim's name (no leading "the") and the
    /// amount; false for anything else: a monster hitting us (or our body part), drains and
    /// heals ("points of mana"), experience, evades. Allocation-light: one Substring for the name.
    /// </summary>
    public static bool TryParseOutgoingHit(string? text, out string victim, out int amount)
    {
        victim = "";
        amount = 0;
        if (string.IsNullOrEmpty(text)) return false;

        int start = SkipPrefixes(text);
        if (!StartsWithWord(text, start, "You")) return false;
        int verbStart = start + 3;
        while (verbStart < text.Length && text[verbStart] == ' ') verbStart++;
        // The verb is one word ("blast", "gore", "scorch", "slash"...).
        int verbEnd = verbStart;
        while (verbEnd < text.Length && char.IsLetter(text[verbEnd])) verbEnd++;
        if (verbEnd == verbStart || verbEnd >= text.Length || text[verbEnd] != ' ') return false;

        int pts = text.IndexOf(" points", verbEnd, StringComparison.OrdinalIgnoreCase);
        if (pts < 0) return false;

        // What follows "points" decides whether it is damage.
        int w = pts + 7;
        while (w < text.Length && text[w] == ' ') w++;
        bool nextWith = WordAt(text, w, "with");
        bool nextOf = WordAt(text, w, "of");
        bool isDamage = nextWith || (nextOf && text.IndexOf("damage", w, StringComparison.OrdinalIgnoreCase) >= 0);
        if (!isDamage) return false;

        // The number right before " points".
        int i = pts - 1;
        long val = 0, mult = 1;
        bool any = false;
        while (i >= 0 && (char.IsDigit(text[i]) || text[i] == ','))
        {
            if (text[i] != ',') { val += (text[i] - '0') * mult; mult *= 10; any = true; }
            i--;
        }
        if (!any || val <= 0 || val > 1_000_000) return false;

        // "... <victim> for <n>": the victim runs from after the verb to " for ".
        const string forWord = " for ";
        int forAt = i - forWord.Length + 1;
        if (forAt <= verbEnd || string.CompareOrdinal(text, forAt, forWord, 0, forWord.Length) != 0) return false;
        int nameStart = verbEnd + 1;
        if (StartsWithWord(text, nameStart, "the")) nameStart += 4;
        if (forAt <= nameStart) return false;
        string name = text.Substring(nameStart, forAt - nameStart).Trim();
        // "You ... yourself", "your ..." are not monsters.
        if (name.Length == 0 || name.Equals("yourself", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("your ", StringComparison.OrdinalIgnoreCase))
            return false;

        victim = name;
        amount = (int)val;
        return true;
    }

    /// <summary>The monster's name out of a corpse's name ("Corpse of Drudge Skulker"), else null.</summary>
    public static string? CorpseOwnerName(string? corpseName)
    {
        const string prefix = "Corpse of ";
        if (string.IsNullOrEmpty(corpseName) || !corpseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string n = corpseName.Substring(prefix.Length).Trim();
        return n.Length == 0 ? null : n;
    }

    // "Critical hit! ", "Sneak Attack! ", "Overpower! " (any order, any number): a short run
    // of words ending in '!' before the sentence proper.
    private static int SkipPrefixes(string text)
    {
        int p = 0;
        for (int guard = 0; guard < 4; guard++)
        {
            while (p < text.Length && text[p] == ' ') p++;
            int bang = text.IndexOf('!', p);
            if (bang < 0 || bang - p > 24) break;
            bool words = true;
            for (int k = p; k < bang; k++)
                if (!(char.IsLetter(text[k]) || text[k] == ' ')) { words = false; break; }
            if (!words || bang == p) break;
            // A sentence that itself ends in '!' ("You scorch X for 5 points of fire damage!") is
            // not a prefix: prefixes never contain "You ".
            if (StartsWithWord(text, p, "You")) break;
            p = bang + 1;
        }
        while (p < text.Length && text[p] == ' ') p++;
        return p;
    }

    private static bool StartsWithWord(string text, int pos, string word)
    {
        if (pos < 0 || pos + word.Length > text.Length) return false;
        if (string.Compare(text, pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        int end = pos + word.Length;
        return end >= text.Length || !char.IsLetter(text[end]);
    }

    private static bool WordAt(string text, int pos, string word) => StartsWithWord(text, pos, word);
}
