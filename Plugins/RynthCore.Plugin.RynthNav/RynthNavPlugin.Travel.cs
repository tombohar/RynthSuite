using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RynthNav.Routing;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

// RynthNav's travel side: the Atlas (NavData\locations.json), the arrow, favorites and
// recent (NavData\atlas.txt), the recalls a character can use and learning where their
// personal ones land (NavData\recalls.txt), and casting recall steps of a route.
// Our own take on GoArrow (Digero 2006, Virindi 2011, MIT): its arrow, atlas and
// recall-aware routes, on our own server's data.
//
// Threads: the chat bar, the chat window and the engine's panel only queue (commands,
// chat lines); everything runs on the tick thread (OnTick), which owns the arrow, the
// stores and the route. The Atlas is loaded once on a worker and then never changes.
// Files are written off the tick thread from a text copy.
public sealed partial class RynthNavPlugin
{
    private const double ArrowDefaultArriveUnits = ArrowGuide.DefaultArriveUnits;
    private const double PortalNearUnits = 12.0;        // a teleport from this close to a known portal = that portal
    private const double RecallNearUnits = 30.0;        // a landing this close to a recall's landing = that recall
    private const long RecallAnnounceWindowMs = 45000;  // "X is recalling home." -> the teleport within this
    private const long RecallCastWindowMs = 40000;      // our own recall cast -> the teleport within this
    private const long RecallStepTimeoutMs = 45000;     // a route's recall step gives up after this
    private const long RecallRecastMs = 4500;           // re-cast a recall spell this often while waiting
    private const long HandCastWindowMs = 12000;        // your spell words -> portal space within this = a recall cast by hand
    private const double HandCastStillUnits = 2.0;      // ...standing still meanwhile (walking into a portal moves you)
    private const double LifestoneLandUnits = 10.0;     // a landing this close to a lifestone = on it (Lifestone Recall)
    private const double PortalBesideUnits = 4.0;       // a live portal this close as portal space begins: maybe that portal, not a recall
    private const long StanceRetryMs = 1500;
    private const long WandAfterMs = 6000;              // no magic stance by then: try wielding a wand
    private const int CombatModeNonCombat = 1, CombatModeMagic = 8;
    private const uint ItemTypeCaster = 0x00008000;

    private volatile Atlas _atlas = Atlas.Empty;
    private volatile string _atlasStatus = "loading…";
    private HashSet<uint> _mapLandblocks = new();   // landblocks with Atlas places on the map (tick thread)
    private volatile HashSet<uint> _dungeonLandblocks = new();   // dungeon landblocks the Atlas knows (Atlas.DungeonLandblocks; read-only once built)
    private AtlasStore _store = new();
    private RecallMemory _memory = new();
    private bool _storesLoaded;
    private int _savedStoreVersion, _savedMemoryVersion;
    private long _nextSaveMs;

    private readonly ArrowGuide _arrow = new();
    private int _openArrowSeq;                       // bumped when a command asks the panel to show the arrow

    /// <summary>The engine polls this (RynthNavArrowSeq) to open the arrow overlay. Any thread.</summary>
    public int ArrowSeq => System.Threading.Volatile.Read(ref _openArrowSeq);

    private readonly ConcurrentQueue<string> _commands = new();
    private readonly ConcurrentQueue<(RecallEvent Event, long Ms)> _recallEvents = new();
    private volatile string _lastChatCommand = "";
    private long _lastChatCommandMs;

    // Character and spellbook (tick thread).
    private string _charKey = "", _playerName = "";
    private HashSet<int>? _known;
    private long _nextKnownMs;
    private readonly uint[] _knownBuf = new uint[8192];

    // Learning: a recall announced or cast, a portal tie waiting for "which one".
    private RecallSlot? _pendingRecallSlot;
    private string _pendingRecallCommand = "";
    private long _pendingRecallMs;
    private int _castRecallSpell;
    private long _castRecallMs;
    private RecallSpot? _pendingTie;
    // Your own spell words (a recall cast by hand teleports a few seconds later): when and where.
    private long _selfCastMs;
    private double _selfCastWx, _selfCastWy;
    private bool _selfCastDied;
    private int _weenieSeq = -1;                     // the last server refusal looked at (-1 = none yet)

    // Teleport tracking (tick thread).
    private bool _trkPortaling, _trkHaveLast, _trkHavePre;
    private uint _trkLastCell, _trkPreCell;
    private double _trkLastWx, _trkLastWy, _trkPreWx, _trkPreWy;
    private long _trkLastTeleportMs, _trkPortalStartMs;
    private bool _trkPortalBeside;                   // a live portal stood right beside you as portal space began

    // Route planning with recalls: the recalls the current route can use (by RouteStep.RecallIndex).
    private List<RecallOption> _routeRecalls = new();
    private volatile bool _recallsEnabled = true;
    private List<RouteStep>? _shownRoute;            // last planned route, for the panel
    private bool _shownRouteLive;                    // ...and it is the running goto's (a plain walk too), not just a plan
    private string _shownRouteFor = "";
    private double _shownRouteEst;
    private int _routeVersion;

    // Recall step execution (tick thread).
    private bool _recallWait;
    private long _recallStartMs, _recallLastCastMs, _recallLastStanceMs;
    private bool _recallWandTried, _recallChangedStance, _recallSent;
    private uint _recallPreLb;
    private double _recallPreWx, _recallPreWy;
    private bool _recallWasPortaling;

    // Status text for the panel (built on the tick when something moves; read under _gate).
    private string _travelJson = "";
    private int _builtArrowV = -1, _builtStoreV = -1, _builtMemV = -1, _builtRouteV = -1, _builtOpenSeq = -1;
    private string _builtArrowExtra = "";
    private bool _builtPortals, _builtRecalls, _builtGoto;
    private int _builtKnownCount = -2;
    private Atlas? _builtAtlas;
    private string _builtAtlasStatus = "";
    private int _builtRouteIdx = -1;
    private RecallSpot? _builtPendingTie;

    private static long NowMs => Environment.TickCount64;
    private string NavFile(string name) => Path.Combine(NavDataDir, name);

    // ── Start up ─────────────────────────────────────────────────────────────

