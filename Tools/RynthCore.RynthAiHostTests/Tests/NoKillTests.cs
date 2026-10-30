using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.RynthAiTests.Harness;
using C = RynthCore.Plugin.RynthAi.LegacyUi.MetaConditionType;
using A = RynthCore.Plugin.RynthAi.LegacyUi.MetaActionType;

namespace RynthCore.RynthAiHostTests.Tests;

// "No kills within X minutes" (2026-10-06): the plugin's KillClock, the expression
// secondssincelastkill[] and the meta condition No Kills Within Minutes (.af NoKillsInMins).
// The kill clock and MetaManager each run on a fake clock, moved together by Rig.Advance.
internal static class NoKillTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 2, 0, 0);

    public static void Register(Runner r)
    {
        r.Add("no-kill: secondssincelastkill[] counts from login, then the later of the last kill and the last macro start", FunctionClock);
        r.Add("no-kill: only KillerNotification counts, not death messages in chat", OnlyOurKills);
        r.Add("no-kill: the condition holds from N minutes without a kill", ConditionThreshold);
        r.Add("no-kill: bad minutes or no clock never hold", ConditionFailsClosed);
        r.Add("no-kill: NoKillsInMins survives an .af save and reload (also nested)", AfRoundTrip);
        r.Add("no-kill: survives the settings JSON; an unknown condition number loads and never holds", JsonRoundTrip);
        r.Add("no-kill: an unknown .af keyword loads as Never (how an older RynthAi reads NoKillsInMins)", UnknownKeywordFailsClosed);
        r.Add("no-kill: an editor save on a running meta doesn't re-fire it or reset the clock", AutosaveKeepsIt);
        r.Add("no-kill: the engine Meta editor's vocabulary matches MetaSchema", () => EngineVocabularyMatches());
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { IsMacroRunning = true, EnableMeta = true, CurrentState = "Default" };
        public readonly MetaManager M;
        public readonly KillClock K;
        public DateTime Now = T0;
        public long Ms;

        public Rig(bool withClock = true, params MetaRule[] rules)
        {
            S.MetaRules = rules.ToList();
            M = new MetaManager(S, default, new PlayerVitalsCache()) { Clock = () => Now };
            K = new KillClock(() => Ms) { MacroRunning = () => S.IsMacroRunning };
            if (withClock) M.SetKillClock(K);
        }

        public void Advance(double seconds)
        {
            Now = Now.AddSeconds(seconds);
            Ms += (long)Math.Round(seconds * 1000);
        }

        public void Tick(double seconds = 0)
        {
            Advance(seconds);
            M.Think();
        }

        public string Eval(string expr) => M.Expressions.Evaluate(expr);
        public string Var(string name) => M.Expressions.Variables.TryGetValue(name, out string? v) ? v : "(unset)";

        /// <summary>One freshly armed tick with only this condition; did it fire? Time does not move.</summary>
        public bool Holds(MetaRule cond)
        {
            cond.State = S.CurrentState;
            cond.Action = A.ExpressionAction;
            cond.ActionData = "setvar[__hit, 1]";
            S.MetaRules = new List<MetaRule> { cond };
            M.Expressions.Evaluate("clearvar[__hit]");
            S.ForceStateReset = true;
            M.Think();
            return M.Expressions.Variables.ContainsKey("__hit");
        }

        public bool Holds(string minutes) => Holds(NoKills(minutes));
    }

    private static MetaRule NoKills(string minutes) => new() { Condition = C.NoKillsWithinMinutes, ConditionData = minutes };

    private static MetaRule Mark(string state, C cond, string data, string v) => new()
    {
        State = state, Condition = cond, ConditionData = data,
        Action = A.ExpressionAction, ActionData = $"setvar[{v}, getvar[{v}]+1]",
    };

    // ── secondssincelastkill[] ────────────────────────────────────────────

    private static void FunctionClock()
    {
        var rig = new Rig();
        rig.S.IsMacroRunning = false;
        rig.Advance(5);
        Check.Eq(rig.Eval("secondssincelastkill[]"), "5", "macro never started: from login");
        Check.Eq(rig.K.Since, "login", "basis: login");
        Check.Eq(rig.K.SecondsSinceKillOnly(), -1, "status feed: no kill yet");

        rig.S.IsMacroRunning = true;
        rig.Advance(5);                              // started at 10 s; nothing read until now
        Check.Eq(rig.Eval("secondssincelastkill[]"), "0", "macro just started: from the start");
        rig.Advance(2.5);
        Check.Eq(rig.Eval("secondssincelastkill[]"), "2", "whole seconds (2.5 -> 2)");
        Check.Eq(rig.K.Since, "macro start", "basis: macro start");

        // A start between reads is still seen: the plugin tick observes the macro every tick.
        rig.S.IsMacroRunning = false;
        rig.K.ObserveMacro(false);
        rig.Advance(10);
        rig.S.IsMacroRunning = true;
        rig.K.ObserveMacro(true);                     // restarted at 22.5 s
        rig.Advance(3);
        Check.Eq(rig.Eval("secondssincelastkill[]"), "3", "a restart before any kill restarts the count");

        rig.K.RecordKill();                           // kill at 25.5 s
        rig.Advance(1.999);
        Check.Eq(rig.Eval("secondssincelastkill[]"), "1", "after a kill: since the kill");
        Check.Eq(rig.K.Since, "kill", "basis: kill");
        Check.Eq(rig.K.SecondsSinceKillOnly(), 1, "status feed: since the kill");

        rig.S.IsMacroRunning = false;
        rig.K.ObserveMacro(false);
        rig.Advance(8);
        rig.S.IsMacroRunning = true;
        rig.Advance(1);
        Check.Eq(rig.Eval("secondssincelastkill[]"), "0", "a macro restart after a kill starts the count again");
        Check.Eq(rig.K.Since, "macro start", "basis: macro start");
        rig.Advance(10);
        Check.Eq(rig.Eval("secondssincelastkill[] >= 10"), "1", "usable in a comparison");
        Check.Eq(rig.Eval("SecondsSinceLastKill[]"), "10", "any case");

        rig.K.SessionStarted();                       // a new login
        rig.Advance(4);
        Check.Eq(rig.Eval("secondssincelastkill[]"), "4", "a new login forgets the old kill (macro already on: counts from login)");
        Check.False(rig.K.HasKill, "no kill this session");

        var none = new Rig(withClock: false);
        none.Advance(600);
        Check.Eq(none.Eval("secondssincelastkill[]"), "0", "no clock wired: 0 (never 'a long time')");
    }

    // ── Only our kills ────────────────────────────────────────────────────

    private static void OnlyOurKills()
    {
        var p = new RynthAiPlugin();
        KillClock k = p.Kills;
        int eat = 0;
        foreach (string line in new[]
        {
            "You killed the Drudge Skulker!",
            "Drudge Skulker died.",
            "Your fellow Bob killed the Mite Scion!",
            "The Olthoi Soldier was killed by Bob.",
            "You have been slain!",
        })
            p.OnChatWindowText(line, 0, ref eat);
        Check.False(k.HasKill, "death messages in chat don't count as our kill");

        p.OnKillNotification("");
        p.OnKillNotification(null);
        Check.False(k.HasKill, "an empty notification isn't a kill");

        p.OnKillNotification("You killed the Drudge Skulker!");
        Check.True(k.HasKill, "KillerNotification (sent to the killer only) is our kill");
        Check.Eq(k.Since, "kill", "the clock counts from it");
    }

    // ── The condition ─────────────────────────────────────────────────────

    private static void ConditionThreshold()
    {
        var rig = new Rig();
        rig.Eval("secondssincelastkill[]");          // the macro start is seen at 0
        rig.Advance(59.999);
        Check.False(rig.Holds("1"), "59.999 s: not 1 minute yet");
        rig.Advance(0.001);
        Check.True(rig.Holds("1"), "60 s without a kill (from the macro start): holds");
        Check.True(rig.Holds("0.5"), "decimal minutes (0.5 = 30 s)");
        Check.True(rig.Holds("0,5"), "a comma decimal too");
        Check.True(rig.Holds(" 1 "), "spaces ignored");
        Check.False(rig.Holds("1.5"), "90 s: not yet");
        Check.True(rig.Holds("0"), "0 minutes: always");

        rig.K.RecordKill();
        Check.False(rig.Holds("1"), "right after a kill: no");
        Check.False(rig.Holds("0.01"), "0.01 min, right after a kill: no");
        rig.Advance(119.999);
        Check.False(rig.Holds("2"), "just under 2 minutes after the kill");
        rig.Advance(0.001);
        Check.True(rig.Holds("2"), "2 minutes after the kill");
        Check.Eq(rig.Eval("secondssincelastkill[]"), "120", "same clock as the function");

        // Nested like any other condition.
        Check.True(rig.Holds(new MetaRule { Condition = C.All, Children = { NoKills("1"), new MetaRule { Condition = C.Always } } }), "inside All");
        Check.False(rig.Holds(new MetaRule { Condition = C.Not, Children = { NoKills("1") } }), "Not of it");

        // A rule fires once per state entry, like every condition.
        var latch = new Rig(true, Mark("Default", C.NoKillsWithinMinutes, "1", "n"));
        latch.Tick();
        latch.Tick(30);
        Check.Eq(latch.Var("n"), "(unset)", "not before a minute");
        latch.Tick(30);
        latch.Tick(30);
        Check.Eq(latch.Var("n"), "1", "fired once at a minute, latched after");
    }

    private static void ConditionFailsClosed()
    {
        var rig = new Rig();
        rig.Eval("secondssincelastkill[]");
        rig.Advance(3600);
        foreach (string bad in new[] { "", "   ", "abc", "-1", "5m", "NaN", "Infinity" })
            Check.False(rig.Holds(bad), $"'{bad}' never holds (an hour without a kill)");
        Check.True(rig.Holds("5"), "a real number does");

        var none = new Rig(withClock: false);
        none.Advance(3600);
        Check.False(none.Holds("0"), "no kill clock wired: never holds");
    }

    // ── Files ─────────────────────────────────────────────────────────────

    private static void AfRoundTrip()
    {
        var rules = new List<MetaRule>
        {
            new() { State = "Default", Condition = C.NoKillsWithinMinutes, ConditionData = "2.5", Action = A.SetMetaState, ActionData = "Move" },
            new()
            {
                State = "Default", Condition = C.All, Action = A.ChatCommand, ActionData = "/say quiet",
                Children = { NoKills("5"), new MetaRule { Condition = C.NavrouteEmpty } },
            },
            new() { State = "Move", Condition = C.Not, Children = { NoKills("10") }, Action = A.SetMetaState, ActionData = "Default" },
        };
        string text = AfFileWriter.SaveToString(rules, new Dictionary<string, List<string>>());
        Check.True(text.Contains("NoKillsInMins 2.5"), "written as 'NoKillsInMins 2.5'");
        Check.False(text.Contains("Expr"), "not written as an expression");

        LoadedMeta back = AfFileParser.LoadFromText(text);
        Check.Eq(back.Warnings.Count, 0, "loads without warnings");
        Check.Eq(back.Rules.Count, 3, "three rules back");
        MetaRule? top = back.Rules.FirstOrDefault(r => r.Condition == C.NoKillsWithinMinutes);
        Check.NotNull(top, "the top-level condition is back");
        if (top != null) Check.Eq(top.ConditionData, "2.5", "with its minutes");
        MetaRule? all = back.Rules.FirstOrDefault(r => r.Condition == C.All);
        Check.True(all != null && all.Children.Count == 2 && all.Children[0].Condition == C.NoKillsWithinMinutes
            && all.Children[0].ConditionData == "5", "inside All");
        MetaRule? not = back.Rules.FirstOrDefault(r => r.Condition == C.Not);
        Check.True(not != null && not.Children.Count == 1 && not.Children[0].Condition == C.NoKillsWithinMinutes
            && not.Children[0].ConditionData == "10", "under Not (same-line operand)");

        string again = AfFileWriter.SaveToString(back.Rules, new Dictionary<string, List<string>>());
        Check.Eq(again, text, "a second save is identical");
    }

    private static void JsonRoundTrip()
    {
        var s = new LegacyUiSettings();
        s.MetaRules.Add(new MetaRule { State = "Default", Condition = C.NoKillsWithinMinutes, ConditionData = "3", Action = A.ChatCommand, ActionData = "/say hi" });
        string json = JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings);
        LegacyUiSettings? back = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(back != null && back.MetaRules.Count == 1 && back.MetaRules[0].Condition == C.NoKillsWithinMinutes
            && back.MetaRules[0].ConditionData == "3", "the rule survives the settings JSON");
        Check.True(Regex.IsMatch(json, "\"Condition\":\\s*" + (int)C.NoKillsWithinMinutes), "stored as its number, like every condition");

        // A condition number this build doesn't know (what an older RynthAi sees for 35): it loads
        // (System.Text.Json doesn't range-check enums) and the rule never fires.
        string future = Regex.Replace(json, "\"Condition\":\\s*" + (int)C.NoKillsWithinMinutes, "\"Condition\": 99");
        LegacyUiSettings? f = JsonSerializer.Deserialize(future, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(f != null && f.MetaRules.Count == 1 && (int)f.MetaRules[0].Condition == 99, "an unknown number loads");
        var rig = new Rig();
        rig.Eval("secondssincelastkill[]");
        rig.Advance(3600);
        Check.False(rig.Holds(new MetaRule { Condition = (C)99, ConditionData = "3" }), "and never holds");
        Check.Eq(MetaSchema.ConditionKeyword((C)99), "Never", "an .af save writes it as Never (never true)");
    }

    private static void UnknownKeywordFailsClosed()
    {
        // An older RynthAi's AfFileParser has no NoKillsInMins: unknown keywords load as Never with a
        // warning (same code path, before 2026-10-06). Simulated with a keyword this build doesn't know.
        var rules = new List<MetaRule>
        {
            new() { State = "Default", Condition = C.NoKillsWithinMinutes, ConditionData = "1", Action = A.ExpressionAction, ActionData = "setvar[x, 1]" },
        };
        string text = AfFileWriter.SaveToString(rules, new Dictionary<string, List<string>>()).Replace("NoKillsInMins", "NoKillsInMinsV2");
        LoadedMeta back = AfFileParser.LoadFromText(text);
        Check.Eq(back.Rules.Count, 1, "the rule still loads");
        Check.Eq(back.Rules[0].Condition, C.Never, "as Never");
        Check.True(back.Warnings.Any(w => w.Contains("NoKillsInMinsV2")), "with a warning naming the keyword");

        var rig = new Rig(true, back.Rules.ToArray());
        rig.Tick();
        rig.Tick(3600);
        Check.Eq(rig.Var("x"), "(unset)", "and never fires");
    }

    // ── Editor autosave (MetaRuleEdits, update_rule) ─────────────────────

    private static void AutosaveKeepsIt()
    {
        var rig = new Rig(true,
            Mark("Default", C.NoKillsWithinMinutes, "1", "nk"),
            Mark("Default", C.NoKillsWithinMinutes, "5", "late"));
        rig.Tick();
        rig.Tick(61);
        Check.Eq(rig.Var("nk"), "1", "fired after a minute without a kill");
        long before = rig.K.ElapsedMs();

        // The editor's autosave: an edited copy of rule 0 (new minutes), then the structural bump.
        Check.True(MetaRuleEdits.Replace(rig.S.MetaRules, 0, Mark("Default", C.NoKillsWithinMinutes, "0.5", "nk")), "replaced");
        rig.S.MetaRulesStructuralVersion++;
        rig.Tick();
        Check.Eq(rig.Var("nk"), "1", "the saved rule did not fire again (latch kept)");
        Check.Eq(rig.K.ElapsedMs(), before, "the kill clock didn't move");
        Check.Eq(rig.S.MetaRules[0].ConditionData, "0.5", "the new minutes are in");

        // An unfired rule saved with a lower threshold fires on the same clock (nothing restarted).
        Check.Eq(rig.Var("late"), "(unset)", "the 5-minute rule hasn't fired at 61 s");
        MetaRuleEdits.Replace(rig.S.MetaRules, 1, Mark("Default", C.NoKillsWithinMinutes, "1", "late"));
        rig.S.MetaRulesStructuralVersion++;
        rig.Tick();
        Check.Eq(rig.Var("late"), "1", "lowered to 1 minute: fires now, the 61 s weren't reset");
        Check.Eq(rig.S.CurrentState, "Default", "state unchanged");
    }

    // ── Engine vocabulary drift ───────────────────────────────────────────

    private static void EngineVocabularyMatches([CallerFilePath] string here = "")
    {
        // The engine's MetaVocabulary (RynthCore UI/Data/MetaData.cs) is indexed by the enum number
        // and can't reference the plugin; read its arrays from the side-by-side engine source.
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "RynthCore",
            "src", "RynthCore.Engine", "UI", "Data", "MetaData.cs"));
        if (!File.Exists(path))
        {
            Check.True(false, $"engine source not found at {path} (RynthCore must sit beside RynthSuite)");
            return;
        }
        string src = File.ReadAllText(path);
        string[] keywords = ArrayIn(src, "ConditionKeywords");
        string[] names = ArrayIn(src, "ConditionNames");
        string[] hints = ArrayIn(src, "ConditionHints");
        Check.Eq(keywords.Length, MetaSchema.Conditions.Length, "as many engine keywords as conditions");
        Check.Eq(names.Length, MetaSchema.Conditions.Length, "as many engine names as conditions");
        Check.Eq(hints.Length, MetaSchema.Conditions.Length, "as many engine hints as conditions");
        for (int i = 0; i < Math.Min(keywords.Length, MetaSchema.Conditions.Length); i++)
            Check.Eq(keywords[i], MetaSchema.Conditions[i].Keyword, $"keyword {i}");
        int nk = (int)C.NoKillsWithinMinutes;
        Check.Eq(nk, 35, "No Kills Within Minutes is 35 (engine MetaRuleEdit.NoKillsWithinMinutes)");
        if (names.Length > nk) Check.Eq(names[nk], MetaSchema.Conditions[nk].Label, "same label in the editor");
    }

    private static string[] ArrayIn(string src, string name)
    {
        int i = src.IndexOf("string[] " + name, StringComparison.Ordinal);
        if (i < 0) return Array.Empty<string>();
        int j = src.IndexOf("};", i, StringComparison.Ordinal);
        string body = Regex.Replace(src.Substring(i, j - i), "//[^\n]*", "");
        int open = body.IndexOf('{');
        return Regex.Matches(body.Substring(open), "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToArray();
    }
}
