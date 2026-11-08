// ============================================================================
//  RynthDefective - RynthDefectivePlugin.DayNight.cs
//  The Day/Night timer (2026-11-04): day or night in Dereth now and the real
//  time until the next dawn or dusk (Clock/DayNightWindow.cs draws it,
//  Plugins/Shared/DerethClock.cs has the math).
//
//  The tick reads the game clock twice a second while the window shows, and
//  only in world: the clock's address isn't readable before world entry, and
//  a fault there can't be caught (RynthAi's ReadGameClock has the same guard).
//  Before that the window shows "—".
// ============================================================================

using System;
using RynthCore.Plugin.RynthDefective.Clock;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDefective;

public sealed partial class RynthDefectivePlugin
{
    internal const string DayNightSurface = "window:daynight";

    private DayNightWindow? _dayNightWindow;                                 // render thread
    private volatile DayNightView _dayNightView = DayNightView.NotReady;     // tick writes, render reads
    private long _dayNightDrawnMs = long.MinValue / 2, _nextDayNightMs;
    private int _dayNightFailures;

    /// <summary>RynthPluginRenderEmbed "window:daynight". 1 drawn, 0 not ready, -2 failed (three in a row). Render thread.</summary>
    internal int RenderDayNight(out float wantHeight)
    {
        wantHeight = 0;
        if (!UsesShortcutBar) return 0;
        System.Threading.Volatile.Write(ref _dayNightDrawnMs, Environment.TickCount64);
        try
        {
            _dayNightWindow ??= new DayNightWindow(Host);
            wantHeight = _dayNightWindow.Draw(_dayNightView);
            _dayNightFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthDefective] day/night draw failed: {ex.GetType().Name}: {ex.Message}");
            return ++_dayNightFailures >= 3 ? -2 : 0;
        }
    }

    /// <summary>Plugin tick: the clock's view while the window shows.</summary>
    private void TickDayNight(long now)
    {
        if (!UsesShortcutBar) return;
        if (now - System.Threading.Volatile.Read(ref _dayNightDrawnMs) > 2000 || now < _nextDayNightMs) return;
        _nextDayNightMs = now + 500;
        try { _dayNightView = ReadDayNight(now); }
        catch (Exception ex) { Host.Log($"[RynthDefective] day/night read failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The clock now; NotReady out of world (never read then) or before the clock is set.</summary>
    private DayNightView ReadDayNight(long now)
    {
        if (!_inWorld || !_identityKnown || Host.GetPlayerId() == 0) return DayNightView.NotReady;
        long bits = System.Runtime.InteropServices.Marshal.ReadInt64(new IntPtr(unchecked((int)DerethClock.ClockAddress)));
        double raw = BitConverter.Int64BitsToDouble(bits);
        return DerethClock.IsUsable(raw) ? new DayNightView(true, DerethClock.At(raw), now) : DayNightView.NotReady;
    }
}
