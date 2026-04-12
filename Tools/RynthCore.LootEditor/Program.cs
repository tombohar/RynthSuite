using Avalonia;
using System;
using System.IO;

namespace RynthCore.LootEditor;

class Program
{
    internal static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LootEditor.log");

    [System.STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"UNHANDLED: {e.ExceptionObject}");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            throw;
        }
    }

    internal static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); }
        catch { /* best-effort */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
