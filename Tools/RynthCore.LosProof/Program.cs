using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.LosProof;

// Offline proof for RynthAi's LOS check. Loads the real dat files (read only) through the
// plugin's own GeometryLoader and asks TargetingFSM.IsPathBlocked - IsTargetBlocked minus the
// client position reads - about a player and a monster in dungeon landblock 0x6346 (the Olthoi
// hive Lucy hunts).
//
// The positions and the ground truth (does the straight line cross a solid dungeon polygon?)
// live in pairs-6346.txt, so a build with the old geometry loader is judged against the same
// walls as the new one. The file was made from the fixed loader, whose render polygons cover
// all 625 EnvCells; delete it and run again to rebuild it.
//
// Run: dotnet run -c Release [-- <AC folder>] [-- --sweep]    exit 0 = pass, 1 = fail
internal static class Program
{
    private static GeometryLoader _geo = null!;
    private static TargetingFSM _fsm = null!;
    private static int _fails;
    private static readonly Vector3 Chest = new Vector3(0, 0, 1.0f);
    private const uint Lb = 0x6346;

    private static int Main(string[] args)
    {
        string acDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? @"C:\Turbine\Asheron's Call";
        _geo = new GeometryLoader();
        if (!_geo.Initialize(acDir))
        {
            Console.WriteLine("Geometry loader failed: " + _geo.StatusMessage);
            return 1;
        }
        _fsm = new TargetingFSM(_geo, new BlacklistManager());
        Console.WriteLine("Dats: " + _geo.StatusMessage);

        if (args.Contains("--sweep"))
            return Sweep();

        var cells = LoadCells(Lb);
        var geometry = _geo.GetLandblockGeometry((Lb << 16) | 0x0100);
        var polys = _geo.DungeonLOS.GetDungeonMapPolygons(Lb);
        var withWalls = polys.Where(p => !p.IsPortal).Select(p => p.CellId).ToHashSet();
        Console.WriteLine($"Landblock 0x{Lb:X4}: {cells.Count} EnvCells ({cells.Values.Count(c => c.Struct == 0)} use CellStruct 0, {cells.Values.Count(c => c.Struct != 0)} use CellStruct 1+)");
        Console.WriteLine($"  cells with wall geometry: {withWalls.Count} of {cells.Count} " +
                          $"(CellStruct 1+ cells with walls: {cells.Values.Count(c => c.Struct != 0 && withWalls.Contains(c.Id))}); LOS volumes {geometry.Count}");

        string fixture = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "pairs-6346.txt");
        fixture = Path.GetFullPath(fixture);
        List<Pair> pairs;
        if (File.Exists(fixture))
        {
            pairs = File.ReadAllLines(fixture).Where(l => l.Length > 0 && l[0] != '#').Select(Pair.Parse).ToList();
            Console.WriteLine($"  pairs: {pairs.Count} from {Path.GetFileName(fixture)}");
        }
        else
        {
            pairs = BuildPairs(cells, polys);
            File.WriteAllLines(fixture, new[]
            {
                "# RynthCore.LosProof ground truth for landblock 0x6346, built from the fixed geometry loader.",
                "# kind playerCell monsterCell ax ay az bx by bz wallBetween  (feet positions, global meters; wallBetween = the chest-height",
                "# line crosses a solid render polygon). W wall pair, H both hugging that wall, O open pair,",
                "# M monster 1.5 m away in the same cell, L Lucy's 2026-09-30 07:19 stall (0x63460132 -> 0x63460118, 3.7-4.9 m),",
                "# D in view through a doorway at an angle (0x6346012E -> 0x63460118, 5.0-5.4 m),",
                "# R up or down a ramp or step (0.5-3 m of height, 2-10 m apart, nothing between)."
            }.Concat(pairs.Select(p => p.ToString())));
            Console.WriteLine($"  pairs: {pairs.Count} built and written to {fixture}");
        }

        // Builds before the melee type existed tested melee as Linear.
        var melee = Enum.TryParse("Melee", out TargetingFSM.AttackType m) ? m : TargetingFSM.AttackType.Linear;
        Console.WriteLine($"\n=== Melee (Lucy): AttackType.{melee} ===");
        Judge(pairs, melee, openLimitPct: 1.0);
        Console.WriteLine("\n=== Spells, peace mode: AttackType.Linear (five silhouette rays in dungeons) ===");
        Judge(pairs, TargetingFSM.AttackType.Linear, openLimitPct: -1);

