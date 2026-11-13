using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;
using DF = RynthCore.Plugin.RynthAi.DirtyFightingHeights;

namespace RynthCore.RynthAiTests.Tests;

// The Dirty Fighting rotation's height choice (Combat/DirtyFightingHeights.cs): each attack aims at the
// first height in the order whose effect is missing or about to run out, then at the default height once
// all three are on; off, or with Dirty Fighting untrained, the fixed Attack Height. Time is passed in.
internal static class DirtyFightingHeightTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private const int FixedMedium = DF.Medium;

    public static void Register(Runner r)
    {
        r.Add("dfheight: a fresh target gets the first height in the order", FreshTarget);
        r.Add("dfheight: partly debuffed goes to the next missing height", PartlyDebuffed);
        r.Add("dfheight: all three on uses the default height", AllOnDefault);
        r.Add("dfheight: an effect near its end is re-applied", ExpiryReapplies);
        r.Add("dfheight: untrained or off keeps the fixed height", UntrainedFixed);
        r.Add("dfheight: other orders and bad order text", Orders);
        r.Add("dfheight: landing lines are parsed, others' and chat are not", ParseLanding);
        r.Add("dfheight: chance per hit", Chance);
        r.Add("dfheight: tracker keeps targets apart and copies", Tracker);
    }

    private static int Pick(DfTargetEffects? fx, DateTime now, string order = "HML", int def = DF.DefaultIsFixedHeight,
        bool on = true, bool trained = true, int fixedHeight = FixedMedium)
        => DF.Choose(on, trained, fixedHeight, order, def, fx, now);

    private static void FreshTarget()
    {
        Check.Eq(Pick(null, T0), DF.High, "nothing known about it: High first");
        Check.Eq(Pick(new DfTargetEffects(), T0), DF.High, "an empty record: High first");
    }

    private static void PartlyDebuffed()
    {
        var fx = new DfTargetEffects();
        fx.Landed(DF.High, T0);
        Check.Eq(Pick(fx, T0.AddSeconds(1)), DF.Medium, "High on: Medium (the bleed) next");
        fx.Landed(DF.Medium, T0.AddSeconds(2));
        Check.Eq(Pick(fx, T0.AddSeconds(3)), DF.Low, "High and Medium on: Low next");

        var onlyLow = new DfTargetEffects();
        onlyLow.Landed(DF.Low, T0);
        Check.Eq(Pick(onlyLow, T0.AddSeconds(1)), DF.High, "only Low on: back to High, the first missing");
        onlyLow.Landed(DF.High, T0.AddSeconds(1));
        Check.Eq(Pick(onlyLow, T0.AddSeconds(2)), DF.Medium, "High and Low on: Medium");
    }

    private static void AllOnDefault()
    {
        var fx = new DfTargetEffects();
        fx.Landed(DF.High, T0);
        fx.Landed(DF.Medium, T0);
        fx.Landed(DF.Low, T0);
        DateTime now = T0.AddSeconds(2);
        Check.Eq(Pick(fx, now, def: DF.Low), DF.Low, "all on, default Low: Low");
        Check.Eq(Pick(fx, now, def: DF.High), DF.High, "all on, default High: High");
        Check.Eq(Pick(fx, now, def: DF.DefaultIsFixedHeight, fixedHeight: DF.Medium), DF.Medium,
            "all on, default = the Attack Height setting (Medium)");
        Check.Eq(Pick(fx, now, def: DF.DefaultIsFixedHeight, fixedHeight: DF.Low), DF.Low,
            "all on, default = the Attack Height setting (Low)");
        Check.Eq(Pick(fx, now, def: 7, fixedHeight: DF.Medium), DF.Medium, "a bad default falls back to the fixed height");
    }

    private static void ExpiryReapplies()
    {
        var fx = new DfTargetEffects();
        fx.Landed(DF.High, T0);
        fx.Landed(DF.Medium, T0.AddSeconds(4));
        fx.Landed(DF.Low, T0.AddSeconds(8));
        Check.Eq(Pick(fx, T0.AddSeconds(10), def: DF.Low), DF.Low, "all on: the default");
        // High ran 20 s from T0; 5 s before its end it counts as missing.
        Check.Eq(Pick(fx, T0.AddSeconds(14.9), def: DF.Low), DF.Low, "High has 5.1 s left: still the default");
        Check.Eq(Pick(fx, T0.AddSeconds(15.1), def: DF.Low), DF.High, "High has 4.9 s left: back to High");
        Check.Eq(Pick(fx, T0.AddSeconds(25), def: DF.Low), DF.High, "High ran out: High");
        fx.Landed(DF.High, T0.AddSeconds(16));   // a landing refreshes it to a full 20 s
        Check.Eq(Pick(fx, T0.AddSeconds(19.5), def: DF.Low), DF.Medium, "High refreshed; Medium (4 s + 20 s) is due at 19 s");
        Check.True(fx.IsOn(DF.High, T0.AddSeconds(35.9)), "the refreshed High lasts to 36 s");
        Check.False(fx.IsOn(DF.High, T0.AddSeconds(36.1)), "and not past it");
    }

    private static void UntrainedFixed()
    {
        Check.Eq(Pick(null, T0, trained: false), FixedMedium, "untrained: the fixed height");
        Check.Eq(Pick(null, T0, trained: false, fixedHeight: DF.Low), DF.Low, "untrained: the fixed height (Low)");
        Check.Eq(Pick(null, T0, on: false), FixedMedium, "rotation off: the fixed height");
        Check.Eq(Pick(null, T0, on: false, fixedHeight: 9), DF.High, "a fixed height out of range is clamped");
    }

    private static void Orders()
    {
        Check.Eq(Pick(null, T0, order: "LMH"), DF.Low, "LMH: Low first");
        Check.Eq(Pick(null, T0, order: "mhl"), DF.Medium, "lower case works: Medium first");
        var fx = new DfTargetEffects();
        fx.Landed(DF.Medium, T0);
        Check.Eq(Pick(fx, T0, order: "MLH"), DF.Low, "MLH with Medium on: Low");
        Check.Eq(Pick(null, T0, order: "HHL"), DF.High, "a repeated letter is not an order: High, Medium, Low");
        Check.Eq(Pick(null, T0, order: null!), DF.High, "no order: the default");
        Check.Eq(DF.NormalizeOrder("xyz"), "HML", "junk normalizes to HML");
        Check.Eq(DF.OrderLabel("HML"), "High, Medium, Low", "label");
        Check.Eq(DF.OrderLabel("LHM"), "Low, High, Medium", "label of LHM");
        Check.Eq(DF.OrderChoices.Length, 6, "six orders");
    }

    private static void ParseLanding()
    {
        Check.True(DF.TryParseLanding("Dirty Fighting! Bob delivers a Blinding Blow to Drudge Skulker!", "Bob", out int h, out string t), "Blinding Blow");
        Check.Eq(h, DF.High, "Blinding is High");
        Check.Eq(t, "Drudge Skulker", "target name");
        Check.True(DF.TryParseLanding("Dirty Fighting! Bob delivers a Traumatic Assault to Drudge!\n", "Bob", out h, out _), "Traumatic Assault (specialized), trailing newline");
        Check.Eq(h, DF.High, "Traumatic is High");
        Check.True(DF.TryParseLanding("Dirty Fighting! Bob delivers a Bleeding Blow to Drudge!", "Bob", out h, out _), "Bleeding");
        Check.Eq(h, DF.Medium, "Bleeding is Medium");
        Check.True(DF.TryParseLanding("Dirty Fighting! Bob delivers an Unbalancing Assault to Mu-miyah Grave Robber!", "Bob", out h, out t), "an Unbalancing");
        Check.Eq(h, DF.Low, "Unbalancing is Low");
        Check.Eq(t, "Mu-miyah Grave Robber", "hyphenated target");
        Check.True(DF.TryParseLanding("Dirty Fighting! Sir Bob the Bold delivers a Bleeding Blow to Ward to the North!", "Sir Bob the Bold", out h, out t), "names with spaces and ' to '");
        Check.Eq(t, "Ward to the North", "target with ' to ' in it");

        Check.False(DF.TryParseLanding("Dirty Fighting! Drudge delivers a Blinding Blow to Bob!", "Bob", out _, out _), "a monster's on me: not mine");
        Check.False(DF.TryParseLanding("Dirty Fighting! Bobby delivers a Blinding Blow to Drudge!", "Bob", out _, out _), "another player whose name starts the same");
        Check.False(DF.TryParseLanding("Bob says, \"Dirty Fighting! Bob delivers a Blinding Blow to Drudge!\"", "Bob", out _, out _), "said in chat");
        Check.False(DF.TryParseLanding("Dirty Fighting! Bob delivers a Fancy Kick to Drudge!", "Bob", out _, out _), "unknown spell");
        Check.False(DF.TryParseLanding("Dirty Fighting! Bob delivers a Blinding Blow to !", "Bob", out _, out _), "no target");
        Check.False(DF.TryParseLanding("You hit Drudge for 40 points of slashing damage!", "Bob", out _, out _), "a hit line");
        Check.False(DF.TryParseLanding("Dirty Fighting! Bob delivers a Blinding Blow to Drudge!", "", out _, out _), "own name unknown");
    }

    private static void Chance()
    {
        Check.Eq(DF.ChancePerHit(true, 300, 300), 0.25, "equal skills: 25%");
        Check.Eq(DF.ChancePerHit(true, 400, 300), 0.25, "Dirty Fighting higher: 25%");
        Check.Eq(DF.ChancePerHit(true, 150, 300), 0.125, "half the weapon skill: 12.5%");
        Check.Eq(DF.ChancePerHit(false, 300, 300), 0.0, "untrained: 0");
        Check.Eq(DF.ChancePerHit(true, 100, 0), 0.25, "weapon skill unknown: 25%");
    }

    private static void Tracker()
    {
        var tr = new DirtyFightingTracker();
        Check.True(tr.Get(1) == null, "nothing on target 1 yet");
        tr.Landed(1, DF.High, T0);
        tr.Landed(2, DF.Low, T0);
        Check.True(tr.Get(1)!.IsOn(DF.High, T0.AddSeconds(1)), "target 1 has High");
        Check.False(tr.Get(1)!.IsOn(DF.Low, T0.AddSeconds(1)), "target 1 has no Low");
        Check.True(tr.Get(2)!.IsOn(DF.Low, T0.AddSeconds(1)), "target 2 has Low");
        var copy = tr.Get(1)!;
        copy.Landed(DF.Low, T0);
        Check.False(tr.Get(1)!.IsOn(DF.Low, T0.AddSeconds(1)), "Get returns a copy");
        Check.Eq(tr.Get(1)!.Describe("HML", T0.AddSeconds(1)), "High 19 s, Medium off, Low off", "describe in order");
        tr.Landed(0, DF.High, T0);
        tr.Landed(3, 5, T0);
        Check.Eq(tr.Count, 2, "no target id, or a bad height: ignored");
        for (int id = 10; id < 200; id++) tr.Landed(id, DF.Medium, T0.AddSeconds(id));
        Check.True(tr.Count <= 64, "at most 64 targets kept");
        Check.True(tr.Get(199) != null, "the newest kept");
        tr.Forget(199);
        Check.True(tr.Get(199) == null, "forgotten");
    }
}
