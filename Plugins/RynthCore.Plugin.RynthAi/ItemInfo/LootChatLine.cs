// LootChatLine.cs — the "[RynthAi] Looted [Keep] ..." chat line, with the item's details the way
// Mag-Tools prints them when it loots something (MagItemInfoSettings.LootChat):
//
//   [RynthAi] Looted [Keep] Blunt Crossbow (rule "(M) Xbow (OD +9)"): Gold, Pierce Crossbow, +22, +162%, ...
//   [RynthAi] Looted [Salvage] Ornate Long Sword (rule "Gold any"): Gold, Craft 6     (Include salvage on)
//   [RynthAi] Looted [Keep] Pyreal                                                     (stackables: plain)
//
// The head stays "[RynthAi] Looted [<action>] <name>": the self-test meta and players' metas match it.
using System;
using System.Collections.Generic;
using System.Text;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

internal static class LootChatLine
{
    /// <summary>How much of an item a loot line describes.</summary>
    public enum Detail
    {
        /// <summary>The plain line: action and name.</summary>
        None,
        /// <summary>Salvage: the rule, material and workmanship.</summary>
        Salvage,
        /// <summary>The rule and the Mag-style fields (the Item Info field settings applied).</summary>
        Full,
    }

    /// <summary>The line every pickup printed before, and still prints when it isn't described.</summary>
    public static string Plain(string action, string name, string where = "") =>
        $"[RynthAi] Looted [{action}] {name}" + (where.Length > 0 ? $" ({where})" : string.Empty);

    /// <summary>
    /// What a pickup's line shows. Only items a loot rule took (Keep, Keep #, Salvage, Read, Sell,
    /// and RynthAi's built-in keeps under those actions) are described; never stackables (pyreals,
    /// trade notes, spell components, anything sold in stacks), and salvage only when asked for.
    /// </summary>
    public static Detail DetailFor(MagItemInfoSettings? s, string action, bool isSalvage, bool stackable, AcObjectClass cls)
    {
        if (s == null || !s.LootChat) return Detail.None;
        if (action is not ("Keep" or "KeepUpTo" or "Salvage" or "Read" or "Sell")) return Detail.None;
        if (stackable || cls is AcObjectClass.Money or AcObjectClass.TradeNote or AcObjectClass.SpellComponent)
            return Detail.None;
        if (isSalvage || action == "Salvage" || cls == AcObjectClass.Salvage)
            return s.LootChatSalvage ? Detail.Salvage : Detail.None;
        return Detail.Full;
    }

    /// <summary>Chat type of a described line.</summary>
    public static int ChatTypeOf(MagItemInfoSettings s) =>
        s.LootChatType == MagItemInfoSettings.SameAsHeader ? s.ChatType : s.LootChatType;

    /// <summary>
    /// One line for one pickup. <paramref name="where"/> is "" for a corpse or chest, "ground" for
    /// ground loot; <paramref name="identified"/> false adds "not IDed" (the stats may be short).
    /// </summary>
    public static string Format(Detail detail, MagItemInfoSettings? s, IItemPropertySource? item,
        string action, string name, string rule, string where = "", bool identified = true)
    {
        if (detail == Detail.None || s == null || item == null) return Plain(action, name, where);

        MagItemInfoOptions options = MagItemInfoOptions.From(s);
        // Salvage: everything off but material and workmanship (still only if those are on).
        if (detail == Detail.Salvage)
            options = options with
            {
                Hidden = options.Hidden | ~(MagItemInfoField.Material | MagItemInfoField.Craft),
                ShowValueAndBurden = false,
            };
        MagItemDescription d = MagItemDescriber.Build(item, options);

        var fields = new List<string>(d.Segments.Count + 3);
        if (d.Material.Length > 0 && !name.Contains(d.Material, StringComparison.OrdinalIgnoreCase))
            fields.Add(d.Material);
        if (d.TypeMastery.Length > 0) fields.Add(d.TypeMastery);
        foreach (var (_, text) in d.Segments) fields.Add(text);
        if (!identified) fields.Add("not IDed");

        var sb = new StringBuilder(Plain(action, name), 256);
        string ruleText = rule.Length > 0 ? $"rule \"{rule}\"" : string.Empty;
        if (where.Length > 0 && ruleText.Length > 0) sb.Append(" (").Append(where).Append(", ").Append(ruleText).Append(')');
        else if (where.Length > 0 || ruleText.Length > 0) sb.Append(" (").Append(where).Append(ruleText).Append(')');
        if (fields.Count > 0) sb.Append(": ").Append(string.Join(", ", fields));
        return sb.ToString();
    }
}
