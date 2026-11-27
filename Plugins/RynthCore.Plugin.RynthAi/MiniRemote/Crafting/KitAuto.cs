// KitAuto.cs - "Make kits when low" (Mini Remote > Crafting > Kits, /ra kits auto): with the macro
// on, a kit run starts by itself when fewer than MakeKitsBelow Plentiful Healing Kits are carried
// and nothing needs the character. Pure (System only), so RynthCore.RynthAiTests links it; the
// plugin side (RynthAiPlugin.Kits.cs) reads the facts each tick, holds the bot while a run goes
// (the way it holds for buffs) and KitRunner does the combines.
//
// A start needs every one of: the setting on, the macro on, fewer kits than the line, nothing else
// going (buffing or healing, a fight, looting, salvaging, a portal, a vendor, following, ...),
// vitals over their heal lines, no monster within the safe radius (35 yd, or Monster Range when
// that is bigger), no hit for 20 s, and the pack making one kit at the Min chance % floor with
// Healing trained. Then the bot is held still: peace mode is asked for (combat mode is the only
// one left from a fight), and once in peace and standing still for a second the run starts.
//
// A run stops at once on a monster inside the radius, a hit, a heal line, buffing, the macro off,
// or moving (KitRunner checks the last three itself). Waits after a run:
//   - a stop for danger (monster, damage, heal, moving, leaving peace): 60 s;
//   - a run that made nothing (every combine failed, the server's chance under the floor, a
//     refusal) or the player's Stop: 10 min;
//   - a run that made kits: the next look in 30 s (the count decides then).
// Before a start, a pack that can't make a kit (out of an ingredient, under the floor, untrained)
// is said once per reason, then looked at again every 30 s.
//
// "Make ammo when low" (MissileCraftingManager.Auto, RynthAiPlugin.Ammo.cs, 2026-11-23) runs on the
// same rules with its own instance: its Name and Doing, and the facts' CountText ("230 arrows"),
// BelowMax (ammo counts run to thousands) and IdleWhy ("no launcher in hand"). Their defaults are the
// kits' own, so a kit look reads exactly as before.
using System;

namespace RynthCore.Plugin.RynthAi.MiniRemote.Crafting;

/// <summary>Why a kit run ended (KitRunner sets it; the "when low" waits follow it).</summary>
internal enum KitStopKind
{
    /// <summary>Logged out, settings gone: no wait of its own.</summary>
    Other,
    /// <summary>Made the count asked for, or the pack ran out.</summary>
    Finished,
    /// <summary>A monster, a hit, a heal line, buffing, moving, leaving peace mode.</summary>
    Danger,
    /// <summary>The server's chance under the floor, a refusal, no answer, AC's dialog wanting a click.</summary>
    Refused,
    /// <summary>The player's Stop (the button, /ra kits stop).</summary>
    User,
    /// <summary>The macro was turned off during a "when low" run.</summary>
    MacroOff,
}

/// <summary>What stops a "when low" start (the first that holds, cheap ones first).</summary>
internal enum KitBlock { None, Off, MacroOff, Enough, Busy, Vitals, Monster, Hurt, Pack }

/// <summary>What the next tick does about "Make kits when low".</summary>
internal enum KitAutoStep
{
    /// <summary>Nothing: the bot goes on as usual.</summary>
    Wait,
    /// <summary>Hold the bot still (getting ready).</summary>
    Hold,
    /// <summary>Hold the bot and ask for peace mode.</summary>
    GoPeace,
    /// <summary>Start the run now (and hold the bot while it goes).</summary>
    Start,
}

