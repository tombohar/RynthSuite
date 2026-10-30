using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// RynthAi's lookup of a server plugin's hub table (MiniRemote/ServerHubClient.Tick, IltClient until
// 2026-10-20: the active server's owner under "RynthServer.Hub", else RynthILT's "RynthILT.Hub"). Until
// 0.5.60 the "never looked" sentinel was long.MinValue, "now - long.MinValue" overflowed to a
// negative number, the recheck gate always said "not due", and RynthAi never found RynthILT:
// no ILT tabs, sections or feature bits in the Mini Remote on Infinite Leaftide.
internal static class ServerHubClientLookupTests
{
    public static void Register(Runner r)
    {
        r.Add("ILT client: the first Tick (and the first after Reset) looks RynthILT up", FirstLook);
        r.Add("ILT client: a live table is kept until the recheck interval", LiveTableKept);
        r.Add("ILT client: no table retries every interval; a shut-down table at once", RetryRules);
    }

    private static void FirstLook()
    {
        // Any clock value, including the large tick counts of a long-running PC.
        foreach (long now in new[] { 0L, 1_000L, 5_000_000L, long.MaxValue / 2 })
            Check.True(ServerHubClient.LookupDue(checkedOnce: false, checkedAt: 0, now, haveTable: false, tableLive: false),
                $"never looked: due at now={now}");
    }

    private static void LiveTableKept()
    {
        Check.False(ServerHubClient.LookupDue(true, checkedAt: 10_000, now: 11_000, haveTable: true, tableLive: true), "live table within the interval");
        Check.True(ServerHubClient.LookupDue(true, checkedAt: 10_000, now: 13_000, haveTable: true, tableLive: true), "live table after the interval");
    }

    private static void RetryRules()
    {
        Check.False(ServerHubClient.LookupDue(true, checkedAt: 10_000, now: 12_999, haveTable: false, tableLive: false), "no table: waits the interval");
        Check.True(ServerHubClient.LookupDue(true, checkedAt: 10_000, now: 13_000, haveTable: false, tableLive: false), "no table: looks again after it");
        Check.True(ServerHubClient.LookupDue(true, checkedAt: 10_000, now: 10_001, haveTable: true, tableLive: false), "Version 0 (RynthILT shut down): looks again at once");
    }
}
