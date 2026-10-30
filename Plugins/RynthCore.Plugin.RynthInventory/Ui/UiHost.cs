using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthInventory.Ui;

/// <summary>
/// The plugin's side of the engine's script windows (API v71): polls the engine's events into
/// the windows' handlers and submits the window set when something changed. Plugin pump thread
/// only (inside the plugin's own tick), as the engine requires.
/// </summary>
internal sealed class UiHost
{
    private readonly List<UiWindow> _windows = new();
    private readonly Dictionary<uint, UiWindow> _byHash = new();
    private byte[] _events = new byte[8192];
    private byte[] _submit = new byte[64 * 1024];
    private uint _lastSeq;
    private bool _submittedSomething;
    private long _retryAtMs;
    private long _lastErrorLogMs;

    public Action<string>? Log;

    public IReadOnlyList<UiWindow> Windows => _windows;

    public UiWindow Add(UiWindow w)
    {
        _windows.Add(w);
        _byHash[w.Hash] = w;
        return w;
    }

    /// <summary>Drains the engine's queued events into the windows' handlers.</summary>
    public void Poll(RynthCoreHost host)
    {
        if (!host.HasUi || !_submittedSomething) return;
        for (int round = 0; round < 8; round++)
        {
            int n = host.UiPollEvents(_events, out int remaining);
            if (n > 0) Apply(_events.AsSpan(0, n));
            if (remaining == 0) break;
            if (n == 0) Array.Resize(ref _events, _events.Length * 2); // one event didn't fit
        }
    }

    private void Apply(ReadOnlySpan<byte> data)
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
            if (seq > _lastSeq) _lastSeq = seq;

            _byHash.TryGetValue(window, out UiWindow? w);
            switch (type)
            {
                case UiFormat.EvClicked when w != null && p.Length >= 2:
                    int count = BinaryPrimitives.ReadUInt16LittleEndian(p);
                    for (int i = 0; i < Math.Min(count, 4); i++) w.Dispatch(key, new UiEvent(type));
                    break;
                case UiFormat.EvBool when w != null && p.Length >= 1:
                    w.Dispatch(key, new UiEvent(type, b: p[0] != 0));
                    break;
                case UiFormat.EvInt when w != null && p.Length >= 4:
                    w.Dispatch(key, new UiEvent(type, i: BinaryPrimitives.ReadInt32LittleEndian(p)));
                    break;
                case UiFormat.EvText when w != null && p.Length >= 3:
                {
                    int n = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(p[1..]), p.Length - 3);
                    w.Dispatch(key, new UiEvent(type, text: Encoding.UTF8.GetString(p.Slice(3, n)), submitted: p[0] != 0));
                    break;
                }
                case UiFormat.EvOpen when w != null && p.Length >= 1:
                    w.Dispatch(key, new UiEvent(type, b: p[0] != 0));
                    break;
                case UiFormat.EvVisibility when w != null && p.Length >= 1:
                    // The player closed it (X) or opened it (bar menu / command): theirs wins.
                    w.Visible = p[0] != 0;
                    w.NeedsPass = true;
                    break;
                case UiFormat.EvError when p.Length >= 2:
                {
                    int n = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(p), p.Length - 2);
                    Log?.Invoke("[RynthInventory] window error: " + Encoding.UTF8.GetString(p.Slice(2, n)));
                    break;
                }
            }
        }
    }

    /// <summary>Sends the window set when any window's list or header changed.</summary>
    public void Submit(RynthCoreHost host, long nowMs)
    {
        if (!host.HasUi || nowMs < _retryAtMs) return;
        bool changed = !_submittedSomething && _windows.Count > 0;
        foreach (UiWindow w in _windows)
            if (w.BodyDirty || w.HeaderDirty) { changed = true; break; }
        if (!changed) return;

        int pos = Write();
        int rc = host.UiSubmit(_submit.AsSpan(0, pos));
        if (rc == 0)
        {
            foreach (UiWindow w in _windows) w.MarkSubmitted(w.BodyDirty);
            _submittedSomething = true;
        }
        else
        {
            // Nothing was applied; the engine keeps the previous lists. Try again in a second.
            _retryAtMs = nowMs + 1000;
            if (nowMs - _lastErrorLogMs > 10_000)
            {
                _lastErrorLogMs = nowMs;
                Log?.Invoke($"[RynthInventory] the engine refused the window list ({rc}).");
            }
        }
    }

    /// <summary>Removes every window from the engine (logout, shutdown). Their placement is kept by the engine.</summary>
    public void SubmitEmpty(RynthCoreHost host)
    {
        if (!host.HasUi || !_submittedSomething) return;
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(b, UiFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], UiFormat.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(b[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], _lastSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], 16);
        if (host.UiSubmit(b) == 0)
        {
            _submittedSomething = false;
            foreach (UiWindow w in _windows) w.ForgetSubmitted();
        }
    }

    private int Write()
    {
        int size = 16;
        foreach (UiWindow w in _windows) size += 2 * (1 + UiFormat.MaxStr8) + 4 + 32 + 8 + (w.BodyDirty ? w.Body.Length : 0);
        if (_submit.Length < size) _submit = new byte[Math.Max(size, _submit.Length * 2)];
        Span<byte> b = _submit;

        BinaryPrimitives.WriteUInt32LittleEndian(b, UiFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], UiFormat.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(b[6..], (ushort)_windows.Count);
        // Every event polled so far has been applied (handlers run as they arrive).
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], _lastSeq);
        int pos = 16;
        foreach (UiWindow w in _windows)
        {
            pos = Str8(b, pos, w.KeyUtf8);
            pos = Str8(b, pos, Encoding.UTF8.GetBytes(w.Title ?? string.Empty));
            BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], w.Flags); pos += 4;
            pos = F32(b, pos, w.DefaultWidth);
            pos = F32(b, pos, w.DefaultHeight);
            pos = F32(b, pos, w.MinWidth);
            pos = F32(b, pos, w.MinHeight);
            pos = F32(b, pos, float.NaN); // RequestSize: none
            pos = F32(b, pos, float.NaN);
            pos = F32(b, pos, float.NaN); // RequestPos: none
            pos = F32(b, pos, float.NaN);
            BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], w.ListSeq); pos += 4;
            if (w.BodyDirty)
            {
                ReadOnlySpan<byte> body = w.Body;
                BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], (uint)body.Length); pos += 4;
                body.CopyTo(b[pos..]);
                pos += body.Length;
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b[pos..], UiFormat.BodyUnchanged); pos += 4;
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)pos);
        return pos;
    }

    private static int Str8(Span<byte> b, int pos, ReadOnlySpan<byte> s)
    {
        int n = UiFormat.CutUtf8(s, UiFormat.MaxStr8);
        b[pos] = (byte)n;
        s[..n].CopyTo(b[(pos + 1)..]);
        return pos + 1 + n;
    }

    private static int F32(Span<byte> b, int pos, float v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b[pos..], v);
        return pos + 4;
    }
}
