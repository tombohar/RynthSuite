using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// What a chat command (/ra, /ub, /mt, /vt) says back: the one place the rule lives (2026-10-28).
/// <list type="bullet">
/// <item>A command the player typed answers in chat, in one line (more only for lists, status and help):
///   <see cref="Ok"/> says what changed and the value now, <see cref="Fail"/> why it didn't work and the
///   usage. A typed command that said nothing at all still gets a line (<see cref="Run"/>).</item>
/// <item>A command a meta, a nav route or a script sent is quiet when it works (the log has the line),
///   so metas don't flood chat. Its errors still show in chat, each distinct one at most once a minute.</item>
/// <item><see cref="Info"/> is output the command was asked for (a list, a status, help): shown either way.</item>
/// </list>
/// Who sent the line is per thread: a command is handled start to finish on the thread it came in on.
/// </summary>
internal sealed class CommandReply
{
    /// <summary>AC chat types: RynthAi's usual lines, and its error lines (AutoVendor, salvage, pack full).</summary>
    public const int OkColor = 1, FailColor = 2;

    /// <summary>A meta repeating the same error says it in chat at most this often (the log has every one).</summary>
    public const long FailRepeatMs = 60_000;

    /// <summary>The line a typed command that answered nothing gets (the log says which command).</summary>
    public const string RanPrefix = "[RynthAi] Ran: ";

    private readonly Action<string, int> _chat;
    private readonly Action<string> _log;
    private readonly Func<bool> _metaActive;
    private readonly Dictionary<string, long> _failSaidAt = new(StringComparer.Ordinal);

    /// <summary>Milliseconds, for the once-a-minute error repeat (tests set it).</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    [ThreadStatic] private static int t_scriptDepth;   // AsScript on the stack: a script's or a route's line
    [ThreadStatic] private static int t_queued;        // RunQueued: 0 none, 1 sent by a script, 2 typed
    [ThreadStatic] private static int t_runDepth;      // Run on the stack
    [ThreadStatic] private static bool t_answered;     // the command being run has said something

    /// <param name="chat">Writes a chat line in a colour.</param>
    /// <param name="log">Writes a log line.</param>
    /// <param name="metaActive">True while a meta's action or chat command is being run.</param>
    public CommandReply(Action<string, int> chat, Action<string> log, Func<bool> metaActive)
    {
        _chat = chat;
        _log = log;
        _metaActive = metaActive;
    }

    /// <summary>True when a meta, a route or a script sent the line being handled, not the player.</summary>
    public bool FromScript => t_queued switch
    {
        1 => true,
        2 => false,
        _ => t_scriptDepth > 0 || _metaActive(),
    };

    /// <summary>It worked: what changed and the value now. Chat when typed; the log only for a meta or script.</summary>
    public void Ok(string text)
    {
        t_answered = true;
        if (FromScript) _log(text);
        else _chat(text, OkColor);
    }

    /// <summary>It didn't work: why, and the usage. Always logged; in chat when typed, and for a meta or
    /// script the first time in a minute that this exact line comes up.</summary>
    public void Fail(string text)
    {
        t_answered = true;
        _log(text);
        if (FromScript && !FirstInAMinute(text)) return;
        _chat(text, FailColor);
    }

    /// <summary>
    /// Accepted and ignored on purpose (a VTank or UtilityBelt verb with no RynthAi equivalent): said
    /// as a failure when typed, so the player knows nothing happened; only logged for a meta, which
    /// sends it on every pass and is meant to keep running.
    /// </summary>
    public void Ignored(string text)
    {
        if (FromScript) { t_answered = true; _log(text); }
        else Fail(text);
    }

    /// <summary>Output the command was asked for (a list, a status, help, a lookup): chat either way.</summary>
    public void Info(string text)
    {
        t_answered = true;
        _chat(text, OkColor);
    }

    /// <summary>The command's answer comes another way: a later line (a queued action that says what
    /// it did), the server, the engine, or the plugin the command was handed to.</summary>
    public void Answered() => t_answered = true;

    /// <summary>
    /// Handles one command line. A typed command that said nothing gets one line anyway (and the
    /// log names it, so it can get a proper answer); one that threw says so instead of nothing.
    /// A meta's or a script's line runs as it is.
    /// </summary>
    public void Run(string line, Action handle)
    {
        bool outer = t_runDepth == 0;
        bool savedAnswered = t_answered;
        if (outer) t_answered = false;
        bool typed = !FromScript;
        t_runDepth++;
        try
        {
            handle();
        }
        catch (Exception ex) when (outer && typed)
        {
            _log($"[CmdReply] '{line}' threw: {ex}");
            Fail($"[RynthAi] {line}: failed ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            t_runDepth--;
            if (outer)
            {
                if (typed && !t_answered)
                {
                    _log($"[CmdReply] '{line}' gave no reply of its own; said \"{RanPrefix}{line}\" for it.");
                    _chat(RanPrefix + line, OkColor);
                }
                t_answered = savedAnswered;
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> as a line a script or a nav route sent (quiet when it works).</summary>
    public T AsScript<T>(Func<T> action)
    {
        t_scriptDepth++;
        try { return action(); }
        finally { t_scriptDepth--; }
    }

    /// <summary>
    /// A queued command (run later on the tick) answers as the line that queued it would have:
    /// <paramref name="fromScript"/> is <see cref="FromScript"/> when it was queued.
    /// </summary>
    public void RunQueued(bool fromScript, Action action)
    {
        int saved = t_queued;
        t_queued = fromScript ? 1 : 2;
        try { action(); }
        finally { t_queued = saved; }
    }

    /// <summary>True when this error line wasn't said in the last minute (a meta repeating it says it once).</summary>
    private bool FirstInAMinute(string text)
    {
        long now = Clock();
        lock (_failSaidAt)
        {
            if (_failSaidAt.TryGetValue(text, out long at) && now - at < FailRepeatMs) return false;
            if (_failSaidAt.Count >= 256)
            {
                var old = new List<string>();
                foreach (var kv in _failSaidAt)
                    if (now - kv.Value >= FailRepeatMs) old.Add(kv.Key);
                foreach (string k in old) _failSaidAt.Remove(k);
                if (_failSaidAt.Count >= 256) _failSaidAt.Clear();
            }
            _failSaidAt[text] = now;
            return true;
        }
    }
}
