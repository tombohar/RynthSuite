// AutoTrade commands. The /ub autotrade syntax is ported from UtilityBelt
// (https://gitlab.com/utilitybelt/utilitybelt.gitlab.io), MIT License, Copyright (c) the
// UtilityBelt contributors; see THIRD-PARTY-NOTICES.md at the root of this repository.

using System;
using System.Globalization;
using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// /ra autotrade ... (and UtilityBelt's /ub autotrade ...): parsed here (chat thread), run by
/// AutoTradeManager on the plugin tick thread. /ra trade ... drives the engine's trade calls by
/// hand (open, add, accept, decline, reset, close, state) for testing.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private AutoTradeManager? _autoTrade;

    /// <summary>/ub autotrade ... Returns false when the verb isn't autotrade.</summary>
    private bool TryHandleAutoTradeCommand(string cmd, string fullCommand)
    {
        if (cmd != "autotrade")
            return false;
        RunAutoTradeCommand(AfterWords(fullCommand, 2));
        return true;
    }

    /// <summary>/ra autotrade [args]: the same commands as /ub autotrade.</summary>
    private void HandleAutoTradeRaCommand(string[] parts)
        => RunAutoTradeCommand(parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2) : string.Empty);

    private void RunAutoTradeCommand(string args)
    {
        var at = _autoTrade;
        if (at == null)
        {
            ReplyFail("[RynthAi] AutoTrade isn't ready yet (log in first).");
            return;
        }
        Reply.Answered();   // AutoTrade says what it did when it runs the command on the tick
        at.Enqueue(() => at.Command(args));
    }

    /// <summary>/ra trade [state | open &lt;name|id|sel&gt; | add &lt;id|sel&gt; | accept | decline | reset | close]</summary>
    private void HandleTradeCommand(string[] parts)
    {
        if (!Host.HasTrade)
        {
            ReplyFail($"[RynthAi] Player trading needs RynthCore 2026.9.x or newer (plugin API v{AutoTradeManager.RequiredApiVersion}; this engine is v{Host.Version}).");
            return;
        }

        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "state";
        string arg = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3).Trim() : string.Empty;
        switch (verb)
        {
            case "state":
            case "status":
                PrintTradeState();
                break;
            case "open":
            {
                uint target = ResolveTradeTarget(arg);
                if (target == 0) { ReplyFail("[RynthAi] Usage: /ra trade open <player name|id|sel>"); break; }
                ReplyIf(Host.TradeOpen(target),
                    $"[RynthAi] Asked {TradeName(target)} to trade.",
                    "[RynthAi] Could not send the trade request.");
                break;
            }
            case "add":
            {
                uint item = ParseTradeId(arg);
                if (item == 0) { ReplyFail("[RynthAi] Usage: /ra trade add <item id|sel>"); break; }
                ReplyIf(Host.TradeAdd(item),
                    $"[RynthAi] Adding {TradeName(item)} to the trade window.",
                    "[RynthAi] Could not send the add.");
                break;
            }
            case "accept":  ReplyIf(Host.TradeAccept(),  "[RynthAi] Accepting the trade.",       "[RynthAi] Could not send the accept."); break;
            case "decline": ReplyIf(Host.TradeDecline(), "[RynthAi] Declining the trade.",       "[RynthAi] Could not send the decline."); break;
            case "reset":   ReplyIf(Host.TradeReset(),   "[RynthAi] Clearing the trade window.", "[RynthAi] Could not send the reset."); break;
            case "close":   ReplyIf(Host.TradeClose(),   "[RynthAi] Closing the trade.",         "[RynthAi] Could not send the close."); break;
            default:
                ReplyFail("[RynthAi] Usage: /ra trade [state | open <name|id|sel> | add <id|sel> | accept | decline | reset | close]");
                break;
        }
    }

    private void PrintTradeState()
    {
        if (!Host.TryGetTradeState(out TradeState st))
        {
            ChatLine("[RynthAi] No trade state from the engine.");
            return;
        }
        ChatLine($"[RynthAi] Trade: {(st.IsOpen ? $"open with {TradeName(st.PartnerId)}" : "closed")}, generation {st.Generation}, seq {st.Sequence}, "
                 + $"last event 0x{st.LastEventType:X4}, completed {st.CompletedCount}, watching {st.Watching}, actions {st.ActionsAvailable}.");
        if (st.IsOpen)
        {
            ChatLine($"[RynthAi]   You ({st.YourItemCount}{(st.YouAccepted ? ", accepted" : string.Empty)}): {DescribeTradeSide(TradeSide.You)}");
            ChatLine($"[RynthAi]   Them ({st.PartnerItemCount}{(st.PartnerAccepted ? ", accepted" : string.Empty)}): {DescribeTradeSide(TradeSide.Partner)}");
        }
        if (st.FailureCount > 0)
            ChatLine($"[RynthAi]   Last failure: {TradeName(st.LastFailureItemId)} error 0x{st.LastFailureReason:X4} ({st.FailureCount} so far).");
        if (!st.IsOpen && st.LastCloseReason != 0)
            ChatLine($"[RynthAi]   Last close reason: 0x{st.LastCloseReason:X}.");
        var at = _autoTrade;
        if (at != null)
            ChatLine($"[RynthAi]   AutoTrade: {at.Status}");
    }

    private string DescribeTradeSide(TradeSide side)
    {
        uint[] ids = Host.GetTradeItems(side);
        if (ids.Length == 0) return "(nothing)";
        var names = new System.Collections.Generic.List<string>();
        for (int i = 0; i < ids.Length && i < 12; i++) names.Add(TradeName(ids[i]));
        return string.Join(", ", names) + (ids.Length > 12 ? $", ...{ids.Length - 12} more" : string.Empty);
    }

    private string TradeName(uint id)
        => id != 0 && Host.TryGetObjectName(id, out string n) && !string.IsNullOrWhiteSpace(n) ? n : $"0x{id:X8}";

    private uint ParseTradeId(string token)
    {
        string t = token.Trim();
        if (t.Length == 0) return 0;
        if (t.Equals("sel", StringComparison.OrdinalIgnoreCase) || t.Equals("selected", StringComparison.OrdinalIgnoreCase))
            return Host.GetSelectedItemId();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            return hex;
        return uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec) ? dec : 0;
    }

    /// <summary>A player by id, "sel", or (partial) name among nearby players.</summary>
    private uint ResolveTradeTarget(string arg)
    {
        uint id = ParseTradeId(arg);
        if (id != 0) return id;
        if (arg.Length == 0 || _objectCache == null) return 0;
        int me = unchecked((int)_playerId);
        WorldObject? partial = null;
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            if (wo == null || wo.Id == me || wo.ObjectClass != AcObjectClass.Player) continue;
            if (string.Equals(wo.Name, arg, StringComparison.OrdinalIgnoreCase)) return unchecked((uint)wo.Id);
            if (partial == null && wo.Name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0) partial = wo;
        }
        return partial != null ? unchecked((uint)partial.Id) : 0;
    }
}
