using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthInventory.Ui;

/// <summary>One input from the engine for a widget (a click, a new value, an open state).</summary>
internal readonly struct UiEvent
{
    public readonly ushort Type;
    public readonly bool Bool;
    public readonly int Int;
    public readonly string Text;
    public readonly bool Submitted;

    public UiEvent(ushort type, bool b = false, int i = 0, string? text = null, bool submitted = false)
    {
        Type = type;
        Bool = b;
        Int = i;
        Text = text ?? string.Empty;
        Submitted = submitted;
    }
}

/// <summary>
/// One script window (engine API v71) and its recorder. A render pass records ImGui-style calls
/// into a display list; every widget's handler (click, value change) is kept by its widget key
/// until the next pass, so an event from the engine runs the code that drew that widget.
///
/// Widget keys are FNV-1a(id, window hash): RynthInventory names every widget itself, so it needs
/// no ID stack. ImGui sees "label###id": its ID comes from the id alone, so labels can change.
/// The engine echoes the key in events and keys its per-widget local values by it.
/// </summary>
internal sealed class UiWindow
{
    public readonly string Key;
    public readonly byte[] KeyUtf8;
    public readonly uint Hash;

    public string Title;
    public bool Visible;
    public bool ShowInBar = true;
    /// <summary>With ShowInBar: the window has its own button on the RynthCore bar (title as the
    /// tooltip) rather than a line in the Scripts menu.</summary>
    public bool BarButton = true;
    public float DefaultWidth, DefaultHeight, MinWidth, MinHeight;

    /// <summary>A pass is wanted at the next chance (input arrived, it was just shown, data changed).</summary>
    public bool NeedsPass = true;
    public long NextPassMs;

    // The list as recorded by the last pass, and what the engine last accepted.
    private byte[] _body = new byte[16 * 1024];
    private int _len;
    private int _ops;
    private bool _cut;
    private byte[] _sentBody = Array.Empty<byte>();
    private int _sentLen = -1;
    public uint ListSeq { get; private set; }
    internal bool BodyDirty;

    // Header as last accepted by the engine.
    private string? _sentTitle;
    private uint _sentFlags;
    private float _sentDw = float.NaN, _sentDh, _sentMw, _sentMh;

    private Dictionary<uint, Action<UiEvent>> _handlers = new();
    private Dictionary<uint, Action<UiEvent>> _next = new();
    private readonly Dictionary<uint, bool> _openStates = new();

    // Child blocks: where each open block's u32 length sits, or -1 when the block was dropped.
    private readonly int[] _blocks = new int[16];
    private int _blockDepth;
    private byte[] _tmp = new byte[512];

    public UiWindow(string key, string title)
    {
        Key = key;
        KeyUtf8 = Encoding.UTF8.GetBytes(key);
        Hash = UiFormat.Fnv1a(KeyUtf8);
        Title = title;
    }

    public uint Flags => (Visible ? UiFormat.FlagVisible : 0) | (ShowInBar ? UiFormat.FlagShowInBar : 0)
        | (ShowInBar && BarButton ? UiFormat.FlagBarButton : 0);

    public bool HeaderDirty =>
        _sentTitle != Title || _sentFlags != Flags || _sentDw != DefaultWidth || _sentDh != DefaultHeight
        || _sentMw != MinWidth || _sentMh != MinHeight;

    public ReadOnlySpan<byte> Body => _body.AsSpan(0, _len);

    // ── Pass ────────────────────────────────────────────────────────────────

    public void Begin()
    {
        _len = 0;
        _ops = 0;
        _cut = false;
        _blockDepth = 0;
        _next.Clear();
    }

    /// <summary>Ends the pass: closes open blocks, writes End, and notes whether the list changed.</summary>
    public void End()
    {
        while (_blockDepth > 0) EndChild();
        if (_cut)
        {
            // Room was kept for this line (see Room).
            int p = BeginOp(UiFormat.OpTextDisabled);
            Str("(more than fits in one window; narrow the list)");
            EndOp(p);
        }
        Ensure(4);
        _body[_len++] = UiFormat.OpEnd;
        _body[_len++] = 0;
        _body[_len++] = 0;
        _body[_len++] = 0;

        (_handlers, _next) = (_next, _handlers);
        bool differs = _sentLen != _len || !_body.AsSpan(0, _len).SequenceEqual(_sentBody.AsSpan(0, _sentLen));
        if (differs && !BodyDirty) ListSeq++;
        BodyDirty = differs;
    }

