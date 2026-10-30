using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Key and mouse input posted to AC's own window, for the Mag-Tools <c>/mt</c> verbs that drive the
/// client directly: <c>send</c>, <c>click</c>, <c>movement</c>, <c>strafe</c>, <c>get xy</c>,
/// <c>client</c>. A port of Mag-Tools' PostMessageTools (Mag-Plugins\Shared\PostMessageTools.cs):
/// the same virtual keys, scan codes (lParam) and click spots, so metas written for Mag-Tools
/// behave the same.
///
/// The window is the one in this process with AC's class ("Turbine Device Class"), found once.
/// PostMessage is safe from any thread. A key held for a time is released from <see cref="Pump"/>
/// (the plugin tick), not from a timer: nothing here may outlive the plugin's Shutdown.
///
/// Nothing in this class decides whether input is allowed. Every caller goes through
/// RynthAiPlugin.KeystrokesAllowed (the setting "Allow metas to send keystrokes", off by
/// default) first; <see cref="PostAttempts"/> counts every post so a test can prove that.
/// </summary>
internal static class GameInput
{
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_MOUSEMOVE = 0x0200,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const byte VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_PAUSE = 0x13,
        VK_SPACE = 0x20, VK_DELETE = 0x2E, VK_F4 = 0x73, VK_F12 = 0x7B;
    private const int SW_MINIMIZE = 6, SW_RESTORE = 9;

    // A key held longer than this is a mistake (a meta typed 1500000 for 1500).
    public const int MaxHoldMs = 10_000;
    // The longest line /mt send msg types.
    public const int MaxMessageChars = 200;

    private static IntPtr _hwnd;
    // Pending key releases, run by Pump when due (or at once by Shutdown).
    private static readonly System.Collections.Generic.List<(long DueMs, Action Release)> Pending = new();

    /// <summary>Every message this class tried to post (counted before the window is looked up).</summary>
    public static int PostAttempts { get; private set; }

    /// <summary>Tests only: receives (message, wParam, lParam) instead of a window, which then counts as found.</summary>
    internal static Action<uint, uint, uint>? TestSink;

