using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The difficulty tier (2026-10-21; RynthAi's AwakenedTier read one server's tier itself before):
// RynthAi takes the tier where the player stands and the health percent a tier from the active
// server plugin's "RynthServer.Profile" values ("difficultyTier", "difficultyHealthPercent";
// RynthAelrynth publishes Aelrynth's) and names no server. Without them everything is tier 0, as on
// every server before; with them HP is scaled and learned numbers are kept per tier. The creature
// data folder a server already has (by RynthCore's server id, its name or the world name; the old
// "Aelrynth" folder included) is read where it is.
internal static class DifficultyTierTests
{
    private const uint Player = 0x50000D01;
    private const uint Drudge = 7;
    private const uint Appraised = 0x7A11;   // a wcid no other test's creature data holds
    private const uint Wand = 0x80000401;
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("difficulty tier: without a server plugin's values it is tier 0, nothing is scaled, the panel shows no picker", NoValues);
        r.Add("difficulty tier: a server plugin's tier and percent scale HP and keep learned numbers per tier", WithValues);
        r.Add("difficulty tier: the creature data folder a server already has is kept (the old 'Aelrynth' one included)", CreatureFolder);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly MonsterDamageStore Store = new();
        public readonly CreatureProfileStore Creatures = new();
        private readonly RynthCoreHost _host;

        public Bot(RynthCoreHost host, string name)
        {
            _host = host;
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            string folder = Path.Combine(Program.TempRoot, "difficulty-tier", name);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            Store.SetCharacter(folder);
            Set("_damageStore", Store);
            Set("_creatureStore", Creatures);
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);

        /// <summary>A fresh read of the server's values (the once-a-second refresh, now), then RynthAi's tick of it.</summary>
        public void Tick()
        {
            P.DifficultyTier.Refresh(_host, force: true);
            typeof(RynthAiPlugin).GetMethod("TickDifficultyTier", Any)!.Invoke(P, null);
        }

        public string TierJson() => P.BuildDifficultyTierJson();

