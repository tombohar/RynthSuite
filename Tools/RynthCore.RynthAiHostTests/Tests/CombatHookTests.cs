using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The script-provided combat hook (RynthAi.Combat, Shared/RynthAiCombatApi.cs): a script plugin
// claims the attack and/or heal kind, RynthAi opens a request where it would choose a spell,
// holds that one cast for the answer, and casts the answer with its own bookkeeping; a decline,
// a bad answer, a timeout or a lapsed lease is RynthAi's own choice the same tick. Each test runs
// the same rig with the hook off as the control, so every check here changes between on and off.
internal static class CombatHookTests
{
    private const uint Player = 0x50000F01;
    private const int Target = 0x7000F001;
    private const int Other = 0x7000F002;       // a second scanned monster
    private const int Stranger = 0x7000F003;    // known to the cache, not in range: never scanned
    private const uint Kit = 0x8000F010;
    private const uint Atk = RynthAiCombat.KindAttack, HealKind = RynthAiCombat.KindHeal;

    public static void Register(Runner r)
    {
        r.Add("combat hook: the decided spell reaches the cast (hook off: RynthAi's bolt)", DecidedSpellIsCast);
        r.Add("combat hook: a rule with no attack shapes enabled is not asked and not cast at, script or not", ScriptRespectsNoAttackShapes);
        r.Add("combat hook: shutdown clears the UseDone reader (it captured the host)", ShutdownDropsUseDoneReader);
        r.Add("combat hook: a decline casts RynthAi's own spell the same tick", DeclineGoesNative);
        r.Add("combat hook: no answer by the deadline - RynthAi's spell, the timeout logged once a minute", TimeoutGoesNative);
        r.Add("combat hook: a lapsed lease is native; another owner is refused while the lease is live", LeaseExpiry);
        r.Add("combat hook: an answer left unused past its deadline + 1 s is stale: asked again, not cast", StaleAnswerDropped);
        r.Add("combat hook: the mana reserve holds the script's spell as it holds RynthAi's own (no native cast instead); it goes out when mana is back", ManaReserveHoldsScriptSpell);
        r.Add("combat hook: a script spell costing more than max mana is rejected on the first answer; RynthAi's bolt the same tick", NeverPayableScriptSpellRejected);
        r.Add("combat hook: Think ends a mana-held tick on the mana hold (Wait.Cast, no attack stamp), then casts the kept answer", ManaHoldThinkOrdering);
        r.Add("combat hook: a mana hold past the deadline + 1 s drops the answer and asks again (no cast, no timeout)", ManaHoldGoesStale);
        r.Add("combat hook: unknown spell, unscanned or unattackable target, and stale seqs are rejected", InvalidAnswersRejected);
        r.Add("combat hook: another scanned monster or an area flag is cast without the pending-offensive judgement", OffTargetAndArea);
        r.Add("combat hook: Think holds the tick without stamping the attack time; the answer's cast stamps it", ThinkHoldsWithoutStamp);
        r.Add("combat hook: the heal handler is asked before kits; a drain at a monster goes through CastVital", HealBeforeKit);
        r.Add("combat hook: a declined, invalid or timed-out heal uses the kit the same check", HealFallsBack);
        r.Add("refusal: a refused peace-for-the-kit change stamps no cast attempt or wait, and is asked again each tick", PeaceForKitRefused);
        r.Add("combat hook: the emergency step stays native (kit first, the handler not asked)", EmergencyStaysNative);
        r.Add("combat hook: the native table - version check, Claim/Poll/Decide/Release, Poll respects StructSize", NativeTable);
        r.Add("combat hook: a script attack spell the server refused is rejected next time (RynthAi's bolt instead)", AttackRefusalRemembered);
        r.Add("combat hook: a script heal the server had no components for is rejected next time (the kit instead)", HealRefusalRemembered);
        r.Add("combat hook: NOJUDGE keeps no-damage script casts out of the blacklist (without it the target is dropped)", NoJudgeSkipsBlacklist);
        r.Add("combat hook: ctx.monsters leaves out a Priority 0 (ignored) monster, even one hitting us", HookScanSkipsIgnored);
        r.Add("combat hook: a script answer at an ignored monster is rejected, even one hitting us (RynthAi casts its own)", HookRejectsIgnoredTarget);
        r.Add("combat hook: a script heal (a drain) at a fight target whose rule turned to Priority 0 is rejected", HookHealRejectsIgnoredTarget);
        r.Add("blacklist: casts answered 'too busy' (UseDone 0x1D) are not no-damage casts (answered 0: the target is dropped)", RefusedCastsNotJudged);
        r.Add("combat hook: ctx.pending is the cast in flight; ctx.last the last resolved one, err its first UseDone (final; -1 when missed or after 7 s)", LastErrIsTheCastsAnswer);
        r.Add("combat hook: an attack and a heal in flight take their UseDones in send order (attack 0x401, heal 0)", AnswersInSendOrder);
        r.Add("combat hook: a script heal refused for mana keeps its 2 s rest (no recovery fast retry)", ScriptHealNoFastRetry);
        r.Add("combat hook: a script attack with no answer is not jumped in 5.5 s; RynthAi's own cast is", ScriptAttackNotJumpedEarly);
        r.Add("combat hook: a script heal (a drain) with no answer is not jumped in 5.5 s", ScriptHealNotJumpedEarly);
        r.Add("combat hook: a 'too busy' answer at 3.5 s does not jump a script attack before 6 s; RynthAi's own cast is jumped", ScriptAttackRefusalNoEarlyJump);
        r.Add("combat hook: a 'too busy' answer at 3.5 s does not jump a script heal before 6 s", ScriptHealRefusalNoEarlyJump);
        r.Add("combat hook: a UseDone that lands during the send is the cast's answer (ctx.last.err)", AnswerDuringSendIsTheCasts);
        r.Add("combat hook: a script cast at the fight target is tagged for the Damage tab (element and tier from its spell)", ScriptCastAttribution);
        r.Add("combat hook: a heal request names the fight target and its health; a nil answer target still heals the player", HealRequestNamesFightTarget);
        r.Add("combat hook: a refusal after another cast (a heal) or after the script spell landed does not park the script spell", RefusalNotMisattributed);
        r.Add("combat hook: a script bolt predicted to kill swaps to the next target like RynthAi's own (not without an alternate, not NOJUDGE)", ScriptKillShotSwaps);
        r.Add("combat hook: with War and Void untrained, a held attack kind makes the listed wand the weapon: Magic stance, not unarmed Melee", HookMakesWandTheWeapon);
    }

