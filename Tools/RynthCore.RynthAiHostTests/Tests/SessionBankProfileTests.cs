using System.IO;
using System.Threading;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The session's luminance from the active server profile's bank value (SessionStatsTracker: RynthAi's
// Mini Remote stats, which RynthTracker shows; 2026-11-02). A server that keeps earned luminance in its
// bank where the client can't read it has its server plugin publish "bankLuminance" (a /b every minute);
// its rises are Lum/hr, a drop (spending) isn't negative, and ACECustom's own bank property (9005) wins
// when it is readable. Without the value nothing changes.
internal static class SessionBankProfileTests
{
    private const uint Player = 0x50000D02;
    private const uint TotalXp = 1;
    private const uint BankedLuminance = 9005;
    private const string Tag = "[RynthAi] Mini Remote stats:";

    public static void Register(Runner r)
    {
        r.Add("session stats: the server profile's bank luminance rises are Lum/hr, a drop isn't negative", BankRises);
        r.Add("session stats: without a bank value in the profile, luminance stays as before", NoValue);
        r.Add("session stats: ACECustom's bank property wins over the profile's bank value", PropertyWins);
        r.Add("session stats: the profile's bank baseline carries over a reload", AcrossReload);
    }

    private static string Folder(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "session-bank", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return dir;
    }

    private static RynthCoreHost Create(double? bank)
    {
        FakeHost.Reset();
        FakeServerState.Reset();
        FakeServerState.Install();
        var host = FakeHost.Create(Player);
        FakeHost.Quads[(Player, TotalXp)] = 1_000_000;
        FakeServerState.StateJson = FakeServerState.State("testsrv", "Test Server", "RynthTestSrv", "Test World");
        SetBank(bank);
        return host;
    }

    private static void SetBank(double? bank)
        => FakeServerState.ProfileJson = bank is double b
            ? FakeServerState.Profile("testsrv", ("bankLuminance", b))
            : FakeServerState.Profile("testsrv", ("difficultyTier", 0));

    private static SessionStatsTracker LogIn(RynthCoreHost host, string dir)
    {
        var t = new SessionStatsTracker(host, Tag, dir) { ProfileIntervalMs = 0 };
        t.OnLoginComplete();
        t.Tick();
        return t;
    }

    /// <summary>The next poll (the tracker reads every 0.5 s on the real clock).</summary>
    private static void NextPoll(SessionStatsTracker t)
    {
        Thread.Sleep(550);
        t.Tick();
    }

    private static void BankRises()
    {
        try
        {
            var host = Create(1_000_000);
            var t = LogIn(host, Folder("rises"));
            Check.Eq(t.View.Lum, 0L, "the first reading is the baseline");
            SetBank(1_250_000);
            NextPoll(t);
            Check.Eq(t.View.Lum, 250_000L, "a rise is earned luminance");
            Check.True(t.View.LumPerHour > 0, "Lum/hr above 0");
            NextPoll(t);
            Check.Eq(t.View.Lum, 250_000L, "the same reading again adds nothing");
            SetBank(200_000);                          // spent from the bank
            NextPoll(t);
            Check.Eq(t.View.Lum, 250_000L, "a drop isn't counted (nor negative)");
            SetBank(260_000);
            NextPoll(t);
            Check.Eq(t.View.Lum, 310_000L, "the next rise counts from the drop");
            Check.Eq(t.View.LumLast5Min, 310_000L, "and in the last five minutes");
            t.OnLogout();
        }
        finally { FakeHost.Reset(); FakeServerState.Reset(); }
    }

    private static void NoValue()
    {
        try
        {
            var host = Create(null);
            FakeHost.Quads[(Player, 6)] = 5_000;       // AvailableLuminance, as on retail ACE
            var t = LogIn(host, Folder("none"));
            FakeHost.Quads[(Player, 6)] = 7_000;
            NextPoll(t);
            Check.Eq(t.View.Lum, 2_000L, "the character's luminance counts as before");
            Check.False(t.View.PassUpTracked, "no bank is tracked");
            t.OnLogout();
        }
        finally { FakeHost.Reset(); FakeServerState.Reset(); }
    }

    private static void PropertyWins()
    {
        try
        {
            var host = Create(50);
            FakeHost.Quads[(Player, BankedLuminance)] = 1_000_000;
            var t = LogIn(host, Folder("property"));
            SetBank(900_000);
            FakeHost.Quads[(Player, BankedLuminance)] = 1_000_400;
            NextPoll(t);
            Check.Eq(t.View.Lum, 400L, "9005's rise counts, the profile's value is not used");
            t.OnLogout();
        }
        finally { FakeHost.Reset(); FakeServerState.Reset(); }
    }

    // A hot reload is a Shutdown (the save) and a new copy's OnLoginComplete (the restore).
    private static void AcrossReload()
    {
        try
        {
            var host = Create(1_000_000);
            string dir = Folder("reload");
            var a = LogIn(host, dir);
            SetBank(1_100_000);
            NextPoll(a);
            Check.Eq(a.View.Lum, 100_000L, "before the reload");
            a.Shutdown();
            SetBank(1_150_000);                        // earned while the plugin was unloaded
            var b = LogIn(host, dir);
            NextPoll(b);
            Check.Eq(b.View.Lum, 150_000L, "the gap's luminance counts once after the reload");
            b.OnLogout();
        }
        finally { FakeHost.Reset(); FakeServerState.Reset(); }
    }
}
