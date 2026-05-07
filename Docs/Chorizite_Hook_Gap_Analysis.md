# Chorizite vs RynthCore — Hook Gap Analysis

*Date: 2026-05-06*
*Sources: `C:\Projects\Chorizite-master\` and `C:\projects\rynthcore\src\RynthCore.Engine\Compatibility\`*

This document compares the hook surface of Chorizite (community AC modding framework) to RynthCore. The aim is to identify Chorizite hooks we could adopt and to record where we have already surpassed Chorizite, so we don't waste cycles re-deriving things they've already mapped.

All addresses below assume the standard `0x00400000` base AC client build that both projects target.

---

## 1. Hook gaps worth implementing

### Tier 1 — UI event hooks Chorizite has, RynthCore does not

| Hook | Chorizite VA | Function | What it exposes |
|------|--------------|----------|-----------------|
| Tooltip start | `0x0045DF70` | `UIElementManager::StartTooltip` | Hovered object ID + icon ID |
| Tooltip reset | `0x0045C440` | `UIElementManager::ResetTooltip` | Tooltip-hidden signal |
| Tooltip check | `0x0045B7C0` | `UIElementManager::CheckTooltip` | Per-frame tooltip evaluation |
| Drag start | `0x0045E120` | `UIElementManager::StartDragandDrop` | Item/spell ID, underlay/overlay icon |
| Drop | `0x00461860` | `UIElement::CatchDroppedItem` | Drop target + flags |
| Cursor change | `0x0045A910` | `UIElementManager::SetCursor` | Cursor + hotspot coords |
| Floaty lock | `0x004D3C20` | `gmFloatyIndicatorsUI::UpdateLockedStatus` | Compass/radar lock state |

Chorizite source: `Chorizite.NativeClientBootstrapper\Hooks\UIHooks.cs`.

**Impact for RynthCore:**
- Tooltip + drag-drop give us first-class hooks for plugin tooltips, appraisal-on-hover, and inventory-aware UIs without polling.
- Cursor hook helps Avalonia overlays that need cursor-aware rendering.
- Floaty lock state ties directly to RynthRadar (`project_rynth_radar.md`) — knowing when the player locks the compass / radar widget.

### Tier 2 — Lifecycle granularity

| Hook | Chorizite VA | Function | Why it matters |
|------|--------------|----------|----------------|
| Screen-mode change | `0x00479AA0` | `UIFlow::UseNewMode` | Charsel ↔ login ↔ world transitions. Likely the canonical signal we should be gating cached AC pointers on (cf. `project_chat_hook_stale_pointer.md`, `project_login_crash_fix.md`). Finer-grained than our current `SetUIReady`. |
| Client cleanup | `0x004118D0` | `Client::Cleanup` | Pre-teardown event for graceful plugin shutdown ahead of AC destroying UI state. Relevant to logout-crash class. |

Chorizite source: `Chorizite.NativeClientBootstrapper\Hooks\ACClientHooks.cs`.

### Tier 3 — Skip

- **Socket-level packet hooks** (`SendTo`/`RecvFrom` at `0x00630644` / `0x0063063C`). RynthCore's message-level `RawPacketHooks` + `RawPacketParser` is strictly more useful.
- **dat / file I/O hooks** (`CLBlockAllocator::OpenDataFile`). No current need.
- **`SetSelectedObject` / `SetVisible`** — Chorizite themselves left these commented out (too noisy / problematic).
- **Disk decompression hooks** (`DiskController::Decompress`, `AsyncCache_LoadData`) — also disabled in Chorizite.

---

## 2. Where RynthCore is ahead of Chorizite

These are areas where Chorizite has nothing equivalent and we should NOT backport from them:

- **PlayerVitalsHooks** — buffed max vitals via `CACQualities::InqAttribute2nd` (cf. `project_player_vitals_reliable.md`).
- **EnchantmentHooks** — direct reads from `CEnchantmentRegistry`.
- **CombatModeHooks** — `ClientCombatSystem::SetCombatMode` + singleton state.
- **DoMotionHooks** — `CPhysicsObj::DoMotion` for door / object motion tracking.
- **CommandInterpreterHooks**, **ClientActionHooks**, **CombatActionHooks**, **MovementActionHooks** — full player command surface.
- **PropertyUpdateHooks**, **CreateObjectHooks**, **DeleteObjectHooks**, **UpdateObjectInventoryHooks** — message-level object lifecycle.
- **LoginLifecycleHooks** (pattern-scanned `SendLoginCompleteNotification`) and **LogoutLifecycleHooks** (`ExecuteLogOff` + `RecvNotice_Logoff`) — explicit lifecycle signals; Chorizite infers from socket teardown.
- **D3D9 EndScene + WndProc subclass** — already implemented (`EndSceneHook.cs`, `Win32Backend.cs`) with full Avalonia overlay, device-reset, and resize handling.

---

## 3. Notable semantic differences (same function, different exposure)

| Subsystem | Chorizite | RynthCore |
|-----------|-----------|-----------|
| Chat | Hooks at three points: `AddTextToScroll`, `OnChatCommand`, `InitializeCommands` | Single hook at `gmMainChatUI::ListenToElementMessage`; visibility managed separately via `TickHide` |
| Packets | Socket-level `SendTo` / `RecvFrom` only | Socket hook + opcode-level parsing in `RawPacketParser` |
| UI visibility | Per-widget hooks (tooltip, drag, cursor, floaty) | Coarse-grained `SetUIReady` plus hookless data reads |
| Login/logout | Inferred from socket teardown | Explicit hooks at the canonical lifecycle entry points |

---

## 4. Priority for adoption

Best leverage given recent debugging history:

1. **`UIFlow::UseNewMode`** — first to grab. Natural successor to the brittle `HasObservedLoginComplete` gate from the chat-hook stale-pointer fix; could become the central authority for "is it safe to dereference cached AC pointers."
2. **Tooltip + drag-drop** — high-value for plugin UX (RynthAi appraisal, inventory tooling).
3. **Cursor + floaty-lock** — small adoption cost; useful for overlays and RynthRadar.
4. **`Client::Cleanup`** — defensive; would close the remaining tail of logout-class crashes.
