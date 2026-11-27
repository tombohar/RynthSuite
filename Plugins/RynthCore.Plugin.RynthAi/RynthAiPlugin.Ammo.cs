using System;
using System.Globalization;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

// Make ammo when low (a players' request, 2026-11-23: "set an amount of arrows and if we fall below it
// does it between combat"; MakeAmmoWhenLow, off by default; /ra ammo ...). The empty-slot path
// (MissileCraftingManager) waited for the quiver to run dry, which is mid-fight, and its first combine
// needs Peace mode. This tops the ammo up between fights instead, on the Make kits when low rules and
// their checks (RynthAiPlugin.Kits.cs: KitBusyWhy, KitVitalsWhy, NearestMonsterYards, the hit and pose
// tracking, the 35 yd / Monster Range radius), with a KitAuto of its own (MissileCraftingManager.Auto):
//   - with the macro on, fewer than MakeAmmoBelow arrows, quarrels or darts for the launcher in hand
//     (quiver and pack), nothing else going (buffing or healing, a fight, looting, salvaging, a vendor,
//     a portal, a launcher swap, Boost Loot's wait for a kill's corpse, a kit run), vitals over their
//     lines, no monster within the radius, no hit for 20 s: peace mode (given up after 6 s), stand
//     still a second, then MissileCraftingManager.StartTopOff makes up to MakeAmmoUpTo;
//   - a monster within the radius, a hit, a heal line, buffing, a fight, a launcher swap, moving or
//     leaving peace mode stops it at once (CheckAutoAmmoDanger, ahead of every hold); 60 s before the
//     next try after a stop for danger, 10 min after one that made nothing or the player's Stop;
//   - while it gets ready or goes, the rest of the tick waits (TickAutoAmmo, after the kits' hold).
// Mid-fight it never asks for Peace mode: the empty-slot path is unchanged and still the only one
// that crafts in a fight (one combine, with nothing at all to shoot).
// Make ammo now (/ra ammo make, the Mini Remote's button): the same, up to MakeAmmoUpTo, with the
// macro on or off, waiting up to a minute for a safe moment.
public sealed partial class RynthAiPlugin
{
    internal const string AmmoUsage = "Usage: /ra ammo [status | make | stop | show | auto [on|off|status|below N|upto M]]";
    internal const string AmmoAutoUsage = "Usage: /ra ammo auto [status | on | off | below <1-5000> | upto <1-10000>]";
    /// <summary>Make ammo now waits this long for a safe moment, then gives up (said in chat).</summary>
    internal const long AmmoMakeNowWaitMs = 60_000;
    /// <summary>Moving further than this from where a top-off started stops it (as a kit run).</summary>
    private const float AmmoMoveLimit = 1.5f;

    private bool _ammoAutoWasOn;
    private long _ammoRunStartedAt = long.MinValue / 2;
    private bool _ammoStartPoseKnown;
    private uint _ammoStartCell;
    private float _ammoStartX, _ammoStartY, _ammoStartZ;

    private static int AmmoBelow(LegacyUiSettings s) => Math.Clamp(s.MakeAmmoBelow, 1, MissileCraftingManager.MaxAmmoBelow);
    /// <summary>The top-off's target: Make up to, never under the start line.</summary>
    private static int AmmoUpTo(LegacyUiSettings s) => Math.Max(Math.Clamp(s.MakeAmmoUpTo, 1, MissileCraftingManager.MaxAmmoUpTo), AmmoBelow(s));

    private void AmmoChat(string line)
    {
        try { if (Host.HasWriteToChat) Host.WriteToChat(line, 1); } catch { }
    }

    /// <summary>Login: the top-off's lines, clock and the panels' view of it.</summary>
    private void WireAmmoTopOff(MissileCraftingManager cm)
    {
        cm.Say = AmmoChat;
        cm.Auto.Say = AmmoChat;
        cm.Auto.NowMs = KitNow;
        _ammoAutoWasOn = false;
    }

    // ── /ra ammo ────────────────────────────────────────────────────────────

