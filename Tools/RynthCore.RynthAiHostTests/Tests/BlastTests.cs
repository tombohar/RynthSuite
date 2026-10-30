using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Attack spell shapes (2026-10-03): Blast joins Arc / Ring / Streak / Bolt. Which shape a rule
// casts, that every element's base names exist in the spell data at the tiers retail has (and
// cast the element they say), that the highest known tier is picked with the tier-7 lore and
// the Incantation, that Blast falls back to Bolt for a character who knows no blast, that a
// spell name with several ids resolves to the one the character knows, and that UseBlast
// saves and loads (old files: off). Expectations come from the plugin's own SpellData.txt.
internal static class BlastTests
{
    private const uint Player = 0x50000D01;

    private static readonly string[] WarElements = { "Fire", "Cold", "Lightning", "Acid", "Blade", "Pierce", "Bludgeon", "Slash" };
    private static readonly int[] TieredShapes = { CombatManager.ShapeArc, CombatManager.ShapeStreak, CombatManager.ShapeBolt, CombatManager.ShapeBlast };

    public static void Register(Runner r)
    {
        r.Add("attack shapes: Arc, Streak, Blast, Bolt in that order; ring-only casts Bolt; none = no cast", ShapeOrder);
        r.Add("attack spells: every element and shape names a real spell line of one element", EveryElementEveryShape);
        r.Add("attack spells: elements cast different spells (Lightning is not Bludgeon)", ElementsAreDistinct);
        r.Add("blast: the highest known tier, the tier-7 lore, the Incantation", BlastTiers);
        r.Add("blast: each element's blast, and Nether Blast for void", BlastElements);
        r.Add("blast: wins over Bolt, loses to Arc and Streak", BlastPrecedence);
        r.Add("blast: no blast known (retail has no blast I/II) -> the same element's Bolt", BlastFallsBackToBolt);
        r.Add("known spells: a name with several ids resolves to the id the character knows", DuplicateNameIds);
        r.Add("monster rules: UseBlast saves and loads; old files load with it off", RuleJson);
        r.Add("blast override: Blast Range off keeps today's shape; on, the base is the other shape; defaults 0 / 3", BlastOverrideSettings);
        r.Add("rings (10-25): the level-5 rings from tier 5 (named or plain), the level-7 II from tier 7", RingLevels);
    }

    // The rings are level 5 (Cassius' Ring of Fire, Halo of Frost, ..., and the plain Flame / Acid / Force /
    // Lightning Ring) and level 7 (the named II). A tier-5 caster used to get no ring at all: the named one
    // waited for tier 6 and the plain ones were looked for as "Ring of Fire V" and the like.
    private static void RingLevels()
    {
        var find = typeof(CombatManager).GetMethod("FindBestRingSpell",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        string Ring(int tier, string element, params string[] known)
        {
            var rig = new CastRig(tier, known);
            return NameOf((int)find.Invoke(rig.C, new object[] { element, RynthCore.Loot.AcSkillType.WarMagic })!);
        }
        Check.Eq(Ring(5, "Fire", "Cassius' Ring of Fire"), "Cassius' Ring of Fire", "tier 5: the named level-5 ring");
        Check.Eq(Ring(5, "Fire", "Flame Ring"), "Flame Ring", "tier 5: the plain level-5 ring when that is the one known");
        Check.Eq(Ring(5, "Lightning", "Lightning Ring"), "Lightning Ring", "Lightning's plain ring");
        Check.Eq(Ring(6, "Cold", "Halo of Frost"), "Halo of Frost", "tier 6: the level-5 ring");
        Check.Eq(Ring(7, "Cold", "Halo of Frost", "Halo of Frost II"), "Halo of Frost II", "tier 7: the II");
        Check.Eq(Ring(7, "Cold", "Halo of Frost"), "Halo of Frost", "tier 7 without the II known: the level-5 one");
        Check.Eq(Ring(6, "Cold", "Halo of Frost", "Halo of Frost II"), "Halo of Frost", "tier 6 knowing the II: not yet (level 7)");
        Check.Eq(Ring(4, "Cold", "Halo of Frost"), "(none)", "tier 4: no ring (none below level 5)");
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private static bool Ok(bool cond, string msg) { Check.True(cond, msg); return cond; }

    private static int Id(string name) => SpellTable.Named(name).Select(r => r.Id).FirstOrDefault();
    private static string NameOf(int id) => SpellTable.ById(id)?.Name ?? (id == 0 ? "(none)" : $"#{id}");
    private static string Roman(int t) => t switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", _ => "?" };

    private sealed class CastRig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;

        /// <summary>A caster whose combat tier is <paramref name="tier"/> and who knows exactly <paramref name="known"/> (every id of each name).</summary>
        public CastRig(int tier, params string[] known)
        {
            FakeHost.Reset();
            foreach (string n in known)
            {
                var rows = SpellTable.Named(n).ToList();
                Check.True(rows.Count > 0, $"test setup: '{n}' is in the spell data");
                foreach (var row in rows) FakeHost.Spellbook.Add((uint)row.Id);
            }
            int T(int k) => k <= tier ? 0 : 10000;   // the no-skills stub reads 250 buffed
            S.MinSkillLevelTier1 = 0; S.MinSkillLevelTier2 = T(2); S.MinSkillLevelTier3 = T(3); S.MinSkillLevelTier4 = T(4);
            S.MinSkillLevelTier5 = T(5); S.MinSkillLevelTier6 = T(6); S.MinSkillLevelTier7 = T(7); S.MinSkillLevelTier8 = T(8);
            S.RingRange = 0;

            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, Array.Empty<uint>());
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.RefreshKnownSpells();
            C.SetSpellManager(sm);
        }

        public (string Spell, string Shape) Cast(string element, MonsterRule rule)
        {
            int id = C.FindBestShapedSpell(element, rule, out _, out int shape);
            return (NameOf(id), CombatManager.ShapeName(shape));
        }
    }

