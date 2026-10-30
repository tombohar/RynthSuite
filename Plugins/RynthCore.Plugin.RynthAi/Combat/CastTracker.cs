using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Bookkeeping for the casts BuffManager sends (buffs and vitals): when each one went out,
/// when its incantation ("You say, ...") arrived, and whether the server has finished it.
///
/// Why (Drakkon on DreamWeave, 2026-10-05, ops\overnight\2026-10-05-stuck-cast.md):
///   * The give-up timers ran from the SEND. A healthy DreamWeave vital takes 2.2-2.5 s from
///     its incantation to its result, and the incantation lags the send by up to 1.2 s, so a
///     cast the server was still finishing was given up at 2.5 s and the next one drew
///     "You're too busy!". Now the give-up runs from the incantation when one arrives
///     (<see cref="GiveUpAt"/>), capped, and from the send when none does.
///   * "Has the server finished?" cleared on ANY UseDone, including the 0x1D refusal of the
///     next cast. Now only a completion that belongs to the outstanding cast counts: its own
///     result line, or a UseDone with no error after its incantation (<see cref="AwaitingServer"/>).
///     A refusal is a refusal, not "done".
///   * A cast the server held open (incantation, no result) was only released when someone
///     moved the character. <see cref="Refusal"/> says when to send one movement nudge.
///   * Each cast's time from incantation to result is logged ([CastTime]), so a slow or held
///     cast shows in the log.
///
/// Pure bookkeeping: it never calls the game. The clock is injectable for the tests.
/// </summary>
internal sealed class CastTracker
{
    internal sealed class Cast
    {
        public int SpellId;
        public string Name = "";
        public DateTime SentAt;
        public DateTime IncantAt = DateTime.MinValue;
        public int SeqAtSend;
        public int SeqAtIncant;
        public int SeqAtRefusal;
        public bool Refused;      // AC answered this cast with "You're too busy!"
        public bool Done;         // its result line, or UseDone(0) after its incantation
        public bool Nudged;       // the one movement nudge for this held cast has gone out
        public bool StuckLogged;
        // The script combat hook chose this cast (a heal): no early nudge, HeldAfterIncantMs only.
        // A Vitality Siphon's result can come 2.75 s after its incantation (2026-10-10 log).
        public bool Script;
        public bool HasIncantation => IncantAt != DateTime.MinValue;
    }

    /// <summary>Incantation to result, at most. Healthy DreamWeave vitals: 2.21-2.52 s.</summary>
    public const double IncantToResultMs = 3000;
    /// <summary>Never wait longer than this after the send, whatever the incantation said.</summary>
    public const double GiveUpCapMs = 6000;
    /// <summary>An incantation with no result for this long is a held cast.</summary>
    public const double HeldAfterIncantMs = 6000;
    /// <summary>
    /// The same, when the held cast is a heal or health is under a heal line (2026-10-05: Drakkon
    /// died 4 s after a Heal Self the server held open). Past the healthy 2.2-2.5 s from
    /// incantation to result, so a cast still finishing normally is not nudged.
    /// </summary>
    public const double UrgentHeldAfterIncantMs = 2800;
    /// <summary>An incantation this long after the send isn't credited to it.</summary>
    private const double IncantWindowMs = 15000;

    private readonly Action<string> _log;

    public CastTracker(Action<string> log) => _log = log;

    public Func<DateTime> Clock = () => RynthClock.Now;
    public DateTime Now => Clock();

    /// <summary>The last cast sent.</summary>
    public Cast? Current { get; private set; }

    /// <summary>The cast whose incantation arrived and whose result hasn't (may be older than Current).</summary>
    public Cast? Open { get; private set; }

    /// <summary>When to stop waiting for <paramref name="c"/>: <paramref name="fallbackMs"/> after
    /// the send, or later when its incantation came late (incantation + IncantToResultMs), but
    /// never past max(fallbackMs, GiveUpCapMs) after the send.</summary>
    public static DateTime GiveUpAt(Cast c, double fallbackMs)
    {
        DateTime basis = c.SentAt.AddMilliseconds(fallbackMs);
        if (!c.HasIncantation) return basis;
        DateTime byIncant = c.IncantAt.AddMilliseconds(IncantToResultMs);
        DateTime cap = c.SentAt.AddMilliseconds(Math.Max(fallbackMs, GiveUpCapMs));
        DateTime d = byIncant > basis ? byIncant : basis;
        return d < cap ? d : cap;
    }

