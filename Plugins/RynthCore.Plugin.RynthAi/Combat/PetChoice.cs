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
/// (Pet damage, else damage type), the Summons settings' Pet element (2026-11-22;
/// "" = Any, the default, skips it), the Default row's pet damage, the monster's
/// learned resists, then the element that has done best on it in the Damage tab;
/// with none of those the Items-panel order is kept. An item whose pets deal more
/// than one element (a server's holder of two summons, PetElements) fits an element
/// by <see cref="Rank"/>: all its pets first, then its first pet, then a later one. Combat pets can't be swapped while one is out
/// (ACE: "already active"), so the choice is made at each summon.
/// </summary>
internal static class PetChoice
{
    /// <summary>Elements a pet can deal, in the order the Damage panel lists them.</summary>
    public static readonly string[] Elements =
        { "Fire", "Cold", "Lightning", "Acid", "Nether", "Slash", "Pierce", "Bludgeon" };

    /// <summary>The Summons settings' Pet element choices: "Any" (stored as "") then every element.</summary>
    public static readonly string[] SettingChoices =
        { "Any", "Fire", "Cold", "Acid", "Lightning", "Nether", "Slash", "Pierce", "Bludgeon" };

    /// <summary>The tooltip both Pet element controls show.</summary>
    public const string SettingTooltip =
        "The element combat pets are summoned for. Any: each monster's own choice (a Damage-panel pick,\n" +
        "its Monsters rule, then the element it is weakest to). Another element: an essence of it is\n" +
        "summoned first, unless the monster has a Damage-panel pick or a Monsters rule of its own.\n" +
        "A summon that holds two (two pets, two elements) counts when either of its pets deals it.";

    /// <summary>The index of a stored Pet element in <see cref="SettingChoices"/> (0 = Any).</summary>
    public static int SettingIndex(string? stored)
    {
        string e = NormalizeElement(stored);
        int i = e.Length == 0 ? 0 : Array.IndexOf(SettingChoices, e);
        return i < 0 ? 0 : i;
    }

    /// <summary>The stored Pet element for a <see cref="SettingChoices"/> index ("" = Any).</summary>
    public static string SettingValue(int index)
        => index <= 0 || index >= SettingChoices.Length ? "" : SettingChoices[index];

