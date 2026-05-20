using System;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthChat;

public sealed class RynthChatPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthChat");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");

    private readonly ChatBuffer _buffer = new();

    public override int Initialize()
    {
        Host.Log("[RynthChat] Plugin initialized.");
        return 0;
    }

    public override void OnLoginComplete()
    {
        Host.Log("[RynthChat] Login complete.");
    }

    public override void OnLogout()
    {
        Host.Log("[RynthChat] Logout.");
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        _buffer.Add(text, chatType);
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        // Phase 5: intercept + own outgoing chat.
    }

    internal string BuildScrollbackJson(ulong sinceSeq) => _buffer.BuildJson(sinceSeq);

    internal void SendLine(string text)
    {
        if (!Host.HasInvokeChatParser || string.IsNullOrWhiteSpace(text)) return;
        Host.InvokeChatParser(text);
    }
}
