// TinkerRunner.cs - Scans one Crafting tab's items, plans them and applies the salvage.
//
// Pump thread only (the tab's Render reads the volatile snapshots and posts Scan/Start/Stop).
// One step at a time:
//   1. gates: peace mode (unless the server allows combat crafting), not busy, ActionPacer;
//   2. re-plan the item from fresh facts (TinkerPlanner) minus what this run already tried;
//   3. pick the bag (TinkerPlanner.SelectBag over TinkerMath's retail chance, Foolproof = 100%);
//   4. arm the engine's confirmation answer (v81: Yes only at the tab's minimum percent) and
//      UseObjectOn(bag, item);
//   5. wait for the result: the craft chat line, the bag leaving the pack, a declined
//      confirmation, "cannot be used on", or a WeenieError (YoureTooBusy retries).
// Without the v81 engine (or with dialog answering off) AC shows its own dialog and the player
// clicks it; the runner waits longer for that.
// Prepared rows (a server's trinket salvage): with no bag prepared at the chosen tier, a raw bag
// is first used on that tier's Ammunition (UB-IT's "prep salvage") and the step runs once the
// prepared bag shows up.
// Items a run finished are left out of later scans this session, so server rows that don't show
// on the item aren't applied twice and "auto next batch" moves on to the next items.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

/// <summary>One scanned item as the tab shows it.</summary>
internal sealed class TinkerRow
{
    public int Id;
    public string Name = string.Empty;
    public int Tinks;
    public double Workmanship;
    public bool Imbued;
    public string Plan = "-";
    /// <summary>Updated by the runner as it works the item.</summary>
    public volatile string Status = "planned";
}

internal sealed class TinkerRunner
{
    private const uint IntWorkmanship = 105;
    private const uint IntNumItemsInMaterial = 170;
    private const uint IntDamageType = 45;
    private const uint IntItemMaxMana = 108;
    private const uint BoolRetained = 91;
    private const uint ErrorYoureTooBusy = 0x1D;
    private const long StepTimeoutMs = 15_000;
    /// <summary>The player answers AC's own dialog: give them time.</summary>
    private const long ManualTimeoutMs = 60_000;
    private const long RetryDelayMs = 1_500;
    private const long StepGapMs = 600;
    private const int MaxRetries = 3;
    /// <summary>Appraisal requests one scan may send (the Items tab sees the whole pack).</summary>
    private const int MaxAppraisalsPerScan = 25;
    /// <summary>UB-IT polls up to ~30 s for the prepared bag.</summary>
    private const long PrepTimeoutMs = 30_000;
    /// <summary>Auto next batch: re-scan while items are still being appraised, this often and this many times.</summary>
    private const long AutoRescanDelayMs = 3_000;
    private const int AutoRescanTries = 10;

