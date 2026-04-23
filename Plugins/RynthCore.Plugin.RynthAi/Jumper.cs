using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

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

    public bool IsBusy => _isTurning || _needToJump || _charging || _waitingForJump;

    public Jumper(RynthCoreHost host, LegacyUiSettings settings, Action<string> chat)
    {
        _host = host;
        _settings = settings;
        _chat = chat;
    }

    public bool Start(string directionLetters, float? faceHeading, int msToHoldDown)
    {
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
            _targetHeading = _host.TryGetPlayerHeading(out float curr) ? curr : 0f;
        }

        PauseNav();
        _needToJump = true;
        _isTurning = needToTurn;

        if (_isTurning)
        {
            _turningStartedAt = DateTime.UtcNow;
            if (_host.HasTurnToHeading)
                _host.TurnToHeading(_targetHeading);
            _host.Log($"[Jumper] Start turn->{_targetHeading:F0} letters='{directionLetters}' hold={_msToHoldDown}ms");
        }
        else
        {
            _host.Log($"[Jumper] Start jump (no turn) letters='{directionLetters}' hold={_msToHoldDown}ms");
        }
        return true;
    }

    public void Cancel()
    {
        RestoreNav();
        _isTurning = _needToJump = _charging = _waitingForJump = false;
        _addW = _addX = _addZ = _addC = _addShift = false;
    }

    public void Tick()
    {
        if (!IsBusy) return;

        try
        {
            // TurnToHeading is an instant quaternion snap; the heading read
            // doesn't always reflect it immediately, so just wait a short
            // settle window rather than polling for a match.
            if (_isTurning && DateTime.UtcNow - _turningStartedAt >= TimeSpan.FromMilliseconds(150))
            {
                _isTurning = false;
                if (_host.TryGetPlayerHeading(out float current))
                    _host.Log($"[Jumper] Turn done heading={current:F1}");
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
                _host.Log("[Jumper] Settle complete -> idle");
            }
        }
        catch (Exception ex)
        {
            _host.Log($"[Jumper] Tick exception: {ex.GetType().Name}: {ex.Message}");
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
            _host.Log($"[Jumper] CommenceJump ok={started} hold={_msToHoldDown}ms " +
                      $"w={_addW} x={_addX} z={_addZ} c={_addC} shift={_addShift}");
        }
        else
        {
            started = _host.HasTapJump && _host.TapJump();
            _waitingForJump = started;
            _jumpStartedAt = DateTime.UtcNow;
            _host.Log($"[Jumper] TapJump fallback ok={started} " +
                      $"w={_addW} x={_addX} z={_addZ} c={_addC} shift={_addShift}");
        }

        if (!started)
            _chat("[RynthAi] Jump hooks unavailable.");
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
            _host.Log($"[Jumper] LaunchJumpWithMotion ok={released} charged={_msToHoldDown}ms " +
                      $"w={_addW} x={_addX} z={_addZ} c={_addC} shift={_addShift}");
        }
        else
        {
            released = _host.HasDoJump && _host.DoJump(true);
            _host.Log($"[Jumper] DoJump fallback ok={released} charged={_msToHoldDown}ms");
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
