// What the Trade Shop needs from the game. RynthAi's implementation (TradeShopHostGame.cs) reads the
// engine's trade state and object cache; the tests use a fake. Keeping the shop behind this lets
// RynthCore.RynthAiTests run its whole sale flow offline.
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>A copy of the trade window's state (the engine's TradeState, API v72).</summary>
public readonly record struct TradeShopTrade(
    bool IsOpen, bool YouAccepted, bool PartnerAccepted,
    uint Generation, uint Sequence, uint PartnerId, uint CompletedCount);

public interface ITradeShopGame
{
    /// <summary>Milliseconds (a steady clock).</summary>
    long NowMs { get; }

    /// <summary>The engine has player trading (API v72).</summary>
    bool HasTrade { get; }

    bool TryGetTrade(out TradeShopTrade trade);

    /// <summary>Item ids on one side of the trade window (partner: the buyer's side).</summary>
    uint[] TradeItems(bool partnerSide);

    bool TradeAdd(uint itemId);
    bool TradeAccept();
    /// <summary>Withdraw our accept (ACE: clears only ours).</summary>
    bool TradeDecline();
    /// <summary>Clear the window (ACE: both sides' items and both accepts).</summary>
    bool TradeReset();
    bool TradeClose();

    /// <summary>One object as the client knows it (null: the client doesn't have it).</summary>
    TradeShopItem? ReadItem(uint id);

    /// <summary>The items in this character's packs (equipped ones flagged).</summary>
    IEnumerable<TradeShopItem> PackItems();

    /// <summary>A nearby player with exactly this name (0: none in view).</summary>
    uint FindPlayer(string exactName);

    /// <summary>An object's name ("" when unknown).</summary>
    string NameOf(uint id);

    /// <summary>Send a /tell.</summary>
    void Tell(string to, string message);

    /// <summary>Say a line on a chat channel (<paramref name="prefix"/>: "/ct ", or "" for local).</summary>
    void Say(string prefix, string message);

    /// <summary>A line in this player's chat only.</summary>
    void Chat(string line);

    void Log(string line);

    /// <summary>A sale, for the sales log file.</summary>
    void RecordSale(string line);

    /// <summary>Why the shop must not trade now (combat, the macro, a hunt, AutoTrade), or "".</summary>
    string PauseReason();

    /// <summary>"HH:mm" now, for the window's last sales.</summary>
    string TimeOfDay();
}
