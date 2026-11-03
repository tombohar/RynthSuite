using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// Boost Nav Priority, owner's corrected rule (2026-11-02, R-20261103-000159-a65e): "Boost nav
// priority should only run the nav route, not from mob to mob. So when no nav route is loaded it
// should fight." The flag counts only while a nav route runs (BoostNavRule.Active). Then RynthAi
// only follows the route: no hunting, no looting, no fighting back (the arbiter never picks Combat
// or Looting, CombatManager.OnHeartbeat stands down, the looter passes every corpse by through
// CorpseOpenController.BoostNavHoldsLoot, PetManager summons nothing). Buffing and vitals still run.
// With no route, a Once route at its end ("when a 'once' nav route is at the end it should hunt
// as well"), nav off, or the Outdoor Hunt walking from monster to monster, the flag does nothing: RynthAi hunts, fights and loots as with it off.
// The flag comes on quietly from NavLootPrio or a hunting meta's SetOpt, and a player who never
// knew it was on saw only that the bot would not loot (R-20261102-041824-3f7c). So the first fight
// or corpse it skips along a route says so, once per session.
public sealed partial class RynthAiPlugin
{
    internal const string BoostNavNotice =
        "[RynthAi] Boost Nav Priority is on: while a nav route runs, RynthAi only follows it (no hunting, no looting). Turn it off to hunt along the route.";

    /// <summary>How often the corpse half of the notice looks for a corpse it would have looted.</summary>
    internal const long BoostNavLootCheckMs = 2000;

    private bool _boostNavNoticeSaid;      // once per session (reset at logout)
    private long _boostNavLootCheckAt;     // CorpseNowMs of the last corpse look

    /// <summary>
    /// After the arbiter's decision, every tick: Boost Nav Priority, with a nav route running, is
    /// why a fight (combat on and a monster to engage) or a corpse (looting on and one in range it
    /// would loot) is skipped. Says so once per session, in chat and the log; nothing at all once
    /// said, and nothing while no route runs (then the flag skips nothing).
    /// </summary>
    private void NoteBoostNavSkip(LegacyUiSettings settings, bool wantCombat)
    {
        if (_boostNavNoticeSaid || !BoostNavRule.Active(settings)) return;

        string? skipped = null;
        if (wantCombat)
            skipped = "a fight";
        else if (settings.EnableLooting)
        {
            long now = CorpseNowMs;
            if (now - _boostNavLootCheckAt >= BoostNavLootCheckMs)
            {
                _boostNavLootCheckAt = now;
                if (BoostNavSkipsLoot(settings)) skipped = "a corpse";
            }
        }
        if (skipped == null) return;

        _boostNavNoticeSaid = true;
        Host.Log($"{BoostNavNotice} (skipped {skipped})");
        Host.WriteToChat(BoostNavNotice, 2);
    }
}

/// <summary>
/// The one test for Boost Nav Priority (owner's corrected rule, 2026-11-02, R-20261103-000159-a65e):
/// it applies only while a nav route runs. Read by the arbiter's input, CombatManager's stand-down,
/// PetManager, the looter and the notice, so they can never disagree.
/// </summary>
internal static class BoostNavRule
{
    /// <summary>Boost Nav Priority is on and a nav route runs: RynthAi only follows the route.</summary>
    public static bool Active(LegacyUiSettings s) => s.BoostNavPriority && NavRouteRuns(s);

    /// <summary>
    /// A nav route runs: the macro runs, navigation is on, and either a follow is steering (a
    /// Follow route or fellowship follow; VTank counts a follow as a nav route too) or a loaded
    /// route has points left to walk. Not a route: none loaded or an empty one, a Once route that
    /// has walked off its last point, and the Outdoor Hunt's walk to its next monster (it goes
    /// from monster to monster, which is hunting). The Dungeon Patrol's generated loop counts:
    /// it is walked waypoint by waypoint like a loaded route.
    /// </summary>
    public static bool NavRouteRuns(LegacyUiSettings s)
    {
        if (!s.IsMacroRunning || !s.EnableNavigation) return false;
        if (RynthAiPlugin.IsFollowActive(s)) return true;
        var route = s.CurrentRoute;
        if (route == null || route.Points.Count == 0) return false;
        // A hunt walk saved to a file (a nav path set) is a loaded route from then on.
        if (route.OutdoorHuntWalk && string.IsNullOrEmpty(s.CurrentNavPath)) return false;
        // Owner (2026-11-02): "When a 'once' nav route is at the end it should hunt as well." The nav
        // engine's CompleteOnceRoute leaves the index past the last point (and turns nav off); loading
        // the route again or /ra nav restart rewinds it, and Boost Nav applies again. Circular and
        // Linear routes never finish.
        return !s.NavRouteFinished();
    }
}
