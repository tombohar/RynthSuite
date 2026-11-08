using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Summons stopped for the rest of the session (report 2026-11-06, release 2026.11.3.2: "after a
// time my character will stop summoning, he keeps fighting and looting", fixed only by restarting
// the game). Two ways PetManager waited forever, with no log line:
//   * the client's gesture gate (CanCastNow) stuck closed: buffs and combat go on through
//     CastGateWatchdog's override, the summon read the raw gate and never went;
//   * a pet the client still shows (a ghost the server no longer has, or one whose delete the
//     cache missed) read as "a pet is out" forever.
// Follow-up from the same player: "it thinks it's already summoned if you die", and it stopped
// while "rescanning inventory". A pet out at a death or a portal is left behind (the server drops
// it without telling a player who left), a read of 0 during a rescan refilled or parked a full
// essence, and an essence that vanished from the client was used instead of its successor.
internal static unsafe class PetSummonStopsTests
{
    private const uint Player = 0x50000D11;
    private const uint Mob = 0x80310010;
    private const uint Essence = 0x80310001;
    private const uint Pet = 0x80310020;
    private const uint Spirit = 0x80310002;
    private const uint Essence2 = 0x80310004;   // a same-named essence that replaces a vanished one
    private const uint StructureKey = 92;   // AcIntProperty.Structure
    private const uint Lb = 0xA9B40000;

    private const string PetName = "Pet Tester's Fire Moar";

    /// <summary>GetObjectServerAge answers (ms since the server's last message), -1 = no record.</summary>
    private static readonly Dictionary<uint, int> ServerAges = new();

    public static void Register(Runner r)
    {
        r.Add("pets: a gesture gate stuck closed holds the summon only 4 s, logged; a short gesture just waits", StuckGestureGate);
        r.Add("pets: a pet the server has been silent about for 60 s is a ghost: a new pet is summoned", GhostPetIgnored);
        r.Add("pets: a pet the client no longer has (missed delete) does not hold summons", MissedDeleteIgnored);
        r.Add("pets: 'is already active' after a ghost summon trusts the quiet pet for 5 min", AlreadyActiveTrustsPet);
        r.Add("pets: a death leaves the pet out behind: the next fight summons at once (before: never again)", DeathLeavesPetBehind);
        r.Add("pets: a death mid-summon clears the summon state; the server's essence cooldown still holds", DeathMidSummon);
        r.Add("pets: a portal leaves the pet out behind; 'is already active' brings it back", PortalLeavesPetBehind);
        r.Add("pets: no server record for a pet for 60 s of watching (engine reload) is silence too", NoRecordIsSilence);
        r.Add("pets: a read of 0 right after 50 (a rescan) summons; it does not refill or park the essence", RescanZeroRead);
        r.Add("pets: empty read, no spirit, never confirmed by the server: tried once, parked only after 'not enough charges'", UnconfirmedEmptyTried);
        r.Add("pets: an essence the client no longer has is not used; its same-named successor is", VanishedEssenceReplaced);
    }

    // ── Rig ───────────────────────────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public long Now = 1_000_000;
        public readonly LegacyUiSettings S = new() { SummonPets = true, MonsterRange = 20, CustomPetRange = 5, PetAutoRefill = true };
        public readonly PetManager Pets;
        public readonly WorldObjectCache Cache;

        public Rig(bool gates = false, bool serverAge = false, bool pet = false, bool spirit = false, bool replacement = false, int charges = 50)
        {
            FakeHost.Reset();
            ServerAges.Clear();
            FakeHost.WeaponCalls = true;   // UseObject (summon)
            FakeHost.UseOnCalls = true;
            FakeHost.ServerGates = gates;  // GetCastBusyState (FakeHost.CastBusy)
            if (serverAge) FakeHost.ConfigureApi = ServerAgeApi;
            Place(Player, "Pet Tester", 96, 96, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            Place(Mob, "Drudge Skulker", 96, 99, creature: true);   // 3 m north
            FakeHost.Names[Essence] = "Fire Moar Essence (50)";
            FakeHost.Containers[Essence] = Player;
            FakeHost.Ints[(Essence, StructureKey)] = charges;
            var known = new List<uint> { Mob, Essence };
            if (spirit)
            {
                FakeHost.Names[Spirit] = "Encapsulated Spirit";
                FakeHost.Containers[Spirit] = Player;
                known.Add(Spirit);
            }
            if (replacement)
            {
                FakeHost.Names[Essence2] = "Fire Moar Essence (50)";
                FakeHost.Containers[Essence2] = Player;
                FakeHost.Ints[(Essence2, StructureKey)] = 50;
                known.Add(Essence2);
            }

            var host = FakeHost.Create(Player);
            Cache = FakeHost.MakeCache(host, Player, known);
            S.IsMacroRunning = true;
            S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Essence), Name = "Fire Moar Essence (50)", Type = "Pet" });
            var combat = new CombatManager(host, S, Cache);
            combat.SetPlayerId(Player);
            combat.ScanNearbyTargets();
            Check.True(combat.ScannedTargets.Count == 1, "rig: the monster is scanned");
            if (pet)
            {
                Place(Pet, PetName, 96, 97, creature: true);
                _ = Cache[unchecked((int)Pet)];   // filed on the landscape as a creature
                Check.True(Cache.GetLandscape().Any(w => w.Id == unchecked((int)Pet)), "rig: the pet is in the landscape cache");
            }
            PetManager.Clock = () => Now;
            Pets = new PetManager(host, S, Cache, combat, null);
        }

        private static void Place(uint id, string name, float x, float y, bool creature)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
            if (creature) FakeHost.ItemTypes[id] = 0x10;
        }

        /// <summary>Runs the heartbeat for <paramref name="ms"/> of fake time in 100 ms steps.</summary>
        public void Run(long ms)
        {
            for (long t = 0; t < ms; t += 100)
            {
                Now += 100;
                Pets.OnHeartbeat(0);
            }
        }

        public int Summons => FakeHost.Uses.Count(u => u == Essence);
        public int Refills => FakeHost.UsesOn.Count(u => u.Source == Spirit);

        public static void SetCharges(int charges) => FakeHost.Ints[(Essence, StructureKey)] = charges;

        public void Dispose()
        {
            PetManager.Clock = static () => Environment.TickCount64;
            FakeHost.ConfigureApi = null;
            ServerAges.Clear();
        }
    }

    private static void ServerAgeApi(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 83u);
        api.GetObjectServerAgeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&GetObjectServerAge;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetObjectServerAge(uint id) => ServerAges.TryGetValue(id, out int age) ? age : -1;

    private static bool Logged(string text) => FakeHost.Logs.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    // ── Tests ─────────────────────────────────────────────────────────────

    private static void StuckGestureGate()
    {
        using (var rig = new Rig(gates: true))
        {
            FakeHost.CastBusy = 1;          // the gate never opens again
            rig.Run(3_000);
            Check.Eq(rig.Summons, 0, "inside 4 s the closed gate holds the summon (a gesture may be animating)");
            rig.Run(3_000);
            Check.Eq(rig.Summons, 1, "closed past 4 s: taken as stuck, the summon goes (before: never again)");
            Check.True(Logged("taken as stuck"), "the stuck gate is logged");
            FakeHost.CastBusy = 0;
            rig.Run(200);
            Check.True(Logged("reads clear again"), "the gate opening again is logged");
        }

        using (var rig = new Rig(gates: true))
        {
            FakeHost.CastBusy = 1;          // a buff gesture
            rig.Run(2_000);
            Check.Eq(rig.Summons, 0, "a gesture animating: no summon");
            FakeHost.CastBusy = 0;
            rig.Run(1_500);
            Check.Eq(rig.Summons, 1, "the gesture ended: the summon goes");
            Check.False(Logged("taken as stuck"), "a short gesture is not called stuck");
        }

        using (var rig = new Rig(gates: true))
        {
            // Gestures back to back with the gate open in between, never 4 s closed in one go.
            for (int i = 0; i < 6; i++)
            {
                FakeHost.CastBusy = 1;
                rig.Run(900);
                FakeHost.CastBusy = 0;
                rig.Run(100);
                FakeHost.CastBusy = 1;
            }
            Check.False(Logged("taken as stuck"), "a gate that opens between gestures is never called stuck");
        }
    }

    private static void GhostPetIgnored()
    {
        using (var rig = new Rig(pet: true))
        {
            rig.Run(5_000);
            Check.Eq(rig.Summons, 0, "no server-age record (older engine): the pet counts as out, as before");
        }

        using (var rig = new Rig(serverAge: true, pet: true))
        {
            ServerAges[Pet] = 2_000;
            rig.Run(5_000);
            Check.Eq(rig.Summons, 0, "the server talked about the pet 2 s ago: it is out, no second summon");
            ServerAges[Pet] = -1;
            rig.Run(5_000);
            Check.Eq(rig.Summons, 0, "no record for the pet says nothing: it is out");
            ServerAges[Pet] = 90_000;
            rig.Run(1_500);
            Check.Eq(rig.Summons, 1, "silent for 90 s: a ghost, a new pet is summoned (before: never again)");
            Check.True(Logged("does not count as out") && Logged("0x80310020"), "the ghost is logged with its id");
            int lines = FakeHost.Logs.Count(l => l.Contains("does not count as out"));
            rig.Run(10_000);
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("does not count as out")), lines, "the ghost is logged once");
        }
    }

    private static void MissedDeleteIgnored()
    {
        using var rig = new Rig(pet: true);
        rig.Run(3_000);
        Check.Eq(rig.Summons, 0, "the pet is out: no summon");
        FakeHost.Names.Remove(Pet);       // the client deleted it; the cache never heard
        FakeHost.Positions.Remove(Pet);
        rig.Run(1_500);
        Check.Eq(rig.Summons, 1, "a pet the client no longer has does not hold summons");
        Check.True(Logged("client no longer has it"), "the missed delete is logged");
    }

    private static void AlreadyActiveTrustsPet()
    {
        using var rig = new Rig(serverAge: true, pet: true);
        ServerAges[Pet] = 90_000;           // quiet, so taken as a ghost
        rig.Run(1_500);
        Check.Eq(rig.Summons, 1, "the quiet pet is taken as a ghost: one summon");
        rig.Pets.OnChatWindowText("Pet Tester's Fire Moar is already active.");
        Check.True(Logged("already active"), "the server's answer is logged");
        rig.Run(4 * 60_000);
        Check.Eq(rig.Summons, 1, "the server has the pet: no Use every 45 s on top of it for 5 min");
        rig.Run(90_000);
        Check.Eq(rig.Summons, 2, "after 5 min the quiet pet is checked again");
    }

    private static void DeathLeavesPetBehind()
    {
        using var rig = new Rig(pet: true);   // no server-age API: only the death says the pet is gone
        rig.Run(3_000);
        Check.Eq(rig.Summons, 0, "the pet is out: no summon");
        rig.Pets.OnPlayerDeath();
        Check.True(Logged("died: 1 pet(s) out are left behind"), "the death is logged with the pet left behind");
        rig.Run(1_500);
        Check.Eq(rig.Summons, 1, "after the death the first fight summons at once");
        Check.True(Logged("left behind)") && Logged("0x80310020"), "the left-behind pet is logged with its id");
    }

    private static void DeathMidSummon()
    {
        using var rig = new Rig();
        rig.Run(1_500);
        Check.Eq(rig.Summons, 1, "first summon");
        rig.Pets.OnPlayerDeath();             // in flight: no pet, no charge drop yet
        Check.True(Logged("the summon state was cleared"), "the in-flight summon is dropped and logged");
        rig.Run(30_000);
        Check.Eq(rig.Summons, 1, "the server's 45 s essence cooldown still holds after the death");
        rig.Run(16_000);
        Check.Eq(rig.Summons, 2, "after the cooldown it summons again");
    }

    private static void PortalLeavesPetBehind()
    {
        using var rig = new Rig(pet: true);
        rig.Run(2_000);
        Check.Eq(rig.Summons, 0, "the pet is out: no summon");
        rig.Pets.OnPortal();
        Check.True(Logged("portal space: 1 pet(s) out are left behind"), "the portal is logged");
        rig.Run(1_500);
        Check.Eq(rig.Summons, 1, "after the portal the first fight summons");
        rig.Pets.OnChatWindowText("Pet Tester's Fire Moar is already active.");
        rig.Run(4 * 60_000);
        Check.Eq(rig.Summons, 1, "the server still has that pet: it counts as out again, no more Uses");
    }

    private static void NoRecordIsSilence()
    {
        using var rig = new Rig(serverAge: true, pet: true);
        ServerAges[Pet] = -1;                // an engine reload: no record for anything
        rig.Run(50_000);
        Check.Eq(rig.Summons, 0, "no record for 50 s: still taken as out");
        rig.Run(12_000);
        Check.Eq(rig.Summons, 1, "no record for 60 s of watching: silence, a new pet is summoned");
        Check.True(Logged("s of watching"), "logged as silence");
    }

    private static void RescanZeroRead()
    {
        using var rig = new Rig(spirit: true);
        rig.Run(1_500);
        Check.Eq(rig.Summons, 1, "50 charges: summon");
        Rig.SetCharges(0);                   // an inventory rescan: the property reads 0 for now
        rig.Run(46_000);
        Check.Eq(rig.Refills, 0, "no spirit spent on a read of 0 right after 50 (before: a refill)");
        Check.Eq(rig.Summons, 2, "it summons from the essence after the cooldown");
        Check.True(Logged("taken as a read in progress"), "the implausible read is logged");
    }

    private static void UnconfirmedEmptyTried()
    {
        using var rig = new Rig(charges: 0);   // no spirit, the read says empty from the start
        rig.Run(1_500);
        Check.Eq(rig.Summons, 1, "the server never called it empty: one summon is tried (before: parked every 15 s, never tried)");
        Check.True(Logged("the server hasn't called it empty"), "the try is logged");
        rig.Pets.OnChatWindowText("Your summoning device does not have enough charges to function!");
        rig.Run(120_000);
        Check.Eq(rig.Summons, 1, "confirmed empty: parked, no Use every 15 s");
        Rig.SetCharges(50);                  // refilled by hand
        rig.Run(2_000);
        Check.Eq(rig.Summons, 2, "charges again: it summons");
    }

    private static void VanishedEssenceReplaced()
    {
        using var rig = new Rig(replacement: true);
        FakeHost.Names.Remove(Essence);       // dropped on a corpse the client let go; the cache still has it
        FakeHost.Containers.Remove(Essence);  // (container reads 0, which read as "in the pack")
        rig.Run(1_500);
        Check.Eq(rig.Summons, 0, "the vanished essence is not used");
        Check.Eq(FakeHost.Uses.Count(u => u == Essence2), 1, "its same-named successor in the pack is");
    }
}
