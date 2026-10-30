// ============================================================================
//  RynthDefective - Corpses/PlaceNames.cs
//  What the Corpses window needs from RynthNav's Atlas (<CoreDir>\NavData\
//  locations.json, the file RynthCore's own readers and RynthNet's Characters
//  window use): the dungeons' names and entrances by landblock, and the
//  landblocks with a place on the surface map (their indoor cells are buildings
//  in the overworld, not a dungeon). Read once, in the background, the first
//  time it's asked; until then (or with no Atlas) a dungeon is named by its
//  landblock number. Any thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthDefective.Corpses;

internal static class PlaceNames
{
    internal sealed record Dungeon(string Name, double Ns, double Ew, bool HasEntrance);

    private sealed record Data(HashSet<uint> MapLandblocks, Dictionary<uint, Dungeon> Dungeons);

    private static volatile Data _data = new(new HashSet<uint>(), new Dictionary<uint, Dungeon>());
    private static int _started;

    /// <summary>Starts the one background read (later calls do nothing).</summary>
    public static void EnsureLoading()
    {
        if (System.Threading.Interlocked.Exchange(ref _started, 1) != 0) return;
        string path = Path.Combine(RynthCore.Install.RynthInstallPaths.CoreDir, "NavData", "locations.json");
        Task.Run(() =>
        {
            try { if (File.Exists(path)) _data = Parse(File.ReadAllText(path)); }
            catch { }
        });
    }

    /// <summary>A surface building's landblock (a cell's top 16 bits).</summary>
    public static bool IsMapLandblock(uint landblock) => _data.MapLandblocks.Contains(landblock & 0xFFFF);

    /// <summary>The dungeon in this landblock, or null when the Atlas doesn't name it.</summary>
    public static Dungeon? DungeonAt(uint landblock) => _data.Dungeons.TryGetValue(landblock & 0xFFFF, out Dungeon? d) ? d : null;

    /// <summary>For tests: the Atlas from text, at once.</summary>
    internal static void LoadForTests(string json)
    {
        _data = Parse(json);
        _started = 1;
    }

    /// <summary>The Atlas's surface landblocks and dungeons (RynthCore's RynthNavAtlas rules, as RynthNet reads them). Pure.</summary>
    private static Data Parse(string json)
    {
        var lbs = new HashSet<uint>();
        var dungeons = new Dictionary<uint, Dungeon>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("locations", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string cell = Str(e, "cell"), type = Str(e, "type"), name = Str(e, "name"), lb = Str(e, "lb");
                bool onMap = TryNum(e, "ns", out double ns) & TryNum(e, "ew", out double ew);
                if (onMap && cell.Length == 8 && uint.TryParse(cell.AsSpan(0, 4), NumberStyles.HexNumber, null, out uint l))
                    lbs.Add(l);
                if (type == "Dungeon" && name.Length > 0 && uint.TryParse(lb, NumberStyles.HexNumber, null, out uint d))
                    dungeons.TryAdd(d, new Dungeon(name, ns, ew, onMap));
            }
        return new Data(lbs, dungeons);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool TryNum(JsonElement e, string name, out double value)
    {
        bool ok = e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number;
        value = ok ? v.GetDouble() : 0;
        return ok;
    }
}
