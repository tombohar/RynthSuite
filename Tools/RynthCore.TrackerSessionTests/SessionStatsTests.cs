using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.TrackerSessionTests;

// SessionStats (Plugins\Shared): the numbers RynthTracker and RynthAi's Mini Remote both show
// (2026-10-11). Luminance on each server kind, ACECustom pass-up, the five-minute window,
// kills / XP per kill, and a reload in the middle giving the same numbers as no reload.
internal static partial class Program
{
    private static readonly DateTime S0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static SessionSample Ilt(long xp, long avail, long bank) => new(xp, avail, bank, null, null, null);

    private static void TestSessionStats()
    {
        TestLumChatLines();
        TestIltBankLum();
        TestIltDepositExpires();
        TestChatFallbackUntilBank();
        TestAelrynthAndRetailLum();
        TestLumLast5Min();
        TestKillsAndXpPerKill();
        TestDeaths();
        TestSessionStatsReloadMatches();
    }

    private static void TestLumChatLines()
    {
        Check(SessionLumChat.TryOwn("You've banked 1,234,567 Luminance.", out long a) && a == 1_234_567, "own lum line with commas");
        Check(SessionLumChat.TryOwn("  You've banked 5 Luminance.\n", out long b) && b == 5, "leading space and newline");
        Check(!SessionLumChat.TryOwn("Bob tells you, \"You've banked 5 Luminance.\"", out _), "a quoted line is not ours");
        Check(!SessionLumChat.TryOwn("You've banked Luminance.", out _), "no amount");
        Check(SessionLumChat.TryMovedIntoBank("Deposited 2,000 luminance", out long d) && d == 2_000, "deposit line");
        Check(SessionLumChat.TryMovedIntoBank("Received 1,000 luminance from Bob", out long r) && r == 1_000, "transfer-in line");
        Check(!SessionLumChat.TryMovedIntoBank("Transferred 1,000 Luminance to Bob", out _), "transfer out is not into the bank");
        Check(!SessionLumChat.TryMovedIntoBank("No luminance available to deposit", out _), "nothing deposited");
    }

    // ACECustom: earned lum goes to the bank (9005); own kill / quest lum prints a line, pass-up doesn't;
    // withdrawals, deposits, transfers in and spending are not income.
    private static void TestIltBankLum()
    {
        var s = new SessionStats();
        s.Reset(S0);
        DateTime t = S0;
        s.Sample(Ilt(1_000, 0, 1_000_000), t);
        Eq(s.LumTotal, 0L, "ILT: first read is the baseline");
        Check(s.BankTracked, "ILT: bank tracked after the first read");

        t = t.AddSeconds(1);
        s.OnChat("You've banked 500 Luminance.", t);
        s.Sample(Ilt(1_100, 0, 1_000_500), t);
        Eq(s.LumTotal, 500L, "ILT: own kill lum counts once (chat and bank)");
        Eq(s.LumPassUp, 0L, "ILT: own lum is not pass-up");

        t = t.AddSeconds(1);
        s.Sample(Ilt(1_100, 0, 1_000_800), t);
        Eq(s.LumTotal, 800L, "ILT: silent bank rise counts");
        Eq(s.LumPassUp, 300L, "ILT: silent bank rise is pass-up");

        t = t.AddSeconds(1);
        s.Sample(Ilt(1_100, 10_000, 990_800), t);
        Eq(s.LumTotal, 800L, "ILT: a withdrawal (character up, bank down) is not income");

        t = t.AddSeconds(1);
        s.OnChat("Deposited 2,000 luminance", t);
        s.Sample(Ilt(1_100, 8_000, 992_800), t);
        Eq(s.LumTotal, 800L, "ILT: a deposit is not income");

        t = t.AddSeconds(1);
        s.OnChat("Received 1,000 luminance from Bob", t);
        s.Sample(Ilt(1_100, 8_000, 993_800), t);
        Eq(s.LumTotal, 800L, "ILT: a transfer from another player is not income");
        Eq(s.LumPassUp, 300L, "ILT: nor pass-up");

        t = t.AddSeconds(1);
        s.Sample(Ilt(1_100, 8_000, 993_750), t);
        s.Sample(Ilt(1_100, 8_000, 993_850), t.AddSeconds(1));
        Eq(s.LumTotal, 900L, "ILT: spending moves the baseline, the next rise counts");
        Eq(s.LumOwn, 500L, "ILT: own lum total");
        Eq(s.XpTotal, 100L, "ILT: experience rise");
    }

