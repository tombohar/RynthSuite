using System;
using System.IO;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Spells window's data and buff profiles: ACECustom's spell catalog (Combat\SpellCatalog.tsv),
// the buffing key a catalog spell maps to, profile files, and how buffing uses a chosen profile
// (its list replaces the built-in one; names without a tier ladder fall back to the spell family).
// Spell ids and levels below are read from the generated catalog.
internal static class SpellCatalogTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeCreature = 31;

    // Catalog ids (ACECustom SpellCatalog-Full.csv).
    private const int StrengthSelfI = 2, StrengthSelfIII = 1329, StrengthSelfVI = 1332, StrengthOtherVI = 1337;
    private const int MightOfTheLugians = 2087, BladeBaneVI = 1562, IncantationOfBladeBane = 4393;
    private const int AuraOfBloodDrinkerSelfIII = 1613, ImpenetrabilityIncantation = 3980;
    private const int StrengthFamily = 1;

    public static void Register(Runner r)
    {
        r.Add("spell catalog: every ACECustom spell with school, level and target", CatalogLoads);
        r.Add("spell catalog: tier names strip to buffing's base names", StripTier);
        r.Add("spell catalog: Other, lore and item spells map to the ladder buffing casts", BuffKeys);
        r.Add("spell catalog: Item Enchantment buffs count as item spells", ItemSchool);
        r.Add("buff profile: save, list and load round-trip", ProfileRoundTrip);
        r.Add("buff profile: an id-only entry is filled in; a newer schema is refused", ProfileNormalizeAndSchema);
        r.Add("buff profile: a chosen profile replaces the built-in list (order kept, no disabled or duplicate lines)", ProfileReplacesList);
        r.Add("buff profile: a profile spell casts the best level known", ProfileResolvesTier);
        r.Add("buff profile: a name with no tier ladder falls back to its spell family", ProfileFamilyFallback);
        r.Add("buff profile: Loaded Files lists profiles, picks one by index, and the snapshot reports it", LoadedFilesPicker);
    }

    // ── Rig ─────────────────────────────────────────────────────────────────

    private static SpellCatalogEntry Spell(int id) =>
        SpellCatalog.Get(id) ?? throw new InvalidOperationException($"spell {id} missing from the catalog");

    /// <summary>A buff manager over FakeHost with the given buffed Creature skill and known spells.</summary>
    private static BuffManager NewBuffs(int buffedCreature, params int[] known)
    {
        FakeHost.Reset();
        foreach (int id in known) FakeHost.Spellbook.Add((uint)id);
        FakeHost.Skills[(Player, STypeCreature)] = (buffedCreature, 2);
        var host = FakeHost.Create();
        var settings = new LegacyUiSettings { EnableBuffing = true };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var spells = new SpellManager(host, settings);
        spells.InitializeNatively();
        spells.SetCharacterSkills(skills);
        var buffs = new BuffManager(host, settings, spells, new PlayerVitalsCache());
        buffs.SetCharacterSkills(skills);
        return buffs;
    }

    /// <summary>Runs <paramref name="body"/> with profile files in a fresh scratch folder.</summary>
    private static void WithProfileFolder(Action<string> body)
    {
        string folder = Path.Combine(Program.TempRoot, "BuffProfiles-" + Guid.NewGuid().ToString("N"));
        string? previous = BuffProfileStore.FolderOverride;
        BuffProfileStore.FolderOverride = folder;
        try { body(folder); }
        finally { BuffProfileStore.FolderOverride = previous; }
    }

    // ── Catalog ─────────────────────────────────────────────────────────────

    private static void CatalogLoads()
    {
        Check.True(SpellCatalog.IsLoaded, "the embedded catalog loads");
        Check.True(SpellCatalog.All.Count >= 6000, $"6000+ spells (got {SpellCatalog.All.Count})");
        foreach (string school in SpellCatalog.Schools)
            Check.True(SpellCatalog.All.Any(e => e.School == school), $"school present: {school}");
        Check.True(SpellCatalog.Effects.Contains("Buff"), "Buff is an effect");

        var s = Spell(StrengthSelfI);
        Check.Eq(s.Name, "Strength Self I", "id 2 is Strength Self I");
        Check.Eq(s.Level, 1, "level 1");
        Check.Eq(s.School, "Creature Enchantment", "Creature school");
        Check.True(s.IsSelf && s.IsBuff, "a Self buff");
        Check.Eq(s.FamilyId, StrengthFamily, "Strength family");
        Check.Eq(Spell(StrengthOtherVI).Target, "Other", "Strength Other VI targets Other");
        Check.Eq(SpellCatalog.GetByName("strength self vi")?.Id ?? 0, StrengthSelfVI, "name lookup ignores case");
        Check.True(SpellCatalog.Family(StrengthFamily).Count >= 16, "the Strength family has Self and Other ladders");
    }

    private static void StripTier()
    {
        Check.Eq(SpellCatalog.StripTier("Strength Self VI"), "Strength Self", "roman VI");
        Check.Eq(SpellCatalog.StripTier("Strength Self VIII"), "Strength Self", "roman VIII (not VII + I)");
        Check.Eq(SpellCatalog.StripTier("Strength Self IV"), "Strength Self", "roman IV (not V)");
        Check.Eq(SpellCatalog.StripTier("Incantation of Blade Bane"), "Blade Bane", "Incantation of");
        Check.Eq(SpellCatalog.StripTier("Aura of Blood Drinker Self III"), "Blood Drinker Self", "Aura of");
        Check.Eq(SpellCatalog.StripTier("Impenetrability Incantation"), "Impenetrability", "trailing Incantation");
        Check.Eq(SpellCatalog.StripTier("Might of the Lugians"), "Might of the Lugians", "lore names are unchanged");
    }

    private static void BuffKeys()
    {
        Check.Eq(SpellCatalog.BuffKeyFor(Spell(StrengthSelfVI)), "Strength Self", "a Self spell is its own ladder");
        Check.Eq(SpellCatalog.BuffKeyFor(Spell(StrengthOtherVI)), "Strength Self", "an Other spell casts the Self ladder");
        Check.Eq(SpellCatalog.BuffKeyFor(Spell(MightOfTheLugians)), "Strength Self", "a lore name casts its family's Self ladder");
        Check.Eq(SpellCatalog.BuffKeyFor(Spell(BladeBaneVI)), "Blade Bane", "an Item spell is its own ladder");
        Check.Eq(SpellCatalog.BuffKeyFor(Spell(IncantationOfBladeBane)), "Blade Bane", "the Incantation joins the Item ladder");
        Check.Eq(SpellCatalog.BuffKeyFor(Spell(AuraOfBloodDrinkerSelfIII)), "Blood Drinker Self", "weapon auras");
        Check.Eq(SpellCatalog.BuffKeyFor(Spell(ImpenetrabilityIncantation)), "Impenetrability", "Impenetrability Incantation");
    }

    private static void ItemSchool()
    {
        Check.True(SpellCatalog.IsItemSchoolBuff("Blade Bane VI"), "Blade Bane VI is an item spell");
        Check.True(SpellCatalog.IsItemSchoolBuff("Aura of Blood Drinker Self III"), "weapon auras are item spells");
        Check.False(SpellCatalog.IsItemSchoolBuff("Strength Self VI"), "Strength Self VI is not");
        Check.False(SpellCatalog.IsItemSchoolBuff("No Such Spell"), "unknown names are not");
        Check.Eq(SpellCatalog.SkillForSchool("Item Enchantment"), AcSkillType.ItemEnchantment, "Item school casts with Item Enchantment");
        Check.Eq(SpellCatalog.SkillForSchool("Life Magic"), AcSkillType.LifeMagic, "Life school casts with Life Magic");
    }

    // ── Profile files ───────────────────────────────────────────────────────

    private static void ProfileRoundTrip() => WithProfileFolder(folder =>
    {
        var profile = new BuffProfile { Name = "Mage: main", Notes = "test" };
        profile.Entries.Add(BuffProfileStore.EntryFor(Spell(StrengthOtherVI)));
        profile.Entries.Add(BuffProfileStore.EntryFor(Spell(BladeBaneVI)));
        profile.Entries[1].Enabled = false;

        string path = BuffProfileStore.PathFor(profile.Name);
        Check.Eq(Path.GetFileName(path), "Mage_ main.json", "':' is not allowed in a file name");
        BuffProfileStore.Save(profile, path);
        Check.False(File.Exists(path + ".tmp"), "no temp file left behind");
        Check.Eq(string.Join(",", BuffProfileStore.ListFileNames()), "Mage_ main.json", "the folder lists it");

        Check.True(BuffProfileStore.TryLoad(path, out var loaded, out string error), "loads: " + error);
        Check.Eq(loaded.Name, "Mage: main", "name kept");
        Check.Eq(loaded.Notes, "test", "notes kept");
        Check.Eq(loaded.Entries.Count, 2, "two entries");
        Check.Eq(loaded.Entries[0].SpellId, StrengthOtherVI, "spell id kept");
        Check.Eq(loaded.Entries[0].Key, "Strength Self", "buffing key kept");
        Check.Eq(loaded.Entries[0].School, "Creature Enchantment", "school kept");
        Check.False(loaded.Entries[1].Enabled, "disabled flag kept");

        Check.True(BuffProfileStore.Delete(path), "deletes");
        Check.Eq(BuffProfileStore.ListFileNames().Count, 0, "the folder is empty again");
    });

    private static void ProfileNormalizeAndSchema() => WithProfileFolder(folder =>
    {
        Directory.CreateDirectory(folder);
        string idOnly = Path.Combine(folder, "Hand Edited.json");
        File.WriteAllText(idOnly, "{\"Entries\":[{\"SpellId\":" + BladeBaneVI + "},{\"SpellId\":0}]}");
        Check.True(BuffProfileStore.TryLoad(idOnly, out var p, out string error), "an id-only file loads: " + error);
        Check.Eq(p.Name, "Hand Edited", "a blank name comes from the file name");
        Check.Eq(p.Entries.Count, 1, "an entry with neither id nor key is dropped");
        Check.Eq(p.Entries[0].Name, "Blade Bane VI", "name filled from the catalog");
        Check.Eq(p.Entries[0].Key, "Blade Bane", "key filled from the catalog");
        Check.Eq(p.Entries[0].School, "Item Enchantment", "school filled from the catalog");
        Check.Eq(p.Entries[0].Level, 6, "level filled from the catalog");
        Check.True(p.Entries[0].Enabled, "entries default to enabled");

        string newer = Path.Combine(folder, "Newer.json");
        File.WriteAllText(newer, "{\"Schema\":99,\"Entries\":[]}");
        Check.False(BuffProfileStore.TryLoad(newer, out _, out string why), "a newer schema is refused");
        Check.True(why.Contains("newer"), "and says why: " + why);
        Check.False(BuffProfileStore.TryLoad(Path.Combine(folder, "missing.json"), out _, out _), "a missing file is refused");
    });

    // ── Buffing with a profile ──────────────────────────────────────────────

    private static void ProfileReplacesList()
    {
        var buffs = NewBuffs(500);
        var builtIn = buffs.BuildBuiltInBuffList();
        Check.Eq(string.Join(",", buffs.BuildDynamicBuffList()), string.Join(",", builtIn), "no profile: the built-in list");

        var profile = new BuffProfile { Name = "test" };
        profile.Entries.Add(BuffProfileStore.EntryFor(Spell(BladeBaneVI)));
        profile.Entries.Add(BuffProfileStore.EntryFor(Spell(StrengthOtherVI)));
        profile.Entries.Add(BuffProfileStore.EntryFor(Spell(StrengthSelfIII)));   // same ladder as the line above
        var focus = BuffProfileStore.EntryFor(Spell(SpellCatalog.GetByName("Focus Self VI")!.Id));
        focus.Enabled = false;
        profile.Entries.Add(focus);
        buffs.ApplyBuffProfile(profile);

        Check.Eq(string.Join(",", buffs.BuildDynamicBuffList()), "Blade Bane,Strength Self", "profile order, one line per ladder, disabled skipped");
        Check.Eq(buffs.ActiveBuffProfileName, "test", "the active profile is named");
        Check.Eq(buffs.ActiveBuffProfileCount, 2, "two spells in use");
        Check.Eq(buffs.SkillFor("Blade Bane"), AcSkillType.ItemEnchantment, "the profile's school picks the casting skill");
    }

    private static void ProfileResolvesTier()
    {
        var buffs = NewBuffs(500, StrengthSelfI, StrengthSelfIII, StrengthSelfVI);
        var profile = new BuffProfile { Name = "test" };
        profile.Entries.Add(BuffProfileStore.EntryFor(Spell(StrengthOtherVI)));
        buffs.ApplyBuffProfile(profile);
        Check.Eq(buffs.FindBestSpellId("Strength Self", buffs.SkillFor("Strength Self")), StrengthSelfVI,
            "Strength Other VI in the profile casts the best Strength Self known (VI)");
    }

    private static void ProfileFamilyFallback()
    {
        // A server-custom line: its base name has no tier ladder, only its family is known.
        var custom = new BuffProfileEntry
        {
            SpellId = StrengthSelfI, Name = "Custom Strength", Key = "Custom Strength",
            FamilyId = StrengthFamily, School = "Creature Enchantment", Target = "Self", Level = 1,
        };
        var profile = new BuffProfile { Name = "custom" };
        profile.Entries.Add(custom);

        var buffs = NewBuffs(500, StrengthSelfIII, MightOfTheLugians);
        buffs.ApplyBuffProfile(profile);
        Check.Eq(buffs.FindBestSpellId("Custom Strength", AcSkillType.CreatureEnchantment), MightOfTheLugians,
            "skill 500: the family's highest known level (Might of the Lugians, 7)");

        buffs = NewBuffs(250, StrengthSelfIII, MightOfTheLugians);
        buffs.ApplyBuffProfile(profile);
        Check.Eq(buffs.FindBestSpellId("Custom Strength", AcSkillType.CreatureEnchantment), StrengthSelfIII,
            "skill 250 (tier 5): capped below 7, so Strength Self III");

        buffs = NewBuffs(500);
        buffs.ApplyBuffProfile(profile);
        Check.Eq(buffs.FindBestSpellId("Custom Strength", AcSkillType.CreatureEnchantment), 0,
            "cold spellbook: no guess for a name without a ladder");
    }

    // ── Loaded Files (engine picker → RynthPluginSelectProfile kind 4) ─────

    private static void LoadedFilesPicker() => WithProfileFolder(folder =>
    {
        foreach (string name in new[] { "Beta", "Alpha" })
        {
            var p = new BuffProfile { Name = name };
            p.Entries.Add(BuffProfileStore.EntryFor(Spell(StrengthSelfVI)));
            BuffProfileStore.Save(p, BuffProfileStore.PathFor(name));
        }

        FakeHost.Reset();
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        dash.RefreshBuffProfileFiles();

        using (var doc = System.Text.Json.JsonDocument.Parse(dash.BuildSnapshotJson()))
        {
            var root = doc.RootElement;
            string list = string.Join(",", root.GetProperty("buffProfiles").EnumerateArray().Select(e => e.GetString()));
            Check.Eq(list, "Built-in,Alpha.json,Beta.json", "Built-in first, then the files sorted");
            Check.Eq(root.GetProperty("currentBuffName").GetString(), "Built-in", "no profile chosen yet");
            Check.Eq(root.GetProperty("selectedBuffIdx").GetInt32(), 0, "Built-in selected");
        }

        dash.SelectProfileAtIndex(4, 2);
        Check.Eq(dash.Settings.CurrentBuffProfilePath, Path.Combine(folder, "Beta.json"), "index 2 picks Beta");
        using (var doc = System.Text.Json.JsonDocument.Parse(dash.BuildSnapshotJson()))
        {
            Check.Eq(doc.RootElement.GetProperty("currentBuffName").GetString(), "Beta", "the snapshot names it");
            Check.Eq(doc.RootElement.GetProperty("selectedBuffIdx").GetInt32(), 2, "and selects it");
        }

        dash.SelectProfileAtIndex(4, 99);
        Check.Eq(dash.Settings.CurrentBuffProfilePath, Path.Combine(folder, "Beta.json"), "an out-of-range index changes nothing");
        dash.SelectProfileAtIndex(4, 0);
        Check.Eq(dash.Settings.CurrentBuffProfilePath, "", "index 0 goes back to the built-in list");
    });
}
