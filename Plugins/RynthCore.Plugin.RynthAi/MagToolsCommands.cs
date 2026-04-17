using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

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
                if (Host.HasInvokeChatParser) Host.InvokeChatParser("/logout");
                return true;
            case "quit":
            case "exit":
                if (Host.HasInvokeChatParser) Host.InvokeChatParser("/quit");
                return true;

            default:
                return false;
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
            if (map == null) { ChatLine("[RynthAi] Settings not ready."); return true; }
            ChatLine("[RynthAi] === Options ===");
            foreach (var kvp in map)
                ChatLine($"[RynthAi]   {kvp.Key} = {kvp.Value.Get()}");
            return true;
        }

        if (sub == "get" && parts.Length >= 4)
        {
            string optName = parts[3];
            string? val = GetOptionValue(optName);
            ChatLine(val != null
                ? $"[RynthAi] {optName} = {val}"
                : $"[RynthAi] Unknown option: {optName}");
            return true;
        }

        if (sub == "set" && parts.Length >= 5)
        {
            string optName = parts[3];
            string optVal  = parts[4];
            SetOptionValue(optName, optVal);
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
                Host.Log($"[RynthAi] Remembered {optName} = {val}");
            }
            return true;
        }

        if (sub == "restore" && parts.Length >= 4)
        {
            string optName = parts[3];
            if (_rememberedOptions.TryGetValue(optName.ToLower(), out string? saved))
            {
                SetOptionValue(optName, saved);
                Host.Log($"[RynthAi] Restored {optName} = {saved}");
            }
            return true;
        }

        return false;
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
        if (!Host.HasChangeCombatMode) { ChatLine("[RynthAi] ChangeCombatMode not available."); return true; }

        string mode = parts[2].ToLower();
        int cm = mode switch
        {
            "peace"   => CombatMode.NonCombat,
            "melee"   => CombatMode.Melee,
            "missile" => CombatMode.Missile,
            "magic"   => CombatMode.Magic,
            _         => 0
        };

        if (cm == 0) { ChatLine($"[RynthAi] Unknown combat mode: {mode}"); return true; }

        Host.ChangeCombatMode(cm);
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt face
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtFace(string[] parts)
    {
        // /mt face <degrees>
        if (parts.Length < 3 || !float.TryParse(parts[2], out float degrees)) return false;
        if (!Host.HasTurnToHeading) { ChatLine("[RynthAi] TurnToHeading not available."); return true; }

        Host.TurnToHeading(degrees);
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt cast / castp
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtCast(string[] parts, bool partial)
    {
        // /mt cast[p] <#|name> [on <target>]
        if (parts.Length < 3) return false;
        if (!Host.HasCastSpell) { ChatLine("[RynthAi] CastSpell not available."); return true; }

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
                ChatLine($"[RynthAi] Target not found: '{targetName}'");
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
                ChatLine($"[RynthAi] Spell not found: '{spellArg}'");
                return true;
            }
        }

        if (targetId == 0)
        {
            // Self-cast — use player ID
            targetId = Host.GetPlayerId();
        }

        Host.CastSpell(targetId, spellId);
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
        if (lower == "closestnpc" || lower == "closestvendor" || lower == "closestportal")
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
            ChatLine("[RynthAi] Not ready.");
            return true;
        }

        if (!Host.HasUseObject) { ChatLine("[RynthAi] UseObject not available."); return true; }

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
            ChatLine($"[RynthAi] No {type.Replace("closest", "")} found nearby.");
            return true;
        }

        Host.UseObject(unchecked((uint)best.Id));
        Host.Log($"[RynthAi] /mt use {type}: {best.Name} (0x{(uint)best.Id:X8}) at {bestDist:F1}m");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt give[p] <item> to <target>
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtGive(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;
        if (!Host.HasMoveItemExternal) { ChatLine("[RynthAi] MoveItemExternal not available."); return true; }

        string argStr = string.Join(" ", parts, 2, parts.Length - 2);
        int toIdx = argStr.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIdx < 0) { ChatLine("[RynthAi] Usage: /mt give[p] <item> to <target>"); return true; }

        string itemName   = argStr[..toIdx].Trim();
        string targetName = argStr[(toIdx + 4)..].Trim();

        var item = FindObject(itemName, inv: true, land: false, partial: partial);
        if (item == null) { ChatLine($"[RynthAi] Item not found: '{itemName}'"); return true; }

        var target = FindObject(targetName, inv: false, land: true, partial: true);
        if (target == null) { ChatLine($"[RynthAi] Target not found: '{targetName}'"); return true; }

        Host.MoveItemExternal(unchecked((uint)item.Id), unchecked((uint)target.Id), 0);
        Host.Log($"[RynthAi] /mt give: {item.Name} → {target.Name}");
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

    internal void TickPendingMtLoot()
    {
        if (_pendingMtLootName == null) return;
        if (_openedContainerId == 0) return; // still waiting for corpse to open

        if (CorpseNowMs > _pendingMtLootExpiryMs)
        {
            ChatLine($"[RynthAi] /mt loot: timed out waiting for '{_pendingMtLootName}' in corpse.");
            _pendingMtLootName = null;
            return;
        }

        if (_objectCache == null) return;

        WorldObject? found = null;
        foreach (WorldObject item in _objectCache.GetContainedItems(_openedContainerId))
        {
            bool match = _pendingMtLootPartial
                ? item.Name.IndexOf(_pendingMtLootName, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(item.Name, _pendingMtLootName, StringComparison.OrdinalIgnoreCase);
            if (match) { found = item; break; }
        }

        if (found == null) return; // items may not be in cache yet — try again next tick

        Host.SelectItem(unchecked((uint)found.Id));
        Host.UseObject(unchecked((uint)found.Id));
        Host.Log($"[RynthAi] /mt loot (deferred): {found.Name} (0x{(uint)found.Id:X8}) from corpse 0x{(uint)_openedContainerId:X8}");
        _pendingMtLootName = null;
    }

    private bool HandleMtLoot(string[] parts, bool partial)
    {
        // Pick a named item from the currently opened corpse container.
        if (parts.Length < 3) return false;
        if (!Host.HasUseObject) { ChatLine("[RynthAi] UseObject not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();

        // If the corpse is already open, try immediately.
        if (_openedContainerId != 0 && _objectCache != null)
        {
            WorldObject? found = null;
            foreach (WorldObject item in _objectCache.GetContainedItems(_openedContainerId))
            {
                bool match = partial
                    ? item.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase);
                if (match) { found = item; break; }
            }

            if (found != null)
            {
                Host.SelectItem(unchecked((uint)found.Id));
                Host.UseObject(unchecked((uint)found.Id));
                Host.Log($"[RynthAi] /mt loot: {found.Name} (0x{(uint)found.Id:X8}) from corpse 0x{(uint)_openedContainerId:X8}");
                _pendingMtLootName = null;
                return true;
            }

            // Item not found — list what IS in the corpse so the user can check names.
            var itemNames = new System.Text.StringBuilder();
            foreach (WorldObject item in _objectCache.GetContainedItems(_openedContainerId))
            {
                if (itemNames.Length > 0) itemNames.Append(", ");
                itemNames.Append(item.Name);
            }
            if (itemNames.Length > 0)
                ChatLine($"[RynthAi] /mt loot: '{name}' not found. Corpse contains: {itemNames}");
            else
                ChatLine($"[RynthAi] /mt loot: '{name}' not found (corpse appears empty or items still loading — queuing)");
        }

        // Corpse not open yet, or items not in cache — queue for deferred retry.
        _pendingMtLootName = name;
        _pendingMtLootPartial = partial;
        _pendingMtLootExpiryMs = CorpseNowMs + PendingMtLootTimeoutMs;
        Host.Log($"[RynthAi] /mt loot: '{name}' queued (corpse={_openedContainerId != 0})");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt drop[p] <item>
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtDrop(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;
        if (!Host.HasMoveItemExternal) { ChatLine("[RynthAi] MoveItemExternal not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        var item = FindObject(name, inv: true, land: false, partial: partial);
        if (item == null) { ChatLine($"[RynthAi] Item not found in inventory: '{name}'"); return true; }

        // Drop = move to the ground (container 0)
        Host.MoveItemExternal(unchecked((uint)item.Id), 0, 0);
        Host.Log($"[RynthAi] /mt drop: {item.Name} (0x{(uint)item.Id:X8})");
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  /mt equip[p] / dequip[p]
    // ═════════════════════════════════════════════════════════════════════════

    private bool HandleMtEquip(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;
        if (!Host.HasUseObject) { ChatLine("[RynthAi] UseObject not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();
        var item = FindObject(name, inv: true, land: false, partial: partial);
        if (item == null) { ChatLine($"[RynthAi] Item not found: '{name}'"); return true; }

        Host.UseObject(unchecked((uint)item.Id));
        Host.Log($"[RynthAi] /mt equip: {item.Name} (0x{(uint)item.Id:X8})");
        return true;
    }

    private bool HandleMtDequip(string[] parts, bool partial)
    {
        if (parts.Length < 3) return false;
        if (!Host.HasMoveItemInternal) { ChatLine("[RynthAi] MoveItemInternal not available."); return true; }

        string name = string.Join(" ", parts, 2, parts.Length - 2).Trim();

        // Find a wielded item matching the name
        if (_objectCache == null) { ChatLine("[RynthAi] Cache not ready."); return true; }

        WorldObject? found = null;
        foreach (var wo in _objectCache.GetInventory())
        {
            if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) <= 0) continue;
            bool match = partial
                ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase);
            if (match) { found = wo; break; }
        }

        if (found == null) { ChatLine($"[RynthAi] Wielded item not found: '{name}'"); return true; }

        // Move to player (main pack)
        uint playerId = Host.GetPlayerId();
        Host.MoveItemInternal(unchecked((uint)found.Id), playerId, 0, 0);
        Host.Log($"[RynthAi] /mt dequip: {found.Name} (0x{(uint)found.Id:X8}) → pack");
        return true;
    }

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
        if (!Host.HasInvokeChatParser) { ChatLine("[RynthAi] ChatParser not available."); return true; }

        string sub = parts[2].ToLower();
        switch (sub)
        {
            case "create":
                if (parts.Length >= 4)
                {
                    string fellowName = string.Join(" ", parts, 3, parts.Length - 3);
                    Host.InvokeChatParser($"/fellowship create {fellowName}");
                }
                return true;
            case "open":
                Host.InvokeChatParser("/fellowship open");
                return true;
            case "close":
                Host.InvokeChatParser("/fellowship close");
                return true;
            case "disband":
                Host.InvokeChatParser("/fellowship disband");
                return true;
            case "quit":
                Host.InvokeChatParser("/fellowship quit");
                return true;
            case "recruit":
                if (parts.Length >= 4)
                {
                    string playerName = string.Join(" ", parts, 3, parts.Length - 3);
                    Host.InvokeChatParser($"/fellowship recruit {playerName}");
                }
                return true;
            default:
                return false;
        }
    }
}
