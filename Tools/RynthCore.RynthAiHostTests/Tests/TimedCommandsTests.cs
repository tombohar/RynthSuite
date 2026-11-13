using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.TimedCommands;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Timed commands (2026-11-03, players asked for MagTools' periodic commands): chat commands sent every
// N seconds (minutes until 2026-11-12; old files are converted), from three lists (this character, this
// server, all characters) kept beside the settings profiles. The file format, the folders, when each runs
// (first delay, interval, one a second, never in portal space, macro only, no backlog), /ra timed and the
// window in the sections manifest (no bar button).
internal static class TimedCommandsTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const long Min = 60_000;

    public static void Register(Runner r)
    {
        r.Add("timed commands: the file round-trips in seconds (version 2); bad values are clamped, missing ones default (on, 600 s)", FileRoundTrip);
        r.Add("timed commands: an old file in minutes (version 1) loads as seconds (x60) and the next save writes seconds", OldMinutesFile);
        r.Add("timed commands: the three lists load from the character's, the server's and the base folder, and save only what changed", Folders);
        r.Add("timed commands: the first run after the first delay (or one interval), then every interval", FirstDelayThenInterval);
        r.Add("timed commands: every second works (one a second, shared fairly); a 0 typed in the box never means every tick", ShortIntervals);
        r.Add("timed commands: one a second; never before the world, in portal space or (macro only) with the macro off; no backlog", Gates);
        r.Add("timed commands: off and empty never run; turned on again it waits a full delay; all three lists run", OffEmptyAndScopes);
        r.Add("timed commands: the interval text in seconds, with minutes and hours for long ones", EveryText);
        r.Add("timed commands: /ra timed add, list, off, on, run and remove (each replies) for this character, server and all", ChatCommand);
        r.Add("timed commands: the window is in the sections manifest without a bar button, only when asked for", Manifest);
        r.Add("timed commands: the dashboard's options menu entry and /ra timed show|hide open and close the window", WindowToggle);
    }

    private static readonly List<(string Key, int Mode)> PanelCalls = new();

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe int FakeSetPanelOpen(byte* key, int mode)
    {
        PanelCalls.Add((System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)key) ?? "", mode));
        return mode == 0 ? 0 : 1;
    }

    private static unsafe void PanelApi(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 89u);
        api.SetPanelOpenFn = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&FakeSetPanelOpen;
    }

    private static void WindowToggle()
    {
        try
        {
            FakeHost.Reset();
            PanelCalls.Clear();
            FakeHost.ConfigureApi = PanelApi;
            FakeHost.Create(0x50000F77);
            var p = new RynthAiPlugin();
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });

            // What the dashboard's ⋯ > Timed commands runs (queued to the tick by RynthAiPlugin.Dashboard.cs).
            typeof(RynthAiPlugin).GetMethod("ToggleTimedCommandsWindow", Any)!.Invoke(p, null);
            Check.Eq(PanelCalls.Count, 1, "one call to the engine");
            Check.True(PanelCalls[0].Key == RynthAiPlugin.TimedCommandsTitle && PanelCalls[0].Mode == (int)RynthPanelOpen.Toggle,
                "toggles the Timed Commands window (" + PanelCalls[0] + ")");

            MethodInfo handle = typeof(RynthAiPlugin).GetMethod("HandleTimedCommand", Any)!;
            string Say(string line)
            {
                int before = FakeHost.Chat.Count;
                handle.Invoke(p, new object[] { line.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
                return string.Join(" | ", FakeHost.Chat.Skip(before));
            }
            Check.True(Say("/ra timed show").Contains("Timed Commands window open"), "/ra timed show opens it");
            Check.Eq(PanelCalls[^1].Mode, (int)RynthPanelOpen.Open, "with Open");
            Check.True(Say("/ra timed hide").Contains("Timed Commands window closed"), "/ra timed hide closes it");
            Check.Eq(PanelCalls[^1].Mode, (int)RynthPanelOpen.Close, "with Close");
        }
        finally { FakeHost.ConfigureApi = null; }
    }

    private static TimedCommandSet Loaded(params (TimedScope Scope, TimedCommand Cmd)[] cmds)
    {
        string root = Path.Combine(Path.GetTempPath(), "rynth-timed-" + Guid.NewGuid().ToString("N"));
        var set = new TimedCommandSet();
        set.Load(Path.Combine(root, "SettingsProfiles", "Server One", "Hero"));
        lock (set.Sync)
            foreach (var (scope, cmd) in cmds) set.List(scope).Add(cmd);
        return set;
    }

    /// <summary>A command every <paramref name="seconds"/>, first run <paramref name="first"/> seconds after login.</summary>
    private static TimedCommand Cmd(string text, int seconds, int first = 0, bool macroOnly = false, bool on = true) =>
        new() { Command = text, Seconds = seconds, FirstAfter = first, MacroOnly = macroOnly, Enabled = on };

    private static TimedRunState At(long ms, bool inWorld = true, bool portal = false, bool macro = true) =>
        new(ms, inWorld, portal, macro);

    private static string? Next(TimedCommandScheduler s, TimedCommandSet set, TimedRunState st) =>
        s.TryNext(set, in st, out string c, out _) ? c : null;

    // ── File ───────────────────────────────────────────────────────────────

    private static void FileRoundTrip()
    {
        var list = new List<TimedCommand> { Cmd("/b", 60), Cmd("/ra timed run 2", 1, first: 3, macroOnly: true, on: false) };
        string json = TimedCommandSet.Serialize(list);
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            Check.Eq(doc.RootElement.GetProperty("version").GetInt32(), 2, "the file is version 2");
            var first = doc.RootElement.GetProperty("commands")[0];
            Check.True(first.GetProperty("seconds").GetInt32() == 60 && first.GetProperty("firstAfterSeconds").GetInt32() == 0,
                "written as seconds and firstAfterSeconds");
            Check.False(first.TryGetProperty("minutes", out _) || first.TryGetProperty("firstAfter", out _), "no minutes keys any more");
        }
        List<TimedCommand> back = TimedCommandSet.Parse(json);
        Check.Eq(back.Count, 2, "two commands back");
        Check.True(back[0].Command == "/b" && back[0].Seconds == 60 && back[0].FirstAfter == 0 && back[0].Enabled && !back[0].MacroOnly, "the first as written");
        Check.True(back[1].Command == "/ra timed run 2" && back[1].Seconds == 1 && back[1].FirstAfter == 3 && !back[1].Enabled && back[1].MacroOnly,
            "the second as written (every second, off, macro only, first after 3 s)");

        List<TimedCommand> odd = TimedCommandSet.Parse(
            "{\"version\":2,\"commands\":[{\"command\":\"  /e wave \\n\"},{\"command\":\"/b\",\"seconds\":0,\"firstAfterSeconds\":-5}," +
            "{\"seconds\":999999,\"firstAfterSeconds\":999999},{\"seconds\":\"x\",\"firstAfterSeconds\":2.6},7]}");
        Check.Eq(odd.Count, 4, "four objects (a number in the array is skipped)");
        Check.True(odd[0].Command == "/e wave" && odd[0].Seconds == 600 && odd[0].FirstAfter == 0 && odd[0].Enabled && !odd[0].MacroOnly,
            "missing fields: every 600 s, on; the text trimmed");
        Check.True(odd[1].Seconds == TimedCommand.MinSeconds && odd[1].FirstAfter == 0, "too short is 1 s; a negative first delay is 0");
        Check.True(odd[2].Seconds == TimedCommand.MaxSeconds && odd[2].FirstAfter == TimedCommand.MaxSeconds && odd[2].Command.Length == 0,
            "too long is a day (86400 s) for both; no command is empty");
        Check.True(odd[3].Seconds == 600 && odd[3].FirstAfter == 3, "not a number: the default; a fraction rounds to whole seconds");
        Check.Eq(TimedCommandSet.Parse("[1,2]").Count, 0, "not an object: nothing");
        Check.Eq(TimedCommand.CleanCommand(new string('x', 300)).Length, TimedCommand.MaxLength, "a command is cut at 200 characters");
        Check.Eq(new TimedCommand().Seconds, 600, "a new command: every 600 s");
    }

    private static void OldMinutesFile()
    {
        List<TimedCommand> old = TimedCommandSet.Parse(
            "{\"version\":1,\"commands\":[{\"command\":\"/b\",\"minutes\":10,\"firstAfter\":2,\"enabled\":true,\"macroOnly\":false}," +
            "{\"command\":\"/q\",\"minutes\":0.25,\"firstAfter\":0},{\"command\":\"/d\",\"minutes\":1440,\"firstAfter\":0.5},{\"command\":\"/t\",\"minutes\":0.01}]}");
        Check.Eq(old.Count, 4, "four old commands");
        Check.True(old[0].Seconds == 600 && old[0].FirstAfter == 120, "10 min, first after 2 -> 600 s, first after 120 s");
        Check.True(old[1].Seconds == 15 && old[1].FirstAfter == 0, "0.25 min -> 15 s");
        Check.True(old[2].Seconds == 86400 && old[2].FirstAfter == 30, "a day -> 86400 s; first after 0.5 min -> 30 s");
        Check.Eq(old[3].Seconds, 1, "0.01 min (0.6 s) -> 1 s, the shortest");

        List<TimedCommand> both = TimedCommandSet.Parse("{\"commands\":[{\"command\":\"/b\",\"seconds\":45,\"minutes\":10,\"firstAfterSeconds\":5,\"firstAfter\":3}]}");
        Check.True(both[0].Seconds == 45 && both[0].FirstAfter == 5, "with both keys the seconds win");

        string root = Path.Combine(Path.GetTempPath(), "rynth-timed-" + Guid.NewGuid().ToString("N"));
        string hero = Path.Combine(root, "SettingsProfiles", "Server One", "Hero");
        Directory.CreateDirectory(hero);
        try
        {
            string file = Path.Combine(hero, TimedCommandSet.FileName);
            File.WriteAllText(file, "{\"version\":1,\"commands\":[{\"command\":\"/b\",\"minutes\":5,\"firstAfter\":1,\"enabled\":true,\"macroOnly\":true}]}");
            var set = new TimedCommandSet();
            set.Load(hero);
            lock (set.Sync)
            {
                TimedCommand c = set.List(TimedScope.Character).Single();
                Check.True(c.Seconds == 300 && c.FirstAfter == 60 && c.MacroOnly, "loaded from the old file as 300 s, first after 60 s");
                set.MarkChanged(TimedScope.Character, 1);
            }
            set.SaveDirty();
            string saved = File.ReadAllText(file);
            Check.True(saved.Contains("\"version\": 2") && saved.Contains("\"seconds\": 300") && saved.Contains("\"firstAfterSeconds\": 60"),
                "the next save writes version 2 in seconds (" + saved.Replace('\n', ' ') + ")");
            Check.False(saved.Contains("\"minutes\""), "and no minutes key");
            var again = new TimedCommandSet();
            again.Load(hero);
            lock (again.Sync) Check.Eq(again.List(TimedScope.Character).Single().Seconds, 300, "read back: still 300 s (not converted twice)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "rynth-timed-" + Guid.NewGuid().ToString("N"));
        string baseDir = Path.Combine(root, "SettingsProfiles"), server = Path.Combine(baseDir, "Server One"), hero = Path.Combine(server, "Hero");
        Directory.CreateDirectory(hero);
        try
        {
            File.WriteAllText(Path.Combine(hero, TimedCommandSet.FileName), TimedCommandSet.Serialize(new[] { Cmd("/hero", 300) }));
            File.WriteAllText(Path.Combine(server, TimedCommandSet.FileName), TimedCommandSet.Serialize(new[] { Cmd("/server", 360), Cmd("/server2", 420) }));
            File.WriteAllText(Path.Combine(baseDir, TimedCommandSet.FileName), TimedCommandSet.Serialize(new[] { Cmd("/all", 480) }));
            var set = new TimedCommandSet();
            var problems = new List<string>();
            set.Load(hero, problems);
            Check.Eq(problems.Count, 0, "no problems");
            Check.True(set.Loaded && set.Character == "Hero" && set.Server == "Server One", "the character and server from the folders");
            lock (set.Sync)
            {
                Check.Eq(set.List(TimedScope.Character).Single().Command, "/hero", "this character's list");
                Check.Eq(string.Join(",", set.List(TimedScope.Server).Select(c => c.Command)), "/server,/server2", "the server's list");
                Check.Eq(set.List(TimedScope.All).Single().Command, "/all", "every character's list");
                set.List(TimedScope.Server).RemoveAt(0);
                set.MarkChanged(TimedScope.Server, 1);
            }
            DateTime heroTime = File.GetLastWriteTimeUtc(Path.Combine(hero, TimedCommandSet.FileName));
            set.SaveDirty();
            Check.False(set.AnyDirty, "saved");
            Check.Eq(TimedCommandSet.Parse(File.ReadAllText(Path.Combine(server, TimedCommandSet.FileName))).Single().Command, "/server2", "the server file has the edit");
            Check.Eq(File.GetLastWriteTimeUtc(Path.Combine(hero, TimedCommandSet.FileName)), heroTime, "the character's file is untouched");

            var other = new TimedCommandSet();
            other.Load(Path.Combine(server, "Sidekick"));
            lock (other.Sync)
            {
                Check.Eq(other.List(TimedScope.Character).Count, 0, "another character: its own (empty) list");
                Check.Eq(other.List(TimedScope.Server).Single().Command, "/server2", "the same server list");
                Check.Eq(other.List(TimedScope.All).Single().Command, "/all", "the same all-characters list");
            }

            File.WriteAllText(Path.Combine(baseDir, TimedCommandSet.FileName), "{ not json");
            var broken = new TimedCommandSet();
            broken.Load(hero, problems);
            Check.True(problems.Count == 1 && problems[0].Contains("all characters"), "a broken file is reported, by its list");
            lock (broken.Sync) Check.Eq(broken.List(TimedScope.Character).Count, 1, "the other lists still load");
            broken.Unload();
            Check.False(broken.Loaded, "unloaded at logout");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ── Scheduler ──────────────────────────────────────────────────────────

    private static void FirstDelayThenInterval()
    {
        var set = Loaded((TimedScope.Character, Cmd("/b", 60)), (TimedScope.Character, Cmd("/hello", 600, first: 120)));
        var s = new TimedCommandScheduler();
        const long t0 = 1_000_000;
        s.OnLogin(set, t0);
        Check.Eq(Next(s, set, At(t0)), null, "nothing at login");
        Check.Eq(Next(s, set, At(t0 + Min - 1)), null, "the 1-minute one: not before a minute");
        Check.Eq(Next(s, set, At(t0 + Min)), "/b", "the 1-minute one at a minute");
        Check.Eq(Next(s, set, At(t0 + 2 * Min - 1)), null, "then not before the next minute");
        Check.Eq(Next(s, set, At(t0 + 2 * Min)), "/b", "at two minutes: /b first (it was due first in the list)");
        Check.Eq(Next(s, set, At(t0 + 2 * Min + 500)), null, "the first-after-2 one waits a second after it");
        Check.Eq(Next(s, set, At(t0 + 2 * Min + 1000)), "/hello", "then the one with First after 2");
        Check.Eq(Next(s, set, At(t0 + 11 * Min)), "/b", "the 1-minute one again (once, no backlog of nine)");
        Check.Eq(Next(s, set, At(t0 + 11 * Min + 1000)), null, "nothing piled up behind it");
        Check.Eq(Next(s, set, At(t0 + 12 * Min + 1000)), "/b", "both due: the one that waited longest first (/b, due at 12:00)");
        Check.Eq(Next(s, set, At(t0 + 12 * Min + 2000)), "/hello", "/hello every 10 minutes from its first run");

        var sec = Loaded((TimedScope.Character, Cmd("/s", 7, first: 3)));
        var s2 = new TimedCommandScheduler();
        s2.OnLogin(sec, 0);
        Check.Eq(Next(s2, sec, At(2999)), null, "first after 3 s: not at 2.999 s");
        Check.Eq(Next(s2, sec, At(3000)), "/s", "at 3 s");
        Check.Eq(Next(s2, sec, At(9999)), null, "every 7 s: not at 9.999 s");
        Check.Eq(Next(s2, sec, At(10_000)), "/s", "at 10 s");
    }

    private static void ShortIntervals()
    {
        var set = Loaded((TimedScope.Character, Cmd("/a", 1)), (TimedScope.Character, Cmd("/b", 1)));
        var s = new TimedCommandScheduler();
        s.OnLogin(set, 0);
        var sent = new List<string>();
        for (long t = 0; t <= 6000; t += 100)
            if (Next(s, set, At(t)) is { } c) sent.Add(t + ":" + c);
        Check.Eq(string.Join(",", sent), "1000:/a,2000:/b,3000:/a,4000:/b,5000:/a,6000:/b",
            "two every-second commands: one a second between them, taking turns");

        TimedCommand typing = Cmd("/t", 600);
        var one = Loaded((TimedScope.Character, typing));
        var s3 = new TimedCommandScheduler();
        s3.OnLogin(one, 0);
        lock (one.Sync) { typing.Seconds = 0; typing.NextDueMs = 0; }   // the box mid-edit (cleared, not left yet)
        Check.Eq(Next(s3, one, At(500)), null, "a 0 in the box is treated as 1 s, not every tick");
        Check.Eq(Next(s3, one, At(1500)), "/t", "it goes at 1 s");
        Check.Eq(Next(s3, one, At(2000)), null, "and not again at once");
        Check.Eq(TimedCommandScheduler.IntervalMs(Cmd("/x", 86400)), 86_400_000L, "a day in ms");
    }

    private static void EveryText()
    {
        Check.Eq(Cmd("/x", 30).EveryText(), "every 30 s", "under a minute: seconds only");
        Check.Eq(Cmd("/x", 60).EveryText(), "every 60 s (1 min)", "a minute");
        Check.Eq(Cmd("/x", 90).EveryText(), "every 90 s (1 min 30 s)", "a minute and a half");
        Check.Eq(Cmd("/x", 600).EveryText(), "every 600 s (10 min)", "ten minutes");
        Check.Eq(Cmd("/x", 5400).EveryText(), "every 5400 s (1 h 30 min)", "an hour and a half");
        Check.Eq(Cmd("/x", 86400).EveryText(), "every 86400 s (24 h)", "a day");
        Check.Eq(TimedCommand.Duration(1), "1 s", "Duration: one second");
    }

    private static void Gates()
    {
        var set = Loaded((TimedScope.Character, Cmd("/b", 60)), (TimedScope.Character, Cmd("/macro", 60, macroOnly: true)));
        var s = new TimedCommandScheduler();
        s.OnLogin(set, 0);
        Check.Eq(Next(s, set, At(Min, inWorld: false)), null, "not in the world: nothing");
        Check.Eq(Next(s, set, At(Min, portal: true, macro: false)), null, "in portal space: nothing");
        Check.Eq(Next(s, set, At(30 * Min, macro: false)), "/b", "out of the portal (half an hour later): /b once");
        Check.Eq(Next(s, set, At(30 * Min + 2000, macro: false)), null, "the macro-only one waits while the macro is stopped, and /b is not due");
        Check.Eq(Next(s, set, At(30 * Min + 3000, macro: true)), "/macro", "the macro starts: it goes");
        Check.Eq(Next(s, set, At(30 * Min + 3500)), null, "nothing more");
        Check.Eq(Next(s, set, At(31 * Min)), "/b", "/b a minute after its last run");

        var empty = new TimedCommandSet();
        Check.Eq(Next(s, empty, At(99 * Min)), null, "no lists loaded (before the settings folder is known): nothing");
    }

    private static void OffEmptyAndScopes()
    {
        TimedCommand off = Cmd("/off", 60, on: false), blank = Cmd("", 60);
        var set = Loaded((TimedScope.Character, off), (TimedScope.Character, blank),
            (TimedScope.Server, Cmd("/server", 60)), (TimedScope.All, Cmd("/all", 60)));
        var s = new TimedCommandScheduler();
        s.OnLogin(set, 0);
        var sent = new List<string>();
        for (long t = Min; t < Min + 5000; t += 250)
            if (Next(s, set, At(t)) is { } c) sent.Add(c);
        Check.Eq(string.Join(",", sent), "/server,/all", "the server's and every character's lists run; off and empty don't");
        lock (set.Sync)
        {
            set.List(TimedScope.Server)[0].Enabled = false;   // out of the way of the next checks
            set.List(TimedScope.All)[0].Enabled = false;
            off.Enabled = true;
        }
        Check.Eq(Next(s, set, At(5 * Min)), null, "turned on at 5 min: it starts a full interval from now");
        Check.Eq(Next(s, set, At(6 * Min)), "/off", "a minute later it runs");
        Check.Eq(TimedCommandScheduler.NextText(Cmd("/x", 300, macroOnly: true), 0, macroRunning: false), "waits for the macro", "the window's Next: waiting for the macro");
        TimedCommand due = Cmd("/x", 300);
        due.NextDueMs = 1000 + 90_000;
        Check.Eq(TimedCommandScheduler.NextText(due, 1000, true), "in 1:30", "the window's Next: in m:ss");
    }

    // ── /ra timed ──────────────────────────────────────────────────────────

    private static void ChatCommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "rynth-timed-" + Guid.NewGuid().ToString("N"));
        try
        {
            FakeHost.Reset();
            FakeHost.Create(0x50000F77);
            var p = new RynthAiPlugin();
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
            var set = (TimedCommandSet)typeof(RynthAiPlugin).GetField("_timed", Any)!.GetValue(p)!;
            MethodInfo handle = typeof(RynthAiPlugin).GetMethod("HandleTimedCommand", Any)!;
            string Say(string line)
            {
                int before = FakeHost.Chat.Count;
                handle.Invoke(p, new object[] { line.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
                return string.Join(" | ", FakeHost.Chat.Skip(before));
            }

            Check.True(Say("/ra timed list").Contains("load once you are in the world"), "before the lists load: it says why");
            set.Load(Path.Combine(root, "SettingsProfiles", "Server One", "Hero"));

            Check.True(Say("/ra timed list").Contains("No timed commands for this character"), "an empty list says so");
            string add = Say("/ra timed add 300 /e waves hello");
            Check.True(add.Contains("Timed command 1 for this character: /e waves hello - every 300 s (5 min), first in 300 s (5 min)"),
                "add replies with the command, in seconds (" + add + ")");
            lock (set.Sync) Check.Eq(set.List(TimedScope.Character)[0].Seconds, 300, "add takes seconds");
            Check.True(File.Exists(Path.Combine(root, "SettingsProfiles", "Server One", "Hero", TimedCommandSet.FileName)), "and saves the character's file");
            Check.True(Say("/ra timed server add 30 /b").Contains("for this server: /b - every 30 s"), "server add (30 s)");
            Check.True(File.Exists(Path.Combine(root, "SettingsProfiles", "Server One", TimedCommandSet.FileName)), "saved in the server's folder");
            Check.True(Say("/ra timed all add 3600 /ra status").Contains("for all characters: /ra status - every 3600 s (1 h)"), "all add (an hour)");
            Check.True(File.Exists(Path.Combine(root, "SettingsProfiles", TimedCommandSet.FileName)), "saved in the base folder");
            Check.True(Say("/ra timed add 1 /e nod").Contains("every 1 s"), "every second is allowed");
            Check.True(Say("/ra timed remove 2").Contains("Removed timed command 2"), "(removed again)");

            string list = Say("/ra timed list");
            Check.True(list.Contains("Timed commands for this character (1)") && list.Contains("1. /e waves hello - every 300 s (5 min)"), "list shows it (" + list + ")");
            Check.True(Say("/ra timed off 1").Contains("Timed command 1 for this character off: /e waves hello"), "off replies");
            lock (set.Sync) Check.False(set.List(TimedScope.Character)[0].Enabled, "and it is off");
            Check.True(Say("/ra timed list").Contains("off"), "list says off");
            Check.True(Say("/ra timed on 1").Contains("on: /e waves hello"), "on replies");
            Check.True(Say("/ra timed off 7").Contains("There is no timed command 7"), "a number past the list says so");
            Check.True(Say("/ra timed add x /b").Contains("Usage: /ra timed add <seconds 1-86400>"), "a bad interval gives the usage");
            Check.True(Say("/ra timed add 0 /b").Contains("Usage: /ra timed add <seconds"), "0 s is too short");
            Check.True(Say("/ra timed add 86401 /b").Contains("Usage: /ra timed add <seconds"), "over a day is too long");
            Check.True(Say("/ra timed add 0.5 /b").Contains("Usage: /ra timed add <seconds"), "whole seconds only");
            lock (set.Sync) Check.Eq(set.List(TimedScope.Character).Count, 1, "none of those added anything");
            Check.True(Say("/ra timed add 5").Contains("Usage"), "no command gives the usage");
            Check.True(Say("/ra timed frob").Contains("Usage: /ra timed"), "an unknown word gives the usage");

            FakeHost.ChatCommands.Clear();
            string run = Say("/ra timed run 1");
            Check.True(run.Contains("Ran timed command 1 for this character: /e waves hello"), "run replies");
            Check.True(FakeHost.ChatCommands.Contains("/e waves hello"), "and the command went to the chat parser (" + string.Join(",", FakeHost.ChatCommands) + ")");

            Check.True(Say("/ra timed server remove 1").Contains("Removed timed command 1 for this server: /b"), "remove replies");
            lock (set.Sync) Check.Eq(set.List(TimedScope.Server).Count, 0, "and it is gone");
            Check.True(Say("/ra timed show").Length > 0, "show replies (either way)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Manifest()
    {
        string without = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections);
        Check.False(without.Contains("timedcommands"), "not asked for: not there");
        string with = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, timedCommands: true);
        using var doc = System.Text.Json.JsonDocument.Parse(with);
        var win = doc.RootElement.GetProperty("windows").EnumerateArray().Single();
        Check.Eq(win.GetProperty("id").GetString(), "timedcommands", "the window's id");
        Check.Eq(win.GetProperty("title").GetString(), RynthAiPlugin.TimedCommandsTitle, "its title");
        Check.Eq(win.GetProperty("surface").GetString(), RynthAiPlugin.TimedCommandsSurface, "its surface");
        Check.False(win.GetProperty("bar").GetBoolean(), "no bar button (the dashboard's options menu and /ra timed show open it)");
        Check.False(win.TryGetProperty("barMenu", out _), "and no bar menu");
        Check.True(win.GetProperty("icon").GetString()!.Length > 0, "an icon for its header");
        string both = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, monsterStats: true, timedCommands: true);
        using var doc2 = System.Text.Json.JsonDocument.Parse(both);
        Check.Eq(doc2.RootElement.GetProperty("windows").GetArrayLength(), 2, "with Monster Stats too: both windows");
    }
}
