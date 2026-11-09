using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Whether an essence summons a combat pet, from the client's data, not its name (2026-11-08,
/// R-20261108-215240-651b: on 2026.11.8.1 the combat summoner kept summoning a "Common Golem
/// Essence", a passive pet that never fights or despawns, ahead of the two combat pets the player
/// had ticked. It was in the Items list as a Pet entry because its name ends in "Essence", and Auto
/// put it first because "Golem" reads as Bludgeon).
///
/// Every combat pet device in the ACE world data (259 of them) carries a charge count (Structure /
/// MaxStructure), a Summoning use requirement (UseRequiresSkill 54) and the summoning-gem icon
/// underlay; none of the 29 passive ones (crates, eggs) carries any of these, and the client is sent
/// all three. That report's golem essence had been appraised (5 ints, no charge count: "charges=-1")
/// before it was used. ACECustom's essences are combat by their wcid block, bond or potency.
///
/// So an essence is <see cref="Verdict.Combat"/> on any of those signs, <see cref="Verdict.NotCombat"/>
/// when it has been appraised and shows none of them, and <see cref="Verdict.Unknown"/> before its
/// appraisal (nothing is concluded from a read the client may not have yet). An essence whose use
/// brought a pet out and that then judges NotCombat is learned for the session
/// (<see cref="Learn"/>), which the Mini Remote Pets tab reads to show it as Cosmetic.
/// </summary>
internal static class PetDeviceKind
{
    internal enum Verdict { Unknown, Combat, NotCombat }

    private const uint IntMaxStructure = 91;
    private const uint IntStructure = 92;
    private const uint IntUseRequiresSkill = 366;
    private const uint IntUseRequiresSkillSpec = 368;
    private const uint IntBondLevel = 9053;      // ACECustom pet essence
    private const uint IntPotencyStored = 9056;  // ACECustom pet essence
    private const int SkillSummoning = 54;
    private const uint DidIconUnderlay = 52;
    private const uint SummonGemUnderlay = 0x007420;

    private static readonly object Gate = new();
    private static readonly HashSet<int> LearnedNotCombat = new();

    /// <summary>The essence's verdict now (see the class summary).</summary>
    public static Verdict Judge(RynthCoreHost host, uint id)
    {
        if (host.HasGetObjectWcid && host.TryGetObjectWcid(id, out uint w) && w is >= 787801001 and <= 787801072)
            return Verdict.Combat;   // ACECustom combat essence block
        if (host.HasGetObjectIntProperty)
        {
            if (host.TryGetObjectIntProperty(id, IntStructure, out _) || host.TryGetObjectIntProperty(id, IntMaxStructure, out _))
                return Verdict.Combat;
            if ((host.TryGetObjectIntProperty(id, IntUseRequiresSkill, out int s) && s == SkillSummoning)
                || (host.TryGetObjectIntProperty(id, IntUseRequiresSkillSpec, out int sp) && sp == SkillSummoning))
                return Verdict.Combat;
            if ((host.TryGetObjectIntProperty(id, IntBondLevel, out int bond) && bond > 0)
                || host.TryGetObjectIntProperty(id, IntPotencyStored, out _))
                return Verdict.Combat;
        }
        if (host.HasGetObjectDataIdProperty && host.TryGetObjectDataIdProperty(id, DidIconUnderlay, out uint did)
            && (did & 0x00FFFFFF) == SummonGemUnderlay)
            return Verdict.Combat;
        // Nothing is concluded without the item's properties: an engine that can't read them, or
        // an appraisal that hasn't landed.
        if (!host.HasGetObjectIntProperty || !host.HasHasAppraisalData || !host.HasAppraisalData(id))
            return Verdict.Unknown;
        return Verdict.NotCombat;
    }

    /// <summary>Appraised, and none of a combat pet device's signs; or learned so this session.</summary>
    public static bool IsNotCombat(RynthCoreHost host, uint id)
        => IsLearned(unchecked((int)id)) || Judge(host, id) == Verdict.NotCombat;

    /// <summary>Records an essence that brought out a pet and is no combat pet device.</summary>
    public static bool Learn(int id)
    {
        lock (Gate)
        {
            if (LearnedNotCombat.Count > 256) LearnedNotCombat.Clear();
            return LearnedNotCombat.Add(id);
        }
    }

    public static bool IsLearned(int id)
    {
        lock (Gate) return LearnedNotCombat.Contains(id);
    }

    /// <summary>Forgets what this session learned (a new PetManager, the tests).</summary>
    public static void ResetSession()
    {
        lock (Gate) LearnedNotCombat.Clear();
    }
}
