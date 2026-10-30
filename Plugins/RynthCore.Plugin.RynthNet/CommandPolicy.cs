// ============================================================================
//  RynthNet - CommandPolicy.cs
//  Whether a command another client sent may run here. Pure (no host calls),
//  so the harness tests it directly.
//
//  Order: remote commands off -> sender not trusted -> loop guard -> deny list
//  -> allow list. The loop guard is not a setting: a received command may never
//  broadcast again (/rn ..., /ub bc, /ub bct anywhere in it, e.g. behind
//  /ub delay), or two clients could bounce a command between them forever.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthNet;

internal static class CommandPolicy
{
    /// <summary>Lower-cased, trimmed, single-spaced.</summary>
    public static string Normalize(string text)
    {
        var parts = (text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).ToLowerInvariant();
    }

    /// <summary>True when the command would send commands to other clients again.</summary>
    public static bool Rebroadcasts(string text)
    {
        string[] words = Normalize(text).Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i] == "/rn") return true;
            if (words[i] == "/ub" && i + 1 < words.Length && words[i + 1] is "bc" or "bct") return true;
        }
        return false;
    }

    public static bool Check(string text, string sender, RynthNetPolicy s, out string why)
    {
        why = "";
        string norm = Normalize(text);
        if (norm.Length == 0) { why = "empty command"; return false; }
        if (!s.AcceptRemoteCommands) { why = "remote commands are off here (/rn remote on)"; return false; }
        if (s.TrustedSenders.Count > 0 && !Contains(s.TrustedSenders, sender))
        {
            why = $"{sender} is not in the trusted list here";
            return false;
        }
        if (Rebroadcasts(norm)) { why = "a received command can't broadcast again"; return false; }
        foreach (string d in s.Deny)
        {
            string p = Normalize(d);
            if (p.Length > 0 && norm.StartsWith(p, StringComparison.Ordinal)) { why = $"denied here ({d})"; return false; }
        }
        if (s.Allow.Count > 0)
        {
            foreach (string a in s.Allow)
            {
                string p = Normalize(a);
                if (p.Length > 0 && norm.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            why = "not in the allow list here";
            return false;
        }
        return true;
    }

    private static bool Contains(List<string> list, string value)
    {
        foreach (string s in list)
            if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
