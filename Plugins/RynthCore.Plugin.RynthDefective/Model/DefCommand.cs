using System;

namespace RynthCore.Plugin.RynthDefective.Model;

internal enum DefVerb { Toggle, Help, Use, Set, Clear, Bind, Unbind, Weapons, Favourite, Slots, List, Status, Error, Panels, Selection, Settings, Spell, Corpses,
    Quantity, Split, Give, Buy }

/// <summary>
/// A parsed "/rdef ..." line. Use: <see cref="Slot"/> (1-based) or <see cref="Text"/> (an item name).
/// The quantity box's verbs (qty, split, give, buy): <see cref="Amount"/> (null = the box's number) and
/// <see cref="Text"/> (split: the pack; buy: the item's name).
/// </summary>
internal sealed record DefCommand(DefVerb Verb, int Slot = 0, string Text = "", string Error = "", QtyAmount? Amount = null);

internal static class DefCommandParser
{
    public const string Usage =
        "/rdef [use <slot or item name> | set <slot> [item name] | spell <slot> <spell name> | clear <slot> | bind <slot> <key> | unbind <slot> | "
        + "weapons [search] | fav | slots <count> | list | status | settings | panels (the game panel buttons) | selection | corpses [clear] | "
        + "qty [number|max|half] | split [number] [into <pack>] | give [number] | buy [number] [item name]]";

    public const string QtyUsage = "Usage: /rdef qty <number|max|half>, e.g. /rdef qty 25 (the Selection window's number)";
    public const string SplitUsage = "Usage: /rdef split [number|max|half] [into <pack>], e.g. /rdef split 25  or  /rdef split 25 into Sack";
    public const string GiveUsage = "Usage: /rdef give [number|max|half], e.g. /rdef give 10 (to the last player or NPC you selected)";
    public const string BuyUsage = "Usage: /rdef buy [number|max|half] [item name], e.g. /rdef buy 25  or  /rdef buy 25 Mana Scarab (a vendor open)";

