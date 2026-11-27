// Trade Shop: how many tells it reads and sends. A shop that answers every tell with a price sheet
// can be made to flood chat (and get the seller muted), so (time passed in, pure):
//   * per buyer: one command every 2 s; at most 20 lines a minute read; past that, or after 5 too-fast
//     commands in a minute, the buyer is ignored for 2 minutes (told once);
//   * per buyer at most 12 lines a minute sent, and at most 30 lines a minute sent in all;
//   * the inbound and outbound queues are capped (TradeShopManager drops what doesn't fit).
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12) as a new part: his shop had no limits.
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Trade;

public sealed class TradeShopTellLimiter
{
    public const long MinCommandGapMs = 2_000;
    public const int MaxInLinesPerMinute = 20;
    public const int SpamStrikes = 5;
    public const long SpamIgnoreMs = 120_000;
    public const int MaxOutPerBuyerPerMinute = 12;
    public const int MaxOutPerMinute = 30;
    public const int MaxInQueue = 20;
    public const int MaxOutQueue = 40;
    private const long MinuteMs = 60_000;
    private const int MaxBuyers = 200;

    public enum Verdict
    {
        /// <summary>Handle it.</summary>
        Accept,
        /// <summary>Within 2 s of their last command: dropped.</summary>
        TooFast,
        /// <summary>The buyer is being ignored: dropped.</summary>
        Ignored,
        /// <summary>This line started the ignore: dropped, and the buyer is told once.</summary>
        NowIgnored,
    }

    private sealed class Buyer
    {
        public long LastCommandAt = long.MinValue / 2;
        public long IgnoredUntil = long.MinValue / 2;
        public long LastSeen;
        public readonly Queue<long> InLines = new();
        public readonly Queue<long> Strikes = new();
        public readonly Queue<long> OutLines = new();
    }

    private readonly Dictionary<string, Buyer> _buyers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<long> _out = new();

    /// <summary>A tell from <paramref name="buyer"/> arrived at <paramref name="now"/> (ms).</summary>
    public Verdict OnIncoming(string buyer, long now)
    {
        Buyer b = Get(buyer, now);
        if (now < b.IgnoredUntil) return Verdict.Ignored;
        Trim(b.InLines, now);
        Trim(b.Strikes, now);
        b.InLines.Enqueue(now);
        bool tooFast = now - b.LastCommandAt < MinCommandGapMs;
        if (tooFast) b.Strikes.Enqueue(now);
        if (b.InLines.Count > MaxInLinesPerMinute || b.Strikes.Count >= SpamStrikes)
        {
            b.IgnoredUntil = now + SpamIgnoreMs;
            b.InLines.Clear();
            b.Strikes.Clear();
            return Verdict.NowIgnored;
        }
        if (tooFast) return Verdict.TooFast;
        b.LastCommandAt = now;
        return Verdict.Accept;
    }

    /// <summary>True when <paramref name="buyer"/> is being ignored now.</summary>
    public bool IsIgnored(string buyer, long now) => _buyers.TryGetValue(buyer, out Buyer? b) && now < b.IgnoredUntil;

    /// <summary>May one line go to <paramref name="to"/> now? True takes the slot (the caller sends it).</summary>
    public bool TryTakeOut(string to, long now, bool overBuyerCap = false)
    {
        Trim(_out, now);
        if (_out.Count >= MaxOutPerMinute) return false;
        Buyer b = Get(to, now);
        Trim(b.OutLines, now);
        if (!overBuyerCap && b.OutLines.Count >= MaxOutPerBuyerPerMinute) return false;
        _out.Enqueue(now);
        b.OutLines.Enqueue(now);
        return true;
    }

    /// <summary>Lines sent in the last minute (all buyers).</summary>
    public int SentLastMinute(long now) { Trim(_out, now); return _out.Count; }

    public void Clear() { _buyers.Clear(); _out.Clear(); }

    private Buyer Get(string name, long now)
    {
        string key = (name ?? "").Trim();
        if (!_buyers.TryGetValue(key, out Buyer? b))
        {
            if (_buyers.Count >= MaxBuyers) Forget(now);
            b = new Buyer();
            _buyers[key] = b;
        }
        b.LastSeen = now;
        return b;
    }

    // Too many buyers remembered: drop those not ignored and not seen for a minute, else the oldest.
    private void Forget(long now)
    {
        var stale = new List<string>();
        foreach (var kv in _buyers)
            if (now >= kv.Value.IgnoredUntil && now - kv.Value.LastSeen > MinuteMs) stale.Add(kv.Key);
        if (stale.Count == 0)
        {
            string? oldest = null;
            long at = long.MaxValue;
            foreach (var kv in _buyers)
                if (kv.Value.LastSeen < at) { at = kv.Value.LastSeen; oldest = kv.Key; }
            if (oldest != null) stale.Add(oldest);
        }
        foreach (string k in stale) _buyers.Remove(k);
    }

    private static void Trim(Queue<long> q, long now)
    {
        while (q.Count > 0 && now - q.Peek() >= MinuteMs) q.Dequeue();
    }
}
