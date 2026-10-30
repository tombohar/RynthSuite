using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.Meta;

// ============================================================================
//  Meta Manager ("schedule"): loads a different meta when a timer runs out.
//  Design: Docs\META_MANAGER.md. This class is the whole decision - pure, no
//  host, the clock passed in - so the tests drive it directly. The glue in
//  RynthAiPlugin.MetaSchedule.cs feeds it chat lines and the bot's state on
//  RynthAi's pump and carries out what Tick returns (send /myquests, load a meta).
// ============================================================================

internal enum ScheduleTrigger
{
    /// <summary>The named quest's /myquests timer has run out (or it has never been done).</summary>
    QuestReady = 0,
    /// <summary>The current meta has been loaded for N minutes.</summary>
    AfterMinutesOnMeta = 1,
    /// <summary>N minutes since the manager was switched on or the rule last fired.</summary>
    EveryMinutes = 2,
    /// <summary>A countdown the player starts has run out.</summary>
    Countdown = 3,
}

/// <summary>One "when TRIGGER, load META" rule. Also the wire format of the engine's mm_add / mm_update.</summary>
internal sealed class MetaScheduleRule
{
    public bool Enabled { get; set; } = true;
    public ScheduleTrigger Trigger { get; set; }
    /// <summary>QuestReady: the quest's name as /myquests prints it (case is ignored).</summary>
    public string Quest { get; set; } = string.Empty;
    /// <summary>The time triggers' N.</summary>
    public double Minutes { get; set; } = 30;
    /// <summary>The meta to load: a file name in MetaFiles (farm.met, farm.af; no extension tries .af then .met).</summary>
    public string Meta { get; set; } = string.Empty;
    /// <summary>Only fire while this meta is loaded (file name, extension ignored). Empty = any.</summary>
    public string OnlyOnMeta { get; set; } = string.Empty;
    /// <summary>False = one-shot: fires once, then stays <see cref="Fired"/> until reset.</summary>
    public bool Repeat { get; set; } = true;
    /// <summary>A one-shot rule that has fired. Saved.</summary>
    public bool Fired { get; set; }
    /// <summary>Countdown end (unix ms), 0 = not running. Saved, so a countdown survives a relog.</summary>
    public long CountdownEndsUnixMs { get; set; }

    // ── Runtime only ────────────────────────────────────────────────────
    /// <summary>A repeating quest rule that fired and whose quest is still ready: it holds first place.</summary>
    [JsonIgnore] public bool Latched;
    [JsonIgnore] public DateTime EveryAnchorUtc = DateTime.MinValue;
    [JsonIgnore] public int FireCount;
    [JsonIgnore] public DateTime LastFiredUtc = DateTime.MinValue;
    /// <summary>Why the last load failed; the rule is skipped until reset or edited.</summary>
    [JsonIgnore] public string Error = string.Empty;

    /// <summary>A copy of the saved settings (runtime state starts fresh).</summary>
    public MetaScheduleRule CloneSettings() => new()
    {
        Enabled = Enabled, Trigger = Trigger, Quest = Quest ?? string.Empty, Minutes = Minutes,
        Meta = Meta ?? string.Empty, OnlyOnMeta = OnlyOnMeta ?? string.Empty, Repeat = Repeat,
        Fired = Fired, CountdownEndsUnixMs = CountdownEndsUnixMs,
    };
}

/// <summary>Everything saved in &lt;character folder&gt;\metamanager.json.</summary>
internal sealed class MetaScheduleConfig
{
    public bool Enabled { get; set; }
    public int PollMinutes { get; set; } = 5;
    public int MaxWaitSeconds { get; set; } = 60;
    public int MinGapSeconds { get; set; } = 30;
    public bool AllowWhileStopped { get; set; }
    public List<MetaScheduleRule> Rules { get; set; } = new();
}

/// <summary>What the bot is doing this tick, gathered by the glue.</summary>
internal readonly record struct ScheduleInputs(
    bool InWorld, bool MacroRunning, string CurrentMetaPath, string? Blocker, bool CanPoll);

internal enum ScheduleActionKind { None, SendPoll, LoadMeta }

