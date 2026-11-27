// ============================================================================
//  RynthAi - AcMaps/AcMapWindow.cs
//  The ACMAPS Maps window: the player's own ACMAPS map of the dungeon they're
//  in, with zoom, pan and their position on it once they've placed it.
//  RynthCore hosts it (the sections manifest's "acmaps" window, plugin API
//  v89; no bar button) and calls Draw through RynthPluginRenderEmbed.
//
//  Top row: the dungeon's name and landblock; This dungeon, Maps (every map in
//  the folder, searchable), Folder (where the maps are, Browse..., Scan, the
//  chat line setting). The map: drag to pan, the wheel zooms where the cursor
//  is, Follow me keeps you in the middle. Bottom row: zoom - / + / Fit,
//  Follow me, Place me here (then click where you stand: AcMapCalibration),
//  Clear floor, the scale - / +.
//
//  AC's render thread. Decoding is on a thread of its own (WicImage); the
//  textures are made here (AcMapTexture). Looks: Shared/RynthUi.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.AcMaps;

/// <summary>What the window needs from RynthAi (RynthAiPlugin.AcMaps.cs).</summary>
internal interface IAcMapsHost
{
    AcMapIndex Index { get; }
    bool Scanning { get; }
    /// <summary>The settings; lock it to change them (the window's spots, the command's folder).</summary>
    AcMapSettings Settings { get; }
    void SetFolder(string folder);
    void Rescan();
    void SetAnnounce(bool on);
    /// <summary>Writes the settings file soon (off this thread).</summary>
    void SaveSettings();
    /// <summary>The player's cell and landblock position, compass heading (0 north, clockwise).</summary>
    bool TryPose(out uint cell, out float x, out float y, out float z, out float heading);
    IntPtr Device { get; }
}

internal sealed class AcMapWindow
{
    private const float MinZoom = 0.05f, MaxZoom = 8f;
    private const string ChooserTitle = "Choose the folder with your ACMAPS maps (the GIF or PNG files and dungeons.txt)";

    internal const string HelpText =
        "Your own ACMAPS dungeon maps, as GoArrow showed them. RynthSuite doesn't come with the maps: " +
        "point Folder at the map pack you have (the images named by landblock, like 02CA.gif, and dungeons.txt).\n\n" +
        "In a dungeon with a map, it shows here. Drag to move it, the mouse wheel zooms.\n\n" +
        "Your position: the maps have no coordinates, so place each floor once. Stand somewhere you can find on the map " +
        "(the portal you came in by, a corner), press Place me here and click that spot. A second spot on the same floor, " +
        "some way off, measures the map's scale. Spots are saved for the map. Clear floor forgets this floor's spots.\n\n" +
        "Maps also lists every map in the folder, to look at any dungeon.\n\n" +
        "/ra acmaps opens this window; /ra acmaps folder <path>, /ra acmaps scan, /ra acmaps announce on|off.";

    private readonly IAcMapsHost _host;
    private float _k = 1f;
    private float Px(float v) => v * _k;

    // What is shown.
    private bool _followDungeon = true;           // show the current dungeon's map (else _picked)
    private AcMapEntry? _picked;
    private int _part;                            // which of a dungeon's maps
    private AcMapEntry? _shown;
    private string _shownFolder = string.Empty;
    private AcMapTexture? _tex;
    private string _loadingPath = string.Empty;
    private string _loadError = string.Empty;
    private sealed record Loaded(string Path, DecodedImage? Image, string Error);
    private Loaded? _loaded;                      // the decode thread's result (Interlocked)

    // View.
    private float _zoom = 1f;
    private Vector2 _center;                      // file pixel at the canvas centre
    private bool _fitPending;                     // a map just loaded: fit it, then centre on you if placed
    private bool _fitAll;                         // Fit: the whole map
    private bool _followMe = true;
    private bool _dragging;
    private bool _placing;
    private string _status = string.Empty;
    private long _statusUntil;

    // Panels.
    private bool _mapsOpen, _folderOpen;
    private string _search = string.Empty;
    private string _folderText = string.Empty;
    private bool _folderTextSet;
    private volatile string? _chosenFolder;

    public AcMapWindow(IAcMapsHost host) => _host = host;

    /// <summary>The window was drawn this long ago: its textures go (they're remade when it shows).</summary>
    public void ReleaseTextures()
    {
        _tex?.Release();
        _tex = null;
        _shown = null;
        _loadingPath = string.Empty;
    }

