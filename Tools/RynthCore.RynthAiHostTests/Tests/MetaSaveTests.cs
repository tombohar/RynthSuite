using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-01, "still having trouble editing metas and the changes not saving". Meta edits go:
// the engine's Meta panel -> RynthPluginSendMetaCommand -> HandleMetaCommand -> DrainMetaCommands ->
// ApplyMetaCommand -> MetaSaver (or RynthAi's own Macro Rules window -> TryAutoSaveMeta -> MetaSaver).
// Before: no save logged anything, a save that didn't happen (no file, a stale rule index, a command
// that threw) returned silently, and at login a meta ran the copy of its rules kept in the settings
// profile, not its file. These tests drive the commands in the engine's wire format (MetaData.cs
// MetaCmd and MetaRuleDto.cs: PascalCase, LastFiredMs included) on scratch files only.
internal static class MetaSaveTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private const string TwoRules =
        "STATE: {Default} ~~ {\n\tIF:\tAlways\n\t\tDO:\tChat {/say one}\n\tIF:\tNever\n\t\tDO:\tChat {/say two}\n~~ }\n";

    public static void Register(Runner r)
    {
        r.Add("meta save: an engine update_rule rewrites the .af and logs it once (path, rule count)", EngineUpdateSaves);
        r.Add("meta save: a write that fails says why (status, log, snapshot) and the next edit saves", FailedWriteSays);
        r.Add("meta save: an edit to a meta with no file is refused with the reason, not silently", NoFileRefused);
        r.Add("meta save: an update for a rule index the meta doesn't have is refused and logged", StaleIndexRefused);
        r.Add("meta save: a command that doesn't read is logged, not swallowed", GarbageCommandLogged);
        r.Add("meta save: Save As (save_file) writes, logs and switches to the new file", SaveAsSwitches);
        r.Add("meta save: the Macro Rules window's autosave shows Saved / NOT saved in its status line", LegacyStatusLine);
        r.Add("meta save: at login the meta runs its file's rules, not the profile's old copy", LoginUsesFile);
        r.Add("meta save: an edit after login is written on top of the newer file, not over it", EditAfterLoginKeepsFile);
        r.Add("meta save: a profile whose last meta save failed keeps its copy at login and says so", UnsavedProfileCopyKept);
        r.Add("meta save: a missing meta file at login keeps the profile's copy (logged)", MissingFileKeepsCopy);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Dir(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "meta-save", name);
        if (Directory.Exists(dir))
        {
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LegacyMetaUi MetaUi(LegacyDashboardRenderer dash) =>
        (LegacyMetaUi)typeof(LegacyDashboardRenderer).GetField("_metaUi", Any)!.GetValue(dash)!;

    private static MetaSaver Saver(LegacyDashboardRenderer dash) =>
        (MetaSaver)typeof(LegacyDashboardRenderer).GetField("_metaSaver", Any)!.GetValue(dash)!;

    /// <summary>A dashboard with <c>farm.af</c> (two rules) loaded from a scratch folder.</summary>
    private static string NewMeta(string name, out LegacyDashboardRenderer dash)
    {
        string path = Path.Combine(Dir(name), "farm.af");
        File.WriteAllText(path, TwoRules);
        FakeHost.Reset();
        DataWriteGuard.Reset();
        dash = new LegacyDashboardRenderer(FakeHost.Create());
        var ui = MetaUi(dash);
        Check.True(ui.LoadMacroFile(path), "loads: " + ui.LastLoadStatus);
        return path;
    }

    private static string RuleJson(string say, string state = "Default") =>
        "{\"State\":\"" + state + "\",\"Condition\":1,\"ConditionData\":\"\",\"Action\":1,\"ActionData\":\"" + say + "\"," +
        "\"Children\":[],\"ActionChildren\":[],\"Enabled\":true,\"Name\":\"\",\"LastFiredMs\":99999}";

    private static string Cmd(string op, int index = -1, string? rule = null, string path = "") =>
        "{\"Op\":\"" + op + "\",\"Index\":" + index + ",\"Value\":\"\",\"Path\":\"" + path.Replace("\\", "\\\\") + "\",\"Text\":\"\"" +
        (rule != null ? ",\"Rule\":" + rule : "") + "}";

    private static void Send(LegacyDashboardRenderer dash, string json)
    {
        dash.HandleMetaCommand(json);
        dash.DrainMetaCommands();
    }

    private static int LogCount(string part)
    {
        int n = 0;
        foreach (string l in FakeHost.Logs)
            if (l.Contains(part, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static string LogWith(string part)
    {
        foreach (string l in FakeHost.Logs)
            if (l.Contains(part, StringComparison.OrdinalIgnoreCase)) return l;
        return "(no log line with '" + part + "'; log: " + string.Join(" | ", FakeHost.Logs) + ")";
    }

    /// <summary>The snapshot's saveResult, as the engine panel reads it.</summary>
    private static (long Seq, bool Ok, string Text, string Path) SaveResult(LegacyDashboardRenderer dash)
    {
        using var doc = JsonDocument.Parse(dash.BuildMetaJson());
        JsonElement r = doc.RootElement.GetProperty("saveResult");
        return (r.GetProperty("seq").GetInt64(), r.GetProperty("ok").GetBoolean(),
                r.GetProperty("text").GetString() ?? "", r.GetProperty("path").GetString() ?? "");
    }

    // ── engine panel commands ────────────────────────────────────────────────

    private static void EngineUpdateSaves()
    {
        string path = NewMeta("update", out var dash);
        var loadedResult = SaveResult(dash);
        Check.True(loadedResult.Ok && loadedResult.Text.Contains("Loaded farm.af"), "the load is the last result: " + loadedResult.Text);
        long s0 = loadedResult.Seq;
        Send(dash, Cmd("update_rule", 0, RuleJson("/say edited")));
        Check.Eq(dash.Settings.MetaRules[0].ActionData, "/say edited", "applied in memory");
        Check.True(File.ReadAllText(path).Contains("/say edited"), "written to the file");
        Check.Eq(LogCount("Meta saved"), 1, "one log line for the save");
        string line = LogWith("Meta saved");
        Check.True(line.Contains(path) && line.Contains("2 rules"), "names the path and the rule count: " + line);
        var r = SaveResult(dash);
        Check.True(r.Seq == s0 + 1 && r.Ok && r.Text.Contains("Saved farm.af") && r.Path == path, $"snapshot says saved: {r}");

        Send(dash, Cmd("add_rule", -1, RuleJson("/say three")));
        Check.True(File.ReadAllText(path).Contains("/say three"), "add_rule written");
        Check.True(SaveResult(dash).Seq == s0 + 2 && LogWith("Meta saved: " + path + " (3 rules").Contains("3 rules"), "add_rule logged with the new count");

        // A move with nothing to swap still answers (the panel isn't left waiting), and writes nothing.
        int saves = LogCount("Meta saved");
        Send(dash, Cmd("move_up", 0));
        var moved = SaveResult(dash);
        Check.True(moved.Seq == s0 + 3 && moved.Ok && moved.Text.Contains("Nothing to move"), "no-op move answered: " + moved.Text);
        Check.Eq(LogCount("Meta saved"), saves, "and wrote nothing");
    }

    private static void FailedWriteSays()
    {
        string path = NewMeta("fail", out var dash);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Send(dash, Cmd("update_rule", 1, RuleJson("/say held")));
            var r = SaveResult(dash);
            Check.False(r.Ok, "snapshot says not saved");
            Check.True(r.Text.Contains("NOT saved") && r.Text.Contains("open in another program"), "says why: " + r.Text);
            Check.True(LogWith("Meta NOT saved").Contains(path), "logged with the path");
            Check.True(dash.Settings.MetaUnsaved, "the profile's copy is marked as the newer one");
        }
        Check.True(DataWriteGuard.PendingCount == 1, "and the once-per-folder chat line is queued");
        Check.False(File.ReadAllText(path).Contains("/say held"), "file untouched while held");
        Send(dash, Cmd("set_rule_enabled", 0) .Replace("\"Value\":\"\"", "\"Value\":\"true\""));
        Check.True(SaveResult(dash).Ok && File.ReadAllText(path).Contains("/say held"), "the next edit saves everything");
        Check.False(dash.Settings.MetaUnsaved, "and clears the mark");
    }

    private static void NoFileRefused()
    {
        FakeHost.Reset();
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        Check.True(MetaUi(dash).LoadMacroFile(""), "a new, empty meta");
        Send(dash, Cmd("add_rule", -1, RuleJson("/say lonely")));
        Check.Eq(dash.Settings.MetaRules.Count, 1, "the rule is added");
        var r = SaveResult(dash);
        Check.True(!r.Ok && r.Text.Contains("no file") && r.Text.Contains("Save As"), "says it has no file and what to do: " + r.Text);
        Check.True(LogCount("no file") == 1, "logged: " + LogWith("no file"));
    }

    private static void StaleIndexRefused()
    {
        string path = NewMeta("stale", out var dash);
        string before = File.ReadAllText(path);
        long s0 = SaveResult(dash).Seq;
        Send(dash, Cmd("update_rule", 5, RuleJson("/say ghost")));
        var r = SaveResult(dash);
        Check.True(!r.Ok && r.Text.Contains("rule 6 isn't in the meta"), "says which rule and why: " + r.Text);
        Check.True(LogWith("update_rule").Contains("isn't in the meta"), "logged");
        Check.Eq(File.ReadAllText(path), before, "file untouched");
        Send(dash, Cmd("delete_rule", 9));
        Check.True(SaveResult(dash).Seq == s0 + 2 && !SaveResult(dash).Ok, "delete of a missing rule is refused too");
        Check.Eq(dash.Settings.MetaRules.Count, 2, "nothing deleted");
    }

    private static void GarbageCommandLogged()
    {
        NewMeta("garbage", out var dash);
        Send(dash, "{\"Op\":\"update_rule\",\"Index\":\"zero\"}");
        var r = SaveResult(dash);
        Check.True(!r.Ok && r.Text.Contains("Edit not applied"), "said: " + r.Text);
        Check.True(LogWith("Edit not applied").Contains("Exception"), "the exception is logged: " + LogWith("Edit not applied"));
    }

    private static void SaveAsSwitches()
    {
        string path = NewMeta("saveas", out var dash);
        string copy = Path.Combine(Path.GetDirectoryName(path)!, "farm2.af");
        Send(dash, Cmd("save_file", path: copy));
        Check.True(File.Exists(copy) && File.ReadAllText(copy).Contains("/say two"), "the copy is written");
        Check.Eq(dash.Settings.CurrentMetaPath, copy, "editing continues in the copy");
        Check.True(LogWith("Meta saved: " + copy).Contains("Save As"), "logged");
        Send(dash, Cmd("update_rule", 0, RuleJson("/say in copy")));
        Check.True(File.ReadAllText(copy).Contains("/say in copy") && !File.ReadAllText(path).Contains("/say in copy"), "later edits go to the copy");
    }

    // ── RynthAi's own Macro Rules window ─────────────────────────────────────

    private static void LegacyStatusLine()
    {
        string path = NewMeta("legacy", out var dash);
        var ui = MetaUi(dash);
        MethodInfo autosave = typeof(LegacyMetaUi).GetMethod("TryAutoSaveMeta", Any)!;
        dash.Settings.MetaRules[0].ActionData = "/say window";
        autosave.Invoke(ui, null);
        Check.True(ui.StatusLine.StartsWith("Saved farm.af"), "status line: " + ui.StatusLine);
        Check.True(File.ReadAllText(path).Contains("/say window"), "written");
        Check.True(LogWith("Meta saved").Contains("Macro Rules"), "logged through the dashboard's saver");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            autosave.Invoke(ui, null);
        Check.True(ui.StatusLine.Contains("NOT saved"), "a failure shows: " + ui.StatusLine);
        Check.True(ReferenceEquals(ui.Saver, Saver(dash)), "one saver for both editors");
    }

    // ── login: the file is the meta ──────────────────────────────────────────

    /// <summary>A character whose profile names <c>farm.af</c> and holds a copy of its two rules.</summary>
    private static string ProfileWithMeta(string name, out string metaPath, Action<LegacyUiSettings>? tweak = null)
    {
        string root = Dir(name);
        LegacyDashboardRenderer.ProfilesBase = Path.Combine(root, "SettingsProfiles");
        LegacyDashboardRenderer.MonstersBase = Path.Combine(root, "MonsterProfiles");
        metaPath = Path.Combine(root, "farm.af");
        File.WriteAllText(metaPath, TwoRules);
        FakeHost.Reset();
        FakeHost.WorldName = "TestWorld";
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        dash.LoadSettings("Tester");
        Check.True(MetaUi(dash).LoadMacroFile(metaPath), "loads");
        tweak?.Invoke(dash.Settings);
        dash.SaveSettings();
        return root;
    }

    private static LegacyDashboardRenderer Relog()
    {
        FakeHost.Reset();
        FakeHost.WorldName = "TestWorld";
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        dash.LoadSettings("Tester");
        return dash;
    }

    private static void LoginUsesFile()
    {
        ProfileWithMeta("login-file", out string meta);
        // Changed elsewhere: another client, the rmeta tool, a text editor.
        File.WriteAllText(meta, TwoRules.Replace("/say two", "/say changed elsewhere"));
        var dash = Relog();
        Check.Eq(dash.Settings.CurrentMetaPath, meta, "same meta");
        Check.Eq(dash.Settings.MetaRules.Count, 2, "two rules");
        Check.Eq(dash.Settings.MetaRules[1].ActionData, "/say changed elsewhere", "the file's rules run, not the profile's old copy");
        Check.True(LogWith("differs from the copy").Contains(meta), "logged");
        dash.SaveSettings();   // what the autosave does next: the profile's copy is the file's now

        var again = Relog();
        Check.Eq(LogCount("differs from the copy"), 0, "an unchanged file loads quietly");
        Check.Eq(again.Settings.MetaRules[1].ActionData, "/say changed elsewhere", "still the file's");
    }

    private static void EditAfterLoginKeepsFile()
    {
        ProfileWithMeta("login-edit", out string meta);
        File.WriteAllText(meta, TwoRules.Replace("/say two", "/say newer file"));
        var dash = Relog();
        Send(dash, Cmd("update_rule", 0, RuleJson("/say my edit")));
        string text = File.ReadAllText(meta);
        Check.True(text.Contains("/say my edit") && text.Contains("/say newer file"), "the edit lands on the newer file: " + text);
    }

    private static void UnsavedProfileCopyKept()
    {
        ProfileWithMeta("login-unsaved", out string meta, s =>
        {
            s.MetaRules[1].ActionData = "/say edit that never reached the file";
            s.MetaUnsaved = true;
        });
        var dash = Relog();
        Check.Eq(dash.Settings.MetaRules[1].ActionData, "/say edit that never reached the file", "the profile's copy is kept");
        Check.True(LogWith("wasn't saved to the file").Contains(meta), "logged");
        Check.True(FakeHost.Chat.Exists(c => c.Contains("wasn't saved to its file")), "and said in chat");
        Send(dash, Cmd("set_rule_enabled", 0).Replace("\"Value\":\"\"", "\"Value\":\"true\""));
        Check.True(File.ReadAllText(meta).Contains("/say edit that never reached the file"), "the next save writes it");
        Check.False(dash.Settings.MetaUnsaved, "mark cleared");
    }

    private static void MissingFileKeepsCopy()
    {
        ProfileWithMeta("login-missing", out string meta);
        File.Delete(meta);
        var dash = Relog();
        Check.Eq(dash.Settings.MetaRules.Count, 2, "the profile's copy runs");
        Check.True(LogWith("isn't there").Contains(meta), "logged");
    }
}
