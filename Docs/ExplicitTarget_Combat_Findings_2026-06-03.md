# Explicit-Target Combat (no SelectItem) — Findings & Decision Notes

**Date:** 2026-06-03
**Status:** Magic = DONE/shipped. Melee/Missile = INVESTIGATED, **NOT applied — undecided.**
**⚠ Blocker for melee/missile:** the explicit-target (direct) path **does not turn the character toward the monster** (§5). This is the real reason we have not switched.

---

## 1. Goal

Stop combat from clobbering the **user's inventory selection**. AC has a **single** selection global (`0x00871E54`). The bot calling `SelectItem`/`SetSelectedObject` to "hold" the combat target overwrites that same global every combat tick, so the user can't keep an inventory item selected while the bot fights. We want combat to target **explicitly** (target id passed as a function argument), never touching AC's selection — the way the magic fix now does.

(Bonus: `SelectItem` is an off-thread `SetSelectedObject` mutation not covered by P1 marshalling, so removing it also drops an off-thread AC mutator = cleaner stability soak.)

## 2. Magic — DONE (shipped 2026-06-03)

Replaced the old `SelectItem(targetId) + ClientMagicSystem::CastSpell(spellId, targetIsSelected=1)` two-step (which reads the selected-object global `[magicSysSingleton+0xF4]`, and didn't survive being marshalled onto AC's tick) with the explicit-target path RC2 uses:

- `ClientMagicSystem::GetMagicSystem()` @ **`0x00567C00`** (`__cdecl`, returns the magic-system singleton `this`)
- `ClientMagicSystem::FreeHandsAndCastSpell(this, uint spellId, uint targetId)` @ **`0x00567C90`** (`__thiscall`; target is an **explicit arg**; also auto-readies the wand)

Bound in `RynthCore\src\RynthCore.Engine\Compatibility\CombatActionHooks.cs` (fixed VAs, validated in-module), invoked on AC's main thread via the `AcMainThreadQueue`/`GameTickHooks` UseTime drain, wrapped in the SEH trampoline (`SehTrampoline.FreeHandsCast`, native `SEH_FreeHandsCast`). **Verified live: casts land, kills mobs, no inventory clobber.** Self-casts still use `CastSpell(spellId, 0)` (no selection dependency).

## 3. Melee / Missile — FOUND, NOT APPLIED

**v1 already has clean explicit-target physical attacks — NO new AC function needed** (unlike magic, which needed FreeHands). Both Ghidra-confirmed to take the target id as an explicit stack arg and to **never read the selection global `0x00871E54`** (they just build + send a game-action packet):

| Attack | AC function | VA | Conv | Opcode | Notes |
|---|---|---|---|---|---|
| Melee  | `CM_Combat::Event_TargetedMeleeAttack(targetId, ATTACK_HEIGHT, power)`     | `0x006AAB70` | cdecl→bool | `0x08` | selection-free |
| Missile| `CM_Combat::Event_TargetedMissileAttack(targetId, ATTACK_HEIGHT, accuracy)`| `0x006AACC0` | cdecl→bool | `0x0A` | selection-free; projectile launches along current heading |

- `ATTACK_HEIGHT`: High=1, Med=2, Low=3. `power`/`accuracy` = 0..1 float (server honors it directly — no local bar needed).
- Already bound in `CombatActionHooks.cs` as `_meleeAttack` / `_missileAttack` (pattern-scan on the `MOV [edx],0x08`/`0x0A` opcode → walk back to prologue `83 EC 0C 53 56 57 E8`; region-bounded so it resolves uniquely — unlike the abandoned `_castSpell` whole-section scan). Already main-thread marshalled via `AcMainThreadQueue.EnqueueMeleeAttack/EnqueueMissileAttack`.
- Plugin already calls them: `_host.MeleeAttack(targetId,...)` / `_host.MissileAttack(targetId,...)` — the **DIRECT** path. No `SelectItem`.

**RC2 has NO physical combat** (RynthBot is magic-only; physical = deferred "Phase 6, needs new shim primitives"). So there was nothing to port — v1 is ahead, and this doc is the blueprint for RC2's eventual Phase 6 (add `CmdMeleeAttack`/`CmdMissileAttack` shim exports calling these VAs with the explicit target; do NOT add a `SelectTarget`+bar-fill pair).

## 4. The clobber source (the ONLY one left in combat)

`RynthCore.Plugin.RynthAi\Combat\CombatManager.cs` `AttackTarget` (~lines 1598–1618):

