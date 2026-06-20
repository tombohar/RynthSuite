# RynthAi Navigation — Deep Dive (2026-06-15)

**Method:** 14-agent investigation (7 dimensions, each adversarially re-verified against the
source, plus a completeness critic) over `NavigationEngine`, `NavRouteParser`, `Jumper`,
`DungeonPathfinder`, `DoorInteractionController`, `CorpseOpenController`, `WorldObjectCache`,
`RynthNavPlugin` + `PortalGraph`, the UI/renderers, and `RynthCore.PluginSdk/RynthCoreHost.cs`.

**Scope note:** the low-level steering/heading control ("the actual movement") is sound and is
out of scope here. Every finding below is *wiring* on top of host primitives that already exist.

All file:line citations were re-confirmed by a second (skeptical) agent. Where the first reader
over-stated something, the **Refinement** notes record the corrected picture.

---

## 0. The diagnosis in one paragraph

Almost everything traces to **two root causes**:

1. **The route parser is fragile and incomplete.** `NavPointType` models only
   `Point/Recall/Pause/Chat/PortalNPC`, and the parse `switch` (`NavRouteParser.cs:94-118`) has
   **no default case** and the loader has no try/catch. A real VTank `.nav` containing a plain
   `Portal=1`, `OpenVendor=5`, or a jump waypoint consumes zero trailer lines → the shared line
   cursor desyncs → every subsequent waypoint mis-parses, or an `int.Parse`/`double.Parse` throws
   and the *entire route fails to load*. This by itself can present as "portals don't work" or
   "the bot wanders off-route."
2. **There is no host "find a usable object near this coordinate" primitive.** `RynthCoreHost`
   exposes only per-id queries, so `FirePortalNpcUse` (`NavigationEngine.cs:905-1054`) is forced
   into a fragile name-substring match against an async classification cache, plus a hardcoded
   `0x70007000–0x700070FF` id probe. Right after a teleport — exactly when the destination portal
   is needed — its name often isn't in the cache yet, so `UseObject` is never called. This single
   missing primitive is the keystone for reliable portals **and** any NPC interaction.

---

## 0b. UPDATE 2026-06-15 — empirical format dump + parser fix SHIPPED

