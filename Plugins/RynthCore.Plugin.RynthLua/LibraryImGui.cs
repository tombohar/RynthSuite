using System;
using System.Collections.Generic;
using System.Numerics;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The imgui module: require("imgui"), also as require("imgui").ImGui. Docs/RYNTHLUA_WINDOWS_DESIGN.md
/// §1.3 lists the Phase 1 functions; §9 the op-level-2 ones (Image, ImageButton, InputInt,
/// InputFloat, DragInt, DragFloat), which need an engine reporting OpLevel 2 (RequireOpLevel).
/// RYNTHLUA.md's table and the LuaLS types list exactly what is here (the offline tests check).
///
/// - Names, argument order and defaults follow ImGui.NET 1.91. ref/out parameters come back as
///   extra return values (changed, v = ImGui.Checkbox("x", v)); Combo's index is 0-based.
/// - Every recording function works only inside a hud's render pass (OnPreRender / OnRender);
///   results come one pass late (the engine replays the list and sends events back).
/// - Value widgets compute the key (KeyOf) first, then take the pending result (Take*), then record
///   with that key; a changed value is written into the list, so it agrees with the engine.
/// - SetNextWindowSize/Pos/SizeConstraints set the hud's header fields (by ImGuiCond), not ops.
/// - GetWindowSize/Pos and GetContentRegionAvail return last frame's geometry (inside a pass);
///   GetTextLineHeight, GetFrameHeight, GetStyle and CalcTextSize use UiGetInfo's metrics and
///   work any time, with defaults until the engine has reported them.
/// - Unknown names raise "ImGui.X isn't supported by RynthLua windows yet".
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private const string ImGuiRegistryKey = "rynth.imgui";

    /// <summary>PushStyleColor ids the engine replays (§4.2 whitelist), ImGui.NET 1.91.6.1 values.</summary>
    private static readonly (string Name, int Id)[] StyleColorWhitelist =
    {
        ("Text", 0), ("TextDisabled", 1), ("ChildBg", 3), ("Border", 5), ("FrameBg", 7), ("CheckMark", 18),
        ("SliderGrab", 19), ("Button", 21), ("ButtonHovered", 22), ("ButtonActive", 23), ("Header", 24),
        ("HeaderHovered", 25), ("PlotHistogram", 44),
    };

    private const uint TreeNodeNoTreePushOnOpen = 8;
    private const uint TreeNodeDefaultOpen = 32;
    private const uint InputTextEnterReturnsTrue = 64;
    private const int DefaultInputTextMax = 256;

    private DynValue CreateImGuiModule(Script s)
    {
        DynValue cached = s.Registry.Get(ImGuiRegistryKey);
        if (cached.Type == DataType.Table) return cached;

        var im = new Table(s);

        // Every function: ImGui.X(...) and ImGui:X(...) both work; anything but a
        // ScriptRuntimeException thrown inside becomes one (nothing else leaves a callback).
        void Fn(string name, Func<DynValue[], DynValue> body)
        {
            im[name] = DynValue.NewCallback((c, a) =>
            {
                try { return body(LuaArgs.Of(a, im)); }
                catch (InterpreterException) { throw; }
                catch (Exception ex) { throw new ScriptRuntimeException($"ImGui.{name}: {ex.Message}"); }
            });
        }

        // ── Text ────────────────────────────────────────────────────────
        Fn("Text", args => { Recording("Text").Text(HudFormat.OpText, TextOf(args)); return DynValue.Nil; });
        Fn("TextWrapped", args => { Recording("TextWrapped").Text(HudFormat.OpTextWrapped, TextOf(args)); return DynValue.Nil; });
        Fn("TextDisabled", args => { Recording("TextDisabled").Text(HudFormat.OpTextDisabled, TextOf(args)); return DynValue.Nil; });
        Fn("BulletText", args => { Recording("BulletText").Text(HudFormat.OpBulletText, TextOf(args)); return DynValue.Nil; });
        Fn("SeparatorText", args => { Recording("SeparatorText").Text(HudFormat.OpSeparatorText, TextOf(args)); return DynValue.Nil; });
        Fn("TextColored", args =>
        {
            HudRecorder rec = Recording("TextColored");
            uint col = LibraryImGuiValues.ColorU32(LuaArgs.At(args, 0));
            rec.TextColored(col, TextOf(args, 1));
            return DynValue.Nil;
        });
        Fn("LabelText", args =>
        {
            HudRecorder rec = Recording("LabelText");
            rec.LabelText(LuaArgs.Str(LuaArgs.At(args, 0)), TextOf(args, 1));
            return DynValue.Nil;
        });

        // ── Layout ──────────────────────────────────────────────────────
        Fn("Separator", args => { Recording("Separator").Separator(); return DynValue.Nil; });
        Fn("NewLine", args => { Recording("NewLine").NewLine(); return DynValue.Nil; });
        Fn("Spacing", args => { Recording("Spacing").Spacing(); return DynValue.Nil; });
        Fn("SameLine", args =>
        {
            HudRecorder rec = Recording("SameLine");
            float offset = (float)LuaArgs.Num(LuaArgs.At(args, 0), 0);
            float spacing = (float)LuaArgs.Num(LuaArgs.At(args, 1), -1);
            rec.SameLine(offset, spacing);
            return DynValue.Nil;
        });
        Fn("Dummy", args =>
        {
            HudRecorder rec = Recording("Dummy");
            SizeArg(args, 0, "Dummy", "size", out float w, out float h);
            rec.Dummy(w, h);
            return DynValue.Nil;
        });
        Fn("Indent", args => { Recording("Indent").Indent((float)LuaArgs.Num(LuaArgs.At(args, 0), 0)); return DynValue.Nil; });
        Fn("Unindent", args => { Recording("Unindent").Unindent((float)LuaArgs.Num(LuaArgs.At(args, 0), 0)); return DynValue.Nil; });

        // ── Buttons ─────────────────────────────────────────────────────
        Fn("Button", args =>
        {
            HudRecorder rec = Recording("Button");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            SizeArg(args, 1, "Button", "size", out float w, out float h);   // nil = (0, 0): auto
            uint key = rec.KeyOf(label);
            bool clicked = TakeClick(rec.Hud!, key);
            rec.Button(key, label, w, h, small: false);
            return DynValue.NewBoolean(clicked);
        });
        Fn("SmallButton", args =>
        {
            HudRecorder rec = Recording("SmallButton");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            uint key = rec.KeyOf(label);
            bool clicked = TakeClick(rec.Hud!, key);
            rec.Button(key, label, 0, 0, small: true);
            return DynValue.NewBoolean(clicked);
        });

        // ── Values: changed, v ──────────────────────────────────────────
        Fn("Checkbox", args =>
        {
            HudRecorder rec = Recording("Checkbox");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            bool value = LuaArgs.At(args, 1).CastToBool();
            uint key = rec.KeyOf(label);
            bool changed = TakeBool(rec.Hud!, key, out bool newValue);
            if (changed) value = newValue;
            rec.Checkbox(key, label, value);   // the list carries the new value, so it agrees with the engine
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewBoolean(value));
        });
        Fn("SliderInt", args =>
        {
            HudRecorder rec = Recording("SliderInt");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            int value = IntArg(LuaArgs.At(args, 1), 0);
            int min = IntArg(LuaArgs.At(args, 2), 0);
            int max = IntArg(LuaArgs.At(args, 3), 100);
            string fmt = LuaArgs.Str(LuaArgs.At(args, 4));   // "" = the engine's "%d"; the engine validates it
            uint key = rec.KeyOf(label);
            bool changed = TakeInt(rec.Hud!, key, out int newValue);
            if (changed) value = newValue;
            rec.SliderInt(key, label, value, min, max, fmt);
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewNumber(value));
        });
        Fn("SliderFloat", args =>
        {
            HudRecorder rec = Recording("SliderFloat");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            double given = LuaArgs.Num(LuaArgs.At(args, 1), 0);
            float min = (float)LuaArgs.Num(LuaArgs.At(args, 2), 0);
            float max = (float)LuaArgs.Num(LuaArgs.At(args, 3), 1);
            string fmt = LuaArgs.Str(LuaArgs.At(args, 4));   // "" = the engine's "%.3f"; the engine validates it
            uint key = rec.KeyOf(label);
            bool changed = TakeFloat(rec.Hud!, key, out float newValue);
            rec.SliderFloat(key, label, changed ? newValue : (float)given, min, max, fmt);
            // Unchanged, the script's own number comes back as it was (no float rounding).
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewNumber(changed ? newValue : given));
        });
        Fn("InputText", args =>
        {
            HudRecorder rec = Recording("InputText");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            string text = LuaArgs.Str(LuaArgs.At(args, 1));
            int maxLen = IntArg(LuaArgs.At(args, 2), DefaultInputTextMax);
            uint flags = FlagsArg(LuaArgs.At(args, 3));
            uint key = rec.KeyOf(label);
            bool changed = false;
            if (TakeText(rec.Hud!, key, out string newText, out bool submitted))
            {
                // ImGui.NET updates the ref string on every edit; with EnterReturnsTrue only the
                // return value waits for Enter.
                text = newText;
                changed = (flags & InputTextEnterReturnsTrue) == 0 || submitted;
            }
            rec.InputText(key, label, text, maxLen, flags);
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewString(text));
        });
        Fn("Combo", args =>
        {
            HudRecorder rec = Recording("Combo");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            int index = IntArg(LuaArgs.At(args, 1), 0);
            List<string> items = ComboItems(LuaArgs.At(args, 2), LuaArgs.At(args, 3));
            uint key = rec.KeyOf(label);
            bool changed = TakeInt(rec.Hud!, key, out int newIndex);
            if (changed) index = newIndex;
            rec.Combo(key, label, index, items);
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewNumber(index));
        });
        Fn("Selectable", args =>
        {
            HudRecorder rec = Recording("Selectable");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            bool selected = LuaArgs.At(args, 1).CastToBool();
            uint flags = FlagsArg(LuaArgs.At(args, 2));
            SizeArg(args, 3, "Selectable", "size", out float w, out float h);
            uint key = rec.KeyOf(label);
            bool clicked = TakeClick(rec.Hud!, key);
            // The list carries the selected state the script passed: the usual value form
            // (Selectable(name, sel == i)) sets its own state from the click next pass.
            rec.Selectable(key, label, selected, flags, w, h);
            // UB's ref form: clicked, selected (toggled by the click). `if Selectable(...)` still works.
            return DynValue.NewTuple(DynValue.NewBoolean(clicked), DynValue.NewBoolean(clicked ? !selected : selected));
        });

        // ── Numbers (op level 2): changed, v ────────────────────────────
        Fn("InputInt", args =>
        {
            HudRecorder rec = Recording("InputInt");
            RequireOpLevel("InputInt");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            int value = IntArg(LuaArgs.At(args, 1), 0);
            int step = IntArg(LuaArgs.At(args, 2), 1);          // ImGui.NET's defaults
            int stepFast = IntArg(LuaArgs.At(args, 3), 100);
            uint flags = FlagsArg(LuaArgs.At(args, 4));
            uint key = rec.KeyOf(label);
            bool changed = TakeInt(rec.Hud!, key, out int newValue);
            if (changed) value = newValue;
            rec.InputInt(key, label, value, step, stepFast, flags);
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewNumber(value));
        });
        Fn("InputFloat", args =>
        {
            HudRecorder rec = Recording("InputFloat");
            RequireOpLevel("InputFloat");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            double given = LuaArgs.Num(LuaArgs.At(args, 1), 0);
            float step = (float)LuaArgs.Num(LuaArgs.At(args, 2), 0);
            float stepFast = (float)LuaArgs.Num(LuaArgs.At(args, 3), 0);
            string fmt = LuaArgs.Str(LuaArgs.At(args, 4));   // "" = the engine's "%.3f"; the engine validates it
            uint flags = FlagsArg(LuaArgs.At(args, 5));
            uint key = rec.KeyOf(label);
            bool changed = TakeFloat(rec.Hud!, key, out float newValue);
            rec.InputFloat(key, label, changed ? newValue : (float)given, step, stepFast, fmt, flags);
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewNumber(changed ? newValue : given));
        });
        Fn("DragInt", args =>
        {
            HudRecorder rec = Recording("DragInt");
            RequireOpLevel("DragInt");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            int value = IntArg(LuaArgs.At(args, 1), 0);
            float speed = (float)LuaArgs.Num(LuaArgs.At(args, 2), 1);
            int min = IntArg(LuaArgs.At(args, 3), 0);            // min == max: no bounds (ImGui)
            int max = IntArg(LuaArgs.At(args, 4), 0);
            string fmt = LuaArgs.Str(LuaArgs.At(args, 5));   // "" = the engine's "%d"
            uint flags = FlagsArg(LuaArgs.At(args, 6));       // ImGuiSliderFlags
            uint key = rec.KeyOf(label);
            bool changed = TakeInt(rec.Hud!, key, out int newValue);
            if (changed) value = newValue;
            rec.DragInt(key, label, value, speed, min, max, fmt, flags);
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewNumber(value));
        });
        Fn("DragFloat", args =>
        {
            HudRecorder rec = Recording("DragFloat");
            RequireOpLevel("DragFloat");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            double given = LuaArgs.Num(LuaArgs.At(args, 1), 0);
            float speed = (float)LuaArgs.Num(LuaArgs.At(args, 2), 1);
            float min = (float)LuaArgs.Num(LuaArgs.At(args, 3), 0);
            float max = (float)LuaArgs.Num(LuaArgs.At(args, 4), 0);
            string fmt = LuaArgs.Str(LuaArgs.At(args, 5));   // "" = the engine's "%.3f"
            uint flags = FlagsArg(LuaArgs.At(args, 6));       // ImGuiSliderFlags
            uint key = rec.KeyOf(label);
            bool changed = TakeFloat(rec.Hud!, key, out float newValue);
            rec.DragFloat(key, label, changed ? newValue : (float)given, speed, min, max, fmt, flags);
            return DynValue.NewTuple(DynValue.NewBoolean(changed), DynValue.NewNumber(changed ? newValue : given));
        });

        // ── Icons (op level 2) ──────────────────────────────────────────
        Fn("Image", args =>
        {
            HudRecorder rec = Recording("Image");
            RequireOpLevel("Image");
            IconArg(s, LuaArgs.At(args, 0), "Image", out byte kind, out uint id);
            IconSizeArg(args, 1, "Image", out float w, out float h);
            Vector2 uv0 = OptVec(args, 2, "Image", "uv0", Vector2.Zero);
            Vector2 uv1 = OptVec(args, 3, "Image", "uv1", Vector2.One);
            uint tint = OptColor(args, 4, 0xFFFFFFFF);
            uint border = OptColor(args, 5, 0);
            rec.Image(kind, id, w, h, uv0, uv1, tint, border);
            return DynValue.Nil;
        });
        Fn("ImageButton", args =>
        {
            HudRecorder rec = Recording("ImageButton");
            RequireOpLevel("ImageButton");
            string strId = LuaArgs.Str(LuaArgs.At(args, 0));
            IconArg(s, LuaArgs.At(args, 1), "ImageButton", out byte kind, out uint id);
            IconSizeArg(args, 2, "ImageButton", out float w, out float h);
            Vector2 uv0 = OptVec(args, 3, "ImageButton", "uv0", Vector2.Zero);
            Vector2 uv1 = OptVec(args, 4, "ImageButton", "uv1", Vector2.One);
            uint bg = OptColor(args, 5, 0);
            uint tint = OptColor(args, 6, 0xFFFFFFFF);
            uint key = rec.KeyOf(strId);
            bool clicked = TakeClick(rec.Hud!, key);
            rec.ImageButton(key, strId, kind, id, w, h, uv0, uv1, bg, tint);
            return DynValue.NewBoolean(clicked);
        });

        // ── Display ─────────────────────────────────────────────────────
        Fn("ProgressBar", args =>
        {
            HudRecorder rec = Recording("ProgressBar");
            float fraction = (float)LuaArgs.Num(LuaArgs.At(args, 0), 0);
            float w = -1, h = 0;   // ImGui's default: fill the width, frame height
            if (!LuaArgs.At(args, 1).IsNil()) SizeArg(args, 1, "ProgressBar", "size", out w, out h);
            DynValue overlay = LuaArgs.At(args, 2);
            rec.ProgressBar(fraction, w, h, overlay.IsNil() ? string.Empty : overlay.ToPrintString());   // "" = ImGui's percentage
            return DynValue.Nil;
        });

        // ── Containers ──────────────────────────────────────────────────
        Fn("BeginChild", args =>
        {
            HudRecorder rec = Recording("BeginChild");
            string id = LuaArgs.At(args, 0).IsNil() ? string.Empty : LuaArgs.At(args, 0).ToPrintString();
            SizeArg(args, 1, "BeginChild", "size", out float w, out float h);
            DynValue b = LuaArgs.At(args, 2);
            // A boolean (the old `border` overload) or ImGuiChildFlags (Borders = bit0).
            bool border = b.Type == DataType.Boolean ? b.Boolean : (FlagsArg(b) & 1) != 0;
            uint windowFlags = FlagsArg(LuaArgs.At(args, 3));
            uint key = rec.KeyOf(id);
            rec.BeginChild(key, id, w, h, border, windowFlags);
            return DynValue.True;   // EndChild is always required (ImGui.NET semantics)
        });
        Fn("EndChild", args => { Recording("EndChild").EndChild(); return DynValue.Nil; });
        Fn("CollapsingHeader", args =>
        {
            HudRecorder rec = Recording("CollapsingHeader");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            if (LuaArgs.At(args, 1).Type == DataType.Boolean)
                throw new ScriptRuntimeException("ImGui.CollapsingHeader(label, visible) isn't supported yet; use CollapsingHeader(label [, flags])");
            // A header never pushes (NoTreePushOnOpen is part of what a header is).
            uint flags = FlagsArg(LuaArgs.At(args, 1)) & ~TreeNodeNoTreePushOnOpen;
            uint key = rec.KeyOf(label);
            bool open = GetOpenState(rec.Hud!, key, (flags & TreeNodeDefaultOpen) != 0);
            rec.CollapsingHeader(key, label, flags, open);
            return DynValue.NewBoolean(open);
        });
        Fn("TreeNode", args =>
        {
            HudRecorder rec = Recording("TreeNode");
            string label = LuaArgs.Str(LuaArgs.At(args, 0));
            uint flags = FlagsArg(LuaArgs.At(args, 1));
            // The engine owns TreePop, and the recorder expects one for every open node, so a node
            // that doesn't push can't be expressed yet.
            if ((flags & TreeNodeNoTreePushOnOpen) != 0)
                throw new ScriptRuntimeException("ImGui.TreeNode: the NoTreePushOnOpen flag isn't supported yet (TreePop is always needed after an open node)");
            uint key = rec.KeyOf(label);
            bool open = GetOpenState(rec.Hud!, key, (flags & TreeNodeDefaultOpen) != 0);
            rec.TreeNode(key, label, flags, open);   // recorded closed: the recorder closes the block itself
            return DynValue.NewBoolean(open);
        });
        Fn("TreePop", args => { Recording("TreePop").TreePop(); return DynValue.Nil; });

        // ── IDs and style ───────────────────────────────────────────────
        Fn("PushID", args =>
        {
            HudRecorder rec = Recording("PushID");
            DynValue v = LuaArgs.At(args, 0);
            if (v.Type == DataType.Number && IntegerId(v.Number, out int n)) rec.PushID(n);
            else if (v.Type is DataType.Number or DataType.String) rec.PushID(v.ToPrintString());
            else throw new ScriptRuntimeException($"ImGui.PushID needs a string or a number, not {v.Type.ToLuaTypeString()}");
            return DynValue.Nil;
        });
        Fn("PopID", args => { Recording("PopID").PopID(); return DynValue.Nil; });
        Fn("PushStyleColor", args =>
        {
            HudRecorder rec = Recording("PushStyleColor");
            int id = StyleColorId(LuaArgs.At(args, 0));
            uint col = LibraryImGuiValues.ColorU32(LuaArgs.At(args, 1));
            rec.PushStyleColor(id, col);
            return DynValue.Nil;
        });
        Fn("PopStyleColor", args => { Recording("PopStyleColor").PopStyleColor(IntArg(LuaArgs.At(args, 0), 1)); return DynValue.Nil; });

        // ── Tooltips ────────────────────────────────────────────────────
        Fn("SetItemTooltip", args => { Recording("SetItemTooltip").SetItemTooltip(TextOf(args)); return DynValue.Nil; });

        // ── Window (OnPreRender or OnRender): hud header fields, not ops ──
        Fn("SetNextWindowSize", args =>
        {
            ScriptHud hud = Recording("SetNextWindowSize").Hud!;
            Vector2 size = VecArg(args, 0, "SetNextWindowSize", "size");
            int cond = IntArg(LuaArgs.At(args, 1), CondNone);
            if (IsFirstUseEver(cond))
            {
                // A default size: used by the engine only when nothing is saved for the window.
                size = Finite(size);   // the header sends non-finite as 0 (= the engine default)
                if (hud.DefaultSize != size) { hud.DefaultSize = size; hud.PropsDirty = true; }
            }
            else if (CondFires(hud, cond))
            {
                // A per-pass request (reset before every pass); SubmitHuds' header compare sends a change.
                hud.RequestSize = size;
            }
            return DynValue.Nil;
        });
        Fn("SetNextWindowPos", args =>
        {
            ScriptHud hud = Recording("SetNextWindowPos").Hud!;
            Vector2 pos = VecArg(args, 0, "SetNextWindowPos", "pos");
            int cond = IntArg(LuaArgs.At(args, 1), CondNone);
            // args[2] (pivot) is accepted and ignored in the MVP.
            if (IsFirstUseEver(cond))
            {
                hud.RequestPos = pos;
                hud.PosIsDefault = true;   // a default position, not a request
            }
            else if (CondFires(hud, cond))
            {
                hud.RequestPos = pos;
                hud.PosIsDefault = false;
            }
            return DynValue.Nil;
        });
        Fn("SetNextWindowSizeConstraints", args =>
        {
            ScriptHud hud = Recording("SetNextWindowSizeConstraints").Hud!;
            Vector2 min = VecArg(args, 0, "SetNextWindowSizeConstraints", "min");
            VecArg(args, 1, "SetNextWindowSizeConstraints", "max");
            // MVP: only the minimum maps onto the panel (MinSize, persistent); the maximum is
            // checked for shape and ignored, as the panel host has no maximum size.
            min = Finite(min);
            if (hud.MinSize != min) { hud.MinSize = min; hud.PropsDirty = true; }
            return DynValue.Nil;
        });

        // ── Queries: last frame's window geometry (inside a pass) ───────
        // (0, 0) until the first Geometry event (0 = auto in every size argument).
        Fn("GetWindowSize", args => Vec2(s, Recording("GetWindowSize").Hud!.Geometry.WindowSize));
        Fn("GetWindowPos", args => Vec2(s, Recording("GetWindowPos").Hud!.Geometry.WindowPos));
        Fn("GetContentRegionAvail", args => Vec2(s, Recording("GetContentRegionAvail").Hud!.Geometry.ContentAvail));

        // ── Metrics (UiGetInfo; any time, logical units) ────────────────
        Fn("GetTextLineHeight", args => { RefreshUiInfo(force: false); return DynValue.NewNumber(_uiLineHeight); });
        Fn("GetTextLineHeightWithSpacing", args => { RefreshUiInfo(force: false); return DynValue.NewNumber(_uiLineHeight + _uiSpacingY); });
        Fn("GetFrameHeight", args => { RefreshUiInfo(force: false); return DynValue.NewNumber(_uiFrameHeight); });
        Fn("GetStyle", args =>
        {
            RefreshUiInfo(force: false);
            var style = new Table(s);
            style["ItemSpacing"] = LibraryImGuiValues.NewVec2(s, _uiSpacingX, _uiSpacingY);
            style["FramePadding"] = LibraryImGuiValues.NewVec2(s, _uiPaddingX, _uiPaddingY);
            var styleMt = new Table(s);
            styleMt["__index"] = DynValue.NewCallback((c, a) =>
            {
                string key = a.Count > 1 ? a[1].CastToString() ?? "?" : "?";
                throw new ScriptRuntimeException($"ImGui.GetStyle().{key} isn't supported by RynthLua windows yet (ItemSpacing and FramePadding are)");
            });
            style.MetaTable = styleMt;
            return DynValue.NewTable(style);
        });
        Fn("CalcTextSize", args =>
        {
            RefreshUiInfo(force: false);
            string text = LuaArgs.At(args, 0).IsNil() ? string.Empty : LuaArgs.At(args, 0).ToPrintString();
            bool hideAfterDoubleHash = LuaArgs.At(args, 1).CastToBool();
            // args[2] (wrapWidth) is ignored in the MVP.
            Vector2 size = CalcTextSize(text, hideAfterDoubleHash);
            return LibraryImGuiValues.NewVec2(s, size.X, size.Y);
        });

        // ── Clipboard (any time; Windows clipboard, Unicode text) ───────
        Fn("SetClipboardText", args =>
        {
            DynValue v = LuaArgs.At(args, 0);
            string text = v.IsNil() ? string.Empty : v.ToPrintString();
            return DynValue.NewBoolean(WinClipboard.SetText(text));
        });
        Fn("GetClipboardText", args => DynValue.NewString(WinClipboard.GetText()));

        im["ImGui"] = DynValue.NewTable(im);   // require("imgui").ImGui works too

        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "?" : "?";
            throw new ScriptRuntimeException($"ImGui.{key} isn't supported by RynthLua windows yet");
        });
        im.MetaTable = mt;

        DynValue module = DynValue.NewTable(im);
        s.Registry.Set(ImGuiRegistryKey, module);
        return module;
    }

    /// <summary>The recorder of the pass in progress; ImGui calls outside a pass are an error.</summary>
    private HudRecorder Recording(string fn)
    {
        if (_recorder.Hud == null)
            throw new ScriptRuntimeException($"ImGui.{fn}: ImGui calls only work inside a hud's OnRender");
        return _recorder;
    }

    /// <summary>The arguments from <paramref name="from"/> on, concatenated (Text("a", 1) = "a1").</summary>
    private static string TextOf(DynValue[] args, int from = 0)
    {
        int n = args.Length - from;
        if (n <= 1) return LuaArgs.At(args, from).ToPrintString();
        var parts = new string[n];
        for (int i = 0; i < n; i++) parts[i] = args[from + i].ToPrintString();
        return string.Concat(parts);
    }

    // ── Window calls and queries ────────────────────────────────────────

    // ImGuiCond (ImGui.NET 1.91.6.1)
    private const int CondNone = 0, CondAlways = 1, CondOnce = 2, CondFirstUseEver = 4, CondAppearing = 8;

    /// <summary>FirstUseEver (without Always): a default the engine uses only when nothing is saved.</summary>
    private static bool IsFirstUseEver(int cond) => (cond & CondFirstUseEver) != 0 && (cond & CondAlways) == 0;

    /// <summary>
    /// Whether a SetNextWindow* request applies this pass: None/Always every pass; Appearing on the
    /// first pass since the hud was shown; Once until the hud's first good pass.
    /// </summary>
    private static bool CondFires(ScriptHud hud, int cond)
    {
        if (cond == CondNone || (cond & CondAlways) != 0) return true;
        if ((cond & CondAppearing) != 0 && hud.FirstPassSinceShown) return true;
        if ((cond & CondOnce) != 0 && !hud.EverPassed) return true;
        return false;
    }

    private static Vector2 Finite(Vector2 v) => new(float.IsFinite(v.X) ? v.X : 0f, float.IsFinite(v.Y) ? v.Y : 0f);

    private static DynValue Vec2(Script s, Vector2 v) => LibraryImGuiValues.NewVec2(s, v.X, v.Y);

    /// <summary>A required Vector2 argument (Vector2, {x, y} or {X=, Y=}).</summary>
    private static Vector2 VecArg(DynValue[] args, int i, string fn, string what)
    {
        DynValue v = LuaArgs.At(args, i);
        if (LibraryImGuiValues.TryVec2(v, out float x, out float y)) return new Vector2(x, y);
        throw new ScriptRuntimeException($"ImGui.{fn}: {what} must be a Vector2 or {{x, y}}, not {v.Type.ToLuaTypeString()}");
    }

    /// <summary>
    /// CalcTextSize from UiGetInfo's default-font advances: exact for ASCII 32..126; any other
    /// character counts as the mean advance of 'a'..'z' (an estimate), a tab as four spaces.
    /// Lines split on \n (\r is skipped, as ImGui does): the width is the longest line, rounded up
    /// in pixels as ImGui does; the height is the line count times the line height. Logical units.
    /// </summary>
    private Vector2 CalcTextSize(string text, bool hideAfterDoubleHash)
    {
        if (hideAfterDoubleHash)
        {
            int cut = text.IndexOf("##", StringComparison.Ordinal);
            if (cut >= 0) text = text[..cut];
        }
        float[] adv = _uiAdvancePx;
        float mean = 0;
        for (int c = 'a'; c <= 'z'; c++) mean += adv[c - ' '];
        mean /= 26;

        float widest = 0, line = 0;
        int newlines = 0;
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                widest = Math.Max(widest, line);
                line = 0;
                newlines++;
                continue;
            }
            if (ch == '\r' || char.IsLowSurrogate(ch)) continue;   // a surrogate pair counts once
            line += ch is >= ' ' and <= '~' ? adv[ch - ' '] : ch == '\t' ? 4 * adv[0] : mean;
        }
        widest = Math.Max(widest, line);
        // ImGui: a trailing empty line after the last \n adds no height; an empty text is one line.
        int lines = newlines + (line > 0 || newlines == 0 ? 1 : 0);
        float widthPx = MathF.Truncate(widest + 0.99999f);
        return new Vector2(widthPx / _uiScale, lines * _uiLineHeight);
    }

    // ── Argument helpers ────────────────────────────────────────────────

    /// <summary>A size: Vector2, {x, y} or {X=, Y=}; nil = (0, 0); anything else is an error.</summary>
    private static void SizeArg(DynValue[] args, int i, string fn, string what, out float w, out float h)
    {
        DynValue v = LuaArgs.At(args, i);
        if (LibraryImGuiValues.TryVec2(v, out w, out h)) return;
        if (v.IsNil()) return;
        throw new ScriptRuntimeException($"ImGui.{fn}: {what} must be a Vector2 or {{x, y}}, not {v.Type.ToLuaTypeString()}");
    }

    /// <summary>The icon size AC draws when the script gives none (RynthLua extra: ImGui.NET requires a size).</summary>
    private const float DefaultIconSize = 32f;

    /// <summary>An icon's size: a Vector2 or {x, y}; nil = 32 x 32 (the size AC's icons are).</summary>
    private static void IconSizeArg(DynValue[] args, int i, string fn, out float w, out float h)
    {
        if (LuaArgs.At(args, i).IsNil()) { w = h = DefaultIconSize; return; }
        SizeArg(args, i, fn, "size", out w, out h);
    }

    /// <summary>An optional Vector2 argument; nil = <paramref name="fallback"/>, another type is an error.</summary>
    private static Vector2 OptVec(DynValue[] args, int i, string fn, string what, Vector2 fallback) =>
        LuaArgs.At(args, i).IsNil() ? fallback : VecArg(args, i, fn, what);

    /// <summary>An optional colour (Vector4 0..1 or 0xAABBGGRR); nil = <paramref name="fallback"/>.</summary>
    private static uint OptColor(DynValue[] args, int i, uint fallback) =>
        LuaArgs.At(args, i).IsNil() ? fallback : LibraryImGuiValues.ColorU32(LuaArgs.At(args, i));

    /// <summary>A number as an int (truncated, clamped to the int range); nil or NaN = <paramref name="fallback"/>.</summary>
    private static int IntArg(DynValue v, int fallback)
    {
        double d = LuaArgs.Num(v, fallback);
        if (double.IsNaN(d)) return fallback;
        return (int)Math.Clamp(Math.Truncate(d), int.MinValue, int.MaxValue);
    }

    /// <summary>A flags number as a u32 (a negative int wraps, as a C# enum cast would); nil = 0.</summary>
    private static uint FlagsArg(DynValue v)
    {
        double d = LuaArgs.Num(v, 0);
        if (!double.IsFinite(d)) return 0;
        d = Math.Truncate(d);
        if (d >= 0) return d >= uint.MaxValue ? uint.MaxValue : (uint)d;
        return unchecked((uint)(int)Math.Max(d, int.MinValue));
    }

    /// <summary>
    /// PushID's int form: an integer in the int range, or in the u32 range (object ids above
    /// 0x7FFFFFFF wrap, as C#'s unchecked (int) cast does). Anything else uses the string form.
    /// </summary>
    private static bool IntegerId(double d, out int n)
    {
        n = 0;
        if (!double.IsFinite(d) || Math.Floor(d) != d || d < int.MinValue || d > uint.MaxValue) return false;
        n = unchecked((int)(long)d);
        return true;
    }

    /// <summary>An ImGuiCol id (number, or a whitelisted name) that the engine replays; others are an error.</summary>
    private static int StyleColorId(DynValue v)
    {
        if (v.Type == DataType.Number && double.IsFinite(v.Number))
        {
            foreach (var (_, id) in StyleColorWhitelist)
                if (id == v.Number) return id;
        }
        else if (v.Type == DataType.String)
        {
            foreach (var (name, id) in StyleColorWhitelist)
                if (string.Equals(name, v.String, StringComparison.OrdinalIgnoreCase)) return id;
        }
        var names = new string[StyleColorWhitelist.Length];
        for (int i = 0; i < names.Length; i++) names[i] = StyleColorWhitelist[i].Name;
        throw new ScriptRuntimeException(
            $"ImGui.PushStyleColor: colour {v.ToPrintString()} isn't supported; use ImGuiCol.{string.Join(", ", names)}");
    }

    /// <summary>
    /// Combo items: a Lua array (1-based; <paramref name="count"/> limits how many are used) or
    /// ImGui's "a\0b\0c\0" string (ending at the first empty item; its optional 4th argument is
    /// ImGui.NET's popup height, which is ignored). At most 512 items are kept.
    /// </summary>
    private static List<string> ComboItems(DynValue items, DynValue count)
    {
        var list = new List<string>();
        if (items.Type == DataType.Table)
        {
            int n = items.Table.Length;
            if (!count.IsNil()) n = Math.Min(n, Math.Max(0, IntArg(count, n)));
            n = Math.Min(n, HudFormat.MaxComboItems);
            for (int i = 1; i <= n; i++)
            {
                DynValue it = items.Table.Get(i);
                list.Add(it.IsNil() ? string.Empty : it.ToPrintString());
            }
        }
        else if (items.Type == DataType.String)
        {
            string all = items.String;
            int start = 0;
            while (start < all.Length && list.Count < HudFormat.MaxComboItems)
            {
                int end = all.IndexOf('\0', start);
                if (end < 0) end = all.Length;
                if (end == start) break;   // an empty item ends the list ("\0\0")
                list.Add(all[start..end]);
                start = end + 1;
            }
        }
        else if (!items.IsNil())
        {
            throw new ScriptRuntimeException($"ImGui.Combo: items must be a table of strings or a \"\\0\"-separated string, not {items.Type.ToLuaTypeString()}");
        }
        return list;
    }
}
