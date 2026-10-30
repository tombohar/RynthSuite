// ============================================================================
//  RynthSuite - Plugins/Shared/CommandReply.cs
//  How a plugin answers a chat command (2026-10-28, the owner's rule): every
//  command a player types gets one clear line back, what changed and the value
//  now, or why not and the usage (more lines only for list, status and help).
//  Never silent. Compiled into each plugin that answers commands (linked
//  source), RynthServers' server plugins included.
//
//  Colours: Ok lines go to chat as type 1 (AC's green, what the plugins always
//  used), Fail lines as type 2 (white, RynthAi's colour for warnings). The
//  engine's chat window files both under Rynth by their "[Rynth..." prefix and
//  draws them in that tab's colour.
//
//  Why a queue: off AC's main thread Host.WriteToChat drops a line that comes
//  within 100 ms of any plugin's previous one (the engine's guard for AC's chat
//  buffer). Commands run on the plugin pump, so a reply right after another
//  plugin's line, or the second line of a help text, used to vanish. A line is
//  written at once when nothing waits; one the host refuses (and every line
//  after it, so they stay in order) waits here, and Flush (once per tick) offers
//  the first again until the host takes it. A line that still can't be written
//  after MaxTries ticks (about 1.5 s) goes to the log and the next one moves up.
//
//  Quiet: a plugin that can tell a command sent by a meta or script from a typed
//  one passes quiet: true for the script's successes (logged only); failures
//  always show. A plugin that can't tell always replies.
//
//  Threads: Ok / Fail from any thread; Flush on the plugin pump (OnTick).
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RynthCore.Plugin.Shared;

public sealed class CommandReply
{
    /// <summary>Chat type of a line saying it worked (AC's green).</summary>
    public const int OkChatType = 1;

    /// <summary>Chat type of a line saying why not (white; RynthAi's warnings use it too).</summary>
    public const int FailChatType = 2;

    /// <summary>Lines kept waiting at most; more are logged instead (a runaway loop can't fill memory).</summary>
    public const int MaxQueued = 128;

    /// <summary>Ticks one line is offered to the host before it goes to the log instead.</summary>
    public const int MaxTries = 90;

    private readonly ConcurrentQueue<(string Line, int Type)> _queue = new();
    private readonly Func<string, int, bool> _write;
    private readonly Action<string>? _log;
    private int _tries;
    private int _count;

    /// <param name="tag">The plugin's prefix, "[RynthNav]"; put in front of each line that has none.</param>
    /// <param name="write">Host.WriteToChat: false when the host didn't take the line.</param>
    /// <param name="log">The plugin's log (quiet lines, failures, lines that couldn't be written).</param>
    public CommandReply(string tag, Func<string, int, bool> write, Action<string>? log = null)
    {
        Tag = tag ?? string.Empty;
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _log = log;
    }

    /// <summary>The plugin's prefix ("[RynthNav]").</summary>
    public string Tag { get; }

    /// <summary>Lines waiting to be written.</summary>
    public int Pending => _queue.Count;

    /// <summary>Every reply given so far, quiet ones included: a caller (or a test) sees whether a command answered.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>It worked: what changed and the value now. <paramref name="quiet"/>: a script's command, logged only.</summary>
    public void Ok(string text, bool quiet = false) => Add(text, OkChatType, quiet);

    /// <summary>It didn't: why, and the usage when the words were wrong. Always shown.</summary>
    public void Fail(string text) => Add(text, FailChatType, quiet: false);

    /// <summary><see cref="Ok"/> or <see cref="Fail"/> by <paramref name="ok"/>.</summary>
    public void Say(bool ok, string text, bool quiet = false) => Add(text, ok ? OkChatType : FailChatType, quiet && ok);

    /// <summary>The line as it goes to chat: the tag in front unless the text brings its own ("[Mini Remote] ...").</summary>
    public string Format(string text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length > 0 && t[0] == '[') return t;
        return Tag.Length == 0 ? t : Tag + " " + t;
    }

    /// <summary>
    /// Offers the first waiting line again (one per call, so call it once per tick). True when a line
    /// went out. Pump thread.
    /// </summary>
    public bool Flush()
    {
        if (!_queue.TryPeek(out var head)) return false;
        if (TryWrite(head.Line, head.Type))
        {
            _queue.TryDequeue(out _);
            _tries = 0;
            return true;
        }
        if (++_tries >= MaxTries)
        {
            _queue.TryDequeue(out _);
            _tries = 0;
            _log?.Invoke("(not shown in chat) " + head.Line);
        }
        return false;
    }

    private bool TryWrite(string line, int type)
    {
        try { return _write(line, type); }
        catch { return false; }
    }

    /// <summary>Forgets the waiting lines (logout, shutdown).</summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
        _tries = 0;
    }

    private void Add(string text, int type, bool quiet)
    {
        // Control characters make the engine refuse a line: a text with line breaks goes out as
        // several lines (each with the tag), tabs as spaces.
        string[] parts = (text ?? string.Empty).Replace("\r", string.Empty).Replace('\t', ' ').Split('\n');
        bool any = false;
        foreach (string part in parts)
        {
            if (part.Trim().Length == 0) continue;
            string line = Format(part);
            any = true;
            if (quiet || type == FailChatType) _log?.Invoke(line);
            if (quiet) continue;
            // Nothing waiting: straight to the host; it may refuse (the 100 ms rule), then the line waits.
            if (_queue.IsEmpty && TryWrite(line, type)) continue;
            if (_queue.Count >= MaxQueued) { _log?.Invoke("(chat queue full) " + line); continue; }
            _queue.Enqueue((line, type));
        }
        if (any) Interlocked.Increment(ref _count);
    }

    // ── Wording shared by the plugins ────────────────────────────────────

    /// <summary>"Vision window shown." / "Vision window hidden."</summary>
    public static string Shown(string what, bool shown) => $"{what} {(shown ? "shown" : "hidden")}.";

    /// <summary>"on" / "off".</summary>
    public static string OnOff(bool on) => on ? "on" : "off";
}
