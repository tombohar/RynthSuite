using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// What the dats place that AC collides with (GeometryLoader.GetStaticCollision, 2026-11-18). The loader kept a setup's
// first placement instead of AC's Resting (0x65) pose, ignored DefaultScale, kept physics polygons only (so 60,289
// cylinder and sphere objects at body height in the dungeons were nowhere: 0x6346's pillars, 0x0190's round tables),
// let a box round each mesh stand for it, and put the cells' objects a cell's offset away in combat LOS. Now:
//   - parts posed as AC poses them and scaled by DefaultScale;
//   - physics polygons, else cylinders, else spheres (AC's order), the round ones as closed solids every triangle
//     consumer reads unchanged (a level cut is the true circle);
//   - triangle hits only, one cached list per landblock shared by every caller;
//   - the landblock's own objects over a building cell's floor filed under that cell;
//   - combat LOS has the 0x0190 table, the 0x00C1 statue and the 0x000A altar where they stand.
internal static class StaticCollisionTests
{
    public static void Register(Runner r)
    {
        r.Add("static collision: a cylinder and a sphere become closed solids whose level cuts are the true circle, rays and the map see them (synthetic)", Solids);
        r.Add("static collision: parts in AC's Resting pose and DefaultScale - 0x0200016C 0..0.82 m, 0x02000183, 0x0200187B and 0x02000D96 scaled (if the dats are here)", PoseAndScale);
        r.Add("static collision: 0x6346's pillars and 0x0190's round tables are on the map at their size and height, 0x0190's room 88 table as it was (if the dats are here)", RealObjects);
        r.Add("static collision: combat LOS has the 0x0190 table, the 0x00C1 statue and the 0x000A altar where they stand (if the dats are here)", CombatLos);
        r.Add("static collision: one list per landblock, built once for two threads, triangle hits only; landblock objects on 0x7D64's upper floor filed under their cells (if the dats are here)", CacheAndLandblockObjects);
    }

    private static Vector3 V(double x, double y, double z) => new((float)x, (float)y, (float)z);

