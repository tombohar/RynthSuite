using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Live object cache tracking AC world objects via engine events.
///
/// Classification strategy:
///   - Creature   : received OnUpdateHealth OR TryGetItemType returns TYPE_CREATURE flag
///   - Corpse     : world object with a corpse-like name (e.g. "Foo's Corpse")
///   - Inventory  : TryGetObjectPosition returns false (not placed in world = in a container)
///                  OR item GUID (0xC0000000+) with no position
///   - Landscape  : TryGetObjectPosition returns true + not a creature = static object
///   - Weapon type: TryGetItemType flags first; name-based heuristics as fallback
///
/// THREADING: the mutating handlers (OnCreateObject/OnDeleteObject/OnUpdateHealth) and the
/// classify pump (Tick → TryClassify/ReclassifyUnknownDynamics) all run on the engine's
/// plugin pump thread — the engine queues AC's main-thread object events and dispatches them
/// (PluginManager.ProcessPendingActions) then runs TickAll, sequentially on that one thread.
/// HOWEVER the read enumerators (GetLandscape/GetLandscapeObjects/GetInventory/AllKnownObjects/
/// GetContainedItems/GetDirectInventory) are ALSO pulled from the Avalonia panel poll thread
/// (~10 Hz) for the radar/dashboard snapshot. Enumerating a collection there while the pump
/// mutates it throws "Collection was modified"; escaping the snapshot's reverse-P/Invoke
/// boundary that fail-fasts the NativeAOT runtime (0xC0000602). So ALL collection access goes
/// through _gate: mutators lock their whole body, enumerators copy-under-lock then iterate the
/// copy outside the lock. The _host.* reads used here are non-blocking/cache-served off-thread,
/// so holding _gate across them cannot deadlock against AC's main thread.
/// </summary>
public class WorldObjectCache
{
    private readonly RynthCoreHost _host;

    // Serializes all collection access (see the THREADING note above). Single reentrant
    // monitor — the indexer re-enters via EnsureInCache — and one lock means no lock-ordering
    // deadlock is possible. Contended only between the pump thread and the ~10 Hz Avalonia poll.
    private readonly object _gate = new();

    private readonly Dictionary<int, WorldObject> _byId = new();
    private readonly HashSet<int> _creatures = new();   // received OnUpdateHealth or TYPE_CREATURE
    private readonly HashSet<int> _landscape = new();   // has physics position
    private readonly HashSet<int> _inventory = new();   // no physics position
    private readonly Dictionary<int, float> _healthRatios = new(); // last known health ratio per object (0-1)
    // Ids whose ratio is an estimate from our own damage lines (a ring, wall or chain spell's other
    // targets: the server sends health only for the selected target). A real health update replaces
    // the estimate and takes the id out of this set.
    private readonly HashSet<int> _estimatedHealth = new();
    // When the server's health for an id last arrived here (Clock), for ranking only: CombatManager's
    // Lowest health compares it with the age of the engine plates' reading (API v93, 2026-11-07).
    private readonly Dictionary<int, DateTime> _serverHealthAt = new();
    /// <summary>The clock <see cref="TryGetServerHealthTime"/> stamps with (host tests set their own).</summary>
    internal Func<DateTime> Clock { get; set; } = () => RynthClock.Now;
    /// <summary>The lowest an estimate goes: 0 means dead to every reader, and only the server says that.</summary>
    internal const float EstimatedHealthFloor = 0.01f;

    // OnCreateObject IDs pending classification
    private readonly Queue<uint> _pending = new();
    // Classification work per tick: up to MaxClassifyPerTick ids, and no more than
    // ClassifyBudgetMs of the plugin tick once at least MinClassifyPerTick are done. Was a flat
    // 30, so a 1000-create flood (2026-10-02) took 34+ ticks, longer when the tick slowed.
    private const int MaxClassifyPerTick = 250;
    private const int MinClassifyPerTick = 30;
    private const double ClassifyBudgetMs = 3.0;
    // Fast retries wait for the next tick: the engine's name/position snapshots only change
    // between ticks, so the old same-tick re-queue spent all 8 retries in ~10 ms on the same
    // answer (and logged each one) before parking the id in the 2 s slow-retry.
    private readonly Queue<uint> _retryNext = new();

    // Retry tracking for 0x8000xxxx objects that have no weenie yet (e.g. spawning corpses)
    private readonly Dictionary<uint, int> _classifyRetry = new();
    private const int MaxClassifyRetries = 8;

    // Slow-retry rescue. When the 8-tick fast-retry burst exhausts (one attempt per
    // tick since 2026-10-02, ~250 ms; before that all 8 ran within ~10 ms), the uid
    // moves here instead of being abandoned. Every ReclassifyIntervalSec the
    // entries are flushed back into _pending so the engine has fresh chances to
    // populate name+position. Successful classification evicts the uid at the
    // top of TryClassify; a fresh give-up re-adds it. OnDeleteObject cleans up.
    // Roots out the "respawned monster invisible until clicked" bug where AC's
    // weenie data lagged behind OnCreateObject by more than 10 ms.
    private readonly HashSet<uint> _slowRetry = new();

    // Objects deleted while still pending classification — skip to avoid stale-pointer AV
    private readonly HashSet<uint> _deletedWhilePending = new();

    // Periodic reclassification — rescues Unknown landscape objects that become recognisable later
    private DateTime _lastReclassifyTime = DateTime.MinValue;
    private const double ReclassifyIntervalSec = 2.0;

    // Diagnostic state for the "bot ignores mobs until clicked" investigation.
    // Records the latest reason each Unknown landscape uid keeps failing to be promoted
    // to creature so we can correlate skip ↔ HEALTHADD-RESCUE ↔ promotion in the log.
    // State-change suppressed per uid; cleared on promotion / health-add rescue.
    private readonly Dictionary<uint, string> _reclassifySkipState = new();
    private int _reclassifyDiagCount;
    private int _reclassifyDiagSummaryCount;
    private const int MaxReclassifyDiagLines = 40;
    private const int MaxReclassifyDiagSummaries = 60;

    // Diagnostic — track every uid OnCreateObject ever saw. Lets HEALTHADD-RESCUE
    // and INDEXER-RESCUE log lines flag the case where a mob enters _creatures via
    // a path other than OnCreateObject → TryClassify (i.e. the engine's CreateObject
    // hook never fired for this respawn — the suspected bug). No eviction: a delete +
    // recreate of the same id MUST keep showing seenCreate=1 to be diagnostic-correct.
    private readonly HashSet<uint> _seenCreateObject = new();

    // [ClassifyTrace] / [ReclassifyDiag]: a few full lines per kind per session, then counts
    // and sample ids in one summary line every ReclassifyIntervalSec (see ObjectDiagLog). Was
    // one line per object per attempt (caps 200-5000), ~950 lines for the 2026-10-02 flood.
    private readonly ObjectDiagLog _diag;
    private const string DeleteBeforeClassify = "DELETE-BEFORE-CLASSIFY";
    private const string HealthAddRescue = "HEALTHADD-RESCUE";
    private const string DeleteWhilePendingSkip = "DELETE-WHILE-PENDING-SKIP";
    private const string ClassifyGiveup = "CLASSIFY-GIVEUP";
    private const string IndexerRescue = "INDEXER-RESCUE";
    private static readonly string[] TraceNotes =
    {
        "noNamePos", "nopos-creature", "nopos-item", "nopos-retry", "nopos-giveup",
        "pos", "attackable->creature", "healthAdd->creature",
    };

    // ItemType flag constants (AC ITEM_TYPE bitmask)
    private const uint ItemTypeMeleeWeapon              = 0x00000001;
    private const uint ItemTypeArmor                    = 0x00000002;
    private const uint ItemTypeClothing                 = 0x00000004;
    private const uint ItemTypeJewelry                  = 0x00000008;
    private const uint ItemTypeCreature                 = 0x00000010;
    private const uint ItemTypeFood                     = 0x00000020;
    private const uint ItemTypeMoney                    = 0x00000040;
    private const uint ItemTypeMisc                     = 0x00000080;
    private const uint ItemTypeMissileWeapon            = 0x00000100;
    private const uint ItemTypeContainer                = 0x00000200;
    private const uint ItemTypeGem                      = 0x00000800;
    private const uint ItemTypeSpellComponents          = 0x00001000;
    private const uint ItemTypeWritable                 = 0x00002000;
    private const uint ItemTypeKey                      = 0x00004000;
    private const uint ItemTypeCaster                   = 0x00008000;
    private const uint ItemTypePortal                   = 0x00010000;
    private const uint ItemTypePromissoryNote           = 0x00040000;
    private const uint ItemTypeManaStone                = 0x00080000;
    private const uint ItemTypeService                  = 0x00100000;
    private const uint ItemTypeCraftCookingBase         = 0x00400000;
    private const uint ItemTypeCraftAlchemyBase         = 0x00800000;
    private const uint ItemTypeCraftFletchingBase       = 0x02000000;
    private const uint ItemTypeCraftAlchemyIntermediate = 0x04000000;
    private const uint ItemTypeCraftFletchingIntermediate = 0x08000000;
    private const uint ItemTypeLifeStone                = 0x10000000;
    private const uint ItemTypeTinkeringTool            = 0x20000000;
    private const uint ItemTypeTinkeringMaterial        = 0x40000000;

    private uint _playerId; // set at login; health updates for self are ignored
    private DateTime _loginTime = DateTime.MinValue; // when SetPlayerId was called

    public WorldObjectCache(RynthCoreHost host)
    {
        _host = host;
        _diag = new ObjectDiagLog(line => _host.Log(line));
        _diag.Define(DeleteBeforeClassify, "[ReclassifyDiag]", 10, "seenCreate");
        _diag.Define(HealthAddRescue, "[ReclassifyDiag]", 10, "seenCreate", "inLandscape", "inById", "wasSkipped");
        _diag.Define(DeleteWhilePendingSkip, "[ReclassifyDiag]", 10);
        _diag.Define(ClassifyGiveup, "[ReclassifyDiag]", 10);
        _diag.Define(IndexerRescue, "[ReclassifyDiag]", 20, "seenCreate");
        foreach (string note in TraceNotes)
            _diag.Define(note, "[ClassifyTrace]", 0);   // detail lines: TraceClassify's own per-uid rule
    }

    /// <summary>Summary lines the bounded diagnostics wrote (tests).</summary>
    internal int DiagSummaryLines => _diag.SummaryLines;