/// <summary>One look at the character for "Make kits when low" (RynthAiPlugin.Kits.cs fills it).</summary>
internal sealed class KitAutoFacts
{
    public bool Enabled;
    public bool MacroOn;
    /// <summary>Plentiful Healing Kits carried (main pack and side packs).</summary>
    public int Kits;
    /// <summary>A run starts with fewer kits than this.</summary>
    public int Below = KitAuto.DefaultBelow;
    /// <summary>"" when the pack makes a kit at the floor; else the planner's why (out of X, under the floor, untrained).</summary>
    public string PackWhy = string.Empty;
    public bool InPeace = true;
    /// <summary>The character moved since the last tick.</summary>
    public bool Moving;
    /// <summary>The nearest live monster (PositiveInfinity = none known).</summary>
    public double NearestMonsterYd = double.PositiveInfinity;
    public double RadiusYd = KitAuto.DefaultRadiusYd;
    /// <summary>Since health last went down (MaxValue = not this session).</summary>
    public long SinceHurtMs = long.MaxValue;
    /// <summary>"" when health, stamina and mana are over their lines; else which one.</summary>
    public string VitalsWhy = string.Empty;
    /// <summary>"" when nothing else needs the character; else what does (buffing, a fight, looting, ...).</summary>
    public string BusyWhy = string.Empty;
    /// <summary>The most <see cref="Below"/> may be (kits: <see cref="KitAuto.MaxBelow"/>).</summary>
    public int BelowMax = KitAuto.MaxBelow;
    /// <summary>"N kits" for the count in the status and chat (null = <see cref="KitAuto.KitsText"/>).</summary>
    public Func<int, string>? CountText;
    /// <summary>"" = look as usual; else nothing to do for now, and why (no launcher in hand, ...): no start, no chat.</summary>
    public string IdleWhy = string.Empty;
}

internal sealed class KitAuto
{
    public const int DefaultBelow = 2;
    public const int DefaultUpTo = 5;
    public const int MaxBelow = 50;
    public const int MaxUpTo = 100;
    /// <summary>No monster this close (or within Monster Range, when that is bigger) for a start; one coming this close stops a run.</summary>
    public const double DefaultRadiusYd = 35;
    /// <summary>No start for this long after a hit.</summary>
    public const long HurtQuietMs = 20_000;
    /// <summary>The next look while something else has the character, or the kits are enough.</summary>
    public const long CheckMs = 5_000;
    /// <summary>The next look after the pack couldn't make a kit, and after a run that made some.</summary>
    public const long PackCheckMs = 30_000;
    /// <summary>The wait after a stop for danger (a monster, a hit, a heal line, moving).</summary>
    public const long CooldownMs = 60_000;
    /// <summary>The wait after a run that made nothing, or the player's Stop.</summary>
    public const long BackoffMs = 600_000;
    /// <summary>Getting ready (peace mode, standing still) gives up after this.</summary>
    public const long ReadyTimeoutMs = 6_000;
    /// <summary>Standing still this long in peace mode before the first combine.</summary>
    public const long SettleMs = 1_000;
    /// <summary>Peace mode is asked for again after this.</summary>
    public const long PeaceResendMs = 2_000;

    /// <summary>Clock (ms, monotonic); tests replace it.</summary>
    public Func<long> NowMs = static () => Environment.TickCount64;
    /// <summary>A chat line for the player (a reason said once).</summary>
    public Action<string>? Say;
    /// <summary>The feature's name in chat ("Make kits when low").</summary>
    public string Name = "Make kits when low";
    /// <summary>What a run does, for the status ("stopping to make kits").</summary>
    public string Doing = "make kits";

    private long _nextCheckAt;
    private string _waitWhy = string.Empty;
    private long _readySince = -1;
    private long _stillSince = -1;
    private long _peaceSentAt = long.MinValue / 2;
    private string _said = string.Empty;
    private volatile string _status = "off";

    /// <summary>What it is doing, for the Kits tab and /ra kits auto status.</summary>
    public string Status => _status;
    /// <summary>Getting ready for a run (the bot is held).</summary>
    public bool Readying => _readySince >= 0;
    /// <summary>No look before this (0 = the next tick).</summary>
    public long NextCheckAt => _nextCheckAt;
    /// <summary>The reason last said in chat ("" = none since the last run that made kits).</summary>
    public string Said => _said;

    // ── Rules ───────────────────────────────────────────────────────────────

