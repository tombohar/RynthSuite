using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-27: the Void debuffs as Monsters-rule switches (VTank's Cs, Cp, DC, WC and FC columns):
// Corrosion, Corruption and Destructive Curse (Nether damage over time), Weakening Curse and
// Festering Curse. They were only castable as typed-in custom debuffs.
internal static class VoidDebuffTests
{
    private const uint Player = 0x50000181;
    private const uint STypeCreature = 31, STypeLife = 33, STypeVoid = 43;

    public static void Register(Runner r)
    {
        r.Add("void debuffs: every one has levels I-VII and an Incantation in the spell data, with its duration", SpellData);
        r.Add("void debuffs: a rule's switches go in the debuff list after the Creature ones", DebuffList);
        r.Add("void debuffs: the level is capped by Void Magic alone", VoidCap);
        r.Add("void debuffs: an engine that doesn't send them can't turn them off", Carried);
    }

    private static void SpellData()
    {
        var seconds = new Dictionary<string, int>
        {
            ["Corrosion"] = 16, ["Corruption"] = 31, ["Destructive Curse"] = 31, ["Weakening Curse"] = 15, ["Festering Curse"] = 30,
        };
        Check.Eq(CombatManager.VoidDebuffBases.Length, 5, "five Void debuffs");
        string[] numerals = { "I", "II", "III", "IV", "V", "VI", "VII" };
        foreach (string b in CombatManager.VoidDebuffBases)
        {
            foreach (string n in numerals)
                Check.True(SpellDatabase.GetIdByName($"{b} {n}") > 0, $"{b} {n} exists");
            int top = SpellDatabase.GetIdByName($"Incantation of {b}");
            Check.True(top > 0, $"Incantation of {b} exists");
            Check.Eq(SpellDatabase.DurationSeconds(top), seconds[b], $"{b} lasts {seconds[b]} s");
        }
    }

    private static void DebuffList()
    {
        MethodInfo build = typeof(CombatManager).GetMethod("BuildDebuffList", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rule = new MonsterRule { Name = "Mosswart", Imperil = true, Corrosion = true, DestructiveCurse = true, FesteringCurse = true, CustomDebuffs = "Corrosion Vulnerability Other" };
        var list = (List<string>)build.Invoke(null, new object[] { rule, "Fire" })!;
        Check.Eq(string.Join("|", list), "Imperil|Void:Corrosion|Void:Destructive Curse|Void:Festering Curse|Custom:Corrosion Vulnerability Other",
            "Creature first, then Void, then the typed-in ones");
        var none = (List<string>)build.Invoke(null, new object[] { new MonsterRule { Name = "Drudge" }, "Fire" })!;
        Check.Eq(none.Count, 0, "a new rule (and an old file) has none on");
    }

    private static void VoidCap()
    {
        FakeHost.Reset();
        foreach (string n in new[] { "Corrosion I", "Corrosion II", "Corrosion VII", "Weakening Curse VII" })
            foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeCreature)] = (400, 3);   // tier 7 by the thresholds below
        FakeHost.Skills[(Player, STypeLife)] = (400, 3);
        FakeHost.Skills[(Player, STypeVoid)] = (60, 2);        // tier 2
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

        MethodInfo find = typeof(CombatManager).GetMethod("FindBestVoidDebuffWithTier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object[] args = { "Corrosion", 0 };
        int id = (int)find.Invoke(c, args)!;
        Check.Eq(SpellTableStub.GetById(id)?.Name, "Corrosion II", "Void at tier 2 casts II, though Creature and Life reach VII");
        Check.Eq((int)args[1], 2, "tier 2");

        object[] curse = { "Weakening Curse", 0 };
        Check.Eq((int)find.Invoke(c, curse)!, 0, "only VII known, above the Void cap: nothing to cast");
        object[] unknown = { "Corruption", 0 };
        Check.Eq((int)find.Invoke(c, unknown)!, 0, "a debuff the character doesn't know: nothing");
    }

    private static void Carried()
    {
        var current = new List<MonsterRule>
        {
            new() { Name = "Default", Corrosion = true, WeakeningCurse = true },
            new() { Name = "Drudge", Corruption = true, DestructiveCurse = true, FesteringCurse = true },
        };
        var incoming = new List<MonsterRule> { new() { Name = "Default" }, new() { Name = "drudge" }, new() { Name = "Mosswart" } };
        LegacyDashboardRenderer.CarryVoidDebuffs(current, incoming);
        Check.True(incoming[0].Corrosion && incoming[0].WeakeningCurse && !incoming[0].Corruption, "Default keeps its own");
        Check.True(incoming[1].Corruption && incoming[1].DestructiveCurse && incoming[1].FesteringCurse, "matched by name, any case");
        Check.False(incoming[2].Corrosion, "a new rule stays off");

        // The bridge looks for the key "Corrosion": a rule saved with it has it, and a typed-in
        // debuff named Corrosion is a value, not the key.
        string json = JsonSerializer.Serialize(new List<MonsterRule> { new() { Name = "X", CustomDebuffs = "Corrosion" } }, RynthAiJsonContext.Default.MonsterRuleList);
        Check.True(json.Contains("\"Corrosion\":"), "the plugin writes the key with a colon after it");
        string old = "[{\"Name\":\"X\",\"CustomDebuffs\":\"Corrosion\"}]";
        Check.False(old.Contains("\"Corrosion\":"), "a typed-in Corrosion doesn't look like the key");
    }
}
