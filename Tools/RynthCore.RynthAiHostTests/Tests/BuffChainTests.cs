using System;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Buff cast chaining (Combat/BuffCastChain.cs). In a buff pass the next buff waited
// ~550 ms after the server's UseDone for the last one, held by the animation gate while the client
// played the recoil ("[Pace] Buff waits: the character is still animating"). On ACE the server
// takes the next cast right after that UseDone. The fast path skips the animation gate only, only
// right after our own buff's UseDone(0), and a refusal turns it off for the rest of the pass.
// FakeHost.CastBusy = 1 stands for "the client is still animating the recoil".
internal static class BuffChainTests
{
    private const uint Player = 0x50000C01;
    private const uint Corpse = 0x80068ACF;
    private const uint STypeLife = 33;
    private const uint TooBusy = 0x1D;

    public static void Register(Runner r)
    {
        r.Add("buff chain: the next buff goes on the last buff's UseDone(0) while the recoil animates; setting off = waits", NextBuffOnUseDone);
        r.Add("buff chain: the animation gate is kept after a non-cast action, after a UseDone error, and past the window", GateKept);
        r.Add("buff chain: a refused fast-path cast turns it off for the rest of the pass; refusals counted and summed up", RefusalFallsBack);
        r.Add("buff chain: FastBuffChain defaults on, loads on from an old profile and copies", SettingDefaults);
        r.Add("buff chain: no Stamina to Mana for the mana reserve on the fast path; it waits for the animation, then goes", ManaReserveWaitsForAnimation);
        r.Add("buff chain: a stop or turn after the last buff sends the next one through the animation gate (no buff 50 ms after a nav stop); no movement = fast path", MovementWaitsForAnimation);
        r.Add("top-off speed: out of a fight the next Heal Self goes on the last one's UseDone(0); in a fight or with the setting off it waits for the animation", VitalChainsOnUseDone);
        r.Add("top-off speed: on the vital fast path only a cast goes; a kit waits for the animation, then goes", VitalFastPathCastOnly);
        r.Add("top-off speed: a buff never chains on a vital's UseDone, a vital never on a buff's; a refused fast vital turns the vital path off 60 s", VitalChainRules);
        r.Add("top-off speed: the kit wait ends when the vitals show the line's heal, else 300 ms after the line (was 700 ms)", KitWaitEndsOnVitals);
    }

    // 2026-11-20: Heal Self, Revitalize and Stamina to Mana in the between-fights top-off each waited for the
    // client's recoil animation after the server's UseDone (200-750 ms), on top of the server's 2.2 s cast and
    // 1.0 s recoil. Out of a fight the next vital cast now goes on our own vital's UseDone(0), as buffs do.
    private static void VitalChainsOnUseDone() => Paced(() =>
    {
        var r = HealRig();
        r.Tick(0);
        Check.Eq(FakeHost.Casts.Count, 1, "the first Heal Self goes");
        Check.True(r.LastCast.StartsWith("Heal Self", StringComparison.Ordinal), $"it is Heal Self ({r.LastCast})");
        r.ServerRuns(0);
        r.Tick(1.55);   // 50 ms after the UseDone, the recoil still animating
        Check.Eq(FakeHost.Casts.Count, 2, "out of a fight: the next Heal Self goes on the UseDone(0)");
        Check.True(r.LastCast.StartsWith("Heal Self", StringComparison.Ordinal), $"and it is Heal Self ({r.LastCast})");
        Check.Eq(r.B.Chain.VitalFastCasts, 1, "one vital on the fast path");
        Check.Eq(r.B.Chain.FastCasts, 0, "no buff counted");
        Check.True(Logged("[BuffChain] vital '" + r.LastCast + "' goes without waiting for the animation"), "logged");

        // In a fight (Heal At): unchanged, the heal waits for the animation.
        var f = HealRig();
        f.B.InCombatOverride = () => true;
        f.Tick(0);
        Check.Eq(FakeHost.Casts.Count, 1, "in a fight: the first heal goes");
        f.ServerRuns(0);
        for (double s = 1.55; s < 2.05; s += 0.1) f.Tick(s);
        Check.Eq(FakeHost.Casts.Count, 1, "in a fight: no heal while the recoil animates");
        FakeHost.CastBusy = 0;
        f.Tick(2.1);
        Check.Eq(FakeHost.Casts.Count, 2, "in a fight: the heal goes once the animation is over");
        Check.Eq(f.B.Chain.VitalFastCasts, 0, "in a fight: nothing on the fast path");
        Check.Eq(f.B.Chain.VitalCasts, 0, "in a fight: the chain records nothing");

        // The setting off: as before.
        var q = HealRig(fast: false);
        q.Tick(0);
        q.ServerRuns(0);
        for (double s = 1.55; s < 2.05; s += 0.1) q.Tick(s);
        Check.Eq(FakeHost.Casts.Count, 1, "setting off: no heal while the recoil animates");
        FakeHost.CastBusy = 0;
        q.Tick(2.1);
        Check.Eq(FakeHost.Casts.Count, 2, "setting off: it goes once the animation is over");
        Check.Eq(q.B.Chain.VitalFastCasts, 0, "setting off: nothing on the fast path");
    });

