using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using RynthCore.Plugin.Shared;
using T = RynthCore.Plugin.RynthAi.LegacyUi.NavPointType;
using RT = RynthCore.Plugin.RynthAi.LegacyUi.NavRouteType;

namespace RynthCore.RynthAiHostTests.Tests;

// Nav3D waypoint labels and guide line (Silentkelpie's rynth PR #1 idea, re-implemented on our
// pump-thread Nav3D markers). Both settings are off by default and then change nothing. Labels
// are filled strokes (Nav3D triangles) on an upright plane facing the character; the guide is
// one Nav3D line from the character's feet to the waypoint nav is heading for. The frame is
// built from the route on the plugin tick and the emit reads only the frame.
internal static class NavOverlayTests
{
    private const double NS0 = 42.0, EW0 = 33.0;

    public static void Register(Runner r)
    {
        r.Add("nav overlay: both settings off by default - markers draw exactly as before", OffByDefault);
        r.Add("nav overlay: number labels over nearby waypoints, upright, facing the character, readable left to right", NumberLabels);
        r.Add("nav overlay: names mode names jumps and checkpoints and hangs actions on the point before", NamesMode);
        r.Add("nav overlay: labels only within range, active point first, nearest next, capped", LabelRangeAndCap);
        r.Add("nav overlay: the guide line runs from the character's feet to the active waypoint, not to an action point", GuideLine);
        r.Add("nav overlay: the frame is a snapshot - emptying the route afterwards changes nothing", FrameIsSnapshot);
        r.Add("nav overlay: the label triangle budget holds for long names", TriangleBudget);
        r.Add("nav overlay: settings round-trip; an older engine's payload leaves them alone", SettingsRoundTrip);
        r.Add("nav overlay: Settings > Navigation shows the Nav panel's Breadcrumbs and Route overlay switches", SettingsLinksNavPanelSwitches);
    }

    /// <summary>
    /// Settings > Navigation carries the Nav panel's two switches (NavOverlay.TrackBreadcrumbs and
    /// ShowRouteMarkers), so either place changes the same setting. An engine that predates them
    /// sends neither, and its save must leave them alone.
    /// </summary>
    private static void SettingsLinksNavPanelSwitches()
    {
        var s = new LegacyUiSettings();
        s.NavOverlay.TrackBreadcrumbs = false;
        s.NavOverlay.ShowRouteMarkers = true;
        var dash = (LegacyDashboardRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(LegacyDashboardRenderer).GetField("_settings", flags)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", flags)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));

        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(sent.TrackBreadcrumbs == false, "Breadcrumbs sent to the Settings face (off)");
        Check.True(sent.ShowRouteOverlay == true, "Route overlay sent to the Settings face (on)");

        dash.ApplySettingsJson("{\"TrackBreadcrumbs\":true,\"ShowRouteOverlay\":false}");
        Check.True(s.NavOverlay.TrackBreadcrumbs, "Settings turns Breadcrumbs on (the Nav panel's switch)");
        Check.False(s.NavOverlay.ShowRouteMarkers, "Settings turns Route overlay off (the Nav panel's switch)");

        dash.ApplySettingsJson("{\"NavRingThickness\":6}");
        Check.True(s.NavOverlay.TrackBreadcrumbs, "an older engine's save leaves Breadcrumbs alone");
        Check.False(s.NavOverlay.ShowRouteMarkers, "an older engine's save leaves Route overlay alone");
    }

    // ── Rig ─────────────────────────────────────────────────────────────

    private static NavPoint Pt(double north, double east, T type = T.Point)
        => new() { Type = type, NS = NS0 + north / 240.0, EW = EW0 + east / 240.0 };

    private static NavRouteParser Route(params NavPoint[] pts)
    {
        var r = new NavRouteParser { RouteType = RT.Linear };
        r.Points.AddRange(pts);
        return r;
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly NavMarkerRenderer R;
        public readonly float Px, Py;   // the player's landblock-local metres (east, north)

        public Rig(NavRouteParser route, int labels = 0, bool guide = false, int active = 0)
        {
            FakeHost.Reset();
            FakeHost.Nav3DCalls = true;
            FakeHost.SetNavPosition(NS0, EW0);
            Px = FakeHost.PlayerPose.X;
            Py = FakeHost.PlayerPose.Y;
            S = new LegacyUiSettings { CurrentRoute = route, ActiveNavIndex = active, NavHeightOffset = 0f, NavSlopeSink = 0f };
            if (labels != 0) S.NavLabelMode = labels;
            if (guide) S.NavGuideLine = true;
            R = new NavMarkerRenderer(FakeHost.Create(), S);
        }

        public void Submit()
        {
            FakeHost.Nav3DRings.Clear(); FakeHost.Nav3DLines.Clear(); FakeHost.Nav3DTriangles.Clear();
            R.SubmitNav3D();
        }

        /// <summary>World position (Nav3D frame) of a point north/east of the player, in metres.</summary>
        public (float X, float Z) At(double north, double east) => (Px + (float)east, Py + (float)north);
    }

    private sealed class Recorder : INav3DSink
    {
        public readonly List<float[]> Lines = new();
        public readonly List<float[]> Tris = new();
        public void Line(float x1, float y1, float z1, float x2, float y2, float z2, float t, uint c) => Lines.Add(new[] { x1, y1, z1, x2, y2, z2 });
        public void Triangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint c)
            => Tris.Add(new[] { x1, y1, z1, x2, y2, z2, x3, y3, z3 });
    }

