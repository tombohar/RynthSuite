# CombatManager Review + Combat/Loot/Nav Priority Architecture

**Reviewer:** Claude (Opus 4.7)
**Date:** May 2026
**Scope:** `CombatManager.cs` (1,703 LOC), `RynthAiPlugin.OnTick` (~240 LOC), `WorldObjectCache.cs`, supporting Compatibility hooks for the login-bug investigation.
**Three things you asked about:**
1. What can we do to make `CombatManager` better?
2. How can we prioritize combat / loot / navigation better?
3. The login-bug where monsters are ignored for a little while.

---

## Implementation status (updated 2026-05-06)

**Shipped this session:**
- ✅ §1 login bug — `ObjectIsAttackable` weenie-null returns `true` + diagnostic counter
- ✅ §1 follow-up — hot-reload monster discovery via `CObjectMaintHooks` walking AC's `weenie_object_table`
- ✅ §2.2c — `DropTarget(reason)` extracted; final straggler in `TrackAttackAttempt` converted
- ✅ §2.2e — live `CurrentCombatMode` everywhere (CombatManager already had it; extended to BuffManager + MissileCraftingManager)
- ✅ §2.2b — blacklist dedup (collapsed to dead-code removal — old impl was never populated)
- ✅ §4.4 — monster-rule priority wired into `ScoreCandidate` with formula tuned to user intent

**Still open:** §2.2d (Think pipeline refactor), §2.2f (settings for tuning constants), §2.2g (FacingController), §4.6/§4.7 (renaming + counter resets), §3 (the big scheduler refactor).

See §5 for the full prioritized list with statuses.

---

I'll start with the bug because I found the root cause, and it's not raycast.

---

## 1. The "monsters ignored after login" bug — actual root cause

### 1.1 What I expected to find vs what's actually happening

You hypothesized raycast. Looking at `ScanNearbyTargets` (`CombatManager.cs:325`):

```csharp
if (_settings.EnableRaycasting && RaycastInitialized && _raycastSystem != null)
{
    RaycastCheckCount++;
    if (_raycastSystem.IsTargetBlocked(_host, (uint)wo.Id, attackType))
    {
        RaycastBlockCount++;
        losBlocked = true;
    }
}
```

When raycast isn't initialized, the entire LOS check is **skipped** (`losBlocked` stays `false`). That means more monsters get accepted, not fewer. So raycast is not what's filtering them out.

### 1.2 Where they're actually being filtered

Two lines up, in the same scanner loop:

```csharp
if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id))
{
    exNotAttackable++;
    if (excludedSamples.Count < 3) excludedSamples.Add($"'{wo.Name}'(!atk @ {dist:F1}yd)");
    continue;
}
```

Now follow `ObjectIsAttackable` into the engine. `RynthCoreHost.ObjectIsAttackable` calls the function pointer; the function pointer points at `ClientObjectHooks.ObjectIsAttackable` (`ClientObjectHooks.cs:1327`):

```csharp
public static bool ObjectIsAttackable(uint objectId)
{
    if (_getCombatSystem == null || _objectIsAttackable == null || _getWeenieObject == null)
    {
        if (!Probe() || ...) return true;       // ← return true: probe failed
    }
    try
    {
        IntPtr weeniePtr = _getWeenieObject(objectId);
        if (weeniePtr == IntPtr.Zero)
            return false;                        // ← return FALSE: weenie not yet in client table

        IntPtr combatSystem = _getCombatSystem();
        if (combatSystem == IntPtr.Zero)
            return true;                        // ← return true: combat system not ready
        return _objectIsAttackable(combatSystem, objectId) != 0;
    }
    catch
    {
        return true;                            // ← return true: AV during call
    }
}
```

