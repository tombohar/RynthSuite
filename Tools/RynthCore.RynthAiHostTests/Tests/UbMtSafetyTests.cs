using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The safety limits on the ub-IT / Mag-Tools meta verbs (SilentKelpie's PR 12, taken 2026-10-19):
// VTank unit conversion only where documented, loot-ownership options, /ub give limits, key
// injection off by default, vendor / trade verbs only from a running meta.
internal static class UbMtSafetyTests
{
    public static void Register(Runner r)
    {
        r.Add("ub/mt safety: VTank distances convert only for the documented option names", VtConversionOnlyDocumentedNames);
        r.Add("ub/mt safety: values above 1 are yards and are never scaled", VtValuesAboveOneUnchanged);
        r.Add("ub/mt safety: loot-ownership options change LootOwnership and nothing else", VtLootOwnership);
        r.Add("ub/mt safety: keystroke verbs do nothing by default, said once", KeystrokesOffByDefault);
        r.Add("ub/mt safety: with the setting on the keystroke verbs reach the game window", KeystrokesWhenAllowed);
        r.Add("ub/mt safety: a meta cannot switch the keystroke setting on", MetaCannotEnableKeystrokes);
        r.Add("ub/mt safety: /mt trade accept and /mt vendor buy|sell refuse outside a running meta", VendorTradeRefusedOutsideMeta);
        r.Add("ub/mt safety: /mt trade accept and /mt vendor sell act for a running meta, one log line each", VendorTradeFromRunningMeta);
        r.Add("ub/mt safety: /mt vendor sell never sells equipped gear or RynthAi's own items", VendorSellSkipsProtected);
    }

    private const uint Player = 0x50000F41;
    private const uint Robe = 0x80282201, Cloak = 0x80282202, Sword = 0x80282203;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>A RynthAi plugin wired enough to run its /mt dispatcher: a carried robe, a worn cloak, a carried sword.</summary>
    private sealed class World
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();
        public readonly List<(uint Msg, uint W, uint L)> Posted = new();

        public World(bool tradeVendor = false)
        {
            FakeHost.Reset();
            FakeHost.TradeVendorCalls = tradeVendor;
            FakeHost.SalvageCalls = true;   // GetObjectWielderInfo: the worn cloak reads as worn
            FakeHost.Names[Player] = "Kelpie";
            FakeHost.PlayerIdValue = Player;
            foreach (var (id, name) in new[] { (Robe, "Faran Robe"), (Cloak, "Worn Cloak"), (Sword, "Sharp Sword") })
            {
                FakeHost.Names[id] = name;
                FakeHost.Containers[id] = Player;
                FakeHost.ItemTypes[id] = 0x2;
            }
            FakeHost.Wielded[Cloak] = 0x0400;
            var host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            foreach (uint id in new[] { Robe, Cloak, Sword }) cache.OnCreateObject(id);
            cache.Tick();
            GameInput.TestSink = (m, w, l) => Posted.Add((m, w, l));
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        /// <summary>A line typed in the chat bar.</summary>
        public bool Mt(string line) => (bool)typeof(RynthAiPlugin).GetMethod("HandleMtCommand", Any)!.Invoke(P, new object[] { line })!;
        /// <summary>A line a meta's chat-command action sends.</summary>
        public bool Meta(string line) => (bool)typeof(RynthAiPlugin).GetMethod("HandleMetaChatCommand", Any)!.Invoke(P, new object[] { line })!;
        public int ChatWith(string text) => FakeHost.Chat.Count(c => c.Contains(text, StringComparison.OrdinalIgnoreCase));
        public int LogWith(string text) => FakeHost.Logs.Count(c => c.Contains(text, StringComparison.OrdinalIgnoreCase));
        public void Dispose() => GameInput.TestSink = null;
    }

    private static readonly string[] KeyLines =
    {
        "/mt send enter", "/mt send msg hello", "/mt send cntrl+a", "/mt click ok", "/mt click 10 20",
        "/mt movement w 100", "/mt strafe left 100", "/mt client minimize", "/mt client restore", "/mt jump 100",
    };

    private static void KeystrokesOffByDefault()
    {
        Check.False(new LegacyUiSettings().AllowMetaKeystrokes, "the setting defaults off");
        var w = new World();
        try
        {
            int before = GameInput.PostAttempts;
            foreach (string line in KeyLines)
            {
                Check.True(w.Meta(line), line + " is handled (so it is not sent on as an unknown command)");
                w.Meta(line);   // a meta pulse again
            }
            Check.Eq(w.Posted.Count, 0, "nothing reached the game window");
            Check.Eq(GameInput.PostAttempts - before, 0, "and nothing was even attempted");
            Check.Eq(w.ChatWith("'Allow metas to send keystrokes"), 6, "each of the six verbs says so once, however often a meta repeats it");
            Check.Eq(w.LogWith("'Allow metas to send keystrokes"), 6, "and logs it once");

            // A chat command through /mt send is not a key press: it works either way.
            w.Meta("/mt send /say hello");
            Check.True(FakeHost.ChatCommands.Contains("/say hello"), "/mt send /command still goes to the chat parser");
            Check.Eq(w.Posted.Count, 0, "and posted no key");

            // Reading the cursor posts nothing, so it is not gated.
            int chat = w.ChatWith("ignored");
            w.Meta("/mt get xy");
            Check.Eq(w.ChatWith("ignored"), chat, "/mt get xy is not refused");
        }
        finally { w.Dispose(); }
    }

    private static (LegacyUiSettings S, MetaManager M) Meta()
    {
        FakeHost.Reset();
        var s = new LegacyUiSettings();
        var m = new MetaManager(s, FakeHost.Create(), new PlayerVitalsCache());
        m.SetRaCommandHandler(_ => { });
        return (s, m);
    }

    private static void VtConversionOnlyDocumentedNames()
    {
        var (s, m) = Meta();

        // Documented: the VTank distance options (landblock units x240 = yards).
        m.Expressions.Evaluate("vtsetsetting[AttackDistance,`0.025`]");
        Check.Eq(s.MonsterRange, 6, "AttackDistance 0.025 = 6 yd");
        m.TryHandleVtCommand("/vt opt set approachdistance 0.0175");
        Check.Eq(s.ApproachRange, 4, "approachdistance 0.0175 = 4 yd");

        // RynthAi's own names (rasetsetting, or a direct setting name) keep their units: no x240.
        s.CorpseApproachRangeMax = 3;
        m.Expressions.Evaluate("rasetsetting[CorpseApproachRangeMax,`0.5`]");
        Check.Near(s.CorpseApproachRangeMax, 0.5, 1e-9, "rasetsetting CorpseApproachRangeMax 0.5 stays 0.5 yd");
        m.Expressions.Evaluate("vtsetsetting[CorpseApproachRangeMax,`0.5`]");
        Check.Near(s.CorpseApproachRangeMax, 0.5, 1e-9, "a RynthAi setting named directly is not a VTank distance");
        m.TryHandleVtCommand("/vt opt set follownavmin 0.5");
        Check.Near(s.FollowNavMin, 0.5, 1e-9, "follownavmin is RynthAi's own name, in yards");

        // Options that are not distances are never scaled, whatever the value.
        m.TryHandleVtCommand("/vt opt set minimumringtargets 1");
        Check.Eq(s.MinRingTargets, 1, "minimumringtargets 1 stays 1 (not 240)");
        m.Expressions.Evaluate("vtsetsetting[BlacklistMonsterTimeoutSeconds,`1`]");
        Check.Eq(s.BlacklistTimeoutSec, 1, "a timeout of 1 s stays 1");
        m.TryHandleVtCommand("/vt opt set rebufftimeremainingseconds 1");
        Check.Eq(s.RebuffSecondsRemaining, 1, "a seconds option of 1 stays 1");
    }

    private static void VtValuesAboveOneUnchanged()
    {
        var (s, m) = Meta();
        foreach (var (name, value, expect) in new[] { ("attackdistance", "25", 25), ("attackdistance", "1.5", 2), ("attackdistance", "50", 50), ("approachdistance", "30", 30), ("ringdistance", "6", 6) })
        {
            m.TryHandleVtCommand($"/vt opt set {name} {value}");
            int got = name switch { "attackdistance" => s.MonsterRange, "approachdistance" => s.ApproachRange, _ => s.RingRange };
            Check.Eq(got, expect, $"{name} {value} is yards already -> {expect}");
        }
        // And through the expression.
        m.Expressions.Evaluate("vtsetsetting[AttackDistance,`40`]");
        Check.Eq(s.MonsterRange, 40, "vtsetsetting AttackDistance 40 stays 40 yd");
        // Zero is not a landblock fraction either.
        m.Expressions.Evaluate("vtsetsetting[AttackDistance,`0`]");
        Check.Eq(s.MonsterRange, 0, "0 stays 0");
        // Read back in VTank units.
        m.TryHandleVtCommand("/vt opt set attackdistance 24");
        Check.Eq(m.Expressions.Evaluate("vtgetsetting[AttackDistance]"), "0.1", "24 yd reads back as 0.1");
    }

    private static void VtLootOwnership()
    {
        var (s, m) = Meta();
        s.LootOwnership = 0;
        m.TryHandleVtCommand("/vt opt set lootfellowcorpses true");
        Check.Eq(s.LootOwnership, 1, "lootfellowcorpses true: fellowship kills");
        m.TryHandleVtCommand("/vt opt set lootallcorpses true");
        Check.Eq(s.LootOwnership, 2, "lootallcorpses true: all corpses");
        m.TryHandleVtCommand("/vt opt set lootfellowcorpses false");
        Check.Eq(s.LootOwnership, 2, "lootfellowcorpses false leaves 'all corpses' alone");
        m.TryHandleVtCommand("/vt opt set lootallcorpses false");
        Check.Eq(s.LootOwnership, 0, "lootallcorpses false: my kills");
        s.LootOwnership = 1;
        m.TryHandleVtCommand("/vt opt set lootallcorpses false");
        Check.Eq(s.LootOwnership, 1, "lootallcorpses false leaves 'fellowship kills' alone");
        m.TryHandleVtCommand("/vt opt set lootfellowcorpses false");
        Check.Eq(s.LootOwnership, 0, "lootfellowcorpses false: my kills");

        // Other options never touch it, and an unreadable value is ignored.
        s.LootOwnership = 2;
        m.TryHandleVtCommand("/vt opt set lootonlyrarecorpses 1");
        m.TryHandleVtCommand("/vt opt set usearcs 0");
        m.TryHandleVtCommand("/vt opt set lootallcorpses banana");
        Check.Eq(s.LootOwnership, 2, "other options and junk values leave LootOwnership alone");
    }

    private static void KeystrokesWhenAllowed()
    {
        var w = new World();
        try
        {
            w.S.AllowMetaKeystrokes = true;
            w.Meta("/mt send enter");
            Check.Eq(w.Posted.Count, 2, "send enter: key down and key up");
            w.Posted.Clear();
            w.Meta("/mt send msg hi");
            Check.Eq(w.Posted.Count, 4, "send msg hi: two keys, down and up");
            w.Posted.Clear();
            w.Meta("/mt click 10 20");
            Check.Eq(w.Posted.Count, 3, "click x y: move, down, up");
            w.Posted.Clear();
            w.Meta("/mt client minimize");
            Check.Eq(w.Posted.Count, 1, "client minimize");
            w.Posted.Clear();
            w.Meta("/mt movement w 0");
            Check.Eq(w.Posted.Count, 2, "movement w 0: pressed and released at once");
            w.Posted.Clear();
            w.Meta("/mt strafe right 0");
            Check.Eq(w.Posted.Count, 2, "strafe right 0");
            w.Posted.Clear();
            w.Meta("/mt jump");
            Check.Eq(w.Posted.Count, 2, "no jump hook: the space key");
            Check.Eq(w.ChatWith("Allow metas to send keystrokes"), 0, "nothing was refused");

            // A held key is released from the plugin tick, not a timer, and never held for ever.
            w.Posted.Clear();
            w.Meta("/mt movement w 99999999");
            Check.Eq(w.Posted.Count, 1, "held: only the key down so far");
            GameInput.Shutdown();
            Check.Eq(w.Posted.Count, 2, "Shutdown releases it at once");
        }
        finally { w.Dispose(); }
    }

    private static void MetaCannotEnableKeystrokes()
    {
        var w = new World();
        try
        {
            var m = new MetaManager(w.S, FakeHost.Create(playerId: Player), new PlayerVitalsCache());
            m.SetRaCommandHandler(_ => { });
            m.Expressions.Evaluate("rasetsetting[AllowMetaKeystrokes,`1`]");
            m.Expressions.Evaluate("vtsetsetting[AllowMetaKeystrokes,`1`]");
            m.TryHandleVtCommand("/vt opt set AllowMetaKeystrokes true");
            w.Mt("/mt opt set AllowMetaKeystrokes true");
            Check.False(w.S.AllowMetaKeystrokes, "no expression, /vt or /mt option can switch it on");
            w.Meta("/mt send enter");
            Check.Eq(w.Posted.Count, 0, "so the keys still go nowhere");
        }
        finally { w.Dispose(); }
    }

    private static void VendorTradeRefusedOutsideMeta()
    {
        var w = new World(tradeVendor: true);
        try
        {
            w.S.IsMacroRunning = true;
            w.S.EnableMeta = true;
            w.Mt("/mt vendor addsell Faran Robe");
            Check.Eq(w.ChatWith("Sell list"), 1, "building the list is allowed from a typed line");

            // Typed lines (the chat bar, a chat line, anything that is not a running meta).
            Check.True(w.Mt("/mt trade accept"), "typed /mt trade accept is handled ...");
            Check.True(w.Mt("/mt vendor sell"), "typed /mt vendor sell ...");
            Check.True(w.Mt("/mt vendor buy"), "typed /mt vendor buy ...");
            Check.Eq(FakeHost.TradeActions.Count, 0, "... but accepted nothing");
            Check.Eq(FakeHost.VendorSells.Count, 0, "... sold nothing");
            Check.Eq(FakeHost.VendorBuys.Count, 0, "... bought nothing");
            Check.Eq(w.ChatWith("only runs from a meta that is running"), 3, "and said why, three times");

            // A meta line while the macro or the Meta switch is off is not a running meta either.
            w.S.IsMacroRunning = false;
            w.Meta("/mt trade accept");
            w.Meta("/mt vendor sell");
            w.S.IsMacroRunning = true;
            w.S.EnableMeta = false;
            w.Meta("/mt trade accept");
            Check.Eq(FakeHost.TradeActions.Count, 0, "macro off or Meta off: no trade accept");
            Check.Eq(FakeHost.VendorSells.Count, 0, "macro off: no sale");
            // A meta repeating the same refusal says it once a minute (CommandReply.cs): the second
            // "/mt trade accept" refusal is in the log, not in chat again.
            Check.Eq(w.ChatWith("only runs from a meta that is running"), 5, "all refused with the reason (a meta's repeat once)");
            Check.Eq(w.LogWith("/mt trade accept only runs from a meta that is running"), 3, "every refusal is logged (one typed, two from the meta)");
        }
        finally { w.Dispose(); }
    }

    private static void VendorTradeFromRunningMeta()
    {
        var w = new World(tradeVendor: true);
        try
        {
            w.S.IsMacroRunning = true;
            w.S.EnableMeta = true;
            Check.True(w.Meta("/mt trade accept"), "a running meta's trade accept");
            Check.Eq(FakeHost.TradeActions.Count(a => a == "accept"), 1, "is sent");
            Check.Eq(w.LogWith("/mt trade accept (from the running meta)"), 1, "with one log line");

            w.Meta("/mt vendor addsell Faran Robe");
            Check.True(w.Meta("/mt vendor sell"), "a running meta's vendor sell");
            Check.Eq(FakeHost.VendorSells.Count, 1, "is sent once");
            Check.True(FakeHost.VendorSells[0].Items.SequenceEqual(new[] { Robe }), "with the listed item");
            Check.Eq(w.LogWith("/mt vendor sell (from the running meta)"), 1, "with one log line naming it");

            // The other trade verbs do not need a meta.
            int actions = FakeHost.TradeActions.Count;
            w.Mt("/mt trade decline");
            Check.Eq(FakeHost.TradeActions.Count, actions + 1, "typed /mt trade decline still works");
        }
        finally { w.Dispose(); }
    }

    private static void VendorSellSkipsProtected()
    {
        var w = new World(tradeVendor: true);
        try
        {
            w.S.IsMacroRunning = true;
            w.S.EnableMeta = true;
            w.Meta("/mt vendor addsell Worn Cloak");
            w.Meta("/mt vendor sell");
            Check.Eq(FakeHost.VendorSells.Count, 0, "an equipped item is never sold");
            Check.True(w.ChatWith("Nothing left to sell") == 1 && w.LogWith("equipped") >= 1, "and the skip is logged");

            // An item RynthAi uses (the Weapons list) is never sold either; the others still are.
            w.S.ItemRules.Add(new ItemRule { Id = unchecked((int)Sword), Name = "Sharp Sword" });
            w.Meta("/mt vendor addsell Sharp Sword");
            w.Meta("/mt vendor addsell Faran Robe");
            w.Meta("/mt vendor sell");
            Check.Eq(FakeHost.VendorSells.Count, 1, "one sale");
            Check.True(FakeHost.VendorSells[0].Items.SequenceEqual(new[] { Robe }), "of the robe only");
        }
        finally { w.Dispose(); }
    }
}
