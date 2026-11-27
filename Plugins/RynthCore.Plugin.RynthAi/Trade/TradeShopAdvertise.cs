// Trade Shop advertising: which chat channel and its slash prefix.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here. Advertising is off
// by default, Trade is the default channel, and the shortest interval is 5 minutes
// (TradeShopSettings.MinAdvertiseSec).
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>Where the shop may advertise.</summary>
public enum TradeShopAdvertiseChannel
{
    Trade = 0,
    General = 1,
    Lfg = 2,
    Roleplay = 3,
    Society = 4,
    Local = 5,
}

public static class TradeShopAdvertise
{
    public static readonly TradeShopAdvertiseChannel[] All =
    {
        TradeShopAdvertiseChannel.Trade, TradeShopAdvertiseChannel.General, TradeShopAdvertiseChannel.Lfg,
        TradeShopAdvertiseChannel.Roleplay, TradeShopAdvertiseChannel.Society, TradeShopAdvertiseChannel.Local,
    };

    public static string DisplayName(TradeShopAdvertiseChannel ch) => ch switch
    {
        TradeShopAdvertiseChannel.Trade => "Trade (/ct)",
        TradeShopAdvertiseChannel.General => "General (/cg)",
        TradeShopAdvertiseChannel.Lfg => "LFG (/clfg)",
        TradeShopAdvertiseChannel.Roleplay => "Roleplay (/crp)",
        TradeShopAdvertiseChannel.Society => "Society (/cs)",
        TradeShopAdvertiseChannel.Local => "Local (say)",
        _ => ch.ToString(),
    };

    /// <summary>The chat command prefix (empty: say it locally).</summary>
    public static string SendPrefix(TradeShopAdvertiseChannel ch) => ch switch
    {
        TradeShopAdvertiseChannel.Trade => "/ct ",
        TradeShopAdvertiseChannel.General => "/cg ",
        TradeShopAdvertiseChannel.Lfg => "/clfg ",
        TradeShopAdvertiseChannel.Roleplay => "/crp ",
        TradeShopAdvertiseChannel.Society => "/cs ",
        _ => "",
    };

    public static bool TryParseChannel(string? text, out TradeShopAdvertiseChannel ch)
    {
        ch = TradeShopAdvertiseChannel.Trade;
        string t = (text ?? "").Trim().TrimStart('/').ToLowerInvariant();
        switch (t)
        {
            case "trade": case "ct": ch = TradeShopAdvertiseChannel.Trade; return true;
            case "general": case "cg": ch = TradeShopAdvertiseChannel.General; return true;
            case "lfg": case "clfg": ch = TradeShopAdvertiseChannel.Lfg; return true;
            case "roleplay": case "rp": case "crp": ch = TradeShopAdvertiseChannel.Roleplay; return true;
            case "society": case "cs": ch = TradeShopAdvertiseChannel.Society; return true;
            case "local": case "say": case "s": ch = TradeShopAdvertiseChannel.Local; return true;
        }
        return false;
    }
}
