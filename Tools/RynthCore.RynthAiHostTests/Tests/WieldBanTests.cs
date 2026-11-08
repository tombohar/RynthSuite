using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

/// <summary>
/// The false "it never reached your hand" ban: combat's wield of a weapon landed, buffing swapped
/// it out for the wand before combat looked again, each of those wields counted as a failed try,
/// and after the third the weapon was left out of the choice for 30 min. The engine's equipment
/// list (FakeHost.Equipped, API v87) plays the server's wield messages here. A wield that truly
/// never shows in the list must still be banned.
/// </summary>
internal static class WieldBanTests
{
    private const uint Player = 0x50000C01;
    private const uint Blade = 0x800484AA, Wand = 0x80048401, Nodachi = 0x80048402;
    private const uint MeleeSlot = 0x00100000, HeldSlot = 0x01000000, ShieldSlot = 0x00200000;
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Register(Runner r)
    {
        r.Add("wield ban: a weapon seen wielded between combat's looks is not banned, its tries start over", SeenWieldedNotBanned);
        r.Add("wield ban: no ban while the equipment list shows the weapon at the give-up", ListShowsItAtGiveUp);
        r.Add("wield ban: a wield that never shows in the list is still banned", NeverShownStillBanned);
        r.Add("wield ban: one sighting, then three wields never seen: banned (a new run forgets the sighting)", SeenOnceThenNeverBanned);
        r.Add("wield ban: a weapon in the shield slot is not a main-hand sighting, it still rests", ShieldSlotNotASighting);
        r.Add("wield ban: the server's 'skill too low' still bans at once", SkillTooLowStillBans);
        r.Add("wield ban: the plugin's sampler call alone resets the tries (no heartbeat)", DirectSamplerResetsTries);
        r.Add("wield ban: RynthAiPlugin's tick samples wields before the buffing hold returns", PluginSamplesBeforeBuffHold);
        r.Add("wield ban: a stale try count stops the equipment list sampling", StaleCountStopsSampling);
    }

    private sealed class Rig
    {
        public readonly CombatManager C;
        private readonly MethodInfo _send = typeof(CombatManager).GetMethod("MarkWieldSent", F)!;
        private readonly MethodInfo _step = typeof(CombatManager).GetMethod("SwapStep", F)!;

        public Rig(bool equipped = true, bool weenieErrors = false)
        {
            FakeHost.Reset();
            FakeHost.EquippedCalls = equipped;
            FakeHost.WeenieErrorCalls = weenieErrors;
            FakeHost.Names[Blade] = "Test Blade";
            FakeHost.Names[Wand] = "Fire Wand";
            FakeHost.Names[Nodachi] = "Nodachi";
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, new[] { Blade, Wand, Nodachi });
            C = new CombatManager(host, new LegacyUiSettings(), cache);
            C.SetPlayerId(Player);
            C.SwapTracker.ResolveMs = 0;   // each look after a send resolves it (no real 2.5 s waits)
            Hold(Wand, HeldSlot);
        }

        /// <summary>What the server says is wielded now: only <paramref name="id"/>.</summary>
        public void Hold(uint id, uint slot)
        {
            FakeHost.Equipped.Clear();
            FakeHost.Equipped[id] = slot;
            EquippedState.Invalidate();
        }

