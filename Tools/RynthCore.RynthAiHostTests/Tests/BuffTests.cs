using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Buff and spell selection: which tier a buffed skill allows (SpellManager), which spell id a
// buff resolves to at each tier (lore names, Incantations, auras, banes, the spellbook
// snapshot), how long the bot thinks a buff lasts, and when it recasts (BuffManager.NeedsAnyBuff
// over the live enchantment registry and chat-confirmed item spells). Expectations about spell
// names, tiers and durations are read from the plugin's own SpellData.txt (Fakes\SpellTable).
internal static class BuffTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeCreature = 31, STypeItem = 32, STypeLife = 33;

    /// <summary>Buff/tier pairs the name tables used to miss (fixed by 940cb21); NameGapsResolve checks them by name.</summary>
    private static readonly HashSet<(string Buff, int Tier)> NameGaps = new()
    {
        ("Bludgeoning Bane", 1), ("Bludgeoning Bane", 2), ("Bludgeoning Bane", 3), ("Bludgeoning Bane", 4),
        ("Bludgeoning Bane", 5), ("Bludgeoning Bane", 6), ("Bludgeoning Bane", 8),
        ("Mana Conversion Mastery", 7), ("Two Handed Combat Mastery", 7), ("Magic Item Tinkering Expertise", 7),
    };

    private static readonly string[] Auras =
        { "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self", "Spirit Drinker Self", "Swift Killer Self", "Defender Self" };

    public static void Register(Runner r)
    {
        r.Add("buff tier: highest tier for a buffed skill, at every threshold", TierThresholds);
        r.Add("buff tier: a skill that can't be read doesn't collapse to tier 1", TierUnreadableSkills);
        r.Add("buff choice: the highest known tier, lower when a tier isn't known", KnownTierWalk);
        r.Add("buff choice: a cold spellbook uses the no-chat blacklist; a warm one overrides it", ColdAndWarm);
        r.Add("buff choice: every buff in the list resolves at every tier (checked against the spell data)", EveryBuffEveryTier);
        r.Add("buff choice: tier 7 lore names and tier 8 forms", LoreAndIncantations);
        r.Add("buff level: spell level from the name", SpellLevels);
        r.Add("buff timers: the duration the bot assumes matches the spell data", DurationsMatchData);
        r.Add("buff timers: Archmage's Endurance adds 20% a rank, at most 5", ArchmageEndurance);
        r.Add("buff school: which magic skill casts each buff", SkillForBuff);
        r.Add("buff school: which buffs live on an item", ItemEnchantments);
        r.Add("rebuff: recast only under the rebuff threshold", RebuffThreshold);
        r.Add("rebuff: permanent, expired, and two enchantments in one family", RebuffPermanentAndDuplicates);
        r.Add("rebuff: a higher tier is only chased above the tier that has landed", RebuffTierUpgrade);
        r.Add("rebuff: a chat-confirmed item spell counts as on", ItemSpellFromChat);
        r.Add("rebuff: our own lower tier under an expiring higher one counts as on", RebuffUnderExpiringHigherTier);
        r.Add("rebuff: cast over item-granted buffs (CastBuffsOverItemBuffs on)", RebuffOverItemBuffs);
        r.Add("rebuff: CastBuffsOverItemBuffs defaults on, loads, saves, bridges", OverItemBuffsSetting);
        r.Add("buff toggles: by default every prot and bane is cast", TogglesDefaultAllOn);
        r.Add("buff toggles: an unticked prot or bane is never cast and never holds NeedsAnyBuff", UntickedSkipped);
        r.Add("buff toggles: an old profile loads all on; the list saves, copies and bridges", TogglesSetting);

        r.Add("buff order: Creature Enchantment, Focus, Willpower, Life Magic come first", PriorityOrder);
        r.Add("buff order: a run casts them first, and a lone expiring buff is still just recast", PriorityCastOrderAndLoneExpiry);
        r.Add("force buff: recasts the whole filtered suite in order; without it nothing is cast", ForceBuffSuite);
        r.Add("force buff: /ra buff force and the button reach the same request", ForceBuffEntryPoints);
        r.Add("buff filter: a mastery for an untrained skill is never cast, trained is; the setting only controls the report", SkillFilter);
        r.Add("buff filter: weapon auras follow the registered melee/missile weapon or wand", WeaponFilter);
        r.Add("buff filter: an unlisted pack wand counts as a caster when WieldUnlistedWandWhenNoneListed is on", UnlistedWandCounts);
        r.Add("buff filter: an unclassifiable listed item, no cache, ammo, wielded weapons", KitEdgeCases);
        r.Add("buff filter: the skip line groups by reason, is capped, and repeats only when it changes", SkipLineGroupedAndCapped);
        r.Add("buff filter: the weapon kit is cached for a second, and rebuilt when its inputs change", KitCached);
        r.Add("buff filter: skipped buffs are named, once per run", FilterVisibility);
        r.Add("buff filter: untrained skills alone never print a chat line (log, page and /ra buff skipped keep them)", UntrainedSkillIsQuiet);
        r.Add("buff filter: SkipUnusableBuffs defaults on, loads, saves, bridges, is in the settings map", SkipUnusableSetting);

        r.Add("buff choice: spell names the tables miss (Bludgeon Bane, Nuhmudira's, T'ing, Celdiseth's)", NameGapsResolve);   // was a known failure; fixed by 940cb21
        r.Add("buff choice: a mastery whose name has several spell ids", MultiIdMastery);   // was a known failure; fixed by 0aedcee
        r.Add("buff timers: tier-8 weapon auras last 90 minutes", TierEightAuras);   // was a known failure; fixed by 9a317a8
        r.Add("buff chat: another player's speech can't record a buff timer", SpeechIsNotACast);   // was a known failure; fixed by 0e384d2
        r.Add("buff school: Armor Tinkering Expertise is a Creature Enchantment spell", ArmorTinkeringSchool);   // was a known failure
        r.Add("mana floor: under Get Mana At with nothing to refill mana, buffs go on while a heal's mana is kept (R-20261029-103248-ce48)", ManaFloorNoRefill);
        r.Add("buff upgrade: in a force run, the priority buffs go first, then the families cast before the skill rose are recast a level higher", UpgradeInRun);
        r.Add("buff upgrade: the next pass recasts every buff a level higher once the skill allows it; equal level or a lower skill casts nothing", UpgradeNextPass);
        r.Add("buff upgrade: a higher level learned from a scroll is cast at once (the spellbook is read again)", UpgradeAfterScroll);
        r.Add("buff upgrade: an in-run upgrade that fizzles is cast again; one that always fizzles keeps being cast", UpgradeFizzleRetried);
        r.Add("buff upgrade: an upgrade failing on a next pass is cast again every time, with no rest, until it lands", UpgradeFizzleNextPass);
        r.Add("buff upgrade: an Incantation landing a level low is not logged or counted as an upgrade when refreshed", IncantationRefreshNotUpgrade);
        r.Add("buff upgrade: 'too busy' refusals of an upgrade: it is sent again and lands", UpgradeBusyNotATry);
        r.Add("buff upgrade: a late line for the lower level does not confirm the pending upgrade", UpgradeLateLowerLine);
        r.Add("buff upgrade: the upgrade line names the level the upgrade is counted at (an Incantation's capped level)", UpgradeLineEffectiveLevel);
        r.Add("buff upgrade: through the heartbeat, an always-fizzling upgrade is sent once per answered beat, never unanswered", UpgradeCapViaHeartbeat);
        r.Add("buff upgrade: a client-refused upgrade is retried once per SpellCastIntervalMs, not every heartbeat", UpgradeClientRefusalPaced);
        r.Add("buff upgrade: an upgrade the registry never shows (still the lower level) is a no-show, not a landing", UpgradeNoShowIsATry);
        r.Add("buff upgrade: a late line for the level below a pending Incantation leaves it pending", UpgradeIncantationLateLine);
        r.Add("buff upgrade: a late line the cast tracker credits to an earlier send does not confirm the pending cast", UpgradeLateLineFromEarlierSend);
        r.Add("buff upgrade: two upgrade no-shows do not park the family; its lower level running low is recast", UpgradeNoShowsDoNotPark);
        r.Add("buff upgrade: two item spell upgrades with no chat do not park the family; its lower level running low is recast", UpgradeArmorNoChatDoesNotPark);
        r.Add("buff upgrade: an upgrade an item spell never lands is parked, not looped; the lower level is still refreshed and the park ends", UpgradeArmorNoChatParksUpgrade);
        r.Add("buff upgrade: three silent self-buff no-shows park the upgrade only; the lower level is still refreshed", UpgradeNoShowsParkUpgrade);
        r.Add("buff upgrade: an in-run upgrade the server never answers is parked and the force run ends", UpgradeNoShowInRunEnds);
        r.Add("buff upgrade: a confirmed upgrade resets the no-show count", UpgradeConfirmedResetsNoShows);
        r.Add("buff upgrade: Cancel Buffing ends the pending upgrade", UpgradeCancelEndsPending);
        r.Add("buff upgrade: an upgrade that would leave mana under the reserve waits for Stamina to Mana, then goes", UpgradeWaitsForManaReserve);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private static int[] Ids(string name) => SpellTable.Named(name).Select(r => r.Id).ToArray();
    private static int Id(string name) => Ids(name).FirstOrDefault();
    private static string NameOf(int id) => SpellTable.ById(id)?.Name ?? (id == 0 ? "(none)" : $"#{id}");

    /// <summary>The tier the spell data gives a spell: its numeral, else 7 for power 300, 8 for 350+.</summary>
    private static int DataTier(SpellTable.Row row)
    {
        string last = row.Name.Split(' ').Last();
        int roman = last switch { "I" => 1, "II" => 2, "III" => 3, "IV" => 4, "V" => 5, "VI" => 6, "VII" => 7, "VIII" => 8, _ => 0 };
        if (roman > 0) return roman;
        if (row.Power >= 350 || row.Name.StartsWith("Incantation", StringComparison.Ordinal)) return 8;
        return row.Power == 300 ? 7 : 0;
    }

    /// <summary>Buff thresholds that make a 250 skill (the no-skills stub) cast exactly <paramref name="tier"/>.</summary>
    private static void ForceBuffTier(LegacyUiSettings s, int tier)
    {
        int T(int k) => k <= tier ? 0 : 10000;
        s.BuffMinSkillLevelTier2 = T(2); s.BuffMinSkillLevelTier3 = T(3); s.BuffMinSkillLevelTier4 = T(4);
        s.BuffMinSkillLevelTier5 = T(5); s.BuffMinSkillLevelTier6 = T(6); s.BuffMinSkillLevelTier7 = T(7);
        s.BuffMinSkillLevelTier8 = T(8);
    }

    private static SpellManager NewSpells(RynthCoreHost host, LegacyUiSettings s, CharacterSkills? skills = null)
    {
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        if (skills != null) sm.SetCharacterSkills(skills);
        return sm;
    }

    private static CharacterSkills Skills(RynthCoreHost host)
    {
        var cs = new CharacterSkills(host);
        cs.SetPlayerId(Player);
        return cs;
    }

    private static void KnowAll()
    {
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
    }

    private static void Know(params string[] names)
    {
        foreach (string n in names) foreach (int id in Ids(n)) FakeHost.Spellbook.Add((uint)id);
    }

    private sealed class BuffRig
    {
        public readonly LegacyUiSettings S = new() { EnableBuffing = true };
        public readonly BuffManager B;
        public readonly SpellManager Sm;
        public readonly RynthCoreHost Host;
        /// <summary>The vitals BuffManager reads (all zero unless a test sets them).</summary>
        public readonly PlayerVitalsCache V = new();

        /// <summary>Call FakeHost.Reset(), set skills and the spellbook first.</summary>
        public BuffRig()
        {
            var host = FakeHost.Create();
            Host = host;
            var skills = Skills(host);
            Sm = NewSpells(host, S, skills);
            B = new BuffManager(host, S, Sm, V);
            B.SetCharacterSkills(skills);
            FakeHost.ServerTime = 100_000;
        }

        /// <summary>Sets the live enchantment registry and reads it (remaining seconds; MaxValue = permanent).</summary>
        public void Live(params (string Name, double Remaining)[] enchantments)
        {
            FakeHost.Enchantments.Clear();
            foreach (var (name, remaining) in enchantments)
                FakeHost.Enchantments.Add(((uint)Id(name), remaining == double.MaxValue ? double.MaxValue : FakeHost.ServerTime + remaining));
            B.RefreshFromLiveMemory();
        }
    }

    private static int BuffTier(int buffed, Action<LegacyUiSettings>? tweak = null, bool combat = false)
    {
        FakeHost.Reset();
        FakeHost.Skills[(Player, STypeCreature)] = (buffed, 2);
        var host = FakeHost.Create();
        var s = new LegacyUiSettings();
        tweak?.Invoke(s);
        var sm = NewSpells(host, s, Skills(host));
        return combat ? sm.GetHighestSpellTier(AcSkillType.CreatureEnchantment) : sm.GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment);
    }

    // ── Tiers ─────────────────────────────────────────────────────────────────

    private static void TierThresholds()
    {
        // Default thresholds: 85 / 135 / 185 / 235 / 285 / 335 / 435 for tiers 2-8.
        var cases = new[] { (10, 1), (84, 1), (85, 2), (134, 2), (135, 3), (184, 3), (185, 4), (234, 4), (235, 5),
                            (284, 5), (285, 6), (334, 6), (335, 7), (434, 7), (435, 8), (600, 8) };
        foreach (var (buffed, tier) in cases)
            Check.Eq(BuffTier(buffed), tier, $"buffed {buffed}");

        Check.Eq(BuffTier(450, s => s.MinSkillLevelTier8 = 500, combat: true), 7, "combat uses its own thresholds (tier 8 at 500)");
        Check.Eq(BuffTier(450, s => s.MinSkillLevelTier8 = 500), 8, "... while buffing keeps its own (435)");
        Check.Eq(BuffTier(300, s => s.BuffMinSkillLevelTier6 = 400), 5, "raising the tier-6 threshold drops a 300 to tier 5");
        // Pinned: the tier-1 threshold is never read; anything below tier 2 is tier 1.
        Check.Eq(BuffTier(10, s => s.BuffMinSkillLevelTier1 = 400), 1, "the tier-1 threshold is not read (pinned)");
    }

    private static void TierUnreadableSkills()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var s = new LegacyUiSettings();
        Check.Eq(NewSpells(host, s).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 5, "no skills object: assumes 250 (tier 5)");
        Check.Eq(NewSpells(host, s, Skills(host)).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 5,
            "the skill read fails: assumes 250 (tier 5), not tier 1");

        FakeHost.Skills[(Player, STypeCreature)] = (0, 2);
        Check.Eq(NewSpells(host, s, Skills(host)).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 5,
            "a trained skill reading 0 is an engine glitch: assumes 250");

        var skills = Skills(host);
        var sm = NewSpells(host, s, skills);
        FakeHost.Skills[(Player, STypeCreature)] = (400, 2);
        Check.Eq(sm.GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 7, "a real read of 400: tier 7");
        FakeHost.Skills[(Player, STypeCreature)] = (0, 2);
        Check.Eq(sm.GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 7, "then a 0 read keeps the last good one");

        FakeHost.Skills[(Player, STypeCreature)] = (0, 1);
        Check.Eq(NewSpells(host, s, Skills(host)).GetHighestBuffSpellTier(AcSkillType.CreatureEnchantment), 1,
            "an untrained skill reading 0 is real: tier 1");
    }

    private static int Resolve(string buff, int buffedCreature, params string[] known)
    {
        FakeHost.Reset();
        Know(known);
        FakeHost.Skills[(Player, STypeCreature)] = (buffedCreature, 2);
        var host = FakeHost.Create();
        return NewSpells(host, new LegacyUiSettings(), Skills(host)).GetDynamicSelfBuffId(buff, AcSkillType.CreatureEnchantment);
    }

    private static void KnownTierWalk()
    {
        string[] all = { "Strength Self I", "Strength Self II", "Strength Self III", "Strength Self IV", "Strength Self V",
                         "Strength Self VI", "Might of the Lugians", "Incantation of Strength Self" };
        Check.Eq(NameOf(Resolve("Strength Self", 500, all)), "Incantation of Strength Self", "skill 500, all known: tier 8");
        Check.Eq(NameOf(Resolve("Strength Self", 500, all[..7])), "Might of the Lugians", "tier 8 not known: tier 7");
        Check.Eq(NameOf(Resolve("Strength Self", 500, all[..5])), "Strength Self V", "only I-V known: V");
        Check.Eq(NameOf(Resolve("Strength Self", 250, all)), "Strength Self V", "skill 250: tier 5 even with more known");
        Check.Eq(NameOf(Resolve("Strength Self", 500, "Strength Self I", "Strength Self III")), "Strength Self III", "gaps are skipped");
        Check.Eq(Resolve("Strength Self", 500, "Focus Self VI"), 0, "no Strength spell known: 0 (the buff is skipped)");
        Check.Eq(NameOf(Resolve("Strength", 500, all)), "Incantation of Strength Self", "the base name works without ' Self'");
    }

    private static void ColdAndWarm()
    {
        FakeHost.Reset();   // empty spellbook: the snapshot is cold
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);
        var host = FakeHost.Create();
        var sm = NewSpells(host, new LegacyUiSettings(), Skills(host));
        Check.False(sm.IsKnownSnapshotWarm, "cold");
        Check.Eq(NameOf(sm.GetDynamicSelfBuffId("Strength Self", AcSkillType.CreatureEnchantment)), "Incantation of Strength Self",
            "cold, no player id: every spell in the data counts as known");
        sm.MarkSpellUnresolvable(Id("Incantation of Strength Self"));
        Check.Eq(NameOf(sm.GetDynamicSelfBuffId("Strength Self", AcSkillType.CreatureEnchantment)), "Might of the Lugians",
            "a spell that got no chat back is skipped while cold");
        Check.False(sm.TryResolveKnownSpellId("Strength Self VI", out _), "combat never guesses while cold");

        FakeHost.Reset();
        Know("Incantation of Strength Self", "Strength Self VI");
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);
        host = FakeHost.Create();
        var warm = NewSpells(host, new LegacyUiSettings(), Skills(host));
        warm.MarkSpellUnresolvable(Id("Incantation of Strength Self"));
        Check.Eq(NameOf(warm.GetDynamicSelfBuffId("Strength Self", AcSkillType.CreatureEnchantment)), "Incantation of Strength Self",
            "warm: the spellbook overrides the blacklist");
        Check.True(warm.IsKnownSnapshotWarm, "warm");
        Check.Eq(warm.UnresolvableSpellIds.Count, 0, "the known spell is purged from the blacklist");
        Check.True(warm.TryResolveKnownSpellId("Strength Self VI", out int vi) && vi == Id("Strength Self VI"), "combat resolves a known spell");
        Check.False(warm.TryResolveKnownSpellId("Strength Self V", out _), "and refuses an unknown one");
    }

    /// <summary>Resolves every buff in the list at tiers 1-8, with the whole spell data known.</summary>
    private static List<(string Buff, int Tier, int Id)> ResolveEverything(out BuffManager bm)
    {
        FakeHost.Reset();
        KnowAll();
        var host = FakeHost.Create();
        var byTier = new SpellManager[9];
        for (int t = 1; t <= 8; t++)
        {
            var s = new LegacyUiSettings();
            ForceBuffTier(s, t);
            byTier[t] = NewSpells(host, s);
        }
        // The whole list, whatever this character could use (the unusable-buff filter is its own tests).
        bm = new BuffManager(host, new LegacyUiSettings { SkipUnusableBuffs = false }, byTier[8], new PlayerVitalsCache());
        var result = new List<(string, int, int)>();
        foreach (string buff in bm.BuildDynamicBuffList())
        {
            var school = BuffManager.SkillForBuff(buff);
            for (int t = 1; t <= 8; t++)
                result.Add((buff, t, byTier[t].GetDynamicSelfBuffId(buff, school)));
        }
        return result;
    }

    private static void EveryBuffEveryTier()
    {
        var all = ResolveEverything(out var bm);
        Check.True(bm.BuildDynamicBuffList().Count >= 60, $"the full buff list ({bm.BuildDynamicBuffList().Count} buffs)");
        foreach (var (buff, tier, id) in all)
        {
            var row = SpellTable.ById(id);
            Check.True(row != null && DataTier(row) == tier, $"{buff} at tier {tier}: got {NameOf(id)}");
        }
    }

    private static void NameGapsResolve()
    {
        var all = ResolveEverything(out _);
        foreach (var (buff, tier, id) in all)
        {
            if (!NameGaps.Contains((buff, tier))) continue;
            var row = SpellTable.ById(id);
            Check.True(row != null && DataTier(row) == tier, $"{buff} at tier {tier}: got {NameOf(id)}");
        }
    }

    private static void LoreAndIncantations()
    {
        var all = ResolveEverything(out _);
        string At(string buff, int tier) => NameOf(all.First(x => x.Buff == buff && x.Tier == tier).Id);

        Check.Eq(At("Strength Self", 7), "Might of the Lugians", "Strength tier 7");
        Check.Eq(At("Endurance Self", 7), "Perseverance", "Endurance tier 7 (the misspelled 'Preservance' is tried first and isn't in the data)");
        Check.Eq(At("Invulnerability Self", 7), "Aura of Defense", "Invulnerability tier 7");
        Check.Eq(At("Armor Self", 7), "Executor's Blessing", "Armor tier 7");
        Check.Eq(At("Blood Drinker Self", 6), "Aura of Blood Drinker Self VI", "weapon aura tier 6");
        Check.Eq(At("Blood Drinker Self", 7), "Aura of Infected Caress", "weapon aura tier 7");
        Check.Eq(At("Blood Drinker Self", 8), "Aura of Incantation of Blood Drinker Self", "weapon aura tier 8");
        Check.Eq(At("Spirit Drinker Self", 7), "Aura of Infected Spirit Caress", "Spirit Drinker tier 7");
        Check.Eq(At("Impenetrability", 6), "Impenetrability VI", "armor spell tier 6 (no 'Self')");
        Check.Eq(At("Impenetrability", 7), "Brogard's Defiance", "armor spell tier 7");
        Check.Eq(At("Impenetrability", 8), "Incantation of Impenetrability", "armor spell tier 8");
        Check.Eq(At("Blade Bane", 7), "Swordsman's Bane", "Blade Bane tier 7");
        Check.Eq(At("Heavy Weapon Mastery", 7), "Heavy Weapon Mastery Self VII", "masteries with a real VII use it");
        Check.Eq(At("Arcanum Salvaging", 7), "Arcanum Salvaging VII", "'X VII' without Self");
    }

    private static int Level(string name) => BuffManager.GetSpellLevel(new SpellInfo(1, name));

    private static void SpellLevels()
    {
        Check.Eq(Level("Strength Self I"), 1, "I");
        Check.Eq(Level("Strength Self II"), 2, "II");
        Check.Eq(Level("Strength Self III"), 3, "III");
        Check.Eq(Level("Strength Self IV"), 4, "IV");
        Check.Eq(Level("Strength Self V"), 5, "V");
        Check.Eq(Level("Strength Self VI"), 6, "VI");
        Check.Eq(Level("Heavy Weapon Mastery Self VII"), 7, "VII");
        Check.Eq(Level("Strength Self VIII"), 8, "VIII (not read as VII)");
        Check.Eq(Level("Incantation of Strength Self"), 8, "Incantation");
        Check.Eq(Level("Incantation of Impenetrability"), 8, "Incantation, item spell");
        foreach (string lore in new[] { "Might of the Lugians", "Brogard's Defiance", "Tusker's Bane", "Aura of Infected Caress",
                                        "Adja's Blessing", "Robustify", "Unflinching Persistence", "Aura of Cragstone's Will" })
            Check.Eq(Level(lore), 7, lore);
        Check.Eq(Level("Frobnicate"), 1, "an unknown name reads as 1");
    }

    private static void DurationsMatchData()
    {
        var all = ResolveEverything(out var bm);
        foreach (var (buff, tier, id) in all)
        {
            var row = SpellTable.ById(id);
            if (row == null) continue;
            double assumed = bm.GetCustomSpellDuration(BuffManager.GetSpellLevel(new SpellInfo(id, row.Name)));
            Check.Near(assumed, row.Duration, 0.5, $"{row.Name}: timer length");
        }
    }

    private static void TierEightAuras()
    {
        foreach (string aura in Auras)
        {
            string name = "Aura of Incantation of " + aura;
            Check.Eq(Level(name), 8, $"{name} is level 8");
        }

        // A chat-confirmed tier-8 aura, with the rebuff threshold at 62 minutes: a 90-minute
        // timer leaves it on; a 60-minute one asks for a recast at once.
        FakeHost.Reset();
        Know("Aura of Incantation of Blood Drinker Self");
        FakeHost.Skills[(Player, STypeItem)] = (500, 2);
        var rig = new BuffRig();
        rig.S.RebuffSecondsRemaining = 3720;
        rig.B.OnChatWindowText("You cast Aura of Incantation of Blood Drinker Self on Rune Sword, refreshing Aura of Infected Caress", 7);
        Check.False(rig.B.NeedsAnyBuff(), "just cast: 90 minutes left, not under 62");
    }

    private static void ArchmageEndurance()
    {
        foreach (var (augs, factor) in new[] { (0, 1.0), (1, 1.2), (2, 1.4), (5, 2.0), (7, 2.0), (-3, 1.0) })
        {
            FakeHost.Reset();
            FakeHost.Ints[(Player, 238u)] = augs;   // AugmentationIncreasedSpellDuration
            var host = FakeHost.Create(playerId: Player);
            var s = new LegacyUiSettings();
            var bm = new BuffManager(host, s, NewSpells(host, s), new PlayerVitalsCache());
            Check.Near(bm.GetCustomSpellDuration(6), 2700 * factor, 1e-6, $"{augs} rank(s): tier 6 lasts {2700 * factor} s");
            Check.Near(bm.GetCustomSpellDuration(8), 5400 * factor, 1e-6, $"{augs} rank(s): tier 8 lasts {5400 * factor} s");
        }
        FakeHost.Reset();
        var noId = FakeHost.Create();
        var s0 = new LegacyUiSettings();
        var unread = new BuffManager(noId, s0, NewSpells(noId, s0), new PlayerVitalsCache());
        Check.Near(unread.GetCustomSpellDuration(1), 1800, 1e-6, "ranks unreadable: no bonus");
        Check.Near(unread.GetCustomSpellDuration(7), 3600, 1e-6, "tier 7: an hour");
    }

    private static void SkillForBuff()
    {
        var creature = new[] { "Strength Self", "Focus Self", "Invulnerability Self", "Impregnability Self", "Magic Resistance Self",
                               "Heavy Weapon Mastery", "Missile Weapon Mastery", "Life Magic Mastery", "War Magic Mastery",
                               "Sprint", "Fealty", "Arcanum Salvaging", "Item Tinkering Expertise" };
        var life = new[] { "Regeneration Self", "Rejuvenation Self", "Mana Renewal Self", "Armor Self", "Acid Protection Self",
                           "Blade Protection Self", "Bludgeoning Protection Self", "Heal Self", "Revitalize Self" };
        var item = new[] { "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self", "Spirit Drinker Self",
                           "Swift Killer Self", "Defender Self", "Impenetrability", "Acid Bane", "Bludgeoning Bane", "Piercing Bane" };
        foreach (string b in creature) Check.Eq(BuffManager.SkillForBuff(b), AcSkillType.CreatureEnchantment, b);
        foreach (string b in life) Check.Eq(BuffManager.SkillForBuff(b), AcSkillType.LifeMagic, b);
        foreach (string b in item) Check.Eq(BuffManager.SkillForBuff(b), AcSkillType.ItemEnchantment, b);
    }

    private static void ArmorTinkeringSchool()
    {
        foreach (string n in new[] { "Armor Tinkering Expertise", "Item Tinkering Expertise", "Weapon Tinkering Expertise",
                                     "Magic Item Tinkering Expertise", "Prodigal Armor Expertise", "Incantation of Armor Tinkering Expertise" })
            Check.Eq(BuffManager.SkillForBuff(n), AcSkillType.CreatureEnchantment, n);
        // The "Armor" keyword still files real Life Magic armor buffs under Life.
        foreach (string n in new[] { "Armor", "Incantation of Armor", "Prodigal Armor", "Blade Protection", "Regeneration" })
            Check.Eq(BuffManager.SkillForBuff(n), AcSkillType.LifeMagic, n);
    }

    private static void ItemEnchantments()
    {
        foreach (string n in new[] { "Impenetrability VI", "Brogard's Defiance", "Incantation of Acid Bane", "Tusker's Bane",
                                     "Aura of Blood Drinker Self VI", "Aura of Infected Caress", "Aura of Cragstone's Will",
                                     "Aura of Incantation of Defender Self", "Swordman's Bane" })
            Check.True(BuffManager.IsItemEnchantment(n), $"{n}: on an item");
        foreach (string n in new[] { "Strength Self VI", "Impregnability Self VI", "Aura of Defense", "Aura of Deflection",
                                     "Armor Self VI", "Blade Protection Self VI", "Heal Self VI", "Might of the Lugians" })
            Check.False(BuffManager.IsItemEnchantment(n), $"{n}: on the player");
    }

    // ── Rebuff timing ─────────────────────────────────────────────────────────

    private static BuffRig StrengthRig(int buffed, params string[] known)
    {
        FakeHost.Reset();
        Know(known);
        FakeHost.Skills[(Player, STypeCreature)] = (buffed, 2);
        return new BuffRig();
    }

    private static void RebuffThreshold()
    {
        var rig = StrengthRig(300, "Strength Self VI");   // 300 buffed: tier 6
        rig.Live();
        Check.True(rig.B.NeedsAnyBuff(), "not on: needs a buff");
        rig.Live(("Strength Self VI", 1000));
        Check.False(rig.B.NeedsAnyBuff(), "1000 s left, threshold 300: fine");
        rig.Live(("Strength Self VI", 310));
        Check.False(rig.B.NeedsAnyBuff(), "310 s left: fine");
        rig.Live(("Strength Self VI", 290));
        Check.True(rig.B.NeedsAnyBuff(), "290 s left: recast");
        rig.S.RebuffSecondsRemaining = 1200;
        rig.Live(("Strength Self VI", 1000));
        Check.True(rig.B.NeedsAnyBuff(), "threshold raised to 1200: 1000 s left is under it");
        rig.S.EnableBuffing = false;
        Check.False(rig.B.NeedsAnyBuff(), "buffing off: never");
        rig.S.EnableBuffing = true;
        rig.S.RebuffSecondsRemaining = 300;
        rig.B.ForceFullRebuff();
        Check.True(rig.B.NeedsAnyBuff(), "Force Rebuff: everything is due");
    }

    // CastBuffsOverItemBuffs off: the behaviour before 2026-10-05 (an item-granted permanent
    // entry at the tier we'd cast satisfies the family). The setting's own cases are below.
    private static void RebuffPermanentAndDuplicates()
    {
        var rig = StrengthRig(300, "Strength Self VI");
        rig.S.CastBuffsOverItemBuffs = false;
        rig.Live(("Strength Self VI", double.MaxValue));
        Check.False(rig.B.NeedsAnyBuff(), "a permanent enchantment counts as on");
        rig.Live(("Strength Self VI", -5));
        Check.True(rig.B.NeedsAnyBuff(), "an expired entry is dropped: recast");

        var t7 = StrengthRig(340, "Strength Self VI", "Might of the Lugians");   // 340 buffed: tier 7
        t7.S.CastBuffsOverItemBuffs = false;
        t7.Live(("Strength Self VI", double.MaxValue), ("Might of the Lugians", 3000));
        Check.False(t7.B.NeedsAnyBuff(), "a permanent VI and a timed VII: the VII owns the family");
        t7.Live(("Might of the Lugians", 3000), ("Strength Self VI", double.MaxValue));
        Check.False(t7.B.NeedsAnyBuff(), "same in the other order");

        var item = StrengthRig(340, "Strength Self VI", "Might of the Lugians");
        item.S.CastBuffsOverItemBuffs = false;
        item.Live(("Strength Self VI", double.MaxValue));
        Check.True(item.B.NeedsAnyBuff(), "only a permanent (item-granted) VI while VII is castable: cast the VII");
    }

    private static void RebuffTierUpgrade()
    {
        var rig = StrengthRig(300, "Strength Self V", "Strength Self VI");
        rig.Live(("Strength Self V", 3000));
        // Pinned: the landed tier caps the target, so a V from before a skill gain is kept
        // until it runs low (this is the guard against Incantations that land a tier low).
        Check.False(rig.B.NeedsAnyBuff(), "only V has ever landed: V with 3000 s left is kept (pinned)");
        rig.Live(("Strength Self VI", 3000));
        rig.Live(("Strength Self V", 3000));
        Check.True(rig.B.NeedsAnyBuff(), "VI has landed before: a V on now is upgraded");

        var inc = StrengthRig(500, "Incantation of Strength Self");
        inc.Live(("Strength Self VI", 3000));
        Check.False(inc.B.NeedsAnyBuff(), "an Incantation that lands as VI isn't recast for ever");
    }

    // A buff bot's level 8 that runs out while our own lower tier is already under it
    // (Drakkon, 2026-10-05): the 8 owns the family timer, so the family read "due"
    // right after our VI landed and every pass recast it until the batch audit parked it.
    private static void RebuffUnderExpiringHigherTier()
    {
        var rig = StrengthRig(300, "Strength Self VI");   // 300 buffed: tier 6
        rig.Live(("Incantation of Strength Self", 3000));
        Check.False(rig.B.NeedsAnyBuff(), "a bot's 8 with 3000 s left: fine");
        rig.Live(("Incantation of Strength Self", 200));
        Check.True(rig.B.NeedsAnyBuff(), "the 8 is running out and nothing is under it: cast our VI");
        rig.Live(("Incantation of Strength Self", 200), ("Strength Self VI", 1800));
        Check.False(rig.B.NeedsAnyBuff(), "our VI is under the expiring 8: covered, no recast");
        rig.Live(("Strength Self VI", 1800), ("Incantation of Strength Self", 200));
        Check.False(rig.B.NeedsAnyBuff(), "same in the other order");
        rig.Live(("Incantation of Strength Self", 200), ("Strength Self VI", 250));
        Check.True(rig.B.NeedsAnyBuff(), "both under the threshold: recast");

        var low = StrengthRig(300, "Strength Self V", "Strength Self VI");
        low.Live(("Incantation of Strength Self", 200), ("Strength Self V", 1800));
        Check.True(low.B.NeedsAnyBuff(), "only a V under it while VI is castable: still cast the VI");
        low.S.CastBuffsOverItemBuffs = false;
        low.Live(("Incantation of Strength Self", 200), ("Strength Self VI", double.MaxValue));
        Check.False(low.B.NeedsAnyBuff(), "setting off: a permanent VI under it covers it");
        low.S.CastBuffsOverItemBuffs = true;
        low.Live(("Incantation of Strength Self", 200), ("Strength Self VI", double.MaxValue));
        Check.True(low.B.NeedsAnyBuff(), "setting on: a permanent (item) VI under it doesn't cover it: cast ours");
    }

    // "Cast buffs even when an item already gives that buff" (CastBuffsOverItemBuffs, default
    // on, 2026-10-05). On servers with buff augments a player's own cast is stronger or longer
    // than the item's, so a permanent item-granted entry must not satisfy the family; the timer
    // follows our own timed entry, and once that lands it reads on until it runs low.
    private static void RebuffOverItemBuffs()
    {
        var rig = StrengthRig(300, "Strength Self VI");   // tier 6
        Check.True(rig.S.CastBuffsOverItemBuffs, "on by default");
        rig.Live(("Strength Self VI", double.MaxValue));
        Check.True(rig.B.NeedsAnyBuff(), "only an item's permanent VI: cast our own");
        rig.Live(("Strength Self VI", double.MaxValue), ("Strength Self VI", 3000));
        Check.False(rig.B.NeedsAnyBuff(), "our VI landed (3000 s) over the item's VI: on, no recast loop");
        rig.Live(("Strength Self VI", 3000), ("Strength Self VI", double.MaxValue));
        Check.False(rig.B.NeedsAnyBuff(), "same in the other order");
        rig.Live(("Strength Self VI", double.MaxValue), ("Strength Self VI", 200));
        Check.True(rig.B.NeedsAnyBuff(), "ours running low (200 s) with the item's VI still on: recast");
        rig.Live(("Strength Self VI", 200), ("Strength Self VI", double.MaxValue));
        Check.True(rig.B.NeedsAnyBuff(), "same in the other order");
        rig.Live(("Strength Self VI", double.MaxValue), ("Strength Self VI", 2700));
        Check.False(rig.B.NeedsAnyBuff(), "recast landed: on again");

        // A higher item tier than we can cast: ours is still cast and tracked.
        var high = StrengthRig(300, "Strength Self VI");
        high.Live(("Might of the Lugians", double.MaxValue));
        Check.True(high.B.NeedsAnyBuff(), "an item's permanent VII, we cast VI: cast it");
        high.Live(("Might of the Lugians", double.MaxValue), ("Strength Self VI", 3000));
        Check.False(high.B.NeedsAnyBuff(), "our VI under the item's VII: on until it runs low");
        high.Live(("Might of the Lugians", double.MaxValue), ("Strength Self VI", 100));
        Check.True(high.B.NeedsAnyBuff(), "our VI running low: recast");

        // The tier-upgrade rule still works on our timed entries.
        var up = StrengthRig(340, "Strength Self VI", "Might of the Lugians");   // tier 7
        up.Live(("Might of the Lugians", 3000));
        up.Live(("Strength Self VI", double.MaxValue), ("Strength Self V", 3000));
        Check.True(up.B.NeedsAnyBuff(), "VII has landed before: our V is upgraded, the item's VI doesn't stop it");
        up.Live(("Strength Self VI", double.MaxValue), ("Might of the Lugians", 3000));
        Check.False(up.B.NeedsAnyBuff(), "our VII on: fine");

        // Switched on between live reads: a permanent that owns the timer no longer counts.
        var toggled = StrengthRig(300, "Strength Self VI");
        toggled.S.CastBuffsOverItemBuffs = false;
        toggled.Live(("Strength Self VI", double.MaxValue));
        Check.False(toggled.B.NeedsAnyBuff(), "off: the item's VI counts");
        toggled.S.CastBuffsOverItemBuffs = true;
        Check.True(toggled.B.NeedsAnyBuff(), "switched on before the next read: cast ours");
        toggled.S.CastBuffsOverItemBuffs = false;
        toggled.Live(("Strength Self VI", double.MaxValue));
        Check.False(toggled.B.NeedsAnyBuff(), "switched back off: the item's VI counts again");
    }

    private static void OverItemBuffsSetting()
    {
        Check.True(new LegacyUiSettings().CastBuffsOverItemBuffs, "a new profile: on");
        var old = JsonSerializer.Deserialize("{\"EnableBuffing\":true,\"RebuffSecondsRemaining\":300}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.CastBuffsOverItemBuffs, "a profile saved before the setting: on");
        var off = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { CastBuffsOverItemBuffs = false }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(off != null && !off.CastBuffsOverItemBuffs, "off is saved and loaded");

        // Profile switch/load copies it (CopySettings is the hand-written field list).
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { CastBuffsOverItemBuffs = false }, dst });
        Check.False(dst.CastBuffsOverItemBuffs, "a profile with it off loads off");
        copy.Invoke(null, new object[] { old!, dst });
        Check.True(dst.CastBuffsOverItemBuffs, "an old profile loads on");

        // Engine Settings face: the payload carries it; an engine without the field leaves it alone.
        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.CastBuffsOverItemBuffs == null, "an older engine's payload has no value");
        var s = new LegacyUiSettings { CastBuffsOverItemBuffs = false };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));   // BuildSettingsJson reads its crafting state
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.CastBuffsOverItemBuffs == false, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.False(s.CastBuffsOverItemBuffs, "a save without the field leaves it off");
        dash.ApplySettingsJson("{\"CastBuffsOverItemBuffs\":true}");
        Check.True(s.CastBuffsOverItemBuffs, "a save with it on turns it on");
    }

    // ── Prot and bane toggles (Buffing settings, DisabledBuffs) ───────────────

    private static readonly MethodInfo CastSelfBuffs =
        typeof(BuffManager).GetMethod("CheckAndCastSelfBuffs", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Tier 6 Life and Item Magic, in Magic stance, knowing two prots and two banes.</summary>
    private static BuffRig ProtBaneRig()
    {
        FakeHost.Reset();
        Know("Fire Protection Self VI", "Cold Protection Self VI", "Flame Bane VI", "Frost Bane VI");
        FakeHost.Skills[(Player, STypeLife)] = (300, 2);
        FakeHost.Skills[(Player, STypeItem)] = (300, 2);
        FakeHost.WeaponCalls = true;   // GetCurrentCombatMode answers CombatModeValue
        FakeHost.CombatModeValue = CombatMode.Magic;
        return new BuffRig();
    }

    /// <summary>Runs the buff cast loop until it casts nothing, confirming each cast by chat as the
    /// server would. Returns the spells cast in order; <paramref name="done"/> = the loop's last
    /// answer was "nothing to do" (what ends a batch in the heartbeat).</summary>
    private static List<string> CastAll(BuffRig rig, out bool done)
    {
        var cast = new List<string>();
        done = false;
        for (int i = 0; i < 20; i++)
        {
            int before = FakeHost.Casts.Count;
            bool acted = (bool)CastSelfBuffs.Invoke(rig.B, null)!;
            if (FakeHost.Casts.Count == before) { done = !acted; break; }
            string name = NameOf(FakeHost.Casts.Last().SpellId);
            cast.Add(name);
            rig.B.OnChatWindowText($"You cast {name} on {(BuffManager.IsItemEnchantment(name) ? "Rune Robe" : "yourself")}", 7);
        }
        return cast;
    }

    private static void TogglesDefaultAllOn()
    {
        var rig = ProtBaneRig();
        Check.Eq(rig.S.DisabledBuffs.Count, 0, "a new profile unticks nothing");
        foreach (var (buff, _) in BuffManager.ToggleProtections.Concat(BuffManager.ToggleBanes))
            Check.True(rig.B.BuildDynamicBuffList().Contains(buff), $"every toggle names a buff in the list: {buff}");
        rig.Live(("Strength Self I", 3000));   // a registry read, with none of ours on
        Check.True(rig.B.NeedsAnyBuff(), "nothing on: needs buffs");
        var cast = CastAll(rig, out bool done);
        Check.Eq(string.Join(", ", cast), "Fire Protection Self VI, Cold Protection Self VI, Flame Bane VI, Frost Bane VI", $"all four are cast (last skip: {rig.B.LastBuffSkipReason})");
        Check.True(done, "then the loop has nothing left");
    }

    private static void UntickedSkipped()
    {
        var rig = ProtBaneRig();
        rig.S.SetBuffEnabled("Fire Protection Self", false);
        rig.S.SetBuffEnabled("flame bane", false);   // names match case-insensitively
        Check.False(rig.B.BuildDynamicBuffList().Contains("Fire Protection Self"), "an unticked prot leaves the list");
        Check.False(rig.B.BuildDynamicBuffList().Contains("Flame Bane"), "an unticked bane leaves the list");
        rig.Live(("Strength Self I", 3000));
        var cast = CastAll(rig, out bool done);
        Check.Eq(string.Join(", ", cast), "Cold Protection Self VI, Frost Bane VI", "only the ticked ones are cast");
        Check.True(done, "the loop ends with the unticked ones still off: the batch can finish");

        // A fresh look, the ticked ones on and the unticked ones never cast: nothing is due.
        var idle = ProtBaneRig();
        idle.S.SetBuffEnabled("Fire Protection Self", false);
        idle.S.SetBuffEnabled("Flame Bane", false);
        idle.Live(("Cold Protection Self VI", 3000));
        idle.B.OnChatWindowText("You cast Frost Bane VI on Rune Robe", 7);
        Check.False(idle.B.NeedsAnyBuff(), "unticked and off doesn't hold Buffing");
        idle.S.SetBuffEnabled("Fire Protection Self", true);
        Check.True(idle.B.NeedsAnyBuff(), "ticked again: Fire Protection is due");
        Check.False(idle.S.IsBuffDisabled("Fire Protection Self"), "and leaves the list");

        // Force Rebuff recasts everything, still not the unticked ones.
        var fr = ProtBaneRig();
        fr.S.SetBuffEnabled("Cold Protection Self", false);
        fr.S.SetBuffEnabled("Frost Bane", false);
        fr.Live(("Strength Self I", 3000));
        fr.B.ForceFullRebuff();
        cast = CastAll(fr, out done);
        Check.Eq(string.Join(", ", cast), "Fire Protection Self VI, Flame Bane VI", "Force Rebuff skips the unticked ones too");
        Check.True(done, "and finishes");
        Check.False(FakeHost.Casts.Any(c => NameOf(c.SpellId) is "Cold Protection Self VI" or "Frost Bane VI"), "never cast");
    }

    private static void TogglesSetting()
    {
        var old = JsonSerializer.Deserialize("{\"EnableBuffing\":true,\"RebuffSecondsRemaining\":300}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.DisabledBuffs.Count == 0, "a profile saved before the setting: all on");
        var nul = JsonSerializer.Deserialize("{\"DisabledBuffs\":null}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(nul != null && nul.DisabledBuffs.Count == 0, "a null list: all on");
        var some = new LegacyUiSettings();
        some.SetBuffEnabled("Acid Bane", false);
        some.SetBuffEnabled("Blade Protection Self", false);
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(some, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(back != null && back.IsBuffDisabled("Acid Bane") && back.IsBuffDisabled("Blade Protection Self") && back.DisabledBuffs.Count == 2,
            "unticked ones are saved and loaded");

        // Profile switch/load copies it (CopySettings is the hand-written field list).
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { some, dst });
        Check.True(dst.IsBuffDisabled("Acid Bane") && dst.DisabledBuffs.Count == 2, "a profile with unticked ones loads them");
        copy.Invoke(null, new object[] { old!, dst });
        Check.Eq(dst.DisabledBuffs.Count, 0, "an old profile loads all on");

        // Engine Settings face: the payload carries the list; an engine without the field leaves it alone.
        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.DisabledBuffs == null, "an older engine's payload has no list");
        var s = new LegacyUiSettings();
        s.SetBuffEnabled("Frost Bane", false);
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent?.DisabledBuffs != null && sent.DisabledBuffs.SequenceEqual(new[] { "Frost Bane" }), "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.IsBuffDisabled("Frost Bane"), "a save without the list leaves it alone");
        dash.ApplySettingsJson("{\"DisabledBuffs\":[]}");
        Check.Eq(s.DisabledBuffs.Count, 0, "a save with an empty list ticks everything");
    }

    private static BuffRig ArmorRig()
    {
        FakeHost.Reset();
        Know("Impenetrability VI");
        FakeHost.Skills[(Player, STypeItem)] = (300, 2);   // tier 6
        return new BuffRig();
    }

    private static void ItemSpellFromChat()
    {
        var rig = ArmorRig();
        Check.True(rig.B.NeedsAnyBuff(), "no armor spell yet");
        rig.B.OnChatWindowText("You cast Impenetrability VI on Rune Robe, refreshing Impenetrability V", 7);
        Check.False(rig.B.NeedsAnyBuff(), "confirmed by chat: on for 45 minutes");

        var stripped = ArmorRig();
        stripped.B.OnChatWindowText("ou cast Impenetrability VI on Rune Robe", 7);   // AC's glyph layer eats the Y
        Check.False(stripped.B.NeedsAnyBuff(), "the 'ou cast' form counts too");

        var fizzle = ArmorRig();
        fizzle.B.OnChatWindowText("Your spell fizzled.", 7);
        Check.True(fizzle.B.NeedsAnyBuff(), "a fizzle records nothing");

        var dead = ArmorRig();
        dead.B.OnChatWindowText("You cast Impenetrability VI on Rune Robe", 7);
        dead.Live(("Strength Self I", 600));   // some player enchantment ...
        dead.Live();                           // ... then the registry drops to zero: a death
        Check.True(dead.B.NeedsAnyBuff(), "after a death wipes the registry, item spells are recast too");
    }

    private static void SpeechIsNotACast()
    {
        var rig = ArmorRig();
        rig.B.OnChatWindowText("Vessa says, \"you cast Impenetrability VI on your robe?\"", 2);
        Check.True(rig.B.NeedsAnyBuff(), "someone else's speech is not our cast");
    }

    private static void MultiIdMastery()
    {
        FakeHost.Reset();
        foreach (int id in new[] { 491, 492, 493, 494, 495, 496 }) FakeHost.Spellbook.Add((uint)id);   // the second line
        FakeHost.Skills[(Player, STypeCreature)] = (300, 2);
        var host = FakeHost.Create();
        var sm = NewSpells(host, new LegacyUiSettings(), Skills(host));
        Check.Eq(SpellTable.ById(496)?.Name, "Missile Weapon Mastery Self VI", "496 is a Missile Weapon Mastery Self VI");
        Check.Eq(sm.GetDynamicSelfBuffId("Missile Weapon Mastery", AcSkillType.CreatureEnchantment), 496, "resolves to the id the character knows");
    }

    // -- Priority order, Force Buff and the unusable-buff filter ----

    private const uint SkillHeavy = 44, SkillMissile = 47, SkillMagicDefense = 15;
    private const uint Sword = 0x80000401, Bow = 0x80000406, Wand = 0x80000408, Shield = 0x80000404;

    /// <summary>Tier 6 Creature/Life/Item Magic, in Magic stance, knowing the head of the list, a
    /// couple of ordinary buffs and all six weapon auras. <paramref name="listed"/> are the items on
    /// the Items list (id, name, ItemType: 0x1 melee, 0x100 missile, 0x8000 caster, 0x2 shield).</summary>
    private static BuffRig SuiteRig(params (uint Id, string Name, uint ItemType)[] listed)
    {
        FakeHost.Reset();
        Know("Creature Enchantment Mastery Self VI", "Focus Self VI", "Willpower Self VI", "Life Magic Mastery Self VI",
             "Strength Self VI", "Heavy Weapon Mastery Self VI", "Armor Self VI",
             "Aura of Blood Drinker Self VI", "Aura of Hermetic Link Self VI", "Aura of Heart Seeker Self VI",
             "Aura of Spirit Drinker Self VI", "Aura of Swift Killer Self VI", "Aura of Defender Self VI");
        FakeHost.Skills[(Player, STypeCreature)] = (300, 2);
        FakeHost.Skills[(Player, STypeLife)] = (300, 2);
        FakeHost.Skills[(Player, STypeItem)] = (300, 2);
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var (id, name, type) in listed)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = type;
            FakeHost.Ints[(id, 9)] = type == 0x2 ? 0x00200000 : type == 0x8000 ? 0x01000000 : type == 0x100 ? 0x00400000 : 0x00100000;
        }
        var rig = new BuffRig();
        rig.S.ItemRules = listed.Select(l => new ItemRule
        {
            Id = unchecked((int)l.Id), Name = l.Name, Element = "", ElementSource = "",
            Action = l.ItemType == 0x2 ? WeaponList.ShieldAction : "Weapon",
        }).ToList();
        rig.B.SetWorldObjectCache(FakeHost.MakeCache(rig.Host, Player, listed.Select(l => l.Id)));   // even with nothing listed: the empty-pack path runs
        return rig;
    }

    private static readonly (uint, string, uint) MeleeItem = (Sword, "Long Sword", 0x1);
    private static readonly (uint, string, uint) BowItem = (Bow, "Longbow", 0x100);
    private static readonly (uint, string, uint) WandItem = (Wand, "Wand", 0x8000);
    private static readonly (uint, string, uint) ShieldItem = (Shield, "Kite Shield", 0x2);

    private static readonly string[] Head =
        { "Creature Enchantment Mastery", "Focus Self", "Willpower Self", "Life Magic Mastery" };

    /// <summary>Every buff the rig knows, on with plenty of time left (item auras by chat).</summary>
    private static void AllOn(BuffRig rig, params string[] except)
    {
        var live = new[] { "Creature Enchantment Mastery Self VI", "Focus Self VI", "Willpower Self VI", "Life Magic Mastery Self VI",
                           "Strength Self VI", "Heavy Weapon Mastery Self VI", "Armor Self VI" }
            .Where(n => !except.Contains(n)).Select(n => (n, 3000.0)).ToArray();
        rig.Live(live);
        foreach (string aura in new[] { "Blood Drinker", "Hermetic Link", "Heart Seeker", "Spirit Drinker", "Swift Killer", "Defender" })
            rig.B.OnChatWindowText($"You cast Aura of {aura} Self VI on Rune Sword", 7);
    }

    // -- Level upgrades: when a higher level of a buff becomes castable (Creature Enchantment Mastery,
    // Focus and Willpower raise the skill), the buff is recast at it -----------------------------

    /// <summary>Every spell known; Creature, Life and Item Magic at <paramref name="buffed"/>; Magic stance; no items listed.</summary>
    private static BuffRig UpgradeRig(int buffed)
    {
        FakeHost.Reset();
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeCreature)] = (buffed, 2);
        FakeHost.Skills[(Player, STypeLife)] = (buffed, 2);
        FakeHost.Skills[(Player, STypeItem)] = (buffed, 2);
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        var rig = new BuffRig();
        rig.B.SetWorldObjectCache(FakeHost.MakeCache(rig.Host, Player, Array.Empty<uint>()));
        return rig;
    }

    private static int LevelOf(int id) => SpellTableStub.GetById(id) is { } s ? BuffManager.GetSpellLevel(s) : 0;
    private static int FamilyOf(int id) => SpellTableStub.GetById(id)?.Family ?? 0;
    private static int FamilyOfBuff(string anyTierName) => FamilyOf(Id(anyTierName));

    /// <summary>Runs the buff step to the end (confirming each cast by chat), calling
    /// <paramref name="afterCast"/> after each cast with the casts so far.</summary>
    private static List<int> RunBuffs(BuffRig rig, Action<List<int>>? afterCast = null, int max = 200)
    {
        var ids = new List<int>();
        for (int i = 0; i < max; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(rig.B, null);
            if (FakeHost.Casts.Count == before) break;
            int id = FakeHost.Casts.Last().SpellId;
            ids.Add(id);
            string name = NameOf(id);
            rig.B.OnChatWindowText($"You cast {name} on {(BuffManager.IsItemEnchantment(name) ? "Rune Robe" : "yourself")}", 7);
            afterCast?.Invoke(ids);
        }
        return ids;
    }

    private static string Show(IEnumerable<int> ids) => string.Join(", ", ids.Select(NameOf));

    /// <summary>The run's end as OnHeartbeat does it when CheckAndCastSelfBuffs has nothing left
    /// (flag off first, the audit, then the family set cleared).</summary>
    private static void EndRun(BuffRig rig)
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        if (!(bool)typeof(BuffManager).GetField("_isForceRebuffing", F)!.GetValue(rig.B)!) return;
        typeof(BuffManager).GetField("_isForceRebuffing", F)!.SetValue(rig.B, false);
        typeof(BuffManager).GetField("_isAutoBatchRebuff", F)!.SetValue(rig.B, false);
        typeof(BuffManager).GetMethod("AuditBatchSatisfied", F)!.Invoke(rig.B, null);
        typeof(BuffManager).GetMethod("ClearRunFamilies", F)!.Invoke(rig.B, null);
    }

    /// <summary>A fake BuffManager clock (the cast tracker's send and give-up times).</summary>
    private static Action<double> FakeClock(BuffRig rig)
    {
        DateTime t0 = new DateTime(2026, 10, 10, 12, 0, 0), now = t0;
        rig.B.Clock = () => now;
        return s => now = t0.AddSeconds(s);
    }

    /// <summary>CEM sends of a cast list, as levels ("6,7,7").</summary>
    private static string CemLevels(IEnumerable<int> sent) =>
        string.Join(",", sent.Where(id => FamilyOf(id) == FamilyOfBuff("Creature Enchantment Mastery Self VI")).Select(LevelOf));

    private static void UpgradeInRun()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI"), focus = FamilyOfBuff("Focus Self VI"), will = FamilyOfBuff("Willpower Self VI");
        var rig = UpgradeRig(300);                       // tier 6
        rig.Live(("Strength Self I", 3000));             // a registry read, nothing of ours on
        rig.B.ForceFullRebuff();
        // Creature Enchantment Mastery, Focus and Willpower land and lift Creature Magic to tier 7.
        var cast = RunBuffs(rig, ids => { if (ids.Count == 3) FakeHost.Skills[(Player, STypeCreature)] = (340, 2); });
        Check.True(cast.Count > 7, "the run casts the suite: " + Show(cast));
        Check.Eq(string.Join(",", cast.Take(3).Select(FamilyOf)), $"{cem},{focus},{will}", "the priority buffs first: " + Show(cast.Take(3)));
        Check.True(cast.Take(3).All(id => LevelOf(id) == 6), "at the level the skill allowed then (6)");
        Check.Eq(string.Join(",", cast.Skip(3).Take(3).Select(FamilyOf)), $"{cem},{focus},{will}", "then the same three again: " + Show(cast.Skip(3).Take(3)));
        Check.True(cast.Skip(3).Take(3).All(id => LevelOf(id) == 7), "a level higher (7)");
        Check.Eq(rig.B.BuffUpgrades, 3, "three upgrades");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] upgrade:")), 3, "one line each");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Buff] upgrade: Creature Enchantment Mastery level 6 -> 7")), "the line names the levels");
        // After the head: every cast is at its school's level now (Creature 7; Life and Item still 6).
        var under = cast.Skip(6).Where(id => LevelOf(id) < (BuffManager.SkillForBuff(NameOf(id)) == AcSkillType.CreatureEnchantment ? 7 : 6)).ToList();
        Check.Eq(under.Count, 0, "nothing after the head under its school's level: " + Show(under));
        Check.Eq(cast.Distinct().Count(), cast.Count, "nothing is cast twice at the same level (no churn)");

        // The skill falls back (a buff ran out) during the run: nothing is cast a level lower.
        var down = UpgradeRig(300);
        down.Live(("Strength Self I", 3000));
        down.B.ForceFullRebuff();
        var dcast = RunBuffs(down, ids => { if (ids.Count == 3) FakeHost.Skills[(Player, STypeCreature)] = (250, 2); });
        Check.False(dcast.Skip(3).Any(id => FamilyOf(id) == cem || FamilyOf(id) == focus || FamilyOf(id) == will), "no downgrade of the head: " + Show(dcast));
        Check.Eq(down.B.BuffUpgrades, 0, "no upgrades");
    }

    private static void UpgradeNextPass()
    {
        var rig = UpgradeRig(300);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var first = RunBuffs(rig);
        EndRun(rig);
        Check.True(first.Count > 7 && first.All(id => LevelOf(id) <= 6), "the run at tier 6: " + Show(first));
        Check.Eq(RunBuffs(rig).Count, 0, "the same skill: nothing more (equal level is no upgrade)");
        EndRun(rig);

        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);   // Creature Magic now allows 7
        var next = RunBuffs(rig);
        EndRun(rig);
        Check.True(next.Count > 0, "the next pass recasts");
        Check.True(next.All(id => LevelOf(id) == 7), "every recast is a level higher: " + Show(next));
        Check.True(next.Select(FamilyOf).Contains(FamilyOfBuff("Creature Enchantment Mastery Self VI")) && next.Select(FamilyOf).Contains(FamilyOfBuff("Focus Self VI"))
                   && next.Select(FamilyOf).Contains(FamilyOfBuff("Willpower Self VI")), "including Creature Enchantment Mastery, Focus and Willpower");
        Check.Eq(rig.B.BuffUpgrades, next.Count, "each one an upgrade");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] upgrade:")), next.Count, "one line each");
        Check.Eq(RunBuffs(rig).Count, 0, "then nothing (no churn)");
        EndRun(rig);

        // One buff runs low: recast at the same level, which is not an upgrade.
        var onNow = first.Concat(next).GroupBy(FamilyOf).Select(g => g.Last()).Where(id => !BuffManager.IsItemEnchantment(NameOf(id))).ToList();
        int low = next.First(id => FamilyOf(id) == FamilyOfBuff("Focus Self VI"));
        rig.Live(onNow.Select(id => (NameOf(id), id == low ? 200.0 : 3000.0)).ToArray());
        int upgradesBefore = rig.B.BuffUpgrades;
        var refresh = RunBuffs(rig);
        Check.True(refresh.Contains(low), "the low one is recast: " + Show(refresh) + " (last skip: " + rig.B.LastBuffSkipReason + ")");
        Check.Eq(rig.B.BuffUpgrades, upgradesBefore, "at the same level: not an upgrade, no line");
        EndRun(rig);

        FakeHost.Skills[(Player, STypeCreature)] = (250, 2);   // the skill drops (tier 5)
        Check.Eq(RunBuffs(rig).Count, 0, "a lower skill: nothing is recast lower");
    }

    // The upgrade path sits after the mana reserve hold (BuffWaitsForMana): a higher level that
    // would leave mana under the reserve is not sent, counted or logged as an upgrade; Stamina to
    // Mana goes instead, and the upgrade goes once mana is back.
    private static void UpgradeWaitsForManaReserve()
    {
        var rig = UpgradeRig(300);                      // tier 6
        BuffManager.ManaCostOverride = id => LevelOf(id) * 10;   // level x 10 (FakeHost.Reset sets 0)
        rig.S.GetManaAt = 20; rig.S.TopOffMana = 20;
        rig.V.MaxHealth = 400; rig.V.CurrentHealth = 400;
        rig.V.MaxStamina = 383; rig.V.CurrentStamina = 289;
        rig.V.MaxMana = 383; rig.V.CurrentMana = 300;
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var first = RunBuffs(rig);
        EndRun(rig);
        Check.True(first.Count > 7 && first.All(id => LevelOf(id) <= 6), "300/383: the run at tier 6: " + Show(first));

        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);   // Creature Magic now allows 7
        int reserve = rig.B.ManaReserve();
        Check.True(reserve > 0, $"a reserve ({reserve})");
        rig.V.CurrentMana = (uint)(reserve + 30);       // a level VII (70) would leave it under the reserve
        int upgradesBefore = rig.B.BuffUpgrades, castsBefore = FakeHost.Casts.Count;
        CastSelfBuffs.Invoke(rig.B, null);
        Check.Eq(FakeHost.Casts.Count, castsBefore + 1, "one cast");
        int held = FakeHost.Casts.Last().SpellId;
        Check.Eq(FamilyOf(held), FamilyOfBuff("Stamina to Mana Self I"), $"it is Stamina to Mana, not the upgrade ({NameOf(held)})");
        Check.Eq(rig.B.BuffUpgrades, upgradesBefore, "the held upgrade is not counted");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] upgrade:")), 0, "and not logged");

        rig.B.OnChatWindowText($"You cast {NameOf(held)} on yourself", 7);
        rig.V.CurrentMana = 300;
        var next = RunBuffs(rig);
        EndRun(rig);
        Check.True(next.Count > 0 && next.All(id => LevelOf(id) == 7), "mana back: the upgrades go at 7: " + Show(next));
        Check.Eq(rig.B.BuffUpgrades, upgradesBefore + next.Count, "each one an upgrade");
    }

    private static void UpgradeAfterScroll()
    {
        FakeHost.Reset();
        int cemFam = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        int seven = SpellTable.Rows.Select(r => r.Id).First(id => FamilyOf(id) == cemFam && LevelOf(id) == 7 && NameOf(id).IndexOf("Other", StringComparison.Ordinal) < 0);
        Know("Creature Enchantment Mastery Self VI");
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);   // tier 7, but only VI is known
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        var rig = new BuffRig();
        rig.B.SetWorldObjectCache(FakeHost.MakeCache(rig.Host, Player, Array.Empty<uint>()));
        rig.Live(("Strength Self I", 3000));
        var first = RunBuffs(rig);
        Check.Eq(Show(first), "Creature Enchantment Mastery Self VI", "only VI is known: VI");
        EndRun(rig);
        FakeHost.Spellbook.Add((uint)seven);                 // the scroll's spell is now in the spellbook
        Check.Eq(RunBuffs(rig).Count, 0, "before the learn line: the spellbook is not read again yet (3 s throttle)");
        rig.B.OnChatWindowText("The scroll is destroyed.", 0);
        var after = RunBuffs(rig);
        Check.Eq(Show(after), NameOf(seven), "the learned level is cast at once");
        Check.Eq(rig.B.BuffUpgrades, 1, "an upgrade");
    }

    /// <summary>A force run where the cast numbered (1-based) in <paramref name="fizzle"/> fizzles;
    /// Creature Magic rises to tier 7 after the third cast. Returns every cast sent and the confirmed ones.</summary>
    private static (List<int> Sent, List<int> Confirmed) RunWithFizzles(BuffRig rig, Func<int, int, bool> fizzle, int max = 300)
    {
        var sent = new List<int>(); var confirmed = new List<int>();
        for (int i = 0; i < max; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(rig.B, null);
            if (FakeHost.Casts.Count == before) break;
            int id = FakeHost.Casts.Last().SpellId;
            sent.Add(id);
            if (fizzle(sent.Count, id)) { rig.B.OnChatWindowText("Your spell fizzled.", 7); continue; }
            confirmed.Add(id);
            string name = NameOf(id);
            rig.B.OnChatWindowText($"You cast {name} on {(BuffManager.IsItemEnchantment(name) ? "Rune Robe" : "yourself")}", 7);
            if (sent.Count == 3) FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        }
        return (sent, confirmed);
    }

    // The upgrade's issue was taken as done, so a fizzled upgrade was never retried.
    private static void UpgradeFizzleRetried()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var rig = UpgradeRig(300);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var (sent, _) = RunWithFizzles(rig, (n, _) => n == 4);   // the first upgrade (CEM 7) fizzles
        var cemLevels = sent.Where(id => FamilyOf(id) == cem).Select(LevelOf).ToList();
        Check.Eq(string.Join(",", cemLevels), "6,7,7", "CEM 6, CEM 7 (fizzled), CEM 7 again in the same run: " + Show(sent.Take(8)));

        // CEM 7 always fizzles: it is cast again every time, with no limit and no rest, and
        // nothing else gets ahead of it (it heads the list).
        var all = UpgradeRig(300);
        all.Live(("Strength Self I", 3000));
        all.B.ForceFullRebuff();
        var (sent2, _) = RunWithFizzles(all, (n, id) => n > 3 && FamilyOf(id) == cem && LevelOf(id) == 7, 40);
        Check.Eq(sent2.Count, 40, "the run goes on");
        Check.Eq(sent2.Skip(3).Count(id => FamilyOf(id) == cem && LevelOf(id) == 7), 37, "every send after the first three is CEM 7 again: " + Show(sent2.Take(8)));
    }

    // A failed upgrade on a next pass is cast again at once each time, at any time (no rest), and
    // stops once it lands.
    private static void UpgradeFizzleNextPass()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var rig = UpgradeRig(300);
        var at = FakeClock(rig);
        at(0);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        RunBuffs(rig);
        EndRun(rig);
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);      // Creature Magic now allows 7
        bool FizzleCem7(int n, int id) => FamilyOf(id) == cem && LevelOf(id) == 7;
        var (sent, _) = RunWithFizzles(rig, FizzleCem7, 20);
        Check.Eq(sent.Count(id => FamilyOf(id) == cem && LevelOf(id) == 7), 20, "CEM 7 sent again after every fizzle: " + Show(sent.Take(6)));
        Check.False(FakeHost.Logs.Any(l => l.Contains("no upgrade of it for")), "no rest");
        at(30);
        var (later, _) = RunWithFizzles(rig, FizzleCem7, 5);
        Check.Eq(CemLevels(later), "7,7,7,7,7", "30 s later: still tried every time");
        at(31);
        var (landed, _) = RunWithFizzles(rig, (n, id) => false, 60);
        EndRun(rig);
        Check.Eq(landed.Count(id => FamilyOf(id) == cem), 1, "it lands, once");
        Check.Eq(RunWithFizzles(rig, (n, id) => false, 60).Sent.Count, 0, "then nothing (no churn)");
    }

    // "You're too busy!" for an upgrade is not a failed try.
    private static void UpgradeBusyNotATry()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var rig = UpgradeRig(300);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var backoff = typeof(BuffManager).GetField("_busyBackoffUntil", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int busy = 0;
        var sent = new List<int>();
        for (int i = 0; i < 200; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(rig.B, null);
            if (FakeHost.Casts.Count == before) break;
            int id = FakeHost.Casts.Last().SpellId;
            sent.Add(id);
            if (FamilyOf(id) == cem && LevelOf(id) == 7 && busy < 2)
            {
                busy++;
                rig.B.OnChatWindowText("You're too busy!", 0);
                backoff.SetValue(rig.B, DateTime.MinValue);      // the backoff runs on the real clock; not what is tested
                continue;
            }
            rig.B.OnChatWindowText($"You cast {NameOf(id)} on {(BuffManager.IsItemEnchantment(NameOf(id)) ? "Rune Robe" : "yourself")}", 7);
            if (sent.Count == 3) FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        }
        Check.Eq(busy, 2, "two refusals");
        Check.Eq(CemLevels(sent), "6,7,7,7", "CEM 6, two refused 7s, then 7 lands: " + Show(sent));
        Check.Eq(rig.B.BuffUpgrades, 3 + 2, "the upgrade line counts sends (CEM 3 times, Focus, Willpower)");
    }

    // A late "You cast ... VI" while VII is pending is the lower cast's line.
    private static void UpgradeLateLowerLine()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var rig = UpgradeRig(300);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var sent = new List<int>();
        bool late = false;
        for (int i = 0; i < 200; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(rig.B, null);
            if (FakeHost.Casts.Count == before) break;
            int id = FakeHost.Casts.Last().SpellId;
            sent.Add(id);
            if (FamilyOf(id) == cem && LevelOf(id) == 7 && !late)
            {
                late = true;
                rig.B.OnChatWindowText("You cast Creature Enchantment Mastery Self VI on yourself", 7);   // the earlier cast's line
                Check.Eq(rig.B.PendingSpellId, id, "the VI line leaves VII pending");
                rig.B.OnChatWindowText("Your spell fizzled.", 7);                                       // VII's own result
                continue;
            }
            rig.B.OnChatWindowText($"You cast {NameOf(id)} on {(BuffManager.IsItemEnchantment(NameOf(id)) ? "Rune Robe" : "yourself")}", 7);
            if (sent.Count == 3) FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        }
        Check.Eq(CemLevels(sent), "6,7,7", "CEM 6, CEM 7 (late VI line, then its fizzle), CEM 7 again: " + Show(sent));
    }

    // The line printed "6 -> 8" for an Incantation counted as an upgrade to its capped 7.
    private static void UpgradeLineEffectiveLevel()
    {
        FakeHost.Reset();
        Know("Incantation of Strength Self");
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);      // tier 8
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        var rig = new BuffRig();
        rig.B.SetWorldObjectCache(FakeHost.MakeCache(rig.Host, Player, Array.Empty<uint>()));
        rig.Live(("Armor Self I", 3000));
        RunBuffs(rig);
        EndRun(rig);
        rig.Live(("Might of the Lugians", 3000));                 // it landed as 7 (the cap)
        Check.Eq(RunBuffs(rig).Count, 0, "at its cap: not recast");
        EndRun(rig);
        rig.Live(("Strength Self VI", 3000));                     // ours is gone, someone's VI is on
        var up = RunBuffs(rig);
        EndRun(rig);
        Check.Eq(Show(up), "Incantation of Strength Self", "recast to reach the cap again");
        var line = FakeHost.Logs.LastOrDefault(l => l.Contains("[Buff] upgrade:")) ?? "";
        Check.True(line.Contains("level 6 -> 7 (Incantation of Strength Self)"), "the line says 6 -> 7, the level counted: " + line);
    }

    // A report (DreamWeave, 2026-10-29: "Buffing seems to be getting stuck"): logged in at
    // 209/207 mana, the first buffs took max mana to 536, the same 209 read 39% under Get Mana At
    // 40%, and with no Stamina to Mana known and no mana potion every buff after waited on regen.
    private static BuffRig FloorRig(bool knowStaminaToMana, uint mana, uint stamina = 383)
    {
        FakeHost.Reset();
        foreach (var row in SpellTable.Rows)
            if (knowStaminaToMana || !row.Name.StartsWith("Stamina to Mana", StringComparison.Ordinal))
                FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeCreature)] = (300, 2);
        FakeHost.Skills[(Player, STypeLife)] = (300, 2);
        FakeHost.Skills[(Player, STypeItem)] = (300, 2);
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        var rig = new BuffRig();
        rig.B.SetWorldObjectCache(FakeHost.MakeCache(rig.Host, Player, Array.Empty<uint>()));
        BuffManager.ManaCostOverride = id => LevelOf(id) * 10;
        rig.S.IsMacroRunning = true;
        rig.S.SpellCastIntervalMs = 0;
        rig.S.GetManaAt = 40; rig.S.TopOffMana = 40;
        rig.S.HealAt = 0; rig.S.TopOffHP = 0; rig.S.EmergencyHealAt = 0; rig.S.StaminaToHealthAt = 0;
        rig.S.RestamAt = 0; rig.S.TopOffStam = 0; rig.S.UsePotions = false;
        rig.V.MaxHealth = 400; rig.V.CurrentHealth = 400;
        rig.V.MaxStamina = 383; rig.V.CurrentStamina = stamina;
        rig.V.MaxMana = 536; rig.V.CurrentMana = mana;
        var at = FakeClock(rig);
        at(0);
        rig.B.MarkLiveBuffsReadyForTests();
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        return rig;
    }

    private static int FloorBeats(BuffRig rig, int n)
    {
        int before = FakeHost.Casts.Count;
        for (int i = 0; i < n; i++)
        {
            rig.B.OnHeartbeat();
            if (FakeHost.Casts.Count > before)
            {
                int id = FakeHost.Casts.Last().SpellId;
                rig.B.OnChatWindowText($"You cast {NameOf(id)} on {(BuffManager.IsItemEnchantment(NameOf(id)) ? "Rune Robe" : "yourself")}", 7);
                SetFld(rig.B, "_lastCastAttempt", DateTime.Now.AddSeconds(-10));
                SetFld(rig.B, "_lastSelfBuffPollAt", DateTime.MinValue);
            }
        }
        return FakeHost.Casts.Count - before;
    }

    private static void ManaFloorNoRefill()
    {
        // The report: 209/536 (39%), Get Mana At 40%, no Stamina to Mana, no potion. Buffs go.
        var r = FloorRig(knowStaminaToMana: false, mana: 209);
        int keep = r.B.ManaFloorHealReserve();
        Check.True(keep > 0, $"a heal's mana is kept ({keep})");
        int sent = FloorBeats(r, 6);
        Check.True(sent > 0, $"buffs are cast under the floor (sent {sent}, last skip: {r.B.LastBuffSkipReason})");
        Check.True(FakeHost.Casts.All(c => !NameOf(c.SpellId).StartsWith("Stamina to Mana", StringComparison.Ordinal)), "no Stamina to Mana (none is known)");
        Check.True(FakeHost.Logs.Count(l => l.Contains("nothing can refill it") && l.Contains("buffing goes on")) == 1, "logged once");

        // Down near the heal reserve: buffing waits, and the line names every source's reason.
        var low = FloorRig(knowStaminaToMana: false, mana: (uint)(keep + 10));
        Check.Eq(FloorBeats(low, 6), 0, "under a heal's mana plus a buff: buffing waits");
        string line = FakeHost.Logs.LastOrDefault(l => l.Contains("buffing waits: mana")) ?? "";
        Check.True(line.Contains("Stamina to Mana: no known") && line.Contains("mana potion: Use Potions is off") && line.Contains("for healing"),
            "the line gives each reason: " + line);

        // Stamina to Mana known, stamina under its line: the floor still refills first, as before
        // (Revitalize for stamina, then Stamina to Mana); no buff goes while it can refill.
        var s2m = FloorRig(knowStaminaToMana: true, mana: 209, stamina: 20);
        FloorBeats(s2m, 6);
        var sentNames = FakeHost.Casts.Select(c => NameOf(c.SpellId)).ToList();
        Check.True(sentNames.Count > 0 && sentNames.All(n => n.StartsWith("Revitalize", StringComparison.Ordinal) || n.StartsWith("Stamina to Mana", StringComparison.Ordinal)
                                                         || n.Contains("Revitalize") || n.Contains("Stamina to Mana")),
            "Stamina to Mana known: only refills go, no buff: " + string.Join(", ", sentNames));
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("buffing goes on")), 0, "the no-refill path isn't taken");
    }

    private static T Fld<T>(BuffManager b, string name) => (T)typeof(BuffManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(b)!;
    private static void SetFld(BuffManager b, string name, object v) => typeof(BuffManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(b, v);

    private static int AttemptedOf(BuffRig rig, int family) => Fld<Dictionary<int, int>>(rig.B, "_familyAttemptedTier").TryGetValue(family, out int n) ? n : 0;

    /// <summary>The macro's path: heartbeats on a fake clock, the buff step running itself.</summary>
    private sealed class BeatRig
    {
        public readonly BuffRig R;
        private readonly Action<double> _at;
        private double _t;
        public BeatRig(int buffed)
        {
            R = UpgradeRig(buffed);
            R.S.IsMacroRunning = true;
            R.S.SpellCastIntervalMs = 0;
            _at = FakeClock(R);
            _at(0);
            R.B.MarkLiveBuffsReadyForTests();
        }
        public void Tick(double s) { _t += s; _at(_t); }
        /// <summary>Up to <paramref name="n"/> beats of 3 s, confirming every cast by chat except
        /// the first one <paramref name="hold"/> picks, which is returned unanswered (0 = none).</summary>
        public int Beats(int n, Func<int, bool> hold)
        {
            for (int i = 0; i < n; i++)
            {
                Tick(3);
                int before = FakeHost.Casts.Count;
                R.B.OnHeartbeat();
                if (FakeHost.Casts.Count == before) continue;
                int id = FakeHost.Casts.Last().SpellId;
                if (hold(id)) return id;
                R.B.OnChatWindowText($"You cast {NameOf(id)} on {(BuffManager.IsItemEnchantment(NameOf(id)) ? "Rune Robe" : "yourself")}", 7);
            }
            return 0;
        }
        /// <summary>A force run at the rig's skill, to its end, through the heartbeat.</summary>
        public void FirstRun()
        {
            R.Live(("Strength Self I", 3000));
            R.B.ForceFullRebuff();
            Beats(150, _ => false);
            Check.False(R.B.GetStateSnapshot().IsForceRebuffing, "the first run ends");
        }
        /// <summary>The pending cast's settle and give-up are read on the real clock too: put the send in the past.</summary>
        public void SendLongAgo(double seconds)
        {
            SetFld(R.B, "_lastCastAttempt", DateTime.Now.AddSeconds(-seconds));
            SetFld(R.B, "_lastSelfBuffPollAt", DateTime.MinValue);
        }
    }

    /// <summary>A pass after a run at tier 6 with Creature Magic now at 7, stopped with CEM 7 sent and pending.</summary>
    private static (BuffRig Rig, int Seven) PendingCem7(Action<BuffRig>? setup = null)
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var rig = UpgradeRig(300);
        setup?.Invoke(rig);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var first = RunBuffs(rig);
        EndRun(rig);
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        rig.Live(first.GroupBy(FamilyOf).Select(g => g.Last()).Where(id => !BuffManager.IsItemEnchantment(NameOf(id))).Select(id => (NameOf(id), 3000.0)).ToArray());
        for (int i = 0; i < 100; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(rig.B, null);
            if (FakeHost.Casts.Count == before) break;
            int id = FakeHost.Casts.Last().SpellId;
            if (FamilyOf(id) == cem && LevelOf(id) == 7) return (rig, id);
            rig.B.OnChatWindowText($"You cast {NameOf(id)} on {(BuffManager.IsItemEnchantment(NameOf(id)) ? "Rune Robe" : "yourself")}", 7);
        }
        return (rig, 0);
    }

    // The registry still holding VI at the give-up was taken as VII landing.
    // registryAtGiveUp: what the registry holds when the poll judges the pending CEM 7.
    private static (BeatRig H, int Seven) NoShowCase((string, double)[] registryAtGiveUp)
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var h = new BeatRig(300);
        h.FirstRun();
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        h.R.Live(("Creature Enchantment Mastery Self VI", 3000), ("Focus Self VI", 3000), ("Willpower Self VI", 3000));
        int seven = h.Beats(60, id => FamilyOf(id) == cem && LevelOf(id) == 7);
        Check.True(seven != 0 && h.R.B.PendingSpellId == seven, "CEM 7 is sent over the live VI and waits");
        h.R.Live(registryAtGiveUp);
        h.SendLongAgo(3);                     // no chat
        h.Tick(3);
        h.R.B.OnHeartbeat();
        return (h, seven);
    }

    private static void UpgradeNoShowIsATry()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var others = new[] { ("Focus Self VI", 3000.0), ("Willpower Self VI", 3000.0) };

        var (h, seven) = NoShowCase(others.Prepend(("Creature Enchantment Mastery Self VI", 3000.0)).ToArray());
        Check.True(FakeHost.Logs.Any(l => l.Contains("not in the live registry") && l.Contains("no strike, tried again")), "VI unmoved: a no-show");
        Check.True(AttemptedOf(h.R, cem) < 7, $"VII is not taken as landed (attempted {AttemptedOf(h.R, cem)})");
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == seven), 2, "given up, and VII is sent again (the same beat)");

        // The entry moved: VII at the same expiry (a higher level) ...
        (h, seven) = NoShowCase(others.Prepend(("Adja's Blessing", 3000.0)).ToArray());
        Check.Eq(AttemptedOf(h.R, cem), 7, "VII in the registry: landed");
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == seven), 1, "not sent again");

        // ... or the same level with a later expiry (an Incantation landing low shows this way).
        (h, seven) = NoShowCase(others.Prepend(("Creature Enchantment Mastery Self VI", 3600.0)).ToArray());
        Check.Eq(AttemptedOf(h.R, cem), 7, "a later expiry: landed");
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == seven), 1, "not sent again");
    }
    // A pending Incantation took a late VII line as its own result.
    private static void UpgradeIncantationLateLine()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var rig = UpgradeRig(340);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var first = RunBuffs(rig);
        EndRun(rig);
        var on = first.GroupBy(FamilyOf).Select(g => g.Last()).Where(id => !BuffManager.IsItemEnchantment(NameOf(id))).ToList();
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);
        rig.Live(on.Select(id => (NameOf(id), 3000.0)).ToArray());
        int inc = 0;
        for (int i = 0; i < 100 && inc == 0; i++)
        {
            int before = FakeHost.Casts.Count;
            CastSelfBuffs.Invoke(rig.B, null);
            if (FakeHost.Casts.Count == before) break;
            int id = FakeHost.Casts.Last().SpellId;
            if (FamilyOf(id) == cem && LevelOf(id) == 8) { inc = id; break; }
            rig.B.OnChatWindowText($"You cast {NameOf(id)} on {(BuffManager.IsItemEnchantment(NameOf(id)) ? "Rune Robe" : "yourself")}", 7);
        }
        Check.True(inc != 0, "the CEM Incantation is sent over VII");
        rig.B.OnChatWindowText($"You cast {NameOf(on.First(id => FamilyOf(id) == cem))} on yourself", 7);   // the VII cast's late line
        Check.Eq(rig.B.PendingSpellId, inc, "the late VII line leaves the Incantation pending");
        Check.True(AttemptedOf(rig, cem) < 8, "and not taken as landed");
        rig.B.OnChatWindowText("Your spell fizzled.", 7);
        Check.Eq(rig.B.PendingSpellId, 0, "its own fizzle ends it");
        int sentBefore = FakeHost.Casts.Count;
        CastSelfBuffs.Invoke(rig.B, null);
        Check.True(FakeHost.Casts.Count > sentBefore && FakeHost.Casts.Last().SpellId == inc, "and it is cast again");
    }

    // An Incantation heard (open on the server) and given up; the
    // skill drops; VII goes out; then the Incantation's own line arrives. It names a HIGHER level than
    // the pending VII, so only the tracker (it credits the line to the open Incantation) says it is late.
    private static void UpgradeLateLineFromEarlierSend()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var h = new BeatRig(340);
        h.FirstRun();
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);
        h.R.Live(("Adja's Blessing", 200), ("Inner Calm", 3000), ("Mind Blossom", 3000));   // CEM VII running low
        int inc = h.Beats(60, id => FamilyOf(id) == cem);
        Check.True(inc != 0 && LevelOf(inc) == 8, "the CEM Incantation is sent: " + NameOf(inc));
        h.R.B.OnChatWindowText("You say, \"Puish Zharil\"", 17);          // its incantation: the server runs it
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);                // the skill drops
        for (int i = 0; i < 6 && h.R.B.PendingSpellId == inc; i++)
        {
            h.SendLongAgo(6);
            h.Tick(3);
            h.R.B.OnHeartbeat();                                            // no result: given up, VII goes out
        }
        int seven = h.R.B.PendingSpellId;
        Check.True(seven != 0 && FamilyOf(seven) == cem && LevelOf(seven) == 7, "given up; VII is pending: " + NameOf(seven));
        h.R.B.OnChatWindowText($"You cast {NameOf(inc)} on yourself", 7);  // the Incantation's late line
        Check.Eq(h.R.B.PendingSpellId, seven, "VII still waits for its own result");
    }

    /// <summary>Lets the pending <paramref name="id"/> reach its give-up unanswered twice (it is resent
    /// in between); returns the give-ups done. Every other cast is confirmed.</summary>
    private static int FailTwice(BeatRig h, int id) => FailTimes(h, id, 2);

    private static int FailTimes(BeatRig h, int id, int times)
    {
        int gaveUp = 0;
        for (int k = 0; k < 20 && gaveUp < times; k++)
        {
            if (h.R.B.PendingSpellId != id) { if (h.Beats(40, x => x == id) == 0) break; continue; }
            h.SendLongAgo(6);
            h.Tick(6);
            h.R.B.OnHeartbeat();
            if (h.R.B.PendingSpellId != id || FakeHost.Casts.Count(c => c.SpellId == id) > gaveUp + 1) gaveUp++;
        }
        // The last give-up's beat may have sent the next cast: answer it, as Beats does.
        int next = h.R.B.PendingSpellId;
        if (next != 0 && next != id)
            h.R.B.OnChatWindowText($"You cast {NameOf(next)} on {(BuffManager.IsItemEnchantment(NameOf(next)) ? "Rune Robe" : "yourself")}", 7);
        return gaveUp;
    }

    private static bool Parked(BuffRig rig, int family) => Fld<Dictionary<int, DateTime>>(rig.B, "_buffFailCooldownUntil").ContainsKey(family);

    // The no-show strikes parked the family 30 min after two upgrade no-shows, so the
    // lower level it was cast over was not recast when it ran low.
    private static void UpgradeNoShowsDoNotPark()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var h = new BeatRig(300);
        h.FirstRun();
        // The registry holds the first run's player buffs (so a batch is the few upgrades, not the suite).
        var six = FakeHost.Casts.Select(c => c.SpellId).Where(id => !BuffManager.IsItemEnchantment(NameOf(id))).Distinct().ToList();
        (string, double)[] Registry(double cemLeft) => six.Select(id => (NameOf(id), FamilyOf(id) == cem ? cemLeft : 3000.0)).ToArray();
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        h.R.Live(Registry(3000));
        int seven = h.Beats(60, id => FamilyOf(id) == cem && LevelOf(id) == 7);
        Check.True(seven != 0, "CEM 7 is sent over the live VI");
        Check.Eq(FailTwice(h, seven), 2, "two no-shows");
        Check.False(Parked(h.R, cem), "the family is not parked");
        h.R.Live(Registry(100));                                         // VI running low
        // No limit: CEM is still being cast (already resent, or on a later beat). A 30 min park (the
        // bug) runs on the real clock and would hold it throughout.
        Check.True(FamilyOf(h.R.B.PendingSpellId) == cem || h.Beats(150, id => FamilyOf(id) == cem) != 0, "CEM is still being cast");
        Check.False(Parked(h.R, cem), "never parked");    }

    // The item spell (armor) twin of the test above.
    private static void UpgradeArmorNoChatDoesNotPark()
    {
        var h = new BeatRig(300);
        h.FirstRun();
        FakeHost.Skills[(Player, STypeItem)] = (340, 2);
        int item7 = h.Beats(80, id => BuffManager.IsItemEnchantment(NameOf(id)) && LevelOf(id) == 7);
        Check.True(item7 != 0, "an item spell is upgraded to 7: " + NameOf(item7));
        int fam = FamilyOf(item7);
        Check.Eq(FailTwice(h, item7), 2, "two no-chat give-ups");
        Check.False(Parked(h.R, fam), "the family is not parked");
        // Its VI record runs low.
        object rec = ((System.Collections.IDictionary)Fld<object>(h.R.B, "_itemSpellTimers"))[fam]!;
        rec.GetType().GetField("ExpiresAt")!.SetValue(rec, DateTime.Now.AddSeconds(100));
        Check.True(h.Beats(40, id => FamilyOf(id) == fam) != 0, "running low: recast");
    }

    private static bool Has(BuffManager b, string field, int key) => ((System.Collections.IDictionary)Fld<object>(b, field)).Contains(key);

    /// <summary>The registry as the first run left it (every player buff of the suite at tier 6), CEM with <paramref name="cemLeft"/> s left.</summary>
    private static (string, double)[] SixRegistry(int cem, double cemLeft) =>
        FakeHost.Casts.Select(c => c.SpellId).Where(id => !BuffManager.IsItemEnchantment(NameOf(id))).Distinct()
            .Select(id => (NameOf(id), FamilyOf(id) == cem ? cemLeft : 3000.0)).ToArray();

    // Review finding (PR 15): an upgrade that is a SILENT no-show was never struck, parked or counted, so it
    // was recast at every give-up forever (12x/minute), and a force/batch run never ended. Now: after three in
    // a row the upgrade alone is parked; the family is not blacklisted and its lower level still refreshes.
    private static void UpgradeNoShowsParkUpgrade()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var h = new BeatRig(300);
        h.FirstRun();
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        h.R.Live(SixRegistry(cem, 3000));
        int seven = h.Beats(60, id => FamilyOf(id) == cem && LevelOf(id) == 7);
        Check.True(seven != 0, "CEM 7 is sent over the live VI");
        Check.Eq(FailTimes(h, seven, 3), 3, "three no-shows");
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == seven), 3, "sent three times, not a fourth: the upgrade is parked");
        Check.False(Parked(h.R, cem), "the family itself is not parked");

        // Nothing more goes out for it, and the run (the batch the upgrade started) ends.
        h.Beats(150, _ => false);
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == seven), 3, "still three after 150 more beats");
        Check.False(h.R.B.GetStateSnapshot().IsForceRebuffing, "the run ends");
        Check.Eq(h.R.B.PendingSpellId, 0, "nothing pending");

        // The level it was cast over runs low: a normal refresh goes out (not counted as an upgrade).
        int upgrades = h.R.B.BuffUpgrades;
        h.R.Live(SixRegistry(cem, 100));
        Check.True(h.Beats(150, id => FamilyOf(id) == cem) != 0, "the lower level running low is recast while the upgrade is parked");
        Check.Eq(h.R.B.BuffUpgrades, upgrades, "and that cast is not an upgrade");
        Check.False(Parked(h.R, cem), "the family is still not parked");
    }

    // The item spell twin (armor lost on death / weapon swapped, its lower record still unexpired).
    private static void UpgradeArmorNoChatParksUpgrade()
    {
        var h = new BeatRig(300);
        h.FirstRun();
        FakeHost.Skills[(Player, STypeItem)] = (340, 2);
        int item7 = h.Beats(80, id => BuffManager.IsItemEnchantment(NameOf(id)) && LevelOf(id) == 7);
        Check.True(item7 != 0, "an item spell is upgraded to 7: " + NameOf(item7));
        int fam = FamilyOf(item7);
        Check.Eq(FailTimes(h, item7, 3), 3, "three no-chat give-ups");
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == item7), 3, "sent three times, not a fourth");
        Check.False(Parked(h.R, fam), "the family itself is not parked");
        h.Beats(150, _ => false);
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == item7), 3, "still three after 150 more beats");
        Check.False(h.R.B.GetStateSnapshot().IsForceRebuffing, "the run ends");

        // Its VI record runs low: the family is recast (a normal refresh, not an upgrade).
        int upgrades = h.R.B.BuffUpgrades;
        object rec = ((System.Collections.IDictionary)Fld<object>(h.R.B, "_itemSpellTimers"))[fam]!;
        rec.GetType().GetField("ExpiresAt")!.SetValue(rec, DateTime.Now.AddSeconds(100));
        Check.True(h.Beats(60, id => FamilyOf(id) == fam) != 0, "running low: recast while the upgrade is parked");
        Check.Eq(h.R.B.BuffUpgrades, upgrades, "not an upgrade");
        Check.False(Parked(h.R, fam), "the family is not parked");

        // The park is time-boxed: once it ends the upgrade is tried again.
        h.R.B.CancelBuffing();
        rec.GetType().GetField("ExpiresAt")!.SetValue(rec, DateTime.Now.AddSeconds(3000));
        ((System.Collections.IDictionary)Fld<object>(h.R.B, "_upgradeParkedUntil"))[fam] = DateTime.Now.AddSeconds(-1);
        Check.True(h.Beats(80, id => id == item7) != 0, "after the park ends the upgrade is cast again");
    }

    // An explicit force run with an upgrade the server never answers: parked after three, the run ends.
    private static void UpgradeNoShowInRunEnds()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var h = new BeatRig(300);
        h.R.Live(("Strength Self I", 3000));
        h.R.B.ForceFullRebuff();
        int n = 0;
        int seven = h.Beats(150, id => { if (++n == 3) FakeHost.Skills[(Player, STypeCreature)] = (340, 2); return FamilyOf(id) == cem && LevelOf(id) == 7; });
        Check.True(seven != 0, "CEM 7 is sent in the run, over the VI it cast");
        Check.Eq(FailTimes(h, seven, 3), 3, "three no-shows");
        h.Beats(200, _ => false);
        // The registry here never shows the run's VIs, so once the upgrade is parked CEM is cast as an ordinary
        // buff with no timer, and the usual two-strike no-show park ends that: three upgrade sends + at most two.
        int sends = FakeHost.Casts.Count(c => c.SpellId == seven);
        Check.True(sends <= 3 + 2, $"bounded: {sends} sends of CEM 7");
        Check.False(h.R.B.GetStateSnapshot().IsForceRebuffing, "the force run ends (it kept buffing's priority for good)");
        Check.Eq(h.R.B.PendingSpellId, 0, "nothing pending");
    }

    // Confirmed upgrades are unchanged: they land, and they reset the count (the next upgrade starts from zero).
    private static void UpgradeConfirmedResetsNoShows()
    {
        var h = new BeatRig(300);
        h.FirstRun();
        FakeHost.Skills[(Player, STypeItem)] = (340, 2);
        int item7 = h.Beats(80, id => BuffManager.IsItemEnchantment(NameOf(id)) && LevelOf(id) == 7);
        Check.True(item7 != 0, "an item spell is upgraded to 7");
        int fam = FamilyOf(item7);
        Check.Eq(FailTimes(h, item7, 2), 2, "two no-chat give-ups");
        Check.Eq(h.R.B.PendingSpellId, item7, "sent again");
        Check.True(Has(h.R.B, "_upgradeNoShowCounts", fam), "two are counted");
        h.R.B.OnChatWindowText($"You cast {NameOf(item7)} on Rune Robe", 7);        // it lands
        Check.False(Has(h.R.B, "_upgradeNoShowCounts", fam), "the count is reset by the landing");
        Check.False(Has(h.R.B, "_upgradeParkedUntil", fam), "nothing parked");
        int sends = FakeHost.Casts.Count(c => c.SpellId == item7);
        h.Beats(60, _ => false);
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == item7), sends, "landed at 7: not cast again");

        // The record falls back to VI (armor swapped): the upgrade gets a full three tries again.
        object rec = ((System.Collections.IDictionary)Fld<object>(h.R.B, "_itemSpellTimers"))[fam]!;
        rec.GetType().GetField("SpellLevel")!.SetValue(rec, 6);
        Check.True(h.Beats(80, id => id == item7) != 0, "the upgrade is cast again");
        Check.Eq(FailTimes(h, item7, 2), 2, "two more no-shows");
        Check.Eq(h.R.B.PendingSpellId, item7, "not parked: sent again after the second");
        Check.False(Has(h.R.B, "_upgradeParkedUntil", fam), "the count started from zero");
    }

    // Cancelling the buff step left the pending upgrade behind.
    private static void UpgradeCancelEndsPending()
    {
        var (rig, seven) = PendingCem7();
        Check.True(seven != 0 && rig.B.PendingSpellId == seven, "CEM 7 is pending");
        Check.Eq(Fld<int>(rig.B, "_pendingUpgradeSpellId"), seven, "as an upgrade");
        rig.B.CancelBuffing();
        Check.Eq(rig.B.PendingSpellId, 0, "cancelled");
        Check.Eq(Fld<int>(rig.B, "_pendingUpgradeSpellId"), 0, "the upgrade is no longer pending");
    }

    // No limit and no rest, through OnHeartbeat: an always-fizzling CEM 7 is sent on
    // every beat (one per answer), the late VI line confirms nothing, and nothing goes out unanswered.
    private static void UpgradeCapViaHeartbeat()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var h = new BeatRig(300);
        h.FirstRun();
        var six = FakeHost.Casts.Select(c => c.SpellId).Where(id => !BuffManager.IsItemEnchantment(NameOf(id))).Distinct().ToList();
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        h.R.Live(six.Select(id => (NameOf(id), 3000.0)).ToArray());
        int seven = h.Beats(60, id => FamilyOf(id) == cem && LevelOf(id) == 7);
        Check.True(seven != 0, "CEM 7 is sent");
        h.R.B.OnChatWindowText("You cast Creature Enchantment Mastery Self VI on yourself", 7);   // a late VI line
        Check.Eq(h.R.B.PendingSpellId, seven, "the late VI line confirms nothing");
        h.R.B.OnChatWindowText("Your spell fizzled.", 7);
        int sends = 1;
        for (int i = 0; i < 20; i++)
        {
            h.Tick(3);
            int before = FakeHost.Casts.Count;
            h.R.B.OnHeartbeat();
            Check.Eq(FakeHost.Casts.Count - before, 1, $"beat {i}: one send");
            Check.Eq(FakeHost.Casts.Last().SpellId, seven, $"beat {i}: CEM 7 again");
            sends++;
            h.R.B.OnChatWindowText("Your spell fizzled.", 7);
        }
        Check.Eq(sends, 21, "21 sends in 21 answered beats");
        // Unanswered: nothing more goes out while it waits.
        int held = FakeHost.Casts.Count;
        h.Tick(3);
        h.R.B.OnHeartbeat();                                              // sends CEM 7
        for (int i = 0; i < 10; i++) { h.Tick(0.1); h.R.B.OnHeartbeat(); }
        Check.Eq(FakeHost.Casts.Count - held, 1, "unanswered: one send, then it waits");
    }

    // With no limit, a cast the client refuses (nothing goes out, no answer comes)
    // must still wait SpellCastIntervalMs between attempts, not re-send on every heartbeat.
    private static void UpgradeClientRefusalPaced()
    {
        int cem = FamilyOfBuff("Creature Enchantment Mastery Self VI");
        var h = new BeatRig(300);
        h.FirstRun();
        var six = FakeHost.Casts.Select(c => c.SpellId).Where(id => !BuffManager.IsItemEnchantment(NameOf(id))).Distinct().ToList();
        FakeHost.Skills[(Player, STypeCreature)] = (340, 2);
        h.R.Live(six.Select(id => (NameOf(id), 3000.0)).ToArray());
        h.R.S.SpellCastIntervalMs = 600_000;          // the interval runs on the real clock: shut for this test
        try
        {
            FakeHost.CastRefused = true;
            h.SendLongAgo(700);                       // an interval has passed
            for (int i = 0; i < 30; i++) { h.Tick(0.1); h.R.B.OnHeartbeat(); }
            Check.Eq(FakeHost.CastsRefused, 1, "30 heartbeats inside one interval: one refused attempt");
            Check.Eq(h.R.B.PendingSpellId, 0, "nothing pending");
            h.SendLongAgo(700);                       // the next interval
            h.Tick(0.1);
            h.R.B.OnHeartbeat();
            Check.Eq(FakeHost.CastsRefused, 2, "the next interval: tried again");
            Check.True(FakeHost.Logs.Count(l => l.Contains("did not take (the client refused it)")) >= 2 && FakeHost.Logs.Any(l => l.Contains("Adja's Blessing")), "the refused cast was the CEM upgrade");
        }
        finally { FakeHost.CastRefused = false; }
    }

    // "Incantation of Strength Self" lands as Strength Self VI here; refreshing it is
    // not an upgrade.
    private static void IncantationRefreshNotUpgrade()
    {
        FakeHost.Reset();
        Know("Incantation of Strength Self");
        FakeHost.Skills[(Player, STypeCreature)] = (500, 2);      // tier 8
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        var rig = new BuffRig();
        rig.B.SetWorldObjectCache(FakeHost.MakeCache(rig.Host, Player, Array.Empty<uint>()));
        rig.Live(("Armor Self I", 3000)   /* a registry read with no Strength on */);
        var first = RunBuffs(rig);
        EndRun(rig);
        Check.Eq(Show(first), "Incantation of Strength Self", "the Incantation is cast and confirmed");
        rig.Live(("Strength Self VI", 3000));                     // it landed as VI
        Check.Eq(RunBuffs(rig).Count, 0, "landed a level low: not recast (the cap)");
        EndRun(rig);
        rig.Live(("Strength Self VI", 200));                      // running low
        var refresh = RunBuffs(rig);
        EndRun(rig);
        Check.Eq(Show(refresh), "Incantation of Strength Self", "refreshed");
        Check.Eq(rig.B.BuffUpgrades, 0, "not an upgrade");
        Check.False(FakeHost.Logs.Any(l => l.Contains("[Buff] upgrade:")), "no upgrade line");
    }

    private static void PriorityOrder()
    {
        var rig = SuiteRig(MeleeItem, WandItem);
        var list = rig.B.BuildDynamicBuffList();
        Check.Eq(string.Join(", ", list.Take(4)), string.Join(", ", Head), "Creature Enchantment, Focus, Willpower, Life Magic first");
        int strength = list.IndexOf("Strength Self"), heavy = list.IndexOf("Heavy Weapon Mastery"), armor = list.IndexOf("Armor Self");
        Check.True(strength == 4, $"everything else follows, Strength next (at {strength})");
        Check.True(strength < heavy && heavy < armor, "and keeps its old order (attributes, masteries, prots)");
        Check.True(list.IndexOf("Blood Drinker Self") > armor, "weapon auras stay after the prots");

        // The head is the head with every skill trained or not: an untrained Life Magic just isn't there.
        var noLifeRig = SuiteRig(MeleeItem);
        FakeHost.Skills[(Player, STypeLife)] = (10, 1);
        var noLife = noLifeRig.B.BuildDynamicBuffList();
        Check.Eq(string.Join(", ", noLife.Take(3)), string.Join(", ", Head.Take(3)), "head without Life Magic");
        Check.False(noLife.Contains("Life Magic Mastery"), "untrained Life Magic: no Life Magic Mastery");
    }

    private static void PriorityCastOrderAndLoneExpiry()
    {
        // A full run on a character with nothing on: the head goes out first, in order.
        var rig = SuiteRig(MeleeItem);
        rig.Live(("Strength Self I", 3000));   // a registry read, with none of the suite on
        rig.B.ForceFullRebuff();
        var cast = CastAll(rig, out bool done);
        Check.Eq(string.Join(", ", cast.Take(4)),
            "Creature Enchantment Mastery Self VI, Focus Self VI, Willpower Self VI, Life Magic Mastery Self VI",
            $"the four first (last skip: {rig.B.LastBuffSkipReason})");
        Check.Eq(cast.Count, 11, $"then the rest: {string.Join(", ", cast)}");
        Check.True(done, "and the run ends");

        // The automatic batch recasts a lone expiring buff alone: timers still drive it.
        var lone = SuiteRig(MeleeItem);
        AllOn(lone, "Strength Self VI");
        lone.Live(("Creature Enchantment Mastery Self VI", 3000), ("Focus Self VI", 3000), ("Willpower Self VI", 3000),
                  ("Life Magic Mastery Self VI", 3000), ("Strength Self VI", 200), ("Heavy Weapon Mastery Self VI", 3000), ("Armor Self VI", 3000));
        Check.True(lone.B.NeedsAnyBuff(), "Strength is under the rebuff line");
        cast = CastAll(lone, out done);
        Check.Eq(string.Join(", ", cast), "Strength Self VI", "only the expiring buff is recast");

        // ... and when a priority buff is the one expiring, it is the one cast.
        var head = SuiteRig(MeleeItem);
        AllOn(head);
        head.Live(("Creature Enchantment Mastery Self VI", 200), ("Focus Self VI", 3000), ("Willpower Self VI", 3000),
                  ("Life Magic Mastery Self VI", 3000), ("Strength Self VI", 3000), ("Heavy Weapon Mastery Self VI", 3000), ("Armor Self VI", 3000));
        cast = CastAll(head, out done);
        Check.Eq(string.Join(", ", cast), "Creature Enchantment Mastery Self VI", "a lone expiring priority buff: just it");
    }

    private static void ForceBuffSuite()
    {
        var rig = SuiteRig(MeleeItem);
        AllOn(rig);
        Check.False(rig.B.NeedsAnyBuff(), "everything has long timers: nothing due");
        var none = CastAll(rig, out bool done);
        Check.Eq(none.Count, 0, $"without force nothing is cast ({string.Join(", ", none)})");
        Check.True(done, "and the loop is idle");

        rig.B.ForceFullRebuff();
        Check.True(rig.B.NeedsAnyBuff(), "force: everything is due");
        var cast = CastAll(rig, out done);
        Check.Eq(string.Join(", ", cast),
            "Creature Enchantment Mastery Self VI, Focus Self VI, Willpower Self VI, Life Magic Mastery Self VI, " +
            "Strength Self VI, Heavy Weapon Mastery Self VI, Armor Self VI, " +
            "Aura of Blood Drinker Self VI, Aura of Heart Seeker Self VI, Aura of Swift Killer Self VI, Aura of Defender Self VI",
            "the whole filtered suite, in priority order; Spirit Drinker and Hermetic Link (no wand) are not in it");
        Check.True(done, "and finishes");

        // Cancel ends a run in progress.
        var c = SuiteRig(MeleeItem);
        AllOn(c);
        c.B.ForceFullRebuff();
        Check.True(c.B.GetStateSnapshot().IsForceRebuffing, "force run under way");
        c.B.CancelBuffing();
        Check.False(c.B.GetStateSnapshot().IsForceRebuffing, "cancelled: the run is over");
    }

    private static void ForceBuffEntryPoints()
    {
        // /ra buff force runs the command handler: one request, applied on the next heartbeat.
        var rig = SuiteRig(MeleeItem);
        AllOn(rig);
        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(plugin, new object[] { FakeHost.LastApi });
        typeof(RynthAiPlugin).GetField("_buffManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, rig.B);
        void Cmd(params string[] words) => typeof(RynthAiPlugin).GetMethod("HandleBuffCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(plugin, new object[] { new[] { "/ra", "buff" }.Concat(words).ToArray() });

        rig.B.MarkLiveBuffsReadyForTests();
        Cmd("force");
        Check.False(rig.B.GetStateSnapshot().IsForceRebuffing, "the command only queues the request");
        rig.B.OnHeartbeat();
        Check.True(rig.B.GetStateSnapshot().IsForceRebuffing, "the next heartbeat starts the force run");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Starting Force Rebuff")), "and says so");

        Cmd("cancel");
        rig.B.OnHeartbeat();
        Check.False(rig.B.GetStateSnapshot().IsForceRebuffing, "/ra buff cancel ends it");

        FakeHost.Chat.Clear();
        int kitRuns = rig.B.KitComputations;
        Cmd("skipped");
        Check.Eq(FakeHost.Chat.Count, 0, "/ra buff skipped only queues the report: the command thread builds nothing and says nothing");
        Check.Eq(rig.B.KitComputations, kitRuns, "and never touches the pump's weapon-kit state");
        rig.B.OnHeartbeat();
        Check.True(FakeHost.Chat.Any(c => c.Contains("Buff filter")), "/ra buff skipped answers from the next heartbeat");
        FakeHost.Chat.Clear();
        rig.B.OnHeartbeat();
        Check.Eq(FakeHost.Chat.Count, 0, "once per request");
        FakeHost.Chat.Clear();
        Cmd();
        Check.True(FakeHost.Chat.Any(c => c.Contains("/ra buff force")), "no word: the help");
    }
    private static void SkillFilter()
    {
        var trained = SuiteRig(MeleeItem);
        Check.True(trained.B.BuildDynamicBuffList().Contains("Heavy Weapon Mastery"), "Heavy Weapons trained: its mastery is cast");

        FakeHost.Reset();
        var rig = SuiteRig(MeleeItem);
        FakeHost.Skills[(Player, SkillHeavy)] = (10, 1);   // untrained
        FakeHost.Skills[(Player, SkillMagicDefense)] = (5, 1);
        var list = rig.B.BuildDynamicBuffList();
        Check.False(list.Contains("Heavy Weapon Mastery"), "Heavy Weapons untrained: its mastery is skipped");
        Check.True(list.Contains("Strength Self") && list.Contains("Armor Self"), "attribute and vital/prot buffs are untouched");
        Check.True(list.Contains("Magic Resistance Self"), "magic defense is not filtered even when untrained");
        Check.True(rig.B.FilterSkips.Any(s => s.Buff == "Heavy Weapon Mastery" && s.Reason == "skill untrained"), "and the skip is recorded");

        rig.S.SkipUnusableBuffs = false;
        Check.False(rig.B.BuildDynamicBuffList().Contains("Heavy Weapon Mastery"), "filter off: an untrained skill' mastery is still never cast");
        Check.Eq(rig.B.FilterSkips.Count, 0, "filter off: nothing is reported either");

        // The cast loop agrees: untrained and filtered, the mastery is never cast.
        var run = SuiteRig(MeleeItem);
        FakeHost.Skills[(Player, SkillHeavy)] = (10, 1);
        run.Live(("Strength Self I", 3000));
        run.B.ForceFullRebuff();
        Check.False(CastAll(run, out _).Contains("Heavy Weapon Mastery Self VI"), "the cast loop skips it too");
        var off = SuiteRig(MeleeItem);
        off.S.SkipUnusableBuffs = false;
        FakeHost.Skills[(Player, SkillHeavy)] = (10, 1);
        off.Live(("Strength Self I", 3000));
        off.B.ForceFullRebuff();
        Check.False(CastAll(off, out _).Contains("Heavy Weapon Mastery Self VI"), "filter off: the cast loop still skips an untrained mastery");
    }

    private static string[] AuraList(BuffRig rig) =>
        rig.B.BuildDynamicBuffList().Where(b => b is "Blood Drinker Self" or "Heart Seeker Self" or "Swift Killer Self"
                                              or "Spirit Drinker Self" or "Hermetic Link Self" or "Defender Self").ToArray();

    private static void WeaponFilter()
    {
        const string phys = "Blood Drinker Self, Heart Seeker Self, Swift Killer Self";
        string Join(IEnumerable<string> a) => string.Join(", ", a);

        var wand = SuiteRig(WandItem);
        Check.Eq(Join(AuraList(wand)), "Hermetic Link Self, Spirit Drinker Self, Defender Self", "only a wand: Spirit Drinker, Hermetic Link, Defender");
        var melee = SuiteRig(MeleeItem);
        Check.Eq(Join(AuraList(melee)), "Blood Drinker Self, Heart Seeker Self, Swift Killer Self, Defender Self", "a melee weapon: Blood Drinker, Heart Seeker, Swift Killer, Defender");
        Check.True(Join(AuraList(melee)).StartsWith(phys), "Blood Drinker is cast with a melee weapon");
        var bow = SuiteRig(BowItem);
        Check.Eq(Join(AuraList(bow)), "Blood Drinker Self, Heart Seeker Self, Swift Killer Self, Defender Self", "a missile weapon counts as physical");
        var both = SuiteRig(MeleeItem, WandItem);
        Check.Eq(AuraList(both).Length, 6, "melee and a wand: all six");
        var none = SuiteRig();
        Check.Eq(AuraList(none).Length, 0, "nothing registered: every weapon aura is skipped");
        var shield = SuiteRig(ShieldItem);
        Check.Eq(AuraList(shield).Length, 0, "a shield alone is no weapon");

        none.S.SkipUnusableBuffs = false;
        Check.Eq(AuraList(none).Length, 6, "filter off: all six again");

        // A listed item the object cache can't place yet doesn't hide anything.
        var unknown = SuiteRig();
        unknown.S.ItemRules = new List<ItemRule> { new() { Id = 0x7FFFFFF1, Name = "Mystery", Action = "Weapon", Element = "", ElementSource = "" } };
        Check.Eq(AuraList(unknown).Length, 6, "a listed weapon we can't classify yet: nothing filtered");

        // The reasons, named.
        var kit = new BuffManager.ListedWeaponKit(false, true, false);
        Check.Eq(BuffManager.WeaponAuraSkipReason("Blood Drinker Self", kit), "no melee or missile weapon listed", "reason, physical aura");
        Check.Eq(BuffManager.WeaponAuraSkipReason("Spirit Drinker Self", kit), "", "wand: Spirit Drinker stays");
        Check.Eq(BuffManager.WeaponAuraSkipReason("Impenetrability", kit), "", "other buffs have no weapon rule");
    }

    private static void UnlistedWandCounts()
    {
        string Join(IEnumerable<string> a) => string.Join(", ", a);
        // A wand in the pack, the Items list empty.
        var rig = SuiteRig(WandItem);
        rig.S.ItemRules = new List<ItemRule>();
        rig.S.WieldUnlistedWandWhenNoneListed = true;
        Check.Eq(Join(AuraList(rig)), "Hermetic Link Self, Spirit Drinker Self, Defender Self", "unlisted wand mode on, wand in the pack: caster auras cast");
        rig.S.WieldUnlistedWandWhenNoneListed = false;
        Check.Eq(AuraList(rig).Length, 0, "setting off: every weapon aura skipped");

        // Physical auras still need a listed melee or missile weapon.
        var mixed = SuiteRig(MeleeItem, WandItem);
        mixed.S.ItemRules = mixed.S.ItemRules.Where(x => x.Id == unchecked((int)Sword)).ToList();   // wand in the pack, only the sword listed
        mixed.S.WieldUnlistedWandWhenNoneListed = true;
        Check.Eq(AuraList(mixed).Length, 6, "sword listed + unlisted pack wand: all six");
        mixed.S.WieldUnlistedWandWhenNoneListed = false;
        Check.Eq(Join(AuraList(mixed)), "Blood Drinker Self, Heart Seeker Self, Swift Killer Self, Defender Self", "setting off: the sword's auras only");

        // On, but no wand anywhere: nothing to wield, so nothing counts.
        var empty = SuiteRig();
        empty.S.WieldUnlistedWandWhenNoneListed = true;
        Check.Eq(AuraList(empty).Length, 0, "on, but no wand available: skipped");
    }

    private static void FilterVisibility()
    {
        var rig = SuiteRig(WandItem);
        FakeHost.Skills[(Player, SkillHeavy)] = (10, 1);
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        rig.B.ForceFullRebuff();   // a restart straight away
        var lines = FakeHost.Logs.Where(l => l.Contains("[Buff] filter skips:")).ToList();
        Check.Eq(lines.Count, 1, "one skip line for two runs inside the rate limit");
        Check.True(lines[0].Contains("skill untrained: ") && lines[0].Contains("Heavy Weapon"), "it names the untrained mastery and why: " + lines[0]);
        Check.True(lines[0].Contains("no melee or missile weapon listed: Blood Drinker"), "and the weapon aura and why: " + lines[0]);
        Check.False(lines[0].Contains("Spirit Drinker"), "but not what stays");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Buff run skips") && c.Contains("no melee or missile weapon listed")), "a line goes to chat for the aura");
        Check.False(FakeHost.Chat.Any(c => c.Contains("Buff run skips") && c.Contains("skill untrained")), "but the untrained skill stays out of the chat line");
        Check.True(rig.B.FilterSkipSummary().Contains("Swift Killer"), "the settings page reads the same list");

        // The automatic batch logs it too, once.
        var auto = SuiteRig(WandItem);
        FakeHost.Skills[(Player, SkillHeavy)] = (10, 1);
        AllOn(auto, "Strength Self VI");
        auto.Live(("Creature Enchantment Mastery Self VI", 3000), ("Strength Self VI", 200));
        CastAll(auto, out _);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] filter skips:")), 1, "an automatic run logs the skips once");

        // Nothing to skip, nothing said.
        var clean = SuiteRig(MeleeItem, WandItem);
        clean.Live(("Strength Self I", 3000));
        clean.B.ForceFullRebuff();
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] filter skips:")), 0, "nothing skipped: no line");
    }

    // An untrained skill is the normal state of most characters. It must not print a chat line at every buff run.
    private static void UntrainedSkillIsQuiet()
    {
        var rig = SuiteRig(MeleeItem, WandItem);
        FakeHost.Skills[(Player, SkillHeavy)] = (10, 1);   // untrained, and nothing else is skipped
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        Check.True(rig.B.FilterSkips.Count > 0 && rig.B.FilterSkips.All(s => s.Reason == "skill untrained"), "only untrained skills are skipped");
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("Buff run skips")), 0, "no chat line for untrained skills alone");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] filter skips:") && l.Contains("skill untrained")), 1, "the log keeps it");
        Check.True(rig.B.FilterSkipSummary().Contains("skill untrained: ") && rig.B.FilterSkipSummary().Contains("Heavy Weapon"), "the Buffing page summary keeps it");
        rig.B.RequestSkipReport();
        rig.B.OnHeartbeat();
        Check.True(FakeHost.Chat.Any(c => c.Contains("Buff filter skips: ") && c.Contains("skill untrained")), "so does /ra buff skipped");

        // A later run stays quiet in chat too.
        FakeHost.Chat.Clear();
        rig.B.ForceFullRebuff();
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("Buff run skips")), 0, "still quiet");
    }

    private static void KitEdgeCases()
    {
        // A listed item the cache keeps unclassified (a Misc-typed thing): nothing is filtered.
        const uint Odd = 0x80000411;
        FakeHost.Reset();
        var odd = SuiteRig();
        FakeHost.Names[Odd] = "Mystery Thing";
        FakeHost.ItemTypes[Odd] = 0;
        odd.B.SetWorldObjectCache(FakeHost.MakeCache(odd.Host, Player, new[] { Odd }));
        odd.S.ItemRules = new List<ItemRule> { new() { Id = unchecked((int)Odd), Name = "Mystery Thing", Action = "Weapon", Element = "", ElementSource = "" } };
        var cls = odd.B.ListedWeapons();
        Check.True(cls.Unknown, $"the odd item stays unclassified (kit {cls})");
        Check.Eq(AuraList(odd).Length, 6, "an unclassifiable listed item: all six auras kept");

        // No cache at all, nothing listed: can't tell, so nothing is filtered.
        FakeHost.Reset();
        Know("Aura of Blood Drinker Self VI");
        var bare = new BuffRig();
        Check.Eq(AuraList(bare).Length, 6, "no object cache: nothing filtered");

        // Only ammo listed: not a weapon.
        const uint Arrows = 0x80000407;
        FakeHost.Reset();
        var ammo = SuiteRig();
        FakeHost.Names[Arrows] = "Arrow";
        FakeHost.ItemTypes[Arrows] = 0x100;
        FakeHost.Ints[(Arrows, 9)] = 0x00800000;
        ammo.B.SetWorldObjectCache(FakeHost.MakeCache(ammo.Host, Player, new[] { Arrows }));
        ammo.S.ItemRules = new List<ItemRule> { new() { Id = unchecked((int)Arrows), Name = "Arrow", Action = "Weapon", Element = "", ElementSource = "" } };
        var k = ammo.B.ListedWeapons();
        Check.False(k.Physical, "ammo is no physical weapon");
        Check.Eq(AuraList(ammo).Length, 0, "ammo-only list (and nothing else): every weapon aura skipped");

        // Wielded weapons count with an empty Items list.
        var sword = SuiteRig(MeleeItem);
        sword.S.ItemRules = new List<ItemRule>();
        sword.S.WieldUnlistedWandWhenNoneListed = false;
        Check.Eq(AuraList(sword).Length, 0, "control: sword in the pack, not wielded, nothing listed: skipped");
        sword = SuiteRig(MeleeItem);
        sword.S.ItemRules = new List<ItemRule>();
        sword.S.WieldUnlistedWandWhenNoneListed = false;
        FakeHost.Ints[(Sword, 10)] = 0x00100000;   // wielded in the melee slot
        Check.Eq(string.Join(", ", AuraList(sword)), "Blood Drinker Self, Heart Seeker Self, Swift Killer Self, Defender Self", "wielded sword, empty Items list: physical auras and Defender");

        var wand = SuiteRig(WandItem);
        wand.S.ItemRules = new List<ItemRule>();
        wand.S.WieldUnlistedWandWhenNoneListed = false;
        FakeHost.Ints[(Wand, 10)] = 0x01000000;    // wielded in the held slot
        Check.Eq(string.Join(", ", AuraList(wand)), "Hermetic Link Self, Spirit Drinker Self, Defender Self", "wielded wand, empty Items list: caster auras and Defender");
    }

    private static void SkipLineGroupedAndCapped()
    {
        var rig = SuiteRig(WandItem);
        foreach (AcSkillType sk in Enum.GetValues<AcSkillType>())
        {
            uint st = CharacterSkills.AcSkillTypeToSTypeSkill(sk);
            if (st == 0 || st == STypeCreature || st == STypeLife || st == STypeItem) continue;
            FakeHost.Skills[(Player, st)] = (5, 1);
        }
        rig.Live(("Strength Self I", 3000));
        rig.B.ForceFullRebuff();
        var lines = FakeHost.Logs.Where(l => l.Contains("[Buff] filter skips:")).ToList();
        Check.Eq(lines.Count, 1, "one line");
        string line = lines[0].Substring(lines[0].IndexOf("skips:", StringComparison.Ordinal) + 7);
        Check.True(line.Length <= 200, $"{line.Length} chars: {line}");
        Check.True(line.Contains("more)"), "the long group is capped: " + line);
        Check.True(line.Contains("no melee or missile weapon listed: Blood Drinker, Heart Seeker, Swift Killer"), "the weapon-aura skip is still named: " + line);

        // The same content again inside 30 s: held back. A change prints at once.
        rig.B.ForceFullRebuff();
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] filter skips:")), 1, "same content: no second line");
        rig.S.ItemRules = new List<ItemRule> { new() { Id = unchecked((int)Sword), Name = "Long Sword", Action = "Weapon", Element = "", ElementSource = "" } };
        FakeHost.Ints[(Sword, 9)] = 0x00100000; FakeHost.Names[Sword] = "Long Sword"; FakeHost.ItemTypes[Sword] = 0x1;
        rig.B.SetWorldObjectCache(FakeHost.MakeCache(rig.Host, Player, new[] { Sword, Wand }));
        rig.B.ForceFullRebuff();
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Buff] filter skips:")), 2, "different content prints at once");

        // The summary string is built once per change.
        string a = rig.B.FilterSkipSummary(), b = rig.B.FilterSkipSummary();
        Check.True(ReferenceEquals(a, b), "cached summary");
    }

    private static void KitCached()
    {
        var rig = SuiteRig(MeleeItem);
        long now = 10_000;
        rig.B.KitClockMs = () => now;
        for (int i = 0; i < 100; i++) rig.B.BuildDynamicBuffList();
        Check.Eq(rig.B.KitComputations, 1, "100 passes inside a second: one computation");
        now += 1_500;
        rig.B.BuildDynamicBuffList();
        Check.Eq(rig.B.KitComputations, 2, "after the TTL: again");
        rig.S.ItemRules = new List<ItemRule>(rig.S.ItemRules);
        rig.B.BuildDynamicBuffList();
        Check.Eq(rig.B.KitComputations, 3, "a new Items list: again, before the TTL");
        rig.S.ItemRules.Add(new ItemRule { Id = 1, Name = "x", Action = "Weapon" });
        rig.B.BuildDynamicBuffList();
        Check.Eq(rig.B.KitComputations, 4, "an item added in place: again");
        rig.S.WieldUnlistedWandWhenNoneListed = !rig.S.WieldUnlistedWandWhenNoneListed;
        rig.B.BuildDynamicBuffList();
        Check.Eq(rig.B.KitComputations, 5, "the unlisted-wand setting flipped: again");
        rig.B.BuildDynamicBuffList();
        Check.Eq(rig.B.KitComputations, 5, "and then cached");
    }

    private static void SkipUnusableSetting()
    {
        Check.True(new LegacyUiSettings().SkipUnusableBuffs, "a new profile: on");
        var old = JsonSerializer.Deserialize("{\"EnableBuffing\":true,\"RebuffSecondsRemaining\":300}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.SkipUnusableBuffs, "a profile saved before the setting: on");
        var off = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { SkipUnusableBuffs = false }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(off != null && !off.SkipUnusableBuffs, "off is saved and loaded");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { SkipUnusableBuffs = false }, dst });
        Check.False(dst.SkipUnusableBuffs, "a profile with it off loads off");
        copy.Invoke(null, new object[] { old!, dst });
        Check.True(dst.SkipUnusableBuffs, "an old profile loads on");

        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.SkipUnusableBuffs == null, "an older engine's payload has no value");
        var s = new LegacyUiSettings { SkipUnusableBuffs = false };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.SkipUnusableBuffs == false, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.False(s.SkipUnusableBuffs, "a save without the field leaves it off");
        dash.ApplySettingsJson("{\"SkipUnusableBuffs\":true}");
        Check.True(s.SkipUnusableBuffs, "a save with it on turns it on");

        // The settings map the meta engine (and /ra settings) reads.
        var live = new LegacyUiSettings();
        var map = ExpressionTests.Engine(settings: live).BuildSettingsMapPublic();
        Check.True(map.TryGetValue("SkipUnusableBuffs", out var entry), "SkipUnusableBuffs is in the settings map");
        Check.Eq(entry.Get(), "1", "reads on");
        entry.Set("0");
        Check.False(live.SkipUnusableBuffs, "and sets off");
    }
}
