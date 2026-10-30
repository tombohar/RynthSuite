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

        s.OnPlayerDied();
        Eq(s.View(S0.AddHours(1)).Deaths, 1, "death");
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
