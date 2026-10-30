using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Every chat command answers (2026-10-28, CommandReply.cs). A /ra, /ub, /mt or /vt line the player
// types always says in chat whether it worked: what changed and the value now, or why not and the
// usage, never nothing and never the catch-all "Ran:" line. The same line from a meta is quiet when
// it works (the log has it) and still shows its errors, a repeated one once a minute.
//
// One table: every verb RynthAi's dispatchers take, with a form that works in this rig and a wrong
// one. Verbs whose working form writes under C:\Games (nav / meta / settings saves, dumpspells,
// translateroute, logs prune, trace on) or plays a sound only run their wrong form here.
internal static class CommandReplyTests
{
    public static void Register(Runner r)
    {
        r.Add("command replies: CommandReply - typed always answers, a script's success is quiet, errors once a minute", HelperRules);
        r.Add("command replies: every typed command answers in chat, never with the catch-all line", TypedAlwaysAnswers);
        r.Add("command replies: a typed command's wrong form answers with a failure line (the failure colour)", TypedWrongFormsFail);
        r.Add("command replies: a meta's command is quiet when it works; its error shows, a repeat not within a minute", MetaQuietOnSuccess);
        r.Add("command replies: /ub opt set AutoVendor.Enabled true answers 'AutoVendor.Enabled = True' (a meta's: log only)", UbOptSetSaysValue);
        r.Add("command replies: deferred answers (LootThisCont, Meta Manager) keep who sent the line", DeferredAnswers);
    }

    private const uint Lb = 0xA9B40000;
    private const uint Player = 0x50000F61, Robe = 0x80282301, Sword = 0x80282302, Cloak = 0x80282303;
    private const uint Crier = 0x80000601, Drudge = 0x80000602;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>What a row's working form must do. Act: answer typed, quiet from a meta. Query: output
    /// asked for, shown either way. Elsewhere: the server, the engine or another plugin answers (it is
    /// handed on). Ignored: an accepted no-equivalent verb (typed: a failure line; a meta: once).</summary>
    private enum K { Act, Query, Elsewhere, Ignored }

    /// <summary>A verb: a working form, a wrong one, and a line typed first (state the working form needs).</summary>
    private sealed record Row(string? Valid, string? Invalid, K Kind, string? Setup = null);

