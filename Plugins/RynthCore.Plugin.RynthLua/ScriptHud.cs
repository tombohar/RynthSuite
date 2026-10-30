using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// One script window (a "hud", UB's name): its properties, its render events, the display
/// list its last good render pass recorded, and the input results waiting for the next pass.
/// Pump thread only. Design: Docs/RYNTHLUA_WINDOWS_DESIGN.md §1.2, §2, §4.2.
/// </summary>
internal sealed class ScriptHud
{
    public readonly ScriptContext Ctx;
    public readonly string Name;
    /// <summary>"RynthLua/&lt;script&gt;/&lt;hud&gt;": the engine's window key (saved placement).</summary>
    public readonly string Key;
    public readonly byte[] KeyUtf8;
    /// <summary>FNV-1a of <see cref="KeyUtf8"/>: events carry it.</summary>
    public readonly uint Hash;
    public readonly ScriptEvent OnRender;
    public readonly ScriptEvent OnPreRender;
    /// <summary>Fired (queued as script threads) whenever Visible actually changes, whoever changed it.</summary>
    public readonly ScriptEvent OnShow;
    public readonly ScriptEvent OnHide;

    public string Title;
    public bool Visible;
    public bool Disposed;
    public string? LastError;
    /// <summary>The hud's Lua table (views module), made on first use.</summary>
    public DynValue? LuaObject;

    // Window properties (the header of every submit; later slices set them).
    public bool ShowInBar = true;
    public bool ChromeNone = false;
    public bool ClickThrough = false;
    public bool NoBackground = false;
    public bool NoScrollbar = false;
    public bool NoScrollWithMouse = false;
    /// <summary>Panel logical units; 0 = the engine's default. Persistent.</summary>
    public Vector2 DefaultSize = Vector2.Zero;
    public Vector2 MinSize = Vector2.Zero;
    /// <summary>hud.WindowSettings as the script last set it (ImGuiWindowFlags); the honoured bits are mapped onto the fields above.</summary>
    public long WindowSettings;
    /// <summary>The "flags ignored" warning was printed (once per hud).</summary>
    public bool WindowSettingsWarned;
    /// <summary>Per-pass requests (SetNextWindow*), NaN = none; reset at the start of every pass.</summary>
    public Vector2 RequestSize = HudFormat.NoRequest;
    public Vector2 RequestPos = HudFormat.NoRequest;
    /// <summary>RequestPos is a FirstUseEver default position (used only when nothing is saved).</summary>
    public bool PosIsDefault;
    /// <summary>The header last accepted by the engine (PropsDirty when the current one differs).</summary>
    public HudHeader SubmittedHeader;
    public bool HeaderSubmitted;

    // Scheduling
    public long NextPassAt;
    /// <summary>A pass is due on the next tick whatever the rate: input arrived, or just shown.</summary>
    public bool PassNow;
    public int ErrorStreak;
    public long LastErrorPrintAt;
    /// <summary>Set when the hud becomes visible; cleared after its next successful pass.</summary>
    public bool FirstPassSinceShown;
    /// <summary>At least one pass has succeeded.</summary>
    public bool EverPassed;
    /// <summary>Passes per second while visible (the views module sets it; 20 = decision 5).</summary>
    public int RefreshRate = 20;

    // The last good list (Body) and whether the engine needs it (again).
    public byte[] Body = new byte[1024];
    public int BodyLength;
    public byte[] Scratch = new byte[1024];
    public int ScratchLength;
    public int OpCount;
    public uint ListSeq;
    public bool BodyDirty = true;
    /// <summary>The last WriteSubmit carried this hud's body (false: sent "unchanged", it goes in a later submit).</summary>
    public bool BodyInSubmit;
    public bool PropsDirty = true;

    // Results from the engine, applied by the next pass.
    public readonly Dictionary<uint, int> Clicks = new();
    public readonly Dictionary<uint, bool> Bools = new();
    public readonly Dictionary<uint, int> Ints = new();
    public readonly Dictionary<uint, float> Floats = new();
    public readonly Dictionary<uint, (string Text, bool Submitted)> Texts = new();
    /// <summary>CollapsingHeader / TreeNode open states the player set. Persistent (pruned to the keys a pass drew).</summary>
    public readonly Dictionary<uint, bool> OpenStates = new();
    /// <summary>The window's last reported geometry (Geometry events).</summary>
    public HudGeometry Geometry;
    /// <summary>The first event seq not yet reflected in a recorded list (0 = none).</summary>
    public uint FirstUnappliedSeq;
    public bool ConsumedInput;
    public readonly HashSet<uint> SeenKeys = new();
    public bool DuplicateWarned;
    /// <summary>The "left N scope(s) open" warning was printed (once per hud).</summary>
    public bool ScopeWarned;

    public ScriptHud(ScriptContext ctx, string name, string key, ScriptEvent onRender, ScriptEvent onPreRender)
    {
        Ctx = ctx;
        Name = name;
        Key = key;
        KeyUtf8 = Encoding.UTF8.GetBytes(key);
        Hash = HudFormat.Fnv1a(KeyUtf8);
        Title = name;
        OnRender = onRender;
        OnPreRender = onPreRender;
        OnShow = ctx.Event($"Hud:{name}:OnShow");
        OnHide = ctx.Event($"Hud:{name}:OnHide");
    }

