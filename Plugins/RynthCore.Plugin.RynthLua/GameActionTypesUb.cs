using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthLua;

// UtilityBelt's other game.Actions: Blank, SetAutorun, SendTellById, CastEquippedWandSpell,
// salvage (SalvageAdd, Salvage), vendor lists (VendorAddToBuyList ... VendorSellAll), trade
// (TradeAdd, TradeAccept, TradeDecline, TradeReset, TradeEnd), and the ones the engine can't
// do yet (UnsupportedAction: they fail with a clear ErrorDetails instead of being missing).

internal static class UbActionErrors
{
    public const string NoActions = "NoActions";
    public const string NoUst = "NoUst";
    public const string UnsalvageableItem = "UnsalvageableItem";
    public const string NoTradePartner = "NoTradePartner";
    public const string VendorNotOpen = "VendorNotOpen";
    public const string VendorDoesntHaveThisItem = "VendorDoesntHaveThisItem";
    public const string VendorDoesntHaveEnoughOfThisItem = "VendorDoesntHaveEnoughOfThisItem";
}

/// <summary>Blank(): does nothing and succeeds (a placeholder, e.g. in RunAllOrdered).</summary>
internal sealed class BlankAction : LuaAction
{
    public BlankAction(RynthLuaPlugin p, ScriptContext ctx) : base(p, ctx) { }
    public override string Kind => "Blank";
    protected override bool NeedsLogin => false;
    protected override bool Send(long now) { Succeed(); return false; }
}

/// <summary>SetAutorun(enabled): turns autorun on or off.</summary>
internal sealed class AutorunAction : LuaAction
{
    public readonly bool Enabled;
    public AutorunAction(RynthLuaPlugin p, ScriptContext ctx, bool enabled) : base(p, ctx) => Enabled = enabled;
    public override string Kind => "SetAutorun";
    protected override int DefaultPriority => ActionPriority.Navigation;
    protected override void DescribeFields(Table t) => t.Set("Enabled", DynValue.NewBoolean(Enabled));

    protected override bool Send(long now)
    {
        if (Host.HasSetAutoRun && Host.SetAutoRun(Enabled)) { Succeed(); return false; }
        Fail(ActionErrors.Exception, "the engine didn't accept the autorun change");
        return false;
    }
}

/// <summary>SendTellById(id, message): a tell to that object, by its name ("/t Name, message").</summary>
internal sealed class TellAction : LuaAction
{
    public readonly uint ObjectId;
    public readonly string Message;

    public TellAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId, string message) : base(p, ctx)
    {
        ObjectId = objectId;
        Message = message;
    }

    public override string Kind => "SendTellById";
    public override bool Immediate => true;
    protected override int DefaultMaxRetryCount => 0;

    protected override void DescribeFields(Table t)
    {
        t.Set("ObjectId", DynValue.NewNumber(ObjectId));
        t.Set("Message", DynValue.NewString(Message));
    }

    protected override string? Check(out string details)
    {
        details = string.Empty;
        if (!Host.TryGetObjectName(ObjectId, out string name) || name.Length == 0)
        {
            details = $"no one with id {Hex(ObjectId)}";
            return ActionErrors.InvalidTargetObject;
        }
        return null;
    }

    protected override bool Send(long now)
    {
        Host.TryGetObjectName(ObjectId, out string name);
        if (Host.HasInvokeChatParser && Host.InvokeChatParser($"/t {name}, {Message}")) { Succeed(DynValue.True); return false; }
        Fail(ActionErrors.Exception, "the chat parser didn't take the tell");
        return false;
    }
}

/// <summary>
/// CastEquippedWandSpell([target]): casts the spell of the wand, staff or orb you wield (AC's
/// "use the equipped caster on the target"). Done on the server's UseDone.
/// </summary>
internal sealed class WandCastAction : LuaAction
{
    public readonly uint TargetId;
    private uint _wand;

    public WandCastAction(RynthLuaPlugin p, ScriptContext ctx, uint targetId) : base(p, ctx) => TargetId = targetId;

    public override string Kind => "CastEquippedWandSpell";
    protected override int DefaultPriority => ActionPriority.CastSpell;
    protected override bool NeedsIdle => true;
    protected override bool WatchesServerCodes => true;

    protected override void DescribeFields(Table t) => t.Set("TargetId", DynValue.NewNumber(TargetId));

    protected override string? Check(out string details)
    {
        details = string.Empty;
        _wand = P.WieldedCaster();
        if (_wand == 0) { details = "you aren't wielding a wand, staff or orb"; return ActionErrors.InvalidSourceObject; }
        if (TargetId != 0 && !Exists(TargetId)) { details = $"no target {Hex(TargetId)}"; return ActionErrors.InvalidTargetObject; }
        return null;
    }

