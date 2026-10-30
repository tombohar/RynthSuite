using System.Text.Json;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthNet.Characters;
using RynthCore.Plugin.Shared.RynthUi;

namespace RynthNet.Tests;

// The parts of the Characters window that moved from RynthCore with it (2026-10-21).
internal static partial class Program
{
    private static void WindowChecks()
    {
        Rows();
        RowsOutOfWorld();
        Atlas();
        Manifest();
        IconsMatchEngine();
    }

    private sealed class FakeGame : ICharacterGame
    {
        public uint PlayerId { get; set; } = 0x50000001;
        public bool InWorld { get; set; } = true;
        public bool Portaling { get; set; }
        public uint SelectedId { get; set; }
        public uint Cell = 0xA9B40019;
        public float X = 100, Y = 100, Z = 10;
        public readonly Dictionary<uint, (string Name, uint Cell, float X, float Y, float Z)> Objects = new();

        public bool TryGetPose(out uint cell, out float x, out float y, out float z)
        {
            cell = Cell; x = X; y = Y; z = Z;
            return Cell != 0;
        }

        public bool TryGetVitals(out uint hp, out uint hpMax, out uint st, out uint stMax, out uint mp, out uint mpMax)
        {
            hp = 300; hpMax = 400; st = 100; stMax = 200; mp = 50; mpMax = 100;
            return true;
        }

        public bool TryGetName(uint id, out string name)
        {
            bool ok = Objects.TryGetValue(id, out var o);
            name = ok ? o.Name : "";
            return ok;
        }

        public bool TryGetPosition(uint id, out uint cell, out float x, out float y, out float z)
        {
            bool ok = Objects.TryGetValue(id, out var o);
            cell = o.Cell; x = o.X; y = o.Y; z = o.Z;
            return ok;
        }
    }

    private static NetCharEntry Peer(string name, uint id, uint cell, float x, float y, string world = "Aelrynth") => new()
    {
        Name = name, LoggedIn = true, World = world, HasStatus = true, AgeMs = 400, Id = id,
        HasPose = true, Cell = cell, X = x, Y = y, Z = 10,
        HasVitals = true, Hp = 10, HpMax = 20, St = 5, StMax = 10, Mp = 1, MpMax = 2,
    };

    private static void Rows()
    {
        var game = new FakeGame { SelectedId = 0x50000002 };
        var snap = new NetCharSnapshot { Online = true, Version = "0.1.1", MyName = "Freya", MyWorld = "Aelrynth" };
        snap.Clients.Add(Peer("Zed", 0x50000002, 0xA9B40019, 100, 130));          // 30 m north, same landblock
        snap.Clients.Add(Peer("Alpha", 0x50000003, 0xA9B40019, 100, 100, "Coldeve"));
        snap.Clients.Add(Peer("Bob", 0x50000004, 0xA9B40019, 0, 0));               // the status says the corner...
        game.Objects[0x50000004] = ("Bob", 0xA9B40019, 160, 100, 10);              // ...but this client sees it 60 m east
        CharactersView v = CharactersRows.Build(snap, game);

        Check(v.Rows.Length == 4 && v.Rows[0].IsSelf && v.Rows[0].Name == "Freya", "this client first, by its name");
        Check(v.Rows[0].HasVitals && v.Rows[0].Hp.Text == "300/400" && v.Rows[0].Location == "42.5N 33.7E", "self: vitals and where: " + v.Rows[0].Location);
        Check(string.Join(",", v.Rows.Skip(1).Select(r => r.Name)) == "Alpha,Bob,Zed", "the others alphabetically");
        CharacterRow alpha = v.Rows[1], bob = v.Rows[2], zed = v.Rows[3];
        Check(alpha.OtherWorld == "Coldeve" && alpha.Location == "on Coldeve" && !alpha.Measured, "another server: its name, no distance");
        Check(bob.InView && bob.Measured && bob.DistanceText == "60 yd" && bob.Compass == "E", $"in view: this client's own position wins ({bob.DistanceText} {bob.Compass})");
        Check(zed.Measured && zed.DistanceText == "30 yd" && zed.Compass == "N" && !zed.InView, "from the status: 30 yd N");
        Check(v.SelectedId == 0x50000002, "the game's selection rides along");
        Check(v.Status == "3 other characters" && v.MyName == "Freya", "status line: " + v.Status);

        game.Portaling = true;
        CharactersView p = CharactersRows.Build(snap, game);
        Check(p.Rows[0].Location == "portal space" && !p.Rows[3].Measured, "in portal space nothing is measured");
    }

