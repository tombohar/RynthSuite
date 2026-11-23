using System;
using System.Text;

namespace RynthCore.Plugin.RynthChat;

/// <summary>
/// The newest chat lines, exported to the engine as JSON
/// (RynthChatGetScrollbackJson). Add runs on the game thread (chat dispatch),
/// BuildJson on the engine's pump thread; the lock covers both.
///
/// How many: DefaultCapacity until the engine says otherwise
/// (RynthChatSetCapacity, from the chat setting "Chat history"; 0.3.0). It was
/// a fixed 500 before. Changing it keeps the newest lines and never touches the
/// numbering: the engine (and its Discord relay) tell new lines from old by seq.
///
/// Player chat (0.4.0) is kept longer, in a second ring (see DefaultPlayerCapacity): a read
/// that starts before the main ring's oldest line gets those player lines first, in seq order.
///
/// JSON per line: {"seq":N,"ts":"HH:mm:ss","chan":"Chat","type":3,"sender":"Bob"|null,"text":"..."}.
/// "type" (AC's ChatMessageType) was added in 0.2.0; engines before it ignore it.
/// Every non-ASCII character is written as a \uXXXX escape, so the export is
/// pure ASCII and survives the ANSI marshalling on both sides (before 0.2.0 an
/// accented name or the "→" of an outgoing tell arrived as "?").
/// </summary>
internal sealed class ChatBuffer
{
    /// <summary>The engine's default for "Chat history" (ChatHistory.DefaultLines there).</summary>
    internal const int DefaultCapacity = 2000;
    internal const int MinCapacity = 100;
    internal const int MaxCapacity = 10000;

    // Player chat (0.4.0, ChatClassifier.IsPlayerChat) is also kept in a ring of its own, so it
    // outlasts the main ring: the engine's "Player chat history" (RynthChatSetPlayerCapacity). A
    // busy hunt fills the main ring with combat lines in minutes; after an engine reload, or with
    // the chat window closed for a while, the engine reads the scrollback from here, and the tells
    // and fellowship lines are still in it. 0 turns the second ring off.
    internal const int DefaultPlayerCapacity = 5000;
    internal const int MaxPlayerCapacity = 20000;

    private ChatLine?[] _ring;
    private readonly object _lock = new();
    private int  _head;   // next write index (mod _ring.Length)
    private int  _count;  // lines stored (<= _ring.Length)
    private ulong _nextSeq = 1;
    private ChatLine?[] _players;
    private int _pHead, _pCount;

    internal ChatBuffer(int capacity = DefaultCapacity, int playerCapacity = DefaultPlayerCapacity)
    {
        _ring = new ChatLine?[Math.Clamp(capacity, MinCapacity, MaxCapacity)];
        _players = new ChatLine?[Math.Clamp(playerCapacity, 0, MaxPlayerCapacity)];
    }

    internal int PlayerCapacity
    {
        get { lock (_lock) return _players.Length; }
    }

    /// <summary>
    /// Keeps up to <paramref name="capacity"/> player lines (0..MaxPlayerCapacity) in their own
    /// ring from now on; when it shrinks, the oldest go. Returns the capacity applied, or -1 for 0
    /// (off), so the engine can tell "applied" from "not up yet". Any thread.
    /// </summary>
    internal int SetPlayerCapacity(int capacity)
    {
        capacity = Math.Clamp(capacity, 0, MaxPlayerCapacity);
        lock (_lock)
        {
            if (capacity != _players.Length)
            {
                (_players, _pHead, _pCount) = Resize(_players, _pHead, _pCount, capacity);
            }
            return capacity == 0 ? -1 : capacity;
        }
    }

    private static (ChatLine?[] Ring, int Head, int Count) Resize(ChatLine?[] old, int head, int count, int capacity)
    {
        int keep = Math.Min(count, capacity);
        var ring = new ChatLine?[capacity];
        int oldCap = old.Length;
        if (oldCap > 0)
        {
            int start = (head - keep + oldCap) % oldCap;   // the oldest line kept
            for (int i = 0; i < keep; i++) ring[i] = old[(start + i) % oldCap];
        }
        return (ring, capacity == 0 ? 0 : keep % capacity, keep);
    }

    internal int Capacity
    {
        get { lock (_lock) return _ring.Length; }
    }

    internal int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>
    /// Keeps up to <paramref name="capacity"/> lines from now on (clamped to
    /// MinCapacity..MaxCapacity); when it shrinks, the oldest go. Returns the capacity
    /// applied. Any thread.
    /// </summary>
    internal int SetCapacity(int capacity)
    {
        capacity = Math.Clamp(capacity, MinCapacity, MaxCapacity);
        lock (_lock)
        {
            if (capacity == _ring.Length) return capacity;
            (_ring, _head, _count) = Resize(_ring, _head, _count, capacity);
            return capacity;
        }
    }

