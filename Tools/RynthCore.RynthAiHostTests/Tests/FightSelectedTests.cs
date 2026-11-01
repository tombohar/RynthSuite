using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// "Fight the monster I select" (2026-11-01, CombatManager.SelectedTarget.cs): a monster the
// player selects in a fight is fought first, until it dies, can't be reached, or the player
// selects another monster / deselects. The bot's own selections (ItemInfo.SelfSelect) never count.
internal static class FightSelectedTests
{
    private const uint Player = 0x50000F21;
    private const uint Lb = 0xA9B40000;
    private const uint A = 0x80000A01, B = 0x80000A02, Boss = 0x80000A03, Far = 0x80000A04,
                       Npc = 0x80000A05, Corpse = 0x80000A06, Skip = 0x80000A07, Never = 0x80000A08, Pk = 0x80000A09;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("fight selected: off (the default) changes nothing", OffNoChange);
        r.Add("fight selected: the player's selection of a lower-priority monster wins; another selection moves it, a deselect ends it", PlayerSelectionWins);
        r.Add("fight selected: the bot's own selections never count (native attack, looter, meta)", BotSelectionNeverCounts);
        r.Add("fight selected: an NPC, a corpse, a player, yourself, an ignored or never-attack monster change nothing", NonMonstersNothing);
        r.Add("fight selected: the chosen monster dying returns to normal priority, quietly", DeathReturnsToNormal);
        r.Add("fight selected: past Disengage Range it's said once, fought if it comes close, dropped after 30 s", OutOfRange);
        r.Add("fight selected: a /ra targetlock lock is never moved; /ra targetlock on the selected one makes it explicit", TargetLockInteraction);
        r.Add("fight selected: the setting off releases it; /ra fightselected, the settings face, profiles, metas", SettingPlumbing);
        r.Add("fight selected: /ra why says it is fighting the monster you selected", WhySaysSo);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly CombatManager Combat;
        public readonly LegacyUiSettings S;
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;
        private DateTime _now = DateTime.Now;

        public Rig(bool on = true)
        {
            FakeHost.Reset();
            SelfSelect.Reset();
            Place(Player, "Me", 96, 96, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            Place(A, "Mob A", 96, 98);
            Place(B, "Mob B", 96, 102);
            Place(Boss, "Boss", 96, 100);
            Place(Far, "Far Mob", 96, 136);       // 40 m: past Disengage Range (20 + 3)
            Place(Skip, "Harmless Bug", 96, 99);
            Place(Never, "Never Mob", 96, 99);
            Place(Pk, "Someone", 96, 99);
            FakeHost.Wcids[Pk] = 1;               // a player's weenie, classed as a monster by the cache
            Place(Npc, "Town Crier", 96, 97);
            FakeHost.NotAttackable.Add(Npc);
            Place(Corpse, "Corpse of Mob A", 96, 97, creature: false);
            FakeHost.ItemTypes[Corpse] = 0x200;
            FakeHost.Bitfields[Corpse] = 0x00002000;
            FakeHost.NotAttackable.Add(Corpse);
            S = new LegacyUiSettings
            {
                MonsterRange = 20, TargetSelection = 1 /* closest */, TargetLock = true,
                IsMacroRunning = true, EnableCombat = true, FightSelectedTarget = on,
            };
            S.MonsterRules.Add(new MonsterRule { Name = "Boss", Priority = 5 });
            S.MonsterRules.Add(new MonsterRule { Name = "Harmless Bug", Priority = 0 });
            var host = FakeHost.Create();
            Host = host;
            Cache = FakeHost.MakeCache(host, Player, new[] { A, B, Boss, Far, Skip, Never, Pk, Npc, Corpse });
            Combat = new CombatManager(host, S, Cache);
            Combat.SetPlayerId(Player);
            Combat.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            Combat.TargetClock = () => _now;
        }

        public void Advance(double ms) => _now = _now.AddMilliseconds(ms);

        public uint Pick(params (uint Id, double Dist)[] scan)
        {
            Combat.SetScannedTargetsForTests(scan.Select(t => new CombatManager.ScannedTarget
            {
                Id = unchecked((int)t.Id), Distance = t.Dist, Angle = 0, Bearing = double.NaN, Name = FakeHost.Names[t.Id],
            }));
            Combat.HandleCombatTrigger();
            return unchecked((uint)Combat.activeTargetId);
        }

        /// <summary>The player selects <paramref name="id"/> (0 = deselect).</summary>
        public string? Select(uint id) => Combat.OnSelectionChanged(id, SelfSelect.IsOurs(id));
        public int Forced => Combat.ForcedTargetId;
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static int I(uint id) => unchecked((int)id);

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void OffNoChange()
    {
        Check.False(new LegacyUiSettings().FightSelectedTarget, "off by default");
        var t = new Rig(on: false);
        Check.Eq(t.Pick((A, 2), (B, 6)), A, "closest: A");
        Check.Eq(t.Select(B), null, "selecting B: nothing");
        Check.Eq(t.Forced, 0, "no lock");
        Check.Eq(t.Pick((A, 2), (B, 6)), A, "still A");
        t.Select(0);
        Check.Eq(t.Pick((A, 2), (B, 6)), A, "a deselect: still A");
    }

    private static void PlayerSelectionWins()
    {
        var t = new Rig();
        Check.Eq(t.Pick((A, 2), (B, 6), (Boss, 4)), Boss, "normal: the Boss's priority wins");
        Check.Eq(t.Select(B), "locked", "the player selects B");
        Check.Eq(t.Pick((A, 2), (B, 6), (Boss, 4)), B, "B (priority 1, farther) over the Boss and the closer A");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] switch") && l.Contains("fight the monster I select: selected by the player")),
            "the switch line says why");
        Check.Eq(t.Pick((A, 1), (B, 6), (Boss, 3)), B, "kept while the others come closer");