    protected override bool Send(long now)
    {
        if (_wand == 0) _wand = P.WieldedCaster();
        if (Host.HasUseEquippedItem && Host.UseEquippedItem(_wand, TargetId)) return true;
        Fail(ActionErrors.Exception, "the engine didn't accept the wand use");
        return false;
    }

    protected override bool IsMyRefusal(uint eventType, uint code, uint objectId)
        => eventType != 0x00A0 && (code == 0x001D || (WeenieErrors.TryKind(code, out var k) && k == WeenieErrors.Kind.Spell));

    protected override void Poll(long now)
    {
        if (ServerCodes && !Settling && NextUseDone(out uint err))
        {
            if (err != 0) FailWithCode(err, "the server's UseDone");
            else SucceedAfter(now, 100);
        }
        else if (!ServerCodes && now - SentAt >= 1000 && Host.GetCastBusyState() == 0) SucceedAfter(now, 100);
    }
}

// ── Salvage ─────────────────────────────────────────────────────────────────

/// <summary>
/// SalvageAdd(item): puts one of your items in the salvage panel, opening the panel with an
/// Ust from your inventory first when it isn't open (as RynthAi's salvager does).
/// </summary>
internal sealed class SalvageAddAction : ObjectAction
{
    private long _nextTry;
    private int _tries;

    public SalvageAddAction(RynthLuaPlugin p, ScriptContext ctx, uint objectId) : base(p, ctx, objectId, 0) { }

    public override string Kind => "SalvageAdd";
    protected override int DefaultPriority => ActionPriority.Inventory;
    protected override bool NeedsIdle => true;
    protected override bool WatchesServerCodes => false;

    protected override string? Check(out string details)
    {
        string? e = NeedObject(out details) ?? NeedMine(out details);
        if (e != null) return e;
        if (!Host.HasSalvagePanel) { details = "this engine has no salvage panel functions"; return ActionErrors.Exception; }
        return null;
    }

    protected override bool Send(long now)
    {
        if (TryAdd()) return false;
        uint ust = P.FindUst();
        if (ust == 0) { Fail(UbActionErrors.NoUst, "no Ust in your inventory"); return false; }
        if (!Host.UseFor(ust, "Script", $"{Ctx?.Name ?? "?"}: SalvageAdd opens the salvage panel (UST)", UseKind.Asked)) { Fail(ActionErrors.Exception, "the engine didn't open the salvage panel"); return false; }
        _nextTry = now + 400;   // the panel animates open first
        return true;
    }

    private bool TryAdd()
    {
        if (!Host.SalvagePanelAddItem(ObjectId)) return false;
        P.SalvageAdded.Add(ObjectId);
        Succeed();
        return true;
    }

    protected override void Poll(long now)
    {
        if (now < _nextTry) return;
        if (TryAdd()) return;
        _nextTry = now + 200;
        if (++_tries >= 15) Fail(ActionErrors.Exception, "the salvage panel didn't take the item (is it open?)");
    }
}

/// <summary>Salvage(): salvages what SalvageAdd put in the panel; done when those items are gone.</summary>
internal sealed class SalvageAction : LuaAction
{
    private uint[] _items = Array.Empty<uint>();

    public SalvageAction(RynthLuaPlugin p, ScriptContext ctx) : base(p, ctx) { }

    public override string Kind => "Salvage";
    protected override int DefaultPriority => ActionPriority.Inventory;
    protected override bool NeedsIdle => true;
    protected override int DefaultTimeoutMs => 10000;
    protected override int DefaultMaxRetryCount => 0;

    protected override string? Check(out string details)
    {
        details = string.Empty;
        P.SalvageAdded.RemoveAll(id => !Exists(id));
        if (P.SalvageAdded.Count == 0) { details = "nothing is in the salvage panel (SalvageAdd first)"; return UbActionErrors.NoActions; }
        return null;
    }

    protected override bool Send(long now)
    {
        _items = P.SalvageAdded.ToArray();
        if (!Host.SalvagePanelExecute()) { Fail(ActionErrors.Exception, "the salvage panel didn't salvage"); return false; }
        P.SalvageAdded.Clear();
        return true;
    }

    protected override void Poll(long now)
    {
        foreach (uint id in _items) if (Exists(id)) return;
        SucceedAfter(now, 150);
    }
}

// ── Vendor ──────────────────────────────────────────────────────────────────

/// <summary>
/// VendorAddToBuyList / VendorAddToSellList / VendorClearBuyList / VendorClearSellList: the
/// buy and sell lists (one per client, like UB's). They change the lists at once.
/// </summary>
internal sealed class VendorListAction : LuaAction
{
    public enum Op { AddBuy, AddSell, ClearBuy, ClearSell }

