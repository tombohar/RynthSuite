// ChatTranslator.cs — Auto-translation of AC chat (UB-IT ChatTranslateTool port, multi-provider).
//
// Inbound   chat lines on the ticked channels are translated into the receive language and written
//           back to chat as "[Translate] [Channel] Name (es): text". Host.WriteToChat output passes the
//           engine's chat hook, so it shows in AC's chat window and in RynthChat.
// Outbound  typed lines on the ticked channels are swallowed, translated into the send language and
//           resent with the same command prefix ("/f ", "/t Bob, ", ...).
//
// Threads   OnChatWindowText / TryInterceptOutbound / UI calls only enqueue (any thread). Tick runs on
//           the plugin pump thread: it owns the queues, starts one HTTP request at a time (spaced by
//           MinIntervalMs) and applies results. The window reads the log through a lock.
//
// Host.WriteToChat off AC's main thread drops calls made within 100 ms of the previous one, so echoes
// go through a queue drained at EchoSpacingMs and retried when the host reports a drop.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Translate;

internal enum TranslateDirection { In, Out, Test }

/// <summary>One line in the Translate window log (immutable once added).</summary>
internal sealed record TranslateLogEntry(
    DateTime Time,
    TranslateDirection Direction,
    TranslateChannel? Channel,
    string Speaker,
    string Original,
    string Translated,
    string From,
    string To,
    string Error);

internal sealed class ChatTranslator : IDisposable
{
    /// <summary>Prefix of every line the translator writes, so its own echoes are never re-translated.</summary>
    public const string EchoPrefix = "[Translate]";

    private const int EchoSpacingMs = 150;
    private const int EchoMaxAttempts = 20;
    private const int MaxInboundQueue = 40;
    private const int DedupWindowMs = 5000;
    private const int ResendGuardMs = 15000;
    private const int SaveEveryMs = 2000;
    private const int ErrorChatEveryMs = 30000;
    private const int ChatTypeSystem = 1;
    private const int ChatTypeSpellcasting = 0x11;

    private enum JobKind { Inbound, Outbound, SendRaw, Test }

    private sealed record Job(JobKind Kind, TranslateChannel? Channel, string Speaker, string Text,
        string Source, string Target, string Prefix);

    private readonly RynthCoreHost _host;
    private readonly Func<string> _myName;
    private readonly TranslateStore _store;
    private readonly CancellationTokenSource _cts = new();

    // Any thread → pump.
    private readonly ConcurrentQueue<Job> _incoming = new();
    private readonly ConcurrentQueue<(Job Job, TranslateResult Result)> _done = new();
    // Translated bodies we are about to resend; OnChatBarEnter lets these through untouched.
    private readonly ConcurrentDictionary<string, long> _resend = new(StringComparer.Ordinal);

    // Pump thread only.
    private readonly Queue<Job> _outQueue = new();
    private readonly LinkedList<Job> _inQueue = new();
    private readonly Dictionary<string, long> _recent = new(StringComparer.Ordinal);
    private readonly Queue<(string Text, int ChatType, int Attempts)> _echo = new();
    private long _lastEchoMs;
    private long _lastStartMs;
    private long _lastSaveMs;
    private long _lastErrorChatMs = -ErrorChatEveryMs;
    private int _inFlight;

    private readonly object _logLock = new();
    private readonly List<TranslateLogEntry> _log = new();
    private TranslateLogEntry[] _logSnapshot = Array.Empty<TranslateLogEntry>();

    public TranslateSettings Settings { get; }

    /// <summary>Last provider error (empty after a success). Read by the UI.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>Characters sent to the provider this session (most plans bill per character).</summary>
    public long CharsSent => Interlocked.Read(ref _charsSent);
    private long _charsSent;

    public int Translated => Volatile.Read(ref _translated);
    private int _translated;

    /// <summary>Requests waiting or in flight (approximate when read off the pump thread).</summary>
    public int Pending => _inQueue.Count + _outQueue.Count + _incoming.Count + Volatile.Read(ref _inFlight);

    public ChatTranslator(RynthCoreHost host, Func<string> myName, TranslateStore? store = null)
    {
        _host = host;
        _myName = myName;
        _store = store ?? new TranslateStore();
        Settings = _store.Load();
    }

