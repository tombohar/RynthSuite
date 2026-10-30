using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using RynthCore.Plugin.RynthAi;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-04, Tom: "since the start of time... when you hit reload or just log in, before the
// bot is on, it will randomly start opening and closing doors or corpses." The cause found was
// the engine's logo-bypass clicking into the in-world client on a hot reload (fixed in the
// engine, LogoBypassHooks). These tests pin the plugin side:
//  - with the macro off, a door and a corpse right next to the player, ticks straight after
//    login send no use at all (doors, looting, the full OnTick);
//  - UseAudit refuses an automatic door/corpse use while the macro is off and logs it, lets
//    what the player asked for through, and logs every use once (repeats counted);
//  - a door use is not judged "didn't open" before the door could swing.
internal static class UseAtLoginTests
{
    private const uint Player = 0x50000E01;
    private const uint Door = 0x7860202C;
    private const uint Corpse = 0x80262100;
    private const uint Potion = 0x80262200;
    private const uint ItemTypeContainer = 0x200;
    private const uint BfDoor = 0x1000;
    private const uint LongDescKey = 16;
    private const string Me = "Usehunt Tester";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("use at login: macro off, door and corpse next to you, doors + looting ticks send no use", MacroOffNoUseFromControllers);
        r.Add("use at login: macro off, full OnTick right after login sends no use", MacroOffFullTickNoUse);
        r.Add("use audit: macro off, an automatic door or corpse use is BLOCKED and logged", AuditBlocksAutoDoorCorpse);
        r.Add("use audit: what the player asked for goes through with the macro off, and is logged", AuditLetsAskedThrough);
        r.Add("use audit: macro on, the use goes through with one line saying who, what and why", AuditLogsAutoUseMacroOn);
        r.Add("use audit: the same use repeated is logged once, the repeats counted on the next line", AuditThrottlesRepeats);
        r.Add("doors: a requested door isn't judged 'not open' before it could swing (no early cooldown)", DoorWaitsBeforeJudging);
    }

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;   // installs UseObject
        FakeHost.WeaponCalls = true;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Door] = "Door";
        FakeHost.Bitfields[Door] = BfDoor;
        FakeHost.NotAttackable.Add(Door);
        FakeHost.Positions[Door] = (Me0.Cell, Me0.X + 1.5f, Me0.Y, Me0.Z);
        FakeHost.Names[Corpse] = "Corpse of Drudge Skulker";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X, Me0.Y + 1.0f, Me0.Z);
        FakeHost.Names[Potion] = "Healing Potion";
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = false,
            EnableLooting = true,
            EnableCombat = false,
            EnableNavigation = true,
            OpenDoors = true,
            OpenDoorRange = 10,
            LootOwnership = 0,
            LootContentSettleMs = 0,
            LootEmptyCorpseMs = 0,
            StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            // The full OnTick reaches the dashboard's queues and lists: give every empty
            // collection/lock field a fresh instance (the constructor builds ImGui panels and
            // lists C:\Games folders, which a test must not need).
            foreach (var f in typeof(LegacyDashboardRenderer).GetFields(Any))
            {
                if (f.GetValue(dash) != null || f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
                bool fillable = f.FieldType == typeof(object)
                    || (f.FieldType.Namespace?.StartsWith("System.Collections") == true && !f.FieldType.IsInterface && !f.FieldType.IsAbstract);
                if (fillable && f.FieldType.GetConstructor(Type.EmptyTypes) != null)
                    f.SetValue(dash, Activator.CreateInstance(f.FieldType));
            }
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Cache.OnCreateObject(Door);
            Cache.OnCreateObject(Corpse);
            Cache.Tick();
            // What RynthAiPlugin.Initialize does.
            UseAudit.Reset("RynthAi", () => S.IsMacroRunning);
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
    }

    /// <summary>Back to no guard, so the other tests (which never Initialize) aren't affected.</summary>
    private static void Done() => UseAudit.Reset("?", null);

    // ── Macro off, straight after login ───────────────────────────────────────

    private static void MacroOffNoUseFromControllers()
    {
        try
        {
            World();
            var host = FakeHost.Create(Player);
            var bot = new Bot(host);
            Check.Eq(bot.Cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "the corpse is a Corpse");
            Check.True(bot.Cache.GetLandscapeObjects().Any(o => o.Id == unchecked((int)Door)), "the door is on the landscape");

            // A door the nav detour asked for before the macro went off (or a reload).
            bot.Set("_doorRequestedId", unchecked((int)Door));
            for (int i = 0; i < 30; i++)
            {
                bot.Set("_activity", BotActivity.Navigating);
                bot.Call("TickCorpseOpening");
                bot.Call("TickDoorInteraction");
            }
            Check.Eq(FakeHost.Uses.Count, 0, "no use with the macro off (uses: " + string.Join(",", FakeHost.Uses.Select(u => u.ToString("X8"))) + ")");
            Check.Eq(bot.Get<int>("_doorRequestedId"), 0, "the stale door request is dropped");
            Check.Eq(bot.Get<int>("_targetCorpseId"), 0, "no corpse claimed");

            // Sanity: the same world with the macro on does open things (the test can see uses).
            bot.S.IsMacroRunning = true;
            for (int i = 0; i < 5 && FakeHost.Uses.Count == 0; i++)
            {
                bot.Call("TickCorpseOpening");
                bot.Call("TickDoorInteraction");
            }
            Check.True(FakeHost.Uses.Count > 0, "with the macro on the door or the corpse is used");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Use] RynthAi/") && l.Contains("macro=on auto")), "and the use is logged");
        }
        finally { Done(); }
    }

    private static void MacroOffFullTickNoUse()
    {
        try
        {
            World();
            var host = FakeHost.Create(Player);
            var bot = new Bot(host);
            bot.Set("_loginComplete", true);
            bot.Set("_loginCompletedAt", DateTime.Now.AddSeconds(-10));   // past the inventory settle window
            bot.Set("_doorRequestedId", unchecked((int)Door));

            for (int i = 0; i < 40; i++)
            {
                bot.P.OnTick();
                Thread.Sleep(2);
            }
            Check.Eq(FakeHost.Uses.Count, 0, "no use in 40 ticks with the macro off (uses: " + string.Join(",", FakeHost.Uses.Select(u => u.ToString("X8"))) + ")");
            Check.True(FakeHost.Logs.Any(l => l.Contains("OnTick: settings ok, macro=False")), "OnTick got past login and the settings to the bot's work");
            Check.False(FakeHost.Logs.Any(l => l.Contains("OnTick exception")),
                "OnTick ran through (an exception would make this test pass for nothing): " + FakeHost.Logs.FirstOrDefault(l => l.Contains("OnTick exception")));
        }
        finally { Done(); }
    }

    // ── UseAudit ─────────────────────────────────────────────────────────────

    private static void AuditBlocksAutoDoorCorpse()
    {
        try
        {
            World();
            var host = FakeHost.Create(Player);
            bool macro = false;
            UseAudit.Reset("RynthAi", () => macro);

            Check.False(host.UseFor(Door, "Door", "test door"), "door use refused");
            Check.False(host.UseFor(Corpse, "Loot", "test corpse"), "corpse use refused (known by its name)");
            Check.Eq(FakeHost.Uses.Count, 0, "nothing sent to the engine");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Use] RynthAi/Door BLOCKED 0x7860202C 'Door' (door) macro=off auto - test door")),
                "door BLOCKED line: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[Use]"))));
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Use] RynthAi/Loot BLOCKED") && l.Contains("(corpse)")), "corpse BLOCKED line");

            Check.True(host.UseFor(Potion, "Buff", "drink"), "a potion (not a door or corpse) is used with the macro off");
            Check.True(FakeHost.Uses.Contains(Potion), "sent");
        }
        finally { Done(); }
    }

    private static void AuditLetsAskedThrough()
    {
        try
        {
            World();
            var host = FakeHost.Create(Player);
            UseAudit.Reset("RynthAi", () => false);
            Check.True(host.UseFor(Door, "Command", "/ra use Door", UseKind.Asked), "a typed command opens the door");
            Check.True(FakeHost.Uses.Contains(Door), "sent");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Use] RynthAi/Command 0x7860202C 'Door' (door) macro=off asked - /ra use Door")),
                "asked line: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[Use]"))));

            // A plugin without a macro (RynthNav, RynthLua) never blocks.
            UseAudit.Reset("RynthLua", null);
            Check.True(host.UseFor(Corpse, "Script", "x.lua: ObjectUse"), "no macro: no guard");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Use] RynthLua/Script") && l.Contains("macro=n/a")), "macro=n/a");
        }
        finally { Done(); }
    }

    private static void AuditLogsAutoUseMacroOn()
    {
        try
        {
            World();
            var host = FakeHost.Create(Player);
            UseAudit.Reset("RynthAi", () => true);
            Check.True(host.UseFor(Corpse, "Loot", "open the claimed corpse"), "used");
            Check.True(FakeHost.Logs.Any(l => l == "[Use] RynthAi/Loot 0x80262100 'Corpse of Drudge Skulker' (corpse) macro=on auto - open the claimed corpse"),
                "line: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[Use]"))));
        }
        finally { Done(); }
    }

    private static void AuditThrottlesRepeats()
    {
        long now = 1_000_000;
        try
        {
            World();
            var host = FakeHost.Create(Player);
            UseAudit.Reset("RynthAi", () => true);
            UseAudit.NowMs = () => now;
            for (int i = 0; i < 10; i++) { host.UseFor(Potion, "Buff", "drink"); now += 100; }
            Check.Eq(FakeHost.Uses.Count, 10, "every use is sent");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Use] RynthAi/Buff")), 1, "one line for the burst");
            now += 6000;
            host.UseFor(Potion, "Buff", "drink");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Use] RynthAi/Buff") && l.Contains("(+9 repeat(s) since its last line)")),
                "the next line counts the 9 repeats: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[Use]"))));

            // Many different objects: at most 20 lines in 10 s, then one line saying how many were left out.
            now += 11_000;   // a fresh window
            FakeHost.Logs.Clear();
            for (uint i = 0; i < 30; i++) host.UseFor(0x80300000u + i, "Combat", "swap");
            Check.Eq(FakeHost.Logs.Count(l => l.StartsWith("[Use] RynthAi/Combat")), 20, "capped at 20 lines in the window");
            now += 11_000;
            host.UseFor(0x80310000u, "Combat", "swap");
            Check.True(FakeHost.Logs.Any(l => l.Contains("10 more use line(s) left out")), "the dropped ones are counted");
        }
        finally { UseAudit.NowMs = () => Environment.TickCount64; Done(); }
    }

    // ── Doors ────────────────────────────────────────────────────────────────

    private static void DoorWaitsBeforeJudging()
    {
        try
        {
            World();
            var host = FakeHost.Create(Player);
            var bot = new Bot(host);
            bot.S.IsMacroRunning = true;
            bot.S.OpenDoors = false;          // only the requested door
            bot.Set("_doorRequestedId", unchecked((int)Door));
            long now = 70_000_000;
            RynthAiPlugin.DoorClock = () => now;

            // The targeting tick stops the character; the use goes on the next one (2026-10-13:
            // it waits for its turn - ActionPacer, the client's busy count).
            bot.Call("TickDoorInteraction");
            bot.Call("TickDoorInteraction");
            Check.Eq(FakeHost.Uses.Count(u => u == Door), 1, "the requested door is used once");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[Use] RynthAi/Door") && l.Contains("nav detour")), "logged with the reason");

            // The fake never reports the door open. Right after the use: still waiting.
            for (int i = 0; i < 5; i++) bot.Call("TickDoorInteraction");
            Check.False(FakeHost.Logs.Any(l => l.Contains("not open")), "not judged 'not open' within ms of the use");
            Check.Eq(FakeHost.Uses.Count(u => u == Door), 1, "and not used again (a second use would close it)");

            // No UseDone count on this fake engine: the use counts as answered after 600 ms, then
            // the door gets its time to swing.
            now += 700;
            bot.Call("TickDoorInteraction");
            Check.False(FakeHost.Logs.Any(l => l.Contains("not open")), "not judged right at the answer");
            now += 1300;
            bot.Call("TickDoorInteraction");
            Check.True(FakeHost.Logs.Any(l => l.Contains("not open")), "judged once the door had time to swing");
            Check.False(FakeHost.Logs.Any(l => l.Contains("is locked")), "and not called locked (the server never said so)");
        }
        finally { RynthAiPlugin.DoorClock = static () => Environment.TickCount64; Done(); }
    }
}