    private static IEnumerable<(float X, float Y, float Z)> Vertices(IEnumerable<float[]> tris)
    {
        foreach (float[] t in tris)
            for (int i = 0; i < 9; i += 3) yield return (t[i], t[i + 1], t[i + 2]);
    }

    // ── Tests ───────────────────────────────────────────────────────────

    private static void OffByDefault()
    {
        var d = new LegacyUiSettings();
        Check.Eq(d.NavLabelMode, NavLabelModes.Off, "labels default off");
        Check.False(d.NavGuideLine, "guide line default off");

        var rig = new Rig(Route(Pt(5, 0), Pt(10, 0), Pt(10, 6)));
        rig.Submit();
        Check.Eq(FakeHost.Nav3DRings.Count, 3, "three rings");
        Check.Eq(FakeHost.Nav3DLines.Count, 2, "two route lines, nothing more");
        Check.Eq(FakeHost.Nav3DTriangles.Count, 0, "no label triangles");
        Check.True(FakeHost.Nav3DLines.All(l => l.Color == 0xFF0088FF), "only route-coloured lines");
    }

    private static void NumberLabels()
    {
        // One waypoint 10 m north, one 10 m east.
        var rig = new Rig(Route(Pt(10, 0), Pt(0, 10)), labels: NavLabelModes.Numbers);
        rig.Submit();
        Check.True(FakeHost.Nav3DTriangles.Count > 0, "labels drawn as triangles");
        Check.Eq(FakeHost.Nav3DLines.Count, 1, "the route line only (labels use no lines)");

        var (nx, nz) = rig.At(10, 0);
        var (ex, ez) = rig.At(0, 10);
        var north = FakeHost.Nav3DTriangles.Where(t => Math.Abs(t.Z1 - nz) < 2f && Math.Abs(t.X1 - nx) < 2f).ToList();
        var east = FakeHost.Nav3DTriangles.Where(t => Math.Abs(t.X1 - ex) < 2f && Math.Abs(t.Z1 - ez) < 2f).ToList();
        Check.Eq(north.Count + east.Count, FakeHost.Nav3DTriangles.Count, "every triangle belongs to one of the two labels");
        Check.True(north.Count > 0 && east.Count > 0, "both waypoints labelled");
        // Facing the character: the north label's plane is east-west (one Z), the east one's north-south (one X).
        Check.True(north.All(t => Math.Abs(t.Z1 - nz) < 1e-3 && Math.Abs(t.Z2 - nz) < 1e-3 && Math.Abs(t.Z3 - nz) < 1e-3), "north label faces south, toward you");
        Check.True(east.All(t => Math.Abs(t.X1 - ex) < 1e-3 && Math.Abs(t.X2 - ex) < 1e-3 && Math.Abs(t.X3 - ex) < 1e-3), "east label faces west, toward you");
        // Upright and above the ring.
        Check.True(north.All(t => t.Y1 >= 0.9f && t.Y2 >= 0.9f && t.Y3 >= 0.9f), "above the ring");
        float minY = north.Min(t => Math.Min(t.Y1, Math.Min(t.Y2, t.Y3))), maxY = north.Max(t => Math.Max(t.Y1, Math.Max(t.Y2, t.Y3)));
        Check.True(maxY - minY > 0.3f, "has height (upright)");

        // Readable from the character: "12" north of you runs west to east ('1' left of '2').
        var rec = new Recorder();
        var frame = new NavOverlayFrame(new[] { new NavLabel(0f, 0f, 10f, "12", 0, NavOverlay.LabelColor) }, null, 0f, 0f, 0f);
        NavOverlay.EmitLabels(frame, rec);
        int oneTris = NavStrokeFont.Glyph('1')!.Sum(p => p.Length / 2 - 1) * 2;
        float oneX = Vertices(rec.Tris.Take(oneTris)).Average(v => v.X);
        float twoX = Vertices(rec.Tris.Skip(oneTris)).Average(v => v.X);
        Check.True(oneX < twoX, $"'1' is west of '2' seen from the south ({oneX:0.00} < {twoX:0.00})");
        // Seen looking east, left-to-right is north to south.
        rec = new Recorder();
        NavOverlay.EmitLabels(new NavOverlayFrame(new[] { new NavLabel(10f, 0f, 0f, "12", 0, NavOverlay.LabelColor) }, null, 0f, 0f, 0f), rec);
        float oneZ = Vertices(rec.Tris.Take(oneTris)).Average(v => v.Z), twoZ = Vertices(rec.Tris.Skip(oneTris)).Average(v => v.Z);
        Check.True(oneZ > twoZ, "looking east, '1' is north of '2'");
        // Far labels are bigger.
        Check.True(NavOverlay.LineHeight(50f) > NavOverlay.LineHeight(5f), "a far label is drawn taller");
    }