/// <summary>What the glue must do now. LoadMeta: load <see cref="Meta"/> and print the reason.</summary>
internal readonly record struct ScheduleAction(
    ScheduleActionKind Kind, int RuleIndex = -1, string Meta = "", string Reason = "", bool Forced = false)
{
    public static readonly ScheduleAction None = new(ScheduleActionKind.None);
}

internal enum QuestState { Unknown, Ready, Waiting, Never }

internal sealed class MetaScheduler
{
    public const double ReplySilenceSeconds = 1.5;
    public const double ReplyTimeoutSeconds = 15;
    /// <summary>How long after our poll the chat bar may still hand us our own "/myquests" back.</summary>
    public const double OwnEchoSeconds = 5;
    public const string ConfigFileName = "metamanager.json";

    public MetaScheduleConfig Config { get; private set; } = new();

    // ── Quest table ─────────────────────────────────────────────────────
    private readonly Dictionary<string, MyQuestsEntry> _quests = new(StringComparer.OrdinalIgnoreCase);
    private bool _haveFullList;            // a whole reply was read: a quest missing from it was never done
    private DateTime _lastPollUtc = DateTime.MinValue;   // last poll sent, or last full reply of anyone's
    private bool _serverDisabled;

    // ── The reply in flight ─────────────────────────────────────────────
    private enum ReplyOwner { None, Ours, Shown }
    private ReplyOwner _owner;
    private DateTime _querySentUtc, _lastReplyLineUtc;
    private bool _gotReplyLine;
    private readonly HashSet<string> _seenThisReply = new(StringComparer.OrdinalIgnoreCase);
    private bool _expectOwnEcho;

    // ── Switching ───────────────────────────────────────────────────────
    private int _pendingRule = -1;
    private DateTime _pendingSinceUtc;
    private string _waitReason = string.Empty;
    private DateTime _lastSwitchUtc = DateTime.MinValue;
    private string _lastMetaPath = string.Empty;
    private DateTime _metaSinceUtc = DateTime.MinValue;
    private bool _dirty;

    public string LastSwitchText { get; private set; } = string.Empty;
    public int PendingRule => _pendingRule;
    public string WaitReason => _waitReason;
    public bool ServerDisabled => _serverDisabled;
    public bool HaveQuestList => _haveFullList;
    public int QuestCount => _quests.Count;
    public DateTime LastPollUtc => _lastPollUtc;
    public bool PollInFlight => _owner == ReplyOwner.Ours;
    public DateTime LastSwitchUtc => _lastSwitchUtc;

    /// <summary>True once after the saved settings changed (a fired one-shot, a countdown, an edit).</summary>
    public bool ConsumeDirty()
    {
        bool d = _dirty;
        _dirty = false;
        return d;
    }

    // =====================================================================
    //  Settings
    // =====================================================================

    public void Load(MetaScheduleConfig? cfg)
    {
        Config = cfg ?? new MetaScheduleConfig();
        Config.Rules ??= new List<MetaScheduleRule>();
        Config.Rules.RemoveAll(r => r == null);
        Config.PollMinutes = ClampPoll(Config.PollMinutes);
        Config.MaxWaitSeconds = Math.Clamp(Config.MaxWaitSeconds, 0, 3600);
        Config.MinGapSeconds = Math.Clamp(Config.MinGapSeconds, 0, 3600);
        foreach (var r in Config.Rules) Sanitize(r);
        _pendingRule = -1;
        _waitReason = string.Empty;
        _dirty = false;
    }

    public static int ClampPoll(int minutes) => Math.Clamp(minutes, 1, 1440);

    private static void Sanitize(MetaScheduleRule r)
    {
        r.Quest = (r.Quest ?? string.Empty).Trim();
        r.Meta = (r.Meta ?? string.Empty).Trim();
        r.OnlyOnMeta = (r.OnlyOnMeta ?? string.Empty).Trim();
        if (double.IsNaN(r.Minutes) || r.Minutes < 0) r.Minutes = 0;
        if (r.Minutes > 100000) r.Minutes = 100000;
        if (!Enum.IsDefined(r.Trigger)) r.Trigger = ScheduleTrigger.QuestReady;
    }

