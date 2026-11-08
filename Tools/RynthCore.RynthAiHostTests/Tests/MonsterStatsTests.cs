using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.MonsterStats;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Monster stats (2026-10-30, a player's request: "combat statistics under individual monsters,
// evades/resists for both the player and the monster"). RynthAi counts, per kind of monster since
// login: your swings, hits and its evades (the typed combat events), your spells that reached it
// and its resists (the spell lines), kills; and the same the other way. They show under each monster
// in the Damage panel (the row's "mstats"; RynthCore's DamageRowStatsTests reads the same fields),
// and in RynthAi's own window only on an engine without that. Checked here: every line of the one
// table and the near-misses that must not count, the typed events, the counters and rates, reset,
// two monsters kept apart, the save across a hot reload, the plugin's wiring (no player's chat, no
// item's spell), their lifetime (Reset XP leaves them), the Damage rows' "mstats", the engine flag
// (tab or fallback window), /ra mstats and the window in the manifest.
internal static class MonsterStatsTests
{
    public static void Register(Runner r)
    {
        r.Add("monster stats: every spell line of the table reads - whose spell, what became of it, the monster, the damage", SpellLinesRead);
        r.Add("monster stats: near-misses don't read (own buffs, DoT ticks, heals, infusions, cloaks, the client's melee lines, a fizzle)", NearMissesDontRead);
        r.Add("monster stats: the four typed combat events read their monster and damage; short bodies and other events don't", TypedEventsRead);
        r.Add("monster stats: counters and rates (swings, spells, the killing blow, damage, percentages)", CountersAndRates);
        r.Add("monster stats: reset starts every monster over", ResetClears);
        r.Add("monster stats: two monsters are kept apart; one name in any case is one monster; found by part of a name", TwoMonstersApart);
        r.Add("monster stats: the save round-trips and is restored only for the same process, character and a recent save", SaveRestoreRules);
        r.Add("monster stats: the plugin counts typed events and spell lines, not a player's chat or an item's spell", PluginWiring);
        r.Add("monster stats: login to logout - Reset XP leaves them, a hot reload carries them on, the resets and logout clear them", Lifetime);
        r.Add("monster stats: each Damage panel row carries its monster's numbers as \"mstats\" (the engine's field names)", DamageRowsCarryStats);
        r.Add("monster stats: an engine that draws them in the Damage panel gets no window or button; an older one gets both", EngineFlag);
        r.Add("monster stats: /ra mstats prints a monster's numbers, lists, resets, and says why not with the usage", Command);
        r.Add("monster stats: the Monster Stats window is in RynthAi's manifest and passes the engine's parser", ManifestWindow);
        r.Add("monster stats: the window's view is published at login and while the window is on screen", WindowView);
        r.Add("kill XP (11-08): a rise after the kill, a rise before it, a quest's rise ignored, two kills two rises", XpMatcher);
        r.Add("kill XP (11-08): the session rows count XP and XP kills; the save keeps them and reads an older save", XpInRowsAndSave);
        r.Add("kill XP (11-08): the learned table averages the last kills and round-trips through its file", XpTableRoundTrip);
        r.Add("kill XP (11-08): through the plugin - kill notice + TotalExperience rise -> the row's xp/xk, /ra mstats, the learned table", XpPluginWiring);
        r.Add("death time (11-08): a kill's corpse is timed; another monster's, a late one, or another object isn't", CorpseTimerPairs);
        r.Add("death time (11-08): rows carry it (cd, cn, 'corpse after 2.1 s'); the save keeps it and reads older saves; the table keeps it", CorpseTimeInRowsAndTable);
    }

    // ── Death animation (2026-11-08) ─────────────────────────────────────

    private static void CorpseTimerPairs()
    {
        Check.Eq(CorpseTimer.KilledName("Corpse of Drudge Skulker"), "Drudge Skulker", "the name in a corpse's");
        Check.Null(CorpseTimer.KilledName("Drudge Skulker"), "not a corpse");
        Check.Null(CorpseTimer.KilledName("Corpse of "), "no name");
        var t = new CorpseTimer();
        t.OnKill("Drudge Skulker", 1000);
        Check.Null(t.OnCorpse("Corpse of Mosswart Worshipper", 2000), "another monster's corpse");
        Check.Null(t.OnCorpse("Black Opal", 2000), "not a corpse");
        Check.True(t.OnCorpse("Corpse of drudge skulker", 3100) is ("Drudge Skulker", 2100), "the kill's corpse 2.1 s later (any case)");
        Check.Eq(t.Waiting, 0, "and the kill is done");
        t.OnKill("Drudge Skulker", 10_000);
        t.OnKill("Drudge Skulker", 10_200);   // a ring: two of them
        Check.True(t.OnCorpse("Corpse of Drudge Skulker", 12_000) is (_, 2000), "the first corpse to the first kill");
        Check.True(t.OnCorpse("Corpse of Drudge Skulker", 12_300) is (_, 2100), "the second to the second");
        t.OnKill("Drudge Skulker", 20_000);
        Check.Null(t.OnCorpse("Corpse of Drudge Skulker", 20_000 + t.WindowMs + 1), "past the window: not its death");
        Check.Eq(t.Waiting, 0, "dropped");
        t.OnKill(" ", 1);
        Check.Eq(t.Waiting, 0, "no name: nothing waits");
    }

    private static void CorpseTimeInRowsAndTable()
    {
        var s = new MonsterCombatStats();
        s.OnKill("You killed Drudge Skulker!", KillBlow.Physical, T0, _ => "Drudge Skulker");
        s.OnCorpseTime("Drudge Skulker", 2000, T0);
        s.OnCorpseTime("Drudge Skulker", 2200, T0);
        MonsterStatsRow r = s.Get("Drudge Skulker")!;
        Check.Eq((r.CorpseMs, r.CorpseKills), (4200L, 2), "summed and counted");
        Check.Near(r.CorpseAvgMs ?? 0, 2100, 1e-9, "2.1 s on average");
        string json = RynthAiPlugin.MonsterStatsJson(r);
        Check.True(json.Contains("\"cd\":2100") && json.Contains("\"cn\":2"), "mstats has cd (ms) and cn: " + json);
        Check.True(MonsterStatsText.ChatLines(r)[0].Contains("corpse after 2.1 s"), "/ra mstats: " + MonsterStatsText.ChatLines(r)[0]);
        Check.True(MonsterStatsText.ListLine(r).Contains("corpse after 2.1 s"), "the list line");
        Check.True(MonsterStatsText.DetailRows(r).Any(d => d.Label == "Death" && d.Mine.Contains("corpse after 2.1 s")), "the window's detail");

        var save = new MonsterStatsSave { ProcessId = 1, ProcessStartUtcTicks = 2, CharacterId = 3, SavedUtcTicks = T0.Ticks, SinceUtcTicks = T0.Ticks, Rows = s.Snapshot().ToList() };
        string text = MonsterStatsStore.Serialize(save);
        Check.True(MonsterStatsStore.TryParse(text, out MonsterStatsSave back) && back.Rows.Single() == r, "the save keeps it");
        string withXpOnly = string.Join("\n", text.Split('\n').Select(l => l.StartsWith("m=") ? string.Join("\t", l.Split('\t').Take(18)) : l));
        Check.True(MonsterStatsStore.TryParse(withXpOnly, out MonsterStatsSave mid) && mid.Rows.Single().CorpseKills == 0, "a save with XP but no corpse times reads");

        string dir = TempDir("ms-deathtable");
        try
        {
            var t = new MonsterXpTable();
            t.SetCharacter(dir);
            t.RecordDeath("Drudge Skulker", 2000);
            t.RecordDeath("Drudge Skulker", 2400);
            t.Record("Drudge Skulker", 500);
            Check.Near(t.DeathMs("Drudge Skulker") ?? 0, 2200, 1e-9, "learned death");
            Check.Null(t.DeathMs("Mosswart"), "none known");
            t.SaveIfDirty();
            var next = new MonsterXpTable();
            next.SetCharacter(dir);
            Check.True(next.DeathMs("Drudge Skulker") is double dm && Math.Abs(dm - 2200) < 1 && next.DeathsTimed("Drudge Skulker") == 2
                       && next.XpPerKill("Drudge Skulker") == 500, "both kept across logins in one file");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── Experience per kill (2026-11-08) ─────────────────────────────────

    private static void XpMatcher()
    {
        var m = new KillXpMatcher();
        Check.Eq(m.OnTotal(1_000_000, T0).Count, 0, "the first value read is the starting point");
        Check.Eq(m.OnTotal(1_000_000, T0.AddSeconds(0.1)).Count, 0, "no change");

        // A rise after the kill.
        Check.Eq(m.OnKill("Drudge Skulker", T0.AddSeconds(1)).Count, 0, "a kill waits for its rise");
        var got = m.OnTotal(1_000_500, T0.AddSeconds(1.4));
        Check.True(got.Count == 1 && got[0].Name == "Drudge Skulker" && got[0].Xp == 500, "the rise 0.4 s later is its XP");

        // A rise before the kill notice (the server's order can be either).
        Check.Eq(m.OnTotal(1_001_300, T0.AddSeconds(5)).Count, 0, "a rise waits for a kill");
        got = m.OnKill("Mosswart Worshipper", T0.AddSeconds(5.6));
        Check.True(got.Count == 1 && got[0].Name == "Mosswart Worshipper" && got[0].Xp == 800, "the kill 0.6 s later takes it");

        // A quest's rise: no kill near it.
        Check.Eq(m.OnTotal(1_101_300, T0.AddSeconds(10)).Count, 0, "a quest turn-in");
        Check.Eq(m.OnTotal(1_101_300, T0.AddSeconds(11.5)).Count, 0, "waited past RiseWaitSec");
        Check.Eq(m.WaitingRises, 0, "and dropped");
        Check.Eq(m.OnKill("Drudge Skulker", T0.AddSeconds(12)).Count, 0, "a later kill doesn't take the quest's XP");

        // That kill gave nothing seen: past KillWaitSec it goes, and a later rise isn't its.
        Check.Eq(m.OnTotal(1_101_300, T0.AddSeconds(15.5)).Count, 0, "no rise in KillWaitSec");
        Check.Eq(m.WaitingKills, 0, "the kill stopped waiting");
        Check.Eq(m.OnTotal(1_102_000, T0.AddSeconds(16)).Count, 0, "a rise after that is no kill's");

        // Two kills (a ring spell), two rises.
        m.OnKill("Drudge Skulker", T0.AddSeconds(20));
        m.OnKill("Drudge Ravener", T0.AddSeconds(20.1));
        var a = m.OnTotal(1_102_400, T0.AddSeconds(20.3));
        var b = m.OnTotal(1_103_100, T0.AddSeconds(20.5));
        Check.True(a.Count == 1 && a[0].Name == "Drudge Skulker" && a[0].Xp == 400, "the first rise to the first kill");
        Check.True(b.Count == 1 && b[0].Name == "Drudge Ravener" && b[0].Xp == 700, "the second to the second");

        // A lower total (another character): a new starting point, no rise.
        Check.Eq(m.OnTotal(50, T0.AddSeconds(30)).Count, 0, "lower: starts over");
        m.OnKill("Drudge Skulker", T0.AddSeconds(31));
        Check.True(m.OnTotal(80, T0.AddSeconds(31.2)) is { Count: 1 } r && r[0].Xp == 30, "and counts from there");
        m.Reset();
        Check.Eq(m.OnTotal(999, T0.AddSeconds(40)).Count, 0, "after a reset the first value is the start again");
        Check.Eq(m.OnKill("  ", T0).Count + m.WaitingKills, 0, "no name: nothing waits");
    }

    private static void XpInRowsAndSave()
    {
        var s = new MonsterCombatStats();
        s.OnKill("You killed Drudge Skulker!", KillBlow.Physical, T0, _ => "Drudge Skulker");
        s.OnKill("You killed Drudge Skulker!", KillBlow.Physical, T0);
        s.OnKill("You killed Drudge Skulker!", KillBlow.Physical, T0);
        int v = s.Version;
        s.OnKillXp("Drudge Skulker", 1_000_000, T0);
        s.OnKillXp("Drudge Skulker", 1_469_134, T0);
        s.OnKillXp("Drudge Skulker", 0, T0);   // nothing
        Check.True(s.Version > v, "the version moves");
        MonsterStatsRow r = s.Get("Drudge Skulker")!;
        Check.Eq((r.Kills, r.Xp, r.XpKills), (3, 2_469_134L, 2), "3 kills, 2 with XP seen");
        Check.Near(r.XpPerKill ?? 0, 1_234_567, 1e-9, "XP/kill over the kills with XP seen");
        Check.Null(new MonsterStatsRow("x", 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0).XpPerKill, "none seen: no XP/kill");

        string json = RynthAiPlugin.MonsterStatsJson(r);
        Check.True(json.Contains("\"xp\":2469134") && json.Contains("\"xk\":2"), "mstats has xp and xk: " + json);
        Check.True(MonsterStatsText.ChatLines(r)[0].Contains("XP/kill 1,234,567"), "/ra mstats: " + MonsterStatsText.ChatLines(r)[0]);
        Check.True(MonsterStatsText.ListLine(r).Contains("XP/kill 1,234,567"), "the list line: " + MonsterStatsText.ListLine(r));
        Check.Eq(MonsterStatsText.XpCell(r), "1,234,567", "the window's cell");
        Check.True(MonsterStatsText.DetailRows(r).Any(d => d.Label == "XP" && d.Mine.Contains("XP/kill 1,234,567")), "the window's detail");

        var save = new MonsterStatsSave { ProcessId = 1, ProcessStartUtcTicks = 2, CharacterId = 3, SavedUtcTicks = T0.Ticks, SinceUtcTicks = T0.Ticks, Rows = s.Snapshot().ToList() };
        string text = MonsterStatsStore.Serialize(save);
        Check.True(MonsterStatsStore.TryParse(text, out MonsterStatsSave back) && back.Rows.Single() == r, "the save keeps XP and XP kills");
        // A save from before 11-08: 16 fields a row.
        string older = string.Join("\n", text.Split('\n').Select(l => l.StartsWith("m=") ? string.Join("\t", l.Split('\t').Take(16)) : l));
        Check.True(MonsterStatsStore.TryParse(older, out MonsterStatsSave old) && old.Rows.Single().Kills == 3 && old.Rows.Single().XpKills == 0,
                   "an older save reads, with no XP");
        var loaded = new MonsterCombatStats();
        loaded.Load(back.Rows);
        Check.Eq(loaded.Get("Drudge Skulker")!.Xp, 2_469_134L, "Load keeps the XP");
    }

    private static void XpTableRoundTrip()
    {
        string dir = TempDir("ms-xptable");
        try
        {
            var t = new MonsterXpTable();
            t.Record("Drudge Skulker", 1000);   // no character yet: kept in memory
            t.SaveIfDirty();
            Check.False(Directory.Exists(dir), "no character, no file");
            t.SetCharacter(dir);
            Check.Eq(t.FilePath, Path.Combine(dir, MonsterXpTable.FileName), "next to the character's settings: " + t.FilePath);
            Check.Null(t.XpPerKill("Drudge Skulker"), "a character's table starts from its file (none yet)");
            t.Record("Drudge Skulker", 1000);
            t.Record("Drudge Skulker", 2000);
            Check.Near(t.XpPerKill("drudge skulker") ?? 0, 1500, 1e-9, "the first kills count fully (any case)");
            for (int i = 0; i < 200; i++) t.Record("Olthoi Slayer", 100);
            for (int i = 0; i < 100; i++) t.Record("Olthoi Slayer", 300);
            Check.Near(t.XpPerKill("Olthoi Slayer") ?? 0, 300, 3, "follows the last ~20 kills (a new level, a bonus weekend)");
            t.Record("Zero", 0);
            t.Record("", 50);
            Check.Eq(t.Count, 2, "no zero XP or empty name");
            t.SaveIfDirty();
            Check.True(File.Exists(t.FilePath), "saved");

            var next = new MonsterXpTable();   // the next login
            next.SetCharacter(dir);
            Check.Near(next.XpPerKill("Drudge Skulker") ?? 0, 1500, 0.01, "loaded at login");
            Check.Eq(next.KillsCounted("Drudge Skulker"), 2, "with its count");
            Check.Eq(next.KillsCounted("Olthoi Slayer"), 300, "every kill counted");
            File.AppendAllText(next.FilePath, "X|Bad|abc|3\nY|later|1|2\nX|Neg|-5|3\n");
            var odd = new MonsterXpTable();
            odd.SetCharacter(dir);
            Check.Eq(odd.Count, 2, "bad lines and a later version's lines are skipped");
            odd.SetCharacter("");
            Check.True(odd.Count == 0 && odd.FilePath.Length == 0, "detached: empty");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void XpPluginWiring()
    {
        string dir = TempDir("ms-xpwire"), charDir = TempDir("ms-xpchar");
        try
        {
            var rig = new Rig(dir);
            rig.Call("SetXpTableCharacter", charDir);
            FakeHost.Quads[(Player, 1)] = 5_000_000;
            rig.Call("TickMonsterStats");   // the starting point
            rig.Set("_currentCombatMode", CombatMode.Melee);
            rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 30));
            rig.Call("NoteMonsterStatsKill", "You killed Drudge Skulker!");
            FakeHost.Quads[(Player, 1)] = 5_250_000;
            rig.Call("TickMonsterStats");
            MonsterStatsRow r = rig.Stats.Get("Drudge Skulker")!;
            Check.Eq((r.Kills, r.Xp, r.XpKills), (1, 250_000L, 1), "the kill's XP on its row");
            Check.Near(rig.P.XpPerKill("Drudge Skulker") ?? 0, 250_000, 1e-9, "and in the learned table");

            FakeHost.Quads[(Player, 1)] = 5_400_000;   // a quest: no kill
            rig.Call("TickMonsterStats");
            Check.Eq(rig.Stats.Get("Drudge Skulker")!.Xp, 250_000L, "a rise with no kill isn't a monster's");

            var lines = rig.Typed("/ra mstats Drudge Skulker");
            Check.True(lines.Count == 3 && lines[0].Text.Contains("XP/kill 250,000"), "/ra mstats says it: " + Show(lines));
            rig.Call("ShutdownMonsterStats");
            Check.True(File.Exists(Path.Combine(charDir, MonsterXpTable.FileName)), "the table is saved at unload");
            rig.Call("OnLogoutMonsterStats");
            Check.Eq(rig.Stats.Count, 0, "logout clears the session's rows");
            Check.Near(rig.P.XpPerKill("Drudge Skulker") ?? 0, 250_000, 1e-9, "the learned table stays");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(charDir, true); } catch { }
        }
    }

    private static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    // ── The table ────────────────────────────────────────────────────────

    private static readonly (string Text, string Rule, bool ByYou, SpellOutcome Outcome, string Name, int Damage)[] Samples =
    {
        // Your spells at a monster (the log's own lines, and ACE's other forms).
        ("You smash Olthoi Slayer for 952 points with Incantation of Shock Wave.", "you-damage", true, SpellOutcome.Landed, "Olthoi Slayer", 952),
        ("Critical hit! You bash Olthoi Slasher for 1,234 points with Incantation of Shock Wave.", "you-damage", true, SpellOutcome.Landed, "Olthoi Slasher", 1234),
        ("Sneak Attack! You gore Olthoi Slayer for 88 points with Force Arc VII.", "you-damage", true, SpellOutcome.Landed, "Olthoi Slayer", 88),
        ("Critical hit! Overpower! Sneak Attack! You crush Drudge Skulker for 12 points with Force Arc VII. Your critical hit was avoided with their augmentation!",
            "you-damage", true, SpellOutcome.Landed, "Drudge Skulker", 12),
        ("You blast The Anchor-Gorged for 153 points with Whirling Blade VII.", "you-damage", true, SpellOutcome.Landed, "The Anchor-Gorged", 153),
        ("With Incantation of Mana Bolt you drain 40 points of mana from Drudge Skulker.", "you-drain", true, SpellOutcome.Landed, "Drudge Skulker", 0),
        ("With Harm Other VI you drain 55 points of health from Drudge Skulker.", "you-drain", true, SpellOutcome.Landed, "Drudge Skulker", 55),
        ("You gain 52 points of health due to casting Drain Health Other I on Olthoi Warrior", "you-transfer", true, SpellOutcome.Landed, "Olthoi Warrior", 0),
        ("You cast Gossamer Flesh on Iron-Gorged Grievver", "you-enchant", true, SpellOutcome.Landed, "Iron-Gorged Grievver", 0),
        ("You cast Gossamer Flesh on Iron-Gorged Grievver, refreshing Gossamer Flesh", "you-enchant", true, SpellOutcome.Landed, "Iron-Gorged Grievver", 0),
        ("You cast Imperil Other VI on Drudge Skulker, surpassing Imperil Other V", "you-enchant", true, SpellOutcome.Landed, "Drudge Skulker", 0),
        ("You cast Imperil Other V on Drudge Skulker, but it is surpassed by Imperil Other VI", "you-enchant", true, SpellOutcome.Landed, "Drudge Skulker", 0),
        ("You cast Nullify All Magic Other on Drudge Skulker and dispel: Strength Self I, Armor Self I.", "you-enchant", true, SpellOutcome.Landed, "Drudge Skulker", 0),
        ("You cast Nullify All Magic Other on Drudge Skulker, but the dispel fails.", "you-enchant", true, SpellOutcome.Landed, "Drudge Skulker", 0),
        ("Frost Golem resists your spell", "you-resisted", true, SpellOutcome.Resisted, "Frost Golem", 0),
        ("Frost Golem resists your spell\n", "you-resisted", true, SpellOutcome.Resisted, "Frost Golem", 0),
        ("You fail to affect Olthoi Slayer with Surge of Affliction", "you-noeffect", true, SpellOutcome.NoEffect, "Olthoi Slayer", 0),
        // A monster's spells at you.
        ("Iron-Gorged Grievver sparks you for 9 points with Lightning Bolt VI.", "it-damage", false, SpellOutcome.Landed, "Iron-Gorged Grievver", 9),
        ("Elite Threadbound Mirebound Rotlord cuts you for 20 points with Incantation of Whirling Blade.", "it-damage", false, SpellOutcome.Landed, "Elite Threadbound Mirebound Rotlord", 20),
        ("Critical hit! Bloodstone sears you for 1,002 points with Acidic Blood. Your augmentation allows you to avoid a critical hit!",
            "it-damage", false, SpellOutcome.Landed, "Bloodstone", 1002),
        ("Bloodstone casts Incantation of Drain Stamina Other and drains 30 points of your stamina.", "it-drain", false, SpellOutcome.Landed, "Bloodstone", 0),
        ("Drudge Shaman casts Harm Other VI and drains 40 points of your health.", "it-drain", false, SpellOutcome.Landed, "Drudge Shaman", 40),
        ("You lose 33 points of health due to Elite Pike Grievver casting Vitality Siphon on you", "it-transfer", false, SpellOutcome.Landed, "Elite Pike Grievver", 33),
        ("You lose 18 points of mana due to Bloodstone casting Incantation of Drain Mana Other on you", "it-transfer", false, SpellOutcome.Landed, "Bloodstone", 0),
        ("Panumbris Shadow cast Weakening Curse VII on you", "it-enchant", false, SpellOutcome.Landed, "Panumbris Shadow", 0),
        ("Panumbris Shadow cast Festering Curse VII on you, refreshing Festering Curse VII", "it-enchant", false, SpellOutcome.Landed, "Panumbris Shadow", 0),
        ("Gilded Wraith cast Lure Blade on your Sharp Sword", "it-enchant", false, SpellOutcome.Landed, "Gilded Wraith", 0),
        ("Gilded Wraith casts Nullify All Magic Self on you and dispels: Strength Self I.", "it-dispel", false, SpellOutcome.Landed, "Gilded Wraith", 0),
        ("Gilded Wraith casts Nullify All Magic Self on you, but the dispel fails.", "it-dispel", false, SpellOutcome.Landed, "Gilded Wraith", 0),
        ("You resist the spell cast by Gilded Shade", "it-resisted", false, SpellOutcome.Resisted, "Gilded Shade", 0),
        ("You resist the spell cast by The Anchor-Gorged", "it-resisted", false, SpellOutcome.Resisted, "The Anchor-Gorged", 0),
        ("Gilded Husk fails to affect you with Lure Blade", "it-noeffect", false, SpellOutcome.NoEffect, "Gilded Husk", 0),
    };

    private static void SpellLinesRead()
    {
        var seen = new HashSet<string>();
        foreach (var s in Samples)
        {
            bool ok = CombatLines.TryParse(s.Text, out CombatLine line);
            Check.True(ok, "reads: " + s.Text);
            if (!ok) continue;
            seen.Add(line.Rule);
            Check.Eq(line.Rule, s.Rule, "row: " + s.Text);
            Check.Eq(line.ByYou, s.ByYou, "whose spell: " + s.Text);
            Check.Eq(line.Outcome, s.Outcome, "outcome: " + s.Text);
            Check.Eq(line.Name, s.Name, "monster: " + s.Text);
            Check.Eq(line.Damage, s.Damage, "damage: " + s.Text);
            Check.Eq(line.MayBeItem, s.Rule is "you-enchant" or "it-enchant" or "it-dispel", "an item may send it: " + s.Text);
        }
        foreach (CombatLines.Row row in CombatLines.Rows)
            Check.True(seen.Contains(row.Id), "every row of the table has a sample: " + row.Id);
        Check.Eq(CombatLines.Rows.Select(r => r.Id).Distinct().Count(), CombatLines.Rows.Length, "row ids are unique");
        Check.Eq(CombatLines.ParseAmount("1,234"), 1234, "thousands");
        Check.Eq(CombatLines.ParseAmount("2000000"), 0, "past a million: not a hit");
    }

    private static readonly string?[] NearMisses =
    {
        null, "", "   ",
        "You cast Executor's Blessing on yourself, refreshing Executor's Blessing",       // your own buff
        "You cast Meditative Trance on yourself and lose 10 points of stamina and also gain 12 points of mana",
        "Some Guy tried and failed to cast a spell at you!",                               // a fizzle: it never reached you
        "You smash Drudge Skulker for 15 points of periodic nether damage!",              // a DoT tick, not a cast
        "You receive 18 points of periodic nether damage.",
        "With Heal Other VI you restore 80 points of health to Drudge Skulker.",          // heals
        "Drudge Shaman casts Heal Other VI and restores 40 points of your health.",
        "You lose 22 points of mana due to casting Incantation of Infuse Mana Other on High-Voltage VI",   // an infusion you cast
        "You gain 22 points of health due to Drudge Shaman casting Drain Health Other on you",              // a gift to you
        "Your cloak reduced the damage from 300 down to 150!",                            // cloaks (player gear)
        "The cloak of Drudge Skulker reduced the damage from 300 down to 150!",
        "Drudge Skulker evaded your attack.",                                             // the client's lines for the typed events
        "You evaded Drudge Skulker!",
        "You slash Drudge Skulker for 12 points of slashing damage!",
        "Olthoi Swarm Harvester blisters your lower leg for 12 points of acid damage!",
        "Drudge Skulker bites you for 12 points of piercing damage!",
        "Drudge Skulker resists your spell!",                                             // not the server's exact line
        "Frost Golem resists your spell and more",
        "Bob says, \"Drudge Skulker resists your spell\"",                                // a player's copy
        "Tester tells you, \"Gilded Shade cast Weakening Curse VII on you\"",
        "resists your spell",                                                             // no monster
        "You resist the spell cast by ",
        "Aetheria surges on Drudge Skulker with the power of Surge of Affliction!",
        "You killed Drudge Skulker!",
        "You fail to heal yourself. Your Greater Stamina Kit has 10 uses left.",
        "You cast Strength Self VI on you",
        "You fail to affect yourself with Lure Blade",
        "You smash Drudge Skulker for 12 points with Force Arc VII",                      // no full stop: not the server's line
    };

    private static void NearMissesDontRead()
    {
        foreach (string? text in NearMisses)
        {
            bool read = CombatLines.TryParse(text, out CombatLine line);
            Check.False(read, $"not counted: '{text ?? "null"}'" + (read ? $" (read as {line.Rule} '{line.Name}')" : ""));
        }
        // An item casts the same line a monster does: it reads, marked as one an item may send (the plugin checks).
        Check.True(CombatLines.TryParse("Frost Hand Wraps cast Aura of Infected Caress on you, refreshing Aura of Infected Caress", out CombatLine item)
                   && item.MayBeItem && item.Name == "Frost Hand Wraps", "an item's line reads, marked MayBeItem");
        Check.True(CombatLines.TryParse("You cast Tusker's Bane on Yoroi Greaves, refreshing Tusker's Bane", out CombatLine own)
                   && own.MayBeItem && own.ByYou, "a spell on your own armour reads, marked MayBeItem");
    }

    // ── Typed events ─────────────────────────────────────────────────────

    /// <summary>AC's 16-bit-length string: u16 length, the bytes, zero padding to a multiple of 4 (with the length).</summary>
    private static byte[] Str16(string s)
    {
        byte[] b = Encoding.Latin1.GetBytes(s);
        var o = new List<byte> { (byte)(b.Length & 0xFF), (byte)(b.Length >> 8) };
        o.AddRange(b);
        while (o.Count % 4 != 0) o.Add(0);
        return o.ToArray();
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);
    private static byte[] F64(double v) => BitConverter.GetBytes(v);
    private static byte[] U64(ulong v) => BitConverter.GetBytes(v);

    internal static byte[] AttackerBody(string name, uint damage) =>
        Cat(Str16(name), U32(4), F64(0.25), U32(damage), U32(1), U64(0));
    internal static byte[] DefenderBody(string name, uint damage) =>
        Cat(Str16(name), U32(1), F64(0.1), U32(damage), U32(3), U32(0), U64(0));
    internal static byte[] NameBody(string name) => Str16(name);

    private static void TypedEventsRead()
    {
        Check.True(CombatEvents.TryParse(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 37), out var k, out string n, out int d)
                   && k == PhysicalEvent.YouHit && n == "Drudge Skulker" && d == 37, "0x01B1: you hit it, for 37");
        Check.True(CombatEvents.TryParse(CombatEvents.DefenderNotification, DefenderBody("Gilded Shade", 12), out k, out n, out d)
                   && k == PhysicalEvent.ItHit && n == "Gilded Shade" && d == 12, "0x01B2: it hit you (a padded name), for 12");
        Check.True(CombatEvents.TryParse(CombatEvents.EvasionAttackerNotification, NameBody("Olthoi Slayer"), out k, out n, out d)
                   && k == PhysicalEvent.ItEvaded && n == "Olthoi Slayer" && d == 0, "0x01B3: it evaded your attack");
        Check.True(CombatEvents.TryParse(CombatEvents.EvasionDefenderNotification, NameBody("Olthoi Slayer"), out k, out n, out d)
                   && k == PhysicalEvent.YouEvaded && n == "Olthoi Slayer", "0x01B4: you evaded its attack");

        byte[] full = AttackerBody("Drudge Skulker", 37);
        Check.False(CombatEvents.TryParse(CombatEvents.AttackerNotification, full.AsSpan(0, full.Length - 16), out _, out _, out _),
            "a hit without its damage doesn't read");
        Check.False(CombatEvents.TryParse(CombatEvents.EvasionAttackerNotification, new byte[] { 40, 0, 65, 66 }, out _, out _, out _),
            "a name longer than the body doesn't read");
        Check.False(CombatEvents.TryParse(CombatEvents.EvasionAttackerNotification, NameBody(""), out _, out _, out _), "no name");
        Check.False(CombatEvents.TryParse(0x01AD, NameBody("You killed Drudge Skulker!"), out _, out _, out _), "a kill notice isn't a combat event");
        Check.True(CombatEvents.GameEvents.All(CombatEvents.IsCombatEvent) && CombatEvents.GameEvents.Length == 4, "the four asked for");
        Check.False(CombatEvents.IsCombatEvent(0x02EB), "transient text isn't one");
    }

    // ── Counters ─────────────────────────────────────────────────────────

    private static CombatLine Line(string text)
    {
        if (!CombatLines.TryParse(text, out CombatLine l)) throw new InvalidOperationException("sample didn't read: " + text);
        return l;
    }

    private static void CountersAndRates()
    {
        var s = new MonsterCombatStats();
        const string D = "Drudge Skulker";
        for (int i = 0; i < 3; i++) s.OnPhysical(PhysicalEvent.YouHit, D, 30, T0);
        s.OnPhysical(PhysicalEvent.ItEvaded, D, 0, T0);
        s.OnSpell(Line("You blast Drudge Skulker for 100 points with Force Bolt VI."), T0);
        s.OnSpell(Line("You cast Imperil Other VI on Drudge Skulker"), T0);
        s.OnSpell(Line("Drudge Skulker resists your spell"), T0);
        s.OnSpell(Line("You fail to affect Drudge Skulker with Surge of Affliction"), T0);
        s.OnPhysical(PhysicalEvent.ItHit, D, 12, T0);
        for (int i = 0; i < 3; i++) s.OnPhysical(PhysicalEvent.YouEvaded, D, 0, T0);
        s.OnSpell(Line("Drudge Skulker zaps you for 9 points with Shock Wave II."), T0);
        s.OnSpell(Line("You resist the spell cast by Drudge Skulker"), T0);

        MonsterStatsRow r = s.Get(D)!;
        Check.Eq((r.Hits, r.Evaded, r.Swings), (3, 1, 4), "your attacks: 3 hit, 1 evaded, 4 swings");
        Check.Eq(MonsterCombatStats.Percent(r.ItEvadesRate), "25%", "it evades 25%");
        Check.Eq((r.SpellsLanded, r.Resisted, r.NoEffect, r.SpellsAtIt), (2, 1, 1, 4), "your spells: 2 landed, 1 resisted, 1 no effect");
        Check.Eq(MonsterCombatStats.Percent(r.ItResistsRate), "25%", "it resists 25% (of the 4 that reached it)");
        Check.Eq((r.HitsTaken, r.YouEvaded, r.Attacks), (1, 3, 4), "its attacks: 1 hit, 3 evaded");
        Check.Eq(MonsterCombatStats.Percent(r.YouEvadeRate), "75%", "you evade 75%");
        Check.Eq((r.SpellsTaken, r.YouResisted, r.SpellsAtYou), (1, 1, 2), "its spells: 1 landed, 1 resisted");
        Check.Eq(MonsterCombatStats.Percent(r.YouResistRate), "50%", "you resist 50%");
        Check.Eq((r.DamageDealt, r.DamageTaken), (190L, 21L), "damage: 3 x 30 + 100 dealt, 12 + 9 taken");
        Check.Eq(r.Kills, 0, "no kill yet");

        // The killing blow sends no hit: the kill notice counts it, by the combat mode it was struck in.
        Check.Eq(s.OnKill("You killed Drudge Skulker!", KillBlow.Physical, T0), D, "a melee kill names the monster");
        r = s.Get(D)!;
        Check.Eq((r.Kills, r.Hits, r.Swings), (1, 4, 5), "a melee kill: a kill and a hit");
        Check.Eq(MonsterCombatStats.Percent(r.ItEvadesRate), "20%", "it evades 1 of 5 now");
        s.OnKill("Drudge Skulker died!", KillBlow.Spell, T0);
        r = s.Get(D)!;
        Check.Eq((r.Kills, r.SpellsLanded), (2, 3), "a magic kill: a kill and a landed spell");
        s.OnKill("The thunder of crushing Drudge Skulker is followed by the deafening silence of death!", KillBlow.None, T0);
        r = s.Get(D)!;
        Check.Eq((r.Kills, r.Hits, r.SpellsLanded), (3, 4, 3), "in peace mode: a kill, no blow");

        // The notice names a monster with no numbers yet: the plugin's look around, only when the notice has the name.
        Check.Null(s.OnKill("You killed Banderling Scout!", KillBlow.Physical, T0), "no numbers and no look around: not credited");
        Check.Eq(s.OnKill("You killed Banderling Scout!", KillBlow.Physical, T0, _ => "Banderling Scout"), "Banderling Scout", "found around");
        Check.Eq(s.Get("Banderling Scout")!.Kills, 1, "a kill for it");
        Check.Null(s.OnKill("You killed Mosswart!", KillBlow.Physical, T0, _ => "Drudge Shaman"), "a name the notice doesn't have isn't credited");
        Check.Null(s.OnKill("", KillBlow.Physical, T0), "an empty notice");
        // The longest name the notice has: Drudge Skulker, not Drudge.
        s.OnPhysical(PhysicalEvent.YouHit, "Drudge", 5, T0);
        Check.Eq(s.OnKill("You flatten Drudge Skulker's body with the force of your assault!", KillBlow.Physical, T0), D, "the longest name wins");

        // Percentages.
        Check.Eq(MonsterCombatStats.Percent(null), "-", "nothing to divide by");
        Check.Eq(MonsterCombatStats.Percent(0), "0%", "0%");
        Check.Eq(MonsterCombatStats.Percent(1), "100%", "100%");
        Check.Eq(MonsterCombatStats.Percent(0.125), "13%", "whole numbers from 10%");
        Check.Eq(MonsterCombatStats.Percent(1 / 3.0), "33%", "a third");
        Check.Eq(MonsterCombatStats.Percent(2 / 3.0), "67%", "two thirds");
        Check.Eq(MonsterCombatStats.Percent(0.045), "4.5%", "one decimal below 10%");
        Check.Eq(MonsterCombatStats.Percent(0.0049), "0.5%", "rounded");
        Check.Eq(MonsterCombatStats.Percent(0.0999), "10%", "9.99 rounds to 10");
        Check.Eq(MonsterCombatStats.Percent(double.NaN), "-", "not a number");
        Check.Eq(MonsterCombatStats.Percent(1.7), "100%", "clamped");
        Check.Null(MonsterCombatStats.Rate(1, 0), "a share of nothing");

        // The words.
        string[] chat = MonsterStatsText.ChatLines(s.Get(D)!);
        Check.Eq(chat.Length, 3, "three chat lines");
        Check.True(chat[0].Contains("Drudge Skulker since login") && chat[0].Contains("4 kills"), "title: " + chat[0]);
        Check.True(chat[1].Contains("6 swings, 5 hit, it evaded 1 (17%)") && chat[1].Contains("it resisted 1 (20%)") && chat[1].Contains("1 no effect"),
            "your side: " + chat[1]);
        Check.True(chat[2].Contains("4 attacks, 1 hit, you evaded 3 (75%)") && chat[2].Contains("you resisted 1 (50%)") && chat[2].Contains("21 damage"),
            "its side: " + chat[2]);
        var detail = MonsterStatsText.DetailRows(s.Get(D)!);
        Check.True(detail.Any(d => d.Label == "No effect"), "the detail shows no effect when there is some");
        Check.False(MonsterStatsText.DetailRows(s.Get("Banderling Scout")!).Any(d => d.Label == "No effect"), "and leaves it out when there is none");

        int v = s.Version;
        s.OnPhysical(PhysicalEvent.YouHit, D, 1, T0);
        Check.True(s.Version > v, "a change moves the version");
    }

    private static void ResetClears()
    {
        var s = new MonsterCombatStats();
        s.OnPhysical(PhysicalEvent.YouHit, "Drudge Skulker", 30, T0);
        s.OnSpell(Line("Mosswart Worshipper resists your spell"), T0);
        s.OnKill("You killed Drudge Skulker!", KillBlow.Physical, T0);
        Check.Eq(s.Count, 2, "two monsters before");
        int v = s.Version;
        s.Reset();
        Check.Eq(s.Count, 0, "none after");
        Check.Eq(s.Snapshot().Length, 0, "an empty list");
        Check.Null(s.Get("Drudge Skulker"), "its numbers are gone");
        Check.False(s.IsTracked("Mosswart Worshipper"), "not tracked");
        Check.True(s.Version > v, "the window sees the change");
        s.OnPhysical(PhysicalEvent.ItEvaded, "Drudge Skulker", 0, T0.AddSeconds(5));
        MonsterStatsRow r = s.Get("Drudge Skulker")!;
        Check.Eq((r.Hits, r.Evaded, r.Kills), (0, 1, 0), "counting starts from zero");
        Check.Eq(r.FirstUtcTicks, T0.AddSeconds(5).Ticks, "first seen anew");
    }

    private static void TwoMonstersApart()
    {
        var s = new MonsterCombatStats();
        const string A = "Drudge Skulker", B = "Mosswart Worshipper";
        s.OnPhysical(PhysicalEvent.YouHit, A, 10, T0);
        s.OnPhysical(PhysicalEvent.ItEvaded, B, 0, T0.AddSeconds(1));
        s.OnSpell(Line("Drudge Skulker resists your spell"), T0.AddSeconds(2));
        s.OnSpell(Line("Mosswart Worshipper blasts you for 20 points with Flame Bolt II."), T0.AddSeconds(3));
        s.OnPhysical(PhysicalEvent.YouEvaded, "drudge skulker", 0, T0.AddSeconds(4));   // one monster in any case
        s.OnKill("You killed Mosswart Worshipper!", KillBlow.Spell, T0.AddSeconds(5));

        Check.Eq(s.Count, 2, "two monsters, not three");
        MonsterStatsRow a = s.Get(A)!, b = s.Get(B)!;
        Check.Eq((a.Hits, a.Evaded, a.Resisted, a.YouEvaded, a.SpellsTaken, a.Kills, a.DamageDealt), (1, 0, 1, 1, 0, 0, 10L), "Drudge's numbers are its own");
        Check.Eq((b.Hits, b.Evaded, b.Resisted, b.YouEvaded, b.SpellsTaken, b.Kills, b.SpellsLanded, b.DamageTaken), (0, 1, 0, 0, 1, 1, 1, 20L),
            "Mosswart's numbers are its own");
        Check.Eq(a.Name, A, "the name as first seen");
        Check.Eq(s.Snapshot()[0].Name, B, "the most recently fought first");

        Check.Eq(s.Find("drudge skulker", out _)!.Name, A, "found by its name in any case");
        Check.Eq(s.Find("moss", out _)!.Name, B, "found by part of its name");
        s.OnPhysical(PhysicalEvent.YouHit, "Drudge Ravener", 1, T0.AddSeconds(6));
        Check.Null(s.Find("Drudge", out List<string> many), "two Drudges: not one");
        Check.True(many.Count == 2 && many.Contains(A) && many.Contains("Drudge Ravener"), "both named: " + string.Join(", ", many));
        s.OnPhysical(PhysicalEvent.YouHit, "Drudge", 1, T0.AddSeconds(7));
        Check.Eq(s.Find("Drudge", out _)!.Name, "Drudge", "an exact name wins over the names it is part of");
        Check.Null(s.Find("Banderling", out List<string> none), "nothing");
        Check.Eq(none.Count, 0, "and no candidates");

        // Past the cap the one fought longest ago goes.
        var cap = new MonsterCombatStats();
        for (int i = 0; i <= MonsterCombatStats.MaxMonsters; i++)
            cap.OnPhysical(PhysicalEvent.YouHit, "Monster " + i, 1, T0.AddSeconds(i));
        Check.Eq(cap.Count, MonsterCombatStats.MaxMonsters, "kept to the cap");
        Check.False(cap.IsTracked("Monster 0"), "the oldest went");
        Check.True(cap.IsTracked("Monster " + MonsterCombatStats.MaxMonsters), "the newest is there");
    }

    // ── Saving ───────────────────────────────────────────────────────────

    private static MonsterStatsSave SampleSave(DateTime saved, DateTime start)
    {
        var s = new MonsterCombatStats();
        s.OnPhysical(PhysicalEvent.YouHit, "Drudge Skulker", 30, start.AddMinutes(1));
        s.OnPhysical(PhysicalEvent.ItEvaded, "Drudge Skulker", 0, start.AddMinutes(1));
        s.OnSpell(Line("You fail to affect Gilded Husk with Lure Blade"), start.AddMinutes(2));
        s.OnPhysical(PhysicalEvent.ItHit, "Bad\tName", 5, start.AddMinutes(3));
        s.OnKill("You killed Drudge Skulker!", KillBlow.Physical, start.AddMinutes(4));
        return new MonsterStatsSave
        {
            ProcessId = 1234, ProcessStartUtcTicks = 5555, CharacterId = 0x50000001,
            SavedUtcTicks = saved.Ticks, SinceUtcTicks = start.Ticks,
            Rows = s.Snapshot().ToList(),
        };
    }

    private static void SaveRestoreRules()
    {
        DateTime start = T0, saved = T0.AddMinutes(30);
        MonsterStatsSave save = SampleSave(saved, start);
        string text = MonsterStatsStore.Serialize(save);
        Check.True(MonsterStatsStore.TryParse(text, out MonsterStatsSave back), "parses");
        Check.Eq((back.ProcessId, back.ProcessStartUtcTicks, back.CharacterId, back.SavedUtcTicks, back.SinceUtcTicks),
            (1234, 5555L, 0x50000001u, saved.Ticks, start.Ticks), "the header fields");
        Check.Eq(back.Rows.Count, 3, "three monsters");
        foreach (MonsterStatsRow r in save.Rows)
        {
            string name = r.Name.Replace('\t', ' ');
            Check.Eq(back.Rows.Single(x => x.Name == name), r with { Name = name }, "round-trips: " + name);
        }
        Check.True(MonsterStatsStore.TryParse(text.Replace("\n", "\r\n"), out _), "Windows line breaks too");

        DateTime soon = saved.AddMinutes(1);
        Check.True(MonsterStatsStore.ShouldRestore(back, 1234, 5555, 0x50000001, soon), "same process, character, recent: restored");
        Check.False(MonsterStatsStore.ShouldRestore(back, 4321, 5555, 0x50000001, soon), "another process");
        Check.False(MonsterStatsStore.ShouldRestore(back, 1234, 6666, 0x50000001, soon), "a new client with a reused process id");
        Check.False(MonsterStatsStore.ShouldRestore(back, 1234, 5555, 0x50000002, soon), "another character");
        Check.False(MonsterStatsStore.ShouldRestore(back, 1234, 5555, 0, soon), "no character");
        Check.False(MonsterStatsStore.ShouldRestore(back, 1234, 5555, 0x50000001, saved.AddMinutes(11)), "too old");
        Check.False(MonsterStatsStore.ShouldRestore(back, 1234, 5555, 0x50000001, saved.AddMinutes(-2)), "from the future");
        var odd = SampleSave(saved, saved.AddMinutes(1));
        Check.False(MonsterStatsStore.ShouldRestore(odd, 1234, 5555, 0x50000001, soon), "started after it was saved: not a real save");

        string noEnd = text.Replace("end=1\n", "");
        Check.False(MonsterStatsStore.TryParse(noEnd, out _), "a torn write (no end marker)");
        Check.False(MonsterStatsStore.TryParse(text.Replace(MonsterStatsStore.Header, "RynthAi monster stats v0"), out _), "another header");
        Check.False(MonsterStatsStore.TryParse(text.Replace("m=Drudge Skulker\t", "m=Drudge Skulker\tx"), out _), "a number that isn't one");
        Check.False(MonsterStatsStore.TryParse(text.Replace("pid=1234\n", "pid=1234\nm=Short\t1\t2\n"), out _), "a row too short");
        Check.False(MonsterStatsStore.TryParse(text.Replace("pid=1234\n", "pid=1234\nnoequals\n"), out _), "a line without '='");
        Check.False(MonsterStatsStore.TryParse(null, out _), "nothing");

        // The files: save, load, delete, cleanup (a temp folder, never %LOCALAPPDATA%).
        string dir = Path.Combine(Program.TempRoot, "monster-stats-" + Guid.NewGuid().ToString("N"));
        try
        {
            MonsterStatsStore.Save(dir, save);
            Check.True(File.Exists(Path.Combine(dir, MonsterStatsStore.FileName(1234, 0x50000001))), "saved as " + MonsterStatsStore.FileName(1234, 0x50000001));
            Check.True(MonsterStatsStore.TryLoad(dir, 1234, 0x50000001, out MonsterStatsSave loaded) && loaded.Rows.Count == 3, "loads");
            Check.False(MonsterStatsStore.TryLoad(dir, 1234, 0x50000002, out _), "another character's file isn't there");
            var other = SampleSave(saved, start);
            other.ProcessId = 999;
            MonsterStatsStore.Save(dir, other);
            var old = SampleSave(saved, start);
            old.ProcessId = 777;
            MonsterStatsStore.Save(dir, old);
            File.SetLastWriteTimeUtc(Path.Combine(dir, MonsterStatsStore.FileName(777, 0x50000001)), DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(Path.Combine(dir, "session-1234-50000001.txt"), "not ours");
            int removed = MonsterStatsStore.Cleanup(dir, 1234, DateTime.UtcNow);
            Check.Eq(removed, 2, "this process's save and a closed client's old one");
            Check.True(File.Exists(Path.Combine(dir, MonsterStatsStore.FileName(999, 0x50000001))), "another running client's stays");
            Check.True(File.Exists(Path.Combine(dir, "session-1234-50000001.txt")), "files that aren't monster stats stay");
            MonsterStatsStore.Delete(dir, 999, 0x50000001);
            Check.False(File.Exists(Path.Combine(dir, MonsterStatsStore.FileName(999, 0x50000001))), "deleted");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── The plugin ───────────────────────────────────────────────────────

    private const uint Player = 0x50000E31, Drudge = 0x80000E01, Mosswart = 0x80000E02;
    private const uint Fellow = 0x50000E99;   // another player nearby (the cache files it as a creature)
    private const uint Lb = 0xA9B40000;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // ── A fake engine's plugin windows (v89) and UI flags (v77), for the tab / fallback choice ──

    private static uint _uiFlags;
    private static readonly List<(string Key, int Mode)> PanelCalls = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint FakeUiFlags() => _uiFlags;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int FakeScreenMode(int* previous) { if (previous != null) *previous = 0; return 0x10000008; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint FakeUiColor(int token) => 0xFFFFFFFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr FakeUiFont(int font) => IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int FakeSetPanelOpen(byte* key, int mode)
    {
        PanelCalls.Add((Marshal.PtrToStringUTF8((IntPtr)key) ?? "", mode));
        return mode == 0 ? 0 : 1;
    }

    /// <summary>An engine with plugin windows and UI flags <paramref name="flags"/> (bit 8: its Damage panel draws "mstats").</summary>
    private static unsafe void PluginWindowsEngine(ref RynthCoreApiNative api, uint flags)
    {
        _uiFlags = flags;
        api.GetUiHookFlagsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&FakeUiFlags;
        api.GetScreenModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, int>)&FakeScreenMode;
        api.GetUiColorFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, uint>)&FakeUiColor;
        api.GetUiFontFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr>)&FakeUiFont;
        api.SetPanelOpenFn = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&FakeSetPanelOpen;
    }

    /// <summary>A logged-in RynthAi on the fakes with two monsters around, its save in a temp folder.
    /// <paramref name="uiFlags"/> set: an engine with plugin windows and those UI flags.</summary>
    private sealed class Rig
    {
        public readonly RynthAiPlugin P = new();
        public readonly RynthCoreHost Host;

        public Rig(string saveDir, uint? uiFlags = null)
        {
            FakeHost.Reset();
            PanelCalls.Clear();
            if (uiFlags is uint flags) FakeHost.ConfigureApi = (ref RynthCoreApiNative api) => PluginWindowsEngine(ref api, flags);
            FakeHost.Names[Player] = "Tester";
            FakeHost.PlayerIdValue = Player;
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
            foreach (var (id, name, y, wcid) in new[] { (Drudge, "Drudge Skulker", 99f, 0x1F01u), (Mosswart, "Mosswart Worshipper", 101f, 0x1F02u), (Fellow, "Some Fellow", 97f, 1u) })
            {
                FakeHost.Names[id] = name;
                FakeHost.Positions[id] = (Lb | 0x1C, 96f, y, 0f);
                FakeHost.ItemTypes[id] = 0x10;   // a creature
                FakeHost.Wcids[id] = wcid;
            }
            Host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var cache = new WorldObjectCache(Host);
            cache.SetPlayerId(Player);
            foreach (uint id in new[] { Drudge, Mosswart, Fellow }) _ = cache[unchecked((int)id)];
            Set("_objectCache", cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            P.MonsterStatsDirectory = saveDir;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Get(string f) => typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        public object? Call(string method, params object?[] args) => typeof(RynthAiPlugin).GetMethod(method, Any)!.Invoke(P, args);
        public MonsterCombatStats Stats => P.MonsterStatsForTests;

        public void Chat(string text, int chatType = 7) { int eat = 0; P.OnChatWindowText(text, chatType, ref eat); }

        /// <summary>A line typed in the chat bar; the chat lines it wrote.</summary>
        public List<(string Text, int Type)> Typed(string line)
        {
            int before = FakeHost.Chat.Count;
            int eat = 0;
            P.OnChatBarEnter(line, ref eat);
            var lines = new List<(string, int)>();
            for (int i = before; i < FakeHost.Chat.Count; i++)
                lines.Add((FakeHost.Chat[i], i < FakeHost.ChatTypes.Count ? FakeHost.ChatTypes[i] : -1));
            return lines;
        }

        /// <summary>The Mini Remote's session, started (as at login), saved in <paramref name="dir"/>.</summary>
        public SessionStatsTracker StartSession(string dir)
        {
            var t = new SessionStatsTracker(Host, "[test]", dir);
            t.OnLoginComplete();
            Set("_sessionStats", t);
            return t;
        }

        /// <summary>The Damage panel's rows (RynthPluginGetMonsterDamageJson), by name, the Default line left out.</summary>
        public Dictionary<string, JsonElement> DamageRows(string storeDir)
        {
            if (Get("_damageStore") == null)
            {
                var store = new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore();
                Directory.CreateDirectory(storeDir);
                store.SetCharacter(storeDir);
                Set("_damageStore", store);
                Set("_creatureStore", new RynthCore.Plugin.RynthAi.CreatureData.CreatureProfileStore());
            }
            using var doc = JsonDocument.Parse(P.BuildMonsterDamageJson());
            var rows = new Dictionary<string, JsonElement>();
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
                if (!(e.TryGetProperty("isDefault", out var d) && d.GetBoolean())) rows[e.GetProperty("name").GetString()!] = e.Clone();
            return rows;
        }
    }

    private static string TempDir(string what) => Path.Combine(Program.TempRoot, what + "-" + Guid.NewGuid().ToString("N"));

    private static void PluginWiring()
    {
        string dir = TempDir("ms-wiring");
        var rig = new Rig(dir);
        rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 30));
        rig.P.NoteCombatEvent(CombatEvents.EvasionAttackerNotification, NameBody("Drudge Skulker"));
        rig.P.NoteCombatEvent(CombatEvents.DefenderNotification, DefenderBody("Drudge Skulker", 8));
        rig.P.NoteCombatEvent(CombatEvents.EvasionDefenderNotification, NameBody("Drudge Skulker"));
        MonsterStatsRow d = rig.Stats.Get("Drudge Skulker")!;
        Check.Eq((d.Hits, d.Evaded, d.HitsTaken, d.YouEvaded, d.DamageDealt, d.DamageTaken), (1, 1, 1, 1, 30L, 8L), "the four typed events, through the plugin");

        rig.Chat("Drudge Skulker resists your spell");
        rig.Chat("You resist the spell cast by Drudge Skulker");
        d = rig.Stats.Get("Drudge Skulker")!;
        Check.Eq((d.Resisted, d.YouResisted), (1, 1), "the spell lines, through OnChatWindowText");

        rig.Chat("Drudge Skulker resists your spell", ChatSource.Speech);
        rig.Chat("You resist the spell cast by Drudge Skulker", ChatSource.Tell);
        d = rig.Stats.Get("Drudge Skulker")!;
        Check.Eq((d.Resisted, d.YouResisted), (1, 1), "a player's chat types don't count");

        rig.Chat("Frost Hand Wraps cast Aura of Infected Caress on you, refreshing Aura of Infected Caress");
        rig.Chat("You cast Tusker's Bane on Yoroi Greaves, refreshing Tusker's Bane");
        Check.False(rig.Stats.IsTracked("Frost Hand Wraps") || rig.Stats.IsTracked("Yoroi Greaves"), "an item's spell, or one on an item, isn't a monster's");
        rig.Chat("Mosswart Worshipper cast Weakening Curse VII on you");
        Check.Eq(rig.Stats.Get("Mosswart Worshipper")?.SpellsTaken, 1, "a creature around casting on you counts");
        rig.Chat("Gilded Wraith cast Weakening Curse VII on you");
        Check.False(rig.Stats.IsTracked("Gilded Wraith"), "a name neither around nor fought isn't counted for an enchantment line");
        rig.P.NoteCombatEvent(CombatEvents.DefenderNotification, DefenderBody("Gilded Wraith", 4));
        rig.Chat("Gilded Wraith cast Weakening Curse VII on you");
        Check.Eq(rig.Stats.Get("Gilded Wraith")?.SpellsTaken, 1, "once fought (out of sight), its enchantment counts");

        // Another player: buffing each other, a spar. Never a monster, even filed as a creature.
        rig.Chat("You cast Strength Other VI on Some Fellow");
        rig.Chat("Some Fellow cast Strength Other VI on you, refreshing Strength Other VI");
        rig.Chat("Some Fellow resists your spell");
        rig.Chat("You resist the spell cast by Some Fellow");
        rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Some Fellow", 9));
        rig.P.NoteCombatEvent(CombatEvents.EvasionDefenderNotification, NameBody("Some Fellow"));
        Check.False(rig.Stats.IsTracked("Some Fellow"), "a player's lines and events don't count");
        rig.Set("_currentTargetId", Fellow);
        Check.Eq((string)rig.Call("MonsterStatsFocusName")!, "", "a selected player isn't the monster in focus");
        rig.Set("_currentTargetId", 0u);

        rig.Set("_currentCombatMode", CombatMode.Melee);
        rig.Call("NoteMonsterStatsKill", "You killed Drudge Skulker!");
        d = rig.Stats.Get("Drudge Skulker")!;
        Check.Eq((d.Kills, d.Hits), (1, 2), "a melee kill: the killing blow is a hit");
        rig.Set("_currentCombatMode", CombatMode.Magic);
        rig.Call("NoteMonsterStatsKill", "Mosswart Worshipper died!");
        MonsterStatsRow m = rig.Stats.Get("Mosswart Worshipper")!;
        Check.Eq((m.Kills, m.SpellsLanded), (1, 1), "a magic kill: a landed spell");

        rig.Set("_loginComplete", false);
        rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 30));
        rig.Chat("Drudge Skulker resists your spell");
        d = rig.Stats.Get("Drudge Skulker")!;
        Check.Eq((d.Hits, d.Resisted), (2, 1), "nothing counts out of the world");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void Lifetime()
    {
        string saveDir = TempDir("ms-save"), sessionDir = TempDir("ms-session");
        try
        {
            // Login: the first tick with the character known starts the counting.
            var rig = new Rig(saveDir);
            SessionStatsTracker session = rig.StartSession(sessionDir);
            Check.False((bool)rig.Get("_monsterStatsStarted")!, "not started before the first tick");
            rig.Call("TickMonsterStats");
            Check.True((bool)rig.Get("_monsterStatsStarted")!, "started on the login's first tick");
            rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 30));
            rig.P.NoteCombatEvent(CombatEvents.EvasionAttackerNotification, NameBody("Drudge Skulker"));

            // Reset XP (the Mini Remote session's reset, which RynthTracker's reset also runs) leaves them.
            Thread.Sleep(20);
            long before = session.View.SessionStartUtc.Ticks;
            session.RequestReset();
            session.Tick();
            Check.True(session.View.SessionStartUtc.Ticks != before, "the session started over");
            rig.Call("TickMonsterStats");
            MonsterStatsRow? d = rig.Stats.Get("Drudge Skulker");
            Check.True(d != null && d.Hits == 1 && d.Evaded == 1, "Reset XP leaves the monster stats");

            // A hot reload: the copy going saves, the next brings them back.
            rig.Call("ShutdownMonsterStats");
            Check.True(File.Exists(Path.Combine(saveDir, MonsterStatsStore.FileName(Environment.ProcessId, Player))), "saved at unload");
            var next = new Rig(saveDir);
            Check.Eq(next.Stats.Count, 0, "the new copy starts empty");
            next.Call("TickMonsterStats");
            d = next.Stats.Get("Drudge Skulker");
            Check.True(d != null && d.Hits == 1 && d.Evaded == 1, "the stats carried over");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Monster stats carried over")), "and the log says so");
            Check.True(next.Stats.Count == 1, "only what was saved");

            // The resets that do clear them: /ra mstats reset (CommandTest), the window's, the Damage panel's Reset stats.
            next.P.ClearMonsterStats();   // the Damage panel's Reset stats (RynthPluginClearMonsterStats)
            Check.Eq(next.Stats.Count, 1, "the panel's reset is done on the tick");
            next.Call("TickMonsterStats");
            Check.Eq(next.Stats.Count, 0, "the Damage panel's Reset stats clears them");
            Check.True(MonsterStatsStore.TryLoad(saveDir, Environment.ProcessId, Player, out MonsterStatsSave afterReset) && afterReset.Rows.Count == 0,
                "a reset is saved at once (a reload right after it can't bring the old numbers back)");
            next.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Mosswart Worshipper", 5));
            next.Call("ResetMonsterStatsFromWindow");
            Check.Eq(next.Stats.Count, 0, "the window's Reset clears them");

            // A real logout ends them, and the save with them; the next login starts fresh.
            next.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Mosswart Worshipper", 5));
            next.Call("ShutdownMonsterStats");
            next.Call("OnLogoutMonsterStats");
            Check.Eq(next.Stats.Count, 0, "logout: no stats");
            Check.False(File.Exists(Path.Combine(saveDir, MonsterStatsStore.FileName(Environment.ProcessId, Player))), "logout: the save is gone");
            Check.False((bool)next.Get("_monsterStatsStarted")!, "and not counting until the next login");
            next.Call("TickMonsterStats");
            Check.True((bool)next.Get("_monsterStatsStarted")! && next.Stats.Count == 0, "the next login starts fresh");

            // A save for another character in this client isn't picked up.
            var other = new MonsterStatsSave
            {
                ProcessId = Environment.ProcessId, ProcessStartUtcTicks = MonsterStatsStore.ProcessStartTicks, CharacterId = Player + 1,
                SavedUtcTicks = DateTime.UtcNow.Ticks, SinceUtcTicks = DateTime.UtcNow.Ticks,
                Rows = new List<MonsterStatsRow> { new("Drudge Skulker", 9, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) },
            };
            MonsterStatsStore.Save(saveDir, other);
            var third = new Rig(saveDir);
            third.Call("TickMonsterStats");
            Check.Eq(third.Stats.Count, 0, "another character's save isn't this one's");
        }
        finally
        {
            try { Directory.Delete(saveDir, true); } catch { }
            try { Directory.Delete(sessionDir, true); } catch { }
        }
    }

    private static void DamageRowsCarryStats()
    {
        string dir = TempDir("ms-rows");
        try
        {
            var rig = new Rig(dir);
            Dictionary<string, JsonElement> rows = rig.DamageRows(Path.Combine(dir, "char"));
            Check.True(rows.ContainsKey("Drudge Skulker") && rows.ContainsKey("Mosswart Worshipper"), "both monsters around are rows: " + string.Join(", ", rows.Keys));
            Check.False(rows.ContainsKey("Some Fellow"), "the other player isn't");
            Check.False(rows.Values.Any(r => r.TryGetProperty("mstats", out _)), "no numbers yet: no mstats");

            for (int i = 0; i < 3; i++) rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 10));
            rig.P.NoteCombatEvent(CombatEvents.EvasionAttackerNotification, NameBody("Drudge Skulker"));
            rig.Chat("Drudge Skulker resists your spell");
            rig.Chat("You blast Drudge Skulker for 40 points with Force Bolt VI.");
            rig.Chat("You fail to affect Drudge Skulker with Surge of Affliction");
            rig.P.NoteCombatEvent(CombatEvents.DefenderNotification, DefenderBody("Drudge Skulker", 7));
            rig.P.NoteCombatEvent(CombatEvents.EvasionDefenderNotification, NameBody("Drudge Skulker"));
            rig.Chat("Drudge Skulker zaps you for 9 points with Shock Wave II.");
            rig.Chat("You resist the spell cast by Drudge Skulker");
            rig.Set("_currentCombatMode", CombatMode.Melee);
            rig.Call("NoteMonsterStatsKill", "You killed Drudge Skulker!");

            rows = rig.DamageRows(Path.Combine(dir, "char"));
            Check.False(rows["Drudge Skulker"].TryGetProperty("mstats", out _), "the panel's poll reads what the tick published (none yet)");
            rig.Call("TickMonsterStats");
            rows = rig.DamageRows(Path.Combine(dir, "char"));
            Check.True(rows["Drudge Skulker"].TryGetProperty("mstats", out JsonElement m), "after the tick the row has mstats");
            if (m.ValueKind == JsonValueKind.Object)
            {
                int I(string k) => m.TryGetProperty(k, out JsonElement v) ? v.GetInt32() : -1;
                // The engine's DamageRowStats field names (RynthCore tools/DamageRowStatsTests).
                Check.Eq((I("sw"), I("ev"), I("sp"), I("rs"), I("ne")), (5, 1, 3, 1, 1), "your side: 3 hits + the killing blow, 1 evaded; 3 spells, 1 resisted, 1 no effect");
                Check.Eq((I("at"), I("yev"), I("ysp"), I("yrs"), I("yne")), (2, 1, 2, 1, 0), "its side");
                Check.Eq((I("k"), I("dd"), I("dt")), (1, 70, 16), "kill and damage (30 + 40 dealt, 7 + 9 taken)");
                Check.Eq((I("xp"), I("xk")), (0, 0), "no XP seen yet (2026-11-08 fields)");
                Check.Eq((I("cd"), I("cn")), (0, 0), "no corpse timed yet (2026-11-08 fields)");
                Check.Eq(m.EnumerateObject().Count(), 17, "the seventeen fields");
            }
            Check.False(rows["Mosswart Worshipper"].TryGetProperty("mstats", out _), "a monster without numbers has none");
            Check.True(rows["Drudge Skulker"].TryGetProperty("key", out _) && rows["Drudge Skulker"].TryGetProperty("kills", out _), "the row's own fields are all still there");

            rig.Call("ResetMonsterStatsFromWindow");
            rig.Call("TickMonsterStats");
            rows = rig.DamageRows(Path.Combine(dir, "char"));
            Check.False(rows["Drudge Skulker"].TryGetProperty("mstats", out _), "after a reset the line goes");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void EngineFlag()
    {
        string dir = TempDir("ms-flag");
        try
        {
            // An older engine with plugin windows (the owner's API-90 core): no bit 8.
            var older = new Rig(dir, uiFlags: 0x7Fu);
            Check.False(older.P.EngineDrawsMonsterStats, "no bit 8: the Damage panel doesn't draw them");
            Check.True(WindowWanted(older), "the fallback window is declared (and the Session Stats button offered: CreateMiniRemote uses the same rule)");
            string json = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, monsterStats: WindowWanted(older));
            Check.True(json.Contains("\"window:monsterstats\""), "in the manifest");
            var lines = older.Typed("/ra mstats show");
            Check.True(lines.Count == 1 && lines[0].Text.Contains("Monster Stats window open") && PanelCalls.Last() == (RynthAiPlugin.MonsterStatsTitle, 1),
                "/ra mstats show opens the window: " + Show(lines));

            // An engine whose Damage panel draws them: no window, no button, show opens the panel.
            var tab = new Rig(dir, uiFlags: 0x7Fu | RynthAiPlugin.DamageRowStatsFlag);
            Check.True(tab.P.EngineDrawsMonsterStats, "bit 8: the Damage panel draws them");
            Check.False(WindowWanted(tab), "no fallback window, and so no Session Stats button");
            Check.False(SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, monsterStats: WindowWanted(tab))
                .Contains("monsterstats"), "not in the manifest");
            lines = tab.Typed("/ra mstats show");
            Check.True(lines.Count == 1 && lines[0].Text.Contains("Damage panel (Monsters) open") && lines[0].Text.Contains("under its line")
                       && PanelCalls.Last() == (RynthAiPlugin.DamagePanelKey, 1), "/ra mstats show opens the Damage panel: " + Show(lines));
            lines = tab.Typed("/ra mstats hide");
            Check.True(lines.Count == 1 && lines[0].Text.Contains("closed") && PanelCalls.Last() == (RynthAiPlugin.DamagePanelKey, 0), "hide closes it: " + Show(lines));

            // No plugin windows at all (the rest of the tests' engine): neither, and show says why.
            var none = new Rig(dir);
            Check.False(none.P.EngineDrawsMonsterStats || WindowWanted(none), "no windows, no flag");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static bool WindowWanted(Rig rig)
        => (bool)typeof(RynthAiPlugin).GetProperty("MonsterStatsWindowWanted", Any)!.GetValue(rig.P)!;

    private static string Show(List<(string Text, int Type)> lines) => string.Join(" | ", lines.Select(l => $"[{l.Type}] {l.Text}"));

    private static void Command()
    {
        string dir = TempDir("ms-cmd");
        var rig = new Rig(dir);
        var lines = rig.Typed("/ra mstats list");
        Check.True(lines.Count == 1 && lines[0].Text.Contains("No monster stats yet"), "list with none: " + Show(lines));
        lines = rig.Typed("/ra mstats Drudge Skulker");
        Check.True(lines.Count == 1 && lines[0].Type == CommandReply.FailColor && lines[0].Text.Contains("No monster stats for 'Drudge Skulker'")
                   && lines[0].Text.Contains(RynthAiPlugin.MonsterStatsUsage), "none yet: why, and the usage: " + Show(lines));

        for (int i = 0; i < 3; i++) rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 10));
        rig.P.NoteCombatEvent(CombatEvents.EvasionAttackerNotification, NameBody("Drudge Skulker"));
        rig.Chat("Drudge Skulker resists your spell");
        rig.P.NoteCombatEvent(CombatEvents.EvasionDefenderNotification, NameBody("Mosswart Worshipper"));
        rig.P.NoteCombatEvent(CombatEvents.EvasionDefenderNotification, NameBody("Drudge Ravener"));

        lines = rig.Typed("/ra mstats Drudge Skulker");
        Check.Eq(lines.Count, 3, "three lines: " + Show(lines));
        Check.True(lines.All(l => l.Type == CommandReply.OkColor), "shown as an answer, not a failure");
        Check.True(lines.Count == 3 && lines[0].Text.Contains("Drudge Skulker since login") && lines[1].Text.Contains("4 swings, 3 hit, it evaded 1 (25%)")
                   && lines[1].Text.Contains("it resisted 1 (100%)") && lines[2].Text.Contains("0 attacks"), "its numbers: " + Show(lines));
        lines = rig.Typed("/ra mstats moss");
        Check.True(lines.Count == 3 && lines[0].Text.Contains("Mosswart Worshipper") && lines[2].Text.Contains("you evaded 1 (100%)"),
            "part of a name: " + Show(lines));
        lines = rig.Typed("/ra mstats Drudge");
        Check.True(lines.Count == 1 && lines[0].Type == CommandReply.FailColor && lines[0].Text.Contains("matches 2 monsters")
                   && lines[0].Text.Contains("Drudge Skulker") && lines[0].Text.Contains("Drudge Ravener"), "two Drudges: say which: " + Show(lines));
        lines = rig.Typed("/ra mstats Banderling");
        Check.True(lines.Count == 1 && lines[0].Type == CommandReply.FailColor && lines[0].Text.Contains("No monster stats for 'Banderling'")
                   && lines[0].Text.Contains("Monsters with stats:") && lines[0].Text.Contains("Usage:"), "unknown: why, who has, the usage: " + Show(lines));

        // No name: the monster selected (or fought).
        lines = rig.Typed("/ra mstats");
        Check.True(lines.Count == 1 && lines[0].Type == CommandReply.FailColor && lines[0].Text.Contains("Select a monster or name one"),
            "nothing selected: " + Show(lines));
        rig.Set("_currentTargetId", Drudge);
        lines = rig.Typed("/ra mstats");
        Check.True(lines.Count == 3 && lines[0].Text.Contains("Drudge Skulker since login"), "the selected monster: " + Show(lines));

        lines = rig.Typed("/ra mstats list");
        Check.True(lines.Count == 4 && lines[0].Text.Contains("3 kinds") && lines.Skip(1).All(l => l.Text.Contains("it evades")), "the list: " + Show(lines));

        lines = rig.Typed("/ra mstats show");
        Check.True(lines.Count == 1 && lines[0].Type == CommandReply.FailColor && lines[0].Text.Contains("plugin windows"),
            "no plugin windows in this engine: says so: " + Show(lines));

        lines = rig.Typed("/ra mstats reset");
        Check.True(lines.Count == 1 && lines[0].Type == CommandReply.OkColor && lines[0].Text.Contains("started over (3 kinds"), "reset: " + Show(lines));
        Check.Eq(rig.Stats.Count, 0, "and they are gone");
        lines = rig.Typed("/ra mstats");
        Check.True(lines.Count == 1 && lines[0].Type == CommandReply.FailColor && lines[0].Text.Contains("No monster stats for Drudge Skulker since login yet"),
            "selected, but no numbers since the reset: " + Show(lines));

        lines = rig.Typed("/ra help");
        Check.True(lines.Any(l => l.Text.Contains("/ra mstats")), "the help lists it");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void WindowView()
    {
        string dir = TempDir("ms-view");
        var rig = new Rig(dir);
        MonsterStatsView View() => (MonsterStatsView)rig.Get("_monsterStatsView")!;
        Check.False(View().InWorld, "before the first tick: the empty view");
        rig.Call("TickMonsterStats");
        Check.True(View().InWorld && View().Rows.Length == 0 && View().PhysicalCounted, "the first tick in the world publishes, window or not");

        rig.P.NoteCombatEvent(CombatEvents.AttackerNotification, AttackerBody("Drudge Skulker", 30));
        rig.Set("_monsterStatsViewAtMs", 0L);
        rig.Call("TickMonsterStats");
        Check.Eq(View().Rows.Length, 0, "off screen a fight doesn't rebuild the view");

        rig.Set("_monsterStatsDrawnMs", Environment.TickCount64);   // the window drew a frame
        rig.Set("_currentTargetId", Drudge);
        rig.Call("TickMonsterStats");
        Check.True(View().Rows.Length == 1 && View().Rows[0].Hits == 1, "on screen it follows the numbers");
        Check.Eq(View().FocusName, "Drudge Skulker", "and the monster in focus (selected)");

        rig.Set("_combatEventsStreaming", false);
        rig.P.NoteCombatEvent(CombatEvents.EvasionAttackerNotification, NameBody("Drudge Skulker"));
        rig.Set("_monsterStatsViewAtMs", 0L);
        rig.Call("TickMonsterStats");
        Check.False(View().PhysicalCounted, "no combat event stream: the window says melee and missile go uncounted");

        rig.Call("OnLogoutMonsterStats");
        Check.False(View().InWorld, "logout: the empty view at once");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void ManifestWindow()
    {
        string json = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, dashboard: true, monsterStats: true);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, string.Empty, "no parse error");
        Check.Eq(m.Windows.Length, 2, "the dashboard and Monster Stats");
        SectionWindowDecl w = m.Windows.Single(x => x.Id == "monsterstats");
        Check.Eq(w.Title, RynthAiPlugin.MonsterStatsTitle, "title (its panel key)");
        Check.Eq(w.Surface, RynthAiPlugin.MonsterStatsSurface, "surface");
        Check.Eq(w.Surface, "window:monsterstats", "the surface PluginExports.RenderEmbed draws");
        Check.False(w.Bar, "no bar button (the Session Stats view and /ra mstats show open it)");
        Check.False(w.TakeOver || w.Frameless, "the engine's frame, nobody's place");
        Check.True(w.Server.Length == 0, "every world");
        Check.True(w.Width >= w.MinWidth && w.Height >= w.MinHeight, "sizes");
        Check.True(m.Windows.Single(x => x.Id == "dashboard").TakeOver, "the dashboard window is unchanged");

        SectionManifest alone = SectionManifestParser.Parse("RynthAi",
            SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, monsterStats: true), out error);
        Check.True(error.Length == 0 && alone.Windows.Length == 1 && alone.Windows[0].Id == "monsterstats", "without the dashboard (Classic look)");
        SectionManifest none = SectionManifestParser.Parse("RynthAi", SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections), out _);
        Check.Eq(none.Windows.Length, 0, "an engine before v89: no window declared");
        Check.Eq(none.Contributions.Length, 5, "the section views are unchanged");
    }
}
