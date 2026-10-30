using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>What <see cref="RynthAiPlugin.MatchOwnDeathCorpse"/> made of a corpse.</summary>
internal enum OwnCorpseMatch
{
    /// <summary>Not your death corpse.</summary>
    No,
    /// <summary>Named "Corpse of &lt;you&gt;", but its ID (the "Killed by" line) hasn't landed yet.</summary>
    Pending,
    /// <summary>Your death corpse.</summary>
    Yes,
}

/// <summary>
/// Own-corpse recovery (owner, 2026-10-05: "It needs to loot your corpse and every item on it.
/// If it's a setting default to true please").
///
/// Recover My Corpse (LootOwnCorpse, default on): when your own death corpse is within loot range
/// the looter opens it and takes every item on it, one at a time with the normal looter's
/// pick-up confirmation, ignoring the loot profile, Loot From, value and salvage rules and the
/// weapon-swap free-slot reserve (only "no pack has a slot at all" stops it). It beats any other
/// corpse in range but not combat (the arbiter's Looting slot, loot starvation as usual).
///
/// It is identified as: name exactly "Corpse of &lt;your name&gt;" (ACE names a player's corpse
/// "Corpse of " + the player's full name, GM sigil included; character names are unique per
/// server, so no other player's corpse can carry it and a pet's corpse carries the pet's name),
/// AND an identified LongDesc that is the server's "Killed by X." line with X not you (a death
/// corpse is never killed by its own victim, so a namesake monster you killed is not it). The
/// corpse's owner (the Victim instance id) is not sent to clients by ACE, so it can't be checked.
/// Until the ID lands a corpse with your name is held, never looted by the profile meanwhile.
///
/// The death spot (position at the death) is remembered and logged, and with Travel Back To My
/// Corpse (TravelToOwnCorpse, default off) the bot walks back there with RynthNav after the
/// respawn: /rnav go &lt;coords&gt; through the engine's plugin-command broker, the way RynthRemote's
/// Travel tab does. RynthNav routes on the map only, so this works for outdoor deaths only.
/// RynthNav walks on its own and does not fight, so whenever RynthAi wants the tick (combat,
/// buffing, looting, salvage) the walk is stopped and RynthAi does that; it resumes once RynthAi
/// is idle again. Gives up on no progress or a timeout, and says why.
/// </summary>
public sealed partial class RynthAiPlugin
{
    internal const string OwnCorpsePrefix = "Corpse of ";

    // ── Identifying it ───────────────────────────────────────────────────────

    /// <summary>
    /// Is this your death corpse? Pure (tests call it directly). <paramref name="myName"/> is the
    /// player object's name as the client has it (with a GM sigil if any, as the corpse name has).
    /// </summary>
    internal static OwnCorpseMatch MatchOwnDeathCorpse(string? corpseName, string? myName, string? longDesc)
    {
        if (string.IsNullOrWhiteSpace(myName) || string.IsNullOrEmpty(corpseName))
            return OwnCorpseMatch.No;
        if (!string.Equals(corpseName, OwnCorpsePrefix + myName, StringComparison.Ordinal))
            return OwnCorpseMatch.No;
        if (string.IsNullOrWhiteSpace(longDesc))
            return OwnCorpseMatch.Pending;
        // The server writes exactly "Killed by X." on every corpse it makes for a death.
        if (!longDesc.TrimStart().StartsWith("Killed by ", StringComparison.OrdinalIgnoreCase))
            return OwnCorpseMatch.No;
        string? killer = ExtractCorpseKillerName(longDesc);
        if (string.IsNullOrWhiteSpace(killer))
            return OwnCorpseMatch.No;
        if (NormalizeCharName(killer).Equals(NormalizeCharName(myName), StringComparison.OrdinalIgnoreCase))
            return OwnCorpseMatch.No;   // your own kill of a namesake, not your death corpse
        return OwnCorpseMatch.Yes;
    }

    /// <summary>Recover My Corpse is on and this corpse id is your identified death corpse.</summary>
    private bool IsOwnDeathCorpseId(LegacyUiSettings? settings, int corpseId)
    {
        if (settings == null || !settings.LootOwnCorpse || corpseId == 0 || _objectCache == null)
            return false;
        WorldObject? corpse = _objectCache[corpseId];
        if (corpse == null)
            return false;
        return MatchOwnDeathCorpse(corpse.Name, GetPlayerNameForLootOwnership(),
            corpse.Values(StringValueKey.LongDesc, string.Empty)) == OwnCorpseMatch.Yes;
    }

