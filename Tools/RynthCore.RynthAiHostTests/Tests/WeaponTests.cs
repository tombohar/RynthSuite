using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Weapons (2026-09-30): each weapon's element from its properties (DamageType, the icon's
// element glow, a rending imbue, the name), the identify pacing, the Items list keeping the
// user's element, full names with the material, the element-and-weapon rule (the weakness
// picks the wand), listed weapons only, the swap tracker (one in flight, retry cap, no
// flip-flop), and the tester's Dragon's Isle case: an unlisted fire wand wielded, Blades cast.
internal static class WeaponTests
{
    private const uint Player = 0x50000C01;

    public static void Register(Runner r)
    {
        r.Add("weapon element: DamageType, then the icon, then a rending, then the name", ElementSources);
        r.Add("weapon element: read through the host (ints 45/18/179, float 152)", ElementFromHost);
        r.Add("weapon element: one identify at a time, paced, at most twice per weapon", IdentifyPacing);
        r.Add("weapon element: the Items list fills in and keeps the user's pick", ItemsListElements);
        r.Add("weapon names: material in front, element after, saved by id", WeaponNaming);
        r.Add("element choice: the rule's damage type drives the wand", ChoiceRule);
        r.Add("element choice: the weakness picks the wand, not the wand in hand", ChoiceWeakness);
        r.Add("element choice: no wand of the weakness, the next-weakest a listed wand has", ChoiceNextWeakest);
        r.Add("element choice: a tie goes to the wand in hand, not to Slash", ChoiceTie);
        r.Add("element choice: weakness unknown, the wand in hand and its element; else Slash", ChoiceUnknown);
        r.Add("element choice: a matching rending wins for its element", ChoiceRending);
        r.Add("element choice: a wand always casts its own element (rule, Damage tab, no match)", ChoiceWandCastsOwn);
        r.Add("element choice: elements the character can't cast are skipped", ChoiceCastable);
        r.Add("preferred damage: Melee/Missile/War/Void picks the pool; Auto keeps the first listed kind", PreferredPicksPool);
        r.Add("preferred damage: War never casts Nether, Void nothing else; an unusable preference falls back to Auto", PreferredSchools);
        r.Add("preferred damage: other-kind slayers, the Default row's weapon and the rule's element obey it; the monster's own pick doesn't", PreferredVsOtherSteps);
        r.Add("preferred damage: names parse tolerantly; a new rule is Auto", PreferredParse);
        r.Add("slayer: beats the weakness and the Monsters rule's damage type", SlayerBeatsWeaknessAndRule);
        r.Add("slayer: only this monster's own Damage tab weapon beats it, not the DEFAULT row's", SlayerVsDamageTab);
        r.Add("slayer (owner's rule 2026-10-05): a Slash slayer beats a Pierce weapon the monster is weaker to", SlayerBeatsLowerResist);
        r.Add("slayer: this monster's own pick that is a slayer is used, labelled", SlayerManualPickIsSlayer);
        r.Add("slayer: the Damage tab's learned best never beats it", SlayerBeatsLearnedBest);
        r.Add("slayer: bonus not sent (ACE never sends float 138) still slays, ranked below a known bonus", SlayerBonusUnknown);
        r.Add("slayer: creature type from the world-database table before the monster's appraisal", SlayerTypeFromTable);
        r.Add("slayer: other creature types, an x1 bonus and other kinds are ignored", SlayerNonMatching);
        r.Add("slayer: the bigger bonus wins, then the element order, then the hand", SlayerOrder);
        r.Add("slayer: creature type unknown, the old choice", SlayerUnknownType);
        r.Add("slayer: the element follows the chosen weapon", SlayerElement);
        r.Add("slayer: read through the host (int 166, float 138, monster int 2), cached by wcid", SlayerFromHost);
        r.Add("refusal: a refused stance change in combat's equip pass sets no backoff and starts no stuck clock", StanceRefusedKeepsBackoff);
        r.Add("weapon choice: an unlisted wand is never picked (Damage tab, learned, pack, name)", UnlistedNeverPicked);
        r.Add("cast element: with the plan's wand not in hand (a held swap), the wand in hand casts its own element (R-20261029-125901-e22d)", HeldWandCastsItsElement);
        r.Add("weapon choice: the Monsters rule's weapon is used (R-20261022-193411-fb72), below this monster's Damage tab pick; the Default row's like the Damage tab default", MonsterRuleWeapon);
        r.Add("Items list: a listed item no longer carried is reported missing (pack, hand, unknown)", MissingFromPack);
        r.Add("weapon choice: a listed weapon someone else now has (sold) is skipped, even as this monster's Damage tab pick", SoldWeaponSkipped);
        r.Add("weapon swap: one in flight, confirmed, retry cap, then what's in hand", SwapOneInFlight);
        r.Add("weapon swap: no flip-flop between two weapons", SwapNoFlipFlop);
        r.Add("weapon swap: a new target that wants the weapon just swapped away from gets it at once (Drakkon 2026-10-12)", SwapBackForNewTarget);
        r.Add("weapon swap: a server refusal counts as a failed try", SwapRefused);
        r.Add("weapon swap: 'too busy' is no try, and a portal or recall ends the rest (Kelpie 2026-10-15)", SwapBusyNotCounted);
        r.Add("weapon swap: combat reads ACE's busy answer (0x1D then SaveFailed 0) as busy, a real refusal as a try", CombatReadsBusyWield);
        r.Add("weapon swap: a wield that lands after the choice moved still counts (no swap straight back)", SwapLandsWhenChoiceMoved);
        r.Add("element choice: a weakness swap must beat the weapon in hand by the min gain", ChoiceMinSwapGain);
        r.Add("tester's case: Dragon's Isle, an unlisted fire wand and Blades", TesterDragonsIsle);
    }

    // ── Element detection ─────────────────────────────────────────────────────

    private static void ElementSources()
    {
        var d = WeaponElements.Detect(0x10, null, null, "Frost Wand");
        Check.Eq(d.Element, "Fire", "DamageType Fire beats a Frost name");
        Check.Eq(d.Source, "properties", "source");
        Check.Eq(WeaponElements.Detect(0x3, null, null, "Sword").Element, "Slash", "Slash/Pierce sword: Slash");
        Check.Eq(WeaponElements.Detect(0x400, null, null, null).Element, "Nether", "nether caster");
        Check.Eq(WeaponElements.Detect(0x40, null, null, null).Element, "Lightning", "electric caster");

        var icon = WeaponElements.Detect(null, 0x0200, 0x0080, "Fire Wand");
        Check.Eq(icon.Element, "Cold", "not identified: the icon's Frost glow beats a Fire Rending and the name");
        Check.Eq(icon.Source, "icon", "source icon");
        Check.Eq(icon.Rending, "Fire", "the rending is still reported");

        var rend = WeaponElements.Detect(0, 0x0200, 0, "Ivory Wand");
        Check.Eq(rend.Element, "Fire", "no DamageType, no glow: the Fire Rending");
        Check.Eq(rend.Source, "rending", "source rending");

        var ui = new (int Fx, string E)[]
        {
            (0x20, "Fire"), (0x40, "Lightning"), (0x80, "Cold"), (0x100, "Acid"), (0x200, "Bludgeon"),
            (0x400, "Slash"), (0x800, "Pierce"), (0x1000, "Nether"), (0x1, ""), (0x0, ""), (0x21, "Fire"),
        };
        foreach (var (fx, e) in ui) Check.Eq(WeaponElements.FromUiEffects(fx), e, $"UiEffects 0x{fx:X}");
        var imb = new (int Imbue, string E)[]
        {
            (0x8, "Slash"), (0x10, "Pierce"), (0x20, "Bludgeon"), (0x40, "Acid"), (0x80, "Cold"),
            (0x100, "Lightning"), (0x200, "Fire"), (0x4000, "Nether"), (0x4, ""), (0x1, ""),
        };
        foreach (var (i, e) in imb) Check.Eq(WeaponElements.FromImbuedEffect(i), e, $"ImbuedEffect 0x{i:X}");

        var names = new (string Name, string E)[]
        {
            ("Flaming Wand", "Fire"), ("Flame Staff", "Fire"), ("Fire Orb", "Fire"), ("Frost Baton", "Cold"),
            ("Ice Scepter", "Cold"), ("Acid Wand", "Acid"), ("Lightning Staff", "Lightning"),
            ("Electric Orb", "Lightning"), ("Staff of Blades", "Slash"), ("Blade Wand", "Slash"),
            ("Piercing Orb", "Pierce"), ("Bludgeoning Staff", "Bludgeon"), ("Nether Orb", "Nether"),
            ("Iceberg Wand", ""), ("Ivory Wand", ""), ("", ""),
        };
        foreach (var (n, e) in names) Check.Eq(WeaponElements.FromName(n), e, $"name '{n}'");
        Check.Eq(WeaponElements.Detect(null, null, null, "Ivory Wand").Unknown, true, "nothing known: unknown (no Slash default)");
        Check.Eq(WeaponElements.Normalize("Frost"), "Cold", "Frost = Cold");
        Check.Eq(WeaponElements.Normalize("Blade"), "Slash", "Blade = Slash");
        Check.Eq(WeaponElements.Normalize("Electric"), "Lightning", "Electric = Lightning");
    }

    private static void ElementFromHost()
    {
        FakeHost.Reset();
        const uint wand = 0x60000031, sword = 0x60000032;
        FakeHost.Ints[(wand, 45)] = 0x10;
        FakeHost.Doubles[(wand, 152)] = 1.12;
        FakeHost.Ints[(sword, 18)] = 0x0100;     // an Acid glow, before any identify
        FakeHost.Ints[(sword, 179)] = 0x0040;    // Acid Rending
        var host = FakeHost.Create();
        var w = WeaponElementTracker.ReadFrom(host, unchecked((int)wand), "Wand");
        Check.Eq(w.Element, "Fire", "caster DamageType 45");
        Check.Eq(w.Bonus, 1.12, "ElementalDamageMod 152");
        var s = WeaponElementTracker.ReadFrom(host, unchecked((int)sword), "Sword");
        Check.Eq(s.Element, "Acid", "icon element");
        Check.Eq(s.Source, "icon", "source icon");
        Check.Eq(s.Rending, "Acid", "rending read");
    }

    private static void IdentifyPacing()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        var host = FakeHost.Create();
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        var tr = new WeaponElementTracker(host, () => t);
        int[] ids = { 0x60000041, 0x60000042, 0x60000043 };