    /// <summary>
    /// A summoning essence's element: its first pet's (<see cref="PetElements.Describe(RynthCoreHost, uint, string?)"/>).
    /// From UiEffects (int 18), which essences carry in their public data (Fire K'nath 32, Frost 128,
    /// Acid 256 ...), else from words in the name. "" if unknown.
    /// </summary>
    public static string ElementOf(RynthCoreHost host, uint deviceId, string name)
    {
        var info = PetElements.Describe(host, deviceId, name);
        foreach (string e in info.Elements)
            if (e.Length > 0) return e;
        return "";
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
    /// <paramref name="preferElement"/> is the Summons settings' Pet element ("" = Any);
    /// <paramref name="defaultElement"/> the Monsters tab Default row's pet damage, for a monster
    /// with no rule of its own (or one left on Auto).
    /// </summary>
    public static List<(int Id, string Name, string Element)> Order(
        RynthCoreHost host, IEnumerable<(int Id, string Name)> devices, string choice,
        CreatureData.CreatureWeakness.Ranking? weakness, out string reason, string ruleElement = "",
        string preferElement = "", string defaultElement = "")
    {
        var list = devices.Select(d => (d.Id, d.Name, Info: PetElements.Describe(host, unchecked((uint)d.Id), d.Name))).ToList();
        return Order(list, choice, weakness, out reason, ruleElement, preferElement, defaultElement);
    }

    /// <summary>
    /// How well an item's pets fit <paramref name="element"/>: 0 every pet deals it (a plain essence of
    /// it), 1 its first pet does (a holder's first held summon), 2 a later pet does (a holder's second,
    /// out only when the character can have two pets), 3 none does or none is known.
    /// </summary>
    public static int Rank(PetDeviceInfo info, string element)
    {
        var e = info.Elements;
        if (e.Count == 0 || element.Length == 0) return 3;
        bool first = e[0].Equals(element, StringComparison.OrdinalIgnoreCase);
        bool all = true;
        foreach (string x in e)
            if (!x.Equals(element, StringComparison.OrdinalIgnoreCase)) { all = false; break; }
        if (all) return 0;
        if (first) return 1;
        return info.Has(element) ? 2 : 3;
    }

    /// <summary>PURE. <see cref="Order(RynthCoreHost, IEnumerable{ValueTuple{int, string}}, string, CreatureData.CreatureWeakness.Ranking?, out string, string, string, string)"/> on described items.</summary>
    public static List<(int Id, string Name, string Element)> Order(
        List<(int Id, string Name, PetDeviceInfo Info)> list, string choice,
        CreatureData.CreatureWeakness.Ranking? weakness, out string reason, string ruleElement = "",
        string preferElement = "", string defaultElement = "")
    {
        static List<(int Id, string Name, string Element)> Out(IEnumerable<(int Id, string Name, PetDeviceInfo Info)> l)
            => l.Select(d => (d.Id, d.Name, d.Info.ElementText)).ToList();
        choice ??= "";

        if (choice.StartsWith("I:", StringComparison.Ordinal)
            && int.TryParse(choice.AsSpan(2), out int wantId))
        {
            // Same essence by id. A replaced essence's pick arrives already pointed at its
            // same-named successor (PetManager.OrderDevicesForTarget; this matched the id only).
            int idx = list.FindIndex(d => d.Id == wantId);
            reason = idx >= 0 ? $"picked {list[idx].Name}" : "picked essence not in pack, using Auto";
            if (idx >= 0)
            {
                var pick = list[idx];
                var rest = new List<(int Id, string Name, PetDeviceInfo Info)>(list);
                rest.RemoveAt(idx);
                rest.Insert(0, pick);
                return Out(rest);
            }
            choice = "";
        }

        if (choice.StartsWith("E:", StringComparison.Ordinal))
        {
            string want = choice.Substring(2);
            var ordered = list.OrderBy(d => Rank(d.Info, want)).ToList();
            reason = ordered.Count > 0 && Rank(ordered[0].Info, want) < 3
                ? $"{want} chosen" : $"{want} chosen, but no {want} essence; using the first";
            return Out(ordered);
        }

        // Auto. 1) the Monsters-tab rule's pet damage / damage type for this monster;
        // 2) the Summons settings' Pet element; 3) the Default row's pet damage;
        // 4) the owned element the monster takes most damage from: server data, else
        // what the Damage tab learned, else its creature type (CreatureWeakness). The
        // old step read resists ACE never sends and took the lowest, the element the
        // monster resists most (2026-09-29). Without any of these the Items order stands.
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in list)
            foreach (string e in d.Info.Elements)
                if (e.Length > 0) have.Add(e);
        string autoElem = "";
        string why = "";
        string note = "";
        string re = NormalizeElement(ruleElement);
        if (re.Length > 0 && have.Contains(re)) { autoElem = re; why = "Monsters rule"; }
        string pe = NormalizeElement(preferElement);
        if (autoElem.Length == 0 && pe.Length > 0)
        {
            if (have.Contains(pe)) { autoElem = pe; why = "Pet element setting"; }
            else note = $"Pet element {pe}, but no {pe} essence; ";
        }
        string de = NormalizeElement(defaultElement);
        if (autoElem.Length == 0 && de.Length > 0 && have.Contains(de)) { autoElem = de; why = "Monsters rule"; }
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
            reason = note + "Auto: no element data for this monster, using Items order";
            return Out(list);
        }
        reason = $"{note}Auto: {autoElem} ({why})";
        return Out(list.OrderBy(d => Rank(d.Info, autoElem)));
    }
}