    // A corpse with your name waiting for its ID: asked again every OwnCorpseIdRetryMs (the
    // general corpse ID request goes out once per corpse; a lost answer must not strand it).
    private readonly Dictionary<int, long> _ownCorpseIdRequestedAt = new();
    private const long OwnCorpseIdRetryMs = 5_000;

    private void RequestOwnCorpseId(int corpseId)
    {
        long now = CorpseNowMs;
        if (!_ownCorpseIdRequestedAt.TryGetValue(corpseId, out long last))
        {
            _ownCorpseIdRequestedAt[corpseId] = now;   // the general request just went out
            LootDiag($"[RynthAi] Own corpse: 0x{(uint)corpseId:X8} is named like yours; waiting for its ID to confirm it.");
            return;
        }
        if (now - last < OwnCorpseIdRetryMs || !Host.HasRequestId)
            return;
        _ownCorpseIdRequestedAt[corpseId] = now;
        Host.RequestId(unchecked((uint)corpseId));
    }

    /// <summary>Your identified death corpse within <paramref name="maxMeters"/> (not completed, not cooling down).</summary>
    private bool TryFindOwnCorpseInRange(double maxMeters, out WorldObject? corpse)
    {
        corpse = null;
        var settings = _dashboard?.Settings;
        if (settings == null || !settings.LootOwnCorpse || _objectCache == null || _playerId == 0)
            return false;
        string myName = GetPlayerNameForLootOwnership();
        if (myName.Length == 0)
            return false;
        string wanted = OwnCorpsePrefix + myName;
        long now = CorpseNowMs;
        int playerId = unchecked((int)_playerId);
        double best = double.MaxValue;
        foreach (WorldObject candidate in _objectCache.GetLandscapeObjects())
        {
            if (candidate.ObjectClass != AcObjectClass.Corpse || !string.Equals(candidate.Name, wanted, StringComparison.Ordinal))
                continue;
            if (_completedCorpses.ContainsKey(candidate.Id))
                continue;
            if (_corpseCooldownUntil.TryGetValue(candidate.Id, out long until) && until > now)
                continue;
            if (!EvaluateCorpseLootDecision(candidate).IsOwnDeathCorpse)
                continue;
            double d = _objectCache.Distance(playerId, candidate.Id);
            if (d > maxMeters || d >= best)
                continue;
            best = d;
            corpse = candidate;
        }
        return corpse != null;
    }

    /// <summary>
    /// The pack gates (Stop Looting When Pack Full, the weapon-swap reserve) don't hold your own
    /// corpse back: only "no pack has a free slot at all" does, since then nothing can move.
    /// </summary>
    private bool OwnCorpseBypassesPackGate(LegacyUiSettings settings, double maxMeters)
        => settings.LootOwnCorpse && !IsPackFull() && TryFindOwnCorpseInRange(maxMeters, out _);

    // ── Recovering it ────────────────────────────────────────────────────────

    private int _ownRecoveryCorpseId;   // the own corpse this recovery is about (kept across pauses)
    private int _ownRecoveryTotal;      // items on it when first opened
    private int _ownRecoveryTaken;      // confirmed moved into a pack
    private int _ownRecoveryRounds;     // opens that ended with items that would not move
    private readonly HashSet<int> _ownCorpseNoRoomSaid = new();

    internal const int OwnCorpseMaxAbandons = 5;        // timeouts before it is written off
    private const int OwnCorpseMaxRounds = 3;            // reopen rounds for items that would not move
    private const long OwnCorpseNoRoomPauseMs = 60_000;  // no pack has a slot: look again after this
    private const long OwnCorpseRetryPauseMs = 30_000;   // items would not move: reopen after this
    private const long OwnCorpseBusyWaitMs = 1_000;      // hold a pickup this long behind a busy count
    private const long OwnCorpseSettleMs = 1_000;        // after the last pickup, before judging what is left

