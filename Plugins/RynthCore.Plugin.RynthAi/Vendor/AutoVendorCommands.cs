using System;
using RynthCore.Plugin.RynthAi.Vendor;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// UtilityBelt's /ub autovendor and /ub vendor commands. Parsed here (chat thread),
/// run by AutoVendorManager on the plugin tick thread.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private AutoVendorManager? _autoVendor;

    /// <summary>
    /// /ub autovendor [cancel|stop|quit|profile]
    /// /ub vendor {open[p] [name|id|hex|selected] | opencancel | buy[all] | sell[all] |
    ///             clearbuy | clearsell | addbuy[p] [count] item | addsell[p] [count] item}
    /// Returns false when the verb isn't an AutoVendor verb.
    /// </summary>
    private bool TryHandleAutoVendorCommand(string cmd, string fullCommand)
    {
        if (cmd != "autovendor" && cmd != "vendor")
            return false;

        var av = _autoVendor;
        if (av == null)
        {
            ReplyFail("[RynthAi] AutoVendor isn't ready yet (log in first).");
            return true;
        }

        string rest = AfterWords(fullCommand, 2);   // everything after "/ub <verb>"

        // Run on the tick, they answer there as this line would have (typed: in chat; a meta's: quiet
        // when it works). Every queued command says what it did, so the line itself is answered.
        bool fromScript = Reply.FromScript;
        void Queue(Action command)
        {
            Reply.Answered();
            av.Enqueue(() => Reply.RunQueued(fromScript, command));
        }

        if (cmd == "autovendor")
        {
            Queue(() => av.CommandAutoVendor(rest));
            return true;
        }

        string action = rest;
        string args = string.Empty;
        int asp = rest.IndexOf(' ');
        if (asp >= 0)
        {
            action = rest.Substring(0, asp);
            args = rest.Substring(asp + 1).Trim();
        }
        action = action.ToLowerInvariant();

        switch (action)
        {
            case "buy":
            case "buyall":    Queue(av.CommandBuyAll); break;
            case "sell":
            case "sellall":   Queue(av.CommandSellAll); break;
            case "clearbuy":  Queue(av.CommandClearBuy); break;
            case "clearsell": Queue(av.CommandClearSell); break;
            // UB: "open" with no name picks the nearest vendor, like openp.
            case "open":      Queue(() => av.CommandOpen(args, partial: args.Length == 0)); break;
            case "openp":     Queue(() => av.CommandOpen(args, partial: true)); break;
            case "opencancel": Queue(av.CommandOpenCancel); break;
            case "addbuy":
            case "addbuyp":
                if (args.Length == 0) { VendorUsage(); break; }
                Queue(() => av.CommandAddBuy(args, partial: action == "addbuyp"));
                break;
            case "addsell":
            case "addsellp":
                if (args.Length == 0) { VendorUsage(); break; }
                Queue(() => av.CommandAddSell(args, partial: action == "addsellp"));
                break;
            default:
                VendorUsage();
                break;
        }
        return true;
    }

    /// <summary>The text after the first <paramref name="words"/> space-separated words, trimmed.</summary>
    private static string AfterWords(string text, int words)
    {
        int i = 0;
        string s = text ?? string.Empty;
        for (int w = 0; w < words; w++)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        }
        return i < s.Length ? s.Substring(i).Trim() : string.Empty;
    }

    private void VendorUsage()
        => ReplyFail("[RynthAi] Usage: /ub vendor {open[p] <vendorname,vendorid,vendorhex> | buyall | sellall | clearbuy | clearsell | opencancel | addbuy[p] [count] <item> | addsell[p] [count] <item>}");
}