    // The fast tick skips the animation gate for a cast only: a kit (or potion, or peace switch) that is the
    // chain's first choice waits for the animation instead of going during the recoil, and goes after it.
    private static void VitalFastPathCastOnly() => Paced(() =>
    {
        var r = HealRig(kit: true);
        r.S.KitMinSuccessPct = 95;   // the kit is skipped: Heal Self
        r.Tick(0);
        Check.Eq(FakeHost.Casts.Count, 1, "Heal Self goes (the kit under Kit Min Success)");
        Check.Eq(FakeHost.UsesOn.Count, 0, "no kit");
        r.ServerRuns(0);
        r.S.KitMinSuccessPct = 0;    // now the kit is the first choice
        r.Tick(1.55);
        Check.Eq(FakeHost.UsesOn.Count, 0, "the fast tick sends no kit during the recoil");
        Check.Eq(FakeHost.Casts.Count, 1, "and no cast in its place");
        FakeHost.CastBusy = 0;
        r.Tick(1.75);
        Check.Eq(FakeHost.UsesOn.Count, 1, "the animation over: the kit goes");
    });

    private static void VitalChainRules()
    {
        DateTime t0 = new(2026, 11, 20, 12, 0, 0);
        var log = new System.Collections.Generic.List<string>();
        // A vital answered UseDone(0): the next vital may go fast, a buff may not.
        var c = new BuffCastChain();
        c.SentVital(100, "Heal Self VI", 10, 5, 0, 0, fast: false);
        c.Observe(true, 11, 0, 11, t0, log.Add);
        Check.True(c.EligibleVital(5, 0, 0, true, 11, 0, 11, t0.AddMilliseconds(50), out _), "vital after a vital: eligible");
        Check.False(c.Eligible(5, 0, 0, true, 11, 0, 11, t0.AddMilliseconds(50), out _), "buff after a vital: not eligible");
        Check.False(c.EligibleVital(6, 0, 0, true, 11, 0, 11, t0.AddMilliseconds(50), out _), "another action since: not eligible");
        Check.False(c.EligibleVital(5, 0, 0, true, 11, 0, 11, t0.AddMilliseconds(BuffCastChain.WindowMs + 10), out _), "past the window: not eligible");

        // A buff answered UseDone(0): the next buff may go fast, a vital may not.
        var b = new BuffCastChain();
        b.PassStart();
        b.Sent(200, "Strength Self VI", 20, 7, 0, 0, fast: false);
        b.Observe(true, 21, 0, 21, t0, log.Add);
        Check.True(b.Eligible(7, 0, 0, true, 21, 0, 21, t0.AddMilliseconds(50), out _), "buff after a buff: eligible");
        Check.False(b.EligibleVital(7, 0, 0, true, 21, 0, 21, t0.AddMilliseconds(50), out _), "vital after a buff: not eligible");

        // A fast vital refused by the server: no vital fast path for VitalOffMs.
        var r = new BuffCastChain();
        r.SentVital(100, "Heal Self VI", 30, 9, 0, 0, fast: true);
        r.Observe(true, 31, 0x1D, 31, t0, log.Add);
        Check.Eq(r.VitalSwitchOffs, 1, "the refusal switches the vital path off");
        Check.Eq(r.ServerRefusals, 1, "counted as a server refusal");
        Check.True(log.Exists(l => l.Contains("vital casts wait for the animation again for 60 s", StringComparison.Ordinal)), "logged");
        r.SentVital(100, "Heal Self VI", 31, 10, 0, 0, fast: false);
        r.Observe(true, 32, 0, 32, t0.AddSeconds(5), log.Add);
        Check.False(r.EligibleVital(10, 0, 0, true, 32, 0, 32, t0.AddSeconds(5.05), out _), "within 60 s of the refusal: not eligible");
        r.SentVital(100, "Heal Self VI", 32, 11, 0, 0, fast: false);
        r.Observe(true, 33, 0, 33, t0.AddSeconds(61), log.Add);
        Check.True(r.EligibleVital(11, 0, 0, true, 33, 0, 33, t0.AddSeconds(61.05), out _), "after 60 s: eligible again");

        // The client refusing a fast vital, and a too-busy line for one, switch it off too.
        var k = new BuffCastChain();
        k.VitalClientRefused(100, "Heal Self VI", fast: true, t0, log.Add);
        Check.Eq(k.VitalSwitchOffs, 1, "a client refusal of a fast vital");
        var tb = new BuffCastChain();
        tb.SentVital(100, "Heal Self VI", 40, 12, 0, 0, fast: true);
        tb.TooBusyLineVital(100, t0, log.Add);
        Check.Eq(tb.VitalSwitchOffs, 1, "a too-busy line for a fast vital");
        Check.Eq(tb.TooBusyLines, 1, "counted");

        // Integration: a buff due right after a heal waits for the animation (buff behaviour unchanged).
        Paced(() =>
        {
            var h = HealRig(buffs: true);
            h.Tick(0);
            Check.True(h.LastCast.StartsWith("Heal Self", StringComparison.Ordinal), $"the heal first ({h.LastCast})");
            h.ServerRuns(0);
            h.V.CurrentHealth = 100;   // healed: only buffs are left
            for (double s = 1.55; s < 2.05; s += 0.1) h.Tick(s);
            Check.Eq(FakeHost.Casts.Count, 1, "no buff during the heal's recoil");
            FakeHost.CastBusy = 0;
            h.Tick(2.1);
            Check.Eq(FakeHost.Casts.Count, 2, "the buff goes once the animation is over");
            Check.False(h.LastCast.StartsWith("Heal Self", StringComparison.Ordinal), $"and it is a buff ({h.LastCast})");
            Check.Eq(h.B.Chain.FastCasts, 0, "not on the fast path");
        });
    }