    private static void NamesMode()
    {
        var pts = new List<NavPoint>
        {
            Pt(5, 0),                                                         // 0
            new() { Type = T.Chat, ChatCommand = "/vt stop" },                // 1
            new() { Type = T.Recall, SpellId = 2645 },                        // 2
            Pt(10, 0, T.Checkpoint),                                          // 3
            Pt(12, 0, T.Jump),                                                // 4
            new() { Type = T.PortalNPC, TargetName = "Gateway" },             // 5
            new() { Type = T.Pause, PauseTimeMs = 2500 },                     // 6
            new() { Type = T.Npc, TargetName = "Town Crier" },                // 7
            new() { Type = T.OpenVendor, TargetName = "Shopkeeper" },         // 8
        };
        var text = NavOverlay.BuildText(pts, pts, NavLabelModes.Names, id => id == 2645 ? "Portal Recall" : "?");
        Check.Eq(text.Own[0], "0", "plain point: its number");
        Check.True(text.Own[1] == null && text.Own[2] == null, "action points get no label of their own");
        Check.Eq(string.Join("|", text.Actions[0] ?? Array.Empty<string>()), "1 CHAT /VT STOP|2 PORTAL RECALL", "actions hang on the point before");
        Check.Eq(text.Own[3], "3 CHECKPOINT", "checkpoint named");
        Check.Eq(text.Own[4], "4 JUMP", "jump named");
        Check.Eq(string.Join("|", text.Actions[4] ?? Array.Empty<string>()), "5 PORTAL GATEWAY|6 PAUSE 2.5S|7 NPC TOWN CRIER|...",
            "at most three action lines, then ...");

        var numbers = NavOverlay.BuildText(pts, pts, NavLabelModes.Numbers, _ => "");
        Check.Eq(numbers.Own[4], "4", "numbers mode: just the number");
        Check.True(numbers.Actions.All(a => a == null), "numbers mode: no action lines");

        // Drawn: the action lines stack above the point's own line.
        var rig = new Rig(Route(pts.ToArray()), labels: NavLabelModes.Names);
        var frame = rig.R.BuildOverlay(rig.S.CurrentRoute!, rig.Px, rig.Py, 0f, NS0, EW0, 0f);
        var at0 = frame.Labels.Where(l => l.Text.StartsWith("0", StringComparison.Ordinal) || l.Text.StartsWith("1 ", StringComparison.Ordinal) || l.Text.StartsWith("2 ", StringComparison.Ordinal)).ToList();
        Check.Eq(string.Join("|", at0.Select(l => $"{l.Row}:{l.Text}")), "0:0|1:1 CHAT /VT STOP|2:2 PORTAL RECALL", "rows 0, 1, 2 over point 0");
        Check.True(at0.Skip(1).All(l => l.Color == NavOverlay.ActionLabelColor), "action lines in their own colour");
    }

    private static void LabelRangeAndCap()
    {
        // 40 points in a line northwards, 2 m apart (0..78 m): 31 of them within 60 m.
        var pts = Enumerable.Range(0, 40).Select(i => Pt(2 * i, 0)).ToArray();
        var rig = new Rig(Route(pts), labels: NavLabelModes.Numbers, active: 5);
        var frame = rig.R.BuildOverlay(rig.S.CurrentRoute!, rig.Px, rig.Py, 0f, NS0, EW0, 0f);
        Check.Eq(frame.Labels.Count, NavOverlay.MaxLabelLines, "capped at the line budget");
        Check.Eq(frame.Labels[0].Text, "5", "the active point first");
        Check.Eq(frame.Labels[0].Color, NavOverlay.ActiveLabelColor, "in the active colour");
        Check.Eq(frame.Labels[1].Text, "0", "then the nearest");
        Check.True(frame.Labels.All(l => Math.Abs(l.Z - rig.Py) <= NavOverlay.LabelRange + 0.01f), "all within range");

        var few = new Rig(Route(Pt(10, 0), Pt(100, 0)), labels: NavLabelModes.Numbers);
        var f2 = few.R.BuildOverlay(few.S.CurrentRoute!, few.Px, few.Py, 0f, NS0, EW0, 0f);
        Check.Eq(string.Join(",", f2.Labels.Select(l => l.Text)), "0", "a point 100 m away gets no label");
    }

