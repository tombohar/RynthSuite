using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-26: Show Line of Sight (ShowLosLine, off by default; /ra losline [on|off]; Settings > Combat >
// Line Of Sight). A line in the world from the character to the combat target in the colour of the
// verdict the target scan's raycast gave it (no second raycast): green clear, red blocked with a
// yellow marker where it hit, dashed green when clear without a geometry test. Why: a melee character
// at 0xB470 swung at monsters 4.3-4.7 m off behind a wall that the scan's raycast called clear.
internal static class LosLineTests
{
    private const uint Player = 0x50000E51;
    private const uint Lb = 0xB4700000;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("los line: the setting is off by default, saved, copied, sent and set like the others", Setting);
        r.Add("los line: /ra losline [on|off] always answers with the state or the usage", Command);
        r.Add("los line: the scan keeps each monster's verdict while the setting is on, none while off", ScanCache);
        r.Add("los line: the raycast's straight line and arc give the nearest point they hit", HitPoints);
        r.Add("los line: the overlay draws the scan's verdict: green clear, red blocked with a marker, dashed untested", Overlay);
        r.Add("los line: stale verdicts, the setting off or portal space draw nothing", OverlayQuiet);
    }

    // ── The setting ─────────────────────────────────────────────────────────

    private static void Setting()
    {
        Check.False(new LegacyUiSettings().ShowLosLine, "off by default");
        var old = System.Text.Json.JsonSerializer.Deserialize("{\"MonsterRange\":7}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && !old.ShowLosLine, "a profile saved before the setting loads it off");
        var saved = System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings { ShowLosLine = true }, RynthAiJsonContext.Default.LegacyUiSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(saved, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(saved.Contains("ShowLosLine") && back != null && back.ShowLosLine, "saved and loaded with the profile");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { ShowLosLine = true }, dst });
        Check.True(dst.ShowLosLine, "a profile's on is copied");
        var dst2 = new LegacyUiSettings { ShowLosLine = true };
        copy.Invoke(null, new object[] { new LegacyUiSettings(), dst2 });
        Check.False(dst2.ShowLosLine, "a profile's off is copied");

        var s = new LegacyUiSettings { ShowLosLine = true };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", Any)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = System.Text.Json.JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.ShowLosLine == true, "sent to the engine's Settings window");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.ShowLosLine, "an engine without the row leaves it alone");
        dash.ApplySettingsJson("{\"ShowLosLine\":false}");
        Check.False(s.ShowLosLine, "the row's untick sets it off");
        dash.ApplySettingsJson("{\"ShowLosLine\":true}");
        Check.True(s.ShowLosLine, "the row's tick sets it on");

        var live = new LegacyUiSettings();
        var map = ExpressionTests.Engine(settings: live).BuildSettingsMapPublic();
        Check.True(map.TryGetValue("ShowLosLine", out var entry), "ShowLosLine is in the settings map");
        Check.Eq(entry.Get(), "0", "reads off");
        entry.Set("1");
        Check.True(live.ShowLosLine, "and sets on");
    }

    // ── /ra losline ─────────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();

        public Bot()
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(P, dash);
        }

        public void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(P, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }

        public string LastChat => FakeHost.Chat.Count > 0 ? FakeHost.Chat[^1] : "";
    }

    private static void Command()
    {
        FakeHost.Reset();
        FakeHost.Create(Player);
        var bot = new Bot();

        bot.Ra("/ra losline");
        Check.True(bot.S.ShowLosLine, "no argument toggles: on");
        Check.True(bot.LastChat.Contains("Show Line of Sight: on"), bot.LastChat);
        int before = FakeHost.Chat.Count;
        bot.Ra("/ra losline");
        Check.False(bot.S.ShowLosLine, "again: off");
        Check.True(FakeHost.Chat.Count > before && bot.LastChat.Contains("Show Line of Sight: off"), bot.LastChat);

        bot.Ra("/ra losline on");
        Check.True(bot.S.ShowLosLine, "on");
        Check.True(bot.LastChat.Contains("Show Line of Sight: on") && bot.LastChat.Contains("green"), "on says what the colours mean: " + bot.LastChat);
        bot.Ra("/ra losline on");
        Check.True(bot.S.ShowLosLine && bot.LastChat.Contains("Show Line of Sight: on"), "on again answers too: " + bot.LastChat);
        bot.Ra("/ra losline off");
        Check.False(bot.S.ShowLosLine, "off");
        Check.True(bot.LastChat.Contains("Show Line of Sight: off"), bot.LastChat);

        bot.Ra("/ra losline banana");
        Check.False(bot.S.ShowLosLine, "an unknown argument changes nothing");
        Check.True(bot.LastChat.Contains("Usage: /ra losline [on|off]") && bot.LastChat.Contains("now off"), "the usage and the state: " + bot.LastChat);

        bot.S.EnableRaycasting = false;
        bot.Ra("/ra losline on");
        Check.True(bot.LastChat.Contains("Enable Raycasting is off"), "with raycasting off it says every line is dashed: " + bot.LastChat);

        FakeHost.Chat.Clear();
        typeof(RynthAiPlugin).GetMethod("HandleHelpCommand", Any)!.Invoke(bot.P, null);
        Check.True(FakeHost.Chat.Any(c => c.Contains("/ra losline [on|off]")), "a help line");
    }

    // ── The scan's verdict cache ─────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { MonsterRange = 20, ShowLosLine = true };
        public CombatManager C = null!;
        public RynthCoreHost Host;
        private readonly List<uint> _ids = new();
        private uint _next = 0x80000701;

        public Rig(bool nav3D = true, float playerX = 100f)
        {
            FakeHost.Reset();
            FakeHost.Nav3DCalls = nav3D;
            Place(Player, "Tester", Lb | 0x29, playerX, 100, 50, creature: false);
            FakeHost.PlayerPose = (Lb | 0x29, playerX, 100f, 50f);
        }

        private static void Place(uint id, string name, uint cell, float x, float y, float z, bool creature = true)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (cell, x, y, z);
            if (creature) FakeHost.ItemTypes[id] = 0x10;
        }

        public uint Mob(string name, float north, float east, float up = 0, uint cell = Lb | 0x29)
        {
            uint id = _next++;
            Place(id, name, cell, 100 + east, 100 + north, 50 + up);
            _ids.Add(id);
            return id;
        }

        public uint MobAt(string name, uint cell, float x, float y, float z)
        {
            uint id = _next++;
            Place(id, name, cell, x, y, z);
            _ids.Add(id);
            return id;
        }

        public void Scan(Func<int, bool>? blocked, int target = 0)
        {
            Host = FakeHost.Create();
            var cache = FakeHost.MakeCache(Host, Player, _ids, _ids.ToDictionary(i => i, _ => 1f));
            C = new CombatManager(Host, S, cache);
            C.SetPlayerId(Player);
            if (blocked != null) C.LosBlockedOverride = blocked;
            if (target != 0) C.activeTargetId = target;
            C.ScanNearbyTargets();
        }
    }

    private static void ScanCache()
    {
        var rig = new Rig();
        uint a = rig.Mob("Clear Drudge", 4.5f, 0);
        uint b = rig.Mob("Walled Drudge", 4.3f, 1);
        rig.Scan(id => id == unchecked((int)b), target: unchecked((int)a));
        var c = rig.C;
        Check.True(c.TryGetLosVerdict(unchecked((int)a), out var va), "the clear one has a verdict");
        Check.False(va.Blocked, "clear");
        Check.True(va.Tested, "tested (the scan gave a verdict)");
        Check.Near(va.Distance, 4.5, 1e-3, "its distance");
        Check.True(c.TryGetLosVerdict(unchecked((int)b), out var vb) && vb.Blocked, "the walled one: blocked");
        Check.False(c.ScannedTargets.Any(t => t.Id == unchecked((int)b)), "and left out of the targets, as before");
        Check.True(c.ScannedTargets.Any(t => t.Id == unchecked((int)a)), "the clear one is a target, as before");
        Check.Eq(c.LosLineTargetId, unchecked((int)a), "the line goes to the combat target");

        // Off: nothing kept (the scan pays nothing for the overlay).
        var off = new Rig();
        off.S.ShowLosLine = false;
        uint a2 = off.Mob("Clear Drudge", 4.5f, 0);
        off.Scan(_ => false, target: unchecked((int)a2));
        Check.Eq(off.C.LosVerdicts.Count, 0, "setting off: no verdicts");
        Check.Eq(off.C.ScannedTargets.Count, 1, "the scan itself unchanged");

        // Each scan holds only its own verdicts: one that died since is gone.
        rig.S.MonsterRange = 20;
        FakeHost.Positions.Remove(b);
        FakeHost.Names.Remove(b);
        typeof(CombatManager).GetField("_lastScanTime", Any)!.SetValue(c, DateTime.MinValue);
        c.ScanNearbyTargets();
        Check.False(c.TryGetLosVerdict(unchecked((int)b), out _), "the next scan's cache doesn't keep a monster it didn't test");

        // Melee within 4 m fights through a blocked line: the verdict stays blocked, marked as fought anyway.
        var melee = new Rig();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = 2;   // melee
        uint m = melee.Mob("Close Drudge", 3f, 0);
        melee.Scan(_ => true, target: unchecked((int)m));
        Check.True(melee.C.TryGetLosVerdict(unchecked((int)m), out var vm) && vm.Blocked && vm.MeleeThrough,
            "blocked, and melee fights it anyway (" + (vm.Blocked ? "blocked" : "clear") + (vm.MeleeThrough ? ", through" : "") + ")");
        Check.True(melee.C.ScannedTargets.Any(t => t.Id == unchecked((int)m)), "still a target, as before");

        // No raycast (not loaded, or Enable Raycasting off): clear without a test.
        var none = new Rig();
        uint n = none.Mob("Untested Drudge", 6f, 0);
        none.Scan(null, target: unchecked((int)n));
        Check.True(none.C.TryGetLosVerdict(unchecked((int)n), out var vn) && !vn.Blocked && !vn.Tested, "no raycast: clear, untested");
    }

    // ── Hit points from the raycast ──────────────────────────────────────────

    private static BoundingVolume Box(float x0, float y0, float z0, float x1, float y1, float z1)
        => new() { Type = BoundingVolume.VolumeType.AxisAlignedBox, Min = new Vector3(x0, y0, z0), Max = new Vector3(x1, y1, z1),
                   Center = new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), Dimensions = new Vector3(x1 - x0, y1 - y0, z1 - z0) };

    private static void HitPoints()
    {
        // Two walls across the line, the far one listed first: the nearer is reported.
        var walls = new List<BoundingVolume> { Box(7f, -5f, 0f, 7.2f, 5f, 4f), Box(4f, -5f, 0f, 4.2f, 5f, 4f) };
        var from = new Vector3(0f, 0f, 1f);
        var to = new Vector3(10f, 0f, 1f);
        Check.True(RaycastEngine.IsLinearPathBlocked(from, to, walls, false), "blocked (the verdict alone)");
        Check.True(RaycastEngine.IsLinearPathBlocked(from, to, walls, false, out Vector3 hit), "blocked (with the point)");
        Check.Near(hit.X, 4f, 1e-3, "the nearer wall's face");
        Check.Near(hit.Y, 0f, 1e-3, "on the line");
        Check.Near(hit.Z, 1f, 1e-3, "at chest height");
        Check.True(RaycastEngine.IsLinearPathBlocked(from, to, walls, true, out Vector3 multi), "multi-ray: blocked");
        Check.Near(multi.X, 4f, 1e-3, "multi-ray: the centre ray's hit");
        var open = new List<BoundingVolume> { Box(4f, 3f, 0f, 4.2f, 5f, 4f) };
        Check.False(RaycastEngine.IsLinearPathBlocked(from, to, open, false, out Vector3 none), "a wall beside the line: clear");
        Check.True(none == Vector3.Zero, "and no point");

        // An arc into a ceiling reports where it hits.
        var ceiling = new List<BoundingVolume> { Box(-1f, -5f, 2.0f, 30f, 5f, 2.2f) };
        var aim = new Vector3(25f, 0f, 1f);
        bool arcBlocked = RaycastEngine.IsBallisticArcBlocked(from, aim, 18f, 0.5f, ceiling, out var arc);
        Check.True(arcBlocked && arc.Blocked && !arc.OutOfReach, "the arc rises into the ceiling");
        Check.Near(arc.HitPoint.Z, 2.0f, 0.05, "the hit is on the ceiling's underside");
        Check.True(arc.HitPoint.X > 0.5f && arc.HitPoint.X < 25f, "between the two (x " + arc.HitPoint.X.ToString("0.00") + ")");
        Check.Near(arc.HitAlong, arc.HitPoint.X, 1e-3, "the same place HitAlong names");
    }

    // ── The overlay ──────────────────────────────────────────────────────────

    private static (float X, float Y, float Z) Chest(float x, float y, float z) => (x, z + 1f, y);

    private static void Overlay()
    {
        var rig = new Rig();
        uint a = rig.Mob("Target", 4.5f, 0);
        uint b = rig.Mob("Other Blocked", 0, 6f);
        uint c = rig.Mob("Other Clear", -8f, 0);
        rig.Scan(id => id == unchecked((int)b), target: unchecked((int)a));
        var ov = new LosLineOverlay(rig.Host, () => rig.S, () => rig.C);
        ov.Submit();

        var main = FakeHost.Nav3DLines.Where(l => l.Color == LosLineOverlay.ColorClear).ToList();
        Check.Eq(main.Count, 1, "one solid green line to the target (the scan tested it clear)");
        var me = Chest(100, 100, 50);
        var tgt = Chest(100, 104.5f, 50);
        Check.True(main.Count == 1 && Math.Abs(main[0].X1 - me.X) < 1e-3 && Math.Abs(main[0].Y1 - me.Y) < 1e-3 && Math.Abs(main[0].Z1 - me.Z) < 1e-3,
            "from the character's chest (x east, y height, z north)");
        Check.True(main.Count == 1 && Math.Abs(main[0].X2 - tgt.X) < 1e-3 && Math.Abs(main[0].Y2 - tgt.Y) < 1e-3 && Math.Abs(main[0].Z2 - tgt.Z) < 1e-3,
            "to the target's chest");
        Check.True(main.Count == 1 && main[0].Thick == LosLineOverlay.MainThick, "the main line's width");
        Check.Eq(FakeHost.Nav3DLines.Count(l => l.Color == LosLineOverlay.ColorOtherBlocked), 1, "a thin dim red line to the blocked monster");
        Check.Eq(FakeHost.Nav3DLines.Count(l => l.Color == LosLineOverlay.ColorOtherClear), 1, "a thin dim green line to the other clear one");
        Check.Eq(FakeHost.Nav3DLines.Count, 3, "nothing else");
        Check.Eq(FakeHost.Nav3DRings.Count, 0, "no marker when clear");
        Check.Eq(ov.LastLines, 3, "three lines counted");

        // The target blocked, with a hit point (global metres): red, and the yellow marker there.
        FakeHost.Nav3DLines.Clear(); FakeHost.Nav3DRings.Clear(); FakeHost.Nav3DTriangles.Clear();
        float gx = 0xB4 * 192f, gy = 0x70 * 192f;
        rig.C.SetLosVerdictForTests(unchecked((int)a), new CombatManager.LosVerdict
        {
            Blocked = true, Tested = true, HasHit = true, HitX = gx + 100f, HitY = gy + 102f, HitZ = 51f, Distance = 4.5, At = RynthClock.Now,
        });
        ov.Submit();
        Check.Eq(FakeHost.Nav3DLines.Count(l => l.Color == LosLineOverlay.ColorBlocked), 1, "one solid red line");
        Check.Eq(FakeHost.Nav3DLines.Count(l => l.Color == LosLineOverlay.ColorClear), 0, "no green one");
        Check.Eq(FakeHost.Nav3DRings.Count, 1, "a marker ring");
        var ring = FakeHost.Nav3DRings.FirstOrDefault();
        Check.True(Math.Abs(ring.X - 100f) < 1e-3 && Math.Abs(ring.Z - 102f) < 1e-3 && ring.Color == LosLineOverlay.ColorHit,
            $"at the hit point in the player's frame (ring {ring.X:0.00},{ring.Y:0.00},{ring.Z:0.00})");
        Check.True(ring.Y < 51f && ring.Y > 50f, "the ring's post spans the hit's height");
        Check.Eq(FakeHost.Nav3DTriangles.Count, 8, "and a small yellow diamond (8 faces)");
        Check.True(FakeHost.Nav3DTriangles.All(t => t.Color == LosLineOverlay.ColorHit), "yellow");

        // Blocked but no hit point (an arc out of reach): red, no marker.
        FakeHost.Nav3DLines.Clear(); FakeHost.Nav3DRings.Clear(); FakeHost.Nav3DTriangles.Clear();
        rig.C.SetLosVerdictForTests(unchecked((int)a), new CombatManager.LosVerdict { Blocked = true, Tested = true, Distance = 4.5, At = RynthClock.Now });
        ov.Submit();
        Check.Eq(FakeHost.Nav3DLines.Count(l => l.Color == LosLineOverlay.ColorBlocked), 1, "red");
        Check.Eq(FakeHost.Nav3DRings.Count + FakeHost.Nav3DTriangles.Count, 0, "no marker without a point");

        // Clear without a geometry test: dashed green.
        FakeHost.Nav3DLines.Clear();
        rig.C.SetLosVerdictForTests(unchecked((int)a), new CombatManager.LosVerdict { Blocked = false, Tested = false, Distance = 4.5, At = RynthClock.Now });
        ov.Submit();
        var dashes = FakeHost.Nav3DLines.Where(l => l.Color == LosLineOverlay.ColorClear).ToList();
        Check.True(dashes.Count >= 2, "dashed: several short green pieces (" + dashes.Count + ")");
        float covered = dashes.Sum(d => MathF.Sqrt((d.X2 - d.X1) * (d.X2 - d.X1) + (d.Y2 - d.Y1) * (d.Y2 - d.Y1) + (d.Z2 - d.Z1) * (d.Z2 - d.Z1)));
        Check.True(covered < 4.5f * 0.7f && covered > 4.5f * 0.4f, "with gaps between them (" + covered.ToString("0.00") + " of 4.5 m drawn)");

        // A target in the next landblock east: drawn in this landblock's frame.
        var edge = new Rig(playerX: 190f);
        uint e = edge.MobAt("Next Block", ((Lb + 0x01000000) & 0xFFFF0000) | 0x01, 2f, 100f, 50f);
        edge.Scan(_ => false, target: unchecked((int)e));
        var ov2 = new LosLineOverlay(edge.Host, () => edge.S, () => edge.C);
        ov2.Submit();
        var l2 = FakeHost.Nav3DLines.FirstOrDefault(l => l.Color == LosLineOverlay.ColorClear);
        Check.Near(l2.X1, 190f, 1e-3, "from the character, 190 m east in this landblock");
        Check.Near(l2.X2, 194f, 1e-3, "to 192 m + 2 m east of this landblock's origin");
        Check.Near(l2.Z2, 100f, 1e-3, "same north");
    }

    private static void OverlayQuiet()
    {
        var rig = new Rig();
        uint a = rig.Mob("Target", 4.5f, 0);
        rig.Scan(_ => false, target: unchecked((int)a));
        var ov = new LosLineOverlay(rig.Host, () => rig.S, () => rig.C);

        ov.Clock = () => RynthClock.Now.AddSeconds(5);
        ov.Submit();
        Check.Eq(FakeHost.Nav3DLines.Count, 0, "verdicts from a scan 5 s ago aren't drawn (no scan lately)");

        ov.Clock = () => RynthClock.Now;
        rig.S.ShowLosLine = false;
        ov.Submit();
        Check.Eq(FakeHost.Nav3DLines.Count, 0, "setting off: nothing");

        rig.S.ShowLosLine = true;
        FakeHost.PlayerPose = (0x00000000, 0f, 0f, 0f);
        ov.Submit();
        Check.Eq(FakeHost.Nav3DLines.Count, 0, "portal space: nothing");

        FakeHost.PlayerPose = (Lb | 0x29, 100f, 100f, 50f);
        ov.Submit();
        Check.Eq(FakeHost.Nav3DLines.Count, 1, "back in the world: the line");

        // No Nav3D in the engine: nothing, and no error.
        var plain = new Rig(nav3D: false);
        uint p = plain.Mob("Target", 4.5f, 0);
        plain.Scan(_ => false, target: unchecked((int)p));
        new LosLineOverlay(plain.Host, () => plain.S, () => plain.C).Submit();
        Check.Eq(FakeHost.Nav3DLines.Count, 0, "an engine without Nav3D: nothing drawn");
    }
}