    /// <summary>
    /// An event the next pass applies. The lowest seq is kept: the engine's queue isn't in seq
    /// order (a coalesced click takes a new seq where it sits), and the AckSeq must stay below
    /// every event a pass hasn't taken yet.
    /// </summary>
    public void NoteUnapplied(uint seq)
    {
        if (FirstUnappliedSeq == 0 || seq < FirstUnappliedSeq) FirstUnappliedSeq = seq;
        PassNow = true;
    }

    /// <summary>Drops the transient results (clicks and values) and releases the AckSeq hold. Open states stay.</summary>
    public void DropPendingInput()
    {
        Clicks.Clear();
        Bools.Clear();
        Ints.Clear();
        Floats.Clear();
        Texts.Clear();
        FirstUnappliedSeq = 0;
        ConsumedInput = false;
    }

    /// <summary>The window flags this hud's header carries now.</summary>
    public uint HeaderFlags()
    {
        uint f = 0;
        if (Visible) f |= HudFormat.FlagVisible;
        if (ShowInBar) f |= HudFormat.FlagShowInBar;
        if (ChromeNone) f |= HudFormat.FlagChromeNone;
        if (ClickThrough) f |= HudFormat.FlagClickThrough;
        if (NoBackground) f |= HudFormat.FlagNoBackground;
        if (NoScrollbar) f |= HudFormat.FlagNoScrollbar;
        if (NoScrollWithMouse) f |= HudFormat.FlagNoScrollWithMouse;
        if (LastError != null) f |= HudFormat.FlagHasError;
        if (PosIsDefault && !HudFormat.IsNone(RequestPos)) f |= HudFormat.FlagPosIsDefault;
        return f;
    }

    public HudHeader Header() => new(Title, HeaderFlags(), DefaultSize, MinSize, RequestSize, RequestPos, LastError);
}

/// <summary>The window's geometry from the engine: logical units at UI scale 1, measured in the panel's body region.</summary>
internal struct HudGeometry
{
    public Vector2 WindowPos;
    public Vector2 WindowSize;
    public Vector2 ContentAvail;
    public Vector2 CursorStart;
    public bool Focused;
    public bool Hovered;
    /// <summary>At least one Geometry event arrived.</summary>
    public bool Known;
}

/// <summary>A snapshot of the fields a submit's window header carries (for change detection).</summary>
internal readonly struct HudHeader
{
    public readonly string Title;
    public readonly uint Flags;
    public readonly Vector2 DefaultSize, MinSize, RequestSize, RequestPos;
    public readonly string? Error;

    public HudHeader(string title, uint flags, Vector2 defaultSize, Vector2 minSize, Vector2 requestSize, Vector2 requestPos, string? error)
    {
        Title = title;
        Flags = flags;
        DefaultSize = defaultSize;
        MinSize = minSize;
        RequestSize = requestSize;
        RequestPos = requestPos;
        Error = error;
    }

    public bool SameAs(in HudHeader o) =>
        Flags == o.Flags && string.Equals(Title, o.Title, StringComparison.Ordinal)
        && string.Equals(Error, o.Error, StringComparison.Ordinal)
        && Same(DefaultSize, o.DefaultSize) && Same(MinSize, o.MinSize)
        && Same(RequestSize, o.RequestSize) && Same(RequestPos, o.RequestPos);

    /// <summary>Bitwise, so NaN (= none) equals NaN.</summary>
    private static bool Same(Vector2 a, Vector2 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y);
}

/// <summary>
/// Display-list format 1 (Docs/RYNTHLUA_WINDOWS_DESIGN.md §4.2): the byte layout both sides
/// agree on. Little-endian. The engine's DisplayListParser.cs is the source of truth.
/// </summary>
internal static class HudFormat
{
    public const uint Magic = 0x31575352;          // 'RSW1'
    public const ushort FormatVersion = 1;
    /// <summary>
    /// The op level this recorder writes (UiGetInfo OpLevel): 2 adds Image, ImageButton, InputInt,
    /// InputFloat, DragInt and DragFloat within format 1. An engine reporting less (0 on engines
    /// before 2026-09-30) would skip those ops, so the imgui functions refuse to record them.
    /// </summary>
    public const ushort OpLevel = 2;
    public const uint BodyUnchanged = 0xFFFFFFFF;
    public const int MaxString = 4096;
    public const int MaxStr8 = 128;
    public const int MaxErrorText = 1024;
    public const int MaxComboItems = 512;
    public const int MaxPayload = ushort.MaxValue;
    public const byte OpHasBlock = 1;

    public static readonly Vector2 NoRequest = new(float.NaN, float.NaN);

    public static bool IsNone(Vector2 v) => float.IsNaN(v.X) || float.IsNaN(v.Y);

