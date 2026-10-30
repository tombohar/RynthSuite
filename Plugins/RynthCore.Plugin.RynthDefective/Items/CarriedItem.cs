using System;

namespace RynthCore.Plugin.RynthDefective.Items;

/// <summary>
/// One thing the character carries or wears, as the client reports it. A plain data holder: the
/// reader (HostInventory) fills it, everything else (slot matching, item kinds, the weapon picker)
/// works on it without the host, so it can be tested offline.
/// </summary>
internal sealed class CarriedItem
{
    public uint Id;
    public string Name = string.Empty;
    public uint Wcid;
    /// <summary>MaterialType (int 131): "Silver" in "Silver Wand"; 0 = none / unknown.</summary>
    public int Material;
    /// <summary>ItemType flags (the client's TryGetItemType).</summary>
    public uint ItemType;
    /// <summary>PublicWeenieDesc bitfield (healer, food ...); 0 when the engine can't read it.</summary>
    public uint Bitfield;
    /// <summary>Stack size (at least 1) and the largest stack (1 = not stackable).</summary>
    public int Stack = 1, MaxStack = 1;
    /// <summary>Uses left / most uses (Structure 92 / MaxStructure 91): kits, essences; MaxUses 0 = none.</summary>
    public int Uses, MaxUses;
    /// <summary>ValidLocations (int 9): the EquipMask slots it can go into; 0 = it can't be worn or wielded.</summary>
    public int ValidLocations;
    /// <summary>The slot it is wielded in now (0 = in a pack).</summary>
    public int WieldedLocation;
    /// <summary>CombatUse (int 51): 1 melee, 2 missile, 3 ammo, 4 shield, 5 two-handed.</summary>
    public int CombatUse;
    /// <summary>DamageType (int 45) and UiEffects (int 18): the element of a caster.</summary>
    public int DamageType, UiEffects;
    /// <summary>Icon, underlay and overlay texture ids (0x06......), 0 = none.</summary>
    public uint Icon, Underlay, Overlay;

    public bool Wielded => WieldedLocation != 0;

    /// <summary>The name with the material in front ("Silver Wand"), as the character sees it in the game.</summary>
    public string FullName => ItemNames.Full(Name, Material);

    public override string ToString() => $"0x{Id:X8} {FullName}";
}
