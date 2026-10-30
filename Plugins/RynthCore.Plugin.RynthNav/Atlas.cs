using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RynthCore.Plugin.RynthNav;

// The Atlas: RynthNav's location database (NavData\locations.json, made from the world
// database by RynthCore's tools\navdata\gen_locations.py). Read-only; loaded once off the
// tick thread and then shared. The idea of a typed, searchable atlas with an arrow is
// GoArrow's (Digero 2006, Virindi 2011, MIT); the data is our own server's.
// No host calls: the offline tests compile this file.

internal sealed class AtlasLocation
{
    public int Id;
    public string Name = "", Type = "", Desc = "", Cell = "", Place = "", Lb = "";
    public uint Wcid;
    public bool OnMap;
    public double Ns, Ew;
    // A portal's destination.
    public bool HasDest, DestOnMap;
    public double DestNs, DestEw;
    public string DestName = "", DestPlace = "", DestCell = "";
    public int MinLevel, MaxLevel;
    public bool Quest, Closed;
}

/// <summary>A spell that lands at a fixed spot (from locations.json "recalls").</summary>
internal sealed class AtlasRecall
{
    public int SpellId;
    public string Name = "", In = "";
    public bool OnMap;
    public double Ns, Ew;
}

internal sealed class Atlas
{
    public static readonly Atlas Empty = new(new List<AtlasLocation>(), new List<AtlasRecall>(), "", "");

    public IReadOnlyList<AtlasLocation> Locations { get; }
    public IReadOnlyList<AtlasRecall> Recalls { get; }
    public string Generated { get; }
    public string Path { get; }

    /// <summary>
    /// The recalls' landing spots as places of type <see cref="RecallType"/> (2026-10-22), for
    /// finding a place by name (<see cref="Search"/>, <see cref="FindBest"/>): the world database
    /// names only the /telepoi towns, so "neft" found nothing while Lost City of Neftet Recall lands
    /// there. Kept out of <see cref="Locations"/>, which the route planner reads. The engine's
    /// Atlas panel lists the same places (RynthCore UI/Data/RynthNavAtlasRecalls.cs).
    /// </summary>
    public IReadOnlyList<AtlasLocation> RecallPlaces { get; }
    public const string RecallType = "Recall";

    private readonly Dictionary<int, AtlasLocation> _byId = new();

    private Atlas(List<AtlasLocation> locations, List<AtlasRecall> recalls, string generated, string path)
    {
        Locations = locations;
        Recalls = recalls;
        RecallPlaces = RecallPlacesOf(recalls);
        Generated = generated;
        Path = path;
        foreach (var l in locations) _byId[l.Id] = l;
    }

