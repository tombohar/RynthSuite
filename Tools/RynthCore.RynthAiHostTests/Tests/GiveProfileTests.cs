using System;
using System.IO;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// actiontrygiveprofile (security review M3, 2026-10-13): a meta someone shared must not read a
// profile from outside ItemGiver\ or hand the inventory to a player, only to an NPC or vendor.
internal static class GiveProfileTests
{
    private const uint Player = 0x50000F01, Imitator = 0x50000F02;
    private const uint Npc = 0x80281000;
    private const uint Mote1 = 0x80281101, Mote2 = 0x80281102;

    public static void Register(Runner r)
    {
        r.Add("give profile: the profile path stays inside ItemGiver (no rooted path, drive, stream or ..)", PathRules);
        r.Add("give profile: gives to the NPC with AC's give action, never to a player of the same name", NpcOnly);
        r.Add("give profile: a refused path returns 0 and is logged once, not every pulse", RefusedPathLoggedOnce);
    }

    private static void PathRules()
    {
        string root = Path.Combine(Program.TempRoot, "ItemGiverPaths");
        string inside = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string? R(string a) => ExpressionEngine.ResolveGiveProfilePath(a, root);

        Check.Eq(R("salvage"), inside + "salvage.utl", "a bare name is a .utl inside");
        Check.Eq(R("salvage.utl"), inside + "salvage.utl", ".utl kept");
        Check.Eq(R("mine.json"), inside + "mine.json", ".json kept");
        Check.Eq(R(@"sub\deep.json"), inside + @"sub\deep.json", "a subfolder is fine");
        Check.Eq(R(@"sub\..\back.utl"), inside + "back.utl", ".. that stays inside is fine");
        Check.Eq(R(@"..\evil.utl"), null, "..\\ climbs out");
        Check.Eq(R(@"sub\..\..\evil"), null, "climbing out through a subfolder");
        Check.Eq(R(@"C:\Windows\win.ini"), null, "a full path");
        Check.Eq(R(@"\\server\share\x.utl"), null, "a UNC path");
        Check.Eq(R(@"\x.utl"), null, "a root-relative path");
        Check.Eq(R("C:x.utl"), null, "a drive-relative path");
        Check.Eq(R("x.utl:hidden"), null, "an alternate data stream");
        Check.Eq(R(".."), null, "the parent folder itself");
        Check.Eq(R("."), null, "the folder itself");
        Check.Eq(R("   "), null, "blank");
    }

    /// <summary>The player, two carried motes, an NPC and (optionally) a player named like it.</summary>
    private static (ExpressionEngine E, WorldObjectCache Cache) World(bool withImitator, bool withNpc = true)
    {
        FakeHost.Reset();
        FakeHost.GiveCalls = true;
        FakeHost.SalvageCalls = true;   // MoveItemExternal recorded: it must not be used
        FakeHost.Names[Player] = "Giver";
        FakeHost.PlayerIdValue = Player;
        FakeHost.Positions[Player] = (0x1048002Au, 100f, 100f, 0f);
        FakeHost.PlayerPose = (0x1048002Au, 100f, 100f, 0f);
        if (withImitator)
        {
            FakeHost.Names[Imitator] = "Town Crier";
            FakeHost.Positions[Imitator] = (0x1048002Au, 100.5f, 100f, 0f);
            FakeHost.ItemTypes[Imitator] = 0x10;    // a creature
            FakeHost.Bitfields[Imitator] = 0x18;    // BF_PLAYER | BF_ATTACKABLE
        }
        if (withNpc)
        {
            FakeHost.Names[Npc] = "Town Crier";
            FakeHost.Positions[Npc] = (0x1048002Au, 101f, 100f, 0f);
            FakeHost.ItemTypes[Npc] = 0x10;
            FakeHost.NotAttackable.Add(Npc);
            FakeHost.Bitfields[Npc] = 0x4;          // not a player, not attackable
        }
        foreach (uint id in new[] { Mote1, Mote2 })
        {
            FakeHost.Names[id] = "Mana Mote";
            FakeHost.Containers[id] = Player;
            FakeHost.ItemTypes[id] = 0x80;
        }
        var host = FakeHost.Create(playerId: Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        foreach (uint id in new[] { Imitator, Npc, Mote1, Mote2 })
            if (FakeHost.Names.ContainsKey(id)) cache.OnCreateObject(id);
        cache.Tick();

        // One rule with no conditions keeps everything.
        Directory.CreateDirectory(ExpressionEngine.ItemGiverDir);
        var p = new LootProfile { Name = "give all" };
        p.Rules.Add(new LootRule { Name = "All", Action = LootAction.Keep });
        p.Save(Path.Combine(ExpressionEngine.ItemGiverDir, "giveall.json"));

        var e = ExpressionTests.Engine(host, playerId: Player);
        e.SetObjectCache(cache);
        return (e, cache);
    }

    // Backticks: an unquoted name with a space doesn't reach the action as one argument.
    private const string GiveExpr = "actiontrygiveprofile[`giveall.json`,`Town Crier`]";

    private static void NpcOnly()
    {
        var (e, _) = World(withImitator: true);
        Check.Eq(e.Evaluate(GiveExpr), "1", "an item was given");
        Check.True(FakeHost.Gives.Count == 1 && FakeHost.Gives[0].Target == Npc, "to the NPC, past the player of that name");
        Check.True(FakeHost.Gives.Count == 1 && FakeHost.Gives[0].Amount == 0, "the whole object");
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "never MoveItemExternal (an NPC ignores it)");

        var (e2, _) = World(withImitator: true, withNpc: false);
        Check.Eq(e2.Evaluate(GiveExpr), "0", "only a player has the name: nothing");
        Check.Eq(FakeHost.Gives.Count, 0, "nothing given to the player");
        Check.Eq(FakeHost.ExternalMoves.Count, 0, "nor moved to them");
    }

    private static void RefusedPathLoggedOnce()
    {
        var (e, _) = World(withImitator: false);
        // The profile exists one folder up: only the path rule refuses it.
        File.Copy(Path.Combine(ExpressionEngine.ItemGiverDir, "giveall.json"),
            Path.Combine(ExpressionEngine.ItemGiverDir, "..", "giveall.json"), overwrite: true);
        for (int i = 0; i < 3; i++)
            Check.Eq(e.Evaluate("actiontrygiveprofile[`../giveall.json`,`Town Crier`]"), "0", $"pulse {i + 1}: refused");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("refused profile '../giveall.json'")), 1, "logged once, with the argument");
        Check.Eq(FakeHost.Gives.Count, 0, "nothing given");
    }
}
