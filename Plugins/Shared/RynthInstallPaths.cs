// ============================================================================
//  RynthInstallPaths.cs - where RynthCore and RynthSuite are installed.
//
//  ONE FILE, TWO COPIES: RynthCore src\Shared\RynthInstallPaths.cs (engine,
//  launcher, injector, loader, StatusAgent) and RynthSuite
//  Plugins\Shared\RynthInstallPaths.cs (plugins, Loot Editor). Keep them
//  byte-identical; RynthSuite's RynthAi host tests compare the two when the
//  RynthCore sibling is checked out.
//
//  READ ONLY. This only answers "which folder"; it never writes or deletes a
//  setting, and nothing here may be used to pick a DLL or EXE to load or run.
//
//  Order, per folder (first usable answer wins):
//    1. environment variable  RYNTHCORE_DIR / RYNTHSUITE_DIR  (dev setups)
//    2. HKCU\Software\Rynth   CoreDir / SuiteDir               (per-user install)
//    3. HKLM\Software\Rynth   CoreDir / SuiteDir, 32-bit view  (all-users install)
//    4. C:\Games\RynthCore / C:\Games\RynthSuite               (today's layout)
//  A value counts only if it is an absolute path; anything else is skipped.
//  With nothing set the result is exactly the old hard-coded folder.
//
//  Idea from Silentkelpie (rynth PR #1); re-implemented here.
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RynthCore.Install;

/// <summary>Where <see cref="RynthInstallPaths"/> reads settings from. Tests pass a fake.</summary>
internal interface IInstallSettingSource
{
    /// <summary>The process environment variable, or null.</summary>
    string? EnvironmentVariable(string name);

    /// <summary>A string value under HKCU\Software\Rynth, or null.</summary>
    string? CurrentUser(string valueName);

    /// <summary>A string value under HKLM\Software\Rynth (32-bit registry view), or null.</summary>
    string? LocalMachine(string valueName);
}

internal static class RynthInstallPaths
{
    /// <summary>RynthCore folder when nothing is set (launcher, Runtime\, Logs\, NavData\, AcClient\).</summary>
    public const string DefaultCoreDir = @"C:\Games\RynthCore";

    /// <summary>RynthSuite folder when nothing is set (plugins and their data, e.g. RynthAi\).</summary>
    public const string DefaultSuiteDir = @"C:\Games\RynthSuite";

    public const string CoreEnvironmentVariable = "RYNTHCORE_DIR";
    public const string SuiteEnvironmentVariable = "RYNTHSUITE_DIR";

    /// <summary>Key under HKCU / HKLM.</summary>
    public const string RegistryKey = @"Software\Rynth";
    public const string CoreValueName = "CoreDir";
    public const string SuiteValueName = "SuiteDir";

    private static string? _coreDir;
    private static string? _suiteDir;

    /// <summary>The RynthCore install folder. Resolved once per process.</summary>
    public static string CoreDir =>
        _coreDir ??= Resolve(CoreEnvironmentVariable, CoreValueName, DefaultCoreDir, SystemInstallSettings.Instance);

    /// <summary>The RynthSuite install folder. Resolved once per process.</summary>
    public static string SuiteDir =>
        _suiteDir ??= Resolve(SuiteEnvironmentVariable, SuiteValueName, DefaultSuiteDir, SystemInstallSettings.Instance);

    /// <summary>&lt;CoreDir&gt;\Logs - the unified log folder.</summary>
    public static string CoreLogsDir => Path.Combine(CoreDir, "Logs");

    /// <summary>&lt;SuiteDir&gt;\RynthAi - RynthAi's home and data root.</summary>
    public static string RynthAiDir => Path.Combine(SuiteDir, "RynthAi");