    // Where the triangles cross the level z (a vertex at z counts as above it), as DungeonGeometry cuts them.
    private static List<(double X0, double Y0, double X1, double Y1)> Cut(IReadOnlyList<Vector3> tris, double z)
    {
        var segs = new List<(double, double, double, double)>();
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            int n = 0;
            double ax = 0, ay = 0, bx = 0, by = 0;
            for (int e = 0; e < 3; e++)
            {
                var p = tris[i + e];
                var q = tris[i + (e + 1) % 3];
                if ((p.Z >= z) == (q.Z >= z)) continue;
                double t = (z - p.Z) / (q.Z - p.Z);
                double x = p.X + (q.X - p.X) * t, y = p.Y + (q.Y - p.Y) * t;
                if (n == 0) { ax = x; ay = y; } else { bx = x; by = y; }
                n++;
            }
            if (n == 2 && (ax - bx) * (ax - bx) + (ay - by) * (ay - by) > 1e-10) segs.Add((ax, ay, bx, by));
        }
        return segs;
    }

    // Every segment end meets another's (a closed outline).
    private static bool Closed(List<(double X0, double Y0, double X1, double Y1)> segs)
    {
        var ends = segs.SelectMany(s => new[] { (s.X0, s.Y0), (s.X1, s.Y1) }).ToList();
        foreach (var (x, y) in ends)
            if (ends.Count(o => Math.Abs(o.Item1 - x) < 1e-4 && Math.Abs(o.Item2 - y) < 1e-4) < 2) return false;
        return true;
    }

    // The radius of the circle with the cut's area (a fan from the centre: the cut is convex round it).
    private static double CutRadius(List<(double X0, double Y0, double X1, double Y1)> segs, double cx, double cy)
    {
        double area = 0;
        foreach (var (x0, y0, x1, y1) in segs) area += Math.Abs((x0 - cx) * (y1 - cy) - (x1 - cx) * (y0 - cy)) / 2;
        return Math.Sqrt(area / Math.PI);
    }

    private static BoundingVolume Mesh(List<Vector3> tris)
    {
        var min = V(tris.Min(v => v.X), tris.Min(v => v.Y), tris.Min(v => v.Z));
        var max = V(tris.Max(v => v.X), tris.Max(v => v.Y), tris.Max(v => v.Z));
        return new BoundingVolume { Type = BoundingVolume.VolumeType.TriangleMesh, Min = min, Max = max, Center = (min + max) * 0.5f, Dimensions = max - min, MeshTriangles = tris.ToArray(), NoAabbFallback = true };
    }

    private static void Solids()
    {
        // A pillar: 0.8 m radius from z 1 to z 3.
        var cyl = new List<Vector3>();
        GeometryLoader.AppendCylinderSolid(cyl, V(5, 5, 1), 0.8f, 2f);
        Check.Eq(cyl.Count / 3, 44, "a 12-sided prism: 24 side triangles and two 10-triangle caps");
        foreach (double z in new[] { 1.05, 1.5, 2.0, 2.95 })
        {
            var cut = Cut(cyl, z);
            Check.True(cut.Count > 0 && Closed(cut), $"the pillar cut at {z} m is a closed outline ({cut.Count} segments)");
            Check.Near(CutRadius(cut, 5, 5), 0.8, 0.004, $"the cut at {z} m has the circle's area");
            double near = cut.Min(s => Math.Sqrt((s.X0 + s.X1 - 10) * (s.X0 + s.X1 - 10) / 4 + (s.Y0 + s.Y1 - 10) * (s.Y0 + s.Y1 - 10) / 4));
            double far = cut.Max(s => Math.Sqrt((s.X0 - 5) * (s.X0 - 5) + (s.Y0 - 5) * (s.Y0 - 5)));
            Check.True(near >= 0.8 * 0.98 && far <= 0.8 * 1.03, $"its outline stays within 2-3 % of the radius ({near:F3}..{far:F3} m)");
        }
        Check.Eq(Cut(cyl, 0.95).Count + Cut(cyl, 3.05).Count, 0, "nothing under or over it");

        // A round table's sphere: 1 m radius round (0, 0, 2).
        var sph = new List<Vector3>();
        GeometryLoader.AppendSphereSolid(sph, V(0, 0, 2), 1f);
        Check.Eq(sph.Count / 3, 168, "12 slices by 8 stacks");
        foreach (double dz in new[] { -0.75, -0.5, -0.25, 0.0, 0.1, 0.25, 0.5, 0.75, 0.9 })
        {
            var cut = Cut(sph, 2 + dz);
            double truth = Math.Sqrt(1 - dz * dz);
            Check.True(cut.Count > 0 && Closed(cut), $"the sphere cut {dz:+0.00;-0.00} m off its middle is a closed outline");
            Check.Near(CutRadius(cut, 0, 0), truth, 0.015, $"the cut {dz:+0.00;-0.00} m off its middle is the true circle ({truth:F3} m)");
        }
        Check.Eq(Cut(sph, 2 + 1.02).Count + Cut(sph, 2 - 1.02).Count, 0, "nothing past its top or bottom");
        var cup = new List<Vector3>();
        GeometryLoader.AppendSphereSolid(cup, V(0, 0, 1), 0.1f);
        Check.Eq(cup.Count / 3, 48, "a 0.1 m sphere (a cup on a table) is 8 x 4");
        Check.Near(CutRadius(Cut(cup, 1.0), 0, 0), 0.1, 0.005, "and its middle cut is within 5 mm");

        // Rays: the solids, no box round them.
        var pillar = Mesh(cyl);
        Check.True(pillar.RayIntersect(V(2, 5, 2), V(1, 0, 0), 5f, out float t) && Math.Abs(t - 2.2) < 0.03, $"a ray at the pillar meets its side 2.2 m on ({t:F3})");
        Check.False(pillar.RayIntersect(V(2, 5.83, 2), V(1, 0, 0), 5f, out _), "a ray 0.83 m off its axis passes: no box fallback");
        var ball = Mesh(sph);
        Check.True(ball.RayIntersect(V(-3, 0, 2.5), V(1, 0, 0), 6f, out float tb) && Math.Abs(tb - (3 - Math.Sqrt(0.75))) < 0.03, $"a ray 0.5 m over the sphere's middle meets it where the circle there is ({tb:F3})");
        Check.False(ball.RayIntersect(V(-3, 0.75, 2.75), V(1, 0, 0), 6f, out _), "one over its shoulder, inside its box, passes");

        // The map: a room with the pillar and the ball, cut as any placed object is.
        const uint cell = 0x7E7E0100;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(10), NS = DungeonGeometry.WorldToNav(10), Z = 0 } };
        var floors = new List<(uint, Vector3[])> { (cell, new[] { V(0, 0, 0), V(20, 0, 0), V(20, 20, 0), V(0, 20, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, Vector3[])>(), floors, Array.Empty<(uint, Vector3[])>());
        var post = new List<Vector3>();
        GeometryLoader.AppendCylinderSolid(post, V(5, 5, 0), 0.75f, 3.75f);
        var table = new List<Vector3>();
        GeometryLoader.AppendSphereSolid(table, V(14, 14, 0.56), 0.56f);
        int added = geo.AddPlacedObjects(new (uint, uint, Vector3[])[] { (0x0200028D, cell, post.ToArray()), (0x020000CE, cell, table.ToArray()) });
        Check.Eq(added, 2, "both stand in the way at body height");
        Check.True(geo.InsidePlaced(5, 5, 0) && geo.InsidePlaced(5.6, 5, 0) && !geo.InsidePlaced(5.9, 5, 0), "the pillar: inside within its radius, not past it");
        Check.True(geo.InsidePlaced(14, 14, 0) && !geo.InsidePlaced(14.7, 14, 0), "the round table: inside its middle, not past its edge");
        Check.Near(geo.Clearance(7, 5, 0), 1.25, 0.1, $"2 m from the pillar's axis: 1.25 m to its side ({geo.Clearance(7, 5, 0):F2})");
    }

    private static GeometryLoader? Loader()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return null; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return null; }
        return loader;
    }

    private static (float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ) Box(IReadOnlyList<Vector3> t) =>
        (t.Min(v => v.X), t.Max(v => v.X), t.Min(v => v.Y), t.Max(v => v.Y), t.Min(v => v.Z), t.Max(v => v.Z));

    private static void PoseAndScale()
    {
        var loader = Loader();
        if (loader == null) return;
        try
        {
            SetupInfo Setup(uint id)
            {
                var s = new SetupInfo();
                s.Unpack(loader.PortalDat.GetFileData(id));
                return s;
            }

            // 0x0200016C (557 placed in 182 landblocks): its Resting pose lifts its one part 0.41 m.
            var s16c = Setup(0x0200016C);
            Check.Eq(s16c.PartFramesKey, 0x65, "0x0200016C is posed Resting (0x65), as AC places it");
            Check.True(s16c.Placements.ContainsKey(0) && s16c.Placements.ContainsKey(0x65), "both its placements are kept");
            var b16c = Box(loader.SetupCollisionTriangles(0x0200016C));
            Console.WriteLine($"    0x0200016C: z {b16c.MinZ:F3}..{b16c.MaxZ:F3} (its first placement had -0.41..0.41)");
            Check.True(Math.Abs(b16c.MinZ) < 0.02 && Math.Abs(b16c.MaxZ - 0.82) < 0.02, "0x0200016C spans z 0..0.82 as AC has it");

            // 0x02000183 (4,728 placed): a metre up from its first placement.
            var b183 = Box(loader.SetupCollisionTriangles(0x02000183));
            Check.True(Math.Abs(b183.MinZ + 0.09) < 0.02 && Math.Abs(b183.MaxZ - 1.91) < 0.02, $"0x02000183 spans z -0.09..1.91 ({b183.MinZ:F2}..{b183.MaxZ:F2})");

            // DefaultScale: 0x0200187B's part is 0.75 wide in x and y, 0x02000D96's 0.54 every way.
            foreach (var (id, sx, sz) in new (uint, double, double)[] { (0x0200187B, 0.75, 1.0), (0x02000D96, 0.54, 0.54) })
            {
                var s = Setup(id);
                Check.True(s.DefaultScale != null && Math.Abs(s.PartScale(0).X - sx) < 0.01 && Math.Abs(s.PartScale(0).Z - sz) < 0.01, $"0x{id:X8} reads its DefaultScale");
                var whole = Box(loader.SetupCollisionTriangles(id));
                var part = Box(loader.SetupCollisionTriangles(s.PartIds[0]));
                Console.WriteLine($"    0x{id:X8}: x {whole.MinX:F2}..{whole.MaxX:F2} z {whole.MinZ:F2}..{whole.MaxZ:F2}; its part 0x{s.PartIds[0]:X8} unscaled x {part.MinX:F2}..{part.MaxX:F2} z {part.MinZ:F2}..{part.MaxZ:F2}");
                Check.Near((whole.MaxX - whole.MinX) / (part.MaxX - part.MinX), sx, 0.005, $"0x{id:X8}: x scaled by {sx}");
                Check.Near((whole.MaxY - whole.MinY) / (part.MaxY - part.MinY), sx, 0.005, $"0x{id:X8}: y scaled by {sx}");
                Check.Near((whole.MaxZ - whole.MinZ) / (part.MaxZ - part.MinZ), sz, 0.005, $"0x{id:X8}: z scaled by {sz}");
            }

            // AC's order of shapes.
            Check.Eq(loader.DescribeCollision(0x0200028D, out _, out int c28d, out _), StaticCollisionKind.Cylinders, "0x0200028D (a pillar, no physics polygons): its cylinder");
            Check.Eq(c28d, 1, "one cylinder");
            Check.Eq(loader.DescribeCollision(0x020000CE, out _, out _, out int s0ce), StaticCollisionKind.Spheres, "0x020000CE (a round table): its sphere");
            Check.Eq(s0ce, 1, "one sphere");
            Check.Eq(loader.DescribeCollision(0x02000183, out _, out int c183, out _), StaticCollisionKind.Polygons, "0x02000183 has cylinders and physics polygons: the polygons, as AC");
            Check.Eq(c183, 0, "and its cylinders are not kept");
            Check.Eq(loader.DescribeCollision(0x010000EE, out int t0ee, out _, out _), StaticCollisionKind.Polygons, "a GfxObj stab (0x010000EE): its polygons");
            Check.True(t0ee > 100, $"all of them ({t0ee})");
        }
        finally { loader.Dispose(); }
    }

    // The lowest floor polygon's height of a cell.
    private static double FloorOf(GeometryLoader loader, uint lb, uint cell) =>
        loader.DungeonLOS.GetDungeonMapFloorPolygons(lb).Where(p => p.CellId == cell).Min(p => p.Vertices.Average(v => (double)v.Z));

    private static void RealObjects()
    {
        var loader = Loader();
        if (loader == null) return;
        var before = DungeonPathfinder.StaticMeshSource;
        try
        {
            // 0x6346: 69 pillars 0x0200028D, 0.76 m round and 3.75 m tall, standing on their floors.
            var all6346 = loader.GetStaticCollision(0x6346);
            var pillars = all6346.Where(s => s.SetupId == 0x0200028D).ToList();
            Console.WriteLine($"    0x6346: {all6346.Count} statics (none before), {pillars.Count} of them 0x0200028D");
            Check.Eq(pillars.Count, 69, "0x6346 has its 69 pillars");
            Check.True(pillars.All(p => p.Kind == StaticCollisionKind.Cylinders && p.Cylinders.Count == 1 && Math.Abs(p.Cylinders[0].Radius - 0.76) < 0.01 && Math.Abs(p.Cylinders[0].Height - 3.75) < 0.01),
                       "each a cylinder 0.76 m round, 3.75 m tall");
            Check.True(pillars.All(p => p.Max.X - p.Min.X > 1.50 && p.Max.X - p.Min.X < 1.60 && Math.Abs(p.Max.Z - p.Min.Z - 3.75) < 0.01), "its solid 1.5-1.6 m across, 3.75 m tall");
            Check.True(pillars.All(p => Math.Abs(p.Min.Z - FloorOf(loader, 0x6346, p.CellId)) < 0.05), "each standing on its cell's floor");

            // 0x0190: 15 round tables 0x020000CE, a 0.56 m sphere on the floor.
            var tables = loader.GetStaticCollision(0x0190).Where(s => s.SetupId == 0x020000CE).ToList();
            Check.Eq(tables.Count, 15, "0x0190 has its 15 round tables");
            Check.True(tables.All(p => p.Kind == StaticCollisionKind.Spheres && Math.Abs(p.Spheres[0].Radius - 0.56) < 0.01), "each a 0.56 m sphere");
            Check.True(tables.All(p => p.Max.X - p.Min.X > 1.10 && p.Max.X - p.Min.X < 1.17 && p.Max.Z - p.Min.Z > 1.11 && p.Max.Z - p.Min.Z < 1.15), "its solid about 1.13 m across and tall");
            Check.True(tables.All(p => Math.Abs(p.Min.Z - FloorOf(loader, 0x0190, p.CellId)) < 0.05), "each resting on its cell's floor");

            // Room 88's table 0x020002FB (cell 01A2): its first placement (0x7A) turned it upside down and lifted it
            // 0.92 m, which puts the same box where Resting (0x65) does: the map's footprint stays.
            var room88 = loader.GetStaticCollision(0x0190).Single(s => s.SetupId == 0x020002FB && s.CellId == 0x019001A2);
            var m = room88.Mesh;
            Console.WriteLine($"    0x0190 room 88's table: x {m.Min.X:F2}..{m.Max.X:F2} y {m.Min.Y:F2}..{m.Max.Y:F2} z {m.Min.Z:F2}..{m.Max.Z:F2}");
            Check.True(Math.Abs(m.Min.X - 260.23) < 0.05 && Math.Abs(m.Max.X - 261.57) < 0.05 && Math.Abs(m.Min.Y - 27596.50) < 0.05 && Math.Abs(m.Max.Y - 27599.50) < 0.05
                       && Math.Abs(m.Min.Z + 11.98) < 0.05 && Math.Abs(m.Max.Z + 11.08) < 0.05, "it stands at x 260.2-261.6, y 27596.5-27599.5, z -12.0..-11.1");
            var st = new SetupInfo();
            st.Unpack(loader.PortalDat.GetFileData(0x020002FB));
            var f7a = st.Placements[0x7A][0];
            var oldPose = loader.SetupCollisionTriangles(st.PartIds[0]).Select(f7a.TransformPoint).ToList();
            var bo = Box(oldPose);
            var bn = Box(loader.SetupCollisionTriangles(0x020002FB));
            Check.True(Math.Abs(bo.MinX - bn.MinX) < 0.03 && Math.Abs(bo.MaxX - bn.MaxX) < 0.03 && Math.Abs(bo.MinY - bn.MinY) < 0.03 && Math.Abs(bo.MaxY - bn.MaxY) < 0.03
                       && Math.Abs(bo.MinZ - bn.MinZ) < 0.03 && Math.Abs(bo.MaxZ - bn.MaxZ) < 0.03,
                       $"its box in the old pose and AC's agree within 3 cm (z {bo.MinZ:F2}..{bo.MaxZ:F2} and {bn.MinZ:F2}..{bn.MaxZ:F2})");

            // The map sees the round ones: a round table's middle and a pillar's are inside an object.
            DungeonPathfinder.StaticMeshSource = lb => loader.GetDungeonStaticMeshes(lb);
            DungeonPathfinder.InvalidateCache();
            DungeonPathfinder.GetGraph(0x0190, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(0x0190, loader.CellDat, loader.DungeonLOS)!;
            int inTables = tables.Count(p => geo.InsidePlaced(p.Spheres[0].Center.X, p.Spheres[0].Center.Y, p.Min.Z));
            Console.WriteLine($"    0x0190: {DungeonPathfinder.PlacedNote}; {inTables} of {tables.Count} round tables' middles inside an object");
            Check.Eq(inTables, tables.Count, "every round table's middle is inside an object on the map");
            Check.True(geo.InsidePlaced(261.0, 27598.0, -12.0), "room 88's table is still on the map");
            DungeonPathfinder.InvalidateCache();
            DungeonPathfinder.GetGraph(0x6346, loader.CellDat);
            var geo6346 = DungeonPathfinder.GetGeometry(0x6346, loader.CellDat, loader.DungeonLOS)!;
            int inPillars = pillars.Count(p => geo6346.InsidePlaced(p.Cylinders[0].Low.X, p.Cylinders[0].Low.Y, p.Cylinders[0].Low.Z));
            Check.Eq(inPillars, pillars.Count, "every 0x6346 pillar's middle is inside an object on the map");
        }
        finally
        {
            DungeonPathfinder.StaticMeshSource = before;
            DungeonPathfinder.InvalidateCache();
            loader.Dispose();
        }
    }

    private static void CombatLos()
    {
        var loader = Loader();
        if (loader == null) return;
        try
        {
            foreach (var (lb, setup, x, y, z, what) in new (uint, uint, double, double, double, string)[]
                     {
                         (0x0190, 0x020002FB, 261.0, 27598.0, -12.0, "0x0190 room 88's table"),
                         (0x00C1, 0x020014A3, 149.0, 36997.0, -55.1, "0x00C1 room 81's statue"),
                         (0x000A, 0x02000969, 60.0, 1890.0, 0.0, "0x000A's altar"),
                     })
            {
                var statics = loader.GetStaticCollision(lb);
                var los = loader.GetLandblockGeometry((lb << 16) | 0x0100);
                var mine = new HashSet<BoundingVolume>(statics.Where(s => s.Source == StaticSource.CellStab).SelectMany(s => s.Parts), ReferenceEqualityComparer.Instance);
                int inLos = los.Count(v => mine.Contains(v));
                var at = los.Where(v => mine.Contains(v) && v.Min.X <= x && v.Max.X >= x && v.Min.Y <= y && v.Max.Y >= y && v.Min.Z <= z + 1.0 && v.Max.Z >= z + 0.6).ToList();
                StaticCollision Owner(BoundingVolume v) => statics.First(s => s.Parts.Any(p => ReferenceEquals(p, v)));
                var obj = Owner(at.First());
                Console.WriteLine($"    {what}: {los.Count} LOS volumes, {inLos} of them the cells' {mine.Count} static pieces; at the spot {string.Join(" ", at.Select(v => Owner(v).SetupId.ToString("X8")))}");
                Check.Eq(inLos, mine.Count, $"{what}: every piece of the cells' statics is in combat LOS, as it stands");
                Check.True(at.Count >= 1 && obj.SetupId == setup, $"{what}: a volume stands at it (0x{setup:X8})");
                // A chest-high ray from 2 m outside its box, at its middle.
                double cx = (obj.Min.X + obj.Max.X) / 2;
                var start = V(obj.Min.X - 2.0, y, z + 0.8);
                bool hit = los.Any(v => mine.Contains(v) && v.RayIntersect(start, V(1, 0, 0), (float)(cx - start.X), out _));
                Check.True(hit, $"{what}: a ray at it, 0.8 m over the floor, is stopped");
            }
        }
        finally { loader.Dispose(); }
    }

    private static void CacheAndLandblockObjects()
    {
        var loader = Loader();
        if (loader == null) return;
        try
        {
            // Two threads asking for a cold landblock get one list.
            IReadOnlyList<StaticCollision>? a = null, b = null;
            using (var go = new System.Threading.ManualResetEventSlim(false))
            {
                var ta = Task.Run(() => { go.Wait(); a = loader.GetStaticCollision(0x8804); });
                var tb = Task.Run(() => { go.Wait(); b = loader.GetStaticCollision(0x8804); });
                go.Set();
                Task.WaitAll(ta, tb);
            }
            Check.True(a != null && ReferenceEquals(a, b), "two threads at once: the same list");
            Check.True(ReferenceEquals(loader.GetStaticCollision(0x8804), a), "and the next caller gets it from the cache");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var l1 = loader.GetDungeonStaticMeshes(0x8804);
            double warm = sw.Elapsed.TotalMilliseconds;
            var l2 = loader.GetDungeonStaticMeshes(0x8804);
            Console.WriteLine($"    0x8804: {a!.Count} statics; GetDungeonStaticMeshes warm {warm:F3} ms");
            Check.False(ReferenceEquals(l1, l2), "GetDungeonStaticMeshes hands each caller a list of its own");
            Check.True(l1.Count == a.Sum(s => s.Parts.Count) && l1.Select((s, i) => ReferenceEquals(s.Mesh, l2[i].Mesh)).All(x => x), "one entry a piece, over the same shared meshes");
            Check.True(a.All(s => s.Parts.All(p => p.Type == BoundingVolume.VolumeType.TriangleMesh && p.NoAabbFallback && p.MeshTriangles.Length >= 9)),
                       "every piece is a triangle mesh that counts triangle hits only");
            Check.True(a.All(s => s.Parts.Count == (s.Kind == StaticCollisionKind.Polygons ? 1 : s.Kind == StaticCollisionKind.Cylinders ? s.Cylinders.Count : s.Spheres.Count)),
                       "polygons are one mesh, each cylinder and sphere a closed piece of its own");
            loader.FlushLandblock(0x8804);
            Check.False(ReferenceEquals(loader.GetStaticCollision(0x8804), a), "a flushed landblock is built again");

            // The dungeons: nothing from the landblock's own list (no outdoor object reaches a dungeon's cells).
            foreach (uint lb in new uint[] { 0x000A, 0x00C1, 0x0190, 0x01F5, 0x6346, 0x5456, 0x8804 })
                Check.False(loader.GetStaticCollision(lb).Any(s => s.Source == StaticSource.LandblockObject), $"0x{lb:X4}: no landblock objects");

            // 0x7D64: the outdoor list puts a bench, chairs and tables on a building's upper floor (z 15.6): each
            // filed under the cell whose floor it stands on.
            var objs = loader.GetStaticCollision(0x7D64).Where(s => s.Source == StaticSource.LandblockObject).ToList();
            var floors = loader.DungeonLOS.GetDungeonMapFloorPolygons(0x7D64);
            Console.WriteLine($"    0x7D64: {objs.Count} landblock objects over a cell's floor: {string.Join(" ", objs.GroupBy(o => o.SetupId).Select(g => $"{g.Key:X8}x{g.Count()}"))}");
            foreach (uint id in new uint[] { 0x02000123, 0x02000272, 0x02000368 })
                Check.True(objs.Any(o => o.SetupId == id && Math.Abs(o.Min.Z - 15.6) < 0.1), $"0x{id:X8} on the upper floor at z 15.6 is there");
            Check.True(objs.All(o => (o.CellId >> 16) == 0x7D64 && (o.CellId & 0xFFFF) >= 0x0100), "each filed under one of the landblock's cells");
            Check.True(objs.All(o => floors.Any(f => f.CellId == o.CellId && f.Vertices.Min(v => v.Z) < o.Max.Z && f.Vertices.Max(v => v.Z) + 1.83 > o.Min.Z)),
                       "over a floor of that cell, with something of it in the body band");
        }
        finally { loader.Dispose(); }
    }
}