    private static MonsterRule Blast() => new() { Name = "Drudge", UseBolt = false, UseBlast = true };

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void ShapeOrder()
    {
        MonsterRule R(bool arc = false, bool ring = false, bool streak = false, bool bolt = false, bool blast = false)
            => new() { UseArc = arc, UseRing = ring, UseStreak = streak, UseBolt = bolt, UseBlast = blast };
        Check.Eq(CombatManager.PickBaseShape(null), CombatManager.ShapeBolt, "no rule: Bolt");
        Check.Eq(CombatManager.PickBaseShape(new MonsterRule()), CombatManager.ShapeBolt, "a new rule (Bolt on): Bolt");
        Check.Eq(CombatManager.PickBaseShape(R(blast: true)), CombatManager.ShapeBlast, "Blast");
        Check.Eq(CombatManager.PickBaseShape(R(bolt: true, blast: true)), CombatManager.ShapeBlast, "Blast + Bolt: Blast");
        Check.Eq(CombatManager.PickBaseShape(R(streak: true, blast: true)), CombatManager.ShapeStreak, "Streak + Blast: Streak");
        Check.Eq(CombatManager.PickBaseShape(R(arc: true, blast: true)), CombatManager.ShapeArc, "Arc + Blast: Arc");
        Check.Eq(CombatManager.PickBaseShape(R(ring: true, blast: true)), CombatManager.ShapeBlast, "Ring + Blast: Blast between rings");
        Check.Eq(CombatManager.PickBaseShape(R(ring: true)), CombatManager.ShapeBolt, "ring only: Bolt between rings (as before)");
        Check.Eq(CombatManager.PickBaseShape(R()), -1, "nothing on: no cast");
    }

