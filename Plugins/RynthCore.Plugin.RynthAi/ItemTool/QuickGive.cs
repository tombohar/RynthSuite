// ============================================================================
//  QuickGive.cs - the Item Tool's "Quick give".
//
//  Tom picks an item in his pack and a target (a player or an NPC) in the world, presses Give, and
//  every matching item in the packs is handed to that target, one give after another, until none
//  is left or Stop. A pure state machine over IQuickGiveEnv (the plugin implements it,
//  RynthAiPlugin.ItemTool.cs; the host tests fake it).
//
//  This is NOT the profile give (actiontrygiveprofile, ItemGiver\ files, /ra ig): that one is
//  locked to NPCs and a folder because a shared meta could run it. Quick give shares nothing with
//  it but AC's give action itself (GiveObjectTo, one whole object per call), and it only starts
//  from the Item Tool panel's button or a /ra itemtool command Tom types (the plugin checks the
//  source before Start).
//
//  Pacing: one give in flight at a time. The next goes after the last LANDED (the item left the
//  pack, or its stack shrank) plus PaceMs; a give with no answer in ConfirmMs, or one the server
//  refused (WeenieError / InventoryServerSaveFailed), counts as a refusal and the item is left out
//  for the rest of the run. MaxRefusals in a row stop the run.
//
//  Items are matched by exactly what Tom picked: the same weenie class id (when the engine can
//  read it) and the same name. Nothing else is ever given.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.ItemTool;

/// <summary>What the picked item is: its weenie class id (0 = unreadable) and its exact name.</summary>
internal readonly record struct GiveItemKey(uint Wcid, string Name);

internal interface IQuickGiveEnv
{
    long Now { get; }
    /// <summary>The target is in the object cache: its current name and distance in metres (-1 unknown).</summary>
    bool TargetLoaded(int targetId, out string name, out double meters);
    bool InCombat();
    /// <summary>Why a give can't go out now (the client is busy, the server owes an answer); null = free.</summary>
    string? HoldReason();
    /// <summary>The next carried (never worn) item matching <paramref name="key"/> that is not in <paramref name="skip"/>.</summary>
    bool NextItem(GiveItemKey key, ISet<int> skip, out int itemId, out int count);
    /// <summary>How many matching items are still carried (whole objects).</summary>
    int CountCarried(GiveItemKey key);
    /// <summary>The item left the pack, or its stack shrank below <paramref name="count"/>.</summary>
    bool Landed(int itemId, int count);
    /// <summary>AC's give action for the whole object. False when the engine refused to send it.</summary>
    bool Send(int itemId, int targetId);
    /// <summary>A count of the server's refusals so far (rises when one arrives).</summary>
    int RefusalSeq();
    void Say(string text);
    void Log(string text);
}

internal sealed class QuickGive
{
    public enum Phase { Idle, Pending, InFlight }

    /// <summary>The target must be this near to start; it ends the run if it gets farther than <see cref="StopMeters"/>.</summary>
    public const double StartMeters = 10, StopMeters = 15;
    public const long PaceMs = 350, ConfirmMs = 6000, RefuseBackoffMs = 1000, HoldMaxMs = 60_000, GoneMs = 1500;
    public const int MaxRefusals = 3, MaxSendFails = 5, MaxGives = 2000;
    private const long ThinkMs = 50;

    private readonly IQuickGiveEnv _env;
    private readonly HashSet<int> _skip = new();

    public QuickGive(IQuickGiveEnv env) => _env = env;

    public int ItemId { get; private set; }
    public GiveItemKey Item { get; private set; }
    public int TargetId { get; private set; }
    public string TargetName { get; private set; } = "";
    public Phase State { get; private set; } = Phase.Idle;
    public bool Running => State != Phase.Idle;
    /// <summary>Items (whole objects) the server took this run, and the units in them.</summary>
    public int Given { get; private set; }
    public int Units { get; private set; }
    public string LastResult { get; private set; } = "";

    private long _lastThink, _nextAt, _sentAt, _goneSince, _holdSince, _leftAt;
    private int _inFlightId, _inFlightCount, _refusalSeqAtSend, _refusals, _sendFails, _left;
    private string _holdWhy = "";

    public void SetItem(int id, GiveItemKey key) { if (!Running) { ItemId = id; Item = key; } }
    public void SetTarget(int id, string name) { if (!Running) { TargetId = id; TargetName = name; } }
    public void Clear() { if (!Running) { ItemId = 0; Item = default; TargetId = 0; TargetName = ""; LastResult = ""; } }

    public bool Start(out string why)
    {
        why = "";
        if (Running) { why = "Quick give is already running"; return false; }
        if (ItemId == 0 || Item.Name.Length == 0) { why = "pick an item first (select it in your pack, then 'Use selected as item')"; return Refuse(why); }
        if (TargetId == 0) { why = "pick a target first (select the player or NPC in the world, then 'Use selected as target')"; return Refuse(why); }
        if (!_env.TargetLoaded(TargetId, out string name, out double meters)) { why = $"{TargetName} isn't loaded (out of view?)"; return Refuse(why); }
        if (!string.Equals(name, TargetName, StringComparison.OrdinalIgnoreCase)) { why = $"the target changed ('{TargetName}' is now '{name}'); pick it again"; return Refuse(why); }
        if (meters < 0 || meters > StartMeters) { why = $"{TargetName} is {(meters < 0 ? "too far" : meters.ToString("0") + " m away")} (give range is {StartMeters:0} m); walk closer"; return Refuse(why); }
        if (_env.InCombat()) { why = "you are in a fight"; return Refuse(why); }
        int have = _env.CountCarried(Item);
        if (have == 0) { why = $"no '{Item.Name}' in your packs"; return Refuse(why); }

        Given = 0; Units = 0; _refusals = 0; _sendFails = 0; _goneSince = 0; _holdSince = 0; _nextAt = 0; _lastThink = 0; _left = have; _leftAt = _env.Now;
        _skip.Clear();
        LastResult = "";
        State = Phase.Pending;
        _env.Say($"[RynthAi] Quick give: giving every '{Item.Name}' ({have} in your packs) to {TargetName}. Stop: /ra itemtool givestop.");
        _env.Log($"[RynthAi] Quick give: start item '{Item.Name}' wcid 0x{Item.Wcid:X} x{have} -> 0x{(uint)TargetId:X8} '{TargetName}' ({meters:0.0} m)");
        return true;
    }

