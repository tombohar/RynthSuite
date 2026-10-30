using System;

namespace RynthCore.Plugin.RynthInventory.Scanning;

/// <summary>
/// When to look at the inventory, when to scan it fully and when to write the file. Pure
/// timing logic (milliseconds in, decisions out) so Tools\RynthInventory.Tests can drive it.
///
///   Poll        a cheap fingerprint (ids, containers, wielders, stack sizes) every
///               <see cref="PollMs"/>, sooner after an inventory event.
///   Full scan   every item's details. After login (three times while the client settles and
///               the engine's auto-identify fills in spells), once the fingerprint has changed
///               and then held still for <see cref="SettleMs"/> (or kept changing for
///               <see cref="MaxSettleMs"/>), and every <see cref="PeriodicMs"/> to pick up
///               details that arrive without a visible change.
///   Write       only when a scan's content differs from what was last written, after
///               <see cref="WriteQuietMs"/> without another change or at most
///               <see cref="WriteMaxDelayMs"/> after the first unsaved one. A loot spree or a
///               salvage run therefore writes about twice a minute, not once per item.
/// </summary>
internal sealed class ScanScheduler
{
    public const long PollMs = 1500;
    public const long EventPollDelayMs = 400;
    public const long SettleMs = 2000;
    public const long MaxSettleMs = 10_000;
    public const long PeriodicMs = 60_000;
    public const long WriteQuietMs = 5000;
    public const long WriteMaxDelayMs = 30_000;
    public static readonly long[] LoginScansMs = { 3000, 12_000, 40_000 };

    private bool _active;
    private long _nextPollMs;
    private ulong _fingerprint;
    private bool _haveFingerprint;
    private long _changeFirstMs = -1, _changeLastMs = -1;
    /// <summary>The last time the poll saw the inventory change (the write waits for this to go quiet).</summary>
    private long _activityMs = -1;
    private long _nextPeriodicMs;
    private int _loginScan;
    private long _loginMs;
    private bool _fullScanWanted;

    private ulong _savedHash;
    private bool _haveSaved;
    private ulong _pendingHash;
    private long _dirtyFirstMs = -1, _dirtyLastMs = -1;

    public bool Active => _active;
    public bool Dirty => _dirtyFirstMs >= 0;

    /// <summary>A character is in the world: start over (nothing carries over from the last one).</summary>
    public void Login(long now)
    {
        _active = true;
        _loginMs = now;
        _loginScan = 0;
        _nextPollMs = now + LoginScansMs[0];
        _haveFingerprint = false;
        _changeFirstMs = _changeLastMs = -1;
        _activityMs = -1;
        _nextPeriodicMs = long.MaxValue;
        _fullScanWanted = false;
        _haveSaved = false;
        _dirtyFirstMs = _dirtyLastMs = -1;
    }

    public void Logout()
    {
        _active = false;
        _dirtyFirstMs = _dirtyLastMs = -1;
    }

    /// <summary>The file on disk already holds content with this hash (loaded at login, or just written).</summary>
    public void KnownSaved(ulong hash)
    {
        _savedHash = hash;
        _haveSaved = true;
    }

    /// <summary>Something that may have touched the inventory happened (an object created, deleted, a container's contents).</summary>
    public void InventoryEvent(long now)
    {
        if (!_active) return;
        long at = now + EventPollDelayMs;
        if (at < _nextPollMs && now >= _loginMs + LoginScansMs[0]) _nextPollMs = at;
    }

    /// <summary>Asks for a full scan at the next tick (the player's /ginv scan).</summary>
    public void RequestFullScan() => _fullScanWanted = true;

    public bool ShouldPoll(long now) => _active && now >= _nextPollMs;

    /// <summary>The fingerprint the poll computed.</summary>
    public void PollResult(long now, ulong fingerprint)
    {
        _nextPollMs = now + PollMs;
        if (!_haveFingerprint)
        {
            _haveFingerprint = true;
            _fingerprint = fingerprint;
            return;
        }
        if (fingerprint == _fingerprint) return;
        _fingerprint = fingerprint;
        if (_changeFirstMs < 0) _changeFirstMs = now;
        _changeLastMs = now;
        _activityMs = now;
    }

    public bool ShouldFullScan(long now)
    {
        if (!_active) return false;
        if (_fullScanWanted) return true;
        if (_loginScan < LoginScansMs.Length && now >= _loginMs + LoginScansMs[_loginScan]) return true;
        if (_changeFirstMs >= 0 && (now - _changeLastMs >= SettleMs || now - _changeFirstMs >= MaxSettleMs)) return true;
        return now >= _nextPeriodicMs;
    }

    /// <summary>A full scan finished with this content hash (and fingerprint, so the next poll doesn't re-trigger).</summary>
    public void FullScanDone(long now, ulong contentHash, ulong fingerprint)
    {
        _fullScanWanted = false;
        while (_loginScan < LoginScansMs.Length && now >= _loginMs + LoginScansMs[_loginScan]) _loginScan++;
        _changeFirstMs = _changeLastMs = -1;
        _fingerprint = fingerprint;
        _haveFingerprint = true;
        _nextPeriodicMs = now + PeriodicMs;
        if (_nextPollMs < now + PollMs) _nextPollMs = now + PollMs;
        ContentChanged(now, contentHash);
    }

    /// <summary>The saved content changed outside a full scan (a storage container was read).</summary>
    public void ContentChanged(long now, ulong contentHash)
    {
        if (_haveSaved && contentHash == _savedHash)
        {
            // Back to what's on disk (e.g. an item moved out and back): nothing to write.
            _dirtyFirstMs = _dirtyLastMs = -1;
            return;
        }
        if (_dirtyFirstMs >= 0 && contentHash == _pendingHash) return;
        _pendingHash = contentHash;
        if (_dirtyFirstMs < 0) _dirtyFirstMs = now;
        _dirtyLastMs = now;
    }

    /// <summary>
    /// Unsaved content, and either nothing has changed for <see cref="WriteQuietMs"/> (neither
    /// the content nor what the poll sees: during a loot spree the scans only run every
    /// <see cref="MaxSettleMs"/>, so the content alone would look quiet in between) or the
    /// first unsaved change is <see cref="WriteMaxDelayMs"/> old.
    /// </summary>
    public bool ShouldWrite(long now)
    {
        if (_dirtyFirstMs < 0) return false;
        long last = Math.Max(_dirtyLastMs, _activityMs);
        return now - last >= WriteQuietMs || now - _dirtyFirstMs >= WriteMaxDelayMs;
    }

    public void Written(ulong contentHash)
    {
        KnownSaved(contentHash);
        _dirtyFirstMs = _dirtyLastMs = -1;
    }

    /// <summary>A write failed: try again after the quiet time.</summary>
    public void WriteFailed(long now)
    {
        if (_dirtyFirstMs < 0) return;
        _dirtyFirstMs = now;
        _dirtyLastMs = now;
    }
}
