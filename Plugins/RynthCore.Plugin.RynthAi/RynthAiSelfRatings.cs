using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.ItemInfo;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Item Info on yourself: with "Describe items when selected" (or the click trigger) on, selecting
/// your own character prints your ratings (SelfRatings) instead of skipping you as a non-item.
/// "/ra iteminfo me" prints them on demand. Client-side only: it identifies the player, whose
/// identify reply carries the server's resolved rating totals, and reads them from the property
/// cache. Partial class of RynthAiPlugin.
/// </summary>
public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// Wait at least this long after asking for the self-identify. There is no "identify arrived"
    /// event and the player may already have an older identify cached, so printing at once could
    /// show stale totals; one round trip is well under this.
    /// </summary>
    private const long SelfRatingsSettleMs = 700;

    /// <summary>TickCount64 of the pending self-identify; 0 = nothing pending (tick thread).</summary>
    private long _selfRatingsRequestedAt;

    /// <summary>The player's object id, or 0 before login / on a host without it.</summary>
    private uint SelfId() => Host.HasGetPlayerId ? Host.GetPlayerId() : 0;

    /// <summary>True when <paramref name="id"/> is this character.</summary>
    private bool IsSelf(uint id) => id != 0 && id == SelfId();

    /// <summary>
    /// Asks the server to identify the player and prints the ratings from <see cref="TickSelfRatings"/>.
    /// <paramref name="auto"/> (select / click triggers) shares Item Info's de-duplication, so one
    /// click that raises both triggers prints once.
    /// </summary>
    internal void QueueSelfRatings(bool auto)
    {
        uint me = SelfId();
        if (me == 0)
        {
            if (!auto) ChatLine("[RynthAi] Ratings: not logged in yet.");
            return;
        }
        int sid = unchecked((int)me);
        long now = Environment.TickCount64;
        if (auto)
        {
            if (sid == _itemInfoLastAutoId && now - _itemInfoLastAutoAt < AutoDedupeMs)
            {
                ItemInfoLog("self: already queued by the other trigger");
                return;
            }
            _itemInfoLastAutoId = sid;
            _itemInfoLastAutoAt = now;
        }

        if (!Host.HasRequestId)
        {
            // Nothing to ask with: print what the cache has (the lum augs at least).
            PrintSelfRatings(me, identified: Host.HasHasAppraisalData && Host.HasAppraisalData(me));
            return;
        }
        Host.RequestId(me);
        Reply.Answered();   // the ratings follow when the ID data arrives
        _selfRatingsRequestedAt = now;
        ItemInfoLog($"self 0x{me:X8}: identify requested for the ratings");
    }

    /// <summary>Plugin-tick half: prints once the identify is in (after the settle time) or timed out.</summary>
    private void TickSelfRatings()
    {
        long at = _selfRatingsRequestedAt;
        if (at == 0) return;
        long since = Environment.TickCount64 - at;
        if (since < SelfRatingsSettleMs) return;

        uint me = SelfId();
        if (me == 0) { _selfRatingsRequestedAt = 0; return; }
        bool ready = !Host.HasHasAppraisalData || Host.HasAppraisalData(me);
        long timeoutMs = (long)((ItemInfoSettings?.IdTimeoutSec ?? 3f) * 1000f);
        if (!ready && since < timeoutMs) return;

        _selfRatingsRequestedAt = 0;
        PrintSelfRatings(me, ready);
    }

    /// <summary>Writes the ratings in Item Info's prefix, layout and chat colours.</summary>
    private void PrintSelfRatings(uint me, bool identified)
    {
        int id = unchecked((int)me);
        var s = ItemInfoSettings;
        bool oneLine = (s?.Layout ?? MagItemInfoSettings.LayoutPetLines) == MagItemInfoSettings.LayoutOneLine;
        string name = _objectCache?[id]?.Name ?? string.Empty;
        List<string> lines = SelfRatings.Build(name, stype => ReadSelfInt(id, stype), identified, oneLine);
        ItemInfoLog($"self 0x{me:X8}: printed {lines.Count} line(s), identified={identified}: {lines[0]}");

        string prefix = s?.Prefix ?? "[RynthAi Item] ";
        int headerType = s?.ChatType ?? 1;
        int detailType = s == null || s.DetailChatType == MagItemInfoSettings.SameAsHeader ? headerType : s.DetailChatType;
        WriteItemInfoLine(prefix + lines[0], headerType);
        for (int i = 1; i < lines.Count; i++)
            WriteItemInfoLine(prefix + "  " + lines[i], detailType);
    }

    /// <summary>One of the player's int properties from the cache (PlayerDescription, else the last identify); 0 when absent.</summary>
    private int ReadSelfInt(int id, uint stype)
    {
        if (_objectCache != null) return _objectCache.GetIntProperty(id, stype, 0);
        return Host.HasGetObjectIntProperty && Host.TryGetObjectIntProperty(unchecked((uint)id), stype, out int v) ? v : 0;
    }
}
