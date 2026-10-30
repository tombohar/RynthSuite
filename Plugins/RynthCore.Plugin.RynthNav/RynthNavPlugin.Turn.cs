using System;

namespace RynthCore.Plugin.RynthNav;

// Turning the way RynthAi's nav does it (NavigationEngine.DriveHeading, mode 0 heading servo):
// the heading is slewed toward the target at a capped rate rather than snapped onto it, and a big
// turn is made standing (autorun off) and the run resumes once lined up. The numbers are RynthAi's
// defaults (Nav Turn Rate 270 deg/s, Stop Turn Angle 20, Resume Turn Angle 10, Dead Zone 4).
public sealed partial class RynthNavPlugin
{
    private const double TurnRateDegPerSec = 270.0;
    private const double TurnDeadZoneDeg = 4.0;
    private const double StopTurnAngleDeg = 20.0;
    private const double ResumeTurnAngleDeg = 10.0;

    private bool _turnInPlace;
    private long _lastTurnMs;

    /// <summary>Turn toward <paramref name="desiredDeg"/> (0 = north, clockwise) and run when lined up.</summary>
    private void DriveTo(double desiredDeg)
    {
        desiredDeg = ((desiredDeg % 360.0) + 360.0) % 360.0;
        double current = desiredDeg;
        if (Host.TryGetPlayerPose(out _, out _, out _, out _, out float qw, out _, out _, out float qz))
            current = (-(2.0 * Math.Atan2(qz, qw) * 180.0 / Math.PI) + 720.0) % 360.0;

        double error = desiredDeg - current;
        if (error > 180.0) error -= 360.0; else if (error < -180.0) error += 360.0;
        double abs = Math.Abs(error);

        long now = NowMs;
        double dt = _lastTurnMs == 0 ? 33.0 : Math.Clamp(now - _lastTurnMs, 10, 200);
        _lastTurnMs = now;
        double maxStep = TurnRateDegPerSec * dt / 1000.0;

        // Run gate with hysteresis: stop to turn when far off, run again once close.
        if (_turnInPlace) { if (abs <= ResumeTurnAngleDeg) _turnInPlace = false; }
        else if (abs > StopTurnAngleDeg) _turnInPlace = true;

        if (abs > TurnDeadZoneDeg)
        {
            double next = current + Math.Clamp(error, -maxStep, maxStep);
            Host.TurnToHeading((float)(((next % 360.0) + 360.0) % 360.0));
        }
        AutoRun.Run(!_turnInPlace);
    }

    // SetAutoRun/StopCompletely for the goto, sent when they change (AutoRunGate.cs).
    private AutoRunGate? _autoRun;
    private AutoRunGate AutoRun => _autoRun ??= new AutoRunGate(on => Host.SetAutoRun(on), () => Host.StopCompletely(), () => NowMs);

    /// <summary>A fresh start for the turn servo (a new goto, a teleport).</summary>
    private void ResetTurn() { _turnInPlace = false; _lastTurnMs = 0; }
}
