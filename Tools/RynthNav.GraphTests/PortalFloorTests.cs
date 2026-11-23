using System;
using System.IO;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using RynthNav.Routing;



// 11-18: the goto stood 1 u from Zaikhal's Olthoi Tunnel, 7.7 u above it on the street, and circled it:
// the goal was looked up from the walker's own height, so the nearest polygon was the street right above
// the portal, not the tunnel floor it stands on. NavPathQuery.PortalFloor finds the floor by the portal's
// own height, and Reaches says whether a walk gets there (that tunnel's floor is a piece of its own: no).
internal static partial class Program
{
    private static void RunPortalFloors()
    {
        Run(nameof(RealPortalFloors), RealPortalFloors);
    }

    private static DtNavMeshQuery? Around(string dir, uint lb)
    {
        if (!File.Exists(Path.Combine(dir, $"nav_{lb:X4}.tile"))) return null;
        var nav = new DtNavMesh();
        nav.Init(new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 64, maxPolys = 1 << 16 }, 6);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                uint t = (uint)((((int)(lb >> 8) + dx) << 8) | ((int)(lb & 0xFF) + dy));
                if (File.Exists(Path.Combine(dir, $"nav_{t:X4}.tile"))) nav.AddTile(ReadTile(dir, t), 0, 0, out _);
            }
        return new DtNavMeshQuery(nav);
    }

    private static void RealPortalFloors()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        DtNavMeshQuery? q = Around(dir, 0x7F8F);
        if (q == null) { Console.WriteLine("     (no real NavData here: skipped)"); return; }
        var filter = new DtQueryDefaultFilter();

        // Zaikhal's Olthoi Tunnel: cell 7F8F0123 at (83.92, 21.12, 118.34); the street above is ~126.
        double e = 0x7F * 192 + 83.92, n = 0x8F * 192 + 21.12, z = 118.34;
        long floor = NavPathQuery.PortalFloor(q, filter, e, z, n, out RcVec3f fPt);
        Check(floor != 0 && Math.Abs(fPt.Y - z) < 1.0, $"the tunnel floor is found by the portal's height ({(floor != 0 ? fPt.Y.ToString("F1") : "none")})");
        q.FindNearestPoly(new RcVec3f((float)e, 126.5f, (float)n), new RcVec3f(3, 1.5f, 3), filter, out long street, out RcVec3f sPt, out _);
        Check(street != 0 && sPt.Y > z + 5, $"the street right above it ({(street != 0 ? sPt.Y.ToString("F1") : "none")})");
        // The old lookup, from the walker's height on the street: the street, not the floor.
        q.FindNearestPoly(new RcVec3f((float)e, sPt.Y, (float)n), new RcVec3f(12, 256, 12), filter, out long old, out RcVec3f oPt, out _);
        Check(old != 0 && old != floor && oPt.Y > z + 5, $"looked up from the street, the goal was the street above ({oPt.Y:F1}): the 11-18 circling");
        Check(!NavPathQuery.Reaches(q, street, sPt, floor, fPt, filter), "no walk from the street reaches that floor: use it from above, then give up and plan another way");
        Check(NavPathQuery.Reaches(q, floor, fPt, floor, fPt, filter), "a floor reaches itself");

        // A portal in a building on the ground: Town of Dryreach (3B4D0104), its floor joined to the land round it.
        DtNavMeshQuery? q2 = Around(dir, 0x3B4D);
        if (q2 == null) { Console.WriteLine("     (no tile for 3B4D: the building part skipped)"); return; }
        double e2 = 0x3B * 192 + 38.63, n2 = 0x4D * 192 + 130.22, z2 = 2.0;
        long f2 = NavPathQuery.PortalFloor(q2, filter, e2, z2, n2, out RcVec3f f2Pt);
        Check(f2 != 0, "the Dryreach portal's floor is found");
        var rnd = new RcRand(1119);
        int reached = 0, tries = 0;
        for (int i = 0; i < 10 && f2 != 0; i++)
        {
            // Ground up to 40 u away that a walk from that floor gets to: from there, back to the floor.
            if (q2.FindRandomPointAroundCircle(f2, f2Pt, 40f, filter, rnd, out long r, out RcVec3f rPt).Failed() || r == 0) continue;
            tries++;
            if (NavPathQuery.Reaches(q2, r, rPt, f2, f2Pt, filter)) reached++;
        }
        Check(tries > 0 && reached == tries, $"walks from the ground round it reach that floor ({reached}/{tries})");
    }
}