    /// <summary>The first thing that stops a start now (KitBlock.None and "" when it can go, peace mode aside).</summary>
    public static (KitBlock Block, string Why) Blocker(KitAutoFacts f)
    {
        if (!f.Enabled) return (KitBlock.Off, "off");
        if (!f.MacroOn) return (KitBlock.MacroOff, "the macro is off");
        if (f.IdleWhy.Length > 0) return (KitBlock.Enough, f.IdleWhy);
        int below = Math.Clamp(f.Below, 1, Math.Max(1, f.BelowMax));
        if (f.Kits >= below) return (KitBlock.Enough, $"{(f.CountText ?? KitsText)(f.Kits)} carried (makes more under {below})");
        if (f.BusyWhy.Length > 0) return (KitBlock.Busy, f.BusyWhy);
        if (f.VitalsWhy.Length > 0) return (KitBlock.Vitals, f.VitalsWhy);
        if (f.NearestMonsterYd <= f.RadiusYd) return (KitBlock.Monster, $"monster {f.NearestMonsterYd:0} yd away");
        if (f.SinceHurtMs < HurtQuietMs) return (KitBlock.Hurt, $"hit {f.SinceHurtMs / 1000} s ago");
        if (f.PackWhy.Length > 0) return (KitBlock.Pack, f.PackWhy);
        return (KitBlock.None, string.Empty);
    }

    /// <summary>
    /// Why a "when low" run must stop now ("" = go on): buffing or a fight, a heal line, a monster
    /// within the radius. The macro off, moving, a hit and leaving peace mode KitRunner stops on itself.
    /// </summary>
    public static string StopReason(KitAutoFacts f)
    {
        if (f.BusyWhy.Length > 0) return f.BusyWhy;
        if (f.VitalsWhy.Length > 0) return f.VitalsWhy;
        if (f.NearestMonsterYd <= f.RadiusYd) return $"a monster came within {f.NearestMonsterYd:0} yd";
        return string.Empty;
    }

    public static string KitsText(int n) => n == 1 ? "1 kit" : $"{n} kits";

    // ── The tick ────────────────────────────────────────────────────────────

    /// <summary>
    /// One tick with no run going. <paramref name="facts"/> is read only when a look is due (it walks
    /// the pack and the world), every tick while getting ready.
    /// </summary>
    public KitAutoStep Decide(Func<KitAutoFacts> facts)
    {
        long now = NowMs();
        if (_readySince < 0 && now < _nextCheckAt)
        {
            long left = (_nextCheckAt - now + 999) / 1000;
            _status = (_waitWhy.Length > 0 ? "waiting: " + _waitWhy + "; " : "") + $"next check in {left} s";
            return KitAutoStep.Wait;
        }

        KitAutoFacts f = facts();
        var (block, why) = Blocker(f);
        if (block != KitBlock.None)
        {
            bool wasReady = _readySince >= 0;
            EndReady();
            switch (block)
            {
                case KitBlock.Off:
                    _status = "off";
                    _nextCheckAt = 0;
                    break;
                case KitBlock.MacroOff:
                    _status = "waiting: the macro is off";
                    _nextCheckAt = 0;
                    break;
                case KitBlock.Pack:
                    // Said once per reason; not "can't read your skill" (the qualities arrive late after a login).
                    if (_said != why && !why.StartsWith("can't read", StringComparison.Ordinal))
                    {
                        _said = why;
                        SayLine($"[RynthAi] {Name}: {(f.CountText ?? KitsText)(f.Kits)} left, but none can be made now: {why}.");
                    }
                    Wait(now, PackCheckMs, why);
                    break;
                case KitBlock.Enough:
                    Wait(now, CheckMs, why);
                    break;
                default:
                    // Something came up while getting ready: the danger wait, as after a stopped run.
                    bool danger = block is KitBlock.Monster or KitBlock.Hurt or KitBlock.Vitals;
                    Wait(now, wasReady && danger ? CooldownMs : CheckMs, why);
                    break;
            }
            if (block is not (KitBlock.Off or KitBlock.MacroOff)) _status = "waiting: " + why;
            return KitAutoStep.Wait;
        }

        // Everything clear: hold the bot, go to peace mode, stand still, then start.
        if (_readySince < 0)
        {
            _readySince = now;
            _stillSince = -1;
            _peaceSentAt = long.MinValue / 2;
        }
        if (now - _readySince > ReadyTimeoutMs)
        {
            EndReady();
            Wait(now, CooldownMs, f.InPeace ? "couldn't stand still" : "couldn't go to peace mode");
            _status = "waiting: " + _waitWhy;
            return KitAutoStep.Wait;
        }
        if (!f.InPeace)
        {
            _stillSince = -1;
            _status = "going to peace mode";
            if (now - _peaceSentAt >= PeaceResendMs)
            {
                _peaceSentAt = now;
                return KitAutoStep.GoPeace;
            }
            return KitAutoStep.Hold;
        }
        if (f.Moving)
        {
            _stillSince = -1;
            _status = "stopping to " + Doing;
            return KitAutoStep.Hold;
        }
        if (_stillSince < 0) _stillSince = now;
        if (now - _stillSince < SettleMs)
        {
            _status = "getting ready";
            return KitAutoStep.Hold;
        }
        EndReady();
        _status = "starting";
        return KitAutoStep.Start;
    }