After the analysis above, all **934 real VTank routes** at `C:\Games\VirindiPlugins\VirindiTank\`
were dumped to determine the true on-disk format. This **corrects root cause #1**:

- **`Portal=1` is a non-issue** — it appears in **zero** of the user's routes. The unmodeled types
  actually present are **`OpenVendor=5`** (1×) and **`Npc=7`** (5×).
- **The real critical bug was `PortalNPC` (type 6) itself.** The parser read **11** trailer lines
  (an invented exit-coords + land-coords structure) but the true VTank layout is **6** trailer
  lines: `name, class, isTie, ew, ns, z`. This silently desynced and corrupted **~213 routes /
  561 portal waypoints** — i.e. the genuine "fails to use portals" was an *active* parse bug, not
  just the missing find-near primitive.
- **Confirmed trailer line counts (after the 5-line type/EW/NS/Z/flag prologue):**
  `Recall=1`, `Pause=1`, `Chat=1`, `OpenVendor=2` (`id:int, name`), `Portal(6)=6`, `Npc(7)=6`.
  Tie flag is written `True`/`False` (so the earlier "wrong casing" note was wrong — it round-trips).
- **Fix shipped** in `NavRouteParser.cs` + `AfFileParser.CountNavPoints`: corrected type-6 to 6
  lines; added `OpenVendor=5` and `Npc=7` parsing; added a shared `TrailerLineCount` table (one
  source of truth for Load/Save/Count, so they can't drift again); added a default-case + per-point
  try/catch (unknown/malformed type → log to `LoadWarning` + stop, never desync); surfaced
  `LoadWarning` to chat at the UI load site (`LegacyDashboardRenderer.cs:2012`).
- **Validated** against all 934 files via the real C# parser: 929 clean, 5 graceful warnings
  (genuinely malformed files), **934/934 round-trip stable (0 failures)**, 37,851 waypoints.
- Root cause #2 (no find-usable-object-near host primitive) still stands as the next-highest fix for
  portal *targeting* reliability; the type-6 fix removes the route-corruption half of the problem.

---

## 1. Architecture: two unrelated nav systems

| System | What it is | Portal handling |
|---|---|---|
| **(A) RynthAi route-follower** | `NavigationEngine.cs` (1416) + `NavRouteParser.cs` — the uTank2 `.nav` waypoint follower (the primary system in daily use) | `PortalNPC` waypoint → name-match → `UseObject` |
| **(B) RynthNav** | Separate DotRecast tiled-navmesh **plugin** (`RynthNavPlugin.cs`, 754) + offline `PortalGraph` tool over GoArrow `portals.tsv` | **Never calls `UseObject`** — walks onto the portal coord and waits for a collision-triggered teleport (`StepPortalWait`) |

The two share **no portal model**. RynthNav has a complete coordinate portal graph but no reliable
trigger; RynthAi has the trigger (`UseObject`) but no good targeting data. They cannot leverage
each other today — two half-working portal implementations.

### Route format (`NavRouteParser.cs`)
- Header: `"uTank2 NAV 1.2"`, `RouteType` {`Circular=1, Linear=2, Follow=3, Once=4`}, point count.
- Per point: fixed 5-line prologue (`Type`, `EW`, `NS`, `Z`, + one skipped flag line, lines 86-92)
  then a per-type trailer.
- **`NavPointType` supported:** `Point=0, Recall=2, Pause=3, Chat=4, PortalNPC=6`.
- **Missing vs the canonical uTank2/VTank spec:** `Portal=1` (plain use-portal-object),
  `OpenVendor=5`, and any **jump** waypoint type.
- `AfFileParser.CountNavPoints` (`AfFileParser.cs:771-791`) **duplicates** the same limited table
  with the same per-type offsets and the same no-default flaw — embedded `.af` navs desync too.

### Host API surface (already present, `RynthCoreHost.cs`)
Movement (`DoMovement`, `StopMovement`, `SetAutoRun`, `TurnToHeading`, `SetMotion`, `StopCompletely`,
`JumpNonAutonomous`, `TapJump`, `CommenceJump`/`DoJump`/`LaunchJumpWithMotion`), object interaction
(`UseObject`, `UseObjectOn`, `UseEquippedItem`, `SelectItem`, `SetSelectedObjectId`, `RequestId`,
`MoveItemExternal`), portal detection (`IsPortaling`), object queries (`GetObjectName`,
`GetObjectWcid`, `GetObjectPosition`, `GetObjectHeading`, `TryGetPlayerPose`, `GetVitae`,
`TryGetPlayerVitals`), chat (`WriteToChat` = local display only; `InvokeChatParser` = real parser).
**No** spatial enumeration and **no** outbound vendor/buy/sell/give primitive.

---

## 2. Concern: Portals — "fails to use" ✅ confirmed

| # | Issue | Severity | Evidence |
|---|---|---|---|
| P1 | Parser has no default case → unknown waypoint type desyncs/corrupts the rest of the file (or throws, aborting the whole load) | **critical** | `NavRouteParser.cs:94-118`, enum `:16-23` |
| P2 | Portal use depends entirely on a name string matching an async, often-empty object cache | **high** | `NavigationEngine.cs:905-1054`, `WorldObjectCache.cs:49,592` |
| P3 | RynthNav portal legs never call `UseObject` — walk-in collision only (4u contact vs ~24u-rounded GoArrow coords) | **high** | `RynthNavPlugin.cs:505-548` |
| P4 | `UseObject` fired with no turn-to-face / use-range gate; bool return discarded; `_portalNpcFired` latches on the **call**, not a confirmed teleport → one misclick burns the full 60s `ActionTimeoutMs` then silently skips | **medium** | `NavigationEngine.cs:1051-1053,1230-1236,1129` |
| P5 | `PortalExit`/`PortalLand` coords parsed then **never read** (dead disambiguation + arrival data) | **medium** | `NavRouteParser.cs:39-44,109-115`; no refs in `NavigationEngine.cs` |
| P6 | Tier-3 rescue hardcodes a single `0x70007000–0x700070FF` id probe (256 `TryGetObjectName` calls/retry); only covers town-network portals | **medium** | `NavigationEngine.cs:1009-1027` |
| P7 | Teleport confirmed by a generic >50yd jump; misses short-hop portals (RynthNav's landblock-change is more robust) | low | `NavigationEngine.cs:1184-1188` vs `RynthNavPlugin.cs:509-511` |

**Why P2 is the core mechanism:** portals are static world objects parked in the cache as
`AcObjectClass.Unknown`; names fill in on a 30/tick budget and frequently arrive empty. The only
path to `UseObject` is a name match (`Equals`/`IndexOf` on `pt.TargetName`), so a typo, an AC name
that doesn't contain the configured substring, two same-named portals, or a just-arrived portal not
yet in the cache all silently break it.

**Refinement (recall confirm):** the first reader claimed recall confirmation "never fires." Not
quite — far recalls (Lifestone/Town, >50yd) ARE caught by the position-jump test, and `IsPortaling`
pulsing also confirms. The genuine gap is narrow: a recall that lands **within 50yd** AND never
pulses `IsPortaling` is undetected and re-casts every 4s until the 60s timeout. Don't over-rewrite
the recall path — the bigger win is portal *targeting*.

**Verified amplifiers (extra issues):**
- `_portalNpcFired` latches `true` the moment `UseObject` is *issued* (`NavigationEngine.cs:1233`),
  and `FirePortalNpcUse` returns `true` on the call alone (`:1051-1053`). If the click does nothing
  (wrong object, cooldown, out of range), there's no retry — just a 60s wait then skip.
- Once-route `Advance` does `route.Points.Clear()` in memory (`:686-687`, `:807`), destroying the
  shared route object — re-arming requires a reload from disk.

---

## 3. Concern: NPCs — "need to be introduced" ✅ confirmed: no general NPC concept

NPC/object interaction lives in three disjoint hard-coded silos — **doors**
(`DoorInteractionController`, a 6-state open/lockpick FSM), **corpses** (`CorpseOpenController`, a
large approach→open→classify→pickup FSM), and a single **PortalNPC** waypoint. There is no
talk-to-NPC, vendor buy/sell, give-item, or confirmation-dialog concept.

| # | Issue | Severity | Evidence |
|---|---|---|---|
| N1 | Host exposes **zero** outbound vendor/buy/sell/give/trade primitives — NPC commerce is unimplementable at the nav layer | **high** | `RynthCoreHost.cs` (no Vendor/Buy/Sell/Give); `OnVendorOpen/Close` inbound-only `CorpseOpenController.cs:375-387` |
| N2 | `WorldObjectCache` never produces `AcObjectClass.Npc(37)` or `Vendor(25)` → nav layer cannot locate an NPC by class | **high** | `WorldObjectCache.cs:1441-1471,592`; working classifier siloed in `ExpressionEngine.cs:1663-1688` |
| N3 | `/mt use closestnpc` and `closestvendor` are **dead commands** — read the raw cache class that's never set, always "not found" | **high** | `MagToolsCommands.cs:378-398` |
| N4 | `PortalNPC` is name-only, ignores its own parsed `ObjectClass`, success = teleport-only → a non-porting NPC burns 60s | **medium** | `NavigationEngine.cs:905-1054,1171-1238`; `NavPoint.ObjectClass` `NavRouteParser.cs:36` parsed-unused |
| N5 | No server confirmation-dialog accept/decline handling anywhere | **medium** | only passive `Meta/PropertyNames.cs:183-184` predicate names exist |
| N6 | Door discovery assumes static GUIDs (`<0x80000000`) — would exclude dynamic-range NPCs/vendors if copied forward | low | `DoorInteractionController.cs:228` |

**Refinement (N4):** `PortalNPC` is **not** "fires blind expecting click-range" — it deliberately
relies on AC's **native `UseObject` auto-walk** (code comment `NavigationEngine.cs:1219-1220`). The
real gap is that it has no plugin-side *class-filtered, approach-then-interact* abstraction and
ignores `ObjectClass`, so it can't disambiguate or model non-teleporting interactions.

**The template already exists:** the door and corpse controllers are two copies of the same
approach→interact→verify FSM (`DoorInteractionController.cs:49` vs `CorpseOpenController.cs:1637`
`ApproachCorpse`). Corpse ownership even shows the `RequestId → parse LongDesc` capability-detection
pattern (`CorpseOpenController.cs:645,815`) that an NPC concept would reuse.

**Already-RE'd opcodes to wire (from prior memory notes):** give `0xA2`, trade senders `0x1F6–0x204`.

---

## 4. Concern: Jumps — "do they work in navigation?" → **No** ✅

| # | Issue | Severity | Evidence |
|---|---|---|---|
| J1 | No `NavPointType.Jump`; engine waypoint switch has no jump case → routes cannot request a jump | **high** | enum `NavRouteParser.cs:16-23`; dispatch `NavigationEngine.cs:310-322` |
| J2 | A real VTank jump waypoint would also desync the file (ties to P1) | **high** | `NavRouteParser.cs:94-118` |
| J3 | `DungeonPathfinder` prunes **all** drop/jump edges → any drop-gated dungeon area is permanently unreachable (silent `null`) | **medium** | `IsDropEdge` `DungeonPathfinder.cs:61-73`; skipped `:262,428,512`; comment `:569-574` |
| J4 | Stuck-recovery jump is a fixed `0.5`, non-directional, `StopMovement`-first hop — can't clear real ledges; the full `Jumper` is never reused | **medium** | `NavigationEngine.cs:1320-1327` vs `Jumper.cs:184-201` |
| J5 | `Jumper` phase transitions are purely timer-based; a failed jump is indistinguishable from success (no landing/motion verify, no retry) | low | `Jumper.cs:116-144,184-201` |

`Jumper.cs` is a **complete** charged + directional jump machine (`CommenceJump`→`DoJump`,
`LaunchJumpWithMotion`), but it is invoked **only** by the manual `/ra jump` command
(`RynthAiCommands.cs:1002`); `NavigationEngine` never references it.

**Gotcha for implementation:** the global ">50yd position jump = teleport" detector
(`NavigationEngine.cs:271-294`) would misfire on a legitimate long jump/fall and force a settle.
A `NavPointType.Jump` implementation must exempt the active jump window from that detector.

---

## 5. Concern: Chat commands — "do they work?" → **partially** ✅

**Waypoint sense:**
- Slash commands **work** — routed through `InvokeChatParser` (`NavigationEngine.cs:867-868`).
- **Non-slash commands silently no-op** — they go to `WriteToChat`, which only *displays text
  locally* and never reaches the server (`RynthCoreHost.cs:488-502` vs `1100-1114`; corroborated by
  the engine's own comment at `NavigationEngine.cs:878-879`). The dispatcher could turn bare text
  into a `/say`, so the fix is to send all non-empty payloads through `InvokeChatParser`.
- **Leading whitespace defeats the slash check** (`' /say hi'` → diverted to local display).
- No pacing/retry; the `InvokeChatParser` return is discarded; `HandleChat` reuses `_portalState`
  as a fired-latch (safe only because it `Advance()`s instantly — adding pacing would break it).

**Command sense:**
- There is **no `/ra` verb to load / start / stop / goto a route.** The `/ra` switch
  (`RynthAiPlugin.cs:1492-1613`) has `navdebug`/`addnavpt`/`dunnav`/`dunnav-patrol`/`hazard` only;
  route load + `EnableNavigation` are reachable **only** via the UI (`LegacyNavigationUi.cs:55,66`)
  or meta (`MetaManager.cs:757,772`). `dunnav` is the separate DotRecast dungeon subsystem.

| # | Issue | Severity |
|---|---|---|
| C1 | Non-slash Chat waypoints silently no-op (local display only) | **high** |
| C2 | Parser desync on unknown type also corrupts downstream Chat points (= P1) | **high** |
| C3 | No chat command to load/start/stop/goto a route | medium |
| C4 | Chat waypoint has no pacing and ignores parser failure | medium |
| C5 | `HandleChat` overloads `_portalState` as its fired-flag | low |
| C6 | Chat payload never trimmed → leading space defeats the slash gate | low |

---

## 6. Concern: UI improvements

| # | Issue | Severity | Evidence |
|---|---|---|---|
| U1 | "Add Portal" button is a stub — only logs to the file (no chat, no NavPoint) → portals enter routes only via imported files | **high** | `LegacyNavigationUi.cs:119-122` |
| U2 | Pause/Chat (and any future Jump) waypoints are invisible in all 3 render passes AND unauthorable in the editor | **high** | `NavMarkerRenderer.cs:202-205,287-290,330-333`; editor `LegacyNavigationUi.cs:97-167` |
| U3 | Map never overlays the nav route; waypoint list is delete/select-only (no edit/reorder/insert) | medium | `DungeonMapUi.cs` (no route refs); list `LegacyNavigationUi.cs:196-237` |

**Refinement (U3 / status):** the status line is **not** as bare as first reported — it already
shows `idx/count`, distance, signed heading error, a `[TURN]` mode flag, and stuck state
(amber via `NavIsStuck`, `NavigationEngine.cs:1376-1384` → `LegacyNavigationUi.cs:41-44`). The real
gaps are: a **human-readable stuck-reason** string, a **route overlay on the map**, and a
**per-point editor**.

**Improvement candidates:** real "Add Portal/NPC" via a nearest-object query (reuse the
`DungeonMapUi` portal-flag detection); per-type **marker glyphs** + an "Add Special" menu;
route overlay + clickable targets on the dungeon map; a **route recorder** (sample coords while
walking, auto-insert a portal waypoint when `IsPortaling` fires).

---

## 7. Bigger-picture gaps that matter most for a daily multi-boxer

(From the completeness critic; the top items were independently re-verified.)

1. **Fellowship / leader-follow nav doesn't exist.** `NavRouteType.Follow` just loops the index
   back to 0 (`NavigationEngine.cs:691,812`); `FellowshipTracker` reads leader/member ids but only
   as read-only meta predicates. **Highest ROI:** designate a main and have boxes follow it live
   instead of every box running an identical, desync-prone pre-authored route. All pieces present
   (`GetObjectPosition`, the tracker, existing steering + portal state machine).
2. **Teleport-to-route rejoin is missing.** `FindNearestWaypoint` is *defined* in the engine
   (`NavigationEngine.cs:382`) but the engine **never calls it** — only the dashboard and
   `MetaManager` do (each with a duplicate copy), and only as a start index. After a recall/portal
   the box resumes from a stale index and runs straight through walls. A guarded post-teleport snap
   fixes it.
3. **Death/recovery nav is completely absent.** No `OnDeath`, no detect-own-death, no nav response;
   a dead box resurrects at a lifestone and sits there. Biggest unattended-multibox risk. Primitives
   exist (`GetVitae`, `TryGetPlayerVitals`, `CastSpell` lifestone 1635, `FindNearestWaypoint`).
4. **Stuck escalation:** `_stuckCount` is incremented (`NavigationEngine.cs:1308`) but **never
   consulted** — it hops in place forever with no reroute/skip/give-up/alert.
5. **Multi-leg portal journeys** — each portal/recall waypoint is handled in isolation with no
   notion of "which leg" or landblock verification of arrival.
6. **Route recording** — no waypoint capture from live movement anywhere.
7. **Indoor/EnvCell Z-gating** — the follower steers on flat NS/EW (`NavigationEngine.cs:330-355`)
   with no cell/Z awareness, so in stacked dungeon cells it can aim "through the floor" and the 2D
   arrival check can falsely fire. `DungeonPathfinder` has real cell adjacency but isn't wired into
   the follower.

---

## 8. Recommended build order

**Foundation (unblocks almost everything):**
1. **Parser hardening** — replace the fixed-offset per-type switch with one shared
   `Dictionary<int, FieldSpec>` driving read/save/count, with a **default case** (consume N lines
   for unknown types) + per-point try/catch + forward re-sync. Add `Portal=1`, `OpenVendor=5`,
   `Jump`. *(S–M)* — fixes P1/J2/C2; prereq for portals/jumps/vendor waypoints.
2. **Host `FindUsableObjectNear(ns, ew, z, radius, classFilter)`** — scan AC's live object table by
   position, return nearest usable/portal/NPC, bypassing the plugin-side cache. *(L)* — the
   keystone; kills P2/P6 and unblocks N-series + coord-based `Portal=1`.

**Quick wins (high value, low effort, independent):**
3. Route non-slash Chat through `InvokeChatParser` + trim. *(S)* — C1/C6
4. `/ra nav load/start/stop/goto` command surface (set `CurrentRoute`/`EnableNavigation`/
   `ActiveNavIndex`). *(M)* — C3; broadcastable to N boxes
5. Post-teleport rejoin: call the existing `FindNearestWaypoint` on teleport-settle exit, guarded by
   a distance threshold. *(S)* — gap #2
6. Stuck escalation tiers off `_stuckCount` (sidestep → directional jump → skip waypoint →
   give-up + chat alert). *(S–M)* — gap #4
7. UI: real "Add Portal/NPC" via nearest-object query + per-type marker glyphs. *(S–M)* — U1/U2

**Features:**
8. `NavPointType.Jump` → wire the existing `Jumper`; exempt the jump window from the >50yd teleport
   detector; allow `DungeonPathfinder` drop edges to emit a jump waypoint. *(M–L)* — J1/J3/J4
9. **Fellowship/leader-follow nav mode** — live leader coord → existing steering + portal FSM.
   *(M)* — gap #1, biggest ROI
10. NPC `InteractableController` (extracted from door/corpse) + cache Npc/Vendor classification +
    host vendor/give primitives (wire the RE'd `0xA2` / `0x1F6–0x204`). *(L)* — N1–N5
11. Death/recovery nav. *(M)* — gap #3
12. Unify the RynthAi + RynthNav portal model (one model: entrance coord + optional name/id + exit
    coord; one trigger: find-near + `UseObject` with walk-in fallback). *(L)*

---

## 9. Open questions (confirm before implementing)

- **Exact uTank2 NAV 1.2 byte layout** for `Portal=1`, `OpenVendor=5`, and jump waypoints (field
  order, tie-flag token form). No sample `.nav` files exist on disk in `RynthSuite` or
  `C:\Games\RynthCore` — confirm against a real VTank export or the uTank2 source first.
- **Does AC's `UseItem` GameAction auto-walk + turn-to-face**, or require the player already in use
  range/facing? Determines whether the 250yd fallback gate is actionable and whether a separate
  approach step is mandatory.
- **Which static id ranges do non-town-network portals use?** The Tier-3 probe only covers
  `0x70007000–0x700070FF`. (Mooted if `FindUsableObjectNear` lands.)
- **Is there a reliable engine hook on portal entry/exit** beyond `IsPortaling` polling that both
  systems could subscribe to for deterministic teleport confirmation?
- **GoArrow coord accuracy** after 0.1° rounding — if routinely >4u off, RynthNav walk-in can't work
  regardless of other fixes (another reason to prefer `UseObject`).
- **`LaunchJumpWithMotion` live behaviour** on the current acclient (Version≥52) — jump hooks are
  built-not-live-verified per prior notes; autonomous nav-jumps depend on it firing correctly.
