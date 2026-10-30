// MagItemInfoSettings.cs — user options for the Mag-style item info line (/ra iteminfo).
//
// Stored per character inside LegacyUiSettings.ItemInfoSettings. Field visibility is kept as a
// *hidden* bitmask so any field added in a later release shows by default for existing profiles.
using System;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

/// <summary>Toggleable sections of the item info line (bit set in HiddenFields = not printed).</summary>
[Flags]
public enum MagItemInfoField
{
    None              = 0,
    Material          = 1 << 0,
    TypeMastery       = 1 << 1,
    Set               = 1 << 2,
    ArmorLevel        = 1 << 3,
    Imbues            = 1 << 4,
    ArmorCleave       = 1 << 5,
    CritStats         = 1 << 6,
    Splits            = 1 << 7,
    Range             = 1 << 8,
    CleaveMultiStrike = 1 << 9,
    Slayer            = 1 << 10,
    Tinks             = 1 << 11,
    Applied           = 1 << 12,
    Damage            = 1 << 13,
    Attack            = 1 << 14,
    Defenses          = 1 << 15,
    ManaConversion    = 1 << 16,
    Spells            = 1 << 17,
    Wield             = 1 << 18,
    Activation        = 1 << 19,
    Difficulty        = 1 << 20,
    Craft             = 1 << 21,
    Protections       = 1 << 22,
    Ratings           = 1 << 23,
    Keyring           = 1 << 24,
    /// <summary>ACECustom T11 state: tier, weapon quality, Gear Grade, property slots, Tainted.</summary>
    T11               = 1 << 25,
}

/// <summary>Persisted item-info options (JSON fields; see RynthAiJsonContext IncludeFields).</summary>
public sealed class MagItemInfoSettings
{
    /// <summary>Spell list modes for <see cref="SpellMode"/>.</summary>
    public const int SpellModeMag = 0, SpellModeAll = 1;
    /// <summary>Mouse triggers for <see cref="ClickTrigger"/>.</summary>
    public const int ClickOff = 0, ClickLeft = 1, ClickRight = 2;
    /// <summary>Output layouts for <see cref="Layout"/>.</summary>
    public const int LayoutOneLine = 0, LayoutPetLines = 1;
    /// <summary><see cref="DetailChatType"/> value meaning "use <see cref="ChatType"/>".</summary>
    public const int SameAsHeader = -1;

    // ── When to print ────────────────────────────────────────────────────────
    /// <summary>Print automatically when an item is selected.</summary>
    public bool OnSelect;
    /// <summary>
    /// Print the selected item after a left (<see cref="ClickLeft"/>) or right (<see cref="ClickRight"/>)
    /// click in the game world / inventory — also re-prints an item that was already selected.
    /// </summary>
    public int ClickTrigger = ClickOff;
    /// <summary>On-select class filters (manual /ra iteminfo ignores them).</summary>
    public bool OnSelectWeapons = true;
    public bool OnSelectArmor   = true;
    public bool OnSelectJewelry = true;
    public bool OnSelectOther   = true;
    /// <summary>Seconds to wait for an ID before printing partial data.</summary>
    public float IdTimeoutSec = 3f;

    // ── What to print ────────────────────────────────────────────────────────
    /// <summary><see cref="MagItemInfoField"/> bits that are NOT printed.</summary>
    public int HiddenFields;
    /// <summary>Bit i set = <see cref="MagItemInfoCatalog.Ratings"/>[i] not printed.</summary>
    public int HiddenRatings;
    /// <summary>Append ", Value N, BU N".</summary>
    public bool ShowValueAndBurden;
    /// <summary><see cref="SpellModeMag"/> (Mag filter) or <see cref="SpellModeAll"/>.</summary>
    public int SpellMode = SpellModeMag;
    /// <summary>Cap for the "all spells" list.</summary>
    public int MaxListedSpells = 26;

    // ── How to print ─────────────────────────────────────────────────────────
    /// <summary><see cref="LayoutPetLines"/> (pet-roster style lines) or <see cref="LayoutOneLine"/> (Mag one-liner).</summary>
    public int Layout = LayoutPetLines;
    /// <summary>AC chat type passed to WriteToChat (selects the chat colour / window).</summary>
    public int ChatType = 1;
    /// <summary>Chat type for pet-style lines 2+, or <see cref="SameAsHeader"/>.</summary>
    public int DetailChatType = SameAsHeader;
    /// <summary>
    /// Text put in front of every line. RynthChat files lines starting "[RynthAi Item] " under
    /// its Items tab (ChatClassifier.IsItemInfo).
    /// </summary>
    public string Prefix = "[RynthAi Item] ";

