using RynthCore.Plugin.RynthNet.Characters;

namespace RynthNet.Tests;

/// <summary>
/// RynthNet's Characters window (moved from RynthCore 2026-10-21): the model (snapshot parsing,
/// where another character is from you, what it is doing, the row order; these checks were
/// RynthCore's tools/CharactersPanelTests), the rows built from a fake game, the Atlas names,
/// the manifest RynthCore hosts the window from, and the icon list against the engine's.
/// </summary>
internal static partial class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        ParsesCurrentSnapshot();
        ParsesOlderEntries();
        RejectsBadJson();
        DistanceSameLandblock();
        DistanceAcrossLandblocks();
        DungeonRules();
        SurfaceBuildings();
        CoordsText();
        CompassPoints();
        States();
        Stale();
        StableOrder();
        Vitals();
        WindowChecks();
        Replies();
        LookChecks();

        Console.WriteLine($"{_checks} checks, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("FAIL: " + what);
    }

    private static bool Near(double a, double b, double tol = 0.01) => Math.Abs(a - b) <= tol;

    // RynthNet 0.1.1's shape: id and cmode in the status, RynthAi's bot object, age after status.
    private const string Current = """
    {"version":"0.1.1","online":true,"node":"n1","char":"Drakkon","world":"Aelrynth","login":true,"tags":["main"],
     "acceptRemote":true,"clients":[
      {"node":"n2","pid":4242,"char":"Zed Healer","world":"Aelrynth","login":true,"tags":["heal","box"],"rtt":3,
       "status":{"hp":300,"hpMax":400,"st":150,"stMax":200,"mp":50,"mpMax":500,"cell":2847146009,"x":40.5,"y":50.25,"z":12.0,
                 "ns":42.1,"ew":33.3,"heading":90.0,"portal":false,"busy":0,"vitae":1.0,"id":1342177281,"cmode":2,
                 "bot":{"macro":true,"action":"Combat","state":"Default","combat":true}},"age":420},
      {"node":"n3","pid":5151,"char":"Alpha","world":"Aelrynth","login":true,"tags":[],"rtt":2,"status":null}
     ]}
    """;

    private static void ParsesCurrentSnapshot()
    {
        NetCharSnapshot? s = CharactersModel.Parse(Current);
        Check(s != null, "current snapshot parses");
        if (s == null) return;
        Check(s.Online && s.Version == "0.1.1" && s.MyName == "Drakkon" && s.MyWorld == "Aelrynth", "snapshot header");
        Check(s.Clients.Count == 2, "two clients");
        NetCharEntry z = s.Clients[0];
        Check(z.Name == "Zed Healer" && z.Pid == 4242 && z.World == "Aelrynth" && z.LoggedIn, "client identity");
        Check(z.Tags.Length == 2 && z.Tags[0] == "heal", "client tags");
        Check(z.HasStatus && z.AgeMs == 420, "status age");
        Check(z.HasVitals && z.Hp == 300 && z.HpMax == 400 && z.St == 150 && z.StMax == 200 && z.Mp == 50 && z.MpMax == 500, "vitals");
        Check(z.HasPose && z.Cell == 2847146009u && Near(z.X, 40.5) && Near(z.Y, 50.25) && Near(z.Z, 12), "pose");
        Check(z.HasCoords && Near(z.Ns, 42.1) && Near(z.Ew, 33.3), "coords");
        Check(z.Id == 1342177281u && z.CombatMode == 2, "id and cmode (0.1.1)");
        Check(z.HasBot && z.Macro && z.Action == "Combat", "bot state");
        NetCharEntry a = s.Clients[1];
        Check(a.LoggedIn && !a.HasStatus && !a.HasVitals && !a.HasPose && a.AgeMs == -1, "null status");
        Check(CharactersModel.State(a).Kind == CharStateKind.NoStatus, "null status reads 'no status yet'");
    }

    private static void ParsesOlderEntries()
    {
        // RynthNet 0.1.0 (no id / cmode), a client without RynthAi (no bot), one at character
        // select (no char, status {} sent as null), and an entry with odd types.
        const string old = """
        {"online":true,"char":"Me","world":"W","clients":[
          {"node":"a","pid":1,"char":"Old","world":"W","login":true,"tags":[],"rtt":-1,
           "status":{"hp":10,"hpMax":20,"st":5,"stMax":5,"mp":0,"mpMax":0,"cell":16777217,"x":1,"y":2,"z":3},"age":100},
          {"node":"b","pid":2,"char":"","world":"","login":false,"tags":[],"rtt":5,"status":null},
          {"node":"c","pid":3,"char":"Odd","login":true,"tags":"notarray","status":{"hp":"lots","cell":0}}
        ]}
        """;
        NetCharSnapshot? s = CharactersModel.Parse(old);
        Check(s != null && s.Clients.Count == 3, "old snapshot parses");
        if (s == null || s.Clients.Count != 3) return;
        NetCharEntry o = s.Clients[0];
        Check(o.HasVitals && o.Hp == 10 && o.HpMax == 20, "old vitals");
        Check(o.Id == 0 && o.CombatMode == 0 && !o.HasBot, "old entry: no id, no cmode, no bot");
        Check(o.HasPose && o.Cell == 0x01000001u, "old pose");
        Check(!o.HasCoords, "no ns/ew -> no coords");
        Check(CharactersModel.State(o) == ("idle", CharStateKind.Idle), "no bot, peace -> idle");
        NetCharEntry b = s.Clients[1];
        Check(!b.LoggedIn && CharactersModel.State(b).Kind == CharStateKind.LoggedOut, "character select -> logged out");
        NetCharEntry c = s.Clients[2];
        Check(c.Tags.Length == 0 && !c.HasVitals && !c.HasPose && c.World == "", "wrong types are ignored, cell 0 is no pose");
        Check(CharactersModel.Parse("{\"online\":false,\"clients\":[]}") is { Online: false, Clients.Count: 0 }, "RynthNet's fallback string");
        Check(CharactersModel.Parse("{\"online\":false,\"offline\":\"pipe busy\",\"version\":\"0.1.1\"}") is { Offline: "pipe busy" }, "offline reason");
    }

    private static void RejectsBadJson()
    {
        Check(CharactersModel.Parse(null) == null, "null");
        Check(CharactersModel.Parse("") == null, "empty");
        Check(CharactersModel.Parse("{\"clients\":[") == null, "truncated");
        Check(CharactersModel.Parse("[1,2]") == null, "array root");
    }

    private const uint Outdoor = 0xA9B40001;   // landblock A9B4, outdoor cell 1

    private static void DistanceSameLandblock()
    {
        CharPlace p = CharactersModel.Locate(Outdoor, 10, 10, 0, Outdoor, 40, 50, 0, null, null);
        Check(p.Measured && Near(p.Distance, 50), "3-4-5 distance");
        Check(Near(p.Bearing, 36.87, 0.05) && p.Compass == "NE", "bearing NE");
        Check(p.DistanceText == "50 yd", "distance text");
        Check(p.Location == CharactersModel.Coords(Outdoor, 40, 50), "outdoor location is map coords");
        CharPlace up = CharactersModel.Locate(Outdoor, 10, 10, 0, Outdoor, 10, 10, 20, null, null);
        Check(up.Measured && Near(up.Distance, 20) && up.Compass == "here", "straight above: here, 3D distance");
    }

    private static void DistanceAcrossLandblocks()
    {
        CharPlace east = CharactersModel.Locate(Outdoor, 10, 10, 0, 0xAAB40001, 10, 10, 0, null, null);
        Check(east.Measured && Near(east.Distance, 192) && east.Compass == "E", "one landblock east");
        CharPlace north = CharactersModel.Locate(Outdoor, 10, 10, 0, 0xA9B50001, 10, 10, 0, null, null);
        Check(north.Measured && Near(north.Distance, 192) && north.Compass == "N", "one landblock north");
        CharPlace sw = CharactersModel.Locate(Outdoor, 100, 100, 0, 0xA8B30001, 100, 100, 0, null, null);
        Check(sw.Measured && Near(sw.Distance, 192 * Math.Sqrt(2), 0.1) && sw.Compass == "SW", "diagonal south-west");
        CharPlace far = CharactersModel.Locate(Outdoor, 0, 0, 0, 0x10100001, 0, 0, 0, null, null);
        Check(far.Measured && far.Distance > 10000, "far across the map is still a distance");
    }

    private static void DungeonRules()
    {
        Func<uint, string> names = lb => lb == 0x01D9 ? "Hive" : "";
        const uint dungeonA = 0x01D90105, dungeonA2 = 0x01D90142, dungeonB = 0x02AB0110;
        CharPlace same = CharactersModel.Locate(dungeonA, 10, 10, -6, dungeonA2, 10, 30, -6, null, names);
        Check(same.Measured && Near(same.Distance, 20) && same.Compass == "N", "same dungeon landblock: measured");
        Check(same.Location == "Hive", "dungeon location is its name");
        CharPlace fromOutside = CharactersModel.Locate(Outdoor, 10, 10, 0, dungeonA, 10, 10, 0, null, names);
        Check(!fromOutside.Measured && fromOutside.Location == "Hive" && fromOutside.DistanceText == "", "outdoors -> dungeon: elsewhere");
        Check(float.IsPositiveInfinity(fromOutside.Distance) && float.IsNaN(fromOutside.Bearing), "elsewhere has no distance or bearing");
        CharPlace other = CharactersModel.Locate(dungeonA, 0, 0, 0, dungeonB, 0, 0, 0, null, names);
        Check(!other.Measured && other.Location == "dungeon LB 02AB", "another, unnamed dungeon");
        CharPlace outFromDungeon = CharactersModel.Locate(dungeonA, 0, 0, 0, Outdoor, 12, 12, 0, null, names);
        Check(!outFromDungeon.Measured && outFromDungeon.Location == "42.1N 33.3E", "dungeon -> outdoors: elsewhere with coords");
        CharPlace noPose = CharactersModel.Locate(0, 0, 0, 0, Outdoor, 12, 12, 0, null, null);
        Check(!noPose.Measured && noPose.Location == "42.1N 33.3E", "no own pose: location only");
    }

    private static void SurfaceBuildings()
    {
        // An indoor cell of a landblock the caller knows is overworld (a cottage): same space as outdoors.
        const uint cottage = 0xAAB40102;
        Func<uint, bool> over = lb => lb == 0xAAB4;
        CharPlace p = CharactersModel.Locate(Outdoor, 10, 10, 0, cottage, 10, 10, 0, over, null);
        Check(p.Measured && Near(p.Distance, 192) && p.Compass == "E", "surface building next door: measured");
        Check(p.Location == CharactersModel.Coords(cottage, 10, 10), "surface building location is coords");
        CharPlace q = CharactersModel.Locate(Outdoor, 10, 10, 0, cottage, 10, 10, 0, null, null);
        Check(!q.Measured, "unknown indoor landblock counts as a dungeon");
        CharPlace r = CharactersModel.Locate(cottage, 10, 10, 0, Outdoor, 10, 10, 0, over, null);
        Check(r.Measured && r.Compass == "W", "from inside the building out");
    }

    private static void CoordsText()
    {
        Check(CharactersModel.Coords(Outdoor, 12, 12) == "42.1N 33.3E", "Coords NE quadrant");
        Check(CharactersModel.Coords(0x10100001, 12, 12) == "89.1S 89.1W", "Coords SW quadrant");
        Check(CharactersModel.CoordsText(-0.04, 0.04) == "0.0S 0.0E", "near zero keeps its sign letter");
    }

    private static void CompassPoints()
    {
        (float b, string c)[] cases =
        {
            (0, "N"), (22.4f, "N"), (22.6f, "NE"), (44, "NE"), (90, "E"), (135, "SE"), (180, "S"),
            (225, "SW"), (270, "W"), (315, "NW"), (337.4f, "NW"), (337.6f, "N"), (359, "N"), (-90, "W"), (450, "E"),
        };
        foreach (var (b, c) in cases)
            Check(CharactersModel.Compass(b) == c, $"compass {b} -> {c} (got {CharactersModel.Compass(b)})");
    }

    private static NetCharEntry Entry(string name = "A", bool macro = false, string action = "", int cmode = 0, uint hp = 10,
        bool bot = true, bool portal = false) => new()
    {
        Name = name, LoggedIn = true, HasStatus = true, AgeMs = 100,
        HasVitals = true, Hp = hp, HpMax = 20, St = 1, StMax = 1, Mp = 1, MpMax = 1,
        HasBot = bot, Macro = macro, Action = action, CombatMode = cmode, Portal = portal,
    };

    private static void States()
    {
        Check(CharactersModel.State(Entry(hp: 0, macro: true)) == ("dead", CharStateKind.Dead), "hp 0 -> dead (before botting)");
        Check(CharactersModel.State(Entry(portal: true, macro: true)) == ("portal space", CharStateKind.Portal), "portal space");
        Check(CharactersModel.State(Entry(macro: true, action: "Combat")) == ("botting: fighting", CharStateKind.Fighting), "fighting");
        Check(CharactersModel.State(Entry(macro: true, action: "Navigating")) == ("botting: navigating", CharStateKind.Botting), "navigating");
        Check(CharactersModel.State(Entry(macro: true, action: "Default")) == ("botting", CharStateKind.Botting), "botting, default");
        Check(CharactersModel.State(Entry(macro: false, cmode: 2)) == ("idle, melee", CharStateKind.CombatMode), "macro off, melee stance");
        Check(CharactersModel.State(Entry(macro: false, cmode: 8)) == ("idle, magic", CharStateKind.CombatMode), "macro off, magic stance");
        Check(CharactersModel.State(Entry(macro: false, cmode: 1)) == ("idle (macro off)", CharStateKind.Idle), "macro off, peace");
        Check(CharactersModel.State(Entry(bot: false)) == ("idle", CharStateKind.Idle), "no RynthAi");
        var maxZero = Entry(hp: 0);
        maxZero.HpMax = 0;
        Check(CharactersModel.State(maxZero).Kind != CharStateKind.Dead, "0/0 vitals (not known yet) isn't dead");
    }

    private static void Stale()
    {
        var e = Entry();
        e.AgeMs = 1000;
        Check(!CharactersModel.IsStale(e), "1 s old: fresh");
        e.AgeMs = 6000;
        Check(CharactersModel.IsStale(e), "6 s old: stale");
        e.LoggedIn = false;
        Check(!CharactersModel.IsStale(e), "logged out: not stale");
    }

    private static void StableOrder()
    {
        var zed = Entry("zed"); zed.Cell = Outdoor; zed.X = 1;
        var alpha = Entry("Alpha"); alpha.Cell = Outdoor; alpha.X = 100;
        var bob = Entry("bob"); bob.Cell = 0x01D90105;
        var nobody = new NetCharEntry { Name = "", LoggedIn = false, Pid = 7 };
        var nobody2 = new NetCharEntry { Name = "", LoggedIn = false, Pid = 3 };
        var list = new List<NetCharEntry> { nobody, zed, bob, nobody2, alpha };
        list.Sort(CharactersModel.CompareRows);
        Check(string.Join(",", list.Select(e => e.Name.Length > 0 ? e.Name : "pid" + e.Pid)) == "Alpha,bob,zed,pid3,pid7",
            "alphabetical (case-insensitive), clients without a character last by pid");
        // Moving characters around never changes the order.
        alpha.X = 0; zed.X = 500; bob.Cell = Outdoor;
        list.Sort(CharactersModel.CompareRows);
        Check(list[0] == alpha && list[1] == bob && list[2] == zed, "positions don't reorder rows");
        // A new character slots in; a gone one just disappears.
        list.Add(Entry("Carl"));
        list.Remove(bob);
        list.Sort(CharactersModel.CompareRows);
        Check(string.Join(",", list.Take(3).Select(e => e.Name)) == "Alpha,Carl,zed", "new character slots in alphabetically");
    }

    private static void Vitals()
    {
        Check(CharactersModel.VitalText(300, 400) == "300/400", "vital text");
        Check(Near(CharactersModel.Fraction(300, 400), 0.75), "fraction");
        Check(CharactersModel.Fraction(5, 0) == 0 && CharactersModel.Fraction(500, 400) == 1, "fraction clamps");
    }
}