    // Ops
    public const byte OpEnd = 0x00;
    public const byte OpText = 0x01;
    public const byte OpTextColored = 0x02;
    public const byte OpTextWrapped = 0x03;
    public const byte OpTextDisabled = 0x04;
    public const byte OpBulletText = 0x05;
    public const byte OpSeparatorText = 0x06;
    public const byte OpLabelText = 0x07;
    public const byte OpSeparator = 0x10;
    public const byte OpSameLine = 0x11;
    public const byte OpNewLine = 0x12;
    public const byte OpSpacing = 0x13;
    public const byte OpDummy = 0x14;
    public const byte OpIndent = 0x15;
    public const byte OpUnindent = 0x16;
    public const byte OpButton = 0x20;
    public const byte OpCheckbox = 0x21;
    public const byte OpSliderInt = 0x22;
    public const byte OpSliderFloat = 0x23;
    public const byte OpInputText = 0x24;
    public const byte OpCombo = 0x25;
    public const byte OpSelectable = 0x26;
    public const byte OpProgressBar = 0x27;
    public const byte OpImage = 0x28;          // op level 2
    public const byte OpImageButton = 0x29;
    public const byte OpInputInt = 0x2A;
    public const byte OpInputFloat = 0x2B;
    public const byte OpDragInt = 0x2C;
    public const byte OpDragFloat = 0x2D;

    // Image / ImageButton icon kinds (the engine's ScriptIconKind)
    public const byte IconKindIcon = 0;
    public const byte IconKindObject = 1;
    public const byte IconKindSpell = 2;
    public const byte OpPushID = 0x30;
    public const byte OpPopID = 0x31;
    public const byte OpPushStyleColor = 0x32;
    public const byte OpPopStyleColor = 0x33;
    public const byte OpSetItemTooltip = 0x38;
    public const byte OpChild = 0x40;
    public const byte OpCollapsingHeader = 0x41;
    public const byte OpTreeNode = 0x42;

    // Window flags
    public const uint FlagVisible = 1u << 0;
    public const uint FlagShowInBar = 1u << 1;
    public const uint FlagChromeNone = 1u << 2;
    public const uint FlagClickThrough = 1u << 3;
    public const uint FlagNoBackground = 1u << 4;
    public const uint FlagNoScrollbar = 1u << 5;
    public const uint FlagNoScrollWithMouse = 1u << 6;
    public const uint FlagHasError = 1u << 9;
    public const uint FlagPosIsDefault = 1u << 10;

    // Event types
    public const ushort EvClicked = 1;
    public const ushort EvBool = 2;
    public const ushort EvInt = 3;
    public const ushort EvFloat = 4;
    public const ushort EvText = 5;
    public const ushort EvOpen = 6;
    public const ushort EvGeometry = 7;
    public const ushort EvVisibility = 8;
    public const ushort EvError = 10;

    // Geometry event flags
    public const uint GeoFocused = 1u << 0;
    public const uint GeoHovered = 1u << 2;

    public static uint Fnv1a(ReadOnlySpan<byte> bytes, uint hash = 2166136261)
    {
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }

    /// <summary>
    /// The characters of <paramref name="s"/> that can make up its first <paramref name="maxBytes"/>
    /// UTF-8 bytes: every character is at least one byte, so no more than maxBytes of them. Encoding
    /// this prefix and cutting it (CutUtf8) gives the same bytes as encoding the whole string and
    /// cutting that (a surrogate pair split at the prefix's end falls past the cut either way).
    /// </summary>
    public static ReadOnlySpan<char> Prefix(string s, int maxBytes) =>
        s.Length > maxBytes ? s.AsSpan(0, Math.Max(0, maxBytes)) : s.AsSpan();

    /// <summary>The length of <paramref name="s"/> cut to at most <paramref name="max"/> bytes without splitting a character.</summary>
    public static int CutUtf8(ReadOnlySpan<byte> s, int max)
    {
        if (s.Length <= max) return s.Length;
        int n = Math.Max(0, max);
        while (n > 0 && (s[n] & 0xC0) == 0x80) n--;
        return n;
    }

    /// <summary>Bytes one window header can take besides its body (key, title, error text at their limits).</summary>
    private const int MaxWindowHeader = (1 + MaxStr8) * 2 + 4 + 32 + 2 + MaxErrorText + 4 + 4;

    /// <summary>The engine's limit for one whole submit (UiGetInfo MaxBytesPerSubmit, §6.1).</summary>
    public const int DefaultMaxSubmitBytes = 256 * 1024;