    /// <summary>Item Info settings window open.</summary>
    public bool ShowWindow;

    public bool IsHidden(MagItemInfoField f) => (HiddenFields & (int)f) != 0;

    public void SetHidden(MagItemInfoField f, bool hidden) =>
        HiddenFields = hidden ? HiddenFields | (int)f : HiddenFields & ~(int)f;

    public bool IsRatingHidden(int index) => (HiddenRatings & (1 << index)) != 0;

    public void SetRatingHidden(int index, bool hidden) =>
        HiddenRatings = hidden ? HiddenRatings | (1 << index) : HiddenRatings & ~(1 << index);

    /// <summary>Clamp values a hand-edited or older profile could carry out of range.</summary>
    public void Sanitize()
    {
        if (float.IsNaN(IdTimeoutSec) || IdTimeoutSec < 0.5f) IdTimeoutSec = 0.5f;
        if (IdTimeoutSec > 15f) IdTimeoutSec = 15f;
        if (SpellMode is not (SpellModeMag or SpellModeAll)) SpellMode = SpellModeMag;
        MaxListedSpells = Math.Clamp(MaxListedSpells, 1, 64);
        if (ChatType < 0 || ChatType > 31) ChatType = 1;
        if (DetailChatType < SameAsHeader || DetailChatType > 31) DetailChatType = SameAsHeader;
        if (ClickTrigger is not (ClickOff or ClickLeft or ClickRight)) ClickTrigger = ClickOff;
        if (Layout is not (LayoutOneLine or LayoutPetLines)) Layout = LayoutPetLines;
        Prefix ??= string.Empty;
        if (Prefix.Length > 32) Prefix = Prefix[..32];
    }

    /// <summary>Back to first-run defaults (window stays open).</summary>
    public void ResetToDefaults()
    {
        var d = new MagItemInfoSettings();
        OnSelect = d.OnSelect;
        ClickTrigger = d.ClickTrigger;
        Layout = d.Layout;
        DetailChatType = d.DetailChatType;
        OnSelectWeapons = d.OnSelectWeapons; OnSelectArmor = d.OnSelectArmor;
        OnSelectJewelry = d.OnSelectJewelry; OnSelectOther = d.OnSelectOther;
        IdTimeoutSec = d.IdTimeoutSec;
        HiddenFields = d.HiddenFields; HiddenRatings = d.HiddenRatings;
        ShowValueAndBurden = d.ShowValueAndBurden;
        SpellMode = d.SpellMode; MaxListedSpells = d.MaxListedSpells;
        ChatType = d.ChatType; Prefix = d.Prefix;
    }
}

/// <summary>Labels for the settings window and the /ra iteminfo field|rating commands.</summary>
public static class MagItemInfoCatalog
{
    public readonly record struct FieldInfo(MagItemInfoField Field, string Label, string Example);
    public readonly record struct RatingInfo(uint StatId, string Tag, string Name);

    /// <summary>In print order.</summary>
    public static readonly FieldInfo[] Fields =
    {
        new(MagItemInfoField.Material,          "Material",             "Gold Ornate Long Sword"),
        new(MagItemInfoField.TypeMastery,       "Damage type / mastery","(Slash Sword)"),
        new(MagItemInfoField.Set,               "Equipment set",        "Noble Relic Set"),
        new(MagItemInfoField.T11,               "T11 tier / grade / slots", "T16, Gear B+ (4 lines), Props 3/5, Tainted"),
        new(MagItemInfoField.ArmorLevel,        "Armor level",          "AL 650"),
        new(MagItemInfoField.Imbues,            "Imbues",               "CS, AR, FireRend"),
        new(MagItemInfoField.ArmorCleave,       "Armor cleaving",       "AC"),
        new(MagItemInfoField.CritStats,         "Crit mult / freq",     "CritMult (1.5), CritFreq (0.15)"),
        new(MagItemInfoField.Splits,            "Split arrows",         "Splits (2)"),
        new(MagItemInfoField.Range,             "Missile range",        "Range 120yd"),
        new(MagItemInfoField.CleaveMultiStrike, "Cleaving / Multi-Strike", "Cleaving"),
        new(MagItemInfoField.Slayer,            "Slayer",               "Undead Slayer"),
        new(MagItemInfoField.Tinks,             "Tinks",                "Tinks 4"),
        new(MagItemInfoField.Applied,           "Applied materials",    "Applied: Steel x2"),
        new(MagItemInfoField.Damage,            "Damage",               "152.48-236, 0.35v, +18, +10%"),
        new(MagItemInfoField.Attack,            "Attack bonus",         "+18%a"),
        new(MagItemInfoField.Defenses,          "Defense bonuses",      "12%md, 1.5%mgc.d, 1%msl.d"),
        new(MagItemInfoField.ManaConversion,    "Mana conversion",      "6%mc"),
        new(MagItemInfoField.Spells,            "Spells",               "Legendary Blood Thirst"),
        new(MagItemInfoField.Wield,             "Wield requirements",   "Wield Lvl 180, Heavy Weapons 375"),
        new(MagItemInfoField.Activation,        "Activation / use reqs","Lvl 50, Melee Defense 300 to Activate"),
        new(MagItemInfoField.Difficulty,        "Arcane difficulty",    "Diff 270"),
        new(MagItemInfoField.Craft,             "Craft / salvage work", "Craft 8"),
        new(MagItemInfoField.Protections,       "Protections (unenchantable armor)", "[1.0/0.8/1.2/...]"),
        new(MagItemInfoField.Ratings,           "Ratings",              "[D 3, CD 2]"),
        new(MagItemInfoField.Keyring,           "Keyring keys / uses",  "Keys: 3, Uses: 25"),
    };

