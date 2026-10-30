// ============================================================================
//  RynthVision - Ui/VisionWindow.cs
//  The Vision window, drawn whole by RynthVision (2026-10-27; it takes the
//  engine's Vision panel's place, Ui/VisionManifest.cs). Owner's layer rule:
//  the plugin owns its screens. Four tabs, the engine's three as they were
//  (RynthCore ImGui/Panels/VisionFace.cs: same rows, sizes, colours, fonts and
//  saving) and the dungeon light:
//    Terrain      the overlays (radar ring, unclimbable slopes, impassable
//                 water), colours (AARRGGBB hex + swatch) each with an opacity
//                 slider (the colour's alpha byte), tuning sliders (Ctrl+click
//                 to type a value), water terrain types, "Log terrain types here";
//    Nameplates   } still RynthCore's settings (the engine draws the plates):
//    Combat text  } the engine draws these two bodies into this window
//                 (RenderSettingsDrawer "nameplates" / "combattext");
//    Light        the dungeon light (Light/DungeonLight.cs, c769ab1's Dungeon
//                 Light window moved in here): on/off, what it does here
//                 ("Lighting in a dungeon: ambient 20% -> 52%."), strength,
//                 where, colour. /rv light show opens the window on this tab.
//
//  Threads: AC's render thread. Terrain edits a private copy of the settings
//  the tick published (SettingsView) and queues just the changed field for the
//  tick (which applies it, saves rynthvision.json and republishes), when a
//  control finishes changing (a slider on release); a newer copy replaces it
//  unless a control is in use. The Light tab reads the LightView the tick
//  publishes; its strength slider lights as it moves and saves when let go.
//  The same file and keys as the engine's panel saved through the plugin's
//  exports, so players keep their choices.
//  Sizes are the engine face's at the default text size, scaled with it.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthVision.Light;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthVision.Ui;

/// <summary>The settings the tick published for the window (a copy; <see cref="Version"/> bumps on every change).</summary>
internal sealed record SettingsView(int Version, VisionSettings Settings);

internal sealed class VisionWindow
{
    private readonly RynthVisionPlugin _plugin;
    private readonly RynthCoreHost _host;

    // The engine face's section header colour (ARGB 0xFF7AB8F5).
    private static readonly Vector4 HeaderColor = Vec(Argb(0xFF7AB8F5));
    private static readonly string[] TabIds = { "##vtab0", "##vtab1", "##vtab2", "##vtab3" };

    private VisionTab _tab;
    private VisionSettings? _s;
    private int _seenVersion = -1;
    private bool _busy;
    private string _slopeHex = "", _waterHex = "", _radarHex = "", _waterTypes = "";
    private float _k = 1f;

    // Light tab.
    private int? _dragStrength;      // the slider's value while it is held
    private string _lightHex = "FFFFFF";
    private bool _lightHexEditing;

    public VisionWindow(RynthVisionPlugin plugin, RynthCoreHost host)
    {
        _plugin = plugin;
        _host = host;
    }

    private float Px(float v) => v * _k;

