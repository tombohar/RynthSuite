using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// [HealLatency]: how long health sat under a heal line before the first heal went out, and what
/// held it meanwhile. One line per episode, and a summary every <see cref="SummaryEvery"/> episodes.
///
/// Why (2026-10-05): a tester playing a mage reported heals that are "sometimes fine, sometimes
/// several seconds late", and nothing in the log said when health crossed a line or what the bot
/// was waiting on. With this a tester's log answers it:
///
///   [HealLatency] 3120 ms: health 41% under Heal At 60% (in combat) -> Heal Self; held by: busy count 2980 ms (leftover), cast gate 140 ms; arbiter Combat 120 ms
///
/// An episode opens on the first tick health is under the active line (Emergency Heal At, Stamina
/// To Health At, Heal At in combat, Top Off HP idle) and ends at the first heal sent for it (kit,
/// Heal Self, Stamina to Health or a health potion). While health stays under a line, the next
/// episode opens once that heal has resolved (no kit or vital cast left in flight), so a slow
/// second heal is measured from when it became possible, not from the first one. An episode that
/// ends with no heal (health rose, or the fight ended and the idle line is lower) is logged only
/// when it lasted <see cref="NoHealLogMs"/> or more.
///
/// Pure bookkeeping: BuffManager feeds it once per heartbeat and when a heal goes out; it never
/// calls the game. The clock comes from the caller (tests move it by hand).
/// </summary>
internal sealed class HealLatencyTracker
{
    /// <summary>A summary line after this many healed episodes.</summary>
    public const int SummaryEvery = 10;
    /// <summary>An episode that ends without a heal is logged only from this long.</summary>
    public const double NoHealLogMs = 1000;

    private readonly Action<string> _log;
    public HealLatencyTracker(Action<string> log) => _log = log;

    // ── The open episode ────────────────────────────────────────────────────
    private bool _open;
    private bool _healed;        // a heal went out for the open episode; waiting for it to resolve
    private bool _followUp;      // opened while health stayed under the line after a heal
    private DateTime _start;
    private int _startHp;
    private string _line = "";
    private int _lineValue;
    private bool _inCombat;
    private DateTime _lastTickAt = DateTime.MinValue;
    // Time held per reason, and time the arbiter gave the tick to something other than Buffing.
    private readonly Dictionary<string, double> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _arbiter = new(StringComparer.Ordinal);
    private string _lastDetail = "";

    // ── Summary since the last summary line ─────────────────────────────────
    private int _n;
    private double _sumMs, _maxMs;
    private string _maxWhat = "";
    private int _over1s, _over3s, _noHeal;

    /// <summary>Totals for the tests and /ra status.</summary>
    public int Episodes { get; private set; }
    public double LastDelayMs { get; private set; } = -1;
    public string LastLine { get; private set; } = "";
    public bool IsOpen => _open && !_healed;

    /// <summary>
    /// Once per heartbeat, before the tick's work: health and the line it is under (line "" = none).
    /// <paramref name="healInFlight"/>: a kit or a heal cast from the last episode hasn't resolved yet.
    /// </summary>
    public void Observe(DateTime now, int hp, string line, int lineValue, bool inCombat, bool healInFlight)
    {
        bool under = line.Length > 0;
        if (!under)
        {
            if (_open && !_healed)
            {
                double ms = (now - _start).TotalMilliseconds;
                if (ms >= NoHealLogMs)
                {
                    _noHeal++;
                    _log($"[HealLatency] no heal in {ms:0} ms: health {_startHp}% under {_line} {_lineValue}%{Where(_inCombat)}{(_followUp ? " (follow-up)" : "")}, " +
                         $"then {hp}% (over the line now); held by: {Held()}{Arb()}");
                }
            }
            _open = false;
            _healed = false;
            _followUp = false;
            return;
        }

        if (_open && _healed)
        {
            // Still under a line after a heal: the next one is measured from when it became possible.
            if (healInFlight) { _lastTickAt = now; return; }
            Begin(now, hp, line, lineValue, inCombat, followUp: true);
            return;
        }
        if (!_open)
        {
            Begin(now, hp, line, lineValue, inCombat, followUp: false);
            return;
        }
        // Open and waiting: a lower line (Heal At -> Emergency) is worth saying in the line.
        if (line != _line && (line.StartsWith("Emergency", StringComparison.Ordinal) || line.StartsWith("Stamina", StringComparison.Ordinal)))
        {
            _line = line;
            _lineValue = lineValue;
        }
    }

