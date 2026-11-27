// Trade Shop: a player-run shop. Buyers tell !list / !price / !buy; after !buy the buyer opens a trade
// (the shop never opens one: on ACE the opener walks to the other player), the shop puts the item in
// the window (Mode 1), checks the buyer's payment by WCID, and the sale is accepted.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here, with these changes:
//   * payment counts by WCID only (TradeShopPayment / TradeShopCurrencyTable), never by name or Value;
//   * the trade is checked again at the moment of accepting: both sides re-read, our side exactly the
//     listed item, the buyer's side covering the price, the trade state unchanged across the reads;
//     items the client can't read yet are never counted as paid;
//   * after we accept, any change re-checks: a trade that no longer passes is un-accepted
//     (TradeDecline), one that still passes is accepted again (or prompted again);
//   * no accept outside a sale: a trade nobody asked to !buy in is greeted and never accepted;
//   * "Confirm each sale" (on by default): the shop stops before the accept and asks the player
//     (Accept sale / Decline in the Trade Shop window, or /ra tradeshop accept | decline);
//   * Mode 2 (a text listing another account holds) is never accepted by the shop: always the prompt;
//   * tells are rate-limited (TradeShopTellLimiter) and queues capped; advertising is off by default;
//   * the shop is off at every login and pauses in combat, while the macro or a hunt runs;
//   * every sale goes to the log, the sales file and the player's chat.
//
// How ACE handles a change after an accept (ACE.Server/WorldObjects/Player_Trade.cs, read for this):
//   * HandleActionAddToTrade clears BOTH players' TradeAccepted before adding the item, but sends only
//     AddToTrade (no ClearTradeAcceptance); RynthCore's trade state clears both accepts on AddToTrade too;
//   * ResetTrade (GameActionResetTrade) runs on both players: both sides' items and both accepts cleared;
//   * DeclineTrade clears only the decliner's accept;
//   * there is no "remove one item": a side only grows, or is reset;
//   * the transfer happens inside the second accept (FinalizeTrade), with the items in the window then;
//   * items in a trade window can't be split, merged, moved or used (IsBeingTradedOrContainsItemBeingTraded).
// So a buyer can't take payment back after our accept without clearing it, and adding only adds to
// what we get. The shop still re-checks every change (a stale read, a reset and re-add) before it
// lets an accept stand.
//
// Threading: OnTell and Request* are called from the chat / render threads and only queue; Tick runs
// everything else on the plugin tick. Pure (System only): RynthCore.RynthAiTests links it.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>The sale waiting for the player's Accept sale (the window and chat show it).</summary>
public sealed record TradeShopPrompt(string Buyer, string Item, long PricePyreals, string Price, string Paid, TradeShopMode Mode);

/// <summary>What the accept-time check found.</summary>
public enum TradeShopCheck { Ok, Pending, Short, Invalid }

public readonly record struct TradeShopCheckResult(TradeShopCheck Status, string Reason, TradeShopTally? Tally);

public sealed class TradeShopManager
{
    public const long OpenTradeWaitMs = 30_000;
    public const long ShowItemWaitMs = 10_000;
    public const long PaymentWaitMs = 120_000;
    public const long ConfirmWaitMs = 180_000;
    public const long AcceptedWaitMs = 60_000;
    public const long TellGapMs = 1_200;
    public const long ManualAdvertiseGapMs = 60_000;
    public const int MaxReaccepts = 3;
    public const long AcceptEchoWaitMs = 1_500;
    public const int RecentSalesKept = 20;

    private readonly ITradeShopGame _game;
    private readonly Func<TradeShopSettings> _cfg;
    private readonly TradeShopTellLimiter _limiter = new();
    private readonly ConcurrentQueue<(string Sender, string Body, long At)> _in = new();
    private int _inCount;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly LinkedList<(string To, string Text, bool Sale)> _out = new();
    private long _nextTellAt;
    private long _nextAdvertiseAt;
    private long _lastManualAdAt = long.MinValue / 2;

    // Trade window tracking.
    private bool _baselined;
    private bool _wasOpen;
    private uint _generation;
    private uint _completed;
    private uint _greetedGeneration;

    private enum Phase { Idle, OpenTrade, ShowItem, WaitPayment, AwaitConfirm, Accepted }
    private Phase _phase = Phase.Idle;
    private TradeShopListing? _listing;
    private TradeShopMode _mode;
    private uint _stockId;
    private string _buyer = "";
    private uint _buyerId;
    private uint _sessionGeneration;
    private long _deadline;
    private uint _checkedSequence;
    private int _reaccepts;
    private long _acceptSentAt = long.MinValue / 2;
    private long _declineSentAt = long.MinValue / 2;
    private TradeShopCheckResult _lastOk;
    private bool _paymentSeenTold;

    private volatile TradeShopPrompt? _prompt;
    private volatile string _status = "Off";
    private readonly object _salesLock = new();
    private readonly List<string> _sales = new();

    public TradeShopManager(ITradeShopGame game, Func<TradeShopSettings> settings)
    {
        _game = game;
        _cfg = settings;
    }

