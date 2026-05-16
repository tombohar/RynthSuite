using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// Saves MetaRules to .af (metaf) format.
/// </summary>
internal static class AfFileWriter
{
    // ── RynthAi MetaConditionType → .af keyword ─────────────────────────────
    private static readonly Dictionary<MetaConditionType, string> ConditionKeywordMap = new()
    {
        [MetaConditionType.Never]                            = "Never",
        [MetaConditionType.Always]                           = "Always",
        [MetaConditionType.All]                              = "All",
        [MetaConditionType.Any]                              = "Any",
        [MetaConditionType.ChatMessage]                      = "ChatMatch",
        [MetaConditionType.PackSlots_LE]                     = "MainSlotsLE",
        [MetaConditionType.SecondsInState_GE]                = "SecsInStateGE",
        [MetaConditionType.NavrouteEmpty]                    = "NavEmpty",
        [MetaConditionType.CharacterDeath]                   = "Death",
        [MetaConditionType.AnyVendorOpen]                    = "VendorOpen",
        [MetaConditionType.VendorClosed]                     = "VendorClosed",
        [MetaConditionType.InventoryItemCount_LE]            = "ItemCountLE",
        [MetaConditionType.InventoryItemCount_GE]            = "ItemCountGE",
        [MetaConditionType.MonsterNameCountWithinDistance]    = "MobsInDist_Name",
        [MetaConditionType.MonsterPriorityCountWithinDistance]= "MobsInDist_Priority",
        [MetaConditionType.NeedToBuff]                       = "NeedToBuff",
        [MetaConditionType.NoMonstersWithinDistance]          = "NoMobsInDist",
        [MetaConditionType.Landblock_EQ]                     = "BlockE",
        [MetaConditionType.Landcell_EQ]                      = "CellE",
        [MetaConditionType.PortalspaceEntered]               = "IntoPortal",
        [MetaConditionType.PortalspaceExited]                = "ExitPortal",
        [MetaConditionType.Not]                              = "Not",
        [MetaConditionType.SecondsInStateP_GE]               = "PSecsInStateGE",
        [MetaConditionType.TimeLeftOnSpell_GE]               = "SecsOnSpellGE",
        [MetaConditionType.TimeLeftOnSpell_LE]               = "SecsOnSpellLE",
        [MetaConditionType.BurdenPercentage_GE]              = "BuPercentGE",
        [MetaConditionType.DistAnyRoutePT_GE]                = "DistToRteGE",
        [MetaConditionType.Expression]                       = "Expr",
        [MetaConditionType.ChatMessageCapture]               = "ChatCapture",
        [MetaConditionType.MainHealthLE]                     = "Expr",
        [MetaConditionType.MainHealthPHE]                    = "Expr",
        [MetaConditionType.MainManaLE]                       = "Expr",
        [MetaConditionType.MainManaPHE]                      = "Expr",
        [MetaConditionType.MainStamLE]                       = "Expr",
        [MetaConditionType.VitaePHE]                         = "Expr",
    };

    // ── RynthAi MetaActionType → .af keyword ────────────────────────────────
    private static readonly Dictionary<MetaActionType, string> ActionKeywordMap = new()
    {
        [MetaActionType.None]             = "None",
        [MetaActionType.SetMetaState]     = "SetState",
        [MetaActionType.ChatCommand]      = "Chat",
        [MetaActionType.EmbeddedNavRoute] = "EmbedNav",
        [MetaActionType.All]              = "DoAll",
        [MetaActionType.CallMetaState]    = "CallState",
        [MetaActionType.ReturnFromCall]   = "Return",
        [MetaActionType.ExpressionAction] = "DoExpr",
        [MetaActionType.ChatExpression]   = "ChatExpr",
        [MetaActionType.SetWatchdog]      = "SetWatchdog",
        [MetaActionType.ClearWatchdog]    = "ClearWatchdog",
        [MetaActionType.GetRAOption]      = "GetOpt",
        [MetaActionType.SetRAOption]      = "SetOpt",
        [MetaActionType.CreateView]       = "CreateView",
        [MetaActionType.DestroyView]      = "DestroyView",
        [MetaActionType.DestroyAllViews]  = "DestroyAllViews",
    };

    // ── Public API ──────────────────────────────────────────────────────────

