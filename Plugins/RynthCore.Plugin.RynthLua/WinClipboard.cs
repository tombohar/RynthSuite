using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The Windows clipboard as Unicode text, for ImGui.SetClipboardText / GetClipboardText. Straight
/// Win32 (no engine op): the clipboard belongs to the desktop, not to the hud's render pass, so it
/// works any time. Another program can hold the clipboard open for a moment; opening it is tried
/// a few times, briefly, and false / "" when it stays busy.
/// </summary>
internal static class WinClipboard
{
    /// <summary>Tests: stand-ins for the real clipboard (the tests mustn't overwrite the user's).</summary>
    internal static Func<string, bool>? SetOverride;
    internal static Func<string>? GetOverride;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int OpenTries = 5;
    private const int OpenRetryMs = 2;

    internal static bool SetText(string text)
    {
        if (SetOverride != null) return SetOverride(text);
        if (!Open()) return false;
        try
        {
            if (EmptyClipboard() == 0) return false;
            int bytes = (text.Length + 1) * 2;
            IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (mem == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(mem);
            if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                Marshal.WriteInt16(p, text.Length * 2, 0);
            }
            finally { GlobalUnlock(mem); }
            if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero) { GlobalFree(mem); return false; }
            return true;   // the clipboard owns mem now
        }
        finally { CloseClipboard(); }
    }

    internal static string GetText()
    {
        if (GetOverride != null) return GetOverride();
        if (IsClipboardFormatAvailable(CF_UNICODETEXT) == 0 || !Open()) return string.Empty;
        try
        {
            IntPtr mem = GetClipboardData(CF_UNICODETEXT);
            if (mem == IntPtr.Zero) return string.Empty;
            IntPtr p = GlobalLock(mem);
            if (p == IntPtr.Zero) return string.Empty;
            try { return Marshal.PtrToStringUni(p) ?? string.Empty; }
            finally { GlobalUnlock(mem); }
        }
        finally { CloseClipboard(); }
    }

    private static bool Open()
    {
        for (int i = 0; i < OpenTries; i++)
        {
            if (OpenClipboard(IntPtr.Zero) != 0) return true;
            Thread.Sleep(OpenRetryMs);
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern int OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern int CloseClipboard();
    [DllImport("user32.dll")] private static extern int EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern int IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern int GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
}
