using System;
using System.Collections.Generic;

namespace RynthCore.RynthAiTests.Harness;

// Tiny assertion helper, in the style of the other RynthSuite test tools: a failed check is
// recorded against the running test and the test carries on, so one run prints every broken
// expectation instead of stopping at the first. The runner reads Failures after each test.
internal static class Check
{
    [ThreadStatic] private static List<string>? _failures;
    [ThreadStatic] private static int _count;

    /// <summary>Checks recorded by the current test so far.</summary>
    public static int Count => _count;
    public static IReadOnlyList<string> Failures => _failures ??= new List<string>();

    internal static void Reset()
    {
        _failures = new List<string>();
        _count = 0;
    }

    private static void Fail(string msg) => (_failures ??= new List<string>()).Add(msg);

    public static void True(bool cond, string msg)
    {
        _count++;
        if (!cond) Fail(msg);
    }

    public static void False(bool cond, string msg) => True(!cond, msg);

    public static void Eq<T>(T actual, T expected, string msg)
    {
        _count++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            Fail($"{msg}: expected <{expected}>, got <{actual}>");
    }

    public static void Near(double actual, double expected, double tol, string msg)
    {
        _count++;
        if (double.IsNaN(actual) || Math.Abs(actual - expected) > tol)
            Fail($"{msg}: expected {expected:0.####} ±{tol}, got {actual:0.####}");
    }

    public static void NotNull(object? value, string msg)
    {
        _count++;
        if (value is null) Fail($"{msg}: was null");
    }

    public static void Null(object? value, string msg)
    {
        _count++;
        if (value is not null) Fail($"{msg}: expected null, got <{value}>");
    }

    public static void Throws<TEx>(Action body, string msg) where TEx : Exception
    {
        _count++;
        try { body(); }
        catch (TEx) { return; }
        catch (Exception ex) { Fail($"{msg}: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); return; }
        Fail($"{msg}: expected {typeof(TEx).Name}, nothing was thrown");
    }
}
