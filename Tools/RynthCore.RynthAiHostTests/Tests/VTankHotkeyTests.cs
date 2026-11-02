using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// VTank's hotkey actions as /ra commands (2026-10-13, RynthAiPlugin.VTankHotkeys.cs): target lock,
// melee height, Boost Nav/Loot Priority toggles, LootThisCont (loot the open container, never
// house storage or your own packs) and Recharge (one vitals pass with the Top Off lines).
internal static class VTankHotkeyTests
{
    private const uint Player = 0x50000F01;
    private const uint Lb = 0xA9B40000;
    private const uint A = 0x80000501, B = 0x80000502, C = 0x80000503, Npc = 0x80000504;
    private const uint Chest = 0x7A9B5001, Gem = 0x80264101, Spoon = 0x80264102, SidePack = 0x80264200, PackGem = 0x80264201;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800, ItemTypeMisc = 0x80;
    private const uint STypeLife = 33;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("vt keys: target lock picks the selected monster over the Targeting setting and a higher priority", TargetLockPicks);
        r.Add("vt keys: target lock holds through blinks, ends on the kill / after 30 s out of reach / when cleared", TargetLockEnds);
        r.Add("vt keys: /ra targetlock refuses non-monsters and dead ones; the same monster again releases", TargetLockCommand);
        r.Add("vt keys: /ra meleelow|meleemid|meleehigh set the melee attack height", MeleeHeight);
        r.Add("vt keys: NavPrio / LootPrio / NavLootPrio toggle Boost Nav / Loot Priority", PriorityToggles);
        r.Add("vt keys: /ra setmetastate switches the state (the meta's spelling), lists states without one", MetaState);
        r.Add("vt keys: LootThisCont loots an open chest with the macro and Looting off, and one Chest loot skips", LootThisLoots);
        r.Add("vt keys: LootThisCont never takes house storage, your own packs, a vendor, or nothing open", LootThisRefuses);
        r.Add("vt keys: LootThisCont re-loots a corpse marked done", LootThisCorpse);
        r.Add("vt keys: Recharge is one vitals pass with the Top Off lines, macro off, on the next heartbeat", RechargeOnce);
    }

    // ── Target lock ───────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly CombatManager Combat;
        public readonly LegacyUiSettings S;
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;
        private DateTime _now = DateTime.Now;

        public Rig()
        {
            FakeHost.Reset();
            Place(Player, "Me", 96, 96, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            Place(A, "Mob A", 96, 98);
            Place(B, "Mob B", 96, 99);
            Place(C, "Boss", 96, 100);
            S = new LegacyUiSettings { MonsterRange = 20, TargetSelection = 1 /* closest */, TargetLock = true };
            S.MonsterRules.Add(new MonsterRule { Name = "Boss", Priority = 5 });
            var host = FakeHost.Create();
            Host = host;
            Cache = FakeHost.MakeCache(host, Player, new[] { A, B, C });
            Combat = new CombatManager(host, S, Cache);
            Combat.SetPlayerId(Player);
            Combat.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            Combat.TargetClock = () => _now;
        }

        public void Advance(double ms) => _now = _now.AddMilliseconds(ms);

        public uint Pick(params (uint Id, double Dist)[] scan)
        {
            Combat.SetScannedTargetsForTests(scan.Select(t => new CombatManager.ScannedTarget
            {
                Id = unchecked((int)t.Id), Distance = t.Dist, Angle = 0, Bearing = double.NaN, Name = FakeHost.Names[t.Id],
            }));
            Combat.HandleCombatTrigger();
            return unchecked((uint)Combat.activeTargetId);
        }
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static void TargetLockPicks()
    {
        var t = new Rig();
        Check.Eq(t.Pick((A, 2), (B, 6)), A, "no lock: closest, A");
        Check.Eq(t.Combat.ForcedTargetRefusal(unchecked((int)B)), null, "B can be locked onto");
        t.Combat.SetForcedTarget(unchecked((int)B));
        Check.Eq(t.Pick((A, 2), (B, 6)), B, "target lock: B, though A is closer and Target Lock held A");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] switch 0x80000501") && l.Contains("(target lock,") && l.Contains("picked by the player")),
            "the switch says it was the target lock");
        Check.Eq(t.Pick((A, 1), (B, 6), (C, 3)), B, "kept when the Boss (priority 5) is in range");

        t = new Rig();
        Check.Eq(t.Pick((A, 2), (C, 4)), C, "no lock: the Boss's priority wins");
        t.Combat.SetForcedTarget(unchecked((int)A));
        Check.Eq(t.Pick((A, 2), (C, 4)), A, "target lock: A, over the higher priority");

        // Not in the scan yet (out of sight): normal selection meanwhile, the lock waits.
        t = new Rig();
        t.Combat.SetForcedTarget(unchecked((int)B));
        Check.Eq(t.Pick((A, 2)), A, "B not in the scan: A meanwhile");
        Check.Eq(t.Pick((A, 2), (B, 7)), B, "B comes into sight: B");
    }

    private static void TargetLockEnds()
    {
        var t = new Rig();
        t.Combat.SetForcedTarget(unchecked((int)C));
        S(t).MonsterRules[0].Priority = 0;   // the Boss rule now says ignore: the lock still attacks it
        Check.Eq(t.Pick((A, 2), (C, 5)), C, "a Priority 0 monster is attacked when locked onto");
        Check.False(t.Combat.IsIgnored(unchecked((int)C)), "and isn't treated as ignored meanwhile");
        Check.Eq(t.Pick((A, 2)), C, "it blinks out of the scan: kept within the grace");
        t.Advance(1000);
        Check.Eq(t.Pick((A, 2)), C, "still within the 1.5 s grace");
        Check.Eq(t.Pick((A, 2), (C, 5)), C, "back in the scan");

        t.Combat.OnKillNotification("Boss's perforated corpse falls before you!");
        Check.Eq(t.Pick((A, 2)), A, "killed: the next target is picked normally");
        Check.Eq(t.Combat.ForcedTargetId, 0, "and the lock is over");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Target lock on Boss ended: killed")), "chat says the lock ended");

        // Never in reach: ends after ForcedTargetLostMs.
        t = new Rig();
        t.Combat.SetForcedTarget(unchecked((int)B));
        Check.Eq(t.Pick((A, 2)), A, "B never in the scan: A");
        t.Advance(CombatManager.ForcedTargetLostMs + 100);
        t.Pick((A, 2));
        Check.Eq(t.Combat.ForcedTargetId, 0, "30 s never in reach: the lock ends");
        Check.True(FakeHost.Chat.Any(c => c.Contains("ended: never in reach")), "and says why");

        // Cleared: normal selection again.
        t = new Rig();
        t.Combat.SetForcedTarget(unchecked((int)B));
        Check.Eq(t.Pick((A, 2), (B, 6)), B, "locked on B");
        t.Combat.ClearForcedTarget();
        Check.Eq(t.Pick((A, 2), (B, 6)), B, "cleared: Target Lock (the setting) keeps the current fight");
    }

    private static LegacyUiSettings S(Rig t) => t.S;

    private static void TargetLockCommand()
    {
        var t = new Rig();
        FakeHost.Names[Npc] = "Town Crier";
        FakeHost.ItemTypes[Npc] = 0x10;
        FakeHost.NotAttackable.Add(Npc);
        t.Cache.OnCreateObject(Npc);
        t.Cache.Tick();

        var bot = new Bot(t.Host, t.Cache);
        bot.Set("_combatManager", t.Combat);
        bot.S.IsMacroRunning = true;
        bot.S.EnableCombat = true;

        FakeHost.SelectedItem = 0;
        bot.Ra("/ra targetlock");
        Check.True(bot.LastChat.Contains("nothing is selected"), "nothing selected: refused (" + bot.LastChat + ")");

        FakeHost.SelectedItem = Npc;
        bot.Ra("/ra targetlock");
        Check.True(bot.LastChat.Contains("can't be attacked"), "an NPC: refused (" + bot.LastChat + ")");
        Check.Eq(t.Combat.ForcedTargetId, 0, "no lock");

        // A corpse selected mid-fight (R-20261021-191720-c788): never a target, and the fight goes on.
        const uint Corpse = 0x80000505;
        FakeHost.Names[Corpse] = "Corpse of Mob A";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Positions[Corpse] = (Lb | 0x1C, 96f, 97f, 0f);
        FakeHost.Bitfields[Corpse] = 0x00002000;   // the corpse flag
        FakeHost.NotAttackable.Add(Corpse);
        t.Cache.OnCreateObject(Corpse);
        t.Cache.Tick();
        Check.Eq(t.Pick((B, 3)), B, "fighting B");
        FakeHost.SelectedItem = Corpse;
        bot.P.OnSelectedTargetChange(Corpse, B);
        bot.Ra("/ra targetlock");
        Check.True(bot.LastChat.Contains("isn't a monster"), "a corpse: refused (" + bot.LastChat + ")");
        Check.Eq(t.Combat.ForcedTargetId, 0, "no lock on the corpse");
        Check.Eq(t.Pick((B, 3)), B, "the corpse selected: still fighting B");

        t.Cache.OnUpdateHealth(A, 0f);
        FakeHost.SelectedItem = A;
        bot.Ra("/ra targetlock");
        Check.True(bot.LastChat.Contains("is dead"), "a dead monster: refused (" + bot.LastChat + ")");

        FakeHost.SelectedItem = B;
        bot.Ra("/ra targetlock");
        Check.Eq(t.Combat.ForcedTargetId, unchecked((int)B), "B selected: locked");
        Check.True(bot.LastChat.Contains("Target lock: attacking Mob B."), "chat: " + bot.LastChat);

        bot.Ra("/ra targetlock");
        Check.Eq(t.Combat.ForcedTargetId, 0, "the same monster again: released");
        Check.True(bot.LastChat.Contains("released"), "chat: " + bot.LastChat);

        bot.Ra("/ra targetlock");
        bot.S.IsMacroRunning = false;
        FakeHost.SelectedItem = C;
        bot.Ra("/ra targetlock");
        Check.Eq(t.Combat.ForcedTargetId, unchecked((int)C), "another monster: the lock moves to it");
        Check.True(bot.LastChat.Contains("the macro is off"), "macro off: said (" + bot.LastChat + ")");
        bot.Ra("/ra targetlock off");
        Check.Eq(t.Combat.ForcedTargetId, 0, "/ra targetlock off");
    }

    // ── Plugin harness ───────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host, WorldObjectCache? cache = null)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = cache ?? new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
        }

        public void UseGemProfile()
        {
            string profile = Path.Combine(Program.TempRoot, "loot-vtkeys-" + Guid.NewGuid().ToString("N")[..8] + ".json");
            var p = new LootProfile { Name = "vtkeys" };
            var gems = new LootRule { Name = "Gems", Action = LootAction.Keep };
            gems.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(gems);
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            object[] args = { parts[1].ToLowerInvariant(), parts, 0 };
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(P, args);
        }
        public string LastChat => FakeHost.Chat.Count > 0 ? FakeHost.Chat[^1] : "";
        public string AllChat => string.Join(" | ", FakeHost.Chat);
        public void Create(params uint[] ids)
        {
            foreach (uint id in ids) Cache.OnCreateObject(id);
            Cache.Tick();
        }
        public void Tick(BotActivity act = BotActivity.Idle)
        {
            Set("_activity", act);
            Call("TickCorpseOpening");
        }
        public void TickUntil(Func<bool> done, int max = 200)
        {
            for (int i = 0; i < max && !done(); i++) { Tick(); Thread.Sleep(10); }
        }
        public bool Moved(uint id) => FakeHost.ExternalMoves.Any(m => m.Id == id);
    }

    // ── Melee height, priorities, meta state ─────────────────────────────────

    private static void MeleeHeight()
    {
        FakeHost.Reset();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.MeleeAttackHeight = 1;
        bot.Ra("/ra meleelow");
        Check.Eq(bot.S.MeleeAttackHeight, 0, "MeleeLow: 0 (Low)");
        Check.True(bot.LastChat.Contains("Melee attack height: Low"), bot.LastChat);
        bot.Ra("/ra meleehigh");
        Check.Eq(bot.S.MeleeAttackHeight, 2, "MeleeHigh: 2 (High)");
        bot.Ra("/ra meleemid");
        Check.Eq(bot.S.MeleeAttackHeight, 1, "MeleeMid: 1 (Medium)");
        Check.True(bot.LastChat.Contains("Medium"), bot.LastChat);
        int missile = bot.S.MissileAttackHeight;
        bot.Ra("/ra meleelow");
        Check.Eq(bot.S.MissileAttackHeight, missile, "missile height untouched");
    }

    private static void PriorityToggles()
    {
        // Pure rule.
        Check.Eq(RynthAiPlugin.NextPriority(false, false, true, false, ""), (true, false), "NavPrio from off: nav on");
        Check.Eq(RynthAiPlugin.NextPriority(true, true, true, false, ""), (false, true), "NavPrio from on: nav off, loot kept");
        Check.Eq(RynthAiPlugin.NextPriority(false, true, false, true, ""), (false, false), "LootPrio from on: off");
        Check.Eq(RynthAiPlugin.NextPriority(false, false, true, true, ""), (true, true), "NavLootPrio, both off: both on");
        Check.Eq(RynthAiPlugin.NextPriority(true, false, true, true, ""), (true, true), "NavLootPrio, one on: both on");
        Check.Eq(RynthAiPlugin.NextPriority(true, true, true, true, ""), (false, false), "NavLootPrio, both on: both off");
        Check.Eq(RynthAiPlugin.NextPriority(true, false, true, false, "on"), (true, false), "'on' sets, doesn't toggle");
        Check.Eq(RynthAiPlugin.NextPriority(true, true, true, true, "off"), (false, false), "'off'");

        FakeHost.Reset();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Ra("/ra navprio");
        Check.True(bot.S.BoostNavPriority && !bot.S.BoostLootPriority, "/ra navprio: Boost Nav on");
        Check.True(bot.LastChat.Contains("Boost Nav Priority on"), bot.LastChat);
        bot.Ra("/ra lootprio");
        Check.True(bot.S.BoostNavPriority && bot.S.BoostLootPriority, "/ra lootprio: Boost Loot on");
        bot.Ra("/ra navlootprio");
        Check.True(!bot.S.BoostNavPriority && !bot.S.BoostLootPriority, "/ra navlootprio with both on: both off");
        bot.Ra("/ra navlootprio");
        Check.True(bot.S.BoostNavPriority && bot.S.BoostLootPriority, "again: both on");
        Check.True(bot.LastChat.Contains("Boost Nav Priority on, Boost Loot Priority on"), bot.LastChat);
        bot.Ra("/ra navprio off");
        Check.True(!bot.S.BoostNavPriority && bot.S.BoostLootPriority, "/ra navprio off");
    }

    private static void MetaState()
    {
        FakeHost.Reset();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.S.MetaRules.Add(new MetaRule { State = "Default" });
        bot.S.MetaRules.Add(new MetaRule { State = "Hunting Grounds" });
        bot.S.MetaRules.Add(new MetaRule { State = "Hunting Grounds" });
        bot.S.MetaRules.Add(new MetaRule { State = "Go Home" });
        bot.Set("_metaManager", new RynthCore.Plugin.RynthAi.Meta.MetaManager(bot.S, host, new PlayerVitalsCache()));

        Check.Eq(string.Join(",", (List<string>)bot.Call("LoadedMetaStates")!), "Default,Hunting Grounds,Go Home", "the loaded meta's states, once each");
        bot.Ra("/ra setmetastate hunting grounds");
        Check.Eq(bot.S.CurrentState, "Hunting Grounds", "set, in the meta's spelling");
        Check.True(bot.S.ForceStateReset, "the state restarts");
        bot.Ra("/ra setmetastate Nowhere");
        Check.Eq(bot.S.CurrentState, "Nowhere", "an unknown state is still set (it may come with another meta)");
        Check.True(bot.LastChat.Contains("has no state 'Nowhere'"), bot.LastChat);
        FakeHost.Chat.Clear();
        bot.Ra("/ra setmetastate");
        Check.True(bot.AllChat.Contains("States: Default, Hunting Grounds, Go Home"), "no state: lists them (" + bot.AllChat + ")");
    }

    // ── LootThisCont ──────────────────────────────────────────────────────────

    private static void ChestWorld(string name = "Chest")
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;
        FakeHost.WeaponCalls = true;
        FakeHost.CloseContainerCalls = true;
        var me = (0x1048002Au, 100f, 100f, 0f);
        FakeHost.Names[Player] = "Dargoth Hera";
        FakeHost.Positions[Player] = me;
        FakeHost.PlayerPose = me;
        FakeHost.Names[Chest] = name;
        FakeHost.ItemTypes[Chest] = ItemTypeContainer;
        FakeHost.Positions[Chest] = (me.Item1, 101f, 100f, 0f);
        FakeHost.NotAttackable.Add(Chest);
        FakeHost.Containers[Chest] = 0;
        AddItem(Gem, "Black Opal", ItemTypeGem, Chest);
        AddItem(Spoon, "Rusty Spoon", ItemTypeMisc, Chest);
    }

    private static void AddItem(uint id, string name, uint type, uint container)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = type;
        FakeHost.Containers[id] = container;
        FakeHost.Appraised.Add(id);
    }

    private static void LootThisLoots()
    {
        // Macro off and Looting off: nothing happens on its own; LootThisCont loots it.
        ChestWorld();
        var bot = new Bot(FakeHost.Create(Player));
        bot.UseGemProfile();
        bot.S.IsMacroRunning = false;
        bot.S.EnableLooting = false;
        bot.S.LootChestsMacroOff = false;   // the chest waits for LootThisCont (Pickup with macro off would take it)
        bot.Create(Chest, Gem, Spoon);
        FakeHost.GroundContainer = Chest;
        bot.P.OnViewObjectContents(Chest);
        bot.P.OnUpdateObjectInventory(Chest);
        for (int i = 0; i < 20; i++) { bot.Tick(); Thread.Sleep(5); }
        Check.False(bot.Moved(Gem), "macro off: the chest isn't looted on its own");

        bot.Ra("/ra lootthis");
        bot.TickUntil(() => bot.Moved(Gem));
        Check.True(FakeHost.ExternalMoves.Contains((Gem, Player, 0)), "LootThisCont: the gem goes into the pack");
        Check.True(FakeHost.Chat.Any(c => c.Contains("LootThisCont: looting Chest (the macro is off: this container only)")), bot.AllChat);
        FakeHost.Containers[Gem] = Player;
        bot.TickUntil(() => FakeHost.Closes.Count > 0);
        Check.False(bot.Moved(Spoon), "the spoon matches nothing and stays");
        Check.Eq(string.Join(",", FakeHost.Closes.Select(c => $"{c:X8}")), $"{Chest:X8}", "closed when done");
        int moves = FakeHost.ExternalMoves.Count;
        for (int i = 0; i < 20; i++) { bot.Tick(); Thread.Sleep(5); }
        Check.Eq(bot.Get<int>("_forcedLootContainerId"), 0, "the flag ends with the container");
        Check.Eq(FakeHost.ExternalMoves.Count, moves, "and nothing else is looted with the macro off");

        // A chest Chest loot skips (held by another object): looted when flagged.
        ChestWorld();
        FakeHost.Containers[Chest] = 0x7A9B5099;
        bot = new Bot(FakeHost.Create(Player));
        bot.UseGemProfile();
        bot.Create(Chest, Gem, Spoon);
        FakeHost.GroundContainer = Chest;
        bot.P.OnViewObjectContents(Chest);
        bot.P.OnUpdateObjectInventory(Chest);
        for (int i = 0; i < 20; i++) { bot.Tick(BotActivity.Looting); Thread.Sleep(5); }
        Check.False(bot.Moved(Gem), "held by another object: Chest loot skips it");
        bot.Ra("/ra lootthiscont");
        bot.TickUntil(() => bot.Moved(Gem));
        Check.True(bot.Moved(Gem), "flagged: looted anyway");
    }

    private static void LootThisRefuses()
    {
        // Nothing open.
        ChestWorld();
        var bot = new Bot(FakeHost.Create(Player));
        bot.Create(Chest, Gem, Spoon);
        bot.Ra("/ra lootthis");
        bot.Tick();
        Check.True(bot.LastChat.Contains("no container is open"), bot.LastChat);

        // House storage by its house owner, and by name.
        ChestWorld();
        FakeHost.InstanceIds[(Chest, 32)] = 0x50000ABC;
        bot = new Bot(FakeHost.Create(Player));
        bot.Create(Chest, Gem, Spoon);
        FakeHost.GroundContainer = Chest;
        bot.Ra("/ra lootthis");
        bot.TickUntil(() => FakeHost.ExternalMoves.Count > 0, 30);
        Check.True(FakeHost.Chat.Any(c => c.Contains("house storage is never looted")), bot.AllChat);
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing taken out of house storage");

        ChestWorld("Storage");
        bot = new Bot(FakeHost.Create(Player));
        bot.Create(Chest, Gem, Spoon);
        FakeHost.GroundContainer = Chest;
        Check.Eq(bot.Call("LootThisRefusal", unchecked((int)Chest)), "house storage is never looted", "named Storage");

        // Your own packs.
        ChestWorld();
        FakeHost.Names[SidePack] = "Pack";
        FakeHost.ItemTypes[SidePack] = ItemTypeContainer;
        FakeHost.Containers[SidePack] = Player;
        AddItem(PackGem, "Black Opal", ItemTypeGem, SidePack);
        bot = new Bot(FakeHost.Create(Player));
        bot.Create(SidePack, PackGem);
        Check.Eq(bot.Call("LootThisRefusal", unchecked((int)SidePack)), "it's in your inventory", "a side pack");
        Check.Eq(bot.Call("LootThisRefusal", unchecked((int)Player)), "that's your own pack", "the main pack");
        FakeHost.GroundContainer = SidePack;
        bot.Ra("/ra lootthis");
        bot.TickUntil(() => FakeHost.ExternalMoves.Count > 0, 30);
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nothing moved out of your own pack");

        // A vendor.
        ChestWorld();
        FakeHost.Bitfields[Chest] = 0x00000200;
        bot = new Bot(FakeHost.Create(Player));
        bot.Create(Chest);
        Check.Eq(bot.Call("LootThisRefusal", unchecked((int)Chest)), "it's a vendor", "a vendor");
        Check.Eq(bot.Call("LootThisRefusal", 0), "no container is open (open a corpse or chest first)", "nothing");
    }

    private static void LootThisCorpse()
    {
        const uint Corpse = 0x80264301, Loot = 0x80264302;
        ChestWorld();
        FakeHost.Names[Corpse] = "Corpse of Mob A";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Positions[Corpse] = (0x1048002Au, 101f, 100f, 0f);
        FakeHost.Bitfields[Corpse] = 0x00002000;   // a corpse
        FakeHost.Containers[Corpse] = 0;
        AddItem(Loot, "Black Opal", ItemTypeGem, Corpse);
        var bot = new Bot(FakeHost.Create(Player));
        bot.UseGemProfile();
        bot.Create(Corpse, Loot);
        Check.True((bool)bot.Call("IsCorpseLikeObject", unchecked((int)Corpse))!, "the test corpse is a corpse to the looter");
        bot.Get<Dictionary<int, long>>("_completedCorpses")[unchecked((int)Corpse)] = 1;   // looted (or skipped) before
        FakeHost.GroundContainer = Corpse;
        bot.P.OnViewObjectContents(Corpse);
        for (int i = 0; i < 20; i++) { bot.Tick(BotActivity.Looting); Thread.Sleep(5); }
        Check.False(bot.Moved(Loot), "a completed corpse reopened: not looted again on its own");

        bot.Ra("/ra lootthis");
        bot.TickUntil(() => bot.Moved(Loot));
        Check.True(bot.Moved(Loot), "LootThisCont: looted again");
    }

    // ── Recharge ──────────────────────────────────────────────────────────────

    private static BuffManager Vitals(uint hp, uint st, uint mn, out LegacyUiSettings s)
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
        FakeHost.Skills[(Player, STypeLife)] = (400, 2);
        var host = FakeHost.Create();
        s = new LegacyUiSettings
        {
            IsMacroRunning = false, HealAt = 50, TopOffHP = 90, EmergencyHealAt = 0, StaminaToHealthAt = 0,
            RestamAt = 0, TopOffStam = 0, GetManaAt = 0, TopOffMana = 0, UsePotions = false,
        };
        var skills = new CharacterSkills(host);
        skills.SetPlayerId(Player);
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        sm.SetCharacterSkills(skills);
        var vitals = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = hp, MaxStamina = 100, CurrentStamina = st, MaxMana = 100, CurrentMana = mn };
        var b = new BuffManager(host, s, sm, vitals);
        b.SetCharacterSkills(skills);
        b.InCombatOverride = () => true;   // a monster in range: the in-combat lines
        return b;
    }

    private static void RechargeOnce()
    {
        // 70% health: over Heal At 50 (in combat nothing heals), under Top Off HP 90.
        var b = Vitals(70, 100, 100, out _);
        Check.False(b.CheckVitals(), "the normal step in combat: 70% is over Heal At 50, nothing");
        Check.Eq(FakeHost.Casts.Count, 0, "no cast");
        string said = b.RechargeOnceNow();
        Check.Eq(FakeHost.Casts.Count, 1, "Recharge: one cast");
        Check.True(said.StartsWith("healing"), "it says it heals (" + said + ")");
        int healFamily = SpellTableStub.GetById(SpellTable.Rows.First(r => r.Name == "Heal Self I").Id)?.Family ?? -1;
        Check.Eq(SpellTableStub.GetById(FakeHost.Casts[0].SpellId)?.Family, healFamily, "a Heal Self (the top level known)");
        Check.True(b.InCombatOverride!(), "the in-combat lines are back after the pass");

        // Everything over Top Off: nothing.
        b = Vitals(95, 100, 100, out _);
        said = b.RechargeOnceNow();
        Check.Eq(FakeHost.Casts.Count, 0, "all over Top Off: no cast");
        Check.True(said.StartsWith("nothing to do"), said);

        // Requested (as /ra recharge does): runs on the next heartbeat, macro off, and says so.
        ActionPacer.Reset();
        b = Vitals(70, 100, 100, out var s);
        Check.False(s.IsMacroRunning, "macro off");
        b.RequestRecharge();
        Check.True(b.RechargePending, "pending until a heartbeat");
        for (int i = 0; i < 20 && b.RechargePending; i++) { b.OnHeartbeat(); Thread.Sleep(10); }
        Check.False(b.RechargePending, "taken on the heartbeat");
        Check.Eq(FakeHost.Casts.Count, 1, "one cast, macro off");
        Check.True(FakeHost.Chat.Any(c => c.StartsWith("[RynthAi] Recharge: healing")), string.Join(" | ", FakeHost.Chat));
        for (int i = 0; i < 5; i++) { b.OnHeartbeat(); Thread.Sleep(10); }
        Check.Eq(FakeHost.Casts.Count, 1, "one pass only: nothing more with the macro off");
    }
}
