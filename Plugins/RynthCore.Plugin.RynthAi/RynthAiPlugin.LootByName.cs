using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

// Loot by name (2026-11-02), the host side. See LootByName.cs for the list and the bookkeeping.
//
//   /ra lootname add <name> | remove <name> | list | radius <yards> | clear
//
// While the bot runs with Looting on, an object in the world (not carried, not a corpse, a player or
// a monster) whose name is on the list and that lies inside the radius is USED, which is how AC opens
// a container or picks an item up: the client walks there by itself. Looting is the activity that
// does this, so it outranks hunting and navigation the way a corpse does (HasLootWork asks
// HasLootNameWork; TickCorpseOpening calls TryLootByName). A container that opens is looted by the
// chest looter (RynthAiPlugin.ChestLoot.cs) with the loaded loot profile.
public sealed partial class RynthAiPlugin
{
    private LootByNameList? _lootNames;
    private readonly LootNameTracker _lootNameTracker = new();
    private int _lootNameTargetId;            // the object the next TryLootByName works on (0 = none)
    private long _lootNameScanAt;
    private long _lootNameLoggedFor;

    private const long LootNameScanMs = 500;

    private string? _lootNamesSrc;
    private float _lootNamesRadiusSrc;

    /// <summary>The list the settings hold (Settings > Looting > Loot By Name, or /ra lootname); rebuilt when they change.</summary>
    private LootByNameList LootNames
    {
        get
        {
            var st = _dashboard?.Settings;
            string names = st?.LootByNames ?? string.Empty;
            float radius = st?.LootByNameRadius ?? (float)LootByNameList.DefaultRadius;
            if (_lootNames == null || names != _lootNamesSrc || radius != _lootNamesRadiusSrc)
            {
                _lootNames = LootByNameList.Parse(names, radius);
                _lootNamesSrc = names;
                _lootNamesRadiusSrc = radius;
            }
            return _lootNames;
        }
    }

    // ── /ra lootname ───────────────────────────────────────────────────────

