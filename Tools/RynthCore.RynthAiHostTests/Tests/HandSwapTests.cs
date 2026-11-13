using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The wand swap as fast as the server allows (2026-10-22, HandSwapSequence). Drakkon on DreamWeave,
// 21:25: Acid + Frost Hand Wraps -> Sceptre took 7.2 s to the first cast, 4.7 s of it our own waits
// (the swap gate's flat 3 s after each pack move). The simulated server below answers the way ACE
// did in that log: the first weapon out of a hand in a combat mode waits for a forced stance change
// (1.25 s), a later one is answered at once, a wield in 60 ms, and a caster wielded in a combat mode
// switches the stance to Magic by itself.
internal static class HandSwapTests
{
    private const uint Player = 0x50000D01;
    private const uint STypeLife = 33;
    private const uint Acid = 0x80000601, Frost = 0x80000602, Sceptre = 0x80000603, Shield = 0x80000604,
                       Bow = 0x80000605, Arrows = 0x80000606, Great = 0x80000607, OtherWand = 0x80000608;
    private static int I(uint id) => unchecked((int)id);
    private const int Melee = HandSwapSequence.MeleeSlot, Off = HandSwapSequence.ShieldSlot, Missile = HandSwapSequence.MissileSlot,
                      Ammo = HandSwapSequence.AmmoSlot, Held = HandSwapSequence.HeldSlot, Two = HandSwapSequence.TwoHandedSlot;

    public static void Register(Runner r)
    {
        r.Add("hand swap: dual wield -> wand: the off hand, then the main hand, then the wand, each once the last landed", DualWieldOrder);
        r.Add("hand swap: sword + shield, two-hander, bow (arrows stay on), empty hands, a wand already in hand", Loadouts);
        r.Add("hand swap: peace first from a combat mode, then the hands (answered at once in peace), then the wand", PeaceFirst);
        r.Add("hand swap: an unanswered step is sent again once, then the swap fails (bounded)", Bounded);
        r.Add("hand swap: an item that must stay on (Offhand None) fails the swap instead of stripping the main hand", KeepFails);
        r.Add("hand swap: the swap gate is held for the whole swap and freed at its end", GateHeld);
        r.Add("wand swap (Drakkon 10-22): two Hand Wraps -> Sceptre -> first heal in under 2 s (was 7.2 s)", DrakkonHealFast);
        r.Add("wand swap: a restam (no emergency) casts once the 1 s stance floor is up, about 2.4 s in", RestamFloor);
        r.Add("wand swap: in a combat mode RynthAi leaves the Magic switch to the server; from peace it asks itself", ServerSwitchesMagic);
        r.Add("wand swap: a swap nothing needs any more stops without counting as a failure", Abandoned);
        r.Add("wand swap: a wand the server never wields: retried once, then the kit / potion path gets its turn", WandNeverWields);
        r.Add("weapons back (Drakkon 10-22): Sceptre -> two Hand Wraps through peace, then the fight's stance, gate held throughout", RearmThroughPeace);
        r.Add("weapons back: a target arriving mid-swap: it goes on for the same weapons; a fight with the wand ends it", RearmAbandoned);
        r.Add("a monster with the wand in hand: peace, the Sceptre out, both Hand Wraps on, then Melee (no client use)", FightFromWand);
        r.Add("a monster with the wand in hand, burst: the Sceptre out and both Hand Wraps on in one tick", FightFromWandBurst);
        r.Add("a monster with the wand in hand: a fight swap that fails leaves that weapon to the client's own swap", FightSwapFallsBack);
        r.Add("wand to wand (hunt report 11-07): the next monster's caster goes in through peace, then Magic (no client use, no bare-hands stance)", WandToWandFromMagic);
        r.Add("wand to wand from peace (Peace Mode When Idle between targets): the old caster out, the new one in, then Magic; no peace request", WandToWandFromPeace);
        r.Add("wand to wand: the swap tracker records the swap (no swap straight back for the same fight)", WandToWandTracked);
        r.Add("WandSwapRules.UseHandsSwap: a swap away from a caster goes through peace, a caster wanted too", UseHandsSwapRules);
        r.Add("WandSwapRules.EquipCastHoldMs: the first cast waits for the stance after a wield, never past the old 3 s", EquipCastHoldRules);
        r.Add("equip gate: held while the stance settles, then the cast goes; one log line per wield", EquipGateHolds);
        r.Add("weapons back burst: peace, the Sceptre out and both Hand Wraps on in one tick, then Melee", RearmBurst);
        r.Add("weapons back burst: the quiet check reads BuffManager's mode change, not combat's late one (Drakkon 23:06-23:21)", RearmBurstQuietFromBuff);
        r.Add("Fast Weapon Swap: on by default, an old saved off (the opt-in name) loads on, an untick since stays off", OnByDefault);
        r.Add("hand swap: a burst waits for every step it sent, then finishes one step at a time if some didn't land", BurstSequence);
        r.Add("wand swap burst: peace, both hands and the wand in one tick; the wand in hand one round trip later", BurstFast);
        r.Add("wand swap burst: not right after a stance change (the server could defer peace): the step route", BurstWaitsQuiet);
    }

    // ── The pure sequence ─────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 10, 22, 21, 25, 0);
    private static DateTime At(double ms) => T0.AddMilliseconds(ms);

