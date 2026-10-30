using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// /ra version: this RynthAi build (the DLL's version resource, stamped at release with the
    /// release number and commit; "dev (commit)" for an unstamped build), when it was built,
    /// and the engine it runs on (build stamp from the engine's status export on engines that
    /// publish it, and the plugin API version).
    /// </summary>
    private void HandleVersionCommand()
    {
        string path = PluginModule.Path();
        string version = PluginModule.FormatVersion(path);
        string built = PluginModule.BuildDate(path);
        ChatLine($"[RynthAi] RynthAi {version} (plugin {PluginModule.HandWrittenVersion}), built {built}.");

        string engine = "unknown";
        uint api = Host.Version;
        string? json = Host.HasGetEngineStatusJson ? Host.GetEngineStatusJson() : null;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("engineVersion", out JsonElement ev) && ev.ValueKind == JsonValueKind.String)
                    engine = ev.GetString() ?? engine;
            }
            catch (JsonException) { }
        }
        ChatLine(engine == "unknown"
            ? $"[RynthAi] Engine: plugin API v{api} (this engine doesn't report its build)."
            : $"[RynthAi] Engine: RynthCore {engine}, plugin API v{api}.");
    }

    /// <summary>Where this plugin's DLL was loaded from, and what its version resource says.</summary>
    private static unsafe class PluginModule
    {
        /// <summary>RynthPluginVersion's hand-written string (it never changes between builds).</summary>
        public const string HandWrittenVersion = "0.5.0-legacy-ui";

        private const uint FromAddress = 0x4, UnchangedRefCount = 0x2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetModuleFileNameW(IntPtr module, char* buffer, int size);

        /// <summary>The loaded DLL (the engine's shadow copy), found from an export's address.</summary>
        public static string Path()
        {
            try
            {
                delegate* unmanaged[Cdecl]<IntPtr> anchor = &PluginExports.GetName;
                if (!GetModuleHandleExW(FromAddress | UnchangedRefCount, (IntPtr)anchor, out IntPtr module) || module == IntPtr.Zero)
                    return string.Empty;
                char* buffer = stackalloc char[1024];
                int n = GetModuleFileNameW(module, buffer, 1024);
                return n > 0 && n < 1024 ? new string(buffer, 0, n) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>"2026.9.30.1 (8ca322f)" for a release, "dev (8ca322f)" for an unstamped build.</summary>
        public static string FormatVersion(string path)
        {
            string? product = null;
            try
            {
                if (path.Length > 0 && File.Exists(path))
                    product = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(product)) return "unknown";
            string version = product.Trim();
            string commit = string.Empty;
            int plus = version.IndexOf('+');
            if (plus >= 0)
            {
                commit = version[(plus + 1)..];
                version = version[..plus];
            }
            if (commit.Length > 7) commit = commit[..7];
            if (version is "1.0.0" or "1.0.0.0") version = "dev";
            return commit.Length > 0 ? $"{version} ({commit})" : version;
        }

        /// <summary>The DLL's last-write time (the engine's shadow copy keeps the build's), local time.</summary>
        public static string BuildDate(string path)
        {
            try
            {
                if (path.Length > 0 && File.Exists(path))
                    return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch { }
            return "unknown";
        }
    }
}
