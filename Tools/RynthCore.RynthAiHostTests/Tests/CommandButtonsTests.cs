using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CommandButtons;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Command Buttons (2026-11-26, a player's ask): a window of buttons that each send one chat line, from
// three lists (this character, this server, all characters) kept beside the settings profiles. The file
// format and its layout, the folders, the chat line rules, colours, the grid's columns, moving, copying
// from another character, /ra buttons, a press going to RynthCore's chat parser, and the window in the
// sections manifest (no bar button).
internal static class CommandButtonsTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("command buttons: the file round-trips (label, command, colour, ask, layout); bad values are cleaned or clamped", FileRoundTrip);
        r.Add("command buttons: the three lists load from the character's, the server's and the base folder; only what changed is saved; layout only in the character's", Folders);
        r.Add("command buttons: chat line rules (one line, 200 characters, never a button that presses buttons) and what each line is", CommandLineRules);
        r.Add("command buttons: colours as #RRGGBB, and text that reads on them", Colours);
        r.Add("command buttons: columns (Auto fits the width), add up to the list's limit, move up and down", LayoutAddMove);
        r.Add("command buttons: copy from another character on the server appends its buttons", CopyFrom);
        r.Add("command buttons: /ra buttons add, list, press and remove (each replies); a press goes to RynthCore's chat parser on the tick", ChatCommand);
        r.Add("command buttons: the window is in the sections manifest without a bar button; the options menu and /ra buttons show|hide open and close it", WindowAndManifest);
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "rynth-buttons-" + Guid.NewGuid().ToString("N"));

    private static CommandButton B(string label, string command, uint color = 0, bool confirm = false) =>
        new() { Label = label, Command = command, Color = color, Confirm = confirm };

    private static void FileRoundTrip()
    {
        var layout = new ButtonLayout { Columns = 4, Width = 90, Height = 30 };
        var list = new[] { B("Start", "/ra start", 0xFF336699, false), B("", "*snow angel*", 0, true), B("Home", "@home") };
        string json = CommandButtonSet.Serialize(list, layout);
        List<CommandButton> back = CommandButtonSet.Parse(json, out ButtonLayout? l2);
        Check.Eq(back.Count, 3, "three buttons back");
        Check.True(back[0].Label == "Start" && back[0].Command == "/ra start" && back[0].Color == 0xFF336699 && !back[0].Confirm, "the first, with its colour");
        Check.True(back[1].Label == "" && back[1].Command == "*snow angel*" && back[1].Color == 0 && back[1].Confirm, "no label, theme colour, asks first");
        Check.Eq(back[2].Shown, "Home", "the label is what it shows");
        Check.Eq(back[1].Shown, "*snow angel*", "no label: it shows its command");
        Check.True(l2 != null && l2.Columns == 4 && l2.Width == 90 && l2.Height == 30, "the layout");
        Check.False(json.Contains("\"color\": \"\""), "the theme's colour isn't written");

        CommandButtonSet.Parse(CommandButtonSet.Serialize(list, null), out ButtonLayout? none);
        Check.True(none == null, "no layout written: none read");

        string messy = "{ \"version\": 1, \"layout\": { \"columns\": 99, \"width\": 5, \"height\": 5000 }, \"buttons\": [ "
            + "{ \"label\": \"" + new string('L', 50) + "\", \"command\": \"/ra start\\n/ra stop\", \"color\": \"blue\", \"confirm\": \"yes\" }, "
            + "{ \"command\": \"" + new string('x', 300) + "\" }, 7, { } ] }";
        List<CommandButton> m = CommandButtonSet.Parse(messy, out ButtonLayout? ml);
        Check.True(ml != null && ml.Columns == ButtonLayout.ColumnsMax && ml.Width == ButtonLayout.WidthMin && ml.Height == ButtonLayout.HeightMax, "the layout is clamped");
        Check.Eq(m.Count, 3, "a non-object entry is skipped; an empty one is an empty button");
        Check.Eq(m[0].Label.Length, CommandButton.MaxLabel, "a long label is cut to 32");
        Check.Eq(m[0].Command, "/ra start /ra stop", "a command is one line");
        Check.True(m[0].Color == 0 && !m[0].Confirm, "a bad colour is the theme's; confirm must be true to ask");
        Check.Eq(m[1].Command.Length, CommandLine.MaxLength, "a long command is cut to 200");
        Check.True(m[2].Label == "" && m[2].Command == "" && m[2].Shown == "(empty)", "an empty button");

        var many = Enumerable.Range(1, CommandButtonSet.MaxPerList + 5).Select(i => B("b" + i, "/b" + i)).ToList();
        Check.Eq(CommandButtonSet.Parse(CommandButtonSet.Serialize(many, null), out _).Count, CommandButtonSet.MaxPerList, "at most 60 a list");
    }

    private static void Folders()
    {
        string root = TempRoot();
        string baseDir = Path.Combine(root, "SettingsProfiles"), server = Path.Combine(baseDir, "Server One"), hero = Path.Combine(server, "Hero");
        Directory.CreateDirectory(hero);
        try
        {
            File.WriteAllText(Path.Combine(hero, CommandButtonSet.FileName), CommandButtonSet.Serialize(new[] { B("Me", "/me") }, new ButtonLayout { Columns = 2, Width = 70, Height = 24 }));
            File.WriteAllText(Path.Combine(server, CommandButtonSet.FileName), CommandButtonSet.Serialize(new[] { B("S1", "/s1"), B("S2", "/s2") }, new ButtonLayout { Columns = 9 }));
            File.WriteAllText(Path.Combine(baseDir, CommandButtonSet.FileName), CommandButtonSet.Serialize(new[] { B("All", "/all") }, null));
            var set = new CommandButtonSet();
            var problems = new List<string>();
            set.Load(hero, problems);
            Check.Eq(problems.Count, 0, "no problems");
            Check.True(set.Loaded && set.Character == "Hero" && set.Server == "Server One", "the character and server from the folders");
            lock (set.Sync)
            {
                Check.Eq(set.List(ButtonScope.Character).Single().Command, "/me", "this character's list");
                Check.Eq(string.Join(",", set.List(ButtonScope.Server).Select(b => b.Command)), "/s1,/s2", "the server's list");
                Check.Eq(set.List(ButtonScope.All).Single().Command, "/all", "every character's list");
                Check.True(set.Layout.Columns == 2 && set.Layout.Width == 70, "the layout is the character's (not the server file's)");
                set.List(ButtonScope.Server).RemoveAt(0);
                set.MarkChanged(ButtonScope.Server, 1);
            }
            DateTime heroTime = File.GetLastWriteTimeUtc(Path.Combine(hero, CommandButtonSet.FileName));
            set.SaveDirty();
            Check.False(set.AnyDirty, "saved");
            string serverJson = File.ReadAllText(Path.Combine(server, CommandButtonSet.FileName));
            Check.Eq(CommandButtonSet.Parse(serverJson, out ButtonLayout? sl).Single().Command, "/s2", "the server file has the edit");
            Check.True(sl == null, "and no layout (only a character's file has one)");
            Check.Eq(File.GetLastWriteTimeUtc(Path.Combine(hero, CommandButtonSet.FileName)), heroTime, "the character's file is untouched");

            // The layout is saved with this character's list.
            lock (set.Sync) { set.Layout.Columns = 5; set.MarkChanged(ButtonScope.Character, 2); }
            set.SaveDirty();
            var again = new CommandButtonSet();
            again.Load(hero);
            lock (again.Sync) Check.Eq(again.Layout.Columns, 5, "the layout survives a reload");

            var other = new CommandButtonSet();
            other.Load(Path.Combine(server, "Sidekick"));
            lock (other.Sync)
            {
                Check.Eq(other.List(ButtonScope.Character).Count, 0, "another character: its own (empty) list");
                Check.Eq(other.List(ButtonScope.Server).Single().Command, "/s2", "the same server list");
                Check.Eq(other.List(ButtonScope.All).Single().Command, "/all", "the same all-characters list");
                Check.True(other.Layout.Columns == 0 && other.Layout.Width == ButtonLayout.DefaultWidth, "and the default layout");
            }

            File.WriteAllText(Path.Combine(baseDir, CommandButtonSet.FileName), "{ not json");
            var broken = new CommandButtonSet();
            problems.Clear();
            broken.Load(hero, problems);
            Check.True(problems.Count == 1 && problems[0].Contains("all characters"), "a broken file is reported: " + string.Join(" | ", problems));
            lock (broken.Sync) Check.Eq(broken.List(ButtonScope.Character).Count, 1, "and the others still load");
            broken.SaveDirty();
            Check.Eq(File.ReadAllText(Path.Combine(baseDir, CommandButtonSet.FileName)), "{ not json", "and it isn't saved over unless edited");

            broken.Unload();
            Check.False(broken.Loaded, "logout forgets them");
            lock (broken.Sync) Check.Eq(broken.List(ButtonScope.Character).Count, 0, "(empty)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void CommandLineRules()
    {
        Check.Eq(CommandLine.KindOf("/ra start"), CommandKind.Slash, "/ra start: a command");
        Check.Eq(CommandLine.KindOf("/lua start threads"), CommandKind.Slash, "/lua: a command");
        Check.Eq(CommandLine.KindOf("*snow angel*"), CommandKind.Emote, "*snow angel*: an emote");
        Check.Eq(CommandLine.KindOf("*"), CommandKind.Say, "a lone * is speech");
        Check.Eq(CommandLine.KindOf("* *"), CommandKind.Say, "* * is speech");
        Check.Eq(CommandLine.KindOf("*wave"), CommandKind.Say, "*wave without the closing * is speech");
        Check.Eq(CommandLine.KindOf("@home"), CommandKind.Server, "@home: a server command");
        Check.Eq(CommandLine.KindOf("@e waves"), CommandKind.Emote, "@e waves: an emote");
        Check.Eq(CommandLine.KindOf("@emote"), CommandKind.Emote, "@emote alone: an emote");
        Check.Eq(CommandLine.KindOf("@echo hi"), CommandKind.Server, "@echo is not @e");
        Check.Eq(CommandLine.KindOf("; grins"), CommandKind.Emote, "; grins: an emote");
        Check.Eq(CommandLine.KindOf(":"), CommandKind.Say, "a lone : is speech");
        Check.Eq(CommandLine.KindOf("hello all"), CommandKind.Say, "plain text is said aloud");
        Check.Eq(CommandLine.KindOf(""), CommandKind.Empty, "nothing");

        CommandCheck ok = CommandLine.Check("  /ra start  ");
        Check.True(ok.CanSend && ok.Line == "/ra start" && ok.Kind == CommandKind.Slash, "trimmed and sendable");
        CommandCheck empty = CommandLine.Check("   ");
        Check.True(empty.Problem == null && !empty.CanSend && empty.Kind == CommandKind.Empty, "empty is no problem, but sends nothing");
        CommandCheck two = CommandLine.Check("/ra start\n/ra stop");
        Check.True(!two.CanSend && two.Problem!.Contains("One line"), "two lines are refused: " + two.Problem);
        CommandCheck tab = CommandLine.Check("/ra\tstart");
        Check.True(tab.CanSend && tab.Line == "/ra start", "a tab is a space");
        CommandCheck control = CommandLine.Check("/ra st\u0007art");
        Check.True(control.CanSend && control.Line == "/ra start", "control characters go");
        CommandCheck tooLong = CommandLine.Check("/" + new string('x', 200));
        Check.True(!tooLong.CanSend && tooLong.Problem!.Contains("200"), "over 200 characters is refused: " + tooLong.Problem);
        Check.True(CommandLine.Check("/" + new string('x', 199)).CanSend, "200 is fine");
        Check.Eq(CommandLine.Clean("/" + new string('x', 300)).Length, CommandLine.MaxLength, "Clean cuts to 200");

        foreach (string loop in new[] { "/ra buttons press 1", "/RA Buttons  PRESS Start", "/ra button run 2", "/ra buttons server press 1", "/ra buttons all run x" })
        {
            CommandCheck c = CommandLine.Check(loop);
            Check.True(!c.CanSend && c.Problem!.Contains("can't press buttons"), "a button can't press buttons: " + loop);
        }
        foreach (string fine in new[] { "/ra buttons", "/ra buttons show", "/ra buttons list", "/ra start", "/rab press 1", "/ra buttonsx press 1" })
            Check.True(CommandLine.Check(fine).CanSend, "allowed: " + fine);

        foreach (CommandKind k in Enum.GetValues<CommandKind>())
            Check.True(CommandLine.Describe(k).Length > 0, "each kind is described: " + k);
        Check.True(CommandLine.Describe(CommandKind.Say).Contains("aloud"), "plain text says it is said aloud");

        Check.Eq(CommandButton.CleanLabel("  Jump\nhigh  "), "Jump high", "a label is one line, trimmed");
        Check.Eq(CommandButton.CleanLabel(new string('a', 40)).Length, CommandButton.MaxLabel, "a label is at most 32");
    }

    private static void Colours()
    {
        Check.True(CommandButton.TryParseColor("#FF8000", out uint orange), "#FF8000 reads");
        Check.Eq(orange, 0xFF0080FFu, "as ImGui's ABGR, opaque");
        Check.Eq(CommandButton.ColorText(orange), "#FF8000", "and writes back");
        Check.True(CommandButton.TryParseColor("336699", out uint noHash) && CommandButton.ColorText(noHash) == "#336699", "without #");
        Check.True(CommandButton.TryParseColor(" #abcdef ", out uint lower) && CommandButton.ColorText(lower) == "#ABCDEF", "lower case, spaces");
        foreach (string bad in new[] { "", "#12345", "#1234567", "#GGHHII", "blue", null! })
            Check.False(CommandButton.TryParseColor(bad, out uint z) || z != 0, "refused: " + (bad ?? "null"));
        Check.Eq(CommandButton.ColorText(0), "", "0 is the theme's colour");
        Check.True(CommandButton.TryParseColor("#000000", out uint black) && black != 0, "black is a colour, not the theme's");
        Check.Eq(CommandButton.TextOn(0xFFFFFFFF), 0xFF141414u, "dark text on white");
        Check.Eq(CommandButton.TextOn(0xFF202020), 0xFFF2F2F2u, "light text on dark grey");
        CommandButton.TryParseColor("#FFEB3B", out uint yellow);
        Check.Eq(CommandButton.TextOn(yellow), 0xFF141414u, "dark text on yellow");
        CommandButton.TryParseColor("#1565C0", out uint blue);
        Check.Eq(CommandButton.TextOn(blue), 0xFFF2F2F2u, "light text on blue");
    }

    private static void LayoutAddMove()
    {
        Check.Eq(ButtonLayout.ColumnsFor(3, 1000, 100, 4), 3, "set columns are kept");
        Check.Eq(ButtonLayout.ColumnsFor(0, 308, 100, 4), 3, "Auto: three 100-wide buttons fit 308 (with two gaps of 4)");
        Check.Eq(ButtonLayout.ColumnsFor(0, 307, 100, 4), 2, "but not 307");
        Check.Eq(ButtonLayout.ColumnsFor(0, 30, 100, 4), 1, "always at least one");
        Check.Eq(ButtonLayout.ColumnsFor(0, 100000, 40, 4), ButtonLayout.ColumnsMax, "at most 12");
        Check.Eq(ButtonLayout.ColumnsFor(0, float.NaN, 100, 4), 1, "a NaN width is one column");
        Check.Eq(ButtonLayout.ColumnsText(0), "Auto", "0 is Auto");
        Check.Eq(ButtonLayout.ColumnsText(4), "4", "else the number");
        var l = new ButtonLayout { Columns = -3, Width = 9999, Height = 0 };
        l.Clamp();
        Check.True(l.Columns == 0 && l.Width == ButtonLayout.WidthMax && l.Height == ButtonLayout.HeightMin, "clamped");

        string root = TempRoot();
        try
        {
            var set = new CommandButtonSet();
            set.Load(Path.Combine(root, "SettingsProfiles", "S", "Hero"));
            lock (set.Sync)
            {
                CommandButton? a = set.Add(ButtonScope.Character, " Jump\n", " /mt jump ", 5);
                Check.True(a != null && a.Label == "Jump" && a.Command == "/mt jump", "add cleans the label and command");
                Check.True(set.AnyDirty && set.ChangedAtMs == 5, "and marks the list changed");
                set.Add(ButtonScope.Character, "B", "/b", 6);
                set.Add(ButtonScope.Character, "C", "/c", 7);
                Check.True(set.Move(ButtonScope.Character, 2, -1, 8), "C up");
                Check.Eq(string.Join(",", set.List(ButtonScope.Character).Select(b => b.Label)), "Jump,C,B", "moved");
                Check.False(set.Move(ButtonScope.Character, 0, -1, 9), "the first can't go up");
                Check.False(set.Move(ButtonScope.Character, 2, 1, 9), "the last can't go down");
                Check.False(set.Move(ButtonScope.Character, 7, 1, 9), "no such button");
                Check.True(set.Move(ButtonScope.Character, 0, 1, 9), "Jump down");
                Check.Eq(string.Join(",", set.List(ButtonScope.Character).Select(b => b.Label)), "C,Jump,B", "moved again");
                while (set.List(ButtonScope.Server).Count < CommandButtonSet.MaxPerList) set.Add(ButtonScope.Server, "x", "/x", 10);
                Check.True(set.Add(ButtonScope.Server, "y", "/y", 11) == null, "a full list takes no more");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void CopyFrom()
    {
        string root = TempRoot();
        string server = Path.Combine(root, "SettingsProfiles", "Server One");
        try
        {
            Directory.CreateDirectory(Path.Combine(server, "Hero"));
            Directory.CreateDirectory(Path.Combine(server, "Alt"));
            Directory.CreateDirectory(Path.Combine(server, "Empty"));    // no buttons file: not offered
            Directory.CreateDirectory(Path.Combine(server, "Bee"));
            File.WriteAllText(Path.Combine(server, "Alt", CommandButtonSet.FileName),
                CommandButtonSet.Serialize(new[] { B("Buff", "/ra buff", 0xFF00FF00), B("Quit", "/rc quit", 0, true) }, new ButtonLayout { Columns = 7 }));
            File.WriteAllText(Path.Combine(server, "Bee", CommandButtonSet.FileName), CommandButtonSet.Serialize(new[] { B("x", "/x") }, null));
            File.WriteAllText(Path.Combine(server, "Hero", CommandButtonSet.FileName), CommandButtonSet.Serialize(new[] { B("Mine", "/mine") }, null));

            var set = new CommandButtonSet();
            Check.Eq(set.OtherCharacters().Count, 0, "before a load: nobody");
            set.Load(Path.Combine(server, "Hero"));
            Check.Eq(string.Join(",", set.OtherCharacters()), "Alt,Bee", "the others with buttons, sorted, not this character");

            int added = set.CopyFrom("Alt", 20, out string problem);
            Check.Eq(added, 2, "two copied (" + problem + ")");
            lock (set.Sync)
            {
                List<CommandButton> mine = set.List(ButtonScope.Character);
                Check.Eq(string.Join(",", mine.Select(b => b.Label)), "Mine,Buff,Quit", "after this character's own");
                Check.True(mine[1].Color == 0xFF00FF00 && mine[2].Confirm, "with their colour and ask");
                Check.Eq(set.Layout.Columns, 0, "the layout stays this character's");
            }
            Check.True(set.AnyDirty, "and it will be saved");
            Check.Eq(set.CopyFrom("Empty", 21, out problem), -1, "a character with no buttons: -1");
            Check.True(problem.Contains("no buttons"), "and says so: " + problem);
            Check.Eq(set.CopyFrom("..", 21, out _), -1, "no climbing out of the server's folder");
            Check.Eq(set.CopyFrom("a\\b", 21, out _), -1, "no paths");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void RunDashActions(RynthAiPlugin p)
    {
        var q = (ConcurrentQueue<Action>)typeof(RynthAiPlugin).GetField("_dashActions", Any)!.GetValue(p)!;
        while (q.TryDequeue(out Action? a)) a();
    }

    private static void ChatCommand()
    {
        string root = TempRoot();
        try
        {
            FakeHost.Reset();
            FakeHost.Create(0x50000F77);
            var p = new RynthAiPlugin();
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
            var set = (CommandButtonSet)typeof(RynthAiPlugin).GetField("_buttons", Any)!.GetValue(p)!;
            MethodInfo handle = typeof(RynthAiPlugin).GetMethod("HandleButtonsCommand", Any)!;
            string Say(string line)
            {
                int before = FakeHost.Chat.Count;
                handle.Invoke(p, new object[] { line.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
                return string.Join(" | ", FakeHost.Chat.Skip(before));
            }

            Check.True(Say("/ra buttons list").Contains("load once you are in the world"), "before the lists load: it says why");
            string hero = Path.Combine(root, "SettingsProfiles", "Server One", "Hero");
            set.Load(hero);

            Check.True(Say("/ra buttons list").Contains("No command buttons for this character"), "an empty list says so");
            string add = Say("/ra buttons add Angel = *snow angel*");
            Check.True(add.Contains("Command button 1 for this character: Angel sends *snow angel*"), "add with a label replies (" + add + ")");
            Check.True(File.Exists(Path.Combine(hero, CommandButtonSet.FileName)), "and saves the character's file");
            Check.True(Say("/ra buttons add /ra start").Contains("Command button 2 for this character: /ra start sends /ra start"), "add without a label");
            Check.True(Say("/ra buttons server add Home = @home").Contains("for this server: Home sends @home"), "server add");
            Check.True(File.Exists(Path.Combine(root, "SettingsProfiles", "Server One", CommandButtonSet.FileName)), "saved in the server's folder");
            Check.True(Say("/ra buttons all add Loc = /loc").Contains("for all characters: Loc sends /loc"), "all add");
            Check.True(Say("/ra buttons add Loop = /ra buttons press 1").Contains("can't press buttons"), "a button that presses buttons is refused");
            Check.True(Say("/ra buttons add").Contains("Usage: /ra buttons add <label> = <command>"), "add with nothing gives the usage");
            Check.True(Say("/ra buttons add Empty =").Contains("Usage"), "a label with no command gives the usage");
            lock (set.Sync) Check.Eq(set.List(ButtonScope.Character).Count, 2, "none of those added anything");

            string list = Say("/ra buttons list");
            Check.True(list.Contains("Command buttons for this character (2)") && list.Contains("1. Angel: *snow angel*") && list.Contains("2. (no label): /ra start"),
                "list shows them (" + list + ")");

            // A press: queued for the tick, then RynthCore's chat parser gets the line as typed.
            FakeHost.ChatCommands.Clear();
            string press = Say("/ra buttons press Angel");
            Check.True(press.Contains("Pressing command button 1 for this character: Angel"), "press by label replies (" + press + ")");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "nothing sent inside the chat command itself");
            RunDashActions(p);
            Check.Eq(string.Join(" | ", FakeHost.ChatCommands), "*snow angel*", "the tick sends the line to the chat parser");
            Say("/ra buttons server press 1");
            RunDashActions(p);
            Check.Eq(FakeHost.ChatCommands.LastOrDefault(), "@home", "press by number, on the server's list");
            Check.True(Say("/ra buttons press 9").Contains("There is no command button '9'"), "a number past the list says so");
            Check.True(Say("/ra buttons press").Contains("Usage: /ra buttons press <n or label>"), "press with nothing gives the usage");

            // A button with no command: not sent, and it says why.
            lock (set.Sync) set.Add(ButtonScope.Character, "Blank", "", 1);
            int sent = FakeHost.ChatCommands.Count;
            Say("/ra buttons press Blank");
            int chatBefore = FakeHost.Chat.Count;
            RunDashActions(p);
            Check.Eq(FakeHost.ChatCommands.Count, sent, "an empty button sends nothing");
            Check.True(string.Join(" | ", FakeHost.Chat.Skip(chatBefore)).Contains("wasn't sent: it has no command yet"), "and says why");

            Check.True(Say("/ra buttons remove 1").Contains("Removed command button 1 for this character: Angel"), "remove replies");
            lock (set.Sync) Check.Eq(set.List(ButtonScope.Character)[0].Command, "/ra start", "and it is gone");
            Check.True(CommandButtonSet.Parse(File.ReadAllText(Path.Combine(hero, CommandButtonSet.FileName)), out _).All(b => b.Label != "Angel"), "from the file too");
            Check.True(Say("/ra buttons frobnicate").Contains("Usage: /ra buttons"), "an unknown verb gives the usage");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
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

    private static void WindowAndManifest()
    {
        string without = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections);
        Check.False(without.Contains("commandbuttons"), "not asked for: not there");
        string with = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, commandButtons: true);
        using (var doc = System.Text.Json.JsonDocument.Parse(with))
        {
            var win = doc.RootElement.GetProperty("windows").EnumerateArray().Single();
            Check.Eq(win.GetProperty("id").GetString(), "commandbuttons", "the window's id");
            Check.Eq(win.GetProperty("title").GetString(), RynthAiPlugin.CommandButtonsTitle, "its title");
            Check.Eq(win.GetProperty("surface").GetString(), RynthAiPlugin.CommandButtonsSurface, "its surface");
            Check.False(win.GetProperty("bar").GetBoolean(), "no bar button (the options menu, /ra buttons and Find a Feature open it)");
            Check.True(win.GetProperty("icon").GetString()!.Length > 0, "an icon for its header");
        }
        string all = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections,
            dashboard: true, monsterStats: true, timedCommands: true, activeGear: true, featureSearch: true, commandButtons: true);
        using (var doc = System.Text.Json.JsonDocument.Parse(all))
            Check.True(doc.RootElement.GetProperty("windows").GetArrayLength() <= 8, "every RynthAi window fits the engine's 8");

        try
        {
            FakeHost.Reset();
            PanelCalls.Clear();
            FakeHost.ConfigureApi = PanelApi;
            FakeHost.Create(0x50000F77);
            var p = new RynthAiPlugin();
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });

            // What the dashboard's ⋯ > Command buttons runs (queued to the tick by RynthAiPlugin.Dashboard.cs).
            typeof(RynthAiPlugin).GetMethod("ToggleCommandButtonsWindow", Any)!.Invoke(p, null);
            Check.Eq(PanelCalls.Count, 1, "one call to the engine");
            Check.True(PanelCalls[0].Key == RynthAiPlugin.CommandButtonsTitle && PanelCalls[0].Mode == (int)RynthPanelOpen.Toggle,
                "toggles the Command Buttons window (" + PanelCalls[0] + ")");

            MethodInfo handle = typeof(RynthAiPlugin).GetMethod("HandleButtonsCommand", Any)!;
            string Say(string line)
            {
                int before = FakeHost.Chat.Count;
                handle.Invoke(p, new object[] { line.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
                return string.Join(" | ", FakeHost.Chat.Skip(before));
            }
            Check.True(Say("/ra buttons").Contains("Command Buttons window open"), "/ra buttons alone opens it");
            Check.Eq(PanelCalls.Last().Mode, (int)RynthPanelOpen.Open, "(open, not toggle: Find a Feature's Open button runs it)");
            Check.True(Say("/ra buttons show").Contains("Command Buttons window open"), "/ra buttons show opens it");
            Check.True(Say("/ra buttons hide").Contains("Command Buttons window closed"), "/ra buttons hide closes it");
            Check.Eq(PanelCalls.Last().Mode, (int)RynthPanelOpen.Close, "(close)");
            Check.True(Say("/ra buttons edit").Contains("editing the buttons for this character"), "/ra buttons edit opens it to edit");
            Check.True(Say("/ra buttons server edit").Contains("editing the buttons for this server"), "on the server's list");
        }
        finally
        {
            FakeHost.ConfigureApi = null;
        }
    }
}
