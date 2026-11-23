namespace RynthCore.Plugin.RynthNav;

// After a recall or a portal: wait for the arrival to finish before the next step, the way
// RynthAi's nav does. The position jump comes first and the client's portal space after it
// (10-01 20:55: Recall Aphus Lassel jumped at 12.5 s, portal space 12.9-19.4 s). Moving on at the
// jump started the Town Network portal step while the recall's portal space was still to come, and
// that portal space's end was then taken for the Town Network portal firing.
// Settled = out of portal space for SettleQuietMs, after portal space was seen (or SettleNoPortalMs
// passed without any).
public sealed partial class RynthNavPlugin
{
    private const long SettleQuietMs = 1000;
    private const long SettleNoPortalMs = 3000;
    private const long SettleGiveUpMs = 60000;

    private bool _settling, _settleSawPortal;
    private long _settleJumpMs, _settleQuietSince;

    private void BeginSettle()
    {
        _settling = true;
        _settleJumpMs = NowMs;
        _settleQuietSince = 0;
        _settleSawPortal = Host.HasIsPortaling && Host.IsPortaling();
        AutoRun.Run(false);
        lock (_gate) _status = "arriving…";
    }

    private void StepSettle()
    {
        long now = NowMs;
        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        AutoRun.Run(false);
        if (portaling)
        {
            _settleSawPortal = true;
            _settleQuietSince = 0;
            if (now - _settleJumpMs > SettleGiveUpMs) { _settling = false; FinishGoto("stopped: still in portal space a minute after the teleport"); }
            return;
        }
        if (_settleQuietSince == 0) _settleQuietSince = now;
        if (now - _settleQuietSince < SettleQuietMs) return;
        if (!_settleSawPortal && now - _settleJumpMs < SettleNoPortalMs) return;

        _settling = false;
        if (_settleSawPortal) ClockSpace(_settleJumpMs);   // portal space, for the trip clock (RynthNavPlugin.TripTime.cs)
        _lastLandMs = now;                    // the portal cooldown counts from the real landing
        RefreshTiles(CurrentLandblock);
        Host.Log($"[RynthNav] arrived after the teleport ({(now - _settleJumpMs) / 1000.0:F1} s{(_settleSawPortal ? ", portal space over" : "")}) — next step");
        // Landed somewhere else (another portal's destination, a recall tied elsewhere, a dungeon): the
        // next legs start from the wrong place, so plan the rest from here (RynthNavPlugin.Ways.cs).
        if (!LandedWherePlanned(out string where))
        {
            string what = _route != null && _routeIdx < _route.Count && _route[_routeIdx].UseRecall ? "recall" : "portal";
            ReplanTrip($"the {what} '{(_route != null && _routeIdx < _route.Count ? Trunc(_route[_routeIdx].Label) : "")}' took you to {where}");
            return;
        }
        AdvanceRoute();
    }
}
