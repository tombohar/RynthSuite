using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// <c>game.Character.Trade</c> (UtilityBelt's Trade): the player-to-player trade window from
/// the engine's trade state (API v72), read live, and its events found by comparing each
/// tick's state with the last one: OnStarted {InitiatorId, PartnerId}, OnObjectAdded
/// {ObjectId, Side}, OnAccepted {AcceptorId}, OnDeclined {DeclinerId}, OnReset
/// {ResetterObjectId}, OnFailedToAddObject {ObjectId, Reason}, OnCompleted {PartnerId,
/// YourItemIds, PartnerItemIds}, OnEnded {Reason}. The trade actions are game.Actions'
/// (TradeAdd, TradeAccept, ...). On an engine without trade support IsOpen stays false and
/// nothing fires.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private static readonly string[] TradeEventNames =
    {
        "OnStarted", "OnObjectAdded", "OnAccepted", "OnDeclined", "OnReset", "OnFailedToAddObject",
        "OnCompleted", "OnEnded", "OnFailed",
    };

    private bool _trKnown;
    private TradeState _tr;
    private uint[] _trSelf = Array.Empty<uint>(), _trPartner = Array.Empty<uint>();

    private DynValue CreateTradeTable(Script s, ScriptContext ctx)
    {
        var t = new Table(s);
        foreach (string e in TradeEventNames)
            t[e] = ctx.Event("Character.Trade." + e).CreateTable(s);
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            bool have = Host.TryGetTradeState(out TradeState st);
            switch (key)
            {
                case "IsOpen": return DynValue.NewBoolean(have && st.IsOpen);
                case "PartnerId": return DynValue.NewNumber(have && st.IsOpen ? st.PartnerId : 0);
                case "YouAccepted": return DynValue.NewBoolean(have && st.IsOpen && st.YouAccepted);
                case "PartnerAccepted": return DynValue.NewBoolean(have && st.IsOpen && st.PartnerAccepted);
                case "YourItemIds": return IdList(s, have && st.IsOpen ? new List<uint>(Host.GetTradeItems(TradeSide.You)) : new List<uint>());
                case "PartnerItemIds": return IdList(s, have && st.IsOpen ? new List<uint>(Host.GetTradeItems(TradeSide.Partner)) : new List<uint>());
                default: return DynValue.Nil;
            }
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
            DynValue.NewString(Host.TryGetTradeState(out TradeState st) && st.IsOpen ? $"Trade with 0x{st.PartnerId:X8}" : "Trade (closed)"));
        t.MetaTable = mt;
        return DynValue.NewTable(t);
    }

    /// <summary>Every tick: what changed in the trade window since the last tick.</summary>
    private void TradeTick()
    {
        if (!Host.HasTrade || !Host.TryGetTradeState(out TradeState st)) return;
        if (!_trKnown)
        {
            _tr = st;
            _trKnown = true;
            if (st.IsOpen) { _trSelf = Host.GetTradeItems(TradeSide.You); _trPartner = Host.GetTradeItems(TradeSide.Partner); }
            return;
        }
        TradeState old = _tr;
        if (st.Sequence == old.Sequence && st.Generation == old.Generation && st.IsOpen == old.IsOpen
            && st.CompletedCount == old.CompletedCount && st.FailureCount == old.FailureCount)
            return;
        _tr = st;

        uint[] oldSelf = _trSelf, oldPartner = _trPartner;
        uint[] self = st.IsOpen ? Host.GetTradeItems(TradeSide.You) : Array.Empty<uint>();
        uint[] partner = st.IsOpen ? Host.GetTradeItems(TradeSide.Partner) : Array.Empty<uint>();

        if (st.Generation != old.Generation && st.IsOpen)
        {
            oldSelf = oldPartner = Array.Empty<uint>();
            TradeFire("OnStarted", ("InitiatorId", st.InitiatorId), ("PartnerId", st.PartnerId));
        }
        foreach (uint id in self)
            if (Array.IndexOf(oldSelf, id) < 0) TradeFire("OnObjectAdded", ("ObjectId", id), ("Side", 1u));
        foreach (uint id in partner)
            if (Array.IndexOf(oldPartner, id) < 0) TradeFire("OnObjectAdded", ("ObjectId", id), ("Side", 2u));
        if (st.LastEventType == 0x0205 && st.Sequence != old.Sequence)
            TradeFire("OnReset", ("ResetterObjectId", st.LastResetBy));
        if (st.PartnerAcceptCount > old.PartnerAcceptCount)
            TradeFire("OnAccepted", ("AcceptorId", st.PartnerId != 0 ? st.PartnerId : old.PartnerId));
        if (st.YouAccepted && !old.YouAccepted)
            TradeFire("OnAccepted", ("AcceptorId", Host.GetPlayerId()));
        if (st.LastEventType == 0x0203 && st.Sequence != old.Sequence)
            TradeFire("OnDeclined", ("DeclinerId", st.LastDeclinedBy));
        if (st.FailureCount > old.FailureCount)
            TradeFire("OnFailedToAddObject", ("ObjectId", st.LastFailureItemId), ("Reason", st.LastFailureReason));

        if (st.CompletedCount > old.CompletedCount)
        {
            uint[] yours = self.Length > 0 ? self : _trSelf, theirs = partner.Length > 0 ? partner : _trPartner;
            uint who = old.PartnerId;
            FireAll("Character.Trade.OnCompleted", s =>
            {
                var t = new Table(s);
                t["PartnerId"] = (double)who;
                t["YourItemIds"] = IdList(s, new List<uint>(yours));
                t["PartnerItemIds"] = IdList(s, new List<uint>(theirs));
                return DynValue.NewTable(t);
            });
        }
        if (old.IsOpen && !st.IsOpen)
            TradeFire("OnEnded", ("Reason", st.LastCloseReason));

        if (st.IsOpen || self.Length > 0 || partner.Length > 0) { _trSelf = self; _trPartner = partner; }
        if (!st.IsOpen) { _trSelf = Array.Empty<uint>(); _trPartner = Array.Empty<uint>(); }
    }

    private void TradeFire(string evt, params (string Key, uint Value)[] fields)
    {
        FireAll("Character.Trade." + evt, s =>
        {
            var t = new Table(s);
            foreach (var (k, v) in fields) t[k] = (double)v;
            return DynValue.NewTable(t);
        });
    }
}
