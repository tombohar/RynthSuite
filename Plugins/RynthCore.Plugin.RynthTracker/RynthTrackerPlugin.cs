using System;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthTracker;

// The session's numbers come from SessionStats (Plugins\Shared), the same code RynthAi's Mini
// Remote uses, so the Tracker panel and the Mini Remote agree (0.4.0, 2026-10-11): experience,
// luminance (with ACECustom's bank and allegiance pass-up), luminance of the last five minutes,
// kills (including a multi-target spell's extra kills found by their corpses), XP per kill,
// deaths and Radiance. SessionStatsTracker reads the game and keeps the session across reloads.
//
// One session (0.4.1, 2026-10-13): with RynthAi loaded the panel shows RynthAi's own session
// (the Mini Remote's Session Stats, read through "RynthAi.Hub"), so the Tracker, the Mini Remote,
// RynthOracle and RynthILT count the same session from the same start, and Reset starts that one
// over. Without RynthAi (or before its login) the Tracker's own session, kept running all along,
// is shown instead.
public sealed class RynthTrackerPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthTracker");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.4.1");   // previous release: 0.4.0

    private SessionStatsTracker? _session;
    private readonly RynthAiSessionClient _ai = new();
    private int _tickCounter;
    // Set by the engine's UI thread (Reset export), acted on by the next OnTick on the pump.
    private volatile bool _resetWanted;

    // Written on the game thread every ~0.5s, read on the engine's UI thread.
    // Reference reads are atomic on x86 so volatile is sufficient; no lock needed.
    private volatile string _snapshot = EmptySnapshot;

    private const string EmptySnapshot = TrackerSnapshot.Empty;

    public override int Initialize()
    {
        _session = new SessionStatsTracker(Host, "[RynthTracker]", TrackerSessionStore.DefaultDirectory());
        Host.Log("[RynthTracker] Plugin initialized.");
        return 0;
    }

    public override void OnLoginComplete()
    {
        _ai.Reset();
        _session?.OnLoginComplete();
    }

    public override void OnLogout()
    {
        _session?.OnLogout();
        _ai.Reset();
        _snapshot = EmptySnapshot;
    }

    public override void Shutdown() => _session?.Shutdown();

    public override void OnKillNotification(string? deathMessage) => _session?.OnKillNotification(deathMessage);

    public override void OnChatWindowText(string? text, int chatType, ref int eat) => _session?.OnChatWindowText(text);

    public override void OnCreateObject(uint objectId) => _session?.OnCreateObject(objectId);

    public override void OnDeleteObject(uint objectId) => _session?.OnDeleteObject(objectId);

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
        => _session?.OnUpdateHealth(targetId, currentHealth, maxHealth);

    public override void OnTick()
    {
        var session = _session;
        if (session == null) return;
        long nowMs = Environment.TickCount64;
        if (_resetWanted)
        {
            _resetWanted = false;
            // RynthAi's session when it is the one shown; the Tracker's own either way, so a later
            // fallback doesn't jump back to an old start.
            _ai.ResetSession(Host, nowMs);
            session.RequestReset();
        }
        session.Tick();
        if (++_tickCounter < 30) return; // rebuild every ~0.5s at 60Hz
        _tickCounter = 0;
        DateTime now = DateTime.UtcNow;
        if (_ai.TryGet(Host, nowMs, out SessionStatsV1 s))
        {
            SessionStatsMoreV1? more = _ai.TryGetMore(Host, nowMs, out SessionStatsMoreV1 m) ? m : null;
            _snapshot = TrackerSnapshot.Build(TrackerSnapshot.FromRynthAi(s, more, session.Active ? session.View : null), now);
        }
        else
            _snapshot = session.Active ? TrackerSnapshot.Build(session.View, now) : EmptySnapshot;
    }

    internal string GetSnapshot() => _snapshot;

    /// <summary>Called from the RynthTrackerReset export (engine UI thread): just flags a reset;
    /// the next OnTick performs it on the pump thread.</summary>
    internal void Reset() => _resetWanted = true;
}
