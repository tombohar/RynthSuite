using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// VTank's hotkey actions as /ra commands (2026-10-13), so RynthCore's hotkeys (Settings > Hotkeys,
// "VTank-style" group), "Chat command" hotkeys, RynthNet broadcasts and metas all reach them:
//
//   /ra setmetastate <state>     Meta: <state>     switch the meta state (no state: list them)
//   /ra targetlock [off]         Target lock       attack the selected monster (again / off: release)
//   /ra fightselected [on|off]                     Fight the monster I select (no argument: toggle)
//   /ra meleelow|meleemid|meleehigh                melee attack height
//   /ra navaddpt                 NavAddPt          waypoint at your position (same as /ra addnavpt)
//   /ra testitem                 TestItem          the selected item against the loot profile
//   /ra propertydump [0xId]      PropertyDump      every property of the selection (/ra dumpprops)
//   /ra recharge                 Recharge          one vitals pass with the Top Off lines
//   /ra heal | stam | mana       One-touch heal   the best kit / spell / potion for that vital now (RynthCore's One-touch hotkeys)
//   /ra navprio|lootprio|navlootprio [on|off]      Boost Nav / Boost Loot Priority (no argument: toggle)
//   /ra lootthis                 LootThisCont      loot the open corpse or chest now
//
// Kept in this file with one call from DispatchRaCommand's default branch, so the command
// switch itself barely changes.
public sealed partial class RynthAiPlugin
{
    private static readonly string[] MeleeHeightNames = { "Low", "Medium", "High" };

    /// <summary>
    /// /ra videopatch and /ub videopatch [enable|disable|off|world|unfocused|all|fps &lt;n&gt;|status]: the
    /// engine's video patch (stop drawing AC's 3D world to save CPU and GPU, like UtilityBelt's), run as
    /// /rc videopatch. <paramref name="argsFrom"/>: the index in <paramref name="parts"/> the arguments start at.
    /// </summary>
    internal void ForwardVideoPatch(string[] parts, int argsFrom)
    {
        string args = parts.Length > argsFrom ? string.Join(" ", parts, argsFrom, parts.Length - argsFrom) : "";
        string command = "/rc videopatch" + (args.Length > 0 ? " " + args : "");
        if (!Host.HasInvokeChatParser || !Host.InvokeChatParser(command))
            ReplyFail("[RynthAi] The video patch is RynthCore's: type " + command + " (this engine can't run it for RynthAi).");
        else
            Reply.Answered();   // the engine's /rc videopatch answers
    }

    /// <summary>The VTank-style /ra commands; false when <paramref name="cmd"/> isn't one of them.</summary>
    private bool TryHandleVTankHotkeyCommand(string cmd, string[] parts)
    {
        switch (cmd)
        {
            case "vtkeys":        VTankKeysHelp(); return true;
            case "videopatch":
            case "video":         ForwardVideoPatch(parts, 2); return true;
            case "setmetastate":
            case "metastate":     HandleSetMetaStateCommand(parts); return true;
            case "targetlock":    HandleTargetLockCommand(parts); return true;
            case "fightselected":
            case "fightselect":   HandleFightSelectedCommand(parts); return true;
            case "meleelow":      SetMeleeAttackHeight(0); return true;
            case "meleemid":
            case "meleemedium":   SetMeleeAttackHeight(1); return true;
            case "meleehigh":     SetMeleeAttackHeight(2); return true;
            case "navaddpt":      HandleAddNavPointCommand(); return true;
            case "testitem":      HandleLootCheckSelectedCommand(new[] { "/ra", "lootcheck" }); return true;
            case "propertydump":  HandleDumpPropsCommand(parts); return true;
            case "recharge":      HandleRechargeCommand(); return true;
            case "heal":
            case "healme":
            case "onetouchheal":  HandleOneTouchCommand(OneTouchVital.Health); return true;
            case "stam":
            case "stamina":
            case "restam":        HandleOneTouchCommand(OneTouchVital.Stamina); return true;
            case "mana":
            case "remana":        HandleOneTouchCommand(OneTouchVital.Mana); return true;
            case "navprio":       HandlePriorityCommand(parts, nav: true, loot: false); return true;
            case "lootprio":      HandlePriorityCommand(parts, nav: false, loot: true); return true;
            case "navlootprio":   HandlePriorityCommand(parts, nav: true, loot: true); return true;
            case "lootthis":
            case "lootthiscont":  RequestLootThisContainer(); Reply.Answered(); return true;   // the loot tick says what it took
            default:              return false;
        }
    }