    private readonly Op _op;
    public readonly uint ObjectId;
    public readonly int Amount;

    public VendorListAction(RynthLuaPlugin p, ScriptContext ctx, Op op, uint objectId, int amount) : base(p, ctx)
    {
        _op = op;
        ObjectId = objectId;
        Amount = amount;
    }

    public override string Kind => _op switch
    {
        Op.AddBuy => "VendorAddToBuyList",
        Op.AddSell => "VendorAddToSellList",
        Op.ClearBuy => "VendorClearBuyList",
        _ => "VendorClearSellList",
    };
    public override bool Immediate => true;
    protected override int DefaultMaxRetryCount => 0;
    protected override int DefaultPriority => ActionPriority.Vendor;

    protected override void DescribeFields(Table t)
    {
        if (_op is Op.AddBuy or Op.AddSell) t.Set("ObjectId", DynValue.NewNumber(ObjectId));
        if (_op == Op.AddBuy) t.Set("Amount", DynValue.NewNumber(Amount));
    }

    protected override string? Check(out string details)
    {
        details = string.Empty;
        if (_op is Op.ClearBuy or Op.ClearSell) return null;
        if (!Host.TryGetVendorInfo(out _)) { details = "no vendor is open"; return UbActionErrors.VendorNotOpen; }
        if (_op == Op.AddBuy)
        {
            if (Amount < 1) { details = "buy at least 1"; return ActionErrors.InvalidInventoryLocation; }
            foreach (VendorItem item in Host.GetVendorItems())
            {
                if (item.ObjectId != ObjectId) continue;
                if (!item.Unlimited && item.Amount >= 0 && Amount > item.Amount)
                {
                    details = $"the vendor has {item.Amount}, not {Amount}";
                    return UbActionErrors.VendorDoesntHaveEnoughOfThisItem;
                }
                return null;
            }
            details = $"the vendor doesn't sell {Hex(ObjectId)}";
            return UbActionErrors.VendorDoesntHaveThisItem;
        }
        if (!IsMine(ObjectId)) { details = $"{Describe(ObjectId)} isn't in your inventory"; return ActionErrors.SourceItemNotInInventory; }
        return null;
    }

    protected override bool Send(long now)
    {
        switch (_op)
        {
            case Op.AddBuy: P.Cart.AddBuy(ObjectId, Amount); break;
            case Op.AddSell: P.Cart.AddSell(ObjectId); break;
            case Op.ClearBuy: P.Cart.ClearBuy(); break;
            default: P.Cart.ClearSell(); break;
        }
        Succeed();
        return false;
    }
}

/// <summary>
/// VendorBuyAll / VendorSellAll: sends the buy or sell list to the open vendor as one
/// transaction; done when the engine reports the server accepted it.
/// </summary>
internal sealed class VendorTradeAction : LuaAction
{
    private readonly bool _buy;
    private uint _request;

    public VendorTradeAction(RynthLuaPlugin p, ScriptContext ctx, bool buy) : base(p, ctx) => _buy = buy;

    public override string Kind => _buy ? "VendorBuyAll" : "VendorSellAll";
    protected override int DefaultPriority => ActionPriority.Vendor;
    protected override bool NeedsIdle => true;
    protected override int DefaultTimeoutMs => 12000;
    protected override int DefaultMaxRetryCount => 0;

    protected override string? Check(out string details)
    {
        details = string.Empty;
        if (!Host.HasVendorTrade || !Host.TryGetVendorInfo(out _)) { details = "no vendor is open"; return UbActionErrors.VendorNotOpen; }
        if ((_buy ? P.Cart.BuyList.Count : P.Cart.SellList.Count) == 0)
        {
            details = $"the {(_buy ? "buy" : "sell")} list is empty";
            return UbActionErrors.NoActions;
        }
        return null;
    }

    protected override bool Send(long now)
    {
        _request = _buy ? P.Cart.BuyAll(Host) : P.Cart.SellAll(Host);
        if (_request != 0) return true;
        string why = Host.TryGetVendorTradeStatus(out VendorTradeStatus st) && st.Message.Length > 0 ? st.Message : "the engine refused the transaction";
        Fail(ActionErrors.ServerError, why);
        return false;
    }

    protected override void Poll(long now)
    {
        if (!Host.TryGetVendorTradeStatus(out VendorTradeStatus st) || st.RequestId != _request || !st.IsFinished) return;
        if (st.Succeeded) Succeed();
        else Fail(ActionErrors.ServerError, st.Message.Length > 0 ? st.Message : $"the vendor said no ({st.State}, {st.Result})");
    }
}

// ── Trade ───────────────────────────────────────────────────────────────────

