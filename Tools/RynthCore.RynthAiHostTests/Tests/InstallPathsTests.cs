using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Install;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// RynthInstallPaths (Plugins/Shared/RynthInstallPaths.cs, compiled into RynthAi): where RynthSuite
// lives. Order is environment, HKCU, HKLM, then C:\Games\RynthSuite. The environment and the
// registry are a fake here; the real registry is only READ, for a value that does not exist.
// Idea from Silentkelpie (rynth PR #1); re-implemented here.
internal static class InstallPathsTests
{
    public static void Register(Runner r)
    {
        r.Add("install paths: nothing set gives C:\\Games\\RynthSuite and C:\\Games\\RynthCore", NothingSet);
        r.Add("install paths: environment beats HKCU beats HKLM beats the default", Order);
        r.Add("install paths: empty, relative and broken values are skipped", Unusable);
        r.Add("install paths: folders with spaces", Spaces);
        r.Add("install paths: a source that throws falls through to the default", Throwing);
        r.Add("install paths: real registry, missing value reads as null (read only)", RealRegistryRead);
        r.Add("install paths: on a default install RynthAi's folders are exactly the old ones", DefaultInstallUnchanged);
        r.Add("install paths: same file as RynthCore src\\Shared (when the sibling is checked out)", SameAsRynthCore);
    }

    private sealed class Fake : IInstallSettingSource
    {
        public readonly Dictionary<string, string?> Env = new();
        public readonly Dictionary<string, string?> User = new();
        public readonly Dictionary<string, string?> Machine = new();
        public int Asked;
        public bool Throw;

        public string? EnvironmentVariable(string name) { Asked++; if (Throw) throw new InvalidOperationException(); return Env.GetValueOrDefault(name); }
        public string? CurrentUser(string valueName) { Asked++; if (Throw) throw new UnauthorizedAccessException(); return User.GetValueOrDefault(valueName); }
        public string? LocalMachine(string valueName) { Asked++; if (Throw) throw new IOException(); return Machine.GetValueOrDefault(valueName); }
    }

    private static string Suite(Fake f) => RynthInstallPaths.Resolve(RynthInstallPaths.SuiteEnvironmentVariable,
        RynthInstallPaths.SuiteValueName, RynthInstallPaths.DefaultSuiteDir, f);

    private static string Core(Fake f) => RynthInstallPaths.Resolve(RynthInstallPaths.CoreEnvironmentVariable,
        RynthInstallPaths.CoreValueName, RynthInstallPaths.DefaultCoreDir, f);

    private static void NothingSet()
    {
        var f = new Fake();
        Check.Eq(Suite(f), @"C:\Games\RynthSuite", "suite default");
        Check.Eq(Core(f), @"C:\Games\RynthCore", "core default");
        Check.Eq(f.Asked, 6, "env, HKCU and HKLM asked for each folder");
    }

    private static void Order()
    {
        var f = new Fake();
        f.Machine["SuiteDir"] = @"D:\Machine\RynthSuite";
        Check.Eq(Suite(f), @"D:\Machine\RynthSuite", "HKLM beats the default");
        f.User["SuiteDir"] = @"E:\User\RynthSuite";
        Check.Eq(Suite(f), @"E:\User\RynthSuite", "HKCU beats HKLM");
        f.Env["RYNTHSUITE_DIR"] = @"F:\Dev\RynthSuite";
        Check.Eq(Suite(f), @"F:\Dev\RynthSuite", "environment beats HKCU");
        Check.Eq(Core(f), @"C:\Games\RynthCore", "suite settings leave the core folder alone");
    }

    private static void Unusable()
    {
        foreach (string? bad in new[] { null, "", "  ", "RynthSuite", @"..\RynthSuite", "C:", "D:\\a|b", "D:\\a*", "D:\\x:y" })
        {
            var f = new Fake();
            f.Env["RYNTHSUITE_DIR"] = bad;
            f.User["SuiteDir"] = bad;
            f.Machine["SuiteDir"] = bad;
            Check.Eq(Suite(f), @"C:\Games\RynthSuite", $"'{bad}' everywhere -> default");
        }
    }

