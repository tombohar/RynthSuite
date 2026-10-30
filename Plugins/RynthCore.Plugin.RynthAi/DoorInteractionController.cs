// ============================================================================
//  DoorInteractionController.cs — partial class RynthAiPlugin
//
//  Detects closed doors while the macro is running and automatically opens them.
//  Optionally uses lockpicks from the Consumable Items list on a door the server
//  says is locked.
//
//  State machine:
//    None -> Pending (a use waits for its turn) -> Opening (the use is out) ->
//    Settling (the server answered; the door gets a moment to swing) -> done,
//    or back to Pending for another use, or Unlocking (a lockpick is out) -> Pending,
//    or Cooldown.
//
//  Two ways in: the proximity scan (OpenDoors setting, nearest closed door in
//  range), and a requested door (RequestDoorOpen): the nav engine's recovery
//  detour asks for the closed door on its next leg. A requested door is opened
//  even with OpenDoors off, since the detour was planned through that doorway.
//
//  The rules (2026-10-13, a mage on DreamWeave: "doors aren't opening"; 13 doors,
//  5 timeouts, 3 lockpick cooldowns with no locked door anywhere):
//    * One door use at a time, sent only when nothing else is waiting for the server
//      (ActionPacer.MayStart: no unanswered use/cast/stance change, no animation, the
//      stop before it settled) and the client isn't busy (up to DoorBusyWaitMaxMs:
//      the busy count only drives AC's cursor and reads 1 for seconds after a slow
//      cast or a portal, BusyCountHooks' watchdog clears it after 5 s). The old code
//      sent the use at once (busy=1) and again after 3 s with the first still
//      unanswered: on ACE a second use restarts the server's walk to the door, and a
//      use of a door that just opened closes it again.
//    * The server walks the character to the door before it opens it: the answer
//      (UseDone) took 0.3-3.7 s in that log, so the use is given DoorAnswerMaxMs
//      before it counts as lost. A door that reads open ends it at once, whatever
//      the wait, so a door that opens is never waited on.
//    * "Locked" only when the server says so: ACE answers a locked door with the
//      transient text "The door is locked!" (Door.ActOnUse), which RynthAi gets as a
//      server message (API v78) or a chat line. A door still closed after an answered
//      use is used again (DoorMaxAttempts), never picked.
//    * Nothing while in portal space or for DoorPostPortalMs after it: two of the
//      log's false "locked" doors were targeted inside portal space (busy=1 from the
//      portal), their uses lost to the teleport.
// ============================================================================