    private void VTankKeysHelp()
    {
        ChatLine("[RynthAi] VTank-style commands (also hotkeys: Settings > Hotkeys, VTank-style):");
        ChatLine("[RynthAi] /ra setmetastate <state> - switch the meta state (no state: list the loaded meta's states)");
        ChatLine("[RynthAi] /ra targetlock [off] - attack the selected monster first; the same one again, or off, releases it");
        ChatLine("[RynthAi] /ra fightselected [on|off] - Fight the monster I select: the monster you select is fought first (toggle)");
        ChatLine("[RynthAi] /ra meleelow | meleemid | meleehigh - melee attack height");
        ChatLine("[RynthAi] /ra navaddpt - add a waypoint at your position to the loaded route");
        ChatLine("[RynthAi] /ra testitem - check the selected item against the loot profile");
        ChatLine("[RynthAi] /ra propertydump [0xId] - every property of the selected object");
        ChatLine("[RynthAi] /ra recharge - one heal / stamina / mana top-off with the Top Off lines, macro on or off");
        ChatLine("[RynthAi] /ra heal | stam | mana - one touch: the best kit, spell or potion for that vital now, whatever the lines say");
        ChatLine("[RynthAi] /ra navprio | lootprio | navlootprio [on|off] - Boost Nav / Boost Loot Priority (toggle)");
        ChatLine("[RynthAi] /ra lootthis - loot the open corpse or chest now (never house storage or your own packs)");
    }

    // ── Meta state ────────────────────────────────────────────────────────────

    /// <summary>The loaded meta's states, in rule order.</summary>
    internal List<string> LoadedMetaStates()
    {
        var states = new List<string>();
        var s = _dashboard?.Settings;
        if (s == null) return states;
        lock (s.MetaRulesLock)
        {
            foreach (var r in s.MetaRules)
                if (!string.IsNullOrEmpty(r.State) && !states.Contains(r.State, StringComparer.OrdinalIgnoreCase))
                    states.Add(r.State);
        }
        return states;
    }

    private void HandleSetMetaStateCommand(string[] parts)
    {
        var s = _dashboard?.Settings;
        if (s == null || _metaManager == null) { ReplyFail("[RynthAi] Meta not ready (log in first)."); return; }
        List<string> states = LoadedMetaStates();
        if (parts.Length < 3)
        {
            ChatLine($"[RynthAi] Meta state: {s.CurrentState}. States: {(states.Count == 0 ? "(no meta loaded)" : string.Join(", ", states))}.");
            ReplyFail("[RynthAi] Usage: /ra setmetastate <state>");
            return;
        }
        string want = string.Join(" ", parts, 2, parts.Length - 2);
        // The loaded meta's spelling when only the case differs.
        string? known = states.FirstOrDefault(x => x.Equals(want, StringComparison.OrdinalIgnoreCase));
        _metaManager.TryHandleVtCommand("/vt setmetastate " + (known ?? want));
        if (known == null && states.Count > 0)
            ReplyFail($"[RynthAi] Note: the loaded meta has no state '{want}' (states: {string.Join(", ", states)}).");
    }

    // ── Target lock ───────────────────────────────────────────────────────────

    private void HandleTargetLockCommand(string[] parts)
    {
        var cm = _combatManager;
        if (cm == null) { ReplyFail("[RynthAi] Combat not ready (log in first)."); return; }
        string arg = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        int current = cm.ForcedTargetId;
        if (arg is "off" or "clear" or "release" or "0")
        {
            cm.ClearForcedTarget();
            ReplyOk(current == 0 ? "[RynthAi] Target lock: none set." : "[RynthAi] Target lock released.");
            return;
        }

        uint selected = Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : _currentTargetId;
        int id = unchecked((int)selected);
        // Already fought because the player selected it (Fight the monster I select): now the
        // explicit lock, which later selections don't move.
        if (cm.PromoteSelectedToTargetLock(id))
        {
            ReplyOk($"[RynthAi] Target lock: attacking {_objectCache?[id]?.Name ?? $"0x{selected:X8}"} (selecting another monster no longer moves it).");
            return;
        }
        if (id != 0 && id == current)
        {
            cm.ClearForcedTarget();
            ReplyOk($"[RynthAi] Target lock on {_objectCache?[id]?.Name ?? $"0x{selected:X8}"} released.");
            return;
        }
        string? refusal = cm.ForcedTargetRefusal(id);
        if (refusal != null)
        {
            ReplyFail($"[RynthAi] Target lock: {refusal}.");
            return;
        }
        cm.SetForcedTarget(id);
        var s = _dashboard?.Settings;
        string name = _objectCache?[id]?.Name ?? $"0x{selected:X8}";
        string note = s == null ? "" : !s.IsMacroRunning ? " (the macro is off: it attacks once started)"
            : !s.EnableCombat ? " (Combat is off: it attacks once Combat is on)" : "";
        ReplyOk($"[RynthAi] Target lock: attacking {name}{note}.");
    }

