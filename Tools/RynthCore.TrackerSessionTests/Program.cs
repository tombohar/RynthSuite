using System;
using System.IO;
using RynthCore.Plugin.Shared;

namespace RynthCore.TrackerSessionTests;

// Tests for RynthTracker's session persistence across plugin reloads (TrackerSessionStore):
// the save format round-trips and rejects torn or foreign files, a save restores only for
// the same client process and character within 10 minutes, the reload gap is counted once
// (never lost, never doubled), and the files land, swap and clean up as intended.
// Self-contained; fails on zero assertions.
//
// Run: dotnet run -c Release  (exit 0 = pass, 1 = fail)
internal static partial class Program
{
    private static int _asserts;
    private static int _fails;

    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static void Eq<T>(T actual, T expected, string msg)
    {
        _asserts++;
        if (!Equals(actual, expected)) { _fails++; Console.WriteLine($"  [FAIL] {msg}: expected {expected}, got {actual}"); }
    }

    private static int Main()
    {
        Console.WriteLine("=== RynthTracker session tests ===");
        TestRise();
        TestRoundTrip();
        TestRoundTripNullBaselines();
        TestRejectsBadText();
        TestRejectsTornWrite();
        TestToleratesCrlfAndUnknownKeys();
        TestShouldRestore();
        TestReloadGapCountedOnce();
        TestReloadGapWithSpending();
        TestReloadsMatchContinuousPolling();
        TestNoBaselineYet();
        TestFileNames();
        TestFiles();
        TestCleanup();
        TestKillCounter();
        TestSessionStats();
        TestRynthAiSnapshot();

        Console.WriteLine($"\n{_asserts} assertions, {_fails} failed.");
        if (_asserts == 0) { Console.WriteLine("ABORT: zero assertions ran."); return 1; }
        Console.WriteLine(_fails == 0 ? "ALL TRACKER SESSION TESTS PASSED." : $"{_fails} FAILURE(S).");
        return _fails == 0 ? 0 : 1;
    }