    // 2026-11-20: after every kit's result line the next kit waited 700 ms "for the vitals to catch up"
    // (sized when the vitals were read every 1-3 s). They are read every 100 ms now.
    private static void KitWaitEndsOnVitals()
    {
        Check.Eq(BuffManager.KitLineAmount("you heal yourself for 52 health points."), 52, "a heal line's amount");
        Check.Eq(BuffManager.KitLineAmount("you expertly heal yourself for 1,093 stamina points. your greater stamina kit has 26 uses left."), 1093, "expertly, a comma, stamina");
        Check.Eq(BuffManager.KitLineAmount("you fail to heal yourself."), 0, "a failed kit names none");

        var r = HealRig();
        r.V.CurrentHealth = 40;
        r.B.MarkKitUsed();
        r.B.OnChatWindowText("You heal yourself for 52 Health points.", 0);
        Check.True(r.B.KitPending(), "the line, the vitals not read yet: wait");
        r.V.CurrentHealth = 92;
        Check.False(r.B.KitPending(), "the vitals show the 52: no more wait");

        var s = HealRig();
        s.V.CurrentHealth = 90;
        s.B.MarkKitUsed();
        s.B.OnChatWindowText("You heal yourself for 52 Health points.", 0);
        s.V.CurrentHealth = 100;   // capped at the maximum
        Check.False(s.B.KitPending(), "capped at the maximum: caught up");

        var f = HealRig();
        f.B.MarkKitUsed();
        f.B.OnChatWindowText("You fail to heal yourself.", 0);
        Check.True(f.B.KitPending(), "a failed kit: wait the short catch-up");
        System.Threading.Thread.Sleep(350);
        Check.False(f.B.KitPending(), "300 ms after the line: done (was 700)");

        var st = HealRig();
        st.V.CurrentStamina = 20;
        st.B.MarkKitUsed(stamina: true);
        st.B.OnChatWindowText("You heal yourself for 30 Stamina points. Your Greater Stamina Kit has 21 uses left.", 0);
        Check.True(st.B.KitPending(), "a stamina kit: health doesn't count");
        st.V.CurrentStamina = 50;
        Check.False(st.B.KitPending(), "its stamina shows the 30: done");
    }

