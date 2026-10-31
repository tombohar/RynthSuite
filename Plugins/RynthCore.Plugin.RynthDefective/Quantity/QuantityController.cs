// ============================================================================
//  RynthDefective - Quantity/QuantityController.cs
//  The quantity box (2026-10-30): one number, and what it does to what is
//  selected. Asked for by a player: "to select a portion of a stack, or buy
//  multiple of an item from a vendor, I need a place to type in the number".
//
//    a stack you carry          Split N into the main pack, a side pack or the
//    (or in the chest you have  container you have open; Give N to the player
//    open)                      or NPC you selected last
//    an item in a vendor's list Buy N, with the price, your pyreals, the room
//                               in your packs and your burden checked first
//                               (a server's bank that pays vendors counts
//                               too, or leaves the money to the server when
//                               its balance can't be read: VendorQuote)
//
//  The Selection window shows the box under what is selected (QuantityRow);
//  /rdef qty, split, give and buy do the same from chat, hotkeys and metas.
//  Nothing here needs the engine's Inventory panel or RynthInventory.
//
//  One action at a time. Each one answers once: at once when it can't go
//  (why, in the player's words), or when the game answers (the stack got
//  smaller, the item left your packs, the vendor's trade finished) or doesn't
//  (a refusal from the server, or no answer in time).
//
//  Threads: everything on the plugin pump, except Value and Into, which the
//  Selection window sets from the render thread (single ints).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective.Quantity;

internal enum QtyMode { None, Single, Stack, Vendor }

/// <summary>Where the selected item is, for a split or a give.</summary>
internal enum QtyWhere { Unknown, Carried, Wielded, Open, Elsewhere }

/// <summary>What is selected, as the box sees it.</summary>
internal sealed record QtySubject(QtyMode Mode, uint Id, string Name, int Stack, QtyWhere Where)
{
    public static readonly QtySubject Nothing = new(QtyMode.None, 0, string.Empty, 0, QtyWhere.Unknown);

    /// <summary>A vendor's item: its price and what you have.</summary>
    public VendorQuote? Quote { get; init; }
    public uint VendorId { get; init; }
    /// <summary>The most you can buy now (vendor items).</summary>
    public int MaxBuy { get; init; }

    /// <summary>What the box's "max" is of: the whole stack, or the most you can buy; 0 = nothing.</summary>
    public int BoxMax => Mode switch
    {
        QtyMode.Stack => Stack,
        QtyMode.Vendor => MaxBuy,
        _ => 0,
    };
}

/// <summary>The box as the Selection window draws it (built on the tick).</summary>
internal sealed record QuantityView(QtySubject Subject, QtyPack[] Targets, int TargetIndex, uint GiveTo, string GiveName,
    bool Busy, bool CanSplit, bool CanGive)
{
    public QtyPack? Target => TargetIndex >= 0 && TargetIndex < Targets.Length ? Targets[TargetIndex] : null;
}

/// <summary>The last thing the box did, for the Selection window's line.</summary>
internal sealed record QtyStatus(string Text, bool Ok, long AtMs);

internal sealed class QuantityController
{
    /// <summary>How long a split, a give (the server may walk you to the NPC first) and a buy wait for the game.</summary>
    public const long SplitAnswerMs = 6000, GiveAnswerMs = 20_000, BuyAnswerMs = 15_000;
    /// <summary>A refusal that doesn't name the item is ours if nothing changed this long after it.</summary>
    public const long RefusalGraceMs = 1500;
    /// <summary>The last player or NPC selected stays the give target this long.</summary>
    public const long GiveTargetMs = 10 * 60 * 1000;
    public const long StatusShownMs = 8000;

    /// <summary>InventoryServerSaveFailed: the server refused a move, split or give of this item.</summary>
    private const uint InventorySaveFailed = 0x00A0;

    private int _value = 1;
    private uint _into;
    private volatile QtyStatus? _status;
    private uint _lastSelection, _giveId;
    private string _giveName = string.Empty;
    private long _giveAtMs;
    private Pending? _pending;

    /// <summary>Every answer (ok, text): the plugin writes it in chat.</summary>
    public Action<bool, string> Report = (_, _) => { };

