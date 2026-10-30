using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// The Town Network (NavData\townnet.json, made by RynthCore's tools\navdata\gen_townnet.py from the
// world database plus RynthNav.TownNet's walks from the dats): the "Portal to Town Network" in many
// towns, the arrival points inside, the exit portal to each town, and the walk from every arrival
// to every exit. RynthNav's route planner uses it as a hub (PortalRoute's HubLinks) and the walker
// follows the stored walks inside, where there is no navmesh. Read-only once loaded.
// No host calls: the offline tests compile this file.

internal sealed class TownNetPoint
{
    public uint Cell;
    public double X, Y, Z;
}

internal sealed class TownNetArrival
{
    public string Id = "";
    public uint Cell;
    public double X, Y, Z;
}

/// <summary>Restrictions the server checks before it lets a character through a portal.</summary>
internal sealed class TownNetRules
{
    public int MinLevel, MaxLevel, AccountRequirements;
    public string Quest = "";
    public bool Closed, NoOlthoi, OnlyOlthoi, AdvocateOnly, NoPk, NoPkLite, NoNpk, NoVitae, NoNewAccounts;

    /// <summary>
    /// Why this character can't use the portal ("" = it can). Level 0 = not known yet: a portal with a
    /// level rule is then left out, since it can't be shown to be open. Rules the plugin can't check
    /// (quests, player killer status, vitae) leave the portal out too.
    /// </summary>
    public string Blocks(int level, bool olthoi)
    {
        if (Closed) return "closed";
        if (AdvocateOnly) return "advocates only";
        if (Quest.Length > 0) return "needs a quest flag";
        if (olthoi && NoOlthoi) return "no Olthoi";
        if (!olthoi && OnlyOlthoi) return "Olthoi only";
        if (MinLevel > 0 || MaxLevel > 0)
        {
            if (level <= 0) return MinLevel > 0 ? $"needs level {MinLevel} (your level isn't known yet)" : $"level {MaxLevel} or lower (your level isn't known yet)";
            if (MinLevel > 0 && level < MinLevel) return $"needs level {MinLevel}";
            if (MaxLevel > 0 && level > MaxLevel) return $"level {MaxLevel} or lower";
        }
        if (NoPk || NoPkLite || NoNpk) return "player killer rules";
        if (NoVitae) return "no vitae";
        if (NoNewAccounts || AccountRequirements > 1) return "account rules";
        return "";
    }
}

internal sealed class TownNetEntry
{
    public string Guid = "", Name = "", Place = "", Town = "";
    public bool OnMap;
    public double Ns, Ew;
    public int Arrival = -1;
    public readonly TownNetRules Rules = new();
}

internal sealed class TownNetExit
{
    public string Guid = "", Name = "", Label = "", DestTown = "", DestPlace = "";
    public uint Cell;
    public double X, Y, Z;
    public bool DestOnMap;
    public double DestNs, DestEw;
    public uint DestCell;
    public readonly TownNetRules Rules = new();

    /// <summary>"Yaraq" for the Portal to Yaraq: the town or place it leads to.</summary>
    public string Where => Label.Length > 0 ? Label : DestTown.Length > 0 ? DestTown : Name;
    public uint DestLandblock => DestCell >> 16;
}

internal sealed class TownNetWalk
{
    public int Arrival, Exit;
    public double Length;
    /// <summary>The least room on the walk from the walker's centre to a wall or object (m); 0 = not given.</summary>
    public double MinClear;
    public TownNetPoint[] Points = Array.Empty<TownNetPoint>();
}

internal sealed class TownNet
{
    public const string FileName = "townnet.json";
    public const int SupportedVersion = 1;
    /// <summary>Landing within this of an arrival point counts as landing there (metres = yards here).</summary>
    public const double ArrivalNearUnits = 3.0;

    public int Version;
    public string SetId = "", Generated = "";
    /// <summary>The network's landblocks (0x0007 on our data).</summary>
    public readonly HashSet<uint> Landblocks = new();
    public readonly List<TownNetArrival> Arrivals = new();
    public readonly List<TownNetEntry> Entries = new();
    public readonly List<TownNetExit> Exits = new();
    public readonly List<TownNetWalk> Walks = new();
    private readonly Dictionary<(int, int), TownNetWalk> _walk = new();

