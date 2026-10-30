using System.Text.Json;
using RynthCore.Plugin.RynthVision;
using RynthCore.Plugin.RynthVision.Light;
using RynthCore.Plugin.RynthVision.Ui;

namespace RynthVision.Tests;

/// <summary>
/// RynthVision's dungeon light (2026-10-27): where it lights, how brightly, its chat words and status
/// line, its settings keys; and the Vision window RynthVision draws in the engine's Vision panel's
/// place (its manifest, its pure rules, the settings it edits and saves).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    // An outdoor cell (Holtburg), a building room and a dungeon cell (ids only; the flag is the test's).
    private const uint Outdoor = 0xA9B40019, Room = 0xA9B40105, DungeonCell = 0x01D9012A;

    private static int Main()
    {
        Places();
        Scopes();
        Levels();
        Floors();
        Percents();
        Colours();
        Commands();
        Replies();
        Status();
        SettingsJson();
        Manifest();
        WindowFields();
        WaterTypes();
        EngineKeys();

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

    private static void Eq<T>(T got, T want, string what) =>
        Check(EqualityComparer<T>.Default.Equals(got, want), $"{what}: got {got}, want {want}");

    private static void Places()
    {
        Eq(DungeonLight.Classify(Outdoor, null), LightPlace.Outdoors, "an outdoor cell");
        Eq(DungeonLight.Classify(Outdoor, true), LightPlace.Outdoors, "an outdoor cell whatever the flag says");
        Eq(DungeonLight.Classify(Room, true), LightPlace.Building, "an EnvCell that sees outside is a building");
        Eq(DungeonLight.Classify(DungeonCell, false), LightPlace.Dungeon, "an EnvCell that doesn't is a dungeon");
        Eq(DungeonLight.Classify(DungeonCell, null), LightPlace.Dungeon, "an EnvCell nobody could read counts as a dungeon");
        Eq(DungeonLight.Classify(0, null), LightPlace.NotInWorld, "cell 0: not in the world");
        Eq(DungeonLight.Classify(0x0000012A, false), LightPlace.NotInWorld, "landblock 0: not in the world (portal space)");
        Eq(DungeonLight.Classify(0xA9B400FF, null), LightPlace.Outdoors, "0xFF is still an outdoor cell");
        Eq(DungeonLight.Classify(0xA9B40100, null), LightPlace.Dungeon, "0x100 is the first EnvCell");
    }

    private static void Scopes()
    {
        foreach (LightScope s in Enum.GetValues<LightScope>())
        {
            Check(DungeonLight.Applies(s, LightPlace.Dungeon), $"{s}: lights dungeons");
            Check(!DungeonLight.Applies(s, LightPlace.NotInWorld), $"{s}: nothing out of the world");
        }
        Check(!DungeonLight.Applies(LightScope.Dungeons, LightPlace.Building), "dungeons: not buildings");
        Check(!DungeonLight.Applies(LightScope.Dungeons, LightPlace.Outdoors), "dungeons: not outdoors");
        Check(DungeonLight.Applies(LightScope.Indoors, LightPlace.Building), "indoors: buildings");
        Check(!DungeonLight.Applies(LightScope.Indoors, LightPlace.Outdoors), "indoors: not outdoors");
        Check(DungeonLight.Applies(LightScope.Everywhere, LightPlace.Building), "everywhere: buildings");
        Check(DungeonLight.Applies(LightScope.Everywhere, LightPlace.Outdoors), "everywhere: outdoors");
    }

    private static void Levels()
    {
        Check(Math.Abs(DungeonLight.Level(0) - 0.2f) < 1e-6, "strength 0 = AC's own 20 %");
        Check(Math.Abs(DungeonLight.Level(100) - 1f) < 1e-6, "strength 100 = 100 %");
        Check(Math.Abs(DungeonLight.Level(DungeonLight.DefaultStrength) - 0.52f) < 1e-6, "the default 40 = 52 %");
        Check(Math.Abs(DungeonLight.Level(-20) - 0.2f) < 1e-6 && Math.Abs(DungeonLight.Level(500) - 1f) < 1e-6, "clamped");
    }

    private static void Floors()
    {
        Eq(DungeonLight.FloorRgb(0xFFFFFFFF, 0), 0u, "strength 0 sends no floor");
        Eq(DungeonLight.FloorRgb(0xFFFFFFFF, -3), 0u, "below 0 sends no floor");
        Eq(DungeonLight.FloorRgb(0xFFFFFFFF, 100), 0xFFFFFFu, "100 % white");
        Eq(DungeonLight.FloorRgb(0xFFFFFFFF, 40), 0x858585u, "the default: 52 % white (133)");
        Eq(DungeonLight.FloorRgb(0xFFFFD8A8, 40), 0x857057u, "torch at 40");
        Check((DungeonLight.FloorRgb(0xFFFFFFFF, 1) & 0xFF) > 0x33, "any strength above 0 is brighter than AC's dungeon (0x33)");
        Check((DungeonLight.FloorRgb(0xFFFFFFFF, 100) & 0xFF000000) == 0, "the floor has no alpha byte");
        Eq(DungeonLight.FloorFor(false, LightScope.Everywhere, 40, 0xFFFFFFFF, LightPlace.Dungeon), 0u, "off: no floor");
        Eq(DungeonLight.FloorFor(true, LightScope.Dungeons, 40, 0xFFFFFFFF, LightPlace.Outdoors), 0u, "dungeons only: none outdoors");
        Eq(DungeonLight.FloorFor(true, LightScope.Dungeons, 40, 0xFFFFFFFF, LightPlace.NotInWorld), 0u, "none out of the world");
        Eq(DungeonLight.FloorFor(true, LightScope.Dungeons, 40, 0xFFFFFFFF, LightPlace.Dungeon), 0x858585u, "on in a dungeon");
    }

    private static void Percents()
    {
        Eq(DungeonLight.Percent(0xFF333333), 20, "AC's dungeon ambient reads 20 %");
        Eq(DungeonLight.Percent(0xFF858585), 52, "the default floor reads 52 %");
        Eq(DungeonLight.Percent(0x00FF0000), 100, "the brightest channel");
        Eq(DungeonLight.Percent(0), 0, "black");
    }

    private static void Colours()
    {
        Check(DungeonLight.TryParseColor("FFE0C0", out uint c) && c == 0xFFFFE0C0, "RRGGBB, made opaque");
        Check(DungeonLight.TryParseColor("#ffe0c0", out c) && c == 0xFFFFE0C0, "lower case with '#'");
        Check(DungeonLight.TryParseColor("80FFE0C0", out c) && c == 0xFFFFE0C0, "AARRGGBB: the alpha is dropped");
        Check(DungeonLight.TryParseColor(" Torch ", out c) && c == 0xFFFFD8A8, "a preset by name");
        Check(!DungeonLight.TryParseColor("nope", out _), "a word that isn't a preset");
        Check(!DungeonLight.TryParseColor("12345", out _), "five hex digits");
        Check(!DungeonLight.TryParseColor("", out _) && !DungeonLight.TryParseColor(null, out _), "nothing");
        Eq(DungeonLight.ColorName(0xFFFFFFFF), "white", "white by name");
        Eq(DungeonLight.ColorName(0xFFC8D8FF), "moon", "moon by name");
        Eq(DungeonLight.ColorName(0xFF102030), "102030", "any other colour as hex");
    }

    private static void Commands()
    {
        Eq(DungeonLight.ParseCommand("").Kind, LightCommandKind.Toggle, "/rv light alone toggles");
        Eq(DungeonLight.ParseCommand(null).Kind, LightCommandKind.Toggle, "null toggles");
        Eq(DungeonLight.ParseCommand(" on ").Kind, LightCommandKind.On, "on");
        Eq(DungeonLight.ParseCommand("off").Kind, LightCommandKind.Off, "off");
        Eq(DungeonLight.ParseCommand("toggle").Kind, LightCommandKind.Toggle, "toggle");
        LightCommand n = DungeonLight.ParseCommand("60");
        Check(n.Kind == LightCommandKind.Strength && n.Strength == 60, "a number is the strength");
        n = DungeonLight.ParseCommand("75%");
        Check(n.Kind == LightCommandKind.Strength && n.Strength == 75, "with a %");
        Check(DungeonLight.ParseCommand("0").Kind == LightCommandKind.Strength && DungeonLight.ParseCommand("100").Strength == 100, "the ends");
        Eq(DungeonLight.ParseCommand("101").Kind, LightCommandKind.Error, "above 100");
        Eq(DungeonLight.ParseCommand("-5").Kind, LightCommandKind.Error, "below 0");
        Eq(DungeonLight.ParseCommand("dungeons").Scope, LightScope.Dungeons, "dungeons");
        Eq(DungeonLight.ParseCommand("indoors").Scope, LightScope.Indoors, "indoors");
        Eq(DungeonLight.ParseCommand("everywhere").Scope, LightScope.Everywhere, "everywhere");
        Eq(DungeonLight.ParseCommand("all").Kind, LightCommandKind.Scope, "all = everywhere");
        LightCommand col = DungeonLight.ParseCommand("color torch");
        Check(col.Kind == LightCommandKind.Color && col.ColorArgb == 0xFFFFD8A8, "color torch");
        col = DungeonLight.ParseCommand("colour 00ff00");
        Check(col.Kind == LightCommandKind.Color && col.ColorArgb == 0xFF00FF00, "colour 00ff00");
        Eq(DungeonLight.ParseCommand("color").Kind, LightCommandKind.Error, "color without one");
        Eq(DungeonLight.ParseCommand("color xyz").Kind, LightCommandKind.Error, "a bad colour");
        Eq(DungeonLight.ParseCommand("show").Kind, LightCommandKind.Show, "show");
        Eq(DungeonLight.ParseCommand("window").Kind, LightCommandKind.Show, "window");
        Eq(DungeonLight.ParseCommand("status").Kind, LightCommandKind.Status, "status");
        Eq(DungeonLight.ParseCommand("help").Kind, LightCommandKind.Help, "help");
        LightCommand bad = DungeonLight.ParseCommand("brighter please");
        Check(bad.Kind == LightCommandKind.Error && bad.Error.Contains("brighter please"), "an unknown word names itself");
    }

    private static LightStatus S(bool on = true, LightScope scope = LightScope.Dungeons, LightPlace place = LightPlace.Dungeon,
        bool api = true, bool live = true, uint client = 0xFF333333, uint applied = 0xFF858585) =>
        new(on, scope, 40, place, api, live, client, applied);

    private static void Status()
    {
        string line = DungeonLight.StatusLine(S());
        Check(line.Contains("dungeon") && line.Contains("20%") && line.Contains("52%"), "lighting a dungeon names the levels: " + line);
        Check(DungeonLight.StatusLine(S(api: false)).Contains("newer RynthCore"), "an older engine");
        Check(DungeonLight.IsWarning(S(api: false)), "an older engine is a warning");
        Check(DungeonLight.StatusLine(S(on: false)).StartsWith("Off"), "off");
        Check(!DungeonLight.IsWarning(S(on: false, live: false)), "off with the hook off isn't a warning");
        Check(DungeonLight.StatusLine(S(live: false)).Contains("/rc hooks"), "the hook off");
        Check(DungeonLight.IsWarning(S(live: false)), "the hook off is a warning");
        Check(DungeonLight.StatusLine(S(place: LightPlace.Outdoors)).Contains("not lighting here"), "outdoors with dungeons only");
        Check(DungeonLight.StatusLine(S(place: LightPlace.NotInWorld)).Contains("waiting"), "out of the world");
        Check(DungeonLight.StatusLine(S(scope: LightScope.Everywhere, place: LightPlace.Outdoors)).StartsWith("Lighting outdoors"), "everywhere, outdoors");
        Check(!DungeonLight.StatusLine(S(client: 0, applied: 0)).Contains("->"), "no levels before the first frame");
    }

    private static void SettingsJson()
    {
        var d = new VisionSettings();
        Check(!d.LightOn && d.LightStrength == 40 && d.LightColorArgb == 0xFFFFFFFF && d.LightScope == 0,
            "defaults: off, 40, white, dungeons");
        using (JsonDocument doc = JsonDocument.Parse(d.ToJson()))
        {
            JsonElement r = doc.RootElement;
            Check(r.GetProperty("light").GetInt32() == 0 && r.GetProperty("lightStrength").GetInt32() == 40
                && r.GetProperty("lightColor").GetUInt32() == 0xFFFFFFFF && r.GetProperty("lightScope").GetInt32() == 0,
                "ToJson carries the light keys");
            Check(r.TryGetProperty("radar", out _) && r.TryGetProperty("waterTypes", out _), "and still the old ones");
        }

        var s = new VisionSettings();
        s.ApplyJson("{\"light\":1,\"lightStrength\":65,\"lightColor\":4294957216,\"lightScope\":2}");
        Check(s.LightOn && s.LightStrength == 65 && s.LightColorArgb == 4294957216u && s.LightScope == 2, "ApplyJson reads them");
        var back = new VisionSettings();
        back.ApplyJson(s.ToJson());
        Check(back.LightOn && back.LightStrength == 65 && back.LightColorArgb == s.LightColorArgb && back.LightScope == 2, "round trip");

        var c = new VisionSettings();
        c.ApplyJson("{\"lightStrength\":250,\"lightColor\":16777215,\"lightScope\":9}");
        Check(c.LightStrength == 100 && c.LightColorArgb == 0xFFFFFFFF && c.LightScope == 2, "pulled into range; the colour made opaque");
        c.ApplyJson("{\"lightStrength\":-4,\"lightScope\":-1}");
        Check(c.LightStrength == 0 && c.LightScope == 0, "low values pulled up");

        // One field at a time (what the engine's Vision panel sends) leaves the light alone.
        s.ApplyJson("{\"radar\":0}");
        Check(s.LightOn && s.LightStrength == 65 && !s.ShowRadarRing, "another key leaves the light as it was");
    }

    private static void Manifest()
    {
        using JsonDocument doc = JsonDocument.Parse(VisionManifest.Json(true));
        Check(doc.RootElement.GetProperty("version").GetInt32() == 1, "manifest version 1");
        Check(!doc.RootElement.TryGetProperty("sections", out _) && !doc.RootElement.TryGetProperty("contributions", out _),
            "nothing for the Mini Remote's sections");
        JsonElement ws = doc.RootElement.GetProperty("windows");
        Eq(ws.GetArrayLength(), 1, "one window: Vision (the separate Dungeon Light window is gone)");
        JsonElement w = ws[0];
        // The engine panel's key (RynthCore ImGui/Panels/VisionFace.Title): the take-over keeps its saved place.
        Check(w.GetProperty("id").GetString() == "vision" && w.GetProperty("title").GetString() == "Vision"
            && VisionManifest.Title == "Vision", "the window's id and title: the engine's Vision panel's key");
        Check(w.GetProperty("surface").GetString() == VisionManifest.Surface && VisionManifest.Surface == "window:vision", "its surface");
        Check(w.GetProperty("takeOver").GetBoolean(), "it takes the engine's Vision panel's place");
        Eq(w.GetProperty("icon").GetString(), "", "no icon of its own: the engine bar's eye for Vision");
        Check(w.GetProperty("bar").GetBoolean() && w.GetProperty("edgeToEdge").GetBoolean(), "a bar button (the engine's steps aside); edge to edge");
        Check(!w.TryGetProperty("chrome", out _) && !w.TryGetProperty("fitHeight", out _), "the engine's header and frame, sized by the player");
        Check(w.GetProperty("width").GetInt32() == VisionManifest.Width && w.GetProperty("height").GetInt32() == VisionManifest.Height
            && w.GetProperty("minWidth").GetInt32() == VisionManifest.MinWidth && w.GetProperty("minHeight").GetInt32() == VisionManifest.MinHeight,
            "the constants are the JSON's");
        Check(VisionManifest.Width == 320 && VisionManifest.Height == 560 && VisionManifest.MinWidth == 280 && VisionManifest.MinHeight == 240,
            "the engine face's sizes (PanelSpec 320 x 560, at least 280 x 240)");

        using JsonDocument none = JsonDocument.Parse(VisionManifest.Json(false));
        Check(!none.RootElement.TryGetProperty("windows", out _), "an older engine: no window, its own Vision panel stays");
        Check(VisionManifest.HostsWindow(true, true, true, true), "v91 with plugin windows: hosted");
        Check(!VisionManifest.HostsWindow(false, true, true, true), "no light hook (before v91): not hosted");
        Check(!VisionManifest.HostsWindow(true, false, true, true), "no SetPanelOpen: not hosted");
        Check(!VisionManifest.HostsWindow(true, true, false, true), "no engine drawers (Nameplates, Combat text): not hosted");
        Check(!VisionManifest.HostsWindow(true, true, true, false), "no engine look: not hosted");
    }

    private static void WindowFields()
    {
        Eq(VisionFields.TabNames.Length, Enum.GetValues<VisionTab>().Length, "a name per tab");
        Check(VisionFields.TabNames[0] == "Terrain" && VisionFields.TabNames[1] == "Nameplates" && VisionFields.TabNames[2] == "Combat text"
            && VisionFields.TabNames[3] == "Light", "the engine's three tabs, then Light");
        Eq(VisionFields.EngineTabKey(VisionTab.Nameplates), "nameplates", "Nameplates: the engine's body");
        Eq(VisionFields.EngineTabKey(VisionTab.CombatText), "combattext", "Combat text: the engine's body");
        Check(VisionFields.EngineTabKey(VisionTab.Terrain) == null && VisionFields.EngineTabKey(VisionTab.Light) == null,
            "Terrain and Light are RynthVision's own");

        Check(Math.Abs(VisionFields.OpacityPercent(0x80FFD000) - 50.196f) < 0.01f, "0x80 = 50 %");
        Eq(VisionFields.WithOpacity(0x60FF2020, 100f), 0xFFFF2020u, "100 %: alpha FF, RGB kept");
        Eq(VisionFields.WithOpacity(0x60FF2020, 0f), 0x00FF2020u, "0 %: alpha 00");
        Eq(VisionFields.WithOpacity(0x60FF2020, 250f), 0xFFFF2020u, "clamped high");
        Eq(VisionFields.WithOpacity(0x60FF2020, float.NaN), 0x00FF2020u, "NaN reads 0");
        Eq(VisionFields.WithOpacity(0x60FF2020, VisionFields.OpacityPercent(0x60FF2020)), 0x60FF2020u, "untouched it round-trips");

        Check(VisionFields.TryParseHex("80FFD000", out uint c) && c == 0x80FFD000, "AARRGGBB");
        Check(VisionFields.TryParseHex("#ff2020", out c) && c == 0xFFFF2020, "RRGGBB with '#': opaque");
        Check(!VisionFields.TryParseHex("FF20", out _) && !VisionFields.TryParseHex("", out _) && !VisionFields.TryParseHex("GG000000", out _),
            "bad hex");

        Check(VisionFields.ParseCsv("18, 19,x,20").SequenceEqual(new[] { 18, 19, 20 }), "csv: numbers kept, words dropped");
        Check(VisionFields.ParseCsv("  ").Count == 0 && VisionFields.ParseCsv(null).Count == 0, "csv: nothing");
        Eq(VisionFields.Csv(new[] { 16, 17, 18 }), "16,17,18", "csv out");
    }

    private static void WaterTypes()
    {
        int[] defaults = new VisionSettings().WaterTerrainTypes;
        Check(defaults.SequenceEqual(new[] { 16, 17, 18, 19, 20 }), "the defaults: ACE's water types 16-20");
        Check(VisionSettings.NormalizeWaterTypes(Array.Empty<int>()).SequenceEqual(defaults), "empty: back to the defaults");
        Check(VisionSettings.NormalizeWaterTypes(new[] { 40, -1 }).SequenceEqual(defaults), "all invalid: the defaults");
        Check(VisionSettings.NormalizeWaterTypes(new[] { 19, 18, 19, 99 }).SequenceEqual(new[] { 19, 18 }), "in range, each once, in order");
        Check(!ReferenceEquals(VisionSettings.NormalizeWaterTypes(Array.Empty<int>()), VisionSettings.NormalizeWaterTypes(Array.Empty<int>())),
            "the defaults are a fresh array each time (never shared, never changed in place)");

        // ApplyJson (the engine's panels, the file) still normalises the same way.
        var s = new VisionSettings();
        s.ApplyJson("{\"waterTypes\":[18,18,33]}");
        Check(s.WaterTerrainTypes.SequenceEqual(new[] { 18 }), "ApplyJson: 33 dropped, 18 once");
        s.ApplyJson("{\"waterTypes\":[]}");
        Check(s.WaterTerrainTypes.SequenceEqual(defaults), "ApplyJson: empty is the defaults");

        // Clone: the window's copy. Changing it leaves the tick's settings alone.
        var a = new VisionSettings { ShowRadarRing = true, SlopeRenderRadius = 9 };
        VisionSettings b = a.Clone();
        b.ShowRadarRing = false;
        b.SlopeRenderRadius = 3;
        b.WaterTerrainTypes = new[] { 18 };
        Check(a.ShowRadarRing && a.SlopeRenderRadius == 9 && a.WaterTerrainTypes.SequenceEqual(defaults), "a clone is a copy");
        Check(new VisionSettings { LightStrength = 77 }.Clone().LightStrength == 77, "the light's settings are copied too");
    }

    /// <summary>
    /// Every key the engine's Vision panel saved (RynthCore UI/Data/VisionKeys) is still written and read: the
    /// window edits the same settings in the same file, so a player's choices carry over.
    /// </summary>
    private static void EngineKeys()
    {
        string[] keys =
        {
            "radar", "slopes", "water", "waterAnyCorner", "waterImpassableOnly", "slopeColor", "waterColor", "radarColor",
            "radarRange", "ringThick", "ringHeight", "slopeRadius", "slopeFloorZ", "slopeBias", "waterRadius", "waterTypes",
        };
        var src = new VisionSettings
        {
            ShowRadarRing = false, ShowUnclimbableSlopes = true, ShowImpassableWater = true, WaterAnyCorner = false,
            WaterImpassableOnly = false, SlopeColorArgb = 0x11223344, WaterColorArgb = 0x55667788, RadarRingColorArgb = 0x99AABBCC,
            RadarRangeWorld = 120f, RadarRingThickness = 3.5f, RadarRingHeight = 7f, SlopeRenderRadius = 5, SlopeFloorZ = 0.5f,
            SlopeHeightBias = 0.25f, WaterRenderRadius = 6, WaterTerrainTypes = new[] { 17, 19 },
        };
        string json = src.ToJson();
        using (JsonDocument doc = JsonDocument.Parse(json))
            foreach (string k in keys)
                Check(doc.RootElement.TryGetProperty(k, out _), $"ToJson writes \"{k}\"");
        var back = new VisionSettings();
        back.ApplyJson(json);
        Check(!back.ShowRadarRing && back.ShowUnclimbableSlopes && back.ShowImpassableWater && !back.WaterAnyCorner && !back.WaterImpassableOnly,
            "the toggles round-trip");
        Check(back.SlopeColorArgb == 0x11223344 && back.WaterColorArgb == 0x55667788 && back.RadarRingColorArgb == 0x99AABBCC, "the colours");
        Check(back.RadarRangeWorld == 120f && back.RadarRingThickness == 3.5f && back.RadarRingHeight == 7f && back.SlopeRenderRadius == 5
            && back.SlopeFloorZ == 0.5f && back.SlopeHeightBias == 0.25f && back.WaterRenderRadius == 6, "the tuning");
        Check(back.WaterTerrainTypes.SequenceEqual(new[] { 17, 19 }), "the water types");
    }
    /// <summary>
    /// Every /rv command answers in chat (2026-10-28): each verb typed right and typed wrong, and the
    /// line it says. The commands themselves run on the plugin's tick with the game's host, so the
    /// parse and the reply wording are checked here.
    /// </summary>
    private static void Replies()
    {
        var verbs = new (string Typed, VisionVerb Verb)[]
        {
            ("", VisionVerb.Status), ("radar", VisionVerb.Radar), ("slopes", VisionVerb.Slopes), ("water", VisionVerb.Water),
            ("watertypes", VisionVerb.WaterTypes), ("debug", VisionVerb.Debug), ("light", VisionVerb.Light),
            ("light 60", VisionVerb.Light), ("rader", VisionVerb.Unknown), ("lightning", VisionVerb.Unknown),
        };
        var seen = new HashSet<VisionVerb>();
        foreach (var (typed, verb) in verbs)
        {
            Eq(VisionCommands.Parse(typed, out _), verb, $"'/rv {typed}'");
            seen.Add(verb);
        }
        foreach (VisionVerb v in Enum.GetValues<VisionVerb>()) Check(seen.Contains(v), $"the table covers /rv's {v}");
        VisionCommands.Parse("light color torch", out string rest);
        Eq(rest.Trim(), "color torch", "/rv light passes the rest on");

        Eq(VisionCommands.Switched(VisionVerb.Radar, true), "Radar ring on.", "radar: the new state");
        Eq(VisionCommands.Switched(VisionVerb.Water, false), "Impassable water off.", "water: the new state");
        Check(VisionCommands.State(true, false, true).Contains("Radar ring on, unclimbable slopes off, impassable water on"), "/rv alone: the state");
        Check(VisionCommands.State(true, false, true).Contains(VisionCommands.Usage), "/rv alone: and the usage");
        Check(VisionCommands.Debug(true).StartsWith("Debug on", StringComparison.Ordinal), "debug: the new state");
        string unknown = VisionCommands.Unknown("rader");
        Check(unknown.Contains("'/rv rader'") && unknown.Contains("Usage"), "an unknown word: named, with the usage");

        // /rv light: every kind answers (the change's summary, show, status, help, or the error with the usage).
        foreach (string typed in new[] { "", "on", "off", "toggle", "60", "dungeons", "color torch", "show", "status", "help", "101", "color", "brighter" })
        {
            LightCommand c = DungeonLight.ParseCommand(typed);
            string reply = c.Kind switch
            {
                LightCommandKind.Error => c.Error + " " + DungeonLight.Usage,
                LightCommandKind.Help => DungeonLight.Usage,
                LightCommandKind.Status => "Dungeon light: " + DungeonLight.StatusLine(S()),
                LightCommandKind.Show => "Vision window shown, on its Light tab.",
                _ => DungeonLight.Summary(true, 40, LightScope.Dungeons, DungeonLight.DefaultColor, engineHasApi: true),
            };
            Check(reply.Trim().Length > 0, $"/rv light {typed}: answers");
            if (c.Kind == LightCommandKind.Error) Check(c.Error.Length > 0 && reply.Contains("/rv light"), $"/rv light {typed}: why, and the usage");
        }
        string summary = DungeonLight.Summary(true, 60, LightScope.Indoors, DungeonLight.DefaultColor, engineHasApi: true);
        Check(summary.StartsWith("Dungeon light on: strength 60%", StringComparison.Ordinal), "the light's summary: " + summary);
        Check(DungeonLight.Summary(false, 40, LightScope.Dungeons, DungeonLight.DefaultColor, engineHasApi: false).Contains("newer RynthCore"), "an old engine says why");
    }
}
