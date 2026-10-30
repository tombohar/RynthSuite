using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// An item's wield requirements against the character (ACE: PropertyInt WieldRequirements 158 /
/// WieldSkillType 159 / WieldDifficulty 160, and the 2nd-4th sets 270-278; WieldRequirement 1 Skill,
/// 2 RawSkill, 7 Level, 8 Training). The client refuses a wield it can't meet without asking the
/// server, so a refused wield never got an answer and was retried: Drakkon's Tetsubo (Two Handed
/// Combat, untrained) took the off-hand weapon off for every fight (2026-10-18), his Electric Baton
/// and Acid Staff (War Magic) held up buffing. Read from the item's appraisal; unknown = not known
/// (the caller decides), never "met". A Skill requirement is checked against the buffed skill (vitae
/// lowers it, as on the server); a RawSkill one against the base skill (vitae doesn't).
/// </summary>
internal static class WieldRequirements
{
    private static readonly (uint Req, uint Skill, uint Diff)[] Sets =
    {
        (158, 159, 160), (270, 271, 272), (273, 274, 275), (276, 277, 278),
    };
    private const uint PropLevel = 25;

    /// <summary>The first requirement of <paramref name="itemId"/> the character doesn't meet, or null (met, none, or not known).</summary>
    public static string? Unmet(RynthCoreHost host, CharacterSkills? skills, uint itemId)
    {
        if (!host.HasGetObjectIntProperty) return null;
        foreach (var (pReq, pSkill, pDiff) in Sets)
        {
            if (!host.TryGetObjectIntProperty(itemId, pReq, out int req) || req == 0) continue;
            host.TryGetObjectIntProperty(itemId, pDiff, out int diff);
            switch (req)
            {
                case 1: case 2: case 8:   // Skill (buffed), RawSkill (base), Training
                {
                    if (skills == null || !host.TryGetObjectIntProperty(itemId, pSkill, out int stype)) continue;
                    AcSkillType? skill = SkillOf((uint)stype);
                    if (skill == null) continue;
                    var s = skills[skill.Value];
                    string name = Name(skill.Value);
                    if (s.Training < 2) return $"needs {name}, untrained";
                    if (req == 8 || diff <= 0) continue;
                    if (req == 2)
                    {
                        // RawSkill: the base skill, without buffs and without vitae (ACE CreatureSkill.Base;
                        // the client's own raw read, which is what the client checks before it sends the
                        // wield). The buffed read carries the vitae penalty: at 40% vitae a Shashqa
                        // (base Two Handed Combat 420) read as 371 and was left out of the weapon choice,
                        // though it wields fine (2026-10-23). Base not readable: not known.
                        uint pid = host.GetPlayerId();
                        if (pid != 0 && host.HasGetObjectSkillBuffed
                            && host.TryGetObjectSkillLevel(pid, (uint)stype, raw: 1, out int baseLevel) && baseLevel > 0
                            && baseLevel < diff)
                            return $"needs base {name} {diff}, you have {baseLevel}";
                        continue;
                    }
                    // Skill: the buffed skill, vitae included (ACE CreatureSkill.Current), as read.
                    if (s.Buffed > 0 && s.Buffed < diff) return $"needs {name} {diff}, you have {s.Buffed}";
                    continue;
                }
                case 7:   // Level
                {
                    uint pid = host.GetPlayerId();
                    if (pid != 0 && host.TryGetObjectIntProperty(pid, PropLevel, out int level) && level > 0 && diff > 0 && level < diff)
                        return $"needs level {diff}, you are {level}";
                    continue;
                }
            }
        }
        return null;
    }

    /// <summary>The item has a wield requirement (any set) per its appraisal.</summary>
    public static bool Has(RynthCoreHost host, uint itemId)
    {
        if (!host.HasGetObjectIntProperty) return false;
        foreach (var (pReq, _, _) in Sets)
            if (host.TryGetObjectIntProperty(itemId, pReq, out int req) && req != 0) return true;
        return false;
    }

    private static AcSkillType? SkillOf(uint stype)
    {
        if (stype == 0) return null;
        foreach (AcSkillType k in Enum.GetValues<AcSkillType>())
            if (CharacterSkills.AcSkillTypeToSTypeSkill(k) == stype) return k;
        return null;
    }

    private static string Name(AcSkillType k)
    {
        string s = k.ToString();
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
