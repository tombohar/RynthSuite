// The linked geometry sources trace through the plugin's RynthLog (Diagnostics/RynthLog.cs),
// which needs the whole plugin (its JSON context, the host). Offline there is nothing to trace
// to: this stands in for the one call they make.

namespace RynthCore.Plugin.RynthAi;

internal enum LogCat { Raycast }

internal static class RynthLog
{
    public static void Trace(LogCat cat, string message) { }
}
