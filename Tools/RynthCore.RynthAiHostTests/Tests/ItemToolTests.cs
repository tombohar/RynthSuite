using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemTool;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Item Tool (2026-10-17): the chest looter ("pick a chest, pick a key, it loots the chest with
// the current loot profile until it is out of keys") and Quick give (a rapid give of one picked
// item to one picked player or NPC). The state machines run here on a fake environment with a
// clock the test moves; the plugin glue (commands, who may start Quick give, the loot hand-over,
// the give path) runs on the fake host. The existing profile give (GiveTests, ExpressionTests) is
// untouched: Quick give shares nothing with it.
internal static class ItemToolTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        // Chest looter state machine
        r.Add("chest looter: key, open, loot, wait for the re-lock, repeat until the key is used up", ChestHappyPath);
        r.Add("chest looter: the next cycle waits for the chest's regen timer", ChestWaitsForRegen);
        r.Add("chest looter: a chest that was unlocked is opened without spending the key; later 'already unlocked' answers are probes, bounded", ChestAlreadyUnlockedProbes);
        r.Add("chest looter: a second key of the same name takes over when the first is gone", ChestSwitchesKey);
        r.Add("chest looter: wrong key, not a lock, chest open elsewhere -> stops with the reason", ChestKeyErrors);
        r.Add("chest looter: chest gone, no answer, chest won't open, looter never starts -> bounded, says why", ChestBoundedFailures);
        r.Add("chest looter: combat/busy holds every send, then the run stops after the cap", ChestHoldsThenStops);
        r.Add("chest looter: can't start without a chest, a key, a loaded near chest; Stop always works and ends the loot session", ChestStartRefusalsAndStop);
        r.Add("chest looter: a key that never runs out is cut off at MaxCycles", ChestMaxCycles);
        // Quick give state machine
        r.Add("quick give: one give at a time, each after the last landed, paced; done when none is left", GiveOneAtATime);
        r.Add("quick give: a refused item is left out; three refusals in a row stop it", GiveRefusals);
        r.Add("quick give: stops when the target vanishes, changes, moves away or a fight starts", GiveStopConditions);
        r.Add("quick give: refuses to start with the target far, unloaded, no item or in a fight; busy client waits", GiveStartRefusals);
        // Plugin glue
        r.Add("item tool: the chest looter picks the selected chest and key, unlocks, opens and loots with the macro off, stops when out of keys", PluginChestRun);
        r.Add("item tool: chest/key picks refuse the wrong things (a pack item as chest, a world object as key, house storage)", PluginChestPicks);
        r.Add("item tool: Quick give hands over exactly the picked item to the picked NPC, never worn gear or other names", PluginQuickGive);
        r.Add("item tool: Quick give starts only from the panel or a typed line, never from a meta, a script or a remote line; stop works from anywhere", PluginGiveTrust);
        r.Add("item tool: Quick give targets are players or NPCs only", PluginGiveTargets);
    }

    // ── Fake environments ────────────────────────────────────────────────────

    private sealed class ChestEnv : IChestKeyEnv
    {
        public long T = 1_000_000;
        public long Now => T;
        public bool Loaded = true;
        public double Meters = 3;
        public bool Unlimited;                         // FindKey always finds the key
        public readonly HashSet<int> Keys = new() { 11 };
        public string KeyName = "Aged Key";
        public string? Hold;
        public bool HasUseDone { get; set; } = true;
        public int Seq;
        public uint LastErr;
        public readonly List<string> Said = new(), Logs = new(), Sent = new();
        public long OpenedAt;
        public bool LootingNow, LootPending;
        public int LootStarts, Aborts;
        public bool? Locked;
        public bool SendFails;
        /// <summary>> 0: the corpse looter is played: it takes a requested chest and lets go this long after.</summary>
        public long AutoLootMs;
        public bool IgnoreStart;               // StartLooting does nothing (the looter never takes the chest)
        private long _lootEnd;

        // The server's script for the next key use / open: filled by the test, run on send.
        public Func<int, uint?>? OnKey;               // returns an error to answer with (null = no answer)
        public Func<bool>? OnOpen;                    // true = the chest opens

        public ChestKeyLooter Looter = null!;

        public string NameOf(int id) => "x";
        public bool ChestLoaded(int chestId, out double meters) { meters = Meters; return Loaded; }
        public int FindKey(int keyId, string keyName, int excludeId)
        {
            if (Unlimited) return keyId != 0 ? keyId : 11;
            if (keyId != 0 && keyId != excludeId && Keys.Contains(keyId)) return keyId;
            foreach (int k in Keys) if (k != excludeId && k != keyId) return k;
            return 0;
        }
        public string? HoldReason() => Hold;
        public int UseDoneSeq() => Seq;
        public bool LastUseDone(out int seq, out uint error) { seq = Seq; error = LastErr; return true; }
        public bool SendKey(int keyId, int chestId)
        {
            if (SendFails) return false;
            Sent.Add("key " + keyId);
            uint? ans = OnKey?.Invoke(keyId);
            if (ans != null) { LastErr = ans.Value; Seq++; }
            return true;
        }
        public bool SendOpen(int chestId)
        {
            if (SendFails) return false;
            Sent.Add("open");
            bool opens = OnOpen?.Invoke() ?? true;
            if (opens) OpenedAt = T + 1;
            LastErr = 0; Seq++;
            return true;
        }
        public long ChestOpenedAt(int chestId) => OpenedAt;
        public void StartLooting(int chestId) { LootStarts++; if (!IgnoreStart) LootPending = true; }
        public bool Looting(int chestId) => LootPending || LootingNow;
        public void AbortLooting(int chestId) { Aborts++; LootPending = LootingNow = false; }
        public bool? ChestLockedHint(int chestId) => Locked;
        public void Say(string text) => Said.Add(text);
        public void Log(string text) => Logs.Add(text);

        /// <summary>Advances the clock in 100 ms steps, ticking, until done or the time is up.</summary>
        public void Run(long ms, Func<bool>? until = null)
        {
            long end = T + ms;
            while (T < end && (until == null || !until()))
            {
                T += 100;
                if (AutoLootMs > 0)
                {
                    if (LootPending) { LootPending = false; LootingNow = true; _lootEnd = T + AutoLootMs; }
                    else if (LootingNow && T >= _lootEnd) LootingNow = false;
                }
                Looter.Tick();
            }
        }
    }

    private static ChestEnv NewChest(out ChestKeyLooter looter)
    {
        var env = new ChestEnv();
        looter = new ChestKeyLooter(env);
        env.Looter = looter;
        looter.SetChest(500, "Mana Forge Chest");
        looter.SetKey(11, "Aged Key");
        return env;
    }

    /// <summary>A server for the chest: the key unlocks (and chats), the chest opens, the loot session runs a little and ends.</summary>
    private static void Serve(ChestEnv env, ChestKeyLooter looter, int usesPerKey = 1, bool spendKey = true)
    {
        int uses = usesPerKey;
        env.OnKey = key =>
        {
            looter.OnChat("The Mana Forge Chest has been unlocked.");
            if (spendKey)
            {
                uses--;
                if (uses <= 0) { looter.OnChat("Your key is used up."); env.Keys.Remove(key); uses = usesPerKey; }
                else looter.OnChat($"Your key has {uses} uses left.");
            }
            return 0u;
        };
    }

    private static string Said(ChestEnv env) => string.Join(" | ", env.Said);

    // ── Chest looter ─────────────────────────────────────────────────────────

    private static void ChestHappyPath()
    {
        var env = NewChest(out var ck);
        env.Keys.Clear(); env.Keys.Add(11);
        Serve(env, ck);
        env.AutoLootMs = 1000;
        Check.True(ck.Start(out _), "starts with a chest, a key and the chest near");
        Check.True(env.Said.Any(s => s.Contains("starting on Mana Forge Chest with Aged Key")), "says what it will do");

        env.Run(60_000, () => !ck.Running);
        Check.Eq(string.Join(",", env.Sent), "key 11,open", "the key goes on the chest, then the chest is opened");
        Check.Eq(env.LootStarts, 1, "the open chest is handed to the looter once");

        // The loot session ran and ended; the key was the last use, so the run ends out of keys.
        Check.False(ck.Running, "stopped");
        Check.Eq(ck.Looted, 1, "one chest looted");
        Check.True(Said(env).Contains("out of keys after 1 chest(s)"), "says it is out of keys (" + Said(env) + ")");
        Check.Eq(env.Sent.Count, 2, "nothing more sent");
    }

    private static void ChestWaitsForRegen()
    {
        var env = NewChest(out var ck);
        env.Keys.Clear(); env.Keys.Add(11); env.Keys.Add(12);   // two keys of the same name
        Serve(env, ck);
        env.AutoLootMs = 1000;
        ck.Start(out _);
        env.Run(5000, () => env.LootStarts > 0);
        env.Run(3000, () => ck.State == ChestKeyLooter.Phase.Waiting);
        Check.True(ck.Running, "a second key is carried: the run goes on");
        Check.Eq(ck.State, ChestKeyLooter.Phase.Waiting, "waiting for the chest to re-lock");
        Check.True(ck.Status.Contains("Waiting for Mana Forge Chest to re-lock"), "the status says so: " + ck.Status);
        int sentBefore = env.Sent.Count;
        long openedAt = env.OpenedAt;

        env.Run(ChestKeyLooter.RegenWaitMs - 20_000);
        Check.Eq(env.Sent.Count, sentBefore, "nothing sent while the regen timer runs");
        env.Run(40_000, () => env.Sent.Count > sentBefore);
        Check.True(env.Sent.Count > sentBefore, "the next key goes after the wait");
        Check.True(env.T - openedAt >= ChestKeyLooter.RegenWaitMs - 1000, $"not before {ChestKeyLooter.RegenWaitMs} ms after the open ({env.T - openedAt})");

        env.Run(60_000, () => !ck.Running);
        // The second key is spent too -> out of keys.
        Check.False(ck.Running, "out of keys after the second chest");
        Check.Eq(ck.Looted, 2, "two chests looted");
    }

    private static void ChestAlreadyUnlockedProbes()
    {
        var env = NewChest(out var ck);
        env.Unlimited = true;
        env.AutoLootMs = 1000;
        bool unlocked = false;
        env.OnKey = _ => unlocked ? ChestKeyLooter.ErrLockAlreadyUnlocked : 0u;   // first: unlocks; later: the chest never re-locks
        ck.Start(out _);
        env.Run(3000, () => env.LootStarts > 0);
        unlocked = true;
        env.Run(3000, () => ck.State == ChestKeyLooter.Phase.Waiting);
        Check.Eq(env.Sent.Where(s => s.StartsWith("key")).Count(), 1, "one key use so far");
        env.Run(ChestKeyLooter.RegenWaitMs + 2000);
        Check.True(env.Sent.Count(s => s.StartsWith("key")) >= 2, "after the regen wait the key is tried again");
        Check.Eq(env.Sent.Count(s => s == "open"), 1, "'already unlocked' on a later cycle does NOT open the empty chest again");
        env.Run(ChestKeyLooter.ProbeMs * (ChestKeyLooter.MaxProbes + 2));
        Check.False(ck.Running, "gave up after the probes");
        Check.Eq(env.Sent.Count(s => s.StartsWith("key")), 1 + ChestKeyLooter.MaxProbes + 1, "bounded: first use + one per probe");
        Check.True(Said(env).Contains("hasn't re-locked"), "says the chest never re-locked: " + Said(env));

        // First cycle on a chest that was never locked: opened, no key spent.
        var env2 = NewChest(out var ck2);
        env2.AutoLootMs = 1000;
        env2.OnKey = _ => ChestKeyLooter.ErrLockAlreadyUnlocked;
        ck2.Start(out _);
        env2.Run(3000, () => env2.LootStarts > 0);
        Check.Eq(string.Join(",", env2.Sent), "key 11,open", "'already unlocked' on the first cycle -> opens it");
    }

    private static void ChestSwitchesKey()
    {
        var env = NewChest(out var ck);
        env.Keys.Clear(); env.Keys.Add(11); env.Keys.Add(12);
        Serve(env, ck);
        env.AutoLootMs = 1000;
        ck.Start(out _);
        env.Run(3000, () => env.LootStarts > 0);
        Check.Eq(ck.KeyId, 11, "starts with the picked key");
        env.Run(3000, () => ck.State == ChestKeyLooter.Phase.Waiting);
        env.Run(ChestKeyLooter.RegenWaitMs + 5000, () => env.Sent.Count(s => s.StartsWith("key")) >= 2);
        Check.Eq(env.Sent.Last(s => s.StartsWith("key")), "key 12", "the other key of the same name is used once the first is gone");
        Check.Eq(ck.KeyId, 12, "remembered");
    }

    private static void ChestKeyErrors()
    {
        foreach (var (err, text) in new[]
        {
            (ChestKeyLooter.ErrKeyDoesntFit, "doesn't fit"),
            (ChestKeyLooter.ErrCannotLockOrUnlock, "can't be unlocked with a key"),
        })
        {
            var env = NewChest(out var ck);
            env.OnKey = _ => err;
            ck.Start(out _);
            env.Run(5000, () => !ck.Running);
            Check.False(ck.Running, $"error 0x{err:X} stops the run");
            Check.True(Said(env).Contains(text), $"says why ({text}): " + Said(env));
            Check.Eq(env.Sent.Count, 1, "one try, no retry of a hopeless key");
        }

        // The chest is open elsewhere: three waits, then it stops.
        var e3 = NewChest(out var c3);
        e3.OnKey = _ => ChestKeyLooter.ErrCannotLockWhatIsOpen;
        c3.Start(out _);
        e3.Run(60_000, () => !c3.Running);
        Check.False(c3.Running, "a chest that stays open ends the run");
        Check.Eq(e3.Sent.Count, ChestKeyLooter.MaxOpenElsewhere, "bounded tries");

        // 'Too busy' backs off and is bounded.
        var e4 = NewChest(out var c4);
        e4.OnKey = _ => ChestKeyLooter.ErrTooBusy;
        c4.Start(out _);
        e4.Run(120_000, () => !c4.Running);
        Check.False(c4.Running, "endless 'too busy' ends the run");
        Check.Eq(e4.Sent.Count, ChestKeyLooter.MaxBusy, "bounded tries");
    }

    private static void ChestBoundedFailures()
    {
        // The chest goes out of view.
        var env = NewChest(out var ck);
        env.OnKey = _ => null;
        ck.Start(out _);
        env.Run(500);
        env.Loaded = false;
        env.Run(ChestKeyLooter.GoneMs + 1000, () => !ck.Running);
        Check.False(ck.Running, "stopped when the chest is gone");
        Check.True(Said(env).Contains("is gone"), "says so");

        // The server never answers the key use.
        var e2 = NewChest(out var c2);
        e2.OnKey = _ => null;
        c2.Start(out _);
        e2.Run(120_000, () => !c2.Running);
        Check.False(c2.Running, "no answer ends the run");
        Check.Eq(e2.Sent.Count, ChestKeyLooter.MaxTimeouts, "after that many tries");
        Check.True(Said(e2).Contains("no answer"), "says why: " + Said(e2));

        // The chest never opens.
        var e3 = NewChest(out var c3);
        Serve(e3, c3, usesPerKey: 99);
        e3.Keys.Add(12);
        e3.OnOpen = () => false;
        c3.Start(out _);
        e3.Run(120_000, () => !c3.Running);
        Check.False(c3.Running, "a chest that won't open ends the run");
        Check.True(Said(e3).Contains("wouldn't open"), "says why: " + Said(e3));
        Check.Eq(e3.Sent.Count(s => s == "open"), ChestKeyLooter.MaxOpenFails, "bounded opens");

        // The looter never takes the chest.
        var e4 = NewChest(out var c4);
        Serve(e4, c4);
        e4.IgnoreStart = true;   // the corpse looter refused it
        c4.Start(out _);
        e4.Run(ChestKeyLooter.LootStartMs + 8000, () => !c4.Running);
        Check.False(c4.Running, "stopped");
        Check.True(Said(e4).Contains("never took"), "says why: " + Said(e4));

        // The loot session never ends.
        var e5 = NewChest(out var c5);
        Serve(e5, c5);
        c5.Start(out _);
        e5.Run(3000, () => e5.LootStarts > 0);
        e5.LootPending = false; e5.LootingNow = true;
        e5.Run(ChestKeyLooter.LootMaxMs + 5000, () => !c5.Running);
        Check.False(c5.Running, "a stuck loot session is cut off");
        Check.Eq(e5.Aborts, 1, "and the session is ended");
    }

    private static void ChestHoldsThenStops()
    {
        var env = NewChest(out var ck);
        Serve(env, ck);
        env.Hold = "combat";
        ck.Start(out _);
        env.Run(ChestKeyLooter.HoldMaxMs - 5000);
        Check.Eq(env.Sent.Count, 0, "nothing is sent while combat holds");
        Check.True(ck.Status.Contains("combat"), "the status says why it waits: " + ck.Status);
        env.Hold = null;
        env.Run(3000, () => env.Sent.Count > 0);
        Check.True(env.Sent.Count > 0, "it goes on when the hold ends");

        var e2 = NewChest(out var c2);
        e2.Hold = "the client is busy (1)";
        c2.Start(out _);
        e2.Run(ChestKeyLooter.HoldMaxMs + 5000, () => !c2.Running);
        Check.False(c2.Running, "a hold that never ends stops the run");
        Check.Eq(e2.Sent.Count, 0, "without sending anything");
        Check.True(Said(e2).Contains("waited"), "says why: " + Said(e2));
    }

    private static void ChestStartRefusalsAndStop()
    {
        var env = new ChestEnv();
        var ck = new ChestKeyLooter(env);
        env.Looter = ck;
        Check.False(ck.Start(out string why), "no chest");
        Check.True(why.Contains("pick a chest"), why);
        ck.SetChest(500, "Chest");
        Check.False(ck.Start(out why), "no key");
        Check.True(why.Contains("pick a key"), why);
        ck.SetKey(11, "Aged Key");
        env.Meters = 40;
        Check.False(ck.Start(out why), "chest too far");
        Check.True(why.Contains("40 m away"), why);
        env.Meters = 3; env.Loaded = false;
        Check.False(ck.Start(out why), "chest not loaded");
        env.Loaded = true; env.Keys.Clear();
        Check.False(ck.Start(out why), "key gone");
        Check.True(why.Contains("no 'Aged Key'"), why);
        Check.True(env.Said.Count(s => s.Contains("can't start")) == 5, "each refusal is said in chat");

        // Stop works in every phase and ends a loot session.
        env.Keys.Add(11);
        Serve(env, ck);
        ck.Start(out _);
        env.Run(3000, () => env.LootStarts > 0);
        env.LootPending = false; env.LootingNow = true;
        env.Run(300);
        Check.Eq(ck.State, ChestKeyLooter.Phase.Looting, "looting");
        ck.Stop("stopped by you");
        Check.False(ck.Running, "stopped");
        Check.Eq(env.Aborts, 1, "the loot session is ended and the chest closed");
        Check.True(Said(env).Contains("stopped by you"), "says it stopped");
        ck.Stop();   // already stopped
        Check.True(Said(env).Contains("not running"), "stopping a stopped looter says so");
        Check.False(ck.Running, "and does nothing");
    }

    private static void ChestMaxCycles()
    {
        var env = NewChest(out var ck);
        env.Unlimited = true;
        env.AutoLootMs = 300;
        env.Locked = true;   // the client reads the chest locked again at once; MinRelockMs still applies
        Serve(env, ck, spendKey: false);
        ck.Start(out _);
        env.Run(60_000_000, () => !ck.Running);
        Check.False(ck.Running, "ended");
        Check.Eq(ck.Looted, ChestKeyLooter.MaxCycles, "after MaxCycles chests");
        string said = Said(env);
        Check.True(said.Contains($"{ChestKeyLooter.MaxCycles} cycles"), "says why: " + said.Substring(Math.Max(0, said.Length - 200)));
    }

    // ── Quick give state machine ─────────────────────────────────────────────

    private sealed class GiveEnv : IQuickGiveEnv
    {
        public long T = 5_000_000;
        public long Now => T;
        public string TargetName = "Town Crier";
        public bool Loaded = true;
        public double Meters = 2;
        public bool Combat;
        public string? Hold;
        public readonly List<int> Items = new() { 1, 2, 3 };       // carried, in order
        public readonly Dictionary<int, int> Counts = new();
        public readonly List<(long At, int Item)> Sent = new();
        public readonly HashSet<int> Takes = new();                 // items the server accepts (empty = all)
        public readonly HashSet<int> Refuses = new();               // items it refuses with an error
        public bool Silent;                                         // never answers
        public int Refusals;
        public readonly List<string> Said = new(), Logs = new();
        public QuickGive Gv = null!;
        public int PendingLand;                                     // item in flight, lands after LandMs
        public long LandAt;
        public long LandMs = 200;

        public bool TargetLoaded(int targetId, out string name, out double meters) { name = TargetName; meters = Meters; return Loaded; }
        public bool InCombat() => Combat;
        public string? HoldReason() => Hold;
        public bool NextItem(GiveItemKey key, ISet<int> skip, out int itemId, out int count)
        {
            foreach (int i in Items)
                if (!skip.Contains(i)) { itemId = i; count = Counts.TryGetValue(i, out int c) ? c : 1; return true; }
            itemId = 0; count = 0; return false;
        }
        public int CountCarried(GiveItemKey key) => Items.Count;
        public bool Landed(int itemId, int count) => !Items.Contains(itemId);
        public bool Send(int itemId, int targetId)
        {
            Sent.Add((T, itemId));
            if (Silent) return true;
            if (Refuses.Contains(itemId)) { Refusals++; return true; }
            PendingLand = itemId; LandAt = T + LandMs;
            return true;
        }
        public int RefusalSeq() => Refusals;
        public void Say(string text) => Said.Add(text);
        public void Log(string text) => Logs.Add(text);

        public void Run(long ms, Func<bool>? until = null)
        {
            long end = T + ms;
            while (T < end && (until == null || !until()))
            {
                T += 50;
                if (PendingLand != 0 && T >= LandAt) { Items.Remove(PendingLand); PendingLand = 0; }
                Gv.Tick();
            }
        }
    }

    private static GiveEnv NewGive(out QuickGive qg)
    {
        var env = new GiveEnv();
        qg = new QuickGive(env);
        env.Gv = qg;
        qg.SetItem(1, new GiveItemKey(0x1234, "Mana Mote"));
        qg.SetTarget(900, "Town Crier");
        return env;
    }

    private static void GiveOneAtATime()
    {
        var env = NewGive(out var qg);
        Check.True(qg.Start(out _), "starts");
        Check.True(env.Said[0].Contains("Town Crier") && env.Said[0].Contains("Mana Mote"), "the start line names the target and the item: " + env.Said[0]);
        env.Run(20_000, () => !qg.Running);
        Check.Eq(string.Join(",", env.Sent.Select(s => s.Item)), "1,2,3", "each item once, in order");
        for (int i = 1; i < env.Sent.Count; i++)
            Check.True(env.Sent[i].At - env.Sent[i - 1].At >= env.LandMs + QuickGive.PaceMs - 50, $"give {i + 1} waited for the last to land and the pace ({env.Sent[i].At - env.Sent[i - 1].At} ms)");
        Check.Eq(qg.Given, 3, "three given");
        Check.False(qg.Running, "done");
        Check.True(env.Said.Last().Contains("done"), "says it is done: " + env.Said.Last());
        Check.True(qg.Status.Contains("3 item(s)"), "the status keeps the result: " + qg.Status);
    }

    private static void GiveRefusals()
    {
        // One refused item is skipped; the others go.
        var env = NewGive(out var qg);
        env.Refuses.Add(2);
        qg.Start(out _);
        env.Run(30_000, () => !qg.Running);
        Check.Eq(string.Join(",", env.Sent.Select(s => s.Item)), "1,2,3", "item 2 refused once, not retried; item 3 given");
        Check.Eq(qg.Given, 2, "two given");
        Check.False(qg.Running, "it ends when only the refused one is left");

        // Three in a row stop it.
        var e2 = NewGive(out var q2);
        e2.Items.Clear(); e2.Items.AddRange(new[] { 1, 2, 3, 4, 5 });
        foreach (int i in e2.Items) e2.Refuses.Add(i);
        q2.Start(out _);
        e2.Run(60_000, () => !q2.Running);
        Check.False(q2.Running, "stopped");
        Check.Eq(e2.Sent.Count, QuickGive.MaxRefusals, "after that many refusals in a row, not the whole pack");
        Check.True(e2.Said.Last().Contains("refusals in a row"), "says why: " + e2.Said.Last());

        // No answer at all counts as a refusal after the confirm timeout.
        var e3 = NewGive(out var q3);
        e3.Silent = true;
        q3.Start(out _);
        e3.Run(120_000, () => !q3.Running);
        Check.False(q3.Running, "stopped");
        Check.Eq(e3.Sent.Count, QuickGive.MaxRefusals, "unanswered gives are bounded the same way");
        Check.True(e3.Sent[1].At - e3.Sent[0].At >= QuickGive.ConfirmMs, "each waited the confirm time before the next");
    }

    private static void GiveStopConditions()
    {
        void Case(string name, Action<GiveEnv> change, string expect)
        {
            var env = NewGive(out var qg);
            env.Items.Clear(); env.Items.AddRange(Enumerable.Range(1, 30));
            qg.Start(out _);
            env.Run(600);
            Check.True(qg.Running, name + ": running before");
            change(env);
            env.Run(5000, () => !qg.Running);
            Check.False(qg.Running, name + ": stops");
            Check.True(env.Said.Last().Contains(expect), $"{name}: says '{expect}' ({env.Said.Last()})");
        }
        Case("vanishes", e => e.Loaded = false, "is gone");
        Case("renamed", e => e.TargetName = "Somebody Else", "the target changed");
        Case("moves away", e => e.Meters = 30, "out of give range");
        Case("fight", e => e.Combat = true, "a fight started");

        // A target that comes back within the grace is fine.
        var env2 = NewGive(out var q2);
        env2.Items.Clear(); env2.Items.AddRange(Enumerable.Range(1, 30));
        q2.Start(out _);
        env2.Run(300);
        env2.Loaded = false; env2.Run(500); env2.Loaded = true; env2.Run(500);
        Check.True(q2.Running, "a flicker shorter than the grace doesn't stop it");
        q2.Stop("stopped by you");
        Check.False(q2.Running, "Stop always works");
    }

    private static void GiveStartRefusals()
    {
        var env = NewGive(out var qg);
        env.Meters = 12;
        Check.False(qg.Start(out string why), "target 12 m away");
        Check.True(why.Contains("give range is 10 m"), why);
        env.Meters = 2; env.Loaded = false;
        Check.False(qg.Start(out why), "target not loaded");
        env.Loaded = true; env.TargetName = "Other";
        Check.False(qg.Start(out why), "target is someone else now");
        env.TargetName = "Town Crier"; env.Combat = true;
        Check.False(qg.Start(out why), "in a fight");
        env.Combat = false; env.Items.Clear();
        Check.False(qg.Start(out why), "nothing to give");
        Check.False(qg.Running, "never started");
        Check.Eq(env.Sent.Count, 0, "nothing sent");

        var e2 = new GiveEnv(); var q2 = new QuickGive(e2); e2.Gv = q2;
        Check.False(q2.Start(out why), "no item picked");
        q2.SetItem(1, new GiveItemKey(1, "X"));
        Check.False(q2.Start(out why), "no target picked");

        // The client is busy: the give waits, then goes.
        var e3 = NewGive(out var q3);
        e3.Hold = "the client is busy (1)";
        q3.Start(out _);
        e3.Run(2000);
        Check.Eq(e3.Sent.Count, 0, "nothing while the client is busy");
        e3.Hold = null;
        e3.Run(500, () => e3.Sent.Count > 0);
        Check.True(e3.Sent.Count > 0, "it goes when the client is free");
    }

    // ── Plugin glue ──────────────────────────────────────────────────────────

    private const uint Player = 0x50000E01;
    private const uint Chest = 0x7A9B4101;
    private const uint Key = 0x80290001, Key2 = 0x80290002;
    private const uint Gem = 0x80290101;
    private const uint Npc = 0x80290200, Bystander = 0x80290201, Monster = 0x80290202, Corpse = 0x80290203;
    private const uint Mote1 = 0x80290301, Mote2 = 0x80290302, Mote3 = 0x80290303, WornMote = 0x80290304, OtherMote = 0x80290305, ClonedMote = 0x80290306;

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = false,       // the Item Tool works with the macro off
            EnableLooting = false,
            EnableCombat = false,
            LootOwnership = 0,
            LootContentSettleMs = 0,
            LootEmptyCorpseMs = 0,
            StopLootingWhenPackFull = false,
            GiveQueueIntervalMs = 0,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host, string profile, params uint[] objects)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            S.CurrentLootPath = profile;
            foreach (uint id in objects) Cache.OnCreateObject(id);
            Cache.Tick();
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object?[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        /// <summary>One plugin tick's worth: the Item Tool, then the loot tick (what OnTick runs).</summary>
        public void Tick()
        {
            Call("TickItemTool");
            Set("_activity", BotActivity.Looting);
            Call("TickCorpseOpening");
        }

        public void TickUntil(Func<bool> done, int max = 600)
        {
            for (int i = 0; i < max && !done(); i++) { Tick(); Thread.Sleep(10); }
        }

        public void ServerOpens(uint container)
        {
            FakeHost.GroundContainer = container;
            P.OnViewObjectContents(container);
            P.OnUpdateObjectInventory(container);
        }

        public void ServerCloses(uint container)
        {
            if (FakeHost.GroundContainer == container) FakeHost.GroundContainer = 0;
            P.OnStopViewingObjectContents(container);
        }

        /// <summary>The player types / presses: a trusted command.</summary>
        public void Cmd(string args) => P.QueueItemToolCommand(args, trusted: true);
    }

    private static string ProfilePath(string name)
    {
        string path = System.IO.Path.Combine(Program.TempRoot, name);
        var p = new LootProfile { Name = "itemtool" };
        var gems = new LootRule { Name = "Gems", Action = LootAction.Keep };
        gems.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        p.Rules.Add(gems);
        p.Save(path);
        return path;
    }

    private static void BaseWorld()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        FakeHost.WeaponCalls = true;
        FakeHost.CloseContainerCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.GiveCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.WeenieErrorCalls = true;
        var pose = (0x1048002Au, 100f, 100f, 0f);
        FakeHost.Names[Player] = "Dargoth Hera";
        FakeHost.Positions[Player] = pose;
        FakeHost.PlayerPose = pose;

        FakeHost.Names[Chest] = "Mana Forge Chest";
        FakeHost.ItemTypes[Chest] = 0x200;
        FakeHost.Positions[Chest] = (pose.Item1, 102f, 100f, 0f);
        FakeHost.NotAttackable.Add(Chest);
        FakeHost.Containers[Chest] = 0;
        FakeHost.Names[Key] = "Aged Key"; FakeHost.ItemTypes[Key] = 0x1000; FakeHost.Containers[Key] = Player;
        FakeHost.Names[Key2] = "Aged Key"; FakeHost.ItemTypes[Key2] = 0x1000; FakeHost.Containers[Key2] = Player;
        FakeHost.Names[Gem] = "Black Opal"; FakeHost.ItemTypes[Gem] = 0x800; FakeHost.Containers[Gem] = Chest; FakeHost.Appraised.Add(Gem);

        FakeHost.Names[Npc] = "Town Crier"; FakeHost.ItemTypes[Npc] = 0x10; FakeHost.NotAttackable.Add(Npc);
        FakeHost.Positions[Npc] = (pose.Item1, 101f, 100f, 0f);
        FakeHost.Names[Bystander] = "Some Player"; FakeHost.ItemTypes[Bystander] = 0x10; FakeHost.Bitfields[Bystander] = 0x8;   // BF_PLAYER
        FakeHost.Positions[Bystander] = (pose.Item1, 101f, 101f, 0f);
        FakeHost.Names[Monster] = "Drudge Lurker"; FakeHost.ItemTypes[Monster] = 0x10;
        FakeHost.Positions[Monster] = (pose.Item1, 103f, 100f, 0f);
        FakeHost.Bitfields[Monster] = 0x10;   // BF_ATTACKABLE: a monster, not an NPC
        foreach (var (id, name) in new[] { (Mote1, "Mana Mote"), (Mote2, "Mana Mote"), (Mote3, "Mana Mote"), (WornMote, "Mana Mote"), (OtherMote, "Lesser Mana Mote"), (ClonedMote, "Mana Mote") })
        {
            FakeHost.Names[id] = name; FakeHost.Containers[id] = Player; FakeHost.ItemTypes[id] = 0x80;
            FakeHost.Wcids[id] = 0x1111;
        }
        FakeHost.Wcids[ClonedMote] = 0x2222;   // same name, a different weenie: not the picked item
        FakeHost.Wielded[WornMote] = 0x8000;
    }

    private static Bot MakeBot(string profile = "itemtool-chest.json")
    {
        var host = FakeHost.Create(Player);
        return new Bot(host, ProfilePath(profile), Chest, Key, Key2, Gem, Npc, Bystander, Monster, Mote1, Mote2, Mote3, WornMote, OtherMote, ClonedMote);
    }

    private static string Chat() => string.Join(" | ", FakeHost.Chat);

    private static void PluginChestRun()
    {
        BaseWorld();
        var bot = MakeBot();
        FakeHost.SelectedItem = Chest;
        bot.Cmd("chest set");
        bot.Tick();
        FakeHost.SelectedItem = Key;
        bot.Cmd("chest key");
        bot.Tick();
        Check.True(Chat().Contains("chest = Mana Forge Chest") && Chat().Contains("key = Aged Key"), "picks are confirmed in chat: " + Chat());
        Check.False(bot.S.IsMacroRunning, "the macro is off");

        // The server: the key unlocks (chat + UseDone), the chest opens (ViewContents + contents + UseDone).
        FakeHost.OnUseObjectOn = (src, tgt) =>
        {
            Check.Eq(src, Key, "the key is the source");
            Check.Eq(tgt, Chest, "the chest is the target");
            bot.Call("ItemToolChat", "The Mana Forge Chest has been unlocked.");
            bot.Call("ItemToolChat", "Your key is used up.");
            FakeHost.Containers.Remove(Key);   // the key is consumed
            FakeHost.UseDone(0);
        };
        FakeHost.OnUseObject = id =>
        {
            if (id != Chest) return;
            FakeHost.UseDone(0);
            bot.ServerOpens(Chest);
        };
        bot.Cmd("chest start");
        // Key2 has the same name: it goes on after the first, so the run continues until it is spent too.
        bot.TickUntil(() => FakeHost.Uses.Contains(Chest));
        Check.Eq(FakeHost.UsesOn.Count, 1, "one key use");
        Check.True(FakeHost.Uses.Contains(Chest), "then the chest is used (opened)");
        bot.TickUntil(() => FakeHost.ExternalMoves.Any(m => m.Id == Gem), 800);
        Check.True(FakeHost.ExternalMoves.Contains((Gem, Player, 0)), "the loot profile's gem is taken (macro off, Looting off)");
        FakeHost.Containers[Gem] = Player;
        int closes = FakeHost.Closes.Count;
        bot.TickUntil(() => FakeHost.Closes.Count > closes);
        Check.True(FakeHost.Closes.Contains(Chest), "the chest is closed when done");
        bot.ServerCloses(Chest);
        bot.TickUntil(() => Chat().Contains("looted Mana Forge Chest"), 200);
        Check.True(Chat().Contains("looted Mana Forge Chest (1 so far)"), "says it looted: " + Chat());
        Check.True(Chat().Contains("Chest looter stopped") || Chat().Contains("Waiting") || bot.Get<ChestKeyLooter>("_chestKey").Running, "the second key keeps it going");

        // Take the second key away: the next check ends the run, out of keys.
        FakeHost.Containers.Remove(Key2);
        bot.TickUntil(() => !bot.Get<ChestKeyLooter>("_chestKey").Running, 400);
        Check.False(bot.Get<ChestKeyLooter>("_chestKey").Running, "stopped");
        Check.True(Chat().Contains("out of keys"), "says it is out of keys: " + Chat());
        Check.Eq(bot.Get<int>("_forcedLootContainerId"), 0, "the forced-loot flag is cleared");
    }

    private static void PluginChestPicks()
    {
        BaseWorld();
        var bot = MakeBot("itemtool-picks.json");
        FakeHost.SelectedItem = 0;
        bot.Cmd("chest set"); bot.Tick();
        Check.True(Chat().Contains("select the chest in the world"), "nothing selected: " + Chat());
        FakeHost.Chat.Clear();
        FakeHost.SelectedItem = Key;
        bot.Cmd("chest set"); bot.Tick();
        Check.True(Chat().Contains("in your pack"), "a pack item is not a chest: " + Chat());
        FakeHost.Chat.Clear();
        FakeHost.SelectedItem = Chest;
        bot.Cmd("chest key"); bot.Tick();
        Check.True(Chat().Contains("must be in your pack"), "a world object is not a key: " + Chat());
        FakeHost.Chat.Clear();
        FakeHost.SelectedItem = WornMote;
        bot.Cmd("chest key"); bot.Tick();
        Check.True(Chat().Contains("worn or wielded"), "worn gear is not a key: " + Chat());

        // House storage is never a chest.
        FakeHost.Chat.Clear();
        FakeHost.Names[Corpse] = "Storage";
        FakeHost.Positions[Corpse] = (0x1048002Au, 101f, 99f, 0f);
        FakeHost.Containers[Corpse] = 0;
        bot.Cache.OnCreateObject(Corpse); bot.Cache.Tick();
        FakeHost.SelectedItem = Corpse;
        bot.Cmd("chest set"); bot.Tick();
        Check.True(Chat().Contains("house storage"), "storage is refused: " + Chat());

        // Start with nothing picked says what is missing and sends nothing.
        FakeHost.Chat.Clear();
        bot.Cmd("chest start"); bot.Tick();
        Check.True(Chat().Contains("pick a chest first"), "start without a chest: " + Chat());
        Check.Eq(FakeHost.UsesOn.Count + FakeHost.Uses.Count, 0, "nothing sent to the server");
        FakeHost.Chat.Clear();
        bot.Cmd("chest stop"); bot.Tick();
        Check.True(Chat().Contains("not running"), "stop when idle: " + Chat());
    }

    private static void PluginQuickGive()
    {
        BaseWorld();
        var bot = MakeBot("itemtool-give.json");
        FakeHost.SelectedItem = Mote1;
        bot.Cmd("giveitem");
        bot.Tick();
        FakeHost.SelectedItem = Npc;
        bot.Cmd("givetarget");
        bot.Tick();
        Check.True(Chat().Contains("item = Mana Mote") && Chat().Contains("target = Town Crier"), "picks are confirmed: " + Chat());
        bot.Cmd("give");
        bot.Tick();
        Check.True(Chat().Contains("giving every 'Mana Mote'") && Chat().Contains("to Town Crier"), "the start line names item and target: " + Chat());
        bot.TickUntil(() => !bot.Get<QuickGive>("_quickGive").Running, 1500);
        Check.False(bot.Get<QuickGive>("_quickGive").Running, "done");
        var given = FakeHost.Gives.Select(g => g.Item).ToArray();
        Check.Eq(string.Join(",", given.OrderBy(x => x).Select(x => x.ToString("X8"))), string.Join(",", new[] { Mote1, Mote2, Mote3 }.Select(x => x.ToString("X8"))), "exactly the three plain Mana Motes: not the worn one, 'Lesser Mana Mote' or the other weenie");
        Check.True(FakeHost.Gives.All(g => g.Target == Npc && g.Amount == 0), "all to the NPC, whole objects, with AC's give action");
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "no MoveItemExternal");
        Check.True(Chat().Contains("Quick give stopped: done"), "ends with a result line: " + Chat());
    }

    private static void PluginGiveTrust()
    {
        BaseWorld();
        var bot = MakeBot("itemtool-trust.json");
        FakeHost.SelectedItem = Mote1; bot.Cmd("giveitem"); bot.Tick();
        FakeHost.SelectedItem = Npc; bot.Cmd("givetarget"); bot.Tick();
        FakeHost.Chat.Clear();

        // A meta's /ra line.
        bot.Call("HandleRaCommand", "/ra itemtool give");
        bot.Tick();
        Check.Eq(FakeHost.Gives.Count, 0, "a meta line gives nothing");
        Check.True(Chat().Contains("only from the Item Tool panel or a command you type yourself"), "and says why: " + Chat());
        Check.False(bot.Get<QuickGive>("_quickGive").Running, "not running");

        // A script's SubmitCommand.
        FakeHost.Chat.Clear();
        bot.P.ScriptSubmitCommand("/ra itemtool give");
        bot.Tick();
        Check.Eq(FakeHost.Gives.Count, 0, "a script line gives nothing");
        Check.False(bot.Get<QuickGive>("_quickGive").Running, "not running");

        // A typed line (the chat bar): runs.
        int eat = 0;
        bot.P.OnChatBarEnter("/ra itemtool give", ref eat);
        bot.Tick();
        Check.Eq(eat, 1, "the line is eaten");
        Check.True(bot.Get<QuickGive>("_quickGive").Running, "a typed line starts it");

        // Stop from a meta works.
        bot.Call("HandleRaCommand", "/ra itemtool givestop");
        bot.Tick();
        Check.False(bot.Get<QuickGive>("_quickGive").Running, "a meta can stop it");
        // The panel's button.
        FakeHost.Gives.Clear();
        bot.Cmd("give");
        bot.TickUntil(() => FakeHost.Gives.Count > 0, 100);
        Check.True(FakeHost.Gives.Count > 0, "the panel's button starts it");
        bot.Cmd("stop"); bot.Tick();
        Check.False(bot.Get<QuickGive>("_quickGive").Running, "/ra itemtool stop ends it");
    }

    private static void PluginGiveTargets()
    {
        BaseWorld();
        var bot = MakeBot("itemtool-targets.json");
        FakeHost.SelectedItem = Monster; bot.Cmd("givetarget"); bot.Tick();
        Check.True(Chat().Contains("player or an NPC"), "a monster is refused: " + Chat());
        FakeHost.Chat.Clear();
        FakeHost.SelectedItem = Mote2; bot.Cmd("givetarget"); bot.Tick();
        Check.True(Chat().Contains("in your pack"), "a pack item is refused: " + Chat());
        FakeHost.Chat.Clear();
        FakeHost.SelectedItem = Player; bot.Cmd("givetarget"); bot.Tick();
        Check.True(Chat().Contains("that is you"), "you are refused: " + Chat());
        FakeHost.Chat.Clear();
        FakeHost.SelectedItem = Bystander; bot.Cmd("givetarget"); bot.Tick();
        Check.True(Chat().Contains("target = PLAYER Some Player"), "a player is allowed and shown as one: " + Chat());
        FakeHost.SelectedItem = Mote1; bot.Cmd("giveitem"); bot.Tick();
        FakeHost.Chat.Clear();
        bot.Cmd("give"); bot.Tick();
        Check.True(Chat().Contains("giving every 'Mana Mote'") && Chat().Contains("to Some Player"), "the start line names the player: " + Chat());
        bot.Cmd("stop"); bot.Tick();
    }
}
