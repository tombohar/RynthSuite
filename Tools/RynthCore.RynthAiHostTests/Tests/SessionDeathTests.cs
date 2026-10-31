using System.IO;
using System.Linq;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The session's death count (SessionStatsTracker: RynthAi's Mini Remote stats, which RynthTracker
// shows; 2026-10-30) against the engine calls it really reads each tick: the player's vitals
// (GetPlayerVitals) and the player's NumDeaths (PropertyInt 43). The count used to wait for the
// player's health in OnUpdateHealth, which the engine never sends for the player (it is a
// target's health), so it stayed 0. Here nothing ever calls OnUpdateHealth, as in the game.
internal static class SessionDeathTests
{
    private const uint Player = 0x50000D01;
    private const uint NumDeaths = 43;
    private const string Tag = "[RynthAi] Mini Remote stats:";

    public static void Register(Runner r)
    {
        r.Add("session stats: a death as the server sends it counts once (vitals, then NumDeaths)", DeathSequence);
        r.Add("session stats: a reload while dead or during a death counts each death once", DeathsAcrossReload);
        r.Add("session stats: deaths count on an engine at API 90", DeathsOnApi90);
    }

    private static string Folder(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "session-deaths", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return dir;
    }

    private static SessionStatsTracker LogIn(RynthCoreHost host, string dir)
    {
        var t = new SessionStatsTracker(host, Tag, dir);
        t.OnLoginComplete();
        t.Tick();
        return t;
    }

    /// <summary>A few ticks with the vitals (and NumDeaths, null = the character has none) as given.</summary>
    private static void Ticks(SessionStatsTracker t, uint health, uint max, int? numDeaths, int ticks = 3)
    {
        FakeHost.Vitals = (health, max, 200, 200, 150, 150);
        if (numDeaths is int n) FakeHost.Ints[(Player, NumDeaths)] = n;
        else FakeHost.Ints.Remove((Player, NumDeaths));
        for (int i = 0; i < ticks; i++) t.Tick();
    }

    private static int Counted() => FakeHost.Logs.Count(l => l.Contains("Death counted"));

    // ACE's order (Player_Death): OnDeath sends the victim notice (no plugin event), then Die sends
    // health 0 and NumDeaths in one packet; after the death animation the corpse and the portal; 3 s
    // into portal space health comes back at 75 %.
    private static void DeathSequence()
    {
        FakeHost.Reset();
        try
        {
            var host = FakeHost.Create(Player);
            FakeHost.Vitals = (0, 0, 0, 0, 0, 0);          // login: 0/0 before the player's vitals
            FakeHost.Ints[(Player, NumDeaths)] = 4;
            var t = LogIn(host, Folder("sequence"));
            Check.True(t.Active, "the session runs after login");

            Ticks(t, 0, 0, 4);
            Ticks(t, 0, 360, 4);                           // the max before the health
            Ticks(t, 360, 360, 4);
            Check.Eq(t.View.Deaths, 0, "login: the 0/0 packet and 0/max before the first reading are no death");

            Ticks(t, 120, 360, 4);                         // a fight
            t.OnKillNotification("You killed the Drudge Skulker!");
            Ticks(t, 30, 360, 4);
            Check.Eq(t.View.Deaths, 0, "a kill notice and low health are no death");

            Ticks(t, 0, 360, 5, ticks: 1);                 // Die(): health 0 and NumDeaths 5, one packet
            Check.Eq(t.View.Deaths, 1, "the death shows at once");
            FakeHost.Portaling = true;                     // dead ~5 s: the corpse, the portal to the lifestone
            Ticks(t, 0, 360, 5, ticks: 150);
            Ticks(t, 270, 360, 5);                         // 3 s into portal space: 75 % health
            FakeHost.Portaling = false;
            Ticks(t, 360, 360, 5);
            Check.Eq(t.View.Deaths, 1, "one death, once, through the portal and the respawn");
            Check.Eq(Counted(), 1, "logged once");

            Ticks(t, 200, 360, 5);
            Ticks(t, 0, 360, 6);
            Ticks(t, 270, 360, 6);
            Check.Eq(t.View.Deaths, 2, "the next death counts");
            Check.Eq(t.View.Kills, 1, "the kill stays one kill");
            t.OnLogout();
        }
        finally { FakeHost.Reset(); }
    }

    // A hot reload is a Shutdown (the save) and a new copy's OnLoginComplete (the restore).
    private static void DeathsAcrossReload()
    {
        FakeHost.Reset();
        try
        {
            // Reloaded while dead: the new copy doesn't count that death again.
            var host = FakeHost.Create(Player);
            string dir = Folder("reload-dead");
            FakeHost.Vitals = (360, 360, 200, 200, 150, 150);
            FakeHost.Ints[(Player, NumDeaths)] = 4;
            var a = LogIn(host, dir);
            Ticks(a, 360, 360, 4);
            Ticks(a, 0, 360, 5);
            Check.Eq(a.View.Deaths, 1, "reload while dead: counted before the reload");
            a.Shutdown();
            var b = LogIn(host, dir);
            Check.True(FakeHost.Logs.Any(l => l.Contains("Session carried over")), "the session carries over");
            Ticks(b, 0, 360, 5);
            Ticks(b, 270, 360, 5);
            Check.Eq(b.View.Deaths, 1, "reload while dead: still one after it");
            Ticks(b, 0, 360, 6);
            Ticks(b, 270, 360, 6);
            Check.Eq(b.View.Deaths, 2, "and the next death counts");
            b.OnLogout();

            // Died while the plugin was unloaded: the saved NumDeaths baseline counts it.
            FakeHost.Reset();
            host = FakeHost.Create(Player);
            dir = Folder("reload-gap");
            FakeHost.Vitals = (360, 360, 200, 200, 150, 150);
            FakeHost.Ints[(Player, NumDeaths)] = 4;
            var c = LogIn(host, dir);
            Ticks(c, 360, 360, 4);
            c.Shutdown();
            FakeHost.Vitals = (0, 360, 200, 200, 150, 150);   // the death lands before the new copy's first tick
            FakeHost.Ints[(Player, NumDeaths)] = 5;
            var d = LogIn(host, dir);
            Ticks(d, 0, 360, 5);
            Ticks(d, 270, 360, 5);
            Check.Eq(d.View.Deaths, 1, "died during the reload: counted once");

            // A real logout ends the session: the next login starts at 0 deaths.
            d.OnLogout();
            var e = LogIn(host, dir);
            Ticks(e, 360, 360, 5);
            Check.Eq(e.View.Deaths, 0, "a new login is a new session");
            e.OnLogout();
        }
        finally { FakeHost.Reset(); }
    }

    // The two calls are older than the v66 baseline: an API 90 engine counts deaths the same way.
    private static void DeathsOnApi90()
    {
        FakeHost.Reset();
        try
        {
            FakeHost.ConfigureApi = (ref RynthCoreApiNative api) => api.Version = 90;
            var host = FakeHost.Create(Player);
            Check.Eq(host.Version, 90u, "the host is at API 90");
            Check.True(host.HasGetPlayerVitals && host.HasGetObjectIntProperty, "both calls there");
            FakeHost.Vitals = (300, 300, 200, 200, 150, 150);
            var t = LogIn(host, Folder("api90"));
            Ticks(t, 300, 300, null);                      // a character that never died: no NumDeaths yet
            Ticks(t, 0, 300, 1);                           // its first death brings the counter
            Ticks(t, 225, 300, 1);
            Check.Eq(t.View.Deaths, 1, "API 90: a new character's first death counts once");
            t.OnLogout();
        }
        finally { FakeHost.Reset(); }
    }
}
