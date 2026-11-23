using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Make kits when low, the macro side (RynthAiPlugin.Kits.cs) on the real plugin and the fake engine:
// the facts it reads (the kits carried, the pack, Healing, the combat scan and the world's monsters,
// a hit, vitals, a portal), the hold while a run gets ready or goes (TickAutoKits), the stop on a
// monster (CheckAutoKitsDanger), the 60 s cooldown and the 10 min backoff, and the settings.
internal static class KitAutoPluginTests
{
    public static void Register(Runner r)
    {
        r.Add("kits when low (plugin): low and quiet - holds the bot, makes up to M, then lets it hunt; enough kits: nothing", StartsAndFinishes);
        r.Add("kits when low (plugin): each blocker - off, macro off, a monster in the world, a heal line, a hit, untrained, out of herbs, a portal", Blockers);
        r.Add("kits when low (plugin): a monster within the radius stops the run at once, then 60 s before the next try", MonsterStopsAndCoolsDown);
        r.Add("kits when low (plugin): out of peace - asks for peace mode, holds, and starts once in peace and still", GoesToPeace);
        r.Add("kits when low (plugin): a run that made nothing waits 10 min, said in its summary", NothingMadeBacksOff);
        r.Add("kits when low (plugin): settings - off, 2 and 5 by default, a missing key loads off, saved, copied with a profile", Settings);
    }

