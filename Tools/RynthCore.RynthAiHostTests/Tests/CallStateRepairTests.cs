using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-11: CallState rules that lost their target. Before 34b7d45e (2026-09-30) the .met parser
// left a CallState's target empty and the .af writer saved it as "CallState {} {<its own state>}"
// ("{Default}" inside a DoAll); such a rule fires, calls nothing and the meta never leaves the
// state. A load now says so in chat, and Repair from .met fills the targets and return states
// from the .met of the same name, matched by order within each state (CallStateRepair).
// Everything is written under the runner's scratch folder.
internal static class CallStateRepairTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("callstate: empty targets are found, nested DoAll ones too (an old .af as written)", Detection);
        r.Add("callstate: the .met next to the .af (or above it inside MetaFiles) is found", FindsMet);
        r.Add("callstate: repair by order fills targets and return states, nested ones included", RepairMatches);
        r.Add("callstate: repair is refused, nothing changed, when a state's counts differ or it isn't in the .met", RepairRefused);
        r.Add("callstate: a repaired meta writes and parses back with its targets and return states", RoundTrip);
        r.Add("callstate: loading a broken .af warns in chat once; Repair from .met backs up, writes and reloads", LoadWarnsAndRepairs);
        r.Add("callstate: without a .met the warning says so and repair changes nothing", NoMet);
        r.Add("callstate: /ra meta repair replies either way", RepairCommand);
        string? sample = Environment.GetEnvironmentVariable("RYNTH_CALLSTATE_SAMPLE");
        if (!string.IsNullOrEmpty(sample) && Directory.Exists(sample))
            r.Add("callstate: sample metas (RYNTH_CALLSTATE_SAMPLE)", () => Sample(sample));
    }

    // ── A hand-built VTank .met ──────────────────────────────────────────

    /// <summary>One action of a .met row: VTank's action type and its data lines.</summary>
    private sealed record Act(int Type, string[] Data);

    private static Act Call(string to, string ret) => new(5, new[] { "TABLE", "2", "k", "v", "n", "n", "2", "s", "st", "s", to, "s", "ret", "s", ret });
    private static Act Chat(string text) => new(2, new[] { "s", text });
    private static Act SetState(string state) => new(1, new[] { "s", state });
    private static Act DoAll(params Act[] children)
    {
        var data = new List<string> { "TABLE", "2", "K", "V", "n", "n", children.Length.ToString() };
        foreach (Act c in children) { data.Add("i"); data.Add(c.Type.ToString()); data.AddRange(c.Data); }
        return new Act(3, data.ToArray());
    }

    /// <summary>A .met of Always rules, one per (state, action), in this order.</summary>
    private static string MetText(params (string State, Act Action)[] rows)
    {
        var lines = new List<string> { "1", "CondAct", "5", "CType", "AType", "CData", "AData", "State", "n", "n", "n", "n", "n", rows.Length.ToString() };
        foreach (var (state, act) in rows)
        {
            lines.AddRange(new[] { "i", "1", "i", act.Type.ToString(), "i", "0" });
            lines.AddRange(act.Data);
            lines.Add("s");
            lines.Add(state);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>The fixture: calls at the top, inside a DoAll and inside a DoAll inside a DoAll.</summary>
    private static string FixtureMet() => MetText(
        ("Default", SetState("20_Upper")),
        ("20_Upper", Chat("/vt nav load upper")),
        ("20_Upper", Call("90_Stone", "21_West")),
        ("21_West", DoAll(Chat("/ra start"), Call("90_Stone", "22_North"))),
        ("22_North", DoAll(Chat("/ra stop"), DoAll(Call("91_Turnin", "99_Done"), Chat("x"))) ),
        ("22_North", Call("90_Stone", "")),   // VTank's ret empty: the calling state
        ("90_Stone", new Act(6, new[] { "i", "0" })));   // Return

    private static string Scratch(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "callstate", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LoadedMeta LoadMet(string name, string text)
    {
        string path = Path.Combine(Scratch("met-" + name), name + ".met");
        File.WriteAllText(path, text);
        return MetFileParser.Load(path);
    }

    /// <summary>
    /// What an older RynthAi saved for <paramref name="met"/>: its parser left every CallState target
    /// (and return state) empty, and its writer filled the return with the rule's State. Nested DoAll
    /// children have State "Default" from the .met parser, so theirs came out as {Default}.
    /// </summary>
    private static string BrokenAfText(LoadedMeta met)
    {
        static void Clear(MetaRule r)
        {
            if (r.Action == MetaActionType.CallMetaState) { r.ActionData = ""; r.CallReturnState = ""; }
            foreach (MetaRule c in r.ActionChildren) Clear(c);
        }
        foreach (MetaRule r in met.Rules) Clear(r);
        return AfFileWriter.SaveToString(met.Rules, met.EmbeddedNavs);
    }

    private static List<MetaRule> Calls(IEnumerable<MetaRule> rules, string state)
        => CallStateRepair.CallStatesByState(rules).First(s => s.State == state).Calls;

    // ── Tests ────────────────────────────────────────────────────────────

    private static void Detection()
    {
        // As the older RynthAi wrote it (a top-level call, one in a DoAll, one in a DoAll in a DoAll).
        LoadedMeta m = AfFileParser.LoadFromText(string.Join("\n", new[]
        {
            "STATE: {20_Upper} ~~ {",
            "\tIF:\tAlways",
            "\t\tDO:\tCallState {} {20_Upper}",
            "\tIF:\tAlways",
            "\t\tDO:\tCallState {90_Stone} {21_West}",
            "~~ }",
            "STATE: {21_West} ~~ {",
            "\tIF:\tAlways",
            "\t\tDO:\tDoAll",
            "\t\t\t\t\tChat {/ra start}",
            "\t\t\t\t\tCallState {} {Default}",
            "\t\t\t\t\tDoAll",
            "\t\t\t\t\t\tCallState {} {Default}",
            "~~ }",
        }));
        Check.Eq(m.Warnings.Count, 0, "parses cleanly: " + string.Join(" | ", m.Warnings));
        Check.Eq(m.Rules.Count, 3, "three rules");
        Check.Eq(m.Rules[0].ActionData, "", "CallState {} reads as an empty target");
        Check.True(CallStateRepair.IsEmptyTarget(m.Rules[0]), "...which is an empty target");
        Check.False(CallStateRepair.IsEmptyTarget(m.Rules[1]), "a call with a target is fine");

        var empty = CallStateRepair.EmptyTargets(m.Rules);
        Check.Eq(string.Join(",", empty.Select(e => $"{e.State}={e.Count}")), "20_Upper=1,21_West=2", "per state, nested DoAll ones counted");
        Check.Eq(CallStateRepair.CountEmpty(m.Rules), 3, "three in all");
        Check.Eq(Calls(m.Rules, "21_West").Count, 2, "depth first: both nested calls");

        // A target from an expression that is only spaces is still nothing to call; other actions don't count.
        var rules = new List<MetaRule>
        {
            new() { State = "A", Action = MetaActionType.CallMetaState, ActionData = "  " },
            new() { State = "A", Action = MetaActionType.SetMetaState, ActionData = "" },
            new() { State = "A", Action = MetaActionType.All },   // an empty DoAll
        };
        Check.Eq(CallStateRepair.CountEmpty(rules), 1, "blank target counts, an empty SetState or DoAll doesn't");
        Check.Eq(CallStateRepair.CountEmpty(new List<MetaRule>()), 0, "no rules, nothing");

        // The chat line.
        string withMet = CallStateRepair.WarningLine("aeth.af", 15, @"C:\x\MetaFiles\aeth.met");
        Check.True(withMet.StartsWith("[RynthAi] aeth.af: 15 CallState rules have no state to call."), "names the file and count: " + withMet);
        Check.True(withMet.Contains("saved by an older RynthAi") && withMet.Contains("Load the original .met instead")
                   && withMet.Contains("Repair from .met") && withMet.Contains("aeth.met"), "says why and both ways out");
        string one = CallStateRepair.WarningLine("x.af", 1, null);
        Check.True(one.Contains("1 CallState rule has no state"), "singular");
        Check.True(one.Contains("No x.met is next to it") && !one.Contains("Repair from .met"), "no .met: says so, offers no repair: " + one);
    }

    private static void FindsMet()
    {
        string root = Scratch("find");
        string metas = Path.Combine(root, "MetaFiles");
        string sub = Path.Combine(metas, "Server", "Char");
        Directory.CreateDirectory(sub);

        string af = Path.Combine(metas, "Farm.af");
        Check.Null(CallStateRepair.FindSourceMet(af), "no .met: null");
        File.WriteAllText(Path.Combine(metas, "Farm.met"), "x");
        Check.Eq(CallStateRepair.FindSourceMet(af), Path.Combine(metas, "Farm.met"), "next to it");
        Check.True(CallStateRepair.FindSourceMet(Path.Combine(metas, "farm.AF")) is string s && s.EndsWith("Farm.met", StringComparison.OrdinalIgnoreCase), "names ignore case");

        string subAf = Path.Combine(sub, "Farm.af");
        Check.Eq(CallStateRepair.FindSourceMet(subAf), Path.Combine(metas, "Farm.met"), "in a subfolder of MetaFiles: MetaFiles' .met");
        File.WriteAllText(Path.Combine(sub, "Farm.met"), "x");
        Check.Eq(CallStateRepair.FindSourceMet(subAf), Path.Combine(sub, "Farm.met"), "its own folder first");

        // Outside MetaFiles only its own folder counts (nothing above MetaFiles either).
        File.WriteAllText(Path.Combine(root, "Other.met"), "x");
        string outside = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(outside);
        Check.Null(CallStateRepair.FindSourceMet(Path.Combine(outside, "Other.af")), "not from the folder above an outside .af");
        Check.Null(CallStateRepair.FindSourceMet(Path.Combine(metas, "Other.af")), "not above MetaFiles");
        Check.Null(CallStateRepair.FindSourceMet(""), "no path");
    }

    private static void RepairMatches()
    {
        LoadedMeta met = LoadMet("match", FixtureMet());
        Check.Eq(met.Warnings.Count, 0, ".met parses: " + string.Join(" | ", met.Warnings));
        Check.Eq(met.Rules.Count, 7, "seven rules");

        LoadedMeta broken = AfFileParser.LoadFromText(BrokenAfText(LoadMet("match2", FixtureMet())));
        Check.Eq(CallStateRepair.CountEmpty(broken.Rules), 4, "the old .af: all four calls empty");
        // What the old writer did to the return states.
        Check.Eq(Calls(broken.Rules, "20_Upper")[0].CallReturnState, "20_Upper", "top level: its own state");
        Check.Eq(Calls(broken.Rules, "21_West")[0].CallReturnState, "Default", "in a DoAll: Default");

        CallStateRepair.Result r = CallStateRepair.Repair(broken.Rules, met.Rules);
        Check.True(r.Ok, "repaired: " + r.Why);
        Check.Eq(r.Filled, 4, "four filled");
        Check.Eq(r.States, 3, "in three states");
        Check.Eq(CallStateRepair.CountEmpty(broken.Rules), 0, "none left");

        MetaRule up = Calls(broken.Rules, "20_Upper")[0];
        Check.Eq(up.ActionData, "90_Stone", "top level: target");
        Check.Eq(up.CallReturnState, "21_West", "top level: the .met's return state, not its own");
        MetaRule west = Calls(broken.Rules, "21_West")[0];
        Check.Eq(west.ActionData, "90_Stone", "in a DoAll: target");
        Check.Eq(west.CallReturnState, "22_North", "in a DoAll: return state (was Default)");
        var north = Calls(broken.Rules, "22_North");
        Check.Eq(north.Count, 2, "22_North: two calls");
        Check.Eq(north[0].ActionData + ">" + north[0].CallReturnState, "91_Turnin>99_Done", "in a DoAll in a DoAll, first in order");
        Check.Eq(north[1].ActionData + ">" + north[1].CallReturnState, "90_Stone>22_North", "the .met's empty return: the calling state");
        Check.True(r.Details.Any(d => d.StartsWith("22_North: 91_Turnin (return 99_Done), 90_Stone (return 22_North)")), "details name what went where: " + string.Join(" | ", r.Details));

        // A call that already has a target is left alone (hand-fixed since), the rest still filled.
        LoadedMeta partly = AfFileParser.LoadFromText(BrokenAfText(LoadMet("match3", FixtureMet())));
        MetaRule fixedByHand = Calls(partly.Rules, "22_North")[1];
        fixedByHand.ActionData = "Elsewhere";
        fixedByHand.CallReturnState = "Back";
        r = CallStateRepair.Repair(partly.Rules, met.Rules);
        Check.True(r.Ok, "partly repaired by hand: still repairs");
        Check.Eq(r.Filled, 3, "three filled");
        Check.Eq(fixedByHand.ActionData + ">" + fixedByHand.CallReturnState, "Elsewhere>Back", "the hand-set one is kept");

        // Nothing to do.
        r = CallStateRepair.Repair(partly.Rules, met.Rules);
        Check.False(r.Ok, "a second repair has nothing to do");
        Check.Eq(r.Why, "no CallState rule is missing its target", "and says so");
    }

    private static void RepairRefused()
    {
        LoadedMeta met = LoadMet("refuse", FixtureMet());

        // The .af lost a CallState in 22_North (edited since): that state's counts differ.
        LoadedMeta af = AfFileParser.LoadFromText(BrokenAfText(LoadMet("refuse2", FixtureMet())));
        af.Rules.RemoveAll(x => x.State == "22_North" && x.Action == MetaActionType.CallMetaState);
        string before = AfFileWriter.SaveToString(af.Rules, af.EmbeddedNavs);
        CallStateRepair.Result r = CallStateRepair.Repair(af.Rules, met.Rules);
        Check.False(r.Ok, "refused");
        Check.True(r.Why.Contains("state '22_North' has 1 CallState rule in the .af and 2 in the .met"), "says which state and the counts: " + r.Why);
        Check.True(r.Why.Contains("nothing was changed"), "and that nothing changed");
        Check.Eq(r.Filled, 0, "none filled");
        Check.Eq(AfFileWriter.SaveToString(af.Rules, af.EmbeddedNavs), before, "the rules are as they were (other states too)");
        Check.Eq(CallStateRepair.CountEmpty(af.Rules), 3, "the other states' calls still empty");

        // More calls in the .af than in the .met.
        af = AfFileParser.LoadFromText(BrokenAfText(LoadMet("refuse3", FixtureMet())));
        af.Rules.Add(new MetaRule { State = "20_Upper", Condition = MetaConditionType.Always, Action = MetaActionType.CallMetaState });
        r = CallStateRepair.Repair(af.Rules, met.Rules);
        Check.False(r.Ok, "an extra call: refused");
        Check.True(r.Why.Contains("state '20_Upper' has 2 CallState rules in the .af and 1 in the .met"), r.Why);

        // A state the .met doesn't have.
        af = AfFileParser.LoadFromText(BrokenAfText(LoadMet("refuse4", FixtureMet())));
        foreach (MetaRule x in af.Rules.Where(x => x.State == "21_West")) x.State = "21_Renamed";
        r = CallStateRepair.Repair(af.Rules, met.Rules);
        Check.False(r.Ok, "a renamed state: refused");
        Check.True(r.Why.Contains("state '21_Renamed' isn't in the .met"), r.Why);
        Check.Eq(CallStateRepair.CountEmpty(af.Rules), 4, "nothing filled anywhere");

        // The .met's own call is empty too (it came from somewhere else broken).
        LoadedMeta emptyMet = LoadMet("refuse5", MetText(("A", Call("", "A"))));
        var one = new List<MetaRule> { new() { State = "A", Action = MetaActionType.CallMetaState } };
        r = CallStateRepair.Repair(one, emptyMet.Rules);
        Check.False(r.Ok, "the .met has no target either: refused");
        Check.True(r.Why.Contains("CallState 1 has no target in the .met either"), r.Why);
    }

    private static void RoundTrip()
    {
        LoadedMeta met = LoadMet("roundtrip", FixtureMet());
        LoadedMeta af = AfFileParser.LoadFromText(BrokenAfText(LoadMet("roundtrip2", FixtureMet())));
        Check.True(CallStateRepair.Repair(af.Rules, met.Rules).Ok, "repaired");

        string text = AfFileWriter.SaveToString(af.Rules, af.EmbeddedNavs);
        Check.True(text.Contains("CallState {90_Stone} {21_West}"), "written: top level");
        Check.True(text.Contains("CallState {91_Turnin} {99_Done}"), "written: nested");
        Check.False(text.Contains("CallState {}"), "no empty target written");

        LoadedMeta back = AfFileParser.LoadFromText(text);
        Check.Eq(back.Warnings.Count, 0, "parses cleanly");
        Check.Eq(back.Rules.Count, af.Rules.Count, "every rule back");
        Check.Eq(CallStateRepair.CountEmpty(back.Rules), 0, "no empty targets after a reload");
        string Pairs(IEnumerable<MetaRule> rules) => string.Join(" | ",
            CallStateRepair.CallStatesByState(rules).SelectMany(s => s.Calls.Select(c => $"{s.State}:{c.ActionData}>{c.CallReturnState}")));
        Check.Eq(Pairs(back.Rules), Pairs(af.Rules), "same targets and return states");
        Check.Eq(Pairs(back.Rules),
            "20_Upper:90_Stone>21_West | 21_West:90_Stone>22_North | 22_North:91_Turnin>99_Done | 22_North:90_Stone>22_North",
            "as in the .met");
        Check.Eq(AfFileWriter.SaveToString(back.Rules, back.EmbeddedNavs), text, "writes back the same");
    }

    private static void LoadWarnsAndRepairs()
    {
        DataWriteGuard.Reset();
        string metas = Path.Combine(Scratch("load"), "MetaFiles");
        Directory.CreateDirectory(metas);
        string metPath = Path.Combine(metas, "Tower.met");
        string afPath = Path.Combine(metas, "Tower.af");
        File.WriteAllText(metPath, FixtureMet());
        File.WriteAllText(afPath, BrokenAfText(MetFileParser.Load(metPath)));
        byte[] brokenBytes = File.ReadAllBytes(afPath);
        byte[] metBytes = File.ReadAllBytes(metPath);

        var log = new List<string>();
        var settings = new LegacyUiSettings();
        var ui = new LegacyMetaUi(settings, new List<string>()) { Log = log.Add };
        Check.True(ui.LoadMacroFile(afPath), "the broken .af loads: " + ui.LastLoadStatus);
        Check.Eq(ui.PendingNotices, 1, "one chat line waits");
        var chat = new List<string>();
        ui.DrainNotices(l => { chat.Add(l); return true; });
        Check.Eq(chat.Count, 1, "one line posted");
        Check.True(chat[0].StartsWith("[RynthAi] Tower.af: 4 CallState rules have no state to call.") && chat[0].Contains("Tower.met"), "the warning: " + string.Join(" / ", chat));
        Check.Eq(ui.PendingNotices, 0, "nothing left");
        Check.True(log.Any(l => l.Contains("state '22_North' has 2 CallState rules with no state to call")), "the log names each state: " + string.Join(" / ", log));
        Check.True(log.Any(l => l.Contains("state '20_Upper' has 1 CallState rule with")), "...every one");
        Check.Eq(ui.EmptyCallStatesNow(out string? found), 4, "the window sees four");
        Check.Eq(found, metPath, "...and the .met to repair from");

        // A refused write: the host is busy (a line it doesn't take waits for the next tick).
        ui.LoadMacroFile(afPath);
        int tries = 0;
        ui.DrainNotices(_ => { tries++; return false; });
        Check.Eq(ui.PendingNotices, 1, "a refused line waits");
        ui.DrainNotices(l => { tries++; return true; });
        Check.Eq(ui.PendingNotices, 0, "and goes out on a later tick");
        Check.Eq(tries, 2, "offered once per tick");

        // An unsaved edit in memory is not thrown away by the reload.
        settings.MetaUnsaved = true;
        Check.False(ui.RepairFromMet(out string reply), "refused with an unsaved edit");
        Check.True(reply.StartsWith("[RynthAi] Can't repair Tower.af: your last change"), reply);
        settings.MetaUnsaved = false;

        settings.CurrentState = "21_West";
        Check.True(ui.RepairFromMet(out reply), "repaired: " + reply);
        Check.True(reply.StartsWith("[RynthAi] Repaired Tower.af: 4 CallState rules in 3 states filled from Tower.met, and loaded it again"), reply);
        string[] backups = Directory.GetFiles(metas, "Tower.af.bak-*");
        Check.Eq(backups.Length, 1, "one backup copy next to it");
        if (backups.Length == 1)
        {
            Check.True(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(backups[0]), @"^Tower\.af\.bak-\d{8}-\d{6}$"), "named <name>.af.bak-yyyyMMdd-HHmmss: " + backups[0]);
            Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(backups[0])).SequenceEqual(brokenBytes), "the backup is the file as it was");
            Check.True(reply.Contains(Path.GetFileName(backups[0])), "the reply names it");
        }
        Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(metPath)).SequenceEqual(metBytes), "the .met is never written");
        LoadedMeta onDisk = AfFileParser.Load(afPath);
        Check.Eq(CallStateRepair.CountEmpty(onDisk.Rules), 0, "the .af on disk has its targets");
        Check.Eq(Calls(onDisk.Rules, "21_West")[0].CallReturnState, "22_North", "...and return states");
        Check.Eq(CallStateRepair.CountEmpty(settings.MetaRules), 0, "the loaded rules are the repaired ones");
        Check.Eq(settings.CurrentMetaPath, afPath, "still the .af");
        Check.Eq(settings.CurrentState, "Default", "loaded again: starts over");
        Check.True(settings.ForceStateReset, "state reset forced");
        Check.Eq(ui.PendingNotices, 0, "the reload has nothing to warn about");
        Check.Eq(ui.EmptyCallStatesNow(out _), 0, "the window has nothing to repair");
        Check.True(ui.StatusLine.StartsWith("[RynthAi] Repaired Tower.af"), "the status line says so");
        Check.Eq(DataWriteGuard.PendingCount, 0, "no save problem reported");

        // Again: nothing to do, nothing written.
        DateTime stamp = File.GetLastWriteTimeUtc(afPath);
        Check.False(ui.RepairFromMet(out reply), "a second repair does nothing");
        Check.True(reply.StartsWith("[RynthAi] No CallState rule in Tower.af is missing its target"), reply);
        Check.Eq(File.GetLastWriteTimeUtc(afPath), stamp, "not rewritten");
        Check.Eq(Directory.GetFiles(metas, "Tower.af.bak-*").Length, 1, "no second backup");

        // A mismatch from the window: refused, file and folder unchanged.
        File.WriteAllText(afPath, BrokenAfText(MetFileParser.Load(metPath)).Replace("STATE: {22_North}", "STATE: {22_Elsewhere}"));
        byte[] mismatch = File.ReadAllBytes(afPath);
        Check.True(ui.LoadMacroFile(afPath), "a mismatched .af loads");
        Check.False(ui.RepairFromMet(out reply), "refused");
        Check.True(reply.StartsWith("[RynthAi] Can't repair Tower.af: the .met doesn't match the .af (state '22_Elsewhere' isn't in the .met)"), reply);
        Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(afPath)).SequenceEqual(mismatch), "file unchanged");
        Check.Eq(Directory.GetFiles(metas, "Tower.af.bak-*").Length, 1, "no backup made for a refusal");
        Check.True(log.Any(l => l.Contains("refused") && l.Contains("22_Elsewhere")), "the refusal is logged");

        // A .met loaded directly: nothing to check, no warning.
        Check.True(ui.LoadMacroFile(metPath), "the .met loads");
        Check.Eq(ui.EmptyCallStatesNow(out _), 0, "a .met has its targets");
        Check.False(ui.RepairFromMet(out reply), "repair refuses a .met");
        Check.True(reply.Contains("only an .af can be repaired"), reply);
    }

    private static void NoMet()
    {
        string metas = Path.Combine(Scratch("nomet"), "MetaFiles");
        Directory.CreateDirectory(metas);
        string afPath = Path.Combine(metas, "Lonely.af");
        File.WriteAllText(afPath, BrokenAfText(LoadMet("nomet-src", FixtureMet())));
        byte[] before = File.ReadAllBytes(afPath);

        var settings = new LegacyUiSettings();
        var ui = new LegacyMetaUi(settings, new List<string>());
        Check.True(ui.LoadMacroFile(afPath), "loads");
        var chat = new List<string>();
        ui.DrainNotices(l => { chat.Add(l); return true; });
        Check.Eq(chat.Count, 1, "one warning");
        Check.True(chat.Count == 1 && chat[0].Contains("4 CallState rules have no state to call") && chat[0].Contains("No Lonely.met is next to it"), "says there's no .met: " + string.Join(" / ", chat));
        Check.Eq(ui.EmptyCallStatesNow(out string? met), 4, "the window still counts them");
        Check.Null(met, "...with no .met (no Repair button)");
        Check.False(ui.RepairFromMet(out string reply), "repair refused");
        Check.True(reply.StartsWith("[RynthAi] Can't repair Lonely.af: there is no Lonely.met next to it."), reply);
        Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(afPath)).SequenceEqual(before), "file unchanged");
        Check.Eq(Directory.GetFiles(metas).Length, 1, "no backup, nothing else written");

        // A healthy .af: no warning at all.
        string good = Path.Combine(metas, "Good.af");
        File.WriteAllText(good, "STATE: {Default} ~~ {\n\tIF:\tAlways\n\t\tDO:\tCallState {B} {Default}\n~~ }\n");
        Check.True(ui.LoadMacroFile(good), "a healthy .af loads");
        Check.Eq(ui.PendingNotices, 0, "no warning for it");
    }

    private static void RepairCommand()
    {
        FakeHost.Reset();
        DataWriteGuard.Reset();
        string metas = Path.Combine(Scratch("command"), "MetaFiles");
        Directory.CreateDirectory(metas);
        string metPath = Path.Combine(metas, "Cmd.met");
        string afPath = Path.Combine(metas, "Cmd.af");
        File.WriteAllText(metPath, FixtureMet());
        File.WriteAllText(afPath, BrokenAfText(MetFileParser.Load(metPath)));

        var settings = new LegacyUiSettings();
        var navFiles = new List<string>();
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        void Set(string f, object v) => typeof(LegacyDashboardRenderer).GetField(f, Any)!.SetValue(dash, v);
        Set("_profileLoaded", true);
        Set("_settings", settings);
        Set("_host", FakeHost.Create());
        Set("_metaFolder", metas);
        Set("_navFiles", navFiles);
        Set("_metaFiles", new List<string>());
        Set("_profileListsLock", new object());
        Set("_metaUi", new LegacyMetaUi(settings, navFiles));

        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(plugin, new object[] { FakeHost.LastApi });
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(plugin, dash);
        MethodInfo dispatch = typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!;
        void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            dispatch.Invoke(plugin, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }
        string Said() => string.Join(" / ", FakeHost.Chat);

        Ra("/ra meta repair");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Can't repair the meta: no meta is loaded")), "no meta loaded: says so: " + Said());
        FakeHost.Chat.Clear();

        Ra("/ra meta load Cmd");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Loaded meta 'Cmd'")), "loaded: " + Said());
        FakeHost.Chat.Clear();
        Ra("/ra meta repair now");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Usage: /ra meta repair")), "extra words: usage: " + Said());
        FakeHost.Chat.Clear();
        Ra("/ra meta repair");
        Check.True(FakeHost.Chat.Exists(l => l.StartsWith("[RynthAi] Repaired Cmd.af: 4 CallState rules in 3 states")), "repaired: " + Said());
        Check.Eq(CallStateRepair.CountEmpty(settings.MetaRules), 0, "the loaded meta has its targets");
        FakeHost.Chat.Clear();
        Ra("/ra meta repair");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("nothing to repair")), "again: nothing to repair: " + Said());
        FakeHost.Chat.Clear();
        Ra("/ra meta");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("/ra meta") && l.Contains("| repair")), "the verb list names repair: " + Said());
    }

    /// <summary>
    /// Opt-in: a folder with real metas (copies; never the live install). For each &lt;name&gt;.af with a
    /// &lt;name&gt;.met beside it, the repair must fill every empty target; when &lt;name&gt;.reference.af is
    /// there too (repaired by hand), every state's CallState targets and return states must match it.
    /// </summary>
    private static void Sample(string dir)
    {
        string work = Scratch("sample");
        int checkedAny = 0;
        foreach (string af in Directory.GetFiles(dir, "*.af"))
        {
            if (af.EndsWith(".reference.af", StringComparison.OrdinalIgnoreCase)) continue;
            string stem = Path.GetFileNameWithoutExtension(af);
            string met = Path.Combine(dir, stem + ".met");
            if (!File.Exists(met)) continue;
            string copyAf = Path.Combine(work, stem + ".af");
            File.Copy(af, copyAf, overwrite: true);
            File.Copy(met, Path.Combine(work, stem + ".met"), overwrite: true);

            var settings = new LegacyUiSettings();
            var ui = new LegacyMetaUi(settings, new List<string>());
            Check.True(ui.LoadMacroFile(copyAf), $"{stem}: loads");
            int empty = ui.EmptyCallStatesNow(out string? found);
            Check.True(empty > 0, $"{stem}: has empty CallState targets ({empty})");
            Check.NotNull(found, $"{stem}: the .met is found");
            Check.True(ui.RepairFromMet(out string reply), $"{stem}: {reply}");
            Check.Eq(CallStateRepair.CountEmpty(AfFileParser.Load(copyAf).Rules), 0, $"{stem}: no empty target left");
            Console.WriteLine("    " + reply);

            string reference = Path.Combine(dir, stem + ".reference.af");
            if (File.Exists(reference))
            {
                static string Pairs(IEnumerable<MetaRule> rules) => string.Join("\n",
                    CallStateRepair.CallStatesByState(rules).SelectMany(s => s.Calls.Select(c => $"{s.State}:{c.ActionData}>{c.CallReturnState}")));
                Check.Eq(Pairs(AfFileParser.Load(copyAf).Rules), Pairs(AfFileParser.Load(reference).Rules), $"{stem}: matches the hand-repaired copy");
            }
            checkedAny++;
        }
        Check.True(checkedAny > 0, "found an .af with its .met in " + dir);
    }
}
