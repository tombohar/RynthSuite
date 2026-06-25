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

    // OnCreateObject IDs pending classification
    private readonly Queue<uint> _pending = new();
    private const int MaxClassifyPerTick = 30;

    // Retry tracking for 0x8000xxxx objects that have no weenie yet (e.g. spawning corpses)
    private readonly Dictionary<uint, int> _classifyRetry = new();
    private const int MaxClassifyRetries = 8;

    // Slow-retry rescue. When the 8-tick fast-retry burst exhausts (all attempts
    // within ~10 ms because Tick processes 30 pending entries per pass), the uid
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
    private const int MaxReclassifyDiagLines = 500;
    private const int MaxReclassifyDiagSummaries = 60;

    // Diagnostic — track every uid OnCreateObject ever saw. Lets HEALTHADD-RESCUE
    // and INDEXER-RESCUE log lines flag the case where a mob enters _creatures via
    // a path other than OnCreateObject → TryClassify (i.e. the engine's CreateObject
    // hook never fired for this respawn — the suspected bug). No eviction: a delete +
    // recreate of the same id MUST keep showing seenCreate=1 to be diagnostic-correct.
    private readonly HashSet<uint> _seenCreateObject = new();
    private int _healthAddLogCount;
    private int _indexerRescueLogCount;
    private int _classifyGiveupLogCount;
    private int _deleteWhilePendingSkipLogCount;
    private int _deleteBeforeClassifyLogCount;
    private const int MaxHealthAddDiagLines = 200;
    private const int MaxIndexerRescueDiagLines = 200;
    private const int MaxClassifyGiveupLogLines = 200;
    private const int MaxDeleteWhilePendingSkipLogLines = 200;
    private const int MaxDeleteBeforeClassifyLogLines = 200;

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
    }

    public void SetPlayerId(uint playerId)
    {
        lock (_gate)
        {
            _playerId = playerId;
            _loginTime = DateTime.Now;
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
            if (id >= 0x80000000u && _deleteBeforeClassifyLogCount < MaxDeleteBeforeClassifyLogLines)
            {
                _deleteBeforeClassifyLogCount++;
                bool seenCreate = _seenCreateObject.Contains(id);
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

    public void OnUpdateHealth(uint id, float healthRatio)
    {
        lock (_gate)
        {
        if (_playerId != 0 && id == _playerId)
            return; // ignore self

        int sid = (int)id;
        _healthRatios[sid] = Math.Clamp(healthRatio, 0f, 1f);

        if (!_creatures.Add(sid))
            return; // creature already known — ratio updated above, nothing more to do

        // DIAG: unconditional log for the FIRST time a dynamic uid lands in _creatures via
        // OnUpdateHealth. seenCreate=0 is the smoking gun for the suspected bug — the
        // engine's CreateObject hook never fired for this respawn, yet the server is
        // sending vitals for it, meaning the mob exists for AC but was invisible to the
        // plugin until something poked it (typically a user click → QueryHealth response).
        if (id >= 0x80000000u && _healthAddLogCount < MaxHealthAddDiagLines)
        {
            _healthAddLogCount++;
            bool wasSkipped = _reclassifySkipState.Remove(id);
            bool seenCreate = _seenCreateObject.Contains(id);
            bool inLandscape = _landscape.Contains(sid);
            bool inById = _byId.ContainsKey(sid);
            _host.Log($"[ReclassifyDiag] 0x{id:X8} HEALTHADD-RESCUE ratio={healthRatio:0.00} seenCreate={(seenCreate ? 1 : 0)} inLandscape={(inLandscape ? 1 : 0)} inById={(inById ? 1 : 0)} wasSkipped={(wasSkipped ? 1 : 0)}");
        }
        else if (_reclassifySkipState.ContainsKey(id))
        {
            // Past the log cap — still clean up skip state so the dict doesn't grow unbounded.
            _reclassifySkipState.Remove(id);
        }

        // DIAG: a creature first learned via server health update (the
        // "aggressive mob self-rescues" path) — NOT via TryClassify. Lets us
        // correlate a manually-selected mob's id to how/when it entered
        // _creatures vs. why classification missed it earlier.
        TraceClassify(id, true, true, false, 0u, $"healthAdd->creature hr={healthRatio:0.00}");

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
    private readonly Dictionary<int, int> _directInventoryIndex = new();
    private DateTime _lastDirectInventoryScan = DateTime.MinValue;
    private const int DirectInventoryCooldownMs = 1000;
    private const int DirectInventoryForceRefreshMinMs = 250;

    /// <summary>Mark inventory as changed — triggers a full container scan on next Tick.</summary>
    public void MarkInventoryDirty() => _inventoryDirty = true;

    /// <summary>Call from OnTick to classify queued objects.</summary>
    public void Tick()
    {
        int pending0;
        lock (_gate) pending0 = _pending.Count;

        int processed = 0;
        while (processed < MaxClassifyPerTick)
        {
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
        if ((DateTime.Now - _lastReclassifyTime).TotalSeconds >= ReclassifyIntervalSec)
        {
            _lastReclassifyTime = DateTime.Now;
            FlushSlowRetry();
            ReclassifyUnknownDynamics();
        }

        // Full container scan disabled — causes delayed crash when 160+ items in cache
        // trigger bulk property queries. MissileCraftingManager does its own lightweight scan.
        if (false && _inventoryDirty && _playerId != 0 && _host.HasGetContainerContents
            && (DateTime.Now - _loginTime).TotalMilliseconds >= 5000
            && (DateTime.Now - _lastScanAttempt).TotalMilliseconds >= ScanCooldownMs)
        {
            _lastScanAttempt = DateTime.Now;
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
    private int _classifyTrace;
    // Bumped from 500 while the missing-respawn investigation needs coverage of late
    // OnCreateObject events (login-burst alone blows the 500 cap in <2s, leaving the
    // session blind to anything that spawned later).
    private void TraceClassify(uint uid, bool hasName, bool hasPos, bool gotType, uint flags, string note)
    {
        if (_classifyTrace >= 5000 || uid < 0x80000000u) return;
        _classifyTrace++;
        int atk = -1;
        try { if (_host.HasObjectIsAttackable) atk = _host.ObjectIsAttackable(uid) ? 1 : 0; }
        catch { atk = -2; }
        _host.Log($"[ClassifyTrace] 0x{uid:X8} name={(hasName ? 1 : 0)} pos={(hasPos ? 1 : 0)} gotType={(gotType ? 1 : 0)} flags=0x{flags:X8} creature={((flags & ItemTypeCreature) != 0 ? 1 : 0)} atk={atk} {note}");
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
            if (uid >= 0x80000000u && _deleteWhilePendingSkipLogCount < MaxDeleteWhilePendingSkipLogLines)
            {
                _deleteWhilePendingSkipLogCount++;
                _host.Log($"[ReclassifyDiag] 0x{uid:X8} DELETE-WHILE-PENDING-SKIP");
            }
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
        bool looksLikeCorpse = hasPos && IsCorpseName(name);

        // Object not accessible yet — retry for any dynamic object (weenie may not be ready)
        if (!hasName && !hasPos)
        {
            TraceClassify(uid, hasName, hasPos, false, 0u, "noNamePos");
            if (uid >= 0x80000000u)
            {
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
                if (retries < MaxClassifyRetries)
                {
                    _classifyRetry[uid] = retries + 1;
                    _pending.Enqueue(uid);
                }
                else
                {
                    // Fast-retry burst exhausted (8 ticks ≈ 10 ms because Tick drains
                    // 30 pending per pass). Hand off to slow-retry: reset the fast
                    // counter so the next attempt gets a fresh 8 ticks, and park the
                    // uid in _slowRetry. FlushSlowRetry re-enqueues it every
                    // ReclassifyIntervalSec until the engine populates name/position.
                    _classifyRetry.Remove(uid);
                    _slowRetry.Add(uid);

                    if (_classifyGiveupLogCount < MaxClassifyGiveupLogLines)
                    {
                        _classifyGiveupLogCount++;
                        _host.Log($"[ReclassifyDiag] 0x{uid:X8} CLASSIFY-GIVEUP retries={retries} (name+pos unreadable across {MaxClassifyRetries} ticks; parked in slow-retry)");
                    }
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
            if (looksLikeCorpse)
            {
                cls = AcObjectClass.Corpse;
            }
            // Could be pack item, equipped item on 0x8000 range, or spawning dynamic object
            // Check item type flags first
            else if (_host.TryGetItemType(uid, out uint typeFlags))
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
                cls = ClassifyByItemType(typeFlags);
            }
            else
            {
                // No weenie/item-type accessible yet — retry for a few ticks
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
                TraceClassify(uid, hasName, hasPos, false, 0u, retries < MaxClassifyRetries ? $"nopos-retry r={retries}" : "nopos-giveup");
                if (retries < MaxClassifyRetries)
                {
                    _classifyRetry[uid] = retries + 1;
                    _pending.Enqueue(uid); // re-queue
                    return;
                }
                // Gave up retrying — fall through to name heuristics
                _classifyRetry.Remove(uid);
                cls = ClassifyInventoryItem(name);
            }

            if (cls == AcObjectClass.Corpse)
            {
                _creatures.Remove(id);
                _inventory.Remove(id);
                _landscape.Add(id);
            }
            else
            {
                _inventory.Add(id);
                _landscape.Remove(id);
            }
        }
        else
        {
            if (looksLikeCorpse)
            {
                _classifyRetry.Remove(uid);
                _creatures.Remove(id);
                _inventory.Remove(id);
                _landscape.Add(id);
                _byId[id] = Make(id, name, AcObjectClass.Corpse);
                return;
            }

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

                // Non-creature with a position — could be equipped item on 0x8000 range
                // or static world object; classify by type flags
                if ((typeFlags & (ItemTypeMeleeWeapon | ItemTypeMissileWeapon | ItemTypeCaster | ItemTypeArmor | ItemTypeContainer)) != 0)
                {
                    // Item with a world position = equipped or ground-dropped
                    cls = ClassifyByItemType(typeFlags);
                    _inventory.Add(id);
                    _landscape.Remove(id);
                    _byId[id] = Make(id, name, cls);
                    return;
                }
            }
            else if (uid >= 0x80000000u)
            {
                // TryGetItemType failed for a dynamic object with position — weenie may not be ready.
                // Re-queue for retry so creatures aren't permanently misclassified as Unknown landscape.
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
                if (retries < MaxClassifyRetries)
                {
                    _classifyRetry[uid] = retries + 1;
                    _pending.Enqueue(uid);
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
    private void ReclassifyUnknownDynamics()
    {
        lock (_gate)
        {
        List<int>? toPromote = null;
        List<int>? toCorpse  = null;
        foreach (int id in _landscape)
        {
            uint uid = unchecked((uint)id);
            if (uid < 0x80000000u) continue; // static objects never become creatures/corpses
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
            if ((wo.ObjectClass == AcObjectClass.Monster || wo.ObjectClass == AcObjectClass.Unknown)
                && _host.TryGetObjectName(uid, out string maybeCorpse)
                && IsCorpseName(maybeCorpse))
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
            bool isCreature = _host.TryGetItemType(uid, out uint typeFlags)
                              && (typeFlags & ItemTypeCreature) != 0;
            if (!isCreature
                && _host.HasObjectIsAttackable
                && _host.ObjectIsAttackable(uid))
                isCreature = true;
            if (!isCreature)
            {
                DiagLogReclassifySkip(uid, wo.Name);
                continue;
            }

            toPromote ??= new List<int>();
            toPromote.Add(id);
        }

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

        // DIAG: heartbeat — Unknown landscape candidates checked but nothing promoted
        // this pass. Confirms ReclassifyUnknownDynamics is running and engine signals
        // keep failing for the stuck uids (vs. them never reaching _landscape at all).
        if (toPromote == null
            && _reclassifySkipState.Count > 0
            && _reclassifyDiagSummaryCount < MaxReclassifyDiagSummaries)
        {
            _reclassifyDiagSummaryCount++;
            _host.Log($"[ReclassifyDiag] pass: {_reclassifySkipState.Count} stuck Unknown landscape candidate(s), 0 promoted");
        }

        if (toPromote == null) return;

        foreach (int id in toPromote)
        {
            uint uid = unchecked((uint)id);
            _host.TryGetObjectName(uid, out string name);
            _creatures.Add(id);
            _byId[id] = Make(id, name ?? string.Empty, AcObjectClass.Monster);
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
            bool looksLikeCorpse = hasPos && IsCorpseName(name);
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
                    if (uid >= 0x80000000u && _indexerRescueLogCount < MaxIndexerRescueDiagLines)
                    {
                        _indexerRescueLogCount++;
                        bool seenCreate = _seenCreateObject.Contains(uid);
                        _host.Log($"[ReclassifyDiag] 0x{uid:X8} INDEXER-RESCUE name='{name}' flags=0x{typeFlags:X8} seenCreate={(seenCreate ? 1 : 0)}");
                    }
                }
                else
                    cls = ClassifyByItemType(typeFlags);
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
    public int GetIntProperty(int id, uint stype, int defaultValue)
    {
        if (!_host.HasGetObjectIntProperty) return defaultValue;
        uint uid = unchecked((uint)id);
        return _host.TryGetObjectIntProperty(uid, stype, out int v) ? v : defaultValue;
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
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
        DateTime now = DateTime.Now;

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
        return FindPackFor(inv, playerId, includeMainPack, requireFree);
    }

    // Snapshot overload — caller supplies an already-built inventory list (AutoCram
    // reuses its single per-tick GetDirectInventory snapshot here; do NOT re-refresh).
    public static int FindPackFor(IReadOnlyList<WorldObject> inv, int playerId,
                                  bool includeMainPack, int requireFree)
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
                && p.Values(LongValueKey.EquippedSlots, 0) == 0)
                mainUsed++;
            if (p.ObjectClass != AcObjectClass.Container) continue;
            // Skip foci — technically containers but reserved for spell components.
            if (!string.IsNullOrEmpty(p.Name)
                && p.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0) continue;
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
        return mainFree > 0 ? playerId : 0;
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
            cls = ClassifyByItemType(flags);
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
            return (_byId.Count, _creatures.Count, _inventory.Count, _landscape.Count, _pending.Count);
    }

    // ── Factory ───────────────────────────────────────────────────────────

    private WorldObject Make(int id, string name, AcObjectClass cls)
    {
        var wo = new WorldObject(id, name, cls);
        wo.Cache = this;
        return wo;
    }

    // ── Type-flag item classification ─────────────────────────────────────

    private static AcObjectClass ClassifyByItemType(uint typeFlags)
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
                    "Flail", "Maul", "Glaive", "Halberd", "Pike", "Trident");

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