    private static readonly Row[] Rows =
    {
        // ── /ra: settings and actions ──
        new("/ra power 80", "/ra power banana", K.Act),
        new("/ra offhand shield", "/ra offhand banana", K.Act),
        new("/ra offhand dual on", "/ra offhand default", K.Act),
        new("/ra follow off", "/ra follow Nobody Here", K.Act),
        new("/ra follow Town Crier", null, K.Act),
        new("/ra navrec on", null, K.Act),
        new("/ra navhud off", null, K.Act),
        new("/ra navtrail on", null, K.Act),
        new("/ra navtrail clear", null, K.Act),
        new("/ra navoverlay off", null, K.Act),
        new("/ra debug off", null, K.Act),
        new("/ra lootcheck off", "/ra lootcheck", K.Act),
        new("/ra groundloot scan", null, K.Act),
        new("/ra groundloot on", null, K.Act),
        new("/ra vtankyield off", "/ra vtankyield banana", K.Act),
        new("/ra stop", null, K.Act),
        new("/ra start", null, K.Act),
        new("/ra resume", null, K.Act),
        new("/ra pause 5", null, K.Act),
        new("/ra clearbusy", null, K.Act),
        new("/ra panic", null, K.Act),
        new("/ra use Faran Robe", "/ra use Nothing Here", K.Act),
        new("/ra usep Robe", "/ra usep", K.Act),
        new("/ra usei Faran Robe", "/ra usei Nothing Here", K.Act),
        new("/ra usel Town Crier", "/ra usel Nothing Here", K.Act),
        new("/ra usepi Robe", null, K.Act),
        new("/ra uselp Crier", null, K.Act),
        new("/ra select Faran Robe", "/ra select Nothing Here", K.Act),
        new("/ra selectp Robe", "/ra selectp", K.Act),
        new("/ra selecti Faran Robe", null, K.Act),
        new("/ra selectl Town Crier", null, K.Act),
        new("/ra give Faran Robe to Town Crier", "/ra give Faran Robe", K.Act),
        new("/ra givep Robe to Town Crier", "/ra givep Robe to Nobody", K.Act),
        new("/ra givexp Faran Robe to Crier", null, K.Act),
        new("/ra givepp Robe to Crier", null, K.Act),
        new("/ra giver ^Faran to Town Crier", "/ra giver ([ to Town Crier", K.Act),
        new("/ra givea Faran Robe to Town Crier", "/ra givea Nothing Here to Town Crier", K.Act),
        new("/ra giveap Robe to Town Crier", null, K.Act),
        new("/ra giveaxp Faran Robe to Crier", null, K.Act),
        new("/ra giveapp Robe to Crier", null, K.Act),
        new("/ra gap Robe to Crier", null, K.Act),
        new("/ra givear ^Faran to Town Crier", null, K.Act),
        new("/ra givea stop", null, K.Act),
        new(null, "/ra ig NoSuchProfileHere to Town Crier", K.Act),
        new(null, "/ra igp NoSuchProfileHere", K.Act),
        new("/ra cast 1234", "/ra cast banana", K.Act),
        new("/ra targetlock off", null, K.Act),
        new("/ra meleelow", null, K.Act),
        new("/ra meleemid", null, K.Act),
        new("/ra meleehigh", null, K.Act),
        new("/ra navprio on", null, K.Act),
        new("/ra lootprio off", null, K.Act),
        new("/ra navlootprio on", null, K.Act),
        new("/ra setmetastate Hunt", "/ra setmetastate", K.Act),
        new("/ra metastate Hunt", null, K.Act),
        new("/ra equipfor Drudge Skulker", "/ra equipfor", K.Act),
        new("/ra myquests", null, K.Act),
        new("/ra refreshquests", null, K.Act),
        new("/ra ghosts forget", null, K.Act),
        new("/ra jump", "/ra jumpq", K.Act),
        new("/ra jumpsw 90 200", "/ra jumpw 90 banana", K.Act),
        new("/ra spells hide", null, K.Act),
        new("/ra trade decline", "/ra trade banana", K.Act),
        new("/ra owncorpse stop", null, K.Act),
        new(null, "/ra testitem", K.Act),
        new(null, "/ra hazard add", K.Act),
        new(null, "/ra hazard texture add banana", K.Act),
        new(null, "/ra dunnav 1 2", K.Act),
        new(null, "/ra dunnav", K.Act),
        new(null, "/ra dunnav-patrol", K.Act),
        new(null, "/ra nav restart", K.Act),
        new(null, "/ra nav banana", K.Act),
        new(null, "/ra meta banana", K.Act),
        new(null, "/ra settings", K.Act),
        new(null, "/ra settings loadchar", K.Act),
        new(null, "/ra loot add banana", K.Act),
        new(null, "/ra lootparse", K.Act),
        new(null, "/ra lootcheckinv", K.Act),
        new(null, "/ra autotrade", K.Act),
        new(null, "/ra forcebuff", K.Act),
        new(null, "/ra cancelforcebuff", K.Act),
        new(null, "/ra buff force", K.Act),
        new(null, "/ra buff skipped", K.Act),
        new(null, "/ra bufftest", K.Act),
        new(null, "/ra swaptest", K.Act),
        new(null, "/ra recharge", K.Act),
        new(null, "/ra heal", K.Act),
        new(null, "/ra stam", K.Act),
        new(null, "/ra mana", K.Act),
        new(null, "/ra hub", K.Act),
        new(null, "/ra quests", K.Act),
        new(null, "/ra pets", K.Act),
        new(null, "/ra huds show", K.Act),
        new(null, "/ra itemhud add Faran Robe", K.Act),
        new(null, "/ra remote toggle", K.Act),
        new(null, "/ra miniremote hide", K.Act),
        new(null, "/ra translate status", K.Act),
        new(null, "/ra tr status", K.Act),
        new(null, "/ra iteminfo", K.Act),
        new(null, "/ra ii", K.Act),
        new(null, "/ra corpseopen", K.Act),
        new(null, "/ra fellow", K.Act),
        new(null, "/ra fellowship leader", K.Act),
        new(null, "/ra nosuchverb", K.Act),
        // ── /ra: output asked for ──
        new("/ra help", null, K.Query),
        new("/ra", null, K.Query),
        new("/ra version", null, K.Query),
        new("/ra server", null, K.Query),
        new("/ra buffs", null, K.Query),
        new("/ra cache", null, K.Query),
        new("/ra cache2", null, K.Query),
        new("/ra attackable", null, K.Query),
        new("/ra mexec 1+1", "/ra mexec", K.Query),
        new("/ra listvars", null, K.Query),
        new("/ra listpvars", null, K.Query),
        new("/ra listgvars", null, K.Query),
        new("/ra dumpprops", null, K.Query),
        new("/ra propertydump", null, K.Query),
        new("/ra wielded", null, K.Query),
        new("/ra scan", null, K.Query),
        new("/ra buildinfo", null, K.Query),
        new("/ra navdebug", null, K.Query),
        new("/ra raycast", null, K.Query),
        new("/ra lostest", null, K.Query),
        new("/ra landtest", null, K.Query),
        new("/ra rayland", null, K.Query),
        new("/ra corpseinfo", null, K.Query),
        new("/ra corpsecheck", null, K.Query),
        new("/ra fellowinfo", null, K.Query),
        new("/ra dumpinv", null, K.Query),
        new("/ra combat", null, K.Query),
        new("/ra why", null, K.Query),
        new("/ra ghosts", null, K.Query),
        new("/ra ghost", null, K.Query),
        new("/ra navstate", null, K.Query),
        new("/ra salvstate", null, K.Query),
        new("/ra mapdump", null, K.Query),
        new("/ra busyinfo", null, K.Query),
        new("/ra pace", null, K.Query),
        new("/ra owncorpse", null, K.Query),
        new("/ra mycorpse", null, K.Query),
        new("/ra vtkeys", null, K.Query),
        new("/ra discord", null, K.Query),
        new("/ra items", null, K.Query),
        new("/ra trace list", "/ra trace nosuchcategory", K.Query),
        new("/ra logs", null, K.Query),
        new("/ra debug", null, K.Query),
        new("/ra loot", null, K.Query),
        new("/ra hazard", null, K.Query),
        new("/ra hazard list", null, K.Query),
        new("/ra hazard texture list", null, K.Query),
        new("/ra groundloot", null, K.Query),
        new("/ra trade state", null, K.Query),
        new("/ra vtankyield", null, K.Query),
        new("/ra navtrail", null, K.Query),
        new("/ra metamgr status", null, K.Elsewhere), // answered on the next tick
        // ── /ra: answered by the engine or another plugin ──
        new("/ra lua run Hello", null, K.Elsewhere),
        new("/ra quit", null, K.Elsewhere),
        new("/ra exit", null, K.Elsewhere),
        new("/ra videopatch status", null, K.Elsewhere),
        new("/ra lootthis", null, K.Elsewhere),       // answered on the loot tick (DeferredAnswers)
        new("/ra metamgr on", null, K.Elsewhere),     // answered on the next tick (DeferredAnswers)

        // ── /mt ──
        new("/mt opt set AutoVendor.Enabled true", "/mt opt set AutoVendor.Enabled", K.Act),
        new("/mt opt set enablecombat 0", "/mt opt", K.Act),
        new("/mt opt remember enablecombat", "/mt opt remember", K.Act),
        new("/mt opt restore enablecombat", "/mt opt restore NoSuchOption", K.Act, Setup: "/mt opt remember enablecombat"),
        new("/mt opt get enablecombat", "/mt opt get NoSuchOption", K.Query),
        new("/mt opt list", null, K.Query),
        new("/mt config", null, K.Query),
        new("/mt combatstate melee", "/mt combatstate banana", K.Act),
        new("/mt face 90", "/mt face banana", K.Act),
        new("/mt cast 1234", "/mt cast No Such Spell Ever", K.Act),
        new("/mt castp 1234", "/mt castp", K.Act),
        new("/mt use Faran Robe", "/mt use", K.Act),
        new("/mt usep Robe", "/mt usep", K.Act),
        new("/mt usei Faran Robe", null, K.Act),
        new("/mt useip Robe", null, K.Act),
        new("/mt usel Town Crier", null, K.Act),
        new("/mt uselp Crier", null, K.Act),
        new(null, "/mt use closestvendor", K.Act),
        new("/mt select Faran Robe", "/mt select Nothing Here", K.Act),
        new("/mt selectp Robe", null, K.Act),
        new("/mt give Faran Robe to Town Crier", "/mt give Faran Robe", K.Act),
        new("/mt givep Robe to Crier", "/mt givep Nothing to Crier", K.Act),
        new("/mt loot Gem", "/mt loot", K.Act),
        new("/mt lootp Gem", "/mt lootp", K.Act),
        new("/mt drop Faran Robe", "/mt drop Nothing Here", K.Act),
        new("/mt dropp Robe", "/mt dropp", K.Act),
        new("/mt equip Sharp Sword", "/mt equip Nothing Here", K.Act),
        new("/mt equipp Sword", "/mt equipp", K.Act),
        new("/mt dequip Worn Cloak", "/mt dequip Nothing Here", K.Query),
        new("/mt dequipp Cloak", "/mt dequipp", K.Query),
        new("/mt fellow open", "/mt fellow create", K.Elsewhere),
        new("/mt fellow close", "/mt fellow recruit", K.Elsewhere),
        new("/mt fellow disband", "/mt fellow banana", K.Elsewhere),
        new("/mt fellow quit", "/mt fellow", K.Elsewhere),
        new("/mt fellow create Hunters", null, K.Elsewhere),
        new("/mt fellow recruit Town Crier", null, K.Elsewhere),
        new("/mt logout", null, K.Elsewhere),
        new("/mt logoff", null, K.Elsewhere),
        new("/mt quit", null, K.Elsewhere),
        new("/mt exit", null, K.Elsewhere),
        new("/mt send /say hi", "/mt send", K.Elsewhere),
        new("/mt send enter", "/mt send banana", K.Act),
        new("/mt send msg hello", "/mt send msg", K.Act),
        new("/mt send cntrl+a", null, K.Act),
        new("/mt jump 100", null, K.Act),
        new("/mt sjumpw 100", null, K.Act),
        new("/mt trade decline", "/mt trade", K.Act),
        new("/mt trade reset", "/mt trade add", K.Act),
        new("/mt trade end", "/mt trade banana", K.Act),
        new("/mt trade add Faran Robe", "/mt trade add Nothing Here", K.Act),
        new(null, "/mt trade accept", K.Act),
        new("/mt vendor addsell Faran Robe", "/mt vendor", K.Act),
        new("/mt vendor clearbuy", "/mt vendor addsell Nothing Here", K.Act),
        new("/mt vendor clearsell", "/mt vendor banana", K.Act),
        new(null, "/mt vendor sell", K.Act),
        new(null, "/mt vendor buy", K.Act),
        new("/mt autopack", null, K.Act),
        new("/mt click ok", "/mt click", K.Act),
        new("/mt click 10 20", "/mt click banana", K.Act),
        new("/mt movement w 0", "/mt movement", K.Act),
        new("/mt strafe left 0", "/mt strafe up 5", K.Act),
        new("/mt client minimize", "/mt client banana", K.Act),
        new("/mt client restore", null, K.Act),
        new("/mt client frame", null, K.Query),
        new("/mt get xy", "/mt get banana", K.Query),
        new("/mt cursor", null, K.Query),
        new("/mt test", null, K.Query),
        new(null, "/mt nosuchverb", K.Act),
        new(null, "/mt", K.Act),

        // ── /ub ──
        new("/ub opt set AutoVendor.Enabled true", "/ub opt set AutoVendor.Enabled", K.Act),
        new("/ub opt toggle EnableLooting", "/ub opt toggle NoSuchOption", K.Act),
        new("/ub opt get enablecombat", null, K.Query),
        new("/ub opt list", null, K.Query),
        new("/ub setvar[cr,1]", null, K.Act),
        new("/ub jumpw", "/ub jumpq", K.Act),
        new("/ub simplejump 100", null, K.Act),
        new("/ub give Faran Robe to Town Crier", "/ub give Nothing Here to Town Crier", K.Act),
        new("/ub givep Robe to Crier", null, K.Act),
        new("/ub give 1 Faran Robe to Crier", null, K.Act),
        new("/ub follow off", null, K.Act),
        new("/ub followp Crier", null, K.Act),
        new("/ub selecti Faran Robe", "/ub selecti Nothing Here", K.Act),
        new("/ub selectlp Crier", null, K.Act),
        new("/ub myquests", null, K.Act),
        new("/ub clearbugged", null, K.Act),
        new("/ub mexec 1+1", "/ub mexec", K.Query),
        new("/ub listvars", null, K.Query),
        new("/ub listpvars", null, K.Query),
        new("/ub listgvars", null, K.Query),
        new("/ub propertydump", null, K.Query),
        new(null, "/ub ig NoSuchProfileHere to Crier", K.Act),
        new("/ub delay 1000 /ra stop", "/ub delay banana", K.Act),
        new("/ub count item Faran Robe", "/ub count", K.Query),
        new("/ub count player 50", "/ub count player banana", K.Query),
        new("/ub date", null, K.Query),
        new("/ub dateutc yyyy", null, K.Query),
        new("/ub id", null, K.Query),
        new("/ub pos", null, K.Query),
        new("/ub vitae", null, K.Elsewhere),
        new(null, "/ub portal Nowhere At All", K.Act),
        new(null, "/ub portalp", K.Act),
        new(null, "/ub closestportal", K.Act),
        new(null, "/ub playsound NoSuchSound.wav", K.Act),
        new("/ub setmotion Forward 0", "/ub setmotion", K.Act),
        new("/ub getmotion", null, K.Query),
        new("/ub clearmotion", null, K.Act),
        new("/ub autostack", null, K.Act),
        new("/ub autocram", null, K.Act),
        new("/ub help", null, K.Query),
        new(null, "/ub dumpskills", K.Act),
        new("/ub quests check SomeFlag", "/ub quests", K.Elsewhere),
        new(null, "/ub translateroute", K.Act),
        new("/ub wear Sharp Sword", "/ub wear Nothing Here", K.Act),
        new("/ub wearp Sword", "/ub wear", K.Act),
        new("/ub getxy", null, K.Query),
        new("/ub stfl 0", "/ub stfl", K.Act),
        new("/ub stfr 0", null, K.Act),
        new("/ub debug off", null, K.Act),
        new(null, "/ub close corpse", K.Act),
        new("/ub globalframelimit 45", "/ub globalframelimit banana", K.Act),
        new("/ub bgframelimit 20", "/ub bgframelimit", K.Act),
        new("/ub fellow dismiss Storm", "/ub fellow dismiss", K.Elsewhere),
        new("/ub fellow leaderp Storm", null, K.Elsewhere),
        new("/ub fellow status", null, K.Query),
        new("/ub arrow point 42.1N, 33.6E", null, K.Elsewhere),
        new("/ub navgoto -12.5 40.25", "/ub navgoto banana", K.Elsewhere),
        new("/ub packsort", null, K.Elsewhere),
        new("/ub packsortshow", null, K.Elsewhere),
        new("/ub bc /say hi", null, K.Elsewhere),
        new("/ub netclients", null, K.Elsewhere),
        new("/ub videopatch status", null, K.Elsewhere),
        new("/ub bank status", null, K.Elsewhere),     // ub-Rythai answers its own verbs
        new("/ub combat on", null, K.Act),
        new("/ub sloot off", null, K.Act),
        new("/ub meta state Hunt", null, K.Act),
        new("/ub meta status", null, K.Query),
        new("/ub lps", null, K.Query),
        new(null, "/ub miniremote show", K.Act),
        new(null, "/ub autovendor", K.Act),
        new(null, "/ub vendor buyall", K.Act),
        new(null, "/ub autotrade", K.Act),
        new("/ub soak on", null, K.Ignored),
        new("/ub window save", null, K.Ignored),
        new("/ub arrow face", null, K.Ignored),
        new("/ub packsortreload", null, K.Ignored),
        new("/ub qblist", null, K.Ignored),
        new("/ub meta banana", null, K.Ignored),
        new("/ub usep Robe", "/ub usep", K.Act),
        new("/ub face 90", "/ub face", K.Act),
        new("/ub combatstate peace", "/ub combatstate", K.Act),
        new("/ub cast 1234", "/ub cast", K.Act),
        new("/ub equip Sharp Sword", "/ub equip", K.Act),
        new("/ub drop Faran Robe", "/ub drop", K.Act),
        new("/ub select Faran Robe", "/ub select", K.Act),
        new("/ub loot Gem", "/ub loot", K.Act),
        new("/ub fellow open", "/ub fellow create", K.Elsewhere),
        new("/ub logout", null, K.Elsewhere),
        new("/ub quit", null, K.Elsewhere),
        new(null, "/ub nosuchverb", K.Act),

        // ── /vt ──
        new("/vt opt set enablecombat true", "/vt opt set enablecombat", K.Act),
        new("/vt opt set NoSuchVtOption 5", "/vt opt", K.Act),
        new("/vt opt get enablecombat", null, K.Query),
        new("/vt opt list", null, K.Query),
        new("/vt stop", null, K.Act),
        new("/vt start", null, K.Act),
        new("/vt e", null, K.Act),
        new("/vt jump", null, K.Act),
        new("/vt tapjump", null, K.Act),
        new("/vt equipitemsfor Drudge Skulker", "/vt equipitemsfor", K.Act),
        new(null, "/vt loot load NoSuchProfileHere", K.Act),
        new(null, "/vt meta", K.Act),
        new(null, "/vt nav", K.Act),
        new("/vt setmetastate Hunt", "/vt setmetastate", K.Act),
        new("/vt attackdistance 0.1", null, K.Act),
        new("/vt setattackbar 0.5", "/vt setattackbar banana", K.Act),
        new("/vt reverseroute", null, K.Act),
        new("/vt mexec 1+1", null, K.Query),
        new("/vt clearbusy", null, K.Act),
        new("/vt propertydump", null, K.Query),
        new(null, "/vt testitem", K.Act),
        new(null, "/vt forcebuff", K.Act),
        new(null, "/vt cancelforcebuff", K.Act),
        new(null, "/vt cancelbuff", K.Act),
        new("/vt echo hello", null, K.Query),
        new("/vt settings banana", null, K.Ignored),
        new("/vt deletemonster", null, K.Ignored),
        new(null, "/vt nosuchverb", K.Act),
        new(null, "/vt", K.Act),
    };

