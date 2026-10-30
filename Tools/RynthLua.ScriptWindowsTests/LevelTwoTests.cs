using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using MoonSharp.Interpreter;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthLua;

namespace RynthLua.ScriptWindowsTests
{
    /// <summary>
    /// Op level 2 (2026-09-30): Image / ImageButton (AC icons) and InputInt / InputFloat /
    /// DragInt / DragFloat, from the recorder and the Lua API through the engine's parser, the
    /// hostile cases, and the engine's icon decoder against the real client_portal.dat.
    /// </summary>
    internal static partial class Program
    {
        /// <summary>The level-2 checks ReplaySafe adds: empty when the op is safe to replay.</summary>
        private static string ReplaySafeLevelTwo(DisplayList l, ReplayOp o)
        {
            const uint NumberInputMask = 64 | 512 | 4096 | 8192 | 16384;
            const uint DragMask = 32 | 64 | 128 | 256 | 512 | 1024;
            bool FormatOk(bool isInt) =>
                o.Str2 >= 0 && DisplayListParser.IsValidSliderFormat(l.Pool.AsSpan(o.Str2, o.Str2Len), isInt);
            switch (o.Code)
            {
                case ScriptOp.Image:
                case ScriptOp.ImageButton:
                    if (o.A < 0 || o.B < 0) return "negative image size";
                    if (o.Flag is not (0 or 1 or 2 or 0xFF)) return $"icon kind {o.Flag}";
                    if (Math.Abs(o.C) > 16 || Math.Abs(o.D) > 16 || Math.Abs(o.E) > 16 || Math.Abs(o.F) > 16) return "uv out of range";
                    if (o.Code == ScriptOp.ImageButton && o.Str < 0) return "no ID string";
                    break;
                case ScriptOp.InputInt:
                    if (o.Str < 0) return "no label";
                    if ((o.Flags & ~NumberInputMask) != 0) return $"flags 0x{o.Flags:X}";
                    break;
                case ScriptOp.InputFloat:
                    if (o.Str < 0 || !FormatOk(isInt: false)) return "label or format";
                    if ((o.Flags & ~NumberInputMask) != 0) return $"flags 0x{o.Flags:X}";
                    break;
                case ScriptOp.DragInt:
                    if (o.Str < 0 || !FormatOk(isInt: true)) return "label or format";
                    if (o.I1 > o.I2 || o.I1 < -(int.MaxValue / 2) || o.I2 > int.MaxValue / 2) return "range";
                    if ((o.Flags & ~DragMask) != 0) return $"flags 0x{o.Flags:X}";
                    break;
                case ScriptOp.DragFloat:
                    if (o.Str < 0 || !FormatOk(isInt: false)) return "label or format";
                    if (o.B > o.C || o.B < -float.MaxValue / 2 || o.C > float.MaxValue / 2) return "range";
                    if ((o.Flags & ~DragMask) != 0) return $"flags 0x{o.Flags:X}";
                    break;
                case ScriptOp.SliderInt:
                    if (!FormatOk(isInt: true)) return "format";
                    break;
                case ScriptOp.SliderFloat:
                    if (!FormatOk(isInt: false)) return "format";
                    break;
            }
            return string.Empty;
        }

