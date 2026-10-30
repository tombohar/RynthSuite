// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>An enchantment on the player: remaining seconds (double.PositiveInfinity = no timer).</summary>
internal readonly record struct ActiveEnchantment(uint SpellId, string Name, double Remaining, double Duration, bool Debuff);

internal readonly record struct SkillValue(bool Known, int Current, int Training);

/// <summary>
/// The player's state as the views need it, read from the engine on the pump thread (player
/// properties, skills, the enchantment snapshot). Rebuilt about once a second while a window
/// that shows it is open.
/// </summary>
internal sealed class CharacterSnapshot
{
    // PropertyInt (ACE)
    public const uint IntAvailableSkillCredits = 24;
    public const uint IntLevel = 25;
    public const uint IntNumDeaths = 43;
    public const uint IntSocietyCelhan = 287;
    public const uint IntSocietyEldweb = 288;
    public const uint IntSocietyRadblo = 289;
    public const uint IntAugSkilledMagic = 302;     // Master of the Five Fold Path
    public const uint IntAugJackOfAllTrades = 326;
    public const uint IntLumAugSkilledSpec = 344;   // Aura of Specialization
    public const uint IntLumAugAllSkills = 365;     // Aura of the World
    public const uint IntEnlightenment = 390;
    // PropertyInt64 (ACE)
    public const uint QuadTotalExperience = 1;
    public const uint QuadAvailableExperience = 2;
    public const uint QuadAvailableLuminance = 6;
    public const uint QuadMaximumLuminance = 7;
    // Skills (ACE Skill)
    public const uint SkillMeleeDefense = 6;
    public const uint SkillLockpick = 23;
    public const uint SkillLifeMagic = 33;
    public const uint SkillVoidMagic = 43;
    public const uint SkillSummoning = 54;

    public bool Valid;
    public uint PlayerId;
    public string Name = "";
    public int Level;
    public long TotalXp, UnassignedXp, Luminance, MaxLuminance;
    public int SkillCredits, Deaths, Enlightenment;
    public int VitaePercent;
    public SkillValue Lockpick, Life, MeleeDefense, Summoning, VoidMagic;
    public readonly List<ActiveEnchantment> Enchantments = new();
    public double ServerTime;

    private readonly Dictionary<uint, int> _ints = new();
    private uint[] _spellIds = new uint[512];
    private double[] _expiry = new double[512];

    /// <summary>A PropertyInt on the player (0 when the client doesn't have it), cached per read.</summary>
    public int Int(RynthCoreHost host, uint id)
    {
        if (_ints.TryGetValue(id, out int v)) return v;
        v = PlayerId != 0 && host.TryGetObjectIntProperty(PlayerId, id, out int x) ? x : 0;
        _ints[id] = v;
        return v;
    }

    public void Read(RynthCoreHost host)
    {
        _ints.Clear();
        PlayerId = host.GetPlayerId();
        Valid = PlayerId != 0;
        if (!Valid) return;

        Name = host.TryGetObjectName(PlayerId, out string n) ? n : "";
        Level = Int(host, IntLevel);
        SkillCredits = Int(host, IntAvailableSkillCredits);
        Deaths = Int(host, IntNumDeaths);
        Enlightenment = Int(host, IntEnlightenment);
        TotalXp = Quad(host, QuadTotalExperience);
        UnassignedXp = Quad(host, QuadAvailableExperience);
        Luminance = Quad(host, QuadAvailableLuminance);
        MaxLuminance = Quad(host, QuadMaximumLuminance);
        float vitae = host.HasGetVitae ? host.GetVitae(PlayerId) : 1f;
        VitaePercent = vitae > 0f && vitae < 1f ? (int)Math.Round((1f - vitae) * 100f) : 0;

        ReadEnchantments(host);
        bool cis = HasEnchantment(SpellIds.CloakedInSkill);
        Lockpick = Skill(host, SkillLockpick, cis);
        Life = Skill(host, SkillLifeMagic, cis);
        MeleeDefense = Skill(host, SkillMeleeDefense, cis);
        Summoning = Skill(host, SkillSummoning, cis);
        VoidMagic = Skill(host, SkillVoidMagic, cis);
    }

    private long Quad(RynthCoreHost host, uint id) =>
        host.HasGetObjectQuadProperty && host.TryGetObjectQuadProperty(PlayerId, id, out long v) ? v : 0;

    private void ReadEnchantments(RynthCoreHost host)
    {
        Enchantments.Clear();
        if (!host.HasReadPlayerEnchantments) return;
        ServerTime = host.HasGetServerTime ? host.GetServerTime() : 0;
        int count = host.ReadPlayerEnchantments(_spellIds, _expiry, _spellIds.Length);
        for (int i = 0; i < count && i < _spellIds.Length; i++)
        {
            uint id = _spellIds[i];
            double exp = _expiry[i];
            SpellTable.TryGet(id, out SpellInfo info);
            // No timer: a negative or absurd expiry (item-style and permanent enchantments).
            double remaining = exp <= 0 || ServerTime <= 0 || exp - ServerTime > 3.0e7
                ? double.PositiveInfinity
                : exp - ServerTime;
            if (remaining <= 0) continue;
            Enchantments.Add(new ActiveEnchantment(id, info.Name ?? $"Spell {id}", remaining, info.Duration, info.IsDebuff));
        }
    }

    public bool HasEnchantment(uint spellId)
    {
        foreach (ActiveEnchantment e in Enchantments)
            if (e.SpellId == spellId) return true;
        return false;
    }