    private const uint Player = TickIsolationTests.Player;
    private const uint Lb = 0xA9B40000;
    private const uint Bandages = 0x80510001, Hyssop = 0x80510002, Mandrake = 0x80510003, OldKit = 0x80510004;
    private const uint Monster = 0x80510010;
    private const uint StackKey = 12;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private sealed class Rig
    {
        public readonly TickIsolationTests.Bot Bot;
        public readonly MiniRemoteController Remote;
        public KitRunner Kits => Remote.Crafting.Kits;
        public KitAuto Auto => Remote.Crafting.AutoKits;
        public LegacyUiSettings S => Bot.S;
        public int FailAll;
        private readonly List<(uint Src, uint Tgt)> _pending = new();
        private uint _nextId = 0x80610001;

        public Rig(int sets = 3, int kits = 0, int healing = 350, int training = 2, bool monster = false, double monsterYd = 25)
        {
            Bot = new TickIsolationTests.Bot(combat: true, heal: false, arrange: () =>
            {
                FakeHost.UseOnCalls = true;
                FakeHost.CombatModeCalls = true;   // peace mode requests recorded
                FakeHost.CombatModeValue = CombatMode.NonCombat;
                FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
                FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
                FakeHost.Skills[(Player, KitPlanner.SkillHealing)] = (healing, training);
                Carry(Bandages, KitPlanner.SoftBandages, sets);
                Carry(Hyssop, KitPlanner.TreatedHyssop, sets);
                Carry(Mandrake, KitPlanner.TreatedMandrake, sets);
                if (kits > 0) Carry(OldKit, KitPlanner.PlentifulHealingKit, kits);
                if (monster)
                {
                    FakeHost.Names[Monster] = "Drudge Skulker";
                    FakeHost.ItemTypes[Monster] = 0x10;
                    FakeHost.Positions[Monster] = (Lb | 0x1C, 96f, 96f + (float)monsterYd, 0f);
                }
                FakeHost.OnUseObjectOn = (src, tgt) => _pending.Add((src, tgt));
            });
            foreach (uint id in new[] { Bandages, Hyssop, Mandrake, OldKit, Monster })
                if (FakeHost.Names.ContainsKey(id)) _ = Bot.Cache[unchecked((int)id)];
            S.MonsterRange = 20;               // the safe radius is then 35 yd
            S.MakeKitsWhenLow = true;
            string dir = Path.Combine(Program.TempRoot, "kitauto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var cache = Bot.Cache;
            var s = S;
            Remote = new MiniRemoteController(Bot.Host, dir, () => cache, () => s, () => null, () => { }, new ServerHubClient(Bot.Host));
            Remote.Crafting.AutoKits.NowMs = () => Bot.Now;
            Bot.Set("_miniRemote", Remote);
        }

        private static void Carry(uint id, KitPart part, int count)
        {
            if (count <= 0) return;
            FakeHost.Names[id] = part.Name;
            FakeHost.Wcids[id] = part.Wcid;
            FakeHost.Ints[(id, StackKey)] = count;
            FakeHost.Containers[id] = Player;
        }

        public int Count(KitPart part) => KitPlanner.Count(part, Kits.Pack());

        /// <summary>One OnTick's worth: the danger check, the hold, the runner's tick, the server's answers. True = held.</summary>
        public bool Tick(long ms = 100)
        {
            Bot.Now += ms;
            Bot.Call("CheckAutoKitsDanger", S);
            bool hold = (bool)Bot.Call("TickAutoKits", S)!;
            Kits.Tick(Bot.Now);
            var sent = _pending.ToList();
            _pending.Clear();
            foreach (var (src, tgt) in sent) Serve(src, tgt);
            return hold;
        }

        /// <summary>Ticks until <paramref name="until"/> or the limit; how many ticks held.</summary>
        public int TickUntil(Func<bool> until, int max = 400, long ms = 100)
        {
            int held = 0;
            for (int i = 0; i < max && !until(); i++) if (Tick(ms)) held++;
            return held;
        }

        private void Serve(uint src, uint tgt)
        {
            var step = KitPlanner.Retail[0].Steps.FirstOrDefault(st =>
                FakeHost.Wcids.GetValueOrDefault(src) == st.Source.Wcid && FakeHost.Wcids.GetValueOrDefault(tgt) == st.Target.Wcid);
            if (step == null) return;
            bool ok = FailAll <= 0;
            Take(tgt);
            Take(src);
            if (ok) Give(step.Product);
            Kits.OnChat(ok ? step.SuccessText : step.FailText);
        }

        private void Take(uint id)
        {
            int n = FakeHost.Ints.GetValueOrDefault((id, StackKey), 1) - 1;
            if (n > 0) { FakeHost.Ints[(id, StackKey)] = n; return; }
            FakeHost.Ints.Remove((id, StackKey));
            Bot.Cache.OnDeleteObject(id);
        }

        private void Give(KitPart part)
        {
            uint id = _nextId++;
            FakeHost.Names[id] = part.Name;
            FakeHost.Wcids[id] = part.Wcid;
            FakeHost.Ints[(id, StackKey)] = 1;
            FakeHost.Containers[id] = Player;
            _ = Bot.Cache[unchecked((int)id)];
        }

        public int Uses => FakeHost.UsesOn.Count;
        public List<string> Lines => FakeHost.Chat.Where(c => c.Contains("Make kits when low")).ToList();
        public string Status => Remote.Crafting.AutoStatusText(S);
        public CombatManager Combat => (CombatManager)Bot.Get("_combatManager")!;
    }

    private static void StartsAndFinishes()
    {
        try
        {
            var rig = new Rig(sets: 3);
            rig.S.MakeKitsUpTo = 2;
            Check.Eq(rig.Count(KitPlanner.PlentifulHealingKit), 0, "no kits carried");
            int held = rig.TickUntil(() => rig.Kits.Running, max: 50);
            Check.True(rig.Kits.Running && rig.Kits.Auto, "a when-low run started by itself");
            Check.True(held >= 9, $"the bot was held while getting ready ({held} ticks)");
            Check.Eq(rig.Kits.Limit, 2, "up to 2 (Make up to)");
            Check.True(rig.Tick(), "held while it runs");
            Check.True(rig.Status.StartsWith("running "), "status: " + rig.Status);
            rig.TickUntil(() => !rig.Kits.Running);
            Check.Eq(rig.Kits.MadeCount, 2, "made 2");
            Check.Eq(rig.Count(KitPlanner.PlentifulHealingKit), 2, "two kits carried now");
            Check.False(rig.Tick(), "over: the bot hunts as normal");
            string line = rig.Lines.LastOrDefault() ?? "";
            Check.True(line.Contains("made 2 Plentiful Healing Kits") && line.Contains("Stopped: made the 2 asked for."), "one summary: " + line);
            int uses = rig.Uses;
            for (int i = 0; i < 20; i++) rig.Tick(5_000);
            Check.Eq(rig.Uses, uses, "2 carried (not under 2): no more runs");
            Check.True(rig.Status.Contains("2 kits carried"), "the status says why: " + rig.Status);
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void Blockers()
    {
        void NoStart(string name, Action<Rig> set, string status, Func<Rig, bool>? cleanUp = null)
        {
            try
            {
                var rig = new Rig();
                set(rig);
                for (int i = 0; i < 30; i++) rig.Tick();
                Check.False(rig.Kits.Running, name + ": no run");
                Check.Eq(rig.Uses, 0, name + ": nothing combined");
                Check.False(rig.Tick(), name + ": the bot isn't held");
                Check.True(rig.Status.Contains(status), $"{name}: the status says '{status}': {rig.Status}");
            }
            finally { TickIsolationTests.Done(); }
        }
        NoStart("setting off", r => r.S.MakeKitsWhenLow = false, "off");
        NoStart("macro off", r => r.S.IsMacroRunning = false, "macro is off");
        NoStart("a heal line", _ => FakeHost.Vitals = (50, 100, 100, 100, 100, 100), "health 50%");
        NoStart("in portal space", _ => FakeHost.Portaling = true, "portal");
        NoStart("a monster in the combat scan", r => r.Combat.SetScannedTargetsForTests(new[] { new CombatManager.ScannedTarget { Id = unchecked((int)Monster), Distance = 30, Name = "Drudge" } }), "monster 30 yd away");
        NoStart("a hit", r => { r.Tick(); FakeHost.Vitals = (90, 100, 100, 100, 100, 100); }, "hit 0 s ago");
        NoStart("untrained Healing", r => FakeHost.Skills[(Player, KitPlanner.SkillHealing)] = (350, 1), "isn't trained");
        NoStart("under the floor", _ => FakeHost.Skills[(Player, KitPlanner.SkillHealing)] = (150, 2), "floor");

        try
        {
            var rig = new Rig(monster: true, monsterYd: 25);
            for (int i = 0; i < 30; i++) rig.Tick();
            Check.False(rig.Kits.Running, "a monster 25 yd away in the world (not in the scan): no run");
            Check.True(rig.Status.Contains("monster 25 yd away"), "status: " + rig.Status);
        }
        finally { TickIsolationTests.Done(); }

        try
        {
            var rig = new Rig(monster: true, monsterYd: 40);
            rig.TickUntil(() => rig.Kits.Running, max: 50);
            Check.True(rig.Kits.Running, "a monster 40 yd away (outside 35): it runs");
        }
        finally { TickIsolationTests.Done(); }

        try
        {
            var rig = new Rig(sets: 0);
            for (int i = 0; i < 10; i++) rig.Tick(30_000);
            Check.False(rig.Kits.Running, "out of herbs: no run");
            Check.Eq(rig.Lines.Count(l => l.Contains("none can be made now")), 1, "said once in ten looks: " + string.Join(" | ", rig.Lines));
        }
        finally { TickIsolationTests.Done(); }

        try
        {
            var rig = new Rig(kits: 2);
            for (int i = 0; i < 30; i++) rig.Tick();
            Check.False(rig.Kits.Running, "two kits carried: no run");
            Check.True(rig.Status.Contains("2 kits carried"), "status: " + rig.Status);
            rig.S.MakeKitsBelow = 3;
            rig.Auto.LookNow();
            rig.TickUntil(() => rig.Kits.Running, max: 50);
            Check.True(rig.Kits.Running, "under 3 now: it runs");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void MonsterStopsAndCoolsDown()
    {
        try
        {
            var rig = new Rig(sets: 3);
            rig.TickUntil(() => rig.Kits.Running, max: 50);
            Check.True(rig.Kits.Running, "running");
            rig.Combat.SetScannedTargetsForTests(new[] { new CombatManager.ScannedTarget { Id = unchecked((int)Monster), Distance = 30, Name = "Drudge" } });
            Check.False(rig.Tick(), "a monster at 30 yd: no hold on this tick");
            Check.False(rig.Kits.Running, "the run stopped at once");
            string line = rig.Lines.LastOrDefault() ?? "";
            Check.True(line.Contains("Stopped: a monster came within 30 yd.") && line.Contains("Next try in 60 s"), "says why and when: " + line);
            rig.Combat.SetScannedTargetsForTests(Array.Empty<CombatManager.ScannedTarget>());
            int uses = rig.Uses;
            for (int i = 0; i < 58; i++) rig.Tick(1_000);
            Check.False(rig.Kits.Running, "58 s later: still waiting");
            Check.Eq(rig.Uses, uses, "nothing combined meanwhile");
            Check.True(rig.Status.Contains("next check in"), "status: " + rig.Status);
            rig.Tick(3_000);
            rig.TickUntil(() => rig.Kits.Running, max: 50);
            Check.True(rig.Kits.Running, "past 60 s: running again");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void GoesToPeace()
    {
        try
        {
            var rig = new Rig(sets: 2);
            FakeHost.CombatModeValue = CombatMode.Melee;
            Check.True(rig.Tick(), "held");
            Check.True(FakeHost.CombatModeRequests.Contains(CombatMode.NonCombat), "peace mode asked for");
            Check.True(rig.Status.Contains("peace"), "status: " + rig.Status);
            for (int i = 0; i < 5; i++) Check.True(rig.Tick(), "held while it switches");
            Check.False(rig.Kits.Running, "not started out of peace");
            FakeHost.CombatModeValue = CombatMode.NonCombat;
            rig.TickUntil(() => rig.Kits.Running, max: 30);
            Check.True(rig.Kits.Running, "in peace and still: started");

            // Never reaching peace: gives up, the bot goes on, 60 s wait.
            var stuck = new Rig(sets: 2);
            FakeHost.CombatModeValue = CombatMode.Melee;
            for (int i = 0; i < 80; i++) stuck.Tick();
            Check.False(stuck.Kits.Running, "never in peace: no run");
            Check.False(stuck.Tick(), "and no hold any more");
            Check.True(stuck.Status.Contains("couldn't go to peace mode"), "status: " + stuck.Status);
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void NothingMadeBacksOff()
    {
        try
        {
            var rig = new Rig(sets: 1);
            rig.FailAll = 1;
            rig.TickUntil(() => rig.Kits.Running, max: 50);
            Check.True(rig.Kits.Running, "running");
            rig.TickUntil(() => !rig.Kits.Running);
            Check.Eq(rig.Kits.MadeCount, 0, "made nothing");
            string line = rig.Lines.LastOrDefault() ?? "";
            Check.True(line.Contains("made 0") && line.Contains("Next try in 10 min"), "the summary says 10 min: " + line);
            // More herbs arrive; it still waits out the 10 minutes.
            FakeHost.Ints[(Bandages, StackKey)] = 2;
            int uses = rig.Uses;
            for (int i = 0; i < 9; i++) rig.Tick(60_000);
            Check.Eq(rig.Uses, uses, "9 min: nothing tried");
            Check.Eq(rig.Lines.Count(l => l.Contains("made 0")), 1, "and nothing said again");
        }
        finally { TickIsolationTests.Done(); }
    }

    private static void Settings()
    {
        var d = new LegacyUiSettings();
        Check.False(d.MakeKitsWhenLow, "off by default");
        Check.Eq(d.MakeKitsBelow, 2, "under 2");
        Check.Eq(d.MakeKitsUpTo, 5, "up to 5");
        var old = JsonSerializer.Deserialize("{\"EnableAutostack\":true}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.False(old.MakeKitsWhenLow, "a profile from before the setting: off");
        Check.Eq(old.MakeKitsBelow, 2, "and 2");
        var saved = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { MakeKitsWhenLow = true, MakeKitsBelow = 4, MakeKitsUpTo = 9 }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(saved.MakeKitsWhenLow && saved.MakeKitsBelow == 4 && saved.MakeKitsUpTo == 9, "saved and loaded");
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { MakeKitsWhenLow = true, MakeKitsBelow = 0, MakeKitsUpTo = 900 }, dst });
        Check.True(dst.MakeKitsWhenLow, "a profile switch copies it");
        Check.Eq(dst.MakeKitsBelow, 1, "a bad count is clamped (1)");
        Check.Eq(dst.MakeKitsUpTo, KitAuto.MaxUpTo, "and the other (100)");
    }
}
