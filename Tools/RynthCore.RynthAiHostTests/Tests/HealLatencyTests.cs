using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Heal latency (2026-10-05): a mage's heals "sometimes fine, sometimes several seconds late".
//  - A war cast's busy count is a leftover once the server's UseDone arrives (AC lowers its own
//    count inline, unseen), and only combat cleared it. With health under a line the arbiter
//    hands the tick to vitals, combat stops running, and the heal waited for the 5 s force-clear.
//  - A healing kit the server refused (too busy, in the air) never gets a result line, so every
//    vital waited the kit's 8 s result timeout.
//  - [HealLatency] logs each episode's delay and what held it.
//  - The activity display says Healing / Restoring mana / Restoring stamina, not "Buffing".
internal static class HealLatencyTests
{
    private const uint Player = 0x50000B01;
    private const uint STypeLife = 33;
    private const uint TooBusy = 0x1D;

    public static void Register(Runner r)
    {
        r.Add("heal latency: a heal goes ahead of a leftover busy count (a war cast the server finished)", HealBeatsLeftoverBusy);
        r.Add("heal latency: a live busy count still holds the heal; a leftover doesn't free buffs or mana", LiveBusyStillHolds);
        r.Add("heal latency: a refused kit ends the kit wait (UseDone error or 'too busy'), Heal Self goes next", KitRefusalEndsWait);
        r.Add("heal latency: Heal Self refused for mana rests 2 s, not the 15 s a buff rests", LowManaHealRest);
        r.Add("heal latency: [HealLatency] logs delay, line and blockers per episode, and a summary", TrackerLines);
        r.Add("heal latency: the heartbeat files a held heal under its blocker", HeartbeatLogsEpisode);
        r.Add("activity label: the Buffing slot shows Healing / Restoring mana / Buffing; control string unchanged", ActivityLabels);
        r.Add("heal latency: an emergency heal goes out during the login buff refresh; buffs still wait for it", EmergencyBeatsLoginRefresh);
    }

    private sealed class Rig
    {
        public DateTime T0 = new(2026, 10, 5, 21, 0, 0);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public readonly List<string> StaleResets = new();

        public Rig(int healthPct = 40, int manaPct = 100, bool lastUseDone = true, bool liveBuffsReady = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.ServerGates = true;
            FakeHost.LastUseDoneCalls = lastUseDone;
            FakeHost.CombatModeValue = CombatMode.Magic;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            var host = FakeHost.Create();
            // No combat manager here, so the idle line applies: Heal At = Top Off HP = 60.
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false,
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
            B.SetStaleBusyCallback(why => StaleResets.Add(why));
            NowAt = T0;
            B.Clock = () => NowAt;
            if (liveBuffsReady) B.MarkLiveBuffsReadyForTests();
        }

        public void At(double seconds) => NowAt = T0.AddSeconds(seconds);
        public bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
        public string? Log(string part) => FakeHost.Logs.LastOrDefault(l => l.Contains(part, StringComparison.Ordinal));
    }

    // Heal Self at any tier (skill 400 casts a high tier with its own name): same category as Heal Self I.
    private static int HealSelfCasts()
    {
        int cat = SpellTable.Named("Heal Self I").First().Category;
        return FakeHost.Casts.Count(c => SpellTable.ById(c.SpellId)?.Category == cat);
    }

    // The mage's case: a war cast went out (busy 0 -> 1 at UseDone #5), the server finished it
    // (UseDone #6, AC lowered its own count unseen), and health dropped under Heal At. Before the
    // fix the heal waited at BusyCount > 0 until CheckBusyTimeout's 5 s force-clear.
    private static void HealBeatsLeftoverBusy()
    {
        var r = new Rig(healthPct: 40);
        FakeHost.UseDoneSeq = 5;
        r.B.BusyCount = 1;            // the war cast's increment
        FakeHost.UseDone(0);          // the server finished it; nothing decrements the plugin's count
        r.At(0.1);
        r.B.OnHeartbeat();
        Check.Eq(HealSelfCasts(), 1, "Heal Self goes out on the first heartbeat, not after the 5 s force-clear");
        Check.Eq(r.B.StaleBusyClears, 1, "the leftover was cleared once");
        Check.Eq(r.B.BusyCount, 0, "and the shared mirror is 0");
        Check.Eq(r.StaleResets.Count, 1, "through the same callback combat uses");
        Check.True(r.StaleResets[0].StartsWith("vitals:", StringComparison.Ordinal), "the reason says it was the heal");
    }

