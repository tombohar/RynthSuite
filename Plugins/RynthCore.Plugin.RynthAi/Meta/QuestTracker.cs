using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed class QuestRecord
{
    public string Key = "";
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
    private DateTime _lastLineTime;
    private DateTime _refreshStarted;

    public bool IsRefreshing => _refreshing;

    public QuestTracker(RynthCoreHost host) => _host = host;

    /// <summary>
    /// Issues /myquests to the server to populate the quest flag cache.
    /// No-ops if already refreshing or InvokeChatParser is not available.
    /// </summary>
    public void Refresh()
    {
        if (_refreshing || !_host.HasInvokeChatParser)
            return;
        _refreshing = true;
        _gotFirstQuest = false;
        _lastLineTime = DateTime.UtcNow;
        _refreshStarted = DateTime.UtcNow;
        _host.InvokeChatParser("/myquests");
    }

    /// <summary>
    /// Feed every incoming chat line here. Quest lines are parsed and cached;
    /// terminal lines (empty list, rate limit, etc.) end the refresh early.
    /// </summary>
    public void OnChatLine(string text)
    {
        if (!_refreshing)
            return;

        if (text.Contains("Quest list is empty") ||
            text.Contains("The command \"myquests\" is not currently enabled"))
        {
            _refreshing = false;
            return;
        }

        if (text.Contains("This command may only be run once every"))
        {
            _refreshing = false;
            return;
        }

        var m = QuestLineRegex.Match(text);
        if (!m.Success)
            return;

        _gotFirstQuest = true;
        _lastLineTime = DateTime.UtcNow;

        var rec = new QuestRecord();
        rec.Key = m.Groups["key"].Value.ToLowerInvariant();
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
                _refreshing = false;
        }
        else
        {
            if ((now - _refreshStarted).TotalSeconds >= 15.0)
                _refreshing = false;
        }
    }

    /// <summary>Returns true if the key exists in the cached quest flag list.</summary>
    public bool HasFlag(string key) => _flags.ContainsKey(key.ToLowerInvariant());

    /// <summary>Returns the cached record for the given key, or false if not found.</summary>
    public bool TryGetFlag(string key, out QuestRecord rec)
        => _flags.TryGetValue(key.ToLowerInvariant(), out rec!);
}
