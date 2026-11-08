using System;
using RynthCore.Plugin.RynthDefective.Clock;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.Shared;

namespace RynthDefective.Tests
{
    // The Day/Night timer (2026-11-04): Plugins/Shared/DerethClock.cs from a raw clock reading (no game).
    // With RynthAi's calendar offset the time of day is (raw + 3600) mod 7620 ticks, a tick a real second.
    internal static partial class Program
    {
        /// <summary>A raw clock reading <paramref name="into"/> ticks after Darktide began (a later day, so the raw is positive).</summary>
        private static double RawAt(double into, int day = 1) => DerethClock.TicksInDay * day + into - 3600;

        private static void ClockDayNight()
        {
            Eq(DerethClock.TicksInDay, 7620.0, "a Dereth day is 7620 ticks");
            Eq(DerethClock.TicksInDay * DerethClock.SecondsPerTick / 60.0, 127.0, "= 2 h 7 min of real time");

            // Darktide's start: night, dawn in 4 hours = 1905 s.
            DerethNow n = DerethClock.At(RawAt(0));
            Check(!n.IsDay, "Darktide: night");
            Eq(n.Hour, 0, "Darktide: hour 0");
            Eq(n.HourName, "Darktide", "Darktide: its name");
            Eq(n.SecondsToChange, 1905.0, "Darktide: dawn in 4 hours of 476.25 s");
            Eq(n.Next, "Day", "at night the day comes next");
            Eq(n.NextHourName, "Dawnsong", "at Dawnsong");

            // Dawn itself: day for 8 hours, 3810 s.
            n = DerethClock.At(RawAt(1905));
            Check(n.IsDay, "Dawnsong: day");
            Eq(n.HourName, "Dawnsong", "Dawnsong: its name");
            Eq(n.SecondsToChange, 3810.0, "Dawnsong: dusk in 63.5 real minutes");
            Eq(n.NextHourName, "Evensong", "by day the night comes at Evensong");

            // The examples: "Night in 23m", "Day in 4m 10s", "Day in 45s".
            n = DerethClock.At(RawAt(5715 - 1380));
            Check(n.IsDay, "23 min before dusk: day");
            Eq(n.HourName, "Midsong-and-Half", "23 min before dusk: hour 9");
            Eq(DerethClock.Label(n, n.SecondsToChange), "Night in 23m", "23 min before dusk");
            n = DerethClock.At(RawAt(5715 - 250));
            Eq(n.HourName, "Warmtide-and-Half", "the last day hour");
            Eq(DerethClock.Label(n, n.SecondsToChange), "Night in 4m 10s", "250 s before dusk");
            n = DerethClock.At(RawAt(1905 - 45));
            Check(!n.IsDay, "45 s before dawn: night");
            Eq(n.HourName, "Foredawn-and-Half", "the last night hour");
            Eq(DerethClock.Label(n, n.SecondsToChange), "Day in 45s", "45 s before dawn");

            // Dusk itself: night through midnight to the next dawn, 3810 s.
            n = DerethClock.At(RawAt(5715));
            Check(!n.IsDay, "Evensong: night");
            Eq(n.HourName, "Evensong", "Evensong: its name");
            Eq(n.SecondsToChange, 3810.0, "Evensong: dawn in 63.5 real minutes (across midnight)");
            Eq(DerethClock.At(RawAt(7000)).SecondsToChange, 7620.0 - 7000 + 1905, "Gloaming: dawn across midnight");

            // Whole days and the 10-year offset change nothing; a big clock keeps its precision.
            Eq(DerethClock.At(RawAt(735, day: 200_000)).SecondsToChange, DerethClock.At(RawAt(735)).SecondsToChange, "a later day: the same");

            // Not readable yet.
            Check(!DerethClock.IsUsable(0), "0: the clock isn't set");
            Check(!DerethClock.IsUsable(double.NaN) && !DerethClock.IsUsable(-5) && !DerethClock.IsUsable(double.PositiveInfinity), "NaN, negative, infinite: not used");
            Check(DerethClock.IsUsable(123456.5), "a real reading is used");
        }

