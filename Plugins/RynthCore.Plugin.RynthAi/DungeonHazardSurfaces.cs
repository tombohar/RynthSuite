// Detector C — offline lava/acid hazard-cell detection from EnvCell floor textures.
//
// WHY: AC environmental hazards are server-side `HotSpot` weenies, and ~75/120 templates set
// PropertyBool.Visibility=true, so ACE never broadcasts them to the client (Player_Tracking.cs:58
// "object is meant for server side only"). A live object scan (WorldObjectCache.MaybeRegisterHazard,
// name-pattern on a *visible* object) therefore structurally cannot see the invisible majority.
// What IS always observable offline is the lava/acid FLOOR TEXTURE the dungeon author painted into
// the EnvCell, sitting in client_portal.dat. Detector C reads that texture directly and pre-emptively
// marks matching cells hazardous at patrol-build time — no first-hit, no broadcast, no visible object.
//
// FLOORS ONLY (2026-10-23): a cell is a hazard when a texture in HazardTextures.txt is on its
// walkable FLOOR — a render polygon facing up at least as steeply as AC's FloorZ (0.66417) — not
// merely somewhere in its surface palette. The palette also holds the walls' and ceilings' textures
// (and sometimes entries no polygon uses), so the palette test flagged cells for their walls: in
// Tiny Hive (0x0289) the learned 0x0500279D is the Olthoi hive WALL texture (on walls in 3,530
// cells across the dats, on a floor in none) and boxed the patrol in at the entrance.
// The floors come from DungeonLOS.GetCellFloorSurfaces (render polygons carry PosSurface, an
// index into the EnvCell's palette); learning (LearnFromCell) looks at floors too, so it can
// never learn a wall texture.
//
// RESOLUTION CHAIN (all verified):
//   EnvCell (cell.dat)  -> ushort[numSurfaces] palette; CellStruct render polygon PosSurface -> palette index
//      -> (0x08000000u | surfIdx)  -> Surface file (portal.dat 0x0800xxxx)
//           -> Surface.Type (uint @ OFFSET 0, NO leading-id skip)
//                if (Type & (Base1Image 0x2 | Base1ClipMap 0x4)) -> OrigTextureId (uint, next)  <-- HAZARD KEY
//                else: ColorValue (solid color) -> not a texture
//   (confirmed against AcTextures.cs:50-59 / Surface.cs:23 and real portal.dat bytes
//    file 0x08000000 = 04 00 00 00 | F2 25 00 05 -> Type=0x4, OrigTextureId=0x050025F2.)
//
// SCOPE: texture-only detection has its own blind spot — air/fog/cloud/mana-field and explicitly
// invisible-floor hotspots carry no distinctive floor texture. Detector C SUPPLEMENTS the live
// name-detector + reactive marking; it does not replace them. All three write the same idempotent
// WorldObjectCache._hazardCells set, so they compose.
//
// THREADING: pure DatDatabase.GetFileData reads — no live AC object access — so this adds no
// off-thread-AV / object-teardown risk. Runs on the patrol-build (pump) thread, cached per landblock.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi;

internal static class DungeonHazardSurfaces
{
    // Sibling of NavProfiles / DungeonHazards (LegacyDashboardRenderer._navFolder family).
    // Settable so the offline tests can point it at a scratch folder.
    internal static string Folder = RynthCore.Install.RynthInstallPaths.RynthAiDir;
    private static string SetPath => System.IO.Path.Combine(Folder, "HazardTextures.txt");

    private static readonly object _gate = new();

    // OrigTextureIds (0x05) the user/learner has flagged as lava/acid/fire floors. Lazy-loaded.
    private static HashSet<uint>? _hazardTextures;

    // surfIdx -> OrigTextureId (0 = solid color / missing). Surfaces are global across dungeons,
    // so a process-wide memo is correct and keeps per-landblock scans cheap.
    private static readonly Dictionary<ushort, uint> _surfTexCache = new();

    // Per-landblock computed hazard-cell set (recomputed when the texture set changes).
    private static uint           _cachedLandblock;
    private static HashSet<uint>? _cachedCells;

    // Per-landblock floor textures: cell -> OrigTextureIds on its walkable floor. Dat-only, so it
    // never goes stale; kept for one landblock (the patrol's).
    private static uint                                _floorTexLandblock;
    private static Dictionary<uint, HashSet<uint>>?    _floorTex;

    // ── Hazard-texture set (persistent, user-editable) ─────────────────────────