    /// <summary>True when the pending cast <paramref name="spellId"/> is past its give-up time.
    /// Null when the tracker doesn't hold that cast (the caller uses its old timer).</summary>
    public bool? GiveUpReached(int spellId, double fallbackMs)
    {
        Cast? c = Current;
        if (c == null || c.SpellId != spellId) return null;
        return Now >= GiveUpAt(c, fallbackMs);
    }

    public void Sent(int spellId, string name, int useDoneSeq)
    {
        DateTime now = Now;
        // An earlier cast that is still held open stays Open; one with no incantation is simply replaced.
        Current = new Cast { SpellId = spellId, Name = name, SentAt = now, SeqAtSend = useDoneSeq };
    }

    /// <summary>"You say, ..." arrived. Credited to the last cast sent when it has none yet.
    /// A new incantation also means the server let go of any older open cast.</summary>
    public Cast? Incantation(int useDoneSeq)
    {
        DateTime now = Now;
        Cast? c = Current;
        if (c == null || c.HasIncantation || c.Refused || c.Done || (now - c.SentAt).TotalMilliseconds > IncantWindowMs)
            return null;

        if (Open != null && Open != c && !Open.Done)
        {
            _log($"[CastTime] '{Open.Name}': no result {Secs(now - Open.IncantAt)} after its incantation; the server started the next cast ('{c.Name}'), so it is over.");
            Open.Done = true;
        }
        c.IncantAt = now;
        c.SeqAtIncant = useDoneSeq;
        Open = c;
        _log($"[CastTime] '{c.Name}': incantation {Ms(now - c.SentAt)} after the send.");
        return c;
    }

    /// <summary>A result line ("You cast X ...", a fizzle, a hard refusal). <paramref name="castText"/>
    /// is what followed "You cast " (null for lines that don't name the spell). <paramref name="atStart"/>:
    /// an unnamed refusal the server gives before the cast starts (no components, not enough mana),
    /// which belongs to the last cast sent. Returns the cast it belongs to, or null.</summary>
    public Cast? Result(string? castText, string kind, bool atStart = false)
    {
        Cast? c = null;
        bool currentLive = Current != null && !Current.Done && !Current.Refused;
        bool openLive = Open != null && !Open.Done;
        if (!string.IsNullOrEmpty(castText))
        {
            if (Current != null && !Current.Done && Matches(castText, Current.Name)) c = Current;
            else if (openLive && Matches(castText, Open!.Name)) c = Open;
        }
        else if (atStart)
            c = currentLive && !Current!.HasIncantation ? Current : (openLive ? Open : null);
        else
            // Unnamed, after the windup (a fizzle): the cast the server is running, else the last one sent.
            c = openLive ? Open : (currentLive ? Current : null);
        if (c == null) return null;
        Close(c, kind);
        return c;
    }