    /// <summary>Draws the window body into the current ImGui window. Render thread.</summary>
    public void Draw()
    {
        if (_plugin.TakeTabRequest(out VisionTab asked)) _tab = asked;
        TakeSnapshot();
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        if (size.X < 40 || size.Y < 40) return;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui10))
        {
            _k = Math.Max(0.5f, ImGui.GetFontSize() / 10f);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGui.Indent(6);
            float w = size.X - 12;
            TabStrip(w);
            // Down to the body's bottom (6 px margin), clear of the host's resize grip in the corner.
            float room = Math.Max(60, origin.Y + size.Y - 6 - Px(10) - ImGui.GetCursorScreenPos().Y);
            switch (_tab)
            {
                case VisionTab.Terrain: _busy = DrawTerrain(w, room); break;
                case VisionTab.Light: _busy = false; DrawLight(w, room); break;
                default: _busy = false; DrawEngineTab(VisionFields.EngineTabKey(_tab)!, w, room); break;
            }
            ImGui.Unindent(6);
            ImGui.PopStyleVar();
        }
        frame.AddRect(origin, origin + size, Border, 4);
    }

    /// <summary>Terrain | Nameplates | Combat text | Light, as a row of flat buttons.</summary>
    private void TabStrip(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float x = p.X;
        int n = VisionFields.TabNames.Length;
        float bw = MathF.Floor((w - 4 * (n - 1)) / n);
        for (int i = 0; i < n; i++)
        {
            bool sel = i == (int)_tab;
            if (Button(TabIds[i], VisionFields.TabNames[i], new Vector2(x, p.Y), new Vector2(bw, Px(24)), sel ? Accent : Mute,
                    sel ? Selected : ButtonFill, border: sel ? Accent : 0))
                _tab = (VisionTab)i;
            if (i == (int)VisionTab.Light) SafeImGui.SetItemTooltip("Dungeon light: lights up dark dungeons (/rv light).");
            x += bw + 4;
        }
        NextLine(p, Px(30));
    }

    // ── Nameplates, Combat text (the engine's rows) ──────────────────────

    private void DrawEngineTab(string key, float w, float room)
    {
        ImGui.BeginChild("##vision_overlays", new Vector2(w, room));
        float cw = ImGui.GetContentRegionAvail().X - 4;
        float drawn = _host.RenderSettingsDrawer(key, cw, 0f);
        if (drawn < 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + cw);
            ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            ImGui.TextUnformatted("This RynthCore can't show these settings here: update RynthCore in the launcher. " +
                "Chat still sets them: /rv plates.");
            ImGui.PopStyleColor();
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
    }

    // ── Terrain (RynthVision's own settings) ─────────────────────────────

    /// <summary>The overlays' settings. True while a control is in use.</summary>
    private bool DrawTerrain(float w, float room)
    {
        if (_s == null)
        {
            Label("Waiting for RynthVision...", Mute);
            return false;
        }
        VisionSettings s = _s;
        bool busy = false;

        ImGui.BeginChild("##vision_body", new Vector2(w, room));
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ButtonFill);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Highlight);

        Header("Overlays");
        if (ImGui.Checkbox("Radar range ring", ref s.ShowRadarRing)) { bool v = s.ShowRadarRing; Change(st => st.ShowRadarRing = v); }
        if (ImGui.Checkbox("Unclimbable slopes", ref s.ShowUnclimbableSlopes)) { bool v = s.ShowUnclimbableSlopes; Change(st => st.ShowUnclimbableSlopes = v); }
        if (ImGui.Checkbox("Impassable water", ref s.ShowImpassableWater)) { bool v = s.ShowImpassableWater; Change(st => st.ShowImpassableWater = v); }

        Header("Colors (hex AARRGGBB) and opacity");
        ImGui.PushItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 8));
        busy |= ColorRow("Slope", "##hex_Slope", ref _slopeHex, ref s.SlopeColorArgb, static (st, c) => st.SlopeColorArgb = c);
        busy |= OpacitySlider("Slope opacity:", "##Slope opacity", ref _slopeHex, ref s.SlopeColorArgb, static (st, c) => st.SlopeColorArgb = c);
        busy |= ColorRow("Water", "##hex_Water", ref _waterHex, ref s.WaterColorArgb, static (st, c) => st.WaterColorArgb = c);
        busy |= OpacitySlider("Water opacity:", "##Water opacity", ref _waterHex, ref s.WaterColorArgb, static (st, c) => st.WaterColorArgb = c);
        busy |= ColorRow("Radar", "##hex_Radar", ref _radarHex, ref s.RadarRingColorArgb, static (st, c) => st.RadarRingColorArgb = c);
        busy |= OpacitySlider("Radar ring opacity:", "##Radar ring opacity", ref _radarHex, ref s.RadarRingColorArgb, static (st, c) => st.RadarRingColorArgb = c);
        ImGui.PopItemWidth();

        Header("Tuning");
        ImGui.PushItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 8));
        busy |= Slider("Radar range:", "##Radar range", ref s.RadarRangeWorld, 24, 300, "%.0f", static (st, v) => st.RadarRangeWorld = v);
        busy |= Slider("Ring thickness:", "##Ring thickness", ref s.RadarRingThickness, 0.5f, 6, "%.1f", static (st, v) => st.RadarRingThickness = v);
        busy |= Slider("Ring height (m):", "##Ring height (m)", ref s.RadarRingHeight, 0.5f, 30, "%.1f", static (st, v) => st.RadarRingHeight = v);
        busy |= IntSlider("Slope radius (cells):", "##Slope radius (cells)", ref s.SlopeRenderRadius, 1, VisionSettings.MaxRenderRadius,
            static (st, v) => st.SlopeRenderRadius = v);
        busy |= Slider("Slope floor Z:", "##Slope floor Z", ref s.SlopeFloorZ, 0.30f, 0.95f, "%.3f", static (st, v) => st.SlopeFloorZ = v);
        busy |= Slider("Slope height bias (m):", "##Slope height bias (m)", ref s.SlopeHeightBias, 0.00f, 1.00f, "%.2f",
            static (st, v) => st.SlopeHeightBias = v);
        busy |= IntSlider("Water radius (cells):", "##Water radius (cells)", ref s.WaterRenderRadius, 1, VisionSettings.MaxRenderRadius,
            static (st, v) => st.WaterRenderRadius = v);
        ImGui.PopItemWidth();

        Header("Water terrain types");
        if (ImGui.Checkbox("Highlight cell if ANY corner is water (else all four)", ref s.WaterAnyCorner))
        { bool v = s.WaterAnyCorner; Change(st => st.WaterAnyCorner = v); }
        if (ImGui.Checkbox("Only paint water cells with an unwalkable triangle (real impassable)", ref s.WaterImpassableOnly))
        { bool v = s.WaterImpassableOnly; Change(st => st.WaterImpassableOnly = v); }
        Vector2 p = ImGui.GetCursorScreenPos();
        bool enter = TextBox("##water_types", ref _waterTypes, p, Px(160), "18,19,20", out bool typing,
            ImGuiInputTextFlags.EnterReturnsTrue);
        bool done = ImGui.IsItemDeactivatedAfterEdit();
        busy |= typing;
        bool apply = Button("##water_apply", "Apply", new Vector2(p.X + Px(164), p.Y), new Vector2(Px(56), Px(22)), Text, ButtonFill);
        if (enter || done || apply)
        {
            // An empty list resets to the defaults (VisionSettings.NormalizeWaterTypes); the box shows them.
            int[] types = VisionSettings.NormalizeWaterTypes(VisionFields.ParseCsv(_waterTypes));
            s.WaterTerrainTypes = types;
            _waterTypes = VisionFields.Csv(types);
            Change(st => st.WaterTerrainTypes = types);
        }
        NextLine(p, Px(26));

        p = ImGui.GetCursorScreenPos();
        if (Button("##inspect", "Log terrain types here", p, new Vector2(ButtonWidth("Log terrain types here") + 8, Px(24)), Text, ButtonFill))
            _plugin.QueueInspectTerrain();
        SafeImGui.SetItemTooltip("Writes the terrain types under you (stand outdoors) to the RynthCore log (/rv watertypes).");
        NextLine(p, Px(28));

        ImGui.PopStyleColor(5);
        ImGui.EndChild();
        return busy;
    }

    private void TakeSnapshot()
    {
        SettingsView? view = _plugin.Settings;
        if (view == null || view.Version == _seenVersion || _busy) return;
        _seenVersion = view.Version;
        _s = view.Settings.Clone();
        _slopeHex = _s.SlopeColorArgb.ToString("X8");
        _waterHex = _s.WaterColorArgb.ToString("X8");
        _radarHex = _s.RadarRingColorArgb.ToString("X8");
        _waterTypes = VisionFields.Csv(_s.WaterTerrainTypes);
    }

    /// <summary>Queues one field's change for the tick (applied, saved, republished).</summary>
    private void Change(Action<VisionSettings> change) => _plugin.QueueSettingsChange(change);

    private void Header(string text)
    {
        ImGui.Dummy(new Vector2(0, Px(4)));
        using (PushFont(RynthUiFont.UiBold11))
            SafeImGui.TextColored(HeaderColor, text);
    }

    /// <summary>Name | hex box | swatch. Commits on Enter or focus loss; bad input reverts. True while typing.</summary>
    private bool ColorRow(string label, string id, ref string hex, ref uint argb, Action<VisionSettings, uint> set)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float h = Px(22);
        dl.AddText(new Vector2(p.X, p.Y + (h - ImGui.GetFontSize()) * 0.5f), Text, label);
        bool enter = TextBox(id, ref hex, new Vector2(p.X + Px(48), p.Y), Px(92), "", out bool typing,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase);
        bool done = ImGui.IsItemDeactivatedAfterEdit();
        if (enter || done)
        {
            if (VisionFields.TryParseHex(hex, out uint c))
            {
                argb = c;
                Change(st => set(st, c));
            }
            hex = argb.ToString("X8");
        }
        Vector2 sw = new(p.X + Px(146), p.Y + Px(3));
        Vector2 swSize = new(Px(22), Px(16));
        dl.AddRectFilled(sw, sw + swSize, Argb(argb));
        dl.AddRect(sw, sw + swSize, Mute);
        NextLine(p, Px(26));
        return typing;
    }

    /// <summary>
    /// The colour's alpha as 0-100 %: label, then the slider. Rewrites the alpha byte (RGB kept) and the hex
    /// box as it moves, saves the colour on release. Untouched, the colour is never rewritten. True while in use.
    /// </summary>
    private bool OpacitySlider(string label, string id, ref string hex, ref uint argb, Action<VisionSettings, uint> set)
    {
        ImGui.TextUnformatted(label);
        float pct = VisionFields.OpacityPercent(argb);
        if (ImGui.SliderFloat(id, ref pct, 0f, 100f, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
        {
            argb = VisionFields.WithOpacity(argb, pct);
            hex = argb.ToString("X8");
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) { uint c = argb; Change(st => set(st, c)); }
        SafeImGui.SetItemTooltip("0% = invisible, 100% = solid. Ctrl+click to type a value");
        return ImGui.IsItemActive();
    }

    /// <summary>Label, then the slider; saves on release (or after Ctrl+click typing). True while in use.</summary>
    private bool Slider(string label, string id, ref float value, float min, float max, string format, Action<VisionSettings, float> set)
    {
        ImGui.TextUnformatted(label);
        ImGui.SliderFloat(id, ref value, min, max, format, ImGuiSliderFlags.AlwaysClamp);
        if (ImGui.IsItemDeactivatedAfterEdit()) { float v = value; Change(st => set(st, v)); }
        ImGui.SetItemTooltip("Ctrl+click to type a value");
        return ImGui.IsItemActive();
    }

    private bool IntSlider(string label, string id, ref int value, int min, int max, Action<VisionSettings, int> set)
    {
        ImGui.TextUnformatted(label);
        ImGui.SliderInt(id, ref value, min, max, "%d", ImGuiSliderFlags.AlwaysClamp);
        if (ImGui.IsItemDeactivatedAfterEdit()) { int v = value; Change(st => set(st, v)); }
        ImGui.SetItemTooltip("Ctrl+click to type a value");
        return ImGui.IsItemActive();
    }

    // ── Light (the dungeon light) ────────────────────────────────────────

    private static readonly string[] ScopeLabels = { "Dungeons", "Indoors", "Everywhere" };
    private static readonly string[] ScopeIds = { "##light_scope0", "##light_scope1", "##light_scope2" };
    private static readonly string[] ScopeTips =
    {
        "Only dungeons: the cells AC itself lights with its dark 20 % dungeon ambient.",
        "Dungeons and building interiors.",
        "Everywhere, outdoors too (dark nights). Never darker than AC's own light.",
    };
    private static readonly string[] PresetIds = { "##light_white", "##light_torch", "##light_moon" };
    private static readonly string[] PresetLabels = { "White", "Torch", "Moon" };

    private const string LightHelp =
        "Lights up dark dungeons, like SkunkVision's Dungeon Light.\n\n" +
        "It raises the ambient light this client draws the world with: walls, floors, monsters and items " +
        "all get brighter, torches and the fog stay as they are. It is drawing only: nothing is sent to the " +
        "server and nobody else sees a difference.\n\n" +
        "Strength 0 is AC's own dungeon (20 % ambient), 100 lights everything fully (flat, no shading).\n\n" +
        "Chat: /rv light (on/off), /rv light 0-100, /rv light dungeons|indoors|everywhere, " +
        "/rv light color RRGGBB|white|torch|moon, /rv light status, /rv light show (this tab).";

    private void DrawLight(float w, float room)
    {
        LightView view = _plugin.LightState;
        ImGui.BeginChild("##vision_light", new Vector2(w, room));
        float cw = ImGui.GetContentRegionAvail().X - Px(4);

        Header("Dungeon light");
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(26), iconW = Px(24);
        string label = view.On ? "Dungeon light: ON" : "Dungeon light: OFF";
        if (Button("##light_toggle", label, p, new Vector2(cw - iconW - Px(4), h), Text, view.On ? AccentFill : ButtonFill,
                border: view.On ? Accent : 0))
        {
            bool on = !view.On;
            _plugin.QueueSettingsChange(st => st.LightOn = on);
        }
        // Tooltips go through SafeImGui: ImGui reads them as printf formats ("20 %" would read an argument).
        SafeImGui.SetItemTooltip("Switch the dungeon light on or off (/rv light).");
        IconButton("##light_help", RynthIcons.Info, new Vector2(p.X + cw - iconW, p.Y), new Vector2(iconW, h), Mute, ButtonFill,
            font: RynthUiFont.Ui11);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
            ImGui.TextUnformatted(LightHelp);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        NextLine(p, h + Px(6));

        // What it does here: "Lighting in a dungeon: ambient 20% -> 52%." (or why it can't).
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + cw);
        ImGui.PushStyleColor(ImGuiCol.Text, view.Warn ? Highlight : Mute);
        SafeImGui.TextWrapped(view.Status);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        Header("Strength");
        int v = _dragStrength ?? view.Strength;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ButtonFill);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Highlight);
        ImGui.SetNextItemWidth(cw);
        bool changed = ImGui.SliderInt("##light_strength", ref v, 0, 100, "%d%%", ImGuiSliderFlags.AlwaysClamp);
        bool active = ImGui.IsItemActive();
        bool released = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.PopStyleColor(3);
        SafeImGui.SetItemTooltip("0 = AC's own dungeon (20 % ambient), 100 = fully lit. Ctrl+click to type a value.");
        int strength = v;
        if (changed) _plugin.QueueSettingsChange(st => st.LightStrength = Math.Clamp(strength, 0, 100), save: false);   // lights as it moves
        if (released) _plugin.QueueSettingsChange(st => st.LightStrength = Math.Clamp(strength, 0, 100));               // saved when let go
        _dragStrength = active ? v : null;

        Header("Where");
        p = ImGui.GetCursorScreenPos();
        float bh = Px(22), gap = Px(4);
        float bw = MathF.Floor((cw - 2 * gap) / 3f);
        for (int i = 0; i < 3; i++)
        {
            bool sel = (int)view.Scope == i;
            if (Button(ScopeIds[i], ScopeLabels[i], new Vector2(p.X + i * (bw + gap), p.Y), new Vector2(bw, bh),
                    sel ? Accent : Text, sel ? Selected : ButtonFill, border: sel ? Accent : 0))
            {
                int scope = i;
                _plugin.QueueSettingsChange(st => st.LightScope = scope);
            }
            SafeImGui.SetItemTooltip(ScopeTips[i]);
        }
        NextLine(p, bh + Px(4));

        Header("Colour");
        DrawLightColor(view, cw);

        ImGui.EndChild();
    }

    /// <summary>Swatch, the presets (white, torch, moon), then any colour as RRGGBB hex.</summary>
    private void DrawLightColor(LightView view, float cw)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float h = Px(22), gap = Px(4);
        dl.AddRectFilled(p, p + new Vector2(h, h), Argb(view.ColorArgb | 0xFF000000u), 3);
        dl.AddRect(p, p + new Vector2(h, h), Border, 3);

        float x = p.X + h + gap;
        float hexW = Px(70);
        float bw = MathF.Floor((cw - h - hexW - 4 * gap) / 3f);
        for (int i = 0; i < 3; i++)
        {
            uint preset = DungeonLight.Presets[i].Argb;
            bool sel = (view.ColorArgb & 0xFFFFFF) == (preset & 0xFFFFFF);
            if (Button(PresetIds[i], PresetLabels[i], new Vector2(x, p.Y), new Vector2(bw, h),
                    sel ? Accent : Text, sel ? Selected : ButtonFill, border: sel ? Accent : 0))
                SetLightColor(preset);
            x += bw + gap;
        }

        if (!_lightHexEditing) _lightHex = (view.ColorArgb & 0xFFFFFF).ToString("X6");
        bool enter = TextBox("##light_hex", ref _lightHex, new Vector2(x, p.Y), p.X + cw - x, "", out _lightHexEditing,
            ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase | ImGuiInputTextFlags.EnterReturnsTrue, 7);
        bool done = ImGui.IsItemDeactivatedAfterEdit();
        SafeImGui.SetItemTooltip("Any colour as RRGGBB hex; Enter to use it.");
        if ((enter || done) && DungeonLight.TryParseColor(_lightHex, out uint c))
            SetLightColor(c);
        NextLine(p, h + Px(6));
    }

    private void SetLightColor(uint argb)
    {
        uint c = 0xFF000000u | (argb & 0x00FFFFFFu);
        _plugin.QueueSettingsChange(st => st.LightColorArgb = c);
    }

    // ── Layout and the engine faces' text box ────────────────────────────

    /// <summary>Moves the layout cursor to the start of the line below a row drawn at <paramref name="rowStart"/>.</summary>
    private static void NextLine(Vector2 rowStart, float rowHeight)
    {
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowStart.Y + rowHeight));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>
    /// FaceKit's bordered text box with a grey <paramref name="hint"/> while empty. True when Enter was
    /// pressed (with EnterReturnsTrue) or the text changed; <paramref name="active"/> while it is typed in.
    /// </summary>
    private bool TextBox(string id, ref string text, Vector2 pos, float width, string hint, out bool active,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, uint maxLength = 128)
    {
        float height = Px(22);
        ImGui.SetCursorScreenPos(pos);
        ImGui.SetNextItemWidth(width);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, ButtonFill);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, (height - ImGui.GetFontSize()) * 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        bool result = ImGui.InputText(id, ref text, maxLength, flags);
        active = ImGui.IsItemActive();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
        if (text.Length == 0 && !active && hint.Length > 0)
            ImGui.GetWindowDrawList().AddText(pos + new Vector2(5, (height - ImGui.GetFontSize()) * 0.5f), Faded(Mute), hint);
        return result;
    }
}