        /// <summary>Every level-2 op from the recorder through the engine's parser, with its clamps and masks.</summary>
        private static void LevelTwoOps()
        {
            ScriptHud h = NewHud("L2");
            var r = new HudRecorder();
            r.Begin(h, 1500, 65536);
            r.Image(HudFormat.IconKindIcon, 0x1234, 24, 20, new Vector2(0.1f, 0.2f), new Vector2(0.9f, 1f), 0xFF00FF00, 0x80FFFFFF);
            r.Image(HudFormat.IconKindObject, 0x80001234, 32, 32, Vector2.Zero, Vector2.One, 0xFFFFFFFF, 0);
            r.Image(HudFormat.IconKindSpell, 2345, -5, 1e9f, new Vector2(-100, 0), new Vector2(100, 1), 0xFFFFFFFF, 0);
            r.Image(9, 5, 8, 8, Vector2.Zero, Vector2.One, 0xFFFFFFFF, 0);   // a kind this engine doesn't know
            uint kib = r.KeyOf("ib");
            r.ImageButton(kib, "ib", HudFormat.IconKindObject, 77, 16, 16, Vector2.Zero, Vector2.One, 0x11223344, 0xFFFFFFFF);
            uint kii = r.KeyOf("ii");
            r.InputInt(kii, "ii", -5, 2, 20, 64 | 512 | 2);                  // CharsHexadecimal (2) is dropped
            uint kif = r.KeyOf("if");
            r.InputFloat(kif, "if", 1.5f, 0.5f, 5f, "%.1f", 64 | 1);          // CharsDecimal (1) is dropped
            uint kdi = r.KeyOf("di");
            r.DragInt(kdi, "di", 7, 0.5f, 10, 0, "%d pts", 1536 | 1);         // min/max swapped; bit 0 dropped
            uint kdf = r.KeyOf("df");
            r.DragFloat(kdf, "df", 0.3f, 0.01f, 1f, 0f, "%s", 32);            // "%s" isn't a float format
            r.DragFloat(r.KeyOf("df2"), "df2", 1f, 1e12f, 0, 0, "", 0);       // speed clamped
            r.DragInt(r.KeyOf("di2"), "di2", 0, 1, int.MinValue, int.MaxValue, "", 0);
            r.End();
            Commit(h);
            DisplayList l = ParseOne(h);
            CheckCodes(l, new[]
            {
                ScriptOp.Image, ScriptOp.Image, ScriptOp.Image, ScriptOp.Image, ScriptOp.ImageButton,
                ScriptOp.InputInt, ScriptOp.InputFloat, ScriptOp.DragInt, ScriptOp.DragFloat, ScriptOp.DragFloat, ScriptOp.DragInt,
            }, "level 2");
            ReplayOp[] o = l.Ops;
            Check(o[0].Flag == 0 && o[0].I0 == 0x1234 && o[0].A == 24 && o[0].B == 20, "Image by icon id: kind, id, size");
            Check(o[0].C == 0.1f && o[0].D == 0.2f && o[0].E == 0.9f && o[0].F == 1f, "Image uv0 / uv1");
            Check(o[0].Color == 0xFF00FF00 && o[0].Color2 == 0x80FFFFFF, "Image tint / border");
            Check(o[1].Flag == 1 && unchecked((uint)o[1].I0) == 0x80001234u, "Image by object id (above 0x7FFFFFFF)");
            Check(o[2].Flag == 2 && o[2].I0 == 2345, "Image by spell id");
            Check(o[2].A == 0 && o[2].B == 4000 && o[2].C == -16 && o[2].E == 16, $"Image size 0..max, uvs within 16 ({o[2].A}, {o[2].B}, {o[2].C}, {o[2].E})");
            Check(o[3].Flag == (byte)ScriptIconKind.Unknown, "an unknown icon kind is kept as Unknown (a placeholder)");
            Check(o[4].Key == kib && S(l, o[4].Str, o[4].StrLen) == "ib" && o[4].Flag == 1 && o[4].I0 == 77
                  && o[4].Color == 0x11223344 && o[4].Color2 == 0xFFFFFFFF, "ImageButton: key, ID, icon, bg, tint");
            Check(o[5].Key == kii && o[5].I0 == -5 && o[5].I1 == 2 && o[5].I2 == 20 && o[5].Flags == (64 | 512), "InputInt: value, steps, flags masked");
            Check(o[6].Key == kif && o[6].A == 1.5f && o[6].B == 0.5f && o[6].C == 5f && S(l, o[6].Str2, o[6].Str2Len) == "%.1f" && o[6].Flags == 64, "InputFloat");
            Check(o[7].Key == kdi && o[7].I0 == 7 && o[7].D == 0.5f && o[7].I1 == 0 && o[7].I2 == 10 && S(l, o[7].Str2, o[7].Str2Len) == "%d pts" && o[7].Flags == 1536, "DragInt: min/max ordered, flags masked");
            Check(o[8].Key == kdf && o[8].A == 0.3f && o[8].D == 0.01f && o[8].B == 0 && o[8].C == 1 && S(l, o[8].Str2, o[8].Str2Len) == "%.3f" && o[8].Flags == 32, "DragFloat: an invalid format falls back to %.3f");
            Check(o[9].D == 1_000_000f && S(l, o[9].Str2, o[9].Str2Len) == "%.3f", "DragFloat speed clamped; empty format = default");
            Check(o[10].I1 == -(int.MaxValue / 2) && o[10].I2 == int.MaxValue / 2 && S(l, o[10].Str2, o[10].Str2Len) == "%d", "DragInt range clamped to ±int.MaxValue/2");
            Check(ReplaySafe(l).Length == 0, "the list is safe to replay: " + ReplaySafe(l));
            Check(DisplayListParser.OpLevel == HudFormat.OpLevel, "the recorder and the engine agree on the op level");
        }

