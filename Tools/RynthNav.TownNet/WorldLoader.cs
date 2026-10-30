using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using DatDb = RynthCore.Plugin.RynthAi.Raycasting.DatDatabase;

namespace RynthNav.TownNet;

/// <summary>Builds the <see cref="Indoor"/> world for townnet.json from the dats and the file's portal list.</summary>
internal static class WorldLoader
{
    public const uint HumanSetup = 0x02000001;        // the player's model: its radius is the walker's

    public static uint Hex(JsonNode? n) => uint.Parse((string)n!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public static float F(JsonNode? n) => (float)(double)n!;

    public static Indoor Load(JsonObject doc, DatDb cellDat, DatDb portalDat, out List<uint> reachable)
    {
        var w = new Indoor();
        var models = new ModelReader(portalDat);
        // The walker's body: the player model's collision spheres (not its larger selection radius).
        var human = models.Get(HumanSetup);
        float body = human.Spheres.Select(sp => sp.Radius).Concat(human.Cylinders.Select(c => c.Radius)).DefaultIfEmpty(0).Max();
        if (body > 0.1f) w.AgentRadius = body;
        else w.Notes.Add($"setup {HumanSetup:X8} has no collision spheres; walker radius {w.AgentRadius} m assumed");

        foreach (var lbNode in doc["landblocks"]!.AsArray())
        {
            uint lb = Hex(lbNode);
            foreach (var c in CellDat.LoadLandblock(cellDat, portalDat, lb)) w.Cells[c.Id] = c;
        }

        // The cells someone arriving can reach through cell portals.
        var arrivalCells = doc["arrivals"]!.AsArray().Select(a => Hex(a!["cell"])).ToList();
        var seen = new HashSet<uint>();
        var queue = new Queue<uint>();
        foreach (uint c in arrivalCells)
        {
            if (!w.Cells.ContainsKey(c)) { w.Notes.Add($"arrival cell {c:X8} is not in the dats"); continue; }
            if (seen.Add(c)) queue.Enqueue(c);
        }
        while (queue.Count > 0)
        {
            var c = w.Cells[queue.Dequeue()];
            foreach (var p in c.Portals)
            {
                if (p.OtherCell == 0xFFFF) continue;
                uint o = w.Full(c, p.OtherCell);
                if (w.Cells.ContainsKey(o) && seen.Add(o)) queue.Enqueue(o);
            }
        }
        reachable = seen.OrderBy(x => x).ToList();

        // Solid things: the cells' own walls (every physics polygon that is not a floor) and their static objects.
        int stabSolid = 0, stabPass = 0;
        foreach (uint id in reachable)
        {
            var c = w.Cells[id];
            foreach (var wall in c.Walls) w.Solids.Add(Footprint.FromPolygon(wall, $"wall {id:X8}"));
            foreach (var s in c.Stabs)
            {
                var shape = models.Get(s.Id);
                if (shape.IsEmpty) { stabPass++; continue; }
                stabSolid++;
                AddShape(w, shape, s.Origin, s.Rotation, 1f, $"object {s.Id:X8} in {id:X8}");
            }
        }
        w.Notes.Add($"{reachable.Count} cells reachable from the arrivals; {stabSolid} solid static objects, {stabPass} with no physics");

        foreach (var n in doc["npcs"]?.AsArray() ?? new JsonArray())
        {
            if (n?["setup"] == null) continue;
            var shape = models.Get(Hex(n["setup"]));
            var pos = new Vector3(F(n["x"]), F(n["y"]), F(n["z"]));
            if (shape.Cylinders.Count + shape.Spheres.Count > 0) AddShape(w, shape, pos, Quaternion.Identity, 1f, "npc " + (string?)n["name"], polygons: false);
            else w.Solids.Add(Footprint.Disc(pos.X, pos.Y, MathF.Max(shape.Radius, 0.5f), pos.Z, pos.Z + MathF.Max(shape.Height, 1.8f), "npc " + (string?)n["name"]));
        }

        foreach (var e in doc["exits"]!.AsArray())
        {
            float scale = e!["scale"] != null ? F(e["scale"]) : 1f;
            float r = 0;
            if (e["setup"] != null)
            {
                var shape = models.Get(Hex(e["setup"]));
                r = shape.Cylinders.Select(c => c.Radius + new Vector2(c.Origin.X, c.Origin.Y).Length())
                    .Concat(shape.Spheres.Select(s => s.Radius + new Vector2(s.Center.X, s.Center.Y).Length()))
                    .DefaultIfEmpty(shape.Radius).Max() * scale;
            }
            if (r <= 0.1f) { r = 1.0f; w.Notes.Add($"portal {(string?)e["name"]} has no collision radius in its setup; 1 m assumed"); }
            w.Portals.Add(new PortalZone
            {
                Guid = (string)e["guid"]!, Name = (string)e["name"]!, Cell = Hex(e["cell"]),
                Pos = new Vector3(F(e["x"]), F(e["y"]), F(e["z"])), Radius = r,
            });
        }
        return w;
    }

    private static void AddShape(Indoor w, ModelShape shape, Vector3 origin, Quaternion rot, float scale, string source, bool polygons = true)
    {
        if (polygons)
            foreach (var poly in shape.Polygons)
            {
                // Every polygon, tops included: Footprint.Blocks keeps only what stands between
                // step-up height and head height, so a low top you can step onto never blocks.
                var p = poly.Select(v => Vector3.Transform(v * scale, rot) + origin).ToArray();
                w.Solids.Add(Footprint.FromPolygon(p, source));
            }
        foreach (var (o, r, h) in shape.Cylinders)
        {
            var c = Vector3.Transform(o * scale, rot) + origin;
            w.Solids.Add(Footprint.Disc(c.X, c.Y, r * scale, c.Z, c.Z + h * scale, source));
        }
        foreach (var (o, r) in shape.Spheres)
        {
            var c = Vector3.Transform(o * scale, rot) + origin;
            w.Solids.Add(Footprint.Disc(c.X, c.Y, r * scale, c.Z - r * scale, c.Z + r * scale, source));
        }
    }
}
