using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>One stack of arrows, quarrels or darts the character carries.</summary>
/// <param name="AmmoType">AC AmmoType (int 50): 1 arrow, 2 quarrel, 4 dart; 0 = none (an ammunition-slot
/// item with no AmmoType and no ammo name: a server's own ammo, see <see cref="MissileAmmo.CanShoot"/>).</param>
/// <param name="Element">Normalized element ("" = none known).</param>
/// <param name="Prismatic">Takes the launcher's element (DamageType Base, or named Prismatic).</param>
/// <param name="Quality">AmmoRecipes rank: Deadly beats Greater beats plain; 1 for ammo no recipe makes.</param>
/// <param name="ListIndex">Position in the Items list; -1 = not listed.</param>
/// <param name="Wielded">In the ammunition slot now.</param>
/// <param name="Infinite">Never runs out (UnlimitedUse, or a single item that doesn't stack): see <see cref="MissileAmmo.LooksInfinite"/>.</param>
internal sealed record AmmoCandidate(int Id, string Name, int AmmoType, string Element, bool Prismatic,
    int Quality, int ListIndex, bool Wielded, bool Infinite = false)
{
    public bool Listed => ListIndex >= 0;
    /// <summary>No AmmoType: fits any launcher in hand, but must come off before a launcher is wielded.</summary>
    public bool Untyped => AmmoType == 0;
}

/// <summary>
/// Which ammo a missile launcher shoots, and which stack to put in the ammunition slot. Pure: no
/// host. The rules are ACE's (Player_Inventory.CheckWeaponCollision, DamageEvent.GetBaseDamage):
///   * A launcher and its ammo carry AmmoType (int 50, in CreateObject): Arrow 1, Bolt 2, Atlatl 4,
///     and crystal / chorizite variants. The server refuses to wield a launcher while ammo of
///     another type is in the ammunition slot (0x00800000), and refuses ammo that doesn't match
///     the launcher in hand. So a bow-to-crossbow swap must take the arrows off first.
///   * The ammo check is skipped when either side has no AmmoType, the launcher check is not: ammo
///     with none goes on with any launcher in hand, but must come off before a launcher is wielded
///     (2026-11-07, servers' custom infinite ammo).
///   * Thrown weapons (stackable, in the missile-weapon slot) are their own ammo: no AmmoType rule.
///   * A launcher's missile damage takes the AMMO's element. Prismatic ammo (DamageType Base)
///     takes the launcher's element instead (Pierce when the launcher has none).
/// </summary>
internal static class MissileAmmo
{
    public const uint PropValidLocations = 9, PropMaxStackSize = 11, PropStackSize = 12, PropAmmoType = 50;
    public const int AmmoSlot = 0x00800000, MissileWeaponSlot = 0x00400000;
    public const int Arrow = 0x1, Bolt = 0x2, Atlatl = 0x4;
    /// <summary>DamageType Base: prismatic ammo, the launcher decides the element.</summary>
    public const int DamageTypeBase = 0x10000000;

    /// <summary>
    /// The ammo type a launcher needs: AmmoType when known, else the name (crossbow, atlatl,
    /// bow). 0 = needs none (a thrown weapon) or not known: ammo is then left alone, as before.
    /// </summary>
    public static int LauncherAmmoType(int ammoTypeProp, string? name, bool stackable)
    {
        if (stackable) return 0;                       // thrown: its own ammo
        if (ammoTypeProp > 0) return ammoTypeProp;
        string n = name ?? "";
        if (n.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0) return Bolt;
        if (n.IndexOf("atlatl", StringComparison.OrdinalIgnoreCase) >= 0) return Atlatl;
        if (n.IndexOf("bow", StringComparison.OrdinalIgnoreCase) >= 0) return Arrow;
        return 0;
    }

    /// <summary>A stack's ammo type: AmmoType when known, else the name (arrow, quarrel/bolt, dart). 0 = not ammo we know.</summary>
    public static int AmmoTypeOf(int ammoTypeProp, string? name)
    {
        if (ammoTypeProp > 0) return ammoTypeProp;
        string n = name ?? "";
        if (AmmoRecipes.IsLooseAmmoName(n, WeaponCategory.Crossbow)) return Bolt;   // before "arrow": no clash, but quarrels first
        if (AmmoRecipes.IsLooseAmmoName(n, WeaponCategory.Atlatl)) return Atlatl;
        if (AmmoRecipes.IsLooseAmmoName(n, WeaponCategory.Bow)) return Arrow;
        return 0;
    }

    /// <summary>The fletching category of an ammo type (for the quality rank and labels).</summary>
    public static WeaponCategory CategoryOf(int ammoType)
    {
        if ((ammoType & (0x2 | 0x10 | 0x80)) != 0) return WeaponCategory.Crossbow;
        if ((ammoType & (0x4 | 0x20 | 0x100)) != 0) return WeaponCategory.Atlatl;
        return WeaponCategory.Bow;
    }