    private static T Field<T>(object o, string name)
        => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void SetField(object o, string name, object? value)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);

    private static void CallPrivate(object o, string name)
        => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(o, null);

    /// <summary>One request answered and cast: hold, Decide, cast.</summary>
    private static void ScriptCast(AttackRig rig, int spell, uint target = 0, uint flags = 0)
    {
        rig.Attack();
        Check.True(CombatHook.Decide(OpenSeq(Atk), Atk, (uint)spell, target, flags), "the answer is taken");
        rig.Attack();
    }

    private const string NoComponents = "You don't have all the components for this spell.";

    private static string NameOf(int id) => id == 0 ? "(none)" : SpellTable.ById(id)?.Name ?? $"#{id}";
    private static int Id(string name) => SpellTable.Named(name).Select(r => r.Id).First();

    private static long _now;

    /// <summary>A fresh hook on a fake clock; restored (and the hook dropped) by <see cref="Done"/>.</summary>
    private static void FreshHook()
    {
        CombatHook.Reset();
        _now = 1_000_000;
        CombatHook.Clock = () => _now;
    }

    private static void Done()
    {
        CombatHook.Reset();
        CombatHook.Clock = static () => Environment.TickCount64;
    }

    private static void Guard(Action body)
    {
        FreshHook();
        try { body(); }
        finally { Done(); }
    }

    // -- The attack rig: a caster knowing Flame Bolt VI and Drain Health Other V, a monster rule
    //    that casts fire bolts, the fight target 10 yd north and a second monster 12 yd north-east.

    private sealed class AttackRig
    {
        public readonly LegacyUiSettings S = new();
        public readonly CombatManager C;
        public readonly RynthCoreHost Host;
        public readonly SpellManager Sm;
        private static readonly MethodInfo AttackM = typeof(CombatManager).GetMethod("AttackWithMagic", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo Awaiting = typeof(CombatManager).GetField("_awaitingCastResolution", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo PendingSpell = typeof(CombatManager).GetField("_pendingOffensiveSpellId", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo WasArea = typeof(CombatManager).GetField("_lastCastWasRing", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private readonly WorldObject _target = new(Target, "Drudge Skulker");

        public AttackRig(bool think = false, Action? setup = null, uint[]? extraItems = null)
        {
            FakeHost.Reset();
            setup?.Invoke();
            foreach (string n in new[] { "Flame Bolt VI", "Drain Health Other V" })
                foreach (var row in SpellTable.Named(n)) FakeHost.Spellbook.Add((uint)row.Id);
            if (think) { FakeHost.WeaponCalls = true; FakeHost.CombatModeValue = CombatMode.Magic; }
            // The no-skills stub reads 250 buffed: tiers 1-6 castable, 7 and 8 not.
            S.MinSkillLevelTier1 = 0; S.MinSkillLevelTier2 = 0; S.MinSkillLevelTier3 = 0;
            S.MinSkillLevelTier4 = 0; S.MinSkillLevelTier5 = 0; S.MinSkillLevelTier6 = 0;
            S.MinSkillLevelTier7 = 10000; S.MinSkillLevelTier8 = 10000;
            S.RingRange = 0;
            S.MonsterRange = 20;
            S.EnableCombat = true;
            S.MonsterRules.Clear();
            S.MonsterRules.Add(new MonsterRule { Name = "Default", DamageType = "Fire", UseBolt = true });
            FakeHost.PlayerPose = (0x0101001Bu, 0f, 0f, 0f);
            FakeHost.Positions[Player] = FakeHost.PlayerPose;
            Place(Target, "Drudge Skulker", 0f, 10f);
            Place(Other, "Drudge Slinker", 8f, 9f);
            Place(Stranger, "Drudge Lurker", 0f, 150f);
            var host = FakeHost.Create(Player);
            Host = host;
            var cache = FakeHost.MakeCache(host, Player, new[] { (uint)Target, (uint)Other, (uint)Stranger }.Concat(extraItems ?? Array.Empty<uint>()).ToArray());
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.RefreshKnownSpells();
            C.SetSpellManager(sm);
            Sm = sm;
            C.Vitals = new PlayerVitalsCache { MaxHealth = 200, CurrentHealth = 150, MaxStamina = 100, CurrentStamina = 40, MaxMana = 100, CurrentMana = 90 };
            C.ScanNearbyTargets();
            C.activeTargetId = Target;
        }

        private static void Place(int id, string name, float x, float y)
        {
            FakeHost.Names[(uint)id] = name;
            FakeHost.Positions[(uint)id] = (0x0101001Bu, x, y, 0f);
            FakeHost.ItemTypes[(uint)id] = 0x10;
        }

        /// <summary>One attack cycle (AttackWithMagic, past every gate Think puts before it).</summary>
        public void Attack()
        {
            Awaiting.SetValue(C, false);   // the last cast resolved (the cadence gate is Think's)
            AttackM.Invoke(C, new object[] { _target });
        }

        public int PendingOffensive => (int)PendingSpell.GetValue(C)!;
        public bool LastWasArea => (bool)WasArea.GetValue(C)!;
        public string LastCast => FakeHost.Casts.Count == 0 ? "(none)" : NameOf(FakeHost.Casts[^1].SpellId);
    }

    private static uint Claim(uint kinds, uint lease = 0, uint atk = 0, uint heal = 0, string owner = "test")
        => CombatHook.Claim(kinds, lease, atk, heal, owner);

    private static uint OpenSeq(uint kind)
    {
        Check.True(CombatHook.Poll(kind, out var req), $"a {RynthAiCombat.KindNames(kind)} request is open");
        return req.Seq;
    }

    private static void DecidedSpellIsCast() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");

        // Control: no hook, RynthAi's own choice.
        var off = new AttackRig();
        off.Attack();
        Check.Eq(off.LastCast, "Flame Bolt VI", "hook off: RynthAi casts its bolt at once");
        Check.Eq(CombatHook.Attack.Opened, 0, "hook off: no request");

        var on = new AttackRig();
        Check.Eq(Claim(Atk), Atk, "the attack kind is granted");
        on.Attack();
        Check.Eq(FakeHost.Casts.Count, 0, "hook on: nothing cast while waiting");
        Check.True(on.C.ScriptHeld, "the cycle is held for the answer");
        Check.Eq(on.C.LastCombatSkipReason, "script-wait", "skip reason says so");
        Check.True(CombatHook.Poll(Atk, out var req), "the request is visible to Poll");
        Check.Eq(req.TargetId, (uint)Target, "about the fight target");
        Check.Eq(req.Kind, Atk, "attack kind");
        Check.Eq((req.HealthPct, req.StaminaPct, req.ManaPct), (75, 40, 90), "carries the player's vitals");
        Check.Eq(req.CombatMode, on.C.CurrentCombatMode, "and the combat mode");
        Check.Eq(req.DeadlineMs - req.IssuedAtMs, (long)RynthAiCombat.DefaultAttackTimeoutMs, "default deadline");
        on.Attack();
        Check.Eq(FakeHost.Casts.Count, 0, "a second cycle before the answer: still waiting, no second request");
        Check.Eq(CombatHook.Attack.Opened, 1, "one request");

        Check.True(CombatHook.Decide(req.Seq, Atk, (uint)drain, 0, 0), "the answer is taken");
        Check.False(CombatHook.Poll(Atk, out _), "an answered request is no longer polled");
        on.Attack();
        Check.Eq(FakeHost.Casts.Count, 1, "the answer is cast");
        Check.Eq(FakeHost.Casts[0], ((uint)Target, drain), "Drain Health Other V at the fight target (target 0 = the fight target)");
        Check.False(on.C.ScriptHeld, "not held");
        Check.Eq(on.PendingOffensive, drain, "single-target at the fight target: the pending-offensive judgement is armed");
        Check.False(on.LastWasArea, "not an area cast");
        Check.Eq(CombatHook.Attack.Casts, 1, "counted");
        Check.Eq((CombatHook.Attack.LastSeq, CombatHook.Attack.LastSpell, CombatHook.Attack.LastTarget), (req.Seq, (uint)drain, (uint)Target),
            "the next request will report it as the last decision");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatCast] script id=" + drain)), "logged");

        // The next cycle asks again, and hands back the last decision.
        on.Attack();
        Check.True(CombatHook.Poll(Atk, out var next), "the next cycle opens a new request");
        Check.True(next.Seq != req.Seq, "with a new seq");
        Check.Eq((next.LastSeq, next.LastSpell, next.LastTarget), (req.Seq, (uint)drain, (uint)Target), "ctx.last is the previous cast");
    });

    // A rule with every attack shape off means no offensive cast at that monster; a script's attack is no exception.
    private static void ScriptRespectsNoAttackShapes() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");

        var off = new AttackRig();
        off.S.MonsterRules.Clear();
        off.S.MonsterRules.Add(new MonsterRule { Name = "Default", DamageType = "Fire", UseBolt = false, UseArc = false, UseStreak = false, UseRing = false, UseBlast = false, UseWall = false });
        Claim(Atk);
        off.Attack();
        Check.Eq(CombatHook.Attack.Opened, 0, "no shapes: the script is not even asked");
        Check.Eq(off.C.LastCombatSkipReason, "no-attack-shapes", "skipped for the rule");
        Check.False(off.C.ScriptHeld, "and not held for an answer");
        Check.Eq(FakeHost.Casts.Count, 0, "nothing cast");

        // Control: the same script with a shape on is asked and its spell is cast.
        var on = new AttackRig();
        Claim(Atk);
        ScriptCast(on, drain);
        Check.Eq(CombatHook.Attack.Opened, 1, "a shape on: the script is asked");
        Check.True(FakeHost.Casts.Any(c => c.SpellId == drain), "and its spell is cast");
    });

    // The UseDone reader the plugin installs captures its host; a shut-down plugin must not keep it.
    private static void ShutdownDropsUseDoneReader() => Guard(() =>
    {
        CombatHook.LastUseDone = () => (1, 0u);
        CombatHookInterface.Shutdown();            // PluginExports.Shutdown calls it for every unload
        Check.Null(CombatHook.LastUseDone, "the exported shutdown clears the reader");

        FakeHost.Reset();
        var host = FakeHost.Create(Player);
        var plugin = new RynthAiPlugin();
        PacerEdgeTests.SetHost(plugin, host);
        CombatHook.LastUseDone = () => (1, 0u);
        plugin.Shutdown();
        Check.Null(CombatHook.LastUseDone, "and so does the plugin's own Shutdown");
    });

    private static void DeclineGoesNative() => Guard(() =>
    {
        var rig = new AttackRig();
        Claim(Atk);
        rig.Attack();
        uint seq = OpenSeq(Atk);
        Check.True(CombatHook.Decide(seq, Atk, 0, 0, 0), "a decline is an answer");
        rig.Attack();
        Check.Eq(FakeHost.Casts.Count, 1, "one cast, this same cycle");
        Check.Eq(rig.LastCast, "Flame Bolt VI", "RynthAi's own bolt");
        Check.Eq(CombatHook.Attack.Declines, 1, "counted as a decline");
        Check.Eq(CombatHook.Attack.LastSpell, 0u, "the last decision cast nothing");
    });

    private static void TimeoutGoesNative() => Guard(() =>
    {
        var rig = new AttackRig();
        Claim(Atk, atk: 200);
        rig.Attack();
        uint seq = OpenSeq(Atk);
        _now += 150;
        rig.Attack();
        Check.Eq(FakeHost.Casts.Count, 0, "150 ms of 200: still waiting");
        _now += 100;
        Check.False(CombatHook.Decide(seq, Atk, (uint)Id("Drain Health Other V"), 0, 0), "an answer after the deadline is stale");
        rig.Attack();
        Check.Eq(rig.LastCast, "Flame Bolt VI", "past the deadline: RynthAi's bolt, this cycle");
        Check.Eq(CombatHook.Attack.Timeouts, 1, "counted");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[CombatHook] attack request") && l.Contains("unanswered")), 1, "logged");

        // A second timeout inside the minute is counted, not logged; after the minute it is logged again.
        rig.Attack();
        _now += 250;
        rig.Attack();
        Check.Eq(CombatHook.Attack.Timeouts, 2, "second timeout counted");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("unanswered")), 1, "not logged again within a minute");
        _now += CombatHook.TimeoutLogIntervalMs;
        Claim(Atk, atk: 200);   // the lease lapsed over that minute: renew, as the script side does every tick
        rig.Attack();
        _now += 250;
        rig.Attack();
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("unanswered")), 2, "logged again after a minute");
    });

    private static void LeaseExpiry() => Guard(() =>
    {
        var rig = new AttackRig();
        Check.Eq(Claim(Atk, lease: 100), Atk, "a lease under the floor is clamped up, not refused");
        Check.Eq(CombatHook.LeaseUntilMs - _now, (long)RynthAiCombat.MinLeaseMs, "clamped to 500 ms");
        Check.Eq(Claim(Atk, owner: "rival"), 0u, "another owner is refused while the lease is live");
        Check.Eq(CombatHook.Owner, "test", "the holder keeps it");
        rig.Attack();
        Check.True(rig.C.ScriptHeld, "held while the lease is live");
        _now += RynthAiCombat.MinLeaseMs;   // not renewed: the script plugin stopped ticking
        Check.False(CombatHook.IsActive(Atk), "lapsed");
        Check.False(CombatHook.Poll(Atk, out _), "nothing to poll");
        rig.Attack();
        Check.Eq(rig.LastCast, "Flame Bolt VI", "lapsed lease: RynthAi casts its own, this cycle");
        Check.False(rig.C.ScriptHeld, "not held");
        Check.Eq(Claim(Atk, owner: "rival"), Atk, "after the lapse another owner can claim");

        // Release: native at once.
        var r2 = new AttackRig();
        Claim(Atk, owner: "rival");
        r2.Attack();
        Check.True(r2.C.ScriptHeld, "held");
        CombatHook.Release(Atk);
        Check.Eq(CombatHook.Owner, "", "released: no owner");
        r2.Attack();
        Check.Eq(r2.LastCast, "Flame Bolt VI", "released: native");

        // Claiming only heal drops the attack kind (its open request too).
        var r3 = new AttackRig();
        Claim(Atk | HealKind);
        r3.Attack();
        Check.True(r3.C.ScriptHeld, "held with both kinds");
        Check.Eq(Claim(HealKind), HealKind, "renewed with heal only");
        r3.Attack();
        Check.Eq(r3.LastCast, "Flame Bolt VI", "the attack kind is no longer held: native");
    });

    private static void StaleAnswerDropped() => Guard(() =>
    {
        var rig = new AttackRig();
        Claim(Atk, lease: 3000);
        rig.Attack();
        uint seq = OpenSeq(Atk);
        Check.True(CombatHook.Decide(seq, Atk, (uint)Id("Drain Health Other V"), 0, 0), "answered in time");
        // RynthAi's gates held the cast (busy, a cast resolving) for longer than the deadline + 1 s.
        _now += RynthAiCombat.DefaultAttackTimeoutMs + CombatHook.StaleGraceMs + 1;
        rig.Attack();
        Check.Eq(FakeHost.Casts.Count, 0, "the old answer is not cast");
        Check.True(rig.C.ScriptHeld, "a fresh request is waiting instead");
        Check.True(CombatHook.Poll(Atk, out var fresh) && fresh.Seq != seq, "with a new seq");
        Check.Eq(CombatHook.Attack.Timeouts, 0, "not counted as a timeout (RynthAi wasn't waiting)");

        // Inside the grace, an answer given in time is still used.
        Check.True(CombatHook.Decide(fresh.Seq, Atk, (uint)Id("Drain Health Other V"), 0, 0), "answered");
        Claim(Atk, lease: 3000);   // renewed every tick by the script side
        _now += RynthAiCombat.DefaultAttackTimeoutMs + CombatHook.StaleGraceMs - 10;
        rig.Attack();
        Check.Eq(rig.LastCast, "Drain Health Other V", "used within the grace");
    });

    private static readonly FieldInfo ManaCastHeld = typeof(CombatManager).GetField("_manaCastHeld", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void ManaReserveHoldsScriptSpell() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        string? hold = "mana 5 is under Drain Health Other V's cost (20)";

        // Control: the same answer with no mana hold is cast at once.
        var free = new AttackRig();
        free.C.ManaReserveHold = _ => null;
        Claim(Atk);
        ScriptCast(free, drain);
        Check.Eq(free.LastCast, "Drain Health Other V", "no mana hold: the script's spell is cast");

        CombatHook.Reset();
        var rig = new AttackRig();
        rig.C.ManaReserveHold = id => id == drain ? hold : null;   // only the script's spell is held
        Claim(Atk, lease: 5000);
        rig.Attack();
        uint seq = OpenSeq(Atk);
        Check.True(CombatHook.Decide(seq, Atk, (uint)drain, 0, 0), "answered");
        rig.Attack();
        Check.Eq(FakeHost.Casts.Count, 0, "mana hold: nothing cast (not the script's drain, not RynthAi's bolt)");
        Check.True((bool)ManaCastHeld.GetValue(rig.C)!, "held for mana, so Think ends the tick");
        Check.Eq(rig.C.LastCombatSkipReason, "mana-reserve", "skip reason says so");
        Check.Eq((CombatHook.Attack.Casts, CombatHook.Attack.Rejects), (0, 0), "neither cast nor rejected: the answer is kept");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatCast] attacks wait: " + hold)), "the hold is logged");

        // Mana comes back inside the request's grace: the kept answer goes out, no new request.
        hold = null;
        _now += 300;
        rig.Attack();
        Check.Eq(rig.LastCast, "Drain Health Other V", "mana back: the kept answer is cast");
        Check.Eq(CombatHook.Attack.Opened, 1, "on the same request");
        Check.False((bool)ManaCastHeld.GetValue(rig.C)!, "no longer held");
    });

    private static readonly FieldInfo TickWait = typeof(CombatManager).GetField("_tickWait", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// The real BuffManager behind both mana delegates (OffensiveCastHold, CanEverAfford), as
    /// RynthAiPlugin wires them; the drain costs <paramref name="drainCost"/>, every other spell its
    /// level x 10 (Flame Bolt VI: 60, payable from the rig's 90 mana).
    /// </summary>
    private static BuffManager WireRealMana(AttackRig rig, int drain, int drainCost, uint maxMana)
    {
        rig.S.IsMacroRunning = true;
        BuffManager.ManaCostOverride = id => id == drain ? drainCost : (SpellTableStub.GetById(id)?.Level ?? 0) * 10;
        rig.C.Vitals!.MaxMana = maxMana;
        var b = new BuffManager(rig.Host, rig.S, rig.Sm, rig.C.Vitals);
        b.InCombatOverride = () => true;
        rig.C.ManaReserveHold = id => b.OffensiveCastHold(id);
        rig.C.ManaCanEverAfford = id => b.CanEverAfford(id);
        return b;
    }

    private static void NeverPayableScriptSpellRejected() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var costOverride = BuffManager.ManaCostOverride;
        try
        {
            // Control: max mana 1000 can pay the drain's 500 some day, so the cost hold keeps it.
            var some = new AttackRig();
            var bSome = WireRealMana(some, drain, drainCost: 500, maxMana: 1000);
            Check.True(bSome.OffensiveCastHold(drain) != null, "control: 90 mana is under the drain's 500, so it is held");
            Claim(Atk);
            ScriptCast(some, drain);
            Check.Eq(FakeHost.Casts.Count, 0, "control (max mana 1000): held, nothing cast");
            Check.Eq(CombatHook.Attack.Rejects, 0, "control: not rejected");

            // Max mana 100 can never pay 500: the cost hold would never lift.
            CombatHook.Reset();
            var rig = new AttackRig();
            var b = WireRealMana(rig, drain, drainCost: 500, maxMana: 100);
            Check.True(b.OffensiveCastHold(drain) != null, "the cost hold says hold for the drain, for good");
            Check.False(b.CanEverAfford(drain), "CanEverAfford: 500 > max mana 100");
            Check.True(b.CanEverAfford(Id("Flame Bolt VI")), "CanEverAfford: the bolt's 60 fits");
            Claim(Atk);
            ScriptCast(rig, drain);
            Check.Eq(FakeHost.Casts.Count, 1, "the first answer: one cast, the same tick");
            Check.Eq(rig.LastCast, "Flame Bolt VI", "RynthAi's own bolt, not the drain");
            Check.Eq(CombatHook.Attack.Rejects, 1, "the answer is rejected");
            Check.Eq(CombatHook.Attack.Casts, 0, "not counted as a script cast");
            Check.False((bool)ManaCastHeld.GetValue(rig.C)!, "not held for mana");
            Check.True(FakeHost.Logs.Any(l => l.Contains("rejected (costs more than max mana)")), "logged with the reason");
        }
        finally { BuffManager.ManaCostOverride = costOverride; }
    });

    private static void ManaHoldThinkOrdering() => Guard(() =>
    {
        bool pacer = ActionPacer.Enabled;
        ActionPacer.Enabled = false;
        try
        {
            int drain = Id("Drain Health Other V");
            string? hold = "mana 5 is under Drain Health Other V's cost (20)";
            var rig = new AttackRig(think: true);
            rig.C.ManaReserveHold = id => id == drain ? hold : null;
            Claim(Atk, lease: 5000);
            rig.C.Think();
            Check.True(rig.C.ScriptHeld, "the first Think waits for the answer");
            Check.Eq(TickWait.GetValue(rig.C)!.ToString(), "Other", "the script wait ends the tick as Wait.Other");
            CombatHook.Decide(OpenSeq(Atk), Atk, (uint)drain, 0, 0);

            rig.C.Think();
            Check.Eq(FakeHost.Casts.Count, 0, "held for mana: Think sends nothing");
            Check.True((bool)ManaCastHeld.GetValue(rig.C)!, "_manaCastHeld set");
            Check.False(rig.C.ScriptHeld, "not a script wait");
            Check.Eq(TickWait.GetValue(rig.C)!.ToString(), "Cast", "Think ended the tick on the mana hold (Wait.Cast)");
            Check.Eq(rig.C.GetStateSnapshot().LastAttackCmd, DateTime.MinValue, "lastAttackCmd not stamped while held for mana");

            hold = null;
            rig.C.Think();
            Check.Eq(rig.LastCast, "Drain Health Other V", "mana back: the kept answer goes out");
            Check.True(rig.C.GetStateSnapshot().LastAttackCmd != DateTime.MinValue, "and stamps the attack time");
        }
        finally
        {
            ActionPacer.Enabled = pacer;
            ActionPacer.Forget("test");
        }
    });

    private static void ManaHoldGoesStale() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var rig = new AttackRig();
        rig.C.ManaReserveHold = id => id == drain ? "mana 5 is under Drain Health Other V's cost (20)" : null;
        Claim(Atk, lease: 5000);
        rig.Attack();
        Check.True(CombatHook.Decide(OpenSeq(Atk), Atk, (uint)drain, 0, 0), "answered");
        rig.Attack();
        Check.True((bool)ManaCastHeld.GetValue(rig.C)!, "held for mana");
        Check.Eq(CombatHook.Attack.Opened, 1, "one request so far");

        // The hold outlasts the request's deadline + StaleGraceMs: the answer is dropped and asked again.
        _now += RynthAiCombat.DefaultAttackTimeoutMs + CombatHook.StaleGraceMs + 1;
        rig.Attack();
        Check.Eq(CombatHook.Attack.Opened, 2, "a fresh request is opened");
        Check.True(rig.C.ScriptHeld, "the tick waits on it");
        Check.False((bool)ManaCastHeld.GetValue(rig.C)!, "not a mana hold on the wait tick");
        Check.Eq(CombatHook.Attack.Casts, 0, "nothing cast");
        Check.Eq(FakeHost.Casts.Count, 0, "nothing sent");
        Check.Eq((CombatHook.Attack.Timeouts, CombatHook.Attack.Rejects), (0, 0), "neither a timeout nor a rejection");
    });

    private static void InvalidAnswersRejected() => Guard(() =>
    {
        void Case(string what, uint spell, uint target, string why, Action? setup = null)
        {
            _now += 10_000;   // past the reject log's 5 s throttle
            var rig = new AttackRig();
            setup?.Invoke();   // after the scan: a monster that turned unattackable since
            Claim(Atk);
            rig.Attack();
            uint seq = OpenSeq(Atk);
            Check.False(CombatHook.Decide(seq + 1, Atk, spell, target, 0), $"{what}: a wrong seq is stale");
            Check.False(CombatHook.Decide(seq, HealKind, spell, target, 0), $"{what}: the wrong kind is stale");
            Check.True(CombatHook.Decide(seq, Atk, spell, target, 0), $"{what}: taken (validated at the cast)");
            Check.False(CombatHook.Decide(seq, Atk, spell, target, 0), $"{what}: a second answer to the same seq is stale");
            int before = CombatHook.Attack.Rejects;
            rig.Attack();
            Check.Eq(rig.LastCast, "Flame Bolt VI", $"{what}: rejected, RynthAi's bolt this cycle");
            Check.Eq(FakeHost.Casts.Count, 1, $"{what}: nothing else cast");
            Check.Eq(CombatHook.Attack.Rejects, before + 1, $"{what}: counted as a rejection");
            Check.True(FakeHost.Logs.Any(l => l.Contains("rejected (" + why)), $"{what}: logged ({why})");
        }

        Case("unknown spell", (uint)Id("Frost Bolt VI"), 0, "spell");
        Case("a monster outside the scan", (uint)Id("Drain Health Other V"), (uint)Stranger, "the target is not");
        Case("an unattackable scanned monster", (uint)Id("Drain Health Other V"), (uint)Other, "the target is not attackable",
            () => FakeHost.NotAttackable.Add((uint)Other));
    });

    private static void OffTargetAndArea() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var rig = new AttackRig();
        Check.True(rig.C.ScannedTargets.Any(t => t.Id == Other), "the rig: the second monster is scanned");
        Claim(Atk);
        rig.Attack();
        CombatHook.Decide(OpenSeq(Atk), Atk, (uint)drain, (uint)Other, 0);
        rig.Attack();
        Check.Eq(FakeHost.Casts.Single(), ((uint)Other, drain), "cast at the other scanned monster");
        Check.Eq(rig.PendingOffensive, 0, "not the fight target: no pending-offensive judgement");
        Check.True(rig.LastWasArea, "and no per-target miss count");
        Check.Eq(rig.C.activeTargetId, Target, "the fight target stays locked");

        var area = new AttackRig();
        Claim(Atk);
        area.Attack();
        CombatHook.Decide(OpenSeq(Atk), Atk, (uint)Id("Flame Bolt VI"), 0, RynthAiCombat.FlagArea);
        area.Attack();
        Check.Eq(FakeHost.Casts.Single(), ((uint)Target, Id("Flame Bolt VI")), "the area answer is cast at the fight target");
        Check.Eq(area.PendingOffensive, 0, "flagged AREA: no pending-offensive judgement");
        Check.True(area.LastWasArea, "an area cast for the bookkeeping");

        var self = new AttackRig();
        Claim(Atk);
        self.Attack();
        CombatHook.Decide(OpenSeq(Atk), Atk, (uint)drain, Player, 0);
        self.Attack();
        Check.Eq(FakeHost.Casts.Single().Target, Player, "the player is a valid target");
    });

    private static void ThinkHoldsWithoutStamp() => Guard(() =>
    {
        bool pacer = ActionPacer.Enabled;
        ActionPacer.Enabled = false;
        try
        {
            // Control: no hook, one Think casts and stamps the attack time.
            var off = new AttackRig(think: true);
            off.C.Think();
            Check.Eq(off.LastCast, "Flame Bolt VI", "hook off: Think casts the bolt");
            Check.True(off.C.GetStateSnapshot().LastAttackCmd != DateTime.MinValue, "hook off: lastAttackCmd stamped");

            var on = new AttackRig(think: true);
            Claim(Atk);
            on.C.Think();
            Check.Eq(FakeHost.Casts.Count, 0, "hook on: Think sends nothing while waiting");
            Check.True(on.C.ScriptHeld, "held for the answer");
            var snap = on.C.GetStateSnapshot();
            Check.Eq(snap.LastAttackCmd, DateTime.MinValue, "lastAttackCmd not stamped while held");
            Check.Eq(snap.ActiveTargetId, Target, "the target lock stays");
            Check.True(on.C.HasEngageableTarget, "still engaged (nav stays paused, in-combat lines stay)");
            Check.Eq(snap.ScriptHookOwner, "test", "/ra combat shows the owner");
            Check.Eq(snap.ScriptHookKinds, Atk, "and the kinds");
            Check.True(snap.ScriptHook.Contains("'test' holds attack"), "and the summary line: " + snap.ScriptHook);

            CombatHook.Decide(OpenSeq(Atk), Atk, (uint)Id("Drain Health Other V"), 0, 0);
            on.C.Think();
            Check.Eq(on.LastCast, "Drain Health Other V", "the next Think casts the answer");
            Check.True(on.C.GetStateSnapshot().LastAttackCmd != DateTime.MinValue, "and stamps the attack time");
        }
        finally
        {
            ActionPacer.Enabled = pacer;
            ActionPacer.Forget("test");
        }
    });

    // -- The heal rig: a caster in Magic mode (Use Kits in Magic Mode on) with a healing kit in
    //    the pack, at 40% health under Heal At 60, a monster scanned 10 yd north.

    private sealed class HealRig
    {
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public readonly CombatManager C;
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;

        public HealRig(int healthPct = 40, int mode = CombatMode.Magic, int emergencyAt = 0, bool peaceKits = false, Action? setup = null)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.CombatModeCalls = peaceKits;   // the peace-for-kits path needs ChangeCombatMode
            FakeHost.UseOnCalls = true;
            FakeHost.CombatModeValue = mode;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, 33)] = (400, 2);   // Life Magic trained
            FakeHost.Names[Kit] = "Plain Healing Kit";
            FakeHost.PlayerPose = (0x0101001Bu, 0f, 0f, 0f);
            FakeHost.Positions[Player] = FakeHost.PlayerPose;
            FakeHost.Names[(uint)Target] = "Drudge Skulker";
            FakeHost.Positions[(uint)Target] = (0x0101001Bu, 0f, 10f, 0f);
            FakeHost.ItemTypes[(uint)Target] = 0x10;
            setup?.Invoke();
            var host = Host = FakeHost.Create(Player);
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = emergencyAt, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, EnableBuffing = false,
                UseKitsInMagicMode = true, PeaceModeForKits = peaceKits, KitMinSuccessPct = 0, MonsterRange = 20,
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            sm.RefreshKnownSpells();
            V = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = (uint)healthPct, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
            var cache = Cache = FakeHost.MakeCache(host, Player, new[] { Kit, (uint)Target });
            B = new BuffManager(host, S, sm, V);
            B.SetCharacterSkills(skills);
            B.SetWorldObjectCache(cache);
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.ScanNearbyTargets();
            B.SetCombatManager(C);
        }

        public bool KitUsed => FakeHost.UsesOn.Any(u => u.Source == Kit);
    }

    private static void HealBeforeKit() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");

        var off = new HealRig();
        Check.True(off.C.ScannedTargets.Any(t => t.Id == Target), "the rig: the monster is scanned");
        Check.True(off.B.CheckVitals(), "hook off: the vitals step acts");
        Check.True(off.KitUsed, "hook off: the kit goes first (Use Kits in Magic Mode)");
        Check.Eq(FakeHost.Casts.Count, 0, "hook off: no spell");

        var on = new HealRig();
        Claim(HealKind);
        Check.True(on.B.CheckVitals(), "hook on: waiting for the answer counts as handled");
        Check.False(on.KitUsed, "hook on: no kit while the handler decides");
        Check.Eq(FakeHost.Casts.Count, 0, "nothing cast yet");
        Check.True(CombatHook.Poll(HealKind, out var req), "a heal request is open");
        Check.Eq((req.TargetId, req.HealLine, req.HealthPct, req.InCombat), (0u, 60, 40, 1), "heal ctx: no target, Heal At 60, 40% health, in combat");
        Check.Eq(req.DeadlineMs - req.IssuedAtMs, (long)RynthAiCombat.DefaultHealTimeoutMs, "the heal deadline");

        Check.True(CombatHook.Decide(req.Seq, HealKind, (uint)drain, (uint)Target, 0), "drain the monster");
        Check.True(on.B.CheckVitals(), "the drain is cast");
        Check.Eq(FakeHost.Casts.Single(), ((uint)Target, drain), "Drain Health Other V at the monster's id");
        Check.False(on.KitUsed, "still no kit");
        Check.Eq(on.B.PendingSpellId, drain, "CastVital: the pending vital cast");
        Check.Eq(on.B.BuffingLabel, BuffManager.LabelHealing, "labelled Healing, like Heal Self");
        Check.Eq(on.B.CastInFlightName, "Drain Health Other V", "the cast in flight");
        Check.Eq(CombatHook.Heal.Casts, 1, "counted");

        // Target 0 is the player (a plain self heal).
        var self = new HealRig();
        Claim(HealKind);
        self.B.CheckVitals();
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)Id("Heal Self V"), 0, 0);
        self.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Single(), (Player, Id("Heal Self V")), "target 0: the player");
    });

    private static void PeaceForKitRefused() => Guard(() =>
    {
        var rig = new HealRig(mode: CombatMode.Melee, peaceKits: true);
        Check.True(rig.B.LastCastAttemptAt == DateTime.MinValue, "set-up: no cast attempt yet");
        FakeHost.CombatModeRefuse = true;   // the engine turns the mode change away
        const int N = 6;
        for (int i = 0; i < N; i++) rig.B.CheckVitals();
        Check.Eq(FakeHost.CombatModeRefusals, N, "the peace request is asked again on every tick");
        Check.True(rig.B.LastCastAttemptAt == DateTime.MinValue, "_lastCastAttempt stays MinValue: a refusal is not an attempt");
        var wait = typeof(BuffManager).GetField("_healWaitKind", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check.False(((string?)wait.GetValue(rig.B) ?? "").Contains("peace mode"), "no peace-mode wait is recorded");
        Check.False(rig.KitUsed, "and the kit is not used out of peace");

        FakeHost.CombatModeRefuse = false;   // accepted: now it counts
        rig.B.CheckVitals();
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "the peace request goes out");
        Check.True(rig.B.LastCastAttemptAt != DateTime.MinValue, "and only now the attempt is stamped");
    });

    private static void HealFallsBack() => Guard(() =>
    {
        void Case(string what, Action<uint> answer, string? log = null)
        {
            _now += 10_000;   // past the reject log's 5 s throttle
            var rig = new HealRig();
            Claim(HealKind, heal: 300);
            Check.True(rig.B.CheckVitals(), $"{what}: waiting first");
            Check.False(rig.KitUsed, $"{what}: no kit while waiting");
            answer(OpenSeq(HealKind));
            Check.True(rig.B.CheckVitals(), $"{what}: the check acts");
            Check.True(rig.KitUsed, $"{what}: the kit, this same check");
            Check.Eq(FakeHost.Casts.Count, 0, $"{what}: no spell");
            if (log != null) Check.True(FakeHost.Logs.Any(l => l.Contains(log)), $"{what}: logged");
        }

        Case("declined", seq => CombatHook.Decide(seq, HealKind, 0, 0, 0));
        Case("an unknown spell", seq => CombatHook.Decide(seq, HealKind, 999_999, 0, 0), "rejected (spell 999999 isn't known");
        Case("a target outside the scan", seq => CombatHook.Decide(seq, HealKind, (uint)Id("Drain Health Other V"), (uint)Stranger, 0), "rejected (the target is not");
        Case("a heal (not a drain) at a monster", seq => CombatHook.Decide(seq, HealKind, (uint)Id("Heal Self V"), (uint)Target, 0), "at a monster is not a drain");
        Case("Infuse Health (caster to target) at a monster", seq => CombatHook.Decide(seq, HealKind, (uint)Id("Infuse Health Other V"), (uint)Target, 0), "at a monster is not a drain");
        Case("no answer by the deadline", _ => _now += 301, "heal request");
        Check.Eq(CombatHook.Heal.Timeouts, 1, "the timeout counted");
    });

    private static void EmergencyStaysNative() => Guard(() =>
    {
        // Melee stance, 20% health, Emergency Heal At 30: the emergency step uses the kit first.
        var em = new HealRig(healthPct: 20, mode: CombatMode.Melee, emergencyAt: 30);
        Claim(HealKind);
        Check.True(em.B.CheckVitals(), "emergency: handled");
        Check.True(em.KitUsed, "emergency: the kit");
        Check.Eq(CombatHook.Heal.Opened, 0, "the handler is not asked");

        // Control: the same rig above the emergency line asks the handler and uses no kit.
        var normal = new HealRig(healthPct: 40, mode: CombatMode.Melee, emergencyAt: 30);
        Claim(HealKind);
        Check.True(normal.B.CheckVitals(), "40%: handled (waiting)");
        Check.False(normal.KitUsed, "40%: no kit");
        Check.Eq(CombatHook.Heal.Opened, 1, "40%: the handler is asked");
    });

    private static unsafe void NativeTable() => Guard(() =>
    {
        Check.Eq(CombatHookInterface.Query("RynthAi.Combat", 2), IntPtr.Zero, "a newer version than it has: none");
        Check.Eq(CombatHookInterface.Query("RynthAi.Script", 1) == IntPtr.Zero, true, "another interface's name: none");
        var t = (RynthAiCombatApiV1*)CombatHookInterface.Query(RynthAiCombatApiV1.InterfaceName, 1);
        Check.True(t != null, "v1 is served");
        Check.Eq((t->Version, t->StructSize), (1u, (uint)sizeof(RynthAiCombatApiV1)), "version and size");

        byte* owner = stackalloc byte[] { (byte)'L', (byte)'u', (byte)'a', 0 };
        Check.Eq(t->Claim(Atk | HealKind | 0x80, 1500, 0, 0, owner), (int)(Atk | HealKind), "unknown kind bits are dropped");
        Check.Eq(CombatHook.Owner, "Lua", "owner from UTF-8");

        var rig = new AttackRig();
        rig.Attack();
        CombatRequestV1 req = default;
        req.StructSize = (uint)sizeof(CombatRequestV1);
        Check.Eq(t->Poll(Atk, &req), 1, "Poll through the table");
        Check.Eq(req.TargetId, (uint)Target, "filled in");
        Check.Eq(t->Poll(HealKind, &req), 0, "no heal request");

        // An older, smaller caller struct: nothing written past its StructSize.
        byte* buf = stackalloc byte[sizeof(CombatRequestV1) + 8];
        for (int i = 0; i < sizeof(CombatRequestV1) + 8; i++) buf[i] = 0xCD;
        ((CombatRequestV1*)buf)->StructSize = 12;
        Check.Eq(t->Poll(Atk, (CombatRequestV1*)buf), 1, "a 12-byte caller");
        Check.Eq(((CombatRequestV1*)buf)->Seq, req.Seq, "its fields are filled");
        bool untouched = true;
        for (int i = 12; i < sizeof(CombatRequestV1) + 8; i++) untouched &= buf[i] == 0xCD;
        Check.True(untouched, "and nothing past them");

        Check.Eq(t->Decide(req.Seq, Atk, (uint)Id("Drain Health Other V"), 0, 0), 1, "Decide through the table");
        Check.Eq(t->Decide(req.Seq, Atk, 0, 0, 0), 0, "again: stale");
        rig.Attack();
        Check.Eq(rig.LastCast, "Drain Health Other V", "cast");
        t->Release(Atk | HealKind);
        Check.Eq(CombatHook.Kinds, 0u, "Release drops the kinds");
        Check.Eq(t->Poll(Atk, &req), 0, "nothing to poll");
        Check.Eq(t->GetScanned(null, null, 0), 0, "GetScanned without a plugin instance: 0");
    });

    private static void AttackRefusalRemembered() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");

        // Control: the same script spell twice, no refusal between: cast both times.
        var ctl = new AttackRig();
        Claim(Atk);
        ScriptCast(ctl, drain, flags: RynthAiCombat.FlagArea);
        ScriptCast(ctl, drain, flags: RynthAiCombat.FlagArea);
        Check.Eq(FakeHost.Casts.Count(c => c.SpellId == drain), 2, "control: no refusal, cast twice");

        // A single-target cast at the fight target: parked too (the native path's uncastable mark is
        // purged by every known-spell refresh, so the script side does not rely on it).
        _now += 10_000;
        var single = new AttackRig();
        Claim(Atk);
        ScriptCast(single, drain);
        Check.Eq(single.LastCast, "Drain Health Other V", "single: cast");
        single.C.HandleChatForDebuffs(NoComponents);
        int rejects = CombatHook.Attack.Rejects;
        ScriptCast(single, drain);
        Check.Eq(single.LastCast, "Flame Bolt VI", "single: refused before, so rejected and RynthAi's bolt is cast");
        Check.Eq(CombatHook.Attack.Rejects, rejects + 1, "single: counted as a rejection");
        Check.True(single.C.ScriptSpellParked(drain), "single: parked");
        Check.True(FakeHost.Logs.Any(l => l.Contains("rejected (spell " + drain + " was refused by the server, parked for")), "single: logged why");

        // An area cast arms no pending judgement: the script's own refusal memory parks it.
        _now += 10_000;
        var area = new AttackRig();
        Claim(Atk);
        ScriptCast(area, drain, flags: RynthAiCombat.FlagArea);
        Check.Eq(area.PendingOffensive, 0, "area: no pending judgement (the native path can't see the refusal)");
        area.C.HandleChatForDebuffs(NoComponents);
        Check.True(area.C.ScriptSpellParked(drain), "area: the refused script spell is parked");
        rejects = CombatHook.Attack.Rejects;
        ScriptCast(area, drain, flags: RynthAiCombat.FlagArea);
        Check.Eq(area.LastCast, "Flame Bolt VI", "area: parked, so rejected and RynthAi's bolt is cast");
        Check.Eq(CombatHook.Attack.Rejects, rejects + 1, "area: counted as a rejection");
        Check.True(FakeHost.Logs.Any(l => l.Contains("was refused by the server, parked for")), "area: logged why");

        // RynthAi's own cast after the script's: a refusal then is not the script spell's.
        _now += 10_000;
        var mixed = new AttackRig();
        Claim(Atk);
        ScriptCast(mixed, drain, flags: RynthAiCombat.FlagArea);
        mixed.Attack();
        CombatHook.Decide(OpenSeq(Atk), Atk, 0, 0, 0);   // decline: RynthAi's bolt goes out
        mixed.Attack();
        Check.Eq(mixed.LastCast, "Flame Bolt VI", "mixed: RynthAi's bolt was the last cast");
        mixed.C.HandleChatForDebuffs(NoComponents);
        Check.False(mixed.C.ScriptSpellParked(drain), "mixed: the refusal is not pinned on the script's earlier spell");
    });

    private static void HealRefusalRemembered() => Guard(() =>
    {
        int heal = Id("Heal Self V");
        var rig = new HealRig();
        Claim(HealKind);
        rig.B.CheckVitals();
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)heal, 0, 0);
        rig.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Single(), (Player, heal), "the script's Heal Self V is cast");
        rig.B.OnChatWindowText(NoComponents, 0);
        Check.Eq(rig.B.PendingSpellId, 0, "the refusal cleared the pending cast");

        int rejects = CombatHook.Heal.Rejects;
        Check.True(rig.B.CheckVitals(), "the next check: waiting for the answer");
        Check.False(rig.KitUsed, "no kit while waiting");
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)heal, 0, 0);
        Check.True(rig.B.CheckVitals(), "the check acts");
        Check.Eq(FakeHost.Casts.Count, 1, "the refused level is not cast again");
        Check.Eq(CombatHook.Heal.Rejects, rejects + 1, "rejected");
        Check.True(rig.KitUsed, "the kit, this same check");
        Check.True(FakeHost.Logs.Any(l => l.Contains("rejected (spell " + heal + " has no components")), "logged why");
    });

    // Monster rule Priority 0 = never attacked: the hook's view of the fight.
    private static List<MonsterRule> Ignoring(string name) => new()
    {
        new MonsterRule { Name = "Default", DamageType = "Fire", UseBolt = true },
        new MonsterRule { Name = name, Priority = 0 },
    };

    private static void Rescan(CombatManager c)
    {
        typeof(CombatManager).GetField("_lastScanTime", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(c, DateTime.MinValue);
        c.ScanNearbyTargets();
    }

    /// <summary>ctx.monsters as RynthLua gets it: RynthAi.Combat GetScanned (RynthAiPlugin.CombatHookScanned).</summary>
    private static unsafe uint[] HookScanned(CombatManager c)
    {
        var plugin = new RynthAiPlugin();
        typeof(RynthAiPlugin).GetField("_combatManager", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, c);
        uint* ids = stackalloc uint[16];
        float* dist = stackalloc float[16];
        int n = plugin.CombatHookScanned(ids, dist, 16);
        var list = new uint[Math.Min(n, 16)];
        for (int i = 0; i < list.Length; i++) list[i] = ids[i];
        return list;
    }

    private static void HookScanSkipsIgnored() => Guard(() =>
    {
        var control = new AttackRig();
        Check.True(HookScanned(control.C).Contains((uint)Other), "control: the Slinker is in ctx.monsters");

        var rig = new AttackRig();
        rig.S.MonsterRules = Ignoring("Drudge Slinker");
        Rescan(rig.C);
        uint[] seen = HookScanned(rig.C);
        Check.True(seen.Contains((uint)Target), "the Skulker is in ctx.monsters");
        Check.False(seen.Contains((uint)Other), "the ignored Slinker is not");

        rig.C.HandleChatForDamage("Drudge Slinker bites you for 3 points of piercing damage!");
        Rescan(rig.C);
        Check.False(HookScanned(rig.C).Contains((uint)Other), "hitting us: the ignored Slinker is still not in ctx.monsters");
    });

    private static void HookRejectsIgnoredTarget() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");

        // The fight target's rule turned to Priority 0 before Think dropped it: a script answer at
        // it (target 0 = the fight target) is rejected, and RynthAi casts its own choice instead.
        var rig = new AttackRig();
        rig.S.MonsterRules = Ignoring("Drudge Skulker");
        Claim(Atk);
        int rejects = CombatHook.Attack.Rejects;
        ScriptCast(rig, drain);
        Check.Eq(CombatHook.Attack.Rejects, rejects + 1, "rejected");
        Check.True(FakeHost.Logs.Any(l => l.Contains("the target is ignored (monster rule Priority 0)")), "logged why");
        Check.False(FakeHost.Casts.Any(c => c.SpellId == drain), "the script's drain is not cast");
        Check.Eq(rig.LastCast, "Flame Bolt VI", "RynthAi's own choice goes instead");

        // Hitting us changes nothing (Priority 0 = never attacked, no exception): still rejected.
        var hit = new AttackRig();
        hit.S.MonsterRules = Ignoring("Drudge Skulker");
        hit.C.HandleChatForDamage("Drudge Skulker bites you for 3 points of piercing damage!");
        Claim(Atk);
        int rejects2 = CombatHook.Attack.Rejects;
        ScriptCast(hit, drain);
        Check.Eq(CombatHook.Attack.Rejects, rejects2 + 1, "hitting us: still rejected");
        Check.False(FakeHost.Casts.Any(c => c.SpellId == drain), "hitting us: the script's drain is not cast");
    });

    private static void HookHealRejectsIgnoredTarget() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");

        // Control: the same drain at the same fight target with no Priority 0 rule is cast.
        var ok = new HealRig();
        ok.C.activeTargetId = Target;
        Claim(HealKind);
        ok.B.CheckVitals();
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)drain, (uint)Target, 0);
        ok.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Single(), ((uint)Target, drain), "control: the drain at the fight target goes out");

        // The fight target's rule turned to Priority 0 (still locked and scanned): rejected.
        var rig = new HealRig();
        rig.C.activeTargetId = Target;
        rig.S.MonsterRules = Ignoring("Drudge Skulker");
        Claim(HealKind);
        int rejects = CombatHook.Heal.Rejects;
        rig.B.CheckVitals();
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)drain, (uint)Target, 0);
        rig.B.CheckVitals();
        Check.Eq(CombatHook.Heal.Rejects, rejects + 1, "rejected");
        Check.True(FakeHost.Logs.Any(l => l.Contains("the target is ignored (monster rule Priority 0)")), "logged why");
        Check.False(FakeHost.Casts.Any(c => c.SpellId == drain), "the drain is not cast");
    });

    // Recovery retry: only RynthAi's own recovery vitals retry fast after "not enough mana"; a script's heal
    // (even the same Heal Self) keeps the rest it had, with Mana Conversion trained.
    private static void ScriptHealNoFastRetry() => Guard(() =>
    {
        int heal = Id("Heal Self V");
        var rig = new HealRig();
        FakeHost.Skills[(Player, 16)] = (300, 2);   // Mana Conversion trained
        Claim(HealKind);
        rig.B.CheckVitals();
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)heal, 0, 0);
        rig.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Single(), (Player, heal), "the script's Heal Self V is cast");
        rig.B.OnChatWindowText("You don't have enough Mana to cast this spell.", 0);
        Check.False(rig.B.RecoveryRetryOpen, "a script heal: no fast-retry episode");
        Check.True(FakeHost.Logs.Any(l => l.Contains("not enough mana; resting the family 2s")), "the 2 s heal rest, as before");
        Check.False(FakeHost.Logs.Any(l => l.Contains("retrying in 200 ms")), "no retry line");
    });

    private static void NoJudgeSkipsBlacklist() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");

        void Miss(AttackRig rig)
        {
            // The refusal window and the damage settle both pass with no damage seen.
            SetField(rig.C, "_pendingOffensiveCastAt", DateTime.Now.AddSeconds(-5));
            CallPrivate(rig.C, "JudgePendingOffensiveCast");
            if (Field<DateTime>(rig.C, "_pendingJudgeCastAt") != DateTime.MinValue)
                SetField(rig.C, "_pendingJudgeCastAt", DateTime.Now.AddSeconds(-30));
            CallPrivate(rig.C, "JudgePendingCast");
        }

        foreach (bool noJudge in new[] { false, true })
        {
            string what = noJudge ? "NOJUDGE" : "no flag";
            var rig = new AttackRig();
            rig.S.BlacklistAttempts = 2;
            rig.S.EnableRaycasting = false;
            Claim(Atk);
            for (int i = 0; i < 2; i++)
            {
                ScriptCast(rig, drain, flags: noJudge ? RynthAiCombat.FlagNoJudge : 0);
                Check.Eq(FakeHost.Casts.Count, i + 1, $"{what}: cast {i + 1} went out");
                Check.Eq(rig.PendingOffensive, noJudge ? 0 : drain, $"{what}: cast {i + 1} pending judgement");
                Miss(rig);
            }
            if (noJudge)
                Check.Eq(rig.C.activeTargetId, Target, "NOJUDGE: two no-damage casts, the target is kept");
            else
                Check.Eq(rig.C.activeTargetId, 0, "no flag: two no-damage casts blacklist and drop the target");
        }
    });

    // In a client log, three healthy targets were blacklisted "after 3 no-damage casts" whose casts
    // the server had refused with UseDone 0x1D.
    private static void RefusedCastsNotJudged() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        foreach (uint answer in new uint[] { 0, ActionPacer.TooBusyErr })
        {
            string what = answer == 0 ? "answered 0" : "answered 0x1D";
            var rig = new AttackRig(setup: () => { FakeHost.ServerGates = true; FakeHost.LastUseDoneCalls = true; });
            rig.S.BlacklistAttempts = 2;
            rig.S.EnableRaycasting = false;
            Claim(Atk);
            for (int i = 0; i < 2; i++)
            {
                ScriptCast(rig, drain);
                Check.Eq(FakeHost.Casts.Count, i + 1, $"{what}: cast {i + 1} went out");
                FakeHost.UseDone(answer);
                SetField(rig.C, "_pendingOffensiveCastAt", DateTime.Now.AddSeconds(-5));
                CallPrivate(rig.C, "JudgePendingOffensiveCast");
                if (Field<DateTime>(rig.C, "_pendingJudgeCastAt") != DateTime.MinValue)
                    SetField(rig.C, "_pendingJudgeCastAt", DateTime.Now.AddSeconds(-30));
                CallPrivate(rig.C, "JudgePendingCast");
            }
            if (answer == 0)
                Check.Eq(rig.C.activeTargetId, 0, "answered 0: two no-damage casts blacklist and drop the target");
            else
            {
                Check.Eq(rig.C.activeTargetId, Target, "answered 0x1D: refusals are not casts, the target is kept");
                Check.Eq(rig.C.RefusedCastsNotJudged, 2, "both kept out of the count");
                Check.True(FakeHost.Logs.Any(l => l.Contains("refused 'too busy' (UseDone 0x1D): not counted")), "logged");
            }
        }
    });

    private static void LastErrIsTheCastsAnswer() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var rig = new AttackRig(setup: () => { FakeHost.ServerGates = true; FakeHost.LastUseDoneCalls = true; });
        FakeHost.UseDone(0);   // an earlier answer
        CombatHook.LastUseDone = () => (FakeHost.UseDoneSeq, FakeHost.LastUseDoneError);
        try
        {
            Claim(Atk);
            ScriptCast(rig, drain);
            uint firstSeq = CombatHook.Attack.PendingSeq;
            rig.Attack();   // the next request opens
            Check.True(CombatHook.Poll(Atk, out var r1), "a request is open");
            Check.Eq(r1.PendingSpell, (uint)drain, "ctx.pending: the drain, in flight");
            Check.Eq(r1.PendingSeq, firstSeq, "ctx.pending.seq");
            Check.Eq(r1.ResolvedSpell, 0u, "ctx.last: no resolved cast yet");
            Check.Eq(r1.ResolvedErr, -1, "no answer: -1 (nil in Lua)");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            CombatHook.ObserveUseDone();
            FakeHost.UseDone(0);    // a later answer (another action) must not overwrite it
            CombatHook.ObserveUseDone();
            Check.True(CombatHook.Poll(Atk, out var r2), "still open");
            Check.Eq((r2.ResolvedSeq, r2.ResolvedSpell), (firstSeq, (uint)drain), "ctx.last: the drain, resolved");
            Check.Eq(r2.ResolvedErr, (int)ActionPacer.TooBusyErr, "its own answer: 0x1D, final");
            Check.Eq(r2.PendingSpell, 0u, "ctx.pending: none");

            // Its first answer missed (two UseDones before RynthAi looked): resolved with none (-1),
            // not with the second one's error.
            ScriptCast(rig, drain);
            uint secondSeq = CombatHook.Attack.PendingSeq;
            Check.True(secondSeq != 0 && secondSeq != firstSeq, "the second cast is in flight");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            FakeHost.UseDone(0);
            CombatHook.ObserveUseDone();
            rig.Attack();
            Check.True(CombatHook.Poll(Atk, out var r3), "a request is open");
            Check.Eq(r3.ResolvedSeq, secondSeq, "ctx.last: the second cast");
            Check.Eq(r3.ResolvedErr, -1, "its answer was not read: -1, not the later 0");

            // No UseDone at all: resolved with none after ResolveTimeoutMs.
            ScriptCast(rig, drain);
            uint thirdSeq = CombatHook.Attack.PendingSeq;
            _now += RynthAiCombat.ResolveTimeoutMs - 1;
            CombatHook.ObserveUseDone();
            Check.Eq(CombatHook.Attack.PendingSeq, thirdSeq, "just under the timeout: still in flight");
            _now += 1;
            CombatHook.ObserveUseDone();
            Check.Eq((CombatHook.Attack.ResolvedSeq, CombatHook.Attack.ResolvedErr), (thirdSeq, -1), "at the timeout: resolved, no answer");

// The next cast goes out before any answer to this one: this one resolves first, with none.
            uint SendOne()
            {
                Claim(Atk);   // the lease ran out with the clock
                var slot = CombatHook.Attack;
                CombatHook.Step(slot, 0x80000777u, () => new CombatRequestV1(), null, out _, out _, out _);
                Check.True(CombatHook.Decide(slot.Request.Seq, Atk, (uint)drain, 0, 0), "decided");
                Check.Eq(CombatHook.Step(slot, 0x80000777u, () => new CombatRequestV1(), null, out _, out _, out _), CombatHook.Outcome.Decided, "taken");
                uint seq = slot.Request.Seq;
                CombatHook.Cast(slot, (uint)drain, 0x80000777u);
                return seq;
            }
            uint fourthSeq = SendOne();
            Check.Eq(CombatHook.Attack.PendingSeq, fourthSeq, "the fourth cast is in flight");
            uint fifthSeq = SendOne();
            Check.Eq(CombatHook.Attack.PendingSeq, fifthSeq, "the fifth cast is in flight");
            Check.Eq((CombatHook.Attack.ResolvedSeq, CombatHook.Attack.ResolvedErr), (fourthSeq, -1), "the fourth: resolved by the fifth's send, no answer");
        }
        finally { CombatHook.LastUseDone = null; }
    });

    // An attack unanswered, then a heal sent: the UseDones answer them in send order.
    private static void AnswersInSendOrder() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        int heal = Id("Heal Other V");
        var rig = new AttackRig(setup: () => { FakeHost.ServerGates = true; FakeHost.LastUseDoneCalls = true; });
        FakeHost.UseDone(0);
        CombatHook.LastUseDone = () => (FakeHost.UseDoneSeq, FakeHost.LastUseDoneError);
        try
        {
            Claim(Atk | HealKind);
            uint Send(CombatHook.Slot slot, uint kind, int spell)
            {
                CombatHook.Step(slot, 0x80000777u, () => new CombatRequestV1(), null, out _, out _, out _);
                Check.True(CombatHook.Decide(slot.Request.Seq, kind, (uint)spell, 0, 0), "decided");
                Check.Eq(CombatHook.Step(slot, 0x80000777u, () => new CombatRequestV1(), null, out _, out _, out _), CombatHook.Outcome.Decided, "taken");
                uint seq = slot.Request.Seq;
                CombatHook.Cast(slot, (uint)spell, 0x80000777u);
                return seq;
            }
            uint atkSeq = Send(CombatHook.Attack, Atk, drain);
            uint healSeq = Send(CombatHook.Heal, HealKind, heal);
            Check.Eq(CombatHook.Attack.PendingSeq, atkSeq, "the attack is in flight");
            Check.Eq(CombatHook.Heal.PendingSeq, healSeq, "the heal is in flight");
            FakeHost.UseDone(0x401);
            CombatHook.ObserveUseDone();
            Check.Eq((CombatHook.Attack.ResolvedSeq, CombatHook.Attack.ResolvedErr), (atkSeq, 0x401), "the first answer is the attack's");
            Check.Eq(CombatHook.Heal.PendingSeq, healSeq, "the heal waits for its own");
            FakeHost.UseDone(0);
            CombatHook.ObserveUseDone();
            Check.Eq((CombatHook.Heal.ResolvedSeq, CombatHook.Heal.ResolvedErr), (healSeq, 0), "the second is the heal's");
        }
        finally { CombatHook.LastUseDone = null; }
    });

    // -- The stuck-cast breaker and the hook: both hook paths mark their cast as a script's
    //    (ActionPacer.MarkScriptCast), and the breaker gives it ScriptCastMinBreakMs from the send.

    private static long _paceT;

    private static void PacerGates()
    {
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.TapJumpCalls = true;
    }

    private static void PaceOn()
    {
        _paceT = 70_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _paceT;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _paceT;
        ActionPacer.StuckCastMs = 8000;
        FakeHost.UseDone(0);   // an earlier action's answer: the observer's baseline
    }

    private static void PaceOff()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
        ActionPacer.StuckCastMs = 8000;
    }

    /// <summary>The pacer's checks every 100 ms for <paramref name="ms"/> (no UseDone comes).</summary>
    private static void RunPacer(RynthCoreHost host, long ms)
    {
        for (long s = 0; s < ms; s += 100) { _paceT += 100; ActionPacer.MayStart(host, "Test", HealNeed.Heal, out _); }
    }

    /// <summary>A heal let ahead of the open cast, answered 'too busy' (the refusal trigger).</summary>
    private static void RefuseAnother(RynthCoreHost host)
    {
        int heal = Id("Heal Self V");
        Check.True(host.CastSpellFor(Player, heal, "Buff", "Heal Self V"), "another send");
        FakeHost.UseDone(ActionPacer.TooBusyErr);
    }

    private static void ScriptHealCast(HealRig rig, int drain)
    {
        Claim(HealKind);
        rig.B.CheckVitals();
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)drain, (uint)Target, 0);
        rig.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Single(), ((uint)Target, drain), "the script's drain went out");
    }

    private static void ScriptAttackNotJumpedEarly() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var own = new AttackRig(setup: PacerGates);
        PaceOn();
        try
        {
            Check.True(own.Host.CastSpellFor((uint)Target, drain, "Combat", "Drain Health Other V"), "control: RynthAi's own cast");
            RunPacer(own.Host, 5500);
            Check.Eq(FakeHost.TapJumps, 1, "control: RynthAi's own cast is jumped at 5 s (cycle 3 s + 2 s)");
        }
        finally { PaceOff(); }

        var rig = new AttackRig(setup: PacerGates);
        PaceOn();
        try
        {
            Claim(Atk);
            ScriptCast(rig, drain);
            Check.Eq(FakeHost.Casts.Last(), ((uint)Target, drain), "the script's drain went out");
            RunPacer(rig.Host, 5500);
            Check.Eq(FakeHost.TapJumps, 0, "a script attack: no jump in 5.5 s");
        }
        finally { PaceOff(); }
    });

    private static void ScriptHealNotJumpedEarly() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var rig = new HealRig(setup: PacerGates);
        PaceOn();
        try
        {
            ScriptHealCast(rig, drain);
            RunPacer(rig.Host, 5500);
            Check.Eq(FakeHost.TapJumps, 0, "a script heal: no jump in 5.5 s");
        }
        finally { PaceOff(); }
    });

    private static void ScriptAttackRefusalNoEarlyJump() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var own = new AttackRig(setup: PacerGates);
        PaceOn();
        try
        {
            Check.True(own.Host.CastSpellFor((uint)Target, drain, "Combat", "Drain Health Other V"), "control: RynthAi's own cast");
            RunPacer(own.Host, 3500);
            Check.Eq(FakeHost.TapJumps, 0, "control: no jump before the refusal");
            RefuseAnother(own.Host);
            RunPacer(own.Host, 600);
            Check.Eq(FakeHost.TapJumps, 1, "control: a 0x1D past its cycle + 1 s (4 s) jumps RynthAi's own cast at once");
        }
        finally { PaceOff(); }

        var rig = new AttackRig(setup: PacerGates);
        PaceOn();
        try
        {
            Claim(Atk);
            ScriptCast(rig, drain);
            RunPacer(rig.Host, 3500);
            RefuseAnother(rig.Host);
            RunPacer(rig.Host, 2000);
            Check.Eq(FakeHost.TapJumps, 0, "a script attack: a 0x1D at 3.5 s does not jump it by 5.5 s");
        }
        finally { PaceOff(); }
    });

    private static void ScriptHealRefusalNoEarlyJump() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var rig = new HealRig(setup: PacerGates);
        PaceOn();
        try
        {
            ScriptHealCast(rig, drain);
            RunPacer(rig.Host, 3500);
            RefuseAnother(rig.Host);
            RunPacer(rig.Host, 2000);
            Check.Eq(FakeHost.TapJumps, 0, "a script heal: a 0x1D at 3.5 s does not jump it by 5.5 s");
        }
        finally { PaceOff(); }
    });

    private static void AnswerDuringSendIsTheCasts() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        var rig = new AttackRig(setup: () => { FakeHost.ServerGates = true; FakeHost.LastUseDoneCalls = true; });
        FakeHost.UseDone(0);   // an earlier answer
        CombatHook.LastUseDone = () => (FakeHost.UseDoneSeq, FakeHost.LastUseDoneError);
        try
        {
            Claim(Atk);
            FakeHost.AfterCastSpell = () => FakeHost.UseDone(0x402);   // the answer lands before CombatHook.Cast runs
            ScriptCast(rig, drain);
            FakeHost.AfterCastSpell = null;
            uint seq = CombatHook.Attack.PendingSeq;
            Check.True(seq != 0, "the drain is in flight");
            rig.Attack();   // the next request opens (ctx as the script sees it)
            Check.True(CombatHook.Poll(Atk, out var r), "a request is open");
            Check.Eq((r.ResolvedSeq, r.ResolvedSpell), (seq, (uint)drain), "ctx.last: the drain");
            Check.Eq(r.ResolvedErr, 0x402, "ctx.last.err: the answer that came during the send");
        }
        finally { CombatHook.LastUseDone = null; FakeHost.AfterCastSpell = null; }
    });

    private static void ScriptCastAttribution() => Guard(() =>
    {
        int shock = Id("Shock Wave IV");
        int drain = Id("Drain Health Other V");
        const uint Wcid = 0x1234;
        var store = new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore();
        var rig = new AttackRig(setup: () =>
        {
            foreach (var row in SpellTable.Named("Shock Wave IV")) FakeHost.Spellbook.Add((uint)row.Id);
        });
        rig.C.SetDamageStores(null, store);
        SetField(rig.C, "_fightTargetWcid", Wcid);
        SetField(rig.C, "_fightTargetName", "Drudge Skulker");
        Claim(Atk);

        // Control: RynthAi's own bolt (a decline) tags Fire, tier 6.
        rig.Attack();
        CombatHook.Decide(OpenSeq(Atk), Atk, 0, 0, 0);
        rig.Attack();
        Check.Eq(rig.LastCast, "Flame Bolt VI", "control: RynthAi's bolt");
        Check.Eq((Field<string>(rig.C, "_lastCastElement"), Field<int>(rig.C, "_lastCastTier")), ("Fire", 6), "control: Fire, tier 6");
        int count = Field<int>(rig.C, "_fightCastCount");

        ScriptCast(rig, shock);
        Check.Eq(rig.LastCast, "Shock Wave IV", "the script's Shock Wave IV is cast");
        Check.Eq(Field<string>(rig.C, "_lastCastElement"), "Bludgeon", "element from the spell (not the rule's Fire, not 'Shock' = Lightning)");
        Check.Eq(Field<int>(rig.C, "_lastCastTier"), 4, "tier = the spell's level");
        Check.Eq(Field<int>(rig.C, "_fightCastCount"), count + 1, "counted for casts-to-kill");
        Check.Eq(store.GetLastTier(Wcid), 4, "the Damage tab's latest tier");
        Check.Eq((Field<uint>(rig.C, "_lastFightWcid"), Field<string>(rig.C, "_lastFightElement"), Field<int>(rig.C, "_lastFightTier")),
            (Wcid, "Bludgeon", 4), "the last-fight snapshot");
        Check.Eq(Field<uint>(rig.C, "_lastCastWeaponId"), Field<uint>(rig.C, "_lastFightWeaponId"), "the weapon is snapshotted with it");

        ScriptCast(rig, shock, flags: RynthAiCombat.FlagArea);
        Check.Eq(Field<int>(rig.C, "_lastCastTier"), -4, "an area cast is filed with the rings (negative tier)");
        Check.Eq(Field<int>(rig.C, "_fightCastCount"), count + 2, "counted");

        ScriptCast(rig, drain);
        Check.Eq(Field<string>(rig.C, "_lastCastElement"), "Fire", "no element in the spell's name: the rule's element");
        Check.Eq(Field<int>(rig.C, "_lastCastTier"), 5, "and its level");
        Check.Eq(Field<int>(rig.C, "_fightCastCount"), count + 3, "counted");

        ScriptCast(rig, shock, flags: RynthAiCombat.FlagNoJudge);
        Check.Eq(Field<int>(rig.C, "_fightCastCount"), count + 3, "NOJUDGE (not meant to damage): not counted");
        Check.Eq(Field<int>(rig.C, "_lastCastTier"), 5, "and not tagged");

        ScriptCast(rig, shock, (uint)Other);
        Check.Eq(FakeHost.Casts[^1].Target, (uint)Other, "cast at another scanned monster");
        Check.Eq(Field<int>(rig.C, "_fightCastCount"), count + 3, "not the fight target: not counted");

        // A caster with neither War nor Void: a Life drain is filed under "Physical" (RynthAi's own
        // key for vital drains), not the plan's element.
        FakeHost.Skills[(Player, 34)] = (0, 1);
        FakeHost.Skills[(Player, 43)] = (0, 1);
        var skills = new CharacterSkills(FakeHost.Create(Player));
        skills.SetPlayerId(Player);
        rig.C.SetCharacterSkills(skills);
        ScriptCast(rig, drain);
        Check.Eq(rig.LastCast, "Drain Health Other V", "untrained: the drain is cast");
        Check.Eq(Field<string>(rig.C, "_lastCastElement"), "Physical", "untrained: a Life drain is filed under Physical, not the rule's Fire");
    });

    private static void HealRequestNamesFightTarget() => Guard(() =>
    {
        int heal = Id("Heal Self V");

        // Control: no fight target, the heal request names none.
        var none = new HealRig();
        Claim(HealKind);
        none.B.CheckVitals();
        Check.True(CombatHook.Poll(HealKind, out var r0), "a heal request is open");
        Check.Eq((r0.TargetId, r0.TargetHealth), (0u, -1f), "no fight target: target 0, health unknown");

        CombatHook.Release(HealKind);   // the control's request (about no target) is not this one's
        var rig = new HealRig();
        rig.C.activeTargetId = Target;
        rig.Cache.OnUpdateHealth((uint)Target, 0.35f);
        Claim(HealKind);
        rig.B.CheckVitals();
        Check.True(CombatHook.Poll(HealKind, out var req), "a heal request is open");
        Check.Eq(req.TargetId, (uint)Target, "it names the fight target");
        Check.Eq(req.TargetHealth, 0.35f, "and its health");
        Check.True(CombatHook.Decide(req.Seq, HealKind, (uint)heal, 0, 0), "a nil (0) answer target");
        rig.B.CheckVitals();
        Check.Eq(FakeHost.Casts.Single(), (Player, heal), "still heals the player, not the fight target");

        // The fight target moves on while the request is open: the heal request is kept (same seq,
        // same deadline). Re-asking would restart the 750 ms deadline on every target swap, and a
        // fight that swaps targets often would never get a heal.
        CombatHook.Release(HealKind);
        _now += 10_000;
        var moved = new HealRig();
        moved.C.activeTargetId = Target;
        Claim(HealKind);
        int opened0 = CombatHook.Heal.Opened, timeouts0 = CombatHook.Heal.Timeouts;
        moved.B.CheckVitals();
        Check.True(CombatHook.Poll(HealKind, out var open1), "a heal request is open");
        moved.C.activeTargetId = 0;
        _now += 300;
        moved.B.CheckVitals();
        Check.True(CombatHook.Poll(HealKind, out var kept) && kept.Seq == open1.Seq && kept.DeadlineMs == open1.DeadlineMs,
            "the fight target changed: the same heal request, its deadline unchanged");
        moved.C.activeTargetId = Target;
        _now += 300;
        moved.B.CheckVitals();
        Check.Eq(CombatHook.Heal.Opened - opened0, 1, "swapping back: still the one request");
        moved.C.activeTargetId = 0;
        _now += 300;   // 900 ms after it opened: past the 750 ms heal deadline
        Check.True(moved.B.CheckVitals(), "the check acts");
        Check.True(moved.KitUsed, "past the deadline: the kit, this same check (target swaps did not hold the heal)");
        Check.Eq(CombatHook.Heal.Timeouts - timeouts0, 1, "counted as a timeout");

        // Control: an attack request IS asked afresh when its target changes.
        Check.True(CombatHook.Attack.Kind == RynthAiCombat.KindAttack, "the attack slot");
        CombatHook.Claim(Atk | HealKind, 60_000, 0, 0, "test");
        CombatHook.Step(CombatHook.Attack, (uint)Target, () => default, null, out _, out _, out _);
        uint atk1 = CombatHook.Attack.Request.Seq;
        CombatHook.Step(CombatHook.Attack, (uint)Other, () => default, null, out _, out _, out _);
        Check.True(CombatHook.Attack.Request.Seq != atk1 && CombatHook.Attack.Request.TargetId == (uint)Other,
            "an attack about another target: a fresh request");
    });

    private static void RefusalNotMisattributed() => Guard(() =>
    {
        int drain = Id("Drain Health Other V");
        int heal = Id("Heal Self V");
        const string Missing = "You are missing some required components.";
        MethodInfo issue = typeof(CombatManager).GetMethod("IssueScriptCast", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void ScriptAttack(HealRig rig) =>
            Check.True((bool)issue.Invoke(rig.C, new object[] { drain, (uint)Target, RynthAiCombat.FlagArea, "Fire" })!, "the script's attack cast went out");
        bool HealParked(HealRig rig, int id) => Field<System.Collections.Generic.Dictionary<int, DateTime>>(rig.B, "_noComponentsUntil").ContainsKey(id);

        // Control: the refusal right after the script's cast is the script spell's.
        var ctl = new HealRig();
        ScriptAttack(ctl);
        ctl.C.HandleChatForDebuffs(Missing);
        ctl.B.OnChatWindowText(Missing, 0);
        Check.True(ctl.C.ScriptSpellParked(drain), "control: parked");

        // A heal (BuffManager's CastVital) goes out after the script's attack: the refusal is the heal's.
        var rig = new HealRig();
        ScriptAttack(rig);
        Claim(HealKind);
        rig.B.CheckVitals();
        CombatHook.Decide(OpenSeq(HealKind), HealKind, (uint)heal, 0, 0);
        rig.B.CheckVitals();
        Check.Eq(FakeHost.Casts[^1], (Player, heal), "then a heal went out");
        rig.C.HandleChatForDebuffs(Missing);
        rig.B.OnChatWindowText(Missing, 0);
        Check.False(rig.C.ScriptSpellParked(drain), "the script's attack spell is not parked");
        Check.True(HealParked(rig, heal), "the heal level is the one passed over");

        // The script spell's own success line: a later refusal is not the script spell's.
        var landed = new HealRig();
        ScriptAttack(landed);
        landed.C.HandleChatForDebuffs("You cast Drain Health Other V on Drudge Skulker and drain 40 points of its health.");
        landed.C.HandleChatForDebuffs(Missing);
        Check.False(landed.C.ScriptSpellParked(drain), "landed: not parked");
    });

    private static void ScriptKillShotSwaps() => Guard(() =>
    {
        int bolt = Id("Flame Bolt VI");
        const uint Wcid = 0x2345;
        bool pacer = ActionPacer.Enabled;
        ActionPacer.Enabled = false;
        try
        {
            // One Think cast at the fight target, with the Damage tab saying this monster dies to
            // one Fire tier-6 cast. Returns whether the kill-shot swap was armed.
            (AttackRig rig, bool armed) CastOnce(string what, bool hook, uint flags = 0, bool alternate = true)
            {
                _now += 10_000;
                var rig = new AttackRig(think: true, setup: () => FakeHost.Wcids[(uint)Target] = Wcid);
                var store = new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore();
                for (int i = 0; i < 3; i++) store.RecordKill(0, Wcid, "Drudge Skulker", "Fire", 6, 1, 100);
                rig.C.SetDamageStores(null, store);
                if (!alternate)
                {
                    // Out of the scan now (the rig scanned at construction) and on any rescan.
                    FakeHost.NotAttackable.Add((uint)Other);
                    typeof(CombatManager).GetMethod("ForgetScanned", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(rig.C, new object[] { Other });
                    Check.False(rig.C.ScannedTargets.Any(t => t.Id == Other), $"{what}: the other monster is not a candidate");
                }
                if (hook)
                {
                    Claim(Atk);
                    rig.C.Think();
                    Check.True(rig.C.ScriptHeld, $"{what}: held for the answer");
                    CombatHook.Decide(OpenSeq(Atk), Atk, (uint)bolt, 0, flags);
                }
                rig.C.Think();
                Check.Eq(rig.LastCast, "Flame Bolt VI", $"{what}: the bolt went out");
                Check.Eq(Field<uint>(rig.C, "_lastCastWeaponId"), 0u, $"{what}: (the rig's weapon id the store is seeded for)");
                return (rig, Field<bool>(rig.C, "_predictKillSwapArmed"));
            }

            Check.True(CastOnce("native", hook: false).armed, "native control: RynthAi's own bolt arms the swap");

            var (s, armed) = CastOnce("script", hook: true);
            Check.True(armed, "script: the script's bolt arms the swap the same way");
            // The cast is confirmed (the arm's wait passed): the next Think swaps to the other monster.
            SetField(s.C, "_predictArmCastAt", DateTime.Now.AddSeconds(-10));
            Claim(Atk);
            s.C.Think();
            Check.Eq(s.C.activeTargetId, Other, "script: RynthAi moved to the other monster");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Predicted kill shot 0x" + Target.ToString("X8"))), "script: logged as a predicted kill shot");
            if (!CombatHook.Poll(Atk, out var next)) { s.Attack(); CombatHook.Poll(Atk, out next); }
            Check.Eq(next.TargetId, (uint)Other, "script: the next request names the new target");

            Check.False(CastOnce("no alternate", hook: true, alternate: false).armed, "no other monster: no swap");
            Check.False(CastOnce("nojudge", hook: true, flags: RynthAiCombat.FlagNoJudge).armed, "NOJUDGE: no swap");
            Check.False(CastOnce("area", hook: true, flags: RynthAiCombat.FlagArea).armed, "AREA: no swap");
        }
        finally
        {
            ActionPacer.Enabled = pacer;
            ActionPacer.Forget("test");
        }
    });

    private static void HookMakesWandTheWeapon() => Guard(() =>
    {
        const uint Wand = 0x6000F070;
        bool pacer = ActionPacer.Enabled;
        ActionPacer.Enabled = false;
        try
        {
            // A Life-only caster (War and Void untrained) in peace mode, the listed wand in hand, a
            // monster in range. Returns the combat modes one Think asked for.
            const uint Sword = 0x6000F071;
            int planned = 0;
            System.Collections.Generic.List<int> OneThink(bool hook, uint[]? listed = null, uint inHand = Wand)
            {
                listed ??= new[] { Wand };
                _now += 10_000;
                var rig = new AttackRig(think: true, extraItems: new[] { Wand, Sword }, setup: () =>
                {
                    FakeHost.CombatModeCalls = true;
                    FakeHost.Names[Wand] = "Wand";
                    FakeHost.ItemTypes[Wand] = 0x8000;   // a caster
                    FakeHost.Names[Sword] = "Sword";
                    FakeHost.ItemTypes[Sword] = 0x1;     // a melee weapon
                    FakeHost.Skills[(Player, 34)] = (0, 1);   // War Magic untrained
                    FakeHost.Skills[(Player, 43)] = (0, 1);   // Void Magic untrained
                });
                FakeHost.CombatModeValue = CombatMode.NonCombat;
                foreach (uint id in listed)
                    rig.S.ItemRules.Add(new() { Id = unchecked((int)id), Name = FakeHost.Names[id], Element = "Fire" });
                var skills = new CharacterSkills(FakeHost.Create(Player));
                skills.SetPlayerId(Player);
                rig.C.SetCharacterSkills(skills);
                rig.C.InHandOverride = () => unchecked((int)inHand);
                if (hook) Claim(Atk);
                planned = rig.C.PlanFor(new WorldObject(Target, "Drudge Skulker"), rig.S.MonsterRules[0]).WeaponId;
                FakeHost.CombatModeRequests.Clear();
                rig.C.Think();
                return FakeHost.CombatModeRequests.ToList();
            }

            // A fighter with a listed buff wand (Fire rule), the hook held: the sword, in either
            // list order; the wand never pulls it out of melee.
            foreach (var order in new[] { new[] { Wand, Sword }, new[] { Sword, Wand } })
            {
                string what = order[0] == Wand ? "wand listed first" : "sword listed first";
                var fighter = OneThink(hook: true, listed: order, inHand: Sword);
                Check.Eq(unchecked((uint)planned), Sword, $"sword+wand, hook held, {what}: the sword is the weapon");
                Check.True(fighter.Contains(CombatMode.Melee) && !fighter.Contains(CombatMode.Magic),
                    $"sword+wand, hook held, {what}: Melee, never Magic: [{string.Join(",", fighter)}]");
            }

            var off = OneThink(hook: false);
            Check.True(off.Contains(CombatMode.Melee) && !off.Contains(CombatMode.Magic),
                $"no hook: the wand is no combat weapon, unarmed Melee is asked for (as before): [{string.Join(",", off)}]");
            Check.True(FakeHost.Logs.Any(l => l.Contains("no weapon found") && l.Contains("canMagic=False")), "no hook: the diag line says canMagic=False");
            Check.True(FakeHost.Logs.Any(l => l.Contains("is not a combat weapon: canMagic=False")), "no hook: the wand's rejection is logged");

            var on = OneThink(hook: true);
            Check.Eq(unchecked((uint)planned), Wand, "hook held, only the wand listed: the weapon plan itself names the wand (not just the no-weapon fallback)");
            Check.True(on.Contains(CombatMode.Magic) && !on.Contains(CombatMode.Melee),
                $"hook held: the wand is the weapon and Magic is asked for, not Melee: [{string.Join(",", on)}]");
            Check.False(FakeHost.Logs.Any(l => l.Contains("no weapon found")), "hook held: no 'no weapon found'");
        }
        finally
        {
            ActionPacer.Enabled = pacer;
            ActionPacer.Forget("test");
        }
    });
}