    /// <summary>
    /// A submit: the header, then the first <paramref name="count"/> huds (the complete set;
    /// a hud left out closes). Bodies go for huds with BodyDirty while they fit in
    /// <paramref name="maxTotal"/> next to every window's header (at its largest), taken from
    /// <paramref name="start"/> on, round-robin; the rest are sent as "unchanged" and go in a
    /// later submit. Each hud's <see cref="ScriptHud.BodyInSubmit"/> says whether its body went.
    /// The engine refuses a submit over its limit whole, so several large huds changing in the
    /// same tick would otherwise freeze every window. Returns the length written into
    /// <paramref name="buffer"/> (grown as needed).
    /// </summary>
    public static int WriteSubmit(ref byte[] buffer, IReadOnlyList<ScriptHud> huds, int count, uint ackSeq,
        int maxTotal = DefaultMaxSubmitBytes, int start = 0)
    {
        long budget = (long)maxTotal - 16 - (long)count * MaxWindowHeader;
        int first = count > 0 ? (int)((uint)start % (uint)count) : 0;
        for (int k = 0; k < count; k++)
        {
            ScriptHud h = huds[(first + k) % count];
            h.BodyInSubmit = h.BodyDirty && h.BodyLength <= budget;
            if (h.BodyInSubmit) budget -= h.BodyLength;
        }

        int size = 16;
        for (int i = 0; i < count; i++)
            size += MaxWindowHeader + (huds[i].BodyInSubmit ? huds[i].BodyLength : 0);
        if (buffer.Length < size) buffer = new byte[Math.Max(size, buffer.Length * 2)];
        Span<byte> b = buffer;

        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(b[6..], (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], ackSeq);
        int pos = 16;
        for (int i = 0; i < count; i++)
        {
            ScriptHud h = huds[i];
            uint flags = h.HeaderFlags();
            pos = Str8(b, pos, h.KeyUtf8);
            pos = Str8Text(b, pos, h.Title);
            BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], flags); pos += 4;
            pos = Size(b, pos, h.DefaultSize);
            pos = Size(b, pos, h.MinSize);
            pos = Request(b, pos, h.RequestSize);
            pos = Request(b, pos, h.RequestPos);
            if ((flags & FlagHasError) != 0) pos = ErrorText(b, pos, h.LastError);
            BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], h.ListSeq); pos += 4;
            if (h.BodyInSubmit)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], (uint)h.BodyLength); pos += 4;
                h.Body.AsSpan(0, h.BodyLength).CopyTo(b[pos..]);
                pos += h.BodyLength;
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], BodyUnchanged); pos += 4;
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)pos);
        return pos;
    }

    /// <summary>ErrorText: u16 length + UTF-8 cut to 1 KB, encoding only the part that can be sent (an error can quote a huge string).</summary>
    private static int ErrorText(Span<byte> b, int pos, string? error)
    {
        Span<byte> err = stackalloc byte[3 * (MaxErrorText + 1)];
        int e = Encoding.UTF8.GetBytes(Prefix(error ?? string.Empty, MaxErrorText), err);
        int n = CutUtf8(err[..e], MaxErrorText);
        BinaryPrimitives.WriteUInt16LittleEndian(b[pos..], (ushort)n);
        err[..n].CopyTo(b[(pos + 2)..]);
        return pos + 2 + n;
    }

    /// <summary>A str8 of text, encoding only the part that can be sent (the title can be any length).</summary>
    private static int Str8Text(Span<byte> b, int pos, string s)
    {
        Span<byte> tmp = stackalloc byte[3 * (MaxStr8 + 1)];
        int n = Encoding.UTF8.GetBytes(Prefix(s ?? string.Empty, MaxStr8), tmp);
        return Str8(b, pos, tmp[..n]);
    }

    private static int Str8(Span<byte> b, int pos, ReadOnlySpan<byte> s)
    {
        int n = CutUtf8(s, MaxStr8);
        b[pos] = (byte)n;
        s[..n].CopyTo(b[(pos + 1)..]);
        return pos + 1 + n;
    }

    /// <summary>A size the engine requires finite: anything else is sent as 0 (= engine default).</summary>
    private static int Size(Span<byte> b, int pos, Vector2 v)
    {
        pos = F32(b, pos, float.IsFinite(v.X) ? v.X : 0f);
        return F32(b, pos, float.IsFinite(v.Y) ? v.Y : 0f);
    }

    /// <summary>A request: both NaN = none; a non-finite component makes it none.</summary>
    private static int Request(Span<byte> b, int pos, Vector2 v)
    {
        bool none = !float.IsFinite(v.X) || !float.IsFinite(v.Y);
        pos = F32(b, pos, none ? float.NaN : v.X);
        return F32(b, pos, none ? float.NaN : v.Y);
    }

    private static int F32(Span<byte> b, int pos, float v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b[pos..], v);
        return pos + 4;
    }
}

/// <summary>
/// Records one hud's render pass into its scratch buffer: one op per ImGui call, in display-list
/// format 1. Keeps the ID stack and the block stack (Child, TreeNode) the way the engine's parser
/// scopes them, so mismatches fail at record time with a message the script author understands.
///
/// Widget keys: FNV-1a(stackHash, the label's ID part: the whole label, or from "###" on).
/// stackHash starts at the hud's hash; PushID(s) hashes the string's bytes into it, PushID(n) its
/// 4 little-endian bytes; an open TreeNode and a Child make their own key the stackHash for their
/// children. CollapsingHeader pushes nothing (as in ImGui).
/// </summary>
internal sealed class HudRecorder
{
    public const int MaxDepth = 32;

    public ScriptHud? Hud;
    public int MaxOps = 1500;
    public int MaxBytes = 64 * 1024;

    private enum BlockKind : byte { Root, Child, Tree }

    private struct Frame
    {
        public BlockKind Kind;
        /// <summary>The stackHash to restore when the block closes.</summary>
        public uint SavedHash;
        /// <summary>Where the block's u32 BlockLength sits in the scratch buffer.</summary>
        public int LengthAt;
        /// <summary>PushIDs and PushStyleColors open in this scope.</summary>
        public int Ids, Colors;
    }

    private readonly Frame[] _frames = new Frame[MaxDepth + 1];   // [0] = the root scope
    private int _frameCount;
    private readonly uint[] _idSaved = new uint[MaxDepth + 1];    // stackHash before each open PushID
    private int _idCount;
    private uint _stackHash;
    /// <summary>Scopes this pass left open that the recorder closed (IDs and colours inside closed blocks).</summary>
    private int _autoClosed;
    private byte[] _tmp = new byte[256];

    public void Begin(ScriptHud hud, int maxOps, int maxBytes)
    {
        Hud = hud;
        MaxOps = maxOps;
        MaxBytes = maxBytes;
        hud.ScratchLength = 0;
        hud.OpCount = 0;
        hud.SeenKeys.Clear();
        _frames[0] = new Frame { Kind = BlockKind.Root };
        _frameCount = 1;
        _idCount = 0;
        _autoClosed = 0;
        _stackHash = hud.Hash;
    }

