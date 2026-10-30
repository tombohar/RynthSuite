// Ported from UtilityBelt (https://gitlab.com/utilitybelt/utilitybelt.gitlab.io), MIT License,
// Copyright (c) the UtilityBelt contributors. Source: UtilityBelt/Tools/AutoTrade.cs (and the
// item-safety checks in UtilityBelt/Lib/Util.cs), ported from the public source. The MIT License
// text is in THIRD-PARTY-NOTICES.md at the root of this repository.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.Vendor;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>
/// UtilityBelt's AutoTrade on RynthCore's player-trade calls (plugin API v72).
///
/// When a trade opens with a player (and AutoTrade is enabled), or on /ub autotrade, it loads
/// a VTank .utl named for the partner (or default.utl), identifies the inventory items a Keep
/// or Keep # rule could want, and puts the matching items in the trade window:
///   Keep            every matching item
///   Keep # (# > 0)  up to # items, splitting a stack if needed
///   Keep # (# &lt; 0)  all but # items, splitting a stack if needed
/// With AutoAccept on it then accepts the trade once the server has confirmed every item.
/// Separately, when the partner accepts and their name matches a pattern on an auto-accept
/// list (character, server or global autoAcceptList.json, or the settings list), it accepts.
///
/// Threading: everything here runs on the plugin tick thread. Chat commands arrive on AC's
/// main thread and are queued with <see cref="Enqueue"/>. The engine's trade calls are safe
/// from any thread: reads are copies, actions are queued for AC's main thread.
///
/// Differences from UB, all on the side of caution: items are only added when a rule
/// definitely matches with the data we have (identified, and no condition RynthAi's evaluator
/// only approximates); items in RynthAi's weapon / consumable / monster-weapon lists are never
/// added; auto-accept after adding waits at most 10 s for the server to confirm the items and
/// does not accept if any is missing; every automatic add and accept is written to chat.
/// </summary>
internal sealed class AutoTradeManager
{
    public static readonly string MainProfileDir = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "AutoTrade");
    public const string ProfileSubfolder = "AutoTrade";
    public const string AutoAcceptListFileName = "autoAcceptList.json";
    public const uint RequiredApiVersion = 72;

    private const long BailMs = 10_000;         // UB bailTimer
    private const long AcceptWaitMs = 10_000;   // ours: server confirmations before auto-accept
    private const long SplitWaitMs = 10_000;
    private const int MaxAddsPerTick = 10;
    private const int IdMaxInFlight = 4;         // UB Assessor pacing (as AutoVendor)
    private const long IdSpacingMs = 75;
    private const long IdResendMs = 1_500;
    private const int IdMaxAttempts = 3;

    private static readonly Regex CommandPattern = new(
        @"^\s*autoaccept\s+(?<Verb>(?:add|remove|list)[gs]?)\s*(?<CharPattern>.*)|(?<LootProfile>.*\.utl)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _cache;
    private readonly Func<string> _charFolder;
    private readonly ConcurrentQueue<Action> _commands = new();
    private uint _playerId;
    private bool _needsUpdateSaid;

    // ── Trade tracking (UB traderId / traderName, from the engine's trade state) ──
    private bool _baselined;
    private bool _wasOpen;
    private uint _generation;
    private uint _partnerAccepts;
    private uint _failures;
    private uint _traderId;
    private string _traderName = string.Empty;
    private uint _sessionGeneration;

    // ── Session (UB running / doAccept / pendingAddItems / addedItems / keepUpToCounts) ──
    private enum Phase { Idle, Identifying, Adding, WaitSplit, WaitAccept }
    private Phase _phase = Phase.Idle;
    private VTankLootProfile? _profile;
    private string _profilePath = string.Empty;
    private long _bailAt;
    private long _acceptDeadline;
    private readonly List<uint> _sessionItems = new();          // UB itemsToId
    private readonly HashSet<uint> _pending = new();            // sent, not yet in the window
    private readonly HashSet<uint> _added = new();              // confirmed in the window
    private readonly HashSet<uint> _handled = new();            // decided (added, kept or split)
    private readonly Dictionary<string, int> _keepUpToCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _pendingNames = new();
    private HashSet<int> _keepIds = new();

    private readonly List<uint> _idQueue = new();
    private readonly Dictionary<uint, (int Attempts, long LastAt)> _idState = new();
    private long _lastIdSentAt;
    private long _nextPollAt;

    private string _splitName = string.Empty;
    private string _splitRule = string.Empty;
    private int _splitAmount;
    private HashSet<uint> _splitBefore = new();
    private long _splitDeadline;

    private volatile string _status = "Idle";

    public AutoTradeManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache cache, uint playerId, Func<string> charFolder)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
        _playerId = playerId;
        _charFolder = charFolder;
        EnsureProfileFolders();
    }

    /// <summary>A session is running: nav, looting and inventory moves wait (UB's VTank locks).</summary>
    public bool HoldsBot => _phase != Phase.Idle;

    /// <summary>One line for the settings UI (any thread).</summary>
    public string Status => _status;

    public bool TradeApiAvailable => _host.HasTrade;

    /// <summary>Queue work from another thread (chat commands); runs on the next Tick.</summary>
    public void Enqueue(Action action)
    {
        if (action != null) _commands.Enqueue(action);
    }

    public void SetPlayerId(uint id) { if (id != 0) _playerId = id; }

    // ════════════════════════════════════════════════════════════════════════
    //  Tick
    // ════════════════════════════════════════════════════════════════════════

    public void Tick(int busyCount)
    {
        while (_commands.TryDequeue(out Action? cmd))
        {
            try { cmd(); }
            catch (Exception ex) { _host.Log($"[RynthAi] AutoTrade command failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        if (!_host.HasTrade)
        {
            if (_settings.AutoTradeEnabled && !_needsUpdateSaid)
            {
                _needsUpdateSaid = true;
                SayNeedsUpdate();
            }
            UpdateStatus();
            return;
        }

        long now = Now();
        PollTrade(now);

        if (_phase != Phase.Idle)
        {
            switch (_phase)
            {
                case Phase.Identifying: TickIdentify(now); break;
                case Phase.Adding:      TickAdding(now, busyCount); break;
                case Phase.WaitSplit:   TickWaitSplit(now); break;
                case Phase.WaitAccept:  TickWaitAccept(now); break;
            }
            if ((_phase is Phase.Identifying or Phase.Adding or Phase.WaitSplit) && now - _bailAt > BailMs)
            {
                _host.Log("[RynthAi] AutoTrade: timed out - STOPPING");
                Stop(true);
            }
        }
        UpdateStatus();
    }

    /// <summary>Logout / teardown: drop everything without chat.</summary>
    public void Reset()
    {
        ClearSession();
        _phase = Phase.Idle;
        _traderId = 0;
        _traderName = string.Empty;
        _baselined = false;
        while (_commands.TryDequeue(out _)) { }
        _status = "Idle";
    }

    // ── Trade events, from the engine's trade state (UB WorldFilter_* handlers) ──

    private void PollTrade(long now)
    {
        if (!_host.TryGetTradeState(out TradeState st))
            return;

        if (!_baselined)
        {
            // Plugin (re)load: a trade already open is known but doesn't start a session.
            _baselined = true;
            _generation = st.Generation;
            _partnerAccepts = st.PartnerAcceptCount;
            _failures = st.FailureCount;
            _wasOpen = st.IsOpen;
            if (st.IsOpen && st.PartnerId != 0)
            {
                _traderId = st.PartnerId;
                _traderName = ObjectName(st.PartnerId);
            }
            return;
        }

        // UB WorldFilter_EndTrade: the trade closed, or a new one replaced it.
        if (_wasOpen && (!st.IsOpen || st.Generation != _generation))
            OnEndTrade();

        // UB WorldFilter_EnterTrade.
        if (st.IsOpen && st.Generation != _generation)
            OnEnterTrade(st);

        _generation = st.Generation;
        _wasOpen = st.IsOpen;

        // UB WorldFilter_FailToAddTradeItem.
        if (st.FailureCount != _failures)
        {
            _failures = st.FailureCount;
            OnFailToAdd(st.LastFailureItemId, st.LastFailureReason);
        }

        // UB WorldFilter_AddTradeItem (our side only).
        if (_phase != Phase.Idle && _pending.Count > 0)
        {
            foreach (uint id in _host.GetTradeItems(TradeSide.You))
            {
                if (!_pending.Remove(id)) continue;
                _added.Add(id);
                _bailAt = now;
                _host.Log($"[RynthAi] AutoTrade: {NameOf(id)} added to trade window");
            }
        }

        // UB WorldFilter_AcceptTrade (the partner accepted).
        if (st.PartnerAcceptCount != _partnerAccepts)
        {
            _partnerAccepts = st.PartnerAcceptCount;
            if (st.IsOpen && st.PartnerAccepted && !st.YouAccepted)
                OnPartnerAccepted(st.PartnerId);
        }
    }

    private void OnEnterTrade(TradeState st)
    {
        _traderId = st.PartnerId;
        if (_traderId == 0)
            return;
        _traderName = ObjectName(_traderId);
        EnsureProfileFolders();
        _host.Log($"[RynthAi] AutoTrade: trade opened with {_traderName} [0x{_traderId:X8}]");

        if (!_settings.AutoTradeEnabled)
            return;

        Start(string.Empty);
    }

    private void OnEndTrade()
    {
        if (_phase != Phase.Idle)
            Stop(true);
        _traderId = 0;
    }

    private void OnFailToAdd(uint itemId, uint reason)
    {
        if (_phase == Phase.Idle || itemId == 0) return;
        if (_pending.Remove(itemId))
            Tool($"the server refused {NameOf(itemId)} (error 0x{reason:X4})");
    }

    private void OnPartnerAccepted(uint partnerId)
    {
        string name = ObjectName(partnerId);
        if (string.IsNullOrEmpty(name) || name.StartsWith("0x", StringComparison.Ordinal))
            return;

        List<string> patterns = GetAutoAcceptChars();
        _host.Log($"[RynthAi] AutoTrade: checking {name} against {patterns.Count} auto-accept pattern(s).");
        foreach (string pattern in patterns)
        {
            if (!IsNameMatch(name, pattern)) continue;
            _host.Log("[RynthAi] AutoTrade: accepting trade...");
            if (_host.TradeAccept())
                ThinkOrWrite($"Trade accepted: {name}", alwaysChat: true);
            else
                Error("could not send the accept");
            return;
        }
    }

    private static bool IsNameMatch(string name, string pattern)
    {
        try
        {
            return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Commands (queued from chat; run on the tick thread)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// /ub autotrade [&lt;lootProfile.utl&gt; | autoaccept {add[gs] &lt;pattern&gt; | remove[gs] &lt;pattern&gt; | list}]
    /// plus RynthAi's on | off | status | stop.
    /// </summary>
    public void Command(string args)
    {
        string a = (args ?? string.Empty).Trim();
        switch (a.ToLowerInvariant())
        {
            case "":
                // UB's documented example: the profile named for the trade partner.
                if (!RequireApi()) return;
                Start(string.Empty);
                return;
            case "on":
            case "enable":
                _settings.AutoTradeEnabled = true;
                Tool("enabled: runs when a trade window opens.");
                if (!_host.HasTrade) SayNeedsUpdate();
                return;
            case "off":
            case "disable":
                _settings.AutoTradeEnabled = false;
                Tool("disabled.");
                return;
            case "stop":
            case "cancel":
            case "quit":
                if (_phase == Phase.Idle) Tool("not running.");
                else Stop(true);
                return;
            case "status":
                PrintStatus();
                return;
            case "help":
                Usage();
                return;
        }

        Match m = CommandPattern.Match(a);
        if (!m.Success)
        {
            Usage();
            return;
        }

        string profile = m.Groups["LootProfile"].Value.Trim();
        string verb = m.Groups["Verb"].Value.ToLowerInvariant();
        string charPattern = m.Groups["CharPattern"].Value.Trim();

        if (profile.Length > 0)
        {
            if (!RequireApi()) return;
            _host.Log($"[RynthAi] AutoTrade: starting with profile: {profile}");
            Start(profile);
        }
        else if (verb == "list")
        {
            List<string> list = GetAutoAcceptChars();
            Chat("Auto Accept List:");
            int i = 0;
            foreach (string aac in list)
                Chat($" [{++i}] {aac}");
        }
        else if (verb.Length > 0 && charPattern.Length > 0)
        {
            ChangeAutoAcceptCharList(verb, charPattern);
        }
        else
        {
            Usage();
        }
    }

    private void Usage()
    {
        Chat("Usage: /ub autotrade [<lootProfile>.utl | autoaccept {add[g|s] <namePattern> | remove[g|s] <namePattern> | list}]");
        Chat("       /ra autotrade on | off | status | stop   (same as /ub autotrade)");
    }

    private void PrintStatus()
    {
        Chat($"AutoTrade: {(_settings.AutoTradeEnabled ? "enabled" : "disabled")}, test mode {(_settings.AutoTradeTestMode ? "on" : "off")}, "
             + $"auto accept {(_settings.AutoTradeAutoAccept ? "on" : "off")}, only main pack {(_settings.AutoTradeOnlyFromMainPack ? "on" : "off")}.");
        Chat($"  {_status}");
        if (_host.HasTrade && _host.TryGetTradeState(out TradeState st))
        {
            if (st.IsOpen)
                Chat($"  Trade open with {ObjectName(st.PartnerId)}: you {st.YourItemCount} item(s){(st.YouAccepted ? " (accepted)" : "")}, "
                     + $"them {st.PartnerItemCount}{(st.PartnerAccepted ? " (accepted)" : "")}.");
            else
                Chat("  No trade open.");
            if (!st.Watching) Error("the engine can't see trade events on this client (see RynthCore.log).");
            if (!st.ActionsAvailable) Error("the engine couldn't bind every trade action on this client (see RynthCore.log).");
        }
        Chat($"  Profiles: <Partner Name>.utl or default.utl in {string.Join(", ", ProfileDirs())}");
    }

    private void ChangeAutoAcceptCharList(string verb, string charPattern)
    {
        string dir = verb[verb.Length - 1] switch
        {
            'g' => MainProfileDir,
            's' => ServerDir() ?? MainProfileDir,
            _ => CharDir() ?? MainProfileDir,
        };
        string scope = verb[verb.Length - 1] switch
        {
            'g' => "all of your characters on every server",
            's' => "all of your characters on this server",
            _ => "this character",
        };
        string path = Path.Combine(dir, AutoAcceptListFileName);
        var list = ReadAutoAcceptCharFile(path);

        if (verb.StartsWith("add", StringComparison.Ordinal))
        {
            if (list.Contains(charPattern))
            {
                Tool($"'{charPattern}' is already on the auto-accept list for {scope}.");
                return;
            }
            try { _ = Regex.Match(string.Empty, charPattern); }
            catch
            {
                Error("Error: Invalid regex");
                return;
            }
            list.Add(charPattern);
            Tool($"added '{charPattern}' to the auto-accept list for {scope}.");
        }
        else if (verb.StartsWith("remove", StringComparison.Ordinal))
        {
            if (!list.Remove(charPattern))
            {
                Tool($"'{charPattern}' isn't on the auto-accept list for {scope}.");
                return;
            }
            Tool($"removed '{charPattern}' from the auto-accept list for {scope}.");
        }

        try
        {
            if (list.Count <= 0)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, WriteJsonStringArray(list));
            }
        }
        catch (Exception ex)
        {
            Error($"couldn't save {path}: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Session (UB Start / Stop / Core_RenderFrame)
    // ════════════════════════════════════════════════════════════════════════

    private void Start(string useProfilePath)
    {
        if (_phase != Phase.Idle)
        {
            Error("Already running.");
            return;
        }

        ClearSession();

        if (!_host.TryGetTradeState(out TradeState st) || !st.IsOpen || st.PartnerId == 0)
        {
            Error("You must open a trade with someone first!");
            _traderId = 0;
            return;
        }
        _traderId = st.PartnerId;
        _traderName = ObjectName(_traderId);
        _sessionGeneration = st.Generation;

        string profilePath = GetProfilePath(string.IsNullOrEmpty(useProfilePath) ? Sanitize(_traderName) + ".utl" : useProfilePath);
        if (!File.Exists(profilePath))
        {
            Chat("No auto trade profile exists: " + profilePath);
            return;
        }

        _host.Log($"[RynthAi] AutoTrade: loading loot profile at {profilePath}");
        try
        {
            _profile = VTankLootParser.Load(profilePath);
        }
        catch (Exception ex)
        {
            Error($"Unable to load loot profile {profilePath}: {ex.Message}");
            ClearSession();
            return;
        }
        _profilePath = profilePath;
        _keepIds = ReadKeepIds();
        _bailAt = Now();

        uint player = PlayerId();
        foreach (var wo in Inventory())
        {
            // UB filters the inventory up front when only trading from the main pack.
            if (_settings.AutoTradeOnlyFromMainPack && !InMainPack(wo, player)) continue;
            _sessionItems.Add(unchecked((uint)wo.Id));
        }

        Tool($"trading with {_traderName} using {Path.GetFileName(profilePath)} ({_profile.Rules.Count} rules){(_settings.AutoTradeTestMode ? " [test mode]" : string.Empty)}");
        BuildIdQueue();
        _phase = Phase.Identifying;
    }

    /// <param name="profileLoaded">UB Stop(profileLoaded): say "AutoTrade finished" when a profile ran.</param>
    private void Stop(bool profileLoaded)
    {
        if (profileLoaded)
            ThinkOrWrite("AutoTrade finished: " + _traderName, alwaysChat: false);
        _phase = Phase.Idle;
        ClearSession();
    }

    private void ClearSession()
    {
        _profile = null;
        _profilePath = string.Empty;
        _sessionItems.Clear();
        _pending.Clear();
        _pendingNames.Clear();
        _added.Clear();
        _handled.Clear();
        _keepUpToCounts.Clear();
        _idQueue.Clear();
        _idState.Clear();
        _splitBefore = new HashSet<uint>();
    }

    // ── Identify (UB Assessor job) ──

    private void BuildIdQueue()
    {
        _idQueue.Clear();
        _idState.Clear();
        if (_profile == null || !_host.HasRequestId || !_host.HasHasAppraisalData)
            return;

        var ctx = NewContext();
        uint player = PlayerId();
        foreach (uint id in _sessionItems)
        {
            var wo = _cache[unchecked((int)id)];
            if (wo == null || Appraised(id)) continue;
            if (IsEquipped(wo) || wo.ObjectClass is AcObjectClass.Container or AcObjectClass.Foci) continue;
            if (_keepIds.Contains(wo.Id)) continue;

            // Only identify what a Keep / Keep # rule might still want: skip items no rule can
            // match, and items another rule already decides without ID data.
            ProfileVerdict verdict = AutoVendorRules.Decide(_profile.Rules, Judge(EvalObject(wo), Facts(wo, appraised: false), ctx));
            if (verdict.Rule == null) continue;
            if (verdict.Certain && !IsKeepAction(verdict.Rule.Action)) continue;
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
                    _idQueue.RemoveAt(i);   // give up; it stays unidentified and is never added
                    _idState.Remove(id);
                    continue;
                }
                if (waiting) inFlight++;
            }
        }

        if (_idQueue.Count == 0)
        {
            if (_settings.AutoTradeTestMode)
            {
                DoTestMode();
                Stop(true);
                return;
            }
            _phase = Phase.Adding;
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

    // ── Add (UB Core_RenderFrame pass) ──

    private void TickAdding(long now, int busyCount)
    {
        if (busyCount != 0)
            return;
        if (!TradeStillOpen())
        {
            Stop(true);
            return;
        }

        int sent = 0;
        foreach (TradeItem item in GetTradeItems(heldBack: null))
        {
            // Skip items already decided (added, pending, kept, or split).
            if (_handled.Contains(item.Id))
                continue;

            _host.Log($"[RynthAi] AutoTrade: Trade Item: {item.Name}");

            if (item.Rule.Action == VTankLootAction.KeepUpTo)
            {
                int data1 = item.Rule.KeepCount ?? 0;
                _keepUpToCounts.TryGetValue(item.RuleName, out int count);
                int stack = item.StackSize;

                if (data1 < 0) // keep this many, give the rest
                {
                    int keep = -data1;
                    if (count < keep)
                    {
                        // The kept item is decided; never offered in a later pass.
                        _handled.Add(item.Id);
                        if (stack > keep - count)
                        {
                            _keepUpToCounts[item.RuleName] = keep;
                            BeginSplit(item, stack - (keep - count), now);
                            return;
                        }
                        _keepUpToCounts[item.RuleName] = count + stack;
                    }
                    else
                    {
                        AddToTradeWindow(item.Id, item.Name, item.RuleName);
                        sent++;
                    }
                }
                else // give this many
                {
                    if (count >= data1)
                    {
                        _handled.Add(item.Id);
                        continue;
                    }
                    if (stack > data1 - count)
                    {
                        _handled.Add(item.Id);
                        _keepUpToCounts[item.RuleName] = data1;
                        BeginSplit(item, data1 - count, now);
                        return;
                    }
                    _keepUpToCounts[item.RuleName] = count + stack;
                    AddToTradeWindow(item.Id, item.Name, item.RuleName);
                    sent++;
                }
            }
            else
            {
                AddToTradeWindow(item.Id, item.Name, item.RuleName);
                sent++;
            }

            if (sent >= MaxAddsPerTick)
                return;   // the rest next tick
        }

        // A whole pass with nothing left to decide.
        if (_settings.AutoTradeAutoAccept)
        {
            _phase = Phase.WaitAccept;
            _acceptDeadline = now + AcceptWaitMs;
        }
        else
        {
            Stop(true);
        }
    }

    private void AddToTradeWindow(uint itemId, string name, string ruleName)
    {
        _handled.Add(itemId);
        _host.Log($"[RynthAi] AutoTrade: Adding to trade window: {name}");
        if (!_host.TradeAdd(itemId))
        {
            Error($"could not send {name} to the trade window");
            return;
        }
        _pending.Add(itemId);
        _pendingNames[itemId] = name;
        _bailAt = Now();
        Tool($"adding {name} ({ruleName})");
    }

    // ── Split (UB TrySplitItem) ──

    private void BeginSplit(TradeItem item, int splitCount, long now)
    {
        uint player = PlayerId();
        if (splitCount <= 0 || player == 0 || !_host.HasSplitStackInternal)
        {
            Error($"can't split {item.Name} on this engine; skipping it");
            return;
        }
        _splitBefore = new HashSet<uint>(Inventory().Select(w => unchecked((uint)w.Id)));
        _splitName = item.Name;
        _splitRule = item.RuleName;
        _splitAmount = splitCount;
        _host.Log($"[RynthAi] AutoTrade: Splitting {item.Name}. old: {item.StackSize} new: {splitCount}");
        if (!_host.SplitStackInternal(item.Id, player, 0, splitCount))
        {
            Error($"could not split {item.Name}");
            return;
        }
        _splitDeadline = now + SplitWaitMs;
        _nextPollAt = now + 250;
        _bailAt = now;
        _phase = Phase.WaitSplit;
    }

    private void TickWaitSplit(long now)
    {
        if (now < _nextPollAt) return;
        _nextPollAt = now + 250;

        uint found = 0;
        uint player = PlayerId();
        foreach (var wo in Inventory())
        {
            uint id = unchecked((uint)wo.Id);
            if (_splitBefore.Contains(id)) continue;
            if (!InMainPack(wo, player)) continue;
            if (string.Equals(wo.Name, _splitName, StringComparison.Ordinal) && StackOf(wo) == _splitAmount)
            {
                found = id;
                break;
            }
        }

        if (found != 0)
        {
            _host.Log($"[RynthAi] AutoTrade: Adding {_splitAmount} of {_splitName} to trade window");
            AddToTradeWindow(found, $"{_splitName} x{_splitAmount}", _splitRule);
            _phase = Phase.Adding;
        }
        else if (now > _splitDeadline)
        {
            Error($"the split of {_splitName} didn't show up; carrying on without it");
            _phase = Phase.Adding;
        }
    }

    // ── Accept (UB doAccept) ──

    private void TickWaitAccept(long now)
    {
        if (!TradeStillOpen())
        {
            Stop(true);
            return;
        }

        if (_pending.Count <= 0)
        {
            if (_traderId != 0 && _settings.AutoTradeAutoAccept)
            {
                _host.Log("[RynthAi] AutoTrade: Accepting trade");
                if (_host.TradeAccept())
                    Tool($"accepted the trade with {_traderName} ({_added.Count} item(s) added).");
                else
                    Error("could not send the accept");
            }
            Stop(true);
            return;
        }

        if (now > _acceptDeadline)
        {
            string missing = string.Join(", ", _pending.Select(NameOf).Take(5));
            Error($"{_pending.Count} item(s) never showed up in the trade window ({missing}); not accepting.");
            Stop(true);
        }
    }

    private bool TradeStillOpen()
        => _host.TryGetTradeState(out TradeState st) && st.IsOpen && st.Generation == _sessionGeneration;

    // ── Test mode (UB DoTestMode) ──

    private void DoTestMode()
    {
        Chat("Trade Items:");
        var held = new List<string>();
        int n = 0;
        foreach (TradeItem item in GetTradeItems(held))
        {
            string rule = item.Rule.Action == VTankLootAction.KeepUpTo
                ? $"{item.RuleName} (Keep # {item.Rule.KeepCount ?? 0})"
                : item.RuleName;
            Chat($"  {item.Name}{(item.StackSize > 1 ? $" x{item.StackSize}" : string.Empty)} - {rule}");
            n++;
        }
        if (n == 0) Chat("  (Nothing)");
        if (held.Count > 0)
        {
            Chat("Held back (never added):");
            foreach (string h in held.Take(10)) Chat($"  {h}");
            if (held.Count > 10) Chat($"  ...and {held.Count - 10} more");
        }
        Chat("TestMode is on: nothing was added (/ub opt set AutoTrade.TestMode false).");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Items (UB GetTradeItems / ItemIsSafeToGetRidOf)
    // ════════════════════════════════════════════════════════════════════════

    private readonly record struct TradeItem(uint Id, string Name, int StackSize, VTankLootRule Rule, string RuleName);

    /// <summary>
    /// The session's items a Keep / Keep # rule matches, smallest stacks first (UB order).
    /// <paramref name="heldBack"/> (test mode) collects keep matches that are never added, with why.
    /// </summary>
    private List<TradeItem> GetTradeItems(List<string>? heldBack)
    {
        var result = new List<TradeItem>();
        if (_profile == null) return result;

        var ctx = NewContext();
        uint player = PlayerId();
        var candidates = new List<(WorldObject Wo, int Stack)>();
        foreach (uint id in _sessionItems)
        {
            var wo = _cache[unchecked((int)id)];
            if (wo == null || wo.Id == 0) continue;
            candidates.Add((wo, StackOf(wo)));
        }

        foreach (var (wo, stack) in candidates.OrderBy(c => c.Stack))
        {
            uint id = unchecked((uint)wo.Id);
            bool appraised = Appraised(id);
            ProfileVerdict verdict = AutoVendorRules.Decide(_profile.Rules, Judge(EvalObject(wo), Facts(wo, appraised), ctx));
            if (verdict.Rule == null) continue;
            bool isKeep = IsKeepAction(verdict.Rule.Action);

            string? unsafeWhy = UnsafeReason(wo, player, appraised);
            if (unsafeWhy != null)
            {
                if (heldBack != null && (isKeep || verdict.KeepPossible))
                    heldBack.Add($"{wo.Name} - {unsafeWhy}");
                continue;
            }

            if (!isKeep) continue;
            if (!verdict.Certain)
            {
                heldBack?.Add($"{wo.Name} - rule '{verdict.RuleName}' needs {verdict.UnknownReason} data we don't have");
                continue;
            }
            result.Add(new TradeItem(id, wo.Name, stack, verdict.Rule, verdict.RuleName));
        }
        return result;
    }

    private static bool IsKeepAction(VTankLootAction a) => a is VTankLootAction.Keep or VTankLootAction.KeepUpTo;

    /// <summary>Null when the item may be traded away; otherwise why it never will be.</summary>
    private string? UnsafeReason(WorldObject wo, uint player, bool appraised)
    {
        uint id = unchecked((uint)wo.Id);
        if (!appraised) return "not identified";
        if (IsEquipped(wo)) return "equipped";
        if (_keepIds.Contains(wo.Id)) return "on RynthAi's item lists";
        if (wo.Values(114 /* Attuned */, 0) > 0) return "attuned";
        if (IsRetained(id)) return "retained";
        if (wo.ObjectClass == AcObjectClass.Container || wo.Values(LongValueKey.ItemsCapacity, 0) > 0) return "container";
        if (wo.ObjectClass == AcObjectClass.Foci) return "focus";
        if (wo.Values(171 /* NumTimesTinkered */, 0) > 0) return "tinkered";
        if (wo.Values(179 /* ImbuedEffect */, 0) != 0) return "imbued";
        if (!string.IsNullOrEmpty(wo.Values((StringValueKey)7 /* Inscription */, string.Empty))) return "inscribed";
        return null;
    }

    private bool IsRetained(uint id)
    {
        if (_host.HasGetObjectBitfield && _host.TryGetObjectBitfield(id, out uint bf) && (bf & 0x01000000u) != 0)
            return true;   // BF_RETAINED
        return _host.HasGetObjectBoolProperty && _host.TryGetObjectBoolProperty(id, 91 /* Retained */, out bool ret) && ret;
    }

    /// <summary>Items RynthAi uses: the Weapons and Consumables lists and every monster rule's weapons.</summary>
    private HashSet<int> ReadKeepIds()
    {
        var ids = new HashSet<int>();
        try
        {
            foreach (var r in _settings.ItemRules.ToList()) if (r.Id != 0) ids.Add(r.Id);
            foreach (var c in _settings.ConsumableRules.ToList()) if (c.Id != 0) ids.Add(c.Id);
            foreach (var m in _settings.MonsterRules.ToList())
            {
                if (m.WeaponId != 0) ids.Add(m.WeaponId);
                if (m.OffhandId != 0) ids.Add(m.OffhandId);
            }
        }
        catch { }
        return ids;
    }

    private ItemFacts Facts(WorldObject wo, bool appraised) => new()
    {
        IsVendorListing = false,
        Appraised = appraised,
        ObjectClass = wo.ObjectClass,
        HasSpellData = appraised && _host.HasGetObjectSpellIds,
        HasPalettes = _host.HasGetObjectPalettes,
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

    private static bool InMainPack(WorldObject wo, uint player)
    {
        int p = unchecked((int)player);
        int container = wo.DirectContainerId != 0 ? wo.DirectContainerId : wo.Container;
        return player != 0 && container == p;
    }

    private static bool IsEquipped(WorldObject wo)
        => wo.WieldedLocation > 0 || wo.Values(LongValueKey.EquippedSlots, 0) > 0;

    private bool Appraised(uint id) => _host.HasHasAppraisalData && _host.HasAppraisalData(id);

    private uint PlayerId()
    {
        if (_playerId == 0) _playerId = _host.GetPlayerId();
        return _playerId;
    }

    private string ObjectName(uint id)
        => id != 0 && _host.TryGetObjectName(id, out string n) && !string.IsNullOrWhiteSpace(n) ? n : $"0x{id:X8}";

    private string NameOf(uint id)
        => _pendingNames.TryGetValue(id, out string? n) ? n : ObjectName(id);

    // ════════════════════════════════════════════════════════════════════════
    //  Profiles and auto-accept lists (UB GetProfilePath / GetAutoAcceptChars)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// First existing file of: character folder, server folder, main folder (the named
    /// profile), then default.utl in the same three; else the main-folder path (missing).
    /// </summary>
    public string GetProfilePath(string profileName)
    {
        var dirs = ProfileDirs();
        foreach (string dir in dirs)
        {
            string p = SafeCombine(dir, profileName);
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
        string? c = CharDir();
        if (c != null) dirs.Add(c);
        string? s = ServerDir();
        if (s != null) dirs.Add(s);
        dirs.Add(MainProfileDir);
        return dirs;
    }

    private string? CharDir()
    {
        string charFolder = _charFolder() ?? string.Empty;
        return string.IsNullOrEmpty(charFolder) ? null : Path.Combine(charFolder, ProfileSubfolder);
    }

    private string? ServerDir()
        => _host.HasGetWorldName && _host.TryGetWorldName(out string world) && !string.IsNullOrWhiteSpace(world)
            ? Path.Combine(MainProfileDir, Sanitize(world))
            : null;

    /// <summary>Create the per-character and per-server folders so players can see where profiles go (UB Init).</summary>
    public void EnsureProfileFolders()
    {
        foreach (string dir in ProfileDirs())
            try { Directory.CreateDirectory(dir); } catch { }
    }

    /// <summary>Global, server and character autoAcceptList.json, plus the settings list.</summary>
    private List<string> GetAutoAcceptChars()
    {
        var result = new List<string>();
        void AddAll(IEnumerable<string> items)
        {
            foreach (string s in items)
                if (!string.IsNullOrWhiteSpace(s) && !result.Contains(s)) result.Add(s);
        }
        AddAll(ReadAutoAcceptCharFile(Path.Combine(MainProfileDir, AutoAcceptListFileName)));
        string? server = ServerDir();
        if (server != null) AddAll(ReadAutoAcceptCharFile(Path.Combine(server, AutoAcceptListFileName)));
        string? character = CharDir();
        if (character != null) AddAll(ReadAutoAcceptCharFile(Path.Combine(character, AutoAcceptListFileName)));
        try { AddAll(_settings.AutoTradeAutoAcceptChars.ToList()); } catch { }
        return result;
    }

    private List<string> ReadAutoAcceptCharFile(string file)
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(file)) return list;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is string s && s.Length > 0)
                    list.Add(s);
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] AutoTrade: couldn't read {file}: {ex.Message}");
        }
        return list;
    }

    private static string WriteJsonStringArray(List<string> items)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (string s in items) w.WriteStringValue(s);
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
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
    private void Tool(string msg) => _host.WriteToChat($"[RynthAi] AutoTrade: {msg}", 1);
    private void Error(string msg) => _host.WriteToChat($"[RynthAi] AutoTrade: {msg}", 2);

    /// <summary>
    /// UB Util.ThinkOrWrite: a /tell to yourself when Think is on (metas watch for it). An
    /// automatic accept is also always written to chat.
    /// </summary>
    private void ThinkOrWrite(string msg, bool alwaysChat)
    {
        uint player = PlayerId();
        if (_settings.AutoTradeThink && _host.HasInvokeChatParser && player != 0
            && _host.TryGetObjectName(player, out string me) && !string.IsNullOrWhiteSpace(me))
        {
            _host.InvokeChatParser($"/tell {me}, {msg}");
            if (alwaysChat) Chat(msg);
            return;
        }
        Chat(msg);
    }

    private bool RequireApi()
    {
        if (_host.HasTrade) return true;
        SayNeedsUpdate();
        return false;
    }

    private void SayNeedsUpdate()
        => Error($"needs RynthCore 2026.9.x or newer (player trading is plugin API v{RequiredApiVersion}; this engine is v{_host.Version}). AutoTrade stays off.");

    private void UpdateStatus()
    {
        if (!_host.HasTrade)
        {
            _status = $"Needs RynthCore 2026.9.x or newer (engine API v{_host.Version}, needs v{RequiredApiVersion})";
            return;
        }
        string s = _phase switch
        {
            Phase.Idle => _traderId != 0 ? $"Idle (trade open with {_traderName})" : "Idle",
            Phase.Identifying => $"Identifying {_idQueue.Count} item(s) - {_traderName}",
            Phase.WaitSplit => $"Splitting {_splitName} - {_traderName}",
            Phase.WaitAccept => $"Waiting for {_pending.Count} item(s) before accepting - {_traderName}",
            _ => $"Adding items for {_traderName} ({Path.GetFileName(_profilePath)}): {_added.Count} added, {_pending.Count} pending",
        };
        if (_settings.AutoTradeTestMode && _phase != Phase.Idle) s += " [test mode]";
        _status = s;
    }

    private static long Now() => Environment.TickCount64;
}
