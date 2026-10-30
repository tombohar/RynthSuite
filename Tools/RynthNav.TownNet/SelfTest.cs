using System.Numerics;
using System.Text.Json.Nodes;

namespace RynthNav.TownNet;

/// <summary>
/// Geometry self-tests on a made-up floor, no dats: two 10 x 10 m rooms side by side, joined by a
/// 2 m doorway in the wall between them, a pillar in the second room, and an exit portal in its far
/// corner with another portal near the doorway. Exit code 0 = all pass.
/// </summary>
internal static class SelfTest
{
    private const uint A = 0x00010100, B = 0x00010101;
    private static int _fail;

    public static int Run()
    {
        var w = MakeWorld();
        var grid = new NavGrid(w, new[] { A, B });
        var planner = new WalkPlanner(w, grid);

        // The cell trace: through the doorway is fine, through the wall is not, off the floor is not.
        Expect(w.Trace(new Vector3(5, 5, 0), new Vector3(15, 5, 0), A, out _)?.SequenceEqual(new[] { A, B }) == true, "through the doorway: A then B");
        Expect(w.Trace(new Vector3(5, 1, 0), new Vector3(15, 1, 0), A, out var why) == null, "through the wall is refused (" + why + ")");
        Expect(w.Trace(new Vector3(5, 5, 0), new Vector3(5, 12, 0), A, out why) == null, "off the floor is refused (" + why + ")");

        // Clearance: the pillar and the walls show up in the grid.
        var nearPillar = grid.Nearest(B, 15f, 2.6f, _ => true)!;
        Expect(nearPillar.Clear < 0.2f, $"next to the pillar the clearance is small ({nearPillar.Clear:F2})");
        var middle = grid.Nearest(A, 5f, 5f, _ => true)!;
        Expect(middle.Clear > 2.9f, $"mid-room clearance is large (capped at 3 m) ({middle.Clear:F2})");

        // A walk from the first room to the exit portal in the second: it exists, passes the check,
        // goes through the doorway, keeps clear of the pillar and of the other portal.
        var exit = w.Portals[0];
        var walks = planner.PlanFrom("A1", A, new Vector3(2, 8.5f, 0.005f), w.Portals);
        var walk = walks.First(x => x.To == exit.Guid);
        Expect(walk.Error == null, "a walk to the exit portal exists (" + walk.Error + ")");
        if (walk.Error == null)
        {
            Expect(walk.Points.Count >= 3, $"it turns at least once ({walk.Points.Count} points)");
            Expect(walk.Cells == 2, $"it crosses into the second room ({walk.Cells} cells)");
            bool door = false;
            for (int i = 1; i < walk.Points.Count; i++)
            {
                var a = walk.Points[i - 1].P; var b = walk.Points[i].P;
                if ((a.X - 10) * (b.X - 10) <= 0 && b.X != a.X)
                {
                    float y = a.Y + (b.Y - a.Y) * (10 - a.X) / (b.X - a.X);
                    door = y > 4f && y < 6f;
                }
            }
            Expect(door, "it crosses the wall line inside the doorway");
            Expect(walk.MinClear >= w.AgentRadius, $"it keeps the walker's radius from walls and the pillar ({walk.MinClear:F2})");
            Expect(walk.MinPortalClear >= planner.MustPortal, $"it keeps clear of the other portal ({walk.MinPortalClear:F2})");
            Expect(Vector2.Distance(new Vector2(walk.Points[^1].P.X, walk.Points[^1].P.Y), new Vector2(exit.Pos.X, exit.Pos.Y)) < 0.01f, "it ends in the portal");

            // The check catches a shortcut through the wall.
            var cut = new Walk { From = "A1", To = exit.Guid, Points = { new Waypoint(A, new Vector3(2, 2, 0)), new Waypoint(B, new Vector3(18, 2, 0)), walk.Points[^1] } };
            planner.Check(cut, exit);
            Expect(cut.Error != null, "the check refuses a walk through the wall (" + cut.Error + ")");
        }

        // A portal behind a wall with no way round is reported, not faked.
        var shut = MakeWorld(doorway: false);
        var shutPlanner = new WalkPlanner(shut, new NavGrid(shut, new[] { A, B }));
        var none = shutPlanner.PlanFrom("A1", A, new Vector3(2, 2, 0), shut.Portals);
        Expect(none.All(x => x.Error != null), "with the doorway walled up there is no walk (" + none[0].Error + ")");

        // The file: the set id ignores the timestamp and changes with the data.
        var doc = new JsonObject { ["version"] = 1, ["setId"] = "", ["generated"] = "2026-01-01T00:00:00Z", ["arrivals"] = new JsonArray(new JsonObject { ["id"] = "A1" }) };
        string id1 = TownNetFile.SetId(TownNetFile.Serialize(doc));
        doc["generated"] = "2027-01-01T00:00:00Z";
        string id2 = TownNetFile.SetId(TownNetFile.Serialize(doc));
        doc["arrivals"]!.AsArray().Add(new JsonObject { ["id"] = "A2" });
        string id3 = TownNetFile.SetId(TownNetFile.Serialize(doc));
        Expect(id1 == id2 && id1 != id3 && id1.Length == 16, "set id: same data same id, new data new id");
        Expect(TownNetFile.SetId(TownNetFile.Serialize(doc).Replace("\n", "\r\n")) == id3, "set id: a CRLF checkout gives the same id");
        Expect(TownNetFile.Serialize(doc).Contains("    {\"id\":\"A2\"}\n"), "lists hold one item per line");

        Console.WriteLine(_fail == 0 ? "all self-tests pass" : $"{_fail} self-test(s) FAILED");
        return _fail == 0 ? 0 : 1;
    }

