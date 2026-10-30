using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// .nav files as RynthAi reads them (NavRouteParser, VTank "uTank2 NAV 1.2"): one file per
// supported route type (Circular, Linear, Once, Follow), every waypoint kind (point,
// checkpoint, recall, pause, chat, vendor, portal, NPC, jump), and malformed files. The
// fixtures in Fixtures\nav were written by hand for these tests.
internal static class NavFormatTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "nav", name);

    /// <summary>Scratch folder next to the exe for files these tests write. Emptied per test.</summary>
    private static string Scratch()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch-nav");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Register(Runner r)
    {
        r.Add("nav format: Circular hunting route file (checkpoint, pause, chat, jump)", CircularFile);
        r.Add("nav format: Linear route file writes back line for line", LinearFile);
        r.Add("nav format: Once route file (recalls, vendor, portal, NPC)", OnceFile);
        r.Add("nav format: Follow file names the leader", FollowFile);
        r.Add("nav format: route type numbers 1-4 survive a save and load", RouteTypeNumbers);
        r.Add("nav format: CRLF file, spaces around numbers, Save then Load", CrlfAndSave);
        r.Add("nav format: Z repair only above 5 nav units", ZRepairBoundary);
        r.Add("nav format: tie and shift flags", Flags);
        r.Add("nav format: empty, missing, header-only and wrong-header files load empty", EmptyFiles);
        r.Add("nav format: malformed header warns", MalformedHeader);
        r.Add("nav format: truncated file keeps the points before the cut and warns", Truncated);
        r.Add("nav format: a bad number stops at that point and warns", BadNumber);
        r.Add("nav format: an unreadable file warns instead of throwing", UnreadableFile);
        r.Add("nav format: pinned oddities (short count, bad route type, comma decimals)", PinnedOddities);
    }

    private static void CircularFile()
    {
        var warnings = new List<string>();
        var route = NavRouteParser.Load(Fixture("circular-hunt.nav"), warnings.Add);
        Check.Null(route.LoadWarning, "no warning");
        Check.Eq(warnings.Count, 0, "warn callback not called");
        Check.Eq(route.RouteType, NavRouteType.Circular, "route type 1 is Circular");
        Check.Eq(route.Points.Count, 7, "point count");
        if (route.Points.Count != 7) return;
        var p = route.Points;

        var kinds = new[] { NavPointType.Point, NavPointType.Point, NavPointType.Checkpoint, NavPointType.Pause,
                            NavPointType.Chat, NavPointType.Jump, NavPointType.Point };
        for (int i = 0; i < kinds.Length; i++) Check.Eq(p[i].Type, kinds[i], $"[{i}] type");

        Check.Near(p[0].EW, 33.6012, 1e-9, "[0] EW (first coordinate line)");
        Check.Near(p[0].NS, 42.4987, 1e-9, "[0] NS (second coordinate line)");
        Check.Near(p[0].Z, 0.05, 1e-9, "[0] Z");
        Check.Eq(p[3].PauseTimeMs, 3000, "[3] pause 3 s");
        Check.Eq(p[4].ChatCommand, "/vt opt set EnableLooting true", "[4] chat text kept whole, spaces included");
        Check.Near(p[5].JumpHeading, 135.5, 1e-9, "[5] jump heading");
        Check.False(p[5].JumpShift, "[5] jump shift False");
        Check.Near(p[5].JumpMs, 420, 1e-9, "[5] jump hold ms");
        Check.True(NavRouteParser.IsPlainWaypoint(p[2].Type), "a checkpoint is walked like a point");
        Check.False(NavRouteParser.IsPlainWaypoint(p[5].Type), "a jump is not a plain waypoint");

        // The writer reproduces this file exactly (every value here is in its written form).
        var fileLines = File.ReadAllLines(Fixture("circular-hunt.nav"));
        Check.Eq(string.Join("|", route.ToLines()), string.Join("|", fileLines), "written back line for line");
    }

    private static void LinearFile()
    {
        var route = NavRouteParser.Load(Fixture("linear-patrol.nav"));
        Check.Null(route.LoadWarning, "no warning");
        Check.Eq(route.RouteType, NavRouteType.Linear, "route type 2 is Linear");
        Check.Eq(route.Points.Count, 3, "point count");
        if (route.Points.Count == 3)
        {
            Check.Near(route.Points[0].EW, -12.25, 1e-9, "west coordinates are negative EW");
            Check.Near(route.Points[2].NS, 8.85, 1e-9, "last point NS");
        }
        var fileLines = File.ReadAllLines(Fixture("linear-patrol.nav"));
        Check.Eq(string.Join("|", route.ToLines()), string.Join("|", fileLines), "written back line for line");
    }

    private static void OnceFile()
    {
        var route = NavRouteParser.Load(Fixture("once-town-run.nav"));
        Check.Null(route.LoadWarning, "no warning");
        Check.Eq(route.RouteType, NavRouteType.Once, "route type 4 is Once");
        Check.Eq(route.Points.Count, 6, "point count");
        if (route.Points.Count != 6) return;
        var p = route.Points;

        Check.Eq(p[0].Type, NavPointType.Recall, "[0] recall");
        Check.Eq(p[0].SpellId, 1635, "[0] Lifestone Recall's spell id");
        Check.Eq(p[2].Type, NavPointType.OpenVendor, "[2] vendor");
        Check.Eq(p[2].VendorId, 2147484001u, "[2] vendor id above int.MaxValue");
        Check.Eq(p[2].TargetName, "Tinker Aldo", "[2] vendor name");
        Check.Eq(p[3].Type, NavPointType.PortalNPC, "[3] portal");
        Check.Eq(p[3].TargetName, "Town Network Portal", "[3] portal name");
        Check.Eq(p[3].ObjectClass, 14, "[3] object class");
        Check.True(p[3].IsTie, "[3] a numeric tie flag 1 reads as true");
        Check.Near(p[3].PortalExitNS, 42.541, 1e-9, "[3] the portal object's NS");
        Check.Near(p[3].PortalExitZ, 0.052, 1e-9, "[3] the portal object's Z");
        Check.Eq(p[4].Type, NavPointType.Npc, "[4] NPC");
        Check.Eq(p[4].TargetName, "Guard Captain Ilsa", "[4] NPC name");
        Check.Eq(p[4].ObjectClass, 37, "[4] NPC object class");
        Check.False(p[4].IsTie, "[4] tie False");
        Check.Eq(p[5].Type, NavPointType.Recall, "[5] recall");
        Check.Eq(p[5].SpellId, 48, "[5] Primary Portal Recall's spell id");

        // Written back, the numeric tie flag becomes "True"; everything else round-trips.
        var again = NavRouteParser.LoadFromLines(route.ToLines());
        Check.Eq(again.Points.Count, 6, "re-read point count");
        for (int i = 0; i < 6 && i < again.Points.Count; i++)
            Check.Eq(again.Points[i].ToString(), p[i].ToString(), $"[{i}] survives a write and read");
        Check.True(again.Points.Count == 6 && again.Points[3].IsTie, "tie survives as True");
    }

    private static void FollowFile()
    {
        var route = NavRouteParser.Load(Fixture("follow-leader.nav"));
        Check.Null(route.LoadWarning, "no warning");
        Check.Eq(route.RouteType, NavRouteType.Follow, "route type 3 is Follow");
        Check.Eq(route.FollowTargetName, "Vessa Thornwood", "leader name with a space");
        Check.Eq(route.FollowTargetId, 1342177301u, "leader id");
        Check.Eq(route.Points.Count, 0, "no points");

        var noId = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "3", "Vessa Thornwood" });
        Check.Eq(noId.FollowTargetName, "Vessa Thornwood", "no id line: name still read");
        Check.Eq(noId.FollowTargetId, 0u, "no id line: id 0");

        var badId = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "3", "Vessa Thornwood", "not-an-id" });
        Check.Eq(badId.FollowTargetId, 0u, "a bad id reads as 0");
        Check.Null(badId.LoadWarning, "a bad id is not a warning");

        // A Follow route with no leader name is written as an empty point list.
        var blank = new NavRouteParser { RouteType = NavRouteType.Follow };
        Check.Eq(string.Join("|", blank.ToLines()), "uTank2 NAV 1.2|3|0", "nameless Follow writes a zero point count");
    }

    private static void RouteTypeNumbers()
    {
        foreach (var type in new[] { NavRouteType.Circular, NavRouteType.Linear, NavRouteType.Once })
        {
            var route = new NavRouteParser { RouteType = type };
            route.Points.Add(new NavPoint { Type = NavPointType.Point, EW = 1.5, NS = -2.5, Z = 0.1 });
            var lines = route.ToLines();
            Check.Eq(lines[1], ((int)type).ToString(), $"{type} is written as {(int)type}");
            var back = NavRouteParser.LoadFromLines(lines);
            Check.Eq(back.RouteType, type, $"{type} reads back");
            Check.Eq(back.Points.Count, 1, $"{type} keeps its point");
        }
        var follow = new NavRouteParser { RouteType = NavRouteType.Follow, FollowTargetName = "Leader Lin", FollowTargetId = 7 };
        var fb = NavRouteParser.LoadFromLines(follow.ToLines());
        Check.Eq(fb.RouteType, NavRouteType.Follow, "Follow reads back");
        Check.Eq(fb.FollowTargetName, "Leader Lin", "Follow leader reads back");
    }

    private static void CrlfAndSave()
    {
        string dir = Scratch();
        // CRLF line endings and spaces around numbers, as a hand-edited or Windows-saved file has.
        string text = string.Join("\r\n", new[]
        {
            "uTank2 NAV 1.2", " 1 ", "2",
            "0", " 10.5", "-20.25 ", "0", "0",
            "4", "10.5", "-20.25", "0", "0", "/ra nav load next",
        }) + "\r\n";
        string path = Path.Combine(dir, "crlf.nav");
        File.WriteAllText(path, text);
        var route = NavRouteParser.Load(path);
        Check.Null(route.LoadWarning, "CRLF: no warning");
        Check.Eq(route.RouteType, NavRouteType.Circular, "CRLF: route type (with spaces)");
        Check.Eq(route.Points.Count, 2, "CRLF: point count");
        if (route.Points.Count == 2)
        {
            Check.Near(route.Points[0].EW, 10.5, 1e-9, "a leading space is allowed");
            Check.Near(route.Points[0].NS, -20.25, 1e-9, "a trailing space is allowed");
            Check.Eq(route.Points[1].ChatCommand, "/ra nav load next", "no carriage return left on the chat text");
        }

        string saved = Path.Combine(dir, "sub", "saved.nav");
        route.Save(saved);   // creates the folder
        Check.True(File.Exists(saved), "Save creates the folder and file");
        var back = NavRouteParser.Load(saved);
        Check.Eq(back.Points.Count, 2, "saved file reads back");
        Check.Eq(string.Join("|", back.ToLines()), string.Join("|", route.ToLines()), "Save and ToLines agree");
    }

    private static void ZRepairBoundary()
    {
        var route = NavRouteParser.LoadFromLines(new[]
        {
            "uTank2 NAV 1.2", "4", "4",
            "0", "1", "2", "5", "0",        // exactly 5 units: kept
            "0", "1", "2", "5.01", "0",     // just above: treated as metres
            "0", "1", "2", "-120", "0",     // negative metres too
            "6", "1", "2", "0", "0", "Portal", "14", "True", "1", "2", "300",   // the portal object's Z is not repaired
        });
        Check.Eq(route.Points.Count, 4, "point count");
        if (route.Points.Count != 4) return;
        Check.Near(route.Points[0].Z, 5.0, 1e-9, "5.0 is kept");
        Check.Near(route.Points[1].Z, 5.01 / 240.0, 1e-9, "5.01 is divided by 240");
        Check.Near(route.Points[2].Z, -0.5, 1e-9, "-120 m becomes -0.5");
        Check.Near(route.Points[3].PortalExitZ, 300, 1e-9, "the portal object's Z is read as is");
    }

    private static void Flags()
    {
        static NavPoint Portal(string tie) => NavRouteParser.LoadFromLines(new[]
            { "uTank2 NAV 1.2", "4", "1", "6", "1", "2", "0", "0", "P", "14", tie, "1", "2", "0" }).Points[0];
        static NavPoint Jump(string shift) => NavRouteParser.LoadFromLines(new[]
            { "uTank2 NAV 1.2", "4", "1", "9", "1", "2", "0", "0", "90", shift, "500" }).Points[0];

        Check.True(Portal("True").IsTie, "tie True");
        Check.True(Portal("true").IsTie, "tie true (any case)");
        Check.True(Portal("1").IsTie, "tie 1");
        Check.True(Portal(" 1 ").IsTie, "tie 1 with spaces");
        Check.False(Portal("False").IsTie, "tie False");
        Check.False(Portal("0").IsTie, "tie 0");
        Check.False(Portal("yes").IsTie, "tie yes: not recognised, false");

        Check.True(Jump("True").JumpShift, "shift True");
        Check.True(Jump(" TRUE ").JumpShift, "shift TRUE with spaces");
        Check.False(Jump("False").JumpShift, "shift False");
        // Pinned: unlike the tie flag, a numeric 1 is not read as true for the jump shift.
        Check.False(Jump("1").JumpShift, "shift 1 reads as false");
    }

    private static void EmptyFiles()
    {
        foreach (var (what, route) in new[]
        {
            ("empty file", NavRouteParser.Load(Fixture("empty.nav"))),
            ("missing file", NavRouteParser.Load(Fixture("no-such-route.nav"))),
            ("header only", NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2" })),
            ("header and type only", NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "1" })),
            ("other header", NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.1", "1", "0" })),
            ("a JSON file", NavRouteParser.LoadFromLines(new[] { "{", "  \"RouteType\": 1,", "}" })),
        })
        {
            Check.Eq(route.Points.Count, 0, $"{what}: no points");
            Check.Null(route.LoadWarning, $"{what}: no warning");
        }
        var lower = NavRouteParser.LoadFromLines(new[] { "utank2 nav 1.2", "1", "1", "0", "1", "2", "0", "0" });
        Check.Eq(lower.Points.Count, 1, "the header ignores case");
        var zero = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "1", "0" });
        Check.Eq(zero.Points.Count, 0, "zero points");
        Check.Null(zero.LoadWarning, "zero points is not a warning");
    }

    private static void MalformedHeader()
    {
        var warnings = new List<string>();
        var badType = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "circular", "1", "0", "1", "2", "0", "0" }, warnings.Add);
        Check.Eq(badType.Points.Count, 0, "non-number route type: no points");
        Check.True(badType.LoadWarning?.Contains("malformed header") == true, "non-number route type: warning");
        var badCount = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "1", "many", "0", "1", "2", "0", "0" }, warnings.Add);
        Check.Eq(badCount.Points.Count, 0, "non-number point count: no points");
        Check.True(badCount.LoadWarning?.Contains("malformed header") == true, "non-number point count: warning");
        Check.Eq(warnings.Count, 2, "the warn callback heard both");
    }

    private static void Truncated()
    {
        var warnings = new List<string>();
        var route = NavRouteParser.Load(Fixture("truncated.nav"), warnings.Add);
        Check.Eq(route.Points.Count, 1, "the whole first point is kept");
        Check.NotNull(route.LoadWarning, "warning set");
        Check.True(route.LoadWarning?.Contains("point 2") == true, "the warning names point 2");
        Check.Eq(warnings.Count, 1, "warned once");

        // Cut inside a trailer (a portal missing its last three lines).
        var cut = NavRouteParser.LoadFromLines(new[]
            { "uTank2 NAV 1.2", "4", "2", "0", "1", "2", "0", "0", "6", "1", "2", "0", "0", "Portal", "14", "True" });
        Check.Eq(cut.Points.Count, 1, "trailer cut: only the complete point is kept");
        Check.True(cut.LoadWarning?.Contains("point 2") == true, "trailer cut: warning names point 2");
    }

    private static void BadNumber()
    {
        var route = NavRouteParser.LoadFromLines(new[]
        {
            "uTank2 NAV 1.2", "1", "3",
            "0", "1", "2", "0", "0",
            "0", "1", "north", "0", "0",       // NS is not a number
            "0", "5", "6", "0", "0",
        });
        Check.Eq(route.Points.Count, 1, "stops at the bad point: the ones after it are not guessed");
        Check.True(route.LoadWarning?.Contains("parse error at point 2") == true, "warning names point 2");

        var badRecall = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "4", "1", "2", "1", "2", "0", "0", "Lifestone Recall" });
        Check.Eq(badRecall.Points.Count, 0, "a recall with a spell name instead of an id is refused");
        Check.NotNull(badRecall.LoadWarning, "and warned");

        var badType = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "4", "1", "point", "1", "2", "0", "0" });
        Check.Eq(badType.Points.Count, 0, "a non-number waypoint type is refused");
        Check.NotNull(badType.LoadWarning, "and warned");
    }

    private static void UnreadableFile()
    {
        string dir = Scratch();
        string path = Path.Combine(dir, "locked.nav");
        File.WriteAllLines(path, new[] { "uTank2 NAV 1.2", "1", "0" });
        var warnings = new List<string>();
        NavRouteParser route;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            route = NavRouteParser.Load(path, warnings.Add);
        Check.Eq(route.Points.Count, 0, "no points");
        Check.True(route.LoadWarning?.Contains("failed to read") == true, "warning says it could not be read");
        Check.Eq(warnings.Count, 1, "warned once");
    }

    private static void PinnedOddities()
    {
        // The count says 5 but the file ends cleanly after 2 points: both load, no warning.
        var shortCount = NavRouteParser.LoadFromLines(new[]
            { "uTank2 NAV 1.2", "1", "5", "0", "1", "2", "0", "0", "0", "3", "4", "0", "0" });
        Check.Eq(shortCount.Points.Count, 2, "short file: the points that are there load");
        Check.Null(shortCount.LoadWarning, "short file: no warning (pinned)");

        // A negative count reads as an empty route, no warning.
        var negative = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "1", "-1", "0", "1", "2", "0", "0" });
        Check.Eq(negative.Points.Count, 0, "negative count: empty");
        Check.Null(negative.LoadWarning, "negative count: no warning (pinned)");

        // Route types VTank never writes are kept as numbers, with no warning. The engine has
        // no case for them: it walks to the first point and stays there.
        var type5 = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "5", "1", "0", "1", "2", "0", "0" });
        Check.Eq((int)type5.RouteType, 5, "route type 5 kept as 5");
        Check.Null(type5.LoadWarning, "route type 5: no warning (pinned)");

        // A comma decimal is read with the comma as a thousands separator: 33,6 becomes 336.
        var comma = NavRouteParser.LoadFromLines(new[] { "uTank2 NAV 1.2", "1", "1", "0", "33,6", "42,5", "0", "0" });
        Check.Eq(comma.Points.Count, 1, "comma decimals: the point loads");
        if (comma.Points.Count == 1) Check.Near(comma.Points[0].EW, 336, 1e-9, "33,6 reads as 336 (pinned)");
        Check.Null(comma.LoadWarning, "comma decimals: no warning (pinned)");
    }
}