    public void SetPlayerId(uint playerId)
    {
        lock (_gate)
        {
            _playerId = playerId;
            _loginTime = RynthClock.Now;
            _deletedWhilePending.Clear();
            // Remove self if mistakenly added as creature before login completed
            if (playerId == 0) return;
            int sid = (int)playerId;
            _creatures.Remove(sid);
            _landscape.Remove(sid);
            _byId.Remove(sid);
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────

    public void OnCreateObject(uint id)
    {
        // AC recycles dynamic GUIDs after delete. A fresh create on a previously-deleted
        // id means the new object should classify normally — drop any stale delete mark
        // before re-queueing so TryClassify doesn't skip it.
        lock (_gate)
        {
            _deletedWhilePending.Remove(id);
            _pending.Enqueue(id);
            _seenCreateObject.Add(id); // diagnostic: record that the engine fired CreateObject for this uid
        }
    }

    public void OnDeleteObject(uint id)
    {
        lock (_gate)
        {
        int sid = (int)id;
        bool wasInventory = _inventory.Remove(sid);
        bool wasClassified = _byId.Remove(sid);
        _creatures.Remove(sid);
        _landscape.Remove(sid);
        _healthRatios.Remove(sid);
        _estimatedHealth.Remove(sid);
        _serverHealthAt.Remove(sid);
        _classifyRetry.Remove(id);
        _slowRetry.Remove(id);
        _reclassifySkipState.Remove(id);
        // Only mark as "deleted while pending" when the object was never classified —
        // i.e. it might still be sitting in _pending and TryClassify must skip it to
        // avoid touching freed AC memory. For already-classified objects the cache
        // entries above are gone cleanly and no protection is needed; marking them
        // would leak forever (TryClassify is never called for them again).
        if (!wasClassified)
        {
            _deletedWhilePending.Add(id);
            // DIAG: OnDeleteObject fired BEFORE TryClassify ever processed this uid.
            // Sets the delete-while-pending mark which TryClassify will use to skip
            // the object. If a respawn never re-fires OnCreateObject (which would
            // clear this mark), the plugin loses awareness of the uid until the
            // indexer's lazy lookup is forced (typically by a user click).
            if (id >= 0x80000000u)
            {
                bool seenCreate = _seenCreateObject.Contains(id);
                if (_diag.Note(DeleteBeforeClassify, id, seenCreate ? 1 : 0))
                    _host.Log($"[ReclassifyDiag] 0x{id:X8} DELETE-BEFORE-CLASSIFY seenCreate={(seenCreate ? 1 : 0)}");
            }
        }
        if (wasInventory)
            _inventoryDirty = true;
        }
    }

    /// <summary>Returns the last known health ratio (0–1) for <paramref name="id"/>, or -1 if no update has been received.</summary>
    public float GetHealthRatio(int id)
    {
        lock (_gate)
            return _healthRatios.TryGetValue(id, out float v) ? v : -1f;
    }

    /// <summary>
    /// Lowers a creature's health by our own hit when the server sends it no health of its own: the
    /// other targets of a ring, wall or chain spell (ACE pushes health only for the selected target).
    /// <paramref name="fraction"/> is damage / max health, or 0 when the max is unknown, which only
    /// marks the creature as hurt (0.99). Never goes to 0 (dead is the server's call: the kill
    /// notice, a health update or the corpse) and never raises a known value. A real health update
    /// overwrites the estimate.
    /// </summary>
    public void ApplyDamageEstimate(int id, double fraction)
    {
        lock (_gate)
        {
            if (_playerId != 0 && unchecked((uint)id) == _playerId) return;
            float cur = _healthRatios.TryGetValue(id, out float v) ? v : 1f;
            if (cur <= 0f) return;
            float next = fraction > 0
                ? (float)Math.Max(EstimatedHealthFloor, cur - fraction)
                : Math.Min(cur, 0.99f);
            if (next >= cur && _healthRatios.ContainsKey(id)) return;
            _healthRatios[id] = next;
            _estimatedHealth.Add(id);
        }
    }

    /// <summary>True when <paramref name="id"/>'s health ratio is our estimate rather than the server's.</summary>
    public bool IsHealthEstimated(int id)
    {
        lock (_gate)
            return _estimatedHealth.Contains(id);
    }

    /// <summary>When the server's health for <paramref name="id"/> last arrived (OnUpdateHealth, on
    /// <see cref="Clock"/>); false when it never did. An estimate doesn't move it.</summary>
    public bool TryGetServerHealthTime(int id, out DateTime at)
    {
        lock (_gate)
            return _serverHealthAt.TryGetValue(id, out at);
    }

    public void OnUpdateHealth(uint id, float healthRatio)
    {
        lock (_gate)
        {
        if (_playerId != 0 && id == _playerId)
            return; // ignore self

        int sid = (int)id;
        _healthRatios[sid] = Math.Clamp(healthRatio, 0f, 1f);
        _estimatedHealth.Remove(sid);   // the server's number replaces any estimate
        _serverHealthAt[sid] = Clock();

        if (!_creatures.Add(sid))
            return; // creature already known — ratio updated above, nothing more to do

        // DIAG: unconditional log for the FIRST time a dynamic uid lands in _creatures via
        // OnUpdateHealth. seenCreate=0 is the smoking gun for the suspected bug — the
        // engine's CreateObject hook never fired for this respawn, yet the server is
        // sending vitals for it, meaning the mob exists for AC but was invisible to the
        // plugin until something poked it (typically a user click → QueryHealth response).
        bool wasSkipped = _reclassifySkipState.Remove(id); // skip state cleared either way
        if (id >= 0x80000000u)
        {
            bool seenCreate = _seenCreateObject.Contains(id);
            bool inLandscape = _landscape.Contains(sid);
            bool inById = _byId.ContainsKey(sid);
            int flags = (seenCreate ? 1 : 0) | (inLandscape ? 2 : 0) | (inById ? 4 : 0) | (wasSkipped ? 8 : 0);
            if (_diag.Note(HealthAddRescue, id, flags))
                _host.Log($"[ReclassifyDiag] 0x{id:X8} HEALTHADD-RESCUE ratio={healthRatio:0.00} seenCreate={(seenCreate ? 1 : 0)} inLandscape={(inLandscape ? 1 : 0)} inById={(inById ? 1 : 0)} wasSkipped={(wasSkipped ? 1 : 0)}");
        }

        // DIAG: a creature first learned via server health update (the
        // "aggressive mob self-rescues" path) — NOT via TryClassify. Lets us
        // correlate a manually-selected mob's id to how/when it entered
        // _creatures vs. why classification missed it earlier.
        TraceClassify(id, true, true, false, 0u, "healthAdd->creature", healthRatio: healthRatio);

        // If the object was already classified as a Corpse, don't promote it back to Monster.
        // AC fires health=0 events during the creature→corpse transition; if TryClassify already
        // reclassified it and removed it from _creatures, the Add above would re-admit it incorrectly.
        if (_byId.TryGetValue(sid, out var existing))
        {
            if (existing.ObjectClass == AcObjectClass.Corpse)
            {
                _creatures.Remove(sid);
                return;
            }
            if (existing.ObjectClass != AcObjectClass.Monster)
                _byId[sid] = Make(sid, existing.Name, AcObjectClass.Monster);
        }
        else
        {
            // Not yet classified — add with empty name; Tick() will fill in name
            _byId[sid] = Make(sid, "", AcObjectClass.Monster);
        }

        _landscape.Add(sid);
        _inventory.Remove(sid);
        }
    }

    // ── Per-frame processing ──────────────────────────────────────────────

    private int _tickDiagCount;
    private bool _inventoryDirty = true; // true at start so first scan runs after hooks ready
    private bool _initialScanDone;
    private DateTime _lastScanAttempt = DateTime.MinValue;
    private const int ScanCooldownMs = 1000;
    private int _initialScanRetries;
    private const int MaxInitialScanRetries = 15;
    private readonly List<WorldObject> _directInventory = new();
    private readonly HashSet<int> _directInventoryIds = new();
    // The ids the last container walk found inside a pack: a side pack, or the main pack when the
    // item's container field is set. The player's own contents also hold what the player wields.
    private readonly HashSet<int> _packWalkIds = new();

    /// <summary>True when the last container walk (GetDirectInventory) found <paramref name="id"/> inside a pack.</summary>
    public bool InPackWalk(int id)
    {
        lock (_gate) return _packWalkIds.Contains(id);
    }
    private readonly Dictionary<int, int> _directInventoryIndex = new();
    private DateTime _lastDirectInventoryScan = DateTime.MinValue;
    private const int DirectInventoryCooldownMs = 1000;
    /// <summary>The clock GetDirectInventory's reuse runs on (tests freeze it; RynthClock otherwise).</summary>
    internal Func<DateTime> WalkClock { get; set; } = static () => RynthClock.Now;
    private const int DirectInventoryForceRefreshMinMs = 250;

    /// <summary>Mark inventory as changed — triggers a full container scan on next Tick.</summary>
    public void MarkInventoryDirty() => _inventoryDirty = true;

    /// <summary>Call from OnTick to classify queued objects.</summary>
    public void Tick()
    {
        int pending0;
        lock (_gate)
        {
            // Last tick's fast retries get their next attempt now.
            while (_retryNext.Count > 0)
                _pending.Enqueue(_retryNext.Dequeue());
            pending0 = _pending.Count;
        }

        int processed = 0;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long budget = (long)(System.Diagnostics.Stopwatch.Frequency * ClassifyBudgetMs / 1000.0);
        while (processed < MaxClassifyPerTick)
        {
            if (processed >= MinClassifyPerTick
                && System.Diagnostics.Stopwatch.GetTimestamp() - start > budget)
                break;
            uint uid;
            lock (_gate)
            {
                if (_pending.Count == 0) break;
                uid = _pending.Dequeue();
            }
            TryClassify(uid); // re-enters _gate for its own body
            processed++;
        }
        if (processed > 0 && _tickDiagCount < 3)
        {
            _tickDiagCount++;
            int total, landscape, creatures;
            lock (_gate) { total = _byId.Count; landscape = _landscape.Count; creatures = _creatures.Count; }
            _host.Log($"[RynthAi] Cache.Tick classified {processed} from {pending0} pending, total now {total}, landscape={landscape}, creatures={creatures}");
        }

        // Periodically re-check Unknown landscape objects — dynamic creatures whose weenie
        // wasn't ready at classify time will be promoted to _creatures here.
        // Same cadence also re-queues uids parked in _slowRetry (their fast-retry burst
        // exhausted in <10 ms before AC could populate their weenie), giving them a
        // fresh 8-tick attempt each pass.
        if ((RynthClock.Now - _lastReclassifyTime).TotalSeconds >= ReclassifyIntervalSec)
        {
            _lastReclassifyTime = RynthClock.Now;
            FlushSlowRetry();
            ReclassifyUnknownDynamics();
            lock (_gate) _diag.Flush("2 s");
        }

        // Full container scan disabled — causes delayed crash when 160+ items in cache
        // trigger bulk property queries. MissileCraftingManager does its own lightweight scan.
        if (false && _inventoryDirty && _playerId != 0 && _host.HasGetContainerContents
            && (RynthClock.Now - _loginTime).TotalMilliseconds >= 5000
            && (RynthClock.Now - _lastScanAttempt).TotalMilliseconds >= ScanCooldownMs)
        {
            _lastScanAttempt = RynthClock.Now;
            int found = ScanFullInventory();
            // Only clear dirty once scan actually discovers items (hooks ready)
            if (found > 0 || _initialScanDone)
                _inventoryDirty = false;
            if (!_initialScanDone)
            {
                _initialScanRetries++;
                if (found > 0)
                {
                    _initialScanDone = true;
                    _host.Log($"[RynthAi] Inventory scan: discovered {found} item(s), inventory now {_inventory.Count}");
                }
                else if (_initialScanRetries >= MaxInitialScanRetries)
                {
                    _initialScanDone = true;
                    _inventoryDirty = false;
                    _host.Log($"[RynthAi] Inventory scan: gave up after {_initialScanRetries} retries (topCount was 0)");
                }
            }
        }
    }

    // Read-only classify trace (no AC probes added — reuses the caller's
    // already-computed hasName/hasPos/TryGetItemType). Bounded so one login is
    // conclusive without flooding. Dynamic (0x8000+) only — that's the
    // creature/NPC/equipped range the login-mob bug lives in.
    // Full trace lines for the first TraceDetailUids dynamic ids of the session, at most
    // TraceLinesPerUid each (their first attempt and the next distinct step), so a login still
    // shows how individual objects classified. Every attempt is counted by note in the 2 s
    // [ClassifyTrace] summary. Was every attempt of every object up to 5000 lines: a create
    // flood logged each object up to 10 times (8 same-tick retries), 600 lines in seconds.
    private const int TraceDetailUids = 25;
    private const int TraceLinesPerUid = 2;
    private readonly Dictionary<uint, (int Lines, string LastNote)> _traceDetail = new();

    private void TraceClassify(uint uid, bool hasName, bool hasPos, bool gotType, uint flags, string note,
                               int retry = -1, float healthRatio = -1f)
    {
        if (uid < 0x80000000u) return;
        _diag.Note(note, uid);
        if (_traceDetail.TryGetValue(uid, out var seen))
        {
            if (seen.Lines >= TraceLinesPerUid || seen.LastNote == note) return;
        }
        else if (_traceDetail.Count >= TraceDetailUids)
            return;
        _traceDetail[uid] = (seen.Lines + 1, note);

        int atk = -1;
        try { if (_host.HasObjectIsAttackable) atk = _host.ObjectIsAttackable(uid) ? 1 : 0; }
        catch { atk = -2; }
        string extra = retry >= 0 ? $" r={retry}" : healthRatio >= 0f ? $" hr={healthRatio:0.00}" : "";
        _host.Log($"[ClassifyTrace] 0x{uid:X8} name={(hasName ? 1 : 0)} pos={(hasPos ? 1 : 0)} gotType={(gotType ? 1 : 0)} flags=0x{flags:X8} creature={((flags & ItemTypeCreature) != 0 ? 1 : 0)} atk={atk} {note}{extra}");
    }

    private void TryClassify(uint uid)
    {
        lock (_gate)
        {
        // Slow-retry eviction: any uid TryClassify gets to process is by definition no
        // longer "abandoned" for this pass. If we give up again below, the give-up
        // branch re-adds; if we succeed or hit a clean early-return, the set is now
        // correct without further bookkeeping.
        _slowRetry.Remove(uid);

        // Skip objects that were deleted after being enqueued — accessing their AC memory
        // would touch stale pointers and cause an AccessViolationException.
        if (_deletedWhilePending.Contains(uid))
        {
            _deletedWhilePending.Remove(uid);
            // DIAG: TryClassify abandoned this uid because OnDeleteObject marked it
            // delete-while-pending. If the mob is actually still alive (server kept
            // the entity, the delete fired spuriously, or the create-delete-recreate
            // sequence dropped one create), it sits invisible to the plugin until
            // an indexer access rescues it. Pairs with DELETE-BEFORE-CLASSIFY.
            if (uid >= 0x80000000u && _diag.Note(DeleteWhilePendingSkip, uid))
                _host.Log($"[ReclassifyDiag] 0x{uid:X8} DELETE-WHILE-PENDING-SKIP");
            return;
        }

        // Never classify the player's own object — after portal/zone changes
        // OnCreateObject fires for the player and would re-add them to _creatures.
        if (_playerId != 0 && uid == _playerId) return;

        int id = (int)uid;

        // Already known as creature — just fill in name if missing
        if (_creatures.Contains(id))
        {
            if (_byId.TryGetValue(id, out var c) && c.Name.Length == 0
                && _host.TryGetObjectName(uid, out string cn) && cn.Length > 0)
                _byId[id] = Make(id, cn, c.ObjectClass);
            return;
        }

        bool hasName = _host.TryGetObjectName(uid, out string name);
        bool hasPos  = _host.TryGetObjectPosition(uid, out _, out _, out _, out _);

        // A corpse is a corpse whether or not its position is readable yet. This used to
        // need hasPos, and a fresh corpse whose name and type (Container) were in the
        // engine's identity snapshot before its position was fell to the "nopos-item"
        // branch below: filed as an inventory Container, off the landscape, and never
        // looked at again (ReclassifyUnknownDynamics only walks the landscape). Your own
        // fresh kills hit that most (created next to you, classified at once), so looting
        // never saw them while older corpses of other players' kills were found
        // (2026-10-03, Dargoth Hera: "Loot All" didn't loot either). Nothing carried
        // in a pack is named "Corpse of X", and the weenie's corpse flag says so too.
        bool looksLikeCorpse = IsCorpseName(name) || HasCorpseFlag(uid);
        if (looksLikeCorpse)
        {
            FileAsCorpse(id, uid, name);
            return;
        }

        // Object not accessible yet — retry (weenie may not be ready). Names and
        // positions come from the engine's snapshots, so an object created between two
        // snapshots reads as neither. That includes static world objects: a portal
        // (0x7xxxxxxx) used to be dropped here for good, so it was missing from the
        // radar and "/ub usel <portal>" until a click looked it up (2026-09-29).
        if (!hasName && !hasPos)
        {
            TraceClassify(uid, hasName, hasPos, false, 0u, "noNamePos");
            {
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
                if (retries < MaxClassifyRetries)
                {
                    _classifyRetry[uid] = retries + 1;
                    _retryNext.Enqueue(uid); // retry next tick
                }
                else
                {
                    // Fast-retry burst exhausted (8 ticks, one attempt each). Hand off
                    // to slow-retry: reset the fast
                    // counter so the next attempt gets a fresh 8 ticks, and park the
                    // uid in _slowRetry. FlushSlowRetry re-enqueues it every
                    // ReclassifyIntervalSec until the engine populates name/position.
                    _classifyRetry.Remove(uid);
                    _slowRetry.Add(uid);

                    if (_diag.Note(ClassifyGiveup, uid))
                        _host.Log($"[ReclassifyDiag] 0x{uid:X8} CLASSIFY-GIVEUP retries={retries} (name+pos unreadable across {MaxClassifyRetries} ticks; parked in slow-retry)");
                }
            }
            return;
        }

        // AC GUID ranges:
        // 0xC0000000–0xFFFFFFFF = pack/ground items (dynamic items)
        // 0x80000000–0xBFFFFFFF = dynamic objects: creatures, NPCs, AND equipped items
        // 0x50000000–0x5FFFFFFF = players (filtered by SetPlayerId)
        // below = static world objects (portals, lifestones, structures)
        bool isPackItemGuid = uid >= 0xC0000000u;

        AcObjectClass cls;
        if (isPackItemGuid || !hasPos)
        {
            // Could be pack item, equipped item on 0x8000 range, or spawning dynamic object
            // Check item type flags first
            if (_host.TryGetItemType(uid, out uint typeFlags))
            {
                if ((typeFlags & ItemTypeCreature) != 0)
                {
                    // It's a creature with no position yet.
                    // Add to _creatures so future health update or retry classifies it
                    TraceClassify(uid, hasName, hasPos, true, typeFlags, "nopos-creature");
                    _creatures.Add(id);
                    _landscape.Add(id);
                    _byId[id] = Make(id, name, AcObjectClass.Monster);
                    return;
                }

                TraceClassify(uid, hasName, hasPos, true, typeFlags, "nopos-item");
                cls = ClassifyItem(uid, typeFlags);
            }
            else
            {
                // No weenie/item-type accessible yet — retry for a few ticks
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
                TraceClassify(uid, hasName, hasPos, false, 0u, retries < MaxClassifyRetries ? "nopos-retry" : "nopos-giveup", retry: retries);
                if (retries < MaxClassifyRetries)
                {
                    _classifyRetry[uid] = retries + 1;
                    _retryNext.Enqueue(uid); // retry next tick
                    return;
                }
                // Gave up retrying — fall through to name heuristics
                _classifyRetry.Remove(uid);
                cls = ClassifyInventoryItem(name);
            }

            // Portals are world objects: one created before its position is known
            // was filed as inventory here, so nothing searching the landscape (e.g.
            // /ub closestportal) could find it (2026-09-28).
            if (cls == AcObjectClass.Corpse || cls == AcObjectClass.Portal)
            {
                _creatures.Remove(id);
                _inventory.Remove(id);
                _landscape.Add(id);
            }
            else if (uid < 0x80000000u)
            {
                // A static id is a world object (a door, a sign, a lever), never something carried.
                // One created before its position was readable - a dungeon's objects arrive while the
                // character is still in portal space - was filed as inventory here for good, so the
                // door scans (which walk the landscape) never saw the door: "Open Doors" on, a closed
                // door in the way, no door use at all (Dargoth Hera, Infested Mine, 2026-10-19). It is
                // Unknown landscape, as when its position is known; the scans read the position then.
                _creatures.Remove(id);
                _inventory.Remove(id);
                _landscape.Add(id);
                cls = AcObjectClass.Unknown;
            }
            else
            {
                _inventory.Add(id);
                _landscape.Remove(id);
            }
        }
        else
        {
            // Has a position — check if it's actually a creature type (equipped items have 0x8000 GUIDs with positions)
            bool gotType = _host.TryGetItemType(uid, out uint typeFlags);
            TraceClassify(uid, hasName, hasPos, gotType, gotType ? typeFlags : 0u, "pos");
            if (gotType)
            {
                if ((typeFlags & ItemTypeCreature) != 0)
                {
                    _creatures.Add(id);
                    _landscape.Add(id);
                    _byId[id] = Make(id, name, AcObjectClass.Monster);
                    return;
                }

                // Portals were left as Unknown landscape below, so nothing looking
                // for ObjectClass.Portal (/ub closestportal, Lua UsePortal) ever
                // found one standing in front of it (2026-09-28).
                if ((typeFlags & ItemTypePortal) != 0)
                {
                    _classifyRetry.Remove(uid);
                    _inventory.Remove(id);
                    _landscape.Add(id);
                    _byId[id] = Make(id, name, AcObjectClass.Portal);
                    return;
                }

                // Non-creature with a position and a known item type: equipped gear, a ground
                // drop, a chest. It gets the class its type gives and is filed by who has it
                // (FileTypedObject). Only weapons, armor, casters and containers were taken
                // here before, all into the inventory: keys, gems, comps, food and the like
                // stayed Unknown landscape and were re-probed every 2 s for good, and other
                // players' wielded gear and ground-dropped weapons counted as ours
                // (2026-10-05). A static object that isn't a chest or a lifestone returns
                // false and stays Unknown scenery below, hazard check included.
                AcObjectClass typedCls = ClassifyItem(uid, typeFlags);
                if (typedCls != AcObjectClass.Unknown && FileTypedObject(uid, id, name, typedCls, typeFlags))
                    return;
            }
            else if (uid >= 0x80000000u)
            {
                // TryGetItemType failed for a dynamic object with position — weenie may not be ready.
                // Re-queue for retry so creatures aren't permanently misclassified as Unknown landscape.
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
                if (retries < MaxClassifyRetries)
                {
                    _classifyRetry[uid] = retries + 1;
                    _retryNext.Enqueue(uid); // retry next tick
                    return;
                }
                _classifyRetry.Remove(uid);
            }

            // Immediate creature rescue. The qualities/appraisal ItemType that
            // TryGetItemType resolves lags ~30s post-login (reports flags=0),
            // so a login mob would sit here as Unknown and never be scanned —
            // the "bot ignores mobs after login" bug. AC's native combat check
            // (ClientCombatSystem::ObjectIsAttackable) is available the instant
            // the weenie object exists (it's how the game shows the health bar
            // immediately) and does NOT depend on qualities/appraisal. A
            // positioned dynamic (0x8000) object the game itself says is
            // attackable IS a creature; NPCs / equipped gear are not attackable
            // so they are not mis-promoted. ItemType refines it later if needed.
            if (uid >= 0x80000000u
                && _host.HasObjectIsAttackable
                && _host.ObjectIsAttackable(uid))
            {
                _classifyRetry.Remove(uid);
                _inventory.Remove(id);
                _creatures.Add(id);
                _landscape.Add(id);
                _byId[id] = Make(id, name, AcObjectClass.Monster);
                TraceClassify(uid, hasName, hasPos, gotType, gotType ? typeFlags : 0u, "attackable->creature");
                return;
            }

            cls = AcObjectClass.Unknown; // landscape non-creature (static object, portal, etc.)
            _landscape.Add(id);
            MaybeRegisterHazard(uid, name); // lava/acid hotspots arrive here as Unknown landscape
        }

        _classifyRetry.Remove(uid);
        _byId[id] = Make(id, name, cls);
        }
    }

    private const uint ItemTypeGear = ItemTypeMeleeWeapon | ItemTypeMissileWeapon | ItemTypeCaster | ItemTypeArmor | ItemTypeContainer;

    private enum Holder { Unknown, Player, OtherWielder, OtherContainer, Nobody }

    /// <summary>
    /// The player has <paramref name="id"/> (wielded, main pack or a side pack): true; someone or
    /// something else has it, or it lies on the ground: false; null when the engine can't say yet.
    /// A sold, dropped or traded item stays known to the client for a while (2026-10-14: a sold
    /// Flaming Greataxe was still tried three times as the listed weapon). Any thread.
    /// </summary>
    public bool? PlayerHas(int id)
    {
        lock (_gate)
        {
            Holder h = WhoHas(id);
            return h == Holder.Unknown ? null : h == Holder.Player;
        }
    }

    /// <summary>
    /// Who has the object: the player (wielded, in the main pack, or in a side pack), another
    /// wielder (other players' and NPCs' gear), another container (a corpse, a chest), or
    /// nobody (it lies on the ground). Unknown when the player isn't known yet or the engine
    /// can't read the object's ownership. Caller holds _gate.
    /// </summary>
    private Holder WhoHas(int id)
    {
        if (_playerId == 0 || !TryGetOwnership(id, out int containerId, out int wielderId, out _))
            return Holder.Unknown;
        uint container = unchecked((uint)containerId), wielder = unchecked((uint)wielderId);
        if (wielder == _playerId || container == _playerId) return Holder.Player;
        if (wielder != 0) return Holder.OtherWielder;
        if (container == 0) return Holder.Nobody;
        // Side packs sit in the main pack (AC has no deeper nesting).
        return TryGetOwnership(containerId, out int outer, out _, out _) && unchecked((uint)outer) == _playerId
            ? Holder.Player : Holder.OtherContainer;
    }

    /// <summary>
    /// Files a positioned non-creature whose item type gave it a real class. Caller holds _gate.
    /// <list type="bullet">
    /// <item>A lifestone: landscape.</item>
    /// <item>Another static object: a chest goes on the landscape; anything else returns false
    /// and the caller keeps it Unknown scenery (doors, signs, static hotspots).</item>
    /// <item>A dynamic object the player has: inventory. One another player or an NPC wields:
    /// neither list (known by id only), so it is not ours, not a ground item on the radar and
    /// not something a meta can pick up. In another container or loose on the ground: landscape.</item>
    /// <item>Ownership unreadable: gear goes to the inventory as before, the rest to the landscape.</item>
    /// </list>
    /// A non-gear object that lands on the landscape gets the hazard check it had as Unknown
    /// (a hotspot spawned with a dynamic id and a Misc type is still a hazard).
    /// </summary>
    private bool FileTypedObject(uint uid, int id, string name, AcObjectClass cls, uint typeFlags)
    {
        bool gear = (typeFlags & ItemTypeGear) != 0;
        bool toInventory, toLandscape;
        if (cls == AcObjectClass.Lifestone)
        {
            toInventory = false;
            toLandscape = true;
        }
        else if (uid < 0x80000000u)
        {
            if ((typeFlags & ItemTypeContainer) == 0) return false;
            toInventory = false;
            toLandscape = true;
        }
        else
        {
            Holder holder = WhoHas(id);
            toInventory = holder == Holder.Player || (holder == Holder.Unknown && gear);
            toLandscape = !toInventory && holder != Holder.OtherWielder;
        }

        _classifyRetry.Remove(uid);
        _reclassifySkipState.Remove(uid);
        if (toInventory) { if (_inventory.Add(id)) _inventoryDirty = true; }
        else if (_inventory.Remove(id)) _inventoryDirty = true;
        if (toLandscape) _landscape.Add(id);
        else _landscape.Remove(id);
        _byId[id] = Make(id, name, cls);
        if (toLandscape && !gear)
            MaybeRegisterHazard(uid, name);
        return true;
    }

    // PublicWeenieDesc._bitfield BF_CORPSE (ObjectDescriptionFlag.Corpse).
    private const uint BfCorpse = 0x2000;

    /// <summary>The weenie's corpse flag (dynamic objects only; false when the engine can't say).</summary>
    private bool HasCorpseFlag(uint uid)
        => uid >= 0x80000000u
           && _host.HasGetObjectBitfield
           && _host.TryGetObjectBitfield(uid, out uint bf)
           && (bf & BfCorpse) != 0;

    /// <summary>Files <paramref name="id"/> as a landscape Corpse (caller holds _gate).</summary>
    private void FileAsCorpse(int id, uint uid, string? name)
    {
        _classifyRetry.Remove(uid);
        _creatures.Remove(id);
        if (_inventory.Remove(id)) _inventoryDirty = true;
        _landscape.Add(id);
        _byId[id] = Make(id, name ?? string.Empty, AcObjectClass.Corpse);
    }

    // Bounded log for corpses found filed as something else and moved to Corpse.
    private int _corpseRescueLogLines;
    private const int MaxCorpseRescueLogLines = 20;

    private void LogCorpseRescue(uint uid, string name, AcObjectClass was, string where)
    {
        if (_corpseRescueLogLines >= MaxCorpseRescueLogLines) return;
        _corpseRescueLogLines++;
        _host.Log($"[RynthAi] Corpse 0x{uid:X8} '{name}' was filed as {was} ({where}); now a Corpse"
                  + (_corpseRescueLogLines == MaxCorpseRescueLogLines ? " (further rescues counted only)" : ""));
    }

    /// <summary>
    /// Re-enqueue every uid that's currently parked in <see cref="_slowRetry"/>. Pairs
    /// with the give-up branch of <see cref="TryClassify"/>: when an OnCreateObject-then-
    /// fast-retry burst exhausts in &lt;10 ms before AC populates the weenie, the uid
    /// lands here. The 2 s flush hands it back to <see cref="_pending"/> so TryClassify
    /// gets another 8-tick window. Successful classification evicts the uid at the top
    /// of TryClassify; persistent failure re-adds it for the next cycle.
    /// </summary>
    private void FlushSlowRetry()
    {
        lock (_gate)
        {
            if (_slowRetry.Count == 0) return;
            foreach (uint uid in _slowRetry)
                _pending.Enqueue(uid);
        }
    }

    /// <summary>
    /// Scan landscape objects classified as Unknown with dynamic GUIDs and try to promote
    /// any that are now recognised as TYPE_CREATURE. Runs every ~2 seconds from Tick().
    /// This rescues creatures whose weenie data wasn't available when they were first classified.
    /// </summary>
    // PublicWeenieDesc._bitfield flags (as ExpressionEngine's BF_PLAYER / BF_ATTACKABLE).
    private const uint BfPlayer = 0x8, BfAttackable = 0x10;

    private void ReclassifyUnknownDynamics()
    {
        lock (_gate)
        {
        List<int>? toPromote = null;
        List<int>? toCorpse  = null;
        List<(int id, string name, AcObjectClass cls)>? statics = null;
        List<(int Id, uint Flags)>? typed = null;   // Unknowns whose item type now gives a class
        foreach (int id in _landscape)
        {
            uint uid = unchecked((uint)id);
            if (uid < 0x80000000u)
            {
                // Static objects never become creatures/corpses, but one classified before
                // its name or type was readable is fixed up here: the name filled in, and
                // a portal that read as Unknown becomes a Portal (radar, /ub closestportal).
                if (!_byId.TryGetValue(id, out var so) || so.ObjectClass != AcObjectClass.Unknown) continue;
                string sName = so.Name;
                if (sName.Length == 0 && _host.TryGetObjectName(uid, out string n2) && n2.Length > 0) sName = n2;
                AcObjectClass sCls = _host.TryGetItemType(uid, out uint sType) && (sType & ItemTypePortal) != 0
                    ? AcObjectClass.Portal : AcObjectClass.Unknown;
                if (sName != so.Name || sCls != so.ObjectClass)
                    (statics ??= new()).Add((id, sName, sCls));
                continue;
            }
            if (!_byId.TryGetValue(id, out var wo)) continue;
            if (wo.ObjectClass == AcObjectClass.Corpse) continue; // already correct

            // Corpse rescue: a dead creature whose corpse name/position wasn't
            // readable at OnCreateObject gets bucketed as Monster (or Unknown).
            // The loot finder (TryFindNearestCorpse) matches ObjectClass==Corpse,
            // and this is the ONLY path that promotes a non-Unknown object to
            // Corpse — without it those corpses are permanently invisible to
            // looting (corpse exists with a valid name, but classed as Monster).
            // Bounded to Monster/Unknown so we don't name-query every ground
            // item each pass; IsCorpseName is specific ("X corpse"/"Corpse of X")
            // so a live mob cannot be misread as a corpse.
            // Container too: a corpse typed before its name or flag was readable is filed as a
            // landscape Container by FileTypedObject (it used to go to the inventory, which the
            // inventory pass below re-checks).
            if ((wo.ObjectClass == AcObjectClass.Monster || wo.ObjectClass == AcObjectClass.Unknown
                 || wo.ObjectClass == AcObjectClass.Container)
                && ((_host.TryGetObjectName(uid, out string maybeCorpse) && IsCorpseName(maybeCorpse))
                    || HasCorpseFlag(uid)))
            {
                toCorpse ??= new List<int>();
                toCorpse.Add(id);
                continue;
            }

            if (_creatures.Contains(id)) continue;
            if (wo.ObjectClass != AcObjectClass.Unknown) continue;

            // Promote if EITHER the (laggy) qualities ItemType now reports
            // creature, OR AC's native combat check says it's attackable (the
            // immediate signal that doesn't wait on qualities/appraisal). This
            // second path is what rescues a login mob within 2s if it slipped
            // to Unknown before its weenie/combat-state was readable.
            bool gotType = _host.TryGetItemType(uid, out uint typeFlags);
            bool isCreature = gotType && (typeFlags & ItemTypeCreature) != 0;
            if (!isCreature
                && _host.HasObjectIsAttackable
                && _host.ObjectIsAttackable(uid))
                isCreature = true;
            if (!isCreature)
            {
                // Its type arrived after it was classified: file it as the item it is instead
                // of probing it again every pass. Done after the loop (filing edits _landscape).
                if (gotType && typeFlags != 0 && ClassifyByItemType(typeFlags) != AcObjectClass.Unknown)
                {
                    (typed ??= new()).Add((id, typeFlags));
                    continue;
                }
                DiagLogReclassifySkip(uid, wo.Name);
                continue;
            }

            toPromote ??= new List<int>();
            toPromote.Add(id);
        }

        // Corpses filed as inventory. Before 2026-10-03 a corpse classified while its
        // position wasn't readable went to the inventory as a Container (its ItemType) and
        // stayed there: invisible to looting for good. TryClassify no longer does that, but
        // an object filed before its name or flag was readable still can, so the bags in the
        // inventory list (a handful) are re-checked here. Packs we carry aren't corpses.
        List<(int id, AcObjectClass was)>? invCorpses = null;
        List<(int Id, uint Flags)>? invLoose = null;   // filed as carried, but lying in the world
        foreach (int id in _inventory)
        {
            uint uid = unchecked((uint)id);
            if (uid < 0x80000000u) continue;
            if (!_byId.TryGetValue(id, out var wo)) continue;
            if (wo.ObjectClass != AcObjectClass.Container && wo.ObjectClass != AcObjectClass.Unknown) continue;
            if (IsCorpseName(wo.Name)
                || (_host.TryGetObjectName(uid, out string invName) && IsCorpseName(invName))
                || HasCorpseFlag(uid))
            {
                (invCorpses ??= new()).Add((id, wo.ObjectClass));
                continue;
            }

            // A chest that appeared before its position was known (the Dreamweave dig's Unearthed Cache)
            // is filed here as something carried; the server says nobody holds it and it now has a
            // position: it lies in the world. Without this it was never on the landscape, so no
            // "use <name>" search, Loot by name or chest scan could find it (2026-11-02).
            if (WhoHas(id) == Holder.Nobody
                && _host.TryGetObjectPosition(uid, out _, out _, out _, out _)
                && _host.TryGetItemType(uid, out uint looseFlags))
                (invLoose ??= new()).Add((id, looseFlags));
        }
        if (invCorpses != null)
        {
            foreach (var (id, was) in invCorpses)
            {
                uint uid = unchecked((uint)id);
                string n = _host.TryGetObjectName(uid, out string nn) && nn.Length > 0 ? nn : _byId[id].Name;
                FileAsCorpse(id, uid, n);
                LogCorpseRescue(uid, n, was, "inventory, no position when classified");
            }
        }

        if (invLoose != null)
        {
            int moved = 0;
            foreach (var (id, flags) in invLoose)
            {
                if (!_byId.TryGetValue(id, out var lw)) continue;
                uint uid = unchecked((uint)id);
                AcObjectClass lc = ClassifyItem(uid, flags);
                if (lc == AcObjectClass.Unknown || !FileTypedObject(uid, id, lw.Name ?? string.Empty, lc, flags)) continue;
                moved++;
            }
            if (moved > 0)
                _host.Log($"[RynthAi] ReclassifyUnknownDynamics: {moved} object(s) filed as carried lie loose in the world -> landscape");
        }

        if (statics != null)
            foreach (var (id, name, cls) in statics)
                _byId[id] = Make(id, name, cls);

        if (toCorpse != null)
        {
            foreach (int id in toCorpse)
            {
                uint uid = unchecked((uint)id);
                _host.TryGetObjectName(uid, out string name);
                _creatures.Remove(id);
                _inventory.Remove(id);
                _byId[id] = Make(id, name ?? string.Empty, AcObjectClass.Corpse);
            }
            _host.Log($"[RynthAi] ReclassifyUnknownDynamics: rescued {toCorpse.Count} stale corpse(s) → Corpse");
        }

        if (typed != null)
        {
            int filed = 0, intoInventory = 0;
            foreach (var (id, flags) in typed)
            {
                if (!_byId.TryGetValue(id, out var wo)) continue;
                uint uid = unchecked((uint)id);
                AcObjectClass cls = ClassifyItem(uid, flags);
                if (cls == AcObjectClass.Unknown || !FileTypedObject(uid, id, wo.Name ?? string.Empty, cls, flags)) continue;
                filed++;
                if (_inventory.Contains(id)) intoInventory++;
            }
            if (filed > 0)
                _host.Log($"[RynthAi] ReclassifyUnknownDynamics: typed {filed} Unknown object(s) as items ({intoInventory} into the inventory)");
        }

        // DIAG: heartbeat — Unknown landscape candidates checked but nothing promoted
        // this pass. Confirms ReclassifyUnknownDynamics is running and engine signals
        // keep failing for the stuck uids (vs. them never reaching _landscape at all).
        if (toPromote == null
            && typed == null
            && _reclassifySkipState.Count > 0
            && _reclassifyDiagSummaryCount < MaxReclassifyDiagSummaries)
        {
            _reclassifyDiagSummaryCount++;
            _host.Log($"[ReclassifyDiag] pass: {_reclassifySkipState.Count} stuck Unknown landscape candidate(s), 0 promoted");
        }

        if (toPromote == null) return;

        uint selfId = _host.GetPlayerId();
        foreach (int id in toPromote)
        {
            uint uid = unchecked((uint)id);
            _host.TryGetObjectName(uid, out string name);
            // Players are creatures too: under the Decal bridge more objects arrive before their
            // type is readable and come through here, and the character and other players were
            // promoted to Monster (Lucy listed herself in the Monsters tab, 2026-10-01). The
            // weenie bitfield says player (0x8) or not attackable (0x10 clear = NPC).
            AcObjectClass cls = AcObjectClass.Monster;
            if (uid == selfId)
                cls = AcObjectClass.Player;
            else if (_host.HasGetObjectBitfield && _host.TryGetObjectBitfield(uid, out uint bf))
            {
                if ((bf & BfPlayer) != 0) cls = AcObjectClass.Player;
                else if ((bf & BfAttackable) == 0) cls = AcObjectClass.Npc;
            }
            if (cls == AcObjectClass.Monster) _creatures.Add(id);
            _byId[id] = Make(id, name ?? string.Empty, cls);
            _reclassifySkipState.Remove(uid); // diagnostic state cleared on success
        }

        _host.Log($"[RynthAi] ReclassifyUnknownDynamics: promoted {toPromote.Count} object(s) to Creature");
        }
    }

    // Diagnostic helper for ReclassifyUnknownDynamics. Logs the live engine signals
    // (name, position, item-type flags, attackable bit) for each Unknown landscape
    // candidate the rescue pass keeps rejecting. Bounded + state-change suppressed so
    // a stuck mob produces one log line, and only re-logs if its engine state changes.
    private void DiagLogReclassifySkip(uint uid, string name)
    {
        if (_reclassifyDiagCount >= MaxReclassifyDiagLines) return;

        bool gotType = _host.TryGetItemType(uid, out uint flags);
        bool hasAtk = _host.HasObjectIsAttackable;
        int atk = -1;
        if (hasAtk)
        {
            try { atk = _host.ObjectIsAttackable(uid) ? 1 : 0; }
            catch { atk = -2; }
        }
        bool hasPos = _host.TryGetObjectPosition(uid, out _, out _, out _, out _);
        string state = $"name='{name}' hasPos={(hasPos ? 1 : 0)} gotType={(gotType ? 1 : 0)} flags=0x{flags:X8} hasAtk={(hasAtk ? 1 : 0)} atk={atk}";
        if (_reclassifySkipState.TryGetValue(uid, out string? prev) && prev == state)
            return; // state unchanged — suppress duplicate
        _reclassifySkipState[uid] = state;
        _reclassifyDiagCount++;
        _host.Log($"[ReclassifyDiag] 0x{uid:X8} skip: {state}");
    }

    // ── WorldFilter API ───────────────────────────────────────────────────

    public WorldObject? this[int id]
    {
        get
        {
            lock (_gate)
            {
            if (_byId.TryGetValue(id, out var wo))
            {
                // Patch empty name on access
                if (wo.Name.Length == 0 && _host.TryGetObjectName(unchecked((uint)id), out string n) && n.Length > 0)
                {
                    // A corpse stored before its name was readable becomes a Corpse here.
                    if (wo.ObjectClass != AcObjectClass.Corpse && IsCorpseName(n))
                    {
                        AcObjectClass was = wo.ObjectClass;
                        FileAsCorpse(id, unchecked((uint)id), n);
                        LogCorpseRescue(unchecked((uint)id), n, was, "named late");
                        return _byId[id];
                    }
                    wo = Make(id, n, wo.ObjectClass);
                    _byId[id] = wo;
                }
                return wo;
            }

            // Lazy lookup for objects not yet in queue (e.g. user-configured weapon IDs)
            uint uid = unchecked((uint)id);
            bool hasName = _host.TryGetObjectName(uid, out string name);
            bool hasPos  = _host.TryGetObjectPosition(uid, out _, out _, out _, out _);
            if (!hasName && !hasPos)
                return null;

            bool isPackItemGuid = uid >= 0xC0000000u;
            bool looksLikeCorpse = IsCorpseName(name) || HasCorpseFlag(uid); // position not needed (see TryClassify)
            AcObjectClass cls;
            if (looksLikeCorpse)
            {
                cls = AcObjectClass.Corpse;
                _creatures.Remove(id);
            }
            else if (_creatures.Contains(id))
                cls = AcObjectClass.Monster;
            else if (_host.TryGetItemType(uid, out uint typeFlags))
            {
                if ((typeFlags & ItemTypeCreature) != 0)
                {
                    cls = AcObjectClass.Monster;
                    _creatures.Add(id);
                    // DIAG: indexer's lazy-lookup side-effect just added a fresh uid to
                    // _creatures. This is the "click → OnSelectedTargetChange → indexer
                    // queries qualities → TYPE_CREATURE finally readable → bot sees the
                    // mob" path. seenCreate=0 here is the strongest signal that the
                    // engine's CreateObject hook missed this respawn entirely.
                    if (uid >= 0x80000000u)
                    {
                        bool seenCreate = _seenCreateObject.Contains(uid);
                        if (_diag.Note(IndexerRescue, uid, seenCreate ? 1 : 0))
                            _host.Log($"[ReclassifyDiag] 0x{uid:X8} INDEXER-RESCUE name='{name}' flags=0x{typeFlags:X8} seenCreate={(seenCreate ? 1 : 0)}");
                    }
                }
                else
                    cls = ClassifyItem(uid, typeFlags);
            }
            else if (isPackItemGuid || !hasPos)
                cls = ClassifyInventoryItem(name);
            else
                cls = AcObjectClass.Unknown;

            var obj = Make(id, name, cls);
            _byId[id] = obj;
            if (cls == AcObjectClass.Corpse || (!isPackItemGuid && hasPos))
            {
                _inventory.Remove(id);
                _landscape.Add(id);
                if (cls == AcObjectClass.Unknown)
                    MaybeRegisterHazard(uid, name);
            }
            else
            {
                _inventory.Add(id);
            }
            return obj;
            }
        }
    }

    /// <summary>
    /// Read an STypeInt property from an object via CBaseQualities::InqInt.
    /// Returns defaultValue if the API is unavailable or the property is not set.
    /// </summary>
    /// <summary>Decal's synthetic LongValueKey "Type" (0x0D000000): the object's weenie class id
    /// (WCID). VTank profiles match WCIDs with it ("loot everything that isn't retail").</summary>
    public const uint DecalTypeKey = 0x0D000000;

    public int GetIntProperty(int id, uint stype, int defaultValue)
    {
        uint uid = unchecked((uint)id);
        if (stype == DecalTypeKey)
            return _host.HasGetObjectWcid && _host.TryGetObjectWcid(uid, out uint wcid) ? unchecked((int)wcid) : defaultValue;
        if ((stype & 0xFFFFFF00u) == DecalTypeKey)
            return GetDecalLongKey(uid, stype, defaultValue);
        if (!_host.HasGetObjectIntProperty) return defaultValue;
        return _host.TryGetObjectIntProperty(uid, stype, out int v) ? v : defaultValue;
    }

    /// <summary>
    /// Decal's other synthetic LongValueKeys (0x0D000001-0x0D00002A, Decal.Adapter's numbering),
    /// read as the game property each one is. VTank profiles use them: LootSnobV4 has 336 rules on
    /// MaxDamage and 109 on Coverage. They were read as STypeInt ids, which no object has, so
    /// they were always 0 and those rules never matched (until 2026-10-07). Icons are the
    /// portal.dat index without its 0x06000000 base, as Decal gives them. Keys with no property
    /// here (landblock, monarch, physics flags, spell counts) answer the default.
    /// </summary>
    private int GetDecalLongKey(uint uid, uint key, int defaultValue)
    {
        switch (key)
        {
            case 0x0D000001: return DecalIcon(uid, 8, defaultValue);    // Icon
            case 0x0D000008: { uint s = GetDataIdProperty(unchecked((int)uid), 28); return s != 0 ? unchecked((int)s) : defaultValue; } // AssociatedSpell
            case 0x0D000029: return DecalIcon(uid, 50, defaultValue);   // IconOverlay
            case 0x0D00002A: return DecalIcon(uid, 52, defaultValue);   // IconUnderlay
        }
        uint stype = DecalIntStype(key);
        if (stype == 0 || !_host.HasGetObjectIntProperty) return defaultValue;
        return _host.TryGetObjectIntProperty(uid, stype, out int v) ? v : defaultValue;
    }

    /// <summary>The STypeInt a Decal synthetic long key is, or 0 (not an int property).</summary>
    public static uint DecalIntStype(uint key) => key switch
    {
        0x0D000004 => 6,     // ItemSlots      -> ItemsCapacity
        0x0D000005 => 7,     // PackSlots      -> ContainersCapacity
        0x0D000006 => 12,    // StackCount     -> StackSize
        0x0D000007 => 11,    // StackMax       -> MaxStackSize
        0x0D00000B => 10,    // WieldingSlot   -> CurrentWieldedLocation
        0x0D00000D => 4,     // Coverage       -> ClothingPriority
        0x0D00000E => 9,     // EquipableSlots -> ValidLocations
        0x0D00000F => 51,    // EquipType      -> CombatUse
        0x0D000010 => 18,    // IconOutline    -> UiEffects
        0x0D000011 => 50,    // MissileType    -> AmmoType
        0x0D000012 => 16,    // UsageMask      -> ItemUseable
        0x0D000014 => 152,   // HookMask       -> HookItemType
        0x0D000015 => 151,   // HookType
        0x0D00001A => 1,     // Category       -> ItemType
        0x0D00001F => 49,    // WeapSpeed      -> WeaponTime
        0x0D000020 => 48,    // EquipSkill     -> WeaponSkill
        0x0D000021 => 45,    // DamageType
        0x0D000022 => 44,    // MaxDamage      -> Damage (the appraisal's weapon profile)
        _ => 0,
    };

    private int DecalIcon(uint uid, uint did, int defaultValue)
    {
        uint v = GetDataIdProperty(unchecked((int)uid), did);
        return v != 0 ? unchecked((int)(v & 0x00FFFFFFu)) : defaultValue;
    }

    /// <summary>
    /// Read an STypeDID property (e.g. a scroll's Spell 28 from its CreateObject header).
    /// 0 if the API is unavailable or the property is not set.
    /// </summary>
    public uint GetDataIdProperty(int id, uint stype)
    {
        try { return _host.TryGetObjectDataIdProperty(unchecked((uint)id), stype, out uint v) ? v : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Read an STypeFloat property from an object via CBaseQualities::InqFloat.
    /// Returns defaultValue if the API is unavailable or the property is not set.
    /// </summary>
    public double GetDoubleProperty(int id, uint stype, double defaultValue)
    {
        if (!_host.HasGetObjectDoubleProperty) return defaultValue;
        uint uid = unchecked((uint)id);
        return _host.TryGetObjectDoubleProperty(uid, stype, out double v) ? v : defaultValue;
    }

    /// <summary>
    /// Read an STypeString property from an object via CBaseQualities::InqString.
    /// Returns defaultValue if the API is unavailable or the property is not set.
    /// </summary>
    public string GetStringProperty(int id, uint stype, string defaultValue)
    {
        if (!_host.HasGetObjectStringProperty) return defaultValue;
        uint uid = unchecked((uint)id);
        return _host.TryGetObjectStringProperty(uid, stype, out string? v) && !string.IsNullOrEmpty(v)
            ? v
            : defaultValue;
    }

    /// <summary>
    /// True when ownership is readable and the object has neither a container nor a wielder,
    /// so it lies loose in the world. Unreadable ownership is false (a pack item is never a ground drop).
    /// </summary>
    public bool IsOnGround(int id)
        => TryGetOwnership(id, out int containerId, out int wielderId, out _) && containerId == 0 && wielderId == 0;

    public int GetContainerId(int id)
    {
        if (!TryGetOwnership(id, out int containerId, out _, out _))
            return 0;

        return containerId;
    }

    public int GetWielderId(int id)
    {
        if (!TryGetOwnership(id, out _, out int wielderId, out _))
            return 0;

        return wielderId;
    }

    public int GetWieldedLocation(int id)
    {
        if (!TryGetOwnership(id, out _, out _, out int location))
            return 0;

        return location;
    }

    /// <summary>
    /// Compute 3D distance between two objects.
    /// Converts AC landblock-local coordinates to global space so cross-block distances work.
    /// </summary>
    public double Distance(int id1, int id2)
    {
        uint uid1 = unchecked((uint)id1);
        uint uid2 = unchecked((uint)id2);

        if (!_host.TryGetObjectPosition(uid1, out uint cell1, out float x1, out float y1, out float z1))
            return double.MaxValue;
        if (!_host.TryGetObjectPosition(uid2, out uint cell2, out float x2, out float y2, out float z2))
            return double.MaxValue;

        // Convert landblock-local to global: each landblock block is 192 meters
        float gx1 = ((cell1 >> 24) & 0xFF) * 192f + x1;
        float gy1 = ((cell1 >> 16) & 0xFF) * 192f + y1;
        float gx2 = ((cell2 >> 24) & 0xFF) * 192f + x2;
        float gy2 = ((cell2 >> 16) & 0xFF) * 192f + y2;

        float dx = gx1 - gx2;
        float dy = gy1 - gy2;
        float dz = z1 - z2;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        // A garbage position read (NaN/∞) must read as out of range. NaN fails every
        // "dist > limit" test, so it passed both the scan's range gate and Think's
        // disengage drop, and a target could be kept at any real distance.
        return double.IsFinite(d) ? d : double.MaxValue;
    }

    /// <summary>
    /// <see cref="Distance"/> from a position the caller already read (cell + landblock-local
    /// x/y/z, e.g. the player's, once per scan) to object <paramref name="id2"/>, with the same
    /// math and the same MaxValue for an unreadable or non-finite position.
    /// <paramref name="hasPos"/>/<paramref name="x2"/>/<paramref name="y2"/> return the object's
    /// position as read (landblock-local) so the caller needn't read it again.
    /// </summary>
    public double DistanceFrom(uint cell1, float x1, float y1, float z1, int id2, out bool hasPos, out float x2, out float y2)
    {
        hasPos = _host.TryGetObjectPosition(unchecked((uint)id2), out uint cell2, out x2, out y2, out float z2);
        if (!hasPos)
            return double.MaxValue;

        float gx1 = ((cell1 >> 24) & 0xFF) * 192f + x1;
        float gy1 = ((cell1 >> 16) & 0xFF) * 192f + y1;
        float gx2 = ((cell2 >> 24) & 0xFF) * 192f + x2;
        float gy2 = ((cell2 >> 16) & 0xFF) * 192f + y2;

        float dx = gx1 - gx2;
        float dy = gy1 - gy2;
        float dz = z1 - z2;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return double.IsFinite(d) ? d : double.MaxValue;
    }

    /// <summary>Enumerate objects in player's inventory (no physics position).</summary>
    public IEnumerable<WorldObject> GetInventory()
    {
        // Copy-under-lock then iterate the copy outside the lock (the Avalonia radar/
        // dashboard poll thread enumerates this while the pump thread mutates _inventory).
        lock (_gate)
        {
            var snapshot = new List<WorldObject>(_inventory.Count);
            foreach (int id in _inventory)
                if (_byId.TryGetValue(id, out var wo))
                    snapshot.Add(wo);
            return snapshot;
        }
    }

    /// <summary>
    /// Every object the cache currently knows about, indexed by id. Useful for
    /// wielded-item lookups when the `_inventory` set hasn't picked them up
    /// yet (the cache classifies items asynchronously and the wielderInfo probe
    /// can race with consumers like HasWieldedAmmo).
    /// </summary>
    public IEnumerable<WorldObject> AllKnownObjects()
    {
        lock (_gate)
            return new List<WorldObject>(_byId.Values);
    }

    /// <summary>
    /// Lightweight live inventory snapshot built from GetContainerContents.
    /// This bypasses the crash-prone full cache scan and is the source of truth
    /// for crafting and ammo diagnostics.
    /// </summary>
    public IReadOnlyList<WorldObject> GetDirectInventory(bool forceRefresh = false)
    {
        // Whole-body lock: the rescan clears+rebuilds _directInventory*, and callers may
        // arrive from the pump thread AND Avalonia button handlers. Each exit returns a
        // snapshot copy so the caller never iterates the live list a later call will clear.
        lock (_gate)
        {
        DateTime now = WalkClock();

        if (!_host.HasGetContainerContents)
        {
            if (forceRefresh || _directInventory.Count == 0)
            {
                _directInventory.Clear();
                foreach (var item in GetInventory())
                    _directInventory.Add(item);
            }
            return _directInventory.ToList();
        }

        double scanAgeMs = (now - _lastDirectInventoryScan).TotalMilliseconds;
        if (_directInventory.Count > 0)
        {
            if (!forceRefresh && scanAgeMs < DirectInventoryCooldownMs)
                return _directInventory.ToList();

            if (forceRefresh && scanAgeMs < DirectInventoryForceRefreshMinMs)
                return _directInventory.ToList();
        }

        _lastDirectInventoryScan = now;
        _directInventory.Clear();
        _directInventoryIds.Clear();
        _directInventoryIndex.Clear();
        _packWalkIds.Clear();

        uint playerId = _host.GetPlayerId();
        if (playerId == 0)
            return _directInventory.ToList();

        _host.TryGetObjectPosition(playerId, out _, out _, out _, out _);

        var pendingContainers = new Queue<uint>();
        var seenContainers = new HashSet<uint> { playerId };
        pendingContainers.Enqueue(playerId);

        while (pendingContainers.Count > 0)
        {
            uint containerId = pendingContainers.Dequeue();
            uint[] buf = new uint[512];

            int count;
            try
            {
                count = _host.GetContainerContents(containerId, buf);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < count; i++)
            {
                uint itemId = buf[i];
                // Pass the container being enumerated + the item's index as the authoritative
                // parent/slot for the remote inventory view (stashed onto the WorldObject).
                if (!TryAddDirectInventoryItem(itemId, playerId, containerId, i, out bool isContainer))
                    continue;
                // The player's own contents can list what the player wields (the 07:27 export had the
                // wielded Sceptre in it, 2026-10-18), and reading a wielded caster as "in the pack" made
                // buffing ignore it and try every other one. So only a side pack, or the main pack with
                // the item's own container field set and no wielder, means "in a pack". Buffing's wand
                // swap puts the weapons into a side pack (the stale-record case of 2026-10-17/18).
                if (containerId != playerId) _packWalkIds.Add(unchecked((int)itemId));
                else if (_host.HasGetObjectOwnershipInfo
                         && _host.TryGetObjectOwnershipInfo(itemId, out uint inContainer, out uint byWielder, out _)
                         && inContainer != 0 && byWielder == 0)
                    _packWalkIds.Add(unchecked((int)itemId));

                if (isContainer && seenContainers.Add(itemId))
                    pendingContainers.Enqueue(itemId);
            }
        }

        // GetContainerContents does not include worn/wielded gear, so merge only
        // the player's own cache-known equipped objects into the same live snapshot.
        foreach (var cachedItem in GetInventory())
        {
            if (IsPlayerWielded(cachedItem))
                UpsertDirectInventoryItem(cachedItem);
        }

        if (_directInventory.Count > 0) DropVanishedInventory();
        return _directInventory.ToList();
        }
    }

    // ── Shared AV-safe pack finder (P2a) ──────────────────────────────────
    // ONE finder replacing the 4 divergent clones (InventoryManager.FindOpenPack,
    // BuffManager.FindOpenPackForDequip, MagToolsCommands.FindOpenPackForDequip,
    // ExpressionEngine.FindCastOpenPack). A FULL target pack CRASHES the client via
    // the native PutItemInContainer path, so we ONLY ever return a sub-pack whose
    // resolved free capacity (ItemsCapacity>0) is >= requireFree, picking the MOST-
    // free so a single mis-count can't tip a near-full pack over. The P0 engine gate
    // (ClientHelperHooks.IsFullOwnedContainer) is the hard truth; requireFree is
    // purely churn tuning.
    //
    //   includeMainPack=false  -> AutoCram: cram empties the MAIN pack, so it must
    //                             NEVER target it (would self-move and spin).
    //   includeMainPack=true   -> wand-swap / dequip: the bow needs ONE slot; fall
    //                             back to the main pack (top-level slots, cap 102)
    //                             when no sub-pack qualifies.
    //   requireFree            -> min free slots a sub-pack must have to be picked
    //                             (2 for the auto-loops = anti-churn margin; 1 for
    //                             user-driven /mt dequip + MetaCast wand-swap).
    //
    // The spec's `item` parameter is intentionally dropped: no clone ever read the
    // source item when choosing a destination (vestigial). Two overloads preserve
    // both snapshot disciplines: the (cache) overload force-refreshes for the AV-
    // risky dequip paths (must not read stale); the (inv) overload reuses a caller-
    // supplied snapshot (AutoCram's single per-tick snapshot — no double BFS).
    public static int FindPackFor(RynthCoreHost host, WorldObjectCache? cache,
                                  bool includeMainPack, int requireFree)
    {
        if (cache == null) return 0;
        int playerId = unchecked((int)host.GetPlayerId());
        if (playerId == 0) return 0;
        // forceRefresh — the dequip paths gate an AV-risky move, so must not read a
        // stale cache before the move.
        var inv = cache.GetDirectInventory(forceRefresh: true);
        return FindPackFor(inv, playerId, includeMainPack, requireFree, host);
    }

    /// <summary>
    /// The player is wielding <paramref name="wo"/>. The cached CurrentWieldedLocation /
    /// EquippedSlots can read 0 for a wielded weapon (2026-09-28: a Longbow in hand read
    /// 0, so the wand swap never stowed it and retried forever with a full pack), so a
    /// 0 falls back to the live wielder info.
    /// </summary>
    public static bool IsWieldedByPlayer(RynthCoreHost host, WorldObject wo)
    {
        // The server's word (the engine's EquipmentTracker, API v87) when the engine has it.
        if (EquippedState.IsWielded(host, wo.Id) is bool tracked) return tracked;
        // Found inside a pack by the client's container walk: not wielded, whatever the weenie's
        // wielder record still says. It kept the player after buffing put both Hand Wraps into the
        // pack for the wand (2026-10-17), so combat took Drakkon as armed, never wielded anything,
        // and he fought bare-handed for 19 s until he died.
        if (wo.Cache != null && wo.Cache.InPackWalk(wo.Id)) return false;
        if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) > 0) return true;
        if (wo.Values(LongValueKey.EquippedSlots, 0) != 0) return true;
        if (!host.HasGetObjectWielderInfo) return false;
        uint pid = host.GetPlayerId();
        return pid != 0
            && host.TryGetObjectWielderInfo(unchecked((uint)wo.Id), out uint wielder, out _)
            && wielder == pid;
    }

    /// <summary>
    /// Arrows, quarrels and darts. AC files ammo as a missile weapon, so the "put the bow
    /// away before the wand" lookups picked the wielded arrows when they came first and
    /// took them off instead of the bow (2026-09-29). Slot bits when known, else the name.
    /// </summary>
    public static bool IsAmmo(WorldObject wo)
    {
        const int MissileAmmoSlot = 0x00800000;
        if ((wo.Values(LongValueKey.Locations, 0) & MissileAmmoSlot) != 0) return true;
        if ((wo.Values(LongValueKey.CurrentWieldedLocation, 0) & MissileAmmoSlot) != 0) return true;
        // Never a launcher: a weapon whose name happens to say "bolt" must still be put away.
        string n = wo.Name ?? "";
        if (n.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0
            || n.TrimEnd().EndsWith("bow", StringComparison.OrdinalIgnoreCase)) return false;
        return AmmoRecipes.IsLooseAmmoName(n, WeaponCategory.Bow)
            || AmmoRecipes.IsLooseAmmoName(n, WeaponCategory.Crossbow)
            || AmmoRecipes.IsLooseAmmoName(n, WeaponCategory.Atlatl);
    }

    private static bool IsWieldable(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon
        || wo.ObjectClass == AcObjectClass.MissileWeapon
        || wo.ObjectClass == AcObjectClass.WandStaffOrb;

    /// <summary>
    /// Free item slots the player has: the main pack (102) plus every side pack. -1 when a
    /// side pack's capacity isn't known yet (callers then don't act on a guess).
    /// </summary>
    public static int CountFreeItemSlots(RynthCoreHost host, WorldObjectCache? cache)
    {
        if (cache == null) return -1;
        int playerId = unchecked((int)host.GetPlayerId());
        if (playerId == 0) return -1;
        var inv = cache.GetDirectInventory(forceRefresh: false);
        int mainUsed = 0, sideFree = 0;
        foreach (var p in inv)
        {
            if (p.Container != playerId) continue;
            if (p.ObjectClass == AcObjectClass.Foci) continue;
            if (p.ObjectClass != AcObjectClass.Container)
            {
                if (p.Values(LongValueKey.EquippedSlots, 0) != 0) continue;
                if (IsWieldable(p) && IsWieldedByPlayer(host, p)) continue;
                mainUsed++;
                continue;
            }
            if (!string.IsNullOrEmpty(p.Name) && p.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            int capacity = p.Values(LongValueKey.ItemsCapacity, 0);
            if (capacity <= 0) return -1;
            int used = 0;
            foreach (var it in inv)
                if (it.Container == p.Id && it.ObjectClass != AcObjectClass.Container) used++;
            sideFree += Math.Max(0, capacity - used);
        }
        return Math.Max(0, 102 - mainUsed) + sideFree;
    }

    private static DateTime _packFullWarnedAt = DateTime.MinValue;

    /// <summary>A chat warning that a full pack blocks something, at most once every 5 minutes.</summary>
    public static void WarnPackFull(RynthCoreHost host, string what)
    {
        if ((RynthClock.Now - _packFullWarnedAt).TotalMinutes < 5) return;
        _packFullWarnedAt = RynthClock.Now;
        host.WriteToChat($"[RynthAi] Pack full: {what}. Free a slot (salvage, sell or drop); looting keeps {LootReserveFreeSlots} slots free.", 2);
    }

    /// <summary>The looter stops picking items up below this many free slots, so weapon swaps always have room.</summary>
    public const int LootReserveFreeSlots = 2;

    // Snapshot overload — caller supplies an already-built inventory list (AutoCram
    // reuses its single per-tick GetDirectInventory snapshot here; do NOT re-refresh).
    public static int FindPackFor(IReadOnlyList<WorldObject> inv, int playerId,
                                  bool includeMainPack, int requireFree, RynthCoreHost? host = null,
                                  Func<int, bool>? skip = null)
    {
        if (inv == null || playerId == 0) return 0;
        if (requireFree < 1) requireFree = 1;
        int bestPack = 0, bestFree = 0;
        int mainUsed = 0; // loose top-level item-slot occupancy (mirrors ScanInventoryStatus 594-604)
        foreach (var p in inv)
        {
            if (p.Container != playerId) continue;
            // Main-pack item-slot tally: loose, non-equipped, non-foci, non-container
            // items directly in the player pack. Sub-packs occupy the side-pack slots,
            // NOT the 102 item slots, so they're excluded from this count.
            if (p.ObjectClass != AcObjectClass.Container
                && p.ObjectClass != AcObjectClass.Foci
                && p.Values(LongValueKey.EquippedSlots, 0) == 0
                && !(host.HasValue && IsWieldable(p) && IsWieldedByPlayer(host.Value, p)))
                mainUsed++;
            if (p.ObjectClass != AcObjectClass.Container) continue;
            // Skip foci — technically containers but reserved for spell components.
            if (!string.IsNullOrEmpty(p.Name)
                && p.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (skip != null && skip(p.Id)) continue; // the server refused it lately (PackRefusals)
            int capacity = p.Values(LongValueKey.ItemsCapacity, 0);
            if (capacity <= 0) continue; // capacity unknown — don't risk the move
            int used = 0;
            foreach (var it in inv)
            {
                if (it.Container != p.Id) continue;
                if (it.ObjectClass == AcObjectClass.Container) continue; // sub-containers use CONTAINERS_CAPACITY
                used++;
            }
            int free = capacity - used;
            // requireFree margin: a pack with fewer than requireFree slots is ineligible
            // so a single mis-count can't tip a near-full pack over (anti-churn).
            if (free >= requireFree && free > bestFree) { bestFree = free; bestPack = p.Id; }
        }
        if (bestPack != 0) return bestPack;
        if (!includeMainPack) return 0; // AutoCram: never target the main pack (would self-move/spin)
        // Dequip fallback: the bow needs ONE free main-pack slot; a dequip into a
        // non-full main pack is AV-safe (the AV is only on a genuinely FULL target).
        int mainFree = 102 - mainUsed;
        return mainFree > 0 && !(skip != null && skip(playerId)) ? playerId : 0;
    }

    /// <summary>
    /// Where a looted item goes: the main pack (the player) or 0. The pickup used to be a
    /// UseObject, which let the client pick the spot, but a use on a wearable item lying in a
    /// corpse is a get-and-wield: jewellery for an empty slot went straight onto the character
    /// (2026-10-02). So it is a move, and a move from a corpse or chest can only go to the main
    /// pack: the client refuses one straight into a side pack ("You must first pick up the X",
    /// and nothing is sent), so the side-pack fallback this used to return when the main pack
    /// was full never worked (2026-11-12: 75 such pickups in a week of logs, none landed; the
    /// looter waited out 3 + 3 tries on each and left them).
    /// <list type="bullet">
    /// <item>The main pack has a free slot: the main pack.</item>
    /// <item>It is full, and <paramref name="item"/> joins a stack you carry: the main pack anyway.
    /// The client puts a stack onto a same-name stack with room wherever that is (the pea stacks
    /// live in a side pack), so no slot is needed.</item>
    /// <item>Otherwise 0: nothing can take it from the corpse (<see cref="SidePackHasRoom"/> tells
    /// a full main pack from no room at all).</item>
    /// </list>
    /// </summary>
    /// <remarks><paramref name="skip"/>: packs (or the player id, for the main pack) the server refused lately;
    /// a stack join needs no slot, so it goes whatever the main pack's mark.</remarks>
    public static int FindLootDestination(IReadOnlyList<WorldObject> inv, int playerId, RynthCoreHost? host = null,
                                          Func<int, bool>? skip = null, WorldObject? item = null)
    {
        if (inv == null || playerId == 0) return 0;
        if (item != null && JoinsCarriedStack(inv, playerId, item)) return playerId;
        if (MainPackFreeSlots(inv, playerId, host) > 0 && !(skip != null && skip(playerId))) return playerId;
        return 0;
    }

    /// <summary>The main pack's free item slots (102 less the loose items in it; packs, foci and worn items don't count).</summary>
    public static int MainPackFreeSlots(IReadOnlyList<WorldObject> inv, int playerId, RynthCoreHost? host = null)
    {
        int mainUsed = 0; // same tally as FindPackFor
        foreach (var p in inv)
        {
            if (p.Container != playerId) continue;
            if (p.ObjectClass == AcObjectClass.Container || p.ObjectClass == AcObjectClass.Foci) continue;
            if (p.Values(LongValueKey.EquippedSlots, 0) != 0) continue;
            if (host.HasValue && IsWieldable(p) && IsWieldedByPlayer(host.Value, p)) continue;
            mainUsed++;
        }
        return 102 - mainUsed;
    }

    /// <summary>A side pack has a free slot (so a loot pickup that found no destination met a full main pack, not full packs).</summary>
    public static bool SidePackHasRoom(IReadOnlyList<WorldObject> inv, int playerId, RynthCoreHost? host = null)
        => inv != null && playerId != 0 && FindPackFor(inv, playerId, includeMainPack: false, requireFree: 1, host) != 0;

    /// <summary>
    /// <paramref name="item"/> stacks and a stack of the same kind you carry (main pack or a side
    /// pack) still has room for one more: a pickup aimed at the main pack joins it.
    /// </summary>
    internal static bool JoinsCarriedStack(IReadOnlyList<WorldObject> inv, int playerId, WorldObject item)
    {
        int max = RynthCore.Plugin.RynthAi.Loot.InventoryManager.GetMaxStackSize(item);
        if (max <= 1 || string.IsNullOrEmpty(item.Name)) return false;
        int wcid = item.Values(unchecked((int)DecalTypeKey), 0);
        var packs = new HashSet<int>();
        foreach (var p in inv)
            if (p.Container == playerId && p.ObjectClass == AcObjectClass.Container) packs.Add(p.Id);
        foreach (var s in inv)
        {
            if (s.Id == item.Id || s.ObjectClass == AcObjectClass.Container) continue;
            if (s.Container != playerId && !packs.Contains(s.Container)) continue;
            int sw = s.Values(unchecked((int)DecalTypeKey), 0);
            bool same = wcid != 0 && sw != 0 ? sw == wcid : string.Equals(s.Name, item.Name, StringComparison.Ordinal);
            if (!same) continue;
            if (Math.Max(1, s.Values(LongValueKey.StackCount, 1)) < max) return true;
        }
        return false;
    }

    /// <summary>
    /// Re-reads the name and item type of an object stored with a blank name or an Unknown
    /// class, and replaces its entry when either improved. Corpse items are usually created
    /// before the engine's off-thread identity snapshot (refreshed every ~0.5 s) has them,
    /// so they were classified once with no name and no type and never revisited: looting
    /// then judged 3 of 4 items nameless and left them (2026-09-27). Cheap when complete.
    /// </summary>
    public WorldObject RefreshIdentity(WorldObject wo)
    {
        bool needName = string.IsNullOrWhiteSpace(wo.Name);
        bool needClass = wo.ObjectClass == AcObjectClass.Unknown;
        if (!needName && !needClass)
            return wo;

        uint uid = unchecked((uint)wo.Id);
        string name = wo.Name ?? string.Empty;
        if (needName && _host.TryGetObjectName(uid, out string n) && !string.IsNullOrWhiteSpace(n))
            name = n;

        AcObjectClass cls = wo.ObjectClass;
        if (needClass)
        {
            if (_host.TryGetItemType(uid, out uint flags) && (flags & ItemTypeCreature) == 0)
                cls = ClassifyItem(uid, flags);
            if (cls == AcObjectClass.Unknown && name.Length > 0)
                cls = ClassifyInventoryItem(name);
        }

        if (name == wo.Name && cls == wo.ObjectClass)
            return wo;

        lock (_gate)
        {
            // Only replace the entry we were handed; a concurrent reclassify wins.
            if (!_byId.TryGetValue(wo.Id, out var current) || !ReferenceEquals(current, wo))
                return current ?? wo;
            var fresh = Make(wo.Id, name, cls);
            _byId[wo.Id] = fresh;
            return fresh;
        }
    }

    /// <summary>
    /// Re-reads the item type of <paramref name="id"/> and replaces its entry when the type
    /// gives a different class. An object first seen before its type was known is classed by
    /// its name, and RefreshIdentity only revisits Unknown ones, so a guess stuck: a quest
    /// axe without "Axe" in its name (Silifi, Tungi, Ono...) was "not a weapon" when added to
    /// the Items list (2026-10-03). Returns the entry to use (null when not cached).
    /// </summary>
    public WorldObject? ReclassifyFromItemType(int id)
    {
        WorldObject? wo;
        lock (_gate) _byId.TryGetValue(id, out wo);
        if (wo == null) return null;
        bool typed = _host.TryGetItemType(unchecked((uint)id), out uint flags);
        if (typed && (flags & ItemTypeCreature) != 0)
            return wo;
        AcObjectClass cls = typed ? ClassifyByItemType(flags) : AcObjectClass.Unknown;
        // Custom content can carry a non-weapon item type on something wielded as a weapon
        // (a Driftwarden Greataxe, 2026-10-03): its weapon slot or weapon skill decides.
        if (cls != AcObjectClass.MeleeWeapon && cls != AcObjectClass.MissileWeapon && cls != AcObjectClass.WandStaffOrb)
        {
            AcObjectClass bySlot = WeaponClassFromSlot(unchecked((uint)id));
            if (bySlot != AcObjectClass.Unknown) cls = bySlot;
        }
        if (cls == AcObjectClass.Unknown || cls == wo.ObjectClass)
            return wo;

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var current) || !ReferenceEquals(current, wo))
                return current ?? wo;
            var fresh = Make(id, wo.Name, cls);
            fresh._wieldedLocationDirect = wo._wieldedLocationDirect;
            fresh._directContainerId = wo._directContainerId;
            fresh._directSlot = wo._directSlot;
            fresh.Overlay = wo.Overlay;
            _byId[id] = fresh;
            return fresh;
        }
    }

    // ValidLocations (STypeInt 9) weapon slots and WeaponSkill (STypeInt 48).
    private const int ValidLocationsInt = 9, WeaponSkillInt = 48;
    private const uint SlotMeleeWeapon = 0x00100000, SlotMissileWeapon = 0x00400000, SlotTwoHanded = 0x02000000;

    /// <summary>ClassifyByItemType, except that an item typed Misc (or nothing) which fits a
    /// weapon slot is a weapon: custom content does this (see ReclassifyFromItemType).</summary>
    private AcObjectClass ClassifyItem(uint uid, uint flags)
    {
        AcObjectClass cls = ClassifyByItemType(flags);
        // A writable that carries a spell (data id 28) or is named "Scroll ..." is a Scroll, as in
        // Decal/VTank, so metas, AutoVendor and loot rules all see "Scroll" (owner, 2026-10-04).
        // Other writables (books, notes, journals) stay Book.
        if (cls == AcObjectClass.Book && IsSpellScroll(uid)) return AcObjectClass.Scroll;
        if (cls == AcObjectClass.Misc || cls == AcObjectClass.Unknown)
        {
            AcObjectClass bySlot = WeaponClassFromSlot(uid);
            if (bySlot != AcObjectClass.Unknown) return bySlot;
        }
        return cls;
    }

    private const uint DataIdSpell = 28;

    private bool IsSpellScroll(uint uid)
    {
        if (_host.TryGetObjectDataIdProperty(uid, DataIdSpell, out uint spell) && spell != 0) return true;
        return _host.TryGetObjectName(uid, out string name)
            && name.StartsWith("Scroll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The weapon class an item's equip slot implies (Unknown when it has no weapon slot).
    /// A weapon skill with no slot known reads as melee.</summary>
    private AcObjectClass WeaponClassFromSlot(uint id)
    {
        if (!_host.HasGetObjectIntProperty) return AcObjectClass.Unknown;
        if (_host.TryGetObjectIntProperty(id, ValidLocationsInt, out int slots) && slots != 0)
        {
            uint s = unchecked((uint)slots);
            if ((s & SlotMissileWeapon) != 0) return AcObjectClass.MissileWeapon;
            if ((s & (SlotMeleeWeapon | SlotTwoHanded)) != 0) return AcObjectClass.MeleeWeapon;
            return AcObjectClass.Unknown;
        }
        if (_host.TryGetObjectIntProperty(id, WeaponSkillInt, out int skill) && skill > 0)
            return AcObjectClass.MeleeWeapon;
        return AcObjectClass.Unknown;
    }

    public IEnumerable<WorldObject> GetContainedItems(int containerId)
    {
        if (containerId == 0)
            return Array.Empty<WorldObject>();

        // Snapshot candidates under the lock (pure collection reads); resolve container
        // ownership outside the lock — GetContainerId is a host-only read, no cache access.
        List<WorldObject> candidates;
        lock (_gate)
        {
            candidates = new List<WorldObject>(_inventory.Count);
            foreach (int id in _inventory)
                if (_byId.TryGetValue(id, out var wo))
                    candidates.Add(wo);

            // Quest items and items with dynamic (0x80000000+) GUIDs are classified
            // as landscape rather than inventory. Check landscape too so they appear
            // as corpse contents when an open corpse is scanned.
            foreach (int id in _landscape)
            {
                if (_inventory.Contains(id)) continue; // already added above
                if (_creatures.Contains(id)) continue; // it's a live creature, not a container item
                if (_byId.TryGetValue(id, out var wo))
                    candidates.Add(wo);
            }
        }

        var result = new List<WorldObject>();
        foreach (var wo in candidates)
            if (GetContainerId(wo.Id) == containerId)
                result.Add(wo);
        return result;
    }

    /// <summary>
    /// Items the client lists inside <paramref name="containerId"/> that the cache never
    /// classified, looked up now (the indexer's lazy lookup files them) and returned.
    /// <paramref name="known"/> is what <see cref="GetContainedItems"/> already found.
    /// An item whose create the cache never processed is otherwise invisible to corpse
    /// looting: it isn't in the inventory or landscape sets, so it is never a candidate
    /// and the corpse is finished without it (2026-10-11, Silentkelpie: a Necrolytic
    /// Extract on a corpse with a Keep rule, left behind with no log line; the cache
    /// reported it DELETE-BEFORE-CLASSIFY when the corpse went away).
    /// </summary>
    public List<WorldObject> RescueContainedItems(int containerId, IReadOnlyCollection<WorldObject> known)
    {
        var rescued = new List<WorldObject>();
        if (containerId == 0 || !_host.HasGetContainerContents) return rescued;

        uint[] buf = new uint[256];
        int count;
        try { count = _host.GetContainerContents(unchecked((uint)containerId), buf); }
        catch { return rescued; }
        if (count <= 0) return rescued;

        var have = new HashSet<int>();
        foreach (var wo in known) have.Add(wo.Id);

        for (int i = 0; i < Math.Min(count, buf.Length); i++)
        {
            int id = unchecked((int)buf[i]);
            if (id == 0 || have.Contains(id)) continue;
            WorldObject? wo = this[id];
            if (wo == null) continue;
            if (wo.ObjectClass == AcObjectClass.Corpse || wo.ObjectClass == AcObjectClass.Monster) continue;
            rescued.Add(wo);
        }
        return rescued;
    }

    /// <summary>Enumerate landscape creatures (received health updates or TYPE_CREATURE).</summary>
    public IEnumerable<WorldObject> GetLandscape()
    {
        // Copy-under-lock — the Avalonia radar poll enumerates this cross-thread (see GetInventory).
        lock (_gate)
        {
            var snapshot = new List<WorldObject>(_creatures.Count);
            foreach (int id in _creatures)
                if (_byId.TryGetValue(id, out var wo))
                    snapshot.Add(wo);
            return snapshot;
        }
    }

    /// <summary>Enumerate all world objects with a valid landscape position, including corpses.</summary>
    public IEnumerable<WorldObject> GetLandscapeObjects()
    {
        // Copy-under-lock — the Avalonia radar poll enumerates this cross-thread (see GetInventory).
        lock (_gate)
        {
            var snapshot = new List<WorldObject>(_landscape.Count);
            foreach (int id in _landscape)
                if (_byId.TryGetValue(id, out var wo))
                    snapshot.Add(wo);
            return snapshot;
        }
    }

    // ── Hazard cells (lava / acid / fire / cold pools) ───────────────────────
    //
    // Hotspot weenies in AC are server-side WorldObjects that damage on collision;
    // they're not part of the EnvCell graph and so the dungeon pathfinder can't see
    // them. We populate this set two ways:
    //   1. WO sightings: any landscape (positioned, non-creature) object whose name
    //      matches a known hazard pattern marks its cellId here.
    //   2. Reactive blacklist: NavigationEngine / patrol loop marks the player's
    //      current cell if HP drops with no nearby hostile (see MarkCellHazard).
    // DungeonPathfinder consumes this set via the hazardCells parameter on
    // FindPath / BuildPatrolRoute and skips edges into hazard cells the same way
    // it skips drop edges.

    private static readonly string[] HazardNamePatterns =
    {
        "lava", "pool of acid", "acid pool", "pool of fire", "pool of cold",
        "cesspool", "hot spring", "magma",
    };

    private readonly HashSet<uint> _hazardCells = new();

    // Bumped every time a NEW hazard cell is registered. The patrol controller
    // snapshots this when it builds a route and compares each tick: a change means
    // a lava/acid hotspot was sighted that the current route doesn't yet avoid, so
    // the route must be rebuilt around it. Cheaper than diffing the set every tick.
    private int _hazardVersion;

    public int HazardVersion
    {
        get { lock (_gate) return _hazardVersion; }
    }

    /// <summary>
    /// A dungeon cell the character took hotspot damage in ("You suffer 32 damage from acid!"):
    /// registered and persisted like a sighted hazard, so the patrol reroutes around it now and
    /// on every later visit. Outdoor cells (a 24 m land cell) are never registered. True when new.
    /// </summary>
    public bool RegisterDamageHazardCell(uint cellId, string source)
    {
        if (cellId == 0 || (cellId & 0xFFFF) < 0x0100) return false;
        lock (_gate)
        {
            if (!_hazardCells.Add(cellId)) return false;
            _hazardVersion++;
        }
        DungeonHazardStore.Append(cellId >> 16, cellId);
        _host.Log($"[Hazard] took {source} damage in cell 0x{cellId:X8} (persisted)");
        return true;
    }

    public bool IsHazardCell(uint cellId)
    {
        lock (_gate)
            return _hazardCells.Contains(cellId);
    }

    public int HazardCellCount
    {
        get { lock (_gate) return _hazardCells.Count; }
    }

    /// <summary>Live hazard cells for a landblock (cellId &gt;&gt; 16 == landblockKey).</summary>
    public List<uint> GetHazardCellsForLandblock(uint landblockKey)
    {
        lock (_gate)
        {
            var list = new List<uint>();
            foreach (uint c in _hazardCells)
                if ((c >> 16) == landblockKey) list.Add(c);
            list.Sort();
            return list;
        }
    }

    /// <summary>
    /// Manually marks a cell as a hazard (user "this is lava" command / UI button). Adds to
    /// the live set, persists it, and bumps <see cref="HazardVersion"/> so an active patrol
    /// reroutes around it. Returns true if it was newly added.
    /// </summary>
    public bool AddHazardCell(uint cellId)
    {
        if (cellId == 0) return false;
        lock (_gate)
        {
            if (!_hazardCells.Add(cellId)) return false;
            _hazardVersion++;
            DungeonHazardStore.Append(cellId >> 16, cellId);
            _host.Log($"[Hazard] manually marked cell 0x{cellId:X8}");
            return true;
        }
    }

    /// <summary>
    /// Removes a manually- or auto-marked hazard cell from the live set and the on-disk store.
    /// Bumps <see cref="HazardVersion"/>. Returns true if it was present.
    /// </summary>
    public bool RemoveHazardCell(uint cellId)
    {
        lock (_gate)
        {
            bool removed = _hazardCells.Remove(cellId);
            DungeonHazardStore.RemoveCell(cellId >> 16, cellId);
            if (removed) _hazardVersion++;
            return removed;
        }
    }

    /// <summary>
    /// Drops live hazard cells for one landblock (cellId &gt;&gt; 16 == landblockKey) and
    /// re-arms seeding for it, so a later patrol of that dungeon reloads from disk fresh.
    /// Bumps <see cref="HazardVersion"/> so an active patrol there rebuilds without the
    /// cleared cells. The caller is responsible for clearing the on-disk store separately.
    /// </summary>
    public void ClearLiveHazards(uint landblockKey)
    {
        lock (_gate)
        {
            _hazardCells.RemoveWhere(c => (c >> 16) == landblockKey);
            if (_hazardsSeededLandblock == landblockKey) _hazardsSeededLandblock = 0;
            _hazardVersion++;
        }
    }

    /// <summary>Drops every live hazard cell and re-arms seeding. Bumps HazardVersion.</summary>
    public void ClearAllLiveHazards()
    {
        lock (_gate)
        {
            _hazardCells.Clear();
            _hazardsSeededLandblock = 0;
            _hazardVersion++;
        }
    }

    public IReadOnlySet<uint> GetHazardCells()
    {
        // Snapshot — consumers (DungeonPathfinder) want a point-in-time set per path call.
        lock (_gate)
            return new HashSet<uint>(_hazardCells);
    }

    // Landblock whose persisted hazards have already been merged in, so SeedHazardsFromStore
    // is a cheap no-op when the patrol builder calls it every (re)build for the same dungeon.
    private uint _hazardsSeededLandblock;

    /// <summary>
    /// Merges the on-disk hazard cells recorded for <paramref name="landblockKey"/> on prior
    /// visits into the live set, so a patrol route built right after entering a known dungeon
    /// avoids them from the start instead of having to re-sight and reroute. Cheap no-op once
    /// per landblock. Bumps <see cref="HazardVersion"/> if it adds anything (so an already-
    /// running patrol reroutes around the loaded cells too).
    /// </summary>
    public void SeedHazardsFromStore(uint landblockKey)
    {
        lock (_gate)
        {
            if (_hazardsSeededLandblock == landblockKey) return;
            _hazardsSeededLandblock = landblockKey;

            var persisted = DungeonHazardStore.Load(landblockKey);
            int added = 0;
            foreach (uint cell in persisted)
                if (_hazardCells.Add(cell)) added++;

            if (added > 0)
            {
                _hazardVersion++;
                _host.Log($"[Hazard] seeded {added} persisted hazard cell(s) for landblock 0x{landblockKey:X4}");
            }
        }
    }

    /// <summary>
    /// Detector C: marks EnvCells flagged by their lava/acid surface texture as live hazard cells.
    /// Unlike <see cref="AddHazardCell"/> these are NOT persisted to DungeonHazardStore — they are
    /// re-derived from the cell.dat surface palette + the hazard-texture set on every patrol build,
    /// so the texture set stays the single source of truth (re-running with a smaller texture set
    /// must drop them). Bumps <see cref="HazardVersion"/> if it adds anything. Never throws.
    /// </summary>
    public void SeedSurfaceHazards(IEnumerable<uint> cells)
    {
        if (cells == null) return;
        lock (_gate)
        {
            int added = 0;
            foreach (uint c in cells)
                if (c != 0 && _hazardCells.Add(c)) added++;

            if (added > 0)
            {
                _hazardVersion++;
                _host.Log($"[Hazard] Detector C: {added} EnvCell-surface hazard cell(s) seeded from floor textures");
            }
        }
    }

    private static bool IsHazardName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string pat in HazardNamePatterns)
            if (name.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    /// <summary>
    /// Inspect a freshly-classified landscape object and register it as a hazard if
    /// its name matches a known hotspot pattern (lava, acid pool, etc.).
    /// Safe to call repeatedly for the same uid — set membership is idempotent.
    /// </summary>
    private void MaybeRegisterHazard(uint uid, string name)
    {
        if (!IsHazardName(name)) return;
        if (!_host.TryGetObjectPosition(uid, out uint cellId, out _, out _, out _)) return;
        if (cellId == 0) return;
        if (_hazardCells.Add(cellId))
        {
            _hazardVersion++;
            // Persist so future visits to this dungeon avoid the cell from the first
            // waypoint. Keyed by landblock (cellId >> 16) — hazards are static world
            // geometry, identical for every character.
            DungeonHazardStore.Append(cellId >> 16, cellId);
            _host.Log($"[Hazard] 0x{uid:X8} '{name}' → cell 0x{cellId:X8} (persisted)");
        }
    }

    /// <summary>
    /// Discover all inventory items via native container scan.
    /// Finds items whose CreateObject events fired before the plugin loaded.
    /// Call once at login after hooks are ready.
    /// </summary>
    public int ScanFullInventory()
    {
        lock (_gate)
        {
        if (!_host.HasGetContainerContents) return -1;
        uint playerId = _host.GetPlayerId();
        if (playerId == 0) return -1;

        // Warm up engine probes before bulk scanning:
        // 1. TryGetObjectPosition triggers physOffsetProbe (_weeniePhysicsObjOffset)
        //    needed by TryGetObjectOwnershipInfo / GetContainerContents
        // 2. TryGetObjectIntProperty triggers qualitiesOffsetProbe (_weenieQualitiesOffset)
        //    needed by later property queries — probing now prevents crash if done after scan
        _host.TryGetObjectPosition(playerId, out _, out _, out _, out _);
        _host.TryGetObjectIntProperty(playerId, 1 /* STypeInt.ItemType */, out _);

        int discovered = 0;
        uint[] buf = new uint[512];

        // Scan player's direct contents
        int topCount = _host.GetContainerContents(playerId, buf);
        _host.Log($"[RynthAi] ScanFullInventory: topCount={topCount} for player 0x{playerId:X8}");
        for (int i = 0; i < topCount; i++)
            discovered += EnsureInCache(buf[i]);

        // Scan inside each pack/container
        // Collect pack IDs first to avoid modifying collection during iteration
        var packIds = new List<uint>();
        for (int i = 0; i < topCount; i++)
        {
            if (_host.TryGetItemType(buf[i], out uint flags) && (flags & ItemTypeContainer) != 0)
                packIds.Add(buf[i]);
        }

        foreach (uint packId in packIds)
        {
            uint[] packBuf = new uint[256];
            int packCount = _host.GetContainerContents(packId, packBuf);
            for (int i = 0; i < packCount; i++)
                discovered += EnsureInCache(packBuf[i]);
        }

        if (discovered > 0)
            _host.Log($"[RynthAi] ScanFullInventory: discovered {discovered} new item(s) across {packIds.Count + 1} container(s)");

        return discovered;
        }
    }

    private int EnsureInCache(uint uid)
    {
        lock (_gate)
        {
            int id = (int)uid;
            // Use the indexer for lazy lookup (reads name, classifies, adds to cache)
            if (_byId.ContainsKey(id) || this[id] != null)
            {
                // Item is known to be inside a container — force into _inventory
                // even if the indexer classified it as landscape (equipped items have positions)
                if (!_inventory.Contains(id))
                {
                    _inventory.Add(id);
                    _landscape.Remove(id);
                }
                return _byId.ContainsKey(id) ? 1 : 0;
            }
            return 0;
        }
    }

    private bool TryAddDirectInventoryItem(uint uid, uint playerId, uint containerId, int slot, out bool isContainer)
    {
        isContainer = false;

        int id = unchecked((int)uid);
        if (!_host.TryGetObjectName(uid, out string name) || string.IsNullOrWhiteSpace(name))
            return false;

        int wieldedLocation = 0;
        if (_host.HasGetObjectWielderInfo &&
            _host.TryGetObjectWielderInfo(uid, out uint wielderByInfo, out uint locByInfo) &&
            wielderByInfo == playerId &&
            locByInfo > 0)
        {
            wieldedLocation = unchecked((int)locByInfo);
        }
        else if (_host.HasGetObjectOwnershipInfo &&
                 _host.TryGetObjectOwnershipInfo(uid, out _, out uint wielder, out uint loc) &&
                 wielder == playerId &&
                 loc > 0)
        {
            wieldedLocation = unchecked((int)loc);
        }

        AcObjectClass cls = AcObjectClass.Unknown;
        if (_host.TryGetItemType(uid, out uint flags))
        {
            isContainer = (flags & ItemTypeContainer) != 0;
            cls = ClassifyItem(uid, flags);
        }

        if (cls == AcObjectClass.Unknown)
            cls = ClassifyInventoryItem(name);

        var wo = new WorldObject(id, name, cls);
        wo._wieldedLocationDirect = wieldedLocation;
        wo._directContainerId = unchecked((int)containerId);   // authoritative parent from the BFS
        wo._directSlot = slot;                                  // position in this container's contents
        wo.Cache = this;
        UpsertDirectInventoryItem(wo);
        return true;
    }

    /// <summary>Inventory entries dropped by <see cref="DropVanishedInventory"/> (for the log and the tests).</summary>
    internal int VanishedDropped { get; private set; }

    /// <summary>
    /// After a live walk: an inventory entry the walk didn't find and the client can't even name any
    /// more is gone (its removal was missed), so it leaves the inventory set. Wielded gear (never in
    /// the walk) and anything the client still knows stay: a partial walk can't empty the pack.
    /// A used-up Treated Healing Kit stayed in the set and was "used" 70 times after a respawn, the
    /// server answering at once with no heal (2026-11-03); every reader of GetInventory (kits,
    /// potions, tinctures, mana stones, component counts, meta item counts, /mt use) saw it. Under _gate.
    /// </summary>
    private void DropVanishedInventory()
    {
        List<int>? gone = null;
        foreach (int id in _inventory)
        {
            if (_directInventoryIds.Contains(id)) continue;
            uint uid = unchecked((uint)id);
            if (_host.TryGetObjectName(uid, out string name) && !string.IsNullOrWhiteSpace(name)) continue;
            (gone ??= new List<int>()).Add(id);
        }
        if (gone == null) return;
        foreach (int id in gone)
        {
            string name = _byId.TryGetValue(id, out var wo) ? wo.Name : string.Empty;
            _inventory.Remove(id);
            _byId.Remove(id);
            _classifyRetry.Remove(unchecked((uint)id));
            _slowRetry.Remove(unchecked((uint)id));
            _reclassifySkipState.Remove(unchecked((uint)id));
            VanishedDropped++;
            _host.Log($"[RynthAi] Inventory: 0x{unchecked((uint)id):X8} '{name}' is gone from the client (used up, given or dropped; its removal was missed): dropped from the cache.");
        }
        _inventoryDirty = true;
    }

    private void UpsertDirectInventoryItem(WorldObject item)
    {
        if (_directInventoryIndex.TryGetValue(item.Id, out int index))
        {
            var existing = _directInventory[index];
            if (ShouldReplaceDirectInventoryItem(existing, item))
                _directInventory[index] = item;
            return;
        }

        _directInventoryIds.Add(item.Id);
        _directInventoryIndex[item.Id] = _directInventory.Count;
        _directInventory.Add(item);
    }

    private static bool ShouldReplaceDirectInventoryItem(WorldObject existing, WorldObject candidate)
    {
        if (candidate.WieldedLocation > 0 && existing.WieldedLocation <= 0)
            return true;

        if (existing.ObjectClass == AcObjectClass.Unknown && candidate.ObjectClass != AcObjectClass.Unknown)
            return true;

        if (existing.Cache == null && candidate.Cache != null)
            return true;

        return false;
    }

    private bool IsPlayerWielded(WorldObject item)
    {
        if (_playerId == 0 || item.WieldedLocation <= 0)
            return false;

        return item.Wielder == unchecked((int)_playerId);
    }

    /// <summary>Cache statistics for diagnostics.</summary>
    public (int Total, int Creatures, int Inventory, int Landscape, int Pending) GetStats()
    {
        lock (_gate)
            return (_byId.Count, _creatures.Count, _inventory.Count, _landscape.Count, _pending.Count + _retryNext.Count);
    }

    // ── Factory ───────────────────────────────────────────────────────────

    private WorldObject Make(int id, string name, AcObjectClass cls)
    {
        var wo = new WorldObject(id, name, cls);
        wo.Cache = this;
        return wo;
    }

    // ── Type-flag item classification ─────────────────────────────────────

    internal static AcObjectClass ClassifyByItemType(uint typeFlags)
    {
        // Most specific / unambiguous types first
        if ((typeFlags & ItemTypePromissoryNote)            != 0) return AcObjectClass.TradeNote;
        if ((typeFlags & ItemTypeManaStone)                 != 0) return AcObjectClass.ManaStone;
        if ((typeFlags & ItemTypeSpellComponents)           != 0) return AcObjectClass.SpellComponent;
        if ((typeFlags & ItemTypeGem)                       != 0) return AcObjectClass.Gem;
        if ((typeFlags & ItemTypeKey)                       != 0) return AcObjectClass.Key;
        if ((typeFlags & ItemTypeLifeStone)                 != 0) return AcObjectClass.Lifestone;
        if ((typeFlags & ItemTypeTinkeringMaterial)         != 0) return AcObjectClass.Salvage;
        if ((typeFlags & ItemTypeTinkeringTool)             != 0) return AcObjectClass.Ust;
        if ((typeFlags & ItemTypeCraftCookingBase)          != 0) return AcObjectClass.BaseCooking;
        if ((typeFlags & ItemTypeCraftAlchemyBase)          != 0) return AcObjectClass.BaseAlchemy;
        if ((typeFlags & ItemTypeCraftFletchingBase)        != 0) return AcObjectClass.BaseFletching;
        if ((typeFlags & ItemTypeCraftAlchemyIntermediate)  != 0) return AcObjectClass.CraftedAlchemy;
        if ((typeFlags & ItemTypeCraftFletchingIntermediate)!= 0) return AcObjectClass.CraftedFletching;
        if ((typeFlags & ItemTypeService)                   != 0) return AcObjectClass.Services;
        if ((typeFlags & ItemTypePortal)                    != 0) return AcObjectClass.Portal;
        if ((typeFlags & ItemTypeWritable)                  != 0) return AcObjectClass.Book;
        if ((typeFlags & ItemTypeContainer)                 != 0) return AcObjectClass.Container;
        if ((typeFlags & ItemTypeCaster)                    != 0) return AcObjectClass.WandStaffOrb;
        if ((typeFlags & ItemTypeMissileWeapon)             != 0) return AcObjectClass.MissileWeapon;
        if ((typeFlags & ItemTypeArmor)                     != 0) return AcObjectClass.Armor;
        if ((typeFlags & ItemTypeMeleeWeapon)               != 0) return AcObjectClass.MeleeWeapon;
        if ((typeFlags & ItemTypeClothing)                  != 0) return AcObjectClass.Clothing;
        if ((typeFlags & ItemTypeJewelry)                   != 0) return AcObjectClass.Jewelry;
        if ((typeFlags & ItemTypeFood)                      != 0) return AcObjectClass.Food;
        if ((typeFlags & ItemTypeMoney)                     != 0) return AcObjectClass.Money;
        if ((typeFlags & ItemTypeMisc)                      != 0) return AcObjectClass.Misc;
        return AcObjectClass.Unknown;
    }

    // ── Name-based item classification (fallback) ─────────────────────────

    private static AcObjectClass ClassifyInventoryItem(string name)
    {
        if (string.IsNullOrEmpty(name))
            return AcObjectClass.Unknown;

        if (IsWand(name))          return AcObjectClass.WandStaffOrb;
        if (IsMissileWeapon(name)) return AcObjectClass.MissileWeapon;
        if (IsAmmo(name))          return AcObjectClass.MissileWeapon;
        if (IsClothing(name))      return AcObjectClass.Clothing;
        if (IsArmor(name))         return AcObjectClass.Armor;
        if (IsMeleeWeapon(name))   return AcObjectClass.MeleeWeapon;
        return AcObjectClass.Unknown;
    }

    private static bool IsWand(string name) =>
        ContainsAny(name, "Orb", "Staff", "Wand", "Scepter", "Sceptre", "Baton", "Crozier");

    private static bool IsMissileWeapon(string name) =>
        ContainsAny(name, "Bow", "Crossbow", "Atlatl");

    private static bool IsAmmo(string name) =>
        ContainsAny(name, "Arrow", "Quarrel", "Bolt", "Atlatl Dart");

    private static bool IsMeleeWeapon(string name) =>
        ContainsAny(name, "Sword", "Falchion", "Axe", "Mace", "Spear",
                    "Dagger", "Katar", "Claw", "Club", "Hammer", "Blade",
                    "Knife", "Lance", "Rapier", "Scimitar", "Estoc", "Cleaver",
                    "Flail", "Maul", "Glaive", "Halberd", "Pike", "Trident",
                    // retail names without one of the words above (quest weapons often use these)
                    "Hatchet", "Silifi", "Tungi", "Shou-ono", "Dolabra", "Lugian Hammer", "Kasrullah",
                    "Nabut", "Jitte", "Yaoji", "Tachi", "Takuba", "Shamshir", "Kaskara",
                    "Flamberge", "Jambiya", "Khanjar", "Kukri", "Poniard", "Stiletto", "Cestus",
                    "Nekode", "Bastone", "Tofun", "Morning Star", "Partizan", "Spadone", "Yari",
                    "Budiaq", "Naginata", "Quarterstaff", "Bardiche", "Assagai");

    private static bool IsClothing(string name) =>
        ContainsAny(name, "Coat", "Shirt", "Smock", "Pants", "Pantaloons", "Trousers",
                    "Vest", "Robe", "Over-Robe", "Cowl", "Cap", "Shoes", "Sandals",
                    "Slippers", "Shorts", "Tunic");

    private static bool IsArmor(string name) =>
        ContainsAny(name, "Shield", "Defender", "Helm", "Armet", "Basinet", "Breastplate",
                    "Cuirass", "Hauberk", "Jerkin", "Leggings", "Greaves", "Tassets",
                    "Pauldrons", "Bracers", "Gauntlets", "Girth", "Sollerets",
                    "Vambraces", "Coif", "Coronet", "Crown", "Diadem", "Kabuton",
                    "Boots", "Mask");

    private static bool IsCorpseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.EndsWith(" corpse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("'s corpse", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("corpse of ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string name, params string[] keywords)
    {
        foreach (string kw in keywords)
            if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private bool TryGetOwnership(int id, out int containerId, out int wielderId, out int location)
    {
        containerId = 0;
        wielderId = 0;
        location = 0;

        uint uid = unchecked((uint)id);
        bool gotAny = false;

        if (_host.HasGetObjectWielderInfo &&
            _host.TryGetObjectWielderInfo(uid, out uint wielderOnly, out uint wieldedOnly))
        {
            wielderId = unchecked((int)wielderOnly);
            location = unchecked((int)wieldedOnly);
            gotAny = gotAny || wielderOnly != 0 || wieldedOnly != 0;
        }

        if (_host.HasGetObjectOwnershipInfo &&
            _host.TryGetObjectOwnershipInfo(uid, out uint container, out uint wielder, out uint wieldedLocation))
        {
            containerId = unchecked((int)container);
            if (wielderId == 0)
                wielderId = unchecked((int)wielder);
            if (location == 0)
                location = unchecked((int)wieldedLocation);
            gotAny = true;
        }

        return gotAny;
    }
}