    public bool Contains(uint landblock) => Landblocks.Contains(landblock);
    public TownNetWalk? Walk(int arrival, int exit) => _walk.TryGetValue((arrival, exit), out var w) ? w : null;

    public override string ToString() =>
        $"{Entries.Count} entry portals, {Exits.Count} exits, {Walks.Count} walks (set {SetId})";

    /// <summary>
    /// The first 16 hex digits of the SHA-256 of the text without its "setId" and "generated" lines,
    /// carriage returns dropped: the same id gen_townnet.py and RynthNav.TownNet write.
    /// </summary>
    public static string ComputeSetId(string text)
    {
        var kept = new List<string>();
        foreach (string l in text.Replace("\r", "").Split('\n'))
            if (!l.StartsWith("  \"setId\":", StringComparison.Ordinal) && !l.StartsWith("  \"generated\":", StringComparison.Ordinal))
                kept.Add(l);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", kept)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Reads townnet.json. Null, with <paramref name="why"/>, when it can't be used: another version,
    /// a set id that doesn't match the contents (edited or damaged), or no walks. Routing then works
    /// as if there were no file.
    /// </summary>
    public static TownNet? Parse(string text, out string why)
    {
        why = "";
        var t = new TownNet();
        using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
        JsonElement r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) { why = "not a townnet.json"; return null; }
        t.Version = Int(r, "version");
        if (t.Version != SupportedVersion) { why = $"version {t.Version}, this RynthNav reads version {SupportedVersion}"; return null; }
        t.SetId = Str(r, "setId");
        t.Generated = Str(r, "generated");
        string actual = ComputeSetId(text);
        if (t.SetId.Length == 0 || !t.SetId.Equals(actual, StringComparison.OrdinalIgnoreCase))
        { why = $"its set id ({(t.SetId.Length > 0 ? t.SetId : "none")}) doesn't match its contents ({actual}): edited or damaged"; return null; }

        if (r.TryGetProperty("landblocks", out var lbs) && lbs.ValueKind == JsonValueKind.Array)
            foreach (var e in lbs.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && uint.TryParse(e.GetString(), NumberStyles.HexNumber, null, out uint lb)) t.Landblocks.Add(lb);

        var arrivalIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in Arr(r, "arrivals"))
        {
            var a = new TownNetArrival { Id = Str(e, "id"), Cell = Hex(e, "cell"), X = Num(e, "x"), Y = Num(e, "y"), Z = Num(e, "z") };
            arrivalIndex[a.Id] = t.Arrivals.Count;
            t.Arrivals.Add(a);
            t.Landblocks.Add(a.Cell >> 16);
        }
        foreach (var e in Arr(r, "entries"))
        {
            var en = new TownNetEntry { Guid = Str(e, "guid"), Name = Str(e, "name"), Place = Str(e, "place") };
            en.OnMap = TryNum(e, "ns", out en.Ns) & TryNum(e, "ew", out en.Ew);
            if (e.TryGetProperty("town", out var town) && town.ValueKind == JsonValueKind.Object) en.Town = Str(town, "name");
            en.Arrival = arrivalIndex.TryGetValue(Str(e, "arrival"), out int ai) ? ai : -1;
            ReadRules(e, en.Rules);
            t.Entries.Add(en);
        }
        var exitIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in Arr(r, "exits"))
        {
            var x = new TownNetExit
            {
                Guid = Str(e, "guid"), Name = Str(e, "name"), Label = Str(e, "label"),
                Cell = Hex(e, "cell"), X = Num(e, "x"), Y = Num(e, "y"), Z = Num(e, "z"),
            };
            if (e.TryGetProperty("dest", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                x.DestCell = Hex(d, "cell");
                x.DestPlace = Str(d, "place");
                x.DestOnMap = TryNum(d, "ns", out x.DestNs) & TryNum(d, "ew", out x.DestEw);
                if (d.TryGetProperty("town", out var town) && town.ValueKind == JsonValueKind.Object) x.DestTown = Str(town, "name");
            }
            ReadRules(e, x.Rules);
            exitIndex[x.Guid] = t.Exits.Count;
            t.Exits.Add(x);
        }
        foreach (var e in Arr(r, "walks"))
        {
            if (!arrivalIndex.TryGetValue(Str(e, "from"), out int a) || !exitIndex.TryGetValue(Str(e, "to"), out int x)) continue;
            var pts = new List<TownNetPoint>();
            if (e.TryGetProperty("points", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3) continue;
                    var pt = new TownNetPoint
                    {
                        Cell = p[0].ValueKind == JsonValueKind.String && uint.TryParse(p[0].GetString(), NumberStyles.HexNumber, null, out uint c) ? c : 0,
                        X = p[1].GetDouble(), Y = p[2].GetDouble(), Z = p.GetArrayLength() > 3 ? p[3].GetDouble() : 0,
                    };
                    pts.Add(pt);
                }
            if (pts.Count < 2) continue;
            var w = new TownNetWalk { Arrival = a, Exit = x, Length = Num(e, "length"), MinClear = Num(e, "minClear"), Points = pts.ToArray() };
            if (w.Length <= 0) w.Length = PathLength(w.Points, 0);
            t.Walks.Add(w);
            t._walk[(a, x)] = w;
        }
        if (t.Arrivals.Count == 0 || t.Exits.Count == 0 || t.Walks.Count == 0) { why = "it has no arrivals, exits or walks"; return null; }
        return t;
    }

    private static void ReadRules(JsonElement e, TownNetRules r)
    {
        r.MinLevel = Int(e, "minLevel");
        r.MaxLevel = Int(e, "maxLevel");
        r.AccountRequirements = Int(e, "accountRequirements");
        r.Quest = Str(e, "quest");
        r.Closed = Bool(e, "closed");
        r.NoOlthoi = Bool(e, "noOlthoi");
        r.OnlyOlthoi = Bool(e, "onlyOlthoi");
        r.AdvocateOnly = Bool(e, "advocateOnly");
        r.NoPk = Bool(e, "noPk");
        r.NoPkLite = Bool(e, "noPkLite");
        r.NoNpk = Bool(e, "noNpk");
        r.NoVitae = Bool(e, "noVitae");
        r.NoNewAccounts = Bool(e, "noNewAccounts");
    }

    // ── The planner's view ───────────────────────────────────────────────────

    /// <summary>
    /// The network as the route planner sees it, for a character of <paramref name="level"/> (0 = not
    /// known) who is (or isn't) an Olthoi: the entry portals on the map, the exits that lead onto the
    /// map, and the walks inside. A portal the character can't use is left out, with a note. Entries
    /// and exits in dungeons have no map position and are left out too (silently: they never could
    /// be on a map route). <see cref="HubExit.Id"/> is the index into <see cref="Exits"/>.
    /// </summary>
    public HubLinks Links(int level, bool olthoi, List<string>? notes)
    {
        var hub = new HubLinks { Name = "Town Network", Arrivals = Arrivals.Count };
        int entriesBlocked = 0;
        string entryWhy = "";
        foreach (TownNetEntry e in Entries)
        {
            if (!e.OnMap || e.Arrival < 0) continue;
            string why = e.Rules.Blocks(level, olthoi);
            if (why.Length > 0) { entriesBlocked++; entryWhy = why; continue; }
            hub.Entries.Add(new HubEntry(e.Ns, e.Ew, e.Arrival, e.Name));
        }
        if (entriesBlocked > 0)
            notes?.Add($"Town Network: {entriesBlocked} entry portal(s) left out ({entryWhy}).");
        for (int x = 0; x < Exits.Count; x++)
        {
            TownNetExit ex = Exits[x];
            if (!ex.DestOnMap) continue;
            string why = ex.Rules.Blocks(level, olthoi);
            if (why.Length > 0) { notes?.Add($"Town Network: {ex.Name} left out ({why})."); continue; }
            var walk = new double[Arrivals.Count];
            bool any = false;
            for (int a = 0; a < Arrivals.Count; a++)
            {
                TownNetWalk? w = Walk(a, x);
                walk[a] = w != null ? w.Length : double.NaN;
                any |= w != null;
            }
            if (!any) continue;
            hub.Exits.Add(new HubExit(ex.DestNs, ex.DestEw, ex.Name, x, walk));
        }
        return hub;
    }

    // ── The walker's view (landblock coordinates inside the network) ─────────

    /// <summary>The arrival within <see cref="ArrivalNearUnits"/> of (x, y), or -1.</summary>
    public int ArrivalNear(double x, double y)
    {
        int best = -1;
        double bestD = ArrivalNearUnits;
        for (int i = 0; i < Arrivals.Count; i++)
        {
            double d = Math.Sqrt((Arrivals[i].X - x) * (Arrivals[i].X - x) + (Arrivals[i].Y - y) * (Arrivals[i].Y - y));
            if (d <= bestD) { best = i; bestD = d; }
        }
        return best;
    }

    /// <summary>
    /// The way from (x, y) to exit <paramref name="exit"/>: the walk from the arrival you stand on,
    /// else the walk (from any arrival) that passes nearest, joined at that point. The first point
    /// returned is where you stand. Null when no walk to that exit comes within
    /// <paramref name="maxOff"/> of you.
    /// </summary>
    public List<TownNetPoint>? WayTo(int exit, double x, double y, double maxOff, out string from)
    {
        from = "";
        int a = ArrivalNear(x, y);
        TownNetWalk? w = a >= 0 ? Walk(a, exit) : null;
        if (w != null)
        {
            from = Arrivals[a].Id;
            var list = new List<TownNetPoint> { new() { Cell = w.Points[0].Cell, X = x, Y = y, Z = w.Points[0].Z } };
            for (int i = 1; i < w.Points.Length; i++) list.Add(w.Points[i]);
            return list;
        }
        TownNetWalk? best = null;
        int bestSeg = -1;
        double bestD = maxOff;
        foreach (TownNetWalk cand in Walks)
        {
            if (cand.Exit != exit) continue;
            for (int i = 1; i < cand.Points.Length; i++)
            {
                double d = SegmentDistance(x, y, cand.Points[i - 1], cand.Points[i], out _);
                if (d <= bestD) { bestD = d; best = cand; bestSeg = i; }
            }
        }
        if (best == null) return null;
        from = "the nearest walk (from " + Arrivals[best.Arrival].Id + ")";
        var way = new List<TownNetPoint> { new() { Cell = best.Points[bestSeg - 1].Cell, X = x, Y = y, Z = best.Points[bestSeg - 1].Z } };
        for (int i = bestSeg; i < best.Points.Length; i++) way.Add(best.Points[i]);
        return way;
    }

    /// <summary>Distance from (x, y) to the segment a→b, and how far along it the nearest point is (0..1).</summary>
    public static double SegmentDistance(double x, double y, TownNetPoint a, TownNetPoint b, out double t)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        t = len2 < 1e-9 ? 1 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
        double px = a.X + dx * t - x, py = a.Y + dy * t - y;
        return Math.Sqrt(px * px + py * py);
    }

    /// <summary>Straight-segment length of <paramref name="pts"/> from index <paramref name="from"/> on.</summary>
    public static double PathLength(IReadOnlyList<TownNetPoint> pts, int from)
    {
        double len = 0;
        for (int i = Math.Max(1, from + 1); i < pts.Count; i++)
            len += Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X) + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));
        return len;
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object) yield return x;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Num(JsonElement e, string name) => TryNum(e, name, out double d) ? d : 0;

    private static bool TryNum(JsonElement e, string name, out double d)
    {
        d = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d);
    }

    private static uint Hex(JsonElement e, string name) =>
        uint.TryParse(Str(e, name), NumberStyles.HexNumber, null, out uint v) ? v : 0;
}