    /// <summary>The box's number (1 .. MaxAmount). Any thread.</summary>
    public int Value
    {
        get => Volatile.Read(ref _value);
        set => Volatile.Write(ref _value, Math.Clamp(value, 1, QuantityRules.MaxAmount));
    }

    /// <summary>The pack a split goes into (its id); 0 or a pack that's gone = the main pack. Any thread.</summary>
    public uint Into
    {
        get => Volatile.Read(ref _into);
        set => Volatile.Write(ref _into, value);
    }

    public bool Busy => _pending != null;

    /// <summary>The last answer while it is fresh. Any thread.</summary>
    public QtyStatus? StatusAt(long nowMs)
    {
        QtyStatus? s = _status;
        return s != null && nowMs - s.AtMs < StatusShownMs ? s : null;
    }

    public void Reset()
    {
        _pending = null;
        _status = null;
        _giveId = _lastSelection = 0;
        _giveName = string.Empty;
        Into = 0;
    }

    private void Say(IQtyWorld w, bool ok, string text)
    {
        _status = new QtyStatus(text, ok, w.NowMs);
        Report(ok, text);
    }

    // ── What is selected ────────────────────────────────────────────────────

    public QtySubject Analyze(IQtyWorld w)
    {
        uint sel = w.SelectedId;
        if (sel == 0) return QtySubject.Nothing;
        string name = w.TryGetName(sel, out string n) ? n : $"0x{sel:X8}";
        if (w.CanTrade && w.TryGetVendor(out VendorInfo info))
        {
            foreach (VendorItem it in w.VendorItems())
            {
                if (it.ObjectId != sel) continue;
                VendorQuote q = Quote(w, info, it);
                return new QtySubject(QtyMode.Vendor, sel, it.Name.Length > 0 ? it.Name : name, Math.Max(1, it.StackSize), QtyWhere.Elsewhere)
                {
                    Quote = q,
                    VendorId = info.VendorId,
                    MaxBuy = q.MaxBuy(),
                };
            }
        }
        int stack = w.StackOf(sel);
        return new QtySubject(stack >= 2 ? QtyMode.Stack : QtyMode.Single, sel, name, Math.Max(1, stack), WhereIs(w, sel));
    }

    private static QtyWhere WhereIs(IQtyWorld w, uint id)
    {
        uint player = w.PlayerId;
        if (player == 0 || !w.TryGetOwner(id, out uint c, out uint wielder)) return QtyWhere.Unknown;
        if (wielder != 0 && wielder == player) return QtyWhere.Wielded;
        if (c == player) return QtyWhere.Carried;
        if (c != 0 && w.TryGetOwner(c, out uint outer, out uint outerWielder) && outer == player && outerWielder == 0) return QtyWhere.Carried;
        if (c != 0 && c == w.OpenContainerId) return QtyWhere.Open;
        return QtyWhere.Elsewhere;
    }

    private static bool Owned(IQtyWorld w, uint id)
    {
        QtyWhere where = WhereIs(w, id);
        return where is QtyWhere.Carried or QtyWhere.Wielded;
    }

    private static VendorQuote Quote(IQtyWorld w, VendorInfo info, VendorItem item)
    {
        int free = 0;
        foreach (QtyPack p in w.Packs())
        {
            if (p.Kind == QtyPackKind.Open) continue;
            if (p.Free < 0) { free = -1; break; }
            free += p.Free;
        }
        return VendorQuote.From(info, item, free, w.FreeContainerSlots(), w.FreeBurden(), w.Bank());
    }

    /// <summary>
    /// The Selection window's box: for a vendor's item, or a stack you carry, wield (it says to take it
    /// off) or have in the open container. Not for a stack on the ground or in someone else's hands. Plugin tick.
    /// </summary>
    public QuantityView? View(IQtyWorld w)
    {
        QtySubject s = Analyze(w);
        if (s.Mode is not (QtyMode.Stack or QtyMode.Vendor)) return null;
        if (s.Mode == QtyMode.Stack && s.Where is not (QtyWhere.Carried or QtyWhere.Wielded or QtyWhere.Open)) return null;
        QtyPack[] targets = s.Mode == QtyMode.Stack ? new List<QtyPack>(w.Packs()).ToArray() : Array.Empty<QtyPack>();
        int index = 0;
        uint into = Into;
        for (int i = 0; i < targets.Length; i++) if (targets[i].Id == into) { index = i; break; }
        TryGiveTarget(w, s.Id, out uint to, out string toName);
        return new QuantityView(s, targets, targets.Length == 0 ? -1 : index, to, toName, Busy, w.CanSplit, w.CanGive);
    }

