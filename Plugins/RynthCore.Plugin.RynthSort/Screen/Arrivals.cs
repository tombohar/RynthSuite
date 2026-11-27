using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Screen;

/// <summary>
/// Which tiles just arrived in another pack, for the window's pop-in: each new view-model is
/// compared with the last one (an item whose pack changed arrived now). The first view of a
/// character arrives nothing. Render thread only (the renderer owns one); pure, so the tests drive it.
/// </summary>
internal sealed class Arrivals
{
    /// <summary>How long the pop-in lasts (seconds).</summary>
    public const double PopSeconds = 0.6;

    private readonly Dictionary<uint, int> _where = new();
    private readonly Dictionary<uint, double> _arrived = new();
    private int _version = -1;
    private string _who = string.Empty;

    /// <summary>Compares <paramref name="m"/> with the last view (once per build); <paramref name="now"/> in seconds.</summary>
    public void Update(ScreenModel m, double now)
    {
        if (m.Version == _version) return;
        _version = m.Version;
        if (!m.Ready) return;
        bool fresh = _who != m.Who || _where.Count == 0;
        _who = m.Who;
        foreach (KeyValuePair<uint, TileModel> kv in m.TilesById)
            if (!fresh && _where.TryGetValue(kv.Key, out int was) && was != kv.Value.Where) _arrived[kv.Key] = now;
        _where.Clear();
        foreach (KeyValuePair<uint, TileModel> kv in m.TilesById) _where[kv.Key] = kv.Value.Where;
        if (_arrived.Count > 64)
        {
            var old = new List<uint>();
            foreach (KeyValuePair<uint, double> kv in _arrived) if (now - kv.Value > PopSeconds) old.Add(kv.Key);
            foreach (uint id in old) _arrived.Remove(id);
        }
    }

    /// <summary>How far the pop-in of <paramref name="itemId"/> is (0 just arrived, 1 done or none).</summary>
    public float Progress(uint itemId, double now)
    {
        if (!_arrived.TryGetValue(itemId, out double at)) return 1f;
        double t = (now - at) / PopSeconds;
        if (t >= 1) { _arrived.Remove(itemId); return 1f; }
        return t <= 0 ? 0f : (float)t;
    }

    public void Clear()
    {
        _where.Clear();
        _arrived.Clear();
        _version = -1;
        _who = string.Empty;
    }
}
