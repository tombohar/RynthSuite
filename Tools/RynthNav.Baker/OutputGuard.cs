namespace RynthNav.Baker;

/// <summary>
/// Keeps the baker out of the folder players' clients read tiles from. A run with no
/// --out used to write straight into C:\Games\RynthCore\NavData (it once overwrote a
/// live tile). Now --out is required, and the live folder needs --force-live as well.
/// Every tile/obj write checks again, so no code path can slip past it.
/// </summary>
internal static class OutputGuard
{
    public const string LiveNavData = @"C:\Games\RynthCore\NavData";

    /// <summary>Set by --force-live (the bake-ahead watcher, which is meant to feed the live folder).</summary>
    public static bool ForceLive { get; set; }

    public static bool IsLive(string dir)
    {
        string a = Normalize(dir), b = Normalize(LiveNavData);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Throws when <paramref name="dir"/> is the live NavData folder and --force-live wasn't given.</summary>
    public static void Check(string dir)
    {
        if (IsLive(dir) && !ForceLive)
            throw new InvalidOperationException(
                $"refusing to write into the live tile folder {LiveNavData}: bake into another folder, or pass --force-live if you really mean it");
    }

    private static string Normalize(string dir)
    {
        string full;
        try { full = Path.GetFullPath(dir); } catch { full = dir; }
        full = full.TrimEnd('\\', '/');
        // A junction or symlink pointing at the live folder counts as the live folder.
        try
        {
            var info = new DirectoryInfo(full);
            if (info.Exists && info.LinkTarget != null)
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null) full = Path.GetFullPath(target.FullName).TrimEnd('\\', '/');
            }
        }
        catch { }
        return full;
    }
}
