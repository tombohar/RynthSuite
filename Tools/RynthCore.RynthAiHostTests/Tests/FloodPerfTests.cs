using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Object flood (2026-10-02, Lucy, a personal copy of Matron Hive South, landblock 0x6346): the
// server deleted 585 objects of the original dungeon and spawned the copy's population, so the
// client took roughly 1000 creates and 600 deletes within seconds, with a few hundred Olthoi
// around. These tests pump the same shape through RynthAi's per-object code on the fake host
// and print what it costs:
//   * the object cache: creates, deletes before classification, health updates for creatures
//     the cache hasn't classified yet, names/positions arriving a few ticks late (the engine's
//     snapshots lag a create), and the periodic reclassify pass;
//   * the combat target scan (20 Hz in game) with LOS against the real 0x6346 dungeon geometry
//     when the dat files are on this PC (C:\Turbine\Asheron's Call), creature spots taken from
//     RynthCore.LosProof's pairs-6346.txt;
//   * the radar snapshot (30 Hz while a radar or dungeon map is open).
// Each Log call costs 350 us here (FakeHost.LogCostMicros), what the engine's synchronous
// open-append-close log write costs per line on the client PC, so log volume shows up as time.
// The checks pin behaviour (every creature ends up a Monster, deleted objects are gone, the
// scan finds the same targets) plus bounds on log lines and time per tick; the timings print.
internal static class FloodPerfTests
{
    private const uint Player = 0x50000C01;
    private const uint Lb = 0x63460000;
    private const uint PlayerCell = Lb | 0x0367;   // Lucy's cell in the incident
    private const string AcDir = @"C:\Turbine\Asheron's Call";
    private const int LogCostUs = 350;

    public static void Register(Runner r)
    {
        r.Add("flood: 1000 creates, 585 deletes, 300 creatures through the object cache", CacheFlood);
        r.Add("flood: combat target scan, 300 creatures (LOS on the real 0x6346 dungeon if the dats are here)", CombatScan);
        r.Add("flood: radar snapshot, 300 creatures and 400 world objects", RadarSnapshot);
    }

    // ── Object cache ──────────────────────────────────────────────────────────

    private static void CacheFlood()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (PlayerCell, 50f, 50f, -24f);
        var rng = new Random(1002);

        var creatures = new List<uint>();
        var late = new List<uint>();          // name + position arrive 3 ticks after the create
        var lateType = new List<uint>();      // item type arrives 6 ticks after the create
        for (int i = 0; i < 300; i++)
        {
            uint id = 0x8000B000u + (uint)i;
            creatures.Add(id);
            if (i % 2 == 0)
                Reveal(id, "Olthoi Swarm Nymph", rng, creature: true);
            else
                late.Add(id);
            if (i % 4 == 1) lateType.Add(id);
        }
        var statics = new List<uint>();
        for (int i = 0; i < 400; i++)
        {
            uint id = 0x76346000u + (uint)i;
            statics.Add(id);
            Reveal(id, i % 50 == 0 ? "Portal to Somewhere" : "Hive Wall Torch", rng, creature: false);
            FakeHost.ItemTypes[id] = i % 50 == 0 ? 0x10000u : 0x80u;
            FakeHost.NotAttackable.Add(id);
        }
        var items = new List<uint>();
        for (int i = 0; i < 300; i++)
        {
            uint id = 0xC0001000u + (uint)i;
            items.Add(id);
            FakeHost.Names[id] = "Mana Stone";
            FakeHost.ItemTypes[id] = 0x80000u;
            FakeHost.NotAttackable.Add(id);
        }
        // The original dungeon's objects: deleted, never created in this session.
        var gone = Enumerable.Range(0, 585).Select(i => 0x8000C000u + (uint)i).ToList();

        var host = FakeHost.Create(Player);

