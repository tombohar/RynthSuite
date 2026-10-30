using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Detector C, "Avoid Lava/Acid Floors" (2026-10-23): a cell is a hazard when a texture in
// HazardTextures.txt is on its walkable FLOOR (a render polygon facing up at least as steeply as
// AC's FloorZ 0.66417), not merely in its surface palette. Before, the palette test flagged cells
// for their walls: Tiny Hive (0x0289) on +Buffy patrolled 2 waypoints, because the learned
// 0x0500279D is the Olthoi hive wall texture (on walls in 3,530 cells of the dats, on a floor in
// none) and 12 cells round the entrance were "hazards". Learning (/ra hazard learnhere) looks at
// floors too, so it can't learn a wall texture again.
//
// Synthetic polygons for the rule; the real Tiny Hive cells when the dat files are on this PC
// (C:\Turbine\Asheron's Call, as DungeonMapBakeTests and FloodPerfTests). The texture set lives in
// the scratch folder (Program.Main points DungeonHazardSurfaces there).
internal static class HazardFloorTests
{
    private const string AcDir = @"C:\Turbine\Asheron's Call";

    public static void Register(Runner r)
    {
        r.Add("hazard floors: a polygon facing up at FloorZ or more is floor; walls, ceilings, steep slopes are not", WalkableFloor);
        r.Add("hazard floors: a cell's floor surfaces are its floor polygons', not its walls', ceiling's or portals'", FloorSurfaces);
        r.Add("hazard floors: a hazard texture on a cell's wall doesn't make it a hazard; on its floor it does", WallVersusFloor);
        r.Add("hazard floors: learnhere takes a rare floor texture, never a wall texture", LearnIgnoresWalls);
        r.Add("hazard floors: Tiny Hive 0x0289 on the real dats - the wall texture flags nothing, the floor texture its floors", TinyHiveRealDats);
    }

    // ── Synthetic geometry ──────────────────────────────────────────────────

    private static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

    // A 2 x 2 square at height z, counter-clockwise seen from above: faces up.
    private static Vector3[] FloorSquare(float z = 0) => new[] { V(0, 0, z), V(2, 0, z), V(2, 2, z), V(0, 2, z) };

    // The same square wound the other way: faces down (a ceiling).
    private static Vector3[] CeilingSquare(float z = 3) => FloorSquare(z).Reverse().ToArray();

    // Upright, facing -y.
    private static Vector3[] WallSquare() => new[] { V(0, 0, 0), V(0, 0, 3), V(2, 0, 3), V(2, 0, 0) };

    // A ramp rising along +x at the given angle from horizontal, facing up.
    private static Vector3[] Ramp(double degrees)
    {
        float rise = (float)(2 * Math.Tan(degrees * Math.PI / 180));
        return new[] { V(0, 0, 0), V(2, 0, rise), V(2, 2, rise), V(0, 2, 0) };
    }

    private static (IReadOnlyList<Vector3> Verts, short PosSurface, bool IsPortal) P(Vector3[] v, short surf, bool portal = false) => (v, surf, portal);

    private static void WalkableFloor()
    {
        Check.True(DungeonLOS.IsWalkableFloor(FloorSquare()), "flat floor");
        Check.False(DungeonLOS.IsWalkableFloor(CeilingSquare()), "ceiling (faces down)");
        Check.False(DungeonLOS.IsWalkableFloor(WallSquare()), "upright wall");
        Check.True(DungeonLOS.IsWalkableFloor(Ramp(30)), "30 degree ramp (nz 0.87, Tiny Hive's bowl floors are 0.81-0.86)");
        Check.True(DungeonLOS.IsWalkableFloor(Ramp(45)), "45 degree ramp (nz 0.71)");
        Check.False(DungeonLOS.IsWalkableFloor(Ramp(50)), "50 degree slope (nz 0.64, steeper than FloorZ)");
        Check.False(DungeonLOS.IsWalkableFloor(new[] { V(0, 0, 0), V(1, 1, 0) }), "two points");
        Check.False(DungeonLOS.IsWalkableFloor(new[] { V(0, 0, 0), V(1, 1, 0), V(2, 2, 0) }), "collinear");
        Check.Near(DungeonLOS.FloorZ, 0.66417, 1e-5, "FloorZ is AC's PhysicsGlobals.FloorZ");
    }

    // Palette: 0 = wall surface, 1 = floor surface, 2 = ceiling surface, 3 = portal surface, 4 = unused.
    private const ushort SWall = 0x04C7, SFloor = 0x0112, SCeil = 0x0113, SPortal = 0x0034, SUnused = 0x04C6;
    private static readonly ushort[] Palette = { SWall, SFloor, SCeil, SPortal, SUnused };

