using System;

namespace RynthCore.Plugin.RynthNav;

// Autorun and stops sent when they change, not every tick (2026-10-19). The goto sent
// SetAutoRun(False) every tick while it settled after a teleport, turned in place or stood at a
// portal, SetAutoRun(True) every tick while it ran, and SetAutoRun(False) + StopCompletely every
// tick while it held in the Town Network: about 55 calls a second. The plugin tick isn't AC's main
// thread, so every call takes a slot in the engine's action ring (256), and the ring is held
// through portal space. On a recall + Town Network trip (Matron Hive East, 10-19 16:38-16:40) it
// filled: 1,204 queued actions refused (anyone's, not only ours), 256 stale stops run 12 s late.
// Now a change goes out at once, "on" is sent again every ReassertMs (a cast, a stance change or a
// server stop turns autorun off without telling us; RynthAi's nav re-asserts the same way), and
// "off" and a halt go out once. A call the engine refuses isn't taken as sent: it is tried again
// after RetryMs, not every tick. Stop() is for a goto's start and end and a teleport: it always sends.
internal sealed class AutoRunGate
{
    public const long ReassertMs = 500;
    public const long RetryMs = 250;

    private readonly Func<bool, bool> _setAutoRun;
    private readonly Func<bool> _stopCompletely;
    private readonly Func<long> _now;

    private int _run = -1;       // the last SetAutoRun that went through: 1 on, 0 off, -1 not known
    private long _runMs;         // when it went through
    private bool _halted;        // a StopCompletely went through and nothing has started since
    private long _retryAtMs;     // a call was refused: nothing but Stop() before this

    public AutoRunGate(Func<bool, bool> setAutoRun, Func<bool> stopCompletely, Func<long> now)
    {
        _setAutoRun = setAutoRun;
        _stopCompletely = stopCompletely;
        _now = now;
    }

    /// <summary>Autorun on or off: sent when it's a change, and "on" again every ReassertMs.</summary>
    public void Run(bool on)
    {
        long now = _now();
        int want = on ? 1 : 0;
        if (want == _run && !(on && now - _runMs >= ReassertMs)) return;
        if (now < _retryAtMs) return;
        bool ok = _setAutoRun(on);
        _run = ok ? want : -1;
        _runMs = now;
        if (ok && on) _halted = false;
        _retryAtMs = ok ? 0 : now + RetryMs;
    }

    /// <summary>Autorun off and StopCompletely, unless that's what was sent last.</summary>
    public void Halt()
    {
        if (_halted) return;
        if (_now() < _retryAtMs) return;
        Stop();
    }

    /// <summary>Autorun off and StopCompletely, always (a goto's start and end, a teleport).</summary>
    public void Stop()
    {
        long now = _now();
        bool off = _setAutoRun(false);
        bool stopped = _stopCompletely();
        _run = off ? 0 : -1;
        _runMs = now;
        _halted = off && stopped;
        _retryAtMs = _halted ? 0 : now + RetryMs;
    }

    /// <summary>What the client has on isn't known any more (a new goto: the d-pad or another plugin may have moved it).</summary>
    public void Forget()
    {
        _run = -1; _runMs = 0; _halted = false; _retryAtMs = 0;
    }
}
