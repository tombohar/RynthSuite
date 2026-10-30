using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// Items this plugin just used, or used something on (Host.ObjectUsed), whose
/// identified properties may now be out of date: uses left on a pet essence after
/// summoning or refilling, a kit's uses, a stack's size. The expression engine
/// re-identifies them on the next property read (ExpressionEngine.EnsureIdentified),
/// so metas don't act on the value from before the use. Engine-independent, so it
/// also covers engines whose property-update hook is missing or broken.
/// </summary>
internal static class ObjectChangeTracker
{
    private static readonly Dictionary<uint, long> _changedAtSec = new();

    public static void MarkChanged(uint id)
    {
        if (id == 0) return;
        lock (_changedAtSec)
        {
            if (_changedAtSec.Count > 500) _changedAtSec.Clear();
            _changedAtSec[id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    /// <summary>
    /// True while <paramref name="id"/> changed after its last identify
    /// (<paramref name="lastIdUnixSec"/>). A later identify clears it.
    /// </summary>
    public static bool IsStale(uint id, long lastIdUnixSec)
    {
        lock (_changedAtSec)
        {
            if (!_changedAtSec.TryGetValue(id, out long changed)) return false;
            if (lastIdUnixSec > changed) { _changedAtSec.Remove(id); return false; }
            return true;
        }
    }

    public static void Clear()
    {
        lock (_changedAtSec) _changedAtSec.Clear();
    }
}
