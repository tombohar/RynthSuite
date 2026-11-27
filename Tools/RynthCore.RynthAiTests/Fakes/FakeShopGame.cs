using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.Trade;

namespace RynthCore.RynthAiTests.Fakes;

/// <summary>
/// A trade window that behaves like ACE's (ACE.Server/WorldObjects/Player_Trade.cs): an add clears
/// both accepts, a reset clears both sides and both accepts, a decline clears only ours, and the
/// second accept completes the trade with what is in the window then.
/// </summary>
internal sealed class FakeShopGame : ITradeShopGame
{
    public long Now = 10_000_000;
    public bool Trade = true;
    public string Pause = "";

    public bool Open, YouAccepted, PartnerAccepted;
    public uint Generation = 1, Sequence = 1, PartnerId, Completed;
    public readonly List<uint> Mine = new(), Theirs = new();
    public readonly Dictionary<uint, TradeShopItem> Items = new();
    public readonly List<TradeShopItem> Pack = new();
    public readonly Dictionary<string, uint> Players = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<uint, string> Names = new();

    public readonly List<string> Actions = new();
    public readonly List<(string To, string Text)> Tells = new();
    public readonly List<string> Said = new(), Chats = new(), Logs = new(), Sales = new();
    public readonly List<uint> Received = new(), Given = new();

    /// <summary>Runs on the Nth read of the buyer's side (1-based), once: a trade event landing mid-check.</summary>
    public int HookOnPartnerRead;
    public Action? Hook;
    private int _partnerReads;
    public int PartnerReads => _partnerReads;

    public long NowMs => Now;
    public bool HasTrade => Trade;

    public bool TryGetTrade(out TradeShopTrade trade)
    {
        trade = new TradeShopTrade(Open, YouAccepted, PartnerAccepted, Generation, Sequence, Open ? PartnerId : 0, Completed);
        return true;
    }

    public uint[] TradeItems(bool partnerSide)
    {
        if (partnerSide)
        {
            _partnerReads++;
            uint[] now = Theirs.ToArray();
            if (Hook != null && _partnerReads == HookOnPartnerRead) { var h = Hook; Hook = null; h(); }
            return now;
        }
        return Mine.ToArray();
    }

    public bool TradeAdd(uint itemId)
    {
        Actions.Add("add:" + itemId.ToString("X8"));
        if (!Open) return false;
        Mine.Add(itemId);
        YouAccepted = PartnerAccepted = false;
        Sequence++;
        return true;
    }

    public bool TradeAccept()
    {
        Actions.Add("accept");
        if (!Open) return false;
        YouAccepted = true;
        Sequence++;
        if (PartnerAccepted) Complete();
        return true;
    }

    public bool TradeDecline()
    {
        Actions.Add("decline");
        YouAccepted = false;
        Sequence++;
        return true;
    }

    public bool TradeReset()
    {
        Actions.Add("reset");
        Mine.Clear();
        Theirs.Clear();
        YouAccepted = PartnerAccepted = false;
        Sequence++;
        return true;
    }

    public bool TradeClose()
    {
        Actions.Add("close");
        Open = false;
        Mine.Clear();
        Theirs.Clear();
        YouAccepted = PartnerAccepted = false;
        Sequence++;
        return true;
    }

    public TradeShopItem? ReadItem(uint id) => Items.TryGetValue(id, out TradeShopItem it) ? it : null;
    public IEnumerable<TradeShopItem> PackItems() => Pack;
    public uint FindPlayer(string exactName) => Players.TryGetValue(exactName, out uint id) ? id : 0;
    public string NameOf(uint id) => Names.TryGetValue(id, out string? n) ? n : "";
    public void Tell(string to, string message) => Tells.Add((to, message));
    public void Say(string prefix, string message) => Said.Add(prefix + message);
    public void Chat(string line) => Chats.Add(line);
    public void Log(string line) => Logs.Add(line);
    public void RecordSale(string line) => Sales.Add(line);
    public string PauseReason() => Pause;
    public string TimeOfDay() => "12:00";

    // ── The other player's side ─────────────────────────────────────────

    /// <summary>The buyer opens a trade with us.</summary>
    public void OpenTradeWith(uint partner)
    {
        Open = true;
        Generation++;
        Sequence++;
        PartnerId = partner;
        Mine.Clear();
        Theirs.Clear();
        YouAccepted = PartnerAccepted = false;
    }

    /// <summary>The buyer puts an item in (ACE: both accepts cleared).</summary>
    public void BuyerAdds(uint id, uint wcid, int stack, string name, bool known = true)
    {
        Items[id] = new TradeShopItem(id, known ? wcid : 0, name, stack, known);
        Theirs.Add(id);
        YouAccepted = PartnerAccepted = false;
        Sequence++;
    }

    /// <summary>The buyer accepts; if we had accepted, the trade completes.</summary>
    public void BuyerAccepts()
    {
        PartnerAccepted = true;
        Sequence++;
        if (YouAccepted) Complete();
    }

    private void Complete()
    {
        Received.AddRange(Theirs);
        Given.AddRange(Mine);
        Completed++;
        Mine.Clear();
        Theirs.Clear();
        YouAccepted = PartnerAccepted = false;
        Sequence++;
    }

    public int Count(string action) => Actions.Count(a => a == action);
}
