using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Opt-in audit (RYNTH_META_AUDIT=<folder of .met files>): what each meta asks of RynthAi - its chat
// commands through the real /vt /mt /ub dispatchers, its expression functions through the engine,
// its VTank options against the option map - and a report of what isn't supported.
internal static class MetaAuditTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        string? dir = Environment.GetEnvironmentVariable("RYNTH_META_AUDIT");
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) r.Add("audit: sample metas (RYNTH_META_AUDIT)", () => Run(dir));
    }

    private sealed class Found
    {
        public readonly SortedDictionary<string, SortedSet<string>> Commands = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, SortedSet<string>> Functions = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, SortedSet<string>> Options = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, SortedSet<string>> Kinds = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, SortedSet<string>> States = new(StringComparer.OrdinalIgnoreCase);
        public void Add(SortedDictionary<string, SortedSet<string>> d, string key, string meta)
        {
            if (!d.TryGetValue(key, out var s)) d[key] = s = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            s.Add(meta);
        }
    }

    private static readonly Regex FnRx = new(@"(?<![\$@&\w])([A-Za-z_][A-Za-z0-9_]*)\[", RegexOptions.CultureInvariant);
    private static readonly Regex CmdInExprRx = new(@"/(vt|mt|ub|ra)\s+[A-Za-z0-9_]+(?:\s+[A-Za-z0-9_]+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OptFnRx = new(@"\b(vtsetsetting|vtgetsetting|vtankgetsetting)\[\s*`?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void Run(string dir)
    {
        var f = new Found();
        var report = new StringBuilder();
        var definedStates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(dir, "*.met").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string meta = Path.GetFileName(path);
            LoadedMeta m = MetFileParser.Load(path);
            report.AppendLine($"## {meta}: {m.Rules.Count} rules, {m.EmbeddedNavs.Count} navs" + (m.Warnings.Count > 0 ? ", warnings: " + string.Join(" | ", m.Warnings) : ""));
            var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            definedStates[meta] = states;
            foreach (MetaRule rule in m.Rules) { states.Add(rule.State); Walk(rule, meta, f); }
        }

        // Chat commands through the dispatchers.
        var w = new PluginWorld();
        report.AppendLine("\n# Chat commands");
        foreach (var (cmd, metas) in f.Commands)
            report.AppendLine($"  [{Classify(cmd, w)}] {cmd}   <- {string.Join(", ", metas)}");

        // Expression functions through the engine.
        report.AppendLine("\n# Expression functions not known to RynthAi");
        FakeHost.Reset();
        var host = FakeHost.Create();
        foreach (var (fn, metas) in f.Functions)
        {
            var e = new ExpressionEngine(host);
            int logs = FakeHost.Logs.Count;
            try { e.Evaluate(fn + "[]"); } catch { }
            bool unknown = FakeHost.Logs.Skip(logs).Any(l => l.Contains("unknown expression function '" + fn, StringComparison.OrdinalIgnoreCase));
            if (unknown) report.AppendLine($"  {fn}   <- {string.Join(", ", metas)}");
        }
        report.AppendLine("  (all functions used: " + string.Join(", ", f.Functions.Keys) + ")");

        // VTank options against the option map / settings map / special options.
        report.AppendLine("\n# VTank options");
        var mm = new MetaManager(new LegacyUiSettings(), host, new PlayerVitalsCache());
        var map = (Dictionary<string, string>)typeof(MetaManager).GetField("VtOptionMap", Any)!.GetValue(null)!;
        var special = typeof(MetaManager).GetMethod("IsSpecialVtOption", Any)!;
        var settings = mm.Expressions.BuildSettingsMapPublic();
        var settingsCi = new HashSet<string>(settings.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (opt, metas) in f.Options)
        {
            bool known = map.ContainsKey(opt) || settingsCi.Contains(opt) || (bool)special.Invoke(null, new object[] { opt })!;
            report.AppendLine($"  [{(known ? "ok" : "NOT MAPPED")}] {opt}{(map.TryGetValue(opt, out string? ra) ? " -> " + ra : "")}   <- {string.Join(", ", metas)}");
        }

        // The same VTank values set two ways: '/vt opt set' and the vtsetsetting[] expression.
        report.AppendLine("\n# /vt opt set vs vtsetsetting[] (VTank units in, RynthAi setting out)");
        foreach (var (opt, val) in new[] { ("AttackDistance", "0.025"), ("ApproachDistance", "0.0208333"), ("NavCloseStopRange", "0.00625"), ("PetCustomRange", "0.0625"), ("LootOnlyRareCorpses", "1") })
        {
            var s1 = new LegacyUiSettings(); var m1 = new MetaManager(s1, host, new PlayerVitalsCache());
            m1.TryHandleVtCommand($"/vt opt set {opt} {val}");
            var s2 = new LegacyUiSettings(); var m2 = new MetaManager(s2, host, new PlayerVitalsCache());
            m2.Expressions.Evaluate($"vtsetsetting[{opt},`{val}`]");
            string Read(MetaManager mm2) => (string?)typeof(MetaManager).GetMethod("GetVtOption", Any)!.Invoke(mm2, new object[] { opt }) ?? "(null)";
            report.AppendLine($"  {opt}={val}: /vt opt set -> {Read(m1)}   vtsetsetting -> {Read(m2)}   (MonsterRange {s1.MonsterRange} vs {s2.MonsterRange}, ApproachRange {s1.ApproachRange} vs {s2.ApproachRange}, CustomPetRange {s1.CustomPetRange} vs {s2.CustomPetRange})");
        }

        report.AppendLine("\n# Rule kinds used");
        foreach (var (k, metas) in f.Kinds) report.AppendLine($"  {k}   <- {string.Join(", ", metas)}");

        report.AppendLine("\n# States set or called but not defined");
        foreach (var (st, metas) in f.States)
            foreach (string meta in metas)
                if (definedStates.TryGetValue(meta, out var defined) && !defined.Contains(st)) report.AppendLine($"  {meta}: '{st}'");

        string outPath = Path.Combine(Path.GetTempPath(), "meta_audit.txt");
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine("    audit report: " + outPath);
        Check.True(true, "audit ran");
    }

    private static void Walk(MetaRule r, string meta, Found f)
    {
        f.Add(f.Kinds, "if " + r.Condition, meta);
        if (r.Condition == MetaConditionType.Expression) Expr(r.ConditionData, meta, f);
        foreach (MetaRule c in r.Children) WalkCondition(c, meta, f);
        Action(r, meta, f);
    }

    private static void WalkCondition(MetaRule c, string meta, Found f)
    {
        f.Add(f.Kinds, "if " + c.Condition, meta);
        if (c.Condition == MetaConditionType.Expression) Expr(c.ConditionData, meta, f);
        foreach (MetaRule cc in c.Children) WalkCondition(cc, meta, f);
    }

    private static void Action(MetaRule r, string meta, Found f)
    {
        f.Add(f.Kinds, "do " + r.Action, meta);
        string d = r.ActionData ?? "";
        switch (r.Action)
        {
            case MetaActionType.ChatCommand:
                foreach (string line in d.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string cmd = line.Trim();
                    f.Add(f.Commands, cmd, meta);
                    Match om = Regex.Match(cmd, @"^/vt\s+opt\s+(?:set|get)\s+(\S+)", RegexOptions.IgnoreCase);
                    if (om.Success) f.Add(f.Options, om.Groups[1].Value, meta);
                }
                break;
            case MetaActionType.ExpressionAction:
            case MetaActionType.ChatExpression:
                Expr(d, meta, f);
                break;
            case MetaActionType.SetRAOption:
                { string[] p = d.Split(';', 2); f.Add(f.Options, p[0].Trim(), meta); if (p.Length > 1) Expr(p[1], meta, f); }
                break;
            case MetaActionType.GetRAOption:
                { string[] p = d.Split(';'); if (p.Length > 1) f.Add(f.Options, p[1].Trim(), meta); }
                break;
            case MetaActionType.SetMetaState:
            case MetaActionType.CallMetaState:
                f.Add(f.States, d.Split(';')[0].Trim(), meta);
                if (r.CallReturnState.Length > 0) f.Add(f.States, r.CallReturnState, meta);
                break;
        }
        foreach (MetaRule c in r.ActionChildren) Action(c, meta, f);
    }

    private static void Expr(string? text, string meta, Found f)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (Match m in FnRx.Matches(text)) f.Add(f.Functions, m.Groups[1].Value.ToLowerInvariant(), meta);
        string plain = text.Replace("\\/", "/").Replace("\\", "");
        foreach (Match m in CmdInExprRx.Matches(plain)) f.Add(f.Commands, m.Value.Trim() + "   (in an expression)", meta);
        foreach (Match m in OptFnRx.Matches(text)) f.Add(f.Options, m.Groups[2].Value, meta);
        Match setopt = Regex.Match(plain, @"/vt\s+opt\s+set\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        if (setopt.Success) f.Add(f.Options, setopt.Groups[1].Value, meta);
    }

    /// <summary>A RynthAi plugin wired enough to run its /mt and /ub dispatchers, plus a meta manager for /vt.</summary>
    private sealed class PluginWorld
    {
        public readonly RynthAiPlugin P = new();
        public readonly MetaManager M;
        public readonly HashSet<string> RaVerbs;

        public PluginWorld()
        {
            FakeHost.Reset();
            FakeHost.PluginCommandCalls = true;
            FakeHost.Create(playerId: 0x50000F99);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var s = new LegacyUiSettings();
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
            Set("_dashboard", dash);
            Set("_initialized", true);
            Set("_loginComplete", true);
            M = new MetaManager(s, FakeHost.Create(playerId: 0x50000F99), new PlayerVitalsCache());
            M.SetRaCommandHandler(_ => { });
            RaVerbs = ReadRaVerbs();
        }

        private void Set(string n, object? v) => typeof(RynthAiPlugin).GetField(n, Any)!.SetValue(P, v);
        public bool Meta(string cmd) => (bool)typeof(RynthAiPlugin).GetMethod("HandleMetaChatCommand", Any)!.Invoke(P, new object[] { cmd })!;

        /// <summary>The /ra verbs: the case labels of DispatchRaCommand's switch (unknown ones are ignored silently).</summary>
        private static HashSet<string> ReadRaVerbs()
        {
            var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? root = AppContext.BaseDirectory;
            while (root != null && !Directory.Exists(Path.Combine(root, "Plugins"))) root = Path.GetDirectoryName(root);
            if (root == null) return verbs;
            string src = File.ReadAllText(Path.Combine(root, "Plugins", "RynthCore.Plugin.RynthAi", "RynthAiPlugin.cs"));
            int a = src.IndexOf("private void DispatchRaCommand(", StringComparison.Ordinal);
            int b = a >= 0 ? src.IndexOf("\n    }\n", a, StringComparison.Ordinal) : -1;
            string body = a >= 0 ? src.Substring(a, (b > a ? b : src.Length) - a) : "";
            foreach (Match m in Regex.Matches(body, "case \"([a-z0-9_]+)\"")) verbs.Add(m.Groups[1].Value);
            return verbs;
        }
    }

    private static string Classify(string cmd, PluginWorld w)
    {
        // A command an expression builds: its arguments only exist at run time, so only the
        // verb was captured; running it bare says nothing.
        if (cmd.EndsWith("(in an expression)", StringComparison.Ordinal)) return "built at run time (verb only)";
        string c = cmd.Trim();
        if (!c.StartsWith("/") && !c.StartsWith("@")) return "say (plain chat)";
        string[] parts = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string prefix = parts[0].ToLowerInvariant();
        string verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        try
        {
            switch (prefix)
            {
                case "/vt":
                    return w.M.TryHandleVtCommand(c) ? "vt ok" : "VT NOT HANDLED";
                case "/mt":
                case "/ub":
                    int chat = FakeHost.Chat.Count;
                    bool ok = w.Meta(c);
                    bool noop = FakeHost.Chat.Skip(chat).Any(l => l.Contains("no RynthAi equivalent"));
                    if (prefix == "/ub" && (bool)typeof(RynthAiPlugin).GetMethod("IsUbRythaiCommand", Any)!.Invoke(null, new object[] { verb, parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2) : "" })!)
                        return "ub-Rythai";
                    return !ok ? prefix.ToUpperInvariant().TrimStart('/') + " NOT HANDLED" : noop ? "accepted no-op" : prefix.TrimStart('/') + " ok";
                case "/ra":
                    return w.RaVerbs.Contains(verb) || verb.StartsWith("jump") ? "ra ok" : "RA verb not in DispatchRaCommand (may be a hotkey verb)";
                default:
                    return "game/server";
            }
        }
        catch (Exception ex) { return "THREW " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
    }
}