    /// <summary>The run went out (the Kits tab shows "running made/limit").</summary>
    public void Running(int made, int limit) => _status = limit > 0 ? $"running {made}/{limit}" : $"running ({made} made)";

    /// <summary>KitRunner refused the start after all (its own checks): look again later.</summary>
    public void StartRefused(string why)
    {
        Wait(NowMs(), PackCheckMs, why);
        _status = "waiting: " + why;
    }

    /// <summary>
    /// A "when low" run ended: the wait that follows it. Returns what the run's summary line adds
    /// ("" = nothing), so one line says how it went and when the next try is.
    /// </summary>
    public string RunEnded(KitStopKind kind, int made)
    {
        long now = NowMs();
        EndReady();
        switch (kind)
        {
            case KitStopKind.MacroOff:
            case KitStopKind.Other:
                _nextCheckAt = 0;
                _waitWhy = string.Empty;
                _status = kind == KitStopKind.MacroOff ? "waiting: the macro is off" : "idle";
                if (made > 0) _said = string.Empty;
                return string.Empty;
            case KitStopKind.Danger:
                if (made > 0) _said = string.Empty;
                Wait(now, CooldownMs, "the last run was stopped");
                return $" Next try in {CooldownMs / 1000} s at the earliest.";
            case KitStopKind.User:
                Wait(now, BackoffMs, "you stopped the last run");
                return $" Next try in {BackoffMs / 60_000} min (turn {Name} off to stop it for good).";
            default:
                if (made <= 0)
                {
                    Wait(now, BackoffMs, "the last run made nothing");
                    return $" Next try in {BackoffMs / 60_000} min.";
                }
                _said = string.Empty;
                Wait(now, PackCheckMs, string.Empty);
                return string.Empty;
        }
    }

    /// <summary>The setting was turned on, or the player asked (/ra kits auto on): look on the next tick.</summary>
    public void LookNow()
    {
        EndReady();
        _nextCheckAt = 0;
        _waitWhy = string.Empty;
        _said = string.Empty;
    }

    /// <summary>The setting is off (or the character logged out).</summary>
    public void Off()
    {
        EndReady();
        _nextCheckAt = 0;
        _waitWhy = string.Empty;
        _status = "off";
    }

    /// <summary>Logout: nothing carries over to the next character.</summary>
    public void Reset()
    {
        Off();
        _said = string.Empty;
    }

    private void Wait(long now, long ms, string why)
    {
        _nextCheckAt = now + ms;
        _waitWhy = why;
    }

    private void EndReady()
    {
        _readySince = -1;
        _stillSince = -1;
    }

    private void SayLine(string line)
    {
        try { Say?.Invoke(line); } catch { }
    }
}