    /// <summary>Per-login reset: send language back to the default, queues cleared.</summary>
    public void ResetSession()
    {
        Settings.CurrentSendLanguage = Settings.DefaultSendLanguage;
        while (_incoming.TryDequeue(out _)) { }
        _outQueue.Clear();
        _inQueue.Clear();
        _echo.Clear();
        _recent.Clear();
        _resend.Clear();
    }

    // ── Entry points (any thread) ──────────────────────────────────────────

    /// <summary>Inbound chat line from the AC chat window.</summary>
    public void OnChatWindowText(string text, int chatType)
    {
        var s = Settings;
        if (!s.Enabled || !s.EnableInbound || string.IsNullOrEmpty(text)) return;
        if (chatType == ChatTypeSpellcasting || text.StartsWith(EchoPrefix, StringComparison.Ordinal)) return;
        if (!TryParseInbound(text, chatType, out var ch, out string speaker, out string msg, out bool own)) return;
        if (!s.ListensTo(ch) || (own && s.SkipOwn)) return;
        if (IsOwnName(speaker) && s.SkipOwn) return;

        msg = AcChatEscapes.Decode(msg).Trim();
        if (msg.Length == 0) return;
        _incoming.Enqueue(new Job(JobKind.Inbound, ch, speaker, msg, s.InboundSource, s.MyLanguage, string.Empty));
    }

    /// <summary>
    /// Typed chat line (OnChatBarEnter). Returns true when the translator took it over and the caller
    /// must swallow the original.
    /// </summary>
    public bool TryInterceptOutbound(string text)
    {
        var s = Settings;
        if (!TranslateChannels.TryParseOutbound(text, out var line)) return false;
        string body = line.Body.Trim();
        if (_resend.TryRemove(body, out _)) return false;
        if (!s.Enabled || !s.EnableOutbound || !s.SendsOn(line.Channel) || body.Length == 0) return false;

        if (s.RawPrefix.Length > 0 && body.StartsWith(s.RawPrefix, StringComparison.Ordinal))
        {
            string raw = body.Substring(s.RawPrefix.Length).TrimStart();
            if (raw.Length == 0) return false;
            _incoming.Enqueue(new Job(JobKind.SendRaw, line.Channel, string.Empty, raw, string.Empty, string.Empty, line.Prefix));
            return true;
        }

        string target = s.CurrentSendLanguage;
        if (TranslateLanguages.BaseOf(target) == TranslateLanguages.BaseOf(s.MyLanguage)) return false;
        _incoming.Enqueue(new Job(JobKind.Outbound, line.Channel, string.Empty, body, TranslateLanguages.Auto, target, line.Prefix));
        return true;
    }

    /// <summary>Translate window compose box: translate into the send language and send on <paramref name="ch"/>.</summary>
    public void Compose(TranslateChannel ch, string tellTarget, string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        var s = Settings;
        string prefix = TranslateChannels.SendPrefix(ch, tellTarget);
        bool same = TranslateLanguages.BaseOf(s.CurrentSendLanguage) == TranslateLanguages.BaseOf(s.MyLanguage);
        _incoming.Enqueue(same
            ? new Job(JobKind.SendRaw, ch, string.Empty, text, string.Empty, string.Empty, prefix)
            : new Job(JobKind.Outbound, ch, string.Empty, text, TranslateLanguages.Auto, s.CurrentSendLanguage, prefix));
    }

