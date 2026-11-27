using System;
using System.Globalization;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

// Make kits (a players' request, 2026-11-22): /ra kits [status|make [N]|stop|floor <0-100>|show].
// The work is MiniRemote/Crafting/KitRunner.cs (ticked by the Crafting tab); the main path is the
// Mini Remote's Crafting tab, Kits sub-tab, "Make kits". Kits are made by hand only while the macro is off.
//
// Make kits when low (MakeKitsWhenLow, off by default; /ra kits auto ...): with the macro on, the
// Crafting tab's KitAuto decides when a run may start (MiniRemote/Crafting/KitAuto.cs) from the
// facts read here, and while one gets ready or goes the rest of the tick waits, as it does for buffs
// (TickAutoKits, right after the Buffing hold). Right after the arbiter's decision, ahead of every
// hold, CheckAutoKitsDanger stops a run at once on a monster within the safe radius, buffing or a
// heal line, a fight, a portal; KitRunner itself stops on a hit, moving, leaving peace or the macro off.
public sealed partial class RynthAiPlugin
{
    internal const string KitsUsage = "Usage: /ra kits [status | make [count] | stop | floor <0-100> | show | auto [on|off|status|below N|upto M]]";
    internal const string KitsAutoUsage = "Usage: /ra kits auto [status | on | off | below <1-50> | upto <1-100>]";