    // ── Fight the monster I select ───────────────────────────────────────────

    private void HandleFightSelectedCommand(string[] parts)
    {
        var dash = _dashboard;
        var s = dash?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string arg = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        bool? set = arg switch
        {
            "" or "toggle" => !s.FightSelectedTarget,
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => null,
        };
        if (set is not bool on)
        {
            ReplyFail($"[RynthAi] Fight the monster I select: unknown '{arg}'. Usage: /ra fightselected [on|off] (now {(s.FightSelectedTarget ? "on" : "off")}).");
            return;
        }
        s.FightSelectedTarget = on;
        dash!.SaveSettings();
        // Off: a monster it was fighting because you selected it is let go now.
        var cm = _combatManager;
        if (!on && cm != null && cm.ForcedBySelection) cm.ClearForcedTarget();
        ReplyOk(on ? "[RynthAi] Fight the monster I select: on (the monster you select in a fight is fought first)."
                   : "[RynthAi] Fight the monster I select: off.");
    }

    // ── Melee attack height ──────────────────────────────────────────────────

    private void SetMeleeAttackHeight(int height)
    {
        var dash = _dashboard;
        if (dash?.Settings == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        dash.Settings.MeleeAttackHeight = Math.Clamp(height, 0, 2);
        dash.SaveSettings();
        ReplyOk($"[RynthAi] Melee attack height: {MeleeHeightNames[dash.Settings.MeleeAttackHeight]}.");
    }

    // ── Boost Nav / Loot Priority ─────────────────────────────────────────────

    /// <summary>
    /// The new (nav, loot) boosts for a toggle or "on"/"off". NavLootPrio (both) toggles them
    /// together: on unless both are on already.
    /// </summary>
    internal static (bool Nav, bool Loot) NextPriority(bool navNow, bool lootNow, bool nav, bool loot, string arg)
    {
        bool? set = arg switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => null,
        };
        bool target = set ?? !((!nav || navNow) && (!loot || lootNow));
        return (nav ? target : navNow, loot ? target : lootNow);
    }

    private void HandlePriorityCommand(string[] parts, bool nav, bool loot)
    {
        var dash = _dashboard;
        var s = dash?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string arg = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "";
        (s.BoostNavPriority, s.BoostLootPriority) = NextPriority(s.BoostNavPriority, s.BoostLootPriority, nav, loot, arg);
        dash!.SaveSettings();
        string On(bool b) => b ? "on" : "off";
        // Owner's rule (2026-11-02): with Boost Nav on RynthAi only navigates; say so when it comes on.
        string navOnly = nav && s.BoostNavPriority ? " RynthAi only navigates (no hunting, no looting)." : "";
        ReplyOk(nav && loot ? $"[RynthAi] Boost Nav Priority {On(s.BoostNavPriority)}, Boost Loot Priority {On(s.BoostLootPriority)}.{navOnly}"
                 : nav ? $"[RynthAi] Boost Nav Priority {On(s.BoostNavPriority)}.{navOnly}"
                 : $"[RynthAi] Boost Loot Priority {On(s.BoostLootPriority)}.");
    }

    // ── Recharge ──────────────────────────────────────────────────────────────

    private void HandleRechargeCommand()
    {
        if (_buffManager == null) { ReplyFail("[RynthAi] Buff manager not ready (log in first)."); return; }
        _buffManager.RequestRecharge();   // runs on the pump's next heartbeat, says what it did
        Reply.Answered();
    }

    // ── One-touch heal / stamina / mana ───────────────────────────────────────

    private void HandleOneTouchCommand(OneTouchVital vital)
    {
        if (_buffManager == null) { ReplyFail("[RynthAi] Buff manager not ready (log in first)."); return; }
        _buffManager.RequestOneTouch(vital);   // runs on the pump's next heartbeat, says what it did
        Reply.Answered();
    }

    // ── LootThisCont ──────────────────────────────────────────────────────────

    private int _lootThisRequested;          // set by /ra lootthis (AC's thread), taken on the pump
    private int _forcedLootContainerId;      // pump thread: the container /ra lootthis flagged

    private void RequestLootThisContainer() => Interlocked.Exchange(ref _lootThisRequested, 1);