    /// <summary>
    /// The first usable folder from <paramref name="source"/>: environment, then HKCU,
    /// then HKLM, else <paramref name="fallback"/> exactly as given. Never throws.
    /// </summary>
    internal static string Resolve(string environmentVariable, string valueName, string fallback, IInstallSettingSource source)
    {
        string? found = Normalize(Ask(() => source.EnvironmentVariable(environmentVariable)))
                     ?? Normalize(Ask(() => source.CurrentUser(valueName)))
                     ?? Normalize(Ask(() => source.LocalMachine(valueName)));
        return found ?? fallback;
    }

    private static readonly char[] BadNameChars = { '*', '?', '<', '>', '"', '|' };

    private static string? Ask(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }

    /// <summary>
    /// An absolute folder path, tidied (whitespace and surrounding quotes trimmed,
    /// trailing separators dropped except on a drive root); null when the value is
    /// empty, relative or not a valid path.
    /// </summary>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string path = value.Trim().Trim('"').Trim();
        if (path.Length == 0) return null;
        // .NET accepts some names Windows folders can't have; refuse them here.
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(BadNameChars) >= 0) return null;
        if (path.Length > 2 && path.IndexOf(':', 2) >= 0) return null; // a colon only after the drive letter
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            string trimmed = full.TrimEnd('\\', '/');
            return trimmed.Length < root.Length ? root : trimmed;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>The real environment and registry (read only, advapi32 directly: NativeAOT and trim safe).</summary>
internal sealed class SystemInstallSettings : IInstallSettingSource
{
    public static readonly SystemInstallSettings Instance = new();

    private SystemInstallSettings() { }

    public string? EnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? CurrentUser(string valueName) => ReadString(HKEY_CURRENT_USER, valueName, 0);

    public string? LocalMachine(string valueName) => ReadString(HKEY_LOCAL_MACHINE, valueName, RRF_SUBKEY_WOW6432KEY);

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));
    private const uint RRF_RT_REG_SZ = 0x00000002;
    private const uint RRF_RT_REG_EXPAND_SZ = 0x00000004;
    private const uint RRF_SUBKEY_WOW6432KEY = 0x00020000;
    private const int ERROR_SUCCESS = 0;
    private const int MaxValueBytes = 64 * 1024;

    // Every argument is a pointer or an integer, so no marshalling stub is needed.
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegGetValueW(IntPtr hkey, IntPtr subKey, IntPtr valueName, uint flags,
                                           IntPtr type, IntPtr data, IntPtr dataBytes);

    private static string? ReadString(IntPtr root, string valueName, uint viewFlag)
    {
        IntPtr subKey = IntPtr.Zero, name = IntPtr.Zero, size = IntPtr.Zero, data = IntPtr.Zero;
        try
        {
            subKey = Marshal.StringToHGlobalUni(RynthInstallPaths.RegistryKey);
            name = Marshal.StringToHGlobalUni(valueName);
            size = Marshal.AllocHGlobal(sizeof(uint));
            uint flags = RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | viewFlag;

            Marshal.WriteInt32(size, 0);
            if (RegGetValueW(root, subKey, name, flags, IntPtr.Zero, IntPtr.Zero, size) != ERROR_SUCCESS)
                return null;
            int bytes = Marshal.ReadInt32(size);
            if (bytes < 2 || bytes > MaxValueBytes) return null;

            data = Marshal.AllocHGlobal(bytes + 2);
            Marshal.WriteInt32(size, bytes);
            if (RegGetValueW(root, subKey, name, flags, IntPtr.Zero, data, size) != ERROR_SUCCESS)
                return null;
            int written = Math.Min(Marshal.ReadInt32(size), bytes);
            Marshal.WriteInt16(data, written - (written % 2), 0); // terminate even if the value wasn't
            return Marshal.PtrToStringUni(data);
        }
        catch
        {
            return null; // no advapi32, access denied, ... -> the next source / the default
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (size != IntPtr.Zero) Marshal.FreeHGlobal(size);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
            if (subKey != IntPtr.Zero) Marshal.FreeHGlobal(subKey);
        }
    }
}