    /// <summary>
    /// One tick of recovering your own corpse (it is open and its contents are listed): the next
    /// item still on it is moved into a pack; the normal pick-up confirmation in
    /// TickPendingCorpsePickup confirms it. Then the corpse is closed and completed.
    /// </summary>
    private void TickOwnCorpseRecovery(LegacyUiSettings settings, int corpseId, List<WorldObject> items, long openAge, long now)
    {
        if (items.Count == 0)
        {
            if (openAge < Math.Max(200, settings.LootEmptyCorpseMs) || LastPickupSettling(now))
                return;
            if (_ownRecoveryCorpseId != corpseId)
                BeginOwnCorpseRecovery(corpseId, 0);
            FinishOwnCorpseRecovery(corpseId, items);
            return;
        }

        if (_ownRecoveryCorpseId != corpseId)
            BeginOwnCorpseRecovery(corpseId, items.Count);

        WorldObject? next = null;
        int remaining = 0;
        foreach (WorldObject item in items)
        {
            if (item.Id == 0 || _processedCorpseItems.Contains(item.Id))
                continue;
            remaining++;
            next ??= item;
        }

        if (next == null)
        {
            // Every item was tried. Let the last confirmed move leave the corpse's list first.
            if (_lastLootActionAt != 0 && now - _lastLootActionAt < OwnCorpseSettleMs)
                return;
            if (LastPickupSettling(now))
                return; // the last pickup's Return still plays: close after it
            FinishOwnCorpseRecovery(corpseId, items);
            return;
        }

        // One item at a time, as the looter does: wait out a busy count from our last action,
        // but only briefly (CheckBusyTimeout clears a leaked one).
        if (_busyCount > 0 && _lastLootActionAt != 0 && now - _lastLootActionAt < OwnCorpseBusyWaitMs)
            return;

        // Still animating: right after a pickup's answer that is its Return, and ACE queues one
        // move that arrives then (see TickOpenCorpseLooting); anything else, wait.
        bool pipelined = false;
        if (PickupAnimating(now))
        {
            if (!CanPipelinePickup(now))
                return;
            pipelined = true;
            _pipelinedAfterItemId = _settlingLootItemId;
        }

        string name = string.IsNullOrWhiteSpace(next.Name) ? $"0x{(uint)next.Id:X8}" : next.Name;
        _currentLootItemId = next.Id;
        Host.SelectItem(unchecked((uint)next.Id));
        LootDiag($"[RynthAi] Own corpse: taking 0x{(uint)next.Id:X8} '{name}' ({remaining} left on 0x{(uint)corpseId:X8}).");

        int errSeqAtSend = LastWeenieErrorSeq();
        bool stackable = IsStackAtSend(next.Id);
        LootPickupResult pickup = SendLootPickup(next.Id, name, corpseId);
        if (pickup == LootPickupResult.NoRoom || pickup == LootPickupResult.Refused)
            return;   // SendLootPickup paused the corpse / left the item
        if (pickup != LootPickupResult.Moved && pickup != LootPickupResult.Used)
        {
            if (_busyCount > 0)
                return;
            LootDiag($"[RynthAi] Own corpse: the move of 0x{(uint)next.Id:X8} was not sent.");
            if (IncrementCorpseItemAttempt(next.Id) >= 3)
                _processedCorpseItems.Add(next.Id);
            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        _currentLootItemMovePending     = true;
        _currentLootItemMoveRequestedAt = now;
        _currentLootItemMoveAttempts    = pipelined ? 0 : 1;
        _currentLootItemPipelined       = pipelined;
        _currentLootItemStackable       = stackable;
        _currentLootItemErrSeqAtSend    = errSeqAtSend;
        NoteLootMoveSent();
        _currentLootItemActionLabel     = "Recover";
        _currentLootItemName            = name;
        _currentLootItemIsSalvage       = false;   // never salvage your own things
        _lastLootActionAt               = now;
        _corpseTargetSince              = now;     // progress: a long recovery must not hit the corpse timeout
    }

    private void BeginOwnCorpseRecovery(int corpseId, int itemCount)
    {
        _ownRecoveryCorpseId = corpseId;
        _ownRecoveryTotal = itemCount;
        _ownRecoveryTaken = 0;
        _ownRecoveryRounds = 0;
        _ownCorpseNoRoomSaid.Remove(corpseId);
        // Anything the profile looter passed over before the corpse was known to be yours
        // (opened by hand before its ID landed) is taken now; items already moved have left it.
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        if (itemCount > 0)
            OwnCorpseSay($"[RynthAi] Own corpse: recovering {itemCount} item{(itemCount == 1 ? "" : "s")}.");
    }

    /// <summary>A pick-up from your corpse was confirmed (ConfirmCurrentLootItemMoved).</summary>
    private void NoteOwnCorpseItemRecovered(long now)
    {
        _ownRecoveryTaken++;
        _corpseTargetSince = now;
    }

    private void FinishOwnCorpseRecovery(int corpseId, List<WorldObject> items)
    {
        var left = items.Where(i => i.Id != 0).ToList();
        if (left.Count == 0)
        {
            OwnCorpseSay(_ownRecoveryTaken > 0
                ? $"[RynthAi] Own corpse: recovered everything ({_ownRecoveryTaken} item{(_ownRecoveryTaken == 1 ? "" : "s")})."
                : "[RynthAi] Own corpse: there was nothing on it to recover.");
            MarkDeathSpotRecovered();
            _ownRecoveryCorpseId = 0;
            MarkCorpseCompleted(corpseId);
            return;
        }

        _ownRecoveryRounds++;
        string names = string.Join(", ", left.Take(5).Select(i => string.IsNullOrWhiteSpace(i.Name) ? $"0x{(uint)i.Id:X8}" : i.Name))
                       + (left.Count > 5 ? ", ..." : "");
        if (_ownRecoveryRounds >= OwnCorpseMaxRounds)
        {
            OwnCorpseSay($"[RynthAi] Own corpse: recovered {_ownRecoveryTaken}; {left.Count} item(s) would not move after {_ownRecoveryRounds} tries ({names}). Take them by hand.");
            _ownRecoveryCorpseId = 0;
            MarkCorpseCompleted(corpseId);
            return;
        }
        OwnCorpseSay($"[RynthAi] Own corpse: {left.Count} item(s) would not move ({names}); trying again in {OwnCorpseRetryPauseMs / 1000}s.");
        PauseOwnCorpse(corpseId, OwnCorpseRetryPauseMs, "own corpse: retry later");
    }

    /// <summary>No pack has a slot for the next item: close the corpse and look again later (never write it off).</summary>
    private void PauseOwnCorpseNoRoom(int corpseId, string itemName)
    {
        LootDiag($"[RynthAi] Own corpse: no pack has room for '{itemName}'; pausing {OwnCorpseNoRoomPauseMs / 1000}s.");
        if (_ownCorpseNoRoomSaid.Add(corpseId))
        {
            ChatLine("[RynthAi] Own corpse: your packs are full. Make room and it carries on (it looks again every minute).");
            WorldObjectCache.WarnPackFull(Host, "own corpse recovery paused, no pack has a free slot");
        }
        PauseOwnCorpse(corpseId, OwnCorpseNoRoomPauseMs, "own corpse: no room");
    }

    /// <summary>Closes the corpse and holds it for <paramref name="ms"/>, without the completed mark.</summary>
    private void PauseOwnCorpse(int corpseId, long ms, string why)
    {
        ResetCurrentLootItem();
        CloseCorpseContainer(corpseId, why);
        if (_openedContainerId == corpseId)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
        }
        MarkCorpseCooldown(corpseId, (int)ms);
        ResetCorpseTarget();
    }

