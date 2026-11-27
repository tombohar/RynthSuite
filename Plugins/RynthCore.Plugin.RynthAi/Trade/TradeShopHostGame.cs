// The Trade Shop's game side on RynthCore: the engine's trade calls (plugin API v72), WCIDs and
// stack sizes from the client, RynthAi's object cache for the packs and nearby players.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here.
using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Trade;

internal sealed class TradeShopHostGame : ITradeShopGame
{
    private const uint StackSizeKey = 12;   // PropertyInt StackSize (the engine reads the client's own copy)

    private readonly RynthCoreHost _host;
    private readonly WorldObjectCache _cache;
    private readonly Func<uint> _playerId;
    private readonly Func<string> _pauseReason;
    private readonly Func<string> _charFolder;

    public TradeShopHostGame(RynthCoreHost host, WorldObjectCache cache, Func<uint> playerId,
        Func<string> pauseReason, Func<string> charFolder)
    {
        _host = host;
        _cache = cache;
        _playerId = playerId;
        _pauseReason = pauseReason;
        _charFolder = charFolder;
    }

    public long NowMs => Environment.TickCount64;

    public bool HasTrade => _host.HasTrade;

    public bool TryGetTrade(out TradeShopTrade trade)
    {
        trade = default;
        if (!_host.TryGetTradeState(out TradeState st)) return false;
        trade = new TradeShopTrade(st.IsOpen, st.YouAccepted, st.PartnerAccepted, st.Generation, st.Sequence,
            st.PartnerId, st.CompletedCount);
        return true;
    }

    public uint[] TradeItems(bool partnerSide) => _host.GetTradeItems(partnerSide ? TradeSide.Partner : TradeSide.You);

    public bool TradeAdd(uint itemId) => _host.TradeAdd(itemId);
    public bool TradeAccept() => _host.TradeAccept();
    public bool TradeDecline() => _host.TradeDecline();
    public bool TradeReset() => _host.TradeReset();
    public bool TradeClose() => _host.TradeClose();

    public TradeShopItem? ReadItem(uint id)
    {
        if (id == 0) return null;
        if (!_host.HasGetObjectWcid || !_host.TryGetObjectWcid(id, out uint wcid) || wcid == 0)
            return new TradeShopItem(id, 0, NameOf(id), -1, Known: false);
        int stack = _host.TryGetObjectIntProperty(id, StackSizeKey, out int s) && s > 0 ? s : 0;
        bool equipped = false;
        WorldObject? wo = _cache[unchecked((int)id)];
        if (wo != null) equipped = wo.WieldedLocation > 0 || wo.Values(LongValueKey.EquippedSlots, 0) > 0;
        return new TradeShopItem(id, wcid, NameOf(id), stack, Known: true, equipped);
    }

    public IEnumerable<TradeShopItem> PackItems()
    {
        var list = new List<TradeShopItem>();
        IReadOnlyList<WorldObject> inv;
        try { inv = _cache.GetDirectInventory(forceRefresh: true); }
        catch { return list; }
        foreach (WorldObject wo in inv)
        {
            if (wo == null || wo.Id == 0) continue;
            TradeShopItem? it = ReadItem(unchecked((uint)wo.Id));
            if (it is { } item) list.Add(item);
        }
        return list;
    }

    public uint FindPlayer(string exactName)
    {
        if (string.IsNullOrWhiteSpace(exactName)) return 0;
        int me = unchecked((int)_playerId());
        foreach (WorldObject wo in _cache.GetLandscapeObjects())
        {
            if (wo == null || wo.Id == me || wo.ObjectClass != AcObjectClass.Player) continue;
            if (string.Equals(wo.Name?.Trim(), exactName.Trim(), StringComparison.OrdinalIgnoreCase))
                return unchecked((uint)wo.Id);
        }
        return 0;
    }

    public string NameOf(uint id)
        => id != 0 && _host.TryGetObjectName(id, out string n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : "";

    public void Tell(string to, string message)
    {
        if (!TradeShopTellParse.IsSafeName(to)) return;
        if (_host.HasInvokeChatParser) _host.InvokeChatParser($"/tell {to}, {message}");
        else _host.Log($"[RynthAi] TradeShop (no chat parser) -> {to}: {message}");
    }

    public void Say(string prefix, string message)
    {
        // The line must never be a command of its own: a leading / or @ is dropped.
        string body = (message ?? "").TrimStart('/', '@', ' ');
        if (body.Length == 0) return;
        if (_host.HasInvokeChatParser) _host.InvokeChatParser(prefix + body);
        else _host.Log($"[RynthAi] TradeShop (no chat parser) advert: {body}");
    }

    public void Chat(string line) => _host.WriteToChat(line, 1);

    public void Log(string line) => _host.Log(line);

    public void RecordSale(string line)
    {
        try
        {
            string dir = _charFolder();
            if (string.IsNullOrWhiteSpace(dir)) dir = RynthCore.Install.RynthInstallPaths.RynthAiDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "TradeShopSales.log"),
                RynthClock.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] TradeShop: couldn't write the sales log: {ex.Message}");
        }
    }

    public string PauseReason() => _pauseReason() ?? "";

    public string TimeOfDay() => RynthClock.Now.ToString("HH:mm");
}
