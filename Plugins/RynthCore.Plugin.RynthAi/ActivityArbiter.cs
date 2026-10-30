namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Centralized activity priority arbiter — replacement for the distributed
/// string-based BotAction state machine. See ACTIVITY_ARBITER_PLAN.md.
///
/// Migration state (2026-09-04): STEP 5 of 5 — COMPLETE. Apply() is the SOLE
/// writer of every BotAction string (Buffing / Combat / Looting / Salvaging /
/// Navigating / Default). No manager writes it any more; the OnTick buff and
/// salvage gap-fills and the four hand-managed nav pause flags are gone. The
/// string is now a display projection of <see cref="Current"/>, and Current is
/// what gates the OnTick body.
///
/// ShadowObserve() is the step-1 observation path, kept for re-validating a
/// predicate change against a live session without giving it control.
///
/// Priority (user-confirmed 2026-05-15), low→high:
///   Idle &lt; Navigating &lt; Salvaging &lt; Looting &lt; Combat &lt; Buffing
///
/// The two Boost* user settings invert part of that order. They used to be
/// implemented by stomping the string from the OnTick cascade ("if boosting nav
/// and the action is Combat/Looting, force it back to Default"), which is
/// exactly the multi-writer pattern this class exists to kill. They are inputs
/// to Decide() now — see the override block there.
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
    public readonly bool WantBuffing;   // pending cast, vital recharge, or EnableBuffing && NeedsAnyBuff()
    public readonly bool WantCombat;    // EnableCombat && has engageable target
    public readonly bool WantLooting;   // open container, claimed corpse, unlooted corpse in range, or loot grace
    public readonly bool WantSalvaging; // salvage queue / busy
    public readonly bool WantNav;       // macro && navEnabled && route loaded

    /// <summary>Actively engaged (a live attack target), not merely "something is engageable".</summary>
    public readonly bool CombatEngaged;
    /// <summary>BoostNavPriority — navigation preempts combat and looting.</summary>
    public readonly bool BoostNav;
    /// <summary>BoostLootPriority — looting preempts combat.</summary>
    public readonly bool BoostLoot;
    /// <summary>
    /// Fellowship-follow is steering. A display sub-state of Navigating, not a
    /// priority tier of its own: it changes the projected string to "Following"
    /// but never changes who wins the tick.
    /// </summary>
    public readonly bool FollowActive;
    /// <summary>
    /// A lootable corpse in range has waited LootStarveMs while combat kept the tick, and no
    /// monster is close (the caller checks both). Looting gets the tick: on a busy spawn there
    /// is always a monster inside MonsterRange, so "combat first" meant corpses were never
    /// looted until the area emptied (2026-10-03).
    /// </summary>
    public readonly bool LootStarved;

    public ArbiterInputs(bool macroRunning, bool wantBuffing, bool wantCombat,
                         bool wantLooting, bool wantSalvaging, bool wantNav,
                         bool combatEngaged = false, bool boostNav = false, bool boostLoot = false,
                         bool followActive = false, bool lootStarved = false)
    {
        MacroRunning  = macroRunning;
        WantBuffing   = wantBuffing;
        WantCombat    = wantCombat;
        WantLooting   = wantLooting;
        WantSalvaging = wantSalvaging;
        WantNav       = wantNav;
        CombatEngaged = combatEngaged;
        BoostNav      = boostNav;
        BoostLoot     = boostLoot;
        FollowActive  = followActive;
        LootStarved   = lootStarved;
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

        // Buffing is unconditionally top priority — buffs are survival, and a
        // cast awaiting server confirmation must not be preempted mid-flight.
        // No Boost* flag outranks it.
        if (s.WantBuffing) return BotActivity.Buffing;

        // ── User priority overrides ────────────────────────────────────────
        // BoostNavPriority: nav outranks combat and looting. Only meaningful
        // when there is actually a route to run, hence the WantNav guard —
        // without it the flag just starved combat and the bot stood among mobs
        // without hunting.
        if (s.BoostNav && s.WantNav) return BotActivity.Navigating;

        // BoostLootPriority: looting outranks combat — EXCEPT while actively
        // engaged. A mob already swinging at you gets fought whatever the flag
        // says; otherwise "walk to the corpse" starves combat and the bot takes
        // hits with zero attack ticks. That is the 2026-06-03 audit's P1#1,
        // which was a hatch bolted onto the OnTick cascade and becomes this one
        // clause instead.
        if (s.BoostLoot && s.WantLooting && !s.CombatEngaged) return BotActivity.Looting;

        // Loot starvation: a corpse waited too long behind combat and nothing is close.
        if (s.LootStarved && s.WantLooting) return BotActivity.Looting;

        // ── Default order ─────────────────────────────────────────────────
        if (s.WantCombat)    return BotActivity.Combat;
        if (s.WantLooting)   return BotActivity.Looting;
        if (s.WantSalvaging) return BotActivity.Salvaging;
        if (s.WantNav)       return BotActivity.Navigating;
        return BotActivity.Idle;
    }

    /// <summary>
    /// Map a BotActivity to the legacy BotAction string the rest of the
    /// codebase (and UI) still reads. Single source of truth for the mapping
    /// so there is exactly one writer.
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
    /// What a display shows for the bot's activity (the RynthAi dashboard and the phone, through
    /// the snapshot's "botAction"; RynthNet's status "action"). The same as the BotAction control
    /// string except for "Buffing", which is one slot for every vitals and buff job: there it is
    /// <paramref name="buffingLabel"/> (Healing, Restoring mana, Restoring stamina or Buffing).
    /// Healing showed as "Buffing" on both (2026-10-05). The control string itself never changes:
    /// CombatManager's canRun and CorpseOpenController compare against "Buffing".
    /// </summary>
    public static string DisplayLabel(string? botAction, string? buffingLabel)
    {
        string action = string.IsNullOrEmpty(botAction) ? "Default" : botAction;
        if (string.Equals(action, "Buffing", OIC) && !string.IsNullOrEmpty(buffingLabel))
            return buffingLabel;
        return action;
    }

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

        string key = $"{decision} want[buff={s.WantBuffing} cbt={s.WantCombat} loot={s.WantLooting} salv={s.WantSalvaging} nav={s.WantNav}] legacy={legacyBotAction} agree={agrees}";
        if (key == _lastShadowKey) return;
        _lastShadowKey = key;
        _log($"Arbiter[shadow]: would={wouldBe} legacy={legacyBotAction} agree={agrees} | {key}");
    }

    /// <summary>The arbiter's most recent decision. Authoritative as of Step 5.</summary>
    public BotActivity Current { get; private set; } = BotActivity.Idle;

    private const System.StringComparison OIC = System.StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Sole writer of BotAction, for every activity. Being the single writer is
    /// what eliminates the stuck-lock "bot just stands there" freeze: the
    /// decision is recomputed from the pure inputs every tick, so no manager can
    /// strand a lock it forgot to release, and there is no ordering in which two
    /// writers can disagree.
    ///
    /// Returns the decision so the caller can gate the tick body on it — the
    /// typed value is the authority; the string exists for the UI, the Meta
    /// expression engine, and CombatManager's canRun read.
    /// </summary>
    public BotActivity Apply(in ArbiterInputs s, LegacyUi.LegacyUiSettings settings)
    {
        BotActivity decision = Decide(in s);
        Current = decision;

        string legacy = settings.BotAction ?? "Default";

        // "Following" is Navigating's display variant, not a priority tier —
        // NavigationEngine used to write it itself (and self-promote "Default"
        // to "Navigating"), which was the last violation of the single-writer
        // invariant this class's own comments claimed to hold.
        string desired = decision == BotActivity.Navigating && s.FollowActive
            ? "Following"
            : ToBotAction(decision);

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
        string key = $"{decision} wrote={wrote} ba={botAction} want[buff={s.WantBuffing} cbt={s.WantCombat} loot={s.WantLooting} salv={s.WantSalvaging} nav={s.WantNav}]"
                   + $" eng={s.CombatEngaged} boost[nav={s.BoostNav} loot={s.BoostLoot}]"
                   + (s.LootStarved ? " lootStarved" : "");
        if (key == _lastDecisionKey) return;
        _lastDecisionKey = key;
        _log($"Arbiter[step5]: {key}");
    }
}