    /// <summary>/ra ammo: status (the default), make, stop, show (the Ammo tab), auto ....</summary>
    private void HandleAmmoCommand(string[] parts)
    {
        string verb = parts.Length > 2 ? parts[2].Trim().ToLowerInvariant() : "status";
        switch (verb)
        {
            case "auto":
                HandleAmmoAutoCommand(parts);
                return;
            case "show":
            {
                var remote = _miniRemote;
                if (remote == null) { ReplyFail("[RynthAi] Make ammo: the Mini Remote isn't available yet (log in first)."); return; }
                remote.Crafting.SelectAmmoTab();
                string? reply = remote.MiniRemoteTabCommand?.Invoke(MiniRemoteTabs.Crafting, "show");
                if (reply == null) ReplyFail("[RynthAi] Make ammo: the Mini Remote isn't available yet.");
                else ReplyOk("[RynthAi] Make ammo: " + reply);
                return;
            }
            case "status":
            case "make":
            case "stop":
                break;
            default:
                ReplyFail($"[RynthAi] Make ammo: unknown option '{parts[2]}'. {AmmoUsage}");
                return;
        }

        var s = _dashboard?.Settings;
        var cm = _missileCraftingManager;
        if (s == null || cm == null) { ReplyFail("[RynthAi] Make ammo: not ready yet (log in first)."); return; }
        switch (verb)
        {
            case "status":
                ChatLine("[RynthAi] Make ammo: " + AmmoLookLine(cm.LookTopOff(forceRefresh: true)));
                ChatLine(AmmoAutoStatusLine());
                break;
            case "make":
            {
                string line = AskMakeAmmoNow(s, cm, out bool ok);
                if (ok) ReplyOk(line); else ReplyFail(line);
                break;
            }
            case "stop":
                if (cm.TopOffRunning) { cm.StopTopOff("stopped by you", KitStopKind.User); Reply.Answered(); }   // the summary line answers
                else if (cm.MakeNowUntilMs > 0) { cm.MakeNowUntilMs = 0; ReplyOk("[RynthAi] Make ammo now: cancelled."); }
                else ReplyOk("[RynthAi] Make ammo: nothing is running.");
                break;
        }
    }

    /// <summary>What the pack holds and makes for the launcher in hand, one line.</summary>
    private static string AmmoLookLine(MissileCraftingManager.AmmoLook look)
    {
        if (!look.Launcher) return look.PackWhy + ".";
        string head = $"{look.CarriedText} carried for the launcher in hand (quiver and pack)";
        if (look.Recipe == null || look.Combines <= 0) return $"{head}; none can be made now: {look.PackWhy}.";
        int yield = AmmoRecipes.EstimatedYield(look.Recipe);
        string combines = look.Combines >= 1000 ? "many combines" : look.Combines == 1 ? "1 combine" : $"{look.Combines} combines";
        return $"{head}; the pack makes {look.Recipe.OutputName}, about {yield} a combine ({combines} of bundles).";
    }

    /// <summary>Make ammo now: checks, then asks the next safe moment for a top-off up to Make up to.</summary>
    private string AskMakeAmmoNow(LegacyUiSettings s, MissileCraftingManager cm, out bool ok)
    {
        ok = false;
        if (!s.EnableMissileCrafting) return "[RynthAi] Make ammo now: Enable Missile Crafting is off (Settings > Combat).";
        if (cm.TopOffRunning) { ok = true; return $"[RynthAi] Make ammo now: already making ({cm.TopOffMade}/{cm.TopOffPlanned} combines). /ra ammo stop to stop."; }
        if (cm.IsCrafting) return "[RynthAi] Make ammo now: ammo crafting is already under way (an empty quiver).";
        var look = cm.LookTopOff(forceRefresh: true);
        if (!look.Launcher) return $"[RynthAi] Make ammo now: {look.PackWhy}.";
        if (look.Recipe == null || look.Combines <= 0) return $"[RynthAi] Make ammo now: {look.CarriedText} carried, but none can be made now: {look.PackWhy}.";
        int target = AmmoUpTo(s);
        if (look.Carried >= target)
        {
            ok = true;
            return $"[RynthAi] Make ammo now: {look.CarriedText} carried, already at Make up to ({target}): nothing to make.";
        }
        cm.MakeNowUntilMs = KitNow() + AmmoMakeNowWaitMs;
        cm.Auto.LookNow();
        ok = true;
        return $"[RynthAi] Make ammo now: making {look.Recipe.OutputName} up to {target} as soon as it is safe (no monster within "
               + $"{KitSafeRadiusYd(s):0} yd, no fight, in peace mode); waits up to {AmmoMakeNowWaitMs / 1000} s. /ra ammo stop to stop.";
    }

    // ── /ra ammo auto ───────────────────────────────────────────────────────