    public void SetEnabled(bool on)
    {
        if (Config.Enabled == on) return;
        Config.Enabled = on;
        if (on)
        {
            _serverDisabled = false;     // try again: the server may have turned it on
            foreach (var r in Config.Rules) r.EveryAnchorUtc = DateTime.MinValue;
        }
        _pendingRule = -1;
        _waitReason = string.Empty;
        _dirty = true;
    }

    public void SetPollMinutes(int m) { Config.PollMinutes = ClampPoll(m); _dirty = true; }
    public void SetMaxWaitSeconds(int s) { Config.MaxWaitSeconds = Math.Clamp(s, 0, 3600); _dirty = true; }
    public void SetMinGapSeconds(int s) { Config.MinGapSeconds = Math.Clamp(s, 0, 3600); _dirty = true; }
    public void SetAllowWhileStopped(bool on) { Config.AllowWhileStopped = on; _dirty = true; }

    public void AddRule(MetaScheduleRule rule)
    {
        var r = rule.CloneSettings();
        r.Fired = false;
        Sanitize(r);
        Config.Rules.Add(r);
        _pendingRule = -1;
        _dirty = true;
    }

    /// <summary>Replaces rule <paramref name="index"/>'s settings. An edit re-arms it; a running countdown keeps running.</summary>
    public bool UpdateRule(int index, MetaScheduleRule rule)
    {
        if (index < 0 || index >= Config.Rules.Count) return false;
        var old = Config.Rules[index];
        var r = rule.CloneSettings();
        r.Fired = false;
        r.CountdownEndsUnixMs = r.Trigger == ScheduleTrigger.Countdown && old.Trigger == ScheduleTrigger.Countdown
            ? old.CountdownEndsUnixMs : 0;
        Sanitize(r);
        Config.Rules[index] = r;
        _pendingRule = -1;
        _dirty = true;
        return true;
    }

    public bool DeleteRule(int index)
    {
        if (index < 0 || index >= Config.Rules.Count) return false;
        Config.Rules.RemoveAt(index);
        _pendingRule = -1;
        _dirty = true;
        return true;
    }

    public bool MoveRule(int index, int direction)
    {
        int other = index + Math.Sign(direction);
        if (index < 0 || index >= Config.Rules.Count || other < 0 || other >= Config.Rules.Count) return false;
        (Config.Rules[index], Config.Rules[other]) = (Config.Rules[other], Config.Rules[index]);
        _pendingRule = -1;
        _dirty = true;
        return true;
    }

    public bool SetRuleEnabled(int index, bool on)
    {
        if (index < 0 || index >= Config.Rules.Count) return false;
        Config.Rules[index].Enabled = on;
        _pendingRule = -1;
        _dirty = true;
        return true;
    }

    /// <summary>Starts rule <paramref name="index"/>'s countdown (its own minutes, or <paramref name="minutes"/>).</summary>
    public bool StartCountdown(int index, double? minutes, DateTime now)
    {
        if (index < 0 || index >= Config.Rules.Count) return false;
        var r = Config.Rules[index];
        if (r.Trigger != ScheduleTrigger.Countdown) return false;
        double m = minutes is > 0 ? minutes.Value : r.Minutes;
        r.CountdownEndsUnixMs = ToUnixMs(now) + (long)(m * 60_000);
        r.Fired = false;
        r.Error = string.Empty;
        _dirty = true;
        return true;
    }

    public bool StopCountdown(int index)
    {
        if (index < 0 || index >= Config.Rules.Count) return false;
        var r = Config.Rules[index];
        if (r.CountdownEndsUnixMs == 0) return false;
        r.CountdownEndsUnixMs = 0;
        _dirty = true;
        return true;
    }

    /// <summary>Re-arms a rule: not fired, not holding, no error, its "every" timer restarted.</summary>
    public bool ResetRule(int index)
    {
        if (index < 0 || index >= Config.Rules.Count) return false;
        var r = Config.Rules[index];
        r.Fired = false;
        r.Latched = false;
        r.Error = string.Empty;
        r.EveryAnchorUtc = DateTime.MinValue;
        _pendingRule = -1;
        _dirty = true;
        return true;
    }

    /// <summary>The next Tick sends /myquests (if the manager is on and in world).</summary>
    public void RequestPoll() => _lastPollUtc = DateTime.MinValue;

