using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// RynthAi's recent-chat buffer: lines arrive through Push (RynthAiPlugin.OnChatWindowText)
/// and SnapshotRecent feeds the dashboard snapshot (the remote status feed). It used to be
/// a plugin-drawn chat window too; the chat window is now the RynthChat plugin's engine
/// face (ChatFace), so nothing here draws.
/// </summary>
internal sealed class RynthChatUi
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;

    private readonly object _lock = new();
    private readonly List<ChatLine> _lines = new(512);

    /// <summary>
    /// Routes a submitted line. Set by the plugin so /ra, /mt, /ub etc. can be
    /// handled locally by OnChatBarEnter before falling back to InvokeChatParser.
    /// Without this, plugin slash commands would be keyboard-simulated into the
    /// (invisible) retail chatbar — unreliable with ImGui focus in the picture.
    /// Nothing calls it since the input line went with the plugin-drawn window.
    /// </summary>
    public Action<string>? OnSubmit { get; set; }

    public RynthChatUi(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    private readonly record struct ChatLine(string Text, int ChatType, DateTime At);

    /// <summary>Append a chat line (called from RynthAiPlugin.OnChatWindowText).</summary>
    public void Push(string? text, int chatType)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_lock)
        {
            _lines.Add(new ChatLine(text, chatType, RynthClock.Now));

            int max = Math.Max(50, _settings.ChatMaxLines);
            int overflow = _lines.Count - max;
            if (overflow > 0)
                _lines.RemoveRange(0, overflow);
        }
    }

    /// <summary>Snapshot the most recent lines (text + chat-type) for the remote status feed. Thread-safe.</summary>
    public (string Text, int Type)[] SnapshotRecent(int max)
    {
        lock (_lock)
        {
            int n = Math.Min(Math.Max(0, max), _lines.Count);
            var outArr = new (string, int)[n];
            int start = _lines.Count - n;
            for (int i = 0; i < n; i++)
                outArr[i] = (_lines[start + i].Text, _lines[start + i].ChatType);
            return outArr;
        }
    }
}