    private static void FloorSurfaces()
    {
        var polys = new[]
        {
            P(WallSquare(), 0), P(WallSquare(), 0),
            P(FloorSquare(), 1), P(Ramp(30), 1),
            P(CeilingSquare(), 2),
            P(FloorSquare(), 3, portal: true),          // a floor-level portal (a shaft) is an opening
            P(FloorSquare(), -1),                       // no front surface
            P(FloorSquare(), 9),                        // index past the palette
        };
        var floor = DungeonLOS.FloorSurfaceIds(Palette, polys);
        Check.Eq(string.Join(",", floor.Select(s => s.ToString("X4"))), SFloor.ToString("X4"), "only the floor's surface");

        // A cap cell: ceiling and portals only - no floor surfaces at all.
        Check.Eq(DungeonLOS.FloorSurfaceIds(Palette, new[] { P(CeilingSquare(), 2), P(FloorSquare(), 3, portal: true) }).Count, 0, "ceiling-only cell has no floor");
        Check.Eq(DungeonLOS.FloorSurfaceIds(null!, polys).Count, 0, "no palette");
    }

    // Surface -> texture as portal.dat would resolve it.
    private const uint TLava = 0x0500AAA1, TStone = 0x0500AAA2, TWallRare = 0x0500AAA3, TCeil = 0x0500AAA4;

    private static HashSet<uint> Textures(ushort[] palette, (IReadOnlyList<Vector3>, short, bool)[] polys, Dictionary<ushort, uint> surfTex)
        => DungeonLOS.FloorSurfaceIds(palette, polys).Select(s => surfTex[s]).ToHashSet();

    private static void ResetSet()
    {
        DungeonHazardSurfaces.Reload();
        foreach (uint t in DungeonHazardSurfaces.ListHazardTextures()) DungeonHazardSurfaces.RemoveHazardTexture(t);
    }

    private static void WallVersusFloor()
    {
        Check.True(DungeonHazardSurfaces.Folder.StartsWith(Program.TempRoot, StringComparison.OrdinalIgnoreCase), "texture set is in the scratch folder: " + DungeonHazardSurfaces.Folder);
        ResetSet();
        try
        {
            const ushort SHz = 0x0700, SStone = 0x0701;
            var surfTex = new Dictionary<ushort, uint> { [SHz] = TLava, [SStone] = TStone };
            ushort[] pal = { SHz, SStone };
            var floors = new Dictionary<uint, HashSet<uint>>
            {
                // A: hazard texture on the walls, stone floor.
                [0xA] = Textures(pal, new[] { P(WallSquare(), 0), P(FloorSquare(), 1) }, surfTex),
                // B: stone walls, hazard texture on the floor.
                [0xB] = Textures(pal, new[] { P(WallSquare(), 1), P(FloorSquare(), 0) }, surfTex),
                // C: hazard texture in the palette and on the ceiling only.
                [0xC] = Textures(pal, new[] { P(CeilingSquare(), 0), P(FloorSquare(), 1) }, surfTex),
                // D: part of the floor is the hazard texture (a pool in a stone room).
                [0xD] = Textures(pal, new[] { P(FloorSquare(), 1), P(FloorSquare(), 0) }, surfTex),
            };
            Check.True(DungeonHazardSurfaces.AddHazardTexture(TLava), "flag the texture");
            Check.True(File.Exists(Path.Combine(Program.TempRoot, "HazardTextures", "HazardTextures.txt")), "persisted to the scratch folder");
            var hz = DungeonHazardSurfaces.HazardCellsFrom(floors);
            Check.False(hz.Contains(0xA), "wall uses the hazard texture: not a hazard");
            Check.True(hz.Contains(0xB), "floor uses the hazard texture: hazard");
            Check.False(hz.Contains(0xC), "ceiling uses the hazard texture: not a hazard");
            Check.True(hz.Contains(0xD), "part of the floor uses it: hazard");
            Check.Eq(hz.Count, 2, "two hazard cells");
        }
        finally { ResetSet(); }
    }