    // ── The give target: the last player or NPC selected ─────────────────────

    /// <summary>Notes a newly selected player or NPC (not you, not a monster) as the give target. Plugin tick, a few times a second.</summary>
    public void NoteSelection(IQtyWorld w)
    {
        uint sel = w.SelectedId;
        if (sel == _lastSelection) return;
        _lastSelection = sel;
        if (sel == 0 || sel == w.PlayerId || !w.CanReceive(sel)) return;
        _giveId = sel;
        _giveName = w.TryGetName(sel, out string n) ? n : $"0x{sel:X8}";
        _giveAtMs = w.NowMs;
    }

    /// <summary>Who a give goes to: the last player or NPC selected (within GiveTargetMs and still there), else AC's previous selection.</summary>
    public bool TryGiveTarget(IQtyWorld w, uint item, out uint to, out string name)
    {
        uint player = w.PlayerId;
        if (_giveId != 0 && _giveId != item && w.NowMs - _giveAtMs < GiveTargetMs && w.Exists(_giveId))
        {
            to = _giveId;
            name = _giveName;
            return true;
        }
        uint prev = w.PreviousSelectedId;
        if (prev != 0 && prev != item && prev != player && w.CanReceive(prev))
        {
            to = prev;
            name = w.TryGetName(prev, out string n) ? n : $"0x{prev:X8}";
            return true;
        }
        to = 0;
        name = string.Empty;
        return false;
    }

    // ── /rdef qty ───────────────────────────────────────────────────────────

    /// <summary>Sets the box (null: says what it is). "max" and "half" are of what is selected.</summary>
    public void SetBox(IQtyWorld w, QtyAmount? amount)
    {
        QtySubject s = Analyze(w);
        if (amount is { } a)
        {
            if (a.Word != QtyWord.Number && s.BoxMax < 1)
            {
                Say(w, false, s.Mode == QtyMode.Vendor ? s.Quote?.Refusal(1) ?? QtyReplies.MaxNeedsSelection : QtyReplies.MaxNeedsSelection);
                return;
            }
            Value = QuantityRules.ForBox(a, s.BoxMax);
        }
        Say(w, true, $"Quantity {QuantityRules.N(Value)}{About(s)}.");
    }

    private string About(QtySubject s) => s.Mode switch
    {
        QtyMode.Stack => $" (the selected {s.Name}: a stack of {QuantityRules.N(s.Stack)}; a split takes up to {QuantityRules.N(s.Stack - 1)})",
        QtyMode.Vendor when s.Quote != null => $" (the vendor's {s.Name}: {s.Quote.PriceLine(Value)}; you can buy {QuantityRules.N(s.MaxBuy)}"
            + (s.Quote.MaxNote.Length > 0 ? "; " + s.Quote.MaxNote : string.Empty) + ")",
        _ => string.Empty,
    };

    // ── Split ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits the box's number (or <paramref name="amount"/>) off the selected stack into
    /// <paramref name="into"/> (a pack's name; empty = the pack chosen in the window, else the main pack).
    /// </summary>
    public void Split(IQtyWorld w, QtyAmount? amount, string into)
    {
        if (_pending != null) { Say(w, false, QtyReplies.Busy); return; }
        if (!w.CanSplit) { Say(w, false, QtyReplies.NoSplitApi); return; }
        QtySubject s = Analyze(w);
        string? why = s.Mode switch
        {
            QtyMode.None => QtyReplies.NothingSelected,
            QtyMode.Vendor => QtyReplies.VendorsItem(s.Name),
            QtyMode.Single => QtyReplies.NotAStack(s.Name),
            _ => s.Where switch
            {
                QtyWhere.Wielded => QtyReplies.Wielded(s.Name),
                QtyWhere.Carried or QtyWhere.Open => null,
                _ => QtyReplies.NotReachable(s.Name),
            },
        };
        if (why != null) { Say(w, false, why); return; }

        IReadOnlyList<QtyPack> packs = w.Packs();
        if (!TryPickTarget(packs, into, out QtyPack target, out why)) { Say(w, false, why); return; }
        if (target.Free == 0) { Say(w, false, QtyReplies.PackFull(target.Where)); return; }

        int n = QuantityRules.ForSplit(amount ?? QtyAmount.Of(Value), s.Stack);
        var p = new Pending
        {
            Kind = PendingKind.Split, Id = s.Id, Name = s.Name, N = n, Where = target.Where,
            StackBefore = s.Stack, RefusalSeq = w.RefusalSeq, AtMs = w.NowMs,
        };
        if (!w.Split(s.Id, target.Id, n)) { Say(w, false, QtyReplies.NotSent("split")); return; }
        _pending = p;
    }

