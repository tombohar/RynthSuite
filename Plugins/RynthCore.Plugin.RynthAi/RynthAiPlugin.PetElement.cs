using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// Pet element (a players' request, 2026-11-22): /ra petelement [status|any|fire|cold|...].
// The main path is the Mini Remote's Pets tab, Combat type, "Pet element". The pick itself is
// PetChoice.Order; what each summoning item deals (two elements for a server's holder of two
// summons) is PetElements.
public sealed partial class RynthAiPlugin
{
    internal const string PetElementUsage =
        "Usage: /ra petelement [status | any | fire | cold | acid | lightning | nether | slash | pierce | bludgeon]";

    /// <summary>/ra petelement: status (the default) or the element combat pets are summoned for.</summary>
    private void HandlePetElementCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Pet element: not ready yet (log in first)."); return; }

        string arg = parts.Length > 2 ? parts[2].Trim() : "status";
        if (arg.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ChatLine(PetElementStatusLine(settings));
            foreach (string line in ListedPetElementLines(settings)) ChatLine(line);
            return;
        }

        string elem = arg.Equals("any", StringComparison.OrdinalIgnoreCase) || arg.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "" : PetChoice.NormalizeElement(arg.Equals("frost", StringComparison.OrdinalIgnoreCase) ? "Cold" : arg);
        if (elem.Length == 0 && !arg.Equals("any", StringComparison.OrdinalIgnoreCase) && !arg.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            ReplyFail($"[RynthAi] Pet element: unknown element '{arg}'. {PetElementUsage}");
            return;
        }
        settings.PetElement = elem;
        _dashboard?.SaveSettings();
        ReplyOk(elem.Length == 0
            ? "[RynthAi] Pet element: Any (each monster's own choice: Damage-panel pick, Monsters rule, then its weakness)."
            : $"[RynthAi] Pet element: {elem}. A {elem} essence is summoned first unless the monster has a pick or a rule of its own.");
    }

    private static string PetElementStatusLine(LegacyUiSettings settings)
    {
        string e = PetChoice.NormalizeElement(settings.PetElement);
        return e.Length == 0
            ? "[RynthAi] Pet element: Any (each monster's own choice). /ra petelement <element>, or Mini Remote > Pets > Combat."
            : $"[RynthAi] Pet element: {e}. /ra petelement any to go back, or Mini Remote > Pets > Combat.";
    }

    /// <summary>One line per listed combat pet item: what it deals ("Fire+Cold" for a holder of two summons).</summary>
    private List<string> ListedPetElementLines(LegacyUiSettings settings)
    {
        var lines = new List<string>();
        foreach (var rule in settings.ConsumableRules)
        {
            if (!rule.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)) continue;
            string name = _objectCache?[rule.Id]?.Name ?? rule.Name;
            var info = PetElements.Describe(Host, unchecked((uint)rule.Id), name);
            string elems = info.ElementText.Length > 0 ? info.ElementText : "element unknown";
            string extra = !info.IsHolder ? ""
                : info.Ready ? $", holds {Math.Max(info.Held.Count, info.Slots)} summons" + (info.Held.Count > 0 ? ": " + string.Join(", ", info.Held) : "")
                : $", holds {info.Held.Count} of {info.Slots} (can't summon yet)";
            lines.Add($"[RynthAi]   {name}: {elems}{extra}");
        }
        if (lines.Count == 0) lines.Add("[RynthAi]   No pet essences listed (Mini Remote > Pets: tick them, or /ra items fill).");
        return lines;
    }
}