    /// <summary>"arrows", "quarrels", "darts".</summary>
    public static string Label(int ammoType) => CategoryOf(ammoType) switch
    {
        WeaponCategory.Crossbow => "quarrels",
        WeaponCategory.Atlatl   => "darts",
        _                       => "arrows",
    };

    /// <summary>
    /// The server's rule for wielding a launcher: the ammo already on must be of the launcher's type
    /// (a crystal arrow is not an arrow). Ammo with no AmmoType does not fit: ACE compares the
    /// launcher's type with the ammo's (none), so it must come off first.
    /// </summary>
    public static bool Fits(int launcherType, int ammoType) => launcherType != 0 && ammoType == launcherType;

    /// <summary>
    /// The launcher in hand can shoot it: ammo of its type, or ammo with no AmmoType at all (ACE checks
    /// ammo going into the ammunition slot against the launcher only when both have a type). Custom
    /// infinite ammo is sometimes made that way.
    /// </summary>
    public static bool CanShoot(int launcherType, int ammoType) => launcherType != 0 && (ammoType == launcherType || ammoType == 0);

    /// <summary>PropertyBool UnlimitedUse: ammo the server never uses up (ACE Player_Missile.UpdateAmmoAfterLaunch).</summary>
    public const uint PropUnlimitedUse = 63;

    /// <summary>
    /// Ammo that never runs out: UnlimitedUse (known once the item is appraised), or a single item that
    /// doesn't stack (every ammo the game drops or crafts stacks), or named so. Servers make their
    /// infinite ammo these ways; a single custom arrow that is used up costs one shot, then the next pick.
    /// </summary>
    public static bool LooksInfinite(bool unlimitedUse, int stackSize, int maxStackSize, string? name)
    {
        if (unlimitedUse) return true;
        if (maxStackSize <= 1 && stackSize <= 1) return true;
        string n = name ?? "";
        return n.IndexOf("Infinite", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("Unlimited", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("Endless", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Prismatic: DamageType has the Base bit, or the name says so.</summary>
    public static bool IsPrismatic(int? damageType, string? name) =>
        (damageType is int dt && (dt & DamageTypeBase) != 0)
        || (name ?? "").IndexOf("Prismatic", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The element the launcher does with this ammo: prismatic takes the launcher's (Pierce
    /// when it has none); other ammo its own ("" when not known).
    /// </summary>
    public static string EffectiveElement(AmmoCandidate ammo, string launcherElement)
    {
        if (ammo.Prismatic) return launcherElement.Length > 0 ? launcherElement : "Pierce";
        return ammo.Element;
    }

    /// <summary>
    /// The stack to wield in <paramref name="launcherType"/>'s launcher, null when the pack has
    /// none it can shoot (<see cref="CanShoot"/>). Order:
    ///   1. listed ammo (the Items list) before anything else;
    ///   2. infinite ammo (it never runs out, owner 2026-11-07) before ammo that is used up;
    ///   3. ammo of the launcher's own type before ammo with no AmmoType, and of those, not
    ///      <paramref name="demoteId"/> (the stack just taken off the other launcher: it is that one's);
    ///   4. the element: <paramref name="elementRank"/> (lower = better) of the element the
    ///      launcher would do with it; ammo of no known element counts as Pierce;
    ///   5. quality (Deadly > Greater > plain), then the stack already wielded, then list / pack order.
    /// </summary>
    public static AmmoCandidate? Choose(int launcherType, string launcherElement, IReadOnlyList<AmmoCandidate> pack,
        Func<string, int> elementRank, int demoteId = 0)
    {
        AmmoCandidate? best = null;
        (int, int, int, int, int, int, int, int) bestKey = default;
        for (int i = 0; i < pack.Count; i++)
        {
            var a = pack[i];
            if (!CanShoot(launcherType, a.AmmoType)) continue;
            string e = EffectiveElement(a, launcherElement);
            var key = (a.Listed ? 0 : 1,
                       a.Infinite ? 0 : 1,
                       a.Untyped ? 1 : 0,
                       demoteId != 0 && a.Id == demoteId ? 1 : 0,
                       elementRank(e.Length > 0 ? e : "Pierce"),
                       -a.Quality,
                       a.Wielded ? 0 : 1,
                       a.Listed ? a.ListIndex : 100000 + i);
            if (best == null || key.CompareTo(bestKey) < 0) { best = a; bestKey = key; }
        }
        return best;
    }

    /// <summary>
    /// An element ranking for <see cref="Choose"/>: the Monsters rule's damage type first (when
    /// set), then the weakness tie groups from weakest; anything else after. With neither, all equal.
    /// </summary>
    public static Func<string, int> RankBy(string ruleElement, IReadOnlyList<List<string>>? weakGroups)
    {
        return e =>
        {
            if (ruleElement.Length > 0 && e == ruleElement) return 0;
            if (weakGroups != null)
                for (int g = 0; g < weakGroups.Count; g++)
                    if (weakGroups[g].Contains(e)) return 1 + g;
            return 1000;
        };
    }
}