    private TradeShopSettings Cfg => _cfg() ?? new TradeShopSettings();

    /// <summary>One line for the window and /ra tradeshop status (any thread).</summary>
    public string Status => _status;

    /// <summary>The sale waiting for Accept sale, or null (any thread).</summary>
    public TradeShopPrompt? Prompt => _prompt;

    /// <summary>A sale is under way: inventory chores wait.</summary>
    public bool HoldsBot => _phase != Phase.Idle;

    /// <summary>The last sales, newest last (any thread).</summary>
    public List<string> RecentSales()
    {
        lock (_salesLock) return new List<string>(_sales);
    }

    // ── From other threads ──────────────────────────────────────────────

    /// <summary>Chat thread: a chat line; tells to us that look like shop commands are queued.</summary>
    public void OnTell(string? text, bool isIncomingTell)
    {
        if (!isIncomingTell || !Cfg.Enabled) return;
        if (!TradeShopTellParse.TryParseTell(text, out string sender, out string body)) return;
        if (TradeShopTellParse.SplitCommand(body).verb.Length == 0) return;
        if (Interlocked.Increment(ref _inCount) > TradeShopTellLimiter.MaxInQueue)
        {
            Interlocked.Decrement(ref _inCount);
            return;   // queue full: dropped
        }
        _in.Enqueue((sender, body, _game.NowMs));   // the limits go by when it arrived
    }

    /// <summary>Run <paramref name="action"/> on the next tick (commands, window buttons).</summary>
    public void Enqueue(Action action)
    {
        if (action != null) _commands.Enqueue(action);
    }

    /// <summary>The Accept sale button / command: re-check, then accept.</summary>
    public void RequestAccept() => Enqueue(AcceptFromPlayer);

    /// <summary>The Decline button / command: the sale is called off and the trade closed.</summary>
    public void RequestDecline() => Enqueue(DeclineFromPlayer);

    /// <summary>Advertise now (the window's button, /ra tradeshop advertise now).</summary>
    public void RequestAdvertise() => Enqueue(() => Advertise(_game.NowMs, manual: true));

    /// <summary>Turn the shop on or off; turning it on says what it does.</summary>
    public void SetEnabled(bool on) => Enqueue(() => ApplyEnabled(on));

    /// <summary>Confirm each sale on or off; off says what that means once.</summary>
    public void SetConfirmEachSale(bool on) => Enqueue(() => ApplyConfirm(on));

    /// <summary>Logout: drop everything quietly.</summary>
    public void Reset()
    {
        ClearSession();
        _baselined = false;
        _wasOpen = false;
        while (_in.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _inCount, 0);
        while (_commands.TryDequeue(out _)) { }
        _out.Clear();
        _limiter.Clear();
        _nextAdvertiseAt = 0;
        _status = "Off";
    }

    // ── Tick ────────────────────────────────────────────────────────────

