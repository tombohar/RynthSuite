using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-08: "Describe items when selected" and the Item Info options did not stay with the
// character's profile. They were written to the profile file, but LegacyDashboardRenderer.
// CopySettings (used by every login and profile switch) copies the loaded profile field by
// field and had no line for ItemInfoSettings, so the in-memory defaults stayed and the next
// autosave wrote them over the file. NavOverlay, PetAutoRefill, EnableGroundLoot,
// MonsterNameBlacklist, AmmoRules and the radar placement were dropped the same way.
//  - every serialized LegacyUiSettings member set to a non-default value comes through
//    CopySettings (catches any field added later without a copy line);
//  - the item info options survive a save / load through a profile file on disk.
internal static class ProfileLoadTests
{
    private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("profile: every saved setting survives loading (CopySettings lists each serialized field)", EverySavedFieldIsCopied);
        r.Add("profile: Describe Items and the Item Info options survive a save and a reload", ItemInfoSurvivesReload);
        r.Add("profile: a profile with null item info / nav overlay loads defaults, not null", NullNestedOptionsLoadDefaults);
        r.Add("profile: a save is atomic (temp file swapped in, no temp left) and keeps the previous profile as .bak", SaveKeepsBackup);
        r.Add("profile: a profile that doesn't read loads the .bak, sets the broken file aside and puts the backup back", UnreadableLoadsBackup);
        r.Add("profile: a profile that doesn't read and has no backup is kept, not overwritten by defaults until a setting changes", UnreadableKeptNotOverwritten);
        r.Add("profile: switching to a profile that doesn't read keeps it (backup used when there is one)", SwitchUnreadableKept);
    }

    /// <summary>LegacyDashboardRenderer.CopySettings(src, dst): what LoadSettings / SwitchProfile run.</summary>
    private static void CopySettings(LegacyUiSettings src, LegacyUiSettings dst) =>
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", Any)!.Invoke(null, new object[] { src, dst });

    /// <summary>A different value of the member's type that CopySettings' normalizing keeps distinct from the default.</summary>
    private static bool TryMutate(string name, Type type, object? value, out object? mutated)
    {
        mutated = null;
        if (name == nameof(LegacyUiSettings.OffhandDefault)) { mutated = "Shield"; return true; }   // must parse as an OffhandMode
        if (name == nameof(LegacyUiSettings.NavLabelMode))   { mutated = 1; return true; }          // NavLabelModes.Clamp: 0..2
        if (type == typeof(bool))   { mutated = !(bool)value!; return true; }
        if (type == typeof(int))    { mutated = (int)value! + 3; return true; }   // +3: MovementMode 1/2 map back to 0
        if (type == typeof(long))   { mutated = (long)value! + 3; return true; }
        if (type == typeof(uint))   { mutated = (uint)value! + 3; return true; }
        if (type == typeof(float))  { mutated = (float)value! + 3f; return true; }
        if (type == typeof(double)) { mutated = (double)value! + 3.0; return true; }
        if (type == typeof(string)) { mutated = (string?)value + "x"; return true; }
        if (type.IsEnum)
        {
            foreach (object v in Enum.GetValues(type))
                if (!v.Equals(value)) { mutated = v; return true; }
        }
        return false;
    }

    private static void EverySavedFieldIsCopied()
    {
        var info = RynthAiJsonContext.Default.LegacyUiSettings;
        var defaults = new LegacyUiSettings();
        var src = new LegacyUiSettings();
        var dst = new LegacyUiSettings();

        // Reference members (lists, option objects): dst must end up with a new instance.
        var dstOriginal = new Dictionary<string, object?>();
        var skipped = new List<string>();
        foreach (var p in info.Properties)
        {
            if (p.Get == null || p.Set == null) continue;
            Type t = p.PropertyType;
            if (t.IsValueType || t == typeof(string))
            {
                if (TryMutate(p.Name, t, p.Get(src), out object? m)) p.Set(src, m);
                else skipped.Add(p.Name);
            }
            else
            {
                dstOriginal[p.Name] = p.Get(dst);
            }
        }

        CopySettings(src, dst);

        int members = 0;
        var missing = new List<string>();
        foreach (var p in info.Properties)
        {
            if (p.Get == null || p.Set == null) continue;
            members++;
            if (dstOriginal.TryGetValue(p.Name, out object? before))
            {
                if (before != null && ReferenceEquals(p.Get(dst), before)) missing.Add(p.Name);
            }
            else if (!skipped.Contains(p.Name)
                     && Equals(p.Get(dst), p.Get(defaults)) && !Equals(p.Get(src), p.Get(defaults)))
            {
                missing.Add(p.Name);
            }
        }

        Check.True(members > 100, $"the JSON metadata lists the settings ({members} members)");
        Check.Eq(skipped.Count, 0, "every value member has a test value (no test value for: " + string.Join(", ", skipped) + ")");
        Check.Eq(missing.Count, 0, "members CopySettings leaves at their default: " + string.Join(", ", missing));
    }

    private static void ItemInfoSurvivesReload()
    {
        // What the Item Info window / the engine checkbox change, then autosave writes.
        var saved = new LegacyUiSettings();
        MagItemInfoSettings s = saved.ItemInfoSettings;
        s.OnSelect = true;
        s.ClickTrigger = MagItemInfoSettings.ClickRight;
        s.OnSelectArmor = false;
        s.SetHidden(MagItemInfoField.Spells, true);
        s.SetRatingHidden(2, true);
        s.Layout = MagItemInfoSettings.LayoutOneLine;
        s.ChatType = 7;
        s.Prefix = "[ii] ";
        saved.EnableGroundLoot = true;
        saved.PetAutoRefill = false;
        saved.MonsterNameBlacklist.Add("Drudge Skulker");

        string path = Path.Combine(Program.TempRoot, "profile-load", "Default.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(saved, RynthAiJsonContext.Default.LegacyUiSettings));

        // LoadSettings: read the file, copy it into the live settings object (a fresh session).
        var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), RynthAiJsonContext.Default.LegacyUiSettings)!;
        var live = new LegacyUiSettings();
        CopySettings(loaded, live);

        MagItemInfoSettings l = live.ItemInfoSettings;
        Check.True(l.OnSelect, "Describe items when selected stays on");
        Check.Eq(l.ClickTrigger, MagItemInfoSettings.ClickRight, "click trigger");
        Check.False(l.OnSelectArmor, "armor filter off");
        Check.True(l.IsHidden(MagItemInfoField.Spells), "hidden field");
        Check.True(l.IsRatingHidden(2), "hidden rating");
        Check.Eq(l.Layout, MagItemInfoSettings.LayoutOneLine, "layout");
        Check.Eq(l.ChatType, 7, "chat type");
        Check.Eq(l.Prefix, "[ii] ", "prefix");
        Check.True(live.EnableGroundLoot, "ground loot");
        Check.False(live.PetAutoRefill, "pet auto refill");
        Check.True(live.MonsterNameBlacklist.Contains("Drudge Skulker"), "monster blacklist");

        // Unchanged JSON means the autosave right after login has nothing to write over the file.
        Check.Eq(JsonSerializer.Serialize(live, RynthAiJsonContext.Default.LegacyUiSettings),
                 JsonSerializer.Serialize(saved, RynthAiJsonContext.Default.LegacyUiSettings),
                 "the reloaded profile saves back unchanged (autosave writes nothing different)");
    }

    private static void NullNestedOptionsLoadDefaults()
    {
        string json = JsonSerializer.Serialize(new LegacyUiSettings(), RynthAiJsonContext.Default.LegacyUiSettings);
        var doc = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!;
        doc.ItemInfoSettings = null!;
        doc.NavOverlay = null!;
        var live = new LegacyUiSettings();
        live.ItemInfoSettings.Prefix = new string('p', 40);   // replaced by the loaded (default) object
        CopySettings(doc, live);
        Check.NotNull(live.ItemInfoSettings, "item info is never null after a load");
        Check.NotNull(live.NavOverlay, "nav overlay is never null after a load");
        Check.Eq(live.ItemInfoSettings.Prefix, new MagItemInfoSettings().Prefix, "item info falls back to defaults");

        // An over-long hand-edited prefix is trimmed on load (Sanitize).
        var edited = new LegacyUiSettings();
        edited.ItemInfoSettings.Prefix = new string('p', 40);
        var live2 = new LegacyUiSettings();
        CopySettings(edited, live2);
        Check.Eq(live2.ItemInfoSettings.Prefix.Length, 32, "prefix trimmed to 32");
    }

    // ── Settings no longer reset after a bad read (2026-10-17, Silentkelpie's design) ──────────────
    // Saving used File.WriteAllText, which empties the profile first: a client killed mid-save left an
    // empty / cut-off file, the load swallowed the failed read, kept defaults and the autosave wrote
    // them over the profile. Saves now swap a temp file in and keep the previous one as <profile>.json.bak;
    // a profile that doesn't read loads the .bak, else is set aside as .unreadable-<time> and not
    // written over until something changes. Scratch folders only: ProfilesBase / MonstersBase are
    // pointed at them.

    private static string NewCharDir(string name, out LegacyDashboardRenderer dash)
    {
        string root = Path.Combine(Program.TempRoot, "profile-reset", name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        LegacyDashboardRenderer.ProfilesBase = Path.Combine(root, "SettingsProfiles");
        LegacyDashboardRenderer.MonstersBase = Path.Combine(root, "MonsterProfiles");
        RynthCore.RynthAiHostTests.Fakes.FakeHost.Reset();
        RynthCore.RynthAiHostTests.Fakes.FakeHost.WorldName = "TestWorld";
        dash = new LegacyDashboardRenderer(RynthCore.RynthAiHostTests.Fakes.FakeHost.Create());
        string dir = Path.Combine(LegacyDashboardRenderer.ProfilesBase, "TestWorld", "Tester");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LegacyUiSettings SettingsOf(LegacyDashboardRenderer dash) =>
        (LegacyUiSettings)typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.GetValue(dash)!;

    private static string Json(LegacyUiSettings s) => JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings);

    private static int ChatCount(string part)
    {
        int n = 0;
        foreach (string c in RynthCore.RynthAiHostTests.Fakes.FakeHost.Chat)
            if (c.Contains(part, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static void SaveKeepsBackup()
    {
        string dir = NewCharDir("bak", out var dash);
        dash.LoadSettings("Tester");
        LegacyUiSettings live = SettingsOf(dash);
        string path = Path.Combine(dir, "Default.json");

        live.EnableGroundLoot = false;
        dash.SaveSettings();
        string first = File.ReadAllText(path);
        Check.True(File.Exists(path) && !File.Exists(path + LegacyDashboardRenderer.BackupSuffix), "first save: the profile, no backup yet");

        live.EnableGroundLoot = true;
        dash.SaveSettings();
        string second = File.ReadAllText(path);
        Check.True(first != second, "the profile now holds the second state");
        Check.Eq(File.ReadAllText(path + LegacyDashboardRenderer.BackupSuffix), first, ".bak holds the previous profile");
        Check.Eq(Directory.GetFiles(dir, "*.tmp").Length, 0, "no temp file left behind");

        // A failing swap leaves the profile and no temp file.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            live.EnableGroundLoot = false;
            dash.SaveSettings();
        }
        Check.Eq(File.ReadAllText(path), second, "a save that can't swap in leaves the profile as it was");
        Check.Eq(Directory.GetFiles(dir, "*.tmp").Length, 0, "and no temp file");
    }

    private static void UnreadableLoadsBackup()
    {
        string dir = NewCharDir("loads-bak", out var dash);
        string path = Path.Combine(dir, "Default.json");

        // The last good save: ground loot on. Then a save cut off mid-write.
        var good = new LegacyUiSettings { EnableGroundLoot = true };
        string goodJson = Json(good);
        File.WriteAllText(path + LegacyDashboardRenderer.BackupSuffix, goodJson);
        string cut = goodJson.Substring(0, goodJson.Length / 2);
        File.WriteAllText(path, cut);

        dash.LoadSettings("Tester");
        LegacyUiSettings live = SettingsOf(dash);
        Check.True(live.EnableGroundLoot, "the backup's settings are in use");
        Check.Eq(File.ReadAllText(path), goodJson, "the backup is put back as the profile");
        Check.Eq(File.ReadAllText(path + LegacyDashboardRenderer.BackupSuffix), goodJson, "and stays as the backup");
        string[] kept = Directory.GetFiles(dir, "Default.json.unreadable-*");
        Check.Eq(kept.Length, 1, "the broken file is kept");
        Check.True(kept.Length == 1 && File.ReadAllText(kept[0]) == cut, "with its contents intact");
        Check.Eq(ChatCount("restored the backup"), 1, "chat says so");

        // The autosave after login may rewrite the profile normalized, but it still holds the backup's settings,
        // and the .bak is still a readable profile.
        dash.TickAutoSave();
        var again = JsonSerializer.Deserialize(File.ReadAllText(path), RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(again != null && again.EnableGroundLoot, "the profile after the autosave still holds the backup's settings");
        var bak = JsonSerializer.Deserialize(File.ReadAllText(path + LegacyDashboardRenderer.BackupSuffix), RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(bak != null && bak.EnableGroundLoot, "and so does the .bak (never the broken file)");
    }

    private static void UnreadableKeptNotOverwritten()
    {
        foreach (string broken in new[] { "", "{ \"EnableGroundLoot\": tru", "not json at all" })
        {
            string dir = NewCharDir("kept", out var dash);
            string path = Path.Combine(dir, "Default.json");
            File.WriteAllText(path, broken);

            dash.LoadSettings("Tester");
            LegacyUiSettings live = SettingsOf(dash);
            Check.Eq(ChatCount("didn't read"), 1, "chat says the file didn't read (" + (broken.Length == 0 ? "empty" : broken) + ")");

            // The autosave must not write the defaults over it.
            for (int i = 0; i < 3; i++) dash.TickAutoSave();
            Check.Eq(File.ReadAllText(path), broken, "the unreadable profile is untouched by the autosave");
            string[] kept = Directory.GetFiles(dir, "Default.json.unreadable-*");
            Check.Eq(kept.Length, 1, "a copy is kept");
            Check.True(kept.Length == 1 && File.ReadAllText(kept[0]) == broken, "with the original contents");
            Check.False(File.Exists(path + LegacyDashboardRenderer.BackupSuffix), "no backup was invented");

            // A real change is saved normally, and the kept copy is still there.
            live.EnableGroundLoot = !live.EnableGroundLoot;
            dash.TickAutoSave();
            string now = File.ReadAllText(path);
            Check.True(now.Length > 2 && now.Contains("EnableGroundLoot"), "once a setting changes the profile is written");
            Check.Eq(Directory.GetFiles(dir, "Default.json.unreadable-*").Length, 1, "the kept copy is still there");
        }

        // A missing profile is still a fresh character: defaults, saved at once, nothing set aside.
        string fresh = NewCharDir("fresh", out var dash2);
        dash2.LoadSettings("Tester");
        dash2.TickAutoSave();
        Check.True(File.Exists(Path.Combine(fresh, "Default.json")), "a new character's profile is created");
        Check.Eq(Directory.GetFiles(fresh, "*.unreadable-*").Length, 0, "nothing to set aside");
    }

    private static void SwitchUnreadableKept()
    {
        string dir = NewCharDir("switch", out var dash);
        dash.LoadSettings("Tester");
        LegacyUiSettings live = SettingsOf(dash);
        dash.SaveSettings();

        // 'Other' is unreadable and has no backup: kept, with the reason in chat.
        string other = Path.Combine(dir, "Other.json");
        File.WriteAllText(other, "{ broken");
        dash.LoadProfile("Other");
        Check.Eq(Directory.GetFiles(dir, "Other.json.unreadable-*").Length, 1, "the unreadable profile is kept");
        Check.Eq(ChatCount("Profile 'Other' didn't read"), 1, "and chat says so");

        // 'Third' is unreadable but has a backup: the backup is used and put back.
        var good = new LegacyUiSettings { EnableGroundLoot = true, SelectedProfile = "Third" };
        string goodJson = Json(good);
        File.WriteAllText(Path.Combine(dir, "Third.json.bak"), goodJson);
        File.WriteAllText(Path.Combine(dir, "Third.json"), "");
        live.EnableGroundLoot = false;
        dash.LoadProfile("Third");
        Check.True(SettingsOf(dash).EnableGroundLoot, "the backup's settings are loaded");
        Check.Eq(File.ReadAllText(Path.Combine(dir, "Third.json")), goodJson, "and put back as the profile");
        Check.Eq(Directory.GetFiles(dir, "Third.json.unreadable-*").Length, 1, "the empty file is kept too");
    }
}