    /// <summary>/ra kits: status (the default), make [count], stop, floor &lt;percent&gt;, show (the Kits tab), auto ....</summary>
    private void HandleKitsCommand(string[] parts)
    {
        string verb = parts.Length > 2 ? parts[2].Trim().ToLowerInvariant() : "status";
        if (verb is not ("status" or "make" or "stop" or "floor" or "show" or "auto"))
        {
            ReplyFail($"[RynthAi] Make kits: unknown option '{parts[2]}'. {KitsUsage}");
            return;
        }
        if (verb == "auto") { HandleKitsAutoCommand(parts); return; }
        var remote = _miniRemote;
        if (remote == null) { ReplyFail("[RynthAi] Make kits: not ready yet (log in first)."); return; }
        var tab = remote.Crafting;
        var kits = tab.Kits;

        switch (verb)
        {
            case "status":
            {
                var rows = kits.RefreshPlan();
                foreach (var row in rows) ChatLine("[RynthAi] Make kits: " + KitPlanner.Line(row));
                ChatLine($"[RynthAi] Make kits: {(kits.Running ? kits.Status : "not running")}; floor {kits.Floor}%. "
                         + "Mini Remote > Crafting > Kits, or /ra kits make.");
                ChatLine(KitsAutoStatusLine());
                break;
            }
            case "make":
            {
                int count = 0;
                if (parts.Length > 3 && !parts[3].Equals("all", StringComparison.OrdinalIgnoreCase)
                    && (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1))
                {
                    ReplyFail($"[RynthAi] Make kits: the count must be a number of 1 or more, or 'all' (got '{parts[3]}'). {KitsUsage}");
                    break;
                }
                var recipe = kits.Find(null);
                if (recipe == null) { ReplyFail("[RynthAi] Make kits: no kit recipe is known."); break; }
                int can = KitPlanner.CanMake(recipe, kits.Pack());
                string why = kits.Start(recipe, count);
                if (why.Length > 0) { ReplyFail($"[RynthAi] Make kits: can't start - {why}."); break; }
                int target = count > 0 ? Math.Min(count, can) : can;
                ReplyOk($"[RynthAi] Make kits: making up to {target} {recipe.Name}(s), floor {kits.Floor}%. /ra kits stop to stop.");
                break;
            }
            case "stop":
                if (!kits.Running) { ReplyOk("[RynthAi] Make kits: nothing is running."); break; }
                kits.Stop("stopped by you");
                Reply.Answered();   // Stop printed the summary line
                break;
            case "floor":
            {
                if (parts.Length < 4)
                {
                    ReplyOk($"[RynthAi] Make kits: the floor is {kits.Floor}% (no combine under it is tried). {KitsUsage}");
                    break;
                }
                string text = parts[3].Trim().TrimEnd('%');
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct) || pct < 0 || pct > 100)
                {
                    ReplyFail($"[RynthAi] Make kits: the floor must be 0-100 (got '{parts[3]}'). {KitsUsage}");
                    break;
                }
                remote.State.Crafting.KitMinChancePercent = pct;
                ReplyOk($"[RynthAi] Make kits: floor set to {pct}%" + (pct == 0 ? " (any chance)." : "."));
                break;
            }
            case "show":
            {
                tab.SelectKitsTab();
                string? reply = remote.MiniRemoteTabCommand?.Invoke(MiniRemoteTabs.Crafting, "show");
                if (reply == null) ReplyFail("[RynthAi] Make kits: the Mini Remote isn't available yet.");
                else ReplyOk("[RynthAi] Make kits: " + reply);
                break;
            }
        }
    }

    // ── /ra kits auto ───────────────────────────────────────────────────────

    /// <summary>/ra kits auto [status | on | off | below N | upto M]: Make kits when low (a profile setting).</summary>
    private void HandleKitsAutoCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Make kits when low: not ready yet (log in first)."); return; }
        string verb = parts.Length > 3 ? parts[3].Trim().ToLowerInvariant() : "status";
        var auto = _miniRemote?.Crafting.AutoKits;
        switch (verb)
        {
            case "status":
                ChatLine(KitsAutoStatusLine());   // the status asked for
                break;
            case "on":
            case "off":
            {
                bool on = verb == "on";
                settings.MakeKitsWhenLow = on;
                _dashboard?.SaveSettings();
                if (on) auto?.LookNow();
                else
                {
                    var kits = _miniRemote?.Crafting.Kits;
                    if (kits != null && kits.Auto) kits.Stop("Make kits when low turned off", KitStopKind.Other);
                    auto?.Off();
                }
                ReplyOk($"[RynthAi] Make kits when low: {verb} (under {Below(settings)} kits, make up to {UpTo(settings)})"
                        + (on && !settings.IsMacroRunning ? "; it works while the macro runs." : "."));
                break;
            }
            case "below":
            case "upto":
            {
                int max = verb == "below" ? KitAuto.MaxBelow : KitAuto.MaxUpTo;
                if (parts.Length < 5)
                {
                    ReplyOk($"[RynthAi] Make kits when low: {(verb == "below" ? $"starts under {Below(settings)} kits" : $"makes up to {UpTo(settings)} kits a run")}. {KitsAutoUsage}");
                    break;
                }
                if (!int.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > max)
                {
                    ReplyFail($"[RynthAi] Make kits when low: {verb} must be a number from 1 to {max} (got '{parts[4]}'). {KitsAutoUsage}");
                    break;
                }
                if (verb == "below") settings.MakeKitsBelow = n; else settings.MakeKitsUpTo = n;
                _dashboard?.SaveSettings();
                auto?.LookNow();
                ReplyOk(verb == "below"
                    ? $"[RynthAi] Make kits when low: starts when you carry fewer than {n} kits{(settings.MakeKitsWhenLow ? "." : " (it is off: /ra kits auto on).")}"
                    : $"[RynthAi] Make kits when low: makes up to {n} kits a run{(settings.MakeKitsWhenLow ? "." : " (it is off: /ra kits auto on).")}");
                break;
            }
            default:
                ReplyFail($"[RynthAi] Make kits when low: unknown option '{parts[3]}'. {KitsAutoUsage}");
                break;
        }
    }

    private static int Below(LegacyUiSettings s) => Math.Clamp(s.MakeKitsBelow, 1, KitAuto.MaxBelow);
    private static int UpTo(LegacyUiSettings s) => Math.Clamp(s.MakeKitsUpTo, 1, KitAuto.MaxUpTo);

    /// <summary>One line: the setting, its numbers, the radius and what it is doing.</summary>
    private string KitsAutoStatusLine()
    {
        var s = _dashboard?.Settings;
        if (s == null) return "[RynthAi] Make kits when low: not ready yet (log in first).";
        string state = !s.MakeKitsWhenLow ? "off" : "on";
        string doing = "";
        var tab = _miniRemote?.Crafting;
        if (s.MakeKitsWhenLow && tab != null) doing = "; " + tab.AutoStatusText(s);
        return $"[RynthAi] Make kits when low: {state}, under {Below(s)} kits make up to {UpTo(s)}, "
               + $"no monster within {KitSafeRadiusYd(s):0} yd{doing}. Mini Remote > Crafting > Kits, /ra kits auto on|off.";
    }

    // ── Make kits when low: the macro side ──────────────────────────────────

    /// <summary>No monster this close for a start, and one coming this close stops a run: 35 yd, or Monster Range when bigger.</summary>
    internal static double KitSafeRadiusYd(LegacyUiSettings s) => Math.Max(KitAuto.DefaultRadiusYd, s.MonsterRange);

    /// <summary>The world walk for monsters is this far apart while nothing is going (the scan list is read every time).</summary>
    private const long KitMonsterWalkMs = 500;
    private long _kitMonsterWalkAt = long.MinValue / 2;
    private double _kitMonsterWalkYd = double.PositiveInfinity;
    private uint _kitLastHealth;
    private long _kitHurtAt = long.MinValue / 2;
    private bool _kitPoseKnown;
    private uint _kitPoseCell;
    private float _kitPoseX, _kitPoseY, _kitPoseZ;
    private bool _kitMoving;
    /// <summary>Moving more than this between two ticks counts as moving (KitRunner stops past 1.5 m from the start).</summary>
    private const float KitMoveEpsilon = 0.15f;

    private long KitNow() => _miniRemote?.Crafting.AutoKits.NowMs() ?? Environment.TickCount64;

    /// <summary>
    /// Every in-world tick, right after the arbiter's decision and ahead of every hold: notes a hit and
    /// a move, and stops a "when low" run at once when a monster comes within the radius, buffing or a
    /// heal is wanted, a fight starts or a portal is taken.
    /// </summary>
    private void CheckAutoKitsDanger(LegacyUiSettings s)
    {
        long now = KitNow();
        TrackKitHurtAndMove(now);
        var kits = _miniRemote?.Crafting.Kits;
        if (kits == null || !kits.Auto) return;
        var f = new KitAutoFacts
        {
            Enabled = true,
            MacroOn = s.IsMacroRunning,
            BusyWhy = KitBusyWhy(s, starting: false),
            VitalsWhy = KitVitalsWhy(s),
            RadiusYd = KitSafeRadiusYd(s),
        };
        // The combat scan (kept fresh by the hold) is read every tick; the world walk past it every half second.
        if (f.BusyWhy.Length == 0 && f.VitalsWhy.Length == 0) f.NearestMonsterYd = NearestMonsterYards(f.RadiusYd, now, force: false);
        string why = KitAuto.StopReason(f);
        if (why.Length > 0) kits.Stop(why, KitStopKind.Danger);
    }

    /// <summary>
    /// Make kits when low, after the Buffing hold: true while a run gets ready or goes, and the rest of
    /// the tick (combat, looting, navigation, item chores) waits, as it does for buffs.
    /// </summary>
    private bool TickAutoKits(LegacyUiSettings s)
    {
        var tab = _miniRemote?.Crafting;
        if (tab == null) return false;
        var kits = tab.Kits;
        var auto = tab.AutoKits;
        if (kits.Running)
        {
            if (!kits.Auto) return false;   // a run by hand (the macro is off then)
            auto.Running(kits.MadeCount, kits.Limit);
            return s.IsMacroRunning;
        }
        if (!s.MakeKitsWhenLow) { if (auto.Status != "off") auto.Off(); return false; }

        long now = auto.NowMs();
        KitAutoStep step = auto.Decide(() => KitStartFacts(s, kits, now, readying: auto.Readying));
        switch (step)
        {
            case KitAutoStep.GoPeace:
                Host.ChangeCombatModeFor(CombatMode.NonCombat, "Kits", "peace mode for Make kits when low");
                return true;
            case KitAutoStep.Hold:
                return true;
            case KitAutoStep.Start:
            {
                var recipe = kits.Find(null);
                if (recipe == null) { auto.StartRefused("no kit recipe is known"); return false; }
                string why = kits.StartAuto(recipe, UpTo(s), (kind, made) => auto.RunEnded(kind, made));
                if (why.Length > 0) { auto.StartRefused(why); return false; }
                auto.Running(0, UpTo(s));
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>One full look for a start (KitAuto reads it only when a look is due).</summary>
    private KitAutoFacts KitStartFacts(LegacyUiSettings s, KitRunner kits, long now, bool readying)
    {
        var f = new KitAutoFacts
        {
            Enabled = s.MakeKitsWhenLow,
            MacroOn = s.IsMacroRunning,
            Below = Below(s),
            RadiusYd = KitSafeRadiusYd(s),
        };
        if (!f.Enabled || !f.MacroOn) return f;
        var pack = kits.Pack();
        f.Kits = KitPlanner.Count(KitPlanner.PlentifulHealingKit, pack);
        if (f.Kits >= f.Below) return f;
        // Getting ready, the client is busy with our own peace-mode switch: not a reason to give up.
        f.BusyWhy = KitBusyWhy(s, starting: true, clientBusyCounts: !readying);
        // Make ammo when low getting ready (RynthAiPlugin.Ammo.cs; its run is "crafting ammunition" above).
        if (f.BusyWhy.Length == 0 && _missileCraftingManager?.Auto.Readying == true) f.BusyWhy = "making ammunition";
        f.VitalsWhy = KitVitalsWhy(s);
        f.NearestMonsterYd = NearestMonsterYards(f.RadiusYd, now, force: false);
        f.SinceHurtMs = _kitHurtAt <= long.MinValue / 4 ? long.MaxValue : Math.Max(0, now - _kitHurtAt);
        var recipe = kits.Find(null);
        if (recipe == null) f.PackWhy = "no kit recipe is known";
        else
        {
            var next = KitPlanner.Next(recipe, pack, kits.Skill, kits.Floor);
            f.PackWhy = next.Step == null ? next.Why : string.Empty;
        }
        try { f.InPeace = !Host.HasGetCurrentCombatMode || Host.GetCurrentCombatMode() == CombatMode.NonCombat; }
        catch { f.InPeace = false; }
        f.Moving = _kitMoving;
        return f;
    }

    /// <summary>
    /// What else has the character ("" = nothing): buffing or healing, a fight, a portal, a vendor or
    /// trade; for a start also looting, salvaging, other item work, following, travelling back to the
    /// corpse, or a busy client.
    /// </summary>
    private string KitBusyWhy(LegacyUiSettings s, bool starting, bool clientBusyCounts = true)
    {
        if (_activity == BotActivity.Buffing || (_buffManager != null && _buffManager.PendingSpellId != 0)) return "buffing or healing";
        var combat = _combatManager;
        if (_activity == BotActivity.Combat || FightIsOn(s) || (combat != null && (combat.IsActivelyEngaged || combat.IsUnderCloseAttack))) return "a fight";
        try { if (Host.HasIsPortaling && Host.IsPortaling()) return "in portal space"; } catch { }
        if (_navigationEngine?.IsInPortalAction == true) return "taking a portal";
        if (_autoVendor?.HoldsBot == true || _autoTrade?.HoldsBot == true) return "at a vendor or a trade";
        if (!starting) return string.Empty;
        if (_activity == BotActivity.Looting || _lastWantLoot || _openedContainerId != 0 || _targetCorpseId != 0) return "looting";
        if (_activity == BotActivity.Salvaging || _salvageManager?.IsBusy == true) return "salvaging";
        if (_missileCraftingManager?.IsCrafting == true) return "crafting ammunition";
        if (_aetheriaUnboxer?.IsUnboxing == true) return "unboxing aetheria";
        if (_scrollLearner?.IsReading == true) return "reading a scroll";
        if (IsFollowActive(s)) return "following";
        if (OwnCorpseTravelHoldsNav) return "travelling back to your corpse";
        if (clientBusyCounts && PacedBusy > 0) return "busy";
        return string.Empty;
    }

    /// <summary>"" when health, stamina and mana are over their lines (and no heal or recharge is wanted); else which.</summary>
    private string KitVitalsWhy(LegacyUiSettings s)
    {
        if (_buffManager?.WantsVitalRecharge == true) return "healing or restoring vitals";
        uint h = 0, mh = 0, st = 0, mst = 0, m = 0, mm = 0;
        try { if (!Host.HasGetPlayerVitals || !Host.TryGetPlayerVitals(out h, out mh, out st, out mst, out m, out mm)) return string.Empty; }
        catch { return string.Empty; }
        if (mh > 0)
        {
            int hp = (int)(h * 100UL / mh);
            int line = Math.Max(s.HealAt, Math.Max(s.EmergencyHealAt, s.StaminaToHealthAt));
            if (hp <= line) return $"health {hp}% (Heal At {line}%)";
        }
        if (mst > 0)
        {
            int sp = (int)(st * 100UL / mst);
            if (sp <= s.RestamAt) return $"stamina {sp}% (Re-stam At {s.RestamAt}%)";
        }
        if (mm > 0)
        {
            int mp = (int)(m * 100UL / mm);
            if (mp <= s.GetManaAt) return $"mana {mp}% (Get Mana At {s.GetManaAt}%)";
        }
        return string.Empty;
    }

    /// <summary>
    /// The nearest live monster: the combat scan (within Monster Range, a clear line) and, at most every
    /// half second unless <paramref name="force"/>, the world cache (any line, past Monster Range).
    /// PositiveInfinity when none is known.
    /// </summary>
    private double NearestMonsterYards(double within, long now, bool force)
    {
        double best = double.PositiveInfinity;
        var combat = _combatManager;
        if (combat != null)
        {
            var scanned = combat.ScannedTargets;
            for (int i = 0; i < scanned.Count; i++)
                if (scanned[i].Distance < best) best = scanned[i].Distance;
            if (best > within && combat.HasCloseThreat(within)) best = within;   // a predicted kill still alive
        }
        if (force || now - _kitMonsterWalkAt >= KitMonsterWalkMs)
        {
            _kitMonsterWalkAt = now;
            _kitMonsterWalkYd = WorldMonsterYards();
        }
        return Math.Min(best, _kitMonsterWalkYd);
    }

    /// <summary>The nearest live, fightable monster in the world cache (PositiveInfinity = none).</summary>
    private double WorldMonsterYards()
    {
        var cache = _objectCache;
        uint me = _playerId;
        if (cache == null || me == 0) return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        foreach (var wo in cache.GetLandscapeObjects())
        {
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            if (cache.GetHealthRatio(wo.Id) == 0f) continue;   // dead
            double d = cache.Distance(unchecked((int)me), wo.Id);
            if (!(d < best)) continue;
            if (!IsFightableCreature(unchecked((uint)wo.Id))) continue;
            best = d;
        }
        return best;
    }

    /// <summary>Health going down is a hit (for "no hit for 20 s"); the pose changing between ticks is moving.</summary>
    private void TrackKitHurtAndMove(long now)
    {
        try
        {
            if (Host.HasGetPlayerVitals && Host.TryGetPlayerVitals(out uint h, out _, out _, out _, out _, out _))
            {
                if (_kitLastHealth > 0 && h > 0 && h < _kitLastHealth) _kitHurtAt = now;
                _kitLastHealth = h;
            }
            if (Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _))
            {
                float dx = x - _kitPoseX, dy = y - _kitPoseY, dz = z - _kitPoseZ;
                _kitMoving = _kitPoseKnown && (cell != _kitPoseCell || dx * dx + dy * dy + dz * dz > KitMoveEpsilon * KitMoveEpsilon);
                _kitPoseKnown = true;
                _kitPoseCell = cell;
                _kitPoseX = x; _kitPoseY = y; _kitPoseZ = z;
            }
        }
        catch { }
    }

    /// <summary>Logout: this character's hit and pose notes go.</summary>
    private void ResetKitTracking()
    {
        _kitLastHealth = 0;
        _kitHurtAt = long.MinValue / 2;
        _kitPoseKnown = false;
        _kitMoving = false;
        _kitMonsterWalkAt = long.MinValue / 2;
        _kitMonsterWalkYd = double.PositiveInfinity;
    }
}