    // A deposit line with no bank rise behind it stops holding back later income after a minute.
    private static void TestIltDepositExpires()
    {
        var s = new SessionStats();
        s.Reset(S0);
        s.Sample(Ilt(1, 0, 5_000), S0);
        s.OnChat("Deposited 1,000 luminance", S0.AddSeconds(1));
        s.Sample(Ilt(1, 0, 5_000), S0.AddSeconds(2));
        s.Sample(Ilt(1, 0, 5_400), S0.AddSeconds(70));
        Eq(s.LumTotal, 400L, "an expired deposit line no longer eats income");
    }

    // "You've banked" counts only until a bank property is read; then the bank covers it.
    private static void TestChatFallbackUntilBank()
    {
        var s = new SessionStats();
        s.Reset(S0);
        s.OnChat("You've banked 400 Luminance.", S0);
        Eq(s.LumTotal, 400L, "chat counts before the bank property");
        s.Sample(Ilt(1, 0, 5_000), S0.AddSeconds(1));
        s.OnChat("You've banked 100 Luminance.", S0.AddSeconds(2));
        s.Sample(Ilt(1, 0, 5_100), S0.AddSeconds(2));
        Eq(s.LumTotal, 500L, "after the bank is read the chat line doesn't count twice");
        Eq(s.LumPassUp, 0L, "own lum after the bank read isn't pass-up");
    }

    private static void TestAelrynthAndRetailLum()
    {
        var ael = new SessionStats();
        ael.Reset(S0);
        ael.Sample(new SessionSample(1, 1_000, null, 0, 0, 0), S0);
        ael.Sample(new SessionSample(1, 1_100, null, 0, 0, 0), S0.AddSeconds(1));
        Eq(ael.LumTotal, 100L, "Aelrynth: character rise counts");
        ael.Sample(new SessionSample(1, 1_150, null, 0, 50, 0), S0.AddSeconds(2));
        Eq(ael.LumTotal, 100L, "Aelrynth: drawn from the bank is not earned");
        ael.Sample(new SessionSample(1, 1_150, null, 200, 50, 7), S0.AddSeconds(3));
        Eq(ael.LumTotal, 300L, "Aelrynth: auto-banked counts");
        ael.OnChat("You've banked 999 Luminance.", S0.AddSeconds(4));
        Eq(ael.LumTotal, 300L, "Aelrynth: chat ignored once its bank totals are read");
        Eq(ael.RadTotal, 7L, "Aelrynth: Radiance rise");
        Check(!ael.BankTracked, "Aelrynth: no ACECustom bank, pass-up unknown");

        var retail = new SessionStats();
        retail.Reset(S0);
        retail.Sample(new SessionSample(1, 0, null, null, null, null), S0);
        retail.Sample(new SessionSample(1, 250, null, null, null, null), S0.AddSeconds(1));
        retail.Sample(new SessionSample(1, 50, null, null, null, null), S0.AddSeconds(2));
        retail.Sample(new SessionSample(1, 80, null, null, null, null), S0.AddSeconds(3));
        Eq(retail.LumTotal, 280L, "retail: rises count, spending doesn't");
    }

    private static void TestLumLast5Min()
    {
        var s = new SessionStats();
        s.Reset(S0);
        s.Sample(Ilt(1, 0, 0), S0);
        s.Sample(Ilt(1, 0, 100), S0.AddSeconds(10));
        s.Sample(Ilt(1, 0, 300), S0.AddMinutes(3));
        Eq(s.LumLast5Min(S0.AddMinutes(3)), 300L, "both gains inside five minutes");
        Eq(s.LumLast5Min(S0.AddMinutes(6)), 200L, "the first gain left the window");
        Eq(s.LumLast5Min(S0.AddMinutes(9)), 0L, "both gone");
        Eq(s.LumTotal, 300L, "the session total keeps them");
        var v = s.View(S0.AddMinutes(30));
        Eq(v.LumLast5Min, 0L, "view: last five minutes");
        Check(Math.Abs(v.LumPerHour - 600) < 0.001, "view: 300 lum in half an hour = 600/h");
    }