        /// <summary>A submit whose one window body is <paramref name="body"/> (the op stream, End included).</summary>
        private static unsafe int ParseBody(byte[] body, out List<ParsedWindow> windows, out string error)
        {
            ScriptHud h = NewHud("Raw");
            h.Body = body;
            h.BodyLength = body.Length;
            h.BodyDirty = true;
            return Parse(new[] { h }, out windows, out error, out _);
        }

        private static byte[] OneOp(byte code, byte[] payload, byte opFlags = 0, byte[]? block = null)
        {
            var b = new List<byte> { code, opFlags };
            b.AddRange(BitConverter.GetBytes((ushort)payload.Length));
            b.AddRange(payload);
            if (block != null)
            {
                b.AddRange(BitConverter.GetBytes((uint)block.Length));
                b.AddRange(block);
            }
            b.AddRange(new byte[] { 0, 0, 0, 0 });   // End
            return b.ToArray();
        }

        private sealed class Pay
        {
            private readonly List<byte> _b = new();
            public Pay U8(byte v) { _b.Add(v); return this; }
            public Pay U32(uint v) { _b.AddRange(BitConverter.GetBytes(v)); return this; }
            public Pay I32(int v) { _b.AddRange(BitConverter.GetBytes(v)); return this; }
            public Pay F32(float v) { _b.AddRange(BitConverter.GetBytes(v)); return this; }
            public Pay Str(string s) { byte[] u = Encoding.UTF8.GetBytes(s); _b.AddRange(BitConverter.GetBytes((ushort)u.Length)); _b.AddRange(u); return this; }
            public Pay Raw(params byte[] b) { _b.AddRange(b); return this; }
            public byte[] Bytes => _b.ToArray();
        }

        private static Pay ImagePay(byte kind, uint id, float w, float h, float u0, float v0, float u1, float v1) =>
            new Pay().U8(kind).U32(id).F32(w).F32(h).F32(u0).F32(v0).F32(u1).F32(v1).U32(0xFFFFFFFF).U32(0);

