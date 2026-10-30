using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// UtilityBelt /ub command compatibility — thin shim that forwards to the
/// matching /ra handlers. Partial class of RynthAiPlugin.
/// </summary>
public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// Portal by class, or, for objects the cache classified before their type was
    /// known (they stay Unknown), by the live item type, or by a name ending in
    /// "Portal" when the type isn't available.
    /// </summary>
    internal bool IsPortalObject(WorldObject wo)
    {
        if (wo.ObjectClass == RynthCore.Loot.AcObjectClass.Portal) return true;
        if (wo.ObjectClass is RynthCore.Loot.AcObjectClass.Monster or RynthCore.Loot.AcObjectClass.Player
            or RynthCore.Loot.AcObjectClass.Corpse or RynthCore.Loot.AcObjectClass.Npc) return false;
        // The cached class can be a name guess; the live item type is the truth.
        if (Host.TryGetItemType(unchecked((uint)wo.Id), out uint t) && t != 0)
            return (t & 0x00010000u) != 0;
        return wo.Name.IndexOf("Portal", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Nearest object with a position matching <paramref name="accept"/>. Looks at
    /// every known object, not only the landscape set: a portal filed as inventory
    /// (created before its position arrived) was invisible to closestportal.
    /// </summary>
    internal WorldObject? FindNearestPositioned(Func<WorldObject, bool> accept, out double distance)
    {
        distance = double.MaxValue;
        if (_objectCache == null || _playerId == 0) return null;
        int me = unchecked((int)_playerId);
        WorldObject? best = null;
        foreach (var wo in _objectCache.AllKnownObjects())
        {
            if (wo == null || wo.Id == me || !accept(wo)) continue;
            double d = _objectCache.Distance(me, wo.Id);
            if (d == double.MaxValue) continue;   // no position (in a pack)
            if (d < distance) { distance = d; best = wo; }
        }
        return best;
    }

    /// <summary>After a miss: log the nearest object whose name mentions Portal, with its class and type.</summary>
    private void LogPortalMiss(string what)
    {
        var near = FindNearestPositioned(w => w.Name.IndexOf("Portal", StringComparison.OrdinalIgnoreCase) >= 0, out double d);
        if (near == null) { Log($"[RynthAi] {what}: no object named like a portal in range either."); return; }
        uint t = Host.TryGetItemType(unchecked((uint)near.Id), out uint tt) ? tt : 0;
        Log($"[RynthAi] {what}: nearest '*Portal*' object was {near.Name} 0x{(uint)near.Id:X8} at {d:F1} m, class={near.ObjectClass}, itemType=0x{t:X8}.");
    }

    private void UseClosestPortal()
    {
        if (_objectCache == null || _playerId == 0) { ReplyFail("[RynthAi] Not logged in."); return; }
        var best = FindNearestPositioned(IsPortalObject, out double bestD);
        if (best == null) { ReplyFail("[RynthAi] No portal nearby."); LogPortalMiss("closestportal"); return; }
        ReplyOk($"[RynthAi] Using {best.Name} ({bestD:F1} m).");
        Host.UseFor(unchecked((uint)best.Id), "Command", "/ub closestportal", UseKind.Asked);
    }

    /// <summary>
    /// UtilityBelt's network commands (/ub bc, /ub bct, /ub netclients) go to the RynthNet
    /// plugin as ("ub", "bc ...") through the host broker; RynthNet runs them on its own tick.
    /// </summary>
    private void ForwardNetCommand(string fullCommand)
    {
        string rest = fullCommand.Length > 3 ? fullCommand.Substring(3).Trim() : "";
        if (Host.HasSendPluginCommand && Host.SendPluginCommand("RynthNet", "ub", rest))
        {
            Reply.Answered();   // RynthNet answers its own verbs
            return;
        }
        ReplyFail("[RynthAi] /ub bc, /ub bct and /ub netclients need the RynthNet plugin (the launcher's Plugins tab).");
    }

    /// <summary>
    /// The usage line for a /ub line RynthAi didn't take: the verb's usage when it is one RynthAi has
    /// (the wrong arguments), else "unknown" and where the list is.
    /// </summary>
    private static string UbUsageFor(string line)
    {
        string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = w.Length > 1 ? w[1].ToLowerInvariant() : "";
        string? usage = MtVerbUsage(verb, "/ub") ?? verb switch
        {
            "follow" or "followp" => "/ub follow[p] <name>",
            "ig" or "igp" => $"/ub {verb} <loot profile> to <player>",
            "mexec" => "/ub mexec <expression>",
            "propertydump" => "/ub propertydump [0xId]",
            _ => null,
        };
        return usage != null
            ? "[RynthAi] Usage: " + usage
            : $"[RynthAi] Unknown /ub command{(verb.Length > 0 ? $" '{verb}'" : "")}. /ub help lists the ones RynthAi takes.";
    }

    /// <summary>
    /// Handle a /ub command. Returns true if recognized and handled.
    /// </summary>
    internal bool HandleUbCommand(string fullCommand)
    {
        string[] parts = fullCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        string cmd = parts[1].ToLower();

        // ub-Rythai's verbs: that plugin answers; handled here only in the sense of "not unknown".
        if (IsUbRythaiCommand(cmd, SplitUbLine(fullCommand).Args))
        {
            Reply.Answered();
            return true;
        }

        // "/ub vtsetmetastate[State]" and the like: an expression, not a verb.
        if (TryHandleUbExpression(fullCommand))
            return true;

        // /ub jump[swzxc] [heading] [holdtime] — see Jumper.cs
        if (cmd.StartsWith("jump", StringComparison.Ordinal))
        {
            HandleJumpCommand(cmd, parts);
            return true;
        }

        // /ub videopatch [enable|disable|...] (UtilityBelt's video patch) is the engine's: /rc videopatch.
        if (cmd is "videopatch" or "video")
        {
            ForwardVideoPatch(parts, 2);
            return true;
        }

        // /ub bc, /ub bct, /ub netclients (UtilityBelt's networking) belong to the RynthNet plugin.
        if (cmd is "bc" or "bct" or "netclients")
        {
            ForwardNetCommand(fullCommand);
            return true;
        }

        // /ub autovendor ... and /ub vendor ... (UtilityBelt AutoVendor) — Vendor/AutoVendorCommands.cs
        if (TryHandleAutoVendorCommand(cmd, fullCommand))
            return true;

        // /ub autotrade ... (UtilityBelt AutoTrade) — Trade/AutoTradeCommands.cs
        if (TryHandleAutoTradeCommand(cmd, fullCommand))
            return true;

        // Most remaining /ub verbs (use / usep / uselp / useip / face / give /
        // givep / combatstate / cast …) share semantics with the natively
        // implemented /mt verbs, which talk to host primitives (UseObject /
        // TurnToHeading / settings) — NOT to Mag-Tools or Decal. Translate the
        // prefix and reuse that handler so the command works with no external
        // plugin loaded. Verbs /mt doesn't know (mexec, ig, prepclick, …) fall
        // through to HandleMtCommand returning false → "Unrecognized /ub".
        int sp = fullCommand.IndexOf(' ');

        // /ub give[p] [count] <item> to <target>: UtilityBelt gives EVERY matching carried item
        // (Tom, 2026-10-05: "/ub give ... should give every item that matches the name"), unless
        // a count is given. It used to fall through to /mt give, which hands over only the first.
        // Targets match partially, as /mt give always did. give = exact item, givep = partial item.
        if (sp > 0 && cmd is "give" or "givep")
        {
            string rest = fullCommand.Substring(sp + cmd.Length + 1).TrimStart();
            string first = rest.Split(' ', 2)[0];
            bool counted = int.TryParse(first, out int n) && n > 0;
            string verb = cmd == "givep"
                ? (counted ? "givepp" : "giveapp")
                : (counted ? "givexp" : "giveaxp");
            HandleRaCommand("/ra " + verb + " " + rest);
            return true;
        }

        // Verbs /mt doesn't implement but /ra does — route to the /ra dispatcher
        // (VTank-meta migration). "/ub follow X" → "/ra follow X"; myquests → /ra
        // quest-flag refresh.
        if (sp > 0 && cmd is "follow" or "ig" or "mexec" or "myquests"
            or "igp" or "giver" or "listvars" or "listgvars" or "listpvars"
            or "selecti" or "selectl" or "selectip" or "selectpi" or "selectlp" or "selectpl")
        {
            HandleRaCommand("/ra" + fullCommand.Substring(sp));
            return true;
        }

        // Same commands under UB's names: /ub propertydump = /ra dumpprops,
        // /ub followp <partial name> = /ra follow (which matches partial names).
        if (cmd == "propertydump")
        {
            HandleRaCommand("/ra dumpprops" + (fullCommand.Length > sp + cmd.Length + 1 ? fullCommand.Substring(sp + cmd.Length + 1) : ""));
            return true;
        }
        if (cmd == "followp")
        {
            HandleRaCommand("/ra follow" + (fullCommand.Length > sp + cmd.Length + 1 ? fullCommand.Substring(sp + cmd.Length + 1) : ""));
            return true;
        }

        // /ub clearbugged is RynthAi's clearbusy (force-reset busy state) under a
        // different name.
        if (cmd == "clearbugged")
        {
            HandleRaCommand("/ra clearbusy");
            return true;
        }

        // Commands added in the 2026-09-28 parity pass (UtilityBeltExtraCommands.cs).
        if (TryHandleUbExtra(cmd, fullCommand))
            return true;

        // The rest of ub-IT's verbs: aliases, features, and accepted no-ops (UtilityBeltCompatCommands.cs).
        // Before the /mt translation, so "/ub fellow dismiss" doesn't land on /mt fellow's usage line.
        if (TryHandleUbCompat(cmd, fullCommand))
            return true;

        // /ub closestportal: use the nearest portal (UtilityBelt's command).
        if (cmd == "closestportal")
        {
            UseClosestPortal();
            return true;
        }

        if (sp > 0)
        {
            string translated = "/mt" + fullCommand.Substring(sp);   // "/ub use X" → "/mt use X"
            if (HandleMtCommand(translated))
                return true;
        }

        return false;
    }
}