    private static void DualWieldOrder()
    {
        var s = new HandSwapSequence();
        var hands = new Dictionary<int, int> { [I(Acid)] = Melee, [I(Frost)] = Off };
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) });

        var st = s.Next(hands, HandSwapSequence.Melee, At(0));
        Check.Eq(st.Act, HandSwapSequence.Act.MoveToPack, "first: a pack move");
        Check.Eq(unchecked((uint)st.Id), Frost, "the off hand first (taking the main hand first costs the off hand a second stance change)");
        s.Sent(st, At(0));
        Check.Eq(s.Next(hands, HandSwapSequence.Melee, At(500)).Act, HandSwapSequence.Act.Wait, "the off hand's move unanswered: wait");
        Check.Eq(s.Next(hands, HandSwapSequence.Melee, At(1200)).Act, HandSwapSequence.Act.Wait, "still inside the forced stance change: wait, nothing sent twice");

        hands.Remove(I(Frost));                                   // the server moved it at 1.25 s
        st = s.Next(hands, HandSwapSequence.Melee, At(1250));
        Check.Eq(st.Act, HandSwapSequence.Act.MoveToPack, "the moment it landed: the main hand, same tick");
        Check.Eq(unchecked((uint)st.Id), Acid, "the main hand");
        s.Sent(st, At(1250));

        hands.Remove(I(Acid));
        st = s.Next(hands, HandSwapSequence.Melee, At(1310));
        Check.Eq(st.Act, HandSwapSequence.Act.Wield, "hands empty: the wand");
        Check.Eq(unchecked((uint)st.Id), Sceptre, "the Sceptre");
        Check.Eq(st.Slot, Held, "into the Held slot");
        s.Sent(st, At(1310));

        hands[I(Sceptre)] = Held;
        Check.Eq(s.Next(hands, HandSwapSequence.Melee, At(1370)).Act, HandSwapSequence.Act.Done, "the wand in hand: done");
        Check.False(s.Active, "no longer running");
        Check.True(s.Trail.Contains("off 0x80000602 1.25 s") && s.Trail.Contains("off 0x80000601 0.06 s") && s.Trail.Contains("on 0x80000603 0.06 s"),
            "the trail has each step's answer time: " + s.Trail);
    }

    private static void PeaceFirst()
    {
        var s = new HandSwapSequence();
        var hands = new Dictionary<int, int> { [I(Acid)] = Melee, [I(Frost)] = Off };
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) }, peaceFirst: true);
        var st = s.Next(hands, HandSwapSequence.Melee, At(0));
        Check.True(st.Act == HandSwapSequence.Act.Stance && st.Slot == HandSwapSequence.NonCombat, "in Melee: peace first, before anything leaves a hand");
        s.Sent(st, At(0));
        Check.Eq(s.Next(hands, HandSwapSequence.Melee, At(30)).Act, HandSwapSequence.Act.Wait, "until the mode reads peace");
        st = s.Next(hands, HandSwapSequence.NonCombat, At(45));
        Check.True(st.Act == HandSwapSequence.Act.MoveToPack && unchecked((uint)st.Id) == Frost, "peace read: the off hand, the same tick");
        s.Sent(st, At(45));
        hands.Remove(I(Frost));
        st = s.Next(hands, HandSwapSequence.NonCombat, At(105));
        Check.True(st.Act == HandSwapSequence.Act.MoveToPack && unchecked((uint)st.Id) == Acid, "then the main hand");
        s.Sent(st, At(105));
        hands.Remove(I(Acid));
        st = s.Next(hands, HandSwapSequence.NonCombat, At(165));
        Check.True(st.Act == HandSwapSequence.Act.Wield && unchecked((uint)st.Id) == Sceptre, "then the wand");
        s.Sent(st, At(165));
        hands[I(Sceptre)] = Held;
        Check.Eq(s.Next(hands, HandSwapSequence.NonCombat, At(225)).Act, HandSwapSequence.Act.Done, "done (Magic is the caller's next step)");
        Check.True(s.WentThroughPeace, "it went through peace");
        Check.True(s.Trail.StartsWith("peace 0.0"), "the trail starts with the peace step: " + s.Trail);

        // Already in peace, or nothing to take off: no stance step.
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) }, peaceFirst: true);
        Check.Eq(s.Next(new Dictionary<int, int> { [I(Acid)] = Melee }, HandSwapSequence.NonCombat, At(0)).Act, HandSwapSequence.Act.MoveToPack, "in peace already: straight to the move");
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) }, peaceFirst: true);
        Check.Eq(s.Next(new Dictionary<int, int>(), HandSwapSequence.Melee, At(0)).Act, HandSwapSequence.Act.Wield, "hands empty: no peace detour, just the wield");

        // Peace never read: after PeaceResolveMs the swap goes on without it.
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) }, peaceFirst: true);
        var h2 = new Dictionary<int, int> { [I(Acid)] = Melee };
        s.Sent(s.Next(h2, HandSwapSequence.Melee, At(0)), At(0));
        Check.Eq(s.Next(h2, HandSwapSequence.Melee, At(1499)).Act, HandSwapSequence.Act.Wait, "waits up to 1.5 s for peace");
        Check.Eq(s.Next(h2, HandSwapSequence.Melee, At(1500)).Act, HandSwapSequence.Act.MoveToPack, "then moves anyway");
        Check.True(s.Trail.Contains("peace not read"), "and says so: " + s.Trail);
    }

    private static void Loadouts()
    {
        // Sword + shield: the shield, the sword, the wand.
        Check.Eq(Order(new() { [I(Acid)] = Melee, [I(Shield)] = Off }), "move 80000604, move 80000601, wield 80000603", "sword + shield");
        // A two-hander: it, then the wand.
        Check.Eq(Order(new() { [I(Great)] = Two }), "move 80000607, wield 80000603", "two-hander");
        // Bow and arrows: the bow only; arrows stay in the ammo slot (the server allows them beside a caster).
        Check.Eq(Order(new() { [I(Bow)] = Missile, [I(Arrows)] = Ammo }), "move 80000605, wield 80000603", "bow, arrows stay");
        // Empty hands: just the wield.
        Check.Eq(Order(new()), "wield 80000603", "hands empty");
        // Another caster in hand: it comes off first.
        Check.Eq(Order(new() { [I(OtherWand)] = Held }), "move 80000608, wield 80000603", "another wand in hand");
        // The wand already in hand: done at once, nothing sent.
        var s = new HandSwapSequence();
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) });
        Check.Eq(s.Next(new Dictionary<int, int> { [I(Sceptre)] = Held }, HandSwapSequence.Melee, At(0)).Act, HandSwapSequence.Act.Done, "already in hand: done");
        // Wanted in hand but something else beside it (a shield): the shield still comes off.
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) });
        var st = s.Next(new Dictionary<int, int> { [I(Sceptre)] = Held, [I(Shield)] = Off }, HandSwapSequence.Melee, At(0));
        Check.True(st.Act == HandSwapSequence.Act.MoveToPack && unchecked((uint)st.Id) == Shield, "a wand beside a shield: the shield off");
    }

    /// <summary>Runs a swap to the Sceptre against an instant server and lists what was sent.</summary>
    private static string Order(Dictionary<int, int> hands)
    {
        var s = new HandSwapSequence();
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) });
        var sent = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var st = s.Next(hands, HandSwapSequence.Melee, At(i * 100));
            if (st.Act == HandSwapSequence.Act.Done || st.Act == HandSwapSequence.Act.Failed) break;
            if (st.Act == HandSwapSequence.Act.Wait) continue;
            s.Sent(st, At(i * 100));
            if (st.Act == HandSwapSequence.Act.MoveToPack) { hands.Remove(st.Id); sent.Add($"move {(uint)st.Id:X8}"); }
            else { hands[st.Id] = st.Slot; sent.Add($"wield {(uint)st.Id:X8}"); }
        }
        return string.Join(", ", sent);
    }

    private static void Bounded()
    {
        var s = new HandSwapSequence();
        var hands = new Dictionary<int, int> { [I(Acid)] = Melee };
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) });
        var st = s.Next(hands, HandSwapSequence.Melee, At(0));
        s.Sent(st, At(0));
        Check.Eq(s.Next(hands, HandSwapSequence.Melee, At(3999)).Act, HandSwapSequence.Act.Wait, "inside StepResolveMs: wait");
        st = s.Next(hands, HandSwapSequence.Melee, At(4000));
        Check.True(st.Act == HandSwapSequence.Act.MoveToPack && unchecked((uint)st.Id) == Acid, "unanswered for 4 s: sent again");
        s.Sent(st, At(4000));
        st = s.Next(hands, HandSwapSequence.Melee, At(8000));
        Check.Eq(st.Act, HandSwapSequence.Act.Failed, "the second send unanswered too: the swap fails");
        Check.Eq(unchecked((uint)s.FailedId), Acid, "and says which item went unanswered");
        Check.False(s.Active, "stopped");
        Check.True(s.Trail.Contains("no answer"), "the trail says so: " + s.Trail);

        // The whole swap has a ceiling too.
        var g = new HandSwapSequence { StepResolveMs = 100_000 };
        g.Start("wand", At(0), new[] { (I(Sceptre), Held) });
        g.Sent(g.Next(hands, HandSwapSequence.Melee, At(0)), At(0));
        Check.Eq(g.Next(hands, HandSwapSequence.Melee, At(12_001)).Act, HandSwapSequence.Act.Wait, "a step still inside its own wait...");
        hands.Clear();
        Check.Eq(g.Next(hands, HandSwapSequence.Melee, At(12_002)).Act, HandSwapSequence.Act.Failed, "...lands after GiveUpMs: the swap fails, not another step");
    }

    private static void KeepFails()
    {
        var s = new HandSwapSequence();
        var hands = new Dictionary<int, int> { [I(Acid)] = Melee, [I(Shield)] = Off };
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) }, keep: new[] { I(Shield) });
        var st = s.Next(hands, HandSwapSequence.Melee, At(0));
        Check.Eq(st.Act, HandSwapSequence.Act.Failed, "the shield may not come off: fail at once");
        Check.True(hands.ContainsKey(I(Acid)), "nothing was taken off for nothing");
    }

    private static void GateHeld()
    {
        long t = 0;
        var g = new WeaponSwapGate { NowMs = () => t, ServerIdle = () => true, IsWielded = _ => false };
        Check.True(g.TryBeginSwap("buff-wand"), "the swap claims the gate");
        g.Hold("buff-wand", 13_000);
        t = 4000;
        Check.False(g.TryBeginSwap("rearm-after-vitals"), "held: no other swap, even after the old 3 s");
        Check.Eq(g.HeldBy, "buff-wand", "who holds it");
        g.Release("someone-else", 0);
        Check.False(g.TryBeginSwap("combat"), "only the holder releases it");
        g.Release("buff-wand", I(Sceptre));
        Check.Eq(g.HeldBy, "", "released");
        t = 6999;
        Check.False(g.TryBeginSwap("combat"), "after its last wield, the next swap still waits the full 3 s");
        t = 7000;
        Check.True(g.TryBeginSwap("combat"), "then it goes");
        // A hold nobody releases runs out.
        g.Hold("buff-wand", 1000);
        t = 8001;
        Check.True(g.TryBeginSwap("combat"), "a forgotten hold ends at its backstop");
    }

    // ── Through BuffManager, against a simulated server ───────────────────────

    /// <summary>
    /// Plays ACE for the rig: pack moves and wields the plugin sent are answered with the timings
    /// of Drakkon's 21:25 log, and a caster wielded in a combat mode switches the stance to Magic.
    /// </summary>
    private sealed class Server
    {
        public double ForcedStanceMs = 1250, AnswerMs = 60;
        public bool SwitchesMagic = true;
        public bool IgnoresStance;   // stance requests change nothing, on the server or the client
        public int RefusedWields;
        public bool WieldsWand = true;
        private readonly List<(double At, Action Do)> _due = new();
        private int _moves, _wields, _modes, _puts;
        private int? _serverMode;   // the server's own mode (the client's read follows it by EchoMs)
        private bool _stanced;   // the forced stance change to bare hands has been played this swap
        private double _nextUse = -1000;  // ACE's NextUseTime: a stance change waits for the last one's animation
        public double PeaceLegMs = 545, MagicLegMs = 521, EchoMs = 40;

        public void Observe(double now)
        {
            _serverMode ??= FakeHost.CombatModeValue;
            while (FakeHost.CombatModeRequests.Count > _modes)
            {
                int mode = FakeHost.CombatModeRequests[_modes++];
                if (IgnoresStance) continue;
                double runAt = Math.Max(now + EchoMs, _nextUse + 50);
                if (now >= _nextUse + 50) _serverMode = mode;   // run at once on the server (NextUseTime passed)
                else _due.Add((runAt - EchoMs, () => _serverMode = mode));
                double leg = mode == CombatMode.NonCombat ? PeaceLegMs : MagicLegMs + (FakeHost.CombatModeValue == CombatMode.NonCombat ? 0 : PeaceLegMs);
                _nextUse = runAt + leg;
                _due.Add((runAt, () => FakeHost.CombatModeValue = mode));
            }
            var moved = new List<uint>();
            while (FakeHost.Moves.Count > _moves) moved.Add(FakeHost.Moves[_moves++].Id);
            while (FakeHost.Puts.Count > _puts) moved.Add(FakeHost.Puts[_puts++].Id);
            foreach (uint id in moved)
            {
                if (!FakeHost.Equipped.ContainsKey(id)) continue;
                bool combat = _serverMode != CombatMode.NonCombat;
                double after = combat && !_stanced ? ForcedStanceMs : AnswerMs;
                if (combat) _stanced = true;
                _due.Add((now + after, () => FakeHost.Equipped.Remove(id)));
            }
            while (FakeHost.Wields.Count > _wields)
            {
                var w = FakeHost.Wields[_wields++];
                if (!WieldsWand) continue;
                _due.Add((now + AnswerMs, () =>
                {
                    // ACE refuses a Held caster with anything still in a hand (CheckWeaponCollision).
                    if (w.Mask == (uint)Held && FakeHost.Equipped.Any(kv => kv.Key != w.Id && (kv.Value & (uint)(Melee | Off | Missile | Two)) != 0))
                    { RefusedWields++; return; }
                    FakeHost.Equipped[w.Id] = w.Mask;
                    if (SwitchesMagic && w.Mask == (uint)Held && FakeHost.CombatModeValue != CombatMode.NonCombat)
                        FakeHost.CombatModeValue = CombatMode.Magic;
                }));
            }
        }

        public void Answer(double now)
        {
            foreach (var d in _due.Where(d => d.At <= now).ToList()) { d.Do(); _due.Remove(d); }
        }
    }

    private sealed class Rig
    {
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public readonly WeaponSwapGate Gate;
        public readonly Server Sv = new();
        public DateTime NowAt = T0;
        public double FirstCastMs = -1;
        private bool _inCombat = true;

        public Rig(int healthPct = 50, int staminaPct = 100, int mode = CombatMode.Melee, bool dualWield = true, bool putCalls = false)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.WieldCalls = true;
            FakeHost.EquippedCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.StopCalls = true;
            FakeHost.PutCalls = putCalls;
            FakeHost.CombatModeValue = mode;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            Item(Acid, "Acid Hand Wraps", 0x1, Melee);
            Item(Frost, "Frost Hand Wraps", 0x1, Melee);
            Item(Sceptre, "Sceptre", 0x8000, Held);
            FakeHost.Appraised.Add(Sceptre);
            FakeHost.Equipped[Acid] = (uint)Melee;
            if (dualWield) FakeHost.Equipped[Frost] = (uint)Off;
            var host = FakeHost.Create(Player);
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 30, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 35, RestamTo = 0, TopOffStam = 35, UsePotions = false,
                EnableBuffing = false, SpellCastIntervalMs = 400, FastWeaponSwap = true,
            };
            S.ItemRules = new List<ItemRule>
            {
                new() { Id = unchecked(I(Acid)), Name = "Acid Hand Wraps", Element = "", ElementSource = "", Action = "Weapon" },
                new() { Id = unchecked(I(Frost)), Name = "Frost Hand Wraps", Element = "", ElementSource = "", Action = "Weapon" },
                new() { Id = unchecked(I(Sceptre)), Name = "Sceptre", Element = "", ElementSource = "", Action = "Weapon" },
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache
            {
                MaxHealth = 100, CurrentHealth = (uint)healthPct, MaxStamina = 100, CurrentStamina = (uint)staminaPct,
                MaxMana = 100, CurrentMana = 100,
            };
            B = new BuffManager(host, S, sm, V) { InCombatOverride = () => _inCombat };
            B.SetCharacterSkills(skills);
            B.SetWorldObjectCache(FakeHost.MakeCache(host, Player, new[] { Acid, Frost, Sceptre }));
            Gate = new WeaponSwapGate { NowMs = () => (long)(NowAt - T0).TotalMilliseconds + 100_000, ServerIdle = () => true, IsWielded = _ => false };
            B.SetWeaponSwapGate(Gate);
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();
            EquippedState.Clock = () => (long)(NowAt - T0).TotalMilliseconds + 100_000;
            ActionPacer.Reset();
            ActionPacer.Enabled = true;
            ActionPacer.NowMs = () => 50_000_000 + (long)(NowAt - T0).TotalMilliseconds;
            MoveAudit.Reset();
            MoveAudit.NowMs = ActionPacer.NowMs;
            FakeHost.UseDone(0);
        }

        public bool InCombat { set => _inCombat = value; }

        private static void Item(uint id, string name, uint itemType, int slots)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = itemType;
            FakeHost.Ints[(id, 9)] = slots;
        }

        /// <summary>Heartbeats every 25 ms (the plugin's tick) from <paramref name="fromMs"/> to <paramref name="toMs"/>, the server answering between them.</summary>
        public void Run(double fromMs, double toMs)
        {
            for (double ms = fromMs; ms <= toMs; ms += 25)
            {
                NowAt = At(ms);
                Sv.Answer(ms);
                int casts = FakeHost.Casts.Count;
                B.OnHeartbeat();
                Sv.Observe(ms);
                if (FirstCastMs < 0 && FakeHost.Casts.Count > casts) FirstCastMs = ms;
                string hold = (string)(typeof(BuffManager).GetField("_healHold", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(B) ?? "");
                string why = hold + " | " + B.LastBuffSkipReason;
                if (why != _lastWhy) { Trace.Add($"{ms:0} mode={FakeHost.CombatModeValue} {why}"); _lastWhy = why; }
            }
        }

        public bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
        public readonly List<string> Trace = new();
        private string _lastWhy = "";
    }

    private static void Done()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
        EquippedState.Clock = null;
        EquippedState.Invalidate();
    }

    private static void DrakkonHealFast()
    {
        try
        {
            var rig = new Rig(healthPct: 50);                      // under Heal At, a monster in range: an urgent heal
            rig.Run(0, 4000);
            Check.True(FakeHost.Moves.Count == 2 && FakeHost.Moves[0].Id == Frost && FakeHost.Moves[1].Id == Acid,
                "the off hand (Frost), then the main hand (Acid): " + string.Join(", ", FakeHost.Moves.Select(m => $"{m.Id:X8}")));
            Check.True(FakeHost.Wields.Count == 1 && FakeHost.Wields[0] == (Sceptre, (uint)Held), "one wield: the Sceptre into the Held slot");
            Check.False(FakeHost.Uses.Contains(Sceptre), "no use of the wand (the client's own swap takes the main hand first)");
            Check.True(rig.FirstCastMs > 0, "the heal went out" + Environment.NewLine + string.Join(Environment.NewLine, FakeHost.Logs.TakeLast(60)) + Environment.NewLine + "mode=" + FakeHost.CombatModeValue + " requests=" + string.Join(",", FakeHost.CombatModeRequests) + Environment.NewLine + string.Join(Environment.NewLine, rig.Trace));
            Check.True(rig.FirstCastMs <= 1500, $"first heal {rig.FirstCastMs:0} ms after the swap began (was 7.2 s)" + Environment.NewLine + string.Join(Environment.NewLine, rig.Trace) + Environment.NewLine +
                string.Join(Environment.NewLine, FakeHost.Logs.Where(l => l.Contains("[WieldGate]") || l.Contains("[BuffStance]") || l.Contains("[Pace]"))));
            Check.True(rig.Logged("[WieldGate] wand in hand"), "the swap's time and steps are logged");
            Console.WriteLine($"          (simulated: first cast {rig.FirstCastMs:0} ms after the swap began; " + FakeHost.Logs.FirstOrDefault(l => l.Contains("wand in hand")) + ")");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free again");
        }
        finally { Done(); }
    }

    private static void RestamFloor()
    {
        try
        {
            var rig = new Rig(healthPct: 100, staminaPct: 20);     // Re-stam At 35: Revitalize, not an emergency
            rig.Run(0, 5000);
            Check.True(rig.FirstCastMs > 0, "the restam went out");
            Console.WriteLine($"          (simulated: first cast {rig.FirstCastMs:0} ms after the swap began; " + FakeHost.Logs.FirstOrDefault(l => l.Contains("wand in hand")) + ")");
            // Peace, both hands and the wand go in about 0.3 s; Magic is asked for the moment the wand is
            // in hand (the server starts it when the peace animation ends), and the first non-emergency
            // cast waits the 1 s stance floor from that request (BuffManager.StanceMinAfterRequestMs).
            Check.True(rig.FirstCastMs >= 1100 && rig.FirstCastMs <= 1500, $"first cast {rig.FirstCastMs:0} ms in" + Environment.NewLine +
                string.Join(Environment.NewLine, FakeHost.Logs.Where(l => l.Contains("[WieldGate]") || l.Contains("[BuffStance]") || l.Contains("[Pace]"))));
        }
        finally { Done(); }
    }

    private static void ServerSwitchesMagic()
    {
        try
        {
            // The peace route: peace, the hands, the wand, then our own Magic request (the server
            // doesn't switch a caster wielded in peace).
            var rig = new Rig(healthPct: 50);
            rig.Run(0, 3000);
            Check.True(FakeHost.CombatModeRequests.Count >= 2 && FakeHost.CombatModeRequests[0] == CombatMode.NonCombat
                       && FakeHost.CombatModeRequests.Contains(CombatMode.Magic), "peace, then Magic: " + string.Join(",", FakeHost.CombatModeRequests));
            Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.NonCombat), 1, "one peace request");

            // Peace never comes (the server ignores it): the hands come off in Melee after 1.5 s, the
            // wand goes on in Melee, and the server's own switch to Magic is waited for, not doubled.
            var slow = new Rig(healthPct: 50);
            slow.Sv.IgnoresStance = true;
            slow.Run(0, 1400);
            Check.Eq(FakeHost.Moves.Count, 0, "nothing leaves a hand while peace is awaited");
            slow.Sv.IgnoresStance = false;                         // later stance changes are answered again
            slow.Run(1425, 4500);
            Check.Eq(FakeHost.Wields.Count, 1, "the wand went on in Melee");
            Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.Magic), 0, "no Magic request of ours: the server switched by itself");
            Check.True(slow.Logged("peace not read"), "the trail says peace never came");

            // Already in peace: no stance change to wait out; both hands at once, then Magic.
            var peace = new Rig(healthPct: 50, mode: CombatMode.NonCombat);
            peace.Run(0, 600);
            Check.True(FakeHost.Moves.Count == 2, "in peace both hands come off at once (no forced stance change)");
            Check.Eq(FakeHost.CombatModeRequests.Count(m => m == CombatMode.NonCombat), 0, "no peace request in peace");
            Check.True(FakeHost.CombatModeRequests.Contains(CombatMode.Magic), "and our Magic request follows the wield");
        }
        finally { Done(); }
    }

    private static void Abandoned()
    {
        try
        {
            var rig = new Rig(healthPct: 50);
            rig.Sv.IgnoresStance = true;                           // peace never comes...
            rig.Sv.ForcedStanceMs = 3000;                          // ...and the melee-mode move is slow
            rig.Run(0, 300);
            rig.V.CurrentHealth = 100;                             // healed some other way: nothing needs the wand
            rig.Run(325, 5000);
            Check.True(rig.Logged("nothing needs the wand now"), "the swap stops");
            Check.Eq(FakeHost.Wields.Count, 0, "no wand wield");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
            Check.False(rig.Logged("didn't wield"), "not counted against the wand");
        }
        finally { Done(); }
    }

    private static void WandNeverWields()
    {
        try
        {
            var rig = new Rig(healthPct: 50);
            rig.Sv.WieldsWand = false;
            rig.Run(0, 10_500);
            Check.Eq(FakeHost.Wields.Count, 2, "the wield is sent twice, then the swap stops");
            Check.True(rig.Logged("the switch to the wand stopped"), "logged with its steps");
            Check.Eq(FakeHost.Casts.Count, 0, "no cast without the wand");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
        }
        finally { Done(); }
    }

    // ── Weapons back after heals (CombatManager.RearmAfterVitalsStep) ────────

    private sealed class CombatRig
    {
        public readonly CombatManager C;
        public readonly LegacyUiSettings S = new() { IsMacroRunning = true, RearmAfterVitals = true, PeaceModeWhenIdle = false, FastWeaponSwap = true };
        public readonly WeaponSwapGate Gate;
        public readonly Server Sv = new() { ForcedStanceMs = 1127 };
        private readonly System.Reflection.MethodInfo _step = typeof(CombatManager).GetMethod("RearmAfterVitalsStep",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        public double SimMs;

        public CombatRig(bool putCalls = false, bool secondWand = false)
        {
            FakeHost.Reset();
            FakeHost.PutCalls = putCalls;
            FakeHost.WeaponCalls = true;
            FakeHost.WieldCalls = true;
            FakeHost.EquippedCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            foreach (var (id, name, type, slots) in new[] { (Acid, "Acid Hand Wraps", 0x1u, Melee), (Frost, "Frost Hand Wraps", 0x1u, Melee), (Sceptre, "Sceptre", 0x8000u, Held), (OtherWand, "Fire Wand", 0x8000u, Held) })
            {
                FakeHost.Names[id] = name; FakeHost.ItemTypes[id] = type; FakeHost.Ints[(id, 9)] = slots;
            }
            FakeHost.Equipped[Sceptre] = (uint)Held;
            var host = FakeHost.Create(Player);
            S.ItemRules = new List<ItemRule>
            {
                new() { Id = I(Acid), Name = "Acid Hand Wraps", Element = "", ElementSource = "", Action = "Weapon" },
                new() { Id = I(Frost), Name = "Frost Hand Wraps", Element = "", ElementSource = "", Action = "Weapon" },
                new() { Id = I(Sceptre), Name = "Sceptre", Element = "", ElementSource = "", Action = "Weapon" },
            };
            if (secondWand) S.ItemRules.Add(new() { Id = I(OtherWand), Name = "Fire Wand", Element = "Fire", ElementSource = "", Action = "Weapon" });
            var cache = FakeHost.MakeCache(host, Player, secondWand ? new[] { Acid, Frost, Sceptre, OtherWand } : new[] { Acid, Frost, Sceptre });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.OpenPackOverride = () => I(Player);
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(CombatManager).GetField("_fightMainId", F)!.SetValue(C, I(Acid));
            typeof(CombatManager).GetField("_fightOffId", F)!.SetValue(C, I(Frost));
            DateTime vital = RynthClock.Now.AddSeconds(-2);    // a heal went out 2 s ago
            C.LastVitalCastAt = () => vital;
            C.BuffingWantsTick = () => false;
            Gate = new WeaponSwapGate { MinIntervalMs = 0 };
            C.SetWeaponSwapGate(Gate);
            ActionPacer.Enabled = false;                       // the pacer's rules have their own tests; this is the order
            EquippedState.Clock = () => (long)SimMs + 100_000;
        }

        /// <summary>RearmAfterVitalsStep every 25 ms (sim time), the server answering between ticks; true when it reported done.</summary>
        public bool Run(double toMs)
        {
            for (; SimMs <= toMs; SimMs += 25)
            {
                Sv.Answer(SimMs);
                bool busy = (bool)_step.Invoke(C, null)!;
                Sv.Observe(SimMs);
                if (!busy && FakeHost.Logs.Any(l => l.Contains("in hand") && l.Contains("weapons back after heals: 0x"))) return true;
            }
            return false;
        }

        // ── A fight (combat's own equip) ──
        public readonly WorldObject Target = new(unchecked((int)0x80009999), "Test Dummy", AcObjectClass.Monster);
        private const System.Reflection.BindingFlags NP = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        private readonly System.Reflection.MethodInfo _equip = typeof(CombatManager).GetMethod("EquipWeaponAndSetStance", NP)!;
        private readonly System.Reflection.MethodInfo _tick = typeof(CombatManager).GetMethod("TickRearmSwap", NP)!;

        /// <summary>A monster to fight: dual wield trained, Frost the Default rule's off-hand pick (Drakkon's setup).</summary>
        public void Fighting()
        {
            typeof(CombatManager).GetField("_activeTargetId", NP)!.SetValue(C, unchecked((int)0x80009999));
        }

        /// <summary>The heartbeat's hands-swap tick, then combat's equip, every 25 ms (sim time); the sim time combat was ready to attack (-1 = not by <paramref name="toMs"/>).</summary>
        public double Fight(double toMs)
        {
            for (; SimMs <= toMs; SimMs += 25)
            {
                Sv.Answer(SimMs);
                _tick.Invoke(C, null);
                bool ready = (bool)_equip.Invoke(C, new object[] { Target, "Auto" })!;
                Sv.Observe(SimMs);
                if (ready) return SimMs;
            }
            return -1;
        }

        public bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
    }

    /// <summary>The rig for a fight that starts with the wand in hand (a monster came while healing).</summary>
    private static CombatRig FightRig(bool putCalls = false)
    {
        var rig = new CombatRig(putCalls);
        Fightable(rig);
        rig.C.LastVitalCastAt = () => DateTime.MinValue;          // no weapons back pending
        rig.Fighting();
        return rig;
    }

    /// <summary>Drakkon's fight setup: Dual Wield trained, Frost the Default rule's off-hand pick, no war magic.</summary>
    private static void Fightable(CombatRig rig)
    {
        FakeHost.Skills[(Player, 49)] = (220, 2);
        var skills = new CharacterSkills(FakeHost.Create(Player));
        skills.SetPlayerId(Player);
        rig.C.SetCharacterSkills(skills);
        rig.C.CanCastOverride = _ => false;
        rig.S.MonsterRules = new List<MonsterRule> { new() { Name = "Default", OffhandId = I(Frost) } };
    }

    private static void FightFromWand()
    {
        try
        {
            var rig = FightRig();
            rig.C.SwapBurstEnabled = () => false;
            double ready = rig.Fight(3000);
            string log = string.Join(Environment.NewLine, FakeHost.Logs.Where(l => l.Contains("[EquipDiag]")).TakeLast(30));
            Check.True(ready > 0, "ready to attack within 3 s: " + log);
            Check.False(FakeHost.Uses.Any(), "no use (the client's own swap pays a forced stance change in Magic): " + string.Join(",", FakeHost.Uses.Select(u => $"{u:X8}")));
            Check.Eq(FakeHost.CombatModeRequests.FirstOrDefault(), CombatMode.NonCombat, "peace first: " + string.Join(",", FakeHost.CombatModeRequests));
            Check.True(FakeHost.CombatModeRequests.Contains(CombatMode.Melee), "then Melee");
            Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Sceptre, "the Sceptre into the pack");
            Check.True(FakeHost.Wields.Count == 2 && FakeHost.Wields[1].Mask == (uint)Off
                       && FakeHost.Wields.Select(w => w.Id).OrderBy(i => i).SequenceEqual(new[] { Acid, Frost }),
                "a Hand Wraps in each hand: " + string.Join(", ", FakeHost.Wields.Select(w => $"{w.Id:X8}@{w.Mask:X}")));
            Check.True(rig.Logged("weapons for the fight in hand"), "the swap's time and steps are logged: " + log);
            Check.True(ready <= 900, $"ready {ready:0} ms in (the client's own swap: weapons at 1.4-1.7 s)");
            Console.WriteLine($"          (simulated: ready to attack {ready:0} ms in; " + FakeHost.Logs.FirstOrDefault(l => l.Contains("weapons for the fight in hand")) + ")");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    private static void FightFromWandBurst()
    {
        try
        {
            var rig = FightRig(putCalls: true);
            rig.C.SwapBurstEnabled = () => true;
            double ready = rig.Fight(3000);
            Check.True(ready > 0, "ready to attack: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("weapons for the fight"))));
            Check.True(FakeHost.Puts.Count == 1 && FakeHost.Puts[0].Id == Sceptre, "the Sceptre out with the raw move");
            Check.Eq(FakeHost.Moves.Count, 0, "no client-gated move");
            Check.Eq(FakeHost.Wields.Count, 2, "both Hand Wraps");
            Check.True(rig.Logged("weapons for the fight burst"), "logged");
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1,2", "peace, then Melee");
            Check.True(ready <= 700, $"ready {ready:0} ms in (Melee waits for the peace leg on the server)");
            Console.WriteLine($"          (simulated burst: ready to attack {ready:0} ms in; " + FakeHost.Logs.FirstOrDefault(l => l.Contains("weapons for the fight in hand")) + ")");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    private static void FightSwapFallsBack()
    {
        try
        {
            var rig = FightRig();
            rig.C.SwapBurstEnabled = () => false;
            rig.Sv.WieldsWand = false;                                 // the server answers no wield
            var seq = (HandSwapSequence)typeof(CombatManager).GetField("_rearmSwap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.C)!;
            seq.StepResolveMs = 0;                                     // (real time) an unanswered step is sent again at once
            rig.Fight(1000);
            Check.True(rig.Logged("weapons for the fight stopped"), "the swap stops: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("weapons for the fight"))));
            Check.True(rig.Logged("the client's own swap for 0x80000601") || rig.Logged("the client's own swap for 0x80000602"), "and leaves that weapon to the client's own swap for a while");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    // ── Caster to caster (2026-11-11, WandSwapRules) ──────────────────────────

    /// <summary>A wand fight: the Sceptre in hand, the Default rule's weapon the Fire Wand, war magic usable.</summary>
    private static CombatRig WandFightRig(bool fromPeace)
    {
        var rig = new CombatRig(secondWand: true);
        rig.C.CanCastOverride = _ => true;
        rig.S.MonsterRules = new List<MonsterRule> { new() { Name = "Default", WeaponId = I(OtherWand) } };
        rig.C.LastVitalCastAt = () => DateTime.MinValue;          // no weapons back pending
        rig.C.SwapBurstEnabled = () => false;
        if (fromPeace) FakeHost.CombatModeValue = CombatMode.NonCombat;
        rig.Fighting();
        return rig;
    }

    private static string Trail() => string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[EquipDiag]")).TakeLast(20));

    private static void WandToWandFromMagic()
    {
        try
        {
            var rig = WandFightRig(fromPeace: false);
            double ready = rig.Fight(3000);
            Check.True(ready > 0, "ready to cast within 3 s: " + Trail());
            Check.False(FakeHost.Uses.Any(), "no use (the client's own use of a caster in Magic plays a forced change to bare hands): "
                + string.Join(",", FakeHost.Uses.Select(u => $"{u:X8}")));
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1,8", "peace, then Magic; nothing else");
            Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Sceptre, "the Sceptre into the pack");
            Check.True(FakeHost.Wields.Count == 1 && FakeHost.Wields[0] == (OtherWand, (uint)Held),
                "the Fire Wand into the Held slot: " + string.Join(", ", FakeHost.Wields.Select(w => $"{w.Id:X8}@{w.Mask:X}")));
            Check.True(rig.Logged("weapons for the fight in hand"), "the swap's time and steps are logged: " + Trail());
            Check.True(rig.Logged("weapons for the fight: Magic mode"), "the Magic request is logged: " + Trail());
            Check.Eq(FakeHost.CombatModeValue, CombatMode.Magic, "in Magic mode");
            Check.True(ready <= 1000, $"ready {ready:0} ms in (one peace leg and one Magic leg)");
            Console.WriteLine($"          (simulated: caster to caster, ready to cast {ready:0} ms in)");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    private static void WandToWandFromPeace()
    {
        try
        {
            var rig = WandFightRig(fromPeace: true);
            double ready = rig.Fight(3000);
            Check.True(ready > 0, "ready to cast within 3 s: " + Trail());
            Check.False(FakeHost.Uses.Any(), "no use");
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "8", "Magic once the Fire Wand is in (no Magic beside the use, no peace)");
            Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Sceptre, "the Sceptre into the pack (answered at once in peace)");
            Check.True(FakeHost.Wields.Count == 1 && FakeHost.Wields[0] == (OtherWand, (uint)Held), "the Fire Wand into the Held slot");
            Check.True(ready <= 900, $"ready {ready:0} ms in");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    private static void WandToWandTracked()
    {
        try
        {
            var rig = WandFightRig(fromPeace: false);
            Check.True(rig.Fight(3000) > 0, "the swap finished: " + Trail());
            var step = rig.C.SwapTracker.Next(I(Sceptre), I(OtherWand), RynthClock.Now, forKey: rig.Target.Id);
            Check.Eq(step, WeaponSwapTracker.Step.Hold, "the Sceptre straight back for the same fight: held");
            Check.Eq(rig.C.SwapTracker.HoldReason, "just swapped away from it", "as for the client's own use");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    private static void UseHandsSwapRules()
    {
        const int MagicKind = CombatMode.Magic, MeleeKind = CombatMode.Melee;
        Check.True(WandSwapRules.UseHandsSwap(true, true, 1, 2, MagicKind), "a caster in hand, another weapon wanted (a caster or not): through peace");
        Check.False(WandSwapRules.UseHandsSwap(false, true, 1, 2, MagicKind), "Fast Weapon Swap off: the old route");
        Check.False(WandSwapRules.UseHandsSwap(true, false, 1, 2, MagicKind), "the swap tracker held it: no swap");
        Check.False(WandSwapRules.UseHandsSwap(true, true, 1, 1, MagicKind), "the wanted caster is in hand: no swap");
        Check.False(WandSwapRules.UseHandsSwap(true, true, 0, 2, 0), "nothing in hand: combat's own wield");
        Check.False(WandSwapRules.UseHandsSwap(true, true, 1, 2, MeleeKind), "a melee weapon in hand: combat's own swap, as before");
        Check.False(WandSwapRules.UseHandsSwap(true, true, 1, 0, MagicKind), "nothing chosen");
    }

    private static void EquipCastHoldRules()
    {
        static double H(double equip, double change, double request) => WandSwapRules.EquipCastHoldMs(equip, change, request);
        Check.Eq(H(-1, -1, -1), 0.0, "no wield seen: no wait");
        Check.Eq(H(3000, -1, -1), 0.0, "the old 3 s are up: no wait");
        Check.Eq(H(500, 4000, -1), 2500.0, "no stance change seen since the wield: the old wait (the client may not have caught up)");
        Check.Eq(H(500, -1, -1), 2500.0, "never a stance change seen: the old wait");
        Check.Eq(H(800, 100, -1), 200.0, "the server's own change to Magic 100 ms ago: 300 ms after it");
        Check.Eq(H(800, 400, -1), 200.0, "Magic read for 400 ms, the wield 800 ms ago: 1 s after the wield");
        Check.Eq(H(1100, 400, -1), 0.0, "1.1 s after the wield, Magic read for 400 ms: cast now");
        Check.Eq(H(800, 400, 600), 400.0, "our own Magic request 600 ms ago: 1 s after it");
        Check.Eq(H(1200, 400, 1100), 0.0, "request 1.1 s ago, Magic read for 400 ms: cast now");
        Check.Eq(H(800, 400, 900), 200.0, "a stance request from before the wield doesn't count: 1 s after the wield");
        Check.Eq(H(2900, 50, 60), 100.0, "never past the old 3 s");
    }

    private static void EquipGateHolds()
    {
        try
        {
            var rig = WandFightRig(fromPeace: false);
            const System.Reflection.BindingFlags NP = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var holds = typeof(CombatManager).GetMethod("EquipCastHoldsNow", NP)!;
            void Set(string field, DateTime v) => typeof(CombatManager).GetField(field, NP)!.SetValue(rig.C, v);
            bool Held() => (bool)typeof(CombatManager).GetField("_equipGateHeld", NP)!.GetValue(rig.C)!;
            bool Holds() => (bool)holds.Invoke(rig.C, null)!;
            int Lines() => FakeHost.Logs.Count(l => l.Contains("[CombatCast] equip-gate"));
            DateTime now = RynthClock.Now;
            Set("_lastEquipTime", now.AddMilliseconds(-700));
            Set("_cmModeChangedAt", now.AddMilliseconds(-100));          // the client read Magic 100 ms ago
            Set("lastStanceAttempt", now.AddMilliseconds(-600));         // our Magic request 600 ms ago
            Check.True(Holds(), "held: the stance is still settling");
            Check.True(Held(), "the tick is marked held (not an attack)");
            Holds();
            Check.Eq(Lines(), 1, "one line per wield");

            Set("_lastEquipTime", now.AddMilliseconds(-1500));
            Set("_cmModeChangedAt", now.AddMilliseconds(-400));
            Set("lastStanceAttempt", now.AddMilliseconds(-1200));
            Check.False(Holds(), "Magic read for 400 ms, 1.2 s after the request: the cast goes (the old gate: 1.5 s more)");

            Set("_cmModeChangedAt", now.AddSeconds(-20));                 // combat's own read is old...
            rig.C.LastModeChangeAt = () => now.AddMilliseconds(-400);      // ...BuffManager's every-heartbeat read is fresh
            Check.False(Holds(), "the freshest mode-change read counts");
            rig.C.LastModeChangeAt = null;
            Check.True(Holds(), "no change seen since the wield: the old 3 s");
            Check.Eq(Lines(), 2, "a new wield, a new line");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    private static void RearmThroughPeace()
    {
        try
        {
            var rig = new CombatRig();
            Check.True(rig.Run(3000), "the weapons are back within 3 s of sim time: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("weapons back"))));
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1,2", "peace, then Melee (the fight's stance) once the weapons are on");
            Check.True(FakeHost.Moves.Count == 1 && FakeHost.Moves[0].Id == Sceptre, "the Sceptre into the pack");
            Check.True(FakeHost.Wields.Count == 2 && FakeHost.Wields[0] == (Acid, (uint)Melee) && FakeHost.Wields[1] == (Frost, (uint)Off),
                "the main hand, then the off hand: " + string.Join(", ", FakeHost.Wields.Select(w => $"{w.Id:X8}@{w.Mask:X}")));
            Check.False(FakeHost.Uses.Any(), "no use (the client's own swap pays a forced stance change in Magic)");
            Check.True(rig.SimMs <= 1200, $"done {rig.SimMs:0} ms in (was 2.3 s)");
            Console.WriteLine($"          (simulated: weapons back {rig.SimMs:0} ms after the swap began; " + FakeHost.Logs.FirstOrDefault(l => l.Contains("weapons back in hand")) + ")");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
            rig.Sv.Answer(rig.SimMs + 2000);                    // the server's deferred stance change runs
            Check.Eq(FakeHost.CombatModeValue, CombatMode.Melee, "in Melee mode");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

    private static void RearmAbandoned()
    {
        try
        {
            var rig = new CombatRig();
            rig.Sv.EchoMs = 60_000;                            // peace never comes: the swap is waiting
            rig.Run(200);
            Check.Eq(rig.Gate.HeldBy, "rearm-after-vitals", "the swap holds the gate");
            // A target appears: the swap goes on (2026-10-23; ending it freed the gate only after 3 s), and
            // combat's equip, wanting the same weapons, waits for it.
            Fightable(rig);
            rig.Fighting();
            rig.C.OnHeartbeat();
            Check.Eq(rig.Gate.HeldBy, "rearm-after-vitals", "still held: the swap goes on with a target");
            Check.Eq(rig.Fight(225), -1.0, "combat waits for the swap");
            Check.True(rig.Logged("the same weapons for 'Test Dummy', going on"), "logged");
            Check.False(rig.Logged("weapons back stopped"), "not stopped");
            // The fight wants the wand instead: the swap ends and the gate is free for combat's wand.
            var cache = (WorldObjectCache)typeof(CombatManager).GetField("_worldFilter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.C)!;
            var sceptre = cache[I(Sceptre)]!;
            bool wait = (bool)typeof(CombatManager).GetMethod("FightJoinsHandsSwap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(rig.C, new object?[] { rig.Target, null, I(Sceptre), sceptre })!;
            Check.False(wait, "combat's own equip goes on");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
            Check.True(rig.Logged("is fought with the wand"), "logged");

            // While no target: the heartbeat itself moves the swap on (the combat tick's gates can hold the step call).
            var rig2 = new CombatRig();
            rig2.C.SwapBurstEnabled = () => false;
            rig2.Run(0);
            Check.Eq(FakeHost.CombatModeRequests.FirstOrDefault(), CombatMode.NonCombat, "tick 0: peace first");
            rig2.Sv.Observe(0);
            rig2.Sv.Answer(100);
            int movesBefore = FakeHost.Moves.Count;
            rig2.C.OnHeartbeat();                                  // no RearmAfterVitalsStep call: the heartbeat goes on with it
            Check.True(FakeHost.Moves.Count > movesBefore, "the Sceptre's move went from the heartbeat");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }


    // ── The setting ───────────────────────────────────────────────────────────

    private static void OnByDefault()
    {
        Check.True(new LegacyUiSettings().FastWeaponSwap, "a new settings object has Fast Weapon Swap on");
        var ctx = RynthAiJsonContext.Default.LegacyUiSettings;
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings(), ctx))!.AsObject();
        Check.True(node.ContainsKey("FastWeaponSwapOn"), "saved under its 10-23 name");
        Check.False(node.ContainsKey("FastWeaponSwap"), "the opt-in build's name is no longer written");
        // The opt-in build (10-22) saved "FastWeaponSwap": false into every profile it touched: on now.
        node.Remove("FastWeaponSwapOn");
        node["FastWeaponSwap"] = false;
        var old = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), ctx)!;
        var live = new LegacyUiSettings { FastWeaponSwap = false };
        CopySettings(old, live);
        Check.True(live.FastWeaponSwap, "an old saved false (the opt-in name) loads on");
        // Unticked since (saved under the new name): stays off, the swap as before.
        node.Remove("FastWeaponSwap");
        node["FastWeaponSwapOn"] = false;
        var off = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), ctx)!;
        var live2 = new LegacyUiSettings();
        CopySettings(off, live2);
        Check.False(live2.FastWeaponSwap, "unticked since 10-23: stays off");
    }

    private static void CopySettings(LegacyUiSettings src, LegacyUiSettings dst) =>
        typeof(RynthCore.Plugin.RynthAi.LegacyUi.LegacyDashboardRenderer).GetMethod("CopySettings",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { src, dst });

    // ── The burst ─────────────────────────────────────────────────────────────

    private static void BurstSequence()
    {
        var s = new HandSwapSequence();
        var hands = new Dictionary<int, int> { [I(Acid)] = Melee, [I(Frost)] = Off };
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) }, peaceFirst: true);
        Check.Eq(string.Join(",", s.Blockers(hands).Select(i => $"{(uint)i:X8}")), "80000602,80000601", "the burst's moves: the off hand first");
        s.SentBurst(s.Blockers(hands), At(0));
        Check.Eq(s.Next(hands, HandSwapSequence.Melee, At(30)).Act, HandSwapSequence.Act.Wait, "nothing landed yet: wait");
        hands.Remove(I(Frost)); hands.Remove(I(Acid));
        Check.Eq(s.Next(hands, HandSwapSequence.NonCombat, At(90)).Act, HandSwapSequence.Act.Wait, "both off, the wand not yet: wait");
        hands[I(Sceptre)] = Held;
        Check.Eq(s.Next(hands, HandSwapSequence.NonCombat, At(120)).Act, HandSwapSequence.Act.Done, "all landed: done");
        Check.True(s.WentThroughPeace, "peace was read during the burst");
        Check.True(s.Trail.StartsWith("burst, off 0x80000602 0.09 s"), "the trail: " + s.Trail);

        // The wield was refused (a hand still full when it arrived): after BurstResolveMs, one step at a time.
        s.Start("wand", At(0), new[] { (I(Sceptre), Held) }, peaceFirst: true);
        var h2 = new Dictionary<int, int> { [I(Acid)] = Melee };
        s.SentBurst(s.Blockers(h2), At(0));
        h2.Remove(I(Acid));
        Check.Eq(s.Next(h2, HandSwapSequence.NonCombat, At(2499)).Act, HandSwapSequence.Act.Wait, "inside the burst's wait");
        var st = s.Next(h2, HandSwapSequence.NonCombat, At(2500));
        Check.True(st.Act == HandSwapSequence.Act.Wield && unchecked((uint)st.Id) == Sceptre, "then the wield again, alone");
        Check.True(s.Trail.Contains("burst incomplete"), "and the trail says so: " + s.Trail);
    }

    private static void BurstFast()
    {
        try
        {
            var rig = new Rig(healthPct: 50, putCalls: true);
            rig.B.SwapBurst = true;
            rig.Run(0, 3000);
            Check.Eq(FakeHost.Moves.Count, 0, "no client-gated moves");
            Check.True(FakeHost.Puts.Count == 2 && FakeHost.Puts[0].Id == Frost && FakeHost.Puts[1].Id == Acid,
                "two raw moves, the off hand first: " + string.Join(", ", FakeHost.Puts.Select(m => $"{m.Id:X8}")));
            Check.Eq(FakeHost.Wields.Count, 1, "one wield");
            Check.Eq(rig.Sv.RefusedWields, 0, "the wield wasn't refused (the server took peace and both moves first)");
            Check.True(rig.Logged("burst 0 ms into the switch"), "logged");
            string inHand = FakeHost.Logs.FirstOrDefault(l => l.Contains("wand in hand")) ?? "";
            Console.WriteLine($"          (simulated burst: {inHand}; first heal {rig.FirstCastMs:0} ms)");
            var m = System.Text.RegularExpressions.Regex.Match(inHand, @"in hand (\d+) ms");
            Check.True(m.Success && int.Parse(m.Groups[1].Value) <= 150, "the wand in hand within about one round trip: " + inHand);
            Check.True(rig.FirstCastMs > 0 && rig.FirstCastMs <= 1000, $"the heal at {rig.FirstCastMs:0} ms");
        }
        finally { Done(); }
    }

    private static void BurstWaitsQuiet()
    {
        try
        {
            var rig = new Rig(healthPct: 100, putCalls: true);
            rig.B.SwapBurst = true;
            rig.Run(0, 200);                                       // the plugin has seen the mode (Melee) since 0
            FakeHost.CombatModeValue = CombatMode.Missile;         // a stance change just happened
            rig.Run(225, 400);
            rig.V.CurrentHealth = 50;                              // now a heal is needed, 0.2 s after the change
            rig.Run(425, 3000);
            Check.Eq(FakeHost.Puts.Count, 0, "no burst inside 1.3 s of a stance change");
            Check.True(FakeHost.Moves.Count == 2, "the step route instead");
        }
        finally { Done(); }
    }


    private static void RearmBurst()
    {
        try
        {
            var rig = new CombatRig(putCalls: true);
            rig.C.SwapBurstEnabled = () => true;
            Check.True(rig.Run(2000), "the weapons are back: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("weapons back"))));
            Check.True(FakeHost.Puts.Count == 1 && FakeHost.Puts[0].Id == Sceptre, "the Sceptre out with the raw move");
            Check.Eq(FakeHost.Moves.Count, 0, "no client-gated move");
            Check.True(FakeHost.Wields.Count == 2 && FakeHost.Wields[0] == (Acid, (uint)Melee) && FakeHost.Wields[1] == (Frost, (uint)Off),
                "both Hand Wraps in the same tick: " + string.Join(", ", FakeHost.Wields.Select(w => $"{w.Id:X8}@{w.Mask:X}")));
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1,2", "peace, then Melee");
            Check.True(FakeHost.Logs.Any(l => l.Contains("weapons back burst")), "logged");
            Check.True(rig.SimMs <= 200, $"done {rig.SimMs:0} ms in");
            Console.WriteLine($"          (simulated weapons-back burst: done {rig.SimMs:0} ms in; " + FakeHost.Logs.FirstOrDefault(l => l.Contains("weapons back in hand")) + ")");
            Check.Eq(rig.Gate.HeldBy, "", "the gate is free");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }


    private static void RearmBurstQuietFromBuff()
    {
        try
        {
            // Combat's tick didn't run while buffing had it, so its own read of the mode saw the switch to
            // Magic only now, at the weapons-back tick. BuffManager's read (every heartbeat) says 5 s ago.
            var rig = new CombatRig(putCalls: true);
            rig.C.SwapBurstEnabled = () => true;
            typeof(CombatManager).GetField("_cmModeChangedAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(rig.C, RynthClock.Now);
            DateTime changed = RynthClock.Now.AddSeconds(-5);
            rig.C.LastModeChangeAt = () => changed;
            rig.Run(2000);
            Check.True(FakeHost.Puts.Count == 1, "the burst went (the mode last changed 5 s ago)");

            // Without BuffManager's read, combat's own (late) read holds it: the step route.
            var old = new CombatRig(putCalls: true);
            old.C.SwapBurstEnabled = () => true;
            typeof(CombatManager).GetField("_cmModeChangedAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(old.C, RynthClock.Now);
            old.Run(2000);
            Check.Eq(FakeHost.Puts.Count, 0, "no BuffManager read, a change just now: no burst");
            Check.Eq(FakeHost.Moves.Count, 1, "the step route instead");
        }
        finally { ActionPacer.Enabled = false; EquippedState.Clock = null; EquippedState.Invalidate(); }
    }

}
