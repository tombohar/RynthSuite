using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;

using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Loot Nearby Chests (2026-11-14): with the setting on, the macro running, Looting on and no fight, a
// chest in loot range is walked to (the server walks: every step is a use), opened and looted by the
// loot profile, then left alone until it resets. Locked: a carried key that fits, else a lockpick when
// the Lockpick skill gives 50% or better, else skipped for 10 minutes. The state machine
// (NearbyChestLooter.cs) runs on a fake environment with a clock the test moves; the plugin glue
// (which objects are chests, the loot hand-over, the setting) runs on the fake host.
internal static class NearbyChestTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("nearby chests: an unlocked chest is appraised, opened and looted, then left alone until the re-lock wait passes", UnlockedHappyPath);
        r.Add("nearby chests: the nearest chest not left alone; nothing while fighting; a fight drops a chest nothing was sent to", CandidateSelection);
        r.Add("nearby chests: locked, a carried key fits -> the key, then the open", LockedKeyFits);
        r.Add("nearby chests: locked, a key that doesn't fit (0x4B2) -> the next key; a key that didn't fit is never tried on that chest again", LockedKeyDoesntFit);
        r.Add("nearby chests: locked, no key fits, skill good enough -> the lockpick (a failed pick is tried again, up to 3)", LockpickEnoughSkill);
        r.Add("nearby chests: locked, skill too low, untrained or an unpickable lock -> skipped, no lockpick spent", LockpickNotEnoughSkill);
        r.Add("nearby chests: locked, no key fits and no lockpick -> skipped 10 minutes, then tried again", NoLockpickSkipThenRetry);
        r.Add("nearby chests: a looted locked chest is ready when it reads locked again; still unlocked after the wait = another wait; gone = forgotten", RespawnTimer);
        r.Add("nearby chests: a chest that won't open is taken as locked; unknown resistance picks at most twice", WontOpenTakenAsLocked);
        r.Add("nearby chests: off -> nothing is scanned or sent", OffNothing);
        r.Add("nearby chests: 'skill good enough' is ACE's GetSkillChance at 50% or better (the server's own percent first)", PickRuleMath);
        r.Add("nearby chests: what is a chest (stuck container in the world; not a corpse, vendor, player, door, house storage, pack or held)", ChestRule);
        r.Add("nearby chests plugin: off by default; off -> no loot work, no appraisal, no use", PluginOffNothing);
        r.Add("nearby chests plugin: in range and eligible only (house storage, a vendor, a dropped pack, a far chest are passed by)", PluginCandidates);
        r.Add("nearby chests plugin: a closed chest in range is appraised, opened and looted by the loot profile, then left alone", PluginLootsChest);
        r.Add("nearby chests plugin: no chest work while combat has a target", PluginSkippedWhileFighting);
        r.Add("nearby chests plugin: the setting reaches the engine, an engine save without it leaves it, the meta option sets it; /ra chests answers", PluginSettingPlumbing);
    }

    // ── The fake environment ────────────────────────────────────────────────

    private sealed class Env : INearbyChestEnv
    {
        public long T = 1_000_000;
        public long Now => T;
        public bool Enabled { get; set; } = true;
        public bool Fighting { get; set; }
        public readonly List<NearbyChest> Chests = new() { new NearbyChest(1, "Chest", 4) };
        public readonly HashSet<int> Gone = new();
        public string? Hold;
        public bool CanAppraise { get; set; } = true;
        public bool AppraisalAnswers = true;
        public readonly Dictionary<int, long> Stamps = new();
        public readonly Dictionary<int, bool> LockedMap = new();
        public readonly Dictionary<int, int> Resist = new();
        public readonly Dictionary<int, int> Pct = new();
        public bool SkillKnown = true, Trained = true;
        public int Skill = 300;
        public readonly List<ChestTool> KeyList = new();
        public ChestTool? Pick;
        public bool HasUseDone { get; set; } = true;
        public int Seq;
        public uint LastErr;
        public readonly List<string> Sent = new(), Logs = new();
        public readonly Dictionary<int, long> Opened = new();
        public int LootingId;
        public long LootEnd;
        /// <summary>The server's script for a use with a tool: (error, chat line, unlocks). Null = no answer.</summary>
        public Func<ChestTool, (uint Err, string? Chat, bool Unlocks)?>? OnUseWith;
        /// <summary>The chest opens on the open (default: when it isn't locked).</summary>
        public Func<int, bool>? OnOpen;
        public NearbyChestLooter L = null!;
        private int _pendingAppraisal;

        public IReadOnlyList<NearbyChest> ScanChests() => Chests.Where(c => !Gone.Contains(c.Id)).ToList();
        public bool InWorld(int id) => !Gone.Contains(id);
        public bool StillEligible(int id, out string why) { why = ""; return true; }
        public string? HoldReason() => Hold;
        public bool RequestAppraisal(int chestId) { Sent.Add("id " + chestId); if (AppraisalAnswers) _pendingAppraisal = chestId; return true; }
        public long AppraisalStamp(int chestId) => Stamps.TryGetValue(chestId, out long s) ? s : 0;
        public bool? Locked(int chestId) => LockedMap.TryGetValue(chestId, out bool b) ? b : null;
        public int? LockResistance(int chestId) => Resist.TryGetValue(chestId, out int v) ? v : null;
        public int? PickSuccessPercent(int chestId) => Pct.TryGetValue(chestId, out int v) ? v : null;
        public bool LockpickSkill(out int buffed, out bool trained) { buffed = Skill; trained = Trained; return SkillKnown; }
        public IReadOnlyList<ChestTool> Keys() => KeyList;
        public ChestTool? Lockpick() => Pick;
        public int UseDoneSeq() => Seq;
        public bool LastUseDone(out int seq, out uint error) { seq = Seq; error = LastErr; return true; }
        public bool SendUseWith(int toolId, int chestId, string why)
        {
            var tool = KeyList.Concat(Pick is { } p ? new[] { p } : Array.Empty<ChestTool>()).First(t => t.Id == toolId);
            Sent.Add("use " + tool.Name);
            var ans = OnUseWith?.Invoke(tool);
            if (ans is { } a)
            {
                if (a.Chat != null) L.OnChat(a.Chat);
                if (a.Unlocks) LockedMap[chestId] = false;
                LastErr = a.Err; Seq++;
            }
            return true;
        }
        public bool SendOpen(int chestId)
        {
            Sent.Add("open " + chestId);
            bool opens = OnOpen?.Invoke(chestId) ?? Locked(chestId) != true;
            if (opens) { Opened[chestId] = T + 1; LootingId = chestId; LootEnd = T + 2000; }
            LastErr = 0; Seq++;
            return true;
        }
        public long OpenedAt(int chestId) => Opened.TryGetValue(chestId, out long t) ? t : 0;
        public bool Looting(int chestId) => LootingId == chestId;
        public void Log(string text) => Logs.Add(text);

        /// <summary>Moves the clock in 100 ms steps, ticking as the loot tick does (may start: no corpse waits).</summary>
        public void Run(long ms, Func<bool>? until = null)
        {
            long end = T + ms;
            while (T < end && (until == null || !until()))
            {
                T += 100;
                if (_pendingAppraisal != 0) { Stamps[_pendingAppraisal] = T; _pendingAppraisal = 0; }
                if (LootingId != 0 && T >= LootEnd) LootingId = 0;   // the corpse looter finishes and closes it
                L.Tick(mayStart: true);
            }
        }

        public int Count(string prefix) => Sent.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static Env NewEnv()
    {
        var e = new Env();
        e.L = new NearbyChestLooter(e);
        return e;
    }

    private static string Sent(Env e) => string.Join(" | ", e.Sent);

    // ── State machine ───────────────────────────────────────────────────────

    private static void UnlockedHappyPath()
    {
        var e = NewEnv();
        e.Run(5000, () => e.L.Looted == 1 && !e.L.Busy);
        Check.Eq(Sent(e), "id 1 | open 1", "one appraisal, one open, nothing else");
        Check.Eq(e.L.Looted, 1, "looted");
        Check.False(e.L.Busy, "done");
        Check.True(e.L.IsLeftAlone(1, e.T), "left alone after the loot");
        Check.True(e.Logs.Any(l => l.Contains("Nearby chests: done with chest") && l.Contains("left alone until it resets")), "the finish says why it is left: " + string.Join(" / ", e.Logs));

        // Nothing more until the re-lock wait (125 s) passes, then it is looted again.
        e.Run(100_000);
        Check.Eq(e.Count("open"), 1, "no second open before the wait");
        e.Run(40_000, () => e.L.Looted == 2);
        Check.Eq(e.L.Looted, 2, "looted again after the re-lock wait (the chest regenerated)");
        Check.Eq(e.Count("id"), 2, "appraised again first");
    }

    private static void CandidateSelection()
    {
        var e = NewEnv();
        e.Chests.Clear();
        e.Chests.Add(new NearbyChest(1, "Far Chest", 8));
        e.Chests.Add(new NearbyChest(2, "Near Chest", 3));
        Check.Eq(e.L.Candidate(), 2, "the nearest first");

        e.Fighting = true;
        Check.Eq(e.L.Candidate(), 0, "nothing while fighting");
        e.Run(2000);
        Check.Eq(e.Sent.Count, 0, "nothing sent while fighting");

        // The fight ends; the appraisal goes out, a fight starts before the next step: the chest is dropped.
        e.Fighting = false;
        e.AppraisalAnswers = false;   // the answer is slow
        e.Run(150, () => e.Sent.Count > 0);
        Check.Eq(Sent(e), "id 2", "the near chest is appraised");
        e.Fighting = true;
        e.Run(AppraiseWait());
        Check.False(e.L.Busy, "a fight drops the chest once nothing is owed");
        Check.Eq(e.Count("open"), 0, "no open in the fight");
        Check.True(e.Logs.Any(l => l.Contains("for the fight")), "says it left the chest for the fight");
        Check.False(e.L.IsLeftAlone(2, e.T), "nothing marked: tried again after the fight");

        // The hold: nothing starts while the client/arbiter holds the send.
        e.Fighting = false;
        e.AppraisalAnswers = true;
        e.Hold = "Looting doesn't have the tick";
        int before = e.Sent.Count;
        e.Run(2000);
        Check.Eq(e.Sent.Count, before, "nothing while held");
        e.Hold = null;
        e.Run(5000, () => e.L.Looted == 1);
        Check.Eq(e.L.Looted, 1, "looted once free");
        Check.Eq(e.L.Candidate(), 1, "then the far chest (the near one is left alone)");
    }

    private static long AppraiseWait() => NearbyChestLooter.AppraiseMaxMs + 300;

    private static Env LockedEnv(int resist = 100, int? pct = null)
    {
        var e = NewEnv();
        e.LockedMap[1] = true;
        e.Resist[1] = resist;
        if (pct != null) e.Pct[1] = pct.Value;
        return e;
    }

    private static void LockedKeyFits()
    {
        var e = LockedEnv();
        e.KeyList.Add(new ChestTool(50, "Aged Key"));
        e.Pick = new ChestTool(60, "Plain Lockpick");
        e.OnUseWith = t => t.Name == "Aged Key" ? (0u, "The Chest has been unlocked.\nYour key has 4 uses left.", true) : (NearbyChestLooter.ErrKeyDoesntFit, null, false);
        e.Run(10_000, () => e.L.Looted == 1 && !e.L.Busy);
        Check.Eq(Sent(e), "id 1 | use Aged Key | open 1", "the key, then the open; no lockpick");
        Check.Eq(e.L.Looted, 1, "looted");
    }

    private static void LockedKeyDoesntFit()
    {
        var e = LockedEnv();
        e.KeyList.Add(new ChestTool(50, "Black Key"));
        e.KeyList.Add(new ChestTool(51, "Aged Key"));
        e.OnUseWith = t => t.Name == "Aged Key" ? (0u, "The Chest has been unlocked.", true) : (NearbyChestLooter.ErrKeyDoesntFit, null, false);
        e.Run(10_000, () => e.L.Looted == 1 && !e.L.Busy);
        Check.Eq(Sent(e), "id 1 | use Black Key | use Aged Key | open 1", "the wrong key, then the next, then the open");
        Check.True(e.Logs.Any(l => l.Contains("Black Key doesn't fit")), "says the key doesn't fit");

        // None fits, no lockpick: skipped. A retry (10 min later) never tries the keys that didn't fit.
        var f = LockedEnv();
        f.KeyList.Add(new ChestTool(50, "Black Key"));
        f.OnUseWith = _ => (NearbyChestLooter.ErrKeyDoesntFit, null, false);
        f.Run(10_000, () => f.L.IsLeftAlone(1, f.T));
        Check.Eq(Sent(f), "id 1 | use Black Key", "one key tried, nothing else (no lockpick)");
        Check.True(f.L.IsLeftAlone(1, f.T), "skipped");
        f.Run(NearbyChestLooter.SkipMs + 2000, () => f.Count("id") == 2);
        f.Run(2000);
        Check.Eq(f.Count("use Black Key"), 1, "the key that didn't fit is not tried again on that chest");
        f.KeyList.Add(new ChestTool(52, "Gold Key"));
        f.Run(NearbyChestLooter.SkipMs + 5000, () => f.Count("use Gold Key") == 1);
        Check.Eq(f.Count("use Gold Key"), 1, "a new key is tried on the next attempt");
    }

    private static void LockpickEnoughSkill()
    {
        // Resistance 250 against skill 300: ACE's chance 81% (as its appraisal truncates it). The first pick fails, the second works.
        var e = LockedEnv(resist: 250);
        e.Pick = new ChestTool(60, "Plain Lockpick");
        int picks = 0;
        e.OnUseWith = t => ++picks == 1
            ? (0u, "You have failed to pick the lock.  It is still locked.  Your lockpicks have 4 uses left.", false)
            : (0u, "You have successfully picked the lock!  It is now unlocked.\n Your lockpicks have 3 uses left.", true);
        e.Run(15_000, () => e.L.Looted == 1 && !e.L.Busy);
        Check.Eq(Sent(e), "id 1 | use Plain Lockpick | use Plain Lockpick | open 1", "two picks (the first failed), then the open");
        Check.True(e.Logs.Any(l => l.Contains("picking chest") && l.Contains("81%")), "says the chance it picks at: " + string.Join(" / ", e.Logs));

        // Every pick fails: three, then skipped.
        var f = LockedEnv(resist: 250);
        f.Pick = new ChestTool(60, "Plain Lockpick");
        f.OnUseWith = _ => (0u, "You have failed to pick the lock.  It is still locked.", false);
        f.Run(20_000, () => f.L.IsLeftAlone(1, f.T));
        Check.Eq(f.Count("use Plain Lockpick"), NearbyChestLooter.MaxPicks, "at most 3 picks");
        Check.True(f.L.IsLeftAlone(1, f.T), "then skipped");
        Check.True(f.Logs.Any(l => l.Contains("the lock held after 3 lockpick tries") && l.Contains("10 minutes")), "says why and when again");

        // The server's own percent wins over the estimate.
        var g = LockedEnv(resist: 250, pct: 40);
        g.Pick = new ChestTool(60, "Plain Lockpick");
        g.Run(10_000, () => g.L.IsLeftAlone(1, g.T));
        Check.Eq(g.Count("use"), 0, "the server says 40%: not picked");
    }

    private static void LockpickNotEnoughSkill()
    {
        var e = LockedEnv(resist: 400);   // skill 300: ~5%
        e.Pick = new ChestTool(60, "Plain Lockpick");
        e.Run(10_000, () => e.L.IsLeftAlone(1, e.T));
        Check.Eq(Sent(e), "id 1", "appraised; no lockpick spent");
        Check.True(e.L.IsLeftAlone(1, e.T), "skipped");
        Check.True(e.Logs.Any(l => l.Contains("too low") && l.Contains("10 minutes")), "says the skill is too low: " + string.Join(" / ", e.Logs));

        var u = LockedEnv(resist: 10);
        u.Pick = new ChestTool(60, "Plain Lockpick");
        u.Trained = false;
        u.Run(10_000, () => u.L.IsLeftAlone(1, u.T));
        Check.Eq(u.Count("use"), 0, "untrained: never picks");
        Check.True(u.Logs.Any(l => l.Contains("isn't trained")), "says so");

        var n = NewEnv();
        n.LockedMap[1] = true;   // locked, appraised, and no resistance: ACE can't pick it
        n.Pick = new ChestTool(60, "Plain Lockpick");
        n.Run(10_000, () => n.L.IsLeftAlone(1, n.T));
        Check.Eq(n.Count("use"), 0, "an unpickable lock: no pick");
        Check.True(n.Logs.Any(l => l.Contains("can't be picked")), "says so");
    }

    private static void NoLockpickSkipThenRetry()
    {
        var e = LockedEnv(resist: 50);
        e.Run(10_000, () => e.L.IsLeftAlone(1, e.T));
        Check.Eq(Sent(e), "id 1", "appraised, nothing to use");
        Check.True(e.Logs.Any(l => l.Contains("carry no lockpick") && l.Contains("10 minutes")), "says why");
        e.Run(NearbyChestLooter.SkipMs - 15_000);
        Check.Eq(e.Count("id"), 1, "left alone for 10 minutes");
        e.Pick = new ChestTool(60, "Plain Lockpick");
        e.OnUseWith = _ => (0u, "You have successfully picked the lock!  It is now unlocked.", true);
        e.Run(30_000, () => e.L.Looted == 1);
        Check.Eq(e.L.Looted, 1, "tried again after 10 minutes (with a lockpick now) and looted");
    }

    private static void RespawnTimer()
    {
        var e = LockedEnv(resist: 50);
        e.Pick = new ChestTool(60, "Plain Lockpick");
        e.OnUseWith = _ => (0u, "You have successfully picked the lock!", true);
        e.Run(10_000, () => e.L.Looted == 1 && !e.L.Busy);
        Check.Eq(e.L.Looted, 1, "looted");
        Check.True(e.L.IsLeftAlone(1, e.T), "left alone");
        e.Run(20_000);
        e.LockedMap[1] = true;   // ACE's Chest.Reset: locked again (and regenerated)
        Check.False(e.L.IsLeftAlone(1, e.T), "reads locked again: ready at once");
        e.Run(10_000, () => e.L.Looted == 2);
        Check.Eq(e.L.Looted, 2, "picked and looted again");

        // Not re-locked when the wait is over: appraised, and another wait without a use.
        int uses = e.Count("use");
        e.Run(126_000, () => e.Count("id") == 3);
        e.Run(1000);
        Check.Eq(e.Count("use"), uses, "no use on a chest that hasn't reset");
        Check.Eq(e.Count("open"), 2, "no open either");
        Check.True(e.L.IsLeftAlone(1, e.T), "another wait");
        Check.True(e.Logs.Any(l => l.Contains("hasn't reset yet")), "says so");

        // Gone from the world: forgotten (a new one comes in fresh).
        e.Gone.Add(1);
        e.Run(1000);
        Check.True(e.Logs.Any(l => l.Contains("left the world; forgotten")), "forgotten");
        e.Gone.Remove(1);
        Check.False(e.L.IsLeftAlone(1, e.T), "back: not left alone");
    }

    private static void WontOpenTakenAsLocked()
    {
        var e = NewEnv();
        e.CanAppraise = false;   // an engine without the ID: the lock is unknown
        e.Pick = new ChestTool(60, "Plain Lockpick");
        bool unlocked = false;
        e.OnOpen = _ => unlocked;
        e.OnUseWith = _ => (0u, "You have failed to pick the lock.  It is still locked.", false);
        e.Run(30_000, () => e.L.IsLeftAlone(1, e.T));
        Check.Eq(Sent(e), "open 1 | use Plain Lockpick | use Plain Lockpick", "the open failed: taken as locked, two picks at an unknown resistance, then skipped");
        Check.True(e.Logs.Any(l => l.Contains("taking it as locked")), "says so");
    }

    private static void OffNothing()
    {
        var e = NewEnv();
        e.Enabled = false;
        Check.Eq(e.L.Candidate(), 0, "no candidate");
        e.Run(5000);
        Check.Eq(e.Sent.Count, 0, "nothing sent");
        // Turned off mid-chest: dropped, nothing marked.
        e.Enabled = true;
        e.AppraisalAnswers = false;
        e.Run(200, () => e.Sent.Count > 0);
        e.L.StandDown("Loot Nearby Chests was turned off");
        Check.False(e.L.Busy, "dropped");
        Check.False(e.L.IsLeftAlone(1, e.T), "nothing marked");
    }

    private static void PickRuleMath()
    {
        Check.True(Math.Abs(NearbyChestLooter.PickChance(300, 300) - 0.5) < 1e-9, "skill = resistance: 50%");
        Check.True(Math.Abs(NearbyChestLooter.PickChance(300, 250) - (1 - 1 / (1 + Math.Exp(1.5)))) < 1e-9, "ACE's logistic, factor 0.03");
        Check.Eq(NearbyChestLooter.PickTries(true, 300, true, true, true, 300, null, out _), NearbyChestLooter.MaxPicks, "50%: picks");
        Check.Eq(NearbyChestLooter.PickTries(true, 299, true, true, true, 300, null, out _), null, "just under 50%: no");
        Check.Eq(NearbyChestLooter.PickTries(true, 100, true, true, true, 300, 60, out _), NearbyChestLooter.MaxPicks, "the server's 60% wins over the estimate");
        Check.Eq(NearbyChestLooter.PickTries(true, 300, false, true, true, 10, null, out _), null, "untrained: no");
        Check.Eq(NearbyChestLooter.PickTries(true, 900, true, true, true, 9999, null, out _), null, "9999: unpickable");
        Check.Eq(NearbyChestLooter.PickTries(true, 300, true, true, true, null, null, out _), null, "locked with no resistance after an appraisal: unpickable");
        Check.Eq(NearbyChestLooter.PickTries(true, 300, true, false, null, null, null, out _), NearbyChestLooter.MaxPicksUnknown, "never appraised: a couple of tries");
    }

    private static void ChestRule()
    {
        const uint stuck = NearbyChestLooter.BfStuck;
        Check.Eq(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, stuck, 0, "Chest", true, 0, 0), null, "a stuck container in the world: a chest");
        Check.Eq(NearbyChestLooter.ChestRefusal(AcObjectClass.Corpse, true, stuck, 0, "Corpse of X", true, 0, 0), "a corpse", "corpse class");
        Check.Eq(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, stuck | NearbyChestLooter.BfCorpse, 0, "x", true, 0, 0), "a corpse", "corpse bit");
        Check.Eq(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, stuck | NearbyChestLooter.BfVendor, 0, "x", true, 0, 0), "a vendor", "vendor");
        Check.Eq(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, stuck | NearbyChestLooter.BfPlayer, 0, "x", true, 0, 0), "a player", "player");
        Check.True(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, stuck, 0x50000001, "Chest", true, 0, 0)!.StartsWith("house storage"), "house owner");
        Check.True(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, stuck, 0, "Storage", true, 0, 0)!.StartsWith("house storage"), "named storage");
        Check.True(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, 0, 0, "Pack", true, 0, 0)!.Contains("picked up"), "a dropped pack (not stuck)");
        Check.Eq(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, true, stuck, 0, "Chest", true, 0x50000001, 0), "someone holds it", "held");
        Check.Eq(NearbyChestLooter.ChestRefusal(AcObjectClass.Door, true, stuck | NearbyChestLooter.BfDoor, 0, "Door", true, 0, 0), "not a container", "a door");
        Check.True(NearbyChestLooter.ChestRefusal(AcObjectClass.Container, false, 0, 0, "Chest", true, 0, 0) != null, "no bitfield read: not trusted");
    }

    // ── Plugin glue ─────────────────────────────────────────────────────────

    private const uint Player = 0x50000D01;
    private const uint Chest = 0x7A9B4011;
    private const uint Storage = 0x7A9B4012;
    private const uint VendorBox = 0x7A9B4013;
    private const uint DroppedPack = 0x80263400;
    private const uint FarChest = 0x7A9B4015;
    private const uint Gem = 0x80263401;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint Stuck = 0x4;

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void Place(uint id, string name, float dx, uint bitfield, uint type = ItemTypeContainer)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = type;
        FakeHost.Positions[id] = (Me0.Cell, Me0.X + dx, Me0.Y, Me0.Z);
        FakeHost.NotAttackable.Add(id);
        FakeHost.Containers[id] = 0;
        FakeHost.Bitfields[id] = bitfield;
    }

    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        FakeHost.WeaponCalls = true;          // RequestId, HasAppraisalData, UseObject
        FakeHost.CloseContainerCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.Names[Player] = "Dargoth Hera";
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Bitfields[Player] = 0x8;
        Place(Storage, "Storage", 1.0f, Stuck);
        FakeHost.InstanceIds[(Storage, 32)] = 0x50000AAA;   // HouseOwner
        Place(VendorBox, "Vendor Chest", 1.2f, Stuck | 0x200);
        Place(DroppedPack, "Pack", 1.6f, 0);
        Place(Chest, "Chest", 3.0f, Stuck);
        Place(FarChest, "Far Chest", 60.0f, Stuck);
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        FakeHost.Containers[Gem] = Chest;
        FakeHost.Appraised.Add(Gem);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true,
            EnableLooting = true,
            EnableCombat = false,
            LootOwnership = 0,
            LootContentSettleMs = 0,
            LootEmptyCorpseMs = 0,
            StopLootingWhenPackFull = false,
            LootNearbyChests = true,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            string profile = Path.Combine(Program.TempRoot, "loot-nearby-chest.json");
            var p = new LootProfile { Name = "chest" };
            var gems = new LootRule { Name = "Gems", Action = LootAction.Keep };
            gems.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(gems);
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public bool HasLootWork() => (bool)Call("HasLootWork", S, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!;
        public void Tick(BotActivity act = BotActivity.Looting)
        {
            Set("_activity", act);
            Call("TickCorpseOpening");
        }
        public void Create(params uint[] ids)
        {
            foreach (uint id in ids) Cache.OnCreateObject(id);
            Cache.Tick();
        }
        public void TickUntil(Func<bool> done, int max = 300)
        {
            for (int i = 0; i < max && !done(); i++) { Tick(); Thread.Sleep(10); }
        }
        public void TickFor(int ms, BotActivity act = BotActivity.Looting)
        {
            long end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end) { Tick(act); Thread.Sleep(10); }
        }
    }

    private static Bot NewBot(bool on = true)
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.LootNearbyChests = on;
        bot.Create(Storage, VendorBox, DroppedPack, Chest, FarChest, Gem);
        return bot;
    }

    private static readonly uint[] NotChests = { Storage, VendorBox, DroppedPack, FarChest };

    private static void PluginOffNothing()
    {
        Check.False(new LegacyUiSettings().LootNearbyChests, "off by default");
        var bot = NewBot(on: false);
        Check.False(bot.HasLootWork(), "off: a chest in range is no loot work");
        bot.TickFor(300);
        Check.Eq(FakeHost.IdRequests.Count, 0, "off: nothing appraised");
        Check.Eq(FakeHost.Uses.Count, 0, "off: nothing used");
        Check.Eq(FakeHost.UsesOn.Count, 0, "off: no key or lockpick used");

        // The macro off with the setting on: nothing either.
        var b2 = NewBot();
        b2.S.IsMacroRunning = false;
        Check.False(b2.HasLootWork(), "macro off: no loot work");
        b2.TickFor(200);
        Check.Eq(FakeHost.IdRequests.Count + FakeHost.Uses.Count, 0, "macro off: nothing sent");
        // Looting off: nothing.
        var b3 = NewBot();
        b3.S.EnableLooting = false;
        Check.False(b3.HasLootWork(), "Looting off: no loot work");
        b3.TickFor(200);
        Check.Eq(FakeHost.IdRequests.Count + FakeHost.Uses.Count, 0, "Looting off: nothing sent");
    }

    private static void PluginCandidates()
    {
        var bot = NewBot();
        Check.True(bot.HasLootWork(), "a chest in loot range is loot work");
        bot.TickUntil(() => FakeHost.IdRequests.Count > 0, 50);
        Check.Eq(string.Join(",", FakeHost.IdRequests.Select(i => $"{i:X8}")), $"{Chest:X8}", "only the real chest is appraised (the nearer storage, vendor chest and dropped pack, and the far chest, are passed by; corpses: ChestRule)");
        bot.TickFor(300);
        foreach (uint id in NotChests)
        {
            Check.False(FakeHost.IdRequests.Contains(id) || FakeHost.Uses.Contains(id), $"0x{id:X8} '{FakeHost.Names[id]}' is never touched");
        }
    }

    private static void PluginLootsChest()
    {
        var bot = NewBot();
        bot.TickUntil(() => FakeHost.IdRequests.Count > 0, 50);
        FakeHost.Appraised.Add(Chest);   // the ID arrives: no Locked property, so it isn't locked
        bot.TickUntil(() => FakeHost.Uses.Contains(Chest), 100);
        Check.True(FakeHost.Uses.Contains(Chest), "the chest is used (the server walks there and opens it)");
        Check.Eq(FakeHost.AutoRunCalls.Count(on => on), 0, "no walking of our own");

        // The server opens it: the chest looter takes it and loots the gem by the profile.
        FakeHost.GroundContainer = Chest;
        bot.P.OnViewObjectContents(Chest);
        bot.P.OnUpdateObjectInventory(Chest);
        bot.TickUntil(() => FakeHost.ExternalMoves.Any(m => m.Id == Gem));
        Check.True(FakeHost.ExternalMoves.Contains((Gem, Player, 0)), "the gem is picked up");
        FakeHost.Containers[Gem] = Player;
        int closes = FakeHost.Closes.Count;
        bot.TickUntil(() => FakeHost.Closes.Count > closes);
        if (FakeHost.GroundContainer == Chest) FakeHost.GroundContainer = 0;
        bot.P.OnStopViewingObjectContents(Chest);
        bot.TickFor(300);
        var looter = bot.Get<NearbyChestLooter>("_nearbyChests");
        Check.Eq(looter.Looted, 1, "counted as looted");
        Check.True(looter.IsLeftAlone(unchecked((int)Chest), RynthAiPlugin.CorpseClock()), "left alone until it resets");
        Check.False(bot.HasLootWork(), "no loot work left");
        int uses = FakeHost.Uses.Count(u => u == Chest);
        bot.TickFor(300);
        Check.Eq(FakeHost.Uses.Count(u => u == Chest), uses, "not opened again");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Nearby chests: done with chest")), "the finish is logged");
    }

    private const uint Monster = 0x80263501;

    private static void PluginSkippedWhileFighting()
    {
        World();
        FakeHost.Names[Monster] = "Drudge Skulker";
        FakeHost.ItemTypes[Monster] = 0x10;
        FakeHost.Positions[Monster] = (Me0.Cell, Me0.X + 20f, Me0.Y + 0.5f, Me0.Z);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.EnableCombat = true;
        bot.S.MonsterRange = 50;
        bot.Create(Storage, VendorBox, DroppedPack, Chest, FarChest, Gem);
        _ = bot.Cache[unchecked((int)Monster)];
        bot.Cache.OnUpdateHealth(Monster, 1f);
        bot.Cache.Tick();
        var combat = new CombatManager(host, bot.S, bot.Cache);
        combat.SetPlayerId(Player);
        typeof(CombatManager).GetField("_lastScanTime", Any)?.SetValue(combat, DateTime.MinValue);
        combat.ScanNearbyTargets();
        bot.Set("_combatManager", combat);
        Check.True(combat.HasTargets, "a monster in range");
        Check.False(bot.HasLootWork(), "fighting: the chest is no loot work");
        bot.TickFor(300);
        bot.TickFor(300, BotActivity.Combat);
        Check.Eq(FakeHost.IdRequests.Count + FakeHost.Uses.Count + FakeHost.UsesOn.Count, 0, "nothing sent to the chest while fighting");
    }

    private static void PluginSettingPlumbing()
    {
        var s = new LegacyUiSettings { LootNearbyChests = true };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", Any)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.LootNearbyChests == true, "sent to the engine's Settings row");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.LootNearbyChests, "an engine save without the field leaves it");
        dash.ApplySettingsJson("{\"LootNearbyChests\":false}");
        Check.False(s.LootNearbyChests, "an engine save with it sets it");

        FakeHost.Reset();
        var host = FakeHost.Create();
        var ms = new LegacyUiSettings();
        var meta = new MetaManager(ms, host, new PlayerVitalsCache());
        meta.TryHandleVtCommand("/vt opt set lootnearbychests true");
        Check.True(ms.LootNearbyChests, "/vt opt set lootnearbychests turns it on");
        meta.TryHandleVtCommand("/vt opt set lootnearbychests false");
        Check.False(ms.LootNearbyChests, "and off");
        var e = ExpressionTests.Engine(settings: ms);
        Check.Eq(e.Evaluate("vtsetsetting[LootNearbyChests, 1]"), "1", "metas: vtsetsetting knows it");
        Check.True(ms.LootNearbyChests, "and turns it on");

        // /ra chests always answers.
        var bot = NewBot(on: false);
        int before = FakeHost.Chat.Count;
        bot.Call("HandleNearbyChestsCommand");
        Check.True(FakeHost.Chat.Skip(before).Any(l => l.Contains("Loot Nearby Chests: off")), "/ra chests says it is off: " + string.Join(" / ", FakeHost.Chat.Skip(before)));
        bot.S.LootNearbyChests = true;
        before = FakeHost.Chat.Count;
        bot.Call("HandleNearbyChestsCommand");
        Check.True(FakeHost.Chat.Skip(before).Any(l => l.Contains("Loot Nearby Chests: on")), "/ra chests says it is on");
    }
}
