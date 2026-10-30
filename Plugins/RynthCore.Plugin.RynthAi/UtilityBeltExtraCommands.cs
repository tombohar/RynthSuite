using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// UtilityBelt commands that had no RynthAi equivalent (2026-09-28 parity pass,
/// names and syntax read from UtilityBelt.dll's CommandPattern/Usage attributes):
/// delay, count, date/dateutc, id, pos, vitae, portal/portalp, playsound,
/// setmotion/getmotion/clearmotion, autostack, autocram, opt, help, simplejump,
/// dumpskills, quests, translateroute.
/// </summary>
public sealed partial class RynthAiPlugin
{
    // ── /ub delay queue ─────────────────────────────────────────────────────
    // FromMeta: the line was queued by a running meta, so it runs as one when due (it may be
    // /mt vendor sell or /mt trade accept, which only act for a meta).
    private readonly List<(long DueMs, string Command, bool FromMeta)> _delayedCommands = new();

    /// <summary>Runs /ub delay commands that are due. Called every tick.</summary>
    private void TickDelayedCommands()
    {
        // Out of world, OnChatBarEnter ignores the line and it went raw to AC's chat
        // parser at character select. TeardownSession empties the queue at logout.
        if (_delayedCommands.Count == 0 || !_loginComplete) return;
        long now = Environment.TickCount64;
        for (int i = 0; i < _delayedCommands.Count; i++)
        {
            if (_delayedCommands[i].DueMs > now) continue;
            string cmd = _delayedCommands[i].Command;
            bool fromMeta = _delayedCommands[i].FromMeta;
            _delayedCommands.RemoveAt(i--);
            try
            {
                if (fromMeta) RunAsMeta(() => { HandleRynthChatSubmit(cmd); return true; });
                else HandleRynthChatSubmit(cmd);
            }
            catch (Exception ex) { Log($"[RynthAi] /ub delay: '{cmd}' threw {ex.Message}"); }
        }
    }

    /// <summary>UB Util.Think: "/tell yourself, msg" so metas can watch for it; else a chat line.</summary>
    private void UbSay(string msg, bool think)
    {
        if (think && Host.HasInvokeChatParser && _playerId != 0
            && Host.TryGetObjectName(_playerId, out string me) && !string.IsNullOrWhiteSpace(me))
        {
            Host.InvokeChatParser($"/tell {me}, {msg}");
            Reply.Answered();   // the tell to yourself is the answer (metas watch for it)
            return;
        }
        ChatLine("[RynthAi] " + msg);
    }

    /// <summary>Handles the commands above. Returns false for anything else.</summary>
    internal bool TryHandleUbExtra(string cmd, string fullCommand)
    {
        int sp = fullCommand.IndexOf(' ');
        string rest = sp > 0 ? fullCommand.Substring(sp + 1).Trim() : "";
        // rest starts with the verb; args = everything after it
        int sp2 = rest.IndexOf(' ');
        string args = sp2 > 0 ? rest.Substring(sp2 + 1).Trim() : "";

        switch (cmd)
        {
            case "delay":         UbDelay(args); return true;
            case "count":         UbCount(args); return true;
            case "date":          UbDate(args, utc: false); return true;
            case "dateutc":       UbDate(args, utc: true); return true;
            case "id":            UbId(); return true;
            case "pos":           UbPos(); return true;
            case "vitae":         UbSay($"My vitae is {EvalExpr("vitae[]")}%", think: true); return true;
            case "portal":        UbPortal(args, partial: false); return true;
            case "portalp":       UbPortal(args, partial: true); return true;
            case "playsound":     UbPlaySound(args); return true;
            case "setmotion":     UbSetMotion(args); return true;
            case "getmotion":     UbGetMotion(); return true;
            case "clearmotion":   EvalExpr("clearmotion[]"); ReplyOk("[RynthAi] Cleared all motions."); return true;
            case "autostack":     _inventoryManager?.RunManual(cram: false, stack: true); ReplyOk("[RynthAi] AutoStack: stacking your inventory."); return true;
            case "autocram":      _inventoryManager?.RunManual(cram: true, stack: false); ReplyOk("[RynthAi] AutoCram: moving items into side packs."); return true;
            // /ub opt list|get|set stay on the /mt opt handler (it knows the Mag-Tools
            // option aliases); only toggle, which that one lacks, is handled here.
            case "opt" when args.StartsWith("toggle", StringComparison.OrdinalIgnoreCase):
                UbOpt(args); return true;
            case "help":          UbHelp(); return true;
            case "simplejump":    UbSimpleJump(args); return true;
            case "dumpskills":    UbDumpSkills(); return true;
            case "quests":        UbQuests(args); return true;
            case "translateroute": UbTranslateRoute(args); return true;
        }
        return false;
    }

