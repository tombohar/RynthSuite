using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// "RynthAi.Activity" v1 (2026-10-11): the read-only bits RynthSort reads so its pack moves stay
// out of RynthAi's way. The mapping from RynthAi's state is ActivityInterface.Compose; the table
// comes from ActivityInterface.Query (what the engine's GetPluginInterface hands other plugins).
internal static class ActivityInterfaceTests
{
    public static void Register(Runner r)
    {
        r.Add("activity: each arbiter decision maps to its own bit", ArbiterBits);
        r.Add("activity: corpse, salvage queue, server wait, crafting and AutoCram add their bits", ExtraBits);
        r.Add("activity: the table answers only its own name and versions", QueryTable);
        r.Add("activity: RynthAi's own unanswered action has its own bit (PacedAction), apart from the busy count", PacedBit);
        r.Add("yield: the RynthAi.Yield table answers only its own name and versions; nothing held before login", YieldTable);
    }

    private static void ArbiterBits()
    {
        Check.Eq(ActivityInterface.Compose(BotActivity.Idle, false, false, false, false, false, false), RynthAiActivity.None, "idle, macro off");
        Check.Eq(ActivityInterface.Compose(BotActivity.Idle, true, false, false, false, false, false), RynthAiActivity.MacroRunning, "idle, macro on");
        Check.Eq(ActivityInterface.Compose(BotActivity.Buffing, true, false, false, false, false, false),
            RynthAiActivity.MacroRunning | RynthAiActivity.Buffing, "buffing");
        Check.Eq(ActivityInterface.Compose(BotActivity.Combat, true, false, false, false, false, false),
            RynthAiActivity.MacroRunning | RynthAiActivity.Combat, "combat");
        Check.Eq(ActivityInterface.Compose(BotActivity.Looting, true, false, false, false, false, false),
            RynthAiActivity.MacroRunning | RynthAiActivity.Looting, "looting");
        Check.Eq(ActivityInterface.Compose(BotActivity.Salvaging, true, false, false, false, false, false),
            RynthAiActivity.MacroRunning | RynthAiActivity.Salvaging, "salvaging");
        Check.Eq(ActivityInterface.Compose(BotActivity.Navigating, true, false, false, false, false, false),
            RynthAiActivity.MacroRunning | RynthAiActivity.Navigating, "navigating");
    }

    private static void ExtraBits()
    {
        // A corpse RynthAi opened or claimed counts as looting whatever the tick's owner is.
        RynthAiActivity f = ActivityInterface.Compose(BotActivity.Navigating, true, corpseInHand: true, false, false, false, false);
        Check.True((f & RynthAiActivity.Looting) != 0 && (f & RynthAiActivity.Navigating) != 0, "corpse in hand while navigating -> looting too");
        f = ActivityInterface.Compose(BotActivity.Combat, true, false, salvageBusy: true, false, false, false);
        Check.True((f & RynthAiActivity.Salvaging) != 0, "salvage queue while fighting -> salvaging");
        f = ActivityInterface.Compose(BotActivity.Idle, false, false, false, serverWait: true, false, false);
        Check.Eq(f, RynthAiActivity.ServerAction, "a player's command waiting on the server, macro off");
        f = ActivityInterface.Compose(BotActivity.Idle, true, false, false, false, crafting: true, autoCram: true);
        Check.Eq(f, RynthAiActivity.MacroRunning | RynthAiActivity.Crafting | RynthAiActivity.AutoCram, "crafting + AutoCram");
    }

    private static unsafe void QueryTable()
    {
        try
        {
            Check.True(ActivityInterface.Query("RynthAi.Status", 1) == IntPtr.Zero, "another interface's name");
            Check.True(ActivityInterface.Query(RynthAiActivityApiV1.InterfaceName, 0) == IntPtr.Zero, "version 0");
            Check.True(ActivityInterface.Query(RynthAiActivityApiV1.InterfaceName, 2) == IntPtr.Zero, "a newer version than RynthAi has");
            IntPtr p = ActivityInterface.Query(RynthAiActivityApiV1.InterfaceName, 1);
            Check.True(p != IntPtr.Zero, "v1 answers");
            var t = (RynthAiActivityApiV1*)p;
            Check.Eq(t->Version, 1u, "table version");
            Check.Eq(t->StructSize, (uint)sizeof(RynthAiActivityApiV1), "table size");
            Check.True(t->GetActivityFlags != null, "function filled in");
            Check.True(ActivityInterface.Query(RynthAiActivityApiV1.InterfaceName, 1) == p, "the same table on every call");
            // No plugin instance (not logged in): not ready, flags cleared.
            uint flags = 0xFFFFFFFF;
            Check.Eq(t->GetActivityFlags(&flags), 0, "not ready without a logged-in plugin");
            Check.Eq(flags, 0u, "flags cleared when not ready");
        }
        finally
        {
            ActivityInterface.Release();
        }
    }

    private static void PacedBit()
    {
        RynthAiActivity f = ActivityInterface.Compose(BotActivity.Buffing, true, false, false, serverWait: true, false, false, pacedWait: true);
        Check.Eq(f, RynthAiActivity.MacroRunning | RynthAiActivity.Buffing | RynthAiActivity.ServerAction | RynthAiActivity.PacedAction,
            "a cast of RynthAi's out: waiting on the server, and its own action");
        f = ActivityInterface.Compose(BotActivity.Idle, false, false, false, serverWait: true, false, false);
        Check.Eq(f, RynthAiActivity.ServerAction, "only the busy count up (a leftover): no PacedAction");
        Check.Eq((uint)RynthAiActivity.PacedAction, 1u << 9, "the next free bit, after AutoCram");
    }

    private static unsafe void YieldTable()
    {
        try
        {
            Check.True(YieldInterface.Query(RynthAiActivityApiV1.InterfaceName, 1) == IntPtr.Zero, "another interface's name");
            Check.True(YieldInterface.Query(RynthAiYieldApiV1.InterfaceName, 0) == IntPtr.Zero, "version 0");
            Check.True(YieldInterface.Query(RynthAiYieldApiV1.InterfaceName, 2) == IntPtr.Zero, "a newer version than RynthAi has");
            IntPtr p = YieldInterface.Query(RynthAiYieldApiV1.InterfaceName, 1);
            Check.True(p != IntPtr.Zero, "v1 answers");
            var t = (RynthAiYieldApiV1*)p;
            Check.Eq(t->Version, 1u, "table version");
            Check.Eq(t->StructSize, (uint)sizeof(RynthAiYieldApiV1), "table size");
            Check.True(t->HoldForPlayer != null && t->ReleaseHold != null, "functions filled in");
            Check.True(YieldInterface.Query(RynthAiYieldApiV1.InterfaceName, 1) == p, "the same table on every call");
            Check.Eq(t->HoldForPlayer(1500), 0, "no logged-in plugin: nothing held");
            Check.Eq(t->ReleaseHold(), 1, "a release with nothing held is fine");
        }
        finally
        {
            YieldInterface.Release();
        }
    }
}