    /// <summary>/ra ammo auto [status | on | off | below N | upto M]: Make ammo when low (a profile setting).</summary>
    private void HandleAmmoAutoCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null) { ReplyFail("[RynthAi] Make ammo when low: not ready yet (log in first)."); return; }
        string verb = parts.Length > 3 ? parts[3].Trim().ToLowerInvariant() : "status";
        var cm = _missileCraftingManager;
        switch (verb)
        {
            case "status":
                ChatLine(AmmoAutoStatusLine());   // the status asked for
                break;
            case "on":
            case "off":
            {
                bool on = verb == "on";
                settings.MakeAmmoWhenLow = on;
                _dashboard?.SaveSettings();
                if (cm != null)
                {
                    _ammoAutoWasOn = on;
                    if (on) cm.Auto.LookNow();
                    else
                    {
                        if (cm.TopOffRunning && !cm.TopOffManual) cm.StopTopOff("Make ammo when low turned off", KitStopKind.Other);
                        cm.Auto.Off();
                    }
                }
                string needs = on && !settings.EnableMissileCrafting ? "; it needs Enable Missile Crafting on (Settings > Combat)."
                    : on && !settings.IsMacroRunning ? "; it works while the macro runs."
                    : ".";
                ReplyOk($"[RynthAi] Make ammo when low: {verb} (under {AmmoBelow(settings)}, make up to {AmmoUpTo(settings)}){needs}");
                break;
            }
            case "below":
            case "upto":
            {
                int max = verb == "below" ? MissileCraftingManager.MaxAmmoBelow : MissileCraftingManager.MaxAmmoUpTo;
                if (parts.Length < 5)
                {
                    ReplyOk($"[RynthAi] Make ammo when low: {(verb == "below" ? $"starts under {AmmoBelow(settings)}" : $"makes up to {AmmoUpTo(settings)} a run")}. {AmmoAutoUsage}");
                    break;
                }
                if (!int.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > max)
                {
                    ReplyFail($"[RynthAi] Make ammo when low: {verb} must be a number from 1 to {max} (got '{parts[4]}'). {AmmoAutoUsage}");
                    break;
                }
                if (verb == "below") settings.MakeAmmoBelow = n; else settings.MakeAmmoUpTo = n;
                _dashboard?.SaveSettings();
                cm?.Auto.LookNow();
                string off = settings.MakeAmmoWhenLow ? "." : " (it is off: /ra ammo auto on).";
                ReplyOk(verb == "below"
                    ? $"[RynthAi] Make ammo when low: starts when you carry fewer than {n}{off}"
                    : $"[RynthAi] Make ammo when low: makes up to {AmmoUpTo(settings)} a run{(AmmoUpTo(settings) > n ? $" (never under the start line, {AmmoBelow(settings)})" : "")}{off}");
                break;
            }
            default:
                ReplyFail($"[RynthAi] Make ammo when low: unknown option '{parts[3]}'. {AmmoAutoUsage}");
                break;
        }
    }

    /// <summary>One line: the setting, its numbers, the radius and what it is doing.</summary>
    private string AmmoAutoStatusLine()
    {
        var s = _dashboard?.Settings;
        if (s == null) return "[RynthAi] Make ammo when low: not ready yet (log in first).";
        return $"[RynthAi] Make ammo when low: {(s.MakeAmmoWhenLow ? "on" : "off")}, under {AmmoBelow(s)} make up to {AmmoUpTo(s)}, "
               + $"no monster within {KitSafeRadiusYd(s):0} yd; {AmmoAutoStatusText(s)}. Mini Remote > Crafting > Ammo, /ra ammo auto on|off.";
    }

    /// <summary>What Make ammo when low is doing ("running 2/5 combines", "waiting: monster 22 yd away; next check in 4 s", ...).</summary>
    internal string AmmoAutoStatusText(LegacyUiSettings s)
    {
        var cm = _missileCraftingManager;
        if (cm == null) return "not ready (log in first)";
        if (cm.TopOffRunning) return $"running {cm.TopOffMade}/{cm.TopOffPlanned} combines" + (cm.TopOffManual ? " (Make ammo now)" : "");
        if (cm.MakeNowUntilMs > 0) return "Make ammo now: " + cm.Auto.Status;
        if (!s.MakeAmmoWhenLow) return "off";
        if (!s.EnableMissileCrafting) return "waiting: Enable Missile Crafting is off";
        if (!s.IsMacroRunning) return "waiting: the macro is off";
        return cm.Auto.Status;
    }

    // ── The macro side ──────────────────────────────────────────────────────

    /// <summary>
    /// Every in-world tick, right after CheckAutoKitsDanger (which keeps the hit and pose notes): stops
    /// a top-off at once on a monster within the radius, buffing or a heal, a fight, a portal, a vendor,
    /// a launcher swap, a hit since it started, or moving away from where it started.
    /// </summary>
    private void CheckAutoAmmoDanger(LegacyUiSettings s)
    {
        var cm = _missileCraftingManager;
        if (cm == null || !cm.TopOffRunning) return;
        long now = KitNow();
        var f = new KitAutoFacts
        {
            Enabled = true,
            MacroOn = true,
            BusyWhy = AmmoBusyWhy(s, starting: false, readying: false),
            VitalsWhy = KitVitalsWhy(s),
            RadiusYd = KitSafeRadiusYd(s),
        };
        if (f.BusyWhy.Length == 0 && f.VitalsWhy.Length == 0) f.NearestMonsterYd = NearestMonsterYards(f.RadiusYd, now, force: false);
        string why = KitAuto.StopReason(f);
        if (why.Length == 0 && _kitHurtAt >= _ammoRunStartedAt) why = "you were hit";
        if (why.Length == 0 && AmmoMovedSinceStart()) why = "you moved";
        if (why.Length > 0) cm.StopTopOff(why, KitStopKind.Danger);
    }

    /// <summary>
    /// Make ammo when low, after the kits' hold: true while a top-off gets ready or goes, and the rest
    /// of the tick (crafting's empty-slot path, combat, looting, navigation, item chores) waits.
    /// Also takes the panels' Make ammo now and Stop.
    /// </summary>
    private bool TickAutoAmmo(LegacyUiSettings s)
    {
        var cm = _missileCraftingManager;
        if (cm == null) return false;
        var auto = cm.Auto;
        long now = KitNow();

        if (cm.TakeStopRequest())
        {
            if (cm.TopOffRunning) cm.StopTopOff("stopped by you", KitStopKind.User);
            else if (cm.MakeNowUntilMs > 0) { cm.MakeNowUntilMs = 0; AmmoChat("[RynthAi] Make ammo now: cancelled."); }
        }
        if (cm.TakeMakeNowRequest()) AmmoChat(AskMakeAmmoNow(s, cm, out _));

        // The setting changed (a panel, a profile switch, a meta): look now, or let go.
        bool on = s.MakeAmmoWhenLow;
        if (on != _ammoAutoWasOn)
        {
            _ammoAutoWasOn = on;
            if (on) auto.LookNow();
            else
            {
                if (cm.TopOffRunning && !cm.TopOffManual) cm.StopTopOff("Make ammo when low turned off", KitStopKind.Other);
                auto.Off();
            }
        }

        if (cm.TopOffRunning)
        {
            if (!cm.TopOffManual && !s.IsMacroRunning) { cm.StopTopOff("the macro was turned off", KitStopKind.MacroOff); return false; }
            auto.Running(cm.TopOffMade, cm.TopOffPlanned);
            cm.ProcessCrafting();
            return cm.IsCrafting;
        }

        bool manual = cm.MakeNowUntilMs > 0;
        if (manual && now >= cm.MakeNowUntilMs)
        {
            cm.MakeNowUntilMs = 0;
            manual = false;
            AmmoChat($"[RynthAi] Make ammo now: gave up after {AmmoMakeNowWaitMs / 1000} s ({auto.Status}).");
            if (!on) auto.Off();
        }
        if (!manual && !on) { if (auto.Status != "off") auto.Off(); return false; }
        if (cm.IsCrafting) return false;   // the empty-slot path is going: it holds the tick itself

        KitAutoStep step = auto.Decide(() => AmmoStartFacts(s, cm, now, auto.Readying, manual));
        switch (step)
        {
            case KitAutoStep.GoPeace:
                Host.ChangeCombatModeFor(CombatMode.NonCombat, "Crafting", "peace mode for Make ammo when low");
                return true;
            case KitAutoStep.Hold:
                return true;
            case KitAutoStep.Start:
            {
                string why = manual ? "Make ammo now" : $"under {AmmoBelow(s)}";
                string refused = cm.StartTopOff(AmmoUpTo(s), manual, why, (kind, made) => auto.RunEnded(kind, made));
                if (refused.Length > 0)
                {
                    auto.StartRefused(refused);
                    if (manual) { cm.MakeNowUntilMs = 0; AmmoChat($"[RynthAi] Make ammo now: can't start - {refused}."); }
                    return false;
                }
                cm.MakeNowUntilMs = 0;
                _ammoRunStartedAt = now;
                _ammoStartPoseKnown = Host.TryGetPlayerPose(out _ammoStartCell, out _ammoStartX, out _ammoStartY, out _ammoStartZ, out _, out _, out _, out _);
                auto.Running(0, cm.TopOffPlanned);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>One full look for a start (KitAuto reads it only when a look is due, every tick while getting ready).</summary>
    private KitAutoFacts AmmoStartFacts(LegacyUiSettings s, MissileCraftingManager cm, long now, bool readying, bool manual)
    {
        var f = new KitAutoFacts
        {
            Enabled = true,
            MacroOn = s.IsMacroRunning || manual,
            Below = manual ? AmmoUpTo(s) : AmmoBelow(s),
            BelowMax = manual ? MissileCraftingManager.MaxAmmoUpTo : MissileCraftingManager.MaxAmmoBelow,
            RadiusYd = KitSafeRadiusYd(s),
        };
        if (!s.EnableMissileCrafting) { f.IdleWhy = "Enable Missile Crafting is off"; return f; }
        if (!f.MacroOn) return f;
        // The scan as it is (under a second old, else walked again): the empty-slot check already forces
        // a walk every 5 s, and StartTopOff walks the packs once more before the first combine.
        var look = cm.LookTopOff(forceRefresh: false);
        var category = look.Category;
        f.CountText = n => MissileCraftingManager.AmmoText(n, category);
        if (!look.Launcher) { f.IdleWhy = look.PackWhy; return f; }
        f.Kits = look.Carried;
        if (f.Kits >= Math.Clamp(f.Below, 1, f.BelowMax)) return f;
        f.BusyWhy = AmmoBusyWhy(s, starting: true, readying: readying);
        f.VitalsWhy = KitVitalsWhy(s);
        f.NearestMonsterYd = NearestMonsterYards(f.RadiusYd, now, force: false);
        f.SinceHurtMs = _kitHurtAt <= long.MinValue / 4 ? long.MaxValue : Math.Max(0, now - _kitHurtAt);
        f.PackWhy = look.PackWhy;
        try { f.InPeace = !Host.HasGetCurrentCombatMode || Host.GetCurrentCombatMode() == CombatMode.NonCombat; }
        catch { f.InPeace = false; }
        f.Moving = _kitMoving;
        return f;
    }

    /// <summary>
    /// Kits' "what else has the character" (KitBusyWhy), plus a launcher swap; for a start also Boost
    /// Loot's wait for a kill's corpse and a kit run getting ready or going.
    /// </summary>
    private string AmmoBusyWhy(LegacyUiSettings s, bool starting, bool readying)
    {
        string why = KitBusyWhy(s, starting, clientBusyCounts: !readying);
        if (why.Length > 0) return why;
        if (_combatManager?.AmmoSwapInProgress == true) return "a launcher swap";
        if (!starting) return string.Empty;
        if (s.BoostLootPriority && BoostLootAwaitsKillCorpse(s)) return "a kill's corpse to loot first (Boost Loot Priority)";
        var tab = _miniRemote?.Crafting;
        if (tab != null && (tab.Kits.Running || tab.AutoKits.Readying)) return "making kits";
        return string.Empty;
    }

    /// <summary>Further than 1.5 m from where the top-off started (another cell's landblock counts as moved).</summary>
    private bool AmmoMovedSinceStart()
    {
        if (!_ammoStartPoseKnown) return false;
        try
        {
            if (!Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _)) return false;
            if ((cell >> 16) != (_ammoStartCell >> 16)) return true;
            float dx = x - _ammoStartX, dy = y - _ammoStartY, dz = z - _ammoStartZ;
            return dx * dx + dy * dy + dz * dz > AmmoMoveLimit * AmmoMoveLimit;
        }
        catch { return false; }
    }

    /// <summary>Logout: this character's top-off notes go (the crafting manager goes with it).</summary>
    private void ResetAmmoTracking()
    {
        _ammoAutoWasOn = false;
        _ammoRunStartedAt = long.MinValue / 2;
        _ammoStartPoseKnown = false;
    }
}