    public static string SaveToString(List<MetaRule> rules,
        IReadOnlyDictionary<string, List<string>> embeddedNavs)
    {
        var sb = new System.Text.StringBuilder();
        using var writer = new StringWriter(sb);
        WriteContent(writer, rules, embeddedNavs);
        return sb.ToString();
    }

    public static void Save(string filePath, List<MetaRule> rules,
        IReadOnlyDictionary<string, List<string>> embeddedNavs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        using var writer = new StreamWriter(filePath, false);
        WriteContent(writer, rules, embeddedNavs);
    }

    private static void WriteContent(TextWriter writer, List<MetaRule> rules,
        IReadOnlyDictionary<string, List<string>> embeddedNavs)
    {
        // Group rules by state
        var grouped = rules
            .GroupBy(r => r.State)
            .OrderBy(g => g.Key)
            .ToList();

        // Collect embedded nav routes referenced by rules
        var navNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectNavNames(rules, navNames);

        // Write states
        foreach (var group in grouped)
        {
            writer.WriteLine($"STATE: {{{group.Key}}} ~~ {{");
            foreach (var rule in group)
                WriteRule(writer, rule);
            writer.WriteLine("~~ }");
        }

        // Write NAV sections for referenced nav routes (in-memory only)
        foreach (string navName in navNames)
        {
            if (embeddedNavs.TryGetValue(navName, out var content))
                WriteNavSection(writer, navName, content);
        }
    }

    // ── Rule writing ────────────────────────────────────────────────────────

    private static void WriteRule(TextWriter writer, MetaRule rule)
    {
        // Write IF: line
        writer.Write("\tIF:\t");
        WriteCondition(writer, rule, "\t\t\t\t");
        writer.WriteLine();

        // Write DO: line
        writer.Write("\t\tDO:\t");
        WriteAction(writer, rule, "\t\t\t\t\t");
        writer.WriteLine();
    }

    private static void WriteCondition(TextWriter writer, MetaRule rule, string childIndent)
    {
        string keyword = ConditionKeywordMap.TryGetValue(rule.Condition, out string? kw) ? kw : "Never";

        // Handle RynthAi-only conditions by converting to expressions
        switch (rule.Condition)
        {
            case MetaConditionType.MainHealthLE:
                writer.Write($"Expr {{getcharvital_current[2]<={rule.ConditionData}}}");
                return;
            case MetaConditionType.MainHealthPHE:
                writer.Write($"Expr {{getcharvital_current[2]*100/getcharvital_buffedmax[2]<={rule.ConditionData}}}");
                return;
            case MetaConditionType.MainManaLE:
                writer.Write($"Expr {{getcharvital_current[4]<={rule.ConditionData}}}");
                return;
            case MetaConditionType.MainManaPHE:
                writer.Write($"Expr {{getcharvital_current[4]*100/getcharvital_buffedmax[4]<={rule.ConditionData}}}");
                return;
            case MetaConditionType.MainStamLE:
                writer.Write($"Expr {{getcharvital_current[6]<={rule.ConditionData}}}");
                return;
            case MetaConditionType.VitaePHE:
                writer.Write($"Expr {{100-vitae[]*100>={rule.ConditionData}}}");
                return;
        }

        if (rule.Condition == MetaConditionType.All ||
            rule.Condition == MetaConditionType.Any ||
            rule.Condition == MetaConditionType.Not)
        {
            writer.Write(keyword);
            if (rule.Children != null)
            {
                foreach (var child in rule.Children)
                {
                    writer.WriteLine();
                    writer.Write(childIndent);
                    WriteCondition(writer, child, childIndent + "\t");
                }
            }
            return;
        }

        // Simple conditions
        string data = rule.ConditionData ?? "";
        switch (rule.Condition)
        {
            case MetaConditionType.ChatMessage:
            case MetaConditionType.Expression:
            case MetaConditionType.PackSlots_LE:
            case MetaConditionType.SecondsInState_GE:
            case MetaConditionType.SecondsInStateP_GE:
            case MetaConditionType.BurdenPercentage_GE:
            case MetaConditionType.DistAnyRoutePT_GE:
            case MetaConditionType.NoMonstersWithinDistance:
            case MetaConditionType.Landblock_EQ:
            case MetaConditionType.Landcell_EQ:
                // Bare values (no braces): MainSlotsLE 4, BlockE F6820033
                writer.Write($"{keyword} {data}");
                break;

            case MetaConditionType.ChatMessageCapture:
                writer.Write($"{keyword} {{{data}}} {{}}");
                break;

            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
            {
                // Format: ItemCountLE <count> {<name>}
                var parts = data.Split(',');
                string name = parts.Length > 0 ? parts[0] : "";
                string count = parts.Length > 1 ? parts[1] : "0";
                writer.Write($"{keyword} {count} {{{name}}}");
                break;
            }

            case MetaConditionType.MonsterNameCountWithinDistance:
            {
                // Format: MobsInDist_Name <count> <distance> {<name>}
                var parts = data.Split(',');
                string name = parts.Length > 0 ? parts[0] : "";
                string dist = parts.Length > 1 ? parts[1] : "0";
                string count = parts.Length > 2 ? parts[2] : "0";
                writer.Write($"{keyword} {count} {dist} {{{name}}}");
                break;
            }

            case MetaConditionType.MonsterPriorityCountWithinDistance:
            {
                // Format: MobsInDist_Priority <count> <distance>
                var parts = data.Split(',');
                string count = parts.Length > 0 ? parts[0] : "0";
                string dist = parts.Length > 1 ? parts[1] : "0";
                writer.Write($"{keyword} {count} {dist}");
                break;
            }

            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
            {
                // Format: SecsOnSpellGE <spell_id> <seconds>
                var parts = data.Split(',');
                string spellId = parts.Length > 0 ? parts[0] : "0";
                string secs = parts.Length > 1 ? parts[1] : "0";
                writer.Write($"{keyword} {spellId} {secs}");
                break;
            }

            default:
                // No-data conditions
                writer.Write(keyword);
                break;
        }
    }