    /// <summary>
    /// Ends the pass: closes the blocks, IDs and colours still open (one console warning per hud),
    /// then writes the End op. Returns the hud.
    /// </summary>
    public ScriptHud End()
    {
        ScriptHud hud = Hud!;
        int left = _autoClosed;
        while (_frameCount > 1) left += CloseTop() + 1;
        left += _frames[0].Ids + _frames[0].Colors;   // the engine pops the root's leftovers itself
        _idCount = 0;
        if (left > 0 && !hud.ScopeWarned)
        {
            hud.ScopeWarned = true;
            hud.Ctx.Host.Print($"hud {hud.Name} left {left} ImGui scope(s) open; they were closed");
        }
        Ensure(hud, 4);
        hud.Scratch[hud.ScratchLength++] = HudFormat.OpEnd;
        hud.Scratch[hud.ScratchLength++] = 0;
        hud.Scratch[hud.ScratchLength++] = 0;
        hud.Scratch[hud.ScratchLength++] = 0;
        Hud = null;
        return hud;
    }

    public void Abort()
    {
        Hud = null;
        _frameCount = 1;
        _idCount = 0;
    }

    /// <summary>Open blocks plus open PushIDs (the engine's nesting depth).</summary>
    public int Depth => _frameCount - 1 + _idCount;

    // ── Keys ────────────────────────────────────────────────────────────

    /// <summary>The widget key <paramref name="label"/> gets under the current ID stack. Writes nothing.</summary>
    public uint KeyOf(string label)
    {
        ReadOnlySpan<byte> bytes = Encode(label ?? string.Empty);
        int hashes = bytes.IndexOf("###"u8);
        return HudFormat.Fnv1a(hashes >= 0 ? bytes[hashes..] : bytes, _stackHash);
    }

    /// <summary>UTF-8 of <paramref name="s"/>, cut to 4 KB on a character boundary, in a reused buffer.</summary>
    private ReadOnlySpan<byte> Encode(string s)
    {
        // Only the first 4096 characters can reach the first 4096 bytes: a huge label costs
        // nothing more, and the buffer stays at most ~12 KB.
        ReadOnlySpan<char> src = HudFormat.Prefix(s, HudFormat.MaxString);
        int max = Encoding.UTF8.GetMaxByteCount(src.Length);
        if (max > _tmp.Length) _tmp = new byte[Math.Max(max, _tmp.Length * 2)];
        int n = Encoding.UTF8.GetBytes(src, _tmp);
        return _tmp.AsSpan(0, HudFormat.CutUtf8(_tmp.AsSpan(0, n), HudFormat.MaxString));
    }

    private static void NoteKey(ScriptHud hud, uint key, string label)
    {
        if (hud.SeenKeys.Add(key) || hud.DuplicateWarned) return;
        hud.DuplicateWarned = true;
        hud.Ctx.Host.Print($"two widgets share the ID '{label}' in hud {hud.Name}; add ##something to one label, or use PushID");
    }

    // ── Text ────────────────────────────────────────────────────────────

    /// <summary>Text, TextWrapped, TextDisabled, BulletText or SeparatorText (by op code).</summary>
    public void Text(byte code, string text)
    {
        if (code is not (HudFormat.OpText or HudFormat.OpTextWrapped or HudFormat.OpTextDisabled
            or HudFormat.OpBulletText or HudFormat.OpSeparatorText))
            code = HudFormat.OpText;
        int payload = BeginOp(code);
        Str(text);
        EndOp(payload);
    }

    public void TextColored(uint abgr, string text)
    {
        int payload = BeginOp(HudFormat.OpTextColored);
        U32(abgr);
        Str(text);
        EndOp(payload);
    }

    public void LabelText(string label, string text)
    {
        int payload = BeginOp(HudFormat.OpLabelText);
        Str(label);
        Str(text);
        EndOp(payload);
    }

    // ── Layout ──────────────────────────────────────────────────────────

    public void Separator() => EndOp(BeginOp(HudFormat.OpSeparator));

    public void SameLine(float offset, float spacing)
    {
        int payload = BeginOp(HudFormat.OpSameLine);
        F32(offset);
        F32(spacing);
        EndOp(payload);
    }

    public void NewLine() => EndOp(BeginOp(HudFormat.OpNewLine));

    public void Spacing() => EndOp(BeginOp(HudFormat.OpSpacing));

    public void Dummy(float w, float h)
    {
        int payload = BeginOp(HudFormat.OpDummy);
        F32(w);
        F32(h);
        EndOp(payload);
    }

    public void Indent(float w)
    {
        int payload = BeginOp(HudFormat.OpIndent);
        F32(w);
        EndOp(payload);
    }

    public void Unindent(float w)
    {
        int payload = BeginOp(HudFormat.OpUnindent);
        F32(w);
        EndOp(payload);
    }

    // ── Widgets ─────────────────────────────────────────────────────────

    public void Button(uint key, string label, float w, float h, bool small)
    {
        int payload = BeginOp(HudFormat.OpButton);
        Keyed(key, label);
        F32(w);
        F32(h);
        U8(small ? (byte)1 : (byte)0);
        EndOp(payload);
    }

