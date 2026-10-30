using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Monster rule Priority 0 = IGNORE: never a target, not "a monster
// nearby" for nav / combat / loot / stance, with no exception: an ignored monster attacking the
// player is still never attacked.
internal static class IgnoreMonsterTests
{
    private const uint Player = 0x50000F01;
    private const uint Lb = 0xA9B40000;
    private const string Grub = "Olthoi Grub", Soldier = "Olthoi Soldier";
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Register(Runner r)
    {
        r.Add("monster ignore: a profile with a Priority 0 row logs and says in chat what Priority 0 means now, once", PriorityZeroIsAnnounced);
        r.Add("monster ignore: a Priority 0 monster is never targeted", NotTargeted);
        r.Add("monster ignore: a Priority 0 monster holds nothing up (nav, combat, close-threat gates)", HoldsNothingUp);
        r.Add("monster ignore: the kill-shot swap does not count a Priority 0 monster as an alternate", KillShotAlternate);
        r.Add("monster ignore: a Priority 0 monster hitting the player is still never targeted", NotTargetedWhenAttackingUs);
        r.Add("monster ignore: Priority 1, no rule and the Default row behave as before", UnchangedForOtherPriorities);
    }

    // -- 0. the owner is told what Priority 0 means now -----------------------

    private static int Notices() => FakeHost.Logs.Count(l => l.Contains("Priority 0 now means"));

    private static void PriorityZeroIsAnnounced()
    {
        var c = World(GrubIgnored(), out var ids, (Grub, 3f));
        var s = (LegacyUiSettings)typeof(CombatManager).GetField("_settings", Any)!.GetValue(c)!;
        s.IsMacroRunning = true;
        c.OnHeartbeat();                                   // the macro runs: the first check
        c.OnHeartbeat();
        c.NotePriorityZeroRules();
        Check.Eq(Notices(), 1, "one log line for the profile's Priority 0 row, however often it is checked");
        Check.True(FakeHost.Logs.Any(l => l.Contains("Priority 0 now means") && l.Contains("never attacked or tracked") && l.Contains(Grub)), "it says what it means and names the row");
        Check.Eq(FakeHost.Chat.Count(l => l.Contains("Priority 0 now means")), 1, "and one chat notice");
        Check.False(Scanned(c, ids[0]), "behavior unchanged: the Priority 0 monster is still never scanned");

        // Another profile loads with the same Priority 0 rule: nothing new to say.
        s.MonsterRules = GrubIgnored();
        c.NotePriorityZeroRules();
        Check.Eq(Notices(), 1, "the same Priority 0 set again: not repeated");
        // A different Priority 0 row: said again.
        s.MonsterRules = new() { new MonsterRule { Name = "Default", Priority = 1 }, new MonsterRule { Name = Soldier, Priority = 0 } };
        c.NotePriorityZeroRules();
        Check.Eq(Notices(), 2, "a different set of Priority 0 rows: said again");

        // No Priority 0 row: silent.
        var none = World(new() { new MonsterRule { Name = "Default", Priority = 1 }, new MonsterRule { Name = Grub, Priority = 2 } }, out _, (Grub, 3f));
        none.NotePriorityZeroRules();
        Check.Eq(Notices(), 0, "no Priority 0 row: no notice");
        var noRules = World(null, out _, (Grub, 3f));
        noRules.NotePriorityZeroRules();
        Check.Eq(Notices(), 0, "default rules: no notice");
    }

    private static uint _next;

