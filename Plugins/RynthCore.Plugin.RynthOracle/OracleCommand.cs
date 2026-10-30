using RynthCore.Plugin.RynthOracle.Views;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthOracle;

internal enum OracleVerb { Window, Void, Timers, Quests, Tab, Trackers, Status, About, Help, Unknown }

/// <summary>A parsed "/ro ..." (the words after /ro, lower case): its verb, and the tab or the trackers it shows.</summary>
internal readonly record struct OracleCommand(OracleVerb Verb, string Tab = "", QuestKind? Trackers = null, string Word = "")
{
    /// <summary>The verbs that show a window (they need the engine's plugin windows).</summary>
    public bool ShowsWindow => Verb is OracleVerb.Window or OracleVerb.Void or OracleVerb.Timers or OracleVerb.Tab
        or OracleVerb.Trackers or OracleVerb.About;

    public static OracleCommand Parse(string? arg)
    {
        string a = (arg ?? string.Empty).Trim().ToLowerInvariant();
        return a switch
        {
            "" => new(OracleVerb.Window),
            "void" => new(OracleVerb.Void),
            "timers" or "timer" => new(OracleVerb.Timers),
            "quests" or "refresh" => new(OracleVerb.Quests),
            // The main window on its Fellowship tab (RynthCore's Char menu and RynthAi's Mini Remote use this).
            "fellowship" or "fellow" or "fship" => new(OracleVerb.Tab, OracleView.TabFellowship),
            // The Character tab: the character overview and RynthAi's session. Shows, never toggles off.
            "character" or "char" => new(OracleVerb.Tab, OracleView.TabCharacter),
            // John's 30 Legendary Key quests: the Quests tab's Legendary rows.
            "legendary" or "legend" or "john" => new(OracleVerb.Trackers, OracleView.TabQuests, QuestKind.Legendary),
            // The 100 exploration markers: the Quests tab's marker rows (the old Markers tab).
            "markers" or "marker" => new(OracleVerb.Trackers, OracleView.TabQuests, QuestKind.Marker),
            // Every tracker: the Legendary Key quests and the exploration markers.
            "trackers" => new(OracleVerb.Trackers, OracleView.TabQuests),
            // The Skills tab: RynthCore's skills panel (raise and train) inside this window.
            "skills" or "skill" => new(OracleVerb.Tab, OracleView.TabSkills),
            "attributes" or "attrs" or "attribute" or "vitals" => new(OracleVerb.Tab, OracleView.TabAttributes),
            "progress" or "progression" => new(OracleVerb.Tab, OracleView.TabProgress),
            "status" => new(OracleVerb.Status),
            "about" => new(OracleVerb.About, OracleView.TabAbout),
            "help" or "?" => new(OracleVerb.Help),
            _ => new(OracleVerb.Unknown, Word: a),
        };
    }

    public const string Usage =
        "Commands: /ro (window), /ro character (Character tab: status, session), "
        + "/ro skills|attributes|progress (those tabs), /ro void (Void window), "
        + "/ro timers (Quest Timers window), /ro fellowship (Fellowship tab), "
        + "/ro trackers (Quests tab, trackers only), /ro legendary (John's 30 key quests), /ro markers, "
        + "/ro quests (read quest flags), /ro status, "
        + "/ro about (About tab: settings, server, quest list). A server's own pages are its server plugin's (/rserver).";

    public const string NoWindows =
        "This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). /ro status still works.";

    // ── What /ro answers (2026-10-28: every command answers, Plugins/Shared/CommandReply.cs) ──

    public static string Shown(string window, bool shown) => CommandReply.Shown(window, shown);

    public static string OnTab(string tab) => $"Oracle window shown on its {tab} tab.";

    public static string OnTrackers(QuestKind? kind) => kind switch
    {
        QuestKind.Legendary => "Oracle window shown on the Quests tab: the Legendary Key quests.",
        QuestKind.Marker => "Oracle window shown on the Quests tab: the exploration markers.",
        _ => "Oracle window shown on the Quests tab: every tracker.",
    };

    /// <summary>"/ro quests": a /myquests sent, or why not (out of the world, the server's one-a-minute limit, one already out).</summary>
    public static (bool Ok, string Text) Quests(bool inWorld, bool sent, int waitSeconds) =>
        !inWorld ? (false, "Log in first: the quest flags come from the server's /myquests.")
        : sent ? (true, "Reading your quest flags (/myquests)...")
        : waitSeconds > 0 ? (false, $"The server answers one /myquests a minute; try again in {waitSeconds}s (the flags shown are kept).")
        : (false, "/myquests was sent moments ago; waiting for that reply.");

    public static string Unknown(string word) => $"Unknown '/ro {word}'. {Usage}";
}