    /// <summary>Once per heartbeat, after the tick's work: why no heal went out this tick.</summary>
    public void EndTick(DateTime now, string heldBy, string detail, string arbiterActivity)
    {
        if (!_open || _healed) { _lastTickAt = now; return; }
        double dt = _lastTickAt == DateTime.MinValue ? 0 : (now - _lastTickAt).TotalMilliseconds;
        if (dt < 0 || dt > 2000) dt = 0;   // a hitch or a paused clock is not "held"
        _lastTickAt = now;
        if (dt <= 0) return;
        string key = heldBy.Length > 0 ? heldBy : "nothing reported";
        _held[key] = (_held.TryGetValue(key, out double h) ? h : 0) + dt;
        if (detail.Length > 0) _lastDetail = detail;
        if (arbiterActivity.Length > 0 && arbiterActivity != "Buffing")
            _arbiter[arbiterActivity] = (_arbiter.TryGetValue(arbiterActivity, out double a) ? a : 0) + dt;
    }

    /// <summary>A heal went out (kit, Heal Self, Stamina to Health, health potion).</summary>
    public void HealSent(DateTime now, string action)
    {
        if (!_open || _healed) return;
        _healed = true;
        double ms = Math.Max(0, (now - _start).TotalMilliseconds);
        Episodes++;
        LastDelayMs = ms;
        string line = $"[HealLatency] {ms:0} ms: health {_startHp}% under {_line} {_lineValue}%{Where(_inCombat)}{(_followUp ? " (follow-up)" : "")} -> {action}; held by: {Held()}{Arb()}";
        LastLine = line;
        _log(line);

        _n++;
        _sumMs += ms;
        if (ms >= 1000) _over1s++;
        if (ms >= 3000) _over3s++;
        if (ms > _maxMs) { _maxMs = ms; _maxWhat = $"{action}, {TopHeld()}"; }
        if (_n >= SummaryEvery)
        {
            _log($"[HealLatency] summary of {_n} heals: avg {_sumMs / _n:0} ms, max {_maxMs:0} ms ({_maxWhat}); " +
                 $"{_over1s} over 1 s, {_over3s} over 3 s; {_noHeal} episode(s) with no heal");
            _n = 0; _sumMs = 0; _maxMs = 0; _maxWhat = ""; _over1s = 0; _over3s = 0; _noHeal = 0;
        }
    }

    private void Begin(DateTime now, int hp, string line, int lineValue, bool inCombat, bool followUp)
    {
        _open = true;
        _healed = false;
        _followUp = followUp;
        _start = now;
        _startHp = hp;
        _line = line;
        _lineValue = lineValue;
        _inCombat = inCombat;
        _lastTickAt = now;
        _held.Clear();
        _arbiter.Clear();
        _lastDetail = "";
    }

    private static string Where(bool inCombat) => inCombat ? " (in combat)" : " (idle)";

    private string Held()
    {
        if (_held.Count == 0) return "nothing (healed on the first tick)";
        var parts = new List<KeyValuePair<string, double>>(_held);
        parts.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count && i < 4; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(parts[i].Key).Append(' ').Append(parts[i].Value.ToString("0")).Append(" ms");
        }
        if (_lastDetail.Length > 0) sb.Append(" [last: ").Append(_lastDetail).Append(']');
        return sb.ToString();
    }

    private string TopHeld()
    {
        string top = "nothing";
        double best = -1;
        foreach (var kv in _held)
            if (kv.Value > best) { best = kv.Value; top = kv.Key; }
        return top;
    }

    private string Arb()
    {
        if (_arbiter.Count == 0) return "";
        var sb = new StringBuilder("; arbiter gave the tick to ");
        bool first = true;
        foreach (var kv in _arbiter)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("0")).Append(" ms");
        }
        return sb.ToString();
    }
}