    private static void Place(uint id, string name, float north)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, 96f, 96f + north, 0f);
        FakeHost.ItemTypes[id] = 0x10;
    }

    /// <summary>A combat manager with the given monsters (name, metres north) and rules.</summary>
    private static CombatManager World(List<MonsterRule>? rules, out List<uint> ids, params (string Name, float North)[] mobs)
    {
        FakeHost.Reset();
        FakeHost.Names[Player] = "Me";
        FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
        FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
        ids = new List<uint>();
        foreach (var (name, north) in mobs)
        {
            uint id = 0x80000401 + _next++;
            Place(id, name, north);
            ids.Add(id);
        }
        var s = new LegacyUiSettings { MonsterRange = 30 };
        if (rules != null) s.MonsterRules = rules;
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, ids);
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        c.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
        Scan(c);
        return c;
    }

    private static void Scan(CombatManager c)
    {
        typeof(CombatManager).GetField("_lastScanTime", Any)?.SetValue(c, DateTime.MinValue);
        c.ScanNearbyTargets();
    }

    private static List<MonsterRule> GrubIgnored(int defaultPriority = 1) => new()
    {
        new MonsterRule { Name = "Default", Priority = defaultPriority },
        new MonsterRule { Name = Grub, Priority = 0 },
    };

    private static bool Scanned(CombatManager c, uint id) => c.ScannedTargets.Any(t => (uint)t.Id == id);

    // -- 1. not targeted -------------------------------------------------------

    private static void NotTargeted()
    {
        // The grub is closer (3 m) than the soldier (8 m): without the rule it would be picked.
        var c = World(GrubIgnored(), out var ids, (Grub, 3f), (Soldier, 8f));
        uint grub = ids[0], soldier = ids[1];
        Check.False(Scanned(c, grub), "the ignored grub is not in the scan list");
        Check.True(Scanned(c, soldier), "the soldier is");
        c.HandleCombatTrigger();
        Check.Eq((uint)c.activeTargetId, soldier, "the soldier is locked, not the nearer grub");

        // Alone, the grub is not acquired at all.
        var lone = World(GrubIgnored(), out var ids2, (Grub, 3f));
        lone.HandleCombatTrigger();
        Check.Eq(lone.activeTargetId, 0, "a lone ignored grub is never locked");
        Check.Eq(lone.ScannedTargets.Count, 0, "and the scan list is empty");

        // A re-lock: the soldier dies (kill suppression), the next pick must not fall to the grub.
        var c2 = World(GrubIgnored(), out var ids3, (Grub, 3f), (Soldier, 8f));
        c2.HandleCombatTrigger();
        Check.Eq((uint)c2.activeTargetId, ids3[1], "locked the soldier");
        c2.OnKillNotification("Olthoi Soldier's perforated corpse falls before you!");
        Scan(c2);
        c2.HandleCombatTrigger();
        Check.Eq(c2.activeTargetId, 0, "after the soldier dies the next pick is nothing, not the grub");
    }

    // -- 2. holds nothing up ---------------------------------------------------

    private static void HoldsNothingUp()
    {
        var c = World(GrubIgnored(), out _, (Grub, 2f));
        Check.False(c.HasTargets, "HasTargets (corpse combat-urgent gates, nav hold)");
        Check.False(c.HasNearbyMonsters, "HasNearbyMonsters (nav hold, clear-the-room gating)");
        Check.False(c.HasEngageableTarget, "HasEngageableTarget (enter/stay in combat, stance switching, arbiter)");
        Check.False(c.IsUnderCloseAttack, "IsUnderCloseAttack");
        Check.False(c.HasCloseThreat(50), "HasCloseThreat (buff and rest gating, loot starvation)");
        Check.False(c.HasCloseThreat(5), "HasCloseThreat at the loot-starve radius");

        // The control: the same grub with no rule is exactly what held everything up.
        var held = World(null, out _, (Grub, 2f));
        Check.True(held.HasTargets && held.HasNearbyMonsters && held.HasEngageableTarget && held.HasCloseThreat(5),
            "without the rule the same grub holds all of them");
    }

    // -- 3. kill-shot alternate ------------------------------------------------

    private static void KillShotAlternate()
    {
        var c = World(GrubIgnored(), out var ids, (Soldier, 5f), (Grub, 3f));
        c.EngageForTest(unchecked((int)ids[0]));
        var alt = typeof(CombatManager).GetMethod("HasAlternateTarget", Any)!;
        bool HasAlt() => (bool)alt.Invoke(c, new object[] { unchecked((int)ids[0]) })!;
        Check.False(HasAlt(), "the only other monster is an ignored grub: no alternate, keep casting at the soldier");

        var open = World(null, out var ids2, (Soldier, 5f), (Grub, 3f));
        open.EngageForTest(unchecked((int)ids2[0]));
        Check.True((bool)alt.Invoke(open, new object[] { unchecked((int)ids2[0]) })!, "control: an unruled grub is an alternate");
    }

    // -- 4. attacking us: still ignored ----------------------------------------

    private static void NotTargetedWhenAttackingUs()
    {
        var c = World(GrubIgnored(), out var ids, (Grub, 3f), (Soldier, 8f));
        foreach (string line in new[]
        {
            "Olthoi Grub bites you for 3 points of piercing damage!",
            "You evaded Olthoi Grub!",
            "Olthoi Grub bites you for 4 points of piercing damage!",
        })
        {
            c.HandleChatForDamage(line);
            Scan(c);
            c.HandleCombatTrigger();
            Check.False(Scanned(c, ids[0]), $"after '{line}' the grub is still not a candidate");
            Check.Eq((uint)c.activeTargetId, ids[1], $"after '{line}' the soldier (farther) stays the target");
        }

        // Alone and hitting us: never locked, holds nothing.
        var lone = World(GrubIgnored(), out _, (Grub, 2f));
        for (int i = 0; i < 5; i++)
        {
            lone.HandleChatForDamage("Olthoi Grub bites you for 3 points of piercing damage!");
            Scan(lone);
            lone.HandleCombatTrigger();
        }
        Check.Eq(lone.activeTargetId, 0, "a lone ignored grub hitting us is never locked");
        Check.False(lone.HasEngageableTarget || lone.HasTargets, "and holds nothing up");

        // Control: the same lines with no rule, the grub (nearer) is the target.
        var open = World(null, out var ids2, (Grub, 3f), (Soldier, 8f));
        open.HandleChatForDamage("Olthoi Grub bites you for 3 points of piercing damage!");
        Scan(open);
        open.HandleCombatTrigger();
        Check.Eq((uint)open.activeTargetId, ids2[0], "control: an unruled grub is locked");
    }
    // -- 5. unchanged ----------------------------------------------------------

    private static void UnchangedForOtherPriorities()
    {
        // Priority 1 rule and Default row: targeted, nearest first.
        var rules = new List<MonsterRule>
        {
            new MonsterRule { Name = "Default", Priority = 1 },
            new MonsterRule { Name = Grub, Priority = 1 },
        };
        var c = World(rules, out var ids, (Soldier, 6f), (Grub, 3f));
        Check.True(Scanned(c, ids[0]) && Scanned(c, ids[1]), "Priority 1 rule and the Default row: both scanned");
        c.HandleCombatTrigger();
        Check.Eq((uint)c.activeTargetId, ids[1], "nearest first, as before");

        // No rules at all.
        var none = World(null, out var ids2, (Soldier, 6f), (Grub, 3f));
        none.HandleCombatTrigger();
        Check.Eq((uint)none.activeTargetId, ids2[1], "no rules: nearest first");

        // Strict tiers still hold: a Priority 2 soldier beats a nearer Priority 1 grub, which is still a target alone.
        var tiers = World(new List<MonsterRule>
        {
            new MonsterRule { Name = "Default", Priority = 1 },
            new MonsterRule { Name = Soldier, Priority = 2 },
        }, out var ids3, (Soldier, 9f), (Grub, 3f));
        tiers.HandleCombatTrigger();
        Check.Eq((uint)tiers.activeTargetId, ids3[0], "Priority 2 outranks a nearer Priority 1");

        // A Default row at 0 ignores monsters no row names, and only those.
        var d0 = World(new List<MonsterRule>
        {
            new MonsterRule { Name = "Default", Priority = 0 },
            new MonsterRule { Name = Soldier, Priority = 1 },
        }, out var ids4, (Soldier, 9f), (Grub, 3f));
        Check.True(Scanned(d0, ids4[0]), "a named Priority 1 monster is scanned under a Default of 0");
        Check.False(Scanned(d0, ids4[1]), "an unnamed monster takes the Default row: ignored");
    }
}