    private string EvalExpr(string expr)
    {
        var engine = _metaManager?.Expressions;
        return engine == null ? "" : engine.Evaluate(expr);
    }

    // /ub delay <millisecondDelay> <command>
    private void UbDelay(string args)
    {
        int sp = args.IndexOf(' ');
        if (sp <= 0 || !int.TryParse(args.AsSpan(0, sp), out int ms) || ms < 0)
        {
            ReplyFail("[RynthAi] Usage: /ub delay <millisecondDelay> <command>");
            return;
        }
        string command = args.Substring(sp + 1).Trim();
        if (command.Length == 0) { ReplyFail("[RynthAi] Usage: /ub delay <millisecondDelay> <command>"); return; }
        _delayedCommands.Add((Environment.TickCount64 + ms, command, MetaCommandActive));
        ReplyOk($"[RynthAi] In {ms} ms: {command}");
    }

    // /ub count {item <name> | profile <lootProfile> | player <range>} [debug] [think]
    private void UbCount(string args)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool think = words.RemoveAll(w => w.Equals("think", StringComparison.OrdinalIgnoreCase)) > 0;
        words.RemoveAll(w => w.Equals("debug", StringComparison.OrdinalIgnoreCase));
        if (words.Count < 2) { ReplyFail("[RynthAi] Usage: /ub count {item <name> | profile <lootProfile> | player <range>} [think]"); return; }
        string kind = words[0].ToLowerInvariant();
        string what = string.Join(" ", words.Skip(1));
        var inv = _objectCache?.GetDirectInventory(forceRefresh: true) ?? (IReadOnlyList<WorldObject>)Array.Empty<WorldObject>();

