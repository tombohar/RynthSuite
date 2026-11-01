using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-01: "damage on wands isn't being read by the loot properly; someone thinks it's missing
// them". Casters through LootSnobV4's own wand rules (Fixtures\lootsnob-excerpt.utl: its No Wield,
// Void and War sections, as LootSnob ships them), with tier 8 values as ACE's caster mutations make
// them: elemental damage vs monsters (152) 1.10-1.18 at wield 355/375/385, melee defense (29) 1.0x,
// mana conversion (144), magic defense (150), and the cantrips that add to them (Spirit Thirst,
// Defender, Hermetic Link). The values are read after the ID, through the host, on a corpse too.
//
// What was wrong:
//  - Until 2026.10.31.2 a "Buffed Double Key" read the plain value without the item's cantrips. A
//    loot caster tops out at 1.18, so every "(W) War/Void (OD +1..+7)" rule (1.19-1.25) failed; on
//    Conquest's CQ_Lootsnob_lite, whose War and Void rules are nearly all OD rules, no war or void
//    wand was kept (the 10-19 report, on 2026.10.17.1). Fixed there; checked here.
//  - The installed LootSnobV4 on the PC has the War "Max 385/375/355", "18pvm + NNmd", "amazing 355",
//    "25MD"-"21MD" and Void "Max" rules switched off in the Loot Editor (the stock file has them on),
//    so typical tier 8 war wands were left. A profile matter, but nothing said so: /ra loot why
//    skipped switched-off rules and the log said "leave". Both now name the switched-off rule that
//    would take the item.
internal static class LootWandTests
{
    private const uint Player = 0x50000E41;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // Property ids (ACE PropertyInt / PropertyFloat).
    private const int ItemType = 1, Burden = 5, ValidLoc = 9, Value = 19, DamageType = 45, Workmanship = 105, Material = 131,
        WieldReq = 158, WieldSkill = 159, WieldDiff = 160;
    private const int MeleeDef = 29, ManaConv = 144, MissileDef = 149, MagicDef = 150, VsMonsters = 152;
    private const int WarMagic = 34, VoidMagic = 43, Ebony = 73;   // Ebony: no Salvage rule in the excerpt

    public static void Register(Runner r)
    {
        r.Add("loot wands: tier 8 war and void casters get LootSnob's own wand rules (Max 385/375/355, OD, MD, MC)", WandsGetTheirRules);
        r.Add("loot wands: the cantrips count (Spirit Thirst OD +2/+5, Defender, Hermetic Link), as floats", WandCantrips);
        r.Add("loot wands: without its ID a caster waits for it; elemental damage is ID data", WandWithoutId);
        r.Add("loot wands: a switched-off rule that would take the wand is named (/ra loot why, chat, window)", SwitchedOffRuleNamed);
        r.Add("loot wands: on a corpse the wand's damage is read from the host after its ID and it is kept", CorpseWandKept);
        r.Add("loot wands: on a corpse, a wand left because its rule is switched off says so in the log", CorpseWandSwitchedOff);
    }

    private static VTankLootProfile Excerpt()
        => VTankLootParser.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lootsnob-excerpt.utl"));

    private static VTankLootContext Ctx() => new(FakeHost.Create(), Player);

    private static uint Spell(string name)
    {
        int id = SpellDatabase.GetIdByName(name);
        if (id == 0) throw new InvalidOperationException($"spell '{name}' is not in SpellData.txt");
        return (uint)id;
    }

    /// <summary>A caster as ACE's tier 8 caster mutations make it, identified.</summary>
    private static Item Caster(string name, int skill, int wield, double vsMonsters, double def, double manaConv = 0.06,
        double magicDef = 1.02, params string[] spells)
    {
        var c = new Item(name, AcObjectClass.WandStaffOrb)
            .Int(ItemType, 0x8000).Int(ValidLoc, 0x01000000).Int(Value, 25000).Int(Burden, 50).Int(Material, Ebony)
            .Int(Workmanship, 6).Dbl(MeleeDef, def).Dbl(ManaConv, manaConv).Dbl(MissileDef, 1.03).Dbl(MagicDef, magicDef);
        if (wield > 0) c.Int(WieldReq, 2).Int(WieldSkill, skill).Int(WieldDiff, wield);
        if (vsMonsters > 0) c.Dbl(VsMonsters, vsMonsters).Int(DamageType, skill == VoidMagic ? 0x400 : 0x10);
        if (spells.Length > 0) FakeHost.SpellIds[unchecked((uint)c.Wo.Id)] = spells.Select(Spell).ToArray();
        return c;
    }