    private static void TestKillsAndXpPerKill()
    {
        var s = new SessionStats();
        s.Reset(S0);
        s.Sample(Ilt(1_000, 0, 0), S0);
        s.OnKillNotice("You killed the Drudge Skulker!", S0.AddSeconds(1));
        s.Sample(Ilt(1_600, 0, 0), S0.AddSeconds(2));
        s.Sample(Ilt(1_600, 0, 0), S0.AddSeconds(5));   // window closed
        Eq(s.Kills, 1, "notice kill");
        Eq(s.XpPerKill, 600L, "XP in the kill window belongs to it");

        // A multi-target volley hits two monsters; one dies with no notice and leaves a corpse.
        DateTime v = S0.AddSeconds(10);
        s.Sample(Ilt(2_000, 0, 0), v);                  // unattributed XP just before the corpse
        s.OnChat("You blast Drudge Slinker for 153 points with Whirling Blade VII.", v);
        s.OnChat("You blast Drudge Prowler for 160 points with Whirling Blade VII.", v.AddMilliseconds(100));
        var result = s.OnCorpse("Drudge Slinker", v.AddSeconds(3));
        Check(result == KillCounter.CorpseResult.Counted, "corpse of a multi-target victim counts");
        Eq(s.Kills, 2, "corpse kill joins the kills");
        Eq(s.XpPerKill, 500L, "corpse kill takes the XP just before it: (600 + 400) / 2");

        s.OnPlayerHealth(300, 300);
        s.OnPlayerHealth(0, 300);
        Eq(s.View(S0.AddHours(1)).Deaths, 1, "death");
        Eq(s.Kills, 2, "a death is no kill");
    }

    // Deaths (2026-10-30): the vitals and NumDeaths, each death once.

    /// <summary>One tick of SessionStatsTracker.CheckDeaths: the vitals, then the counter (null = not readable).</summary>
    private static void Read(SessionStats s, uint health, uint max, int? numDeaths)
    {
        s.OnPlayerHealth(health, max);
        if (numDeaths is int n) s.OnDeathCount(n);
    }

    /// <summary>Capture, save text, parse, apply: a plugin reload in the middle of the session.</summary>
    private static SessionStats Reload(SessionStats s, DateTime t)
    {
        var st = Sample();
        s.CaptureInto(st, t);
        Check(TrackerSessionStore.TryParse(TrackerSessionStore.Serialize(st), out var back), "deaths: the save parses");
        var next = new SessionStats();
        next.ApplyFrom(back);
        return next;
    }

    private static void TestDeaths()
    {
        TestDeathSequence();
        TestDeathCounterOrder();
        TestDeathsWithoutCounter();
        TestDeathsWithoutVitals();
        TestDeathReset();
        TestDeathsAcrossReload();
    }

    // A real death as the server sends it (ACE Player_Death: OnDeath sends the victim notice, then Die
    // sends health 0 and NumDeaths in one packet; the corpse and the portal follow the death animation,
    // and 3 s into portal space health comes back at 75 %), read tick by tick as the tracker reads it.
    private static void TestDeathSequence()
    {
        var s = new SessionStats();
        s.Reset(S0);
        DateTime t = S0;

        // Login: 0/0 until the player's vitals land, and a moment with the max but no health yet.
        Read(s, 0, 0, null);
        Read(s, 0, 0, 4);
        Read(s, 0, 360, 4);
        Eq(s.Deaths, 0, "login: the 0/0 packet and 0/max before the first real reading are no death");
        Read(s, 360, 360, 4);
        Eq(s.Deaths, 0, "login: the death counter's first read is its baseline");

        // A fight: health falls and the character lands a kill. Neither is a death.
        Read(s, 120, 360, 4);
        s.OnKillNotice("You killed the Drudge Skulker!", t.AddSeconds(1));
        Read(s, 40, 360, 4);
        Eq(s.Deaths, 0, "a kill notice and low health are no death");
        Eq(s.Kills, 1, "the kill counts");

        // Die(): health 0 and NumDeaths 5 in one packet, read in the same tick.
        Read(s, 0, 360, 5);
        Eq(s.Deaths, 1, "health 0 and NumDeaths +1: one death");
        for (int i = 0; i < 300; i++) Read(s, 0, 360, 5);   // ~5 s dead: the animation, the corpse, the portal
        Eq(s.Deaths, 1, "still one through the death animation and the portal");
        Read(s, 270, 360, 5);                               // 3 s into portal space: 75 % health
        Read(s, 360, 360, 5);
        Eq(s.Deaths, 1, "alive again: still one");
        Eq(s.Kills, 1, "a death is no kill");

        // The next death counts again.
        Read(s, 200, 360, 5);
        Read(s, 0, 360, 6);
        Read(s, 0, 360, 6);
        Read(s, 270, 360, 6);
        Eq(s.Deaths, 2, "a second death");
        Eq(s.View(S0.AddHours(1)).Deaths, 2, "the view shows them");
    }

