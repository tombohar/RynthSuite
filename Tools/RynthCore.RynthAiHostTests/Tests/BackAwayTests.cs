using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The back-away combat does on a server plugin's request (CombatManager.BackAwayFrom, RynthAi.Hub
// BackAwayFrom). It was the Empyrean Handler lethal-ring evade (Silentkelpie's PR 12) until 2026-10-20,
// when the Handler's detection and its setting went to RynthILT (Infinite Leaftide content); RynthAi
// keeps the generic part: face the object, back-jump, hold attacks until it settles. Gated: only while
// the macro is running with combat on, and only inside the radius (at least Ring Range). RynthAi alone
// never reacts to the Handler's line any more.
internal static class BackAwayTests
{
    private const uint Player = 0x50000D11;
    private const uint Danger = 0x80300110;
    private const uint Lb = 0xA9B40000;
    private const string RingLine = "The handler briefly channels infused Empyrean hatred to release a lethal ring.";

    public static void Register(Runner r)
    {
        r.Add("back away: inside the radius with the macro running and combat on, one back-jump (and none while it runs)", Jumps);
        r.Add("back away: not with the macro stopped or combat off", Gated);
        r.Add("back away: outside the radius (Ring Range widens it), or an object without a position: no jump", NotThese);
        r.Add("back away: RynthAi alone ignores the Empyrean Handler's ring line (RynthILT's now)", HandlerLineIgnored);
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { MonsterRange = 40, EnableCombat = true };
        public readonly CombatManager Combat;
        public int Jumps;
        public float Heading = -1;
        public bool Busy;

        public Rig(float dangerYards, bool place = true)
        {
            FakeHost.Reset();
            FakeHost.Names[Player] = "Evader";
            FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.Names[Danger] = "Empyrean Handler";
            if (place) FakeHost.Positions[Danger] = (Lb | 0x1C, 96f, 96f + dangerYards, 0f);
            FakeHost.ItemTypes[Danger] = 0x10;
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { Danger });
            S.IsMacroRunning = true;
            Combat = new CombatManager(host, S, cache);
            Combat.SetPlayerId(Player);
            Combat.ScanNearbyTargets();
            Combat.RequestBackJump = (h, ms) => { Jumps++; Heading = h; Busy = true; return true; };
            Combat.JumpBusy = () => Busy;
        }

        public int Ask(double radius = 12, int holdMs = 750) => Combat.BackAwayFrom(unchecked((int)Danger), radius, holdMs, "test");
    }

    private static void Jumps()
    {
        var rig = new Rig(dangerYards: 5);
        Check.Eq(rig.Ask(), RynthAiHub.BackAwayStarted, "5 yards inside 12: started");
        Check.Eq(rig.Jumps, 1, "one back-jump");
        Check.True(rig.Combat.BackAwayActive, "combat holds while it runs");
        Check.Eq(rig.Ask(), RynthAiHub.BackAwayNotNow, "a second request while backing away: not now");
        Check.Eq(rig.Jumps, 1, "still one jump");

        var refused = new Rig(5);
        refused.Combat.RequestBackJump = (_, _) => false;
        Check.Eq(refused.Ask(), RynthAiHub.BackAwayJumpRefused, "the jumper refused: said so");
        Check.False(refused.Combat.BackAwayActive, "and combat isn't held");
    }

    private static void Gated()
    {
        var stopped = new Rig(5);
        stopped.S.IsMacroRunning = false;
        Check.Eq(stopped.Ask(), RynthAiHub.BackAwayNotNow, "macro stopped: not now");
        Check.Eq(stopped.Jumps, 0, "no jump");

        var noCombat = new Rig(5);
        noCombat.S.EnableCombat = false;
        Check.Eq(noCombat.Ask(), RynthAiHub.BackAwayNotNow, "combat off: not now");
        Check.Eq(noCombat.Jumps, 0, "no jump");
    }

    private static void NotThese()
    {
        var far = new Rig(30);
        Check.Eq(far.Ask(), RynthAiHub.BackAwayOutside, "30 yards away is outside 12");
        Check.Eq(far.Jumps, 0, "no jump");
        far.S.RingRange = 40;
        Check.Eq(far.Ask(), RynthAiHub.BackAwayStarted, "Ring Range 40 widens the radius: 30 yards is inside");

        var unknown = new Rig(5, place: false);
        Check.Eq(unknown.Ask(), RynthAiHub.BackAwayUnknownObject, "no position for the object");
        Check.Eq(unknown.Jumps, 0, "no jump");
    }

    private static void HandlerLineIgnored()
    {
        var rig = new Rig(5);
        rig.Combat.HandleChatForDamage(RingLine, 0x0C);
        rig.Combat.HandleChatForDebuffs(RingLine);
        Check.Eq(rig.Jumps, 0, "the ring line alone does nothing in RynthAi (no server plugin asked)");
        Check.False(rig.Combat.BackAwayActive, "and combat isn't held");
        Check.True(new LegacyUiSettings().HandlerRingEvade, "the old profile field still loads (on by default; unused, for RynthILT's import)");
    }
}
