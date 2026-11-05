using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The item count HUD and the Mini Remote's item slots (HudController.RebuildPack) count from the
// client's own pack walk. They read the cache's inventory set, kept up from object events, and after
// 10+ hours of macro it showed 1 healing kit of 4 and 4 mana stones of 6 (report
// R-20261103-125252-251c, 2026-11-03); the loot step's Keep # count already walked the pack.
internal static class ItemHudCountTests
{
    private const uint Player = 0x50000D01;
    private const uint Pack = 0x80004D10;
    private const uint Kit1 = 0x80004D01, Kit2 = 0x80004D02, Kit3 = 0x80004D03, Kit4 = 0x80004D04;
    private const uint Stone1 = 0x80004D05, Stone2 = 0x80004D06;

    public static void Register(Runner r)
    {
        r.Add("item HUD: counts what the pack walk finds, not the cache's event-kept inventory set", CountsThePackWalk);
    }

    private static void CountsThePackWalk()
    {
        FakeHost.Reset();
        FakeHost.ContentsCalls = true;   // GetContainerContents: the client's pack walk
        FakeHost.Names[Pack] = "Pack";
        FakeHost.ItemTypes[Pack] = 0x200;   // Container
        FakeHost.Containers[Pack] = Player;
        foreach (uint k in new[] { Kit1, Kit2, Kit3, Kit4 })
        {
            FakeHost.Names[k] = "Treated Healing Kit";
            FakeHost.Containers[k] = k == Kit4 ? Pack : Player;   // one in a side pack
        }
        foreach (uint s in new[] { Stone1, Stone2 })
        {
            FakeHost.Names[s] = "Major Mana Stone";
            FakeHost.Containers[s] = Player;
        }
        var host = FakeHost.Create(Player);
        // The cache's own set heard of one kit and one stone only (the drift).
        var cache = FakeHost.MakeCache(host, Player, new[] { Kit1, Stone1 });
        Check.Eq(cache.GetInventory().Count(w => w.Name == "Treated Healing Kit"), 1, "the event-kept set is short (the report's case)");

        string dir = Path.Combine(Path.GetTempPath(), "rynthai-hudcount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var hud = new HudController(host, dir, () => cache, null!, () => null, new HudIconCache(host, () => null), () => 0);
            typeof(HudController).GetMethod("RebuildPack", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(hud, null);
            Check.Eq(hud.Pack.CountOf("Treated Healing Kit"), 4L, "all four kits, the side pack's too");
            Check.Eq(hud.Pack.CountOf("Major Mana Stone"), 2L, "both stones");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}