    private static readonly Regex Applied = new(
        @"^(?<character>[\w\s]+) successfully applies the (?<salvage>[\w\s\-]+?)(\sSalvage.*)(\s?\(100\))?\s\(workmanship (?<workmanship>\d+\.\d+)\) to the (?<item>[\w\s\'\-]+)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Failed = new(
        @"^(?<character>[\w\s]+) fails to apply the (?<salvage>[\w\s\-]+?)(\sSalvage.*)(\s?\(100\))?\s\(workmanship (?<workmanship>\d+\.\d+)\) to the (?<item>[\w\s\'\-]+)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Foolproof = new(@"^You apply the (?<salvage>.*)\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CannotUse = new(@"^The .+ cannot be used on the .+\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MiniRemoteContext _ctx;
    private readonly Func<TinkerBench, IReadOnlyList<TinkerMaterial>> _rows;
    private readonly Func<TinkerBench, TinkerBenchSettings> _settings;

    // Job (pump thread)
    private TinkerBench _bench;
    private readonly Queue<int> _queue = new();
    private int _itemId;
    private readonly Dictionary<string, int> _attempted = new();
    private readonly HashSet<string> _skipped = new();
    private bool _waiting;
    private TinkerStep _step;
    private int _bagId;
    private string _bagName = string.Empty;
    private int _confSeq, _errSeq, _retries, _stepPercent;
    private bool _manual;
    private long _stepAt, _nextAt;
    private Dictionary<int, TinkerRow> _byId = new();
    /// <summary>The wait is for a bag being prepared, not a tinker.</summary>
    private bool _prep;
    private int _prepsThisItem;
    /// <summary>Bags this batch used up (auto next batch stops after a batch that used none).</summary>
    private int _usedThisBatch;
    private long _autoRescanAt;
    private int _autoRescansLeft;
    /// <summary>Items a run finished this session, per bench (pump thread).</summary>
    private readonly Dictionary<TinkerBench, HashSet<int>> _done = new();
    /// <summary>Per-bench copies of the done counts for the tab.</summary>
    private readonly int[] _doneCounts = new int[Enum.GetValues<TinkerBench>().Length];

    // Render snapshots
    private volatile TinkerRow[] _scan = Array.Empty<TinkerRow>();
    private volatile string _status = "idle";
    private volatile int _scannedBench = -1;

    public TinkerRunner(MiniRemoteContext ctx, Func<TinkerBench, IReadOnlyList<TinkerMaterial>> rows,
                        Func<TinkerBench, TinkerBenchSettings> settings)
    {
        _ctx = ctx;
        _rows = rows;
        _settings = settings;
    }

    /// <summary>A job is on (set by Start, cleared by Stop: the queue empties before the batch-done step runs).</summary>
    public bool Running => _active;
    private volatile bool _active;
    public TinkerBench Bench => _bench;
    public string Status => _status;
    /// <summary>The last scan's rows (for <see cref="ScannedBench"/>).</summary>
    public TinkerRow[] Rows => _scan;
    public int ScannedBench => _scannedBench;
    /// <summary>How many of a bench's items this session already finished (render thread safe).</summary>
    public int DoneCount(TinkerBench bench) => Volatile.Read(ref _doneCounts[(int)bench]);

    private RynthCoreHost Host => _ctx.Host;
    private MiniRemoteInventory Inv => _ctx.Inventory;
    private CraftingTabState C => _ctx.State.Crafting;
    private int Tier => Math.Clamp(C.TrinketTier, 0, TinkerCatalog.TrinketTiers.Length - 1);

    private HashSet<int> DoneSet(TinkerBench bench)
    {
        if (!_done.TryGetValue(bench, out var set)) _done[bench] = set = new HashSet<int>();
        return set;
    }

    /// <summary>Lets a bench's finished items show in scans again (pump thread).</summary>
    public void ForgetDone(TinkerBench bench)
    {
        DoneSet(bench).Clear();
        Volatile.Write(ref _doneCounts[(int)bench], 0);
        _status = $"{TinkerCatalog.Label(bench)}: finished items will show in scans again";
    }

    private void MarkDone(TinkerBench bench, int id)
    {
        var set = DoneSet(bench);
        if (set.Add(id)) Volatile.Write(ref _doneCounts[(int)bench], set.Count);
    }

    // ── Scan ────────────────────────────────────────────────────────────────

    /// <summary>Reads what the planner needs from an item.</summary>
    public TinkerItemFacts Facts(WorldObject wo) => new()
    {
        Id = wo.Id,
        Name = wo.Name ?? string.Empty,
        Class = wo.ObjectClass,
        Workmanship = Inv.Int(wo, IntWorkmanship),
        Tinks = Inv.Int(wo, MiniRemoteInventory.IntNumTimesTinkered),
        Imbued = Inv.Int(wo, MiniRemoteInventory.IntImbuedEffect) != 0,
        DamageType = Inv.Int(wo, IntDamageType),
        Retained = Inv.Bool(wo, BoolRetained),
        MaxMana = Inv.Int(wo, IntItemMaxMana),
        Equipped = Inv.IsEquipped(wo),
        FlagInt = k => Inv.Int(wo, unchecked((uint)k)),
    };

    /// <summary>A thread-safe copy of a bench's settings (the tab edits Counts under its lock).</summary>
    public static TinkerBenchSettings Snapshot(TinkerBenchSettings s)
    {
        lock (s.Counts)
        {
            return new TinkerBenchSettings
            {
                Counts = new Dictionary<string, int>(s.Counts),
                MinChancePercent = s.MinChancePercent,
                MaxTinks = s.MaxTinks,
                MinFreeTinks = s.MinFreeTinks,
                SkipImbued = s.SkipImbued,
                PierceForCombo = s.PierceForCombo,
                RendLast = s.RendLast,
            };
        }
    }

    /// <summary>
    /// Scans the pack for a tab: up to BatchSize items that have something to apply, leaving out
    /// the ones this session already finished. Returns how many items are still being appraised.
    /// </summary>
    public int Scan(TinkerBench bench)
    {
        if (Running) { _status = "stop the running job before scanning"; return 0; }
        _autoRescanAt = 0;
        var s = Snapshot(_settings(bench));
        var rows = _rows(bench);
        var done = DoneSet(bench);
        int batch = Math.Clamp(C.BatchSize, 1, 100);
        var list = new List<TinkerRow>();
        int appraising = 0, hidden = 0;
        foreach (var wo in Inv.Items())
        {
            if (!TinkerCatalog.Targets(bench, wo.ObjectClass)) continue;
            bool trinket = TinkerCatalog.IsTrinket(wo.Name, _ctx.ServerTrinkets);
            if (bench == TinkerBench.Jewelry && trinket) continue;
            if (bench == TinkerBench.Trinkets && !trinket) continue;
            if (done.Contains(wo.Id)) { hidden++; continue; }
            if (!Inv.HasAppraisal(wo))
            {
                if (appraising < MaxAppraisalsPerScan && Inv.RequestAppraisal(wo)) appraising++;
                continue;
            }
            var f = Facts(wo);
            if (TinkerPlanner.Excluded(f, s).Length > 0) continue;
            var plan = TinkerPlanner.Plan(bench, f, s, rows);
            if (plan.Count == 0) continue;
            list.Add(new TinkerRow
            {
                Id = f.Id, Name = f.Name, Tinks = f.Tinks, Workmanship = f.Workmanship, Imbued = f.Imbued,
                Plan = TinkerPlanner.Summary(plan),
            });
            if (list.Count >= batch) break;
        }
        _scan = list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _scannedBench = (int)bench;
        string hiddenText = hidden > 0 ? $"; {hidden} finished this session left out" : "";
        _status = (list.Count == 0
            ? (appraising > 0 ? $"nothing to do yet - appraising {appraising} item(s), scan again in a moment" : "nothing to do: no item matches the ticked salvage")
            : $"{list.Count} item(s) planned" + (appraising > 0 ? $" ({appraising} still being appraised)" : "")) + hiddenText;
        return appraising;
    }

    // ── Job control ─────────────────────────────────────────────────────────

    /// <summary>Starts on the last scan's rows (pump thread, after the tab's confirm).</summary>
    public void Start(TinkerBench bench)
    {
        if (Running) return;
        if (_scannedBench != (int)bench || _scan.Length == 0) { _status = "scan first"; return; }
        _bench = bench;
        _queue.Clear();
        _byId = _scan.ToDictionary(r => r.Id);
        foreach (var r in _scan) { _queue.Enqueue(r.Id); r.Status = "queued"; }
        _itemId = 0;
        _waiting = false;
        _prep = false;
        _nextAt = 0;
        _usedThisBatch = 0;
        _autoRescanAt = 0;
        _active = true;
        _status = $"working {_queue.Count} item(s)";
        RynthLog.Write(LogCat.Crafting, $"[Crafting] {TinkerCatalog.Label(bench)}: start, {_queue.Count} item(s)");
    }

    public void Stop(string why)
    {
        if (_waiting && Host.HasConfirmationAnswers) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
        if (_itemId != 0) SetRow(_itemId, "stopped");
        while (_queue.Count > 0) SetRow(_queue.Dequeue(), "not started");
        _itemId = 0;
        _waiting = false;
        _prep = false;
        _autoRescanAt = 0;
        _active = false;
        _status = why;
    }

    public void OnLogout()
    {
        Stop("idle");
        _scan = Array.Empty<TinkerRow>();
        _scannedBench = -1;
        foreach (var bench in Enum.GetValues<TinkerBench>())
        {
            DoneSet(bench).Clear();
            Volatile.Write(ref _doneCounts[(int)bench], 0);
        }
    }

    /// <summary>
    /// The queue ran out. With auto next batch on, scan the next items (the finished ones are left
    /// out) and start them if asked; a batch that used no bag at all ends it, so a pack of items
    /// that all lack salvage isn't walked batch after batch.
    /// </summary>
    private void BatchDone(long nowMs)
    {
        var bench = _bench;
        int used = _usedThisBatch;
        Stop($"done - {used} bag(s) used");
        RynthLog.Write(LogCat.Crafting, $"[Crafting] {TinkerCatalog.Label(bench)}: batch done, {used} bag(s) used");
        if (!C.AutoNextBatch) return;
        if (used == 0) { _status = "done - this batch used no bag, auto next batch stopped"; return; }
        _autoRescansLeft = AutoRescanTries;
        AutoScan(bench, nowMs);
    }

    private void AutoScan(TinkerBench bench, long nowMs)
    {
        int appraising = Scan(bench);
        if (_scan.Length == 0)
        {
            // The next items may only be waiting for their appraisal.
            if (appraising > 0 && _autoRescansLeft-- > 0)
            {
                _bench = bench;
                _autoRescanAt = nowMs + AutoRescanDelayMs;
            }
            else _status = "auto next batch: nothing left to do";
            return;
        }
        if (C.AutoStartNextBatch) Start(bench);
        else _status = $"next batch ready: {_scan.Length} item(s) - press Start";
    }

    private void SetRow(int id, string status)
    {
        if (_byId.TryGetValue(id, out var r)) r.Status = status;
    }

    private string AttemptKey(string rowKey) => _itemId + ":" + rowKey;

    private void NextItem(string doneStatus)
    {
        if (_itemId != 0)
        {
            SetRow(_itemId, doneStatus);
            MarkDone(_bench, _itemId);
        }
        _itemId = 0;
        _prepsThisItem = 0;
        _attempted.Clear();
        _skipped.Clear();
    }

    // ── Tick (pump thread) ──────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        if (!Running)
        {
            if (_autoRescanAt != 0 && nowMs >= _autoRescanAt)
            {
                _autoRescanAt = 0;
                AutoScan(_bench, nowMs);
            }
            return;
        }
        if (_waiting) { Wait(nowMs); return; }
        if (nowMs < _nextAt) return;

        if (!C.AllowInCombat && !Inv.InPeaceMode) { _status = "waiting for peace mode"; return; }
        if (Inv.IsBusy) return;
        if (!ActionPacer.MayStart(Host, "Crafting", HealNeed.None, out string why)) { _status = "waiting: " + why; return; }

        if (_itemId == 0)
        {
            if (_queue.Count == 0) { BatchDone(nowMs); return; }
            _itemId = _queue.Dequeue();
            _retries = 0;
        }

        var wo = Inv.Cache?[_itemId];
        if (wo == null) { NextItem("gone from the pack"); return; }
        if (Inv.IsEquipped(wo)) { NextItem("equipped - skipped"); return; }

        var s = Snapshot(_settings(_bench));
        var facts = Facts(wo);
        var plan = TinkerPlanner.Plan(_bench, facts, s, _rows(_bench),
                k => _attempted.TryGetValue(k, out int n) ? n : 0)
            .Where(st => !_skipped.Contains(TinkerCatalog.Key(st.Row)))
            .ToList();
        if (plan.Count == 0) { NextItem($"done ({facts.Tinks} tinks)"); return; }

        var step = plan[0];
        string key = TinkerCatalog.Key(step.Row);
        string matName = TinkerCatalog.MaterialName(step.Material);

        var bags = BagsFor(step);
        if (bags.Count == 0)
        {
            if (step.Row.Prepared) Prepare(nowMs, step, key, matName);
            else Skip(key, $"no full bag of {matName}");
            return;
        }

        int skill = Skill(step.Row.Skill > 0 ? step.Row.Skill : TinkerCatalog.DefaultSkill(_bench));
        var choices = bags.Select(b =>
        {
            double bagWork = TinkerMath.BagWorkmanship(Inv.Int(b, IntWorkmanship), Inv.Int(b, IntNumItemsInMaterial, 1));
            bool fool = TinkerPlanner.IsFoolproof(b.Name);
            double? chance = fool ? 1.0
                : step.Row.Origin == TinkerOrigin.Retail && skill > 0
                    ? TinkerMath.Chance(step.Material, skill, facts.Workmanship, bagWork, facts.Tinks,
                                        step.Row.Kind == TinkerKind.Imbue, false)
                    : null;
            return new TinkerBagChoice(b.Id, bagWork, chance);
        }).ToList();

        int min = Math.Clamp(s.MinChancePercent, 0, 100);
        int pick = TinkerPlanner.SelectBag(choices, min);
        if (pick < 0)
        {
            int best = choices.Max(ch => TinkerMath.Percent(ch.Chance ?? 0));
            Skip(key, $"{matName}: best chance {best}% is under {min}%");
            return;
        }

        var bag = bags[pick];
        _stepPercent = choices[pick].Chance is double pc ? TinkerMath.Percent(pc) : -1;
        _manual = !(C.AnswerCraftDialog && Host.HasConfirmationAnswers
                    && Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, min, (int)StepTimeoutMs) == 1);
        _confSeq = Host.TryGetLastConfirmation(out int cs, out _, out _, out _, out _) ? cs : 0;
        _errSeq = Host.TryGetLastWeenieError(out int es, out _, out _, out _) ? es : 0;

        string pctText = _stepPercent >= 0 ? $" ~{_stepPercent}%" : "";
        if (!Host.UseOnFor(unchecked((uint)bag.Id), unchecked((uint)_itemId), "Crafting",
                $"{TinkerCatalog.Label(_bench)}: {matName} on {facts.Name}{pctText}", UseKind.Asked))
        {
            if (!_manual) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
            Retry(nowMs, "the use request didn't go out");
            return;
        }

        _step = step;
        _bagId = bag.Id;
        _bagName = bag.Name ?? matName;
        _waiting = true;
        _stepAt = nowMs;
        _attempted[key] = (_attempted.TryGetValue(key, out int n0) ? n0 : 0) + 1;
        string ask = _manual ? " - click Yes in AC's craft dialog" : "";
        SetRow(_itemId, $"applying {matName}{pctText}{ask}");
        _status = $"{facts.Name}: applying {_bagName}{pctText}{ask}";
    }