        switch (kind)
        {
            case "item":
            {
                int n = 0;
                foreach (var wo in inv)
                    if (string.Equals(wo.Name, what, StringComparison.OrdinalIgnoreCase))
                        n += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
                UbSay($"You have {n} {what}.", think);
                break;
            }
            case "profile":
            {
                string path = ResolveLootProfilePath(what);
                if (path.Length == 0 || !TryLoadLootProfile(path, out VTankLootProfile profile, out _)) { ReplyFail($"[RynthAi] count: loot profile not found: {what}"); return; }
                var ctx = new VTankLootContext(Host, _playerId) { Cache = _objectCache };
                int n = 0;
                foreach (var wo in inv)
                {
                    var rule = profile.Rules.FirstOrDefault(r => VTankLootEvaluator.Match(r, wo, ctx));
                    if (rule != null && rule.Action is VTankLootAction.Keep or VTankLootAction.KeepUpTo)
                        n += Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
                }
                UbSay($"You have {n} items matching {Path.GetFileName(path)}.", think);
                break;
            }
            case "player":
            {
                if (!double.TryParse(what, NumberStyles.Float, CultureInfo.InvariantCulture, out double range)) { ReplyFail("[RynthAi] Usage: /ub count player <range>"); return; }
                int n = 0;
                int me = unchecked((int)_playerId);
                if (_objectCache != null)
                    foreach (var wo in _objectCache.GetLandscapeObjects())
                        if (wo.Id != me && wo.ObjectClass == AcObjectClass.Player && _objectCache.Distance(me, wo.Id) <= range) n++;
                UbSay($"There are {n} players within {range} meters.", think);
                break;
            }
            default:
                ReplyFail("[RynthAi] Usage: /ub count {item <name> | profile <lootProfile> | player <range>} [think]");
                break;
        }
    }

    private static string ResolveLootProfilePath(string name)
    {
        string n = name.Trim().Trim('"');
        if (File.Exists(n)) return n;
        string folder = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "LootProfiles");
        foreach (string cand in new[] { n, n + ".utl", n + ".json" })
        {
            string p = Path.Combine(folder, cand);
            if (File.Exists(p)) return p;
        }
        return "";
    }

    // /ub date[utc] [format]
    private void UbDate(string format, bool utc)
    {
        DateTime t = utc ? DateTime.UtcNow : RynthClock.Now;
        string s;
        try { s = format.Length > 0 ? t.ToString(format, CultureInfo.InvariantCulture) : t.ToString(CultureInfo.InvariantCulture); }
        catch (FormatException) { ReplyFail($"[RynthAi] Bad date format: {format}"); return; }
        ChatLine("[RynthAi] " + s);
    }

    private void UbId()
    {
        uint sel = Host.GetSelectedItemId();
        if (sel == 0) { ReplyFail("[RynthAi] Nothing selected."); return; }
        string name = Host.TryGetObjectName(sel, out string n) ? n : "?";
        ChatLine($"[RynthAi] {name}: id 0x{sel:X8} ({sel})");
    }

    private void UbPos()
    {
        uint sel = Host.GetSelectedItemId();
        if (sel == 0) sel = _playerId;
        if (sel == 0 || !Host.TryGetObjectPosition(sel, out uint cell, out float x, out float y, out float z))
        {
            ReplyFail("[RynthAi] No position for the selected object.");
            return;
        }
        string name = Host.TryGetObjectName(sel, out string n) ? n : "?";
        string coords = NavCoordinateHelper.TryConvertPoseToCoords(cell, x, y, out double ns, out double ew)
            ? $"{Math.Abs(ns):F4}{(ns < 0 ? "S" : "N")}, {Math.Abs(ew):F4}{(ew < 0 ? "W" : "E")}" : "?";
        ChatLine($"[RynthAi] {name}: landcell 0x{cell:X8} x={x:F3} y={y:F3} z={z:F3} ({coords})");
    }

    // /ub portal[p] <portalName>: use the nearest portal (or NPC) by name; portalp = partial.
    private void UbPortal(string name, bool partial)
    {
        if (name.Length == 0 || _objectCache == null || _playerId == 0) { ReplyFail("[RynthAi] Usage: /ub portal[p] <portalName>"); return; }
        var best = FindNearestPositioned(wo =>
            (partial ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                     : wo.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            && (IsPortalObject(wo) || wo.ObjectClass == AcObjectClass.Npc), out double bestD);
        if (best == null) { ReplyFail($"[RynthAi] No portal named {(partial ? "like " : "")}'{name}' nearby."); LogPortalMiss("portal " + name); return; }
        _navigationEngine?.Stop();
        ReplyOk($"[RynthAi] Using {best.Name} ({bestD:F1} m).");
        Host.UseFor(unchecked((uint)best.Id), "Command", "/ub portal", UseKind.Asked);
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

    // /ub playsound [volume] <filepath>  (volume accepted for compatibility; played at system volume)
    private void UbPlaySound(string args)
    {
        string file = args;
        int sp = args.IndexOf(' ');
        if (sp > 0 && int.TryParse(args.AsSpan(0, sp), out _)) file = args.Substring(sp + 1).Trim();
        file = file.Trim('"');
        if (!Path.IsPathRooted(file)) file = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, file);
        if (!File.Exists(file)) { ReplyFail($"[RynthAi] Sound file not found: {file}"); return; }
        try
        {
            if (PlaySound(file, IntPtr.Zero, 0x00020000 /*SND_FILENAME*/ | 0x0001 /*SND_ASYNC*/)) ReplyOk($"[RynthAi] Playing {Path.GetFileName(file)}.");
            else ReplyFail($"[RynthAi] playsound: Windows couldn't play {file}.");
        }
        catch (Exception ex) { ReplyFail($"[RynthAi] playsound failed: {ex.Message}"); }
    }

    private static readonly string[] UbMotions = { "Forward", "Backward", "TurnRight", "TurnLeft", "StrafeRight", "StrafeLeft", "Walk" };

    // /ub setmotion <Forward|Backward|TurnRight|TurnLeft|StrafeRight|StrafeLeft|Walk> <0|1>
    private void UbSetMotion(string args)
    {
        var w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length != 2 || (w[1] != "0" && w[1] != "1")) { ReplyFail("[RynthAi] Usage: /ub setmotion <" + string.Join("|", UbMotions) + "> <0|1>"); return; }
        EvalExpr($"setmotion[`{w[0]}`,{w[1]}]");
        ReplyOk($"[RynthAi] Motion {w[0]}: {(w[1] == "1" ? "on" : "off")}.");
    }

    private void UbGetMotion()
    {
        var on = UbMotions.Where(m => EvalExpr($"getmotion[`{m}`]") == "1").ToList();
        ChatLine("[RynthAi] Motions on: " + (on.Count > 0 ? string.Join(", ", on) : "none"));
    }

    // /ub opt {list | get <option> | set <option> <newValue> | toggle <options>}
    private void UbOpt(string args)
    {
        var map = _metaManager?.Expressions.BuildSettingsMapPublic();
        if (map == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        var w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = w.Length > 0 ? w[0].ToLowerInvariant() : "list";
        switch (verb)
        {
            case "list":
                foreach (var chunk in map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Chunk(12))
                    ChatLine("[RynthAi] " + string.Join(", ", chunk));
                break;
            case "get" when w.Length >= 2:
                if (map.TryGetValue(w[1], out var g)) ChatLine($"[RynthAi] {w[1]} = {g.Get()}");
                else ReplyFail($"[RynthAi] No option '{w[1]}' (/ub opt list).");
                break;
            case "set" when w.Length >= 3:
                if (map.TryGetValue(w[1], out var st))
                {
                    string v = string.Join(" ", w.Skip(2));
                    v = v.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : v.Equals("false", StringComparison.OrdinalIgnoreCase) ? "0" : v;
                    st.Set(v);
                    _dashboard?.SaveSettings();
                    ReplyOk($"[RynthAi] {w[1]} = {st.Get()}");
                }
                else ReplyFail($"[RynthAi] No option '{w[1]}' (/ub opt list).");
                break;
            case "toggle" when w.Length >= 2:
                foreach (string name in w.Skip(1))
                {
                    if (!map.TryGetValue(name, out var tg)) { ReplyFail($"[RynthAi] No option '{name}'."); continue; }
                    tg.Set(tg.Get() is "0" or "" or "False" ? "1" : "0");
                    ReplyOk($"[RynthAi] {name} = {tg.Get()}");
                }
                _dashboard?.SaveSettings();
                break;
            default:
                ReplyFail("[RynthAi] Usage: /ub opt {list | get <option> | set <option> <newValue> | toggle <options>}");
                break;
        }
    }

    private void UbHelp()
    {
        ChatLine("[RynthAi] /ub commands: use[i][l][p], give[p], ig[p], select, face, fellow, equip, combatstate, close, quit, logout, " +
                 "follow[p], mexec, myquests, list[g|p]vars, propertydump, jump[swzxc], simplejump, closestportal, portal[p], " +
                 "autovendor, vendor, autotrade, autostack, autocram, delay, count, date[utc], id, pos, vitae, playsound, " +
                 "setmotion, getmotion, clearmotion, opt, dumpskills, quests, translateroute, clearbugged.");
    }

    // /ub simplejump [msToHoldDown]: jump in place.
    private void UbSimpleJump(string args)
    {
        int ms = int.TryParse(args, out int v) ? Math.Clamp(v, 0, 1000) : 0;
        if (_jumper == null) { ReplyFail("[RynthAi] Jumper not ready (not logged in yet)."); return; }
        if (_jumper.Start("", null, ms)) ReplyOk(JumpLine("", null, ms));   // a refusal says why itself
    }

    private void UbDumpSkills()
    {
        if (_charSkills == null) { ReplyFail("[RynthAi] Skills not ready."); return; }
        var lines = new List<string>();
        foreach (AcSkillType sk in Enum.GetValues<AcSkillType>())
        {
            if (sk == AcSkillType.Unknown) continue;
            var info = _charSkills[sk];
            string tr = info.Training switch { 3 => "spec", 2 => "trained", 1 => "untrained", _ => "?" };
            lines.Add($"{sk} {info.Buffed} ({tr})");
        }
        foreach (var chunk in lines.Chunk(6))
            ChatLine("[RynthAi] " + string.Join(", ", chunk));
    }

    // /ub quests check <questFlag>
    private void UbQuests(string args)
    {
        var w = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 2 || !w[0].Equals("check", StringComparison.OrdinalIgnoreCase)) { ReplyFail("[RynthAi] Usage: /ub quests check <questFlag>"); return; }
        string flag = w[1].Trim();
        bool has = EvalExpr($"testquestflag[`{flag}`]") == "1";
        string status = EvalExpr($"getqueststatus[`{flag}`]");
        UbSay($"Quest {flag}: {(has ? "have flag" : "no flag")}, {(status == "1" ? "ready" : "not ready")}", think: true);
    }

    // /ub translateroute <startLandblock> <routeToLoad> <endLandblock> <routeToSaveAs> [force]
    private void UbTranslateRoute(string args)
    {
        var w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool force = w.Count > 0 && w[^1].Equals("force", StringComparison.OrdinalIgnoreCase);
        if (force) w.RemoveAt(w.Count - 1);
        if (w.Count != 4 || !TryParseLandblock(w[0], out uint start) || !TryParseLandblock(w[2], out uint end))
        {
            ReplyFail("[RynthAi] Usage: /ub translateroute <startLandblock> <routeToLoad> <endLandblock> <routeToSaveAs> [force]");
            return;
        }
        string navFolder = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "NavProfiles");
        string inPath = Path.IsPathRooted(w[1]) ? w[1] : Path.Combine(navFolder, w[1]);
        string outPath = Path.IsPathRooted(w[3]) ? w[3] : Path.Combine(navFolder, w[3]);
        if (!outPath.EndsWith(".nav", StringComparison.OrdinalIgnoreCase)) outPath += ".nav";
        if (!File.Exists(inPath)) { ReplyFail($"[RynthAi] Route not found: {inPath}"); return; }
        if (File.Exists(outPath) && !force) { ReplyFail($"[RynthAi] {Path.GetFileName(outPath)} exists; add force to overwrite."); return; }

        // A landblock is 192 m = 0.8 nav units. Landblock id: 0xXXYY.
        double dEW = (((int)(end >> 8) & 0xFF) - ((int)(start >> 8) & 0xFF)) * 0.8;
        double dNS = (((int)end & 0xFF) - ((int)start & 0xFF)) * 0.8;
        var route = NavRouteParser.Load(inPath);
        foreach (var p in route.Points)
        {
            p.NS += dNS; p.EW += dEW;
            if (p.Type is NavPointType.PortalNPC or NavPointType.Npc) { p.PortalExitNS += dNS; p.PortalExitEW += dEW; }
        }
        route.Save(outPath);
        ReplyOk($"[RynthAi] Translated {route.Points.Count} points to {Path.GetFileName(outPath)} (NS {dNS:+0.0;-0.0}, EW {dEW:+0.0;-0.0}).");
    }

    private static bool TryParseLandblock(string s, out uint lb)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        bool ok = uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lb);
        if (ok && lb > 0xFFFF) lb >>= 16;   // accept a full cell id (0xXXYY0000)
        return ok;
    }
}
