using System;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Cast safeguards after Drakkon's held casts on DreamWeave (2026-10-05,
// ops\overnight\2026-10-05-stuck-cast.md): give-up timed from the incantation, a refusal never
// counts as the server finishing, the first cast after a stance change waits for the stance,
// and a cast the server holds open gets exactly one movement nudge.
//
// BuffManager's cast-safety timing runs on an injectable clock (BuffManager.Clock), so these
// tests move time by hand. The rig is a mage at 10% mana: the only thing CheckVitals can do is
// cast Stamina to Mana Self.
internal static class CastSafetyTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeLife = 33;
    private const uint TooBusy = 0x1D;

    public static void Register(Runner r)
    {
        r.Add("cast safety: a vital's give-up runs from its incantation; from the send without one", GiveUpFromIncantation);
        r.Add("cast safety: give-up time is capped, and never earlier than the send-timed one", GiveUpCap);
        r.Add("cast safety: 'You're too busy!' and a 0x1D UseDone don't count as the server finishing", TooBusyIsNotDone);
        r.Add("cast safety: a result line for another spell doesn't clear the pending cast", ResultForAnotherSpell);
        r.Add("cast safety: the first cast after a stance change waits for Magic plus the settle", StanceWait);
        r.Add("cast safety: a cast held open gets exactly one movement nudge", NudgeOnce);
        // Drakkon's death, 2026-10-05 13:43:20 (ops\overnight\2026-10-05-death-fixes.md).
        r.Add("cast safety: an emergency heal waits for Magic plus 300 ms only; a top-off heal the full 1 s", EmergencyStanceWait);
        r.Add("cast safety: the stance clock starts when the client reads Magic, not at the first cast attempt", StanceClockFromObservation);
        r.Add("cast safety: the 4.1 s settle line: the stance was ready at 1 s, a kit in flight held the cast, and the line says so", StanceLateHolderNamed);
        r.Add("cast safety: a held heal is nudged at 2.8 s without waiting for a refusal, once", UrgentNudgeForHeal);
        r.Add("cast safety: any held cast is nudged early while health is under a heal line; not when health is fine", UrgentNudgeForLowHealth);
        r.Add("cast safety: no kit or potion while a cast is held open until its nudge, then one try per 3 s", HeldCastItemGate);
        r.Add("move audit: one line per call, repeats counted, subsystem in the line", MoveAuditLines);
        r.Add("cast safety: a script heal's 'due to casting' result closes the cast, so no early nudge steps the character forward", TransferResultClosesCast);
        r.Add("cast safety: a script heal takes no early nudge: a 2.75 s result or a 3.8 s UseDone sees no autorun before 6 s", ScriptHealNoEarlyNudge);
        r.Add("cast safety: a script heal's 'resists your spell' closes its cast", ScriptHealResisted);
        r.Add("cast safety: another caster's transfer and the drain line leave the script heal pending", TransferLinesNotOurs);
        r.Add("cast safety: extra 'due to casting' lines after the cast closed resolve nothing and record no timer", ExtraTransferLines);
        r.Add("cast safety: a 'due to casting' line for no open cast is read only when it names the pending spell", StrayTransferLine);
    }

    // 2026-10-10 log (RynthCore.40732.log): the BloodMage script's Vitality Siphon reports
    // "You gain N points of health due to casting Vitality Siphon on <monster>". The result
    // matcher only read "ou cast ", so the heal stayed held and the early heal nudge (autorun on,
    // off) went out 2.8 s after the incantation, mid-cast: 13 of the session's 14 nudges.
    private static void TransferResultClosesCast()
    {
        foreach (bool withResult in new[] { false, true })
        {
            string what = withResult ? "result line read" : "control, no result line";
            try
            {
                var r = StartScriptSiphon(what);
                r.At(0.2); r.Incantation();
                if (withResult)
                {
                    r.At(2.5); r.Chat("You gain 51 points of health due to casting Vitality Siphon on Doorheld Surveyor", 7);
                    Check.Eq(r.B.PendingSpellId, 0, "result line read: the pending heal is resolved");
                    Check.True(r.Logged("via due-to-casting match"), "result line read: the CLEARED line names the branch that matched");
                }
                r.At(3.1); r.B.OnHeartbeat();
                Check.Eq(FakeHost.AutoRunCalls.Count, 0, $"{what}: a script heal takes no early nudge");
                // Held past 6 s while AC refuses: the normal nudge, unless the result closed the cast.
                r.At(7.0); r.Chat("You're too busy!");
                if (withResult)
                    Check.Eq(FakeHost.AutoRunCalls.Count, 0, "result line read: the cast is closed, nothing moves the character");
                else
                    Check.True(FakeHost.AutoRunCalls.SequenceEqual(new[] { true, false }), "control: the unanswered heal is still held at 6.8 s and nudged on the refusal");
            }
            finally { CombatHook.Reset(); }
        }
    }

    private static int SiphonId => SpellTable.Named("Vitality Siphon").Select(x => x.Id).First();

    /// <summary>A rig at 50% health (heal line 60) whose script hook chose Vitality Siphon, sent at 0 s.
    /// The caller resets CombatHook.</summary>
    private static Rig StartScriptSiphon(string what)
    {
        var r = new Rig();
        MoveAudit.Reset();
        CombatHook.Reset();
        r.S.HealAt = 60; r.S.TopOffHP = 60;
        r.V.CurrentHealth = 50; r.V.CurrentMana = 100;
        // The script seam checks the known-spell snapshot (this rig never refreshed it).
        ((SpellManager)typeof(BuffManager).GetField("_spellManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(r.B)!).RefreshKnownSpells();
        CombatHook.Claim(RynthAiCombat.KindHeal, 0, 0, 0, "test");
        r.At(0);
        Check.True(r.B.CheckVitals(), $"{what}: waiting for the script's heal");
        Check.True(CombatHook.Poll(RynthAiCombat.KindHeal, out var req), $"{what}: a heal request is open");
        CombatHook.Decide(req.Seq, RynthAiCombat.KindHeal, (uint)SiphonId, 0, 0);
        r.B.CheckVitals();
        Check.Eq(r.CastName, "Vitality Siphon", $"{what}: the script's Vitality Siphon is cast");
        return r;
    }

    /// <summary>Heartbeats every 100 ms over [from, to] seconds.</summary>
    private static void Tick(Rig r, double from, double to)
    {
        for (int i = 0; from + i * 0.1 <= to + 1e-9; i++) { r.At(from + i * 0.1); r.B.OnHeartbeat(); }
    }

    // 2026-10-10 log: real Vitality Siphon results came 2.75, 2.45 and 2.42 s after the incantation,
    // against the 2.8 s early heal nudge. A script-chosen heal waits the normal 6 s instead.
    private static void ScriptHealNoEarlyNudge()
    {
        try
        {
            // Incantation lag (1.0 s after the send), result line 2.75 s after the incantation.
            var r = StartScriptSiphon("late result");
            r.At(1.0); r.Incantation();
            Tick(r, 1.1, 3.7);
            r.At(3.75); r.Chat("You gain 51 points of health due to casting Vitality Siphon on Doorheld Surveyor", 7);
            Check.Eq(r.B.PendingSpellId, 0, "late result: read at 2.75 s");
            Tick(r, 3.8, 6.9);
            Check.Eq(FakeHost.AutoRunCalls.Count, 0, "late result: no AutoRun call before 6 s after the incantation");

            // No result line at all; the server's UseDone(0) 3.8 s after the incantation ends it.
            var q = StartScriptSiphon("UseDone only");
            q.At(1.0); q.Incantation();
            Tick(q, 1.1, 4.7);
            q.At(4.8); FakeHost.UseDone(0);
            Tick(q, 4.8, 6.9);
            Check.Eq(FakeHost.AutoRunCalls.Count, 0, "UseDone only: no AutoRun call before 6 s after the incantation");
            Check.True(q.Logged("'Vitality Siphon': UseDone #"), "UseDone only: the UseDone closed the cast");
        }
        finally { CombatHook.Reset(); }

        // Control: RynthAi's own Heal Self with no result is still nudged at 2.8 s.
        var h = new Rig();
        MoveAudit.Reset();
        h.S.HealAt = 60; h.S.TopOffHP = 60;
        h.V.CurrentHealth = 50; h.V.CurrentMana = 100;
        h.At(0); h.B.CheckVitals();
        Check.True(CastIs("Heal Self"), "control: Heal Self is cast");
        h.At(0.2); h.Incantation();
        Tick(h, 0.3, 2.9);
        Check.Eq(FakeHost.AutoRunCalls.Count, 0, "control: no nudge 2.7 s after the incantation");
        h.At(3.1); h.B.OnHeartbeat();
        Check.True(FakeHost.AutoRunCalls.SequenceEqual(new[] { true, false }), "control: RynthAi's own heal is nudged at 2.8 s");
    }

    // A resisted transfer prints "<monster> resists your spell" and no "due to casting" line.
    private static void ScriptHealResisted()
    {
        try
        {
            int resolves = 0;
            var r = StartScriptSiphon("resisted");
            r.B.SetCastResolvedCallback(_ => resolves++);
            r.At(0.2); r.Incantation();
            r.At(2.5); r.Chat("Doorheld Surveyor resists your spell", 7);
            Check.Eq(r.B.PendingSpellId, 0, "resisted: the pending heal is resolved");
            Check.Eq(resolves, 1, "resisted: resolved once");
            Check.True(r.Logged("'Vitality Siphon': resisted"), "resisted: [CastTime] closes the cast as resisted");
            r.At(7.0); r.Chat("You're too busy!");
            Check.Eq(FakeHost.AutoRunCalls.Count, 0, "resisted: the cast is closed, so a later refusal sends no nudge");
        }
        finally { CombatHook.Reset(); }

        // RynthAi's own cast is not closed by a resist line (it can't be one of its transfers).
        var h = new Rig();
        h.S.HealAt = 60; h.S.TopOffHP = 60;
        h.V.CurrentHealth = 50; h.V.CurrentMana = 100;
        h.At(0); h.B.CheckVitals();
        int pending = h.B.PendingSpellId;
        h.At(0.2); h.Incantation();
        h.At(1.0); h.Chat("Doorheld Surveyor resists your spell", 7);
        Check.Eq(h.B.PendingSpellId, pending, "own Heal Self: a resist line leaves it pending");
    }

    // Lines that name a transfer but are not the result of our cast.
    private static void TransferLinesNotOurs()
    {
        try
        {
            var r = StartScriptSiphon("not ours");
            int pending = r.B.PendingSpellId;
            Check.True(pending != 0, "the Siphon is pending");
            r.At(0.2); r.Incantation();
            foreach (string line in new[]
            {
                "You lose 12 points of mana due to Zofrit Zefir casting Drain Mana Other II on you",
                // ACE WorldObject_Magic.cs:1370, a fellow's transfer shared with us.
                "You gain 51 points of health due to Zofrit Zefir casting Vitality Siphon on Doorheld Surveyor",
                "With Vitality Siphon you drain 51 points of Health from Doorheld Surveyor.",
            })
            {
                r.At(1.0); r.Chat(line, 7);
                Check.Eq(r.B.PendingSpellId, pending, $"'{line}': pending unchanged");
            }
            r.At(2.5); r.Chat("You gain 51 points of health due to casting Vitality Siphon on Doorheld Surveyor", 7);
            Check.Eq(r.B.PendingSpellId, 0, "our own line still resolves it");
        }
        finally { CombatHook.Reset(); }
    }

    // Crimson Harvest prints several "due to casting" lines for one cast: only the first is its result.
    private static void ExtraTransferLines()
    {
        try
        {
            int resolves = 0;
            var r = StartScriptSiphon("extra lines");
            r.B.SetCastResolvedCallback(_ => resolves++);
            r.At(0.2); r.Incantation();
            r.At(2.5); r.Chat("You gain 51 points of health due to casting Vitality Siphon on Doorheld Surveyor", 7);
            Check.Eq(resolves, 1, "the first line resolves the cast");
            var timers = (System.Collections.IDictionary)typeof(BuffManager)
                .GetField("_ramBuffTimers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(r.B)!;
            timers.Clear();
            r.At(2.6); r.Chat("You gain 20 points of health due to casting Vitality Siphon on Doorheld Surveyor", 7);
            Check.Eq(resolves, 1, "the second line resolves nothing");
            Check.Eq(timers.Count, 0, "the second line records no timer");
        }
        finally { CombatHook.Reset(); }
    }

    private static System.Collections.IDictionary RamTimers(Rig r) => (System.Collections.IDictionary)typeof(BuffManager)
        .GetField("_ramBuffTimers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(r.B)!;

    // A "due to casting" line for no open cast is read only when it names the pending spell.
    private static void StrayTransferLine()
    {
        // Another spell pending (RynthAi's Stamina to Mana); a stray line from a drain attack
        // CombatManager cast (never tracked by BuffManager).
        var r = new Rig();
        int resolves = 0;
        r.B.SetCastResolvedCallback(_ => resolves++);
        r.At(0); r.B.CheckVitals();
        int pending = r.B.PendingSpellId;
        Check.True(pending != 0, "Stamina to Mana is pending");
        r.At(0.3); r.Incantation();
        var timers = RamTimers(r);
        timers.Clear();
        r.At(1.0); r.Chat("You gain 52 points of health due to casting Drain Health Other I on Olthoi Warrior", 7);
        Check.Eq(r.B.PendingSpellId, pending, "stray drain line: pending unchanged");
        Check.Eq(timers.Count, 0, "stray drain line: no timer recorded");
        Check.Eq(resolves, 0, "stray drain line: nothing resolved");

        // The pending spell's own line after its cast already closed (a UseDone came first).
        try
        {
            var q = StartScriptSiphon("closed first");
            int qResolves = 0;
            q.B.SetCastResolvedCallback(_ => qResolves++);
            q.At(0.2); q.Incantation();
            q.At(2.0); FakeHost.UseDone(0); q.B.OnHeartbeat();
            Check.True(q.Logged("'Vitality Siphon': UseDone #"), "closed first: the UseDone closed the cast");
            int qPending = q.B.PendingSpellId;
            Check.True(qPending != 0, "closed first: the Siphon is still pending");
            q.At(2.4); q.Chat("You gain 51 points of health due to casting Vitality Siphon on Doorheld Surveyor", 7);
            Check.Eq(q.B.PendingSpellId, 0, "closed first: the line naming the pending spell still resolves it");
            Check.Eq(qResolves, 1, "closed first: resolved once");
        }
        finally { CombatHook.Reset(); }
    }

    private sealed class Rig
    {
        public DateTime T0 = new(2026, 10, 5, 10, 30, 40);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;

        public Rig(int combatMode = CombatMode.Magic)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.ServerGates = true;
            FakeHost.LastUseDoneCalls = true;
            FakeHost.CombatModeValue = combatMode;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            var host = FakeHost.Create();
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 0, TopOffHP = 0, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false,
                EnableBuffing = false,
                // OnHeartbeat's interval check runs on the real clock: keep it shut so a heartbeat
                // only resolves the pending cast and never casts again.
                SpellCastIntervalMs = 600_000,
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 10 };
            B = new BuffManager(host, S, sm, V);
            B.SetCharacterSkills(skills);
            NowAt = T0;
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();
        }

        public void At(double seconds) => NowAt = T0.AddSeconds(seconds);
        public void Chat(string text, int type = 0) => B.OnChatWindowText(text, type);
        public void Incantation() => Chat("You say, \"Puish Zharil\"", 17);
        public int CastId => FakeHost.Casts.Last().SpellId;
        public string CastName => SpellTable.ById(CastId)?.Name ?? "";
        public bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
    }

    private static void GiveUpFromIncantation()
    {
        // The DreamWeave case: incantation 1.0 s after the send, result 2.3 s after that.
        var r = new Rig();
        r.At(0);
        Check.True(r.B.CheckVitals(), "10% mana: Stamina to Mana is cast");
        Check.Eq(FakeHost.Casts.Count, 1, "one cast");
        int pending = r.B.PendingSpellId;
        Check.True(pending != 0, "the vital is pending");
        r.At(1.0); r.Incantation();
        r.At(2.6); r.B.OnHeartbeat();
        Check.Eq(r.B.PendingSpellId, pending, "2.6 s after the send, 1.6 s after the incantation: still waiting (the old timer gave up at 2.5 s)");
        r.At(3.9); r.B.OnHeartbeat();
        Check.Eq(r.B.PendingSpellId, pending, "2.9 s after the incantation: still waiting");
        r.At(4.1); r.B.OnHeartbeat();
        Check.Eq(r.B.PendingSpellId, 0, "3.1 s after the incantation: given up");
        Check.True(r.Logged("after its incantation") && r.Logged("released, no strike"), "the release line says it was timed from the incantation");

        // No incantation at all: the send-timed 2.5 s as before.
        var q = new Rig();
        q.At(0);
        q.B.CheckVitals();
        int p2 = q.B.PendingSpellId;
        q.At(2.4); q.B.OnHeartbeat();
        Check.Eq(q.B.PendingSpellId, p2, "no incantation, 2.4 s: still waiting");
        q.At(2.6); q.B.OnHeartbeat();
        Check.Eq(q.B.PendingSpellId, 0, "no incantation, 2.6 s: given up from the send");
        Check.True(q.Logged("no incantation in"), "the release line says no incantation came");

        // A healthy result inside the window resolves it, and the time is logged.
        var h = new Rig();
        h.At(0); h.B.CheckVitals();
        h.At(0.3); h.Incantation();
        h.At(2.6); h.Chat($"You cast {h.CastName} on yourself and lose 35 points of stamina and also gain 47 points of mana");
        Check.Eq(h.B.PendingSpellId, 0, "its result clears the pending cast");
        Check.True(h.Logged("[CastTime]") && h.Logged("2.30 s after its incantation (send to incantation 300 ms)"),
            "[CastTime] logs incantation-to-result and send-to-incantation");
    }

    private static void GiveUpCap()
    {
        var t0 = new DateTime(2026, 10, 5);
        var c = new CastTracker.Cast { SentAt = t0 };
        Check.Eq(CastTracker.GiveUpAt(c, 2500), t0.AddMilliseconds(2500), "no incantation: the fallback from the send");
        c.IncantAt = t0.AddMilliseconds(100);
        Check.Eq(CastTracker.GiveUpAt(c, 2500), t0.AddMilliseconds(3100), "incantation at 0.1 s: 3 s after it");
        c.IncantAt = t0.AddMilliseconds(4500);
        Check.Eq(CastTracker.GiveUpAt(c, 2500), t0.AddMilliseconds(6000), "a very late incantation: capped 6 s after the send");
        c.IncantAt = t0.AddMilliseconds(500);
        Check.Eq(CastTracker.GiveUpAt(c, 5000), t0.AddMilliseconds(5000), "armor (5 s from the send): an early incantation never makes it earlier");
    }

    private static void TooBusyIsNotDone()
    {
        // The cast is running on the server (incantation seen).
        var r = new Rig();
        r.At(0); r.B.CheckVitals();
        int pending = r.B.PendingSpellId;
        r.At(0.1);
        Check.True(r.B.IsAwaitingCastResolution(), "just sent: waiting for the server");
        FakeHost.UseDone(0);
        Check.True(r.B.IsAwaitingCastResolution(), "a UseDone(0) before the incantation is some earlier action's, not this cast's (the old wait cleared on it)");
        r.At(0.5); r.Incantation();
        FakeHost.UseDone(TooBusy);
        Check.True(r.B.IsAwaitingCastResolution(), "a 0x1D UseDone is a refusal, not 'done' (the old wait cleared on it)");
        r.At(1.0); r.Chat("You're too busy!");
        Check.True(r.B.IsAwaitingCastResolution(), "'You're too busy!' is not 'done'");
        Check.Eq(r.B.PendingSpellId, pending, "the running cast stays pending: the refusal was for something else");
        r.At(2.0); FakeHost.UseDone(0);
        Check.False(r.B.IsAwaitingCastResolution(), "a UseDone(0) after the incantation: the server finished it");
        Check.True(r.Logged("UseDone #"), "[CastTime] logs the UseDone that finished it");

        // A cast refused before it started: the refusal is not 'done' either; the next UseDone(0)
        // (whatever the server was busy with, finishing) is.
        var q = new Rig();
        q.At(0); q.B.CheckVitals();
        q.At(0.3); q.Chat("You're too busy!");
        Check.Eq(q.B.PendingSpellId, 0, "a cast refused before its incantation is parked, as before");
        FakeHost.UseDone(TooBusy);
        Check.True(q.B.IsAwaitingCastResolution(), "refused: still waiting for the server to be free");
        q.At(1.0); FakeHost.UseDone(0);
        Check.False(q.B.IsAwaitingCastResolution(), "a UseDone(0) after the refusal: the server is free");

        // The cast's own result line ends the wait.
        var s = new Rig();
        s.At(0); s.B.CheckVitals();
        s.At(0.4); s.Incantation();
        s.At(2.5); s.Chat($"You cast {s.CastName} on yourself and lose 18 points of stamina and also gain 24 points of mana");
        Check.False(s.B.IsAwaitingCastResolution(), "its result line: done");

        // Mana and stamina casts wait for the server too (heals don't).
        var w = new Rig();
        w.At(0); w.B.CheckVitals();
        w.At(0.4); w.Incantation();
        w.At(3.5); w.B.OnHeartbeat();   // pending released (incantation + 3 s)
        Check.Eq(w.B.PendingSpellId, 0, "released");
        int before = FakeHost.Casts.Count;
        w.At(3.6);
        Check.True(w.B.CheckVitals(), "past the give-up the next Stamina to Mana may go");
        Check.Eq(FakeHost.Casts.Count, before + 1, "and it is cast");
    }

    private static void ResultForAnotherSpell()
    {
        var r = new Rig();
        r.At(0); r.B.CheckVitals();
        int pending = r.B.PendingSpellId;
        r.At(0.2); r.Chat("You cast Strength Self V on yourself, refreshing Strength Self V");
        Check.Eq(r.B.PendingSpellId, pending, "a late result for Strength Self V doesn't clear the pending Stamina to Mana");
        Check.True(r.Logged("pending kept"), "and says so");
        r.At(1.0); r.Chat($"You cast {r.CastName} on yourself and lose 35 points of stamina and also gain 47 points of mana");
        Check.Eq(r.B.PendingSpellId, 0, "its own result does");
    }

    private static void StanceWait()
    {
        // Peace mode, no wand listed: the bare-handed switch to Magic.
        var r = new Rig(CombatMode.NonCombat);
        r.At(0);
        Check.True(r.B.CheckVitals(), "out of Magic: the vital yields while the stance changes");
        Check.Eq(FakeHost.Casts.Count, 0, "no cast before the stance");
        FakeHost.CombatModeValue = CombatMode.Magic;   // the client field flips 60 ms later
        r.At(0.06);
        Check.True(r.B.CheckVitals(), "client reads Magic: still yielding");
        Check.Eq(FakeHost.Casts.Count, 0, "no cast 60 ms after the stance request");
        r.At(0.55);
        r.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 0, "no cast at 0.55 s (when the first hung cast went out)");
        r.At(1.01);
        r.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 1, "cast once the stance has settled (1 s after the request, 300 ms after Magic)");
        Check.True(r.Logged("Magic stance settled"), "the settle is logged");

        // Already in Magic, no stance change from us: no wait.
        var q = new Rig();
        q.At(0); q.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 1, "already in Magic: cast at once");
    }

    private static void NudgeOnce()
    {
        var r = new Rig();
        MoveAudit.Reset();
        r.At(0); r.B.CheckVitals();
        string name = r.CastName;
        r.At(0.3); r.Incantation();
        r.At(5.0); r.Chat("You're too busy!");
        Check.Eq(FakeHost.AutoRunCalls.Count, 0, "4.7 s after the incantation: no nudge yet");
        r.At(6.5); r.Chat("You're too busy!");
        Check.True(FakeHost.AutoRunCalls.SequenceEqual(new[] { true, false }), "held 6.2 s and still refused: one nudge, autorun on then off");
        Check.Eq(r.B.CastNudges, 1, "one nudge counted");
        Check.True(r.Logged("[CastSafe]") && r.Logged("Buff/Nudge SetAutoRun(True)"), "the nudge is logged, with its subsystem");
        r.At(8.0); r.Chat("You're too busy!");
        r.At(12.0); r.Chat("You're too busy!");
        r.B.OnHeartbeat();
        Check.Eq(FakeHost.AutoRunCalls.Count, 2, "more refusals: no second nudge for the same held cast");
        Check.True(r.Logged("HELD"), "the held cast is logged once it passes 6 s");
        r.At(13.0); r.Chat($"You cast {name} on yourself and lose 97 points of stamina and also gain 131 points of mana");
        Check.True(r.Logged("(after the movement nudge)"), "its late result is logged as coming after the nudge");

        // Macro off: no nudge.
        var q = new Rig();
        q.At(0); q.B.CheckVitals();
        q.At(0.3); q.Incantation();
        q.S.IsMacroRunning = false;
        q.At(7.0); q.Chat("You're too busy!");
        Check.Eq(FakeHost.AutoRunCalls.Count, 0, "macro off: nothing moves the character");
    }

    // The same spell at any tier (skill 400 casts a high tier with its own name, e.g. Adja's Intervention).
    private static bool CastIs(string baseName)
    {
        if (FakeHost.Casts.Count == 0) return false;
        int cat = SpellTable.Named(baseName + " I").First().Category;
        return SpellTable.ById(FakeHost.Casts.Last().SpellId)?.Category == cat;
    }

    // Drakkon at 27% under Emergency Heal At 30%: Heal Self waited the full stance settle
    // (at least 1 s after the request). An emergency heal now waits only for Magic plus 300 ms.
    private static void EmergencyStanceWait()
    {
        var r = new Rig(CombatMode.NonCombat);
        r.S.EmergencyHealAt = 30; r.S.HealAt = 60; r.S.TopOffHP = 60;
        r.V.CurrentHealth = 20;
        r.At(0);
        Check.True(r.B.CheckVitals(), "20% health out of Magic: Heal Self yields while the stance changes");
        Check.Eq(FakeHost.Casts.Count, 0, "no cast before the stance");
        FakeHost.CombatModeValue = CombatMode.Magic;
        r.At(0.06); r.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 0, "the client reads Magic: the 300 ms settle still runs");
        r.At(0.30); r.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 0, "240 ms after Magic: still settling");
        r.At(0.37); r.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 1, "310 ms after Magic (370 ms after the request): Heal Self goes, no 1 s floor");
        Check.True(CastIs("Heal Self"), "and it is Heal Self");
        Check.True(r.Logged("emergency heal"), "the stance lines say it was an emergency heal");
        Check.Eq(r.B.UrgentStanceSettles, 1, "counted");

        // A top-off heal (idle, nothing in range): the full settle, as for a buff.
        var q = new Rig(CombatMode.NonCombat);
        q.S.EmergencyHealAt = 30; q.S.HealAt = 60; q.S.TopOffHP = 60;
        q.V.CurrentHealth = 50;
        q.At(0); q.B.CheckVitals();
        FakeHost.CombatModeValue = CombatMode.Magic;
        q.At(0.06); q.B.CheckVitals();
        q.At(0.5); q.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 0, "50% under Top Off HP: no cast at 0.5 s");
        q.At(1.01); q.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Count, 1, "cast once 1 s has passed since the request");
        Check.Eq(q.B.UrgentStanceSettles, 0, "not an emergency");
    }

    // The settle clock started at the first StanceSettled CALL in Magic, so any gate in front of
    // it (a busy count here) pushed the 300 ms back. It now starts when a heartbeat sees Magic.
    private static void StanceClockFromObservation()
    {
        var r = new Rig(CombatMode.NonCombat);
        r.S.SpellCastIntervalMs = 0;
        r.S.EmergencyHealAt = 30; r.S.HealAt = 60; r.S.TopOffHP = 60;
        r.V.CurrentHealth = 20;
        r.At(0); r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "stance requested");
        FakeHost.CombatModeValue = CombatMode.Magic;
        FakeHost.UseDoneSeq = 5;
        r.B.BusyCount = 1;                 // a live busy count: the vitals step doesn't run
        r.At(0.1); r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "held by the busy count, but the heartbeat saw Magic");
        FakeHost.UseDone(0);               // the server finished it: the count is a leftover now
        r.At(0.45); r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 1, "Magic seen at 0.1 s + 300 ms: Heal Self at 0.45 s (the old clock started at 0.45 s)");
    }

    // 13:43:12.67 stance request, 13:43:13.10 the client reads Magic (ready at 13.67), 13:43:13.56 a
    // healing kit, 13:43:16.02 its UseDone, 13:43:16.77 Heal Self: "first cast 4097 ms after the
    // stance request". The stance had settled; the kit in flight (KitPending: its result line plus
    // a 700 ms grace) held the spell. The settle line now says when it was ready and what held it.
    private static void StanceLateHolderNamed()
    {
        var r = new Rig(CombatMode.NonCombat);
        r.S.SpellCastIntervalMs = 0;
        r.S.HealAt = 60; r.S.TopOffHP = 60;
        r.V.CurrentHealth = 50;
        r.At(0); r.B.OnHeartbeat();
        FakeHost.CombatModeValue = CombatMode.Magic;
        r.At(0.4); r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "settling");
        r.At(0.9); r.B.MarkKitUsed();      // a healing kit goes out during the wait
        r.At(1.2); r.B.OnHeartbeat();
        r.At(2.0); r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "the kit in flight holds the spell past the stance's ready time");
        r.Chat("You heal yourself for 10 Health points.");
        System.Threading.Thread.Sleep(750);   // the kit's grace runs on the real clock
        r.At(3.1); r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 1, "the kit landed: Heal Self goes");
        string? line = FakeHost.Logs.LastOrDefault(l => l.Contains("Magic stance settled", StringComparison.Ordinal));
        Check.NotNull(line, "the settle is logged");
        Check.True(line != null && line.Contains("first cast 3100 ms after the stance request")
                   && line.Contains("the stance was ready 1000 ms after the request and the cast then waited 2100 ms for: healing kit in flight"),
                   $"and says the stance was ready at 1 s and the kit held the cast: {line}");
    }

    private static void UrgentNudgeForHeal()
    {
        var r = new Rig();
        MoveAudit.Reset();
        r.S.HealAt = 60; r.S.TopOffHP = 60;
        r.V.CurrentHealth = 50;
        r.At(0); r.B.CheckVitals();
        Check.True(CastIs("Heal Self"), "50% health: Heal Self is cast");
        r.At(0.2); r.Incantation();
        r.At(2.7); r.B.OnHeartbeat();
        Check.Eq(FakeHost.AutoRunCalls.Count, 0, "2.5 s after the incantation (a healthy cast takes 2.2-2.5 s): no nudge");
        r.At(3.1); r.B.OnHeartbeat();
        Check.True(FakeHost.AutoRunCalls.SequenceEqual(new[] { true, false }), "2.9 s with no result: one nudge, no refusal needed");
        Check.Eq(r.B.UrgentNudges, 1, "counted as an early nudge");
        Check.True(r.Logged("it is a heal: nudged at 2.8 s, not 6 s"), "the line says why it was early");
        r.At(4.0); r.Chat("You're too busy!");
        r.At(7.0); r.Chat("You're too busy!");
        r.B.OnHeartbeat();
        Check.Eq(FakeHost.AutoRunCalls.Count, 2, "still once per held cast");
    }

    private static void UrgentNudgeForLowHealth()
    {
        // 20% health under Emergency Heal At, nothing that can heal: Stamina to Mana is what's held.
        var r = new Rig();
        r.S.EmergencyHealAt = 30;
        r.V.CurrentHealth = 20;
        r.At(0); r.B.CheckVitals();
        Check.True(CastIs("Stamina to Mana Self"), "Stamina to Mana is cast");
        r.At(0.3); r.Incantation();
        r.At(3.2); r.B.OnHeartbeat();
        Check.True(FakeHost.AutoRunCalls.SequenceEqual(new[] { true, false }), "held 2.9 s while health is critical: nudged");
        Check.True(r.Logged("health 20% is under a heal line"), "and says why");

        // Health fine: the 6 s rule (on a refusal) as before.
        var q = new Rig();
        q.At(0); q.B.CheckVitals();
        q.At(0.3); q.Incantation();
        q.At(3.2); q.B.OnHeartbeat();
        q.At(5.0); q.B.OnHeartbeat();
        Check.Eq(FakeHost.AutoRunCalls.Count, 0, "health fine: no early nudge");
    }

    // ACE refuses a kit or potion while the player is busy, and every kit Drakkon used while a
    // Heal Self was held open came back 0x1D.
    private static void HeldCastItemGate()
    {
        var r = new Rig();
        r.At(0); r.B.CheckVitals();
        string name = r.CastName;
        r.At(0.3); r.Incantation();
        r.At(2.0);
        Check.False(r.B.HeldCastBlocksItemUse(out _), "inside the normal result window: no gate (the vital is still pending)");
        r.At(3.5);
        Check.True(r.B.HeldCastBlocksItemUse(out string why), "held 3.2 s, not nudged yet: no kit or potion");
        Check.True(why.Contains("nudge first"), $"why: {why}");
        r.At(6.5); r.Chat("You're too busy!");
        Check.Eq(FakeHost.AutoRunCalls.Count, 2, "the refusal at 6.2 s nudges it");
        r.At(6.6);
        Check.False(r.B.HeldCastBlocksItemUse(out _), "after the nudge: one kit or potion try");
        r.At(7.0);
        Check.True(r.B.HeldCastBlocksItemUse(out why), "the next try waits");
        Check.True(why.Contains("next kit/potion try in"), $"why: {why}");
        r.At(9.7);
        Check.False(r.B.HeldCastBlocksItemUse(out _), "3 s later: another try");
        r.At(10.0); r.Chat($"You cast {name} on yourself and lose 35 points of stamina and also gain 47 points of mana");
        Check.False(r.B.HeldCastBlocksItemUse(out _), "the cast resolved: no gate");
        Check.Eq(r.B.HeldItemSkips, 2, "two attempts skipped");
    }

    private static void MoveAuditLines()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        UseAudit.Reset("RynthAi", null);
        MoveAudit.Reset();
        long now = 1000;
        var oldClock = MoveAudit.NowMs;
        MoveAudit.NowMs = () => now;
        try
        {
            host.SetAutoRunBy("Nav", true);
            host.SetAutoRunBy("Nav", true);    // a repeat 0 ms later: counted
            now += 100;
            host.TurnToHeadingBy("Nav", 10f);
            now += 30; host.TurnToHeadingBy("Nav", 12f);
            now += 30; host.TurnToHeadingBy("Nav", 14f);
            now += 30; host.SetAutoRunBy("Meta", false);
            var lines = FakeHost.Logs.Where(l => l.StartsWith("[Move]", StringComparison.Ordinal)).ToList();
            Check.Eq(lines.Count, 3, "three lines: autorun, the first turn (carrying the autorun repeat), the meta stop (carrying the turns)");
            if (lines.Count == 3)
            {
                Check.Eq(lines[0], "[Move] RynthAi/Nav SetAutoRun(True)", "plugin/subsystem and the call");
                Check.True(lines[1].StartsWith("[Move] RynthAi/Nav TurnToHeading(10.0) (+1 more SetAutoRun(True)", StringComparison.Ordinal), $"the repeat is counted: {lines[1]}");
                Check.True(lines[2].StartsWith("[Move] RynthAi/Meta SetAutoRun(False) (+2 more TurnToHeading, last 14.0", StringComparison.Ordinal), $"turns collapse whatever the angle: {lines[2]}");
            }
            Check.Eq(FakeHost.AutoRunCalls.Count, 3, "every call still reaches the engine");
            Check.Eq(FakeHost.Turns, 3, "every turn still reaches the engine");

            now += 2000; host.SetAutoRunBy("Meta", false);
            Check.Eq(FakeHost.Logs.Count(l => l.StartsWith("[Move]", StringComparison.Ordinal)), 4, "the same call after the 1 s window gets its own line");
        }
        finally { MoveAudit.NowMs = oldClock; }
    }
}
