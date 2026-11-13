using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Dirty Fighting rotation through CombatManager (CombatManager.DirtyFighting.cs): the height each
// melee attack is sent at follows this character's "Dirty Fighting! ..." landing lines for the target
// being fought, High, then Medium, then Low, then the chosen default; with the rotation off or Dirty
// Fighting untrained the fixed Melee Attack Height goes out exactly as before. The pure choice and the
// expiry timing are RynthCore.RynthAiTests' DirtyFightingHeightTests.
internal static class DirtyFightingRotationTests
{
    private const uint Player = 0x50000F71;
    private const uint Lb = 0xA9B40000;
    private const uint Mob = 0x80000471, Other = 0x80000472;
    private const uint DfSkill = 52, HeavySkill = 44;
    // The game's attack heights (what the server is sent): High 1, Medium 2, Low 3.
    private const int AcHigh = 1, AcMedium = 2, AcLow = 3;

    public static void Register(Runner r)
    {
        r.Add("dfheight rotation: High, then Medium, then Low, then the default as each effect lands", RotationFollowsLandings);
        r.Add("dfheight rotation: off, or Dirty Fighting untrained, sends the fixed Melee Attack Height", FixedWhenOffOrUntrained);
        r.Add("dfheight rotation: a landing on another monster, or by someone else, isn't counted", OtherLandingsIgnored);
        r.Add("dfheight rotation: the settings ride the Settings bridge; a sender without them leaves them alone", SettingsBridge);
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager C;

        public Rig(Action<LegacyUiSettings> set, int dfTraining = 2)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.AttackSendCalls = true;
            FakeHost.CombatModeValue = CombatMode.Melee;
            Place(Player, "Me", 96, 96, creature: false);
            Place(Mob, "Drudge Skulker", 96, 99);
            Place(Other, "Drudge Slinker", 110, 110);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.HeadingDeg = 0;
            FakeHost.Skills[(Player, DfSkill)] = (300, dfTraining);
            FakeHost.Skills[(Player, HeavySkill)] = (300, 3);
            S = new LegacyUiSettings { MonsterRange = 50, IsMacroRunning = true, EnableCombat = true, MeleeAttackHeight = 1 };
            set(S);
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { Mob, Other });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            C.SetCharacterSkills(skills);
            C.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            C.ScanNearbyTargets();
            C.HandleCombatTrigger();
            FakeHost.AttackCalls.Clear();
        }

        /// <summary>Thinks until an attack reaches the server; its height (-1 if none in 30 ticks).</summary>
        public int NextHeight()
        {
            int before = FakeHost.AttackCalls.Count;
            for (int i = 0; i < 30 && FakeHost.AttackCalls.Count == before; i++)
            {
                LastAttackCmd.SetValue(C, DateTime.MinValue);
                C.Think();
            }
            return FakeHost.AttackCalls.Count > before ? FakeHost.AttackCalls[^1].Height : -1;
        }

        private static readonly FieldInfo LastAttackCmd = typeof(CombatManager).GetField("lastAttackCmd", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static string Landing(string spell, string target = "Drudge Skulker", string who = "Me") =>
        $"Dirty Fighting! {who} delivers a {spell} to {target}!";

    private static void RotationFollowsLandings()
    {
        var rig = new Rig(s => { s.DfHeightRotation = true; s.DfDefaultHeight = 0; });   // then Low
        Check.Eq(rig.NextHeight(), AcHigh, "a fresh target: High first");
        Check.Eq(rig.NextHeight(), AcHigh, "nothing landed yet: High again");
        rig.C.NoteDirtyFightingChat(Landing("Blinding Blow"));
        rig.C.NoteDirtyFightingChat(Landing("Traumatic Blow"));
        Check.Eq(rig.NextHeight(), AcMedium, "High's effects landed: Medium for the bleed");
        rig.C.NoteDirtyFightingChat(Landing("Bleeding Blow"));
        Check.Eq(rig.NextHeight(), AcLow, "bleeding: Low next");
        rig.C.NoteDirtyFightingChat(Landing("Unbalancing Blow"));
        Check.Eq(rig.NextHeight(), AcLow, "all three on: the default (Low)");

        var toFixed = new Rig(s => { s.DfHeightRotation = true; s.DfDefaultHeight = -1; s.MeleeAttackHeight = 2; s.DfHeightOrder = "LMH"; });
        Check.Eq(toFixed.NextHeight(), AcLow, "order LMH: Low first");
        toFixed.C.NoteDirtyFightingChat(Landing("Unbalancing Assault"));
        toFixed.C.NoteDirtyFightingChat(Landing("Bleeding Assault"));
        toFixed.C.NoteDirtyFightingChat(Landing("Blinding Assault"));
        Check.Eq(toFixed.NextHeight(), AcHigh, "all on, default = the Melee Attack Height (High)");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[DFHeight]") && l.Contains("landed on")), "landings are logged");

        string status = toFixed.C.DirtyFightingStatus();
        Check.True(status.Contains("Target 'Drudge Skulker'") && status.Contains("Low 20 s"), "status shows the target's effects: " + status);
        Check.True(status.Contains("about 25% per hit"), "status shows the chance: " + status);
    }

    private static void FixedWhenOffOrUntrained()
    {
        var off = new Rig(s => { s.DfHeightRotation = false; s.MeleeAttackHeight = 0; });
        Check.Eq(off.NextHeight(), AcLow, "rotation off: the fixed height (Low)");
        off.C.NoteDirtyFightingChat(Landing("Unbalancing Blow"));
        Check.Eq(off.NextHeight(), AcLow, "rotation off: landings change nothing");
        Check.False(FakeHost.Logs.Any(l => l.Contains("attacking")), "rotation off: no height choice logged");

        var untrained = new Rig(s => { s.DfHeightRotation = true; s.MeleeAttackHeight = 1; }, dfTraining: 1);
        Check.Eq(untrained.NextHeight(), AcMedium, "Dirty Fighting untrained: the fixed height (Medium)");
        Check.True(untrained.C.DirtyFightingStatus().Contains("not trained"), "status says it isn't trained");
    }

    private static void OtherLandingsIgnored()
    {
        var rig = new Rig(s => { s.DfHeightRotation = true; s.DfDefaultHeight = 0; });
        Check.Eq(rig.NextHeight(), AcHigh, "High first");
        rig.C.NoteDirtyFightingChat(Landing("Blinding Blow", target: "Drudge Slinker"));
        Check.Eq(rig.NextHeight(), AcHigh, "a landing on a monster not being fought: still High");
        rig.C.NoteDirtyFightingChat(Landing("Blinding Blow", who: "Someone Else"));
        Check.Eq(rig.NextHeight(), AcHigh, "another attacker's landing: still High");
        rig.C.NoteDirtyFightingChat(Landing("Blinding Blow", target: "Me", who: "Drudge Skulker"));
        Check.Eq(rig.NextHeight(), AcHigh, "the monster's landing on this character: still High");
        rig.C.NoteDirtyFightingChat("Someone says, \"" + Landing("Blinding Blow") + "\"");
        Check.Eq(rig.NextHeight(), AcHigh, "the words said in chat: still High");
    }

    /// <summary>A dashboard stand-in with its profile in memory (as SkCombatAddonTests builds one).</summary>
    private static LegacyDashboardRenderer Dash(LegacyUiSettings s)
    {
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));
        return dash;
    }

    private static void SettingsBridge()
    {
        var s = new LegacyUiSettings { DfHeightRotation = true, DfHeightOrder = "MLH", DfDefaultHeight = 2 };
        var dash = Dash(s);
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload)!;
        Check.True(sent.DfHeightRotation == true && sent.DfHeightOrder == "MLH" && sent.DfDefaultHeight == 2,
            "the bridge carries the three settings");

        dash.ApplySettingsJson("{\"HealAt\":50}");
        Check.True(s.DfHeightRotation, "a sender without them: rotation kept");
        Check.Eq(s.DfHeightOrder, "MLH", "order kept");
        Check.Eq(s.DfDefaultHeight, 2, "default kept");

        dash.ApplySettingsJson("{\"DfHeightRotation\":false,\"DfHeightOrder\":\"bad\",\"DfDefaultHeight\":9}");
        Check.False(s.DfHeightRotation, "rotation set off");
        Check.Eq(s.DfHeightOrder, "HML", "a bad order normalizes to HML");
        Check.Eq(s.DfDefaultHeight, 2, "an out-of-range default is clamped");

        var fresh = new LegacyUiSettings();
        Check.False(fresh.DfHeightRotation, "off by default");
        Check.Eq(fresh.DfHeightOrder, "HML", "High, Medium, Low by default");
        Check.Eq(fresh.DfDefaultHeight, -1, "default height = the Attack Height setting");
    }
}