    /// <summary>Translates <paramref name="text"/> into <paramref name="target"/> (send language when null) without sending.</summary>
    public void Test(string text, string? target = null)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        _incoming.Enqueue(new Job(JobKind.Test, null, string.Empty, text, TranslateLanguages.Auto,
            target ?? Settings.CurrentSendLanguage, string.Empty));
    }

    // ── Pump thread ────────────────────────────────────────────────────────

    public void Tick()
    {
        long now = Environment.TickCount64;
        DrainIncoming(now);
        while (_done.TryDequeue(out var d)) ApplyResult(d.Job, d.Result);
        StartNextRequest(now);
        DrainEcho(now);

        if (now - _lastSaveMs >= SaveEveryMs)
        {
            _lastSaveMs = now;
            PruneGuards(now);
            _store.SaveIfDirty(Settings);
        }
    }

    private void DrainIncoming(long now)
    {
        while (_incoming.TryDequeue(out var job))
        {
            switch (job.Kind)
            {
                case JobKind.SendRaw:
                    Send(job.Prefix, job.Text);
                    AddLog(new TranslateLogEntry(RynthClock.Now, TranslateDirection.Out, job.Channel, string.Empty,
                        job.Text, job.Text, string.Empty, string.Empty, "sent untranslated"));
                    break;
                case JobKind.Inbound:
                    string key = $"{job.Channel}|{job.Speaker}|{job.Text}";
                    if (_recent.TryGetValue(key, out long seen) && now - seen < DedupWindowMs) break;
                    _recent[key] = now;
                    while (_inQueue.Count >= MaxInboundQueue) _inQueue.RemoveFirst();
                    _inQueue.AddLast(job);
                    break;
                default:
                    _outQueue.Enqueue(job);
                    break;
            }
        }
    }

    private void StartNextRequest(long now)
    {
        if (Volatile.Read(ref _inFlight) != 0) return;
        if (now - _lastStartMs < Settings.MinIntervalMs) return;

        Job? job = null;
        if (_outQueue.Count > 0) job = _outQueue.Dequeue();
        else if (_inQueue.Count > 0) { job = _inQueue.First!.Value; _inQueue.RemoveFirst(); }
        if (job == null) return;

        var s = Settings;
        if (!s.Enabled && job.Kind == JobKind.Inbound) return;

        _lastStartMs = now;
        Volatile.Write(ref _inFlight, 1);
        Interlocked.Add(ref _charsSent, job.Text.Length);
        string provider = s.Provider, key = s.ApiKey, url = s.ApiUrl;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            TranslateResult r;
            try { r = await TranslateClient.TranslateAsync(provider, key, url, job.Text, job.Source, job.Target, ct).ConfigureAwait(false); }
            catch (Exception ex) { r = TranslateResult.Fail(ex.Message); }
            _done.Enqueue((job, r));
            Volatile.Write(ref _inFlight, 0);
        });
    }

    private void ApplyResult(Job job, TranslateResult r)
    {
        if (r.Ok) LastError = string.Empty;
        else
        {
            LastError = r.Error;
            RynthLog.Write(LogCat.Chat, $"[Translate] {job.Kind} failed: {r.Error}");
        }

        switch (job.Kind)
        {
            case JobKind.Inbound: ApplyInbound(job, r); break;
            case JobKind.Outbound: ApplyOutbound(job, r); break;
            case JobKind.Test: ApplyTest(job, r); break;
        }
    }

    private void ApplyInbound(Job job, TranslateResult r)
    {
        if (!r.Ok)
        {
            AddLog(new TranslateLogEntry(RynthClock.Now, TranslateDirection.In, job.Channel, job.Speaker, job.Text,
                string.Empty, job.Source, job.Target, r.Error));
            ChatErrorThrottled(r.Error);
            return;
        }

        string translated = r.Text.Trim();
        // Already in the receive language, or nothing changed: stay quiet.
        if (TranslateLanguages.BaseOf(r.DetectedSource) == TranslateLanguages.BaseOf(job.Target)) return;
        if (translated.Length == 0 || string.Equals(translated, job.Text, StringComparison.OrdinalIgnoreCase)) return;

        Interlocked.Increment(ref _translated);
        AddLog(new TranslateLogEntry(RynthClock.Now, TranslateDirection.In, job.Channel, job.Speaker, job.Text,
            translated, r.DetectedSource, job.Target, string.Empty));

        if (Settings.EchoToChat && job.Channel is { } ch)
        {
            string from = r.DetectedSource.Length > 0 ? $" ({r.DetectedSource})" : string.Empty;
            QueueEcho($"{EchoPrefix} [{TranslateChannels.Name(ch)}] {job.Speaker}{from}: {translated}", TranslateChannels.ChatType(ch));
        }
    }

    private void ApplyOutbound(Job job, TranslateResult r)
    {
        string body;
        if (r.Ok && r.Text.Trim().Length > 0)
        {
            body = r.Text.Trim();
            Interlocked.Increment(ref _translated);
        }
        else if (Settings.SendOriginalOnError)
        {
            body = job.Text;
            QueueEcho($"{EchoPrefix} Couldn't translate ({(r.Ok ? "empty result" : r.Error)}); sent the original.", ChatTypeSystem);
        }
        else
        {
            QueueEcho($"{EchoPrefix} Couldn't translate ({(r.Ok ? "empty result" : r.Error)}); message not sent.", ChatTypeSystem);
            AddLog(new TranslateLogEntry(RynthClock.Now, TranslateDirection.Out, job.Channel, string.Empty, job.Text,
                string.Empty, r.DetectedSource, job.Target, r.Ok ? "empty result" : r.Error));
            return;
        }

        string sent = Send(job.Prefix, body);
        AddLog(new TranslateLogEntry(RynthClock.Now, TranslateDirection.Out, job.Channel, string.Empty, job.Text,
            sent, r.DetectedSource, job.Target, r.Ok ? string.Empty : r.Error));

        if (r.Ok && Settings.EchoOutbound && job.Channel is { } ch)
            QueueEcho($"{EchoPrefix} [{TranslateChannels.Name(ch)} -> {job.Target}] you typed: {job.Text}", TranslateChannels.ChatType(ch));
    }

    private void ApplyTest(Job job, TranslateResult r)
    {
        AddLog(new TranslateLogEntry(RynthClock.Now, TranslateDirection.Test, null, string.Empty, job.Text,
            r.Ok ? r.Text : string.Empty, r.DetectedSource, job.Target, r.Error));
        QueueEcho(r.Ok
            ? $"{EchoPrefix} Test ({(r.DetectedSource.Length > 0 ? r.DetectedSource : "?")} -> {job.Target}): {r.Text}"
            : $"{EchoPrefix} Test failed: {r.Error}", ChatTypeSystem);
    }

    /// <summary>Sends <paramref name="prefix"/> + body through the chat parser; returns the body as sent.</summary>
    private string Send(string prefix, string body)
    {
        string encoded = AcChatEscapes.EncodeForSend(body);
        if (encoded.Length == 0) return string.Empty;
        _resend[encoded] = Environment.TickCount64;
        bool ok = _host.HasInvokeChatParser && _host.InvokeChatParser(prefix + encoded);
        if (!ok)
        {
            _resend.TryRemove(encoded, out _);
            QueueEcho($"{EchoPrefix} The client refused to send: {prefix}{encoded}", ChatTypeSystem);
        }
        return encoded;
    }

    private void QueueEcho(string text, int chatType) => _echo.Enqueue((text, chatType, 0));

    private void DrainEcho(long now)
    {
        if (_echo.Count == 0 || now - _lastEchoMs < EchoSpacingMs) return;
        var (text, type, attempts) = _echo.Peek();
        _lastEchoMs = now;
        if (_host.WriteToChat(text, type) || attempts + 1 >= EchoMaxAttempts)
        {
            _echo.Dequeue();
            return;
        }
        // Dropped by the host's rate limit: retry the same line next time.
        _echo.Dequeue();
        var rest = _echo.ToArray();
        _echo.Clear();
        _echo.Enqueue((text, type, attempts + 1));
        foreach (var e in rest) _echo.Enqueue(e);
    }

    private void ChatErrorThrottled(string error)
    {
        long now = Environment.TickCount64;
        if (now - _lastErrorChatMs < ErrorChatEveryMs) return;
        _lastErrorChatMs = now;
        QueueEcho($"{EchoPrefix} Translation error: {error}", ChatTypeSystem);
    }

    private void PruneGuards(long now)
    {
        foreach (var kv in _resend)
            if (now - kv.Value > ResendGuardMs) _resend.TryRemove(kv.Key, out _);
        if (_recent.Count > 256)
        {
            var stale = new List<string>();
            foreach (var kv in _recent) if (now - kv.Value > DedupWindowMs) stale.Add(kv.Key);
            foreach (var k in stale) _recent.Remove(k);
        }
    }

    // ── Log (pump writes, render reads) ────────────────────────────────────

    private void AddLog(TranslateLogEntry e)
    {
        lock (_logLock)
        {
            _log.Add(e);
            int max = Settings.MaxLog;
            if (_log.Count > max) _log.RemoveRange(0, _log.Count - max);
            _logSnapshot = _log.ToArray();
        }
    }

    public TranslateLogEntry[] LogSnapshot()
    {
        lock (_logLock) return _logSnapshot;
    }

    public void ClearLog()
    {
        lock (_logLock)
        {
            _log.Clear();
            _logSnapshot = Array.Empty<TranslateLogEntry>();
        }
    }

    // ── Inbound parsing ────────────────────────────────────────────────────

    // "[General] " style tab prefix some servers / chat options put in front of channel lines.
    private static readonly Regex RxBracket = new(@"^\[(?<b>[A-Za-z ]{1,24})\]\s*", RegexOptions.CultureInvariant);

    // "You say ...", "You tell Bob, ..." — the server echo of your own lines.
    private static readonly Regex RxOwn = new(@"^You\s+(?:say|tell)\b(?<mid>[^""]*)""(?<msg>.*)""\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // "Bob says, "...""  /  "Bob says on the General channel, "...""  /  "Bob tells you, "...""
    private static readonly Regex RxSpeech = new(@"^(?<name>.+?)\s+(?<verb>says|tells)\b(?<mid>[^""]*)""(?<msg>.*)""\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex RxOnChannel = new(@"on the (?<ch>[A-Za-z ]+?) channel", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a chat line into channel, speaker and message. Channel comes from (in order) a "[Name]"
    /// prefix, "on the X channel", "tells you", the chat colour, then words like "patron" / "vassal".
    /// </summary>
    internal static bool TryParseInbound(string raw, int chatType, out TranslateChannel channel,
        out string speaker, out string message, out bool own)
    {
        channel = TranslateChannel.Local;
        speaker = message = string.Empty;
        own = false;

        string t = AcChatEscapes.StripLinks(raw).Trim();
        if (t.Length < 6 || t.IndexOf('"') < 0) return false;

        TranslateChannel? hint = null;
        var b = RxBracket.Match(t);
        if (b.Success && TranslateChannels.TryParse(b.Groups["b"].Value, out var bc))
        {
            hint = bc;
            t = t.Substring(b.Length);
        }

        string mid;
        bool tellsYou;
        var mo = RxOwn.Match(t);
        if (mo.Success)
        {
            own = true;
            speaker = "You";
            mid = mo.Groups["mid"].Value;
            message = mo.Groups["msg"].Value;
            tellsYou = t.StartsWith("You tell", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var m = RxSpeech.Match(t);
            if (!m.Success) return false;
            speaker = m.Groups["name"].Value.Trim().TrimStart('+');
            mid = m.Groups["mid"].Value;
            message = m.Groups["msg"].Value;
            tellsYou = m.Groups["verb"].Value == "tells" && mid.TrimStart().StartsWith("you", StringComparison.OrdinalIgnoreCase);
            if (speaker.Length == 0 || speaker.Length > 40) return false;
        }

        TranslateChannel? ch = hint;
        if (ch == null)
        {
            var oc = RxOnChannel.Match(mid);
            if (oc.Success && TranslateChannels.TryParse(oc.Groups["ch"].Value, out var named)) ch = named;
        }
        if (ch == null && tellsYou) ch = TranslateChannel.Tell;
        ch ??= TranslateChannels.FromChatType(chatType);
        ch ??= FromWords(mid);
        if (ch == null) return false;

        channel = ch.Value;
        return message.Trim().Length > 0;
    }

    private static TranslateChannel? FromWords(string mid)
    {
        string m = mid.ToLowerInvariant();
        if (m.Contains("covassal")) return TranslateChannel.Covassals;
        if (m.Contains("vassal")) return TranslateChannel.Vassals;
        if (m.Contains("patron")) return TranslateChannel.Patron;
        if (m.Contains("monarch")) return TranslateChannel.Monarch;
        if (m.Contains("fellowship")) return TranslateChannel.Fellowship;
        if (m.Contains("allegiance")) return TranslateChannel.Allegiance;
        return null;
    }

    private bool IsOwnName(string speaker)
    {
        string me;
        try { me = _myName() ?? string.Empty; }
        catch { return false; }
        return me.Length > 0 && string.Equals(me.TrimStart('+'), speaker, StringComparison.OrdinalIgnoreCase);
    }

    // ── Language helpers (UI + commands) ───────────────────────────────────

    /// <summary>Swaps the receive and send languages for this session.</summary>
    public void SwapLanguages()
    {
        var s = Settings;
        string recv = s.MyLanguage;
        s.MyLanguage = s.CurrentSendLanguage;
        s.CurrentSendLanguage = recv;
    }

    // ── /ra translate ──────────────────────────────────────────────────────

    /// <summary>
    /// /ra translate [on|off|in on|off|out on|off|window|send &lt;lang&gt;|recv &lt;lang&gt;|default &lt;lang&gt;|swap|reset|test &lt;text&gt;|status]
    /// </summary>
    public void HandleCommand(string[] args, Action<string> chat)
    {
        var s = Settings;
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        string arg = args.Length > 1 ? args[1] : string.Empty;
        switch (verb)
        {
            case "on": s.Enabled = true; chat($"{EchoPrefix} On. {StatusLine()}"); break;
            case "off": s.Enabled = false; chat($"{EchoPrefix} Off."); break;
            case "in":
            case "out":
                bool? v = arg.Equals("on", StringComparison.OrdinalIgnoreCase) ? true
                        : arg.Equals("off", StringComparison.OrdinalIgnoreCase) ? false : null;
                if (verb == "in") s.EnableInbound = v ?? !s.EnableInbound;
                else s.EnableOutbound = v ?? !s.EnableOutbound;
                chat($"{EchoPrefix} Inbound {(s.EnableInbound ? "on" : "off")}, outbound {(s.EnableOutbound ? "on" : "off")}.");
                break;
            case "window":
            case "show":
                s.ShowWindow = !s.ShowWindow;
                break;
            case "send":
            case "recv":
            case "receive":
            case "default":
                string? code = TranslateLanguages.Normalize(arg);
                if (code == null || code == TranslateLanguages.Auto)
                {
                    chat($"{EchoPrefix} Unknown language '{arg}'. Codes: {string.Join(", ", TranslateLanguages.Codes, 1, TranslateLanguages.Codes.Length - 1)}");
                    break;
                }
                if (verb == "send") s.CurrentSendLanguage = code;
                else if (verb == "default") s.DefaultSendLanguage = code;
                else s.MyLanguage = code;
                chat($"{EchoPrefix} {StatusLine()}");
                break;
            case "swap":
                SwapLanguages();
                chat($"{EchoPrefix} {StatusLine()}");
                break;
            case "reset":
                s.CurrentSendLanguage = s.DefaultSendLanguage;
                chat($"{EchoPrefix} {StatusLine()}");
                break;
            case "test":
                string text = string.Join(" ", args, 1, Math.Max(0, args.Length - 1));
                if (text.Length == 0) { chat($"{EchoPrefix} Usage: /ra translate test <text>"); break; }
                Test(text);
                break;
            default:
                chat($"{EchoPrefix} {(s.Enabled ? "On" : "Off")}. {StatusLine()}"
                     + (LastError.Length > 0 ? $" Last error: {LastError}" : string.Empty));
                chat($"{EchoPrefix} /ra translate on|off | in|out [on|off] | window | send|recv|default <lang> | swap | reset | test <text>");
                break;
        }
    }

    public string StatusLine()
    {
        var s = Settings;
        return $"Receive {s.MyLanguage}, send {s.CurrentSendLanguage} (default {s.DefaultSendLanguage}), "
             + $"inbound {(s.EnableInbound ? "on" : "off")}, outbound {(s.EnableOutbound ? "on" : "off")}, provider {s.Provider}"
             + (s.ApiKey.Length == 0 && s.Provider != TranslateSettings.ProviderLibre ? " (no API key set)" : string.Empty) + ".";
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _store.SaveIfDirty(Settings);
        _cts.Dispose();
    }
}
