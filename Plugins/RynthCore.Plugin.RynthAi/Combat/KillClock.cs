using System;
using System.Threading;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The one "time since our last kill" clock (2026-10-06). Read by the meta expression
/// secondssincelastkill[], the "No Kills Within Minutes" meta condition and the status feed's
/// secsSinceLastKill.
///
/// A kill is ACE's KillerNotification (GameEvent 0x01AD), which the server sends only to the
/// killer: RynthAiPlugin.OnKillNotification is the only caller of <see cref="RecordKill"/>.
/// Death messages in chat ("X died", a fellow's kill) never count.
///
/// Before the first kill this session the clock counts from the last macro start, or, if the
/// macro hasn't been started since login, from login (a plugin load or hot reload counts as a
/// login), so a meta can still act on "no kills at all". After that it counts from the later of
/// the last kill and the last macro start: restarting the macro starts the count again.
///
/// Monotonic (Environment.TickCount64, not the wall clock). Writers are the plugin pump thread;
/// the status feed reads from its poll thread, so every field is read and written with Interlocked.
/// </summary>
internal sealed class KillClock
{
    private const long None = long.MinValue;

    private readonly Func<long> _nowMs;
    private long _sessionStartMs;
    private long _macroStartMs = None;
    private long _lastKillMs = None;
    private int _macroWasRunning;

    /// <param name="nowMs">Monotonic milliseconds; tests pass a fake clock.</param>
    public KillClock(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
        _sessionStartMs = _nowMs();
    }

    /// <summary>Asked on every read, so a macro start is seen before the first condition after it.
    /// The plugin also calls <see cref="ObserveMacro"/> every tick.</summary>
    public Func<bool>? MacroRunning { get; set; }

    /// <summary>Login (and plugin load): no kill yet, the clock counts from now. A macro that is
    /// already on (start macro on login) counts as started now.</summary>
    public void SessionStarted()
    {
        Interlocked.Exchange(ref _sessionStartMs, _nowMs());
        Interlocked.Exchange(ref _macroStartMs, None);
        Interlocked.Exchange(ref _lastKillMs, None);
        Interlocked.Exchange(ref _macroWasRunning, 0);
        Observe();
    }

    /// <summary>Notes a macro start (off to on). Safe to call any number of times.</summary>
    public void ObserveMacro(bool running)
    {
        int prev = Interlocked.Exchange(ref _macroWasRunning, running ? 1 : 0);
        if (running && prev == 0)
            Interlocked.Exchange(ref _macroStartMs, _nowMs());
    }

    /// <summary>We killed something (KillerNotification only).</summary>
    public void RecordKill() => Interlocked.Exchange(ref _lastKillMs, _nowMs());

    /// <summary>True once we have a kill this session.</summary>
    public bool HasKill => Interlocked.Read(ref _lastKillMs) != None;

    /// <summary>What the clock counts from: "kill", "macro start" or "login".</summary>
    public string Since
    {
        get
        {
            Observe();
            long kill = Interlocked.Read(ref _lastKillMs), macro = Interlocked.Read(ref _macroStartMs);
            if (kill != None && kill >= macro) return "kill";
            return macro != None ? "macro start" : "login";
        }
    }

    /// <summary>Milliseconds since our last kill (before the first: since the macro start, or login). Never negative.</summary>
    public long ElapsedMs()
    {
        Observe();
        // The later of the last kill and the last macro start: a restart after a long pause
        // starts the count again (Tom, 2026-10-06), so "no kills in 10 min" doesn't fire at once.
        long from = Math.Max(Interlocked.Read(ref _lastKillMs), Interlocked.Read(ref _macroStartMs));
        if (from == None) from = Interlocked.Read(ref _sessionStartMs);
        return Math.Max(0, _nowMs() - from);
    }

    /// <summary>Whole seconds for secondssincelastkill[].</summary>
    public long SecondsSinceLastKill() => ElapsedMs() / 1000;

    /// <summary>Whole seconds since an actual kill, or -1 before the first (the status feed's value).</summary>
    public int SecondsSinceKillOnly()
    {
        long kill = Interlocked.Read(ref _lastKillMs);
        if (kill == None) return -1;
        return (int)Math.Clamp((_nowMs() - kill) / 1000, 0, int.MaxValue);
    }

    /// <summary>"No Kills Within Minutes": true when nothing was killed for at least
    /// <paramref name="minutes"/> minutes (decimals allowed: 0.5 = 30 s).</summary>
    public bool NoKillsFor(double minutes) => ElapsedMs() >= minutes * 60_000.0;

    /// <summary>The condition's value as typed in the editor or the .af file: minutes, decimals
    /// allowed ("5", "2.5"; a comma decimal is accepted too). False for empty, negative or not a number.</summary>
    public static bool TryParseMinutes(string? text, out double minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().Replace(',', '.');
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double m)) return false;
        if (double.IsNaN(m) || double.IsInfinity(m) || m < 0) return false;
        minutes = m;
        return true;
    }

    private void Observe()
    {
        Func<bool>? probe = MacroRunning;
        if (probe != null) ObserveMacro(probe());
    }
}