        // Warm-up on a throwaway cache so the first measured tick isn't JIT compilation (the
        // plugin is warm long before a flood in game).
        var warm = new WorldObjectCache(host);
        warm.SetPlayerId(Player);
        foreach (uint id in creatures.Take(40).Concat(statics.Take(20)).Concat(items.Take(20))) warm.OnCreateObject(id);
        foreach (uint id in creatures.Take(40)) warm.OnUpdateHealth(id, 1f);
        foreach (uint id in gone.Take(20)) warm.OnDeleteObject(id);
        for (int i = 0; i < 12; i++) { if (i == 5) ForceReclassify(warm); warm.Tick(); }
        FakeHost.Logs.Clear();

        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        FakeHost.LogCostMicros = LogCostUs;

        var sw = Stopwatch.StartNew();
        double maxTickMs = 0;
        int ticks = 0;

        // Tick 0: the flood lands in one pump loop (the engine drains its queues in full),
        // with health already arriving for half the creatures.
        foreach (uint id in creatures.Concat(statics).Concat(items))
            cache.OnCreateObject(id);
        foreach (uint id in creatures.Where((_, i) => i % 2 == 1))
            cache.OnUpdateHealth(id, 1f);

        for (; ticks < 90; ticks++)
        {
            if (ticks == 3)
                foreach (uint id in late) Reveal(id, "Olthoi Swarm Gardener", rng, creature: false);
            if (ticks == 6)
                foreach (uint id in lateType) FakeHost.ItemTypes[id] = 0x10;
            if (ticks == 10)
                foreach (uint id in gone) cache.OnDeleteObject(id);
            if (ticks == 12)
                foreach (uint id in creatures.Where((_, i) => i % 2 == 0)) cache.OnUpdateHealth(id, 1f);
            if (ticks == 20 || ticks == 50)
                ForceReclassify(cache);

            long t0 = Stopwatch.GetTimestamp();
            int logsBefore = FakeHost.Logs.Count;
            cache.Tick();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > maxTickMs) maxTickMs = ms;
            if (ms > 10 && Environment.GetEnvironmentVariable("FLOOD_VERBOSE") == "1")
                Console.WriteLine($"          tick {ticks}: {ms:0.0} ms, {FakeHost.Logs.Count - logsBefore} log lines");
        }
        sw.Stop();
        FakeHost.LogCostMicros = 0;

        var stats = cache.GetStats();
        int logLines = FakeHost.Logs.Count;
        int traceLines = FakeHost.Logs.Count(l => l.StartsWith("[ClassifyTrace]"));
        int diagLines = FakeHost.Logs.Count(l => l.StartsWith("[ReclassifyDiag]"));
        Console.WriteLine($"          object cache: {sw.Elapsed.TotalMilliseconds:0} ms for the flood + {ticks} ticks, " +
                          $"slowest tick {maxTickMs:0.0} ms; {logLines} log lines ({traceLines} ClassifyTrace, {diagLines} ReclassifyDiag); " +
                          $"cache: {stats.Total} objects, {stats.Creatures} creatures, {stats.Landscape} landscape, {stats.Inventory} inventory, {stats.Pending} pending");

        // Behaviour: every creature a Monster, the statics on the landscape, the items in the
        // inventory, nothing deleted left behind, nothing stuck in the queue.
        int monsters = creatures.Count(id => cache[unchecked((int)id)]?.ObjectClass == AcObjectClass.Monster);
        Check.Eq(monsters, 300, "all 300 creatures classified Monster");
        Check.Eq(stats.Creatures, 300, "300 creatures in the cache");
        var landscapeIds = cache.GetLandscapeObjects().Select(o => unchecked((uint)o.Id)).ToHashSet();
        Check.True(statics.All(landscapeIds.Contains), "every static object on the landscape");
        Check.Eq(cache.GetLandscapeObjects().Count(o => o.ObjectClass == AcObjectClass.Portal), 8, "the 8 portals classified Portal");
        Check.Eq(stats.Inventory, 300, "300 pack items in the inventory");
        Check.Eq(stats.Pending, 0, "nothing left pending");
        Check.False(gone.Any(landscapeIds.Contains), "no deleted object in the cache");

        // Cost: the log stays a summary, and no tick stalls the pump.
        Check.True(logLines <= 120, $"log lines bounded: {logLines} (at most 120)");
        Check.True(maxTickMs < 50, $"slowest tick {maxTickMs:0.0} ms (under 50 ms)");
        // The summary still carries what the per-object lines said.
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[ReclassifyDiag] last 2 s:") && l.Contains("DELETE-BEFORE-CLASSIFY 585 (seenCreate 0)")),
            "one summary line counts all 585 deletes before classification");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[ReclassifyDiag] last 2 s:") && l.Contains("HEALTHADD-RESCUE")),
            "health-before-classify rescues are counted in a summary");
        Check.True(FakeHost.Logs.Any(l => l.StartsWith("[ClassifyTrace] last 2 s:") && l.Contains(" pos ")),
            "classify attempts are counted by step in a summary");
        if (Environment.GetEnvironmentVariable("FLOOD_VERBOSE") == "1")
            foreach (string l in FakeHost.Logs.Where(l => l.Contains(" last 2 s:"))) Console.WriteLine("          " + l);
    }

    private static void Reveal(uint id, string name, Random rng, bool creature)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (PlayerCell, 10f + (float)rng.NextDouble() * 80f, 10f + (float)rng.NextDouble() * 80f, -24f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static void ForceReclassify(WorldObjectCache cache)
    {
        FieldInfo? f = typeof(WorldObjectCache).GetField("_lastReclassifyTime", BindingFlags.Instance | BindingFlags.NonPublic);
        f?.SetValue(cache, DateTime.MinValue);
    }

    // ── Combat target scan ────────────────────────────────────────────────────

    private static void CombatScan()
    {
        FakeHost.Reset();
        var spots = LoadDungeonSpots(out (uint Cell, float X, float Y, float Z) me);
        FakeHost.PlayerPose = me;
        FakeHost.Names[Player] = "Lucy McJuicy";
        FakeHost.Positions[Player] = me;

        // 300 Olthoi: 60 at dungeon spots within 45 m of Lucy (in range, LOS-checked), the rest
        // of the hive further out (seen by the scan, out of range).
        var ids = new List<uint>();
        var health = new Dictionary<uint, float>();
        var far = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            uint id = 0x8000B000u + (uint)i;
            ids.Add(id);
            FakeHost.Names[id] = i % 3 == 0 ? "Olthoi Swarm Nymph" : "Olthoi Swarm Gardener";
            FakeHost.ItemTypes[id] = 0x10;
            if (i < spots.Count)
                FakeHost.Positions[id] = spots[i];
            else
                FakeHost.Positions[id] = (me.Cell, me.X + 80f + (float)far.NextDouble() * 100f, me.Y + (float)far.NextDouble() * 100f, me.Z);
            health[id] = 1f;
        }
        int inRangeSpots = spots.Count;

        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, ids, health);
        var settings = new LegacyUiSettings { MonsterRange = 50, EnableRaycasting = true };
        var combat = new CombatManager(host, settings, cache);
        combat.SetPlayerId(Player);
        bool los = Directory.Exists(AcDir) && spots.Count > 0;
        if (los) combat.InitializeRaycasting(AcDir);
        los &= combat.RaycastInitialized;
        FakeHost.LogCostMicros = LogCostUs;

        FieldInfo? lastScan = typeof(CombatManager).GetField("_lastScanTime", BindingFlags.Instance | BindingFlags.NonPublic);
        // Warm: the first scan loads the landblock's geometry, and a few hundred more get the
        // code past the JIT's first tier (in game it is hot long before a flood).
        lastScan?.SetValue(combat, DateTime.MinValue);
        combat.ScanNearbyTargets();
        var firstPick = combat.ScannedTargets.Select(t => t.Id).ToList();
        for (int i = 0; i < 400; i++) { lastScan?.SetValue(combat, DateTime.MinValue); combat.ScanNearbyTargets(); }

        const int scans = 200;
        double total = 0, max = 0;
        for (int i = 0; i < scans; i++)
        {
            lastScan?.SetValue(combat, DateTime.MinValue);
            long t0 = Stopwatch.GetTimestamp();
            combat.ScanNearbyTargets();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            total += ms;
            if (ms > max) max = ms;
        }
        FakeHost.LogCostMicros = 0;
        double avg = total / scans;
        Console.WriteLine($"          combat scan: {avg:0.000} ms a scan (slowest {max:0.00} ms), {avg * 20:0.0} ms a second at 20 Hz; " +
                          $"LOS {(los ? "on (real 0x6346 geometry)" : "off (no dats or no spots)")}; " +
                          $"{combat.LastScanTotalMonsters} monsters, {combat.LastScanInRing} in range, {combat.LastScanPossible} possible, " +
                          $"{combat.LastScanLosBlocked} LOS-blocked; {inRangeSpots} dungeon spots");

        Check.Eq(combat.LastScanTotalMonsters, 300, "the scan sees all 300 monsters");
        Check.True(combat.LastScanInRing >= 1, "some monsters in range");
        Check.Eq(combat.LastScanPossible + combat.LastScanLosBlocked, combat.LastScanInRing, "every in-range monster is a target or LOS-blocked");
        Check.True(combat.ScannedTargets.Select(t => t.Id).SequenceEqual(firstPick), "every scan picks the same targets in the same order");

        // FLOOD_VERBOSE=1: where a scan's time goes.
        if (Environment.GetEnvironmentVariable("FLOOD_VERBOSE") == "1")
        {
            foreach (string l in FakeHost.Logs.Distinct().Take(5)) Console.WriteLine($"          (log: {l})");
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < scans; i++) { lastScan?.SetValue(combat, DateTime.MinValue); combat.ScanNearbyTargets(); }
            Console.WriteLine($"          (scan with LOS, free logging: {sw2.Elapsed.TotalMilliseconds / scans:0.000} ms)");
            settings.EnableRaycasting = false;
            sw2.Restart();
            for (int i = 0; i < scans; i++) { lastScan?.SetValue(combat, DateTime.MinValue); combat.ScanNearbyTargets(); }
            Console.WriteLine($"          (scan without LOS: {sw2.Elapsed.TotalMilliseconds / scans:0.000} ms; log lines during scans: {FakeHost.Logs.Count})");
            settings.EnableRaycasting = true;
            if (los)
            {
                var raycast = (RynthCore.Plugin.RynthAi.Raycasting.MainLogic)typeof(CombatManager)
                    .GetField("_raycastSystem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(combat)!;
                var fsm = raycast.TargetingFSM;
                sw2.Restart();
                int n = 0;
                for (int r = 0; r < 20; r++)
                    foreach (uint id in ids.Take(spots.Count)) { fsm.IsTargetBlocked(host, id, RynthCore.Plugin.RynthAi.Raycasting.TargetingFSM.AttackType.Linear, out _); n++; }
                Console.WriteLine($"          (IsTargetBlocked Linear: {sw2.Elapsed.TotalMilliseconds * 1000 / n:0.0} us a target)");
                sw2.Restart(); n = 0;
                for (int r = 0; r < 20; r++)
                    foreach (uint id in ids.Take(spots.Count)) { fsm.IsTargetBlocked(host, id, RynthCore.Plugin.RynthAi.Raycasting.TargetingFSM.AttackType.Melee, out _); n++; }
                Console.WriteLine($"          (IsTargetBlocked Melee: {sw2.Elapsed.TotalMilliseconds * 1000 / n:0.0} us a target)");
            }
        }
    }

    // Lucy's spot and the monster spots within 50 m of it, from LosProof's ground-truth pairs.
    private static List<(uint Cell, float X, float Y, float Z)> LoadDungeonSpots(out (uint Cell, float X, float Y, float Z) me)
    {
        me = (PlayerCell, 50f, 50f, -24f);
        var spots = new List<(uint, float, float, float)>();
        string? file = FindUp(Path.Combine("RynthCore.LosProof", "pairs-6346.txt"));
        if (file == null) return spots;

        var rows = File.ReadAllLines(file).Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 10).ToList();
        if (rows.Count == 0) return spots;
        // The player spot with the most monster spots near it, so the scan has a crowd: first the
        // pairs judged from that very spot (open lines, doorways and walls, so some targets are
        // in sight and some behind walls), then other spots within 45 m, up to 60 in range (a
        // big swarm: Lucy's scan saw up to 18 in range and 68 in total).
        var players = rows.Select(p => (Cell: Hex(p[1]), X: F(p[3]), Y: F(p[4]), Z: F(p[5]))).Distinct().ToList();
        var targets = rows.Select(p => (Cell: Hex(p[2]), X: F(p[6]), Y: F(p[7]), Z: F(p[8]))).Distinct().ToList();
        var best = players.OrderByDescending(pl => targets.Count(t => Near(pl, t))).First();
        me = Local(best);
        var picked = rows.Where(p => (Hex(p[1]), F(p[3]), F(p[4]), F(p[5])) == best)
            .Select(p => (Cell: Hex(p[2]), X: F(p[6]), Y: F(p[7]), Z: F(p[8])))
            .Concat(targets.Where(t => Near(best, t)))
            .Distinct().Take(60);
        foreach (var t in picked)
            spots.Add(Local(t));
        return spots;

        static bool Near((uint Cell, float X, float Y, float Z) a, (uint Cell, float X, float Y, float Z) b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy <= 45f * 45f && Math.Abs(dz) < 6f && (dx != 0 || dy != 0);
        }
        static (uint, float, float, float) Local((uint Cell, float X, float Y, float Z) g)
            => (g.Cell, g.X - ((g.Cell >> 24) & 0xFF) * 192f, g.Y - ((g.Cell >> 16) & 0xFF) * 192f, g.Z);
        static uint Hex(string s) => uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    }

    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // ── Radar snapshot ────────────────────────────────────────────────────────

    private static void RadarSnapshot()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (PlayerCell, 50f, 50f, -24f);
        var rng = new Random(3);
        var ids = new List<uint>();
        for (int i = 0; i < 300; i++)
        {
            uint id = 0x8000B000u + (uint)i;
            ids.Add(id);
            Reveal(id, "Olthoi Swarm Nymph", rng, creature: true);
        }
        for (int i = 0; i < 400; i++)
        {
            uint id = 0x76346000u + (uint)i;
            ids.Add(id);
            Reveal(id, i % 40 == 0 ? "Door" : "Hive Wall Torch", rng, creature: false);
            FakeHost.ItemTypes[id] = 0x80u;
            FakeHost.NotAttackable.Add(id);
        }
        var host = FakeHost.Create(Player);
        var cache = FakeHost.MakeCache(host, Player, ids);
        var radar = new RynthRadarUi(host, new LegacyUiSettings());
        radar.SetWorldObjectCache(cache);

        string json = radar.BuildSnapshotJson(0);
        for (int i = 0; i < 300; i++) json = radar.BuildSnapshotJson(Lb >> 16); // past the JIT's first tier
        const int calls = 300;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < calls; i++) json = radar.BuildSnapshotJson(Lb >> 16);
        double avg = sw.Elapsed.TotalMilliseconds / calls;
        Console.WriteLine($"          radar snapshot: {avg:0.000} ms a call, {avg * 30:0.0} ms a second at 30 Hz, {json.Length / 1024.0:0.0} KB of JSON");

        if (Environment.GetEnvironmentVariable("FLOOD_VERBOSE") == "1")
        {
            var build = typeof(RynthRadarUi).GetMethod("BuildSnapshotPayload", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object? payload = null;
            var sw3 = Stopwatch.StartNew();
            for (int i = 0; i < calls; i++) payload = build.Invoke(radar, new object[] { Lb >> 16 });
            Console.WriteLine($"          (radar payload build: {sw3.Elapsed.TotalMilliseconds / calls:0.000} ms)");
            sw3.Restart();
            for (int i = 0; i < calls; i++) RynthCore.Engine.UI.Data.RadarSnapshot.Parse(json);
            Console.WriteLine($"          (engine parse of the snapshot: {sw3.Elapsed.TotalMilliseconds / calls:0.000} ms)");
        }

        var parsed = RynthCore.Engine.UI.Data.RadarSnapshot.Parse(json);
        Check.NotNull(parsed, "the engine parses the snapshot");
        Check.Eq(parsed?.Markers.Count ?? 0, 310, "300 creatures and 10 doors on the radar");
    }
}