    internal void Add(string? text, int chatType)
    {
        if (string.IsNullOrEmpty(text)) return;
        string channel = ChatClassifier.IsItemInfo(text) ? ChatClassifier.Items
            : ChatClassifier.IsRynthOutput(text) ? ChatClassifier.Rynth
            : ChatClassifier.ChannelFor(text, chatType);
        string? sender = ChatClassifier.SenderFor(text, chatType);
        string ts = DateTime.Now.ToString("HH:mm:ss");
        bool player = ChatClassifier.IsPlayerChat(channel, chatType, text);

        lock (_lock)
        {
            // Numbered under the lock, so the ring is always in seq order (the
            // engine skips anything at or below the last seq it has seen).
            var line = new ChatLine
            {
                Seq       = _nextSeq++,
                Timestamp = ts,
                Channel   = channel,
                ChatType  = chatType,
                Sender    = sender,
                Text      = text,
            };
            _ring[_head] = line;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            if (player && _players.Length > 0)
            {
                _players[_pHead] = line;   // the same object: no extra copy of the text
                _pHead = (_pHead + 1) % _players.Length;
                if (_pCount < _players.Length) _pCount++;
            }
        }
    }

    /// <summary>
    /// The lines with Seq > sinceSeq as a JSON array, oldest first. Only those are
    /// copied out under the lock: the ring holds consecutive seqs, so they are the
    /// newest (newest seq - sinceSeq) lines. Until 0.3.0 the whole ring was cloned
    /// and walked on every poll (10 a second), which grows with the history length.
    /// </summary>
    internal string BuildJson(ulong sinceSeq)
    {
        ChatLine?[] lines;
        lock (_lock)
        {
            ulong newest = _nextSeq - 1;
            int n = sinceSeq >= newest ? 0 : (int)Math.Min((ulong)_count, newest - sinceSeq);
            if (n == 0) return "[]";
            // Player lines older than the main ring (0.4.0), first: only when the reader is behind
            // the main ring's oldest line (its first read after a bind or reload, or a long gap).
            // An ordinary poll (a few new lines) skips this, so it still copies only those.
            ulong oldestMain = newest - (ulong)_count + 1;
            int older = 0;
            int pCap = _players.Length;
            int pStart = pCap == 0 ? 0 : (_pHead - _pCount + pCap) % pCap;
            if (_pCount > 0 && sinceSeq + 1 < oldestMain)
                for (int i = 0; i < _pCount; i++)
                {
                    ChatLine? p = _players[(pStart + i) % pCap];
                    if (p == null || p.Seq >= oldestMain) break;
                    if (p.Seq > sinceSeq) older++;
                }
            lines = new ChatLine?[older + n];
            int w = 0;
            if (older > 0)
                for (int i = 0; i < _pCount && w < older; i++)
                {
                    ChatLine? p = _players[(pStart + i) % pCap];
                    if (p != null && p.Seq > sinceSeq && p.Seq < oldestMain) lines[w++] = p;
                }
            int cap = _ring.Length;
            int start = (_head - n + cap) % cap;
            for (int i = 0; i < n; i++) lines[w + i] = _ring[(start + i) % cap];
        }

        var sb = new StringBuilder(64 + lines.Length * 192);
        sb.Append('[');
        bool first = true;
        foreach (ChatLine? line in lines)
        {
            if (line == null || line.Seq <= sinceSeq) continue;

            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"seq\":");
            sb.Append(line.Seq);
            sb.Append(",\"ts\":\"");
            AppendEscaped(sb, line.Timestamp);
            sb.Append("\",\"chan\":\"");
            AppendEscaped(sb, line.Channel);
            sb.Append("\",\"type\":");
            sb.Append(line.ChatType);
            sb.Append(",\"sender\":");
            if (line.Sender != null)
            {
                sb.Append('"');
                AppendEscaped(sb, line.Sender);
                sb.Append('"');
            }
            else
            {
                sb.Append("null");
            }
            sb.Append(",\"text\":\"");
            AppendEscaped(sb, line.Text);
            sb.Append("\"}");
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // A surrogate pair is escaped as two \uXXXX; a lone half becomes U+FFFD.
            // System.Text.Json refuses a lone surrogate, and before 0.2.1 one such line
            // failed the engine's whole batch (and every poll after it).
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    AppendUnicodeEscape(sb, c);
                    AppendUnicodeEscape(sb, s[++i]);
                }
                else
                    sb.Append("\\uFFFD");
                continue;
            }
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    // Control characters and everything outside ASCII: the string is
                    // marshalled as ANSI, which would turn those into '?'.
                    if (c < 0x20 || c > 0x7E)
                        AppendUnicodeEscape(sb, c);
                    else
                        sb.Append(c);
                    break;
            }
        }
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c)
    {
        sb.Append("\\u");
        sb.Append(((int)c).ToString("X4"));
    }
}
