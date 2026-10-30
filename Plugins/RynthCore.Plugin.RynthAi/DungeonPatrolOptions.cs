// DungeonPatrolOptions: the player-tunable side of the dungeon patrol (Settings > Navigation >
// Dungeon Patrol). Passed into DungeonPathfinder.BuildPatrolRoute as a value, never kept in a
// static: every build says what it was built with, and a reload starts from the settings again.
// The defaults are the patrol as it was before the settings existed (2026-10-12, f15c153), but
// for the dead-end limit: since 2026-10-13 it counts whole dead-end areas, default 10 (was 3).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthAi;

/// <summary>Which cells the patrol walks (Settings > Navigation > Dungeon Patrol > Coverage).</summary>
internal enum PatrolCoverage
{
    /// <summary>Every reachable cell but dead-end areas of up to MaxDeadEndCells cells (2026-10-12 on).</summary>
    WholeDungeon = 0,
    /// <summary>Only the loops and the corridors between junctions (the 2-core; before 2026-10-12).</summary>
    LoopsOnly = 1,
    /// <summary>Every reachable cell, every dead end included.</summary>
    Everything = 2,
    /// <summary>The Whole dungeon cells, but walked only close enough that every one is seen: a
    /// stop within SightMeters, on about the same level, with no wall between (2026-10-14: a pillar
    /// hall, 0x00AE, is 1,700 cells of corridor pieces, and Whole dungeon walked every lane).
    /// DungeonPathfinder.Sweep.cs.</summary>
    Sweep = 3,
}

internal sealed record DungeonPatrolOptions
{
    public const int    MaxDeadEndCellsMin = 0,     MaxDeadEndCellsMax = 30,     MaxDeadEndCellsDefault = 10;
    public const double WallClearanceMin = 0.5,  WallClearanceMax = 2.0,   WallClearanceDefault = 1.0;
    public const double LookInRoomMin = 3.0,     LookInRoomMax = 40.0,     LookInRoomDefault = 10.0;
    public const double SightMin = 5.0,          SightMax = 60.0,          SightDefault = 20.0;

    public static readonly DungeonPatrolOptions Default = new();

    public PatrolCoverage Coverage { get; init; } = PatrolCoverage.WholeDungeon;

    /// <summary>Whole dungeon (and Loops only when hazards cut the loops): dead-end areas this
    /// small (all their cells: a closet, a short corridor with a small room at its end) are left
    /// out, and the patrol turns round where a longer dead end has only this much left ahead.
    /// A big room (LookInRoomMeters) is never left out. 0 = none left out.
    /// <see cref="DungeonPathfinder.TrimDeadEndPockets"/>. Until 2026-10-13 a spur limit (cells from
    /// a tip to the first junction, default 3), which kept every corridor that ended in a room.</summary>
    public int MaxDeadEndCells { get; init; } = MaxDeadEndCellsDefault;

    /// <summary>Lava/acid floor cells (Detector C, HazardTextures.txt) are kept out of the patrol.
    /// Cells marked by hand or sighted as pools are avoided either way. Applied by the caller,
    /// which knows which cells are which (<see cref="DungeonPathfinder.PatrolHazards"/>).</summary>
    public bool AvoidHazardFloors { get; init; } = true;

    /// <summary>Every point laid keeps at least this far (metres) from walls where there is room.</summary>
    public double WallClearance { get; init; } = WallClearanceDefault;

    /// <summary>A room at least this big (metres across its cell centres) that the patrol only
    /// skirts or leaves by the doorway it came in by gets a walk to its middle and back.</summary>
    public double LookInRoomMeters { get; init; } = LookInRoomDefault;

    /// <summary>Sweep: how far (metres) a monster notices the character, so how far apart the
    /// patrol's lanes may be. Settings "Sight Distance (yd)". Unused by the other coverages.</summary>
    public double SightMeters { get; init; } = SightDefault;

    /// <summary>The options with every value in range (a hand-edited profile or a remote setting).</summary>
    public DungeonPatrolOptions Clamped() => this with
    {
        Coverage         = Enum.IsDefined(Coverage) ? Coverage : PatrolCoverage.WholeDungeon,
        MaxDeadEndCells     = Math.Clamp(MaxDeadEndCells, MaxDeadEndCellsMin, MaxDeadEndCellsMax),
        WallClearance    = double.IsFinite(WallClearance) ? Math.Clamp(WallClearance, WallClearanceMin, WallClearanceMax) : WallClearanceDefault,
        LookInRoomMeters = double.IsFinite(LookInRoomMeters) ? Math.Clamp(LookInRoomMeters, LookInRoomMin, LookInRoomMax) : LookInRoomDefault,
        SightMeters      = double.IsFinite(SightMeters) ? Math.Clamp(SightMeters, SightMin, SightMax) : SightDefault,
    };

    public static string CoverageName(PatrolCoverage c) => c switch
    {
        PatrolCoverage.LoopsOnly  => "loops only",
        PatrolCoverage.Everything => "everything",
        PatrolCoverage.Sweep      => "sweep",
        _                         => "whole dungeon",
    };

    /// <summary>For the "main route from ..." diagnostic line and PatrolProof.</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        string spurs = Coverage == PatrolCoverage.Everything ? "" : $", skip dead ends <= {MaxDeadEndCells}";
        if (Coverage == PatrolCoverage.Sweep) spurs += $", sight {SightMeters.ToString("0.#", inv)} m";
        return $"coverage {CoverageName(Coverage)}{spurs}, lava/acid floors {(AvoidHazardFloors ? "avoided" : "walked")}, "
             + $"walls {WallClearance.ToString("0.0#", inv)} m, look into rooms >= {LookInRoomMeters.ToString("0.#", inv)} m";
    }
}

/// <summary>
/// Notices a patrol setting changed while a patrol runs and says when to rebuild it: once the
/// new settings have stayed put for <see cref="SettleMs"/> (the Settings panel saves on every
/// click of a +/- step, and a rebuild per click would restart the lap each time). One per
/// plugin instance, so nothing outlives a reload.
/// </summary>
internal sealed class PatrolSettingsWatch
{
    public const long SettleMs = 1000;

    private DungeonPatrolOptions? _built, _pending;
    private long _pendingSince;

    /// <summary>The settings the running patrol was built with (null = none built yet).</summary>
    public DungeonPatrolOptions? Built => _built;

    public void MarkBuilt(DungeonPatrolOptions options)
    {
        _built = options;
        _pending = null;
    }

    /// <summary>True when <paramref name="current"/> differs from what the patrol was built with
    /// and has not changed for SettleMs.</summary>
    public bool ShouldRebuild(DungeonPatrolOptions current, long nowMs)
    {
        if (_built == null || current.Equals(_built)) { _pending = null; return false; }
        if (!current.Equals(_pending)) { _pending = current; _pendingSince = nowMs; return false; }
        return nowMs - _pendingSince >= SettleMs;
    }
}