    /// <summary>AC's game window in this process, or IntPtr.Zero.</summary>
    public static IntPtr GameWindow
    {
        get
        {
            if (TestSink != null) return (IntPtr)1;
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) return _hwnd;
            // Every top-level AC window on the desktop, keeping this process's (several clients run).
            // FindWindowEx rather than EnumWindows: no callback delegate to marshal under NativeAOT.
            uint pid = (uint)Environment.ProcessId;
            IntPtr found = IntPtr.Zero, h = IntPtr.Zero;
            for (int i = 0; i < 64; i++)
            {
                h = FindWindowEx(IntPtr.Zero, h, "Turbine Device Class", null);
                if (h == IntPtr.Zero) break;
                GetWindowThreadProcessId(h, out uint p);
                if (p == pid) { found = h; break; }
            }
            _hwnd = found;
            return found;
        }
    }

    public static bool Available => GameWindow != IntPtr.Zero;

    // ── Keys (Mag-Tools PostMessageTools) ──────────────────────────────────────

    public static void SendEnter() => Key(VK_RETURN, 0x001C0001, 0xC01C0001);
    public static void SendPause() => Key(VK_PAUSE, 0x00450001, 0xC0450001);
    public static void SendDel() => Key(VK_DELETE, 0x01530001, 0x01530001);
    /// <summary>F4: opens / closes the fellowship panel.</summary>
    public static void SendF4() => Key(VK_F4, 0x003E0001, 0xC03E0001);
    /// <summary>F12: opens / closes the main pack.</summary>
    public static void SendF12() => Key(VK_F12, 0x00580001, 0xC0580001);

    /// <summary>Ctrl + a letter.</summary>
    public static void SendCntrl(char ch)
    {
        Post(WM_KEYDOWN, VK_CONTROL, 0x001D0001);
        Post(WM_KEYDOWN, CharCode(ch), 0x00100001);
        Post(WM_KEYUP, CharCode(ch), 0xC0100001);
        Post(WM_KEYUP, VK_CONTROL, 0xC01D0001);
    }

    /// <summary>Types <paramref name="msg"/> key by key (letters, space and "/"; as Mag-Tools).</summary>
    public static void SendMsg(string msg)
    {
        if (msg.Length > MaxMessageChars) msg = msg[..MaxMessageChars];
        foreach (char ch in msg)
        {
            uint lparam = (uint)((ScanCode(ch) << 16) | 1);
            Post(WM_KEYDOWN, CharCode(ch), lparam);
            Post(WM_KEYUP, CharCode(ch), 0xC0000000 | lparam);
        }
    }

    /// <summary>Space (a jump), held <paramref name="msToHoldDown"/> ms, with the shift / w z x c keys
    /// pressed as it is released (Mag-Tools /mt jump).</summary>
    public static void SendSpace(int msToHoldDown = 0, bool shift = false, bool w = false, bool z = false, bool x = false, bool c = false)
    {
        Post(WM_KEYDOWN, VK_SPACE, 0x00390001);
        void Release()
        {
            if (shift) Post(WM_KEYDOWN, VK_SHIFT, 0x002A0001);
            if (w) Post(WM_KEYDOWN, CharCode('w'), 0x00110001);
            if (z) Post(WM_KEYDOWN, CharCode('z'), 0x002C0001);
            if (x) Post(WM_KEYDOWN, CharCode('x'), 0x002D0001);
            if (c) Post(WM_KEYDOWN, CharCode('c'), 0x002E0001);
            Post(WM_KEYUP, VK_SPACE, 0xC0390001);
            if (w) Post(WM_KEYUP, CharCode('w'), 0xC0110001);
            if (z) Post(WM_KEYUP, CharCode('z'), 0xC02C0001);
            if (x) Post(WM_KEYUP, CharCode('x'), 0xC02D0001);
            if (c) Post(WM_KEYUP, CharCode('c'), 0xC02E0001);
            if (shift) Post(WM_KEYUP, VK_SHIFT, 0xC02A0001);
        }
        After(msToHoldDown, Release);
    }

    /// <summary>A movement key (a letter of AC's keymap) held <paramref name="msToHoldDown"/> ms.</summary>
    public static void SendMovement(char ch, int msToHoldDown = 0)
    {
        uint scan = (uint)ScanCode(ch) * 0x10000;
        Post(WM_KEYDOWN, CharCode(ch), 0x00000001 + scan);
        After(msToHoldDown, () => Post(WM_KEYUP, CharCode(ch), 0xC0000001 + scan));
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────

    /// <summary>A left click at window coordinates (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static void SendMouseClick(int x, int y)
    {
        uint loc = (uint)((y << 16) | (x & 0xFFFF));
        Post(WM_MOUSEMOVE, 0, loc);
        Post(WM_LBUTTONDOWN, 1, loc);
        Post(WM_LBUTTONUP, 0, loc);
    }

    // AC's confirmation box buttons, relative to the window's centre. Three heights each, as Mag-Tools:
    // windowed and windowless clients put the box a few pixels apart.
    public static void ClickOK() => ClickCentre(0);
    public static void ClickYes() => ClickCentre(-80);
    public static void ClickNo() => ClickCentre(80);

    private static void ClickCentre(int dx)
    {
        RECT r;
        if (TestSink != null) r = new RECT { Right = 800, Bottom = 600 };
        else if (!GetWindowRect(GameWindow, out r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        foreach (int dy in new[] { 18, 25, 31 })
            SendMouseClick(w / 2 + dx, h / 2 + dy);
    }

    /// <summary>The cursor relative to the game window's top-left (Mag-Tools /mt get xy), or false.</summary>
    public static bool TryCursorInWindow(out int x, out int y)
    {
        x = y = 0;
        if (!GetCursorPos(out POINT p) || !GetWindowRect(GameWindow, out RECT r)) return false;
        x = p.X - r.Left;
        y = p.Y - r.Top;
        return true;
    }

    // ── Window ───────────────────────────────────────────────────────────────

    public static bool Minimize() => TestSink != null ? Sink(0xFFF0, SW_MINIMIZE) : ShowWindow(GameWindow, SW_MINIMIZE);
    public static bool Restore() => TestSink != null ? Sink(0xFFF0, SW_RESTORE) : ShowWindow(GameWindow, SW_RESTORE);

    private static bool Sink(uint msg, uint wParam)
    {
        PostAttempts++;
        TestSink!(msg, wParam, 0);
        return true;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static void Key(byte vk, uint down, uint up)
    {
        Post(WM_KEYDOWN, vk, down);
        Post(WM_KEYUP, vk, up);
    }

    private static void Post(uint msg, uint wParam, uint lParam)
    {
        PostAttempts++;
        if (TestSink != null) { TestSink(msg, wParam, lParam); return; }
        IntPtr h = GameWindow;
        if (h != IntPtr.Zero) PostMessage(h, msg, (IntPtr)wParam, (IntPtr)unchecked((int)lParam));
    }

    /// <summary>Runs <paramref name="action"/> now, or from <see cref="Pump"/> after <paramref name="ms"/>.</summary>
    private static void After(int ms, Action action)
    {
        if (ms <= 0) { action(); return; }
        ms = Math.Min(ms, MaxHoldMs);
        lock (Pending) Pending.Add((Environment.TickCount64 + ms, action));
    }

    /// <summary>Releases the keys whose hold time is up. Called every plugin tick.</summary>
    public static void Pump()
    {
        if (Pending.Count == 0) return;
        List<Action>? due = null;
        long now = Environment.TickCount64;
        lock (Pending)
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Pending[i].DueMs > now) continue;
                (due ??= new()).Insert(0, Pending[i].Release);
                Pending.RemoveAt(i);
            }
        }
        if (due == null) return;
        foreach (var release in due)
        {
            try { release(); }
            catch (Exception ex) { Debug.WriteLine("GameInput release failed: " + ex.Message); }
        }
    }

    /// <summary>Plugin shutdown: releases every key still held, then forgets the window.</summary>
    public static void Shutdown()
    {
        List<Action> all;
        lock (Pending)
        {
            all = new List<Action>(Pending.Count);
            foreach (var p in Pending) all.Add(p.Release);
            Pending.Clear();
        }
        foreach (var release in all)
        {
            try { release(); }
            catch (Exception ex) { Debug.WriteLine("GameInput release failed: " + ex.Message); }
        }
        _hwnd = IntPtr.Zero;
    }

    private static byte ScanCode(char ch) => char.ToLowerInvariant(ch) switch
    {
        'a' => 0x1E, 'b' => 0x30, 'c' => 0x2E, 'd' => 0x20, 'e' => 0x12, 'f' => 0x21, 'g' => 0x22, 'h' => 0x23,
        'i' => 0x17, 'j' => 0x24, 'k' => 0x25, 'l' => 0x26, 'm' => 0x32, 'n' => 0x31, 'o' => 0x18, 'p' => 0x19,
        'q' => 0x10, 'r' => 0x13, 's' => 0x1F, 't' => 0x14, 'u' => 0x16, 'v' => 0x2F, 'w' => 0x11, 'x' => 0x2D,
        'y' => 0x15, 'z' => 0x2C, '/' => 0x35, ' ' => 0x39,
        _ => 0,
    };

    private static byte CharCode(char ch)
    {
        char c = char.ToLowerInvariant(ch);
        if (c >= 'a' && c <= 'z') return (byte)(0x41 + (c - 'a'));
        return c == '/' ? (byte)0xBF : (byte)0x20;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
