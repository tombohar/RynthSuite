# Mesh Navigation Deep Dive — RynthNav

**Date:** 2026-06-04
**Goal:** Type a coordinate → the character auto-navigates there, avoiding static objects and taking necessary portals.
**Status:** Design locked, not yet built. This doc is the master reference for a multi-session build.

---

## Locked decisions (2026-06-04)

1. **Representation: true tiled navmesh via DotRecast.** Recast (offline bake) + Detour (in-process query). Chosen for "best possible end product" — handles AC's stacked geometry (dungeons under hills, bridges, multi-level) natively, which a 2.5D grid fights. The heavy cost lands entirely in the *offline* baker, so runtime stays light.
2. **New standalone plugin** `RynthCore.Plugin.RynthNav`, separate from RynthAi. Incorporate into RynthAi later. Maximum isolation: nav work physically cannot destabilize combat/buffing.
3. **v1 scope = outdoor goto-coord** within a baked region, avoiding static obstacles. No portals yet.

---

## The reframe: three problems at three scales

| Tier | Question | Status today |
|---|---|---|
| **1. Local steering** | "Walk toward this point, face it, don't get stuck" | ✅ Solid — `NavigationEngine.cs`, 30 Hz servo |
| **2. Local/regional pathfinding** | "Find a walkable path *around* obstacles A→B" | ⚠️ Indoor only (`DungeonPathfinder`); outdoor = the gap |
| **3. Global routing** | "Which portals/recalls chain me across the map?" | ❌ Manual only (execution works, planning doesn't) |

"Go to a coord avoiding objects and taking portals" = build tier 2 (the navmesh) and tier 3 (the portal graph), composed on top of the tier-1 steering we already trust.

---

## Foundation inventory (what already exists)

### Actuators / readers — present and working
| Capability | Where |
|---|---|
| Move/turn/jump primitives | engine hooks: `SetAutoRun`, `SetPlayerHeadingDirect` (instant quaternion snap), `TurnToHeading` (gradual), `DoMovement`/`StopMovement` (Tier-1 motion), `JumpNonAutonomous` |
| Player pose + heading | `RynthCore.Engine\Compatibility\PlayerPhysicsHooks.cs` `TryGetPlayerPose` (SmartBox; objCellId,x,y,z,quat) |
| Waypoint steering loop | `RynthSuite\Plugins\RynthCore.Plugin.RynthAi\NavigationEngine.cs` (servo, stuck-watchdog, closest-approach, lookahead blend) |
| Coordinate math | `...RynthAi\LegacyUi\NavCoordinateHelper.cs` and `PlayerPhysicsHooks.cs:438` |
| Indoor A* (cell→portal graph) | `...RynthAi\DungeonPathfinder.cs` |
| Terrain heightmap reader | `RynthSuite\Shared\RynthCore.TerrainData\` (`DatDatabase`, `LandblockData`, `TerrainSampler`) — **shared lib, plugin-consumable** |
| Static collision (buildings/scenery/trees) | `AcClientReborn\landblock\Parsers\` (`GeometryLoader`, `LandblockStructs` Setup/GfxObj, collision spheres/cylinders/meshes) |
| Dungeon walls + connectivity | `AcClientReborn\landblock\Parsers\DungeonLOS.cs` |
| Portal detect + use + teleport-confirm | `WorldObjectCache` (`ObjectClass.Portal`); `NavigationEngine` portal/recall state machine; `IsPortaling()` (`TeleportStateHooks`) |
| Portal source→destination data | ACE-World-Database `landblock_instance` + `weenie_properties_position` (PositionType.Destination=2; LinkedPortalOne=8/Two=16; LinkedLifestone=15) |

### Missing (what we build)
- **Navmesh baker** (offline): geometry → Detour tiles.
- **Outdoor pathfinder** (in-process): Detour findPath + string-pull → waypoints.
- **Portal-graph planner** (later): zone-level A* sequencing walk → portal → walk.

---

## Key coordinate facts (verified)

- **Landblock** = 192 world units square; grid is 0x100 × 0x100. `originX = ((lbKey>>8)&0xFF)*192`, `originY = (lbKey&0xFF)*192`.
- **Terrain cell** = 24 units; landblock terrain = 9×9 vertex grid / 8×8 cells. Per-cell diagonal split is a PRNG (SW→NE vs SE→NW) — `LandblockData`/`TerrainSampler` already implement it; **must** be respected for correct Z.
- **objCellId** = `0xLLXXYYCC` (LL landblock hi, XX/YY grid, CC cell; ≥0x0100 = EnvCell/indoor).
- **/loc NS/EW** from pose: `EW=(lbX*8 + x/24 - 1019.5)/10`, `NS=(lbY*8 + y/24 - 1019.5)/10` (`PlayerPhysicsHooks.cs:438`, `NavCoordinateHelper.cs:22`).
- **Nav-frame ⇄ world**: nav engine distance uses `*240`; `worldZ = navZ*240`. Movement is player-relative: `(pt−player)*240 + playerWorldPos` — never absolute coord→world. (See memory: `rynthcore_nav_coord_frame_pitfall`.)
- **Heading**: physics yaw `2*atan2(qz,qw)` (0=N, CCW); Decal/VTank heading = `(-physYaw+720)%360` (0=N, CW). Physics heading is 90° off Decal basis (`NormalizeDecalHeading = 90 - h`). Direct snap writes yaw-only quat: qw@pos+0x48+0x08, qz@+0x14.

---

## Architecture — three components

```
[portal.dat / cell.dat] ─► ① RynthNav.Baker (CLI, Recast)  ─┐
[ACE world DB]          ─► ② RynthNav.PortalExtractor (CLI) ─┤─► on-disk nav data (tiles + portal graph)
                                                              │
RynthCore.Plugin.RynthNav (in-process, Detour) ◄─────────────┘
   loads tiles ► hierarchical A* ► funnel/straight-path ► waypoints
   ► existing steering primitives + portal/recall execution
   ► command: goto 23.4N, 56.7E
```

### ① RynthNav.Baker (new CLI, offline)
- **References:** `RynthCore.TerrainData` + `AcClientReborn.Landblock` + DotRecast.Recast/Core.
- **Per landblock tile (192m → one Detour tile):**
  1. Terrain → triangle soup: triangulate the 9×9 grid using the AC diagonal-split PRNG (`TerrainSampler` already knows it). World-space.
  2. Static obstacles → triangle soup: `GeometryLoader` stabs/buildings (Setup→GfxObj meshes); for collision-volume-only objects (sphere/cylinder, no mesh) emit a proxy box/cylinder so Recast carves them out.
  3. Dungeon walls (later phase): `DungeonLOS` polys.
  4. **Axis remap AC→Recast:** AC is Z-up (X=EW, Y=NS, Z=height); Recast is Y-up. Map `(x,y,z)_AC → (EW, height, NS)_Recast`. Get this exactly right once, centrally.
  5. `RcConfig` (cellSize ~0.3–0.5m, agentRadius/height/maxClimb/maxSlope tuned to AC) → `RcBuilder` → polymesh + detail → `DtNavMeshBuilder.CreateNavMeshData` → serialize tile keyed by (lbX, lbY).
- **Output:** per-landblock `.tile` files in a nav-data dir. Bake once; cache.
- **Validation:** AcClientReborn already renders terrain + Nav3D markers → draw baked navmesh polys over the terrain for visual confirmation. This is the test harness.

### ② RynthNav.PortalExtractor (new CLI, offline — Phase 4)
- Reads ACE DB `landblock_instance` (portal physical placement) + `weenie_properties_position` Destination + recall spell defs.
- Output: portal/teleport edge list (JSON): `{ srcWorldPos, dstWorldPos, kind: portal|recall, weenieId, name }`.

### ③ RynthCore.Plugin.RynthNav (new plugin, in-process — kept lightweight)
- **References:** DotRecast.Detour. Loads tiles for current + neighbor landblocks lazily (Detour add/removeTile as player streams).
- **goto pipeline:** player world pos → `DtNavMeshQuery.FindNearestPoly` → `FindPath` → `FindStraightPath` (string-pull) → world points → NS/EW waypoints → steering.
- **Steering:** port a minimal proven servo (autorun + heading servo via `SetPlayerHeadingDirect`/`TurnToHeading` + arrival + stuck/jump) consuming Detour straight-path points. Keeps the plugin self-contained.
- **No AC mutation beyond the existing safe primitives** — that's the entire stability story. New in-process code is pure-CPU (tile load + Detour query + funnel).
- **Command:** `goto <coord>` (and later landblock/dungeon-cell variants).

---

## Phase plan (each phase independently testable + shippable)

- **Phase 0 — Pipeline proof. ✅ DONE 2026-06-04.** `RynthNav.Baker` (`RynthSuite\Tools\RynthNav.Baker`) bakes terrain (SwToNeCut-exact) + static obstacles (GeometryLoader buildings/scenery) → one Detour tile. Holtburg 0xA9B4: 265 polys, paths route around buildings (6–7 waypoint detours), `.tile` serialize/reload round-trips. Outputs `.obj`+`.tile` to `C:\Games\RynthCore\NavData`. DotRecast 2026.1.3. (Visual overlay in AcClientReborn still optional — `.obj` opens in any 3D viewer for now.)
- **Phase 1 — Outdoor goto-coord (v1).** `RynthCore.Plugin.RynthNav` skeleton; load tile(s); `goto <coord>` → Detour path → string-pull → minimal steering. Bake a small test region so cross-tile paths work. *Test:* stand in a town, `goto` a coord across a building; bot routes around it. **This is the chosen v1.**
- **Phase 2 — Region bake + tile streaming.** Batch-bake many landblocks; lazy load/unload tiles as player moves; graceful fallback on un-baked tiles. *Test:* long cross-landblock outdoor run.
- **Phase 3 — Dungeons / multi-level.** Bake `DungeonLOS` geometry into Detour tiles (layered); goto works indoors. *Test:* goto a dungeon cell coord.
- **Phase 4 — Portals / hierarchical planner.** `RynthNav.PortalExtractor`; portals/recalls as Detour off-mesh connections OR a zone-graph A* sequencing walk→portal→walk; reuse RynthAi's portal/recall execution. *Test:* goto a coord that requires a portal.
- **Phase 5 — Dynamic avoidance + RynthAi integration.** Avoid mobs/players (Detour crowd or simple steering); fold the nav runtime into RynthAi as its navigation provider ("incorporate later").

---

## Open questions / risks

- **DotRecast config tuning** (cellSize, agentRadius/height, maxSlope, maxClimb) for AC's scale and the player's jump/step ability — needs empirical iteration in Phase 0/1.
- **Collision-volume → mesh proxies:** many AC objects only have sphere/cylinder/bounding collision, not full physics meshes. Proxy quality affects how tight the bot hugs obstacles.
- **Tile-border stitching:** Detour handles tiled connectivity if tiles share edges at the same coords — axis remap + tile origin must be exact.
- **Off-mesh connections vs zone graph** for portals: decide in Phase 4.
- **Where the CLIs live / names:** proposed `RynthSuite\Tools\RynthNav.Baker` + `...\RynthNav.PortalExtractor`; plugin in `RynthSuite\Plugins\RynthCore.Plugin.RynthNav`. Open to rename.
- **DotRecast NuGet** must restore on .NET 10 (DotRecast.Core/Recast/Detour). Confirm in Phase 0.

---

## Constraints carried from hard-won lessons (memory)
- Out-of-process tooling preferred — baker/extractor are CLIs; runtime only loads cheap data. (`feedback_rynthcore_isolation`)
- New in-process code can destabilize even when gated off — separate plugin keeps RynthNav off RynthAi's critical path entirely. (`rynthcore_engine_addition_destabilizes_buffs`)
- Deploy is part of done; each phase ends with a deployed, RL-tested build. (`feedback_rynthcore_deploy_is_part_of_done`)
- Keep changes scoped — one testable phase at a time. (`feedback_rynthcore_scope_discipline`)
- Movement math stays player-relative `(pt−player)*240 + playerWorldPos`. (`rynthcore_nav_coord_frame_pitfall`)
- **UI is Avalonia, never ImGui.** The ImGui shell is defunct (`engine.json EnableImGuiShell=false`); all in-AC UI is engine-side Avalonia panels (the OverlayHost already has a `Nav` panel) bridged to the plugin via C exports. RynthNav v0.1 uses `/rnav` chat commands + log only — no UI, no `ImGui.NET` ref, no `RynthPluginRender` export. When we draw the route in-world, use the engine's **Nav3D overlay API** (D3D9 `Host.Nav3DAddLine`/`AddRing`), not ImGui.