    private static string? First(VTankLootProfile p, Item item) => VTankLootEvaluator.FirstMatch(p, item, Ctx(), out _)?.Name.Trim();

    private static void WandsGetTheirRules()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        Check.Eq(First(p, Caster("Fire Sceptre", WarMagic, 385, 1.18, 1.12)), "(W) War (Max 385)", "1.18 at 385: War (Max 385)");
        Check.Eq(First(p, Caster("Frost Baton", WarMagic, 375, 1.15, 1.10)), "(W) War (Max 375)", "1.15 at 375: War (Max 375)");
        Check.Eq(First(p, Caster("Acid Staff", WarMagic, 355, 1.13, 1.08)), "(W) War (Max 355)", "1.13 at 355: War (Max 355)");
        Check.Null(First(p, Caster("Fire Baton", WarMagic, 375, 1.14, 1.08)), "1.14 at 375, 1.08 defense: no rule (LootSnob leaves it)");
        Check.Eq(First(p, Caster("Blunt Sceptre", WarMagic, 385, 1.18, 1.20)), "(W) War (18pvm + 20md)", "1.18 and 1.20 defense");
        Check.Eq(First(p, Caster("Piercing Sceptre", WarMagic, 355, 1.17, 1.25)), "(W) War (amazing 355)", "1.17 at 355 with 1.25 defense");
        Check.Eq(First(p, Caster("Slashing Sceptre", WarMagic, 385, 1.10, 1.26)), "(W) War (26MD)", "a war caster on defense alone");
        Check.Eq(First(p, Caster("Nether Sceptre", VoidMagic, 385, 1.18, 1.12)), "(W) Void (Max 385)", "void 1.18 at 385");
        Check.Eq(First(p, Caster("Nether Baton", VoidMagic, 375, 1.18, 1.17)), "(W) Void (18pvm + 17md)", "void 1.18 and 1.17 defense");
        Check.Null(First(p, Caster("Nether Staff", VoidMagic, 355, 1.12, 1.05)), "void 1.12 at 355: under Void (Max 355)'s 1.13");
        // The name filters are LootSnob's: War wants Baton|Sceptre|Staff, Void wants Nether.
        Check.Null(First(p, Caster("Fire Wand", WarMagic, 385, 1.18, 1.12)), "a war caster named 'Wand' is outside LootSnob's War names");
        Check.Eq(First(p, Caster("Orb", 0, 0, 0, 1.24)), "(W) No Wield (24MD)", "an orb with no wield requirement on defense");
        Check.Eq(First(p, Caster("Orb", 0, 0, 0, 1.20, manaConv: 0.10, magicDef: 1.0)), "(W) No Wield (20MD + 10MC)", "20 defense and 10% mana conversion");
        Check.Eq(First(p, Caster("Orb", 0, 0, 0, 1.20, manaConv: 0.05, magicDef: 1.04)), "(W) No Wield (20MD + 4MgcD)", "20 defense and 4% magic defense");
        Check.Null(First(p, Caster("Orb", 0, 0, 0, 1.20, manaConv: 0.05, magicDef: 1.0)), "20 defense alone");
        Check.Null(First(p, Caster("Orb", WarMagic, 330, 0, 1.24)), "an orb with a wield requirement (330) is not 'No Wield', and War wants Baton|Sceptre|Staff");
    }

    private static void WandCantrips()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        Check.Eq(First(p, Caster("Electric Baton", WarMagic, 385, 1.17, 1.10, spells: "Major Spirit Thirst")), "(W) War (OD +2)",
            "1.17 + Major Spirit Thirst 0.03 = 1.20 (as floats)");
        Check.Eq(First(p, Caster("Nether Sceptre", VoidMagic, 385, 1.16, 1.10, spells: "Legendary Spirit Thirst")), "(W) Void (OD +5)",
            "1.16 + Legendary Spirit Thirst 0.07 = 1.23");
        Check.Eq(First(p, Caster("Fire Baton", WarMagic, 375, 1.14, 1.10, spells: "Epic Spirit Thirst")), "(W) War (OD +1)",
            "1.14 + Epic Spirit Thirst 0.05 = 1.19");
        Check.Eq(First(p, Caster("Frost Baton", WarMagic, 375, 1.14, 1.10, spells: "Minor Spirit Thirst")), "(W) War (Max 375)",
            "Max 375 reads the buffed value too: 1.14 + 0.01");
        Check.Eq(First(p, Caster("Orb", 0, 0, 0, 1.17, spells: "Epic Defender")), "(W) No Wield (24MD)", "1.17 + Epic Defender 0.07");
        Check.Null(First(p, Caster("Orb", 0, 0, 0, 1.20, manaConv: 0.08, magicDef: 1.0, spells: "Minor Hermetic Link")),
            "0.08 x Minor Hermetic Link 1.10 = 0.088: under 10% mana conversion");
        Check.Eq(First(p, Caster("Orb", 0, 0, 0, 1.20, manaConv: 0.08, magicDef: 1.0, spells: "Legendary Hermetic Link")), "(W) No Wield (20MD + 10MC)",
            "0.08 x Legendary Hermetic Link 1.30 = 0.104");
        // A castable item spell is not a cantrip: it adds nothing.
        Check.Eq(First(p, Caster("Fire Sceptre", WarMagic, 385, 1.18, 1.12, spells: "Flame Bolt VI")), "(W) War (Max 385)", "its own war spell adds nothing");
    }

    private static void WandWithoutId()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        // What the CreateObject shows: name, class, value, burden, material. No 152, wield or defense.
        var bare = new Item("Fire Sceptre", AcObjectClass.WandStaffOrb).Int(ItemType, 0x8000).Int(ValidLoc, 0x01000000)
            .Int(Value, 25000).Int(Burden, 50).Int(Material, Ebony);
        Check.Eq(VTankLootEvaluator.Decide(p, bare, Ctx(), hasIdData: false, out int idx), VTankLootVerdict.NeedsId, "a caster waits for its ID");
        Check.Eq(p.Rules[idx].Name.Trim(), "(W) No Wield (29MD)", "at LootSnob's first wand rule");
        static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(VTankNodeTypes.BuffedDoubleValKeyGE, "1.18", "152")), "buffed elemental damage needs the ID");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(VTankNodeTypes.DoubleValKeyGE, "1.18", "152")), "plain elemental damage too");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(VTankNodeTypes.LongValKeyE, "385", "160")), "and the wield difficulty");
        Check.Null(VTankLootEvaluator.FirstMatch(p, bare, Ctx(), out _), "judged with 0s nothing takes it (why it must wait)");
    }

    private static VTankLootProfile WithOff(params string[] names)
    {
        VTankLootProfile p = Excerpt();
        foreach (VTankLootRule r in p.Rules)
            if (names.Contains(r.Name.Trim())) LootEditSessionOff(r);
        return p;
    }

    // As the Loot Editor switches a rule off (LootEditSession.SetEnabled: a DisabledRule node "true" at the end).
    private static void LootEditSessionOff(VTankLootRule r)
        => r.Conditions.Add(new VTankLootCondition(VTankNodeTypes.DisabledRule, "0", new[] { "true" }));

    private static void SwitchedOffRuleNamed()
    {
        FakeHost.Reset();
        VTankLootProfile p = WithOff("(W) War (Max 385)", "(W) War (18pvm + 20md)");
        var wand = Caster("Fire Sceptre", WarMagic, 385, 1.18, 1.12);
        Check.Null(First(p, wand), "with War (Max 385) off, nothing takes the 1.18 / 385 sceptre");
        int off = VTankLootEvaluator.FirstSwitchedOffMatch(p, wand, Ctx());
        Check.True(off >= 0 && p.Rules[off].Name.Trim() == "(W) War (Max 385)", $"the switched-off rule that would take it ({(off >= 0 ? p.Rules[off].Name : "none")})");
        Check.Eq(VTankLootEvaluator.FirstSwitchedOffMatch(p, Caster("Fire Baton", WarMagic, 375, 1.14, 1.08), Ctx()), -1, "none for a wand no rule wants");
        Check.Eq(VTankLootEvaluator.FirstSwitchedOffMatch(Excerpt(), wand, Ctx()), -1,
            "LootSnob's own switched-off rules (the 'leave this disabled' separators, Creature Cantrip ...) don't take it");
        Check.True(VTankLootEvaluator.FirstSwitchedOffMatch(p, wand, Ctx(), end: off) == -1, "only rules above the end count");

        LootWhy.Result why = LootWhy.Explain(p, wand, Ctx(), hasIdData: true);
        Check.Eq(why.Verdict, VTankLootVerdict.NoMatch, "no rule matches");
        Check.Eq(why.SwitchedOffName, "(W) War (Max 385)", "why names the switched-off rule");
        Check.Eq(why.SwitchedOffAction, VTankLootAction.Keep, "and what it does");
        Check.True(why.NearMisses.Any(m => m.RuleName == "(W) War (OD +7)" && m.Failed.Contains("read 1.18")),
            "the OD rules came close, reading its 1.18 (" + string.Join(" | ", why.NearMisses.Take(3).Select(LootWhy.NearMissLine)) + ")");

        LootWhy.Report r = LootWhy.Describe("Fire Sceptre", true, "left", "LootSnobV4.utl", p.Rules.Count, why, askedForId: false);
        string expect = $"#{off + 1} '(W) War (Max 385)' [Keep]";
        Check.Eq(r.SwitchedOff, expect, "the report carries it");
        var lines = LootWhy.ChatLines(r, 3);
        Check.Eq(lines[2], "[RynthAi]   Switched off in the profile: " + expect
            + " would take it. Switch that rule on in the Loot Editor to loot items like this.", "chat says so, after the decision");
        Check.True(lines.Count > 3 && lines[3].Contains("came close"), "then the near misses as before");

        // A rule that takes the item comes first: a switched-off rule below the winner isn't named.
        VTankLootProfile q = WithOff("(W) War (Max 375)");
        LootWhy.Result kept = LootWhy.Explain(q, wand, Ctx(), hasIdData: true);
        Check.Eq(kept.RuleName, "(W) War (Max 385)", "kept by Max 385");
        Check.Eq(kept.SwitchedOffIndex, -1, "Max 375 (off, below it) isn't named");
        Check.Eq(LootWhy.Describe("Fire Sceptre", true, "[Keep] (W) War (Max 385)", "x.utl", q.Rules.Count, kept, false).SwitchedOff, "",
            "nothing switched off in its report");

        // LootSnob ships its Salvage rules off: /ra loot why names one (the player may want it), the
        // looter's log line (keeps only) doesn't, or nearly every left item would carry one.
        VTankLootProfile s = Excerpt();
        var ebony = new VTankLootRule { Name = "(Salv) Ebony", Action = VTankLootAction.Salvage };
        ebony.Conditions.Add(new VTankLootCondition(VTankNodeTypes.LongValKeyE, "0", new[] { Ebony.ToString(), Material.ToString() }));
        LootEditSessionOff(ebony);
        s.Rules.Add(ebony);
        var plainBaton = Caster("Fire Baton", WarMagic, 375, 1.14, 1.08);
        Check.Eq(VTankLootEvaluator.FirstSwitchedOffMatch(s, plainBaton, Ctx()), s.Rules.Count - 1, "any action: the switched-off Ebony salvage");
        Check.Eq(VTankLootEvaluator.FirstSwitchedOffMatch(s, plainBaton, Ctx(), keepsOnly: true), -1, "keeps only: none");
        Check.Eq(LootWhy.Explain(s, plainBaton, Ctx(), hasIdData: true).SwitchedOffAction, VTankLootAction.Salvage, "/ra loot why names the salvage rule");

        // Without the ID its stats read 0: no switched-off rule is judged.
        var bare = new Item("Fire Sceptre", AcObjectClass.WandStaffOrb).Int(ItemType, 0x8000).Int(Value, 25000);
        Check.Eq(LootWhy.Explain(p, bare, Ctx(), hasIdData: false).SwitchedOffIndex, -1, "no ID: none named");
    }

    // ── On a corpse: the looter, the host's reads, the log ───────────────────

    private const uint Corpse = 0x80265100;
    private const uint Wand = 0x80265101;
    private const uint LongDescKey = 16;
    private const string Me = "Test Looter";
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);
    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 70_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;          // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;           // RequestId, HasAppraisalData
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Tusker Guard";
        FakeHost.ItemTypes[Corpse] = 0x200;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        // The CreateObject: name, type, slot, value, burden, material.
        FakeHost.Names[Wand] = "Fire Sceptre";
        FakeHost.ItemTypes[Wand] = 0x8000;
        FakeHost.Containers[Wand] = Corpse;
        foreach (var (k, v) in new (uint, int)[] { (1, 0x8000), (9, 0x01000000), (19, 28640), (5, 50), (131, Ebony) })
            FakeHost.Ints[(Wand, k)] = v;
    }

    /// <summary>The ID lands: wield 385 War Magic, fire, workmanship 6; 1.18 vs monsters, 1.12 defense.</summary>
    private static void Identify()
    {
        foreach (var (k, v) in new (uint, int)[] { (105, 6), (158, 2), (159, WarMagic), (160, 385), (45, 0x10) })
            FakeHost.Ints[(Wand, k)] = v;
        FakeHost.Doubles[(Wand, VsMonsters)] = 1.18;
        FakeHost.Doubles[(Wand, MeleeDef)] = 1.12;
        FakeHost.Doubles[(Wand, ManaConv)] = 0.06;
        FakeHost.Doubles[(Wand, MissileDef)] = 1.03;
        FakeHost.Doubles[(Wand, MagicDef)] = 1.02;
        FakeHost.SpellIds[Wand] = new[] { Spell("Flame Bolt VI") };
        FakeHost.Appraised.Add(Wand);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
            LootAssessWindowMs = 200, LootCorpseTimeoutMs = 12_000,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host, VTankLootProfile profile, string file)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });
            string path = Path.Combine(Program.TempRoot, file);
            File.WriteAllText(path, VTankLootWriter.Serialize(profile));
            S.CurrentLootPath = path;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public void Tick()
        {
            Set("_activity", BotActivity.Looting);
            typeof(RynthAiPlugin).GetMethod("TickCorpseOpening", Any)!.Invoke(P, null);
        }
        public bool Completed(uint corpse) =>
            Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)corpse));
    }

    private static bool Moved() => FakeHost.ExternalMoves.Any(m => m.Id == Wand);

    private static Bot OpenCorpse(VTankLootProfile profile, string file)
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, profile, file);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.OnCreateObject(Wand);
        bot.Cache.Tick();
        bot.Tick();
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");
        FakeHost.UseDone(0);
        bot.P.OnViewObjectContents(Corpse);
        for (int i = 0; i < 5; i++) { _now += 20; bot.Tick(); }
        Check.True(FakeHost.IdRequests.Contains(Wand), "the wand's ID is asked for (LootSnob's wand rules need it)");
        Check.False(Moved(), "not taken before its ID");
        return bot;
    }

    private static void CorpseWandKept() => Clocked(() =>
    {
        var bot = OpenCorpse(Excerpt(), "loot-wand-stock.utl");
        Identify();
        // Read through the host as the looter reads it: the WorldObject in the cache, no overlay.
        WorldObject? wo = bot.Cache[unchecked((int)Wand)];
        Check.NotNull(wo, "the wand is in the cache");
        Check.Near(wo?.Values((DoubleValueKey)VsMonsters, 0.0) ?? 0, 1.18, 1e-9, "elemental damage vs monsters read from the host after the ID");
        for (int i = 0; i < 200 && !Moved(); i++) { _now += 20; bot.Tick(); }
        Check.True(Moved(), "taken once the ID is in");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Loot rule: Fire Sceptre -> [Keep] (W) War (Max 385)")),
            "by LootSnob's War (Max 385) (chat: " + string.Join(" | ", FakeHost.Chat.Where(c => c.Contains("Loot rule"))) + ")");
    });

    private static void CorpseWandSwitchedOff() => Clocked(() =>
    {
        VTankLootProfile p = WithOff("(W) War (Max 385)");
        var bot = OpenCorpse(p, "loot-wand-off.utl");
        Identify();
        for (int i = 0; i < 1000 && !bot.Completed(Corpse); i++) { _now += 20; bot.Tick(); }
        Check.False(Moved(), "left: the rule that wants it is off");
        int off = p.Rules.FindIndex(r => r.Name.Trim() == "(W) War (Max 385)");
        string note = $"-> leave (switched-off rule #{off + 1} '(W) War (Max 385)' [Keep] would take it)";
        Check.True(FakeHost.Logs.Any(l => l.Contains("[LootEval] 'Fire Sceptre'") && l.Contains(note)),
            "the log's leave line names the switched-off rule (" + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[LootEval] 'Fire Sceptre'"))) + ")");
    });
}