    public void Checkbox(uint key, string label, bool value)
    {
        int payload = BeginOp(HudFormat.OpCheckbox);
        Keyed(key, label);
        U8(value ? (byte)1 : (byte)0);
        EndOp(payload);
    }

    public void SliderInt(uint key, string label, int v, int min, int max, string fmt)
    {
        int payload = BeginOp(HudFormat.OpSliderInt);
        Keyed(key, label);
        I32(v);
        I32(min);
        I32(max);
        Str(fmt ?? string.Empty);
        EndOp(payload);
    }

    public void SliderFloat(uint key, string label, float v, float min, float max, string fmt)
    {
        int payload = BeginOp(HudFormat.OpSliderFloat);
        Keyed(key, label);
        F32(v);
        F32(min);
        F32(max);
        Str(fmt ?? string.Empty);
        EndOp(payload);
    }

    /// <summary><paramref name="maxLen"/> (bytes of text) is clamped to 1..4096; the value is cut to it.</summary>
    public void InputText(uint key, string label, string value, int maxLen, uint flags)
    {
        maxLen = Math.Clamp(maxLen, 1, HudFormat.MaxString);
        int payload = BeginOp(HudFormat.OpInputText);
        Keyed(key, label);
        Str(value ?? string.Empty, maxLen);
        U16((ushort)maxLen);
        U32(flags);
        EndOp(payload);
    }

    /// <summary>At most 512 items are written (the engine's limit); an empty list is an empty combo.</summary>
    public void Combo(uint key, string label, int index, IReadOnlyList<string> items)
    {
        int count = Math.Min(items?.Count ?? 0, HudFormat.MaxComboItems);
        int payload = BeginOp(HudFormat.OpCombo);
        Keyed(key, label);
        I32(index);
        U16((ushort)count);
        for (int i = 0; i < count; i++) Str(items![i] ?? string.Empty);
        EndOp(payload);
    }

    public void Selectable(uint key, string label, bool selected, uint flags, float w, float h)
    {
        int payload = BeginOp(HudFormat.OpSelectable);
        Keyed(key, label);
        U8(selected ? (byte)1 : (byte)0);
        U32(flags);
        F32(w);
        F32(h);
        EndOp(payload);
    }

    /// <summary>An empty overlay shows ImGui's percentage.</summary>
    public void ProgressBar(float fraction, float w, float h, string overlay)
    {
        int payload = BeginOp(HudFormat.OpProgressBar);
        F32(fraction);
        F32(w);
        F32(h);
        Str(overlay ?? string.Empty);
        EndOp(payload);
    }

    // ── Icons and number inputs (op level 2) ────────────────────────────

    /// <summary>An AC icon: kind (icon / object / spell) and id, size, uv0, uv1, tint and border (0xAABBGGRR).</summary>
    public void Image(byte kind, uint id, float w, float h, Vector2 uv0, Vector2 uv1, uint tint, uint border)
    {
        int payload = BeginOp(HudFormat.OpImage);
        IconBody(kind, id, w, h, uv0, uv1, tint, border);
        EndOp(payload);
    }

    /// <summary>A button showing an AC icon; <paramref name="strId"/> is its ID (not drawn). bg and tint are 0xAABBGGRR.</summary>
    public void ImageButton(uint key, string strId, byte kind, uint id, float w, float h, Vector2 uv0, Vector2 uv1, uint bg, uint tint)
    {
        int payload = BeginOp(HudFormat.OpImageButton);
        Keyed(key, strId);
        IconBody(kind, id, w, h, uv0, uv1, bg, tint);
        EndOp(payload);
    }

    private void IconBody(byte kind, uint id, float w, float h, Vector2 uv0, Vector2 uv1, uint col1, uint col2)
    {
        U8(kind);
        U32(id);
        F32(w);
        F32(h);
        F32(uv0.X);
        F32(uv0.Y);
        F32(uv1.X);
        F32(uv1.Y);
        U32(col1);
        U32(col2);
    }

    public void InputInt(uint key, string label, int v, int step, int stepFast, uint flags)
    {
        int payload = BeginOp(HudFormat.OpInputInt);
        Keyed(key, label);
        I32(v);
        I32(step);
        I32(stepFast);
        U32(flags);
        EndOp(payload);
    }

    public void InputFloat(uint key, string label, float v, float step, float stepFast, string fmt, uint flags)
    {
        int payload = BeginOp(HudFormat.OpInputFloat);
        Keyed(key, label);
        F32(v);
        F32(step);
        F32(stepFast);
        Str(fmt ?? string.Empty);
        U32(flags);
        EndOp(payload);
    }

    public void DragInt(uint key, string label, int v, float speed, int min, int max, string fmt, uint flags)
    {
        int payload = BeginOp(HudFormat.OpDragInt);
        Keyed(key, label);
        I32(v);
        F32(speed);
        I32(min);
        I32(max);
        Str(fmt ?? string.Empty);
        U32(flags);
        EndOp(payload);
    }

    public void DragFloat(uint key, string label, float v, float speed, float min, float max, string fmt, uint flags)
    {
        int payload = BeginOp(HudFormat.OpDragFloat);
        Keyed(key, label);
        F32(v);
        F32(speed);
        F32(min);
        F32(max);
        Str(fmt ?? string.Empty);
        U32(flags);
        EndOp(payload);
    }

