using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Recovery retry: Stamina to Mana at about 10 mana took a long time to get off. ACE rolls Mana
// Conversion on every attempt (Creature_Magic.GetManaCost, at least 1 mana when trained) before the
// windup, and a refusal (UseDone 0x401) costs nothing. RynthAi parked the family 15 s after each
// refusal. Recovery vitals now retry every RecoveryRetryMs (200), at most 5 a second and 20 refusals
// in a row; everything else keeps the old rest.
internal static class RecoveryRetryTests
{
    private const uint Player = 0x50000B21;
    private const uint STypeLife = 33;
    private const uint STypeManaConversion = 16;
    private const uint NotEnoughMana = 0x401;
    private const string RefusalLine = "You don't have enough Mana to cast this spell.";

    public static void Register(Runner r)
    {
        r.Add("recovery retry: with spell costs unknown, Stamina to Mana goes out at 1 mana and a refusal is retried", PrecheckAllowsBelowBaseCost);
        r.Add("recovery retry: with spell costs known, the mana reserve holds Stamina to Mana under its cost (no cast, no episode); Heal Self still retries", CostKnownHoldsStaminaToMana);
        r.Add("recovery retry: a mana refusal is retried after 200 ms, past Spell Interval; the episode line counts the tries", RetryCadence);
        r.Add("recovery retry: 20 refusals in a row end it (the vital's 2 s rest); stamina too low ends it; untrained Mana Conversion never starts it", RetryCap);
        r.Add("recovery retry: at most 5 recovery casts in any second (Heal Self and Stamina to Mana alternating)", RateCeiling);
        r.Add("recovery retry: an episode with no refusal for RecoveryEpisodeStaleMs (7 s) ends; it no longer lets casts past Spell Interval", StaleEpisodeEnds);
        r.Add("recovery retry: RecoveryRetryMs at its 5000 ms maximum keeps its episode between retries", SlowRetryKeepsEpisode);
    }