    // The two readings of one death in either order: the pump can read the vitals just before the
    // packet and the counter just after it, or the counter can come a tick after the health.
    private static void TestDeathCounterOrder()
    {
        var s = new SessionStats();
        s.Reset(S0);
        Read(s, 360, 360, 10);

        Read(s, 90, 360, 11);           // the vitals read before the packet, the counter after it
        Eq(s.Deaths, 1, "counter first: counted at once");
        Read(s, 0, 360, 11);
        Read(s, 0, 360, 11);
        Eq(s.Deaths, 1, "its health 0 a tick later is the same death");
        Read(s, 270, 360, 11);

        Read(s, 0, 360, 11);            // health first, the counter a tick behind
        Eq(s.Deaths, 2, "health first: counted at once");
        Read(s, 0, 360, 12);
        Eq(s.Deaths, 2, "its counter rise a tick later is the same death");
        Read(s, 270, 360, 12);
        Eq(s.Deaths, 2, "two deaths, each once");
    }

    // A character that never died has no NumDeaths until its first death; a server may never send it.
    private static void TestDeathsWithoutCounter()
    {
        var fresh = new SessionStats();
        fresh.Reset(S0);
        Read(fresh, 300, 300, null);
        Read(fresh, 0, 300, 1);         // the counter appears with the first death: that read is its baseline
        Eq(fresh.Deaths, 1, "first death of a new character: health counts it");
        Read(fresh, 225, 300, 1);
        Read(fresh, 0, 300, 2);
        Read(fresh, 225, 300, 2);
        Eq(fresh.Deaths, 2, "its second death: health and the counter, once");

        var none = new SessionStats();
        none.Reset(S0);
        for (int d = 0; d < 3; d++)
        {
            Read(none, 300, 300, null);
            Read(none, 0, 300, null);
            Read(none, 0, 300, null);
        }
        Read(none, 225, 300, null);
        Eq(none.Deaths, 3, "no death counter from the server: each health 0 counts once");
    }

    // An engine without the vitals call: the counter alone, and two deaths between two reads.
    private static void TestDeathsWithoutVitals()
    {
        var s = new SessionStats();
        s.Reset(S0);
        s.OnDeathCount(4);
        s.OnDeathCount(5);
        s.OnDeathCount(5);
        Eq(s.Deaths, 1, "counter only: a rise is a death");
        s.OnDeathCount(7);
        Eq(s.Deaths, 3, "counter only: a rise of two is two deaths");
        s.OnDeathCount(3);
        s.OnDeathCount(4);
        Eq(s.Deaths, 4, "a drop only moves the baseline");
    }

    // Reset while dead: that death was the old session's.
    private static void TestDeathReset()
    {
        var s = new SessionStats();
        s.Reset(S0);
        Read(s, 360, 360, 4);
        Read(s, 0, 360, 5);
        Eq(s.Deaths, 1, "reset: a death first");
        s.Reset(S0.AddMinutes(1));
        Read(s, 0, 360, 5);
        Read(s, 270, 360, 5);
        Eq(s.Deaths, 0, "reset while dead: that death stays in the old session");
        Read(s, 0, 360, 6);
        Eq(s.Deaths, 1, "the next death counts");
    }

