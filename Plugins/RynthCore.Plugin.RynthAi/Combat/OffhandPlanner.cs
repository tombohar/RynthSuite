using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>What combat puts in the off hand (the Shield slot).</summary>
internal enum OffhandMode
{
    /// <summary>A listed shield with a one-handed melee or thrown weapon; dual wield instead only
    /// when "Prefer dual wield" is on and the character has the skill.</summary>
    Auto,
    /// <summary>A listed shield, or nothing.</summary>
    Shield,
    /// <summary>A second listed one-handed melee weapon (dual wield), else a listed shield.</summary>
    Weapon,
    /// <summary>Hands off: never wield, never take off, whatever is there.</summary>
    None,
}

/// <summary>What the main hand holds, by ACE's wield rules.</summary>
internal enum MainHandKind { None, OneHandedMelee, TwoHanded, Launcher, Thrown, Caster }

/// <summary>A listed item that can go in the off hand: a shield, or a one-handed melee weapon.</summary>
internal sealed record OffhandCandidate(int Id, string Name, bool IsShield, bool InOffhand);

/// <summary>The off hand to wield (0 = add nothing; what is there stays) and why.</summary>
internal readonly record struct OffhandPlan(int Id, string Source);

/// <summary>
/// ACE's off-hand rules (ACE.Server WorldObjects/Player_Inventory.cs: CheckWeaponCollision and
/// DoHandleActionGetAndWieldItem; Creature_Equipment.cs), pure:
///   * The off hand is the Shield slot, 0x00200000. A shield (CombatUse 4) goes there; so does
///     an off-hand weapon (dual wield) whose ValidLocations is exactly MeleeWeapon (0x00100000).
///   * The Shield slot is refused while it is occupied, and while the main hand holds a
///     two-handed weapon (WeaponSkill TwoHandedCombat), a caster or an ammo launcher.
///   * A weapon in the Shield slot (dual wield) also needs a one-handed melee main weapon: a
///     thrown weapon, a launcher, a wand or a two-hander there is refused (ConflictingInventoryLocation).
///   * Two-handed weapons (TwoHanded slot), launchers and casters (Held slot) are refused while
///     anything is in the off hand. A thrown weapon is refused with an off-hand weapon, but a
///     shield may stay ("Thrown weapons (ie. phials) can have a shield").
///   * The server does not ask for the Dual Wield skill; off-hand swings use the lower of Dual
///     Wield and the weapon skill (Player_Combat.GetCurrentWeaponSkill), so RynthAi only dual
///     wields with the skill trained.
/// </summary>
internal static class OffhandRules
{
    public const int ShieldSlot = 0x00200000, MeleeSlot = 0x00100000, MissileSlot = 0x00400000,
                     HeldSlot = 0x01000000, TwoHandedSlot = 0x02000000;
    public const uint PropValidLocations = 9, PropCurrentWieldedLocation = 10, PropWeaponSkill = 48, PropCombatUse = 51;
    public const int CombatUseShield = 4, CombatUseTwoHanded = 5;
    public const int SkillTwoHandedCombat = 41;

    // MonsterRule.OffhandId: 0 = the Default row's choice (on the Default row: the global setting), 1-4 a mode, anything else an item id (no AC
    // object has an id of 1-4). The engine and the external editor carry the int as it is.
    public const int RuleDefault = 0, RuleAuto = 1, RuleShield = 2, RuleWeapon = 3, RuleNone = 4;

    /// <summary>A shield: CombatUse Shield, or a non-weapon whose ValidLocations has the Shield slot.</summary>
    public static bool IsShield(int combatUse, int validLocations, bool weaponClass) =>
        combatUse == CombatUseShield || (!weaponClass && (validLocations & ShieldSlot) != 0);

    /// <summary>Two-handed: CombatUse TwoHanded, the TwoHanded slot, or WeaponSkill Two Handed Combat.</summary>
    public static bool IsTwoHanded(int combatUse, int validLocations, int weaponSkill) =>
        combatUse == CombatUseTwoHanded || (validLocations & TwoHandedSlot) != 0 || weaponSkill == SkillTwoHandedCombat;