    /// <summary>Every war element x Arc/Streak/Bolt/Blast, plus void Nether: the tiers retail has
    /// exist, the tier-7 form (lore or "VII") and the Incantation exist, and all of them are one
    /// spell category (one element) — a lore of another element (the old Shock Wave / Alset's
    /// Coil mix) fails here.</summary>
    private static void EveryElementEveryShape()
    {
        foreach (bool useVoid in new[] { false, true })
            foreach (string element in useVoid ? new[] { "Nether" } : WarElements)
                foreach (int shape in TieredShapes)
                {
                    string what = $"{(useVoid ? "void" : "war")} {element} {CombatManager.ShapeName(shape)}";
                    if (!Ok(CombatManager.TryGetShapeBase(element, shape, useVoid, out string b, out string? lore, out bool forced), what + ": has a base"))
                        continue;
                    Check.False(forced, what + ": its own row, not the Fire fallback");
                    var cats = new HashSet<int>();
                    int low = shape == CombatManager.ShapeBlast && !useVoid ? 3 : 1;   // retail blasts start at III
                    for (int t = low; t <= 6; t++)
                    {
                        var rows = SpellTable.Named($"{b} {Roman(t)}").ToList();
                        if (Ok(rows.Count > 0, $"{what}: '{b} {Roman(t)}' exists"))
                            cats.Add(rows[0].Category);
                    }
                    string t7 = lore ?? $"{b} VII";
                    if (!useVoid && shape != CombatManager.ShapeArc)
                        Check.True(lore != null, $"{what}: a tier-7 lore name ({b} has no VII)");
                    var r7 = SpellTable.Named(t7).ToList();
                    if (Ok(r7.Count > 0, $"{what}: tier 7 '{t7}' exists")) cats.Add(r7[0].Category);
                    var r8 = SpellTable.Named($"Incantation of {b}").ToList();
                    if (Ok(r8.Count > 0, $"{what}: 'Incantation of {b}' exists")) cats.Add(r8[0].Category);
                    Check.Eq(cats.Count, 1, $"{what}: every tier is the same spell line ({string.Join(",", cats)})");
                }
    }

    private static void ElementsAreDistinct()
    {
        foreach (int shape in TieredShapes)
        {
            var seen = new Dictionary<int, string>();
            foreach (string element in WarElements)
            {
                if (element == "Slash") continue;   // Slash shares the Blade line
                CombatManager.TryGetShapeBase(element, shape, false, out string b, out _, out _);
                var row = SpellTable.Named($"{b} VI").FirstOrDefault();
                if (!Ok(row != null, $"{element} {CombatManager.ShapeName(shape)}: '{b} VI'")) continue;
                if (seen.TryGetValue(row!.Category, out string? other))
                    Check.True(false, $"{element} and {other} {CombatManager.ShapeName(shape)} cast the same line ({b})");
                else seen[row.Category] = element;
            }
        }
        CombatManager.TryGetShapeBase("Slash", CombatManager.ShapeBlast, false, out string slash, out _, out _);
        Check.Eq(slash, "Blade Blast", "Slash blasts are Blade Blast");
    }

    private static void BlastTiers()
    {
        string[] all = { "Flame Blast III", "Flame Blast IV", "Flame Blast V", "Flame Blast VI", "Silencia's Scorn", "Incantation of Flame Blast",
                         "Flame Bolt VI", "Ilservian's Flame", "Incantation of Flame Bolt" };
        Check.Eq(new CastRig(8, all).Cast("Fire", Blast()), ("Incantation of Flame Blast", "Blast"), "tier 8, all known: the Incantation");
        Check.Eq(new CastRig(7, all).Cast("Fire", Blast()), ("Silencia's Scorn", "Blast"), "tier 7: the lore (Flame Blast VII)");
        Check.Eq(new CastRig(6, all).Cast("Fire", Blast()), ("Flame Blast VI", "Blast"), "tier 6: VI");
        Check.Eq(new CastRig(4, all).Cast("Fire", Blast()), ("Flame Blast IV", "Blast"), "tier 4: IV even with more known");
        Check.Eq(new CastRig(8, "Flame Blast IV", "Flame Bolt VI").Cast("Fire", Blast()), ("Flame Blast IV", "Blast"),
            "tier 8, only IV known: IV (unknown tiers are skipped, not cast)");
        Check.Eq(new CastRig(8, "Flame Blast III", "Flame Blast V").Cast("Fire", Blast()), ("Flame Blast V", "Blast"), "gaps are skipped");
    }