        public (int Hp, string Source) Detail(uint wcid)
        {
            using var doc = JsonDocument.Parse(P.BuildMonsterDetailJson(wcid));
            return (doc.RootElement.GetProperty("hp").GetInt32(), doc.RootElement.GetProperty("hpSrc").GetString() ?? "");
        }
    }

    private static Bot NewBot(string name, string world, bool serverState)
    {
        FakeHost.Reset();
        FakeServerState.Reset();
        FakeHost.WorldName = world;
        if (serverState) FakeServerState.Install();
        return new Bot(FakeHost.Create(Player), name);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    private static void NoValues()
    {
        // No server list at all (an engine without GetServerStateJson): the world name keys the data.
        var bot = NewBot("none", "Some World", serverState: false);
        bot.Store.RecordKill(Wand, Drudge, "Drudge Skulker", "Fire", 7, 2, 900);
        bot.Tick();
        Check.False(bot.P.DifficultyTier.Active, "no server plugin reports a tier");
        Check.Eq((bot.P.DifficultyTier.Current, bot.P.DifficultyTier.HealthPercentPerTier), (0, 0.0), "tier 0, no percent");
        Check.Eq(bot.Store.Difficulty, 0, "the damage store records at tier 0");
        Check.Eq(bot.TierJson(), "{\"show\":false}", "the Damage panel shows no tier picker");
        Check.Eq(bot.Creatures.ServerKey, "Some_World", "creature data under the world name, as before");
        bot.P.SetMonsterHp(Drudge, 1000);
        Check.Eq(bot.Store.GetManualHp(Drudge), 1000.0, "a typed HP is kept as typed");
        Check.Eq(bot.Detail(Drudge), (1000, "set by you"), "and shown as typed");
        bot.P.SetDamageViewTier(5);
        Check.Eq(bot.P.DamageViewDifficulty, 0, "a picked tier means nothing without tiers");
        Check.Eq(bot.Detail(Drudge).Hp, 1000, "the HP stays tier 0's");
        Check.Near(bot.Store.GetLearnedHp(Drudge), 900, 0.01, "the learned HP is tier 0's");

        // A server plugin that is active but publishes no tier (any other server): the same.
        var other = NewBot("other", "Other World", serverState: true);
        FakeServerState.StateJson = FakeServerState.State("other", "Other", "RynthOther", "Other World");
        FakeServerState.ProfileJson = FakeServerState.Profile("other", ("voidDotDurationScale", 1.35));
        other.Tick();
        Check.False(other.P.DifficultyTier.Active, "another value isn't a tier");
        Check.Eq(other.TierJson(), "{\"show\":false}", "no picker");
        Check.Eq(other.Store.Difficulty, 0, "tier 0");
        Check.True(FakeServerState.Queries.Any(q => q.Plugin == "RynthOther" && q.Iface == RynthServerProfileApiV1.InterfaceName),
                   "the values come from the active server's owner plugin");

        // Pure checks of the scale and the reading.
        Check.Eq(DifficultyTier.HealthScale(3, 5), 1.15, "tier 3 at 5% a tier: x1.15");
        Check.Eq((DifficultyTier.HealthScale(0, 5), DifficultyTier.HealthScale(3, 0), DifficultyTier.HealthScale(-1, 5)), (1.0, 1.0, 1.0),
                 "tier 0, no percent, or a negative tier: x1");
        Check.False(DifficultyTier.Read("", "x").Active || DifficultyTier.Read("{broken", "x").Active
                    || DifficultyTier.Read("{\"values\":{\"difficultyTier\":\"3\"}}", "x").Active, "no profile, broken JSON, text: no tier");
        var odd = DifficultyTier.Read("{\"values\":{\"difficultyTier\":-4,\"difficultyHealthPercent\":5000}}", "x");
        Check.True(odd.Active && odd.Tier == 0 && odd.HealthPercent == 0, "a negative tier reads 0, an absurd percent 0");
        var round = DifficultyTier.Read("{\"values\":{\"difficultyTier\":2.6,\"difficultyHealthPercent\":2.5}}", "x");
        Check.True(round.Tier == 3 && round.HealthPercent == 2.5, "a tier is a whole number; the percent may not be");
    }

    private static void WithValues()
    {
        var bot = NewBot("values", "Test World", serverState: true);
        FakeServerState.StateJson = FakeServerState.State("testsrv", "Test Server", "RynthTestSrv", "Test World");
        FakeServerState.ProfileJson = FakeServerState.Profile("testsrv", ("difficultyTier", 3), ("difficultyHealthPercent", 5));
        bot.Store.RecordKill(Wand, Drudge, "Drudge Skulker", "Fire", 7, 2, 900);   // tier 0, before the tier is known
        bot.Tick();
        DifficultyTier t = bot.P.DifficultyTier;
        Check.True(t.Active, "the server plugin reports a tier");
        Check.Eq((t.Current, t.HealthPercentPerTier, t.Source), (3, 5.0, "RynthTestSrv"), "tier 3, +5% a tier, from its plugin");
        Check.Eq(bot.Store.Difficulty, 3, "the damage store records under tier 3");
        Check.Eq(bot.TierJson(), "{\"show\":true,\"current\":3,\"view\":3,\"following\":true,\"percent\":5,\"tiers\":[0,3]}",
                 "the Damage panel's picker (the export's JSON shape)");
        Check.Eq(bot.Creatures.ServerKey, "testsrv", "a new server's creature data goes under its RynthCore id");

        // HP: typed at the shown tier, kept as tier 0's, shown scaled.
        bot.P.SetMonsterHp(Drudge, 1150);
        Check.Near(bot.Store.GetManualHp(Drudge), 1000, 1e-9, "1150 typed at tier 3 is kept as tier 0's 1000");
        var (hp3, src3) = bot.Detail(Drudge);
        Check.True(hp3 == 1150 && src3.Contains("+15%"), $"shown at tier 3 as 1150, +15% ({src3})");
        bot.P.SetDamageViewTier(0);
        Check.Eq(bot.TierJson(), "{\"show\":true,\"current\":3,\"view\":0,\"following\":false,\"percent\":5,\"tiers\":[0,3]}", "tier 0 picked");
        Check.Eq(bot.Detail(Drudge), (1000, "set by you"), "tier 0 shows the value itself");
        bot.P.SetDamageViewTier(-1);

        // Learned numbers per tier.
        bot.Store.RecordKill(Wand, Drudge, "Drudge Skulker", "Fire", 7, 3, 1350);
        Check.Near(bot.Store.GetLearnedHp(Drudge, 3), 1350, 0.01, "tier 3 learned its own HP");
        Check.Near(bot.Store.GetLearnedHp(Drudge, 0), 900, 0.01, "tier 0's is untouched");
        Check.Eq(string.Join(",", bot.Store.KnownDifficulties()), "3", "tier 3 is known");
        bot.Creatures.Upsert(new CreatureProfile { Name = "Scaled Golem", Wcid = Appraised, MaxHealth = 1380 }, t.Current);
        Check.True(bot.Creatures.TryGetByWcid(Appraised, out var prof) && prof!.MaxHealth == 0 && bot.Creatures.MaxHealthAt(prof, 3) == 1380,
                   "an appraisal at tier 3 is filed under tier 3, not as tier 0's health");

        // The server moves the player to tier 0, then a percent-less tier, then publishes none.
        FakeServerState.ProfileJson = FakeServerState.Profile("testsrv", ("difficultyTier", 0), ("difficultyHealthPercent", 5));
        bot.Tick();
        Check.True(t.Active && t.Current == 0 && bot.Store.Difficulty == 0, "tier 0: still a server with tiers, records at tier 0");
        Check.Eq(bot.TierJson(), "{\"show\":true,\"current\":0,\"view\":0,\"following\":true,\"percent\":5,\"tiers\":[0,3]}", "the picker stays");
        FakeServerState.ProfileJson = FakeServerState.Profile("testsrv", ("difficultyTier", 2));
        bot.Tick();
        Check.True(t.Current == 2 && t.HealthPercentPerTier == 0 && t.HealthScale(2) == 1.0, "a tier without a percent: per-tier data, no scaling");
        FakeServerState.ProfileJson = FakeServerState.Profile("testsrv");
        bot.Tick();
        Check.False(t.Active, "no values: no tier");
        Check.Eq((bot.Store.Difficulty, bot.TierJson()), (0, "{\"show\":false}"), "back to tier 0 and no picker");

        // Logout forgets it; the next login reads fresh.
        t.Reset();
        Check.True(!t.Active && t.ServerKey.Length == 0, "logout: nothing carries over");
    }

    private static void CreatureFolder()
    {
        string saved = CreatureProfileStore.Folder;
        string root = Path.Combine(Program.TempRoot, "difficulty-tier-folders");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        try
        {
            CreatureProfileStore.Folder = root;
            void Seed(string folder, uint wcid, uint hp)
            {
                var s = new CreatureProfileStore();
                s.SetServer(folder);
                s.Upsert(new CreatureProfile { Name = "Drudge Skulker", Wcid = wcid, MaxHealth = hp });
                s.SaveIfDirty();
            }
            Seed("Aelrynth", Drudge, 280);            // RynthAi before 2026-10-21: every Aelrynth world name
            Seed("InfiniteLeaftide", Drudge, 300);    // and every other server: the world name
            Check.Eq(CreatureProfileStore.ExistingServerFolder("aelrynth"), "Aelrynth", "found in any case, named as on disk");
            Check.Null(CreatureProfileStore.ExistingServerFolder("coldeve"), "no folder");

            string? Exists(string k) => CreatureProfileStore.ExistingServerFolder(k);
            EngineServerState? S(string json) => EngineServerState.Parse(json);
            Check.Eq(DifficultyTier.ResolveServerKey(S(FakeServerState.State("aelrynth", "Aelrynth", "RynthAelrynth", "Aeshnidae")), "Aeshnidae", Exists),
                     "Aelrynth", "the old Aelrynth folder is kept (found by the server's name; not 'Aeshnidae', not a new 'aelrynth')");
            Check.Eq(DifficultyTier.ResolveServerKey(S(FakeServerState.State("ilt", "Infinite Leaftide", "RynthILT", "InfiniteLeaftide")), "InfiniteLeaftide", Exists),
                     "InfiniteLeaftide", "a world-named folder is kept");
            Check.Eq(DifficultyTier.ResolveServerKey(S(FakeServerState.State("coldeve", "Coldeve", "RynthColdeve", "Coldeve Two")), "Coldeve Two", Exists),
                     "coldeve", "a server without a folder yet: its RynthCore id");
            Check.Eq(DifficultyTier.ResolveServerKey(S(FakeServerState.State("", "", "", "Aeshnidae", ("aelrynth", "Aelrynth", true, "RynthAelrynth"))), "Aeshnidae", Exists),
                     "Aelrynth", "a server detected but turned off for this character keeps its folder");
            Check.Eq(DifficultyTier.ResolveServerKey(S(FakeServerState.State("", "", "", "Some World")), "Some World", Exists),
                     "Some_World", "no server: the world name, as before");
            Check.Eq(DifficultyTier.ResolveServerKey(null, "Some World", Exists), "Some_World", "no server list: the world name, as before");
            Check.Eq(DifficultyTier.ResolveServerKey(null, "", Exists), "", "nothing known yet: no key");

            // Through the plugin: RynthAelrynth's server, the old folder read in place.
            var bot = NewBot("aelrynth-folder", "Aeshnidae", serverState: true);
            FakeServerState.StateJson = FakeServerState.State("aelrynth", "Aelrynth", "RynthAelrynth", "Aeshnidae");
            FakeServerState.ProfileJson = FakeServerState.Profile("aelrynth", ("difficultyTier", 2), ("difficultyHealthPercent", 5));
            bot.Tick();
            Check.Eq(bot.Creatures.ServerKey, "Aelrynth", "the creature store reads the Aelrynth folder");
            Check.True(bot.Creatures.TryGetByWcid(Drudge, out var p) && p!.MaxHealth == 280, "with what was learned there");
            Check.Eq(string.Join(",", Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(n => n)), "Aelrynth,InfiniteLeaftide",
                     "no new folder was made, nothing moved");
            Check.Eq(bot.P.DifficultyTier.Current, 2, "and the tier is the server plugin's");
        }
        finally
        {
            CreatureProfileStore.Folder = saved;
        }
    }
}