using System;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private int _doorTargetId;
    private long _doorActionAt;          // when the current state started (Pending: when the use was wanted)
    private int _doorAttempts;           // door uses that came back with the door still closed
    private int _doorPicks;              // lockpicks used on the current door
    private DoorState _doorState = DoorState.None;
    private long _doorLastDiagAt;
    private int _doorRequestedId;        // asked for by the nav recovery detour; taken on the next idle tick
    private bool _doorTargetRequested;   // the current target came from a request
    private bool _doorWantLockpick;      // Pending sends a lockpick (the server said locked), not an open
    private long _doorSentAt;            // when the last door use / lockpick went out
    private int _doorSeqAtSend;          // the UseDone count read just before it went out
    private uint _doorAnswerErr;         // the error of the UseDone that answered it (0 = done)
    private bool _doorWaitLogged;        // one "waits" line per Pending
    private long _doorLastPortalAt = long.MinValue / 2;

    // "The door is locked!" (ACE Door.ActOnUse, a transient string): when it last arrived.
    private long _doorLockedSaidAt = long.MinValue / 2;

    // Doors we've already opened — skip for 60s so we don't re-target them
    private readonly System.Collections.Generic.Dictionary<int, long> _doorOpenedRecently = new();
    // Locked doors we can't pick (no lockpick, Auto Unlock off, out of picks) — skip a while
    private readonly System.Collections.Generic.Dictionary<int, long> _doorLockedRecently = new();

    /// <summary>Clock (ms) for the door logic; tests replace it.</summary>
    internal static Func<long> DoorClock = static () => Environment.TickCount64;

    /// <summary>A use with no answer this long is taken as lost (the longest answer in the 10-13 log was 3.7 s;
    /// ActionPacer gives a world use 5 s).</summary>
    private const long DoorAnswerMaxMs = 6000;
    /// <summary>After the server's answer, the door gets this long to read open before the use counts as failed.</summary>
    private const long DoorOpenSettleMs = 1200;
    /// <summary>A use waits for the client's busy count to read 0 at most this long (see the header).</summary>
    private const long DoorBusyWaitMaxMs = 2500;
    /// <summary>A use that can't go out this long (something else keeps the server busy) gives up for now.</summary>
    private const long DoorSendWaitMaxMs = 10000;
    /// <summary>No door is targeted for this long after portal space.</summary>
    private const long DoorPostPortalMs = 1500;
    private const long DoorCooldownMs = 5000;
    private const long DoorOpenedSkipMs = 60000;
    private const long DoorLockedSkipMs = 30000;
    private const int DoorMaxAttempts = 3;
    private const int DoorMaxPicks = 3;
    private const uint DoorTooBusyErr = 0x1D;
    private const float YardsToMeters = 0.9144f;

    // ACE: "The door is locked!"; other servers name the door ("The Gate is locked."). Server text only.
    private static readonly Regex DoorLockedText = new(@"^\s*the\s.+\sis\s+locked\s*[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private enum DoorState
    {
        None,
        Pending,
        Opening,
        Settling,
        Unlocking,
        Cooldown
    }

    /// <summary>
    /// Called each tick when the macro is running.
    /// Returns true if door interaction is blocking navigation.
    /// </summary>
    private bool TickDoorInteraction()
    {
        var settings = _dashboard?.Settings;
        bool requested = _doorRequestedId != 0 || _doorTargetRequested;
        if (settings == null || !settings.IsMacroRunning || (!settings.OpenDoors && !requested))
        {
            ResetDoorState();
            _doorRequestedId = 0;
            return false;
        }

        long now = DoorClock();

        PruneDoorSkips(_doorOpenedRecently, now, DoorOpenedSkipMs);
        PruneDoorSkips(_doorLockedRecently, now, DoorLockedSkipMs);

        // Portal space: the server drops a use sent now, and the client's busy count reads 1 for the
        // portal. Whatever was under way is forgotten; nothing new starts until a moment after it.
        if (ActionPacer.IsPortaling(Host))
        {
            _doorLastPortalAt = now;
            if (_doorState != DoorState.None)
            {
                Host.Log($"[RynthAi] Door: portal space - dropping 0x{(uint)_doorTargetId:X8} (the server drops a use sent now)");
                ResetDoorState();
            }
            return false;
        }

        // ── Idle: scan for a nearby closed door ─────────────────────────────
        if (_doorState == DoorState.None)
        {
            if (now - _doorLastPortalAt < DoorPostPortalMs) return false;

            int doorId = 0;
            if (_doorRequestedId != 0)
            {
                int req = _doorRequestedId;
                _doorRequestedId = 0;
                if (!IsDoorOpen(req) && !_doorLockedRecently.ContainsKey(req))
                {
                    doorId = req;
                    _doorTargetRequested = true;
                }
            }
            if (doorId == 0 && settings.OpenDoors)
                doorId = settings.OpenDoorsOnPathOnly
                    ? FindClosedDoorOnPath(settings.OpenDoorRange, now)
                    : FindNearestClosedDoor(settings.OpenDoorRange, now);
            if (doorId == 0) return false;

            string why = _doorTargetRequested ? " (asked for by the nav detour)" : "";
            Host.Log($"[RynthAi] Door: targeting 0x{(uint)doorId:X8}{why} (state={GetDoorStateDebug(doorId)}, busy={_busyCount})");
            _doorTargetId = doorId;
            _doorAttempts = 0;
            _doorPicks = 0;
            _doorWantLockpick = false;

            _navigationEngine?.Stop();
            if (Host.HasStopCompletely) Host.StopCompletelyBy("Door");

            // The use goes once the stop has settled and nothing else waits for the server.
            EnterDoorPending(now);
            return true;
        }

        switch (_doorState)
        {
            case DoorState.Pending:
                return TickDoorPending(settings, now);

            case DoorState.Opening:
            {
                if (IsDoorOpen(_doorTargetId))
                {
                    Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} opened OK after {now - _doorSentAt} ms — ready for next");
                    MarkDoorOpenedAndReset(now);
                    return false;
                }
                if (DoorSaidLocked())
                    return OnDoorLocked(settings, now);
                if (DoorUseAnswered(out uint err))
                {
                    _doorAnswerErr = err;
                    _doorState = DoorState.Settling;
                    _doorActionAt = now;
                    return true;
                }
                if (now - _doorSentAt >= DoorAnswerMaxMs)
                {
                    Host.Log($"[RynthAi] Door: no answer from the server to the use of 0x{(uint)_doorTargetId:X8} in {now - _doorSentAt} ms (busy={_busyCount})");
                    return DoorUseFailed(now, "the server never answered the use");
                }
                return true;
            }

            case DoorState.Settling:
            {
                if (IsDoorOpen(_doorTargetId))
                {
                    Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} opened OK after {now - _doorSentAt} ms — ready for next");
                    MarkDoorOpenedAndReset(now);
                    return false;
                }
                if (DoorSaidLocked())
                    return OnDoorLocked(settings, now);
                if (_doorAnswerErr == DoorTooBusyErr)
                    return DoorUseFailed(now, "the server answered 'too busy' (0x1D)");
                if (now - _doorActionAt < DoorOpenSettleMs) return true;
                Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} not open {now - _doorSentAt} ms after the use ({GetDoorStateDebug(_doorTargetId)}, answer err=0x{_doorAnswerErr:X}); no 'locked' from the server");
                return DoorUseFailed(now, "still closed after the server's answer");
            }

            case DoorState.Unlocking:
            {
                if (IsDoorOpen(_doorTargetId))
                {
                    Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} unlocked & opened OK — ready for next");
                    MarkDoorOpenedAndReset(now);
                    return false;
                }
                bool answered = DoorUseAnswered(out _);
                if (!answered && now - _doorSentAt < DoorAnswerMaxMs) return true;
                Host.Log(answered
                    ? $"[RynthAi] Door: lockpick answered, opening 0x{(uint)_doorTargetId:X8} again"
                    : $"[RynthAi] Door: no answer to the lockpick in {now - _doorSentAt} ms, opening 0x{(uint)_doorTargetId:X8} again");
                _doorWantLockpick = false;
                EnterDoorPending(now);
                return true;
            }

            case DoorState.Cooldown:
                if (now - _doorActionAt >= DoorCooldownMs)
                {
                    Host.Log($"[RynthAi] Door: cooldown done for 0x{(uint)_doorTargetId:X8}, resetting");
                    ResetDoorState();
                }
                return _doorState != DoorState.None;
        }

        return false;
    }

    /// <summary>A door use (or a lockpick) is wanted: it goes when <see cref="TickDoorPending"/> lets it.</summary>
    private void EnterDoorPending(long now)
    {
        _doorState = DoorState.Pending;
        _doorActionAt = now;
        _doorWaitLogged = false;
    }

    /// <summary>Sends the wanted use once nothing else waits for the server; true while the door holds nav.</summary>
    private bool TickDoorPending(LegacyUiSettings settings, long now)
    {
        // Open already (another player, or the last use's swing showed late): never use it again,
        // a use of an open door closes it.
        if (IsDoorOpen(_doorTargetId))
        {
            Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} reads open — ready for next");
            MarkDoorOpenedAndReset(now);
            return false;
        }

        long waited = now - _doorActionAt;
        if (!DoorMayUseNow(waited, out string why))
        {
            if (waited >= DoorSendWaitMaxMs)
            {
                Host.Log($"[RynthAi] Door: couldn't use 0x{(uint)_doorTargetId:X8} for {waited} ms ({why}), cooldown");
                EnterDoorCooldown(now);
                return true;
            }
            if (!_doorWaitLogged)
            {
                _doorWaitLogged = true;
                Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} waits: {why}");
            }
            return true;
        }

        int seqBefore = ActionPacer.UseDoneSeqNow(Host);
        bool sent;
        if (_doorWantLockpick)
        {
            var lockpick = FindLockpickInConsumables();
            if (lockpick == null)
            {
                Host.Log("[RynthAi] Door: the lockpick is gone, cooldown");
                EnterDoorCooldown(now, locked: true);
                return true;
            }
            Host.Log($"[RynthAi] Door: using lockpick {lockpick.Name} on 0x{(uint)_doorTargetId:X8}");
            sent = Host.UseOnFor(unchecked((uint)lockpick.Id), unchecked((uint)_doorTargetId), "Door", "Auto Unlock Doors: the server says this door is locked");
            if (sent) { _doorPicks++; _doorState = DoorState.Unlocking; }
        }
        else
        {
            string reason = _doorAttempts > 0
                ? $"try {_doorAttempts + 1}: still closed after the last use"
                : _doorPicks > 0 ? "open after the lockpick"
                : _doorTargetRequested ? "the nav detour asked to open this door"
                : settings.OpenDoorsOnPathOnly ? $"Open Doors: closed door on the path, within {settings.OpenDoorRange:0} yd"
                : $"Open Doors: closed door within {settings.OpenDoorRange:0} yd";
            sent = Host.UseFor(unchecked((uint)_doorTargetId), "Door", reason);
            if (sent) _doorState = DoorState.Opening;
        }

        if (!sent)
        {
            // Held (salvage panel), or the engine refused it: try again on a later tick.
            if (waited >= DoorSendWaitMaxMs)
            {
                Host.Log($"[RynthAi] Door: the use of 0x{(uint)_doorTargetId:X8} didn't go out for {waited} ms, cooldown");
                EnterDoorCooldown(now);
            }
            return true;
        }

        _doorSentAt = now;
        _doorActionAt = now;
        _doorSeqAtSend = seqBefore;
        _doorAnswerErr = 0;
        _doorLockedSaidAt = long.MinValue / 2;   // only a "locked" answer to THIS use counts
        return true;
    }

    /// <summary>May a door use go out now? No while another server action is unanswered, the character
    /// animates or its stop hasn't settled (ActionPacer), nor while the client reads busy (up to
    /// DoorBusyWaitMaxMs).</summary>
    private bool DoorMayUseNow(long waited, out string why)
    {
        if (!ActionPacer.MayStart(Host, "Door", HealNeed.None, out why))
            return false;
        if (_busyCount > 0 && waited < DoorBusyWaitMaxMs)
        {
            why = $"the client is busy ({_busyCount})";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>The server answered the last door use / lockpick (a UseDone since it went out); err = its error.</summary>
    private bool DoorUseAnswered(out uint err)
    {
        err = 0;
        try
        {
            if (!Host.HasUseDoneSeq)
                return DoorClock() - _doorSentAt >= ActionPacer.NoUseDoneFallbackMs;   // no UseDone on this engine
            int seq = Host.GetUseDoneSeq();
            if (seq == _doorSeqAtSend) return false;
            if (Host.TryGetLastUseDone(out int ls, out uint le) && ls == _doorSeqAtSend + 1) err = le;
            return true;
        }
        catch { return false; }
    }

    /// <summary>The server said "locked" since the last door use went out.</summary>
    private bool DoorSaidLocked() => _doorLockedSaidAt >= _doorSentAt;

    /// <summary>A door use came back with the door still closed: use it again, or give up for now.</summary>
    private bool DoorUseFailed(long now, string why)
    {
        _doorAttempts++;
        if (_doorAttempts >= DoorMaxAttempts)
        {
            Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} still closed after {_doorAttempts} uses ({why}), cooldown");
            EnterDoorCooldown(now);
            return true;
        }
        Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} {why} - using it again (try {_doorAttempts + 1} of {DoorMaxAttempts})");
        EnterDoorPending(now);
        return true;
    }

    /// <summary>The server said the door is locked: a lockpick if allowed and carried, else leave it for a while.</summary>
    private bool OnDoorLocked(LegacyUiSettings settings, long now)
    {
        if (!settings.AutoUnlockDoors)
        {
            Host.Log($"[RynthAi] Door: the server says 0x{(uint)_doorTargetId:X8} is locked; Auto Unlock Doors is off, cooldown");
            EnterDoorCooldown(now, locked: true);
            return true;
        }
        if (_doorPicks >= DoorMaxPicks)
        {
            Host.Log($"[RynthAi] Door: 0x{(uint)_doorTargetId:X8} still locked after {_doorPicks} lockpick(s), cooldown");
            EnterDoorCooldown(now, locked: true);
            return true;
        }
        var lockpick = FindLockpickInConsumables();
        if (lockpick == null)
        {
            Host.Log($"[RynthAi] Door: the server says 0x{(uint)_doorTargetId:X8} is locked but there's no lockpick in consumables, cooldown");
            EnterDoorCooldown(now, locked: true);
            return true;
        }
        Host.Log($"[RynthAi] Door: the server says 0x{(uint)_doorTargetId:X8} is locked - lockpick next");
        _doorWantLockpick = true;
        EnterDoorPending(now);
        return true;
    }

    private void EnterDoorCooldown(long now, bool locked = false)
    {
        if (locked && _doorTargetId != 0) _doorLockedRecently[_doorTargetId] = now;
        _doorState = DoorState.Cooldown;
        _doorActionAt = now;
    }

    /// <summary>
    /// Server text (a transient string from the server-message stream, or a chat line): remembers a
    /// "The door is locked!" for the door logic. Called from both, so whichever the client delivers
    /// first counts.
    /// </summary>
    internal void NoteDoorServerText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 120) return;
        if (!DoorLockedText.IsMatch(text)) return;
        _doorLockedSaidAt = DoorClock();
        if (_doorState is DoorState.Opening or DoorState.Settling)
            Host.Log($"[RynthAi] Door: server says \"{text.Trim()}\" (0x{(uint)_doorTargetId:X8})");
    }

    private static void PruneDoorSkips(System.Collections.Generic.Dictionary<int, long> skips, long now, long forMs)
    {
        if (skips.Count == 0) return;
        foreach (int key in skips.Where(kv => now - kv.Value >= forMs).Select(kv => kv.Key).ToList())
            skips.Remove(key);
    }

    private void MarkDoorOpenedAndReset(long now)
    {
        _doorOpenedRecently[_doorTargetId] = now;
        ResetDoorState();
    }

    private void ResetDoorState()
    {
        _doorTargetId = 0;
        _doorState = DoorState.None;
        _doorAttempts = 0;
        _doorPicks = 0;
        _doorWantLockpick = false;
        _doorTargetRequested = false;
    }

    /// <summary>
    /// Nav recovery: open this door on the next tick, whether or not OpenDoors is on.
    /// Ignored while another door is being handled (the detour asks again later).
    /// </summary>
    private void RequestDoorOpen(int doorId)
    {
        if (doorId != 0 && _doorState == DoorState.None) _doorRequestedId = doorId;
    }

    /// <summary>
    /// Nav recovery: the closed door nearest the start of the leg (ax, ay) to (bx, by), in
    /// world units, within <paramref name="corridor"/> of it and about the same height as
    /// <paramref name="z"/>; 0 when there is none. Doors are static objects with BF_DOOR
    /// (or a door-like name when the bitfield isn't available).
    /// </summary>
    private int FindClosedDoorOnLeg(double ax, double ay, double bx, double by, double z, double corridor)
    {
        if (_objectCache == null || !Host.HasGetObjectPosition) return 0;
        bool hasBitfield = Host.HasGetObjectBitfield;
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;

        int bestId = 0;
        double bestT = double.MaxValue;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (uid >= 0x80000000u) continue;
            if (hasBitfield)
            {
                if (!Host.TryGetObjectBitfield(uid, out uint bf) || (bf & 0x1000u) == 0) continue;
            }
            else
            {
                string nl = (wo.Name ?? string.Empty).ToLowerInvariant();
                if (!nl.Contains("door") && !nl.Contains("gate") && !nl.Contains("hatch") && !nl.Contains("portcullis")) continue;
            }
            if (IsDoorOpen(wo.Id)) continue;
            if (!Host.TryGetObjectPosition(uid, out uint cell, out float x, out float y, out float oz)) continue;
            if (Math.Abs(oz - z) > 4.0) continue;

            double px = ((cell >> 24) & 0xFF) * 192.0 + x;
            double py = ((cell >> 16) & 0xFF) * 192.0 + y;
            double t = len2 < 1e-6 ? 0.0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0.0, 1.0);
            double cx = ax + dx * t - px, cy = ay + dy * t - py;
            if (cx * cx + cy * cy > corridor * corridor) continue;
            if (t < bestT) { bestT = t; bestId = wo.Id; }
        }
        return bestId;
    }

    // ── Only doors on the path of travel ─────────────────────────────────────

    /// <summary>A door counts as on the path when it stands this close (world units, about yards) to it.</summary>
    private const double PathDoorCorridorYd = 1.5;
    private const int PathDoorMaxPoints = 12;

    /// <summary>
    /// The path of travel from the character, in world units, as far as <paramref name="rangeYd"/> yards:
    /// along the route's next points when the macro is navigating a route (in the direction it is walking it),
    /// else straight ahead along the character's heading (a RynthNav walk, or no nav at all).
    /// </summary>
    private System.Collections.Generic.List<(double X, double Y)> PathAhead(RynthCore.Plugin.RynthAi.LegacyUi.LegacyUiSettings s, double ax, double ay, float qw, float qz, double rangeYd)
    {
        var path = new System.Collections.Generic.List<(double X, double Y)> { (ax, ay) };
        var route = s.CurrentRoute;
        if (s.EnableNavigation && route != null && route.Points.Count > 0 && route.RouteType != NavRouteType.Follow)
        {
            int n = route.Points.Count, idx = s.ActiveNavIndex;
            int dir = route.RouteType == NavRouteType.Linear ? (_navigationEngine?.GetStateSnapshot().LinearDir ?? 1) : 1;
            double used = 0, cx = ax, cy = ay;
            for (int k = 0; k < PathDoorMaxPoints && idx >= 0 && idx < n && used < rangeYd; k++)
            {
                var pt = route.Points[idx];
                if (pt.NS != 0 || pt.EW != 0)
                {
                    double wx = (pt.EW * 10.0 + 1019.5) * 24.0, wy = (pt.NS * 10.0 + 1019.5) * 24.0;   // nav coordinates to world units
                    double leg = Math.Sqrt((wx - cx) * (wx - cx) + (wy - cy) * (wy - cy));
                    path.Add((wx, wy)); used += leg; cx = wx; cy = wy;
                }
                idx = route.RouteType == NavRouteType.Circular ? (idx + 1) % n : idx + dir;
            }
            if (path.Count > 1) return path;
        }
        // Straight ahead (physics yaw is counter-clockwise from north).
        double yaw = 2.0 * Math.Atan2(qz, qw);
        path.Add((ax - Math.Sin(yaw) * rangeYd, ay + Math.Cos(yaw) * rangeYd));
        return path;
    }

    /// <summary>
    /// Open Doors with "Only Doors On My Path": the closed door nearest the character along the path of
    /// travel (<see cref="PathAhead"/>), within <paramref name="rangeYards"/> of path, never one that merely
    /// stands within range. Same door rules as the proximity scan (static BF_DOOR objects, none opened or
    /// found locked a moment ago, none already open, about the character's height).
    /// </summary>
    private int FindClosedDoorOnPath(float rangeYards, long now)
    {
        var s = _dashboard?.Settings;
        if (s == null || _objectCache == null || !Host.HasGetObjectPosition) return 0;
        if (!Host.TryGetPlayerPose(out uint cell, out float lx, out float ly, out float lz, out float qw, out _, out _, out float qz)) return 0;
        double ax = ((cell >> 24) & 0xFF) * 192.0 + lx, ay = ((cell >> 16) & 0xFF) * 192.0 + ly;
        var path = PathAhead(s, ax, ay, qw, qz, rangeYards);
        bool hasBitfield = Host.HasGetObjectBitfield;

        int bestId = 0;
        double bestAlong = double.MaxValue;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            if (uid >= 0x80000000u) continue;
            if (hasBitfield)
            {
                if (!Host.TryGetObjectBitfield(uid, out uint bf) || (bf & 0x1000u) == 0) continue;
            }
            else
            {
                string nl = (wo.Name ?? string.Empty).ToLowerInvariant();
                if (!nl.Contains("door") && !nl.Contains("gate") && !nl.Contains("hatch") && !nl.Contains("portcullis")) continue;
            }
            if (_doorOpenedRecently.TryGetValue(wo.Id, out long openedAt) && (now - openedAt) < DoorOpenedSkipMs) continue;
            if (_doorLockedRecently.ContainsKey(wo.Id)) continue;
            if (IsDoorOpen(wo.Id)) continue;
            if (!Host.TryGetObjectPosition(uid, out uint dcell, out float x, out float y, out float oz)) continue;
            if (Math.Abs(oz - lz) > 4.0) continue;

            double px = ((dcell >> 24) & 0xFF) * 192.0 + x, py = ((dcell >> 16) & 0xFF) * 192.0 + y;
            double along = 0;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                double x1 = path[i].X, y1 = path[i].Y, dx = path[i + 1].X - x1, dy = path[i + 1].Y - y1;
                double len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
                double t = len2 < 1e-6 ? 0.0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / len2, 0.0, 1.0);
                double ex = x1 + dx * t - px, ey = y1 + dy * t - py;
                if (ex * ex + ey * ey <= PathDoorCorridorYd * PathDoorCorridorYd)
                {
                    double a = along + len * t;
                    if (a <= rangeYards + PathDoorCorridorYd && a < bestAlong) { bestAlong = a; bestId = wo.Id; }
                    break;
                }
                along += len;
            }
        }
        return bestId;
    }

    private int FindNearestClosedDoor(float rangeYards, long now)
    {
        if (_objectCache == null || _playerId == 0) return 0;

        float rangeMeters = rangeYards * YardsToMeters;
        int bestId = 0;
        double bestDist = double.MaxValue;
        bool hasBitfield = Host.HasGetObjectBitfield;

        int totalLandscape = 0, doorsFound = 0, openSkipped = 0, recentSkipped = 0, lockedSkipped = 0;

        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            uint uid = unchecked((uint)wo.Id);
            totalLandscape++;

            // Doors have static GUIDs (< 0x80000000)
            if (uid >= 0x80000000u) continue;

            // Primary: BF_DOOR bitfield check
            if (hasBitfield)
            {
                if (!Host.TryGetObjectBitfield(uid, out uint bf) || (bf & 0x1000u) == 0)
                    continue;
            }
            else
            {
                string nl = (wo.Name ?? string.Empty).ToLowerInvariant();
                if (!nl.Contains("door") && !nl.Contains("gate") && !nl.Contains("hatch") && !nl.Contains("portcullis"))
                    continue;
            }

            doorsFound++;

            // Skip doors we recently opened
            if (_doorOpenedRecently.TryGetValue(wo.Id, out long openedAt) && (now - openedAt) < DoorOpenedSkipMs)
            {
                recentSkipped++;
                continue;
            }

            // Skip locked doors we couldn't pick
            if (_doorLockedRecently.ContainsKey(wo.Id))
            {
                lockedSkipped++;
                continue;
            }

            // Skip already-open doors
            if (IsDoorOpen(wo.Id))
            {
                openSkipped++;
                continue;
            }

            double dist = _objectCache.Distance(unchecked((int)_playerId), wo.Id);
            if (dist > rangeMeters || dist >= bestDist) continue;

            bestDist = dist;
            bestId = wo.Id;
        }

        // Periodic diagnostic when doors are nearby but none targeted
        if (bestId == 0 && doorsFound > 0 && (now - _doorLastDiagAt) >= 5000)
        {
            _doorLastDiagAt = now;
            Host.Log($"[RynthAi] Door scan: {totalLandscape} landscape, {doorsFound} doors, {openSkipped} open, {recentSkipped} recent-skip, {lockedSkipped} locked-skip, range={rangeMeters:F1}m");
        }

        return bestId;
    }

    private bool IsDoorOpen(int doorId)
    {
        uint uid = unchecked((uint)doorId);

        // Primary: read CPhysicsObj::m_state directly — ETHEREAL_PS (0x4) = open
        if (Host.HasGetObjectState && Host.TryGetObjectState(uid, out uint physState))
            return (physState & 0x4u) != 0;

        // Fallback: DoMotionHooks tracks MotionOn/MotionOff per object
        if (Host.HasGetObjectMotionOn && Host.TryGetObjectMotionOn(uid, out bool isOn))
            return isOn;

        return false;
    }

    private string GetDoorStateDebug(int doorId)
    {
        uint uid = unchecked((uint)doorId);
        if (Host.HasGetObjectState && Host.TryGetObjectState(uid, out uint ps))
            return $"phys=0x{ps:X8} ethereal={(ps & 0x4u) != 0}";
        if (Host.HasGetObjectMotionOn && Host.TryGetObjectMotionOn(uid, out bool on))
            return $"motionOn={on}";
        return "no-state";
    }

    /// <summary>The first listed lockpick still carried: its own stack, else one of the same name (ConsumableLookup).</summary>
    private WorldObject? FindLockpickInConsumables()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null) return null;

        foreach (var r in settings.ConsumableRules)
            if (r.Type.Equals("Lockpick", StringComparison.OrdinalIgnoreCase)
                && ConsumableLookup.Resolve(_objectCache, r) is WorldObject pick)
                return pick;
        return null;
    }
}