    private static readonly DateTime Now = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One session (2026-10-13): RynthTracker shows RynthAi's session ("RynthAi.Hub" SessionStats and
    /// SessionStatsMore) when RynthAi answers; its rates are rounded, not cut off.
    /// </summary>
    private static void TestRynthAiSnapshot()
    {
        Console.WriteLine("-- RynthAi's session in the Tracker panel");
        var s = new RynthCore.Plugin.Shared.SessionStatsV1
        {
            StartUtcTicks = Now.AddHours(-2).Ticks, SessionSeconds = 7200, Xp = 5_000_000, XpPerHour = 2_499_999.6,
            Lum = 9000, LumPerHour = 4500.5, LumLast5Min = 120, PassUpTracked = 1, LumPassUp = 300, PassUpPerHour = 149.4,
            Kills = 40, KillsPerHour = 20,
        };
        var more = new RynthCore.Plugin.Shared.SessionStatsMoreV1 { XpPerKill = 125_000, Deaths = 1, MaxLevel = 0, Rad = 77, RadPerHour = 38.5 };
        var v = RynthCore.Plugin.RynthTracker.TrackerSnapshot.FromRynthAi(s, more, null);
        Eq(v.SessionStartUtc, Now.AddHours(-2), "RynthAi's session start");
        Check(v.Xp == 5_000_000 && v.Lum == 9000 && v.Kills == 40 && v.PassUpTracked && v.LumPassUp == 300, "RynthAi's totals");
        Check(v.XpPerKill == 125_000 && v.Deaths == 1 && v.Rad == 77 && !v.MaxLevel, "and the rest from SessionStatsMore");

        string json = RynthCore.Plugin.RynthTracker.TrackerSnapshot.Build(v, Now);
        Check(json.Contains("\"ss\":7200.0"), "the clock runs from RynthAi's start: " + json);
        Check(json.Contains("\"xh\":2500000") && json.Contains("\"lh\":4501") && json.Contains("\"ph\":149") && json.Contains("\"rh\":39"),
              "rates rounded, not cut off: " + json);
        Check(json.Contains("\"kt\":40") && json.Contains("\"xk\":125000") && json.Contains("\"dt\":1") && json.Contains("\"pk\":1"), "panel fields: " + json);

        // An older RynthAi (no SessionStatsMore): those fields from the Tracker's own session, else zero.
        var own = new SessionStatsView(Now.AddHours(-1), 3600, 1, 1, 1, 1, 0, 0, 0, false, 3, 3, 999, 4, 5, 6, true);
        var old = RynthCore.Plugin.RynthTracker.TrackerSnapshot.FromRynthAi(s, null, own);
        Check(old.Xp == 5_000_000 && old.XpPerKill == 999 && old.Deaths == 4 && old.Rad == 5 && old.MaxLevel,
              "older RynthAi: RynthAi's totals, the Tracker's own deaths / Radiance / XP per kill");
        var bare = RynthCore.Plugin.RynthTracker.TrackerSnapshot.FromRynthAi(s, null, null);
        Check(bare.XpPerKill == 0 && bare.Deaths == 0 && bare.Rad == 0 && !bare.MaxLevel, "and zero with no own session");

        Eq(RynthCore.Plugin.RynthTracker.TrackerSnapshot.PerHour(double.NaN), 0L, "NaN rate is 0");
        Eq(RynthCore.Plugin.RynthTracker.TrackerSnapshot.PerHour(-5), 0L, "negative rate is 0");
        Eq(RynthCore.Plugin.RynthTracker.TrackerSnapshot.PerHour(0.5), 1L, "half rounds up");
        unsafe
        {
            Eq(RynthCore.Plugin.Shared.RynthAiHubApiV1.BaseStructSize, (uint)(8 + 10 * sizeof(nint)), "the hub table's base size (10 functions)");
            // Append-only: each field added after the base sits at the offset readers check it by
            // (VaultedDungeonsEnabled, BackAwayFrom and SetPlayerCounter came after SessionStatsMore).
            RynthCore.Plugin.Shared.RynthAiHubApiV1 t = default;
            byte* at = (byte*)&t;
            Eq((int)((byte*)&t.SessionStatsMore - at), RynthCore.Plugin.Shared.RynthAiHubApiV1.SessionStatsMoreOffset, "SessionStatsMore appended right after the base");
            Eq((int)((byte*)&t.VaultedDungeonsEnabled - at), RynthCore.Plugin.Shared.RynthAiHubApiV1.VaultedDungeonsEnabledOffset, "VaultedDungeonsEnabled next");
            Eq((int)((byte*)&t.BackAwayFrom - at), RynthCore.Plugin.Shared.RynthAiHubApiV1.BackAwayFromOffset, "BackAwayFrom next");
            Eq((int)((byte*)&t.SetPlayerCounter - at), RynthCore.Plugin.Shared.RynthAiHubApiV1.SetPlayerCounterOffset, "SetPlayerCounter next");
            Eq((uint)sizeof(RynthCore.Plugin.Shared.RynthAiHubApiV1), (uint)(RynthCore.Plugin.Shared.RynthAiHubApiV1.SetPlayerCounterOffset + sizeof(nint)),
               "SetPlayerCounter is the last field (a new one appends here, with its own offset check)");
        }
    }

    private static TrackerSessionState Sample() => new()
    {
        ProcessId = 4321,
        ProcessStartUtcTicks = Now.AddHours(-5).Ticks,
        CharacterId = 0x5000_00A1,
        SavedUtcTicks = Now.Ticks,
        SessionStartUtcTicks = Now.AddHours(-3).Ticks,
        XpTotal = 123_456_789_012,
        LumTotal = 987_654,
        RadTotal = 4_200,
        KillsTotal = 812,
        DeathsTotal = 2,
        KillXp = 100_000_000_000,
        XpKills = 800,
        OpenKills = 3,
        KillWindowUntilUtcTicks = Now.AddSeconds(2).Ticks,
        MaxLevel = true,
        XpBase = 191_226_310_247,
        LumBase = 1_500_000,
        RadBase = 0,
        BankedBase = 77,
        DrawnBase = -5,
        BankLumBase = 3_900_000_000_000,
        LumOwn = 5_000,
        BankIncome = 9_000,
        OwnSinceBank = 4_000,
        RecentLum = { (Now.AddMinutes(-2).Ticks, 300), (Now.AddMinutes(-1).Ticks, 700) },
    };