    private static void WriteAction(TextWriter writer, MetaRule rule, string childIndent)
    {
        string keyword = ActionKeywordMap.TryGetValue(rule.Action, out string? kw) ? kw : "None";

        if (rule.Action == MetaActionType.All)
        {
            writer.Write(keyword);
            var children = rule.ActionChildren ?? rule.Children ?? new List<MetaRule>();
            foreach (var child in children)
            {
                writer.WriteLine();
                writer.Write(childIndent);
                WriteAction(writer, child, childIndent + "\t");
            }
            return;
        }

        string data = rule.ActionData ?? "";

        switch (rule.Action)
        {
            case MetaActionType.SetMetaState:
            case MetaActionType.ChatCommand:
            case MetaActionType.ExpressionAction:
            case MetaActionType.ChatExpression:
            case MetaActionType.DestroyView:
                writer.Write($"{keyword} {{{data}}}");
                break;

            case MetaActionType.CallMetaState:
                // Format: CallState {<target>} {<return>}
                writer.Write($"{keyword} {{{data}}} {{{rule.State}}}");
                break;

            case MetaActionType.EmbeddedNavRoute:
            {
                string navName = string.IsNullOrEmpty(data) ? "empty" : data.Split(';')[0];
                writer.Write($"{keyword} {navName} {{[none]}}");
                break;
            }

            case MetaActionType.SetWatchdog:
            {
                // Format: SetWatchdog <distance> <seconds> {<state>}
                var parts = data.Split(';');
                string state = parts.Length > 0 ? parts[0] : "Default";
                string range = parts.Length > 1 ? parts[1] : "10";
                string time = parts.Length > 2 ? parts[2] : "60";
                writer.Write($"{keyword} {range} {time} {{{state}}}");
                break;
            }

            case MetaActionType.SetRAOption:
            {
                var parts = data.Split(';');
                string option = parts.Length > 0 ? parts[0] : "";
                string value = parts.Length > 1 ? parts[1] : "";
                writer.Write($"{keyword} {{{option}}} {{{value}}}");
                break;
            }

            case MetaActionType.GetRAOption:
            {
                var parts = data.Split(';');
                string variable = parts.Length > 0 ? parts[0] : "";
                string option = parts.Length > 1 ? parts[1] : "";
                writer.Write($"{keyword} {{{variable}}} {{{option}}}");
                break;
            }

            case MetaActionType.CreateView:
            {
                int semi = data.IndexOf(';');
                string name = semi >= 0 ? data.Substring(0, semi) : data;
                string xml = semi >= 0 ? data.Substring(semi + 1) : "";
                writer.Write($"{keyword} {{{name}}} {{{xml}}}");
                break;
            }

            default:
                // No-data actions
                writer.Write(keyword);
                break;
        }
    }

    // ── NAV section writing ─────────────────────────────────────────────────

