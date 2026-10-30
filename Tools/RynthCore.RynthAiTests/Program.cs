using RynthCore.RynthAiTests.Harness;
using RynthCore.RynthAiTests.Tests;

namespace RynthCore.RynthAiTests;

// Test runner for RynthAi's pure logic: no game, no engine, no host. Each test prints
// PASS / FAIL (or KNOWN for a registered known bug); the exit code is 1 on any failure.
// README.md lists the seams for the areas that aren't reachable yet.
//
// Run: dotnet run -c Release                 (all tests)
//      dotnet run -c Release -- weakness     (tests whose name contains "weakness")
internal static class Program
{
    private static int Main(string[] args)
    {
        var runner = new Runner();
        NavRouteParserTests.Register(runner);
        NavFormatTests.Register(runner);
        CombatStallWatchdogTests.Register(runner);
        CreatureWeaknessTests.Register(runner);
        MonsterDamageTierTests.Register(runner);
        LootBaselineTests.Register(runner);
        ChatClassifierTests.Register(runner);
        return runner.Run("RynthAi tests", args.Length > 0 ? args[0] : null);
    }
}