**Three of the four "I don't know" paths return `true` (default-attackable). One returns `false`.** The `false` path is exactly the post-login window: WorldObjectCache has classified an object as a creature (via `OnUpdateHealth`, which fires from network packets independently of the client's GUID-to-weenie table), but the client's `GetWeenieObject` table doesn't have an entry for that ID yet because the C++ client hasn't finished walking through its create-object queue.

The window is brief — typically a few hundred ms — but combat scans every 50ms and the diagnostic log will say `monsters=N accepted=0 ... !atk=N` for that whole window. From the user's perspective, "the bot stares at a monster for 3 seconds before engaging."

### 1.3 The fix

> **✅ Implemented (2026-05-06).** `ClientObjectHooks.ObjectIsAttackable` now returns `true` on weenie-null (`src/RynthCore.Engine/Compatibility/ClientObjectHooks.cs:1341-1349`), with a 20-line-throttled `_weenieNullCount` diagnostic per §1.4. The post-login window where monsters were filtered as not-attackable is closed.

The simplest correct fix is to change line 1341 to `return true`:

```csharp
IntPtr weeniePtr = _getWeenieObject(objectId);
if (weeniePtr == IntPtr.Zero)
{
    // Weenie not in client object table yet (post-login burst / fresh spawn).
    // The combat-system check above also defaults to "attackable" when the
    // system pointer is null; staying consistent here is what the scanner
    // wants. CombatManager's _targetLostScanTime grace window handles the
    // case where we then attempt to attack a stale ID.
    return true;
}
```

The original comment says *"stale IDs cause access violations that bypass managed try/catch in NativeAOT."* That justification still holds — we **can't** call `_objectIsAttackable(combatSystem, objectId)` if the weenie is null, because the function will deref it. But "we can't call the underlying function safely" is not the same as "the answer is no." The right behavior is "skip the call, default-true, let the next layer handle it."

The 1.5-second `TARGET_SCAN_GRACE_MS` in CombatManager already exists exactly for this case: a target that briefly disappears from the scan stays locked until 1500ms have elapsed. So if we accept a monster, then `GetWeenieObject` still returns null when combat tries to attack, the attack will fail, the target stays locked, and the next tick will retry — by which time the weenie is populated.

There's a deeper API issue here too: `ObjectIsAttackable` returns `bool`, conflating "definitely no" with "I don't know yet." The proper fix is a tri-state:

```csharp
public enum AttackableState { No, Yes, Unknown }

public static AttackableState QueryAttackable(uint objectId)
{
    // ... return Unknown for the weenie-null case
}
```

Then `ScanNearbyTargets` decides what to do with `Unknown` — likely "include in scan, but tag as tentative; don't blacklist on attack failure."

That's the long-term shape. The one-line fix unblocks today.

### 1.4 Confirming with a log probe

> **✅ Implemented.** `_weenieNullCount` is incremented and logged for the first 20 occurrences via `Interlocked.Increment` at `ClientObjectHooks.cs:1346-1347`. After login the log shows a small burst then goes quiet, confirming the fix.
>
> **Follow-up shipped (2026-05-06): hot-reload monster discovery.** Even with §1.3 in place, monsters that already existed in AC's world before a hot-reload were invisible to combat — `_liveObjects` is per-engine-module static state, so the new module starts empty and `ReplayPrePluginCreateObjects` skips with nothing to send the freshly loaded plugin. Added `src/RynthCore.Engine/Compatibility/CObjectMaintHooks.cs` which walks `CObjectMaint::s_pcInstance` (`0x00842ADC`) → `weenie_object_table` (offset `+0xB4`) on init, seeding `_liveObjects` from AC's authoritative table. `PluginManager.InitPlugins` calls it before the replay step. Logs `"PluginManager: Seeded N new live object id(s) from CObjectMaint (visited M)."` per init. Closes the "monsters skipped after hot reload" symptom and bonus-recovers any drift on cold start where a `CreateObject` event was queue-dropped or fired before the plugin loaded.

Before deploying, add a one-time diagnostic to confirm. In `ObjectIsAttackable`:

```csharp
if (weeniePtr == IntPtr.Zero)
{
    if (Interlocked.Increment(ref _weenieNullCount) <= 20)
        RynthLog.Compat($"ObjectIsAttackable: weenie null for 0x{objectId:X8} (count {_weenieNullCount})");
    return true;   // changed from false
}
```

After login, the log should show 5-50 of these in the first few seconds and then go quiet. If it keeps firing forever, something else is going on.

---

## 2. The CombatManager — what it does well, and what's holding it back

### 2.1 Things to keep

Reading the 1700 lines top-to-bottom: this is real combat logic, not toy code. Things that are obviously right:

- **Pre-scan separated from selection.** `ScanNearbyTargets` filters all candidates by distance/attackable/dead/blacklist/LOS into `_scannedTargets`. `HandleCombatTrigger` then picks via score. Most bots conflate these; you don't.
- **Utility-AI target scoring with stickiness.** `ScoreCandidate` blends distance, HP-remaining, threat-proximity, facing angle, and `TARGET_SWITCH_STICKINESS=25` keeps you from flapping on near-ties. This is the right shape.
- **Target-lost grace period.** `TARGET_SCAN_GRACE_MS=1500` papers over scan flicker without letting nav steal control mid-fight. Good defense in depth.
- **Time-based no-progress blacklist.** If you've been engaged with a target for `TargetNoProgressTimeoutSec` without dealing damage (immune mob, broken weapon, bad LOS that the raycaster missed), give up and blacklist. Catches the "spinning forever" failure mode.
- **`HasNearbyMonsters` distinct from `HasTargets`.** The former includes LOS-blocked mobs so navigation stops *before* walking into a room. Subtle but important.
- **Diagnostic snapshot** (`GetStateSnapshot`). Exposes internal state for `/ra combat`. The fact that you built this is a sign you've spent enough time debugging this thing to need it. Keep it.
- **`_settings.IsMacroRunning` short-circuit at the top.** Not running → return immediately, with a one-shot `ClearCombatTurnMotions()` on the false→true transition so the character stops spinning. Edge case handled correctly.

### 2.2 The problems

Reading through, the things that bother me, in priority order:

#### (a) `OnHeartbeat` and `Think` both call `ScanNearbyTargets`

`OnHeartbeat` line 848: `try { ScanNearbyTargets(); } catch { ... }`
`Think` line 569: `try { ScanNearbyTargets(); } catch { ... }`

The 50ms throttle inside the function makes the second call cheap (early return), but it's still a dictionary lookup, a date compare, and an extra log-noise opportunity. More importantly: the comment at line 882 explicitly notes that `Think()` already calls scan, but you re-derive `BotAction` from the scan results both before and after `Think`. That's confusing — every reader has to figure out which scan informed which decision.

**Fix:** Make `ScanNearbyTargets` cheaper to call multiple times (it already short-circuits) but call it explicitly *once* per heartbeat at the top, and pass the result down. Or, equivalently, have `OnHeartbeat` be the only caller and have `Think` assume the scan is already fresh. Pick one ownership model and document it.

#### (b) Two parallel blacklist implementations

> **✅ Implemented (2026-05-06) — but as dead-code removal rather than migration.** When we dug into the actual code, the old `blacklistedTargets` dictionary was queried in two places and cleaned via `CleanupExpiredBlacklist`, **but nothing in the current codebase ever inserted into it.** The `BlacklistedTarget` class with its 5s/20s `IsLosBlocked`-driven timeout was unreachable code — vestigial state from a previous implementation that got partially removed. So the consolidation collapsed to dead-code deletion: removed the dict field, the nested class, the cleanup method and its call site, and simplified two `ContainsKey(...) || _blacklistManager.IsBlacklisted(...)` checks to single calls. The proposed `BlacklistReason` enum was deferred — there's no caller today that wants a different timeout per reason, so adding it would have been premature design. If LOS-blacklisting comes back later, adding the enum is a 15-minute change.

```csharp
private readonly Dictionary<int, BlacklistedTarget> blacklistedTargets = new();  // line 87
private readonly BlacklistManager _blacklistManager = new();                      // line 89
```

And every check is `blacklistedTargets.ContainsKey(...) || _blacklistManager.IsBlacklisted(...)`. There are at least four such double-checks in the scanner and Think. Two lifetime models (`BlacklistedTarget.IsExpired()` uses 5s for LOS-blocked vs 20s otherwise; `BlacklistManager` uses `TimeoutSeconds` from settings), two cleanup paths.

This was almost certainly an "I added the new manager and didn't finish migrating from the old one" situation. The cost is real: a target marked as LOS-blocked goes to `blacklistedTargets` (5s timeout) but a target marked from no-progress timeout goes to `_blacklistManager` (configured timeout). The behavior is asymmetric in a way that's not documented at the call sites.

**Fix:** Pick one. `_blacklistManager` is the newer interface. Migrate the LOS-blacklist case into it with a new `Reason` enum field that drives the timeout:

```csharp
public enum BlacklistReason { LosBlocked, NoProgress, AttackFailed, Manual }

_blacklistManager.Report(targetId, BlacklistReason.LosBlocked);  // 5s
_blacklistManager.Report(targetId, BlacklistReason.NoProgress);  // _settings.BlacklistTimeoutSec
```

Internally, the manager keeps one dictionary and looks up the timeout from the reason. Delete `BlacklistedTarget` and the dual-check sites. ~2 hours of work, removes ~50 lines of duplicate handling.

#### (c) Target-validation cleanup is duplicated 5 times

> **✅ Implemented.** `DropTarget(string reason)` was extracted (now at `CombatManager.cs:550`) and the five duplicated cleanup blocks in `Think` were converted. The final straggler — manual `activeTargetId = 0; _lockedTargetId = 0;` in `TrackAttackAttempt`'s blacklist path — was converted to `DropTarget("blacklisted after N no-damage attacks")` on 2026-05-06. Drop reasons now appear in logs at every site.

`Think` lines 581-627 has five `if/else if` branches that each look like:

```csharp
activeTargetId = 0;
_lockedTargetId = 0;
_facingTarget = false;
_returnToPhysicalCombat = false;
_targetLockedAt    = DateTime.MinValue;
_lastDamageDealtAt = DateTime.MinValue;
ClearCombatTurnMotions();
```

The reasons differ (blacklisted, became corpse, hp=0, out of range, lost from scan after grace period), but the *cleanup* is identical. It's also literally repeated again at lines 668-676 and 697-704.

**Fix:** Extract a single method:

```csharp
private void DropTarget(string reason)
{
    if (activeTargetId != 0)
        Log.Debug($"DropTarget: {activeTargetId} ({reason})");
    activeTargetId = 0;
    _lockedTargetId = 0;
    _facingTarget = false;
    _returnToPhysicalCombat = false;
    _targetLockedAt = DateTime.MinValue;
    _lastDamageDealtAt = DateTime.MinValue;
    ClearCombatTurnMotions();
}
```

Now reasons are visible in logs and the cleanup is in one place. 15 minutes; reduces Think by ~30 lines.

#### (d) `Think` is a single 200-line method

Lines 547-823. It does target validation, candidate scoring, peace-mode-when-idle, scan-grace timing, no-progress timeout, distance gate, weapon equip, ranged facing, native attack, spell selection, ring/streak handling, debuff dispatch... in one method. If anything in this method throws, the whole think tick is lost.

The Think pipeline naturally has phases:

```
1. Refresh state            (cleanup blacklist, sync FSM settings)
2. Scan                     (already extracted)
3. Validate active target   (5 cleanup conditions)
4. Pick best candidate      (HandleCombatTrigger)
5. Verify target visibility (scan-grace check)
6. Check no-progress timer
7. Distance gate
8. Equip weapon / set stance
9. Face target (ranged only)
10. Execute attack          (native / spell / melee)
```

Each phase is ~10-30 lines. Refactor each into a private method that returns `bool` (true = continue to next phase, false = bail this tick). The Think becomes a list:

```csharp
public bool Think()
{
    if (!_settings.EnableCombat) return false;
    if (!RefreshFrameState()) return true;
    ScanNearbyTargets();
    if (!ValidateActiveTarget()) return true;
    HandleCombatTrigger();
    if (activeTargetId == 0) { HandlePeaceModeWhenIdle(); return true; }
    if (!VerifyTargetVisible()) return true;
    if (!CheckNoProgressTimeout()) return true;
    if (!IsTargetInRange()) return true;
    if (!EquipWeaponAndSetStance(...)) return true;
    if (NeedsFacing() && !PerformFacing()) return true;
    return ExecuteAttack();
}
```

Now each phase is readable, testable in isolation, and an exception in one phase logs that phase's name in the message. ~half a day.

#### (e) State-sync brittleness with `CurrentCombatMode`

> **✅ Implemented in three places.** `CombatManager.CurrentCombatMode` was already converted to a live getter (`_host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : CombatMode.NonCombat`). On 2026-05-06 the same fix was extended to `BuffManager.CurrentCombatMode` and `MissileCraftingManager.CurrentCombatMode`, both of which were still cached fields fed via `RynthAiPlugin.OnCombatModeChange`. The redundant pushes from `RynthAiPlugin` were removed. All three managers now read live from AC every access — drift class fully eliminated, hot-reload-safe.

`CurrentCombatMode` is set on `CombatManager` from outside (line 65 + 253 in plugin). The plugin reads it via `Host.GetCurrentCombatMode()` once at login (line 248) and also on `OnCombatModeChange` events. But CombatManager itself never re-reads from the host during a tick — it trusts the cached value.

If the user manually changes combat mode in-game and the `OnCombatModeChange` event is dropped (it's not on the pattern-verified hook list — see the v2 review), CombatManager's view of the world goes stale.

`GetStateSnapshot` at line 188-192 already shows you suspect this:

```csharp
int liveMode = -1;
if (_host.HasGetCurrentCombatMode)
{
    try { liveMode = _host.GetCurrentCombatMode(); } catch { liveMode = -1; }
}
```

You're reading `LiveCombatMode` as a *separate* field for the diagnostic. So you've noticed they can diverge.

**Fix:** Just read live every tick. `Host.GetCurrentCombatMode()` is a pointer deref + integer read, it's free. Drop the cached field; make `CurrentCombatMode` a getter that calls the host. Then there's no possibility of drift.

#### (f) Magic-number timing constants

```csharp
private const double FACE_TIMEOUT_MS = 1000.0;
private const double FACE_TOLERANCE_DEG = 15.0;
private const double TARGET_SCAN_GRACE_MS = 1500.0;
private const double ATTACK_SPELL_COOLDOWN_MS = 100.0;
private const double DEBUFF_RESULT_TIMEOUT_MS = 3000.0;
private const int SCAN_INTERVAL_MS = 50;
private const double TARGET_SWITCH_STICKINESS = 25.0;
```

Some of these are physics ("how fast can the character turn"), some are network ("how long to wait for a server confirmation"), some are utility-AI tuning ("how strongly to prefer current target"). They're all `private const` baked into combat code.

The physics ones are server-dependent (PvE servers vs custom emulator builds may differ). The utility-AI ones are user-tunable preferences. The network ones are tied to the `_settings.BlacklistTimeoutSec`-style config you already have.

**Fix:** Move them to `LegacyUiSettings` (or a dedicated `CombatTuning` settings struct). The user-facing UI doesn't need to expose all of them; an "Advanced" settings panel can. Keep defaults sensible. This unblocks future-you from "I need to tune face-tolerance for a 2H sword character" without recompiling.

#### (g) `_facingTarget` and ranged-attack facing logic interleaved with target selection

Lines 30-44 show face-state fields. They're consulted across multiple places in Think. The face-before-attack flow is a mini state machine inside the larger combat logic:

```
Idle → Facing (start turn) → Facing (in progress) → ReadyToAttack → Attack → Idle
```

But it's expressed as `_facingTarget` bool + `_faceStartTime` DateTime + branches scattered in Think. The transitions aren't all in one place; you have to grep `_facingTarget` to follow them.

**Fix:** A `FacingController` class with explicit states and a `bool TickAndCheckReady()` returning true when it's safe to attack. CombatManager owns one and calls `_facing.Begin(targetId)` to start, `_facing.Tick()` per heartbeat, `_facing.IsReady` to gate attack. That's a 50-line class and Think loses 4 fields and ~30 lines of facing logic. Same pattern works for combat-mode-transition and weapon-swap logic.

---

## 3. Combat / Loot / Nav prioritization — the bigger architectural problem

### 3.1 What's there now

`RynthAiPlugin.OnTick` is the de-facto scheduler. It runs through ~10 different subsystems, each with conditional gates, settle windows, gap-fill state corrections, and boost flags. Reading lines 307-549, the prioritization model is:

```
String state machine:
  BotAction ∈ {"Default", "Combat", "Buffing", "Salvaging", "Looting", "Navigating"}

Hard priority order (when boosts off):
  Buffing > Salvaging > Crafting > Combat > Looting > Navigation

User-toggleable overrides:
  BoostNavPriority   — Nav wins over Combat and Looting
  BoostLootPriority  — Loot wins over Combat
  (no override for Buffing — buffs dropping = death)

Settle gates:
  inventorySettled = (now - loginCompleted) > 3000ms  — blocks Inventory/Salvage/Missile

State corrections (called "gap-fills"):
  if NeedsAnyBuff and BotAction != "Buffing": BotAction = "Buffing"
  if (container open or salvage busy) and BotAction != "Buffing":
      BotAction = "Salvaging"
```

### 3.2 What's wrong with this

Most of these aren't bugs *yet*, but they're fault lines that will become bugs as the system grows:

**1. String-keyed state machine.** Magic strings everywhere, no compiler check on typos, no centralized list of valid states. `"Combat"` vs `"Combat "` (trailing space) is a silent bug. Make it an enum.

**2. Gap-fill state corrections fight against the priority model.** The pattern is: "let combat run, but if buffs need attention, retroactively patch BotAction to 'Buffing' so other code sees the right state." That's saying out loud "the priority model isn't the source of truth; the activities are, and we patch the priority field to look consistent." A real scheduler would have the activities *score themselves* and the scheduler would pick. No retroactive patching.

**3. Boost flags are a binary escape hatch for a scoring problem.** Some users want loot-cleanup to interrupt next-mob engagement; others don't. Some want chain-pulling past minor mobs; others don't. The current design says "you get one ON/OFF boost per axis." A real solution is a *weight* (0-200%, default 100%) per activity, and the scheduler factors it in. Default behavior stays the same (weights all = 100%); power users get fine-grained control.

**4. Settle windows are race-condition workarounds.** `inventorySettled` (3000ms after login) gates `inventoryManager`, `salvageManager`, and `missileCraftingManager` because their `GetDirectInventory` walks race against the cache classification burst. That's a real race, but the fix is "wait 3 seconds and hope" — which is fragile (faster CPUs are fine, slower CPUs may need longer; busy spawn rooms produce more pending objects). The *correct* fix is for those managers to depend on a "cache stable" signal from the cache itself. The cache knows when `_pending.Count == 0` and `_classifyRetry.Count == 0`; when both are zero it's stable. That signal should drive the gate, not wall-clock time.

**5. MetaManager.Think called three times.** Lines 382, 463, 542. Each is on a different early-return path. The intent is "Meta gets to think no matter which other activity runs." That works, but it's brittle: any new early return will silently skip Meta. A scheduler that runs Meta as a *background* activity (always, regardless of which exclusive activity is current) would be cleaner.

**6. Activity dependencies are implicit.** Combat depends on combat-mode being correct, weapon equipped, target facing. Looting depends on busy-count == 0 and a corpse open. Salvage depends on inventory not being mid-walk. These dependencies are scattered as "if (...) return" guards inside each manager. They should be declared.

**7. `OnTick` itself is 240+ lines** and as the v2 review noted, has its own diag-tracer flag because debugging it requires log lines between every step. That's the hallmark of a method that should be a pipeline, not a sequence of conditionals.

### 3.3 What I'd build instead

The right pattern for this size of system is **utility-AI scheduler + activity classes**. The shape:

```csharp
public interface IActivity
{
    string Name { get; }
    ActivityCategory Category { get; }   // Background or Exclusive

    // Scheduler asks each activity, every tick:
    bool CanRun(TickContext ctx);        // hard prerequisites (e.g. combat needs login complete)
    double Score(TickContext ctx);       // 0-100, higher = more wanted; 0 means "irrelevant right now"
    double Stickiness { get; }           // bonus when this activity is current (10-30 typical)

    // Lifecycle:
    void OnEnter(TickContext ctx);       // fires once when this activity becomes current
    void Tick(TickContext ctx);          // do the work
    void OnYield(TickContext ctx);       // fires once when another activity wins
}

public enum ActivityCategory
{
    Background,  // runs every tick regardless (cache, vitals refresh, meta, jumper)
    Exclusive,   // only one runs per tick (combat, looting, navigation, salvage, buffing, crafting)
}
```

The scheduler:

```csharp
public sealed class ActivityScheduler
{
    private readonly List<IActivity> _bg = new();
    private readonly List<IActivity> _ex = new();
    private IActivity? _current;

    public void RegisterBackground(IActivity a) => _bg.Add(a);
    public void RegisterExclusive(IActivity a) => _ex.Add(a);

    public void Tick(TickContext ctx)
    {
        // Background activities always run
        foreach (var a in _bg)
        {
            try { a.Tick(ctx); }
            catch (Exception ex) { ctx.Log.Error($"{a.Name} bg threw: {ex}"); }
        }

        // Exclusive activity selection
        IActivity? best = null;
        double bestScore = double.MinValue;
        foreach (var a in _ex)
        {
            if (!a.CanRun(ctx)) continue;
            double s = a.Score(ctx);
            if (a == _current) s += a.Stickiness;
            if (s > bestScore) { bestScore = s; best = a; }
        }

        if (best != _current)
        {
            try { _current?.OnYield(ctx); } catch { }
            _current = best;
            try { _current?.OnEnter(ctx); } catch { }
            ctx.Log.Info($"Scheduler: {_current?.Name ?? "<none>"} active");
        }

        try { _current?.Tick(ctx); }
        catch (Exception ex) { ctx.Log.Error($"{_current?.Name} threw: {ex}"); }
    }

    public string CurrentActivityName => _current?.Name ?? "<idle>";
}
```

Then activities self-describe:

```csharp
public sealed class CombatActivity : IActivity
{
    public string Name => "Combat";
    public ActivityCategory Category => ActivityCategory.Exclusive;
    public double Stickiness => 20.0;

    public bool CanRun(TickContext ctx) =>
        ctx.Settings.IsMacroRunning &&
        ctx.Settings.EnableCombat &&
        ctx.LoginComplete;

    public double Score(TickContext ctx)
    {
        if (!ctx.Combat.HasNearbyMonsters) return 0;

        double baseScore = 70.0;                                   // beats nav (30) and loot (50)
        if (ctx.Player.HpRatio < 0.4) baseScore -= 30;             // low HP, deprioritize
        if (ctx.Combat.HasCloseThreat(3.0)) baseScore += 20;       // melee range, urgent
        return baseScore * ctx.Settings.CombatPriorityWeight;       // user multiplier
    }

    public void Tick(TickContext ctx) => ctx.CombatManager.OnHeartbeat();

    public void OnEnter(TickContext ctx) => ctx.NavManager.Stop();
    public void OnYield(TickContext ctx) { /* nothing */ }
}

public sealed class BuffingActivity : IActivity
{
    public string Name => "Buffing";
    public ActivityCategory Category => ActivityCategory.Exclusive;
    public double Stickiness => 50.0;   // very sticky — don't interrupt mid-rebuff

    public bool CanRun(TickContext ctx) =>
        ctx.Settings.EnableBuffing && ctx.LoginComplete;

    public double Score(TickContext ctx)
    {
        if (!ctx.BuffManager.NeedsAnyBuff()) return 0;
        // Buffs that have completely dropped → max priority
        if (ctx.BuffManager.HasExpiredBuff()) return 100.0;
        // Buffs about to expire → high priority
        if (ctx.BuffManager.HasBuffExpiringWithin(60)) return 85.0;
        // Buffs need refresh but not urgent → modest
        return 60.0;
    }

    public void Tick(TickContext ctx) => ctx.BuffManager.RunOneStep(ctx);
    public void OnEnter(TickContext ctx) => ctx.NavManager.Stop();
    public void OnYield(TickContext ctx) { }
}

public sealed class NavigationActivity : IActivity
{
    public string Name => "Navigating";
    public ActivityCategory Category => ActivityCategory.Exclusive;
    public double Stickiness => 5.0;   // not very sticky — interruptable

    public bool CanRun(TickContext ctx) =>
        ctx.Settings.IsMacroRunning && ctx.Nav.HasRoute;

    public double Score(TickContext ctx) =>
        ctx.Nav.HasRoute ? 30.0 * ctx.Settings.NavPriorityWeight : 0;

    public void Tick(TickContext ctx) => ctx.NavManager.Tick();
    public void OnEnter(TickContext ctx) { /* resume from current waypoint */ }
    public void OnYield(TickContext ctx) => ctx.NavManager.Stop();
}
```

User boost flags become weight settings (`CombatPriorityWeight`, `LootPriorityWeight`, `NavPriorityWeight`, defaults 1.0). The user can dial them. Defaults reproduce current behavior.

**Background activities** (cache tick, quest tracker, give-queue drain, jumper, vitals refresh, meta-rule evaluation) run every tick regardless of which exclusive activity won. No more "MetaManager.Think called from 3 places."

OnTick becomes:

```csharp
public override void OnTick()
{
    var ctx = BuildTickContext();   // packages up references to vitals, cache, settings, etc.
    _scheduler.Tick(ctx);
}
```

That's it. The scheduler is ~80 LOC. Each activity is 30-80 LOC. Total similar to today; structure dramatically clearer.

### 3.4 What you get

- **No more BotAction string state.** The scheduler's `_current` is the source of truth. UI displays `_scheduler.CurrentActivityName`.
- **No more gap-fill corrections.** Buffing's `Score` returns 100 when buffs dropped — it wins on its own merit.
- **No more boost flags.** Score weights with sensible defaults.
- **No more settle windows.** `BuildTickContext` includes `ctx.CacheStable`; activities that need a stable cache check it in `CanRun`.
- **Adding a new activity is local.** New `IActivity` class, register it in scheduler. No edits to existing activities.
- **Test-friendly.** Pass a fake `TickContext` to an activity, assert what it does. No need to spin up the whole plugin.
- **Single-place exception logging.** The scheduler wraps every Tick call; one bad activity can't break others.

### 3.5 The catch

This is a real refactor. Order-of-magnitude estimate:

- Build the scheduler + IActivity infra: half a day.
- Move each existing manager into an IActivity wrapper: 2-3 hours per manager × ~7 managers = 2-3 days.
- Wire the new scheduler into OnTick, parallel-run with current code behind a feature flag: 1 day.
- Test: 2 days minimum (combat in dungeons, edge cases like portals, lifestone deaths, fellow summons, salvage queue interrupts during combat, nav-while-buffing, etc.).
- Delete old code: 1 hour.

Ballpark: **a week of focused work**. That's a lot. But the current model is already groaning under the weight of seven managers; an eighth (e.g., lockpicking, fishing, vendor-buy-buffs) is going to require touching every existing one's "can I run?" guard. The refactor pays itself back the next time you add an activity.

---

## 4. Tactical bugs and code smells worth fixing immediately

These don't depend on the architectural refactor:

### 4.1 The login bug — fix `ObjectIsAttackable` weenie-null path (§1)

5 minutes. Highest-impact one-line change.

### 4.2 Inconsistency in `_objectIsAttackable` exception path

`ClientObjectHooks.cs:1346` — calls into the C++ function inside a `try`. The catch (line 1348) defaults to `true`. But the call itself doesn't have an additional null-check on `combatSystem` after retrieving it (it does at line 1344, but if `_getCombatSystem()` returns garbage that's not null, no AV protection). Low probability but worth confirming.

### 4.3 `ClearCombatTurnMotions` is called inside heartbeat *and* on every drop-target

I count 9 call sites in CombatManager. Each one does `Host.SetMotion(MotionStop, ...)` or similar. If two consecutive ticks both clear, you're sending duplicate motion stops to the server. Almost certainly idle harmless, but worth a "did this already clear?" guard.

### 4.4 `ScoreCandidate` ignores monster-rule weights

> **✅ Implemented (2026-05-06), formula adjusted per user intent.** The review's `Priority * 5.0` would have biased *every* monster with any rule (default `Priority = 1`) over rule-less monsters by +5 even when the user didn't intend any elevation. After the user clarified that priority is meant for "higher-threat targets the player explicitly marks via the monster panel" — most grinding wants the natural attack-fastest scoring — the shipped formula is `(Priority - 1) * 5.0`, gated to only fire when a *specific* (non-`"Default"`) rule matches by name. Default `Priority = 1` adds 0 (no bias toward configured-but-unelevated monsters); `Priority = 10` adds +45 (about half a max-distance score, switches targets unless alternatives are very close/low-HP); `Priority = 20` adds +95 (dominates unless the alternative is right in your face). Score formula is now `distScore + hpScore + threatScore + facingScore + priorityScore`.

Line 928-937: scores only look at distance, HP, threat, facing. But you have `_settings.MonsterRules` with per-monster weapon/priority overrides. If a `MonsterRule` says "Always attack Drudges first," that priority isn't reflected in the score. The `MonsterMatchEvaluator` exists but isn't used in scoring. That's a missed feature.

**Fix:** Add a `priorityScore` term:
```csharp
double priorityScore = 0;
var rule = _settings.MonsterRules.FirstOrDefault(
    r => c.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);
if (rule != null) priorityScore = rule.Priority * 5.0;  // user-tuned 0-20 → 0-100 score
return distScore + hpScore + threatScore + facingScore + priorityScore;
```

### 4.5 `_consecutiveMisses` and `_lastAttackedTargetId` declared but mostly dead

Grep shows them set but the read sites are minimal. Either wire them into the score (penalize a target you've missed N times in a row) or remove them. Right now they're carrying fields without earning their keep.

### 4.6 The 50ms scan interval doesn't account for tick rate

`SCAN_INTERVAL_MS = 50` means "scan at most every 50ms." But OnTick fires at frame rate (60fps = 16.7ms). If the engine is at 144fps, scan fires every 3rd frame; at 30fps, every other. That's fine but it means "scan rate" is a misnomer — it's a debounce. Consider naming or commenting:

```csharp
private const int SCAN_DEBOUNCE_MS = 50;  // skip scan if last ran <50ms ago
```

### 4.7 `RaycastBlockCount` and `RaycastCheckCount` increment forever

No reset path. Over a long session they grow unbounded. Not a leak in the dangerous sense, but they're meant to be diagnostic counters; a reset on activity transition (or at least exposed for the user to reset in the diag panel) would be useful.

---

## 5. Prioritized action list

### Status legend
- ✅ **Done** — shipped to production
- 🟡 **Deferred** — reviewed and decided to skip for now
- ⬜ **Open** — not yet attempted

### Immediately (this week)

| # | Status | Item | Effort | Section |
|---|---|---|---|---|
| 1 | ✅ | Fix `ObjectIsAttackable` weenie-null path → return true | 5min | §1.3 |
| 2 | ✅ | Add login-window log probe to confirm fix | 10min | §1.4 |
| 2b | ✅ | **Bonus: hot-reload monster discovery** — `CObjectMaintHooks` walks AC's `weenie_object_table` to re-seed `_liveObjects` so the freshly-loaded plugin sees existing world | ~1h | §1.4 follow-up |
| 3 | ✅ | Extract `DropTarget(reason)` helper | 15min | §2.2c |
| 4 | ✅ | Drop cached `CurrentCombatMode`, read live each tick (extended to BuffManager + MissileCraftingManager) | 30min | §2.2e |

### Next sprint

| # | Status | Item | Effort | Section |
|---|---|---|---|---|
| 5 | ✅ | Unify two blacklist implementations *(simplified to dead-code removal — old impl was never populated)* | 30min | §2.2b |
| 6 | ⬜ | Refactor `Think` into pipeline-of-phases | 4h | §2.2d |
| 7 | ✅ | Add monster-rule priority into `ScoreCandidate` *(formula `(Priority-1)*5` per user intent)* | 30min | §4.4 |
| 8 | ⬜ | Move magic-number combat tunings into Settings | 1h | §2.2f |
| 9 | ⬜ | Extract `FacingController` from Think | 2h | §2.2g |
| 10 | ⬜ | Rename `SCAN_INTERVAL_MS` → `SCAN_DEBOUNCE_MS` and fix counters | 30min | §4.6, §4.7 |

### The big refactor (when you have a week)

| # | Item | Effort | Section |
|---|---|---|---|
| 11 | Build `ActivityScheduler` + `IActivity` infra | 0.5d | §3.3 |
| 12 | Migrate each existing manager into an `IActivity` wrapper | 2-3d | §3.3 |
| 13 | Replace BotAction string state with scheduler's CurrentActivityName | 0.5d | §3.4 |
| 14 | Replace boost flags with score weights in settings | 0.5d | §3.3 |
| 15 | Replace `inventorySettled` time gate with `ctx.CacheStable` flag | 1h | §3.2 |
| 16 | Test in real play sessions, behind feature flag | 2d | — |
| 17 | Remove old OnTick orchestration code | 1h | — |

### The order I'd actually do this in

1. **Day 1:** Fix the login bug (§1.3). Add the log probe. Do steps 3 and 4 (DropTarget + live combat mode) — both are small, both reduce noise.
2. **Day 2:** Unify blacklists (§2.2b) and add monster-rule priority (§4.4). Combat targeting becomes more correct.
3. **Day 3-4:** Refactor `Think` into the phase pipeline (§2.2d). With smaller phases, the next week's work becomes much easier.
4. **Days 5-9:** The scheduler refactor. Behind a flag. Run both old and new code in parallel — the new scheduler decides what runs but the diagnostic still shows the old `BotAction` field for comparison. When they agree for two weeks of real play, delete the old code.

That's two weeks of focused work to a substantially better-architected combat/loot/nav system, with the login bug fixed on day 1.

---

## 6. One thing I want to push back on

You asked "how can we prioritize combat/loot/nav better." The implicit assumption is that the current model needs better prioritization *rules*. I think the actual problem is one level up: the prioritization model itself is the wrong shape. A string-keyed BotAction with binary boost flags and gap-fill corrections is what you build when you have 2-3 activities; you have 7+ now and growing.

The utility-AI + scheduler pattern isn't just a refactor — it's a different model where you don't *write* prioritization rules at all. You write *scoring functions* per activity, and the right priorities emerge from the scores. The user's "boost loot priority" becomes "set LootPriorityWeight to 1.5"; the bot author's "make sure buffing wins" becomes "BuffingActivity.Score returns 100 when expired" — and those are independent statements in independent files.

The current model has buffing winning over combat because of an `if BotAction == "Buffing"` early return at line 370. That's the priority *being enforced as a side effect of code structure*. With the scheduler, that priority is data — and data is debuggable, tunable, and overridable per-character without code changes.

You don't have to do the scheduler refactor today. But I'd plant the flag now: every new activity from this point should be designed *as if* it were going to be an `IActivity` later. That means it owns its own "should I run?" logic and "what's my score?" computation, not depends on string state being patched from outside. When you finally do the refactor, those activities migrate cleanly; the older string-based ones become the painful migration.

---

## 7. Quick win you might not have considered

The login window of "monsters ignored" can be made *visible* before you fix it, which makes future bugs of similar shape easier to catch.

In `ScanNearbyTargets` line 280-373, you already log diagnostics:

```csharp
_host.Log($"[RynthAi] scan: monsters={monstersSeen} accepted={_scannedTargets.Count} " +
          $"bl={exBlacklist} !atk={exNotAttackable} dead={exDead} range={exRange} los={exLos} ...");
```

After the fix, the `!atk` count for the post-login window should drop to near-zero. After the fix, if it ever spikes again, you have an immediate signal. Also worth: add a *first-engagement-latency* metric — `(time of first scan with accepted > 0) - (login complete time)` — and log it once at engagement. Today you have no number for this. After the fix, you should see it drop from "2-3 seconds" to "tens of milliseconds." That's a regression-detection asset for free.