    public void SetItemTooltip(string text)
    {
        int payload = BeginOp(HudFormat.OpSetItemTooltip);
        Str(text);
        EndOp(payload);
    }

    // ── IDs and style ───────────────────────────────────────────────────

    public void PushID(string s)
    {
        CheckDepth();
        ScriptHud hud = Hud!;
        int payload = BeginOp(HudFormat.OpPushID);
        U8(0);
        int strAt = hud.ScratchLength + 2;
        Str(s ?? string.Empty);
        uint hash = HudFormat.Fnv1a(hud.Scratch.AsSpan(strAt, hud.ScratchLength - strAt), _stackHash);
        EndOp(payload);
        OpenId(hash);
    }

    public void PushID(int n)
    {
        CheckDepth();
        int payload = BeginOp(HudFormat.OpPushID);
        U8(1);
        I32(n);
        EndOp(payload);
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(le, n);
        OpenId(HudFormat.Fnv1a(le, _stackHash));
    }

    private void OpenId(uint hash)
    {
        _idSaved[_idCount++] = _stackHash;
        _stackHash = hash;
        _frames[_frameCount - 1].Ids++;
    }

    public void PopID()
    {
        ref Frame top = ref _frames[_frameCount - 1];
        if (top.Ids == 0) throw new ScriptRuntimeException("ImGui.PopID() without a PushID() in this block");
        EndOp(BeginOp(HudFormat.OpPopID));
        top.Ids--;
        _stackHash = _idSaved[--_idCount];
    }

    public void PushStyleColor(int colId, uint abgr)
    {
        int payload = BeginOp(HudFormat.OpPushStyleColor);
        U16((ushort)Math.Clamp(colId, 0, ushort.MaxValue));
        U32(abgr);
        EndOp(payload);
        _frames[_frameCount - 1].Colors++;
    }

    public void PopStyleColor(int count)
    {
        if (count <= 0) return;
        ref Frame top = ref _frames[_frameCount - 1];
        if (count > top.Colors)
            throw new ScriptRuntimeException($"ImGui.PopStyleColor({count}) pops more than were pushed ({top.Colors} in this block)");
        for (int left = count; left > 0; left -= 255)
        {
            int payload = BeginOp(HudFormat.OpPopStyleColor);
            U8((byte)Math.Min(left, 255));
            EndOp(payload);
        }
        top.Colors -= count;
    }

    // ── Blocks ──────────────────────────────────────────────────────────

    public void BeginChild(uint key, string id, float w, float h, bool border, uint windowFlags)
    {
        CheckDepth();
        int payload = BeginOp(HudFormat.OpChild, HudFormat.OpHasBlock);
        Keyed(key, id);
        F32(w);
        F32(h);
        U8(border ? (byte)1 : (byte)0);
        U32(windowFlags);
        EndOp(payload);
        OpenBlock(BlockKind.Child, key);
    }

    public void EndChild()
    {
        if (_frames[_frameCount - 1].Kind != BlockKind.Child)
            throw new ScriptRuntimeException(_frames[_frameCount - 1].Kind == BlockKind.Tree
                ? "ImGui.EndChild() inside an open TreeNode; call TreePop() first"
                : "ImGui.EndChild() without a matching BeginChild()");
        _autoClosed += CloseTop();
    }

    /// <summary>A plain op (ImGui has no end call for it): its contents follow it as ordinary ops.</summary>
    public void CollapsingHeader(uint key, string label, uint flags, bool recordedOpen)
    {
        int payload = BeginOp(HudFormat.OpCollapsingHeader);
        Keyed(key, label);
        U32(flags);
        U8(recordedOpen ? (byte)1 : (byte)0);
        EndOp(payload);
    }

    /// <summary>
    /// Recorded open: the block stays open until <see cref="TreePop"/>. Recorded closed: an empty
    /// block, closed at once (the script doesn't call TreePop for a closed node).
    /// </summary>
    public void TreeNode(uint key, string label, uint flags, bool recordedOpen)
    {
        CheckDepth();
        int payload = BeginOp(HudFormat.OpTreeNode, HudFormat.OpHasBlock);
        Keyed(key, label);
        U32(flags);
        U8(recordedOpen ? (byte)1 : (byte)0);
        EndOp(payload);
        if (recordedOpen) OpenBlock(BlockKind.Tree, key);
        else
        {
            U32(0);   // BlockLength 0: no children
            CheckBytes();
        }
    }

    public void TreePop()
    {
        if (_frames[_frameCount - 1].Kind != BlockKind.Tree)
            throw new ScriptRuntimeException(_frames[_frameCount - 1].Kind == BlockKind.Child
                ? "ImGui.TreePop() inside an open BeginChild(); call EndChild() first"
                : "ImGui.TreePop() without an open TreeNode");
        _autoClosed += CloseTop();
    }

    private void OpenBlock(BlockKind kind, uint key)
    {
        ScriptHud hud = Hud!;
        int lengthAt = hud.ScratchLength;
        U32(0);   // BlockLength, patched by CloseTop
        CheckBytes();   // before the frame opens, so a throw leaves nothing half-open
        _frames[_frameCount++] = new Frame { Kind = kind, SavedHash = _stackHash, LengthAt = lengthAt };
        _stackHash = key;
    }

