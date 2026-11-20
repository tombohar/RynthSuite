// The raycasting sources linked in from RynthAi log through RynthLog, which needs the plugin's install
// paths and host; this tool has neither, so its trace calls go nowhere.
namespace RynthCore.Plugin.RynthAi
{
    internal enum LogCat { Raycast }
    internal static class RynthLog
    {
        public static void Trace(LogCat cat, string message) { }
    }
}
