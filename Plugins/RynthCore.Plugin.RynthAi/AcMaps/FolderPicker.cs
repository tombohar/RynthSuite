// ============================================================================
//  RynthAi - AcMaps/FolderPicker.cs
//  The ACMAPS window's "Browse..." button: Windows' folder chooser, on its own
//  STA thread so the game keeps running while it is open (it isn't owned by the
//  game window, so it never freezes it). In full-screen it may open behind the
//  game: the folder box beside the button takes a typed or pasted path too.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Plugin.RynthAi.AcMaps;

internal static unsafe class FolderPicker
{
    private const uint BifReturnOnlyFsDirs = 0x0001, BifEditBox = 0x0010, BifNewDialogStyle = 0x0040;
    private const uint BffmInitialized = 1, BffmSetSelectionW = 0x0400 + 103;
    private const int MaxPath = 260;

    [StructLayout(LayoutKind.Sequential)]
    private struct BrowseInfo
    {
        public IntPtr Owner;
        public IntPtr Root;
        public char* DisplayName;
        public char* Title;
        public uint Flags;
        public IntPtr Callback;
        public IntPtr Param;
        public int Image;
    }

    [DllImport("shell32.dll")] private static extern IntPtr SHBrowseForFolderW(BrowseInfo* info);
    [DllImport("shell32.dll")] private static extern int SHGetPathFromIDListW(IntPtr pidl, char* path);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(IntPtr hwnd);

    private static int _open;

    /// <summary>A chooser is open now (one at a time).</summary>
    public static bool IsOpen => Volatile.Read(ref _open) != 0;

    /// <summary>
    /// Opens the chooser starting at <paramref name="start"/>; <paramref name="done"/> gets the folder chosen, or
    /// null when cancelled, on the chooser's thread. False when one is already open.
    /// </summary>
    public static bool Pick(string title, string start, Action<string?> done)
    {
        if (Interlocked.Exchange(ref _open, 1) != 0) return false;
        var t = new Thread(() =>
        {
            string? chosen = null;
            try { chosen = Run(title, start); }
            catch { }
            finally { Volatile.Write(ref _open, 0); }
            try { done(chosen); } catch { }
        })
        { IsBackground = true, Name = "RynthAi folder chooser" };
        try { t.SetApartmentState(ApartmentState.STA); } catch { }
        t.Start();
        return true;
    }

    private static string? Run(string title, string start)
    {
        int ole = OleInitialize(IntPtr.Zero);
        IntPtr startText = Marshal.StringToHGlobalUni(start ?? string.Empty);
        try
        {
            char* name = stackalloc char[MaxPath + 1];
            fixed (char* t = title)
            {
                var info = new BrowseInfo
                {
                    DisplayName = name,
                    Title = t,
                    Flags = BifReturnOnlyFsDirs | BifNewDialogStyle | BifEditBox,
                    Callback = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)&OnEvent,
                    Param = startText,
                };
                IntPtr pidl = SHBrowseForFolderW(&info);
                if (pidl == IntPtr.Zero) return null;
                try
                {
                    char* path = stackalloc char[MaxPath + 1];
                    if (SHGetPathFromIDListW(pidl, path) == 0) return null;
                    string s = new string(path);
                    return s.Length > 0 ? s : null;
                }
                finally { CoTaskMemFree(pidl); }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(startText);
            if (ole >= 0) OleUninitialize();
        }
    }

    /// <summary>BFFM_INITIALIZED: start in the current folder and come to the front.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int OnEvent(IntPtr hwnd, uint msg, IntPtr lParam, IntPtr data)
    {
        try
        {
            if (msg == BffmInitialized)
            {
                if (data != IntPtr.Zero) SendMessageW(hwnd, BffmSetSelectionW, (IntPtr)1, data);
                SetForegroundWindow(hwnd);
            }
        }
        catch { }
        return 0;
    }
}