        // Arc spells (2026-10-04): the arc ACE flies (40 m/s across the ground, from the head).
        MagicArcShortPairs(pairs);
        MagicArcLongLines(polys);
        MagicArcOutdoors();

        var ex = pairs.Where(p => p.Kind == 'W' && cells.TryGetValue(p.Cell, out var ca) && ca.Struct != 0)
                      .OrderBy(p => (p.B - p.A).Length()).FirstOrDefault();
        if (ex != null)
        {
            uint other = ex.CellB;
            Console.WriteLine($"\nExample (a wall of a CellStruct 1+ cell): player in 0x{ex.Cell:X8} (CellStruct {cells[ex.Cell].Struct}) at " +
                              $"({ex.A.X:F2}, {ex.A.Y:F2}, {ex.A.Z:F2}), monster at ({ex.B.X:F2}, {ex.B.Y:F2}, {ex.B.Z:F2}) " +
                              $"in 0x{other:X8} (CellStruct {cells[other].Struct}), {(ex.B - ex.A).Length():F1} m apart, a wall between.");
            Console.WriteLine($"   melee verdict: {(_fsm.IsPathBlocked(ex.Cell, ex.A, ex.B, melee, out _) ? "BLOCKED" : "clear")}");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "PASS" : $"{_fails} FAILURE(S)");
        return _fails == 0 ? 0 : 1;
    }

    private static void Judge(List<Pair> pairs, TargetingFSM.AttackType type, double openLimitPct)
    {
        bool Blocked(Pair p) => _fsm.IsPathBlocked(p.Cell, p.A, p.B, type, out _);

        void Row(char kind, string what, bool expectBlocked, double limitPct)
        {
            var set = pairs.Where(p => p.Kind == kind).ToList();
            int blocked = set.Count(Blocked);
            Console.WriteLine($"  {what}: blocked {blocked} / {set.Count}");
            if (limitPct < 0) return;
            if (expectBlocked) Expect(set.Count > 0 && blocked == set.Count, $"{what}: every one blocked");
            else Expect(set.Count > 0 && blocked * 100.0 <= set.Count * limitPct, $"{what}: at most {limitPct}% blocked");
        }

        Row('W', "1. opposite sides of a wall, standing mid-cell ", true, 0);
        Row('H', "2. opposite sides of a wall, both 0.45 m off it", true, 0);
        Row('L', "3. Lucy's stall 09-30 07:19, 0132 -> 0118      ", true, 0);
        Row('O', "4. open line, same room or corridor            ", false, openLimitPct);
        Row('M', "5. monster 1.5 m away, same cell, nothing between", false, openLimitPct < 0 ? -1 : 0);
        Row('D', "6. in view through a doorway at an angle     ", false, openLimitPct < 0 ? -1 : 10);
        Row('R', "7. up or down a ramp or step, nothing between", false, openLimitPct);
    }

    // ── Arc spells ───────────────────────────────────────────────────────────────

    private static readonly Vector3 Head = new Vector3(0, 0, 1.0f + TargetingFSM.MagicArcLaunchAboveChest);

    /// <summary>Short shots (the fixture's open, wall and same-cell pairs, all under 15 m): an
    /// arc rises under 0.15 m there, so its verdict is the straight line's.</summary>
    private static void MagicArcShortPairs(List<Pair> pairs)
    {
        Console.WriteLine($"\n=== Arc spells, short shots: IsMagicArcPathBlocked vs the straight line (v={_fsm.MagicArcVelocity:0}) ===");
        foreach (char kind in new[] { 'O', 'M', 'W' })
        {
            var set = pairs.Where(p => p.Kind == kind).ToList();
            int arc = set.Count(p => _fsm.IsMagicArcPathBlocked(p.Cell, p.A, p.B, out _));
            int line = set.Count(p => _fsm.IsPathBlocked(p.Cell, p.A, p.B, TargetingFSM.AttackType.Linear, out _));
            Console.WriteLine($"  {kind}: arc blocked {arc} / {set.Count}, straight line {line}");
            Expect(set.Count > 0 && arc == line, $"{kind}: short arcs give the straight line's verdict");
        }
    }

    /// <summary>
    /// Long open lines in the hive (15-70 m, the chest-height line crosses nothing): the arc's
    /// verdict against an independent ground truth, the arc sampled every 0.25 m against the
    /// render polygons (no clearance). An arc the polygons say hits something must be blocked
    /// (no arc into a ceiling); the clearance makes the plugin a little stricter than that.
    /// </summary>
    private static void MagicArcLongLines(List<DungeonLOS.MapPolygon> polys)
    {
        Console.WriteLine($"\n=== Arc spells, long open lines in 0x{Lb:X4} (clearance {_fsm.MissileArcClearance:0.0} m) ===");
        var solid = polys.Where(p => !p.IsPortal).Select(Tris).ToList();
        var spots = FloorSpots().Where(kv => Clear(kv.Value, polys)).OrderBy(kv => kv.Key).ToList();
        var candidates = new List<(uint Cell, Vector3 A, Vector3 B, float D)>();
        for (int i = 0; i < spots.Count; i++)
            for (int j = i + 1; j < spots.Count; j++)
            {
                Vector3 a = spots[i].Value, b = spots[j].Value;
                float d = Flat(a, b);
                if (d < 15f || d > 70f || Math.Abs(a.Z - b.Z) > 3f) continue;
                if (Hits(a + Chest, b + Chest, solid, 0.02f, 0.02f)) continue;
                candidates.Add((spots[i].Key, a, b, d));
            }
        Console.WriteLine($"  open lines 15-70 m between standing spots: {candidates.Count}");
        if (candidates.Count == 0) { Expect(false, "found long open lines to test"); return; }

        // Each verdict has two parts: the straight line (five silhouette rays in dungeons, which
        // also clip corridor walls on long lines; target selection runs the same test) and the
        // arc. The arc part is judged against the polygons with the same clearance bump.
        float clr = _fsm.MissileArcClearance;
        int gt0 = 0, lineBlocked = 0, arcTested = 0, arcBlocked = 0, gtArc = 0, missed = 0, missedNoClr = 0, stricter = 0, ceiling = 0;
        (float D, float Hit, float Ceil)? example = null;
        float longestClear = 0f;
        foreach (var c in candidates)
        {
            bool gtNoClr = ArcHitsPolygons(c.A, c.B, solid, 0f);
            bool fsm = _fsm.IsMagicArcPathBlocked(c.Cell, c.A, c.B, out var det);
            if (gtNoClr) gt0++;
            if (gtNoClr && !fsm) missedNoClr++;
            if (!fsm) longestClear = Math.Max(longestClear, c.D);
            if (det.LineBlocked) { lineBlocked++; continue; }
            arcTested++;
            bool gt = ArcHitsPolygons(c.A, c.B, solid, clr);
            bool arc = fsm;
            if (gt) gtArc++;
            if (arc) arcBlocked++;
            if (gt && !arc) missed++;
            if (arc && !gt) stricter++;
            if (arc && det.Arc.Blocked && det.Arc.HitZ > 0f)
            {
                ceiling++;
                float ceil = det.Arc.HitZ + Head.Z;   // the hit, above the floor the caster stands on
                if (example == null || c.D < example.Value.D) example = (c.D, det.Arc.HitAlong, ceil);
            }
        }
        Console.WriteLine($"  straight line (five rays) blocked: {lineBlocked}; the arc tested on the other {arcTested}");
        Console.WriteLine($"  arc: plugin blocked {arcBlocked} ({ceiling} by a ceiling, above the launch point); polygons with the same " +
                          $"{clr:0.0} m clearance: {gtArc}; missed {missed}, stricter {stricter}");
        Console.WriteLine($"  polygons with no clearance: {gt0} arcs hit something, every one blocked by the plugin: {(missedNoClr == 0 ? "yes" : $"NO ({missedNoClr} missed)")}");
        if (example != null)
            Console.WriteLine($"  e.g. a {example.Value.D:0.0} m shot: the arc (plus clearance) meets the ceiling {example.Value.Hit:0.0} m out, " +
                              $"{example.Value.Ceil:0.0} m above the caster's feet");
        Console.WriteLine($"  longest clear arc: {longestClear:0.0} m");
        Expect(missedNoClr == 0, "every arc the polygons say hits something is blocked (no arc into a ceiling)");
        Expect(missed == 0, "the arc test agrees with the polygons on every arc they block (same clearance)");
        Expect(stricter * 100 <= Math.Max(1, arcTested) * 5, "and blocks at most 5% more (2.5 m segments vs 0.25 m samples, volumes vs polygons)");
        Expect(ceiling > 0, "some long open lines are blocked by the ceiling the arc rises into");
    }

    /// <summary>Outdoors (Holtburg, 0xA9B4): open sky, so a 30-60 m arc over open ground is clear
    /// wherever the straight line is.</summary>
    private static void MagicArcOutdoors()
    {
        const uint lb = 0xA9B4;
        uint cell = (lb << 16) | 0x0001;
        float bx = (lb >> 8) * 192f, by = (lb & 0xFF) * 192f;
        Console.WriteLine($"\n=== Arc spells outdoors, landblock 0x{lb:X4} ===");
        int tested = 0, lineBlocked = 0, arcBlocked = 0, arcOnly = 0;
        float maxApex = 0f;
        for (float x = 16; x < 176; x += 16)
            for (float y = 16; y < 176; y += 16)
                foreach (var (dx, dy) in new[] { (40f, 0f), (0f, 50f), (30f, 30f) })
                {
                    float ax = bx + x, ay = by + y, tx = ax + dx, ty = ay + dy;
                    if (tx > bx + 190 || ty > by + 190) continue;
                    float az = _geo.GetTerrainZWorld(ax, ay), tz = _geo.GetTerrainZWorld(tx, ty);
                    if (float.IsNaN(az) || float.IsNaN(tz) || Math.Abs(az - tz) > 4f) continue;
                    var a = new Vector3(ax, ay, az); var t = new Vector3(tx, ty, tz);
                    // Terrain is not part of the plugin's verdict; keep lines over open ground.
                    if (TerrainBetween(a + Chest, t + Chest)) continue;
                    tested++;
                    bool line = _fsm.IsPathBlocked(cell, a, t, TargetingFSM.AttackType.Linear, out _);
                    bool arc = _fsm.IsMagicArcPathBlocked(cell, a, t, out var det);
                    if (line) lineBlocked++;
                    if (arc) arcBlocked++;
                    if (arc && !line) arcOnly++;
                    maxApex = Math.Max(maxApex, det.Arc.Sag);
                }
        Console.WriteLine($"  {tested} lines 40-50 m over open ground: straight line blocked {lineBlocked}, arc blocked {arcBlocked} " +
                          $"(arc only: {arcOnly}); highest rise above the line {maxApex:0.00} m");
        Expect(tested > 20, "found outdoor lines to test");
        Expect(arcOnly * 100 <= Math.Max(1, tested) * 5, "outdoors an arc is clear wherever the line is (at most 5% clip a tree or roof)");
    }

    private static bool TerrainBetween(Vector3 a, Vector3 b)
    {
        for (int i = 1; i < 40; i++)
        {
            float t = i / 40f;
            float x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t, z = a.Z + (b.Z - a.Z) * t;
            float g = _geo.GetTerrainZWorld(x, y);
            if (!float.IsNaN(g) && g > z - 0.2f) return true;
        }
        return false;
    }

    private sealed class TriSet { public Vector3[] V; public Vector3 Lo, Hi; }

    private static TriSet Tris(DungeonLOS.MapPolygon p)
    {
        var v = p.Vertices;
        return new TriSet
        {
            V = v,
            Lo = new Vector3(v.Min(q => q.X), v.Min(q => q.Y), v.Min(q => q.Z)),
            Hi = new Vector3(v.Max(q => q.X), v.Max(q => q.Y), v.Max(q => q.Z)),
        };
    }

    /// <summary>Does segment a→b cross a solid polygon, ignoring <paramref name="skipStart"/> m
    /// at its start and <paramref name="skipEnd"/> m at its end?</summary>
    private static bool Hits(Vector3 a, Vector3 b, List<TriSet> solid, float skipStart, float skipEnd)
    {
        Vector3 d = b - a; float len = d.Length(); if (len < 1e-4f) return false;
        Vector3 dir = d / len;
        float lx = Math.Min(a.X, b.X), ly = Math.Min(a.Y, b.Y), lz = Math.Min(a.Z, b.Z);
        float hx = Math.Max(a.X, b.X), hy = Math.Max(a.Y, b.Y), hz = Math.Max(a.Z, b.Z);
        foreach (var p in solid)
        {
            if (p.Hi.X < lx || p.Lo.X > hx || p.Hi.Y < ly || p.Lo.Y > hy || p.Hi.Z < lz || p.Lo.Z > hz) continue;
            for (int i = 1; i + 1 < p.V.Length; i++)
                if (Tri(a, dir, p.V[0], p.V[i], p.V[i + 1], out float t) && t > skipStart && t < len - skipEnd)
                    return true;
        }
        return false;
    }

    /// <summary>Ground truth for an arc spell: the path ACE flies (head height to chest height,
    /// 40 m/s across the ground, g 9.8) plus the clearance bump, sampled every 0.25 m, against the render polygons.
    /// Like the plugin, the first 0.5 m (the caster) and the last 0.3 m (the target) don't count.</summary>
    private static bool ArcHitsPolygons(Vector3 feetA, Vector3 feetB, List<TriSet> solid, float clearance)
    {
        var launch = feetA + Head; var aim = feetB + Chest;
        var arc = MissileBallistics.SolveLateral(launch.X, launch.Y, launch.Z, aim.X, aim.Y, aim.Z, _fsm.MagicArcVelocity);
        int n = Math.Max(8, (int)Math.Ceiling(arc.HorizDist / 0.25f));
        Vector3 prev = launch; float travelled = 0f;
        for (int i = 1; i <= n; i++)
        {
            arc.PointAt((float)i / n, clearance, out float x, out float y, out float z);
            var cur = i == n ? aim : new Vector3(x, y, z);
            float len = (cur - prev).Length();
            float skipStart = Math.Max(0f, 0.5f - travelled);
            float skipEnd = i == n ? 0.3f : 0f;
            if (skipStart < len && Hits(prev, cur, solid, skipStart, skipEnd)) return true;
            travelled += len; prev = cur;
        }
        return false;
    }

    private static void Expect(bool ok, string msg)
    {
        Console.WriteLine((ok ? "     [ok]   " : "     [FAIL] ") + msg);
        if (!ok) _fails++;
    }

    // ── Building the pairs (ground truth from the dat polygons, independent of the ray code) ──

    private static List<Pair> BuildPairs(Dictionary<uint, Cell> cells, List<DungeonLOS.MapPolygon> polys)
    {
        var result = new List<Pair>();
        var spots = FloorSpots();
        var ids = spots.Keys.OrderBy(k => k).ToList();

        // W/H: a short straight line but a long walk (not within 6 portals), same floor level,
        // and the line crosses solid polygons. O: the line crosses nothing, 2-15 m, cells
        // linked within 2 portals (the same room or corridor).
        foreach (uint a in ids)
        {
            var hops = Hops(cells, a, 6);
            foreach (uint b in ids)
            {
                if (b <= a) continue;
                Vector3 pa = spots[a], pb = spots[b];
                if (Math.Abs(pa.Z - pb.Z) > 0.5f) continue;
                float flat = Flat(pa, pb);
                if (flat < 2f || flat > 15f) continue;
                var hits = Crossings(pa + Chest, pb + Chest, polys);
                bool linked = hops.TryGetValue(b, out int h);
                if (!linked && hits.Count > 0)
                {
                    result.Add(new Pair('W', a, b, pa, pb, true));
                    float first = hits[0], last = hits[^1], len = (pb - pa).Length();
                    if (first >= 0.5f && len - last >= 0.5f)
                    {
                        Vector3 dir = (pb - pa) / len;
                        result.Add(new Pair('H', a, b, pa + dir * (first - 0.45f), pa + dir * (last + 0.45f), true));
                    }
                }
                else if (linked && h <= 2 && hits.Count == 0 && Clear(pa, polys) && Clear(pb, polys))
                    result.Add(new Pair('O', a, b, pa, pb, false));
            }
            Vector3 p = spots[a], q = p + new Vector3(1.5f, 0, 0);
            if (Crossings(p + Chest, q + Chest, polys).Count == 0)
                result.Add(new Pair('M', a, a, p, q, false));
        }

        // L: every pair of standing spots (0.5 m grid, 0.45 m clear of walls) in Lucy's cell and
        // the monster's cell at the logged 3.7-4.9 m.
        uint me = 0x63460132, mob = 0x63460118;
        var ga = FloorGrid(me, 0.5f).Where(x => Clear(x, polys)).ToList();
        var gb = FloorGrid(mob, 0.5f).Where(x => Clear(x, polys)).ToList();
        foreach (var pa in ga)
            foreach (var pb in gb)
            {
                float f = Flat(pa, pb);
                if (f < 3.65f || f > 4.95f) continue;
                bool wall = Crossings(pa + Chest, pb + Chest, polys).Count > 0;
                if (wall) result.Add(new Pair('L', me, mob, pa, pb, true));
            }

        // R: up or down a ramp or step. Lucy's logs have monsters up to 2.6 m above or below
        // her. Centres of floor polygons (each with room for a body) 2-10 m apart, 0.5-3 m of
        // height between them, cells linked within 2 portals, the line crossing nothing.
        // (The loader's "floor" polygons are all near-horizontal ones, ceilings included: a
        // polygon with another one of its cell more than 1.5 m below its centre is a ceiling.)
        var horiz = _geo.DungeonLOS.GetDungeonMapFloorPolygons(Lb);
        var byCell = horiz.GroupBy(p => p.CellId).ToDictionary(g => g.Key, g => g.ToList());
        var rampSpots = horiz
            .Select(p => (Cell: p.CellId, P: new Vector3(p.Vertices.Average(v => v.X), p.Vertices.Average(v => v.Y), p.Vertices.Average(v => v.Z))))
            .Where(s => !byCell[s.Cell].Any(o => Inside(o.Vertices, s.P.X, s.P.Y) && o.Vertices.Average(v => v.Z) < s.P.Z - 1.5f))
            .Where(s => cells.ContainsKey(s.Cell) && Clear(s.P, polys)).ToList();
        foreach (var sa in rampSpots)
        {
            var hops = Hops(cells, sa.Cell, 2);
            foreach (var sb in rampSpots)
            {
                if (sb.Cell <= sa.Cell || !hops.ContainsKey(sb.Cell)) continue;
                float dz = Math.Abs(sa.P.Z - sb.P.Z), f = Flat(sa.P, sb.P);
                if (dz < 0.5f || dz > 3f || f < 2f || f > 10f) continue;
                if (Crossings(sa.P + Chest, sb.P + Chest, polys).Count == 0)
                    result.Add(new Pair('R', sa.Cell, sb.Cell, sa.P, sb.P, false));
            }
        }

        // D: a doorway at an angle. 0x6346012E and 0x63460118 are two portals apart (Lucy's
        // lock on 09-29 at 5.0-5.4 m): every pair of standing spots at that distance whose line
        // crosses nothing, i.e. the monster is in view through the opening.
        me = 0x6346012E;
        ga = FloorGrid(me, 0.5f).Where(x => Clear(x, polys)).ToList();
        foreach (var pa in ga)
            foreach (var pb in gb)
            {
                float f = Flat(pa, pb);
                if (f < 4.95f || f > 5.45f) continue;
                if (Crossings(pa + Chest, pb + Chest, polys).Count == 0) result.Add(new Pair('D', me, mob, pa, pb, false));
            }
        return result;
    }

    internal sealed class Pair
    {
        public char Kind; public uint Cell, CellB; public Vector3 A, B; public bool Wall;
        public Pair(char k, uint c, uint cb, Vector3 a, Vector3 b, bool w) { Kind = k; Cell = c; CellB = cb; A = a; B = b; Wall = w; }
        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "{0} {1:X8} {2:X8} {3:F3} {4:F3} {5:F3} {6:F3} {7:F3} {8:F3} {9}", Kind, Cell, CellB, A.X, A.Y, A.Z, B.X, B.Y, B.Z, Wall ? 1 : 0);
        public static Pair Parse(string l)
        {
            var f = l.Split(' ');
            float F(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
            return new Pair(f[0][0], uint.Parse(f[1], NumberStyles.HexNumber), uint.Parse(f[2], NumberStyles.HexNumber),
                new Vector3(F(3), F(4), F(5)), new Vector3(F(6), F(7), F(8)), f[9] == "1");
        }
    }

    private static float Flat(Vector3 a, Vector3 b)
        => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // A standing spot in a cell: the centre of its lowest floor polygon (feet height).
    private static Dictionary<uint, Vector3> FloorSpots()
    {
        var spots = new Dictionary<uint, Vector3>();
        foreach (var g in _geo.DungeonLOS.GetDungeonMapFloorPolygons(Lb).GroupBy(p => p.CellId))
        {
            var floor = g.OrderBy(p => p.Vertices.Average(v => v.Z)).ThenByDescending(p => Area2D(p.Vertices)).First();
            spots[g.Key] = new Vector3(floor.Vertices.Average(v => v.X), floor.Vertices.Average(v => v.Y), floor.Vertices.Average(v => v.Z));
        }
        return spots;
    }

    private static float Area2D(Vector3[] v)
    {
        float a = 0;
        for (int i = 0; i < v.Length; i++) { var p = v[i]; var q = v[(i + 1) % v.Length]; a += p.X * q.Y - q.X * p.Y; }
        return Math.Abs(a) * 0.5f;
    }

    private static List<Vector3> FloorGrid(uint cellId, float step)
    {
        var pts = new List<Vector3>();
        var floors = _geo.DungeonLOS.GetDungeonMapFloorPolygons(cellId >> 16).Where(p => p.CellId == cellId).ToList();
        if (floors.Count == 0) return pts;
        float minZ = floors.Min(p => p.Vertices.Average(v => v.Z));
        foreach (var f in floors.Where(p => p.Vertices.Average(v => v.Z) < minZ + 0.6f))
        {
            float x0 = f.Vertices.Min(v => v.X), x1 = f.Vertices.Max(v => v.X);
            float y0 = f.Vertices.Min(v => v.Y), y1 = f.Vertices.Max(v => v.Y);
            for (float x = x0 + step / 2; x < x1; x += step)
                for (float y = y0 + step / 2; y < y1; y += step)
                    if (Inside(f.Vertices, x, y)) pts.Add(new Vector3(x, y, f.Vertices.Average(v => v.Z)));
        }
        return pts;
    }

    private static bool Inside(Vector3[] poly, float x, float y)
    {
        bool c = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) &&
                x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                c = !c;
        return c;
    }

    // Room for a body: nothing solid within 0.45 m at chest height.
    private static bool Clear(Vector3 feet, List<DungeonLOS.MapPolygon> polys)
    {
        Vector3 c = feet + Chest;
        for (int i = 0; i < 8; i++)
        {
            double ang = i * Math.PI / 4;
            if (Crossings(c, c + new Vector3((float)Math.Cos(ang) * 0.45f, (float)Math.Sin(ang) * 0.45f, 0), polys).Count > 0)
                return false;
        }
        return true;
    }

    // Every crossing of the segment with a solid (non-portal) render polygon, as distances along it.
    private static List<float> Crossings(Vector3 a, Vector3 b, List<DungeonLOS.MapPolygon> polys)
    {
        var hits = new List<float>();
        Vector3 d = b - a; float len = d.Length(); if (len < 1e-4f) return hits;
        Vector3 dir = d / len;
        foreach (var p in polys)
        {
            if (p.IsPortal) continue;
            for (int i = 1; i + 1 < p.Vertices.Length; i++)
                if (Tri(a, dir, p.Vertices[0], p.Vertices[i], p.Vertices[i + 1], out float t) && t > 0.02f && t < len - 0.02f)
                    hits.Add(t);
        }
        hits.Sort();
        return hits;
    }

    private static bool Tri(Vector3 o, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0;
        Vector3 e1 = v1 - v0, e2 = v2 - v0, h = Vector3.Cross(dir, e2);
        float a = Vector3.Dot(e1, h);
        if (Math.Abs(a) < 1e-7f) return false;
        float f = 1f / a; Vector3 s = o - v0;
        float u = f * Vector3.Dot(s, h); if (u < 0 || u > 1) return false;
        Vector3 qv = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(dir, qv); if (v < 0 || u + v > 1) return false;
        t = f * Vector3.Dot(e2, qv);
        return t > 0;
    }

    private static Dictionary<uint, int> Hops(Dictionary<uint, Cell> cells, uint from, int max)
    {
        var dist = new Dictionary<uint, int> { [from] = 0 };
        var q = new Queue<uint>(); q.Enqueue(from);
        uint hi = from & 0xFFFF0000u;
        while (q.Count > 0)
        {
            uint c = q.Dequeue();
            if (dist[c] >= max || !cells.TryGetValue(c, out var cell)) continue;
            foreach (uint l in cell.Links)
            {
                uint n = hi | l;
                if (!dist.ContainsKey(n)) { dist[n] = dist[c] + 1; q.Enqueue(n); }
            }
        }
        return dist;
    }

    // ── Dat-wide: how many dungeon cells have wall geometry at all ──

    private static int Sweep()
    {
        var byLb = new Dictionary<uint, int>();
        foreach (var e in _geo.CellDat.EnumerateEntries())
        {
            uint low = e.ObjectId & 0xFFFF;
            if (low < 0x0100 || low >= 0xFFFE) continue;
            uint lb = e.ObjectId >> 16;
            byLb[lb] = byLb.TryGetValue(lb, out int n) ? n + 1 : 1;
        }
        long cellsTotal = 0, cellsWalled = 0, struct0 = 0;
        var missing = new Dictionary<(uint Env, uint Struct), int>();
        long portalOnly = 0;
        int lbs = 0;
        foreach (uint lb in byLb.Keys.OrderBy(k => k))
        {
            var cells = LoadCells(lb);
            var lbPolys = _geo.DungeonLOS.GetDungeonMapPolygons(lb);
            var walled = lbPolys.Where(p => !p.IsPortal).Select(p => p.CellId).ToHashSet();
            portalOnly += lbPolys.Select(p => p.CellId).Distinct().Count(id => !walled.Contains(id));
            cellsTotal += cells.Count;
            cellsWalled += cells.Keys.Count(walled.Contains);
            foreach (var c in cells.Values)
                if (!walled.Contains(c.Id))
                {
                    var key = (c.Env, c.Struct);
                    missing[key] = missing.TryGetValue(key, out int m) ? m + 1 : 1;
                }
            struct0 += cells.Values.Count(c => c.Struct == 0);
            lbs++;
            _geo.DungeonLOS.FlushCache();
        }
        Console.WriteLine($"Sweep: {lbs} landblocks with EnvCells, {cellsTotal} EnvCells ({struct0} use CellStruct 0); " +
                          $"{cellsWalled} have wall geometry, {cellsTotal - cellsWalled} have none ({100.0 * (cellsTotal - cellsWalled) / Math.Max(1, cellsTotal):F1}%).");
        Console.WriteLine($"  of those, {portalOnly} have polygons that are all portals (openings), the rest none at all");
        Console.WriteLine($"  cells without walls by (Environment, CellStruct): {missing.Count} kinds; most common:");
        foreach (var kv in missing.OrderByDescending(k => k.Value).Take(12))
        {
            byte[] env = _geo.PortalDat.GetFileData(kv.Key.Env);
            string info = env == null ? "no Environment file" : $"Environment declares {BitConverter.ToUInt32(env, 4)} CellStructs";
            Console.WriteLine($"    0x{kv.Key.Env:X8} #{kv.Key.Struct}: {kv.Value} cells ({info})");
        }
        return 0;
    }

    internal sealed class Cell
    {
        public uint Id;
        public float X, Y, Z;
        public uint Env;
        public uint Struct;
        public List<uint> Links = new();
    }

    // Independent EnvCell reader (ACE EnvCell layout), for the CellStruct index and portal links.
    private static Dictionary<uint, Cell> LoadCells(uint lb)
    {
        var result = new Dictionary<uint, Cell>();
        foreach (uint id in _geo.CellDat.GetLandblockCellIds(lb))
        {
            if ((id & 0xFFFF) < 0x0100 || (id & 0xFFFF) >= 0xFFFE) continue;
            byte[] d = _geo.CellDat.GetFileData(id);
            if (d == null || d.Length < 20) continue;
            try
            {
                using var r = new BinaryReader(new MemoryStream(d));
                r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
                byte nSurf = r.ReadByte(); byte nPort = r.ReadByte(); r.ReadUInt16();
                for (int i = 0; i < nSurf; i++) r.ReadUInt16();
                var c = new Cell { Id = id, Env = 0x0D000000u | r.ReadUInt16(), Struct = r.ReadUInt16() };
                c.X = r.ReadSingle() + ((lb >> 8) & 0xFF) * 192f;
                c.Y = r.ReadSingle() + (lb & 0xFF) * 192f;
                c.Z = r.ReadSingle();
                r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle();
                for (int i = 0; i < nPort; i++)
                {
                    r.ReadUInt16(); r.ReadUInt16();
                    ushort other = r.ReadUInt16(); r.ReadUInt16();
                    c.Links.Add(other);
                }
                result[id] = c;
            }
            catch (EndOfStreamException) { }
        }
        return result;
    }
}