    /// <summary>Closes the innermost block: patches its length, drops its IDs. Returns the IDs and colours it left open.</summary>
    private int CloseTop()
    {
        ScriptHud hud = Hud!;
        Frame f = _frames[--_frameCount];
        _idCount -= f.Ids;
        _stackHash = f.SavedHash;
        int length = hud.ScratchLength - (f.LengthAt + 4);
        BinaryPrimitives.WriteUInt32LittleEndian(hud.Scratch.AsSpan(f.LengthAt), (uint)length);
        return f.Ids + f.Colors;
    }

    private void CheckDepth()
    {
        if (Depth + 1 > MaxDepth)
            throw new ScriptRuntimeException($"ImGui calls nested deeper than {MaxDepth}");
    }

    // ── Writing ─────────────────────────────────────────────────────────

    /// <summary>key + label.</summary>
    private void Keyed(uint key, string label)
    {
        U32(key);
        Str(label ?? string.Empty);
        NoteKey(Hud!, key, label ?? string.Empty);
    }

    private int BeginOp(byte code, byte opFlags = 0)
    {
        ScriptHud hud = Hud ?? throw new ScriptRuntimeException("ImGui calls only work inside a hud's OnRender");
        if (hud.OpCount >= MaxOps)
            throw new ScriptRuntimeException($"hud {hud.Name} drew more than {MaxOps} items");
        hud.OpCount++;
        Ensure(hud, 4);
        hud.Scratch[hud.ScratchLength++] = code;
        hud.Scratch[hud.ScratchLength++] = opFlags;
        hud.ScratchLength += 2;                 // PayloadLength, patched by EndOp
        return hud.ScratchLength;
    }

    private void EndOp(int payloadStart)
    {
        ScriptHud hud = Hud!;
        int length = hud.ScratchLength - payloadStart;
        if (length > HudFormat.MaxPayload)
            throw new ScriptRuntimeException($"hud {hud.Name}: one item is over {HudFormat.MaxPayload / 1024} KB (too many or too long combo items?)");
        BinaryPrimitives.WriteUInt16LittleEndian(hud.Scratch.AsSpan(payloadStart - 2), (ushort)length);
        CheckBytes();
    }

    /// <summary>
    /// The body so far must leave room for the End op (4 bytes), so the whole list stays within
    /// MaxBytes, the engine's per-window limit. Checked after every op and after a block's
    /// BlockLength field (written after its op): a list over the limit is refused by the engine,
    /// which refuses the whole submit, every hud's included.
    /// </summary>
    private void CheckBytes()
    {
        ScriptHud hud = Hud!;
        if (hud.ScratchLength > MaxBytes - 4)
            throw new ScriptRuntimeException($"hud {hud.Name} drew more than {MaxBytes / 1024} KB");
    }

    private static void Ensure(ScriptHud hud, int extra)
    {
        if (hud.ScratchLength + extra <= hud.Scratch.Length) return;
        int size = hud.Scratch.Length;
        while (size < hud.ScratchLength + extra) size *= 2;
        Array.Resize(ref hud.Scratch, size);
    }

    private void U8(byte v)
    {
        ScriptHud hud = Hud!;
        Ensure(hud, 1);
        hud.Scratch[hud.ScratchLength++] = v;
    }

    private void U16(ushort v)
    {
        ScriptHud hud = Hud!;
        Ensure(hud, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(hud.Scratch.AsSpan(hud.ScratchLength), v);
        hud.ScratchLength += 2;
    }

    private void U32(uint v)
    {
        ScriptHud hud = Hud!;
        Ensure(hud, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(hud.Scratch.AsSpan(hud.ScratchLength), v);
        hud.ScratchLength += 4;
    }

    private void I32(int v)
    {
        ScriptHud hud = Hud!;
        Ensure(hud, 4);
        BinaryPrimitives.WriteInt32LittleEndian(hud.Scratch.AsSpan(hud.ScratchLength), v);
        hud.ScratchLength += 4;
    }

    /// <summary>A finite f32; NaN and infinities are written as 0 (the engine refuses them).</summary>
    private void F32(float v)
    {
        ScriptHud hud = Hud!;
        Ensure(hud, 4);
        BinaryPrimitives.WriteSingleLittleEndian(hud.Scratch.AsSpan(hud.ScratchLength), float.IsFinite(v) ? v : 0f);
        hud.ScratchLength += 4;
    }

    /// <summary>str: u16 length + UTF-8, cut to <paramref name="maxBytes"/> (at most 4 KB) on a character boundary.</summary>
    private void Str(string s, int maxBytes = HudFormat.MaxString)
    {
        ScriptHud hud = Hud!;
        s ??= string.Empty;
        maxBytes = Math.Clamp(maxBytes, 0, HudFormat.MaxString);
        // Only the first maxBytes characters can reach the first maxBytes bytes, so a huge
        // string (string.rep) costs O(maxBytes) here, not O(its length), and allocates nothing.
        ReadOnlySpan<char> src = HudFormat.Prefix(s, maxBytes);
        Ensure(hud, 2 + Encoding.UTF8.GetMaxByteCount(src.Length));
        int at = hud.ScratchLength + 2;
        Span<byte> dst = hud.Scratch.AsSpan(at);
        int n = HudFormat.CutUtf8(dst[..Encoding.UTF8.GetBytes(src, dst)], maxBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(hud.Scratch.AsSpan(hud.ScratchLength), (ushort)n);
        hud.ScratchLength = at + n;
    }
}