    public bool HasQuestRules
    {
        get
        {
            foreach (var r in Config.Rules)
                if (r.Enabled && r.Trigger == ScheduleTrigger.QuestReady && !(r.Fired && !r.Repeat) && r.Quest.Length > 0)
                    return true;
            return false;
        }
    }

    // =====================================================================
    //  /myquests replies
    // =====================================================================

    /// <summary>
    /// A "/myquests" went out through the chat bar: typed by the player, sent by a script or
    /// another plugin, or our own poll coming back through the plugins' chat-bar hook. Its reply
    /// is shown - unless it is the echo of the poll we just sent.
    /// </summary>
    public void NoteTypedQuery(DateTime now)
    {
        if (_expectOwnEcho && (now - _querySentUtc).TotalSeconds <= OwnEchoSeconds)
        {
            _expectOwnEcho = false;
            return;
        }
        NoteShownQuery(now);
    }

    /// <summary>Someone other than the manager asked (RynthAi's quest refresh): don't eat that reply.</summary>
    public void NoteShownQuery(DateTime now)
    {
        if (_owner == ReplyOwner.None)
        {
            _seenThisReply.Clear();
            _gotReplyLine = false;
        }
        _owner = ReplyOwner.Shown;   // a query on top of ours: show the rest of the lines
        _querySentUtc = now;
        _expectOwnEcho = false;
    }

    private void BeginOwnPoll(DateTime now)
    {
        _owner = ReplyOwner.Ours;
        _querySentUtc = now;
        _gotReplyLine = false;
        _seenThisReply.Clear();
        _expectOwnEcho = true;
        _lastPollUtc = now;
    }

    /// <summary>Every incoming chat line. Returns true to eat it (it answers our own poll).</summary>
    public bool OnChatLine(string? text, DateTime now)
    {
        MyQuestsLineKind kind = MyQuestsParser.Parse(text, out MyQuestsEntry entry);
        if (kind == MyQuestsLineKind.None) return false;

        bool eat = _owner == ReplyOwner.Ours;
        switch (kind)
        {
            case MyQuestsLineKind.Quest:
                _quests[entry.Name] = entry;
                if (_owner != ReplyOwner.None)
                {
                    _seenThisReply.Add(entry.Name);
                    _gotReplyLine = true;
                    _lastReplyLineUtc = now;
                }
                break;
            case MyQuestsLineKind.Empty:
                _quests.Clear();
                _haveFullList = true;
                if (_owner != ReplyOwner.Ours) _lastPollUtc = now;   // someone else's full list counts as a poll
                _owner = ReplyOwner.None;
                break;
            case MyQuestsLineKind.Disabled:
                _serverDisabled = true;
                _owner = ReplyOwner.None;
                break;
            case MyQuestsLineKind.RateLimited:
                _owner = ReplyOwner.None;
                break;
        }
        return eat;
    }

    private void TickReply(DateTime now)
    {
        if (_owner == ReplyOwner.None) return;
        if (_gotReplyLine)
        {
            if ((now - _lastReplyLineUtc).TotalSeconds >= ReplySilenceSeconds)
            {
                // The whole list arrived: a quest that is not in it has never been done.
                var gone = new List<string>();
                foreach (string k in _quests.Keys) if (!_seenThisReply.Contains(k)) gone.Add(k);
                foreach (string k in gone) _quests.Remove(k);
                _haveFullList = true;
                // Our polls keep their send time (a steady interval); someone else's full list counts as a poll.
                if (_owner != ReplyOwner.Ours) _lastPollUtc = now;
                _owner = ReplyOwner.None;
            }
        }
        else if ((now - _querySentUtc).TotalSeconds >= ReplyTimeoutSeconds)
        {
            _owner = ReplyOwner.None;   // nothing came back; try again at the next poll
        }
        if (_expectOwnEcho && (now - _querySentUtc).TotalSeconds > OwnEchoSeconds) _expectOwnEcho = false;
    }