    private static HashSet<uint> Textures()
    {
        // caller holds _gate
        if (_hazardTextures != null) return _hazardTextures;
        var set = new HashSet<uint>();
        try
        {
            if (File.Exists(SetPath))
            {
                foreach (string raw in File.ReadAllLines(SetPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) line = line.Substring(2);
                    if (uint.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint t))
                        set.Add(t);
                }
            }
        }
        catch { /* unreadable set — behave as empty */ }
        _hazardTextures = set;
        return set;
    }

    public static bool IsHazardTexture(uint origTextureId)
    {
        if (origTextureId == 0) return false;
        lock (_gate) return Textures().Contains(origTextureId);
    }

    public static int HazardTextureCount { get { lock (_gate) return Textures().Count; } }

    public static List<uint> ListHazardTextures()
    {
        lock (_gate) { var l = new List<uint>(Textures()); l.Sort(); return l; }
    }

    /// <summary>Flags a texture id as hazardous, persists it, and invalidates the cell cache. True if newly added.</summary>
    public static bool AddHazardTexture(uint origTextureId)
    {
        if (origTextureId == 0) return false;
        lock (_gate)
        {
            if (!Textures().Add(origTextureId)) return false;
            Persist();
            _cachedCells = null; _cachedLandblock = 0;
            return true;
        }
    }

    /// <summary>Unflags a texture id, persists, and invalidates the cell cache. True if it was present.</summary>
    public static bool RemoveHazardTexture(uint origTextureId)
    {
        lock (_gate)
        {
            if (!Textures().Remove(origTextureId)) return false;
            Persist();
            _cachedCells = null; _cachedLandblock = 0;
            return true;
        }
    }

    private static void Persist()
    {
        // caller holds _gate
        try
        {
            Directory.CreateDirectory(Folder);
            var lines = new List<string>
            {
                "# RynthAi Detector C — hazard floor textures (0x05 OrigTextureId), one hex id per line.",
                "# Flag a lava/acid floor with: /ra hazard surfaces  then  /ra hazard learnhere (or texture add 0x<id>).",
                "# Only a texture on a cell's walkable floor marks it; the same texture on walls or ceilings does not.",
            };
            var sorted = new List<uint>(_hazardTextures!);
            sorted.Sort();
            foreach (uint t in sorted) lines.Add($"0x{t:X8}");

            string tmp = SetPath + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Copy(tmp, SetPath, overwrite: true); // best-effort; Load is defensive against a torn file
            File.Delete(tmp);
        }
        catch { /* best-effort persistence */ }
    }

    /// <summary>Drops the in-memory set + cell cache so the next query reloads from disk (after a hand-edit).</summary>
    public static void Reload()
    {
        lock (_gate) { _hazardTextures = null; _cachedCells = null; _cachedLandblock = 0; }
    }

    public static void InvalidateCells()
    {
        lock (_gate) { _cachedCells = null; _cachedLandblock = 0; }
    }

    // ── Surface resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a 16-bit EnvCell surface-palette index to its 0x05 OrigTextureId via portal.dat.
    /// Returns 0 for solid-color / missing / non-image surfaces. Memoized process-wide. Never throws.
    /// </summary>
    public static uint ResolveOrigTextureId(DatDatabase? portal, ushort surfIdx)
    {
        lock (_gate)
            if (_surfTexCache.TryGetValue(surfIdx, out uint cached)) return cached;

        uint tex = 0;
        try
        {
            byte[]? d = portal?.GetFileData(0x08000000u | surfIdx);
            if (d != null && d.Length >= 8)
            {
                // Surface (0x08) body begins at offset 0 with uint Type — NO leading file-id.
                uint type = BitConverter.ToUInt32(d, 0);
                if ((type & (0x2u | 0x4u)) != 0)        // Base1Image | Base1ClipMap -> has a texture
                    tex = BitConverter.ToUInt32(d, 4);  // OrigTextureId (0x05 SurfaceTexture id)
            }
        }
        catch { tex = 0; }

        lock (_gate) _surfTexCache[surfIdx] = tex;
        return tex;
    }

    /// <summary>Reads an EnvCell's surface palette → (surfIdx, OrigTextureId) pairs. For diagnostics. Never throws.</summary>
    public static List<(ushort surfIdx, uint texId)> GetCellSurfaces(DatDatabase? cellDat, DatDatabase? portal, uint cellId)
    {
        var result = new List<(ushort, uint)>();
        try
        {
            byte[]? data = cellDat?.GetFileData(cellId);
            if (data == null || data.Length < 16) return result;
            using var ms = new MemoryStream(data);
            using var r  = new BinaryReader(ms);
            r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); // id, flags, dupId
            byte numSurfaces = r.ReadByte();
            r.ReadByte();    // numPortals
            r.ReadUInt16();  // numVisibleCells
            for (int i = 0; i < numSurfaces; i++)
            {
                if (ms.Position + 2 > ms.Length) break;
                ushort s = r.ReadUInt16();
                result.Add((s, ResolveOrigTextureId(portal, s)));
            }
        }
        catch { }
        return result;
    }

    // ── Floor textures ─────────────────────────────────────────────────────────

    /// <summary>
    /// (Cached) cell → OrigTextureIds on its walkable floor, for every EnvCell of the landblock
    /// that has a textured floor (DungeonLOS.GetCellFloorSurfaces, resolved through portal.dat).
    /// Empty without geometry. Never throws.
    /// </summary>
    public static Dictionary<uint, HashSet<uint>> GetFloorTextures(uint landblockKey, DungeonLOS? los, DatDatabase? portal)
    {
        lock (_gate)
            if (_floorTex != null && _floorTexLandblock == landblockKey) return _floorTex;

        var map = new Dictionary<uint, HashSet<uint>>();
        try
        {
            if (los != null && portal != null)
            {
                foreach (var kv in los.GetCellFloorSurfaces(landblockKey))
                {
                    var set = new HashSet<uint>();
                    foreach (ushort s in kv.Value)
                    {
                        uint t = ResolveOrigTextureId(portal, s);
                        if (t != 0) set.Add(t);
                    }
                    if (set.Count > 0) map[kv.Key] = set;
                }
            }
        }
        catch { }

        // Nothing read (geometry not up yet): don't pin an empty answer for this landblock.
        if (map.Count > 0)
            lock (_gate) { _floorTex = map; _floorTexLandblock = landblockKey; }
        return map;
    }

    /// <summary>OrigTextureIds on one cell's walkable floor (empty when none). For diagnostics. Never throws.</summary>
    public static HashSet<uint> GetCellFloorTextures(uint cellId, DungeonLOS? los, DatDatabase? portal)
        => GetFloorTextures(cellId >> 16, los, portal).TryGetValue(cellId, out var set) ? set : new HashSet<uint>();

    // ── Per-landblock hazard-cell computation ──────────────────────────────────

    /// <summary>
    /// Returns (cached) the EnvCells in the landblock whose walkable floor shows a flagged hazard
    /// texture. Cheap no-op re-call for the same landblock. Pure dat reads. Never throws.
    /// </summary>
    public static HashSet<uint> ComputeHazardCells(uint landblockKey, DungeonLOS? los, DatDatabase? portal)
    {
        lock (_gate)
            if (_cachedCells != null && _cachedLandblock == landblockKey) return _cachedCells;

        var cells = new HashSet<uint>();
        bool settled = false;   // cache only a real answer, not "geometry not up yet"
        try
        {
            if (los != null && portal != null)
            {
                if (HazardTextureCount == 0) settled = true;
                else
                {
                    var floors = GetFloorTextures(landblockKey, los, portal);
                    cells = HazardCellsFrom(floors);
                    settled = floors.Count > 0;
                }
            }
        }
        catch { }

        if (settled)
            lock (_gate) { _cachedCells = cells; _cachedLandblock = landblockKey; }
        return cells;
    }

    /// <summary>The cells whose floor textures include a flagged hazard texture.</summary>
    internal static HashSet<uint> HazardCellsFrom(IReadOnlyDictionary<uint, HashSet<uint>> floorTexByCell)
    {
        var cells = new HashSet<uint>();
        foreach (var kv in floorTexByCell)
            foreach (uint t in kv.Value)
                if (IsHazardTexture(t)) { cells.Add(kv.Key); break; }
        return cells;
    }

    // ── Learn-here (precision-guarded auto-flag) ───────────────────────────────

    /// <summary>
    /// Stand in a hazard cell and call this: it flags the texture(s) on the cell's FLOOR that are
    /// RARE across the landblock's floors (the texture only lava/acid cells use), while rejecting
    /// the common floor textures. Wall and ceiling textures are never candidates. This is the safe
    /// auto-bootstrap — it avoids the catastrophic failure of learning a ubiquitous floor texture
    /// and banning the whole dungeon. Returns the newly-flagged ids.
    /// </summary>
    public static List<uint> LearnFromCell(uint cellId, DungeonLOS? los, DatDatabase? portal,
                                           double rareFractionMax = 0.25)
    {
        var learned = new List<uint>();
        try
        {
            if (los == null || portal == null) return learned;
            var floors = GetFloorTextures(cellId >> 16, los, portal);
            foreach (uint t in RareFloorTextures(cellId, floors, rareFractionMax))
                if (AddHazardTexture(t)) learned.Add(t);
        }
        catch { }
        return learned;
    }

    /// <summary>
    /// The textures on <paramref name="cellId"/>'s floor that are on the floor of at most
    /// <paramref name="rareFractionMax"/> of the landblock's cells with a textured floor.
    /// </summary>
    internal static List<uint> RareFloorTextures(uint cellId, IReadOnlyDictionary<uint, HashSet<uint>> floorTexByCell,
                                                 double rareFractionMax)
    {
        var rare = new List<uint>();
        if (!floorTexByCell.TryGetValue(cellId, out var here) || here.Count == 0) return rare;

        var freq = new Dictionary<uint, int>();
        foreach (var texs in floorTexByCell.Values)
            foreach (uint t in texs) freq[t] = freq.GetValueOrDefault(t) + 1;
        int totalCells = floorTexByCell.Count;
        if (totalCells == 0) return rare;

        foreach (uint t in here)
            if (freq.GetValueOrDefault(t) / (double)totalCells <= rareFractionMax) rare.Add(t);
        rare.Sort();
        return rare;
    }
}
