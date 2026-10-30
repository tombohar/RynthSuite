using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Which combat pet to summon for a monster. The Damage panel stores a choice per
/// monster (wcid): Auto (""), an element ("E:Cold"), or a specific essence
/// ("I:&lt;id&gt;"). Auto picks an element from, in order: the Monsters-tab rule
/// (Pet damage, else damage type), the monster's learned resists, then the
/// element that has done best on it in the Damage tab; with none of those the
/// Items-panel order is kept. Combat pets can't be swapped while one is out
/// (ACE: "already active"), so the choice is made at each summon.
/// </summary>
internal static class PetChoice
{
    /// <summary>Elements a pet can deal, in the order the Damage panel lists them.</summary>
    public static readonly string[] Elements =
        { "Fire", "Cold", "Lightning", "Acid", "Nether", "Slash", "Pierce", "Bludgeon" };

    /// <summary>
    /// A summoning essence's element. From UiEffects (int 18), which essences carry
    /// in their public data (Fire K'nath 32, Frost 128, Acid 256 ...), else from
    /// words in the name. "" if unknown.
    /// </summary>
    public static string ElementOf(RynthCoreHost host, uint deviceId, string name)
    {
        if (host.HasGetObjectIntProperty && host.TryGetObjectIntProperty(deviceId, 18, out int fx) && fx != 0)
        {
            if ((fx & 0x0020) != 0) return "Fire";
            if ((fx & 0x0040) != 0) return "Lightning";
            if ((fx & 0x0080) != 0) return "Cold";
            if ((fx & 0x0100) != 0) return "Acid";
            if ((fx & 0x1000) != 0) return "Nether";
            if ((fx & 0x0200) != 0) return "Bludgeon";
            if ((fx & 0x0400) != 0) return "Slash";
            if ((fx & 0x0800) != 0) return "Pierce";
        }
        return ElementFromName(name);
    }

    public static string ElementFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        static bool Has(string n, params string[] words)
            => words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        if (Has(name, "Fire", "Flame", "Blaz", "Burn", "Scorch", "Charred", "Inferno", "Magma", "Incendiary")) return "Fire";
        if (Has(name, "Frost", "Blizzard", "Arctic", "Ice", "Frozen", "Glacial", "Cold")) return "Cold";
        if (Has(name, "Lightning", "Electric", "Storm", "Spark", "Shock", "Thunder", "Voltaic")) return "Lightning";
        if (Has(name, "Acid", "Caustic", "Corros", "Blister", "Toxic")) return "Acid";
        if (Has(name, "Nether", "Void", "Shadow")) return "Nether";
        if (Has(name, "Golem")) return "Bludgeon";   // golem essences carry no UiEffects; all bludgeon
        return "";
    }

    /// <summary>Normalises element names from rules and the Damage tab ("Electric" → "Lightning").</summary>
    public static string NormalizeElement(string? e)
    {
        if (string.IsNullOrWhiteSpace(e)) return "";
        string t = e.Trim();
        if (t.StartsWith("P", StringComparison.Ordinal) && t.Equals("PAuto", StringComparison.OrdinalIgnoreCase)) return "";
        if (t.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return "";
        if (t.Equals("Electric", StringComparison.OrdinalIgnoreCase)) return "Lightning";
        foreach (string x in Elements)
            if (x.Equals(t, StringComparison.OrdinalIgnoreCase)) return x;
        return "";
    }

    /// <summary>
    /// Orders <paramref name="devices"/> (id, name) for summoning against a monster:
    /// the chosen essence first, or the chosen element's essences first, or (Auto) the
    /// element the monster takes most damage from. Stable, so ties keep the Items-panel order.
    /// </summary>
    public static List<(int Id, string Name, string Element)> Order(
        RynthCoreHost host, IEnumerable<(int Id, string Name)> devices, string choice,
        CreatureData.CreatureWeakness.Ranking? weakness, out string reason, string ruleElement = "")
    {
        var list = devices.Select(d => (d.Id, d.Name, Element: ElementOf(host, unchecked((uint)d.Id), d.Name))).ToList();
        choice ??= "";

        if (choice.StartsWith("I:", StringComparison.Ordinal)
            && int.TryParse(choice.AsSpan(2), out int wantId))
        {
            // Same essence by id, or (after it was replaced) by the name it had.
            int idx = list.FindIndex(d => d.Id == wantId);
            reason = idx >= 0 ? $"picked {list[idx].Name}" : "picked essence not in pack, using Auto";
            if (idx >= 0)
            {
                var pick = list[idx];
                list.RemoveAt(idx);
                list.Insert(0, pick);
                return list;
            }
            choice = "";
        }

        if (choice.StartsWith("E:", StringComparison.Ordinal))
        {
            string want = choice.Substring(2);
            var ordered = list.OrderBy(d => d.Element.Equals(want, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            reason = ordered.Count > 0 && ordered[0].Element.Equals(want, StringComparison.OrdinalIgnoreCase)
                ? $"{want} chosen" : $"{want} chosen, but no {want} essence; using the first";
            return ordered;
        }

        // Auto. 1) the Monsters-tab rule's pet damage / damage type for this monster;
        // 2) the owned element the monster takes most damage from: server data, else
        // what the Damage tab learned, else its creature type (CreatureWeakness). The
        // old step read resists ACE never sends and took the lowest, the element the
        // monster resists most (2026-09-29). Without any of these the Items order stands.
        var have = new HashSet<string>(list.Where(d => d.Element.Length > 0).Select(d => d.Element), StringComparer.OrdinalIgnoreCase);
        string autoElem = "";
        string why = "";
        string re = NormalizeElement(ruleElement);
        if (re.Length > 0 && have.Contains(re)) { autoElem = re; why = "Monsters rule"; }
        if (autoElem.Length == 0 && weakness != null)
            foreach (var (elem, mult) in weakness.Order)
            {
                if (!have.Contains(elem)) continue;
                autoElem = elem;
                why = double.IsNaN(mult) ? weakness.Source : $"{weakness.Source}, takes {mult:0.0#}x";
                break;
            }

        if (autoElem.Length == 0)
        {
            reason = "Auto: no element data for this monster, using Items order";
            return list;
        }
        reason = $"Auto: {autoElem} ({why})";
        return list.OrderBy(d => d.Element.Equals(autoElem, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
    }
}