    private static List<AtlasLocation> RecallPlacesOf(List<AtlasRecall> recalls)
    {
        var places = new List<AtlasLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recalls)
        {
            string name = r.Name.Trim();
            // A spell listed twice for one spot (Ulgrim's Recall) is one place.
            string key = string.Create(CultureInfo.InvariantCulture, $"{name}|{r.OnMap}|{r.Ns:0.00}|{r.Ew:0.00}|{r.In}");
            if (name.Length == 0 || !seen.Add(key)) continue;
            places.Add(new AtlasLocation
            {
                Id = -r.SpellId,   // locations have positive ids
                Name = name, Type = RecallType, OnMap = r.OnMap, Ns = r.Ns, Ew = r.Ew,
                Place = r.OnMap ? "outdoor" : "dungeon",
                Desc = r.In.Length > 0 ? "in " + r.In : "",
            });
        }
        return places;
    }

    /// <summary>What a name search looks through: the locations, then the recall places.</summary>
    private IEnumerable<AtlasLocation> Searchable()
    {
        foreach (var l in Locations) yield return l;
        foreach (var l in RecallPlaces) yield return l;
    }

    public static Atlas Load(string path) => Parse(File.ReadAllText(path), path);

    public static Atlas Parse(string json, string path = "")
    {
        var locs = new List<AtlasLocation>();
        var recalls = new List<AtlasRecall>();
        string generated = "";
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("generated", out var g)) generated = g.GetString() ?? "";
        if (root.TryGetProperty("locations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in arr.EnumerateArray())
            {
                var l = new AtlasLocation
                {
                    Id = Int(e, "id"),
                    Name = Str(e, "name"),
                    Type = Str(e, "type"),
                    Desc = Str(e, "desc"),
                    Cell = Str(e, "cell"),
                    Place = Str(e, "place"),
                    Lb = Str(e, "lb"),
                    Wcid = (uint)Int(e, "wcid"),
                    MinLevel = Int(e, "minLevel"),
                    MaxLevel = Int(e, "maxLevel"),
                    Quest = Bool(e, "quest"),
                    Closed = Bool(e, "closed"),
                };
                l.OnMap = TryNum(e, "ns", out l.Ns) & TryNum(e, "ew", out l.Ew);
                if (e.TryGetProperty("dest", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    l.HasDest = true;
                    l.DestOnMap = TryNum(d, "ns", out l.DestNs) & TryNum(d, "ew", out l.DestEw);
                    l.DestName = Str(d, "name");
                    l.DestPlace = Str(d, "place");
                    l.DestCell = Str(d, "cell");
                }
                locs.Add(l);
            }
        }
        if (root.TryGetProperty("recalls", out arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in arr.EnumerateArray())
            {
                var r = new AtlasRecall { SpellId = Int(e, "spell"), Name = Str(e, "name"), In = Str(e, "in") };
                r.OnMap = TryNum(e, "ns", out r.Ns) & TryNum(e, "ew", out r.Ew);
                recalls.Add(r);
            }
        }
        return new Atlas(locs, recalls, generated, path);
    }

    public AtlasLocation? ById(int id) => _byId.TryGetValue(id, out var l) ? l : null;

    /// <summary>
    /// Landblocks that are dungeons: places (and portal destinations) the Atlas has in "dungeon"
    /// cells, less any landblock with places on the map. A dungeon's nav_XXXX.tile holds only that
    /// landblock's surface (a flat sheet; RynthNav.Baker leaves dungeon cells out), so it isn't
    /// a navmesh for walking inside the dungeon.
    /// </summary>
    public HashSet<uint> DungeonLandblocks()
    {
        var dungeon = new HashSet<uint>();
        var map = new HashSet<uint>();
        foreach (AtlasLocation l in Locations)
        {
            if (TryLandblock(l.Cell, out uint lb))
            {
                if (l.OnMap) map.Add(lb);
                else if (l.Place.Equals("dungeon", StringComparison.Ordinal)) dungeon.Add(lb);
            }
            if (l.HasDest && !l.DestOnMap && l.DestPlace.Equals("dungeon", StringComparison.Ordinal) && TryLandblock(l.DestCell, out uint dlb))
                dungeon.Add(dlb);
        }
        dungeon.ExceptWith(map);
        return dungeon;
    }

    private static bool TryLandblock(string cell, out uint lb)
    {
        lb = 0;
        return cell.Length == 8 && uint.TryParse(cell.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lb);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>
    /// "a || b || c": the first candidate that is coordinates, or that names a place on the map as a
    /// whole word or more (<see cref="MatchScore"/> 60+: "Mite Maze" yes, "Bur" inside "Burun" no),
    /// so a loose candidate never picks an unrelated place. Null when none does.
    /// </summary>
    public string? FirstOnMap(string alternatives, bool hasPos, double ns, double ew)
    {
        foreach (string raw in alternatives.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (NavCoords.TrySplitCoordsAndName(raw, out _, out _, out _)) return raw;
            AtlasLocation? best = FindBest(raw, hasPos, ns, ew);
            if (best != null && MatchScore(best.Name, raw) >= 60) return raw;
        }
        return null;
    }

    /// <summary>How well <paramref name="name"/> matches <paramref name="q"/> (lower case): 0 = not at all.</summary>
    public static int MatchScore(string name, string q)
    {
        if (q.Length == 0) return 1;
        if (name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 100;
        int at = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (at == 0) return 80;
        if (at > 0)
        {
            char before = name[at - 1];
            return char.IsLetterOrDigit(before) ? 40 : 60;   // starts a word / inside a word
        }
        // Every word of the query somewhere in the name ("mines despair").
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return 0;
        foreach (string w in words)
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return 0;
        return 30;
    }

    /// <summary>Type order when names match equally well: places before people.</summary>
    public static int TypeRank(string type) => type switch
    {
        "Town" => 0,
        "Dungeon" => 1,
        "Landmark" => 2,
        RecallType => 2,
        "Lifestone" => 3,
        "Bindstone" => 4,
        "Portal" => 5,
        "Vendor" => 6,
        "NPC" => 7,
        _ => 8,
    };

    /// <summary>
    /// The best atlas match for a name (/rnav arrow Holtburg): only places with a map
    /// position; best name match, then type (a town before its portal), then the
    /// nearest to the player when <paramref name="hasPos"/>.
    /// </summary>
    public AtlasLocation? FindBest(string query, bool hasPos, double ns, double ew)
    {
        string q = query.Trim();
        if (q.Length == 0) return null;
        AtlasLocation? best = null;
        int bestScore = 0, bestRank = int.MaxValue;
        double bestDist = double.MaxValue;
        foreach (var l in Searchable())
        {
            if (!l.OnMap) continue;
            int s = MatchScore(l.Name, q);
            if (s == 0) continue;
            int rank = TypeRank(l.Type);
            double dist = hasPos ? NavCoords.Distance(ns, ew, l.Ns, l.Ew) : 0;
            bool better = s > bestScore
                || (s == bestScore && rank < bestRank)
                || (s == bestScore && rank == bestRank && dist < bestDist);
            if (!better) continue;
            best = l; bestScore = s; bestRank = rank; bestDist = dist;
        }
        return best;
    }

    /// <summary>
    /// Locations matching <paramref name="query"/> (empty = all) and, when
    /// <paramref name="type"/> isn't empty, of that type; best matches first.
    /// </summary>
    public List<AtlasLocation> Search(string query, string? type, int max)
    {
        string q = query.Trim();
        var hits = new List<(AtlasLocation L, int S)>();
        foreach (var l in Searchable())
        {
            if (!string.IsNullOrEmpty(type) && !l.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
            int s = MatchScore(l.Name, q);
            if (s > 0) hits.Add((l, s));
        }
        hits.Sort((a, b) =>
        {
            int c = b.S.CompareTo(a.S);
            if (c != 0) return c;
            c = TypeRank(a.L.Type).CompareTo(TypeRank(b.L.Type));
            return c != 0 ? c : string.Compare(a.L.Name, b.L.Name, StringComparison.OrdinalIgnoreCase);
        });
        var result = new List<AtlasLocation>(Math.Min(max, hits.Count));
        for (int i = 0; i < hits.Count && result.Count < max; i++) result.Add(hits[i].L);
        return result;
    }

    /// <summary>The nearest on-map location of a type within <paramref name="maxUnits"/>, or null.</summary>
    public AtlasLocation? Nearest(string type, double ns, double ew, double maxUnits)
    {
        AtlasLocation? best = null;
        double bestD = maxUnits;
        foreach (var l in Locations)
        {
            if (!l.OnMap || !l.Type.Equals(type, StringComparison.Ordinal)) continue;
            double d = NavCoords.Distance(ns, ew, l.Ns, l.Ew);
            if (d <= bestD) { best = l; bestD = d; }
        }
        return best;
    }

    /// <summary>A portal by its weenie class, the one nearest the player when there are several.</summary>
    public AtlasLocation? PortalByWcid(uint wcid, double ns, double ew)
    {
        AtlasLocation? best = null;
        double bestD = double.MaxValue;
        foreach (var l in Locations)
        {
            if (l.Wcid != wcid || l.Type != "Portal") continue;
            double d = l.OnMap ? NavCoords.Distance(ns, ew, l.Ns, l.Ew) : double.MaxValue / 2;
            if (best == null || d < bestD) { best = l; bestD = d; }
        }
        return best;
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool TryNum(JsonElement e, string name, out double d)
    {
        d = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Locations.Count} locations, {Recalls.Count} recalls ({Generated})");
}
