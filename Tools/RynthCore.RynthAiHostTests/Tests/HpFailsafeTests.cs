using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot; // AcObjectClass
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// HP failsafe: under Emergency Heal At with heals failing (here: every heal sent and never answered,
// as in a client log's hung casts), the character must not sit and die. Recall off: 3 rounds of 2.5 s
// with no rise, a forced jump (ActionPacer.ForceBreak), then healing again. HpFailsafeRecall on
// (default off; most tests here turn it on): 2 rounds, the jump, and the Lifestone Recall SPELL at
// once (2026-10-22: not "/lifestone", whose long kneel got characters killed).
internal static class HpFailsafeTests
{
    private const uint Player = 0x50000B31;
    private const uint STypeLife = 33;

    public static void Register(Runner r)
    {
        r.Add("hp failsafe: recall on, 5 s at Emergency Heal At with no heal landing -> one jump, the macro stop and Lifestone Recall cast once landed; portal space ends it", BreakThenRecall);
        r.Add("hp failsafe: a heal landing (+3%) restarts the count; health over the line ends it", HealLandingResets);
        r.Add("hp failsafe: HP Failsafe Recall off -> the jump at 7.5 s, no recall; no portal space -> 3 casts 3 s apart, then it gives up", RecallOffAndResends);
        r.Add("hp failsafe: a recall that never starts turns the macro back on and re-arms; the next recall after 30 s", FailedRecallResumes);
        r.Add("hp failsafe: Emergency Heal At 100 at full health is not an emergency", FullHealthAtHundred);
        r.Add("hp failsafe: nothing counts in portal space", NothingInPortalSpace);
        r.Add("hp failsafe: a death drops the pending recall (0 health, and the plugin's death hold): no Lifestone Recall, the macro stays off", DeathDropsRecall);
        r.Add("hp failsafe: a failed recall does not switch the macro back on past a VTank yield", VTankYieldRespected);
        r.Add("hp failsafe: no recall without the spell or its mana: the jump, healing on, the macro keeps running, said once", NoSpellNoManaNoRecall);
        r.Add("hp failsafe: HP Failsafe Recall is off by default; a profile with no saved value loads off, an old saved true (before 10-22) loads off, a tick since then stays", RecallOffByDefault);
        r.Add("hp failsafe: a character that cannot heal at all (no spell, kit or potion) never jumps or recalls", NoHealCapabilityNeverFires);
        r.Add("hp failsafe: a character that can heal (a known Heal Self) and is stuck still jumps, then casts Lifestone Recall when the option is on", CapableStuckStillRecalls);
        r.Add("hp failsafe: a heal that went out proves the character can heal (a listed potion, no spell)", PotionSentProvesCapability);
        r.Add("vitals (L8):a heal the server leaves unanswered is not re-sent at the 2.5 s give-up after a 'too busy' answer", VitalResendObeysLatch);
        r.Add("vitals: under Emergency Heal At with a weapon out and no usable kit, a health potion before Heal Self's wand swap", EmergencyPotionBeforeWandSwap);
        r.Add("low components: tapers at 10, a scarab at 2 (not Diamond), kinds never carried ignored, a kind run out still counts", LowComponentsRule);
    }

