using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// R-20261029-122319-321f (Conquest, RynthAi 2026.10.29.1): "If max loot attempts are reached it
// locks up on looting other items". A pack move was retried every ~0.5 s once the character stood
// still, and after 3 tries (~1.6 s) the pack counted as full for 5 min. That server answered a
// pickup 1.5-2.7 s after the send (one confirmation landed after attempt 3 had gone out), and a
// resend while a move is out is dropped by the client (one pending inventory request) or turned
// away by ACE as too busy. Side packs were marked full one after another until looting stopped.
// Also: a full main pack's refusals (InventoryServerSaveFailed, "error 0x0") were all taken for
// "too busy" and resent for free, 6 sends over 18 s.
// The server is simulated (SlowAce): a 1.7 s pickup motion, room per pack, refusals for room only
// after the motion, "too busy" on arrival, the queued move behind a Return, and the client's own
// one-request slot (a move sent while the last is unanswered never leaves the client).
// The loot clock (CorpseOpenController.CorpseClock) is moved by hand.
internal static class LootRetryTimingTests
{
    private const uint Player = 0x50000D31;
    private const uint Corpse = 0x80263100;
    private const uint SidePack = 0x80263001;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint LongDescKey = 16, ItemsCapacityKey = 6;
    private const string Me = "Brannoc Vell";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const long TickMs = 25;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("loot retry (R-20261029-122319-321f): with 1.7 s answers no move is resent while one is out, no pack is marked full, every item lands", SlowServerNoFalseFull);
        r.Add("loot retry (R-20261029-122319-321f): a full main pack's refusals after the pickup count as tries (no free resends) and mark it full; the items go to the side pack", FullMainPackRefusals);
        r.Add("loot retry (R-20261029-122319-321f): unanswered moves are retried only after the answer wait; their pack marks end with the item, the next item starts fresh", UnansweredMovesDontPoison);
        r.Add("loot retry (R-20261029-122319-321f): an item that lands late, after its pack was given up on, un-marks that pack", LateLandingUnmarks);
        r.Add("loot retry (R-20261029-122319-321f): a refusal on arrival is 'too busy' (free resend), one after the pickup is the pack's answer", RefusalTiming);
        r.Add("loot retry (R-20261029-122319-321f): the answer wait grows with the server's measured answers, within 4-8 s", AnswerWaitAdapts);
        r.Add("loot retry (R-20261029-122319-321f): PackRefusals keeps confirmed marks and drops the unconfirmed ones with the item", PackRefusalMarks);
    }

    // ── Clock and world ───────────────────────────────────────────────────────

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 70_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    private static void World(bool sidePack, params uint[] gems)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;        // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;         // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = true; // CloseContainer + GetGroundContainerId
        FakeHost.ServerGates = true;         // GetCastBusyState (the pickup motions)
        FakeHost.WeenieErrorCalls = true;    // GetLastWeenieError (the refusals)
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Brier Wasp";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        if (sidePack)
        {
            FakeHost.Names[SidePack] = "Backpack";
            FakeHost.ItemTypes[SidePack] = ItemTypeContainer;
            FakeHost.Ints[(SidePack, ItemsCapacityKey)] = 24;
            FakeHost.Containers[SidePack] = Player;
        }
        foreach (uint g in gems)
        {
            FakeHost.Names[g] = $"Gem {g & 0xFF}";
            FakeHost.ItemTypes[g] = ItemTypeGem;
            FakeHost.Containers[g] = Corpse;
            FakeHost.Appraised.Add(g);
        }
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;
        public SlowAce? Sim;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });

            string profile = Path.Combine(Program.TempRoot, "loot-retry-timing.json");
            var p = new LootProfile { Name = "gems" };
            var rule = new LootRule { Name = "Gems", Action = LootAction.Keep };
            rule.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(rule);
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        /// <summary>One plugin tick: the clock moves on, the simulated server catches up, the looter runs.</summary>
        public void Tick()
        {
            _now += TickMs;
            Sim?.Step(_now);
            Set("_activity", BotActivity.Looting);
            Call("TickCorpseOpening");
        }

        public void TickUntil(Func<bool> done, long maxMs)
        {
            long end = _now + maxMs;
            while (_now < end && !done()) Tick();
        }

        public int MovesOf(uint id) => FakeHost.ExternalMoves.Count(m => m.Id == id);

        public RynthCore.Plugin.RynthAi.Loot.PackRefusals Refusals => Get<RynthCore.Plugin.RynthAi.Loot.PackRefusals>("_packRefusals");

        public bool Completed => Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)Corpse));
    }

    /// <summary>
    /// ACE's pickup (Player_Inventory.cs): the move arrives, the pickup motion plays (PickupMs), then
    /// the item goes into the pack or, with no room there, "Unable to put X into container" +
    /// InventoryServerSaveFailed(item, no error code); then the Return. A move arriving during the
    /// Return is queued (NextPickup), one during the motion is "You're too busy". The client keeps
    /// one pending inventory request: a move sent while one is unanswered never leaves the client.
    /// <see cref="Lag"/>: extra delay before the server takes up an item's first move (a lag spike);
    /// <see cref="NeverAnswer"/>: items whose moves the server never answers. The engine empties a
    /// pending request that has had no answer for 10 s (ClientActionGates.PendingTimeoutMs).
    /// </summary>
    private sealed class SlowAce
    {
        public long Rtt = 200, PickupMs = 1700, ReturnMs = 740;
        public readonly Dictionary<uint, int> Room = new();    // the server's free slots per pack (absent: plenty)
        public readonly Dictionary<uint, long> Lag = new();
        public readonly HashSet<uint> NeverAnswer = new();
        public long ClientPendingTimeoutMs = 10_000;
        public int TooBusy, LocalRefused, Queued, RoomRefusals;
        public readonly List<(uint Id, uint Pack, long At)> Landed = new();

        private enum St { Idle, Pickup, Return }
        private St _st;
        private (uint Id, uint Pack) _next;
        private int _seenMoves;
        private uint _clientPending;
        private long _clientPendingAt;
        private readonly List<(long At, int Seq, Action Act)> _events = new();
        private int _seq;

        private void At(long at, Action act) => _events.Add((at, _seq++, act));

        public void Step(long now)
        {
            while (_seenMoves < FakeHost.ExternalMoves.Count)
            {
                var m = FakeHost.ExternalMoves[_seenMoves++];
                if (_clientPending != 0 && now - _clientPendingAt >= ClientPendingTimeoutMs) _clientPending = 0;   // the engine lets it go
                if (_clientPending != 0) { LocalRefused++; continue; }   // "You can only move or use one item at a time"
                _clientPending = m.Id;
                _clientPendingAt = now;
                if (NeverAnswer.Contains(m.Id)) continue;
                long arrive = now - TickMs + Rtt / 2;
                if (Lag.Remove(m.Id, out long lag)) arrive += lag;
                At(arrive, () => Receive(m.Id, m.Target, arrive));
            }
            while (true)
            {
                var due = _events.Where(e => e.At <= now).OrderBy(e => e.At).ThenBy(e => e.Seq).ToList();
                if (due.Count == 0) break;
                var e = due[0];
                _events.Remove(e);
                e.Act();
            }
        }

        private void Answered(uint id) { if (_clientPending == id) _clientPending = 0; }

        private void Receive(uint id, uint pack, long ts)
        {
            if (_st == St.Idle) { StartPickup(id, pack, ts); return; }
            if (_st == St.Return && _next.Id == 0) { _next = (id, pack); Queued++; return; }
            TooBusy++;
            At(ts + Rtt / 2, () =>
            {
                FakeHost.WeenieError(0x1D);               // YoureTooBusy
                FakeHost.WeenieError(0, 0x00A0, id);      // InventoryServerSaveFailed(item)
                Answered(id);
            });
        }

        private void StartPickup(uint id, uint pack, long ts)
        {
            _st = St.Pickup;
            At(ts + Rtt / 2, () => FakeHost.CastBusy = 1);
            At(ts + PickupMs, () => Finish(id, pack, ts + PickupMs));
        }

        private void Finish(uint id, uint pack, long ts)
        {
            _st = St.Return;
            if (Room.TryGetValue(pack, out int free) && free <= 0)
            {
                RoomRefusals++;
                At(ts + Rtt / 2, () =>
                {
                    FakeHost.WeenieError(0, 0x00A0, id);  // "Unable to put X into container" + save failed, no code
                    Answered(id);
                });
            }
            else
            {
                if (Room.ContainsKey(pack)) Room[pack] = free - 1;
                At(ts + Rtt / 2, () =>
                {
                    FakeHost.Containers[id] = pack;
                    Landed.Add((id, pack, ts + Rtt / 2));
                    Answered(id);
                });
            }
            At(ts + ReturnMs, () => ReturnDone(ts + ReturnMs));
        }

        private void ReturnDone(long ts)
        {
            if (_next.Id != 0)
            {
                var n = _next;
                _next = default;
                StartPickup(n.Id, n.Pack, ts);
                return;
            }
            _st = St.Idle;
            At(ts + Rtt / 2, () => FakeHost.CastBusy = 0);
        }
    }

    /// <summary>Claims and opens the corpse (the server's ViewContents), with the side pack known to the cache.</summary>
    private static Bot Open(SlowAce sim, bool sidePack, params uint[] gems)
    {
        var host = FakeHost.Create(Player);
        var bot = new Bot(host) { Sim = sim };
        var ids = new List<uint> { Corpse };
        if (sidePack) ids.Add(SidePack);
        ids.AddRange(gems);
        foreach (uint id in ids) bot.Cache.OnCreateObject(id);
        for (int i = 0; i < 50; i++) bot.Cache.Tick();
        if (sidePack)
            Check.True(bot.Cache.GetDirectInventory(forceRefresh: true).Any(o => o.Id == unchecked((int)SidePack) && o.ObjectClass == AcObjectClass.Container),
                "the side pack is in the inventory as a container");
        bot.Tick();
        Check.True(FakeHost.Uses.Contains(Corpse), "the corpse is opened");
        FakeHost.UseDone(0);
        FakeHost.GroundContainer = Corpse;
        bot.P.OnViewObjectContents(Corpse);
        return bot;
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));

    /// <summary>Ticks until <paramref name="done"/>, noting the clock at each new move.</summary>
    private static List<(uint Id, long At)> Run(Bot bot, Func<bool> done, long maxMs)
    {
        var sends = new List<(uint, long)>();
        int seen = FakeHost.ExternalMoves.Count;
        long end = _now + maxMs;
        while (_now < end && !done())
        {
            bot.Tick();
            while (seen < FakeHost.ExternalMoves.Count) sends.Add((FakeHost.ExternalMoves[seen++].Id, _now));
        }
        return sends;
    }

    // ── 1. A slow server, room everywhere ─────────────────────────────────────

    private static void SlowServerNoFalseFull() => Clocked(() =>
    {
        const uint G1 = 0x80263101, G2 = 0x80263102, G3 = 0x80263103, G4 = 0x80263104;
        World(sidePack: true, G1, G2, G3, G4);
        var sim = new SlowAce();
        var bot = Open(sim, sidePack: true, G1, G2, G3, G4);

        Run(bot, () => bot.Completed, 60_000);

        foreach (uint g in new[] { G1, G2, G3, G4 })
        {
            Check.Eq(bot.MovesOf(g), 1, $"0x{g:X8} is sent once: its answer (~1.8 s, or behind the last Return) is waited for");
            Check.True(sim.Landed.Any(l => l.Id == g), $"0x{g:X8} lands");
        }
        Check.Eq(sim.LocalRefused, 0, "no move sent while another was unanswered (the client would drop it)");
        Check.Eq(sim.TooBusy, 0, "no 'too busy' from the server");
        Check.False(Logged("took none of"), "no pack judged full");
        Check.False(Logged("pickup failed"), "nothing given up on");
        Check.False(Logged("retrying use"), "no retries at all");
        Check.False(bot.Refusals.IsRefused(unchecked((int)Player), _now), "the main pack is not marked");
        Check.True(bot.Completed, "the corpse is finished");
    });

    // ── 2. The main pack is full (the client counts room) ─────────────────────

    private static void FullMainPackRefusals() => Clocked(() =>
    {
        const uint Map = 0x80263111, G2 = 0x80263112;
        World(sidePack: true, Map, G2);
        var sim = new SlowAce();
        sim.Room[Player] = 0;    // the server's main pack is full; the client's count says room
        var bot = Open(sim, sidePack: true, Map, G2);

        Run(bot, () => bot.Completed, 60_000);

        var mapMoves = FakeHost.ExternalMoves.Where(m => m.Id == Map).ToList();
        Check.Eq(mapMoves.Count(m => m.Target == Player), 3, "three tries into the main pack (each refused after the pickup ran), no free resends");
        Check.False(Logged("as too busy"), "a refusal after the pickup is not taken for 'too busy'");
        Check.True(Logged("the main pack took none of 3 tries") && Logged("the server refused 3 after the pickup ran") && Logged("treating it as full"),
            "the main pack is judged full on the server's refusals");
        Check.True(mapMoves.Last().Target == SidePack && sim.Landed.Any(l => l.Id == Map && l.Pack == SidePack), "then the map goes into the side pack and lands");
        Check.True(FakeHost.ExternalMoves.Where(m => m.Id == G2).All(m => m.Target == SidePack), "the next item goes straight to the side pack");
        Check.True(sim.Landed.Any(l => l.Id == G2), "and lands");
        Check.True(bot.Refusals.IsConfirmed(unchecked((int)Player), _now), "the main pack stays marked (confirmed by the server)");
        Check.False(bot.Refusals.IsRefused(unchecked((int)SidePack), _now), "the side pack is not marked");
        Check.Eq(sim.LocalRefused, 0, "no move sent while another was unanswered");
    });

    // ── 3. Moves the server never answers ─────────────────────────────────────

    private static void UnansweredMovesDontPoison() => Clocked(() =>
    {
        const uint Lost = 0x80263121, Next = 0x80263122;
        World(sidePack: true, Lost, Next);
        var sim = new SlowAce();
        sim.NeverAnswer.Add(Lost);   // the server never answers the lost item's moves
        var bot = Open(sim, sidePack: true, Lost, Next);
        var sends = Run(bot, () => bot.Completed, 90_000).Where(s => s.Id == Lost).ToList();

        var lostMoves = FakeHost.ExternalMoves.Where(m => m.Id == Lost).ToList();
        Check.Eq(lostMoves.Count, 6, "three tries into the main pack, three into the side pack");
        var gaps = sends.Zip(sends.Skip(1), (a, b) => b.At - a.At).ToList();
        Check.True(gaps.Count > 0 && gaps.All(g => g >= RynthAiPlugin.PickupAnswerWaitMinMs - TickMs),
            $"each retry only after the answer wait ({string.Join(", ", gaps)} ms)");
        Check.True(Logged("trying another pack for this item only"), "the main pack is left out for this item only");
        Check.False(Logged("treating it as full"), "no pack is judged full on unanswered tries");
        Check.True(Logged("pickup failed for 0x80263121"), "the item is given up after the second round");
        Check.False(bot.Refusals.IsRefused(unchecked((int)Player), _now), "the main pack's mark ended with the item");
        Check.False(bot.Refusals.IsRefused(unchecked((int)SidePack), _now), "so did the side pack's");
        Check.True(FakeHost.ExternalMoves.Where(m => m.Id == Next).All(m => m.Target == Player), "the next item goes into the main pack again");
        Check.True(sim.Landed.Any(l => l.Id == Next && l.Pack == Player), "and lands there");
        Check.False(Logged("pickup failed for 0x80263122"), "the next item is not given up on");
        Check.False(Logged("no pack has a free slot"), "looting does not stop for want of a pack");
    });

    // ── 4. A late landing ─────────────────────────────────────────────────────

    private static void LateLandingUnmarks() => Clocked(() =>
    {
        const uint Slow = 0x80263131, Next = 0x80263132;
        World(sidePack: true, Slow, Next);
        var sim = new SlowAce();
        sim.Lag[Slow] = 12_500;   // a lag spike: the server takes up the first move 12.5 s late
        var bot = Open(sim, sidePack: true, Slow, Next);

        Run(bot, () => bot.Completed, 90_000);

        Check.True(sim.Landed.Any(l => l.Id == Slow && l.Pack == Player), "the first move lands in the main pack, late");
        Check.True(Logged("trying another pack for this item only"), "after three unanswered tries the main pack was left out for the item");
        Check.True(Logged("0x80263131 landed in the main pack; it has room after all and is no longer treated as full"), "the late landing un-marks the main pack");
        Check.False(bot.Refusals.IsRefused(unchecked((int)Player), _now), "the main pack is not marked");
        Check.True(sim.Landed.Any(l => l.Id == Next), "the next item lands too");
        Check.False(Logged("pickup failed for 0x80263132"), "the next item is not given up on");
        Check.False(Logged("no pack has a free slot"), "looting does not stop");
        Check.Eq((long)bot.Call("PickupAnswerWaitMs")!, RynthAiPlugin.PickupAnswerWaitMinMs,
            "the lag spike's late answer (to an earlier, timed-out send) does not stretch the answer wait");
    });

    // ── 5. Busy or room ───────────────────────────────────────────────────────

    private static void RefusalTiming() => Clocked(() =>
    {
        const uint G1 = 0x80263141;
        World(sidePack: false, G1);
        var bot = Open(new SlowAce(), sidePack: false, G1);
        bot.Sim = null;   // the answers are given by hand here
        bot.TickUntil(() => bot.MovesOf(G1) > 0, 2000);
        Check.Eq(bot.MovesOf(G1), 1, "the move is sent");

        // Refused on arrival (ACE: YoureTooBusy, then InventoryServerSaveFailed with no code; the
        // engine keeps the last one, error 0x0): a free resend once still.
        bot.Tick();
        FakeHost.WeenieError(0x1D);
        FakeHost.WeenieError(0, 0x00A0, G1);
        bot.TickUntil(() => bot.MovesOf(G1) > 1, 1000);
        Check.Eq(bot.MovesOf(G1), 2, "resent at once");
        Check.True(Logged("the server refused 0x80263141 as too busy"), "logged as too busy");
        Check.Eq(bot.Get<int>("_currentLootItemMoveAttempts"), 1, "not one of the 3 tries");

        // Refused after its pickup ran (no room; the same error 0x0): one of the tries.
        FakeHost.CastBusy = 1;
        bot.TickUntil(() => false, 1800);
        FakeHost.WeenieError(0, 0x00A0, G1);
        bot.Tick();
        FakeHost.CastBusy = 0;
        bot.TickUntil(() => bot.MovesOf(G1) > 2, 3000);
        Check.Eq(bot.MovesOf(G1), 3, "sent again after the retry window");
        Check.Eq(bot.Get<int>("_currentLootItemMoveAttempts"), 2, "counted as a try");
        Check.True(Logged("retrying use for 0x80263141 attempt 2 (last: refused"), "logged as refused after its pickup ran");
        Check.Eq(bot.Get<int>("_currentLootItemDestRefusals"), 1, "and it counts against the main pack (the busy one does not)");
    });

    // ── 6. The wait itself ────────────────────────────────────────────────────

    private static void AnswerWaitAdapts() => Clocked(() =>
    {
        World(sidePack: false);
        var bot = new Bot(FakeHost.Create(Player));
        long Wait() => (long)bot.Call("PickupAnswerWaitMs")!;
        Check.Eq(Wait(), RynthAiPlugin.PickupAnswerWaitMinMs, "4 s with nothing measured");
        foreach (long ms in new long[] { 1500, 1657, 2735 }) bot.Call("NotePickupAnswerLatency", ms);
        Check.Eq(Wait(), 2735 + RynthAiPlugin.PickupAnswerWaitMarginMs, "the slowest answer (2.7 s, Conquest's queued pickups) plus 1.5 s");
        bot.Call("NotePickupAnswerLatency", 20_000L);
        Check.Eq(Wait(), RynthAiPlugin.PickupAnswerWaitMaxMs, "at most 8 s");
        for (int i = 0; i < 8; i++) bot.Call("NotePickupAnswerLatency", 800L);
        Check.Eq(Wait(), RynthAiPlugin.PickupAnswerWaitMinMs, "the last 8 answers only, never under 4 s");
    });

    private static void PackRefusalMarks()
    {
        var r = new RynthCore.Plugin.RynthAi.Loot.PackRefusals();
        r.Refuse(1, 0, confirmed: true);
        r.Refuse(2, 0, confirmed: false);
        Check.True(r.IsRefused(1, 10) && r.IsConfirmed(1, 10), "a confirmed mark");
        Check.True(r.IsRefused(2, 10) && !r.IsConfirmed(2, 10), "an unconfirmed mark is left out too");
        r.Refuse(1, 5, confirmed: false);
        Check.True(r.IsConfirmed(1, 10), "a confirmed mark is not weakened by an unconfirmed one");
        r.ClearUnconfirmed();
        Check.True(r.IsRefused(1, 10), "the confirmed mark stays");
        Check.False(r.IsRefused(2, 10), "the unconfirmed one goes with its item");
        r.Accepted(1);
        Check.False(r.IsRefused(1, 10), "an item landing there ends a confirmed mark");
    }
}