    private static void BlastElements()
    {
        var cases = new (string Element, string Known, string Expect)[]
        {
            ("Fire", "Flame Blast VI", "Flame Blast VI"),
            ("Cold", "Frost Blast VI", "Frost Blast VI"),
            ("Lightning", "Lightning Blast VI", "Lightning Blast VI"),
            ("Acid", "Acid Blast VI", "Acid Blast VI"),
            ("Blade", "Blade Blast VI", "Blade Blast VI"),
            ("Slash", "Blade Blast VI", "Blade Blast VI"),
            ("Pierce", "Force Blast VI", "Force Blast VI"),
            ("Bludgeon", "Shock Blast VI", "Shock Blast VI"),
            ("Cold", "Winter's Embrace", "Winter's Embrace"),
            ("Bludgeon", "Pummeling Storm", "Pummeling Storm"),
            ("Pierce", "Stinging Needles", "Stinging Needles"),
            ("Nether", "Nether Blast VII", "Nether Blast VII"),
        };
        foreach (var (element, known, expect) in cases)
        {
            // Every other element's blast is known too: the element must pick its own.
            var book = new List<string> { known, "Flame Blast VI", "Frost Blast VI", "Shock Blast VI", "Lightning Blast VI", "Force Blast VI" };
            Check.Eq(new CastRig(7, book.Distinct().ToArray()).Cast(element, Blast()), (expect, "Blast"), $"{element} with {known} known");
        }
    }

    private static void BlastPrecedence()
    {
        string[] book = { "Frost Blast VI", "Frost Bolt VI", "Frost Arc VI", "Frost Streak VI" };
        Check.Eq(new CastRig(6, book).Cast("Cold", new MonsterRule { UseBolt = true, UseBlast = true }), ("Frost Blast VI", "Blast"), "Blast + Bolt: Blast");
        Check.Eq(new CastRig(6, book).Cast("Cold", new MonsterRule { UseBolt = true }), ("Frost Bolt VI", "Bolt"), "Bolt alone: Bolt (unchanged)");
        Check.Eq(new CastRig(6, book).Cast("Cold", new MonsterRule { UseArc = true, UseBlast = true }), ("Frost Arc VI", "Arc"), "Arc + Blast: Arc");
        Check.Eq(new CastRig(6, book).Cast("Cold", new MonsterRule { UseStreak = true, UseBlast = true, UseBolt = false }), ("Frost Streak VI", "Streak"), "Streak + Blast: Streak");
    }

    private static void BlastFallsBackToBolt()
    {
        Check.Eq(new CastRig(2, "Frost Bolt I", "Frost Bolt II").Cast("Cold", Blast()), ("Frost Bolt II", "Bolt"),
            "tier 2, Blast on: no blast exists that low, so Frost Bolt II");
        Check.Eq(new CastRig(6, "Frost Bolt VI", "Frost Blast III").Cast("Cold", Blast()), ("Frost Blast III", "Blast"),
            "a known blast, even a low one, beats the fallback");
        Check.Eq(new CastRig(6, "Acid Stream VI").Cast("Lightning", Blast()), ("(none)", "?"),
            "the fallback is the same element's Bolt only: nothing known, nothing cast");
        Check.Eq(new CastRig(6, "Frost Arc VI").Cast("Cold", new MonsterRule { UseArc = true }), ("Frost Arc VI", "Arc"), "Arc unchanged");
        Check.Eq(new CastRig(6, "Frost Bolt VI").Cast("Cold", new MonsterRule { UseArc = true, UseBolt = false }), ("(none)", "?"),
            "Arc with no arc known still casts nothing (no cross-shape fallthrough for the other shapes)");
    }

    private static void DuplicateNameIds()
    {
        // "Acid Blast III" is ids 99 and 3653 in the data; the name map keeps the last one.
        var ids = SpellTable.Named("Acid Blast III").Select(r => r.Id).ToList();
        Check.True(ids.Count >= 2, $"Acid Blast III has several ids ({string.Join(",", ids)})");
        FakeHost.Reset();
        FakeHost.Spellbook.Add((uint)ids[0]);
        var host = FakeHost.Create(Player);
        var sm = new SpellManager(host, new LegacyUiSettings());
        sm.InitializeNatively();
        sm.RefreshKnownSpells();
        Check.True(sm.TryResolveKnownSpellId("Acid Blast III", out int got), "the character knows one of its ids: resolves");
        Check.Eq(got, ids[0], "to the id the character knows");
        Check.False(sm.TryResolveKnownSpellId("Acid Blast IV", out _), "an unknown spell still doesn't");

        var fb = SpellTable.Named("Flame Bolt I").Select(r => r.Id).ToList();
        FakeHost.Reset();
        FakeHost.Spellbook.Add((uint)fb[0]);
        sm = new SpellManager(FakeHost.Create(Player), new LegacyUiSettings());
        sm.InitializeNatively();
        sm.RefreshKnownSpells();
        Check.True(sm.TryResolveKnownSpellId("Flame Bolt I", out int fbId) && fbId == fb[0], $"Flame Bolt I ({fb.Count} ids): the learned one ({fb[0]})");
    }

