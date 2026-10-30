using System;
using System.Diagnostics;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's clock for timing: a local DateTime that moves forward at the real rate (Stopwatch) from
/// the wall clock's time when the plugin loaded, and never jumps.
///
/// DateTime.Now jumps when the clocks change: an hour back when daylight saving ends (Europe 25 Oct,
/// the US 1 Nov 2026), forward in spring, and by seconds when Windows corrects the time. Every
/// "(DateTime.Now - last) &lt; interval" check then went negative for the hour: no target scans, no
/// attacks, no buff or loot timeouts, and bane timers read an hour long (structural audit,
/// 2026-10-21). Intervals, timeouts and timestamps compared with each other use this clock.
///
/// DateTime.Now stays only where the time of day is shown or handed out as such: log lines and log
/// file names, backup file names, a meta's getdatetimelocal. Times saved to disk (bane timers) are
/// this clock's local time, read back after a restart against a fresh anchor, as before.
/// </summary>
internal static class RynthClock
{
    private static readonly DateTime Anchor = DateTime.Now;
    private static readonly long AnchorStamp = Stopwatch.GetTimestamp();

    /// <summary>Local time that only moves forward with real time; use for intervals and timeouts.</summary>
    public static DateTime Now => Anchor + Stopwatch.GetElapsedTime(AnchorStamp);
}