    // With RecoveryRetryMs at the slider's 5000 ms maximum the retry (plus a little pacing) must not
    // come after the stale limit, or the episode would end itself between retries.
    private static void SlowRetryKeepsEpisode()
    {
        var r = new Rig(manaPct: 10);
        r.S.RecoveryRetryMs = 5000;
        r.S.SpellCastIntervalMs = 0;
        r.At(0); r.B.OnHeartbeat();
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "Stamina to Mana goes out");
        r.Refuse();
        r.At(5.05); r.B.OnHeartbeat();        // the retry, 50 ms late
        Check.Eq(CastsOf("Stamina to Mana Self"), 2, "the retry at 5.05 s");
        Check.True(r.B.RecoveryRetryOpen, "the episode is still open");
        Check.False(r.Logged("stopped retrying"), "not ended between retries: " + r.B.LastRecoverySummary);
    }

    // The vital stops being needed (mana regenerated) and no refusal comes again: the open episode
    // must end, or it would let every cast past Spell Interval for good.
    private static void StaleEpisodeEnds()
    {
        var r = new Rig(manaPct: 10);
        r.S.SpellCastIntervalMs = 60_000;    // real clock: nothing else gets past it in this test
        r.At(0); r.B.OnHeartbeat();
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "Stamina to Mana goes out");
        r.Refuse();
        Check.True(r.B.RecoveryRetryOpen, "an episode opens");
        r.V.CurrentMana = 100;               // no longer needed
        r.At(BuffManager.RecoveryEpisodeStaleMs / 1000.0 + 0.1); r.B.OnHeartbeat();
        Check.False(r.B.RecoveryRetryOpen, "no refusal for RecoveryEpisodeStaleMs: the episode ends");
        Check.True(r.B.LastRecoverySummary.Contains("no refusal for 7 s"), "summary: " + r.B.LastRecoverySummary);
        r.V.CurrentHealth = 40;              // a heal is needed now
        int before = FakeHost.Casts.Count;
        r.At(BuffManager.RecoveryEpisodeStaleMs / 1000.0 + 1); r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, before, "the heal waits for Spell Interval (the ended episode lets nothing past)");
    }

    private sealed class Rig
    {
        public DateTime T0 = new(2026, 10, 10, 18, 0, 0);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;

        public Rig(int healthPct = 100, int manaPct = 10, bool manaConversion = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.ServerGates = true;
            FakeHost.LastUseDoneCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            FakeHost.Skills[(Player, STypeManaConversion)] = manaConversion ? (300, 2) : (10, 1);
            var host = FakeHost.Create();
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 50, TopOffMana = 50, RestamAt = 0, TopOffStam = 0, UsePotions = false,
                EnableBuffing = false,
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache
            {
                MaxHealth = 100, CurrentHealth = (uint)healthPct, MaxStamina = 100, CurrentStamina = 100,
                MaxMana = 100, CurrentMana = (uint)manaPct,
            };
            B = new BuffManager(host, S, sm, V);
            B.SetCharacterSkills(skills);
            NowAt = T0;
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();
        }

        public void At(double seconds) => NowAt = T0.AddMilliseconds(Math.Round(seconds * 1000));
        public bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
        public int LogCount(string part) => FakeHost.Logs.Count(l => l.Contains(part, StringComparison.Ordinal));
        public string LastCastName => SpellTable.ById(FakeHost.Casts.Last().SpellId)?.Name ?? "";

        /// <summary>The server's answer to an unaffordable cast: UseDone 0x401, the client's chat line.</summary>
        public void Refuse()
        {
            FakeHost.UseDone(NotEnoughMana);
            B.OnChatWindowText(RefusalLine, 0);
        }
    }

    private static int CastsOf(string baseName)
    {
        int cat = SpellTable.Named(baseName + " I").First().Category;
        return FakeHost.Casts.Count(c => SpellTable.ById(c.SpellId)?.Category == cat);
    }

    private static void PrecheckAllowsBelowBaseCost()
    {
        // This rig leaves spell costs unknown (FakeHost.Reset: ManaCostOverride 0, as with no
        // portal.dat), so the mana reserve's cost check in AttemptVitalCast has nothing to hold on.
        var r = new Rig(manaPct: 1);
        Check.True(r.B.CheckVitals(), "1 mana: the vitals step acts");
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "costs unknown: Stamina to Mana is sent at 1 mana (the server rolls the cost)");

        // Refused at 1 mana: the retry is allowed (the least a trained cast can cost is 1 mana).
        r.Refuse();
        Check.True(r.B.RecoveryRetryOpen, "a retry episode is open at 1 mana");
        Check.True(r.Logged("retrying in 200 ms"), "and the refusal line says so");

        // At 0 mana no cast can be afforded: the old rest.
        var z = new Rig(manaPct: 0);
        z.B.CheckVitals();
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "0 mana: the first try still goes (as before)");
        z.Refuse();
        Check.False(z.B.RecoveryRetryOpen, "0 mana: no retry episode");
        Check.True(z.Logged("resting the family 2s") && z.Logged("(no mana at all)"), "the vital's 2 s rest, and why");
    }

    private static void CostKnownHoldsStaminaToMana()
    {
        // Costs known (level x 10 here, as ManaReserveTests): at 5 mana even Stamina to Mana I (10)
        // is unaffordable, so AttemptVitalCast's reserve check sends nothing and no episode opens.
        try
        {
            var r = new Rig(manaPct: 5);
            BuffManager.ManaCostOverride = id => (SpellTableStub.GetById(id)?.Level ?? 0) * 10;
            r.At(0); r.B.CheckVitals();
            r.At(0.2); r.B.CheckVitals();
            Check.Eq(CastsOf("Stamina to Mana Self"), 0, "under its cost: Stamina to Mana is not cast");
            Check.False(r.B.RecoveryRetryOpen, "and no retry episode opens");

            // Heal Self is tried below its cost (60 here) and a refusal is still retried.
            var h = new Rig(healthPct: 40, manaPct: 5);
            BuffManager.ManaCostOverride = id => (SpellTableStub.GetById(id)?.Level ?? 0) * 10;
            h.At(0); h.B.CheckVitals();
            Check.Eq(CastsOf("Heal Self"), 1, "Heal Self goes out under its cost");
            h.Refuse();
            Check.True(h.B.RecoveryRetryOpen, "refused for mana: a retry episode");
            h.At(0.2); h.B.CheckVitals();
            Check.Eq(CastsOf("Heal Self"), 2, "retried after 200 ms");
            Check.Eq(CastsOf("Stamina to Mana Self"), 0, "Stamina to Mana still held under its cost");
        }
        finally { BuffManager.ManaCostOverride = _ => 0; }
    }

    private static void RetryCadence()
    {
        var r = new Rig(manaPct: 10);
        r.At(0); r.B.OnHeartbeat();
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "Stamina to Mana goes out");
        r.Refuse();
        Check.False(r.Logged("resting the family"), "no family rest");

        r.At(0.1); r.B.OnHeartbeat();
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "100 ms after the refusal: still waiting");
        r.At(0.2); r.B.OnHeartbeat();
        Check.Eq(CastsOf("Stamina to Mana Self"), 2, "200 ms: tried again (Spell Interval 400 on the real clock does not hold it)");
        r.Refuse();
        r.At(0.3); r.B.OnHeartbeat();
        Check.Eq(CastsOf("Stamina to Mana Self"), 2, "300 ms: waiting");
        r.At(0.4); r.B.OnHeartbeat();
        Check.Eq(CastsOf("Stamina to Mana Self"), 3, "400 ms: the third try");

        // A good roll: the result line ends the episode with its summary.
        r.At(0.55);
        r.B.OnChatWindowText($"You cast {r.LastCastName} on yourself and lose 35 points of stamina and also gain 47 points of mana", 0);
        Check.False(r.B.RecoveryRetryOpen, "the result closes the episode");
        Check.True(r.B.LastRecoverySummary.Contains("went through after 3 attempts (2 refused for mana") && r.B.LastRecoverySummary.Contains("in 550 ms"),
            $"summary: {r.B.LastRecoverySummary}");

        // The retry gap is a setting.
        var s = new Rig(manaPct: 10);
        s.S.RecoveryRetryMs = 1000;
        s.At(0); s.B.CheckVitals(); s.Refuse();
        s.At(0.9); s.B.CheckVitals();
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "RecoveryRetryMs 1000: nothing at 900 ms");
        s.At(1.0); s.B.CheckVitals();
        Check.Eq(CastsOf("Stamina to Mana Self"), 2, "RecoveryRetryMs 1000: the retry at 1 s");
        // ...clamped to 200 at the bottom.
        var c = new Rig(manaPct: 10);
        c.S.RecoveryRetryMs = 50;
        c.At(0); c.B.CheckVitals(); c.Refuse();
        c.At(0.1); c.B.CheckVitals();
        Check.Eq(CastsOf("Stamina to Mana Self"), 1, "RecoveryRetryMs 50 is clamped: nothing at 100 ms");
        c.At(0.2); c.B.CheckVitals();
        Check.Eq(CastsOf("Stamina to Mana Self"), 2, "...the retry at 200 ms");
    }

    private static void RetryCap()
    {
        var r = new Rig(manaPct: 10);
        for (int i = 0; i < 25; i++)
        {
            r.At(i * 0.2);
            int before = FakeHost.Casts.Count;
            r.B.CheckVitals();
            if (FakeHost.Casts.Count == before) break;
            r.Refuse();
        }
        Check.Eq(CastsOf("Stamina to Mana Self"), 20, "20 tries, then it stops");
        Check.True(r.B.LastRecoverySummary.Contains("after 20 mana refusals") && r.B.LastRecoverySummary.Contains("(20 refusals in a row)"),
            $"summary: {r.B.LastRecoverySummary}");
        Check.Eq(r.LogCount("resting the family 2s"), 1, "the 20th refusal rests the family 2 s, as before");
        r.At(4.2); r.B.CheckVitals();
        Check.Eq(CastsOf("Stamina to Mana Self"), 20, "no try inside the rest");

        // Stamina too low to convert when the refusal comes: the old rest.
        var st = new Rig(manaPct: 10);
        st.V.CurrentStamina = 20;
        st.B.CheckVitals();
        st.V.CurrentStamina = 10;
        st.Refuse();
        Check.False(st.B.RecoveryRetryOpen, "stamina 10%: no retry");
        Check.True(st.B.LastRecoverySummary.Contains("stamina 10% too low to convert") && st.Logged("resting the family 2s"),
            $"summary: {st.B.LastRecoverySummary}");

        // Mana Conversion untrained: the cost is never rolled, so no retry (the vital's 2 s rest).
        var u = new Rig(manaPct: 10, manaConversion: false);
        u.B.CheckVitals();
        u.Refuse();
        Check.False(u.B.RecoveryRetryOpen, "untrained: no retry");
        Check.True(u.Logged("resting the family 2s") && u.B.LastRecoverySummary.Contains("Mana Conversion not trained"), "the old rest, and why");
    }

    private static void RateCeiling()
    {
        // 40% health and 2% mana: Heal Self and Stamina to Mana each retry every 200 ms, which would
        // be 10 casts a second. Two vitals checks per 100 ms, every cast refused.
        var r = new Rig(healthPct: 40, manaPct: 2);
        var sentAt = new List<double>();
        for (int step = 0; step < 30; step++)
        {
            double t = step * 0.1;
            r.At(t);
            for (int k = 0; k < 2; k++)
            {
                int before = FakeHost.Casts.Count;
                r.B.CheckVitals();
                if (FakeHost.Casts.Count == before) continue;
                sentAt.Add(t);
                r.Refuse();
            }
        }
        Check.True(CastsOf("Heal Self") > 0 && CastsOf("Stamina to Mana Self") > 0, "both spells were tried");
        int worst = 0;
        foreach (double a in sentAt)
            worst = Math.Max(worst, sentAt.Count(b => b >= a && b < a + 1.0 - 1e-9));
        Check.True(worst <= BuffManager.RecoveryRetryMaxPerSecond, $"at most 5 in any second (worst {worst}, sent at {string.Join(",", sentAt.Select(x => x.ToString("0.0")))})");
        Check.True(worst == BuffManager.RecoveryRetryMaxPerSecond, $"and the ceiling is reached, so it is what held them (worst {worst})");
        Check.True(r.B.RecoveryRateHolds > 0, "the ceiling held some");
    }
}
