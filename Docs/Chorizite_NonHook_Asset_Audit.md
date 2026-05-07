# Chorizite Non-Hook Asset Audit

*Date: 2026-05-06*
*Source: `C:\Projects\Chorizite-master\`*
*Companion to: [Chorizite_Hook_Gap_Analysis.md](Chorizite_Hook_Gap_Analysis.md)*

The hook gap analysis covers what Chorizite **detours**. This document covers everything else they have that we could utilize — struct layouts, helper libraries, plugin infrastructure, dat readers, render scaffolding, and reverse-engineered metadata.

> **Caveat:** Items below were enumerated by an exploration agent. Before adopting anything, verify the file still exists and the API matches the current Chorizite tip. VAs and struct offsets in `AcClient\` files target the standard `0x00400000` AC build but should be validated against our running client.

---

## 1. High-value assets to port now

Ranked by likely impact on outstanding RynthCore work.

### 1.1 SymbolResolver / crash enrichment
**File:** `Chorizite.Core\Lib\SymbolResolver.cs`

Uses ClrMD + reflection + `acclient.map` parsing to resolve crash addresses to symbols. Registers a native callback to enrich crash reports with loaded plugins, dat versions, CLR info, module base addresses.

**Why we want it:** Our `CrashLogger.cs` writes raw RVAs and a stack sweep; resolving those to AC function names + plugin assembly offsets at crash time would dramatically shorten the diagnostic loop on the next AV. Worth porting the bridge pattern (delegate→native function pointer) and the `acclient.map` line-search logic specifically.

### 1.2 Dat reader
**Files:** `Chorizite.Core\Dats\FSDatReader.cs`, `IDatReaderInterface.cs`

Wraps the external `DatReaderWriter` NuGet package. Lazy-loads SpellTable, SpellComponentTable, SkillTable, VitalTable from Portal/Cell/HighRes/Local databases.

**Why we want it:** RynthCore currently doesn't parse dats. Unlocks:
- Dungeon walls for RynthRadar / dungeon overlay (cf. `project_dungeon_radar_walls.md`, `project_rynth_radar.md`).
- Spell metadata for the buff/debuff macro work (cf. `project_macro_armor_cast_broken.md`).
- Static spawn / landblock data.

### 1.3 Frustum + BoundingBox
**Files:** `Chorizite.Core\Render\Frustum.cs`, `Chorizite.Core\Lib\BoundingBox.cs`

Frustum extracts 6 planes from the view-projection matrix and tests AABB intersection.

**Why we want it:** Direct fit for the building raycast / Nav3D culling work (`project_triangle_mesh_raycast.md`, `project_nav3d_rendering.md`). Port verbatim.

### 1.4 Plugin manifest format
**File:** `Chorizite.Core\Plugins\PluginManifest.cs`

JSON-serialized: Id, Name, Version, Dependencies, Environments (bitfield via `ChoriziteEnvironmentJsonConverter`).

**Why we want it:** Our current plugin discovery (per `feedback_plugin_deploy_paths.md`) is launcher-driven with no dependency ordering or environment gating. Adopting at minimum the `Dependencies[]` array would let us enforce load order; the `Environments` flag idea (e.g., launcher-only vs. in-game) maps to features we'll likely want.

> **Note vs current state:** We already have `src\RynthCore.Engine\Plugins\PluginManager.cs`. Diff before adopting wholesale — Chorizite's design assumes per-plugin AssemblyLoadContext isolation, which conflicts with our NativeAOT model and may not be portable directly.

### 1.5 FileWatcher with debounce
**File:** `Chorizite.Core\Lib\FileWatcher.cs`

`FileSystemWatcher` wrapper with configurable coalescing delay (300 ms default).

**Why we want it:** Useful for engine.json hot-reload and Avalonia panel hot-reload follow-up (cf. `project_avalonia_hotreload_options.md`). Cheap to copy.

---

## 2. Reference-only assets (extend `reference_chorizite.md`)

### 2.1 Backend event-arg types
`Chorizite.Core\Backend\Client\` — useful as **API design references** for our own hook events, even though we don't consume Chorizite's framework:

- `ChatInputEventArgs.cs`, `ChatTextAddedEventArgs.cs`
- `ObjectSelectedEventArgs.cs`
- `ShowTooltipEventArgs.cs`, `ToggleElementVisibleEventArgs.cs`
- `UILockedEventArgs.cs`, `GameObjectDragDropEventArgs.cs`
- `IconData.cs`, `PacketWriter.cs`

### 2.2 AcClient struct layouts (validate before use)
Treat these as starting points; verify against the running client. Many are version-dependent.

| File | Provides |
|------|----------|
| `Inventory.cs` | InventoryPlacement, CObjectInventory, ShortCutData, ShortCutManager |
| `Movement.cs` | MovementManager struct + function pointers |
| `Physics.cs` | CPhysics, CPhysicsObj, GfxVelocityDesc |
| `Allegiance.cs` | AllegianceData, AllegianceRankData |
| `Magic.cs` | ClientMagicSystem, spell/component table accessors |
| `Combat.cs` | ClientCombatSystem, power-bar / attack-state |
| `Net.cs` | NetAuthenticator, NetError |
| `Player.cs` | CPlayerSystem (large — login/logout/character state) |
| `Vendor.cs` | VendorProfile |
| `PString.cs` | `AC1Legacy::PStringBase<T>` ref-counted strings |
| `UIElementId.cs` | 3000+ enum entries for in-client UI IDs |
| `ChatInterface.cs`, `UIFlow.cs`, `UIRegion.cs`, `Input.cs` | UI subsystem entry points (handy for future UI hooks) |

### 2.3 NetworkParser
**File:** `Chorizite.Core\Net\NetworkParser.cs` — wraps a packet reader and dispatches C2S/S2C messages by opcode. Compare against our `RawPacketParser.cs` for API ideas, especially around opcode dispatch tables.

### 2.4 Render abstractions
Reference only — too coupled to Chorizite's chosen renderer to copy directly:
- `Chorizite.Core\Render\` — `BaseRenderer`, `DrawList`, `FontManager`, `BlendState`, `MathHelper`, `TextLayout`, `TextOptions`, `SurfaceInfo`, `IRenderer/IGraphicsDevice/IShader/ITexture/IFramebuffer` interfaces.

---

## 3. Project-level architecture worth borrowing

### 3.1 Plugin system
```
Chorizite.Core/Plugins/
├── IPluginManager + PluginManager       — registry, loader, dispatch
├── PluginManifest.cs                    — JSON metadata (Dependencies, Environments)
├── PluginInstance.cs                    — loaded plugin state
├── AssemblyLoader/
│   ├── AssemblyPluginLoadContext        — per-plugin AssemblyLoadContext isolation
│   ├── AssemblyPluginLoader             — .dll discovery, context creation
│   └── IPluginCore                      — interface plugins implement
└── Models/                              — listing / release / remote-index models
```

Notable design choices:
- **Per-plugin temp directory** for native-DLL copies — avoids file-lock conflicts on reload.
- **Environments bitfield** for conditional loading.
- **HttpClient-driven plugin index** + per-release details fetch (online discovery / update flow).
- **WeakEvent** pattern for plugin event subscriptions (memory-leak prevention on unload).

**Caveat for RynthCore:** AssemblyLoadContext-based isolation is a managed-runtime feature. Our Engine is NativeAOT, so we cannot adopt the AssemblyLoadContext mechanism wholesale — but the manifest schema, dependency graph, environments flags, and update flow are all portable.

### 3.2 Dat reader integration shape
- External NuGet (`DatReaderWriter`) so we don't reinvent the format parsing.
- Single interface (`IDatReaderInterface`) covers all dat sources.
- Lazy table init keeps cold-start cheap.
- Already structured for IoC injection.

If we adopt this, validate the NuGet package supports our targets (NativeAOT, x86, .NET 9) before committing.

---

## 4. Stuff to ignore or flag as suspect

- **`List.cs`** — memory `feedback_idlist_vas.md` confirms its IDList VAs crash our client; use Weenie.cs equivalents. Other VAs in `List.cs` likely also unreliable.
- **`Array.cs`, `Hash.cs`, `SmartBox.cs`** — template-style containers; VAs likely version-dependent. Validate locally.
- **`Chorizite.Launcher`** — Chorizite's launcher UI framework. Not relevant to us.
- **`Render.cs`** (the AcClient one, not Render scaffolding above) — Chorizite-renderer-coupled.
- **`_AcClient.cs`, `_Enums.cs`, `_Template.cs`, `_Todo.cs`** — scaffolding / placeholder files. No value.
- **`Communication.cs`, `Fellow.cs`, `Maint.cs`, `DB.cs`, `Client.cs`** — niche client subsystems; pick from à la carte only when a feature requires it.

---

## 5. Action shortlist

| # | Asset | Action | Why |
|---|-------|--------|-----|
| 1 | `SymbolResolver.cs` | Port the acclient.map lookup + native callback | Crash logs become readable |
| 2 | `Frustum.cs` + `BoundingBox.cs` | Copy verbatim | Unblocks spatial queries (Nav3D, raycast) |
| 3 | `FSDatReader.cs` + `DatReaderWriter` NuGet | Evaluate + adopt | Unlocks dungeon walls, spell metadata |
| 4 | `PluginManifest.cs` schema | Adopt Dependencies + Environments fields | Plugin load ordering |
| 5 | `FileWatcher.cs` | Copy | engine.json hot-reload, future panel reload |
| 6 | `reference_chorizite.md` | Extend with §2 file table | So we don't keep rediscovering the catalog |

---

## 6. Open questions

- Does `DatReaderWriter` support NativeAOT x86? Verify before §1.2 / §3.2.
- Does Chorizite's plugin manifest model survive the loss of AssemblyLoadContext isolation, or do we need a different isolation story?
- Is there a Chorizite tagged release that matches our AC build, or are we tracking `master`? VA drift over time on `master` is the main risk for §2.2.
