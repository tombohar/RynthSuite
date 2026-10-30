using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// UtilityBelt-compatible jumper. Mirrors UB Jumper.cs flag flow:
/// isTurning → needToJump → waitingForJump. Letters in the command name
/// pick held motions (w=fwd, x=back, z=strafeL, c=strafeR, s=run modifier),
/// optional heading faces first, msToHoldDown is the jump power (0-1000).
/// </summary>
internal sealed class Jumper
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly Action<string> _chat;

    private bool _isTurning;
    private bool _needToJump;
    private bool _charging;
    private bool _waitingForJump;

    private bool _addW, _addX, _addZ, _addC, _addShift;
    private int _msToHoldDown;
    private float _targetHeading;

    private DateTime _turningStartedAt;
    private DateTime _chargeStartedAt;
    private DateTime _jumpStartedAt;
    private TimeSpan _enableNavTimer;

    private bool _pausedNav;
    private bool _prevEnableNavigation;

    // Face only (/mt face, /ub face; ub-IT's DoFace): navigation is held while turning, the turn
    // is sent again every 100 ms until the heading is within 1 degree, and given up after 5 s.
    private bool _faceOnly;
    private DateTime _lastFaceTurnAt;
    private const double FaceToleranceDeg = 1.0;
    private static readonly TimeSpan FaceRetry = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FaceTimeout = TimeSpan.FromSeconds(5);

    public bool IsBusy => _isTurning || _needToJump || _charging || _waitingForJump;

    /// <summary>Turns to <paramref name="heading"/> (any degrees; normalized to 0-360), holding nav meanwhile.</summary>
    public bool Face(double heading)
    {
        if (IsBusy && !_faceOnly)
        {
            _chat("[RynthAi] You are jumping. Try again later.");
            return false;
        }
        if (!_host.HasTurnToHeading)
        {
            _chat("[RynthAi] TurnToHeading not available.");
            return false;
        }
        _targetHeading = (float)(((heading % 360) + 360) % 360);
        if (!_faceOnly) PauseNav();
        _faceOnly = true;
        _isTurning = true;
        _turningStartedAt = _lastFaceTurnAt = DateTime.UtcNow;
        _host.TurnToHeadingBy("Face", _targetHeading);
        RynthLog.Write(LogCat.Jumper, $"[Jumper] Face -> {_targetHeading:F1}");
        return true;
    }

    /// <summary>The face turn: done within <see cref="FaceToleranceDeg"/>, else turn again; nav back when done.</summary>
    private void TickFace()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastFaceTurnAt < FaceRetry) return;
        _lastFaceTurnAt = now;
        bool read = PlayerCompass.TryGet(_host, out float current);
        double off = read ? Math.Abs(((current - _targetHeading) % 360 + 540) % 360 - 180) : 0;
        if (read && off >= FaceToleranceDeg && now - _turningStartedAt < FaceTimeout)
        {
            _host.TurnToHeadingBy("Face", _targetHeading);
            return;
        }
        _isTurning = _faceOnly = false;
        RestoreNav();
        RynthLog.Write(LogCat.Jumper, read && off >= FaceToleranceDeg
            ? $"[Jumper] Face {_targetHeading:F1}: still {off:F1} deg off after {FaceTimeout.TotalSeconds:0} s, gave up"
            : $"[Jumper] Face {_targetHeading:F1} done (heading {(read ? current.ToString("F1") : "?")})");
    }

    public Jumper(RynthCoreHost host, LegacyUiSettings settings, Action<string> chat)
    {
        _host = host;
        _settings = settings;
        _chat = chat;
    }

    public bool Start(string directionLetters, float? faceHeading, int msToHoldDown)
    {
        // A face still turning (a meta's "/mt face 90" then "/ub jumpw 90") gives way to the jump.
        if (_faceOnly)
        {
            _faceOnly = _isTurning = false;
            RestoreNav();
        }
        if (IsBusy)
        {
            _chat("[RynthAi] You are already jumping. Try again later.");
            return false;
        }

        if (msToHoldDown < 0 || msToHoldDown > 1000)
        {
            _chat("[RynthAi] holdtime should be 0-1000 (ms).");
            return false;
        }

        _msToHoldDown = msToHoldDown;
        _addW     = directionLetters.Contains('w');
        _addX     = directionLetters.Contains('x');
        _addZ     = directionLetters.Contains('z');
        _addC     = directionLetters.Contains('c');
        _addShift = directionLetters.Contains('s');

        bool needToTurn = faceHeading.HasValue;
        if (needToTurn)
        {
            float h = faceHeading!.Value;
            if (h < 0 || h >= 360)
            {
                _chat("[RynthAi] heading should be 0-359.");
                return false;
            }
            _targetHeading = h;
        }
        else
        {
            _targetHeading = PlayerCompass.TryGet(_host, out float curr) ? curr : 0f;
        }

        PauseNav();
        _needToJump = true;
        _isTurning = needToTurn;

        if (_isTurning)
        {
            _turningStartedAt = DateTime.UtcNow;
            if (_host.HasTurnToHeading)
                _host.TurnToHeadingBy("Jump", _targetHeading);
            RynthLog.Write(LogCat.Jumper, $"[Jumper] Start turn->{_targetHeading:F0} letters='{directionLetters}' hold={_msToHoldDown}ms");
        }
        else
        {
            RynthLog.Write(LogCat.Jumper, $"[Jumper] Start jump (no turn) letters='{directionLetters}' hold={_msToHoldDown}ms");
        }
        return true;
    }

    public void Cancel()
    {
        RestoreNav();
        _isTurning = _needToJump = _charging = _waitingForJump = _faceOnly = false;
        _addW = _addX = _addZ = _addC = _addShift = false;
    }

    public void Tick()
    {
        if (!IsBusy) return;

        try
        {
            if (_faceOnly)
            {
                TickFace();
                return;
            }
            // TurnToHeading is an instant quaternion snap; the heading read
            // doesn't always reflect it immediately, so just wait a short
            // settle window rather than polling for a match.
            if (_isTurning && DateTime.UtcNow - _turningStartedAt >= TimeSpan.FromMilliseconds(150))
            {
                _isTurning = false;
                if (PlayerCompass.TryGet(_host, out float current))
                    RynthLog.Write(LogCat.Jumper, $"[Jumper] Turn done heading={current:F1}");
            }

            if (_needToJump && !_isTurning)
            {
                _needToJump = false;
                _enableNavTimer = TimeSpan.FromMilliseconds(_msToHoldDown + 2000);
                BeginJump();
            }

            if (_charging && DateTime.UtcNow - _chargeStartedAt >= TimeSpan.FromMilliseconds(_msToHoldDown))
            {
                _charging = false;
                ReleaseJump();
                _waitingForJump = true;
                _jumpStartedAt = DateTime.UtcNow;
            }

            if (_waitingForJump && DateTime.UtcNow - _jumpStartedAt >= _enableNavTimer)
            {
                RestoreNav();
                _waitingForJump = false;
                _addW = _addX = _addZ = _addC = _addShift = false;
                RynthLog.Write(LogCat.Jumper, "[Jumper] Settle complete -> idle");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Write(LogCat.Jumper, $"[Jumper] Tick exception: {ex.GetType().Name}: {ex.Message}");
            Cancel();
        }
    }

    private void BeginJump()
    {
        // Start the spacebar-down phase. The power bar charges while held.
        // Release is handled in Tick() after _msToHoldDown ms via ReleaseJump(),
        // which writes the motion vector directly into CMotionInterp right
        // before DoJump — mirrors UB's UBHelper.Jumper. We deliberately do NOT
        // SetMotion here, because SetMotion would start walking the character
        // *before* the jump, and the velocity wouldn't be baked into the
        // physics sim at DoJump time anyway.
        bool started;
        if (_host.HasCommenceJump)
        {
            started = _host.CommenceJump();
            _charging = started;
            _chargeStartedAt = DateTime.UtcNow;
            RynthLog.Write(LogCat.Jumper, $"[Jumper] CommenceJump ok={started} hold={_msToHoldDown}ms " +
                      $"w={_addW} x={_addX} z={_addZ} c={_addC} shift={_addShift}");
        }
        else
        {
            started = _host.HasTapJump && _host.TapJump();
            _waitingForJump = started;
            _jumpStartedAt = DateTime.UtcNow;
            RynthLog.Write(LogCat.Jumper, $"[Jumper] TapJump fallback ok={started} " +
                      $"w={_addW} x={_addX} z={_addZ} c={_addC} shift={_addShift}");
        }

        if (!started)
        {
            // Deep-audit finding #7 (2026-06-18): a rejected jump (both hook
            // paths unavailable/failed) left every flag false -> IsBusy false
            // on the very next Tick(), but PauseNav() from Start() was never
            // undone. Nav stayed force-disabled for the rest of the session —
            // a real "bot just stands there" cause with no error visible to
            // the user beyond this one chat line. Restore nav on the spot.
            _chat("[RynthAi] Jump hooks unavailable.");
            RestoreNav();
        }
    }

    private void ReleaseJump()
    {
        // Prefer LaunchJumpWithMotion — writes velocity into CMotionInterp,
        // calls DoJump(1), clears motion. Falls back to plain DoJump only if
        // the engine is older than v52.
        bool released;
        if (_host.HasLaunchJumpWithMotion)
        {
            released = _host.LaunchJumpWithMotion(_addShift, _addW, _addX, _addZ, _addC);
            RynthLog.Write(LogCat.Jumper, $"[Jumper] LaunchJumpWithMotion ok={released} charged={_msToHoldDown}ms " +
                      $"w={_addW} x={_addX} z={_addZ} c={_addC} shift={_addShift}");
        }
        else
        {
            released = _host.HasDoJump && _host.DoJump(true);
            RynthLog.Write(LogCat.Jumper, $"[Jumper] DoJump fallback ok={released} charged={_msToHoldDown}ms");
        }
    }

    private void PauseNav()
    {
        _prevEnableNavigation = _settings.EnableNavigation;
        if (_settings.EnableNavigation)
        {
            _settings.EnableNavigation = false;
            _pausedNav = true;
        }
    }

    private void RestoreNav()
    {
        if (_pausedNav)
        {
            _settings.EnableNavigation = _prevEnableNavigation;
            _pausedNav = false;
        }
    }

}
