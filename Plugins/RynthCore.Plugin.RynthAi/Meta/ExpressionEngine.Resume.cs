// ExpressionEngine.Resume.cs - The meta's $variables, dictionaries and stopwatches out of and back into the
// engine, for Resume Meta After Reload (MetaResume.cs). @pvars and &gvars are on disk and need nothing.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed partial class ExpressionEngine
{
    /// <summary>Copies every $variable, dictionary and stopwatch (and the next handles) into <paramref name="s"/>.</summary>
    public void ExportVariables(MetaResumeState s)
    {
        s.Variables = new Dictionary<string, string>(_variables, StringComparer.OrdinalIgnoreCase);
        s.Dicts = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var d in _dicts) s.Dicts[d.Key] = new Dictionary<string, string>(d.Value, StringComparer.Ordinal);
        s.NextDictId = _nextDictId;
        long now = TickMs();
        s.Stopwatches = new Dictionary<string, (double Seconds, bool Running)>(StringComparer.Ordinal);
        foreach (var sw in _stopwatches) s.Stopwatches[sw.Key] = (sw.Value.ElapsedMs(now) / 1000.0, sw.Value.Running);
        s.NextStopwatchId = _nextSwId;
    }

    /// <summary>Replaces the $variables, dictionaries and stopwatches with the saved ones. A handle ("D:3",
    /// "SW:4") held in a variable names the same dict or stopwatch again; new handles never reuse a saved one.
    /// A running stopwatch runs on from its saved time (the reload's few seconds aren't counted, as with the
    /// seconds-in-state clocks).</summary>
    public void RestoreVariables(MetaResumeState s)
    {
        _variables.Clear();
        foreach (var kv in s.Variables) _variables[kv.Key] = kv.Value;
        _dicts.Clear();
        foreach (var d in s.Dicts) _dicts[d.Key] = new Dictionary<string, string>(d.Value, StringComparer.Ordinal);
        _nextDictId = Math.Max(_nextDictId, s.NextDictId);
        _dictSweepAt = Math.Max(DictSweepMin, _dicts.Count * 2);
        long now = TickMs();
        _stopwatches.Clear();
        foreach (var sw in s.Stopwatches)
            _stopwatches[sw.Key] = new MetaStopwatch
            {
                AccumulatedMs = (long)Math.Round(Math.Max(0, sw.Value.Seconds) * 1000.0),
                Running = sw.Value.Running,
                StartedAtMs = now,
            };
        _nextSwId = Math.Max(_nextSwId, s.NextStopwatchId);
    }
}
