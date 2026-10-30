using System;
using System.Collections.Generic;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Meta Manager (Docs\META_MANAGER.md): the /myquests parser, rule evaluation and priority,
// the safe-switch wait and its cap, the minimum gap, and eating only our own poll's reply.
// MetaScheduler is pure (the clock is passed in), so these drive it directly on a fake clock.
internal static class MetaSchedulerTests
{
    private static readonly DateTime T0 = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long UnixT0 = MetaScheduler.ToUnixMs(T0) / 1000;
    private const string FarmMeta = @"C:\Games\RynthSuite\RynthAi\MetaFiles\farm.af";

    public static void Register(Runner r)
    {
        r.Add("metamgr: /myquests lines parse (ACE HandleQuests format)", ParserQuestLines);
        r.Add("metamgr: /myquests empty, disabled and unrelated lines", ParserOtherLines);
        r.Add("metamgr: ready time, solve limit, never-done quests", QuestReadiness);
        r.Add("metamgr: the first due rule wins; off, one-shot and failed rules are skipped", FirstMatchWins);
        r.Add("metamgr: a fired quest rule holds first place while its quest is ready", QuestLatchHoldsPriority);
        r.Add("metamgr: after N minutes on the meta, every N minutes, countdown", TimeTriggers);
        r.Add("metamgr: only-while-on meta filter", OnlyOnMetaFilter);
        r.Add("metamgr: the switch waits for combat/looting/buffing, capped", SafeSwitchWaitAndCap);
        r.Add("metamgr: no switch while the macro is stopped unless allowed", MacroStopped);
        r.Add("metamgr: minimum gap between switches", MinimumGap);
        r.Add("metamgr: polls only while on with a quest rule, at the interval", PollCadence);
        r.Add("metamgr: eats only the reply to its own poll", EatsOnlyOwnPoll);
        r.Add("metamgr: a /myquests typed during our poll shows the rest", PlayerQueryDuringPoll);
        r.Add("metamgr: a /myquests typed seconds after our poll's echo shows", PlayerQueryRightAfterEcho);
        r.Add("metamgr: server has /myquests off: polling stops", ServerDisabled);
        r.Add("metamgr: settings file and panel snapshot are valid JSON", ConfigAndSnapshotJson);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>A /myquests line exactly as ACE builds it (PlayerCommands.HandleQuests).</summary>
    private static string Line(string name, int solves, long lastUnix, string message, int maxSolves, long minDelta)
        => $"{name.ToLower()} - {solves} solves ({lastUnix})" + $"\"{message}\" {maxSolves} {minDelta}";

    private static ScheduleInputs In(bool macro = true, string meta = FarmMeta, string? blocker = null, bool inWorld = true)
        => new(inWorld, macro, meta, blocker, CanPoll: true);

    private static MetaScheduleRule Quest(string quest, string meta, bool repeat = true)
        => new() { Trigger = ScheduleTrigger.QuestReady, Quest = quest, Meta = meta, Repeat = repeat };

    private static MetaScheduleRule Timed(ScheduleTrigger t, double minutes, string meta, bool repeat = true)
        => new() { Trigger = t, Minutes = minutes, Meta = meta, Repeat = repeat };

    private static MetaScheduler Make(bool enabled, params MetaScheduleRule[] rules)
    {
        var s = new MetaScheduler();
        var cfg = new MetaScheduleConfig { Enabled = enabled };
        cfg.Rules.AddRange(rules);
        s.Load(cfg);
        return s;
    }

    /// <summary>Plays a whole /myquests reply for a poll the scheduler asked for: returns how many lines it ate.</summary>
    private static int Reply(MetaScheduler s, ref DateTime now, params string[] lines)
    {
        int eaten = 0;
        foreach (string l in lines)
        {
            now = now.AddMilliseconds(100);
            if (s.OnChatLine(l, now)) eaten++;
        }
        now = now.AddSeconds(2);   // silence: the reply is over
        s.Tick(now, In(macro: false));
        return eaten;
    }

    /// <summary>A scheduler whose first tick polled and whose reply (the given lines) has been read.</summary>
    private static MetaScheduler Polled(ref DateTime now, MetaScheduleRule[] rules, params string[] lines)
    {
        var s = Make(true, rules);
        var first = s.Tick(now, In(macro: false));
        Check.Eq(first.Kind, ScheduleActionKind.SendPoll, "the first tick with a quest rule polls");
        Reply(s, ref now, lines);
        return s;
    }

    // ── Parser ───────────────────────────────────────────────────────────────

    private static void ParserQuestLines()
    {
        // Quest names from Aelrynth's own registry (Mods\Aeshnidae.AllegianceBounty\Quests.json,
        // Aeshnidae.QuestBonus\Settings.json); ACE prints them lowercased.
        string a = Line("BlightLordLairWait1008", 3, 1759190400, "Blight Lord Lair", -1, 72000);
        Check.Eq(a, "blightlordlairwait1008 - 3 solves (1759190400)\"Blight Lord Lair\" -1 72000", "helper matches ACE's format string");
        Check.Eq(MyQuestsParser.Parse(a, out var e), MyQuestsLineKind.Quest, "a quest line");
        Check.Eq(e.Name, "blightlordlairwait1008", "name");
        Check.Eq(e.Solves, 3, "solves");
        Check.Eq(e.LastSolvedUnix, 1759190400L, "last solved (unix)");
        Check.Eq(e.Message, "Blight Lord Lair", "message");
        Check.Eq(e.MaxSolves, -1, "max solves -1 = unlimited");
        Check.Eq(e.MinDeltaSeconds, 72000L, "min delta");
        Check.Eq(e.ReadyAtUtc, DateTime.UnixEpoch.AddSeconds(1759190400 + 72000), "ready at = last + delta");

        Check.Eq(MyQuestsParser.Parse("stipendtimer_monthly - 12 solves (1759276800)\"Monthly Stipend\" -1 2592000", out e),
            MyQuestsLineKind.Quest, "underscores in the name");
        Check.Eq(e.Name, "stipendtimer_monthly", "underscored name");

        // The message may hold quotes and the line may carry the client's trailing newline.
        Check.Eq(MyQuestsParser.Parse("taskmoarsmenartifactswait - 1 solves (1759000000)\"Bring \"artifacts\" to Kanji\" -1 72000\n", out e),
            MyQuestsLineKind.Quest, "quotes in the message, trailing newline");
        Check.Eq(e.Message, "Bring \"artifacts\" to Kanji", "message keeps its quotes");
        Check.Eq(e.MinDeltaSeconds, 72000L, "delta after a quoted message");

        // An empty message (a quest with no Message in the world db) and a zero timer.
        Check.Eq(MyQuestsParser.Parse("celestialhandmember - 1 solves (1700000000)\"\" 1 0", out e), MyQuestsLineKind.Quest, "empty message");
        Check.Eq(e.MaxSolves, 1, "max solves 1");
    }

    private static void ParserOtherLines()
    {
        Check.Eq(MyQuestsParser.Parse("Quest list is empty.", out _), MyQuestsLineKind.Empty, "empty list");
        Check.Eq(MyQuestsParser.Parse("The command \"myquests\" is not currently enabled on this server.", out _),
            MyQuestsLineKind.Disabled, "server has it off");
        Check.Eq(MyQuestsParser.Parse("Bob says, \"blightlord - 3 solves (12)\"", out _), MyQuestsLineKind.None, "chat quoting a quest line");
        Check.Eq(MyQuestsParser.Parse("You say, \"- 3 solves (5)\"x\" 1 2\"", out _), MyQuestsLineKind.None, "said text that looks close");
        Check.Eq(MyQuestsParser.Parse("You have solved this quest too recently!", out _), MyQuestsLineKind.None, "quest refusal line");
        Check.Eq(MyQuestsParser.Parse("abc - x solves (1)\"m\" 1 2", out _), MyQuestsLineKind.None, "non-numeric solves");
        Check.Eq(MyQuestsParser.Parse("", out _), MyQuestsLineKind.None, "empty text");
        Check.Eq(MyQuestsParser.Parse(null, out _), MyQuestsLineKind.None, "null text");
    }

    private static void QuestReadiness()
    {
        var waiting = new MyQuestsEntry("q", 1, UnixT0 - 3600, "", -1, 7200);   // solved an hour ago, 2 h timer
        Check.False(waiting.IsReady(T0), "timer still running");
        Check.True(waiting.IsReady(T0.AddHours(1)), "ready when last + delta passes");
        var limit = new MyQuestsEntry("q", 1, UnixT0 - 999999, "", 1, 0);
        Check.True(limit.AtSolveLimit, "1 of 1 solves");
        Check.False(limit.IsReady(T0.AddYears(5)), "never ready again at the solve limit");
        var belowLimit = new MyQuestsEntry("q", 2, UnixT0 - 10, "", 5, 0);
        Check.True(belowLimit.IsReady(T0), "2 of 5 with no timer: ready");

        DateTime now = T0;
        var s = Make(true, Quest("NeverDone", "a.af"));
        Check.Eq(s.GetQuestState("NeverDone", now, out _), QuestState.Unknown, "no list yet: unknown");
        s.Tick(now, In(macro: false));   // polls
        Reply(s, ref now, Line("OtherQuest", 1, UnixT0 - 60, "x", -1, 600));
        Check.Eq(s.GetQuestState("neverdone", now, out _), QuestState.Ready, "not in a full list: never done, ready (case ignored)");
        Check.Eq(s.GetQuestState("OTHERQUEST", now, out _), QuestState.Waiting, "in the list, timer running (case ignored)");
        Check.Eq(s.GetQuestState("otherquest", now.AddMinutes(10), out _), QuestState.Ready, "counted down locally between polls");
    }

    // ── Rules and priority ───────────────────────────────────────────────────

    private static void FirstMatchWins()
    {
        DateTime now = T0;
        var s = Polled(ref now, new[]
        {
            Quest("waitingquest", "first.af"),
            Quest("readyone", "second.af"),
            Quest("readytwo", "third.af"),
        },
            Line("waitingquest", 1, UnixT0, "", -1, 86400),
            Line("readyone", 1, UnixT0 - 90000, "", -1, 72000),
            Line("readytwo", 1, UnixT0 - 90000, "", -1, 72000));

        Check.Eq(s.FindWinner(now, FarmMeta), 1, "the first DUE rule wins, not the first rule");
        var act = s.Tick(now, In());
        Check.Eq(act.Kind, ScheduleActionKind.LoadMeta, "loads");
        Check.Eq(act.Meta, "second.af", "the first ready quest's meta");
        Check.Eq(act.Reason, "quest readyone ready", "reason for the chat line");

        // Off, one-shot-fired and failed rules are skipped.
        var t = Make(true,
            new MetaScheduleRule { Trigger = ScheduleTrigger.EveryMinutes, Minutes = 0, Meta = "off.af", Enabled = false },
            new MetaScheduleRule { Trigger = ScheduleTrigger.EveryMinutes, Minutes = 0, Meta = "done.af", Repeat = false, Fired = true },
            Timed(ScheduleTrigger.EveryMinutes, 0, "broken.af"),
            Timed(ScheduleTrigger.EveryMinutes, 0, "good.af"));
        t.ReportLoadFailed(2, "not found in MetaFiles");
        act = t.Tick(T0, In());
        Check.Eq(act.Meta, "good.af", "disabled, fired one-shot and failed rules are passed over");
        Check.True(t.RuleState(2, T0).StartsWith("error"), "the failed rule shows its error");

        // A one-shot fires once, then stays fired until reset.
        var o = Make(true, Timed(ScheduleTrigger.EveryMinutes, 0, "once.af", repeat: false));
        Check.Eq(o.Tick(T0, In()).Meta, "once.af", "one-shot fires");
        Check.True(o.Config.Rules[0].Fired, "then marked fired (saved)");
        Check.Eq(o.Tick(T0.AddMinutes(5), In()).Kind, ScheduleActionKind.None, "and does not fire again");
        o.ResetRule(0);
        Check.Eq(o.Tick(T0.AddMinutes(6), In()).Meta, "once.af", "reset re-arms it");
    }

    private static void QuestLatchHoldsPriority()
    {
        DateTime now = T0;
        var s = Polled(ref now, new[] { Quest("questx", "x.af"), Quest("questy", "y.af") },
            Line("questx", 1, UnixT0 - 90000, "", -1, 72000),
            Line("questy", 1, UnixT0 - 90000, "", -1, 72000));

        Check.Eq(s.Tick(now, In()).Meta, "x.af", "quest X first");
        // Both still ready (the table only changes at the next poll): X holds first place, Y waits.
        now = now.AddMinutes(2);
        Check.Eq(s.Tick(now, In(meta: "x.af")).Kind, ScheduleActionKind.None, "Y does not fire while X is still ready");
        Check.True(s.RuleState(0, now).Contains("holding"), "rule 1 says it is holding");

        // The meta did X: the next poll shows X's timer running again. Now Y fires.
        now = now.AddMinutes(4);
        var poll = s.Tick(now, In(meta: "x.af"));
        Check.Eq(poll.Kind, ScheduleActionKind.SendPoll, "5-minute poll");
        Reply(s, ref now,
            Line("questx", 2, UnixT0 + 300, "", -1, 72000),
            Line("questy", 1, UnixT0 - 90000, "", -1, 72000));
        var act = s.Tick(now, In(meta: "x.af"));
        Check.Eq(act.Meta, "y.af", "after X is done, Y is next");
        Check.False(s.Config.Rules[0].Latched, "X re-armed (its timer is running)");

        // X's timer runs out again 20 h later: it fires again (repeating).
        now = DateTime.UnixEpoch.AddSeconds(UnixT0 + 300 + 72000 + 1);
        s.Tick(now, In(meta: "y.af"));   // polls (its interval has long passed)
        Reply(s, ref now,
            Line("questx", 2, UnixT0 + 300, "", -1, 72000),
            Line("questy", 2, UnixT0 + 600, "", -1, 72000));
        Check.Eq(s.Tick(now, In(meta: "y.af")).Meta, "x.af", "X is ready again and fires again");
    }

    private static void TimeTriggers()
    {
        // After N minutes on the meta: counts from the last meta change, which the switch resets.
        var s = Make(true, Timed(ScheduleTrigger.AfterMinutesOnMeta, 30, "b.af"));
        Check.Eq(s.Tick(T0, In(meta: "a.af")).Kind, ScheduleActionKind.None, "just loaded");
        Check.Eq(s.Tick(T0.AddMinutes(20), In(meta: "c.af")).Kind, ScheduleActionKind.None, "another meta loaded at 20 min: its clock restarts");
        Check.Eq(s.Tick(T0.AddMinutes(49), In(meta: "c.af")).Kind, ScheduleActionKind.None, "29 min on c");
        var act = s.Tick(T0.AddMinutes(50), In(meta: "c.af"));
        Check.Eq(act.Meta, "b.af", "30 min on c: load b");
        Check.Eq(act.Reason, "30 min on c.af", "reason names the meta it left");

        // Every N minutes: from the moment the manager is on, then from each firing.
        var e = Make(true, Timed(ScheduleTrigger.EveryMinutes, 60, "e.af"));
        Check.Eq(e.Tick(T0, In()).Kind, ScheduleActionKind.None, "starts counting");
        Check.Eq(e.Tick(T0.AddMinutes(59), In()).Kind, ScheduleActionKind.None, "59 min");
        Check.Eq(e.Tick(T0.AddMinutes(60), In()).Meta, "e.af", "60 min");
        Check.Eq(e.Tick(T0.AddMinutes(119), In()).Kind, ScheduleActionKind.None, "counts again from the firing");
        Check.Eq(e.Tick(T0.AddMinutes(120), In()).Meta, "e.af", "and fires again");

        // Countdown: nothing until started; a repeating one restarts itself.
        var c = Make(true, Timed(ScheduleTrigger.Countdown, 10, "c.af"));
        Check.Eq(c.Tick(T0.AddHours(3), In()).Kind, ScheduleActionKind.None, "not started: never fires");
        Check.True(c.StartCountdown(0, null, T0.AddHours(3)), "start");
        Check.Eq(c.Tick(T0.AddHours(3).AddMinutes(9), In()).Kind, ScheduleActionKind.None, "9 of 10 min");
        Check.Eq(c.Tick(T0.AddHours(3).AddMinutes(10), In()).Meta, "c.af", "10 min: fires");
        Check.True(c.Config.Rules[0].CountdownEndsUnixMs > 0, "a repeating countdown restarts");
        var once = Make(true, Timed(ScheduleTrigger.Countdown, 10, "c.af", repeat: false));
        once.StartCountdown(0, 2, T0);   // a different length for this run
        Check.Eq(once.Tick(T0.AddMinutes(2), In()).Meta, "c.af", "started with 2 min: fires at 2");
        Check.Eq(once.Config.Rules[0].CountdownEndsUnixMs, 0L, "a one-shot countdown stops");
        Check.False(Make(true, Timed(ScheduleTrigger.EveryMinutes, 5, "x.af")).StartCountdown(0, null, T0), "only countdown rules start");
    }

    private static void OnlyOnMetaFilter()
    {
        var r = Timed(ScheduleTrigger.EveryMinutes, 0, "b.af");
        r.OnlyOnMeta = "A.met";
        var s = Make(true, r);
        Check.Eq(s.Tick(T0, In(meta: @"C:\m\other.af")).Kind, ScheduleActionKind.None, "another meta: no");
        Check.Eq(s.Tick(T0.AddMinutes(1), In(meta: @"C:\m\a.af")).Meta, "b.af", "the named meta (extension and case ignored)");
        Check.True(MetaScheduler.MetaMatches("", ""), "blank filter matches anything");
        Check.False(MetaScheduler.MetaMatches("a", ""), "no meta loaded does not match a name");
    }

    // ── Safe switch ──────────────────────────────────────────────────────────

    private static void SafeSwitchWaitAndCap()
    {
        var s = Make(true, Timed(ScheduleTrigger.EveryMinutes, 1, "b.af"));
        s.Tick(T0, In());
        DateTime due = T0.AddMinutes(1);
        Check.Eq(s.Tick(due, In(blocker: "combat")).Kind, ScheduleActionKind.None, "due but fighting: waits");
        Check.Eq(s.WaitReason, "combat", "says why");
        Check.Eq(s.Tick(due.AddSeconds(30), In(blocker: "looting")).Kind, ScheduleActionKind.None, "30 s, looting: waits");
        Check.Eq(s.Tick(due.AddSeconds(59), In(blocker: "buffing")).Kind, ScheduleActionKind.None, "59 s, buffing: waits");
        var act = s.Tick(due.AddSeconds(60), In(blocker: "combat"));
        Check.Eq(act.Kind, ScheduleActionKind.LoadMeta, "the 60 s cap: switches anyway");
        Check.True(act.Forced, "marked forced");
        Check.True(act.Reason.Contains("waited 60 s for combat"), "the chat line says it waited: " + act.Reason);

        // Clears early: switches the moment it is safe.
        var q = Make(true, Timed(ScheduleTrigger.EveryMinutes, 1, "b.af"));
        q.Tick(T0, In());
        Check.Eq(q.Tick(due, In(blocker: "combat")).Kind, ScheduleActionKind.None, "fighting");
        act = q.Tick(due.AddSeconds(12), In());
        Check.Eq(act.Kind, ScheduleActionKind.LoadMeta, "fight over at 12 s: switches");
        Check.False(act.Forced, "not forced");

        // A different cap from the settings.
        var z = Make(true, Timed(ScheduleTrigger.EveryMinutes, 1, "b.af"));
        z.SetMaxWaitSeconds(10);
        z.Tick(T0, In());
        Check.Eq(z.Tick(due, In(blocker: "combat")).Kind, ScheduleActionKind.None, "cap 10: waits at 0");
        Check.Eq(z.Tick(due.AddSeconds(10), In(blocker: "combat")).Kind, ScheduleActionKind.LoadMeta, "cap 10: switches at 10 s");
    }

    private static void MacroStopped()
    {
        var s = Make(true, Timed(ScheduleTrigger.EveryMinutes, 1, "b.af"));
        s.Tick(T0, In());
        Check.Eq(s.Tick(T0.AddMinutes(1), In(macro: false)).Kind, ScheduleActionKind.None, "macro stopped: no switch");
        Check.Eq(s.Tick(T0.AddMinutes(30), In(macro: false)).Kind, ScheduleActionKind.None, "not even after the wait cap");
        Check.Eq(s.WaitReason, "macro stopped", "says why");
        // The cap counts only while running: started mid-fight, it still waits for the fight.
        DateTime started = T0.AddMinutes(30).AddSeconds(10);
        Check.Eq(s.Tick(started, In(blocker: "combat")).Kind, ScheduleActionKind.None, "macro started mid-fight: waits");
        Check.Eq(s.Tick(started.AddSeconds(5), In()).Kind, ScheduleActionKind.LoadMeta, "then switches");

        var a = Make(true, Timed(ScheduleTrigger.EveryMinutes, 1, "b.af"));
        a.SetAllowWhileStopped(true);
        a.Tick(T0, In());
        Check.Eq(a.Tick(T0.AddMinutes(1), In(macro: false)).Kind, ScheduleActionKind.LoadMeta, "allowed while stopped");

        var off = Make(false, Timed(ScheduleTrigger.EveryMinutes, 0, "b.af"));
        Check.Eq(off.Tick(T0.AddHours(1), In()).Kind, ScheduleActionKind.None, "manager off (the default): never");
        Check.False(new MetaScheduleConfig().Enabled, "off by default");
    }

    private static void MinimumGap()
    {
        var s = Make(true,
            Timed(ScheduleTrigger.EveryMinutes, 1, "a.af"),
            Timed(ScheduleTrigger.EveryMinutes, 1, "b.af"));
        s.Tick(T0, In());
        DateTime due = T0.AddMinutes(1);
        Check.Eq(s.Tick(due, In()).Meta, "a.af", "first rule");
        Check.Eq(s.Tick(due.AddSeconds(1), In()).Kind, ScheduleActionKind.None, "second is due too but the gap holds it");
        Check.Eq(s.WaitReason, "minimum gap", "says why");
        Check.Eq(s.Tick(due.AddSeconds(29), In()).Kind, ScheduleActionKind.None, "29 s");
        Check.Eq(s.Tick(due.AddSeconds(30), In()).Meta, "b.af", "30 s: the second switch");

        // The safe-switch wait starts after the gap: fighting at the end of the gap is not "capped".
        var g = Make(true,
            Timed(ScheduleTrigger.EveryMinutes, 1, "a.af", repeat: false),
            Timed(ScheduleTrigger.EveryMinutes, 1, "b.af"));
        g.Tick(T0, In());
        Check.Eq(g.Tick(due, In()).Meta, "a.af", "first switch");
        Check.Eq(g.Tick(due.AddSeconds(29), In(blocker: "combat")).Kind, ScheduleActionKind.None, "in the gap");
        Check.Eq(g.Tick(due.AddSeconds(31), In(blocker: "combat")).Kind, ScheduleActionKind.None,
            "gap over, fighting: waits (the gap does not count toward the cap)");
        Check.Eq(g.Tick(due.AddSeconds(89), In(blocker: "combat")).Kind, ScheduleActionKind.None, "59 s after the gap: still waits");
        Check.Eq(g.Tick(due.AddSeconds(90), In(blocker: "combat")).Kind, ScheduleActionKind.LoadMeta, "60 s after the gap: capped");
    }

    // ── Polling and eating ───────────────────────────────────────────────────

    private static void PollCadence()
    {
        var none = Make(true, Timed(ScheduleTrigger.EveryMinutes, 60, "a.af"));
        Check.Eq(none.Tick(T0, In()).Kind, ScheduleActionKind.None, "no quest rule: no poll");
        var off = Make(false, Quest("q", "a.af"));
        Check.Eq(off.Tick(T0, In()).Kind, ScheduleActionKind.None, "manager off: no poll");
        var notInWorld = Make(true, Quest("q", "a.af"));
        Check.Eq(notInWorld.Tick(T0, In(inWorld: false)).Kind, ScheduleActionKind.None, "not in world: no poll");

        DateTime now = T0;
        var s = Make(true, Quest("q", "a.af"));
        Check.Eq(s.Tick(now, In()).Kind, ScheduleActionKind.SendPoll, "polls at once when switched on");
        Reply(s, ref now, Line("q", 1, UnixT0, "", -1, 86400));
        Check.Eq(s.Tick(T0.AddMinutes(4), In()).Kind, ScheduleActionKind.None, "4 min: no poll");
        Check.Eq(s.Tick(T0.AddMinutes(5), In()).Kind, ScheduleActionKind.SendPoll, "5 min (default): poll");

        var p = Make(true, Quest("q", "a.af"));
        p.SetPollMinutes(0);
        Check.Eq(p.Config.PollMinutes, 1, "minimum 1 minute");
        p.Tick(T0, In());
        Check.Eq(p.Tick(T0.AddSeconds(30), In()).Kind, ScheduleActionKind.None, "no second poll while the first waits for its reply");
        Check.Eq(p.Tick(T0.AddSeconds(59), In()).Kind, ScheduleActionKind.None, "a silent reply times out, still under 1 min");
        Check.Eq(p.Tick(T0.AddSeconds(61), In()).Kind, ScheduleActionKind.SendPoll, "1 min later: polls again");
        p.RequestPoll();
        now = T0.AddSeconds(62);
        Check.Eq(p.Tick(now.AddSeconds(20), In()).Kind, ScheduleActionKind.SendPoll, "/ra metamgr poll: polls now");
    }

    private static void EatsOnlyOwnPoll()
    {
        DateTime now = T0;
        var s = Make(true, Quest("q", "a.af"));
        Check.Eq(s.Tick(now, In()).Kind, ScheduleActionKind.SendPoll, "our poll");
        // The engine hands our own /myquests to the chat-bar hook: that echo is not the player.
        s.NoteTypedQuery(now.AddMilliseconds(20));
        Check.True(s.PollInFlight, "still our poll after the echo");
        int eaten = Reply(s, ref now,
            Line("q", 1, UnixT0, "", -1, 86400),
            Line("stipendtimer_monthly", 1, UnixT0, "", -1, 2592000));
        Check.Eq(eaten, 2, "both reply lines eaten");
        Check.False(s.OnChatLine("Bob tells you, \"hi\"", now), "other chat is never eaten");

        // Reply over: a later /myquests line (someone else asked) shows.
        now = now.AddSeconds(5);
        Check.False(s.OnChatLine(Line("q", 1, UnixT0, "", -1, 86400), now), "a line after our reply ended shows");

        // The player types /myquests between polls: shown, and the table still learns from it.
        now = T0.AddMinutes(2);
        s.NoteTypedQuery(now);
        eaten = Reply(s, ref now, Line("q", 2, UnixT0 + 120, "", -1, 86400));
        Check.Eq(eaten, 0, "the player's /myquests is not eaten");
        s.GetQuestState("q", now, out DateTime readyAt);
        Check.Eq(readyAt, DateTime.UnixEpoch.AddSeconds(UnixT0 + 120 + 86400), "but its lines update the timers");

        // RynthAi's own quest refresh (login, /ra myquests, refreshquests[]) is shown too.
        now = T0.AddMinutes(3);
        s.NoteShownQuery(now);
        Check.Eq(Reply(s, ref now, "Quest list is empty."), 0, "RynthAi's refresh is shown");
        Check.Eq(s.GetQuestState("q", now, out _), QuestState.Ready, "an empty list: everything can be solved");

        // An empty list answering our poll is eaten like any reply line.
        now = T0.AddMinutes(10);
        Check.Eq(s.Tick(now, In()).Kind, ScheduleActionKind.SendPoll, "next poll");
        Check.True(s.OnChatLine("Quest list is empty.", now.AddMilliseconds(300)), "our empty reply eaten");
    }

    private static void PlayerQueryDuringPoll()
    {
        DateTime now = T0;
        var s = Make(true, Quest("q", "a.af"));
        s.Tick(now, In());
        now = now.AddMilliseconds(100);
        Check.True(s.OnChatLine(Line("a", 1, UnixT0, "", -1, 1), now), "our reply starts: eaten");
        // 10 s after our poll (not its echo) the player asks too: show everything from here.
        now = now.AddSeconds(10);
        s.NoteTypedQuery(now);
        Check.False(s.OnChatLine(Line("b", 1, UnixT0, "", -1, 1), now.AddMilliseconds(100)), "after the player asked: shown");
        Check.False(s.PollInFlight, "no longer treated as ours");
    }

    // The engine now hides eaten lines in AC's own chat window too, so a reply the player asked
    // for must never be taken for ours: only the one chat-bar echo of our own poll is ours.
    private static void PlayerQueryRightAfterEcho()
    {
        DateTime now = T0;
        var s = Make(true, Quest("q", "a.af"));
        Check.Eq(s.Tick(now, In()).Kind, ScheduleActionKind.SendPoll, "our poll");
        s.NoteTypedQuery(now.AddMilliseconds(20));               // our own poll's echo
        Check.True(s.PollInFlight, "the echo is ours");
        s.NoteTypedQuery(now.AddSeconds(2));                     // the player types /myquests 2 s later
        Check.False(s.PollInFlight, "a second /myquests is the player's");
        Check.False(s.OnChatLine(Line("q", 1, UnixT0, "", -1, 86400), now.AddSeconds(2.3)), "its reply shows");
        Check.False(s.OnChatLine("Quest list is empty.", now.AddSeconds(2.4)), "an empty list shows too");
        Check.False(s.OnChatLine(MyQuestsParser.DisabledText, now.AddSeconds(2.5)), "a refusal shows too");
    }

    private static void ServerDisabled()
    {
        DateTime now = T0;
        var s = Make(true, Quest("q", "a.af"));
        s.Tick(now, In());
        Check.True(s.OnChatLine(MyQuestsParser.DisabledText, now.AddMilliseconds(200)), "our poll's refusal is eaten");
        Check.True(s.ServerDisabled, "noted");
        Check.Eq(s.Tick(now.AddMinutes(30), In()).Kind, ScheduleActionKind.None, "no more polls");
        Check.True(s.RuleState(0, now).Contains("off on this server"), "the rule says why it never fires");
        s.SetEnabled(false);
        s.SetEnabled(true);
        Check.Eq(s.Tick(now.AddMinutes(31), In()).Kind, ScheduleActionKind.SendPoll, "switching the manager on again retries");
    }

    // ── Storage and panel ────────────────────────────────────────────────────

    private static void ConfigAndSnapshotJson()
    {
        var s = Make(true, Quest("BlightLordLairWait1008", "x \"quoted\".af"), Timed(ScheduleTrigger.Countdown, 15, "c.af", repeat: false));
        s.StartCountdown(1, null, T0);
        s.SetPollMinutes(7);
        Check.True(s.ConsumeDirty(), "edits mark the file dirty");
        Check.False(s.ConsumeDirty(), "once");

        string json = JsonSerializer.Serialize(s.Config, RynthAiJsonContext.Default.MetaScheduleConfig);
        Check.False(json.Contains("Latched") || json.Contains("EveryAnchorUtc") || json.Contains("FireCount"), "runtime state is not saved");
        var back = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MetaScheduleConfig);
        Check.NotNull(back, "reads back");
        var t = new MetaScheduler();
        t.Load(back);
        Check.Eq(t.Config.PollMinutes, 7, "poll interval saved");
        Check.Eq(t.Config.Rules.Count, 2, "rules saved");
        Check.Eq(t.Config.Rules[0].Quest, "BlightLordLairWait1008", "quest name saved");
        Check.Eq(t.Config.Rules[1].Trigger, ScheduleTrigger.Countdown, "trigger saved");
        Check.Eq(t.Config.Rules[1].CountdownEndsUnixMs, MetaScheduler.ToUnixMs(T0) + 15 * 60_000L, "a running countdown survives a relog");
        Check.False(t.Config.Rules[1].Repeat, "one-shot saved");

        string snap = s.BuildSnapshotJson(T0, FarmMeta);
        using var doc = JsonDocument.Parse(snap);
        var root = doc.RootElement;
        Check.True(root.GetProperty("enabled").GetBoolean(), "snapshot: enabled");
        Check.Eq(root.GetProperty("pollMinutes").GetInt32(), 7, "snapshot: poll minutes");
        var rules = root.GetProperty("rules");
        Check.Eq(rules.GetArrayLength(), 2, "snapshot: rules");
        Check.Eq(rules[0].GetProperty("meta").GetString(), "x \"quoted\".af", "snapshot: escaped meta name");
        Check.Eq(rules[0].GetProperty("dueAtMs").GetInt64(), 0L, "snapshot: quest unknown before a poll = 0");
        Check.Eq(rules[1].GetProperty("dueAtMs").GetInt64(), MetaScheduler.ToUnixMs(T0) + 15 * 60_000L, "snapshot: countdown end");
        Check.Eq(s.BuildSnapshotJson(T0.AddSeconds(7), FarmMeta), snap, "snapshot text does not change with the clock alone");

        // The engine panel's mm_add as its MetaCmd serializes it (PascalCase, status fields left out).
        const string fromPanel = "{\"Op\":\"mm_add\",\"Index\":-1,\"Value\":\"\",\"Path\":\"\",\"Text\":\"\",\"Rule\":null,"
            + "\"ScheduleRule\":{\"Enabled\":true,\"Trigger\":3,\"Quest\":\"\",\"Minutes\":12.5,\"Meta\":\"b.af\",\"OnlyOnMeta\":\"a.af\",\"Repeat\":false}}";
        var cmd = JsonSerializer.Deserialize(fromPanel, RynthAiJsonContext.Default.MetaCommand);
        Check.NotNull(cmd?.ScheduleRule, "the panel's rule reads");
        Check.Eq(cmd!.ScheduleRule!.Trigger, ScheduleTrigger.Countdown, "trigger number");
        Check.Eq(cmd.ScheduleRule.Minutes, 12.5, "minutes");
        Check.Eq(cmd.ScheduleRule.OnlyOnMeta, "a.af", "only-on meta");
        Check.False(cmd.ScheduleRule.Repeat, "once");
        t.AddRule(cmd.ScheduleRule);
        Check.Eq(t.Config.Rules.Count, 3, "added");
    }
}
