using System;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The player casting by hand while the macro runs (2026-11-18, R-20261118-214609-a9a8 and
// R-20261118-205826-9c70, DreamWeave, 2026.11.15.3): "spells are not connecting anymore". A 'too busy'
// UseDone about every 0.4 s with nothing sent by RynthAi; the sender was the player's own spell bar
// (AC prints "Casting <spell>" for each click, type 26; RynthAi's casts never do). RynthAi took each
// refusal as its own: an 8 s backoff, and at the 4th a StopCompletely into the cast that was running.
// Replayed here: the player's clicks, each refused, never stop the character, never back RynthAi off,
// never count as stuck-cast evidence; RynthAi starts nothing while the player casts (an emergency heal
// still may); and with no hand cast the old streak rules still hold.
internal static class HandCastTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeLife = 33;
    private const uint TooBusy = 0x1D;
    private const string Spell = "Incantation of Force Streak";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static void Register(Runner r)
    {
        r.Add("hand cast: AC's own 'Casting <spell>' (type 26) is the player's cast; RynthAi's and other lines are not", ParsesOnlyAcLine);
        r.Add("hand cast (R-20261118-214609-a9a8): the player's refused clicks send no StopCompletely, no backoff, no streak", RefusalsAreNotOurs);
        r.Add("hand cast: while the player casts RynthAi starts nothing (an emergency heal may), no move starts; it ends 3 s after the last click, logged", PacerYields);
        r.Add("hand cast: with no hand cast, RynthAi's own refusal streak still stops the character once", OwnStreakStillStops);
    }

    private static long _t;

    private static void PaceOn()
    {
        _t = 20_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _t;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _t;
    }

    private static void PaceOff()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
    }

    private static void Paced(Action body)
    {
        PaceOn();
        try { body(); }
        finally { PaceOff(); }
    }

    private static (RynthCoreHost host, BuffManager b) Caster()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create(Player);
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                                       GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);
        return (host, b);
    }

    private static int Streak(BuffManager b) => (int)typeof(BuffManager).GetField("_busyRefusalStreak", Any)!.GetValue(b)!;
    private static DateTime BackoffUntil(BuffManager b) => (DateTime)typeof(BuffManager).GetField("_busyBackoffUntil", Any)!.GetValue(b)!;

    /// <summary>One click of the player's, as the client sees it: AC's line, the server's 0x1D, AC's refusal line.</summary>
    private static void RefusedClick(RynthCoreHost host, BuffManager b)
    {
        Check.True(RynthAiPlugin.TryParseHandCast("Casting " + Spell, RynthAiPlugin.AcStatusChatType, out string spell), "AC's line parses");
        ActionPacer.NoteHandCast(host, spell);
        FakeHost.UseDone(TooBusy);
        ActionPacer.MayStart(host, "Buff", HealNeed.None, out _);   // RynthAi's tick reads the answer
        b.OnChatWindowText("You're too busy!", RynthAiPlugin.AcStatusChatType);
    }

    private static void ParsesOnlyAcLine()
    {
        Check.True(RynthAiPlugin.TryParseHandCast("Casting Incantation of Force Arc", 26, out string s1), "the report's line");
        Check.Eq(s1, "Incantation of Force Arc", "its spell");
        Check.True(RynthAiPlugin.TryParseHandCast("Casting Blade Arc VII", 26, out string s2) && s2 == "Blade Arc VII", "a level VII spell");
        Check.False(RynthAiPlugin.TryParseHandCast("[RynthAi] Casting: Incantation of Focus Self", 5, out _), "RynthAi's own line");
        Check.False(RynthAiPlugin.TryParseHandCast("Casting Incantation of Force Arc", 5, out _), "not AC's status type");
        Check.False(RynthAiPlugin.TryParseHandCast("Casting Incantation of Focus Self...", 26, out _), "another plugin's notice");
        Check.False(RynthAiPlugin.TryParseHandCast("Bob tells you, \"Casting Blade Arc VII\"", 26, out _), "speech");
        Check.False(RynthAiPlugin.TryParseHandCast("Using the Corpse of Olthoi Warrior", 26, out _), "a use line");
        Check.False(RynthAiPlugin.TryParseHandCast("Casting ", 26, out _), "nothing after it");
    }

    // 16:45:16.9-16:45:30.7: 24 refusals of the player's clicks; at the 4th RynthAi sent StopCompletely
    // ("the cast gesture looks stuck") into a running cast, and backed its buffs off 8 s.
    private static void RefusalsAreNotOurs() => Paced(() =>
    {
        var (host, b) = Caster();
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "RynthAi's tick before the clicks (the answers' baseline)");
        for (int i = 0; i < 8; i++)
        {
            RefusedClick(host, b);
            _t += 400;
        }
        Check.Eq(FakeHost.Stops, 0, "eight refused clicks of the player's: no StopCompletely into the running cast");
        Check.Eq(Streak(b), 0, "no 'too busy' streak of RynthAi's");
        Check.True(BackoffUntil(b) == DateTime.MinValue, "no backoff for RynthAi's own casts");
        Check.Eq(ActionPacer.HandCastRefusals, 8L, "the eight refusals counted as the player's");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("answers the player's own cast by hand")), 1, "said once for the run");
        Check.False(FakeHost.Logs.Any(l => l.Contains("too-busy streak=")), "no streak lines");
    });

    private static void PacerYields() => Paced(() =>
    {
        var (host, _) = Caster();
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _), "free before the player casts");
        ActionPacer.NoteHandCast(host, Spell);
        Check.True(FakeHost.Logs.Any(l => l.Contains("the player is casting by hand: AC's own 'Casting " + Spell + "'")), "the run's start logged");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out string why), "a buff waits");
        Check.True(why.Contains("casting by hand"), "and says why: " + why);
        Check.False(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _), "combat waits");
        Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "a plain heal waits");
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _), "an emergency heal still goes");
        Check.True(ActionPacer.GestureHolds(host, "Items", HealNeed.None, out _), "the item managers' moves wait");
        FakeHost.CombatModeRequests.Clear();
        Check.False(host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle peace"), "no stance change into the player's cast");
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "and none sent");
        Check.True(MoveAudit.HeldForAction(host, "Nav", "SetAutoRun(True)", isStop: false), "a nav start is held");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[Pace] Buff waits: the player is casting by hand")), 1, "the buff's wait logged once");

        // A click every 0.4 s keeps the run going; the 0x1D answers are the player's, not breaker evidence.
        for (int i = 0; i < 5; i++)
        {
            _t += 400;
            ActionPacer.NoteHandCast(host, Spell);
            FakeHost.UseDone(TooBusy);
            ActionPacer.MayStart(host, "Buff", HealNeed.None, out _);
        }
        Check.Eq(ActionPacer.HandCastRuns, 1L, "one run");
        Check.Eq(ActionPacer.HandCasts, 6L, "six casts in it");
        Check.Eq(ActionPacer.HandCastRefusals, 5L, "its five refusals counted as the player's");

        // 3 s after the last click the player is done; the server's next 'done' clears the latch.
        _t += ActionPacer.HandCastHoldMs - 1;
        Check.True(ActionPacer.HandCasting(host), "just under 3 s after the last click: still the player's");
        _t += 2;
        FakeHost.UseDone(0);
        Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out string after), "3 s after the last click RynthAi goes on: " + after);
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("the player's casting by hand is over (6 cast(s)")), 1, "the run's end logged once");
        Check.False(MoveAudit.HeldForAction(host, "Nav", "SetAutoRun(True)", isStop: false), "nav may start again");
    });

    // TooBusyVitals' rule kept: a refusal streak with no hand cast is RynthAi's, and stops once.
    private static void OwnStreakStillStops() => Paced(() =>
    {
        var (_, b) = Caster();
        for (int i = 0; i < 4; i++) { FakeHost.UseDone(TooBusy); b.OnChatWindowText("You're too busy!", RynthAiPlugin.AcStatusChatType); }
        Check.Eq(FakeHost.Stops, 1, "4 refusals in a row, no hand cast: one StopCompletely");
        Check.Eq(Streak(b), 4, "the streak counts");
    });
}
