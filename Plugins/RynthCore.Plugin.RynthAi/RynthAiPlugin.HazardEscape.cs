using System;
using System.Text.RegularExpressions;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Out of the acid at once. A hotspot (an acid or lava pool, ACE's Hotspot) hurts every couple of
/// seconds while you stand in it and says so: "You suffer 32 damage from acid!". On 2026-10-07
/// Drakkon died twice that way in an hour: the patrol (once after a reload, once after a fight)
/// left him in a pool the hazard list didn't know, and topping up with healing kits held him there
/// - every kit stopped him, the acid matched each kit, and the kits ran his stamina out.
///
/// Now the first hotspot hit, while the macro runs, stops everything (combat, buffs, healing,
/// looting, the route, the meta: OnTick returns here, like the death hold) and walks the character
/// back out along its own footsteps (the positions it just came through, so a path it could walk)
/// to where it stood just before the hit. It hands back once it got there and no hit came
/// for HazardQuietMs. The cell it was hurt in is NOT made a patrol hazard: from 2026-10-07 it was
/// (persisted, patrol rerouted around it), and the reroutes sent the patrol through doors into small
/// side rooms (2026-10-10). The patrol is back to how it was before; the escape alone stays.
/// </summary>
public sealed partial class RynthAiPlugin
{
    /// <summary>The escape clock (ms); host tests replace it.</summary>
    internal static Func<long> HazardClock = static () => Environment.TickCount64;

    /// <summary>No hit for this long = out of it (hotspots hit about every 2.5-3.5 s).</summary>
    internal const long HazardQuietMs = 4000;
    /// <summary>An escape that hasn't got out by then gives the tick back (and says so).</summary>
    internal const long HazardEscapeMaxMs = 20000;
    private const long HazardTrailSampleMs = 250;
    private const int HazardTrailSize = 80;          // 20 s of positions
    private const double HazardArriveMeters = 0.6;
    /// <summary>A footstep on the way counts as passed this close (the target needs HazardArriveMeters).</summary>
    private const double HazardStepMeters = 1.0;
    private const double HazardMinBackMeters = 2.5;
    /// <summary>A trail point this long before the first hit is taken as outside the pool.</summary>
    private const long HazardBeforeHitMs = 750;

    private static readonly Regex HotspotDamageRx = new(
        @"^You suffer (\d+) (?:points of )?damage from (?:the )?([A-Za-z][A-Za-z ]*?)[.!]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly (long At, uint Cell, float X, float Y)[] _hazardTrail = new (long, uint, float, float)[HazardTrailSize];
    private int _hazardTrailHead;
    private int _hazardTrailCount;
    private long _hazardTrailLastAt;

    private long _hazardLastHitAt;
    private long _hazardFirstHitAt;
    private long _hazardEscapeSince;      // 0 = not escaping
    private long _hazardArrivedAt;        // 0 = still running to the target
    private bool _hazardHasTarget;
    private uint _hazardTargetCell;
    private float _hazardTargetX, _hazardTargetY;
    private long _hazardTargetAt;         // the trail time of the target (older = further back)
    /// <summary>The footsteps back to the target, newest first; the last one is the target.</summary>
    private readonly System.Collections.Generic.List<(uint Cell, float X, float Y)> _hazardPath = new();
    private int _hazardPathIdx;
    private bool _hazardRunning;
    private long _hazardRunBeatAt;
    private string _hazardSource = "";
    /// <summary>Cells hurt in this session: the escape steers its target out of them. Not patrol hazards.</summary>
    private readonly System.Collections.Generic.HashSet<uint> _hazardHurtCells = new();

    /// <summary>True while the escape owns the character.</summary>
    internal bool HazardEscaping => _hazardEscapeSince != 0;

    /// <summary>"You suffer 32 damage from acid!" -> 32, "acid". Broadcast text only.</summary>
    internal static bool TryParseHotspotDamage(string? text, out int amount, out string source)
    {
        amount = 0;
        source = "";
        if (string.IsNullOrEmpty(text)) return false;
        Match m = HotspotDamageRx.Match(text.Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out amount)) return false;
        source = m.Groups[2].Value.Trim().ToLowerInvariant();
        return true;
    }

    /// <summary>Chat hook: a hotspot hit starts (or pushes on) the escape.</summary>
    internal void NoteHotspotChat(string? text, int chatType)
    {
        if (chatType != 0 || !TryParseHotspotDamage(text, out int amount, out string source)) return;
        // Off (Escape Acid and Lava): no escape.
        if (_dashboard?.Settings is { EscapeHazards: false }) return;
        long now = HazardClock();
        bool fresh = _hazardLastHitAt == 0 || now - _hazardLastHitAt > HazardQuietMs;
        if (fresh) _hazardFirstHitAt = now;
        _hazardLastHitAt = now;
        _hazardSource = source;
        if (Host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _) && cell != 0)
            _hazardHurtCells.Add(cell);

        if (_dashboard?.Settings is not { IsMacroRunning: true }) return;
        if (!HazardEscaping)
        {
            StartHazardEscape(now, amount);
            return;
        }
        // Got to the target and still hurt: the pool reaches further back. Go further.
        if (_hazardArrivedAt != 0 || !_hazardHasTarget)
        {
            _hazardArrivedAt = 0;
            if (!PickHazardTarget(now, olderThan: _hazardHasTarget ? _hazardTargetAt : _hazardFirstHitAt - HazardBeforeHitMs + 1))
                Log("[RynthAi] Hazard: still taking damage and no older spot to run back to; running on.");
        }
    }

    /// <summary>Every tick: where the character stood (not while escaping, nor dead).</summary>
    internal void RecordHazardTrail()
    {
        if (HazardEscaping) return;
        long now = HazardClock();
        if (now - _hazardTrailLastAt < HazardTrailSampleMs) return;
        if (!Host.TryGetPlayerPose(out uint cell, out float x, out float y, out _, out _, out _, out _, out _) || cell == 0)
            return;
        _hazardTrailLastAt = now;
        _hazardTrail[_hazardTrailHead] = (now, cell, x, y);
        _hazardTrailHead = (_hazardTrailHead + 1) % HazardTrailSize;
        if (_hazardTrailCount < HazardTrailSize) _hazardTrailCount++;
    }

    private void StartHazardEscape(long now, int amount)
    {
        _hazardEscapeSince = now;
        _hazardArrivedAt = 0;
        _hazardRunning = false;
        PickHazardTarget(now, olderThan: _hazardFirstHitAt - HazardBeforeHitMs + 1);
        Log($"[RynthAi] Hazard: took {amount} {_hazardSource} damage - stopping everything and moving out"
            + (_hazardHasTarget ? $" (back to ({_hazardTargetX:0.0}, {_hazardTargetY:0.0}))." : " (no earlier spot known: running back the way it faces)."));
        try { Host.WriteToChat($"[RynthAi] Standing in {_hazardSource} - stopping everything to move out.", 1); } catch { }

        try { if (Host.HasCancelAttack) Host.CancelAttack(); } catch { }
        _combatManager?.ReleaseHeldTurn();
        _navigationEngine?.Stop();
        try { StopCorpseMovement(); } catch { }
    }

    /// <summary>
    /// The newest trail point older than <paramref name="olderThan"/>, in this landblock, at least
    /// HazardMinBackMeters away, preferring one outside a known hazard cell. False = none; then
    /// the escape runs straight back from where it faces.
    /// </summary>
    private bool PickHazardTarget(long now, long olderThan)
    {
        _hazardHasTarget = false;
        if (!Host.TryGetPlayerPose(out uint cell, out float px, out float py, out _, out float qw, out _, out _, out float qz))
            return false;

        int fallback = -1;
        for (int k = 1; k <= _hazardTrailCount; k++)
        {
            int i = (_hazardTrailHead - k + HazardTrailSize) % HazardTrailSize;
            var p = _hazardTrail[i];
            if (p.At >= olderThan || (p.Cell >> 16) != (cell >> 16)) continue;
            double d = Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
            if (d < HazardMinBackMeters) continue;
            if (_objectCache?.IsHazardCell(p.Cell) == true || _hazardHurtCells.Contains(p.Cell))
            {
                if (fallback < 0) fallback = i;
                continue;
            }
            SetHazardTarget(p);
            return true;
        }
        if (fallback >= 0)
        {
            SetHazardTarget(_hazardTrail[fallback]);
            return true;
        }

        // No trail (fresh login, or it all lies in the pool): 4 m straight back from the facing.
        double yawDeg = ((-(2.0 * Math.Atan2(qz, qw) * 180.0 / Math.PI)) % 360.0 + 720.0) % 360.0;
        double back = (yawDeg + 180.0) * Math.PI / 180.0;
        SetHazardTarget((olderThan - 1, cell, (float)(px + 4.0 * Math.Sin(back)), (float)(py + 4.0 * Math.Cos(back))));
        return false;
    }

    private void SetHazardTarget((long At, uint Cell, float X, float Y) p)
    {
        _hazardHasTarget = true;
        _hazardTargetAt = p.At;
        _hazardTargetCell = p.Cell;
        _hazardTargetX = p.X;
        _hazardTargetY = p.Y;

        // The footsteps from now back to the target, newest first, thinned to about 1 m apart:
        // the way it came in is a way it can walk out (no straight line through a wall).
        _hazardPath.Clear();
        _hazardPathIdx = 0;
        float lx = float.NaN, ly = float.NaN;
        for (int k = 1; k <= _hazardTrailCount; k++)
        {
            var t = _hazardTrail[(_hazardTrailHead - k + HazardTrailSize) % HazardTrailSize];
            if (t.At <= p.At) break;
            if ((t.Cell >> 16) != (p.Cell >> 16)) continue;
            if (!float.IsNaN(lx) && (t.X - lx) * (t.X - lx) + (t.Y - ly) * (t.Y - ly) < 1.0f) continue;
            _hazardPath.Add((t.Cell, t.X, t.Y));
            lx = t.X; ly = t.Y;
        }
        _hazardPath.Add((p.Cell, p.X, p.Y));
    }

    /// <summary>
    /// Called in OnTick right after the death hold. True = the escape owns this tick: nothing else
    /// runs (no buffs, healing kits, combat, looting, route or meta).
    /// </summary>
    internal bool HoldForHazardEscape()
    {
        if (!HazardEscaping) return false;
        long now = HazardClock();

        if (_dashboard?.Settings is { EscapeHazards: false })
        {
            EndHazardEscape("Escape Acid and Lava was turned off");
            return false;
        }
        if (_dashboard?.Settings is not { IsMacroRunning: true })
        {
            EndHazardEscape("the macro was stopped");
            return false;
        }

        // Died, recalled or portalled meanwhile: the target is somewhere else now.
        if (_hazardHasTarget && Host.TryGetPlayerPose(out uint cellNow, out _, out _, out _, out _, out _, out _, out _)
            && (cellNow >> 16) != (_hazardTargetCell >> 16))
        {
            EndHazardEscape("left the area");
            return false;
        }

        bool quiet = now - _hazardLastHitAt >= HazardQuietMs;
        if (now - _hazardEscapeSince > HazardEscapeMaxMs && quiet)
        {
            EndHazardEscape("no hit for a while");
            return false;
        }
        if (now - _hazardEscapeSince > HazardEscapeMaxMs * 2)
        {
            EndHazardEscape($"still hurt after {HazardEscapeMaxMs * 2 / 1000} s - giving the tick back");
            return false;
        }

        if (_hazardArrivedAt == 0 && _hazardHasTarget)
            RunToHazardTarget(now);
        if (_hazardArrivedAt != 0 && quiet)
        {
            EndHazardEscape("out of it");
            return false;
        }
        return true;
    }

    private void RunToHazardTarget(long now)
    {
        if (!Host.TryGetPlayerPose(out _, out float px, out float py, out _, out _, out _, out _, out _))
            return;
        // Skip to just past the furthest footstep already reached (it may have been carried
        // along, or started near the end); the last one is the target.
        for (int j = _hazardPath.Count - 1; j >= _hazardPathIdx; j--)
        {
            var r = _hazardPath[j];
            double rd = Math.Sqrt((r.X - px) * (r.X - px) + (r.Y - py) * (r.Y - py));
            if (rd <= (j == _hazardPath.Count - 1 ? HazardArriveMeters : HazardStepMeters))
            {
                _hazardPathIdx = j + 1;
                break;
            }
        }
        double dx = 0, dy = 0;
        if (_hazardPathIdx < _hazardPath.Count)
        {
            var w = _hazardPath[_hazardPathIdx];
            dx = w.X - px; dy = w.Y - py;
        }
        if (_hazardPathIdx >= _hazardPath.Count)
        {
            _hazardArrivedAt = now;
            StopHazardRun();
            return;
        }
        double desired = Math.Atan2(dx, dy) * (180.0 / Math.PI);
        if (desired < 0) desired += 360.0;
        bool aligned = _navigationEngine?.ServoToward(desired) ?? TurnDirect(desired);
        if (!aligned)
        {
            if (_hazardRunning) StopHazardRun();
            return;
        }
        if (!_hazardRunning || now - _hazardRunBeatAt >= 500)
        {
            Host.SetAutoRunBy("Hazard", true);
            _hazardRunning = true;
            _hazardRunBeatAt = now;
        }
    }

    private bool TurnDirect(double desired)
    {
        if (Host.HasTurnToHeading) Host.TurnToHeadingBy("Hazard", (float)desired);
        return true;
    }

    private void StopHazardRun()
    {
        if (!_hazardRunning) return;
        _hazardRunning = false;
        Host.SetAutoRunBy("Hazard", false);
        if (Host.HasStopCompletely) Host.StopCompletelyBy("Hazard");
    }

    private void EndHazardEscape(string why)
    {
        StopHazardRun();
        Log($"[RynthAi] Hazard: {why} after {(HazardClock() - _hazardEscapeSince) / 1000.0:0.0} s - resuming.");
        _hazardEscapeSince = 0;
        _hazardArrivedAt = 0;
        _hazardHasTarget = false;
        _hazardPath.Clear();
        _hazardTrailCount = 0;   // the old trail runs through the pool
        _navigationEngine?.ResumeFromNearestWaypoint(always: true);
    }
}