    public void Tick()
    {
        while (_commands.TryDequeue(out Action? cmd))
        {
            try { cmd(); }
            catch (Exception ex) { _game.Log($"[RynthAi] TradeShop command failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        long now = _game.NowMs;
        TradeShopSettings cfg = Cfg;
        if (!cfg.Enabled)
        {
            if (_phase != Phase.Idle) Abort("the shop was turned off", tellBuyer: true);
            DropInbound();
            DropPriceLines();
            DrainOut(now);   // the "sale called off" tell still goes
            _nextAdvertiseAt = 0;
            _status = "Off";
            return;
        }

        string pause = _game.PauseReason() ?? "";
        if (pause.Length > 0)
        {
            if (_phase != Phase.Idle) Abort("paused: " + pause, tellBuyer: true);
            DropInbound();
            DropPriceLines();
            _nextAdvertiseAt = 0;
            DrainOut(now);   // the "sale called off" tell still goes
            _status = "Paused: " + pause;
            return;
        }

        DrainOut(now);
        TickAdvertise(now, cfg);
        HandleTells(now, cfg);

        if (!_game.HasTrade)
        {
            _status = "On, but this RynthCore has no player trading (plugin API 72+): price tells only";
            return;
        }

        PollTrade(now);
        TickPhase(now, cfg);
        if (_phase == Phase.Idle) _status = $"On, {(cfg.Mode == TradeShopMode.Mode1Stock ? "Mode 1" : "Mode 2")}, waiting for buyers";
    }

    // ── Tells ───────────────────────────────────────────────────────────

    private void DropPriceLines()
    {
        var node = _out.First;
        while (node != null)
        {
            var next = node.Next;
            if (!node.Value.Sale) _out.Remove(node);
            node = next;
        }
    }

    private void DropInbound()
    {
        while (_in.TryDequeue(out _)) Interlocked.Decrement(ref _inCount);
    }

    // Every queued tell goes through the limits (by when it arrived); the ones let through are answered.
    private void HandleTells(long now, TradeShopSettings cfg)
    {
        for (int i = 0; i < TradeShopTellLimiter.MaxInQueue && _in.TryDequeue(out var tell); i++)
        {
            Interlocked.Decrement(ref _inCount);
            HandleTell(tell.Sender, tell.Body, tell.At, now, cfg);
        }
    }

    private void HandleTell(string sender, string body, long at, long now, TradeShopSettings cfg)
    {
        var tell = (Sender: sender, Body: body);
        switch (_limiter.OnIncoming(sender, at))
        {
            case TradeShopTellLimiter.Verdict.TooFast:
            case TradeShopTellLimiter.Verdict.Ignored:
                return;
            case TradeShopTellLimiter.Verdict.NowIgnored:
                _game.Log($"[RynthAi] TradeShop: {tell.Sender} sent too many tells; ignored for {TradeShopTellLimiter.SpamIgnoreMs / 1000} s.");
                Queue(tell.Sender, $"Too many messages. The shop will ignore you for {TradeShopTellLimiter.SpamIgnoreMs / 60000} minutes.", sale: true);
                return;
        }

        var (verb, arg) = TradeShopTellParse.SplitCommand(tell.Body);
        List<TradeShopListing> forSale = cfg.ForSale();
        switch (verb)
        {
            case "help":
                Queue(tell.Sender, TradeShopMessages.HelpLine());
                break;
            case "rates":
                Queue(tell.Sender, TradeShopMessages.Clip(TradeShopMessages.RatesLine(cfg.Rates)));
                break;
            case "list":
                foreach (string chunk in TradeShopMessages.PriceSheet(forSale, cfg.Rates, includeRates: true))
                    Queue(tell.Sender, chunk);
                break;
            case "price":
            {
                TradeShopListing? l = TradeShopMessages.ResolveListing(forSale, arg);
                Queue(tell.Sender, l == null
                    ? "No such item. Tell !list for the price sheet."
                    : TradeShopMessages.Clip($"{l.Label()}: {cfg.Rates.FormatAlternatives(l.PricePyreals)}"));
                break;
            }
            case "buy":
                BeginBuy(tell.Sender, arg, forSale, cfg, now);
                break;
        }
    }

    private void Queue(string to, string text, bool sale = false)
    {
        string line = TradeShopMessages.Clip(text);
        if (!TradeShopTellParse.IsSafeName(to) || line.Length == 0) return;
        if (_out.Count >= TradeShopTellLimiter.MaxOutQueue)
        {
            if (!sale) { _game.Log($"[RynthAi] TradeShop: tell queue full, dropped a line to {to}."); return; }
            // A sale line goes in ahead of a price-sheet line.
            var node = _out.Last;
            while (node != null && node.Value.Sale) node = node.Previous;
            if (node == null) return;
            _out.Remove(node);
        }
        if (sale)
        {
            var after = _out.First;
            while (after != null && after.Value.Sale) after = after.Next;
            if (after == null) _out.AddLast((to, line, true));
            else _out.AddBefore(after, (to, line, true));
        }
        else _out.AddLast((to, line, false));
    }

    private void DrainOut(long now)
    {
        if (_out.Count == 0 || now < _nextTellAt) return;
        while (_out.First != null)
        {
            var next = _out.First.Value;
            if (!_limiter.TryTakeOut(next.To, now, overBuyerCap: next.Sale))
            {
                if (_limiter.SentLastMinute(now) >= TradeShopTellLimiter.MaxOutPerMinute) return;   // wait for room
                _out.RemoveFirst();   // that buyer had their share this minute: dropped
                _game.Log($"[RynthAi] TradeShop: dropped a line to {next.To} (their tell limit this minute).");
                continue;
            }
            _out.RemoveFirst();
            _game.Tell(next.To, next.Text);
            _nextTellAt = now + TellGapMs;
            return;
        }
    }

    // ── Advertising ─────────────────────────────────────────────────────

    private void TickAdvertise(long now, TradeShopSettings cfg)
    {
        if (!cfg.AdvertiseEnabled) { _nextAdvertiseAt = 0; return; }
        long every = TradeShopSettings.ClampAdvertiseSec(cfg.AdvertiseIntervalSec) * 1000L;
        if (_nextAdvertiseAt == 0) { _nextAdvertiseAt = now + every; return; }   // the first one after a full interval
        if (now < _nextAdvertiseAt) return;
        _nextAdvertiseAt = now + every;
        Advertise(now, manual: false);
    }

    private void Advertise(long now, bool manual)
    {
        TradeShopSettings cfg = Cfg;
        if (manual)
        {
            if (!cfg.Enabled) { _game.Chat("[RynthAi] Trade Shop: turn the shop on first (it advertises only while on)."); return; }
            if (now - _lastManualAdAt < ManualAdvertiseGapMs)
            {
                _game.Chat($"[RynthAi] Trade Shop: advertised less than {ManualAdvertiseGapMs / 1000} s ago; wait a moment.");
                return;
            }
            _lastManualAdAt = now;
        }
        List<TradeShopListing> forSale = cfg.ForSale();
        if (forSale.Count == 0)
        {
            if (manual) _game.Chat("[RynthAi] Trade Shop: nothing listed for sale, so no advert was sent.");
            return;
        }
        string line = AdvertiseLine(cfg, forSale);
        _game.Say(TradeShopAdvertise.SendPrefix(cfg.AdvertiseChannel), line);
        _game.Log($"[RynthAi] TradeShop advertised on {TradeShopAdvertise.DisplayName(cfg.AdvertiseChannel)}: {line}");
        if (manual) _game.Chat($"[RynthAi] Trade Shop: advertised on {TradeShopAdvertise.DisplayName(cfg.AdvertiseChannel)}.");
    }

    /// <summary>The advert: prefix, then the items and prices that fit, then how to ask.</summary>
    public static string AdvertiseLine(TradeShopSettings cfg, IReadOnlyList<TradeShopListing> forSale)
    {
        string prefix = (cfg.AdvertisePrefix ?? "").Trim().TrimStart('/', '@').Trim();
        string head = prefix.Length == 0 ? "[Trade Shop] Selling:" : prefix;
        const string tail = " Tell me !list";
        string line = TradeShopMessages.Clip(head);
        int shown = 0;
        foreach (TradeShopListing l in forSale)
        {
            string item = $" {l.Label()} {TradeShopRates.N(l.PricePyreals)} py;";
            if (line.Length + item.Length + tail.Length + 12 > TradeShopMessages.MaxLineChars) break;
            line += item;
            shown++;
        }
        if (shown < forSale.Count) line += $" +{forSale.Count - shown} more.";
        return line + tail;
    }

    // ── Turning it on / off ─────────────────────────────────────────────

    private void ApplyEnabled(bool on)
    {
        TradeShopSettings cfg = Cfg;
        if (cfg.Enabled == on)
        {
            _game.Chat($"[RynthAi] Trade Shop is already {(on ? "on" : "off")}.");
            return;
        }
        cfg.Enabled = on;
        if (!on)
        {
            _game.Chat("[RynthAi] Trade Shop OFF: it no longer answers tells, trades or advertises.");
            return;
        }
        int n = cfg.ForSale().Count;
        _game.Chat($"[RynthAi] Trade Shop ON ({(cfg.Mode == TradeShopMode.Mode1Stock ? "Mode 1: items from this character's packs" : "Mode 2: text listings")}, {n} item(s) for sale).");
        _game.Chat("[RynthAi]   It answers tells (!list, !price, !buy). On !buy it opens a trade with the buyer"
                   + (cfg.Mode == TradeShopMode.Mode1Stock ? ", puts the item in the window" : "")
                   + " and checks their payment by item type (pyreals, trade notes, single-use Legendary Keys only).");
        _game.Chat(cfg.Mode == TradeShopMode.Mode2Text
            ? "[RynthAi]   Mode 2: you accept every sale yourself (Accept sale in the Trade Shop window), then hand the item over."
            : cfg.ConfirmEachSale
                ? "[RynthAi]   Confirm each sale is ON: it stops before accepting and asks you (Accept sale / Decline in the Trade Shop window)."
                : "[RynthAi]   Confirm each sale is OFF: it accepts a sale itself once the payment checks out.");
        _game.Chat("[RynthAi]   It never accepts a trade nobody asked to !buy in, and pauses in combat, while the macro or a hunt runs. "
                   + (cfg.AdvertiseEnabled ? $"It advertises on {TradeShopAdvertise.DisplayName(cfg.AdvertiseChannel)} every {TradeShopSettings.ClampAdvertiseSec(cfg.AdvertiseIntervalSec) / 60} min." : "Advertising is off."));
    }

    private void ApplyConfirm(bool on)
    {
        TradeShopSettings cfg = Cfg;
        bool was = cfg.ConfirmEachSale;
        cfg.ConfirmEachSale = on;
        if (on) _game.Chat("[RynthAi] Trade Shop: Confirm each sale ON (it asks you before every accept).");
        else if (was) _game.Chat("[RynthAi] Trade Shop: Confirm each sale OFF. Warning: Mode 1 sales are now accepted without asking you, once the payment checks out.");
        else _game.Chat("[RynthAi] Trade Shop: Confirm each sale is already OFF.");
    }

    // ── !buy ────────────────────────────────────────────────────────────

    private void BeginBuy(string buyer, string arg, List<TradeShopListing> forSale, TradeShopSettings cfg, long now)
    {
        if (!_game.HasTrade) { Queue(buyer, "The shop can't trade right now. Try later.", sale: true); return; }
        if (forSale.Count == 0) { Queue(buyer, "Nothing is for sale right now.", sale: true); return; }
        TradeShopListing? listing = TradeShopMessages.ResolveListing(forSale, arg);
        if (listing == null) { Queue(buyer, "No such item. Tell !list, then !buy #<slot>.", sale: true); return; }

        if (_phase != Phase.Idle && !NamesEqual(_buyer, buyer))
        {
            Queue(buyer, "The shop is busy with another buyer. Try again in a minute.", sale: true);
            return;
        }
        if (_phase == Phase.Accepted)
        {
            Queue(buyer, "Your trade is already accepted on my side: accept it, or cancel the trade first.", sale: true);
            return;
        }

        uint stock = 0;
        if (cfg.Mode == TradeShopMode.Mode1Stock)
        {
            stock = ResolveStock(listing);
            if (stock == 0) { Queue(buyer, $"Out of stock: {listing.Label()}.", sale: true); return; }
        }

        // A trade open with someone else: busy.
        _game.TryGetTrade(out TradeShopTrade st);
        if (st.IsOpen && !IsBuyer(st.PartnerId, buyer, 0))
        {
            Queue(buyer, "The shop is in another trade. Try again in a minute.", sale: true);
            return;
        }

        bool restart = _phase != Phase.Idle;
        ClearSession();
        _listing = listing;
        _mode = cfg.Mode;
        _stockId = stock;
        _buyer = buyer;
        _buyerId = _game.FindPlayer(buyer);
        string price = cfg.Rates.FormatAlternatives(listing.PricePyreals);
        _game.Log($"[RynthAi] TradeShop: {buyer} wants {listing.Label()} ({price}){(restart ? " (changed their pick)" : "")}.");

        Queue(buyer, _mode == TradeShopMode.Mode1Stock
            ? $"{listing.Label()}: {price}. Open a trade with me: I'll put it in the window, then you put the payment in."
            : $"{listing.Label()}: {price}. Open a trade with me and put the payment in; the seller checks it and accepts by hand.", sale: true);

        if (st.IsOpen)
        {
            StartSession(st, now);
            return;
        }

        // The buyer opens the trade. The shop never sends one itself: on ACE the one who opens a trade
        // walks to the other player (CreateMoveToChain), and a shop must not be walked anywhere.
        _phase = Phase.OpenTrade;
        _deadline = now + OpenTradeWaitMs;
        _status = $"Waiting for {buyer} to open a trade";
    }

    // A trade with the buyer is open: Mode 1 shows the item, Mode 2 waits for the payment.
    private void StartSession(TradeShopTrade st, long now)
    {
        _sessionGeneration = st.Generation;
        if (_buyerId == 0) _buyerId = st.PartnerId;
        if (_mode == TradeShopMode.Mode1Stock)
        {
            // Our side must be exactly the item: clear the window, then add it.
            _game.TradeReset();
            if (!_game.TradeAdd(_stockId))
            {
                Abort($"couldn't put {_listing?.Label()} in the trade window", tellBuyer: true);
                return;
            }
            _phase = Phase.ShowItem;
            _deadline = now + ShowItemWaitMs;
            _status = $"Putting {_listing?.Label()} in the window for {_buyer}";
            return;
        }
        _phase = Phase.WaitPayment;
        _deadline = now + PaymentWaitMs;
        _status = $"Mode 2: waiting for {_buyer}'s payment";
    }

    // ── The trade window ────────────────────────────────────────────────

    private void PollTrade(long now)
    {
        if (!_game.TryGetTrade(out TradeShopTrade st)) return;
        if (!_baselined)
        {
            _baselined = true;
            _generation = st.Generation;
            _completed = st.CompletedCount;
            _wasOpen = st.IsOpen;
            _greetedGeneration = st.Generation;   // a trade open at load isn't greeted
            return;
        }

        if (st.CompletedCount != _completed)
        {
            _completed = st.CompletedCount;
            OnTradeCompleted();
        }

        bool newTrade = st.IsOpen && (!_wasOpen || st.Generation != _generation);
        bool ended = _wasOpen && (!st.IsOpen || st.Generation != _generation);
        _generation = st.Generation;
        _wasOpen = st.IsOpen;

        if (ended && _phase != Phase.Idle && _phase != Phase.OpenTrade)
        {
            _game.Log($"[RynthAi] TradeShop: the trade with {_buyer} closed before the sale.");
            ClearSession();
        }

        if (newTrade) OnTradeOpened(st, now);
    }

    private void OnTradeOpened(TradeShopTrade st, long now)
    {
        string partner = _game.NameOf(st.PartnerId);
        if (_phase == Phase.OpenTrade && IsBuyer(st.PartnerId, _buyer, _buyerId))
        {
            StartSession(st, now);
            return;
        }
        // A trade nobody asked to !buy in: greet once, never accept.
        if (_greetedGeneration == st.Generation) return;
        _greetedGeneration = st.Generation;
        _game.Log($"[RynthAi] TradeShop: {partner} opened a trade without !buy: greeted, not accepted.");
        if (TradeShopTellParse.IsSafeName(partner))
            Queue(partner, "Hi! This is a shop: tell me !list for prices, then !buy #<slot>. I only accept a trade after !buy.");
    }

    private void TickPhase(long now, TradeShopSettings cfg)
    {
        if (_phase == Phase.Idle) return;
        if (now > _deadline)
        {
            Abort(_phase switch
            {
                Phase.OpenTrade => "no trade was opened in time",
                Phase.ShowItem => "the item didn't show in the trade window",
                Phase.WaitPayment => "no payment in time",
                Phase.AwaitConfirm => "the sale wasn't confirmed in time",
                _ => "the buyer didn't accept in time",
            }, tellBuyer: true);
            return;
        }
        if (_phase == Phase.OpenTrade) return;

        if (!_game.TryGetTrade(out TradeShopTrade st) || !st.IsOpen || st.Generation != _sessionGeneration)
        {
            ClearSession();
            return;
        }

        // Accepted on our side while the shop hadn't accepted (the game's own Accept button, or an
        // accept still in flight from before a change): it stands only if the trade passes the check.
        if (st.YouAccepted && (_phase is Phase.ShowItem or Phase.WaitPayment or Phase.AwaitConfirm)
            && now - _declineSentAt >= AcceptEchoWaitMs)
        {
            TradeShopCheckResult r = Check(st, cfg);
            if (r.Status == TradeShopCheck.Ok)
            {
                _prompt = null;
                _lastOk = r;
                _checkedSequence = st.Sequence;
                _phase = Phase.Accepted;
                _deadline = now + AcceptedWaitMs;
                _game.Log($"[RynthAi] TradeShop: accepted on our side (by hand) with the payment checked: {PaidText(r.Tally)}.");
            }
            else
            {
                _game.TradeDecline();
                _declineSentAt = now;
                _game.Chat($"[RynthAi] Trade Shop: accept withdrawn: {r.Reason}. (Decline the sale first to trade by hand.)");
            }
            return;
        }

        switch (_phase)
        {
            case Phase.ShowItem:
                if (_game.TradeItems(partnerSide: false).Contains(_stockId))
                {
                    _phase = Phase.WaitPayment;
                    _deadline = now + PaymentWaitMs;
                    _status = $"Waiting for {_buyer}'s payment for {_listing?.Label()}";
                    Queue(_buyer, $"{_listing?.Label()} is in the window. Put in {cfg.Rates.FormatAlternatives(_listing?.PricePyreals ?? 0)}, then accept.", sale: true);
                }
                break;

            case Phase.WaitPayment:
            {
                TradeShopCheckResult r = Check(st, cfg);
                if (r.Status != TradeShopCheck.Ok)
                {
                    _status = $"Waiting for {_buyer}'s payment: {r.Reason}";
                    break;
                }
                if (_mode == TradeShopMode.Mode1Stock && !cfg.ConfirmEachSale)
                {
                    TryAccept(cfg, now, fromPlayer: false);
                    break;
                }
                ShowPrompt(st, r, cfg);
                break;
            }

            case Phase.AwaitConfirm:
                // The trade changed while the prompt was up: back to checking.
                if (st.Sequence != _checkedSequence)
                {
                    _prompt = null;
                    _phase = Phase.WaitPayment;
                    _game.Chat($"[RynthAi] Trade Shop: the trade with {_buyer} changed; checking it again.");
                }
                break;

            case Phase.Accepted:
                TickAccepted(st, cfg, now);
                break;
        }
    }

    private void ShowPrompt(TradeShopTrade st, TradeShopCheckResult r, TradeShopSettings cfg)
    {
        TradeShopListing l = _listing!;
        _checkedSequence = st.Sequence;
        _lastOk = r;
        _phase = Phase.AwaitConfirm;
        _deadline = _game.NowMs + ConfirmWaitMs;
        string paid = PaidText(r.Tally);
        _prompt = new TradeShopPrompt(_buyer, l.Label(), l.PricePyreals, cfg.Rates.FormatAlternatives(l.PricePyreals), paid, _mode);
        _status = $"Sale ready: {l.Label()} to {_buyer}; waiting for your Accept sale";
        _game.Chat($"[RynthAi] Trade Shop: sale ready. Buyer {_buyer}, {l.Label()}, price {TradeShopRates.N(l.PricePyreals)} py, paid {paid}. "
                   + "Accept sale or Decline in the Trade Shop window (/ra tradeshop accept | decline).");
        if (!_paymentSeenTold)
        {
            _paymentSeenTold = true;
            Queue(_buyer, _mode == TradeShopMode.Mode2Text
                ? "Payment seen. The seller will accept by hand in a moment."
                : "Payment seen. The seller confirms the sale in a moment.", sale: true);
        }
    }

    private void TickAccepted(TradeShopTrade st, TradeShopSettings cfg, long now)
    {
        if (st.Sequence == _checkedSequence) return;
        TradeShopCheckResult r = Check(st, cfg);
        if (r.Status != TradeShopCheck.Ok)
        {
            if (st.YouAccepted) { _game.TradeDecline(); _declineSentAt = now; }
            _game.Log($"[RynthAi] TradeShop: the trade with {_buyer} changed after the accept ({r.Reason}): accept withdrawn, checking again.");
            _game.Chat($"[RynthAi] Trade Shop: the trade with {_buyer} changed after the accept ({r.Reason}); accept withdrawn.");
            _prompt = null;
            _phase = Phase.WaitPayment;
            _deadline = now + PaymentWaitMs;
            return;
        }
        if (st.YouAccepted)
        {
            _checkedSequence = st.Sequence;   // still good (our own accept event, or the buyer's)
            _lastOk = r;
            return;
        }
        // Our accept was cleared (the buyer added an item) and the trade still passes. Right after
        // sending it, the server's echo may not be back yet: wait for it.
        if (now - _acceptSentAt < AcceptEchoWaitMs) return;
        if (_mode == TradeShopMode.Mode1Stock && !cfg.ConfirmEachSale && _reaccepts < MaxReaccepts)
        {
            _reaccepts++;
            TryAccept(cfg, now, fromPlayer: false);
            return;
        }
        if (_mode == TradeShopMode.Mode1Stock && !cfg.ConfirmEachSale)
        {
            Abort("the trade kept changing", tellBuyer: true);
            return;
        }
        ShowPrompt(st, r, cfg);
    }

    /// <summary>
    /// The accept-time check: read the state, both sides, then the state again; accept only when
    /// everything passes and nothing changed between the reads.
    /// </summary>
    private bool TryAccept(TradeShopSettings cfg, long now, bool fromPlayer)
    {
        if (!_game.TryGetTrade(out TradeShopTrade before))
            return false;
        TradeShopCheckResult r = Check(before, cfg);
        if (!_game.TryGetTrade(out TradeShopTrade after) || after.Sequence != before.Sequence
            || after.Generation != before.Generation || after.IsOpen != before.IsOpen)
        {
            r = new TradeShopCheckResult(TradeShopCheck.Pending, "the trade changed while it was checked", r.Tally);
        }
        if (r.Status != TradeShopCheck.Ok)
        {
            _prompt = null;
            _phase = Phase.WaitPayment;
            if (fromPlayer) _game.Chat($"[RynthAi] Trade Shop: not accepted: {r.Reason}. Checking again.");
            return false;
        }
        if (!_game.TradeAccept())
        {
            _game.Chat("[RynthAi] Trade Shop: the accept couldn't be sent.");
            _phase = Phase.WaitPayment;
            _prompt = null;
            return false;
        }
        _prompt = null;
        _lastOk = r;
        _checkedSequence = after.Sequence;
        _acceptSentAt = now;
        _phase = Phase.Accepted;
        _deadline = now + AcceptedWaitMs;
        _status = $"Accepted {_listing?.Label()} for {_buyer}; waiting for them to accept";
        _game.Log($"[RynthAi] TradeShop: accepted the sale of {_listing?.Label()} to {_buyer} (paid {PaidText(r.Tally)}){(fromPlayer ? " (you confirmed)" : "")}.");
        Queue(_buyer, "Accepted on my side. Accept on yours to finish.", sale: true);
        return true;
    }

    /// <summary>The check the shop runs before every accept (and while it waits for the payment).</summary>
    public TradeShopCheckResult Check(TradeShopTrade st, TradeShopSettings cfg)
    {
        TradeShopListing? l = _listing;
        if (l == null) return new(TradeShopCheck.Invalid, "no sale", null);
        if (!st.IsOpen) return new(TradeShopCheck.Invalid, "no trade open", null);
        if (st.Generation != _sessionGeneration) return new(TradeShopCheck.Invalid, "a different trade", null);
        if (!IsBuyer(st.PartnerId, _buyer, _buyerId)) return new(TradeShopCheck.Invalid, "trading with someone else", null);

        uint[] mine = _game.TradeItems(partnerSide: false) ?? Array.Empty<uint>();
        uint[] theirs = _game.TradeItems(partnerSide: true) ?? Array.Empty<uint>();
        if (_mode == TradeShopMode.Mode1Stock)
        {
            if (mine.Length != 1 || mine[0] != _stockId)
                return new(TradeShopCheck.Invalid, $"our side isn't exactly {l.Label()}", null);
            if (!StockMatches(l, _game.ReadItem(_stockId)))
                return new(TradeShopCheck.Invalid, $"the item in the window isn't {l.Label()} any more", null);
        }
        else if (mine.Length != 0)
        {
            return new(TradeShopCheck.Invalid, "our side must be empty for a Mode 2 sale", null);
        }

        TradeShopTally tally = TradeShopPayment.Tally(theirs, _game.ReadItem);
        if (tally.Unknown > 0) return new(TradeShopCheck.Pending, "reading the payment items", tally);
        long paid = cfg.Rates.AcceptedPyreals(tally);
        if (!cfg.Rates.Covers(l.PricePyreals, tally))
            return new(TradeShopCheck.Short, $"paid {TradeShopRates.N(paid)} of {TradeShopRates.N(l.PricePyreals)} py", tally);
        return new(TradeShopCheck.Ok, "paid", tally);
    }

    private void OnTradeCompleted()
    {
        if (_phase is not (Phase.Accepted or Phase.AwaitConfirm) || _listing == null)
        {
            if (_phase != Phase.Idle)
            {
                _game.Log($"[RynthAi] TradeShop: a trade completed with {_buyer} before the shop had checked the payment (accepted by hand); not logged as a sale.");
                ClearSession();
            }
            return;
        }
        bool byHand = _phase == Phase.AwaitConfirm;
        TradeShopListing l = _listing;
        string paid = PaidText(_lastOk.Tally);
        string line = $"Sold {l.Label()} to {_buyer} for {TradeShopRates.N(l.PricePyreals)} py; paid {paid}"
                      + (_mode == TradeShopMode.Mode2Text ? $" (Mode 2: hand it over{(l.HeldBy.Length > 0 ? " from " + l.HeldBy : "")})" : "")
                      + (byHand ? " (accepted in the game's trade window)" : "") + ".";
        _game.Log("[RynthAi] TradeShop: " + line);
        _game.RecordSale(line);
        _game.Chat("[RynthAi] Trade Shop: " + line);
        lock (_salesLock)
        {
            _sales.Add(_game.TimeOfDay() + " " + line);
            if (_sales.Count > RecentSalesKept) _sales.RemoveAt(0);
        }
        Queue(_buyer, _mode == TradeShopMode.Mode2Text && l.Description.Length > 0
            ? "Thanks! " + l.Description
            : "Thanks for your purchase!", sale: true);
        ClearSession();
        _game.TradeClose();   // ready for the next buyer
    }

    // ── The player's buttons ────────────────────────────────────────────

    private void AcceptFromPlayer()
    {
        if (_phase != Phase.AwaitConfirm)
        {
            _game.Chat("[RynthAi] Trade Shop: no sale is waiting for your accept.");
            return;
        }
        if (TryAccept(Cfg, _game.NowMs, fromPlayer: true))
            _game.Chat($"[RynthAi] Trade Shop: accepted. Waiting for {_buyer} to accept.");
    }

    private void DeclineFromPlayer()
    {
        if (_phase == Phase.Idle)
        {
            if (_game.TryGetTrade(out TradeShopTrade st) && st.IsOpen)
            {
                _game.TradeClose();
                _game.Chat("[RynthAi] Trade Shop: no sale was under way; the trade window was closed.");
            }
            else _game.Chat("[RynthAi] Trade Shop: no sale to decline.");
            return;
        }
        string buyer = _buyer;
        Abort("you declined it", tellBuyer: false);
        Queue(buyer, "The seller declined this sale. Sorry!", sale: true);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void Abort(string why, bool tellBuyer)
    {
        string buyer = _buyer;
        bool inTrade = _phase is not (Phase.Idle or Phase.OpenTrade);
        if (_phase == Phase.Accepted && _game.TryGetTrade(out TradeShopTrade st) && st.YouAccepted) _game.TradeDecline();
        if (inTrade) _game.TradeClose();
        _game.Log($"[RynthAi] TradeShop: sale to {buyer} called off: {why}.");
        _game.Chat($"[RynthAi] Trade Shop: sale to {buyer} called off: {why}.");
        if (tellBuyer && buyer.Length > 0) Queue(buyer, "The sale was called off (" + why + "). Tell !buy to try again.", sale: true);
        ClearSession();
    }

    private void ClearSession()
    {
        _phase = Phase.Idle;
        _listing = null;
        _stockId = 0;
        _buyer = "";
        _buyerId = 0;
        _sessionGeneration = 0;
        _checkedSequence = 0;
        _reaccepts = 0;
        _acceptSentAt = long.MinValue / 2;
        _declineSentAt = long.MinValue / 2;
        _lastOk = default;
        _paymentSeenTold = false;
        _prompt = null;
    }

    private uint ResolveStock(TradeShopListing l)
    {
        List<TradeShopItem> pack;
        try { pack = (_game.PackItems() ?? Enumerable.Empty<TradeShopItem>()).ToList(); }
        catch { return 0; }
        if (l.ObjectId != 0)
            foreach (TradeShopItem it in pack)
                if (it.Id == l.ObjectId && !it.Equipped && StockMatches(l, it)) return it.Id;
        foreach (TradeShopItem it in pack)
            if (!it.Equipped && StockMatches(l, it)) return it.Id;
        return 0;
    }

    /// <summary>The stock item is still the listed one: same WCID (when known), name, and stack size (when a stack).</summary>
    public static bool StockMatches(TradeShopListing l, TradeShopItem? read)
    {
        if (read is not { } it || !it.Known || it.Equipped) return false;
        if (l.Wcid != 0 && it.Wcid != l.Wcid) return false;
        if (!string.Equals((it.Name ?? "").Trim(), l.Name.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (l.Stack > 0 && it.Stack != l.Stack) return false;
        return true;
    }

    private bool IsBuyer(uint partnerId, string buyer, uint buyerId)
    {
        if (partnerId == 0) return false;
        if (buyerId != 0) return partnerId == buyerId;
        return NamesEqual(_game.NameOf(partnerId), buyer);
    }

    private static string PaidText(TradeShopTally? t)
        => t == null ? "nothing" : t.Lines.Count == 0 ? "nothing" : string.Join(", ", t.Lines);

    private static bool NamesEqual(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
