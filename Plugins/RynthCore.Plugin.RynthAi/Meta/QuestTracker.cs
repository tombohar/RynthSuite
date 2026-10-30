using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed class QuestRecord
{
    public string Key = "";
    /// <summary>Server-supplied description from /myquests (may be empty).</summary>
    public string Description = "";
    public int Solves;
    public int MaxSolves;
    public DateTime CompletedOn = DateTime.MinValue;
    public TimeSpan RepeatTime = TimeSpan.Zero;

    /// <summary>
    /// Returns true if the quest can be done again now.
    /// Mirrors UB QuestFlag.IsReady(): timer expired AND not a once-only flag.
    /// Once-only: MaxSolves==1 &amp;&amp; Solves&lt;=1 — never ready again.
    /// </summary>
    public bool IsReady()
    {
        if ((CompletedOn + RepeatTime) > DateTime.UtcNow)
            return false;
        return !(MaxSolves == 1 && Solves <= 1);
    }

    /// <summary>Time until the flag can be solved again (zero when ready).</summary>
    public TimeSpan TimeUntilReady()
    {
        var left = (CompletedOn + RepeatTime) - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

/// <summary>
/// Fires /myquests, parses the resulting chat lines into a per-session quest flag cache,
/// and exposes UB-compatible query methods used by ExpressionEngine.
/// </summary>
internal sealed class QuestTracker
{
    // Matches lines produced by the AC /myquests command.
    // Format: key - N solves (unixTimestamp)"description" maxSolves repeatSeconds
    // Example: blankaug - 1 solves (1609459200)"Blank Aug" 1 0
    private static readonly Regex QuestLineRegex = new Regex(
        @"(?<key>\S+) \- (?<solves>\d+) solves \((?<completedOn>\d{0,11})\)""?((?<description>.*)"" (?<maxSolves>.*) (?<repeatTime>\d{0,11}))?.*$",
        RegexOptions.Compiled);

    private readonly RynthCoreHost _host;
    private readonly Dictionary<string, QuestRecord> _flags = new(StringComparer.OrdinalIgnoreCase);

    private bool _refreshing;
    private bool _gotFirstQuest;
    private bool _quietRefresh;
    private DateTime _lastLineTime;
    private DateTime _refreshStarted;

    public bool IsRefreshing => _refreshing;

    /// <summary>The server answered that /myquests is not enabled.</summary>
    public bool Disabled { get; private set; }

    /// <summary>UTC time the last refresh finished with data (MinValue = never).</summary>
    public DateTime LastRefreshUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Last refresh outcome text for the Mini Remote's Quests tab ("12 flags", "rate limited", ...).</summary>
    public string LastStatus { get; private set; } = "not refreshed";

    /// <summary>Number of cached flags.</summary>
    public int Count => _flags.Count;

    /// <summary>
    /// Bumped on the pump thread each time a refresh ends (0 = none yet). RynthILT polls it through
    /// RynthAi.Hub QuestStatus to read Disabled and Count (a counter, not an event: nothing to
    /// unsubscribe across plugin reloads).
    /// </summary>
    public int RefreshGeneration { get; private set; }

    public QuestTracker(RynthCoreHost host) => _host = host;

    /// <summary>
    /// Issues /myquests to the server to populate the quest flag cache.
    /// No-ops if already refreshing or InvokeChatParser is not available.
    /// </summary>
    public void Refresh() => Refresh(quiet: false);

    /// <summary>
    /// Issues /myquests. <paramref name="quiet"/> asks <see cref="OnChatLine"/> to report
    /// those lines as eatable so the Quests tab can hide its own poll.
    /// </summary>
    public void Refresh(bool quiet)
    {
        if (_refreshing || !_host.HasInvokeChatParser)
            return;
        _quietRefresh = quiet;
        ExpectReply();
        RefreshSent?.Invoke();
        _host.InvokeChatParser("/myquests");
    }

    /// <summary>Set by the plugin: told whenever Refresh sends /myquests (the Meta Manager shows that reply).</summary>
    public Action? RefreshSent;

    /// <summary>
    /// Someone else (the Meta Manager's poll) just sent /myquests: read the reply into the
    /// cache as if Refresh had sent it, so quest expressions stay fresh.
    /// </summary>
    public void ExpectReply()
    {
        _refreshing = true;
        _gotFirstQuest = false;
        _lastLineTime = DateTime.UtcNow;
        _refreshStarted = DateTime.UtcNow;
    }

    /// <summary>
    /// Feed every incoming chat line here. Quest lines are parsed and cached;
    /// terminal lines (empty list, rate limit, etc.) end the refresh early.
    /// </summary>
    public bool OnChatLine(string text)
    {
        if (!_refreshing)
            return false;

        if (text.Contains("Quest list is empty"))
        {
            bool quiet = _quietRefresh;
            Finish("quest list is empty", gotData: true);
            return quiet;
        }

        if (text.Contains("The command \"myquests\" is not currently enabled"))
        {
            Disabled = true;
            bool quiet = _quietRefresh;
            Finish("/myquests is disabled on this server", gotData: false);
            return quiet;
        }

        if (text.Contains("This command may only be run once every"))
        {
            bool quiet = _quietRefresh;
            Finish("rate limited by the server (try again in a minute)", gotData: false);
            return quiet;
        }

        var m = QuestLineRegex.Match(text);
        if (!m.Success)
            return false;

        _gotFirstQuest = true;
        _lastLineTime = DateTime.UtcNow;

        var rec = new QuestRecord();
        rec.Key = m.Groups["key"].Value.ToLowerInvariant();
        rec.Description = m.Groups["description"].Value.Trim().Trim('"');
        int.TryParse(m.Groups["solves"].Value, out rec.Solves);
        int.TryParse(m.Groups["maxSolves"].Value, out rec.MaxSolves);

        if (double.TryParse(m.Groups["completedOn"].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double ts) && ts > 0)
            rec.CompletedOn = DateTimeOffset.FromUnixTimeSeconds((long)ts).UtcDateTime;

        if (double.TryParse(m.Groups["repeatTime"].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double rt) && rt > 0)
            rec.RepeatTime = TimeSpan.FromSeconds(rt);

        _flags[rec.Key] = rec;
        return _quietRefresh;
    }

    /// <summary>
    /// Call once per game tick. Detects end-of-list by silence:
    ///   1 second after the last quest line received → done.
    ///   15 second global timeout if no quest lines ever arrive → done.
    /// </summary>
    public void Tick()
    {
        if (!_refreshing)
            return;

        var now = DateTime.UtcNow;
        if (_gotFirstQuest)
        {
            if ((now - _lastLineTime).TotalSeconds >= 1.0)
                Finish($"{Count} flags", gotData: true);
        }
        else
        {
            if ((now - _refreshStarted).TotalSeconds >= 15.0)
                Finish("no reply from server", gotData: false);
        }
    }

    private void Finish(string status, bool gotData)
    {
        _refreshing = false;
        _quietRefresh = false;
        LastStatus = status;
        if (gotData)
        {
            Disabled = false;
            LastRefreshUtc = DateTime.UtcNow;
        }
        RefreshGeneration++;
    }

    /// <summary>Copy of every cached record for the Quests tab.</summary>
    public QuestRecord[] Snapshot() => _flags.Values.ToArray();

    /// <summary>Returns true if the key exists in the cached quest flag list.</summary>
    public bool HasFlag(string key) => _flags.ContainsKey(key.ToLowerInvariant());

    /// <summary>Returns the cached record for the given key, or false if not found.</summary>
    public bool TryGetFlag(string key, out QuestRecord rec)
        => _flags.TryGetValue(key.ToLowerInvariant(), out rec!);
}
