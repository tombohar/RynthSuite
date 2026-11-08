using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// The route maker's parts that need no navmesh (RynthNavPlugin.RouteMaker.cs): what a /rnav command and
// the "RynthNav.Routes" JSON are read as and what they refuse (RouteRequest), the job table and its
// states (RouteJobTable), a loop's order and the joined legs as a nav file RynthAi reads back
// (RouteShape), what a saved nav is (RouteInfo, on a made-up file and the real NavProfiles when they're
// here, read only), a plan's figures (TripSummary) and the building outlines file (BuildingOutlines).
// The navmesh side (choosing loop points, joining legs) is in RynthNav.GraphTests.
internal static partial class Program
{
    private static void RunRouteMaker()
    {
        Run(nameof(RouteNameRules), RouteNameRules);
        Run(nameof(RouteJsonRequests), RouteJsonRequests);
        Run(nameof(RouteJsonRefusals), RouteJsonRefusals);
        Run(nameof(RouteChatCommands), RouteChatCommands);
        Run(nameof(RouteJobStates), RouteJobStates);
        Run(nameof(LoopOrderNoCrossings), LoopOrderNoCrossings);
        Run(nameof(LoopNavWrittenAndReadBack), LoopNavWrittenAndReadBack);
        Run(nameof(RouteInfoOnASampleFile), RouteInfoOnASampleFile);
        Run(nameof(RouteInfoOnRealNavProfiles), RouteInfoOnRealNavProfiles);
        Run(nameof(PlanSummaryFigures), PlanSummaryFigures);
        Run(nameof(BuildingOutlinesFile), BuildingOutlinesFile);
    }

    private static RouteRequest? Json(string json) => RouteRequest.FromJson(json, out _);

    private static string JsonError(string json)
    {
        RouteRequest? r = RouteRequest.FromJson(json, out string error);
        return r == null ? error : "";
    }

    // ── Names ────────────────────────────────────────────────────────────