    // Recall When Low On Components (RynthAiPlugin.LowComponents.cs), the rule itself.
    private static void LowComponentsRule()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [RynthAiPlugin.TaperKey] = 250, ["Lead Scarab"] = 12, ["Iron Scarab"] = 3,
        };
        Check.Null(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "plenty of everything: nothing low");
        counts[RynthAiPlugin.TaperKey] = 10;
        Check.Eq(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "tapers 10", "10 tapers: at the line");
        counts[RynthAiPlugin.TaperKey] = 11;
        counts["Iron Scarab"] = 2;
        Check.Eq(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "Iron Scarab 2", "2 Iron Scarabs: low");
        counts.Remove("Iron Scarab");   // used the last ones: gone from the pack
        Check.Eq(RynthAiPlugin.LowComponents(counts, seen, 10, 2), "Iron Scarab 0", "run out entirely: still low (seen before)");
        Check.Null(RynthAiPlugin.LowComponents(counts, seen, 10, 0), "Low Scarabs At 0: scarabs aren't checked");

        var fighter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Check.Null(RynthAiPlugin.LowComponents(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), fighter, 10, 2),
            "a character that never carried tapers or scarabs is never low");

        var inv = new[]
        {
            new WorldObject(1, "Prismatic Taper"), new WorldObject(2, "Red Taper"),
            new WorldObject(3, "Diamond Scarab"), new WorldObject(4, "Mana Scarab"), new WorldObject(5, "Scarab Shell Armor"),
        };
        var c = RynthAiPlugin.CountComponents(inv);
        Check.Eq(c.GetValueOrDefault(RynthAiPlugin.TaperKey), 2, "every kind of taper counts together");
        Check.False(c.ContainsKey("Diamond Scarab"), "Diamond Scarabs never count");
        Check.Eq(c.GetValueOrDefault("Mana Scarab"), 1, "a Mana Scarab counts");
        Check.Eq(c.Count, 2, "only tapers and scarabs (not 'Scarab Shell Armor')");

        // 2026-10-30: the "Mechanical Scarab" jewelry counted as a scarab kind, and once it had left the
        // pack the macro stopped as "Mechanical Scarab 0". Only spell components count now.
        var mixed = new[]
        {
            new WorldObject(11, "Mechanical Scarab", AcObjectClass.Jewelry),
            new WorldObject(12, "Mechanical Scarab"),
            new WorldObject(13, "Ornate Taper", AcObjectClass.Jewelry),
            new WorldObject(14, "Lead Scarab", AcObjectClass.SpellComponent),
            new WorldObject(15, "Obsidian Scarab", AcObjectClass.SpellComponent),
            new WorldObject(16, "Gold Scarab"),
            new WorldObject(17, "Diamond Scarab", AcObjectClass.SpellComponent),
            new WorldObject(18, "Prismatic Taper", AcObjectClass.SpellComponent),
        };
        var m = RynthAiPlugin.CountComponents(mixed);
        Check.False(m.ContainsKey("Mechanical Scarab"), "jewelry named '... Scarab' is no component, class known or not");
        Check.Eq(m.GetValueOrDefault(RynthAiPlugin.TaperKey), 1, "a taper counts only as a spell component (not jewelry)");
        Check.Eq(m.GetValueOrDefault("Lead Scarab"), 1, "a scarab spell component counts");
        Check.Eq(m.GetValueOrDefault("Obsidian Scarab"), 1, "a spell component scarab counts whatever its kind (a server's own)");
        Check.Eq(m.GetValueOrDefault("Gold Scarab"), 1, "a real scarab name counts before its class is known");
        Check.False(m.ContainsKey("Diamond Scarab"), "Diamond Scarabs still never count");
        Check.Eq(m.Count, 4, "tapers, Lead, Obsidian and Gold only");
        var seenMixed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RynthAiPlugin.LowComponents(m, seenMixed, 0, 1);
        Check.False(seenMixed.Contains("Mechanical Scarab"), "the jewelry never becomes a kind that can run low");
    }

    // Drakkon, 2026-10-14: 15% health in melee among Depraved Shadows, the kit at 3% (under Kit Min
    // Success), and the chain went to Heal Self: a wand swap still unanswered when he died 3.2 s later.
    // The potion came after Heal Self, so it was never reached. Now the emergency step drinks first.
    private static void EmergencyPotionBeforeWandSwap()
    {
        const uint Potion = 0x80001234;
        try
        {
            var r = new Rig(healthPct: 15);
            FakeHost.CombatModeValue = CombatMode.Melee;
            FakeHost.Names[Potion] = "Healing Elixir";
            r.S.UsePotions = true;
            r.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Potion), Name = "Healing Elixir", Type = "HealthPotion" });
            var cache = new WorldObjectCache(r.Host);
            cache.SetPlayerId(Player);
            cache.OnCreateObject(Player);
            _ = cache[unchecked((int)Potion)];
            cache.Tick();
            r.B.SetWorldObjectCache(cache);

            r.Run(0, 0.3);
            Check.True(FakeHost.Uses.Contains(Potion), "the health potion is drunk: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("Vitals")).Take(10)));
            Check.Eq(FakeHost.Casts.Count, 0, "no Heal Self cast (no wand swap) while the potion goes");
        }
        finally { Done(); }
    }

    // No re-send into a busy server: a client log re-sent a heal every 2.5 s (the vitals give-up) into a server
    // answering 0x1D. Through the whole vitals step (BuffManager heartbeats, pacer on, the pacer's
    // breaker off to isolate the latch): a heal goes out, the server never answers it, and another
    // send is refused 0x1D. No heal is re-sent while that heal is unanswered.
    private static void VitalResendObeysLatch()
    {
        try
        {
            var r = new Rig(healthPct: 40);
            r.S.EmergencyHealAt = 0;          // the failsafe out of it
            r.S.SpellCastIntervalMs = 100;
            r.Run(0, 0);
            Check.Eq(FakeHost.Casts.Count, 1, "the heal goes out (and is never answered)");
            r.Run(0.1, 0.5);
            int heal = FakeHost.Casts[0].SpellId;
            // Another send goes out over the open heal (as when a cast hangs) and is refused 'too busy'.
            Check.True(r.Host.CastSpellFor(0x80000AAAu, 1237, "Combat", "Drain Health Other I"), "the other send");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            // Real time too: BuffManager's own retry limits run on the real clock.
            for (int ms = 600; ms <= 6000; ms += 200) { System.Threading.Thread.Sleep(200); r.At(ms / 1000.0); r.B.OnHeartbeat(); }
            Check.Eq(FakeHost.Casts.Count(c => c.SpellId == heal), 1, "no heal re-sent in 6 s (the 2.5 s give-up re-sent one before): " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("[Pace]") || l.Contains("[Buff")).Take(20)));
            Check.True(r.Logged("Buff waits:") && r.Logged("nothing goes until the server answers"), "the hold is logged: " + string.Join(" | ", FakeHost.Logs.Take(30)));
        }
        finally { Done(); }
    }

    private sealed class Rig
    {
        public DateTime T0 = new(2026, 10, 10, 19, 0, 0);
        public DateTime NowAt;
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public readonly RynthCore.PluginSdk.RynthCoreHost Host;
        public int MacroStops;

        public Rig(int healthPct = 20, bool canHeal = true, bool recall = true, bool knowsRecall = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.ServerGates = true;
            FakeHost.LastUseDoneCalls = true;
            FakeHost.TapJumpCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            FakeHost.Names[Player] = "Mira";
            FakeHost.PlayerPose = (0x6347028Au, 100f, 100f, 0f);
            FakeHost.Positions[Player] = FakeHost.PlayerPose;
            if (canHeal)
            {
                foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
                if (!knowsRecall) FakeHost.Spellbook.Remove((uint)BuffManager.RecallSpellId);
                FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            }
            else
            {
                // A warm spellbook that holds no heal (an empty one reads as "not loaded yet").
                FakeHost.Spellbook.Add((uint)SpellTable.Rows.First(row => !row.Name.Contains("Heal", StringComparison.OrdinalIgnoreCase)
                    && !row.Name.Contains("Health", StringComparison.OrdinalIgnoreCase)).Id);
            }
            var host = Host = FakeHost.Create(Player);
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 30, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false,
                EnableBuffing = false, SpellCastIntervalMs = 100,
                HpFailsafeRecall = recall,   // off by default now; most tests here are about the recall
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            V = new PlayerVitalsCache
            {
                MaxHealth = 100, CurrentHealth = (uint)healthPct, MaxStamina = 100, CurrentStamina = 100,
                MaxMana = 100, CurrentMana = 100,
            };
            B = new BuffManager(host, S, sm, V);
            B.SetCharacterSkills(skills);
            B.MacroStopForRecall = () => MacroStops++;
            NowAt = T0;
            B.Clock = () => NowAt;
            B.MarkLiveBuffsReadyForTests();

            ActionPacer.Reset();
            ActionPacer.Enabled = true;
            ActionPacer.StuckCastMs = 0;   // the pacer's own breaker off: only the failsafe jumps here
            ActionPacer.NowMs = () => 50_000_000 + (long)(NowAt - T0).TotalMilliseconds;
            MoveAudit.Reset();
            MoveAudit.NowMs = ActionPacer.NowMs;
            FakeHost.UseDone(0);
        }

        public void At(double seconds) => NowAt = T0.AddMilliseconds(Math.Round(seconds * 1000));
        public bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
        public static int Recalls => FakeHost.Casts.Count(c => c.SpellId == BuffManager.RecallSpellId);

        /// <summary>Heartbeats as <see cref="Run"/>; each recall cast is answered at once (done, no teleport: a fizzle or refusal).</summary>
        public void RunAnswered(double from, double to)
        {
            for (int ms = (int)Math.Round(from * 1000); ms <= (int)Math.Round(to * 1000); ms += 100)
            {
                At(ms / 1000.0);
                int before = Recalls;
                B.OnHeartbeat();
                if (Recalls > before) FakeHost.UseDone(0);
            }
        }

        /// <summary>Heartbeats every 100 ms from <paramref name="from"/> up to and including <paramref name="to"/>.</summary>
        public void Run(double from, double to)
        {
            for (int ms = (int)Math.Round(from * 1000); ms <= (int)Math.Round(to * 1000); ms += 100)
            {
                At(ms / 1000.0);
                B.OnHeartbeat();
            }
        }
    }

    private static void Done()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        ActionPacer.StuckCastMs = 8000;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
    }

    private static void CopySettings(LegacyUiSettings src, LegacyUiSettings dst) =>
        typeof(RynthCore.Plugin.RynthAi.LegacyUi.LegacyDashboardRenderer).GetMethod("CopySettings",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(null, new object[] { src, dst });

    private static void RecallOffByDefault()
    {
        Check.False(new LegacyUiSettings().HpFailsafeRecall, "a new settings object has the recall off");
        var ctx = RynthAiJsonContext.Default.LegacyUiSettings;
        // A profile with no saved value for it: off.
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new LegacyUiSettings(), ctx))!.AsObject();
        Check.True(node.Remove("HpFailsafeRecallOptIn"), "the key is a serialized setting, under its 10-22 name");
        Check.False(node.ContainsKey("HpFailsafeRecall"), "the old name is no longer written");
        var noKey = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), ctx)!;
        var live = new LegacyUiSettings { HpFailsafeRecall = true };
        CopySettings(noKey, live);
        Check.False(live.HpFailsafeRecall, "no saved value: the recall loads off");
        // A profile saved before 10-22 with the old name on (the default until 10-18): off now. The
        // recall stops the macro, heals included, mid-fight, and it was killing players.
        node["HpFailsafeRecall"] = true;
        var old = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), ctx)!;
        var live2 = new LegacyUiSettings { HpFailsafeRecall = true };
        CopySettings(old, live2);
        Check.False(live2.HpFailsafeRecall, "an old saved true (the old name) loads off");
        // Ticked again since (saved under the new name): kept.
        node.Remove("HpFailsafeRecall");
        node["HpFailsafeRecallOptIn"] = true;
        var saved = System.Text.Json.JsonSerializer.Deserialize(node.ToJsonString(), ctx)!;
        var live3 = new LegacyUiSettings();
        CopySettings(saved, live3);
        Check.True(live3.HpFailsafeRecall, "ticked since 10-22: stays on");
    }

    // A melee character with nothing to heal with (no spell, kit or potion), resting or out-healed at
    // low health, must not be jumped around and sent to the lifestone: there is nothing to retry.
    private static void NoHealCapabilityNeverFires()
    {
        try
        {
            var r = new Rig(healthPct: 20, canHeal: false);
            r.Run(0, 40);
            Check.Eq(FakeHost.Casts.Count, 0, "nothing to cast with");
            Check.Eq(FakeHost.TapJumps, 0, "no failsafe jump");
            Check.Eq(r.B.HpFailsafeBreaks, 0, "nothing counted");
            Check.Eq(Rig.Recalls, 0, "no Lifestone Recall");
            Check.Eq(r.B.HpFailsafeRecalls, 0, "no recall");
            Check.True(r.S.IsMacroRunning, "the macro keeps running");

            // Life Magic trained but no Heal Self known, and a potion listed with Use Potions off: still nothing.
            const uint Potion = 0x80001234;
            var p = new Rig(healthPct: 20, canHeal: false);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            FakeHost.Names[Potion] = "Healing Elixir";
            p.S.UsePotions = false;
            p.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Potion), Name = "Healing Elixir", Type = "HealthPotion" });
            var cache = new WorldObjectCache(p.Host);
            cache.SetPlayerId(Player);
            cache.OnCreateObject(Player);
            _ = cache[unchecked((int)Potion)];
            cache.Tick();
            p.B.SetWorldObjectCache(cache);
            p.Run(0, 40);
            Check.Eq(FakeHost.TapJumps, 0, "no spell known, potions off: no jump");
            Check.Eq(Rig.Recalls, 0, "and no recall");
            Check.True(p.S.IsMacroRunning, "the macro keeps running");
        }
        finally { Done(); }
    }

    // The gate does not switch the failsafe off for a character that can heal: stuck heals still break, then recall.
    private static void CapableStuckStillRecalls()
    {
        try
        {
            var r = new Rig(healthPct: 20, canHeal: true, recall: true);
            r.Run(0, 5.0);
            Check.Eq(FakeHost.TapJumps, 1, "capable + stuck: the jump");
            Check.False(r.S.IsMacroRunning, "capable + stuck + recall on: the macro stops");
            r.Run(5.1, 7.0);
            Check.Eq(Rig.Recalls, 1, "and Lifestone Recall is cast");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "never /lifestone");

            // The option off (the default): the jump only, the macro keeps running.
            var d = new Rig(healthPct: 20, canHeal: true, recall: false);
            d.Run(0, 30.0);
            Check.Eq(FakeHost.TapJumps, 1, "recall off: the jump still");
            Check.True(d.S.IsMacroRunning, "recall off: no stop");
            Check.Eq(Rig.Recalls, 0, "recall off: no Lifestone Recall");
        }
        finally { Done(); }
    }

    // No spell, but a listed health potion with Use Potions on: the potion goes out (a heal sent), so
    // the failsafe can arm when the potion does not raise health.
    private static void PotionSentProvesCapability()
    {
        const uint Potion = 0x80001234;
        try
        {
            var r = new Rig(healthPct: 20, canHeal: false);
            FakeHost.Names[Potion] = "Healing Elixir";
            r.S.UsePotions = true;
            r.S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Potion), Name = "Healing Elixir", Type = "HealthPotion" });
            var cache = new WorldObjectCache(r.Host);
            cache.SetPlayerId(Player);
            cache.OnCreateObject(Player);
            _ = cache[unchecked((int)Potion)];
            cache.Tick();
            r.B.SetWorldObjectCache(cache);
            r.Run(0, 15.0);
            Check.True(FakeHost.Uses.Contains(Potion), "the potion is drunk");
            Check.Eq(FakeHost.TapJumps, 1, "it did not raise health: the failsafe jump");
            // This spellbook has no Lifestone Recall: no recall, healing goes on.
            Check.True(r.S.IsMacroRunning, "no recall spell: the macro keeps running");
            Check.True(r.Logged("no recall: this character doesn't know Lifestone Recall"), "said why");
        }
        finally { Done(); }
    }

    private static void BreakThenRecall()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.Run(0, 4.9);
            Check.True(FakeHost.Casts.Count > 0, "a heal went out (and is never answered)");
            Check.Eq(FakeHost.TapJumps, 0, "4.9 s: no jump yet");
            r.Run(5.0, 5.0);
            Check.Eq(FakeHost.TapJumps, 1, "5 s (2 rounds, no rise, recall on): one jump");
            Check.Eq(r.B.HpFailsafeBreaks, 1, "counted");
            Check.True(r.Logged("HP failsafe: health 20% at or under Emergency Heal At 30%, no heal landed in 5 s: breaking the cast with a jump, then Lifestone Recall at once"),
                "the break is logged: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("HP failsafe"))));
            Check.False(r.S.IsMacroRunning, "with the jump: the macro is stopped, no second wait");
            Check.Eq(r.MacroStops, 1, "with the rest of a /ra stop");
            Check.Eq(r.B.HpFailsafeRecalls, 1, "counted");
            Check.True(FakeHost.Chat.Any(c => c.Contains("casting Lifestone Recall")), "and said in chat");
            Check.Eq(Rig.Recalls, 0, "not in the same heartbeat: the stop edge runs first");
            r.Run(5.1, 6.6);
            Check.Eq(Rig.Recalls, 1, "once landed from the jump (400 ms or more): one Lifestone Recall: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("HP failsafe")).TakeLast(6)));
            Check.Eq(FakeHost.Casts.Last(c => c.SpellId == BuffManager.RecallSpellId).Target, Player, "on ourselves");
            Check.Eq(FakeHost.ChatCommands.Count, 0, "never /lifestone");
            FakeHost.Portaling = true;   // the recall went
            r.Run(6.7, 20);
            Check.Eq(Rig.Recalls, 1, "portal space: no recast");
            Check.True(r.Logged("HP failsafe: the recall went (portal space"), "logged");
            FakeHost.Portaling = false;
        }
        finally { Done(); }
    }

    private static void HealLandingResets()
    {
        try
        {
            // The round counting, recall off (the jump at 3 rounds).
            var r = new Rig(healthPct: 20, recall: false);
            r.Run(0, 5.0);
            r.V.CurrentHealth = 22;     // +2: regeneration, not a heal
            r.Run(5.1, 7.5);
            Check.Eq(FakeHost.TapJumps, 1, "+2% is not a heal landing: the jump at 7.5 s");

            var h = new Rig(healthPct: 20, recall: false);
            h.Run(0, 5.0);
            h.V.CurrentHealth = 23;     // +3: a heal landed
            h.Run(5.1, 12.4);
            Check.Eq(FakeHost.TapJumps, 0, "a heal landed at 5 s: no jump at 7.5 s, none by 12.4 s");
            h.Run(12.5, 12.6);
            Check.Eq(FakeHost.TapJumps, 1, "7.5 s after the heal with no other: the jump");

            var o = new Rig(healthPct: 20, recall: false);
            o.Run(0, 5.0);
            o.V.CurrentHealth = 31;     // over Emergency Heal At 30
            o.Run(5.1, 5.1);
            o.V.CurrentHealth = 20;
            o.Run(5.2, 12.6);
            Check.Eq(FakeHost.TapJumps, 0, "over the line at 5.1 s: the count started again at 5.2 s");
            Check.True(o.Logged("HP failsafe: health back to 31%"), "the end is logged");
        }
        finally { Done(); }
    }

    private static void RecallOffAndResends()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.S.HpFailsafeRecall = false;
            r.Run(0, 20);
            Check.Eq(FakeHost.TapJumps, 1, "recall off: the jump still");
            Check.True(r.S.IsMacroRunning, "recall off: the macro keeps running");
            Check.Eq(Rig.Recalls, 0, "no recall");
            Check.True(r.Logged("HP Failsafe Recall is off"), "said once in the log");

            var n = new Rig(healthPct: 20);
            n.Run(0, 5.0);
            Check.False(n.S.IsMacroRunning, "recall: macro stopped");
            n.RunAnswered(5.1, 8.0);
            Check.Eq(Rig.Recalls, 1, "the first cast; answered, no portal space");
            n.RunAnswered(8.1, 25);
            Check.Eq(Rig.Recalls, 3, "cast 3 times, 3 s apart, then stopped: " + string.Join(" | ", FakeHost.Logs.Where(l => l.Contains("HP failsafe")).TakeLast(8)));
            Check.True(n.Logged("no portal space after 3 cast(s) of Lifestone Recall"), "and logged");
        }
        finally { Done(); }
    }

    // A recall that never starts must not leave the macro stopped at low health.
    private static void FailedRecallResumes()
    {
        try
        {
            var n = new Rig(healthPct: 20);
            n.Run(0, 5.0);
            Check.False(n.S.IsMacroRunning, "recall: macro stopped");
            n.RunAnswered(5.1, 17.0);
            Check.Eq(Rig.Recalls, 3, "3 casts, no portal space");
            Check.True(n.S.IsMacroRunning, "the macro is back on (the heals and kits run again)");
            Check.Eq(n.B.HpFailsafeRecallsFailed, 1, "a failed recall counted");
            Check.True(FakeHost.Chat.Any(c => c.Contains("macro back on to keep healing")), "said in chat");
            n.RunAnswered(17.1, 40.0);
            Check.Eq(Rig.Recalls, 3, "inside the 30 s cooldown: no recall");
            Check.True(n.S.IsMacroRunning, "and the macro keeps running");
            int breaks = FakeHost.Logs.Count(l => l.Contains("HP failsafe: health 20% at or under Emergency Heal At 30%, no heal landed in"));
            Check.True(breaks >= 2, $"the failsafe re-armed: it reached the break stage again ({breaks})");
            Check.True(n.Logged("no recall: the last recall never started"), "the cooldown is said");
            n.RunAnswered(40.1, 75.0);
            Check.Eq(n.B.HpFailsafeRecalls, 2, "after the cooldown: the next recall");
            Check.True(Rig.Recalls > 3, "cast");
        }
        finally { Done(); }
    }

    // Emergency Heal At 100 at full health is not an emergency.
    private static void FullHealthAtHundred()
    {
        try
        {
            var r = new Rig(healthPct: 100);
            r.S.EmergencyHealAt = 100;
            r.Run(0, 20);
            Check.Eq(FakeHost.TapJumps, 0, "100% at Emergency Heal At 100: no break");
            Check.True(r.S.IsMacroRunning, "no recall");
        }
        finally { Done(); }
    }

    // A pending recall is dropped on death: after the respawn it must not cast the recall or switch
    // the macro on, against Stop Macro On Death.
    private static void DeathDropsRecall()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.Run(0, 5.0);
            Check.False(r.S.IsMacroRunning, "recall pending: macro stopped");
            r.V.CurrentHealth = 0;                    // dead before the first cast
            r.Run(5.1, 30);
            Check.Eq(Rig.Recalls, 0, "0 health: no Lifestone Recall");
            Check.False(r.S.IsMacroRunning, "the macro stays off");
            Check.True(r.Logged("the pending recall is dropped (dead)"), "logged");

            // The plugin's death hold drops it too (the vitals cache still reads the old health).
            var p = new Rig(healthPct: 20);
            p.Run(0, 5.0);
            var plugin = new RynthAiPlugin();
            PacerEdgeTests.SetHost(plugin, p.Host);
            typeof(RynthAiPlugin).GetField("_buffManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(plugin, p.B);
            FakeHost.Vitals = (0, 100, 0, 100, 0, 100);
            typeof(RynthAiPlugin).GetMethod("HoldForDeath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(plugin, null);
            p.Run(5.1, 30);
            Check.Eq(Rig.Recalls, 0, "death hold: no Lifestone Recall after the respawn");
            Check.False(p.S.IsMacroRunning, "death hold: the macro stays off");
            Check.True(p.Logged("the pending recall is dropped (death)"), "logged");
        }
        finally { Done(); }
    }

    // The failsafe's restart is a macro start like any other.
    private static void VTankYieldRespected()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            r.B.MayRestartMacro = () => false;        // VTank runs and yield is on
            r.RunAnswered(0, 21.0);
            Check.Eq(Rig.Recalls, 3, "3 casts, no portal space");
            Check.False(r.S.IsMacroRunning, "VTank yield: the macro stays off");
            Check.True(FakeHost.Chat.Any(c => c.Contains("the macro stays off (VTank yield)")), "said in chat");
        }
        finally { Done(); }
    }

    // No recall the character can't make: without the spell, or with less mana than it costs, the jump
    // runs, healing goes on and the macro keeps running; the reason is logged once per episode.
    private static void NoSpellNoManaNoRecall()
    {
        try
        {
            var r = new Rig(healthPct: 20, knowsRecall: false);
            r.Run(0, 20);
            Check.True(FakeHost.TapJumps >= 1, "the jump still");
            Check.Eq(Rig.Recalls, 0, "no Lifestone Recall");
            Check.True(r.S.IsMacroRunning, "the macro keeps running");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("no recall: this character doesn't know Lifestone Recall")), 1, "said once");

            var m = new Rig(healthPct: 20);
            BuffManager.ManaCostOverride = id => id == BuffManager.RecallSpellId ? 50 : 0;   // FakeHost.Reset sets 0 for all
            m.V.CurrentMana = 10;
            m.Run(0, 20);
            Check.True(FakeHost.TapJumps >= 1, "no mana: the jump still");
            Check.Eq(Rig.Recalls, 0, "no mana: no Lifestone Recall");
            Check.True(m.S.IsMacroRunning, "no mana: the macro keeps running");
            Check.True(m.Logged("is under Lifestone Recall's cost"), "said why");
        }
        finally { Done(); }
    }

    // Nothing counts in portal space.
    private static void NothingInPortalSpace()
    {
        try
        {
            var r = new Rig(healthPct: 20);
            FakeHost.Portaling = true;
            r.Run(0, 20);
            Check.Eq(FakeHost.TapJumps, 0, "portal space: no failsafe jump");
            Check.True(r.S.IsMacroRunning, "no recall");
            Check.Eq(r.B.HpFailsafeBreaks, 0, "nothing counted");
        }
        finally { FakeHost.Portaling = false; Done(); }
    }
}
