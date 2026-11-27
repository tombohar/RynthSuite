// ============================================================================
//  RynthSuite - RynthAiPlugin.RechargeHold.cs
//  No recharge for now: /ra norecharge and the dashboard (2026-11-24, R-20261124-121710-7876).
//
//  /ra norecharge                     all three held, or all released when any is held
//  /ra norecharge hp|stam|mana [on|off]   one vital (no on/off: toggle)
//  /ra norecharge all on|off
//  The dashboard: click HP, ST or MN by its bar to hold or release that vital, right-click any of
//  them for all three; the options menu has "No recharge for now". The hold itself is
//  LegacyUi/RechargeHold.cs: the recharge lines at 0 until released, never saved.
// ============================================================================

using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>The vitals whose recharge is held now.</summary>
    internal RechargeVital RechargeHeld => _dashboard?.Settings.RechargeHold.Held ?? RechargeVital.None;

    /// <summary>Holds or releases <paramref name="vitals"/>' recharge, and says so (chat, the command's reply).</summary>
    internal bool SetRechargeHold(RechargeVital vitals, bool on)
    {
        var s = _dashboard?.Settings;
        if (s == null) return false;
        s.RechargeHold.Set(s, vitals, on);
        RechargeVital now = s.RechargeHold.Held;
        string text = on
            ? $"[RynthAi] No recharge for now: {RechargeHold.Describe(vitals)} won't be recharged (their Heal At / Re-stam At / Get Mana At and top-off lines are held at 0; " +
              "Emergency Heal At still heals). Your settings are kept: click the vital on the dashboard again, or /ra norecharge, to put them back."
            : $"[RynthAi] Recharge back on for {RechargeHold.Describe(vitals)}: your settings are as they were" +
              (now != RechargeVital.None ? $" ({RechargeHold.Describe(now)} still held)." : ".");
        ReplyOk(text);
        return true;
    }

    private void HandleNoRechargeCommand(string[] parts)
    {
        var s = _dashboard?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string which = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        string onOff = parts.Length >= 4 ? parts[3].ToLowerInvariant() : "";
        // "/ra norecharge on|off" means all three.
        if (which is "on" or "off" or "1" or "0" or "true" or "false") { onOff = which; which = "all"; }
        RechargeVital v = which switch
        {
            "" or "all" => RechargeVital.All,
            "hp" or "health" or "heal" => RechargeVital.Health,
            "st" or "stam" or "stamina" => RechargeVital.Stamina,
            "mn" or "mana" => RechargeVital.Mana,
            _ => RechargeVital.None,
        };
        if (v == RechargeVital.None)
        {
            ReplyFail($"[RynthAi] No recharge: unknown '{which}'. Usage: /ra norecharge [hp|stam|mana|all] [on|off] (held now: {RechargeHold.Describe(s.RechargeHold.Held)}).");
            return;
        }
        bool? set = onOff switch
        {
            "" or "toggle" => null,
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => null,
        };
        if (onOff.Length > 0 && onOff != "toggle" && set == null)
        {
            ReplyFail($"[RynthAi] No recharge: unknown '{onOff}'. Usage: /ra norecharge [hp|stam|mana|all] [on|off].");
            return;
        }
        // A toggle: all of them held -> release; otherwise hold (for "all": any held releases all).
        bool on = set ?? (v == RechargeVital.All ? s.RechargeHold.Held == RechargeVital.None : (s.RechargeHold.Held & v) == 0);
        SetRechargeHold(v, on);
    }
}
