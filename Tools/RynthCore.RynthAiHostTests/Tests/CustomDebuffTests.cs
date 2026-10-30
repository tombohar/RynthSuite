using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-08: with Corruption V and VII known, the custom debuff "Corruption" cast V. A typed-in
// debuff was capped by Creature and Life alone; Corruption is Void, so the search started at VI.
internal static class CustomDebuffTests
{
    private const uint Player = 0x50000180;
    private const uint STypeCreature = 31, STypeLife = 33, STypeVoid = 43;

    public static void Register(Runner r)
    {
        r.Add("custom debuff: a base name casts the highest known level, capped by Void too (Corruption VII, not V)", VoidCap);
    }

    private static void VoidCap()
    {
        FakeHost.Reset();
        foreach (string n in new[] { "Corruption V", "Corruption VII" })
            foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeCreature)] = (250, 2);   // tier 5 by the thresholds below
        FakeHost.Skills[(Player, STypeLife)] = (250, 2);
        FakeHost.Skills[(Player, STypeVoid)] = (400, 3);       // tier 7
        var s = new LegacyUiSettings
        {
            MinSkillLevelTier1 = 0, MinSkillLevelTier2 = 50, MinSkillLevelTier3 = 100, MinSkillLevelTier4 = 150,
            MinSkillLevelTier5 = 200, MinSkillLevelTier6 = 300, MinSkillLevelTier7 = 350, MinSkillLevelTier8 = 10000,
        };
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, System.Array.Empty<uint>());
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        sm.RefreshKnownSpells();
        c.SetSpellManager(sm);

        MethodInfo find = typeof(CombatManager).GetMethod("FindBestCustomDebuffWithTier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object[] args = { "Corruption", 0 };
        int id = (int)find.Invoke(c, args)!;
        Check.Eq(SpellTableStub.GetById(id)?.Name, "Corruption VII", "the highest known level");
        Check.Eq((int)args[1], 7, "tier 7");

        object[] args5 = { "Corruption V", 0 };
        Check.Eq(SpellTableStub.GetById((int)find.Invoke(c, args5)!)?.Name, "Corruption V", "a full name still casts as is");
    }
}
