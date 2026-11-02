using System;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Turning: the heading servo is WalkSteer (host-free, tested in RynthNav.RouteTests). It slews the
// heading toward the target at a capped rate and turns standing (autorun off) for a big turn.
// Two settings: the walk over open ground (the goto's path, the blind push, stepping round
// something) makes corrections up to 50 degrees on the run and sends a turn at most 20 times a
// second; the close work (the Town Network's corridors, aiming into a portal, holding off one)
// keeps RynthAi's tight defaults (stop for anything over 20 degrees).
public sealed partial class RynthNavPlugin
{
    private readonly WalkSteer _steerOpen = new();
    private readonly WalkSteer _steerTight = WalkSteer.Legacy();

    /// <summary>The player's heading (0 = north, clockwise) from the pose quaternion, or NaN.</summary>
    private double PlayerHeadingDeg()
    {
        // The quaternion, not GetPlayerHeading (that one is 0 = east, counter-clockwise).
        if (Host.TryGetPlayerPose(out _, out _, out _, out _, out float qw, out _, out _, out float qz))
            return (-(2.0 * Math.Atan2(qz, qw) * 180.0 / Math.PI) + 720.0) % 360.0;
        return double.NaN;
    }

    /// <summary>Turn toward <paramref name="desiredDeg"/> (0 = north, clockwise) and run when lined up.</summary>
    private void DriveTo(double desiredDeg, WalkSteer? servo = null)
    {
        servo ??= _steerTight;
        desiredDeg = WalkSteer.Norm(desiredDeg);
        double current = PlayerHeadingDeg();
        if (double.IsNaN(current)) current = desiredDeg;
        double turn = servo.Step(desiredDeg, current, NowMs, out bool run);
        if (!double.IsNaN(turn)) Host.TurnToHeading((float)turn);
        AutoRun.Run(run);
    }

    // SetAutoRun/StopCompletely for the goto, sent when they change (AutoRunGate.cs).
    private AutoRunGate? _autoRun;
    private AutoRunGate AutoRun => _autoRun ??= new AutoRunGate(on => Host.SetAutoRun(on), () => Host.StopCompletely(), () => NowMs);

    /// <summary>A fresh start for the turn servo (a new goto, a new leg).</summary>
    private void ResetTurn() { _steerOpen.Reset(); _steerTight.Reset(); }
}