    // ── The rig: a logged-in RynthAi with a meta manager, combat, a jumper and the fakes ──

    private sealed class Rig : IDisposable
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();
        public readonly MetaManager M;
        public readonly List<(uint Msg, uint W, uint L)> Posted = new();

        public Rig()
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = FakeHost.SalvageCalls = FakeHost.GiveCalls = FakeHost.CombatModeCalls = true;
            FakeHost.PluginCommandCalls = FakeHost.CloseContainerCalls = FakeHost.TradeVendorCalls = true;
            FakeHost.BusyCalls = FakeHost.StopCalls = FakeHost.SetMotionCalls = FakeHost.TapJumpCalls = true;
            FakeHost.Names[Player] = "Tester";
            FakeHost.PlayerIdValue = Player;
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
            foreach (var (id, name) in new[] { (Robe, "Faran Robe"), (Sword, "Sharp Sword"), (Cloak, "Worn Cloak") })
            {
                FakeHost.Names[id] = name;
                FakeHost.Containers[id] = Player;
                FakeHost.ItemTypes[id] = 0x2;
            }
            FakeHost.Wielded[Cloak] = 0x0400;
            foreach (var (id, name, y) in new[] { (Crier, "Town Crier", 98f), (Drudge, "Drudge Skulker", 99f) })
            {
                FakeHost.Names[id] = name;
                FakeHost.Positions[id] = (Lb | 0x1C, 96f, y, 0f);
                FakeHost.ItemTypes[id] = 0x10;
            }
            var host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);

            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            foreach (uint id in new[] { Robe, Sword, Cloak }) cache.OnCreateObject(id);
            cache.Tick();
            foreach (uint id in new[] { Crier, Drudge }) _ = cache[unchecked((int)id)];
            Set("_objectCache", cache);
            Set("_playerId", Player);
            Set("_currentTargetId", Drudge);

            M = new MetaManager(S, host, new PlayerVitalsCache());
            M.SetPlayerId(Player);
            M.SetObjectCache(cache);
            M.SetMtCommandHandler(line => (bool)Call("HandleMetaChatCommand", line)!);
            M.SetRaCommandHandler(line => Call("HandleRaCommand", line));
            M.Reply = P.Reply;
            Set("_metaManager", M);

            var combat = new CombatManager(host, S, cache);
            combat.SetPlayerId(Player);
            combat.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            Set("_combatManager", combat);
            Set("_fellowshipTracker", new FellowshipTracker());
            Set("_jumper", new Jumper(host, S, text => P.Reply.Fail(text)));
            Set("_initialized", true);
            Set("_loginComplete", true);

            S.AllowMetaKeystrokes = true;   // the key verbs act (into the test sink) instead of refusing
            S.MonsterRules.Add(new MonsterRule { Name = "Default" });
            S.CurrentRoute.Points.Add(new NavPoint { NS = 1.0, EW = 1.0 });
            S.CurrentRoute.Points.Add(new NavPoint { NS = 1.1, EW = 1.1 });
            GameInput.TestSink = (m, w, l) => Posted.Add((m, w, l));
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Get(string f) => typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        public object? Call(string method, params object[] args) => typeof(RynthAiPlugin).GetMethod(method, Any)!.Invoke(P, args);

        /// <summary>A line typed in the chat bar; the chat lines it wrote.</summary>
        public List<(string Text, int Type)> Typed(string line) => Lines(() => { int eat = 0; P.OnChatBarEnter(line, ref eat); });

        /// <summary>The same line run by a meta (RunAsMeta, as HandleMetaChatCommand does); the chat lines it wrote.</summary>
        public List<(string Text, int Type)> Meta(string line) => Lines(() =>
        {
            var run = typeof(RynthAiPlugin).GetMethod("RunAsMeta", Any)!.MakeGenericMethod(typeof(bool));
            run.Invoke(P, new object[] { (Func<bool>)(() => { int eat = 0; P.OnChatBarEnter(line, ref eat); return true; }) });
        });

        public static List<(string Text, int Type)> Lines(Action act)
        {
            int before = FakeHost.Chat.Count;
            act();
            var lines = new List<(string, int)>();
            for (int i = before; i < FakeHost.Chat.Count; i++)
                lines.Add((FakeHost.Chat[i], i < FakeHost.ChatTypes.Count ? FakeHost.ChatTypes[i] : -1));
            return lines;
        }

        public void Dispose()
        {
            GameInput.Shutdown();
            GameInput.TestSink = null;
            RynthLog.DebugToChat = false;
        }
    }

    /// <summary>Set RYNTH_SHOW_REPLIES=1 to print every typed line's answer (to read them over).</summary>
    private static readonly bool ShowReplies = Environment.GetEnvironmentVariable("RYNTH_SHOW_REPLIES") == "1";

    private static string Show(List<(string Text, int Type)> lines)
        => lines.Count == 0 ? "(nothing)" : string.Join(" | ", lines.Select(l => $"[{l.Type}] {l.Text}"));

    /// <summary>A reply that is the catch-all line or a caught exception: the command has no answer of its own.</summary>
    private static string? Bad(List<(string Text, int Type)> lines)
    {
        foreach (var (t, _) in lines)
        {
            if (t.StartsWith(CommandReply.RanPrefix, StringComparison.Ordinal)) return "only the catch-all 'Ran:' line";
            if (t.Contains(": failed (", StringComparison.Ordinal)) return "threw: " + t;
        }
        return null;
    }

    private static void Report(List<string> problems, string what)
    {
        foreach (string p in problems) Console.WriteLine("        " + p);
        Check.Eq(problems.Count, 0, what);
    }

    // ── The table ─────────────────────────────────────────────────────────────

    private static void TypedAlwaysAnswers()
    {
        var problems = new List<string>();
        foreach (var row in Rows)
        {
            foreach (string? line in new[] { row.Valid, row.Invalid })
            {
                if (line == null) continue;
                using var r = new Rig();
                if (row.Setup != null && line == row.Valid) r.Typed(row.Setup);
                int handedOn = FakeHost.ChatCommands.Count + FakeHost.PluginCommands.Count;
                var lines = r.Typed(line);
                if (ShowReplies) Console.WriteLine($"        {line}  =>  {Show(lines)}");
                bool elsewhere = row.Kind == K.Elsewhere && line == row.Valid;
                if (Bad(lines) is string bad) problems.Add($"{line}: {bad}");
                else if (elsewhere)
                {
                    bool passedOn = FakeHost.ChatCommands.Count + FakeHost.PluginCommands.Count > handedOn;
                    bool deferred = line is "/ra lootthis" or "/ub bank status" || line.StartsWith("/ra metamgr", StringComparison.Ordinal);
                    if (!passedOn && lines.Count == 0 && !deferred) problems.Add($"{line}: nothing said and nothing handed on");
                }
                else if (lines.Count == 0) problems.Add($"{line}: said nothing");
            }
        }
        Report(problems, "every typed command answers with a line of its own");
    }

    private static void TypedWrongFormsFail()
    {
        var problems = new List<string>();
        foreach (var row in Rows)
        {
            if (row.Invalid == null) continue;
            using var r = new Rig();
            var lines = r.Typed(row.Invalid);
            if (!lines.Any(l => l.Type == CommandReply.FailColor))
                problems.Add($"{row.Invalid}: no failure line: {Show(lines)}");
        }
        Report(problems, "a wrong form answers with a failure line");

        // Each typed failure answers every time (no once-a-minute for the player).
        using var again = new Rig();
        int first = again.Typed("/ra power banana").Count;
        int second = again.Typed("/ra power banana").Count;
        Check.True(first >= 1 && second == first, $"typed twice, answered twice ({first}, {second})");

        // A key verb with "Allow metas to send keystrokes" off: typed, refused every time; a meta, once.
        using var keys = new Rig();
        keys.S.AllowMetaKeystrokes = false;
        int t1 = keys.Typed("/mt send enter").Count(l => l.Type == CommandReply.FailColor);
        int t2 = keys.Typed("/mt send enter").Count(l => l.Type == CommandReply.FailColor);
        int m1 = keys.Meta("/mt send enter").Count;
        int m2 = keys.Meta("/mt send enter").Count;
        Check.True(t1 == 1 && t2 == 1 && m1 == 1 && m2 == 0, $"keys off: typed {t1}+{t2}, meta {m1}+{m2}");
        Check.Eq(keys.Posted.Count, 0, "and no key went out");
    }

    private static void MetaQuietOnSuccess()
    {
        var problems = new List<string>();
        foreach (var row in Rows)
        {
            if (row.Valid != null && row.Kind is K.Act or K.Elsewhere)
            {
                using var r = new Rig();
                if (row.Setup != null) r.Typed(row.Setup);
                var lines = r.Meta(row.Valid);
                if (lines.Count > 0) problems.Add($"{row.Valid} (meta, works): said {Show(lines)}");
                if (lines.Any(l => l.Text.StartsWith(CommandReply.RanPrefix, StringComparison.Ordinal)))
                    problems.Add($"{row.Valid} (meta): the catch-all line");
            }
            if (row.Valid != null && row.Kind == K.Ignored)
            {
                using var r = new Rig();
                var once = r.Meta(row.Valid);
                var twice = r.Meta(row.Valid);
                if (once.Count > 1 || twice.Count > 0) problems.Add($"{row.Valid} (meta, ignored): {Show(once)} then {Show(twice)}");
            }
            if (row.Invalid != null && row.Kind != K.Ignored)
            {
                using var r = new Rig();
                var lines = r.Meta(row.Invalid);
                if (!lines.Any(l => l.Type == CommandReply.FailColor))
                    problems.Add($"{row.Invalid} (meta, wrong): no failure line: {Show(lines)}");
                var repeat = r.Meta(row.Invalid);
                if (repeat.Any(l => l.Type == CommandReply.FailColor))
                    problems.Add($"{row.Invalid} (meta, wrong, again within a minute): said again: {Show(repeat)}");
            }
        }
        Report(problems, "a meta's command: quiet when it works, its error once a minute");

        // The quiet success is in the log.
        using var logged = new Rig();
        logged.Meta("/ra power 80");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Attack power set to 80%")), "a meta's quiet success is logged");
        Check.Eq(logged.S.MeleeAttackPower, 80, "and it worked");
    }

    private static void UbOptSetSaysValue()
    {
        using var r = new Rig();
        Check.False(r.S.AutoVendorEnabled, "AutoVendor starts off");
        var lines = r.Typed("/ub opt set AutoVendor.Enabled true");
        Check.True(r.S.AutoVendorEnabled, "the setting changed");
        Check.Eq(Show(lines), "[1] [RynthAi] AutoVendor.Enabled = True", "one line: the option and its value now");

        lines = r.Meta("/ub opt set AutoVendor.Enabled false");
        Check.False(r.S.AutoVendorEnabled, "a meta's set works");
        Check.Eq(lines.Count, 0, "and says nothing in chat");
        Check.True(FakeHost.Logs.Any(l => l == "[RynthAi] AutoVendor.Enabled = False"), "the log has it");

        // The real meta route (MetaManager's chat-command action -> HandleMetaChatCommand) is quiet too.
        lines = Rig.Lines(() => r.Call("HandleMetaChatCommand", "/ub opt set AutoVendor.Enabled true"));
        Check.True(r.S.AutoVendorEnabled && lines.Count == 0, "HandleMetaChatCommand: set, quiet");
        lines = Rig.Lines(() => r.M.TryHandleVtCommand("/vt opt set enablecombat false"));
        Check.Eq(lines.Count, 1, "MetaManager's /vt outside a meta (typed): one line");
        Check.Eq(lines[0].Text, "[RynthAi] enablecombat = False", "the option and its value now");

        // An option RynthAi doesn't map is kept for metas, and says so.
        lines = r.Typed("/ub opt set NoSuchOption 5");
        Check.True(lines.Count == 1 && lines[0].Text.Contains("NoSuchOption = 5") && lines[0].Text.Contains("kept only for metas"),
            "an unmapped option: its value, and that it is only kept for metas: " + Show(lines));
    }

    private static void DeferredAnswers()
    {
        // /ra lootthis answers on the loot tick, as the line that asked: typed in chat ...
        using (var r = new Rig())
        {
            r.Typed("/ra lootthis");
            var lines = Rig.Lines(() => r.Call("TakeLootThisRequest", r.S));
            Check.True(lines.Count == 1 && lines[0].Text.Contains("LootThisCont"), "typed lootthis: the tick answers: " + Show(lines));
        }

        // /ra metamgr is applied on the next tick; a meta's answer stays quiet there, a typed one shows.
        foreach (bool meta in new[] { false, true })
        {
            using var r = new Rig();
            if (meta) r.Meta("/ra metamgr on"); else r.Typed("/ra metamgr on");
            var queue = (System.Collections.Concurrent.ConcurrentQueue<(string[] Parts, bool FromScript)>)r.Get("_metaMgrCommands")!;
            var lines = Rig.Lines(() =>
            {
                while (queue.TryDequeue(out var q))
                    r.P.Reply.RunQueued(q.FromScript, () => r.Call("HandleMetaMgrCommand", new object[] { q.Parts }));
            });
            Check.Eq(lines.Count, meta ? 0 : 1, meta ? "a meta's metamgr on: quiet on the tick" : "typed metamgr on: answered on the tick");
        }
    }

    // ── The helper itself ───────────────────────────────────────────────────────

    private static void HelperRules()
    {
        var chat = new List<(string Text, int Type)>();
        var log = new List<string>();
        bool meta = false;
        long now = 1_000_000;
        var reply = new CommandReply((t, c) => chat.Add((t, c)), log.Add, () => meta) { Clock = () => now };

        reply.Run("/ra quiet", () => { });
        Check.True(chat.Count == 1 && chat[0].Text == CommandReply.RanPrefix + "/ra quiet", "typed, no answer: the catch-all line");
        Check.True(log.Any(l => l.Contains("gave no reply of its own")), "and the log names the command");

        chat.Clear();
        reply.Run("/ra ok", () => reply.Ok("done"));
        Check.True(chat.Count == 1 && chat[0] == ("done", CommandReply.OkColor), "typed Ok: one line, the Ok colour");
        chat.Clear();
        reply.Run("/ra fail", () => reply.Fail("nope"));
        Check.True(chat.Count == 1 && chat[0] == ("nope", CommandReply.FailColor), "typed Fail: one line, the failure colour");
        chat.Clear();
        reply.Run("/ra throws", () => throw new InvalidOperationException("boom"));
        Check.True(chat.Count == 1 && chat[0].Text.Contains("failed (InvalidOperationException: boom)"), "typed, threw: says so, nothing escapes");
        chat.Clear();
        reply.Run("/ra answered", () => reply.Answered());
        Check.Eq(chat.Count, 0, "answered another way: no catch-all line");
        reply.Run("/ra ignored", () => reply.Ignored("not here"));
        Check.True(chat.Count == 1 && chat[0].Type == CommandReply.FailColor, "typed Ignored: a failure line");

        // A meta's lines.
        chat.Clear(); log.Clear();
        meta = true;
        reply.Run("/ra ok", () => reply.Ok("done"));
        Check.Eq(chat.Count, 0, "meta Ok: no chat");
        Check.True(log.Contains("done"), "meta Ok: the log");
        reply.Run("/ra quiet", () => { });
        Check.Eq(chat.Count, 0, "meta, no answer: no catch-all line");
        reply.Ignored("not here");
        Check.Eq(chat.Count, 0, "meta Ignored: log only");
        reply.Fail("nope");
        reply.Fail("nope");
        Check.Eq(chat.Count, 1, "meta Fail: in chat, a repeat within a minute not");
        reply.Fail("other");
        Check.Eq(chat.Count, 2, "a different error is said");
        now += CommandReply.FailRepeatMs;
        reply.Fail("nope");
        Check.Eq(chat.Count, 3, "after a minute the same error is said again");
        Check.Eq(log.Count(l => l == "nope"), 3, "every one is logged");
        bool threw = false;
        try { reply.Run("/ra throws", () => throw new InvalidOperationException("boom")); }
        catch (InvalidOperationException) { threw = true; }
        Check.True(threw, "a meta's exception goes on as before");

        // A script's line (RynthLua, a route's chat waypoint) is a meta's; a queued command keeps its sender.
        chat.Clear();
        meta = false;
        reply.AsScript(() => { reply.Ok("script done"); return true; });
        Check.Eq(chat.Count, 0, "AsScript Ok: quiet");
        reply.RunQueued(true, () => reply.Ok("queued by a meta"));
        Check.Eq(chat.Count, 0, "RunQueued(meta) Ok: quiet");
        reply.RunQueued(false, () => reply.Ok("queued by the player"));
        Check.Eq(chat.Count, 1, "RunQueued(typed) Ok: in chat");
        meta = true;
        reply.RunQueued(false, () => reply.Ok("typed, run while a meta acts"));
        Check.Eq(chat.Count, 2, "the queued sender wins over a meta running now");
    }
}