    private static bool Matches(string castText, string name) =>
        name.Length > 0 && castText.StartsWith(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The cast's own enchantment landed (self-buff confirmed by event or registry).</summary>
    public void Confirmed(int spellId, string kind)
    {
        if (Current != null && !Current.Done && Current.SpellId == spellId) Close(Current, kind);
        else if (Open != null && !Open.Done && Open.SpellId == spellId) Close(Open, kind);
    }

    private void Close(Cast c, string kind)
    {
        c.Done = true;
        // The server is in the cast's recoil now: one heal may be queued behind it (ActionPacer).
        RynthCore.Plugin.Shared.ActionPacer.NoteCastResult();
        DateTime now = Now;
        string nudged = c.Nudged ? " (after the movement nudge)" : "";
        if (c.HasIncantation)
            _log($"[CastTime] '{c.Name}': {kind} {Secs(now - c.IncantAt)} after its incantation (send to incantation {Ms(c.IncantAt - c.SentAt)}){nudged}.");
        else
            _log($"[CastTime] '{c.Name}': {kind} {Secs(now - c.SentAt)} after the send (no incantation seen){nudged}.");
        if (Open == c) Open = null;
    }

    /// <summary>The last UseDone the engine saw. A UseDone with no error after a cast's incantation
    /// is that cast finishing; anything else (0x1D too busy, a UseDone before the incantation) is not.</summary>
    public void ObserveUseDone(int lastSeq, uint lastError)
    {
        if (lastError != 0) return;
        foreach (Cast? c in new[] { Open, Current })
        {
            if (c == null || c.Done || !c.HasIncantation || lastSeq <= c.SeqAtIncant) continue;
            Close(c, $"UseDone #{lastSeq} (no error)");
        }
    }

    /// <summary>
    /// True while the server hasn't finished the last cast sent. Clears on that cast's result
    /// line, on a UseDone(0) after its incantation, after a refusal once a UseDone(0) shows the
    /// server free again, or at its give-up time (<paramref name="fallbackMs"/> from the send,
    /// later with a late incantation). <paramref name="hasLast"/> false (an engine before v70)
    /// keeps the old rule: any new UseDone clears it.
    /// </summary>
    public bool AwaitingServer(double fallbackMs, int useDoneSeq, bool hasLast, int lastSeq, uint lastError)
    {
        Cast? c = Current;
        if (c == null || c.Done) return false;
        if (Now >= GiveUpAt(c, fallbackMs)) return false;
        if (!hasLast) return useDoneSeq == c.SeqAtSend;
        ObserveUseDone(lastSeq, lastError);
        if (c.Done) return false;
        if (c.Refused && lastError == 0 && lastSeq > c.SeqAtRefusal) return false;
        return true;
    }

    /// <summary>
    /// "You're too busy!" arrived. The last cast sent, if it had no incantation yet, was refused.
    /// Returns true exactly once per held cast: an incantation more than HeldAfterIncantMs ago,
    /// no result, and AC still refusing casts. The caller sends one movement nudge.
    /// </summary>
    public bool Refusal(int useDoneSeq, double heldAfterMs = HeldAfterIncantMs)
    {
        Cast? c = Current;
        if (c != null && !c.HasIncantation && !c.Done && !c.Refused)
        {
            c.Refused = true;
            c.SeqAtRefusal = useDoneSeq;
        }
        return TakeNudge(heldAfterMs);
    }

    /// <summary>
    /// True exactly once per held cast: open (incantation, no result) for more than
    /// <paramref name="heldAfterMs"/> and not nudged yet. Marks it nudged. The refusal path uses
    /// it with HeldAfterIncantMs; a heal (or health under a heal line) polls it each tick with
    /// UrgentHeldAfterIncantMs, without waiting for a refusal.
    /// </summary>
    public bool TakeNudge(double heldAfterMs)
    {
        Cast? held = Open;
        if (held == null || held.Done || held.Nudged || !held.HasIncantation) return false;
        if ((Now - held.IncantAt).TotalMilliseconds <= heldAfterMs) return false;
        held.Nudged = true;
        return true;
    }

    /// <summary>The cast the server holds open (incantation, no result), or null.</summary>
    public Cast? Held => Open is { Done: false } o && o.HasIncantation ? o : null;

    /// <summary>One log line the first time a cast has been open past HeldAfterIncantMs (polled each tick).</summary>
    public void Poll()
    {
        Cast? held = Open;
        if (held == null || held.Done || held.StuckLogged) return;
        if ((Now - held.IncantAt).TotalMilliseconds <= HeldAfterIncantMs) return;
        held.StuckLogged = true;
        _log($"[CastTime] '{held.Name}': HELD - incantation {Secs(Now - held.IncantAt)} ago and no result yet (the server is holding the cast open).");
    }

    /// <summary>Seconds a cast has been held open (incantation, no result), or 0.</summary>
    public double HeldSeconds
    {
        get
        {
            Cast? held = Open;
            return held == null || held.Done ? 0 : (Now - held.IncantAt).TotalSeconds;
        }
    }

    private static string Secs(TimeSpan t) => $"{t.TotalSeconds:0.00} s";
    private static string Ms(TimeSpan t) => $"{t.TotalMilliseconds:0} ms";
}
