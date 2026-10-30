using System;
using System.Globalization;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthTracker;

/// <summary>
/// The Tracker panel's snapshot JSON, and RynthAi's session turned into the same view (2026-10-13:
/// one session for every tracker). Pure, so RynthCore.TrackerSessionTests compiles it in directly.
/// </summary>
internal static class TrackerSnapshot
{
    public const string Empty =
        "{\"ss\":0.0,\"xt\":0,\"xh\":0,\"lt\":0,\"lh\":0,\"l5\":0,\"pt\":0,\"ph\":0,\"pk\":0,"
        + "\"kt\":0,\"kh\":0.0,\"xk\":0,\"dt\":0,\"rt\":0,\"rh\":0,\"ml\":0}";

    /// <summary>
    /// RynthAi's session as the panel's view. The fields an older RynthAi doesn't send (no
    /// SessionStatsMore) come from the Tracker's own session when it runs, else zero.
    /// </summary>
    public static SessionStatsView FromRynthAi(SessionStatsV1 s, SessionStatsMoreV1? more, SessionStatsView? own)
    {
        var start = new DateTime(Math.Clamp(s.StartUtcTicks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
        long xpPerKill = more?.XpPerKill ?? own?.XpPerKill ?? 0;
        int deaths = more?.Deaths ?? own?.Deaths ?? 0;
        long rad = more?.Rad ?? own?.Rad ?? 0;
        double radPerHour = more?.RadPerHour ?? own?.RadPerHour ?? 0;
        bool maxLevel = more is { } m ? m.MaxLevel != 0 : own?.MaxLevel ?? false;
        return new SessionStatsView(start, s.SessionSeconds, s.Xp, s.XpPerHour, s.Lum, s.LumPerHour, s.LumLast5Min,
            s.LumPassUp, s.PassUpPerHour, s.PassUpTracked != 0, (int)Math.Clamp(s.Kills, 0, int.MaxValue), s.KillsPerHour,
            xpPerKill, deaths, rad, radPerHour, maxLevel);
    }

    /// <summary>A rate per hour as a whole number: rounded (not cut off), never negative or NaN.</summary>
    public static long PerHour(double v) => double.IsFinite(v) && v > 0 ? (long)Math.Round(v, MidpointRounding.AwayFromZero) : 0;

    /// <summary>The panel's JSON. The session clock runs between polls; the totals and rates are the poll's.</summary>
    public static string Build(SessionStatsView v, DateTime now)
    {
        double ss = Math.Max(0, (now - v.SessionStartUtc).TotalSeconds);
        CultureInfo ic = CultureInfo.InvariantCulture;
        return "{\"ss\":"  + ss.ToString("F1", ic)
             + ",\"xt\":"  + v.Xp.ToString(ic)
             + ",\"xh\":"  + PerHour(v.XpPerHour).ToString(ic)
             + ",\"lt\":"  + v.Lum.ToString(ic)
             + ",\"lh\":"  + PerHour(v.LumPerHour).ToString(ic)
             + ",\"l5\":"  + v.LumLast5Min.ToString(ic)              // luminance of the last five minutes
             + ",\"pt\":"  + v.LumPassUp.ToString(ic)                // allegiance pass-up (ACECustom bank)
             + ",\"ph\":"  + PerHour(v.PassUpPerHour).ToString(ic)
             + ",\"pk\":"  + (v.PassUpTracked ? 1 : 0)               // 1 = the bank is read, pass-up is known
             + ",\"kt\":"  + v.Kills.ToString(ic)
             + ",\"kh\":"  + v.KillsPerHour.ToString("F1", ic)
             + ",\"xk\":"  + v.XpPerKill.ToString(ic)                // your kills only
             + ",\"dt\":"  + v.Deaths.ToString(ic)
             + ",\"rt\":"  + v.Rad.ToString(ic)                      // Radiance (Aelrynth); 0 elsewhere
             + ",\"rh\":"  + PerHour(v.RadPerHour).ToString(ic)
             + ",\"ml\":"  + (v.MaxLevel ? 1 : 0)                    // at the level cap: experience can't rise
             + "}";
    }
}
