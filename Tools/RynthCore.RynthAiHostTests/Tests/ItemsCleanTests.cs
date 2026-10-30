using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Items "Remove Missing" (/ra items clean, LegacyWeaponsUi.CleanMissing): weapons and consumables
// whose item is no longer owned are removed, unless an unlisted item of the same name is in the
// pack, in which case the entry is re-pointed at it. Live entries are never touched.
internal static class ItemsCleanTests
{
    public static void Register(Runner r)
    {
        r.Add("items clean: entries for items no longer owned are removed, owned ones kept", RemovesGone);
        r.Add("items clean: a gone entry moves to an unlisted item of the same name", RelinksSameName);
        r.Add("items clean: a gone duplicate of an already-listed item is removed, not re-pointed", DuplicateRemoved);
    }

    private static (LegacyUiSettings S, LegacyWeaponsUi W) Rig()
    {
        var s = new LegacyUiSettings();
        return (s, new LegacyWeaponsUi(s, FakeHost.Create()));
    }

    private static WorldObject Wo(int id, string name) => new(id, name, AcObjectClass.MeleeWeapon);

    private static void RemovesGone()
    {
        var (s, w) = Rig();
        s.ItemRules.Add(new ItemRule { Id = 1, Name = "Gold Epee", Element = "Slash", Action = "Weapon" });
        s.ItemRules.Add(new ItemRule { Id = 2, Name = "War Axe", Element = "Slash", Action = "Weapon" });
        s.ConsumableRules.Add(new ConsumableRule { Id = 3, Name = "Peerless Healing Kit", Type = "HealthKit" });
        s.ConsumableRules.Add(new ConsumableRule { Id = 4, Name = "Intricate Lockpick", Type = "Lockpick" });

        var (removed, relinked) = w.CleanMissing(new List<WorldObject> { Wo(1, "Gold Epee"), Wo(3, "Peerless Healing Kit") });

        Check.Eq(s.ItemRules.Count, 1, "one weapon left");
        Check.Eq(s.ItemRules[0].Id, 1, "the owned weapon stays");
        Check.Eq(s.ConsumableRules.Count, 1, "one consumable left");
        Check.Eq(s.ConsumableRules[0].Id, 3, "the owned consumable stays");
        Check.True(removed.SequenceEqual(new[] { "War Axe", "Intricate Lockpick" }), "the gone ones are reported");
        Check.Eq(relinked.Count, 0, "nothing re-pointed");
    }

    private static void RelinksSameName()
    {
        var (s, w) = Rig();
        s.ItemRules.Add(new ItemRule { Id = 10, Name = "Silver Acid Rapier", Element = "Acid", Action = "Weapon" });
        s.ConsumableRules.Add(new ConsumableRule { Id = 20, Name = "Peerless Healing Kit", Type = "HealthKit" });

        var (removed, relinked) = w.CleanMissing(new List<WorldObject> { Wo(11, "Silver Acid Rapier"), Wo(21, "peerless healing kit") });

        Check.Eq(removed.Count, 0, "nothing removed");
        Check.Eq(relinked.Count, 2, "both re-pointed");
        Check.Eq(s.ItemRules[0].Id, 11, "weapon now points at the replacement");
        Check.Eq(s.ItemRules[0].Element, "Acid", "its element is kept");
        Check.Eq(s.ConsumableRules[0].Id, 21, "kits point at the new stack (name match ignores case)");
    }

    private static void DuplicateRemoved()
    {
        var (s, w) = Rig();
        s.ItemRules.Add(new ItemRule { Id = 30, Name = "Ebony Jo", Element = "Bludgeon", Action = "Weapon" });
        s.ItemRules.Add(new ItemRule { Id = 31, Name = "Ebony Jo", Element = "Bludgeon", Action = "Weapon" });

        var (removed, relinked) = w.CleanMissing(new List<WorldObject> { Wo(30, "Ebony Jo") });

        Check.Eq(s.ItemRules.Count, 1, "the gone copy is removed");
        Check.Eq(s.ItemRules[0].Id, 30, "the live entry stays");
        Check.Eq(relinked.Count, 0, "not re-pointed at the item already listed");
        Check.Eq(removed.Count, 1, "one removal reported");
    }
}