    private const uint HealKit = 0x80071111;

    /// <summary>A rig for the top-off: health 40%, Top Off HP 95, Heal At 50, out of a fight, Heal Self known,
    /// in Magic stance; buffs off unless <paramref name="buffs"/>; a healing kit carried with <paramref name="kit"/>.</summary>
    private static Rig HealRig(bool fast = true, bool kit = false, bool buffs = false)
    {
        var r = new Rig(fast);
        foreach (var row in SpellTable.Rows.Where(x => x.Name.StartsWith("Heal Self", StringComparison.Ordinal)))
            FakeHost.Spellbook.Add((uint)row.Id);
        r.S.EnableBuffing = buffs;
        r.S.TopOffHP = 95; r.S.HealAt = 50; r.S.EmergencyHealAt = 30; r.S.StaminaToHealthAt = 0;
        r.S.TopOffStam = 0; r.S.RestamAt = 0; r.S.TopOffMana = 0; r.S.GetManaAt = 0;
        r.V.CurrentHealth = 40;
        r.B.InCombatOverride = () => false;
        if (kit)
        {
            FakeHost.Names[HealKit] = "Treated Healing Kit";
            FakeHost.Skills[(Player, 21)] = (10, 2);   // Healing, low: under a 95% bar, over 0
            r.B.SetWorldObjectCache(FakeHost.MakeCache(r.Host, Player, new[] { HealKit }));
        }
        return r;
    }

    // A buff went out 50 ms after StopCompletelyBy("Nav"): the stop starts a motion the client
    // still has to play, so a movement call after the last buff turns the fast path off for the
    // next buff, which then waits on the animation gate like any other.
    private static void MovementWaitsForAnimation() => Paced(() =>
    {
        // Control: the same sequence with no movement in between takes the fast path.
        var c = FirstBuffOut();
        c.ServerRuns(0);
        c.Tick(1.55);
        Check.Eq(FakeHost.Casts.Count, 2, "no movement: the next buff goes on the UseDone(0)");
        Check.Eq(c.B.Chain.FastCasts, 1, "no movement: on the fast path");
        Check.Eq(c.B.Chain.MovementWaits, 0, "no movement: no movement wait");

        var r = FirstBuffOut();
        r.ServerRuns(0);
        r.At(1.5);
        Check.True(r.Host.StopCompletelyBy("Nav"), "nav's stop goes out");
        r.Tick(1.55);
        Check.Eq(FakeHost.Casts.Count, 1, "50 ms after the stop: the next buff waits for the animation");
        Check.Eq(r.B.Chain.FastCasts, 0, "not on the fast path");
        Check.Eq(r.B.Chain.MovementWaits, 1, "one movement wait counted");
        // Held by the animation gate, or first by the movement settle (ActionPacer/MoveAudit, PR #14:
        // 150 ms after a movement change and until the motion queue is clear), which covers the same
        // 50 ms window. Either way the fast path does not skip it.
        Check.True(Logged("[Pace] Buff waits: the character is still animating") || Logged("[Pace] Buff waits: the movement change StopCompletely()"),
            "held by the animation gate or the movement settle");
        r.Tick(1.65);
        Check.Eq(r.B.Chain.MovementWaits, 1, "counted once per buff, not per tick");
        FakeHost.CastBusy = 0;
        r.Tick(1.75);
        Check.Eq(FakeHost.Casts.Count, 2, "the animation over: the buff goes through the normal gate");
        Check.Eq(r.B.Chain.FastCasts, 0, "still nothing on the fast path");

        // A turn counts the same as a stop.
        var t = FirstBuffOut();
        t.ServerRuns(0);
        t.At(1.5);
        Check.True(t.Host.TurnToHeadingBy("Nav", 90f), "nav's turn goes out");
        t.Tick(1.55);
        Check.Eq(FakeHost.Casts.Count, 1, "50 ms after a turn: the next buff waits for the animation");
        Check.Eq(t.B.Chain.MovementWaits, 1, "a movement wait");

        // Stops sent while the buff is still unanswered are held (MoveAudit pending stops), not sent:
        // no movement is counted yet, and the next cast would flush them inside itself. A held stop
        // keeps the fast path off; the buff waits for the animation.
        var h = FirstBuffOut();
        h.At(0.5);
        for (int i = 0; i < 5; i++) h.Host.StopCompletelyBy("Nav");
        Check.True(MoveAudit.HasPendingStop, "the stops are held while the buff is unanswered");
        h.ServerRuns(0);
        h.Tick(1.55);
        Check.Eq(FakeHost.Casts.Count, 1, "a held stop: the next buff waits for the animation");
        Check.Eq(h.B.Chain.FastCasts, 0, "a held stop: not on the fast path");
        FakeHost.CastBusy = 0;
        h.Tick(1.75);
        Check.Eq(FakeHost.Casts.Count, 2, "the animation over: the buff goes");
        Check.Eq(h.B.Chain.FastCasts, 0, "and not on the fast path");
    });