    /// <summary>A melee weapon the server lets into the Shield slot: ValidLocations exactly MeleeWeapon, not two-handed.</summary>
    public static bool CanBeOffhandWeapon(int validLocations, bool twoHanded) =>
        validLocations == MeleeSlot && !twoHanded;

    /// <summary>May the off hand (a shield, else a weapon) stay with this main hand?</summary>
    public static bool OffhandAllowed(MainHandKind main, bool offIsShield) => main switch
    {
        MainHandKind.None           => true,
        MainHandKind.OneHandedMelee => true,
        MainHandKind.Thrown         => offIsShield,
        _                           => false,
    };

    /// <summary>The mode a MonsterRule.OffhandId encodes; null for 0 (the global setting) or an item id.</summary>
    public static OffhandMode? RuleMode(int offhandId) => offhandId switch
    {
        RuleAuto   => OffhandMode.Auto,
        RuleShield => OffhandMode.Shield,
        RuleWeapon => OffhandMode.Weapon,
        RuleNone   => OffhandMode.None,
        _          => null,
    };

    /// <summary>MonsterRule.OffhandId names an item (the old per-rule picker).</summary>
    public static bool IsRuleItem(int offhandId) => offhandId != RuleDefault && RuleMode(offhandId) == null;

    public static int RuleValue(OffhandMode m) => m switch
    {
        OffhandMode.Shield => RuleShield,
        OffhandMode.Weapon => RuleWeapon,
        OffhandMode.None   => RuleNone,
        _                  => RuleAuto,
    };

    /// <summary>"Auto", "Shield", "Weapon" ("Offhand weapon", "Dual", "DualWield"), "None"; or 0-3. Else <paramref name="fallback"/>.</summary>
    public static OffhandMode Parse(string? s, OffhandMode fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string t = s.Trim().Replace(" ", "").Replace("-", "");
        if (t.Equals("auto", StringComparison.OrdinalIgnoreCase) || t == "0") return OffhandMode.Auto;
        if (t.Equals("shield", StringComparison.OrdinalIgnoreCase) || t == "1") return OffhandMode.Shield;
        if (t.Equals("weapon", StringComparison.OrdinalIgnoreCase) || t.Equals("offhandweapon", StringComparison.OrdinalIgnoreCase)
            || t.Equals("offhand", StringComparison.OrdinalIgnoreCase) || t.Equals("dual", StringComparison.OrdinalIgnoreCase)
            || t.Equals("dualwield", StringComparison.OrdinalIgnoreCase) || t == "2") return OffhandMode.Weapon;
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("off", StringComparison.OrdinalIgnoreCase) || t == "3") return OffhandMode.None;
        return fallback;
    }

    public static bool TryParse(string? s, out OffhandMode mode)
    {
        mode = Parse(s, (OffhandMode)(-1));
        return (int)mode >= 0;
    }

    /// <summary>The spelling saved in LegacyUiSettings.OffhandDefault.</summary>
    public static string SettingValue(OffhandMode m) => m switch
    {
        OffhandMode.Shield => "Shield",
        OffhandMode.Weapon => "Weapon",
        OffhandMode.None   => "None",
        _                  => "Auto",
    };

    public static string Label(OffhandMode m) => m switch
    {
        OffhandMode.Shield => "Shield",
        OffhandMode.Weapon => "Offhand weapon",
        OffhandMode.None   => "None",
        _                  => "Auto",
    };

    public static string KindLabel(MainHandKind k) => k switch
    {
        MainHandKind.OneHandedMelee => "one-handed",
        MainHandKind.TwoHanded      => "two-handed",
        MainHandKind.Launcher       => "launcher",
        MainHandKind.Thrown         => "thrown weapon",
        MainHandKind.Caster         => "caster",
        _                           => "empty hand",
    };
}

