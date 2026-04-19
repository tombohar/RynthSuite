using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// AutoCram + AutoStack. Ported from the legacy NexSuite2 LootManager.
///
/// AutoCram moves non-equipped loose items out of the player's main pack into
/// sub-packs so the main pack stays empty for new loot pickups.
///
/// AutoStack merges two partial stacks of the same item (arrows, components,
/// salvage, pyreals, etc.) by dropping the smaller one onto the larger one's
/// slot — the server auto-merges stackable same-type items at the same slot.
/// </summary>
public sealed class InventoryManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _cache;

    private DateTime _nextInventoryActionTime = DateTime.MinValue;
    private readonly HashSet<string> _failedStackPairs = new();
    private readonly HashSet<int> _cramBlacklist = new();
    private DateTime _lastBlacklistClear = DateTime.MinValue;
    private const double BlacklistClearIntervalMs = 30000.0;

    // AC item-type flags we care about for cram filtering
    private const uint ItemTypeContainer = 0x00000200;

    public InventoryManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache cache)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
    }

    // Kept as a no-op for call-site compatibility. Scans are now driven by
    // the backoff timer below — we re-scan every 2 seconds when idle, every
    // 500 ms after an action, so external "dirty" signals aren't needed.
    public void MarkDirty() { }

    public void OnHeartbeat(int busyCount)
    {
        if (!_settings.IsMacroRunning) return;
        if (busyCount != 0) return; // don't move items while the client is busy (casting, crafting, etc.)

        // Periodically clear stale blacklists so items that genuinely need to move
        // after a failed attempt will be retried.
        if ((DateTime.Now - _lastBlacklistClear).TotalMilliseconds >= BlacklistClearIntervalMs)
        {
            _failedStackPairs.Clear();
            _cramBlacklist.Clear();
            _lastBlacklistClear = DateTime.Now;
        }

        if (DateTime.Now < _nextInventoryActionTime) return;

        // Snapshot inventory once per tick — both passes use the same list.
        var inv = _cache.GetDirectInventory(forceRefresh: true).ToList();
        uint playerId = _host.GetPlayerId();
        if (playerId == 0 || inv.Count == 0) return;

        // AutoCram runs before AutoStack — clearing space in the main pack is
        // higher priority than tidying partials, and the two passes can starve
        // each other if AutoStack keeps finding new partial pairs every tick.
        if (ProcessAutoCram(inv, (int)playerId))
        {
            _nextInventoryActionTime = DateTime.Now.AddMilliseconds(500);
            return;
        }

        if (ProcessAutoStack(inv, (int)playerId))
        {
            _nextInventoryActionTime = DateTime.Now.AddMilliseconds(500);
            return;
        }

        // Nothing to do — back off re-scan for a bit.
        _nextInventoryActionTime = DateTime.Now.AddMilliseconds(2000);
    }

    // ── AutoCram ──────────────────────────────────────────────────────────

    private bool ProcessAutoCram(List<WorldObject> inv, int playerId)
    {
        if (!_settings.EnableAutocram) return false;

        int crammable = 0;
        foreach (var item in inv)
        {
            if (item.Container != playerId) continue;             // only main pack
            if (item.Values(LongValueKey.EquippedSlots, 0) != 0) continue; // not worn/wielded
            if (item.ObjectClass == AcObjectClass.Container) continue; // don't cram containers
            if (_cramBlacklist.Contains(item.Id)) continue;
            // Skip items the user has registered in the RynthAi Weapons tab —
            // those are weapons they want to keep accessible at the top level.
            if (_settings.ItemRules.Any(r => r.Id == item.Id)) continue;

            // Name-based foci guard — focis are also classified as containers above,
            // but catch named edge cases where itemType isn't set yet.
            if (!string.IsNullOrEmpty(item.Name)
                && item.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            // Capacity check — if this item has its own item slots, treat it as a pack.
            if (item.Values(LongValueKey.ItemsCapacity, 0) > 0) continue;
            if (item.Values(LongValueKey.ContainersCapacity, 0) > 0) continue;

            crammable++;

            int targetPack = FindOpenPack(inv, playerId);
            if (targetPack == 0)
            {
                _host.Log($"[RynthAi] AutoCram: {crammable} crammable items in main pack but no open sub-pack found");
                break; // no open packs — stop scanning this tick
            }

            int amount = item.Values(LongValueKey.StackCount, 1);
            if (amount < 1) amount = 1;

            _host.Log($"[RynthAi] AutoCram: move {item.Name} id=0x{item.Id:X8} -> pack=0x{(uint)targetPack:X8} slot=0 amount={amount}");
            bool ok = _host.MoveItemInternal(
                unchecked((uint)item.Id),
                unchecked((uint)targetPack),
                slot: 0,
                amount: amount);

            if (!ok)
                _host.Log($"[RynthAi] AutoCram: MoveItemInternal FAILED id=0x{item.Id:X8} pack=0x{(uint)targetPack:X8} slot=0 amount={amount}");

            _cramBlacklist.Add(item.Id);
            return true; // one action per tick
        }

        return false;
    }

    private int FindOpenPack(List<WorldObject> inv, int playerId)
    {
        // We deliberately do NOT count items per pack here. The engine-side
        // GetContainerContents brute-forces the entire 0x8000xxxx weenie range
        // and matches by containerID, which picks up STALE weenies for items
        // the player has merely seen/looted/dropped — their old containerID
        // field never gets cleared. That makes per-pack item counts wildly
        // inflated and causes every sub-pack to look "full".
        //
        // Instead: return the first non-foci sub-pack. Items that fail to
        // cram (because the pack is genuinely full) get blacklisted for 30s
        // via _cramBlacklist, so we don't spam retries on the same item.
        foreach (var p in inv)
        {
            if (p.Container != playerId) continue;
            if (p.ObjectClass != AcObjectClass.Container) continue;

            // Skip foci — they're technically containers but meant for spell components.
            if (!string.IsNullOrEmpty(p.Name)
                && p.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            return p.Id;
        }

        return 0;
    }

    // ── AutoStack ─────────────────────────────────────────────────────────

    private bool ProcessAutoStack(List<WorldObject> inv, int playerId)
    {
        if (!_settings.EnableAutostack) return false;

        // Build candidate list — items belonging to the player (directly or in a sub-pack).
        var playerItems = new List<WorldObject>();
        foreach (var item in inv)
        {
            bool inPlayerScope;
            if (item.Container == playerId) inPlayerScope = true;
            else
            {
                var container = inv.FirstOrDefault(c => c.Id == item.Container);
                inPlayerScope = container != null && container.Container == playerId;
            }
            if (inPlayerScope) playerItems.Add(item);
        }

        // Group by name, keep only stackable items with >1 in the same group.
        var groupList = playerItems
            .Where(x => GetMaxStackSize(x) > 1)
            .GroupBy(x => x.Name)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var g in groupList)
        {
            int max = GetMaxStackSize(g.First());

            // Read raw stack counts. Sentinel -999 distinguishes "InqInt failed" from
            // "real stack of 1" (a freshly-looted single doesn't have STACK_SIZE in the
            // qualities table because 1 is the implicit default).
            var withCounts = g
                .Select(x => (Item: x, Raw: x.Values(LongValueKey.StackCount, -999)))
                .ToList();

            // For merging, treat InqInt failure as a stack of 1 (legitimate single stack).
            // Partials = anything below the cap. Sorted descending so we walk from
            // largest target down to find one that fits with the smallest source.
            var parts = withCounts
                .Select(t => (t.Item, Count: t.Raw == -999 ? 1 : t.Raw))
                .Where(t => t.Count > 0 && t.Count < max)
                .OrderByDescending(t => t.Count)
                .ToList();

            if (parts.Count < 2) continue;

            // Pick largest target (most almost-full → topping it off removes it
            // from the partials list fastest) and smallest source (most likely
            // to be fully absorbed). The engine performs a split-merge,
            // peeling only as much from source as fits in target, so we no
            // longer need to require target+source <= max — overflow cases
            // become partial merges instead of being skipped.
            var target = parts[0];
            var source = parts[parts.Count - 1];

            string pairKey = target.Item.Id + "_" + source.Item.Id;
            if (_failedStackPairs.Contains(pairKey))
                continue;

            _host.Log($"[RynthAi] AutoStack: merge {source.Item.Name} src=0x{source.Item.Id:X8}({source.Count}) -> tgt=0x{target.Item.Id:X8}({target.Count}) max={max}");
            bool ok = _host.MergeStackInternal(
                unchecked((uint)source.Item.Id),
                unchecked((uint)target.Item.Id));

            if (!ok)
                _host.Log($"[RynthAi] AutoStack: MergeStackInternal FAILED src=0x{source.Item.Id:X8} tgt=0x{target.Item.Id:X8}");

            _failedStackPairs.Add(pairKey);
            return true; // one action per tick
        }

        return false;
    }

    // ── Stack-size lookup ─────────────────────────────────────────────────

    private static int GetMaxStackSize(WorldObject item)
    {
        int max = item.Values(LongValueKey.MaxStackSize, 0);
        if (max > 1) return max;

        // Name-based fallbacks for items whose quality property didn't resolve yet.
        string n = item.Name;
        if (string.IsNullOrEmpty(n)) return 1;

        if (n.Contains("Pyreal", StringComparison.OrdinalIgnoreCase)) return 25000;
        if (n.Contains("Trade Note", StringComparison.OrdinalIgnoreCase)) return 100;
        if (n.Contains("Taper", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Scarab", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Prismatic", StringComparison.OrdinalIgnoreCase)) return 100;
        if (n.Contains("Arrow", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Bolt", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Quarrel", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Atlatl Dart", StringComparison.OrdinalIgnoreCase)) return 250;
        if (n.Contains("Pea", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Grain", StringComparison.OrdinalIgnoreCase)) return 100;

        // Salvage bags are intentionally NOT here — each bag is a unique item
        // with quality props; they do not stack.

        return 1;
    }
}
