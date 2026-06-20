// Detector C — offline lava/acid hazard-cell detection from EnvCell surface textures.
//
// WHY: AC environmental hazards are server-side `HotSpot` weenies, and ~75/120 templates set
// PropertyBool.Visibility=true, so ACE never broadcasts them to the client (Player_Tracking.cs:58
// "object is meant for server side only"). A live object scan (WorldObjectCache.MaybeRegisterHazard,
// name-pattern on a *visible* object) therefore structurally cannot see the invisible majority.
// What IS always observable offline is the lava/acid FLOOR TEXTURE the dungeon author painted into
// the EnvCell, sitting in client_portal.dat. Detector C reads that texture directly and pre-emptively
// marks matching cells hazardous at patrol-build time — no first-hit, no broadcast, no visible object.
//
// RESOLUTION CHAIN (all verified):
//   EnvCell (cell.dat)  -> ushort[numSurfaces] palette
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

namespace RynthCore.Plugin.RynthAi;

internal static class DungeonHazardSurfaces
{
    // Sibling of NavProfiles / DungeonHazards (LegacyDashboardRenderer._navFolder family).
    private static readonly string Folder  = @"C:\Games\RynthSuite\RynthAi";
    private static readonly string SetPath = System.IO.Path.Combine(Folder, "HazardTextures.txt");

    private static readonly object _gate = new();

    // OrigTextureIds (0x05) the user/learner has flagged as lava/acid/fire floors. Lazy-loaded.
    private static HashSet<uint>? _hazardTextures;

    // surfIdx -> OrigTextureId (0 = solid color / missing). Surfaces are global across dungeons,
    // so a process-wide memo is correct and keeps per-landblock scans cheap.
    private static readonly Dictionary<ushort, uint> _surfTexCache = new();

    // Per-landblock computed hazard-cell set (recomputed when the texture set changes).
    private static uint           _cachedLandblock;
    private static HashSet<uint>? _cachedCells;

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

    /// <summary>Distinct non-zero OrigTextureIds present in an EnvCell's surface palette. Never throws.</summary>
    private static HashSet<uint> CellTextureSet(DatDatabase? cellDat, DatDatabase? portal, uint cellId)
    {
        var set = new HashSet<uint>();
        foreach (var (_, texId) in GetCellSurfaces(cellDat, portal, cellId))
            if (texId != 0) set.Add(texId);
        return set;
    }

    // ── Per-landblock hazard-cell computation ──────────────────────────────────

    /// <summary>
    /// Returns (cached) the EnvCells in the landblock whose surface palette contains a flagged
    /// hazard texture. Cheap no-op re-call for the same landblock. Pure dat reads. Never throws.
    /// </summary>
    public static HashSet<uint> ComputeHazardCells(uint landblockKey, DatDatabase? cellDat, DatDatabase? portal)
    {
        lock (_gate)
            if (_cachedCells != null && _cachedLandblock == landblockKey) return _cachedCells;

        var cells = new HashSet<uint>();
        try
        {
            if (cellDat != null && portal != null && HazardTextureCount > 0)
            {
                foreach (uint cellId in cellDat.GetLandblockCellIds(landblockKey))
                {
                    foreach (uint texId in CellTextureSet(cellDat, portal, cellId))
                    {
                        if (IsHazardTexture(texId)) { cells.Add(cellId); break; }
                    }
                }
            }
        }
        catch { }

        lock (_gate) { _cachedCells = cells; _cachedLandblock = landblockKey; }
        return cells;
    }

    // ── Learn-here (precision-guarded auto-flag) ───────────────────────────────

    /// <summary>
    /// Stand in a hazard cell and call this: it flags the cell's surface texture(s) that are RARE
    /// across the landblock (the texture only lava/acid cells use), while rejecting the common
    /// floor/wall textures. This is the safe auto-bootstrap — it avoids the catastrophic failure of
    /// learning a ubiquitous floor texture and banning the whole dungeon. Returns the newly-flagged ids.
    /// </summary>
    public static List<uint> LearnFromCell(uint cellId, DatDatabase? cellDat, DatDatabase? portal,
                                           double rareFractionMax = 0.25)
    {
        var learned = new List<uint>();
        try
        {
            if (cellDat == null || portal == null) return learned;
            uint landblockKey = cellId >> 16;

            var freq = new Dictionary<uint, int>();
            int totalCells = 0;
            foreach (uint cid in cellDat.GetLandblockCellIds(landblockKey))
            {
                var texs = CellTextureSet(cellDat, portal, cid);
                if (texs.Count == 0) continue;
                totalCells++;
                foreach (uint t in texs) freq[t] = freq.GetValueOrDefault(t) + 1;
            }
            if (totalCells == 0) return learned;

            foreach (uint t in CellTextureSet(cellDat, portal, cellId))
            {
                double frac = freq.GetValueOrDefault(t) / (double)totalCells;
                if (frac <= rareFractionMax && AddHazardTexture(t))
                    learned.Add(t);
            }
        }
        catch { }
        return learned;
    }
}