    private static void RuleJson()
    {
        // A monsters.json from before Blast existed (the engine and plugin write PascalCase names).
        const string old = "[{\"Name\":\"Default\",\"Priority\":1,\"UseBolt\":true},{\"Name\":\"Drudge\",\"Priority\":5,\"UseArc\":true,\"UseBolt\":false}]";
        var rules = JsonSerializer.Deserialize(old, RynthAiJsonContext.Default.MonsterRuleList)!;
        Check.Eq(rules.Count, 2, "old file: both rules");
        Check.False(rules[0].UseBlast || rules[1].UseBlast, "old file: Blast off");
        Check.True(rules[0].UseBolt && rules[1].UseArc && rules[1].Priority == 5, "old file: the rest as saved");
        Check.Eq(CombatManager.PickBaseShape(rules[0]), CombatManager.ShapeBolt, "old Default still casts Bolt");

        rules[1].UseBlast = true;
        string json = JsonSerializer.Serialize(rules, RynthAiJsonContext.Default.MonsterRuleList);
        Check.True(json.Contains("\"UseBlast\": true"), "saved with UseBlast");
        var back = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonsterRuleList)!;
        Check.True(back[1].UseBlast && !back[0].UseBlast, "round trip keeps it");

        // The engine's Damage panel pushes { "rules": [...] } (MonstersPanel.Payload, default names).
        const string fromEngine = "{\"rules\":[{\"Name\":\"Default\",\"UseBolt\":true,\"UseBlast\":false},{\"Name\":\"Mite\",\"UseBolt\":false,\"UseBlast\":true,\"Priority\":3}]}";
        var payload = JsonSerializer.Deserialize(fromEngine, RynthAiJsonContext.Default.MonstersBridgePayload)!;
        Check.True(payload.Rules[1].UseBlast && payload.Rules[1].Priority == 3, "the engine's rules JSON carries UseBlast and Priority");
    }

    private static void BlastOverrideSettings()
    {
        // With Blast Range on, Blast is an override like the ring: the base is the rule's other shape.
        Check.Eq(CombatManager.PickBaseShapeWithoutBlast(new MonsterRule { UseBlast = true, UseBolt = false }), CombatManager.ShapeBolt, "Blast alone: Bolt between blasts");
        Check.Eq(CombatManager.PickBaseShapeWithoutBlast(new MonsterRule { UseBlast = true, UseArc = true }), CombatManager.ShapeArc, "Blast + Arc: Arc between blasts");
        Check.Eq(CombatManager.PickBaseShapeWithoutBlast(new MonsterRule { UseBlast = true, UseStreak = true, UseBolt = false }), CombatManager.ShapeStreak, "Blast + Streak: Streak between blasts");

        // Off (Blast Range 0, the default): Blast is just the shape it always casts.
        string[] book = { "Frost Blast VI", "Frost Bolt VI" };
        Check.Eq(new CastRig(6, book).Cast("Cold", new MonsterRule { UseBolt = false, UseBlast = true }), ("Frost Blast VI", "Blast"), "Blast Range off: Blast as before");

        // Profiles and engine payloads from before the setting: off, and 3 targets once turned on.
        var old = JsonSerializer.Deserialize("{\"MinRingTargets\":4}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.Eq(old.BlastRange, 0, "old profile: Blast Range off");
        Check.Eq(old.MinBlastTargets, 3, "old profile: Min Blast Targets 3");
        var bridge = JsonSerializer.Deserialize("{\"MinRingTargets\":4}", RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.Eq(bridge.BlastRange, 0, "older engine's settings: Blast Range off");
        Check.Eq(bridge.MinBlastTargets, 3, "older engine's settings: Min Blast Targets kept at 3");
        Check.Near(CombatManager.BlastFanHalfAngle, 55.0, 1e-9, "the fan: 90 degrees of projectiles plus a margin each side");
    }
}
