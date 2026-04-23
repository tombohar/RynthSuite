using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Paints dungeon walls on the retail AC radar. Walls the player has walked
/// past render blue; unvisited walls render white. Portals (doorways) are
/// skipped so openings read as gaps. Current Z-layer only — multi-floor
/// dungeons stack would be unreadable otherwise.
///
/// Explored state is per-EnvCell and persisted per-landblock under
/// C:\Games\RynthSuite\RynthAi\ExploredDungeons\XXYY0000.json.
/// </summary>
internal sealed class RadarWallRenderer
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private MainLogic? _raycast;

    // Colours (ABGR for ImGui).
    private const uint ColorWall     = 0xFFFFFFFF; // white — unvisited
    private const uint ColorWallSeen = 0xFFFF6A2A; // blue-ish (RGB 0x2A6AFF → ABGR 0xFFFF6A2A)
    private const uint ColorButtonBg = 0x80202020;
    private const uint ColorButtonFg = 0xFFCCCCCC;

    // Explored state (in-memory; flushed to disk).
    private uint _currentLandblock;
    private readonly HashSet<uint> _visitedCells = new();
    private bool _visitedDirty;
    private DateTime _lastSaveAt = DateTime.MinValue;
    private uint _lastPlayerCellId;

    // Z-layer filter: polygons whose cell Z is within this many world units of
    // the player's Z are considered "same floor". AC dungeon floors are ~5u apart.
    private const float FloorZTolerance = 2.5f;

    // Reusable per-frame scratch for polygon vertex projection. Dungeon
    // polygons are almost always ≤4 verts; 16 is ample headroom.
    private const int MaxPolyVerts = 16;
    private readonly Vector2[] _pts = new Vector2[MaxPolyVerts];
    private readonly bool[] _inside = new bool[MaxPolyVerts];

    private static readonly string ExploredDir =
        @"C:\Games\RynthSuite\RynthAi\ExploredDungeons";

    public RadarWallRenderer(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    public void SetRaycast(MainLogic raycast) => _raycast = raycast;

    public void Render()
    {
        try { RenderCore(); }
        catch (Exception ex) { _host.Log($"RadarWalls: {ex.Message}"); }
    }

    private void RenderCore()
    {
        if (!_settings.ShowRadarWalls)
            return;
        if (!_host.HasGetRadarRect)
            return;

        var los = _raycast?.GeometryLoader?.DungeonLOS;
        if (los == null) return;

        // ── Player state ─────────────────────────────────────────────
        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out float pz,
                out _, out _, out _, out _))
            return;
        if ((cellId >> 16) == 0) return;                          // portalspace
        if (_host.HasIsPortaling && _host.IsPortaling()) return;

        uint landblock = cellId >> 16;

        // Outdoor cells (0x0000–0x00FF in the low word) have no EnvCell geometry.
        if ((cellId & 0xFFFF) < 0x0100)
            return;

        // ── Landblock transition — load explored state, flush previous ─
        if (landblock != _currentLandblock)
        {
            if (_currentLandblock != 0 && _visitedDirty)
                Save(_currentLandblock);
            _currentLandblock = landblock;
            _visitedCells.Clear();
            Load(landblock);
            _visitedDirty = false;
            _lastPlayerCellId = 0;
        }

        // ── Mark visited when player cellId changes ──────────────────
        if (cellId != _lastPlayerCellId)
        {
            _lastPlayerCellId = cellId;
            if (_visitedCells.Add(cellId))
                _visitedDirty = true;
        }

        // Debounced persist (save at most every 3s while moving).
        if (_visitedDirty && (DateTime.UtcNow - _lastSaveAt).TotalSeconds > 3.0)
        {
            Save(landblock);
            _lastSaveAt = DateTime.UtcNow;
            _visitedDirty = false;
        }

        // ── Radar geometry ────────────────────────────────────────────
        if (!_host.TryGetRadarRect(out int rx0, out int ry0, out int rx1, out int ry1))
            return;

        float centerX = (rx0 + rx1) * 0.5f;
        float centerY = (ry0 + ry1) * 0.5f;
        float radius  = MathF.Min(rx1 - rx0, ry1 - ry0) * 0.5f - 2f;
        if (radius < 10f) return;

        float heading = 0f;
        _host.TryGetPlayerHeading(out heading);
        float hRad = heading * (MathF.PI / 180f);
        float sinH = MathF.Sin(hRad);
        float cosH = MathF.Cos(hRad);

        float worldRange = MathF.Max(5f, _settings.RadarWallWorldRange);
        float pxPerWorld = radius / worldRange;

        // ── Polygon fetch ─────────────────────────────────────────────
        var polys = los.GetDungeonMapPolygons(landblock);
        if (polys == null || polys.Count == 0) return;

        // ── Fullscreen transparent ImGui window ──────────────────────
        if (!_host.TryGetViewportSize(out uint vpW, out uint vpH) || vpW == 0 || vpH == 0)
            return;

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(vpW, vpH));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        bool open = true;
        if (!ImGui.Begin("##RadarWalls", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var clipMin = new Vector2(rx0, ry0);
        var clipMax = new Vector2(rx1, ry1);
        dl.PushClipRect(clipMin, clipMax, intersect_with_current_clip_rect: true);

        float r2 = radius * radius;

        foreach (var poly in polys)
        {
            if (poly.IsPortal) continue;
            if (MathF.Abs(poly.CellZ - pz) > FloorZTolerance) continue;

            uint color = _visitedCells.Contains(poly.CellId) ? ColorWallSeen : ColorWall;

            var verts = poly.Vertices;
            int n = verts.Length;
            if (n < 2 || n > MaxPolyVerts) continue;

            // Project vertices to radar space once.
            for (int i = 0; i < n; i++)
            {
                float wdx = verts[i].X - px;      // east offset
                float wdy = verts[i].Y - py;      // north offset

                // Rotate by -heading so player facing points "up" (screen -Y).
                //   forward = wx*sin(h) + wy*cos(h)
                //   right   = wx*cos(h) - wy*sin(h)
                float radarRight   = wdx * cosH - wdy * sinH;
                float radarForward = wdx * sinH + wdy * cosH;

                float sx = centerX + radarRight   * pxPerWorld;
                float sy = centerY - radarForward * pxPerWorld;

                _pts[i] = new Vector2(sx, sy);
                float ddx = sx - centerX;
                float ddy = sy - centerY;
                _inside[i] = (ddx * ddx + ddy * ddy) <= r2;
            }

            // Draw each edge. Clip segments that stray fully outside the radar
            // circle (PushClipRect handles the square bounds; circle clipping
            // would need per-edge trimming we skip for MVP).
            for (int i = 0; i < n - 1; i++)
            {
                if (!_inside[i] && !_inside[i + 1]) continue;
                dl.AddLine(_pts[i], _pts[i + 1], color, 1.0f);
            }
        }

        dl.PopClipRect();

        // ── +/- zoom buttons at bottom-right of the radar ────────────
        DrawZoomButtons(dl, rx1, ry1);

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private void DrawZoomButtons(ImDrawListPtr dl, int rx1, int ry1)
    {
        const float btnW = 14f;
        const float btnH = 14f;
        const float gap = 2f;

        float minusX = rx1 - btnW - 1f;
        float plusX  = rx1 - btnW * 2f - gap - 1f;
        float btnY   = ry1 - btnH - 1f;

        var plusMin  = new Vector2(plusX, btnY);
        var plusMax  = new Vector2(plusX + btnW, btnY + btnH);
        var minusMin = new Vector2(minusX, btnY);
        var minusMax = new Vector2(minusX + btnW, btnY + btnH);

        Vector2 mouse = ImGui.GetIO().MousePos;
        bool plusHover  = mouse.X >= plusMin.X  && mouse.X <= plusMax.X  && mouse.Y >= plusMin.Y  && mouse.Y <= plusMax.Y;
        bool minusHover = mouse.X >= minusMin.X && mouse.X <= minusMax.X && mouse.Y >= minusMin.Y && mouse.Y <= minusMax.Y;

        dl.AddRectFilled(plusMin,  plusMax,  plusHover  ? 0xC0404040 : ColorButtonBg, 2f);
        dl.AddRectFilled(minusMin, minusMax, minusHover ? 0xC0404040 : ColorButtonBg, 2f);
        dl.AddRect(plusMin,  plusMax,  ColorButtonFg, 2f);
        dl.AddRect(minusMin, minusMax, ColorButtonFg, 2f);

        var plusCenter  = (plusMin  + plusMax)  * 0.5f;
        var minusCenter = (minusMin + minusMax) * 0.5f;
        dl.AddLine(new Vector2(plusCenter.X - 4f, plusCenter.Y),
                   new Vector2(plusCenter.X + 4f, plusCenter.Y), ColorButtonFg, 1.5f);
        dl.AddLine(new Vector2(plusCenter.X, plusCenter.Y - 4f),
                   new Vector2(plusCenter.X, plusCenter.Y + 4f), ColorButtonFg, 1.5f);
        dl.AddLine(new Vector2(minusCenter.X - 4f, minusCenter.Y),
                   new Vector2(minusCenter.X + 4f, minusCenter.Y), ColorButtonFg, 1.5f);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (plusHover)  _settings.RadarWallWorldRange = MathF.Max(10f, _settings.RadarWallWorldRange - 5f);
            if (minusHover) _settings.RadarWallWorldRange = MathF.Min(150f, _settings.RadarWallWorldRange + 5f);
        }
    }

    // ── Persistence ─────────────────────────────────────────────────

    // File format: raw little-endian uint32 cell ids, back-to-back. No header —
    // filename implies the landblock. Plain binary keeps this AOT-trivial (no
    // JSON reflection) and stays tiny.
    private static string PathFor(uint landblock)
        => Path.Combine(ExploredDir, $"{landblock:X4}0000.bin");

    private void Load(uint landblock)
    {
        string path = PathFor(landblock);
        if (!File.Exists(path)) return;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            int count = bytes.Length / sizeof(uint);
            for (int i = 0; i < count; i++)
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(i * sizeof(uint), sizeof(uint)));
                _visitedCells.Add(id);
            }
        }
        catch (Exception ex)
        {
            _host.Log($"RadarWalls: load {path} failed: {ex.Message}");
        }
    }

    private void Save(uint landblock)
    {
        try
        {
            Directory.CreateDirectory(ExploredDir);
            byte[] bytes = new byte[_visitedCells.Count * sizeof(uint)];
            int i = 0;
            foreach (uint id in _visitedCells)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(i * sizeof(uint), sizeof(uint)), id);
                i++;
            }
            File.WriteAllBytes(PathFor(landblock), bytes);
        }
        catch (Exception ex)
        {
            _host.Log($"RadarWalls: save failed: {ex.Message}");
        }
    }

    /// <summary>Flush any unsaved state — call on shutdown or logout.</summary>
    public void Flush()
    {
        if (_visitedDirty && _currentLandblock != 0)
        {
            Save(_currentLandblock);
            _visitedDirty = false;
        }
    }
}