    /// <summary>
    /// The move for an item on your corpse. A pack (or a foci) only fits the main pack, and its
    /// contents come with it (ACE never drops packs on death, but a slippery one can); everything
    /// else goes where the looter puts things (LootPickup).
    /// </summary>
    private LootPickupResult SendOwnCorpsePickup(int itemId, out uint destination)
    {
        destination = 0;
        WorldObject? item = _objectCache?[itemId];
        if (item != null && item.ObjectClass is AcObjectClass.Container or AcObjectClass.Foci
            && Host.HasMoveItemExternal && _playerId != 0)
        {
            destination = _playerId;
            return Host.MoveItemExternal(unchecked((uint)itemId), _playerId, 0) ? LootPickupResult.Moved : LootPickupResult.Failed;
        }
        return LootPickup.Send(Host, _objectCache, unchecked((uint)itemId), _playerId, out destination);
    }

    private void OwnCorpseSay(string text)
    {
        Log(text);
        ChatLine(text);
    }

    // ── Where you died ───────────────────────────────────────────────────────

    internal sealed class OwnDeathSpot
    {
        public uint Cell;              // 0 when only the server's chat line gave the place
        public float X, Y, Z;
        public double Ns, Ew;
        public bool HasCoords;
        public bool Outdoors;          // outdoor landblock cell: map coordinates mean something
        public DateTime AtUtc;
        public bool NothingDropped;    // "You have retained all your items..."
        public bool Recovered;
        public string Where
        {
            get
            {
                if (Cell == 0)
                    return FormatMapCoords(Ns, Ew);
                string cell = $"landblock 0x{Cell >> 16:X4}, cell 0x{Cell & 0xFFFF:X4}";
                return HasCoords && Outdoors
                    ? $"{FormatMapCoords(Ns, Ew)} ({cell})"
                    : $"{cell} at ({X:0.0}, {Y:0.0}, {Z:0.0}), indoors";
            }
        }
    }