    /// <summary>The engine accepted the last submit: remember what it now has.</summary>
    internal void MarkSubmitted(bool bodySent)
    {
        if (bodySent)
        {
            if (_sentBody.Length < _len) _sentBody = new byte[_body.Length];
            _body.AsSpan(0, _len).CopyTo(_sentBody);
            _sentLen = _len;
            BodyDirty = false;
        }
        _sentTitle = Title;
        _sentFlags = Flags;
        _sentDw = DefaultWidth; _sentDh = DefaultHeight; _sentMw = MinWidth; _sentMh = MinHeight;
    }

    /// <summary>The engine dropped every window (logout, empty submit): send everything again next time.</summary>
    internal void ForgetSubmitted()
    {
        _sentLen = -1;
        _sentTitle = null;
        BodyDirty = _len > 0;
    }

    internal void Dispatch(uint widgetKey, in UiEvent e)
    {
        if (_handlers.TryGetValue(widgetKey, out Action<UiEvent>? h))
            h(e);
        NeedsPass = true;
    }

    // ── Widgets ─────────────────────────────────────────────────────────────

    public uint KeyOf(string id)
    {
        int max = Encoding.UTF8.GetMaxByteCount(id.Length);
        if (_tmp.Length < max) _tmp = new byte[Math.Max(max, _tmp.Length * 2)];
        int n = Encoding.UTF8.GetBytes(id, _tmp);
        return UiFormat.Fnv1a(_tmp.AsSpan(0, n), Hash);
    }

    public void Text(string text) => WriteText(UiFormat.OpText, text);
    public void TextDisabled(string text) => WriteText(UiFormat.OpTextDisabled, text);
    public void TextWrapped(string text) => WriteText(UiFormat.OpTextWrapped, text);
    public void BulletText(string text) => WriteText(UiFormat.OpBulletText, text);
    public void SeparatorText(string text) => WriteText(UiFormat.OpSeparatorText, text);

    public void TextColored(uint abgr, string text)
    {
        if (!Room()) return;
        int p = BeginOp(UiFormat.OpTextColored);
        U32(abgr);
        Str(text);
        EndOp(p);
    }

    public void Separator()
    {
        if (!Room()) return;
        EndOp(BeginOp(UiFormat.OpSeparator));
    }

    public void Spacing()
    {
        if (!Room()) return;
        EndOp(BeginOp(UiFormat.OpSpacing));
    }

    /// <summary>Next item on the same line, <paramref name="x"/> logical units from the content start (0 = right after).</summary>
    public void SameLine(float x = 0f, float spacing = -1f)
    {
        if (!Room()) return;
        int p = BeginOp(UiFormat.OpSameLine);
        F32(x);
        F32(spacing);
        EndOp(p);
    }

    public void Dummy(float w, float h)
    {
        if (!Room()) return;
        int p = BeginOp(UiFormat.OpDummy);
        F32(w);
        F32(h);
        EndOp(p);
    }

    public void Button(string label, string id, Action onClick, float w = 0f, float h = 0f, bool small = false)
    {
        if (!Room()) return;
        uint key = KeyOf(id);
        int p = BeginOp(UiFormat.OpButton);
        U32(key);
        Str(label + "###" + id);
        F32(w);
        F32(h);
        U8(small ? (byte)1 : (byte)0);
        EndOp(p);
        _next[key] = e => { if (e.Type == UiFormat.EvClicked) onClick(); };
    }

    public void SmallButton(string label, string id, Action onClick) => Button(label, id, onClick, small: true);

    /// <summary>A button drawn highlighted when <paramref name="active"/> (tabs, toggles).</summary>
    public void ToggleButton(string label, string id, bool active, Action onClick, bool small = false)
    {
        if (active)
        {
            PushStyleColor(UiFormat.ColButton, UiColors.TabActive);
            PushStyleColor(UiFormat.ColButtonHovered, UiColors.TabActiveHover);
        }
        Button(label, id, onClick, small: small);
        if (active) PopStyleColor(2);
    }

