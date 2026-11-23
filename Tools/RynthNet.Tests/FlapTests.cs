using RynthCore.Plugin.RynthNet.Transport;

namespace RynthNet.Tests;

// R-20261122-193027-b795: muling, another client hung up within a few ms of every connect and was
// dialed again each second, a chat line each way. A dialed link that dies young now waits before the
// next dial: 2 s, then doubling, at most a minute.
internal static partial class Program
{
    private static void FlapChecks()
    {
        Check(FlapBackoff.DelayMs(0) == 0, "no flaps: no wait");
        Check(FlapBackoff.DelayMs(1) == 2_000, "first: 2 s");
        Check(FlapBackoff.DelayMs(2) == 4_000, "second: 4 s");
        Check(FlapBackoff.DelayMs(3) == 8_000, "third: 8 s");
        Check(FlapBackoff.DelayMs(6) == 60_000, "sixth: capped at a minute");
        Check(FlapBackoff.DelayMs(50) == 60_000, "many: still a minute (no overflow)");
        Check(FlapBackoff.FlapLifeMs < FlapBackoff.StableLifeMs, "a link counts as flapping only well before it counts as stable");
    }
}
