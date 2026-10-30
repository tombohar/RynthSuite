using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using MoonSharp.Interpreter;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// Script windows (engine API v71), Phase 0: the huds of every running script, their render
/// passes and the round trip with the engine. Each tick, on the pump thread:
///   1. UiPollEvents: clicks and checkbox values go to their hud (applied by its next pass);
///   2. (scripts run: TickScripts);
///   3. render passes for the huds that are due, in their own 1.5 ms slice;
///   4. UiSubmit when any hud's list or properties changed.
/// Lua never runs on AC's thread: OnRender records a display list the engine replays every
/// frame. Design: Docs/RYNTHLUA_WINDOWS_DESIGN.md.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private const int HudsPerScript = 8;
    private const int HudsPerPlugin = 32;
    internal const int DefaultRefreshRate = 20;           // decision 5: passes per second while visible
    internal const int MinRefreshRate = 1, MaxRefreshRate = 60;
    private const int PassInstructionCap = 200_000;       // §6.1
    private const int HudErrorStrikes = 20;
    private const int MaxHudErrorChars = 2000;
    private static readonly long PassSliceTicks = Stopwatch.Frequency * 15 / 10_000;   // 1.5 ms
    private long _lastSlowLogAt;
    private static readonly long StatsEveryTicks = Stopwatch.Frequency * 10;

    private readonly List<ScriptHud> _huds = new();
    private readonly Dictionary<uint, ScriptHud> _hudsByHash = new();
    private readonly HudRecorder _recorder = new();
    private bool _hudSetChanged;
    private bool _hudsEverSubmitted;
    private uint _lastPolledSeq;
    private byte[] _submitBuffer = new byte[4096];
    private byte[] _eventBuffer = new byte[8192];
    private int _hudRotation;
    private int _submitRotation;
    private long _submitRetryAt;
    private long _lastSubmitErrorLog;

    // Engine facts (UiGetInfo), refreshed about once a second.
    private bool _uiAvailable;
    private long _uiInfoAt;
    private int _maxOps = 1500, _maxBytes = 64 * 1024, _maxSubmitBytes = HudFormat.DefaultMaxSubmitBytes;

    // Phase 0 measurements (logged about every 10 s while a hud is visible).
    private long _statsStart;
    private int _statPasses, _statAborts, _statSubmits, _statMaxOps;
    private long _statPassTicks, _statPassMax, _statOps, _statSubmitBytes, _statSubmitTicks;

    // ── Availability ────────────────────────────────────────────────────────

    /// <summary>views.Available: the engine has API v71 and ImGui is on.</summary>
    internal bool UiAvailable
    {
        get
        {
            RefreshUiInfo(force: false);
            return _uiAvailable;
        }
    }

    /// <summary>Refreshes views.Available, the limits and the query metrics from UiGetInfo (at most once a second unless forced).</summary>
    private void RefreshUiInfo(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && _uiInfoAt != 0 && now - _uiInfoAt < Stopwatch.Frequency) return;
        _uiInfoAt = now;
        if (!Host.HasUi) { _uiAvailable = false; return; }
        bool got = Host.TryGetUiInfo(out UiInfoNative info);
        _uiAvailable = got && (info.Flags & 1) != 0;
        // OpLevel was a reserved 0 before 2026-09-30: those engines replay the Phase 1 ops only.
        if (got) _uiOpLevel = Math.Max((int)info.OpLevel, 1);
        if (info.MaxOpsPerWindow > 0) _maxOps = (int)Math.Min(info.MaxOpsPerWindow, 1500);
        if (info.MaxBytesPerWindow > 0) _maxBytes = (int)Math.Min(info.MaxBytesPerWindow, 64 * 1024);
        if (info.MaxBytesPerSubmit > 0) _maxSubmitBytes = (int)Math.Min(info.MaxBytesPerSubmit, (uint)HudFormat.DefaultMaxSubmitBytes);
        // The engine fills the metrics once its ImGui frame has run; until then they are 0.
        if (got && float.IsFinite(info.TextLineHeight) && info.TextLineHeight > 0) CopyUiMetrics(info);
    }

    // UiGetInfo's metrics for the queries (GetTextLineHeight, CalcTextSize, ...), in logical units
    // (pixels / UiScale), the units of widget sizes and geometry. Defaults until the engine reports them.
    private float _uiScale = 1f;
    private float _uiLineHeight = 14f, _uiFrameHeight = 20f;
    private float _uiSpacingX = 8f, _uiSpacingY = 4f, _uiPaddingX = 4f, _uiPaddingY = 3f;
    /// <summary>Default-font advance of ' '..'~', PIXELS at <see cref="_uiScale"/> (CalcTextSize rounds in pixels as ImGui does).</summary>
    private readonly float[] _uiAdvancePx = DefaultAdvances();

    private static float[] DefaultAdvances()
    {
        var a = new float[95];
        Array.Fill(a, 7f);
        return a;
    }

    private unsafe void CopyUiMetrics(UiInfoNative info)
    {
        float scale = float.IsFinite(info.UiScale) && info.UiScale > 0 ? info.UiScale : 1f;
        static float Ok(float v, float fallback) => float.IsFinite(v) && v >= 0 ? v : fallback;
        _uiScale = scale;
        _uiLineHeight = Ok(info.TextLineHeight, 14f * scale) / scale;
        _uiFrameHeight = Ok(info.FrameHeight, 20f * scale) / scale;
        _uiSpacingX = Ok(info.ItemSpacingX, 8f * scale) / scale;
        _uiSpacingY = Ok(info.ItemSpacingY, 4f * scale) / scale;
        _uiPaddingX = Ok(info.FramePaddingX, 4f * scale) / scale;
        _uiPaddingY = Ok(info.FramePaddingY, 3f * scale) / scale;
        for (int i = 0; i < 95; i++) _uiAdvancePx[i] = Ok(info.AsciiAdvance[i], 7f * scale);
    }

    // ── Huds (the views module calls these) ─────────────────────────────────

    internal ScriptHud CreateHud(ScriptContext ctx, string name)
    {
        foreach (ScriptHud h in _huds)
            if (h.Ctx == ctx && h.Name == name && !h.Disposed) return h;   // UB: same name, same hud

        if (_huds.Count(h => h.Ctx == ctx) >= HudsPerScript)
            throw new ScriptRuntimeException($"a script can have at most {HudsPerScript} huds");
        if (_huds.Count >= HudsPerPlugin)
            throw new ScriptRuntimeException($"RynthLua can show at most {HudsPerPlugin} huds at once");

        string key = WindowKey(ctx.Name, name);
        var hud = new ScriptHud(ctx, name, key,
            ctx.Event($"Hud:{name}:OnRender"), ctx.Event($"Hud:{name}:OnPreRender"));
        if (_hudsByHash.ContainsKey(hud.Hash))
            throw new ScriptRuntimeException($"CreateHud(\"{name}\"): the name clashes with another hud; pick another name");
        // The engine's panel key (saved placement, the bar, /rc ui) ignores case and turns
        // control characters into spaces and '=', ',' and '#' into '_'; it refuses a second
        // window on the same panel key, on every submit.
        string panelKey = PanelKeyOf(key);
        foreach (ScriptHud h in _huds)
            if (!h.Disposed && string.Equals(PanelKeyOf(h.Key), panelKey, StringComparison.OrdinalIgnoreCase))
                throw new ScriptRuntimeException(
                    $"CreateHud(\"{name}\"): the name clashes with hud \"{h.Name}\"{(h.Ctx == ctx ? "" : $" of {h.Ctx.Name}")} (hud names ignore case, and '=', ',' and '#' count as '_'); pick another name");

        // Initial visibility (§1.2, decision 9): the player's last choice for this window on
        // this character, else hidden. Before login there is no character, so hidden. The
        // memory applies only here, at CreateHud: the huds of a global script that keeps
        // running across a character change are not re-applied for the new character (the
        // script's own hud.Visible, or the player, decides from there).
        string character = HudCharacter();
        if (character.Length > 0 && HudMemory.TryGet(character, key, out bool remembered) && remembered)
        {
            hud.Visible = true;
            hud.PassNow = true;
            hud.FirstPassSinceShown = true;
        }

        if (!_huds.Any(h => h.Ctx == ctx))
            ctx.OnStop.Add(() => RemoveHudsOf(ctx));
        _huds.Add(hud);
        _hudsByHash[hud.Hash] = hud;
        _hudSetChanged = true;
        if (!UiAvailable && _uiWarned.Add(ctx.Name))
            ctx.Host.Print("script windows need RynthCore API v71 or later with ImGui on; this hud won't show");
        return hud;
    }

    private readonly HashSet<string> _uiWarned = new(StringComparer.OrdinalIgnoreCase);

    // ── Per-character visibility memory (decision 9) ────────────────────────

    private HudVisibilityMemory? _hudMemory;
    /// <summary>Offline tests only: stands in for <see cref="CharacterKey"/> when set.</summary>
    private string? HudCharacterOverride { get; set; }

    private HudVisibilityMemory HudMemory =>
        _hudMemory ??= new HudVisibilityMemory(System.IO.Path.Combine(DataFolder, "hud_visibility.json"), s => Log(s));

    /// <summary>"server|character", or "" before login (nothing is remembered or recalled then).</summary>
    private string HudCharacter()
    {
        if (HudCharacterOverride != null) return HudCharacterOverride;
        try { return CharacterKey(); }
        catch { return string.Empty; }
    }

    /// <summary>OnTick: writes the memory ~2 s after the player's last change.</summary>
    private void TickHudMemory()
    {
        try { _hudMemory?.Tick(Environment.TickCount64); }
        catch { }
    }

    /// <summary>Logout and shutdown: writes pending changes now.</summary>
    private void FlushHudMemory()
    {
        try { _hudMemory?.Flush(); }
        catch { }
    }

    /// <summary>A window key as the engine's panel key sees it (compare case-insensitively).</summary>
    internal static string PanelKeyOf(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key)
            sb.Append(char.IsControl(c) ? ' ' : c is '=' or ',' or '#' ? '_' : c);
        return sb.ToString();
    }

    /// <summary>"RynthLua/&lt;script&gt;/&lt;hud&gt;", at most 128 UTF-8 bytes.</summary>
    private static string WindowKey(string script, string hud)
    {
        string key = $"RynthLua/{script}/{hud}";
        while (Encoding.UTF8.GetByteCount(key) > HudFormat.MaxStr8) key = key[..^1];
        return key;
    }

    /// <summary>
    /// Shows or hides a hud, whoever asked: the script (hud.Visible), the player (the X, the
    /// bar; through <see cref="OnPlayerVisibility"/>) or /lua hud. On an actual change it fires
    /// OnShow / OnHide (queued as script threads, never run inline, so a handler can sleep and
    /// can't re-enter this call). The flag goes to the engine with the next submit.
    /// </summary>
    internal void SetHudVisible(ScriptHud hud, bool visible)
    {
        if (hud.Visible == visible || hud.Disposed) return;
        hud.Visible = visible;
        hud.PropsDirty = true;
        if (visible)
        {
            hud.PassNow = true;
            hud.FirstPassSinceShown = true;
            hud.ErrorStreak = 0;   // hidden and shown again: a hud stopped after 20 errors tries again
        }
        else
        {
            // A hidden hud runs no pass: its clicks and values are dropped, so they neither fire
            // when it comes back nor hold every window's AckSeq back meanwhile.
            hud.DropPendingInput();
        }
        try { hud.Ctx.Fire((visible ? hud.OnShow : hud.OnHide).Key, s => DynValue.NewTable(new Table(s))); }
        catch (Exception ex) { hud.Ctx.Host.Print($"hud {hud.Name}: {(visible ? "OnShow" : "OnHide")}: {ex.Message}"); }
    }

    /// <summary>
    /// The player showed or hid the window (reason 1: the X or /rc ui close; 2: the bar's
    /// Scripts menu or /rc ui open). hud.Visible follows (OnShow/OnHide fire), the choice is
    /// remembered for this character, and the next submit carries the flag with an AckSeq that
    /// covers the event, so the engine takes the flag again (it ignores older ones).
    /// </summary>
    internal void OnPlayerVisibility(ScriptHud hud, bool visible, byte reason)
    {
        if (hud.Disposed) return;
        SetHudVisible(hud, visible);
        hud.PropsDirty = true;   // the next submit's flag agrees with the player even when nothing changed here
        if (reason is VisibilityPlayerClose or VisibilityBarOrCommand) RememberPlayerChoice(hud, visible);
    }

    /// <summary>/lua hud show|hide|toggle: the same as the player doing it (remembered; OnShow/OnHide fire).</summary>
    internal void PlayerSetHudVisible(ScriptHud hud, bool visible)
    {
        if (hud.Disposed) return;
        SetHudVisible(hud, visible);
        RememberPlayerChoice(hud, visible);
    }

    private const byte VisibilityPlayerClose = 1, VisibilityBarOrCommand = 2;   // the engine's reason codes

    private void RememberPlayerChoice(ScriptHud hud, bool visible)
    {
        try { HudMemory.Record(HudCharacter(), hud.Key, visible, Environment.TickCount64); }
        catch { }
    }

    internal void SetHudTitle(ScriptHud hud, string title)
    {
        if (hud.Title == title) return;
        hud.Title = title;
        hud.PropsDirty = true;
        hud.PassNow = true;
    }

    // ImGuiWindowFlags (ImGui 1.91.6) for hud.WindowSettings.
    private const long WsNoTitleBar = 1, WsNoScrollbar = 8, WsNoScrollWithMouse = 16, WsNoCollapse = 32,
        WsNoBackground = 128, WsNoMouseInputs = 512, WsNoNavInputs = 1 << 16, WsNoNavFocus = 1 << 17;
    /// <summary>Accepted without a warning: NoInputs' nav bits (keyboard nav doesn't reach script windows) and NoCollapse (the RynthCore frame never collapses).</summary>
    private const long WsQuiet = WsNoNavInputs | WsNoNavFocus | WsNoCollapse;
    private const long WsHonoured = WsNoTitleBar | WsNoScrollbar | WsNoScrollWithMouse | WsNoBackground | WsNoMouseInputs;
    private static readonly string[] WsNames =
    {
        "NoTitleBar", "NoResize", "NoMove", "NoScrollbar", "NoScrollWithMouse", "NoCollapse", "AlwaysAutoResize",
        "NoBackground", "NoSavedSettings", "NoMouseInputs", "MenuBar", "HorizontalScrollbar", "NoFocusOnAppearing",
        "NoBringToFrontOnFocus", "AlwaysVerticalScrollbar", "AlwaysHorizontalScrollbar", "NoNavInputs", "NoNavFocus",
        "UnsavedDocument", "NoDocking",
    };

    /// <summary>
    /// hud.WindowSettings: the honoured ImGuiWindowFlags map onto the hud's fields (NoTitleBar =
    /// chrome "none", NoInputs/NoMouseInputs = click-through, NoBackground, NoScrollbar,
    /// NoScrollWithMouse). Everything else (AlwaysAutoResize until Phase 2 included) is ignored
    /// with one console warning per hud.
    /// </summary>
    internal void SetHudWindowSettings(ScriptHud hud, long flags)
    {
        hud.WindowSettings = flags;
        hud.ChromeNone = (flags & WsNoTitleBar) != 0;
        hud.ClickThrough = (flags & WsNoMouseInputs) != 0;
        hud.NoBackground = (flags & WsNoBackground) != 0;
        hud.NoScrollbar = (flags & WsNoScrollbar) != 0;
        hud.NoScrollWithMouse = (flags & WsNoScrollWithMouse) != 0;
        hud.PropsDirty = true;
        hud.PassNow = true;

        long ignored = flags & ~(WsHonoured | WsQuiet);
        if (ignored == 0 || hud.WindowSettingsWarned) return;
        hud.WindowSettingsWarned = true;
        var names = new List<string>();
        for (int bit = 0; bit < 63; bit++)
            if ((ignored & (1L << bit)) != 0) names.Add(bit < WsNames.Length ? WsNames[bit] : $"bit {bit}");
        hud.Ctx.Host.Print($"hud {hud.Name}: WindowSettings {string.Join(", ", names)} ignored (script windows honour NoTitleBar, NoScrollbar, NoScrollWithMouse, NoBackground and NoInputs)");
    }

    internal void DisposeHud(ScriptHud hud)
    {
        if (hud.Disposed) return;
        hud.Disposed = true;
        hud.OnRender.Clear();
        hud.OnPreRender.Clear();
        hud.OnShow.Clear();
        hud.OnHide.Clear();
        _huds.Remove(hud);
        _hudsByHash.Remove(hud.Hash);
        _hudSetChanged = true;
    }

    private void RemoveHudsOf(ScriptContext ctx)
    {
        foreach (ScriptHud h in _huds.Where(h => h.Ctx == ctx).ToList())
            DisposeHud(h);
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    /// <summary>Step 1: events from the engine to their huds.</summary>
    private void PollHudEvents()
    {
        if (_huds.Count == 0 && !_hudsEverSubmitted) return;
        RefreshUiInfo(force: false);
        if (!Host.HasUi) return;

        for (int round = 0; round < 8; round++)
        {
            int n = Host.UiPollEvents(_eventBuffer, out int remaining);
            if (n > 0) ApplyHudEvents(_eventBuffer.AsSpan(0, n));
            if (remaining == 0) break;
            if (n == 0) Array.Resize(ref _eventBuffer, _eventBuffer.Length * 2);   // one event didn't fit
        }
    }

    private void ApplyHudEvents(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        while (data.Length - pos >= 16)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            if (size < 16 || size > data.Length - pos) break;
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            uint window = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 8)..]);
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 12)..]);
            ReadOnlySpan<byte> p = data.Slice(pos + 16, size - 16);
            pos += size;
            if (seq > _lastPolledSeq) _lastPolledSeq = seq;

            _hudsByHash.TryGetValue(window, out ScriptHud? hud);
            switch (type)
            {
                case HudFormat.EvClicked when hud != null && p.Length >= 2:
                    hud.Clicks[key] = (hud.Clicks.TryGetValue(key, out int c) ? c : 0) + BinaryPrimitives.ReadUInt16LittleEndian(p);
                    hud.NoteUnapplied(seq);
                    break;
                case HudFormat.EvBool when hud != null && p.Length >= 1:
                    hud.Bools[key] = p[0] != 0;
                    hud.NoteUnapplied(seq);
                    break;
                case HudFormat.EvInt when hud != null && p.Length >= 4:
                    hud.Ints[key] = BinaryPrimitives.ReadInt32LittleEndian(p);
                    hud.NoteUnapplied(seq);
                    break;
                case HudFormat.EvFloat when hud != null && p.Length >= 4:
                {
                    float f = BinaryPrimitives.ReadSingleLittleEndian(p);
                    if (!float.IsFinite(f)) break;
                    hud.Floats[key] = f;
                    hud.NoteUnapplied(seq);
                    break;
                }
                case HudFormat.EvText when hud != null && p.Length >= 3:
                {
                    bool submitted = p[0] != 0;
                    int n = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(p[1..]), p.Length - 3);
                    string text = Encoding.UTF8.GetString(p.Slice(3, n));
                    // "submitted" stays set until a pass takes it, as the engine's queue does.
                    if (hud.Texts.TryGetValue(key, out var old) && old.Submitted) submitted = true;
                    hud.Texts[key] = (text, submitted);
                    hud.NoteUnapplied(seq);
                    break;
                }
                case HudFormat.EvOpen when hud != null && p.Length >= 1:
                    hud.OpenStates[key] = p[0] != 0;
                    hud.NoteUnapplied(seq);
                    break;
                case HudFormat.EvGeometry when hud != null && p.Length >= 36:
                {
                    // Feeds GetWindowSize and friends; no pass, no AckSeq hold.
                    uint gf = BinaryPrimitives.ReadUInt32LittleEndian(p[32..]);
                    hud.Geometry = new HudGeometry
                    {
                        WindowPos = ReadVec2(p, 0),
                        WindowSize = ReadVec2(p, 8),
                        ContentAvail = ReadVec2(p, 16),
                        CursorStart = ReadVec2(p, 24),
                        Focused = (gf & HudFormat.GeoFocused) != 0,
                        Hovered = (gf & HudFormat.GeoHovered) != 0,
                        Known = true,
                    };
                    break;
                }
                case HudFormat.EvVisibility when hud != null && p.Length >= 1:
                    OnPlayerVisibility(hud, p[0] != 0, p.Length >= 2 ? p[1] : (byte)0);
                    break;
                case HudFormat.EvError when p.Length >= 2:
                {
                    int n = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(p), p.Length - 2);
                    string text = Encoding.UTF8.GetString(p.Slice(2, n));
                    if (hud != null) hud.Ctx.Host.Print($"hud {hud.Name}: {text}");
                    else Log("[RynthLua] script windows: " + text);
                    break;
                }
            }
        }
    }

    private static System.Numerics.Vector2 ReadVec2(ReadOnlySpan<byte> p, int at)
    {
        float x = BinaryPrimitives.ReadSingleLittleEndian(p[at..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(p[(at + 4)..]);
        return new System.Numerics.Vector2(float.IsFinite(x) ? x : 0f, float.IsFinite(y) ? y : 0f);
    }

    /// <summary>Step 3: render passes for the huds that are due, within the pass slice.</summary>
    private void RunHudPasses()
    {
        if (_huds.Count == 0) return;
        RefreshUiInfo(force: false);
        if (!_uiAvailable) return;

        long now = Stopwatch.GetTimestamp();
        long deadline = now + PassSliceTicks;
        int n = _huds.Count;
        int start = _hudRotation++ % n;
        // §2.3: first the huds with a pass due now whatever their rate (input arrived, just
        // shown, a property changed); then the ones whose RefreshRate interval has passed,
        // round-robin. When the 1.5 ms slice runs out the rest wait for the next tick.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = 0; k < n; k++)
            {
                if (Stopwatch.GetTimestamp() >= deadline) return;
                if (k >= _huds.Count) break;
                ScriptHud hud = _huds[(start + k) % _huds.Count];
                if (!PassDue(hud, pass == 0, now)) continue;
                long interval = PassInterval(hud);
                // Interval passes keep their cadence (a pump tick landing just after the due
                // time doesn't push every later pass back); a pass out of turn starts a new one.
                hud.NextPassAt = !hud.PassNow && now - hud.NextPassAt < interval ? hud.NextPassAt + interval : now + interval;
                hud.PassNow = false;
                RunPass(hud);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="hud"/> runs a pass this tick. <paramref name="urgent"/>: only
    /// passes due whatever the rate (PassNow); otherwise only interval passes. Hidden huds,
    /// huds of scripts that aren't loaded and huds stopped after 20 errors run none.
    /// </summary>
    private static bool PassDue(ScriptHud hud, bool urgent, long now)
    {
        if (hud.Disposed || !hud.Visible || !hud.Ctx.Host.Loaded) return false;
        if (hud.ErrorStreak >= HudErrorStrikes) return false;
        return urgent ? hud.PassNow : now >= hud.NextPassAt;
    }

    /// <summary>Stopwatch ticks between interval passes: hud.RefreshRate (1..60) per second.</summary>
    private static long PassInterval(ScriptHud hud) =>
        Stopwatch.Frequency / Math.Clamp(hud.RefreshRate, MinRefreshRate, MaxRefreshRate);

    private const string PassRunnerKey = "rynth.hudpass";

    /// <summary>One render pass: OnPreRender + OnRender in one coroutine under the instruction cap.</summary>
    private void RunPass(ScriptHud hud)
    {
        Script? s = hud.Ctx.Host.Lua;
        if (s == null) return;
        DynValue runner = s.Registry.Get(PassRunnerKey);
        if (runner.Type != DataType.Function)
        {
            runner = s.LoadString("local list = ... for i = 1, #list do list[i]() end", null, "hud pass");
            s.Registry.Set(PassRunnerKey, runner);
        }

        long t0 = Stopwatch.GetTimestamp();
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);

        // SetNextWindowSize/Pos are per-pass requests: OnPreRender sets them again every pass.
        // A failed pass keeps the last good pass's requests (as it keeps its list).
        var savedRequestSize = hud.RequestSize;
        var savedRequestPos = hud.RequestPos;
        bool savedPosIsDefault = hud.PosIsDefault;
        hud.RequestSize = HudFormat.NoRequest;
        hud.RequestPos = HudFormat.NoRequest;
        hud.PosIsDefault = false;

        var list = new Table(s);
        foreach (DynValue fn in hud.OnPreRender.TakeForCall()) list.Append(fn);
        foreach (DynValue fn in hud.OnRender.TakeForCall()) list.Append(fn);

        _recorder.Begin(hud, _maxOps, _maxBytes);
        string? error = null;
        try
        {
            DynValue co = s.CreateCoroutine(runner);
            co.Coroutine.AutoYieldCounter = PassInstructionCap;
            DynValue r = co.Coroutine.Resume(DynValue.NewTable(list));
            if (co.Coroutine.State != CoroutineState.Dead)
            {
                error = r.Type == DataType.YieldRequest
                    ? $"OnRender ran too long (over {PassInstructionCap} instructions); the last good frame stays"
                    : "sleep/await can't be used in OnRender; use spawn";
                if (r.Type == DataType.YieldRequest) _statAborts++;
            }
        }
        catch (InterpreterException ex) { error = ex.DecoratedMessage ?? ex.Message; }
        catch (Exception ex) { error = ex.Message; }

        if (error != null)
        {
            // An error can quote a huge string (error(string.rep(...))): keep what the window,
            // the console and hud.LastError can use, not megabytes per failed pass.
            if (error.Length > MaxHudErrorChars) error = error[..MaxHudErrorChars] + "...";
            _recorder.Abort();
            hud.ConsumedInput = false;
            hud.RequestSize = savedRequestSize;
            hud.RequestPos = savedRequestPos;
            hud.PosIsDefault = savedPosIsDefault;
            if (hud.LastError != error) hud.PropsDirty = true;   // the window shows the error above the last good list
            hud.LastError = error;
            hud.ErrorStreak++;
            long nowMs = Environment.TickCount64;
            if (nowMs - hud.LastErrorPrintAt >= 5000)
            {
                hud.LastErrorPrintAt = nowMs;
                hud.Ctx.Host.Print($"hud {hud.Name}: {error}");
            }
            if (hud.ErrorStreak == HudErrorStrikes)
            {
                hud.Ctx.Host.Print($"hud {hud.Name} stopped drawing after {HudErrorStrikes} errors in a row; hide and show it (or restart the script) to try again");
                hud.DropPendingInput();   // no pass will take it; don't hold the AckSeq for it
            }
        }
        else
        {
            _recorder.End();
            hud.ErrorStreak = 0;
            if (hud.LastError != null) hud.PropsDirty = true;
            hud.LastError = null;
            hud.FirstPassSinceShown = false;
            hud.EverPassed = true;
            // Results the pass didn't draw a widget for are stale (the widget is gone).
            foreach (uint key in hud.Clicks.Keys.ToList())
                if (!hud.SeenKeys.Contains(key) || hud.Clicks[key] <= 0) hud.Clicks.Remove(key);
            hud.Bools.Clear();
            hud.Ints.Clear();
            hud.Floats.Clear();
            hud.Texts.Clear();
            PruneOpenStates(hud);
            if (hud.Clicks.Count == 0) hud.FirstUnappliedSeq = 0;
            else hud.PassNow = true;   // another click is waiting: one per pass

            // Submit on change; a pass that consumed input is always sent, so the engine
            // sees its AckSeq move even when the picture didn't change.
            ReadOnlySpan<byte> recorded = hud.Scratch.AsSpan(0, hud.ScratchLength);
            if (hud.ConsumedInput || !recorded.SequenceEqual(hud.Body.AsSpan(0, hud.BodyLength)))
            {
                (hud.Body, hud.Scratch) = (hud.Scratch, hud.Body);
                hud.BodyLength = hud.ScratchLength;
                hud.ListSeq++;
                hud.BodyDirty = true;
            }
            hud.ConsumedInput = false;
            _statMaxOps = Math.Max(_statMaxOps, hud.OpCount);
            _statOps += hud.OpCount;
        }

        long ticks = Stopwatch.GetTimestamp() - t0;
        // Phase 0 diagnostics: what a slow pass (over 10 ms) was spent on.
        if (ticks > Stopwatch.Frequency / 100 && Environment.TickCount64 - _lastSlowLogAt > 2000)
        {
            _lastSlowLogAt = Environment.TickCount64;
            Log($"[RynthLua] slow hud pass: {ticks * 1000.0 / Stopwatch.Frequency:F1} ms, hud {hud.Name}, ops {hud.OpCount}, " +
                $"GCs during it gen0 {GC.CollectionCount(0) - gc0} gen1 {GC.CollectionCount(1) - gc1} gen2 {GC.CollectionCount(2) - gc2}, " +
                $"heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        }
        _statPasses++;
        _statPassTicks += ticks;
        _statPassMax = Math.Max(_statPassMax, ticks);
    }

    /// <summary>A widget's pending click (one per pass while the count is above zero).</summary>
    internal bool TakeClick(ScriptHud hud, uint key)
    {
        if (!hud.Clicks.TryGetValue(key, out int count) || count <= 0) return false;
        hud.Clicks[key] = count - 1;
        hud.ConsumedInput = true;
        return true;
    }

    /// <summary>A widget's pending new bool value, if the player changed it.</summary>
    internal bool TakeBool(ScriptHud hud, uint key, out bool value)
    {
        if (!hud.Bools.Remove(key, out value)) return false;
        hud.ConsumedInput = true;
        return true;
    }

    /// <summary>A SliderInt's or Combo's pending new value (Combo: the 0-based index).</summary>
    internal bool TakeInt(ScriptHud hud, uint key, out int value)
    {
        if (!hud.Ints.Remove(key, out value)) return false;
        hud.ConsumedInput = true;
        return true;
    }

    /// <summary>A SliderFloat's pending new value.</summary>
    internal bool TakeFloat(ScriptHud hud, uint key, out float value)
    {
        if (!hud.Floats.Remove(key, out value)) return false;
        hud.ConsumedInput = true;
        return true;
    }

    /// <summary>An InputText's pending new text; <paramref name="submitted"/>: Enter was pressed (EnterReturnsTrue).</summary>
    internal bool TakeText(ScriptHud hud, uint key, out string text, out bool submitted)
    {
        if (!hud.Texts.Remove(key, out var t))
        {
            text = string.Empty;
            submitted = false;
            return false;
        }
        text = t.Text;
        submitted = t.Submitted;
        hud.ConsumedInput = true;
        return true;
    }

    /// <summary>A CollapsingHeader's or TreeNode's open state: what the player last set, else the default.</summary>
    internal static bool GetOpenState(ScriptHud hud, uint key, bool defaultOpen) =>
        hud.OpenStates.TryGetValue(key, out bool open) ? open : defaultOpen;

    private readonly List<uint> _pruneKeys = new();

    /// <summary>Drops the open states of headers and trees the pass didn't draw.</summary>
    private void PruneOpenStates(ScriptHud hud)
    {
        if (hud.OpenStates.Count == 0) return;
        _pruneKeys.Clear();
        foreach (uint key in hud.OpenStates.Keys)
            if (!hud.SeenKeys.Contains(key)) _pruneKeys.Add(key);
        foreach (uint key in _pruneKeys) hud.OpenStates.Remove(key);
    }

    /// <summary>Step 4: the complete window set, bodies only for the huds whose list changed.</summary>
    private void SubmitHuds(bool force = false)
    {
        if (!Host.HasUi) return;
        bool changed = _hudSetChanged || force;
        foreach (ScriptHud h in _huds)
        {
            // Any header field that differs from what the engine last accepted (properties,
            // per-pass requests, the error line) needs a submit.
            if (!h.PropsDirty && (!h.HeaderSubmitted || !h.Header().SameAs(h.SubmittedHeader))) h.PropsDirty = true;
            if (h.BodyDirty || h.PropsDirty) changed = true;
        }
        if (!changed) return;
        if (_huds.Count == 0 && !_hudsEverSubmitted) { _hudSetChanged = false; return; }
        long now = Stopwatch.GetTimestamp();
        if (!force && now < _submitRetryAt) return;

        // AckSeq: every event up to here has been applied, except in huds still waiting for a
        // pass. A hud that can't run one (hidden, stopped, script not loaded) holds nothing back:
        // the engine ignores every window's Visible flag while the AckSeq is below its last
        // player visibility change, so a stuck hold would freeze them all.
        uint ack = _lastPolledSeq;
        foreach (ScriptHud h in _huds)
            if (h.FirstUnappliedSeq != 0 && h.Visible && h.ErrorStreak < HudErrorStrikes && h.Ctx.Host.Loaded)
                ack = Math.Min(ack, h.FirstUnappliedSeq - 1);

        // Bodies that don't fit in the engine's per-submit limit wait for a later tick (still
        // dirty); the start rotates so a busy hud can't keep the others waiting.
        int count = Math.Min(_huds.Count, HudsPerPlugin);
        int pos = HudFormat.WriteSubmit(ref _submitBuffer, _huds, count, ack, _maxSubmitBytes, _submitRotation++);

        long t0 = Stopwatch.GetTimestamp();
        int rc = Host.UiSubmit(_submitBuffer.AsSpan(0, pos));
        _statSubmitTicks += Stopwatch.GetTimestamp() - t0;
        if (rc == 0)
        {
            foreach (ScriptHud h in _huds)
            {
                if (h.BodyInSubmit) h.BodyDirty = false;
                h.PropsDirty = false;
                h.SubmittedHeader = h.Header();
                h.HeaderSubmitted = true;
            }
            _hudSetChanged = false;
            _hudsEverSubmitted = _huds.Count > 0;
            _statSubmits++;
            _statSubmitBytes += pos;
        }
        else
        {
            // Nothing was applied; the engine keeps the previous lists. Try again in a second.
            _submitRetryAt = now + Stopwatch.Frequency;
            if (Environment.TickCount64 - _lastSubmitErrorLog > 10_000)
            {
                _lastSubmitErrorLog = Environment.TickCount64;
                Log($"[RynthLua] script windows: the engine refused the window list ({rc}); see the scripts' consoles");
            }
        }
    }

    /// <summary>Steps 3 and 4 plus the measurement log; after the scripts ran.</summary>
    private void TickHuds()
    {
        RunHudPasses();
        SubmitHuds();
        LogHudStats();
    }

    /// <summary>Shutdown: the windows close with RynthLua (the engine drops them too).</summary>
    private void ShutdownHuds()
    {
        foreach (ScriptHud h in _huds.ToList()) DisposeHud(h);
        try { SubmitHuds(force: true); } catch { }
    }

    private void LogHudStats()
    {
        long now = Stopwatch.GetTimestamp();
        if (_statsStart == 0) _statsStart = now;
        if (now - _statsStart < StatsEveryTicks) return;
        double seconds = (now - _statsStart) / (double)Stopwatch.Frequency;
        _statsStart = now;
        if (_statPasses > 0 || _statSubmits > 0)
        {
            double us(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
            int good = Math.Max(1, _statPasses - _statAborts);
            string line = string.Create(CultureInfo.InvariantCulture,
                $"[RynthLua] hud passes: {seconds:0.0}s passes={_statPasses} huds={_huds.Count(h => h.Visible)} "
                + $"us/pass avg={us(_statPassTicks) / Math.Max(1, _statPasses):0} max={us(_statPassMax):0} "
                + $"ops/pass avg={_statOps / (double)good:0} max={_statMaxOps} us/op={(_statOps > 0 ? us(_statPassTicks) / _statOps : 0):0.00} "
                + $"aborted={_statAborts} | submits={_statSubmits} bytes/submit={(_statSubmits > 0 ? _statSubmitBytes / _statSubmits : 0)} "
                + $"us/submit={(_statSubmits > 0 ? us(_statSubmitTicks) / _statSubmits : 0):0}");
            Log(line);
        }
        _statPasses = _statAborts = _statSubmits = _statMaxOps = 0;
        _statPassTicks = _statPassMax = _statOps = _statSubmitBytes = _statSubmitTicks = 0;
    }

    // ── /lua hud ────────────────────────────────────────────────────────────

    private const string BenchScript = "hud-bench";

    private void HandleHudCommand(string rest)
    {
        string[] p = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = p.Length > 0 ? p[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "bench":
            {
                string arg = p.Length > 1 ? p[1].Trim() : "";
                if (arg.Equals("off", StringComparison.OrdinalIgnoreCase) || arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (StopScript(BenchScript)) ChatLine("Bench stopped.");
                    else ChatFail("The bench isn't running.");
                    break;
                }
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                {
                    ChatFail("Usage: /lua hud bench <widgets 1-1500> | /lua hud bench off");
                    break;
                }
                n = Math.Min(n, 1500);
                if (!UiAvailable) { ChatFail("Script windows need RynthCore API v71 with ImGui on."); break; }
                RunCode(BenchScript, BenchCode(n));
                ChatLine($"Bench: a hud with {n} widgets. Measurements go to the log every ~10 s ('hud passes:' and 'ScriptWindows replay:'). /lua hud bench off to stop.");
                break;
            }
            case "list":
                ChatLine(HudListLine());
                break;
            case "show":
            case "hide":
            case "toggle":
            {
                string name = p.Length > 1 ? p[1].Trim() : "";
                ScriptHud? hud = FindHud(name, out string? problem);
                if (hud == null)
                {
                    ChatFail((problem ?? $"No hud named '{name}'.") + " " + HudListLine());
                    break;
                }
                bool show = sub == "show" || sub == "toggle" && !hud.Visible;
                bool changed = hud.Visible != show;
                PlayerSetHudVisible(hud, show);
                ChatLine($"{hud.Ctx.Name}/{hud.Name} {(show ? "shown" : "hidden")}{(changed ? "" : " (it already was)")}.");
                break;
            }
            default:
                if (sub is "" or "help") ChatLine("/lua hud list | show|hide|toggle <script>/<hud> | bench <widgets 1-1500> | bench off");
                else ChatFail($"Unknown '/lua hud {sub}'. /lua hud list | show|hide|toggle <script>/<hud> | bench <widgets 1-1500> | bench off");
                break;
        }
    }

    private string HudListLine()
    {
        if (_huds.Count == 0) return "No huds.";
        return $"Huds ({_huds.Count}): " + string.Join(", ", _huds.Select(h =>
            $"{h.Ctx.Name}/{h.Name}{(h.Visible ? " [shown]" : "")}{(h.LastError != null ? " [error]" : "")}{(h.ShowInBar ? "" : " [not in bar]")}"));
    }

    /// <summary>
    /// A hud by "script/hud" (case-insensitive), or by the hud's name alone when only one hud
    /// has it. Null with <paramref name="problem"/> set when the name is missing or ambiguous.
    /// </summary>
    internal ScriptHud? FindHud(string name, out string? problem)
    {
        problem = null;
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) { problem = "Which hud? Use <script>/<hud>."; return null; }
        foreach (ScriptHud h in _huds)
            if (!h.Disposed && string.Equals($"{h.Ctx.Name}/{h.Name}", name, StringComparison.OrdinalIgnoreCase)) return h;
        ScriptHud[] byName = _huds.Where(h => !h.Disposed && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byName.Length == 1) return byName[0];
        if (byName.Length > 1) problem = $"More than one hud is called '{name}'; use <script>/<hud>.";
        return null;
    }

    /// <summary>N widgets: a counter line, then Text / Button / Checkbox in turn.</summary>
    private static string BenchCode(int n) => $$"""
local views = require("views")
local ImGui = require("imgui")
local N = {{n}}
local hud = views.Huds.CreateHud("Bench")
hud.Title = "Bench (" .. N .. " widgets)"
hud.Visible = true
local checks, clicks = {}, 0
hud.OnRender.Add(function()
  ImGui.Text("clicks: " .. clicks .. "   widgets: " .. N)
  for i = 2, N do
    local m = i % 3
    if m == 0 then
      ImGui.Text("Text line " .. i)
    elseif m == 1 then
      if ImGui.Button("Button " .. i) then clicks = clicks + 1 end
    else
      local _, v = ImGui.Checkbox("Check " .. i, checks[i] or false)
      checks[i] = v
    end
  end
end)
print("bench hud with " .. N .. " widgets; /lua hud bench off to stop")
""";
}
