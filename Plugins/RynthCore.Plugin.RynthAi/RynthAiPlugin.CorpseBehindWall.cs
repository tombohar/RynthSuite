// ============================================================================
//  RynthSuite - RynthAiPlugin.CorpseBehindWall.cs
//  A corpse behind a wall (2026-11-21, R-20261121-190950-ee4e, 2026.11.19.1): "I keep trying to loot
//  through walls" in a dungeon (Shadowfang Keep, 0x005A). Corpses 3.4-3.7 m away on the far side of a
//  wall: every open came back UseDone 0 and nothing opened (the server walks the character straight
//  at the corpse, and the wall stops it). Each one cost about 11 s (four opens, the use check's clean
//  stop, a jump and its pause), then came back 30 s later for two more rounds, while other corpses
//  waited, and the stops and jumps read as the character fighting the wall.
//
//  Now an open that is about to be re-sent for a corpse that didn't open checks the straight line to
//  it (the raycast's single chest-height ray, as melee uses): a wall in the way sets the corpse aside
//  instead. Set aside is not an abandon (it never counts toward the three), its failed open is not a
//  use refusal, and it gets another look every CorpseWallRecheckMs: once the line is clear (the
//  character moved, the hunt walked round) it is a corpse like any other. Only a failed open leads
//  here, so a corpse the ray reads behind a wall that still opens is looted as before.
// ============================================================================

using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>A corpse set aside behind a wall is looked at again this often.</summary>
    internal const long CorpseWallRecheckMs = 3000;

    private readonly HashSet<int> _corpsesBehindWall = new();

    /// <summary>Tests: whether a wall stands between the character and the corpse (else the raycast decides).</summary>
    internal System.Func<int, bool>? CorpseWallOverride { get; set; }

    /// <summary>The straight line to <paramref name="corpseId"/> crosses a wall. False when the raycast can't say.</summary>
    private bool CorpseBehindWall(int corpseId)
    {
        if (CorpseWallOverride != null) return CorpseWallOverride(corpseId);
        var raycast = _raycastReady;
        if (raycast == null || !raycast.IsInitialized) return false;
        try { return raycast.IsTargetBlocked(Host, unchecked((uint)corpseId), TargetingFSM.AttackType.Melee); }
        catch { return false; }
    }

    /// <summary>
    /// Before an open is re-sent for <paramref name="corpseId"/> (one already went out and nothing
    /// opened): true when it was set aside behind a wall instead, and nothing is sent.
    /// </summary>
    private bool SetAsideBehindWall(int corpseId, long now)
    {
        if (corpseId == 0 || _lastOpenAttemptCorpseId != corpseId || _openedContainerId == corpseId) return false;
        if (!CorpseBehindWall(corpseId)) return false;
        _useRefusals.Forget(corpseId);
        if (_corpsesBehindWall.Count >= 256) _corpsesBehindWall.Clear();   // a bound: corpses go in minutes
        _corpsesBehindWall.Add(corpseId);
        _corpseCooldownUntil[corpseId] = now + CorpseWallRecheckMs;
        string name = _objectCache?[corpseId]?.Name ?? $"0x{(uint)corpseId:X8}";
        double d = _objectCache?.Distance(unchecked((int)_playerId), corpseId) ?? double.MaxValue;
        LootDiag($"[RynthAi] Corpse loot: 0x{(uint)corpseId:X8} '{name}'{(d < 1000 ? $" ({d:0.0} m)" : "")} didn't open and a wall " +
                 "stands between: set aside (not a refusal or an abandon) until the line to it is clear.");
        StopCorpseMovement();
        ResetCorpseTarget();
        _lastOpenAttemptCorpseId = 0;
        _lastCorpseOpenAttemptAt = 0;
        return true;
    }

    /// <summary>
    /// HasUnlootedCorpsesInRange, for a corpse set aside behind a wall whose look-again time came:
    /// true while the wall is still there (it waits another CorpseWallRecheckMs).
    /// </summary>
    private bool StillBehindWall(int corpseId, long now)
    {
        if (!_corpsesBehindWall.Contains(corpseId)) return false;
        if (CorpseBehindWall(corpseId))
        {
            _corpseCooldownUntil[corpseId] = now + CorpseWallRecheckMs;
            return true;
        }
        _corpsesBehindWall.Remove(corpseId);
        LootDiag($"[RynthAi] Corpse loot: the line to 0x{(uint)corpseId:X8} is clear now: it can be looted.");
        return false;
    }
}
