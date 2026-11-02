using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Outdoor hunting (2026-10-23): walk from monster to monster around obstacles on RynthNav's map.
// The path function here plays RynthNav: by default a straight walk, with per-monster overrides (a
// building in the way makes the walk long; a cliff makes it impossible).
internal static class OutdoorHuntTests
{
    public static void Register(Runner r)
    {
        r.Add("hunt: the next monster is the shortest walk, not the nearest in a straight line (one behind a building)", ShortestWalk);
        r.Add("hunt: a higher Priority comes first, whatever the walk", PriorityFirst);
        r.Add("hunt: no way to a monster (a cliff, water): left out for a while; a walk ending close enough still counts", Unreachable);
        r.Add("hunt: it stays within Hunt Radius of where it started (0 = anywhere)", Radius);
        r.Add("hunt: plans again when the monster moves, the walk runs out, or a while passes; not every tick", Replan);
        r.Add("hunt: nothing in range: it waits where it is (no walk back to the start); Hunt Radius defaults to anywhere", WaitsWhereItIs);
        r.Add("hunt: /ra hunt range|radius answer and cap; status and stop don't toggle it; panel opens the window", Commands);
        r.Add("hunt: the target dies: the next one; no pathfinder: it stands", NextAndNoPathfinder);
        r.Add("hunt: the settings round-trip with the rest (saved, copied, engine payload)", Settings);
        r.Add("hunt (test 10-23): a monster 12 yd away but a 143 yd walk is too far round; the one being walked to is kept past Hunt Range", FarRound);
        r.Add("hunt (test 10-23): at a wall with the monster on its far side: no 1 yd walks every half second; left out after NoFightSec", WallBetween);
        r.Add("hunt: a monster right beside it that combat doesn't take: stands, then leaves it out", RightHere);
        r.Add("hunt (test 10-23): in a building it walks back out the way it went in (the loops it made cut out)", WayBackOut);
        r.Add("hunt: in a building with no way in known, or the walks out ending still inside: it waits", IndoorsWaits);
    }

