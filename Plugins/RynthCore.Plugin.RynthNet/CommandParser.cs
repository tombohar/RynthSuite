// ============================================================================
//  RynthNet - CommandParser.cs
//  Argument parsing for /rn and the UB-compatible /ub bc, /ub bct forms. Pure,
//  so the harness tests it directly.
//
//  UtilityBelt's syntax (docs: utilitybelt.gitlab.io/docs/tools/networking):
//    /ub bc [millisecondDelay] <command>
//    /ub bct <tag,tag|"some tag"> [millisecondDelay] <command>
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthNet;

internal static class CommandParser
{
    /// <summary>"[delay] command": a leading all-digit word followed by more text is the delay in ms.</summary>
    public static void DelayAndCommand(string rest, out int delayMs, out string command)
    {
        delayMs = 0;
        command = (rest ?? "").Trim();
        int sp = command.IndexOf(' ');
        if (sp <= 0) return;
        string first = command.Substring(0, sp);
        foreach (char c in first)
            if (c < '0' || c > '9') return;
        if (first.Length > 7) return;   // over 9999999 ms: not a delay
        delayMs = int.Parse(first, System.Globalization.CultureInfo.InvariantCulture);
        command = command.Substring(sp + 1).Trim();
    }

    /// <summary>
    /// A comma-separated tag list (no spaces, or "quoted tags"), then the rest. False when no tag.
    /// </summary>
    public static bool TagsAndRest(string input, out List<string> tags, out string rest)
    {
        var list = new List<string>();
        tags = list;
        rest = "";
        string s = (input ?? "").TrimStart();
        int i = 0;
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (quoted)
            {
                if (c == '"') quoted = false;
                else current.Append(c);
                continue;
            }
            if (c == '"') { quoted = true; continue; }
            if (c == ',') { Flush(); continue; }
            if (char.IsWhiteSpace(c)) break;
            current.Append(c);
        }
        Flush();
        rest = i < s.Length ? s.Substring(i).Trim() : "";
        return list.Count > 0;

        void Flush()
        {
            string t = current.ToString().Trim();
            current.Clear();
            if (t.Length > 0 && !list.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) list.Add(t);
        }
    }

    /// <summary>
    /// "/rn tell &lt;character&gt; &lt;command&gt;". Names can have spaces: "Name, command" splits at the
    /// comma (as AC's /tell does); otherwise the longest known name the text starts with wins,
    /// else the first word.
    /// </summary>
    public static bool NameAndCommand(string input, IEnumerable<string> knownNames, out string name, out string command)
    {
        name = "";
        command = "";
        string s = (input ?? "").Trim();
        if (s.Length == 0) return false;

        int comma = s.IndexOf(',');
        int slash = s.IndexOf('/');
        if (comma > 0 && (slash < 0 || comma < slash))
        {
            name = s.Substring(0, comma).Trim();
            command = s.Substring(comma + 1).Trim();
            return name.Length > 0 && command.Length > 0;
        }

        string best = "";
        foreach (string known in knownNames)
        {
            if (string.IsNullOrEmpty(known) || known.Length <= best.Length) continue;
            if (s.Length > known.Length && s.StartsWith(known, StringComparison.OrdinalIgnoreCase) && s[known.Length] == ' ')
                best = known;
        }
        if (best.Length > 0)
        {
            name = best;
            command = s.Substring(best.Length).Trim();
            return command.Length > 0;
        }

        int sp = s.IndexOf(' ');
        if (sp <= 0) return false;
        name = s.Substring(0, sp);
        command = s.Substring(sp + 1).Trim();
        return command.Length > 0;
    }
}