        public void Send(uint id, uint from) => _send.Invoke(C, new object[] { unchecked((int)id), unchecked((int)from) });
        public WeaponSwapTracker.Step Step(uint id, uint inHand) =>
            (WeaponSwapTracker.Step)_step.Invoke(C, new object[] { unchecked((int)id), unchecked((int)inHand) })!;
        public bool Banned(uint id) => C.SkillTooLowFor(unchecked((int)id));
        public int Tries(uint id) => C.SwapTracker.TriesFor(unchecked((int)id));
    }

    private static bool NeverReachedLogged() => FakeHost.Logs.Any(l => l.Contains("never reached your hand"));

    /// <summary>A weapon that lands, is swapped out for the wand by buffing, and lands again is never banned: three times over.</summary>
    private static void SeenWieldedNotBanned()
    {
        var rig = new Rig();
        try
        {
            for (int i = 1; i <= 3; i++)
            {
                rig.Send(Blade, Wand);                       // combat sends the wield
                rig.Hold(Blade, MeleeSlot);                  // the server wields it
                rig.C.OnHeartbeat();                         // a tick samples the equipment list
                Check.Eq(rig.Tries(Blade), 0, $"wield {i}: seen in hand, its try count starts over");
                rig.Hold(Wand, HeldSlot);                    // buffing swaps it out for the wand
                rig.Step(Blade, Wand);                       // combat looks: the wand is in hand
            }
            Check.False(rig.Banned(Blade), "three wields that landed: not left out of the weapon choice");
            Check.False(NeverReachedLogged(), "no 'never reached your hand' line");
        }
        finally { EquippedState.Invalidate(); }
    }

    /// <summary>The give-up itself reads the list, and a list holding the weapon is no ban.</summary>
    private static void ListShowsItAtGiveUp()
    {
        var rig = new Rig();
        try
        {
            for (int i = 1; i <= 3; i++)
            {
                rig.Send(Blade, Wand);
                // No tick samples between the wields here. At the third look the server's list
                // holds the weapon (the hand read combat passed in is stale).
                if (i == 3) { FakeHost.Equipped[Blade] = MeleeSlot; EquippedState.Invalidate(); }
                rig.Step(Blade, Wand);
            }
            Check.False(rig.Banned(Blade), "the list shows it wielded at the give-up: not banned");
            Check.False(NeverReachedLogged(), "no 'never reached your hand' line");

            // Control: no equipment list from the engine, nothing is known: no ban either way (as before).
            var blind = new Rig(equipped: false);
            for (int i = 1; i <= 3; i++) { blind.Send(Blade, Wand); blind.Step(Blade, Wand); }
            Check.False(blind.Banned(Blade), "no equipment list: no 'never reached' ban");
        }
        finally { EquippedState.Invalidate(); }
    }

    /// <summary>A wield the client or server truly refuses (never in the list) still bans.</summary>
    private static void NeverShownStillBanned()
    {
        var rig = new Rig();
        try
        {
            for (int i = 1; i <= 3; i++)
            {
                rig.Send(Blade, Wand);
                rig.C.OnHeartbeat();                         // ticks sample: only the wand, every time
                Check.Eq(rig.Tries(Blade), i, $"wield {i}: never seen, the try counts");
                rig.Step(Blade, Wand);
            }
            Check.True(rig.Banned(Blade), "three wields never seen in the list: left out of the weapon choice");
            Check.True(NeverReachedLogged(), "logged as 'never reached your hand'");
        }
        finally { EquippedState.Invalidate(); }
    }

    /// <summary>
    /// A sighting counts for its own run of wields only. The weapon lands once (buffing swaps it
    /// out), then stops reaching the hand: three unseen wields still end in the ban, instead of
    /// three wields and an off-hand clear every 2 min forever.
    /// </summary>
    private static void SeenOnceThenNeverBanned()
    {
        var rig = new Rig();
        try
        {
            rig.Send(Blade, Wand);
            rig.Hold(Blade, MeleeSlot);
            rig.C.OnHeartbeat();
            Check.Eq(rig.Tries(Blade), 0, "the landed wield: seen, the count starts over");
            rig.Hold(Wand, HeldSlot);
            rig.Step(Blade, Wand);
            for (int i = 1; i <= 3; i++)
            {
                rig.Send(Blade, Wand);
                rig.C.OnHeartbeat();                         // only the wand, every time
                rig.Step(Blade, Wand);
            }
            Check.True(rig.Banned(Blade), "three later wields never seen: left out of the weapon choice");
            Check.True(NeverReachedLogged(), "logged as 'never reached your hand'");
        }
        finally { EquippedState.Invalidate(); }
    }

    /// <summary>
    /// Only main-hand slots count (as InHandWeaponId reads them). A weapon chosen while it sits in
    /// the shield slot is not a landed wield: its tries count, and after three it rests instead
    /// of being sent again every 2.5 s.
    /// </summary>
    private static void ShieldSlotNotASighting()
    {
        var rig = new Rig();
        try
        {
            FakeHost.Equipped[Blade] = ShieldSlot;           // the off-hand slot, next to the wand
            EquippedState.Invalidate();
            var last = WeaponSwapTracker.Step.Swap;
            for (int i = 1; i <= 3; i++)
            {
                rig.Send(Blade, Wand);
                rig.C.OnHeartbeat();
                Check.Eq(rig.Tries(Blade), i, $"wield {i}: the shield slot is no sighting, the try counts");
                last = rig.Step(Blade, Wand);
            }
            Check.Eq(last, WeaponSwapTracker.Step.Hold, "after three tries: rested, not sent again");
            Check.Eq(rig.Step(Blade, Wand), WeaponSwapTracker.Step.Hold, "still resting on the next look");
        }
        finally { EquippedState.Invalidate(); }
    }

    /// <summary>The server's own "skill too low" answer is untouched by the sightings: banned at once.</summary>
    private static void SkillTooLowStillBans()
    {
        var rig = new Rig(weenieErrors: true);
        try
        {
            rig.Send(Nodachi, Wand);
            FakeHost.WeenieError(CombatManager.SkillTooLowErr, 0x00A0, Nodachi);
            rig.C.OnHeartbeat();
            rig.Step(Nodachi, Wand);
            Check.True(rig.Banned(Nodachi), "the server refused it: left out of the weapon choice");
        }
        finally { EquippedState.Invalidate(); }
    }

    /// <summary>The sampler the plugin calls every tick works without combat's heartbeat.</summary>
    private static void DirectSamplerResetsTries()
    {
        var rig = new Rig();
        try
        {
            rig.Send(Blade, Wand);
            Check.Eq(rig.Tries(Blade), 1, "setup: one wield sent");
            rig.Hold(Blade, MeleeSlot);
            rig.C.NoteSwapWieldsSeen();
            Check.Eq(rig.Tries(Blade), 0, "the sampler alone sees it in hand: the count starts over");
        }
        finally { EquippedState.Invalidate(); }
    }

    /// <summary>
    /// Source pin. The buffing hold returns from OnTick before combat's heartbeat runs, so the
    /// plugin's own sampler call must come after the buff step and before that hold.
    /// </summary>
    private static void PluginSamplesBeforeBuffHold()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Plugins"))) root = Path.GetDirectoryName(root);
        Check.True(root != null, "found the repo (a Plugins folder above the test output)");
        if (root == null) return;
        string src = File.ReadAllText(Path.Combine(root, "Plugins", "RynthCore.Plugin.RynthAi", "RynthAiPlugin.cs"));
        int buffStep = src.IndexOf("Guard(TickStep.BuffsAndVitals, () => _buffManager?.OnHeartbeat());", StringComparison.Ordinal);
        int sample = src.IndexOf("_combatManager?.NoteSwapWieldsSeen();", StringComparison.Ordinal);
        int buffHold = src.IndexOf("if (settings.IsMacroRunning && _activity == BotActivity.Buffing)", StringComparison.Ordinal);
        Check.True(buffStep >= 0, "the buff step is in OnTick");
        Check.True(buffHold >= 0, "the buffing hold is in OnTick");
        Check.True(sample >= 0, "OnTick calls NoteSwapWieldsSeen");
        Check.True(sample > buffStep && sample < buffHold, "after the buff step and before the buffing hold's return");
        int holdReturn = buffHold >= 0 ? src.IndexOf("return;", buffHold, StringComparison.Ordinal) : -1;
        Check.True(holdReturn > buffHold, "the buffing hold returns early (what makes the call's place matter)");
    }

    /// <summary>A try count left behind with no send for 2 min no longer keeps the sampler reading every tick.</summary>
    private static void StaleCountStopsSampling()
    {
        var tr = new WeaponSwapTracker();
        var t = new DateTime(2026, 11, 2, 12, 0, 0);
        tr.Sent(5, 1, t);
        tr.Next(5, 1, t.AddSeconds(3));                      // not seen in 2.5 s: the try stays counted
        Check.Eq(tr.PendingId, 0, "setup: nothing in flight");
        Check.Eq(tr.TriesFor(5), 1, "setup: one try counted");
        Check.True(tr.HasAttemptsAt(t.AddSeconds(10)), "10 s after the send: sampled");
        Check.False(tr.HasAttemptsAt(t.AddMinutes(3)), "3 min after the last send: not sampled");
        tr.Sent(5, 1, t.AddMinutes(3));
        Check.True(tr.HasAttemptsAt(t.AddMinutes(3)), "a new send: sampled again");
    }
}
