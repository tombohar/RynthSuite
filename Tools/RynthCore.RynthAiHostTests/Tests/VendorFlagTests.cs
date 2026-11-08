using System;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Vendor;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

/// <summary>
/// /ub vendor open finds a vendor by the server's vendor flag (PublicWeenieDesc BF_VENDOR 0x200).
/// The world cache never files anything as AcObjectClass.Vendor: a retail-style shopkeeper is a
/// creature (an Npc there), and a summoned storage NPC can reach the cache as a typed item. By
/// name, partial name or id the flagged object opens; without the flag it is not a vendor.
/// </summary>
internal static class VendorFlagTests
{
    private const uint Player = 0x50000E01;
    private const uint Here = 0xA9B4001C;
    private const uint Shopkeeper = 0x80310003;   // a creature with the vendor flag (retail and stock ACE)
    private const uint Keeper = 0x80310001;       // a typed item with the vendor flag (a server's own NPC)
    private const uint Plain = 0x80310002;        // a typed item without it
    private const uint Bystander = 0x80310004;    // a creature (an Npc) without it
    private const uint BfVendor = 0x200;
    private const uint TGem = 0x800, TCreature = 0x10;
    private const string Attempt = "AutoVendor: attempting to open vendor";

    public static void Register(Runner r)
    {
        r.Add("vendor open: a shopkeeper NPC with the vendor bitfield is found by name (retail-style)", RetailShopkeeper);
        r.Add("vendor open: a cache non-Vendor with the vendor bitfield is found by name", FoundByName);
        r.Add("vendor open: a cache non-Vendor with the vendor bitfield is found by id", FoundById);
        r.Add("vendor open: objects without the vendor bitfield are not vendors (by name and by id)", NotFlagged);
        r.Add("vendor open: a partial name finds the flagged object", FoundByPartial);
    }

    private static AutoVendorManager Rig()
    {
        FakeHost.Reset();
        FakeHost.Names[Player] = "Vendor Tester";
        FakeHost.Positions[Player] = (Here, 96f, 96f, 0f);
        FakeHost.PlayerPose = (Here, 96f, 96f, 0f);
        FakeHost.Containers[Player] = 0;
        foreach (var (id, name, type) in new[]
                 {
                     (Keeper, "Vault Keeper", TGem), (Plain, "Plain Gem", TGem),
                     (Shopkeeper, "Shopkeeper Alfrid", TCreature), (Bystander, "Town Crier", TCreature),
                 })
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Here, 97f, 97f, 0f);
            FakeHost.ItemTypes[id] = type;
            FakeHost.Containers[id] = 0;
            FakeHost.NotAttackable.Add(id);
        }
        FakeHost.Bitfields[Keeper] = BfVendor;
        FakeHost.Bitfields[Shopkeeper] = BfVendor;
        FakeHost.Bitfields[Bystander] = 0;

        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        foreach (uint id in new[] { Keeper, Plain, Shopkeeper, Bystander }) cache.OnCreateObject(id);
        for (int i = 0; i < 12; i++) cache.Tick();
        // Precondition: the cache really does file all of them as something other than Vendor.
        foreach (uint id in new[] { Keeper, Plain, Shopkeeper, Bystander })
            Check.True(cache.AllKnownObjects().Any(w => w.Id == unchecked((int)id) && w.ObjectClass != AcObjectClass.Vendor),
                $"precondition: 0x{id:X8} cached as a non-Vendor");
        return new AutoVendorManager(host, new LegacyUiSettings(), cache, Player, () => string.Empty);
    }

    private static bool Attempted() => FakeHost.Logs.Any(l => l.Contains(Attempt, StringComparison.Ordinal));

    // The open attempt names the chosen object: "...attempting to open vendor <name>".
    private static bool Opened(string name) => FakeHost.Logs.Any(l => l.EndsWith(Attempt + " " + name, StringComparison.Ordinal));

    private static void RetailShopkeeper()
    {
        var m = Rig();
        m.CommandOpen("Shopkeeper Alfrid", false);
        Check.True(Opened("Shopkeeper Alfrid"), "the flagged shopkeeper is the one opened by name");
    }

    private static void FoundByName()
    {
        var m = Rig();
        m.CommandOpen("Vault Keeper", false);
        Check.True(Opened("Vault Keeper"), "the flagged object is the one opened by name");
    }

    private static void FoundById()
    {
        var m = Rig();
        m.CommandOpen("0x" + Keeper.ToString("X8"), false);
        Check.True(Opened("Vault Keeper"), "the flagged object is the one opened by hex id");
    }

    private static void NotFlagged()
    {
        var m = Rig();
        m.CommandOpen("Plain Gem", false);
        Check.False(Attempted(), "an unflagged item is not opened by name");
        m.CommandOpen("0x" + Plain.ToString("X8"), false);
        Check.False(Attempted(), "an unflagged item is not opened by id");
        m.CommandOpen("Town Crier", false);
        Check.False(Attempted(), "an unflagged NPC is not opened by name");
    }

    private static void FoundByPartial()
    {
        var m = Rig();
        m.CommandOpen("keeper", true);
        Check.True(Opened("Vault Keeper") || Opened("Shopkeeper Alfrid"), "a partial name opens a flagged object");
        Check.False(Opened("Plain Gem") || Opened("Town Crier"), "never an unflagged one");
    }
}
