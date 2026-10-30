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
    public const uint InterfaceVersion = 1;

    /// <summary>FindPath: a full path was found.</summary>
    public const int ResultOk = 1;
    /// <summary>FindPath: both ends are on the navmesh but the goal can't be reached (or only part of the way).</summary>
    public const int ResultNoPath = 0;
    /// <summary>FindPath: no navmesh at one of the ends (tile not baked, indoors, or no pose yet).</summary>
    public const int ResultNoMesh = -1;

    public uint Version;      // InterfaceVersion of the table RynthNav filled
    public uint StructSize;   // sizeof(RynthNavPathApiV1) as RynthNav built it

    /// <summary>
    /// Walkable path from (sx, sy, sz) to (gx, gy, gz) on the landscape navmesh.
    /// Writes the path corners as x,y,z triples into <c>points</c> (room for
    /// <c>maxPoints</c> corners, so 3 * maxPoints doubles), start first and goal last,
    /// and the corner count into <c>*count</c>. Returns ResultOk, ResultNoPath or ResultNoMesh.
    /// </summary>
    public delegate* unmanaged[Cdecl]<double, double, double, double, double, double, double*, int, int*, int> FindPath;
}
