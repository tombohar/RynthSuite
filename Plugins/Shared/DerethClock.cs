// ============================================================================
//  RynthSuite - Plugins/Shared/DerethClock.cs
//  Dereth's clock: day or night now, the hour's name, and how long until the
//  next dawn or dusk in real time (a request 2026-11-01: a small window with
//  the real minutes until the next day/night change). Pure: the caller reads
//  the raw clock and hands it in, so RynthDefective.Tests checks the math.
//
//  The raw clock is the double at 0x008379A8 (acclient's Timer::cur_time, in
//  seconds). It is only readable once the player is in world, and a fault
//  there is an AccessViolation .NET can't catch: never read it before world
//  entry (RynthAi's Meta/ExpressionEngine.cs ReadGameClock has the same rule).
//
//  The calendar is RynthAi's meta expressions' (the same file, which matches
//  UtilityBelt's DerethTime): 476.25 ticks an hour, 16 hours a day; day is
//  hours 4 (Dawnsong) to 11 (Warmtide-and-Half), night 12 (Evensong) to 3
//  (Foredawn-and-Half). A tick is one real second (the clock counts seconds),
//  so a Dereth day is 16 x 476.25 = 7620 s = 2 h 7 min of real time: 63.5
//  real minutes of day, then 63.5 of night.
// ============================================================================

using System;
using System.Globalization;

namespace RynthCore.Plugin.Shared;

/// <summary>Dereth's time of day at one reading of the clock.</summary>
internal readonly record struct DerethNow(
    bool IsDay,
    int Hour,                 // 0-15
    string HourName,          // "Dawnsong", "Evensong-and-Half"...
    double SecondsToChange)   // real seconds until the next dawn (at night) or dusk (by day)
{
    /// <summary>What comes next: "Night" by day, "Day" at night.</summary>
    public string Next => IsDay ? "Night" : "Day";

    /// <summary>The hour the change comes at: Evensong (dusk) or Dawnsong (dawn).</summary>
    public string NextHourName => DerethClock.HourNames[IsDay ? DerethClock.DuskHour : DerethClock.DawnHour];
}

internal static class DerethClock
{
    /// <summary>acclient's Timer::cur_time: a double, in seconds. Mapped only once in world.</summary>
    public const uint ClockAddress = 0x008379A8;

    public const double TicksInHour = 476.25;
    public const int HoursInDay = 16;
    public const double TicksInDay = TicksInHour * HoursInDay;   // 7620
    /// <summary>The clock counts seconds: one tick is one real second.</summary>
    public const double SecondsPerTick = 1.0;
    public const int DawnHour = 4, DuskHour = 12;

    // RynthAi's GetGameTicks: the raw clock to Dereth's calendar (UB's DerethTime). Only its
    // -210 + 8 hours moves the time of day; the 10 years are whole days.
    private const double CalendarOffset = -210 + TicksInHour * 8 + TicksInHour * HoursInDay * 30 * 12 * 10;

    public static readonly string[] HourNames =
    {
        "Darktide", "Darktide-and-Half", "Foredawn", "Foredawn-and-Half",
        "Dawnsong", "Dawnsong-and-Half", "Morntide", "Morntide-and-Half",
        "Midsong", "Midsong-and-Half", "Warmtide", "Warmtide-and-Half",
        "Evensong", "Evensong-and-Half", "Gloaming", "Gloaming-and-Half",
    };

    /// <summary>A reading worth using: a number, past zero (0 before the clock is set).</summary>
    public static bool IsUsable(double raw) => double.IsFinite(raw) && raw > 0;

    /// <summary>Dereth's calendar ticks for the raw clock (RynthAi's getgameticks).</summary>
    public static double GameTicks(double raw) => raw + CalendarOffset;

    /// <summary>Ticks since Darktide began today, 0 to just under <see cref="TicksInDay"/>.</summary>
    public static double TicksIntoDay(double raw)
    {
        double t = GameTicks(raw) % TicksInDay;
        return t < 0 ? t + TicksInDay : t;
    }

    /// <summary>Day or night, the hour, and the real seconds until the next dawn or dusk.</summary>
    public static DerethNow At(double raw)
    {
        double into = TicksIntoDay(raw);
        int hour = Math.Clamp((int)(into / TicksInHour), 0, HoursInDay - 1);
        bool day = hour >= DawnHour && hour < DuskHour;
        double dawn = DawnHour * TicksInHour, dusk = DuskHour * TicksInHour;
        double ticks = day ? dusk - into
                     : into < dawn ? dawn - into
                     : TicksInDay - into + dawn;
        return new DerethNow(day, hour, HourNames[hour], ticks * SecondsPerTick);
    }

    /// <summary>
    /// A countdown in real time: "1h 4m" and "23m" (whole minutes, rounded up) from 10 minutes,
    /// "4m 10s" under 10 minutes, "45s" under one. Never "0s" before the change: rounded up.
    /// </summary>
    public static string Countdown(double seconds)
    {
        long s = (long)Math.Ceiling(Math.Max(0, seconds));
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + "s";
        if (s < 600) return (s / 60).ToString(CultureInfo.InvariantCulture) + "m " + (s % 60).ToString(CultureInfo.InvariantCulture) + "s";
        long m = (s + 59) / 60;
        if (m < 60) return m.ToString(CultureInfo.InvariantCulture) + "m";
        return (m / 60).ToString(CultureInfo.InvariantCulture) + "h " + (m % 60).ToString(CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>The window's line: "Night in 23m", "Day in 4m 10s".</summary>
    public static string Label(DerethNow now, double seconds) => now.Next + " in " + Countdown(seconds);
}