    /// <summary>The split's pack: the one named, else the one chosen in the window (if it's still there), else the main pack.</summary>
    internal bool TryPickTarget(IReadOnlyList<QtyPack> packs, string typed, out QtyPack target, out string why)
    {
        target = default;
        why = string.Empty;
        if (packs.Count == 0) { why = "Your packs can't be read yet: try again in a moment."; return false; }
        string t = (typed ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            uint into = Into;
            target = packs[0];
            foreach (QtyPack p in packs) if (p.Id == into) { target = p; break; }
            return true;
        }
        string low = t.ToLowerInvariant();
        if (low is "main" or "main pack" or "mainpack" or "pack" or "me" or "inventory")
        {
            target = packs[0];
            return true;
        }
        if (low is "open" or "chest" or "container" or "the chest" or "the open container")
        {
            foreach (QtyPack p in packs) if (p.Kind == QtyPackKind.Open) { target = p; return true; }
            why = "No chest or other container is open: open it first, or split into one of your packs.";
            return false;
        }
        // The pack's own name: the whole of it, then its start, then any part of it.
        foreach (QtyPack p in packs) if (p.Name.Equals(t, StringComparison.OrdinalIgnoreCase) || p.Label.Equals(t, StringComparison.OrdinalIgnoreCase)) { target = p; return true; }
        foreach (QtyPack p in packs) if (p.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)) { target = p; return true; }
        foreach (QtyPack p in packs) if (p.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) { target = p; return true; }
        var names = new List<string>(packs.Count);
        foreach (QtyPack p in packs) names.Add(p.Label);
        why = QtyReplies.NoSuchPack(t, string.Join(", ", names));
        return false;
    }

    // ── Give ────────────────────────────────────────────────────────────────

    /// <summary>Gives the box's number (or <paramref name="amount"/>) of the selected stack to the last player or NPC selected.</summary>
    public void Give(IQtyWorld w, QtyAmount? amount)
    {
        if (_pending != null) { Say(w, false, QtyReplies.Busy); return; }
        if (!w.CanGive) { Say(w, false, QtyReplies.NoGiveApi); return; }
        QtySubject s = Analyze(w);
        string? why = s.Mode switch
        {
            QtyMode.None => QtyReplies.NothingSelected,
            QtyMode.Vendor => QtyReplies.VendorsItem(s.Name),
            _ => s.Where switch
            {
                QtyWhere.Wielded => QtyReplies.Wielded(s.Name),
                QtyWhere.Carried => null,
                _ => QtyReplies.NotCarried(s.Name),
            },
        };
        if (why != null) { Say(w, false, why); return; }
        if (!TryGiveTarget(w, s.Id, out uint to, out string toName)) { Say(w, false, QtyReplies.NoGiveTarget); return; }

        int n = QuantityRules.ForGive(amount ?? QtyAmount.Of(Value), s.Stack);
        var p = new Pending
        {
            Kind = PendingKind.Give, Id = s.Id, Name = s.Name, N = n, Where = toName,
            StackBefore = s.Stack, RefusalSeq = w.RefusalSeq, AtMs = w.NowMs,
        };
        // 0 = the whole of it (the give the client sends for a drag of the whole stack).
        if (!w.Give(s.Id, to, n >= s.Stack ? 0 : n)) { Say(w, false, QtyReplies.NotSent("give")); return; }
        _pending = p;
    }

    // ── Buy ─────────────────────────────────────────────────────────────────

    /// <summary>Buys the box's number (or <paramref name="amount"/>) of the selected vendor item, or the one named.</summary>
    public void Buy(IQtyWorld w, QtyAmount? amount, string itemName)
    {
        if (_pending != null) { Say(w, false, QtyReplies.Busy); return; }
        if (!w.CanTrade) { Say(w, false, QtyReplies.NoTradeApi); return; }
        if (!w.TryGetVendor(out VendorInfo info)) { Say(w, false, QtyReplies.NoVendor); return; }
        if (!info.TradingAvailable) { Say(w, false, QtyReplies.VendorUnreadable); return; }
        if (info.TradeInFlight) { Say(w, false, QtyReplies.VendorBusy); return; }

        IReadOnlyList<VendorItem> items = w.VendorItems();
        VendorItem? item;
        string name = (itemName ?? string.Empty).Trim();
        if (name.Length > 0)
        {
            item = FindVendorItem(items, name, out string? why);
            if (item == null) { Say(w, false, why!); return; }
        }
        else
        {
            item = null;
            uint sel = w.SelectedId;
            foreach (VendorItem it in items) if (it.ObjectId == sel && sel != 0) { item = it; break; }
            if (item == null) { Say(w, false, QtyReplies.NoVendorItem); return; }
        }

        VendorQuote q = Quote(w, info, item);
        int n = amount is { } a ? QuantityRules.ForBuy(a, q.MaxBuy()) : Value;
        if (n < 1) { Say(w, false, q.Refusal(1) ?? $"You can't buy any {item.Name} now."); return; }
        string? refusal = q.Refusal(n);
        if (refusal != null) { Say(w, false, refusal); return; }

        // The bank is to pay what the pack is short of (read or not): the answers say so.
        bool bankMayPay = q.BankPays && q.Funds >= 0 && q.Cost(n) > q.Funds;
        uint request = w.Buy(info.VendorId, item.ObjectId, n);
        if (request == 0)
        {
            Say(w, false, QtyReplies.BuyRefusal(w.TryGetTradeStatus(out VendorTradeStatus st) ? st.Message : null, bankMayPay));
            return;
        }
        _pending = new Pending
        {
            Kind = PendingKind.Buy, Id = item.ObjectId, Name = item.Name, N = n, RequestId = request,
            Cost = q.Cost(n), Currency = q.Currency, AtMs = w.NowMs, BankMayPay = bankMayPay,
        };
    }

    /// <summary>A vendor's item by name: the whole name, else the one whose name starts so, else the one that holds it. Several = ask for more.</summary>
    internal static VendorItem? FindVendorItem(IReadOnlyList<VendorItem> items, string name, out string? why)
    {
        why = null;
        foreach (VendorItem it in items) if (it.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return it;
        foreach (bool starts in new[] { true, false })
        {
            VendorItem? found = null;
            var names = new List<string>();
            foreach (VendorItem it in items)
            {
                bool match = starts ? it.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) : it.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                if (!names.Exists(x => x.Equals(it.Name, StringComparison.OrdinalIgnoreCase))) names.Add(it.Name);
                found ??= it;
            }
            if (names.Count == 1) return found;
            if (names.Count > 1)
            {
                why = $"'{name}' could be {string.Join(", ", names.GetRange(0, Math.Min(5, names.Count)))}{(names.Count > 5 ? ", ..." : "")}: type more of the name.";
                return null;
            }
        }
        why = $"The vendor doesn't sell '{name}'.";
        return null;
    }

    // ── Waiting for the game ────────────────────────────────────────────────

    private enum PendingKind { Split, Give, Buy }

    private sealed class Pending
    {
        public PendingKind Kind;
        public uint Id;
        public string Name = string.Empty;
        public int N;
        /// <summary>Split: the pack ("the main pack"); give: who to.</summary>
        public string Where = string.Empty;
        public int StackBefore;
        public int RefusalSeq;
        public long AtMs;
        public long RefusedAtMs;
        public uint Error;
        public bool RefusalNamesItem;
        public uint RequestId;
        public long Cost;
        public string Currency = string.Empty;
        /// <summary>Buy: the server's bank was to pay what the pack is short of.</summary>
        public bool BankMayPay;
    }

    /// <summary>Plugin tick: the give target, and the answer to the action in flight.</summary>
    public void Tick(IQtyWorld w)
    {
        NoteSelection(w);
        Pending? p = _pending;
        if (p == null) return;
        long now = w.NowMs;
        switch (p.Kind)
        {
            case PendingKind.Split:
            {
                int stack = w.StackOf(p.Id);
                if (stack > 0 && stack < p.StackBefore) Finish(w, true, QtyReplies.Splitting(p.N, p.Name, p.Where));
                else if (RefusedNow(w, p, now)) Finish(w, false, QtyReplies.Refused("split", p.Error));
                else if (now - p.AtMs >= SplitAnswerMs) Finish(w, false, QtyReplies.NoAnswer("split"));
                break;
            }
            case PendingKind.Give:
            {
                int stack = w.StackOf(p.Id);
                bool left = !w.Exists(p.Id) || !Owned(w, p.Id);
                if (left || (stack > 0 && stack < p.StackBefore)) Finish(w, true, QtyReplies.Gave(p.N, p.Name, p.Where));
                else if (RefusedNow(w, p, now)) Finish(w, false, QtyReplies.Refused("give", p.Error));
                else if (now - p.AtMs >= GiveAnswerMs) Finish(w, false, QtyReplies.NoAnswer("give"));
                break;
            }
            case PendingKind.Buy:
                WatchBuy(w, p, now);
                break;
        }
    }

    private void WatchBuy(IQtyWorld w, Pending p, long now)
    {
        if (w.TryGetTradeStatus(out VendorTradeStatus s))
        {
            if (s.RequestId == p.RequestId)
            {
                if (s.State == VendorTradeState.Refused) { Finish(w, false, QtyReplies.BuyRefusal(s.Message, p.BankMayPay)); return; }
                if (s.State == VendorTradeState.Done)
                {
                    if (s.Result == VendorTradeResult.Accepted)
                        Finish(w, true, QtyReplies.Bought(p.N, p.Name, s.Estimate > 0 ? s.Estimate : p.Cost, p.Currency));
                    else if (s.Result == VendorTradeResult.TimedOut)
                        Finish(w, false, $"No answer from the vendor about {QuantityRules.N(p.N)} {p.Name}: check your pack and {p.Currency} before trying again.");
                    else
                        Finish(w, false, QtyReplies.NotSold(p.N, p.Name, p.BankMayPay, p.Cost, p.Currency));
                    return;
                }
            }
            else if (s.RequestId > p.RequestId)
            {
                // Another plugin's trade came after ours: the engine keeps only the newest one's status.
                Finish(w, true, $"Asked the vendor for {QuantityRules.N(p.N)} {p.Name}; another trade followed it, so check your pack.");
                return;
            }
        }
        if (now - p.AtMs >= BuyAnswerMs)
            Finish(w, false, $"No answer about {QuantityRules.N(p.N)} {p.Name}: check your pack and {p.Currency} before trying again.");
    }

    /// <summary>
    /// The server refused it: a refusal naming the item (InventoryServerSaveFailed) at once, any other
    /// new refusal once nothing changed for RefusalGraceMs after it (a fight's own errors pass by).
    /// </summary>
    private static bool RefusedNow(IQtyWorld w, Pending p, long now)
    {
        int seq = w.RefusalSeq;
        if (p.RefusedAtMs == 0 && seq != 0 && seq != p.RefusalSeq && w.LastRefusal(out uint error, out uint eventType, out uint obj))
        {
            p.RefusalSeq = seq;
            bool names = eventType == InventorySaveFailed && obj != 0;
            if (names && obj != p.Id) return false;   // another item's
            p.RefusedAtMs = now;
            p.Error = error;
            p.RefusalNamesItem = names;
        }
        return p.RefusedAtMs != 0 && (p.RefusalNamesItem || now - p.RefusedAtMs >= RefusalGraceMs);
    }

    private void Finish(IQtyWorld w, bool ok, string text)
    {
        _pending = null;
        Say(w, ok, text);
    }
}
