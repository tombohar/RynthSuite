using System;
using System.Text;
using System.Threading;

namespace RynthCore.Plugin.RynthChat;

internal sealed class ChatBuffer
{
    private const int Capacity = 500;

    private readonly ChatLine[] _ring = new ChatLine[Capacity];
    private readonly object     _lock = new();
    private int  _head;   // next write index (mod Capacity)
    private int  _count;  // total items stored (≤ Capacity)
    private ulong _nextSeq = 1;

    internal void Add(string? text, int chatType)
    {
        if (string.IsNullOrEmpty(text)) return;
        string channel = ChatClassifier.IsRynthOutput(text)
            ? ChatClassifier.Rynth
            : ChatClassifier.ChannelFor(chatType);
        string? sender = ChatClassifier.SenderFor(text, chatType);
        string ts = DateTime.Now.ToString("HH:mm:ss");

        var line = new ChatLine
        {
            Seq       = Interlocked.Increment(ref _nextSeq) - 1,
            Timestamp = ts,
            Channel   = channel,
            Sender    = sender,
            Text      = text,
        };

        lock (_lock)
        {
            _ring[_head % Capacity] = line;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    // Returns JSON array of lines with Seq > sinceSeq, oldest-first.
    internal string BuildJson(ulong sinceSeq)
    {
        ChatLine[] snapshot;
        int count;
        int head;
        lock (_lock)
        {
            snapshot = (ChatLine[])_ring.Clone();
            count    = _count;
            head     = _head;
        }

        var sb = new StringBuilder(256);
        sb.Append('[');
        bool first = true;

        // Iterate oldest→newest. Oldest entry is at (head - count + Capacity) % Capacity.
        int start = (head - count + Capacity) % Capacity;
        for (int i = 0; i < count; i++)
        {
            var line = snapshot[(start + i) % Capacity];
            if (line == null || line.Seq <= sinceSeq) continue;

            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"seq\":");
            sb.Append(line.Seq);
            sb.Append(",\"ts\":\"");
            sb.Append(line.Timestamp);
            sb.Append("\",\"chan\":\"");
            AppendEscaped(sb, line.Channel);
            sb.Append("\",\"sender\":");
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
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
    }
}
