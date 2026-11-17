using System;
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthOracle.Tests
{
    // R-20261110-155059-e947 (2026-11-13): every daily quest showed "ready" hours early; the server
    // turned one down with "You may complete this quest again in 2h 50m 42s." /myquests had been read
    // once, at a login 34 hours before, so a quest done since kept its older stamp. The store now asks
    // for a fresh read when the server turns a quest down or the flags on screen are old.
    internal static partial class Program
    {
        private static void ReadyEarly()
        {
            DateTime now = DateTime.UtcNow;   // the store stamps its reads with the real clock
            long ms = 1_000_000;
            long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

            // The login read: the daily (20 h timer) was last done 30 h before. Ready, rightly, then.
            var store = new QuestFlagStore();
            Check(!store.NeedsReread(now, timersInUse: true), "ready-early: no re-read before the first read (the login read does that)");
            store.OnChatLine($"dailyquesttimer - 5 solves ({Unix(now.AddHours(-30))})\"Daily\" -1 72000", ms);
            store.Tick(ms + 2_000);
            Check(store.TryGet("dailyquesttimer", out QuestFlag before) && before.Ready(now), "ready-early: the login read has the daily ready");
            Check(!store.NeedsReread(now.AddMinutes(5), timersInUse: true), "ready-early: fresh flags on screen aren't re-read");
            Check(!store.NeedsReread(now.AddHours(5), timersInUse: false), "ready-early: old flags nobody looks at aren't re-read");
            Check(store.NeedsReread(now.AddMinutes(11), timersInUse: true), "ready-early: flags over 10 min old on screen are re-read");

            // Hours later the player turns it in and the server says no (they did it again after the
            // login read). The pasted lines, with the chat timestamp.
            DateTime said = now.AddMinutes(5);
            ms += 300_000;
            Check(store.OnChatLine("9:45:47 You have solved this quest too recently!", ms, said) == MyQuestsLine.TooRecent,
                  "ready-early: 'solved this quest too recently' is recognised");
            Check(store.OnChatLine("9:45:47 You may complete this quest again in 2h 50m 42s.", ms + 1, said) == MyQuestsLine.TooRecent,
                  "ready-early: 'again in' is recognised");
            TimeSpan wait = new(2, 50, 42);
            Check(store.LastServerWait is { } w && w.Left == wait && w.AtUtc == said, "ready-early: the server's wait is kept: " + store.LastServerWait);
            Check(store.RereadWanted && store.NeedsReread(said, timersInUse: false), "ready-early: the turn-down asks for a fresh read, on screen or not");
            Check(store.WaitingAbout(wait, said, TimeSpan.FromMinutes(2)).Count == 0, "ready-early: the stale flags have no quest that long from ready");

            // The fresh read: completed 20 h - 2:50:42 before the server's line.
            int sent = 0;
            Check(store.Request(_ => { sent++; return true; }, ms + 10_000) && sent == 1, "ready-early: the re-read sends /myquests");
            DateTime redone = said - (TimeSpan.FromSeconds(72000) - wait);
            store.OnChatLine($"dailyquesttimer - 6 solves ({Unix(redone)})\"Daily\" -1 72000", ms + 10_200);
            store.Tick(ms + 12_000);
            Check(!store.RereadWanted && !store.NeedsReread(said, timersInUse: true), "ready-early: the reply ends the re-read");
            Check(store.TryGet("dailyquesttimer", out QuestFlag after) && !after.Ready(said)
                  && QuestTimer.State(after, true, said, out string text) == QuestTimerState.Waiting && text == "2h 50m",
                  "ready-early: after the re-read the daily waits 2h 50m, as the server said");
            var near = store.WaitingAbout(wait, said, TimeSpan.FromMinutes(2));
            Check(near.Count == 1 && near[0].Flag.Key == "dailyquesttimer" && near[0].Off.Duration() < TimeSpan.FromSeconds(2),
                  "ready-early: the server's wait points at the daily in the fresh read");

            // The lines only count whole: quoted in a tell, they're chat.
            var s2 = new QuestFlagStore();
            Check(s2.OnChatLine("Someone tells you, \"You may complete this quest again in 1h 0m 0s.\"", 1) == MyQuestsLine.None
                  && s2.OnChatLine("Someone tells you, \"You have solved this quest too recently!\"", 2) == MyQuestsLine.None
                  && !s2.RereadWanted, "ready-early: a quoted turn-down is ordinary chat");
            Check(QuestFlagStore.TryParseAgainIn("You may complete this quest again in 1d 3h 0m 5s.", out TimeSpan d)
                  && d == new TimeSpan(1, 3, 0, 5), "ready-early: days in the wait");
            Check(QuestFlagStore.TryParseAgainIn("You may complete this quest again in 12m 4s.", out TimeSpan m) && m == new TimeSpan(0, 12, 4),
                  "ready-early: a short wait");
            Check(!QuestFlagStore.TryParseAgainIn("You may complete this quest again in a while.", out _), "ready-early: no number, no wait");

            // A server without /myquests is never re-read; a logout forgets the wish.
            var s3 = new QuestFlagStore();
            s3.OnChatLine("alpha - 1 solves (1719432000)\"A\" -1 72000", 10);
            s3.Tick(5_000);
            s3.OnChatLine(QuestFlagStore.DisabledText, 6_000);
            s3.WantReread();
            Check(!s3.NeedsReread(DateTime.UtcNow.AddHours(1), timersInUse: true), "ready-early: no re-read where /myquests is off");
            s3.Clear();
            Check(!s3.RereadWanted && s3.LastServerWait == null, "ready-early: Clear forgets the re-read");
        }
    }
}
