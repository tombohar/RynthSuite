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
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Make ammo when low (a players' request, 2026-11-23: "set an amount of arrows and if we fall below it
// does it between combat"), on the real plugin and the fake engine: RynthAiPlugin.Ammo.cs decides when
// (the Make kits when low rules and checks), MissileCraftingManager's top-off makes the combines. A bow
// in hand, Greater Arrows in the quiver, bundles in the pack; the fake server makes 100 arrows a combine.
internal static class AmmoAutoPluginTests
{
    public static void Register(Runner r)
    {
        r.Add("ammo when low (plugin): under N and quiet - holds the bot, makes up to M (3 combines for 40 -> 300), no wield over a full quiver, then hunts", TopsUp);
        r.Add("ammo when low (plugin): a monster closing in stops the top-off at once, then 60 s before the next try", MonsterStops);
        r.Add("ammo when low (plugin): a hit stops the top-off at once", HitStops);
        r.Add("ammo when low (plugin): never goes to peace mode for a top-off in a fight (before it or during it)", NeverPeaceInFight);
        r.Add("ammo when low (plugin): off by default - nothing changes; settings saved, loaded, copied and clamped", OffByDefault);
        r.Add("ammo when low (plugin): the empty-quiver path still crafts in a fight with nothing to shoot", EmergencyPathStillWorks);
        r.Add("ammo when low (plugin): an empty quiver between fights - the top-off wields the arrows it made", EquipsIntoEmptySlot);
        r.Add("ammo when low (plugin): blockers - no launcher, untrained, too hard, a launcher swap, Missile Crafting off, enough carried", Blockers);
        r.Add("ammo when low (plugin): /ra ammo make works with the macro off; /ra ammo stop stops it (10 min wait)", MakeNowAndStop);
        r.Add("ammo when low: a combine's yield - 100 for a plain bundle, 1000 for a wrapped one", Yields);
    }

    private const uint Player = TickIsolationTests.Player;
    private const uint Lb = 0xA9B40000;
    private const uint Bow = 0x80520001, Quiver = 0x80520002, Heads = 0x80520003, Shafts = 0x80520004, PackArrows = 0x80520005;
    private const uint Monster = 0x80520010;
    private const uint StackKey = 12, ValidKey = 9;
    private const int MissileSlot = 0x00400000, AmmoSlot = 0x00800000;
    private const uint SkillFletching = 37;

    private sealed class Rig
    {
        public readonly TickIsolationTests.Bot Bot;
        public readonly MiniRemoteController Remote;
        public readonly MissileCraftingManager Cm;
        public LegacyUiSettings S => Bot.S;
        public bool Fight;                 // the crafting manager's own fight signal (on top of the plugin's)
        public bool FailAll;
        private readonly List<(uint Src, uint Tgt)> _pending = new();
        private uint _nextId = 0x80620001;
        private static readonly DateTime Epoch = new(2026, 11, 23, 12, 0, 0);

        public Rig(int quiver = 40, int bundles = 10, int fletching = 400, int training = 3, bool bow = true,
                   string heads = "Bundle of Greater Arrowheads", string shafts = "Bundle of Arrowshafts", int pack = 0)
        {
            Bot = new TickIsolationTests.Bot(combat: true, heal: false, arrange: () =>
            {
                FakeHost.SalvageCalls = true;      // GetObjectWielderInfo
                FakeHost.UseOnCalls = true;
                FakeHost.CombatModeCalls = true;   // peace mode requests recorded
                FakeHost.CombatModeValue = CombatMode.NonCombat;
                FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
                FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
                FakeHost.Skills[(Player, SkillFletching)] = (fletching, training);
                if (bow)
                {
                    Item(Bow, "Longbow", MissileSlot, 1);
                    FakeHost.Wielded[Bow] = MissileSlot;   // in hand
                }
                if (quiver > 0)
                {
                    Item(Quiver, "Greater Arrow", AmmoSlot, quiver);
                    FakeHost.Wielded[Quiver] = AmmoSlot;
                }
                if (pack > 0) { Item(PackArrows, "Greater Arrow", AmmoSlot, pack); FakeHost.Containers[PackArrows] = Player; }
                if (bundles > 0)
                {
                    Item(Heads, heads, 0, bundles);
                    FakeHost.Containers[Heads] = Player;
                    Item(Shafts, shafts, 0, bundles);
                    FakeHost.Containers[Shafts] = Player;
                }
                FakeHost.OnUseObjectOn = (src, tgt) => _pending.Add((src, tgt));
            });
            RynthClock.TestNow = () => Epoch.AddMilliseconds(Bot.Now);
            foreach (uint id in new[] { Bow, Quiver, Heads, Shafts, PackArrows })
                if (FakeHost.Names.ContainsKey(id)) _ = Bot.Cache[unchecked((int)id)];
            S.MonsterRange = 20;               // the safe radius is then 35 yd
            S.EnableMissileCrafting = true;
            S.MakeAmmoWhenLow = true;
            S.MakeAmmoBelow = 100;
            S.MakeAmmoUpTo = 300;
            string dir = Path.Combine(Program.TempRoot, "ammoauto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var cache = Bot.Cache;
            var s = S;
            Remote = new MiniRemoteController(Bot.Host, dir, () => cache, () => s, () => null, () => { }, new ServerHubClient(Bot.Host));
            Remote.Crafting.AutoKits.NowMs = () => Bot.Now;   // KitNow: the plugin's clock for both
            Bot.Set("_miniRemote", Remote);

            Cm = new MissileCraftingManager(Bot.Host, S);
            Cm.SetObjectCache(cache);
            var skills = new CharacterSkills(Bot.Host);
            skills.SetPlayerId(Player);
            Cm.SetCharacterSkills(skills);
            var combat = Combat;
            Cm.HoldWhile = () => combat.AmmoSwapInProgress;
            Cm.FightIsOn = () => Fight || Bot.P.FightIsOn();
            Bot.Set("_missileCraftingManager", Cm);
            Bot.Call("WireAmmoTopOff", Cm);
        }

        private static void Item(uint id, string name, int valid, int stack)
        {
            FakeHost.Names[id] = name;
            FakeHost.ItemTypes[id] = 0x100;
            if (valid != 0) FakeHost.Ints[(id, ValidKey)] = valid;
            FakeHost.Ints[(id, StackKey)] = stack;
        }

        public CombatManager Combat => (CombatManager)Bot.Get("_combatManager")!;

        /// <summary>One OnTick's worth: the danger checks, the kits' hold, the ammo hold, the server's answers. True = held.</summary>
        public bool Tick(long ms = 100)
        {
            Bot.Now += ms;
            Bot.Call("CheckAutoKitsDanger", S);
            Bot.Call("CheckAutoAmmoDanger", S);
            bool hold = (bool)Bot.Call("TickAutoAmmo", S)!;
            ForceRescan();
            var sent = _pending.ToList();
            _pending.Clear();
            foreach (var (src, tgt) in sent) Serve(src, tgt);
            return hold;
        }

        public int TickUntil(Func<bool> until, int max = 600, long ms = 100)
        {
            int held = 0;
            for (int i = 0; i < max && !until(); i++) if (Tick(ms)) held++;
            return held;
        }

        /// <summary>The fake server: 100 Greater Arrows into the pack a combine (a head and a shaft bundle used).</summary>
        private void Serve(uint src, uint tgt)
        {
            if (src != Heads || tgt != Shafts) return;
            Take(Heads);
            Take(Shafts);
            if (FailAll) { Cm.HandleChat("You fail to make a bundle of greater arrows."); return; }
            if (FakeHost.Containers.TryGetValue(PackArrows, out _) && FakeHost.Ints.ContainsKey((PackArrows, StackKey)))
                FakeHost.Ints[(PackArrows, StackKey)] += 100;
            else
            {
                uint id = PackArrows;
                FakeHost.Names[id] = "Greater Arrow";
                FakeHost.ItemTypes[id] = 0x100;
                FakeHost.Ints[(id, ValidKey)] = AmmoSlot;
                FakeHost.Ints[(id, StackKey)] = 100;
                FakeHost.Containers[id] = Player;
                _ = Bot.Cache[unchecked((int)id)];
            }
            Cm.HandleChat("You make a bundle of greater arrows.");
        }

        private void Take(uint id)
        {
            int n = FakeHost.Ints.GetValueOrDefault((id, StackKey), 1) - 1;
            if (n > 0) { FakeHost.Ints[(id, StackKey)] = n; return; }
            FakeHost.Ints.Remove((id, StackKey));
            FakeHost.Containers.Remove(id);
            Bot.Cache.OnDeleteObject(id);
        }

        public void ForceRescan()
        {
            typeof(WorldObjectCache).GetField("_lastDirectInventoryScan", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Bot.Cache, DateTime.MinValue);
            EquippedState.Invalidate();
        }

        public int Carried => Cm.LookTopOff(forceRefresh: true).Carried;
        public int Combines => FakeHost.UsesOn.Count;
        public bool PeaceAsked => FakeHost.CombatModeRequests.Contains(CombatMode.NonCombat);
        public List<string> Lines => FakeHost.Chat.Where(c => c.Contains("Make ammo")).ToList();
        public string Status => (string)Bot.Call("AmmoAutoStatusText", S)!;
        public void MonsterAt(double yd) =>
            Combat.SetScannedTargetsForTests(new[] { new CombatManager.ScannedTarget { Id = unchecked((int)Monster), Distance = yd, Name = "Drudge" } });

        public List<string> Typed(string line)
        {
            Bot.Set("_initialized", true);   // the chat bar reads typed commands once the plugin is up
            int before = FakeHost.Chat.Count;
            int eat = 0;
            Bot.P.OnChatBarEnter(line, ref eat);
            return FakeHost.Chat.Skip(before).ToList();
        }
    }

    private static void Done()
    {
        RynthClock.TestNow = null;
        TickIsolationTests.Done();
    }

    private static string Show(IEnumerable<string> lines) => string.Join(" | ", lines);

    private static void TopsUp()
    {
        try
        {
            var rig = new Rig(quiver: 40);
            Check.Eq(rig.Carried, 40, "40 arrows carried (the quiver)");
            int held = rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            Check.True(rig.Cm.TopOffRunning, "a top-off started by itself: " + rig.Status);
            Check.True(held >= 9, $"the bot was held while getting ready ({held} ticks)");
            Check.Eq(rig.Cm.TopOffPlanned, 3, "3 combines planned (40 + 3 x 100 reaches 300)");
            string start = rig.Lines.FirstOrDefault(l => l.Contains("carried (under 100)")) ?? "";
            Check.True(start.Contains("40 arrows carried (under 100); making 3 combines of Greater Arrow, about 300, toward 300"), "the start line says why: " + Show(rig.Lines));
            Check.True(rig.Tick(), "held while it runs");
            Check.True(rig.Status.StartsWith("running "), "status: " + rig.Status);
            rig.TickUntil(() => !rig.Cm.TopOffRunning);
            Check.Eq(rig.Combines, 3, "three combines sent");
            Check.Eq(rig.Carried, 340, "340 carried now");
            Check.False(FakeHost.Uses.Contains(PackArrows), "the quiver had arrows: nothing wielded");
            Check.False(rig.Tick(), "over: the bot hunts as normal");
            string line = rig.Lines.LastOrDefault() ?? "";
            Check.True(line.Contains("made 3 combines of Greater Arrow (about 300 arrows)") && line.Contains("Stopped: made the 3 combines planned."),
                "one summary line: " + line);
            Check.False(FakeHost.Chat.Any(c => c.StartsWith("[RynthAi Craft]")), "crafting's own lines stay in the log: " + Show(FakeHost.Chat));
            int combines = rig.Combines;
            for (int i = 0; i < 30; i++) rig.Tick(5_000);
            Check.Eq(rig.Combines, combines, "340 carried (not under 100): no more top-offs");
            Check.True(rig.Status.Contains("340 arrows carried"), "the status says why: " + rig.Status);
        }
        finally { Done(); }
    }

    private static void MonsterStops()
    {
        try
        {
            var rig = new Rig(quiver: 40);
            rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            rig.Tick();   // the first combine goes out
            Check.True(rig.Cm.TopOffRunning, "running");
            rig.MonsterAt(30);
            Check.False(rig.Tick(), "a monster at 30 yd: no hold on this tick");
            Check.False(rig.Cm.TopOffRunning, "the top-off stopped at once");
            Check.False(rig.Cm.IsCrafting, "crafting let go of the bot");
            string line = rig.Lines.LastOrDefault() ?? "";
            Check.True(line.Contains("Stopped: a monster came within 30 yd.") && line.Contains("Next try in 60 s"), "says why and when: " + line);
            rig.Combat.SetScannedTargetsForTests(Array.Empty<CombatManager.ScannedTarget>());
            rig.S.MakeAmmoBelow = 250;   // the one combine made 140: still under the line
            int combines = rig.Combines;
            for (int i = 0; i < 58; i++) rig.Tick(1_000);
            Check.False(rig.Cm.TopOffRunning, "58 s later: still waiting");
            Check.Eq(rig.Combines, combines, "nothing combined meanwhile");
            rig.Tick(3_000);
            rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            Check.True(rig.Cm.TopOffRunning, "past 60 s: running again");
        }
        finally { Done(); }

        try
        {
            var rig = new Rig(quiver: 40);
            rig.MonsterAt(30);
            for (int i = 0; i < 40; i++) rig.Tick();
            Check.False(rig.Cm.TopOffRunning, "a monster 30 yd away: no start");
            Check.Eq(rig.Combines, 0, "nothing combined");
            Check.False(rig.PeaceAsked, "no peace mode asked for");
            Check.True(rig.Status.Contains("monster 30 yd away"), "status: " + rig.Status);
        }
        finally { Done(); }
    }

    private static void HitStops()
    {
        try
        {
            var rig = new Rig(quiver: 40);
            rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            rig.Tick();
            Check.True(rig.Cm.TopOffRunning, "running");
            FakeHost.Vitals = (95, 100, 100, 100, 100, 100);
            rig.Tick();
            Check.False(rig.Cm.TopOffRunning, "a hit: stopped at once");
            string line = rig.Lines.LastOrDefault() ?? "";
            Check.True(line.Contains("Stopped: you were hit."), "says why: " + line);
        }
        finally { Done(); }
    }

    private static void NeverPeaceInFight()
    {
        // A fight before the top-off: in missile mode, a monster in range, under the line.
        try
        {
            var rig = new Rig(quiver: 40);
            FakeHost.CombatModeValue = CombatMode.Missile;
            rig.MonsterAt(10);
            for (int i = 0; i < 80; i++) rig.Tick();
            Check.False(rig.PeaceAsked, "no peace mode asked for in a fight");
            Check.Eq(rig.Combines, 0, "no combine");
            Check.False(rig.Tick(), "and no hold: combat has the tick");
        }
        finally { Done(); }

        // The crafting manager on its own: a top-off isn't started in a fight.
        try
        {
            var rig = new Rig(quiver: 40);
            rig.Fight = true;
            string why = (string)typeof(MissileCraftingManager).GetMethod("StartTopOff", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Cm, new object?[] { 300, false, "test", null })!;
            Check.Eq(why, "a fight is on", "StartTopOff refuses in a fight");
            Check.False(rig.Cm.IsCrafting, "nothing started");
        }
        finally { Done(); }

        // A fight starting during the top-off (seen first by crafting itself): it stops, no peace asked again.
        try
        {
            var rig = new Rig(quiver: 40);
            rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            rig.Tick();
            Check.True(rig.Cm.TopOffRunning, "running");
            int combines = rig.Combines;
            FakeHost.CombatModeRequests.Clear();
            rig.Fight = true;
            for (int i = 0; i < 40; i++) rig.Tick();
            Check.False(rig.Cm.TopOffRunning, "a fight: stopped");
            Check.Eq(rig.Combines, combines, "no combine after the fight began");
            Check.False(rig.PeaceAsked, "no peace mode asked for after the fight began");
            Check.True((rig.Lines.LastOrDefault() ?? "").Contains("Stopped: a fight."), "says why: " + Show(rig.Lines));
        }
        finally { Done(); }
    }

    private static void OffByDefault()
    {
        var d = new LegacyUiSettings();
        Check.False(d.MakeAmmoWhenLow, "off by default");
        Check.Eq(d.MakeAmmoBelow, 100, "under 100");
        Check.Eq(d.MakeAmmoUpTo, 500, "up to 500");
        var old = JsonSerializer.Deserialize("{\"EnableAutostack\":true}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.False(old.MakeAmmoWhenLow, "a profile from before the setting: off");
        Check.Eq(old.MakeAmmoBelow, 100, "and 100");
        var saved = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { MakeAmmoWhenLow = true, MakeAmmoBelow = 150, MakeAmmoUpTo = 900 }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(saved.MakeAmmoWhenLow && saved.MakeAmmoBelow == 150 && saved.MakeAmmoUpTo == 900, "saved and loaded");
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { MakeAmmoWhenLow = true, MakeAmmoBelow = 0, MakeAmmoUpTo = 99_999 }, dst });
        Check.True(dst.MakeAmmoWhenLow, "a profile switch copies it");
        Check.Eq(dst.MakeAmmoBelow, 1, "a bad count is clamped (1)");
        Check.Eq(dst.MakeAmmoUpTo, MissileCraftingManager.MaxAmmoUpTo, "and the other (10000)");

        try
        {
            var rig = new Rig(quiver: 40);
            rig.S.MakeAmmoWhenLow = false;
            for (int i = 0; i < 60; i++) Check.False(rig.Tick(), "off: never held");
            Check.Eq(rig.Combines, 0, "off: nothing combined");
            Check.False(rig.PeaceAsked, "off: no peace mode asked for");
            Check.Eq(rig.Status, "off", "status: off");
            Check.Eq(rig.Lines.Count, 0, "off: nothing said: " + Show(rig.Lines));
        }
        finally { Done(); }
    }

    private static readonly FieldInfo LastAmmoCheck =
        typeof(MissileCraftingManager).GetField("_lastAmmoCheck", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void EmergencyPathStillWorks()
    {
        try
        {
            // Nothing to shoot at all, in a fight: the top-off stays out, the empty-quiver path makes one combine.
            var rig = new Rig(quiver: 0);
            FakeHost.CombatModeValue = CombatMode.Missile;
            rig.MonsterAt(10);
            rig.Fight = true;
            for (int i = 0; i < 20; i++) rig.Tick();
            Check.False(rig.Cm.TopOffRunning, "no top-off in the fight");
            Check.Eq(rig.Combines, 0, "no top-off combine");
            for (int i = 0; i < 6 && rig.Combines == 0; i++)
            {
                if (rig.Cm.State == MissileCraftingManager.CraftState.Idle) LastAmmoCheck.SetValue(rig.Cm, DateTime.MinValue);
                rig.Bot.Now += 1_000;
                rig.ForceRescan();
                rig.Cm.ProcessCrafting();   // OnTick's crafting step
            }
            Check.Eq(rig.Combines, 1, "the empty-quiver path made its first combine, as before");
            Check.True(FakeHost.Chat.Any(c => c.StartsWith("[RynthAi Craft]") && c.Contains("Crafting Greater Arrow")),
                "its own chat line, as before: " + Show(FakeHost.Chat));
        }
        finally { Done(); }
    }

    private static void EquipsIntoEmptySlot()
    {
        try
        {
            var rig = new Rig(quiver: 0);
            rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            Check.True(rig.Cm.TopOffRunning, "an empty quiver and nothing carried: a top-off between fights");
            rig.TickUntil(() => !rig.Cm.TopOffRunning);
            Check.Eq(rig.Combines, 3, "three combines (0 -> 300)");
            Check.True(FakeHost.Uses.Contains(PackArrows), "the arrows made were wielded");
            Check.True((rig.Lines.LastOrDefault() ?? "").Contains("Stopped: made the 3 combines planned."), "summary: " + Show(rig.Lines));
        }
        finally { Done(); }
    }

    private static void Blockers()
    {
        void NoStart(string name, Func<Rig> make, Action<Rig> set, string status)
        {
            try
            {
                var rig = make();
                set(rig);
                for (int i = 0; i < 40; i++) rig.Tick();
                Check.False(rig.Cm.TopOffRunning, name + ": no top-off");
                Check.Eq(rig.Combines, 0, name + ": nothing combined");
                Check.False(rig.Tick(), name + ": the bot isn't held");
                Check.True(rig.Status.Contains(status), $"{name}: the status says '{status}': {rig.Status}");
            }
            finally { Done(); }
        }
        NoStart("no launcher", () => new Rig(bow: false), _ => { }, "no bow, crossbow or atlatl in hand");
        NoStart("untrained Fletching", () => new Rig(training: 1), _ => { }, "Fletching is untrained");
        NoStart("too hard", () => new Rig(fletching: 100), _ => { }, "under the 90% line");
        NoStart("Missile Crafting off", () => new Rig(), r => r.S.EnableMissileCrafting = false, "Enable Missile Crafting is off");
        NoStart("macro off", () => new Rig(), r => r.S.IsMacroRunning = false, "macro is off");
        NoStart("a heal line", () => new Rig(), _ => FakeHost.Vitals = (50, 100, 100, 100, 100, 100), "health 50%");
        NoStart("enough carried", () => new Rig(quiver: 60, pack: 50), _ => { }, "110 arrows carried");
        NoStart("no bundles", () => new Rig(bundles: 0), _ => { }, "no heads and shafts for arrows");
        NoStart("a launcher swap", () => new Rig(), r =>
        {
            typeof(CombatManager).GetField("_ammoFollowUpFor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(r.Combat, unchecked((int)Bow));
            typeof(CombatManager).GetField("_ammoFollowUpUntil", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(r.Combat, RynthClock.Now.AddHours(1));
        }, "a launcher swap");
    }

    private static void MakeNowAndStop()
    {
        try
        {
            var rig = new Rig(quiver: 150);   // over the line (100), under Make up to (300)
            rig.S.IsMacroRunning = false;
            rig.S.MakeAmmoWhenLow = false;
            var said = rig.Typed("/ra ammo make");
            Check.True(said.Any(l => l.Contains("Make ammo now: making Greater Arrow up to 300 as soon as it is safe")), "the reply: " + Show(said));
            rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            Check.True(rig.Cm.TopOffRunning && rig.Cm.TopOffManual, "macro off: Make ammo now runs");
            Check.Eq(rig.Cm.TopOffPlanned, 2, "2 combines (150 -> 350)");
            rig.Tick();
            said = rig.Typed("/ra ammo stop");
            Check.False(rig.Cm.TopOffRunning, "/ra ammo stop: stopped");
            Check.True(said.Any(l => l.Contains("Stopped: stopped by you.") && l.Contains("Next try in 10 min")), "the summary answers: " + Show(said));

            said = rig.Typed("/ra ammo stop");
            Check.True(said.Any(l => l.Contains("nothing is running")), "again: nothing is running: " + Show(said));
            said = rig.Typed("/ra ammo");
            Check.True(said.Any(l => l.Contains("carried for the launcher in hand")) && said.Any(l => l.Contains("Make ammo when low: off")), "status: " + Show(said));
            said = rig.Typed("/ra ammo banana");
            Check.True(said.Any(l => l.Contains("unknown option") && l.Contains(RynthAiPlugin.AmmoUsage)), "a wrong option: " + Show(said));
            said = rig.Typed("/ra ammo auto upto 0");
            Check.True(said.Any(l => l.Contains("from 1 to 10000")), "out of range: " + Show(said));
            said = rig.Typed("/ra ammo auto on");
            Check.True(rig.S.MakeAmmoWhenLow && said.Any(l => l.Contains("Make ammo when low: on")), "on: " + Show(said));
        }
        finally { Done(); }

        // The panel's buttons go through the same path on the next tick.
        try
        {
            var rig = new Rig(quiver: 150);
            rig.S.MakeAmmoWhenLow = false;
            rig.Cm.RequestMakeNow();
            rig.TickUntil(() => rig.Cm.TopOffRunning, max: 50);
            Check.True(rig.Cm.TopOffRunning, "the panel's Make ammo now started a top-off");
            rig.Cm.RequestStop();
            rig.Tick();
            Check.False(rig.Cm.TopOffRunning, "the panel's Stop stopped it");
        }
        finally { Done(); }
    }

    private static void Yields()
    {
        AmmoRecipe Find(string head, string shaft) => AmmoRecipes.All.First(r => r.HeadName == head && r.ShaftName == shaft);
        Check.Eq(AmmoRecipes.EstimatedYield(Find("Bundle of Greater Arrowheads", "Bundle of Arrowshafts")), 100, "a plain bundle: 100");
        Check.Eq(AmmoRecipes.EstimatedYield(Find("Wrapped Bundle of Deadly Prismatic Arrowheads", "Wrapped Bundle of Arrowshafts")), 1000, "a wrapped bundle: 1000");
        Check.True(AmmoRecipes.HeadIsInfinite(AmmoRecipes.All.First(r => r.HeadName.StartsWith("Infinite"))), "Infinite heads are never used up");
        Check.Eq(MissileCraftingManager.AmmoText(1, WeaponCategory.Crossbow), "1 quarrel", "one quarrel");
        Check.Eq(MissileCraftingManager.AmmoText(12, WeaponCategory.Atlatl), "12 darts", "darts");
    }
}