    // The fast path skips the animation gate, so only the buff may ride it. Mana spent by the last
    // buff can leave the next one under the mana reserve (BuffWaitsForMana): Stamina to Mana is then
    // cast first, and it must wait for the recoil like any vital cast.
    //   50/300: under the reserve itself (the vitals check keeps the fast path off);
    //   120/300: over the reserve, but under it once the next buff's 60 is paid (the cast site drops
    //            the fast path for that buff).
    private static void ManaReserveWaitsForAnimation() => Paced(() =>
    {
        foreach (uint after in new uint[] { 50, 120 })
        {
            var r = new Rig(staminaToMana: true);
            BuffManager.ManaCostOverride = id => (SpellTableStub.GetById(id)?.Level ?? 0) * 10;   // after Reset: level x 10
            r.S.GetManaAt = 10; r.S.TopOffMana = 10;
            r.V.MaxMana = 300; r.V.CurrentMana = 300;
            int reserve = r.B.ManaReserve();
            Check.Eq(reserve, 90, "the reserve (1.5 x Stamina to Mana VI's 60)");
            r.Tick(0);
            Check.Eq(FakeHost.Casts.Count, 1, $"{after}: the first buff goes at 300/300");
            Check.True(!r.LastCast.StartsWith("Stamina to Mana", StringComparison.Ordinal), $"{after}: and it is a buff ({r.LastCast})");
            r.V.CurrentMana = after;   // what the buffs so far left
            r.ServerRuns(0);           // CastBusy = 1: the recoil still animating
            r.Tick(1.55);
            Check.Eq(FakeHost.Casts.Count, 1, $"{after}/300, 50 ms after the UseDone: nothing goes during the recoil");
            Check.Eq(r.B.Chain.FastCasts, 0, $"{after}: nothing on the fast path");
            r.Tick(1.65);
            Check.Eq(FakeHost.Casts.Count, 1, $"{after}: still nothing while it animates");
            FakeHost.CastBusy = 0;
            r.Tick(1.75);
            Check.Eq(FakeHost.Casts.Count, 2, $"{after}: the animation over, one cast");
            Check.True(r.LastCast.StartsWith("Stamina to Mana", StringComparison.Ordinal), $"{after}: it is Stamina to Mana ({r.LastCast})");
        }
    });

    private static long _t;

    private sealed class Rig
    {
        public DateTime T0 = new(2026, 10, 10, 14, 0, 0);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly RynthCoreHost Host;
        public readonly PlayerVitalsCache V;

        public Rig(bool fast = true, bool staminaToMana = false)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.SalvageCalls = true;
            FakeHost.ServerGates = true;
            FakeHost.LastUseDoneCalls = true;
            FakeHost.UseOnCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.StopCalls = true;
            FakeHost.BusyCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.Names[Corpse] = "Corpse of Drudge Skulker";
            FakeHost.Bitfields[Corpse] = 0x2000;
            foreach (string n in new[] { "Fire Protection Self VI", "Cold Protection Self VI", "Acid Protection Self VI" })
                foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
            if (staminaToMana)
                foreach (var row in SpellTable.Rows.Where(x => x.Name.StartsWith("Stamina to Mana Self", StringComparison.Ordinal)))
                    FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (300, 2);
            Host = FakeHost.Create(Player);
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, EnableBuffing = true, FastBuffChain = fast, SpellCastIntervalMs = 0,
            };
            var skills = new CharacterSkills(Host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(Host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
            B = new BuffManager(Host, S, sm, V);
            B.SetCharacterSkills(skills);
            NowAt = T0;
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();
            FakeHost.ServerTime = 100_000;
            _t = 10_000_000;
            ActionPacer.NowMs = () => _t + (long)(NowAt - T0).TotalMilliseconds;
            MoveAudit.NowMs = ActionPacer.NowMs;
            // Each rig restarts the clock at T0: the previous rig's movement (MoveAudit's settle,
            // PR #14) and pacer state would otherwise sit in this rig's future and hold it.
            MoveAudit.Reset();
            ActionPacer.Reset();
        }

        public void At(double seconds) => NowAt = T0.AddSeconds(seconds);
        public void Tick(double seconds) { At(seconds); B.OnHeartbeat(); }
        public string LastCast => SpellTable.ById(FakeHost.Casts.Last().SpellId)?.Name ?? "";

        /// <summary>The server runs the last cast: incantation, result line, then (on ACE) the
        /// recoil, then UseDone(err). The client is still animating when the UseDone arrives.</summary>
        public void ServerRuns(double sentAt, uint err = 0)
        {
            string name = LastCast;
            At(sentAt + 0.1); B.OnChatWindowText("You say, \"Zojak Tugeth\"", 17);
            FakeHost.CastBusy = 1;
            At(sentAt + 1.0); B.OnChatWindowText($"You cast {name} on yourself", 7);
            At(sentAt + 1.5);
            FakeHost.UseDone(err);
        }
    }