    public void Checkbox(string label, string id, bool value, Action<bool> set)
    {
        if (!Room()) return;
        uint key = KeyOf(id);
        int p = BeginOp(UiFormat.OpCheckbox);
        U32(key);
        Str(label + "###" + id);
        U8(value ? (byte)1 : (byte)0);
        EndOp(p);
        _next[key] = e => { if (e.Type == UiFormat.EvBool) set(e.Bool); };
    }

    /// <summary>A text box; <paramref name="set"/> gets every edit (submitted = Enter was pressed).</summary>
    public void InputText(string label, string id, string value, int maxLen, Action<string> set, uint flags = 0)
    {
        if (!Room()) return;
        maxLen = Math.Clamp(maxLen, 1, UiFormat.MaxString);
        uint key = KeyOf(id);
        int p = BeginOp(UiFormat.OpInputText);
        U32(key);
        Str(label + "###" + id);
        Str(value ?? string.Empty, maxLen);
        U16((ushort)maxLen);
        U32(flags);
        EndOp(p);
        _next[key] = e => { if (e.Type == UiFormat.EvText) set(e.Text); };
    }

    public void Combo(string label, string id, int index, IReadOnlyList<string> items, Action<int> set)
    {
        if (!Room()) return;
        uint key = KeyOf(id);
        int count = Math.Min(items.Count, 512);
        int p = BeginOp(UiFormat.OpCombo);
        U32(key);
        Str(label + "###" + id);
        I32(index);
        U16((ushort)count);
        for (int i = 0; i < count; i++) Str(items[i]);
        EndOp(p);
        _next[key] = e => { if (e.Type == UiFormat.EvInt) set(e.Int); };
    }

    public void Selectable(string label, string id, bool selected, Action onClick, float w = 0f, float h = 0f)
    {
        if (!Room()) return;
        uint key = KeyOf(id);
        int p = BeginOp(UiFormat.OpSelectable);
        U32(key);
        Str(label + "###" + id);
        U8(selected ? (byte)1 : (byte)0);
        U32(0);
        F32(w);
        F32(h);
        EndOp(p);
        _next[key] = e => { if (e.Type == UiFormat.EvClicked) onClick(); };
    }

    public void ProgressBar(float fraction, float w, float h, string overlay)
    {
        if (!Room()) return;
        int p = BeginOp(UiFormat.OpProgressBar);
        F32(float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f);
        F32(w);
        F32(h);
        Str(overlay ?? string.Empty);
        EndOp(p);
    }

    /// <summary>
    /// An icon (op level 2): <paramref name="kind"/> <see cref="UiFormat.IconKindIcon"/> = a 0x06
    /// texture id, <see cref="UiFormat.IconKindObject"/> = a live object. Engines without op level
    /// 2 skip it (the caller checks first so the layout doesn't shift).
    /// </summary>
    public void Image(byte kind, uint id, float w, float h)
    {
        if (!Room()) return;
        int p = BeginOp(UiFormat.OpImage);
        U8(kind);
        U32(id);
        F32(w);
        F32(h);
        F32(0f); F32(0f);   // uv0
        F32(1f); F32(1f);   // uv1
        U32(0xFFFFFFFFu);   // tint: as is
        U32(0u);            // border: none
        EndOp(p);
    }

    /// <summary>Tooltip for the item just drawn.</summary>
    public void Tooltip(string text)
    {
        if (!Room() || string.IsNullOrEmpty(text)) return;
        int p = BeginOp(UiFormat.OpSetItemTooltip);
        Str(text);
        EndOp(p);
    }

    public void PushStyleColor(int colId, uint abgr)
    {
        if (!Room()) return;
        int p = BeginOp(UiFormat.OpPushStyleColor);
        U16((ushort)colId);
        U32(abgr);
        EndOp(p);
    }

    /// <summary>Pops are dropped when the list is cut short; the engine pops leftovers at the scope's end.</summary>
    public void PopStyleColor(int count = 1)
    {
        if (!Room()) return;
        int p = BeginOp(UiFormat.OpPopStyleColor);
        U8((byte)Math.Clamp(count, 1, 255));
        EndOp(p);
    }