/// <summary>
/// Follows one stored walk inside the Town Network: straight segments, steering at the next waypoint
/// every tick; a waypoint counts once within 0.5 m or passed along its segment (never circled); more
/// than 2 m off the line steers back to it, more than 6 m stops. It holds short of the exit portal
/// until 4 s after landing (ACE refuses a portal within 3.5 s of a teleport), then walks into its
/// centre; standing there with no teleport it backs off and walks in once more. No 0.5 m of progress
/// in 3 s, or more than length ÷ 3 + 10 s (plus the hold) in all, stops with a reason. The teleport
/// itself is the caller's to notice. Host-free: the route tests drive it.
/// </summary>
internal sealed class HubWalker
{
    public const double WaypointUnits = 0.5;
    /// <summary>
    /// A corner is cut by about (radius × sin of the turn); the walks keep 0.15 m of spare room, so a
    /// sharp corner counts as reached closer in: radius = min(0.5, MaxCutUnits / sin(turn)).
    /// </summary>
    public const double MaxCutUnits = 0.1;
    public const double SteerBackUnits = 2.0;
    public const double OffRouteUnits = 6.0;
    /// <summary>Hold this far from the exit's centre (portal reach 1.36 m + body 0.48 m + room) during the cooldown.</summary>
    public const double HoldUnits = 2.6;
    public const double BackOffUnits = 3.0;
    public const double StuckProgress = 0.5;
    public const long CooldownMs = 4000;
    public const long StuckMs = 8000;   // 3 s stopped a walk that had only just landed (10-01 20:13: the client holds you a moment after portal space; a turn in place takes time too)
    public const long TouchWaitMs = 3000;

