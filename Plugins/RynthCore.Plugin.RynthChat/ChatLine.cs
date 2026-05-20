namespace RynthCore.Plugin.RynthChat;

internal sealed class ChatLine
{
    internal ulong  Seq       { get; init; }
    internal string Timestamp { get; init; } = "";
    internal string Channel   { get; init; } = "";
    internal string? Sender   { get; init; }
    internal string Text      { get; init; } = "";
}
