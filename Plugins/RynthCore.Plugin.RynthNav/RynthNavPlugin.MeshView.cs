using System;
using System.Globalization;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// The navmesh on the ground (NavMeshView): green where nav can take you, red walls where the mesh
// ends (buildings, trees, rocks, cliffs, steep slopes, and the margin round them). Drawn on the
// engine's Nav3D buffer like the path markers. Off by default; it is a look-at-it tool, not part
// of walking. Switches in rynthnav.json: meshView, meshFloor, meshWalls, meshRadius, meshTriangles.
// /rnav mesh changes them. In a dungeon it draws NavData\dun_LLLL.tile (a dungeon's own mesh, baked by
// RynthNav.Baker --dungeons), the floor you're on; with no such file it draws nothing there. Not drawn while portaling.
public sealed partial class RynthNavPlugin
{
    private const double MeshRadiusDefault = 40, MeshRadiusMin = 10, MeshRadiusMax = 150;
    private const int MeshTrianglesDefault = 3000, MeshTrianglesMin = 200, MeshTrianglesMax = 7000;

    private volatile bool _meshOn, _meshFloor = true, _meshWalls = true;
    private double _meshRadius = MeshRadiusDefault;
    private int _meshBudget = MeshTrianglesDefault;
    private readonly NavMeshView _meshView = new();
    private volatile int _meshDrawn;

    private void LoadMeshSettings()
    {
        _meshOn = NavDataConfig.ReadSwitch("meshView", false);
        _meshFloor = NavDataConfig.ReadSwitch("meshFloor", true);
        _meshWalls = NavDataConfig.ReadSwitch("meshWalls", true);
        _meshRadius = Math.Clamp(NavDataConfig.ReadNumber("meshRadius", MeshRadiusDefault), MeshRadiusMin, MeshRadiusMax);
        _meshBudget = (int)Math.Clamp(NavDataConfig.ReadNumber("meshTriangles", MeshTrianglesDefault), MeshTrianglesMin, MeshTrianglesMax);
    }

    // A dungeon draws only the floor you're on, give or take: the floors above and below sit metres away.
    private const float MeshDungeonUpDown = 8f;
    private uint _meshDunLb;            // the landblock a dungeon file was last looked for (0 = outdoors / none yet)
    private string _meshDunStatus = "";

    private void TickMeshView()
    {
        if (!_meshOn || !_hasPose || !Host.HasNav3D || !Host.HasNav3DTriangle) { _meshDrawn = 0; return; }
        if ((_cellId >> 16) == 0 || (Host.HasIsPortaling && Host.IsPortaling())) { _meshDrawn = 0; return; }
        try
        {
            // Cells from 0x100 up are buildings or a dungeon. A landblock with a dungeon file (dun_LLLL.tile)
            // is drawn from it; a building in an outdoor landblock keeps the outdoor mesh.
            uint lb = CurrentLandblock;
            bool indoors = (_cellId & 0xFFFF) >= 0x100;
            if (indoors && lb != _meshDunLb)
            {
                _meshDunLb = lb;
                LoadMeshDungeon(lb);
            }
            else if (!indoors && _meshDunLb != 0)
            {
                _meshDunLb = 0;
                _meshView.UnregisterDungeon();
                _meshDunStatus = "";
            }
            bool dungeon = indoors && _meshView.DungeonLandblock == lb;
            NavMeshView.Options opts = dungeon
                ? new NavMeshView.Options((float)_meshRadius, _meshBudget, _meshFloor, _meshWalls, maxUpDown: MeshDungeonUpDown,
                    layer: 1, frameX: ((lb >> 8) & 0xFF) * 192.0, frameY: (lb & 0xFF) * 192.0)
                : new NavMeshView.Options((float)_meshRadius, _meshBudget, _meshFloor, _meshWalls);
            _meshDrawn = _meshView.Emit(_markerSink ??= new HostMarkerSink(Host), _wx, _wy, _wz, opts);
        }
        catch (Exception ex) { _meshDrawn = 0; Host.Log($"[RynthNav] mesh view: {ex.Message}"); }
    }

