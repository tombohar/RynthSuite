using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// No recharge for now (2026-11-24, R-20261124-121710-7876: Threads on DreamWeave, "you generally don't
// want your character to pull out a wand to recharge vitals while fighting", and a one-click way
// to put the settings back). LegacyUi/RechargeHold.cs, RynthAiPlugin.RechargeHold.cs.
internal static class RechargeHoldTests
{
    public static void Register(Runner r)
    {
        r.Add("no recharge: a hold sets the vital's lines to 0 and releasing puts the real values back (Emergency Heal At stays)", HoldAndRelease);
        r.Add("no recharge: a save writes the real values, never the held 0s", SaveWritesRealValues);
        r.Add("no recharge: a line the player set while held keeps their value on release", PlayerValueWins);
        r.Add("no recharge: a profile loaded while held stays held, over the new profile's lines", ProfileLoadKeepsHold);
        r.Add("no recharge: /ra norecharge holds all, again releases; hp|stam|mana [on|off] one vital", Command);
    }

    private static LegacyUiSettings Settings() => new()
    {
        HealAt = 60, TopOffHP = 90, StaminaToHealthAt = 30, EmergencyHealAt = 25,
        RestamAt = 30, RestamTo = 70, TopOffStam = 85, GetManaAt = 40, TopOffMana = 80,
    };

    private static void HoldAndRelease()
    {
        var s = Settings();
        var h = s.RechargeHold;
        h.Set(s, RechargeVital.All, true);
        Check.Eq(h.Held, RechargeVital.All, "all held");
        Check.Eq($"{s.HealAt}/{s.TopOffHP}/{s.StaminaToHealthAt}/{s.RestamAt}/{s.RestamTo}/{s.TopOffStam}/{s.GetManaAt}/{s.TopOffMana}", "0/0/0/0/0/0/0/0",
            "every recharge line at 0 (the settings' own off)");
        Check.Eq(s.EmergencyHealAt, 25, "Emergency Heal At is the safety floor: not held");
        Check.Eq(h.RealValue("Heal At"), 60, "the real Heal At kept");

        h.Set(s, RechargeVital.Stamina, false);
        Check.Eq($"{s.RestamAt}/{s.RestamTo}/{s.TopOffStam}", "30/70/85", "stamina released: its lines back");
        Check.Eq(s.HealAt, 0, "health still held");
        Check.Eq(h.Held, RechargeVital.Health | RechargeVital.Mana, "health and mana held");
        h.Set(s, RechargeVital.All, false);
        Check.Eq($"{s.HealAt}/{s.TopOffHP}/{s.StaminaToHealthAt}/{s.GetManaAt}/{s.TopOffMana}", "60/90/30/40/80", "all released: as they were");
        Check.Eq(h.Held, RechargeVital.None, "nothing held");
        Check.Eq(RechargeHold.Describe(RechargeVital.All), "health, stamina and mana", "described");
    }

    private static void SaveWritesRealValues()
    {
        var s = Settings();
        s.RechargeHold.Set(s, RechargeVital.All, true);
        string json = s.RechargeHold.WithRealValues(s, () => JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings));
        var saved = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.Eq($"{saved.HealAt}/{saved.RestamAt}/{saved.GetManaAt}/{saved.TopOffMana}", "60/30/40/80", "the saved profile has the real values");
        Check.Eq($"{s.HealAt}/{s.RestamAt}/{s.GetManaAt}", "0/0/0", "and the live settings are held again after the save");
        Check.False(json.Contains("RechargeHold"), "the hold itself is never saved");
    }

    private static void PlayerValueWins()
    {
        var s = Settings();
        s.RechargeHold.Set(s, RechargeVital.Health, true);
        s.HealAt = 45;   // set in Settings while held
        string json = s.RechargeHold.WithRealValues(s, () => JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings));
        Check.Eq(JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings)!.HealAt, 45, "a save writes the player's new value");
        s.RechargeHold.Set(s, RechargeVital.Health, false);
        Check.Eq(s.HealAt, 45, "release keeps it (theirs wins)");
        Check.Eq(s.TopOffHP, 90, "the untouched line comes back");
    }

    private static void ProfileLoadKeepsHold()
    {
        var live = Settings();
        live.RechargeHold.Set(live, RechargeVital.Mana, true);
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var other = Settings(); other.GetManaAt = 55; other.TopOffMana = 95;
        copy.Invoke(null, new object[] { other, live });
        Check.Eq($"{live.GetManaAt}/{live.TopOffMana}", "0/0", "the loaded profile's mana lines are held");
        Check.Eq(live.RechargeHold.Held, RechargeVital.Mana, "still held");
        live.RechargeHold.Set(live, RechargeVital.Mana, false);
        Check.Eq($"{live.GetManaAt}/{live.TopOffMana}", "55/95", "released: the loaded profile's values, not the old ones");
    }

    private static void Run(TickIsolationTests.Bot bot, string line) =>
        typeof(RynthAiPlugin).GetMethod("HandleNoRechargeCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(bot.P, new object[] { line.Split(' ') });

    private static void Command()
    {
        var bot = new TickIsolationTests.Bot();
        bot.S.HealAt = 60; bot.S.GetManaAt = 40; bot.S.RestamAt = 30;
        Run(bot, "/ra norecharge");
        Check.Eq(bot.S.RechargeHold.Held, RechargeVital.All, "no argument: all three held");
        Check.Eq($"{bot.S.HealAt}/{bot.S.GetManaAt}/{bot.S.RestamAt}", "0/0/0", "their lines at 0");
        Check.True(FakeHost.Chat.Any(l => l.Contains("No recharge for now: health, stamina and mana")), "said in chat: " + string.Join(" | ", FakeHost.Chat));
        Check.Eq(bot.P.RechargeHeld, RechargeVital.All, "the dashboard reads it");

        Run(bot, "/ra norecharge");
        Check.Eq(bot.S.RechargeHold.Held, RechargeVital.None, "again: all released");
        Check.Eq($"{bot.S.HealAt}/{bot.S.GetManaAt}/{bot.S.RestamAt}", "60/40/30", "as they were");
        Check.True(FakeHost.Chat.Any(l => l.Contains("Recharge back on for health, stamina and mana")), "said in chat");

        Run(bot, "/ra norecharge mana on");
        Check.Eq(bot.S.RechargeHold.Held, RechargeVital.Mana, "one vital");
        Run(bot, "/ra norecharge hp");
        Check.Eq(bot.S.RechargeHold.Held, RechargeVital.Mana | RechargeVital.Health, "hp toggled on beside it");
        Run(bot, "/ra norecharge off");
        Check.Eq(bot.S.RechargeHold.Held, RechargeVital.None, "off: all released");
        int fails = FakeHost.Chat.Count;
        Run(bot, "/ra norecharge legs");
        Check.True(FakeHost.Chat.Skip(fails).Any(l => l.Contains("unknown 'legs'")), "a wrong vital says the usage");
    }
}
