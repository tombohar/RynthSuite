namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// One Recall On Death attempt (RynthAiPlugin.DeathRecall.cs), as a pure clock-driven state machine
/// so tests can run it. The plugin calls <see cref="Tick"/> every frame and does what it answers:
/// <see cref="Step.Fire"/> = cast the spell / use the item now. A teleport is portal space seen, or
/// the landblock changing; once one is seen nothing more is fired, and after
/// <see cref="SettleMs"/> out of portal space the run is <see cref="Step.Done"/>.
/// </summary>
internal sealed class DeathRecallRun
{
    public enum Step { Wait, Fire, Done, Failed }

    public const long SpellRetryMs = 4_000;    // a fizzle or interrupted cast: try again
    public const long ItemRetryMs  = 6_000;    // a gem's use animation takes ~3 s before the portal
    public const int  MaxFires     = 5;
    public const long TimeoutMs    = 60_000;
    public const long SettleMs     = 3_000;    // out of portal space this long before nav/corpse travel

    private readonly long _retryMs;
    private readonly long _startedAt;
    private readonly uint _startLandblock;
    private long _lastFireAt;
    private long _teleportedAt;
    private bool _sawPortal;

    public int Fires { get; private set; }
    public string FailReason { get; private set; } = string.Empty;
    public bool Teleported => _teleportedAt != 0 || _sawPortal;

    /// <param name="useItem">True for a gem or other item (slower retry), false for a spell.</param>
    /// <param name="now">Clock in ms.</param>
    /// <param name="startLandblock">Landblock (cell &gt;&gt; 16) at the start; 0 when unknown.</param>
    public DeathRecallRun(bool useItem, long now, uint startLandblock)
    {
        _retryMs = useItem ? ItemRetryMs : SpellRetryMs;
        _startedAt = now;
        _startLandblock = startLandblock;
    }

    /// <param name="now">Clock in ms.</param>
    /// <param name="inPortalSpace">The client is in portal space.</param>
    /// <param name="landblock">Current landblock; 0 when unknown.</param>
    /// <param name="ready">Ready to fire (a spell: wand wielded and in magic mode).</param>
    public Step Tick(long now, bool inPortalSpace, uint landblock, bool ready)
    {
        if (inPortalSpace) { _sawPortal = true; return Step.Wait; }

        bool moved = landblock != 0 && _startLandblock != 0 && landblock != _startLandblock;
        if (_sawPortal || moved)
        {
            if (_teleportedAt == 0) _teleportedAt = now;
            return now - _teleportedAt >= SettleMs ? Step.Done : Step.Wait;
        }

        if (now - _startedAt > TimeoutMs)
            return Fail($"no teleport within {TimeoutMs / 1000} s");
        if (Fires >= MaxFires && now - _lastFireAt >= _retryMs)
            return Fail($"no teleport after {MaxFires} tries");
        if (!ready || Fires >= MaxFires)
            return Step.Wait;
        if (Fires > 0 && now - _lastFireAt < _retryMs)
            return Step.Wait;

        Fires++;
        _lastFireAt = now;
        return Step.Fire;
    }

    private Step Fail(string why)
    {
        FailReason = why;
        return Step.Failed;
    }
}
