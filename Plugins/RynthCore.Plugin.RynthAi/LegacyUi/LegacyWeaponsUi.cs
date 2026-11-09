using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// The Items list logic behind the engine's Items panel (ItemsFace): add the selected
/// weapon or consumable, fill from pack, prune. Draws nothing; its ImGui window went
/// with the plugin render path.
/// </summary>
internal sealed class LegacyWeaponsUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private WorldObjectCache? _worldFilter;

    public LegacyWeaponsUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    public void SetWorldFilter(WorldObjectCache cache) => _worldFilter = cache;

    public void AddSelectedWeapon()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click a weapon in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        // A class guessed from the name before the item type was known can be wrong: ask the
        // game for the real type before refusing (and fix the cached entry for combat too).
        if (wo != null && !IsWeapon(wo) && !CombatManager.IsShieldItem(wo))
            wo = _worldFilter.ReclassifyFromItemType(wo.Id) ?? wo;
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (!IsWeapon(wo) && !CombatManager.IsShieldItem(wo))
            _host.WriteToChat("[RynthAi] Selected item is not a weapon, wand or shield.", 1);
        else if (_settings.ItemRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else if (!IsWeapon(wo))
        {
            // A shield: combat wields it in the off hand (the Monsters rule's / global Offhand setting).
            _settings.ItemRules.Add(new ItemRule
            {
                Id            = wo.Id,
                Name          = wo.Name,
                Element       = "",
                ElementSource = "",
                Action        = WeaponList.ShieldAction,
            });
            string shown = WeaponNames.For(_host, _worldFilter, wo.Id, wo.Name);
            _host.WriteToChat($"[RynthAi] Added shield: {shown} (0x{(uint)wo.Id:X8}) — wielded in the off hand with a one-handed weapon (Offhand: {_settings.OffhandDefault})", 1);
        }
        else
        {
            var info = WeaponElementTracker.ReadFrom(_host, wo.Id, wo.Name);
            _settings.ItemRules.Add(new ItemRule
            {
                Id            = wo.Id,
                Name          = wo.Name,
                Element       = info.Element,
                ElementSource = info.Source,
                Action        = "Weapon",
            });
            string shown = WeaponNames.For(_host, _worldFilter, wo.Id, wo.Name);
            string elem = info.Unknown ? "element unknown until identified" : $"{info.Element}, from {info.Source}";
            _host.WriteToChat($"[RynthAi] Added weapon: {shown} (0x{(uint)wo.Id:X8}) [{elem}]", 1);
        }
    }

    public void AddSelectedConsumable()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click an item in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (_settings.ConsumableRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else
        {
            string type = DetectConsumableType(wo);
            _settings.ConsumableRules.Add(new ConsumableRule
            {
                Id   = wo.Id,
                Name = wo.Name,
                Type = type,
            });
            _host.WriteToChat($"[RynthAi] Added consumable: {wo.Name} (0x{(uint)wo.Id:X8}) [{type}]", 1);
        }
    }

    /// <summary>
    /// Adds every weapon and usable consumable in <paramref name="inventory"/> that
    /// isn't listed yet: weapons (melee, missile, wands; the wielded one first, since
    /// combat treats the first weapon as the default), healing kits, lockpicks and
    /// pet essences. Mana stones are left out on purpose: listing any switches mana
    /// tapping from "every mana stone" to "only these names".
    /// </summary>
    public (int Weapons, int Consumables) FillFromPack(IReadOnlyList<WorldObject> inventory, uint playerId)
    {
        int weapons = 0, consumables = 0;
        var ordered = inventory
            .OrderByDescending(wo => playerId != 0 && wo.Wielder == unchecked((int)playerId))
            .ThenBy(wo => wo.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var wo in ordered)
        {
            if (IsWeapon(wo))
            {
                if (_settings.ItemRules.Any(x => x.Id == wo.Id)) continue;
                var info = WeaponElementTracker.ReadFrom(_host, wo.Id, wo.Name);
                _settings.ItemRules.Add(new ItemRule { Id = wo.Id, Name = wo.Name, Element = info.Element, ElementSource = info.Source, Action = "Weapon" });
                weapons++;
                continue;
            }
            string? type = PackConsumableType(wo);
            if (type == null || _settings.ConsumableRules.Any(x => x.Id == wo.Id)) continue;
            // An "...Essence" that is no combat pet device (appraised: no charges, no Summoning
            // requirement, no summoning-gem icon; PetDeviceKind) is not listed as a combat pet: a
            // listed "Common Golem Essence" summoned a passive pet that held the slot
            // (2026-11-08, R-20261108-215240-651b). One not appraised yet is still listed, as before.
            if (type == "Pet" && PetDeviceKind.IsNotCombat(_host, unchecked((uint)wo.Id))) continue;
            _settings.ConsumableRules.Add(new ConsumableRule { Id = wo.Id, Name = wo.Name, Type = type });
            consumables++;
        }
        return (weapons, consumables);
    }

    /// <summary>
    /// Removes weapons and consumables this character has nothing of: entries a profile
    /// picked up from another character. Returns their names. See <see cref="CleanMissing"/>.
    /// </summary>
    public List<string> PruneNotOwned(IReadOnlyList<WorldObject> inventory) => CleanMissing(inventory).Removed;

    /// <summary>
    /// Clears out weapons and consumables whose item is no longer in
    /// <paramref name="inventory"/> (owned items: main pack, side packs, wielded gear).
    /// An entry whose item is gone, but with an unlisted item of the same name in the pack
    /// (a new stack of kits, a replacement weapon), is re-pointed at that item instead of
    /// removed. Returns the names removed and the names re-pointed.
    /// </summary>
    public (List<string> Removed, List<string> Relinked) CleanMissing(IReadOnlyList<WorldObject> inventory)
    {
        var removed  = new List<string>();
        var relinked = new List<string>();
        CleanList(_settings.ItemRules, r => r.Id, (r, id) => r.Id = id, r => r.Name, inventory, removed, relinked);
        CleanList(_settings.ConsumableRules, r => r.Id, (r, id) => r.Id = id, r => r.Name, inventory, removed, relinked);
        return (removed, relinked);
    }

    private static void CleanList<T>(List<T> rules, Func<T, int> getId, Action<T, int> setId, Func<T, string> getName,
                                     IReadOnlyList<WorldObject> inventory, List<string> removed, List<string> relinked)
    {
        var owned = new HashSet<int>(inventory.Select(w => w.Id));
        // Items already listed by a live entry: a gone entry is never pointed at one of them.
        var listed = new HashSet<int>(rules.Select(getId).Where(owned.Contains));
        rules.RemoveAll(r =>
        {
            if (owned.Contains(getId(r))) return false;
            string name = getName(r);
            var spare = inventory.FirstOrDefault(w => !listed.Contains(w.Id)
                && string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            if (spare != null)
            {
                setId(r, spare.Id);
                listed.Add(spare.Id);
                relinked.Add(name);
                return false;
            }
            removed.Add(name);
            return true;
        });
    }

    internal static string? PackConsumableType(WorldObject wo)
    {
        switch (wo.ObjectClass)
        {
            case AcObjectClass.HealingKit:
                // A kit restores the vital in its name: a Greater Mana Kit is no healing kit (DreamWeave has them).
                if ((wo.Name ?? "").Contains("Stamina", StringComparison.OrdinalIgnoreCase)) return "StaminaKit";
                if ((wo.Name ?? "").Contains("Mana", StringComparison.OrdinalIgnoreCase)) return "ManaKit";
                return "HealthKit";
            case AcObjectClass.Food:       return PotionType(wo);
            case AcObjectClass.Lockpick:   return "Lockpick";
            case AcObjectClass.Misc:
            case AcObjectClass.CombatPet:
                return wo.Name.Contains("Essence", StringComparison.OrdinalIgnoreCase) ? "Pet" : null;
            default: return null;
        }
    }

    /// <summary>HealthPotion / ManaPotion / StaminaPotion for a Food item named for a vital, else null.</summary>
    internal static string? PotionType(WorldObject wo)
    {
        if (wo.ObjectClass != AcObjectClass.Food || string.IsNullOrEmpty(wo.Name)) return null;
        string n = wo.Name;
        // Field Rations restore stamina (BoostEnum 4) but don't say so in the name.
        if (n.EndsWith("Field Rations", StringComparison.OrdinalIgnoreCase)) return "StaminaPotion";
        if (n.Contains("Mana", StringComparison.OrdinalIgnoreCase)) return "ManaPotion";
        if (n.Contains("Stamina", StringComparison.OrdinalIgnoreCase)) return "StaminaPotion";
        if (n.Contains("Heal", StringComparison.OrdinalIgnoreCase) || n.Contains("Health", StringComparison.OrdinalIgnoreCase)) return "HealthPotion";
        return null;
    }

    private static bool IsWeapon(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon   ||
        wo.ObjectClass == AcObjectClass.MissileWeapon ||
        wo.ObjectClass == AcObjectClass.WandStaffOrb;

    /// <summary>Auto-detect consumable type from item name.</summary>
    private static string DetectConsumableType(WorldObject wo)
    {
        string name = wo.Name;
        if (string.IsNullOrEmpty(name)) return "General";

        // Potions and elixirs are Food: typed by the vital they restore. A mana
        // potion used to become "ManaStone" from the word "Mana".
        string? potion = PotionType(wo);
        if (potion != null) return potion;

        // Health items: a kit is used on yourself, anything else (Conquest's Health Tincture is
        // no Food) is used with no target, like a potion.
        if (name.Contains("Heal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Health", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Elixir of Healing", StringComparison.OrdinalIgnoreCase))
            return BuffManager.IsUseOnSelfKit(wo) ? "HealthKit" : "HealthPotion";

        // Mana items. A mana kit (it restores your own mana, like a stamina kit) is no stone: typed
        // ManaStone it switched off the ordinary stones and Drakkon's gear ran dry (10-23).
        if (name.Contains("Mana", StringComparison.OrdinalIgnoreCase))
            return name.TrimEnd().EndsWith(" Kit", StringComparison.OrdinalIgnoreCase) ? "ManaKit" : "ManaStone";

        // Stamina items. A stamina kit is used on yourself, not drunk: its own type, so the
        // potion step never takes it ("Stamina" is the old name for stamina potions).
        if (name.Contains("Stamina", StringComparison.OrdinalIgnoreCase))
            return name.TrimEnd().EndsWith(" Kit", StringComparison.OrdinalIgnoreCase) ? "StaminaKit" : "Stamina";

        // Lockpicks
        if (name.Contains("Lockpick", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Lock Pick", StringComparison.OrdinalIgnoreCase))
            return "Lockpick";

        // Combat-pet summoning essences (PetDevices) — consumed by PetManager.
        if (name.Contains("Essence", StringComparison.OrdinalIgnoreCase))
            return "Pet";

        return "General";
    }
}