    /// <summary>
    /// The skill as the server computes it: the client's buffed value plus what the client
    /// doesn't count (Five Fold Path for Life, Aura of the World, Jack of All Trades, Aura of
    /// Specialization, enlightenment, Cloaked in Skill), less an approximation of vitae.
    /// Upstream's rules, unchanged.
    /// </summary>
    private SkillValue Skill(RynthCoreHost host, uint skill, bool cis)
    {
        if (!host.TryGetObjectSkill(PlayerId, skill, out int buffed, out int training))
            return default;
        bool trained = training == 2, specialized = training == 3;
        if (!trained && !specialized) return new SkillValue(false, buffed, training);

        int value = buffed;
        if (skill == SkillLifeMagic) value += Int(host, IntAugSkilledMagic) * 10;
        value += Int(host, IntLumAugAllSkills);
        if (VitaePercent > 0)
            value -= (int)Math.Round(value * (VitaePercent / 100.0)) - ((int)Math.Ceiling(VitaePercent / 2.0) + 1);
        value += Int(host, IntAugJackOfAllTrades) * 5;
        if (specialized) value += Int(host, IntLumAugSkilledSpec) * 2;
        value += Enlightenment;
        if (cis) value += 20;
        return new SkillValue(true, value, training);
    }

    // ── Levels (retail table, cumulative XP to reach each level) ───────────

    private static readonly long[] LevelXp =
    {
        0, 0, 1000, 2777, 5697, 10248, 17031, 26784, 40391, 58895, 83511,
        115645, 156898, 209088, 274259, 354692, 452925, 571762, 714286, 883872, 1084206,
        1319289, 1593459, 1911400, 2278153, 2699136, 3180153, 3727407, 4347513, 5047517, 5834900,
        6717600, 7704021, 8803044, 10024047, 11376914, 12872048, 14520384, 16333408, 18323161, 20502261,
        22883912, 25481915, 28310688, 31385275, 34721359, 38335275, 42244029, 46465302, 51017472, 55919623,
        61191556, 66853809, 72927666, 79435170, 86399136, 93843170, 101791673, 110269863, 119303784, 128920317,
        139147200, 150013037, 161547311, 173780397, 186743581, 200469064, 214989984, 230340425, 246555428, 263671011,
        281724178, 300752932, 320796288, 341894292, 364088025, 387419625, 411932296, 437670319, 464679072, 493005039,
        522695823, 553800159, 586367933, 620450186, 656099136, 693368187, 732311940, 772986213, 815448050, 859755734,
        905968800, 954148054, 1004355577, 1056654747, 1111110248, 1167788081, 1226755584, 1288081441, 1351835695, 1418089761,
        1486916445, 1558389948, 1632585888, 1709581309, 1789454692, 1872285975, 1958156562, 2047149336, 2139348672, 2234840456,
        2333712089, 2436052509, 2541952200, 2651503203, 2764799136, 2881935203, 3003008207, 3128116563, 3257360317, 3390841150,
        3528662400, 3670929071, 3817747844, 3969227097, 4125476914, 4286609098, 4452737184, 4623976457, 4800443961, 4982258511,
        5169540711, 5362412965, 5560999488, 5765426325, 5975821358, 6192314325, 6415036828, 6644122352, 6879706272, 7121925872,
        7370920356, 7626830859, 7889800466, 8159974219, 8437499136, 8722524219, 9015200473, 9315680913, 9624120583, 9940676567,
        10265508000, 10598776087, 10940644110, 11291277447, 11650843580, 12019512114, 12397454784, 12784845474, 13181860228, 13588677261,
        14005476978, 14432441981, 14869757088, 15317609341, 15776188025, 16245684675, 16726293095, 17218209369, 17721631872, 18236761289,
        18763800622, 19302955209, 19854432732, 20418443236, 20995199136, 21584915236, 22187808740, 22804099263, 23434008850, 24077761983,
        24735585600, 25407709103, 26094364377, 26795785797, 27512210247, 28243877131, 28991028384, 29753908491, 30532764494, 31327846011,
        32139405244, 32967696998, 33812978688, 34675510358, 35555554692, 36453377025, 37369245362, 38303430385, 39256205472, 40227846705,
        41218632889, 42228845559, 43258768999, 44308690253, 45378899136, 46469688253, 47581353006, 48714191613, 49868505116, 51044597400,
        52242775200, 53463348120, 54706628644, 55972932147, 57262576914, 58575884147, 59913177984, 61274785507, 62661036761, 64072264761,
        65508805511, 66970998015, 68459184288, 69973709375, 71514921358, 73083171375, 74678813628, 76302205402, 77953707072, 79633682122,
        81342497156, 83080521909, 84848129266, 86645695269, 88473599136, 90332223269, 92221953273, 94143177963, 96096289383, 98081682817,
        100099756800, 102150913137, 104235556910, 106354096497, 108506943580, 110694513164, 112917223584, 115175496524, 117469757028, 119800433511,
        122167957778, 124572765031, 127015293888, 129495986391, 132015288025, 134573647725, 137171517895, 139809354419, 142487616672, 145206767539,
        147967273422, 150769604259, 153614233532, 156501638286, 159432299136, 162406700286, 165425329540, 168488678313, 171597241650, 174751518233,
        177952010400, 181199224153, 184493669177, 187835858847, 191226310247,
    };

    public const int MaxLevel = 275;

    /// <summary>XP still needed for the next level, and the fraction of this level done; (0, 1) at 275 and above.</summary>
    public (long ToNext, float Fraction) LevelProgress()
    {
        if (Level <= 0 || Level >= MaxLevel) return (0, 1f);
        long start = LevelXp[Level], next = LevelXp[Level + 1];
        long toNext = Math.Max(0, next - TotalXp);
        float frac = next > start ? (float)Math.Clamp((double)(TotalXp - start) / (next - start), 0, 1) : 1f;
        return (toNext, frac);
    }
}