    /// <summary>ACE gear ratings (PropertyInt 370–389) in Mag print order.</summary>
    public static readonly RatingInfo[] Ratings =
    {
        new(370, "D",    "Damage"),
        new(371, "DR",   "Damage Resist"),
        new(372, "C",    "Crit"),
        new(374, "CD",   "Crit Damage"),
        new(373, "CR",   "Crit Resist"),
        new(375, "CDR",  "Crit Damage Resist"),
        new(376, "HB",   "Healing Boost"),
        new(379, "V",    "Vitality"),
        new(377, "NR",   "Nether Resist"),
        new(378, "LR",   "Life Resist"),
        new(383, "PKD",  "PK Damage"),
        new(384, "PKDR", "PK Damage Resist"),
        new(388, "OP",   "Overpower"),
        new(389, "OPR",  "Overpower Resist"),
    };

    /// <summary>Labels for <see cref="MagItemInfoSettings.ClickTrigger"/>, indexed by value.</summary>
    public static readonly string[] ClickTriggers = { "Off", "Left mouse click", "Right mouse click" };

    /// <summary>Labels for <see cref="MagItemInfoSettings.Layout"/>, indexed by value.</summary>
    public static readonly string[] Layouts = { "One line (Mag-Tools)", "Pet-style lines" };

    /// <summary>Common AC chat types for the colour picker (value passed to WriteToChat).</summary>
    public static readonly (int Type, string Label)[] ChatTypes =
    {
        (0,  "0 - Broadcast"),
        (1,  "1 - RynthAi default"),
        (2,  "2 - Speech"),
        (3,  "3 - Tell"),
        (4,  "4 - Outgoing tell"),
        (5,  "5 - System"),
        (6,  "6 - Combat"),
        (7,  "7 - Magic"),
        (8,  "8 - Channels"),
        (10, "10 - Social"),
        (12, "12 - Emote"),
        (13, "13 - Advancement"),
        (15, "15 - Help"),
        (16, "16 - Appraisal"),
        (17, "17 - Spellcasting"),
        (18, "18 - Allegiance"),
        (19, "19 - Fellowship"),
        (21, "21 - Combat (enemy)"),
        (22, "22 - Combat (self)"),
        (24, "24 - Craft"),
        (25, "25 - Salvaging"),
    };

    /// <summary>Field by enum name or label word, case-insensitive ("ratings", "slayer", "typemastery").</summary>
    public static bool TryFindField(string key, out MagItemInfoField field)
    {
        foreach (var f in Fields)
        {
            if (f.Field.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)
                || f.Label.Replace(" ", string.Empty).StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                field = f.Field;
                return true;
            }
        }
        field = MagItemInfoField.None;
        return false;
    }

    /// <summary>Rating index by tag ("CD") or name ("critdamage"), case-insensitive.</summary>
    public static int FindRating(string key)
    {
        for (int i = 0; i < Ratings.Length; i++)
        {
            if (Ratings[i].Tag.Equals(key, StringComparison.OrdinalIgnoreCase)
                || Ratings[i].Name.Replace(" ", string.Empty).Equals(key, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