    private static void LiveBusyStillHolds()
    {
        // A count with no UseDone since it rose is real (a swing, a cast still on the server).
        var r = new Rig(healthPct: 40);
        FakeHost.UseDoneSeq = 5;
        r.B.BusyCount = 1;
        r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "no UseDone since the count rose: the heal waits");
        Check.Eq(r.B.StaleBusyClears, 0, "nothing cleared");

        // A gesture still animating: real too, even after a UseDone.
        FakeHost.UseDone(0);
        FakeHost.CastBusy = 1;
        r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "a cast gesture animating: the heal waits");
        FakeHost.CastBusy = 0;
        r.B.OnHeartbeat();
        Check.Eq(HealSelfCasts(), 1, "gesture done, UseDone seen: the heal goes");

        // Health fine, mana low: the leftover rule is for heals only, so Stamina to Mana still waits.
        var m = new Rig(healthPct: 100, manaPct: 10);
        m.S.GetManaAt = 50; m.S.TopOffMana = 50;
        FakeHost.UseDoneSeq = 5;
        m.B.BusyCount = 1;
        FakeHost.UseDone(0);
        m.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "no heal wanted: a leftover count still holds mana and buffs");
        Check.Eq(m.B.BusyCount, 1, "and is left for combat / the watchdog");
    }

    // ACE Healer.cs answers a kit used while busy with UseDone(YoureTooBusy) and no chat result.
    private static void KitRefusalEndsWait()
    {
        // UseDone error.
        var r = new Rig(healthPct: 40);
        FakeHost.UseDoneSeq = 10;
        r.B.MarkKitUsed();
        Check.True(r.B.KitPending(), "kit just used: waiting for its result");
        FakeHost.UseDone(TooBusy);
        Check.False(r.B.KitPending(), "a UseDone error after the kit: refused, no 8 s wait");
        Check.Eq(r.B.KitRefusals, 1, "counted");
        Check.True(r.Logged("kit was refused") && r.Logged("UseDone error 0x1D"), "and logged with the error");
        Check.True(r.B.CheckVitals(), "vitals carry on at once");
        Check.Eq(HealSelfCasts(), 1, "Heal Self is cast (the kit waits a second after a refusal)");

        // UseDone(0) after the kit is not a refusal: the kit is still healing.
        var ok = new Rig(healthPct: 40);
        FakeHost.UseDoneSeq = 10;
        ok.B.MarkKitUsed();
        FakeHost.UseDone(0);
        Check.True(ok.B.KitPending(), "UseDone(0): no refusal, still waiting for the result line");
        Check.Eq(ok.B.KitRefusals, 0, "not counted");

        // No GetLastUseDone on this engine: the "too busy" line ends it.
        var c = new Rig(healthPct: 40, lastUseDone: false);
        c.B.MarkKitUsed();
        c.B.OnChatWindowText("You're too busy!", 26);
        Check.False(c.B.KitPending(), "'You're too busy!' right after the kit: refused");
        Check.Eq(c.B.KitRefusals, 1, "counted");

        // A result line first: a later "too busy" (something else refused) doesn't cut the grace.
        var g = new Rig(healthPct: 40, lastUseDone: false);
        g.B.MarkKitUsed();
        g.B.OnChatWindowText("You heal yourself for 52 Health points.", 0);
        g.B.OnChatWindowText("You're too busy!", 26);
        Check.True(g.B.KitPending(), "result seen: the grace for the vitals still runs");
        Check.Eq(g.B.KitRefusals, 0, "no refusal counted");
    }

    // A mage out of mana mid-fight: "You don't have enough Mana" parked Heal Self for 15 s, so after
    // Stamina to Mana refilled it 2-3 s later the mage still couldn't heal for 12 s more.
    private static void LowManaHealRest()
    {
        var r = new Rig(healthPct: 40, manaPct: 2);
        r.S.GetManaAt = 50; r.S.TopOffMana = 50;
        // Mana Conversion untrained: the rests below. Trained, a recovery vital retries in 200 ms
        // instead (RecoveryRetryTests).
        FakeHost.Skills[(Player, 16)] = (10, 1);
        Check.True(r.B.CheckVitals(), "40% health: Heal Self is cast");
        Check.Eq(HealSelfCasts(), 1, "one Heal Self");
        r.B.OnChatWindowText("You don't have enough Mana to cast this spell.", 0);
        Check.True(r.Logged("not enough mana; resting the family 2s"), "Heal Self rests 2 s");
        Check.Eq(r.B.PendingSpellId, 0, "pending cleared");
        Check.True(r.B.CheckVitals(), "next check: something is done");
        Check.Eq(HealSelfCasts(), 1, "not Heal Self inside the rest...");
        Check.Eq(FakeHost.Casts.Count, 2, "...but Stamina to Mana, so the mana comes back");
        Check.Eq(r.B.BuffingLabel, "Restoring mana", "and the slot reads Restoring mana");
        r.B.OnChatWindowText("You don't have enough Mana to cast this spell.", 0);
        // Stamina to Mana is a vital too (the mana reserve, 2026-10-11): 2 s, not the 15 s a buff rests.
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("not enough mana; resting the family 2s", StringComparison.Ordinal)), 2,
                 "Stamina to Mana rests 2 s as well, not 15 s");
        System.Threading.Thread.Sleep(2100);
        r.V.CurrentMana = 60;
        Check.True(r.B.CheckVitals(), "2 s later, mana back");
        Check.Eq(HealSelfCasts(), 2, "Heal Self is cast again");
    }

    private static void TrackerLines()
    {
        var logs = new List<string>();
        var t = new HealLatencyTracker(logs.Add);
        DateTime t0 = new(2026, 10, 5, 21, 0, 0);
        DateTime At(double s) => t0.AddSeconds(s);

        t.Observe(At(0), 41, "Heal At", 60, inCombat: true, healInFlight: false);
        for (int i = 1; i <= 30; i++)
            t.EndTick(At(i * 0.1), i <= 28 ? "busy count" : "cast gate (a gesture is animating)", "busy 1", "Buffing");
        t.HealSent(At(3.1), "Heal Self");
        string line = logs.Last();
        Check.True(line.StartsWith("[HealLatency] 3100 ms: health 41% under Heal At 60% (in combat) -> Heal Self", StringComparison.Ordinal), $"episode line: {line}");
        Check.True(line.Contains("busy count 2800 ms") && line.Contains("cast gate (a gesture is animating) 200 ms"), "blockers with their time, biggest first");
        Check.Eq(t.Episodes, 1, "one episode");

        // Still under the line: the next episode opens only once the heal has resolved.
        t.Observe(At(3.2), 45, "Heal At", 60, true, healInFlight: true);
        Check.False(t.IsOpen, "heal in flight: no new episode yet");
        t.Observe(At(5.5), 45, "Heal At", 60, true, healInFlight: false);
        Check.True(t.IsOpen, "resolved and still under: a follow-up episode");
        t.EndTick(At(5.6), "cast interval", "", "Combat");
        t.HealSent(At(5.9), "Heal Self");
        Check.True(logs.Last().Contains("400 ms") && logs.Last().Contains("(follow-up)") && logs.Last().Contains("arbiter gave the tick to Combat 100 ms"),
                   $"follow-up measured from when it became possible: {logs.Last()}");

        // Over the line with no heal: logged when it lasted a second or more.
        t.Observe(At(6), 80, "", 0, true, false);
        t.Observe(At(7), 55, "Top Off HP", 95, false, false);
        t.Observe(At(7.5), 96, "", 0, false, false);
        Check.False(logs.Last().Contains("no heal"), "a half-second dip isn't logged");
        t.Observe(At(8), 55, "Top Off HP", 95, false, false);
        t.EndTick(At(8.5), "nothing could heal", "kit: no healing kit", "Navigating");
        t.Observe(At(9.5), 96, "", 0, false, false);
        Check.True(logs.Last().StartsWith("[HealLatency] no heal in 1500 ms", StringComparison.Ordinal) && logs.Last().Contains("nothing could heal"),
                   $"a 1.5 s episode with no heal is: {logs.Last()}");

        // Summary every 10 heals.
        for (int i = 0; i < 8; i++)
        {
            double s0 = 20 + i * 10;
            t.Observe(At(s0), 50, "Heal At", 60, true, false);
            t.EndTick(At(s0 + 0.5), "busy count", "", "Buffing");
            t.HealSent(At(s0 + 0.5), "Heal Self");
        }
        string sum = logs.Last();
        Check.True(sum.StartsWith("[HealLatency] summary of 10 heals:", StringComparison.Ordinal), $"summary line: {sum}");
        Check.True(sum.Contains("max 3100 ms (Heal Self, busy count)") && sum.Contains("2 over 1 s") == false && sum.Contains("1 over 1 s") && sum.Contains("1 over 3 s") && sum.Contains("1 episode(s) with no heal"),
                   $"summary numbers: {sum}");
    }

    private static void HeartbeatLogsEpisode()
    {
        var r = new Rig(healthPct: 40);
        r.S.SpellCastIntervalMs = 0;
        FakeHost.UseDoneSeq = 5;
        r.B.BusyCount = 1;
        r.At(0);
        r.B.OnHeartbeat();            // opens the episode; a live count holds it
        r.At(0.4);
        r.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "held by the live busy count");
        FakeHost.UseDone(0);
        r.At(0.5);
        r.B.OnHeartbeat();
        Check.Eq(HealSelfCasts(), 1, "the UseDone frees it");
        string? line = r.Log("[HealLatency] 500 ms: health 40% under Top Off HP 60% (idle) -> Heal Self");
        Check.NotNull(line, "the episode line is logged with the delay and the line");
        Check.True(line!.Contains("busy count 400 ms"), $"and what held it: {line}");
        Check.Eq(r.B.BuffingLabel, BuffManager.LabelHealing, "while Heal Self is in flight the slot reads Healing");
    }

    // Drakkon, 2026-10-05 13:43:09: "[HealLatency] 5831 ms: health 12% under Emergency Heal At 30%
    // (idle) -> Peerless Healing Kit; held by: login buff refresh 5737 ms". An engine hot reload
    // made RynthAi wait for its login buff refresh, and the refresh held heals too. It holds buffs
    // only now. (The registry read never succeeds here: server time 0, as before a time sync.)
    private static void EmergencyBeatsLoginRefresh()
    {
        var r = new Rig(healthPct: 12, liveBuffsReady: false);
        r.S.EmergencyHealAt = 30;
        r.S.EnableBuffing = true;
        r.At(0);
        r.B.OnHeartbeat();
        Check.False(r.B.LiveBuffsReady, "the login refresh hasn't got a snapshot");
        Check.Eq(HealSelfCasts(), 1, "Heal Self goes out on the first heartbeat anyway");
        string? line = r.Log("[HealLatency]");
        Check.True(line != null && !line.Contains("login buff refresh"), $"the heal was not held by the refresh: {line}");

        // Health fine: the refresh still holds every buff.
        var q = new Rig(healthPct: 100, liveBuffsReady: false);
        q.S.EnableBuffing = true;
        q.S.SpellCastIntervalMs = 0;
        q.B.OnHeartbeat();
        q.B.OnHeartbeat();
        Check.Eq(FakeHost.Casts.Count, 0, "no buff cast before the refresh is ready");
        Check.Eq(FakeHost.Uses.Count, 0, "and no buff item used");
    }

    private static void ActivityLabels()
    {
        Check.Eq(ActivityArbiter.DisplayLabel("Buffing", "Healing"), "Healing", "Buffing slot: its label");
        Check.Eq(ActivityArbiter.DisplayLabel("Buffing", ""), "Buffing", "no label: Buffing");
        Check.Eq(ActivityArbiter.DisplayLabel("Combat", "Healing"), "Combat", "anything else: the control string");
        Check.Eq(ActivityArbiter.DisplayLabel(null, "Healing"), "Default", "empty: Default (the dashboard shows Idle)");
        Check.Eq(ActivityArbiter.ToBotAction(BotActivity.Buffing), "Buffing", "the control string stays Buffing");

        Check.Eq(BuffManager.VitalLabel("Heal Self"), "Healing", "Heal Self");
        Check.Eq(BuffManager.VitalLabel("Stamina to Health Self"), "Healing", "Stamina to Health");
        Check.Eq(BuffManager.VitalLabel("Stamina to Mana Self"), "Restoring mana", "Stamina to Mana");
        Check.Eq(BuffManager.VitalLabel("Revitalize Self"), "Restoring stamina", "Revitalize");

        var mana = new Rig(healthPct: 100, manaPct: 10);
        mana.S.GetManaAt = 50; mana.S.TopOffMana = 50;
        Check.True(mana.B.CheckVitals(), "10% mana: Stamina to Mana is cast");
        Check.Eq(mana.B.BuffingLabel, "Restoring mana", "and the slot reads Restoring mana");

        var heal = new Rig(healthPct: 40);
        Check.Eq(heal.B.BuffingLabel, "Healing", "health under the line before anything ran: Healing");
        heal.B.MarkKitUsed();
        Check.Eq(heal.B.BuffingLabel, "Healing", "a healing kit in flight: Healing");
        var stam = new Rig(healthPct: 100);
        stam.B.MarkKitUsed(stamina: true);
        Check.Eq(stam.B.BuffingLabel, "Restoring stamina", "a stamina kit in flight: Restoring stamina");

        var idle = new Rig(healthPct: 100);
        Check.Eq(idle.B.BuffingLabel, "Buffing", "nothing vital going on: Buffing");
    }
}