    /// <summary>
    /// The flagged container is still the one being looted: the loot tick runs for it even with
    /// the macro off, Looting off or Boost Nav Priority on. Clears the flag once it isn't.
    /// </summary>
    private bool ForcedLootHolds()
    {
        int id = _forcedLootContainerId;
        if (id == 0) return false;
        if (_openedContainerId == id || _targetCorpseId == id) return true;
        _forcedLootContainerId = 0;
        return false;
    }

    /// <summary>Why the open container <paramref name="id"/> is never looted by /ra lootthis, or null.</summary>
    internal string? LootThisRefusal(int id)
    {
        if (id == 0) return "no container is open (open a corpse or chest first)";
        uint uid = unchecked((uint)id);
        if (uid == _playerId) return "that's your own pack";
        if (Host.HasGetObjectOwnershipInfo && Host.TryGetObjectOwnershipInfo(uid, out uint container, out uint wielder, out _))
        {
            if (container == _playerId || wielder == _playerId) return "it's in your inventory";
            if (container != 0 && Host.TryGetObjectOwnershipInfo(container, out uint outer, out _, out _) && outer == _playerId)
                return "it's in your inventory";
        }
        if (InPlayerInventory(_objectCache?.GetContainerId(id) ?? 0)) return "it's in your inventory";
        if (Host.HasGetObjectBitfield && Host.TryGetObjectBitfield(uid, out uint bf))
        {
            if ((bf & ChestBfVendor) != 0) return "it's a vendor";
            if ((bf & ChestBfPlayer) != 0) return "it's a player";
        }
        if (Host.HasGetObjectInstanceIdProperty
            && Host.TryGetObjectInstanceIdProperty(uid, ChestIidHouseOwner, out uint houseOwner) && houseOwner != 0)
            return "house storage is never looted";
        string name = _objectCache?[id]?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) && Host.TryGetObjectName(uid, out string hostName)) name = hostName;
        if (IsHouseStorageName(name)) return "house storage is never looted";
        return null;
    }

    /// <summary>TickCorpseOpening, every tick: a /ra lootthis request makes the open container the loot container.</summary>
    private void TakeLootThisRequest(LegacyUiSettings settings)
    {
        if (Interlocked.Exchange(ref _lootThisRequested, 0) == 0) return;
        int id = Host.HasGetGroundContainerId ? unchecked((int)Host.GetGroundContainerId()) : 0;
        if (id == 0) id = _openedContainerId != 0 ? _openedContainerId : _chestCandidateId;

        string? refusal = LootThisRefusal(id);
        string name = id != 0 ? _objectCache?[id]?.Name ?? $"0x{(uint)id:X8}" : "";
        if (refusal != null)
        {
            ChatLine($"[RynthAi] LootThisCont: {refusal}.");
            return;
        }

        bool already = _openedContainerId == id;
        _forcedLootContainerId = id;
        if (!already)
        {
            long now = CorpseNowMs;
            if (_targetCorpseId != 0 && _targetCorpseId != id) ResetCorpseTarget();
            if (IsCorpseLikeObject(id))
            {
                // A corpse finished before (or skipped) loots again: the flag overrides the marks.
                _completedCorpses.Remove(id);
                _corpseCooldownUntil.Remove(id);
                if (_lootChestId != 0 && _lootChestId != id) _lootChestId = 0;
                _openedContainerId = id;
                _openedContainerAt = now;
                _openedContainerInventoryObservedAt = 0;
                _lastLootActionAt = 0;
                ResetLootTally();
                _corpseIdsRequested = false;
                ResetCurrentLootItem();
                _processedCorpseItems.Clear();
                _corpseItemAttempts.Clear();
                _pendingManaStoneIds.Clear(); _pendingManaTapIds.Clear(); _pendingKeepUpTo.Clear();
                _targetCorpseId = id;
                _corpseTargetSince = now;
                _lastCorpseOpenAttemptAt = 0;
            }
            else
            {
                if (_chestCandidateId != id) { _chestCandidateSince = now; _chestCandidateInvSeenAt = 0; }
                TakeChestAsLootContainer(id, now);
            }
            PauseNavigationForCorpse();
            StopCorpseMovement();
        }
        string note = !settings.IsMacroRunning ? " (the macro is off: this container only)"
            : !settings.EnableLooting ? " (Looting is off: this container only)"
            : BoostNavHoldsLoot(settings) ? " (Boost Nav Priority is on: this container only)" : "";
        LootDiag($"[RynthAi] LootThisCont: 0x{(uint)id:X8} '{name}' flagged{(already ? " (already open as the loot container)" : "")}.");
        ChatLine($"[RynthAi] LootThisCont: looting {name}{note}.");
    }
}