    private static void GuideLine()
    {
        var rig = new Rig(Route(Pt(5, 0), Pt(10, 4)), guide: true, active: 1);
        rig.Submit();
        Check.Eq(FakeHost.Nav3DLines.Count, 2, "the route line plus the guide");
        var g = FakeHost.Nav3DLines[0];
        Check.Eq(g.Color, NavOverlay.GuideColor, "the guide goes in first (never crowded out)");
        var (tx, tz) = rig.At(10, 4);
        Check.True(Math.Abs(g.X1 - rig.Px) < 1e-3 && Math.Abs(g.Z1 - rig.Py) < 1e-3, "starts at the character");
        Check.True(Math.Abs(g.X2 - tx) < 1e-3 && Math.Abs(g.Z2 - tz) < 1e-3, "ends at the active waypoint");
        Check.True(g.Y1 > 0f && g.Y1 < 0.5f, "at the feet, lifted a little");
        Check.Eq(FakeHost.Nav3DTriangles.Count, 0, "labels stay off");

        var chat = new Rig(Route(Pt(5, 0), new NavPoint { Type = T.Chat, ChatCommand = "hi" }, Pt(10, 0)), guide: true, active: 1);
        chat.Submit();
        Check.False(FakeHost.Nav3DLines.Any(l => l.Color == NavOverlay.GuideColor), "no guide to an action point (placeholder coordinates)");
    }

    private static void FrameIsSnapshot()
    {
        var route = Route(Pt(5, 0), Pt(10, 0), Pt(15, 0));
        var rig = new Rig(route, labels: NavLabelModes.Numbers, guide: true, active: 2);
        NavOverlayFrame frame = rig.R.BuildOverlay(route, rig.Px, rig.Py, 0f, NS0, EW0, 0f);
        var before = new Recorder();
        NavOverlay.EmitGuide(frame, before, 0.06f);
        NavOverlay.EmitLabels(frame, before);

        route.Points.Clear();   // what a reload or an edit on another thread would do
        var after = new Recorder();
        NavOverlay.EmitGuide(frame, after, 0.06f);
        NavOverlay.EmitLabels(frame, after);
        Check.Eq(after.Tris.Count, before.Tris.Count, "same label triangles from the frame");
        Check.Eq(after.Lines.Count, 1, "same guide line");
        Check.True(before.Tris.Zip(after.Tris).All(p => p.First.SequenceEqual(p.Second)), "identical geometry");
        Check.False(frame.Labels is List<NavLabel>, "labels are not a list anyone can grow");

        rig.Submit();
        Check.Eq(FakeHost.Nav3DTriangles.Count + FakeHost.Nav3DLines.Count + FakeHost.Nav3DRings.Count, 0, "an empty route draws nothing next tick");
    }

    private static void TriangleBudget()
    {
        var labels = Enumerable.Range(0, NavOverlay.MaxLabelLines)
            .Select(i => new NavLabel(0f, 0f, 5f + i, "WWWWWWWWWWWWWWWWWWWWWW", 0, NavOverlay.LabelColor)).ToArray();
        var rec = new Recorder();
        int used = NavOverlay.EmitLabels(new NavOverlayFrame(labels, null, 0f, 0f, 0f), rec);
        Check.True(used <= NavOverlay.MaxLabelTriangles && rec.Tris.Count == used, $"at most {NavOverlay.MaxLabelTriangles} triangles ({used})");
        Check.True(used > NavOverlay.MaxLabelTriangles - 20, "and the budget is used, not cut short");
    }

    private static void SettingsRoundTrip()
    {
        var s = new LegacyUiSettings { NavLabelMode = NavLabelModes.Names, NavGuideLine = true };
        string json = JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings);
        var back = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.Eq(back.NavLabelMode, NavLabelModes.Names, "label mode saved per character");
        Check.True(back.NavGuideLine, "guide line saved per character");

        var old = JsonSerializer.Deserialize("{\"NavRingThickness\":6}", RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.Eq(old.NavLabelMode, -1, "an older engine's payload: label mode absent (-1, kept)");
        Check.Null(old.NavGuideLine, "an older engine's payload: guide line absent (null, kept)");

        Check.Eq(NavLabelModes.Clamp(7), NavLabelModes.Off, "a bad mode reads as off");
    }
}
