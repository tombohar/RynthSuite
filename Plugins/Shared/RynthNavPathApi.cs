// ============================================================================
//  RynthSuite - Plugins/Shared/RynthNavPathApi.cs
//  The "RynthNav.Path" interface: RynthNav's landscape navmesh offered to other
//  plugins through the host's GetPluginInterface (API v68). RynthAi uses it to
//  plan a way back to its route when it gets stuck outdoors. Compiled into both
//  plugins (linked source), so the layout can't drift between them.
//
//  Rules (same as RynthAi.Script):
//  - Append-only: new functions go at the end, and Version goes up. A caller
//    checks StructSize before using a field past the ones it knows.
//  - Call only from the plugin pump thread (OnTick and the events it drains).
//    RynthNav touches its navmesh only on that thread.
//  - Resolve the table again every time it is needed; never keep the pointer
//    across ticks. RynthNav may not be loaded, or may be reloaded.
//  - Every function returns safely (no exception crosses the boundary).
//
//  World units: x = east  (landblockX * 192 + local x),
//               y = north (landblockY * 192 + local y),
//               z = height (the pose z).
// ============================================================================

using System.Runtime.InteropServices;

namespace RynthCore.Plugin.Shared;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RynthNavPathApiV1
{
    public const string PluginName = "RynthNav";
    public const string InterfaceName = "RynthNav.Path";
    public const uint InterfaceVersion = 3;

    /// <summary>FindPath: a full path was found.</summary>
    public const int ResultOk = 1;
    /// <summary>FindPath: both ends are on the navmesh but the goal can't be reached (or only part of the way).</summary>
    public const int ResultNoPath = 0;
    /// <summary>FindPath: no navmesh at one of the ends (tile not baked, indoors, or no pose yet).</summary>
    public const int ResultNoMesh = -1;

    /// <summary>The table's size up to FindPath (version 1): what a v1 caller checks StructSize against,
    /// so an older RynthNav's smaller table still passes.</summary>
    public static uint SizeV1 => (uint)(2 * sizeof(uint) + sizeof(nint));

    public uint Version;      // InterfaceVersion of the table RynthNav filled
    public uint StructSize;   // sizeof(RynthNavPathApiV1) as RynthNav built it

    /// <summary>
    /// Walkable path from (sx, sy, sz) to (gx, gy, gz) on the landscape navmesh.
    /// Writes the path corners as x,y,z triples into <c>points</c> (room for
    /// <c>maxPoints</c> corners, so 3 * maxPoints doubles), start first and goal last,
    /// and the corner count into <c>*count</c>. Returns ResultOk, ResultNoPath or ResultNoMesh.
    /// </summary>
    public delegate* unmanaged[Cdecl]<double, double, double, double, double, double, double*, int, int*, int> FindPath;

    // ── Version 2 (2026-10-23): RynthAi's outdoor hunting walks to monsters on these ──

    /// <summary>FindPathEx flag: keep off portals (their circles) and bend around them, as RynthNav's own walks do.</summary>
    public const int PathAvoidPortals = 1;
    /// <summary>FindPathEx flag: push corners out from walls by RynthNav's wall clearance, as its own walks do.</summary>
    public const int PathWallClearance = 2;
    /// <summary>FindPathEx flag: when the goal can't be reached, a path to the reachable point nearest it (ResultPartial).</summary>
    public const int PathAllowPartial = 4;
    /// <summary>FindPathEx flag: no log line for this query (a caller asking several times a second).</summary>
    public const int PathQuiet = 8;
    /// <summary>FindPathEx with PathAllowPartial: the path ends at the reachable point nearest the goal (info[2..4]), info[5] from it.</summary>
    public const int ResultPartial = 2;
    /// <summary>Raycast: the straight walk stays on the navmesh.</summary>
    public const int RayClear = 1;
    /// <summary>Raycast: something is in the way (a wall, a building, a drop, the mesh's edge).</summary>
    public const int RayBlocked = 0;
    /// <summary>FindPathEx: doubles written to <c>info</c>.</summary>
    public const int InfoLength = 6;

    /// <summary>
    /// FindPath with options: (sx, sy, sz) to (gx, gy, gz), <c>flags</c> (Path* above), <c>maxTiles</c> the
    /// tile box it may load (0 = RynthNav's default of 25), the corners into <c>points</c> / <c>*count</c> as
    /// FindPath, and <c>info</c> (InfoLength doubles): [0] path length (the sum of its legs), [1] 1 when
    /// partial, [2..4] the path's last corner, [5] the distance from that corner to the goal. Returns
    /// ResultOk, ResultPartial (PathAllowPartial only), ResultNoPath or ResultNoMesh; ResultNoMesh also in a
    /// dungeon or a building's cells (the landscape mesh doesn't hold them).
    /// </summary>
    public delegate* unmanaged[Cdecl]<double, double, double, double, double, double, int, int, double*, int, int*, double*, int> FindPathEx;

    /// <summary>
    /// Whether walking straight from (sx, sy, sz) to (gx, gy, gz) stays on the navmesh (a Detour raycast
    /// along the mesh surface): RayClear, RayBlocked or ResultNoMesh.
    /// </summary>
    public delegate* unmanaged[Cdecl]<double, double, double, double, double, double, int> Raycast;

    // ── Version 3 (2026-10-25): RynthAi's hunt walks out of buildings through their doors ──
    // No new functions: a v3 RynthNav knows this flag (an older one ignores it and answers ResultNoMesh
    // for a start near the player while the player is in a building).

    /// <summary>
    /// FindPathEx flag: the start is a spot on the landscape (a building's doorstep), planned from even
    /// while the player stands in a building's cells. Without it a start within 24 units of the player
    /// is the player's own spot and, in a building, refused (ResultNoMesh). In a dungeon it is refused
    /// either way.
    /// </summary>
    public const int PathStartOutdoors = 16;
}
