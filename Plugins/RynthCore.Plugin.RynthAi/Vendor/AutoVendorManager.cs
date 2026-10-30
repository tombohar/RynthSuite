using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Vendor;

/// <summary>
/// UtilityBelt's AutoVendor on RynthCore's vendor trading calls (plugin API v67).
///
/// When a vendor opens (or on /ub autovendor) it loads a VTank .utl named for the vendor
/// (or default.utl), identifies inventory items a rule could want, then repeats one
/// transaction at a time until there's nothing left: buy what Keep / Keep # rules want,
/// else sell what Sell rules match, splitting stacks when needed. Each transaction waits
/// for the server (engine trade status) and for the inventory to show the result.
///
/// Threading: everything here runs on the plugin tick thread (Tick, OnVendorOpen/Close,
/// which the engine dispatches from the same pump). Chat commands arrive on AC's main
/// thread and are queued with <see cref="Enqueue"/>. The engine's vendor calls are safe
/// from any thread; they read a snapshot and queue sends for AC's main thread.
///
/// On an engine older than API v67 the vendor calls are absent (Host.HasVendorTrade is
/// false): AutoVendor says it needs a RynthCore update and does nothing else. Opening a
/// vendor (/ub vendor open) only needs UseObject and works on any engine.
/// </summary>
internal sealed class AutoVendorManager
{
    public static readonly string MainProfileDir = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "AutoVendor");
    public const string ProfileSubfolder = "AutoVendor";

    private const long BailMs = 60_000;          // UB bailTimer
    private const long ConfirmTimeoutMs = 15_000; // UB lastEvent timeout
    private const long TradeWaitMs = 15_000;      // engine answers or times out in 10 s
    private const long SplitWaitMs = 10_000;
    private const long RetryDelayMs = 1_500;
    private const int MaxFailures = 3;
    private const int IdMaxInFlight = 4;          // UB Assessor pacing
    private const long IdSpacingMs = 75;
    private const long IdResendMs = 1_500;
    private const int IdMaxAttempts = 3;
    private const long PollMs = 300;

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _cache;
    private readonly Func<string> _charFolder;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly VendorCart _cart = new();
    private uint _playerId;

    // ── Vendor tracking ──
    private uint _openVendorId;          // open right now (events)
    private uint _vendorId;              // last approached (UB vendorId)
    private string _vendorName = string.Empty;
    private volatile string? _openVendorProfilePath;
    /// <summary>The open vendor's AutoVendor profile (existing, or where it would be created); null when none is open.</summary>
    public string? OpenVendorProfilePath => _openVendorProfilePath;
    private bool _needsUpdateSaid;

    // ── Session ──
    private enum Phase { Idle, Identifying, Ready, WaitTrade, WaitConfirm, WaitSplit, WaitMerge }
    private Phase _phase = Phase.Idle;
    private VTankLootProfile? _profile;
    private string _profilePath = string.Empty;
    private long _bailAt;
    private long _nextActionAt;
    private long _nextPollAt;
    private int _failures;
    private readonly HashSet<uint> _sessionSkip = new();   // items the engine refused to sell
    private readonly HashSet<string> _sellSkipLogged = new(StringComparer.OrdinalIgnoreCase); // names logged as held back this session

    private readonly List<uint> _idQueue = new();
    private readonly Dictionary<uint, (int Attempts, long LastAt)> _idState = new();
    private long _lastIdSentAt;

    private uint _requestId;
    private bool _tradeIsBuy;
    private long _tradeSentAt;
    private readonly List<uint> _lastSellIds = new();
    private readonly List<(string Name, int Target)> _expectBought = new();
    private readonly HashSet<uint> _expectSold = new();
    private long _confirmDeadline;

    private string _splitName = string.Empty;
    private int _splitAmount;
    private HashSet<uint> _splitBefore = new();
    private long _splitDeadline;
    // A split or merge that never shows up (no new stack / nothing moved): MaxFailures in a row ends the
    // session instead of retrying every 10 s forever.
    private int _tidyFails;
    private int _tidySteps;                       // splits/merges since the last trade
    private const int MaxTidySteps = 8;
    private uint _mergeSource, _mergeTarget;
    private int _mergeSourceCount, _mergeTargetCount;

    private int _pyrealStackSize;

    // ── /ub vendor open ──
    private bool _opening;
    private uint _openTarget;
    private string _openTargetName = string.Empty;
    private int _openAttempts;
    private long _openNextAt;
    private long _openHoldUntil;   // UB's nav lock outlives the open by 500 ms + TriesTime

    // ── /ub vendor addsell ──
    private sealed class AddSellJob
    {
        public string Name = string.Empty;
        public int Count;
        public bool Partial;
        public int Stage;              // 0 = identifying, 1 = plan, 2 = waiting for split
        public long Deadline;
        public string SplitName = string.Empty;
        public int Missing;
        public string FirstName = string.Empty;
        public HashSet<uint> Before = new();
    }
    private AddSellJob? _addSell;

    private volatile string _status = "Idle";

    public AutoVendorManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache cache, uint playerId, Func<string> charFolder)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
        _playerId = playerId;
        _charFolder = charFolder;
        try { Directory.CreateDirectory(MainProfileDir); } catch { }
    }

    /// <summary>A session or a vendor-open attempt is running: nav, looting and inventory moves wait.</summary>
    public bool HoldsBot => _phase != Phase.Idle || _opening || Now() < _openHoldUntil;

    /// <summary>One line for the settings UI (any thread).</summary>
    public string Status => _status;

    public bool VendorApiAvailable => _host.HasVendorTrade;

    /// <summary>Queue work from another thread (chat commands); runs on the next Tick.</summary>
    public void Enqueue(Action action)
    {
        if (action != null) _commands.Enqueue(action);
    }

    public void SetPlayerId(uint id) { if (id != 0) _playerId = id; }

    // ════════════════════════════════════════════════════════════════════════
    //  Vendor events (tick thread)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>UB WorldFilter_ApproachVendor. Also fires on every vendor-list re-send.</summary>
    public void OnVendorOpen(uint vendorId)
    {
        if (vendorId == 0) return;
        bool fresh = vendorId != _openVendorId;
        _openVendorId = vendorId;

        if (_opening && vendorId == _openTarget)
        {
            _opening = false;
            _host.Log($"[RynthAi] AutoVendor: vendor {_openTargetName} opened successfully");
        }

        if (fresh)
        {
            PrintVendorOpened(vendorId);
            EnsureProfileFolders();
            // For the dashboard's Edit button: the profile this vendor uses, or where its
            // own would be created. Resolved once per open (it checks files on disk).
            try { _openVendorProfilePath = ResolveProfilePath(VendorName(vendorId) + ".utl", false); }
            catch { _openVendorProfilePath = null; }
        }

        if (_phase != Phase.Idle)
            return;   // our own trade's list re-send, or a restart mid-run

        if (vendorId != _vendorId)
        {
            _vendorId = vendorId;
            _vendorName = VendorName(vendorId);
        }

        if (!_settings.AutoVendorEnabled)
            return;

        if (!_host.HasVendorTrade)
        {
            if (!_needsUpdateSaid) { _needsUpdateSaid = true; SayNeedsUpdate(); }
            return;
        }

        Start(vendorId, string.Empty, explicitCommand: false);
    }

    public void OnVendorClose(uint vendorId)
    {
        if (vendorId == 0) return;
        if (vendorId == _openVendorId)
        {
            _openVendorId = 0;
            _openVendorProfilePath = null;
            Chat($"Vendor closed: {VendorName(vendorId)}");
            _cart.ClearBuy();
            _cart.ClearSell();
        }
        if (_phase != Phase.Idle)
            Stop(false);
    }

    private void PrintVendorOpened(uint vendorId)
    {
        string name = VendorName(vendorId);
        if (_settings.AutoVendorShowMerchantInfo && _host.HasVendorTrade
            && _host.TryGetVendorInfo(out VendorInfo vi) && vi.VendorId == vendorId)
        {
            bool on = _settings.AutoVendorEnabled;
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0}[0x{1:X8}]: BuyRate: {2:n0}% SellRate: {3:n0}% MaxValue: {4:n0} Buy: {5} Sell: {6}",
                name, vendorId, vi.BuyRate * 100, vi.SellRate * 100, vi.MaxValue,
                on && _settings.AutoVendorEnableBuying ? "Enabled" : "Disabled",
                on && _settings.AutoVendorEnableSelling ? "Enabled" : "Disabled");
            if (vi.UsesAltCurrency)
                line += $" Currency: {(string.IsNullOrEmpty(vi.AltCurrencyName) ? $"wcid {vi.AltCurrencyWcid}" : vi.AltCurrencyName)}";
            Chat(line);
        }
        else
        {
            Chat($"Vendor open: {name}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Commands (queued from chat; run on the tick thread)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>/ub autovendor [cancel|stop|quit|profile]</summary>
    public void CommandAutoVendor(string arg)
    {
        string a = (arg ?? string.Empty).Trim();
        if (a.Equals("cancel", StringComparison.OrdinalIgnoreCase)
            || a.Equals("stop", StringComparison.OrdinalIgnoreCase)
            || a.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            Stop(false);
            return;
        }
        if (!_host.HasVendorTrade) { SayNeedsUpdate(); return; }
        Start(0, a, explicitCommand: true);
        if (_phase != Phase.Idle)
            Said($"started at {_vendorName} with {Path.GetFileName(_profilePath)}{(_settings.AutoVendorTestMode ? " (TestMode: nothing is bought or sold)" : "")}.");
    }

    public void CommandBuyAll()
    {
        if (!_host.HasVendorTrade) { SayNeedsUpdate(); return; }
        if (_cart.BuyList.Count == 0) { Tool("buy list is empty"); return; }
        int entries = _cart.BuyList.Count;
        if (_cart.BuyAll(_host, _openVendorId) == 0)
            Error($"buy refused: {TradeMessage()}");
        else
            Said($"buying the buy list ({entries} entr{(entries == 1 ? "y" : "ies")}).");
    }

    public void CommandSellAll()
    {
        if (!_host.HasVendorTrade) { SayNeedsUpdate(); return; }
        if (_cart.SellList.Count == 0) { Tool("sell list is empty"); return; }
        int items = _cart.SellList.Count;
        if (_cart.SellAll(_host, _openVendorId) == 0)
            Error($"sell refused: {TradeMessage()}");
        else
            Said($"selling the sell list ({items} item(s)).");
    }

    public void CommandClearBuy() { _cart.ClearBuy(); Said("buy list cleared."); }
    public void CommandClearSell() { _cart.ClearSell(); Said("sell list cleared."); }

    /// <summary>/ub vendor addbuy[p] [count] name</summary>
    public void CommandAddBuy(string args, bool partial)
    {
        if (!_host.HasVendorTrade) { SayNeedsUpdate(); return; }
        var (count, name) = AutoVendorPlanner.ParseCountAndName(args);
        if (_openVendorId == 0 || !_host.TryGetVendorInfo(out _))
        {
            Error("addbuy: No vendor open");
            return;
        }
        string want = name.ToLowerInvariant();
        foreach (VendorItem it in _host.GetVendorItems())
        {
            string n = it.Name.ToLowerInvariant();
            if (partial ? n.Contains(want) : n == want)
            {
                _cart.AddBuy(it.ObjectId, count);
                Chat($"Added item to buy list: {it.Name} * {count}");
                return;
            }
        }
        Error($"addbuy: Unable to find item {(partial ? "partially " : string.Empty)}named '{name}' in vendor sell list");
    }

    /// <summary>/ub vendor addsell[p] [count] name (splits a stack when no whole stacks add up).</summary>
    public void CommandAddSell(string args, bool partial)
    {
        if (!_host.HasVendorTrade) { SayNeedsUpdate(); return; }
        var (count, name) = AutoVendorPlanner.ParseCountAndName(args);
        if (_openVendorId == 0 || !_host.TryGetVendorInfo(out _))
        {
            Error("addsell: No vendor open");
            return;
        }
        if (_addSell != null) { Error("addsell: still working on the last addsell"); return; }

        var job = new AddSellJob { Name = name, Count = Math.Max(1, count), Partial = partial, Deadline = Now() + 5_000 };
        // Identify matches first: the safety checks read appraisal data.
        int asked = 0;
        foreach (var wo in Inventory())
        {
            if (!NameMatches(wo.Name, name, partial)) continue;
            uint id = unchecked((uint)wo.Id);
            if (!Appraised(id) && _host.HasRequestId && asked < 20)
            {
                _host.RequestId(id);
                asked++;
            }
        }
        job.Stage = asked > 0 ? 0 : 1;
        _addSell = job;
    }

    /// <summary>/ub vendor open[p] [name|id|hex|selected]</summary>
    public void CommandOpen(string target, bool partial)
    {
        WorldObject? vendor = FindVendor(target ?? string.Empty, partial);
        if (vendor == null)
        {
            ThinkOrWrite("AutoVendor failed to open vendor");
            return;
        }
        _openTarget = unchecked((uint)vendor.Id);
        _openTargetName = vendor.Name;
        _openAttempts = 1;
        _openNextAt = Now() + 250;   // UB fudges the first attempt to ~250 ms after the command
        _opening = true;
        if (_host.HasSetAutoRun) _host.SetAutoRunBy("Vendor", false);
        _host.Log($"[RynthAi] AutoVendor: attempting to open vendor {vendor.Name}");
        Said($"opening {vendor.Name}.");
    }

    /// <summary>/ub vendor opencancel</summary>
    public void CommandOpenCancel()
    {
        bool was = _opening;
        if (_opening) CancelUse();
        _opening = false;
        _openHoldUntil = 0;
        Said(was ? $"stopped opening {_openTargetName}." : "no vendor was being opened.");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Tick
    // ════════════════════════════════════════════════════════════════════════

    public void Tick(int busyCount)
    {
        while (_commands.TryDequeue(out Action? cmd))
        {
            try { cmd(); }
            catch (Exception ex) { _host.Log($"[RynthAi] AutoVendor command failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        long now = Now();
        TickOpener(now);
        TickAddSell(now, busyCount);

        if (_phase == Phase.Idle)
        {
            UpdateStatus();
            return;
        }

        if (now - _bailAt > BailMs)
        {
            Tool("bail, Timeout expired");
            Stop(false);
            return;
        }
        if (!_settings.AutoVendorEnabled)
        {
            Stop(false);
            return;
        }

        switch (_phase)
        {
            case Phase.Identifying: TickIdentify(now); break;
            case Phase.Ready:       TickReady(now, busyCount); break;
            case Phase.WaitTrade:   TickWaitTrade(now); break;
            case Phase.WaitConfirm: TickWaitConfirm(now); break;
            case Phase.WaitSplit:   TickWaitSplit(now); break;
            case Phase.WaitMerge:   TickWaitMerge(now); break;
        }
        UpdateStatus();
    }

    /// <summary>Logout / teardown: drop everything without chat.</summary>
    public void Reset()
    {
        _phase = Phase.Idle;
        _opening = false;
        _openHoldUntil = 0;
        _addSell = null;
        _profile = null;
        _openVendorId = 0;
        _cart.ClearBuy();
        _cart.ClearSell();
        while (_commands.TryDequeue(out _)) { }
        _status = "Idle";
    }

    // ── Session start / stop (UB Start / Stop) ──

    private void Start(uint merchantId, string profileArg, bool explicitCommand)
    {
        if (merchantId == 0) merchantId = _vendorId;
        if (!string.IsNullOrEmpty(profileArg) && merchantId == 0) merchantId = _openVendorId;
        if (merchantId == 0)
        {
            ThinkOrWrite("AutoVendor Fatal - no vendor, cannot start");
            return;
        }

        if (_phase != Phase.Idle)
            Stop(true);

        _vendorId = merchantId;
        _vendorName = VendorName(merchantId);

        if (explicitCommand && !_settings.AutoVendorEnabled)
        {
            Tool("AutoVendor is disabled (/ub opt set AutoVendor.Enabled true)");
            Stop(false);
            return;
        }

        bool fromArg = !string.IsNullOrEmpty(profileArg);
        string path = ResolveProfilePath(fromArg ? profileArg : _vendorName + ".utl", fromArg);
        if (!File.Exists(path))
        {
            Error($"No vendor profile exists: {path}");
            Stop(false);
            return;
        }

        try
        {
            _profile = VTankLootParser.Load(path);
        }
        catch (Exception ex)
        {
            Error($"Unable to load loot profile {path}: {ex.Message}");
            Stop(false);
            return;
        }

        _profilePath = path;
        _failures = 0;
        _sessionSkip.Clear();
        _sellSkipLogged.Clear();
        _bailAt = Now();
        _nextActionAt = 0;
        _host.Log($"[RynthAi] AutoVendor: {_vendorName} [0x{merchantId:X8}] profile '{path}' ({_profile.Rules.Count} rules)");

        BuildIdQueue();
        _phase = Phase.Identifying;
    }

    private void Stop(bool silent)
    {
        if (!silent)
            ThinkOrWrite($"AutoVendor finished: {_vendorName}");
        _phase = Phase.Idle;
        _tidyFails = 0;
        _tidySteps = 0;
        _profile = null;
        _requestId = 0;
        _idQueue.Clear();
        _idState.Clear();
        _expectBought.Clear();
        _expectSold.Clear();
        _lastSellIds.Clear();
    }

    // ── Identify (UB Assessor job) ──

    private void BuildIdQueue()
    {
        _idQueue.Clear();
        _idState.Clear();
        if (_profile == null || !_settings.AutoVendorEnableSelling || !_host.HasRequestId || !_host.HasHasAppraisalData)
            return;
        if (!TryGetVendor(out VendorView v, out _))
            return;

        var ctx = NewContext();
        uint player = PlayerId();
        foreach (var wo in Inventory())
        {
            uint id = unchecked((uint)wo.Id);
            if (IsEquipped(wo)) continue;
            if (_settings.AutoVendorOnlyFromMainPack && !InMainPack(wo, player)) continue;
            if (wo.ObjectClass is AcObjectClass.Container or AcObjectClass.Foci or AcObjectClass.Money) continue;
            if (Appraised(id)) continue;
            uint type = ItemType(wo);
            if (type == 0 || !v.Buys(type)) continue;

            // Only ID what a Sell rule might still take: skip items no rule can match, and items
            // a Keep (or other non-Sell) rule already decides without ID data.
            ItemFacts f = InventoryFacts(wo, appraised: false);
            ProfileVerdict verdict = AutoVendorRules.Decide(_profile.Rules, Judge(EvalObject(wo), f, ctx));
            if (verdict.Rule == null) continue;
            if (verdict.Certain && verdict.Rule.Action != VTankLootAction.Sell) continue;

            _idQueue.Add(id);
        }
        if (_idQueue.Count > 0)
            Tool($"identifying {_idQueue.Count} item(s)...");
    }

    private void TickIdentify(long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + 100;

        int inFlight = 0;
        for (int i = _idQueue.Count - 1; i >= 0; i--)
        {
            uint id = _idQueue[i];
            if (Appraised(id))
            {
                _idQueue.RemoveAt(i);
                _idState.Remove(id);
                _bailAt = now;
                continue;
            }
            if (_idState.TryGetValue(id, out var st))
            {
                bool waiting = now - st.LastAt < IdResendMs;
                if (!waiting && st.Attempts >= IdMaxAttempts)
                {
                    _idQueue.RemoveAt(i);   // give up; it stays unsellable
                    _idState.Remove(id);
                    continue;
                }
                if (waiting) inFlight++;
            }
        }

        if (_idQueue.Count == 0)
        {
            if (_settings.AutoVendorTestMode)
            {
                DoTestMode();
                Stop(false);
                return;
            }
            _phase = Phase.Ready;
            return;
        }

        if (inFlight >= IdMaxInFlight || now - _lastIdSentAt < IdSpacingMs)
            return;
        foreach (uint id in _idQueue)
        {
            _idState.TryGetValue(id, out var st);
            if (st.Attempts > 0 && now - st.LastAt < IdResendMs) continue;
            if (!_host.RequestId(id)) break;   // refused (a death or a portal): not an attempt, asked again next tick
            _idState[id] = (st.Attempts + 1, now);
            _lastIdSentAt = now;
            break;
        }
    }

    // ── Plan and act (UB Core_RenderFrame + DoVendoring) ──

    private void TickReady(long now, int busyCount)
    {
        if (!TryGetVendor(out VendorView v, out VendorInfo vi) || v.Id != _vendorId)
        {
            if (now - _bailAt > 500)
            {
                _host.Log("[RynthAi] AutoVendor: stop because no vendor");
                Stop(false);
            }
            return;
        }
        if (now < _nextActionAt || busyCount != 0 || vi.TradeInFlight)
            return;
        if (!vi.TradingAvailable)
        {
            ThinkOrWrite("AutoVendor Fatal - the engine can't trade with this vendor (see RynthCore.log)");
            Stop(false);
            return;
        }

        var inv = Inventory();
        var ctx = NewContext();
        List<BuyWant> wants = CollectWants(v, inv, ctx);
        List<SellItem> sells = AutoVendorPlanner.SortSells(v, CollectSells(v, inv, ctx, null));
        PackState pack = ReadPack(v, vi, inv);
        VendorPlan plan = AutoVendorPlanner.PlanNext(v, wants, sells, pack);

        // Splits and merges only get the stacks ready for a trade: a run of them with no trade between
        // is going in circles (split-merge-split, 2026-10-19), so it ends the session.
        if (plan.Kind is PlanKind.Split or PlanKind.Merge)
        {
            if (++_tidySteps > MaxTidySteps)
            {
                ThinkOrWrite($"AutoVendor Fatal - {MaxTidySteps} splits/merges of {plan.SplitItem?.Name} without a sale");
                Stop(false);
                return;
            }
        }
        else
            _tidySteps = 0;

        switch (plan.Kind)
        {
            case PlanKind.Done:
                Stop(false);
                break;

            case PlanKind.Fatal:
                ThinkOrWrite(plan.Message);
                Stop(false);
                break;

            case PlanKind.Buy:
                SendBuy(v, plan, pack, inv, now);
                break;

            case PlanKind.Sell:
                SendSell(v, plan, now);
                break;

            case PlanKind.Split:
                DoSplit(plan.SplitItem!, plan.SplitAmount, plan.Message, inv, now);
                break;

            case PlanKind.Merge:
                DoMerge(plan.SplitItem!, plan.MergeTarget!, plan.Message, now);
                break;
        }
    }

    private void SendBuy(VendorView v, VendorPlan plan, PackState pack, List<WorldObject> inv, long now)
    {
        var entries = new List<VendorTradeEntryNative>(plan.Buys.Count);
        var sb = new StringBuilder();
        _expectBought.Clear();
        foreach (var line in plan.Buys)
        {
            entries.Add(new VendorTradeEntryNative(line.Item.Id, line.Amount));
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(line.Amount.ToString("n0", CultureInfo.InvariantCulture)).Append("x ").Append(line.Item.Name);
            int have = CountByName(inv, line.Item.Name);
            int prev = _expectBought.FindIndex(e => e.Name == line.Item.Name);
            if (prev >= 0) _expectBought[prev] = (line.Item.Name, _expectBought[prev].Target + line.Amount);
            else _expectBought.Add((line.Item.Name, have + line.Amount));
        }
        _host.Log($"[RynthAi] AutoVendor Buy List: {sb} - {plan.Total}/{pack.Funds}");

        uint id = _host.VendorBuy(v.Id, entries);
        if (id == 0)
        {
            TradeFailed(isBuy: true, TradeMessage(), now);
            return;
        }
        Tool(string.Format(CultureInfo.InvariantCulture, "buying {0} (~{1:n0} {2})", sb, plan.Total, v.UsesAltCurrency ? "currency" : "pyreals"));
        _requestId = id;
        _tradeIsBuy = true;
        _tradeSentAt = now;
        _bailAt = now;
        _phase = Phase.WaitTrade;
    }

    private void SendSell(VendorView v, VendorPlan plan, long now)
    {
        var ids = plan.Sells.Select(s => s.Id).ToList();
        _expectSold.Clear();
        _lastSellIds.Clear();
        foreach (uint i in ids) { _expectSold.Add(i); _lastSellIds.Add(i); }
        string names = string.Join(", ", plan.Sells.Select(s => s.StackSize > 1 ? $"{s.Name} x{s.StackSize}" : s.Name));
        _host.Log($"[RynthAi] AutoVendor Sell List: {names} - {plan.Total}");

        uint id = _host.VendorSell(v.Id, ids);
        if (id == 0)
        {
            TradeFailed(isBuy: false, TradeMessage(), now);
            return;
        }
        Tool(string.Format(CultureInfo.InvariantCulture, "selling {0} item(s) (~{1:n0} pyreals)", ids.Count, plan.Total));
        _requestId = id;
        _tradeIsBuy = false;
        _tradeSentAt = now;
        _bailAt = now;
        _phase = Phase.WaitTrade;
    }

    private static readonly Regex HexId = new(@"0x([0-9A-Fa-f]{8})", RegexOptions.CultureInvariant);

    private void TradeFailed(bool isBuy, string message, long now)
    {
        Error($"vendor {(isBuy ? "buy" : "sell")} refused: {message}");

        // An item the engine won't sell (e.g. "0x80001234 is not in your packs") is dropped for
        // this session rather than blocking every later batch; that doesn't count as a failure.
        if (!isBuy)
        {
            Match m = HexId.Match(message ?? string.Empty);
            if (m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bad)
                && _lastSellIds.Contains(bad) && _sessionSkip.Add(bad))
            {
                _phase = Phase.Ready;
                _nextActionAt = now + RetryDelayMs;
                return;
            }
        }

        if (++_failures >= MaxFailures)
        {
            ThinkOrWrite($"AutoVendor Fatal - {message}");
            Stop(false);
            return;
        }
        _phase = Phase.Ready;
        _nextActionAt = now + RetryDelayMs;
    }

    private void TickWaitTrade(long now)
    {
        bool mine = _host.TryGetVendorTradeStatus(out VendorTradeStatus st) && st.RequestId == _requestId;
        if (!mine || !st.IsFinished)
        {
            if (now - _tradeSentAt > TradeWaitMs)
            {
                _host.Log("[RynthAi] AutoVendor: no trade status in time; checking the inventory");
                EnterConfirm(now);
            }
            return;
        }

        if (st.State == VendorTradeState.Refused)
        {
            TradeFailed(_tradeIsBuy, st.Message, now);
            return;
        }
        switch (st.Result)
        {
            case VendorTradeResult.Accepted:
                _failures = 0;
                EnterConfirm(now);
                break;
            case VendorTradeResult.AnsweredNoRefresh:
                TradeFailed(_tradeIsBuy, string.IsNullOrEmpty(st.Message) ? "the server refused" : st.Message, now);
                break;
            default:   // timed out: it may still have gone through, so look before planning again
                if (++_failures >= MaxFailures)
                {
                    ThinkOrWrite($"AutoVendor Fatal - {st.Message}");
                    Stop(false);
                    return;
                }
                EnterConfirm(now);
                break;
        }
    }

    private void EnterConfirm(long now)
    {
        _phase = Phase.WaitConfirm;
        _confirmDeadline = now + ConfirmTimeoutMs;
        _nextPollAt = now + PollMs;
        _bailAt = now;
    }

    /// <summary>UB pendingBuy / pendingSell: wait for the inventory to show the trade.</summary>
    private void TickWaitConfirm(long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + PollMs;

        var inv = Inventory();
        int sellLeft = 0;
        if (_expectSold.Count > 0)
        {
            var have = new HashSet<uint>(inv.Select(w => unchecked((uint)w.Id)));
            foreach (uint id in _expectSold)
                if (have.Contains(id)) sellLeft++;
        }
        int buyLeft = 0;
        foreach (var (name, target) in _expectBought)
            if (CountByName(inv, name) < target) buyLeft++;

        if (sellLeft == 0 && buyLeft == 0)
        {
            _expectSold.Clear();
            _expectBought.Clear();
            _phase = Phase.Ready;
            return;
        }
        if (now > _confirmDeadline)
        {
            _host.Log($"[RynthAi] AutoVendor: event timeout. Sell list: {sellLeft}, buy list: {buyLeft}");
            _expectSold.Clear();
            _expectBought.Clear();
            _phase = Phase.Ready;
        }
    }

    private void DoSplit(SellItem item, int amount, string why, List<WorldObject> inv, long now)
    {
        if (!_host.HasSplitStackInternal)
        {
            ThinkOrWrite("AutoVendor Fatal - this engine can't split stacks");
            Stop(false);
            return;
        }
        uint player = PlayerId();
        _splitBefore = new HashSet<uint>(inv.Select(w => unchecked((uint)w.Id)));
        _splitName = item.Name;
        _splitAmount = amount;
        _host.Log($"[RynthAi] AutoVendor DoSplit {item.Name}:{item.Id:X8} old: {item.StackSize} new: {amount} ({why})");
        if (player == 0 || !_host.SplitStackInternal(item.Id, player, 0, amount))
        {
            TradeFailed(isBuy: false, $"could not split {item.Name}", now);
            return;
        }
        Tool($"splitting {amount} off {item.Name} ({why})");
        _splitDeadline = now + SplitWaitMs;
        _nextPollAt = now + PollMs;
        _bailAt = now;
        _phase = Phase.WaitSplit;
    }

    private void TickWaitSplit(long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + PollMs;
        if (FindNewStack(Inventory(), _splitBefore, _splitName, _splitAmount) != 0)
        {
            _tidyFails = 0;
            _phase = Phase.Ready;
        }
        else if (now > _splitDeadline)
            TidyFailed($"splitting {_splitAmount} off {_splitName} made no new stack");
    }

    private void DoMerge(SellItem source, SellItem target, string why, long now)
    {
        if (!_host.HasMergeStackInternal)
        {
            ThinkOrWrite("AutoVendor Fatal - this engine can't merge stacks");
            Stop(false);
            return;
        }
        _mergeSource = source.Id;
        _mergeTarget = target.Id;
        _mergeSourceCount = source.StackSize;
        _mergeTargetCount = target.StackSize;
        _splitName = source.Name;
        _host.Log($"[RynthAi] AutoVendor merge {source.Name}:{source.Id:X8} ({source.StackSize}) onto {target.Id:X8} ({target.StackSize}) - {why}");
        if (!_host.MergeStackInternal(source.Id, target.Id))
        {
            TidyFailed($"could not merge {source.Name}");
            return;
        }
        Tool($"merging {source.Name} stacks ({why})");
        _splitDeadline = now + SplitWaitMs;
        _nextPollAt = now + PollMs;
        _bailAt = now;
        _phase = Phase.WaitMerge;
    }

    private void TickWaitMerge(long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + PollMs;
        var inv = Inventory();
        var src = inv.FirstOrDefault(w => unchecked((uint)w.Id) == _mergeSource);
        var tgt = inv.FirstOrDefault(w => unchecked((uint)w.Id) == _mergeTarget);
        // Done when the source is used up, or either count moved (past the max the target fills and
        // the source survives smaller).
        if (src == null || StackOf(src) != _mergeSourceCount || (tgt != null && StackOf(tgt) != _mergeTargetCount))
        {
            _tidyFails = 0;
            _phase = Phase.Ready;
        }
        else if (now > _splitDeadline)
            TidyFailed($"merging {_splitName} stacks moved nothing");
    }

    private void TidyFailed(string what)
    {
        _tidyFails++;
        _host.Log($"[RynthAi] AutoVendor: {what} ({_tidyFails}/{MaxFailures})");
        if (_tidyFails >= MaxFailures)
        {
            _tidyFails = 0;
            ThinkOrWrite($"AutoVendor Fatal - {what}, {MaxFailures} times in a row");
            Stop(false);
            return;
        }
        _phase = Phase.Ready;
    }

    // ── Collect (UB GetBuyItems / GetSellItems) ──

    private List<BuyWant> CollectWants(VendorView v, List<WorldObject> inv, VTankLootContext ctx)
    {
        if (!_settings.AutoVendorEnableBuying || _profile == null)
            return new List<BuyWant>();
        var judged = new List<(Listing, BuyDecision)>();
        foreach (Listing l in Listings(v))
        {
            ItemFacts f = ListingFacts(l);
            judged.Add((l, AutoVendorRules.DecideBuy(_profile.Rules, Judge(ListingObject(l), f, ctx))));
        }
        return AutoVendorPlanner.BuildBuyWants(v, judged, n => CountByName(inv, n), c => CountByClass(inv, c));
    }

    /// <param name="heldBack">Test mode: items a Sell rule points at that are held back, with why.</param>
    private List<SellItem> CollectSells(VendorView v, List<WorldObject> inv, VTankLootContext ctx, List<string>? heldBack)
    {
        var result = new List<SellItem>();
        if (!_settings.AutoVendorEnableSelling || _profile == null)
            return result;

        uint player = PlayerId();
        var packOrder = PackOrder(inv, player);
        var unitValues = TrueUnitValues(inv);
        foreach (var wo in inv)
        {
            uint id = unchecked((uint)wo.Id);
            if (_sessionSkip.Contains(id)) continue;

            SafetyFacts safety = ReadSafety(wo, player);
            string? unsafeWhy = AutoVendorPlanner.UnsafeReason(safety, _settings.AutoVendorOnlyFromMainPack);
            if (unsafeWhy != null && heldBack == null)
            {
                if (!_sellSkipLogged.Contains(wo.Name))
                {
                    ProfileVerdict uv = AutoVendorRules.Decide(_profile.Rules, Judge(EvalObject(wo), InventoryFacts(wo, safety.Appraised), ctx));
                    // "Not identified" is usually just not yet (the IDs are on their way): only a
                    // certain Sell match is worth a line.
                    if (uv.Rule?.Action == VTankLootAction.Sell && uv.Certain && _sellSkipLogged.Add(wo.Name))
                        _host.Log($"[RynthAi] AutoVendor: not selling '{wo.Name}' (rule '{uv.RuleName}'): {unsafeWhy}.");
                }
                continue;
            }

            uint type = ItemType(wo);
            int stack = StackOf(wo);
            bool isNote = wo.ObjectClass == AcObjectClass.TradeNote || (type & AutoVendorPlanner.ItemTypePromissoryNote) != 0;
            int unitValue = stack > 1 ? safety.Value / stack : safety.Value;
            // A stack keeps its old Value after a split (100 peas' worth on a stack of 2 read "1,250,000
            // each", 2026-10-19), which hid it from the sell list and from the split planner. Use the
            // per-unit value most of this item's units carry (TrueUnitValues).
            bool stackable = InventoryManager.GetMaxStackSize(wo) > 1;
            if (stackable && unitValues.TryGetValue(StackKey(wo), out int trueUnit) && trueUnit > 0)
                unitValue = trueUnit;
            int stackValue = stackable ? unitValue * Math.Max(1, stack) : safety.Value;
            if (unsafeWhy == null && !AutoVendorPlanner.VendorWillBuy(v, type, isNote, unitValue))
            {
                // A Sell rule wanted it and the vendor won't take it: say why, once per name a
                // session (peas not sold at Aun Amanaualuan, 2026-10-07, with nothing logged).
                if (heldBack == null && !_sellSkipLogged.Contains(wo.Name))
                {
                    ProfileVerdict sv = AutoVendorRules.Decide(_profile.Rules, Judge(EvalObject(wo), InventoryFacts(wo, safety.Appraised), ctx));
                    if (sv.Rule?.Action == VTankLootAction.Sell && sv.Certain && _sellSkipLogged.Add(wo.Name))
                        _host.Log(!v.Buys(type)
                            ? $"[RynthAi] AutoVendor: not selling '{wo.Name}' (rule '{sv.RuleName}'): this vendor doesn't buy {AutoVendorPlanner.CategoryName(type)} (item type 0x{type:X}, vendor buys 0x{v.ItemTypes:X})."
                            : $"[RynthAi] AutoVendor: not selling '{wo.Name}' (rule '{sv.RuleName}'): worth {unitValue:n0} each, this vendor pays for items up to {v.MaxValue:n0}.");
                }
                continue;
            }

            ItemFacts f = InventoryFacts(wo, safety.Appraised);
            ProfileVerdict verdict = AutoVendorRules.Decide(_profile.Rules, Judge(EvalObject(wo), f, ctx));
            SellDecision sd = AutoVendorRules.JudgeSell(verdict);

            if (unsafeWhy != null)
            {
                if (verdict.Rule?.Action == VTankLootAction.Sell)
                    heldBack!.Add($"{wo.Name} - {unsafeWhy}");
                continue;
            }
            if (!sd.Sell)
            {
                if (heldBack != null && verdict.Rule?.Action == VTankLootAction.Sell)
                    heldBack.Add($"{wo.Name} - {sd.Reason}");
                else if (heldBack == null && verdict.Rule?.Action == VTankLootAction.Sell && _sellSkipLogged.Add(wo.Name))
                    _host.Log($"[RynthAi] AutoVendor: not selling '{wo.Name}': {sd.Reason}.");
                continue;
            }

            bool main = InMainPack(wo, player);
            int packIdx = main ? 0 : (packOrder.TryGetValue(wo.DirectContainerId, out int pi) ? pi : 99);
            string packName = main ? "Main Pack" : (_cache[wo.DirectContainerId]?.Name ?? "Pack");
            result.Add(new SellItem
            {
                Id = id,
                Name = wo.Name,
                Class = wo.ObjectClass,
                ItemType = type,
                StackSize = stack,
                Value = stackValue,
                RuleName = sd.RuleName,
                Location = main ? "Main Pack" : $"{packName} #{packIdx}",
                SortSlot = main ? Math.Max(0, wo.DirectSlot) : 1000 + 100 * packIdx + Math.Max(0, wo.DirectSlot),
                MaxStack = InventoryManager.GetMaxStackSize(wo),
                Wcid = _host.HasGetObjectWcid && _host.TryGetObjectWcid(id, out uint wcid) ? wcid : 0,
            });
        }
        return result;
    }

    // ── Test mode (UB DoTestMode) ──

    private void DoTestMode()
    {
        Chat("AutoVendor TEST MODE");
        if (!TryGetVendor(out VendorView v, out _))
        {
            Chat("  (no vendor open)");
            return;
        }
        var inv = Inventory();
        var ctx = NewContext();

        Chat("Buy Items:");
        var wants = CollectWants(v, inv, ctx);
        foreach (var w in wants)
            Chat($"  {AutoVendorPlanner.CategoryName(w.Item.ItemType)} -> {w.Item.Name} * {(w.Amount == int.MaxValue ? "max" : w.Amount.ToString("n0", CultureInfo.InvariantCulture))} - {w.RuleName}");
        if (wants.Count == 0) Chat("  (Nothing)");

        Chat("Sell Items:");
        var held = new List<string>();
        var sells = CollectSells(v, inv, ctx, held).OrderBy(s => s.SortSlot).ToList();
        foreach (var s in sells)
            Chat($"  {s.Location}: {s.Name}{(s.StackSize > 1 ? $" x{s.StackSize}" : string.Empty)} - {s.RuleName}");
        if (sells.Count == 0) Chat("  (Nothing)");

        if (held.Count > 0)
        {
            Chat("Held back (never sold):");
            foreach (string h in held.Take(10)) Chat($"  {h}");
            if (held.Count > 10) Chat($"  ...and {held.Count - 10} more");
        }
        Chat("TestMode is on: nothing was bought or sold (/ub opt set AutoVendor.TestMode false).");
    }

    // ── /ub vendor open attempts ──

    private void TickOpener(long now)
    {
        if (!_opening || now < _openNextAt) return;
        int tries = Math.Clamp(_settings.AutoVendorTries, 1, 20);
        if (_openAttempts <= tries)
        {
            if (_openAttempts > 1)
                _host.Log("[RynthAi] AutoVendor: vendor open timed out, trying again");
            _openAttempts++;
            _openNextAt = now + Math.Clamp(_settings.AutoVendorTriesTime, 500, 30_000);
            if (_host.HasUseObject) _host.UseFor(_openTarget, "Vendor", "AutoVendor: open the vendor", UseKind.Asked);
            _openHoldUntil = now + 500 + Math.Clamp(_settings.AutoVendorTriesTime, 500, 30_000);
            return;
        }
        CancelUse();
        _opening = false;
        _openHoldUntil = 0;
        ThinkOrWrite("AutoVendor failed to open vendor");
    }

    /// <summary>UB cancels a pending use by facing one degree away.</summary>
    private void CancelUse()
    {
        if (_host.HasTurnToHeading && PlayerCompass.TryGet(_host, out float h))
            _host.TurnToHeadingBy("Vendor", (h + 359f) % 360f);
        else if (_host.HasStopCompletely)
            _host.StopCompletelyBy("Vendor");
    }

    private WorldObject? FindVendor(string target, bool partial)
    {
        string t = target.Trim();
        if (t.Length > 0)
        {
            if (uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec) && IsVendor(dec, out var byDec))
                return byDec;
            string hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t.Substring(2) : t;
            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hx) && IsVendor(hx, out var byHex))
                return byHex;
            if (t.Equals("selected", StringComparison.OrdinalIgnoreCase) && _host.HasGetSelectedItemId
                && IsVendor(_host.GetSelectedItemId(), out var bySel))
                return bySel;
        }

        int player = unchecked((int)PlayerId());
        string lower = t.ToLowerInvariant();
        WorldObject? best = null;
        double bestDist = double.MaxValue;
        foreach (var wo in _cache.GetLandscapeObjects())
        {
            if (wo.ObjectClass != AcObjectClass.Vendor || wo.Id == player) continue;
            string n = (wo.Name ?? string.Empty).ToLowerInvariant();
            bool match = lower.Length == 0 || (partial ? n.Contains(lower) : n == lower);
            if (!match) continue;
            double d = _cache.Distance(player, wo.Id);
            if (best == null || d < bestDist) { best = wo; bestDist = d; }
        }
        return best;
    }

    private bool IsVendor(uint id, out WorldObject? wo)
    {
        wo = id != 0 ? _cache[unchecked((int)id)] : null;
        return wo != null && wo.ObjectClass == AcObjectClass.Vendor;
    }

    // ── /ub vendor addsell ──

    private void TickAddSell(long now, int busyCount)
    {
        var job = _addSell;
        if (job == null) return;

        if (job.Stage == 0)
        {
            bool allIded = Inventory().Where(w => NameMatches(w.Name, job.Name, job.Partial))
                                      .All(w => Appraised(unchecked((uint)w.Id)));
            if (allIded || now > job.Deadline) job.Stage = 1;
            return;
        }

        if (job.Stage == 1)
        {
            if (busyCount != 0 && now < job.Deadline + 5_000) return;
            var inv = Inventory();
            uint player = PlayerId();
            var matches = new List<SellItem>();
            foreach (var wo in inv)
            {
                if (!NameMatches(wo.Name, job.Name, job.Partial)) continue;
                SafetyFacts safety = ReadSafety(wo, player);
                if (AutoVendorPlanner.UnsafeReason(safety, _settings.AutoVendorOnlyFromMainPack) != null) continue;
                matches.Add(new SellItem { Id = unchecked((uint)wo.Id), Name = wo.Name, Class = wo.ObjectClass, StackSize = StackOf(wo), Value = safety.Value });
            }
            if (matches.Count == 0)
            {
                Error($"addsell: Unable to find item {(job.Partial ? "partially " : string.Empty)}named '{job.Name}' in inventory");
                _addSell = null;
                return;
            }

            var (whole, missing, splitFrom) = AutoVendorPlanner.PlanAddSell(matches, job.Count);
            foreach (var w in whole) _cart.AddSell(w.Id);
            job.FirstName = matches[0].Name;
            if (missing == 0)
            {
                Chat($"Added item to sell list: {job.FirstName} * {job.Count}");
                _addSell = null;
                return;
            }
            if (splitFrom == null || !_host.HasSplitStackInternal || player == 0)
            {
                Chat($"Added item to sell list: {job.FirstName} * {job.Count}, but was missing {missing} items");
                _addSell = null;
                return;
            }
            job.Before = new HashSet<uint>(inv.Select(w => unchecked((uint)w.Id)));
            job.SplitName = splitFrom.Name;
            job.Missing = missing;
            if (!_host.SplitStackInternal(splitFrom.Id, player, 0, missing))
            {
                Chat($"Added item to sell list: {job.FirstName} * {job.Count - missing}, but was missing {missing} items");
                _addSell = null;
                return;
            }
            job.Deadline = now + SplitWaitMs;
            job.Stage = 2;
            return;
        }

        // Stage 2: wait for the split-off stack, then add it.
        uint found = FindNewStack(Inventory(), job.Before, job.SplitName, job.Missing);
        if (found != 0)
        {
            _cart.AddSell(found);
            Chat($"Added item to sell list: {job.FirstName} * {job.Count}");
            _addSell = null;
        }
        else if (now > job.Deadline)
        {
            Chat($"Added item to sell list: {job.FirstName} * {job.Count - job.Missing}, but was missing {job.Missing} items");
            _addSell = null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Reads
    // ════════════════════════════════════════════════════════════════════════

    private bool TryGetVendor(out VendorView v, out VendorInfo vi)
    {
        v = null!;
        vi = null!;
        if (!_host.HasVendorTrade || !_host.TryGetVendorInfo(out vi) || vi.VendorId == 0)
            return false;
        v = new VendorView
        {
            Id = vi.VendorId,
            Name = string.IsNullOrEmpty(vi.Name) ? VendorName(vi.VendorId) : vi.Name,
            BuyRate = vi.BuyRate,
            SellRate = vi.SellRate,
            ItemTypes = vi.ItemTypes,
            MaxValue = vi.MaxValue,
            UsesAltCurrency = vi.UsesAltCurrency,
        };
        return true;
    }

    private List<Listing> Listings(VendorView v)
    {
        var list = new List<Listing>();
        foreach (VendorItem it in _host.GetVendorItems())
        {
            if (it.ObjectId == 0) continue;
            if (!it.Unlimited && it.Amount <= 0) continue;
            AcObjectClass cls = WorldObjectCache.ClassifyByItemType(it.ItemType);
            list.Add(new Listing
            {
                Id = it.ObjectId,
                Name = it.Name,
                Class = cls,
                ItemType = it.ItemType,
                Amount = it.Unlimited ? -1 : it.Amount,
                StackSize = Math.Max(1, it.StackSize),
                MaxStackSize = it.Stackable ? Math.Max(1, it.MaxStackSize) : 1,
                Value = it.Value,
                UnitPrice = it.UnitPrice,
                Burden = it.Burden,
                NeedsContainerSlot = it.NeedsContainerSlot,
            });
        }
        return list;
    }

    private PackState ReadPack(VendorView v, VendorInfo vi, List<WorldObject> inv)
    {
        uint player = PlayerId();
        long funds;
        if (v.UsesAltCurrency)
        {
            funds = vi.AltCurrencyHave >= 0 ? vi.AltCurrencyHave
                  : vi.AltCurrencyServerCount >= 0 ? vi.AltCurrencyServerCount : 0;
        }
        else
        {
            funds = vi.PlayerCoins >= 0 ? vi.PlayerCoins : CountPyreals(inv);
        }

        int mainCap = ReadPlayerInt(player, 6, 102);
        int mainCount = _host.HasGetNumContainedItems ? _host.GetNumContainedItems(player) : -1;
        if (mainCount < 0)
            mainCount = inv.Count(w => InMainPack(w, player) && !IsEquipped(w)
                                       && w.ObjectClass is not (AcObjectClass.Container or AcObjectClass.Foci));
        int contCap = ReadPlayerInt(player, 7, 7);
        int contCount = _host.HasGetNumContainedContainers ? _host.GetNumContainedContainers(player) : -1;
        if (contCount < 0)
            contCount = inv.Count(w => InMainPack(w, player) && w.ObjectClass is AcObjectClass.Container or AcObjectClass.Foci);

        return new PackState
        {
            Funds = Math.Max(0, funds),
            FreeMainSlots = Math.Max(0, mainCap - mainCount),
            FreeContainerSlots = Math.Max(0, contCap - contCount),
            PyrealStackSize = PyrealStack(inv),
        };
    }

    private int ReadPlayerInt(uint player, uint stype, int fallback)
    {
        if (player != 0 && _host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(player, stype, out int v) && v > 0)
            return v;
        return fallback;
    }

    private int PyrealStack(List<WorldObject> inv)
    {
        if (_pyrealStackSize > 0) return _pyrealStackSize;
        foreach (var wo in inv)
        {
            if (!IsPyreal(wo)) continue;
            int max = wo.Values(LongValueKey.MaxStackSize, 0);
            if (max > 0) { _pyrealStackSize = max; return max; }
        }
        return AutoVendorPlanner.DefaultPyrealStackSize;
    }

    private long CountPyreals(List<WorldObject> inv)
    {
        long total = 0;
        foreach (var wo in inv)
            if (IsPyreal(wo)) total += StackOf(wo);
        return total;
    }

    private bool IsPyreal(WorldObject wo)
        => _host.HasGetObjectWcid && _host.TryGetObjectWcid(unchecked((uint)wo.Id), out uint wcid) && wcid == AutoVendorPlanner.PyrealWcid;

    private SafetyFacts ReadSafety(WorldObject wo, uint player)
    {
        uint id = unchecked((uint)wo.Id);
        bool appraised = Appraised(id);
        bool? canBeSold = _host.HasGetObjectBoolProperty && _host.TryGetObjectBoolProperty(id, 69 /* IsSellable */, out bool sellable)
            ? sellable : null;
        bool retained = false;
        if (_host.HasGetObjectBitfield && _host.TryGetObjectBitfield(id, out uint bf) && (bf & 0x01000000u) != 0)
            retained = true;   // BF_RETAINED
        if (!retained && _host.HasGetObjectBoolProperty && _host.TryGetObjectBoolProperty(id, 91 /* Retained */, out bool ret) && ret)
            retained = true;
        bool rare = wo.Values(17 /* RareId */, 0) > 0;
        if (!rare && _host.HasGetObjectDataIdProperty && _host.TryGetObjectDataIdProperty(id, 52 /* IconUnderlay */, out uint underlay)
            && (underlay & 0x00FFFFFFu) == 23308u)
            rare = true;       // UB: IconUnderlay 23308 = rare

        return new SafetyFacts
        {
            Appraised = appraised,
            Class = wo.ObjectClass,
            Value = wo.Values(19 /* Value */, 0),
            CanBeSold = canBeSold,
            IsRare = rare,
            InMainPack = InMainPack(wo, player),
            Equipped = IsEquipped(wo),
            Attuned = wo.Values(114 /* Attuned */, 0),
            Bonded = wo.Values(33 /* Bonded */, 0),
            Retained = retained,
            TimesTinkered = wo.Values(171 /* NumTimesTinkered */, 0),
            Imbued = wo.Values(179 /* ImbuedEffect */, 0),
            Inscription = wo.Values((StringValueKey)7 /* Inscription */, string.Empty),
            HoldsItems = wo.Values(LongValueKey.ItemsCapacity, 0) > 0,
        };
    }

    private ItemFacts InventoryFacts(WorldObject wo, bool appraised) => new()
    {
        IsVendorListing = false,
        Appraised = appraised,
        ObjectClass = wo.ObjectClass,
        HasSpellData = appraised && _host.HasGetObjectSpellIds,
        HasPalettes = _host.HasGetObjectPalettes,
        HasCharacterSkills = _host.HasGetObjectSkill,
        HasCharacterLevel = _host.HasGetObjectIntProperty,
    };

    private ItemFacts ListingFacts(Listing l) => new()
    {
        IsVendorListing = true,
        Appraised = false,
        ObjectClass = l.Class,
        HasCharacterSkills = _host.HasGetObjectSkill,
        HasCharacterLevel = _host.HasGetObjectIntProperty,
    };

    /// <summary>The inventory item as the evaluator sees it: live reads, with the name we already have.</summary>
    private WorldObject EvalObject(WorldObject wo)
    {
        var overlay = new ItemPropertyOverlay { LiveFallback = true };
        overlay.Strings[(uint)StringValueKey.Name] = wo.Name;
        return new WorldObject(wo.Id, wo.Name, wo.ObjectClass) { Cache = _cache, Overlay = overlay };
    }

    /// <summary>A vendor listing as the evaluator sees it: only what the vendor snapshot holds.</summary>
    private static WorldObject ListingObject(Listing l)
    {
        var overlay = new ItemPropertyOverlay { LiveFallback = false };
        overlay.Ints[1] = unchecked((int)l.ItemType);
        overlay.Ints[5] = l.Burden;
        overlay.Ints[11] = l.MaxStackSize;
        overlay.Ints[12] = l.StackSize;
        overlay.Ints[19] = l.Value;
        overlay.Strings[(uint)StringValueKey.Name] = l.Name;
        return new WorldObject(unchecked((int)l.Id), l.Name, l.Class) { Overlay = overlay };
    }

    private static Func<VTankLootCondition, Tri> Judge(WorldObject evalObj, ItemFacts facts, VTankLootContext ctx)
        => c => AutoVendorRules.JudgeCondition(c, facts, cc => VTankLootEvaluator.MatchCondition(cc, evalObj, ctx));

    private VTankLootContext NewContext() => new(_host, PlayerId()) { Cache = _cache };

    private List<WorldObject> Inventory()
    {
        try { return _cache.GetDirectInventory(forceRefresh: true).Where(w => w != null && w.Id != 0).ToList(); }
        catch { return new List<WorldObject>(); }
    }

    private static int StackOf(WorldObject wo)
    {
        int s = wo.Values(LongValueKey.StackCount, 0);
        return s > 0 ? s : 1;
    }

    /// <summary>UB GetItemCountInInventoryByName: exact name, stack sizes summed.</summary>
    private static int CountByName(List<WorldObject> inv, string name)
    {
        int count = 0;
        foreach (var wo in inv)
            if (string.Equals(wo.Name, name, StringComparison.Ordinal))
                count += StackOf(wo);
        return count;
    }

    private static int CountByClass(List<WorldObject> inv, AcObjectClass cls)
    {
        int count = 0;
        foreach (var wo in inv)
            if (wo.ObjectClass == cls)
                count += StackOf(wo);
        return count;
    }

    /// <summary>
    /// Per item (weenie, else name): the Value / StackSize that the most units carry. A stack's Value goes
    /// stale after a split (too high) or a merge (too low); the untouched stacks outnumber them.
    /// </summary>
    private Dictionary<string, int> TrueUnitValues(List<WorldObject> inv)
    {
        var votes = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        foreach (var wo in inv)
        {
            int stack = StackOf(wo);
            int value = wo.Values(19 /* Value */, 0);
            if (stack < 1 || value <= 0 || InventoryManager.GetMaxStackSize(wo) < 2) continue;
            string key = StackKey(wo);
            if (!votes.TryGetValue(key, out var byUnit)) votes[key] = byUnit = new Dictionary<int, long>();
            int unit = value / stack;
            byUnit[unit] = byUnit.GetValueOrDefault(unit) + stack;
        }
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, byUnit) in votes)
            result[key] = AutoVendorPlanner.MostCarriedUnit(byUnit);
        return result;
    }

    private string StackKey(WorldObject wo) =>
        _host.HasGetObjectWcid && _host.TryGetObjectWcid(unchecked((uint)wo.Id), out uint wcid) && wcid != 0
            ? "w:" + wcid
            : "n:" + wo.Name;

    private uint FindNewStack(List<WorldObject> inv, HashSet<uint> before, string name, int amount)
    {
        foreach (var wo in inv)
        {
            uint id = unchecked((uint)wo.Id);
            if (before.Contains(id)) continue;
            if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase) && StackOf(wo) == amount)
                return id;
        }
        return 0;
    }

    private static Dictionary<int, int> PackOrder(List<WorldObject> inv, uint player)
    {
        var order = new Dictionary<int, int>();
        int n = 0;
        foreach (var wo in inv.Where(w => w.DirectContainerId == unchecked((int)player)
                                          && w.ObjectClass is AcObjectClass.Container or AcObjectClass.Foci)
                              .OrderBy(w => w.DirectSlot))
            order[wo.Id] = ++n;
        return order;
    }

    private static bool InMainPack(WorldObject wo, uint player)
    {
        int p = unchecked((int)player);
        int container = wo.DirectContainerId != 0 ? wo.DirectContainerId : wo.Container;
        return player != 0 && container == p;
    }

    private static bool IsEquipped(WorldObject wo)
        => wo.WieldedLocation > 0 || wo.Values(LongValueKey.EquippedSlots, 0) > 0;

    private uint ItemType(WorldObject wo)
        => _host.TryGetItemType(unchecked((uint)wo.Id), out uint t) ? t : 0u;

    private bool Appraised(uint id) => _host.HasHasAppraisalData && _host.HasAppraisalData(id);

    private static bool NameMatches(string itemName, string want, bool partial)
    {
        string n = (itemName ?? string.Empty).ToLowerInvariant();
        string w = (want ?? string.Empty).ToLowerInvariant();
        return partial ? n.Contains(w) : n == w;
    }

    private uint PlayerId()
    {
        if (_playerId == 0) _playerId = _host.GetPlayerId();
        return _playerId;
    }

    private string VendorName(uint id)
    {
        if (id != 0 && _host.HasVendorTrade && _host.TryGetVendorInfo(out VendorInfo vi) && vi.VendorId == id && !string.IsNullOrEmpty(vi.Name))
            return vi.Name;
        return _host.TryGetObjectName(id, out string n) && !string.IsNullOrWhiteSpace(n) ? n : $"0x{id:X8}";
    }

    private string TradeMessage()
        => _host.TryGetVendorTradeStatus(out VendorTradeStatus st) && !string.IsNullOrEmpty(st.Message) ? st.Message : "no reason given";

    // ── Profiles (UB GetProfilePath) ──

    /// <summary>
    /// First existing file of: character folder, server folder, main folder (the named
    /// profile), then default.utl in the same three; else the main-folder path (missing).
    /// A name typed on /ub autovendor also tries name + ".utl".
    /// </summary>
    public string ResolveProfilePath(string profileName, bool addUtlGuess)
    {
        var dirs = ProfileDirs();
        var names = new List<string> { profileName };
        if (addUtlGuess && string.IsNullOrEmpty(Path.GetExtension(profileName)))
            names.Add(profileName + ".utl");

        foreach (string dir in dirs)
            foreach (string n in names)
            {
                string p = SafeCombine(dir, n);
                if (p.Length > 0 && File.Exists(p)) return p;
            }
        foreach (string dir in dirs)
        {
            string p = SafeCombine(dir, "default.utl");
            if (p.Length > 0 && File.Exists(p)) return p;
        }
        string fallback = SafeCombine(MainProfileDir, profileName);
        return fallback.Length > 0 ? fallback : Path.Combine(MainProfileDir, "default.utl");
    }

    /// <summary>Character, server, main - UB's order.</summary>
    public List<string> ProfileDirs()
    {
        var dirs = new List<string>();
        string charFolder = _charFolder() ?? string.Empty;
        if (!string.IsNullOrEmpty(charFolder))
            dirs.Add(Path.Combine(charFolder, ProfileSubfolder));
        if (_host.HasGetWorldName && _host.TryGetWorldName(out string world) && !string.IsNullOrWhiteSpace(world))
            dirs.Add(Path.Combine(MainProfileDir, Sanitize(world)));
        dirs.Add(MainProfileDir);
        return dirs;
    }

    /// <summary>Create the per-character and per-server folders so players can see where profiles go (UB Init).</summary>
    public void EnsureProfileFolders()
    {
        foreach (string dir in ProfileDirs())
            try { Directory.CreateDirectory(dir); } catch { }
    }

    private static string SafeCombine(string dir, string name)
    {
        try { return Path.Combine(dir, name); } catch { return string.Empty; }
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Trim();
    }

    // ── Chat ──

    private void Chat(string msg) => _host.WriteToChat($"[RynthAi] {msg}", 1);
    private void Tool(string msg) => _host.WriteToChat($"[RynthAi] AutoVendor: {msg}", 1);
    private void Error(string msg) => _host.WriteToChat($"[RynthAi] AutoVendor: {msg}", 2);

    /// <summary>The plugin's command replies (CommandReply.cs): a /ub vendor command answers by them.</summary>
    internal CommandReply? Reply { get; set; }

    /// <summary>A /ub vendor or /ub autovendor command worked: said when typed, only logged when a meta sent it.</summary>
    private void Said(string msg)
    {
        if (Reply is { } r) r.Ok($"[RynthAi] AutoVendor: {msg}");
        else Tool(msg);
    }

    /// <summary>UB Util.ThinkOrWrite: a /tell to yourself when Think is on (metas watch for it).</summary>
    private void ThinkOrWrite(string msg)
    {
        uint player = PlayerId();
        if (_settings.AutoVendorThink && _host.HasInvokeChatParser && player != 0
            && _host.TryGetObjectName(player, out string me) && !string.IsNullOrWhiteSpace(me))
        {
            _host.InvokeChatParser($"/tell {me}, {msg}");
            return;
        }
        Chat(msg);
    }

    private void SayNeedsUpdate()
        => Error($"needs a RynthCore update: vendor trading is plugin API v67, this engine is v{_host.Version}.");

    private void UpdateStatus()
    {
        string s;
        if (_opening)
            s = $"Opening {_openTargetName} (attempt {Math.Max(1, _openAttempts - 1)}/{Math.Clamp(_settings.AutoVendorTries, 1, 20)})";
        else
            s = _phase switch
            {
                Phase.Idle => _host.HasVendorTrade ? "Idle" : $"Needs RynthCore update (engine API v{_host.Version}, needs v67)",
                Phase.Identifying => $"Identifying {_idQueue.Count} item(s) - {_vendorName}",
                Phase.WaitTrade => $"Waiting for the server - {_vendorName}",
                Phase.WaitConfirm => $"Confirming trade - {_vendorName}",
                Phase.WaitSplit => $"Splitting {_splitName} - {_vendorName}",
                Phase.WaitMerge => $"Merging {_splitName} - {_vendorName}",
                _ => $"Vendoring {_vendorName} ({Path.GetFileName(_profilePath)})",
            };
        if (_settings.AutoVendorTestMode && _phase != Phase.Idle) s += " [test mode]";
        _status = s;
    }

    private static long Now() => Environment.TickCount64;
}
