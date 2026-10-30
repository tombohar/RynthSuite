using System;
using System.Collections.Generic;

namespace RynthCore.RynthAiTests.Harness;

// Runs registered tests one by one and prints a PASS / FAIL line per test (with the failed
// checks under it). A test fails when any Check fails, when it throws, or when it made no
// checks at all (a test that asserts nothing proves nothing).
//
// Known failures: a test that exposes a real, unfixed bug is registered with KnownFailure and
// a reason. It still runs; while it fails it prints KNOWN and does not fail the run. If it
// starts passing it prints XPASS and DOES fail the run, so the marker gets removed with the fix.
internal sealed class Runner
{
    private readonly List<(string Name, Action Body, string? KnownReason)> _tests = new();

    public void Add(string name, Action body) => _tests.Add((name, body, null));

    public void KnownFailure(string name, string reason, Action body) => _tests.Add((name, body, reason));

    /// <summary>Runs every test whose name contains <paramref name="filter"/> (all when empty). Returns the exit code.</summary>
    public int Run(string title, string? filter)
    {
        Console.WriteLine($"=== {title} ===");
        int ran = 0, passed = 0, failed = 0, known = 0, checks = 0;

        foreach (var (name, body, knownReason) in _tests)
        {
            if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            ran++;
            Check.Reset();
            string? crash = null;
            try { body(); }
            catch (Exception ex) { crash = $"threw {ex.GetType().Name}: {ex.Message}"; }

            var fails = new List<string>(Check.Failures);
            if (crash != null) fails.Add(crash);
            if (crash == null && Check.Count == 0) fails.Add("made no checks");
            checks += Check.Count;
            bool ok = fails.Count == 0;

            if (knownReason == null)
            {
                if (ok) { passed++; Console.WriteLine($"[PASS]  {name}"); }
                else { failed++; Console.WriteLine($"[FAIL]  {name}"); Print(fails); }
            }
            else if (!ok)
            {
                known++;
                Console.WriteLine($"[KNOWN] {name} - known failure: {knownReason}");
                Print(fails);
            }
            else
            {
                failed++;
                Console.WriteLine($"[XPASS] {name} - marked as a known failure but passed; remove the KnownFailure marker ({knownReason})");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{ran} test(s), {checks} check(s): {passed} passed, {failed} failed, {known} known failure(s).");
        if (ran == 0)
        {
            Console.WriteLine(string.IsNullOrEmpty(filter) ? "ABORT: no tests registered." : $"ABORT: no test matches '{filter}'.");
            return 1;
        }
        Console.WriteLine(failed == 0 ? "ALL RYNTHAI TESTS PASSED." : $"{failed} FAILURE(S).");
        return failed == 0 ? 0 : 1;
    }

    private static void Print(List<string> fails)
    {
        foreach (string f in fails) Console.WriteLine($"          - {f}");
    }
}