        /// <summary>Against RynthAi's own meta expressions (getisday, getgamehour, getminutesuntilday/night), and the countdown's continuity.</summary>
        private static void ClockMatchesRynthAi()
        {
            const double hourTicks = 476.25;
            const int dayTicks = 7620;
            int mismatches = 0, jumps = 0, flips = 0;
            DerethNow prev = default;
            double prevRaw = 0;
            for (double raw = 10_000; raw < 10_000 + 2 * dayTicks; raw += 3.7)
            {
                // RynthAi (Meta/ExpressionEngine.cs), as it is written there.
                double ticks = raw - 210 + hourTicks * 8 + hourTicks * 16 * 30 * 12 * 10;
                int hour = (int)(ticks % dayTicks / hourTicks);
                bool isDay = hour >= 4 && hour < 12;
                double into = ticks % dayTicks;
                double untilDay = isDay ? 0 : into < 4 * hourTicks ? 4 * hourTicks - into : dayTicks - into + 4 * hourTicks;
                double untilNight = isDay ? 12 * hourTicks - into : 0;
                int minutes = (int)((isDay ? untilNight : untilDay) / 60);

                DerethNow n = DerethClock.At(raw);
                if (n.Hour != hour || n.IsDay != isDay || (int)(n.SecondsToChange / 60) != minutes) mismatches++;

                if (raw > 10_000)
                {
                    if (n.IsDay != prev.IsDay)
                    {
                        flips++;
                        // The change came when the countdown ran out.
                        if (prev.SecondsToChange > raw - prevRaw + 1e-6) jumps++;
                    }
                    else if (Math.Abs(prev.SecondsToChange - (raw - prevRaw) - n.SecondsToChange) > 1e-6) jumps++;   // a second a second
                }
                prev = n;
                prevRaw = raw;
            }
            Eq(mismatches, 0, "the hour, day or night and the minutes are RynthAi's");
            Eq(jumps, 0, "the countdown falls a second a real second, and the change comes when it runs out");
            Eq(flips, 4, "two days: two dawns and two dusks");
        }

        private static void ClockCountdownText()
        {
            Eq(DerethClock.Countdown(0), "0s", "0");
            Eq(DerethClock.Countdown(0.2), "1s", "a part second rounds up: never 0s before the change");
            Eq(DerethClock.Countdown(45), "45s", "under a minute: seconds");
            Eq(DerethClock.Countdown(59.5), "1m 0s", "59.5 s rounds up to a minute");
            Eq(DerethClock.Countdown(250), "4m 10s", "under 10 minutes: minutes and seconds");
            Eq(DerethClock.Countdown(599), "9m 59s", "just under 10 minutes");
            Eq(DerethClock.Countdown(600), "10m", "10 minutes: minutes only");
            Eq(DerethClock.Countdown(601), "11m", "over 10 minutes: whole minutes rounded up");
            Eq(DerethClock.Countdown(1380), "23m", "23 minutes");
            Eq(DerethClock.Countdown(3600), "1h 0m", "an hour");
            Eq(DerethClock.Countdown(3810), "1h 4m", "the longest wait: 63.5 minutes");
            Eq(DerethClock.Countdown(-3), "0s", "a stale reading past the change: 0s, not negative");
        }

        private static void ClockWindowAndCommand()
        {
            // The window counts down from when the tick read the clock.
            var v = new DayNightView(true, DerethClock.At(RawAt(5715 - 100)), 1000);
            Eq(v.SecondsLeft(1000), 100.0, "at the reading");
            Eq(v.SecondsLeft(3500), 97.5, "2.5 s later");
            Eq(v.SecondsLeft(1_000_000), 0.0, "long after: 0, not negative");
            Check(!DayNightView.NotReady.Ready, "before the world: not ready");
            Eq(DayNightWindow.NotInWorld, "—", "out of world it shows a dash");
            Check(DayNightWindow.Tip(DayNightView.NotReady, 0).Contains("in the world", StringComparison.Ordinal), "tooltip out of world");
            string tip = DayNightWindow.Tip(v, 100);
            Check(tip.Contains("Day now (Warmtide-and-Half)", StringComparison.Ordinal) && tip.Contains("Night comes at Evensong, in 1 min 40 s", StringComparison.Ordinal), "tooltip: " + tip);

            // /rdef daynight [show|hide]: parsed, and every answer says what happened.
            Eq(DefCommandParser.Parse("daynight").Text, "", "alone: toggle");
            Eq(DefCommandParser.Parse("daynight show").Text, "show", "show");
            Eq(DefCommandParser.Parse("DayNight ON").Text, "show", "on = show, any case");
            Eq(DefCommandParser.Parse("daynight hide").Text, "hide", "hide");
            Eq(DefCommandParser.Parse("daynight off").Text, "hide", "off = hide");
            DefCommand bad = DefCommandParser.Parse("daynight soon");
            Check(bad.Verb == DefVerb.Error && bad.Error.Contains("Usage", StringComparison.Ordinal), "a wrong word: the usage");
            Check(DefCommandParser.Usage.Contains("daynight", StringComparison.Ordinal), "in the usage");

            DerethNow dusk = DerethClock.At(RawAt(5715));
            Eq(DefReplies.DayNight(1, dusk), (true, "Day/Night timer shown. Night now (Evensong); day in 1h 4m of real time."), "shown, with the time");
            Eq(DefReplies.DayNight(0, null), (true, "Day/Night timer hidden."), "hidden, out of world: no time");
            var old = DefReplies.DayNight(-1, dusk);
            Check(!old.Ok && old.Text.Contains("v90", StringComparison.Ordinal) && old.Text.Contains("day in 1h 4m", StringComparison.Ordinal),
                "an engine without the window: why, and the time still");
        }
    }
}
