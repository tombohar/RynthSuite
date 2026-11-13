using System;
using System.Globalization;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

namespace RynthCore.Plugin.RynthAi;

// Auto-confirm crafting (a players' request, 2026-11-11): /ra autoconfirm [on|off|<percent>|status].
// The work is MiniRemote/Crafting/CraftAutoConfirm.cs (ticked by the Crafting tab); the main path is
// the Mini Remote's Crafting tab, "Auto-confirm crafting" and its percent.
public sealed partial class RynthAiPlugin
{
    internal const string AutoConfirmUsage = "Usage: /ra autoconfirm [on | off | <percent 50-100> | status]";

    /// <summary>/ra autoconfirm: status (the default), on, off, or a percent (sets it and turns it on).</summary>
    private void HandleAutoConfirmCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Auto-confirm crafting: not ready yet (log in first)."); return; }

        bool supported = Host.HasConfirmationLeaveBelow;
        string verb = parts.Length > 2 ? parts[2].Trim().TrimEnd('%').ToLowerInvariant() : "status";
        switch (verb)
        {
            case "status":
                ChatLine(AutoConfirmStatusLine(settings, supported));
                break;
            case "on":
                if (!supported) { ReplyFail($"[RynthAi] Auto-confirm crafting {CraftAutoConfirm.NeedsEngineText}; it stays off."); break; }
                settings.AutoConfirmCraft = true;
                _dashboard?.SaveSettings();
                ReplyOk($"[RynthAi] Auto-confirm crafting: on, Yes at {CraftAutoConfirm.ClampPercent(settings.AutoConfirmCraftPercent)}% or better (lower chances show AC's dialog).");
                break;
            case "off":
                settings.AutoConfirmCraft = false;
                _dashboard?.SaveSettings();
                ReplyOk("[RynthAi] Auto-confirm crafting: off (AC asks you every time).");
                break;
            default:
                if (!int.TryParse(verb, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
                {
                    ReplyFail($"[RynthAi] Auto-confirm crafting: unknown option '{parts[2]}'. {AutoConfirmUsage}");
                    break;
                }
                if (percent < CraftAutoConfirm.MinThreshold || percent > CraftAutoConfirm.MaxThreshold)
                {
                    ReplyFail($"[RynthAi] Auto-confirm crafting: the percent must be {CraftAutoConfirm.MinThreshold}-{CraftAutoConfirm.MaxThreshold} (got {percent}). {AutoConfirmUsage}");
                    break;
                }
                if (!supported) { ReplyFail($"[RynthAi] Auto-confirm crafting {CraftAutoConfirm.NeedsEngineText}; nothing changed."); break; }
                settings.AutoConfirmCraftPercent = percent;
                settings.AutoConfirmCraft = true;
                _dashboard?.SaveSettings();
                ReplyOk($"[RynthAi] Auto-confirm crafting: on, Yes at {percent}% or better (lower chances show AC's dialog).");
                break;
        }
    }

    private string AutoConfirmStatusLine(LegacyUiSettings settings, bool supported)
    {
        int percent = CraftAutoConfirm.ClampPercent(settings.AutoConfirmCraftPercent);
        if (!supported)
            return $"[RynthAi] Auto-confirm crafting: {(settings.AutoConfirmCraft ? "on" : "off")}, but it {CraftAutoConfirm.NeedsEngineText}.";
        if (!settings.AutoConfirmCraft)
            return $"[RynthAi] Auto-confirm crafting: off (the percent is {percent}%). /ra autoconfirm on, or the Mini Remote's Crafting tab.";
        var ac = _miniRemote?.Crafting.AutoConfirm;
        string detail = ac == null ? "starts once you are in the world"
            : (ac.Armed ? "armed" : ac.Status) + $"; {ac.AcceptedCount} confirmed this session";
        return $"[RynthAi] Auto-confirm crafting: on, Yes at {percent}% or better ({detail}).";
    }
}