/// <summary>TradeAdd / TradeAccept / TradeDecline / TradeReset / TradeEnd on the open trade window.</summary>
internal sealed class TradeAction : LuaAction
{
    public enum Op { Add, Accept, Decline, Reset, End }

    private readonly Op _op;
    public readonly uint ObjectId;
    private uint _seq, _failures;

    public TradeAction(RynthLuaPlugin p, ScriptContext ctx, Op op, uint objectId = 0) : base(p, ctx)
    {
        _op = op;
        ObjectId = objectId;
    }

    public override string Kind => "Trade" + (_op == Op.Add ? "Add" : _op.ToString());
    protected override int DefaultPriority => ActionPriority.Trade;
    protected override int DefaultMaxRetryCount => 0;

    protected override void DescribeFields(Table t)
    {
        if (_op == Op.Add) t.Set("ObjectId", DynValue.NewNumber(ObjectId));
    }

    protected override string? Check(out string details)
    {
        details = string.Empty;
        if (!Host.HasTrade) { details = "this engine has no trade functions"; return ActionErrors.Exception; }
        if (!Host.TryGetTradeState(out TradeState st) || !st.IsOpen) { details = "no trade is open"; return UbActionErrors.NoTradePartner; }
        if (_op == Op.Add && !IsMine(ObjectId)) { details = $"{Describe(ObjectId)} isn't in your inventory"; return ActionErrors.SourceItemNotInInventory; }
        return null;
    }

    protected override bool Send(long now)
    {
        Host.TryGetTradeState(out TradeState st);
        _seq = st.Sequence;
        _failures = st.FailureCount;
        bool ok = _op switch
        {
            Op.Add => Host.TradeAdd(ObjectId),
            Op.Accept => Host.TradeAccept(),
            Op.Decline => Host.TradeDecline(),
            Op.Reset => Host.TradeReset(),
            _ => Host.TradeClose(),
        };
        if (ok) return true;
        Fail(ActionErrors.Exception, "the engine didn't accept the trade action");
        return false;
    }

    protected override void Poll(long now)
    {
        if (!Host.TryGetTradeState(out TradeState st)) return;
        switch (_op)
        {
            case Op.Add:
                if (st.FailureCount != _failures && st.LastFailureItemId == ObjectId)
                { FailWithCode(st.LastFailureReason, "the trade refused the item"); return; }
                if (Array.IndexOf(Host.GetTradeItems(TradeSide.You), ObjectId) >= 0) Succeed();
                break;
            case Op.Accept:
                if (st.YouAccepted || !st.IsOpen) Succeed();
                break;
            case Op.Decline:
                if (!st.YouAccepted && st.Sequence != _seq) Succeed();
                break;
            case Op.Reset:
                if (st.Sequence != _seq && st.YourItemCount == 0) Succeed();
                break;
            default:
                if (!st.IsOpen) Succeed();
                break;
        }
    }
}

// ── Not available yet ───────────────────────────────────────────────────────

/// <summary>
/// UB actions the engine can't do yet (login/logout, raising skills and attributes, the
/// fellowship, allegiance, inscribing): the call works and the action fails at once with
/// Error "Exception" and an ErrorDetails that says so, instead of the function being missing.
/// </summary>
internal sealed class UnsupportedAction : LuaAction
{
    private readonly string _kind;
    private readonly string _why;

    public UnsupportedAction(RynthLuaPlugin p, ScriptContext ctx, string kind, string why) : base(p, ctx)
    {
        _kind = kind;
        _why = why;
    }

    public override string Kind => _kind;
    protected override bool NeedsLogin => false;
    protected override int DefaultMaxRetryCount => 0;

    protected override bool Send(long now)
    {
        Fail(ActionErrors.Exception, $"{_kind} isn't available in RynthLua yet: it needs engine support ({_why})");
        return false;
    }
}

public sealed partial class RynthLuaPlugin
{
    /// <summary>The client's vendor buy/sell lists (UB keeps one per client too); cleared when the vendor closes.</summary>
    internal readonly VendorCart Cart = new();

    /// <summary>Items SalvageAdd put in the salvage panel, for Salvage.</summary>
    internal readonly List<uint> SalvageAdded = new();

    /// <summary>A wand, staff or orb the player wields (0: none).</summary>
    internal uint WieldedCaster()
    {
        foreach (uint id in WorldWieldedIds(Host.GetPlayerId()))
            if (Host.TryGetItemType(id, out uint t) && (t & ItemTypeCaster) != 0) return id;
        return 0;
    }

    /// <summary>An Ust in the player's inventory (0: none).</summary>
    internal uint FindUst()
    {
        foreach (uint id in WorldInventoryIds())
            if (WorldClassOf(id) == OcUst) return id;
        return 0;
    }
}
