using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The rest of ub-IT's /ub verbs (2026-10-18 meta-compatibility pass). Three kinds:
///   1. Aliases and features with a RynthAi, RynthNav, RynthSort, engine or server equivalent
///      (wear, getxy, stfl/stfr, arrow, navgoto, frame limits, fellow dismiss/leader/status,
///      qblist, augs, window save, packsort*, miniremote, meta/combat/sloot on|off, debug, lps).
///   2. ub-Rythai's verbs, which RynthAi leaves to that plugin without a reply.
///   3. ub-IT features that mean nothing here (videopatch, chat max, playeroption, ...): accepted
///      without error, said once per session, so a meta that sends them keeps running.
/// </summary>
public sealed partial class RynthAiPlugin
{
    /// <summary>Verbs the ub-Rythai plugin answers. Every plugin sees each /ub line, so RynthAi stays quiet for these.</summary>
    private static readonly HashSet<string> UbRythaiVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "autopercent", "autotradeaccept", "bank", "build", "guard", "guardian", "ilt", "leaftide",
        "log", "loginq", "p2", "periodic", "xpmeter",
    };

    /// <summary>
    /// ub-IT verbs with no RynthAi equivalent: diagnostics of ub-IT's own tools, Decal UI and
    /// client patches, and features RynthCore does its own way. Accepted so metas keep running.
    /// </summary>
    private static readonly HashSet<string> UbNoEquivalentVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "acobject", "autologin", "autoloot", "autosalvage", "autosalvagedebug", "autosalvagereload",
        "bankparse", "breakallegiance", "bridge", "calcdamage", "chat", "chatfilter", "chunkaeth",
        "chunkaetheria", "chunkdebug", "chunkreset", "chunkstatus", "clapstatus", "dispel", "dispeltool",
        "egg", "element", "enlighten", "enlightenment", "getjob", "getui", "invcache", "joke", "keystore",
        "login", "lsdclearcache", "lsdlb", "mtbridge", "nav", "navfollow", "navfollowarrival",
        "onetouchheal", "pcap", "perf", "playeroption", "prepclick", "printcolors", "professor",
        "questbonus", "queuestatus", "refreshqueues", "resolution", "revalidate", "setui", "shittalk",
        "soak", "spell", "statushud", "storm", "surgingstrength", "swearallegiance", "testassessor",
        "textures", "tinkcalc", "trace", "tracearmorbuffs", "ubhudrecover", "ucm", "unstack",
        "vhudbridgestatus", "xp", "xpdebug",
    };

    // No-equivalent verbs already reported this session (one chat line each, not one per meta pulse).
    private readonly HashSet<string> _ubNoEquivalentSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when ub-Rythai owns this /ub verb. "/ub portal use|list" (portal gems) is
    /// ub-Rythai's; "/ub portal &lt;name&gt;" (the nearest portal) is RynthAi's.</summary>
    internal static bool IsUbRythaiCommand(string cmd, string args)
    {
        if (UbRythaiVerbs.Contains(cmd)) return true;
        if (!cmd.Equals("portal", StringComparison.OrdinalIgnoreCase)) return false;
        string first = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } w ? w[0] : "";
        return first.Length == 0 || first.Equals("use", StringComparison.OrdinalIgnoreCase)
                                 || first.Equals("list", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The verb and its arguments from a "/ub verb args" line.</summary>
    private static (string Cmd, string Args) SplitUbLine(string fullCommand)
    {
        string rest = fullCommand.Length > 3 ? fullCommand.Substring(3).Trim() : "";
        int sp = rest.IndexOf(' ');
        return sp > 0 ? (rest.Substring(0, sp).ToLowerInvariant(), rest.Substring(sp + 1).Trim())
                      : (rest.ToLowerInvariant(), "");
    }

    /// <summary>
    /// A meta's chat-command line. /ub lines go to the /ub dispatcher (ub-IT meaning: "/ub give" gives
    /// every match, where "/mt give" gives one). ub-Rythai's verbs return false, so the meta hands the
    /// line to the chat parser, which every plugin's chat handler sees. Anything else is /mt.
    /// The whole line runs as one a running meta executes (<see cref="MetaCommandActive"/>), which is
    /// what lets /mt vendor buy|sell and /mt trade accept act, and what /ub give's stricter item
    /// protection (the loot profile's Keep rules) is keyed on. MetaManager's chat-command and
    /// chat-expression actions are the only callers.
    /// </summary>
    internal bool HandleMetaChatCommand(string cmd) => RunAsMeta(() =>
    {
        // A meta's own "/fellowship create" goes on to the chat parser; Open fellowship on create
        // still opens it (RynthAiPlugin.FellowOpen.cs).
        if (IsFellowshipCreateLine(cmd)) NoteFellowshipCreate();
        if (cmd.StartsWith("/ub ", StringComparison.OrdinalIgnoreCase))
        {
            var (verb, args) = SplitUbLine(cmd);
            return !IsUbRythaiCommand(verb, args) && HandleUbCommand(cmd);
        }
        return HandleMtCommand(cmd);
    });

    /// <summary>The verbs above. Returns false for anything else.</summary>
    internal bool TryHandleUbCompat(string cmd, string fullCommand)
    {
        string args = SplitUbLine(fullCommand).Args;
        string[] w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = w.Length > 0 ? w[0].ToLowerInvariant() : "";

        switch (cmd)
        {
            // ── Aliases of commands RynthAi already has ─────────────────────────
            case "wear":  UbWear(args, partial: false); return true;
            case "wearp": UbWear(args, partial: true); return true;
            case "getxy": return HandleMtCursor();
            case "stfl":  return HandleMtStrafe(new[] { "/mt", "strafe", "left", args });
            case "stfr":  return HandleMtStrafe(new[] { "/mt", "strafe", "right", args });
            case "debug": HandleRaCommand(("/ra debug " + args).TrimEnd()); return true;
            case "close": UbClose(sub); return true;

            case "bgframelimit":
            case "globalframelimit":
                UbFrameLimit(background: cmd == "bgframelimit", args);
                return true;

            case "fellow" when sub is "dismiss" or "dismissp" or "leader" or "leaderp" or "status" or "recruitp":
                UbFellow(sub, w.Length > 1 ? string.Join(" ", w, 1, w.Length - 1) : "");
                return true;

            // ── RynthNav: the arrow and travel to coordinates ───────────────────
            case "arrow":
                if (sub == "point" && args.Length > 6)
                    SendToPlugin("RynthNav", "rnav", "arrow " + args.Substring(6).Trim(), "/ub arrow needs the RynthNav plugin.");
                else
                    UbNoEquivalent(cmd, "\"arrow face\" has no RynthNav equivalent");
                return true;
            case "navgoto":
                UbNavGoto(w);
                return true;

            // ── RynthSort: pack sorting ─────────────────────────────────────────
            case "packsort":
            case "packsortscansort":
            case "packsortscanforcesort":
                SendToPlugin("RynthSort", "rsort", "now", "/ub packsort needs the RynthSort plugin.");
                return true;
            case "packsortshow":
            case "packsortlist":
                SendToPlugin("RynthSort", "rsort", "status", "/ub packsort needs the RynthSort plugin.");
                return true;
            case "packsortreload":
            case "packsortscan":
            case "packsortscanforce":
                UbNoEquivalent(cmd, "RynthSort reads its rules itself");
                return true;

            // ── A server's own commands (ub-IT's server verbs): the active server plugin's, if it takes them ──
            case "qblist":
            case "augs":
                if (!TryForwardToServerPlugin(new[] { "/ra", "ub", cmd }))
                    UbNoEquivalent(cmd, "it is a server plugin's command, and no loaded server plugin takes it");
                return true;

            // ── RynthCore's window placement ────────────────────────────────────
            // The engine has no chat command for it ("/rc window save" never existed and went nowhere):
            // its Settings > Display saves the game window for each character and puts it back at login.
            case "window":
                UbNoEquivalent(cmd + (sub.Length > 0 ? " " + sub : ""), null,
                    $"[RynthAi] /ub window{(sub.Length > 0 ? " " + sub : "")}: RynthCore keeps the game window itself - Settings > Display, "
                    + "'Remember Game Window Position' (saved for each character, put back after login), or 'Save Layout Now' there.");
                return true;

            // ── RynthAi's Mini Remote ───────────────────────────────────────────
            case "miniremote":
                if (sub is "show" or "hide" or "toggle") HandleHudCommand("miniremote", sub);
                else UbNoEquivalent(cmd + " " + sub, "use /ub miniremote show|hide|toggle");
                return true;

            // ── Project Storm's switches onto RynthAi's macro features ──────────
            case "meta":   return UbStormSwitch(cmd, sub, "enablemeta", w);
            case "combat": return UbStormSwitch(cmd, sub, "enablecombat", w);
            case "sloot":  return UbStormSwitch(cmd, sub, "enablelooting", w);

            case "lps":
            case "lootprofilestatus":
                {
                    string path = _dashboard?.Settings?.CurrentLootPath ?? "";
                    ChatLine("[RynthAi] Loot profile: " + (path.Length > 0 ? Path.GetFileName(path) : "(none)"));
                    return true;
                }
        }

        // ── ub-IT features with no RynthAi equivalent ───────────────────────────
        if (UbNoEquivalentVerbs.Contains(cmd))
        {
            UbNoEquivalent(cmd, null);
            return true;
        }
        return false;
    }

    /// <summary>
    /// /ub close corpse|chest (ub-IT): closes the open container when it is that kind (a bare
    /// "/ub close" closes whatever is open). Metas send it after looting a quest corpse.
    /// </summary>
    private void UbClose(string kind)
    {
        uint open = Host.HasGetGroundContainerId ? Host.GetGroundContainerId() : 0;
        if (open == 0) { ReplyFail("[RynthAi] No container is currently open."); return; }
        WorldObject? wo = _objectCache?[unchecked((int)open)];
        bool fits = kind switch
        {
            "corpse" => wo == null || wo.ObjectClass == AcObjectClass.Corpse,
            "chest" => wo == null || wo.ObjectClass == AcObjectClass.Container,
            _ => true,
        };
        string name = wo?.Name is { Length: > 0 } n ? n : $"0x{open:X8}";
        if (!fits)
        {
            // As ub-IT: the open container isn't that kind, so there is nothing to do.
            ReplyOk($"[RynthAi] /ub close {kind}: the open container ({name}) isn't a {kind}; left it open.");
            return;
        }
        bool closed = Host.HasCloseContainer ? Host.CloseContainer(open) : Host.UseFor(open, "Command", "/ub close", UseKind.Asked);
        Log($"[RynthAi] /ub close {kind}: 0x{open:X8} {(closed ? "closed" : "close refused")}.");
        if (closed) ReplyOk($"[RynthAi] Closed {name}.");
        else ReplyFail($"[RynthAi] Couldn't close {name} (the engine refused it).");
    }

    /// <summary>
    /// "/ub &lt;expression&gt;": a /ub line whose verb is an expression call (S_BulbOfMornings broadcasts
    /// "/ub bc /ub vtsetmetastate[State]" so every client switches state). ub-IT has no such
    /// command; RynthAi evaluates the expression, which is what the meta means. True when it was one.
    /// </summary>
    internal bool TryHandleUbExpression(string fullCommand)
    {
        string rest = fullCommand.Length > 3 ? fullCommand.Substring(3).Trim() : "";
        int bracket = rest.IndexOf('[');
        int space = rest.IndexOf(' ');
        if (bracket <= 0 || (space >= 0 && space < bracket) || !rest.EndsWith("]", StringComparison.Ordinal)) return false;
        string result = EvalExpr(rest);
        // A meta's (the usual sender): the log line only. Typed: the same line in chat.
        ReplyOk($"[RynthAi] /ub {rest} evaluated as an expression -> '{result}'.");
        return true;
    }

    /// <summary>
    /// A /ub verb accepted and ignored: said every time it is typed, and once per session when a meta
    /// sends it (so a meta that repeats it doesn't flood chat); every use is logged.
    /// </summary>
    private void UbNoEquivalent(string what, string? note, string? text = null)
    {
        Log($"[RynthAi] /ub {what}: no RynthAi equivalent; ignored.");
        if (!Reply.FromScript || _ubNoEquivalentSaid.Add(what))
            ReplyFail(text ?? $"[RynthAi] /ub {what} is a ub-IT feature with no RynthAi equivalent; it is accepted and ignored so metas keep running"
                              + (note != null ? $" ({note})." : "."));
        else
            Reply.Answered();
    }

    /// <summary>Sends a chat line to the chat parser (server commands): the server answers it.</summary>
    private void InvokeServerCommand(string line)
    {
        if (Host.HasInvokeChatParser) { Host.InvokeChatParser(line); Reply.Answered(); }
        else ReplyFail($"[RynthAi] Can't send {line}: the chat parser isn't available.");
    }

    /// <summary>A command for another plugin through the engine broker: that plugin answers it.</summary>
    private void SendToPlugin(string plugin, string action, string value, string missing)
    {
        if (Host.HasSendPluginCommand && Host.SendPluginCommand(plugin, action, value)) Reply.Answered();
        else ReplyFail("[RynthAi] " + missing);
    }

    /// <summary>/ub wear[p] &lt;name&gt;: wield a carried item by name, skipping copies already worn
    /// (using a worn item would take it off).</summary>
    private void UbWear(string name, bool partial)
    {
        if (string.IsNullOrWhiteSpace(name)) { ReplyFail("[RynthAi] Usage: /ub wear <item name> (wearp: partial name)"); return; }
        if (_objectCache == null || !Host.HasUseObject) { ReplyFail("[RynthAi] /ub wear: not ready (log in first)."); return; }

        bool sawWorn = false;
        foreach (var wo in _objectCache.GetInventory())
        {
            bool match = partial
                ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) > 0) { sawWorn = true; continue; }
            Host.UseFor(unchecked((uint)wo.Id), "Command", "/ub wear", UseKind.Asked);
            Log($"[RynthAi] /ub wear: {wo.Name} (0x{(uint)wo.Id:X8})");
            ReplyOk($"[RynthAi] Wearing {wo.Name}.");
            return;
        }
        if (sawWorn) ReplyOk($"[RynthAi] Already wearing '{name}'.");
        else ReplyFail($"[RynthAi] Item not found: '{name}'");
    }

    /// <summary>/ub globalframelimit|bgframelimit &lt;fps&gt;: RynthAi's frame limit (Settings, Display).
    /// 0 turns the limit off (global) or lifts the background cap to its maximum.</summary>
    private void UbFrameLimit(bool background, string arg)
    {
        LegacyUiSettings? s = _dashboard?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Settings aren't loaded yet."); return; }
        if (!int.TryParse(arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) || fps < 0)
        {
            ReplyFail($"[RynthAi] Usage: /ub {(background ? "bgframelimit" : "globalframelimit")} <frames per second>");
            return;
        }
        if (background)
        {
            s.TargetFPSBackground = fps == 0 ? 60 : Math.Clamp(fps, 5, 60);
            s.EnableFPSLimit = true;
            ReplyOk($"[RynthAi] Background frame limit: {s.TargetFPSBackground} fps.");
        }
        else if (fps == 0)
        {
            s.EnableFPSLimit = false;
            ReplyOk("[RynthAi] Frame limit off.");
        }
        else
        {
            s.TargetFPSFocused = Math.Clamp(fps, 10, 240);
            s.EnableFPSLimit = true;
            ReplyOk($"[RynthAi] Frame limit: {s.TargetFPSFocused} fps.");
        }
        try { _dashboard?.SaveSettings(); } catch { }
    }

    /// <summary>/ub fellow dismiss[p]|leader[p] &lt;name&gt;, status, recruitp &lt;name&gt; (the p forms match part of a name).</summary>
    private void UbFellow(string sub, string name)
    {
        if (sub == "status") { HandleFellowshipCommand(new[] { "/ra", "fellow", "status" }); return; }
        if (name.Length == 0) { ReplyFail($"[RynthAi] Usage: /ub fellow {sub} <name>"); return; }

        bool partial = sub.EndsWith('p');
        string resolved = name;
        if (partial && sub != "recruitp" && _fellowshipTracker != null)
        {
            foreach (string member in _fellowshipTracker.GetMemberNames())
                if (member.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { resolved = member; break; }
        }
        else if (sub == "recruitp" && FindObject(name, inv: false, land: true, partial: true) is { } wo)
        {
            resolved = wo.Name;
        }

        string verb = sub.TrimEnd('p');
        InvokeServerCommand($"/fellowship {verb} {resolved}");
    }

    /// <summary>/ub navgoto &lt;ns&gt; &lt;ew&gt; [walk|run]: ub-IT's signed coordinates (south and west
    /// negative) to RynthNav's "/rnav go 42.1N, 33.6E". RynthNav picks its own pace.</summary>
    private void UbNavGoto(string[] w)
    {
        if (w.Length < 2
            || !double.TryParse(w[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ns)
            || !double.TryParse(w[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ew))
        {
            ReplyFail("[RynthAi] Usage: /ub navgoto <ns> <ew> [walk|run] (south and west are negative)");
            return;
        }
        string coords = FormattableString.Invariant($"{Math.Abs(ns):0.0#}{(ns < 0 ? 'S' : 'N')}, {Math.Abs(ew):0.0#}{(ew < 0 ? 'W' : 'E')}");
        SendToPlugin("RynthNav", "rnav", "go " + coords, "/ub navgoto needs the RynthNav plugin.");
    }

    /// <summary>
    /// Project Storm's /ub meta|combat|sloot: on/off switch the matching RynthAi macro feature
    /// (enablemeta, enablecombat, enablelooting); "meta load &lt;name&gt;" and "meta state &lt;name&gt;"
    /// are VTank's meta load and setmetastate. The other Storm verbs have no equivalent.
    /// </summary>
    private bool UbStormSwitch(string cmd, string sub, string vtOption, string[] w)
    {
        string rest = w.Length > 1 ? string.Join(" ", w, 1, w.Length - 1) : "";
        if (_metaManager == null) { ReplyFail("[RynthAi] Not ready (log in first)."); return true; }

        switch (sub)
        {
            case "on" or "off":
                _metaManager.TryHandleVtCommand($"/vt opt set {vtOption} {(sub == "on" ? "true" : "false")}");
                return true;
            case "status" or "":
                _metaManager.TryHandleVtCommand($"/vt opt get {vtOption}");
                return true;
            case "load" when cmd == "meta" && rest.Length > 0:
                _metaManager.TryHandleVtCommand("/vt meta load " + rest);
                return true;
            case "state" when cmd == "meta" && rest.Length > 0:
                _metaManager.TryHandleVtCommand("/vt setmetastate " + rest);
                return true;
        }
        UbNoEquivalent(cmd + " " + sub, "Project Storm's own engine; RynthAi is the macro here");
        return true;
    }
}