/// <summary>
/// The off hand for a main weapon, pure. After the main weapon is chosen:
///   * None: nothing (what is in the off hand stays).
///   * Anything but a one-handed melee weapon in the main hand: nothing to add (a two-hander,
///     launcher or caster can't have one; for a thrown weapon ACE allows a shield, but RynthAi
///     adds none).
///   * An explicit item (the Damage tab's per-monster offhand, else the rule's old item pick):
///     used when it is listed and allowed (a weapon needs the Dual Wield skill and the
///     slot-wield call), else the mode decides. An explicit weapon that is the main weapon this
///     fight (picked for the monster's weakness) gives its place to the other listed one-handed
///     weapon, so the character keeps dual wielding.
///   * Shield: the best listed shield. Weapon: a second listed one-handed melee weapon when
///     the Dual Wield skill is trained, else the best shield. Auto: the best shield; a second
///     listed one-handed weapon instead only when <paramref name="preferDualWield"/> is on and
///     dual wield is possible. A thrown weapon gets a shield too (never a second weapon).
/// "Best": the listed one already in the off hand, else list order. Only listed items count.
/// </summary>
internal static class OffhandPlanner
{
    public static OffhandPlan Choose(OffhandMode mode, MainHandKind main, int mainId,
        IReadOnlyList<OffhandCandidate> listed, bool dualWieldTrained, bool preferDualWield, bool canWieldOffhandWeapon,
        int explicitId = 0, string explicitSource = "")
    {
        if (mode == OffhandMode.None) return new OffhandPlan(0, "Offhand None");
        // A shield goes with a one-handed melee weapon, and with a thrown weapon too (ACE
        // allows it; owner, 2026-10-01). Dual wield only with a one-handed melee weapon.
        if (main != MainHandKind.OneHandedMelee && main != MainHandKind.Thrown)
            return new OffhandPlan(0, $"nothing with a {OffhandRules.KindLabel(main)}");

        bool dualOk = dualWieldTrained && canWieldOffhandWeapon && main == MainHandKind.OneHandedMelee;
        OffhandCandidate? shield = null, weapon = null;
        foreach (var c in listed)
        {
            if (c.Id == 0 || c.Id == mainId) continue;
            if (c.IsShield) { if (shield == null || (c.InOffhand && !shield.InOffhand)) shield = c; }
            else if (weapon == null || (c.InOffhand && !weapon.InOffhand)) weapon = c;
        }

        if (explicitId != 0)
        {
            foreach (var c in listed)
            {
                if (c.Id != explicitId) continue;
                if (explicitId != mainId)
                {
                    if (c.IsShield || dualOk) return new OffhandPlan(c.Id, explicitSource);
                }
                // The off-hand weapon picked is the main weapon this fight (chosen for the monster's
                // weakness): the other listed one-handed weapon goes in the off hand, so the
                // character still dual wields. It used to fall to the mode, and Auto with no shield
                // listed left the off hand empty (Drakkon, 2026-10-21).
                else if (!c.IsShield && dualOk && weapon != null)
                    return new OffhandPlan(weapon.Id, explicitSource + "; it is in the main hand, so the other weapon");
                break;
            }
        }

        string why = dualWieldTrained ? "can't wield into the off hand on this engine" : "Dual Wield not trained";
        switch (mode)
        {
            case OffhandMode.Shield:
                return shield != null ? new OffhandPlan(shield.Id, "Shield") : new OffhandPlan(0, "Shield: none listed");
            case OffhandMode.Weapon:
                if (dualOk && weapon != null) return new OffhandPlan(weapon.Id, "Offhand weapon");
                if (shield != null) return new OffhandPlan(shield.Id, weapon == null ? "Offhand weapon: no second one-handed weapon listed; shield" : $"Offhand weapon: {why}; shield");
                return new OffhandPlan(0, weapon == null ? "Offhand weapon: no second one-handed weapon listed" : $"Offhand weapon: {why}");
            default:
                // Auto is the shield; dual wield only with "Prefer dual wield" on (owner,
                // 2026-10-01), so two swords listed for element switching don't start dual wielding.
                if (dualOk && weapon != null && preferDualWield)
                    return new OffhandPlan(weapon.Id, "Auto (dual wield preferred)");
                return shield != null ? new OffhandPlan(shield.Id, "Auto") : new OffhandPlan(0, "Auto: no shield listed");
        }
    }
}