    // Plugin reloads around a death (the session save: SessionStats.CaptureInto / ApplyFrom).
    private static void TestDeathsAcrossReload()
    {
        DateTime t = S0.AddMinutes(5);

        // Reloaded while dead, after the death was counted: not counted again.
        var a = new SessionStats();
        a.Reset(S0);
        Read(a, 360, 360, 4);
        Read(a, 0, 360, 5);
        a = Reload(a, t);
        Read(a, 0, 360, 5);
        Read(a, 270, 360, 5);
        Eq(a.Deaths, 1, "reload while dead: still one");
        Read(a, 0, 360, 6);
        Eq(a.Deaths, 2, "and the next death after the reload counts");

        // Health 0 counted, then the reload before the counter was read: its rise is the same death.
        var b = new SessionStats();
        b.Reset(S0);
        Read(b, 360, 360, 4);
        Read(b, 0, 360, 4);
        b = Reload(b, t);
        Read(b, 0, 360, 5);
        Read(b, 270, 360, 5);
        Eq(b.Deaths, 1, "reload between the health and the counter: still one");

        // Died while the plugin was unloaded: the counter's saved baseline counts it.
        var c = new SessionStats();
        c.Reset(S0);
        Read(c, 360, 360, 4);
        c = Reload(c, t);
        Read(c, 0, 360, 5);
        Read(c, 0, 360, 5);
        Read(c, 270, 360, 5);
        Eq(c.Deaths, 1, "died during the reload: counted once, from the counter");

        // Died and came back while the plugin was unloaded.
        var d = new SessionStats();
        d.Reset(S0);
        Read(d, 360, 360, 4);
        d = Reload(d, t);
        Read(d, 270, 360, 5);
        Eq(d.Deaths, 1, "died and revived during the reload: counted from the counter");

        // An engine reload: the vitals start at 0/0 and can read 0/max before the health is seeded.
        var e = new SessionStats();
        e.Reset(S0);
        Read(e, 360, 360, 4);
        e = Reload(e, t);
        Read(e, 0, 0, 4);
        Read(e, 0, 360, 4);
        Read(e, 360, 360, 4);
        Eq(e.Deaths, 0, "engine reload: 0/max before the health is seeded is no death");
    }

    // A reload (capture, save text, parse, apply) mid-session gives the same numbers as none.
    private static void TestSessionStatsReloadMatches()
    {
        SessionSample[] reads =
        {
            Ilt(1_000, 0, 10_000), Ilt(1_500, 0, 10_400), Ilt(2_000, 0, 10_900),
            Ilt(2_200, 500, 10_400), Ilt(2_900, 500, 11_600), Ilt(3_000, 500, 12_000),
        };
        string?[] chat = { null, "You've banked 400 Luminance.", null, null, "You've banked 200 Luminance.", null };

        var straight = new SessionStats();
        straight.Reset(S0);
        for (int i = 0; i < reads.Length; i++)
        {
            DateTime t = S0.AddMinutes(i);
            straight.OnChat(chat[i], t);
            straight.Sample(reads[i], t);
        }

        var a = new SessionStats();
        a.Reset(S0);
        SessionStats current = a;
        for (int i = 0; i < reads.Length; i++)
        {
            DateTime t = S0.AddMinutes(i);
            if (i == 3)
            {
                var st = Sample();
                current.CaptureInto(st, t);
                Check(TrackerSessionStore.TryParse(TrackerSessionStore.Serialize(st), out var back), "stats save parses");
                current = new SessionStats();
                current.ApplyFrom(back);
            }
            current.OnChat(chat[i], t);
            current.Sample(reads[i], t);
        }

        DateTime end = S0.AddMinutes(reads.Length);
        var x = straight.View(end);
        var y = current.View(end);
        Eq(y.Xp, x.Xp, "reload: XP");
        Eq(y.Lum, x.Lum, "reload: luminance");
        Eq(y.LumPassUp, x.LumPassUp, "reload: pass-up");
        Eq(y.LumLast5Min, x.LumLast5Min, "reload: last five minutes");
        Eq(y.SessionStartUtc, x.SessionStartUtc, "reload: session start");
        Eq(x.Lum, 2_500L, "lum: bank rises 400 + 500 + 1,200 + 400; the withdrawal isn't income");
        Eq(x.LumPassUp, 1_900L, "pass-up: bank income 2,500 minus own 600");
    }
}