        Check.Eq(t.Select(A), "locked", "the player selects A");
        Check.Eq(t.Pick((A, 2), (B, 6), (Boss, 4)), A, "the lock moves to A");
        Check.True(FakeHost.Logs.Any(l => l.Contains("(the player selected another monster,")), "the switch says so");

        Check.Eq(t.Select(0), "deselected", "the player deselects");
        Check.Eq(t.Forced, 0, "the lock is gone");
        Check.Eq(t.Pick((A, 2), (B, 6), (Boss, 4)), Boss, "normal priority again: the Boss");
        Check.True(t.Combat.ForcedBySelection == false, "and nothing forced");
        Check.False(FakeHost.Chat.Any(c => c.Contains("Stopped fighting")), "no chat for a selection or a deselect");

        // Only while the macro runs with Combat on.
        t = new Rig();
        t.S.IsMacroRunning = false;
        Check.Eq(t.Select(B), null, "macro off: a selection isn't followed");
        t.S.IsMacroRunning = true;
        t.S.EnableCombat = false;
        Check.Eq(t.Select(B), null, "Combat off: not followed either");
    }

    private static void BotSelectionNeverCounts()
    {
        var t = new Rig();
        var bot = new Bot(t);
        long clock = 1_000_000;
        SelfSelect.Clock = () => clock;
        try
        {
            Check.Eq(t.Pick((A, 2), (B, 6)), A, "fighting A");

            // Combat's native attack (SelectAsBot), the looter, a meta: all marked before the select.
            SelfSelect.SelectAsBot(t.Host, A);
            bot.P.OnSelectedTargetChange(A, 0);
            Check.Eq(t.Forced, 0, "the bot selecting its own target: not the player's");
            SelfSelect.Mark(B);   // e.g. a meta's selectitem
            bot.P.OnSelectedTargetChange(B, A);
            Check.Eq(t.Forced, 0, "the bot selecting another monster: not the player's");
            Check.Eq(t.Pick((A, 2), (B, 6)), A, "still A");

            // Several marks in a row: the earlier ones are still the bot's (not just the last).
            SelfSelect.Mark(Boss);
            bot.P.OnSelectedTargetChange(A, Boss);
            Check.Eq(t.Forced, 0, "an earlier mark (A) is still ours after a later one (Boss)");

            // The same monster selected by the player once the mark has expired.
            clock += SelfSelect.WindowMs + 1;
            bot.P.OnSelectedTargetChange(B, A);
            Check.Eq(t.Forced, I(B), "the player's click on B (no fresh mark): followed");
            Check.True(t.Combat.ForcedBySelection, "as a selection lock");

            // The bot re-selecting its old target while the player's pick is pending changes nothing.
            SelfSelect.SelectAsBot(t.Host, A);
            bot.P.OnSelectedTargetChange(A, B);
            Check.Eq(t.Forced, I(B), "the bot's native-attack re-select of A doesn't undo the player's B");
            Check.Eq(t.Pick((A, 2), (B, 6)), B, "B is fought");
        }
        finally
        {
            SelfSelect.Clock = () => Environment.TickCount64;
            SelfSelect.Reset();
        }
    }

    private static void NonMonstersNothing()
    {
        var t = new Rig();
        t.S.MonsterNameBlacklist.Add("Never Mob");
        Check.True(t.Select(Npc)?.Contains("can't be attacked") == true, "an NPC: not followed");
        Check.True(t.Select(Corpse)?.Contains("isn't a monster") == true, "a corpse: not followed");
        Check.Eq(t.Select(Player), "that's you", "yourself: not followed");
        Check.True(t.Select(Pk)?.Contains("is a player") == true, "a player (by weenie): not followed");
        Check.True(t.Select(Skip)?.Contains("Priority 0") == true, "an ignored (Priority 0) monster: not followed");
        Check.True(t.Select(Never)?.Contains("never-attack") == true, "a never-attack monster: not followed");
        Check.Eq(t.Forced, 0, "no lock from any of them");

        // With a selected monster being fought, those selections leave it alone.
        t.Select(B);
        Check.Eq(t.Pick((A, 2), (B, 6)), B, "fighting the selected B");
        foreach (uint id in new[] { Npc, Corpse, Player, Pk, Skip, Never })
            t.Select(id);
        Check.Eq(t.Forced, I(B), "B is still the selected target");
        Check.Eq(t.Pick((A, 2), (B, 6)), B, "and still fought");
        Check.False(FakeHost.Logs.Any(l => l.Contains("fight-selected") && l.Contains("Corpse of")), "a corpse click isn't even logged");
    }

    private static void DeathReturnsToNormal()
    {
        var t = new Rig();
        t.Select(B);
        Check.Eq(t.Pick((A, 2), (B, 6), (Boss, 4)), B, "fighting the selected B");
        t.Cache.OnUpdateHealth(B, 0f);
        Check.Eq(t.Pick((A, 2), (Boss, 4)), Boss, "B died: normal priority, the Boss");
        Check.Eq(t.Forced, 0, "the lock is over");
        Check.True(FakeHost.Logs.Any(l => l.Contains("fight-selected") && l.Contains("ended: it died")), "logged");
        Check.False(FakeHost.Chat.Any(c => c.Contains("Stopped fighting")), "no chat for the normal end");

        // Its kill notice ends it too.
        t = new Rig();
        t.Select(Boss);
        t.Pick((A, 2), (Boss, 4));
        t.Combat.OnKillNotification("You killed Boss!");
        Check.Eq(t.Pick((A, 2), (B, 6)), A, "killed: the closest again");
        Check.Eq(t.Forced, 0, "and the lock is over");
    }

    private static void OutOfRange()
    {
        var t = new Rig();
        Check.Eq(t.Select(Far), "far", "40 m away: past the 23 m reach");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Far Mob is 40 yd away, past Disengage Range (23 yd)") && c.Contains("not chasing")),
            "said in chat: " + string.Join(" | ", FakeHost.Chat));
        int lines = FakeHost.Chat.Count;
        t.Select(0);
        Check.Eq(t.Select(Far), "far", "selected again");
        Check.Eq(FakeHost.Chat.Count, lines, "said once per monster");

        Check.Eq(t.Pick((A, 2)), A, "not in the scan (it is never chased): A meanwhile");
        t.Advance(10_000);
        Check.Eq(t.Pick((A, 2), (Far, 15)), Far, "it comes within range: fought");

        // Never comes close: dropped 30 s after the click, with one line.
        t = new Rig();
        t.Select(Far);
        t.Advance(CombatManager.ForcedTargetLostMs - 1000);
        Check.Eq(t.Pick((A, 2)), A, "29 s: A, the lock waits");
        Check.Eq(t.Forced, I(Far), "still waiting");
        t.Advance(2000);
        Check.Eq(t.Pick((A, 2)), A, "31 s");
        Check.Eq(t.Forced, 0, "dropped");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Stopped fighting the monster you selected (Far Mob): never in reach")),
            "and says why: " + string.Join(" | ", FakeHost.Chat));

        // Selected, deselected and selected again: the 30 s restart from the new click.
        t = new Rig();
        t.Select(Far);
        t.Pick((A, 2));
        t.Advance(25_000);
        t.Select(0);
        t.Pick((A, 2));
        t.Select(Far);
        t.Advance(10_000);
        t.Pick((A, 2));
        Check.Eq(t.Forced, I(Far), "35 s after the first click, 10 s after the second: still waiting");
    }

    private static void TargetLockInteraction()
    {
        // A /ra targetlock lock is never moved by a selection.
        var t = new Rig();
        t.Combat.SetForcedTarget(I(B));
        Check.Eq(t.Select(A), null, "selecting A under a /ra targetlock lock: nothing");
        Check.Eq(t.Select(0), null, "nor a deselect");
        Check.Eq(t.Forced, I(B), "the lock stays on B");
        Check.False(t.Combat.ForcedBySelection, "as the explicit lock");
        Check.True(FakeHost.Logs.All(l => !l.Contains("fight-selected")), "and nothing logged for it");

        // The commands.
        t = new Rig();
        var bot = new Bot(t);
        FakeHost.SelectedItem = A;
        bot.P.OnSelectedTargetChange(A, 0);
        Check.True(t.Combat.ForcedBySelection && t.Forced == I(A), "the player selects A: fought by the setting");
        bot.Ra("/ra targetlock");
        Check.Eq(t.Forced, I(A), "/ra targetlock on it: still A (not released)");
        Check.False(t.Combat.ForcedBySelection, "now the explicit lock");
        Check.True(bot.LastChat.Contains("no longer moves it"), "chat: " + bot.LastChat);
        FakeHost.SelectedItem = B;
        bot.P.OnSelectedTargetChange(B, A);
        Check.Eq(t.Forced, I(A), "selecting B doesn't move the explicit lock");
        bot.Ra("/ra targetlock off");
        Check.Eq(t.Forced, 0, "/ra targetlock off releases it");

        // /ra targetlock off also ends a selection lock.
        bot.P.OnSelectedTargetChange(A, B);
        bot.P.OnSelectedTargetChange(B, A);
        Check.True(t.Combat.ForcedBySelection && t.Forced == I(B), "selection lock on B");
        bot.Ra("/ra targetlock off");
        Check.Eq(t.Forced, 0, "/ra targetlock off ends the selection lock too");

        // The Target Lock setting (keep the incumbent) is a different thing: it doesn't stop the switch.
        t = new Rig();
        t.S.TargetLock = true;
        Check.Eq(t.Pick((A, 2), (B, 6)), A, "Target Lock (setting) holds A");
        t.Select(B);
        Check.Eq(t.Pick((A, 2), (B, 6)), B, "the player's selection still switches to B");
        t.S.TargetLock = false;
        t.Select(A);
        Check.Eq(t.Pick((A, 2), (B, 6)), A, "Target Lock off: the same");
    }

    private static void SettingPlumbing()
    {
        // Turned off mid-fight: released on the next tick, quietly.
        var t = new Rig();
        t.Select(B);
        Check.Eq(t.Pick((A, 2), (B, 6), (Boss, 4)), B, "fighting the selected B");
        t.S.FightSelectedTarget = false;
        Check.Eq(t.Pick((A, 2), (B, 6), (Boss, 4)), Boss, "the setting off: normal priority");
        Check.Eq(t.Forced, 0, "released");
        Check.False(FakeHost.Chat.Any(c => c.Contains("Stopped fighting")), "no chat line");

        // /ra fightselected [on|off] (toggle without an argument); every reply says the state.
        t = new Rig(on: false);
        var bot = new Bot(t);
        bot.Ra("/ra fightselected");
        Check.True(bot.S.FightSelectedTarget, "toggle: on");
        Check.True(bot.LastChat.Contains("Fight the monster I select: on"), bot.LastChat);
        bot.Ra("/ra fightselected on");
        Check.True(bot.S.FightSelectedTarget, "on: on");
        bot.P.OnSelectedTargetChange(B, 0);
        Check.True(t.Combat.ForcedBySelection, "selection followed");
        bot.Ra("/ra fightselected off");
        Check.False(bot.S.FightSelectedTarget, "off");
        Check.Eq(t.Forced, 0, "off releases the selection lock at once");
        Check.True(bot.LastChat.Contains("Fight the monster I select: off"), bot.LastChat);
        bot.Ra("/ra fightselected banana");
        Check.True(bot.LastChat.Contains("Usage: /ra fightselected [on|off]"), bot.LastChat);
        Check.False(bot.S.FightSelectedTarget, "an unknown argument changes nothing");

        // Profiles: an old profile loads it off; a profile's value is copied.
        var loaded = System.Text.Json.JsonSerializer.Deserialize("{\"MonsterRange\":7}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(loaded != null && !loaded.FightSelectedTarget, "a profile saved before the setting loads it off");
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { FightSelectedTarget = true }, dst });
        Check.True(dst.FightSelectedTarget, "a profile's value is copied");
        string json = System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings { FightSelectedTarget = true }, RynthAiJsonContext.Default.LegacyUiSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(json.Contains("FightSelectedTarget") && back != null && back.FightSelectedTarget, "saved with the profile");

        // The engine Settings face: sent; a save without the field leaves it alone.
        var s = new LegacyUiSettings { FightSelectedTarget = true };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", Any)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = System.Text.Json.JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.FightSelectedTarget == true, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.FightSelectedTarget, "an engine without the field leaves it alone");
        dash.ApplySettingsJson("{\"FightSelectedTarget\":false}");
        Check.False(s.FightSelectedTarget, "a save with it sets it");

        // Metas: rasetsetting / ragetsetting.
        var e = ExpressionTests.Engine(settings: s);
        Check.Eq(e.Evaluate("rasetsetting[FightSelectedTarget, 1]"), "1", "meta sets it");
        Check.True(s.FightSelectedTarget, "on");
        Check.Eq(e.Evaluate("ragetsetting[FightSelectedTarget]"), "1", "meta reads it");
    }

    private static void WhySaysSo()
    {
        var t = new Rig();
        var bot = new Bot(t);
        bot.P.OnSelectedTargetChange(B, 0);
        t.Pick((A, 2), (B, 6));
        FakeHost.Chat.Clear();
        bot.Ra("/ra why");
        Check.True(bot.AllChat.Contains("fighting the monster you selected: Mob B"), "the target line: " + bot.AllChat);
        Check.True(bot.AllChat.Contains("likely: fighting the monster you selected"), "the interpretation: " + bot.AllChat);
    }

    // ── Plugin harness ───────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S;

        public Bot(Rig t)
        {
            S = t.S;
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Set("_objectCache", t.Cache);
            Set("_playerId", Player);
            Set("_combatManager", t.Combat);
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            object[] args = { parts[1].ToLowerInvariant(), parts, 0 };
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(P, args);
        }
        public string LastChat => FakeHost.Chat.Count > 0 ? FakeHost.Chat[^1] : "";
        public string AllChat => string.Join(" | ", FakeHost.Chat);
    }
}