    private static void LearnIgnoresWalls()
    {
        ResetSet();
        try
        {
            const ushort SStone = 0x0701, SWallRare = 0x0702, SLava = 0x0703, SCeilS = 0x0704;
            var surfTex = new Dictionary<ushort, uint> { [SStone] = TStone, [SWallRare] = TWallRare, [SLava] = TLava, [SCeilS] = TCeil };
            ushort[] pal = { SStone, SWallRare, SLava, SCeilS };
            var floors = new Dictionary<uint, HashSet<uint>>();
            // 20 ordinary rooms: stone floor and walls, ceiling texture overhead.
            for (uint c = 0x100; c < 0x114; c++)
                floors[c] = Textures(pal, new[] { P(FloorSquare(), 0), P(WallSquare(), 0), P(CeilingSquare(), 3) }, surfTex);
            // A room with a rare WALL texture (only this cell has it) and the common stone floor.
            const uint RareWallRoom = 0x200;
            floors[RareWallRoom] = Textures(pal, new[] { P(FloorSquare(), 0), P(WallSquare(), 1) }, surfTex);
            // A lava room: lava floor, stone walls.
            const uint LavaRoom = 0x201;
            floors[LavaRoom] = Textures(pal, new[] { P(FloorSquare(), 2), P(WallSquare(), 0) }, surfTex);

            Check.Eq(DungeonHazardSurfaces.RareFloorTextures(RareWallRoom, floors, 0.25).Count, 0, "rare wall texture is not learned (its floor is common stone)");
            var lava = DungeonHazardSurfaces.RareFloorTextures(LavaRoom, floors, 0.25);
            Check.Eq(string.Join(",", lava.Select(t => t.ToString("X8"))), TLava.ToString("X8"), "the lava room's floor texture is learned");
            Check.Eq(DungeonHazardSurfaces.RareFloorTextures(0x100, floors, 0.25).Count, 0, "a common floor is never learned");
            Check.Eq(DungeonHazardSurfaces.RareFloorTextures(0x999, floors, 0.25).Count, 0, "a cell without floor data learns nothing");
            Check.False(floors.Values.Any(s => s.Contains(TWallRare) || s.Contains(TCeil)), "wall and ceiling textures never reach the floor map");
        }
        finally { ResetSet(); }
    }

    // ── The real dats ───────────────────────────────────────────────────────

    private static GeometryLoader? _geo;
    private static bool _geoTried;

    private static GeometryLoader? Geo()
    {
        if (!_geoTried)
        {
            _geoTried = true;
            if (Directory.Exists(AcDir))
            {
                var g = new GeometryLoader();
                if (g.Initialize(AcDir)) _geo = g;
                else Console.WriteLine("    geometry loader failed: " + g.StatusMessage);
            }
        }
        return _geo;
    }

    private const uint TinyHive = 0x0289;
    private const uint HiveFloor = 0x0500279C, HiveWall = 0x0500279D;   // the two ids in the live HazardTextures.txt

    private static string Ids(IEnumerable<uint> cells) => string.Join(",", cells.OrderBy(c => c).Select(c => (c & 0xFFFF).ToString("X4")));

    private static void TinyHiveRealDats()
    {
        var geo = Geo();
        if (geo?.DungeonLOS is null || geo.PortalDat is null) { Console.WriteLine("    skipped: no dats at " + AcDir); return; }
        var los = geo.DungeonLOS;
        ResetSet();
        try
        {
            var floors = DungeonHazardSurfaces.GetFloorTextures(TinyHive, los, geo.PortalDat);
            Check.True(floors.Count > 300, $"floor textures for most of Tiny Hive's 450 cells ({floors.Count})");
            Check.False(floors.Values.Any(s => s.Contains(HiveWall)), "0x0500279D is on no floor in Tiny Hive");

            // The wall texture alone flags nothing (before: 02A2-02B0, the 12 cells round the entrance).
            DungeonHazardSurfaces.AddHazardTexture(HiveWall);
            var wallOnly = DungeonHazardSurfaces.ComputeHazardCells(TinyHive, los, geo.PortalDat);
            Console.WriteLine($"    0x0500279D flagged: {wallOnly.Count} cells {Ids(wallOnly)}");
            Check.Eq(wallOnly.Count, 0, "wall texture flags no cell");

            // The floor texture flags the cells it's the floor of: 02A6 (walls only), 02A8 and
            // 02A9 (in the palette, on no polygon / a different floor) are not among them.
            DungeonHazardSurfaces.AddHazardTexture(HiveFloor);
            var floorCells = DungeonHazardSurfaces.ComputeHazardCells(TinyHive, los, geo.PortalDat);
            Console.WriteLine($"    + 0x0500279C flagged: {floorCells.Count} cells {Ids(floorCells)}");
            Check.Eq(Ids(floorCells), "02A2,02A3,02A4,02A7,02AA,02AB,02AE,02AF,02B0", "exactly the cells whose floor is 0x0500279C");
            Check.False(floorCells.Contains(0x028902A5), "the entrance cell 02A5 is not a hazard");

            // Learning standing in 02A6 (hive wall texture on its walls, common floor 0x05002627)
            // takes nothing; before, the palette rule offered both 0x0500279C and 0x0500279D.
            ResetSet();
            var learned = DungeonHazardSurfaces.LearnFromCell(0x028902A6, los, geo.PortalDat);
            Check.Eq(learned.Count, 0, "learnhere in 02A6 learns nothing: " + string.Join(",", learned.Select(t => t.ToString("X8"))));
            Check.Eq(DungeonHazardSurfaces.HazardTextureCount, 0, "and the set stays empty");
        }
        finally { ResetSet(); }
    }
}
