// ImGuiNativeBinding.cs - Binds ImGui.NET's "cimgui" P/Invokes to the cimgui module the
// engine already loaded. Linked into every plugin that draws with ImGui.NET (RynthAi, RynthILT).
//
// The engine ships its own build as RynthCore.cimgui.dll. Without this resolver, Windows
// resolves "cimgui" to a separate cimgui.dll: a second ImGui instance whose windows (the Mini
// Remote, its tabs, the Item HUD) are built but never reach the engine's draw data. Both
// plugins bound to the one module share its current context, so one can draw inside a window
// the other has begun (RynthILT's tabs inside RynthAi's Mini Remote).
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ImGuiNET;

namespace RynthCore.Plugin.Shared;

internal static class ImGuiNativeBinding
{
    private const string EngineModule = "RynthCore.cimgui.dll";
    private const string LegacyModule = "cimgui.dll";

    private static int _configured;   // 1 once the resolver is registered (survives DLL-copy reuse)
    private static int _resolvedLogged;
    private static Action<string>? _log;

    /// <summary>
    /// Registers the resolver. Call before the first ImGui call (Initialize). The resolver can be
    /// set only once per assembly, so later calls (a reused plugin DLL copy) are no-ops.
    /// <paramref name="log"/> gets one line when the resolver is registered and one when it binds.
    /// </summary>
    public static void Ensure(Action<string>? log = null)
    {
        if (log != null) _log = log;
        if (Interlocked.Exchange(ref _configured, 1) != 0) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, Resolve);
            Log("[ImGui] native resolver registered (prefers the engine's " + EngineModule + ").");
        }
        catch (InvalidOperationException)
        {
            // Already registered by an earlier instance in this process.
        }
        catch (Exception ex)
        {
            Log($"[ImGui] ImGuiNativeBinding.Ensure failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try { _log?.Invoke(message); }
        catch { }
    }

    /// <summary>Runs on the first cimgui P/Invoke: the engine's module, else whatever cimgui.dll is loaded.</summary>
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, LegacyModule, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string bound = EngineModule;
        IntPtr module = GetModuleHandleA(EngineModule);
        if (module == IntPtr.Zero)
        {
            bound = LegacyModule;
            module = GetModuleHandleA(LegacyModule);
        }

        if (Interlocked.Exchange(ref _resolvedLogged, 1) == 0)
            Log(module != IntPtr.Zero
                ? $"[ImGui] cimgui bound to loaded {bound} (0x{module.ToInt64():X})."
                : "[ImGui] no loaded cimgui module found; falling back to the default DLL search.");
        return module; // IntPtr.Zero = default resolution
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);
}