    private OwnDeathSpot? _deathSpot;
    internal OwnDeathSpot? DeathSpot => _deathSpot;

    /// <summary>The death edge (CheckPlayerDeath): where the player stands is where the corpse will be.</summary>
    private void RecordDeathSpot()
    {
        if (_ownCorpseTravel != OwnCorpseTravelState.None)
            EndOwnCorpseTravel("you died again", sayInChat: false);

        if (!Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _) || cell == 0)
        {
            _deathSpot = null;
            Log("[RynthAi] Death spot: no player position to remember.");
            return;
        }
        bool outdoors = (cell & 0xFFFF) < 0x100;
        bool hasCoords = NavCoordinateHelper.TryConvertPoseToCoords(cell, x, y, out double ns, out double ew);
        _deathSpot = new OwnDeathSpot
        {
            Cell = cell, X = x, Y = y, Z = z, Ns = ns, Ew = ew,
            HasCoords = hasCoords, Outdoors = outdoors, AtUtc = DateTime.UtcNow,
        };
        OwnCorpseSay($"[RynthAi] Your corpse is at {_deathSpot.Where}.");
    }

    private static readonly Regex CorpseLocatedRx = new(
        @"Your corpse is located at \(\s*(\d+(?:\.\d+)?)\s*([NS])\s*,\s*(\d+(?:\.\d+)?)\s*([EW])\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The server's death lines: where the corpse is (outdoor deaths), or that nothing dropped.</summary>
    private void OnOwnCorpseChat(string text)
    {
        if (text.IndexOf("You do not need to recover your corpse", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (_deathSpot != null && (DateTime.UtcNow - _deathSpot.AtUtc).TotalMinutes < 2)
            {
                _deathSpot.NothingDropped = true;
                Log("[RynthAi] Own corpse: the server says nothing dropped; no need to go back for it.");
            }
            return;
        }
        Match m = CorpseLocatedRx.Match(text);
        if (!m.Success)
            return;
        double ns = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value.Equals("S", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
        double ew = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * (m.Groups[4].Value.Equals("W", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
        if (_deathSpot != null && (DateTime.UtcNow - _deathSpot.AtUtc).TotalMinutes < 2)
        {
            Log($"[RynthAi] Own corpse: the server confirms {FormatMapCoords(ns, ew)} (remembered {_deathSpot.Where}).");
            return;
        }
        // No position at the death edge (or it was missed): the server's line is the death spot.
        _deathSpot = new OwnDeathSpot { Ns = ns, Ew = ew, HasCoords = true, Outdoors = true, AtUtc = DateTime.UtcNow };
        OwnCorpseSay($"[RynthAi] Your corpse is at {FormatMapCoords(ns, ew)} (from the server).");
    }

    private void MarkDeathSpotRecovered()
    {
        if (_deathSpot != null)
            _deathSpot.Recovered = true;
    }

    internal static string FormatMapCoords(double ns, double ew)
        => Math.Abs(ns).ToString("0.00", CultureInfo.InvariantCulture) + (ns >= 0 ? "N" : "S") + ", "
         + Math.Abs(ew).ToString("0.00", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");

    /// <summary>/ra owncorpse [go|stop]: where your corpse is, the two settings, the travel state.</summary>
    private void HandleOwnCorpseCommand(string[] parts)
    {
        var s = _dashboard?.Settings;
        string sub = parts.Length >= 3 ? parts[2].Trim().ToLowerInvariant() : string.Empty;
        if (sub == "stop")
        {
            if (_ownCorpseTravel == OwnCorpseTravelState.None) ReplyOk("[RynthAi] Own corpse: not travelling.");
            else EndOwnCorpseTravel("stopped by /ra owncorpse stop");
            return;
        }
        if (sub == "go")
        {
            if (s == null) { ReplyFail("[RynthAi] Own corpse: settings not ready (log in first)."); return; }
            StartOwnCorpseTravel(manual: true);   // says where it goes, or why not
            return;
        }

        var spot = _deathSpot;
        if (spot == null)
            ChatLine("[RynthAi] Own corpse: no death remembered this session.");
        else
        {
            int mins = (int)(DateTime.UtcNow - spot.AtUtc).TotalMinutes;
            string state = spot.Recovered ? ", recovered" : spot.NothingDropped ? ", nothing dropped" : string.Empty;
            ChatLine($"[RynthAi] Your corpse is at {spot.Where}, died {mins} min ago{state}.");
        }
        if (s != null)
            ChatLine($"[RynthAi] Recover My Corpse: {(s.LootOwnCorpse ? "on" : "off")}, Travel Back To My Corpse: {(s.TravelToOwnCorpse ? "on" : "off")}; travel: {OwnCorpseTravelText()}. /ra owncorpse go|stop");
    }

    // ── Travelling back (TravelToOwnCorpse) ─────────────────────────────────

    internal enum OwnCorpseTravelState { None, Waiting, Travelling, Paused, Arrived }

    private OwnCorpseTravelState _ownCorpseTravel;
    private long _travelStartedAt;
    private long _travelIdleSince;
    private long _travelBestAt;
    private long _travelArrivedAt;
    private double _travelBestDistance;
    private bool _ownCorpseTravelManual;

    internal const long OwnCorpseTravelTimeoutMs = 15 * 60_000;   // whole trip, fights included
    internal const long OwnCorpseTravelNoProgressMs = 120_000;    // walking without getting 10 m closer
    internal const double OwnCorpseTravelProgressMeters = 10.0;
    internal const double OwnCorpseArriveMeters = 6.0;            // at the spot: stop walking, look for it
    internal const long OwnCorpseArriveSearchMs = 20_000;         // at the spot with no corpse in range
    internal const long OwnCorpseTravelResumeMs = 1_500;          // idle this long before walking (again)
    internal const string RynthNavPlugin = "RynthNav";

    /// <summary>While travelling back, RynthAi's own nav route stays parked (the arbiter's wantNav).</summary>
    private bool OwnCorpseTravelHoldsNav => _ownCorpseTravel != OwnCorpseTravelState.None;

    private string OwnCorpseTravelText() => _ownCorpseTravel switch
    {
        OwnCorpseTravelState.None => "off",
        OwnCorpseTravelState.Waiting => "waiting for a quiet moment",
        OwnCorpseTravelState.Travelling => "walking (RynthNav)",
        OwnCorpseTravelState.Paused => "paused while RynthAi works",
        OwnCorpseTravelState.Arrived => "at the spot, looking for the corpse",
        _ => _ownCorpseTravel.ToString(),
    };

    /// <summary>The respawn edge (HoldForDeath): start the trip back when Travel Back To My Corpse is on.</summary>
    private void OnRespawnedForOwnCorpse()
    {
        var s = _dashboard?.Settings;
        if (s == null || !s.TravelToOwnCorpse)
            return;
        StartOwnCorpseTravel(manual: false);
    }

    private void StartOwnCorpseTravel(bool manual)
    {
        var s = _dashboard?.Settings;
        var spot = _deathSpot;
        if (s == null)
            return;
        if (spot == null)
        {
            if (manual) ChatLine("[RynthAi] Own corpse: no death remembered this session, so nowhere to go.");
            return;
        }
        if (spot.Recovered)
        {
            if (manual) ChatLine("[RynthAi] Own corpse: already recovered.");
            return;
        }
        if (spot.NothingDropped && !manual)
        {
            Log("[RynthAi] Own corpse: nothing dropped on it; not travelling back.");
            return;
        }
        if (!s.IsMacroRunning)
        {
            OwnCorpseSay("[RynthAi] Own corpse: the macro is off; not travelling back (start it and use /ra owncorpse go).");
            return;
        }
        if (!spot.Outdoors || !spot.HasCoords)
        {
            OwnCorpseSay($"[RynthAi] Own corpse: you died indoors ({spot.Where}); travelling back only works outdoors, where RynthNav can route on the map.");
            return;
        }
        if (!Host.HasSendPluginCommand)
        {
            OwnCorpseSay("[RynthAi] Own corpse: this engine can't send RynthNav commands; not travelling back.");
            return;
        }

        long now = Environment.TickCount64;
        _ownCorpseTravel = OwnCorpseTravelState.Waiting;
        _ownCorpseTravelManual = manual;
        _travelStartedAt = now;
        _travelIdleSince = 0;
        _travelArrivedAt = 0;
        _travelBestAt = now;
        _travelBestDistance = double.MaxValue;
        OwnCorpseSay($"[RynthAi] Own corpse: travelling back to {FormatMapCoords(spot.Ns, spot.Ew)} with RynthNav once nothing else needs doing.");
    }

    /// <summary>
    /// Every tick after the arbiter's decision. Walks (RynthNav) only while RynthAi is idle; stops
    /// the walk the moment RynthAi wants the tick (combat, buffing, looting, salvage, a vendor or
    /// trade session, crafting) and resumes after. Ends when your corpse is in loot range.
    /// </summary>
    private void TickOwnCorpseTravel(LegacyUiSettings s)
    {
        if (_ownCorpseTravel == OwnCorpseTravelState.None)
            return;

        long now = Environment.TickCount64;
        var spot = _deathSpot;
        if (spot == null) { EndOwnCorpseTravel("no death spot"); return; }
        if (!s.TravelToOwnCorpse && !_ownCorpseTravelManual) { EndOwnCorpseTravel("Travel Back To My Corpse was turned off"); return; }
        if (!s.IsMacroRunning) { EndOwnCorpseTravel("the macro stopped"); return; }
        if (spot.Recovered) { EndOwnCorpseTravel("your corpse is recovered", sayInChat: false); return; }

        double lootRange = GetCorpseApproachRangeMaxMeters(s);
        if (s.EnableLooting && TryFindOwnCorpseInRange(lootRange, out WorldObject? corpse) && corpse != null)
        {
            EndOwnCorpseTravel($"your corpse (0x{(uint)corpse.Id:X8}) is in loot range; recovering it");
            return;
        }
        if (now - _travelStartedAt > OwnCorpseTravelTimeoutMs)
        {
            EndOwnCorpseTravel($"gave up: not there after {OwnCorpseTravelTimeoutMs / 60_000} min");
            return;
        }

        double d = DistanceToDeathSpotMeters(spot);
        if (!double.IsNaN(d) && d <= OwnCorpseArriveMeters)
        {
            if (_ownCorpseTravel != OwnCorpseTravelState.Arrived)
            {
                if (_ownCorpseTravel == OwnCorpseTravelState.Travelling) SendRynthNav("stop");
                _ownCorpseTravel = OwnCorpseTravelState.Arrived;
                _travelArrivedAt = now;
                Log($"[RynthAi] Own corpse: at the death spot ({d:0.0} m); looking for the corpse.");
            }
            if (now - _travelArrivedAt > OwnCorpseArriveSearchMs)
                EndOwnCorpseTravel(s.EnableLooting
                    ? "gave up: at the spot, but no corpse of yours in loot range (it may have decayed, or lie further than loot range)"
                    : "at the spot; Enable Looting is off, so it is not recovered");
            return;
        }

        if (OwnCorpseTravelBlockedBy(out string by))
        {
            if (_ownCorpseTravel == OwnCorpseTravelState.Travelling)
            {
                SendRynthNav("stop");
                Log($"[RynthAi] Own corpse: travel paused for {by}.");
            }
            if (_ownCorpseTravel != OwnCorpseTravelState.Waiting)
                _ownCorpseTravel = OwnCorpseTravelState.Paused;
            _travelIdleSince = 0;
            _travelBestAt = now;   // a fight is not "no progress"
            return;
        }

        if (_ownCorpseTravel != OwnCorpseTravelState.Travelling)
        {
            if (_travelIdleSince == 0) _travelIdleSince = now;
            if (now - _travelIdleSince < OwnCorpseTravelResumeMs)
                return;
            bool resume = _ownCorpseTravel == OwnCorpseTravelState.Paused || _ownCorpseTravel == OwnCorpseTravelState.Arrived;
            if (!SendRynthNav("go " + FormatMapCoords(spot.Ns, spot.Ew)))
            {
                EndOwnCorpseTravel("gave up: RynthNav isn't loaded in this client", sendStop: false);
                return;
            }
            Log($"[RynthAi] Own corpse: {(resume ? "resuming the walk" : "walking")} to {FormatMapCoords(spot.Ns, spot.Ew)}{(double.IsNaN(d) ? "" : $" ({d:0} m away)")}.");
            _ownCorpseTravel = OwnCorpseTravelState.Travelling;
            _travelArrivedAt = 0;
            _travelBestAt = now;
            _travelBestDistance = double.IsNaN(d) ? double.MaxValue : d;
            return;
        }

        if (!double.IsNaN(d) && (_travelBestDistance == double.MaxValue || d < _travelBestDistance - OwnCorpseTravelProgressMeters))
        {
            _travelBestDistance = d;
            _travelBestAt = now;
        }
        else if (now - _travelBestAt > OwnCorpseTravelNoProgressMs)
        {
            EndOwnCorpseTravel($"gave up: no closer in {OwnCorpseTravelNoProgressMs / 1000}s (RynthNav may have no route or navmesh here; /rnav here says)");
        }
    }

    /// <summary>RynthAi wants the tick for something, so RynthNav must not be walking.</summary>
    private bool OwnCorpseTravelBlockedBy(out string by)
    {
        by = string.Empty;
        if (_activity != BotActivity.Idle) by = ActivityArbiter.ToBotAction(_activity);
        else if (_autoVendor?.HoldsBot == true) by = "AutoVendor";
        else if (_autoTrade?.HoldsBot == true) by = "AutoTrade";
        else if (_missileCraftingManager?.IsCrafting == true) by = "ammo crafting";
        else if (Host.HasIsPortaling && Host.IsPortaling()) by = "portal space";
        return by.Length > 0;
    }

    private void EndOwnCorpseTravel(string why, bool sayInChat = true, bool sendStop = true)
    {
        if (sendStop && _ownCorpseTravel == OwnCorpseTravelState.Travelling)
            SendRynthNav("stop");
        _ownCorpseTravel = OwnCorpseTravelState.None;
        _ownCorpseTravelManual = false;
        _travelIdleSince = 0;
        _travelArrivedAt = 0;
        string line = $"[RynthAi] Own corpse: travel back ended: {why}.";
        if (sayInChat) OwnCorpseSay(line); else Log(line);
        // RynthAi's own nav carries on from the nearest waypoint, not one from before the death.
        _navigationEngine?.ResumeFromNearestWaypoint(always: true);
    }

    private bool SendRynthNav(string command)
    {
        bool sent = Host.HasSendPluginCommand && Host.SendPluginCommand(RynthNavPlugin, "rnav", command);
        Log($"[RynthAi] Own corpse: /rnav {command} -> RynthNav {(sent ? "ok" : "NOT delivered")}.");
        return sent;
    }

    /// <summary>Metres from the player to the death spot by map coordinates (NaN without a position).</summary>
    private double DistanceToDeathSpotMeters(OwnDeathSpot spot)
    {
        if (!spot.HasCoords)
            return double.NaN;
        if (!Host.TryGetPlayerPose(out uint cell, out float x, out float y, out _, out _, out _, out _, out _) || cell == 0)
            return double.NaN;
        if (!NavCoordinateHelper.TryConvertPoseToCoords(cell, x, y, out double ns, out double ew))
            return double.NaN;
        // 0.1 of a map coordinate is 24 m.
        return Math.Sqrt(Math.Pow(ns - spot.Ns, 2) + Math.Pow(ew - spot.Ew, 2)) * 240.0;
    }
}