    private bool Refuse(string why)
    {
        _env.Say($"[RynthAi] Quick give: can't start - {why}.");
        return false;
    }

    public void Stop(string why = "stopped")
    {
        if (!Running) { _env.Say("[RynthAi] Quick give: not running."); return; }
        Finish(why);
    }

    private void Finish(string why)
    {
        State = Phase.Idle;
        _inFlightId = 0;
        LastResult = $"stopped: {why} ({Given} item(s), {Units} unit(s) given to {TargetName})";
        _env.Say($"[RynthAi] Quick give {LastResult}.");
        _env.Log($"[RynthAi] Quick give: finish - {why}; gave {Given} ({Units} units)");
    }

    public string Status
    {
        get
        {
            switch (State)
            {
                case Phase.Idle: return LastResult.Length > 0 ? $"Quick give {LastResult}." : "Idle.";
                default:
                    string hold = _holdWhy.Length > 0 ? $" (waiting: {_holdWhy})" : "";
                    return $"Giving '{Item.Name}' to {TargetName}: {Given} given ({Units} units), {_left} left{hold}.";
            }
        }
    }

    public void Tick()
    {
        if (State == Phase.Idle) return;
        long now = _env.Now;
        if (now - _lastThink < ThinkMs) return;
        _lastThink = now;

        // The target must stay the one Tom picked, loaded and near.
        if (!_env.TargetLoaded(TargetId, out string name, out double meters))
        {
            if (_goneSince == 0) _goneSince = now;
            else if (now - _goneSince >= GoneMs) Finish($"{TargetName} is gone");
            return;
        }
        _goneSince = 0;
        if (!string.Equals(name, TargetName, StringComparison.OrdinalIgnoreCase)) { Finish($"the target changed ('{TargetName}' is now '{name}')"); return; }
        if (meters < 0 || meters > StopMeters) { Finish($"{TargetName} is out of give range ({(meters < 0 ? "position unknown" : meters.ToString("0") + " m")})"); return; }
        if (_env.InCombat()) { Finish("a fight started"); return; }

        if (now - _leftAt >= 500) { _left = _env.CountCarried(Item); _leftAt = now; }

        if (State == Phase.InFlight) { TickInFlight(now); return; }
        TickPending(now);
    }

    private void TickPending(long now)
    {
        if (now < _nextAt) return;
        string? why = _env.HoldReason();
        if (why != null)
        {
            _holdWhy = why;
            if (_holdSince == 0) _holdSince = now;
            else if (now - _holdSince >= HoldMaxMs) Finish($"waited {HoldMaxMs / 1000} s: {why}");
            return;
        }
        _holdSince = 0;
        _holdWhy = "";

        if (Given + _refusals >= MaxGives) { Finish($"reached {MaxGives} gives"); return; }
        if (!_env.NextItem(Item, _skip, out int id, out int count))
        {
            Finish(Given > 0 ? "done - no more of that item" : "no more of that item to give");
            return;
        }
        _refusalSeqAtSend = _env.RefusalSeq();
        if (!_env.Send(id, TargetId))
        {
            if (++_sendFails >= MaxSendFails) Finish("the client wouldn't send the give");
            else _nextAt = now + RefuseBackoffMs;
            return;
        }
        _sendFails = 0;
        _inFlightId = id;
        _inFlightCount = count;
        _sentAt = now;
        State = Phase.InFlight;
    }

    private void TickInFlight(long now)
    {
        if (_env.Landed(_inFlightId, _inFlightCount))
        {
            Given++;
            Units += _inFlightCount;
            _skip.Add(_inFlightId);   // it is the target's now, whatever a stale inventory read still says
            _refusals = 0;
            _left = Math.Max(0, _left - 1);
            State = Phase.Pending;
            _nextAt = now + PaceMs;
            return;
        }
        if (_env.RefusalSeq() != _refusalSeqAtSend) { Refused(now, "the server refused it"); return; }
        if (now - _sentAt >= ConfirmMs) Refused(now, $"no answer in {ConfirmMs / 1000} s");
    }

    private void Refused(long now, string why)
    {
        _skip.Add(_inFlightId);
        _env.Log($"[RynthAi] Quick give: 0x{(uint)_inFlightId:X8} not given - {why} ({_refusals + 1} of {MaxRefusals} in a row)");
        _inFlightId = 0;
        if (++_refusals >= MaxRefusals)
        {
            Finish($"{MaxRefusals} refusals in a row (last: {why})");
            return;
        }
        State = Phase.Pending;
        _nextAt = now + RefuseBackoffMs;
    }
}
