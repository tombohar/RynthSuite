using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using MoonSharp.Interpreter;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Plugin.RynthLua;

// The engine parser's only outside dependency: the registry's limits (the same values as the engine's).
namespace RynthCore.Engine.UI.ScriptWindows
{
    internal static class ScriptWindowRegistry
    {
        public const int MaxOpsPerWindow = 1500;
        public const int MaxBytesPerWindow = 64 * 1024;
        public const int MaxWindowsPerOwner = 32;
        public const int MaxBytesPerSubmit = 256 * 1024;
    }
}

namespace RynthLua.ScriptWindowsTests
{
    internal static partial class Program
    {
        private static int _pass, _fail;

        // Private members of the plugin, reached without reflection.
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ApplyHudEvents")]
        private static extern void ApplyHudEvents(RynthLuaPlugin p, ReadOnlySpan<byte> data);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_hudsByHash")]
        private static extern ref Dictionary<uint, ScriptHud> HudsByHash(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_huds")]
        private static extern ref List<ScriptHud> Huds(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RunPass")]
        private static extern void RunPass(RynthLuaPlugin p, ScriptHud hud);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CreateImGuiModule")]
        private static extern DynValue CreateImGuiModule(RynthLuaPlugin p, Script s);

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "BenchCode")]
        private static extern string BenchCode(RynthLuaPlugin? p, int n);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CopyUiMetrics")]
        private static extern void CopyUiMetrics(RynthLuaPlugin p, RynthCore.PluginSdk.UiInfoNative info);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CreateViewsModule")]
        private static extern DynValue CreateViewsModule(RynthLuaPlugin p, Script s, ScriptContext ctx);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RunHudPasses")]
        private static extern void RunHudPasses(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HandleHudCommand")]
        private static extern void HandleHudCommand(RynthLuaPlugin p, string rest);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_hudMemory")]
        private static extern ref HudVisibilityMemory? HudMemoryField(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_HudCharacterOverride")]
        private static extern void SetCharacter(RynthLuaPlugin p, string? character);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_uiAvailable")]
        private static extern ref bool UiAvailableField(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_uiInfoAt")]
        private static extern ref long UiInfoAtField(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scripts")]
        private static extern ref Dictionary<string, ScriptContext> ScriptsField(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_ResumeFileOverride")]
        private static extern void SetResumeFile(RynthLuaPlugin p, string? path);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "LoadResume")]
        private static extern void LoadResume(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ResumeScripts")]
        private static extern void ResumeScripts(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_resumePending")]
        private static extern ref List<string>? ResumePendingField(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_resumePendingAt")]
        private static extern ref long ResumePendingAtField(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_uiOpLevel")]
        private static extern ref int UiOpLevelField(RynthLuaPlugin p);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_tmp")]
        private static extern ref byte[] RecorderTmp(HudRecorder r);

        private static int Main()
        {
            Run("every op round-trips", EveryOp);
            Run("header fields and error text", Header);
            Run("record-time mismatch errors", Mismatches);
            Run("depth limit", Depth);
            Run("auto-close with one warning", AutoClose);
            Run("size and count limits", Limits);
            Run("submit byte budget (several large huds at once)", SubmitBudget);
            Run("engine parser: truncated, bit-flipped and random submits", ParserFuzz);
            Run("events into results", Events);
            Run("Lua: Phase 0 imgui functions and the bench", LuaBench);
            Run("Lua: every MVP imgui function, with events", LuaMvpFunctions);
            Run("Lua: SetNextWindow* by ImGuiCond", LuaWindowCalls);
            Run("Lua: errors (unsupported, refused, outside a pass)", LuaErrors);
            Run("Lua: metrics and CalcTextSize", LuaMetrics);
            Run("Lua: clipboard", LuaClipboard);
            Run("Lua: the design doc's §1.4 examples", LuaDocExamples);
            Run("Lua: the Examples folder's window scripts", ExampleScripts);
            Run("Lua: Vector2 / Vector4 / colours", LuaVectors);
            Run("views: hud properties", ViewsProperties);
            Run("views: hud names that meet on one engine panel key", ViewsPanelKeyClash);
            Run("views: visibility, OnShow/OnHide, memory, /lua hud", ViewsVisibility);
            Run("views: pass scheduling and strikes", ViewsScheduling);
            Run("views: stop(), Dispose() and CreateHud inside a pass", ViewsStopDuringPass);
            Run("hud_visibility.json: load, save, cap, bad files", VisibilityMemoryFile);
            Run("resume.json: shutdown, reload, login", ResumeAfterReload);
            Run("resume.json: pid, age and bad files", ResumeFileChecks);
            Run("op level 2: icons and number inputs round-trip", LevelTwoOps);
            Run("op level 2: hostile icon and number ops", LevelTwoHostile);
            Run("Lua: icons (views textures, Image, ImageButton) and number inputs, with events", LuaIconsAndNumbers);
            Run("AC icon decoder on client_portal.dat (read only)", IconDecoderOnRealDat);
            Run("docs and LuaLS types match the imgui module", DocsAndTypes);
            CleanTempFiles();
            Run("ImGui enums match ImGui.NET 1.91.6.1", Enums);
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            Console.WriteLine("-- " + name);
            try { test(); }
            catch (Exception ex) { Check(false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Console.WriteLine("   FAIL " + what); }
        }

        private static void Throws(Action a, string contains)
        {
            try { a(); Check(false, $"expected an error containing '{contains}'"); }
            catch (ScriptRuntimeException ex) { Check(ex.Message.Contains(contains), $"error '{ex.Message}' should contain '{contains}'"); }
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static ScriptHud NewHud(string name, LuaScriptHost? host = null)
        {
            host ??= new LuaScriptHost(_ => { }, (_, _) => { });
            var ctx = new ScriptContext("t", null, string.Empty, new ScriptManifest(), host);
            return new ScriptHud(ctx, name, "RynthLua/t/" + name, ctx.Event("Hud:" + name + ":OnRender"), ctx.Event("Hud:" + name + ":OnPreRender"));
        }

        /// <summary>What RunPass does with a good recording.</summary>
        private static void Commit(ScriptHud h)
        {
            (h.Body, h.Scratch) = (h.Scratch, h.Body);
            h.BodyLength = h.ScratchLength;
            h.ListSeq++;
            h.BodyDirty = true;
        }

        private static unsafe int Parse(ScriptHud[] huds, out List<ParsedWindow> windows, out string error, out uint ack)
        {
            byte[] buf = new byte[16];
            int n = HudFormat.WriteSubmit(ref buf, huds, huds.Length, 77);
            fixed (byte* p = buf)
                return DisplayListParser.Parse(p, n, 4000f, out ack, out windows, out error);
        }

        private static DisplayList ParseOne(ScriptHud h)
        {
            int rc = Parse(new[] { h }, out var w, out string err, out _);
            Check(rc == 0, $"parse rc {rc}: {err}");
            if (rc != 0 || w[0].List == null) throw new Exception("no list");
            return w[0].List!;
        }

        private static string S(DisplayList l, int off, int len) => off < 0 ? string.Empty : Encoding.UTF8.GetString(l.Pool, off, len);

        private static uint Fnv(string s, uint h) => HudFormat.Fnv1a(Encoding.UTF8.GetBytes(s), h);

        // ── Tests ───────────────────────────────────────────────────────

        private static void EveryOp()
        {
            ScriptHud h = NewHud("All");
            var r = new HudRecorder();
            r.Begin(h, 1500, 65536);
            r.Text(HudFormat.OpText, "hello");                                           // 0
            r.TextColored(0xFF00FF00, "green");                                          // 1
            r.Text(HudFormat.OpTextWrapped, "wrap");                                     // 2
            r.Text(HudFormat.OpTextDisabled, "dis");                                     // 3
            r.Text(HudFormat.OpBulletText, "bullet");                                    // 4
            r.Text(HudFormat.OpSeparatorText, "sep");                                    // 5
            r.LabelText("lbl", "50%");                                                   // 6
            r.Separator();                                                               // 7
            r.SameLine(10, -1);                                                          // 8
            r.NewLine();                                                                 // 9
            r.Spacing();                                                                 // 10
            r.Dummy(5, 6);                                                               // 11
            r.Indent(7);                                                                 // 12
            r.Unindent(8);                                                               // 13
            uint kb = r.KeyOf("OK"); r.Button(kb, "OK", 100, 20, false);                 // 14
            uint kc = r.KeyOf("Check##c"); r.Checkbox(kc, "Check##c", true);             // 15
            uint ksi = r.KeyOf("SI"); r.SliderInt(ksi, "SI", 5, 0, 10, "%d units");      // 16
            uint ksf = r.KeyOf("SF"); r.SliderFloat(ksf, "SF", 0.5f, 0, 1, "%.2f");      // 17
            uint kit = r.KeyOf("IT"); r.InputText(kit, "IT", "héllo world", 4, 64 | 1u << 20);   // 18
            uint kco = r.KeyOf("CO"); r.Combo(kco, "CO", 1, new[] { "a", "", "c" });     // 19
            uint kse = r.KeyOf("SE"); r.Selectable(kse, "SE", true, 4, 0, 0);            // 20
            r.ProgressBar(0.25f, -1, 0, "quarter");                                      // 21
            r.PushID("row");                                                             // 22
            uint kin = r.KeyOf("X"); r.Button(kin, "X", 0, 0, true);                     // 23
            r.PopID();                                                                   // 24
            r.PushID(42);                                                                // 25
            uint k42 = r.KeyOf("Y###id"); r.Button(k42, "Y###id", 0, 0, false);          // 26
            r.PopID();                                                                   // 27
            r.PushStyleColor(21, 0xFF0000FF);                                            // 28
            r.PopStyleColor(1);                                                          // 29
            r.SetItemTooltip("tip");                                                     // 30
            uint kch = r.KeyOf("child"); r.BeginChild(kch, "child", 100, 50, true, 8);   // 31
            uint kcb = r.KeyOf("in child"); r.Button(kcb, "in child", 0, 0, false);      // 32
            r.EndChild();
            uint khd = r.KeyOf("Header"); r.CollapsingHeader(khd, "Header", 0, true);    // 33
            r.Text(HudFormat.OpText, "under header");                                    // 34
            uint ktn = r.KeyOf("Tree"); r.TreeNode(ktn, "Tree", 32, true);               // 35
            uint ktb = r.KeyOf("leaf"); r.Button(ktb, "leaf", 0, 0, false);              // 36
            r.TreePop();
            uint ktc = r.KeyOf("Closed"); r.TreeNode(ktc, "Closed", 0, false);           // 37
            r.Text(HudFormat.OpText, "end");                                             // 38
            r.End();
            Check(!h.ScopeWarned, "no scope warning for a balanced list");
            Commit(h);

            // Keys as specified.
            Check(kb == Fnv("OK", h.Hash), "key = FNV(hud hash, label)");
            Check(kc == Fnv("Check##c", h.Hash), "## is part of the ID");
            Check(kin == Fnv("X", Fnv("row", h.Hash)), "PushID(string) hashes into the stack");
            Span<byte> le = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(le, 42);
            Check(k42 == Fnv("###id", HudFormat.Fnv1a(le, h.Hash)), "PushID(int) + ### label");
            Check(kcb == Fnv("in child", kch), "a Child's key is its children's stack");
            Check(ktb == Fnv("leaf", ktn), "an open TreeNode's key is its children's stack");
            Check(khd == Fnv("Header", h.Hash) && ktc == Fnv("Closed", h.Hash), "keys after blocks close are back on the root stack");

            DisplayList l = ParseOne(h);
            ScriptOp[] codes =
            {
                ScriptOp.Text, ScriptOp.TextColored, ScriptOp.TextWrapped, ScriptOp.TextDisabled, ScriptOp.BulletText,
                ScriptOp.SeparatorText, ScriptOp.LabelText, ScriptOp.Separator, ScriptOp.SameLine, ScriptOp.NewLine,
                ScriptOp.Spacing, ScriptOp.Dummy, ScriptOp.Indent, ScriptOp.Unindent, ScriptOp.Button, ScriptOp.Checkbox,
                ScriptOp.SliderInt, ScriptOp.SliderFloat, ScriptOp.InputText, ScriptOp.Combo, ScriptOp.Selectable,
                ScriptOp.ProgressBar, ScriptOp.PushID, ScriptOp.Button, ScriptOp.PopID, ScriptOp.PushID, ScriptOp.Button,
                ScriptOp.PopID, ScriptOp.PushStyleColor, ScriptOp.PopStyleColor, ScriptOp.SetItemTooltip, ScriptOp.Child,
                ScriptOp.Button, ScriptOp.CollapsingHeader, ScriptOp.Text, ScriptOp.TreeNode, ScriptOp.Button,
                ScriptOp.TreeNode, ScriptOp.Text,
            };
            Check(l.OpCount == codes.Length, $"op count {l.OpCount}, expected {codes.Length}");
            for (int i = 0; i < Math.Min(l.OpCount, codes.Length); i++)
                Check(l.Ops[i].Code == codes[i], $"op {i}: {l.Ops[i].Code}, expected {codes[i]}");
            ReplayOp[] o = l.Ops;
            Check(S(l, o[0].Str, o[0].StrLen) == "hello", "Text string");
            Check(o[1].Color == 0xFF00FF00 && S(l, o[1].Str, o[1].StrLen) == "green", "TextColored");
            Check(S(l, o[5].Str, o[5].StrLen) == "sep", "SeparatorText");
            Check(S(l, o[6].Str, o[6].StrLen) == "lbl" && S(l, o[6].Str2, o[6].Str2Len) == "50%%", "LabelText (engine doubles %)");
            Check(o[8].A == 10 && o[8].B == -1, "SameLine");
            Check(o[11].A == 5 && o[11].B == 6 && o[12].A == 7 && o[13].A == 8, "Dummy / Indent / Unindent");
            Check(o[14].Key == kb && S(l, o[14].Str, o[14].StrLen) == "OK" && o[14].A == 100 && o[14].B == 20 && o[14].Flag == 0, "Button");
            Check(o[15].Key == kc && o[15].Flag == 1, "Checkbox");
            Check(o[16].Key == ksi && o[16].I0 == 5 && o[16].I1 == 0 && o[16].I2 == 10 && S(l, o[16].Str2, o[16].Str2Len) == "%d units", "SliderInt");
            Check(o[17].Key == ksf && o[17].A == 0.5f && o[17].B == 0 && o[17].C == 1 && S(l, o[17].Str2, o[17].Str2Len) == "%.2f", "SliderFloat");
            Check(o[18].Key == kit && S(l, o[18].Str2, o[18].Str2Len) == "hél" && o[18].Aux == 4 && o[18].Flags == 64, "InputText (value cut to maxLen, flags masked)");
            Check(o[19].Key == kco && o[19].I0 == 1 && o[19].Aux == 3 && S(l, o[19].Str2, o[19].Str2Len) == "a\0 \0c\0", "Combo items pool");
            Check(o[20].Key == kse && o[20].Flag == 1 && o[20].Flags == 4, "Selectable");
            Check(o[21].A == 0.25f && o[21].B == -1 && o[21].C == 0 && S(l, o[21].Str2, o[21].Str2Len) == "quarter", "ProgressBar");
            Check(o[22].Flag == 0 && S(l, o[22].Str, o[22].StrLen) == "row", "PushID(string)");
            Check(o[23].Key == kin && o[23].Flag == 1, "SmallButton inside PushID");
            Check(o[25].Flag == 1 && o[25].I0 == 42, "PushID(int)");
            Check(o[28].Aux == 21 && o[28].Color == 0xFF0000FF && o[29].Aux == 1, "PushStyleColor / PopStyleColor");
            Check(S(l, o[30].Str, o[30].StrLen) == "tip", "SetItemTooltip");
            Check(o[31].Key == kch && o[31].A == 100 && o[31].B == 50 && o[31].Flag == 1 && o[31].Flags == 8 && o[31].EndIndex == 33, "Child block");
            Check(o[33].Key == khd && o[33].Flag == 1 && o[33].EndIndex == 34, "CollapsingHeader is a plain op");
            Check(o[35].Key == ktn && o[35].Flags == 32 && o[35].Flag == 1 && o[35].EndIndex == 37, "open TreeNode block");
            Check(o[37].Key == ktc && o[37].Flag == 0 && o[37].EndIndex == 38, "closed TreeNode: an empty block");
        }

        private static void Header()
        {
            ScriptHud a = NewHud("A");
            var r = new HudRecorder();
            r.Begin(a, 1500, 65536);
            r.Text(HudFormat.OpText, "x");
            r.End();
            Commit(a);
            a.Title = "Tïtle";
            a.Visible = true;
            a.ChromeNone = true;
            a.NoScrollbar = true;
            a.ShowInBar = false;
            a.DefaultSize = new System.Numerics.Vector2(400, 300);
            a.MinSize = new System.Numerics.Vector2(100, 50);
            a.RequestSize = new System.Numerics.Vector2(300, 200);
            a.RequestPos = new System.Numerics.Vector2(10, 20);
            a.PosIsDefault = true;
            a.LastError = string.Concat(Enumerable.Repeat("é", 700)) + "\nline 2";   // 1400+ bytes

            ScriptHud b = NewHud("B");                  // no body yet sent as "unchanged", defaults
            b.BodyDirty = false;
            b.RequestSize = new System.Numerics.Vector2(float.PositiveInfinity, 3);   // not finite: no request

            int rc = Parse(new[] { a, b }, out var w, out string err, out uint ack);
            Check(rc == 0, $"parse rc {rc}: {err}");
            if (rc != 0) return;
            Check(ack == 77, "AckSeq");
            ParsedWindow pa = w[0], pb = w[1];
            uint want = HudFormat.FlagVisible | HudFormat.FlagChromeNone | HudFormat.FlagNoScrollbar | HudFormat.FlagHasError | HudFormat.FlagPosIsDefault;
            Check(pa.Flags == want, $"flags 0x{pa.Flags:X}, expected 0x{want:X}");
            Check(pa.Title == "Tïtle" && pa.Key == "RynthLua/t/A" && pa.Hash == a.Hash, "key / title / hash");
            Check(pa.DefaultSize == new System.Numerics.Vector2(400, 300) && pa.MinSize == new System.Numerics.Vector2(100, 50), "DefaultSize / MinSize");
            Check(pa.RequestSize == new System.Numerics.Vector2(300, 200) && pa.RequestPos == new System.Numerics.Vector2(10, 20) && pa.PosIsDefault, "requests");
            Check(pa.ErrorText != null && Encoding.UTF8.GetByteCount(pa.ErrorText) <= 1024 && pa.ErrorText.All(c => c == 'é'), "ErrorText cut to 1024 bytes on a character boundary");
            Check(pa.List != null && pa.ListSeq == a.ListSeq, "body sent");
            Check(pb.List == null && pb.Flags == HudFormat.FlagShowInBar && pb.ErrorText == null, "unchanged body, default flags, no error");
            Check(float.IsNaN(pb.RequestSize.X) && float.IsNaN(pb.RequestPos.X), "no requests");

            // Header change detection.
            HudHeader snap = a.Header();
            Check(snap.SameAs(a.Header()), "the same header compares equal (NaN included)");
            Check(b.Header().SameAs(b.Header()), "NaN requests compare equal");
            a.LastError = null;
            Check(!snap.SameAs(a.Header()), "a cleared error is a change");
        }

        private static void Mismatches()
        {
            ScriptHud h = NewHud("M");
            var r = new HudRecorder();
            r.Begin(h, 1500, 65536);
            Throws(() => r.PopID(), "PopID() without a PushID()");
            Throws(() => r.EndChild(), "EndChild() without a matching BeginChild()");
            Throws(() => r.TreePop(), "TreePop() without an open TreeNode");
            r.PushStyleColor(0, 1);
            Throws(() => r.PopStyleColor(2), "pops more than were pushed");
            r.PopStyleColor(1);
            r.PushID("outer");
            r.BeginChild(r.KeyOf("c"), "c", 0, 0, false, 0);
            Throws(() => r.PopID(), "PopID() without a PushID() in this block");
            Throws(() => r.TreePop(), "TreePop() inside an open BeginChild()");
            r.TreeNode(r.KeyOf("t"), "t", 0, true);
            Throws(() => r.EndChild(), "EndChild() inside an open TreeNode");
            r.TreePop();
            r.EndChild();
            r.PopID();
            r.End();
            Check(!h.ScopeWarned, "balanced after the errors");
            Commit(h);
            ParseOne(h);
            Throws(() => r.Text(HudFormat.OpText, "late"), "only work inside a hud's OnRender");
        }

        private static void Depth()
        {
            ScriptHud h = NewHud("D");
            var r = new HudRecorder();
            r.Begin(h, 1500, 65536);
            for (int i = 0; i < 16; i++) r.BeginChild(r.KeyOf("c" + i), "c" + i, 0, 0, false, 0);
            for (int i = 0; i < 16; i++) r.PushID(i);
            Check(r.Depth == 32, "depth 32");
            Throws(() => r.PushID(99), "nested deeper than 32");
            Throws(() => r.TreeNode(r.KeyOf("t"), "t", 0, false), "nested deeper than 32");   // even closed: the engine counts its block
            Throws(() => r.BeginChild(r.KeyOf("x"), "x", 0, 0, false, 0), "nested deeper than 32");
            r.Text(HudFormat.OpText, "deep");
            for (int i = 0; i < 16; i++) r.PopID();
            for (int i = 0; i < 16; i++) r.EndChild();
            r.End();
            Commit(h);
            DisplayList l = ParseOne(h);
            Check(l.OpCount == 16 + 16 + 1 + 16, $"ops {l.OpCount}");
        }

        private static void AutoClose()
        {
            ScriptHud h = NewHud("AC");
            var r = new HudRecorder();
            r.Begin(h, 1500, 65536);
            r.BeginChild(r.KeyOf("c"), "c", 0, 0, false, 0);
            r.PushID("p");
            r.TreeNode(r.KeyOf("t"), "t", 0, true);
            r.PushStyleColor(21, 5);
            r.Text(HudFormat.OpText, "inside");
            r.End();
            Check(h.ScopeWarned, "warned");
            Commit(h);
            DisplayList l = ParseOne(h);
            Check(l.OpCount == 5 && l.Ops[0].EndIndex == 5 && l.Ops[2].EndIndex == 5, "blocks closed at the end");

            // Second pass: no second warning (flag stays), still parses.
            r.Begin(h, 1500, 65536);
            r.BeginChild(r.KeyOf("c"), "c", 0, 0, false, 0);
            r.End();
            Commit(h);
            ParseOne(h);
        }

        private static void Limits()
        {
            ScriptHud h = NewHud("L");
            var r = new HudRecorder();
            r.Begin(h, 10, 65536);
            for (int i = 0; i < 10; i++) r.Separator();
            Throws(() => r.Separator(), "drew more than 10 items");
            r.Abort();

            r.Begin(h, 1500, 65536);
            var items = Enumerable.Range(0, 600).Select(i => "i" + i).ToArray();
            r.Combo(r.KeyOf("big"), "big", 700, items);
            r.Dummy(float.NaN, float.PositiveInfinity);
            r.Text(HudFormat.OpText, new string('é', 3000));                  // 6000 bytes: cut to 4096
            r.InputText(r.KeyOf("in"), "in", "abc", 100_000, 0);              // maxLen clamped to 4096
            r.End();
            Commit(h);
            DisplayList l = ParseOne(h);
            Check(l.Ops[0].Aux == 512 && l.Ops[0].I0 == 511, "combo: 512 items, index clamped by the engine");
            Check(l.Ops[1].A == 0 && l.Ops[1].B == 0, "non-finite floats written as 0");
            Check(l.Ops[2].StrLen == 4096, $"string cut to 4096 bytes ({l.Ops[2].StrLen})");
            Check(l.Ops[3].Aux == 4096, "InputText maxLen clamped");

            r.Begin(h, 1500, 65536);
            var huge = Enumerable.Range(0, 20).Select(_ => new string('x', 4000)).ToArray();
            Throws(() => r.Combo(r.KeyOf("huge"), "huge", 0, huge), "one item is over");
            r.Abort();

            r.Begin(h, 1500, 16 * 1024);
            Throws(() => { for (int i = 0; i < 10; i++) r.Text(HudFormat.OpText, new string('x', 4000)); }, "drew more than 16 KB");
            r.Abort();

            // A block's BlockLength (written after its op) counts too: however close to the limit
            // a Child or a closed TreeNode lands, the list stays within it or the pass fails.
            bool within = true;
            int refused = 0;
            for (int pad = 3960; pad <= 4090; pad++)
            {
                foreach (int kind in new[] { 0, 1 })
                {
                    r.Begin(h, 1500, 4096);
                    try
                    {
                        r.Text(HudFormat.OpText, new string('x', pad));
                        if (kind == 0) { r.BeginChild(r.KeyOf("c"), "c", 0, 0, false, 0); r.EndChild(); }
                        else r.TreeNode(r.KeyOf("t"), "t", 0, false);
                        r.End();
                        within &= h.ScratchLength <= 4096;
                    }
                    catch (ScriptRuntimeException) { r.Abort(); refused++; }
                }
            }
            Check(within, "a block near the byte limit never makes a list over it");
            Check(refused > 0, "a block that doesn't fit fails the pass");

            // Huge strings: only what can be sent is encoded, the result is the same as cutting the
            // whole encoding, and the key buffer doesn't grow to the string's size.
            string giant = new string('g', 20_000_000);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            r.Begin(h, 1500, 65536);
            for (int i = 0; i < 15; i++) r.Text(HudFormat.OpText, giant);
            uint kg = 0;
            for (int i = 0; i < 200; i++) kg = r.KeyOf(giant);
            r.Abort();
            sw.Stop();
            Check(sw.ElapsedMilliseconds < 2000, $"15 texts and 200 keys of a 20M-character string are cheap ({sw.ElapsedMilliseconds} ms)");
            Check(RecorderTmp(r).Length <= 16 * 1024, $"the key buffer stays small ({RecorderTmp(r).Length} bytes)");
            Check(kg == HudFormat.Fnv1a(Encoding.UTF8.GetBytes(new string('g', 4096)), h.Hash), "a huge label's key is its first 4096 bytes'");
            string[] edges =
            {
                new string('a', 4095) + "\U0001F600" + "tail",   // a surrogate pair across the 4096-byte cut
                new string('a', 4094) + "é" + "zz",              // a 2-byte character ending exactly at 4096
                new string('a', 4095) + "é",                     // ... straddling it
                new string('é', 3000),                           // all 2-byte
                "\U0001F600" + new string('b', 5000),
                new string('a', 4096) + "\uD800",                // a lone surrogate past the cut
            };
            foreach (string e in edges)
            {
                r.Begin(h, 1500, 65536);
                r.Text(HudFormat.OpText, e);
                r.End();
                Commit(h);
                DisplayList el = ParseOne(h);
                byte[] want = Encoding.UTF8.GetBytes(e);
                want = want[..HudFormat.CutUtf8(want, 4096)];
                Check(el.Pool.AsSpan(el.Ops[0].Str, el.Ops[0].StrLen).SequenceEqual(want), $"edge string of {e.Length} chars cut as before");
            }

            // A giant title and error text go out cut to their limits.
            ScriptHud t = NewHud("T");
            r.Begin(t, 1500, 65536);
            r.Text(HudFormat.OpText, "x");
            r.End();
            Commit(t);
            t.Title = new string('é', 5_000_000);
            t.LastError = new string('!', 5_000_000);
            int rcT = Parse(new[] { t }, out var tw, out string terr, out _);
            Check(rcT == 0 && tw[0].Title.Length == 64 && tw[0].ErrorText!.Length == 1024, $"title cut to 128 bytes, error to 1 KB ({rcT}: {terr})");
        }

        /// <summary>
        /// Six huds with ~60 KB lists all changed in one tick (360 KB) don't fit the engine's
        /// 256 KB per-submit limit: the bodies that fit go, the rest are sent "unchanged" and go
        /// next time, and every submit parses. Before, the whole submit was refused, every time.
        /// </summary>
        private static unsafe void SubmitBudget()
        {
            var huds = new ScriptHud[6];
            var r = new HudRecorder();
            for (int i = 0; i < huds.Length; i++)
            {
                huds[i] = NewHud("Big" + i);
                r.Begin(huds[i], 1500, 65536);
                for (int k = 0; k < 15; k++) r.Text(HudFormat.OpText, new string((char)('a' + i), 4000));
                r.End();
                Commit(huds[i]);
            }
            Check(huds.Sum(h => h.BodyLength) > HudFormat.DefaultMaxSubmitBytes, "the bodies together are over the limit");

            var sentBy = new int[huds.Length];
            byte[] buf = new byte[16];
            for (int round = 0; round < 4 && huds.Any(h => h.BodyDirty); round++)
            {
                int n = HudFormat.WriteSubmit(ref buf, huds, huds.Length, 5, HudFormat.DefaultMaxSubmitBytes, round);
                Check(n <= HudFormat.DefaultMaxSubmitBytes, $"round {round}: submit of {n} bytes within the limit");
                int rc;
                List<ParsedWindow> windows;
                string error;
                fixed (byte* p = buf) rc = DisplayListParser.Parse(p, n, 4000f, out _, out windows, out error);
                Check(rc == 0, $"round {round}: the engine accepts it ({rc}: {error})");
                Check(windows.Count == huds.Length, "every window is in every submit");
                for (int i = 0; i < huds.Length; i++)
                {
                    Check((windows.Count > i && windows[i].List != null) == huds[i].BodyInSubmit, $"round {round}: hud {i} body sent as marked");
                    if (huds[i].BodyInSubmit) { sentBy[i]++; huds[i].BodyDirty = false; }   // what SubmitHuds does on rc 0
                }
                Check(round > 0 || huds.Count(h => h.BodyInSubmit) is > 0 and < 6, "the first submit carries some bodies, not all");
            }
            Check(huds.All(h => !h.BodyDirty) && sentBy.All(c => c == 1), "every body went exactly once within a few submits");

            // A body that doesn't change never goes again; nothing dirty means no bodies at all.
            int m = HudFormat.WriteSubmit(ref buf, huds, huds.Length, 5);
            Check(huds.All(h => !h.BodyInSubmit) && m < 16 * 1024, "no dirty bodies: headers only");
        }

        /// <summary>
        /// Hostile input for the engine's parser: every truncation of a real submit, single-byte
        /// corruptions of it, and random bytes behind a valid header. Parse must never throw, and
        /// whatever it accepts must be safe to replay: block ends inside the list and nested at
        /// most 32 deep, every string inside the pool and NUL-terminated, every float finite.
        /// </summary>
        private static unsafe void ParserFuzz()
        {
            // A real two-window submit with every op, blocks and nesting.
            ScriptHud a = NewHud("FuzzA"), b = NewHud("FuzzB");
            var r = new HudRecorder();
            r.Begin(a, 1500, 65536);
            r.Text(HudFormat.OpText, "hello %s %n");
            r.LabelText("l", "50%");
            uint k = r.KeyOf("c"); r.BeginChild(k, "c", 10, 10, true, 8);
            r.PushID("p"); r.PushStyleColor(21, 0xFF00FF00);
            r.TreeNode(r.KeyOf("t"), "t", 32, true);
            r.SliderInt(r.KeyOf("si"), "si", 5, 10, 0, "%d%%");
            r.SliderFloat(r.KeyOf("sf"), "sf", 0.5f, 0, 1, "%.2f");
            r.InputText(r.KeyOf("it"), "it", "text", 16, 64);
            r.Combo(r.KeyOf("co"), "co", 1, new[] { "a", "b" });
            r.TreePop();
            r.PopStyleColor(1); r.PopID();
            r.EndChild();
            r.CollapsingHeader(r.KeyOf("h"), "h", 0, true);
            r.Selectable(r.KeyOf("se"), "se", true, 4, 0, 0);
            r.ProgressBar(0.5f, -1, 0, "p");
            r.Image(HudFormat.IconKindObject, 0x80001234, 24, 24, Vector2.Zero, Vector2.One, 0xFFFFFFFF, 0xFF00FF00);
            r.ImageButton(r.KeyOf("ib"), "ib", HudFormat.IconKindSpell, 42, 16, 16, new Vector2(0.25f, 0), Vector2.One, 0, 0xFFFFFFFF);
            r.InputInt(r.KeyOf("ii"), "ii", 5, 1, 100, 64);
            r.InputFloat(r.KeyOf("if"), "if", 1.5f, 0.5f, 5, "%.2f", 64);
            r.DragInt(r.KeyOf("di"), "di", 5, 0.5f, 0, 10, "%d", 1536);
            r.DragFloat(r.KeyOf("df"), "df", 0.5f, 0.01f, 0, 1, "%.3f", 32);
            r.End();
            Commit(a);
            r.Begin(b, 1500, 65536);
            for (int d = 0; d < 20; d++) r.TreeNode(r.KeyOf("n" + d), "n" + d, 0, true);
            r.Button(r.KeyOf("deep"), "deep", 0, 0, false);
            r.End();
            Commit(b);
            a.LastError = "err";
            byte[] good = new byte[16];
            int len = HudFormat.WriteSubmit(ref good, new[] { a, b }, 2, 1);
            good = good[..len];

            int accepted = 0, refused = 0, bad = 0;
            void Try(byte[] data)
            {
                int rc;
                List<ParsedWindow> ws;
                try
                {
                    fixed (byte* p = data) rc = DisplayListParser.Parse(data.Length == 0 ? null : p, data.Length, 4000f, out _, out ws, out _);
                }
                catch (Exception ex) { bad++; if (bad <= 3) Check(false, $"Parse threw {ex.GetType().Name}: {ex.Message}"); return; }
                if (rc != 0) { refused++; return; }
                accepted++;
                foreach (ParsedWindow w in ws)
                {
                    if (w.List == null) continue;
                    string why = ReplaySafe(w.List);
                    if (why.Length > 0) { bad++; if (bad <= 3) Check(false, $"accepted list unsafe: {why}"); }
                }
            }

            Try(good);
            Check(accepted == 1, "the real submit parses");
            for (int n = 0; n < good.Length; n++) Try(good[..n]);                          // every truncation
            var rng = new Random(1234);
            for (int i = 0; i < 20000; i++)
            {
                byte[] m = (byte[])good.Clone();
                int flips = 1 + rng.Next(3);
                for (int f = 0; f < flips; f++) m[rng.Next(16, m.Length)] = (byte)rng.Next(256);   // keep the header
                Try(m);
            }
            for (int i = 0; i < 5000; i++)
            {
                byte[] m = new byte[16 + rng.Next(600)];
                rng.NextBytes(m);
                good.AsSpan(0, 16).CopyTo(m);
                BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(6), (ushort)(1 + rng.Next(3)));
                BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(12), (uint)m.Length);
                Try(m);
            }
            Console.WriteLine($"   {accepted + refused} submits: {accepted} accepted, {refused} refused");
            Check(bad == 0, $"no throw and nothing unsafe accepted ({accepted} accepted, {refused} refused, {bad} bad)");
            Check(accepted > 1 && refused > 1000, $"the fuzz reached both outcomes ({accepted} / {refused})");
        }

        /// <summary>Empty when <paramref name="l"/> is safe for the replay; otherwise what isn't.</summary>
        private static string ReplaySafe(DisplayList l)
        {
            if (l.OpCount != l.Ops.Length && l.OpCount > l.Ops.Length) return "OpCount past the array";
            if (l.Pool.Length == 0) return "empty pool";
            bool StrOk(int off, int length) => off < 0 || (off + length < l.Pool.Length && l.Pool[off + length] == 0);
            var ends = new Stack<int>();
            for (int i = 0; i < l.OpCount; i++)
            {
                while (ends.Count > 0 && i >= ends.Peek()) ends.Pop();
                ReplayOp o = l.Ops[i];
                if (o.EndIndex <= i || o.EndIndex > l.OpCount) return $"op {i} EndIndex {o.EndIndex}";
                if (ends.Count > 0 && o.EndIndex > ends.Peek()) return $"op {i} ends past its block";
                if (!StrOk(o.Str, o.StrLen) || !StrOk(o.Str2, o.Str2Len)) return $"op {i} string outside the pool";
                if (!float.IsFinite(o.A) || !float.IsFinite(o.B) || !float.IsFinite(o.C)
                    || !float.IsFinite(o.D) || !float.IsFinite(o.E) || !float.IsFinite(o.F)) return $"op {i} float";
                string level2 = ReplaySafeLevelTwo(l, o);
                if (level2.Length > 0) return $"op {i} ({o.Code}) {level2}";
                if (o.Code is ScriptOp.Child or ScriptOp.TreeNode)
                {
                    ends.Push(o.EndIndex);
                    if (ends.Count > DisplayListParser.MaxDepth) return $"op {i} nested {ends.Count} deep";
                }
                else if (o.EndIndex != i + 1) return $"op {i} ({o.Code}) has children";
                if (o.Code is ScriptOp.Text or ScriptOp.Button or ScriptOp.Checkbox or ScriptOp.TreeNode or ScriptOp.Child
                    && o.Str < 0) return $"op {i} ({o.Code}) without its string";
                if (o.Code is ScriptOp.SliderInt or ScriptOp.SliderFloat or ScriptOp.InputText or ScriptOp.Combo or ScriptOp.LabelText
                    && o.Str2 < 0) return $"op {i} ({o.Code}) without its second string";
                if (o.Code == ScriptOp.Combo && (o.Str2 + o.Str2Len + 1 > l.Pool.Length || l.Pool[o.Str2 + o.Str2Len] != 0))
                    return $"op {i} combo items not double-NUL terminated";
                if (o.Code == ScriptOp.InputText && (o.Str2Len > o.Aux || o.Aux < 1 || o.Aux > 4096)) return $"op {i} InputText value over maxLen";
                if (o.Code == ScriptOp.SliderInt && (o.I0 < o.I1 || o.I0 > o.I2)) return $"op {i} SliderInt out of range";
                if (o.Code == ScriptOp.Combo && (o.I0 < -1 || o.I0 >= Math.Max(1, (int)o.Aux) && o.Aux > 0)) return $"op {i} Combo index";
            }
            return string.Empty;
        }

        private static byte[] Ev(ushort type, uint seq, uint window, uint key, params byte[] payload)
        {
            byte[] e = new byte[16 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(e, type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)e.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), window);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), key);
            payload.CopyTo(e, 16);
            return e;
        }

        private static byte[] Le(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
        private static byte[] Lef(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); return b; }

        private static void Events()
        {
            var plugin = new RynthLuaPlugin();
            ScriptHud h = NewHud("E");
            HudsByHash(plugin)[h.Hash] = h;
            Huds(plugin).Add(h);
            h.Visible = true;
            byte[] text = Encoding.UTF8.GetBytes("typed");
            byte[] textPayload = new byte[3 + text.Length];
            textPayload[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(textPayload.AsSpan(1), (ushort)text.Length);
            text.CopyTo(textPayload, 3);
            byte[] geo = new byte[36];
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteSingleLittleEndian(geo.AsSpan(i * 4), i + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(geo.AsSpan(32), 5);
            byte[] text2 = Encoding.UTF8.GetBytes("typed2");
            byte[] text2Payload = new byte[3 + text2.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(text2Payload.AsSpan(1), (ushort)text2.Length);
            text2.CopyTo(text2Payload, 3);

            var all = new List<byte>();
            all.AddRange(Ev(HudFormat.EvClicked, 3, h.Hash, 11, 2, 0, 0));
            all.AddRange(Ev(HudFormat.EvBool, 4, h.Hash, 12, 1));
            all.AddRange(Ev(HudFormat.EvInt, 5, h.Hash, 13, Le(-7)));
            all.AddRange(Ev(HudFormat.EvFloat, 6, h.Hash, 14, Lef(2.5f)));
            all.AddRange(Ev(HudFormat.EvText, 7, h.Hash, 15, textPayload));
            all.AddRange(Ev(HudFormat.EvText, 8, h.Hash, 15, text2Payload));   // submitted stays set
            all.AddRange(Ev(HudFormat.EvOpen, 9, h.Hash, 16, 1));
            all.AddRange(Ev(HudFormat.EvGeometry, 10, h.Hash, 0, geo));
            all.AddRange(Ev(99, 11, h.Hash, 0, 1, 2, 3));                        // unknown type: skipped
            all.AddRange(Ev(HudFormat.EvInt, 12, 0xDEAD, 13, Le(1)));            // unknown window: ignored
            ApplyHudEvents(plugin, all.ToArray());

            Check(h.Clicks.TryGetValue(11, out int c) && c == 2, "Clicked count");
            Check(h.Bools.TryGetValue(12, out bool bv) && bv, "Bool");
            Check(h.Ints.TryGetValue(13, out int iv) && iv == -7, "Int");
            Check(h.Floats.TryGetValue(14, out float fv) && fv == 2.5f, "Float");
            Check(h.Texts.TryGetValue(15, out var tv) && tv.Text == "typed2" && tv.Submitted, "Text, submitted sticky");
            Check(h.OpenStates.TryGetValue(16, out bool ov) && ov, "Open");
            Check(h.Geometry.Known && h.Geometry.WindowPos.X == 1 && h.Geometry.CursorStart.Y == 8 && h.Geometry.Focused && h.Geometry.Hovered, "Geometry");
            Check(h.FirstUnappliedSeq == 3 && h.PassNow, "the first unapplied seq holds the AckSeq");

            Check(plugin.TakeInt(h, 13, out int ti) && ti == -7 && h.ConsumedInput, "TakeInt");
            Check(!plugin.TakeInt(h, 13, out _), "TakeInt only once");
            Check(plugin.TakeFloat(h, 14, out float tf) && tf == 2.5f, "TakeFloat");
            Check(plugin.TakeText(h, 15, out string tt, out bool ts) && tt == "typed2" && ts, "TakeText");
            Check(RynthLuaPlugin.GetOpenState(h, 16, false) && !RynthLuaPlugin.GetOpenState(h, 17, false) && RynthLuaPlugin.GetOpenState(h, 17, true), "GetOpenState");

            // The player closes it: Visible follows, transient input is dropped, open states stay.
            ApplyHudEvents(plugin, Ev(HudFormat.EvVisibility, 20, h.Hash, 0, 0, 1));
            Check(!h.Visible && h.Clicks.Count == 0 && h.Bools.Count == 0 && h.FirstUnappliedSeq == 0, "hidden: input dropped");
            Check(h.OpenStates.Count == 1, "open states survive a hide");
            h.FirstPassSinceShown = false;
            h.ErrorStreak = 20;
            ApplyHudEvents(plugin, Ev(HudFormat.EvVisibility, 21, h.Hash, 0, 1, 2));
            Check(h.Visible && h.FirstPassSinceShown && h.PassNow && h.ErrorStreak == 0, "shown from the bar: a pass is due, strikes reset");

            // The engine's queue isn't in seq order: a click coalesced into an earlier entry takes
            // a newer seq where it sits. The hold is the lowest unapplied seq, not the first seen.
            var outOfOrder = new List<byte>();
            outOfOrder.AddRange(Ev(HudFormat.EvClicked, 30, h.Hash, 11, 1, 0, 0));
            outOfOrder.AddRange(Ev(HudFormat.EvBool, 27, h.Hash, 12, 1));
            ApplyHudEvents(plugin, outOfOrder.ToArray());
            Check(h.FirstUnappliedSeq == 27, $"lowest unapplied seq held ({h.FirstUnappliedSeq})");
        }

        /// <summary>Phase 0's imgui functions through Lua, with the /lua hud bench code itself.</summary>
        private static void LuaBench()
        {
            var plugin = new RynthLuaPlugin();
            ScriptHud? hud = null;
            ScriptContext? ctx = null;
            var host = new LuaScriptHost(_ => { }, (s, h) => LibraryImGuiValues.Register(s));
            ctx = new ScriptContext("hud-bench", null, string.Empty, new ScriptManifest(), host);
            hud = new ScriptHud(ctx, "Bench", "RynthLua/hud-bench/Bench", ctx.Event("Hud:Bench:OnRender"), ctx.Event("Hud:Bench:OnPreRender"));
            HudsByHash(plugin)[hud.Hash] = hud;
            Huds(plugin).Add(hud);
            host.ResolveModule = (s, name) =>
            {
                if (name == "imgui") return CreateImGuiModule(plugin, s);
                if (name != "views") return null;
                // A stand-in views module: CreateHud returns a table whose OnRender is the real hud's event.
                var fake = new Table(s);
                fake["OnRender"] = hud.OnRender.CreateTable(s);
                var huds = new Table(s);
                huds["CreateHud"] = DynValue.NewCallback((c, a) => DynValue.NewTable(fake));
                var views = new Table(s);
                views["Huds"] = DynValue.NewTable(huds);
                return DynValue.NewTable(views);
            };
            host.Run(BenchCode(null, 9), "hud-bench");
            host.Tick(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
            Check(host.Loaded, "bench script loaded: " + host.ConsoleText());

            RunPass(plugin, hud);
            Check(hud.LastError == null, "pass ok: " + hud.LastError);
            DisplayList l = ParseOne(hud);
            Check(l.OpCount == 9, $"9 widgets ({l.OpCount})");
            Check(l.Ops[1].Code == ScriptOp.Checkbox && l.Ops[2].Code == ScriptOp.Text && l.Ops[3].Code == ScriptOp.Button, "Checkbox / Text / Button in turn");
            Check(l.Ops[1].Flag == 0, "Check 2 starts off");

            // Click "Button 4" twice and tick "Check 2": the next passes see them.
            uint button4 = Fnv("Button 4", hud.Hash), check2 = Fnv("Check 2", hud.Hash);
            var evs = new List<byte>();
            evs.AddRange(Ev(HudFormat.EvClicked, 1, hud.Hash, button4, 2, 0, 0));
            evs.AddRange(Ev(HudFormat.EvBool, 2, hud.Hash, check2, 1));
            ApplyHudEvents(plugin, evs.ToArray());
            hud.BodyDirty = false;
            RunPass(plugin, hud);
            l = ParseOne(hud);
            Check(l.Ops[1].Flag == 1, "Check 2 recorded on after the Bool event");
            Check(hud.Clicks.TryGetValue(button4, out int left) && left == 1 && hud.PassNow, "one click per pass; the second waits");
            RunPass(plugin, hud);   // takes the second click (the counter line was drawn before it)
            Check(hud.Clicks.Count == 0 && hud.FirstUnappliedSeq == 0, "results cleared");
            RunPass(plugin, hud);
            l = ParseOne(hud);
            Check(l.OpCount > 0 && S(l, l.Ops[0].Str, l.Ops[0].StrLen).StartsWith("clicks: 2"), "both clicks counted: " + S(l, l.Ops[0].Str, l.Ops[0].StrLen));

            // Errors: a failing pass keeps the last good list and carries the error in the header.
            host.Exec("local ImGui = require('imgui'); local views = require('views'); views.Huds.CreateHud('Bench').OnRender.Add(function() error('boom in render') end)");
            host.Tick(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
            uint seqBefore = hud.ListSeq;
            RunPass(plugin, hud);
            Check(hud.LastError != null && hud.LastError.Contains("boom in render") && hud.ListSeq == seqBefore, "error kept, last good list stays: " + hud.LastError);
            int rc = Parse(new[] { hud }, out var w, out string err, out _);
            Check(rc == 0 && w[0].ErrorText != null && (w[0].Flags & HudFormat.FlagHasError) != 0, "submit carries HasError + ErrorText " + err);

            // Outside a pass.
            host.Exec("local ImGui = require('imgui'); ok, msg = pcall(ImGui.Text, 'x')");
            host.Tick(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
            DynValue msg = host.Lua!.Globals.Get("msg");
            Check(msg.Type == DataType.String && msg.String.Contains("only work inside a hud's OnRender"), "ImGui outside a pass: " + msg.ToPrintString());
        }

        // ── Lua harness for the imgui module ────────────────────────────

        private sealed class LuaHud
        {
            public required RynthLuaPlugin Plugin;
            public required LuaScriptHost Host;
            public required ScriptHud Hud;
            public DynValue G(string name) => Host.Lua!.Globals.Get(name);
            public DynValue Eval(string expr) => Host.Lua!.DoString("return " + expr);
        }

        /// <summary>
        /// A plugin with one visible hud and a script running <paramref name="code"/>. The views
        /// module is a stand-in whose CreateHud returns a table carrying the real hud's OnRender /
        /// OnPreRender events; <paramref name="modules"/> resolves any other module.
        /// </summary>
        private static LuaHud LuaHudWith(string code, Func<Script, string, DynValue?>? modules = null, string name = "H")
        {
            var plugin = new RynthLuaPlugin();
            var host = new LuaScriptHost(_ => { }, (s, h) => LibraryImGuiValues.Register(s));
            var ctx = new ScriptContext("t", null, string.Empty, new ScriptManifest(), host);
            var hud = new ScriptHud(ctx, name, "RynthLua/t/" + name, ctx.Event("Hud:" + name + ":OnRender"), ctx.Event("Hud:" + name + ":OnPreRender"));
            hud.Visible = true;
            hud.FirstPassSinceShown = true;
            HudsByHash(plugin)[hud.Hash] = hud;
            Huds(plugin).Add(hud);
            host.ResolveModule = (s, m) =>
            {
                if (m == "imgui") return CreateImGuiModule(plugin, s);
                if (m != "views") return modules?.Invoke(s, m);
                var fake = new Table(s);
                fake["OnRender"] = hud.OnRender.CreateTable(s);
                fake["OnPreRender"] = hud.OnPreRender.CreateTable(s);
                var huds = new Table(s);
                huds["CreateHud"] = DynValue.NewCallback((c, a) => DynValue.NewTable(fake));
                var views = new Table(s);
                views["Huds"] = DynValue.NewTable(huds);
                return DynValue.NewTable(views);
            };
            host.Run(code, "t");
            host.Tick(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
            Check(host.Loaded, "script loaded: " + host.ConsoleText());
            return new LuaHud { Plugin = plugin, Host = host, Hud = hud };
        }

        private static DisplayList Pass(LuaHud t, string what)
        {
            RunPass(t.Plugin, t.Hud);
            Check(t.Hud.LastError == null, what + ": pass ok: " + t.Hud.LastError);
            t.Hud.BodyDirty = true;   // send the last good list even when this pass recorded the same bytes
            return ParseOne(t.Hud);
        }

        private static byte[] TextEv(uint seq, uint window, uint key, bool submitted, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            var p = new byte[3 + utf8.Length];
            p[0] = submitted ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), (ushort)utf8.Length);
            utf8.CopyTo(p, 3);
            return Ev(HudFormat.EvText, seq, window, key, p);
        }

        private static void CheckCodes(DisplayList l, ScriptOp[] codes, string what)
        {
            Check(l.OpCount == codes.Length, $"{what}: op count {l.OpCount}, expected {codes.Length}");
            for (int i = 0; i < Math.Min(l.OpCount, codes.Length); i++)
                Check(l.Ops[i].Code == codes[i], $"{what}: op {i}: {l.Ops[i].Code}, expected {codes[i]}");
        }

        /// <summary>Every §1.3 function through Lua: the ops it records, then its results after events.</summary>
        private static void LuaMvpFunctions()
        {
            LuaHud t = LuaHudWith("""
                local ImGui = require("imgui")
                local hud = require("views").Huds.CreateHud("H")
                state = { chk = false, si = 5, sf = 0.25, it = "abc", ite = "x", co = 1, sel = false }
                r = {}
                hud.OnRender.Add(function()
                  ImGui.Text("a", 1)
                  ImGui.TextColored(Vector4.new(1, 0, 0, 1), "red")
                  ImGui.TextWrapped("wrap")
                  ImGui.TextDisabled("dis")
                  ImGui.BulletText("bul")
                  ImGui.SeparatorText("sep")
                  ImGui.LabelText("lbl", "val")
                  ImGui.Separator()
                  ImGui.SameLine(10)
                  ImGui.NewLine()
                  ImGui.Spacing()
                  ImGui.Dummy(Vector2.new(5, 6))
                  ImGui.Indent()
                  ImGui.Unindent(8)
                  r.btn = ImGui.Button("OK", {100, 20})
                  r.small = ImGui:SmallButton("sm")
                  r.chkc, state.chk = ImGui.Checkbox("Check", state.chk)
                  r.sic, state.si = ImGui.SliderInt("SI", state.si, 0, 10, "%d u")
                  r.sfc, state.sf = ImGui.SliderFloat("SF", state.sf, 0, 1)
                  r.itc, state.it = ImGui.InputText("IT", state.it, 32)
                  r.itec, state.ite = ImGui.InputText("ITE", state.ite, 32, ImGuiInputTextFlags.EnterReturnsTrue)
                  r.coc, state.co = ImGui.Combo("CO", state.co, { "a", "b", "c" })
                  r.co2c, r.co2 = ImGui.Combo("CO2", 0, "x\0y\0\0")
                  r.selc, state.sel = ImGui.Selectable("SE", state.sel)
                  ImGui.ProgressBar(0.5)
                  ImGui.PushID(7)
                  ImGui.PushID("s")
                  ImGui.PopID()
                  ImGui.PopID()
                  ImGui.PushID(0x80000001)
                  ImGui.PopID()
                  ImGui.PushStyleColor(ImGuiCol.Button, 0xFF0000FF)
                  ImGui.PopStyleColor()
                  ImGui.SetItemTooltip("tip")
                  if ImGui.BeginChild("child", Vector2.new(0, 50), true) then ImGui.Text("in") end
                  ImGui.EndChild()
                  r.hdr = ImGui.CollapsingHeader("Header")
                  if r.hdr then ImGui.Text("under") end
                  r.tree = ImGui.TreeNode("Tree", ImGuiTreeNodeFlags.DefaultOpen)
                  if r.tree then ImGui.Text("leaf"); ImGui.TreePop() end
                  r.ws = ImGui.GetWindowSize()
                  r.wp = ImGui.GetWindowPos()
                  r.ca = ImGui.GetContentRegionAvail()
                  r.same = require("imgui").ImGui == ImGui
                end)
                """);
            ScriptHud h = t.Hud;
            DisplayList l = Pass(t, "first");
            CheckCodes(l, new[]
            {
                ScriptOp.Text, ScriptOp.TextColored, ScriptOp.TextWrapped, ScriptOp.TextDisabled, ScriptOp.BulletText,   // 0-4
                ScriptOp.SeparatorText, ScriptOp.LabelText, ScriptOp.Separator, ScriptOp.SameLine, ScriptOp.NewLine,      // 5-9
                ScriptOp.Spacing, ScriptOp.Dummy, ScriptOp.Indent, ScriptOp.Unindent, ScriptOp.Button, ScriptOp.Button,   // 10-15
                ScriptOp.Checkbox, ScriptOp.SliderInt, ScriptOp.SliderFloat, ScriptOp.InputText, ScriptOp.InputText,      // 16-20
                ScriptOp.Combo, ScriptOp.Combo, ScriptOp.Selectable, ScriptOp.ProgressBar, ScriptOp.PushID,               // 21-25
                ScriptOp.PushID, ScriptOp.PopID, ScriptOp.PopID, ScriptOp.PushID, ScriptOp.PopID,                         // 26-30
                ScriptOp.PushStyleColor, ScriptOp.PopStyleColor, ScriptOp.SetItemTooltip, ScriptOp.Child, ScriptOp.Text,  // 31-35
                ScriptOp.CollapsingHeader, ScriptOp.TreeNode, ScriptOp.Text,                                              // 36-38
            }, "first pass");
            ReplayOp[] o = l.Ops;
            Check(S(l, o[0].Str, o[0].StrLen) == "a1", "Text concatenates its arguments");
            Check(o[1].Color == 0xFF0000FF && S(l, o[1].Str, o[1].StrLen) == "red", "TextColored(Vector4, s)");
            Check(S(l, o[6].Str, o[6].StrLen) == "lbl" && S(l, o[6].Str2, o[6].Str2Len) == "val", "LabelText(label, s)");
            Check(o[8].A == 10 && o[8].B == -1, "SameLine(10): spacing defaults to -1");
            Check(o[11].A == 5 && o[11].B == 6 && o[12].A == 0 && o[13].A == 8, "Dummy / Indent() / Unindent(8)");
            Check(o[14].A == 100 && o[14].B == 20 && o[14].Flag == 0 && o[15].Flag == 1, "Button({100, 20}); ImGui:SmallButton");
            Check(o[16].Flag == 0, "Checkbox off");
            Check(o[17].I0 == 5 && o[17].I1 == 0 && o[17].I2 == 10 && S(l, o[17].Str2, o[17].Str2Len) == "%d u", "SliderInt");
            Check(o[18].A == 0.25f && o[18].B == 0 && o[18].C == 1, "SliderFloat");
            Check(S(l, o[19].Str2, o[19].Str2Len) == "abc" && o[19].Aux == 32 && o[19].Flags == 0, "InputText");
            Check(S(l, o[20].Str2, o[20].Str2Len) == "x" && o[20].Flags == 64, "InputText EnterReturnsTrue");
            Check(o[21].I0 == 1 && o[21].Aux == 3 && S(l, o[21].Str2, o[21].Str2Len) == "a\0b\0c\0", "Combo(table): 0-based index");
            Check(o[22].I0 == 0 && o[22].Aux == 2 && S(l, o[22].Str2, o[22].Str2Len) == "x\0y\0", "Combo(\"x\\0y\\0\\0\")");
            Check(o[23].Flag == 0 && o[23].Flags == 0 && o[23].A == 0, "Selectable defaults");
            Check(o[24].A == 0.5f && o[24].B == -1 && o[24].C == 0 && S(l, o[24].Str2, o[24].Str2Len) == "", "ProgressBar default size and overlay");
            Check(o[25].Flag == 1 && o[25].I0 == 7 && o[26].Flag == 0 && S(l, o[26].Str, o[26].StrLen) == "s", "PushID(int) / PushID(string)");
            Check(o[29].Flag == 1 && o[29].I0 == unchecked((int)0x80000001), "PushID of an id above 0x7FFFFFFF wraps to the int form");
            Check(o[31].Aux == 21 && o[31].Color == 0xFF0000FF && o[32].Aux == 1, "PushStyleColor / PopStyleColor()");
            Check(S(l, o[33].Str, o[33].StrLen) == "tip", "SetItemTooltip");
            Check(o[34].Flag == 1 && o[34].B == 50 && o[34].EndIndex == 36, "BeginChild(id, size, true) with a child");
            Check(o[36].Flag == 0 && o[36].Flags == 0, "CollapsingHeader recorded closed");
            Check(o[37].Flag == 1 && o[37].Flags == 32 && o[37].EndIndex == 39, "TreeNode DefaultOpen recorded open");
            DynValue r = t.G("r");
            Check(r.Table.Get("btn").Type == DataType.Boolean && !r.Table.Get("btn").Boolean, "Button returns false");
            Check(r.Table.Get("hdr").Type == DataType.Boolean && !r.Table.Get("hdr").Boolean && r.Table.Get("tree").Boolean, "header closed, tree open");
            Check(r.Table.Get("co2").Number == 0 && !r.Table.Get("co2c").Boolean, "Combo returns changed, index");
            Check(r.Table.Get("same").Boolean, "require('imgui').ImGui is the module");
            Check(t.Eval("r.ws.X == 0 and r.ca.Y == 0").Boolean, "geometry is (0, 0) before any Geometry event");

            // Events: every value widget changes, the header opens, the tree closes.
            uint K(string label) => Fnv(label, h.Hash);
            var evs = new List<byte>();
            evs.AddRange(Ev(HudFormat.EvClicked, 1, h.Hash, K("OK"), 1, 0, 0));
            evs.AddRange(Ev(HudFormat.EvClicked, 2, h.Hash, K("SE"), 1, 0, 0));
            evs.AddRange(Ev(HudFormat.EvBool, 3, h.Hash, K("Check"), 1));
            evs.AddRange(Ev(HudFormat.EvInt, 4, h.Hash, K("SI"), Le(9)));
            evs.AddRange(Ev(HudFormat.EvFloat, 5, h.Hash, K("SF"), Lef(0.75f)));
            evs.AddRange(TextEv(6, h.Hash, K("IT"), false, "hello"));
            evs.AddRange(TextEv(7, h.Hash, K("ITE"), false, "typed"));
            evs.AddRange(Ev(HudFormat.EvInt, 8, h.Hash, K("CO"), Le(2)));
            evs.AddRange(Ev(HudFormat.EvOpen, 9, h.Hash, K("Header"), 1));
            evs.AddRange(Ev(HudFormat.EvOpen, 10, h.Hash, K("Tree"), 0));
            var geo = new byte[36];
            BinaryPrimitives.WriteSingleLittleEndian(geo.AsSpan(8), 320);
            BinaryPrimitives.WriteSingleLittleEndian(geo.AsSpan(12), 240);
            BinaryPrimitives.WriteSingleLittleEndian(geo.AsSpan(0), 15);
            BinaryPrimitives.WriteSingleLittleEndian(geo.AsSpan(16), 300);
            evs.AddRange(Ev(HudFormat.EvGeometry, 11, h.Hash, 0, geo));
            ApplyHudEvents(t.Plugin, evs.ToArray());
            l = Pass(t, "after events");
            o = l.Ops;
            r = t.G("r");
            DynValue st = t.G("state");
            Check(r.Table.Get("btn").Boolean && !r.Table.Get("small").Boolean, "Button clicked once");
            Check(r.Table.Get("chkc").Boolean && st.Table.Get("chk").Boolean && o[16].Flag == 1, "Checkbox: changed, new value recorded");
            Check(r.Table.Get("sic").Boolean && st.Table.Get("si").Number == 9 && o[17].I0 == 9, "SliderInt: changed, 9 recorded");
            Check(r.Table.Get("sfc").Boolean && st.Table.Get("sf").Number == 0.75 && o[18].A == 0.75f, "SliderFloat: changed, 0.75 recorded");
            Check(r.Table.Get("itc").Boolean && st.Table.Get("it").String == "hello" && S(l, o[19].Str2, o[19].Str2Len) == "hello", "InputText: changed on an edit");
            Check(!r.Table.Get("itec").Boolean && st.Table.Get("ite").String == "typed" && S(l, o[20].Str2, o[20].Str2Len) == "typed",
                "InputText EnterReturnsTrue: the text follows the edit, changed waits for Enter");
            Check(r.Table.Get("coc").Boolean && st.Table.Get("co").Number == 2 && o[21].I0 == 2, "Combo: changed, index 2 recorded");
            Check(r.Table.Get("selc").Boolean && st.Table.Get("sel").Boolean && o[23].Flag == 0,
                "Selectable: clicked, selected toggled (the list keeps what the script passed)");
            Check(r.Table.Get("hdr").Boolean && o[36].Flag == 1 && o[37].Code == ScriptOp.Text && S(l, o[37].Str, o[37].StrLen) == "under",
                "CollapsingHeader opened: recorded open, contents follow it");
            Check(!r.Table.Get("tree").Boolean && o[38].Code == ScriptOp.TreeNode && o[38].Flag == 0 && o[38].EndIndex == 39,
                "TreeNode closed by the player: an empty block, no TreePop needed");
            Check(t.Eval("r.ws.X == 320 and r.ws.Y == 240 and r.wp.X == 15 and r.ca.X == 300").Boolean, "geometry queries return the last Geometry event");
            Check(h.FirstUnappliedSeq == 0 && h.Ints.Count == 0 && h.Texts.Count == 0, "results taken");

            // Enter in the EnterReturnsTrue box.
            ApplyHudEvents(t.Plugin, TextEv(12, h.Hash, K("ITE"), true, "typed!"));
            Pass(t, "after Enter");
            Check(t.Eval("r.itec == true and state.ite == 'typed!'").Boolean, "InputText EnterReturnsTrue: changed on Enter");
            Pass(t, "quiet");
            Check(t.Eval("r.itec == false and r.btn == false and r.selc == false and r.sic == false").Boolean, "no event: nothing changed");
        }

        private static void LuaWindowCalls()
        {
            LuaHud t = LuaHudWith("""
                local ImGui = require("imgui")
                local hud = require("views").Huds.CreateHud("H")
                cfg = {}
                hud.OnPreRender.Add(function()
                  if cfg.size then ImGui.SetNextWindowSize(cfg.size, cfg.cond) end
                  if cfg.pos then ImGui.SetNextWindowPos(cfg.pos, cfg.cond, Vector2.new(0.5, 0.5)) end
                  if cfg.min then ImGui.SetNextWindowSizeConstraints(cfg.min, Vector2.new(9999, 9999)) end
                end)
                hud.OnRender.Add(function() ImGui.Text("x") end)
                """);
            ScriptHud h = t.Hud;
            bool None(Vector2 v) => HudFormat.IsNone(v);
            void Cfg(string lua) => t.Host.Lua!.DoString("cfg = " + lua);

            // FirstUseEver: DefaultSize (persistent) and a default position.
            Cfg("{ size = Vector2.new(300, 200), pos = {10, 20}, min = {100, 50}, cond = ImGuiCond.FirstUseEver }");
            h.PropsDirty = false;
            Pass(t, "FirstUseEver");
            Check(h.DefaultSize == new Vector2(300, 200) && None(h.RequestSize), "FirstUseEver size -> DefaultSize, no request");
            Check(h.RequestPos == new Vector2(10, 20) && h.PosIsDefault, "FirstUseEver pos -> RequestPos + PosIsDefault");
            Check(h.MinSize == new Vector2(100, 50), "SetNextWindowSizeConstraints -> MinSize");
            Check(h.PropsDirty, "a persistent field changed: PropsDirty");
            Parse(new[] { h }, out var w, out string err, out _);
            Check(w.Count == 1 && w[0].DefaultSize == new Vector2(300, 200) && w[0].MinSize == new Vector2(100, 50)
                  && w[0].PosIsDefault && w[0].RequestPos == new Vector2(10, 20) && float.IsNaN(w[0].RequestSize.X), "header on the wire " + err);
            h.PropsDirty = false;
            Pass(t, "FirstUseEver again");
            Check(!h.PropsDirty, "same persistent values: not dirty again");

            // Always / None: a request every pass.
            Cfg("{ size = Vector2.new(250, 150), pos = Vector2.new(40, 50), cond = ImGuiCond.Always }");
            Pass(t, "Always");
            Check(h.RequestSize == new Vector2(250, 150) && h.RequestPos == new Vector2(40, 50) && !h.PosIsDefault, "Always -> requests");
            Pass(t, "Always again");
            Check(h.RequestSize == new Vector2(250, 150), "Always repeats every pass");
            Cfg("{ size = Vector2.new(1, 2) }");
            Pass(t, "None");
            Check(h.RequestSize == new Vector2(1, 2) && None(h.RequestPos), "no cond = None = every pass; no pos call = no request");
            Cfg("{}");
            Pass(t, "no calls");
            Check(None(h.RequestSize) && None(h.RequestPos) && !h.PosIsDefault, "requests reset each pass");

            // Appearing: only on the first pass since shown.
            Cfg("{ size = Vector2.new(9, 9), pos = Vector2.new(8, 8), cond = ImGuiCond.Appearing }");
            Pass(t, "Appearing, not just shown");
            Check(None(h.RequestSize) && None(h.RequestPos), "Appearing: nothing when not just shown");
            h.FirstPassSinceShown = true;
            Pass(t, "Appearing, just shown");
            Check(h.RequestSize == new Vector2(9, 9) && h.RequestPos == new Vector2(8, 8), "Appearing: the first pass since shown");
            Check(!h.FirstPassSinceShown, "FirstPassSinceShown cleared by the good pass");
            Pass(t, "Appearing, next");
            Check(None(h.RequestSize), "Appearing: once per showing");

            // Once: only before the first good pass.
            Cfg("{ size = Vector2.new(7, 7), cond = ImGuiCond.Once }");
            Pass(t, "Once after passes");
            Check(None(h.RequestSize), "Once: nothing after the first good pass");
            h.EverPassed = false;
            Pass(t, "Once, first");
            Check(h.RequestSize == new Vector2(7, 7), "Once: the first good pass");

            // Outside a pass, and a bad size.
            t.Host.Lua!.DoString("ok1, msg1 = pcall(require('imgui').SetNextWindowSize, Vector2.new(1, 1))");
            Check(t.G("msg1").String.Contains("only work inside a hud's OnRender"), "SetNextWindowSize outside a pass: " + t.G("msg1").ToPrintString());
        }

        private static void LuaErrors()
        {
            LuaHud t = LuaHudWith("""
                local ImGui = require("imgui")
                local hud = require("views").Huds.CreateHud("H")
                errs = {}
                local function e(f, ...)
                  local ok, m = pcall(f, ...)
                  errs[#errs + 1] = ok and "ok" or tostring(m)
                end
                hud.OnRender.Add(function()
                  errs = {}
                  e(function() return ImGui.BeginTable end)
                  e(function() return ImGui.PlotLines end)
                  e(ImGui.PushStyleColor, ImGuiCol.WindowBg, 0xFFFFFFFF)
                  e(ImGui.CollapsingHeader, "H", true)
                  e(ImGui.TreeNode, "T", ImGuiTreeNodeFlags.NoTreePushOnOpen)
                  e(ImGui.Button, "B", "big")
                  e(ImGui.SetNextWindowSize, nil)
                  e(ImGui.PushID, {})
                  e(ImGui.Combo, "C", 0, 5)
                  e(ImGui.PushStyleColor, "button", Vector4.new(1, 1, 1, 1))
                  e(ImGui.PopStyleColor)
                  e(ImGui.TextColored, "red", "x")
                  e(function() return ImGui.GetStyle().WindowPadding end)
                  e(ImGui.EndChild)
                  e(ImGui.TreePop)
                  e(ImGui.PopID)
                  ImGui.Text("still drawing")
                end)
                """);
            DisplayList l = Pass(t, "errors");
            string[] want =
            {
                "ImGui.BeginTable isn't supported by RynthLua windows yet",
                "ImGui.PlotLines isn't supported by RynthLua windows yet",
                "use ImGuiCol.Text, TextDisabled, ChildBg, Border, FrameBg, CheckMark, SliderGrab, Button, ButtonHovered, ButtonActive, Header, HeaderHovered, PlotHistogram",
                "CollapsingHeader(label, visible) isn't supported yet",
                "NoTreePushOnOpen flag isn't supported yet",
                "ImGui.Button: size must be a Vector2",
                "ImGui.SetNextWindowSize: size must be a Vector2",
                "ImGui.PushID needs a string or a number",
                "ImGui.Combo: items must be",
                "ok",
                "ok",
                "a colour is a Vector4",
                "GetStyle().WindowPadding isn't supported",
                "EndChild() without a matching BeginChild()",
                "TreePop() without an open TreeNode",
                "PopID() without a PushID()",
            };
            Table errs = t.G("errs").Table;
            for (int i = 0; i < want.Length; i++)
            {
                string got = errs.Get(i + 1).ToPrintString();
                Check(got.Contains(want[i]), $"error {i + 1}: '{got}' should contain '{want[i]}'");
            }
            Check(l.OpCount == 3 && l.Ops[0].Code == ScriptOp.PushStyleColor && l.Ops[1].Code == ScriptOp.PopStyleColor
                  && l.Ops[2].Code == ScriptOp.Text, "refused calls recorded nothing; the pass went on");

            // Outside a pass: recording functions and geometry queries refuse; metrics answer.
            t.Host.Lua!.DoString("""
                local ImGui = require("imgui")
                out = {}
                for _, name in ipairs({ "Text", "Button", "Checkbox", "SliderInt", "InputText", "Combo", "Selectable",
                                        "BeginChild", "CollapsingHeader", "TreeNode", "PushID", "PushStyleColor",
                                        "SetItemTooltip", "ProgressBar", "SetNextWindowPos", "SetNextWindowSizeConstraints",
                                        "GetWindowSize", "GetWindowPos", "GetContentRegionAvail" }) do
                  local ok, m = pcall(ImGui[name], "x")
                  out[#out + 1] = (not ok and tostring(m):find("only work inside a hud's OnRender", 1, true)) and "ok" or (name .. ": " .. tostring(m))
                end
                lh = ImGui.GetTextLineHeight()
                """);
            Table outT = t.G("out").Table;
            for (int i = 1; i <= outT.Length; i++)
                Check(outT.Get(i).String == "ok", "outside a pass: " + outT.Get(i).String);
            Check(t.G("lh").Number == 14, "GetTextLineHeight works outside a pass");
        }

        private static void LuaClipboard()
        {
            string clip = "";
            WinClipboard.SetOverride = text => { clip = text; return true; };
            WinClipboard.GetOverride = () => clip;
            try
            {
                LuaHud t = LuaHudWith("ImGui = require('imgui')");
                Check(t.Eval("ImGui.SetClipboardText('Damage Rating: 12')").CastToBool(), "SetClipboardText works outside a pass and returns true");
                Check(clip == "Damage Rating: 12", "the text reaches the clipboard: " + clip);
                Check(t.Eval("ImGui.GetClipboardText()").String == "Damage Rating: 12", "GetClipboardText reads it back");
                t.Eval("ImGui:SetClipboardText(0x8000ABCD)");
                Check(clip == "2147527629", "a number is copied as tostring shows it: " + clip);
                t.Eval("ImGui.SetClipboardText(nil)");
                Check(clip == "", "nil copies empty text");
                WinClipboard.SetOverride = _ => false;
                Check(!t.Eval("ImGui.SetClipboardText('x')").CastToBool(), "a busy clipboard returns false");
            }
            finally { WinClipboard.SetOverride = null; WinClipboard.GetOverride = null; }
        }

        private static unsafe void LuaMetrics()
        {
            LuaHud t = LuaHudWith("ImGui = require('imgui')");
            bool Lua(string expr) => t.Eval(expr).CastToBool();

            // Defaults before the engine reported anything: line 14, frame 20, advance 7, spacing (8, 4).
            Check(Lua("ImGui.GetTextLineHeight() == 14 and ImGui.GetFrameHeight() == 20"), "default line / frame height");
            Check(Lua("ImGui.GetTextLineHeightWithSpacing() == 18"), "line height + ItemSpacing.Y");
            Check(Lua("ImGui.GetStyle().ItemSpacing.X == 8 and ImGui.GetStyle().ItemSpacing.Y == 4 and ImGui.GetStyle().FramePadding.Y == 3"), "default style");
            Check(Lua("ImGui.CalcTextSize('abc') == Vector2.new(21, 14)"), "CalcTextSize('abc') = (21, 14): " + t.Eval("tostring(ImGui.CalcTextSize('abc'))").String);
            Check(Lua("ImGui.CalcTextSize('ab\\ncde') == Vector2.new(21, 28)"), "two lines: the longest, twice the height");
            Check(Lua("ImGui.CalcTextSize('Name##id', true) == Vector2.new(28, 14)"), "hideAfterDoubleHash cuts at ##");
            Check(Lua("ImGui.CalcTextSize('Name##id') == Vector2.new(56, 14)"), "without it, ## counts");
            Check(Lua("ImGui.CalcTextSize('') == Vector2.new(0, 14)"), "empty text: one line");
            Check(Lua("ImGui.CalcTextSize('a\\n') == Vector2.new(7, 14)"), "a trailing newline adds no line (as ImGui)");
            Check(Lua("ImGui:CalcTextSize('é😀', false, 100).X == 14"), "non-ASCII: the mean advance per character (a surrogate pair once)");

            // The engine's metrics at UI scale 2 come back in logical units.
            var info = new RynthCore.PluginSdk.UiInfoNative
            {
                UiScale = 2, TextLineHeight = 26, FrameHeight = 38,
                ItemSpacingX = 16, ItemSpacingY = 8, FramePaddingX = 8, FramePaddingY = 6,
            };
            for (int i = 0; i < 95; i++) info.AsciiAdvance[i] = 13;   // 6.5 logical
            info.AsciiAdvance['W' - ' '] = 20;
            CopyUiMetrics(t.Plugin, info);
            Check(Lua("ImGui.GetTextLineHeight() == 13 and ImGui.GetFrameHeight() == 19"), "metrics / UiScale");
            Check(Lua("ImGui.GetTextLineHeightWithSpacing() == 17 and ImGui.GetStyle().ItemSpacing.X == 8 and ImGui.GetStyle().FramePadding.X == 4"), "spacing / padding / UiScale");
            Check(Lua("ImGui.CalcTextSize('W a') == Vector2.new(23, 13)"), "exact ASCII advances: (20 + 13 + 13) px / 2 = 23: " + t.Eval("tostring(ImGui.CalcTextSize('W a'))").String);
            for (int i = 0; i < 95; i++) info.AsciiAdvance[i] = 6.5f;
            CopyUiMetrics(t.Plugin, info);
            Check(Lua("ImGui.CalcTextSize('abc') == Vector2.new(10, 13)"), "width rounded up in pixels as ImGui does (19.5 px -> 20 px -> 10)");
        }

        /// <summary>Stand-ins for the game API the window examples use (no AC here).</summary>
        private const string ExampleStubs = """
            game = {
              Character = { TotalExperience = 1000, Health = 40, MaxHealth = 80,
                            Inventory = function(cls)
                              return { { Id = 0x80001234, Name = "Salvaged Iron", Use = function() used = (used or 0) + 1 end, Give = function() end },
                                       { Id = 17, Name = "Salvaged Gold", Use = function() end, Give = function() end } }
                            end },
              RegisterCommand = function(name, fn) commands = commands or {}; commands[name] = fn end,
              Actions = { SetCombatMode = function(m) combatMode = m end },
              World = {},
            }
            ObjectClass = { Salvage = 1 }

            """;

        private static DynValue? StorageStub(Script s, string m)
        {
            if (m != "storage") return null;
            return s.DoString("return { Get = function(k, d) return d end, Set = function(k, v) saved = (saved or 0) + 1 end }");
        }

        /// <summary>
        /// The window scripts in the plugin's Examples folder, run against the real views and imgui
        /// modules (game and storage are stand-ins): each loads, its hud draws without an error, and
        /// the engine's parser accepts the list.
        /// </summary>
        private static void ExampleScripts()
        {
            foreach (var (file, hudName) in new[] { ("hud-demo.lua", "Tracker"), ("xp-tracker.lua", "XP Tracker"), ("hunt-settings.lua", "Hunt Settings"), ("salvage-list.lua", "Salvage"), ("item-icons.lua", "Item Icons") })
            {
                string code = System.IO.File.ReadAllText(RepoPath("Plugins", "RynthCore.Plugin.RynthLua", "Examples", file));
                RynthLuaPlugin p = NewViewsPlugin(TempFile(), "Srv|Alice");
                UiAvailableField(p) = true;
                UiInfoAtField(p) = long.MaxValue;
                var host = new LuaScriptHost(_ => { }, (s, h) => LibraryImGuiValues.Register(s));
                var ctx = new ScriptContext(System.IO.Path.GetFileNameWithoutExtension(file), null, string.Empty, new ScriptManifest(), host);
                host.ResolveModule = (s, m) => m == "views" ? CreateViewsModule(p, s, ctx) : m == "imgui" ? CreateImGuiModule(p, s) : StorageStub(s, m);
                host.Run(ExampleStubs + code, ctx.Name);
                void Tick() => host.Tick(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
                Tick();
                Check(host.Loaded, $"{file} loaded: " + host.ConsoleText());
                if (!host.Loaded) continue;
                ScriptHud? h = Huds(p).FirstOrDefault(x => x.Name == hudName);
                Check(h != null, $"{file} made its hud '{hudName}'");
                if (h == null) continue;
                if (!h.Visible && host.Lua!.Globals.Get("commands").Type == DataType.Table)
                {
                    host.Lua.DoString("for _, fn in pairs(commands) do fn('') end");   // /hunt shows it
                    Tick();
                }
                Check(h.Visible, $"{file}: the hud is shown");
                RunHudPasses(p);
                Tick();
                h.PassNow = true;
                RunHudPasses(p);
                Check(h.EverPassed && h.LastError == null && h.OpCount > 0, $"{file}: a pass drew {h.OpCount} items without an error: {h.LastError}");
                int rc = Parse(new[] { h }, out var w, out string err, out _);
                Check(rc == 0 && w[0].List != null, $"{file}: the engine parser accepts the list: {err}");
                Check(!host.ConsoleText().Contains("error", StringComparison.OrdinalIgnoreCase), $"{file}: no error in the console: {host.ConsoleText()}");

                if (file == "salvage-list.lua")
                {
                    byte[] le = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(le, unchecked((int)0x80001234));
                    uint give =Fnv("Give to selected", HudFormat.Fnv1a(le, Fnv("rows", h.Hash)));
                    ApplyHudEvents(p, Ev(HudFormat.EvClicked, 1, h.Hash, give, 1, 0, 0));
                    RunHudPasses(p);
                    Tick();
                    Check(host.ConsoleText().Contains("Select someone to give to first."), "salvage-list: Give with nothing selected says so: " + host.ConsoleText());
                }
            }
        }

        /// <summary>The three examples in design doc §1.4, verbatim, with stand-ins for game / storage.</summary>
        private static void LuaDocExamples()
        {
            string doc = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Docs", "RYNTHLUA_WINDOWS_DESIGN.md"));
            int from = doc.IndexOf("### 1.4 Examples", StringComparison.Ordinal);
            int to = doc.IndexOf("### 1.5", StringComparison.Ordinal);
            Check(from > 0 && to > from, "§1.4 found in the design doc");
            if (from < 0 || to < from) return;
            var examples = new List<string>();
            string section = doc[from..to];
            for (int at = section.IndexOf("```lua", StringComparison.Ordinal); at >= 0; at = section.IndexOf("```lua", at, StringComparison.Ordinal))
            {
                int start = section.IndexOf('\n', at) + 1;
                int end = section.IndexOf("```", start, StringComparison.Ordinal);
                examples.Add(section[start..end]);
                at = end + 3;
            }
            Check(examples.Count == 3, $"three examples ({examples.Count})");
            if (examples.Count != 3) return;

            const string Stubs = ExampleStubs;
            Func<Script, string, DynValue?> Storage = StorageStub;

            // 1. The tracker.
            LuaHud t = LuaHudWith(Stubs + examples[0], Storage, "XP Tracker");
            DisplayList l = Pass(t, "tracker");
            CheckCodes(l, new[] { ScriptOp.Text, ScriptOp.Text, ScriptOp.Text, ScriptOp.ProgressBar, ScriptOp.Button, ScriptOp.SetItemTooltip }, "tracker");
            Check(l.Ops[3].A == 0.5f && l.Ops[3].B == -1 && S(l, l.Ops[3].Str2, l.Ops[3].Str2Len) == "40 / 80", "tracker: ProgressBar(fraction, size, overlay)");
            Check(t.Hud.DefaultSize == new Vector2(240, 130), "tracker: SetNextWindowSize FirstUseEver -> DefaultSize");
            t.Host.Lua!.DoString("game.Character.TotalExperience = 5000");
            ApplyHudEvents(t.Plugin, Ev(HudFormat.EvClicked, 1, t.Hud.Hash, Fnv("Reset", t.Hud.Hash), 1, 0, 0));
            Pass(t, "tracker reset");   // the Reset button comes after the text lines
            l = Pass(t, "tracker after reset");
            Check(S(l, l.Ops[0].Str, l.Ops[0].StrLen).StartsWith("XP gained:  0"), "tracker: Reset restarted the count: " + S(l, l.Ops[0].Str, l.Ops[0].StrLen));

            // 2. The settings form.
            t = LuaHudWith(Stubs + examples[1], Storage, "Hunt Settings");
            l = Pass(t, "settings");
            CheckCodes(l, new[] { ScriptOp.Checkbox, ScriptOp.SliderInt, ScriptOp.Combo, ScriptOp.InputText, ScriptOp.Separator, ScriptOp.Button }, "settings");
            Check(l.Ops[0].Flag == 1 && l.Ops[1].I0 == 30 && l.Ops[1].I1 == 5 && l.Ops[1].I2 == 60 && l.Ops[2].I0 == 0 && l.Ops[2].Aux == 3 && l.Ops[3].Aux == 128,
                "settings: values from storage's defaults");
            uint hh = t.Hud.Hash;
            var evs = new List<byte>();
            evs.AddRange(Ev(HudFormat.EvInt, 1, hh, Fnv("Mode", hh), Le(2)));
            evs.AddRange(Ev(HudFormat.EvInt, 2, hh, Fnv("Range (m)", hh), Le(45)));
            evs.AddRange(TextEv(3, hh, Fnv("Note", hh), false, "hi"));
            evs.AddRange(Ev(HudFormat.EvClicked, 4, hh, Fnv("Apply combat mode", hh), 1, 0, 0));
            ApplyHudEvents(t.Plugin, evs.ToArray());
            l = Pass(t, "settings changed");
            Check(l.Ops[2].I0 == 2 && l.Ops[1].I0 == 45 && S(l, l.Ops[3].Str2, l.Ops[3].Str2Len) == "hi", "settings: new values recorded");
            Check(t.G("saved").Number == 3, "settings: save() on each change (" + t.G("saved").ToPrintString() + ")");
            Check(t.G("combatMode").String == "Magic", "settings: modes[s.mode + 1] with the 0-based index");

            // 3. The list with buttons.
            t = LuaHudWith(Stubs + examples[2], Storage, "Salvage");
            t.Host.Tick(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);   // the spawned refresh runs
            l = Pass(t, "salvage");
            CheckCodes(l, new[]
            {
                ScriptOp.InputText, ScriptOp.Child,
                ScriptOp.PushID, ScriptOp.Text, ScriptOp.SameLine, ScriptOp.Button, ScriptOp.SameLine, ScriptOp.Button, ScriptOp.PopID,
                ScriptOp.PushID, ScriptOp.Text, ScriptOp.SameLine, ScriptOp.Button, ScriptOp.SameLine, ScriptOp.Button, ScriptOp.PopID,
            }, "salvage");
            Check(l.Ops[1].Flag == 1 && l.Ops[1].EndIndex == 16, "salvage: BeginChild(id, size, true) holds the rows");
            Check(l.Ops[2].Flag == 1 && l.Ops[2].I0 == unchecked((int)0x80001234) && l.Ops[4].A == 220, "salvage: PushID(object id), SameLine(220)");
            Span<byte> le = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(le, unchecked((int)0x80001234));
            uint rowUse = Fnv("Use", HudFormat.Fnv1a(le, Fnv("rows", t.Hud.Hash)));
            Check(l.Ops[5].Key == rowUse, "salvage: row button key = FNV(child, PushID(id), label)");
            ApplyHudEvents(t.Plugin, Ev(HudFormat.EvClicked, 1, t.Hud.Hash, rowUse, 1, 0, 0));
            Pass(t, "salvage click");
            Check(t.G("used").Number == 1, "salvage: the row's Use ran once");
            ApplyHudEvents(t.Plugin, TextEv(2, t.Hud.Hash, Fnv("Filter", t.Hud.Hash), false, "gold"));
            l = Pass(t, "salvage filtered");
            Check(l.OpCount == 9 && S(l, l.Ops[3].Str, l.Ops[3].StrLen) == "Salvaged Gold", "salvage: the filter left one row");
        }

        private static void LuaVectors()
        {
            var s = new Script(CoreModules.Preset_SoftSandbox);
            LibraryImGuiValues.Register(s);
            string code = """
                local a = Vector2.new(1, 2)
                local b = Vector2(3, 4)
                assert(tostring(a + b) == "Vector2(4, 6)", tostring(a + b))
                assert((a * 2).X == 2 and (2 * a).y == 4)
                assert(a * b == Vector2.new(3, 8))
                assert(-a == Vector2.new(-1, -2))
                assert((b / 2).X == 1.5)
                assert((b - a) == Vector2.new(2, 2))
                assert(a ~= b)
                a.x = 9; assert(a.X == 9 and a.x == 9)
                local c = Vector4.new(1, 0, 0.5, 1)
                assert(tostring(c) == "Vector4(1, 0, 0.5, 1)", tostring(c))
                assert(c.z == 0.5 and c.W == 1)
                assert(Vector2.Zero.X == 0 and Vector4.One.W == 1)
                local z = Vector2.Zero; z.X = 5; assert(Vector2.Zero.X == 0)
                local ok = pcall(function() return a + "x" end)
                assert(not ok)
                assert(ImGuiWindowFlags.NoDecoration == 43 and ImGuiWindowFlags[43] == "NoDecoration")
                assert(ImGuiCol.button == 21 and ImGuiCond.FirstUseEver == 4 and ImGuiTreeNodeFlags.DefaultOpen == 32)
                assert(ImGuiInputTextFlags.EnterReturnsTrue == 64 and ImGuiSelectableFlags.AllowDoubleClick == 4)
                assert(ImGuiChildFlags.Borders == 1)
                return true
                """;
            try { Check(s.DoString(code).CastToBool(), "Lua vector script"); }
            catch (InterpreterException ex) { Check(false, "Lua: " + (ex.DecoratedMessage ?? ex.Message)); }

            Check(LibraryImGuiValues.TryVec2(s.DoString("return {3, 4}"), out float x, out float y) && x == 3 && y == 4, "TryVec2 {3, 4}");
            Check(LibraryImGuiValues.TryVec2(s.DoString("return {X = 1, Y = 2}"), out x, out y) && x == 1 && y == 2, "TryVec2 {X=, Y=}");
            Check(LibraryImGuiValues.TryVec2(s.DoString("return {x = 5, y = 6}"), out x, out y) && x == 5 && y == 6, "TryVec2 {x=, y=}");
            Check(LibraryImGuiValues.TryVec2(s.DoString("return Vector2.new(7, 8)"), out x, out y) && x == 7 && y == 8, "TryVec2 Vector2");
            Check(!LibraryImGuiValues.TryVec2(DynValue.Nil, out _, out _) && !LibraryImGuiValues.TryVec2(DynValue.NewString("a"), out _, out _)
                  && !LibraryImGuiValues.TryVec2(s.DoString("return {}"), out _, out _), "TryVec2 refuses nil, strings, empty tables");
            Check(LibraryImGuiValues.TryVec4(s.DoString("return {1, 2, 3, 4}"), out float vx, out _, out _, out float vw) && vx == 1 && vw == 4, "TryVec4");
            Check(LibraryImGuiValues.ColorU32(s.DoString("return Vector4.new(1, 0, 0, 1)")) == 0xFF0000FF, "red Vector4 -> 0xFF0000FF");
            Check(LibraryImGuiValues.ColorU32(s.DoString("return {0.5, 0.5, 0.5, 1}")) == 0xFF808080, "grey");
            Check(LibraryImGuiValues.ColorU32(s.DoString("return {2, -1, 0, 0.5}")) == 0x800000FF, "clamped");
            Check(LibraryImGuiValues.ColorU32(DynValue.NewNumber(0xFF00FF00)) == 0xFF00FF00, "numbers pass through");
            Check(LibraryImGuiValues.ColorU32(DynValue.NewNumber(-1)) == 0xFFFFFFFF, "-1 = white");
            Throws(() => LibraryImGuiValues.ColorU32(DynValue.NewString("red")), "a colour is a Vector4");
            DynValue v = LibraryImGuiValues.NewVec2(s, 1.5f, 2);
            Check(v.Table.Get("X").Number == 1.5 && v.Table.MetaTable != null, "NewVec2");
        }

        // ── views module (slice 5) ──────────────────────────────────────

        private sealed class ViewsRig
        {
            public required RynthLuaPlugin Plugin;
            public required LuaScriptHost Host;
            public required ScriptContext Ctx;
            public void Tick() => Host.Tick(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
            public DynValue Eval(string expr) => Host.Lua!.DoString("return " + expr);
            public void Do(string code) => Host.Lua!.DoString(code);
            public ScriptHud Hud(string name) => Huds(Plugin).First(h => h.Name == name && h.Ctx == Ctx);
            public HudVisibilityMemory Memory => HudMemoryField(Plugin)!;
        }

        private static readonly List<string> TempFiles = new();

        /// <summary>A fresh path in the temp folder (never RynthLua's real data folder).</summary>
        private static string TempFile()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rynthlua-test-{Guid.NewGuid():N}.json");
            TempFiles.Add(path);
            return path;
        }

        private static void CleanTempFiles()
        {
            foreach (string f in TempFiles)
            {
                try { if (System.IO.File.Exists(f)) System.IO.File.Delete(f); } catch { }
                try { if (System.IO.File.Exists(f + ".tmp")) System.IO.File.Delete(f + ".tmp"); } catch { }
            }
        }

        /// <summary>A plugin whose visibility memory lives at <paramref name="memoryPath"/>, logged in as <paramref name="character"/>.</summary>
        private static RynthLuaPlugin NewViewsPlugin(string memoryPath, string character, List<string>? log = null)
        {
            var p = new RynthLuaPlugin();
            HudMemoryField(p) = new HudVisibilityMemory(memoryPath, s => log?.Add(s));
            SetCharacter(p, character);
            return p;
        }

        /// <summary>A script <paramref name="script"/> running <paramref name="code"/> against the real views and imgui modules.</summary>
        private static ViewsRig ViewsWith(RynthLuaPlugin plugin, string code, string script = "t")
        {
            var host = new LuaScriptHost(_ => { }, (s, h) => LibraryImGuiValues.Register(s));
            var ctx = new ScriptContext(script, null, string.Empty, new ScriptManifest(), host);
            host.ResolveModule = (s, m) => m == "views" ? CreateViewsModule(plugin, s, ctx) : m == "imgui" ? CreateImGuiModule(plugin, s) : null;
            host.Run(code, script);
            var rig = new ViewsRig { Plugin = plugin, Host = host, Ctx = ctx };
            rig.Tick();
            Check(host.Loaded, "views script loaded: " + host.ConsoleText());
            return rig;
        }

        /// <summary>
        /// The engine's panel key ignores case and treats '=', ',' and '#' as '_' (control
        /// characters as spaces), and refuses a second window on one key on every submit.
        /// CreateHud refuses such a name up front, in the same script or another.
        /// </summary>
        private static void ViewsPanelKeyClash()
        {
            RynthLuaPlugin p = NewViewsPlugin(TempFile(), "Srv|Alice");
            ViewsRig r = ViewsWith(p, """
                views = require("views")
                a = views.Huds.CreateHud("Main")
                b = views.Huds.CreateHud("a=b")
                c = views.Huds.CreateHud("Tab\tHud")
                """);
            LuaFails(r, "views.Huds.CreateHud('main')", "clashes with hud \"Main\"");
            LuaFails(r, "views.Huds.CreateHud('A_B')", "clashes with hud \"a=b\"");
            LuaFails(r, "views.Huds.CreateHud('a#b')", "clashes with hud \"a=b\"");
            LuaFails(r, "views.Huds.CreateHud('tab hud')", "clashes with hud");
            r.Do("same = views.Huds.CreateHud('Main') == a; other = views.Huds.CreateHud('Main 2')");
            Check(r.Eval("same").CastToBool() && !r.Eval("other").IsNil(), "the same name still returns the hud; a different one is fine");
            Check(Huds(p).Count == 4, $"four huds ({Huds(p).Count})");

            // Another script: its keys differ by the script name, so only a same-script clash counts...
            ViewsRig r2 = ViewsWith(p, """views = require("views"); m = views.Huds.CreateHud("main")""", "u");
            Check(!r2.Eval("m").IsNil(), "another script can use the name");
            // ...unless the script names themselves meet (T vs t).
            ViewsRig r3 = ViewsWith(p, """views = require("views")""", "T");
            LuaFails(r3, "views.Huds.CreateHud('MAIN')", "of t");

            // Disposed huds free their key.
            r.Do("a.Dispose(); again = views.Huds.CreateHud('MAIN')");
            Check(!r.Eval("again").IsNil(), "a disposed hud's name can be taken again in another case");
            Check(RynthLuaPlugin.PanelKeyOf("RynthLua/s/a=b,c#d\te") == "RynthLua/s/a_b_c_d e", "PanelKeyOf mirrors the engine");
        }

        private static int Count(string text, string what)
        {
            int n = 0;
            for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        private static bool LuaFails(ViewsRig r, string statement, string contains)
        {
            r.Do($"ok_, err_ = pcall(function() {statement} end)");
            bool failed = !r.Eval("ok_").CastToBool();
            string err = r.Eval("tostring(err_)").String;
            Check(failed && err.Contains(contains), $"'{statement}' fails with '{contains}': {(failed ? err : "it didn't fail")}");
            return failed;
        }

        private static void ViewsProperties()
        {
            RynthLuaPlugin p = NewViewsPlugin(TempFile(), "Srv|Alice");
            ViewsRig r = ViewsWith(p, """
                views = require("views"); ImGui = require("imgui")
                hud = views.Huds.CreateHud("Main")
                hud.OnRender.Add(function() ImGui.Text("hi") end)
                same = views.Huds.CreateHud("Main") == hud
                avail = views.Available
                views.Huds.CreateHud("Second").OnRender.Add(function() end)
                """);
            ScriptHud h = r.Hud("Main");
            Check(r.Eval("same").CastToBool(), "CreateHud with the same name returns the same hud");
            Check(!r.Eval("avail").CastToBool(), "views.Available is false without API v71");
            Check(Count(r.Host.ConsoleText(), "need RynthCore API v71 or later with ImGui on") == 1, "the v71 note is printed once per script: " + r.Host.ConsoleText());

            // Defaults.
            Check(r.Eval("hud.Name").String == "Main" && r.Eval("hud.Title").String == "Main", "Name and Title");
            Check(!r.Eval("hud.Visible").CastToBool() && !h.Visible, "starts hidden when nothing is remembered");
            Check(r.Eval("hud.ShowInBar").CastToBool() && r.Eval("hud.WindowSettings").Number == 0, "ShowInBar true, WindowSettings 0");
            Check(r.Eval("hud.Chrome").String == "standard" && r.Eval("hud.RefreshRate").Number == 20, "Chrome standard, RefreshRate 20");
            Check(r.Eval("hud.DefaultSize.X").Number == 0 && r.Eval("hud.LastError").IsNil() && !r.Eval("hud.IsDisposed").CastToBool(), "DefaultSize 0, no error, not disposed");

            // Title: header change, and a pass.
            h.PropsDirty = h.PassNow = false;
            r.Do("hud.Title = 'Tracker'");
            Check(h.Title == "Tracker" && h.PropsDirty && h.PassNow, "Title sets PropsDirty and PassNow");

            // WindowSettings: the honoured subset, one warning naming the rest.
            h.PropsDirty = h.PassNow = false;
            r.Do("hud.WindowSettings = ImGuiWindowFlags.NoTitleBar + ImGuiWindowFlags.NoInputs + ImGuiWindowFlags.NoBackground + ImGuiWindowFlags.AlwaysAutoResize + ImGuiWindowFlags.NoCollapse");
            Check(h.ChromeNone && h.ClickThrough && h.NoBackground && !h.NoScrollbar && !h.NoScrollWithMouse, "NoTitleBar / NoInputs / NoBackground honoured");
            Check(h.PropsDirty && h.PassNow, "WindowSettings sets PropsDirty and PassNow");
            Check(r.Eval("hud.WindowSettings").Number == 1 + 197120 + 128 + 64 + 32, "WindowSettings reads back as set");
            Check(r.Eval("hud.Chrome").String == "none", "NoTitleBar reads as Chrome none");
            string console = r.Host.ConsoleText();
            Check(Count(console, "ignored") == 1 && console.Contains("AlwaysAutoResize") && !console.Contains("NoCollapse") && !console.Contains("NoNavInputs"),
                "one warning naming AlwaysAutoResize only: " + console);
            r.Do("hud.WindowSettings = ImGuiWindowFlags.NoScrollbar + ImGuiWindowFlags.NoScrollWithMouse + ImGuiWindowFlags.MenuBar");
            Check(!h.ChromeNone && !h.ClickThrough && !h.NoBackground && h.NoScrollbar && h.NoScrollWithMouse, "a new value replaces the old mapping");
            Check(Count(r.Host.ConsoleText(), "ignored") == 1, "still one warning per hud");
            LuaFails(r, "hud.WindowSettings = -1", "WindowSettings must be ImGuiWindowFlags");
            LuaFails(r, "hud.WindowSettings = 'NoTitleBar'", "WindowSettings must be ImGuiWindowFlags");
            r.Do("hud.WindowSettings = nil");
            Check(!h.NoScrollbar && r.Eval("hud.WindowSettings").Number == 0, "nil clears WindowSettings");

            // Chrome.
            r.Do("hud.Chrome = 'none'");
            Check(h.ChromeNone, "Chrome none");
            r.Do("hud.Chrome = 'Standard'");
            Check(!h.ChromeNone, "Chrome standard (any case)");
            LuaFails(r, "hud.Chrome = 'fancy'", "must be \"standard\" or \"none\"");
            LuaFails(r, "hud.Chrome = nil", "must be \"standard\" or \"none\"");

            // DefaultSize.
            h.PropsDirty = false;
            r.Do("hud.DefaultSize = Vector2.new(300, 200)");
            Check(h.DefaultSize == new Vector2(300, 200) && h.PropsDirty, "DefaultSize from a Vector2 sets PropsDirty");
            Check(r.Eval("hud.DefaultSize.Y").Number == 200 && r.Eval("tostring(hud.DefaultSize)").String == "Vector2(300, 200)", "DefaultSize reads back as a Vector2");
            r.Do("hud.DefaultSize = {10, 20}");
            Check(h.DefaultSize == new Vector2(10, 20), "DefaultSize from {x, y}");
            r.Do("hud.DefaultSize = nil");
            Check(h.DefaultSize == Vector2.Zero, "nil resets DefaultSize");
            r.Do("hud.DefaultSize = Vector2.new(0/0, 5)");
            Check(h.DefaultSize == new Vector2(0, 5), "non-finite DefaultSize is stored as 0");
            LuaFails(r, "hud.DefaultSize = 'big'", "DefaultSize must be a Vector2");

            // RefreshRate.
            r.Do("hud.RefreshRate = 500");
            Check(h.RefreshRate == 60, "RefreshRate clamps to 60");
            r.Do("hud.RefreshRate = 0");
            Check(h.RefreshRate == 1, "RefreshRate clamps to 1");
            r.Do("hud.RefreshRate = 7.4");
            Check(h.RefreshRate == 7 && r.Eval("hud.RefreshRate").Number == 7, "RefreshRate rounds");
            r.Do("hud.RefreshRate = nil");
            Check(h.RefreshRate == 20, "nil restores the default rate");
            LuaFails(r, "hud.RefreshRate = 'fast'", "RefreshRate must be a number");

            // ShowInBar.
            h.PropsDirty = false;
            r.Do("hud.ShowInBar = false");
            Check(!h.ShowInBar && h.PropsDirty && (h.HeaderFlags() & HudFormat.FlagShowInBar) == 0, "ShowInBar false clears the header bit");

            // NoPopOut (header bit8: no ↗; the engine redocks a popped-out window when it turns on).
            Check(!r.Eval("hud.NoPopOut").CastToBool() && (h.HeaderFlags() & HudFormat.FlagNoPopOut) == 0, "NoPopOut starts false");
            h.PropsDirty = false;
            r.Do("hud.NoPopOut = true");
            Check(h.NoPopOut && h.PropsDirty && (h.HeaderFlags() & HudFormat.FlagNoPopOut) != 0 && r.Eval("hud.NoPopOut").CastToBool(), "NoPopOut true sets the header bit");
            int rcPop = Parse(new[] { h }, out var wPop, out string errPop, out _);
            Check(rcPop == 0 && (wPop[0].Flags & WindowFlags.NoPopOut) != 0 && HudFormat.FlagNoPopOut == WindowFlags.NoPopOut, "the engine reads NoPopOut " + errPop);
            h.PropsDirty = false;
            r.Do("hud.NoPopOut = true");
            Check(!h.PropsDirty, "setting the same NoPopOut again changes nothing");
            r.Do("hud.NoPopOut = false");
            Check(!h.NoPopOut && h.PropsDirty && (h.HeaderFlags() & HudFormat.FlagNoPopOut) == 0, "NoPopOut false clears it");

            // Read-only members and the script's own fields.
            LuaFails(r, "hud.Name = 'x'", "read-only");
            LuaFails(r, "hud.LastError = 'x'", "read-only");
            LuaFails(r, "hud.IsDisposed = true", "read-only");
            r.Do("hud.myField = 5");
            Check(r.Eval("hud.myField").Number == 5, "unknown keys are the script's own fields");

            // The header the engine parses.
            r.Do("hud.Chrome = 'none'; hud.WindowSettings = ImGuiWindowFlags.NoTitleBar + ImGuiWindowFlags.NoScrollbar; hud.DefaultSize = Vector2.new(320, 240)");
            int rc = Parse(new[] { h }, out var w, out string err, out _);
            Check(rc == 0 && (w[0].Flags & WindowFlags.ChromeNone) != 0 && (w[0].Flags & WindowFlags.NoScrollbar) != 0
                  && w[0].DefaultSize == new Vector2(320, 240) && w[0].Title == "Tracker", "the engine reads the properties from the header " + err);

            // Dispose.
            r.Do("hud.Dispose()");
            Check(r.Eval("hud.IsDisposed").CastToBool() && !Huds(p).Contains(h), "Dispose removes the hud");
        }

        private const string VisibilityScript = """
            views = require("views")
            hud = views.Huds.CreateHud("Main")
            shows, hides = 0, 0
            hud.OnShow.Add(function() shows = shows + 1 end)
            hud.OnHide.Add(function() hides = hides + 1 end)
            hud.OnRender.Add(function() end)
            """;

        private static void ViewsVisibility()
        {
            const string alice = "Srv|Alice";
            string memPath = TempFile();
            RynthLuaPlugin p = NewViewsPlugin(memPath, alice);
            ViewsRig r = ViewsWith(p, VisibilityScript);
            ScriptHud h = r.Hud("Main");
            HudVisibilityMemory mem = r.Memory;
            int Shows(ViewsRig x) => (int)x.Eval("shows").Number;
            int Hides(ViewsRig x) => (int)x.Eval("hides").Number;

            // (a) The script shows it: OnShow fires, the flag goes out, nothing is remembered.
            h.PropsDirty = false;
            r.Do("hud.Visible = true");
            r.Tick();
            Check(h.Visible && h.PropsDirty && h.PassNow && h.FirstPassSinceShown, "hud.Visible = true: shown, a pass and a submit are due");
            Check((h.HeaderFlags() & HudFormat.FlagVisible) != 0, "the Visible flag is in the next header");
            Check(Shows(r) == 1 && Hides(r) == 0, "OnShow fired once");
            Check(!mem.Dirty && !mem.TryGet(alice, h.Key, out _), "the script's choice is not remembered");
            r.Do("hud.Visible = true");
            r.Tick();
            Check(Shows(r) == 1, "no OnShow without a change");

            // (b) The player's X: reason 1.
            ApplyHudEvents(p, Ev(HudFormat.EvVisibility, 5, h.Hash, 0, 0, 1));
            r.Tick();
            Check(!h.Visible && !r.Eval("hud.Visible").CastToBool() && Hides(r) == 1, "the X hides it and OnHide fires");
            Check(mem.TryGet(alice, h.Key, out bool v) && !v && mem.Dirty, "the player's close is remembered");
            h.PropsDirty = false;
            ApplyHudEvents(p, Ev(HudFormat.EvVisibility, 6, h.Hash, 0, 0, 2));
            Check(h.PropsDirty && Hides(r) == 1, "an event matching the state still sends the flag (PropsDirty), no second OnHide");

            // Debounced write.
            mem.Tick(Environment.TickCount64);
            Check(!System.IO.File.Exists(memPath), "not written straight away");
            mem.Tick(Environment.TickCount64 + 2500);
            Check(System.IO.File.Exists(memPath) && !mem.Dirty, "written ~2 s after the last change");
            using (var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(memPath)))
                Check(doc.RootElement.GetProperty(alice).GetProperty(h.Key).ValueKind == System.Text.Json.JsonValueKind.False, "file shape {character: {key: false}}");

            // Reason 0 (a script-side change echoed): applied, not remembered.
            ApplyHudEvents(p, Ev(HudFormat.EvVisibility, 7, h.Hash, 0, 1, 0));
            Check(h.Visible && !mem.Dirty, "reason 0 isn't remembered");
            r.Do("hud.Visible = false");

            // (c) The script restarts: CreateHud starts hidden (the player closed it).
            r.Host.Stop("restarted");
            Check(!Huds(p).Contains(h) && h.Disposed, "stopping the script disposes its huds");
            ViewsRig r2 = ViewsWith(p, VisibilityScript);
            ScriptHud h2 = r2.Hud("Main");
            Check(!h2.Visible && !h2.PassNow && Shows(r2) == 0, "restarted: hidden, as the player left it");

            // /lua hud show|hide|toggle: like the player.
            HandleHudCommand(p, "show t/main");
            r2.Tick();
            Check(h2.Visible && Shows(r2) == 1 && mem.TryGet(alice, h2.Key, out v) && v, "/lua hud show t/main: shown, OnShow, remembered");
            HandleHudCommand(p, "toggle Main");
            r2.Tick();
            Check(!h2.Visible && Hides(r2) == 1 && mem.TryGet(alice, h2.Key, out v) && !v, "/lua hud toggle Main (unique name): hidden, remembered");
            HandleHudCommand(p, "toggle MAIN");
            r2.Tick();
            Check(h2.Visible && Shows(r2) == 2 && mem.TryGet(alice, h2.Key, out v) && v, "toggle again, any case");
            HandleHudCommand(p, "hide nope");
            HandleHudCommand(p, "show");
            HandleHudCommand(p, "list");
            HandleHudCommand(p, "");
            Check(h2.Visible, "unknown or missing names change nothing");
            ViewsRig other = ViewsWith(p, VisibilityScript, "u");
            HandleHudCommand(p, "hide Main");
            Check(h2.Visible && !other.Hud("Main").Visible, "an ambiguous hud name changes nothing");
            HandleHudCommand(p, "hide u/Main");
            HandleHudCommand(p, "show u/Main");
            Check(other.Hud("Main").Visible && h2.Visible, "script/hud picks one of two same-named huds");

            // Restarted after the player showed it: starts shown, a pass due, no OnShow at creation.
            r2.Host.Stop("restarted");
            ViewsRig r3 = ViewsWith(p, VisibilityScript);
            ScriptHud h3 = r3.Hud("Main");
            Check(h3.Visible && h3.PassNow && h3.FirstPassSinceShown && Shows(r3) == 0, "restarted: shown, as the player left it");
            // The script's hud.Visible wins, and isn't remembered.
            r3.Do("hud.Visible = false");
            r3.Tick();
            Check(!h3.Visible && Hides(r3) == 1 && mem.TryGet(alice, h3.Key, out v) && v, "the script hides it; the player's 'shown' stays remembered");

            // Characters don't share; before login nothing is recalled or recorded.
            mem.Flush();
            RynthLuaPlugin bob = NewViewsPlugin(memPath, "Srv|Bob");
            Check(!ViewsWith(bob, VisibilityScript).Hud("Main").Visible, "another character: hidden");
            RynthLuaPlugin alice2 = NewViewsPlugin(memPath, alice);
            Check(ViewsWith(alice2, VisibilityScript).Hud("Main").Visible, "the same character after a reload: shown (read from the file)");
            RynthLuaPlugin nobody = NewViewsPlugin(memPath, "");
            ViewsRig rn = ViewsWith(nobody, VisibilityScript);
            ScriptHud hn = rn.Hud("Main");
            Check(!hn.Visible, "before login: hidden");
            ApplyHudEvents(nobody, Ev(HudFormat.EvVisibility, 1, hn.Hash, 0, 1, 2));
            Check(hn.Visible && !HudMemoryField(nobody)!.Dirty, "before login: the player's choice applies but isn't recorded");
        }

        /// <summary>
        /// A pass that stops its own script, disposes its own hud or creates another one: the
        /// pass ends cleanly, the recorder is free afterwards, and the stopped script's huds are
        /// gone from the next submit (so the engine closes their windows).
        /// </summary>
        private static void ViewsStopDuringPass()
        {
            RynthLuaPlugin p = NewViewsPlugin(TempFile(), "Srv|Alice");
            UiAvailableField(p) = true;
            UiInfoAtField(p) = long.MaxValue;
            ViewsRig r = ViewsWith(p, """
                views = require("views"); ImGui = require("imgui")
                a = views.Huds.CreateHud("A"); b = views.Huds.CreateHud("B"); c = views.Huds.CreateHud("C")
                a.OnRender.Add(function() ImGui.Text("a"); a.Dispose(); ImGui.Text("after dispose") end)
                b.OnRender.Add(function() ImGui.Text("b"); if not made then made = views.Huds.CreateHud("Made") end end)
                c.OnRender.Add(function() ImGui.PushID("x"); ImGui.Text("c"); stop(); ImGui.Text("after stop") end)
                a.Visible = true; b.Visible = true; c.Visible = true
                """);
            for (int i = 0; i < 3; i++)
            {
                try { RunHudPasses(p); }
                catch (Exception ex) { Check(false, $"a pass threw {ex.GetType().Name}: {ex.Message}"); }
            }
            Check(RecorderOf(p).Hud == null, "the recorder is free after the passes");
            Check(!r.Host.Loaded, "stop() inside OnRender stopped the script");
            Check(Huds(p).Count == 0, $"the stopped script's huds are all gone ({string.Join(", ", Huds(p).Select(h => h.Name))})");
            Throws(() => RecorderOf(p).Separator(), "only work inside a hud's OnRender");
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_recorder")]
        private static extern ref HudRecorder RecorderOf(RynthLuaPlugin p);

        private static void ViewsScheduling()
        {
            RynthLuaPlugin p = NewViewsPlugin(TempFile(), "Srv|Alice");
            ViewsRig r = ViewsWith(p, """
                views = require("views"); ImGui = require("imgui")
                hud = views.Huds.CreateHud("S")
                renders, fail = 0, false
                hud.OnRender.Add(function()
                  renders = renders + 1
                  if fail then error("bad render") end
                  ImGui.Button("B")
                end)
                hud.Visible = true
                """);
            ScriptHud h = r.Hud("S");
            UiAvailableField(p) = true;
            UiInfoAtField(p) = long.MaxValue;   // RefreshUiInfo leaves _uiAvailable alone
            int Renders() => (int)r.Eval("renders").Number;
            long f = System.Diagnostics.Stopwatch.Frequency;

            // Each pass is timed between a stamp before and one after it, not from "now" after it: on a
            // busy PC the gap after a pass can be longer than the 1/60 s being checked (2026-10-22).
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            RunHudPasses(p);
            long after = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(Renders() == 1 && h.EverPassed && !h.FirstPassSinceShown && !h.PassNow, "just shown: a pass on the first tick");
            Check(h.NextPassAt > before && h.NextPassAt <= after + f / 20, "the next pass is one 20/s interval away");
            RunHudPasses(p);
            Check(Renders() == 1, "no pass before the interval");
            h.NextPassAt = 0;
            RunHudPasses(p);
            Check(Renders() == 2, "a pass once the interval has passed");

            // Cadence: an interval pass a little late doesn't push the next one back.
            long due = System.Diagnostics.Stopwatch.GetTimestamp() - f / 200;   // 5 ms late
            h.NextPassAt = due;
            RunHudPasses(p);
            Check(Renders() == 3 && h.NextPassAt == due + f / 20, "interval passes keep their cadence");

            r.Do("hud.RefreshRate = 60");
            before = System.Diagnostics.Stopwatch.GetTimestamp();
            RunHudPasses(p);
            after = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(Renders() == 4 && h.NextPassAt > before && h.NextPassAt <= after + f / 60, "a new RefreshRate starts at once, 60/s");
            r.Do("hud.RefreshRate = 1");
            before = System.Diagnostics.Stopwatch.GetTimestamp();
            RunHudPasses(p);
            Check(Renders() == 5 && h.NextPassAt - before > f / 2, "1/s");

            // Input: a pass on the next tick whatever the rate.
            ApplyHudEvents(p, Ev(HudFormat.EvClicked, 1, h.Hash, Fnv("B", h.Hash), 1, 0));
            RunHudPasses(p);
            Check(Renders() == 6 && h.Clicks.Count == 0, "a pass right after input");
            // A property that changes the picture.
            r.Do("hud.Chrome = 'none'");
            RunHudPasses(p);
            Check(Renders() == 7, "a pass after a property change");

            // Hidden: no passes at all.
            r.Do("hud.Visible = false");
            h.PassNow = true;
            h.NextPassAt = 0;
            RunHudPasses(p);
            Check(Renders() == 7, "a hidden hud runs no pass");
            r.Do("hud.Visible = true");
            RunHudPasses(p);
            Check(Renders() == 8, "shown again: a pass");

            // (d) Errors: LastError in the window, 20 strikes stop it, hide + show retries.
            r.Do("fail = true");
            for (int i = 0; i < 25; i++) { h.PassNow = true; RunHudPasses(p); }
            Check(Renders() == 28 && h.ErrorStreak == 20, $"20 strikes, then no more passes ({Renders()}, streak {h.ErrorStreak})");
            Check(h.LastError != null && h.LastError.Contains("bad render") && r.Eval("hud.LastError").String.Contains("bad render"), "LastError: " + h.LastError);
            int rc = Parse(new[] { h }, out var w, out _, out _);
            Check(rc == 0 && w[0].ErrorText != null && w[0].ErrorText!.Contains("bad render"), "the error is shown in the window");
            Check(r.Host.ConsoleText().Contains("stopped drawing after 20 errors"), "the strike-out message");
            r.Do("fail = false; hud.Visible = false; hud.Visible = true");
            Check(h.ErrorStreak == 0, "hide + show resets the strikes");
            RunHudPasses(p);
            Check(Renders() == 29 && h.LastError == null, "it draws again");
        }

        private static void VisibilityMemoryFile()
        {
            string path = TempFile();
            var log = new List<string>();
            var m = new HudVisibilityMemory(path, log.Add);
            Check(!m.TryGet("A", "k", out _) && log.Count == 0, "a missing file is empty, quietly");
            m.Record("", "k", true, 0);
            Check(!m.Dirty, "no character: nothing recorded");
            m.Record("A", "k1", true, 0);
            m.Record("B", "k1", false, 0);
            m.Record("A", "k2", false, 0);
            m.Flush();
            Check(System.IO.File.Exists(path) && !m.Dirty && !System.IO.File.Exists(path + ".tmp"), "Flush writes the file");
            var m2 = new HudVisibilityMemory(path, log.Add);
            Check(m2.TryGet("A", "k1", out bool v) && v && m2.TryGet("B", "k1", out v) && !v && m2.TryGet("A", "k2", out v) && !v
                  && !m2.TryGet("B", "k2", out _), "read back per character");
            m2.Record("A", "k1", true, 0);
            Check(!m2.Dirty, "recording the same choice doesn't dirty it");

            // The cap: 500 per character, the oldest dropped.
            for (int i = 0; i < 499; i++) m2.Record("A", "w" + i, true, 0);   // 501 keys: one goes
            Check(m2.TryGet("A", "k1", out _) && !m2.TryGet("A", "k2", out _) && m2.TryGet("A", "w0", out _),
                "at 501 the oldest (k2) goes; k1 was freshened by the repeat record, so it stays");
            for (int i = 499; i < 510; i++) m2.Record("A", "w" + i, true, 0);
            m2.Flush();
            var m3 = new HudVisibilityMemory(path, log.Add);
            Check(m3.TryGet("A", "w509", out _) && m3.TryGet("A", "w10", out _) && !m3.TryGet("A", "w9", out _) && !m3.TryGet("A", "k1", out _),
                "500 keys per character, oldest dropped, across a save and load");
            Check(m3.TryGet("B", "k1", out _), "another character's keys are untouched by the cap");

            // Bad files: ignored with one log line; the next save replaces them.
            System.IO.File.WriteAllText(path, "{not json");
            var m4 = new HudVisibilityMemory(path, log.Add);
            Check(!m4.TryGet("A", "k1", out _) && !m4.TryGet("B", "k1", out _), "a bad file is empty");
            Check(log.Count(l => l.Contains("can't read")) == 1, "one log line for it: " + string.Join(" | ", log));
            m4.Record("A", "x", true, 0);
            m4.Flush();
            Check(new HudVisibilityMemory(path, log.Add).TryGet("A", "x", out v) && v, "the next save replaces it");
            System.IO.File.WriteAllText(path, """{"A": 5, "B": {"k": "yes", "j": true, "n": null}, "": {"z": true}, "C": []}""");
            var m5 = new HudVisibilityMemory(path, log.Add);
            Check(m5.TryGet("B", "j", out v) && v && !m5.TryGet("B", "k", out _) && !m5.TryGet("A", "k", out _) && !m5.TryGet("", "z", out _),
                "wrong shapes are skipped, good entries kept");

            // A write that fails: one log line, no exception.
            string blocker = TempFile();
            System.IO.File.WriteAllText(blocker, "a file, not a folder");
            var m6 = new HudVisibilityMemory(System.IO.Path.Combine(blocker, "hud_visibility.json"), log.Add);
            m6.Record("A", "k", true, 0);
            m6.Flush();
            m6.Record("A", "k", false, 0);
            m6.Flush();
            Check(log.Count(l => l.Contains("can't write")) == 1 && !m6.Dirty, "a failing write logs once and doesn't throw");
        }

        // ── resume.json (slice 6) ───────────────────────────────────────

        /// <summary>A script context that is loaded (running) until stopped; no game API.</summary>
        private static ScriptContext RunningContext(string name, bool transient, bool running = true)
        {
            var host = new LuaScriptHost(_ => { }, (_, _) => { });
            var ctx = new ScriptContext(name, null, string.Empty, new ScriptManifest(), host) { Transient = transient };
            host.Run("x = 1", name);
            if (!running) host.Stop("stopped");
            return ctx;
        }

        private static List<string> ResumeScriptsIn(string path, out int pid, out long savedAt)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
            pid = doc.RootElement.GetProperty("pid").GetInt32();
            savedAt = doc.RootElement.GetProperty("savedAtUtcMs").GetInt64();
            return doc.RootElement.GetProperty("scripts").EnumerateArray().Select(e => e.GetString()!).ToList();
        }

        private static void ResumeAfterReload()
        {
            // Names no real settings.json lists as autostart (AutostartOf only reads it).
            const string manual = "rl-resume-test-manual", exec = "rl-resume-test-exec", idle = "rl-resume-test-idle";
            string path = TempFile();

            // (a) Shutdown takes the list while the scripts run, then stops them, then writes the file.
            var p = NewViewsPlugin(TempFile(), "Srv|Alice");
            SetResumeFile(p, path);
            var scripts = ScriptsField(p);
            ScriptContext m = RunningContext(manual, transient: false);
            ScriptContext e = RunningContext(exec, transient: true);
            scripts[manual] = m;
            scripts[exec] = e;
            scripts[idle] = RunningContext(idle, transient: false, running: false);
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            p.Shutdown();
            Check(!m.Host.Loaded && !e.Host.Loaded, "Shutdown stopped the scripts");
            Check(System.IO.File.Exists(path) && !System.IO.File.Exists(path + ".tmp"), "Shutdown wrote resume.json");
            List<string> names = ResumeScriptsIn(path, out int pid, out long savedAt);
            Check(pid == Environment.ProcessId, "pid is this process");
            Check(savedAt >= before && savedAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "savedAtUtcMs is now");
            Check(names.Count == 1 && names[0] == manual, "only the running file script, captured before StopAll (not the exec one, not the stopped one): " + string.Join(",", names));

            // The reload: Initialize-time read keeps the list and deletes the file.
            var p2 = NewViewsPlugin(TempFile(), "Srv|Alice");
            SetResumeFile(p2, path);
            LoadResume(p2);
            Check(ResumePendingField(p2) is { Count: 1 } l && l[0] == manual, "the new generation keeps the list");
            Check(!System.IO.File.Exists(path), "the file is deleted once read");

            // Login too long after the reload: the list is dropped, nothing starts.
            ResumePendingAtField(p2) = Environment.TickCount64 - 10 * 60_000;
            ResumeScripts(p2);
            Check(ResumePendingField(p2) == null, "a late login drops the list");

            // An empty shutdown still writes the file, so an older one can't bring scripts back.
            System.IO.File.WriteAllText(path, $$"""{"pid": {{Environment.ProcessId}}, "savedAtUtcMs": {{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}, "scripts": ["{{manual}}"]}""");
            var p3 = NewViewsPlugin(TempFile(), "Srv|Alice");
            SetResumeFile(p3, path);
            p3.Shutdown();
            Check(System.IO.File.Exists(path) && ResumeScriptsIn(path, out _, out _).Count == 0, "an empty list is written over the old file");
            var p4 = NewViewsPlugin(TempFile(), "Srv|Alice");
            SetResumeFile(p4, path);
            LoadResume(p4);
            Check(ResumePendingField(p4) == null && !System.IO.File.Exists(path), "an empty list resumes nothing and is deleted");

            // (c) Candidates: transient and stopped scripts never; (d) autostart ones never (they restart on their own).
            var ctxs = new[]
            {
                RunningContext("A", false), RunningContext("G", false), RunningContext("C", false),
                RunningContext("Ed", true), RunningContext("S", false, running: false), RunningContext("a", false),
            };
            List<string> c = RynthLuaPlugin.ResumeCandidates(ctxs, n => n == "G" ? "global" : n == "C" ? "character" : "none");
            Check(c.SequenceEqual(new[] { "A" }), "candidates: running, from a file, no autostart, no duplicate names: " + string.Join(",", c));

            // (d) Login: running ones skipped, one failure or throw doesn't stop the rest.
            var log = new List<string>();
            var startedNames = new List<string>();
            int n = RynthLuaPlugin.RunResume(new[] { "one", "Auto", "gone", "boom", "two" },
                x => x == "Auto",
                x =>
                {
                    if (x == "boom") throw new InvalidOperationException("kaboom");
                    if (x == "gone") return "No script named 'gone'. /lua list shows them.";
                    startedNames.Add(x);
                    return null;
                },
                log.Add);
            Check(n == 2 && startedNames.SequenceEqual(new[] { "one", "two" }), "RunResume started one and two: " + string.Join(",", startedNames));
            Check(log.Contains("[RynthLua] Resumed one.") && log.Any(s => s.StartsWith("[RynthLua] Resume gone: No script")) && log.Any(s => s.Contains("kaboom"))
                  && !log.Any(s => s.Contains("Auto")), "each result logged; the running one skipped quietly: " + string.Join(" | ", log));
        }

        private static void ResumeFileChecks()
        {
            int pid = Environment.ProcessId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var log = new List<string>();
            string path = TempFile();

            Check(RynthLua_Read(path, pid, now, log) == null && log.Count == 0, "no file: nothing, quietly");

            Check(RynthLuaPlugin.WriteResumeFile(path, new[] { "a", "b" }, pid, now, log.Add), "write");
            Check(RynthLua_Read(path, pid, now + 59_000, log) is { Count: 2 }, "same pid, 59 s: kept");
            Check(!System.IO.File.Exists(path), "deleted after reading");

            RynthLuaPlugin.WriteResumeFile(path, new[] { "a" }, pid + 1, now, log.Add);
            Check(RynthLua_Read(path, pid, now, log) == null && !System.IO.File.Exists(path), "(b) another pid (a new client): ignored and deleted");

            RynthLuaPlugin.WriteResumeFile(path, new[] { "a" }, pid, now, log.Add);
            Check(RynthLua_Read(path, pid, now + 61_000, log) == null && !System.IO.File.Exists(path), "61 s old: ignored and deleted");
            RynthLuaPlugin.WriteResumeFile(path, new[] { "a" }, pid, now + 5_000, log.Add);
            Check(RynthLua_Read(path, pid, now, log) == null, "saved in the future: ignored");

            int logged = log.Count;
            System.IO.File.WriteAllText(path, "{not json");
            Check(RynthLua_Read(path, pid, now, log) == null && !System.IO.File.Exists(path) && log.Count == logged + 1, "a bad file: ignored, deleted, one log line");
            System.IO.File.WriteAllText(path, $$"""{"pid": "{{pid}}", "savedAtUtcMs": {{now}}, "scripts": ["a"]}""");
            Check(RynthLua_Read(path, pid, now, log) == null, "pid as a string: ignored");
            System.IO.File.WriteAllText(path, $$"""{"pid": {{pid}}, "savedAtUtcMs": {{now}}, "scripts": "a"}""");
            Check(RynthLua_Read(path, pid, now, log) == null, "scripts not an array: ignored");
            System.IO.File.WriteAllText(path, $$"""{"pid": {{pid}}, "savedAtUtcMs": {{now}}, "scripts": ["a", 5, null, "", " A ", "b"]}""");
            List<string>? got = RynthLua_Read(path, pid, now, log);
            Check(got != null && got.SequenceEqual(new[] { "a", "b" }), "non-strings, blanks and case-insensitive repeats skipped: " + string.Join(",", got ?? new()));

            // A write that fails logs and doesn't throw.
            string blocker = TempFile();
            System.IO.File.WriteAllText(blocker, "a file, not a folder");
            logged = log.Count;
            Check(!RynthLuaPlugin.WriteResumeFile(System.IO.Path.Combine(blocker, "resume.json"), new[] { "a" }, pid, now, log.Add) && log.Count == logged + 1,
                "a failing write returns false with one log line");
        }

        private static List<string>? RynthLua_Read(string path, int pid, long now, List<string> log)
            => RynthLuaPlugin.ReadResumeFile(path, pid, now, log.Add);

        // ── Docs and types (slice 7) ────────────────────────────────────

        private static string RepoPath(params string[] parts)
            => System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(parts).ToArray());

        /// <summary>
        /// Every imgui function is in RYNTHLUA.md's table and declared in the LuaLS types, and
        /// nothing else is; the type files parse; the ImGui enums in the types equal the real ones.
        /// </summary>
        private static void DocsAndTypes()
        {
            var plugin = new RynthLuaPlugin();
            var s = new Script(CoreModules.Preset_SoftSandbox);
            DynValue im = CreateImGuiModule(plugin, s);
            var functions = im.Table.Pairs.Where(p => p.Value.Type == DataType.ClrFunction).Select(p => p.Key.String).OrderBy(n => n).ToList();
            Check(functions.Count >= 40, $"the imgui module has its functions ({functions.Count})");

            string doc = System.IO.File.ReadAllText(RepoPath("Docs", "RYNTHLUA.md"));
            string types = System.IO.File.ReadAllText(RepoPath("Plugins", "RynthCore.Plugin.RynthLua", "Types", "rynthlua.d.lua"));
            var undocumented = functions.Where(f => !doc.Contains("`" + f + "(", StringComparison.Ordinal)).ToList();
            Check(undocumented.Count == 0, "every imgui function is in RYNTHLUA.md: missing " + string.Join(", ", undocumented));
            var untyped = functions.Where(f => !types.Contains("function ImGui." + f + "(", StringComparison.Ordinal)).ToList();
            Check(untyped.Count == 0, "every imgui function is in rynthlua.d.lua: missing " + string.Join(", ", untyped));
            var declared = System.Text.RegularExpressions.Regex.Matches(types, @"function ImGui\.(\w+)\(").Select(m => m.Groups[1].Value).ToList();
            var extra = declared.Where(d => !functions.Contains(d)).ToList();
            Check(extra.Count == 0, "rynthlua.d.lua declares no imgui function the module lacks: " + string.Join(", ", extra));

            // The type files are valid Lua, and running the definitions gives the real enum values.
            var defs = new Script(CoreModules.Preset_SoftSandbox);
            foreach (string f in System.IO.Directory.GetFiles(RepoPath("Plugins", "RynthCore.Plugin.RynthLua", "Types"), "*.lua", System.IO.SearchOption.AllDirectories))
            {
                try { new Script(CoreModules.Preset_SoftSandbox).LoadString(System.IO.File.ReadAllText(f)); Check(true, ""); }
                catch (InterpreterException ex) { Check(false, $"{System.IO.Path.GetFileName(f)} parses: {ex.DecoratedMessage ?? ex.Message}"); }
            }
            try { defs.DoString(types); }
            catch (InterpreterException ex) { Check(false, "rynthlua.d.lua runs: " + (ex.DecoratedMessage ?? ex.Message)); return; }
            var real = new Script(CoreModules.Preset_SoftSandbox);
            LibraryImGuiValues.Register(real);
            foreach (string e in new[] { "ImGuiWindowFlags", "ImGuiCond", "ImGuiCol", "ImGuiTreeNodeFlags", "ImGuiInputTextFlags", "ImGuiSelectableFlags", "ImGuiChildFlags", "ImGuiSliderFlags" })
            {
                Table t = defs.Globals.Get(e).Table, r = real.Globals.Get(e).Table;
                var typed = t.Pairs.ToDictionary(p => p.Key.String, p => p.Value.Number);
                var names = r.Pairs.Where(p => p.Key.Type == DataType.String).Select(p => p.Key.String).ToList();
                bool same = typed.Count == names.Count && typed.All(kv => r.Get(kv.Key).Type == DataType.Number && r.Get(kv.Key).Number == kv.Value);
                Check(same, $"{e}: the types match the real table ({typed.Count} vs {names.Count})");
            }
        }

        private static void Enums()
        {
            var s = new Script(CoreModules.Preset_SoftSandbox);
            LibraryImGuiValues.Register(s);
            Compare<ImGuiWindowFlags>(s, "ImGuiWindowFlags");
            Compare<ImGuiCond>(s, "ImGuiCond");
            Compare<ImGuiCol>(s, "ImGuiCol");
            Compare<ImGuiTreeNodeFlags>(s, "ImGuiTreeNodeFlags");
            Compare<ImGuiInputTextFlags>(s, "ImGuiInputTextFlags");
            Compare<ImGuiSelectableFlags>(s, "ImGuiSelectableFlags");
            Compare<ImGuiChildFlags>(s, "ImGuiChildFlags");
            Compare<ImGuiSliderFlags>(s, "ImGuiSliderFlags");
        }

        /// <summary>
        /// ImGui.NET names left out on purpose: ImGui's internal window flags (set by ImGui itself,
        /// never by callers) and the InputText callback flags (no callback is ever passed).
        /// </summary>
        private static readonly HashSet<string> Excluded = new()
        {
            "ImGuiWindowFlags.ChildWindow", "ImGuiWindowFlags.Tooltip", "ImGuiWindowFlags.Popup", "ImGuiWindowFlags.Modal",
            "ImGuiWindowFlags.ChildMenu", "ImGuiWindowFlags.DockNodeHost",
            "ImGuiInputTextFlags.CallbackCompletion", "ImGuiInputTextFlags.CallbackHistory", "ImGuiInputTextFlags.CallbackAlways",
            "ImGuiInputTextFlags.CallbackCharFilter", "ImGuiInputTextFlags.CallbackResize", "ImGuiInputTextFlags.CallbackEdit",
            "ImGuiSliderFlags.InvalidMask",
        };

        private static void Compare<T>(Script s, string global) where T : struct, Enum
        {
            Table t = s.Globals.Get(global).Table;
            var ours = new Dictionary<string, long>();
            foreach (TablePair p in t.Pairs)
                if (p.Key.Type == DataType.String) ours[p.Key.String] = (long)p.Value.Number;
            foreach (string name in Enum.GetNames<T>())
            {
                if (Excluded.Contains(global + "." + name)) continue;
                long want = Convert.ToInt64(Enum.Parse<T>(name));
                Check(ours.TryGetValue(name, out long got) && got == want, $"{global}.{name}: ours {(ours.TryGetValue(name, out long g2) ? g2.ToString() : "missing")}, ImGui.NET {want}");
            }
            foreach (string name in ours.Keys)
                Check(Enum.TryParse<T>(name, out _), $"{global}.{name} isn't in ImGui.NET");
        }
    }
}
