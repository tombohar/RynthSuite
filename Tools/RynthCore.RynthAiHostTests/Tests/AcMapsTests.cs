using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.AcMaps;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// ACMAPS Maps (2026-11-26): the player's own ACMAPS dungeon map pack in game (RynthAiPlugin.AcMaps.cs,
// AcMaps/*). The index of a pack folder (images named by landblock, GoArrow's dungeons.txt, a GoArrow
// locations XML), the dungeon position <-> map pixel transform the player sets with "Place me here", the
// settings file, the image decode (Windows' WIC), the texture pieces, the window in the manifest and
// /ra acmaps. Every file these tests write is under the scratch folder; nothing in C:\Games is touched.
internal static class AcMapsTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Register(Runner r)
    {
        r.Add("acmaps: landblock from a map's file name (02CA, 0x02CA, 02CA0100, 02CA - Name)", FileNames);
        r.Add("acmaps: dungeon list lines (02CA;Name, commas, tabs, reversed, quotes, comments)", DungeonList);
        r.Add("acmaps: GoArrow locations XML (dungeon_id, retired, entities, entrance note)", LocationsXml);
        r.Add("acmaps: index from files: names from the list, name-only images, parts, problems", BuildIndex);
        r.Add("acmaps: scan a real folder (subfolders, other files skipped, missing folder)", ScanFolder);
        r.Add("acmaps: search maps by name or landblock", Search);
        r.Add("acmaps: one spot places a floor at GoArrow's scale, north up", OneSpot);
        r.Add("acmaps: two spots measure the scale; pixel <-> position round trip", TwoSpots);
        r.Add("acmaps: floors are separate; near spot moves; third spot replaces the nearer", Floors);
        r.Add("acmaps: a mirrored or absurd second spot is refused", Refused);
        r.Add("acmaps: clear floor, clear all, nudge scale, bad saved values", ClearAndNudge);
        r.Add("acmaps: settings file round trip (folder, announce, spots), bad JSON", SettingsJson);
        r.Add("acmaps: image decode size limits and texture pieces", SizesAndPieces);
        r.Add("acmaps: decode a GIF and a PNG with Windows' decoder", Decode);
        r.Add("acmaps: the chat line when a dungeon has a map", AnnounceLine);
        r.Add("acmaps: the window is in the manifest with no bar button", Manifest);
        r.Add("acmaps: /ra acmaps opens the window and says what's there; folder, scan, announce reply", Command);
    }

    private static string Dir(string name)
    {
        string d = Path.Combine(Program.TempRoot, "acmaps", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    // ── Index ────────────────────────────────────────────────────────────

    private static void FileNames()
    {
        void Yes(string name, ushort lb, string rest)
        {
            bool ok = AcMapIndex.TryLandblockFromFileName(name, out ushort got, out string r);
            Check.True(ok && got == lb && r == rest, $"'{name}' is {lb:X4} '{rest}' (got {ok} {got:X4} '{r}')");
        }
        void No(string name) => Check.False(AcMapIndex.TryLandblockFromFileName(name, out _, out _), $"'{name}' has no landblock");
        Yes("02CA", 0x02CA, "");
        Yes("02ca", 0x02CA, "");
        Yes("0x02CA", 0x02CA, "");
        Yes("02CA0100", 0x02CA, "");
        Yes("0x02CA0100", 0x02CA, "");
        Yes("02CA - Mosswart Maze", 0x02CA, "Mosswart Maze");
        Yes("02CA_Mosswart Maze", 0x02CA, "Mosswart Maze");
        Yes("02CA (Mosswart Maze)", 0x02CA, "Mosswart Maze");
        Yes("02CA_2", 0x02CA, "2");
        No("02CAB");
        No("02C");
        No("Mosswart Maze");
        No("0000");
        No("02CA01");
        Check.Eq(AcMapIndex.ParseLandblock("02CA"), (ushort)0x02CA, "4 hex");
        Check.Eq(AcMapIndex.ParseLandblock(" 0x02ca "), (ushort)0x02CA, "0x, lower case, spaces");
        Check.Eq(AcMapIndex.ParseLandblock("02CA0107"), (ushort)0x02CA, "a full cell id: its landblock");
        Check.Eq(AcMapIndex.ParseLandblock("2CA"), (ushort)0, "3 digits: not one");
        Check.Eq(AcMapIndex.ParseLandblock("zz12"), (ushort)0, "not hex");
        Check.Eq(AcMapIndex.NameKey("The Mosswart Maze!"), "themosswartmaze", "name key: letters and digits, lower case");
    }

    private static void DungeonList()
    {
        var names = new Dictionary<ushort, string>();
        int n = AcMapIndex.ParseDungeonList(
            "\uFEFF02CA;Mosswart Maze\r\n" +
            "# a comment\n" +
            "\n" +
            "0x01E3,Abandoned Mine\n" +
            "00AB\tTab Dungeon\n" +
            "Reversed Hall;0123\n" +
            "\"0456\";\"Quoted, Place\"\n" +
            "nothing here\n" +
            "0789;\n" +
            "0ABC;0DEF\n", names);
        Check.Eq(n, 5, "five dungeons: " + string.Join(", ", names.Select(k => $"{k.Key:X4}={k.Value}")));
        Check.Eq(names.GetValueOrDefault((ushort)0x02CA), "Mosswart Maze", "semicolon (GoArrow's dungeons.txt), BOM skipped");
        Check.Eq(names.GetValueOrDefault((ushort)0x01E3), "Abandoned Mine", "0x and a comma");
        Check.Eq(names.GetValueOrDefault((ushort)0x00AB), "Tab Dungeon", "a tab");
        Check.Eq(names.GetValueOrDefault((ushort)0x0123), "Reversed Hall", "name first");
        Check.Eq(names.GetValueOrDefault((ushort)0x0456), "Quoted, Place", "quotes, and the comma inside them kept (the semicolon is the separator)");
        Check.False(names.ContainsKey(0x0789), "no name: skipped");
        Check.False(names.ContainsKey(0x0ABC), "two ids, no name: skipped");
        // A later line for the same dungeon renames it, without counting twice.
        Check.Eq(AcMapIndex.ParseDungeonList("02CA;Mosswart Maze (new)", names), 0, "a known dungeon isn't counted again");
        Check.Eq(names[0x02CA], "Mosswart Maze (new)", "the later name wins");
    }

    private const string LocationsXmlSample =
        "<?xml version=\"1.0\"?>\n<locations>\n" +
        "<location>\n<id>1</id>\n<latitude>25.200</latitude>\n<longitude>19.400</longitude>\n" +
        "<restrictions>Level 10+</restrictions>\n<name>Mosswart Maze</name>\n<type>Dungeon</type>\n" +
        "<description>Quest(s): Mosswart &amp; friends &#8212; levers.</description>\n<dungeon_id>02CA</dungeon_id>\n<retired>N</retired>\n</location>\n" +
        "<location>\n<id>2</id>\n<latitude>-10.000</latitude>\n<longitude>-5.500</longitude>\n<restrictions></restrictions>\n" +
        "<name>North West Hole</name>\n<type>Dungeon</type>\n<description></description>\n<dungeon_id>0x0123</dungeon_id>\n</location>\n" +
        "<location>\n<id>3</id>\n<name>Old Gone Place</name>\n<type>Dungeon</type>\n<dungeon_id>0456</dungeon_id>\n<retired>Y</retired>\n</location>\n" +
        "<location>\n<id>4</id>\n<name>Some Shopkeeper</name>\n<type>NPC</type>\n<dungeon_id></dungeon_id>\n</location>\n" +
        "</locations>\n";

    private static void LocationsXml()
    {
        var names = new Dictionary<ushort, string>();
        var notes = new Dictionary<ushort, string>();
        int n = AcMapIndex.ParseLocationsXml(LocationsXmlSample, names, notes);
        Check.Eq(n, 2, "two dungeons (the retired one and the NPC skipped)");
        Check.Eq(names.GetValueOrDefault((ushort)0x02CA), "Mosswart Maze", "by dungeon_id");
        Check.Eq(names.GetValueOrDefault((ushort)0x0123), "North West Hole", "0x dungeon_id");
        Check.False(names.ContainsKey(0x0456), "retired Y: skipped");
        string note = notes.GetValueOrDefault((ushort)0x02CA) ?? "";
        Check.True(note.StartsWith("Entrance 25.2S, 19.4E.") && note.Contains("Level 10+.") && note.Contains("Mosswart & friends \u2014 levers."),
            "the note: entrance (south and east positive), restrictions, description with entities: " + note);
        Check.True((notes.GetValueOrDefault((ushort)0x0123) ?? "").StartsWith("Entrance 10.0N, 5.5W."), "north and west are negative in GoArrow's file");
        Check.True(AcMapIndex.TryCoord("-22.2", 'S', 'N', out string c) && c == "22.2N", "a coordinate: " + c);
        // A name the dungeon list gave is kept.
        var named = new Dictionary<ushort, string> { [0x02CA] = "From The List" };
        AcMapIndex.ParseLocationsXml(LocationsXmlSample, named, new Dictionary<ushort, string>());
        Check.Eq(named[0x02CA], "From The List", "the dungeon list's name wins");
    }

    private static AcMapIndex SampleIndex() => AcMapIndex.Build(@"C:\maps",
        new[]
        {
            "02CA.gif", "0x01E3.png", "Dead End.png", "Some Unknown Place.jpg", "packs\\0123_2.gif", "packs\\0123.gif",
            "dungeons.txt", "data_cod.xml", "readme.txt", "notes.doc",
        },
        rel => rel switch
        {
            "dungeons.txt" => "02CA;Mosswart Maze\n01E3;Abandoned Mine\nDEAD;Dead End\n",
            "data_cod.xml" => LocationsXmlSample,
            "readme.txt" => "These maps are ACMAPS'. Enjoy!",
            _ => null,
        });

    private static void BuildIndex()
    {
        AcMapIndex index = SampleIndex();
        Check.Eq(index.Maps.Count, 6, "six images: " + string.Join(", ", index.Maps.Select(m => m.RelativePath)));
        AcMapEntry? moss = index.ForLandblock(0x02CA).FirstOrDefault();
        Check.True(moss != null && moss.Name == "Mosswart Maze" && moss.Label == "Mosswart Maze (02CA)", "named from dungeons.txt: " + moss?.Label);
        Check.True(moss != null && moss.Notes.Contains("Entrance 25.2S"), "the locations file's note");
        Check.Eq(index.ForLandblock(0x01E3).FirstOrDefault()?.Name, "Abandoned Mine", "0x02CA-style name");
        Check.Eq(index.ForLandblock(0xDEAD).FirstOrDefault()?.RelativePath, "Dead End.png", "an image named by a listed dungeon's name (Dead End is DEAD in the list)");
        AcMapEntry unknown = index.Maps.First(m => m.RelativePath == "Some Unknown Place.jpg");
        Check.True(unknown.Landblock == 0 && unknown.Label == "Some Unknown Place", "an unknown name: shown, no landblock");
        IReadOnlyList<AcMapEntry> parts = index.ForLandblock(0x0123);
        Check.True(parts.Count == 2 && parts.All(p => p.Name == "North West Hole"), "two parts in a subfolder, named by the XML");
        Check.True(index.Has(0x02CA) && !index.Has(0x0456), "Has");
        Check.Eq(index.NameOf(0x0456), "", "a retired dungeon has no name");
        Check.True(index.IndexFiles.Contains("dungeons.txt") && index.IndexFiles.Contains("data_cod.xml") && !index.IndexFiles.Contains("readme.txt"),
            "index files: those that named something: " + string.Join(", ", index.IndexFiles));
        Check.Eq(index.Problems.Count, 0, "no problems: " + string.Join(" ", index.Problems));
        Check.True(index.Summary().StartsWith("6 maps, 4 dungeon names ("), "summary: " + index.Summary());

        AcMapIndex none = AcMapIndex.Build(@"C:\x", new[] { "dungeons.txt" }, _ => "02CA;Mosswart Maze");
        Check.True(none.Maps.Count == 0 && none.Problems.Count == 1 && none.Problems[0].StartsWith("No map images"), "no images: says so");
        AcMapIndex noList = AcMapIndex.Build(@"C:\x", new[] { "02CA.gif" }, _ => null);
        Check.True(noList.Maps.Count == 1 && noList.Maps[0].Label == "Dungeon 02CA" && noList.Problems.Any(p => p.Contains("dungeons.txt")),
            "no list: shown by landblock, and says so");
        AcMapIndex unreadable = AcMapIndex.Build(@"C:\x", new[] { "02CA.gif", "dungeons.txt" }, _ => null);
        Check.True(unreadable.Problems.Any(p => p.Contains("Couldn't read dungeons.txt")), "an unreadable list: says so");
    }

    private static void ScanFolder()
    {
        string d = Dir("scan");
        File.WriteAllBytes(Path.Combine(d, "02CA.gif"), TinyGif);
        Directory.CreateDirectory(Path.Combine(d, "more"));
        File.WriteAllBytes(Path.Combine(d, "more", "01E3.GIF"), TinyGif);
        File.WriteAllText(Path.Combine(d, "dungeons.txt"), "02CA;Mosswart Maze\n01E3;Abandoned Mine\n");
        File.WriteAllText(Path.Combine(d, "blacklist.xml"), "<blacklist></blacklist>");
        File.WriteAllText(Path.Combine(d, "thumbs.db"), "x");
        AcMapIndex index = AcMapIndex.Scan(d);
        Check.Eq(index.Maps.Count, 2, "two images, one in a subfolder: " + string.Join(", ", index.Maps.Select(m => m.RelativePath)));
        Check.True(index.Has(0x02CA) && index.Has(0x01E3), "both landblocks");
        Check.Eq(index.ForLandblock(0x01E3)[0].RelativePath, Path.Combine("more", "01E3.GIF"), "relative path kept, any case of extension");
        Check.Eq(index.Folder, Path.GetFullPath(d), "the folder, in full");
        Check.Eq(index.Problems.Count, 0, "no problems (GoArrow's blacklist.xml is harmless): " + string.Join(" ", index.Problems));

        AcMapIndex missing = AcMapIndex.Scan(Path.Combine(d, "not-here"));
        Check.True(missing.Maps.Count == 0 && missing.Problems.Count == 1 && missing.Problems[0].Contains("isn't there"), "a missing folder: says so");
        Check.True(AcMapIndex.Scan("").Problems.Count == 1, "no folder: says so");
    }

    private static void Search()
    {
        AcMapIndex index = SampleIndex();
        Check.Eq(AcMapIndex.Search(index.Maps, "").Count, index.Maps.Count, "no words: every map");
        Check.Eq(AcMapIndex.Search(index.Maps, "moss").Single().Landblock, (ushort)0x02CA, "a word of the name");
        Check.Eq(AcMapIndex.Search(index.Maps, "02ca").Single().Landblock, (ushort)0x02CA, "the landblock");
        Check.Eq(AcMapIndex.Search(index.Maps, "north hole").Count, 2, "words in any order (both parts)");
        Check.Eq(AcMapIndex.Search(index.Maps, "packs").Count, 2, "the folder name");
        Check.Eq(AcMapIndex.Search(index.Maps, "zzzz").Count, 0, "nothing");
    }

    // ── Calibration ──────────────────────────────────────────────────────

    private static void OneSpot()
    {
        var c = new AcMapCalibration();
        Check.False(c.TryToImage(10, 20, 0, out _), "nothing placed: no pixel");
        Check.Eq(c.Place(50, 60, -6, 400, 300), PlaceResult.FloorPlaced, "first spot places the floor");
        Check.True(c.TryToImage(50, 60, -6, out Vector2 p) && Near(p, new Vector2(400, 300)), "the spot itself is the clicked pixel: " + p);
        Check.True(c.TryToImage(60, 60, -6, out p) && Near(p, new Vector2(430, 300)), "10 east: 30 px right (GoArrow's 3 a unit): " + p);
        Check.True(c.TryToImage(50, 70, -6, out p) && Near(p, new Vector2(400, 270)), "10 north: 30 px up: " + p);
        Check.True(c.TryToImage(50, 60, -4, out _), "a little higher (a ramp): the same floor");
        Check.False(c.TryToImage(50, 60, -12, out _), "6 lower: another floor, not placed");
        Check.False(c.ScaleMeasuredOrSet, "the scale is still GoArrow's");
        Check.Eq(c.FloorCount(), 1, "one floor");
    }

    private static void TwoSpots()
    {
        var c = new AcMapCalibration();
        c.Place(10, 10, 0, 100, 500);
        // 40 east and 20 north at 2.5 px a unit: 100 px right, 50 px up.
        Check.Eq(c.Place(50, 30, 0, 200, 450), PlaceResult.ScaleMeasured, "a second spot far enough away measures the scale");
        Check.Near(c.Scale, 2.5, 1e-4, "the scale");
        Check.True(c.ScaleMeasuredOrSet, "measured");
        Check.True(c.TryToImage(10, 10, 0, out Vector2 a) && Near(a, new Vector2(100, 500)), "first spot: " + a);
        Check.True(c.TryToImage(50, 30, 0, out Vector2 b) && Near(b, new Vector2(200, 450)), "second spot: " + b);
        Check.True(c.TryToImage(30, 20, 0, out Vector2 m) && Near(m, new Vector2(150, 475)), "between them: " + m);
        Check.True(c.TryToWorld(new Vector2(150, 475), 0, out float x, out float y) && Math.Abs(x - 30) < 1e-3 && Math.Abs(y - 20) < 1e-3,
            $"pixel back to position: {x}, {y}");
        // A slightly off click: the floor sits between the two (mean), scale from the least-squares fit.
        var d = new AcMapCalibration();
        d.Place(0, 0, 0, 0, 0);
        d.Place(100, 0, 0, 302, 4);
        Check.True(d.Scale > 2.9f && d.Scale < 3.1f, "an off click still gives about the scale: " + d.Scale);
        Check.True(AcMapCalibration.TrySolveScale(new MapSpot(0, 0, 0, 0, 0), new MapSpot(0, 20, 0, 0, -60), out float s) && Math.Abs(s - 3) < 1e-4,
            "north is up the image (pixel y falls): " + s);
    }

    private static void Floors()
    {
        var c = new AcMapCalibration();
        c.Place(0, 0, 0, 100, 100);
        c.Place(0, 0, -6, 700, 100);   // the floor below, drawn to the right on the image
        Check.Eq(c.FloorCount(), 2, "two floors");
        Check.True(c.TryToImage(10, 0, 0, out Vector2 a) && Near(a, new Vector2(130, 100)), "top floor: " + a);
        Check.True(c.TryToImage(10, 0, -6, out Vector2 b) && Near(b, new Vector2(730, 100)), "lower floor has its own place: " + b);
        Check.Eq(c.Place(3, 0, 0, 112, 100), PlaceResult.Moved, "a spot near the floor's spot moves it");
        Check.Eq(c.FloorSpots(0).Count, 1, "still one spot on the floor");
        Check.True(c.TryToImage(3, 0, 0, out Vector2 moved) && Near(moved, new Vector2(112, 100)), "the moved spot: " + moved);
        c.Place(43, 0, 0, 232, 100);   // 40 east, 120 px: scale 3
        Check.Eq(c.FloorSpots(0).Count, 2, "two spots on the floor");
        // A third spot replaces the nearer of the two: (40 east of the first... near the second).
        Check.Eq(c.Place(40, 30, 0, 223, 10), PlaceResult.ScaleMeasured, "a third spot: replaces the nearer, re-measures with the farther");
        List<MapSpot> floor = c.FloorSpots(0);
        Check.True(floor.Count == 2 && floor.Any(s => s.X == 3) && floor.Any(s => s.X == 40), "kept the farther (x 3) and the new (x 40): " + string.Join(" ", floor));
        Check.True(c.TryToImage(0, 0, -6, out Vector2 lower) && Math.Abs(lower.X - 700) < 1e-3, "the other floor's spot untouched");
    }

    private static void Refused()
    {
        var c = new AcMapCalibration();
        c.Place(0, 0, 0, 100, 100);
        // Walked 20 east but clicked 60 px left: a mirrored map, or a wrong click.
        Check.Eq(c.Place(20, 0, 0, 40, 100), PlaceResult.ScaleRefused, "mirrored: refused");
        Check.Eq(c.Scale, AcMapCalibration.DefaultScale, "scale unchanged");
        List<MapSpot> floor = c.FloorSpots(0);
        Check.True(floor.Count == 1 && floor[0].X == 20, "the new spot alone places the floor");
        Check.False(AcMapCalibration.TrySolveScale(new MapSpot(0, 0, 0, 0, 0), new MapSpot(20, 0, 0, 20000, 0), out _), "absurd scale (1000): refused");
        Check.False(AcMapCalibration.TrySolveScale(new MapSpot(0, 0, 0, 0, 0), new MapSpot(20, 0, 0, 0, -60), out _), "90 degrees off: refused");
        Check.False(AcMapCalibration.TrySolveScale(new MapSpot(0, 0, 0, 0, 0), new MapSpot(5, 0, 0, 15, 0), out _), "too close: refused");
    }

    private static void ClearAndNudge()
    {
        var c = new AcMapCalibration();
        c.Place(0, 0, 0, 100, 100);
        c.Place(0, 0, -6, 300, 100);
        Check.True(c.ClearFloor(-6.5f), "clear the lower floor");
        Check.False(c.FloorPlaced(-6), "it's gone");
        Check.True(c.FloorPlaced(0), "the top floor stays");
        Check.False(c.ClearFloor(-30), "nothing to clear");
        c.NudgeScale(1.02f);
        Check.Near(c.Scale, 3.06, 1e-4, "nudged 2%");
        Check.True(c.ScaleMeasuredOrSet, "nudged counts as set");
        for (int i = 0; i < 500; i++) c.NudgeScale(1.5f);
        Check.Eq(c.Scale, AcMapCalibration.MaxScale, "kept in range");
        c.NudgeScale(float.NaN);
        Check.Eq(c.Scale, AcMapCalibration.MaxScale, "NaN ignored");
        c.ClearAll();
        Check.True(c.IsEmpty && c.Scale == AcMapCalibration.DefaultScale && !c.ScaleMeasuredOrSet, "clear all: back to the start");
        var bad = new AcMapCalibration(-5f, true, new[] { new MapSpot(float.NaN, 0, 0, 0, 0), new MapSpot(1, 2, 3, 4, 5) });
        Check.True(bad.Scale == AcMapCalibration.DefaultScale && !bad.ScaleMeasuredOrSet && bad.Spots.Count == 1, "a bad saved scale and spot are dropped");
    }

    // ── Settings ─────────────────────────────────────────────────────────

    private static void SettingsJson()
    {
        var s = new AcMapSettings { Folder = @"D:\AC\Dungeon Map Cache", Announce = true };
        s.For("02CA.gif").Place(10, 10, 0, 100, 500);
        s.For("02CA.gif").Place(50, 30, 0, 200, 450);
        s.For("packs\\0123.gif").Place(1, 2, -6, 3, 4);
        s.For("empty.gif");   // nothing set: not written
        string json = s.ToJson();
        AcMapSettings back = AcMapSettings.FromJson(json);
        Check.Eq(back.Folder, @"D:\AC\Dungeon Map Cache", "folder");
        Check.True(back.Announce, "announce");
        Check.Eq(back.Maps.Count, 2, "two maps with spots (the empty one isn't written): " + string.Join(", ", back.Maps.Keys));
        AcMapCalibration c = back.For("02ca.GIF");
        Check.True(c.Spots.Count == 2 && Math.Abs(c.Scale - 2.5f) < 1e-3 && c.ScaleMeasuredOrSet, "spots and scale, any case of the key");
        Check.True(c.TryToImage(30, 20, 0, out Vector2 m) && Near(m, new Vector2(150, 475)), "the same transform after loading: " + m);
        Check.Eq(back.For("packs\\0123.gif").Spots[0], new MapSpot(1, 2, -6, 3, 4), "a spot's numbers");

        AcMapSettings junk = AcMapSettings.FromJson("{ not json");
        Check.True(junk.Folder == "" && !junk.Announce && junk.Maps.Count == 0, "bad JSON: defaults");
        AcMapSettings partial = AcMapSettings.FromJson("{\"announce\": \"yes\", \"maps\": {\"a.gif\": {\"spots\": [[1,2,3], [1,2,3,4,\"x\"], [1,2,3,4,5]]}}}");
        Check.True(!partial.Announce && partial.For("a.gif").Spots.Count == 1, "bad values skipped, good ones kept");
        Check.True(new AcMapSettings().FolderOrDefault.EndsWith(Path.Combine("RynthAi", "ACMaps")), "no folder: RynthSuite's own ACMaps folder");

        string path = Path.Combine(Dir("settings"), "acmaps.json");
        Check.Eq(AcMapSettings.Save(path, json), "", "saved");
        Check.False(File.Exists(path + ".tmp"), "no temp file left");
        Check.Eq(AcMapSettings.Load(path).Folder, @"D:\AC\Dungeon Map Cache", "loaded from disk");
        Check.Eq(AcMapSettings.Load(path + ".missing").Maps.Count, 0, "a missing file: defaults");
    }

    // ── Image and textures ───────────────────────────────────────────────

    // A 1 x 1 GIF whose one pixel is palette colour 0, red.
    private static readonly byte[] TinyGif =
    {
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    };

    private static void SizesAndPieces()
    {
        Check.Eq(WicImage.FitSize(1200, 900), (1200, 900), "a usual map: as it is");
        Check.Eq(WicImage.FitSize(8192, 2048), (4096, 1024), "too wide: halved, aspect kept");
        (int w, int h) = WicImage.FitSize(4000, 4000);
        Check.True(w == h && (long)w * h <= WicImage.MaxPixels && w > 2800, $"too many pixels: down to {w} x {h}");
        Check.Eq(WicImage.FitSize(0, 10), (0, 0), "empty");
        Check.Eq(AcMapTexture.Pow2(1), 1, "pow2 1");
        Check.Eq(AcMapTexture.Pow2(512), 512, "pow2 512");
        Check.Eq(AcMapTexture.Pow2(513), 1024, "pow2 513");
        var img = new DecodedImage { Width = 1200, Height = 700, SourceWidth = 2400, SourceHeight = 1400, Pixels = new uint[1200 * 700] };
        Check.Near(img.Factor, 0.5, 1e-6, "a halved decode's factor");
        var tex = new AcMapTexture(img);
        Check.Eq(tex.PieceCount, 6, "1200 x 700 in 512 pieces: 3 x 2");
        AcMapTexture.Piece last = tex.Pieces[^1];
        Check.True(last.X == 1024 && last.Y == 512 && last.W == 176 && last.H == 188, $"the corner piece: {last.X},{last.Y} {last.W}x{last.H}");
        Check.True(Math.Abs(last.Uv1.X - 176f / 256f) < 1e-6 && Math.Abs(last.Uv1.Y - 188f / 256f) < 1e-6, "its UVs use part of a 256 texture: " + last.Uv1);
        Check.False(tex.Complete, "nothing made without a device");
        tex.Upload(IntPtr.Zero);
        Check.False(tex.Complete, "no device: still nothing");
    }

    private static void Decode()
    {
        string d = Dir("decode");
        string gif = Path.Combine(d, "02CA.gif");
        File.WriteAllBytes(gif, TinyGif);
        DecodedImage? g = WicImage.Load(gif, out string error);
        Check.True(g != null && g.Width == 1 && g.Height == 1 && g.SourceWidth == 1, "the GIF decodes: " + error);
        Check.Eq(g?.Pixels[0] ?? 0, 0xFFFF0000u, "its pixel is opaque red (BGRA as A8R8G8B8)");

        string png = Path.Combine(d, "0123.png");
        File.WriteAllBytes(png, Png(3, 2, new uint[] { 0xFF0000FF, 0xFF00FF00, 0xFFFF0000, 0x80FFFFFF, 0xFF000000, 0x00000000 }));
        DecodedImage? p = WicImage.Load(png, out error);
        Check.True(p != null && p.Width == 3 && p.Height == 2, "the PNG decodes: " + error);
        if (p != null)
        {
            Check.Eq(p.Pixels[0], 0xFF0000FFu, "blue, top left");
            Check.Eq(p.Pixels[2], 0xFFFF0000u, "red, top right");
            Check.Eq(p.Pixels[3], 0x80FFFFFFu, "half-clear white, bottom left (straight alpha)");
        }

        string notImage = Path.Combine(d, "dungeons.gif");
        File.WriteAllText(notImage, "02CA;Mosswart Maze");
        Check.Null(WicImage.Load(notImage, out error), "a text file named .gif: null");
        Check.True(error.Length > 0, "and why: " + error);
        Check.Null(WicImage.Load(Path.Combine(d, "missing.png"), out error), "a missing file: null");

        // The async path hands the result over on its own thread.
        var done = new TaskCompletionSource<(DecodedImage?, string)>();
        WicImage.DecodeAsync(gif, (img, err) => done.TrySetResult((img, err)));
        Check.True(done.Task.Wait(10000) && done.Task.Result.Item1?.Width == 1, "DecodeAsync");
    }

    /// <summary>A PNG of ARGB pixels (8-bit RGBA, no filter).</summary>
    private static byte[] Png(int w, int h, uint[] argb)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                uint c = argb[y * w + x];
                raw.WriteByte((byte)(c >> 16)); raw.WriteByte((byte)(c >> 8)); raw.WriteByte((byte)c); raw.WriteByte((byte)(c >> 24));
            }
        }
        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw.ToArray());
            idat = z.ToArray();
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        Be(ihdr, 0, (uint)w); Be(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, (uint)data.Length); s.Write(len);
        byte[] t = Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        var crc = new byte[4]; Be(crc, 0, Crc(t.Concat(data).ToArray())); s.Write(crc);
    }

    private static void Be(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    private static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    // ── Chat line, manifest, command ─────────────────────────────────────

    private static void AnnounceLine()
    {
        AcMapIndex index = SampleIndex();
        string? line = RynthAiPlugin.AcAnnounceLine(index, 0x02CA);
        Check.True(line != null && line.Contains("Mosswart Maze") && line.Contains("Map (right-click)") && line.Contains("/ra acmaps"), "a map: the line says where: " + line);
        Check.Null(RynthAiPlugin.AcAnnounceLine(index, 0x0456), "no map: no line");
        Check.Null(RynthAiPlugin.AcAnnounceLine(index, 0), "outdoors: no line");
        AcMapIndex noList = AcMapIndex.Build(@"C:\x", new[] { "02CA.gif" }, _ => null);
        Check.True(RynthAiPlugin.AcAnnounceLine(noList, 0x02CA)?.Contains("Dungeon 02CA") == true, "no name: by landblock");
    }

    private static void Manifest()
    {
        string json = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, acMaps: true);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, "", "parses");
        SectionWindowDecl? w = m.Windows.FirstOrDefault(x => x.Title == RynthAiPlugin.AcMapsTitle);
        Check.True(w != null && w.Surface == RynthAiPlugin.AcMapsSurface && !w.Bar, "the ACMAPS Maps window, no bar button");
        string without = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections);
        Check.False(without.Contains(RynthAiPlugin.AcMapsSurface), "not declared without plugin windows");
    }

    private static readonly List<(string Key, int Mode)> PanelCalls = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint FakeUiColor(int token) => 0xFFFFFFFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr FakeUiFont(int font) => IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int FakeSetPanelOpen(byte* key, int mode)
    {
        PanelCalls.Add((Marshal.PtrToStringUTF8((IntPtr)key) ?? "", mode));
        return mode == 0 ? 0 : 1;
    }

    private static unsafe void PluginWindows(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 89u);
        api.GetUiColorFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, uint>)&FakeUiColor;
        api.GetUiFontFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr>)&FakeUiFont;
        api.SetPanelOpenFn = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&FakeSetPanelOpen;
    }

    private static void Command()
    {
        FakeHost.Reset();
        PanelCalls.Clear();
        FakeHost.ConfigureApi = PluginWindows;
        FakeHost.Create(playerId: 0x50000F41);
        var p = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        void Set(string f, object v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(p, v);
        Set("_playerId", 0x50000F41u);
        Set("_initialized", true);
        Set("_loginComplete", true);

        // Settings and maps in scratch: never the real RynthSuite folder.
        string root = Dir("command");
        string maps = Path.Combine(root, "maps");
        Directory.CreateDirectory(maps);
        File.WriteAllBytes(Path.Combine(maps, "02CA.gif"), TinyGif);
        File.WriteAllText(Path.Combine(maps, "dungeons.txt"), "02CA;Mosswart Maze\n");
        p.AcMapsSettingsPath = Path.Combine(root, "acmaps.json");
        p.AcSettings.Folder = maps;
        p.StartAcScan().Wait(10000);
        Check.Eq(p.AcMapsIndexForTests.Maps.Count, 1, "the scratch folder is read");

        List<string> Typed(string line)
        {
            int before = FakeHost.Chat.Count;
            int eat = 0;
            p.OnChatBarEnter(line, ref eat);
            return FakeHost.Chat.Skip(before).ToList();
        }
        string Show(List<string> l) => string.Join(" | ", l);

        // In the dungeon (an indoor cell of 02CA).
        FakeHost.PlayerPose = (0x02CA0105u, 30f, -40f, -6f);
        List<string> lines = Typed("/ra acmaps");
        Check.True(lines.Count == 1 && lines[0].Contains("1 map in " + maps) && lines[0].Contains("this dungeon (Mosswart Maze, 02CA) has one")
            && lines[0].Contains("ACMAPS Maps open"), "opens and says what's there: " + Show(lines));
        Check.True(PanelCalls.Count > 0 && PanelCalls[^1] == (RynthAiPlugin.AcMapsTitle, (int)RynthPanelOpen.Open), "opened by its panel key");

        FakeHost.PlayerPose = (0x01E30105u, 30f, -40f, -6f);
        lines = Typed("/ra acmaps");
        Check.True(lines.Count == 1 && lines[0].Contains("none for this dungeon (01E3)"), "a dungeon without a map: says so: " + Show(lines));

        lines = Typed("/ra acmaps folder relative\\path");
        Check.True(lines.Count == 1 && lines[0].Contains("isn't a full folder path") && lines[0].Contains(RynthAiPlugin.AcMapsUsage), "a relative path: refused: " + Show(lines));
        lines = Typed("/ra acmaps folder " + Path.Combine(root, "nope"));
        Check.True(lines.Count == 1 && lines[0].Contains("there's no folder"), "a missing folder: refused: " + Show(lines));
        lines = Typed("/ra acmaps folder");
        Check.True(lines.Count == 1 && lines[0].Contains(maps), "no path: says the folder: " + Show(lines));

        string other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        lines = Typed("/ra acmaps folder " + other);
        Check.True(lines.Count == 1 && lines[0].Contains("folder set to " + other), "folder set: " + Show(lines));
        Check.Eq(p.AcSettings.Folder, other, "the setting changed");
        lines = Typed("/ra acmaps folder " + maps);
        Check.True(lines.Count == 1 && lines[0].Contains("folder set"), "back: " + Show(lines));

        lines = Typed("/ra acmaps scan");
        Check.True(lines.Count == 1 && lines[0].Contains("reading " + maps + " again"), "scan: " + Show(lines));

        lines = Typed("/ra acmaps announce on");
        Check.True(lines.Count == 1 && lines[0].Contains("ON"), "announce on: " + Show(lines));
        Check.True(p.AcSettings.Announce, "the setting is on");
        lines = Typed("/ra acmaps announce");
        Check.True(lines.Count == 1 && lines[0].Contains("OFF") && !p.AcSettings.Announce, "announce alone: toggles: " + Show(lines));
        lines = Typed("/ra acmaps announce maybe");
        Check.True(lines.Count == 1 && lines[0].Contains("on or off"), "announce maybe: refused: " + Show(lines));

        lines = Typed("/ra acmaps hide");
        Check.True(lines.Count == 1 && lines[0].Contains("closed") && PanelCalls[^1].Mode == (int)RynthPanelOpen.Close, "hide: " + Show(lines));
        lines = Typed("/ra acmaps wibble");
        Check.True(lines.Count == 1 && lines[0].Contains("unknown option 'wibble'") && lines[0].Contains(RynthAiPlugin.AcMapsUsage), "unknown: " + Show(lines));
        lines = Typed("/ra acmaps ?");
        Check.True(lines.Count == 2 && lines.All(l => l.Contains("/ra acmaps")), "? says the usage: " + Show(lines));
        lines = Typed("/ra help");
        Check.True(lines.Any(l => l.Contains("/ra acmaps")), "/ra help lists it");

        // The settings were written to scratch, and only there.
        System.Threading.Thread.Sleep(800);
        Check.True(File.Exists(p.AcMapsSettingsPath), "settings saved in scratch");
        Check.Eq(AcMapSettings.Load(p.AcMapsSettingsPath).Folder, maps, "the saved folder");

        // The chat line: announce on, walk into the dungeon (the scans above finished first).
        p.StartAcScan().Wait(10000);
        Typed("/ra acmaps announce on");
        FakeHost.PlayerPose = (0x02CA0105u, 30f, -40f, -6f);
        int chatBefore = FakeHost.Chat.Count;
        typeof(RynthAiPlugin).GetMethod("TickAcMaps", Any)!.Invoke(p, null);
        typeof(RynthAiPlugin).GetMethod("TickAcMaps", Any)!.Invoke(p, null);
        List<string> said = FakeHost.Chat.Skip(chatBefore).ToList();
        Check.True(said.Count == 1 && said[0].Contains("Mosswart Maze"), "one line on entering, not every tick: " + Show(said));
        FakeHost.PlayerPose = (0x02CA0031u, 30f, -40f, -6f);   // an outdoor cell: left
        typeof(RynthAiPlugin).GetMethod("TickAcMaps", Any)!.Invoke(p, null);
        FakeHost.PlayerPose = (0x02CA0105u, 30f, -40f, -6f);
        typeof(RynthAiPlugin).GetMethod("TickAcMaps", Any)!.Invoke(p, null);
        Check.Eq(FakeHost.Chat.Skip(chatBefore).Count(), 2, "again after leaving and coming back");
        Typed("/ra acmaps announce off");
        FakeHost.Reset();
    }

    private static bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < 1e-3f;
}