    private static void WallBetween()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        nav.Walls[(1010, 1000)] = (V(1008.7, 1000), 1.3);   // behind a wall: the walk ends 1.3 yd short, on this side
        var list = Sorted(C(1, 1010, 1000, me), C(2, 1000, 1030, me));
        var d = h.Tick(T0, me, true, list, nav.Path);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.TargetId == 1, "walks to the nearer one (its walk ends close enough to count)");
        // At the wall: combat can't see through it, so the hunt is asked again and again.
        var atWall = V(1008.7, 1000);
        var walks = 0;
        for (double t = 3; t < 3 + h.NoFightSec - 0.1; t += 0.25)
        {
            d = h.Tick(T0.AddSeconds(t), atWall, true, Sorted(C(1, 1010, 1000, atWall), C(2, 1000, 1030, atWall)), nav.Path);
            if (d.Act == OutdoorHunt.Act.Walk) walks++;
        }
        Check.Eq(walks, 0, "no 1 yd walks at the wall");
        Check.Eq(h.TargetId, 1, "still its target while combat has the time to take it");
        h.ClearNotes();
        d = h.Tick(T0.AddSeconds(3.5 + h.NoFightSec), atWall, true, Sorted(C(1, 1010, 1000, atWall), C(2, 1000, 1030, atWall)), nav.Path);
        Check.True(h.IsSkipped(1, T0.AddSeconds(30)), "left out after NoFightSec");
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.TargetId == 2, $"and on to the other one at once: {d.Act} {d.TargetId}");
        var notes = string.Join(" | ", h.PendingNotes);
        Check.True(h.PendingNotes.Count == 2 && h.PendingNotes[0].Contains("wall") && h.PendingNotes[1].StartsWith("next: 'Monster 2'"),
                   "both logged, the why first: " + notes);
    }

    private static void RightHere()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        var d = h.Tick(T0, me, true, Sorted(C(1, 1001.5, 1000, me)), nav.Path);
        Check.True(d.Act == OutdoorHunt.Act.Idle && d.TargetId == 1, $"1.5 yd away: no walk, it stands with it as the target ({d.Act} {d.TargetId})");
        d = h.Tick(T0.AddSeconds(1), me, true, Sorted(C(1, 1001.5, 1000, me)), nav.Path);
        Check.True(d.Act != OutdoorHunt.Act.Walk && !h.IsSkipped(1, T0.AddSeconds(1)), "a second: still waiting for combat");
        h.Tick(T0.AddSeconds(h.NoFightSec + 0.6), me, true, Sorted(C(1, 1001.5, 1000, me)), nav.Path);
        Check.True(h.IsSkipped(1, T0.AddSeconds(h.NoFightSec + 1)), "combat never took it: left out");
    }

    private static void WayBackOut()
    {
        var h = new OutdoorHunt();
        h.Start(T0, V(990, 1000));
        h.Track(V(1000, 1000), indoors: false);        // the last spot outdoors, at the doorway
        for (int x = 1001; x <= 1010; x++) h.Track(V(x, 1000), indoors: true);
        h.Track(V(1010, 1002), indoors: true);         // a fight took it up a side room...
        h.Track(V(1010, 1004), indoors: true);
        h.Track(V(1010, 1000.5), indoors: true);       // ...and back
        Check.Eq(h.WayInSteps, 6, "the side room is cut out of the way in (the door and 5 steps)");
        var me = V(1010, 1000.5);
        var d = h.Indoors(T0, me, routeDone: true);
        Check.Eq(d.Act, OutdoorHunt.Act.Walk, "walks out");
        var want = new[] { V(1008, 1000), V(1006, 1000), V(1004, 1000), V(1002, 1000), V(1000, 1000), V(1000 - h.OutPastYd, 1000) };
        Check.True(d.Corners!.SequenceEqual(want), "the way in backwards, then out past the doorway: " + string.Join(" ", d.Corners!.Select(c => $"{c.X:0.#},{c.Y:0.#}")));
        Check.True(h.WalkingOut && h.Note.Contains("back out"), "walking out: " + h.Note);
        Check.Eq(h.Indoors(T0.AddSeconds(2), V(1005, 1000), routeDone: false).Act, OutdoorHunt.Act.None, "on the way: keeps walking");
        h.Track(V(996, 1000), indoors: false);
        Check.False(h.WalkingOut, "outdoors again: done");
        Check.Eq(h.WayInSteps, 0, "and the way in forgotten");
    }

    private static void IndoorsWaits()
    {
        var never = new OutdoorHunt();
        never.Start(T0, V(1000, 1000));
        never.Track(V(1005, 1000), indoors: true);   // never seen outdoors (a reload inside, say)
        var d = never.Indoors(T0, V(1005, 1000), routeDone: true);
        Check.Eq(d.Act, OutdoorHunt.Act.Idle, "no way in known: waits");
        Check.True(never.Note.Contains("no way out known"), never.Note);

        var h = new OutdoorHunt();
        h.Start(T0, V(990, 1000));
        h.Track(V(1000, 1000), indoors: false);
        h.Track(V(1003, 1000), indoors: true);
        var t = T0;
        for (int i = 0; i < h.OutTriesMax; i++)
        {
            d = h.Indoors(t, V(1003, 1000), routeDone: true);
            Check.Eq(d.Act, OutdoorHunt.Act.Walk, $"walk out {i + 1}");
            t = t.AddSeconds(5);
            // It got to the end of the walk and the cell still says indoors.
            d = h.Indoors(t, d.Corners![^1], routeDone: true);
            if (i < h.OutTriesMax - 1) Check.Eq(d.Act, OutdoorHunt.Act.Walk, $"ended still inside: again ({i + 2})");
            t = t.AddSeconds(5);
            if (d.Act != OutdoorHunt.Act.Walk) break;
        }
        Check.Eq(d.Act, OutdoorHunt.Act.Idle, $"after {h.OutTriesMax} walks out ending inside: waits");
        Check.True(h.Note.Contains("ended still inside"), h.Note);
    }

    private static readonly DateTime T0 = new(2026, 10, 23, 16, 0, 0);
    private static OutdoorHunt.Vec V(double x, double y) => new(x, y, 0);

    private static OutdoorHunt.Candidate C(int id, double x, double y, OutdoorHunt.Vec me, int priority = 1, string? name = null)
        => new(id, name ?? $"Monster {id}", V(x, y), V(x, y).DistanceTo(me), priority);

    private sealed class Nav
    {
        public readonly Dictionary<(double, double), OutdoorHunt.PathResult> Overrides = new();
        /// <summary>A wall just in front of a monster: every walk to it ends at <c>end</c>, <c>gap</c> short.</summary>
        public readonly Dictionary<(double, double), (OutdoorHunt.Vec End, double Gap)> Walls = new();
        public int Calls;
        public bool Unavailable;

        public OutdoorHunt.PathResult Path(OutdoorHunt.Vec a, OutdoorHunt.Vec b)
        {
            Calls++;
            if (Unavailable) return new(OutdoorHunt.PathStatus.Unavailable, Array.Empty<OutdoorHunt.Vec>(), 0, 0);
            if (Overrides.TryGetValue((b.X, b.Y), out var o)) return o;
            if (Walls.TryGetValue((b.X, b.Y), out var w)) return new(OutdoorHunt.PathStatus.Partial, new[] { w.End }, a.DistanceTo(w.End), w.Gap);
            return new(OutdoorHunt.PathStatus.Ok, new[] { b }, a.DistanceTo(b), 0);
        }

        public void Around(double x, double y, double length) =>
            Overrides[(x, y)] = new(OutdoorHunt.PathStatus.Ok, new[] { V(x + 5, y + 5), V(x, y) }, length, 0);
    }

    private static List<OutdoorHunt.Candidate> Sorted(params OutdoorHunt.Candidate[] c) => c.OrderBy(x => x.Distance).ToList();

    private static void ShortestWalk()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        nav.Around(1010, 1000, 45);    // 10 yd away, a building between: a 45 yd walk
        var list = Sorted(C(1, 1010, 1000, me), C(2, 1000, 1018, me));
        var d = h.Tick(T0, me, true, list, nav.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.Walk, "walks");
        Check.Eq(d.TargetId, 2, "to the one 18 yd away in the open, not the one 10 yd away behind the building");
        Check.True(h.Note.Contains("18 yd walk"), "logged: " + h.Note);
        Check.Eq(d.Corners!.Last(), V(1000, 1018), "the walk ends at the monster");
    }

    private static void PriorityFirst()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        var d = h.Tick(T0, me, true, Sorted(C(1, 1005, 1000, me, priority: 1), C(2, 1030, 1000, me, priority: 5)), nav.Path);
        Check.Eq(d.TargetId, 2, "Priority 5 at 30 yd before Priority 1 at 5 yd");
    }

    private static void Unreachable()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        nav.Overrides[(1008, 1000)] = new(OutdoorHunt.PathStatus.NoPath, Array.Empty<OutdoorHunt.Vec>(), 0, 0);   // up a cliff
        nav.Overrides[(1012, 1000)] = new(OutdoorHunt.PathStatus.Partial, new[] { V(1004, 1000) }, 4, 8);      // in water: the walk ends 8 yd short
        nav.Overrides[(1020, 1000)] = new(OutdoorHunt.PathStatus.Partial, new[] { V(1018, 1000) }, 18, 2);     // on a rock: 2 yd short is close enough
        var list = Sorted(C(1, 1008, 1000, me), C(2, 1012, 1000, me), C(3, 1020, 1000, me));
        var d = h.Tick(T0, me, true, list, nav.Path);
        Check.Eq(d.TargetId, 3, "the one whose walk ends 2 yd short; the cliff and the water ones are left out");
        Check.True(h.IsSkipped(1, T0.AddSeconds(60)) && h.IsSkipped(2, T0.AddSeconds(60)), "left out for a while");
        Check.False(h.IsSkipped(1, T0.AddSeconds(h.SkipSec + 1)), "and tried again after SkipSec");
    }

    private static void Radius()
    {
        var start = V(1000, 1000);
        var h = new OutdoorHunt { RadiusYd = 50, RangeYd = 100 };
        h.Start(T0, start);
        var nav = new Nav();
        var me = V(1040, 1000);   // hunted out to 40 yd east
        var d = h.Tick(T0, me, true, Sorted(C(1, 1070, 1000, me), C(2, 1000, 1030, me)), nav.Path);
        Check.Eq(d.TargetId, 2, "the one 70 yd from the start is outside the 50 yd radius, even though it is closer");
        var any = new OutdoorHunt { RadiusYd = 0, RangeYd = 100 };
        any.Start(T0, start);
        Check.Eq(any.Tick(T0, me, true, Sorted(C(1, 1070, 1000, me), C(2, 1000, 1030, me)), nav.Path).TargetId, 1, "radius 0: anywhere, the nearest walk");
    }

    private static void Replan()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        Check.Eq(h.Tick(T0, me, true, Sorted(C(1, 1030, 1000, me)), nav.Path).Act, OutdoorHunt.Act.Walk, "walks to it");
        int calls = nav.Calls;
        // Walking; it moved 2 yd: not worth a new plan.
        var d = h.Tick(T0.AddSeconds(2), V(1005, 1000), false, Sorted(C(1, 1032, 1000, me)), nav.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.None, "it moved 2 yd: keep walking");
        Check.Eq(nav.Calls, calls, "no path asked for");
        // It moved 6 yd: plan again.
        d = h.Tick(T0.AddSeconds(3), V(1008, 1000), false, Sorted(C(1, 1036, 1000, me)), nav.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.Walk, "it moved 6 yd: a new walk");
        Check.Eq(d.Corners!.Last(), V(1036, 1000), "to where it is now");
        // Within a second: not again, even if it moves.
        d = h.Tick(T0.AddSeconds(3.5), V(1009, 1000), false, Sorted(C(1, 1045, 1000, me)), nav.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.None, "not twice in a second");
        // The walk ran out short of it (the monster still out of combat's reach): plan again.
        d = h.Tick(T0.AddSeconds(4.2), V(1030, 1000), true, Sorted(C(1, 1036, 1000, me)), nav.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.Walk, "the walk ran out: again");
        // Nothing changed for ReplanMaxSec: again anyway.
        d = h.Tick(T0.AddSeconds(4.2 + h.ReplanMaxSec + 0.1), V(1030, 1000), false, Sorted(C(1, 1036, 1000, me)), nav.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.Walk, "a while with no change: again");
    }

    // Tom 10-23: no walk back to the start ("it shouldn't bother").
    private static void WaitsWhereItIs()
    {
        var start = V(1000, 1000);
        var h = new OutdoorHunt { RadiusYd = 100 };
        h.Start(T0, start);
        var nav = new Nav();
        var me = V(1060, 1000);
        h.ClearNotes();
        var d = h.Tick(T0, me, true, new List<OutdoorHunt.Candidate>(), nav.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.Idle, "nothing to hunt, 60 yd from the start: waits there, no walk back");
        Check.Eq(nav.Calls, 0, "no path asked for");
        Check.True(h.Note.Contains("waiting here") && h.Note.Contains($"({h.RangeYd:0} yd)"), "says why, with Hunt Range: " + h.Note);
        h.Tick(T0.AddSeconds(2), me, true, new List<OutdoorHunt.Candidate>(), nav.Path);
        Check.Eq(h.PendingNotes.Count, 1, "said once, not every second");
        // A monster comes into range: hunted from there.
        d = h.Tick(T0.AddSeconds(4), me, true, Sorted(C(7, 1060, 1025, me)), nav.Path);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.TargetId == 7, $"a monster turns up: hunted ({d.Act} {d.TargetId})");
        Check.Eq(new OutdoorHunt().RadiusYd, 0.0, "Hunt Radius defaults to 0 (anywhere)");
    }

    // Tom 10-23: "/mt opt set" gave no answer, and "/ra hunt status" toggled the hunt (the command read
    // the word "hunt" as its argument). The /ra hunt settings answer, and status/stop don't start it.
    private static void Commands()
    {
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        RynthCore.RynthAiHostTests.Fakes.FakeHost.Reset();
        var host = RynthCore.RynthAiHostTests.Fakes.FakeHost.Create();
        var p = new RynthAiPlugin();
        typeof(RynthCore.PluginCore.RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { RynthCore.RynthAiHostTests.Fakes.FakeHost.LastApi });
        var dash = new LegacyDashboardRenderer(host);
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(p, dash);
        var chat = RynthCore.RynthAiHostTests.Fakes.FakeHost.Chat;
        void Ra(string line)
        {
            chat.Clear();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(p, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }
        string All() => string.Join(" | ", chat);

        Ra("/ra hunt range 150");
        Check.Eq(dash.Settings.OutdoorHuntRange, 150f, "range set");
        Check.True(All().Contains("Outdoor hunt set") && All().Contains("150 yd (Hunt Range)"), "and said: " + All());
        Ra("/ra hunt range 2000");
        Check.Eq(dash.Settings.OutdoorHuntRange, 2000f, "2000 is allowed (Tom 10-23)");
        Ra("/ra hunt range 5000");
        Check.Eq(dash.Settings.OutdoorHuntRange, (float)RynthAiPlugin.OutdoorHuntRangeMax, "capped at the most");
        Check.True(All().Contains("outside"), "and says it was capped: " + All());
        Ra("/ra hunt radius 0");
        Check.True(dash.Settings.OutdoorHuntRadius == 0 && All().Contains("anywhere"), "radius 0 = anywhere: " + All());
        Ra("/ra hunt range");
        Check.True(All().Contains("Hunt Range") && All().Contains("/ra hunt range <yd>"), "no number: shows the setting: " + All());
        Ra("/ra hunt status");
        Check.False(p.HuntActive, "status doesn't start it");
        Check.True(All().Contains("Outdoor hunt: off") && All().Contains("Hunt Range"), "status says off, with the distances: " + All());
        Ra("/ra hunt stop");
        Check.False(p.HuntActive, "stop doesn't start it");
        Ra("/ra hunt nonsense");
        Check.True(All().Contains("/ra hunt range <yd>"), "anything else: the usage: " + All());
        Ra("/ra hunt panel");
        Check.True((bool)typeof(RynthAiPlugin).GetField("_huntPanelOpen", Any)!.GetValue(p)!, "panel opens the window");
    }

    private static void NextAndNoPathfinder()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        Check.Eq(h.Tick(T0, me, true, Sorted(C(1, 1010, 1000, me), C(2, 1030, 1000, me)), nav.Path).TargetId, 1, "the nearer first");
        // It died (combat killed it; it is gone from the list): the next one.
        var d = h.Tick(T0.AddSeconds(10), V(1009, 1000), true, Sorted(C(2, 1030, 1000, V(1009, 1000))), nav.Path);
        Check.Eq(d.TargetId, 2, "killed: on to the next");
        var none = new OutdoorHunt();
        none.Start(T0, me);
        var gone = new Nav { Unavailable = true };
        d = none.Tick(T0, me, true, Sorted(C(1, 1010, 1000, me)), gone.Path);
        Check.Eq(d.Act, OutdoorHunt.Act.Idle, "RynthNav missing: stands");
        Check.Eq(d.Why, "no pathfinder", "and says why");
    }

    private static void FarRound()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt { RangeYd = 60 };
        h.Start(T0, me);
        var nav = new Nav();
        nav.Around(1012, 1000, 143);   // a buff bot inside a building: 12 yd away, a 143 yd walk
        var d = h.Tick(T0, me, true, Sorted(C(1, 1012, 1000, me), C(2, 1040, 1000, me)), nav.Path);
        Check.Eq(d.TargetId, 2, "the 143 yd walk (over twice Hunt Range) is too far round: the one 40 yd away instead");
        Check.True(h.IsSkipped(1, T0.AddSeconds(30)), "and it is left out for a while (no choosing it again every second)");
        Check.True(h.Note.Contains("40 yd walk"), "logged: " + h.Note);
        // Walking round to the 40 yd one took the character 70 yd from it (past Hunt Range): still its target.
        var far = V(970, 1000);
        var c2 = new OutdoorHunt.Candidate(2, "Monster 2", V(1040, 1000), 70, 1);
        d = h.Tick(T0.AddSeconds(2), far, false, new List<OutdoorHunt.Candidate> { c2 }, nav.Path);
        Check.True(d.Act != OutdoorHunt.Act.Idle && h.TargetId == 2, $"kept at 70 yd (Hunt Range 60): {d.Act}, target {h.TargetId}");
    }

    private static void Settings()
    {
        var s = new LegacyUiSettings();
        // 2026-10-25 (Tom: "the entire radar range"): 192, the radar's default range (was 60).
        Check.Eq(s.OutdoorHuntRange, 192f, "Hunt Range defaults to 192 yd, the radar's range");
        Check.Eq(new OutdoorHunt().RangeYd, 192.0, "and the hunt's own default too");
        Check.Eq(s.OutdoorHuntRadius, 0f, "Hunt Radius defaults to 0 (anywhere)");
        var ctx = RynthAiJsonContext.Default.LegacyUiSettings;
        var saved = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings { OutdoorHuntRange = 90, OutdoorHuntRadius = 250 }, ctx), ctx)!;
        var live = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { saved, live });
        Check.True(live.OutdoorHuntRange == 90 && live.OutdoorHuntRadius == 250, "saved and copied");
        // A file saved before 10-23 evening still has "OutdoorHuntReturn": it loads (the setting is gone).
        var old = System.Text.Json.JsonSerializer.Deserialize("{\"OutdoorHuntRange\":70,\"OutdoorHuntRadius\":2000,\"OutdoorHuntReturn\":true}", ctx);
        Check.True(old != null && old.OutdoorHuntRange == 70 && old.OutdoorHuntRadius == 2000, "an old file with Back To Start loads");
    }
}