        Check.Eq(tr.Pump(ids), ids[0], "first: the first weapon not identified");
        Check.Eq(tr.Pump(ids), 0, "one at a time: nothing while it's in flight");
        t = t.AddMilliseconds(2100);
        Check.Eq(tr.Pump(ids), 0, "past the gap but not answered and not timed out: still waiting");
        FakeHost.Appraised.Add(unchecked((uint)ids[0]));
        Check.Eq(tr.Pump(ids), ids[1], "answered: the next one");
        t = t.AddMilliseconds(500);
        FakeHost.Appraised.Add(unchecked((uint)ids[1]));
        Check.Eq(tr.Pump(ids), 0, "answered, but paced: no request inside the gap");
        t = t.AddMilliseconds(2000);
        Check.Eq(tr.Pump(ids), ids[2], "after the gap: the third");
        t = t.AddMilliseconds(5100);
        Check.Eq(tr.Pump(ids), ids[2], "never answered: asked a second time after the timeout");
        t = t.AddMilliseconds(5100);
        Check.Eq(tr.Pump(ids), 0, "at most twice per weapon");
        Check.Eq(FakeHost.IdRequests.Count, 4, "four identifies in all");
        Check.Eq(tr.RequestsSent, 4, "tracker count");
    }

    private static void ItemsListElements()
    {
        var rules = new List<ItemRule>
        {
            new() { Id = 1, Name = "Old Sword", Element = "Slash" },                         // legacy default
            new() { Id = 2, Name = "Old Wand", Element = "Fire" },                           // legacy, not the default
            new() { Id = 3, Name = "Set Wand", Element = "Cold", ElementSource = "set" },    // the user's
            new() { Id = 4, Name = "Flame Wand", Element = "Fire", ElementSource = "name" }, // name, then identified
            new() { Id = 5, Name = "Ivory Wand", Element = "", ElementSource = "" },         // nothing known yet
        };
        var known = new Dictionary<int, WeaponElements.Info>
        {
            [1] = new("Pierce", "properties", "", 1.0),
            [2] = new("Acid", "properties", "", 1.0),
            [3] = new("Fire", "properties", "", 1.0),
            [4] = new("Acid", "properties", "", 1.0),
        };
        int changed = WeaponList.RefreshElements(rules, (id, _) => known.TryGetValue(id, out var i) ? i : new("", "", "", 1.0));
        Check.Eq(rules[0].Element, "Pierce", "a legacy Slash default is replaced once properties are known");
        Check.Eq(rules[1].Element, "Fire", "a legacy non-Slash element is kept as the user's");
        Check.Eq(rules[1].ElementSource, "set", "and marked as the user's");
        Check.Eq(rules[2].Element, "Cold", "the user's pick beats the properties");
        Check.Eq(rules[3].Element, "Acid", "properties beat the name");
        Check.Eq(rules[4].Element, "", "still unknown");
        Check.Eq(changed, 3, "three entries changed");

        // The panel round trip: it shows full names and "Unknown", sends them back; saved by id.
        var shown = WeaponList.ForDisplay(rules, r => "Silver " + r.Name);
        Check.Eq(shown[4].Element, "Unknown", "an unknown element shows as Unknown");
        Check.Eq(shown[0].Name, "Silver Old Sword", "the panel gets the full name");
        shown[0].Element = "Bludgeon";                 // the user picks an element
        shown.RemoveAt(1);                             // and deletes one
        var merged = WeaponList.MergeEdited(rules, shown);
        Check.Eq(merged.Count, 4, "the deletion is kept");
        Check.Eq(merged[0].Name, "Old Sword", "the saved name stays (matched by id, not by the shown name)");
        Check.Eq(merged[0].Element, "Bludgeon", "the user's element");
        Check.Eq(merged[0].ElementSource, "set", "is the user's");
        Check.Eq(merged[3].Element, "", "Unknown comes back as unknown");
        Check.Eq(merged[3].ElementSource, "", "and keeps no source");
        Check.Eq(merged[2].ElementSource, "properties", "an untouched entry keeps its source");
    }

    private static void WeaponNaming()
    {
        Check.Eq(WeaponNames.Full("Wand", 63), "Silver Wand", "Silver (63)");
        Check.Eq(WeaponNames.Full("Staff", 73), "Ebony Staff", "Ebony (73)");
        Check.Eq(WeaponNames.Full("Orb", 15), "Black Garnet Orb", "Black Garnet (15)");
        Check.Eq(WeaponNames.Full("Scepter", 26), "Imperial Topaz Scepter", "Imperial Topaz (26)");
        Check.Eq(WeaponNames.Full("Silver Wand", 63), "Silver Wand", "already named with it");
        Check.Eq(WeaponNames.Full("Wand", 0), "Wand", "no material");
        Check.Eq(WeaponNames.Full("Wand", 999), "Wand", "unknown material");
        Check.Eq(WeaponNames.WithElement("Silver Wand", "Fire"), "Silver Wand (Fire)", "with the element");
        Check.Eq(WeaponNames.WithElement("Silver Wand", ""), "Silver Wand", "no element");
        Check.Eq(WeaponNames.Material(53), "Armoredillo Hide", "ACE spelling, split into words");

        FakeHost.Reset();
        const uint wand = 0x60000051;
        FakeHost.Names[wand] = "Wand";
        FakeHost.Ints[(wand, 131)] = 63;
        var host = FakeHost.Create();
        Check.Eq(WeaponNames.For(host, null, unchecked((int)wand), "?"), "Silver Wand", "read through the host");
        Check.Eq(WeaponNames.For(host, null, 0x60000052, "Old Name"), "Old Name", "a gone object keeps the saved name");
    }

    // ── Element choice (WeaponPlanner) ────────────────────────────────────────

    private static WeaponCandidate Wand(int id, string elem, bool inHand = false, string rending = "")
        => new(id, "Wand " + id, CombatMode.Magic, elem, rending, inHand);

    private static CreatureWeakness.Ranking Rank(params (string E, double M)[] order)
        => new() { Source = "test", Order = order.OrderByDescending(o => o.M).ToList() };

    private static bool All(string e) => true;

    private static WeaponCandidate Weapon(int id, int kind, string elem = "", bool inHand = false)
        => new(id, "Weapon " + id, kind, elem, "", inHand);

    private static void PreferredPicksPool()
    {
        // A sword listed first, a bow, a fire wand and a nether wand: Auto fights with the first kind (melee).
        var list = new List<WeaponCandidate>
        {
            Weapon(1, CombatMode.Melee, "Slash"), Weapon(2, CombatMode.Missile, "Pierce"),
            Weapon(3, CombatMode.Magic, "Fire"), Weapon(4, CombatMode.Magic, "Nether"),
        };
        var weak = Rank(("Fire", 1.5), ("Nether", 1.4), ("Slash", 1.0), ("Pierce", 1.0));
        Check.Eq(WeaponPlanner.Choose(list, "", weak, All).WeaponId, 1, "Auto: the first listed kind (melee), the Slash sword");
        Check.Eq(WeaponPlanner.Choose(list, "", weak, All, preferredDamage: "Auto").WeaponId, 1, "Auto spelled out");

        var melee = WeaponPlanner.Choose(list, "", weak, All, preferredDamage: "Melee");
        Check.Eq(melee.WeaponId, 1, "Melee: the sword");
        Check.True(melee.Source.EndsWith("prefers Melee"), "Melee: the source says why (" + melee.Source + ")");
        var missile = WeaponPlanner.Choose(list, "", weak, All, preferredDamage: "Missile");
        Check.Eq(missile.WeaponId, 2, "Missile: the bow, though the monster is weaker to Fire and Nether");
        var war = WeaponPlanner.Choose(list, "", weak, All, preferredDamage: "War Magic");
        Check.Eq(war.WeaponId, 3, "War Magic: the fire wand");
        Check.Eq(war.Element, "Fire", "casting Fire");
        var voidP = WeaponPlanner.Choose(list, "", weak, All, preferredDamage: "Void Magic");
        Check.Eq(voidP.WeaponId, 4, "Void Magic: the nether wand");
        Check.Eq(voidP.Element, "Nether", "casting Nether");

        // Weakness unknown: the weapon in hand within the pool, else the first of the pool.
        var hand = new List<WeaponCandidate>
        {
            Weapon(1, CombatMode.Melee, "Slash", inHand: true), Weapon(2, CombatMode.Magic, "Fire"), Weapon(3, CombatMode.Magic, "Cold"),
        };
        Check.Eq(WeaponPlanner.Choose(hand, "", null, All, preferredDamage: "War Magic").WeaponId, 2, "unknown weakness: the first War wand, not the sword in hand");
    }

    private static void PreferredSchools()
    {
        // A wand with no element: War casts the weakest non-Nether element, Void casts Nether.
        var plain = new List<WeaponCandidate> { Weapon(1, CombatMode.Magic, "", inHand: true) };
        var weak = Rank(("Nether", 1.6), ("Fire", 1.4), ("Cold", 1.0));
        Check.Eq(WeaponPlanner.Choose(plain, "", weak, All).Element, "Nether", "Auto: the weakest castable, Nether");
        Check.Eq(WeaponPlanner.Choose(plain, "", weak, All, preferredDamage: "War Magic").Element, "Fire", "War Magic: Nether is out, Fire");
        var voidPlain = WeaponPlanner.Choose(plain, "", weak, All, preferredDamage: "Void Magic");
        Check.Eq(voidPlain.WeaponId, 1, "an element-less wand is a War wand: no Void pool, so Auto keeps the same wand");
        Check.False(voidPlain.Source.Contains("prefers"), "and the source doesn't claim the preference");

        // An unusable preference falls back to Auto and doesn't claim the preference.
        var mixed = new List<WeaponCandidate> { Weapon(1, CombatMode.Melee, "Slash"), Weapon(2, CombatMode.Magic, "Fire") };
        var noVoid = WeaponPlanner.Choose(mixed, "", null, All, preferredDamage: "Void Magic");
        Check.Eq(noVoid.WeaponId, 1, "no nether wand listed: Auto, the first kind");
        Check.False(noVoid.Source.Contains("prefers"), "and the source doesn't claim the preference");
        var noVoidSkill = WeaponPlanner.Choose(
            new List<WeaponCandidate> { Weapon(1, CombatMode.Melee, "Slash"), Weapon(2, CombatMode.Magic, "Nether") }, "", null,
            e => e != "Nether", preferredDamage: "Void Magic");
        Check.Eq(noVoidSkill.WeaponId, 1, "a nether wand but no Void Magic: Auto");
        var noWarSkill = WeaponPlanner.Choose(mixed, "", null, e => false, preferredDamage: "War Magic");
        Check.Eq(noWarSkill.WeaponId, 1, "a fire wand but nothing castable: Auto");
        var noMissile = WeaponPlanner.Choose(mixed, "", null, All, preferredDamage: "Missile");
        Check.Eq(noMissile.WeaponId, 1, "no launcher listed: Auto");
        Check.Eq(WeaponPlanner.Choose(new List<WeaponCandidate>(), "", null, All, preferredDamage: "Melee").WeaponId, 0, "nothing listed: nothing");
    }

    private static void PreferredVsOtherSteps()
    {
        var list = new List<WeaponCandidate>
        {
            Weapon(1, CombatMode.Melee, "Slash"), Weapon(2, CombatMode.Magic, "Fire"), Weapon(3, CombatMode.Magic, "Nether"),
        };

        // A slayer of another kind is not a candidate (like a melee slayer for a mage).
        var slayer = new List<WeaponCandidate>
        {
            new(1, "Sword", CombatMode.Melee, "Slash", "", false, SlayerType: 5, SlayerBonus: 2),
            Weapon(2, CombatMode.Magic, "Fire"),
        };
        Check.Eq(WeaponPlanner.Choose(slayer, "", null, All, creatureType: 5).WeaponId, 1, "Auto: the melee slayer");
        Check.Eq(WeaponPlanner.Choose(slayer, "", null, All, creatureType: 5, preferredDamage: "War Magic").WeaponId, 2, "War Magic: the slayer isn't used");

        // The Default row's weapon (fixedIsDefault) only when it is of the preferred kind.
        Check.Eq(WeaponPlanner.Choose(list, "", null, All, fixedWeaponId: 1, fixedIsDefault: true, preferredDamage: "War Magic").WeaponId, 2,
            "Default row's sword, War Magic preferred: a wand");
        Check.Eq(WeaponPlanner.Choose(list, "", null, All, fixedWeaponId: 2, fixedIsDefault: true, preferredDamage: "War Magic").WeaponId, 2,
            "Default row's fire wand, War Magic preferred: kept");
        Check.Eq(WeaponPlanner.Choose(list, "", null, All, fixedWeaponId: 1, fixedIsDefault: true).WeaponId, 1, "Auto: the Default row's sword");

        // The monster's own Damage tab weapon is the player's explicit pick: it wins.
        Check.Eq(WeaponPlanner.Choose(list, "", null, All, fixedWeaponId: 1, fixedIsDefault: false, preferredDamage: "War Magic").WeaponId, 1,
            "this monster's own sword pick, War Magic preferred: the sword");

        // The rule's element picks within the preferred kind; none of it there: stay in the kind.
        var cast = WeaponPlanner.Choose(list, "Nether", null, All, preferredDamage: "Void Magic");
        Check.Eq(cast.WeaponId, 3, "rule Nether + Void Magic: the nether wand");
        var other = WeaponPlanner.Choose(list, "Slash", null, All, preferredDamage: "War Magic");
        Check.Eq(other.WeaponId, 2, "rule Slash + War Magic: no Slash wand, so the war wand, not the sword");
    }

    private static void PreferredParse()
    {
        Check.Eq(DamageStyles.Parse("war magic"), DamageStyle.War, "lower case with a space");
        Check.Eq(DamageStyles.Parse("WarMagic"), DamageStyle.War, "joined");
        Check.Eq(DamageStyles.Parse("Void"), DamageStyle.Void, "short form");
        Check.Eq(DamageStyles.Parse("Void Magic"), DamageStyle.Void, "panel name");
        Check.Eq(DamageStyles.Parse("Melee"), DamageStyle.Melee, "melee");
        Check.Eq(DamageStyles.Parse("Missile"), DamageStyle.Missile, "missile");
        Check.Eq(DamageStyles.Parse(null), DamageStyle.Auto, "null");
        Check.Eq(DamageStyles.Parse("Fire"), DamageStyle.Auto, "unknown");
        foreach (string n in DamageStyles.Names) Check.Eq(DamageStyles.Name(DamageStyles.Parse(n)), n, "round trip " + n);
        Check.Eq(new MonsterRule().PreferredDamage, "Auto", "a new rule is Auto");
    }

    private static void ChoiceRule()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire", inHand: true), Wand(2, "Cold") };
        var weak = Rank(("Fire", 1.5), ("Cold", 0.5));
        var p = WeaponPlanner.Choose(wands, "Cold", weak, All);
        Check.Eq(p.WeaponId, 2, "the rule says Cold: the Cold wand, whatever the weakness and the hand");
        Check.Eq(p.Element, "Cold", "casts Cold");
        var q = WeaponPlanner.Choose(wands, "Frost", weak, All);
        Check.Eq(q.WeaponId, 2, "Frost is Cold");
        var none = WeaponPlanner.Choose(wands, "Acid", weak, All);
        Check.Eq(none.WeaponId, 1, "no Acid wand listed: keep the one in hand");
        Check.Eq(none.Element, "Fire", "and the wand in hand casts its own element (Fire), not the rule's Acid");
    }

    private static void ChoiceWeakness()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Cold", inHand: true), Wand(2, "Fire"), Wand(3, "Acid") };
        var weak = Rank(("Fire", 1.4), ("Acid", 1.1), ("Cold", 0.6), ("Slash", 1.0));
        var p = WeaponPlanner.Choose(wands, "", weak, All);
        Check.Eq(p.WeaponId, 2, "weak to Fire: the Fire wand, though Cold is in hand");
        Check.Eq(p.Element, "Fire", "casts Fire");
        Check.Eq(p.Source, "weak to Fire (test)", "source");
    }

    private static void ChoiceNextWeakest()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Cold"), Wand(2, "Acid") };
        var weak = Rank(("Fire", 1.4), ("Acid", 1.1), ("Cold", 1.0), ("Lightning", 0.2));
        var p = WeaponPlanner.Choose(wands, "", weak, All);
        Check.Eq(p.WeaponId, 2, "no Fire wand: Acid, the next-weakest a listed wand has");
        Check.Eq(p.Element, "Acid", "casts Acid");

        var generic = new List<WeaponCandidate> { Wand(7, "", inHand: true) };
        var g = WeaponPlanner.Choose(generic, "", weak, All);
        Check.Eq(g.WeaponId, 7, "no listed wand has any element: keep the one in hand");
        Check.Eq(g.Element, "Fire", "and cast the weakest element");
    }

    private static void ChoiceTie()
    {
        // Physical 1.0 and Fire 1.0: the table lists Slash first. With a fire wand in hand the
        // tie goes to Fire (no swap, and not Blades from a fire wand).
        var weak = Rank(("Slash", 1.0), ("Pierce", 1.0), ("Bludgeon", 1.0), ("Fire", 1.0), ("Cold", 0.5));
        var wands = new List<WeaponCandidate> { Wand(1, "Cold"), Wand(2, "Fire", inHand: true), Wand(3, "Slash") };
        var p = WeaponPlanner.Choose(wands, "", weak, All);
        Check.Eq(p.WeaponId, 2, "the tie goes to the wand in hand");
        Check.Eq(p.Element, "Fire", "casts Fire, not Slash");

        var notInHand = new List<WeaponCandidate> { Wand(1, "Cold", inHand: true), Wand(2, "Fire"), Wand(3, "Slash") };
        var q = WeaponPlanner.Choose(notInHand, "", weak, All);
        Check.Eq(q.WeaponId, 3, "nothing of the tie in hand: the table order (Slash first)");
        Check.Eq(WeaponPlanner.TieGroups(weak).Count, 2, "two tie groups");
    }

    private static void ChoiceUnknown()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire"), Wand(2, "Cold", inHand: true) };
        var p = WeaponPlanner.Choose(wands, "", null, All);
        Check.Eq(p.WeaponId, 2, "weakness unknown: keep the wand in hand");
        Check.Eq(p.Element, "Cold", "and cast its element");
        Check.Eq(p.Source, "in hand (weakness unknown)", "source");

        var generic = new List<WeaponCandidate> { Wand(5, "") };
        var g = WeaponPlanner.Choose(generic, "", null, All);
        Check.Eq(g.Element, "Slash", "nothing known at all: Slash");
        var noSlash = WeaponPlanner.Choose(generic, "", null, e => e != "Slash");
        Check.Eq(noSlash.Element, "Fire", "can't cast Slash: the next in the fallback order");
        var none = WeaponPlanner.Choose(new List<WeaponCandidate>(), "", null, All);
        Check.Eq(none.WeaponId, 0, "nothing listed: no weapon");
    }

    private static void ChoiceRending()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire"), Wand(2, "Fire", rending: "Fire") };
        Check.Eq(WeaponPlanner.Choose(wands, "Fire", null, All).WeaponId, 2, "rule Fire: the Fire Rending wand");
        var weak = Rank(("Fire", 1.3), ("Cold", 1.0));
        Check.Eq(WeaponPlanner.Choose(wands, "", weak, All).WeaponId, 2, "weak to Fire: the Fire Rending wand");

        var swords = new List<WeaponCandidate>
        {
            new(10, "Sword", CombatMode.Melee, "Slash", "", false),
            new(11, "Rending Sword", CombatMode.Melee, "Slash", "Slash", false),
        };
        Check.Eq(WeaponPlanner.Choose(swords, "", Rank(("Slash", 1.2), ("Pierce", 1.0)), All).WeaponId, 11,
            "melee: the Slash Rending sword");
    }

    private static void ChoiceWandCastsOwn()
    {
        var weak = Rank(("Slash", 1.3), ("Cold", 1.0));
        var fire = new List<WeaponCandidate> { Wand(1, "Fire", inHand: true) };

        // Damage tab fixed wand + a Monsters rule of another element: the wand's element.
        var fixedWand = WeaponPlanner.Choose(fire, "Acid", weak, All, fixedWeaponId: 1);
        Check.Eq(fixedWand.WeaponId, 1, "the Damage tab's wand");
        Check.Eq(fixedWand.Element, "Fire", "casts the wand's Fire, not the rule's Acid");

        // Auto, no listed wand has any element of the weakness: the wand in hand casts its own.
        var noMatch = WeaponPlanner.Choose(fire, "", Rank(("Slash", 1.3), ("Cold", 1.1)), All);
        Check.Eq(noMatch.Element, "Fire", "weak to Slash/Cold, only a Fire wand: casts Fire (not Blades)");

        // A rended wand with no other element: its rending is its element.
        var rended = new List<WeaponCandidate> { new(5, "Wand", CombatMode.Magic, "Cold", "Cold", true) };
        Check.Eq(WeaponPlanner.Choose(rended, "Acid", weak, All).Element, "Cold", "a Cold-rended wand casts Cold");

        // The character can't cast the wand's element: fall back to the rule.
        Check.Eq(WeaponPlanner.Choose(fire, "Acid", weak, e => e != "Fire").Element, "Acid",
            "can't cast Fire: the rule's Acid");
    }

    private static void ChoiceCastable()
    {
        var wands = new List<WeaponCandidate> { Wand(1, "Fire"), Wand(2, "Cold") };
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var p = WeaponPlanner.Choose(wands, "", weak, e => e != "Fire");
        Check.Eq(p.WeaponId, 2, "can't cast Fire: the Cold wand");
        Check.Eq(p.Element, "Cold", "casts Cold");

        // Only a wand whose element can't be cast (a Nether wand without Void Magic): keep it,
        // but cast the weakest element the character can, not the wand's uncastable Nether.
        var nether = new List<WeaponCandidate> { Wand(4, "Nether", inHand: true) };
        var n = WeaponPlanner.Choose(nether, "", Rank(("Nether", 1.5), ("Fire", 1.2), ("Cold", 0.8)), e => e != "Nether");
        Check.Eq(n.WeaponId, 4, "uncastable wand: keep the one in hand");
        Check.Eq(n.Element, "Fire", "and cast the weakest castable element (Fire), not Nether");
    }

    // ── Slayers (WeaponPlanner step 2) ────────────────────────────────────────

    private const int Olthoi = 1, Undead = 14;

    private static WeaponCandidate Slayer(int id, int kind, string elem, int type, double bonus, bool inHand = false, string rending = "")
        => new(id, "Slayer " + id, kind, elem, rending, inHand, type, bonus);

    private static void SlayerBeatsWeaknessAndRule()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0), ("Lightning", 0.5));
        var list = new List<WeaponCandidate> { Wand(1, "Fire", inHand: true), Slayer(2, CombatMode.Magic, "Lightning", Olthoi, 2.0) };

        var auto = WeaponPlanner.Choose(list, "", weak, All, creatureType: Olthoi);
        Check.Eq(auto.WeaponId, 2, "Auto: the Olthoi slayer, though the monster is weak to the Fire wand in hand");
        Check.Eq(auto.Element, "Lightning", "the slayer wand casts its own element");
        Check.Eq(auto.Source, "slayer (Olthoi x2)", "source names the type and the bonus");

        var rule = WeaponPlanner.Choose(list, "Fire", weak, All, creatureType: Olthoi);
        Check.Eq(rule.WeaponId, 2, "a Monsters rule of Fire: still the slayer");
        Check.True(rule.Source.StartsWith("slayer"), "source is the slayer");

        // A learned best and the weapon in hand lose to it too.
        var learned = WeaponPlanner.Choose(list, "", null, All, learnedBestId: 1, creatureType: Olthoi);
        Check.Eq(learned.WeaponId, 2, "weakness unknown, learned best and in hand: the slayer");
    }

    private static void SlayerVsDamageTab()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate> { Wand(1, "Fire"), Slayer(2, CombatMode.Magic, "Cold", Olthoi, 2.0) };

        var pick = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 1, creatureType: Olthoi);
        Check.Eq(pick.WeaponId, 1, "a non-slayer set on this monster's own Damage tab row: the player's pick wins");
        Check.Eq(pick.Source, "Damage tab", "source");
        Check.Eq(pick.Element, "Fire", "and casts its own element");

        var def = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 1, fixedSource: "Damage tab default",
            creatureType: Olthoi, fixedIsDefault: true);
        Check.Eq(def.WeaponId, 2, "the DEFAULT row's non-slayer weapon: a slayer of this monster still wins");
        Check.Eq(def.Source, "slayer (Olthoi x2)", "source");

        var defRule = WeaponPlanner.Choose(list, "Fire", weak, All, fixedWeaponId: 1, fixedSource: "Damage tab default",
            creatureType: Olthoi, fixedIsDefault: true);
        Check.Eq(defRule.WeaponId, 2, "the DEFAULT row's weapon and a Monsters rule of Fire: still the slayer");

        var defNoSlayer = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 1, fixedSource: "Damage tab default",
            creatureType: Undead, fixedIsDefault: true);
        Check.Eq(defNoSlayer.WeaponId, 1, "no matching slayer: the default still beats the weakness");
        Check.Eq(defNoSlayer.Source, "Damage tab default", "source");
    }

    private static void SlayerBeatsLowerResist()
    {
        // Owner, 2026-10-05: an Olthoi slayer that does Slash is picked before a Pierce weapon
        // even when the Olthoi resists Pierce less, because the slayer outperforms it.
        var weak = Rank(("Pierce", 0.86), ("Slash", 0.67), ("Fire", 0.65));
        var spear = new WeaponCandidate(1, "Spear", CombatMode.Melee, "Pierce", "", true);
        var list = new List<WeaponCandidate> { spear, Slayer(2, CombatMode.Melee, "Slash", Olthoi, 2.0) };

        var p = WeaponPlanner.Choose(list, "", weak, All, creatureType: Olthoi);
        Check.Eq(p.WeaponId, 2, "the Slash Olthoi slayer, not the Pierce spear in hand");
        Check.Eq(p.Element, "Slash", "it hits with Slash");
        Check.Eq(p.Source, "slayer (Olthoi x2)", "source");

        var r = WeaponPlanner.Choose(list, "Pierce", weak, All, creatureType: Olthoi);
        Check.Eq(r.WeaponId, 2, "a Monsters rule of Pierce: still the slayer");

        Check.Eq(WeaponPlanner.Choose(list, "", weak, All, creatureType: Undead).WeaponId, 1,
            "not an Olthoi: the Pierce spear (weakness)");
    }

    private static void SlayerManualPickIsSlayer()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate>
        {
            Wand(1, "Fire"),
            Slayer(2, CombatMode.Magic, "Acid", Olthoi, 3.0),
            Slayer(3, CombatMode.Magic, "Cold", Olthoi, 2.0),
        };
        var p = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 3, creatureType: Olthoi);
        Check.Eq(p.WeaponId, 3, "this monster's own pick is a slayer: that one, though another has a bigger bonus");
        Check.Eq(p.Element, "Cold", "its own element");
        Check.Eq(p.Source, "Damage tab, slayer (Olthoi x2)", "source names the pick and the slayer");

        // The DEFAULT row's pick is a slayer: step 2 finds it like any listed slayer (biggest bonus first).
        var d = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 3, fixedSource: "Damage tab default",
            creatureType: Olthoi, fixedIsDefault: true);
        Check.Eq(d.WeaponId, 2, "DEFAULT row's slayer pick: the best slayer of the monster (x3)");
    }

    private static void SlayerBeatsLearnedBest()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate> { Wand(1, "Fire"), Slayer(2, CombatMode.Magic, "Cold", Olthoi, 2.0) };
        Check.Eq(WeaponPlanner.Choose(list, "", weak, All, learnedBestId: 1, creatureType: Olthoi).WeaponId, 2,
            "weakness known, the learned best is the Fire wand: the slayer");
        Check.Eq(WeaponPlanner.Choose(list, "", null, All, learnedBestId: 1, creatureType: Olthoi).WeaponId, 2,
            "weakness unknown, learned best: the slayer");
        Check.Eq(WeaponPlanner.Choose(list, "", null, All, learnedBestId: 1, creatureType: 0).WeaponId, 1,
            "creature type unknown: the learned best, as before");

        // Through CombatManager: the store's learned best (GetBestWeapon) doesn't beat a listed slayer.
        var rig = new Rig(990002, "Olthoi Learner");
        rig.S.ItemRules.Add(new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" });
        for (int i = 0; i < 4; i++) rig.Store.RecordHit(FireWand, 990002, "Olthoi Learner", "Fire", 7, 300, false);
        Check.Eq(rig.Store.GetBestWeapon(990002), FireWand, "the Damage tab learned the fire wand as best");
        rig.C.InHandOverride = () => unchecked((int)FireWand);
        FakeHost.Ints[(ListedWand, 166)] = Olthoi;
        FakeHost.Ints[(Monster, 2)] = Olthoi;
        Check.Eq(unchecked((uint)rig.C.PlanFor(rig.Target, null).WeaponId), ListedWand, "the slayer wand, not the learned best");
    }

    private static void SlayerBonusUnknown()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var unknown = Slayer(2, CombatMode.Magic, "Cold", Olthoi, 0);
        Check.True(WeaponPlanner.Slays(unknown, Olthoi), "SlayerCreatureType sent, bonus not: it slays");
        Check.False(WeaponPlanner.Slays(unknown, Undead), "not another type");
        var p = WeaponPlanner.Choose(new List<WeaponCandidate> { Wand(1, "Fire", inHand: true), unknown }, "", weak, All, creatureType: Olthoi);
        Check.Eq(p.WeaponId, 2, "the slayer with no bonus sent beats the Fire wand the monster is weak to");
        Check.Eq(p.Source, "slayer (Olthoi)", "source has no multiplier");

        var both = new List<WeaponCandidate> { unknown, Slayer(3, CombatMode.Magic, "Acid", Olthoi, 2.0) };
        Check.Eq(WeaponPlanner.Choose(both, "", weak, All, creatureType: Olthoi).WeaponId, 3,
            "a known x2 bonus ranks above one not sent");
        var two = new List<WeaponCandidate> { Slayer(4, CombatMode.Magic, "Acid", Olthoi, 0), unknown };
        Check.Eq(WeaponPlanner.Choose(two, "", weak, All, creatureType: Olthoi).WeaponId, 2,
            "two with no bonus sent: the element order (Cold before Acid)");
    }

    private static void SlayerTypeFromTable()
    {
        // wcid 3 "Olthoi Worker" is type 1 (Olthoi) in creature_resists.tsv. The monster isn't
        // appraised (no int 2) and the creature store doesn't know it: the table answers.
        var rig = new Rig(3, "Olthoi Worker");
        rig.S.ItemRules.Add(new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" });
        rig.C.InHandOverride = () => unchecked((int)FireWand);
        FakeHost.Ints[(ListedWand, 166)] = Olthoi;          // no float 138, as ACE sends it
        Check.Eq(CreatureWeakness.TableCreatureType(3, "Olthoi Worker"), Olthoi, "table: wcid 3 is an Olthoi");
        Check.Eq(CreatureWeakness.TableCreatureType(3, "Not An Olthoi"), 0, "a custom monster reusing the wcid: no type");
        Check.Eq(rig.C.CreatureTypeOf(3, "Olthoi Worker"), Olthoi, "CreatureTypeOf falls back to the table");
        var p = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "first sight of an Olthoi Worker: the slayer wand");
        Check.Eq(p.Source, "slayer (Olthoi)", "source");
        Check.True(FakeHost.Logs.Any(l => l.Contains("slays Olthoi (bonus not sent)")), "the weapon's slayer is logged");

        // A listed slayer and a monster of unknown type: logged once, no slayer step.
        var odd = new WorldObject(unchecked((int)Monster) + 5, "Custom Thing", AcObjectClass.Monster);
        FakeHost.Wcids[Monster + 5] = 990005;
        Check.Eq(unchecked((uint)rig.C.PlanFor(odd, null).WeaponId), FireWand, "type unknown: the weakness choice");
        Check.True(FakeHost.Logs.Any(l => l.Contains("creature type not known yet")), "and it is logged");
    }

    private static void SlayerNonMatching()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate>
        {
            Wand(1, "Fire"),
            Slayer(2, CombatMode.Magic, "Cold", Undead, 2.0),      // another type
            Slayer(4, CombatMode.Magic, "Cold", Olthoi, 1.0),      // x1: no gain
            Slayer(5, CombatMode.Melee, "Slash", Olthoi, 3.0),     // not the main kind (magic)
        };
        var p = WeaponPlanner.Choose(list, "", weak, All, creatureType: Olthoi);
        Check.Eq(p.WeaponId, 1, "none of them slays this Olthoi for a mage: weak to Fire, the Fire wand");
        Check.Eq(p.Source, "weak to Fire (test)", "source");
        Check.False(WeaponPlanner.Slays(list[1], Olthoi), "an Undead slayer doesn't slay an Olthoi");
        Check.True(WeaponPlanner.Slays(list[1], Undead), "it slays Undead");
        Check.False(WeaponPlanner.Slays(list[1], 0), "type 0 (unknown) never matches");
    }

    private static void SlayerOrder()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0), ("Acid", 0.6));
        var bigger = new List<WeaponCandidate>
        {
            Slayer(1, CombatMode.Magic, "Fire", Olthoi, 1.5),
            Slayer(2, CombatMode.Magic, "Acid", Olthoi, 3.0),
        };
        var p = WeaponPlanner.Choose(bigger, "", weak, All, creatureType: Olthoi);
        Check.Eq(p.WeaponId, 2, "x3 beats x1.5, though Fire is the weakness");
        Check.Eq(p.Source, "slayer (Olthoi x3)", "source");

        var same = new List<WeaponCandidate>
        {
            Slayer(1, CombatMode.Magic, "Acid", Olthoi, 2.0, inHand: true),
            Slayer(2, CombatMode.Magic, "Fire", Olthoi, 2.0),
            Slayer(3, CombatMode.Magic, "Cold", Olthoi, 2.0),
        };
        Check.Eq(WeaponPlanner.Choose(same, "", weak, All, creatureType: Olthoi).WeaponId, 2,
            "same bonus: the weakest element (Fire), not the Acid one in hand");
        Check.Eq(WeaponPlanner.Choose(same, "Cold", weak, All, creatureType: Olthoi).WeaponId, 3,
            "same bonus and a rule of Cold: the Cold one");
        Check.Eq(WeaponPlanner.Choose(same, "", weak, e => e != "Fire", creatureType: Olthoi).WeaponId, 3,
            "can't cast Fire: Cold, the next weakest");

        var hand = new List<WeaponCandidate>
        {
            Slayer(1, CombatMode.Melee, "Slash", Olthoi, 2.0),
            Slayer(2, CombatMode.Melee, "Slash", Olthoi, 2.0, inHand: true),
            Slayer(3, CombatMode.Melee, "Slash", Olthoi, 2.0, rending: "Slash"),
        };
        Check.Eq(WeaponPlanner.Choose(hand, "", null, All, creatureType: Olthoi).WeaponId, 3,
            "same bonus and element: the matching rending");
        hand.RemoveAt(2);
        Check.Eq(WeaponPlanner.Choose(hand, "", null, All, creatureType: Olthoi).WeaponId, 2,
            "then the one in hand (no needless swap)");
    }

    private static void SlayerUnknownType()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0));
        var list = new List<WeaponCandidate> { Wand(1, "Fire"), Slayer(2, CombatMode.Magic, "Cold", Olthoi, 2.0, inHand: true) };
        var p = WeaponPlanner.Choose(list, "", weak, All);
        Check.Eq(p.WeaponId, 1, "creature type unknown: the weakness choice (Fire wand)");
        Check.Eq(p.Source, "weak to Fire (test)", "source");
        var r = WeaponPlanner.Choose(list, "Cold", weak, All, creatureType: 0);
        Check.Eq(r.Source, "Monsters rule Cold", "and the rule as before");
        var d = WeaponPlanner.Choose(list, "", weak, All, fixedWeaponId: 1, fixedSource: "Damage tab default", creatureType: 0, fixedIsDefault: true);
        Check.Eq(d.WeaponId, 1, "type unknown and a DEFAULT row weapon: the default, as before");
        Check.Eq(d.Source, "Damage tab default", "source");
    }

    private static void SlayerElement()
    {
        var weak = Rank(("Fire", 1.4), ("Cold", 1.0), ("Slash", 0.8));

        var sword = new List<WeaponCandidate> { new(1, "Sword", CombatMode.Melee, "Fire", "", true), Slayer(2, CombatMode.Melee, "Slash", Olthoi, 2.0) };
        var m = WeaponPlanner.Choose(sword, "Cold", weak, All, creatureType: Olthoi);
        Check.Eq(m.WeaponId, 2, "the Slash slayer sword");
        Check.Eq(m.Element, "Slash", "its own element (what it hits with), not the rule's Cold");

        var blank = new List<WeaponCandidate> { Slayer(3, CombatMode.Magic, "", Olthoi, 2.0) };
        Check.Eq(WeaponPlanner.Choose(blank, "", weak, All, creatureType: Olthoi).Element, "Fire",
            "a slayer wand with no element casts the weakest element");
        Check.Eq(WeaponPlanner.Choose(blank, "Cold", weak, All, creatureType: Olthoi).Element, "Cold",
            "or the rule's element");

        var nether = new List<WeaponCandidate> { Slayer(4, CombatMode.Magic, "Nether", Olthoi, 2.0) };
        var n = WeaponPlanner.Choose(nether, "", weak, e => e != "Nether", creatureType: Olthoi);
        Check.Eq(n.WeaponId, 4, "a Nether slayer wand without Void Magic still slays (any war spell from it)");
        Check.Eq(n.Element, "Fire", "and casts the weakest element the character can");
    }

    private static void SlayerFromHost()
    {
        // Olthoi weakness (creature_types.txt): Fire 0.65 beats Lightning 0.35, so without the
        // slayer the Fire wand wins; the listed Lightning wand slays Olthoi x2.
        var rig = new Rig(990001, "Olthoi Testling");
        rig.S.ItemRules.Add(new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" });
        rig.C.InHandOverride = () => unchecked((int)FireWand);

        var before = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)before.WeaponId), FireWand, "no slayer properties yet: the Fire wand (weakness)");
        Check.Eq(rig.C.SlayerOf(unchecked((int)ListedWand)), (0, 0.0), "no int 166: no slayer");
        FakeHost.Ints[(ListedWand, 166)] = Olthoi;
        Check.Eq(rig.C.SlayerOf(unchecked((int)ListedWand)), (Olthoi, 0.0), "int 166 alone (what ACE sends): a slayer, bonus not known");

        FakeHost.Ints[(ListedWand, 166)] = Olthoi;
        FakeHost.Doubles[(ListedWand, 138)] = 2.0;
        var cands = rig.C.ListedCandidates();
        var lw = cands.First(c => c.Id == unchecked((int)ListedWand));
        Check.Eq(lw.SlayerType, Olthoi, "int 166 read");
        Check.Near(lw.SlayerBonus, 2.0, 1e-9, "float 138 read");

        var noType = new WorldObject(unchecked((int)Monster) + 1, "Olthoi Testling", AcObjectClass.Monster);
        FakeHost.Wcids[Monster + 1] = 990001;
        Check.Eq(unchecked((uint)rig.C.PlanFor(noType, null).WeaponId), FireWand,
            "the monster isn't appraised (no int 2): no guess from the name, the weakness choice");

        FakeHost.Ints[(Monster, 2)] = Olthoi;
        var target = new WorldObject(unchecked((int)Monster), "Olthoi Testling", AcObjectClass.Monster);
        var p = rig.C.PlanFor(target, null);
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "appraised Olthoi: the slayer wand");
        Check.Eq(p.Source, "slayer (Olthoi x2)", "source");

        // The next spawn of the same wcid: known at once, before its own appraisal.
        var next = new WorldObject(unchecked((int)Monster) + 2, "Olthoi Testling", AcObjectClass.Monster);
        FakeHost.Wcids[Monster + 2] = 990001;
        Check.Eq(unchecked((uint)rig.C.PlanFor(next, null).WeaponId), ListedWand, "the next spawn: cached by wcid");
        Check.Eq(rig.C.CreatureTypeOf(990001, "Olthoi Testling"), Olthoi, "cache");
    }

    // ── Listed weapons only ───────────────────────────────────────────────────

    private const uint ListedWand = 0x60000061, FireWand = 0x60000062, NamedJunk = 0x60000063, Monster = 0x80000201;

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public readonly MonsterDamageStore Store = new();
        public WorldObject Target;

        public Rig(uint wcid, string monsterName, bool modeCalls = false)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeCalls = modeCalls;
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.Names[ListedWand] = "Wand"; FakeHost.ItemTypes[ListedWand] = 0x8000;
            FakeHost.Ints[(ListedWand, 45)] = 0x40;            // identified: Lightning
            FakeHost.Names[FireWand] = "Fire Wand"; FakeHost.ItemTypes[FireWand] = 0x8000;
            FakeHost.Ints[(FireWand, 18)] = 0x20;              // Fire glow
            FakeHost.Names[NamedJunk] = "Fire Wand"; FakeHost.ItemTypes[NamedJunk] = 0x8;   // jewelry named like a wand
            FakeHost.Wcids[Monster] = wcid;
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, new[] { ListedWand, FireWand, NamedJunk });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            C.CanCastOverride = e => e != "Nether";            // a War mage without Void
            Target = new WorldObject(unchecked((int)Monster), monsterName, AcObjectClass.Monster);
            S.ItemRules = new List<ItemRule> { new() { Id = unchecked((int)ListedWand), Name = "Wand", Element = "Lightning", ElementSource = "properties" } };
        }
    }

    // 2026-10-14: a sold Flaming Greataxe, still listed and still known to the client, was the weapon
    // chosen for the monster; three wield tries over 13 s with the character standing in the fight.
    private static void SoldWeaponSkipped()
    {
        const uint Vendor = 0x80099999;
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        FakeHost.Names[ListedWand] = "Wand"; FakeHost.ItemTypes[ListedWand] = 0x8000;
        FakeHost.Ints[(ListedWand, 45)] = 0x40;            // Lightning
        FakeHost.Names[FireWand] = "Fire Wand"; FakeHost.ItemTypes[FireWand] = 0x8000;
        FakeHost.Ints[(FireWand, 18)] = 0x20;              // Fire
        FakeHost.Wcids[Monster] = 1535;
        FakeHost.Containers[ListedWand] = Player;          // ours
        FakeHost.Containers[FireWand] = Vendor;            // sold: the vendor has it now
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { ListedWand, FireWand });
        var s = new LegacyUiSettings();
        var store = new MonsterDamageStore();
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        c.SetDamageStores(null, store);
        c.CanCastOverride = e => e != "Nether";
        s.ItemRules = new List<ItemRule>
        {
            new() { Id = unchecked((int)ListedWand), Name = "Wand", Element = "Lightning", ElementSource = "properties" },
            new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "properties" },
        };
        store.SetManualWeapon(1535, FireWand);             // the Damage tab's pick for this monster
        var target = new WorldObject(unchecked((int)Monster), "Ethereal Wisp", AcObjectClass.Monster);

        var plan = c.PlanFor(target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), ListedWand, "the carried wand, not the sold Damage tab pick");
        Check.True(FakeHost.Logs.Any(l => l.Contains("isn't in your pack")), "the skip is logged: " + string.Join(" | ", FakeHost.Logs.Take(10)));
        Check.Eq(c.ListedCandidates().Count, 1, "the sold weapon isn't a candidate at all");

        FakeHost.Containers[FireWand] = Player;            // bought back
        Check.Eq(c.ListedCandidates().Count, 2, "back in the pack: a candidate again");
    }

    private static void MissingFromPack()
    {
        // 2026-10-11: Drakkon's buff wand left his pack and the Items slot still just said "Wand".
        const int InPack = 0x7000A001, InHand = 0x7000A002, Gone = 0x7000A003, Unknown = 0x7000A004;
        var known = new Dictionary<int, WorldObject>
        {
            [InPack] = new WorldObject(InPack, "Silver Wand", AcObjectClass.WandStaffOrb),
            [InHand] = new WorldObject(InHand, "Ken", AcObjectClass.MeleeWeapon),
            [Gone] = new WorldObject(Gone, "Wand", AcObjectClass.WandStaffOrb),   // the client still knows it
        };
        WorldObject? Look(int id) => known.TryGetValue(id, out var wo) ? wo : null;
        var rules = new List<ItemRule>
        {
            new() { Id = InPack, Name = "Silver Wand" },
            new() { Id = InHand, Name = "Ken" },
            new() { Id = Gone, Name = "Wand" },
            new() { Id = Unknown, Name = "Old Bow" },
            new() { Id = 0, Name = "never resolved" },
        };
        var pack = new[] { known[InPack], new WorldObject(0x7000A0FF, "Pyreal", AcObjectClass.Money) };
        var missing = WeaponList.MissingIds(rules, pack, Look, wo => wo.Id == InHand);
        Check.True(!missing.Contains(InPack), "in the pack: carried");
        Check.True(!missing.Contains(InHand), "wielded (not in the pack list): carried");
        Check.True(missing.Contains(Gone), "left the pack, the client still knows it: missing");
        Check.True(missing.Contains(Unknown), "the client doesn't know it at all: missing");
        Check.Eq(missing.Count, 2, "only those two (id 0 is never reported)");

        missing = WeaponList.MissingIds(rules, Array.Empty<WorldObject>(), Look, _ => false);
        Check.Eq(missing.Count, 0, "inventory not known yet (empty, logging in): nothing reported missing");
    }

    private static void StanceRefusedKeepsBackoff()
    {
        var rig = new Rig(1535, "Ethereal Wisp", modeCalls: true);
        rig.C.InHandOverride = () => unchecked((int)ListedWand);   // the wand is in hand
        FakeHost.CombatModeValue = CombatMode.NonCombat;           // but the stance is not Magic yet
        FakeHost.Ints[(ListedWand, 10)] = 0x01000000;               // CurrentWieldedLocation: reads wielded (the stuck-clock branch)
        var equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var last = typeof(CombatManager).GetField("lastStanceAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stuck = typeof(CombatManager).GetField("_stanceStuckSince", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FakeHost.CombatModeRefuse = true;
        for (int i = 0; i < 5; i++) equip.Invoke(rig.C, new object[] { rig.Target, "Auto" });
        Check.Eq(FakeHost.CombatModeRefusals, 5, "every pass asks again: a refusal sets no backoff");
        Check.True((DateTime)last.GetValue(rig.C)! == DateTime.MinValue, "lastStanceAttempt stays put");
        Check.True((DateTime)stuck.GetValue(rig.C)! == DateTime.MinValue, "the stuck clock has not started");
        Check.Eq(FakeHost.Uses.Count, 0, "no re-equip UseObject");
        FakeHost.CombatModeRefuse = false;
        equip.Invoke(rig.C, new object[] { rig.Target, "Auto" });
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), CombatMode.Magic.ToString(), "accepted: the request goes out");
        Check.True((DateTime)last.GetValue(rig.C)! != DateTime.MinValue, "and only now lastStanceAttempt is stamped");
    }
    /// <summary>
    /// Problem report R-20261022-193411-fb72 (2026-10-22): "ignoring monster rules, specifically
    /// which weapon is set for the mob type". The rule's WeaponId was imported and shown but never
    /// read; the weakness picked the weapon ("src=weak to Cold"). Ethereal Wisp's weakness picks the
    /// Lightning wand; the rule says the fire wand. Each step uses a new rule object, so PlanFor's
    /// cache (keyed on the rule by reference) doesn't hand back the previous answer.
    /// </summary>
    // Conquest, 2026-10-29: "not swapping 0x80181B46 'Frost Sceptre' for 0x8024231C 'Acid Staff' yet
    // (target at 23% health)", then Incantation of Acid Blast cast through the Frost Sceptre.
    private static void HeldWandCastsItsElement()
    {
        var rig = new Rig(1535, "Ethereal Wisp");
        rig.S.ItemRules.Add(new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" });
        var rule = new MonsterRule { Name = "Ethereal" };   // damage type Auto
        var pref = typeof(CombatManager).GetMethod("GetPreferredElement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string Elem() => (string)pref.Invoke(rig.C, new object?[] { rig.Target, rule })!;

        var plan = rig.C.PlanFor(rig.Target, rule);
        Check.Eq(unchecked((uint)plan.WeaponId), ListedWand, "the plan: the Lightning wand (the weakness)");

        rig.C.InHandOverride = () => unchecked((int)ListedWand);
        Check.Eq(Elem(), "Lightning", "the plan's wand in hand: its element");

        rig.C.InHandOverride = () => unchecked((int)FireWand);   // the swap was held: the fire wand stays
        Check.Eq(Elem(), "Fire", "the fire wand in hand casts Fire, not the plan's Lightning");
        Check.True(FakeHost.Logs.Count(l => l.Contains("isn't in hand: casting Fire")) == 1, "logged once");
        Elem();
        Check.True(FakeHost.Logs.Count(l => l.Contains("isn't in hand: casting Fire")) == 1, "still once");

        rig.C.CanCastOverride = e => e != "Nether" && e != "Fire";   // a wand whose element can't be cast
        Check.Eq(Elem(), "Lightning", "an element the character can't cast: the plan's element");

        rig.C.InHandOverride = () => 0;                               // nothing readable in hand
        rig.C.CanCastOverride = e => e != "Nether";
        Check.Eq(Elem(), "Lightning", "nothing in hand: the plan's element");
    }

    private static void MonsterRuleWeapon()
    {
        var rig = new Rig(1535, "Ethereal Wisp");
        rig.S.ItemRules.Add(new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" });
        rig.S.EnsureDefaultRule();
        var defRow = rig.S.MonsterRules[0];
        MonsterRule Own(uint weapon) => new() { Name = "Ethereal", WeaponId = unchecked((int)weapon) };

        var auto = rig.C.PlanFor(rig.Target, Own(0));
        Check.Eq(unchecked((uint)auto.WeaponId), ListedWand, "no weapon on the rule: the weakness picks the Lightning wand");

        var p = rig.C.PlanFor(rig.Target, Own(FireWand));
        Check.Eq(unchecked((uint)p.WeaponId), FireWand, "the rule's weapon, not the weakness pick");
        Check.Eq(p.Source, "Monsters rule weapon", "source");
        Check.Eq(p.Element, "Fire", "it casts its own element");

        // This monster's own Damage tab pick beats the rule's weapon.
        rig.Store.SetManualWeapon(1535, ListedWand);
        p = rig.C.PlanFor(rig.Target, Own(FireWand));
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "the Damage tab pick for this monster wins over the rule's");
        Check.Eq(p.Source, "Damage tab", "source");
        rig.Store.SetManualWeapon(1535, 0);

        // The rule's weapon beats the Damage tab DEFAULT row's.
        rig.Store.SetDefaultWeapon(ListedWand);
        p = rig.C.PlanFor(rig.Target, Own(FireWand));
        Check.Eq(unchecked((uint)p.WeaponId), FireWand, "the rule's weapon wins over the Damage tab default");
        p = rig.C.PlanFor(rig.Target, Own(0));
        Check.Eq(p.Source, "Damage tab default", "no weapon on the rule: the Damage tab default");

        // The Monsters Default row's weapon comes after the Damage tab default.
        defRow.WeaponId = unchecked((int)FireWand);
        p = rig.C.PlanFor(rig.Target, defRow);
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "Default row matched: the Damage tab default beats its weapon");
        Check.Eq(p.Source, "Damage tab default", "source");
        rig.Store.SetDefaultWeapon(0);
        p = rig.C.PlanFor(rig.Target, Own(0));
        Check.Eq(unchecked((uint)p.WeaponId), FireWand, "no Damage tab weapons: the Monsters Default row's weapon");
        Check.Eq(p.Source, "Monsters Default weapon", "source");

        // A slayer beats the Default row's weapon (a DEFAULT pick, step 3), not the rule's own (step 1).
        FakeHost.Ints[(ListedWand, 166)] = Olthoi;
        var olthoi = new WorldObject(unchecked((int)Monster) + 7, "Ethereal Wisp", AcObjectClass.Monster);
        FakeHost.Wcids[Monster + 7] = 990007;
        FakeHost.Ints[(Monster + 7, 2)] = Olthoi;
        p = rig.C.PlanFor(olthoi, defRow);
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "the Default row's fire wand loses to an Olthoi slayer");
        Check.True(p.Source.StartsWith("slayer"), "source: " + p.Source);
        p = rig.C.PlanFor(olthoi, Own(FireWand));
        Check.Eq(unchecked((uint)p.WeaponId), FireWand, "the rule's own fire wand beats the slayer, like a Damage tab pick");
        FakeHost.Ints.Remove((ListedWand, 166));
        defRow.WeaponId = 0;

        // The monster's wcid isn't known: the rule's weapon still applies.
        var noWcid = new WorldObject(unchecked((int)Monster) + 9, "Ethereal Wisp", AcObjectClass.Monster);
        p = rig.C.PlanFor(noWcid, Own(FireWand));
        Check.Eq(unchecked((uint)p.WeaponId), FireWand, "wcid unknown: the rule's weapon");

        // Not in the Items list: still used, the Monsters tab pick is the player's (owner, 2026-10-22).
        rig.S.ItemRules.RemoveAll(r => r.Id == unchecked((int)FireWand));
        p = rig.C.PlanFor(rig.Target, Own(FireWand));
        Check.Eq(unchecked((uint)p.WeaponId), FireWand, "an unlisted rule weapon is still used");
        Check.Eq(p.Source, "Monsters rule weapon", "source");
        Check.Eq(p.Element, "Fire", "it casts its own element (read from the weapon, not the list)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Monsters rule 'Ethereal' weapon") && l.Contains("isn't in the Items list") && l.Contains("using it")),
            "logged once: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("EquipDiag")).Take(6)));
        defRow.WeaponId = unchecked((int)FireWand);
        p = rig.C.PlanFor(rig.Target, Own(0));
        Check.Eq(unchecked((uint)p.WeaponId), FireWand, "the Default row's unlisted weapon too");
        defRow.WeaponId = 0;
        // ...but a Damage tab pick still must be listed.
        rig.Store.SetManualWeapon(1535, FireWand);
        p = rig.C.PlanFor(rig.Target, Own(0));
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "an unlisted Damage tab pick is still skipped");
        rig.Store.SetManualWeapon(1535, 0);
        // A rule weapon the client doesn't know (not carried, never seen): skipped, the weakness pick.
        const uint Unknown = 0x6000006F;
        p = rig.C.PlanFor(rig.Target, Own(Unknown));
        Check.Eq(unchecked((uint)p.WeaponId), ListedWand, "an unknown rule weapon is skipped: the weakness pick");
        rig.S.ItemRules.Add(new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" });

        // Through combat's equip pass: the rule matched by name drives the wield.
        rig.S.MonsterRules.Add(Own(FireWand));
        rig.C.InHandOverride = () => unchecked((int)ListedWand);   // the weakness pick is in hand
        var equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        equip.Invoke(rig.C, new object[] { rig.Target, "Auto" });
        Check.Eq(FakeHost.Uses.FirstOrDefault(), FireWand, "combat wields the rule's fire wand");

        // The same with the fire wand not in the Items list (a fresh rig: the first wield is in flight).
        var bare = new Rig(1535, "Ethereal Wisp");
        bare.S.EnsureDefaultRule();
        bare.S.MonsterRules.Add(Own(FireWand));
        bare.C.InHandOverride = () => unchecked((int)ListedWand);
        equip.Invoke(bare.C, new object[] { bare.Target, "Auto" });
        Check.Eq(FakeHost.Uses.FirstOrDefault(), FireWand, "combat wields the rule's unlisted fire wand");
    }

    private static void UnlistedNeverPicked()
    {
        var rig = new Rig(1535, "Ethereal Wisp");
        rig.Store.SetDefaultWeapon(FireWand);                  // picked on the Damage tab before the list edit
        rig.Store.SetManualWeapon(1535, FireWand);
        for (int i = 0; i < 4; i++) rig.Store.RecordHit(FireWand, 1535, "Ethereal Wisp", "Slash", 7, 300, false);
        Check.Eq(rig.Store.GetBestWeapon(1535), FireWand, "the Damage tab learned the fire wand as best");
        rig.C.InHandOverride = () => unchecked((int)FireWand);

        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), ListedWand, "the listed wand, not the Damage tab's unlisted fire wand");
        Check.True(FakeHost.Logs.Any(l => l.Contains("isn't in the Items list")), "the skipped pick is logged");

        // The wand lookups (buffing, recalls, debuffs) take listed wands only.
        var cache = FakeHost.MakeCache(FakeHost.Create(), Player, new[] { ListedWand, FireWand, NamedJunk });
        WorldObject? Look(int id) => cache[id];
        var inv = new[] { cache[unchecked((int)FireWand)]!, cache[unchecked((int)NamedJunk)]! };
        var invWithListed = new[] { cache[unchecked((int)ListedWand)]!, cache[unchecked((int)FireWand)]!, cache[unchecked((int)NamedJunk)]! };
        int got = WeaponList.FindWand(rig.S.ItemRules, Look, _ => false, invWithListed, allowUnlisted: true, out bool unlisted);
        Check.Eq(unchecked((uint)got), ListedWand, "a wand is listed and carried: that one, never another from the pack");
        Check.False(unlisted, "not from the pack");

        // 2026-10-11: the listed wand left the pack (the client still knows the object). It is
        // not returned - the server ignores a wield of it and every buff was parked.
        got = WeaponList.FindWand(rig.S.ItemRules, Look, _ => false, inv, allowUnlisted: false, out _);
        Check.Eq(got, 0, "listed wand not carried, setting off: no wand (buffs go bare-handed), not the gone one");
        got = WeaponList.FindWand(rig.S.ItemRules, Look, _ => false, inv, allowUnlisted: true, out unlisted);
        Check.Eq(unchecked((uint)got), FireWand, "listed wand not carried, setting on: a caster from the pack");
        got = WeaponList.FindWand(rig.S.ItemRules, Look, wo => wo.Id == unchecked((int)ListedWand), inv, allowUnlisted: false, out _);
        Check.Eq(unchecked((uint)got), ListedWand, "a listed wand in hand counts as carried");
        var sameName = new WorldObject(0x7000ABCD, cache[unchecked((int)ListedWand)]!.Name, AcObjectClass.WandStaffOrb);
        got = WeaponList.FindWand(rig.S.ItemRules, Look, _ => false, new[] { sameName }, allowUnlisted: false, out _);
        Check.Eq(got, 0, "listed wand gone, setting off: no same-name stand-in (it took unlisted 'Wand's, 2026-10-18)");
        got = WeaponList.FindWand(rig.S.ItemRules, Look, _ => false, new[] { sameName }, allowUnlisted: true, out unlisted);
        Check.Eq(got, 0x7000ABCD, "listed wand gone, setting on: any caster in the pack");
        Check.True(unlisted, "from the pack");
        got = WeaponList.FindWand(rig.S.ItemRules, Look, _ => false, invWithListed, allowUnlisted: true, out unlisted,
            skip: wo => wo.Id == unchecked((int)ListedWand));
        Check.Eq(got, 0, "the listed wand is carried but can't be wielded: no caster from the pack (owner, 2026-10-18)");

        var none = new List<ItemRule>();
        got = WeaponList.FindWand(none, Look, _ => false, new[] { cache[unchecked((int)NamedJunk)]! }, allowUnlisted: true, out _);
        Check.Eq(got, 0, "no wand listed: a jewel named 'Fire Wand' is never taken (no name match)");
        got = WeaponList.FindWand(none, Look, _ => false, inv, allowUnlisted: false, out _);
        Check.Eq(got, 0, "setting off: nothing from the pack");
        got = WeaponList.FindWand(none, Look, _ => false, inv, allowUnlisted: true, out unlisted);
        Check.Eq(unchecked((uint)got), FireWand, "setting on and no wand listed: a real caster from the pack");
        Check.True(unlisted, "flagged as unlisted");

        var staff = new WorldObject(0x60000064, "Quarter Staff", AcObjectClass.MeleeWeapon);
        Check.False(WeaponList.IsWand(staff), "a melee Quarter Staff is not a wand");
        Check.True(WeaponList.IsWand(new WorldObject(0x60000065, "Ivory Staff", AcObjectClass.Unknown)), "an unclassified 'Staff' still is");
    }

    // ── Swap tracker ──────────────────────────────────────────────────────────

    private static void SwapOneInFlight()
    {
        var tr = new WeaponSwapTracker();
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        Check.Eq(tr.Next(2, 1, t), WeaponSwapTracker.Step.Swap, "B wanted, A in hand: swap");
        tr.Sent(2, 1, t);
        Check.Eq(tr.Next(2, 1, t.AddMilliseconds(500)), WeaponSwapTracker.Step.Wait, "one in flight: wait");
        Check.Eq(tr.Next(3, 1, t.AddMilliseconds(600)), WeaponSwapTracker.Step.Wait, "even for another weapon");
        Check.Eq(tr.Next(2, 2, t.AddMilliseconds(900)), WeaponSwapTracker.Step.InHand, "B seen in hand: confirmed");
        Check.Eq(tr.PendingId, 0, "nothing in flight");

        // A weapon that never arrives: 3 tries, then rest and fight with what's in hand.
        var tr2 = new WeaponSwapTracker();
        for (int i = 1; i <= 3; i++)
        {
            Check.Eq(tr2.Next(5, 1, t), WeaponSwapTracker.Step.Swap, $"try {i}: swap");
            tr2.Sent(5, 1, t);
            t = t.AddMilliseconds(2600);
        }
        Check.Eq(tr2.Next(5, 1, t), WeaponSwapTracker.Step.Hold, "after 3 tries: hold, fight with what's in hand");
        Check.True(tr2.Notice != null && tr2.Notice.Contains("didn't wield after 3 tries"), "logged once");
        tr2.Notice = null;
        Check.Eq(tr2.Next(5, 1, t.AddSeconds(30)), WeaponSwapTracker.Step.Hold, "still resting 30 s later");
        Check.True(tr2.Notice == null, "not logged again");
        Check.Eq(tr2.Next(5, 1, t.AddMinutes(2.1)), WeaponSwapTracker.Step.Swap, "after the rest: try again");
    }

    private static void SwapNoFlipFlop()
    {
        var tr = new WeaponSwapTracker();
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        tr.Next(2, 1, t); tr.Sent(2, 1, t);
        tr.Next(2, 2, t.AddSeconds(1));                                           // A -> B landed
        Check.Eq(tr.Next(1, 2, t.AddSeconds(3)), WeaponSwapTracker.Step.Hold, "straight back to A: held");
        Check.Eq(tr.Next(1, 2, t.AddSeconds(19)), WeaponSwapTracker.Step.Hold, "still held inside 20 s");
        Check.Eq(tr.Next(1, 2, t.AddSeconds(22)), WeaponSwapTracker.Step.Swap, "after 20 s it may go back");

        // Many swaps in a minute: held.
        var tr2 = new WeaponSwapTracker();
        int hand = 1;
        for (int i = 0; i < 4; i++)
        {
            int want = 10 + i;
            t = t.AddSeconds(5);
            tr2.Next(want, hand, t); tr2.Sent(want, hand, t);
            tr2.Next(want, want, t.AddSeconds(1));
            hand = want;
        }
        Check.Eq(tr2.Next(99, hand, t.AddSeconds(5)), WeaponSwapTracker.Step.Hold, "a fifth swap inside a minute: held");
    }

    // Drakkon 2026-10-12 (0x01C5): Tetsubo -> Lightning Quadrelle for a Giant Maggot; 16 s later
    // the next target, a Noble Grub weak to Bludgeon, was fought with the Quadrelle ("keeping ...
    // just swapped away from it") until the 20 s dwell ran out, 5.3 s after the lock.
    private static void SwapBackForNewTarget()
    {
        const int Tetsubo = 1, Quadrelle = 2, Maggot = 0x7001, Grub = 0x7002;
        var tr = new WeaponSwapTracker();
        DateTime t = new(2026, 10, 12, 20, 35, 8);
        Check.Eq(tr.Next(Quadrelle, Tetsubo, t, forKey: Maggot), WeaponSwapTracker.Step.Swap, "maggot: Tetsubo -> Quadrelle");
        tr.Sent(Quadrelle, Tetsubo, t);
        tr.Next(Quadrelle, Quadrelle, t.AddSeconds(1), forKey: Maggot);              // landed
        Check.Eq(tr.Next(Tetsubo, Quadrelle, t.AddSeconds(16), forKey: Grub), WeaponSwapTracker.Step.Swap,
            "the grub (a new target) gets the Tetsubo back at once");
        tr.Sent(Tetsubo, Quadrelle, t.AddSeconds(16));
        tr.Next(Tetsubo, Tetsubo, t.AddSeconds(17), forKey: Grub);                  // landed

        // The same target asking to flip back is still held (weakness data flickering mid-fight).
        Check.Eq(tr.Next(Quadrelle, Tetsubo, t.AddSeconds(19), forKey: Grub), WeaponSwapTracker.Step.Hold,
            "same target flipping straight back: held");
        Check.Eq(tr.HoldReason, "just swapped away from it", "the reason");
        // No target given (old callers): the old rule.
        Check.Eq(tr.Next(Quadrelle, Tetsubo, t.AddSeconds(20)), WeaponSwapTracker.Step.Hold, "no key: held as before");
        // A third target: free to swap.
        Check.Eq(tr.Next(Quadrelle, Tetsubo, t.AddSeconds(20), forKey: 0x7003), WeaponSwapTracker.Step.Swap, "another new target: swap");

        // The window cap still holds a fight that alternates between two kinds of monster.
        var tr2 = new WeaponSwapTracker();
        DateTime u = new(2026, 10, 12, 21, 0, 0);
        int hand = Tetsubo;
        for (int i = 0; i < 4; i++)
        {
            int want = hand == Tetsubo ? Quadrelle : Tetsubo;
            u = u.AddSeconds(5);
            Check.Eq(tr2.Next(want, hand, u, forKey: 0x8000 + i), WeaponSwapTracker.Step.Swap, $"alternating target {i}: swap");
            tr2.Sent(want, hand, u);
            tr2.Next(want, want, u.AddSeconds(1), forKey: 0x8000 + i);
            hand = want;
        }
        int next = hand == Tetsubo ? Quadrelle : Tetsubo;
        Check.Eq(tr2.Next(next, hand, u.AddSeconds(5), forKey: 0x8010), WeaponSwapTracker.Step.Hold, "a fifth swap inside a minute: held");
    }

    /// <summary>
    /// Kelpie 2026-10-14 11:41:06-08: Fire -> Piercing sent, the target changed and the plan
    /// wanted Fire again while Piercing reached the hand. The landing must count, so the swap
    /// straight back is held (before: "not seen in hand", no landing, swapped back 2.5 s later).
    /// </summary>
    private static void SwapLandsWhenChoiceMoved()
    {
        var tr = new WeaponSwapTracker();
        DateTime t = new(2026, 10, 14, 11, 41, 6);
        const int fire = 1, piercing = 2;
        Check.Eq(tr.Next(piercing, fire, t), WeaponSwapTracker.Step.Swap, "Piercing chosen: swap");
        tr.Sent(piercing, fire, t);
        Check.Eq(tr.Next(fire, piercing, t.AddMilliseconds(2500)), WeaponSwapTracker.Step.Hold,
            "Piercing landed while Fire is wanted again: the swap back is held");
        Check.Eq(tr.PendingId, 0, "nothing in flight once it landed");
        Check.Eq(tr.TriesFor(piercing), 0, "the landed weapon's tries are cleared");
        Check.Eq(tr.Notice, null, "no give-up notice: it didn't fail");

        // A third weapon is not the one just swapped away from: allowed.
        Check.Eq(tr.Next(3, piercing, t.AddMilliseconds(3000)), WeaponSwapTracker.Step.Swap, "another weapon may still swap");
    }

    /// <summary>WeaponPlanner.KeepInHand: a weakness swap must beat the hand by the min gain.</summary>
    private static void ChoiceMinSwapGain()
    {
        var bows = new List<WeaponCandidate>
        {
            new(1, "Fire Bow", CombatMode.Missile, "Fire", "", InHand: true),
            new(2, "Cold Bow", CombatMode.Missile, "Cold", "", InHand: false),
        };
        var close = Rank(("Cold", 1.1), ("Fire", 1.0));
        var off = WeaponPlanner.Choose(bows, "", close, All);
        Check.Eq(off.WeaponId, 2, "threshold off: the weakest element wins as before");
        var keep = WeaponPlanner.Choose(bows, "", close, All, minSwapGain: 0.2);
        Check.Eq(keep.WeaponId, 1, "Cold only 10% better than Fire in hand: keep the Fire bow");
        Check.Eq(keep.Element, "Fire", "and its element");
        Check.True(keep.Source.Contains("within 20%"), "source says why: " + keep.Source);

        var far = Rank(("Cold", 1.5), ("Fire", 1.0));
        Check.Eq(WeaponPlanner.Choose(bows, "", far, All, minSwapGain: 0.2).WeaponId, 2, "Cold 50% better: swap");

        var learned = new CreatureWeakness.Ranking { Source = "learned", Order = { ("Cold", double.NaN), ("Fire", double.NaN) } };
        Check.Eq(WeaponPlanner.Choose(bows, "", learned, All, minSwapGain: 0.2).WeaponId, 2, "order only (learned): no threshold, swap");

        var immune = Rank(("Cold", 1.0), ("Fire", 0.0));
        Check.Eq(WeaponPlanner.Choose(bows, "", immune, All, minSwapGain: 0.5).WeaponId, 2, "the hand does nothing to it: swap");

        // An explicit Monsters rule element is never held back.
        Check.Eq(WeaponPlanner.Choose(bows, "Cold", close, All, minSwapGain: 0.5).WeaponId, 2, "the rule's Cold: swap");
    }

    private static void SwapRefused()
    {
        var tr = new WeaponSwapTracker { MaxTries = 2 };
        DateTime t = new(2026, 9, 30, 12, 0, 0);
        tr.Next(2, 1, t); tr.Sent(2, 1, t);
        tr.Refused(t.AddMilliseconds(200));
        Check.Eq(tr.PendingId, 0, "a refusal ends the swap in flight");
        Check.Eq(tr.Next(2, 1, t.AddMilliseconds(300)), WeaponSwapTracker.Step.Swap, "retry without waiting out the timeout");
        tr.Sent(2, 1, t.AddMilliseconds(300));
        tr.Refused(t.AddMilliseconds(400));
        Check.Eq(tr.Next(2, 1, t.AddMilliseconds(500)), WeaponSwapTracker.Step.Hold, "second refusal with MaxTries 2: rest");
    }

    /// <summary>
    /// Kelpie 2026-10-15 00:44:22-32: a house recall right after the wield; the server answered
    /// all 3 tries 'too busy' and the Frost crossbow was benched for 2 min. A busy answer is no
    /// try (bounded by MaxBusyRefunds), and a portal or recall ends every rest.
    /// </summary>
    private static void SwapBusyNotCounted()
    {
        var tr = new WeaponSwapTracker();
        DateTime t = new(2026, 10, 15, 0, 44, 22);
        for (int i = 0; i < 5; i++)
        {
            Check.Eq(tr.Next(2, 1, t), WeaponSwapTracker.Step.Swap, $"busy round {i}: swap");
            tr.Sent(2, 1, t);
            tr.Busy(t.AddMilliseconds(100));
            Check.Eq(tr.PendingId, 0, "a busy answer ends the swap in flight");
            Check.Eq(tr.TriesFor(2), 0, "and gives the try back");
            Check.Eq(tr.Next(2, 1, t.AddMilliseconds(500)), WeaponSwapTracker.Step.Wait, "the wield waits out the busy pause");
            t = t.AddMilliseconds(100 + tr.BusyRetryMs);
        }
        Check.Eq(tr.Notice, null, "no give-up while the server is only busy");
        Check.Eq(tr.Next(2, 2, t), WeaponSwapTracker.Step.InHand, "once the server is free the weapon lands");

        // A weapon the server always calls busy still gets rested, after MaxBusyRefunds.
        var tr2 = new WeaponSwapTracker { MaxTries = 1, MaxBusyRefunds = 2 };
        DateTime u = t;
        for (int i = 0; i < 2; i++)
        {
            tr2.Next(3, 1, u); tr2.Sent(3, 1, u);
            tr2.Busy(u);
            u = u.AddMilliseconds(tr2.BusyRetryMs);
        }
        tr2.Next(3, 1, u); tr2.Sent(3, 1, u);
        tr2.Busy(u);
        Check.True(tr2.Notice != null && tr2.Notice.Contains("server too busy"), "past the refunds it counts: rested");
        Check.Eq(tr2.Next(3, 1, u.AddSeconds(5)), WeaponSwapTracker.Step.Hold, "resting");

        // A portal or recall ends the rest.
        tr2.NewPlace();
        Check.Eq(tr2.Next(3, 1, u.AddSeconds(10)), WeaponSwapTracker.Step.Swap, "after a portal: try again at once");
        Check.Eq(tr2.TriesFor(3), 0, "with a fresh count");
    }

    /// <summary>
    /// Combat reads ACE's busy answer to a wield: WeenieError 0x1D, then InventoryServerSaveFailed
    /// with no error. The engine keeps only the last record, so the SaveFailed alone must read as
    /// busy (before: "error 0", no answer, the try ran into the timeout and counted).
    /// </summary>
    private static void CombatReadsBusyWield()
    {
        const uint Old = 0x7000B001, New = 0x7000B002;
        FakeHost.Reset();
        FakeHost.WeenieErrorCalls = true;
        FakeHost.Names[Old] = "Fire Crossbow"; FakeHost.Names[New] = "Frost Compound Crossbow";
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { Old, New });
        var c = new CombatManager(host, new LegacyUiSettings(), cache);
        c.SetPlayerId(Player);
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        var send = typeof(CombatManager).GetMethod("MarkWieldSent", F)!;
        var step = typeof(CombatManager).GetMethod("SwapStep", F)!;
        int oldId = unchecked((int)Old), newId = unchecked((int)New);

        send.Invoke(c, new object[] { newId, oldId });
        FakeHost.WeenieError(0x1D);
        FakeHost.WeenieError(0, 0x00A0, Old);
        var s = (WeaponSwapTracker.Step)step.Invoke(c, new object[] { newId, oldId })!;
        Check.Eq(s, WeaponSwapTracker.Step.Wait, "busy: wait out the pause, then wield again");
        Check.Eq(c.SwapTracker.PendingId, 0, "the wield in flight is over");
        Check.Eq(c.SwapTracker.TriesFor(newId), 0, "and not counted");
        Check.True(FakeHost.Logs.Any(l => l.Contains("answered 'too busy': not counted")), "logged");

        // A real refusal (a specific error) still counts.
        c.SwapTracker.NewPlace();
        send.Invoke(c, new object[] { newId, oldId });
        FakeHost.WeenieError(0x0036);
        step.Invoke(c, new object[] { newId, oldId });
        Check.Eq(c.SwapTracker.TriesFor(newId), 1, "a refusal is a try");

        // Leaving portal space ends tries and rests.
        FakeHost.Portaling = true;
        c.OnHeartbeat();
        FakeHost.Portaling = false;
        c.OnHeartbeat();
        Check.Eq(c.SwapTracker.TriesFor(newId), 0, "after the portal: a fresh count");
    }

    // ── The tester's case ─────────────────────────────────────────────────────

    /// <summary>
    /// Dragon's Isle (2026-09-30): the tester cut the Items list down to the wands used there,
    /// and the bot went in with another fire wand and cast Blades. Reproduced with a monster
    /// whose weakness table ties Slash, Pierce, Bludgeon and Nether at the top (Ethereal Wisp:
    /// Lightning 0.8, Fire 0.55, Acid 0.4, Cold 0), the fire wand in hand, and the Damage tab
    /// (default weapon and learned best) still naming the fire wand from before the list edit.
    /// Before the fix: the Damage tab pick won without checking the list, Magic mode trusted
    /// whatever wand was in hand, and Auto took Slash (first of the tie) whatever the wand.
    /// </summary>
    private static void TesterDragonsIsle()
    {
        var weak = CreatureWeakness.Rank(1535, "Ethereal Wisp", 0, null);
        Check.True(weak != null && weak.Source == "server data", "the table has the Ethereal Wisp");
        if (weak == null) return;
        Check.Eq(weak.Order[0].Element, "Slash", "the table lists Slash first (the old Auto pick)");

        var rig = new Rig(1535, "Ethereal Wisp");
        rig.Store.SetDefaultWeapon(FireWand);
        int inHand = unchecked((int)FireWand);
        rig.C.InHandOverride = () => inHand;

        var plan = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)plan.WeaponId), ListedWand, "the listed (Lightning) wand, not the fire wand");
        Check.Eq(plan.Element, "Lightning", "Lightning: the weakest element a listed wand has (Slash/Pierce/Bludgeon/Nether have none)");
        Check.True(plan.Element != "Slash", "no Blades");

        // Magic mode with the fire wand in hand: combat swaps to the listed wand, once.
        var equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool ready = (bool)equip.Invoke(rig.C, new object[] { rig.Target, "Auto" })!;
        Check.False(ready, "not ready: a swap was sent");
        Check.Eq(FakeHost.Uses.Count, 1, "one UseObject");
        Check.Eq(FakeHost.Uses.FirstOrDefault(), ListedWand, "for the listed wand");
        equip.Invoke(rig.C, new object[] { rig.Target, "Auto" });
        equip.Invoke(rig.C, new object[] { rig.Target, "Auto" });
        Check.Eq(FakeHost.Uses.Count, 1, "one swap in flight: nothing more while it lands");
        Check.False(FakeHost.Uses.Contains(FireWand), "the fire wand is never used");

        inHand = unchecked((int)ListedWand);                 // it landed
        ready = (bool)equip.Invoke(rig.C, new object[] { rig.Target, "Auto" })!;
        Check.True(ready, "listed wand in hand, Magic mode: ready to cast");
        Check.Eq(FakeHost.Uses.Count, 1, "no further swaps");

        // With only the fire wand listed (and in hand), it casts Fire, the weakest element it has.
        rig.S.ItemRules = new List<ItemRule> { new() { Id = unchecked((int)FireWand), Name = "Fire Wand", Element = "Fire", ElementSource = "icon" } };
        inHand = unchecked((int)FireWand);
        var fire = rig.C.PlanFor(rig.Target, null);
        Check.Eq(unchecked((uint)fire.WeaponId), FireWand, "the fire wand, now listed");
        Check.Eq(fire.Element, "Fire", "casts Fire from it, not Blades");
    }
}