    /// <summary>Reads NavData\dun_LLLL.tile into the mesh view's dungeon layer (none: the layer is emptied). Tick thread.</summary>
    private void LoadMeshDungeon(uint lb)
    {
        _meshView.UnregisterDungeon();
        string path = System.IO.Path.Combine(NavDataDir, DungeonTileFile.FileName(lb));
        if (!System.IO.File.Exists(path)) { _meshDunStatus = $"no dungeon tile for 0x{lb:X4}"; return; }
        DungeonTileFile? f = DungeonTileFile.Read(path);
        if (f == null) { _meshDunStatus = $"dungeon tile 0x{lb:X4} unreadable"; Host.Log($"[RynthNav] {_meshDunStatus}"); return; }
        _meshView.RegisterDungeon(lb, f);
        _meshDunStatus = $"dungeon 0x{lb:X4}: {f.Tiles.Count} tiles";
        Host.Log($"[RynthNav] mesh view: dungeon 0x{lb:X4} loaded ({f.Tiles.Count} tiles)");
    }

    /// <summary>/rnav mesh [on|off | floor on|off | walls on|off | radius &lt;m&gt; | triangles &lt;n&gt;]</summary>
    private void CmdMesh(string rest)
    {
        string[] a = rest.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool isNum = a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        double num = isNum ? double.Parse(a[1], CultureInfo.InvariantCulture) : 0;
        if (a.Length == 1 && a[0] is "on" or "off")
        {
            _meshOn = a[0] == "on";
            NavDataConfig.WriteSwitch("meshView", _meshOn);
        }
        else if (a.Length == 2 && a[0] == "floor" && a[1] is "on" or "off")
        { _meshFloor = a[1] == "on"; NavDataConfig.WriteSwitch("meshFloor", _meshFloor); }
        else if (a.Length == 2 && a[0] == "walls" && a[1] is "on" or "off")
        { _meshWalls = a[1] == "on"; NavDataConfig.WriteSwitch("meshWalls", _meshWalls); }
        else if (a.Length == 2 && a[0] == "radius" && isNum)
        { _meshRadius = Math.Clamp(num, MeshRadiusMin, MeshRadiusMax); NavDataConfig.WriteNumber("meshRadius", _meshRadius); }
        else if (a.Length == 2 && a[0] == "triangles" && isNum)
        { _meshBudget = (int)Math.Clamp(num, MeshTrianglesMin, MeshTrianglesMax); NavDataConfig.WriteNumber("meshTriangles", _meshBudget); }
        else if (a.Length > 0)
        {
            Say("/rnav mesh on|off, mesh floor on|off, mesh walls on|off, mesh radius <m> (10-150), mesh triangles <n> (200-7000)");
            return;
        }
        bool inDungeonMesh = _hasPose && (_cellId & 0xFFFF) >= 0x100 && _meshView.DungeonLandblock == CurrentLandblock;
        string where = !_hasPose ? ""
            : (_cellId & 0xFFFF) >= 0x100 && _meshDunStatus.StartsWith("no dungeon tile", StringComparison.Ordinal) && InDungeon(_cellId) ? $" — {_meshDunStatus}: bake it with RynthNav.Baker --dungeons"
            : inDungeonMesh ? (_meshOn ? $" — {_meshDunStatus}, {_meshDrawn} triangles drawn" : $" — {_meshDunStatus}")
            : _meshView.TileCount == 0 ? " — no tiles loaded here" : _meshOn ? $" — {_meshDrawn} triangles drawn, {_meshView.TileCount} tiles loaded" : "";
        Say($"navmesh view {(_meshOn ? "ON" : "OFF")}: green floor {(_meshFloor ? "on" : "off")}, red walls {(_meshWalls ? "on" : "off")}, " +
            $"{_meshRadius:0} m, at most {_meshBudget} triangles{where}. Green = nav can walk here; red = the mesh ends (building, tree, cliff, steep ground).");
    }
}
