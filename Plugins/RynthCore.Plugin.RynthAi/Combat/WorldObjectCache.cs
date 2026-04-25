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
/// All accesses are from the game thread (EndScene hook thread), so no locks are needed.
/// </summary>
public class WorldObjectCache
{
    private readonly RynthCoreHost _host;

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

    // Periodic reclassification — rescues Unknown landscape objects that become recognisable later
    private DateTime _lastReclassifyTime = DateTime.MinValue;
    private const double ReclassifyIntervalSec = 2.0;

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
        _playerId = playerId;
        _loginTime = DateTime.Now;
        // Remove self if mistakenly added as creature before login completed
        if (playerId == 0) return;
        int sid = (int)playerId;
        _creatures.Remove(sid);
        _landscape.Remove(sid);
        _byId.Remove(sid);
    }

    // ── Event handlers ────────────────────────────────────────────────────

    public void OnCreateObject(uint id)
    {
        _pending.Enqueue(id);
    }

    public void OnDeleteObject(uint id)
    {
        int sid = (int)id;
        bool wasInventory = _inventory.Remove(sid);
        _byId.Remove(sid);
        _creatures.Remove(sid);
        _landscape.Remove(sid);
        _healthRatios.Remove(sid);
        _classifyRetry.Remove(id);
        if (wasInventory)
            _inventoryDirty = true;
    }

    /// <summary>Returns the last known health ratio (0–1) for <paramref name="id"/>, or -1 if no update has been received.</summary>
    public float GetHealthRatio(int id) => _healthRatios.TryGetValue(id, out float v) ? v : -1f;

    public void OnUpdateHealth(uint id, float healthRatio)
    {
        if (_playerId != 0 && id == _playerId)
            return; // ignore self

        int sid = (int)id;
        _healthRatios[sid] = Math.Clamp(healthRatio, 0f, 1f);

        if (!_creatures.Add(sid))
            return; // creature already known — ratio updated above, nothing more to do

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
        int pending0 = _pending.Count;
        int processed = 0;
        while (_pending.Count > 0 && processed < MaxClassifyPerTick)
        {
            TryClassify(_pending.Dequeue());
            processed++;
        }
        if (processed > 0 && _tickDiagCount < 3)
        {
            _tickDiagCount++;
            _host.Log($"[RynthAi] Cache.Tick classified {processed} from {pending0} pending, total now {_byId.Count}");
        }

        // Periodically re-check Unknown landscape objects — dynamic creatures whose weenie
        // wasn't ready at classify time will be promoted to _creatures here.
        if ((DateTime.Now - _lastReclassifyTime).TotalSeconds >= ReclassifyIntervalSec)
        {
            _lastReclassifyTime = DateTime.Now;
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

    private void TryClassify(uint uid)
    {
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
            if (uid >= 0x80000000u)
            {
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
                if (retries < MaxClassifyRetries)
                {
                    _classifyRetry[uid] = retries + 1;
                    _pending.Enqueue(uid);
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
                    _creatures.Add(id);
                    _landscape.Add(id);
                    _byId[id] = Make(id, name, AcObjectClass.Monster);
                    return;
                }

                cls = ClassifyByItemType(typeFlags);
            }
            else
            {
                // No weenie/item-type accessible yet — retry for a few ticks
                int retries = _classifyRetry.TryGetValue(uid, out int r) ? r : 0;
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
            if (_host.TryGetItemType(uid, out uint typeFlags))
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

            cls = AcObjectClass.Unknown; // landscape non-creature (static object, portal, etc.)
            _landscape.Add(id);
        }

        _classifyRetry.Remove(uid);
        _byId[id] = Make(id, name, cls);
    }

    /// <summary>
    /// Scan landscape objects classified as Unknown with dynamic GUIDs and try to promote
    /// any that are now recognised as TYPE_CREATURE. Runs every ~2 seconds from Tick().
    /// This rescues creatures whose weenie data wasn't available when they were first classified.
    /// </summary>
    private void ReclassifyUnknownDynamics()
    {
        List<int>? toPromote = null;
        foreach (int id in _landscape)
        {
            if (_creatures.Contains(id)) continue;
            uint uid = unchecked((uint)id);
            if (uid < 0x80000000u) continue; // static objects never become creatures
            if (!_byId.TryGetValue(id, out var wo) || wo.ObjectClass != AcObjectClass.Unknown) continue;
            if (!_host.TryGetItemType(uid, out uint typeFlags)) continue;
            if ((typeFlags & ItemTypeCreature) == 0) continue;

            toPromote ??= new List<int>();
            toPromote.Add(id);
        }

        if (toPromote == null) return;

        foreach (int id in toPromote)
        {
            uint uid = unchecked((uint)id);
            _host.TryGetObjectName(uid, out string name);
            _creatures.Add(id);
            _byId[id] = Make(id, name ?? string.Empty, AcObjectClass.Monster);
        }

        _host.Log($"[RynthAi] ReclassifyUnknownDynamics: promoted {toPromote.Count} object(s) to Creature");
    }

    // ── WorldFilter API ───────────────────────────────────────────────────

    public WorldObject? this[int id]
    {
        get
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
            }
            else
            {
                _inventory.Add(id);
            }
            return obj;
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
        foreach (int id in _inventory)
        {
            if (_byId.TryGetValue(id, out var wo))
                yield return wo;
        }
    }

    /// <summary>
    /// Every object the cache currently knows about, indexed by id. Useful for
    /// wielded-item lookups when the `_inventory` set hasn't picked them up
    /// yet (the cache classifies items asynchronously and the wielderInfo probe
    /// can race with consumers like HasWieldedAmmo).
    /// </summary>
    public IEnumerable<WorldObject> AllKnownObjects() => _byId.Values;

    /// <summary>
    /// Lightweight live inventory snapshot built from GetContainerContents.
    /// This bypasses the crash-prone full cache scan and is the source of truth
    /// for crafting and ammo diagnostics.
    /// </summary>
    public IReadOnlyList<WorldObject> GetDirectInventory(bool forceRefresh = false)
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
            return _directInventory;
        }

        double scanAgeMs = (now - _lastDirectInventoryScan).TotalMilliseconds;
        if (_directInventory.Count > 0)
        {
            if (!forceRefresh && scanAgeMs < DirectInventoryCooldownMs)
                return _directInventory;

            if (forceRefresh && scanAgeMs < DirectInventoryForceRefreshMinMs)
                return _directInventory;
        }

        _lastDirectInventoryScan = now;
        _directInventory.Clear();
        _directInventoryIds.Clear();
        _directInventoryIndex.Clear();

        uint playerId = _host.GetPlayerId();
        if (playerId == 0)
            return _directInventory;

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
                if (!TryAddDirectInventoryItem(itemId, playerId, out bool isContainer))
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

        return _directInventory;
    }

    public IEnumerable<WorldObject> GetContainedItems(int containerId)
    {
        if (containerId == 0)
            yield break;

        foreach (int id in _inventory)
        {
            if (!_byId.TryGetValue(id, out var wo))
                continue;
            if (GetContainerId(id) != containerId)
                continue;
            yield return wo;
        }

        // Quest items and items with dynamic (0x80000000+) GUIDs are classified
        // as landscape rather than inventory. Check landscape too so they appear
        // as corpse contents when an open corpse is scanned.
        foreach (int id in _landscape)
        {
            if (_inventory.Contains(id)) continue; // already yielded above
            if (_creatures.Contains(id)) continue; // it's a live creature, not a container item
            if (!_byId.TryGetValue(id, out var wo)) continue;
            if (GetContainerId(id) != containerId) continue;
            yield return wo;
        }
    }

    /// <summary>Enumerate landscape creatures (received health updates or TYPE_CREATURE).</summary>
    public IEnumerable<WorldObject> GetLandscape()
    {
        foreach (int id in _creatures)
        {
            if (_byId.TryGetValue(id, out var wo))
                yield return wo;
        }
    }

    /// <summary>Enumerate all world objects with a valid landscape position, including corpses.</summary>
    public IEnumerable<WorldObject> GetLandscapeObjects()
    {
        foreach (int id in _landscape)
        {
            if (_byId.TryGetValue(id, out var wo))
                yield return wo;
        }
    }

    /// <summary>
    /// Discover all inventory items via native container scan.
    /// Finds items whose CreateObject events fired before the plugin loaded.
    /// Call once at login after hooks are ready.
    /// </summary>
    public int ScanFullInventory()
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

    private int EnsureInCache(uint uid)
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

    private bool TryAddDirectInventoryItem(uint uid, uint playerId, out bool isContainer)
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
        => (_byId.Count, _creatures.Count, _inventory.Count, _landscape.Count, _pending.Count);

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