    public enum Act { Run, Hold, Stop, NoTeleport }

    private readonly List<TownNetPoint> _way;
    private readonly long _landMs;
    private int _idx = 1;
    private double _best = double.MaxValue;
    private long _bestMs, _atPortalMs;
    private bool _retried, _backing;
    private readonly double[] _radius;   // per waypoint: how close counts as reached (sharper turn, closer)

    public double Length { get; }
    public long LimitMs { get; }
    public bool Waiting { get; private set; }
    /// <summary>True on the tick the cooldown hold began (say so once).</summary>
    public bool JustStartedWaiting { get; private set; }
    /// <summary>Whole seconds of cooldown left when the hold began.</summary>
    public long WaitSeconds { get; private set; }
    public int Index => _idx;
    public int Count => _way.Count;

    /// <param name="way">From where you stand to the exit portal's centre (TownNet.WayTo).</param>
    /// <param name="landMs">When you landed in the network (the cooldown counts from it).</param>
    public HubWalker(List<TownNetPoint> way, long landMs, long nowMs)
    {
        if (way.Count < 2) throw new ArgumentException("a walk needs two points", nameof(way));
        _way = way;
        _landMs = landMs;
        _bestMs = nowMs;
        Length = TownNet.PathLength(way, 0);
        _radius = new double[way.Count];
        for (int i = 0; i < way.Count; i++)
        {
            _radius[i] = WaypointUnits;
            if (i == 0 || i == way.Count - 1) continue;
            double ax = way[i].X - way[i - 1].X, ay = way[i].Y - way[i - 1].Y, bx = way[i + 1].X - way[i].X, by = way[i + 1].Y - way[i].Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6) continue;
            double cos = (ax * bx + ay * by) / (la * lb);
            double sin = cos < 0 ? 1.0 : Math.Sqrt(Math.Max(0, 1 - cos * cos));   // past 90 degrees: as sharp as it gets
            if (sin > 1e-6) _radius[i] = Math.Min(WaypointUnits, Math.Max(0.05, MaxCutUnits / sin));
        }
        LimitMs = nowMs + (long)((Length / 3.0 + 10.0) * 1000) + CooldownMs;
    }

    /// <summary>Metres left to the exit's centre from (x, y).</summary>
    public double Left(double x, double y)
    {
        TownNetPoint t = _way[_idx];
        return Math.Sqrt((t.X - x) * (t.X - x) + (t.Y - y) * (t.Y - y)) + TownNet.PathLength(_way, _idx);
    }

    /// <summary>
    /// One tick at (x, y): Run with <paramref name="heading"/> (degrees, 0 = north, 90 = east), Hold
    /// (stand still), Stop (<paramref name="why"/>), or NoTeleport (stood on the exit twice and
    /// nothing happened).
    /// </summary>
    /// <summary>Not moving through no fault of the walk (portal space): the stuck clock starts again.</summary>
    public void Pause(long now) { _bestMs = now; _best = double.MaxValue; }

    public Act Step(double x, double y, long now, out double heading, out string why)
    {
        heading = 0; why = "";
        JustStartedWaiting = false;
        if (now > LimitMs) { why = "it took too long"; return Act.Stop; }
        int last = _way.Count - 1;

        while (_idx < last)
        {
            TownNetPoint b = _way[_idx];
            double db = Math.Sqrt((b.X - x) * (b.X - x) + (b.Y - y) * (b.Y - y));
            TownNet.SegmentDistance(x, y, _way[_idx - 1], b, out double along);
            if (db >= _radius[_idx] && along < 1.0) break;
            _idx++;
            _best = double.MaxValue; _bestMs = now;
        }
        TownNetPoint tgt = _way[_idx], prev = _way[_idx - 1];
        double off = TownNet.SegmentDistance(x, y, prev, tgt, out double tSeg);
        if (off > OffRouteUnits) { why = $"{off:F1} m off the way"; return Act.Stop; }
        double toExit = Math.Sqrt((_way[last].X - x) * (_way[last].X - x) + (_way[last].Y - y) * (_way[last].Y - y));

        // The portal cooldown: hold short of the exit until CooldownMs after landing.
        if (_idx == last && !_backing && now - _landMs < CooldownMs && toExit < HoldUnits)
        {
            if (!Waiting)
            {
                Waiting = true;
                JustStartedWaiting = true;
                WaitSeconds = (CooldownMs - (now - _landMs) + 999) / 1000;
            }
            _bestMs = now;   // waiting isn't being stuck
            return Act.Hold;
        }
        if (Waiting) { Waiting = false; _best = double.MaxValue; _bestMs = now; }

        // On the exit's centre with no teleport: back off and walk in again, once.
        if (_idx == last && !_backing && toExit < WaypointUnits)
        {
            if (_atPortalMs == 0) _atPortalMs = now;
            _bestMs = now;
            if (now - _atPortalMs < TouchWaitMs) return Act.Hold;
            if (_retried) return Act.NoTeleport;
            _retried = true; _backing = true; _atPortalMs = 0;
            _best = double.MaxValue;
        }

        double aimX = tgt.X, aimY = tgt.Y;
        if (_backing)
        {
            // A point BackOffUnits back along the last segment (or its start, if it's shorter).
            TownNetPoint a = _way[last - 1], e = _way[last];
            double sx = a.X - e.X, sy = a.Y - e.Y, sl = Math.Sqrt(sx * sx + sy * sy);
            double k = sl > BackOffUnits ? BackOffUnits / sl : 1.0;
            aimX = e.X + sx * k; aimY = e.Y + sy * k;
            if (Math.Sqrt((aimX - x) * (aimX - x) + (aimY - y) * (aimY - y)) < WaypointUnits)
            {
                _backing = false;
                _best = double.MaxValue; _bestMs = now;
                aimX = tgt.X; aimY = tgt.Y;
            }
        }
        else if (off > SteerBackUnits)
        {
            // Drifted off the line: steer back to it, a metre ahead of the nearest point.
            double sx = tgt.X - prev.X, sy = tgt.Y - prev.Y, sl = Math.Sqrt(sx * sx + sy * sy);
            double tt = sl > 1e-6 ? Math.Min(1.0, tSeg + 1.0 / sl) : 1.0;
            aimX = prev.X + sx * tt; aimY = prev.Y + sy * tt;
        }

        // Stuck: no StuckProgress closer to where we're heading in StuckMs.
        double dAim = Math.Sqrt((aimX - x) * (aimX - x) + (aimY - y) * (aimY - y));
        if (dAim < _best - StuckProgress) { _best = dAim; _bestMs = now; }
        else if (now - _bestMs > StuckMs) { why = $"stuck near {x:F1}, {y:F1}"; return Act.Stop; }

        heading = Math.Atan2(aimX - x, aimY - y) * 180.0 / Math.PI;
        if (heading < 0) heading += 360.0;
        return Act.Run;
    }
}
