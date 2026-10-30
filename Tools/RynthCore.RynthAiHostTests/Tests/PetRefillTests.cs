using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Pet essence refill loop (2026-10-05; spotted by Silentkelpie, rynth PR #1). PetManager decides
// "empty, refill it" from the essence's Structure read alone. When that read is stale (0 while the
// server holds a full essence), the server answers "This essence is already full." and PetManager
// went idle, read 0 again a second later and refilled again: a refill every couple of seconds,
// each one forcing peace mode and a clap (ACE PetDevice refill), for as long as the read stayed
// stale. A confirmed refill ("You add the spirit to the essence.") with a read that stays 0 burned
// a spirit every time. Now the server's reply wins over the read until a summon says the essence
// really is empty, the essence is re-identified after a refill, and an essence refilled twice in
// 2 minutes that still reads empty is parked for 5 minutes.
internal static class PetRefillTests
{
    private const uint Player = 0x50000D01;
    private const uint Mob = 0x80300010;
    private const uint Essence = 0x80300001;
    private const uint Spirit = 0x80300002;
    private const uint Essence2 = 0x80300003;   // a second essence, only for the assume-window test
    private const uint StructureKey = 92;   // AcIntProperty.Structure
    private const uint Lb = 0xA9B40000;

    private const string AlreadyFull = "This essence is already full.";
    private const string Added = "You add the spirit to the essence.";
    private const string NoCharges = "Your summoning device does not have enough charges to function!";

    public static void Register(Runner r)
    {
        r.Add("pets: a charged essence summons; an empty one is refilled once, then summons", NormalPath);
        r.Add("pets: stale empty read + 'already full' -> summon, not another refill (the loop)", AlreadyFullLoop);
        r.Add("pets: confirmed refill with a read that stays empty does not burn more spirits", AddedButStale);
        r.Add("pets: 'not enough charges' after a confirmed refill ends the trust; refill again", TrustEndsOnEmptySummon);
        r.Add("pets: refilled twice in 2 min and still empty (no reply) -> parked 5 min", SilentRefillsParked);
        r.Add("pets: the server's 'full' trust expires after 30 min", TrustExpires);
        r.Add("pets: a landed summon whose pet is not in the cache yet holds a second essence back for the 10 s window", LandedSummonHoldsWindow);
        r.Add("pets: failed summon (no charge drop) does not assume a pet; waits the 45 s essence cooldown", FailedSummonRespectsCooldown);
    }

    // ── Rig ───────────────────────────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public long Now = 1_000_000;
        public readonly LegacyUiSettings S = new() { SummonPets = true, MonsterRange = 20, CustomPetRange = 5, PetAutoRefill = true };
        public readonly PetManager Pets;

        public Rig(int charges, bool secondEssence = false)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;   // UseObject (summon), RequestId (re-identify)
            FakeHost.UseOnCalls = true;    // UseObjectOn (spirit on essence)
            Place(Player, "Pet Tester", 96, 96, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            Place(Mob, "Drudge Skulker", 96, 99, creature: true);   // 3 m north
            FakeHost.Names[Essence] = "Fire Moar Essence (50)";
            FakeHost.Containers[Essence] = Player;
            FakeHost.Names[Spirit] = "Encapsulated Spirit";
            FakeHost.Containers[Spirit] = Player;
            SetCharges(charges);
            if (secondEssence)
            {
                FakeHost.Names[Essence2] = "Frost Moar Essence (50)";
                FakeHost.Containers[Essence2] = Player;
                FakeHost.Ints[(Essence2, StructureKey)] = 50;
            }

            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, secondEssence ? new[] { Mob, Essence, Spirit, Essence2 } : new[] { Mob, Essence, Spirit });
            S.IsMacroRunning = true;
            S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Essence), Name = "Fire Moar Essence (50)", Type = "Pet" });
            if (secondEssence)
                S.ConsumableRules.Add(new ConsumableRule { Id = unchecked((int)Essence2), Name = "Frost Moar Essence (50)", Type = "Pet" });
            var combat = new CombatManager(host, S, cache);
            combat.SetPlayerId(Player);
            combat.ScanNearbyTargets();
            Check.True(combat.ScannedTargets.Count == 1, "rig: the monster is scanned");
            PetManager.Clock = () => Now;
            Pets = new PetManager(host, S, cache, combat, null);
        }

        private static void Place(uint id, string name, float x, float y, bool creature)
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
            if (creature) FakeHost.ItemTypes[id] = 0x10;
        }

        public static void SetCharges(int charges)
        {
            if (charges < 0) FakeHost.Ints.Remove((Essence, StructureKey));
            else FakeHost.Ints[(Essence, StructureKey)] = charges;
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

        public void Chat(string line) => Pets.OnChatWindowText(line);

        public int Refills => FakeHost.UsesOn.Count(u => u.Source == Spirit && u.Target == Essence);
        public int Summons => FakeHost.Uses.Count(u => u == Essence);
        public int AllSummons => FakeHost.Uses.Count(u => u == Essence || u == Essence2);

        public void Dispose() => PetManager.Clock = static () => Environment.TickCount64;
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    private static void NormalPath()
    {
        using (var rig = new Rig(charges: 50))
        {
            rig.Run(1500);
            Check.Eq(rig.Summons, 1, "a charged essence: one summon");
            Check.Eq(rig.Refills, 0, "a charged essence: no refill");
        }

        using (var rig = new Rig(charges: 0))
        {
            rig.Run(1500);
            Check.Eq(rig.Refills, 1, "an empty essence: one refill");
            Check.Eq(rig.Summons, 0, "an empty essence: no summon yet");
            Rig.SetCharges(50);           // the server's Structure update lands
            rig.Chat(Added);
            rig.Run(1500);
            Check.Eq(rig.Refills, 1, "after the refill: still one refill");
            Check.Eq(rig.Summons, 1, "after the refill: it summons");
            Check.True(FakeHost.IdRequests.Contains(Essence), "the essence is re-identified after a refill");
        }
    }

    private static void AlreadyFullLoop()
    {
        using var rig = new Rig(charges: 0);   // the read says empty and never changes
        rig.Run(1500);
        Check.Eq(rig.Refills, 1, "the empty read starts one refill");
        for (int i = 0; i < 10; i++)
        {
            rig.Chat(AlreadyFull);              // the server: it was full all along
            rig.Run(2000);
        }
        Check.Eq(rig.Refills, 1, "the server said full: no further refills over 20 s");
        Check.True(rig.Summons >= 1, "it summons from the essence the server called full");
        Check.True(FakeHost.IdRequests.Contains(Essence), "the stale essence is re-identified");
        Check.True(FakeHost.Logs.Any(l => l.Contains("already full", StringComparison.OrdinalIgnoreCase)), "the stale read is logged");
    }

    private static void AddedButStale()
    {
        using var rig = new Rig(charges: 0);
        rig.Run(1500);
        for (int i = 0; i < 5; i++)
        {
            rig.Chat(Added);                    // each reply would be a spirit spent
            rig.Run(2000);
        }
        Check.Eq(rig.Refills, 1, "one spirit spent, not one per think tick");
        Check.True(rig.Summons >= 1, "it summons after the confirmed refill");
    }

    private static void TrustEndsOnEmptySummon()
    {
        using var rig = new Rig(charges: 0);
        rig.Run(1500);
        rig.Chat(AlreadyFull);
        rig.Run(1500);
        Check.Eq(rig.Summons, 1, "trusting the server, it summons");
        rig.Chat(NoCharges);                    // the summon says it really is empty
        rig.Run(1500);
        Check.Eq(rig.Refills, 2, "really empty: refilled again");
    }

    private static void SilentRefillsParked()
    {
        using var rig = new Rig(charges: 0);   // no reply ever arrives, the read stays 0
        rig.Run(10 * 60_000);
        // Before: a refill every ~38 s (8 s timeout + 30 s park) = about 16 in 10 minutes.
        // Now: two refills, a 5 minute park, two refills, a park.
        Check.True(rig.Refills <= 4, $"at most 4 refills in 10 minutes (got {rig.Refills})");
        Check.True(rig.Refills >= 2, $"it still tries to refill (got {rig.Refills})");
        Check.True(FakeHost.Logs.Any(l => l.Contains("parking", StringComparison.OrdinalIgnoreCase)), "the park is logged");
    }

    private static void TrustExpires()
    {
        using var rig = new Rig(charges: 0);
        rig.Run(1500);
        rig.Chat(AlreadyFull);
        rig.Run(1500);
        int refills = rig.Refills;
        Check.Eq(refills, 1, "trusted: no second refill");
        // No summon lands (no pet appears, the read never moves): each summon times out and
        // parks the essence for the 45 s PetDevice cooldown. After 30 minutes the trust is gone.
        rig.Run(31 * 60_000);
        Check.True(rig.Refills > refills, "after 30 minutes the empty read is believed again");
    }

    /// <summary>
    /// Kelpie 2026-10-16: Use refused (cooldown / environment) leaves Structure unchanged.
    /// Must not assume a pet is out, and must wait the full ~45 s essence cooldown before retry.
    /// </summary>
    private static void FailedSummonRespectsCooldown()
    {
        using var rig = new Rig(charges: 50);
        rig.Run(1500);
        Check.Eq(rig.Summons, 1, "first Use fired");
        // Structure never drops and no pet appears → timeout treats it as a failed Use.
        Rig.SetCharges(50);
        rig.Run(9_000);
        Check.Eq(rig.Summons, 1, "still one Use during the 45 s cooldown (no false re-issue)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("failed Use", StringComparison.OrdinalIgnoreCase)
                                          || l.Contains("no charge drop", StringComparison.OrdinalIgnoreCase)),
            "failed Use is logged");
        // Park is from IssueSummon (~t=100 ms), not from the 8 s timeout. At ~44 s from start
        // still cooling; a few seconds more clears the 45 s essence cooldown.
        rig.Run(34_000);
        Check.Eq(rig.Summons, 1, "still cooling at ~44 s from first Use");
        rig.Run(4_000);
        Check.Eq(rig.Summons, 2, "second Use after the 45 s essence cooldown");
    }

    /// <summary>
    /// The charge dropped (the Use landed) but the pet is not in the landscape cache yet: the assume
    /// window must keep a second essence from being summoned beside it, and lapse after 10 s.
    /// </summary>
    private static void LandedSummonHoldsWindow()
    {
        using var rig = new Rig(charges: 50, secondEssence: true);
        rig.Run(1500);
        Check.Eq(rig.AllSummons, 1, "first Use fired");
        Rig.SetCharges(49);
        FakeHost.Ints[(Essence2, StructureKey)] = 49;     // whichever essence it was, its charge dropped
        rig.Run(8_000);
        Check.Eq(rig.AllSummons, 1, "inside the 10 s window the other essence is not used");
        rig.Run(5_000);
        Check.Eq(rig.AllSummons, 2, "window over and still no pet: the other essence is used");
    }
}
