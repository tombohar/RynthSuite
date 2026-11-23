using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// What a summoning item brings out, for the combat summoner's element pick: the element of each pet
/// one use can bring, in summon order, and whether the item can summon at all.
/// </summary>
/// <param name="Elements">The element of each pet, in summon order ("" where it isn't known).</param>
/// <param name="Held">For a holder: the held items' names, in summon order (empty for a plain essence).</param>
/// <param name="IsHolder">The item holds other summoning items (a server's own summon item, ServerSummonView).</param>
/// <param name="Ready">The item can summon: a plain essence always; a holder once it holds <paramref name="Slots"/> items.</param>
/// <param name="Slots">The items a holder needs before it summons (1 for a plain essence).</param>
internal sealed record PetDeviceInfo(IReadOnlyList<string> Elements, IReadOnlyList<string> Held, bool IsHolder, bool Ready, int Slots)
{
    /// <summary>The known elements, joined: "Fire", "Fire+Cold"; "" when none is known.</summary>
    public string ElementText
    {
        get
        {
            var known = new List<string>();
            foreach (string e in Elements)
                if (e.Length > 0 && !known.Contains(e)) known.Add(e);
            return string.Join("+", known);
        }
    }

    /// <summary>True when one of its pets deals <paramref name="element"/>.</summary>
    public bool Has(string element)
    {
        foreach (string e in Elements)
            if (e.Length > 0 && e.Equals(element, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// Reads a summoning item's elements (<see cref="PetDeviceInfo"/>). A plain essence has one: its
/// UiEffects element bits, else words in its name (<see cref="PetChoice.ElementFromName"/>).
///
/// A "holder" is a server's own summon item that holds other summoning items, named by the active
/// server plugin's profile ("summons", <see cref="ServerSummonView"/>; RynthAi names no server). Its
/// appraisal description lists the held items ("Summon 1: Fire Moar Essence (180)"); one use brings
/// out the first held item's pet, and the next ones' too when the character can have more pets out.
/// Each held item's element is read from its name; with "elementIsLast" the item's own UiEffects is
/// the last held item's element (the server writes it there). Until it holds every slot it summons
/// nothing (<see cref="PetDeviceInfo.Ready"/> false), and the summoner skips it.
/// </summary>
internal static class PetElements
{
    private const uint IntUiEffects = 18;
    private const uint IntMaxStructure = 91;
    private const uint IntStructure = 92;
    private const uint StringLongDesc = 16;

    private static volatile IReadOnlyList<ServerSummonView> _serverSummons = Array.Empty<ServerSummonView>();

    /// <summary>
    /// The active server plugin's holder rows (its profile's "summons"; empty without one). Set by the
    /// Mini Remote's server poll (MiniRemoteContext.PollServer), and by the tests. Any thread.
    /// </summary>
    public static IReadOnlyList<ServerSummonView> ServerSummons
    {
        get => _serverSummons;
        set => _serverSummons = value ?? Array.Empty<ServerSummonView>();
    }

    /// <summary>The element of a UiEffects value's element bits ("" for none).</summary>
    public static string FromUiEffects(int fx)
    {
        if (fx == 0) return "";
        if ((fx & 0x0020) != 0) return "Fire";
        if ((fx & 0x0040) != 0) return "Lightning";
        if ((fx & 0x0080) != 0) return "Cold";
        if ((fx & 0x0100) != 0) return "Acid";
        if ((fx & 0x1000) != 0) return "Nether";
        if ((fx & 0x0200) != 0) return "Bludgeon";
        if ((fx & 0x0400) != 0) return "Slash";
        if ((fx & 0x0800) != 0) return "Pierce";
        return "";
    }

    /// <summary>The holder row for an item, or null (a plain essence, or no server rows).</summary>
    public static ServerSummonView? HolderRule(uint wcid, string? name, IReadOnlyList<ServerSummonView>? rules)
    {
        if (rules == null) return null;
        foreach (var r in rules)
            if (r.Matches(wcid, name)) return r;
        return null;
    }

    /// <summary>
    /// The held items' names from a holder's description: each line that starts with
    /// <paramref name="prefix"/>, a number and ":" ("Summon 2: Frost K'nath Essence (180)"), in order.
    /// </summary>
    public static List<string> HeldItems(string? longDesc, string prefix)
    {
        var held = new List<string>();
        if (string.IsNullOrEmpty(longDesc) || string.IsNullOrWhiteSpace(prefix)) return held;
        foreach (string raw in longDesc.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            int i = prefix.Length;
            while (i < line.Length && line[i] == ' ') i++;
            int digits = i;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i == digits || i >= line.Length || line[i] != ':') continue;
            string name = line[(i + 1)..].Trim();
            if (name.Length > 0) held.Add(name);
        }
        return held;
    }

    /// <summary>
    /// PURE. What an item brings out, from what the client knows of it: its name, wcid (0 = not known),
    /// UiEffects (0 = none), appraisal description (null = not appraised yet) and whether it carries a
    /// charge count; <paramref name="rules"/> are the server's holder rows.
    /// </summary>
    public static PetDeviceInfo Describe(string? name, uint wcid, int uiEffects, string? longDesc, bool hasCharges,
                                         IReadOnlyList<ServerSummonView>? rules)
    {
        name ??= "";
        string own = FromUiEffects(uiEffects);
        var rule = HolderRule(wcid, name, rules);
        if (rule == null)
        {
            string e = own.Length > 0 ? own : PetChoice.ElementFromName(name);
            return new PetDeviceInfo(e.Length > 0 ? new[] { e } : Array.Empty<string>(), Array.Empty<string>(), false, true, 1);
        }

        var held = HeldItems(longDesc, rule.SlotPrefix);
        var elements = new List<string>(Math.Max(held.Count, rule.Slots));
        for (int i = 0; i < held.Count; i++)
        {
            bool last = i == held.Count - 1 && held.Count >= rule.Slots;
            string e = last && rule.ElementIsLast && own.Length > 0 ? own : PetChoice.ElementFromName(held[i]);
            elements.Add(e);
        }
        // Not appraised yet (no description): a holder with charges is a full one (the server gives
        // charges only when it binds); its own element is the last slot's.
        bool undescribed = string.IsNullOrEmpty(longDesc);
        if (held.Count == 0 && undescribed && hasCharges)
        {
            for (int i = 0; i < rule.Slots; i++)
                elements.Add(i == rule.Slots - 1 && rule.ElementIsLast ? own : "");
        }
        bool ready = held.Count >= rule.Slots || (held.Count == 0 && undescribed && hasCharges);
        return new PetDeviceInfo(elements, held, true, ready, rule.Slots);
    }

    /// <summary>What the item <paramref name="id"/> brings out, read from the client (see the pure overload).</summary>
    public static PetDeviceInfo Describe(RynthCoreHost host, uint id, string? name)
    {
        var rules = ServerSummons;
        uint wcid = 0;
        if (host.HasGetObjectWcid) host.TryGetObjectWcid(id, out wcid);
        int fx = 0;
        bool charges = false;
        if (host.HasGetObjectIntProperty)
        {
            if (!host.TryGetObjectIntProperty(id, IntUiEffects, out fx)) fx = 0;
            charges = (host.TryGetObjectIntProperty(id, IntMaxStructure, out int max) && max > 0)
                      || host.TryGetObjectIntProperty(id, IntStructure, out _);
        }
        string? desc = null;
        if (rules.Count > 0 && HolderRule(wcid, name, rules) != null && host.HasGetObjectStringProperty
            && host.TryGetObjectStringProperty(id, StringLongDesc, out string d) && !string.IsNullOrWhiteSpace(d))
            desc = d;
        return Describe(name, wcid, fx, desc, charges, rules);
    }

    /// <summary>True when the item is one of the active server's holders (cheap: no description read).</summary>
    public static bool IsHolder(RynthCoreHost host, uint id, string? name)
    {
        var rules = ServerSummons;
        if (rules.Count == 0) return false;
        uint wcid = 0;
        if (host.HasGetObjectWcid) host.TryGetObjectWcid(id, out wcid);
        if (name == null && host.TryGetObjectName(id, out string n)) name = n;
        return HolderRule(wcid, name, rules) != null;
    }
}
