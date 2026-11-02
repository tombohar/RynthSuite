using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Mag-Tools /mt command compatibility — maps /mt commands to /ra equivalents.
/// Partial class of RynthAiPlugin.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private Dictionary<string, int>? _spellNameToId;

    /// <summary>
    /// Handle a /mt command. Returns true if recognized and handled.
    /// Called from OnChatBarEnter (manual typing) and MetaManager (ChatCommand actions).
    /// </summary>
    internal bool HandleMtCommand(string fullCommand)
    {
        string[] parts = fullCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        string cmd = parts[1].ToLower();

        switch (cmd)
        {
            // ── Options ──────────────────────────────────────────────────────
            case "opt":
                return HandleMtOpt(parts);

            // ── Combat state ─────────────────────────────────────────────────
            case "combatstate":
                return HandleMtCombatState(parts);

            // ── Facing ───────────────────────────────────────────────────────
            case "face":
                return HandleMtFace(parts);

            // ── Cast spell ───────────────────────────────────────────────────
            case "cast":   return HandleMtCast(parts, partial: false);
            case "castp":  return HandleMtCast(parts, partial: true);

            // ── Use item ─────────────────────────────────────────────────────
            case "use":    return HandleMtUse(parts, inv: true,  land: true,  partial: false);
            case "usep":   return HandleMtUse(parts, inv: true,  land: true,  partial: true);
            case "usei":   return HandleMtUse(parts, inv: true,  land: false, partial: false);
            case "useip":
            case "usepi":  return HandleMtUse(parts, inv: true,  land: false, partial: true);
            case "usel":   return HandleMtUse(parts, inv: false, land: true,  partial: false);
            case "uselp":
            case "usepl":  return HandleMtUse(parts, inv: false, land: true,  partial: true);

            // ── Select item ──────────────────────────────────────────────────
            case "select":  HandleSelectCommand(parts, inv: true,  land: true,  partial: false); return true;
            case "selectp": HandleSelectCommand(parts, inv: true,  land: true,  partial: true);  return true;

            // ── Give item ────────────────────────────────────────────────────
            case "give":   return HandleMtGive(parts, partial: false);
            case "givep":  return HandleMtGive(parts, partial: true);

            // ── Loot item ────────────────────────────────────────────────────
            case "loot":   return HandleMtLoot(parts, partial: false);
            case "lootp":  return HandleMtLoot(parts, partial: true);

            // ── Drop item ────────────────────────────────────────────────────
            case "drop":   return HandleMtDrop(parts, partial: false);
            case "dropp":  return HandleMtDrop(parts, partial: true);

            // ── Equip / unequip ──────────────────────────────────────────────
            case "equip":   return HandleMtEquip(parts, partial: false);
            case "equipp":  return HandleMtEquip(parts, partial: true);
            case "dequip":  return HandleMtDequip(parts, partial: false);
            case "dequipp": return HandleMtDequip(parts, partial: true);

            // ── Fellowship ───────────────────────────────────────────────────
            case "fellow":
                return HandleMtFellow(parts);

            // ── Session ──────────────────────────────────────────────────────
            case "logoff":
            case "logout":
                if (Host.HasInvokeChatParser) { Host.InvokeChatParser("/logout"); Reply.Answered(); }   // the client logs out
                else ReplyFail("[RynthAi] Can't log out: the chat parser isn't available.");
                return true;
            case "quit":
            case "exit":
                // Close the client (also /ub quit|exit, which land here). "/quit" is not an AC
                // client command and did nothing; the engine's /rc quit closes the client the way
                // its close button does, logging the character off.
                if (Host.HasInvokeChatParser) { Host.InvokeChatParser("/rc quit"); Reply.Answered(); }   // the client closes
                else ReplyFail("[RynthAi] Can't close the client: the chat parser isn't available.");
                return true;

            // ── Keys in the game window (Mag-Tools /mt send); a "/" line is still sent as a command ──
            case "send":
                return HandleMtSend(parts, fullCommand);

            default:
                // jump, trade, vendor, autopack, dumpspells, click, get xy, movement, client, config,
                // strafe, test (MagToolsCommands.Extra.cs).
                return TryHandleMtExtra(cmd, parts, fullCommand);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt opt
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtOpt(string[] parts)
    {
        // /mt opt list
        // /mt opt get <option>
        // /mt opt set <option> <value>
        if (parts.Length < 3) return false;

        string sub = parts[2].ToLower();

        if (sub == "list")
        {
            var map = _metaManager?.Expressions?.BuildSettingsMapPublic();
            if (map == null) { ReplyFail("[RynthAi] Settings not ready."); return true; }
            ChatLine("[RynthAi] === Options ===");
            foreach (var kvp in map)
                ChatLine($"[RynthAi]   {kvp.Key} = {kvp.Value.Get()}");
            return true;
        }

        if (sub == "get" && parts.Length >= 4)
        {
            string optName = parts[3];
            string? val = GetOptionValue(optName);
            if (val != null && OptionKnown(optName)) ChatLine($"[RynthAi] {optName} = {val}");
            else ReplyFail($"[RynthAi] Unknown option: {optName} (/mt opt list shows them).");
            return true;
        }

        if (sub == "set" && parts.Length >= 5)
        {
            string optName = parts[3];
            string optVal  = parts[4];
            if (_metaManager?.Expressions == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return true; }
            SetOptionValue(optName, optVal);
            ReplyOptionValue(optName, optVal);
            return true;
        }

        // /mt opt remember / restore — store/recall for later
        if (sub == "remember" && parts.Length >= 4)
        {
            string optName = parts[3];
            string? val = GetOptionValue(optName);
            if (val != null)
            {
                _rememberedOptions[optName.ToLower()] = val;
                ReplyOk($"[RynthAi] Remembered {optName} = {val}"
                        + (OptionKnown(optName) ? "" : " (not an option RynthAi maps or a meta has set)"));
            }
            else ReplyFail($"[RynthAi] Unknown option: {optName} (nothing to remember).");
            return true;
        }

        if (sub == "restore" && parts.Length >= 4)
        {
            string optName = parts[3];
            if (_rememberedOptions.TryGetValue(optName.ToLower(), out string? saved))
            {
                SetOptionValue(optName, saved);
                ReplyOk($"[RynthAi] Restored {optName} = {saved}");
            }
            else ReplyFail($"[RynthAi] Nothing remembered for {optName} (/mt opt remember {optName} first).");
            return true;
        }

        return false;
    }

    /// <summary>A Mag-Tools / VTank option RynthAi maps, or one a meta has stored (an unknown name reads as 0).</summary>
    private bool OptionKnown(string optName)
        => MtOptionMap.ContainsKey(optName) || _metaManager?.Expressions?.Options.ContainsKey(optName) == true;

    /// <summary>
    /// After /mt opt set (and /ub opt set): the option's value now. Bools read back as 1/0; asked as
    /// true/false/on/off they show as True/False. An option RynthAi doesn't map is only kept for metas.
    /// </summary>
    private void ReplyOptionValue(string optName, string asked)
    {
        string now = GetOptionValue(optName) ?? "(not set)";
        bool askedBool = asked.ToLowerInvariant() is "true" or "false" or "on" or "off";
        if (askedBool && now is "1" or "0") now = now == "1" ? "True" : "False";
        ReplyOk($"[RynthAi] {optName} = {now}"
                + (MtOptionMap.ContainsKey(optName) ? "" : " (not an option RynthAi maps: kept only for metas to read)"));
    }

    /// <summary>The /mt verbs RynthAi takes, for the usage line.</summary>
    private const string MtVerbList = "opt, combatstate, face, cast[p], use[i|l][p], select[p], give[p], loot[p], drop[p], "
        + "equip[p], dequip[p], fellow, logout, quit, send, jump, trade, vendor, autopack, dumpspells, click, get xy, "
        + "cursor, movement, strafe, client, config, test";

    /// <summary>How to use a /mt (or, with <paramref name="prefix"/> "/ub", a translated /ub) verb; null when it isn't one.</summary>
    private static string? MtVerbUsage(string verb, string prefix) => verb switch
    {
        "opt" or "config" => $"{prefix} opt list | get <option> | set <option> <value> | remember <option> | restore <option>"
                             + (prefix == "/ub" ? " | toggle <options>" : ""),
        "combatstate" => $"{prefix} combatstate peace|melee|missile|magic",
        "face" => $"{prefix} face <degrees> | {prefix} face <NS>, <EW> (e.g. 42.1N, 33.6E)",
        "cast" or "castp" => $"{prefix} {verb} <spell id|name> [on <target>]",
        "use" or "usep" or "usei" or "useip" or "usepi" or "usel" or "uselp" or "usepl"
            => $"{prefix} {verb} <name> [on <name2>] | {prefix} use closestnpc|closestvendor|closestportal",
        "select" or "selectp" => $"{prefix} {verb} <name>",
        "give" or "givep" => $"{prefix} {verb} {(prefix == "/ub" ? "[count] " : "")}<item> to <target>",
        "loot" or "lootp" => $"{prefix} {verb} <item> (from the open corpse or chest)",
        "drop" or "dropp" => $"{prefix} {verb} <item>",
        "equip" or "equipp" => $"{prefix} {verb} <item>",
        "dequip" or "dequipp" => $"{prefix} {verb} <wielded item>",
        "fellow" => $"{prefix} fellow create <name> | open | close | disband | quit | recruit <name>",
        _ => null,
    };

    /// <summary>The usage line for a /mt line RynthAi didn't take (an unknown verb or the wrong arguments).</summary>
    private static string MtUsageFor(string line)
    {
        string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = w.Length > 1 ? w[1].ToLowerInvariant() : "";
        string? usage = MtVerbUsage(verb, "/mt");
        return usage != null
            ? "[RynthAi] Usage: " + usage
            : $"[RynthAi] Unknown /mt command{(verb.Length > 0 ? $" '{verb}'" : "")}. RynthAi takes: {MtVerbList}.";
    }

    private readonly Dictionary<string, string> _rememberedOptions = new(StringComparer.OrdinalIgnoreCase);

    // VTank/MagTools option name → RynthAi settings name
    private static readonly Dictionary<string, string> MtOptionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enablecombat"]              = "EnableCombat",
        ["enablelooting"]             = "EnableLooting",
        ["enablenav"]                 = "EnableNavigation",
        ["enablebuffing"]             = "EnableBuffing",
        ["enablemeta"]                = "EnableMeta",
        ["opendoors"]                 = "OpenDoors",
        ["idlepeacemode"]             = "PeaceModeWhenIdle",
        ["fastweaponswap"]            = "FastWeaponSwap",
        ["idlebufftopoff"]            = "RebuffWhenIdle",
        ["summonpets"]                = "SummonPets",
        ["combinesalvage"]            = "EnableCombineSalvage",
        ["attackdistance"]            = "MonsterRange",
        ["approachdistance"]          = "ApproachRange",
        ["navpriorityboost"]          = "BoostNavPriority",
        ["lootpriorityboost"]         = "BoostLootPriority",
        ["dooropenrange"]             = "OpenDoorRange",
        ["autofellowmanagement"]      = "AutoFellowMgmt",
        ["switchwandstodebuff"]       = "UseDispelItems",
        ["lootonlyrarecorpses"]       = "MineOnly",
        // VTank-meta migration aliases (mirror of MetaManager.VtOptionMap so options
        // set via /ub opt / /mt opt resolve the same as /vt opt set).
        ["autocram"]                  = "EnableAutocram",
        ["autostack"]                 = "EnableAutostack",
        ["readunknownscrolls"]        = "ReadUnknownScrolls",      // VTank: loots and reads unknown scrolls
        ["usedispelitems"]            = "UseDispelItems",
        ["castdispelself"]            = "CastDispelSelf",
        ["opendoorrange"]             = "OpenDoorRange",
        ["recharge-norm-hitp"]        = "HealAt",
        ["recharge-norm-mana"]        = "GetManaAt",
        ["recharge-norm-stam"]        = "RestamAt",
        ["recharge-notarg-hitp"]      = "TopOffHP",
        ["recharge-notarg-mana"]      = "TopOffMana",
        ["recharge-notarg-stam"]      = "TopOffStam",
        ["petmonsterdensity"]         = "PetMinMonsters",
        ["corpseapproachrange-max"]   = "CorpseApproachRangeMax",
        ["corpseapproachrange-min"]   = "CorpseApproachRangeMin",
        ["manastonelootcount"]        = "ManaStoneKeepCount",
        ["rebuftimeremainingseconds"] = "RebuffSecondsRemaining",
        ["navclosestoprange"]         = "NavCloseStopRange",
        ["follownavmin"]              = "FollowNavMin",
        ["outdoorhuntrange"]          = "OutdoorHuntRange",
        ["outdoorhuntradius"]         = "OutdoorHuntRadius",
        // UtilityBelt AutoVendor options (/ub opt set AutoVendor.TestMode true)
        ["autovendor.enabled"]          = "AutoVendorEnabled",
        ["autovendor.enablebuying"]     = "AutoVendorEnableBuying",
        ["autovendor.enableselling"]    = "AutoVendorEnableSelling",
        ["autovendor.testmode"]         = "AutoVendorTestMode",
        ["autovendor.think"]            = "AutoVendorThink",
        ["autovendor.showmerchantinfo"] = "AutoVendorShowMerchantInfo",
        ["autovendor.onlyfrommainpack"] = "AutoVendorOnlyFromMainPack",
        ["autovendor.tries"]            = "AutoVendorTries",
        ["autovendor.triestime"]        = "AutoVendorTriesTime",
        // UtilityBelt AutoTrade options (/ub opt set AutoTrade.AutoAccept true)
        ["autotrade.enabled"]           = "AutoTradeEnabled",
        ["autotrade.testmode"]          = "AutoTradeTestMode",
        ["autotrade.think"]             = "AutoTradeThink",
        ["autotrade.onlyfrommainpack"]  = "AutoTradeOnlyFromMainPack",
        ["autotrade.autoaccept"]        = "AutoTradeAutoAccept",
    };

    private string? GetOptionValue(string optName)
    {
        if (MtOptionMap.TryGetValue(optName, out string? raName))
        {
            var map = _metaManager?.Expressions?.BuildSettingsMapPublic();
            if (map != null && map.TryGetValue(raName, out var entry))
                return entry.Get();
        }
        // Check generic options stored by expression engine
        return _metaManager?.Expressions?.GetOption(optName);
    }

    private void SetOptionValue(string optName, string optVal)
    {
        // Normalize bool strings
        string value = optVal.ToLower() switch
        {
            "true"  => "1",
            "false" => "0",
            "on"    => "1",
            "off"   => "0",
            _       => optVal
        };

        if (MtOptionMap.TryGetValue(optName, out string? raName))
        {
            var map = _metaManager?.Expressions?.BuildSettingsMapPublic();
            if (map != null && map.TryGetValue(raName, out var entry))
            {
                entry.Set(value);
                return;
            }
        }
        // Store as generic option
        _metaManager?.Expressions?.SetOption(optName, value);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt combatstate
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtCombatState(string[] parts)
    {
        // /mt combatstate [magic,melee,missile,peace]
        if (parts.Length < 3) return false;
        if (!Host.HasChangeCombatMode) { ReplyFail("[RynthAi] ChangeCombatMode not available."); return true; }

        string mode = parts[2].ToLower();
        int cm = mode switch
        {
            "peace"   => CombatMode.NonCombat,
            "melee"   => CombatMode.Melee,
            "missile" => CombatMode.Missile,
            "magic"   => CombatMode.Magic,
            _         => 0
        };

        if (cm == 0) { ReplyFail($"[RynthAi] Unknown combat mode: {mode}. Usage: /mt combatstate peace|melee|missile|magic"); return true; }

        Host.ChangeCombatModeFor(cm, "Command", "/mt combat", force: true);
        ReplyOk($"[RynthAi] Combat mode: {mode}.");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt face
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtFace(string[] parts)
    {
        // /mt face <degrees> (Mag-Tools) and /ub face <degrees> (ub-IT, translated here). The heading
        // may have decimals (getheadingto[] gives them), read the same on every PC; or a coordinate
        // ("42.1N, 33.6E") to face that spot.
        if (parts.Length < 3) return false;
        string arg = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        double heading;
        if (double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double deg))
            heading = deg;
        else if (!TryHeadingToCoords(arg, out heading))
            return false;

        // The jumper holds navigation while it turns (nav would turn the character back) and turns
        // again until it faces the heading, as ub-IT does; without it, one plain turn.
        double norm = ((heading % 360) + 360) % 360;
        if (_jumper != null)
        {
            if (_jumper.Face(heading)) ReplyOk($"[RynthAi] Facing {norm:0.#}°.");   // a refusal says why itself
        }
        else if (Host.HasTurnToHeading) { Host.TurnToHeadingBy("Command", (float)norm); ReplyOk($"[RynthAi] Facing {norm:0.#}°."); }
        else ReplyFail("[RynthAi] TurnToHeading not available.");
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex FaceCoordsRx = new(
        @"^\s*(?<ns>\d+(?:\.\d+)?)\s*(?<nsc>[NnSs])[\s,]*(?<ew>\d+(?:\.\d+)?)\s*(?<ewc>[EeWw])\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The heading from the character to a "42.1N, 33.6E" coordinate (AC's: 0 north, 90 east, the
    /// same as getheadingto[]). False when the text isn't a coordinate or our position is unknown.
    /// </summary>
    private bool TryHeadingToCoords(string text, out double heading)
    {
        heading = 0;
        var m = FaceCoordsRx.Match(text);
        if (!m.Success || !NavCoordinateHelper.TryGetNavCoords(Host, out double myNs, out double myEw)) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double ns = double.Parse(m.Groups["ns"].Value, inv) * (char.ToUpperInvariant(m.Groups["nsc"].Value[0]) == 'S' ? -1 : 1);
        double ew = double.Parse(m.Groups["ew"].Value, inv) * (char.ToUpperInvariant(m.Groups["ewc"].Value[0]) == 'W' ? -1 : 1);
        double dNs = ns - myNs, dEw = ew - myEw;
        if (Math.Abs(dNs) < 1e-6 && Math.Abs(dEw) < 1e-6) return false;
        heading = Math.Atan2(dEw, dNs) * (180.0 / Math.PI);
        if (heading < 0) heading += 360.0;
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt cast / castp
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtCast(string[] parts, bool partial)
    {
        // /mt cast[p] <#|name> [on <target>]
        if (parts.Length < 3) return false;
        if (!Host.HasCastSpell) { ReplyFail("[RynthAi] CastSpell not available."); return true; }

        string argStr = string.Join(" ", parts, 2, parts.Length - 2);

        // Parse "X on Y" — target is optional
        uint targetId = _currentTargetId;
        int onIdx = argStr.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        string spellArg;
        if (onIdx >= 0)
        {
            spellArg = argStr[..onIdx].Trim();
            string targetName = argStr[(onIdx + 4)..].Trim();
            var targetObj = FindObject(targetName, inv: false, land: true, partial: true);
            if (targetObj != null)
                targetId = unchecked((uint)targetObj.Id);
            else
            {
                ReplyFail($"[RynthAi] Target not found: '{targetName}'");
                return true;
            }
        }
        else
        {
            spellArg = argStr.Trim();
        }

        // Try parse as spell ID first
        int spellId;
        if (!int.TryParse(spellArg, out spellId))
        {
            // Name lookup
            _spellNameToId ??= SpellDatabase.BuildNameToIdMap();
            if (partial)
            {
                // Partial match — find first spell name containing the search string
                spellId = 0;
                string search = spellArg.ToLower();
                foreach (var kvp in _spellNameToId)
                {
                    if (kvp.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        spellId = kvp.Value;
                        break;
                    }
                }
            }
            else
            {
                _spellNameToId.TryGetValue(spellArg, out spellId);
            }

            if (spellId == 0)
            {
                ReplyFail($"[RynthAi] Spell not found: '{spellArg}'");
                return true;
            }
        }

        if (targetId == 0)
        {
            // Self-cast — use player ID
            targetId = Host.GetPlayerId();
        }

        bool cast = Host.CastSpellFor(targetId, spellId, "Command", $"spell {spellId} (/mt cast)", asked: true);
        string spellName = SpellDatabase.HasSpell(spellId) ? SpellDatabase.GetSpellName(spellId) : $"spell {spellId}";
        string on = targetId == Host.GetPlayerId() ? "yourself" : (Host.TryGetObjectName(targetId, out string tn) && tn.Length > 0 ? tn : $"0x{targetId:X8}");
        if (cast) ReplyOk($"[RynthAi] Casting {spellName} on {on}.");
        else ReplyFail($"[RynthAi] Couldn't cast {spellName} on {on} (the engine refused it).");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt use (with closestnpc / closestvendor / closestportal support)
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtUse(string[] parts, bool inv, bool land, bool partial)
    {
        if (parts.Length < 3) return false;

        string argStr = string.Join(" ", parts, 2, parts.Length - 2).Trim();

        // Special targets: closestnpc, closestvendor, closestportal
        string lower = argStr.ToLower();
        if (lower == "closestportal")
        {
            UseClosestPortal();
            return true;
        }
        if (lower == "closestnpc" || lower == "closestvendor")
        {
            return HandleMtUseClosest(lower);
        }

        // Delegate to existing /ra use handler
        HandleUseCommand(parts, inv, land, partial);
        return true;
    }

    private bool HandleMtUseClosest(string type)
    {
        if (_objectCache == null || _playerId == 0)
        {
            ReplyFail("[RynthAi] Not ready.");
            return true;
        }

        if (!Host.HasUseObject) { ReplyFail("[RynthAi] UseObject not available."); return true; }

        int playerId = (int)_playerId;
        WorldObject? best = null;
        double bestDist = double.MaxValue;

        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            bool match = type switch
            {
                "closestnpc"    => wo.ObjectClass == AcObjectClass.Npc,
                "closestvendor" => wo.ObjectClass == AcObjectClass.Vendor,
                "closestportal" => wo.ObjectClass == AcObjectClass.Portal,
                _               => false
            };

            if (!match) continue;

            double dist = _objectCache.Distance(playerId, wo.Id);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = wo;
            }
        }

        if (best == null)
        {
            ReplyFail($"[RynthAi] No {type.Replace("closest", "")} found nearby.");
            return true;
        }

        Host.UseFor(unchecked((uint)best.Id), "Command", $"/mt use {type}", UseKind.Asked);
        Host.Log($"[RynthAi] /mt use {type}: {best.Name} (0x{(uint)best.Id:X8}) at {bestDist:F1}m");
        ReplyOk($"[RynthAi] Using {best.Name} ({bestDist:F1} m).");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt give[p] <item> to <target>
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtGive(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;

        string argStr = string.Join(" ", parts, 2, parts.Length - 2);
        int toIdx = argStr.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIdx < 0) { ReplyFail("[RynthAi] Usage: /mt give[p] <item> to <target>"); return true; }

        string itemName   = argStr[..toIdx].Trim();
        string targetName = argStr[(toIdx + 4)..].Trim();

        // /mt give hands over the FIRST carried match (/ub give hands over all of them).
        // Never worn or wielded gear: FindObject's inventory walk includes it.
        var candidates = GiveCandidates(n => partial
                ? n.IndexOf(itemName, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(n, itemName, StringComparison.OrdinalIgnoreCase));
        var item = candidates.Count > 0 ? candidates[0] : null;
        if (item == null) { ReplyFail($"[RynthAi] Item not found: '{itemName}'"); return true; }

        var target = FindObject(targetName, inv: false, land: true, partial: true);
        if (target == null) { ReplyFail($"[RynthAi] Target not found: '{targetName}'"); return true; }

        // Giving to an NPC requires AC's give GameAction (Event_GiveObjectRequest).
        // The old path used MoveItemExternal = move-to-CONTAINER, which does NOT
        // give to an NPC — so /mt givep silently did nothing. Use the real give
        // primitive (engine API v62+); on an older engine, say so rather than
        // pretend it worked.
        if (Host.HasGiveObjectTo)
        {
            bool ok = Host.GiveObjectTo(unchecked((uint)item.Id), unchecked((uint)target.Id), 0);
            Host.Log($"[RynthAi] /mt give: {item.Name} → {target.Name} (give ok={ok})");
            if (ok) ReplyOk($"[RynthAi] Giving {item.Name} to {target.Name}.");
            else ReplyFail($"[RynthAi] Couldn't give {item.Name} to {target.Name} (the engine refused it).");
        }
        else
        {
            ReplyFail("[RynthAi] Give requires engine API v62+ — update the RynthCore engine.");
        }
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt loot[p] <item>
    // ═════════════════════════════════════════════════════════════════════════

    // Deferred loot state — when the meta fires /mt loot in the same tick as
    // UseObject on the corpse, _openedContainerId is still 0. We queue the
    // request here and retry each heartbeat until the corpse opens (or timeout).
    private string? _pendingMtLootName;
    private bool _pendingMtLootPartial;
    private long _pendingMtLootExpiryMs;
    private const long PendingMtLootTimeoutMs = 8_000;

    /// <summary>
    /// Resolve the container /mt loot should pull from. Prefer the auto-looter's
    /// tracked open corpse (<see cref="_openedContainerId"/>), but fall back to
    /// whatever the client currently has open (GetGroundContainerId). The fallback
    /// is what lets /mt loot work on a corpse RynthAi already finished + marked
    /// completed (so SyncCorpseContainerState refuses to re-register it), or one
    /// opened manually / by the meta with auto-looting disabled.
    /// </summary>
    private int ResolveOpenLootContainer()
    {
        if (_openedContainerId != 0)
            return _openedContainerId;
        if (Host.HasGetGroundContainerId)
        {
            int raw = unchecked((int)Host.GetGroundContainerId());
            if (raw != 0)
                return raw;
        }
        return 0;
    }

    /// <summary>
    /// Enumerate a container's contents as (id, name) pairs. Prefers AC's live
    /// container query (GetContainerContents), which reads the actual client
    /// container object and is immune to the passive cache-population timing that
    /// leaves GetContainedItems empty for a freshly-opened corpse. Falls back to
    /// the ownership-probe cache scan only when the direct API isn't available or
    /// returns nothing.
    /// </summary>
    private List<(uint Id, string Name)> EnumerateContainer(int containerId)
    {
        var result = new List<(uint, string)>();
        if (containerId == 0) return result;

        if (Host.HasGetContainerContents)
        {
            uint[] buf = new uint[256];
            int count = Host.GetContainerContents(unchecked((uint)containerId), buf);
            for (int i = 0; i < count && i < buf.Length; i++)
            {
                uint id = buf[i];
                if (id == 0) continue;
                Host.TryGetObjectName(id, out string name);
                result.Add((id, name ?? string.Empty));
            }
            if (result.Count > 0) return result;
        }

        // Fallback: passive cache ownership scan.
        if (_objectCache != null)
            foreach (WorldObject wo in _objectCache.GetContainedItems(containerId))
                result.Add((unchecked((uint)wo.Id), wo.Name));

        return result;
    }

    /// <summary>
    /// Find a named item inside a container. An exact (case-insensitive) match
    /// wins; when no exact match exists we fall back to a substring match so a
    /// meta written as "/mt loot Acid" still grabs "Olthoi Acid Larva". Passing
    /// partial:true skips straight to substring matching.
    /// </summary>
    private (uint Id, string Name)? FindContainedItem(int containerId, string name, bool partial)
    {
        if (containerId == 0 || string.IsNullOrEmpty(name))
            return null;

        (uint Id, string Name)? substringHit = null;
        foreach (var item in EnumerateContainer(containerId))
        {
            if (!partial && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                return item; // exact match preferred
            if (substringHit == null &&
                item.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                substringHit = item;
        }
        return substringHit; // substring fallback (or the only mode when partial)
    }

    internal void TickPendingMtLoot()
    {
        if (_pendingMtLootName == null) return;

        int container = ResolveOpenLootContainer();
        if (container == 0) return; // still waiting for a container to open

        if (CorpseNowMs > _pendingMtLootExpiryMs)
        {
            ChatLine($"[RynthAi] /mt loot: timed out waiting for '{_pendingMtLootName}'.");
            _pendingMtLootName = null;
            return;
        }

        var found = FindContainedItem(container, _pendingMtLootName, _pendingMtLootPartial);
        if (found == null) return; // items may not be available yet — try again next tick

        Host.SelectItem(found.Value.Id);
        Host.UseFor(found.Value.Id, "Command", "/mt loot (waited for the container)", UseKind.Asked);
        Host.Log($"[RynthAi] /mt loot (deferred): {found.Value.Name} (0x{found.Value.Id:X8}) from container 0x{(uint)container:X8}");
        _pendingMtLootName = null;
    }

    private bool HandleMtLoot(string[] parts, bool partial)
    {
        // Pick a named item from the currently opened corpse/container.
        if (parts.Length < 3) return false;
        if (!Host.HasUseObject) { ReplyFail("[RynthAi] UseObject not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        int container = ResolveOpenLootContainer();

        // If a container is open, try immediately.
        if (container != 0)
        {
            var found = FindContainedItem(container, name, partial);
            if (found != null)
            {
                Host.SelectItem(found.Value.Id);
                Host.UseFor(found.Value.Id, "Command", "/mt loot", UseKind.Asked);
                Host.Log($"[RynthAi] /mt loot: {found.Value.Name} (0x{found.Value.Id:X8}) from container 0x{(uint)container:X8}");
                _pendingMtLootName = null;
                ReplyOk($"[RynthAi] Looting {found.Value.Name}.");
                return true;
            }

            // Item not found — list what IS in the container so names can be checked.
            var itemNames = new System.Text.StringBuilder();
            foreach (var item in EnumerateContainer(container))
            {
                if (itemNames.Length > 0) itemNames.Append(", ");
                itemNames.Append(item.Name);
            }
            if (itemNames.Length > 0)
                ReplyFail($"[RynthAi] /mt loot: '{name}' not found. Container 0x{(uint)container:X8} contains: {itemNames}");
            else
                ReplyFail($"[RynthAi] /mt loot: '{name}' not found (container 0x{(uint)container:X8} appears empty or still loading — queuing)");
        }

        // Container not open yet, or items not in cache — queue for deferred retry.
        _pendingMtLootName = name;
        _pendingMtLootPartial = partial;
        _pendingMtLootExpiryMs = CorpseNowMs + PendingMtLootTimeoutMs;
        Host.Log($"[RynthAi] /mt loot: '{name}' queued (container=0x{(uint)container:X8})");
        if (container == 0)
            ReplyOk($"[RynthAi] /mt loot: no container is open yet; '{name}' is taken when one opens (for {PendingMtLootTimeoutMs / 1000} s).");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt drop[p] <item>
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtDrop(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;
        if (!Host.HasMoveItemExternal) { ReplyFail("[RynthAi] MoveItemExternal not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        var item = FindObject(name, inv: true, land: false, partial: partial);
        if (item == null) { ReplyFail($"[RynthAi] Item not found in inventory: '{name}'"); return true; }

        // Drop = move to the ground (container 0)
        Host.MoveItemExternal(unchecked((uint)item.Id), 0, 0);
        Host.Log($"[RynthAi] /mt drop: {item.Name} (0x{(uint)item.Id:X8})");
        ReplyOk($"[RynthAi] Dropping {item.Name}.");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt equip[p] / dequip[p]
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtEquip(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;
        if (!Host.HasUseObject) { ReplyFail("[RynthAi] UseObject not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        var item = FindObject(name, inv: true, land: false, partial: partial);
        if (item == null) { ReplyFail($"[RynthAi] Item not found: '{name}'"); return true; }

        Host.UseFor(unchecked((uint)item.Id), "Command", "/mt use item", UseKind.Asked);
        Host.Log($"[RynthAi] /mt equip: {item.Name} (0x{(uint)item.Id:X8})");
        ReplyOk($"[RynthAi] Equipping {item.Name}.");
        return true;
    }

    private bool HandleMtDequip(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;
        if (!Host.HasMoveItemInternal) { ReplyFail("[RynthAi] MoveItemInternal not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();

        // Find a wielded item matching the name
        if (_objectCache == null) { ReplyFail("[RynthAi] Cache not ready."); return true; }

        WorldObject? found = null;
        foreach (var wo in _objectCache.GetInventory())
        {
            if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) <= 0) continue;
            bool match = partial
                ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase);
            if (match) { found = wo; break; }
        }

        if (found == null) { ReplyFail($"[RynthAi] Wielded item not found: '{name}'"); return true; }

        // FIX (2026-06-24): MoveItemInternal with amount=0 is a silent no-op — the engine
        // guard (ClientHelperHooks.cs:431 `if (amount <= 0) return false;`) rejects it, so
        // the old amount=0 call dequipped nothing. Must pass amount>=1. Also target a
        // capacity-verified open pack (not the main pack blindly) — moving into a FULL pack
        // CRASHES the client via the native PutItemInContainer path.
        int dest = WorldObjectCache.FindPackFor(Host, _objectCache, includeMainPack: true, requireFree: 1);
        if (dest == 0)
        {
            ReplyFail($"[RynthAi] /mt dequip: no open pack for '{found.Name}' — inventory full.");
            return true;
        }
        Host.MoveItemInternal(unchecked((uint)found.Id), unchecked((uint)dest), 0, 1);
        Host.Log($"[RynthAi] /mt dequip: {found.Name} (0x{(uint)found.Id:X8}) → pack 0x{(uint)dest:X8}");
        ReplyOk($"[RynthAi] Taking off {found.Name} (into a pack).");
        return true;
    }

    // FindOpenPackForDequip DELETED (P2a) — replaced by shared
    // WorldObjectCache.FindPackFor(host, cache, includeMainPack:true, requireFree:1).
    // User-driven /mt dequip: requireFree:1 so a single genuinely-free slot still
    // counts (do not report 'full' when 1 slot exists).

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt fellow
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtFellow(string[] parts)
    {
        // /mt fellow create <name>
        // /mt fellow open|close|disband|quit
        // /mt fellow recruit <name>
        // Route through chat parser since AC handles these natively
        if (parts.Length < 3) return false;
        if (!Host.HasInvokeChatParser) { ReplyFail("[RynthAi] ChatParser not available."); return true; }

        // The server answers each of these in chat ("You have created the fellowship ...").
        string sub = parts[2].ToLower();
        switch (sub)
        {
            case "create":
                if (parts.Length >= 4)
                {
                    string fellowName = string.Join(" ", parts, 3, parts.Length - 3);
                    Host.InvokeChatParser($"/fellowship create {fellowName}");
                    Reply.Answered();
                }
                else ReplyFail($"[RynthAi] Usage: {parts[0]} fellow create <fellowship name>");
                return true;
            case "open":
                Host.InvokeChatParser("/fellowship open");
                Reply.Answered();
                return true;
            case "close":
                Host.InvokeChatParser("/fellowship close");
                Reply.Answered();
                return true;
            case "disband":
                Host.InvokeChatParser("/fellowship disband");
                Reply.Answered();
                return true;
            case "quit":
                Host.InvokeChatParser("/fellowship quit");
                Reply.Answered();
                return true;
            case "recruit":
                if (parts.Length >= 4)
                {
                    string playerName = string.Join(" ", parts, 3, parts.Length - 3);
                    Host.InvokeChatParser($"/fellowship recruit {playerName}");
                    Reply.Answered();
                }
                else ReplyFail($"[RynthAi] Usage: {parts[0]} fellow recruit <player name>");
                return true;
            default:
                return false;
        }
    }
}