    /// <summary>The full bags that can serve a step (prepared rows: prepared at the chosen tier).</summary>
    private List<WorldObject> BagsFor(TinkerStep step)
        => Inv.Items()
            .Where(b => b.ObjectClass == AcObjectClass.Salvage
                        && TinkerPlanner.BagMatches(step, b.Name, Inv.Int(b, MiniRemoteInventory.IntMaterialType), Inv.Uses(b), Tier))
            .ToList();

    /// <summary>
    /// No prepared bag: use a raw one on the tier's Ammunition (UB-IT's prep). The wait
    /// ends when a prepared bag shows up; the next tick then applies it.
    /// </summary>
    private void Prepare(long nowMs, TinkerStep step, string key, string matName)
    {
        string tierName = TinkerCatalog.TrinketTiers[Tier];
        if (!C.TrinketPrep) { Skip(key, $"no {tierName} {matName} bag (or tick 'prepare on Ammunition')"); return; }
        if (++_prepsThisItem > MaxRetries) { Skip(key, $"{matName}: no {tierName} bag came out of preparing"); return; }
        var raw = Inv.Items().FirstOrDefault(b => b.ObjectClass == AcObjectClass.Salvage
            && TinkerPlanner.IsRawTrinketBag(step.Material, b.Name, Inv.Int(b, MiniRemoteInventory.IntMaterialType), Inv.Uses(b)));
        if (raw == null) { Skip(key, $"no full bag of {matName} to prepare"); return; }
        var ammo = Inv.Items().FirstOrDefault(a => !Inv.IsEquipped(a) && TinkerPlanner.IsTierAmmunition(a.Name, Tier));
        if (ammo == null) { Skip(key, $"no {tierName} Ammunition to prepare {matName}"); return; }

        // Preparing is the player's choice (the tick box): any craft dialog it raises is a Yes.
        _manual = !(C.AnswerCraftDialog && Host.HasConfirmationAnswers
                    && Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, (int)PrepTimeoutMs) == 1);
        _confSeq = Host.TryGetLastConfirmation(out int cs, out _, out _, out _, out _) ? cs : 0;
        _errSeq = Host.TryGetLastWeenieError(out int es, out _, out _, out _) ? es : 0;
        _step = step;
        _prep = true;
        if (!Host.UseOnFor(unchecked((uint)raw.Id), unchecked((uint)ammo.Id), "Crafting",
                $"{TinkerCatalog.Label(_bench)}: prepare {matName} on {ammo.Name}", UseKind.Asked))
        {
            if (!_manual) Host.ArmConfirmation(RynthCoreHost.ConfirmationKind.CraftInteraction, 0, 0);
            Retry(nowMs, "the prepare request didn't go out");
            return;
        }
        _bagId = raw.Id;
        _bagName = raw.Name ?? matName;
        _waiting = true;
        _stepAt = nowMs;
        SetRow(_itemId, $"preparing {tierName} {matName}");
        _status = $"preparing {_bagName} on {ammo.Name}";
    }

    /// <summary>Buffed tinkering skill (0 when the engine can't read it: the server's percent decides).</summary>
    private int Skill(int skillId)
        => Host.HasGetObjectSkill && Host.TryGetObjectSkill(Inv.PlayerId, unchecked((uint)skillId), out int buffed, out _) ? buffed : 0;

    private void Skip(string rowKey, string why)
    {
        _skipped.Add(rowKey);
        SetRow(_itemId, why);
        _status = why;
    }

    private void Retry(long nowMs, string why)
    {
        _waiting = false;
        string key = TinkerCatalog.Key(_step.Row);
        // A preparation never counted as an attempt.
        if (!_prep && _attempted.TryGetValue(key, out int n) && n > 0) _attempted[key] = n - 1;
        _prep = false;
        if (++_retries > MaxRetries) { NextItem("gave up: " + why); _status = why; return; }
        _nextAt = nowMs + RetryDelayMs;
        _status = $"{why} - retrying ({_retries}/{MaxRetries})";
    }

    private void Finish(string outcome)
    {
        _waiting = false;
        _retries = 0;
        _nextAt = Environment.TickCount64 + StepGapMs;
        if (!_prep) _usedThisBatch++;
        _prep = false;
        SetRow(_itemId, outcome);
        _status = outcome;
    }

    /// <summary>The step's row was refused (No, an error, "cannot be used"): drop it for this item.</summary>
    private void Refuse(string why)
    {
        _waiting = false;
        _prep = false;
        Skip(TinkerCatalog.Key(_step.Row), why);
    }

    private void Wait(long nowMs)
    {
        // Engine answer (v81): a No ends the step without using the bag.
        if (Host.TryGetLastConfirmation(out int seq, out uint type, out int pct, out int outcome, out _)
            && seq != _confSeq && type == RynthCoreHost.ConfirmationKind.CraftInteraction)
        {
            _confSeq = seq;
            if (outcome == RynthCoreHost.ConfirmationOutcome.Declined)
            {
                Refuse($"{TinkerCatalog.MaterialName(_step.Material)}: server chance {pct}% is under the minimum");
                return;
            }
            if (outcome is RynthCoreHost.ConfirmationOutcome.Shown or RynthCoreHost.ConfirmationOutcome.SendFailed)
            {
                _manual = true;
                _status = $"click Yes in AC's craft dialog ({(pct >= 0 ? pct + "%" : "chance not shown")})";
            }
        }

        if (Host.TryGetLastWeenieError(out int es, out uint error, out _, out _) && es != _errSeq)
        {
            _errSeq = es;
            if (error == ErrorYoureTooBusy) { Retry(nowMs, "server says busy"); return; }
            Refuse($"server refused {TinkerCatalog.MaterialName(_step.Material)} (error 0x{error:X})");
            return;
        }

        if (_prep)
        {
            // Prepared: the tier bag is in the pack (or the raw bag is gone, and the next tick looks again).
            if (BagsFor(_step).Count > 0 || Inv.Cache?[_bagId] == null)
            {
                Finish($"{TinkerCatalog.TrinketTiers[Tier]} {TinkerCatalog.MaterialName(_step.Material)} prepared");
                return;
            }
            if (nowMs - _stepAt > (_manual ? ManualTimeoutMs : PrepTimeoutMs))
                Retry(nowMs, "the bag wasn't prepared");
            return;
        }

        // Every tinker consumes the bag, success or failure: covers server recipes with their own text.
        if (Inv.Cache?[_bagId] == null) { Finish($"{_bagName} used"); return; }

        if (nowMs - _stepAt > (_manual ? ManualTimeoutMs : StepTimeoutMs))
            Retry(nowMs, _manual ? "no answer to AC's craft dialog" : "no result from the server");
    }

    /// <summary>The craft result lines (never hidden).</summary>
    public void OnChat(string text)
    {
        if (!_waiting || string.IsNullOrEmpty(text)) return;
        string mat = TinkerCatalog.MaterialName(_step.Material);
        if (CannotUse.IsMatch(text))
        {
            Refuse(_prep ? $"{mat} can't be prepared on that Ammunition" : $"{mat} can't be used on this item");
            return;
        }
        // A preparation ends when the prepared bag shows up (Wait), not on a craft line.
        if (_prep) return;
        if (Applied.IsMatch(text)) Finish($"{mat} applied");
        else if (Failed.IsMatch(text)) Finish($"{mat} failed");
        else if (Foolproof.IsMatch(text)) Finish($"{mat} applied (foolproof)");
    }
}