    /// <summary>Parses the arguments after "/rdef" (blank = show or hide the bar).</summary>
    public static DefCommand Parse(string? args)
    {
        string a = (args ?? string.Empty).Trim();
        if (a.Length == 0) return new DefCommand(DefVerb.Toggle);
        int sp = a.IndexOf(' ');
        string verb = (sp < 0 ? a : a[..sp]).ToLowerInvariant();
        string rest = sp < 0 ? string.Empty : a[(sp + 1)..].Trim();

        switch (verb)
        {
            case "help": case "?": return new DefCommand(DefVerb.Help);
            case "list": case "ls": return new DefCommand(DefVerb.List);
            case "status": return new DefCommand(DefVerb.Status);
            case "panels": case "panel": return new DefCommand(DefVerb.Panels);
            case "settings": case "options": case "setup": return new DefCommand(DefVerb.Settings);
            case "corpses": case "corpse": return new DefCommand(DefVerb.Corpses, Text: rest);
            case "selection": case "target":
                // "/rdef selection 0x80001234": select that object (a test hook for the Selection window).
                return new DefCommand(DefVerb.Selection, Text: rest);
            case "fav": case "favourite": case "favorite": return new DefCommand(DefVerb.Favourite);
            case "weapons": case "weapon": case "picker": return new DefCommand(DefVerb.Weapons, Text: rest);
            case "use": case "wield": case "equip":
                if (rest.Length == 0) return Fail($"Use which slot or item? e.g. /rdef use 1  or  /rdef use Peerless Healing Kit");
                return TrySlot(rest, out int n) ? new DefCommand(DefVerb.Use, n) : new DefCommand(DefVerb.Use, Text: rest);
            case "set":
            {
                SplitSlot(rest, out string? slotText, out string tail);
                if (slotText == null || !TrySlot(slotText, out int slot)) return Fail("Which slot? e.g. /rdef set 3  (the item selected in the game goes in slot 3) or /rdef set 3 Peerless Healing Kit");
                return new DefCommand(DefVerb.Set, slot, tail);
            }
            case "spell":
            {
                SplitSlot(rest, out string? slotText, out string tail);
                if (slotText == null || !TrySlot(slotText, out int slot) || tail.Length == 0)
                    return Fail("Usage: /rdef spell <slot> <spell name>, e.g. /rdef spell 3 Heal Self VI (one of your spells; the slot casts it)");
                return new DefCommand(DefVerb.Spell, slot, tail);
            }
            case "clear":
            {
                return TrySlot(rest, out int slot) ? new DefCommand(DefVerb.Clear, slot) : Fail("Which slot? e.g. /rdef clear 3");
            }
            case "bind":
            {
                SplitSlot(rest, out string? slotText, out string key);
                if (slotText == null || !TrySlot(slotText, out int slot) || key.Length == 0)
                    return Fail("Usage: /rdef bind <slot> <key>, e.g. /rdef bind 3 F9  or  /rdef bind 3 Ctrl+1");
                return new DefCommand(DefVerb.Bind, slot, key);
            }
            case "unbind":
                return TrySlot(rest, out int us) ? new DefCommand(DefVerb.Unbind, us) : Fail("Which slot? e.g. /rdef unbind 3");
            case "slots":
                return int.TryParse(rest, out int count) && count >= DefSettings.SlotsMin && count <= DefSettings.SlotCountMax
                    ? new DefCommand(DefVerb.Slots, count)
                    : Fail($"How many slots? {DefSettings.SlotsMin} to {DefSettings.SlotCountMax}, e.g. /rdef slots 10");
            // The quantity box (2026-10-30, Quantity/QuantityController.cs).
            case "qty": case "quantity": case "amount":
            {
                if (rest.Length == 0) return new DefCommand(DefVerb.Quantity);
                return QuantityRules.TryParse(rest, out QtyAmount q, out string why)
                    ? new DefCommand(DefVerb.Quantity, Amount: q)
                    : Fail(why + " " + QtyUsage);
            }
            case "split":
            {
                if (!QuantityRules.TryParseLeading(rest, out QtyAmount? q, out string tail, out string why)) return Fail(why + " " + SplitUsage);
                string into = StripWord(tail, "into", "in", "to");
                return new DefCommand(DefVerb.Split, Text: into, Amount: q);
            }
            case "give":
            {
                if (!QuantityRules.TryParseLeading(rest, out QtyAmount? q, out string tail, out string why)) return Fail(why + " " + GiveUsage);
                if (tail.Length > 0) return Fail($"'{tail}' isn't a quantity. {GiveUsage}");
                return new DefCommand(DefVerb.Give, Amount: q);
            }
            case "buy":
            {
                if (!QuantityRules.TryParseLeading(rest, out QtyAmount? q, out string tail, out string why)) return Fail(why + " " + BuyUsage);
                return new DefCommand(DefVerb.Buy, Text: StripWord(tail, "of"), Amount: q);
            }
            default:
                // "/rdef Stamina Kit" is a shorthand for use.
                return new DefCommand(DefVerb.Use, Text: a);
        }
    }

    /// <summary>The slot number a hotkey runs for slot <paramref name="slot"/> (1-based).</summary>
    public static string UseCommand(int slot) => "/rdef use " + slot;

    private static bool TrySlot(string text, out int slot)
    {
        slot = 0;
        return int.TryParse(text.Trim(), out slot) && slot >= 1 && slot <= DefSettings.SlotsMax;
    }

    private static void SplitSlot(string rest, out string? first, out string tail)
    {
        int sp = rest.IndexOf(' ');
        first = rest.Length == 0 ? null : (sp < 0 ? rest : rest[..sp]);
        tail = sp < 0 ? string.Empty : rest[(sp + 1)..].Trim();
    }

    private static DefCommand Fail(string error) => new(DefVerb.Error, Error: error);

    /// <summary>The text without a leading word ("into Sack" -> "Sack").</summary>
    private static string StripWord(string text, params string[] words)
    {
        string t = text.Trim();
        foreach (string w in words)
            if (t.Length > w.Length && t.StartsWith(w, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(t[w.Length]))
                return t[(w.Length + 1)..].Trim();
        return t;
    }
}