```csharp
if (_settings.UseNativeAttack && _host.HasNativeAttack)   // <-- DEFAULT TRUE
{
    _host.SelectItem(targetId);     // <-- THE CLOBBER (writes selection global 0x871E54)
    _host.NativeAttack(acHeight, power);
    return;
}
// Direct path (explicit target, NO SelectItem):
if (isMissile) _host.MissileAttack(targetId, acHeight, power);
else           _host.MeleeAttack(targetId, acHeight, power);
```

- **NATIVE path** (`UseNativeAttack=true`, default — `LegacyUi\LegacyUiSettings.cs:171`): `NativeAttack` → `ClientCombatHooks.NativeAttack` → AC `ClientCombatSystem` pipeline (`GetCombatSystem` `0x0056B210` → `SetRequestedAttackHeight` `0x0056D640` → `StartAttackRequest` `0x0056CD90` → `EndAttackRequest` `0x0056CE30`, all `__thiscall` on the combat-system singleton). **This pipeline has NO target argument** — it attacks whatever is currently *selected*, which is why it must be preceded by `SelectItem(targetId)`. That coupling is the clobber.
- The per-tick targeting `SelectItem` calls (CombatManager `:995`, `:1203`) were already removed 2026-06-03 (the magic-fix change). The native-path `SelectItem` (`:1609`) is the **last** selection clobber in the combat hot path.

## 5. ⚠ THE BLOCKER: facing (why we're undecided)

**The native path turns the character to face the monster; the direct path does NOT.**

- Native: AC's `ClientCombatSystem` does turn-to-face as part of the attack sequence. The plugin skips its own facing when `useNative`.
- Direct: `Event_Targeted*Attack` just builds + sends the attack packet. **It does not turn the character.** Observed live 2026-06-03: with the direct path the bot **does not turn toward the monster** — a real problem (melee swings/arrows go nowhere useful if not facing).
- The plugin *has* a `FaceTarget`/`GetFacingError` gate (applied to `isRanged`), but it's the non-default path, under-tested, and in practice is not reliably turning. Melee currently has *no* explicit face-first (it relied on AC's swing auto-orienting, which is not happening on the direct path).

**=> Going direct REQUIRES building reliable turn-to-face for the physical attack** (issue a turn/heading toward the target before the direct attack, and confirm the character is within tolerance). RC2 does this for magic via a rate-limited `TurnToHeading` slew to within ~20°. v1 would need the equivalent, working, for melee AND missile, on a moving target. **Until that's solved, the native path (with the clobber) is the only one that reliably faces the monster.**

## 6. The fix (IF we solve facing and decide to go this route)

Plugin-side only — **no new VA, no engine/shim change**:
- **Option A:** default `LegacyUiSettings.UseNativeAttack = false`. (User can re-enable → clobber returns.)
- **Option B:** hard-gate the native branch off for the bot (guarantees no clobber; removes native as an option).
- **Required regardless:** make the direct-path turn-to-face reliable (§5) before/around the `_host.MeleeAttack/MissileAttack` call. Keep the existing ammo gate (`HasWieldedAmmo`) and accuracy/power computation (both already path-agnostic) and the main-thread marshalling.

Natural bundle: ship this together with the **stuck-target watchdog** (the other open CombatManager follow-up — bot looped casting a non-resolving locked target and wedged, 2026-06-03).

## 7. Key files / VAs

- Plugin combat: `C:\Projects\RynthSuite\Plugins\RynthCore.Plugin.RynthAi\Combat\CombatManager.cs` — `AttackTarget` ~1598-1618; facing gate ~967-989; ammo `HasWieldedAmmo` ~1566-1575; accuracy/power ~1589-1600. Default flag: `LegacyUi\LegacyUiSettings.cs:171`.
- Engine direct bindings: `C:\Projects\RynthCore\src\RynthCore.Engine\Compatibility\CombatActionHooks.cs` (`_meleeAttack`/`_missileAttack` pattern-scan in `Probe`; `MeleeAttack`/`MissileAttack` callsites).
- Engine native binding (needs selection): `C:\Projects\RynthCore\src\RynthCore.Engine\Compatibility\ClientCombatHooks.cs` (`NativeAttack`).
- AC VAs: Melee `0x006AAB70`, Missile `0x006AACC0`, GetMagicSystem `0x00567C00`, FreeHandsAndCastSpell `0x00567C90`, SetSelectedObject `0x0058D110`, selected-id global `0x00871E54`. CombatSystem pipeline: `0x0056B210/0x0056D640/0x0056CD90/0x0056CE30`.
- References: Chorizite `CM.cs` (verified VAs/signatures), `acclient.map` (liveVA = mapVA + 0x401000), Ghidra decompile `G:\My Drive\ACClient Decompile\Ghidra`.