    private static void RouteNameRules()
    {
        // saveroute's rules, the same for every make op: chat and JSON.
        Check(RouteRequest.FromCommand("makeloop", "Hunt 1 radius 50", out _)?.Name == "Hunt 1", "a plain name");
        Check(RouteRequest.FromCommand("makeloop", "Hunt 1.nav radius 50", out _)?.Name == "Hunt 1", ".nav is dropped");
        Check(RouteRequest.FromCommand("makeloop", "a\\b radius 50", out string e1) == null && e1.Contains("file name"), "a path isn't a name: " + e1);
        Check(RouteRequest.FromCommand("makeloop", "a:b radius 50", out _) == null, "nor characters a file can't have");
        Check(RouteRequest.FromCommand("makeloop", ".. radius 50", out _) == null, "nor ..");
        Check(RouteRequest.FromCommand("makeloop", new string('x', 101) + " radius 50", out _) == null, "nor over 100 characters");
        Check(RouteRequest.FromCommand("routeinfo", "", out string e2) == null && e2.Length > 0, "routeinfo needs a name");
        Check(Json("{\"op\":\"info\",\"name\":\"../x\"}") == null, "JSON: a path is refused too");
        Check(Json("{\"op\":\"makeloop\",\"name\":\"\",\"radius\":50}") == null, "JSON: an empty name is refused");
        Check(Json("{\"op\":\"makeloop\",\"name\":\"Loop A\",\"radius\":50}")?.Overwrite == false, "never overwrite unless asked");
        Check(Json("{\"op\":\"makeloop\",\"name\":\"Loop A\",\"radius\":50,\"overwrite\":true}")?.Overwrite == true, "overwrite: true asks");
        Check(RouteRequest.FromCommand("makeloop", "Loop A radius 50 overwrite", out _)?.Overwrite == true, "chat: overwrite at the end");
        Check(RouteRequest.FromCommand("makeloop", "Loop A radius 50", out _)?.Overwrite == false, "chat: not without it");

        // An existing file stays unless overwrite (the same NavRouteExport.Save the jobs use).
        string dir = Path.Combine(Path.GetTempPath(), "rnav-maker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = new NavRouteParser { RouteType = NavRouteType.Circular };
            a.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1, EW = 1 });
            a.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1.1, EW = 1 });
            Check(NavRouteExport.Save(dir, "Loop A", a, false, out string file, out _, out _), "a new loop is written");
            var b = new NavRouteParser { RouteType = NavRouteType.Once };
            b.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 5, EW = 5 });
            Check(!NavRouteExport.Save(dir, "Loop A", b, false, out _, out bool exists, out _) && exists, "the same name: refused");
            Check(NavRouteParser.Load(file).RouteType == NavRouteType.Circular, "and the file is the first one still");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── JSON in ──────────────────────────────────────────────────────────

    private static void RouteJsonRequests()
    {
        RouteRequest? p = Json("{\"op\":\"plan\",\"to\":\"Holtburg\",\"portals\":true}");
        Check(p != null && p.Op == RouteOp.Plan && p.To == "Holtburg" && p.Portals == true && !p.Show && !p.FromChat, "plan to a place, portals on");
        RouteRequest? pc = Json("{\"op\":\"plan\",\"to\":{\"ns\":42.1234,\"ew\":-33.5}}");
        Check(pc != null && pc.Portals == null, "plan without portals: the switch");
        Check(pc != null && NavCoords.TryParseLoc(pc.To, out double tns, out double tew) && Math.Abs(tns - 42.1234) < 1e-9 && Math.Abs(tew + 33.5) < 1e-9,
            "coordinates kept exactly as text /rnav go reads: " + pc?.To);

        RouteRequest? mr = Json("{\"op\":\"makeroute\",\"name\":\"To Holtburg\",\"to\":\"Holtburg\",\"overwrite\":true,\"show\":true}");
        Check(mr != null && mr.Op == RouteOp.MakeRoute && mr.Name == "To Holtburg" && mr.Overwrite && mr.Show, "makeroute");

        RouteRequest? ml = Json("{\"op\":\"makeloop\",\"name\":\"Hunt\",\"radius\":80,\"points\":8}");
        Check(ml != null && ml.Op == RouteOp.MakeLoop && ml.Here && ml.Radius == 80 && ml.Points == 8, "makeloop here (no at)");
        RouteRequest? mla = Json("{\"op\":\"makeloop\",\"name\":\"Hunt\",\"at\":{\"ns\":10.5,\"ew\":-20.25},\"radius\":20}");
        Check(mla != null && !mla.Here && mla.AtNs == 10.5 && mla.AtEw == -20.25 && mla.Points == 0, "makeloop at coordinates, default points");
        RouteRequest? mls = Json("{\"op\":\"makeloop\",\"name\":\"Hunt\",\"at\":\"10.5N, 20.25W\",\"radius\":250}");
        Check(mls != null && !mls.Here && mls.AtNs == 10.5 && mls.AtEw == -20.25, "makeloop at a coordinate string, radius at the cap");
        Check(Json("{\"op\":\"makeloop\",\"name\":\"Hunt\",\"at\":\"here\",\"radius\":20}")?.Here == true, "at here");

        RouteRequest? mp = Json("{\"op\":\"makepath\",\"name\":\"Ridge\",\"type\":\"loop\",\"points\":[{\"ns\":1,\"ew\":2},\"1.1N, 2.1E\",{\"ns\":1.2,\"ew\":2}]}");
        Check(mp != null && mp.PathType == NavRouteType.Circular && mp.Path.Count == 3 && mp.Path[1] == (1.1, 2.1), "makepath loop with mixed points");
        Check(Json("{\"op\":\"makepath\",\"name\":\"R\",\"type\":\"line\",\"points\":[\"1N 1E\",\"1.1N 1E\"]}")?.PathType == NavRouteType.Linear, "type line = linear");
        Check(Json("{\"op\":\"makepath\",\"name\":\"R\",\"points\":[\"1N 1E\",\"1.1N 1E\"]}")?.PathType == NavRouteType.Once, "no type = once");

        RouteRequest? at = Json("{\"op\":\"atlas\",\"search\":\" holt \",\"max\":5}");
        Check(at != null && at.Search == "holt" && at.Max == 5, "atlas");
        Check(Json("{\"op\":\"atlas\",\"search\":\"holt\"}")?.Max == RouteLimits.DefaultAtlas, "atlas: max defaults");
        Check(Json("{\"op\":\"info\",\"name\":\"Hunt.nav\"}")?.Name == "Hunt", "info");
        Check(Json("{\"op\":\"go\",\"to\":\"Holtburg\"}")?.Op == RouteOp.Go, "go");
        Check(Json("{\"op\":\"stop\"}")?.Op == RouteOp.Stop && Json("{\"op\":\"WHERE\"}")?.Op == RouteOp.Where, "stop, where (any case)");
        Check(Json("{\"op\":\"where\",\"something\":\"new\"}") != null, "unknown fields are ignored");
    }

    private static void RouteJsonRefusals()
    {
        string[] bad =
        {
            "", "   ", "not json", "[1,2]", "{}", "{\"op\":3}", "{\"op\":\"teleport\"}",
            "{\"op\":\"plan\"}", "{\"op\":\"plan\",\"to\":\"\"}", "{\"op\":\"plan\",\"to\":5}", "{\"op\":\"plan\",\"to\":{\"ns\":1}}",
            "{\"op\":\"plan\",\"to\":{\"ns\":200,\"ew\":1}}", "{\"op\":\"plan\",\"to\":\"150N, 2E\"}", "{\"op\":\"plan\",\"to\":\"X\",\"portals\":\"yes\"}",
            "{\"op\":\"makeroute\",\"to\":\"X\"}", "{\"op\":\"makeroute\",\"name\":\"A\"}",
            "{\"op\":\"makeloop\",\"name\":\"A\"}", "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":\"80\"}",
            "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":19}", "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":251}",
            "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":80,\"points\":2}", "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":80,\"points\":17}",
            "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":80,\"points\":4.5}", "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":80,\"at\":\"there\"}",
            "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":80,\"at\":{\"ns\":1,\"ew\":-300}}", "{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":80,\"overwrite\":1}",
            "{\"op\":\"makepath\",\"name\":\"A\",\"points\":[\"1N 1E\"]}", "{\"op\":\"makepath\",\"name\":\"A\",\"points\":\"1N 1E; 2N 2E\"}",
            "{\"op\":\"makepath\",\"name\":\"A\",\"type\":\"zigzag\",\"points\":[\"1N 1E\",\"2N 2E\"]}",
            "{\"op\":\"makepath\",\"name\":\"A\",\"points\":[\"1N 1E\",\"somewhere\"]}",
            "{\"op\":\"makepath\",\"name\":\"A\",\"points\":[\"1N 1E\",\"90N 90E\"]}",
            "{\"op\":\"atlas\"}", "{\"op\":\"atlas\",\"search\":\" \"}", "{\"op\":\"atlas\",\"search\":\"a\",\"max\":0}", "{\"op\":\"atlas\",\"search\":\"a\",\"max\":51}",
            "{\"op\":\"info\"}", "{\"op\":\"go\"}",
        };
        foreach (string b in bad)
        {
            string why = JsonError(b);
            Check(why.Length > 0, $"refused: {b}");
        }
        var many = new System.Text.StringBuilder("{\"op\":\"makepath\",\"name\":\"A\",\"points\":[");
        for (int i = 0; i < RouteLimits.MaxPathPoints + 1; i++) many.Append(i > 0 ? "," : "").Append("{\"ns\":1,\"ew\":").Append(1 + i * 0.01).Append('}');
        Check(JsonError(many.Append("]}").ToString()).Contains("too many"), "33 points: too many");
        Check(JsonError("{\"op\":\"where\",\"pad\":\"" + new string('x', RouteLimits.MaxJson) + "\"}").Contains("characters"), "a request over 16 KB");
        Check(JsonError("{\"op\":\"teleport\"}").Contains("makeloop"), "an unknown op lists the ones there are");
        Check(JsonError("{\"op\":\"makeloop\",\"name\":\"A\",\"radius\":300}").Contains("20-250"), "out of range says the range");
        Check(RouteRequest.UsageFor("{\"op\":\"makeloop\"}").Contains("\"radius\""), "the usage for a known op is its JSON form");
        Check(RouteRequest.UsageFor("junk").Contains("makepath"), "...and the op list otherwise");
        string err = RouteJson.Error("why \"quoted\"", "use \\ this");
        using JsonDocument d = JsonDocument.Parse(err);
        Check(d.RootElement.GetProperty("error").GetString() == "why \"quoted\"" && d.RootElement.GetProperty("usage").GetString() == "use \\ this", "the refusal is valid JSON, escaped");
    }

    // ── Chat in ──────────────────────────────────────────────────────────

    private static void RouteChatCommands()
    {
        RouteRequest? a = RouteRequest.FromCommand("makeroute", "To Town to Holtburg", out string e0);
        Check(a != null && a.Op == RouteOp.MakeRoute && a.Name == "To Town" && a.To == "Holtburg" && a.FromChat && a.Show, "makeroute <name> to <place>: " + e0);
        RouteRequest? b = RouteRequest.FromCommand("makeroute", "Cragstone run to Portal to Cragstone overwrite", out _);
        Check(b != null && b.Name == "Cragstone run" && b.To == "Portal to Cragstone" && b.Overwrite, "the first \" to \" ends the name; overwrite at the end");
        RouteRequest? c = RouteRequest.FromCommand("makeroute", "\"Go to the hill\" to 42.1N, 33.6E", out _);
        Check(c != null && c.Name == "Go to the hill" && c.To == "42.1N, 33.6E", "a quoted name may hold \" to \"");
        Check(RouteRequest.FromCommand("makeroute", "Holtburg", out string e1) == null && e1.Contains(" to "), "makeroute without to: refused");
        Check(RouteRequest.FromCommand("makeroute", "A to 150N, 2E", out _) == null, "makeroute to coordinates off the map: refused");

        RouteRequest? l1 = RouteRequest.FromCommand("makeloop", "Hunt at the lake radius 60", out _);
        Check(l1 != null && l1.Name == "Hunt at the lake" && l1.Here && l1.Radius == 60 && l1.Points == 0, "a name with 'at' in it, round here");
        RouteRequest? l2 = RouteRequest.FromCommand("makeloop", "Hunt at 42.1N, 33.6E radius 80 points 6 overwrite", out string e2);
        Check(l2 != null && l2.Name == "Hunt" && !l2.Here && l2.AtNs == 42.1 && l2.AtEw == 33.6 && l2.Radius == 80 && l2.Points == 6 && l2.Overwrite, "at coords, points, overwrite: " + e2);
        RouteRequest? l3 = RouteRequest.FromCommand("makeloop", "Hunt radius 40 at 1.5S 2.5W", out _);
        Check(l3 != null && !l3.Here && l3.AtNs == -1.5 && l3.AtEw == -2.5, "the options in any order");
        RouteRequest? l4 = RouteRequest.FromCommand("makeloop", "Hunt here radius 40", out _);
        Check(l4 != null && l4.Here && l4.Name == "Hunt", "here");
        Check(RouteRequest.FromCommand("makeloop", "Hunt", out string e3) == null && e3.Contains("radius"), "no radius: refused (" + e3 + ")");
        Check(RouteRequest.FromCommand("makeloop", "Hunt radius 500", out string e4) == null && e4.Contains("20-250"), "radius over the cap: refused (" + e4 + ")");
        Check(RouteRequest.FromCommand("makeloop", "Hunt radius 50 points 40", out string e5) == null && e5.Contains("3-16"), "too many points: refused");
        Check(RouteRequest.FromCommand("makeloop", "Hunt radius fifty", out _) == null, "radius not a number: refused");
        Check(RouteRequest.FromCommand("makeloop", "Hunt at 999N 2E radius 50", out string e6) == null && e6.Contains("off the map"), "at coordinates off the map: refused (" + e6 + ")");
        RouteRequest? l5 = RouteRequest.FromCommand("makeloop", "Hunt at nowhere radius 50", out _);
        Check(l5 != null && l5.Name == "Hunt at nowhere" && l5.Here, "'at' followed by words, not coordinates, is part of the name");
        Check(RouteRequest.FromCommand("makeloop", "radius 50", out _) == null, "no name: refused");
        RouteRequest? lq = RouteRequest.FromCommand("makeloop", "\"Hunt at 42N 33E\" radius 60 overwrite", out string eq);
        Check(lq != null && lq.Name == "Hunt at 42N 33E" && lq.Here && lq.Radius == 60 && lq.Overwrite, "a quoted name holds anything: " + eq);
        Check(RouteRequest.FromCommand("makeloop", "\"Hunt radius 60", out string eq2) == null && eq2.Contains("quote"), "an unclosed quote: refused");

        RouteRequest? p1 = RouteRequest.FromCommand("makepath", "Ridge walk loop 42.1N 33.6E; 42.3N, 33.9E; 42.0N 34.0E overwrite", out string e7);
        Check(p1 != null && p1.Name == "Ridge walk" && p1.PathType == NavRouteType.Circular && p1.Path.Count == 3 && p1.Overwrite
            && p1.Path[0] == (42.1, 33.6) && p1.Path[2] == (42.0, 34.0), "makepath name, type, points, overwrite: " + e7);
        RouteRequest? p2 = RouteRequest.FromCommand("makepath", "Route 1 42 33; 42.1 33.1", out _);
        Check(p2 != null && p2.Name == "Route 1" && p2.PathType == NavRouteType.Once && p2.Path.Count == 2, "a number in the name; no type = once");
        RouteRequest? p3 = RouteRequest.FromCommand("makepath", "Back and forth line 1N 1E; 1.1N 1E;", out _);
        Check(p3 != null && p3.PathType == NavRouteType.Linear && p3.Path.Count == 2, "line; a trailing ; is fine");
        Check(RouteRequest.FromCommand("makepath", "Solo 1N 1E", out string e8) == null && e8.Contains("at least"), "one point: refused");
        Check(RouteRequest.FromCommand("makepath", "Bad 1N 1E; up the hill", out string e9) == null && e9.Contains("point 2"), "a point that isn't coordinates: refused, naming it");
        Check(RouteRequest.FromCommand("makepath", "Far 0N 0E; 80N 80E", out string e10) == null && e10.Contains("leg 1"), "a leg too long: refused, naming it");
        Check(RouteRequest.FromCommand("makepath", "Nothing", out _) == null, "no points: refused");
        RouteRequest? pq = RouteRequest.FromCommand("makepath", "\"Ridge, east\" loop 1N 1E; 1.1N 1E", out string e11);
        Check(pq != null && pq.Name == "Ridge, east" && pq.PathType == NavRouteType.Circular && pq.Path.Count == 2, "makepath with a quoted name: " + e11);

        Check(RouteRequest.FromCommand("routeinfo", "Hunt", out _)?.Op == RouteOp.Info, "routeinfo");
        Check(RouteRequest.FromCommand("routeinfo", "\"Hunt\" extra", out _) == null, "routeinfo takes only the name");
        Check(RouteRequest.Usage(RouteOp.MakeLoop, chat: true).StartsWith("/rnav makeloop"), "the chat usage line");
    }

    // ── Jobs ─────────────────────────────────────────────────────────────

    private static void RouteJobStates()
    {
        var t = new RouteJobTable(5000);
        RouteJob? j1 = t.Add(Json("{\"op\":\"where\"}")!, 100, out string err);
        Check(j1 != null && j1.Id == 5000 && j1.State == RouteJobState.Queued && err == "", "a new job is queued, with the first id");
        RouteJob? j2 = t.Add(Json("{\"op\":\"stop\"}")!, 100, out _);
        Check(j2 != null && j2.Id == 5001, "ids go up");
        Check(t.Queued().Count == 2 && t.Queued()[0] == j1, "the queue, oldest first");

        using (JsonDocument a = JsonDocument.Parse(t.Accepted(j1!)))
            Check(a.RootElement.GetProperty("id").GetInt32() == 5000 && a.RootElement.GetProperty("state").GetString() == "queued"
                && a.RootElement.GetProperty("op").GetString() == "where" && a.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
                "Request's answer: id, op, queued, no result yet");

        t.Start(j1!, "working on it", 110);
        using (JsonDocument w = JsonDocument.Parse(t.PollJson(5000, out bool known)))
            Check(known && w.RootElement.GetProperty("state").GetString() == "working" && w.RootElement.GetProperty("detail").GetString() == "working on it", "working");
        t.Update(j1!, "nearly", "{\"partial\":1}");
        t.Finish(j1!, true, "done it", "{\"answer\":42}", 120);
        using (JsonDocument d = JsonDocument.Parse(t.PollJson(5000, out _)))
            Check(d.RootElement.GetProperty("state").GetString() == "done" && d.RootElement.GetProperty("result").GetProperty("answer").GetInt32() == 42, "done, with the result");
        t.Finish(j1!, false, "changed my mind", null, 130);
        Check(j1!.State == RouteJobState.Done && j1.Detail == "done it", "a finished job stays as it ended");
        t.Start(j1, "again", 140);
        Check(j1.State == RouteJobState.Done, "...and can't be started again");

        t.Finish(j2!, false, "no player position yet", RouteJson.Error("no player position yet", ""), 150);
        using (JsonDocument f = JsonDocument.Parse(t.PollJson(5001, out _)))
            Check(f.RootElement.GetProperty("state").GetString() == "failed" && f.RootElement.GetProperty("result").GetProperty("error").GetString() == "no player position yet", "failed, with why");
        using (JsonDocument u = JsonDocument.Parse(t.PollJson(4999, out bool k2)))
            Check(!k2 && u.RootElement.GetProperty("state").GetString() == "unknown" && u.RootElement.GetProperty("error").GetString()!.Length > 0, "an id never given: unknown");

        // The last 32: finished jobs make room, oldest first; 32 unfinished refuse a 33rd.
        var full = new RouteJobTable(1);
        var first = full.Add(Json("{\"op\":\"where\"}")!, 0, out _)!;
        full.Finish(first, true, "ok", null, 1);
        for (int i = 1; i < RouteJobTable.Capacity; i++) full.Add(Json("{\"op\":\"where\"}")!, i, out _);
        Check(full.Count == RouteJobTable.Capacity, "32 kept");
        RouteJob? next = full.Add(Json("{\"op\":\"where\"}")!, 99, out _);
        Check(next != null && full.Get(first.Id) == null && full.Count == RouteJobTable.Capacity, "the 33rd drops the oldest finished job");
        RouteJob? refused = full.Add(Json("{\"op\":\"where\"}")!, 100, out string why);
        Check(refused == null && why.Contains("32"), "with all 32 unfinished, a new one is refused: " + why);
        Check(!full.PollJson(first.Id, out bool gone).Contains("\"done\"") && !gone, "a dropped job is unknown");
        Check(RouteJobTable.StateName(RouteJobState.Working) == "working" && RouteJobTable.StateName(RouteJobState.Failed) == "failed", "state names");

        // A go job's end, from the goto's own lines (FinishGoto, Entrance, a stop).
        Check(RouteJobTable.GoOutcome("arrived", out string o1) && o1 == "arrived", "arrived: done");
        Check(RouteJobTable.GoOutcome(Entrance.ArrivedText("Matron Hive", "Dungeon"), out string o2) && o2 == "arrived", "into a dungeon: arrived");
        Check(RouteJobTable.GoOutcome(Entrance.ArrivedText("Portal to X", "Portal"), out string o3) && o3 == "arrived", "through a portal: arrived");
        Check(RouteJobTable.GoOutcome("stopped", out string o4) && o4 == "stopped", "a stop: done, stopped");
        Check(!RouteJobTable.GoOutcome("stopped: no progress for 30 s, 12u short (off navmesh)", out string o5) && o5 == "failed", "the watchdog: failed");
        Check(!RouteJobTable.GoOutcome(Entrance.NoTeleportText("Matron Hive"), out _), "a portal that didn't take you in: failed");
        Check(!RouteJobTable.GoOutcome("stopped: logged out", out _) && !RouteJobTable.GoOutcome("recall step lost — stopped", out _), "logged out, a lost recall: failed");
    }

    // ── Loops: order and the nav file ────────────────────────────────────

    private static void LoopOrderNoCrossings()
    {
        var c = new WorldPt(5000, 5000, 0);
        var rnd = new Random(7);
        for (int trial = 0; trial < 50; trial++)
        {
            int n = 3 + rnd.Next(14);
            var pts = new List<WorldPt>();
            for (int i = 0; i < n; i++)
            {
                double a = rnd.NextDouble() * 2 * Math.PI, r = 30 + rnd.NextDouble() * 120;
                pts.Add(new WorldPt(c.X + Math.Sin(a) * r, c.Y + Math.Cos(a) * r, 0));
            }
            int[] order = RouteShape.OrderLoop(pts, c);
            Check(order.Length == n && order.Distinct().Count() == n, $"trial {trial}: every point once");
            var loop = order.Select(i => pts[i]).ToList();
            Check(RouteShape.Crossings(loop, closed: true) == 0, $"trial {trial}: no two legs cross ({n} points)");
        }
        // A star-shaped set given in a tangled order comes out round the centre, from the north, clockwise.
        var star = new List<WorldPt> { new(5100, 5000, 0), new(4900, 5000, 0), new(5000, 5100, 0), new(5000, 4900, 0) };   // E, W, N, S
        int[] o = RouteShape.OrderLoop(star, c);
        Check(o.SequenceEqual(new[] { 2, 0, 3, 1 }), "N, E, S, W: " + string.Join(",", o));
        Check(RouteShape.Crossings(new List<WorldPt> { new(0, 0, 0), new(10, 10, 0), new(10, 0, 0), new(0, 10, 0) }, closed: true) == 1, "a bow tie crosses once");
        Check(RouteShape.DefaultPoints(20) == 4 && RouteShape.DefaultPoints(80) == 11 && RouteShape.DefaultPoints(250) == 12, "default points: about one per 45 yd, 4 to 12");
    }

    private static void LoopNavWrittenAndReadBack()
    {
        // Three legs round a triangle, the last back to the start; corners and heights along them.
        WorldPt a = WorldPt.FromMap(10, 10, 50), b = WorldPt.FromMap(10.3, 10, 52), cc = WorldPt.FromMap(10.15, 10.3, 51);
        List<WorldPt> Leg(WorldPt p, WorldPt q)
        {
            var l = new List<WorldPt>();
            for (int i = 0; i <= 40; i++) { double t = i / 40.0; l.Add(new WorldPt(p.X + (q.X - p.X) * t + Math.Sin(t * Math.PI) * 6, p.Y + (q.Y - p.Y) * t, p.H + (q.H - p.H) * t)); }
            return l;
        }
        var legs = new List<IReadOnlyList<WorldPt>> { Leg(a, b), Leg(b, cc), Leg(cc, a) };
        NavRouteParser route = RouteShape.Assemble(legs, NavRouteType.Circular, RouteShape.LoopSpacing, out double yards);
        Check(route.RouteType == NavRouteType.Circular, "a circular route");
        Check(NavCoords.Distance(route.Points[0].NS, route.Points[0].EW, a.Ns, a.Ew) < 0.01, "it starts at the first point");
        Check(NavCoords.Distance(route.Points[^1].NS, route.Points[^1].EW, a.Ns, a.Ew) > 1, "and doesn't repeat it at the end (RynthAi walks back to it)");
        Check(route.Points.All(p => p.Type == NavPointType.Point), "all walking points");
        double gap = 0;
        for (int i = 0; i < route.Points.Count; i++)
        {
            NavPoint p = route.Points[i], q = route.Points[(i + 1) % route.Points.Count];
            gap = Math.Max(gap, NavCoords.Distance(p.NS, p.EW, q.NS, q.EW));
        }
        Check(gap <= RouteShape.LoopSpacing + 0.5, $"a point at least every {RouteShape.LoopSpacing} yd ({gap:F1})");
        Check(route.Points.Count < 3 * 41, "thinned, not every input point");
        Check(route.Points.All(p => p.Z > 49.0 / 240 && p.Z < 53.0 / 240), "heights as nav Z (metres / 240)");
        Check(Math.Abs(yards - RouteInfo.Length(route)) < 1.0, $"its yards are the file's walking length, the lap closed ({yards:F1} / {RouteInfo.Length(route):F1})");

        string dir = Path.Combine(Path.GetTempPath(), "rnav-loop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(NavRouteExport.Save(dir, "Triangle", route, false, out string file, out _, out string err), "saved: " + err);
            NavRouteParser back = NavRouteParser.Load(file);
            Check(back.LoadWarning == null && back.RouteType == NavRouteType.Circular && back.Points.Count == route.Points.Count, "RynthAi's reader takes it whole");
            bool same = true;
            for (int i = 0; i < back.Points.Count; i++)
                same &= back.Points[i].Type == NavPointType.Point && Math.Abs(back.Points[i].NS - route.Points[i].NS) < 1e-9
                     && Math.Abs(back.Points[i].EW - route.Points[i].EW) < 1e-9 && Math.Abs(back.Points[i].Z - route.Points[i].Z) < 1e-9;
            Check(same, "every point the same after the round trip");
            string[] lines = File.ReadAllLines(file);
            Check(lines[0] == "uTank2 NAV 1.2" && lines[1] == "1" && lines[2] == route.Points.Count.ToString(), "the VTank header: type 1 (circular), the count");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // Once and linear keep both ends.
        var once = RouteShape.Assemble(new List<IReadOnlyList<WorldPt>> { Leg(a, b) }, NavRouteType.Once, 30, out _);
        Check(NavCoords.Distance(once.Points[^1].NS, once.Points[^1].EW, b.Ns, b.Ew) < 0.01 && once.RouteType == NavRouteType.Once, "once ends at the last point");
    }

    // ── routeinfo ────────────────────────────────────────────────────────

    private static void RouteInfoOnASampleFile()
    {
        // A trip: walk 3 points, a recall, walk 2, a portal, walk 2 more (in a dungeon landblock), a chat and a pause.
        var r = new NavRouteParser { RouteType = NavRouteType.Once };
        void P(NavPointType t, double ns, double ew, Action<NavPoint>? f = null) { var p = new NavPoint { Type = t, NS = ns, EW = ew }; f?.Invoke(p); r.Points.Add(p); }
        P(NavPointType.Point, 10.0, 10.0); P(NavPointType.Point, 10.1, 10.0); P(NavPointType.Point, 10.1, 10.1);
        P(NavPointType.Recall, 10.1, 10.1, p => p.SpellId = 1635);
        P(NavPointType.Point, 40.0, 40.0); P(NavPointType.Point, 40.0, 40.2);
        P(NavPointType.PortalNPC, 40.0, 40.25, p => { p.TargetName = "Portal to Somewhere"; p.ObjectClass = 14; });
        P(NavPointType.Point, 61.87, -100.98); P(NavPointType.Point, 61.88, -100.97);
        P(NavPointType.Chat, 61.88, -100.97, p => p.ChatCommand = "@lifestone");
        P(NavPointType.Pause, 61.88, -100.97, p => p.PauseTimeMs = 20000);
        uint dunLb = WorldPt.FromMap(61.87, -100.98).Landblock;
        string dir = Path.Combine(Path.GetTempPath(), "rnav-info-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(NavRouteExport.Save(dir, "Sample", r, false, out string file, out _, out _), "the sample is written");
            NavRouteParser back = NavRouteParser.Load(file);
            RouteInfo info = RouteInfo.Describe(back, "Sample", file, lb => lb == dunLb, true, 10.0, 10.05,
                id => id == 1635 ? "Lifestone Recall" : null);
            Check(info.Type == "once" && info.Points == 11, "type and points");
            Check(info.Waypoints == 7 && info.Recalls == 1 && info.Portals == 1 && info.Chats == 1 && info.Pauses == 1, "the points by kind");
            // Walks: 10.0->10.1 (24 yd), 10.1,10.0->10.1,10.1 (24), 40,40->40,40.2 (48), 40,40.2->portal (12), the dungeon pair (~3.4).
            double dun = NavCoords.Distance(61.87, -100.98, 61.88, -100.97);
            Near(info.Yards, 24 + 24 + 48 + 12 + dun, 0.5, "yards: the walks only, not across the recall or the portal");
            Near(info.MinNs, 10.0, 1e-9, "box: south edge"); Near(info.MaxNs, 61.88, 1e-9, "box: north edge");
            Near(info.MinEw, -100.98, 1e-9, "box: west edge"); Near(info.MaxEw, 40.25, 1e-9, "box: east edge");
            Near(info.CentreNs, (10.0 + 61.88) / 2, 1e-9, "centre ns");
            Near(info.CentreEw, (-100.98 + 40.25) / 2, 1e-9, "centre ew");
            Check(info.HasStart && info.StartNs == 10.0 && info.StartEw == 10.0, "the start is the first point");
            Near(info.StartYards, NavCoords.Distance(10.0, 10.05, 10.0, 10.0), 0.01, "the player's distance to the start");
            Check(info.Landblocks.Contains(WorldPt.FromMap(10, 10).Landblock) && info.Landblocks.Contains(dunLb) && info.Landblocks.Contains(WorldPt.FromMap(40, 40.1).Landblock), "landblocks it covers");
            Check(info.Landblocks.SequenceEqual(info.Landblocks.OrderBy(x => x)) && info.Landblocks.Distinct().Count() == info.Landblocks.Count, "each once, in order");
            Check(!info.Dungeon && info.DungeonLandblocks.SequenceEqual(new[] { dunLb }), "partly in a dungeon (2 of 7 points): not a dungeon route, the landblock listed");
            Check(info.PortalNames.SequenceEqual(new[] { "Portal to Somewhere" }) && info.RecallNames.SequenceEqual(new[] { "Lifestone Recall" })
                && info.ChatCommands.SequenceEqual(new[] { "@lifestone" }), "portals, recalls and commands used");
            Check(info.NearestIndex == 0 || info.NearestIndex == 2, "the nearest waypoint to the player");

            using JsonDocument d = JsonDocument.Parse(RouteJson.Write(info.WriteJson));
            JsonElement e = d.RootElement;
            Check(e.GetProperty("type").GetString() == "once" && e.GetProperty("points").GetInt32() == 11 && e.GetProperty("counts").GetProperty("portals").GetInt32() == 1, "JSON: type, points, counts");
            Check(e.GetProperty("box").GetProperty("minEw").GetDouble() == -100.98 && e.GetProperty("centre").TryGetProperty("ew", out _), "JSON: box and centre");
            Check(e.GetProperty("landblocks").EnumerateArray().Any(x => x.GetString() == dunLb.ToString("X4")), "JSON: landblocks as hex");
            Check(e.GetProperty("dungeon").GetBoolean() == false && e.GetProperty("portals")[0].GetString() == "Portal to Somewhere", "JSON: dungeon, portals");
            Check(e.GetProperty("start").GetProperty("yards").ValueKind == JsonValueKind.Number, "JSON: the start's distance");
            List<string> lines = info.ChatLines();
            Check(lines.Count >= 4 && lines[0].StartsWith("routeinfo Sample: once, 11 point(s)") && lines.Any(l => l.Contains("portals: Portal to Somewhere")), "chat lines: " + lines[0]);

            // A dungeon route: most of it in dungeon landblocks; no distance from a player on the landscape.
            var dr = new NavRouteParser { RouteType = NavRouteType.Circular };
            foreach (double k in new[] { 0.0, 0.01, 0.02, 0.03 }) dr.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 61.87 + k, EW = -100.98 });
            RouteInfo di = RouteInfo.Describe(dr, "Crypt", "", lb => lb == dunLb, true, 10, 10, null);
            Check(di.Dungeon && di.Type == "circular" && double.IsNaN(di.StartYards) && di.NearestIndex < 0, "a dungeon route: no map distance from the player");
            Near(di.Yards, 0.03 * 240 * 2, 0.01, "a circular route's yards include the way back to the start");
            RouteInfo none = RouteInfo.Describe(new NavRouteParser { RouteType = NavRouteType.Once }, "Empty", "", null, false, 0, 0, null);
            Check(none.Points == 0 && !none.HasBox && none.Landblocks.Count == 0, "an empty route");
            Check(RouteInfo.Describe(r, "x", "", null, false, 0, 0, null).RecallNames[0] == "spell 1635", "a spell the caller can't name");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static HashSet<uint>? _realDungeons, _realMap;

    /// <summary>
    /// A dungeon landblock as the plugin judges one for routeinfo (RynthNavPlugin.RouteMaker.cs InfoWork):
    /// the Atlas's dungeon landblocks, and those with a dungeon navmesh and no places on the map.
    /// </summary>
    private static bool RealDungeon(string navData, uint lb)
    {
        if (_realDungeons == null || _realMap == null)
        {
            string loc = Path.Combine(navData, "locations.json");
            Atlas atlas = File.Exists(loc) ? Atlas.Load(loc) : Atlas.Empty;
            _realDungeons = atlas.DungeonLandblocks();
            _realMap = new HashSet<uint>();
            foreach (AtlasLocation l in atlas.Locations)
                if (l.OnMap && l.Cell.Length == 8 && uint.TryParse(l.Cell.AsSpan(0, 4), System.Globalization.NumberStyles.HexNumber, null, out uint m)) _realMap.Add(m);
        }
        return _realDungeons.Contains(lb) || (!_realMap.Contains(lb) && File.Exists(Path.Combine(navData, $"dun_{lb:X4}.tile")));   // DungeonTileFile.FileName (needs Detour: not linked here)
    }

    /// <summary>The real NavProfiles (read only, when here): every route RynthAi has describes without a throw and adds up.</summary>
    private static void RouteInfoOnRealNavProfiles()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHAI_NAVPROFILES") ?? @"C:\Games\RynthSuite\RynthAi\NavProfiles";
        if (!Directory.Exists(dir)) { Console.WriteLine("     (no NavProfiles here: skipped)"); return; }
        string navData = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        Func<uint, bool>? dungeon = File.Exists(Path.Combine(navData, "locations.json")) ? lb => RealDungeon(navData, lb) : null;
        int files = 0, dungeons = 0, points = 0;
        foreach (string f in Directory.EnumerateFiles(dir, "*.nav"))
        {
            files++;
            NavRouteParser route = NavRouteParser.Load(f);
            RouteInfo info = RouteInfo.Describe(route, Path.GetFileNameWithoutExtension(f), f, dungeon, true, 42, 33, null);
            points += info.Points;
            if (info.Dungeon) dungeons++;
            Check(info.Points == route.Points.Count && info.Waypoints + info.Portals + info.Recalls + info.Chats + info.Pauses + info.Others == info.Points,
                $"route #{files}: the kinds add up");
            Check(double.IsFinite(info.Yards) && info.Yards >= 0, $"route #{files}: a length");
            Check(info.Points == 0 || info.HasBox, $"route #{files}: a box");
            using JsonDocument d = JsonDocument.Parse(RouteJson.Write(info.WriteJson));
            Check(d.RootElement.GetProperty("points").GetInt32() == info.Points, $"route #{files}: its JSON parses");
        }
        Console.WriteLine($"     {files} real nav file(s), {points} points in all, {dungeons} read as dungeon routes{(dungeon == null ? " (no Atlas here: none can be)" : "")}");
    }

    // ── plan's figures ───────────────────────────────────────────────────

    private static void PlanSummaryFigures()
    {
        var steps = new List<RouteStep>
        {
            new(10, 10, false, true, "Lifestone Recall", 0, 20, 20),
            new(20.5, 20, true, false, "Portal to Far", -1, 50, 50),
            new(50.4, 50.3, false, false, "walk"),
        };
        var trip = Trip(steps, 10, 10, "Far Goal", 50.4, 50.3, new List<TripRecall> { new("Lifestone Recall", 1635, "", 13) });
        TripPath path = TripPathBuilder.Build(trip, FakeWalk, null);
        TripSummary s = TripSummary.Of(trip, path);
        Check(s.Steps.Select(x => x.Kind).SequenceEqual(new[] { "recall", "portal", "walk" }), "kinds: recall, portal, walk");
        Check(s.Steps[0].Name == "Lifestone Recall" && s.Steps[1].Name == "Portal to Far" && s.Steps[2].Name == "Far Goal", "names: the recall, the portal, the goal");
        Check(s.Steps[0].Yards == 0 && s.Steps[1].Yards > 120 && s.Steps[2].Yards > 96, "yards: none for a recall, the walked path for the rest");
        Check(s.Steps[0].LandNs == 20 && s.Steps[1].LandNs == 50 && double.IsNaN(s.Steps[2].LandNs), "where the teleports land");
        Check(s.Teleports == 2 && s.CastSeconds == 13, "two teleports, the recall's cast time");
        Near(s.Yards, s.Steps.Sum(x => x.Yards), 1e-9, "the yards add up");
        Near(s.Minutes, (s.Yards / PortalRoute.RunUnitsPerSecond + 2 * PortalRoute.TeleportOverheadUnits / PortalRoute.RunUnitsPerSecond + 13) / 60, 1e-9, "minutes: running, teleports, casting");
        TripSummary straight = TripSummary.Of(trip, null);
        Near(straight.Steps[1].Yards, NavCoords.Distance(20, 20, 20.5, 20), 0.01, "without a path: the straight line");
        using JsonDocument d = JsonDocument.Parse(RouteJson.Write(w => { w.WriteStartObject(); s.WriteFields(w); w.WriteEndObject(); }));
        Check(d.RootElement.GetProperty("steps").GetArrayLength() == 3 && d.RootElement.GetProperty("teleports").GetInt32() == 2
            && d.RootElement.GetProperty("steps")[1].GetProperty("land").GetProperty("ns").GetDouble() == 50, "plan JSON fields");
        Check(d.RootElement.GetProperty("steps")[2].TryGetProperty("land", out _) == false, "a walk has no landing");
    }

    // ── worldmap.vec ─────────────────────────────────────────────────────

    private static void BuildingOutlinesFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rnav-vec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Two buildings (in map cells, 24 yd): a square at cells (10..11, 10..11) and a triangle; then a road.
            string file = Path.Combine(dir, BuildingOutlines.FileName);
            using (var w = new BinaryWriter(File.Create(file)))
            {
                w.Write(new[] { (byte)'R', (byte)'V', (byte)'E', (byte)'C' }); w.Write(1u); w.Write(2u); w.Write(1u);
                w.Write((byte)4); foreach (var (x, y) in new[] { (10f, 10f), (11f, 10f), (11f, 11f), (10f, 11f) }) { w.Write(x); w.Write(y); }
                w.Write((byte)3); foreach (var (x, y) in new[] { (20f, 20f), (21f, 20f), (20f, 21f) }) { w.Write(x); w.Write(y); }
                w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)0);
            }
            BuildingOutlines? b = BuildingOutlines.Load(file);
            Check(b != null && b.Count == 2, "two buildings read");
            Check(b!.Inside(10.5 * 24, 10.5 * 24) == 0, "inside the square");
            Check(b.Inside(11.2 * 24, 10.5 * 24) == -1, "4.8 yd outside: not inside");
            Check(b.Inside(11.2 * 24, 10.5 * 24, margin: 5) == 0, "...but within a 5 yd margin");
            Check(b.Inside(20.2 * 24, 20.2 * 24) == 1 && b.Inside(20.9 * 24, 20.9 * 24) == -1, "the triangle, not its bounding box");
            Check(b.Inside(-5, -5) == -1, "off the map: none");
            File.WriteAllBytes(Path.Combine(dir, "bad.vec"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
            Check(BuildingOutlines.Load(Path.Combine(dir, "bad.vec")) == null && BuildingOutlines.Load(Path.Combine(dir, "none.vec")) == null, "a bad or missing file: null");
            var fromPolys = BuildingOutlines.FromPolygons(new[] { new (double, double)[] { (0, 0), (10, 0), (10, 10), (0, 10) } });
            Check(fromPolys.Inside(5, 5) == 0 && fromPolys.Inside(15, 5) == -1, "outlines given in world units");

            string real = Path.Combine(Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData", BuildingOutlines.FileName);
            if (File.Exists(real))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BuildingOutlines? all = BuildingOutlines.Load(real);
                Console.WriteLine($"     the real {BuildingOutlines.FileName}: {all?.Count} buildings in {sw.ElapsedMilliseconds} ms");
                Check(all != null && all.Count > 1000, "the real file reads (thousands of buildings)");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
