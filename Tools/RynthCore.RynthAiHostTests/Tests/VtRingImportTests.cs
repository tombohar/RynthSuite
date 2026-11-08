using System;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.RynthAi.ProfileImport;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// A VTank .usd import brings VTank's ring options along with the monsters' Ring ticks (2026-11-06):
// MinimumRingTargets -> Min Ring Targets, RingDistance (landblock units, x240 = yards, as every
// VTank distance RynthAi reads) -> Ring Range, each held to the settings window's range. A value
// the profile lacks leaves the setting alone. Only an import the player starts sets them.
// Fixtures\vtank-rings*.usd: a MyMonsters table (DEFAULT with Ring on) and then the options.
internal static class VtRingImportTests
{
    private const uint Player = 0x50000D01;

    public static void Register(Runner r)
    {
        r.Add("vtank ring import: the options read, converted (landblock units x240) and held to the settings' range", ReadAndConvert);
        r.Add("vtank ring import (Hub): preview lists them, the import sets them and says so; none in the profile = left alone", HubImport);
        r.Add("vtank ring import (profile importer): set when the profiles agree, left alone when they differ or have none", ProfileImporter);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static void ReadAndConvert()
    {
        var r = VtRingSettings.Read(File.ReadAllLines(Fixture("vtank-rings.usd")));
        Check.Eq(r.MinRingTargets, 2, "MinimumRingTargets 2");
        Check.Eq(r.RingRange, 4, "RingDistance 0.0166667 landblock = 4 yards");
        Check.Eq(r.Describe(), "Min Ring Targets 2, Ring Range 4 (from the profile)", "the summary");
        Check.Eq(r.Clamped.Count, 0, "nothing held");

        var none = VtRingSettings.Read(File.ReadAllLines(Fixture("vtank-rings-none.usd")));
        Check.False(none.Any, "a profile without them: none (AttackDistance and the rest are not ring options)");
        Check.Eq(none.Describe(), "", "nothing to say");

        var clamp = VtRingSettings.Read(File.ReadAllLines(Fixture("vtank-rings-clamp.usd")));
        Check.Eq(clamp.MinRingTargets, 20, "MinimumRingTargets 30 held to 20");
        Check.Eq(clamp.RingRange, 50, "RingDistance 0.3 = 72 yards, held to 50");
        Check.Eq(string.Join("; ", clamp.Clamped), "Min Ring Targets 30 -> 20; Ring Range 72 -> 50", "the holds are noted");

        var one = VtRingSettings.Read(File.ReadAllLines(Fixture("vtank-rings-mintargets-only.usd")));
        Check.Eq(one.MinRingTargets, 1, "MinimumRingTargets 0 held to 1");
        Check.Eq(one.RingRange, null, "no RingDistance: no Ring Range");

        // The conversion is the meta path's: a meta's RingDistance gives the same Ring Range.
        string[] Lines(string d) => new[] { "s", "RingDistance", "d", d };
        Check.Eq(VtRingSettings.Read(Lines("0.0208333333333333")).RingRange, 5, "5/240 landblock = 5");
        Check.Eq(VtRingSettings.Read(Lines("0.00833333333333333")).RingRange, 2, "2/240 landblock = 2");
        Check.Eq(VtRingSettings.Read(Lines("0.0188333333333333")).RingRange, 5, "4.52 yards rounds to 5");
        Check.Eq(VtRingSettings.Read(Lines("7")).RingRange, 7, "a value over 1 is yards already (typed by hand), as the meta path reads it");
        Check.Eq(VtRingSettings.Read(Lines("0")).RingRange, 1, "0 held to 1");
        Check.Eq(VtRingSettings.Read(new[] { "x", "RingDistance", "d", "0.02" }).RingRange, null, "only an option row (an 's' line before the name)");
    }

    private sealed class HubRig
    {
        public readonly LegacyUiSettings S = new() { MinRingTargets = 4, RingRange = 5 };
        public readonly VTankUsdImporter Usd;
        public int Saves;

        public HubRig()
        {
            FakeHost.Reset();
            var host = FakeHost.Create(Player);
            string dir = Path.Combine(Program.TempRoot, "vtank-ring-import");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            var store = new MiniRemoteStore(dir);
            var cache = FakeHost.MakeCache(host, Player, Array.Empty<uint>());
            var ctx = new MiniRemoteContext(host, store.Load(), store, new MiniRemoteInventory(host, () => cache),
                                            new ServerHubClient(host), () => S)
            { SaveCombatSettings = () => Saves++ };
            Usd = new VTankUsdImporter(ctx);
        }
    }

    private static void HubImport()
    {
        var rig = new HubRig();
        rig.Usd.Preview(Fixture("vtank-rings.usd"));
        Check.True(rig.Usd.Status.Contains("ring settings: Min Ring Targets 2 (now 4), Ring Range 4 (now 5)"), $"the preview lists them: {rig.Usd.Status}");
        Check.Eq((rig.S.MinRingTargets, rig.S.RingRange), (4, 5), "a preview changes nothing");
        rig.Usd.Apply();
        Check.Eq((rig.S.MinRingTargets, rig.S.RingRange), (2, 4), "the import sets them");
        Check.True(rig.Usd.Status.Contains("set Min Ring Targets 2, Ring Range 4 (from the profile)"), $"and says so: {rig.Usd.Status}");
        Check.True(FakeHost.Chat.Any(c => c.Contains("Min Ring Targets 2, Ring Range 4 (from the profile)")), "in the chat too");
        Check.True(rig.S.MonsterRules.Any(m => m.UseRing), "the monsters' Ring ticks came along");
        Check.Eq(rig.Saves, 1, "saved once");

        var none = new HubRig();
        none.S.MinRingTargets = 3; none.S.RingRange = 9;
        none.Usd.Preview(Fixture("vtank-rings-none.usd"));
        Check.True(none.Usd.Status.Contains("no ring settings in the profile"), $"the preview says there are none: {none.Usd.Status}");
        none.Usd.Apply();
        Check.Eq((none.S.MinRingTargets, none.S.RingRange), (3, 9), "none in the profile: left alone");
        Check.True(none.Usd.Status.Contains("ring settings left alone"), $"and says so: {none.Usd.Status}");

        var half = new HubRig();
        half.Usd.Preview(Fixture("vtank-rings-mintargets-only.usd"));
        half.Usd.Apply();
        Check.Eq((half.S.MinRingTargets, half.S.RingRange), (1, 5), "only Min Ring Targets in the profile: Ring Range left alone");

        var clamp = new HubRig();
        clamp.Usd.Preview(Fixture("vtank-rings-clamp.usd"));
        clamp.Usd.Apply();
        Check.Eq((clamp.S.MinRingTargets, clamp.S.RingRange), (20, 50), "held to the settings' range");
    }

    private static void ProfileImporter()
    {
        var s = new LegacyUiSettings { MinRingTargets = 4, RingRange = 5 };
        var result = new VirindiTankProfileImporter.Result();
        VirindiTankProfileImporter.TryMergeUsdMyMonstersScf(Fixture("vtank-rings.usd"), s, result);
        Check.Eq((s.MinRingTargets, s.RingRange), (4, 5), "reading a profile changes nothing by itself");
        VirindiTankProfileImporter.ApplyRingSettings(s, result);
        Check.Eq((s.MinRingTargets, s.RingRange), (2, 4), "one profile: its values");
        Check.Eq(result.RingSettingsSet, "Min Ring Targets 2, Ring Range 4 (from the profile)", "the summary");
        Check.True(result.LogLines.Any(l => l.Contains("Ring settings: set Min Ring Targets 2, Ring Range 4")), "the import's lines say it");

        // Two profiles (two characters) that disagree: neither wins.
        var s2 = new LegacyUiSettings { MinRingTargets = 4, RingRange = 5 };
        var r2 = new VirindiTankProfileImporter.Result();
        VirindiTankProfileImporter.TryMergeUsdMyMonstersScf(Fixture("vtank-rings.usd"), s2, r2);
        VirindiTankProfileImporter.TryMergeUsdMyMonstersScf(Fixture("vtank-rings-clamp.usd"), s2, r2);
        VirindiTankProfileImporter.TryMergeUsdMyMonstersScf(Fixture("vtank-rings-none.usd"), s2, r2);
        VirindiTankProfileImporter.ApplyRingSettings(s2, r2);
        Check.Eq((s2.MinRingTargets, s2.RingRange), (4, 5), "profiles that differ: left alone");
        Check.True(r2.LogLines.Any(l => l.Contains("the profiles differ") && l.Contains("left alone")), "and it says so");
        Check.Eq(r2.RingSettingsSet, "", "nothing set");

        // Only profiles without ring options: nothing found, nothing said or set.
        var s3 = new LegacyUiSettings { MinRingTargets = 3, RingRange = 7 };
        var r3 = new VirindiTankProfileImporter.Result();
        VirindiTankProfileImporter.TryMergeUsdMyMonstersScf(Fixture("vtank-rings-none.usd"), s3, r3);
        VirindiTankProfileImporter.ApplyRingSettings(s3, r3);
        Check.Eq((s3.MinRingTargets, s3.RingRange), (3, 7), "none in the profiles: left alone");
        Check.False(r3.LogLines.Any(l => l.StartsWith("Ring settings")), "nothing to say");
    }
}