    /// <summary>Draws the window body into the current ImGui window and returns the size it would like. Render thread.</summary>
    public Vector2 Draw()
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        var want = new Vector2(Px(620), Px(560));
        if (size.X < 80 || size.Y < 80) return want;

        string? chosen = Interlocked.Exchange(ref _chosenFolder, null);
        if (chosen != null) { _folderText = chosen; _host.SetFolder(chosen); Say("Folder set: scanning it."); }

        bool havePose = _host.TryPose(out uint cell, out float px, out float py, out float pz, out float heading);
        ushort here = havePose && (cell & 0xFFFF) >= 0x100 ? (ushort)(cell >> 16) : (ushort)0;
        AcMapIndex index = _host.Index;
        AcMapEntry? want_ = Choose(index, here);
        Track(index, want_);

        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui11))
        {
            float pad = Px(6), rowH = Px(22);
            float w = size.X - pad * 2;
            Vector2 p = origin + new Vector2(pad, pad);
            DrawTop(index, here, p, w, rowH);
            p.Y += rowH + Px(4);
            DrawStatusLine(index, here, havePose, pz, p, w);
            p.Y += Px(18);
            float bottomH = rowH + Px(6);
            float canvasH = Math.Max(Px(40), origin.Y + size.Y - pad - bottomH - p.Y - Px(8));
            var cmin = p;
            var cmax = p + new Vector2(w, canvasH);
            bool onThisMap = _shown != null && here != 0 && _shown.Landblock == here;
            // The map takes the mouse only where no panel lies over it (ImGui gives a press to the first
            // item under it, and the canvas is drawn first).
            Vector2 inputMin = cmin;
            if (_mapsOpen) inputMin.X = cmin.X + MapsPanelWidth(cmin, cmax) + Px(6);
            if (_folderOpen) inputMin.Y = cmin.Y + FolderPanelHeight(cmin, cmax) + Px(6);
            DrawCanvas(cmin, cmax, inputMin, onThisMap, havePose, px, py, pz, heading);
            if (_mapsOpen) DrawMapsPanel(index, cmin, cmax);
            if (_folderOpen) DrawFolderPanel(index, cmin, cmax);
            DrawBottom(new Vector2(p.X, cmax.Y + Px(6)), w, rowH, onThisMap, havePose, pz);
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return want;
    }

    // ── What to show ────────────────────────────────────────────────────

    private AcMapEntry? Choose(AcMapIndex index, ushort here)
    {
        if (!_followDungeon) return _picked;
        if (here == 0) return null;
        IReadOnlyList<AcMapEntry> maps = index.ForLandblock(here);
        if (maps.Count == 0) return null;
        return maps[Math.Clamp(_part, 0, maps.Count - 1)];
    }

    /// <summary>Starts loading the map to show, takes a finished decode, makes its textures.</summary>
    private void Track(AcMapIndex index, AcMapEntry? entry)
    {
        Loaded? done = Interlocked.Exchange(ref _loaded, null);
        string path = entry == null ? string.Empty : Path.Combine(index.Folder, entry.RelativePath);
        if (done != null && done.Path == _loadingPath && done.Path == path)
        {
            _tex?.Release();
            _tex = done.Image != null ? new AcMapTexture(done.Image) : null;
            _loadError = done.Error;
            _loadingPath = string.Empty;
            _fitPending = true;
        }
        bool same = _shown != null && entry != null && _shown.RelativePath == entry.RelativePath && _shownFolder == index.Folder;
        if (!same)
        {
            _tex?.Release();
            _tex = null;
            _shown = entry;
            _shownFolder = index.Folder;
            _loadError = string.Empty;
            _placing = false;
            _loadingPath = string.Empty;
            if (entry != null)
            {
                _loadingPath = path;
                string p = path;
                // A decode for a map no longer wanted (the player moved on) is dropped, so it can't
                // take the slot from the one that is.
                WicImage.DecodeAsync(p, (img, err) =>
                {
                    if (p == Volatile.Read(ref _loadingPath)) Interlocked.Exchange(ref _loaded, new Loaded(p, img, err));
                });
            }
        }
        _tex?.Upload(_host.Device);
        if (_tex is { NeedsReload: true })
        {
            // A new D3D device after the pixels were let go: read the file again.
            _tex = null;
            _shown = null;
        }
    }

    // ── Top row ─────────────────────────────────────────────────────────

    private void DrawTop(AcMapIndex index, ushort here, Vector2 p, float w, float h)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        const string thisLabel = "This dungeon", mapsLabel = "Maps", folderLabel = "Folder";
        float bw1 = ButtonWidth(thisLabel), bw2 = ButtonWidth(mapsLabel), bw3 = ButtonWidth(folderLabel), helpW = h;
        float right = p.X + w;
        float x3 = right - helpW - Px(4) - bw3, x2 = x3 - Px(4) - bw2, x1 = x2 - Px(4) - bw1;

        string title = _shown != null ? _shown.Label
            : _followDungeon && here != 0 ? (index.NameOf(here) is { Length: > 0 } n ? $"{n} ({here:X4})" : $"Dungeon {here:X4}")
            : _followDungeon ? "Not in a dungeon" : "No map picked";
        using (PushFont(RynthUiFont.Ui14))
        {
            dl.PushClipRect(p, new Vector2(x1 - Px(6), p.Y + h), true);
            float th = ImGui.GetFontSize();
            dl.AddText(new Vector2(p.X, p.Y + (h - th) * 0.5f), Text, title);
            dl.PopClipRect();
        }
        IReadOnlyList<AcMapEntry> parts = _followDungeon && here != 0 ? index.ForLandblock(here) : Array.Empty<AcMapEntry>();
        if (parts.Count > 1)
        {
            // A dungeon with more than one map: which part.
            float pw = Px(70);
            float tx = x1 - Px(8) - pw;
            if (Button("##am_part", $"Map {Math.Clamp(_part, 0, parts.Count - 1) + 1} of {parts.Count}", new Vector2(tx, p.Y), new Vector2(pw, h), Text, ButtonFill))
                _part = (Math.Clamp(_part, 0, parts.Count - 1) + 1) % parts.Count;
            SafeImGui.SetItemTooltip("This dungeon has " + parts.Count + " maps: show the next.");
        }
        if (Button("##am_here", thisLabel, new Vector2(x1, p.Y), new Vector2(bw1, h), Text, _followDungeon ? AccentFill : ButtonFill))
        {
            _followDungeon = true;
            _mapsOpen = false;
            _followMe = true;
        }
        SafeImGui.SetItemTooltip("Show the map of the dungeon you're in (changes as you go).");
        if (Button("##am_maps", mapsLabel, new Vector2(x2, p.Y), new Vector2(bw2, h), Text, _mapsOpen ? AccentFill : ButtonFill))
        {
            _mapsOpen = !_mapsOpen;
            if (_mapsOpen) _folderOpen = false;
        }
        SafeImGui.SetItemTooltip("Every map in your folder: pick one to look at.");
        if (Button("##am_folder", folderLabel, new Vector2(x3, p.Y), new Vector2(bw3, h), Text, _folderOpen ? AccentFill : ButtonFill))
        {
            _folderOpen = !_folderOpen;
            if (_folderOpen) _mapsOpen = false;
        }
        SafeImGui.SetItemTooltip("Where your ACMAPS maps are, and the chat line setting.");
        IconButton("##am_help", RynthIcons.Info, new Vector2(right - helpW, p.Y), new Vector2(helpW, h), Mute, ButtonFill, font: RynthUiFont.Ui11);
        SafeImGui.SetItemTooltip(HelpText);
    }

    private void DrawStatusLine(AcMapIndex index, ushort here, bool havePose, float z, Vector2 p, float w)
    {
        long now = Environment.TickCount64;
        string line;
        uint color = Mute;
        if (_status.Length > 0 && now < _statusUntil) { line = _status; color = Highlight; }
        else if (_host.Scanning) line = "Reading the maps folder...";
        else if (index.Maps.Count == 0)
        {
            line = index.Folder.Length == 0 && index.Problems.Count == 0
                ? "No maps yet: press Folder and choose your ACMAPS map pack."
                : (index.Problems.Count > 0 ? index.Problems[0] + " " : string.Empty) + "Press Folder to choose another.";
            color = index.Problems.Count > 0 ? Red : Mute;
        }
        else if (_shown == null && _followDungeon)
            line = here == 0 ? "You're not in a dungeon. Maps lists every map in your folder."
                : $"No ACMAPS map for this dungeon ({here:X4}) in your folder.";
        else if (_loadError.Length > 0) { line = _loadError; color = Red; }
        else if (_loadingPath.Length > 0) line = "Loading the map...";
        else if (_placing) { line = "Click the spot on the map where you're standing (Esc: stop)."; color = Highlight; }
        else if (_shown != null && here != 0 && _shown.Landblock == here)
        {
            AcMapCalibration cal = Calibration(_shown);
            line = !havePose ? "Your position can't be read right now."
                : cal.FloorPlaced(z) ? (cal.ScaleMeasuredOrSet || cal.FloorSpots(z).Count >= 2
                    ? "You're the green arrow."
                    : "You're the green arrow. A second spot on this floor, some way off, makes it exact.")
                : "This floor isn't placed: stand somewhere you can find on the map, press Place me here, click that spot.";
        }
        else line = _shown?.Notes is { Length: > 0 } notes ? notes : "Looking at another dungeon's map.";
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(p, p + new Vector2(w, Px(16)), true);
        dl.AddText(p, color, line);
        dl.PopClipRect();
        if (ImGui.IsMouseHoveringRect(p, p + new Vector2(w, Px(16))) && line.Length > 60) SafeImGui.SetTooltip(line);
    }

    // ── The map ─────────────────────────────────────────────────────────

    private void DrawCanvas(Vector2 cmin, Vector2 cmax, Vector2 inputMin, bool onThisMap, bool havePose, float px, float py, float pz, float heading)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(cmin, cmax, Field, 3);
        Vector2 csize = cmax - cmin;
        Vector2 mid = cmin + csize * 0.5f;
        bool hovered = false, active = false;
        Vector2 inputSize = cmax - inputMin;
        if (inputSize.X >= 4 && inputSize.Y >= 4)
        {
            ImGui.SetCursorScreenPos(inputMin);
            ImGui.InvisibleButton("##am_canvas", inputSize);
            hovered = ImGui.IsItemHovered();
            active = ImGui.IsItemActive();
        }

        AcMapTexture? tex = _tex;
        if (tex == null)
        {
            dl.AddRect(cmin, cmax, Border, 3);
            return;
        }
        float factor = tex.Factor;   // decoded per file pixel
        var fileSize = new Vector2(tex.SourceWidth, tex.SourceHeight);
        if (_fitAll)
        {
            _fitAll = false;
            Fit(fileSize, csize);
        }
        if (_fitPending)
        {
            _fitPending = false;
            Fit(fileSize, csize);
            if (onThisMap && havePose && Calibration(_shown!).TryToImage(px, py, pz, out Vector2 me))
            {
                _center = me;
                _zoom = Math.Max(_zoom, 1f);
            }
        }

        AcMapCalibration cal = Calibration(_shown!);
        Vector2 mePx = default;
        bool meKnown = onThisMap && havePose && cal.TryToImage(px, py, pz, out mePx);

        // Input: drag pans (Follow me off), the wheel zooms at the cursor, a click places when armed.
        ImGuiIOPtr io = ImGui.GetIO();
        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
        {
            _dragging = true;
            _followMe = false;
            _center -= io.MouseDelta / _zoom;
        }
        if (hovered && io.MouseWheel != 0)
        {
            Vector2 before = ToFile(io.MousePos, mid);
            _zoom = Math.Clamp(_zoom * MathF.Pow(1.2f, io.MouseWheel), MinZoom, MaxZoom);
            if (!_followMe || !meKnown) _center += before - ToFile(io.MousePos, mid);
        }
        bool clicked = hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !_dragging;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _dragging = false;
        if (_placing && ImGui.IsKeyPressed(ImGuiKey.Escape)) { _placing = false; Say("Stopped placing."); }
        if (_placing && clicked)
        {
            Vector2 at = ToFile(io.MousePos, mid);
            if (!onThisMap || !havePose) Say("Placing needs you in this map's dungeon.");
            else if (at.X < 0 || at.Y < 0 || at.X > fileSize.X || at.Y > fileSize.Y) Say("That's off the map: click on the map.");
            else
            {
                PlaceResult r;
                lock (_host.Settings) r = cal.Place(px, py, pz, at.X, at.Y);
                _host.SaveSettings();
                _placing = false;
                _followMe = true;
                Say(r switch
                {
                    PlaceResult.FloorPlaced => "Floor placed. A second spot on this floor, some way off, measures the scale.",
                    PlaceResult.Moved => "Spot moved (it was near the last one).",
                    PlaceResult.ScaleMeasured => "Scale measured from two spots: " + AcMapSettings.ScaleText(cal.Scale) + " pixels a unit.",
                    _ => "Those two spots don't fit a north-up map: the new spot places the floor on its own.",
                });
                meKnown = cal.TryToImage(px, py, pz, out mePx);
            }
        }
        if (_followMe && meKnown && !_dragging) _center = mePx;
        ClampCenter(fileSize);

        // The image, piece by piece, culled to the canvas.
        dl.PushClipRect(cmin, cmax, true);
        ReadOnlySpan<AcMapTexture.Piece> pieces = tex.Pieces;
        int missing = 0;
        foreach (AcMapTexture.Piece piece in pieces)
        {
            if (piece.Tex == IntPtr.Zero) { if (!piece.Tried) missing++; continue; }
            Vector2 a = ToScreen(new Vector2(piece.X, piece.Y) / factor, mid);
            Vector2 b = ToScreen(new Vector2(piece.X + piece.W, piece.Y + piece.H) / factor, mid);
            if (b.X < cmin.X || b.Y < cmin.Y || a.X > cmax.X || a.Y > cmax.Y) continue;
            dl.AddImage(piece.Tex, a, b, Vector2.Zero, piece.Uv1);
        }

        // The spots of this floor while placing (so a bad one can be clicked again).
        if (_placing && onThisMap && havePose)
            foreach (MapSpot s in cal.FloorSpots(pz))
            {
                Vector2 sp = ToScreen(new Vector2(s.Px, s.Py), mid);
                dl.AddCircle(sp, Px(6), Highlight, 12, 2f);
            }

        // You: a green arrow pointing your way (north up).
        if (meKnown)
        {
            Vector2 c = ToScreen(mePx, mid);
            float rad = heading * MathF.PI / 180f;
            var fwd = new Vector2(MathF.Sin(rad), -MathF.Cos(rad));
            var side = new Vector2(-fwd.Y, fwd.X);
            float len = Px(11), half = Px(6);
            Vector2 tip = c + fwd * len, l = c - fwd * Px(5) + side * half, r = c - fwd * Px(5) - side * half;
            uint green = Green;
            dl.AddTriangleFilled(tip, l, r, WithAlpha(green, 0xE6));
            dl.AddTriangle(tip, l, r, 0xFF000000, 1.5f);
            dl.AddCircleFilled(c, Px(2.5f), 0xFF000000);
        }
        if (missing > 0)
            dl.AddText(cmin + new Vector2(Px(6), Px(4)), Mute, "Drawing the map...");

        // Under the cursor: the landblock position, when the floor is placed.
        if (hovered && onThisMap && havePose && cal.FloorPlaced(pz) && cal.TryToWorld(ToFile(io.MousePos, mid), pz, out float wx, out float wy))
        {
            string t = string.Format(CultureInfo.InvariantCulture, "{0:0.0}, {1:0.0}", wx, wy);
            Vector2 ts = ImGui.CalcTextSize(t);
            Vector2 tp = new(cmax.X - ts.X - Px(6), cmax.Y - ts.Y - Px(4));
            dl.AddRectFilled(tp - new Vector2(Px(3), Px(1)), tp + ts + new Vector2(Px(3), Px(1)), WithAlpha(Background, 0xC0), 2);
            dl.AddText(tp, Mute, t);
        }
        dl.PopClipRect();
        dl.AddRect(cmin, cmax, _placing ? Highlight : Border, 3, ImDrawFlags.None, _placing ? 2f : 1f);
        if (_placing && hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
    }

    private Vector2 ToScreen(Vector2 file, Vector2 mid) => mid + (file - _center) * _zoom;
    private Vector2 ToFile(Vector2 screen, Vector2 mid) => _center + (screen - mid) / _zoom;

    private void Fit(Vector2 fileSize, Vector2 canvas)
    {
        if (fileSize.X <= 0 || fileSize.Y <= 0) return;
        _zoom = Math.Clamp(Math.Min(canvas.X / fileSize.X, canvas.Y / fileSize.Y), MinZoom, MaxZoom);
        _center = fileSize * 0.5f;
    }

    private void ClampCenter(Vector2 fileSize)
    {
        _center = Vector2.Clamp(_center, Vector2.Zero, fileSize);
        if (!float.IsFinite(_center.X) || !float.IsFinite(_center.Y)) _center = fileSize * 0.5f;
    }

    // ── Bottom row ──────────────────────────────────────────────────────

    private void DrawBottom(Vector2 p, float w, float h, bool onThisMap, bool havePose, float pz)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool haveMap = _tex != null;
        float x = p.X, sq = h, gap = Px(4);
        if (Button("##am_zout", "-", new Vector2(x, p.Y), new Vector2(sq, h), Text, ButtonFill, haveMap)) Zoom(1f / 1.25f);
        SafeImGui.SetItemTooltip("Zoom out (or the mouse wheel).");
        x += sq + gap;
        string zt = Math.Round(_zoom * 100).ToString(CultureInfo.InvariantCulture) + "%";
        float zw = Px(44);
        Vector2 zs = ImGui.CalcTextSize(zt);
        dl.AddText(new Vector2(x + (zw - zs.X) * 0.5f, p.Y + (h - zs.Y) * 0.5f), haveMap ? Text : Faded(Text), zt);
        x += zw + gap;
        if (Button("##am_zin", "+", new Vector2(x, p.Y), new Vector2(sq, h), Text, ButtonFill, haveMap)) Zoom(1.25f);
        SafeImGui.SetItemTooltip("Zoom in (or the mouse wheel).");
        x += sq + gap;
        const string fitLabel = "Fit";
        float fw = ButtonWidth(fitLabel);
        if (Button("##am_fit", fitLabel, new Vector2(x, p.Y), new Vector2(fw, h), Text, ButtonFill, haveMap))
        {
            _followMe = false;
            _fitAll = true;   // fitted to the canvas as it is drawn
        }
        SafeImGui.SetItemTooltip("The whole map in the window.");
        x += fw + gap * 2;

        AcMapCalibration? cal = _shown != null ? Calibration(_shown) : null;
        bool canPlace = haveMap && onThisMap && havePose;
        const string followLabel = "Follow me";
        float folw = ButtonWidth(followLabel);
        bool meKnown = canPlace && cal!.FloorPlaced(pz);
        if (Button("##am_follow", followLabel, new Vector2(x, p.Y), new Vector2(folw, h), Text, _followMe && meKnown ? AccentFill : ButtonFill, meKnown))
            _followMe = !_followMe;
        SafeImGui.SetItemTooltip(meKnown ? "Keep you in the middle of the map (dragging the map turns it off)."
            : "Needs this floor placed (Place me here).");
        x += folw + gap;

        const string placeLabel = "Place me here";
        float plw = ButtonWidth(placeLabel);
        if (Button("##am_place", placeLabel, new Vector2(x, p.Y), new Vector2(plw, h), Text, _placing ? AccentFill : ButtonFill, canPlace))
        {
            _placing = !_placing;
            if (_placing) Say("Now click the spot on the map where you're standing.");
        }
        SafeImGui.SetItemTooltip(canPlace
            ? "Stand somewhere you can find on the map (a portal, a corner), press this, then click that spot on the map.\n" +
              "Once per floor; a second spot on a floor, some way off, measures the scale."
            : "Needs you in this map's dungeon, with the map loaded.");
        x += plw + gap;

        const string clearLabel = "Clear floor";
        float clw = ButtonWidth(clearLabel);
        bool canClear = canPlace && cal!.FloorPlaced(pz);
        if (Button("##am_clear", clearLabel, new Vector2(x, p.Y), new Vector2(clw, h), Text, ButtonFill, canClear))
        {
            lock (_host.Settings) cal!.ClearFloor(pz);
            _host.SaveSettings();
            Say("This floor's spots are cleared.");
        }
        SafeImGui.SetItemTooltip("Forget this floor's spots (to place it again).");
        x += clw + gap * 2;

        // Scale: - 3.00 +
        float right = p.X + w;
        float sw = Px(40);
        float need = sq * 2 + sw + gap * 2;
        if (x + need <= right && cal != null)
        {
            float sx = right - need;
            bool canScale = haveMap;
            if (Button("##am_sdn", "-", new Vector2(sx, p.Y), new Vector2(sq, h), Text, ButtonFill, canScale))
            {
                lock (_host.Settings) cal.NudgeScale(1f / 1.02f);
                _host.SaveSettings();
            }
            SafeImGui.SetItemTooltip("The map's scale, a little smaller (when the arrow runs ahead of you).");
            string st = AcMapSettings.ScaleText(cal.Scale);
            Vector2 ss = ImGui.CalcTextSize(st);
            dl.AddText(new Vector2(sx + sq + gap + (sw - ss.X) * 0.5f, p.Y + (h - ss.Y) * 0.5f), cal.ScaleMeasuredOrSet ? Text : Mute, st);
            if (ImGui.IsMouseHoveringRect(new Vector2(sx + sq + gap, p.Y), new Vector2(sx + sq + gap + sw, p.Y + h)))
                SafeImGui.SetTooltip(cal.ScaleMeasuredOrSet ? "Map pixels for each unit you walk (measured or set)."
                    : "Map pixels for each unit you walk: GoArrow's 3 until a second spot measures it.");
            if (Button("##am_sup", "+", new Vector2(sx + sq + gap * 2 + sw, p.Y), new Vector2(sq, h), Text, ButtonFill, canScale))
            {
                lock (_host.Settings) cal.NudgeScale(1.02f);
                _host.SaveSettings();
            }
            SafeImGui.SetItemTooltip("The map's scale, a little bigger (when the arrow lags behind you).");
        }
    }

    private void Zoom(float k) => _zoom = Math.Clamp(_zoom * k, MinZoom, MaxZoom);

    // ── Maps panel ──────────────────────────────────────────────────────

    private float MapsPanelWidth(Vector2 cmin, Vector2 cmax) => Math.Min(Px(280), (cmax.X - cmin.X) * 0.7f);

    private float FolderPanelHeight(Vector2 cmin, Vector2 cmax) => Math.Min(cmax.Y - cmin.Y - Px(8), Px(250));

    private void DrawMapsPanel(AcMapIndex index, Vector2 cmin, Vector2 cmax)
    {
        float pw = MapsPanelWidth(cmin, cmax);
        Vector2 a = cmin + new Vector2(Px(4), Px(4)), b = new(cmin.X + pw, cmax.Y - Px(4));
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(a, b, WithAlpha(Background, 0xF0), 4);
        dl.AddRect(a, b, Border, 4);
        ImGui.SetCursorScreenPos(a + new Vector2(Px(6), Px(6)));
        ImGui.PushItemWidth(pw - Px(16));
        ImGui.InputTextWithHint("##am_search", "Find a map: a name or landblock", ref _search, 64u);
        ImGui.PopItemWidth();
        List<AcMapEntry> hits = AcMapIndex.Search(index.Maps, _search);
        Vector2 listPos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(a.X + Px(6), listPos.Y + Px(2)));
        ImGui.BeginChild("##am_list", new Vector2(pw - Px(12), Math.Max(Px(20), b.Y - listPos.Y - Px(8))));
        if (hits.Count == 0) Label(index.Maps.Count == 0 ? "No maps in the folder." : "No map matches.", Mute);
        int i = 0;
        foreach (AcMapEntry e in hits)
        {
            bool sel = _shown != null && _shown.RelativePath == e.RelativePath;
            if (ImGui.Selectable(SafeLabel(e.Label) + "##am_e" + i, sel))
            {
                _followDungeon = false;
                _picked = e;
                _followMe = false;
                _mapsOpen = false;
            }
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(e.RelativePath + (e.Notes.Length > 0 ? "\n" + e.Notes : string.Empty));
            i++;
        }
        ImGui.EndChild();
    }

    // ── Folder panel ────────────────────────────────────────────────────

    private void DrawFolderPanel(AcMapIndex index, Vector2 cmin, Vector2 cmax)
    {
        AcMapSettings s = _host.Settings;
        string current;
        bool announce;
        lock (s) { current = s.FolderOrDefault; announce = s.Announce; }
        if (!_folderTextSet) { _folderText = current; _folderTextSet = true; }
        float w = cmax.X - cmin.X - Px(8);
        Vector2 a = cmin + new Vector2(Px(4), Px(4));
        float h = FolderPanelHeight(cmin, cmax);
        Vector2 b = a + new Vector2(w, h);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(a, b, WithAlpha(Background, 0xF4), 4);
        dl.AddRect(a, b, Border, 4);
        float pad = Px(8), rowH = Px(22);
        Vector2 p = a + new Vector2(pad, pad);
        float iw = w - pad * 2;
        dl.AddText(p, Text, "Your ACMAPS maps folder");
        p.Y += Px(18);

        const string browse = "Browse...", useDefault = "Default", scan = "Scan";
        float bw = ButtonWidth(browse), dw = ButtonWidth(useDefault), sw = ButtonWidth(scan);
        float boxW = iw - bw - dw - sw - Px(12);
        ImGui.SetCursorScreenPos(p);
        ImGui.PushItemWidth(Math.Max(Px(60), boxW));
        bool enter = ImGui.InputText("##am_folderbox", ref _folderText, 260u, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.PopItemWidth();
        SafeImGui.SetItemTooltip("Type or paste the folder, then Enter (or Scan).");
        float bx = p.X + boxW + Px(4);
        bool picking = FolderPicker.IsOpen;
        if (Button("##am_browse", picking ? "Open..." : browse, new Vector2(bx, p.Y), new Vector2(bw, rowH), Text, ButtonFill, !picking))
        {
            string start = Directory.Exists(_folderText) ? _folderText : current;
            if (!FolderPicker.Pick(ChooserTitle, start, f => { if (f != null) _chosenFolder = f; }))
                Say("A folder chooser is already open.");
            else Say("The folder chooser is open (in full screen it may be behind the game: Alt+Tab).");
        }
        SafeImGui.SetItemTooltip("Choose the folder in Windows' folder chooser.");
        bx += bw + Px(4);
        if (Button("##am_default", useDefault, new Vector2(bx, p.Y), new Vector2(dw, rowH), Text, ButtonFill))
        {
            _folderText = AcMapSettings.DefaultFolder;
            enter = true;
        }
        SafeImGui.SetItemTooltip("RynthSuite's own maps folder: " + AcMapSettings.DefaultFolder + "\n(copy your map pack into it; it is made if it isn't there).");
        bx += dw + Px(4);
        bool doScan = Button("##am_scan", scan, new Vector2(bx, p.Y), new Vector2(sw, rowH), Text, ButtonFill, !_host.Scanning);
        SafeImGui.SetItemTooltip("Read the folder again (after adding maps).");
        if (enter || doScan)
        {
            string f = _folderText.Trim().Trim('"');
            if (f.Length == 0) f = AcMapSettings.DefaultFolder;
            if (!string.Equals(f, current, StringComparison.OrdinalIgnoreCase)) { _host.SetFolder(f); Say("Folder set: scanning it."); }
            else { _host.Rescan(); Say("Scanning the folder."); }
        }
        p.Y += rowH + Px(6);

        string summary = _host.Scanning ? "Reading the folder..." : index.Summary();
        dl.AddText(p, Mute, summary);
        p.Y += Px(16);
        foreach (string problem in index.Problems)
        {
            dl.PushClipRect(p, p + new Vector2(iw, Px(16)), true);
            dl.AddText(p, Red, problem);
            dl.PopClipRect();
            p.Y += Px(16);
            if (p.Y > b.Y - Px(80)) break;
        }
        p.Y += Px(4);
        ImGui.SetCursorScreenPos(p);
        bool a2 = announce;
        if (ImGui.Checkbox("Say in chat when a dungeon has an ACMAPS map##am_announce", ref a2)) _host.SetAnnounce(a2);
        SafeImGui.SetItemTooltip("A line in chat as you enter a dungeon your folder has a map for (off unless you turn it on).");
        p.Y += rowH + Px(8);
        ImGui.SetCursorScreenPos(p);
        ImGui.PushTextWrapPos(a.X + w - pad);
        ImGui.PushStyleColor(ImGuiCol.Text, Mute);
        SafeImGui.TextWrapped("The maps are ACMAPS', made by its players and kept by the AC community; RynthSuite doesn't come with them. " +
                              "A pack is the images named by landblock (02CA.gif) and dungeons.txt. GoArrow's \"Dungeon Map Cache\" folder works as it is.");
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private AcMapCalibration Calibration(AcMapEntry e)
    {
        lock (_host.Settings) return _host.Settings.For(e.RelativePath);
    }

    private void Say(string text)
    {
        _status = text;
        _statusUntil = Environment.TickCount64 + 6000;
    }

    private static string SafeLabel(string s) => s.Replace("##", "# ");

    /// <summary>"/ra acmaps" with a map named: show it (not following the dungeon).</summary>
    public void Show(AcMapEntry entry)
    {
        _followDungeon = false;
        _picked = entry;
        _followMe = false;
    }

    /// <summary>Back to the dungeon you're in.</summary>
    public void FollowDungeon()
    {
        _followDungeon = true;
        _followMe = true;
    }
}
