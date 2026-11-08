using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Unbox Aetheria When Idle (a player's request, 2026-11-01: "auto aetheria unbox when idle option").
// ACE (Entity/Aetheria.cs): an Aetheria Mana Stone used on a Coalesced Aetheria reveals its sigil
// ("A sigil rises ...", the name becomes "Aetheria", UseDone); the stone is not used up; busy is
// UseDone YoureTooBusy. RynthAi does it one piece at a time, only when idle, and backs off a refusal.
internal static class AetheriaUnboxTests
{
    private const uint Player = TickIsolationTests.Player;
    private const uint Blue = 0x80000A51, Red = 0x80000A52, Stone = 0x80000A53, Revealed = 0x80000A54, Monster = 0x80000A55;
    private const uint ItemTypeMisc = 0x80;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("aetheria: idle with the option on, a mana stone goes on one coalesced aetheria, and the next only after its sigil rose", UnboxesOneAtATime);
        r.Add("aetheria: nothing while a monster is on us; it unboxes once the fight is over, and a fight mid-unbox takes the tick back", NotInAFight);
        r.Add("aetheria: nothing with the option off or the macro off", NothingWhenOff);
        r.Add("aetheria: a 'too busy' refusal waits 10 s, then longer; no answer at all is a failed try", RefusalBacksOff);
        r.Add("aetheria: coalesced aetheria and no mana stone: said once in chat, not again", NoStoneSaidOnce);
        r.Add("aetheria: an already revealed 'Aetheria' is left alone", RevealedLeftAlone);
        r.Add("aetheria: /ra aetheria unbox does every piece with the option and the macro off, then says how many", ManualRun);
        r.Add("aetheria: settings - off by default, saved, copied, sent to the engine face, metas reach UnboxAetheriaWhenIdle", Settings);
    }

    private static void Item(uint id, string name, uint wcid)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = ItemTypeMisc;
        FakeHost.Wcids[id] = wcid;
        FakeHost.Containers[id] = Player;
    }

    private static TickIsolationTests.Bot Rig(bool stone = true, bool combat = false, bool revealedOnly = false)
    {
        var bot = new TickIsolationTests.Bot(combat: combat, heal: false, arrange: () =>
        {
            FakeHost.ContentsCalls = true;   // the live pack walk
            FakeHost.UseOnCalls = true;
            if (revealedOnly) Item(Revealed, "Aetheria", AetheriaUnboxer.WcidBlue);
            else
            {
                Item(Blue, AetheriaUnboxer.CoalescedName, AetheriaUnboxer.WcidBlue);
                Item(Red, AetheriaUnboxer.CoalescedName, AetheriaUnboxer.WcidRed);
            }
            if (stone) Item(Stone, AetheriaUnboxer.StoneName, AetheriaUnboxer.WcidManaStone);
        });
        foreach (uint id in new[] { Blue, Red, Stone, Revealed })
            if (FakeHost.Names.ContainsKey(id)) _ = bot.Cache[unchecked((int)id)];
        bot.S.UnboxAetheriaWhenIdle = true;
        bot.S.MonsterRange = 20;
        var unboxer = (AetheriaUnboxer)bot.Call("CreateAetheriaUnboxer", bot.S)!;
        unboxer.NowMs = () => bot.Now;
        bot.Set("_aetheriaUnboxer", unboxer);
        return bot;
    }

    private static AetheriaUnboxer U(TickIsolationTests.Bot bot) => (AetheriaUnboxer)bot.Get("_aetheriaUnboxer")!;
    private static bool Tick(TickIsolationTests.Bot bot) => (bool)bot.Call("TickAetheriaUnbox", bot.S)!;

    /// <summary>The server's answer to the unbox in flight: the sigil line, the new name, the UseDone.</summary>
    private static void Reveal(TickIsolationTests.Bot bot, uint id)
    {
        U(bot).OnChat(AetheriaUnboxer.SigilLine);
        FakeHost.Names[id] = "Aetheria";
        FakeHost.UseDone(0);
    }

    private static CombatManager.ScannedTarget At(double yards) =>
        new() { Id = unchecked((int)Monster), Distance = yards, Name = "Drudge Skulker" };

    private static void UnboxesOneAtATime()
    {
        try
        {
            var bot = Rig();
            Check.True(Tick(bot), "the unbox goes out, and the bot holds still for the clap");
            Check.Eq(FakeHost.UsesOn.Count, 1, "one use-on");
            var first = FakeHost.UsesOn[0];
            Check.True(first.Source == Stone && (first.Target == Blue || first.Target == Red), "the mana stone on a coalesced aetheria");

            for (int i = 0; i < 5; i++) { bot.Now += 500; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 1, "no answer yet: nothing more goes out");

            Reveal(bot, first.Target);
            Check.False(Tick(bot), "the sigil rose: the unbox is over");
            Check.Eq(U(bot).Revealed, 1, "one revealed");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Aetheria] revealed")), "and logged");

            bot.Now += 500;
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 1, "the server stays busy for the return to ready: a short pause first");
            bot.Now += AetheriaUnboxer.GapMs;
            Check.True(Tick(bot), "the next piece goes out");
            Check.Eq(FakeHost.UsesOn.Count, 2, "two use-ons");
            var second = FakeHost.UsesOn[1];
            Check.True(second.Source == Stone && second.Target != first.Target && (second.Target == Blue || second.Target == Red),
                "the same stone (it isn't used up) on the other piece");

            Reveal(bot, second.Target);
            Tick(bot);
            for (int i = 0; i < 20; i++) { bot.Now += 1_000; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 2, "nothing coalesced left: nothing more");
            Check.Eq(U(bot).Revealed, 2, "both revealed");
            Check.False(FakeHost.Chat.Any(c => c.Contains("no Aetheria Mana Stone")), "and no out-of-stones line (the stone is still there)");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void NotInAFight()
    {
        try
        {
            var bot = Rig(combat: true);
            var cm = (CombatManager)bot.Get("_combatManager")!;
            cm.SetScannedTargetsForTests(new[] { At(2) });
            for (int i = 0; i < 5; i++) { bot.Now += 2_000; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 0, "a monster on us: no unbox (the server would put us in peace mode)");

            bot.S.EnableCombat = false;
            for (int i = 0; i < 3; i++) { bot.Now += 2_000; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 0, "not with Enable Combat off either: a fight by hand counts");
            bot.S.EnableCombat = true;

            cm.SetScannedTargetsForTests(Array.Empty<CombatManager.ScannedTarget>());
            bot.Now += 2_000;
            Check.True(Tick(bot), "the fight is over: it unboxes");
            Check.Eq(FakeHost.UsesOn.Count, 1, "one use-on");

            cm.SetScannedTargetsForTests(new[] { At(5) });
            Check.False(Tick(bot), "a monster comes mid-unbox: no hold, combat has the tick");
            Check.Eq(FakeHost.UsesOn.Count, 1, "and nothing more goes out");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void NothingWhenOff()
    {
        try
        {
            var bot = Rig();
            bot.S.UnboxAetheriaWhenIdle = false;
            for (int i = 0; i < 5; i++) { bot.Now += 6_000; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 0, "the option off: nothing");

            bot.S.UnboxAetheriaWhenIdle = true;
            bot.S.IsMacroRunning = false;
            for (int i = 0; i < 5; i++) { bot.Now += 6_000; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 0, "the macro off: nothing");

            bot.S.IsMacroRunning = true;
            bot.Now += 6_000;
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 1, "both on: it unboxes");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void RefusalBacksOff()
    {
        try
        {
            var bot = Rig();
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 1, "the first try");
            FakeHost.UseDone(AetheriaUnboxer.TooBusyErr);
            Check.False(Tick(bot), "refused as busy: over");

            bot.Now += 9_000;
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 1, "9 s later: still waiting");
            bot.Now += 1_500;
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 2, "past 10 s: tried again");

            // No answer at all: a failed try after the timeout, and a longer wait (30 s).
            bot.Now += AetheriaUnboxer.UnboxTimeoutMs;
            Check.False(Tick(bot), "no answer in 10 s: given up for now");
            bot.Now += 20_000;
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 2, "20 s later: the second wait is 30 s");
            bot.Now += 11_000;
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 3, "past 30 s: tried again");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Aetheria] unbox of") && l.Contains("too busy")), "the refusal is logged");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void NoStoneSaidOnce()
    {
        try
        {
            var bot = Rig(stone: false);
            for (int i = 0; i < 6; i++) { bot.Now += 6_000; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 0, "no stone: nothing to use");
            Check.Eq(FakeHost.Chat.Count(c => c.Contains("no Aetheria Mana Stone")), 1, "said once in chat");
            Check.True(FakeHost.Chat.Any(c => c.Contains("2 coalesced aetheria")), "with how many are waiting");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void RevealedLeftAlone()
    {
        try
        {
            var bot = Rig(revealedOnly: true);
            for (int i = 0; i < 4; i++) { bot.Now += 6_000; Tick(bot); }
            Check.Eq(FakeHost.UsesOn.Count, 0, "an 'Aetheria' (sigil showing) is not unboxed again");
            Check.True(U(bot).Describe().Contains("0 coalesced"), "status: none coalesced (" + U(bot).Describe() + ")");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void ManualRun()
    {
        try
        {
            var bot = Rig();
            bot.S.UnboxAetheriaWhenIdle = false;
            bot.S.IsMacroRunning = false;
            var u = U(bot);
            Check.True(u.RequestManual(out string reply), "asked by hand: " + reply);
            Check.True(reply.Contains("2 coalesced"), "the reply says how many");
            Check.False(Tick(bot), "the macro is off: nothing to hold");
            Check.Eq(FakeHost.UsesOn.Count, 1, "but the unbox goes out");
            Reveal(bot, FakeHost.UsesOn[0].Target);
            Tick(bot);
            bot.Now += AetheriaUnboxer.GapMs;
            Tick(bot);
            Check.Eq(FakeHost.UsesOn.Count, 2, "the second piece");
            Reveal(bot, FakeHost.UsesOn[1].Target);
            Tick(bot);
            bot.Now += AetheriaUnboxer.GapMs;
            Tick(bot);
            Check.True(FakeHost.Chat.Any(c => c.Contains("Aetheria: revealed 2")), "the run ends with how many: " + string.Join(" | ", FakeHost.Chat));
            Check.False(u.ManualRun, "and is over");
            Check.False(u.RequestManual(out string none), "nothing left to unbox: refused");
            Check.True(none.Contains("no Coalesced Aetheria"), "with the reason: " + none);
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void Settings()
    {
        Check.False(new LegacyUiSettings().UnboxAetheriaWhenIdle, "off by default");
        var old = JsonSerializer.Deserialize("{\"EnableAutostack\":true}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.False(old.UnboxAetheriaWhenIdle, "a profile from before the setting: off");
        var saved = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { UnboxAetheriaWhenIdle = true }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(saved.UnboxAetheriaWhenIdle, "on saves and loads");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { UnboxAetheriaWhenIdle = true }, dst });
        Check.True(dst.UnboxAetheriaWhenIdle, "a profile switch copies it");

        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.UnboxAetheriaWhenIdle == null, "an older engine's payload has no value");
        var s = new LegacyUiSettings { UnboxAetheriaWhenIdle = true };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", Any)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.UnboxAetheriaWhenIdle == true, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.UnboxAetheriaWhenIdle, "an engine save without the field leaves it");
        dash.ApplySettingsJson("{\"UnboxAetheriaWhenIdle\":false}");
        Check.False(s.UnboxAetheriaWhenIdle, "an engine save with it sets it");

        var ms = new LegacyUiSettings();
        var e = ExpressionTests.Engine(settings: ms);
        Check.Eq(e.Evaluate("vtsetsetting[UnboxAetheriaWhenIdle, 1]"), "1", "metas: vtsetsetting knows it");
        Check.True(ms.UnboxAetheriaWhenIdle, "and turns it on");
        Check.Eq(e.Evaluate("vtgetsetting[UnboxAetheriaWhenIdle]"), "1", "vtgetsetting reads it back");
    }
}
