namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Tells combat when the plugin's busy count is a leftover rather than a real busy client.
///
/// The plugin counts AC's busy count from the engine's increment/decrement events. A cast or
/// an item use (a corpse open) raises it through the hooked increment, but AC lowers it inline
/// when the server's UseDone (0x01C7) arrives, which the hook never sees (see the engine's
/// BusyCountHooks: "the shadow counter never sees AC's inline UseDone decrement"). The engine's
/// reconciler only decrements what is still in the real field, so after a UseDone there is
/// nothing left for it and no decrement event follows. The plugin's count then sits at 1 until
/// its own watchdog force-clears it 5 s later (CorpseOpenController.CheckBusyTimeout), and
/// combat's "busy, don't attack" gate waits all of that.
///
/// Measured (2026-09-29 caster logs, 157 kills): kill to the next cast median 2.5 s, p90 2.7 s,
/// nearly all of it waiting for busy=0, and the steady cast cadence pinned near 5 s by the
/// force-clear. Melee/missile after a corpse open waited up to 4 s the same way (10-01 log).
///
/// The rule: the count is stale when the server has finished an action (the UseDone count moved)
/// SINCE the count last rose, and no cast gesture is animating. Melee and missile swings raise
/// and lower the count through the hooked calls (CommenceAttack / AttackDone), so a swing in
/// progress is never mistaken for stale: no UseDone arrives for it. Inert without the engine's
/// UseDone counter.
/// </summary>
internal sealed class CombatBusyGate
{
    private int _lastCount;
    private int _seqAtRise;
    private bool _haveRise;

    /// <summary>Record a new busy count. A rise remembers the UseDone count at that moment.</summary>
    public void NoteCount(int count, int useDoneSeqNow)
    {
        if (count > _lastCount)
        {
            _seqAtRise = useDoneSeqNow;
            _haveRise = true;
        }
        if (count <= 0) _haveRise = false;
        _lastCount = count;
    }

    /// <summary>True when the busy count is positive but every action behind it has been finished
    /// by the server: a UseDone arrived after the last rise and no cast gesture is animating.</summary>
    public bool IsStale(int busyCount, bool hasUseDoneSeq, int useDoneSeqNow, bool gestureInProgress)
    {
        if (busyCount <= 0 || !hasUseDoneSeq || !_haveRise) return false;
        if (gestureInProgress) return false;
        return useDoneSeqNow != _seqAtRise;
    }

    /// <summary>Forget the remembered rise (the count was reset to 0 elsewhere).</summary>
    public void Reset()
    {
        _lastCount = 0;
        _haveRise = false;
    }
}