    private static void Spaces()
    {
        var f = new Fake();
        f.User["SuiteDir"] = @"C:\Program Files (x86)\Rynth Suite\";
        Check.Eq(Suite(f), @"C:\Program Files (x86)\Rynth Suite", "HKCU value with spaces, trailing separator dropped");
        Check.Eq(Path.Combine(Suite(f), "RynthAi", "LootProfiles"), @"C:\Program Files (x86)\Rynth Suite\RynthAi\LootProfiles", "joined");
        var g = new Fake();
        g.Env["RYNTHSUITE_DIR"] = "  \"D:\\My Games\\Rynth Suite\"  ";
        Check.Eq(Suite(g), @"D:\My Games\Rynth Suite", "quoted, padded environment value with spaces");
    }

    private static void Throwing()
    {
        var f = new Fake { Throw = true };
        Check.Eq(Suite(f), @"C:\Games\RynthSuite", "every source throws -> default");
        Check.Eq(f.Asked, 3, "all three tried");
    }

    private static void RealRegistryRead()
    {
        string missing = "NoSuchValue-" + Guid.NewGuid().ToString("N");
        Check.Null(SystemInstallSettings.Instance.CurrentUser(missing), "HKCU missing value");
        Check.Null(SystemInstallSettings.Instance.LocalMachine(missing), "HKLM missing value");
        Check.True(Path.IsPathFullyQualified(RynthInstallPaths.SuiteDir), "live SuiteDir is absolute: " + RynthInstallPaths.SuiteDir);
    }

    private static void DefaultInstallUnchanged()
    {
        if (RynthInstallPaths.SuiteDir != RynthInstallPaths.DefaultSuiteDir)
        {
            Console.WriteLine($"    (this machine sets the RynthSuite folder to {RynthInstallPaths.SuiteDir}; default-install check skipped)");
            return;
        }
        Check.Eq(RynthInstallPaths.RynthAiDir, @"C:\Games\RynthSuite\RynthAi", "RynthAi folder");
        Check.Eq(RynthCore.Plugin.RynthAi.Loot.LootEditorBridge.LootFolder, @"C:\Games\RynthSuite\RynthAi\LootProfiles", "loot profiles");
        Check.Eq(RynthCore.Plugin.RynthAi.Vendor.AutoVendorManager.MainProfileDir, @"C:\Games\RynthSuite\RynthAi\AutoVendor", "AutoVendor");
        Check.Eq(RynthCore.Plugin.RynthAi.Trade.AutoTradeManager.MainProfileDir, @"C:\Games\RynthSuite\RynthAi\AutoTrade", "AutoTrade");
        Check.Eq(RynthCore.Plugin.RynthAi.CreatureData.CreatureWeakness.WeaknessFile, @"C:\Games\RynthSuite\RynthAi\Data\creature_weakness.json", "weakness file");
        Check.Eq(PrivateStatic(typeof(RynthCore.Plugin.RynthAi.RadarWallRenderer), "ExploredDir"), @"C:\Games\RynthSuite\RynthAi\ExploredDungeons", "explored dungeons");
        // The tests point the store at their scratch folder (Program.Main); this is its value before that.
        Check.Eq(Program.LiveHazardFolder, @"C:\Games\RynthSuite\RynthAi\DungeonHazards", "dungeon hazards");
        Check.Eq(Program.LiveHazardTextureFolder, @"C:\Games\RynthSuite\RynthAi", "hazard floor textures (HazardTextures.txt)");
        Check.Eq(Program.LiveCreatureFolder, @"C:\Games\RynthSuite\RynthAi\CreatureData", "creature data");
    }

    private static string? PrivateStatic(Type type, string field)
        => type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string;

    private static void SameAsRynthCore()
    {
        string here = Path.GetFullPath(Path.Combine(ThisFile(), "..", "..", "..", "..", "Plugins", "Shared", "RynthInstallPaths.cs"));
        string sibling = Path.GetFullPath(Path.Combine(ThisFile(), "..", "..", "..", "..", "..", "RynthCore", "src", "Shared", "RynthInstallPaths.cs"));
        Check.True(File.Exists(here), "RynthSuite copy found at " + here);
        if (!File.Exists(sibling))
        {
            Console.WriteLine($"    (no RynthCore sibling at {sibling}; comparison skipped)");
            return;
        }
        Check.True(Text(here) == Text(sibling),
            "Plugins\\Shared\\RynthInstallPaths.cs matches RynthCore src\\Shared\\RynthInstallPaths.cs");
    }

    private static string Text(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
