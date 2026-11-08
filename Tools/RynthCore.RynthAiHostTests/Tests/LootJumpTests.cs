using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Jump-loot: the jump is the keyboard one (TapJump, 0xF61B Jump, which ACE handles), never
// JumpNonAutonomous (0xF7C9, no handler on ACE). It goes BEFORE the move, on every pickup (a
// corpse's only match included), only with the setting on and stamina above 5.
// The loot clock (RynthAiPlugin.CorpseClock) is moved by hand here.
internal static class LootJumpTests
{
    private const uint Player = 0x50000F31;
    private const uint CorpseA = 0x80263100;
    private const uint GemA = 0x80263101;
    private const uint GemA2 = 0x80263102;
    private const uint GemA3 = 0x80263103;
    private const uint PackStack = 0x80263120;
    private const uint KeyMaxStack = 11, KeyStack = 12;
    private const uint CorpseB = 0x80263110;
    private const uint GemB = 0x80263111;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint LongDescKey = 16;
    private const string Me = "Test Looter";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("jump loot: two-item corpse - the keyboard jump (0xF61B) goes out before the move; never the unhandled 0xF7C9", TwoItemJumpsBeforeMove);
        r.Add("jump loot: a corpse's only match jumps too (one tap jump)", SingleItemJumps);
        r.Add("jump loot: the setting off sends no jump", SettingOffNoJump);
        r.Add("jump loot: stamina of 5 or less sends no jump", LowStaminaNoJump);
        r.Add("jump loot: health under Heal At sends no jump (the flight would hold the heal)", LowHealthNoJump);
        r.Add("jump loot: a pickup answered with no bend-down releases the corpse at once; the next corpse is opened while still in the air", NoReturnNextCorpseInAir);
        r.Add("jump loot: after a pickup answered with no bend-down, the next item goes out at once in the air with no second jump (not queued); after the landing the next one jumps again", NoReturnNextItemJumps);
        r.Add("jump loot: a jump pickup answered only after a bend-down's time still waits for its Return before the corpse is released", SlowAnswerKeepsReturnWait);
        r.Add("jump loot: a bend-down answer inside an earlier jump's flight ends it: the next item is queued behind that Return, with no jump", SlowAnswerEndsAirWindow);
        r.Add("jump loot: a jump send's answer at 150 ms is confirmed then, and the next corpse opened then, not at 500 ms", JumpAnswerConfirmedOnArrival);
        r.Add("jump loot: a jump send merged into a pack stack keeps the floor (the client's request slot stays held for a merged-away object)", JumpMergeKeepsAnswerFloor);
        r.Add("jump loot: the no-bend-down window after an in-air send that rode an earlier jump is anchored to that jump", AirWindowAnchoredToTheInAirJump);
        r.Add("jump loot: a fast refusal that is not 'too busy' (a code-0 save-failed) does not turn the no-bend-down call off", NonBusyRefusalKeepsNoMotion);
        r.Add("jump loot: a send with no jump still waits PickupAnswerMinMs before its answer is believed", NoJumpKeepsAnswerFloor);
        r.Add("jump loot: a jump send's weak sign (an unowned stack, no pack stack grown) still waits PickupAnswerMinMs", JumpWeakSignKeepsAnswerFloor);
        r.Add("jump loot: a send with no jump answered at 100 ms still waits its Return before the next corpse", NoJumpFastAnswerWaitsReturn);
        r.Add("jump loot: a jump send refused, then resent (no jump), answered at 100 ms still waits its Return", ResentFastAnswerWaitsReturn);
        r.Add("jump loot: a move sent on a no-bend-down call and refused 'too busy' turns the call off: the next fast jump answer waits its Return", BusyRefusalTurnsNoMotionOff);
        r.Add("jump loot: an open sent on a no-bend-down call and refused 'too busy' turns the call off too", BusyOpenTurnsNoMotionOff);
        r.Add("jump loot: dropping the corpse claim (macro off) ends the no-bend-down window", ResetEndsAirWindow);
        r.Add("jump loot: a pickup sent while still in the air from the last loot jump takes no second jump; after the landing the next one jumps", NoSecondJumpInTheAir);
        r.Add("jump loot: the pickup's jump puts combat and buffs on hold until the landing (or the cap); loot never", JumpHoldsCombatNotLoot);
        r.Add("jump loot: portal space and a death drop the combat hold (no landing is coming)", PortalAndDeathEndJumpHold);
        r.Add("jump loot cadence: one move in flight at a time (the client's one inventory request), the next sent on the tick the answer shows", OneInFlightNextOnAnswerTick);
        r.Add("jump loot: the flight budget lets an early in-air send through with no new jump", FlightBudgetLetsEarlySendThrough);
        r.Add("jump loot: the flight budget holds a late in-air send until the landing, then jumps again", FlightBudgetHoldsLateSend);
        r.Add("jump loot: ResetCurrentLootItem clears the flight hold's logged flag", ResetClearsFlightHoldLogged);
    }

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        var oldPace = ActionPacer.NowMs;
        _now = 50_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        ActionPacer.NowMs = () => _now;   // the loot jump's air hold (ActionPacer.LootJumpAirborne) on the same clock
        ActionPacer.ForgetLootJump();
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; ActionPacer.NowMs = oldPace; ActionPacer.ForgetLootJump(); }
    }

    private static RynthAiPlugin _bot = null!;   // the bot LootUntilFirstMove made
    private static RynthCoreHost _host;          // and its host
    private static T Field<T>(string name) => (T)typeof(RynthAiPlugin).GetField(name, Any)!.GetValue(_bot)!;
    private static void SetField(string name, object v) => typeof(RynthAiPlugin).GetField(name, Any)!.SetValue(_bot, v);

    /// <summary>The character's height: up in the air, or back on the ground (ActionPacer's landing check reads it).</summary>
    private static void Air(bool up) => FakeHost.PlayerPose = (Me0.Cell, Me0.X, Me0.Y, up ? Me0.Z + 1.0f : Me0.Z);

    private static void Gem(uint id, uint container = CorpseA)
    {
        FakeHost.Names[id] = "Black Opal";
        FakeHost.ItemTypes[id] = ItemTypeGem;
        FakeHost.Containers[id] = container;
        FakeHost.Appraised.Add(id);
    }

    private static void Corpse(uint id, float dx)
    {
        FakeHost.Names[id] = "Corpse of Olthoi Swarm Drone";
        FakeHost.ItemTypes[id] = ItemTypeContainer;
        FakeHost.Strings[(id, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(id);
        FakeHost.Positions[id] = (Me0.Cell, Me0.X + dx, Me0.Y, Me0.Z);
    }

    private static void World(bool secondGem, bool secondCorpse, bool serverGates, bool thirdGem = false, bool packStack = false)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;          // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;           // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = true;   // CloseContainer + GetGroundContainerId
        FakeHost.TapJumpCalls = true;
        FakeHost.ServerGates = serverGates;    // GetCastBusyState: the client's motion queue
        FakeHost.LastUseDoneCalls = serverGates; // GetLastUseDone: the open's answer
        FakeHost.WeenieErrorCalls = true;      // GetLastWeenieError: "You're too busy"
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        Corpse(CorpseA, 1.0f);
        Gem(GemA);
        if (secondGem) Gem(GemA2);
        if (thirdGem) Gem(GemA3);
        if (secondCorpse)
        {
            Corpse(CorpseB, 1.5f);
            Gem(GemB, CorpseB);
        }
        if (packStack)
        {
            // GemA is a stack of 1, and the pack holds a same-name stack of 3 it can merge into.
            FakeHost.Ints[(GemA, KeyMaxStack)] = 100;
            FakeHost.Ints[(GemA, KeyStack)] = 1;
            Gem(PackStack, Player);
            FakeHost.Ints[(PackStack, KeyMaxStack)] = 100;
            FakeHost.Ints[(PackStack, KeyStack)] = 3;
        }
    }

    /// <summary>Opens the corpse and ticks until the first pickup's move has gone out. Returns the tick.</summary>
    private static Action LootUntilFirstMove(bool jumpEnabled, bool secondGem, uint stamina, bool secondCorpse = false, bool serverGates = false, bool thirdGem = false, bool packStack = false, uint health = 100)
    {
        World(secondGem, secondCorpse, serverGates, thirdGem, packStack);
        var host = FakeHost.Create(Player);
        _host = host;
        var bot = new RynthAiPlugin();
        _bot = bot;
        var s = new LegacyUiSettings
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
            LootJumpEnabled = jumpEnabled,
        };
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(bot, new object[] { FakeHost.LastApi });
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(bot, v);
        Set("_dashboard", dash);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        Set("_objectCache", cache);
        Set("_playerId", Player);
        Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = health, MaxStamina = 100, CurrentStamina = stamina, MaxMana = 100, CurrentMana = 100 });

        string profile = Path.Combine(Program.TempRoot, "loot-jump-test.json");
        var p = new LootProfile { Name = "gems" };
        var rule = new LootRule { Name = "Gems", Action = LootAction.Keep };
        rule.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        p.Rules.Add(rule);
        p.Save(profile);
        s.CurrentLootPath = profile;

        void Tick()
        {
            Set("_activity", BotActivity.Looting);
            typeof(RynthAiPlugin).GetMethod("TickCorpseOpening", Any)!.Invoke(bot, null);
        }
        var ids = new List<uint> { CorpseA, GemA };
        if (secondGem) ids.Add(GemA2);
        if (thirdGem) ids.Add(GemA3);
        if (packStack) ids.Add(PackStack);
        if (secondCorpse) { ids.Add(CorpseB); ids.Add(GemB); }
        foreach (uint id in ids) cache.OnCreateObject(id);
        for (int i = 0; i < 50; i++) cache.Tick();
        Tick();
        Check.True(FakeHost.Uses.Contains(CorpseA), "the corpse is opened");
        FakeHost.UseDone(0);
        FakeHost.GroundContainer = CorpseA;
        bot.OnViewObjectContents(CorpseA);
        _openedAt = _now;
        for (int i = 0; i < 200 && FakeHost.ExternalMoves.Count == 0; i++) { _now += 20; Tick(); }
        Check.True(FakeHost.ExternalMoves.Count > 0, "a gem's move is sent");
        return Tick;
    }

    private const long TickMs = 20;

    private static long _openedAt;   // CorpseA's contents arrived (LootUntilFirstMove)

    /// <summary>
    /// The latest tick the next corpse's open may go out after the last item's confirm at
    /// <paramref name="confirmed"/>: at once, except that a corpse emptied sooner than the
    /// Loot Empty Corpse floor (max(200 ms, LootEmptyCorpseMs) from its open, a user setting, not
    /// a landing wait) is completed only then; a tick or two of slack.
    /// </summary>
    private static long NextOpenDue(long confirmed) => Math.Max(confirmed, _openedAt + 200) + 3 * TickMs;

    private static bool Confirmed(uint id) => FakeHost.Logs.Any(l => l.Contains($"pickup confirmed for 0x{id:X8}"));

    /// <summary>Ticks (TickMs each) until <paramref name="done"/> or <paramref name="maxMs"/> has passed.</summary>
    private static void TickUntil(Action tick, Func<bool> done, long maxMs)
    {
        long end = _now + maxMs;
        while (_now < end && !done()) { _now += TickMs; tick(); }
    }

    private static void TickFor(Action tick, long ms)
    {
        long end = _now + ms;
        while (_now < end) { _now += TickMs; tick(); }
    }

    // ACE with allow_jump_loot (Player_Inventory.GetPickupMotion): the move reaches the server while
    // the character is in the air, the pickup motion is Invalid, so the item moves at once (about the
    // round trip) and EnqueuePickupDone has no Return to play (IsBusy clears at once). What the
    // client's motion queue (GetCastBusyState) reads in the air is not known, so both readings are
    // run: busy (the jump's own motion for the whole flight) and idle.
    private static void NoReturnNextCorpseInAir() => Clocked(() =>
    {
        foreach (int airQueue in new[] { 1, 0 })
        {
            var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, secondCorpse: true, serverGates: true);
            Check.Eq(FakeHost.ExternalMoves[0].Id, GemA, $"[air queue {airQueue}] corpse A's gem goes first");
            long sent = _now;
            FakeHost.CastBusy = airQueue;
            TickFor(tick, 100);
            FakeHost.Containers[GemA] = Player;   // the answer, no bend-down
            TickUntil(tick, () => Confirmed(GemA), 1000);
            Check.True(Confirmed(GemA), $"[air queue {airQueue}] the pickup is confirmed");
            long confirmed = _now;
            TickUntil(tick, () => FakeHost.Uses.Contains(CorpseB), 1500);
            Check.True(FakeHost.Uses.Contains(CorpseB), $"[air queue {airQueue}] the next corpse is opened");
            Check.True(_now <= NextOpenDue(confirmed),
                $"[air queue {airQueue}] the next corpse is opened at the confirm, not after a landing / Return wait: {_now - confirmed} ms after the confirm, {_now - sent} ms after the send");
        }
    });

    // The second item goes out at once, still in the air, with no second jump (an air jump costs
    // stamina on ACE and does nothing for the pickup); once the character has landed, the next one
    // jumps again.
    private static void NoReturnNextItemJumps() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true, thirdGem: true);
        uint first = FakeHost.ExternalMoves[0].Id;
        Check.Eq(FakeHost.TapJumps, 1, "the first pickup jumps");
        Air(up: true);
        FakeHost.CastBusy = 1;                   // the jump's motion plays
        TickFor(tick, 100);
        FakeHost.Containers[first] = Player;     // the answer, no bend-down
        TickUntil(tick, () => Confirmed(first), 1000);
        Check.True(Confirmed(first), "the first pickup is confirmed");
        long confirmed = _now;
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 1500);
        Check.Eq(FakeHost.ExternalMoves.Count, 2, "the second move is sent");
        uint second = FakeHost.ExternalMoves[1].Id;
        Check.True(_now - confirmed <= 3 * TickMs, $"at the confirm, not after the landing: {_now - confirmed} ms");
        Check.Eq(FakeHost.TapJumps, 1, "no jump in the air: " + Order());
        Check.False(FakeHost.Logs.Any(l => l.Contains("queuing 0x")), "not queued behind a Return that ACE never plays for it");

        // The second, sent in the air, answered with no bend-down; the character has landed by now.
        Air(up: false);
        TickFor(tick, ActionPacer.JumpLandMinMs);
        FakeHost.Containers[second] = Player;
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 3, 1500);
        Check.Eq(FakeHost.ExternalMoves.Count, 3, "the third move is sent");
        int at = FakeHost.Order.IndexOf($"move:{FakeHost.ExternalMoves[2].Id:X8}");
        Check.True(at > 0 && FakeHost.Order[at - 1] == "tapjump", "landed: the third jumps again: " + Order());
        Check.Eq(FakeHost.TapJumps, 2, "two jumps in all");
    });

    // The jump went out, but the answer took a bend-down's time (the server turned the character
    // first and it landed, so ACE played the pickup motion and its Return): that Return is waited out.
    private static void SlowAnswerKeepsReturnWait() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, secondCorpse: true, serverGates: true);
        FakeHost.CastBusy = 1;
        TickFor(tick, 800);
        FakeHost.Containers[GemA] = Player;     // answered after the bend-down; the Return plays
        TickUntil(tick, () => Confirmed(GemA), 1000);
        Check.True(Confirmed(GemA), "the pickup is confirmed");
        TickFor(tick, 600);
        Check.False(FakeHost.Uses.Contains(CorpseB), "no next open while the Return plays");
        FakeHost.CastBusy = 0;
        TickUntil(tick, () => FakeHost.Uses.Contains(CorpseB), 1000);
        Check.True(FakeHost.Uses.Contains(CorpseB), "the next corpse is opened once the character stands still");
    });

    // First pickup in the air with no bend-down; the second, sent at once, is answered only after a
    // bend-down (the character had landed), so its Return plays: the third is queued behind that
    // Return (ACE NextPickup) and gets no jump, which would disturb the queue - the first jump's
    // flight window no longer counts.
    private static void SlowAnswerEndsAirWindow() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true, thirdGem: true);
        uint first = FakeHost.ExternalMoves[0].Id;
        FakeHost.CastBusy = 1;
        TickFor(tick, 100);
        FakeHost.Containers[first] = Player;          // no bend-down
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 1000);
        Check.Eq(FakeHost.ExternalMoves.Count, 2, "the second move goes out at the first's confirm");
        uint second = FakeHost.ExternalMoves[1].Id;
        long secondSent = _now;
        TickFor(tick, 700);
        FakeHost.Containers[second] = Player;         // answered after a bend-down: its Return plays
        TickUntil(tick, () => Confirmed(second), 1000);
        Check.True(Confirmed(second), "the second pickup is confirmed");
        Check.True(_now - secondSent < 1500 - 100, $"(still inside the first jump's flight window: {_now - secondSent} ms after the second send)");
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 3, 300);
        Check.Eq(FakeHost.ExternalMoves.Count, 3, "the third move goes out during the Return");
        uint third = FakeHost.ExternalMoves[2].Id;
        int at = FakeHost.Order.IndexOf($"move:{third:X8}");
        Check.True(at > 0 && FakeHost.Order[at - 1] != "tapjump", "with no jump into the Return: " + Order());
        Check.True(FakeHost.Logs.Any(l => l.Contains($"queuing 0x{third:X8}")), "queued behind the second's Return");
    });

    // -- The PickupAnswerMinMs floor --
    // The floor is ACE's pickup motion time. A jump send has no motion: its server-only answer
    // (the item in a pack, a pack stack grown, the object gone from the client) is believed on
    // arrival. Every other send, and a jump send's weak sign (an unowned stack the client still has),
    // keep the floor.

    private const long AnswerAtMs = 150;

    /// <summary>The ms from <paramref name="sent"/> to the tick the pickup of <paramref name="id"/> is confirmed (-1: never, in 1.5 s).</summary>
    private static long ConfirmAfter(Action tick, uint id, long sent)
    {
        TickUntil(tick, () => Confirmed(id), 1500);
        return Confirmed(id) ? _now - sent : -1;
    }

    private static void JumpAnswerConfirmedOnArrival() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, secondCorpse: true, serverGates: true);
        long sent = _now;
        TickFor(tick, AnswerAtMs - TickMs);
        FakeHost.Containers[GemA] = Player;     // ACE's answer, no bend-down
        long confirmedAfter = ConfirmAfter(tick, GemA, sent);
        Check.True(confirmedAfter >= 0 && confirmedAfter <= AnswerAtMs + TickMs,
            $"confirmed on the tick the answer shows ({AnswerAtMs} ms), not at PickupAnswerMinMs: {confirmedAfter} ms after the send");
        long confirmed = _now;
        TickUntil(tick, () => FakeHost.Uses.Contains(CorpseB), 1000);
        Check.True(FakeHost.Uses.Contains(CorpseB) && _now <= NextOpenDue(confirmed),
            $"the next corpse is opened then too (or at the empty-corpse floor, 200 ms from the open): {_now - sent} ms after the send, {_now - _openedAt} ms after the open");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no bend-down or Return to wait for")), "classed as a pickup with no bend-down");
    });

    private static void JumpMergeKeepsAnswerFloor() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, serverGates: true, packStack: true);
        Check.Eq(FakeHost.ExternalMoves[0].Id, GemA, "the corpse gem goes");
        long sent = _now;
        TickFor(tick, AnswerAtMs - TickMs);
        // ACE StackableMerge: the corpse item is removed (gone from the client) and the pack stack grows.
        FakeHost.Containers.Remove(GemA);
        FakeHost.Names.Remove(GemA);
        FakeHost.Positions.Remove(GemA);
        FakeHost.Ints[(PackStack, KeyStack)] = 4;
        // The client's one-request slot is emptied only by an answer about that same object, and a
        // merged-away object is gone (RynthCore ClientActionGates): the slot can stay held, and a move
        // sent now would be dropped by the client. The floor is kept.
        long confirmedAfter = ConfirmAfter(tick, GemA, sent);
        Check.True(confirmedAfter >= RynthAiPlugin.PickupAnswerMinMs, $"a merge keeps the floor: confirmed {confirmedAfter} ms after the send");
        // (By the floor the gem is gone from the client, which names no stack: either merge wording.)
        Check.True(FakeHost.Logs.Any(l => l.Contains("pickup confirmed") && l.Contains("merged into")), "as a merge into the pack stack: "
            + (FakeHost.Logs.LastOrDefault(l => l.Contains("pickup confirmed")) ?? "(none)"));
    });

    // The no-bend-down window after a pickup is anchored to the JUMP (it ends JumpLandMaxMs after
    // it), not to the send that confirmed: a second send that rode the first jump, in the air, must
    // not stretch it.
    private static void AirWindowAnchoredToTheInAirJump() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true);
        long jumpAt = _now;
        uint first = FakeHost.ExternalMoves[0].Id;
        Air(up: true);
        TickFor(tick, 100);
        FakeHost.Containers[first] = Player;      // no bend-down
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 500);
        Check.Eq(FakeHost.ExternalMoves.Count, 2, "the second goes out in the air");
        Check.Eq(FakeHost.TapJumps, 1, "riding the first jump");
        uint second = FakeHost.ExternalMoves[1].Id;
        long secondAt = _now;
        TickFor(tick, 100);
        FakeHost.Containers[second] = Player;     // no bend-down
        TickUntil(tick, () => Confirmed(second), 500);
        Check.True(Confirmed(second), "the second is confirmed");
        Check.True(FakeHost.Logs.Count(l => l.Contains("no bend-down or Return to wait for")) == 2, "both as no-bend-down pickups");
        long until = Field<long>("_jumpPickupAirUntil");
        Check.True(Math.Abs(until - (jumpAt + ActionPacer.JumpLandMaxMs)) <= TickMs,
            $"the window ends JumpLandMaxMs after the jump: {until - jumpAt} ms from it (the second send was {secondAt - jumpAt} ms in)");
    });

    // Only "You're too busy" (0x1D) proves the no-bend-down call wrong. A save-failed with
    // no code (a full pack, or anything else) that comes fast is not that evidence.
    private static void NonBusyRefusalKeepsNoMotion() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true);
        uint first = FakeHost.ExternalMoves[0].Id;
        TickFor(tick, 100 - TickMs);
        FakeHost.Containers[first] = Player;      // no bend-down: the window opens
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 1000);
        Check.True(Field<long>("_jumpPickupAirUntil") > _now, "the window is open");
        uint second = FakeHost.ExternalMoves[1].Id;
        Check.True(Field<bool>("_currentLootItemSentInAirWindow"), "the second went out in it");
        TickFor(tick, 100);
        FakeHost.WeenieError(0, 0x00A0, second);  // InventoryServerSaveFailed, no code, 100 ms after the send
        TickFor(tick, 3 * TickMs);
        Check.False(FakeHost.Logs.Any(l => l.Contains("the server played a bend-down after all")), "no distrust line");
        Check.Eq(Field<long>("_jumpNoMotionOffUntil"), 0L, "the call stays on");
    });

    private static void NoJumpKeepsAnswerFloor() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: false, secondGem: false, stamina: 100u, serverGates: true);
        Check.Eq(FakeHost.TapJumps, 0, "no jump went out");
        long sent = _now;
        TickFor(tick, AnswerAtMs - TickMs);
        FakeHost.Containers[GemA] = Player;
        long confirmedAfter = ConfirmAfter(tick, GemA, sent);
        Check.True(confirmedAfter >= RynthAiPlugin.PickupAnswerMinMs,
            $"not believed before PickupAnswerMinMs ({RynthAiPlugin.PickupAnswerMinMs} ms): confirmed {confirmedAfter} ms after the send");
    });

    // -- What JumpPickupHadNoMotion must not take for a no-bend-down pickup --

    /// <summary>
    /// After the last item's confirm: the next corpse is not opened while the Return plays
    /// (CastBusy 1 for <paramref name="returnMs"/>), and is opened once it ends.
    /// </summary>
    private static void NextOpenWaitsReturn(Action tick, uint item, long returnMs, string what)
    {
        TickUntil(tick, () => Confirmed(item), 1500);
        Check.True(Confirmed(item), what + ": the pickup is confirmed");
        TickFor(tick, returnMs);
        Check.False(FakeHost.Uses.Contains(CorpseB), what + ": no next open while the Return plays");
        FakeHost.CastBusy = 0;
        TickUntil(tick, () => FakeHost.Uses.Contains(CorpseB), 1500);
        Check.True(FakeHost.Uses.Contains(CorpseB), what + ": the next corpse is opened once the character stands still");
    }

    private static void NoJumpFastAnswerWaitsReturn() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: false, secondGem: false, stamina: 100u, secondCorpse: true, serverGates: true);
        TickFor(tick, 100 - TickMs);
        FakeHost.Containers[GemA] = Player;
        FakeHost.CastBusy = 1;                    // its Return
        NextOpenWaitsReturn(tick, GemA, 300, "no jump");
    });

    private static void ResentFastAnswerWaitsReturn() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, secondCorpse: true, serverGates: true);
        Check.Eq(FakeHost.TapJumps, 1, "the first send jumps");
        TickFor(tick, 40);
        FakeHost.WeenieError(0x1D);               // YoureTooBusy
        FakeHost.WeenieError(0, 0x00A0, GemA);    // InventoryServerSaveFailed(item)
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 1000);
        Check.Eq(FakeHost.ExternalMoves.Count, 2, "resent");
        Check.Eq(FakeHost.TapJumps, 1, "the resend goes without a jump");
        TickFor(tick, 100 - TickMs);
        FakeHost.Containers[GemA] = Player;
        FakeHost.CastBusy = 1;
        NextOpenWaitsReturn(tick, GemA, 300, "resent");
    });

    // On a fast server a real bend-down can be 0.25-0.4 s, inside PickupAnswerMinMs.
    // A move sent on the no-bend-down call and refused 'too busy' proves the call wrong: it is off.
    private static void BusyRefusalTurnsNoMotionOff() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, secondCorpse: true, serverGates: true, thirdGem: true);
        uint first = FakeHost.ExternalMoves[0].Id;
        FakeHost.CastBusy = 1;                    // a fast real bend-down, then its Return
        TickFor(tick, 300 - TickMs);
        FakeHost.Containers[first] = Player;      // answered at 300 ms: taken for no bend-down
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 1000);
        Check.True(FakeHost.Logs.Any(l => l.Contains("no bend-down or Return to wait for")), "the first is (wrongly) called no bend-down");
        uint second = FakeHost.ExternalMoves[1].Id;
        TickFor(tick, 40);
        FakeHost.WeenieError(0x1D);               // the Return is playing: too busy
        FakeHost.WeenieError(0, 0x00A0, second);
        TickFor(tick, 2 * TickMs);
        Check.True(FakeHost.Logs.Any(l => l.Contains("the server played a bend-down after all")), "the call is turned off");
        Check.Eq(Field<long>("_jumpPickupAirUntil"), 0L, "and the window ended");

        TickFor(tick, 400);
        FakeHost.CastBusy = 0;                    // the Return ends: the refused move goes again
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 3, 1000);
        Check.Eq(FakeHost.ExternalMoves[2].Id, second, "the refused move is resent");
        TickFor(tick, 150);
        FakeHost.Containers[second] = Player;
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 4, 1500);
        Check.Eq(FakeHost.ExternalMoves.Count, 4, "the third goes");
        uint third = FakeHost.ExternalMoves[3].Id;
        int at = FakeHost.Order.IndexOf($"move:{third:X8}");
        Check.True(at > 0 && FakeHost.Order[at - 1] == "tapjump", "with a jump (landed by now): " + Order());

        // Answered at 300 ms again: with the call off it waits its Return.
        FakeHost.CastBusy = 1;
        TickFor(tick, 300 - TickMs);
        FakeHost.Containers[third] = Player;
        NextOpenWaitsReturn(tick, third, 300, "after the call was turned off");
    });

    private static void BusyOpenTurnsNoMotionOff() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, secondCorpse: true, serverGates: true);
        TickFor(tick, 300 - TickMs);
        FakeHost.Containers[GemA] = Player;       // taken for no bend-down
        TickUntil(tick, () => FakeHost.Uses.Contains(CorpseB), 1000);
        Check.True(FakeHost.Uses.Contains(CorpseB), "the next corpse is opened on the call");
        Check.True(Field<int>("_openInAirWindowSeq") >= 0, "its answer is watched");
        FakeHost.UseDone(0x1D);                   // too busy: the Return was playing
        TickFor(tick, 2 * TickMs);
        Check.True(FakeHost.Logs.Any(l => l.Contains("the next corpse's open, sent on it, was refused as too busy")), "the call is turned off");
        Check.True(Field<long>("_jumpNoMotionOffUntil") > _now, "for a while");
    });

    // The fastest cadence there is. The acclient holds ONE pending inventory request and refuses
    // every other move until the server's answer about that object ("You can only move or use one
    // item at a time", RynthCore ClientActionGates), and on a FastTick character ACE cancels a pickup
    // whose turn is still pending when the next one arrives (Player_Move2.CreateMoveToChain2 ->
    // CheckMoveToParams -> ActionCancelled). So never two at once: the next goes on the very tick
    // the last one's answer shows, with no tick lost in between.
    private static void OneInFlightNextOnAnswerTick() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true, thirdGem: true);
        Air(up: true);
        var answered = new HashSet<uint>();
        int maxInFlight = 1;
        for (int k = 0; k < 3; k++)
        {
            Check.Eq(FakeHost.ExternalMoves.Count, k + 1, $"move {k + 1} is out");
            uint id = FakeHost.ExternalMoves[k].Id;
            for (int i = 0; i < 4; i++)
            {
                _now += TickMs; tick();
                maxInFlight = Math.Max(maxInFlight, FakeHost.ExternalMoves.Count - answered.Count);
            }
            Check.Eq(FakeHost.ExternalMoves.Count, k + 1, $"nothing more while move {k + 1} is unanswered");
            FakeHost.Containers[id] = Player;     // the answer arrives
            answered.Add(id);
            _now += TickMs; tick();               // the one tick that sees it
            Check.True(Confirmed(id), $"move {k + 1} confirmed on the tick its answer shows");
            if (k < 2)
                Check.Eq(FakeHost.ExternalMoves.Count, k + 2, $"and move {k + 2} goes on that same tick");
        }
        Check.Eq(maxInFlight, 1, "never two moves in flight");
    });

    // Dropping the corpse claim ends the no-bend-down window and the open watch.
    private static void ResetEndsAirWindow() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, serverGates: true);
        SetField("_jumpPickupAirUntil", _now + 1000);
        SetField("_openInAirWindowSeq", 3);
        typeof(RynthAiPlugin).GetMethod("ResetCorpseTarget", Any)!.Invoke(_bot, null);
        Check.Eq(Field<long>("_jumpPickupAirUntil"), 0L, "the window is gone");
        Check.Eq(Field<int>("_openInAirWindowSeq"), -1, "and the open watch");
    });

    // A pickup sent while the character is still in the air from the last loot jump takes no second
    // jump: the move arrives in the air anyway, and ACE HandleActionJump has no ground check, so an
    // air jump only costs stamina. Once the character has landed, the next pickup jumps again.
    private static void NoSecondJumpInTheAir() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true, thirdGem: true);
        long jumpAt = _now;
        uint first = FakeHost.ExternalMoves[0].Id;
        Check.Eq(FakeHost.TapJumps, 1, "the first pickup jumps");
        Air(up: true);                           // and stays up: no landing is seen
        TickFor(tick, 100);
        FakeHost.Containers[first] = Player;     // the answer
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, ActionPacer.JumpLandMaxMs);
        Check.Eq(FakeHost.ExternalMoves.Count, 2, "the second move is sent");
        Check.True(_now - jumpAt < ActionPacer.JumpLandMaxMs, $"(inside the first jump's flight: {_now - jumpAt} ms after it)");
        Check.True(ActionPacer.LootJumpAirborne(_host, out _), "(still in the air at the second send)");
        Check.Eq(FakeHost.TapJumps, 1, "no second jump in the air: " + Order());

        uint second = FakeHost.ExternalMoves[1].Id;
        Air(up: false);                          // landed
        TickFor(tick, ActionPacer.JumpLandMinMs);   // the landing check looks from JumpLandMinMs on
        FakeHost.Containers[second] = Player;
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 3, 3000);
        Check.Eq(FakeHost.ExternalMoves.Count, 3, "the third move is sent");
        int at = FakeHost.Order.IndexOf($"move:{FakeHost.ExternalMoves[2].Id:X8}");
        Check.True(at > 0 && FakeHost.Order[at - 1] == "tapjump", "landed: the third jumps again: " + Order());
        Check.Eq(FakeHost.TapJumps, 2, "two jumps in all");
    });

    // The loot half: the pickup's jump is what holds combat and buffs (ActionPacer); loot is not held.
    // CombatHookTests has the combat half (Think sends no attack while it holds).
    private static void JumpHoldsCombatNotLoot() => Clocked(() =>
    {
        bool pacer = ActionPacer.Enabled;
        try
        {
            LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, serverGates: true);
            Check.Eq(FakeHost.TapJumps, 1, "the pickup jumped");
            Check.True(ActionPacer.LootJumpAirborne(_host, out _), "and the loot code noted the jump for combat and buffs");
            // The pacer from here on, on this clock, with nothing of other tests' left in it
            // (CorpseCloseTests.PacerOn): only the jump can hold anything.
            ActionPacer.Reset(); ActionPacer.Enabled = true;
            MoveAudit.Reset(); MoveAudit.NowMs = () => _now;
            UseAudit.NowMs = () => _now;
            ActionPacer.NoteLootJump(_host);   // (Reset dropped the pickup's; the same jump, noted again)
            var host = _host;
            Air(up: true);
            Check.True(ActionPacer.LootJumpAirborne(host, out string why), "in the air after the pickup's jump: " + why);
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "a buff or heal waits");
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "an emergency heal too (ACE refuses it in the air)");
            Check.True(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "loot does not");
            _now += ActionPacer.JumpLandMinMs;
            Check.True(ActionPacer.LootJumpAirborne(host, out _), "still up at the height check");
            Air(up: false);
            Check.False(ActionPacer.LootJumpAirborne(host, out _), "landed: the hold ends");
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "the buff goes");

            // The cap: a landing never seen still ends the hold at JumpLandMaxMs.
            ActionPacer.NoteLootJump(host);
            Air(up: true);
            _now += ActionPacer.JumpLandMaxMs - 1;
            Check.True(ActionPacer.LootJumpAirborne(host, out _), "up, under the cap");
            _now += 1;
            Check.False(ActionPacer.LootJumpAirborne(host, out _), "the cap ends it");
        }
        finally
        {
            ActionPacer.Enabled = pacer; ActionPacer.Reset();
            MoveAudit.Reset(); MoveAudit.NowMs = () => Environment.TickCount64;
            UseAudit.NowMs = () => Environment.TickCount64;
        }
    });

    // The plugin's portal-space and death paths drop the hold: no landing is coming to end it.
    private static void PortalAndDeathEndJumpHold() => Clocked(() =>
    {
        var oldVitals = FakeHost.Vitals;
        try
        {
            LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, serverGates: true);
            var host = _host;
            const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

            Air(up: true);
            Check.True(ActionPacer.LootJumpAirborne(host, out _), "the pickup's jump holds combat");
            FakeHost.Portaling = true;
            typeof(RynthAiPlugin).GetMethod("NotePortalForPacer", Inst)!.Invoke(_bot, null);
            Check.False(ActionPacer.LootJumpAirborne(host, out _), "portal space ends the hold");
            FakeHost.Portaling = false;
            typeof(RynthAiPlugin).GetMethod("NotePortalForPacer", Inst)!.Invoke(_bot, null);

            ActionPacer.NoteLootJump(host);
            Check.True(ActionPacer.LootJumpAirborne(host, out _), "a second jump holds again");
            FakeHost.Vitals = (0, 100, 0, 100, 0, 100);
            typeof(RynthAiPlugin).GetMethod("HoldForDeath", Inst)!.Invoke(_bot, null);
            Check.False(ActionPacer.LootJumpAirborne(host, out _), "the death ends the hold");
        }
        finally { FakeHost.Vitals = oldVitals; FakeHost.Portaling = false; Air(up: false); }
    });

    private static void JumpWeakSignKeepsAnswerFloor() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, serverGates: true, packStack: true);
        Check.Eq(FakeHost.TapJumps, 1, "the jump went out");
        long sent = _now;
        TickFor(tick, AnswerAtMs - TickMs);
        FakeHost.Containers[GemA] = 0;          // an unowned stack; the pack stack has not grown
        long confirmedAfter = ConfirmAfter(tick, GemA, sent);
        Check.True(confirmedAfter >= RynthAiPlugin.PickupAnswerMinMs,
            $"a weak sign keeps the floor ({RynthAiPlugin.PickupAnswerMinMs} ms): confirmed {confirmedAfter} ms after the send");
    });

    private static string Order() => string.Join(",", FakeHost.Order);

    private static void TwoItemJumpsBeforeMove() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u);
        Check.Eq(FakeHost.Order.FirstOrDefault() ?? "", "tapjump", "the keyboard jump goes first: " + Order());
        Check.True(FakeHost.Order.Count >= 2 && FakeHost.Order[1].StartsWith("move:", StringComparison.Ordinal), "then the move: " + Order());
        Check.Eq(FakeHost.Jumps, 0, "never JumpNonAutonomous (0xF7C9, no handler on ACE)");
    });

    private static void SingleItemJumps() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u);
        Check.Eq(FakeHost.Order.FirstOrDefault() ?? "", "tapjump", "single item: the jump goes first: " + Order());
        Check.True(FakeHost.Order.Count >= 2 && FakeHost.Order[1].StartsWith("move:", StringComparison.Ordinal), "single item: then the move: " + Order());
        Check.Eq(FakeHost.TapJumps, 1, "single item: exactly one tap jump");
        Check.Eq(FakeHost.Jumps, 0, "single item: never JumpNonAutonomous");
    });

    private static void SettingOffNoJump() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: false, secondGem: true, stamina: 100u);
        Check.Eq(FakeHost.TapJumps + FakeHost.Jumps, 0, "jump-loot off: no jump: " + Order());
    });

    private static void LowStaminaNoJump() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 5u);
        Check.Eq(FakeHost.TapJumps + FakeHost.Jumps, 0, "stamina 5: no jump: " + Order());
    });

    // The flight holds heals (ACE refuses them in the air), so health under Heal At (60 by
    // default) picks up with a bend-down; at Heal At the jump goes.
    private static void LowHealthNoJump() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, health: 59u);
        Check.Eq(FakeHost.TapJumps + FakeHost.Jumps, 0, "health 59 (Heal At 60): no jump: " + Order());
        Check.False(ActionPacer.LootJumpAirborne(_host, out _), "nothing held for a landing");
        LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, health: 60u);
        Check.Eq(FakeHost.TapJumps, 1, "health 60: the jump goes: " + Order());
    });

    // The flight budget (RynthAiPlugin.LootJumpFlightBudgetMs): a send in the air with no fresh jump
    // is predicted by jumpAge + the recent no-bend-down answer time.
    private static void FlightBudgetLetsEarlySendThrough() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true, thirdGem: true);
        uint first = FakeHost.ExternalMoves[0].Id;
        Air(up: true);
        TickFor(tick, 100);
        FakeHost.Containers[first] = Player;     // answered 100 ms in, no bend-down
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 500);
        Check.Eq(FakeHost.ExternalMoves.Count, 2, "the next send goes out at the confirm: " + Order());
        Check.Eq(FakeHost.TapJumps, 1, "in the air, with no new jump");
        Check.False(FakeHost.Logs.Any(l => l.Contains("holding 0x")), "no hold logged");
    });

    private static void FlightBudgetHoldsLateSend() => Clocked(() =>
    {
        var tick = LootUntilFirstMove(jumpEnabled: true, secondGem: true, stamina: 100u, serverGates: true, thirdGem: true);
        uint first = FakeHost.ExternalMoves[0].Id;
        Air(up: true);
        TickFor(tick, 460);
        FakeHost.Containers[first] = Player;     // answered ~480 ms in, still no bend-down (under the 500 ms mark)
        TickUntil(tick, () => Confirmed(first), 300);
        Check.True(Confirmed(first), "the first is confirmed");
        Check.True(FakeHost.Logs.Any(l => l.Contains("no bend-down or Return to wait for")), "as a no-bend-down pickup");
        // jumpAge ~500 plus an answer that took ~480 ms is over the budget: the next one waits for the landing.
        TickFor(tick, 400);
        Check.Eq(FakeHost.ExternalMoves.Count, 1, "no send into the rest of the flight: " + Order());
        Check.Eq(FakeHost.TapJumps, 1, "no jump yet");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("holding 0x")), 1, "the hold is logged once");
        string hold = FakeHost.Logs.FirstOrDefault(l => l.Contains("holding 0x")) ?? "(none)";
        Check.True(hold.Contains("ms into the flight") && hold.Contains("takes about 4"), "naming jumpAge and the expected answer: " + hold);
        Air(up: false);                          // landed
        TickUntil(tick, () => FakeHost.ExternalMoves.Count >= 2, 1500);
        Check.Eq(FakeHost.ExternalMoves.Count, 2, "after the landing the next goes");
        int at = FakeHost.Order.IndexOf($"move:{FakeHost.ExternalMoves[1].Id:X8}");
        Check.True(at > 0 && FakeHost.Order[at - 1] == "tapjump", "with a fresh jump: " + Order());
        Check.Eq(FakeHost.TapJumps, 2, "two jumps in all");
    });

    private static void ResetClearsFlightHoldLogged() => Clocked(() =>
    {
        LootUntilFirstMove(jumpEnabled: true, secondGem: false, stamina: 100u, serverGates: true);
        SetField("_flightHoldLogged", true);
        typeof(RynthAiPlugin).GetMethod("ResetCurrentLootItem", Any)!.Invoke(_bot, null);
        Check.False(Field<bool>("_flightHoldLogged"), "hold-logged flag cleared");
    });
}
