using System;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// The name an Embedded Nav Route action and an embedded route share (2026-10-13).
///
/// VTank shows a route that has no file name as "[None]". A player's meta reached RynthAi with
/// that name broken into "None]" on both the action and its route (log: "EmbedNav: loaded 'None]'
/// (1 pts)"), and an action naming "[None]" with no route behind it went looking for a nav file
/// called "[None].nav" and reported "Route missing". Every reader cleans names the same way:
/// whitespace, and brackets that wrap the whole name or have no partner ("[None]", "None]",
/// "[None" -> "None"). Brackets inside a name ("Hive [East]") are kept.
///
/// "None" itself is not "no route": VTank stores a route that was recorded but never saved under
/// "[None]" with all its points (a 213-point one in S_Tusks.met, a one-point hunt spot in CQ metas)
/// and loads it. Only a None with nothing behind it means "no embedded nav": MetaManager then keeps
/// the current route.
/// </summary>
internal static class EmbedNavName
{
    public const string None = "None";

    /// <summary>The name without surrounding whitespace and stray or wrapping brackets.</summary>
    public static string Clean(string? name)
    {
        string s = (name ?? string.Empty).Trim();
        for (int guard = 0; guard < 4 && s.Length > 0; guard++)
        {
            bool open = s[0] == '[', close = s[^1] == ']';
            if (open && close && s.Length >= 2 && Balanced(s, 1, s.Length - 1))
                s = s.Substring(1, s.Length - 2).Trim();          // "[None]"
            else if (close && s.IndexOf('[') < 0)
                s = s.Substring(0, s.Length - 1).Trim();          // "None]"
            else if (open && s.IndexOf(']') < 0)
                s = s.Substring(1).Trim();                        // "[None"
            else
                break;
        }
        return s;
    }

    /// <summary>VTank's name for a route with no file name ("[None]", any case, stray brackets too).</summary>
    public static bool IsNone(string? name) => string.Equals(Clean(name), None, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when s[from..to) has no ']' before its '[' (so "[a] [b]" is not unwrapped to "a] [b").</summary>
    private static bool Balanced(string s, int from, int to)
    {
        int depth = 0;
        for (int i = from; i < to; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']' && --depth < 0) return false;
        }
        return depth == 0;
    }
}
