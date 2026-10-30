// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>One quest flag the character holds, as /myquests reports it.</summary>
internal sealed class QuestFlag
{
    public string Key = "";
    public string Description = "";
    public int Solves;
    /// <summary>-1 = no limit (ACE).</summary>
    public int MaxSolves = -1;
    public DateTime CompletedOnUtc = DateTime.MinValue;
    public TimeSpan RepeatTime = TimeSpan.Zero;

    public TimeSpan NextAvailableTime(DateTime utcNow) => CompletedOnUtc + RepeatTime - utcNow;

    /// <summary>ACE: a quest with a solve limit that has reached it can never be solved again.</summary>
    public bool AtSolveLimit => MaxSolves > -1 && Solves >= MaxSolves;

    public bool Ready(DateTime utcNow) => NextAvailableTime(utcNow).TotalSeconds <= 0;

    public string NextAvailable(DateTime utcNow)
    {
        TimeSpan d = NextAvailableTime(utcNow);
        return d.TotalSeconds > 0 ? TimeText.Friendly(d) : "ready";
    }
}

/// <summary>What one chat line is, as far as /myquests goes.</summary>
internal enum MyQuestsLine { None, Quest, Empty, Disabled, Throttled }

/// <summary>
/// The character's quest flags, read from the server's /myquests reply (ACE: one Broadcast line
/// per flag, "name - N solves (unix)"message" maxSolves minDelta"). Every reply is the whole
/// list, so a burst of lines replaces the flags once it goes quiet, whoever asked: RynthOracle,
/// the player, or RynthAi's meta manager (which polls and hides its own replies).
///
/// Threads: everything runs on the plugin pump, except <see cref="NoteTypedCommand"/>, which the
/// chat bar calls on AC's thread (it only touches interlocked fields).
/// </summary>
internal sealed class QuestFlagStore
{
    // Optional chat timestamp, then the flag line. Anchored so a pasted 'Bob says, "x - 1 solves (0)"'
    // can't inject a flag (upstream's hardening). ACE puts no space between ")" and the quote.
    private static readonly Regex LineRegex = new(
        @"^\s*(?:(?:\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AP]M)?\s+)|(?:\[[^\]]+\][\s:]*))*"
        + @"(?<key>\S+) - (?<solves>-?\d+) solves \((?<completed>\d{0,11})\)\s*""?(?:(?<desc>.*)"" (?<max>-?\d+) (?<delta>\d+))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public const string EmptyText = "Quest list is empty.";
    public const string DisabledText = "The command \"myquests\" is not currently enabled on this server.";
    /// <summary>ACECustom's per-session throttle reply ("... may only be run once every 60 seconds.").</summary>
    public const string ThrottledText = "[MyQuests] This command may only be run once every";

    private const int QuietMs = 1500;          // a reply is complete once no line came for this long
    private const int RequestTimeoutMs = 10_000;
    private const int MinRequestGapMs = 5_000;
    /// <summary>ACECustom answers one /myquests a minute per session (whoever sent the last one).</summary>
    private const int ServerThrottleMs = 60_000;
    /// <summary>
    /// A reply this much smaller than the flags already held is taken as cut short (chat lines
    /// dropped before they reached the plugin), so it updates the flags instead of replacing them.
    /// </summary>
    private const double PartialReplyRatio = 0.8;
    /// <summary>Below this many flags held, a smaller reply always replaces (nothing much to lose).</summary>
    private const int PartialReplyMinHeld = 20;

    public Dictionary<string, QuestFlag> Flags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, QuestFlag>? _pending;
    private long _lastLineMs;

    public DateTime LastReadUtc { get; private set; } = DateTime.MinValue;
    public bool ServerDisabled { get; private set; }
    /// <summary>The server refused the last /myquests (its once-a-minute limit) until this tick.</summary>
    public long ThrottledUntilMs { get; private set; } = long.MinValue / 2;
    /// <summary>This server has shown its throttle line this session, so every reply starts its minute.</summary>
    private bool _serverThrottles;
    /// <summary>
    /// Set when the last reply looked cut short and was merged into the held flags (lines in the
    /// reply, flags held before it). The plugin logs it once and clears it.
    /// </summary>
    public (int Got, int Had)? LastPartialReply { get; set; }
    /// <summary>Bumps whenever the flags change (views rebuild their caches on it).</summary>
    public int Revision { get; private set; }

    // Our own request: its reply may be hidden from chat.
    public bool AwaitingOurs { get; private set; }
    private long _requestedMs = long.MinValue / 2;
    private int _selfSends;      // our "/myquests" sends not yet seen by the chat bar
    private int _typedByPlayer;  // the player typed /myquests (AC's thread sets it)

    public bool Loading => _pending != null || AwaitingOurs;

    /// <summary>Seconds left on the server's /myquests limit (0 when a request would be answered).</summary>
    public int ThrottleSecondsLeft(long nowMs) =>
        nowMs < ThrottledUntilMs ? (int)((ThrottledUntilMs - nowMs + 999) / 1000) : 0;

    /// <summary>Sends /myquests unless one went out moments ago. Returns false if nothing was sent.</summary>
    public bool Request(Func<string, bool> invokeChat, long nowMs)
    {
        if (nowMs - _requestedMs < MinRequestGapMs) return false;
        // The server would only answer with its throttle line; don't spend the request.
        if (nowMs < ThrottledUntilMs) return false;
        _requestedMs = nowMs;
        Interlocked.Increment(ref _selfSends);
        if (!invokeChat("/myquests"))
        {
            Interlocked.Decrement(ref _selfSends);
            return false;
        }
        AwaitingOurs = true;
        return true;
    }

    /// <summary>AC's thread (chat bar): a "/myquests" went through it. Ours, or typed by the player?</summary>
    public void NoteTypedCommand()
    {
        if (Interlocked.Decrement(ref _selfSends) >= 0) return;
        Interlocked.Exchange(ref _selfSends, 0);
        Interlocked.Exchange(ref _typedByPlayer, 1);
    }

    /// <summary>Classifies a chat line and takes in its flag.</summary>
    public MyQuestsLine OnChatLine(string? text, long nowMs)
    {
        if (string.IsNullOrEmpty(text)) return MyQuestsLine.None;
        string line = text.Trim();

        if (line.EndsWith(EmptyText, StringComparison.Ordinal))
        {
            _pending = new Dictionary<string, QuestFlag>(StringComparer.OrdinalIgnoreCase);
            _lastLineMs = nowMs - QuietMs; // nothing follows it
            ServerDisabled = false;
            return MyQuestsLine.Empty;
        }
        if (line.EndsWith(DisabledText, StringComparison.Ordinal))
        {
            ServerDisabled = true;
            AwaitingOurs = false;
            Revision++;
            return MyQuestsLine.Disabled;
        }
        if (line.Contains(ThrottledText, StringComparison.Ordinal))
        {
            // No reply is coming: stop "Reading..." and keep the flags already held. The minute
            // really started at the last answered request; a full minute from now is the safe side.
            _serverThrottles = true;
            ThrottledUntilMs = nowMs + ServerThrottleMs;
            AwaitingOurs = false;
            Revision++;
            return MyQuestsLine.Throttled;
        }
        if (line.IndexOf(" solves (", StringComparison.Ordinal) < 0) return MyQuestsLine.None;

        QuestFlag? flag = Parse(line);
        if (flag == null) return MyQuestsLine.None;
        _pending ??= new Dictionary<string, QuestFlag>(StringComparer.OrdinalIgnoreCase);
        _pending[flag.Key] = flag;
        _lastLineMs = nowMs;
        ServerDisabled = false;
        return MyQuestsLine.Quest;
    }

    /// <summary>Pump tick: completes a reply once it has gone quiet.</summary>
    public void Tick(long nowMs)
    {
        if (Interlocked.Exchange(ref _typedByPlayer, 0) != 0)
            AwaitingOurs = false; // the player asked too: their reply shows in chat

        if (_pending != null && nowMs - _lastLineMs >= QuietMs)
        {
            Dictionary<string, QuestFlag> reply = _pending;
            _pending = null;
            int had = Flags.Count;
            // A full reply replaces the list (a flag the server dropped goes away). One much
            // smaller than what is held lost lines on the way (the engine's chat queue drops the
            // oldest of a long burst): update what came and keep the rest, rather than blanking.
            if (had >= PartialReplyMinHeld && reply.Count > 0 && reply.Count < had * PartialReplyRatio)
            {
                var merged = new Dictionary<string, QuestFlag>(Flags, StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, QuestFlag> kv in reply) merged[kv.Key] = kv.Value;
                Flags = merged;
                LastPartialReply = (reply.Count, had);
            }
            else
            {
                Flags = reply;
            }
            LastReadUtc = DateTime.UtcNow;
            // On a throttling server any reply (ours, the player's, RynthAi's poll) starts its minute.
            if (_serverThrottles)
                ThrottledUntilMs = nowMs + ServerThrottleMs - QuietMs;
            AwaitingOurs = false;
            Revision++;
        }
        else if (AwaitingOurs && _pending == null && nowMs - _requestedMs > RequestTimeoutMs)
        {
            AwaitingOurs = false; // no reply (not in world, server busy)
        }
    }

    public void Clear()
    {
        Flags = new Dictionary<string, QuestFlag>(StringComparer.OrdinalIgnoreCase);
        _pending = null;
        AwaitingOurs = false;
        LastReadUtc = DateTime.MinValue;
        ServerDisabled = false;
        ThrottledUntilMs = long.MinValue / 2;
        _serverThrottles = false;
        LastPartialReply = null;
        Revision++;
    }

    public bool TryGet(string flag, out QuestFlag q) => Flags.TryGetValue(flag, out q!);

    internal static QuestFlag? Parse(string line)
    {
        Match m = LineRegex.Match(line);
        if (!m.Success) return null;

        var q = new QuestFlag
        {
            Key = m.Groups["key"].Value.ToLowerInvariant(),
            Description = m.Groups["desc"].Value.Trim().Trim('"').Trim(),
        };
        int.TryParse(m.Groups["solves"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out q.Solves);
        if (m.Groups["max"].Success && int.TryParse(m.Groups["max"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int max))
            q.MaxSolves = max;
        if (long.TryParse(m.Groups["completed"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long completed) && completed > 0)
            q.CompletedOnUtc = DateTime.UnixEpoch.AddSeconds(completed);
        if (m.Groups["delta"].Success && long.TryParse(m.Groups["delta"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long delta))
            q.RepeatTime = TimeSpan.FromSeconds(Math.Min(delta, (long)TimeSpan.MaxValue.TotalSeconds / 2));
        return q;
    }
}