    private static void PaceOn()
    {
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        UseAudit.Reset("RynthAi", () => true);
        MoveAudit.Reset();
        var rec = typeof(RynthAiPlugin).GetMethod("RecordUseForPacing",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        UseAudit.AfterSend = (h, u) => rec.Invoke(null, new object[] { h, u });
    }

    private static void PaceOff()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        UseAudit.Reset("?", null);
        UseAudit.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
    }

    private static void Paced(Action body)
    {
        PaceOn();
        try { body(); }
        finally { PaceOff(); }
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));

    /// <summary>First buff of a pass on a quiet client: it goes on the first tick.</summary>
    private static Rig FirstBuffOut(bool fast = true)
    {
        var r = new Rig(fast);
        r.Tick(0);
        Check.Eq(FakeHost.Casts.Count, 1, "the first buff of the pass goes out");
        return r;
    }

    private static void NextBuffOnUseDone() => Paced(() =>
    {
        var r = FirstBuffOut();
        string first = r.LastCast;
        r.ServerRuns(0);
        r.Tick(1.55);   // 50 ms after the UseDone, the recoil still animating
        Check.Eq(FakeHost.Casts.Count, 2, "the next buff goes on the UseDone(0), the recoil still animating");
        Check.True(r.LastCast != first, $"and it is the next buff ({first} then {r.LastCast})");
        Check.True(Logged("[BuffChain] '" + r.LastCast + "' goes without waiting for the animation"), "the fast path is logged");
        Check.Eq(r.B.Chain.FastCasts, 1, "one buff on the fast path");

        // The same sequence with the setting off: the buff waits for the animation, as before.
        var q = FirstBuffOut(fast: false);
        q.ServerRuns(0);
        for (double s = 1.55; s < 2.05; s += 0.1) q.Tick(s);
        Check.Eq(FakeHost.Casts.Count, 1, "setting off: no buff while the recoil animates");
        Check.True(Logged("[Pace] Buff waits: the character is still animating"), "held by the animation gate, as in the log");
        FakeHost.CastBusy = 0;
        q.Tick(2.1);
        Check.Eq(FakeHost.Casts.Count, 2, "setting off: the buff goes once the animation is over");
        Check.Eq(q.B.Chain.FastCasts, 0, "nothing on the fast path");
    });

    private static void GateKept() => Paced(() =>
    {
        // A corpse use went out after the buff (a non-cast action) and was answered: the next buff
        // is the first after a non-cast action, so it waits for the animation.
        // The buff's UseDone(0) is read on a tick the busy count holds (nothing goes), then the corpse
        // use goes out and is answered.
        var r = FirstBuffOut();
        r.ServerRuns(0);
        r.B.BusyCount = 1;
        r.Tick(1.52);
        Check.Eq(FakeHost.Casts.Count, 1, "the busy count holds this tick");
        r.B.BusyCount = 0;
        Check.True(r.Host.UseFor(Corpse, "Loot", "open the claimed corpse"), "a corpse use goes out");
        FakeHost.UseDone(0);
        for (double s = 1.6; s < 2.1; s += 0.1) r.Tick(s);
        Check.Eq(FakeHost.Casts.Count, 1, "after a non-cast action: the buff waits for the animation");
        FakeHost.CastBusy = 0;
        r.Tick(2.2);
        Check.Eq(FakeHost.Casts.Count, 2, "and goes when the animation is over");

        // A swing went out after the buff's UseDone(0) (no UseDone of its own): the gate is kept.
        var a = FirstBuffOut();
        a.ServerRuns(0);
        a.B.BusyCount = 1;
        a.Tick(1.52);
        a.B.BusyCount = 0;
        ActionPacer.NoteAttackSent();
        for (double s = 1.6; s < 2.1; s += 0.1) a.Tick(s);
        Check.Eq(FakeHost.Casts.Count, 1, "after a swing: the buff waits for the animation");

        // A UseDone with an error for the buff: no fast path (and a server refusal counted).
        var e = FirstBuffOut();
        e.ServerRuns(0, err: TooBusy);
        for (double s = 1.55; s < 2.05; s += 0.1) e.Tick(s);
        Check.Eq(FakeHost.Casts.Count, 1, "after a UseDone error: the buff waits for the animation");
        Check.Eq(e.B.Chain.ServerRefusals, 1, "the error is counted as a server refusal");
        Check.True(Logged("[BuffChain] refusal (server): UseDone error 0x1D"), "and logged");

        // The UseDone(0) was read, but the client still reads busy past the window: not the recoil.
        var w = FirstBuffOut();
        w.ServerRuns(0);
        w.B.BusyCount = 1;   // something else holds the busy count meanwhile: the busy gate holds
        w.Tick(1.52);
        Check.Eq(FakeHost.Casts.Count, 1, "the busy gate is kept on the fast path");
        w.B.BusyCount = 0;
        w.Tick(1.52 + BuffCastChain.WindowMs / 1000.0 + 0.1);
        Check.Eq(FakeHost.Casts.Count, 1, "past the window after the UseDone: the animation gate again");
    });

    private static void RefusalFallsBack() => Paced(() =>
    {
        var r = FirstBuffOut();
        r.ServerRuns(0);
        // The client refuses the fast-path cast: nothing goes out.
        FakeHost.CastRefused = true;
        r.Tick(1.55);
        Check.Eq(FakeHost.CastsRefused, 1, "the fast-path cast was tried and the client refused it");
        Check.Eq(r.B.Chain.ClientRefusals, 1, "counted as a client refusal");
        Check.True(Logged("[BuffChain] refusal (client): CastSpell refused"), "logged");
        Check.True(r.B.Chain.OffThisPass, "the fast path is off for the rest of the pass");
        Check.True(Logged("the next buffs of this pass wait for the animation again"), "and that is logged");
        FakeHost.CastRefused = false;

        // The next buff waits for the animation, even right after a buff's UseDone(0).
        FakeHost.CastBusy = 0;
        r.Tick(1.7);
        Check.Eq(FakeHost.Casts.Count, 2, "gate open: the buff goes");
        double sent = 1.7;
        r.ServerRuns(sent);
        for (double s = sent + 1.55; s < sent + 2.05; s += 0.1) r.Tick(s);
        Check.Eq(FakeHost.Casts.Count, 2, "after the refusal: the next buff waits for the animation");
        FakeHost.CastBusy = 0;
        r.Tick(sent + 2.1);
        Check.Eq(FakeHost.Casts.Count, 3, "and goes when it ends");

        // The pass ends: one summary line with this pass's refusals.
        r.ServerRuns(sent + 2.1);
        FakeHost.CastBusy = 0;
        for (double s = sent + 3.7; s < sent + 6; s += 0.2) r.Tick(s);
        Check.True(Logged("[BuffChain] buff pass complete:"), "a summary line at the end of the pass");
        Check.True(Logged("refusals this pass: client 1, server 0"), "with its refusals: " +
            (FakeHost.Logs.LastOrDefault(l => l.Contains("[BuffChain] buff pass")) ?? "(none)"));
    });

    private static void SettingDefaults()
    {
        Check.True(new LegacyUiSettings().FastBuffChain, "a new profile: on");
        var old = System.Text.Json.JsonSerializer.Deserialize("{\"EnableBuffing\":true}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.FastBuffChain, "a profile saved before the setting: on");
        var off = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings { FastBuffChain = false }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(off != null && !off.FastBuffChain, "off is saved and loaded");
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { FastBuffChain = false }, dst });
        Check.False(dst.FastBuffChain, "a profile with it off loads off");
    }
}