        /// <summary>Hand-made hostile level-2 ops: refused, or accepted only in a safe shape.</summary>
        private static void LevelTwoHostile()
        {
            void Refused(byte[] body, string what)
            {
                int rc = ParseBody(body, out _, out string err);
                Check(rc != 0, $"{what}: refused ({rc}: {err})");
            }
            ReplayOp Accepted(byte[] body, string what)
            {
                int rc = ParseBody(body, out var w, out string err);
                Check(rc == 0 && w[0].List != null && w[0].List!.OpCount == 1, $"{what}: accepted ({rc}: {err})");
                if (rc != 0 || w[0].List == null || w[0].List!.OpCount == 0) return default;
                Check(ReplaySafe(w[0].List!).Length == 0, $"{what}: safe to replay: {ReplaySafe(w[0].List!)}");
                return w[0].List!.Ops[0];
            }

            Refused(OneOp(0x28, ImagePay(0, 1, 24, 24, float.NaN, 0, 1, 1).Bytes), "Image with a NaN uv");
            Refused(OneOp(0x28, ImagePay(0, 1, float.PositiveInfinity, 24, 0, 0, 1, 1).Bytes), "Image with an infinite size");
            Refused(OneOp(0x28, new Pay().U8(0).U32(1).Bytes), "Image payload cut short");
            Refused(OneOp(0x28, ImagePay(0, 1, 24, 24, 0, 0, 1, 1).Bytes, opFlags: 1, block: Array.Empty<byte>()), "Image with a block");
            Refused(OneOp(0x29, new Pay().U32(5).Raw(200, 0).Raw(1, 2, 3).Bytes), "ImageButton whose ID runs past the payload");
            Refused(OneOp(0x2B, new Pay().U32(1).Str("f").F32(1).F32(float.PositiveInfinity).F32(0).Str("").U32(0).Bytes), "InputFloat with an infinite step");
            Refused(OneOp(0x2D, new Pay().U32(1).Str("d").F32(float.NaN).F32(1).F32(0).F32(1).Str("").U32(0).Bytes), "DragFloat with a NaN value");
            Refused(OneOp(0x2C, new Pay().U32(1).Str("d").I32(0).F32(1).I32(0).Bytes), "DragInt cut short");

            ReplayOp img = Accepted(OneOp(0x28, ImagePay(200, 0xFFFFFFFF, -1e30f, 1e30f, -1e30f, 1e30f, 0, 0).Bytes), "Image with a wild kind, sizes and uvs");
            Check(img.Flag == 0xFF && img.A == 0 && img.B == 4000 && img.C == -16 && img.D == 16, "...clamped: kind Unknown, size 0..max, uvs within 16");
            ReplayOp extra = Accepted(OneOp(0x28, ImagePay(1, 7, 8, 8, 0, 0, 1, 1).Raw(9, 9, 9, 9, 9).Bytes), "Image with extra trailing payload (a newer recorder)");
            Check(extra.Flag == 1 && extra.I0 == 7, "...read as usual, the extra bytes ignored");
            ReplayOp di = Accepted(OneOp(0x2C, new Pay().U32(1).Str("d").I32(5).F32(-1e30f).I32(int.MaxValue).I32(int.MinValue).Str("%n").U32(uint.MaxValue).Bytes), "DragInt with wild numbers, %n and every flag bit");
            Check(di.D == -1_000_000f && di.I1 == -(int.MaxValue / 2) && di.I2 == int.MaxValue / 2 && di.Flags == 2016, $"...speed and range clamped, flags masked ({di.D}, {di.I1}, {di.I2}, {di.Flags})");
            ReplayOp df = Accepted(OneOp(0x2D, new Pay().U32(1).Str("d").F32(0).F32(1).F32(float.MaxValue).F32(-float.MaxValue).Str("%f %f").U32(0).Bytes), "DragFloat with extreme range and two conversions");
            Check(df.B == -float.MaxValue / 2 && df.C == float.MaxValue / 2, "...range ordered and clamped");
            ReplayOp ii = Accepted(OneOp(0x2A, new Pay().U32(1).Str("i").I32(int.MinValue).I32(int.MaxValue).I32(int.MinValue).U32(uint.MaxValue).Bytes), "InputInt with extreme values and every flag bit");
            Check(ii.I0 == int.MinValue && ii.Flags == (64 | 512 | 4096 | 8192 | 16384), "...value kept, flags masked");
            ReplayOp ifl = Accepted(OneOp(0x2B, new Pay().U32(1).Str("f").F32(3).F32(float.MaxValue).F32(-float.MaxValue).Str("%.9999f").U32(0).Bytes), "InputFloat with a too-precise format");
            Check(ifl.B == float.MaxValue / 2 && ifl.C == -float.MaxValue / 2, "...steps clamped");
        }