    private static void RowsOutOfWorld()
    {
        var game = new FakeGame { InWorld = false };
        CharactersView v = CharactersRows.Build(new NetCharSnapshot { Online = true, Version = "0.1.1" }, game);
        Check(v.Rows.Length == 0 && v.Status == "No other RynthCore clients with RynthNet on this PC.", "not in the world, alone: no rows, why");
        CharactersView off = CharactersRows.Build(new NetCharSnapshot { Online = false, Version = "0.1.1", Offline = "pipe busy" }, game);
        Check(off.Status == "RynthNet is offline: pipe busy", "offline says why: " + off.Status);
    }

    private static void Atlas()
    {
        AtlasNames.LoadForTests("""
        { "locations": [
          { "name": "Holtburg", "type": "Town", "cell": "A9B40019", "ns": 42.1, "ew": 33.6 },
          { "name": "Matron Hive East", "type": "Dungeon", "lb": "6146" },
          { "name": "Nameless", "type": "Dungeon", "lb": "zz" } ] }
        """);
        Check(AtlasNames.IsMapLandblock(0xA9B4) && !AtlasNames.IsMapLandblock(0x6146), "surface landblocks from places on the map");
        Check(AtlasNames.DungeonLabel(0x6146) == "Matron Hive East" && AtlasNames.DungeonLabel(0x0123) == "LB 0123", "dungeon names, else the landblock");

        // A surface building (indoor cell in a map landblock) counts as the overworld: a distance.
        var game = new FakeGame();
        var snap = new NetCharSnapshot { Online = true, Version = "0.1.1", MyName = "Freya" };
        snap.Clients.Add(Peer("Inside", 0x50000009, 0xA9B40105, 100, 160));
        snap.Clients.Add(Peer("Deep", 0x5000000A, 0x61460110, 10, 10));
        CharactersView v = CharactersRows.Build(snap, game);
        CharacterRow deep = v.Rows.First(r => r.Name == "Deep"), inside = v.Rows.First(r => r.Name == "Inside");
        Check(inside.Measured && inside.DistanceText == "60 yd", "a surface building is measured: " + inside.DistanceText);
        Check(!deep.Measured && deep.DistanceText == "elsewhere" && deep.Location == "Matron Hive East", "a dungeon: elsewhere, named");
    }

    private static void Manifest()
    {
        using JsonDocument doc = JsonDocument.Parse(CharactersManifest.Json);
        JsonElement w = doc.RootElement.GetProperty("windows")[0];
        Check(doc.RootElement.GetProperty("version").GetInt32() == 1, "manifest version 1");
        Check(w.GetProperty("id").GetString() == "characters" && w.GetProperty("title").GetString() == "Characters", "the window's id and title (the panel key players' saved places use)");
        Check(w.GetProperty("surface").GetString() == CharactersManifest.Surface && CharactersManifest.Surface == "window:characters", "its surface");
        Check(w.GetProperty("icon").GetString() == RynthIcons.UsersThree, "its bar icon: UsersThree");
        Check(w.GetProperty("bar").GetBoolean() && w.GetProperty("edgeToEdge").GetBoolean(), "a bar button; edge to edge");
        Check(w.GetProperty("width").GetInt32() == 420 && w.GetProperty("minWidth").GetInt32() == 280, "the old panel's sizes");
    }

    /// <summary>Shared\RynthUi\RynthIcons.cs is the engine's Baked set (Tools\gen-rynthui-icons.py writes it).</summary>
    private static void IconsMatchEngine()
    {
        string? dir = AppContext.BaseDirectory;
        string? engine = null;
        while (dir != null && engine == null)
        {
            string p = Path.Combine(dir, "RynthCore", "src", "RynthCore.Engine", "ImGui", "PhosphorIcons.cs");
            if (File.Exists(p)) engine = p;
            dir = Path.GetDirectoryName(dir);
        }
        if (engine == null) { Console.WriteLine("  (no sibling RynthCore: the icon list isn't compared)"); return; }
        string text = File.ReadAllText(engine);
        var consts = Regex.Matches(text, @"public const string (\w+) = ""\\u([0-9A-Fa-f]{4})"";")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
        string baked = Regex.Match(text, @"public const string Baked =(.*?);", RegexOptions.Singleline).Groups[1].Value;
        var names = Regex.Matches(baked, @"[A-Z]\w*").Select(m => m.Value).ToHashSet();
        var ours = typeof(RynthIcons).GetFields().ToDictionary(f => f.Name, f => ((int)((string)f.GetRawConstantValue()!)[0]).ToString("X4"));
        Check(ours.Count == names.Count && names.All(ours.ContainsKey), $"RynthIcons has the engine's {names.Count} baked icons (has {ours.Count}); run Tools\\gen-rynthui-icons.py");
        Check(names.All(n => consts.TryGetValue(n, out string? cp) && ours.TryGetValue(n, out string? o) && cp == o), "and the same code points");
    }
}
