using System;

namespace RynthCore.Plugin.RynthNet.Transport;

/// <summary>
/// A dialed peer that drops right after connecting (R-20261122-193027-b795): muling, another client
/// hung up within a few ms of each connect and the next scan (1 s) dialed it again, a connect and a
/// disconnect every second. A link that dies within <see cref="FlapLifeMs"/> of its connect waits
/// before the next dial, <see cref="FirstDelayMs"/> then doubling to <see cref="MaxDelayMs"/>; one that
/// lasts <see cref="StableLifeMs"/> clears the count. Pure (the tests run it).
/// </summary>
internal static class FlapBackoff
{
    public const long FlapLifeMs = 10_000, StableLifeMs = 30_000, FirstDelayMs = 2_000, MaxDelayMs = 60_000;

    /// <summary>The wait before redialing after <paramref name="flaps"/> short-lived links in a row.</summary>
    public static long DelayMs(int flaps)
        => flaps <= 0 ? 0 : Math.Min(MaxDelayMs, FirstDelayMs << Math.Min(flaps - 1, 10));
}