    private static void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    private static Vector3[] Quad(float x0, float y0, float x1, float y1, float z) =>
        new[] { new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z) };

    /// <summary>A vertical wall from (x0, y0) to (x1, y1), 4 m high.</summary>
    private static Vector3[] Wall(float x0, float y0, float x1, float y1) =>
        new[] { new Vector3(x0, y0, 0), new Vector3(x1, y1, 0), new Vector3(x1, y1, 4), new Vector3(x0, y0, 4) };

    private static Indoor MakeWorld(bool doorway = true)
    {
        var w = new Indoor { AgentRadius = 0.5f };
        var a = new EnvCell { Id = A, Floors = { Quad(0, 0, 10, 10, 0) } };
        var b = new EnvCell { Id = B, Floors = { Quad(10, 0, 20, 10, 0) } };
        foreach (var c in new[] { a, b })
        {
            float x0 = c == a ? 0 : 10, x1 = x0 + 10;
            c.Walls.Add(Wall(x0, 0, x1, 0)); c.Walls.Add(Wall(x0, 10, x1, 10));
            c.Walls.Add(Wall(c == a ? 0 : 20, 0, c == a ? 0 : 20, 10));
            c.Walls.Add(Wall(10, 0, 10, 4)); c.Walls.Add(Wall(10, 6, 10, 10));
            if (!doorway) c.Walls.Add(Wall(10, 4, 10, 6));
        }
        if (doorway)
        {
            var door = Wall(10, 4, 10, 6);
            a.Portals.Add(new CellPortal { OtherCell = (ushort)(B & 0xFFFF), Polygon = door });
            b.Portals.Add(new CellPortal { OtherCell = (ushort)(A & 0xFFFF), Polygon = door });
        }
        w.Cells[A] = a; w.Cells[B] = b;
        foreach (var c in new[] { a, b })
            foreach (var wall in c.Walls) w.Solids.Add(Footprint.FromPolygon(wall, $"wall {c.Id:X8}"));
        w.Solids.Add(Footprint.Disc(15, 3, 0.5f, 0, 3, "pillar"));
        w.Portals.Add(new PortalZone { Guid = "EXIT", Name = "Exit", Cell = B, Pos = new Vector3(18, 8, 0), Radius = 1f });
        w.Portals.Add(new PortalZone { Guid = "OTHER", Name = "Other", Cell = B, Pos = new Vector3(13, 8.5f, 0), Radius = 1f });
        return w;
    }
}