    private static void WriteNavSection(TextWriter writer, string navName, IReadOnlyList<string> lines)
    {
        try
        {
            if (lines.Count < 3 || !lines[0].Contains("uTank2 NAV"))
                return;

            string routeType = int.TryParse(lines[1], out int rt) ? rt switch
            {
                1 => "circular",
                2 => "linear",
                3 => "follow",
                4 => "once",
                _ => "circular"
            } : "circular";

            writer.WriteLine($"NAV: {navName} {routeType} ~~ {{");

            int pointCount = int.TryParse(lines[2], out int pc) ? pc : 0;
            int idx = 3;

            for (int p = 0; p < pointCount && idx < lines.Count; p++)
            {
                int pointType = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                double ew = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                double ns = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                double z = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                idx++; // skip flag

                switch (pointType)
                {
                    case 0: // Point
                        writer.Write("\t");
                        writer.WriteLine($"pnt {ew.ToString(CultureInfo.InvariantCulture)} {ns.ToString(CultureInfo.InvariantCulture)} {z.ToString(CultureInfo.InvariantCulture)}");
                        break;
                    case 2: // Recall
                    {
                        int spellId = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        string recallName = SpellIdToRecallName(spellId);
                        writer.Write("\t");
                        writer.WriteLine($"rcl {ew.ToString(CultureInfo.InvariantCulture)} {ns.ToString(CultureInfo.InvariantCulture)} {z.ToString(CultureInfo.InvariantCulture)} {{{recallName}}}");
                        break;
                    }
                    case 3: // Pause
                    {
                        int ms = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        double sec = ms / 1000.0;
                        writer.Write("\t");
                        writer.WriteLine($"pau {sec.ToString(CultureInfo.InvariantCulture)}");
                        break;
                    }
                    case 4: // Chat
                    {
                        string chatCmd = lines[idx++];
                        writer.Write("\t");
                        writer.WriteLine($"cht {ew.ToString(CultureInfo.InvariantCulture)} {ns.ToString(CultureInfo.InvariantCulture)} {z.ToString(CultureInfo.InvariantCulture)} {{{chatCmd}}}");
                        break;
                    }
                    case 6: // PortalNPC
                    {
                        string targetName = lines[idx++];
                        int objClass = int.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        bool isTie = bool.Parse(lines[idx++]);
                        double exitEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        double exitNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        double exitZ = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        idx++; // flag
                        double landEW = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        double landNS = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        double landZ = double.Parse(lines[idx++], CultureInfo.InvariantCulture);
                        idx++; // flag

                        writer.Write("\t");
                        writer.WriteLine($"ptl {ew.ToString(CultureInfo.InvariantCulture)} {ns.ToString(CultureInfo.InvariantCulture)} {z.ToString(CultureInfo.InvariantCulture)} {exitEW.ToString(CultureInfo.InvariantCulture)} {exitNS.ToString(CultureInfo.InvariantCulture)} {exitZ.ToString(CultureInfo.InvariantCulture)} {objClass} {{{targetName}}}");
                        break;
                    }
                }
            }

            writer.WriteLine("~~ }");
        }
        catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void CollectNavNames(List<MetaRule> rules, HashSet<string> navNames)
    {
        foreach (var rule in rules)
        {
            if (rule.Action == MetaActionType.EmbeddedNavRoute &&
                !string.IsNullOrEmpty(rule.ActionData))
            {
                navNames.Add(rule.ActionData.Split(';')[0]);
            }

            if (rule.ActionChildren != null)
                CollectNavNames(rule.ActionChildren, navNames);
            if (rule.Children != null)
                CollectNavNames(rule.Children, navNames);
        }
    }

    private static string SpellIdToRecallName(int spellId)
    {
        return spellId switch
        {
            2931 => "Recall Aphus Lassel",
            1635 => "Lifestone Recall",
            48   => "Primary Portal Recall",
            2647 => "Secondary Portal Recall",
            2645 => "Portal Recall",
            2023 => "Recall the Sanctuary",
            4213 => "Call of the Mhoire Forge",
            3865 => "Glenden Wood Recall",
            2041 => "Aerlinthe Recall",
            4084 => "Colosseum Recall",
            5541 => "Facility Hub Recall",
            5542 => "Gear Knight Recall",
            5543 => "Neftet Recall",
            6321 => "Rynthid Recall",
            6322 => "Viridian Rise Recall",
            _    => $"Spell {spellId}"
        };
    }
}
