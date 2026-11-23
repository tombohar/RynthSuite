using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Placed objects by the body band (DungeonGeometry's placed objects on PlacedSolid, 2026-11-18). d02e4278 cut each
// object at 0.5, 1.0 and 1.5 m over its floor: a table's top and a rail between the cuts were missed (the
// 0x01003694 tables: 1.5 of 2.4 m2), so was a beam between 1.5 m and the body's top (1.83 m), an open cut (a
// fence, a web) filled one side by even-odd (0x02001A58: 31.5 m2 inside against 11.3), a step block whose top is a
// door's floor read as inside (E74E doors 0186/0196), and an object reaching into the next cell was walled under
// its own cell only. Now each object is solid columns, a body is in it when something there is between its feet
// + 0.45 and + 1.83 m, and the footprint at the object's floor is walls under every cell it covers.
internal static class PlacedBandTests
{
    public static void Register(Runner r)
    {
        r.Add("placed band: a table is inside under its whole top, a rail and a 1.6 m beam are in the way, a 2 m beam and a 0.4 m bench are not, a 0.55 m dais is a step-over still in the way", Heights);
        r.Add("placed band: an open fence plane is a wall line with nothing filled beside it; a ramp is a connector, on the map unless an option leaves connectors out", Shapes);
        r.Add("placed band: objects on floors 2 m and 4 m up don't block the floor below; an object across two cells is seen from either", Floors);
        r.Add("placed band: a turned copy shares its model and is turned right; an overlay joins the map and leaves it", Copies);
        r.Add("placed band: real dats - two tables inside under their tops, the 0x020003B3 rail walls, 0x02001A58 its real footprint, E74E's door sills not inside (if the dats are here)", RealDats);
    }

    private const uint Cell = 0x7E7E0100, CellB = 0x7E7E0101, Upper = 0x7E7E0102, Ledge = 0x7E7E0103;

    private static Vector3 V(double x, double y, double z) => new((float)x, (float)y, (float)z);
    private static Vector3[] Box(double x0, double y0, double z0, double x1, double y1, double z1) => PlacedObjectTests.Box(x0, y0, z0, x1, y1, z1);
    private static Vector3[] Rect(double x0, double y0, double x1, double y1, double z) => new[] { V(x0, y0, z), V(x1, y0, z), V(x1, y1, z), V(x0, y1, z) };

    private static DungeonGeometry Map(params (uint Cell, Vector3[] Floor)[] floors)
    {
        var graph = floors.Select(f => f.Cell).Distinct().ToDictionary(c => c, c => new DungeonNavNode { CellId = c, EW = DungeonGeometry.WorldToNav(10), NS = DungeonGeometry.WorldToNav(10), Z = 0 });
        return DungeonGeometry.Build(graph, Array.Empty<(uint, Vector3[])>(), floors.Select(f => (f.Cell, f.Floor)), Array.Empty<(uint, Vector3[])>());
    }

    // One 20 x 20 m room, floor at 0.
    private static DungeonGeometry Room() => Map((Cell, Rect(0, 0, 20, 20, 0)));

    // A table: its top 0.75 to 0.85 m on four 8 cm legs at its corners.
    private static Vector3[] Table(double x0, double y0, double x1, double y1)
        => Box(x0, y0, 0.75, x1, y1, 0.85)
            .Concat(Box(x0, y0, 0, x0 + 0.08, y0 + 0.08, 0.75)).Concat(Box(x1 - 0.08, y0, 0, x1, y0 + 0.08, 0.75))
            .Concat(Box(x0, y1 - 0.08, 0, x0 + 0.08, y1, 0.75)).Concat(Box(x1 - 0.08, y1 - 0.08, 0, x1, y1, 0.75)).ToArray();

    // A ramp from (x0, y0..y1) on the floor up to height h at x1, closed (base, slope, its high end and two sides).
    private static Vector3[] Ramp(double x0, double y0, double x1, double y1, double h)
    {
        Vector3 a = V(x0, y0, 0), b = V(x1, y0, 0), c = V(x1, y1, 0), d = V(x0, y1, 0), e = V(x1, y0, h), f = V(x1, y1, h);
        return new[] { a, c, b, a, d, c,      // base, facing down
                       a, e, f, a, f, d,      // slope
                       b, c, f, b, f, e,      // high end
                       a, b, e, d, f, c };    // the two sides
    }

    private static PlacedInfo Info(DungeonGeometry geo, uint setup) => geo.PlacedObjects.First(p => p.Setup == setup);

    private static void Heights()
    {
        var geo = Room();
        int added = geo.AddPlacedObjects(new (uint, uint, Vector3[])[]
        {
            (0x7E7E0011, Cell, Table(4, 4, 5.6, 5.0)),
            (0x7E7E0012, Cell, Box(10, 4, 0.79, 12, 4.05, 0.92)),   // a rail at 0.79 to 0.92 m
            (0x7E7E0013, Cell, Box(14, 4, 1.6, 16, 4.3, 1.8)),      // a beam at 1.6 to 1.8 m
            (0x7E7E0014, Cell, Box(14, 8, 2.0, 16, 8.3, 2.2)),      // a beam at 2.0 to 2.2 m
            (0x7E7E0015, Cell, Box(4, 10, 0, 6, 10.5, 0.4)),        // a 0.4 m bench
            (0x7E7E0016, Cell, Box(10, 10, 0, 12, 12, 0.55)),       // a 0.55 m dais
        });
        Check.Eq(added, 4, "the table, the rail, the low beam and the dais go on the map; the high beam and the bench don't");

        // The table: the old cuts (0.5, 1.0, 1.5 m) met its legs only.
        Check.Eq(Info(geo, 0x7E7E0011).Class, PlacedClass.Obstacle, "the table: an obstacle");
        Check.True(geo.InsidePlaced(4.8, 4.5, 0), "under the table's middle, 0.7 m from any leg: inside");
        Check.Eq(geo.Clearance(4.8, 4.5, 0), 0.0, "there: no room");
        Check.Near(Info(geo, 0x7E7E0011).FootprintArea, 1.6, 0.25, "its footprint is its top (1.6 m2)");

        // The rail: no cut height fell on it.
        Check.True(geo.InsidePlaced(11, 4.025, 0), "on the rail: inside");
        Check.False(geo.SegmentClear(new[] { Cell }, 11, 2, 0, 11, 6, 0, 0.3), "a straight line across the rail: not clear");
        Check.Near(geo.Clearance(11, 4.6, 0), 0.55, 0.15, "half a metre off the rail: half a metre of room");

        // Beams: the body's top is 1.83 m.
        Check.True(geo.InsidePlaced(15, 4.15, 0), "under the beam at 1.6 m: in the way of a body's head");
        Check.Eq(Info(geo, 0x7E7E0013).Class, PlacedClass.Obstacle, "the low beam: an obstacle");
        Check.False(geo.InsidePlaced(15, 8.15, 0), "under the beam at 2.0 m: free");
        Check.Eq(Info(geo, 0x7E7E0014).Class, PlacedClass.Overhead, "the high beam: overhead, left out");
        Check.True(geo.Clearance(15, 8.15, 0) > 1.0, $"under it: room ({geo.Clearance(15, 8.15, 0):F2})");

        // Low things.
        Check.Eq(Info(geo, 0x7E7E0015).Class, PlacedClass.Low, "the 0.4 m bench: low, left out");
        Check.False(geo.InsidePlaced(5, 10.25, 0), "on the bench's spot: not inside");
        var dais = Info(geo, 0x7E7E0016);
        Check.Eq(dais.Class, PlacedClass.StepOver, "the 0.55 m dais: a step-over (under the 0.60 m step-up, a top to stand on)");
        Check.True(dais.OnMap && geo.InsidePlaced(11, 11, 0), "still in the way in this wave (the step-up isn't proven in game)");
    }

    private static void Shapes()
    {
        var fence = new[] { V(3, 12, 0), V(7, 16, 0), V(7, 16, 1.5), V(3, 12, 0), V(7, 16, 1.5), V(3, 12, 1.5) };   // one upright plane, open
        var ramp = Ramp(12, 14, 16, 16, 2.0);
        var objs = new (uint, uint, Vector3[])[] { (0x7E7E0021, Cell, fence), (0x7E7E0022, Cell, ramp) };
        var geo = Room();
        geo.AddPlacedObjects(objs);
        Check.True(geo.InsidePlaced(5, 14, 0), "on the fence's line: inside");
        // Even-odd over the open cut (one segment) filled the half of its box on one side: (4, 15) was inside.
        Check.False(geo.InsidePlaced(4, 15, 0) || geo.InsidePlaced(6, 13, 0), "1.4 m off it on either side: not inside");
        Check.True(geo.Clearance(4, 15, 0) >= 1.2, $"there: its line is a wall 1.4 m off ({geo.Clearance(4, 15, 0):F2})");
        double area = Info(geo, 0x7E7E0021).FootprintArea;
        Check.True(area > 0.2 && area < 1.5, $"its footprint is its line ({area:F2} m2; half its box is 8 m2)");

        var r = Info(geo, 0x7E7E0022);
        Check.Eq(r.Class, PlacedClass.Connector, "the ramp: a connector (walked onto from its foot, 2 m up)");
        Check.True(r.OnMap && geo.InsidePlaced(15, 15, 0), "on the map still (taking a ramp's sides out was measured wrong in 0x0174)");
        Check.Near(r.Floor, 0, 0.01, "its floor (L): the room's");

        // A map that leaves the connectors out (a building's stairs, next).
        var geo2 = Room();
        int n = geo2.AddPlacedObjects(objs, new PlacedObjectOptions { Keep = p => p.Class != PlacedClass.Connector });
        Check.Eq(n, 1, "only the fence goes on the map");
        Check.False(Info(geo2, 0x7E7E0022).OnMap || geo2.InsidePlaced(15, 15, 0), "the ramp is listed, not on the map");
        Check.Eq(geo2.PlacedObjects.Count, 2, "both listed, with their classes");
    }

    private static void Floors()
    {
        // A balcony 4 m up and a ledge 2 m up over parts of a room, an altar on each.
        var geo = Map((Cell, Rect(0, 0, 20, 20, 0)), (Upper, Rect(0, 0, 8, 8, 4)), (Ledge, Rect(12, 0, 20, 8, 2)));
        geo.AddPlacedObjects(new (uint, uint, Vector3[])[]
        {
            (0x7E7E0031, Upper, Box(2, 2, 4, 3, 3, 5)),
            (0x7E7E0032, Ledge, Box(14, 2, 2, 15, 3, 3)),
        });
        Check.Near(Info(geo, 0x7E7E0031).Floor, 4, 0.01, "the balcony's altar stands on the balcony");
        Check.True(geo.InsidePlaced(2.5, 2.5, 4), "on the balcony: inside it");
        Check.False(geo.InsidePlaced(2.5, 2.5, 0), "on the floor 4 m under it: not inside");
        Check.Near(Info(geo, 0x7E7E0032).Floor, 2, 0.01, "the ledge's altar stands on the ledge");
        Check.True(geo.InsidePlaced(14.5, 2.5, 2), "on the ledge: inside it");
        // The old rule counted it for any floor within 2 m of its own.
        Check.False(geo.InsidePlaced(14.5, 2.5, 0), "on the floor 2 m under it: not inside (its bottom is over a body's top)");
        Check.True(geo.Clearance(14.5, 2.5, 0) > 1.0 && geo.Clearance(2.5, 2.5, 0) > 1.0, "room under both");

        // An object reaching from cell A into cell B: CrossRoom's string-pull reads the walls of the cells on its path only.
        var two = Map((Cell, Rect(0, 0, 10, 10, 0)), (CellB, Rect(10, 0, 20, 10, 0)));
        two.AddPlacedObjects(new (uint, uint, Vector3[])[] { (0x7E7E0033, Cell, Box(8, 4, 0, 12, 6, 1)) });
        Check.False(two.SegmentClear(new[] { CellB }, 11, 1, 0, 11, 9, 0, 0.3), "over cell B through its part there: not clear (it was walled under cell A only)");
        Check.False(two.SegmentClear(new[] { Cell }, 9, 1, 0, 9, 9, 0, 0.3), "over cell A through it: not clear");
        Check.True(two.SegmentClear(new[] { CellB }, 15, 1, 0, 15, 9, 0, 0.3), "over cell B clear of it: clear");
    }

    // (x, y) turned by deg about (ox, oy).
    private static Vector3 Turn(Vector3 p, double ox, double oy, double deg, double dx, double dy)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a), x = p.X - ox, y = p.Y - oy;
        return V(ox + dx + x * c - y * s, oy + dy + x * s + y * c, p.Z);
    }

    private static void Copies()
    {
        var geo = Room();
        var altar = Box(3, 3, 0, 5, 3.6, 1.0);
        var turned = altar.Select(p => Turn(p, 3, 3, 30, 7, 7)).ToArray();   // the same, turned 30 degrees and moved to (10, 10)
        int before = PlacedSolid.CachedModels;
        geo.AddPlacedObjects(new (uint, uint, Vector3[])[] { (0x7E7E0041, Cell, altar), (0x7E7E0041, Cell, turned) });
        Check.Eq(PlacedSolid.CachedModels - before, 1, "one model for both copies");
        Check.True(geo.InsidePlaced(4, 3.3, 0), "inside the first");
        var inTurned = Turn(V(4, 3.3, 0), 3, 3, 30, 7, 7);        // its middle, turned with it
        var offTurned = Turn(V(4.4, 2.5, 0), 3, 3, 30, 7, 7);     // off its side, still in its box
        Check.True(geo.InsidePlaced(inTurned.X, inTurned.Y, 0), "inside the turned copy at its middle");
        Check.False(geo.InsidePlaced(offTurned.X, offTurned.Y, 0), "off its turned side, inside its world box: not inside");

        // The overlay (wave 2: objects found at run time) joins and leaves without the base built again.
        var room = Room();
        Check.Near(room.ClearanceMemo(10, 11.6, 0, 3.0), 3.0, 0.01, "an empty room: 3 m of room");
        int n = room.SetPlacedOverlay(new (uint, uint, Vector3[])[] { (0x7E7E0042, Cell, Box(9, 9.4, 0, 11, 10.6, 1.0)) }, new PlacedObjectOptions { Source = "test" });
        Check.Eq(n, 1, "the overlay's object goes on the map");
        Check.True(room.InsidePlaced(10, 10, 0) && room.PlacedCount == 1, "inside it");
        Check.Near(room.ClearanceMemo(10, 11.6, 0, 3.0), 1.0, 0.15, "its walls count (the remembered clearance dropped)");
        Check.Eq(room.PlacedObjects.Single().Source, "test", "listed with its source");
        room.ClearPlacedOverlay();
        Check.False(room.InsidePlaced(10, 10, 0), "gone with the overlay");
        Check.Near(room.ClearanceMemo(10, 11.6, 0, 3.0), 3.0, 0.01, "and its walls");
    }

    private static void RealDats()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        var before = DungeonPathfinder.StaticMeshSource;
        try
        {
            DungeonPathfinder.StaticMeshSource = lb => loader.GetDungeonStaticMeshes(lb);
            DungeonGeometry Geo(uint lb)
            {
                DungeonPathfinder.InvalidateCache();
                DungeonPathfinder.GetGraph(lb, loader.CellDat);
                return DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            }
            // Inside area over an object's world box on its own floor, 0.1 m grid.
            static double InsideArea(DungeonGeometry geo, PlacedInfo p)
            {
                int n = 0;
                for (double x = p.MinX + 0.05; x < p.MaxX; x += 0.1)
                    for (double y = p.MinY + 0.05; y < p.MaxY; y += 0.1)
                        if (geo.InsidePlaced(x, y, p.Floor)) n++;
                return n * 0.01;
            }

            // Tables 0.81 m tall on legs: the old cuts met the legs at 0.5 m, 1.5 of their 2.4 m2 missed.
            foreach (var (lb, setup) in new[] { (0x0007u, 0x01003694u), (0x00CEu, 0x020012A0u) })
            {
                var geo = Geo(lb);
                var tables = geo.PlacedObjects.Where(p => p.Setup == setup && p.FloorFound).ToList();
                Check.True(tables.Count > 0 && tables.All(p => p.OnMap && p.Class == PlacedClass.Obstacle), $"0x{lb:X4}: its {tables.Count} 0x{setup:X8} tables are obstacles on the map");
                foreach (var t in tables.Take(3))
                {
                    double area = InsideArea(geo, t);
                    Console.WriteLine($"    0x{lb:X4} 0x{setup:X8} at ({(t.MinX + t.MaxX) / 2:F1},{(t.MinY + t.MaxY) / 2:F1},{t.Floor:F1}): inside {area:F2} m2, footprint {t.FootprintArea:F2} m2");
                    Check.True(area >= 2.0, $"0x{lb:X4} 0x{setup:X8}: inside under its whole top ({area:F2} m2 of about 2.4)");
                    Check.True(geo.InsidePlaced((t.MinX + t.MaxX) / 2, (t.MinY + t.MaxY) / 2, t.Floor), "its middle: inside");
                }
            }

            // The rail at 0.79 to 0.92 m between posts (0x0137): no cut fell on it, now it is a wall.
            {
                var geo = Geo(0x0137);
                var rails = geo.PlacedObjects.Where(p => p.Setup == 0x020003B3).ToList();
                int on = rails.Count(p => p.OnMap);
                Console.WriteLine($"    0x0137: {on} of {rails.Count} 0x020003B3 rails on the map");
                Check.True(rails.Count > 100 && on >= rails.Count * 0.9, $"0x0137: the 0x020003B3 rails are on the map ({on} of {rails.Count})");
                var r = rails.First(p => p.OnMap);
                double mx = (r.MinX + r.MaxX) / 2, my = (r.MinY + r.MaxY) / 2;
                Check.True(geo.InsidePlaced(mx, my, r.Floor), "on a rail's middle: inside");
                Check.True(geo.WallsNear(mx, my, 0.3).Any(w => w.ZMin <= r.Floor + 0.85 && w.ZMax >= r.Floor + 0.85), "its walls beside it, at its height");
            }

            // 0x02001A58 (an open mesh, 10 x 10 x 6 m): even-odd over its open cuts filled 31.5 m2; its real footprint is 11.3.
            {
                var geo = Geo(0x7E02);
                var all = geo.PlacedObjects.Where(p => p.Setup == 0x02001A58 && p.OnMap).ToList();
                Check.True(all.Count > 0, $"0x7E02: its 0x02001A58 on the map ({all.Count})");
                foreach (var p in all.Take(3))
                {
                    double area = InsideArea(geo, p);
                    Console.WriteLine($"    0x7E02 0x02001A58@{p.Cell & 0xFFFF:X4}: inside {area:F2} m2, footprint {p.FootprintArea:F2} m2");
                    Check.True(area > 6 && area < 16, $"0x7E02 0x02001A58@{p.Cell & 0xFFFF:X4}: inside {area:F2} m2, near its 11.3 m2 footprint");
                }
            }

            // E74E: the steps under door sills 0186 and 0196 (their top, 31.99, the door's floor at 32; their own floor the
            // ground at 30.8) read as inside the doors' inner points (2-building-interiors.json: the building map's floors with
            // the cells' objects added, as here; the building map itself takes no objects yet). The map has no floor on the
            // steps, so a clearance there is measured on the ground 1.2 m under them, inside them: the steps are tagged
            // connectors for the building map to leave out or lay as floor (next wave).
            {
                var blk = BuildingMap.Load(0xE74E, loader.CellDat, loader.DungeonLOS);
                var doors = blk.Buildings.SelectMany(b => b.Doors).Where(d => d.Cell is 0xE74E0186 or 0xE74E0196).ToList();
                if (doors.Count == 0) Console.WriteLine("    (E74E: doors 0186/0196 not found: skipped)");
                var geo = blk.Geo!;
                geo.AddPlacedObjects(loader.GetDungeonStaticMeshes(0xE74E).Where(s => s.Mesh.MeshTriangles is { Length: >= 3 }).Select(s => (s.SetupId, s.CellId, s.Mesh.MeshTriangles)));
                foreach (var d in doors)
                {
                    var near = geo.PlacedObjects.Where(p => p.OnMap && d.Inner.X >= p.MinX - 0.5 && d.Inner.X <= p.MaxX + 0.5 && d.Inner.Y >= p.MinY - 0.5 && d.Inner.Y <= p.MaxY + 0.5).ToList();
                    Console.WriteLine($"    E74E door {d.Cell & 0xFFFF:X4}: inner ({d.Inner.X:F2},{d.Inner.Y:F2},{d.Inner.Z:F2}); near: "
                                      + string.Join(", ", near.Select(o => $"{o.Setup:X8} {o.Class} floor {o.Floor:F2} top {o.MaxZ:F2}")));
                    Check.False(geo.InsidePlaced(d.Inner.X, d.Inner.Y, d.Inner.Z), $"E74E door {d.Cell & 0xFFFF:X4}: its inner point, on the door's floor, is not inside the steps under its sill");
                    Check.True(near.Any(p => p.Class == PlacedClass.Connector && Math.Abs(p.MaxZ - d.Inner.Z) < 0.1), "the steps up to the sill: a connector");
                }
            }
        }
        finally
        {
            DungeonPathfinder.StaticMeshSource = before;
            DungeonPathfinder.InvalidateCache();
            loader.Dispose();
        }
    }
}