    public QuestState GetQuestState(string quest, DateTime now, out DateTime readyAtUtc)
    {
        readyAtUtc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(quest)) return QuestState.Unknown;
        if (_quests.TryGetValue(quest.Trim(), out MyQuestsEntry e))
        {
            if (e.AtSolveLimit) { readyAtUtc = DateTime.MaxValue; return QuestState.Never; }
            readyAtUtc = e.ReadyAtUtc;
            return now >= readyAtUtc ? QuestState.Ready : QuestState.Waiting;
        }
        // Not in a full list = never done = can be solved now (ACE: no registry entry → can solve).
        return _haveFullList ? QuestState.Ready : QuestState.Unknown;
    }

    // =====================================================================
    //  Tick
    // =====================================================================

    public ScheduleAction Tick(DateTime now, in ScheduleInputs inputs)
    {
        string path = inputs.CurrentMetaPath ?? string.Empty;
        if (_metaSinceUtc == DateTime.MinValue || !string.Equals(path, _lastMetaPath, StringComparison.OrdinalIgnoreCase))
        {
            _lastMetaPath = path;
            _metaSinceUtc = now;
        }

        TickReply(now);

        if (!Config.Enabled)
        {
            _pendingRule = -1;
            _waitReason = string.Empty;
            return ScheduleAction.None;
        }

        foreach (var r in Config.Rules)
            if (r.Trigger == ScheduleTrigger.EveryMinutes && r.EveryAnchorUtc == DateTime.MinValue)
                r.EveryAnchorUtc = now;

        if (inputs.InWorld && inputs.CanPoll && !_serverDisabled && _owner == ReplyOwner.None && HasQuestRules
            && (_lastPollUtc == DateTime.MinValue || (now - _lastPollUtc).TotalMinutes >= ClampPoll(Config.PollMinutes)))
        {
            BeginOwnPoll(now);
            return new ScheduleAction(ScheduleActionKind.SendPoll);
        }

        // A repeating quest rule that fired re-arms once its quest is seen waiting again.
        foreach (var r in Config.Rules)
            if (r.Latched && GetQuestState(r.Quest, now, out _) is QuestState.Waiting or QuestState.Never)
                r.Latched = false;

        int win = FindWinner(now, path);
        if (win != _pendingRule)
        {
            _pendingRule = win;
            _pendingSinceUtc = now;
        }
        if (win < 0)
        {
            _waitReason = string.Empty;
            return ScheduleAction.None;
        }

        if (!inputs.InWorld)
        {
            _waitReason = "not in world";
            _pendingSinceUtc = now;
            return ScheduleAction.None;
        }
        if (!inputs.MacroRunning && !Config.AllowWhileStopped)
        {
            _waitReason = "macro stopped";
            _pendingSinceUtc = now;     // the wait cap only counts while the macro runs
            return ScheduleAction.None;
        }
        DateTime gapEnds = _lastSwitchUtc == DateTime.MinValue ? DateTime.MinValue : _lastSwitchUtc.AddSeconds(Config.MinGapSeconds);
        if (now < gapEnds)
        {
            _waitReason = "minimum gap";
            return ScheduleAction.None;
        }
        // The safe-switch wait starts when the rule won or the gap ended, whichever is later.
        DateTime waitStart = _pendingSinceUtc > gapEnds ? _pendingSinceUtc : gapEnds;
        bool capped = (now - waitStart).TotalSeconds >= Config.MaxWaitSeconds;
        if (inputs.Blocker != null && !capped)
        {
            _waitReason = inputs.Blocker;
            return ScheduleAction.None;
        }

        var rule = Config.Rules[win];
        string reason = ReasonText(rule, path);
        if (inputs.Blocker != null) reason += $"; waited {Config.MaxWaitSeconds} s for {inputs.Blocker}";
        MarkFired(rule, now, reason);
        return new ScheduleAction(ScheduleActionKind.LoadMeta, win, rule.Meta, reason, inputs.Blocker != null);
    }

    /// <summary>The meta could not be loaded: the rule shows the error and is skipped until reset or edited.</summary>
    public void ReportLoadFailed(int index, string error)
    {
        if (index < 0 || index >= Config.Rules.Count) return;
        Config.Rules[index].Error = string.IsNullOrEmpty(error) ? "load failed" : error;
        LastSwitchText = $"failed: {Config.Rules[index].Meta} ({Config.Rules[index].Error})";
    }

    /// <summary>
    /// First due rule, top to bottom. A repeating quest rule that already fired and whose
    /// quest is still ready holds first place: nothing below it fires (returns -1).
    /// </summary>
    public int FindWinner(DateTime now, string currentMetaPath)
    {
        for (int i = 0; i < Config.Rules.Count; i++)
        {
            var r = Config.Rules[i];
            if (!r.Enabled || (r.Fired && !r.Repeat) || r.Error.Length > 0) continue;
            if (r.Trigger == ScheduleTrigger.QuestReady && r.Latched)
            {
                if (GetQuestState(r.Quest, now, out _) == QuestState.Ready) return -1;
                continue;
            }
            if (!MetaMatches(r.OnlyOnMeta, currentMetaPath)) continue;
            if (IsDue(r, now)) return i;
        }
        return -1;
    }

    public bool IsDue(MetaScheduleRule r, DateTime now)
    {
        if (r.Meta.Length == 0) return false;
        switch (r.Trigger)
        {
            case ScheduleTrigger.QuestReady:
                return GetQuestState(r.Quest, now, out _) == QuestState.Ready;
            case ScheduleTrigger.AfterMinutesOnMeta:
                return _metaSinceUtc != DateTime.MinValue && (now - _metaSinceUtc).TotalMinutes >= r.Minutes;
            case ScheduleTrigger.EveryMinutes:
                return r.EveryAnchorUtc != DateTime.MinValue && (now - r.EveryAnchorUtc).TotalMinutes >= r.Minutes;
            case ScheduleTrigger.Countdown:
                return r.CountdownEndsUnixMs != 0 && ToUnixMs(now) >= r.CountdownEndsUnixMs;
        }
        return false;
    }

    private void MarkFired(MetaScheduleRule r, DateTime now, string reason)
    {
        r.FireCount++;
        r.LastFiredUtc = now;
        _lastSwitchUtc = now;
        _metaSinceUtc = now;   // a reload of the same meta counts as a new load
        _pendingRule = -1;
        _waitReason = string.Empty;
        if (!r.Repeat) r.Fired = true;
        switch (r.Trigger)
        {
            case ScheduleTrigger.QuestReady: r.Latched = r.Repeat; break;
            case ScheduleTrigger.EveryMinutes: r.EveryAnchorUtc = now; break;
            case ScheduleTrigger.Countdown:
                r.CountdownEndsUnixMs = r.Repeat ? ToUnixMs(now) + (long)(r.Minutes * 60_000) : 0;
                break;
        }
        LastSwitchText = $"{r.Meta} ({reason})";
        _dirty = true;
    }

    public static string ReasonText(MetaScheduleRule r, string currentMetaPath) => r.Trigger switch
    {
        ScheduleTrigger.QuestReady => $"quest {r.Quest} ready",
        ScheduleTrigger.AfterMinutesOnMeta => $"{Num(r.Minutes)} min on {(currentMetaPath.Length > 0 ? Path.GetFileName(currentMetaPath) : "no meta")}",
        ScheduleTrigger.EveryMinutes => $"every {Num(r.Minutes)} min",
        ScheduleTrigger.Countdown => $"{Num(r.Minutes)} min countdown done",
        _ => "timer",
    };

    public static string TriggerText(MetaScheduleRule r) => r.Trigger switch
    {
        ScheduleTrigger.QuestReady => $"quest {(r.Quest.Length > 0 ? r.Quest : "?")} ready",
        ScheduleTrigger.AfterMinutesOnMeta => $"after {Num(r.Minutes)} min on the meta",
        ScheduleTrigger.EveryMinutes => $"every {Num(r.Minutes)} min",
        ScheduleTrigger.Countdown => $"countdown {Num(r.Minutes)} min",
        _ => "?",
    };

    /// <summary>Only-on filter: empty matches anything; names compare without extension, ignoring case.</summary>
    public static bool MetaMatches(string onlyOn, string currentMetaPath)
    {
        if (string.IsNullOrWhiteSpace(onlyOn)) return true;
        if (string.IsNullOrEmpty(currentMetaPath)) return false;
        return string.Equals(Path.GetFileNameWithoutExtension(onlyOn.Trim()),
            Path.GetFileNameWithoutExtension(currentMetaPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static long ToUnixMs(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;

    /// <summary>A never-done quest's ready time (unix ms 1; 0 means "unknown" in the snapshot).</summary>
    private static readonly DateTime NeverDoneReadyAt = DateTime.UnixEpoch.AddMilliseconds(1);

    // =====================================================================
    //  Status (chat) and snapshot (engine panel)
    // =====================================================================

    /// <summary>When rule <paramref name="r"/> is due: a time, MaxValue = never, null = not known / not running.</summary>
    public DateTime? DueAt(MetaScheduleRule r, DateTime now)
    {
        switch (r.Trigger)
        {
            case ScheduleTrigger.QuestReady:
                return GetQuestState(r.Quest, now, out DateTime at) switch
                {
                    QuestState.Unknown => null,
                    QuestState.Never => DateTime.MaxValue,
                    // Never done: "ready since forever" (a fixed time, so the snapshot doesn't change every call).
                    QuestState.Ready => at == DateTime.MinValue ? NeverDoneReadyAt : at,
                    _ => at,
                };
            case ScheduleTrigger.AfterMinutesOnMeta:
                return _metaSinceUtc == DateTime.MinValue ? null : _metaSinceUtc.AddMinutes(r.Minutes);
            case ScheduleTrigger.EveryMinutes:
                return r.EveryAnchorUtc == DateTime.MinValue ? null : r.EveryAnchorUtc.AddMinutes(r.Minutes);
            case ScheduleTrigger.Countdown:
                return r.CountdownEndsUnixMs == 0 ? null : DateTime.UnixEpoch.AddMilliseconds(r.CountdownEndsUnixMs);
        }
        return null;
    }

    /// <summary>A rule's state in a few words, or "" when its due time says it all.</summary>
    public string RuleState(int index, DateTime now)
    {
        var r = Config.Rules[index];
        if (!r.Enabled) return "off";
        if (r.Error.Length > 0) return "error: " + r.Error;
        if (r.Fired && !r.Repeat) return "fired (one-shot)";
        if (r.Meta.Length == 0) return "no meta picked";
        if (index == _pendingRule && _waitReason.Length > 0) return "waiting: " + _waitReason;
        if (index == _pendingRule) return "switching";
        if (r.Trigger == ScheduleTrigger.QuestReady)
        {
            if (r.Quest.Length == 0) return "no quest named";
            QuestState qs = GetQuestState(r.Quest, now, out _);
            if (r.Latched && qs == QuestState.Ready) return "fired, holding while the quest is ready";
            if (qs == QuestState.Unknown) return _serverDisabled ? "/myquests is off on this server" : "no /myquests yet";
            if (qs == QuestState.Never) return "never (solve limit reached)";
        }
        if (r.Trigger == ScheduleTrigger.Countdown && r.CountdownEndsUnixMs == 0) return "not started";
        return string.Empty;
    }

    /// <summary>The /ra metamgr status lines.</summary>
    public List<string> StatusLines(DateTime now, string currentMetaPath)
    {
        var lines = new List<string>();
        var c = Config;
        string poll = !HasQuestRules ? "no quest rules, not polling"
            : _serverDisabled ? "the server has /myquests off"
            : _lastPollUtc == DateTime.MinValue ? $"polls every {c.PollMinutes} min"
            : $"polls every {c.PollMinutes} min, last {Ago(now - _lastPollUtc)} ago, {_quests.Count} quest(s)";
        lines.Add($"Meta Manager is {(c.Enabled ? "ON" : "OFF")}: {c.Rules.Count} rule(s); {poll}; wait up to {c.MaxWaitSeconds} s, gap {c.MinGapSeconds} s{(c.AllowWhileStopped ? ", switches while stopped" : "")}.");
        int win = c.Enabled ? FindWinner(now, currentMetaPath) : -1;
        for (int i = 0; i < c.Rules.Count; i++)
        {
            var r = c.Rules[i];
            string when = RuleState(i, now);
            if (when.Length == 0)
            {
                DateTime? due = DueAt(r, now);
                when = due == null ? "?" : due.Value <= now ? "ready" : "in " + Ago(due.Value - now);
            }
            string mark = i == win ? " <- first match" : "";
            lines.Add($"#{i + 1} when {TriggerText(r)}{(r.OnlyOnMeta.Length > 0 ? $" (on {r.OnlyOnMeta})" : "")}, load {(r.Meta.Length > 0 ? r.Meta : "?")}"
                + $" [{(r.Repeat ? "repeat" : "once")}] - {when}{(r.FireCount > 0 ? $", fired {r.FireCount}x" : "")}{mark}");
        }
        if (LastSwitchText.Length > 0) lines.Add("Last: " + LastSwitchText);
        return lines;
    }

    public static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 24) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalMinutes >= 60) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalSeconds >= 60) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalSeconds}s";
    }

    /// <summary>
    /// The engine panel's snapshot (the "schedule" object in RynthPluginGetMetaJson). Times are
    /// absolute (unix ms) so the text only changes on events; the panel counts down itself.
    /// dueAtMs: 0 = unknown / not running, -1 = never.
    /// </summary>
    public string BuildSnapshotJson(DateTime now, string currentMetaPath)
    {
        var c = Config;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256 + c.Rules.Count * 200);
        sb.Append('{');
        Bool(sb, "enabled", c.Enabled).Append(',');
        sb.Append("\"pollMinutes\":").Append(c.PollMinutes).Append(',');
        sb.Append("\"maxWaitSeconds\":").Append(c.MaxWaitSeconds).Append(',');
        sb.Append("\"minGapSeconds\":").Append(c.MinGapSeconds).Append(',');
        Bool(sb, "allowWhileStopped", c.AllowWhileStopped).Append(',');
        Bool(sb, "polling", c.Enabled && HasQuestRules && !_serverDisabled).Append(',');
        Bool(sb, "serverDisabled", _serverDisabled).Append(',');
        Bool(sb, "haveQuestList", _haveFullList).Append(',');
        sb.Append("\"questCount\":").Append(_quests.Count).Append(',');
        sb.Append("\"lastPollMs\":").Append(_lastPollUtc == DateTime.MinValue ? 0 : ToUnixMs(_lastPollUtc)).Append(',');
        Str(sb, "lastSwitch", LastSwitchText).Append(',');
        sb.Append("\"lastSwitchMs\":").Append(_lastSwitchUtc == DateTime.MinValue ? 0 : ToUnixMs(_lastSwitchUtc)).Append(',');
        int win = c.Enabled ? (_pendingRule >= 0 ? _pendingRule : FindWinner(now, currentMetaPath)) : -1;
        sb.Append("\"firstMatch\":").Append(win).Append(',');
        sb.Append("\"rules\":[");
        for (int i = 0; i < c.Rules.Count; i++)
        {
            var r = c.Rules[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Bool(sb, "enabled", r.Enabled).Append(',');
            sb.Append("\"trigger\":").Append((int)r.Trigger).Append(',');
            Str(sb, "quest", r.Quest).Append(',');
            sb.Append("\"minutes\":").Append(r.Minutes.ToString("0.###", inv)).Append(',');
            Str(sb, "meta", r.Meta).Append(',');
            Str(sb, "onlyOnMeta", r.OnlyOnMeta).Append(',');
            Bool(sb, "repeat", r.Repeat).Append(',');
            Bool(sb, "fired", r.Fired).Append(',');
            sb.Append("\"fireCount\":").Append(r.FireCount).Append(',');
            sb.Append("\"lastFiredMs\":").Append(r.LastFiredUtc == DateTime.MinValue ? 0 : ToUnixMs(r.LastFiredUtc)).Append(',');
            DateTime? due = DueAt(r, now);
            long dueMs = due == null ? 0 : due.Value == DateTime.MaxValue ? -1 : ToUnixMs(due.Value);
            sb.Append("\"dueAtMs\":").Append(dueMs).Append(',');
            Str(sb, "state", RuleState(i, now));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static StringBuilder Bool(StringBuilder sb, string key, bool v)
        => sb.Append('"').Append(key).Append("\":").Append(v ? "true" : "false");

    private static StringBuilder Str(StringBuilder sb, string key, string v)
    {
        sb.Append('"').Append(key).Append("\":\"");
        foreach (char ch in v ?? string.Empty)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"');
    }
}
