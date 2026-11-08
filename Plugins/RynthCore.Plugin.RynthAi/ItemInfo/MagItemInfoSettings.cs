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
    /// <summary>Print automatically when an item is selected. On unless the player turned it off (2026-10-31).</summary>
    public bool OnSelect = true;
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

    // ── Loot lines ───────────────────────────────────────────────────────────
    /// <summary>
    /// The "Looted [Keep] ..." line of an item a loot rule took also names the rule and the item's
    /// details (one line, the fields below applied). Stackables (pyreals, notes, comps) never do.
    /// </summary>
    public bool LootChat = true;
    /// <summary>Salvage is described too, short: material and workmanship only.</summary>
    public bool LootChatSalvage;
    /// <summary>Chat type of a described loot line, or <see cref="SameAsHeader"/> for <see cref="ChatType"/>.</summary>
    public int LootChatType = SameAsHeader;

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
    /// "Info text colour" (2026-11-05): "#RRGGBB" draws every item info line (name, details, the
    /// described "Looted" line, your ratings) in that colour in RynthCore's chat, whatever its chat
    /// type; the type still picks the tab and AC's own chat window, which can only show the type's
    /// colour. "" = the type's own colour (the default, and every profile before it).
    /// </summary>
    public string TextColor = string.Empty;
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
        if (LootChatType < SameAsHeader || LootChatType > 31) LootChatType = SameAsHeader;
        if (ClickTrigger is not (ClickOff or ClickLeft or ClickRight)) ClickTrigger = ClickOff;
        if (Layout is not (LayoutOneLine or LayoutPetLines)) Layout = LayoutPetLines;
        Prefix ??= string.Empty;
        if (Prefix.Length > 32) Prefix = Prefix[..32];
        TextColor = MagItemInfoCatalog.NormalizeTextColor(TextColor) ?? string.Empty;
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
        LootChat = d.LootChat; LootChatSalvage = d.LootChatSalvage; LootChatType = d.LootChatType;
        HiddenFields = d.HiddenFields; HiddenRatings = d.HiddenRatings;
        ShowValueAndBurden = d.ShowValueAndBurden;
        SpellMode = d.SpellMode; MaxListedSpells = d.MaxListedSpells;
        ChatType = d.ChatType; Prefix = d.Prefix; TextColor = d.TextColor;
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

    /// <summary>
    /// Named choices for <see cref="MagItemInfoSettings.TextColor"/> ("" first: the chat type's own
    /// colour). The chat ones are AC's own colours for those chat types, as RynthCore's chat draws
    /// them with Retail colours on (the engine's ChatLineColors.RetailArgbByType); the last two are
    /// RynthCore's own tab colours. <c>Word</c> is what /ra iteminfo color takes.
    /// </summary>
    public static readonly (string Word, string Label, string Color)[] TextColors =
    {
        ("default",     "Chat type's own colour",      ""),
        ("allegiance",  "Allegiance (orange)",          "#EE921E"),
        ("fellowship",  "Fellowship / tell (yellow)",   "#FFFF3F"),
        ("tellout",     "Tell you send (olive)",        "#D2D264"),
        ("speech",      "Speech (white)",               "#FFFFFF"),
        ("emote",       "Emote (grey)",                 "#D2D2C8"),
        ("system",      "System (purple)",              "#FF7FFF"),
        ("channels",    "Channels (rose)",              "#FF9696"),
        ("combat",      "Combat (red)",                 "#FF3F3F"),
        ("magic",       "Magic (light blue)",           "#3FBFFF"),
        ("advancement", "Advancement (cyan)",           "#3FDCDC"),
        ("broadcast",   "Broadcast (green)",            "#80FF7F"),
        ("items",       "Items tab (teal)",             "#9FD8C8"),
        ("rynth",       "Rynth tab (amber)",            "#E6B450"),
    };

    /// <summary>
    /// "#RRGGBB" (upper case) for "#rrggbb", "rrggbb" or "0xRRGGBB"; "" for ""; null for anything else.
    /// </summary>
    public static string? NormalizeTextColor(string? s)
    {
        string v = (s ?? string.Empty).Trim();
        if (v.Length == 0) return string.Empty;
        if (v.StartsWith('#')) v = v[1..];
        else if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        if (v.Length != 6) return null;
        foreach (char c in v)
            if (!Uri.IsHexDigit(c)) return null;
        return "#" + v.ToUpperInvariant();
    }

    /// <summary>
    /// /ra iteminfo color's argument: a <see cref="TextColors"/> word ("allegiance", "default",
    /// "none" / "off" / "type" / "" = ""), or a colour code. Null when it is neither.
    /// </summary>
    public static string? ParseTextColor(string arg)
    {
        string a = arg.Trim();
        if (a.Length == 0 || a.Equals("none", StringComparison.OrdinalIgnoreCase) || a.Equals("off", StringComparison.OrdinalIgnoreCase)
            || a.Equals("type", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        foreach (var c in TextColors)
            if (c.Word.Equals(a, StringComparison.OrdinalIgnoreCase)) return c.Color;
        return NormalizeTextColor(a);
    }

    /// <summary>A colour as the window and the command reply name it: its label, else the code.</summary>
    public static string TextColorLabel(string color)
    {
        foreach (var c in TextColors)
            if (string.Equals(c.Color, color, StringComparison.OrdinalIgnoreCase)) return c.Color.Length > 0 ? $"{c.Label} {c.Color}" : c.Label;
        return color;
    }

    /// <summary>"#RRGGBB" as 0..1 RGB for ImGui's colour picker (white when it isn't a colour).</summary>
    public static System.Numerics.Vector3 TextColorRgb(string color)
    {
        string? n = NormalizeTextColor(color);
        if (string.IsNullOrEmpty(n)) return System.Numerics.Vector3.One;
        int v = Convert.ToInt32(n[1..], 16);
        return new System.Numerics.Vector3(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    /// <summary>0..1 RGB from ImGui's colour picker as "#RRGGBB".</summary>
    public static string TextColorFromRgb(System.Numerics.Vector3 rgb)
    {
        static int B(float f) => (int)MathF.Round(Math.Clamp(f, 0f, 1f) * 255f);
        return $"#{B(rgb.X):X2}{B(rgb.Y):X2}{B(rgb.Z):X2}";
    }

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