    /// <summary>
    /// A collapsing header; returns whether to record its contents. The open state is the
    /// player's (kept from the engine's Open events), so a header stays as they left it.
    /// </summary>
    public bool CollapsingHeader(string label, string id, bool defaultOpen = false)
    {
        uint key = KeyOf(id);
        bool open = _openStates.TryGetValue(key, out bool o) ? o : defaultOpen;
        if (!Room()) return false;
        int p = BeginOp(UiFormat.OpCollapsingHeader);
        U32(key);
        Str(label + "###" + id);
        U32(defaultOpen ? UiFormat.TreeDefaultOpen : 0);
        U8(open ? (byte)1 : (byte)0);
        EndOp(p);
        _next[key] = e => { if (e.Type == UiFormat.EvOpen) _openStates[key] = e.Bool; };
        return open;
    }

    /// <summary>A scrolling region; h 0 = fill the rest of the window. Must be closed with <see cref="EndChild"/>.</summary>
    public void BeginChild(string id, float w = 0f, float h = 0f, bool border = true)
    {
        if (_blockDepth >= _blocks.Length) throw new InvalidOperationException("child regions nested too deep");
        if (!Room())
        {
            _blocks[_blockDepth++] = -1;
            return;
        }
        uint key = KeyOf(id);
        int p = BeginOp(UiFormat.OpChild, UiFormat.OpHasBlock);
        U32(key);
        Str(id);
        F32(w);
        F32(h);
        U8(border ? (byte)1 : (byte)0);
        U32(0);
        EndOp(p);
        _blocks[_blockDepth++] = _len;
        U32(0); // BlockLength, patched by EndChild
    }

    public void EndChild()
    {
        if (_blockDepth == 0) return;
        int at = _blocks[--_blockDepth];
        if (at < 0) return;
        BinaryPrimitives.WriteUInt32LittleEndian(_body.AsSpan(at), (uint)(_len - (at + 4)));
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether another op fits. Room is kept for the closing "list cut short" line and the End
    /// op, so the list never goes over the engine's limits (it refuses an over-limit list whole).
    /// </summary>
    private bool Room()
    {
        if (_cut) return false;
        if (_ops < UiFormat.MaxOps - 8 && _len < UiFormat.MaxBodyBytes - 4096) return true;
        _cut = true;
        return false;
    }

    private void WriteText(byte code, string text)
    {
        if (!Room()) return;
        int p = BeginOp(code);
        Str(text);
        EndOp(p);
    }

    private int BeginOp(byte code, byte opFlags = 0)
    {
        _ops++;
        Ensure(4);
        _body[_len++] = code;
        _body[_len++] = opFlags;
        _len += 2; // PayloadLength, patched by EndOp
        return _len;
    }

    private void EndOp(int payloadStart)
    {
        int length = _len - payloadStart;
        BinaryPrimitives.WriteUInt16LittleEndian(_body.AsSpan(payloadStart - 2), (ushort)Math.Min(length, ushort.MaxValue));
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _body.Length) return;
        int size = _body.Length;
        while (size < _len + extra) size *= 2;
        Array.Resize(ref _body, size);
    }

    private void U8(byte v) { Ensure(1); _body[_len++] = v; }
    private void U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_body.AsSpan(_len), v); _len += 2; }
    private void U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_body.AsSpan(_len), v); _len += 4; }
    private void I32(int v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_body.AsSpan(_len), v); _len += 4; }

    private void F32(float v)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleLittleEndian(_body.AsSpan(_len), float.IsFinite(v) ? v : 0f);
        _len += 4;
    }

    /// <summary>str: u16 length + UTF-8, cut to <paramref name="maxBytes"/> on a character boundary.</summary>
    private void Str(string s, int maxBytes = UiFormat.MaxString)
    {
        s ??= string.Empty;
        ReadOnlySpan<char> src = s.Length > maxBytes ? s.AsSpan(0, maxBytes) : s.AsSpan();
        Ensure(2 + Encoding.UTF8.GetMaxByteCount(src.Length));
        int at = _len + 2;
        Span<byte> dst = _body.AsSpan(at);
        int n = UiFormat.CutUtf8(dst[..Encoding.UTF8.GetBytes(src, dst)], maxBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(_body.AsSpan(_len), (ushort)n);
        _len = at + n;
    }
}
