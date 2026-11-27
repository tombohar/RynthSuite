// ============================================================================
//  RynthCore.Plugin.RynthAi - Combat/CombatManager.LosVerdicts.cs
//  Show Line of Sight (2026-11-26, a testing aid): the scan's own line-of-sight
//  verdict for each monster it tested, kept for the overlay (LosLineOverlay.cs)
//  so the line is drawn in the colour the targeting raycast gave, with no second
//  raycast per frame. Filled only while ShowLosLine is on; cleared each scan.
//
//  Why: 2026-11-26, a melee character outdoors in 0xB470, locked monsters
//  4.3-4.7 m off in the same outdoor cell behind a wall; the scan's raycast said
//  clear, nothing landed, and the target was blacklisted after three no-damage
//  attacks. The line shows in game what the raycast thinks.
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager
{
    /// <summary>One monster's line-of-sight verdict from the last scan.</summary>
    internal struct LosVerdict
    {
        /// <summary>What the targeting raycast said: true = blocked.</summary>
        public bool Blocked;
        /// <summary>The raycast tested geometry (false: clear without a test: raycasting off or warming up,
        /// no geometry near the path, too far, no position).</summary>
        public bool Tested;
        /// <summary>Where it is blocked, global metres (landblock * 192 + local; Z the height).</summary>
        public bool HasHit;
        public float HitX, HitY, HitZ;
        /// <summary>Blocked, but melee within MeleeIgnoresLosWithin fights it anyway (the scan kept it).</summary>
        public bool MeleeThrough;
        /// <summary>3D distance at the scan, metres.</summary>
        public double Distance;
        public TargetingFSM.AttackType Type;
        public DateTime At;
    }

    private readonly Dictionary<int, LosVerdict> _losVerdicts = new();

    /// <summary>The last scan's verdicts by monster id (empty while Show Line of Sight is off).</summary>
    internal IReadOnlyDictionary<int, LosVerdict> LosVerdicts => _losVerdicts;

    internal bool TryGetLosVerdict(int id, out LosVerdict verdict) => _losVerdicts.TryGetValue(id, out verdict);

    /// <summary>The monster the line goes to: the combat target, else the locked one.</summary>
    internal int LosLineTargetId => _activeTargetId != 0 ? _activeTargetId : _lockedTargetId;

    /// <summary>Start of a scan: the cache holds this scan's verdicts only; on, the raycast reports hit points.</summary>
    private bool BeginLosVerdicts()
    {
        bool on = _settings.ShowLosLine;
        if (_losVerdicts.Count > 0) _losVerdicts.Clear();
        var fsm = _raycastSystem?.TargetingFSM;
        if (fsm != null && fsm.ReportHitPoints != on) fsm.ReportHitPoints = on;
        return on;
    }

    private void NoteLosVerdict(int id, bool blocked, bool tested, in TargetingFSM.LosDetail detail, double dist, TargetingFSM.AttackType type)
    {
        var v = new LosVerdict
        {
            Blocked = blocked,
            Tested = tested,
            HasHit = blocked && detail.HasHit,
            HitX = detail.Hit.X, HitY = detail.Hit.Y, HitZ = detail.Hit.Z,
            Distance = dist,
            Type = type,
            At = RynthClock.Now,
        };
        _losVerdicts[id] = v;
    }

    private void NoteLosMeleeThrough(int id)
    {
        if (_losVerdicts.TryGetValue(id, out var v)) { v.MeleeThrough = true; _losVerdicts[id] = v; }
    }

    /// <summary>Tests: a verdict as a scan would leave it.</summary>
    internal void SetLosVerdictForTests(int id, LosVerdict verdict) => _losVerdicts[id] = verdict;
}
