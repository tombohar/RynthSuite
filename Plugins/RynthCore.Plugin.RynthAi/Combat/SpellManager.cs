using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public class SpellManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private CharacterSkills? _charSkills;
    private uint _playerId;

    public Dictionary<string, int> SpellDictionary { get; private set; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // Map base spell names to their exact Level 7 Lore counterparts
    private readonly Dictionary<string, string[]> LoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Strength", new[] { "Might of the Lugians" } },
        { "Endurance", new[] { "Preservance", "Perseverance" } },
        { "Coordination", new[] { "Honed Control" } },
        { "Quickness", new[] { "Hastening" } },
        { "Focus", new[] { "Inner Calm" } },
        { "Willpower", new[] { "Mind Blossom" } },
        { "Invulnerability", new[] { "Aura of Defense" } },
        { "Impregnability", new[] { "Aura of Deflection" } },
        { "Magic Resistance", new[] { "Aura of Resistance" } },
        { "Armor", new[] { "Executor's Blessing" } },
        { "Acid Protection", new[] { "Caustic Blessing" } },
        { "Blade Protection", new[] { "Blessing of the Blade Turner" } },
        { "Bludgeoning Protection", new[] { "Blessing of the Mace Turner" } },
        { "Cold Protection", new[] { "Icy Blessing" } },
        { "Fire Protection", new[] { "Fiery Blessing" } },
        { "Lightning Protection", new[] { "Storm's Blessing" } },
        { "Mana Renewal", new[] { "Battlemage's Blessing" } },
        { "Piercing Protection", new[] { "Blessing of the Arrow Turner" } },
        { "Regeneration", new[] { "Robustify" } },
        { "Rejuvenation", new[] { "Unflinching Persistence", "Unfinching Persistance" } },
        { "Heal", new[] { "Adja's Intervention" } },
        { "Revitalize", new[] { "Robustification" } },
        { "Stamina to Mana", new[] { "Meditative Trance" } },
        { "Stamina to Health", new[] { "Rushed Recovery" } },
        { "Monster Attunement", new[] { "Topheron's Blessing" } },
        { "Person Attunement", new[] { "Kaluhc's Blessing" } },
        { "Arcane Enlightenment", new[] { "Aliester's Blessing" } },
        { "Armor Tinkering Expertise", new[] { "Jibril's Blessing" } },
        { "Item Tinkering Expertise", new[] { "Yoshi's Blessing" } },
        { "Weapon Tinkering Expertise", new[] { "Koga's Blessing" } },
        { "Mana Conversion Mastery", new[] { "Nuhmidura's Blessing" } },
        { "Sprint", new[] { "Saladur's Blessing" } },
        { "Jumping Mastery", new[] { "Jahannan's Blessing" } },
        { "Fealty", new[] { "Odif's Blessing", "Odif's Boon" } },
        { "Leadership Mastery", new[] { "Ar-Pei's Blessing" } },
        { "Deception Mastery", new[] { "Ketnan's Blessing" } },
        { "Healing Mastery", new[] { "Avalenne's Blessing" } },
        { "Lockpick Mastery", new[] { "Oswald's Blessing" } },
        { "Cooking Mastery", new[] { "Morimoto's Blessing" } },
        { "Fletching Mastery", new[] { "Lilitha's Blessing" } },
        { "Alchemy Mastery", new[] { "Silencia's Blessing" } },
        { "Creature Enchantment Mastery", new[] { "Adja's Blessing" } },
        { "Item Enchantment Mastery", new[] { "Celcynd's Blessing" } },
        { "Life Magic Mastery", new[] { "Harlune's Blessing" } },
        { "War Magic Mastery", new[] { "Hieromancer's Blessing" } },
        { "Blood Drinker", new[] { "Aura of Infected Caress" } },
        { "Hermetic Link", new[] { "Aura of Mystic's Blessing" } },
        { "Heart Seeker", new[] { "Aura of Elysa's Sight" } },
        { "Spirit Drinker", new[] { "Aura of Infected Spirit Carress", "Aura of Infected Spirit Caress" } },
        { "Swift Killer", new[] { "Aura of Atlan's Alacrity" } },
        { "Defender", new[] { "Aura of Cragstone's Will" } },
        { "Impenetrability", new[] { "Brogard's Defiance" } },
        { "Acid Bane", new[] { "Olthoi's Bane" } },
        { "Blade Bane", new[] { "Swordsman's Bane", "Swordman's Bane" } },
        { "Bludgeoning Bane", new[] { "Tusker's Bane" } },
        { "Flame Bane", new[] { "Inferno's Bane" } },
        { "Frost Bane", new[] { "Gelidite's Bane" } },
        { "Lightning Bane", new[] { "Astyrrian's Bane" } },
        { "Piercing Bane", new[] { "Archer's Bane" } },
    };

    public SpellManager(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;
    public void SetPlayerId(uint playerId) => _playerId = playerId;

    public void InitializeNatively()
    {
        try
        {
            if (!SpellDatabase.IsLoaded)
                SpellDatabase.Load(msg => _host.WriteToChat(msg, 2));
            SpellDictionary = SpellDatabase.BuildNameToIdMap();
            _host.WriteToChat($"[RynthAi] Magic System Online: {SpellDictionary.Count} spells loaded.", 1);
        }
        catch (Exception ex)
        {
            _host.WriteToChat("[RynthAi] Spell init error: " + ex.Message, 2);
        }
    }

    /// <summary>
    /// Returns the highest castable spell tier for combat (offensive/debuff) casts.
    /// Uses the combat thresholds in LegacyUiSettings (MinSkillLevelTier1..8) which are
    /// padded above AC's hard minimums (1/50/100/150/200/250/300/350) to avoid fizzles
    /// during fights.
    /// </summary>
    public int GetHighestSpellTier(AcSkillType skill)
        => ComputeTier(skill,
            _settings.MinSkillLevelTier1, _settings.MinSkillLevelTier2,
            _settings.MinSkillLevelTier3, _settings.MinSkillLevelTier4,
            _settings.MinSkillLevelTier5, _settings.MinSkillLevelTier6,
            _settings.MinSkillLevelTier7, _settings.MinSkillLevelTier8);

    /// <summary>
    /// Same as <see cref="GetHighestSpellTier"/> but uses the Buffing-specific thresholds
    /// (BuffMinSkillLevelTier1..8). Buffing fizzles are inexpensive — just retry next
    /// heartbeat — so these defaults sit closer to AC's hard minimums, ensuring a
    /// 390-buffed caster actually receives Incantation-tier self buffs.
    /// </summary>
    public int GetHighestBuffSpellTier(AcSkillType skill)
        => ComputeTier(skill,
            _settings.BuffMinSkillLevelTier1, _settings.BuffMinSkillLevelTier2,
            _settings.BuffMinSkillLevelTier3, _settings.BuffMinSkillLevelTier4,
            _settings.BuffMinSkillLevelTier5, _settings.BuffMinSkillLevelTier6,
            _settings.BuffMinSkillLevelTier7, _settings.BuffMinSkillLevelTier8);

    private int ComputeTier(AcSkillType skill, int t1, int t2, int t3, int t4, int t5, int t6, int t7, int t8)
    {
        int buffed = _charSkills != null ? _charSkills[skill].Buffed : 250;
        if (buffed >= t8) return 8;
        if (buffed >= t7) return 7;
        if (buffed >= t6) return 6;
        if (buffed >= t5) return 5;
        if (buffed >= t4) return 4;
        if (buffed >= t3) return 3;
        if (buffed >= t2) return 2;
        return 1;
    }

    private static string GetRomanNumeral(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV",
        5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => "I"
    };

    public int GetDynamicSelfBuffId(string baseSpellName, AcSkillType magicSkill)
    {
        // Self-buffs use the buffing-specific thresholds so the user can tune
        // buffing tier independently from combat tier.
        int maxTier = GetHighestBuffSpellTier(magicSkill);
        string cleanBase = baseSpellName.Replace(" Self", "").Trim();

        for (int tier = maxTier; tier >= 1; tier--)
        {
            if (tier == 8)
            {
                if (TryGetId($"Incantation of {cleanBase} Self", out int id1)) return id1;
                if (TryGetId($"Incantation of {cleanBase}", out int id2)) return id2;
                if (TryGetId($"Aura of Incantation of {cleanBase} Self", out int id3)) return id3;
                if (TryGetId($"Aura of Incantation of {cleanBase}", out int id4)) return id4;
            }
            else if (tier == 7)
            {
                if (LoreNames.TryGetValue(cleanBase, out string[]? lores))
                    foreach (string lore in lores)
                        if (TryGetId(lore, out int idL)) return idL;

                if (TryGetId($"{cleanBase} Self VII", out int id7a)) return id7a;
                if (TryGetId($"{cleanBase} VII", out int id7b)) return id7b;
                if (TryGetId($"Aura of {cleanBase} Self VII", out int id7c)) return id7c;
                if (TryGetId($"Aura of {cleanBase} VII", out int id7d)) return id7d;
            }
            else
            {
                string numeral = GetRomanNumeral(tier);
                if (TryGetId($"{cleanBase} Self {numeral}", out int idNum1)) return idNum1;
                if (TryGetId($"{cleanBase} {numeral}", out int idNum2)) return idNum2;
                if (TryGetId($"Aura of {cleanBase} Self {numeral}", out int idNum3)) return idNum3;
                if (TryGetId($"Aura of {cleanBase} {numeral}", out int idNum4)) return idNum4;
            }
        }
        return 0;
    }

    private bool TryGetId(string exactName, out int spellId)
    {
        if (!SpellDictionary.TryGetValue(exactName, out spellId))
            return false;

        // Use real IsSpellKnown when available; fall back to database presence check
        if (_playerId != 0 && _host.HasIsSpellKnown)
        {
            _host.IsSpellKnown(_playerId, unchecked((uint)spellId), out bool known);
            if (!known) { spellId = 0; return false; }
            return true;
        }

        if (SpellDatabase.IsSpellKnown(spellId)) return true;
        spellId = 0;
        return false;
    }
}
