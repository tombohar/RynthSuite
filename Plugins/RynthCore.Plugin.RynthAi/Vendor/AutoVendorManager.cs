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
    public static readonly string DefaultMainProfileDir = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "AutoVendor");
    /// <summary>RynthAi\AutoVendor: the main folder, with one folder per server in it. The host tests point it at their scratch folder.</summary>
    public static string MainProfileDir => MainProfileDirOverride ?? DefaultMainProfileDir;
    internal static string? MainProfileDirOverride;
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

    private string _splitName = string.Empty;   // the item being split or merged (status line)
    private long _splitDeadline;                 // merges

    // ── Splits (2026-11-01) ──
    // A split is one PendingSplit until its result is seen: an object the server CREATED after the
    // send (OnCreateObject), else a stack of the same item that grew by the amount (the split went
    // onto the stack at the target slot). It outlives the session, so a vendor closed mid-split
    // still learns where the units went, and the next open picks up from there instead of
    // splitting again. Before, the wait ended at the first new 1-count stack in a polled list and
    // the next plan split again at once; that second split went onto the first one's stack, and
    // "made no new stack" (Master Raiden Stormcaller, 2026-11-01).
    private sealed class PendingSplit
    {
        public uint SourceId;
        public int SourceBefore;
        public int Amount;
        public string Name = string.Empty;
        public uint Wcid;
        public string RuleName = string.Empty;
        public HashSet<uint> Before = new();
        public Dictionary<uint, int> Siblings = new();   // same-item stacks and their counts at the send
        public long SentAt;
        public long Deadline;
        public long NextPollAt;
    }
    private volatile PendingSplit? _pendingSplit;
    private readonly ConcurrentQueue<uint> _created = new();
    private readonly HashSet<uint> _createdSinceSplit = new();
    // Stacks a split made to be sold, and the rule that sold the stack they came from: sold even when
    // a rule matches them differently at their new size ("Value = 250,000" on a one-note stack).
    private readonly Dictionary<uint, string> _splitOffForSale = new();
    // Identifies AutoVendor asks for outside the session's ID pass (a split-off stack, a loose note).
    private readonly Dictionary<uint, (int Attempts, long LastAt)> _extraIds = new();
    private readonly HashSet<uint> _waitIdLogged = new();
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

    /// <summary>The server created an object (any thread). Only kept while a split waits for its stack.</summary>
    public void OnCreateObject(uint id)
    {
        if (id != 0 && _pendingSplit != null && _created.Count < 4096)
            _created.Enqueue(id);
    }

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
        TickPendingSplit(now);   // also between sessions: a split sent before the vendor closed

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
        _pendingSplit = null;
        while (_created.TryDequeue(out _)) { }
        _createdSinceSplit.Clear();
        _splitOffForSale.Clear();
        _extraIds.Clear();
        _waitIdLogged.Clear();
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
            // Every folder it looked in: the main folder's path alone read as "only the main
            // folder is read", with the character and server folders beside it (2026-11-04).
            Error($"No vendor profile exists: no {Path.GetFileName(path)} or default.utl in {string.Join(", ", ProfileDirs())}");
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
        // The vendor's own profile (or one named on /ub autovendor) is the player's word for this
        // vendor; default.utl is not (see SellsForNothing).
        _profileIsVendors = fromArg || !string.Equals(Path.GetFileName(path), "default.utl", StringComparison.OrdinalIgnoreCase);
        _failures = 0;
        _sessionSkip.Clear();
        _sellSkipLogged.Clear();
        _waitIdLogged.Clear();
        _bailAt = Now();
        _nextActionAt = 0;
        _host.Log($"[RynthAi] AutoVendor: {_vendorName} [0x{merchantId:X8}] profile '{path}' ({_profile.Rules.Count} rules)");
        if (_settings.AutoVendorEnableSelling && TryGetVendor(out VendorView zv, out _) && zv.Id == merchantId && SellsForNothing(zv))
        {
            string why = $"{_vendorName} pays nothing for items (buy rate 0): nothing is sold to it with default.utl. " +
                         $"To hand it items anyway (a storage vault), give it its own profile, {_vendorName}.utl.";
            Tool(why);
            _host.Log($"[RynthAi] AutoVendor: {why}");
        }

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
        if (!TryGetVendor(out VendorView v, out _) || SellsForNothing(v))
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

        // A split from earlier (this session or one the vendor closed on) still has no result: wait for
        // it rather than plan a second split onto the first one's stack.
        if (_pendingSplit != null)
        {
            _phase = Phase.WaitSplit;
            return;
        }

        var inv = Inventory();
        var ctx = NewContext();
        List<BuyWant> wants = CollectWants(v, inv, ctx);
        var spareNotes = new List<SellItem>();
        var needId = new List<SellItem>();
        List<SellItem> sells = AutoVendorPlanner.SortSells(v, CollectSells(v, inv, ctx, null, spareNotes, needId));
        PackState pack = ReadPack(v, vi, inv);
        VendorPlan plan = AutoVendorPlanner.PlanNext(v, wants, sells, pack, spareNotes);

        // About to split or merge while a loose stack of the same item (a split's result, a note split
        // off by hand) is still being identified: it may be the very stack the plan needs, so wait.
        // The same before giving up on a buy: the stack may be what pays for it.
        bool tidy = plan.Kind is PlanKind.Split or PlanKind.Merge && plan.SplitItem != null;
        if (tidy || (plan.Kind == PlanKind.Done && wants.Count > 0))
        {
            foreach (SellItem w in needId)
            {
                if ((tidy && !SameItem(w, plan.SplitItem!)) || !AwaitId(w.Id, now)) continue;
                if (_waitIdLogged.Add(w.Id))
                    _host.Log($"[RynthAi] AutoVendor: waiting for the ID of {w.Name} 0x{w.Id:X8} (x{w.StackSize}) before {(tidy ? $"another split or merge of {plan.SplitItem!.Name}" : "ending the session")}");
                _nextActionAt = now + 500;
                _bailAt = now;
                return;
            }
        }

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
                if (!string.IsNullOrEmpty(plan.Message))
                    SayWhy(plan.Message);
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
        // One split at a time: a second one sent before the first's result is seen lands on the first's
        // new stack (the split targets slot 0 of the main pack and joins a same-item stack there).
        if (_pendingSplit is { } busy)
        {
            _host.Log($"[RynthAi] AutoVendor: not splitting {item.Name} yet: the split of {busy.Amount} off {busy.Name} sent {now - busy.SentAt} ms ago has no result yet");
            _phase = Phase.WaitSplit;
            return;
        }
        uint player = PlayerId();
        var ps = new PendingSplit
        {
            SourceId = item.Id,
            SourceBefore = item.StackSize,
            Amount = amount,
            Name = item.Name,
            Wcid = item.Wcid,
            RuleName = item.RuleName,
            Before = new HashSet<uint>(inv.Select(w => unchecked((uint)w.Id))),
            SentAt = now,
            Deadline = now + SplitWaitMs,
            NextPollAt = now + PollMs,
        };
        foreach (var wo in inv)
            if (SameItem(wo, item.Name, item.Wcid))
                ps.Siblings[unchecked((uint)wo.Id)] = StackOf(wo);
        _splitName = item.Name;
        _host.Log($"[RynthAi] AutoVendor DoSplit {item.Name}:{item.Id:X8} old: {item.StackSize} new: {amount} ({why})");
        while (_created.TryDequeue(out _)) { }
        _createdSinceSplit.Clear();
        _pendingSplit = ps;   // before the send: a create that arrives at once is kept
        if (player == 0 || !_host.SplitStackInternal(item.Id, player, 0, amount))
        {
            _pendingSplit = null;
            TradeFailed(isBuy: false, $"could not split {item.Name}", now);
            return;
        }
        Tool($"splitting {amount} off {item.Name} ({why})");
        _bailAt = now;
        _phase = Phase.WaitSplit;
    }

    private void TickWaitSplit(long now)
    {
        // TickPendingSplit (every tick, any phase) moves the session on when the split resolves.
        if (_pendingSplit == null)
            _phase = Phase.Ready;
    }

    /// <summary>
    /// Watches the split in flight: done when the server created a stack of the item after the send,
    /// or (no create) when a stack of the item that was there grew by the amount while the source
    /// shrank: the split went onto it. Failed when neither shows up in SplitWaitMs.
    /// </summary>
    private void TickPendingSplit(long now)
    {
        var ps = _pendingSplit;
        if (ps == null)
        {
            while (_created.TryDequeue(out _)) { }
            return;
        }
        while (_created.TryDequeue(out uint c))
            if (!ps.Before.Contains(c)) _createdSinceSplit.Add(c);
        if (now < ps.NextPollAt) return;
        ps.NextPollAt = now + PollMs;

        var byId = new Dictionary<uint, WorldObject>();
        foreach (var wo in Inventory())
            byId[unchecked((uint)wo.Id)] = wo;
        int sourceNow = byId.TryGetValue(ps.SourceId, out var src) ? StackOf(src) : 0;

        SplitOutcome outcome = AutoVendorSplits.Resolve(
            ps.Amount, ps.SourceBefore, sourceNow, ps.Siblings, _createdSinceSplit,
            byId.Where(kv => SameItem(kv.Value, ps.Name, ps.Wcid)).Select(kv => (kv.Key, StackOf(kv.Value), ps.Before.Contains(kv.Key))),
            now > ps.Deadline);

        switch (outcome.Kind)
        {
            case SplitOutcomeKind.Pending:
                return;

            case SplitOutcomeKind.NewStack:
            case SplitOutcomeKind.JoinedStack:
                _pendingSplit = null;
                _createdSinceSplit.Clear();
                // Only a stack the split made is sold past the rules; one it joined was already
                // there and keeps whatever the profile says about it.
                if (outcome.Kind == SplitOutcomeKind.NewStack && outcome.StackCount == ps.Amount)
                    _splitOffForSale[outcome.StackId] = ps.RuleName;
                _host.Log(outcome.Kind == SplitOutcomeKind.NewStack
                    ? $"[RynthAi] AutoVendor: split {ps.Amount} off {ps.Name}: new stack 0x{outcome.StackId:X8} (x{outcome.StackCount}) after {now - ps.SentAt} ms"
                    : $"[RynthAi] AutoVendor: split {ps.Amount} off {ps.Name}: no new object; the units went onto stack 0x{outcome.StackId:X8} (now x{outcome.StackCount}) at the split's slot");
                if (!Appraised(outcome.StackId)) AwaitId(outcome.StackId, now);
                if (_phase == Phase.WaitSplit)
                {
                    if (outcome.Kind == SplitOutcomeKind.NewStack) _tidyFails = 0;
                    _phase = Phase.Ready;
                    _nextActionAt = 0;
                }
                return;

            default:   // failed
                _pendingSplit = null;
                _createdSinceSplit.Clear();
                string what = sourceNow == ps.SourceBefore
                    ? $"splitting {ps.Amount} off {ps.Name} changed nothing (the server didn't split it; still x{sourceNow})"
                    : $"splitting {ps.Amount} off {ps.Name} made no stack we can find (source x{ps.SourceBefore} -> x{sourceNow})";
                if (_phase == Phase.WaitSplit)
                    TidyFailed(what);
                else
                    _host.Log($"[RynthAi] AutoVendor: {what}");
                return;
        }
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
    /// <param name="spareNotes">Filled with loose single notes that pass the safety checks but no Sell rule
    /// took (see AutoVendorPlanner.PlanNext): one of them is sold before another note is split off.</param>
    /// <param name="needId">Filled with split-off stacks and loose single notes that still need an ID
    /// before they can be sold.</param>
    private List<SellItem> CollectSells(VendorView v, List<WorldObject> inv, VTankLootContext ctx, List<string>? heldBack,
                                        List<SellItem>? spareNotes = null, List<SellItem>? needId = null)
    {
        var result = new List<SellItem>();
        if (!_settings.AutoVendorEnableSelling || _profile == null)
            return result;
        if (SellsForNothing(v))
        {
            heldBack?.Add($"everything - {_vendorName} pays nothing (buy rate 0) and the profile is default.utl");
            return result;
        }

        uint player = PlayerId();
        var packOrder = PackOrder(inv, player);
        var unitValues = TrueUnitValues(inv);

        // Split-off stacks that were sold or merged away are forgotten.
        if (_splitOffForSale.Count > 0)
        {
            var have = new HashSet<uint>(inv.Select(w => unchecked((uint)w.Id)));
            foreach (uint gone in _splitOffForSale.Keys.Where(k => !have.Contains(k)).ToList())
                _splitOffForSale.Remove(gone);
        }

        foreach (var wo in inv)
        {
            uint id = unchecked((uint)wo.Id);
            if (_sessionSkip.Contains(id)) continue;

            uint type = ItemType(wo);
            int stack = StackOf(wo);
            bool isNote = wo.ObjectClass == AcObjectClass.TradeNote || (type & AutoVendorPlanner.ItemTypePromissoryNote) != 0;
            bool splitOff = _splitOffForSale.TryGetValue(id, out string? splitRule);
            bool looseNote = isNote && stack <= 1;

            SafetyFacts safety = ReadSafety(wo, player);
            string? unsafeWhy = AutoVendorPlanner.UnsafeReason(safety, _settings.AutoVendorOnlyFromMainPack);
            if (!safety.Appraised && (splitOff || looseNote) && needId != null)
                needId.Add(new SellItem { Id = id, Name = wo.Name, Class = wo.ObjectClass, ItemType = type, StackSize = stack, Wcid = WcidOf(id) });
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

            SellItem Make(string ruleName)
            {
                bool main = InMainPack(wo, player);
                int packIdx = main ? 0 : (packOrder.TryGetValue(wo.DirectContainerId, out int pi) ? pi : 99);
                string packName = main ? "Main Pack" : (_cache[wo.DirectContainerId]?.Name ?? "Pack");
                return new SellItem
                {
                    Id = id,
                    Name = wo.Name,
                    Class = wo.ObjectClass,
                    ItemType = type,
                    StackSize = stack,
                    Value = stackValue,
                    RuleName = ruleName,
                    Location = main ? "Main Pack" : $"{packName} #{packIdx}",
                    SortSlot = main ? Math.Max(0, wo.DirectSlot) : 1000 + 100 * packIdx + Math.Max(0, wo.DirectSlot),
                    MaxStack = InventoryManager.GetMaxStackSize(wo),
                    Wcid = WcidOf(id),
                };
            }

            if (!sd.Sell)
            {
                // Split off to be sold, from a stack a Sell rule took: sold even if a rule reads the
                // smaller stack differently (a Keep rule for the vendor's "Value = 250,000" listing
                // also matches a one-note stack, 2026-11-01).
                if (splitOff)
                {
                    result.Add(Make(string.IsNullOrEmpty(splitRule) ? "split off to sell" : $"{splitRule} (split off to sell)"));
                    continue;
                }
                // A loose single note: kept unless one note has to pay for a buy (PlanNext).
                if (looseNote && spareNotes != null)
                    spareNotes.Add(Make(string.IsNullOrEmpty(sd.RuleName) ? "loose note" : $"loose note, rule '{sd.RuleName}' alone keeps it"));
                if (heldBack != null && verdict.Rule?.Action == VTankLootAction.Sell)
                    heldBack.Add($"{wo.Name} - {sd.Reason}");
                else if (heldBack == null && verdict.Rule?.Action == VTankLootAction.Sell && _sellSkipLogged.Add(wo.Name))
                    _host.Log($"[RynthAi] AutoVendor: not selling '{wo.Name}': {sd.Reason}.");
                continue;
            }

            result.Add(Make(sd.RuleName));
        }
        return result;
    }

    private uint WcidOf(uint id) => _host.HasGetObjectWcid && _host.TryGetObjectWcid(id, out uint wcid) ? wcid : 0;

    /// <summary>Same item for stacking: the weenie when both are known, else the exact name.</summary>
    private bool SameItem(WorldObject wo, string name, uint wcid)
    {
        uint w = WcidOf(unchecked((uint)wo.Id));
        return wcid != 0 && w != 0 ? w == wcid : string.Equals(wo.Name, name, StringComparison.Ordinal);
    }

    private static bool SameItem(SellItem a, SellItem b)
        => a.Wcid != 0 && b.Wcid != 0 ? a.Wcid == b.Wcid : string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    /// <summary>
    /// Asks for <paramref name="id"/>'s ID (outside the session's ID pass) and says whether it is worth
    /// waiting for: true while a request is out or another may be sent, false once it is identified
    /// or IdMaxAttempts went unanswered.
    /// </summary>
    private bool AwaitId(uint id, long now)
    {
        if (Appraised(id)) return false;
        _extraIds.TryGetValue(id, out var st);
        if (st.Attempts > 0 && now - st.LastAt < IdResendMs) return true;
        if (st.Attempts >= IdMaxAttempts) return false;
        if (!_host.HasRequestId || !_host.RequestId(id)) return false;
        _extraIds[id] = (st.Attempts + 1, now);
        return true;
    }

    // ── Test mode (UB DoTestMode) ──

    private void DoTestMode()
    {
        Chat("AutoVendor TEST MODE");
        if (!TryGetVendor(out VendorView v, out VendorInfo tvi))
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
        var spare = new List<SellItem>();
        var sells = CollectSells(v, inv, ctx, held, spare).OrderBy(s => s.SortSlot).ToList();
        foreach (var s in sells)
            Chat($"  {s.Location}: {s.Name}{(s.StackSize > 1 ? $" x{s.StackSize}" : string.Empty)} - {s.RuleName}");
        if (sells.Count == 0) Chat("  (Nothing)");
        if (spare.Count > 0)
        {
            Chat("Loose notes (one is sold when a note has to pay for a buy):");
            foreach (var s in spare.Take(10)) Chat($"  {s.Location}: {s.Name} - {s.RuleName}");
            if (spare.Count > 10) Chat($"  ...and {spare.Count - 10} more");
        }

        // What the next step would be, and why nothing would be bought.
        {
            PackState pack = ReadPack(v, tvi, inv);
            VendorPlan plan = AutoVendorPlanner.PlanNext(v, wants, AutoVendorPlanner.SortSells(v, sells), pack, spare);
            string step = plan.Kind switch
            {
                PlanKind.Buy => "buy " + string.Join(", ", plan.Buys.Select(b => $"{b.Amount:n0}x {b.Item.Name}")),
                PlanKind.Sell => "sell " + string.Join(", ", plan.Sells.Select(s => s.Name)),
                PlanKind.Split => $"split {plan.SplitAmount} off {plan.SplitItem?.Name}",
                PlanKind.Merge => $"merge {plan.SplitItem?.Name} stacks",
                PlanKind.Fatal => plan.Message,
                _ => string.IsNullOrEmpty(plan.Message) ? "done" : $"done - {plan.Message}",
            };
            Chat($"Next step: {step}");
        }

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
            if (wo.Id == player) continue;
            string n = (wo.Name ?? string.Empty).ToLowerInvariant();
            bool match = lower.Length == 0 || (partial ? n.Contains(lower) : n == lower);
            if (!match) continue;
            if (!IsVendorObject(wo)) continue;   // after the name test: the bitfield read costs more
            double d = _cache.Distance(player, wo.Id);
            if (best == null || d < bestDist) { best = wo; bestDist = d; }
        }
        return best;
    }

    private bool IsVendor(uint id, out WorldObject? wo)
    {
        wo = id != 0 ? _cache[unchecked((int)id)] : null;
        return wo != null && IsVendorObject(wo);
    }

    // The session's profile is the vendor's own (<vendor>.utl) or one named on /ub autovendor, not default.utl.
    private bool _profileIsVendors;

    /// <summary>
    /// A vendor that pays nothing for what it takes (buy rate 0), met with default.utl: nothing is
    /// sold to it. Since the engine trades with zero-rate vendors (a free storage vault), a
    /// default.utl's Sell rules, written to turn loot into pyreals, would otherwise hand every
    /// matching item to such a vendor for nothing (on stock ACE for 1 pyreal a stack). A vault the
    /// player means to fill gets its own profile, which sells (deposits) as written. Buying is not
    /// changed: a vendor that charges 0 already ends the session at its first buy ("No vendor price
    /// found").
    /// </summary>
    private bool SellsForNothing(VendorView v) => v.BuyRate == 0f && !_profileIsVendors;

    // PublicWeenieDesc._bitfield BF_VENDOR (retail's ObjectDescriptionFlag.Vendor, which ACE sets too).
    private const uint BfVendor = 0x00000200;

    /// <summary>
    /// A vendor by the server's own vendor flag. The world cache files a vendor by its type (a
    /// creature: Npc or Monster) and never as <see cref="AcObjectClass.Vendor"/>, so
    /// /ub vendor open by name, id or "selected" found no vendor at all. The cache class still
    /// counts should it ever be Vendor. An engine without the bitfield read: as before.
    /// </summary>
    private bool IsVendorObject(WorldObject wo)
    {
        if (wo.ObjectClass == AcObjectClass.Vendor) return true;
        try
        {
            return _host.HasGetObjectBitfield
                && _host.TryGetObjectBitfield(unchecked((uint)wo.Id), out uint bf)
                && (bf & BfVendor) != 0;
        }
        catch { return false; }
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
        bool rare = Loot.RareItems.IsRare(_host, wo, out _);   // RareId, or UB's IconUnderlay 23308

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
        ItemType = ItemType(wo),
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
        ItemType = l.ItemType,
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
    /// profile), then default.utl in the same three; else where a new one goes (missing):
    /// the server folder, or the main folder while the server's name isn't known. UB says
    /// the main folder there; ours made the dashboard's Edit save every new profile in the
    /// main folder, beside the server folders it had just made (2026-11-04).
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
        string home = ServerDir() ?? MainProfileDir;
        string fallback = SafeCombine(home, profileName);
        return fallback.Length > 0 ? fallback : Path.Combine(home, "default.utl");
    }

    /// <summary>Character, server, main - UB's order.</summary>
    public List<string> ProfileDirs()
    {
        var dirs = new List<string>();
        string charFolder = _charFolder() ?? string.Empty;
        if (!string.IsNullOrEmpty(charFolder))
            dirs.Add(Path.Combine(charFolder, ProfileSubfolder));
        string? server = ServerDir();
        if (server != null) dirs.Add(server);
        dirs.Add(MainProfileDir);
        return dirs;
    }

    /// <summary>AutoVendor\&lt;server&gt;, or null while the server's name isn't known.</summary>
    private string? ServerDir()
        => _host.HasGetWorldName && _host.TryGetWorldName(out string world) && !string.IsNullOrWhiteSpace(world)
            ? Path.Combine(MainProfileDir, Sanitize(world))
            : null;

    /// <summary>
    /// The profile pickers' list (the Loot Editor's): the .utl files in <paramref name="dirs"/>
    /// (ProfileDirs: character, server, main), in that order, then the other servers' folders,
    /// each marked by its folder: "AutoVendor (character): ", "AutoVendor (&lt;server&gt;): ",
    /// "AutoVendor: " for the main folder. The picker listed the main folder and its server
    /// folders as one tree and never the character folder (2026-11-04).
    /// </summary>
    public static List<(string Path, string Display)> ListProfiles(IReadOnlyList<string> dirs)
    {
        var list = new List<(string Path, string Display)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string main = FullPath(MainProfileDir);
        var folders = new List<string>();
        foreach (string d in dirs ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(d)) folders.Add(d);
        if (!folders.Exists(d => SameDir(d, main))) folders.Add(MainProfileDir);
        try
        {
            if (Directory.Exists(MainProfileDir))
            {
                var others = new List<string>(Directory.GetDirectories(MainProfileDir));
                others.Sort(StringComparer.OrdinalIgnoreCase);
                folders.AddRange(others);
            }
        }
        catch { }

        foreach (string dir in folders)
        {
            string full = FullPath(dir);
            if (full.Length == 0 || !seen.Add(full)) continue;
            string label = SameDir(full, main) ? "AutoVendor: "
                : SameDir(Path.GetDirectoryName(full) ?? string.Empty, main) ? $"AutoVendor ({Path.GetFileName(full)}): "
                : "AutoVendor (character): ";
            try
            {
                if (!Directory.Exists(full)) continue;
                var files = new List<string>(Directory.GetFiles(full, "*.utl"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files) list.Add((f, label + Path.GetFileName(f)));
            }
            catch { }
        }
        return list;
    }

    private static string FullPath(string dir)
    {
        try { return dir.Length == 0 ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { return string.Empty; }
    }

    private static bool SameDir(string a, string b)
        => string.Equals(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase);

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
        // In RynthCore.log too: a session's end (finished, Fatal) only reached chat, so a log showed
        // vendor opens with no buy, sell or split and no reason (2026-11-01).
        _host.Log($"[RynthAi] {msg}");
        uint player = PlayerId();
        if (_settings.AutoVendorThink && _host.HasInvokeChatParser && player != 0
            && _host.TryGetObjectName(player, out string me) && !string.IsNullOrWhiteSpace(me))
        {
            _host.InvokeChatParser($"/tell {me}, {msg}");
            return;
        }
        Chat(msg);
    }

    /// <summary>Why a session ends without buying what the profile wants: chat and RynthCore.log.</summary>
    private void SayWhy(string why)
    {
        _host.Log($"[RynthAi] AutoVendor: {why}");
        Error(why);
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

    /// <summary>The manager's clock in ms (tests move it by hand).</summary>
    internal static Func<long> Clock = () => Environment.TickCount64;

    private static long Now() => Clock();
}