    private void InitTravel()
    {
        string path = NavFile("locations.json");
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) { _atlasStatus = "no locations.json in NavData"; return; }
                Atlas a = Atlas.Load(path);
                _atlas = a;
                _atlasStatus = $"{a.Locations.Count} places";
                Host.Log($"[RynthNav] Atlas: {a}");
            }
            catch (Exception ex)
            {
                _atlasStatus = "locations.json failed to load";
                Host.Log($"[RynthNav] Atlas load failed: {ex.Message}");
            }
        });
        Task.Run(() =>
        {
            AtlasStore store = new();
            RecallMemory mem = new();
            try { if (File.Exists(NavFile("atlas.txt"))) store = AtlasStore.Parse(File.ReadAllText(NavFile("atlas.txt"))); }
            catch (Exception ex) { Host.Log($"[RynthNav] atlas.txt: {ex.Message}"); }
            try { if (File.Exists(NavFile("recalls.txt"))) mem = RecallMemory.Parse(File.ReadAllText(NavFile("recalls.txt"))); }
            catch (Exception ex) { Host.Log($"[RynthNav] recalls.txt: {ex.Message}"); }
            _loadedStore = store;
            _loadedMemory = mem;
        });
    }

    private volatile AtlasStore? _loadedStore;
    private volatile RecallMemory? _loadedMemory;
    private Atlas? _indexedAtlas;

    private void OnLoginTravel()
    {
        _charKey = ""; _playerName = ""; _known = null; _nextKnownMs = 0;
        _pendingRecallSlot = null; _castRecallSpell = 0; _pendingTie = null; _selfCastMs = 0;
        _trkHaveLast = false; _trkPortaling = false;
    }

    private void OnLogoutTravel()
    {
        _recallWait = false;
        _trkHaveLast = false; _trkPortaling = false;
        _known = null; _charKey = "";
        SaveStores(force: true);
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void TickTravel()
    {
        long now = NowMs;
        if (!_storesLoaded && _loadedStore != null && _loadedMemory != null)
        {
            _store = _loadedStore; _memory = _loadedMemory; _storesLoaded = true;
            _savedStoreVersion = _store.Version; _savedMemoryVersion = _memory.Version;
        }
        Atlas atlas = _atlas;
        if (!ReferenceEquals(atlas, _indexedAtlas))
        {
            var lbs = new HashSet<uint>();
            foreach (var l in atlas.Locations)
                if (l.OnMap && l.Cell.Length == 8 && uint.TryParse(l.Cell.AsSpan(0, 4), NumberStyles.HexNumber, null, out uint lb)) lbs.Add(lb);
            _mapLandblocks = lbs;
            _dungeonLandblocks = atlas.DungeonLandblocks();
            _indexedAtlas = atlas;
            if (_known != null && atlas.Recalls.Count > 0) LogKnownRecalls(_known);
        }
        if (_charKey.Length == 0 && _hasPose) ResolveCharacter();
        if (now >= _nextKnownMs && _hasPose) RefreshKnownSpells(now);

        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd, fromPanel: false);
        while (_recallEvents.TryDequeue(out var ev)) OnRecallEvent(ev.Event, ev.Ms);
        PollRecallRefusal();
        if (_selfCastMs != 0 && !_selfCastDied && now - _selfCastMs < HandCastWindowMs + 30000
            && Host.TryGetPlayerVitals(out uint hp, out uint maxHp, out _, out _, out _, out _) && maxHp > 0 && hp == 0)
            _selfCastDied = true;   // died mid-cast: the trip to the lifestone that follows isn't a recall

        bool teleported = TrackTeleport(now);
        UpdateArrow(teleported);

        if (now >= _nextSaveMs) { _nextSaveMs = now + 3000; SaveStores(force: false); }
        RebuildTravelJson();
    }

    private void ResolveCharacter()
    {
        uint id = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (id == 0 || !Host.TryGetObjectName(id, out string name) || name.Length == 0) return;
        Host.TryGetWorldName(out string world);
        _playerName = name;
        _charKey = (world.Length > 0 ? world : "?") + "|" + name;
    }

    private void RefreshKnownSpells(long now)
    {
        _nextKnownMs = now + 10000;
        if (!Host.HasReadKnownSpells) return;
        int n = Host.ReadKnownSpells(_knownBuf, _knownBuf.Length);
        if (n <= 0) { _nextKnownMs = now + 2000; return; }   // not read yet (cold)
        var set = new HashSet<int>();
        for (int i = 0; i < Math.Min(n, _knownBuf.Length); i++) set.Add((int)_knownBuf[i]);
        bool changed = _known == null || _known.Count != set.Count || !_known.SetEquals(set);
        _known = set;
        if (changed) LogKnownRecalls(set);
    }

    /// <summary>
    /// At login (and whenever the spellbook changes): which recall spells the character knows, as
    /// RynthNav will plan with them. A recall missing here is never planned (10-01: Recall Aphus
    /// Lassel added in the DB but not seen; this line shows whether the client has it).
    /// </summary>
    private void LogKnownRecalls(HashSet<int> known)
    {
        var names = new List<string>();
        foreach (var (id, name) in new[]
        {
            (RecallPlanner.LifestoneRecall, "Lifestone Recall"), (RecallPlanner.PortalRecall, "Portal Recall"),
            (RecallPlanner.PrimaryPortalTieRecall, "Primary Portal Tie Recall"), (RecallPlanner.SecondaryPortalTieRecall, "Secondary Portal Tie Recall"),
        })
            if (known.Contains(id)) names.Add(name);
        int atlasKnown = 0, atlasAll = 0;
        var seen = new HashSet<int>();
        foreach (AtlasRecall r in _atlas.Recalls)
        {
            if (!seen.Add(r.SpellId)) continue;
            atlasAll++;
            if (!known.Contains(r.SpellId)) continue;
            atlasKnown++;
            names.Add($"{r.Name} ({r.SpellId}{(r.OnMap ? "" : ", lands in a dungeon")})");
        }
        Host.Log($"[RynthNav] spellbook: {known.Count} spells; recalls known: {(names.Count == 0 ? "none" : string.Join(", ", names))}"
               + $" ({atlasKnown} of the Atlas's {atlasAll} fixed recalls{(_atlas.Recalls.Count == 0 ? "; the Atlas isn't loaded yet" : "")})");
    }

    private bool PlayerMapPos(out double ns, out double ew)
    {
        ns = NavCoords.NsFromWorld(_wy); ew = NavCoords.EwFromWorld(_wx);
        return _hasPose && IsOnMap(_cellId);
    }

    /// <summary>Outdoors, or in a building of a landblock that has Atlas places on the map.</summary>
    private bool IsOnMap(uint cell) => (cell & 0xFFFF) < 0x100 || _mapLandblocks.Contains(cell >> 16);

    /// <summary>
    /// In a dungeon the Atlas knows. Its landblock's tile is the surface's (dungeon cells aren't
    /// baked), so there is no navmesh to walk here even though nav_XXXX.tile exists.
    /// </summary>
    private bool InDungeon(uint cell) => (cell & 0xFFFF) >= 0x100 && _dungeonLandblocks.Contains(cell >> 16);

    // ── Teleports: the arrow's portal/recall steps and learning recall landings ──

    private bool TrackTeleport(long now)
    {
        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        if (portaling)
        {
            if (!_trkPortaling)
            {
                _trkPortaling = true;
                _trkPortalStartMs = now;
                // Only worth the look after your own spell words (a hand-cast recall, or a portal used standing at it?).
                _trkPortalBeside = _selfCastMs != 0 && now - _selfCastMs < HandCastWindowMs && _trkHaveLast && LivePortalWithin(_trkLastWx, _trkLastWy, PortalBesideUnits);
                _trkHavePre = _trkHaveLast;
                _trkPreCell = _trkLastCell; _trkPreWx = _trkLastWx; _trkPreWy = _trkLastWy;
            }
            return false;
        }
        if (!_hasPose) return false;
        bool jump = _trkHaveLast && Math.Sqrt((_wx - _trkLastWx) * (_wx - _trkLastWx) + (_wy - _trkLastWy) * (_wy - _trkLastWy)) > PortalJumpUnits;
        bool teleported = false;
        if (_trkPortaling || jump)
        {
            bool havePre = _trkPortaling ? _trkHavePre : _trkHaveLast;
            uint preCell = _trkPortaling ? _trkPreCell : _trkLastCell;
            double preWx = _trkPortaling ? _trkPreWx : _trkLastWx, preWy = _trkPortaling ? _trkPreWy : _trkLastWy;
            long startMs = _trkPortaling ? _trkPortalStartMs : now;
            bool portalBeside = _trkPortaling && _trkPortalBeside;
            _trkPortaling = false;
            if (now - _trkLastTeleportMs > 2000)
            {
                teleported = true;
                _trkLastTeleportMs = now;
                OnTeleported(havePre, preCell, preWx, preWy, startMs, portalBeside);
            }
        }
        _trkHaveLast = true; _trkLastCell = _cellId; _trkLastWx = _wx; _trkLastWy = _wy;
        return teleported;
    }

    private void OnTeleported(bool havePre, uint preCell, double preWx, double preWy, long startMs, bool portalBeside)
    {
        long now = NowMs;
        RecallSpot landing = SpotHere();
        string who = _charKey;
        // Your spell words shortly before portal space, standing still since, not dead, no portal
        // right beside you: a recall cast by hand (walking into a portal moves you; using one you
        // stand at doesn't, hence the portal check; dying is a trip, not a recall).
        bool handCast = _selfCastMs != 0 && startMs - _selfCastMs >= 0 && startMs - _selfCastMs < HandCastWindowMs
            && !_selfCastDied && havePre && !portalBeside
            && Math.Sqrt((preWx - _selfCastWx) * (preWx - _selfCastWx) + (preWy - _selfCastWy) * (preWy - _selfCastWy)) < HandCastStillUnits;
        _selfCastMs = 0; _selfCastDied = false;
        if (who.Length == 0) return;

        if (_pendingRecallSlot is RecallSlot slot && now - _pendingRecallMs < RecallAnnounceWindowMs)
        {
            landing.Command = _pendingRecallCommand.Length > 0 ? _pendingRecallCommand : RecallMemory.DefaultCommand(slot);
            _memory.Set(who, slot, landing);
            _pendingRecallSlot = null;
            Host.Log($"[RynthNav] learned {RecallMemory.SlotName(slot)} lands at {Describe(landing)}");
            return;
        }
        _pendingRecallSlot = null;
        if (_castRecallSpell != 0 && now - _castRecallMs < RecallCastWindowMs)
        {
            int spell = _castRecallSpell;
            _castRecallSpell = 0;
            RecallSlot? learned = spell switch
            {
                RecallPlanner.LifestoneRecall => RecallSlot.Lifestone,
                RecallPlanner.PrimaryPortalTieRecall => RecallSlot.Tie1,
                RecallPlanner.SecondaryPortalTieRecall => RecallSlot.Tie2,
                _ => null,
            };
            if (learned is RecallSlot ls)
            {
                _memory.Set(who, ls, landing);
                Host.Log($"[RynthNav] learned {RecallMemory.SlotName(ls)} lands at {Describe(landing)}");
            }
            return;
        }
        // A recall cast by hand (or by another plugin): never the last portal's landing.
        if (handCast && !CameThroughAtlasPortal(havePre, preCell, preWx, preWy, landing))
        {
            LearnHandCast(landing);
            return;
        }
        // Not a recall we know of: a portal, most likely. Portal Recall goes back to where
        // the last portal dropped you. A landing at a known recall's spot (a recall cast by
        // hand, or dying and waking at the lifestone) isn't a portal.
        if (landing.OnMap && LandsLikeARecall(landing)) return;
        string label = landing.Label;
        if (havePre && IsOnMap(preCell))
        {
            AtlasLocation? p = _atlas.Nearest("Portal", NavCoords.NsFromWorld(preWy), NavCoords.EwFromWorld(preWx), PortalNearUnits);
            if (p != null) label = p.Name + (landing.Label.Length > 0 ? " → " + landing.Label : "");
        }
        landing.Label = label;
        _memory.Set(who, RecallSlot.LastPortal, landing);
    }

    private bool LandsLikeARecall(RecallSpot s)
    {
        foreach (AtlasRecall r in _atlas.Recalls)
            if (r.OnMap && NavCoords.Distance(s.Ns, s.Ew, r.Ns, r.Ew) < RecallNearUnits) return true;
        foreach (RecallSlot slot in new[] { RecallSlot.Sanctuary, RecallSlot.Lifestone, RecallSlot.House, RecallSlot.Mansion, RecallSlot.Hometown })
        {
            RecallSpot? k = _memory.Get(_charKey, slot);
            if (k != null && k.OnMap && NavCoords.Distance(s.Ns, s.Ew, k.Ns, k.Ew) < RecallNearUnits) return true;
        }
        return false;
    }

    /// <summary>A live portal object within <paramref name="units"/> of a world point (this landblock's objects).</summary>
    private bool LivePortalWithin(double wx, double wy, double units)
    {
        if (!Host.HasGetLiveObjectIds) return false;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!Host.TryGetItemType(id, out uint type) || (type & ItemTypePortal) == 0) continue;
            if (!Host.TryGetObjectPosition(id, out uint cell, out float x, out float y, out _) || cell == 0) continue;
            double ox = ((cell >> 24) & 0xFF) * 192.0 + x, oy = ((cell >> 16) & 0xFF) * 192.0 + y;
            if ((ox - wx) * (ox - wx) + (oy - wy) * (oy - wy) < units * units) return true;
        }
        return false;
    }

    /// <summary>
    /// A known portal stood where you were, and goes where you landed: the teleport was that
    /// portal (used standing at it), not the spell you'd just cast.
    /// </summary>
    private bool CameThroughAtlasPortal(bool havePre, uint preCell, double preWx, double preWy, RecallSpot landing)
    {
        if (!havePre || !IsOnMap(preCell) || !landing.OnMap) return false;
        double pns = NavCoords.NsFromWorld(preWy), pew = NavCoords.EwFromWorld(preWx);
        foreach (AtlasLocation l in _atlas.Locations)
        {
            if (!l.OnMap || !l.HasDest || !l.DestOnMap || !l.Type.Equals("Portal", StringComparison.Ordinal)) continue;
            if (NavCoords.Distance(pns, pew, l.Ns, l.Ew) < PortalNearUnits
                && NavCoords.Distance(landing.Ns, landing.Ew, l.DestNs, l.DestEw) < RecallNearUnits) return true;
        }
        return false;
    }

    /// <summary>
    /// A recall cast by hand (RynthNav didn't cast it): learn which one from where it landed
    /// (RecallPlanner.GuessHandCast), so ties made before RynthNav was there, or cast from the
    /// spell bar, are planned from now on.
    /// </summary>
    private void LearnHandCast(RecallSpot landing)
    {
        if (landing.OnMap)
            foreach (AtlasRecall r in _atlas.Recalls)
                if (r.OnMap && NavCoords.Distance(landing.Ns, landing.Ew, r.Ns, r.Ew) < RecallNearUnits)
                {
                    Host.Log($"[RynthNav] recall cast by hand landed at {Describe(landing)}: {r.Name}'s landing, nothing to learn");
                    return;
                }
        AtlasLocation? ls = landing.OnMap ? _atlas.Nearest("Lifestone", landing.Ns, landing.Ew, LifestoneLandUnits) : null;
        RecallPlanner.HandCast kind = RecallPlanner.GuessHandCast(landing, ls != null, _known, _memory, _charKey, RecallNearUnits,
            out RecallSlot slot, out string why);
        switch (kind)
        {
            case RecallPlanner.HandCast.Learn:
                if (ls != null) landing.Label = ls.Name + (landing.Label.Length > 0 ? " (" + landing.Label + ")" : "");
                _memory.Set(_charKey, slot, landing);
                Host.Log($"[RynthNav] learned {RecallMemory.SlotName(slot)} lands at {Describe(landing)} (a recall cast by hand: {why})");
                Say($"{RecallPlanner.Title(slot)} takes you to {Describe(landing)}: routes can use it now.");
                break;
            case RecallPlanner.HandCast.AskTie:
                _pendingTie = landing;
                Host.Log($"[RynthNav] recall cast by hand landed at {Describe(landing)}: {why}; asking which tie");
                Say($"Recalled to {Describe(landing)}. If that was a portal tie recall: /rnav tie 1 (primary) or /rnav tie 2 (secondary).");
                break;
            default:
                Host.Log($"[RynthNav] recall cast by hand landed at {Describe(landing)}: {why}");
                break;
        }
    }

    /// <summary>
    /// The server's "You must link to a lifestone / portal to recall it": that recall has no
    /// tie (any more), so forget its landing; a route waiting on it plans again without it.
    /// </summary>
    private void PollRecallRefusal()
    {
        if (!Host.HasGetLastWeenieError || !Host.TryGetLastWeenieError(out int seq, out uint error, out _, out _)) return;
        if (_weenieSeq < 0 || seq == _weenieSeq) { _weenieSeq = seq; return; }   // the first look only notes where the count stands
        _weenieSeq = seq;
        int cast = _castRecallSpell != 0 && NowMs - _castRecallMs < RecallCastWindowMs ? _castRecallSpell : 0;
        if (RecallPlanner.SlotUnlinked(error, cast) is not RecallSlot slot || _charKey.Length == 0) return;
        bool had = _memory.Get(_charKey, slot) != null;
        _memory.Clear(_charKey, slot);
        if (slot == RecallSlot.Tie1 || slot == RecallSlot.Tie2) _pendingTie = null;
        string name = RecallPlanner.Title(slot);
        Host.Log($"[RynthNav] {name}: the server says it isn't linked (0x{error:X4}){(had ? "; its landing is forgotten" : "")}");
        RecallOption? o = _recallWait ? CurrentRecallOption() : null;
        if (o != null && o.SpellId == RecallPlanner.SpellFor(slot) && _gotoActive)
        {
            string goal = _gotoName, type = _gotoType, enter = _enterName, enterType = _enterType;
            double gns = NavCoords.NsFromWorld(_finalTns), gew = NavCoords.EwFromWorld(_finalTew);
            Say($"goto: {name} isn't tied to anything; planning again without it");
            EndRecall();
            StartWalk(goal.Length > 0 ? goal : NavCoords.Fmt(gns, gew), type, gns, gew, enter, enterType);
        }
        else if (had) Say($"{name} isn't tied to anything: routes won't use it until you tie it again.");
    }

    /// <summary>Where you stand now, as a recall landing: on the map or not, and a label.</summary>
    private RecallSpot SpotHere()
    {
        var s = new RecallSpot();
        if (PlayerMapPos(out double ns, out double ew)) { s.OnMap = true; s.Ns = Math.Round(ns, 2); s.Ew = Math.Round(ew, 2); s.Label = NearLabel(ns, ew); }
        else s.Label = $"a dungeon (landblock {(_cellId >> 16):X4})";
        return s;
    }

    private string NearLabel(double ns, double ew)
    {
        AtlasLocation? town = _atlas.Nearest("Town", ns, ew, 1500);
        return town != null ? "near " + town.Name : NavCoords.Fmt(ns, ew);
    }

    private static string Describe(RecallSpot s) => s.OnMap ? $"{NavCoords.Fmt(s.Ns, s.Ew)} ({s.Label})" : s.Label;

    // ── Chat lines: what the server says about recalls ───────────────────────

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        RecallEvent e = RecallMemory.Classify(text, _playerName, chatType);
        if (e != RecallEvent.None) _recallEvents.Enqueue((e, NowMs));
    }

    private void OnRecallEvent(RecallEvent e, long ms)
    {
        if (_charKey.Length == 0) return;
        switch (e)
        {
            case RecallEvent.AttunedLifestone:
            {
                RecallSpot here = SpotHere();
                here.Command = RecallMemory.DefaultCommand(RecallSlot.Sanctuary);
                AtlasLocation? ls = here.OnMap ? _atlas.Nearest("Lifestone", here.Ns, here.Ew, 40) : null;
                if (ls != null) here.Label = ls.Name + (here.Label.Length > 0 ? " (" + here.Label + ")" : "");
                _memory.Set(_charKey, RecallSlot.Sanctuary, here);
                Say($"@lifestone now takes you to {Describe(here)}.");
                break;
            }
            case RecallEvent.LinkedLifestone:
            {
                RecallSpot? spot = SelectedObjectSpot(out string selName) ?? NearestAtlasSpot("Lifestone", 40);
                if (spot == null) { Say("Lifestone tie noted, but not which lifestone (select it before casting)."); break; }
                if (selName.Length > 0) spot.Label = selName + (spot.Label.Length > 0 ? " (" + spot.Label + ")" : "");
                _memory.Set(_charKey, RecallSlot.Lifestone, spot);
                Say($"Lifestone Recall now takes you to {Describe(spot)}.");
                break;
            }
            case RecallEvent.LinkedPortal:
                OnPortalTied();
                break;
            case RecallEvent.RecallingMarketplace:
                _pendingRecallSlot = null;
                break;
            case RecallEvent.OwnSpellWords:
                // Any spell: only a teleport soon after, standing here, makes it a recall (OnTeleported).
                if (!_hasPose) break;
                _selfCastMs = ms; _selfCastWx = _wx; _selfCastWy = _wy; _selfCastDied = false;
                break;
            default:
                if (RecallMemory.SlotFor(e, out RecallSlot slot))
                {
                    _pendingRecallSlot = slot;
                    _pendingRecallMs = ms;
                    // The command you typed just before, if it was one (@house recall, /hr...).
                    string typed = _lastChatCommand;
                    _pendingRecallCommand = ms - _lastChatCommandMs < 5000 && typed.Length > 0 && typed[0] is '@' or '/' ? typed : "";
                }
                break;
        }
    }

    /// <summary>The selected object's place (a portal tie or lifestone tie's target).</summary>
    private RecallSpot? SelectedObjectSpot(out string name)
    {
        name = "";
        uint sel = Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0;
        if (sel == 0 || !Host.HasGetObjectPosition || !Host.TryGetObjectPosition(sel, out uint cell, out float x, out float y, out _)) return null;
        Host.TryGetObjectName(sel, out name);
        NavCoords.FromCell(cell, x, y, out double ns, out double ew);
        bool onMap = IsOnMap(cell);
        return new RecallSpot { OnMap = onMap, Ns = Math.Round(ns, 2), Ew = Math.Round(ew, 2), Label = onMap ? NearLabel(ns, ew) : "a dungeon" };
    }

    private RecallSpot? NearestAtlasSpot(string type, double maxUnits)
    {
        if (!PlayerMapPos(out double ns, out double ew)) return null;
        AtlasLocation? l = _atlas.Nearest(type, ns, ew, maxUnits);
        return l == null ? null : new RecallSpot { OnMap = true, Ns = l.Ns, Ew = l.Ew, Label = l.Name };
    }

    /// <summary>
    /// The tied portal: the selected portal, looked up in the Atlas by its weenie class (ACE
    /// ties the portal's class), else the nearest portal in the Atlas within <paramref name="nearUnits"/>.
    /// </summary>
    private AtlasLocation? TiePortal(double nearUnits)
    {
        AtlasLocation? portal = null;
        uint sel = Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0;
        PlayerMapPos(out double pns, out double pew);
        if (sel != 0 && Host.TryGetObjectWcid(sel, out uint wcid)) portal = _atlas.PortalByWcid(wcid, pns, pew);
        portal ??= _hasPose ? _atlas.Nearest("Portal", pns, pew, nearUnits) : null;
        return portal;
    }

    private static RecallSpot TieLanding(AtlasLocation portal) => portal.DestOnMap
        ? new RecallSpot { OnMap = true, Ns = portal.DestNs, Ew = portal.DestEw, Label = portal.Name }
        : new RecallSpot { OnMap = false, Label = portal.DestName.Length > 0 ? portal.DestName : portal.Name };

    /// <summary>/rnav tie 1|2 with no tie waiting: where the portal you've selected or stand at goes.</summary>
    private RecallSpot? TiedPortalSpot(out string why)
    {
        AtlasLocation? portal = TiePortal(PortalNearUnits);
        why = portal == null ? "select the tied portal, or stand at it, first"
            : !portal.HasDest ? $"{portal.Name} isn't in the Atlas with a destination" : "";
        return portal != null && portal.HasDest ? TieLanding(portal) : null;
    }

    private void OnPortalTied()
    {
        AtlasLocation? portal = TiePortal(60);
        if (portal == null || !portal.HasDest)
        {
            Say("Portal tie noted, but the portal isn't in the Atlas: its recall won't be used for routes.");
            return;
        }
        RecallSpot spot = TieLanding(portal);
        bool k1 = _known?.Contains(RecallPlanner.PrimaryPortalTie) ?? false, k2 = _known?.Contains(RecallPlanner.SecondaryPortalTie) ?? false;
        if (k1 && !k2) { _memory.Set(_charKey, RecallSlot.Tie1, spot); Say($"Primary portal tie: {portal.Name}."); return; }
        if (k2 && !k1) { _memory.Set(_charKey, RecallSlot.Tie2, spot); Say($"Secondary portal tie: {portal.Name}."); return; }
        _pendingTie = spot;
        Say($"Portal tie to {portal.Name} noted. Which tie was it? /rnav tie 1 (primary) or /rnav tie 2 (secondary).");
    }

    // ── The arrow ────────────────────────────────────────────────────────────

    private void UpdateArrow(bool teleported)
    {
        if (!_arrow.Active) return;
        ArrowStep? final = _arrow.Final;
        bool hasPos = PlayerMapPos(out double ns, out double ew);
        ArrowEvent e = _arrow.Update(hasPos, ns, ew, teleported);
        // A goto walking there reports its own arrival (it stops a few yards closer).
        if (e == ArrowEvent.Arrived && final != null && !_gotoActive)
            Say($"Arrived at {final.Name}.");
        else if (e == ArrowEvent.Advanced && _arrow.Current is ArrowStep next)
            Say($"Next: {StepText(next)}.");
    }

    private static string StepText(ArrowStep s) => s.Kind switch
    {
        ArrowStepKind.Recall => "cast " + s.Name,
        ArrowStepKind.Portal when !s.OnMap => s.Hint.Length > 0 ? s.Hint : "the portal " + s.Name,
        ArrowStepKind.Portal => "the portal " + s.Name + " at " + NavCoords.Fmt(s.Ns, s.Ew),
        _ => s.Name + " at " + NavCoords.Fmt(s.Ns, s.Ew),
    };

    /// <summary>A target from text: coordinates (with an optional name after them) or an Atlas name.</summary>
    private bool ResolveTarget(string text, out string name, out string type, out double ns, out double ew, out string error)
    {
        name = ""; type = ""; ns = 0; ew = 0; error = "";
        _resolvedPlace = null;
        text = text.Trim();
        if (text.Length == 0) { error = "give a place: a name from the Atlas, or coordinates like 42.1N, 33.6E"; return false; }
        if (NavCoords.TrySplitCoordsAndName(text, out ns, out ew, out string given))
        {
            name = given.Length > 0 ? given : NavCoords.Fmt(ns, ew);
            // The Atlas place at those coordinates, for its type (and name when none was given).
            double n0 = ns, e0 = ew;
            AtlasLocation? at = null;
            foreach (var l in _atlas.Locations)
                if (l.OnMap && NavCoords.Distance(n0, e0, l.Ns, l.Ew) < 3 && (given.Length == 0 || l.Name.Equals(given, StringComparison.OrdinalIgnoreCase)))
                { at = l; if (given.Length > 0) break; }
            if (at != null && given.Length > 0) { type = at.Type; _resolvedPlace = at; }
            return true;
        }
        bool hasPos = PlayerMapPos(out double pns, out double pew);
        AtlasLocation? best = _atlas.FindBest(text, hasPos, pns, pew);
        if (best == null)
        {
            error = _atlas.Locations.Count == 0 ? "the Atlas isn't loaded (" + _atlasStatus + ")" : $"no place called \"{text}\" on the map";
            return false;
        }
        name = best.Name; type = best.Type; ns = best.Ns; ew = best.Ew;
        _resolvedPlace = best;
        return true;
    }

    /// <summary>
    /// "a || b || c": the first candidate that is coordinates, or that names a place on the map
    /// as a whole word or more (Atlas.MatchScore 60+: "Mite Maze" yes, "Bur" inside "Burun" no),
    /// so a loose candidate never sends the character to an unrelated place. False (and why)
    /// when none does.
    /// </summary>
    private bool PickAlternative(string text, out string picked, out string why)
    {
        why = "";
        bool hasPos = PlayerMapPos(out double pns, out double pew);
        picked = _atlas.FirstOnMap(text, hasPos, pns, pew) ?? "";
        if (picked.Length > 0) return true;
        // Coordinates are always taken, so here every candidate was a name.
        why = _atlas.Locations.Count == 0
            ? "the Atlas isn't loaded (" + _atlasStatus + ")"
            : "none of these is a place on the map: " + text.Replace("||", ",");
        return false;
    }

    /// <summary>The Atlas place the last ResolveTarget picked (null for bare coordinates). Tick thread.</summary>
    private AtlasLocation? _resolvedPlace;

    private void SetArrow(string name, string type, double ns, double ew, bool announce)
    {
        _arrow.SetTarget(name, type, ns, ew);
        _store.Touch(new SavedPlace { Name = name, Type = type, Ns = ns, Ew = ew });
        _openArrowSeq++;
        if (announce) Say($"Arrow: {name}{(type.Length > 0 ? " (" + type + ")" : "")} at {NavCoords.Fmt(ns, ew)}{DistanceFromHere(ns, ew)}.");
    }

    private string DistanceFromHere(double ns, double ew)
    {
        if (!PlayerMapPos(out double pns, out double pew)) return "";
        double d = NavCoords.Distance(pns, pew, ns, ew);
        return ", " + NavCoords.FmtDistance(d) + " " + NavCoords.CompassPoint(NavCoords.Bearing(pns, pew, ns, ew));
    }

    // ── Planning with recalls ────────────────────────────────────────────────

    /// <summary>The recalls this character can use right now, and notes on the ones left out.</summary>
    private List<RecallOption> CurrentRecalls(List<string>? notes)
    {
        if (!_recallsEnabled) { notes?.Add("Recalls are off (/rnav recalls on)."); return new List<RecallOption>(); }
        if (_charKey.Length == 0) { notes?.Add("Not logged in yet."); return new List<RecallOption>(); }
        return RecallPlanner.Build(_atlas, _known, _memory, _charKey, notes);
    }

    // ── Recall steps of a route ──────────────────────────────────────────────

    private void BeginRecallStep()
    {
        _recallWait = true;
        _recallStartMs = NowMs;
        _recallLastCastMs = 0; _recallLastStanceMs = 0;
        _recallWandTried = false; _recallChangedStance = false; _recallSent = false; _recallWasPortaling = false;
        _recallPreLb = CurrentLandblock; _recallPreWx = _wx; _recallPreWy = _wy;
        BeginWand();
        AutoRun.Stop();
        RecallOption? o = CurrentRecallOption();
        lock (_gate) _status = o == null ? "recall step: unknown recall" : $"recalling: {o.Name}";
        Host.Log($"[RynthNav] recall step: {o?.Name ?? "?"}");
    }

    private RecallOption? CurrentRecallOption()
    {
        if (_route == null || _routeIdx >= _route.Count) return null;
        int i = _route[_routeIdx].RecallIndex;
        return i >= 0 && i < _routeRecalls.Count ? _routeRecalls[i] : null;
    }

    /// <summary>
    /// Casts the route's recall (magic stance, a wand if needed, CastSpell on yourself) or
    /// sends its command, then waits for the teleport the way a portal step does.
    /// (RynthAi's nav does the same for its Recall waypoints: NavigationEngine.HandlePortalOrRecall.)
    /// </summary>
    private void StepRecall()
    {
        long now = NowMs;
        RecallOption? o = CurrentRecallOption();
        if (o == null) { EndRecall(); FinishGoto("recall step lost — stopped"); return; }

        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        if (portaling) _recallWasPortaling = true;
        double moved = Math.Sqrt((_wx - _recallPreWx) * (_wx - _recallPreWx) + (_wy - _recallPreWy) * (_wy - _recallPreWy));
        if ((_recallWasPortaling && !portaling) || CurrentLandblock != _recallPreLb || moved > PortalJumpUnits)
        {
            Host.Log($"[RynthNav] recall {o.Name}: teleported (moved {moved:F0}u)");
            _lastLandMs = NowMs;
            EndRecall();
            AutoRun.Stop();
            RefreshTiles(CurrentLandblock);
            BeginSettle();
            return;
        }
        if (portaling) return;

        if (now - _recallStartMs > RecallStepTimeoutMs)
        {
            EndRecall();
            FinishGoto($"stopped: {o.Name} didn't take you anywhere");
            return;
        }

        if (o.SpellId == 0)
        {
            // A command recall (@lifestone, @house recall): send it once, again after 20 s.
            if (!_recallSent || now - _recallLastCastMs > 20000)
            {
                if (Host.HasInvokeChatParser) Host.InvokeChatParser(o.Command);
                _recallSent = true;
                _recallLastCastMs = now;
                // The server's "X is recalling home." that follows learns this command.
                _lastChatCommand = o.Command;
                _lastChatCommandMs = now;
            }
            return;
        }

        // A spell: a wand in hand first (RynthNavPlugin.Wand.cs), then the magic stance.
        if (!PrepareWand(now)) { lock (_gate) _status = $"recalling: {o.Name} (getting a wand)"; return; }
        int mode = Host.HasGetCurrentCombatMode ? Host.GetCurrentCombatMode() : CombatModeMagic;
        if (mode != CombatModeMagic)
        {
            if (now - _recallLastStanceMs > StanceRetryMs)
            {
                if (!_recallWandTried && now - _recallStartMs > WandAfterMs + WandGiveUpMs) { _recallWandTried = true; TryWieldWand(); }
                if (Host.HasChangeCombatMode) Host.ChangeCombatMode(CombatModeMagic);
                _recallChangedStance = true;
                _recallLastStanceMs = now;
                lock (_gate) _status = $"recalling: {o.Name} (magic stance…)";
            }
            return;
        }
        if (_recallLastCastMs == 0 || now - _recallLastCastMs > RecallRecastMs)
        {
            uint me = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
            if (me != 0 && Host.HasCastSpell)
            {
                Host.CastSpell(me, o.SpellId);
                _castRecallSpell = o.SpellId;
                _castRecallMs = now;
                Host.Log($"[RynthNav] cast {o.Name} ({o.SpellId})");
            }
            _recallLastCastMs = now;
            lock (_gate) _status = $"recalling: {o.Name}";
        }
    }

    private void EndRecall()
    {
        _recallWait = false;
        if (_recallChangedStance && Host.HasChangeCombatMode) Host.ChangeCombatMode(CombatModeNonCombat);
        _recallChangedStance = false;
    }

    /// <summary>Wields the first caster found in your pack (UseObject, as RynthAi equips its wand).</summary>
    private void TryWieldWand()
    {
        if (!Host.HasGetLiveObjectIds || !Host.HasUseObject) return;
        uint me = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (me == 0) return;
        foreach (uint id in Host.GetLiveObjectIds())
        {
            if (!Host.TryGetItemType(id, out uint type) || (type & ItemTypeCaster) == 0) continue;
            if (!Host.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _)) continue;
            if (wielder == me) return;   // already in hand: the stance is refused for another reason
            bool mine = container == me
                || (container != 0 && Host.TryGetObjectOwnershipInfo(container, out uint outer, out _, out _) && outer == me);
            if (!mine) continue;
            Host.UseFor(id, "Recall", "wield a caster for the recall spell", UseKind.Asked);
            Host.Log($"[RynthNav] recall: wielding caster 0x{id:X8}");
            return;
        }
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    private void SaveStores(bool force)
    {
        if (!_storesLoaded) return;
        if (_store.Version != _savedStoreVersion || force)
        {
            _savedStoreVersion = _store.Version;
            WriteLater("atlas.txt", _store.Serialize());
        }
        if (_memory.Version != _savedMemoryVersion || force)
        {
            _savedMemoryVersion = _memory.Version;
            WriteLater("recalls.txt", _memory.Serialize());
        }
    }

    private readonly object _writeGate = new();

    private void WriteLater(string file, string text)
    {
        string path = NavFile(file);
        Task.Run(() =>
        {
            try
            {
                lock (_writeGate)
                {
                    Directory.CreateDirectory(NavDataDir);
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, text);
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception ex) { Host.Log($"[RynthNav] saving {file}: {ex.Message}"); }
        });
    }

    // ── Status for the panel ─────────────────────────────────────────────────

    private void RebuildTravelJson()
    {
        int knownCount = _known?.Count ?? -1;
        int routeIdx = _gotoActive && _route != null ? _routeIdx : -1;
        string arrowExtra = _arrow.Active ? _arrow.ArriveUnits.ToString("F0", CultureInfo.InvariantCulture) : "";
        string avoidKey = AvoidSummary();
        if (_arrow.Version == _builtArrowV && _store.Version == _builtStoreV && _memory.Version == _builtMemV
            && _routeVersion == _builtRouteV && _openArrowSeq == _builtOpenSeq && knownCount == _builtKnownCount
            && _portalsEnabled == _builtPortals && _recallsEnabled == _builtRecalls && _gotoActive == _builtGoto
            && routeIdx == _builtRouteIdx && arrowExtra == _builtArrowExtra && ReferenceEquals(_builtAtlas, _atlas)
            && ReferenceEquals(_builtAtlasStatus, _atlasStatus) && ReferenceEquals(_builtPendingTie, _pendingTie)
            && avoidKey == _builtAvoidKey)
            return;
        _builtArrowV = _arrow.Version; _builtStoreV = _store.Version; _builtMemV = _memory.Version;
        _builtRouteV = _routeVersion; _builtOpenSeq = _openArrowSeq; _builtKnownCount = knownCount; _builtAtlas = _atlas; _builtAtlasStatus = _atlasStatus;
        _builtPortals = _portalsEnabled; _builtRecalls = _recallsEnabled; _builtGoto = _gotoActive;
        _builtRouteIdx = routeIdx; _builtArrowExtra = arrowExtra; _builtPendingTie = _pendingTie; _builtAvoidKey = avoidKey;

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(2048);
        Atlas atlas = _atlas;
        sb.Append(",\"atlas\":{\"path\":\"").Append(Esc(NavFile("locations.json"))).Append("\",\"count\":").Append(atlas.Locations.Count)
          .Append(",\"generated\":\"").Append(Esc(atlas.Generated)).Append("\",\"status\":\"").Append(Esc(_atlasStatus)).Append("\"}");
        sb.Append(",\"portalsOn\":").Append(_portalsEnabled ? 1 : 0).Append(",\"recallsOn\":").Append(_recallsEnabled ? 1 : 0);
        sb.Append(",\"avoidPortals\":").Append(_avoidPortals ? 1 : 0).Append(",\"avoidPortalM\":").Append(PortalAvoid.LiveRadius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"avoidWalls\":").Append(_avoidWalls ? 1 : 0).Append(",\"avoidWallM\":").Append(_wallClearance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"cornerM\":").Append(_cornerReach.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"openArrow\":").Append(_openArrowSeq);

        // Arrow
        sb.Append(",\"arrow\":");
        ArrowStep? cur = _arrow.Current;
        if (cur == null) sb.Append("null");
        else
        {
            ArrowStep fin = _arrow.Final!;
            sb.Append("{\"name\":\"").Append(Esc(cur.Name)).Append("\",\"type\":\"").Append(Esc(cur.Type))
              .Append("\",\"kind\":\"").Append(cur.Kind == ArrowStepKind.Recall ? "recall" : cur.Kind == ArrowStepKind.Portal ? "portal" : "place")
              .Append("\",\"onMap\":").Append(cur.OnMap ? 1 : 0)
              .Append(",\"ns\":").Append(cur.Ns.ToString("F3", ci)).Append(",\"ew\":").Append(cur.Ew.ToString("F3", ci))
              .Append(",\"step\":").Append(_arrow.Index + 1).Append(",\"steps\":").Append(_arrow.Count)
              .Append(",\"hint\":\"").Append(Esc(cur.Hint)).Append("\",\"final\":\"").Append(Esc(fin.Name))
              .Append("\",\"finalType\":\"").Append(Esc(fin.Type))
              .Append("\",\"finalNs\":").Append(fin.Ns.ToString("F3", ci)).Append(",\"finalEw\":").Append(fin.Ew.ToString("F3", ci))
              .Append(",\"arrive\":").Append(arrowExtra).Append('}');
        }

        // Route (the last one planned)
        sb.Append(",\"route\":");
        if (_shownRoute == null) sb.Append("null");
        else
        {
            sb.Append("{\"for\":\"").Append(Esc(_shownRouteFor)).Append("\",\"est\":").Append(_shownRouteEst.ToString("F0", ci))
              .Append(",\"active\":").Append(_gotoActive && _shownRouteLive ? 1 : 0)
              .Append(",\"idx\":").Append(_gotoActive && _shownRouteLive ? (_route != null ? routeIdx : 0) : -1).Append(",\"steps\":[");
            for (int i = 0; i < _shownRoute.Count; i++)
            {
                RouteStep s = _shownRoute[i];
                if (i > 0) sb.Append(',');
                string kind = s.UseRecall ? "recall" : s.IsHub ? "town" : s.UsePortal ? "portal" : "walk";
                sb.Append("{\"k\":\"").Append(kind).Append("\",\"l\":\"").Append(Esc(s.Label))
                  .Append("\",\"ns\":").Append(s.Ns.ToString("F2", ci)).Append(",\"ew\":").Append(s.Ew.ToString("F2", ci));
                if (!double.IsNaN(s.LandNs))
                    sb.Append(",\"lns\":").Append(s.LandNs.ToString("F2", ci)).Append(",\"lew\":").Append(s.LandEw.ToString("F2", ci));
                sb.Append('}');
            }
            sb.Append("]}");
        }

        // Recalls this character can use, and why others are left out
        var notes = new List<string>();
        List<RecallOption> recalls = _charKey.Length == 0 ? new List<RecallOption>() : RecallPlanner.Build(atlas, _known, _memory, _charKey, notes);
        if (_charKey.Length == 0) notes.Add("Not logged in yet.");
        sb.Append(",\"recalls\":[");
        for (int i = 0; i < recalls.Count; i++)
        {
            RecallOption r = recalls[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"n\":\"").Append(Esc(r.Name)).Append("\",\"ns\":").Append(r.Ns.ToString("F2", ci))
              .Append(",\"ew\":").Append(r.Ew.ToString("F2", ci)).Append(",\"how\":\"")
              .Append(Esc(r.SpellId != 0 ? "spell " + r.SpellId.ToString(ci) : r.Command)).Append("\",\"learned\":").Append(r.Learned ? 1 : 0).Append('}');
        }
        sb.Append("],\"recallNotes\":[");
        for (int i = 0; i < notes.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(Esc(notes[i])).Append('"'); }
        sb.Append(']');

        AppendPlaces(sb, "favs", _store.Favorites, ci);
        AppendPlaces(sb, "recent", _store.Recent, ci);
        AppendMyRecalls(sb, ci);
        lock (_gate) _travelJson = sb.ToString();
    }

    private static void AppendPlaces(StringBuilder sb, string key, List<SavedPlace> places, CultureInfo ci)
    {
        sb.Append(",\"").Append(key).Append("\":[");
        for (int i = 0; i < places.Count; i++)
        {
            SavedPlace p = places[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"n\":\"").Append(Esc(p.Name)).Append("\",\"t\":\"").Append(Esc(p.Type))
              .Append("\",\"ns\":").Append(p.Ns.ToString("F2", ci)).Append(",\"ew\":").Append(p.Ew.ToString("F2", ci)).Append('}');
        }
        sb.Append(']');
    }

    // ── Commands (chat /rnav, the engine panel, other plugins) ───────────────

    /// <summary>Queues a /rnav command (without "/rnav") for the tick thread. Any thread.</summary>
    public void EnqueueCommand(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) _commands.Enqueue(text.Trim());
    }

    private const string HelpText =
        "/rnav arrow <place|coords> | arrow off | arrow route <place> — the arrow\n" +
        "/rnav go <place|coords> — walk there (route with portals/recalls when /rnav portals on)\n" +
        "/rnav route <place|coords> — plan a route (portals and recalls), no walking\n" +
        "/rnav atlas <search> — find places | fav [place] — favorite | recalls [on|off] | tie 1|2|lifestone\n" +
        "/rnav goto <coords> | preview <coords> | portals on|off | stop | here | load | test | navdata\n" +
        "/rnav avoid — show; avoid portals on|off|<m> | walls on|off|<m> | corners <m>\n" +
        "/rnav markers on|off | markers labels on|off | markers guide on|off — the trip on the ground\n" +
        "/rnav mesh on|off | floor on|off | walls on|off | radius <m> | triangles <n> — draw the navmesh: green floor, red walls\n" +
        "/rnav saveroute [name] [overwrite] — save the last trip as a RynthAi nav route (NavProfiles)";

    /// <summary>One /rnav command. Tick thread.</summary>
    private void RunCommand(string raw, bool fromPanel)
    {
        int sp = raw.IndexOf(' ');
        string cmd = (sp < 0 ? raw : raw.Substring(0, sp)).ToLowerInvariant();
        string rest = sp < 0 ? string.Empty : raw.Substring(sp + 1).Trim();
        // "go a || b || c" (Oracle's Dynamic Stamps, any caller): the first candidate on the map.
        if (cmd is "go" or "goto" or "route" or "arrow" && rest.Contains("||", StringComparison.Ordinal))
        {
            if (!PickAlternative(rest, out string picked, out string why)) { Note(why); return; }
            rest = picked;
        }
        switch (cmd)
        {
            case "":
            case "help":
                foreach (string line in HelpText.Split('\n')) Say(line);
                break;
            case "here": Say(Where()); break;
            // load/test/preview run in ProcessRequests and answer in chat from there
            // (they used to report to the panel only); goto reports its own start and end.
            case "load": _echoChat = true; DoLoadTile(); break;
            case "test": _echoChat = true; DoTestQuery(); break;
            case "preview": _echoChat = true; DoPreviewPath(rest); break;
            case "navdata": Say($"tiles and Atlas folder: {NavDataDir} ({NavConfig.Source}); route graph: {_graphStatus}; Town Network: {_townNetStatus}"); break;
            case "avoid": CmdAvoid(rest); break;
            case "aphus": CmdAphus(rest.Trim().ToLowerInvariant()); break;
            case "portals":
                _portalsEnabled = !rest.Equals("off", StringComparison.OrdinalIgnoreCase);
                NavDataConfig.WriteSwitch("portals", _portalsEnabled);
                Say($"portal and recall routing {(_portalsEnabled ? "ON" : "OFF")} for walking (go/goto)");
                break;
            case "stop": DoMove(5); Say("stopped"); break;
            case "goto":
            case "go":
                CmdGo(rest);
                break;
            case "route": CmdRoute(rest); break;
            case "arrow": CmdArrow(rest); break;
            case "atlas": CmdAtlas(rest); break;
            case "fav": CmdFav(rest); break;
            case "recent":
                if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase)) { _store.ClearRecent(); Say("recent places cleared"); }
                else Say(NavReplies.Recent(_store.Recent));
                break;
            case "recalls": CmdRecalls(rest); break;
            case "tie": CmdTie(rest); break;
            case "markers": CmdMarkers(rest); break;
            case "mesh": CmdMesh(rest); break;
            case "saveroute": CmdSaveRoute(rest); break;
            case "arrive":
                if (NavReplies.TryArrive(rest, out double yd)) { _arrow.ArriveUnits = yd; Say(NavReplies.Arrived(yd)); }
                else if (rest.Length == 0) Say(NavReplies.ArriveUsage(rest, _arrow.ArriveUnits));
                else SayFail(NavReplies.ArriveUsage(rest, _arrow.ArriveUnits));
                break;
            default: SayFail(NavReplies.Unknown(cmd)); break;
        }
    }

    private void CmdArrow(string rest)
    {
        if (rest.Length == 0)
        {
            ArrowStep? cur = _arrow.Current;
            Say(cur == null ? "no arrow — /rnav arrow <place or coords>" : $"arrow: {StepText(cur)}{(cur.OnMap ? DistanceFromHere(cur.Ns, cur.Ew) : "")}");
            return;
        }
        if (rest.Equals("off", StringComparison.OrdinalIgnoreCase) || rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _arrow.Clear();
            Say("arrow off");
            return;
        }
        if (rest.StartsWith("route ", StringComparison.OrdinalIgnoreCase))
        {
            string where = rest.Substring(6);
            if (!ResolveTarget(where, out string n, out string t, out double rns, out double rew, out string err)) { Note(err); return; }
            List<RouteStep>? steps = PlanAndShow(n, rns, rew, out int used, out double est, out string refusal);
            if (refusal.Length > 0) Note("arrow route: " + refusal);
            if (steps == null || used == 0) { SetArrow(n, t, rns, rew, announce: true); return; }
            _arrow.SetRoute(steps, n, t, rns, rew);
            _store.Touch(new SavedPlace { Name = n, Type = t, Ns = rns, Ew = rew });
            _openArrowSeq++;
            Say($"arrow route to {n}: {used} teleport(s). First: {StepText(_arrow.Current!)}.");
            return;
        }
        if (!ResolveTarget(rest, out string name, out string type, out double ns, out double ew, out string error)) { Note(error); return; }
        SetArrow(name, type, ns, ew, announce: true);
    }

    private void CmdGo(string rest)
    {
        string name, type; double ns, ew;
        AtlasLocation? place;
        if (rest.Length == 0)
        {
            ArrowStep? fin = _arrow.Final;
            if (fin == null || !fin.OnMap) { Note("go where? /rnav go <place or coords> (or set the arrow first)"); return; }
            name = fin.Name; type = fin.Type; ns = fin.Ns; ew = fin.Ew;
            place = null;
            foreach (var l in _atlas.Locations)
                if (l.OnMap && l.Name == fin.Name && l.Type == fin.Type && NavCoords.Distance(l.Ns, l.Ew, ns, ew) < 3) { place = l; break; }
        }
        else if (!ResolveTarget(rest, out name, out type, out ns, out ew, out string error))
        {
            // A dungeon or portal the Atlas knows, but with no map position: say why, don't guess.
            foreach (var l in _atlas.Search(rest, null, 5))
                if (!l.OnMap && Entrance.IsEntrance(l))
                {
                    Entrance.PortalFor(_atlas, l, false, 0, 0, out string offWhy);
                    if (offWhy.Length > 0) { Note("goto: " + offWhy); return; }
                }
            Note(error);
            return;
        }
        else place = _resolvedPlace;

        // A dungeon (or a portal): walk INTO its entrance portal and wait for the teleport.
        string enter = "", enterType = "";
        if (place != null && Entrance.IsEntrance(place))
        {
            bool hasPos = PlayerMapPos(out double pns, out double pew);
            AtlasLocation? portal = Entrance.PortalFor(_atlas, place, hasPos, pns, pew, out string why);
            if (portal == null) { Note("goto: " + why); return; }
            ns = portal.Ns; ew = portal.Ew; enter = place.Name; enterType = place.Type;
            _nextEnterPortal = portal.Name;   // the portal object a saved nav route uses (RynthNavPlugin.SaveRoute.cs)
        }
        StartGoto(name, type, ns, ew, enter, enterType);   // StartWalk says "walking to ..." (or why it can't)
    }

    private void CmdRoute(string rest)
    {
        if (!ResolveTarget(rest, out string name, out _, out double ns, out double ew, out string error)) { Note(error); return; }
        if (!_hasPose) { Note("no player pose yet"); return; }
        List<RouteStep>? steps = PlanAndShow(name, ns, ew, out int used, out double est, out string refusal);
        if (refusal.Length > 0) { Note($"route to {name}: {refusal}"); return; }
        if (steps == null || used == 0)
        {
            Note(_portalsEnabled
                ? $"route to {name}: walk ({NavCoords.FmtDistance(est)}; no portal or recall helps)"
                : $"route to {name}: walk ({NavCoords.FmtDistance(est)}; portals and recalls are off)");
            return;
        }
        _goNote = $"route to {name}: {used} teleport(s), {steps.Count} step(s)";
        Say($"route to {name} {NavCoords.Fmt(ns, ew)}: ~{est:F0} yd-equivalent, {used} teleport(s):");
        for (int i = 0; i < steps.Count; i++)
        {
            RouteStep s = steps[i];
            string act = s.UseRecall ? $"recall: {s.Label}"
                : s.IsHub ? $"in the Town Network, walk to the {s.Label} and take it to {NavCoords.Fmt(s.LandNs, s.LandEw)}"
                : s.UsePortal ? $"walk to {NavCoords.Fmt(s.Ns, s.Ew)}, take '{Trunc(s.Label)}'" : $"walk to {NavCoords.Fmt(s.Ns, s.Ew)}";
            Say($"  {i + 1}. {act}");
        }
    }

    private void CmdAtlas(string rest)
    {
        Atlas atlas = _atlas;
        if (atlas.Locations.Count == 0) { Say("the Atlas isn't loaded (" + _atlasStatus + ")"); return; }
        if (rest.Length == 0) { Say($"Atlas: {atlas.Locations.Count} places ({atlas.Generated}). /rnav atlas <search>"); return; }
        bool hasPos = PlayerMapPos(out double pns, out double pew);
        List<AtlasLocation> hits = atlas.Search(rest, null, 9);
        if (hits.Count == 0) { Say($"no place matches \"{rest}\""); return; }
        foreach (var l in hits)
        {
            string where = l.OnMap ? NavCoords.Fmt(l.Ns, l.Ew) + (hasPos ? ", " + NavCoords.FmtDistance(NavCoords.Distance(pns, pew, l.Ns, l.Ew)) : "") : (l.Desc.Length > 0 ? l.Desc : "in a dungeon");
            string dest = l.HasDest ? " → " + (l.DestOnMap ? NavCoords.Fmt(l.DestNs, l.DestEw) : l.DestName.Length > 0 ? l.DestName : "a dungeon") : "";
            Say($"  {l.Name} ({l.Type}) {where}{dest}");
        }
    }

    private void CmdFav(string rest)
    {
        if (rest.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
        {
            string n = rest.Substring(7).Trim();
            Say(_store.RemoveFavorite(n) ? $"{n} is no longer a favorite" : $"no favorite called {n}");
            return;
        }
        SavedPlace p;
        if (rest.Length == 0)
        {
            ArrowStep? fin = _arrow.Final;
            if (fin == null || !fin.OnMap) { Say("favorite what? /rnav fav <place> (or set the arrow first)"); return; }
            p = new SavedPlace { Name = fin.Name, Type = fin.Type, Ns = fin.Ns, Ew = fin.Ew };
        }
        else
        {
            if (!ResolveTarget(rest, out string n, out string t, out double ns, out double ew, out string err)) { Say(err); return; }
            p = new SavedPlace { Name = n, Type = t, Ns = ns, Ew = ew };
        }
        Say(_store.ToggleFavorite(p) ? $"{p.Name} added to favorites" : $"{p.Name} removed from favorites");
    }

    private void CmdRecalls(string rest)
    {
        if (rest.Equals("on", StringComparison.OrdinalIgnoreCase) || rest.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _recallsEnabled = rest.Equals("on", StringComparison.OrdinalIgnoreCase);
            NavDataConfig.WriteSwitch("recalls", _recallsEnabled);
            Say($"recalls in routes {(_recallsEnabled ? "ON" : "OFF")}");
            return;
        }
        var notes = new List<string>();
        List<RecallOption> list = CurrentRecalls(notes);
        Say(list.Count == 0 ? "no recall can be used for routes yet" : $"{list.Count} recall(s) for routes:");
        foreach (var o in list) Say($"  {o.Name} → {NavCoords.Fmt(o.Ns, o.Ew)}{(o.SpellId == 0 ? " (" + o.Command + ")" : "")}");
        foreach (string n in notes) Say("  " + n);
    }

    /// <summary>
    /// /rnav tie 1|2: which portal tie the last one (or the last tie recall cast by hand) was;
    /// with none waiting, the portal you have selected or stand at is that tie (a tie made
    /// before RynthNav was there). /rnav tie lifestone: the lifestone you have selected or
    /// stand at is the one Lifestone Recall is tied to. /rnav tie clear 1|2|lifestone forgets one.
    /// </summary>
    private void CmdTie(string rest)
    {
        if (_charKey.Length == 0) { Note("not logged in yet"); return; }
        string arg = rest.Trim().ToLowerInvariant();
        bool clear = arg.StartsWith("clear", StringComparison.Ordinal);
        if (clear) arg = arg.Substring(5).Trim();
        RecallSlot slot = arg switch
        {
            "1" => RecallSlot.Tie1,
            "2" => RecallSlot.Tie2,
            "lifestone" or "ls" => RecallSlot.Lifestone,
            _ => (RecallSlot)(-1),
        };
        if ((int)slot < 0) { Say("/rnav tie 1 (primary) | 2 (secondary) | lifestone, or /rnav tie clear 1|2|lifestone"); return; }
        string name = RecallPlanner.Title(slot);
        if (clear)
        {
            _memory.Clear(_charKey, slot);
            Note($"{name}: landing forgotten (routes won't use it until it's learned again)");
            return;
        }
        RecallSpot? spot;
        if (slot == RecallSlot.Lifestone)
        {
            // The Atlas lifestone at the selected object (anything else selected doesn't count), else the nearest one.
            RecallSpot? sel = SelectedObjectSpot(out _);
            AtlasLocation? ls = sel != null && sel.OnMap ? _atlas.Nearest("Lifestone", sel.Ns, sel.Ew, LifestoneLandUnits) : null;
            spot = ls != null ? new RecallSpot { OnMap = true, Ns = ls.Ns, Ew = ls.Ew, Label = ls.Name } : NearestAtlasSpot("Lifestone", 40);
            if (spot == null) { Note("tie lifestone: select your tied lifestone, or stand at it, first"); return; }
        }
        else if (_pendingTie != null) { spot = _pendingTie; _pendingTie = null; }
        else if ((spot = TiedPortalSpot(out string why)) == null) { Note($"tie {arg}: {why}"); return; }
        _memory.Set(_charKey, slot, spot);
        Note($"{name}: {Describe(spot)}");
    }

    /// <summary>Plans a route (portals and, unless off, recalls) and shows it in the panel. Tick thread.</summary>
    private List<RouteStep>? PlanAndShow(string name, double ns, double ew, out int used, out double est, out string refusal)
    {
        TravelPlan.Result plan = PlanRoute(name, ns, ew, out List<RecallOption> recalls);
        List<RouteStep>? steps = plan.Steps;
        used = plan.Teleports; est = plan.Est; refusal = plan.Refusal;
        _shownRoute = steps ?? new List<RouteStep> { new(ns, ew, false, false, "walk") };
        _shownRouteFor = name;
        _shownRouteEst = est;
        _shownRouteLive = false;
        _routeVersion++;
        if (refusal.Length == 0) SetTrip(steps, name, ns, ew, recalls, "", live: false);
        return steps;
    }

    /// <summary>Starts walking to a place: the arrow points along the same route.</summary>
    private void StartGoto(string name, string type, double ns, double ew, string enter = "", string enterType = "")
    {
        _store.Touch(new SavedPlace { Name = name, Type = type, Ns = ns, Ew = ew });
        lock (_reqGate) { _reqGotoNamed = (name, type, ns, ew, enter, enterType); }
    }
}
