using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-31, owner: "We need a loot mode that only loots mana stones when it needs them and rares."
// Loot Mode "Rares and needed mana stones only" (LootModes.Rares): the loot profile is not used. One
// corpse, looted with LootSnob (Fixtures\lootsnob-excerpt.utl), in each mode:
//   * a rare gem (its rare icon, IconUnderlay 0x06005B0C, from the create message; never identified),
//   * a rare ring known by its RareId (identified),
//   * a Blunt Crossbow (identified) that LootSnob's "(M) Xbow (OD +9)" keeps,
//   * a gold Helm (identified) that LootSnob's "(Salv) Gold" salvages,
//   * a Mana Stone, with tapping on and the keep count 5 (none in the pack: needed).
// The loot clock (CorpseClock) is moved by hand; the server lands each move in the pack at once.
internal static class LootRaresModeTests
{
    private const uint Player = 0x50000D51;
    private const uint Corpse = 0x80267100;
    private const uint Xbow = 0x80267101, RareGem = 0x80267102, RareRing = 0x80267103, Stone = 0x80267104, Helm = 0x80267105;
    private const uint PackStone = 0x80267106;
    private const uint ItemTypeContainer = 0x200, ItemTypeMissile = 0x100, ItemTypeGem = 0x800, ItemTypeJewelry = 0x8,
                       ItemTypeArmor = 0x2, ItemTypeManaStone = 0x80000;
    private const uint LongDescKey = 16;
    private const string Me = "Test Hunter";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const long TickMs = 25;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x1048002Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("loot rares mode: the rares and a needed mana stone are looted; the OD crossbow LootSnob keeps and the gold salvage are not", RaresModeCorpse);
        r.Add("loot rares mode: a mana stone at the keep count is left (not needed), and the corpse's end line says the mode", StoneNotNeeded);
        r.Add("loot rares mode: profile mode is unchanged (crossbow kept, helm salvaged, stone kept, the rares left)", ProfileModeUnchanged);
        r.Add("loot rares mode: a rare needs no ID (rare icon) or is known by its RareId; Mana Tap takes nothing", NoIdNoTap);
        r.Add("loot rares mode: the loot details line names the reason (rare, mana stone (needed))", DetailsLineNamesReason);
        r.Add("loot rares mode: the setting round trip (profile file, profile switch, engine face, meta LootMode)", SettingRoundTrip);
        r.Add("loot rares mode: /ra loot mode replies with the mode, or why not and the usage; /ra loot why gives the mode's verdict", Commands);
        r.Add("loot rares mode: the dashboard's LOOT line and the status say the mode", ShowsTheMode);
    }

    // ── Clock and world ───────────────────────────────────────────────────────

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 70_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    private static void World(bool stoneInPack)
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;        // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;         // RequestId, HasAppraisalData
        FakeHost.CloseContainerCalls = true; // CloseContainer + GetGroundContainerId
        FakeHost.ContentsCalls = true;       // GetContainerContents: the stone count walks the pack
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Names[Corpse] = "Corpse of Olthoi Swarm Drone";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);

        // The crossbow, identified: LootSnob's (M) Xbow (OD +9) keeps it (as in LootLateIdTests).
        Item(Xbow, "Blunt Crossbow", ItemTypeMissile, Corpse, appraised: true);
        foreach (var (k, v) in new (uint, int)[] { (1, 0x100), (9, 0x00400000), (19, 12000), (5, 600), (131, 60),
                     (105, 6), (353, 9), (158, 2), (159, 47), (160, 385), (204, 22), (45, 4), (48, 47) })
            FakeHost.Ints[(Xbow, k)] = v;
        FakeHost.Doubles[(Xbow, 63)] = 2.62;
        FakeHost.Doubles[(Xbow, 29)] = 1.15;
        FakeHost.SpellIds[Xbow] = new[] { (uint)SpellDatabase.GetIdByName("Legendary Blood Thirst") };
        // Mana on it (an ID shows it): Mana Tap would take it in rares mode if Mana Tap were on there.
        FakeHost.Ints[(Xbow, 107)] = 4000;

        // A rare gem as the create message shows it: its rare icon plate, no ID.
        Item(RareGem, "Pearl of Spirit Drinking", ItemTypeGem, Corpse, appraised: false);
        FakeHost.DataIds[(RareGem, RareItems.IconUnderlayKey)] = 0x06005B0C;
        // A rare known by its RareId (identified), without the icon plate.
        Item(RareRing, "Ring of Fortitude", ItemTypeJewelry, Corpse, appraised: true);
        FakeHost.Ints[(RareRing, RareItems.RareIdKey)] = 30183;

        // A gold helm, identified: LootSnob's (Salv) Gold salvages it.
        Item(Helm, "Helm", ItemTypeArmor, Corpse, appraised: true);
        foreach (var (k, v) in new (uint, int)[] { (131, 60), (105, 6), (19, 900), (5, 300) })
            FakeHost.Ints[(Helm, k)] = v;

        Item(Stone, "Mana Stone", ItemTypeManaStone, Corpse, appraised: true);
        FakeHost.Ints[(Stone, 107)] = 0;
        if (stoneInPack)
        {
            Item(PackStone, "Mana Stone", ItemTypeManaStone, Player, appraised: true);
            FakeHost.Ints[(PackStone, 107)] = 0;
        }
    }

    private static void Item(uint id, string name, uint type, uint container, bool appraised)
    {
        FakeHost.Names[id] = name;
        FakeHost.ItemTypes[id] = type;
        FakeHost.Containers[id] = container;
        if (appraised) FakeHost.Appraised.Add(id);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
            LootAssessWindowMs = 200, LootCorpseTimeoutMs = 12_000,
            EnableManaTapping = true, ManaTapMinMana = 2500, ManaStoneKeepCount = 5,
        };
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;
        public readonly LegacyDashboardRenderer Dash;
        private int _seenMoves;

        public Bot(RynthCoreHost host, int mode)
        {
            Host = host;
            S.LootMode = mode;
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            Dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(Dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(Dash, S);
            Set("_dashboard", Dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });
            Set("_manaStoneManager", new ManaStoneManager(host, S, Cache));

            VTankLootProfile p = VTankLootParser.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lootsnob-excerpt.utl"));
            string path = Path.Combine(Program.TempRoot, "loot-rares-mode.utl");
            File.WriteAllText(path, VTankLootWriter.Serialize(p));
            S.CurrentLootPath = path;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        public void Tick()
        {
            _now += TickMs;
            Set("_activity", BotActivity.Looting);
            Call("TickCorpseOpening");
            while (_seenMoves < FakeHost.ExternalMoves.Count)
            {
                var m = FakeHost.ExternalMoves[_seenMoves++];
                FakeHost.Containers[m.Id] = m.Target;
            }
        }

        public void Create(params uint[] ids)
        {
            foreach (uint id in ids) Cache.OnCreateObject(id);
            for (int i = 0; i < 5; i++) Cache.Tick();
        }

        public void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int eat = 0;
            object[] args = { parts[1].ToLowerInvariant(), parts, eat };
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(P, args);
        }

        public (bool Taken, string Action, string Rule, string BuiltIn) Classify(uint id)
        {
            MethodInfo m = typeof(RynthAiPlugin).GetMethod("ClassifyLootItem", Any)!;
            object?[] args = { Cache[unchecked((int)id)]!, S, true, null, null, null, null };
            bool taken = (bool)m.Invoke(P, args)!;
            return (taken, (string)args[3]!, (string)args[5]!, (string)args[6]!);
        }

        public bool CorpseDone => Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)Corpse));
    }

    private static bool Said(string part) => FakeHost.Chat.Any(l => l.Contains(part, StringComparison.Ordinal));
    private static string ChatDump() => string.Join(" | ", FakeHost.Chat);
    private static bool Moved(uint id) => FakeHost.ExternalMoves.Any(m => m.Id == id && m.Target == Player);
    private static readonly uint[] OnCorpse = { Xbow, RareGem, RareRing, Helm, Stone };

    /// <summary>Opens the corpse (the server's ViewContents) and loots it to its end.</summary>
    private static Bot LootCorpse(int mode, bool stoneInPack = false, Action<Bot>? setUp = null)
    {
        World(stoneInPack);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, mode);
        setUp?.Invoke(bot);
        var ids = new List<uint> { Corpse };
        ids.AddRange(OnCorpse);
        if (stoneInPack) ids.Add(PackStone);
        bot.Create(ids.ToArray());
        long end = _now + 30_000;
        bool opened = false;
        while (_now < end && !bot.CorpseDone)
        {
            bot.Tick();
            if (!opened && FakeHost.Uses.Contains(Corpse))
            {
                opened = true;
                FakeHost.UseDone(0);
                FakeHost.GroundContainer = Corpse;
                bot.P.OnViewObjectContents(Corpse);
                bot.P.OnUpdateObjectInventory(Corpse);
            }
        }
        Check.True(opened, "the corpse is opened (rares mode still opens corpses)");
        Check.True(bot.CorpseDone, "the corpse is finished");
        return bot;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void RaresModeCorpse() => Clocked(() =>
    {
        LootCorpse(LootModes.Rares);
        Check.True(Moved(RareGem), $"the rare gem is looted ({ChatDump()})");
        Check.True(Moved(RareRing), "the rare ring (RareId) is looted");
        Check.True(Moved(Stone), "the mana stone is looted: none in the pack, keep count 5");
        Check.False(Moved(Xbow), "the OD crossbow LootSnob keeps is NOT looted (the profile isn't used)");
        Check.False(Moved(Helm), "the gold helm LootSnob salvages is NOT looted");
        Check.True(Said("Loot rule: Pearl of Spirit Drinking -> [Keep] Rares mode: rare"), "the pickup says why: a rare");
        Check.True(Said("Loot rule: Mana Stone -> [Keep] Rares mode: mana stone (needed)"), "and: a needed mana stone");
        Check.False(Said("(M) Xbow"), "no LootSnob rule is named");
        Check.False(Said("(Salv) Gold"), "nor its salvage rule");
    });

    private static void StoneNotNeeded() => Clocked(() =>
    {
        LootCorpse(LootModes.Rares, stoneInPack: true, bot =>
        {
            bot.S.ManaStoneKeepCount = 1;
            FakeHost.DataIds.Remove((RareGem, RareItems.IconUnderlayKey));   // no rares this time
            FakeHost.Ints.Remove((RareRing, RareItems.RareIdKey));
        });
        Check.False(Moved(Stone), $"the stone is left: one in the pack, keep count 1 ({ChatDump()})");
        Check.True(Said("Mana stone match: 'Mana Stone' (have 1/1) — at cap, leaving."), "and said, as in profile mode");
        Check.False(OnCorpse.Any(Moved), "nothing else is looted");
        Check.True(Said("[RynthAi] Nothing to loot on Corpse of Olthoi Swarm Drone: 1 item(s) match, but you already have enough."),
            "the end line: the stone matched, you have enough");

        // With no stone either, the end line names the mode.
        LootCorpse(LootModes.Rares, stoneInPack: true, bot =>
        {
            bot.S.ManaStoneKeepCount = 1;
            bot.S.EnableManaTapping = false;   // no ManaStone rules, tapping off: no stone is a mana stone to keep
            FakeHost.DataIds.Remove((RareGem, RareItems.IconUnderlayKey));
            FakeHost.Ints.Remove((RareRing, RareItems.RareIdKey));
        });
        Check.False(OnCorpse.Any(Moved), "nothing looted");
        Check.True(Said("[RynthAi] Nothing on Corpse of Olthoi Swarm Drone is a rare or a needed mana stone (Loot Mode: rares)."),
            $"the end line says the mode ({ChatDump()})");
    });

    private static void ProfileModeUnchanged() => Clocked(() =>
    {
        LootCorpse(LootModes.Profile);
        Check.True(Moved(Xbow), $"the crossbow is kept by LootSnob ({ChatDump()})");
        Check.True(Said("Loot rule: Blunt Crossbow -> [Keep] (M) Xbow (OD +9)"), "by its (M) Xbow (OD +9) rule");
        Check.True(Moved(Helm), "the gold helm is taken");
        Check.True(Said("Loot rule: Helm -> [Salvage] (Salv) Gold"), "for LootSnob's (Salv) Gold");
        Check.True(Moved(Stone), "the mana stone is kept by the built-in stone rule");
        Check.True(Said("Loot rule: Mana Stone -> [Keep] ManaStone (Consumable, 1/5)"), "under its old label");
        Check.False(Moved(RareGem), "the rare gem: no LootSnob rule takes it, so profile mode leaves it, as before");
        Check.False(Said("Rares mode"), "nothing says rares mode");
    });

    private static void NoIdNoTap() => Clocked(() =>
    {
        World(stoneInPack: false);
        // An empty stone in the pack: Mana Tap (profile mode) would loot a 4,000-mana item to drain.
        Item(PackStone, "Mana Stone", ItemTypeManaStone, Player, appraised: true);
        FakeHost.Ints[(PackStone, 107)] = 0;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, LootModes.Rares);
        bot.Create(Corpse, Xbow, RareGem, RareRing, Helm, Stone, PackStone);

        var gem = bot.Classify(RareGem);
        Check.True(gem.Taken && gem.Action == "Keep" && gem.Rule == LootModes.RareRule, $"the rare gem, never identified: [{gem.Action}] {gem.Rule}");
        Check.True(gem.BuiltIn.StartsWith("a rare (rare icon)", StringComparison.Ordinal), "known by its rare icon: " + gem.BuiltIn);
        var ring = bot.Classify(RareRing);
        Check.True(ring.Taken && ring.BuiltIn.StartsWith("a rare (RareId 30183)", StringComparison.Ordinal), "the ring by its RareId: " + ring.BuiltIn);
        Check.False((bool)bot.Call("ItemNeedsAppraisalForLoot", bot.Cache[unchecked((int)RareGem)]!)!, "rares mode asks for no ID");
        Check.False((bool)bot.Call("ItemNeedsAppraisalForLoot", bot.Cache[unchecked((int)Helm)]!)!, "for any item");

        var xbow = bot.Classify(Xbow);
        Check.False(xbow.Taken, $"the 4,000-mana crossbow is not a Mana Tap pickup in rares mode: [{xbow.Action}] {xbow.Rule}");
        Check.True(xbow.BuiltIn.StartsWith("not a rare or a mana stone", StringComparison.Ordinal), "and says why: " + xbow.BuiltIn);
        var stone = bot.Classify(Stone);
        Check.True(stone.Taken && stone.Rule == LootModes.StoneRule, $"the stone: 1 in the pack, keep count 5: [{stone.Action}] {stone.Rule}");
        Check.True(stone.BuiltIn.StartsWith("a needed mana stone, 1 of 5 kept", StringComparison.Ordinal), stone.BuiltIn);

        // Profile mode, same world: no LootSnob rule takes the 4,000-mana ring, so Mana Tap does.
        FakeHost.Ints[(RareRing, 107)] = 4000;
        bot.S.LootMode = LootModes.Profile;
        var tap = bot.Classify(RareRing);
        Check.True(tap.Taken && tap.Action == "ManaTap", $"profile mode: Mana Tap as before: [{tap.Action}] {tap.Rule}");
        bot.S.LootMode = LootModes.Rares;
        Check.Eq(bot.Classify(RareRing).Action, "Keep", "rares mode: the same ring is kept as a rare, not drained");
    });

    private static void DetailsLineNamesReason() => Clocked(() =>
    {
        LootCorpse(LootModes.Rares, setUp: bot => bot.S.ItemInfoSettings.LootChat = true);
        Check.True(Said("[RynthAi] Looted [Keep] Pearl of Spirit Drinking (Rares mode: rare)"), $"the rare's line ({ChatDump()})");
        Check.True(Said("[RynthAi] Looted [Keep] Mana Stone (Rares mode: mana stone (needed))"), "the stone's line");
        Check.False(Said("rule \"Rares mode"), "shown as the reason, not as a rule name");
    });

    private static void SettingRoundTrip()
    {
        // The settings profile file.
        var ctx = RynthAiJsonContext.Default.LegacyUiSettings;
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(new LegacyUiSettings { LootMode = LootModes.Rares }, ctx), ctx);
        Check.Eq(back?.LootMode, LootModes.Rares, "saved and loaded with the settings profile");
        var old = JsonSerializer.Deserialize("{\"EnableLooting\":true}", ctx);
        Check.Eq(old?.LootMode, LootModes.Profile, "a profile saved before it loads as Profile");

        // A profile switch copies it.
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { LootMode = LootModes.Rares }, dst });
        Check.Eq(dst.LootMode, LootModes.Rares, "a profile switch copies it");
        Check.True(!LootModes.IsRares(7) && LootModes.Clamp(7) == LootModes.Profile, "an unknown value reads as Profile");

        // The engine's Settings face: sent; a save without it leaves it alone.
        var s = new LegacyUiSettings { LootMode = LootModes.Rares };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", Any)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent?.LootMode == LootModes.Rares, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.Eq(s.LootMode, LootModes.Rares, "a save without it leaves it alone");
        dash.ApplySettingsJson("{\"LootMode\":0}");
        Check.Eq(s.LootMode, LootModes.Profile, "a save with 0 sets Profile");

        // The meta settings map (setsetting / getsetting LootMode).
        var live = new LegacyUiSettings();
        var map = ExpressionTests.Engine(settings: live).BuildSettingsMapPublic();
        Check.True(map.TryGetValue("LootMode", out var entry), "LootMode is in the meta settings map");
        Check.Eq(entry.Get(), "0", "reads 0 (Profile)");
        entry.Set("1");
        Check.Eq(live.LootMode, LootModes.Rares, "1 sets rares");
        Check.Eq(entry.Get(), "1", "and reads 1");
        entry.Set("profile");
        Check.Eq(live.LootMode, LootModes.Profile, "the word 'profile' works too");
        entry.Set("rares");
        Check.Eq(live.LootMode, LootModes.Rares, "and 'rares'");
        entry.Set("banana");
        Check.Eq(live.LootMode, LootModes.Rares, "anything else changes nothing");
    }

    private static void Commands() => Clocked(() =>
    {
        World(stoneInPack: false);
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, LootModes.Profile);
        bot.Create(Corpse, Xbow, RareGem, RareRing, Helm, Stone);

        FakeHost.Chat.Clear();
        bot.Ra("/ra loot mode rares");
        Check.Eq(bot.S.LootMode, LootModes.Rares, "/ra loot mode rares sets it");
        Check.True(Said("[RynthAi] Loot Mode: Rares and needed mana stones only (mana stones up to the keep count, 5; the loot profile loot-rares-mode.utl is not used)."),
            "and replies with the mode now: " + ChatDump());

        FakeHost.Chat.Clear();
        bot.Ra("/ra loot mode");
        Check.True(Said("[RynthAi] Loot Mode: Rares and needed mana stones only") && Said("Usage: /ra loot mode profile|rares"),
            "no word: says the mode now, and the usage: " + ChatDump());

        FakeHost.Chat.Clear();
        bot.Ra("/ra loot mode banana");
        Check.Eq(bot.S.LootMode, LootModes.Rares, "a wrong word changes nothing");
        Check.True(Said("[RynthAi] loot mode: 'banana'? Loot Mode is still Rares and needed mana stones only") && Said("Usage: /ra loot mode profile|rares"),
            "it says why, the mode now and the usage: " + ChatDump());

        // /ra loot why: the mode's verdict.
        FakeHost.SelectedItem = Xbow;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot why");
        Check.True(Said("[RynthAi] Loot why: 'Blunt Crossbow' (identified) -> left (not a rare or a mana stone (Loot Mode: rares and needed mana stones only)")
                    && Said("Loot Mode: Rares and needed mana stones only"), "the crossbow: left, by the mode: " + ChatDump());
        FakeHost.SelectedItem = RareGem;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot why");
        Check.True(Said("[RynthAi] Loot why: 'Pearl of Spirit Drinking' (not identified yet) -> [Keep] Rares mode: rare (a rare (rare icon)"),
            "the rare gem: kept as a rare: " + ChatDump());

        FakeHost.Chat.Clear();
        bot.Ra("/ra loot mode profile");
        Check.Eq(bot.S.LootMode, LootModes.Profile, "/ra loot mode profile sets it back");
        Check.True(Said("[RynthAi] Loot Mode: Profile (the loot profile)."), "and replies: " + ChatDump());
        FakeHost.SelectedItem = Xbow;
        FakeHost.Chat.Clear();
        bot.Ra("/ra loot why");
        Check.True(Said("-> [Keep] (M) Xbow (OD +9)"), "profile mode's loot why, as before: " + ChatDump());
    });

    private static void ShowsTheMode()
    {
        FakeHost.Reset();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host, LootModes.Profile);
        Check.Eq(bot.Dash.LootLineName(), "loot-rares-mode", "profile mode: the LOOT line is the profile's name");
        bot.S.LootMode = LootModes.Rares;
        Check.Eq(bot.Dash.LootLineName(), "Rares + needed mana stones (loot-rares-mode not used)", "rares mode: the LOOT line says so");
        bot.S.CurrentLootPath = string.Empty;
        Check.Eq(bot.Dash.LootLineName(), "Rares + needed mana stones", "with no profile picked");

        bot.Set("_loginComplete", true);
        string? json = (string?)bot.Call("BuildBotStatusJson");
        Check.True(json != null && json.Contains("\"lootMode\":\"rares\""), "the status says lootMode rares: " + json);
        bot.S.LootMode = LootModes.Profile;
        json = (string?)bot.Call("BuildBotStatusJson");
        Check.True(json != null && json.Contains("\"lootMode\":\"profile\""), "and profile: " + json);
    }
}
