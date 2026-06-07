using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// On-disk persistence for discovered dungeon hazard cells (lava / acid / fire pools).
///
/// Hazards are static world geometry — they never move and are identical for every
/// character — so the store is keyed by <b>landblock</b>, not by character, and lives in
/// one shared global folder. The first time a hotspot is sighted in a dungeon it is
/// written here; on every later visit the patrol builder seeds these cells before it
/// constructs the route, so the route avoids them from the very first waypoint instead of
/// having to discover and reroute around them again.
///
/// One file per landblock keeps multi-box writers in different dungeons from contending,
/// and <see cref="Append"/> does a read-merge-write union so two boxes exploring the SAME
/// dungeon both keep their discoveries. Format is one cell-id hex per line (comment-safe):
/// trivial to read, merge, and hand-edit.
/// </summary>
internal static class DungeonHazardStore
{
    // Sibling of the existing NavProfiles folder (see LegacyDashboardRenderer._navFolder).
    private static readonly string Folder = @"C:\Games\RynthSuite\RynthAi\DungeonHazards";

    private static string PathFor(uint landblockKey)
        => System.IO.Path.Combine(Folder, $"{landblockKey:X4}.txt");

    /// <summary>
    /// Returns the persisted hazard cell-ids for a landblock, or an empty set if none have
    /// been recorded (or the file is missing/unreadable). Never throws.
    /// </summary>
    public static HashSet<uint> Load(uint landblockKey)
    {
        var set = new HashSet<uint>();
        try
        {
            string path = PathFor(landblockKey);
            if (!File.Exists(path)) return set;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                // Accept optional 0x prefix.
                if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring(2);
                if (uint.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint cell))
                    set.Add(cell);
            }
        }
        catch { /* unreadable store — behave as if empty */ }
        return set;
    }

    /// <summary>Landblock keys that have a persisted hazard file, ascending. Never throws.</summary>
    public static List<uint> ListLandblocks()
    {
        var result = new List<uint>();
        try
        {
            if (!Directory.Exists(Folder)) return result;
            foreach (string path in Directory.GetFiles(Folder, "*.txt"))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (uint.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint lb))
                    result.Add(lb);
            }
            result.Sort();
        }
        catch { }
        return result;
    }

    /// <summary>Number of hazard cells recorded for a landblock (0 if none). Never throws.</summary>
    public static int Count(uint landblockKey) => Load(landblockKey).Count;

    /// <summary>Deletes the persisted hazards for one landblock. Never throws.</summary>
    public static void Clear(uint landblockKey)
    {
        try
        {
            string path = PathFor(landblockKey);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>Deletes every persisted-hazard file. Never throws.</summary>
    public static void ClearAll()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (string path in Directory.GetFiles(Folder, "*.txt"))
                File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// Records a newly-discovered hazard cell. Read-merge-writes so a concurrent box in the
    /// same dungeon does not clobber the other's discoveries. No-op (no rewrite) if the cell
    /// is already on disk. Never throws.
    /// </summary>
    public static void Append(uint landblockKey, uint cellId)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var existing = Load(landblockKey);
            if (!existing.Add(cellId)) return; // already persisted

            var lines = new List<string>(existing.Count);
            foreach (uint c in existing)
                lines.Add($"0x{c:X8}");
            // Atomic-ish: write a temp then move over the target so a crash mid-write
            // can't leave a half-written file.
            string path = PathFor(landblockKey);
            string tmp  = path + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
        }
        catch { /* best-effort persistence — a lost write just means we re-learn it */ }
    }
}