        /// <summary>The Lua side: views' icon textures, Image / ImageButton, the number inputs, their events and errors.</summary>
        private static void LuaIconsAndNumbers()
        {
            RynthLuaPlugin p = NewViewsPlugin(TempFile(), "Srv|Alice");
            UiAvailableField(p) = true;
            UiInfoAtField(p) = long.MaxValue;
            ViewsRig r = ViewsWith(p, """
                views = require("views"); ImGui = require("imgui")
                hud = views.Huds.CreateHud("Icons"); hud.Visible = true
                tex = views.Huds.GetIconTexture(0x1234)
                otex = views.Huds.GetObjectIconTexture({ Id = 0x80001234, Name = "x" })
                stex = views.Huds.GetSpellIconTexture(2345)
                same = views.Huds.GetIconTexture(0x1234) == tex
                st = { ii = 5, fl = 0.1, di = 3, df = 0.25 }
                r = {}
                errs = nil
                local function e(f, ...)
                  local ok, m = pcall(f, ...)
                  errs[#errs + 1] = ok and "ok" or tostring(m)
                end
                hud.OnRender.Add(function()
                  ImGui.Image(tex.TexturePtr, Vector2.new(24, 24))
                  ImGui.Image(otex)
                  ImGui.Image(0x06001234, {16, 16}, {0, 0}, {0.5, 0.5}, Vector4.new(1, 0, 0, 1), 0xFF00FF00)
                  ImGui.Image(stex, Vector2.new(20, 20))
                  r.ib = ImGui.ImageButton("ib", otex, Vector2.new(20, 20))
                  r.iic, st.ii = ImGui.InputInt("II", st.ii)
                  r.flc, st.fl = ImGui.InputFloat("IF", st.fl, 0.5, 1, "%.2f")
                  r.dic, st.di = ImGui.DragInt("DI", st.di, 0.5, 0, 10)
                  r.dfc, st.df = ImGui.DragFloat("DF", st.df, 0.01, 0, 1, "%.2f", ImGuiSliderFlags.AlwaysClamp)
                  if errs then
                    e(ImGui.Image, "abc")
                    e(ImGui.Image, {})
                    e(ImGui.Image, -1)
                    e(ImGui.Image, 1.5)
                    e(ImGui.Image, tex, "big")
                    e(ImGui.ImageButton, "b", nil)
                    e(ImGui.Image, tex, nil, "uv")
                    e(ImGui.Image, tex, nil, nil, nil, "red")
                    e(ImGui.Image, 0)
                  end
                end)
                """);
            ScriptHud h = r.Hud("Icons");
            Check(r.Eval("same").CastToBool(), "the same icon gives the same texture table");
            Check(r.Eval("tostring(tex)").String == "IconTexture(icon 0x00001234)" && r.Eval("tex.Kind").String == "icon" && r.Eval("tex.Id").Number == 0x1234,
                "texture: tostring, Kind, Id: " + r.Eval("tostring(tex)").String);
            Check(r.Eval("tex.TexturePtr == tex").CastToBool(), "TexturePtr is the texture itself (UB's ImGui.Image(tex.TexturePtr, ...))");
            Check(r.Eval("otex.Kind").String == "object" && r.Eval("otex.Id").Number == 0x80001234 && r.Eval("stex.Kind").String == "spell", "object and spell textures");
            r.Do("tex.Dispose(); tex.Release()");
            Check(true, "Dispose / Release do nothing");
            LuaFails(r, "local x = tex.Bitmap", "isn't available in RynthLua");
            LuaFails(r, "tex.Foo = 1", "read-only");
            LuaFails(r, "views.Huds.GetIconTexture('x')", "needs iconId");
            LuaFails(r, "views.Huds.GetIconTexture(-1)", "needs iconId");
            LuaFails(r, "views.Huds.GetIconTexture(1.5)", "needs iconId");
            LuaFails(r, "views.Huds.GetObjectIconTexture({})", "needs an object or object id");
            LuaFails(r, "views.Huds.GetSpellIconTexture(nil)", "needs a spell or spell id");
            LuaFails(r, "views.Huds.CreateTexture('a.png')", "isn't supported");
            LuaFails(r, "ImGui.Image(tex)", "only work inside a hud's OnRender");

            RunPass(p, h);
            Check(h.LastError == null, "pass ok: " + h.LastError);
            h.BodyDirty = true;
            DisplayList l = ParseOne(h);
            CheckCodes(l, new[]
            {
                ScriptOp.Image, ScriptOp.Image, ScriptOp.Image, ScriptOp.Image, ScriptOp.ImageButton,
                ScriptOp.InputInt, ScriptOp.InputFloat, ScriptOp.DragInt, ScriptOp.DragFloat,
            }, "Lua level 2");
            ReplayOp[] o = l.Ops;
            Check(o[0].Flag == 0 && o[0].I0 == 0x1234 && o[0].A == 24, "Image(tex.TexturePtr, size): the icon id as given (the engine adds 0x06000000)");
            Check(o[1].Flag == 1 && unchecked((uint)o[1].I0) == 0x80001234u && o[1].A == 32 && o[1].B == 32, "Image(objectTexture): default size 32 x 32");
            Check(o[2].Flag == 0 && o[2].I0 == 0x06001234 && o[2].E == 0.5f && o[2].Color == 0xFF0000FF && o[2].Color2 == 0xFF00FF00, "Image(number, {16,16}, uv0, uv1, Vector4 tint, number border)");
            Check(o[3].Flag == 2 && o[3].I0 == 2345, "Image(spellTexture)");
            uint kib = Fnv("ib", h.Hash);
            Check(o[4].Key == kib && o[4].Flag == 1 && o[4].Color == 0 && o[4].Color2 == 0xFFFFFFFF, "ImageButton: key, default bg and tint");
            Check(o[5].I0 == 5 && o[5].I1 == 1 && o[5].I2 == 100, "InputInt defaults (step 1, step_fast 100)");
            Check(o[6].A == 0.1f && o[6].B == 0.5f && o[6].C == 1 && S(l, o[6].Str2, o[6].Str2Len) == "%.2f", "InputFloat");
            Check(o[7].I0 == 3 && o[7].D == 0.5f && o[7].I1 == 0 && o[7].I2 == 10 && S(l, o[7].Str2, o[7].Str2Len) == "%d", "DragInt, default format");
            Check(o[8].A == 0.25f && o[8].D == 0.01f && o[8].Flags == 1536, "DragFloat with ImGuiSliderFlags.AlwaysClamp");
            Check(r.Eval("st.fl == 0.1").CastToBool(), "unchanged, InputFloat returns the script's number exactly");
            Check(!r.Eval("r.ib").CastToBool() && !r.Eval("r.iic").CastToBool(), "nothing clicked or changed yet");

            // The player: clicks the icon button, types 42, 2.5, drags to 7 and 0.5.
            byte[] events = Ev(HudFormat.EvClicked, 1, h.Hash, kib, 1, 0, 0)
                .Concat(Ev(HudFormat.EvInt, 2, h.Hash, Fnv("II", h.Hash), Le(42)))
                .Concat(Ev(HudFormat.EvFloat, 3, h.Hash, Fnv("IF", h.Hash), Lef(2.5f)))
                .Concat(Ev(HudFormat.EvInt, 4, h.Hash, Fnv("DI", h.Hash), Le(7)))
                .Concat(Ev(HudFormat.EvFloat, 5, h.Hash, Fnv("DF", h.Hash), Lef(0.5f))).ToArray();
            ApplyHudEvents(p, events);
            RunPass(p, h);
            Check(r.Eval("r.ib").CastToBool(), "ImageButton returns true after the click");
            Check(r.Eval("r.iic").CastToBool() && r.Eval("st.ii").Number == 42, "InputInt: changed, 42");
            Check(r.Eval("r.flc").CastToBool() && r.Eval("st.fl").Number == 2.5, "InputFloat: changed, 2.5");
            Check(r.Eval("r.dic").CastToBool() && r.Eval("st.di").Number == 7, "DragInt: changed, 7");
            Check(r.Eval("r.dfc").CastToBool() && r.Eval("st.df").Number == 0.5, "DragFloat: changed, 0.5");
            h.BodyDirty = true;
            l = ParseOne(h);
            Check(l.Ops[5].I0 == 42 && l.Ops[6].A == 2.5f && l.Ops[7].I0 == 7 && l.Ops[8].A == 0.5f, "the next list carries the new values");
            RunPass(p, h);
            Check(!r.Eval("r.ib").CastToBool() && !r.Eval("r.iic").CastToBool() && !r.Eval("r.dfc").CastToBool(), "one pass later: no click, no change");

            // Argument errors inside a pass.
            r.Do("errs = {}");
            RunPass(p, h);
            string[] errs = Enumerable.Range(1, 9).Select(i => r.Eval($"errs[{i}]").CastToString() ?? "nil").ToArray();
            string[] want =
            {
                "the texture must come from", "the texture must come from", "the texture must come from", "the texture must come from",
                "size must be a Vector2", "the texture must come from", "uv0 must be a Vector2", "a colour is", "ok",
            };
            for (int i = 0; i < want.Length; i++)
                Check(errs[i].Contains(want[i]), $"error {i + 1}: '{errs[i]}' should contain '{want[i]}'");
            r.Do("errs = nil");

            // An engine without op level 2: the functions refuse rather than record ops it would skip.
            UiOpLevelField(p) = 1;
            RunPass(p, h);
            Check(h.LastError != null && h.LastError.Contains("ImGui.Image needs a newer RynthCore engine (script windows op level 2; this engine has 1)"),
                "old engine: a clear error: " + h.LastError);
            UiOpLevelField(p) = HudFormat.OpLevel;
            RunPass(p, h);
            Check(h.LastError == null, "back on a level-2 engine the pass works: " + h.LastError);
        }