    private static void SameState(TrackerSessionState a, TrackerSessionState b, string what)
    {
        Eq(a.ProcessId, b.ProcessId, what + " pid");
        Eq(a.ProcessStartUtcTicks, b.ProcessStartUtcTicks, what + " process start");
        Eq(a.CharacterId, b.CharacterId, what + " char");
        Eq(a.SavedUtcTicks, b.SavedUtcTicks, what + " saved");
        Eq(a.SessionStartUtcTicks, b.SessionStartUtcTicks, what + " start");
        Eq(a.XpTotal, b.XpTotal, what + " xp");
        Eq(a.LumTotal, b.LumTotal, what + " lum");
        Eq(a.RadTotal, b.RadTotal, what + " rad");
        Eq(a.KillsTotal, b.KillsTotal, what + " kills");
        Eq(a.DeathsTotal, b.DeathsTotal, what + " deaths");
        Eq(a.KillXp, b.KillXp, what + " killxp");
        Eq(a.XpKills, b.XpKills, what + " xpkills");
        Eq(a.OpenKills, b.OpenKills, what + " openkills");
        Eq(a.KillWindowUntilUtcTicks, b.KillWindowUntilUtcTicks, what + " killuntil");
        Eq(a.MaxLevel, b.MaxLevel, what + " maxlevel");
        Eq(a.XpBase, b.XpBase, what + " xpbase");
        Eq(a.LumBase, b.LumBase, what + " lumbase");
        Eq(a.RadBase, b.RadBase, what + " radbase");
        Eq(a.BankedBase, b.BankedBase, what + " bankedbase");
        Eq(a.DrawnBase, b.DrawnBase, what + " drawnbase");
        Eq(a.BankLumBase, b.BankLumBase, what + " bankbase");
        Eq(a.LumOwn, b.LumOwn, what + " lumown");
        Eq(a.BankIncome, b.BankIncome, what + " bankincome");
        Eq(a.OwnSinceBank, b.OwnSinceBank, what + " ownsincebank");
        Eq(string.Join(";", a.RecentLum), string.Join(";", b.RecentLum), what + " recentlum");
    }

    // First read is the baseline; rises count; a drop only moves the baseline.
    private static void TestRise()
    {
        var r = new TrackerRise();
        Eq(r.Baseline, (long?)null, "no baseline before the first read");
        Eq(r.Take(1000), 0L, "first read is the baseline");
        Eq(r.Baseline, (long?)1000, "baseline after the first read");
        Eq(r.Take(1250), 250L, "rise counts");
        Eq(r.Take(900), 0L, "drop counts nothing");
        Eq(r.Take(950), 50L, "rise after a drop counts from the drop");
        r.Clear();
        Eq(r.Take(5000), 0L, "after Clear the next read is the baseline again");
    }

    private static void TestRoundTrip()
    {
        var s = Sample();
        string text = TrackerSessionStore.Serialize(s);
        Check(text.StartsWith(TrackerSessionStore.Header + "\n", StringComparison.Ordinal), "header first");
        Check(TrackerSessionStore.TryParse(text, out var back), "round trip parses");
        SameState(back, s, "round trip");
    }

    private static void TestRoundTripNullBaselines()
    {
        var s = Sample();
        s.XpBase = null; s.LumBase = null; s.RadBase = null; s.BankedBase = null; s.DrawnBase = null;
        s.BankLumBase = null;
        s.RecentLum.Clear();
        s.MaxLevel = false;
        Check(TrackerSessionStore.TryParse(TrackerSessionStore.Serialize(s), out var back), "null baselines parse");
        SameState(back, s, "null baselines");
    }

