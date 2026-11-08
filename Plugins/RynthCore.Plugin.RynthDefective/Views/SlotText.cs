using System;
using System.Text;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;

namespace RynthCore.Plugin.RynthDefective.Views;

/// <summary>The words a slot shows: the count under its button, its key, and its tooltip. Pure.</summary>
internal static class SlotText
{
    public static readonly uint Missing = UiFormat.Rgb(225, 95, 95);
    public static readonly uint InHand = UiFormat.Rgb(110, 210, 110);
    public static readonly uint Plain = UiFormat.Rgb(220, 220, 220);
    public static readonly uint KeyColor = UiFormat.Rgb(150, 185, 235);
    public static readonly uint Waiting = UiFormat.Rgb(255, 191, 64);

    /// <summary>The line under a slot button: uses left (kits), the count (stacks, several of a kind), "in hand", or "none".</summary>
    public static string Count(SlotStatus s, out uint color)
    {
        color = Plain;
        if (s.Item == null)
        {
            color = Missing;
            return "none";
        }
        CarriedItem it = s.Item;
        if (it.Wielded)
        {
            color = InHand;
            return ItemKinds.IsWeapon(ItemKinds.Classify(it)) ? "in hand" : "worn";
        }
        if (it.MaxUses > 0) return s.Uses.ToString();
        if (it.MaxStack > 1 || s.Count > 1) return "x" + s.Count;
        return string.Empty;
    }

    /// <summary>A key as the cell under a slot has room for: "Ctrl+F9" is "C+F9".</summary>
    public static string ShortKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        string k = key.EndsWith(" (off)", StringComparison.Ordinal) ? key[..^6] : key;
        return k.Replace("Ctrl+", "C+").Replace("Alt+", "A+").Replace("Shift+", "S+");
    }

    /// <summary>Up to <paramref name="max"/> letters for a slot drawn as text (icons off): "Peerless Healing Kit" is "PHK".</summary>
    public static string Abbreviation(string name, int max = 4)
    {
        var sb = new StringBuilder();
        foreach (string word in (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length == 0 || !char.IsLetterOrDigit(word[0])) continue;
            sb.Append(char.ToUpperInvariant(word[0]));
            if (sb.Length >= max) break;
        }
        return sb.Length == 0 ? "?" : sb.ToString();
    }

    /// <param name="keys">The slot can have a key (an extra Shortcuts bar's can't: clicks only).</param>
    public static string Tooltip(int number, ItemRef? item, SlotStatus s, UseMode mode, string? key, bool keys = true)
    {
        if (item == null) return $"Slot {number}: empty. Click to set it up.";
        var sb = new StringBuilder();
        sb.Append("Slot ").Append(number).Append(": ").Append(s.Item?.FullName ?? item.FullName);
        if (s.Item == null)
        {
            sb.Append("\nNot in your pack - nothing to use. It comes back by itself when you carry one of the same name.");
        }
        else
        {
            ItemKind kind = ItemKinds.Classify(s.Item);
            UseAction action = UseModes.ActionFor(mode, kind);
            sb.Append(keys ? "\nClick or press the key: " : "\nClick: ").Append(UseModes.Describe(action).Replace("used", "use it").Replace("wielded", "wield it"));
            if (s.Item.MaxUses > 0) sb.Append("\n").Append(s.Uses).Append(" uses left");
            if (s.Count > 1 || s.Item.MaxStack > 1) sb.Append(s.Item.MaxUses > 0 ? ", " : "\n").Append(s.Count).Append(" carried");
            if (s.Item.Wielded) sb.Append("\nIn your hands now.");
        }
        if (keys) sb.Append(key != null ? "\nKey: " + key : "\nNo key yet (Edit slots).");
        return sb.ToString();
    }
}
