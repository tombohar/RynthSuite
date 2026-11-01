using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// Packs the server would not take loot into. The destination comes from the client's own
/// item counts, and a side pack whose contents the client never loaded counts as nearly empty
/// however full it is: every item went to that one pack, the server refused all 3 tries each
/// time, and every item on every corpse was skipped (Drakkon, 2026-10-15, 62 items in a row).
/// A pack that takes none of an item's tries counts as full for <see cref="HoldMs"/>; the
/// destination search skips it, so the next pack is tried. Pure: the caller passes the time.
///
/// 2026-10-29 (R-20261029-122319-321f, Conquest): a mark is CONFIRMED only when the server
/// answered the tries with a refusal after it had run the pickup (ACE's "Unable to put X into
/// container" + InventoryServerSaveFailed). Tries that simply had no answer in time mark the
/// pack UNCONFIRMED: the item tries another pack, but the mark is the item's alone and is
/// dropped when the item is done (<see cref="ClearUnconfirmed"/>). Quick unanswered retries
/// on a slow server had marked one good pack after another full for 5 min, until looting
/// "locked up" with nothing left to put loot into.
///
/// 2026-11-01 (#rynth-issues "Stopped looting", Conquest treasure maps with pickup timers): an
/// item the server refuses for itself, not for room, was refused by every pack it tried, and
/// each one was marked full: the next item had nowhere to go, so it was selected and never
/// picked up, corpse after corpse. A confirmed mark remembers the item whose refusals made it;
/// an item refused in two or more packs takes its marks back with it (<see cref="Withdraw"/>).
/// A pack another item was refused in as well stays marked: then it is the pack.
/// </summary>
internal sealed class PackRefusals
{
    /// <summary>How long a refusing pack is left out (time to free some room, or open it).</summary>
    public const long HoldMs = 5 * 60_000;

    // Item: the one item whose refusals made the mark; 0 = more than one item's (or not said).
    private readonly Dictionary<int, (long Until, bool Confirmed, int Item)> _until = new();
    // Marks taken back with their item (Withdraw): another item refused there too confirms the pack.
    private readonly Dictionary<int, (long Until, int Item)> _withdrawn = new();

    /// <summary>
    /// The server took none of the tries into <paramref name="packId"/>. <paramref name="confirmed"/>:
    /// it said so (refusals after the pickup ran); false: the tries went unanswered. A confirmed
    /// mark is never weakened by a later unconfirmed one. <paramref name="itemId"/>: the item
    /// refused (0: not said, the mark is never withdrawn).
    /// </summary>
    public void Refuse(int packId, long nowMs, bool confirmed = true, int itemId = 0)
    {
        if (packId == 0) return;
        int by = itemId;
        if (_until.TryGetValue(packId, out var old) && nowMs < old.Until)
        {
            if (old.Confirmed && !confirmed)
                return;
            if (old.Confirmed && old.Item != itemId)
                by = 0;   // another item was refused here too
        }
        if (confirmed && by != 0 && _withdrawn.TryGetValue(packId, out var w) && nowMs < w.Until && w.Item != itemId)
            by = 0;       // so was the item that took its mark back
        _until[packId] = (nowMs + HoldMs, confirmed, by);
    }

    /// <summary>True while <paramref name="packId"/> is left out.</summary>
    public bool IsRefused(int packId, long nowMs)
    {
        if (!_until.TryGetValue(packId, out var mark)) return false;
        if (nowMs < mark.Until) return true;
        _until.Remove(packId);
        return false;
    }

    /// <summary>True while <paramref name="packId"/> is left out on the server's own refusals.</summary>
    public bool IsConfirmed(int packId, long nowMs) =>
        IsRefused(packId, nowMs) && _until.TryGetValue(packId, out var mark) && mark.Confirmed;

    /// <summary>An item landed in <paramref name="packId"/>: it has room after all.</summary>
    public void Accepted(int packId)
    {
        _until.Remove(packId);
        _withdrawn.Remove(packId);
    }

    /// <summary>
    /// The server refused <paramref name="itemId"/> in every pack it tried: it is refusing the item,
    /// not the packs. Takes back the confirmed marks that item's refusals alone made and returns
    /// those packs (a mark another item's refusals back as well stays).
    /// </summary>
    public List<int> Withdraw(int itemId, long nowMs)
    {
        var packs = new List<int>();
        if (itemId == 0) return packs;
        foreach (var kv in _until)
            if (kv.Value.Confirmed && kv.Value.Item == itemId && nowMs < kv.Value.Until) packs.Add(kv.Key);
        foreach (int id in packs)
        {
            _until.Remove(id);
            _withdrawn[id] = (nowMs + HoldMs, itemId);
        }
        return packs;
    }

    /// <summary>Drops the marks no refusal backed (the item that made them is done).</summary>
    public void ClearUnconfirmed()
    {
        if (_until.Count == 0) return;
        List<int>? drop = null;
        foreach (var kv in _until)
            if (!kv.Value.Confirmed) (drop ??= new()).Add(kv.Key);
        if (drop != null)
            foreach (int id in drop) _until.Remove(id);
    }

    public void Clear()
    {
        _until.Clear();
        _withdrawn.Clear();
    }
}
