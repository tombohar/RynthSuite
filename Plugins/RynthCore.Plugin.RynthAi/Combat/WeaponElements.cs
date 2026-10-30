using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.CreatureData;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// A weapon's damage element, from its properties. Pure: no host, no cache.
///
/// Sources, best first:
///   1. DamageType (int 45): the caster's element, or the melee/missile weapon profile's
///      damage type. Only arrives with an identify.
///   2. The icon's element glow (UiEffects, int 18). In CreateObject, so known before
///      any identify; the engine serves it from the object's PublicWeenieDesc.
///   3. A rending imbue (ImbuedEffect, int 179): "Fire Rending" etc. Identify-only. Used
///      when the weapon has no other element; it also marks the weapon as the best pick
///      against that element, since rending ignores the target's resistance to it.
///   4. The name: Fire/Flame/Flaming, Frost/Ice, Acid, Lightning/Electric, Blade, ...
/// Element names are CreatureWeakness's: Slash, Pierce, Bludgeon, Fire, Cold, Acid,
/// Lightning, Nether. "" = no element known.
/// </summary>
internal static class WeaponElements
{
    public const uint PropUiEffects = 18, PropDamageType = 45, PropImbuedEffect = 179;
    /// <summary>Float 152: the caster's bonus to war spells of its own element (1.0 = none).</summary>
    public const uint PropElementalDamageMod = 152;

    /// <summary>
    /// AC DAMAGE_TYPE flags → element. A single elemental bit wins; several physical bits
    /// (a Slash/Pierce sword, 0x3) give the first of Slash, Pierce, Bludgeon. 0 → "".
    /// </summary>
    public static string FromDamageType(int dt)
    {
        if (dt == 0) return "";
        if ((dt & 0x400) != 0) return "Nether";
        if ((dt & 0x10)  != 0) return "Fire";
        if ((dt & 0x08)  != 0) return "Cold";
        if ((dt & 0x40)  != 0) return "Lightning";
        if ((dt & 0x20)  != 0) return "Acid";
        if ((dt & 0x01)  != 0) return "Slash";
        if ((dt & 0x02)  != 0) return "Pierce";
        if ((dt & 0x04)  != 0) return "Bludgeon";
        return "";
    }

    /// <summary>ACE UiEffects (the icon's glow) → element; "" when it has none.</summary>
    public static string FromUiEffects(int fx)
    {
        if (fx == 0) return "";
        if ((fx & 0x1000) != 0) return "Nether";
        if ((fx & 0x0020) != 0) return "Fire";
        if ((fx & 0x0080) != 0) return "Cold";
        if ((fx & 0x0040) != 0) return "Lightning";
        if ((fx & 0x0100) != 0) return "Acid";
        if ((fx & 0x0400) != 0) return "Slash";
        if ((fx & 0x0800) != 0) return "Pierce";
        if ((fx & 0x0200) != 0) return "Bludgeon";
        return "";
    }

    /// <summary>ACE ImbuedEffectType rending flags → element; "" when there is none.</summary>
    public static string FromImbuedEffect(int imbue)
    {
        if (imbue == 0) return "";
        if ((imbue & 0x4000) != 0) return "Nether";
        if ((imbue & 0x0200) != 0) return "Fire";
        if ((imbue & 0x0080) != 0) return "Cold";
        if ((imbue & 0x0100) != 0) return "Lightning";
        if ((imbue & 0x0040) != 0) return "Acid";
        if ((imbue & 0x0008) != 0) return "Slash";
        if ((imbue & 0x0010) != 0) return "Pierce";
        if ((imbue & 0x0020) != 0) return "Bludgeon";
        return "";
    }

    // Whole words in a weapon's name, first match wins. "Staff of the Blade" is Slash;
    // "Iceberg" isn't "Ice" (whole words only).
    private static readonly (string Word, string Element)[] NameWords =
    {
        ("Fire", "Fire"), ("Flame", "Fire"), ("Flaming", "Fire"), ("Fiery", "Fire"), ("Burning", "Fire"), ("Inferno", "Fire"),
        ("Frost", "Cold"), ("Ice", "Cold"), ("Icy", "Cold"), ("Cold", "Cold"), ("Freezing", "Cold"), ("Frozen", "Cold"), ("Glacial", "Cold"),
        ("Acid", "Acid"), ("Acidic", "Acid"), ("Corrosive", "Acid"), ("Caustic", "Acid"),
        ("Lightning", "Lightning"), ("Electric", "Lightning"), ("Electrical", "Lightning"), ("Shock", "Lightning"), ("Thunder", "Lightning"),
        ("Nether", "Nether"), ("Void", "Nether"),
        ("Blade", "Slash"), ("Blades", "Slash"), ("Slashing", "Slash"), ("Slash", "Slash"),
        ("Pierce", "Pierce"), ("Piercing", "Pierce"), ("Force", "Pierce"),
        ("Bludgeon", "Bludgeon"), ("Bludgeoning", "Bludgeon"),
    };

    /// <summary>The element a weapon's name says, "" when it names none.</summary>
    public static string FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        foreach (var (word, element) in NameWords)
            if (ContainsWord(name, word)) return element;
        return "";
    }

    /// <summary>What <see cref="Detect"/> worked out for one weapon.</summary>
    public readonly record struct Info(string Element, string Source, string Rending, double Bonus)
    {
        /// <summary>No element known from anything.</summary>
        public bool Unknown => Element.Length == 0;
    }

    /// <summary>
    /// A weapon's element from what is known about it. Pass null for a property that isn't
    /// known (not sent yet); 0 for one that is known to be empty. <paramref name="elementalMod"/>
    /// is the caster's float 152 (null = unknown; it only counts for the caster's own element).
    /// Source: "properties", "icon", "rending", "name" or "".
    /// </summary>
    public static Info Detect(int? damageType, int? imbuedEffect, int? uiEffects, string? name, double? elementalMod = null)
    {
        string rending = imbuedEffect is int ie ? FromImbuedEffect(ie) : "";
        double bonus = elementalMod is double m && m > 1.0 && m < 5.0 ? m : 1.0;

        string e = damageType is int dt ? FromDamageType(dt) : "";
        if (e.Length > 0) return new Info(e, "properties", rending, bonus);
        e = uiEffects is int fx ? FromUiEffects(fx) : "";
        if (e.Length > 0) return new Info(e, "icon", rending, bonus);
        if (rending.Length > 0) return new Info(rending, "rending", rending, bonus);
        e = FromName(name);
        if (e.Length > 0) return new Info(e, "name", rending, bonus);
        return new Info("", "", rending, bonus);
    }

    /// <summary>Element spelling as the rest of RynthAi uses it (Blade → Slash, Electric → Lightning, Frost → Cold).</summary>
    public static string Normalize(string? element)
    {
        if (string.IsNullOrWhiteSpace(element)) return "";
        string t = element.Trim();
        if (t.Equals("Frost", StringComparison.OrdinalIgnoreCase) || t.Equals("Ice", StringComparison.OrdinalIgnoreCase)) return "Cold";
        return CreatureWeakness.Normalize(t);
    }

    private static bool ContainsWord(string text, string word)
    {
        int i = 0;
        while ((i = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = i == 0 || !char.IsLetter(text[i - 1]);
            int end = i + word.Length;
            bool endOk = end >= text.Length || !char.IsLetter(text[end]);
            if (startOk && endOk) return true;
            i = end;
        }
        return false;
    }
}
