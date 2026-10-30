// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

internal enum VoidFamily { Corrosion, Corruption, Curse }

/// <summary>One of your Void damage-over-time spells on a target.</summary>
internal sealed class VoidDot
{
    public uint TargetId;
    public string TargetName = "";
    public uint SpellId;
    public string SpellName = "";
    public VoidFamily Family;
    public long StartedMs;
    public long TickedMs;      // 0 until the first "periodic nether damage" line
    /// <summary>Cast while Surge of Destruction was up (shown highlighted).</summary>
    public bool Destruction;

    /// <summary>A server's own rule scales void DoT duration (Conquest's, from RynthConquest's profile; 1.0 elsewhere).</summary>
    public double DurationScale = 1.0;

    /// <summary>Corrosion 15 s, Corruption and Destructive Curse 30 s (upstream's retail values), times the server's scale.</summary>
    public int Duration => (int)((Family == VoidFamily.Corrosion ? 15 : 30) * DurationScale);

    /// <summary>
    /// Seconds left. Before the first tick: from the cast, plus 2 s for the first tick's delay;
    /// after it: from the first tick (upstream's timing).
    /// </summary>
    public int SecondsRemaining(long nowMs) => TickedMs == 0
        ? Duration - (int)((nowMs - StartedMs) / 1000) + 2
        : Duration - (int)((nowMs - TickedMs) / 1000);
}

/// <summary>
/// Tracks your own Corrosion, Corruption and Destructive Curse on each target from the chat
/// lines the server sends you ("You cast X on Y", "You scar Y for N points of periodic nether
/// damage"). Upstream also uses Decal's SpellCast event for the target's id; RynthCore has no
/// spell-cast event for plugins, so the id is the selected target when its name matches, else
/// unknown (the spell then matches targets by name). Only your own spells; Corruption's splash
/// isn't tracked.
/// </summary>
internal sealed class VoidTracker
{
    private static readonly Regex YouCast = new(@"^You cast (.+?) on (.+?)(?:,.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex PeriodicNether = new(@"^You scar (.+?) for (\d+) points of periodic nether damage", RegexOptions.CultureInvariant);

    private readonly List<VoidDot> _dots = new();
    private Dictionary<string, (uint Id, VoidFamily Family)>? _byName;

    public IReadOnlyList<VoidDot> Dots => _dots;
    public int Revision { get; private set; }

    /// <summary>
    /// A chat line. <paramref name="resolveTarget"/> finds a target's id by name;
    /// <paramref name="destructionUp"/> says whether Surge of Destruction is on you now.
    /// </summary>
    public bool OnChatLine(string text, long nowMs, Func<string, uint> resolveTarget, Func<bool> destructionUp, double durationScale = 1.0)
    {
        if (text.StartsWith("You cast ", StringComparison.Ordinal))
        {
            Match m = YouCast.Match(text);
            if (!m.Success) return false;
            if (!SpellsByName().TryGetValue(m.Groups[1].Value.Trim(), out var spell)) return false;
            string target = m.Groups[2].Value.Trim();
            uint targetId = resolveTarget(target);

            VoidDot? existing = Find(targetId, target, spell.Family, nowMs);
            bool destruction = destructionUp();
            bool keepDestruction = existing != null && existing.Destruction;
            if (existing != null) _dots.Remove(existing);
            _dots.Insert(0, new VoidDot
            {
                TargetId = targetId,
                TargetName = target,
                SpellId = spell.Id,
                SpellName = m.Groups[1].Value.Trim(),
                Family = spell.Family,
                StartedMs = nowMs,
                DurationScale = durationScale > 0 ? durationScale : 1.0,
                Destruction = keepDestruction || (destruction && existing == null),
            });
            Revision++;
            return true;
        }

        if (text.StartsWith("You scar ", StringComparison.Ordinal))
        {
            Match m = PeriodicNether.Match(text);
            if (!m.Success) return false;
            string target = m.Groups[1].Value.Trim();
            foreach (VoidDot d in _dots)
                if (d.TickedMs == 0 && d.TargetName == target) d.TickedMs = nowMs;
            Revision++;
            return true;
        }
        return false;
    }

    private VoidDot? Find(uint targetId, string targetName, VoidFamily family, long nowMs)
    {
        foreach (VoidDot d in _dots)
            if (d.Family == family && d.SecondsRemaining(nowMs) > 0
                && (targetId != 0 ? d.TargetId == targetId : d.TargetName == targetName))
                return d;
        return null;
    }

    /// <summary>
    /// Seconds left on a family's spell on a target, or -1; destruction = cast with the surge up.
    /// A spell whose target id wasn't known at the cast matches by name.
    /// </summary>
    public int Remaining(uint targetId, string targetName, VoidFamily family, long nowMs, out bool destruction)
    {
        destruction = false;
        foreach (VoidDot d in _dots)
        {
            if (d.Family != family) continue;
            if (d.TargetId != 0 ? d.TargetId != targetId : d.TargetName != targetName) continue;
            int s = d.SecondsRemaining(nowMs);
            if (s <= 0) continue;
            destruction = d.Destruction;
            return s;
        }
        return -1;
    }

    public void RemoveExpired(long nowMs)
    {
        if (_dots.RemoveAll(d => d.SecondsRemaining(nowMs) <= 0) > 0) Revision++;
    }

    public void Clear()
    {
        _dots.Clear();
        Revision++;
    }

    private Dictionary<string, (uint Id, VoidFamily Family)> SpellsByName()
    {
        if (_byName != null) return _byName;
        var map = new Dictionary<string, (uint, VoidFamily)>(StringComparer.OrdinalIgnoreCase);
        void Add(HashSet<uint> ids, VoidFamily f)
        {
            foreach (uint id in ids)
                if (SpellTable.TryGet(id, out SpellInfo s) && !string.IsNullOrEmpty(s.Name)) map[s.Name] = (id, f);
        }
        Add(SpellIds.Corrosion, VoidFamily.Corrosion);
        Add(SpellIds.Corruption, VoidFamily.Corruption);
        Add(SpellIds.Curse, VoidFamily.Curse);
        return _byName = map;
    }
}