    private void HandleLootNameCommand(string[] parts)
    {
        string sub = parts.Length > 2 ? parts[2].ToLowerInvariant() : "list";
        string rest = parts.Length > 3 ? string.Join(" ", parts, 3, parts.Length - 3).Trim() : "";
        var st = _dashboard?.Settings;
        if (st == null) { ChatLine("[RynthAi] Settings not ready."); return; }
        var list = LootByNameList.Parse(st.LootByNames, st.LootByNameRadius);   // edited, then written back
        switch (sub)
        {
            case "add":
                if (rest.Length == 0) { ChatLine("[RynthAi] Usage: /ra lootname add <name>   (*part* matches part of a name)"); return; }
                if (!list.Add(rest)) { ChatLine($"[RynthAi] Loot by name: '{rest}' is already on the list (or the list is full)."); return; }
                SaveLootNames(list);
                ChatLine($"[RynthAi] Loot by name: added '{rest}' ({list.Names.Count} on the list, radius {list.Radius:0} yd).");
                return;
            case "remove":
            case "delete":
                if (!list.Remove(rest)) { ChatLine($"[RynthAi] Loot by name: '{rest}' isn't on the list."); return; }
                SaveLootNames(list);
                ChatLine($"[RynthAi] Loot by name: removed '{rest}'.");
                return;
            case "clear":
                list.Clear();
                _lootNameTracker.Clear();
                SaveLootNames(list);
                ChatLine("[RynthAi] Loot by name: the list is empty.");
                return;
            case "radius":
            case "range":
                if (!double.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double yd))
                { ChatLine($"[RynthAi] Usage: /ra lootname radius <{LootByNameList.MinRadius:0}-{LootByNameList.MaxRadius:0} yards>"); return; }
                list.SetRadius(yd);
                SaveLootNames(list);
                ChatLine($"[RynthAi] Loot by name: radius {list.Radius:0} yd.");
                return;
            case "list":
            case "status":
            default:
                if (list.IsEmpty)
                    ChatLine("[RynthAi] Loot by name: nothing on the list. /ra lootname add <name>");
                else
                    ChatLine($"[RynthAi] Loot by name (radius {list.Radius:0} yd, while looting is on): {string.Join(" | ", list.Names)}");
                return;
        }
    }

    /// <summary>Writes the list back to the settings (the profile saves them like any other setting).</summary>
    private void SaveLootNames(LootByNameList list)
    {
        var st = _dashboard?.Settings;
        if (st == null) return;
        st.LootByNames = list.ToSettingString();
        st.LootByNameRadius = (float)list.Radius;
    }

    // ── The work ───────────────────────────────────────────────────────────

    /// <summary>HasLootWork: a named object is in range and worth a use (nothing is changed but the pick).</summary>
    private bool HasLootNameWork(LegacyUiSettings settings, long now)
    {
        var list = LootNames;
        if (list.IsEmpty || _objectCache == null || _playerId == 0) { _lootNameTargetId = 0; return false; }
        if (IsLootingBlockedByPack(settings)) { _lootNameTargetId = 0; return false; }

        // A scan twice a second; between scans the pick stands (it is dropped when it is no longer usable).
        if (now - _lootNameScanAt >= LootNameScanMs || _lootNameTargetId == 0)
        {
            _lootNameScanAt = now;
            _lootNameTargetId = PickLootNameTarget(list, now);
        }
        else if (_lootNameTracker.IsBlocked(_lootNameTargetId, now))
            _lootNameTargetId = 0;
        return _lootNameTargetId != 0;
    }

    private int PickLootNameTarget(LootByNameList list, long now)
    {
        var cache = _objectCache!;
        int best = 0;
        double bestDist = double.MaxValue;
        var present = new HashSet<int>();
        foreach (var wo in cache.GetLandscapeObjects())
        {
            present.Add(wo.Id);
            if (unchecked((uint)wo.Id) == _playerId) continue;
            if (!list.MatchesAny(wo.Name)) continue;
            if (wo.ObjectClass is AcObjectClass.Player or AcObjectClass.Monster or AcObjectClass.Corpse) continue;
            if (wo.Container != 0 || wo.Wielder != 0) continue;

            // Opened (the chest looter has it, or is about to): finished until it is gone.
            if (wo.Id == _chestCandidateId || wo.Id == _lootChestId) { _lootNameTracker.MarkDone(wo.Id); continue; }
            if (_lootNameTracker.Exhausted(wo.Id, now)) { _lootNameTracker.GiveUp(wo.Id, now); continue; }
            if (_lootNameTracker.IsBlocked(wo.Id, now)) continue;

            double d = cache.Distance(unchecked((int)_playerId), wo.Id);
            if (d > list.Radius || d >= bestDist) continue;
            best = wo.Id;
            bestDist = d;
        }
        _lootNameTracker.Prune(present.Contains);
        return best;
    }

    /// <summary>
    /// TickCorpseOpening, nothing claimed or open: uses the picked object when its turn comes, else waits for the
    /// client to walk there. True when this tick belongs to it.
    /// </summary>
    private bool TryLootByName(LegacyUiSettings settings, long now)
    {
        int id = _lootNameTargetId;
        if (id == 0 || _targetCorpseId != 0 || _openedContainerId != 0 || _chestCandidateId != 0)
            return false;
        if (_objectCache?[id] == null) { _lootNameTargetId = 0; return false; }

        if (_activity != BotActivity.Looting)
            return true;   // the arbiter has not given Looting the tick yet

        PauseNavigationForCorpse();
        if (!_lootNameTracker.CanUse(id, now))
            return true;   // walking there / waiting for the server

        string name = _objectCache![id]?.Name ?? string.Empty;
        int uses = _lootNameTracker.Uses(id) + 1;
        StopCorpseMovement();
        bool sent = Host.UseFor(unchecked((uint)id), "LootName", $"loot by name: '{name}' (try {uses})", UseKind.Asked);
        if (sent)
        {
            _lootNameTracker.NoteUse(id, now);
            if (_lootNameLoggedFor != id)
            {
                _lootNameLoggedFor = id;
                LootDiag($"[RynthAi] Loot by name: using 0x{(uint)id:X8} '{name}'.");
            }
        }
        return true;
    }
}
