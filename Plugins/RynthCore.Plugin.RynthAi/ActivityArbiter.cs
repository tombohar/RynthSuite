namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Centralized activity priority arbiter — replacement for the distributed
/// string-based BotAction state machine. See ACTIVITY_ARBITER_PLAN.md.
///
/// STEP 1 (shadow mode): this class only DECIDES and LOGS. It does not write
/// BotAction and does not run any subsystem. Its decision is logged next to
/// the legacy BotAction so we can validate the arbiter's choices against real
/// sessions before it controls anything. Zero behavior change.
///
/// Priority (user-confirmed 2026-05-15), low→high:
///   Idle < Navigating < Salvaging < Looting < Combat < Buffing
/// </summary>
internal enum BotActivity
{
    Idle = 0,
    Navigating = 1,
    Salvaging = 2,
    Looting = 3,
    Combat = 4,
    Buffing = 5,
}

/// <summary>
/// Immutable snapshot of the pure "do you want to run" signals, captured once
/// per tick by the caller. The arbiter is a pure function of this snapshot —
/// no side effects, no game commands, no state writes. That purity is the
/// whole point: the decision is recomputed from scratch every tick, so no
/// subsystem can wedge the bot by leaving a stale lock behind.
/// </summary>
internal readonly struct ArbiterInputs
{
    public readonly bool MacroRunning;
    public readonly bool WantBuffing;   // EnableBuffing && NeedsAnyBuff()
    public readonly bool WantCombat;    // EnableCombat && has target/scan
    public readonly bool WantLooting;   // open container or target corpse
    public readonly bool WantSalvaging; // salvage queue / busy
    public readonly bool WantNav;       // macro && navEnabled && route loaded

    public ArbiterInputs(bool macroRunning, bool wantBuffing, bool wantCombat,
                         bool wantLooting, bool wantSalvaging, bool wantNav)
    {
        MacroRunning  = macroRunning;
        WantBuffing   = wantBuffing;
        WantCombat    = wantCombat;
        WantLooting   = wantLooting;
        WantSalvaging = wantSalvaging;
        WantNav       = wantNav;
    }
}

internal sealed class ActivityArbiter
{
    private readonly System.Action<string> _log;
    private string _lastShadowKey = "";

    public ActivityArbiter(System.Action<string> log) => _log = log;

    /// <summary>
    /// Pure decision: highest-priority subsystem that wants to run.
    /// Returns Idle when the macro is stopped or nobody wants to run.
    /// </summary>
    public static BotActivity Decide(in ArbiterInputs s)
    {
        if (!s.MacroRunning) return BotActivity.Idle;
        if (s.WantBuffing)   return BotActivity.Buffing;
        if (s.WantCombat)    return BotActivity.Combat;
        if (s.WantLooting)   return BotActivity.Looting;
        if (s.WantSalvaging) return BotActivity.Salvaging;
        if (s.WantNav)       return BotActivity.Navigating;
        return BotActivity.Idle;
    }

    /// <summary>
    /// Map a BotActivity to the legacy BotAction string the rest of the
    /// codebase (and UI) still reads. Single source of truth for the mapping
    /// so when the arbiter goes authoritative there's exactly one writer.
    /// </summary>
    public static string ToBotAction(BotActivity a) => a switch
    {
        BotActivity.Buffing    => "Buffing",
        BotActivity.Combat     => "Combat",
        BotActivity.Looting    => "Looting",
        BotActivity.Salvaging  => "Salvaging",
        BotActivity.Navigating => "Navigating",
        _                      => "Default",
    };

    /// <summary>
    /// STEP 1 shadow mode. Compute the would-be decision and log it whenever
    /// it (or the legacy BotAction it's being compared against) changes.
    /// Caller still runs the legacy cascade; this only observes.
    /// </summary>
    public void ShadowObserve(in ArbiterInputs s, string legacyBotAction)
    {
        BotActivity decision = Decide(in s);
        string wouldBe = ToBotAction(decision);
        bool agrees = string.Equals(wouldBe, string.IsNullOrEmpty(legacyBotAction) ? "Default" : legacyBotAction,
                                    System.StringComparison.OrdinalIgnoreCase);

        string key = $"{decision} want[buff={s.WantBuffing} cbt={s.WantCombat} loot={s.WantLooting} salv={s.WantSalvaging} nav={s.WantNav}] legacy='{legacyBotAction}' agree={agrees}";
        if (key == _lastShadowKey) return;
        _lastShadowKey = key;
        _log($"Arbiter[shadow]: would={wouldBe} legacy='{legacyBotAction}' agree={agrees} | {key}");
    }

    /// <summary>The arbiter's most recent decision (authoritative as of Step 2).</summary>
    public BotActivity Current { get; private set; } = BotActivity.Idle;

    private const System.StringComparison OIC = System.StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// STEP 2: authoritative for the Combat ↔ Navigating ↔ Default transitions
    /// ONLY. Buffing / Looting / Salvaging strings are still written by their
    /// legacy managers (migrated in steps 3-4) — this method must not stomp
    /// them, or the not-yet-migrated subsystems lose coherence.
    ///
    /// Returns the decision so the caller can also use it (e.g. diagnostics).
    /// Sole writer of "Combat" / "Navigating" / "Default" — CombatManager's
    /// own BotAction writes were removed in the same step, so there is exactly
    /// one writer for those strings now. That single-writer property is what
    /// eliminates the stuck-lock "stands there" freeze.
    /// </summary>
    public BotActivity ApplyStep2(in ArbiterInputs s, LegacyUi.LegacyUiSettings settings)
    {
        BotActivity decision = Decide(in s);
        Current = decision;

        string legacy = settings.BotAction ?? "Default";

        // While legacy still owns these strings (steps 3-4 migrate them),
        // never overwrite — even if our priority would preempt. The reported
        // bug is the Combat↔Nav boundary; Combat-vs-Loot/Salv handoff is a
        // later step. Buffing is highest priority anyway so staying out is
        // also correct.
        bool legacyOwnsString =
            legacy.Equals("Buffing",   OIC) ||
            legacy.Equals("Looting",   OIC) ||
            legacy.Equals("Salvaging", OIC);

        string? desired = decision switch
        {
            BotActivity.Combat     => "Combat",
            BotActivity.Navigating => "Navigating",
            BotActivity.Idle       => "Default",
            _                      => null, // Buffing/Looting/Salvaging → legacy owns it
        };

        if (desired == null || legacyOwnsString)
        {
            // Arbiter defers to legacy for this string this tick. Still log
            // decision transitions so we can see the arbiter's intent.
            LogDecisionIfChanged(decision, s, legacy, wrote: false);
            return decision;
        }

        if (!string.Equals(legacy, desired, OIC))
        {
            settings.BotAction = desired;
            LogDecisionIfChanged(decision, s, desired, wrote: true);
        }
        else
        {
            LogDecisionIfChanged(decision, s, legacy, wrote: false);
        }
        return decision;
    }

    private string _lastDecisionKey = "";
    private void LogDecisionIfChanged(BotActivity decision, in ArbiterInputs s, string botAction, bool wrote)
    {
        string key = $"{decision} wrote={wrote} ba='{botAction}' want[buff={s.WantBuffing} cbt={s.WantCombat} loot={s.WantLooting} salv={s.WantSalvaging} nav={s.WantNav}]";
        if (key == _lastDecisionKey) return;
        _lastDecisionKey = key;
        _log($"Arbiter[step2]: {key}");
    }
}