    private static void TestRejectsBadText()
    {
        string good = TrackerSessionStore.Serialize(Sample());
        Check(!TrackerSessionStore.TryParse(null, out _), "null rejected");
        Check(!TrackerSessionStore.TryParse("", out _), "empty rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("session v1", "session v9"), out _), "other version rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("end=1\n", ""), out _), "no end marker rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("kills=812\n", ""), out _), "missing field rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("kills=812", "kills=lots"), out _), "bad number rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("maxlevel=1", "maxlevel=yes"), out _), "bad flag rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("xpbase=191226310247", "xpbase=?"), out _), "bad baseline rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("char=1342177441", "char=-1"), out _), "negative character rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("deaths=2", "deaths 2"), out _), "line without = rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("saved=" + Now.Ticks, "saved=" + long.MaxValue), out _), "ticks out of DateTime range rejected");
    }

    // Any cut before the end marker (a half-written file) must not parse.
    private static void TestRejectsTornWrite()
    {
        string good = TrackerSessionStore.Serialize(Sample());
        int endAt = good.IndexOf("end=1", StringComparison.Ordinal) + "end=1".Length;
        int parsed = 0;
        for (int len = 0; len < endAt; len++)
            if (TrackerSessionStore.TryParse(good.Substring(0, len), out _)) parsed++;
        Eq(parsed, 0, "no cut before the end marker parses");
    }

    private static void TestToleratesCrlfAndUnknownKeys()
    {
        string good = TrackerSessionStore.Serialize(Sample());
        Check(TrackerSessionStore.TryParse(good.Replace("\n", "\r\n"), out var crlf), "CRLF parses");
        SameState(crlf, Sample(), "CRLF");
        Check(TrackerSessionStore.TryParse(good.Replace("end=1", "future=thing\nend=1"), out _), "unknown key ignored");

        // A save written before the 2026-10-11 keys still carries the session on.
        string old = good;
        foreach (string key in new[] { "bankbase", "lumown", "bankincome", "ownsincebank", "recentlum" })
        {
            int at = old.IndexOf("\n" + key + "=", StringComparison.Ordinal);
            int end = old.IndexOf('\n', at + 1);
            old = old.Remove(at, end - at);
        }
        Check(TrackerSessionStore.TryParse(old, out var oldBack), "save without the 2026-10-11 keys parses");
        Eq(oldBack.XpTotal, Sample().XpTotal, "older save keeps its totals");
        Eq(oldBack.BankLumBase, (long?)null, "older save: no bank baseline");
        Eq(oldBack.RecentLum.Count, 0, "older save: no recent luminance");
        Check(!TrackerSessionStore.TryParse(good.Replace("lumown=5000", "lumown=x"), out _), "malformed new key rejected");
        Check(!TrackerSessionStore.TryParse(good.Replace("recentlum=", "recentlum=12"), out _), "malformed recent luminance rejected");
    }

    private static void TestShouldRestore()
    {
        var s = Sample();
        int pid = s.ProcessId;
        uint chr = s.CharacterId;
        long ps = s.ProcessStartUtcTicks;
        Check(TrackerSessionStore.ShouldRestore(s, pid, ps, chr, Now.AddSeconds(5)), "same client, same character, 5 s later");
        Check(TrackerSessionStore.ShouldRestore(s, pid, ps, chr, Now.AddMinutes(9).AddSeconds(59)), "9:59 later");
        Check(!TrackerSessionStore.ShouldRestore(s, pid, ps, chr, Now.AddMinutes(10).AddSeconds(1)), "10:01 later is stale");
        Check(!TrackerSessionStore.ShouldRestore(s, pid + 1, ps, chr, Now.AddSeconds(5)), "another client process");
        Check(!TrackerSessionStore.ShouldRestore(s, pid, ps, chr + 1, Now.AddSeconds(5)), "another character");
        Check(!TrackerSessionStore.ShouldRestore(s, pid, ps, 0, Now.AddSeconds(5)), "no character id");
        Check(!TrackerSessionStore.ShouldRestore(s, pid, ps + 1, chr, Now.AddSeconds(5)), "same process id, another client (reused id)");
        Check(TrackerSessionStore.ShouldRestore(s, pid, ps, chr, Now.AddSeconds(-30)), "clock 30 s behind the save: slack");
        Check(!TrackerSessionStore.ShouldRestore(s, pid, ps, chr, Now.AddMinutes(-2)), "save 2 min in the future");

        var odd = Sample();
        odd.SessionStartUtcTicks = odd.SavedUtcTicks + 1;
        Check(!TrackerSessionStore.ShouldRestore(odd, pid, ps, chr, Now.AddSeconds(5)), "session starting after its save");
    }

    // XP keeps coming in while the plugin is unloaded: the first read after the restore
    // counts the whole gap once, so the total equals the real rise since the session began.
    private static void TestReloadGapCountedOnce()
    {
        long total = 0;
        var xp = new TrackerRise();
        total += xp.Take(1_000);   // baseline
        total += xp.Take(1_100);
        total += xp.Take(1_250);

        var saved = Sample();
        saved.XpTotal = total;
        saved.XpBase = xp.Baseline;
        Check(TrackerSessionStore.TryParse(TrackerSessionStore.Serialize(saved), out var back), "gap save parses");

        // Unloaded: XP rises to 1,400. New plugin copy restores.
        long total2 = back.XpTotal;
        var xp2 = new TrackerRise();
        xp2.Restore(back.XpBase);
        total2 += xp2.Take(1_400);
        Eq(total2, 400L, "gap counted once (not lost, not doubled)");
        total2 += xp2.Take(1_400);
        Eq(total2, 400L, "a second read with no change adds nothing");
        total2 += xp2.Take(1_450);
        Eq(total2, 450L, "later rises carry on");
    }

    // Luminance spent during the gap only moves the baseline, as it would while loaded.
    private static void TestReloadGapWithSpending()
    {
        var lum = new TrackerRise();
        lum.Take(500);
        lum.Take(600);   // +100 before the save
        long? saved = lum.Baseline;

        var lum2 = new TrackerRise();
        lum2.Restore(saved);
        Eq(lum2.Take(200), 0L, "spent in the gap: nothing counted");
        Eq(lum2.Take(260), 60L, "rise after the spend counts from the new low");
    }

    // A monotonic total (experience) polled with reloads at arbitrary points ends with the
    // same session total as polling it straight through.
    private static void TestReloadsMatchContinuousPolling()
    {
        var rng = new Random(20261001);
        for (int trial = 0; trial < 200; trial++)
        {
            int reads = 5 + rng.Next(40);
            long[] values = new long[reads];
            long v = 1_000_000 + rng.Next(1_000_000);
            for (int i = 0; i < reads; i++) { v += rng.Next(3) == 0 ? 0 : rng.Next(50_000); values[i] = v; }

            long straight = 0;
            var a = new TrackerRise();
            foreach (long x in values) straight += a.Take(x);

            long reloaded = 0;
            var b = new TrackerRise();
            int savedThrough = -1;
            long savedTotal = 0;
            long? savedBase = null;
            for (int i = 0; i < reads; i++)
            {
                // Sometimes a save (after this read), sometimes a reload that skips reads
                // (the gap) and restores from the last save.
                if (rng.Next(4) == 0) { savedTotal = reloaded; savedBase = b.Baseline; savedThrough = i - 1; }
                if (rng.Next(6) == 0 && savedThrough >= 0)
                {
                    var st = Sample();
                    st.XpTotal = savedTotal;
                    st.XpBase = savedBase;
                    TrackerSessionStore.TryParse(TrackerSessionStore.Serialize(st), out var back);
                    reloaded = back.XpTotal;
                    b = new TrackerRise();
                    b.Restore(back.XpBase);
                    i += rng.Next(3);   // reads missed while unloaded
                    if (i >= reads) i = reads - 1;
                }
                reloaded += b.Take(values[i]);
            }
            if (reloaded != straight)
            {
                Eq(reloaded, straight, $"trial {trial}: reloaded total vs straight-through total");
                return;
            }
        }
        Check(true, "200 random reload trials match straight-through polling");
    }

    // Saved before anything was read: the restored copy treats its first read as the baseline.
    private static void TestNoBaselineYet()
    {
        var r = new TrackerRise();
        r.Restore(null);
        Eq(r.Baseline, (long?)null, "null restore keeps no baseline");
        Eq(r.Take(7_000), 0L, "first read after a null restore is the baseline");
        Eq(r.Take(7_100), 100L, "then rises count");
    }

    private static void TestFileNames()
    {
        string a = TrackerSessionStore.FileName(100, 0x50000001);
        Eq(a, "session-100-50000001.txt", "file name format");
        Check(a != TrackerSessionStore.FileName(101, 0x50000001), "process id in the name");
        Check(a != TrackerSessionStore.FileName(100, 0x50000002), "character id in the name");
        Check(TrackerSessionStore.DefaultDirectory().EndsWith(Path.Combine("RynthCore", "tracker"), StringComparison.OrdinalIgnoreCase),
            "default folder is RynthCore\\tracker");
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rynthtracker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TestFiles()
    {
        string dir = Path.Combine(TempDir(), "nested", "tracker");   // Save creates it
        try
        {
            var s = Sample();
            TrackerSessionStore.Save(dir, s);
            Check(TrackerSessionStore.TryLoad(dir, s.ProcessId, s.CharacterId, out var back), "saved file loads");
            SameState(back, s, "file round trip");
            Check(!TrackerSessionStore.TryLoad(dir, s.ProcessId, s.CharacterId + 1, out _), "another character finds no file");
            Check(!TrackerSessionStore.TryLoad(dir, s.ProcessId + 1, s.CharacterId, out _), "another process finds no file");

            s.KillsTotal = 900;
            TrackerSessionStore.Save(dir, s);
            TrackerSessionStore.TryLoad(dir, s.ProcessId, s.CharacterId, out back);
            Eq(back.KillsTotal, 900, "second save replaces the first");
            Eq(Directory.GetFiles(dir).Length, 1, "one file, no temp left over");

            File.WriteAllText(Path.Combine(dir, TrackerSessionStore.FileName(s.ProcessId, s.CharacterId)), "RynthTracker session v1\npid=4");
            Check(!TrackerSessionStore.TryLoad(dir, s.ProcessId, s.CharacterId, out _), "half file doesn't load");

            TrackerSessionStore.Delete(dir, s.ProcessId, s.CharacterId);
            Check(!TrackerSessionStore.TryLoad(dir, s.ProcessId, s.CharacterId, out _), "deleted (logout) file is gone");
            TrackerSessionStore.Delete(dir, s.ProcessId, s.CharacterId);
            Check(true, "deleting a missing file is fine");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, true); } catch { } }
    }

    private static void TestCleanup()
    {
        string dir = TempDir();
        try
        {
            Eq(TrackerSessionStore.Cleanup(Path.Combine(dir, "missing"), 1, Now), 0, "missing folder: nothing to do");

            void Put(int pid, uint chr, DateTime written)
            {
                var s = Sample(); s.ProcessId = pid; s.CharacterId = chr;
                TrackerSessionStore.Save(dir, s);
                File.SetLastWriteTimeUtc(Path.Combine(dir, TrackerSessionStore.FileName(pid, chr)), written);
            }
            DateTime now = DateTime.UtcNow;
            Put(10, 0xA, now);               // this client, old character (no logout seen)
            Put(10, 0xB, now);               // this client, another old character
            Put(20, 0xC, now.AddMinutes(-1));  // another live client: keep
            Put(30, 0xD, now.AddDays(-2));   // a client closed long ago: purge
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not ours");

            int removed = TrackerSessionStore.Cleanup(dir, 10, now);
            Eq(removed, 3, "own-process saves and the long-dead client's save removed");
            Check(!File.Exists(Path.Combine(dir, TrackerSessionStore.FileName(10, 0xA))), "own save A gone");
            Check(!File.Exists(Path.Combine(dir, TrackerSessionStore.FileName(10, 0xB))), "own save B gone");
            Check(File.Exists(Path.Combine(dir, TrackerSessionStore.FileName(20, 0xC))), "other live client's save kept");
            Check(!File.Exists(Path.Combine(dir, TrackerSessionStore.FileName(30, 0xD))), "2-day-old save purged");
            Check(File.Exists(Path.Combine(dir, "notes.txt")), "unrelated files left alone");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
