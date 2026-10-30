using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Bounded per-object diagnostics for <see cref="WorldObjectCache"/> ([ClassifyTrace] and
/// [ReclassifyDiag]). Each kind of event keeps a few full detail lines per session; after that
/// it is only counted, and <see cref="Flush"/> writes one summary line per prefix (counts, the
/// sub-flags the old detail lines carried, a few sample ids) on the cache's 2 s cadence.
///
/// Why (2026-10-02, Matron Hive South copy): the old lines were one per object per attempt
/// with caps of 200-5000 each, and every engine log line is a synchronous open-append-close of
/// the log file on the plugin thread (~350 us). A flood of 1000 creates and 585 deletes wrote
/// ~950 lines, a third of a second of the plugin tick, exactly when the tick was busiest.
///
/// Not thread-safe: the cache calls it under its own _gate.
/// </summary>
internal sealed class ObjectDiagLog
{
    private const int MaxSamples = 6;

    private sealed class Kind
    {
        public required string Name;
        public required string Prefix;
        public required string[] FlagNames;
        public int DetailLeft;
        public int Count;
        public readonly int[] Flags = new int[4];
        public readonly uint[] Samples = new uint[MaxSamples];
        public int SampleCount;
        public long Total;
    }

    private readonly Action<string> _log;
    private readonly Dictionary<string, Kind> _kinds = new(StringComparer.Ordinal);
    private readonly List<Kind> _order = new();

    public ObjectDiagLog(Action<string> log) => _log = log;

    /// <summary>Lines written by <see cref="Flush"/> so far (tests).</summary>
    public int SummaryLines { get; private set; }

    /// <summary>Declares a kind: its log prefix, how many full detail lines a session may write,
    /// and up to four sub-flags counted per event (e.g. "seenCreate").</summary>
    public void Define(string name, string prefix, int detailLines, params string[] flagNames)
    {
        var k = new Kind { Name = name, Prefix = prefix, FlagNames = flagNames, DetailLeft = detailLines };
        _kinds[name] = k;
        _order.Add(k);
    }

    /// <summary>
    /// Counts one event of <paramref name="kind"/> for <paramref name="id"/>; bit i of
    /// <paramref name="flags"/> counts the kind's i-th sub-flag. True while the kind still has
    /// detail lines left: the caller then writes its full line (the summary counts it too).
    /// </summary>
    public bool Note(string kind, uint id, int flags = 0)
    {
        if (!_kinds.TryGetValue(kind, out Kind? k))
            return false;
        k.Count++;
        k.Total++;
        for (int i = 0; i < k.FlagNames.Length && i < k.Flags.Length; i++)
            if ((flags & (1 << i)) != 0) k.Flags[i]++;
        if (k.SampleCount < MaxSamples)
            k.Samples[k.SampleCount++] = id;
        if (k.DetailLeft > 0)
        {
            k.DetailLeft--;
            return true;
        }
        return false;
    }

    /// <summary>Events of <paramref name="kind"/> since the session started.</summary>
    public long Total(string kind) => _kinds.TryGetValue(kind, out Kind? k) ? k.Total : 0;

    /// <summary>
    /// Writes one line per prefix covering every kind counted since the last flush, e.g.
    /// "[ReclassifyDiag] last 2 s: DELETE-BEFORE-CLASSIFY 585 (seenCreate 12) e.g. 0x8000CC23 ...".
    /// Writes nothing when nothing happened.
    /// </summary>
    public void Flush(string window)
    {
        StringBuilder? sb = null;
        string? prefix = null;
        foreach (Kind k in _order)
        {
            if (k.Count == 0) continue;
            if (prefix != k.Prefix)
            {
                if (sb != null) Emit(sb);
                prefix = k.Prefix;
                sb = new StringBuilder(160).Append(k.Prefix).Append(" last ").Append(window).Append(':');
            }
            else
            {
                sb!.Append(';');
            }
            sb!.Append(' ').Append(k.Name).Append(' ').Append(k.Count);
            bool any = false;
            for (int i = 0; i < k.FlagNames.Length && i < k.Flags.Length; i++)
            {
                sb.Append(any ? ", " : " (").Append(k.FlagNames[i]).Append(' ').Append(k.Flags[i]);
                any = true;
            }
            if (any) sb.Append(')');
            sb.Append(" e.g.");
            for (int i = 0; i < k.SampleCount; i++)
                sb.Append(" 0x").Append(k.Samples[i].ToString("X8"));
            if (k.Count > k.SampleCount) sb.Append(" ...");

            k.Count = 0;
            k.SampleCount = 0;
            Array.Clear(k.Flags);
        }
        if (sb != null) Emit(sb);
    }

    private void Emit(StringBuilder sb)
    {
        SummaryLines++;
        _log(sb.ToString());
    }
}