        /// <summary>
        /// The engine's icon decoder against the real client_portal.dat (read only; skipped when it
        /// isn't there): the reader finds files in the B-tree, icons decode to sane RGBA, and the
        /// underlay / icon / overlay stacking puts the icon over the underlay.
        /// </summary>
        private static void IconDecoderOnRealDat()
        {
            Check(AcIconDecoder.NormalizeIconId(0x1234) == 0x06001234 && AcIconDecoder.NormalizeIconId(0x06001234) == 0x06001234
                  && AcIconDecoder.NormalizeIconId(0x80001234) == 0x80001234, "icon ids below 0x06000000 get 0x06000000 added (UB)");

            // A texture file made by hand: 2 x 2 A8R8G8B8 (stored B, G, R, A).
            var hand = new List<byte>();
            foreach (int v in new[] { 0x06000001, 0, 2, 2, 21, 16 }) hand.AddRange(BitConverter.GetBytes(v));
            hand.AddRange(new byte[] { 3, 2, 1, 255, 0, 0, 0, 0, 10, 20, 30, 128, 255, 255, 255, 255 });
            AcIconImage? tiny = AcIconDecoder.DecodeTextureFile(hand.ToArray(), _ => null);
            Check(tiny != null && tiny.Width == 2 && tiny.Pixels[0] == 1 && tiny.Pixels[2] == 3 && tiny.Pixels[3] == 255 && tiny.Pixels[7] == 0 && tiny.Pixels[11] == 128,
                "A8R8G8B8 decodes to RGBA");
            byte[] cut = hand.Take(30).ToArray();
            Check(AcIconDecoder.DecodeTextureFile(cut, _ => null) == null, "a texture file shorter than it says is refused");
            hand[8] = 0; hand[9] = 0x10;   // width 4096: over the icon limit
            Check(AcIconDecoder.DecodeTextureFile(hand.ToArray(), _ => null) == null, "a texture over 128 wide (not an icon) is refused");

            const string path = @"C:\Turbine\Asheron's Call\client_portal.dat";
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine("   (skipped the real-dat checks: no " + path + ")");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using AcDatFile? dat = AcDatFile.Open(path, out string why);
            Check(dat != null, "client_portal.dat opens read-only: " + why);
            if (dat == null) return;
            byte[]? spells = dat.Read(0x0E00000E, 64 * 1024 * 1024);
            Check(spells != null && spells.Length > 100_000, $"the spell table (0x0E00000E) is found ({spells?.Length ?? 0} bytes)");
            Check(dat.Read(0x06FFFFFF, 1 << 20) == null && AcIconDecoder.Decode(dat, 0x06FFFFFF) == null, "a missing id is null, not an error");
            Check(AcIconDecoder.Decode(dat, 0x0E00000E) == null, "a non-texture id isn't decoded as an icon");

            var found = new List<(uint Id, AcIconImage Img)>();
            for (uint id = 0x06001000; id < 0x06001800 && found.Count < 12; id++)
            {
                AcIconImage? img = AcIconDecoder.Decode(dat, id);
                if (img != null && img.Width == 32 && img.Height == 32) found.Add((id, img));
            }
            Console.WriteLine($"   decoded 32x32 icons: {string.Join(", ", found.Select(f => "0x" + f.Id.ToString("X8")))} ({sw.ElapsedMilliseconds} ms)");
            Check(found.Count >= 8, $"at least 8 icons decode from 0x06001000 up ({found.Count})");
            foreach (var (id, img) in found)
            {
                bool sized = img.Pixels.Length == img.Width * img.Height * 4;
                int opaque = 0;
                var colours = new HashSet<int>();
                for (int i = 0; sized && i < img.Pixels.Length; i += 4)
                {
                    if (img.Pixels[i + 3] > 0) opaque++;
                    colours.Add(img.Pixels[i] | (img.Pixels[i + 1] << 8) | (img.Pixels[i + 2] << 16));
                }
                Check(sized && opaque > 0 && colours.Count > 4, $"0x{id:X8}: {img.Width}x{img.Height}, {opaque} visible pixels, {colours.Count} colours");
            }
            if (found.Count < 2) return;

            // Stacking: an icon with transparent pixels over another as the underlay.
            var (iconId, icon) = found.FirstOrDefault(f => Enumerable.Range(0, 1024).Any(i => f.Img.Pixels[i * 4 + 3] == 0));
            if (icon == null) { Console.WriteLine("   (no icon with transparent pixels in the sample; stacking check skipped)"); return; }
            var (underId, under) = found.First(f => f.Id != iconId);
            AcIconImage? both = AcIconDecoder.Compose(dat, iconId, underId, 0);
            Check(both != null && both.Width == icon.Width && both.Height == icon.Height, "stacked: the icon's size");
            // Since 2026-10-09 (engine fd12e93) Compose repaints the dat's white outline band, as the client
            // does, so the icon on its own is Compose with no layers, not the raw Decode.
            AcIconImage? alone = AcIconDecoder.Compose(dat, iconId, 0, 0);
            Check(alone != null && alone.Pixels.Length == icon.Pixels.Length, "the icon alone: same size as decoded");
            if (both == null || alone == null) return;
            bool overOk = true, underOk = true;
            for (int i = 0; i < 1024; i++)
            {
                int a = icon.Pixels[i * 4 + 3];
                if (a == 255 && !both.Pixels.AsSpan(i * 4, 4).SequenceEqual(alone.Pixels.AsSpan(i * 4, 4))) overOk = false;
                if (a == 0 && !both.Pixels.AsSpan(i * 4, 4).SequenceEqual(under.Pixels.AsSpan(i * 4, 4)) && under.Pixels[i * 4 + 3] == 255) underOk = false;
            }
            Check(overOk, $"stacked: opaque icon pixels win (0x{iconId:X8} over 0x{underId:X8})");
            Check(underOk, "stacked: where the icon is clear, the underlay shows");
            AcIconImage? missingLayers = AcIconDecoder.Compose(dat, iconId, 0x06FFFFFE, 0x06FFFFFD);
            Check(missingLayers != null && missingLayers.Pixels.AsSpan().SequenceEqual(alone.Pixels), "a missing underlay / overlay is left out");
            Check(AcIconDecoder.Compose(dat, 0x06FFFFFE, underId, 0) == null, "no picture when the icon itself is missing");
        }
    }
}
