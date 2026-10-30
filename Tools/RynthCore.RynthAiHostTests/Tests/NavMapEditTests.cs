using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;
using T = RynthCore.Plugin.RynthAi.LegacyUi.NavPointType;

namespace RynthCore.RynthAiHostTests.Tests;

// The engine Dungeon Map's route edits (2026-10-12): addPointAt / movePointTo carry the map's
// coordinates (worked out from the floor under the cursor), setActive picks the next waypoint.
// RynthPluginGetNavJson reports MapEdit so the engine only offers editing to a RynthAi that has
// these commands; older builds ignore them.
internal static class NavMapEditTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("nav map edit: addPointAt appends a Point at the map's coordinates", AddAppends);
        r.Add("nav map edit: addPointAt after a selected step inserts there and keeps the next waypoint", AddInsertsAfter);
        r.Add("nav map edit: movePointTo moves a step and keeps its type and other fields", MoveKeepsFields);
        r.Add("nav map edit: bad index or coordinates change nothing", Refusals);
        r.Add("nav map edit: setActive sets the next waypoint", SetActive);
        r.Add("nav map edit: the engine's command JSON reads; the nav JSON says MapEdit", JsonShapes);
        r.Add("nav map edit: an edit through the dashboard saves the route file", SavesRouteFile);
    }

    private static LegacyUiSettings Settings(params NavPoint[] pts)
    {
        var s = new LegacyUiSettings();
        s.CurrentRoute = new NavRouteParser { RouteType = NavRouteType.Circular };
        s.CurrentRoute.Points.AddRange(pts);
        return s;
    }

    private static NavPoint P(double ns, double ew, T type = T.Point) => new() { Type = type, NS = ns, EW = ew, Z = -0.1 };

    private static NavCommand Add(double ns, double ew, double z, int addMode = 0, int insertAt = -1) =>
        new() { Cmd = "addPointAt", NS = ns, EW = ew, Z = z, AddMode = addMode, InsertAt = insertAt };

    private static void AddAppends()
    {
        var s = Settings(P(1, 1), P(2, 2));
        var res = NavMapEdit.Apply(s, Add(-12.5, 40.25, -0.1), out string status);
        Check.Eq(res, NavMapEdit.Result.RouteChanged, "route changed");
        Check.Eq(s.CurrentRoute.Points.Count, 3, "one point added");
        NavPoint p = s.CurrentRoute.Points[2];
        Check.Eq(p.Type, T.Point, "a Point");
        Check.Near(p.NS, -12.5, 1e-9, "NS");
        Check.Near(p.EW, 40.25, 1e-9, "EW");
        Check.Near(p.Z, -0.1, 1e-9, "Z (nav units)");
        Check.True(status.Contains("2"), "status names the new step: " + status);

        // Into an empty route, whatever InsertAt says.
        var e = Settings();
        NavMapEdit.Apply(e, Add(1, 2, 0, addMode: 2, insertAt: 5), out _);
        Check.Eq(e.CurrentRoute.Points.Count, 1, "first point of an empty route");
        Check.Eq(e.ActiveNavIndex, 0, "active stays 0");
    }

    private static void AddInsertsAfter()
    {
        var s = Settings(P(0, 0), P(1, 1), P(2, 2), P(3, 3));
        s.ActiveNavIndex = 2;
        NavMapEdit.Apply(s, Add(9, 9, 0, addMode: 2, insertAt: 0), out _);   // after step 0 -> index 1
        Check.Near(s.CurrentRoute.Points[1].NS, 9, 1e-9, "inserted after step 0");
        Check.Eq(s.ActiveNavIndex, 3, "active follows its step (was 2, shifted down)");
        Check.Near(s.CurrentRoute.Points[s.ActiveNavIndex].NS, 2, 1e-9, "still heading for the same point");

        NavMapEdit.Apply(s, Add(8, 8, 0, addMode: 2, insertAt: 3), out _);   // after the active step -> index 4
        Check.Near(s.CurrentRoute.Points[4].NS, 8, 1e-9, "inserted after the active step");
        Check.Eq(s.ActiveNavIndex, 3, "active unchanged by a point after it");

        NavMapEdit.Apply(s, Add(7, 7, 0, addMode: 2, insertAt: 99), out _);  // past the end -> appended
        Check.Near(s.CurrentRoute.Points[^1].NS, 7, 1e-9, "InsertAt past the end appends");

        Check.Eq(NavMapEdit.InsertIndex(5, 1, 2), 2, "AddMode 1 = before");
        Check.Eq(NavMapEdit.InsertIndex(5, 0, 2), 5, "AddMode 0 = end");
    }

    private static void MoveKeepsFields()
    {
        var recall = new NavPoint { Type = T.Recall, NS = 5, EW = 5, Z = 0.2, SpellId = 1635 };
        var chat = new NavPoint { Type = T.Chat, NS = 6, EW = 6, Z = 0.2, ChatCommand = "/say hi" };
        var s = Settings(P(1, 1), recall, chat);
        var res = NavMapEdit.Apply(s, new NavCommand { Cmd = "movePointTo", Index = 1, NS = -3.25, EW = 7.5, Z = -0.05 }, out _);
        Check.Eq(res, NavMapEdit.Result.RouteChanged, "route changed");
        Check.True(ReferenceEquals(s.CurrentRoute.Points[1], recall), "same step object (moved in place)");
        Check.Eq(recall.Type, T.Recall, "type kept");
        Check.Eq(recall.SpellId, 1635, "spell kept");
        Check.Near(recall.NS, -3.25, 1e-9, "NS moved");
        Check.Near(recall.EW, 7.5, 1e-9, "EW moved");
        Check.Near(recall.Z, -0.05, 1e-9, "Z from the floor under the drop");

        NavMapEdit.Apply(s, new NavCommand { Cmd = "movePointTo", Index = 2, NS = 1, EW = 2, Z = 0 }, out _);
        Check.Eq(chat.ChatCommand, "/say hi", "chat text kept");
        Check.Eq(s.CurrentRoute.Points.Count, 3, "count unchanged");
    }

    private static void Refusals()
    {
        var s = Settings(P(1, 1), P(2, 2));
        Check.Eq(NavMapEdit.Apply(s, new NavCommand { Cmd = "movePointTo", Index = 2, NS = 1, EW = 1 }, out _),
            NavMapEdit.Result.None, "index past the end");
        Check.Eq(NavMapEdit.Apply(s, new NavCommand { Cmd = "movePointTo", Index = -1, NS = 1, EW = 1 }, out _),
            NavMapEdit.Result.None, "negative index");
        Check.Eq(NavMapEdit.Apply(s, Add(double.NaN, 1, 0), out _), NavMapEdit.Result.None, "NaN NS");
        Check.Eq(NavMapEdit.Apply(s, Add(1, 500, 0), out _), NavMapEdit.Result.None, "EW off the world");
        Check.Eq(NavMapEdit.Apply(s, Add(1, 1, double.PositiveInfinity), out _), NavMapEdit.Result.None, "infinite Z");
        Check.Eq(NavMapEdit.Apply(s, new NavCommand { Cmd = "movePointTo", Index = 0, NS = 1, EW = 1, Z = 1e9 }, out _),
            NavMapEdit.Result.None, "absurd Z on a move");
        Check.Eq(NavMapEdit.Apply(s, new NavCommand { Cmd = "somethingElse" }, out _), NavMapEdit.Result.None, "unknown command");
        Check.Eq(s.CurrentRoute.Points.Count, 2, "route untouched");
        Check.Near(s.CurrentRoute.Points[0].NS, 1, 1e-9, "point untouched");
        Check.True(NavMapEdit.Handles("addPointAt") && NavMapEdit.Handles("movePointTo") && NavMapEdit.Handles("setActive"),
            "the three map commands are routed here");
        Check.False(NavMapEdit.Handles("addWaypoint"), "addWaypoint is not");
    }

    private static void SetActive()
    {
        var s = Settings(P(1, 1), P(2, 2), P(3, 3));
        Check.Eq(NavMapEdit.Apply(s, new NavCommand { Cmd = "setActive", Index = 2 }, out _), NavMapEdit.Result.ActiveChanged, "changed");
        Check.Eq(s.ActiveNavIndex, 2, "active = 2");
        Check.Eq(NavMapEdit.Apply(s, new NavCommand { Cmd = "setActive", Index = 3 }, out _), NavMapEdit.Result.None, "past the end refused");
        Check.Eq(s.ActiveNavIndex, 2, "active unchanged");
    }

    private static void JsonShapes()
    {
        // What the engine's NavCmd serializes to (source-generated, PascalCase, every property).
        const string json = "{\"Cmd\":\"addPointAt\",\"SpellId\":0,\"Index\":0,\"RouteType\":0,\"AddMode\":2,\"InsertAt\":3,"
            + "\"NavName\":\"\",\"Text\":\"\",\"On\":false,\"Indices\":null,\"Delta\":0,\"Seconds\":0,\"Yards\":0,\"Reverse\":false,"
            + "\"NS\":-12.5,\"EW\":40.25,\"Z\":-0.1}";
        NavCommand? cmd = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.NavCommand);
        Check.NotNull(cmd, "parsed");
        Check.Eq(cmd!.Cmd, "addPointAt", "Cmd");
        Check.Near(cmd.NS, -12.5, 1e-9, "NS");
        Check.Near(cmd.EW, 40.25, 1e-9, "EW");
        Check.Near(cmd.Z, -0.1, 1e-9, "Z");
        Check.Eq(cmd.AddMode, 2, "AddMode");
        Check.Eq(cmd.InsertAt, 3, "InsertAt");

        // An older engine's command (no NS/EW/Z) still reads, coordinates 0.
        NavCommand? old = JsonSerializer.Deserialize("{\"Cmd\":\"deletePoint\",\"Index\":4}", RynthAiJsonContext.Default.NavCommand);
        Check.Eq(old!.Index, 4, "old command reads");
        Check.Near(old.NS, 0, 0, "absent NS = 0");

        // RynthPluginGetNavJson reports the map edit version with the route.
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, Settings(P(1, 2), P(3, 4)));
        typeof(LegacyDashboardRenderer).GetField("_navFiles", Any)!.SetValue(dash, new System.Collections.Generic.List<string>());
        // Field initializers don't run on an uninitialized object; the route editor's status line.
        typeof(LegacyDashboardRenderer).GetField("_navEditStatus", Any)!.SetValue(dash, string.Empty);
        using JsonDocument doc = JsonDocument.Parse(dash.BuildNavJson());
        Check.True(doc.RootElement.TryGetProperty("MapEdit", out JsonElement me), "MapEdit present");
        Check.Eq(me.GetInt32(), NavMapEdit.Version, "MapEdit = Version");
        Check.Eq(doc.RootElement.GetProperty("Points").GetArrayLength(), 2, "points exported");
    }

    private static void SavesRouteFile()
    {
        string dir = Path.Combine(Program.TempRoot, "NavMapEdit");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "maproute.nav");
        var s = Settings(P(1, 1), P(2, 2));
        s.CurrentRoute.Save(path);
        s.CurrentNavPath = path;

        var nav = (LegacyNavigationUi)RuntimeHelpers.GetUninitializedObject(typeof(LegacyNavigationUi));
        typeof(LegacyNavigationUi).GetField("_settings", Any)!.SetValue(nav, s);
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_navigationUi", Any)!.SetValue(dash, nav);

        dash.HandleMapNavCommand(Add(-1.5, 2.5, -0.1, addMode: 2, insertAt: 0));
        NavRouteParser reread = NavRouteParser.Load(path);
        Check.Eq(reread.Points.Count, 3, "file has the added point");
        Check.Near(reread.Points[1].NS, -1.5, 1e-6, "at step 1 (after the selected step 0)");

        dash.HandleMapNavCommand(new NavCommand { Cmd = "movePointTo", Index = 2, NS = 4.25, EW = -3.75, Z = -0.2 });
        reread = NavRouteParser.Load(path);
        Check.Near(reread.Points[2].NS, 4.25, 1e-6, "file has the moved NS");
        Check.Near(reread.Points[2].EW, -3.75, 1e-6, "file has the moved EW");
        Check.Near(reread.Points[2].Z, -0.2, 1e-6, "file has the moved Z");
    }
}
