// ============================================================================
//  RynthNet - Characters/AtlasNames.cs
//  What the Characters window needs from RynthNav's Atlas (NavData\locations.json,
//  the file RynthCore's own readers use): the landblocks with a place on the
//  surface map (their indoor cells are buildings in the overworld, not a
//  dungeon) and the dungeons' names by landblock. Read once, in the background,
//  the first time the window is drawn; until then (or with no Atlas) every
//  indoor landblock counts as a dungeon and is named by its number. Any thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthNet.Characters;

internal static class AtlasNames
{
    private sealed record Data(HashSet<uint> MapLandblocks, Dictionary<uint, string> Dungeons);

    private static volatile Data _data = new(new HashSet<uint>(), new Dictionary<uint, string>());
    private static int _started;

    /// <summary>Starts the one background read (later calls do nothing).</summary>
    public static void EnsureLoading(string path)
    {
        if (System.Threading.Interlocked.Exchange(ref _started, 1) != 0) return;
        Task.Run(() =>
        {
            try { if (File.Exists(path)) _data = Parse(File.ReadAllText(path)); }
            catch { }
        });
    }

    /// <summary>A surface building's landblock (a cell's top 16 bits).</summary>
    public static bool IsMapLandblock(uint lb) => _data.MapLandblocks.Contains(lb & 0xFFFF);

    /// <summary>The dungeon's name, "" when the Atlas doesn't name it.</summary>
    public static string DungeonName(uint lb) => _data.Dungeons.TryGetValue(lb & 0xFFFF, out string? n) ? n : "";

    /// <summary>"Matron Hive East" for a named dungeon, else "LB 6146".</summary>
    public static string DungeonLabel(uint lb)
    {
        string name = DungeonName(lb);
        return name.Length > 0 ? name : "LB " + (lb & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>The Atlas's surface landblocks and dungeon names (RynthCore's RynthNavAtlas rules). Pure.</summary>
    private static Data Parse(string json)
    {
        var lbs = new HashSet<uint>();
        var dungeons = new Dictionary<uint, string>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("locations", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string cell = Str(e, "cell"), type = Str(e, "type"), name = Str(e, "name"), lb = Str(e, "lb");
                bool onMap = e.TryGetProperty("ns", out JsonElement ns) && ns.ValueKind == JsonValueKind.Number
                             && e.TryGetProperty("ew", out JsonElement ew) && ew.ValueKind == JsonValueKind.Number;
                if (onMap && cell.Length == 8 && uint.TryParse(cell.AsSpan(0, 4), NumberStyles.HexNumber, null, out uint l))
                    lbs.Add(l);
                if (type == "Dungeon" && name.Length > 0 && uint.TryParse(lb, NumberStyles.HexNumber, null, out uint d))
                    dungeons.TryAdd(d, name);
            }
        return new Data(lbs, dungeons);
    }

    /// <summary>For tests: the Atlas from text, at once.</summary>
    internal static void LoadForTests(string json)
    {
        _data = Parse(json);
        _started = 1;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
